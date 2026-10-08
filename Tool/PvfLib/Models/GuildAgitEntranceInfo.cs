using System;
using System.IO;
using System.Linq;

namespace PvfLib
{
    /// <summary>The MAP entrance rectangle and destination; source presence is not a guild-town role.</summary>
    public sealed class GuildAgitEntranceInfo
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public bool HasDefinition { get; private set; }
        public int TargetTownId { get; private set; }
        public int TargetAreaId { get; private set; }
        public bool HasPhysicalDestination => IsValid && HasDefinition && TargetTownId > 0;

        public static GuildAgitEntranceInfo Parse(string content)
        {
            var result = new GuildAgitEntranceInfo();
            try
            {
                content ??= string.Empty;
                var root = new ScriptParser().Parse(content);
                var nodes = root.Children.Where(node => ActivityConfigScriptReader.TagIs(node,
                    "guild agit entrance info")).ToArray();
                if (nodes.Length > 1 || nodes.Length == 1 && !nodes[0].HasEndTag)
                    throw new InvalidOperationException("[guild agit entrance info] must be unique and closed.");
                var node = nodes.SingleOrDefault();
                using var reader = new StringReader(content);
                string line;
                var index = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    var text = line.Trim();
                    if (text.Length > 0 && text[0] != '#')
                    {
                        var boundary = text.StartsWith("[guild agit entrance info", StringComparison.OrdinalIgnoreCase)
                            || text.StartsWith("[/guild agit entrance info", StringComparison.OrdinalIgnoreCase);
                        if (boundary && (node == null
                            || !(index == node.StartLineIndex && string.Equals(text, "[" + node.Tag + "]", StringComparison.OrdinalIgnoreCase))
                                && !(index == node.EndLineIndex && string.Equals(text, "[/" + node.Tag + "]", StringComparison.OrdinalIgnoreCase))))
                            throw new InvalidOperationException("[guild agit entrance info] has an unmatched or malformed boundary.");
                        if (node != null && index > node.StartLineIndex && index < node.EndLineIndex
                            && (text[0] == '[' || text[text.Length - 1] == ']'))
                            throw new InvalidOperationException("[guild agit entrance info] cannot contain nested or unmatched tags.");
                    }
                    index++;
                }
                if (node != null)
                {
                    var values = MapFile.ReadSixIntegerRows(node, content);
                    if (values?.Length != 6 || values[2] <= 0 || values[3] <= 0)
                        throw new InvalidOperationException("[guild agit entrance info] requires one six-integer rectangle/destination row.");
                    var town = values[4];
                    var area = values[5];
                    var physical = town > 0 && town <= byte.MaxValue && area >= 0 && area <= byte.MaxValue;
                    var sentinel = town == area && town is 0 or -1 or -2;
                    if (!physical && !sentinel)
                        throw new InvalidOperationException("[guild agit entrance info] has an invalid physical target or return sentinel.");
                    result.HasDefinition = true;
                    result.TargetTownId = town;
                    result.TargetAreaId = area;
                }
                result.IsValid = true;
            }
            catch (Exception exception)
            {
                result.InvalidReason = exception.Message;
            }
            return result;
        }
    }
}
