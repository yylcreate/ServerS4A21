using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvfLib
{
    public sealed class GuildContentsEntry
    {
        internal GuildContentsEntry(IReadOnlyList<int> columns, string text, string sourceText)
        {
            Columns = columns;
            Text = text;
            SourceText = sourceText;
        }

        public int Index => Columns[0];
        public int ContentType => Columns[1];
        public int ItemId => Columns[5];
        // Eight resource integers precede Text; additional contents have one
        // more integer after it. Unknown columns retain their exact values.
        public IReadOnlyList<int> Columns { get; }
        public string Text { get; }
        public string SourceText { get; }
    }

    public sealed class GuildContractReward
    {
        internal GuildContractReward(int key, int itemId, int count, string sourceText)
        {
            Key = key;
            ItemId = itemId;
            Count = count;
            SourceText = sourceText;
        }

        public int Key { get; }
        public int ItemId { get; }
        public int Count { get; }
        public string SourceText { get; }
    }

    public sealed class GuildStrikerPriceRow
    {
        internal GuildStrikerPriceRow(int key, int value, string sourceText)
        {
            Key = key;
            Value = value;
            SourceText = sourceText;
        }

        public int Key { get; }
        public int Value { get; }
        public string SourceText { get; }
    }

    public sealed class GuildStorageUpgradeRow
    {
        internal GuildStorageUpgradeRow(IReadOnlyList<int> columns, string sourceText)
        {
            Columns = columns;
            SourceText = sourceText;
        }

        // The five-integer resource layout does not establish which columns
        // authorize a capacity change, a payment or an upgrade stage.
        public IReadOnlyList<int> Columns { get; }
        public string SourceText { get; }
    }

    /// <summary>
    /// Immutable resource definitions from etc/(r)guild.etc, using the shared
    /// script reader. This model owns no archive/cache, guild entitlement,
    /// rental, daily counter, expiry, payment or reward delivery state. Unknown
    /// resource columns and unrelated tags remain available without inventing
    /// their business meaning. Missing scalar tags remain null rather than zero.
    /// </summary>
    public sealed class GuildAdditionalContentsConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuildContentsEntry> Contents { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildContentsEntry>());
        public IReadOnlyList<GuildContentsEntry> AdditionalContents { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildContentsEntry>());
        public IReadOnlyList<GuildContractReward> ContractRewards { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildContractReward>());
        public int? StrikerMaxRentCount { get; private set; }
        public int? StrikerMaxUseCount { get; private set; }
        public IReadOnlyList<GuildStrikerPriceRow> StrikerBuyerFees { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildStrikerPriceRow>());
        public IReadOnlyList<GuildStrikerPriceRow> StrikerSellerPrices { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildStrikerPriceRow>());
        public int? CargoSizeLimit { get; private set; }
        public int? DonationUnit { get; private set; }
        public IReadOnlyList<GuildStorageUpgradeRow> StorageUpgradeRows { get; private set; }
            = Array.AsReadOnly(Array.Empty<GuildStorageUpgradeRow>());

        private GuildAdditionalContentsConfigFile() { }

        public static GuildAdditionalContentsConfigFile Parse(string content)
        {
            var file = new GuildAdditionalContentsConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var root = ActivityConfigScriptReader.ReadRoot(content);
                var sections = new Dictionary<string, ScriptNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in root.Children)
                {
                    if (!IsSelectedTag(node.Tag))
                    {
                        RejectNestedSelectedTag(node);
                        continue;
                    }
                    if (!sections.TryAdd(node.Tag, node))
                        throw new InvalidOperationException("Guild resource field is duplicated: " + node.Tag);
                    if (!node.HasEndTag || node.Children.Count != 0)
                        throw new InvalidOperationException("Guild [" + node.Tag + "] must be a closed leaf.");
                }
                if (sections.Count == 0)
                    throw new InvalidOperationException("Guild resource has no supported definitions.");

                var contents = ReadContents(sections, "guild contents list", content, additional: false);
                var additionalContents = ReadContents(sections, "additional guild contents list", content, additional: true);
                var contracts = ReadContracts(sections, content);
                var maxRent = ReadNonnegativeScalar(sections, "guild striker max rent count", content);
                var maxUse = ReadNonnegativeScalar(sections, "guild striker max use count", content);
                var buyerFees = ReadPrices(sections, "guild striker buyer fee", content);
                var sellerPrices = ReadPrices(sections, "guild striker seller price", content);
                var cargoSize = ReadNonnegativeScalar(sections, "guild cargo size limit", content);
                var storageUpgrades = ReadStorageUpgrades(sections, content);
                var donationUnit = ReadDonationUnit(root, content);

                // Publish all tables together: a malformed later section cannot
                // leave earlier resource rows available for business operations.
                file.Root = root;
                file.Contents = contents;
                file.AdditionalContents = additionalContents;
                file.ContractRewards = contracts;
                file.StrikerMaxRentCount = maxRent;
                file.StrikerMaxUseCount = maxUse;
                file.StrikerBuyerFees = buyerFees;
                file.StrikerSellerPrices = sellerPrices;
                file.CargoSizeLimit = cargoSize;
                file.StorageUpgradeRows = storageUpgrades;
                file.DonationUnit = donationUnit;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        private static bool IsSelectedTag(string tag)
            => string.Equals(tag, "guild contents list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "additional guild contents list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild contracts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild striker max rent count", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild striker max use count", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild striker buyer fee", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild striker seller price", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild cargo size limit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "guild storage upgrade info", StringComparison.OrdinalIgnoreCase);

        private static int? ReadDonationUnit(ScriptNode root, string content)
        {
            ScriptNode found = null;
            void Visit(ScriptNode parent, bool underContribute)
            {
                foreach (var node in parent.Children)
                {
                    if (string.Equals(node.Tag, "guild donate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!underContribute || !parent.HasEndTag || found != null)
                            throw new InvalidOperationException("Guild donation unit must occur once under the root [guild contribute].");
                        found = node;
                    }
                    Visit(node, ReferenceEquals(parent, root)
                        && string.Equals(node.Tag, "guild contribute", StringComparison.OrdinalIgnoreCase));
                }
            }
            Visit(root, false);
            if (found == null) return null;
            var value = ActivityConfigScriptReader.SingleInteger(found, content);
            if (value <= 0) throw new InvalidOperationException("Guild donation unit must be positive.");
            return value;
        }

        private static void RejectNestedSelectedTag(ScriptNode node)
        {
            foreach (var child in node.Children)
            {
                if (IsSelectedTag(child.Tag))
                    throw new InvalidOperationException("Guild resource field must be at the root: " + child.Tag);
                RejectNestedSelectedTag(child);
            }
        }

        private static IReadOnlyList<GuildContentsEntry> ReadContents(
            IDictionary<string, ScriptNode> sections, string tag, string content, bool additional)
        {
            var rows = new List<GuildContentsEntry>();
            if (!sections.TryGetValue(tag, out var node)) return rows.AsReadOnly();
            var tokens = ReadTokens(node, content);
            var width = additional ? 10 : 9;
            RequireRows(tokens, width, tag);
            var indices = new HashSet<int>();
            for (var start = 0; start < tokens.Count; start += width)
            {
                var columns = new int[additional ? 9 : 8];
                for (var column = 0; column < 8; column++)
                    columns[column] = ReadInteger(tokens[start + column], tag);
                if (!tokens[start + 8].Quoted)
                    throw new InvalidOperationException("Guild [" + tag + "] requires a backtick text after eight integers.");
                if (additional) columns[8] = ReadInteger(tokens[start + 9], tag);
                if (columns[0] <= 0 || columns[1] <= 0 || columns[5] <= 0 || !indices.Add(columns[0]))
                    throw new InvalidOperationException("Guild contents index/type/item must be positive; indices must be unique within their table.");
                rows.Add(new GuildContentsEntry(Array.AsReadOnly(columns), tokens[start + 8].Value,
                    SourceRow(content, tokens, start, width)));
            }
            return rows.AsReadOnly();
        }

        private static IReadOnlyList<GuildContractReward> ReadContracts(
            IDictionary<string, ScriptNode> sections, string content)
        {
            var rows = new List<GuildContractReward>();
            if (!sections.TryGetValue("guild contracts", out var node)) return rows.AsReadOnly();
            var tokens = ReadTokens(node, content);
            RequireRows(tokens, 3, node.Tag);
            var keys = new HashSet<int>();
            for (var start = 0; start < tokens.Count; start += 3)
            {
                var key = ReadInteger(tokens[start], node.Tag);
                var itemId = ReadInteger(tokens[start + 1], node.Tag);
                var count = ReadInteger(tokens[start + 2], node.Tag);
                if (key <= 0 || itemId <= 0 || count <= 0 || !keys.Add(key))
                    throw new InvalidOperationException("Guild contracts require positive unique keys and positive item/count pairs.");
                rows.Add(new GuildContractReward(key, itemId, count, SourceRow(content, tokens, start, 3)));
            }
            return rows.AsReadOnly();
        }

        private static IReadOnlyList<GuildStrikerPriceRow> ReadPrices(
            IDictionary<string, ScriptNode> sections, string tag, string content)
        {
            var rows = new List<GuildStrikerPriceRow>();
            if (!sections.TryGetValue(tag, out var node)) return rows.AsReadOnly();
            var tokens = ReadTokens(node, content);
            RequireRows(tokens, 2, tag);
            var keys = new HashSet<int>();
            for (var start = 0; start < tokens.Count; start += 2)
            {
                var key = ReadInteger(tokens[start], tag);
                var value = ReadInteger(tokens[start + 1], tag);
                if (key <= 0 || value < 0 || !keys.Add(key))
                    throw new InvalidOperationException("Guild striker price keys must be positive and unique; values must be nonnegative.");
                rows.Add(new GuildStrikerPriceRow(key, value, SourceRow(content, tokens, start, 2)));
            }
            return rows.AsReadOnly();
        }

        private static IReadOnlyList<GuildStorageUpgradeRow> ReadStorageUpgrades(
            IDictionary<string, ScriptNode> sections, string content)
        {
            var rows = new List<GuildStorageUpgradeRow>();
            if (!sections.TryGetValue("guild storage upgrade info", out var node)) return rows.AsReadOnly();
            var tokens = ReadTokens(node, content);
            RequireRows(tokens, 5, node.Tag);
            if (tokens.Count == 0)
                throw new InvalidOperationException("Guild storage upgrade info requires at least one complete resource row.");
            for (var start = 0; start < tokens.Count; start += 5)
            {
                var columns = new int[5];
                for (var column = 0; column < columns.Length; column++)
                    columns[column] = ReadInteger(tokens[start + column], node.Tag);
                rows.Add(new GuildStorageUpgradeRow(Array.AsReadOnly(columns), SourceRow(content, tokens, start, 5)));
            }
            return rows.AsReadOnly();
        }

        private static int? ReadNonnegativeScalar(
            IDictionary<string, ScriptNode> sections, string tag, string content)
        {
            if (!sections.TryGetValue(tag, out var node)) return null;
            var value = ActivityConfigScriptReader.SingleInteger(node, content);
            if (value < 0) throw new InvalidOperationException("Guild [" + tag + "] must be nonnegative.");
            return value;
        }

        private static void RequireRows(IReadOnlyList<Token> tokens, int width, string tag)
        {
            if (tokens.Count % width != 0)
                throw new InvalidOperationException("Guild [" + tag + "] has an incomplete resource row.");
        }

        private static int ReadInteger(Token token, string tag)
        {
            if (token.Quoted || !int.TryParse(token.Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("Guild [" + tag + "] contains an invalid Int32: " + token.Value);
            return value;
        }

        private static string SourceRow(string content, IReadOnlyList<Token> tokens, int start, int width)
            => content.Substring(tokens[start].Start, tokens[start + width - 1].End - tokens[start].Start);

        private static List<Token> ReadTokens(ScriptNode node, string content)
        {
            var start = content.IndexOf('\n', node.StartIndex);
            var closingEnd = node.EndIndex;
            if (closingEnd > 0 && content[closingEnd - 1] == '\n') closingEnd--;
            if (closingEnd > 0 && content[closingEnd - 1] == '\r') closingEnd--;
            var end = content.LastIndexOf('\n', closingEnd - 1) + 1;
            if (start < 0 || end <= start || end > content.Length)
                throw new InvalidOperationException("Guild resource has invalid source boundaries: " + node.Tag);
            var tokens = new List<Token>();
            for (var offset = start + 1; offset < end;)
            {
                if (char.IsWhiteSpace(content[offset])) { offset++; continue; }
                if (content[offset] == '#')
                {
                    while (offset < end && content[offset] != '\n') offset++;
                    continue;
                }
                var tokenStart = offset;
                var quoted = content[offset] == '`';
                if (quoted)
                {
                    var valueStart = ++offset;
                    while (offset < end && content[offset] != '`') offset++;
                    if (offset == end)
                        throw new InvalidOperationException("Guild [" + node.Tag + "] has an unclosed backtick string.");
                    var value = content.Substring(valueStart, offset - valueStart);
                    offset++;
                    if (offset < end && !char.IsWhiteSpace(content[offset]) && content[offset] != '#')
                        throw new InvalidOperationException("Guild resource tokens must be separated: " + node.Tag);
                    tokens.Add(new Token(value, true, tokenStart, offset));
                }
                else
                {
                    while (offset < end && !char.IsWhiteSpace(content[offset]) && content[offset] != '#') offset++;
                    tokens.Add(new Token(content.Substring(tokenStart, offset - tokenStart), false, tokenStart, offset));
                }
            }
            return tokens;
        }

        private readonly record struct Token(string Value, bool Quoted, int Start, int End);
    }
}
