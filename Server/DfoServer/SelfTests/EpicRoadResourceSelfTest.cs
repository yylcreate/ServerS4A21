using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class EpicRoadResourceSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== EPIC_ROAD_RESOURCE selftest ===");
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

            Section("current archive entry/count/town/map/drop resources", () => VerifyCurrentArchive(Check));
            Section("strict count configuration", () => VerifyCountModel(Check));
            Section("typed NPC activity role boundaries", () => VerifyNpcModel(Check));
            Console.WriteLine($"EPIC_ROAD_RESOURCE assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCurrentArchive(Action<string, bool> check)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 archive before this selftest.", path);
            using var archive = PvfArchive.Open(path);
            var dungeonPath = ListedPath(archive, "dungeon/dungeon.lst", "dungeon/", 6004);
            var npc1125Path = ListedPath(archive, "npc/npc.lst", "npc/", 1125);
            var npc1126Path = ListedPath(archive, "npc/npc.lst", "npc/", 1126);
            var townPath = ListedPath(archive, "town/town.lst", "town/", 28);
            var worldPath = ListedPath(archive, "worldmap/worldmap.lst", "worldmap/", 35);
            check("current LST resolves the epic entry through NPC indices and real town/world identifiers",
                dungeonPath.Equals("dungeon/Event/2016_EpciRoad/EpicRoad_01.dgn", StringComparison.OrdinalIgnoreCase)
                && npc1125Path.Equals("npc/event_npc_TouLevel_1.npc", StringComparison.OrdinalIgnoreCase)
                && npc1126Path.Equals("npc/event_npc_TouLevel_2.npc", StringComparison.OrdinalIgnoreCase)
                && townPath.Equals("town/Event_hendon.twn", StringComparison.OrdinalIgnoreCase)
                && worldPath.Equals("worldmap/TauLevel.wdm", StringComparison.OrdinalIgnoreCase)
                && LstFile.Parse(Required(archive, "town/town.lst")).GetById(1126) == null);
            var count = DungeonIncountConfigFile.Parse(Required(archive, "etc/dungeonetc/dungeonincountinfo.etc"));
            var epicCount = count.AccountEntries.SingleOrDefault(entry => entry.DungeonId == 6004);
            check("current epic count is an account-scoped ten-entry row, separate from character rows",
                count.IsValid && epicCount?.MaximumCount == 10
                && epicCount.RawFields.SequenceEqual(new[] { 0, 6004, 10, 0 })
                && count.CharacterEntries.All(entry => entry.DungeonId != 6004)
                && count.AccountEntries.Count == 7 && count.CharacterEntries.Count == 25);
            check("current reset and enter-added raw data are retained without inventing a timezone or replacement grouping",
                count.ResetRules.Count == 1 && count.ResetRules[0].RawFields.SequenceEqual(new[] { 0, -1 })
                && count.EnterAddIncount.SequenceEqual(new[] { 8014, 8015, 8014, 8016, 8015, 8014, 8015, 8016, 8016, 8014, 8016, 8015 }));
            var npc1125 = NpcFile.Parse(Required(archive, npc1125Path));
            var source = NpcActivityEntryDefinition.From(npc1125);
            var target = NpcActivityEntryDefinition.From(NpcFile.Parse(Required(archive, npc1126Path)));
            check("current NPC source names a destination NPC and gates its display with event 286",
                source.IsValid && source.EnterTownNpcId == 1126 && source.ExitTownRoute == null
                && source.EventId == 286 && source.AccountDungeonId == 6004 && npc1125.Name == "湍流"
                && source.HiddenTownIds.SequenceEqual(new[] { 17, 19 }));
            check("current target NPC retains exit route zero and account display without fabricating a visible event",
                target.IsValid && target.EnterTownNpcId == null && target.ExitTownRoute == 0
                && target.EventId == null && target.AccountDungeonId == 6004 && target.HiddenTownIds.Count == 0);

            var town = TownFile.Parse(Required(archive, townPath));
            var area = town.Areas.Single(entry => entry.Id == 0);
            var region = MapFile.Parse(Required(archive, "map/" + area.MapPath));
            check("real town area binds the destination NPC, world gate and current permission",
                town.Name == "祥瑞溪谷" && area.LinkedId == 35 && area.AreaType == "dungeon gate"
                && area.Permission?.NeedLevel == 17 && area.Permission.NeedQuest == 8598
                && region.Npcs.Any(npc => npc.NpcId == 1126 && npc.X == 441 && npc.Y == 177));
            var worldText = Required(archive, worldPath);
            var world = new ScriptParser().Parse(worldText);
            check("current world selection has only dungeon 6004 and distinguishes its display flags from a DGN hell contract",
                Tokens(world, worldText, "dungeon").SequenceEqual(new[] { "6004", "-1" })
                && Tokens(world, worldText, "hell fire effect").SequenceEqual(new[] { "1" })
                && Tokens(world, worldText, "hell dungeon").SequenceEqual(new[] { "1" }));
            var eventText = Required(archive, "event/dungeonopencloseevent.evt");
            var opening = new ScriptParser().Parse(eventText);
            check("current dungeon-open/close resource identifies its own type and dungeon without supplying a guessed event ID",
                Tokens(opening, eventText, "type").SequenceEqual(new[] { "1" })
                && Tokens(opening, eventText, "dungeon indexes").SequenceEqual(new[] { "6004" })
                && opening.Children.Count == 2);
            var questPath = ListedPath(archive, "n_quest/quest.lst", "n_quest/", 8598);
            var quest = QuestFile.Parse(Required(archive, questPath));
            check("town prerequisite is the current second introduction quest, allowing the later target-NPC introduction",
                quest.Name == "青龙的召唤 (2/3)" && quest.NpcIndex == 1125
                && quest.Type == "[meet npc]" && quest.IntData.Trim() == "1125"
                && quest.PreRequiredQuest.Trim() == "8597" && quest.RelationQuest.Trim() == "8599"
                && quest.Level.SequenceEqual(new[] { 17, 99 }) && quest.GetIntValue("show quest on event") == 286);

            var dungeon = DungeonFile.Parse(Required(archive, dungeonPath));
            check("current DGN admission is solo level 17, designated normal difficulty and initial fatigue three without a resource ticket",
                dungeon.MinimumRequiredLevel == 17 && dungeon.LimitPartyCount == 1
                && dungeon.DesignateDungeonDifficulty.SequenceEqual(new[] { 0 })
                && dungeon.UseFatigueOnlyStartDungeon && dungeon.UseFatigueOnlyStartDungeonData.Trim() == "3"
                && !dungeon.NoFatigue && !dungeon.EnterWithoutFatigue && dungeon.RequiredItems.Count == 0
                && !dungeon.HasTag("ignore default dungeon clear") && !dungeon.HasTag("clear condition"));
            var expTokens = ScriptValueTokenizer.Tokenize(dungeon.GetValue("tau event exp"));
            check("current tau-type experience/revision fields remain distinct from ordinary story or new Tau Kingdom data",
                dungeon.DungeonType == "tau Dungeon" && dungeon.MobLevelCharacLevelReplaceFlag == 1
                && dungeon.GetStringValue("revision table").Equals("Dungeon/Towers/table/EventEpicRoad.tbl", StringComparison.OrdinalIgnoreCase)
                && expTokens.Count == 210 && expTokens.Take(3).SequenceEqual(new[] { "17", "54", "78" })
                && expTokens.TakeLast(3).SequenceEqual(new[] { "86", "763", "1114" })
                && dungeon.GetIntValue("clear daily challenge quest") == 14722);
            var expectedGreed = new[] { "BBFFEE", "AAIIBBGG", "BBMMAACC" };
            var expectedStart = new[] { new[] { 0, 0 }, new[] { 0, 1 }, new[] { 0, 0 } };
            var expectedBoss = new[] { new[] { 2, 0 }, new[] { 1, 0 }, new[] { 1, 1 } };
            check("all three current maze variants retain full multiline grids and independent start/boss endpoints",
                dungeon.Mazes.Count == 3 && dungeon.Mazes.Select((maze, index) =>
                    maze.MapSpecifications.Count == 3 && Compact(maze.Greed) == expectedGreed[index]
                    && maze.StartMap.SequenceEqual(expectedStart[index]) && maze.BossMap.SequenceEqual(expectedBoss[index]))
                    .All(valid => valid)
                && dungeon.Mazes.SelectMany(maze => maze.MapSpecifications.Select(cell => cell.Index))
                    .SequenceEqual(Enumerable.Range(80011, 9)));
            var maps = Enumerable.Range(80011, 9).ToDictionary(id => id,
                id => MapFile.Parse(Required(archive, "map/event/16_epicroad/" + id + ".map")));
            check("all nine current MAPs bind dungeon 6004 and one fixed normal spawn rather than a fabricated wave/hell-object mechanism",
                maps.Values.All(map => map.DungeonId == 6004 && map.Monsters.Count == 1
                    && map.Monsters[0].Type == MonsterType.Normal && map.Monsters[0].Fixed == "[fixed]"
                    && map.MonsterConditionMonsters.Count == 0 && map.ConditionalSummonMonsters.Count == 0
                    && map.PassiveObjects.Count == 0 && map.SpecialPassiveObjects.Count == 0));
            foreach (var pair in new[] { (Map: 80013, Monster: 140019), (Map: 80016, Monster: 140021), (Map: 80019, Monster: 140020) })
                check("final MAP " + pair.Map + " marks its endpoint and correct boss resource despite a normal spawn type",
                    maps[pair.Map].Type == "[boss]" && maps[pair.Map].Monsters[0].MonsterId == pair.Monster
                    && maps[pair.Map].Npcs.Any(npc => npc.NpcId == 1000));

            var drops = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(Required(archive, "etc/independent_drop.etc"));
            foreach (var monster in new[] { 140019, 140020, 140021 })
            {
                var entries = drops[monster];
                var coin = entries.Where(entry => entry.ItemId == 490003519 && !entry.HasItemPool).ToArray();
                var equipment = entries.Single(entry => entry.PoolKind == IndependentDropPoolKind.Inline);
                check("current independent-drop owner retains three coin entries and the weighted equipment opportunity for " + monster,
                    entries.Length == 4 && coin.Length == 3
                    && coin.All(entry => Enumerable.Range(0, 5).All(tier => entry.GetProbability(tier) == 1000000 && entry.GetCount(tier) == 1))
                    && Enumerable.Range(0, 5).All(tier => equipment.GetProbability(tier) == 100000 && equipment.GetCount(tier) == 1)
                    && equipment.TryResolvePool(IndependentDropEntryDefinition.UniversalJobGroup, out var pool)
                    && pool.TotalWeight > 0 && pool.Items.Count > 0);
            }
            check("read-only resource regression does not mutate the opened archive", !archive.HasModifications);
        }

        private static void VerifyCountModel(Action<string, bool> check)
        {
            var valid = CountConfig();
            var parsed = DungeonIncountConfigFile.Parse(valid);
            check("typed count tables preserve unknown signed fields and do not merge character/account definitions",
                parsed.IsValid && parsed.CharacterEntries.Single().RawFields.SequenceEqual(new[] { -7, 707, 4, 123 })
                && parsed.AccountEntries.Single().RawFields.SequenceEqual(new[] { 91, 707, 5, -2 })
                && parsed.CharacterEntries[0].DungeonId == 707 && parsed.AccountEntries[0].MaximumCount == 5
                && parsed.ResetRules[0].RawFields.SequenceEqual(new[] { 0, -1 })
                && parsed.EnterAddIncount.SequenceEqual(new[] { 707, 808, 707 }));
            check("published count definitions and each raw array are immutable",
                ReadOnly(() => ((IList<DungeonIncountEntry>)parsed.AccountEntries).Clear())
                && ReadOnly(() => ((IList<int>)parsed.CharacterEntries[0].RawFields)[0] = 9)
                && ReadOnly(() => ((IList<DungeonIncountResetRule>)parsed.ResetRules).Clear())
                && ReadOnly(() => ((IList<int>)parsed.ResetRules[0].RawFields)[0] = 9)
                && ReadOnly(() => ((IList<int>)parsed.EnterAddIncount)[0] = 9));
            var invalid = new (string Label, string Text)[]
            {
                ("missing archive text", null), ("empty", ""), ("unexpected root integers", "1\n" + valid),
                ("duplicate account table", valid + AccountBlock("0 808 3 0")),
                ("duplicate account dungeon", CountConfig(account: "0 808 3 0 1 808 4 0")),
                ("duplicate character dungeon", CountConfig(character: "0 808 3 0 0 808 3 0")),
                ("partial row after valid account row", CountConfig(account: "0 707 5 0 0 808 3")),
                ("late malformed account value", CountConfig(account: "0 707 5 0 0 808 3 bad")),
                ("zero dungeon", CountConfig(account: "0 0 3 0")),
                ("zero maximum", CountConfig(account: "0 808 0 0")),
                ("negative maximum", CountConfig(account: "0 808 -1 0")),
                ("overflow", CountConfig(account: "0 808 2147483648 0")),
                ("quoted numeric", CountConfig(account: "0 `808` 3 0")),
                ("fraction", CountConfig(account: "0 808 3.5 0")),
                ("unclosed account section", valid.Replace("[/account dungeon incount info]\n", "")),
                ("missing reset child", valid.Replace("[reset info]\n0 -1\n[/reset info]\n", "")),
                ("partial reset pair", valid.Replace("0 -1\n", "0\n")),
                ("duplicate reset pair", valid.Replace("[reset info]\n0 -1\n[/reset info]\n",
                    "[reset info]\n0 -1\n[/reset info]\n[reset info]\n0 -1\n[/reset info]\n")),
                ("empty enter-added array", valid.Replace("707 808 707\n", "")),
                ("nonpositive enter-added index", valid.Replace("707 808 707\n", "707 0 707\n")),
                ("unexpected late section", valid + "[unknown]\n1\n[/unknown]\n"),
                ("stray closing tag", valid + "[/unknown]\n"),
            };
            foreach (var sample in invalid)
            {
                var result = DungeonIncountConfigFile.Parse(sample.Text);
                check("count " + sample.Label + " cannot expose a partial earlier table",
                    !result.IsValid && !string.IsNullOrWhiteSpace(result.InvalidReason)
                    && result.CharacterEntries.Count == 0 && result.AccountEntries.Count == 0
                    && result.ResetRules.Count == 0 && result.EnterAddIncount.Count == 0);
            }
        }

        private static void VerifyNpcModel(Action<string, bool> check)
        {
            var source = NpcActivityEntryDefinition.Parse(NpcConfig());
            check("NPC activity definition supports a destination NPC and optional current fields without touching unrelated roles",
                source.IsValid && source.EnterTownNpcId == 1126 && source.ExitTownRoute == null
                && source.EventId == 286 && source.AccountDungeonId == 6004
                && source.HiddenTownIds.SequenceEqual(new[] { 17, 19 }));
            check("NPC hidden-town definitions publish immutable ordered indices",
                ReadOnly(() => ((IList<int>)source.HiddenTownIds)[0] = 18)
                && ReadOnly(() => ((IList<int>)source.HiddenTownIds).Clear()));
            var exit = NpcActivityEntryDefinition.Parse("[role]\n`[exit town]` 0 `[item shop]` 174\n[/role]\n");
            check("zero exit route is explicit and remains distinct from an absent enter route",
                exit.IsValid && exit.ExitTownRoute == 0 && exit.EnterTownNpcId == null
                && exit.EventId == null && exit.AccountDungeonId == null && exit.HiddenTownIds.Count == 0);
            var empty = NpcActivityEntryDefinition.Parse("[name]\n`Ordinary NPC`\n[role]\n`[item shop]` 174\n[/role]\n");
            check("an unrelated existing NPC is a valid empty optional activity definition",
                empty.IsValid && empty.EnterTownNpcId == null && empty.ExitTownRoute == null
                && empty.EventId == null && empty.AccountDungeonId == null && empty.HiddenTownIds.Count == 0);
            var commented = NpcActivityEntryDefinition.Parse(NpcConfig(role: "`[enter town]` 1126 # route comment"));
            check("role comments do not become extra route arguments", commented.IsValid && commented.EnterTownNpcId == 1126);
            var multiline = NpcActivityEntryDefinition.Parse(NpcConfig(hiddenTowns: "17 # first town\n19"));
            check("hidden-town lines retain their order and omit comments",
                multiline.IsValid && multiline.HiddenTownIds.SequenceEqual(new[] { 17, 19 }));
            var absent = NpcActivityEntryDefinition.Parse(NpcConfig(hiddenTowns: null));
            check("an absent hidden-town section remains an immutable empty collection",
                absent.IsValid && absent.EnterTownNpcId == 1126 && absent.HiddenTownIds.Count == 0
                && ReadOnly(() => ((IList<int>)absent.HiddenTownIds).Add(17)));
            var invalid = new (string Label, string Text)[]
            {
                ("missing NPC text", null), ("empty", ""),
                ("duplicate enter role", NpcConfig(role: "`[enter town]` 1126 `[enter town]` 1126")),
                ("duplicate exit role", NpcConfig(role: "`[exit town]` 0 `[exit town]` 0")),
                ("duplicate role section", NpcConfig() + "[role]\n`[enter town]` 1126\n[/role]\n"),
                ("missing role argument", NpcConfig(role: "`[enter town]`")),
                ("zero target NPC", NpcConfig(role: "`[enter town]` 0")),
                ("negative target NPC", NpcConfig(role: "`[enter town]` -1")),
                ("negative exit", NpcConfig(role: "`[exit town]` -1")),
                ("overflow route", NpcConfig(role: "`[enter town]` 2147483648")),
                ("fraction route", NpcConfig(role: "`[enter town]` 1126.0")),
                ("quoted route integer", NpcConfig(role: "`[enter town]` `1126`")),
                ("extra route argument", NpcConfig(role: "`[enter town]` 1126 0")),
                ("unquoted role key", NpcConfig(role: "[enter town] 1126")),
                ("unclosed role quote", NpcConfig(role: "`[enter town] 1126")),
                ("unclosed role section", NpcConfig().Replace("[/role]\n", "")),
                ("duplicate visible event", NpcConfig() + "[open visible event]\n286\n"),
                ("invalid late visible event", NpcConfig(eventValue: "bad")),
                ("quoted event number", NpcConfig(eventValue: "`286`")),
                ("zero account dungeon", NpcConfig(accountValue: "0")),
                ("duplicate account dungeon", NpcConfig() + "[account dungeon incount display]\n6004\n"),
                ("extra scalar account value", NpcConfig(accountValue: "6004 1")),
                ("duplicate hidden-town section", NpcConfig() + "[hide at town]\n17 19\n[/hide at town]\n"),
                ("duplicate hidden town", NpcConfig(hiddenTowns: "17 19 17")),
                ("empty hidden-town section", NpcConfig(hiddenTowns: "")),
                ("zero hidden town", NpcConfig(hiddenTowns: "17 0")),
                ("negative hidden town", NpcConfig(hiddenTowns: "17 -19")),
                ("overflow hidden town", NpcConfig(hiddenTowns: "17 2147483648")),
                ("fraction hidden town", NpcConfig(hiddenTowns: "17 19.0")),
                ("quoted hidden town", NpcConfig(hiddenTowns: "17 `19`")),
                ("malformed late hidden town", NpcConfig(hiddenTowns: "17 bad")),
                ("nested hidden-town field", NpcConfig(hiddenTowns: "17\n[name]\n19\n[/name]")),
                ("unclosed hidden-town section", NpcConfig().Replace("[/hide at town]\n", "")),
                ("late failure after hidden-town section", NpcConfig() + "[open visible event]\nbad\n"),
            };
            foreach (var sample in invalid)
            {
                var result = NpcActivityEntryDefinition.Parse(sample.Text);
                check("NPC " + sample.Label + " publishes no earlier route or activity fields",
                    !result.IsValid && !string.IsNullOrWhiteSpace(result.InvalidReason)
                    && result.EnterTownNpcId == null && result.ExitTownRoute == null
                    && result.EventId == null && result.AccountDungeonId == null && result.HiddenTownIds.Count == 0);
            }
        }

        private static string CountConfig(string character = "-7 707 4 123", string account = "91 707 5 -2")
            => "[dungeon incount reset info]\n[reset info]\n0 -1\n[/reset info]\n[/dungeon incount reset info]\n"
                + "[dungeon incount info]\n" + character + "\n[/dungeon incount info]\n"
                + "[enter add incount dungeon index]\n707 808 707\n[/enter add incount dungeon index]\n" + AccountBlock(account);
        private static string AccountBlock(string row)
            => "[account dungeon incount info]\n" + row + "\n[/account dungeon incount info]\n";
        private static string NpcConfig(string role = "`[enter town]` 1126", string eventValue = "286", string accountValue = "6004",
            string hiddenTowns = "17 19")
            => "[name]\n`Entry NPC`\n[role]\n" + role + "\n[/role]\n"
                + "[open visible event]\n" + eventValue + "\n[account dungeon incount display]\n" + accountValue + "\n"
                + (hiddenTowns == null ? string.Empty : "[hide at town]\n" + hiddenTowns + "\n[/hide at town]\n");
        private static bool ReadOnly(Action mutation)
        {
            try { mutation(); return false; }
            catch (NotSupportedException) { return true; }
        }
        private static string Compact(string text)
            => string.Concat((text ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
        private static IReadOnlyList<string> Tokens(ScriptNode root, string text, string tag)
            => root.Children.Where(node => node.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .SelectMany(node => node.DataItems.SelectMany(data => ScriptValueTokenizer.Tokenize(data.GetContent(text)))).ToArray();
        private static string ListedPath(PvfArchive archive, string list, string prefix, int id)
        {
            var row = LstFile.Parse(Required(archive, list)).GetById(id)
                ?? throw new InvalidDataException("Current PVF LST is missing " + id.ToString(CultureInfo.InvariantCulture) + ": " + list);
            return prefix + row.FilePath;
        }
        private static string Required(PvfArchive archive, string path)
        {
            var text = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(text)) throw new FileNotFoundException("Current PVF asset is missing or empty.", path);
            return text;
        }
    }
}
