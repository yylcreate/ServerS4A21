using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuildAdditionalContentsConfigSelfTest
    {
        private const string ResourcePath = "etc/(r)guild.etc";
        private const string BaseRow = "4 2 -1 1 0 10149665 1 1000 ``";
        private const string AdditionalRows = "22 20 -1 0 0 490001375 0 10000000 `` 1\n"
            + "23 21 300 1 0 490001373 0 3000000 `` 1\n"
            + "24 21 500 1 0 490001374 0 5000000 `` 1";
        private const string StorageRows = "8 1000000 2000 1800 8\n"
            + "16 1500000 3000 2700 16\n32 2300000 4000 4000 16";

        internal static int Run()
        {
            Console.WriteLine("=== GUILD_ADDITIONAL_CONTENTS_CONFIG selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + ": " + exception, false); }
            }

            Section("complete current guild resource", () => CheckCurrentResource(Check));
            Section("source rows, absent fields and immutable data", () => CheckPreservation(Check));
            Section("malformed resource rejects all typed tables", () => CheckMalformed(Check));
            Console.WriteLine($"GUILD_ADDITIONAL_CONTENTS_CONFIG assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckCurrentResource(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var content = archive.GetFileContent(ResourcePath);
            if (string.IsNullOrWhiteSpace(content)) throw new FileNotFoundException(ResourcePath);
            var config = GuildAdditionalContentsConfigFile.Parse(content);
            check("the complete current ETC is valid", config.IsValid && config.InvalidReason.Length == 0);
            if (!config.IsValid) throw new InvalidDataException(config.InvalidReason);

            check("the current two contents tables preserve their complete source order",
                config.Contents.Count == 16
                && config.Contents.Select(row => row.Index).SequenceEqual(Enumerable.Range(1, 16))
                && config.AdditionalContents.Select(row => row.Index).SequenceEqual(new[] { 22, 23, 24 }));
            check("base support products preserve signed and unknown columns without inventing expiry",
                config.Contents.Where(row => row.ContentType == 2).Select(row => row.Index)
                    .SequenceEqual(new[] { 4, 7, 10 })
                && config.Contents.Single(row => row.Index == 4).Columns
                    .SequenceEqual(new[] { 4, 2, -1, 1, 0, 10149665, 1, 1000 })
                && config.Contents.Single(row => row.Index == 10).Text == "hot");
            check("the current additional product rows retain all nine integers and empty text",
                config.AdditionalContents[0].Columns.SequenceEqual(new[] { 22, 20, -1, 0, 0, 490001375, 0, 10000000, 1 })
                && config.AdditionalContents[1].Columns.SequenceEqual(new[] { 23, 21, 300, 1, 0, 490001373, 0, 3000000, 1 })
                && config.AdditionalContents[2].Columns.SequenceEqual(new[] { 24, 21, 500, 1, 0, 490001374, 0, 5000000, 1 })
                && config.AdditionalContents.All(row => row.Text == ""));
            check("the current contract reward is exactly the configured item/count triple",
                config.ContractRewards.Count == 1 && config.ContractRewards[0].Key == 21
                && config.ContractRewards[0].ItemId == 490001385 && config.ContractRewards[0].Count == 1);
            check("the two source counts remain independent resource values without a reset policy",
                config.StrikerMaxRentCount == 100 && config.StrikerMaxUseCount == 25);
            check("both complete striker price tables retain all fifty source keys",
                config.StrikerBuyerFees.Count == 50 && config.StrikerSellerPrices.Count == 50
                && config.StrikerBuyerFees.Select(row => row.Key).SequenceEqual(Enumerable.Range(50, 50))
                && config.StrikerSellerPrices.Select(row => row.Key).SequenceEqual(Enumerable.Range(50, 50))
                && config.StrikerBuyerFees.All(row => row.Value == 25000)
                && config.StrikerSellerPrices.All(row => row.Value == 0));
            check("cargo limit and the three complete raw upgrade rows come from this same definition",
                config.CargoSizeLimit == 56 && config.StorageUpgradeRows.Count == 3
                && config.StorageUpgradeRows[0].Columns.SequenceEqual(new[] { 8, 1000000, 2000, 1800, 8 })
                && config.StorageUpgradeRows[1].Columns.SequenceEqual(new[] { 16, 1500000, 3000, 2700, 16 })
                && config.StorageUpgradeRows[2].Columns.SequenceEqual(new[] { 32, 2300000, 4000, 4000, 16 }));
            check("every resource row retains an exact unmodified source substring",
                config.Contents.All(row => row.SourceText.Length > 0 && content.Contains(row.SourceText, StringComparison.Ordinal))
                && config.AdditionalContents.All(row => content.Contains(row.SourceText, StringComparison.Ordinal))
                && config.ContractRewards.All(row => content.Contains(row.SourceText, StringComparison.Ordinal))
                && config.StrikerBuyerFees.All(row => content.Contains(row.SourceText, StringComparison.Ordinal))
                && config.StrikerSellerPrices.All(row => content.Contains(row.SourceText, StringComparison.Ordinal))
                && config.StorageUpgradeRows.All(row => content.Contains(row.SourceText, StringComparison.Ordinal)));
            check("unrelated source sections remain available without being consumed as entitlement",
                config.Content == content && config.Root.GetChild("guild contribute") != null
                && config.Root.GetChild("guild contracts") != null);
        }

        private static void CheckPreservation(Action<string, bool> check)
        {
            var source = Fixture();
            var config = GuildAdditionalContentsConfigFile.Parse(source);
            check("complete synthetic definitions retain raw rows and signed columns",
                config.IsValid && config.Contents[0].Columns[2] == -1
                && config.Contents[0].SourceText == BaseRow
                && config.AdditionalContents.Count == 3 && config.StorageUpgradeRows.Count == 3
                && config.ContractRewards[0].SourceText == "21 490001385 1");
            var commentRow = "4 2 -1\r\n# row comment\r\n1 0 10149665 1 1000 `label # text`";
            var commented = GuildAdditionalContentsConfigFile.Parse(source.Replace(BaseRow, commentRow, StringComparison.Ordinal));
            check("rows may span lines and preserve comments plus hashes inside quoted text",
                commented.IsValid && commented.Contents[0].Text == "label # text"
                && commented.Contents[0].SourceText == commentRow
                && commented.Contents[0].Columns.SequenceEqual(config.Contents[0].Columns));
            var extension = "[future guild extension]\n[value]\n99\n[/future guild extension]\n";
            var extended = GuildAdditionalContentsConfigFile.Parse(source + extension);
            check("unknown extensions remain in original content and tree without altering selected rows",
                extended.IsValid && extended.Content == source + extension
                && extended.Root.GetChild("future guild extension")?.GetChild("value") != null
                && extended.ContractRewards[0].ItemId == 490001385);
            var absent = GuildAdditionalContentsConfigFile.Parse(Block("guild contracts", "21 490001385 1"));
            check("absent support and warehouse scalars remain null instead of granting default capacity or counts",
                absent.IsValid && absent.StrikerMaxRentCount == null && absent.StrikerMaxUseCount == null
                && absent.CargoSizeLimit == null && absent.Contents.Count == 0
                && absent.StrikerBuyerFees.Count == 0 && absent.StorageUpgradeRows.Count == 0);
            var zero = GuildAdditionalContentsConfigFile.Parse(Block("guild striker max use count", "0"));
            check("an explicit zero count remains distinct from a missing tag",
                zero.IsValid && zero.StrikerMaxUseCount == 0 && zero.StrikerMaxRentCount == null);
            var zeroCargo = GuildAdditionalContentsConfigFile.Parse(Block("guild cargo size limit", "0"));
            check("an explicit zero cargo limit is retained without substituting a usable capacity",
                zeroCargo.IsValid && zeroCargo.CargoSizeLimit == 0 && zeroCargo.StorageUpgradeRows.Count == 0);
            var empty = GuildAdditionalContentsConfigFile.Parse(Block("guild contracts", ""));
            check("an explicitly empty reward table creates no synthetic reward",
                empty.IsValid && empty.ContractRewards.Count == 0 && empty.HasTag("guild contracts"));
            var rawUpgrade = GuildAdditionalContentsConfigFile.Parse(Block("guild storage upgrade info", "8 -1 2000 -2 8"));
            check("unknown warehouse columns are preserved without inferring a payment or sentinel meaning",
                rawUpgrade.IsValid && rawUpgrade.StorageUpgradeRows[0].Columns.SequenceEqual(new[] { 8, -1, 2000, -2, 8 }));
            var independent = GuildAdditionalContentsConfigFile.Parse(source.Replace(BaseRow,
                "22 2 -1 1 0 10149665 1 1000 ``", StringComparison.Ordinal));
            check("contents tables retain separate source namespaces without inventing cross-table replacement",
                independent.IsValid && independent.Contents[0].Index == 22 && independent.AdditionalContents[0].Index == 22);

            check("all typed table collections reject mutation",
                RejectMutation((IList<GuildContentsEntry>)config.Contents)
                && RejectMutation((IList<GuildContentsEntry>)config.AdditionalContents)
                && RejectMutation((IList<GuildContractReward>)config.ContractRewards)
                && RejectMutation((IList<GuildStrikerPriceRow>)config.StrikerBuyerFees)
                && RejectMutation((IList<GuildStrikerPriceRow>)config.StrikerSellerPrices)
                && RejectMutation((IList<GuildStorageUpgradeRow>)config.StorageUpgradeRows));
            check("raw integer columns are also immutable",
                RejectMutation((IList<int>)config.Contents[0].Columns)
                && RejectMutation((IList<int>)config.AdditionalContents[0].Columns)
                && RejectMutation((IList<int>)config.StorageUpgradeRows[0].Columns));
            config.Root.Children.Clear();
            check("mutating the diagnostic tree cannot mutate frozen typed definitions",
                config.Contents[0].Columns.SequenceEqual(new[] { 4, 2, -1, 1, 0, 10149665, 1, 1000 })
                && config.ContractRewards[0].ItemId == 490001385 && config.CargoSizeLimit == 56);
        }

        private static void CheckMalformed(Action<string, bool> check)
        {
            var valid = Fixture();
            var cases = new (string Label, string Content)[]
            {
                ("null source", null), ("empty source", ""), ("whitespace source", "\r\n\t "),
                ("only unrelated source", "[unrelated]\n1\n"),
                ("unexpected root data", "99\n" + valid),
                ("quoted integer", valid.Replace("4 2 -1", "`4` 2 -1", StringComparison.Ordinal)),
                ("integer overflow", valid.Replace("3000000", "2147483648", StringComparison.Ordinal)),
                ("invalid integer", valid.Replace("490001373", "bad", StringComparison.Ordinal)),
                ("unquoted text", valid.Replace(BaseRow, "4 2 -1 1 0 10149665 1 1000 hot", StringComparison.Ordinal)),
                ("unterminated quote", valid.Replace(BaseRow, "4 2 -1 1 0 10149665 1 1000 `hot", StringComparison.Ordinal)),
                ("adjacent quoted token", valid.Replace(BaseRow, "4 2 -1 1 0 10149665 1 1000 `hot`bad", StringComparison.Ordinal)),
                ("invalid positive item key", valid.Replace("490001373", "0", StringComparison.Ordinal)),
                ("incomplete additional row", valid.Replace("23 21 300 1 0 490001373 0 3000000 `` 1", "23 21 300 1 0 490001373 0 3000000 ``", StringComparison.Ordinal)),
                ("incomplete base row", valid.Replace(BaseRow, "4 2 -1 1 0 10149665 1 ``", StringComparison.Ordinal)),
                ("incomplete contract row", valid.Replace("21 490001385 1", "21 490001385", StringComparison.Ordinal)),
                ("invalid contract count", valid.Replace("21 490001385 1", "21 490001385 -1", StringComparison.Ordinal)),
                ("incomplete buyer pair", valid.Replace("50 25000 99 25000", "50 25000 99", StringComparison.Ordinal)),
                ("negative seller value", valid.Replace("50 0 99 0", "50 -1 99 0", StringComparison.Ordinal)),
                ("duplicate buyer key", valid.Replace("50 25000 99 25000", "50 25000 50 25000", StringComparison.Ordinal)),
                ("incomplete upgrade row", valid.Replace("32 2300000 4000 4000 16", "32 2300000 4000 4000", StringComparison.Ordinal)),
                ("extra upgrade integer", valid.Replace("32 2300000 4000 4000 16", "32 2300000 4000 4000 16 1", StringComparison.Ordinal)),
                ("empty upgrade table", valid.Replace(StorageRows, "", StringComparison.Ordinal)),
                ("quoted upgrade integer", valid.Replace("8 1000000", "8 `1000000`", StringComparison.Ordinal)),
                ("negative capacity", valid.Replace(Block("guild cargo size limit", "56"), Block("guild cargo size limit", "-1"), StringComparison.Ordinal)),
                ("extra scalar", valid.Replace(Block("guild striker max use count", "25"), Block("guild striker max use count", "25 1"), StringComparison.Ordinal)),
                ("duplicate scalar", valid + Block("guild striker max use count", "25")),
                ("duplicate contents block", valid + Block("additional guild contents list", AdditionalRows)),
                ("duplicate contents index", valid.Replace(AdditionalRows, AdditionalRows + "\n22 20 -1 0 0 490001375 0 10000000 `` 1", StringComparison.Ordinal)),
                ("duplicate contract key", valid.Replace("21 490001385 1", "21 490001385 1 21 490001385 1", StringComparison.Ordinal)),
                ("unclosed contents", valid.Replace("[/additional guild contents list]\n", "", StringComparison.Ordinal)),
                ("stray closing tag", valid + "[/unknown]\n"),
                ("nested selected field", valid + "[extension]\n" + Block("guild cargo size limit", "56") + "[/extension]\n"),
                ("nested contents data", valid.Replace(BaseRow, "[nested]\n" + BaseRow + "\n[/nested]", StringComparison.Ordinal)),
                ("malformed opening boundary", valid.Replace("[guild contracts]", "[guild contracts", StringComparison.Ordinal)),
            };
            foreach (var (label, content) in cases)
            {
                var config = GuildAdditionalContentsConfigFile.Parse(content);
                check(label + " rejects every typed table and scalar atomically",
                    !config.IsValid && !string.IsNullOrWhiteSpace(config.InvalidReason)
                    && config.Contents.Count == 0 && config.AdditionalContents.Count == 0
                    && config.ContractRewards.Count == 0 && config.StrikerBuyerFees.Count == 0
                    && config.StrikerSellerPrices.Count == 0 && config.StorageUpgradeRows.Count == 0
                    && config.StrikerMaxRentCount == null && config.StrikerMaxUseCount == null
                    && config.CargoSizeLimit == null && config.Root.Children.Count == 0);
            }
        }

        private static string Fixture()
            => Block("guild contents list", BaseRow)
                + Block("additional guild contents list", AdditionalRows)
                + Block("guild contracts", "21 490001385 1")
                + Block("guild striker max rent count", "100")
                + Block("guild striker max use count", "25")
                + Block("guild striker buyer fee", "50 25000 99 25000")
                + Block("guild striker seller price", "50 0 99 0")
                + Block("guild cargo size limit", "56")
                + Block("guild storage upgrade info", StorageRows);

        private static string Block(string tag, string values) => "[" + tag + "]\n" + values + "\n[/" + tag + "]\n";

        private static bool RejectMutation<T>(IList<T> values)
        {
            try { values.Add(default); return false; }
            catch (NotSupportedException) { return true; }
        }
    }
}
