using System;
using System.Collections.Generic;
using System.IO;

namespace PvfLib
{
    public sealed record VariousBuffEffectDefinition(string Name, IReadOnlyList<int> Parameters)
    {
        // Parameters exposes only a conventional single block. Multi-block
        // effects retain their ordered resource blocks without concatenation.
        public IReadOnlyList<IReadOnlyList<int>> ParameterBlocks { get; init; }
            = Array.AsReadOnly(Array.Empty<IReadOnlyList<int>>());
    }
    public sealed record VariousBuffResourceDefinition(int Index, string SystemGroup,
        IReadOnlyList<VariousBuffEffectDefinition> Effects);

    // Read-only script definitions. Runtime eligibility and expiry belong to
    // the owning service; effect values remain in the shared client resource.
    public sealed class VariousBuffConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<VariousBuffResourceDefinition> Definitions { get; private set; }
            = Array.AsReadOnly(Array.Empty<VariousBuffResourceDefinition>());

        private VariousBuffConfigFile() { }

        public static VariousBuffConfigFile Parse(string content)
        {
            var file = new VariousBuffConfigFile { Content = content ?? string.Empty };
            try
            {
                // Tooltip strings can span lines and contain text such as
                // [effect]. Preserve source offsets while hiding quoted text
                // from the shared line-based tag parser and boundary checker.
                var root = ReadRoot(MaskQuotedText(content));
                var rows = new List<VariousBuffResourceDefinition>();
                var indices = new HashSet<int>();
                foreach (var node in root.Children)
                {
                    if (!ActivityConfigScriptReader.TagIs(node, "index")) continue;
                    if (!node.HasEndTag || node.DataItems.Count != 1)
                        throw new InvalidOperationException("Buff [index] at line " + (node.StartLineIndex + 1)
                            + " must be a closed block with one index value.");
                    if (!int.TryParse(node.DataItems[0].GetContent(content).Trim(), out var index)
                        || index <= 0 || !indices.Add(index))
                        throw new InvalidOperationException("Buff index at line " + (node.StartLineIndex + 1) + " must be positive and unique.");
                    var groups = node.GetChildren("system group");
                    if (groups.Count > 1)
                        throw new InvalidOperationException("Buff index " + index + " [system group] is duplicated.");
                    var group = groups.Count == 0 ? string.Empty : ReadString(groups[0], content);
                    var effects = new List<VariousBuffEffectDefinition>();
                    foreach (var effect in node.GetChildren("effect"))
                    {
                        try { effects.Add(ReadEffect(effect, content)); }
                        catch (InvalidOperationException exception)
                        {
                            throw new InvalidOperationException("Buff index " + index + " effect at line "
                                + (effect.StartLineIndex + 1) + ": " + exception.Message, exception);
                        }
                    }
                    rows.Add(new VariousBuffResourceDefinition(index, group, effects.AsReadOnly()));
                }
                if (rows.Count == 0) throw new InvalidOperationException("Buff list contains no index definitions.");
                file.Root = root;
                file.Definitions = rows.AsReadOnly();
                file.IsValid = true;
            }
            catch (Exception exception) { file.InvalidReason = exception.Message; }
            return file;
        }

        private static VariousBuffEffectDefinition ReadEffect(ScriptNode effect, string content)
        {
            if (!effect.HasEndTag || effect.DataItems.Count != 0)
                throw new InvalidOperationException("[effect] must be a closed block.");
            var names = effect.GetChildren("name");
            var parameters = effect.GetChildren("param");
            if (names.Count != 1)
                throw new InvalidOperationException("Effect requires exactly one [name].");
            var name = ReadString(names[0], content);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Effect [name] cannot be empty.");

            bool certificate = string.Equals(name, "guild_certificate", StringComparison.Ordinal);
            // Current index 1151, lines 17594..17606, has two independent
            // scalar [param] blocks (10154439, then 1). Preserve those blocks;
            // this resource model does not grant or interpret their items.
            if (certificate ? parameters.Count != 2 : parameters.Count > 1)
                throw new InvalidOperationException(certificate
                    ? "guild_certificate requires exactly two scalar [param] blocks."
                    : "Effect has ambiguous repeated [param] blocks.");
            var blocks = new List<IReadOnlyList<int>>();
            foreach (var parameter in parameters)
            {
                if (!parameter.HasEndTag)
                    throw new InvalidOperationException("Effect [param] must be closed.");
                var values = ActivityConfigScriptReader.Integers(parameter, content);
                if (certificate && (values.Count != 1 || values[0] <= 0))
                    throw new InvalidOperationException("guild_certificate [param] blocks require one positive integer each.");
                blocks.Add(values.AsReadOnly());
            }
            return new VariousBuffEffectDefinition(name, blocks.Count == 1 ? blocks[0] : Array.Empty<int>())
                { ParameterBlocks = blocks.AsReadOnly() };
        }

