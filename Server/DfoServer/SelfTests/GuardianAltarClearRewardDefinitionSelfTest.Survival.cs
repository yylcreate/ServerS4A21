using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarClearRewardDefinitionSelfTest
    {
        private static void CheckRealSurvivalRewards(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH before survival reward validation.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var registration = LstFile.Parse(ReadRequired(archive, "dungeon/dungeon.lst")).GetById(3523)
                ?? throw new InvalidDataException("Current survival dungeon registration is missing.");
            var content = ReadRequired(archive, "dungeon/" + registration.FilePath);
            var shared = DungeonFile.Parse(content);
            var original = CaptureTree(shared.Root);
            var typed = GuardianAltarClearRewardFile.ParseSurvival(shared);
            var direct = GuardianAltarClearRewardFile.ParseSurvival(content);
            var repeated = GuardianAltarClearRewardFile.ParseSurvival(shared);
            check("survival rewards project the same current shared tree without publishing ordinary difficulties",
                typed.IsValid && direct.IsValid && repeated.IsValid && typed.Difficulties.Count == 0
                && ReferenceEquals(typed.Root, shared.Root) && ReferenceEquals(typed.Content, shared.Content)
                && original.All(row => row.IsUnchanged())
                && SurvivalRewardRows(typed).SequenceEqual(SurvivalRewardRows(direct))
                && SurvivalRewardRows(typed).SequenceEqual(SurvivalRewardRows(repeated)));
            if (!typed.IsValid) throw new InvalidDataException(typed.InvalidReason);
            var expected = new (int First, int Last, int Urns, int Souls)[]
            {
                (1, 1, 0, 2), (2, 3, 1, 3), (4, 6, 2, 3), (7, 9, 2, 5),
                (10, 12, 2, 6), (13, 15, 2, 7), (16, 18, 3, 8), (19, 21, 3, 9),
                (22, 24, 3, 9), (25, 27, 3, 10), (28, 30, 3, 12), (31, 33, 3, 13),
                (34, 36, 4, 14), (37, 39, 4, 15), (40, 42, 4, 16), (43, 45, 5, 17),
                (46, 48, 5, 18), (49, 99, 6, 19),
            };
            check("current survival rewards retain all eighteen ordered inclusive ranges through round 99",
                typed.SurvivalRanges.Select(row => (row.FirstRound, row.LastRound))
                    .SequenceEqual(expected.Select(row => (row.First, row.Last))));
            foreach (var row in expected)
            {
                var expectedItems = new List<(string Kind, int Item, int Count)>();
                if (row.Urns > 0) expectedItems.Add(("normal", 2749213, row.Urns));
                expectedItems.Add(("normal", 2749211, row.Souls));
                expectedItems.Add(("normal", 10006059, 1));
                check($"round range {row.First}..{row.Last} resolves every completed round to its configured final reward",
                    Enumerable.Range(row.First, row.Last - row.First + 1).All(round =>
                        typed.GetRewards(round).Select(item => (item.Kind, item.ItemId, item.Count))
                            .SequenceEqual(expectedItems)));
            }
            check("zero cleared rounds grant no items; negative or beyond-resource results reject",
                typed.GetRewards(0).Count == 0 && SurvivalLookupRejects(typed, -1) && SurvivalLookupRejects(typed, 100));
            check("the configured capped range retains the same final reward from round 49 through 99",
                ReferenceEquals(typed.GetRewards(49), typed.GetRewards(99)));
            check("survival range and item lists are read-only and source archive stays unmodified",
                typed.SurvivalRanges is IList<GuardianAltarSurvivalClearReward> rows && rows.IsReadOnly
                && typed.SurvivalRanges.All(row => row.Items is IList<GuardianAltarClearRewardItem> items && items.IsReadOnly)
                && !archive.HasModifications);
        }

        private static void CheckSurvivalRewardValidation(Action<string, bool> check)
        {
            var valid = SurvivalReward(SurvivalRange("1 1", "`NORMAL` 7 1\n`normal` 7 2"),
                SurvivalRange("2 4", "`normal`\t8\n3 # source comment"));
            var typed = GuardianAltarClearRewardFile.ParseSurvival(valid);
            check("survival parser keeps repeated reward rows and inclusive range boundaries",
                typed.IsValid && typed.Difficulties.Count == 0 && SurvivalRewardRows(typed).SequenceEqual(new[]
                {
                    (1, 1, "normal", 7, 1), (1, 1, "normal", 7, 2), (2, 4, "normal", 8, 3),
                }) && typed.GetRewards(2).Single().ItemId == 8 && typed.GetRewards(4).Single().Count == 3);
            var unrelated = DungeonFile.Parse(valid + "[advance altar clear reward]\n[unrelated]\n0\n[/unrelated]\n"
                + "[/advance altar clear reward]\n");
            var tree = CaptureTree(unrelated.Root);
            check("explicit survival reward projection does not reinterpret unrelated ordinary reward fields",
                GuardianAltarClearRewardFile.ParseSurvival(unrelated).IsValid && tree.All(row => row.IsUnchanged()));
            foreach (var test in new (string Name, string Text)[]
            {
                ("null survival", null), ("empty survival", ""), ("missing section", "[name]\n`unknown`\n"),
                ("empty section", SurvivalReward()), ("duplicate section", valid + valid),
                ("missing first round", SurvivalReward(SurvivalRange("2 4", Row))),
                ("zero range", SurvivalReward(SurvivalRange("0 1", Row))),
                ("negative range", SurvivalReward(SurvivalRange("-1 1", Row))),
                ("reversed later range", SurvivalReward(SurvivalRange("1 1", Row), SurvivalRange("2 1", Row))),
                ("overlap", SurvivalReward(SurvivalRange("1 2", Row), SurvivalRange("2 4", Row))),
                ("gap", SurvivalReward(SurvivalRange("1 1", Row), SurvivalRange("3 4", Row))),
                ("duplicate range", SurvivalReward(SurvivalRange("1 1", Row), SurvivalRange("1 1", Row))),
                ("overflow range", SurvivalReward(SurvivalRange("1 2147483648", Row))),
                ("too few range values", SurvivalReward(SurvivalRange("1", Row))),
                ("too many range values", SurvivalReward(SurvivalRange("1 2 3", Row))),
                ("quoted range", SurvivalReward(SurvivalRange("`1` 1", Row))),
                ("unknown section child", valid.Replace("[type]", "[other]")),
                ("direct section data", valid.Replace("[advance altar survival clear reward]\n",
                    "[advance altar survival clear reward]\n1\n")),
                ("direct type data", valid.Replace("[type]\n", "[type]\n1\n")),
                ("extra field", valid.Replace("[/type]", "[other]\n1\n[/other]\n[/type]")),
                ("duplicate round", valid.Replace("[/round]", "[/round]\n[round]\n1 1\n[/round]")),
                ("duplicate list", valid.Replace("[/list]", "[/list]\n[list]\n" + Row + "\n[/list]")),
                ("unclosed section", valid.Replace("[/advance altar survival clear reward]\n", "")),
                ("unclosed type", valid.Replace("[/type]\n", "")),
                ("unclosed round", valid.Replace("[/round]\n", "")),
                ("unclosed list", valid.Replace("[/list]\n", "")),
                ("stray closing", valid.Replace("[/list]", "[/other]\n[/list]")),
                ("nested reward list", SurvivalReward(SurvivalRange("1 1", "[items]\n" + Row + "\n[/items]"))),
            })
                CheckSurvivalRewardFailed(check, test.Name, GuardianAltarClearRewardFile.ParseSurvival(test.Text));
            foreach (var row in new[] { "", "`normal` 1", "normal 1 1", "`equipment` 1 1", "`normal` 0 1",
                "`normal` 1 0", "`normal` -1 1", "`normal` 1 -1", "`normal` 1.5 1", "`normal` 1 1.5",
                "`normal` 2147483648 1", "`normal` 1 2147483648", Row + "\n`normal` 2 -1" })
                CheckSurvivalRewardFailed(check, "invalid later list " + row,
                    GuardianAltarClearRewardFile.ParseSurvival(SurvivalReward(SurvivalRange("1 1", Row),
                        SurvivalRange("2 3", row))));
            CheckSurvivalRewardFailed(check, "null shared survival", GuardianAltarClearRewardFile.ParseSurvival((DungeonFile)null));
            CheckSurvivalRewardFailed(check, "unparsed shared survival", GuardianAltarClearRewardFile.ParseSurvival(new DungeonFile()));
        }

        private static void CheckSurvivalRewardFailed(Action<string, bool> check, string label,
            GuardianAltarClearRewardFile file) => check(label + " rejects without partial reward data",
                !file.IsValid && !string.IsNullOrWhiteSpace(file.InvalidReason)
                && file.SurvivalRanges.Count == 0 && file.Difficulties.Count == 0);

        private static bool SurvivalLookupRejects(GuardianAltarClearRewardFile file, int round)
        {
            try { file.GetRewards(round); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
        }

        private static string SurvivalReward(params string[] ranges) =>
            "[advance altar survival clear reward]\n" + string.Concat(ranges) + "[/advance altar survival clear reward]\n";

        private static string SurvivalRange(string range, string items) =>
            "[type]\n[round]\n" + range + "\n[/round]\n[list]\n" + items + "\n[/list]\n[/type]\n";

        private static IEnumerable<(int First, int Last, string Kind, int Item, int Count)>
            SurvivalRewardRows(GuardianAltarClearRewardFile file) => file.SurvivalRanges.SelectMany(range =>
                range.Items.Select(item => (range.FirstRound, range.LastRound, item.Kind, item.ItemId, item.Count)));
    }
}
