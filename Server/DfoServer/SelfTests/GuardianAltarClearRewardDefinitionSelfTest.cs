using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarClearRewardDefinitionSelfTest
    {
        private const string Row = "`normal` 2749211 4";

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_CLEAR_REWARD_DEFINITION selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("current formal PVF ordinary rewards", () => CheckRealResources(Check));
            Section("ordered immutable reward definitions", () => CheckValidDefinitions(Check));
            Section("malformed rewards publish no partial definitions", () => CheckInvalidDefinitions(Check));
            Section("current formal PVF survival interval rewards", () => CheckRealSurvivalRewards(Check));
            Section("survival interval ownership and malformed resources", () => CheckSurvivalRewardValidation(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_CLEAR_REWARD_DEFINITION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealResources(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var formal = LstFile.Parse(ReadRequired(archive, "dungeon/dungeon.lst")).Entries
                .Where(x => x.FilePath.StartsWith("AdvanceAltar/", StringComparison.OrdinalIgnoreCase)).ToArray();
            check("formal DGN list registers exactly the twelve ordinary stages and separate survival stage",
                formal.Length == 13 && formal.Select(x => x.Id).OrderBy(x => x)
                    .SequenceEqual(Enumerable.Range(3511, 13)));

            // Frozen current-PVF assertions only; these IDs/counts are not a
            // production classifier or a formula for generating later rewards.
            var expected = new Dictionary<int, (int[] Urns, int[] Souls)>
            {
                [3511] = (new[] { 0, 2, 3 }, new[] { 4, 7, 9 }),
                [3512] = (new[] { 1, 2, 3 }, new[] { 4, 7, 9 }),
                [3513] = (new[] { 1, 2, 3 }, new[] { 4, 7, 9 }),
                [3514] = (new[] { 1, 2, 3 }, new[] { 5, 8, 10 }),
                [3515] = (new[] { 2, 2, 3 }, new[] { 5, 8, 10 }),
                [3516] = (new[] { 2, 2, 3 }, new[] { 5, 8, 10 }),
                [3517] = (new[] { 2, 2, 3 }, new[] { 5, 8, 10 }),
                [3518] = (new[] { 2, 2, 3 }, new[] { 7, 9, 11 }),
                [3519] = (new[] { 2, 2, 3 }, new[] { 7, 9, 11 }),
                [3520] = (new[] { 2, 3, 3 }, new[] { 8, 9, 11 }),
                [3521] = (new[] { 2, 3, 3 }, new[] { 8, 9, 11 }),
                [3522] = (new[] { 2, 3, 4 }, new[] { 9, 10, 12 }),
            };
            var ordinaryIds = new List<int>();
            var itemIds = new HashSet<int>();
            var difficultyCount = 0;
            var rewardCount = 0;
            foreach (var entry in formal)
            {
                var content = ReadRequired(archive, "dungeon/" + entry.FilePath);
                var shared = DungeonFile.Parse(content);
                var dungeon = GuardianAltarDungeonFile.Parse(shared);
                if (!dungeon.IsValid) throw new InvalidDataException(dungeon.InvalidReason);
                if (dungeon.IsSurvival) continue;
                ordinaryIds.Add(entry.Id);
                var originalTree = CaptureTree(shared.Root);
                var raw = (shared.AdvanceAltarType, shared.AdvanceAltarMap, shared.AdvanceAltarClearReward,
                    shared.AdvanceAltarSurvivalMap, shared.AdvanceAltarSurvivalClearReward);
                var typed = GuardianAltarClearRewardFile.Parse(shared);
                var direct = GuardianAltarClearRewardFile.Parse(content);
                var repeated = GuardianAltarClearRewardFile.Parse(shared);
                check($"DGN {entry.Id} validates direct, shared and repeated ordinary reward projections",
                    typed.IsValid && direct.IsValid && repeated.IsValid
                    && typed.InvalidReason.Length == 0 && direct.InvalidReason.Length == 0
                    && repeated.InvalidReason.Length == 0 && RewardRows(typed).SequenceEqual(RewardRows(direct))
                    && RewardRows(typed).SequenceEqual(RewardRows(repeated)));
                if (!typed.IsValid) throw new InvalidDataException(typed.InvalidReason);
                check($"DGN {entry.Id} preserves shared Root, Content, every node and raw Guardian fields",
                    ReferenceEquals(typed.Root, shared.Root) && ReferenceEquals(typed.Content, shared.Content)
                    && ReferenceEquals(repeated.Root, shared.Root) && ReferenceEquals(repeated.Content, shared.Content)
                    && originalTree.All(x => x.IsUnchanged())
                    && raw == (shared.AdvanceAltarType, shared.AdvanceAltarMap, shared.AdvanceAltarClearReward,
                        shared.AdvanceAltarSurvivalMap, shared.AdvanceAltarSurvivalClearReward));
                check($"DGN {entry.Id} retains all three difficulty names in resource order",
                    typed.Difficulties.Select(x => x.DifficultyName).SequenceEqual(new[] { "easy", "medium", "hard" }));
                var stageExpected = expected[entry.Id];
                for (var index = 0; index < typed.Difficulties.Count; index++)
                {
                    var difficulty = typed.Difficulties[index];
                    var expectedItems = new List<(string Kind, int ItemId, int Count)>();
                    if (stageExpected.Urns[index] != 0)
                        expectedItems.Add(("normal", 2749213, stageExpected.Urns[index]));
                    expectedItems.Add(("normal", 2749211, stageExpected.Souls[index]));
                    expectedItems.Add(("normal", 10006059, 1));
                    check($"DGN {entry.Id} {difficulty.DifficultyName} preserves every configured item/count/order",
                        difficulty.Items.Select(x => (x.Kind, x.ItemId, x.Count)).SequenceEqual(expectedItems));
                    foreach (var item in difficulty.Items) itemIds.Add(item.ItemId);
                    rewardCount += difficulty.Items.Count;
                }
                difficultyCount += typed.Difficulties.Count;
            }
            check("all twelve formal ordinary stages retain 36 difficulty lists and 107 reward triples",
                ordinaryIds.OrderBy(x => x).SequenceEqual(Enumerable.Range(3511, 12))
                && difficultyCount == 36 && rewardCount == 107
                && itemIds.OrderBy(x => x).SequenceEqual(new[] { 2749211, 2749213, 10006059 }));
            var itemRegistrations = LstFile.Parse(ReadRequired(archive, "stackable/stackable.lst")).Entries
                .Select(x => (x.Id, Path: "stackable/" + x.FilePath))
                .Concat(LstFile.Parse(ReadRequired(archive, "equipment/equipment.lst")).Entries
                    .Select(x => (x.Id, Path: "equipment/" + x.FilePath))).ToLookup(x => x.Id);
            foreach (var itemId in itemIds)
            {
                var rows = itemRegistrations[itemId].ToArray();
                check($"ordinary reward template {itemId} has one readable formal item registration",
                    rows.Length == 1 && !string.IsNullOrWhiteSpace(archive.GetFileContent(rows[0].Path)));
            }
            check("reward validation leaves the archive read-only", !archive.HasModifications);
        }

        private static void CheckValidDefinitions(Action<string, bool> check)
        {
            var text = Reward(Difficulty("hard", "`NORMAL` 7 1\n`normal` 7 2"),
                Difficulty("easy", "`normal`\t8\n3 # configured count"),
                Difficulty("medium", "`normal` 2147483647 2147483647"));
            var typed = GuardianAltarClearRewardFile.Parse(text);
            check("difficulty source order, repeated item rows, comments and positive Int32 limits remain valid",
                typed.IsValid && typed.InvalidReason.Length == 0 && RewardRows(typed).SequenceEqual(new[]
                {
                    ("hard", "normal", 7, 1), ("hard", "normal", 7, 2),
                    ("easy", "normal", 8, 3), ("medium", "normal", int.MaxValue, int.MaxValue),
                }));
            check("published difficulty and item lists cannot be mutated",
                typed.Difficulties is IList<GuardianAltarDifficultyClearReward> difficulties && difficulties.IsReadOnly
                && typed.Difficulties.All(x => x.Items is IList<GuardianAltarClearRewardItem> items && items.IsReadOnly));
            var shared = DungeonFile.Parse("[name]\n`unchanged ordinary fields`\n" + text
                + "[advance altar survival clear reward]\n[unrelated]\n0\n[/unrelated]\n"
                + "[/advance altar survival clear reward]\n");
            var originalTree = CaptureTree(shared.Root);
            var originalContent = shared.Content;
            var projected = GuardianAltarClearRewardFile.Parse(shared);
            check("ordinary reward projection does not validate or reinterpret unrelated survival fields",
                projected.IsValid && ReferenceEquals(projected.Root, shared.Root)
                && ReferenceEquals(projected.Content, originalContent) && ReferenceEquals(shared.Content, originalContent)
                && originalTree.All(x => x.IsUnchanged()) && RewardRows(projected).SequenceEqual(RewardRows(typed)));
        }

        private static void CheckInvalidDefinitions(Action<string, bool> check)
        {
            var valid = Reward(Difficulty("easy", Row), Difficulty("medium", Row), Difficulty("hard", Row));
            var cases = new (string Name, string Text)[]
            {
                ("null text", null), ("empty text", string.Empty), ("whitespace", " \t\r\n"),
                ("missing reward tag", "[name]\n`ordinary dungeon`\n"),
                ("empty reward section", Reward()),
                ("missing difficulty", Reward(Difficulty("easy", Row), Difficulty("medium", Row))),
                ("duplicate reward section", valid + valid),
                ("duplicate difficulty", Reward(Difficulty("easy", Row), Difficulty("easy", Row), Difficulty("hard", Row))),
                ("case duplicate difficulty", Reward(Difficulty("easy", Row), Difficulty("EASY", Row), Difficulty("hard", Row))),
                ("unknown difficulty", Reward(Difficulty("easy", Row), Difficulty("medium", Row), Difficulty("king", Row))),
                ("extra difficulty", valid.Replace("[/advance altar clear reward]", Difficulty("expert", Row)
                    + "[/advance altar clear reward]")),
                ("direct section data", valid.Replace("[advance altar clear reward]\n", "[advance altar clear reward]\n1\n")),
                ("empty later difficulty", Reward(Difficulty("easy", Row), Difficulty("medium", Row), Difficulty("hard", ""))),
                ("unclosed section", valid.Replace("[/advance altar clear reward]\n", "")),
                ("wrong section closing", valid.Replace("[/advance altar clear reward]", "[/other]")),
                ("unclosed difficulty", valid.Replace("[/hard]\n", "")),
                ("mismatched difficulty closing", valid.Replace("[/hard]", "[/easy]")),
                ("stray closing inside section", valid.Replace("[/advance altar clear reward]", "[/other]\n[/advance altar clear reward]")),
                ("nested difficulty data", Reward(Difficulty("easy", Row), Difficulty("medium", Row),
                    Difficulty("hard", "[items]\n" + Row + "\n[/items]"))),
                ("nested unbalanced difficulty", Reward(Difficulty("easy", Row), Difficulty("medium", Row),
                    Difficulty("hard", "[items]\n" + Row))),
            };
            foreach (var test in cases)
                CheckFailed(check, test.Name, GuardianAltarClearRewardFile.Parse(test.Text));
            foreach (var row in new[]
            {
                "`normal`", "`normal` 1", Row + " `normal` 2", "`equipment` 1 1", "`gold` 1 1",
                "normal 1 1", "`normal 1 1", "`normal` 0 1", "`normal` -1 1", "`normal` 1 0",
                "`normal` 1 -1", "`normal` 1.5 1", "`normal` 1 1.5", "`normal` `1` 1",
                "`normal` 1 `1`", "`normal` bad 1", "`normal` 1 bad", "`normal` 2147483648 1",
                "`normal` 1 2147483648", Row + "\n`normal` 2 -1",
            })
                CheckFailed(check, "invalid last-difficulty row " + row,
                    GuardianAltarClearRewardFile.Parse(Reward(Difficulty("easy", Row),
                        Difficulty("medium", Row), Difficulty("hard", row))));
            CheckFailed(check, "null shared dungeon", GuardianAltarClearRewardFile.Parse((DungeonFile)null));
            CheckFailed(check, "unparsed shared dungeon", GuardianAltarClearRewardFile.Parse(new DungeonFile()));
            CheckFailed(check, "empty shared dungeon", GuardianAltarClearRewardFile.Parse(DungeonFile.Parse(string.Empty)));

            var shared = DungeonFile.Parse(Reward(Difficulty("easy", Row), Difficulty("medium", Row),
                Difficulty("hard", Row + "\n`normal` 2 0")));
            var originalTree = CaptureTree(shared.Root);
            var projected = GuardianAltarClearRewardFile.Parse(shared);
            var repeated = GuardianAltarClearRewardFile.Parse(shared);
            CheckFailed(check, "invalid shared later row", projected);
            CheckFailed(check, "repeated invalid shared later row", repeated);
            check("failed shared projections preserve the Root, Content and complete node tree",
                ReferenceEquals(projected.Root, shared.Root) && ReferenceEquals(projected.Content, shared.Content)
                && ReferenceEquals(repeated.Root, shared.Root) && ReferenceEquals(repeated.Content, shared.Content)
                && originalTree.All(x => x.IsUnchanged()));
        }

        private static void CheckFailed(Action<string, bool> check, string label,
            GuardianAltarClearRewardFile typed) => check(label + " rejects atomically without partial difficulty rows",
                !typed.IsValid && !string.IsNullOrWhiteSpace(typed.InvalidReason) && typed.Difficulties.Count == 0);

        private static string Reward(params string[] difficulties) =>
            "[advance altar clear reward]\n" + string.Concat(difficulties) + "[/advance altar clear reward]\n";

        private static string Difficulty(string name, string rows) =>
            "[" + name + "]\n" + rows + "\n[/" + name + "]\n";

        private static IEnumerable<(string Difficulty, string Kind, int ItemId, int Count)>
            RewardRows(GuardianAltarClearRewardFile file) => file.Difficulties.SelectMany(difficulty =>
                difficulty.Items.Select(item => (difficulty.DifficultyName, item.Kind, item.ItemId, item.Count)));

        private static string ReadRequired(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("Required Guardian Altar reward resource is empty: " + path);
            return content;
        }

        private static List<NodeSnapshot> CaptureTree(ScriptNode root)
        {
            var snapshots = new List<NodeSnapshot>();
            void Capture(ScriptNode node)
            {
                snapshots.Add(new NodeSnapshot(node));
                foreach (var child in node.Children) Capture(child);
            }
            Capture(root);
            return snapshots;
        }

        private sealed class NodeSnapshot
        {
            private readonly ScriptNode _node;
            private readonly string _tag;
            private readonly (int Start, int End, int StartLine, int EndLine, bool Closed) _positions;
            private readonly List<ScriptNode> _children;
            private readonly List<ScriptDataItem> _data;
            private readonly ScriptNode[] _childRows;
            private readonly (ScriptDataItem Item, int Start, int End, int Line)[] _dataRows;

            internal NodeSnapshot(ScriptNode node)
            {
                _node = node;
                _tag = node.Tag;
                _positions = (node.StartIndex, node.EndIndex, node.StartLineIndex, node.EndLineIndex, node.HasEndTag);
                _children = node.Children;
                _data = node.DataItems;
                _childRows = node.Children.ToArray();
                _dataRows = node.DataItems.Select(x => (x, x.StartIndex, x.EndIndex, x.LineIndex)).ToArray();
            }

            internal bool IsUnchanged() => ReferenceEquals(_node.Tag, _tag)
                && _positions == (_node.StartIndex, _node.EndIndex, _node.StartLineIndex, _node.EndLineIndex, _node.HasEndTag)
                && ReferenceEquals(_node.Children, _children) && ReferenceEquals(_node.DataItems, _data)
                && _node.Children.SequenceEqual(_childRows)
                && _node.DataItems.Select(x => (x, x.StartIndex, x.EndIndex, x.LineIndex)).SequenceEqual(_dataRows);
        }
    }
}
