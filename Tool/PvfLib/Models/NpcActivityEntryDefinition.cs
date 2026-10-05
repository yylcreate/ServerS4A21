using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvfLib
{
    /// <summary>
    /// Optional NPC activity entry fields. A role's enter-town argument is a
    /// destination NPC index; it does not establish a physical town identifier.
    /// Existing NpcFile consumers and unrelated role fields remain unchanged.
    /// </summary>
    public sealed class NpcActivityEntryDefinition
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public int? EnterTownNpcId { get; private set; }
        public int? ExitTownRoute { get; private set; }
        public int? EventId { get; private set; }
        public int? AccountDungeonId { get; private set; }
        public IReadOnlyList<int> HiddenTownIds { get; private set; }
            = Array.AsReadOnly(Array.Empty<int>());

        private NpcActivityEntryDefinition() { }

        public static NpcActivityEntryDefinition From(NpcFile file)
            => Parse(file?.Content);

        public static NpcActivityEntryDefinition Parse(string content)
        {
            var definition = new NpcActivityEntryDefinition();
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("NPC activity definition is empty.");
                var root = new ScriptParser().Parse(content);
                int? enterTownNpcId = null;
                int? exitTownRoute = null;
                int? eventId = null;
                int? accountDungeonId = null;
                IReadOnlyList<int> hiddenTownIds = null;
                var seenRole = false;
                foreach (var node in root.Children)
                {
                    if (ActivityConfigScriptReader.TagIs(node, "role"))
                    {
                        if (seenRole || !node.HasEndTag || node.Children.Count != 0)
                            throw new InvalidOperationException("NPC role must be one closed, non-nested section.");
                        seenRole = true;
                        ReadRole(node, content, ref enterTownNpcId, ref exitTownRoute);
                    }
                    else if (ActivityConfigScriptReader.TagIs(node, "open visible event"))
                    {
                        if (eventId.HasValue)
                            throw new InvalidOperationException("NPC visible event is duplicated.");
                        eventId = PositiveScalar(node, content);
                    }
                    else if (ActivityConfigScriptReader.TagIs(node, "account dungeon incount display"))
                    {
                        if (accountDungeonId.HasValue)
                            throw new InvalidOperationException("NPC account dungeon display is duplicated.");
                        accountDungeonId = PositiveScalar(node, content);
                    }
                    else if (ActivityConfigScriptReader.TagIs(node, "hide at town"))
                    {
                        if (hiddenTownIds != null)
                            throw new InvalidOperationException("NPC hidden-town section is duplicated.");
                        hiddenTownIds = ReadHiddenTownIds(node, content);
                    }
                }

                // An unrelated NPC is a valid empty definition. Publish fields
                // together only after every relevant field has been checked.
                definition.EnterTownNpcId = enterTownNpcId;
                definition.ExitTownRoute = exitTownRoute;
                definition.EventId = eventId;
                definition.AccountDungeonId = accountDungeonId;
                if (hiddenTownIds != null) definition.HiddenTownIds = hiddenTownIds;
                definition.IsValid = true;
            }
            catch (Exception exception)
            {
                definition.InvalidReason = exception.Message;
            }
            return definition;
        }

        private static int PositiveScalar(ScriptNode node, string content)
        {
            var value = ActivityConfigScriptReader.SingleInteger(node, content);
            if (value <= 0)
                throw new InvalidOperationException("NPC [" + node.Tag + "] requires a positive integer.");
            return value;
        }

        private static IReadOnlyList<int> ReadHiddenTownIds(ScriptNode node, string content)
        {
            if (!node.HasEndTag)
                throw new InvalidOperationException("NPC hidden-town section must be closed.");
            var values = ActivityConfigScriptReader.Integers(node, content);
            if (values.Count == 0)
                throw new InvalidOperationException("NPC hidden-town section requires a town index.");
            var seen = new HashSet<int>();
            foreach (var value in values)
                if (value <= 0 || !seen.Add(value))
                    throw new InvalidOperationException("NPC hidden-town indices must be positive and unique.");
            return values.AsReadOnly();
        }

        private static void ReadRole(ScriptNode node, string content,
            ref int? enterTownNpcId, ref int? exitTownRoute)
        {
            var tokens = new List<RoleToken>();
            foreach (var item in node.DataItems)
                ReadRoleTokens(item.GetContent(content), tokens);
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                var enter = string.Equals(token.Value, "[enter town]", StringComparison.OrdinalIgnoreCase);
                var exit = string.Equals(token.Value, "[exit town]", StringComparison.OrdinalIgnoreCase);
                if (!enter && !exit)
                    continue;
                if (!token.Quoted || index + 1 >= tokens.Count || tokens[index + 1].Quoted
                    || !int.TryParse(tokens[index + 1].Value, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var value)
                    || (enter ? value <= 0 : value < 0)
                    || (index + 2 < tokens.Count && !IsRoleKey(tokens[index + 2])))
                    throw new InvalidOperationException("NPC " + token.Value + " requires one valid unquoted integer.");
                if (enter)
                {
                    if (enterTownNpcId.HasValue)
                        throw new InvalidOperationException("NPC enter-town role is duplicated.");
                    enterTownNpcId = value;
                }
                else
                {
                    if (exitTownRoute.HasValue)
                        throw new InvalidOperationException("NPC exit-town role is duplicated.");
                    exitTownRoute = value;
                }
                index++;
            }
        }

        private static bool IsRoleKey(RoleToken token)
            => token.Quoted && token.Value.Length >= 2
                && token.Value[0] == '[' && token.Value[token.Value.Length - 1] == ']';

        private static void ReadRoleTokens(string source, ICollection<RoleToken> destination)
        {
            // ScriptValueTokenizer remains the value parser. This boundary pass
            // only retains quoting information and rejects incomplete quoting,
            // so a quoted integer cannot become a valid role-route argument.
            var quoted = new List<bool>();
            var end = source.Length;
            var position = 0;
            while (position < source.Length)
            {
                while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
                if (position == source.Length) break;
                if (source[position] == '#') { end = position; break; }
                if (source[position] == '`')
                {
                    var close = source.IndexOf('`', position + 1);
                    if (close < 0)
                        throw new InvalidOperationException("NPC role contains an unclosed quoted value.");
                    quoted.Add(true);
                    position = close + 1;
                    if (position < source.Length && !char.IsWhiteSpace(source[position]) && source[position] != '#')
                        throw new InvalidOperationException("NPC role values need separate token boundaries.");
                }
                else
                {
                    var start = position;
                    while (position < source.Length && !char.IsWhiteSpace(source[position]) && source[position] != '#')
                    {
                        if (source[position] == '`')
                            throw new InvalidOperationException("NPC role contains malformed quoting.");
                        position++;
                    }
                    var raw = source.Substring(start, position - start);
                    if (raw.StartsWith("[enter", StringComparison.OrdinalIgnoreCase)
                        || raw.StartsWith("[exit", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("NPC town role keys must be quoted script values.");
                    quoted.Add(false);
                }
            }
            var tokens = ScriptValueTokenizer.Tokenize(source.Substring(0, end));
            if (tokens.Count != quoted.Count)
                throw new InvalidOperationException("NPC role value boundaries are inconsistent.");
            for (var index = 0; index < tokens.Count; index++)
                destination.Add(new RoleToken(tokens[index], quoted[index]));
        }

        private readonly struct RoleToken
        {
            internal RoleToken(string value, bool quoted) { Value = value; Quoted = quoted; }
            internal string Value { get; }
            internal bool Quoted { get; }
        }
    }
}