        private static string ReadString(ScriptNode node, string content)
        {
            if (node.Children.Count != 0 || node.DataItems.Count != 1)
                throw new InvalidOperationException("Buff [" + node.Tag + "] requires one backtick string.");
            var text = node.DataItems[0].GetContent(content).Trim();
            if (text.Length < 2 || text[0] != '`' || text[^1] != '`'
                || text.AsSpan(1, text.Length - 2).IndexOf('`') >= 0)
                throw new InvalidOperationException("Buff [" + node.Tag + "] contains an invalid backtick string.");
            return text.Substring(1, text.Length - 2);
        }

        private static string MaskQuotedText(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Buff list is empty.");
            var masked = content.ToCharArray();
            bool quoted = false;
            for (int index = 0; index < content.Length; index++)
            {
                if (!quoted && content[index] == '#')
                {
                    while (index < content.Length && content[index] != '\n') index++;
                    if (index == content.Length) break;
                }
                if (content[index] == '`')
                {
                    masked[index] = '_';
                    if (quoted && index + 1 < content.Length && content[index + 1] == '`')
                    {
                        masked[++index] = '_';
                        continue;
                    }
                    quoted = !quoted;
                }
                else if (quoted && content[index] != '\n' && content[index] != '\r')
                    masked[index] = '_';
            }
            if (quoted) throw new InvalidOperationException("Buff resource contains an unclosed backtick string.");
            return new string(masked);
        }

        private static ScriptNode ReadRoot(string masked)
        {
            var root = new ScriptParser().Parse(masked);
            if (root.DataItems.Count != 0)
                throw new InvalidOperationException("Buff list contains root data.");
            var boundaries = new Dictionary<int, string>();
            var rootIndexEnds = new HashSet<int>();
            foreach (var node in root.Children)
            {
                foreach (var child in node.Children) RejectNestedIndex(child);
                AddBoundaries(node, boundaries);
                if (ActivityConfigScriptReader.TagIs(node, "index") && node.HasEndTag)
                    rootIndexEnds.Add(node.EndLineIndex);
            }
            // Current etc/variousbufflist.etc has one redundant [/index] at
            // line 4886, immediately after index 131's legal closing tag.
            // Accept that second closing token only; keep all source offsets
            // and reject an orphan, a third close or any intervening data/tag.
            using var reader = new StringReader(masked);
            string line;
            int lineIndex = 0;
            bool previousLegalIndexEnd = false;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.Length == 0) { lineIndex++; continue; }
                if (text[0] == '#') { previousLegalIndexEnd = false; lineIndex++; continue; }
                if (text[0] == '[' || text[^1] == ']')
                {
                    if (boundaries.TryGetValue(lineIndex, out var expected)
                        && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase))
                        previousLegalIndexEnd = rootIndexEnds.Contains(lineIndex);
                    else if (previousLegalIndexEnd && string.Equals(text, "[/index]", StringComparison.OrdinalIgnoreCase))
                        previousLegalIndexEnd = false;
                    else
                        throw new InvalidOperationException("Buff list has an unmatched or malformed tag at line " + (lineIndex + 1) + ".");
                }
                else previousLegalIndexEnd = false;
                lineIndex++;
            }
            return root;
        }

        private static void AddBoundaries(ScriptNode node, IDictionary<int, string> boundaries)
        {
            boundaries.Add(node.StartLineIndex, "[" + node.Tag + "]");
            if (node.HasEndTag) boundaries.Add(node.EndLineIndex, "[/" + node.Tag + "]");
            foreach (var child in node.Children) AddBoundaries(child, boundaries);
        }

        private static void RejectNestedIndex(ScriptNode node)
        {
            if (ActivityConfigScriptReader.TagIs(node, "index"))
                throw new InvalidOperationException("Buff [index] must be at the resource root.");
            foreach (var child in node.Children) RejectNestedIndex(child);
        }
    }
}
