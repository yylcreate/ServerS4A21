using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PvfLib
{
    public sealed class GoldSilverHornDungeonEntry
    {
        internal GoldSilverHornDungeonEntry(int dungeonId, int maxEnterCount,
            IReadOnlyList<int> dungeonEnterCost)
        {
            DungeonId = dungeonId;
            MaxEnterCount = maxEnterCount;
            DungeonEnterCost = dungeonEnterCost;
        }

        public int DungeonId { get; }
        public int MaxEnterCount { get; }

        // Absent in the current resource. Retain configured integer pairs without
        // assigning unproven item, currency or level semantics to either field.
        public IReadOnlyList<int> DungeonEnterCost { get; }
    }

    /// <summary>
    /// Read-only etc/chn_goldsilverhorndungeon.etc definition. Missing gold or
    /// silver sections stay unconfigured; this model does not open an activity,
    /// choose an admission policy or authorize an inventory debit.
    /// </summary>
    public sealed class GoldSilverHornDungeonConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public int WorldMapIndex { get; private set; }
        public GoldSilverHornDungeonEntry Gold { get; private set; }
        public GoldSilverHornDungeonEntry Silver { get; private set; }

        private GoldSilverHornDungeonConfigFile() { }

        public static GoldSilverHornDungeonConfigFile Parse(string content)
        {
            var file = new GoldSilverHornDungeonConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var root = ActivityConfigScriptReader.ReadRoot(content);
                int? worldMapIndex = null;
                GoldSilverHornDungeonEntry gold = null;
                GoldSilverHornDungeonEntry silver = null;
                for (var index = 0; index < root.Children.Count; index++)
                {
                    var node = root.Children[index];
                    if (ActivityConfigScriptReader.TagIs(node, "world map index"))
                    {
                        if (worldMapIndex.HasValue || !node.HasEndTag)
                            throw new InvalidOperationException("Gold/silver world-map field must be unique and closed.");
                        var value = ActivityConfigScriptReader.SingleInteger(node, content);
                        if (value <= 0)
                            throw new InvalidOperationException("Gold/silver world-map index must be positive.");
                        worldMapIndex = value;
                        continue;
                    }

                    var isGold = ActivityConfigScriptReader.TagIs(node, "gold horn dungeon");
                    if (!isGold && !ActivityConfigScriptReader.TagIs(node, "silver horn dungeon"))
                        throw new InvalidOperationException("Unexpected gold/silver root field: " + node.Tag);
                    if ((isGold ? gold : silver) != null)
                        throw new InvalidOperationException("Gold/silver dungeon sections must be unique.");

                    GoldSilverHornDungeonEntry entry;
                    if (node.HasEndTag)
                    {
                        if (node.DataItems.Count != 0)
                            throw new InvalidOperationException("Gold/silver section has data outside its fields.");
                        entry = ReadEntry(node.Children, content);
                    }
                    else
                    {
                        // The current ETC uses the same empty section tag twice,
                        // with scalar fields as ScriptParser root siblings.
                        RequireMarker(node);
                        var fields = new List<ScriptNode>();
                        var closed = false;
                        while (++index < root.Children.Count)
                        {
                            var field = root.Children[index];
                            if (ActivityConfigScriptReader.TagIs(field, node.Tag))
                            {
                                RequireMarker(field);
                                closed = true;
                                break;
                            }
                            fields.Add(field);
                        }
                        if (!closed)
                            throw new InvalidOperationException("Gold/silver repeated-tag section is not closed.");
                        entry = ReadEntry(fields, content);
                    }

                    if (isGold) gold = entry;
                    else silver = entry;
                }
                if (!worldMapIndex.HasValue || (gold == null && silver == null))
                    throw new InvalidOperationException("Gold/silver config needs a world map and at least one configured dungeon.");

                // Publish only after every field and section has been validated.
                file.Root = root;
                file.WorldMapIndex = worldMapIndex.Value;
                file.Gold = gold;
                file.Silver = silver;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        private static void RequireMarker(ScriptNode node)
        {
            if (node.HasEndTag || node.Children.Count != 0 || node.DataItems.Count != 0)
                throw new InvalidOperationException("Gold/silver repeated-tag boundary must be an empty marker.");
        }

        private static GoldSilverHornDungeonEntry ReadEntry(
            IReadOnlyList<ScriptNode> fields, string content)
        {
            int? dungeonId = null;
            int? maxEnterCount = null;
            IReadOnlyList<int> costs = null;
            foreach (var node in fields)
            {
                if (ActivityConfigScriptReader.TagIs(node, "dungeon index"))
                {
                    if (dungeonId.HasValue)
                        throw new InvalidOperationException("Gold/silver dungeon index is duplicated.");
                    dungeonId = ActivityConfigScriptReader.SingleInteger(node, content);
                }
                else if (ActivityConfigScriptReader.TagIs(node, "max enter count"))
                {
                    if (maxEnterCount.HasValue)
                        throw new InvalidOperationException("Gold/silver maximum entry count is duplicated.");
                    maxEnterCount = ActivityConfigScriptReader.SingleInteger(node, content);
                }
                else if (ActivityConfigScriptReader.TagIs(node, "dungeon enter cost"))
                {
                    if (costs != null)
                        throw new InvalidOperationException("Gold/silver entry-cost table is duplicated.");
                    var values = ActivityConfigScriptReader.Integers(node, content);
                    if (values.Count == 0 || values.Count % 2 != 0)
                        throw new InvalidOperationException("Gold/silver entry costs require complete integer pairs.");
                    costs = values.AsReadOnly();
                }
                else
                    throw new InvalidOperationException("Unexpected gold/silver dungeon field: " + node.Tag);
            }
            if (!dungeonId.HasValue || dungeonId.Value <= 0
                || !maxEnterCount.HasValue || maxEnterCount.Value <= 0)
                throw new InvalidOperationException("Gold/silver dungeon needs a positive index and maximum entry count.");
            return new GoldSilverHornDungeonEntry(dungeonId.Value, maxEnterCount.Value, costs);
        }
    }

    // Shared only by these numeric activity resources. Business data is read
    // from ScriptParser nodes; the source pass rejects ignored stray tag lines.
    internal static class ActivityConfigScriptReader
    {
        internal static ScriptNode ReadRoot(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Activity configuration is empty.");
            var root = new ScriptParser().Parse(content);
            if (root.DataItems.Count != 0)
                throw new InvalidOperationException("Activity configuration contains root data.");
            var boundaries = new Dictionary<int, string>();
            foreach (var node in root.Children) AddBoundaries(node, boundaries);
            using var reader = new StringReader(content);
            string line;
            var lineIndex = 0;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.Length != 0 && text[0] != '#'
                    && (text[0] == '[' || text[text.Length - 1] == ']')
                    && (!boundaries.TryGetValue(lineIndex, out var expected)
                        || !string.Equals(text, expected, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Activity configuration contains an unmatched or malformed tag.");
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

        internal static bool TagIs(ScriptNode node, string tag)
            => string.Equals(node.Tag, tag, StringComparison.OrdinalIgnoreCase);

        internal static int SingleInteger(ScriptNode node, string content)
        {
            var values = Integers(node, content);
            if (values.Count != 1)
                throw new InvalidOperationException("Activity [" + node.Tag + "] requires exactly one integer.");
            return values[0];
        }

        internal static List<int> Integers(ScriptNode node, string content)
        {
            if (node.Children.Count != 0)
                throw new InvalidOperationException("Activity [" + node.Tag + "] cannot contain nested tags.");
            var values = new List<int>();
            foreach (var data in node.DataItems)
            {
                var text = data.GetContent(content);
                var comment = text.IndexOf('#');
                if (comment >= 0) text = text.Substring(0, comment);
                foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(token, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var value))
                        throw new InvalidOperationException("Activity [" + node.Tag + "] contains an invalid Int32: " + token);
                    values.Add(value);
                }
            }
            return values;
        }
    }
}
