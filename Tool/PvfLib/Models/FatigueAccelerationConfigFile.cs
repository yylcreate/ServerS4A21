using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace PvfLib
{
    /// <summary>
    /// Read-only level/gold and disabled-dungeon tables from the fatigue
    /// acceleration resources. The caller must require IsValid; this definition
    /// does not enable an event, spend gold/fatigue or choose experience rates.
    /// Root and Content retain the level-money source.
    /// </summary>
    public sealed class FatigueAccelerationConfigFile : PvfModelBase
    {
        private HashSet<int> _disabledDungeons = new HashSet<int>();

        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public string DisabledDungeonContent { get; private set; } = string.Empty;
        public IReadOnlyDictionary<int, int> GoldCostsByLevel { get; private set; }
            = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());
        public IReadOnlyList<int> DisabledDungeonIds { get; private set; }
            = Array.Empty<int>();

        private FatigueAccelerationConfigFile() { }

        public static FatigueAccelerationConfigFile Parse(
            string levelMoneyContent, string unableDungeonContent)
        {
            var file = new FatigueAccelerationConfigFile
            {
                Content = levelMoneyContent ?? string.Empty,
                DisabledDungeonContent = unableDungeonContent ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var (root, costValues) = ReadRequiredTable(
                    file.Content, "level money", allowEmpty: false);
                var (_, dungeonValues) = ReadRequiredTable(
                    file.DisabledDungeonContent, "unable dungeon", allowEmpty: true);
                if (costValues.Count % 2 != 0)
                    throw new InvalidOperationException(
                        "Fatigue acceleration [level money] requires level/gold pairs.");

                var costs = new Dictionary<int, int>();
                for (var offset = 0; offset < costValues.Count; offset += 2)
                {
                    var level = costValues[offset];
                    var gold = costValues[offset + 1];
                    if (level <= 0 || gold < 0 || !costs.TryAdd(level, gold))
                        throw new InvalidOperationException(
                            "Fatigue acceleration levels must be positive and unique, and gold costs non-negative.");
                }

                var dungeonIds = new List<int>();
                var seenDungeons = new HashSet<int>();
                foreach (var dungeonId in dungeonValues)
                {
                    if (dungeonId <= 0 || !seenDungeons.Add(dungeonId))
                        throw new InvalidOperationException(
                            "Fatigue acceleration disabled dungeon IDs must be positive and unique.");
                    dungeonIds.Add(dungeonId);
                }

                // Validate both complete resources before publishing any rule.
                // Missing levels stay absent, including levels above the table.
                var readOnlyCosts = new ReadOnlyDictionary<int, int>(costs);
                var readOnlyDungeons = dungeonIds.AsReadOnly();
                file.Root = root;
                file.GoldCostsByLevel = readOnlyCosts;
                file.DisabledDungeonIds = readOnlyDungeons;
                file._disabledDungeons = seenDungeons;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public bool TryGetGoldCost(int level, out int goldCost)
        {
            goldCost = 0;
            return IsValid && GoldCostsByLevel.TryGetValue(level, out goldCost);
        }

        public bool IsDungeonDisabled(int dungeonId)
            => IsValid && _disabledDungeons.Contains(dungeonId);

        private static (ScriptNode Root, List<int> Values) ReadRequiredTable(
            string content, string tag, bool allowEmpty)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException(
                    $"Fatigue acceleration [{tag}] resource is empty.");
            var root = new ScriptParser().Parse(content);
            if (root.Children.Count != 1 || root.DataItems.Count != 0
                || !string.Equals(root.Children[0].Tag, tag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Fatigue acceleration requires only one [{tag}] root table.");
            var node = root.Children[0];
            if (!node.HasEndTag || node.Children.Count != 0)
                throw new InvalidOperationException(
                    $"Fatigue acceleration [{tag}] must be a closed table without nested tags.");

            // ScriptParser intentionally ignores unmatched closing tags. These
            // two leaf resources must not accept such ignored source lines.
            var lines = content.Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
            {
                var line = lines[lineIndex].Trim();
                if (line.Length == 0 || line[0] == '#'
                    || lineIndex == node.StartLineIndex || lineIndex == node.EndLineIndex)
                    continue;
                if (lineIndex < node.StartLineIndex || lineIndex > node.EndLineIndex
                    || (line[0] == '[' && line[line.Length - 1] == ']'))
                    throw new InvalidOperationException(
                        $"Fatigue acceleration [{tag}] contains data or tags outside its leaf table.");
            }

            var values = new List<int>();
            foreach (var data in node.DataItems)
            {
                var text = data.GetContent(content);
                var commentIndex = text.IndexOf('#');
                if (commentIndex >= 0) text = text.Substring(0, commentIndex);
                foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(token, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var value))
                        throw new InvalidOperationException(
                            $"Fatigue acceleration [{tag}] contains an invalid Int32 token: {token}");
                    values.Add(value);
                }
            }
            if (!allowEmpty && values.Count == 0)
                throw new InvalidOperationException(
                    $"Fatigue acceleration [{tag}] table is empty.");
            return (root, values);
        }
    }
}
