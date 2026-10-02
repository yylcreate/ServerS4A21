using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarDungeonDefinitionSelfTest
    {
        private const string DungeonHeader = "[dungeon type]\n`advance altar`\n";
        private const string OrdinaryRows =
            "`easy` 100001 0\n`medium` 100002 10\n`hard` 100003 17\n";
        private const string Center = "[center index]\n64597\n[/center index]\n";

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_DUNGEON_DEFINITION selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }

            Section("current registered PVF resources", () => CheckRealResources(Check));
            Section("typed shared-root projections", () => CheckSharedProjections(Check));
            Section("DGN fail-closed validation", () => CheckInvalidDungeons(Check));
            Section("MAP fail-closed validation", () => CheckInvalidMaps(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_DUNGEON_DEFINITION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealResources(Action<string, bool> check)
        {
            // The required resource section must fail if the current archive is
            // missing. Synthetic fixtures are independent tests, not a fallback.
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException(
                    "Set PVF_ARCHIVE_PATH to the current PVF before running this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var stageList = LstFile.Parse(ReadRequired(archive, "stagemap/stagemap.lst"));
            var stageEntry = RegisteredEntry(stageList, 1, "ridable stage map");
            var stages = GuardianAltarStageMapFile.Parse(ReadRequired(archive, "stagemap/" + stageEntry.FilePath));
            check("current ridable stage map is valid", stages.IsValid && stages.InvalidReason.Length == 0);
            if (!stages.IsValid)
                throw new InvalidDataException(stages.InvalidReason);
            check("formal stage map exposes twelve ordinary DGN and one survival DGN only",
                stages.Stages.Where(x => !x.IsSurvival).Select(x => x.DungeonId)
                    .SequenceEqual(Enumerable.Range(3511, 12))
                && stages.Stages.Where(x => x.IsSurvival).Select(x => x.DungeonId)
                    .SequenceEqual(new[] { 3523 }));

            var dungeonList = LstFile.Parse(ReadRequired(archive, "dungeon/dungeon.lst"));
            var mapList = LstFile.Parse(ReadRequired(archive, "map/map.lst"));
            var referencedMaps = new HashSet<int>();
            var maps = new Dictionary<int, GuardianAltarMapFile>();
            var ordinaryMapCount = 0;
            var survivalMapCount = 0;
            foreach (var stage in stages.Stages)
            {
                var entry = RegisteredEntry(dungeonList, stage.DungeonId, "Guardian dungeon");
                var text = ReadRequired(archive, "dungeon/" + entry.FilePath);
                var shared = DungeonFile.Parse(text);
                var root = shared.Root;
                var advanceAltarType = shared.AdvanceAltarType;
                var advanceAltarMap = shared.AdvanceAltarMap;
                var typed = GuardianAltarDungeonFile.Parse(shared);
                var fromText = GuardianAltarDungeonFile.Parse(text);
                check($"DGN {stage.DungeonId} parses from shared and text resources",
                    typed.IsValid && fromText.IsValid && typed.InvalidReason.Length == 0);
                check($"DGN {stage.DungeonId} projection preserves shared root/content and ordinary fields",
                    ReferenceEquals(typed.Root, root) && ReferenceEquals(typed.Content, shared.Content)
                    && ReferenceEquals(shared.Root, root)
                    && shared.AdvanceAltarType == advanceAltarType && shared.AdvanceAltarMap == advanceAltarMap
                    && shared.DungeonType == "advance altar");
                if (!typed.IsValid || !fromText.IsValid)
                    throw new InvalidDataException(typed.InvalidReason + "; " + fromText.InvalidReason);
                check($"DGN {stage.DungeonId} normal/survival ownership matches the formal stage map",
                    typed.IsSurvival == stage.IsSurvival && SameDungeon(typed, fromText));

                if (stage.IsSurvival)
                {
                    var sourceIds = SourceIntegers(root.GetChild("advance altar survival map"), text);
                    check("survival DGN retains all 99 ordered map references, including repeats",
                        typed.Maps.Count == 0 && typed.SurvivalMapIds.Count == 99
                        && typed.SurvivalMapIds.SequenceEqual(sourceIds)
                        && typed.SurvivalMapIds.Distinct().OrderBy(x => x).SequenceEqual(Enumerable.Range(25117, 15)));
                    foreach (var mapId in typed.SurvivalMapIds)
                    {
                        if (!referencedMaps.Add(mapId))
                            continue;
                        maps.Add(mapId, CheckRealMap(check, archive, mapList, mapId));
                        survivalMapCount++;
                    }
                }
                else
                {
                    check($"DGN {stage.DungeonId} keeps three source difficulty rows and basis levels",
                        typed.Maps.Count == 3 && typed.SurvivalMapIds.Count == 0
                        && typed.Maps.Select(x => x.DifficultyName).SequenceEqual(new[] { "easy", "medium", "hard" })
                        && typed.Maps.All(x => x.MapBasisLevel >= 0)
                        && typed.Maps.Select(x => (x.DifficultyName, x.MapId, x.MapBasisLevel))
                            .SequenceEqual(SourceMapRows(root.GetChild("advance altar map"), text)));
                    check($"DGN {stage.DungeonId} resolves its own three MAP endpoints",
                        typed.Maps.Select(x => x.MapId)
                            .SequenceEqual(Enumerable.Range(25081 + (stage.DungeonId - 3511) * 3, 3)));
                    foreach (var map in typed.Maps)
                    {
                        check($"ordinary DGN {stage.DungeonId}/{map.DifficultyName} does not reuse another endpoint",
                            referencedMaps.Add(map.MapId));
                        if (!maps.ContainsKey(map.MapId))
                            maps.Add(map.MapId, CheckRealMap(check, archive, mapList, map.MapId));
                        ordinaryMapCount++;
                    }
                }
            }

            // Only LST-registered references reachable from the formal stage
            // map were read above. Do not scan orphan/test MAP or DGN files.
            check("the formal catalog resolves 36 ordinary and 15 distinct survival MAP resources",
                ordinaryMapCount == 36 && survivalMapCount == 15 && maps.Count == 51
                && referencedMaps.OrderBy(x => x).SequenceEqual(Enumerable.Range(25081, 51)));
            if (maps.TryGetValue(25081, out var easy))
                check("current first easy MAP has two phases totalling 145000 milliseconds",
                    easy.Phases.Count == 2 && easy.TotalDurationMilliseconds == 145000
                    && easy.Phases.Select(x => x.Type).SequenceEqual(new[] { "normal", "named" })
                    && easy.Phases.Select(x => x.DurationMilliseconds).SequenceEqual(new[] { 125000, 20000 }));
            else
                check("current first easy MAP is reachable", false);
            if (maps.TryGetValue(25082, out var medium))
                check("current first medium MAP has three ordered phases totalling 90000 milliseconds",
                    medium.Phases.Count == 3 && medium.TotalDurationMilliseconds == 90000
                    && medium.Phases.Select(x => x.Type).SequenceEqual(new[] { "normal", "normal", "named" })
                    && medium.Phases.Select(x => x.DurationMilliseconds).SequenceEqual(new[] { 60000, 25000, 5000 }));
            else
                check("current first medium MAP is reachable", false);
            check("read-only PVF validation does not modify the archive", !archive.HasModifications);
        }

        private static GuardianAltarMapFile CheckRealMap(Action<string, bool> check,
            PvfArchive archive, LstFile mapList, int mapId)
        {
            var entry = RegisteredEntry(mapList, mapId, "Guardian map");
            var text = ReadRequired(archive, "map/" + entry.FilePath);
            var shared = MapFile.Parse(text);
            var root = shared.Root;
            var monsters = shared.Monsters;
            var passives = shared.PassiveObjects;
            var monsterRows = monsters.ToArray();
            var passiveRows = passives.ToArray();
            var typed = GuardianAltarMapFile.Parse(shared);
            var fromText = GuardianAltarMapFile.Parse(text);
            check($"MAP {mapId} parses from the registered shared and text resources",
                typed.IsValid && fromText.IsValid && typed.InvalidReason.Length == 0);
            check($"MAP {mapId} projection preserves shared root/content and static object ownership",
                ReferenceEquals(typed.Root, root) && ReferenceEquals(typed.Content, shared.Content)
                && ReferenceEquals(shared.Root, root) && ReferenceEquals(shared.Monsters, monsters)
                && ReferenceEquals(shared.PassiveObjects, passives)
                && shared.Monsters.SequenceEqual(monsterRows) && shared.PassiveObjects.SequenceEqual(passiveRows));
            // All 51 current referenced MAPs omit [dungeon]. Their relation is
            // the registered DGN reference, not an invented MapFile.DungeonId.
            check($"MAP {mapId} has no resource dungeon tag and retains DungeonId=-1",
                root.GetChildren("dungeon").Count == 0 && shared.DungeonId == -1);
            if (!typed.IsValid || !fromText.IsValid)
                throw new InvalidDataException(typed.InvalidReason + "; " + fromText.InvalidReason);
            check($"MAP {mapId} keeps the source center index without treating it as a dungeon id",
                typed.CenterIndices.SequenceEqual(SourceIntegers(root.GetChild("center index"), text))
                && typed.CenterIndices.SequenceEqual(new[] { 64597 }));
            var sourcePhases = root.GetChild("time line").GetChildren("phase");
            check($"MAP {mapId} retains every timeline phase in source order",
                typed.Phases.Count == sourcePhases.Count && SameMap(typed, fromText));
            var cumulative = 0;
            for (var index = 0; index < sourcePhases.Count; index++)
            {
                var source = sourcePhases[index];
                var duration = SourceIntegers(source.GetChild("duration"), text).Single();
                cumulative = checked(cumulative + duration);
                var type = source.GetChild("type").GetFirstDataContent(text).Trim().Trim('`');
                var assignments = SourceIntegers(source.GetChild("action assign"), text);
                var phase = index < typed.Phases.Count ? typed.Phases[index] : null;
                check($"MAP {mapId} phase {index} retains duration/type/all three action positions",
                    phase != null && phase.DurationMilliseconds == duration && phase.Type == type
                    && phase.ActionAssignments.Count == 3 && phase.ActionAssignments.SequenceEqual(assignments));
            }
            check($"MAP {mapId} total duration equals checked cumulative source durations",
                cumulative > 0 && typed.TotalDurationMilliseconds == cumulative);
            return typed;
        }

        private static void CheckSharedProjections(Action<string, bool> check)
        {
            var text = OrdinaryDungeon();
            var sharedDungeon = DungeonFile.Parse(text);
            var dungeonRoot = sharedDungeon.Root;
            var dungeonChildren = dungeonRoot.Children.ToArray();
            var dungeon = GuardianAltarDungeonFile.Parse(sharedDungeon);
            check("ordinary typed projection keeps existing Root and Content references",
                dungeon.IsValid && ReferenceEquals(dungeon.Root, dungeonRoot)
                && ReferenceEquals(dungeon.Content, sharedDungeon.Content)
                && sharedDungeon.Root.Children.SequenceEqual(dungeonChildren));
            check("ordinary typed rows keep source difficulty, MAP and basis level values",
                dungeon.Maps.Select(x => x.DifficultyName).SequenceEqual(new[] { "easy", "medium", "hard" })
                && dungeon.Maps.Select(x => x.MapId).SequenceEqual(new[] { 100001, 100002, 100003 })
                && dungeon.Maps.Select(x => x.MapBasisLevel).SequenceEqual(new[] { 0, 10, 17 })
                && dungeon.SurvivalMapIds.Count == 0);
            check("explicit empty survival section is compatible with an ordinary PVF DGN",
                GuardianAltarDungeonFile.Parse(text + "[advance altar survival map]\n[/advance altar survival map]\n").IsValid);
            check("the same MAP may be referenced by distinct ordinary difficulties without fabricating ownership",
                GuardianAltarDungeonFile.Parse(OrdinaryDungeon(OrdinaryRows.Replace("100002", "100001"))).IsValid);
            var survival = GuardianAltarDungeonFile.Parse(SurvivalDungeon("100003 100001 100003 100002"));
            check("survival repeated MAP IDs and source ordering are retained, not deduplicated",
                survival.IsValid && survival.IsSurvival && survival.Maps.Count == 0
                && survival.SurvivalMapIds.SequenceEqual(new[] { 100003, 100001, 100003, 100002 }));
            check("explicit empty ordinary section is compatible with a survival PVF DGN",
                GuardianAltarDungeonFile.Parse(SurvivalDungeon("100001")
                    + "[advance altar map]\n[/advance altar map]\n").IsValid);

            var twoPhases = Timeline(Phase(1000, "normal", "7 -1 -1") + Phase(250, "named", "8 9 -1"));
            var sharedMap = MapFile.Parse(twoPhases);
            var mapRoot = sharedMap.Root;
            var mapChildren = mapRoot.Children.ToArray();
            var map = GuardianAltarMapFile.Parse(sharedMap);
            check("two-phase typed projection keeps Root/Content and checked cumulative total 1250",
                map.IsValid && ReferenceEquals(map.Root, mapRoot) && ReferenceEquals(map.Content, sharedMap.Content)
                && sharedMap.Root.Children.SequenceEqual(mapChildren)
                && map.Phases.Count == 2 && map.TotalDurationMilliseconds == 1250);
            check("two-phase typed projection preserves action assignment positions including -1 sentinels",
                map.IsValid && map.Phases[0].ActionAssignments.SequenceEqual(new[] { 7, -1, -1 })
                && map.Phases[1].ActionAssignments.SequenceEqual(new[] { 8, 9, -1 }));
            var three = GuardianAltarMapFile.Parse(Timeline(Phase(100, "normal")
                + Phase(200, "normal") + Phase(300, "boss")));
            check("three phases permit repeated phase types and retain checked total 600",
                three.IsValid && three.Phases.Count == 3 && three.TotalDurationMilliseconds == 600
                && three.Phases.Select(x => x.Type).SequenceEqual(new[] { "normal", "normal", "boss" }));
            var maximum = GuardianAltarMapFile.Parse(Timeline(Phase(int.MaxValue, "boss")));
            check("one positive Int32 maximum duration is retained without narrowing",
                maximum.IsValid && maximum.TotalDurationMilliseconds == int.MaxValue);
            check("a map without its own dungeon tag retains the ordinary parser's unknown owner",
                sharedMap.DungeonId == -1 && sharedMap.Root.GetChildren("dungeon").Count == 0);
            var ownedMap = MapFile.Parse("[dungeon]\n700009\n" + twoPhases);
            var ownedProjection = GuardianAltarMapFile.Parse(ownedMap);
            check("an explicit ordinary MAP dungeon owner is preserved rather than overwritten by a center index",
                ownedProjection.IsValid && ownedMap.DungeonId == 700009
                && ReferenceEquals(ownedProjection.Root, ownedMap.Root)
                && ownedProjection.CenterIndices.SequenceEqual(new[] { 64597 }));
            check("ordinary DGN and MAP fields remain outside the Guardian resource namespace",
                GuardianAltarDungeonFile.Parse("[name]\n`ordinary metadata`\n" + text).IsValid
                && GuardianAltarMapFile.Parse("[map name]\n`ordinary metadata`\n" + twoPhases).IsValid);
        }

        private static void CheckInvalidDungeons(Action<string, bool> check)
        {
            var ordinary = OrdinaryDungeon();
            foreach (var fixture in new (string Label, string Content)[]
            {
                ("null DGN", null), ("empty DGN", ""), ("whitespace DGN", " \n\t"),
                ("missing dungeon type", ordinary.Replace(DungeonHeader, "")),
                ("duplicate dungeon type", DungeonHeader + ordinary),
                ("wrong dungeon type", ordinary.Replace("`advance altar`", "`normal`")),
                ("unquoted dungeon type", ordinary.Replace("`advance altar`", "advance altar")),
                ("missing stage type", DungeonHeader + MapTable(OrdinaryRows)),
                ("duplicate stage type", ordinary + "[advance altar type]\n`normal`\n"),
                ("unknown stage type", ordinary.Replace("`normal`", "`unknown`")),
                ("unquoted stage type", ordinary.Replace("`normal`", "normal")),
                ("multiple stage type values", ordinary.Replace("`normal`", "`normal` `survival`")),
                ("missing normal map table", DungeonHeader + "[advance altar type]\n`normal`\n"),
                ("empty normal map table", OrdinaryDungeon("")),
                ("duplicate normal map table", ordinary + MapTable(OrdinaryRows)),
                ("missing hard difficulty", OrdinaryDungeon("`easy` 1 0\n`medium` 2 0\n")),
                ("unknown difficulty", ordinary.Replace("`hard`", "`king`")),
                ("duplicate difficulty", ordinary.Replace("`hard`", "`easy`")),
                ("unquoted difficulty", ordinary.Replace("`easy`", "easy")),
                ("wrong map row width", OrdinaryDungeon("`easy` 1\n`medium` 2 0\n`hard` 3 0\n")),
                ("zero MAP id", ordinary.Replace("100001", "0")),
                ("negative MAP id", ordinary.Replace("100001", "-1")),
                ("invalid MAP id", ordinary.Replace("100001", "one")),
                ("overflowing MAP id", ordinary.Replace("100001", "2147483648")),
                ("negative basis level", ordinary.Replace("100002 10", "100002 -1")),
                ("invalid basis level", ordinary.Replace("100002 10", "100002 ten")),
                ("unclosed map table", ordinary.Replace("[/advance altar map]\n", "")),
                ("wrong closing map table", ordinary.Replace("[/advance altar map]", "[/advance altar survival map]")),
                ("isolated wrong closing inside map table", ordinary.Replace("`easy`", "[/unexpected]\n`easy`")),
                ("nested map table tag", ordinary.Replace("`easy`", "[unexpected]\n1\n[/unexpected]\n`easy`")),
                ("unsupported Guardian root tag", ordinary + "[advance altar typo]\n0\n"),
                ("ordinary DGN with survival maps", ordinary + "[advance altar survival map]\n1\n[/advance altar survival map]\n"),
                ("missing survival map list", DungeonHeader + "[advance altar type]\n`survival`\n"),
                ("empty survival map list", SurvivalDungeon("")),
                ("duplicate survival map list", SurvivalDungeon("1") + "[advance altar survival map]\n2\n[/advance altar survival map]\n"),
                ("survival with ordinary difficulties", SurvivalDungeon("1") + MapTable(OrdinaryRows)),
                ("zero survival MAP", SurvivalDungeon("1 0 2")),
                ("negative survival MAP", SurvivalDungeon("1 -1 2")),
                ("invalid survival MAP", SurvivalDungeon("1 bad 2")),
                ("overflowing survival MAP", SurvivalDungeon("1 2147483648 2")),
                ("unclosed survival list", SurvivalDungeon("1").Replace("[/advance altar survival map]\n", "")),
                ("nested survival list", SurvivalDungeon("[unexpected]\n1\n[/unexpected]")),
                ("isolated wrong closing inside survival list", SurvivalDungeon("1\n[/unexpected]\n2"))
            })
            {
                var invalid = GuardianAltarDungeonFile.Parse(fixture.Content);
                check(fixture.Label + " fails closed without publishing partial maps",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.StageType.Length == 0 && invalid.Maps.Count == 0 && invalid.SurvivalMapIds.Count == 0);
            }
            foreach (var fixture in new[] { (DungeonFile)null, new DungeonFile(), DungeonFile.Parse("") })
            {
                var invalid = GuardianAltarDungeonFile.Parse(fixture);
                check("missing shared DGN content/root fails closed",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.Maps.Count == 0 && invalid.SurvivalMapIds.Count == 0);
            }
        }

        private static void CheckInvalidMaps(Action<string, bool> check)
        {
            var phase = Phase(1000, "normal");
            var map = Timeline(phase);
            foreach (var fixture in new (string Label, string Content)[]
            {
                ("null MAP", null), ("empty MAP", ""), ("whitespace MAP", " \n\t"),
                ("missing center index", map.Replace(Center, "")),
                ("duplicate center index", Center + map),
                ("empty center index", map.Replace("64597", "")),
                ("zero center index", map.Replace("64597", "0")),
                ("negative center index", map.Replace("64597", "-1")),
                ("duplicate center value", map.Replace("64597", "64597 64597")),
                ("invalid center value", map.Replace("64597", "center")),
                ("overflowing center value", map.Replace("64597", "2147483648")),
                ("unclosed center index", map.Replace("[/center index]\n", "")),
                ("nested center tag", map.Replace("64597", "[unexpected]\n64597\n[/unexpected]")),
                ("isolated wrong closing inside center", map.Replace("64597", "64597\n[/unexpected]")),
                ("missing timeline", Center),
                ("duplicate timeline", map + "[time line]\n" + phase + "[/time line]\n"),
                ("empty timeline", Timeline("")),
                ("unclosed timeline", map.Replace("[/time line]\n", "")),
                ("wrong closing timeline", map.Replace("[/time line]", "[/phase]")),
                ("isolated wrong closing in timeline", Timeline("[/unexpected]\n" + phase)),
                ("unsupported timeline child tag", Timeline("[unexpected]\n1\n[/unexpected]\n" + phase)),
                ("direct timeline data", Timeline("123\n" + phase)),
                ("unclosed phase", map.Replace("[/phase]\n", "")),
                ("isolated wrong closing in phase", Timeline(phase.Replace("[duration]", "[/unexpected]\n[duration]"))),
                ("phase directly contains data", Timeline(phase.Replace("[phase]\n", "[phase]\n1\n"))),
                ("unknown phase child tag", Timeline(phase.Replace("[/phase]", "[unexpected]\n1\n[/unexpected]\n[/phase]"))),
                ("missing duration", Timeline(phase.Replace("[duration]\n1000\n[/duration]\n", ""))),
                ("duplicate duration", Timeline(phase.Replace("[/duration]", "[/duration]\n[duration]\n1000\n[/duration]"))),
                ("zero duration", Timeline(Phase(0, "normal"))),
                ("negative duration", Timeline(Phase(-1, "normal"))),
                ("invalid duration", map.Replace("1000", "forever")),
                ("multiple duration values", map.Replace("1000", "1000 2000")),
                ("single duration overflow", map.Replace("1000", "2147483648")),
                ("checked cumulative duration overflow", Timeline(Phase(int.MaxValue, "normal") + Phase(1, "boss"))),
                ("unclosed duration", map.Replace("[/duration]\n", "")),
                ("nested duration data", map.Replace("1000", "[unexpected]\n1000\n[/unexpected]")),
                ("crossed phase field closing order", map.Replace("[/duration]\n", "").Replace("[/type]", "[/duration]\n[/type]")),
                ("missing phase type", Timeline(phase.Replace("[type]\n`normal`\n[/type]\n", ""))),
                ("duplicate phase type", Timeline(phase.Replace("[/type]", "[/type]\n[type]\n`normal`\n[/type]"))),
                ("unknown phase type", Timeline(Phase(1000, "unknown"))),
                ("unquoted phase type", map.Replace("`normal`", "normal")),
                ("multiple phase types", map.Replace("`normal`", "`normal` `named`")),
                ("unclosed phase type", map.Replace("[/type]\n", "")),
                ("missing action assignments", Timeline(phase.Replace("[action assign]\n1 -1 -1\n[/action assign]\n", ""))),
                ("duplicate action assignments", Timeline(phase.Replace("[/action assign]", "[/action assign]\n[action assign]\n1 -1 -1\n[/action assign]"))),
                ("too few action assignments", Timeline(Phase(1000, "normal", "1 -1"))),
                ("too many action assignments", Timeline(Phase(1000, "normal", "1 -1 -1 2"))),
                ("invalid action assignment", Timeline(Phase(1000, "normal", "1 unknown -1"))),
                ("overflowing action assignment", Timeline(Phase(1000, "normal", "1 2147483648 -1"))),
                ("unclosed action assignments", map.Replace("[/action assign]\n", ""))
            })
            {
                var invalid = GuardianAltarMapFile.Parse(fixture.Content);
                check(fixture.Label + " fails closed without a partial timeline or wrapped total",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.CenterIndices.Count == 0 && invalid.Phases.Count == 0
                    && invalid.TotalDurationMilliseconds == 0);
            }
            foreach (var fixture in new[] { (MapFile)null, new MapFile(), MapFile.Parse("") })
            {
                var invalid = GuardianAltarMapFile.Parse(fixture);
                check("missing shared MAP content/root fails closed",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.CenterIndices.Count == 0 && invalid.Phases.Count == 0);
            }
        }

        private static string OrdinaryDungeon(string rows = OrdinaryRows) =>
            DungeonHeader + "[advance altar type]\n`normal`\n" + MapTable(rows);

        private static string MapTable(string rows) =>
            "[advance altar map]\n" + rows + "[/advance altar map]\n";

        private static string SurvivalDungeon(string rows) =>
            DungeonHeader + "[advance altar type]\n`survival`\n[advance altar survival map]\n"
            + rows + "\n[/advance altar survival map]\n";

        private static string Phase(int duration, string type, string assignments = "1 -1 -1") =>
            "[phase]\n[duration]\n" + duration.ToString(CultureInfo.InvariantCulture)
            + "\n[/duration]\n[type]\n`" + type + "`\n[/type]\n[action assign]\n"
            + assignments + "\n[/action assign]\n[/phase]\n";

        private static string Timeline(string phases) =>
            Center + "[time line]\n" + phases + "[/time line]\n";

        private static LstEntry RegisteredEntry(LstFile list, int id, string kind)
        {
            var entries = list.Entries.Where(x => x.Id == id).ToArray();
            if (entries.Length != 1 || string.IsNullOrWhiteSpace(entries[0].FilePath))
                throw new InvalidDataException($"Expected exactly one registered {kind} for id {id}.");
            return entries[0];
        }

        private static string ReadRequired(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content))
                throw new FileNotFoundException("The current PVF lacks a required registered resource.", path);
            return content;
        }

        private static string[] SourceWords(ScriptNode node, string content) =>
            node.DataItems.SelectMany(x => x.GetContent(content).Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
                .Select(x => x.Split('#')[0])
                .SelectMany(x => x.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).ToArray();

        private static int[] SourceIntegers(ScriptNode node, string content) =>
            SourceWords(node, content)
                .Select(x => int.Parse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)).ToArray();

        private static IEnumerable<(string Difficulty, int MapId, int BasisLevel)> SourceMapRows(
            ScriptNode node, string content)
        {
            var words = SourceWords(node, content);
            if (words.Length % 3 != 0)
                throw new InvalidDataException("Source Guardian map rows are not three-field records.");
            for (var offset = 0; offset < words.Length; offset += 3)
                yield return (words[offset].Trim('`').ToLowerInvariant(),
                    int.Parse(words[offset + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                    int.Parse(words[offset + 2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        private static bool SameDungeon(GuardianAltarDungeonFile left, GuardianAltarDungeonFile right) =>
            left.StageType == right.StageType
            && left.Maps.Select(x => (x.DifficultyName, x.MapId, x.MapBasisLevel))
                .SequenceEqual(right.Maps.Select(x => (x.DifficultyName, x.MapId, x.MapBasisLevel)))
            && left.SurvivalMapIds.SequenceEqual(right.SurvivalMapIds);

        private static bool SameMap(GuardianAltarMapFile left, GuardianAltarMapFile right) =>
            left.CenterIndices.SequenceEqual(right.CenterIndices)
            && left.TotalDurationMilliseconds == right.TotalDurationMilliseconds
            && left.Phases.Count == right.Phases.Count
            && left.Phases.Zip(right.Phases, (a, b) => a.DurationMilliseconds == b.DurationMilliseconds
                && a.Type == b.Type && a.ActionAssignments.SequenceEqual(b.ActionAssignments)).All(x => x);
    }
}
