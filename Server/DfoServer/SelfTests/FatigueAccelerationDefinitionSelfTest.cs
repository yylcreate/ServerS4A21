using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class FatigueAccelerationDefinitionSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== FATIGUE_ACCELERATION_DEFINITION selftest ===");
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
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("current PVF resources", () => CheckRealResources(Check));
            Section("exact costs and immutable exclusions", () => CheckValidDefinitions(Check));
            Section("malformed cost definitions", () => CheckInvalidCosts(Check));
            Section("malformed exclusion definitions", () => CheckInvalidDungeons(Check));
            Console.WriteLine($"FATIGUE_ACCELERATION_DEFINITION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealResources(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException(
                    "Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var costs = ReadRequired(archive, "event/chn_event/chn_fatiguespeedup.evt");
            var exclusions = ReadRequired(archive, "etc/chn_fatigueaccelchkdungeon.etc");
            var parsed = FatigueAccelerationConfigFile.Parse(costs, exclusions);
            check("current fatigue cost and exclusion resources form a valid definition",
                parsed.IsValid && parsed.InvalidReason.Length == 0
                && parsed.GoldCostsByLevel.Count > 0 && parsed.DisabledDungeonIds.Count > 0);
            if (!parsed.IsValid) throw new InvalidDataException(parsed.InvalidReason);
            check("current zero-cost rows are configured rather than missing",
                parsed.GoldCostsByLevel.Where(x => x.Value == 0).Any()
                && parsed.GoldCostsByLevel.Where(x => x.Value == 0)
                    .All(x => parsed.TryGetGoldCost(x.Key, out var value) && value == 0));
            check("current PVF leaves levels 85 and 86 unconfigured without extrapolation",
                !parsed.TryGetGoldCost(85, out _) && !parsed.TryGetGoldCost(86, out _));
            var registeredDungeonIds = LstFile.Parse(ReadRequired(archive,
                "dungeon/dungeon.lst")).Entries.Select(x => x.Id).ToHashSet();
            check("every current exclusion is a registered dungeon and is queried by membership",
                parsed.DisabledDungeonIds.All(x => registeredDungeonIds.Contains(x)
                    && parsed.IsDungeonDisabled(x)));
            check("definition retains source text without modifying the archive",
                parsed.Content == costs && parsed.DisabledDungeonContent == exclusions
                && !archive.HasModifications);
        }

        private static void CheckValidDefinitions(Action<string, bool> check)
        {
            var parsed = FatigueAccelerationConfigFile.Parse(
                Costs("3 0\n19 650\n84 12"), Dungeons("707\n501"));
            check("sparse level tables retain exact gold values, including a configured zero",
                parsed.IsValid && parsed.GoldCostsByLevel.Count == 3
                && parsed.TryGetGoldCost(3, out var free) && free == 0
                && parsed.TryGetGoldCost(19, out var paid) && paid == 650
                && parsed.TryGetGoldCost(84, out var final) && final == 12);
            check("missing, invalid and above-table levels never reuse a neighbouring fee",
                !parsed.TryGetGoldCost(0, out _) && !parsed.TryGetGoldCost(-1, out _)
                && !parsed.TryGetGoldCost(4, out _) && !parsed.TryGetGoldCost(85, out _)
                && !parsed.TryGetGoldCost(86, out _) && !parsed.TryGetGoldCost(int.MaxValue, out _));
            check("exclusions retain resource order without blocking unrelated dungeons",
                parsed.DisabledDungeonIds.SequenceEqual(new[] { 707, 501 })
                && parsed.IsDungeonDisabled(707) && parsed.IsDungeonDisabled(501)
                && !parsed.IsDungeonDisabled(808));
            var dictionary = (IDictionary<int, int>)parsed.GoldCostsByLevel;
            var dictionaryRejected = false;
            try { dictionary[3] = 999; }
            catch (NotSupportedException) { dictionaryRejected = true; }
            var list = (IList<int>)parsed.DisabledDungeonIds;
            var listRejected = false;
            try { list[0] = 808; }
            catch (NotSupportedException) { listRejected = true; }
            check("published cost and exclusion collections reject external mutation",
                dictionaryRejected && listRejected
                && parsed.TryGetGoldCost(3, out var unchanged) && unchanged == 0
                && parsed.IsDungeonDisabled(707) && !parsed.IsDungeonDisabled(808));
            var commented = FatigueAccelerationConfigFile.Parse(
                "# source\n" + Costs("# costs\n3\t0 # free\n19 650\n") + "# end\n",
                "# exclusions\n" + Dungeons("707 # blocked\n\t501"));
            check("comments and multiline whitespace preserve complete integer rows",
                commented.IsValid && commented.GoldCostsByLevel.Count == 2
                && commented.DisabledDungeonIds.SequenceEqual(new[] { 707, 501 }));
            var boundaries = FatigueAccelerationConfigFile.Parse(
                Costs("+1 +0\n2147483647 2147483647"), Dungeons("2147483647"));
            check("valid signed integer syntax and full non-negative Int32 values are retained",
                boundaries.IsValid && boundaries.TryGetGoldCost(1, out var zero) && zero == 0
                && boundaries.TryGetGoldCost(int.MaxValue, out var maximum) && maximum == int.MaxValue
                && boundaries.IsDungeonDisabled(int.MaxValue));
            var emptyExclusions = FatigueAccelerationConfigFile.Parse(Costs("3 0"), Dungeons(""));
            check("an explicitly empty exclusion table is valid without an invented fallback",
                emptyExclusions.IsValid && emptyExclusions.DisabledDungeonIds.Count == 0
                && !emptyExclusions.IsDungeonDisabled(707));
        }

        private static void CheckInvalidCosts(Action<string, bool> check)
        {
            var valid = Costs("3 0\n19 650");
            var cases = new (string Label, string Content)[]
            {
                ("null source", null), ("empty source", ""), ("whitespace source", "\t\n "),
                ("missing table", "[other]\n3 0\n[/other]\n"),
                ("empty table", Costs("")), ("partial row", Costs("3 0 19")),
                ("duplicate same-value level", Costs("3 0 3 0")),
                ("duplicate changed-value level", Costs("3 0 3 650")),
                ("zero level", Costs("0 0")), ("negative level", Costs("-3 0")),
                ("negative fee", Costs("3 -1")), ("decimal level", Costs("3.0 0")),
                ("decimal fee", Costs("3 1.5")), ("exponent fee", Costs("3 1e3")),
                ("hex fee", Costs("3 0x10")), ("quoted level", Costs("`3` 0")),
                ("quoted fee", Costs("3 `0`")), ("quoted space", Costs("`3 0`")),
                ("comma fee", Costs("3 1,000")), ("nonnumeric late row", Costs("3 0 19 bad")),
                ("level overflow", Costs("2147483648 0")),
                ("fee overflow", Costs("3 2147483648")),
                ("duplicate root table", valid + valid),
                ("nested table", Costs("[nested]\n3 0\n[/nested]")),
                ("unsupported rate tag", valid + "[experience rate]\n2\n[/experience rate]\n"),
                ("unclosed table", valid.Replace("[/level money]\n", "")),
                ("wrong closing", valid.Replace("[/level money]", "[/other]")),
                ("stray closing inside table", Costs("3 0\n[/other]")),
                ("stray closing before table", "[/other]\n" + valid),
                ("stray closing after table", valid + "[/other]\n"),
                ("data before table", "123\n" + valid),
                ("data after table", valid + "123\n"),
            };
            foreach (var candidate in cases)
                CheckInvalid(check, candidate.Label,
                    FatigueAccelerationConfigFile.Parse(candidate.Content, Dungeons("707")));
        }

        private static void CheckInvalidDungeons(Action<string, bool> check)
        {
            var valid = Dungeons("707");
            var cases = new (string Label, string Content)[]
            {
                ("null exclusion source", null), ("empty exclusion source", ""),
                ("missing exclusion table", "[other]\n707\n[/other]\n"),
                ("zero dungeon", Dungeons("0")), ("negative dungeon", Dungeons("-1")),
                ("duplicate dungeon", Dungeons("707 707")),
                ("nonnumeric late dungeon", Dungeons("707 bad")),
                ("decimal dungeon", Dungeons("707.0")),
                ("quoted dungeon", Dungeons("`707`")),
                ("dungeon overflow", Dungeons("2147483648")),
                ("duplicate exclusion table", valid + valid),
                ("nested exclusion", Dungeons("[nested]\n707\n[/nested]")),
                ("unclosed exclusion", valid.Replace("[/unable dungeon]\n", "")),
                ("wrong exclusion closing", valid.Replace("[/unable dungeon]", "[/other]")),
                ("stray closing inside exclusion", Dungeons("707\n[/other]")),
                ("stray closing before exclusion", "[/other]\n" + valid),
                ("stray closing after exclusion", valid + "[/other]\n"),
                ("direct exclusion root data", "123\n" + valid),
                ("unsupported exclusion root", valid + "[other]\n0\n[/other]\n"),
            };
            foreach (var candidate in cases)
                CheckInvalid(check, candidate.Label,
                    FatigueAccelerationConfigFile.Parse(Costs("3 0\n19 650"), candidate.Content));
        }

        private static void CheckInvalid(Action<string, bool> check, string label,
            FatigueAccelerationConfigFile parsed)
            => check(label + " rejects both tables without publishing partial rules",
                !parsed.IsValid && !string.IsNullOrWhiteSpace(parsed.InvalidReason)
                && parsed.GoldCostsByLevel.Count == 0 && parsed.DisabledDungeonIds.Count == 0
                && !parsed.TryGetGoldCost(3, out _) && !parsed.IsDungeonDisabled(707));

        private static string Costs(string values)
            => "[level money]\n" + values + "\n[/level money]\n";

        private static string Dungeons(string values)
            => "[unable dungeon]\n" + values + "\n[/unable dungeon]\n";

        private static string ReadRequired(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content)) throw new FileNotFoundException(path);
            return content;
        }
    }
}
