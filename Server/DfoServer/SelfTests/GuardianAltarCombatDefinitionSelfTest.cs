using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarCombatDefinitionSelfTest
    {
        private const string ObjectRow = "1 `enemy` `normal` 64599 1 1 1682 265 0";
        private const string ActionRow = "1 1 0 6500 0";

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_COMBAT_DEFINITION selftest ===");
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
            Section("current PVF combat definitions", () => CheckRealResources(Check));
            Section("ordered objects, actions and map selections", () => CheckValidDefinitions(Check));
            Section("summon definitions fail closed", () => CheckInvalidSummons(Check));
            Section("map selections and summon areas fail closed", () => CheckInvalidMaps(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_COMBAT_DEFINITION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealResources(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var summon = GuardianAltarSummonDataFile.Parse(ReadRequired(archive,
                "etc/advancealtar/advancealtarsummondata.etc"));
            check("current summon ETC is valid and retains 20 objects, 63 actions and 116 action entries",
                summon.IsValid && summon.InvalidReason.Length == 0 && summon.Objects.Count == 20
                && summon.Actions.Count == 63 && summon.Actions.Sum(x => x.Entries.Count) == 116);
            if (!summon.IsValid) throw new InvalidDataException(summon.InvalidReason);
            check("current enemy source retains side, level, raw parameter and both spawn positions",
                summon.Objects.All(x => x.IsEnemy && x.Level == 1 && x.RawParameter == 1
                    && x.X == 1682 && (x.Y == 265 || x.Y == 270) && x.Z == 0)
                && summon.Objects.Select(x => x.Type).Distinct().OrderBy(x => x)
                    .SequenceEqual(new[] { "boss", "named", "normal" }));
            var first = summon.Actions.Single(x => x.Id == 1001);
            var second = summon.Actions.Single(x => x.Id == 1002);
            check("easy first stage references action 1001 with 10s first delay, 15s period and limit eight",
                Entries(first).SequenceEqual(new[] { (1, 10000, 15000, 8) }));
            check("easy second phase retains two separate object actions with one-second period and limit two",
                Entries(second).SequenceEqual(new[] { (1, 0, 1000, 2), (2, 0, 1000, 2) }));
            check("current action object references resolve and zero maximum remains unlimited source data",
                summon.Actions.All(x => x.Entries.All(y => summon.Objects.Any(z => z.Id == y.SummonObjectId)))
                && summon.Actions.SelectMany(x => x.Entries).Any(x => x.MaximumCount == 0));

            var registered = LstFile.Parse(ReadRequired(archive, "map/map.lst")).Entries
                .Where(x => x.FilePath.StartsWith("AdvanceAltar/", StringComparison.OrdinalIgnoreCase)).ToArray();
            check("formal MAP list contains all 51 Guardian resources",
                registered.Length == 51 && registered.Select(x => x.Id).OrderBy(x => x)
                    .SequenceEqual(Enumerable.Range(25081, 51)));
            var phaseCount = 0;
            var actionIds = summon.Actions.Select(x => x.Id).ToHashSet();
            foreach (var entry in registered)
            {
                var text = ReadRequired(archive, "map/" + entry.FilePath);
                var shared = MapFile.Parse(text);
                var root = shared.Root;
                var children = root.Children.ToArray();
                var monsters = shared.Monsters;
                var monsterRows = monsters.ToArray();
                var passiveObjects = shared.PassiveObjects;
                var passiveRows = passiveObjects.ToArray();
                var rawArea = shared.SummonStartArea;
                var typed = GuardianAltarMapFile.Parse(shared);
                check($"MAP {entry.Id} combat projection preserves shared nodes and ordinary object ownership",
                    typed.IsValid && ReferenceEquals(typed.Root, root) && ReferenceEquals(typed.Content, shared.Content)
                    && ReferenceEquals(shared.Root, root) && root.Children.SequenceEqual(children)
                    && ReferenceEquals(shared.Monsters, monsters) && monsters.SequenceEqual(monsterRows)
                    && ReferenceEquals(shared.PassiveObjects, passiveObjects) && passiveObjects.SequenceEqual(passiveRows)
                    && shared.SummonStartArea == rawArea);
                if (!typed.IsValid) throw new InvalidDataException(typed.InvalidReason);
                var area = typed.SummonStartArea;
                check($"MAP {entry.Id} retains the current scalar summon rectangle",
                    area != null && (area.X, area.Y, area.Width, area.Height) == (15, 260, 1, 1));
                var sourcePhases = root.GetChild("time line").GetChildren("phase");
                check($"MAP {entry.Id} preserves raw action triples and matching typed default selections",
                    typed.Phases.Count == sourcePhases.Count && typed.Phases.Select((phase, index) =>
                        phase.ActionAssignments.SequenceEqual(SourceIntegers(sourcePhases[index].GetChild("action assign"), text))
                        && phase.ActionSelections.Count == 1 && phase.ActionSelections[0].IsDefault
                        && phase.ActionSelections[0].ActionId == phase.ActionAssignments[0]
                        && actionIds.Contains(phase.ActionSelections[0].ActionId)).All(x => x));
                phaseCount += typed.Phases.Count;
            }
            check("all registered MAP timelines retain 325 resource phases", phaseCount == 325);
            check("combat definition validation does not modify the PVF archive", !archive.HasModifications);
        }

        private static void CheckValidDefinitions(Action<string, bool> check)
        {
            var source = Definition(ObjectRow + "\n2 `friend` `named` 2749171 65535 -7 -20 30 -1\n"
                + "3 `enemy` `boss` 64605 2 0 1 2 3", Action(ActionRow)
                + Action("2 2 500 500 32767\n1 1000 1500 2", "named")
                + Action("3 3 0 2147483647 0", "boss"));
            var parsed = GuardianAltarSummonDataFile.Parse(source);
            check("synthetic objects and repeated action nodes remain ordered and valid", parsed.IsValid
                && parsed.Objects.Select(x => x.Id).SequenceEqual(new[] { 1, 2, 3 })
                && parsed.Actions.Select(x => x.Id).SequenceEqual(new[] { 1, 2, 3 }));
            check("friend prototypes retain full Int32 identity, unsigned wire level and unknown raw parameter",
                parsed.IsValid && !parsed.Objects[1].IsEnemy && parsed.Objects[1].PrototypeIndex == 2749171
                && parsed.Objects[1].Level == 65535 && parsed.Objects[1].RawParameter == -7
                && (parsed.Objects[1].X, parsed.Objects[1].Y, parsed.Objects[1].Z) == (-20, 30, -1));
            check("action entry order, unlimited zero count and delay bounds are retained", parsed.IsValid
                && Entries(parsed.Actions[1]).SequenceEqual(new[] { (2, 500, 500, 32767), (1, 1000, 1500, 2) })
                && parsed.Actions[0].Entries[0].MaximumCount == 0
                && parsed.Actions[2].Entries[0].RepeatDelayMilliseconds == int.MaxValue);
            var commented = GuardianAltarSummonDataFile.Parse(Definition(
                ObjectRow + " # object\n", Action("# action\n1\t1 0\n6500 0")));
            check("resource comments and whitespace do not create extra object or action rows",
                commented.IsValid && commented.Objects.Count == 1 && commented.Actions.Count == 1);

            const string selections = "1 -1 -1 2 0 3 1 4 32767";
            var mapText = Map(selections, "-20 260 5 1");
            var shared = MapFile.Parse(mapText);
            var root = shared.Root;
            var map = GuardianAltarMapFile.Parse(shared);
            check("multiple action selection triples preserve raw order and shared Root/Content",
                map.IsValid && map.Phases[0].ActionAssignments.SequenceEqual(new[] { 1, -1, -1, 2, 0, 3, 1, 4, 32767 })
                && ReferenceEquals(map.Root, root) && ReferenceEquals(map.Content, shared.Content));
            check("action selections are alive ranges with a default, rather than difficulty positions",
                map.IsValid && map.Phases[0].ActionSelections.Select(x =>
                    (x.ActionId, x.MinimumAlive, x.MaximumAlive, x.IsDefault)).SequenceEqual(
                        new[] { (1, -1, -1, true), (2, 0, 3, false), (1, 4, 32767, false) }));
            check("scalar summon rectangle retains signed origin and positive dimensions", map.IsValid
                && map.SummonStartArea != null
                && (map.SummonStartArea.X, map.SummonStartArea.Y, map.SummonStartArea.Width, map.SummonStartArea.Height)
                    == (-20, 260, 5, 1));
            var missingArea = GuardianAltarMapFile.Parse(Map("1 -1 -1"));
            check("missing optional scalar rectangle remains absent for caller admission validation",
                missingArea.IsValid && missingArea.SummonStartArea == null);
        }

        private static void CheckInvalidSummons(Action<string, bool> check)
        {
            var valid = Definition(ObjectRow, Action(ActionRow));
            var fixtures = new (string Label, string Text)[]
            {
                ("null", null), ("empty", ""), ("whitespace", " \n\t"),
                ("missing objects", "[action define]\n" + Action(ActionRow) + "[/action define]\n"),
                ("missing actions", "[summon object]\n" + ObjectRow + "\n[/summon object]\n"),
                ("empty objects", Definition("", Action(ActionRow))),
                ("empty action list", Definition(ObjectRow, "")),
                ("duplicate object section", valid + "[summon object]\n" + ObjectRow + "\n[/summon object]\n"),
                ("duplicate action section", valid + "[action define]\n" + Action(ActionRow) + "[/action define]\n"),
                ("duplicate object ID", Definition(ObjectRow + "\n" + ObjectRow, Action(ActionRow))),
                ("zero object ID", valid.Replace(ObjectRow, "0 `enemy` `normal` 64599 1 1 1682 265 0")),
                ("negative prototype", valid.Replace("64599", "-1")),
                ("prototype overflow", valid.Replace("64599", "2147483648")),
                ("unknown side", valid.Replace("`enemy`", "`neutral`")),
                ("unquoted side", valid.Replace("`enemy`", "enemy")),
                ("unknown object type", valid.Replace("`normal`", "`elite`")),
                ("zero level", valid.Replace("64599 1 1", "64599 0 1")),
                ("negative level", valid.Replace("64599 1 1", "64599 -1 1")),
                ("wire level overflow", valid.Replace("64599 1 1", "64599 65536 1")),
                ("coordinate overflow", valid.Replace("1682", "2147483648")),
                ("decimal raw parameter", valid.Replace("64599 1 1", "64599 1 1.5")),
                ("partial object", Definition("1 `enemy` `normal` 64599 1 1 1682 265", Action(ActionRow))),
                ("malformed later object", Definition(ObjectRow + "\n2 `enemy` `normal` bad 1 1 1 2 3", Action(ActionRow))),
                ("nested object data", Definition("[nested]\n" + ObjectRow + "\n[/nested]", Action(ActionRow))),
                ("unclosed object section", valid.Replace("[/summon object]\n", "")),
                ("wrong object closing", valid.Replace("[/summon object]", "[/other]")),
                ("duplicate action ID", Definition(ObjectRow, Action(ActionRow) + Action(ActionRow, "boss"))),
                ("zero action ID", Definition(ObjectRow, Action("0 1 0 6500 0"))),
                ("action without entries", Definition(ObjectRow, Action("1"))),
                ("partial action entry", Definition(ObjectRow, Action("1 1 0 6500"))),
                ("unknown action object", Definition(ObjectRow, Action("1 2 0 6500 0"))),
                ("negative first delay", Definition(ObjectRow, Action("1 1 -1 6500 0"))),
                ("repeat below minimum", Definition(ObjectRow, Action("1 1 0 499 0"))),
                ("negative maximum", Definition(ObjectRow, Action("1 1 0 6500 -1"))),
                ("maximum count overflow", Definition(ObjectRow, Action("1 1 0 6500 32768"))),
                ("first delay integer overflow", Definition(ObjectRow, Action("1 1 2147483648 6500 0"))),
                ("unknown action tag", Definition(ObjectRow, Action(ActionRow, "random"))),
                ("nested action data", Definition(ObjectRow, Action("[nested]\n" + ActionRow + "\n[/nested]"))),
                ("action container direct data", Definition(ObjectRow, "1\n" + Action(ActionRow))),
                ("unclosed action", valid.Replace("[/normal]\n", "")),
                ("unclosed action container", valid.Replace("[/action define]\n", "")),
                ("stray closing within action", Definition(ObjectRow, Action(ActionRow + "\n[/other]"))),
                ("malformed late action", Definition(ObjectRow, Action(ActionRow) + Action("2 1 bad 1000 0"))),
                ("unsupported root field", valid + "[unexpected]\n0\n"),
                ("direct root data", "123\n" + valid)
            };
            foreach (var fixture in fixtures)
            {
                var failed = GuardianAltarSummonDataFile.Parse(fixture.Text);
                check(fixture.Label + " rejects without publishing partial objects/actions", !failed.IsValid
                    && !string.IsNullOrWhiteSpace(failed.InvalidReason) && failed.Objects.Count == 0 && failed.Actions.Count == 0);
            }
        }

        private static void CheckInvalidMaps(Action<string, bool> check)
        {
            foreach (var assignments in new[]
            {
                "", "1 -1", "1 -1 -1 2", "0 -1 -1", "-1 -1 -1", "bad -1 -1",
                "1 -2 -1", "1 0 -1", "1 9 3", "1 32768 32768", "1 0 2147483648",
                "1 -1 -1 2 0 bad"
            })
                CheckFailedMap(check, "invalid selection " + assignments, Map(assignments, "15 260 1 1"));
            foreach (var area in new[]
            {
                "", "15 260 1", "15 260 1 1 0", "15 260 0 1", "15 260 -1 1",
                "15 260 1 0", "15 bad 1 1", "15 260 1.5 1", "2147483647 260 1 1",
                "15 2147483647 1 1", "15 260 2147483648 1", "[nested]\n15 260 1 1\n[/nested]"
            })
                CheckFailedMap(check, "invalid summon area " + area, Map("1 -1 -1", area));
            CheckFailedMap(check, "duplicate summon area", Map("1 -1 -1", "15 260 1 1")
                + "[summon start area]\n15 260 1 1\n");
        }

        private static void CheckFailedMap(Action<string, bool> check, string label, string content)
        {
            var failed = GuardianAltarMapFile.Parse(content);
            check(label + " rejects without publishing partial map combat definition", !failed.IsValid
                && !string.IsNullOrWhiteSpace(failed.InvalidReason) && failed.CenterIndices.Count == 0
                && failed.Phases.Count == 0 && failed.TotalDurationMilliseconds == 0 && failed.SummonStartArea == null);
        }

        private static string Definition(string objects, string actions) =>
            "[summon object]\n" + objects + "\n[/summon object]\n[action define]\n" + actions + "[/action define]\n";

        private static string Action(string rows, string type = "normal") =>
            "[" + type + "]\n" + rows + "\n[/" + type + "]\n";

        private static string Map(string assignments, string area = null) =>
            "[center index]\n64597\n[/center index]\n[time line]\n[phase]\n"
            + "[duration]\n1000\n[/duration]\n[type]\n`normal`\n[/type]\n"
            + "[action assign]\n" + assignments + "\n[/action assign]\n[/phase]\n[/time line]\n"
            + (area == null ? "" : "[summon start area]\n" + area + "\n");

        private static IEnumerable<(int ObjectId, int FirstDelay, int RepeatDelay, int Maximum)> Entries(
            GuardianAltarSummonAction action) => action.Entries.Select(x =>
                (x.SummonObjectId, x.FirstDelayMilliseconds, x.RepeatDelayMilliseconds, x.MaximumCount));

        private static int[] SourceIntegers(ScriptNode node, string content) =>
            node.DataItems.SelectMany(x => x.GetContent(content).Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)).Select(x => x.Split('#')[0])
                .SelectMany(x => x.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(x => int.Parse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)).ToArray();

        private static string ReadRequired(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Required resource is empty: " + path);
            return content;
        }
    }
}
