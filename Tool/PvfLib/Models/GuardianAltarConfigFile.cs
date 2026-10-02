using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PvfLib
{
    public sealed class GuardianAltarShopLevel
    {
        public short Level { get; }
        public short Price { get; }

        internal GuardianAltarShopLevel(short level, short price)
        {
            Level = level;
            Price = price;
        }
    }

    public sealed class GuardianAltarShopEntry
    {
        public int ShopType { get; }
        public int ResourceId { get; }
        public int UnlockStageIndex { get; }
        public bool IsSettableSlot { get; }
        public IReadOnlyList<GuardianAltarShopLevel> Levels { get; }

        internal GuardianAltarShopEntry(
            int shopType, int resourceId, int unlockStageIndex,
            bool isSettableSlot, List<GuardianAltarShopLevel> levels)
        {
            ShopType = shopType;
            ResourceId = resourceId;
            UnlockStageIndex = unlockStageIndex;
            IsSettableSlot = isSettableSlot;
            Levels = levels.AsReadOnly();
        }
    }

    public sealed class GuardianAltarShopRidable
    {
        public int RidableId { get; }
        public IReadOnlyList<GuardianAltarShopEntry> Entries { get; }

        internal GuardianAltarShopRidable(
            int ridableId, List<GuardianAltarShopEntry> entries)
        {
            RidableId = ridableId;
            Entries = entries.AsReadOnly();
        }
    }

    /// <summary>
    /// Static Guardian Altar definitions. Prices and catalog levels are not
    /// player purchases; creating progress belongs to the server application.
    /// </summary>
    public sealed class GuardianAltarConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public int DailyRefillTicket { get; private set; }
        public IReadOnlyList<GuardianAltarShopRidable> Ridables
            { get; private set; } = Array.Empty<GuardianAltarShopRidable>();
        public IReadOnlyList<string> Diagnostics
            { get; private set; } = Array.Empty<string>();

        public static GuardianAltarConfigFile Parse(
            string configContent, string shopContent)
        {
            var file = new GuardianAltarConfigFile
            {
                Content = configContent ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            if (string.IsNullOrWhiteSpace(configContent)
                || string.IsNullOrWhiteSpace(shopContent))
            {
                file.InvalidReason = "Guardian Altar config or shop is empty";
                return file;
            }

            try
            {
                file.Root = new ScriptParser().Parse(configContent);
                var shopRoot = new ScriptParser().Parse(shopContent);
                file.ParseCore(configContent, shopContent, shopRoot);
                file.IsValid = true;
            }
            catch (Exception ex)
            {
                file.InvalidReason = ex.Message;
            }
            return file;
        }

        /// <summary>
        /// Reads only the registered achievement keys used to validate persisted
        /// claim rows. It does not build rewards or create claimed/unclaimed facts.
        /// </summary>
        public static IReadOnlyList<int> ParseAchievementIds(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException(
                    "Guardian Altar achievement definition is empty");
            ScriptNode root;
            try
            {
                root = new ScriptParser().Parse(content);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Guardian Altar achievement script could not be parsed", ex);
            }
            if (root.Children.Any(child => !string.Equals(
                    child.Tag, "reward data", StringComparison.OrdinalIgnoreCase))
                || GuardianAltarScriptReader.Tokens(root, content).Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar achievement definition contains unsupported root data");
            var rows = root.GetChildren("reward data");
            if (rows.Count == 0)
                throw new InvalidOperationException(
                    "Guardian Altar achievement definition has no reward data");

            var allowedTags = new HashSet<string>(new[]
            {
                "type", "stage difficulty", "stage clear count", "icon image",
                "reward item", "reward title", "postal title", "postal letter",
            }, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            var ids = new List<int>();
            foreach (var row in rows)
            {
                GuardianAltarScriptReader.RequireClosed(row);
                if (GuardianAltarScriptReader.Tokens(row, content).Count != 0
                    || row.Children.Any(child => !allowedTags.Contains(child.Tag)))
                    throw new InvalidOperationException(
                        "Guardian Altar reward data contains unsupported fields");
                var type = GuardianAltarScriptReader.SingleChild(row, "type");
                GuardianAltarScriptReader.RequireLeaf(type);
                var id = GuardianAltarScriptReader.SingleInteger(type, content);
                if (id <= 0 || !seen.Add(id))
                    throw new InvalidOperationException(
                        "Guardian Altar reward data has an invalid or duplicate type");
                ids.Add(id);

                foreach (var tag in allowedTags)
                {
                    if (string.Equals(tag, "type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var node = GuardianAltarScriptReader.SingleChild(row, tag, required: false);
                    if (node == null)
                        continue;
                    GuardianAltarScriptReader.RequireLeaf(node);
                    var tokens = GuardianAltarScriptReader.Tokens(node, content);
                    if (string.Equals(tag, "reward item", StringComparison.OrdinalIgnoreCase))
                    {
                        // Retain the resource-owned reward table without interpreting
                        // quantities or granting items in this ID-only reader.
                        GuardianAltarScriptReader.RequireClosed(node);
                        continue;
                    }
                    if (tokens.Count != 1)
                        throw new InvalidOperationException(
                            $"Guardian Altar achievement [{tag}] requires one value");
                    if (string.Equals(tag, "stage clear count", StringComparison.OrdinalIgnoreCase))
                    {
                        if (GuardianAltarScriptReader.Integer(tokens[0]) <= 0)
                            throw new InvalidOperationException(
                                "Guardian Altar achievement clear count must be positive");
                    }
                    else if (string.Equals(tag, "icon image", StringComparison.OrdinalIgnoreCase))
                    {
                        if (GuardianAltarScriptReader.Integer(tokens[0]) < 0)
                            throw new InvalidOperationException(
                                "Guardian Altar achievement icon index must be non-negative");
                    }
                    else
                        GuardianAltarScriptReader.Quoted(tokens[0]);
                }
            }
            return ids.AsReadOnly();
        }

        private void ParseCore(
            string configContent, string shopContent, ScriptNode shopRoot)
        {
            var configTags = new HashSet<string>(
                new[] { "gage upgrade cost percent", "gage up rate", "daily refill ticket" },
                StringComparer.OrdinalIgnoreCase);
            if (Root.Children.Any(child => !configTags.Contains(child.Tag))
                || GuardianAltarScriptReader.Tokens(Root, configContent).Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar config contains unsupported data");
            foreach (var tag in configTags)
                GuardianAltarScriptReader.SingleChild(Root, tag, required: false);

            // The current resource closes this scalar with [/refill ticket].
            // Keep the resource unchanged; ScriptParser exposes the scalar.
            var refillNode = GuardianAltarScriptReader.SingleChild(
                Root, "daily refill ticket");
            GuardianAltarScriptReader.RequireLeaf(refillNode);
            var refill = GuardianAltarScriptReader.SingleInteger(
                refillNode, configContent);
            if (refill <= 0)
                throw new InvalidOperationException(
                    "daily refill ticket must be positive");

            var resetNode = GuardianAltarScriptReader.SingleChild(
                shopRoot, "star reset fee gold", required: false);
            if (shopRoot.Children.Any(child =>
                    !string.Equals(child.Tag, "star reset fee gold", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(child.Tag, "ridable id", StringComparison.OrdinalIgnoreCase))
                || GuardianAltarScriptReader.Tokens(shopRoot, shopContent).Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar shop contains unsupported data");
            if (resetNode != null)
            {
                GuardianAltarScriptReader.RequireClosed(resetNode);
                GuardianAltarScriptReader.RequireLeaf(resetNode);
                var resetTokens = GuardianAltarScriptReader.Tokens(
                    resetNode, shopContent);
                if (resetTokens.Count == 0 || resetTokens.Count % 2 != 0)
                    throw new InvalidOperationException(
                        "star reset fee gold requires count/gold pairs");
                var resetCounts = new HashSet<int>();
                for (var i = 0; i < resetTokens.Count; i += 2)
                {
                    var count = GuardianAltarScriptReader.Integer(resetTokens[i]);
                    var gold = GuardianAltarScriptReader.Integer(resetTokens[i + 1]);
                    if (count < 0 || gold < 0 || !resetCounts.Add(count))
                        throw new InvalidOperationException(
                            "star reset fee gold has an invalid or duplicate count");
                }
            }

            var ridableNodes = shopRoot.GetChildren("ridable id");
            if (ridableNodes.Count == 0)
                throw new InvalidOperationException(
                    "Guardian Altar shop has no ridable id");
            var seenRidables = new HashSet<int>();
            var ridables = new List<GuardianAltarShopRidable>();
            var diagnostics = new List<string>();
            foreach (var ridableNode in ridableNodes)
            {
                GuardianAltarScriptReader.RequireClosed(ridableNode);
                if (ridableNode.Children.Any(child =>
                        !string.Equals(child.Tag, "unit", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(child.Tag, "skill", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(child.Tag, "tower", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Guardian Altar ridable contains an unsupported category");
                var ridableId = GuardianAltarScriptReader.SingleInteger(
                    ridableNode, shopContent);
                if (ridableId <= 0 || !seenRidables.Add(ridableId))
                    throw new InvalidOperationException(
                        "Guardian Altar shop has an invalid or duplicate ridable id");

                var entries = new List<GuardianAltarShopEntry>();
                var categoryTags = new[] { "unit", "skill", "tower" };
                for (var shopType = 0; shopType < categoryTags.Length; ++shopType)
                {
                    var categoryNode = GuardianAltarScriptReader.SingleChild(
                        ridableNode, categoryTags[shopType]);
                    GuardianAltarScriptReader.RequireClosed(categoryNode);
                    if (GuardianAltarScriptReader.Tokens(
                            categoryNode, shopContent).Count != 0
                        || categoryNode.Children.Any(
                            child => !string.Equals(child.Tag, "id",
                                StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(
                            "Guardian Altar category contains unsupported data");

                    var entryNodes = categoryNode.GetChildren("id");
                    if (entryNodes.Count == 0)
                        throw new InvalidOperationException(
                            "Guardian Altar shop category has no entries");
                    var seenEntries = new HashSet<int>();
                    foreach (var entryNode in entryNodes)
                    {
                        GuardianAltarScriptReader.RequireClosed(entryNode);
                        var resourceId = GuardianAltarScriptReader.SingleInteger(
                            entryNode, shopContent);
                        if (resourceId <= 0 || !seenEntries.Add(resourceId))
                            throw new InvalidOperationException(
                                "Guardian Altar category has an invalid or duplicate id");
                        entries.Add(ParseEntry(
                            entryNode, shopContent, shopType, resourceId, diagnostics));
                    }
                }
                ridables.Add(new GuardianAltarShopRidable(ridableId, entries));
            }
            DailyRefillTicket = refill;
            Ridables = ridables.AsReadOnly();
            Diagnostics = diagnostics.AsReadOnly();
        }

        private static GuardianAltarShopEntry ParseEntry(
            ScriptNode node, string content, int shopType, int resourceId,
            List<string> diagnostics)
        {
            var supportedTags = new HashSet<string>(
                new[] { "unlock stage index", "is settable slot", "field name", "list" },
                StringComparer.OrdinalIgnoreCase);
            if (node.Children.Any(child => !supportedTags.Contains(child.Tag)))
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} has an unsupported tag");

            var unlockNode = GuardianAltarScriptReader.SingleChild(node, "unlock stage index");
            GuardianAltarScriptReader.RequireLeaf(unlockNode);
            var unlock = GuardianAltarScriptReader.SingleInteger(unlockNode, content);
            if (unlock < 0)
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} has an invalid unlock stage");

            // BuyUpgradeData's verified default is true. The resource overrides
            // passive skills explicitly; auto-deployment still accepts types 0/1 only.
            var settable = true;
            var settableNode = GuardianAltarScriptReader.SingleChild(
                node, "is settable slot", required: false);
            if (settableNode != null)
            {
                GuardianAltarScriptReader.RequireLeaf(settableNode);
                var value = GuardianAltarScriptReader.SingleInteger(
                    settableNode, content);
                if (value != 0 && value != 1)
                    throw new InvalidOperationException(
                        "is settable slot must be 0 or 1");
                settable = value == 1;
            }

            var fieldsNode = GuardianAltarScriptReader.SingleChild(node, "field name");
            var listNode = GuardianAltarScriptReader.SingleChild(node, "list");
            GuardianAltarScriptReader.RequireClosed(fieldsNode);
            GuardianAltarScriptReader.RequireClosed(listNode);
            GuardianAltarScriptReader.RequireLeaf(fieldsNode);
            GuardianAltarScriptReader.RequireLeaf(listNode);
            var rawFields = GuardianAltarScriptReader.Tokens(fieldsNode, content);
            if (rawFields.Count == 0)
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} has no field names");
            var fields = new List<string>();
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The verified shop field-type binding is a closed vocabulary;
            // an unrecognized column must not be consumed as an arbitrary integer.
            var supportedFields = new HashSet<string>(new[]
            {
                "level", "index", "price", "name", "cooltime", "need point",
                "explain", "type explain", "set slot", "image index", "hp", "dmg",
                "pic", "rcv", "sum", "mht", "rgn", "msd", "asd", "sdt", "fdt",
                "etc", "lrg", "spl", "etc2",
            }, StringComparer.OrdinalIgnoreCase);
            foreach (var rawField in rawFields)
            {
                var field = GuardianAltarScriptReader.Quoted(rawField).Trim();
                if (field.Length == 0 || !supportedFields.Contains(field)
                    || !fieldNames.Add(field))
                    throw new InvalidOperationException(
                        $"Guardian Altar entry {resourceId} has an unsupported or duplicate field name");
                fields.Add(field);
            }
            var levelColumn = Column(fields, "level", resourceId);
            var indexColumn = Column(fields, "index", resourceId);
            var priceColumn = Column(fields, "price", resourceId);
            var tokens = GuardianAltarScriptReader.Tokens(listNode, content);
            if (tokens.Count == 0 || tokens.Count % fields.Count != 0)
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} list width differs from field name");

            var levels = new List<GuardianAltarShopLevel>();
            var seenLevels = new HashSet<short>();
            for (var row = 0; row < tokens.Count; row += fields.Count)
            {
                for (var column = 0; column < fields.Count; ++column)
                {
                    var field = fields[column];
                    if (string.Equals(field, "name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(field, "explain", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(field, "type explain", StringComparison.OrdinalIgnoreCase))
                        GuardianAltarScriptReader.Quoted(tokens[row + column]);
                    else
                        GuardianAltarScriptReader.Integer(tokens[row + column]);
                }

                var level = GuardianAltarScriptReader.NonNegativeShort(
                    tokens[row + levelColumn]);
                var price = GuardianAltarScriptReader.NonNegativeShort(
                    tokens[row + priceColumn]);
                if (GuardianAltarScriptReader.Integer(tokens[row + indexColumn]) < 0)
                    throw new InvalidOperationException(
                        $"Guardian Altar entry {resourceId} index must be non-negative");
                if (!seenLevels.Add(level))
                {
                    if (level <= 1)
                        throw new InvalidOperationException(
                            $"Guardian Altar entry {resourceId} repeats initial level {level}");
                    // Current unit 7000 has two level-3 rows. Preserve the source
                    // order and report it; this reader does not enable upgrades.
                    diagnostics.Add(
                        $"Guardian Altar entry {resourceId} repeats catalog level {level}");
                }
                levels.Add(new GuardianAltarShopLevel(level, price));
            }
            if (!seenLevels.Contains(0) && !seenLevels.Contains(1))
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} has no initial level 0 or 1");

            return new GuardianAltarShopEntry(
                shopType, resourceId, unlock, settable, levels);
        }

        private static int Column(List<string> fields, string name, int resourceId)
        {
            var index = fields.FindIndex(
                field => string.Equals(field, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException(
                    $"Guardian Altar entry {resourceId} lacks required field {name}");
            return index;
        }
    }

    /// <summary>Strict data-token reads over the existing ScriptParser tree.</summary>
    internal static class GuardianAltarScriptReader
    {
        private static readonly Regex TokenRegex = new Regex(
            @"\G(?:\s+|#[^\r\n]*|(?<quoted>`[^`]*`)|(?<word>[^\s`#]+))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static ScriptNode SingleChild(
            ScriptNode parent, string tag, bool required = true)
        {
            var children = parent.GetChildren(tag);
            if (children.Count > 1 || (required && children.Count != 1))
                throw new InvalidOperationException(
                    $"Guardian Altar requires one [{tag}] tag");
            return children.Count == 0 ? null : children[0];
        }

        internal static void RequireClosed(ScriptNode node)
        {
            if (!node.HasEndTag)
                throw new InvalidOperationException(
                    $"Guardian Altar [{node.Tag}] lacks a closing tag");
        }

        internal static void RequireLeaf(ScriptNode node)
        {
            if (node.Children.Count != 0)
                throw new InvalidOperationException(
                    $"Guardian Altar [{node.Tag}] contains nested tags");
        }

        internal static List<string> Tokens(ScriptNode node, string content)
        {
            var result = new List<string>();
            foreach (var data in node.DataItems)
            {
                var text = data.GetContent(content);
                var offset = 0;
                while (offset < text.Length)
                {
                    var match = TokenRegex.Match(text, offset);
                    if (!match.Success || match.Index != offset)
                        throw new InvalidOperationException(
                            $"Guardian Altar [{node.Tag}] contains an invalid token");
                    if (match.Groups["quoted"].Success)
                        result.Add(match.Groups["quoted"].Value);
                    else if (match.Groups["word"].Success)
                        result.Add(match.Groups["word"].Value);
                    offset += match.Length;
                }
            }
            return result;
        }

        internal static int SingleInteger(ScriptNode node, string content)
        {
            var values = Tokens(node, content);
            if (values.Count != 1)
                throw new InvalidOperationException(
                    $"Guardian Altar [{node.Tag}] must contain one integer");
            return Integer(values[0]);
        }

        internal static int Integer(string token)
        {
            if (!int.TryParse(token, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException(
                    $"Guardian Altar integer token is invalid: {token}");
            return value;
        }

        internal static short NonNegativeShort(string token)
        {
            var value = Integer(token);
            if (value < 0 || value > short.MaxValue)
                throw new InvalidOperationException(
                    "Guardian Altar level/price exceeds its non-negative Int16 range");
            return (short)value;
        }

        internal static string Quoted(string token)
        {
            if (token.Length < 2 || token[0] != '`'
                || token[token.Length - 1] != '`')
                throw new InvalidOperationException(
                    "Guardian Altar string field must use backticks");
            return token.Substring(1, token.Length - 2);
        }
    }
}
