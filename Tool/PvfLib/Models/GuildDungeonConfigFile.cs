using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class GuildDungeonItemReward
    {
        internal GuildDungeonItemReward(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public sealed class GuildDungeonGaugeReward
    {
        internal GuildDungeonGaugeReward(int gaugeRatio, GuildDungeonItemReward item, int contribute)
        {
            GaugeRatio = gaugeRatio;
            Item = item;
            Contribute = contribute;
        }

        // The resource key is preserved without choosing a reward interval.
        public int GaugeRatio { get; }
        public GuildDungeonItemReward Item { get; }
        public int Contribute { get; }
    }

    public sealed class GuildDungeonEntry
    {
        internal GuildDungeonEntry(int dungeonId, int gaugeLimit, int gaugeOnClear,
            int conquestGaugeRatio, string color, string type,
            IReadOnlyList<int> requiredDungeonConquest, IReadOnlyList<GuildDungeonGaugeReward> rewards)
        {
            DungeonId = dungeonId;
            GaugeLimit = gaugeLimit;
            GaugeOnClear = gaugeOnClear;
            ConquestGaugeRatio = conquestGaugeRatio;
            Color = color;
            Type = type;
            RequiredDungeonConquest = requiredDungeonConquest;
            Rewards = rewards;
        }

        public int DungeonId { get; }
        public int GaugeLimit { get; }
        public int GaugeOnClear { get; }
        public int ConquestGaugeRatio { get; }
        public string Color { get; }
        public string Type { get; }
        public IReadOnlyList<int> RequiredDungeonConquest { get; }
        public IReadOnlyList<GuildDungeonGaugeReward> Rewards { get; }
    }

    /// <summary>
    /// Immutable typed ETC definition for guild-dungeon resource fields. Raw
    /// gauges, ratios, reward keys and party-buff pairs keep their resource
    /// values; this model does not choose units, unlock semantics, a reset
    /// schedule, player/guild progress, a reward owner or an admission policy.
    /// </summary>
    public sealed class GuildDungeonConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuildDungeonEntry> Dungeons { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildDungeonEntry>());
        public int? WeeklyRewardFinalDungeonId { get; private set; }
        public GuildDungeonItemReward RepeatClearBonusReward { get; private set; }
        public int? LevelDifference { get; private set; }
        public int? DeleteItem { get; private set; }
        public IReadOnlyList<int> PartyBuffList { get; private set; }
            = Array.AsReadOnly(Array.Empty<int>());

        private GuildDungeonConfigFile() { }

        public static GuildDungeonConfigFile Parse(string content)
        {
            var file = new GuildDungeonConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var root = ActivityConfigScriptReader.ReadRoot(content);
                var sections = new Dictionary<string, ScriptNode>(StringComparer.OrdinalIgnoreCase);
                var dungeons = new List<GuildDungeonEntry>();
                var dungeonIds = new HashSet<int>();
                foreach (var node in root.Children)
                {
                    if (ActivityConfigScriptReader.TagIs(node, "dungeon"))
                    {
                        var entry = ReadDungeon(node, content);
                        if (!dungeonIds.Add(entry.DungeonId))
                            throw new InvalidOperationException("Guild dungeon index is duplicated.");
                        dungeons.Add(entry);
                        continue;
                    }
                    if (!ActivityConfigScriptReader.TagIs(node, "weekly reward")
                        && !ActivityConfigScriptReader.TagIs(node, "repeat clear bonus reward")
                        && !ActivityConfigScriptReader.TagIs(node, "level difference")
                        && !ActivityConfigScriptReader.TagIs(node, "delete item")
                        && !ActivityConfigScriptReader.TagIs(node, "party buff list"))
                        throw new InvalidOperationException("Unexpected guild dungeon root field: " + node.Tag);
                    if (!sections.TryAdd(node.Tag, node))
                        throw new InvalidOperationException("Guild dungeon root field is duplicated: " + node.Tag);
                }
                if (dungeons.Count == 0)
                    throw new InvalidOperationException("Guild dungeon configuration has no dungeon definitions.");

                var weekly = ReadFields(Required(sections, "weekly reward"));
                RequireOnlyFields(weekly, "final dungeon");
                var finalDungeon = ActivityConfigScriptReader.SingleInteger(Required(weekly, "final dungeon"), content);
                if (!dungeonIds.Contains(finalDungeon))
                    throw new InvalidOperationException("Guild weekly reward refers to an undefined final dungeon.");
                var repeatReward = ReadItemReward(Required(sections, "repeat clear bonus reward"), content);
                var levelDifference = ActivityConfigScriptReader.SingleInteger(Required(sections, "level difference"), content);
                var deleteItem = ActivityConfigScriptReader.SingleInteger(Required(sections, "delete item"), content);
                var partyBuff = ActivityConfigScriptReader.Integers(Required(sections, "party buff list"), content);
                if (levelDifference < 0 || deleteItem <= 0)
                    throw new InvalidOperationException("Guild level difference must be nonnegative and delete-item index positive.");
                if (partyBuff.Count == 0 || partyBuff.Count % 2 != 0)
                    throw new InvalidOperationException("Guild party-buff list requires complete integer pairs.");
                ValidatePrerequisites(dungeons);

                // Publish no earlier row or root field if any later definition
                // is malformed, ambiguous or references an invalid graph.
                file.Root = root;
                file.Dungeons = dungeons.AsReadOnly();
                file.WeeklyRewardFinalDungeonId = finalDungeon;
                file.RepeatClearBonusReward = repeatReward;
                file.LevelDifference = levelDifference;
                file.DeleteItem = deleteItem;
                file.PartyBuffList = partyBuff.AsReadOnly();
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        private static GuildDungeonEntry ReadDungeon(ScriptNode node, string content)
        {
            var fields = ReadFields(node);
            RequireOnlyFields(fields, "dungeon index", "gauge limit", "gauge on clear",
                "conquest gauge ratio", "color", "type", "required dungeon conquest", "reward");
            var dungeonId = ActivityConfigScriptReader.SingleInteger(Required(fields, "dungeon index"), content);
            var gaugeLimit = ActivityConfigScriptReader.SingleInteger(Required(fields, "gauge limit"), content);
            var gaugeOnClear = ActivityConfigScriptReader.SingleInteger(Required(fields, "gauge on clear"), content);
            var conquestRatio = ActivityConfigScriptReader.SingleInteger(Required(fields, "conquest gauge ratio"), content);
            if (dungeonId <= 0 || gaugeLimit <= 0 || gaugeOnClear < 0 || conquestRatio < 0)
                throw new InvalidOperationException("Guild dungeon needs a positive index/limit and nonnegative gauge/ratio.");
            var color = ReadQuotedString(Required(fields, "color"), content);
            var type = ReadQuotedString(Required(fields, "type"), content);
            IReadOnlyList<int> required = Array.AsReadOnly(Array.Empty<int>());
            if (fields.TryGetValue("required dungeon conquest", out var requiredNode))
            {
                if (!requiredNode.HasEndTag)
                    throw new InvalidOperationException("Guild dungeon prerequisites must be closed.");
                var values = ActivityConfigScriptReader.Integers(requiredNode, content);
                var seen = new HashSet<int>();
                if (values.Count == 0)
                    throw new InvalidOperationException("Configured guild dungeon prerequisites cannot be empty.");
                foreach (var value in values)
                    if (value <= 0 || value == dungeonId || !seen.Add(value))
                        throw new InvalidOperationException("Guild dungeon prerequisite is invalid, self-referencing or duplicated.");
                required = values.AsReadOnly();
            }
            return new GuildDungeonEntry(dungeonId, gaugeLimit, gaugeOnClear, conquestRatio,
                color, type, required, ReadRewards(Required(fields, "reward"), content));
        }

        private static IReadOnlyList<GuildDungeonGaugeReward> ReadRewards(ScriptNode node, string content)
        {
            RequireClosedContainer(node);
            if (node.Children.Count == 0)
                throw new InvalidOperationException("Guild dungeon reward table is empty.");
            var rewards = new List<GuildDungeonGaugeReward>();
            var ratios = new HashSet<int>();
            foreach (var child in node.Children)
            {
                if (!ActivityConfigScriptReader.TagIs(child, "gauge ratio"))
                    throw new InvalidOperationException("Unexpected guild dungeon reward row: " + child.Tag);
                var fields = ReadFields(child, allowDirectData: true);
                RequireOnlyFields(fields, "item", "contribute");
                foreach (var data in child.DataItems)
                    foreach (var field in child.Children)
                        if (data.LineIndex > field.StartLineIndex)
                            throw new InvalidOperationException("Guild reward ratio must precede its reward fields.");
                // A ratio row has one direct integer before its item and
                // contribution fields. The shared numeric reader is strict
                // for tokens; only its leaf view excludes those child fields.
                var ratioNode = new ScriptNode { Tag = child.Tag, DataItems = child.DataItems };
                var ratio = ActivityConfigScriptReader.SingleInteger(ratioNode, content);
                var item = ReadItemReward(Required(fields, "item"), content);
                var contribute = ActivityConfigScriptReader.SingleInteger(Required(fields, "contribute"), content);
                if (ratio < 0 || contribute < 0 || !ratios.Add(ratio))
                    throw new InvalidOperationException("Guild reward ratio/contribution is invalid or ratio is duplicated.");
                rewards.Add(new GuildDungeonGaugeReward(ratio, item, contribute));
            }
            return rewards.AsReadOnly();
        }

        private static GuildDungeonItemReward ReadItemReward(ScriptNode node, string content)
        {
            var values = ActivityConfigScriptReader.Integers(node, content);
            if (values.Count != 2 || values[0] <= 0 || values[1] <= 0)
                throw new InvalidOperationException("Guild item reward requires exactly one positive item/count pair.");
            return new GuildDungeonItemReward(values[0], values[1]);
        }

        private static string ReadQuotedString(ScriptNode node, string content)
        {
            if (node.Children.Count != 0 || node.DataItems.Count != 1)
                throw new InvalidOperationException("Guild [" + node.Tag + "] requires one quoted string.");
            var text = node.DataItems[0].GetContent(content).Trim();
            var end = text.Length > 0 && text[0] == '`' ? text.IndexOf('`', 1) : -1;
            if (end <= 1)
                throw new InvalidOperationException("Guild [" + node.Tag + "] requires one nonempty backtick string.");
            var suffix = text.Substring(end + 1).Trim();
            if (suffix.Length != 0 && suffix[0] != '#')
                throw new InvalidOperationException("Guild [" + node.Tag + "] has data after its quoted string.");
            return text.Substring(1, end - 1);
        }

        private static Dictionary<string, ScriptNode> ReadFields(ScriptNode node, bool allowDirectData = false)
        {
            if (!node.HasEndTag || (!allowDirectData && node.DataItems.Count != 0))
                throw new InvalidOperationException("Guild [" + node.Tag + "] must be closed with no unexpected direct data.");
            var fields = new Dictionary<string, ScriptNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in node.Children)
                if (!fields.TryAdd(child.Tag, child))
                    throw new InvalidOperationException("Guild [" + node.Tag + "] field is duplicated: " + child.Tag);
            return fields;
        }

        private static void RequireClosedContainer(ScriptNode node)
        {
            if (!node.HasEndTag || node.DataItems.Count != 0)
                throw new InvalidOperationException("Guild [" + node.Tag + "] must be a closed container without direct data.");
        }

        private static ScriptNode Required(IDictionary<string, ScriptNode> fields, string tag)
        {
            if (!fields.TryGetValue(tag, out var node))
                throw new InvalidOperationException("Guild dungeon definition is missing [" + tag + "].");
            return node;
        }

        private static void RequireOnlyFields(IDictionary<string, ScriptNode> fields, params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields)
                if (!names.Contains(field.Key))
                    throw new InvalidOperationException("Unexpected guild dungeon field: " + field.Key);
        }

        private static void ValidatePrerequisites(IReadOnlyList<GuildDungeonEntry> dungeons)
        {
            var definitions = new Dictionary<int, GuildDungeonEntry>();
            foreach (var entry in dungeons) definitions.Add(entry.DungeonId, entry);
            foreach (var entry in dungeons)
                foreach (var required in entry.RequiredDungeonConquest)
                    if (!definitions.ContainsKey(required))
                        throw new InvalidOperationException("Guild dungeon prerequisite refers to an undefined dungeon.");
            var visiting = new HashSet<int>();
            var visited = new HashSet<int>();
            foreach (var entry in dungeons) Visit(entry, definitions, visiting, visited);
        }

        private static void Visit(GuildDungeonEntry entry, IDictionary<int, GuildDungeonEntry> definitions,
            ISet<int> visiting, ISet<int> visited)
        {
            if (visited.Contains(entry.DungeonId)) return;
            if (!visiting.Add(entry.DungeonId))
                throw new InvalidOperationException("Guild dungeon prerequisites contain a cycle.");
            foreach (var required in entry.RequiredDungeonConquest)
                Visit(definitions[required], definitions, visiting, visited);
            visiting.Remove(entry.DungeonId);
            visited.Add(entry.DungeonId);
        }
    }
}
