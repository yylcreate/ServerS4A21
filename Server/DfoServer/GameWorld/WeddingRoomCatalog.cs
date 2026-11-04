using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal sealed record WeddingTreeReward(int ItemId, int Weight, int Count);
    internal sealed record WeddingTreeRule(int Index, int ExpireDays, int DailyWater, int MaximumGrowth,
        IReadOnlyDictionary<int, int> WaterItems, IReadOnlyList<WeddingTreeReward> Rewards);

    internal sealed class WeddingRoomCatalog
    {
        internal const string TreePath = "decoration/coupletree.etc";
        internal const string FurniturePath = "decoration/coupleroomiteminfo.etc";
        private static readonly Lazy<WeddingRoomCatalog> Current = new(() => Parse(
            PvfArchiveAccessor.ReadText(TreePath), PvfArchiveAccessor.ReadText(FurniturePath)));
        internal static WeddingRoomCatalog Get() => Current.Value;
        internal IReadOnlyDictionary<int, int> Seeds { get; private init; }
        internal IReadOnlyDictionary<int, WeddingTreeRule> Trees { get; private init; }
        internal IReadOnlyDictionary<int, IReadOnlySet<int>> FurniturePositions { get; private init; }

        internal static WeddingRoomCatalog Parse(string treeText, string furnitureText)
        {
            var treeRoot = new ScriptParser().Parse(treeText);
            var furnitureRoot = new ScriptParser().Parse(furnitureText);
            int[] Ints(ScriptNode node, string text) => Tokens(node, text).Select(x => Integer(x, node.Tag)).ToArray();
            ScriptNode One(ScriptNode parent, string tag)
            {
                var nodes = parent.GetChildren(tag);
                return nodes.Count == 1 ? nodes[0] : throw Bad(tag);
            }
            int Scalar(ScriptNode parent, string tag)
            {
                var values = Ints(One(parent, tag), treeText);
                return values.Length == 1 ? values[0] : throw Bad(tag);
            }
            var seeds = new Dictionary<int, int>();
            var seedValues = Ints(One(treeRoot, "seed"), treeText);
            if (seedValues.Length == 0 || seedValues.Length % 2 != 0) throw Bad("seed");
            for (int i = 0; i < seedValues.Length; i += 2)
                if (seedValues[i] <= 0 || seedValues[i + 1] <= 0 || !seeds.TryAdd(seedValues[i], seedValues[i + 1])) throw Bad("seed");
            var trees = new Dictionary<int, WeddingTreeRule>();
            foreach (var node in treeRoot.GetChildren("tree"))
            {
                if (!node.HasEndTag) throw Bad("tree");
                int index = Scalar(node, "index"), days = Scalar(node, "expire day"), waterCount = Scalar(node, "day water count");
                var water = new Dictionary<int, int>();
                var waterValues = Ints(One(node, "grow point"), treeText);
                if (waterValues.Length == 0 || waterValues.Length % 2 != 0) throw Bad("grow point");
                for (int i = 0; i < waterValues.Length; i += 2)
                    if (waterValues[i] <= 0 || waterValues[i + 1] <= 0 || !water.TryAdd(waterValues[i], waterValues[i + 1])) throw Bad("grow point");
                var steps = Tokens(One(node, "step"), treeText);
                if (steps.Length == 0 || steps.Length % 2 != 0) throw Bad("step");
                int maximum = 0;
                for (int i = 0; i < steps.Length; i += 2)
                {
                    int next = Integer(steps[i], "step");
                    if (next <= maximum || string.IsNullOrWhiteSpace(steps[i + 1])) throw Bad("step");
                    maximum = next;
                }
                var rewards = new List<WeddingTreeReward>();
                var rewardValues = Ints(One(node, "reward"), treeText);
                if (rewardValues.Length == 0 || rewardValues.Length % 3 != 0) throw Bad("reward");
                long total = 0;
                for (int i = 0; i < rewardValues.Length; i += 3)
                {
                    if (rewardValues[i] <= 0 || rewardValues[i + 1] <= 0 || rewardValues[i + 2] <= 0) throw Bad("reward");
                    total += rewardValues[i + 1];
                    rewards.Add(new(rewardValues[i], rewardValues[i + 1], rewardValues[i + 2]));
                }
                if (index <= 0 || days is <= 0 or > 36500 || waterCount is <= 0 or > 127 || maximum <= 0 || total > int.MaxValue
                    || !trees.TryAdd(index, new(index, days, waterCount, maximum,
                        new ReadOnlyDictionary<int, int>(water), rewards.AsReadOnly()))) throw Bad("tree");
            }
            if (trees.Count == 0 || seeds.Values.Any(x => !trees.ContainsKey(x))) throw Bad("seed/tree");
            var positions = new HashSet<int>();
            var positionValues = Ints(One(furnitureRoot, "position list"), furnitureText);
            if (positionValues.Length == 0 || positionValues.Length % 4 != 0) throw Bad("position list");
            for (int i = 0; i < positionValues.Length; i += 4)
                if (positionValues[i] is < 0 or >= 28 || !positions.Add(positionValues[i])) throw Bad("position list");
            var furniture = new Dictionary<int, IReadOnlySet<int>>();
            foreach (var node in One(furnitureRoot, "coupleroom item list").GetChildren("coupleroom item"))
            {
                var tokens = Tokens(node, furnitureText);
                if (!node.HasEndTag || tokens.Length < 9) throw Bad("coupleroom item");
                var values = tokens.Take(tokens.Length - 1).Select(x => Integer(x, "coupleroom item")).ToArray();
                int count = values[1];
                if (values[0] <= 0 || count is < 1 or > 28 || values.Length != 3 + 5 * count
                    || string.IsNullOrWhiteSpace(tokens[^1])) throw Bad("coupleroom item");
                var allowed = new HashSet<int>();
                for (int i = 0; i < count; i++)
                    if (!positions.Contains(values[2 + 5 * i]) || !allowed.Add(values[2 + 5 * i])) throw Bad("coupleroom item position");
                if (!furniture.TryAdd(values[0], allowed.ToFrozenSet())) throw Bad("coupleroom item duplicate");
            }
            if (furniture.Count == 0) throw Bad("coupleroom item list");
            return new WeddingRoomCatalog { Seeds = new ReadOnlyDictionary<int, int>(seeds), Trees = new ReadOnlyDictionary<int, WeddingTreeRule>(trees),
                FurniturePositions = new ReadOnlyDictionary<int, IReadOnlySet<int>>(furniture) };
        }

        private static string[] Tokens(ScriptNode node, string text)
        {
            if (node.Children.Count != 0) throw Bad(node.Tag);
            return ScriptValueTokenizer.Tokenize(string.Join("\n", node.DataItems.Select(x => x.GetContent(text)))).ToArray();
        }
        private static int Integer(string text, string tag) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw Bad(tag);
        private static InvalidDataException Bad(string tag) => new($"A21 wedding room PVF: invalid [{tag}].");
    }
}
