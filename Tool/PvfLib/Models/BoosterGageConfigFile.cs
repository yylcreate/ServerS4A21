using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PvfLib
{
    /// <summary>
    /// Read-only definition of etc/boostergage.etc. Gauge projection, account
    /// progress, event visibility and reward grants remain with their callers.
    /// </summary>
    public sealed class BoosterGageConfigFile : PvfModelBase
    {
        private static readonly Regex TokenRegex = new Regex(
            @"\G(?:\s+|#[^\r\n]*|(?<token>[^\s#]+))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public int MaxGage { get; private set; }
        public int SubGage { get; private set; }
        public int RewardMultiple { get; private set; }
        public int RandomBoxType { get; private set; }
        public IReadOnlyDictionary<int, int> BoosterPointsByItemId
            { get; private set; }
            = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

        private BoosterGageConfigFile() { }

        public static BoosterGageConfigFile Parse(string content)
        {
            var file = new BoosterGageConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("Booster gauge config is empty.");

                var root = new ScriptParser().Parse(content);
                if (root.DataItems.Count != 0 || root.Children.Count != 5)
                    throw new InvalidOperationException(
                        "Booster gauge requires its five root fields without root data.");

                var maxNode = RequiredLeaf(root, "max gage", requireEndTag: true);
                var subNode = RequiredLeaf(root, "sub gage", requireEndTag: true);
                var rewardNode = RequiredLeaf(root, "reward multiple", requireEndTag: true);
                var pointsNode = RequiredLeaf(root, "booster point", requireEndTag: true);
                // The current resource ends with a scalar without an end tag.
                var typeNode = RequiredLeaf(root, "randombox type", requireEndTag: false);
                ValidateSourceBoundaries(root, content);

                var maximum = SingleInteger(maxNode, content);
                var subtraction = SingleInteger(subNode, content);
                var rewardMultiple = SingleInteger(rewardNode, content);
                var randomBoxType = SingleInteger(typeNode, content);
                if (maximum <= 0 || maximum > byte.MaxValue)
                    throw new InvalidOperationException(
                        "Booster gauge [max gage] must fit a positive client byte.");
                if (subtraction <= 0 || subtraction > maximum)
                    throw new InvalidOperationException(
                        "Booster gauge [sub gage] must be positive and not exceed [max gage].");
                if (rewardMultiple <= 0)
                    throw new InvalidOperationException(
                        "Booster gauge [reward multiple] must be positive.");
                if (randomBoxType < 0 || randomBoxType > byte.MaxValue)
                    throw new InvalidOperationException(
                        "Booster gauge [randombox type] must fit a non-negative byte.");

                var values = Integers(pointsNode, content);
                if (values.Count == 0 || values.Count % 2 != 0)
                    throw new InvalidOperationException(
                        "Booster gauge [booster point] requires non-empty item/point pairs.");
                var points = new Dictionary<int, int>();
                for (var offset = 0; offset < values.Count; offset += 2)
                {
                    var itemId = values[offset];
                    var point = values[offset + 1];
                    if (itemId <= 0 || point < 0 || point > maximum
                        || !points.TryAdd(itemId, point))
                    {
                        throw new InvalidOperationException(
                            "Booster gauge item IDs must be positive and unique; points must be between zero and [max gage].");
                    }
                }

                // Do not expose partial rules if any later field or row fails.
                file.Root = root;
                file.MaxGage = maximum;
                file.SubGage = subtraction;
                file.RewardMultiple = rewardMultiple;
                file.RandomBoxType = randomBoxType;
                file.BoosterPointsByItemId = new ReadOnlyDictionary<int, int>(points);
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public bool TryGetBoosterPoint(int itemTemplateId, out int point)
        {
            point = 0;
            return IsValid && BoosterPointsByItemId.TryGetValue(itemTemplateId, out point);
        }

        private static ScriptNode RequiredLeaf(
            ScriptNode root, string tag, bool requireEndTag)
        {
            var children = root.GetChildren(tag);
            if (children.Count != 1)
                throw new InvalidOperationException(
                    $"Booster gauge requires exactly one [{tag}] field.");
            var node = children[0];
            if (node.Children.Count != 0 || (requireEndTag && !node.HasEndTag))
                throw new InvalidOperationException(
                    $"Booster gauge [{tag}] must be a leaf with its required closing tag.");
            return node;
        }

        private static int SingleInteger(ScriptNode node, string content)
        {
            var values = Integers(node, content);
            if (values.Count != 1)
                throw new InvalidOperationException(
                    $"Booster gauge [{node.Tag}] requires exactly one integer.");
            return values[0];
        }

        private static List<int> Integers(ScriptNode node, string content)
        {
            var result = new List<int>();
            foreach (var data in node.DataItems)
            {
                var text = data.GetContent(content);
                var offset = 0;
                while (offset < text.Length)
                {
                    var match = TokenRegex.Match(text, offset);
                    if (!match.Success || match.Index != offset)
                        throw new InvalidOperationException(
                            $"Booster gauge [{node.Tag}] contains an invalid token.");
                    if (match.Groups["token"].Success)
                    {
                        var token = match.Groups["token"].Value;
                        if (!int.TryParse(token, NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out var value))
                            throw new InvalidOperationException(
                                $"Booster gauge [{node.Tag}] contains an invalid Int32 token: {token}");
                        result.Add(value);
                    }
                    offset += match.Length;
                }
            }
            return result;
        }

        private static void ValidateSourceBoundaries(ScriptNode root, string content)
        {
            // ScriptParser ignores unmatched closing tags. Validate only the
            // boundaries it produced; data extraction remains on its tree.
            var boundaries = new Dictionary<int, string>();
            foreach (var node in root.Children)
            {
                boundaries.Add(node.StartLineIndex, "[" + node.Tag + "]");
                if (node.HasEndTag)
                    boundaries.Add(node.EndLineIndex, "[/" + node.Tag + "]");
            }
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
                {
                    throw new InvalidOperationException(
                        "Booster gauge contains an unmatched, malformed or unexpected tag.");
                }
                lineIndex++;
            }
        }
    }
}
