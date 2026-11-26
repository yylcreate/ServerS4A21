using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events;
using DfoServer.Game.Events.ScheduledDungeons;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class EventDungeonOpeningSelfTest
    {
        private const string TauDescriptor = "3700 1 1 1 1 1 14 15 16 17 18 19 19 20 21 0 -1";
        private const string SilverDescriptor = "4123 1 1 1 1 0 -1 -1 -1 -1 -1 -1 0 0 0 0 -1";

        internal static int Run()
        {
            Console.WriteLine("=== EVENT_DUNGEON_OPENING selftest ===");
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

            Section("current PVF activity resources", () => VerifyCurrentResources(Check));
            Section("gold/silver config validation", () => VerifyGoldSilverConfig(Check));
            Section("raw daily schedule descriptors", () => VerifyScheduleConfig(Check));
            Section("finite activity windows and resource admission", () => VerifyOpeningRules(Check));
            Section("entry normalization and existing count admission", () => VerifyEntryIntegration(Check));
            Section("existing durable entry-count owner", () => VerifyDurableCounts(Check));
            Section("count projection validation and wire bodies", () => VerifyCountProjection(Check));
            Section("activity publication and count refresh", () => VerifyPublicationAsync(Check).GetAwaiter().GetResult());
            Section("queued and per-frame publication fences", () => VerifyPublicationFencesAsync(Check).GetAwaiter().GetResult());
            Section("publication failure and count mismatch", () => VerifyPublicationFailuresAsync(Check).GetAwaiter().GetResult());
            Console.WriteLine($"EVENT_DUNGEON_OPENING assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCurrentResources(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var goldSilverText = Required(archive, "etc/chn_goldsilverhorndungeon.etc");
            var scheduleText = Required(archive, "etc/chn_schedule.etc");
            var goldSilver = GoldSilverHornDungeonConfigFile.Parse(goldSilverText);
            var schedule = DailyScheduleConfigFile.Parse(scheduleText);
            check("current repeated-marker ETC defines world-map 14 and the configured silver-side dungeon only",
                goldSilver.IsValid && goldSilver.WorldMapIndex == 14 && goldSilver.Gold == null
                && goldSilver.Silver?.DungeonId == 4123 && goldSilver.Silver.MaxEnterCount == 3
                && goldSilver.Silver.DungeonEnterCost == null);
            check("both actual dungeon schedule descriptors retain all 17 raw fields",
                schedule.IsValid && schedule.TryGetDungeon(3700, out var tauRow)
                && tauRow.RawFields.SequenceEqual(Numbers(TauDescriptor))
                && schedule.TryGetDungeon(4123, out var silverRow)
                && silverRow.RawFields.SequenceEqual(Numbers(SilverDescriptor)));
            var tau = DungeonFile.Parse(Required(archive, "dungeon/tau_kingdom/tau_kingdom.dgn"));
            var silver = DungeonFile.Parse(Required(archive,
                "dungeon/event/chn_dayof_goldsilver_dungeon/chn_dayof_goldsilver_dungeon.dgn"));
            check("new tau resource uses level 27, solo admission and an entry-consumed ticket",
                tau.MinimumRequiredLevel == 27 && tau.LimitPartyCount == 1 && tau.NoFatigue
                && tau.RequiredItems.Count == 1 && tau.RequiredItems[0].ItemId == 690001556
                && tau.RequiredItems[0].Count == 1 && tau.RequiredItems[0].ConsumeOnEntry
                && tau.DesignateDungeonDifficulty.SequenceEqual(new[] { 0 }));
            check("new gold/silver resource uses level 50, solo admission and its designated difficulty",
                silver.MinimumRequiredLevel == 50 && silver.LimitPartyCount == 1 && silver.NoFatigue
                && silver.RequiredItems.Count == 0 && silver.DesignateDungeonDifficulty.SequenceEqual(new[] { 2 }));
            check("new gold/silver maze keeps all three boss alternatives and both grid rows",
                silver.Mazes.Count == 1 && silver.Mazes[0].Width == 2 && silver.Mazes[0].Height == 2
                && silver.Mazes[0].Greed.Replace(" ", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty) == "BBMMBBGG"
                && silver.Mazes[0].MapSpecifications.Any(cell => cell.X == 0 && cell.Y == 1
                    && cell.MapCandidates.SequenceEqual(new[] { 25606, 25607, 25608 })));
            var oldTau = DungeonFile.Parse(Required(archive, "dungeon/event/2015_tou_level/tou_01.dgn"));
            check("old TauLevel resource remains distinct from the new ticket-based activity",
                oldTau.MinimumRequiredLevel == 17 && oldTau.DungeonType == "tau Dungeon"
                && tau.DungeonType != oldTau.DungeonType && !archive.HasModifications
                && goldSilver.Content == goldSilverText && schedule.Content == scheduleText);
        }

        private static void VerifyGoldSilverConfig(Action<string, bool> check)
        {
            var valid = GoldSilverConfig();
            var parsed = GoldSilverHornDungeonConfigFile.Parse(valid);
            check("missing gold and missing costs are valid unconfigured fields",
                parsed.IsValid && parsed.Gold == null && parsed.Silver.DungeonId == 4123
                && parsed.Silver.MaxEnterCount == 3 && parsed.Silver.DungeonEnterCost == null);
            var complete = GoldSilverHornDungeonConfigFile.Parse(WorldMap()
                + "[gold horn dungeon]\n[dungeon index]\n707\n[max enter count]\n2\n"
                + "[dungeon enter cost]\n1 10 2 20\n[/dungeon enter cost]\n[/gold horn dungeon]\n"
                + "[silver horn dungeon]\n[dungeon index]\n808\n[max enter count]\n4\n[silver horn dungeon]\n");
            check("configured canonical and repeated-marker sections publish their own values without a hardcoded dungeon",
                complete.IsValid && complete.Gold.DungeonId == 707 && complete.Gold.MaxEnterCount == 2
                && complete.Gold.DungeonEnterCost.SequenceEqual(new[] { 1, 10, 2, 20 })
                && complete.Silver.DungeonId == 808 && complete.Silver.MaxEnterCount == 4
                && complete.Silver.DungeonEnterCost == null);
            check("configured cost pairs are immutable",
                RejectsMutation(() => ((IList<int>)complete.Gold.DungeonEnterCost)[0] = 999)
                && complete.Gold.DungeonEnterCost[0] == 1);
            var cases = new (string Label, string Source)[]
            {
                ("null", null), ("empty", ""), ("root data", "7\n" + valid),
                ("duplicate world map", WorldMap() + valid),
                ("world scalar pair", valid.Replace("14\n", "14 15\n")),
                ("zero world map", valid.Replace("14\n", "0\n")),
                ("missing world close", valid.Replace("[/world map index]\n", "")),
                ("missing section close", WorldMap() + "[silver horn dungeon]\n[dungeon index]\n4123\n[max enter count]\n3\n"),
                ("empty section", WorldMap() + "[silver horn dungeon]\n[silver horn dungeon]\n"),
                ("duplicate section", valid + SilverBlock()),
                ("duplicate dungeon index", GoldSilverConfig("[dungeon index]\n4123\n[dungeon index]\n4123\n[max enter count]\n3\n")),
                ("duplicate maximum", GoldSilverConfig("[dungeon index]\n4123\n[max enter count]\n3\n[max enter count]\n3\n")),
                ("zero dungeon", valid.Replace("4123\n", "0\n")),
                ("negative dungeon", valid.Replace("4123\n", "-1\n")),
                ("zero maximum", valid.Replace("3\n", "0\n")),
                ("negative maximum", valid.Replace("3\n", "-1\n")),
                ("overflow integer", valid.Replace("4123\n", "2147483648\n")),
                ("fraction", valid.Replace("4123\n", "4123.0\n")),
                ("quoted integer", valid.Replace("4123\n", "`4123`\n")),
                ("unknown field", GoldSilverConfig("[dungeon index]\n4123\n[max enter count]\n3\n[unknown]\n1\n")),
                ("partial cost pair", GoldSilverConfig("[dungeon index]\n4123\n[max enter count]\n3\n[dungeon enter cost]\n1 10 2\n")),
                ("bad late cost", GoldSilverConfig("[dungeon index]\n4123\n[max enter count]\n3\n[dungeon enter cost]\n1 10 2 bad\n")),
                ("stray closing tag", "[/unknown]\n" + valid),
                ("unmatched closing tag", valid + "[/silver horn dungeon]\n"),
            };
            foreach (var test in cases)
            {
                var invalid = GoldSilverHornDungeonConfigFile.Parse(test.Source);
                check("gold/silver " + test.Label + " does not publish partial rules",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.WorldMapIndex == 0 && invalid.Gold == null && invalid.Silver == null);
            }
        }

        private static void VerifyScheduleConfig(Action<string, bool> check)
        {
            var raw = "707 -1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 -1";
            var parsed = DailyScheduleConfigFile.Parse(ScheduleConfig(raw)
                + "[quest]\n1 -1 0\n[/quest]\n[common schedule]\n4 10 -1\n[/common schedule]\n"
                + "[dungeon minimap]\n707 1 149 492 18 19\n[/dungeon minimap]\n");
            check("schedule retains signed unknown fields and coexists with the other current tables",
                parsed.IsValid && parsed.Dungeons.Count == 1 && parsed.TryGetDungeon(707, out var row)
                && row.DungeonId == 707 && row.RawFields.SequenceEqual(Numbers(raw))
                && !parsed.TryGetDungeon(808, out _));
            check("descriptor list and each raw row are immutable",
                RejectsMutation(() => ((IList<DailyScheduleDungeonDescriptor>)parsed.Dungeons).Clear())
                && RejectsMutation(() => ((IList<int>)parsed.Dungeons[0].RawFields)[0] = 808)
                && parsed.TryGetDungeon(707, out _));
            var valid = ScheduleConfig(raw);
            var cases = new (string Label, string Source)[]
            {
                ("null", null), ("empty", ""), ("root data", "3\n" + valid),
                ("missing dungeon table", "[quest]\n1\n[/quest]\n"),
                ("empty dungeon table", ScheduleConfig("")),
                ("16 fields", ScheduleConfig(string.Join(" ", Numbers(raw).Take(16)))),
                ("18 fields", ScheduleConfig(raw + " 0")),
                ("duplicate dungeon ID", ScheduleConfig(raw + "\n" + raw)),
                ("duplicate table", valid + valid),
                ("zero dungeon ID", ScheduleConfig("0" + raw.Substring(3))),
                ("negative dungeon ID", ScheduleConfig("-707" + raw.Substring(3))),
                ("overflow field", ScheduleConfig(raw.Replace("15", "2147483648"))),
                ("fraction", ScheduleConfig(raw.Replace("15", "15.1"))),
                ("quoted field", ScheduleConfig(raw.Replace("15", "`15`"))),
                ("malformed late row", ScheduleConfig(raw + "\n808 -1 2 3 4 5 6 7 8 9 10 11 12 13 14 bad -1")),
                ("missing closing tag", valid.Replace("[/dungeon]\n", "")),
                ("nested table", ScheduleConfig("[nested]\n" + raw + "\n[/nested]")),
                ("stray closing tag", "[/other]\n" + valid),
                ("unexpected table", valid + "[unknown]\n1\n[/unknown]\n"),
                ("duplicate sibling table", valid + "[quest]\n1\n[/quest]\n[quest]\n2\n[/quest]\n"),
                ("unclosed sibling table", valid + "[quest]\n1\n"),
            };
            foreach (var test in cases)
            {
                var invalid = DailyScheduleConfigFile.Parse(test.Source);
                check("schedule " + test.Label + " does not publish any earlier row",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.Dungeons.Count == 0 && !invalid.TryGetDungeon(707, out _));
            }
        }

        private static void VerifyOpeningRules(Action<string, bool> check)
        {
            var now = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
            var service = CreateOpeningService();
            check("both new activities default closed and old TauLevel remains outside this gate",
                service.IsManagedDungeon(3700) && service.IsManagedDungeon(4123)
                && !service.IsManagedDungeon(8014) && !service.GetSnapshot(3700, now).IsOpen
                && service.GetSnapshot(3700, now).DefinitionValid
                && !service.TryValidateEntry(3700, 50, 1, now, out _, out var closedReason)
                && closedReason == "activity_window_closed"
                && service.TryValidateEntry(8014, 1, 4, now, out _, out _));
            var start = now.AddSeconds(1);
            var end = start.AddSeconds(9).AddMilliseconds(1);
            var revision = service.SetWindow(3700, start, end);
            check("opening window excludes time before start and rounds a positive UI budget upward",
                !service.GetSnapshot(3700, start.AddTicks(-1)).IsOpen
                && service.GetSnapshot(3700, start.AddTicks(-1)).NextTransitionUtc == start
                && service.GetSnapshot(3700, start).IsOpen
                && service.GetSnapshot(3700, start).RemainingSeconds == 10
                && service.GetSnapshot(3700, end.AddTicks(-1)).RemainingSeconds == 1
                && !service.GetSnapshot(3700, end).IsOpen
                && service.GetSnapshot(3700, end).RemainingSeconds == 0);
            check("equivalent absolute windows are idempotent across timezone representation",
                service.SetWindow(3700, start.ToOffset(TimeSpan.FromHours(8)),
                    end.ToOffset(TimeSpan.FromHours(8))) == revision && service.Revision == revision);
            check("absolute time recomputation handles late and backward clock observations",
                !service.GetSnapshot(3700, end.AddDays(1)).IsOpen
                && service.GetSnapshot(3700, start).IsOpen && service.Revision == revision);
            check("new tau admission uses its own minimum and solo rule",
                !service.TryValidateEntry(3700, 26, 1, start, out _, out var low)
                && low == "minimum_level_not_met"
                && !service.TryValidateEntry(3700, 27, 2, start, out _, out var party)
                && party == "party_count_not_allowed"
                && !service.TryValidateEntry(3700, 27, 0, start, out _, out _)
                && service.TryValidateEntry(3700, 27, 1, start, out var tauDifficulty, out _)
                && tauDifficulty == 0);
            service.SetWindow(4123, start, end);
            check("silver-side combined dungeon keeps its own level and designated difficulty with gold absent",
                !service.TryValidateEntry(4123, 49, 1, start, out _, out _)
                && service.TryValidateEntry(4123, 50, 1, start, out var silverDifficulty, out _)
                && silverDifficulty == 2 && service.GoldSilverConfiguration.Gold == null);
            var beforeInvalid = service.Revision;
            var rejected = false;
            try { service.SetWindow(3700, start, start); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check("invalid interval cannot replace a valid window or advance its revision",
                rejected && service.Revision == beforeInvalid && service.GetSnapshot(3700, start).IsOpen);
            var closeRevision = service.Close(3700);
            check("closing one activity is idempotent and does not close the other",
                service.Close(3700) == closeRevision && !service.GetSnapshot(3700, start).IsOpen
                && service.GetSnapshot(4123, start).IsOpen);

            service.SetWindow(3700, now, now.AddDays(60));
            var longStart = service.GetSnapshot(3700, now);
            var longNext = service.GetSnapshot(3700, longStart.NextTransitionUtc.Value);
            check("long windows renew bounded client milliseconds budgets without extending the server window",
                longStart.IsOpen && longStart.RemainingSeconds > 0
                && longStart.RemainingSeconds <= EventDungeonOpeningService.MaximumWireSeconds
                && checked(longStart.RemainingSeconds * 1000) > 0
                && longNext.IsOpen && longNext.ProjectionSegment > longStart.ProjectionSegment
                && longNext.Revision == longStart.Revision
                && longNext.RemainingSeconds <= EventDungeonOpeningService.MaximumWireSeconds
                && !service.GetSnapshot(3700, now.AddDays(60)).IsOpen);

            DungeonFile mutableDungeon = SyntheticDungeon(3700);
            var mutableSchedule = DailyScheduleConfigFile.Parse(ScheduleConfig(TauDescriptor + "\n" + SilverDescriptor));
            var mutableSilver = GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig());
            var mutable = new EventDungeonOpeningService(_ => mutableDungeon, () => mutableSilver, () => mutableSchedule);
            mutable.SetWindow(3700, now, now.AddMinutes(1));
            mutable.SetWindow(4123, now, now.AddMinutes(1));
            check("a valid published opening is revalidated against the current definition at request time",
                mutable.GetSnapshot(3700, now).IsOpen);
            mutableDungeon = null;
            check("missing DGN closes UI and entry even with a live configured window",
                !mutable.GetSnapshot(3700, now).DefinitionValid && !mutable.GetSnapshot(3700, now).IsOpen
                && !mutable.TryValidateEntry(3700, 50, 1, now, out _, out _));
            mutableDungeon = SyntheticDungeon(3700);
            mutableDungeon.DesignateDungeonDifficulty = new[] { 0, 1 };
            check("ambiguous designated difficulty cannot publish an open or admit entry",
                !mutable.GetSnapshot(3700, now).IsOpen && !mutable.TryValidateEntry(3700, 50, 1, now, out _, out _));
            mutableDungeon.DesignateDungeonDifficulty = new[] { 256 };
            check("out-of-wire-range designated difficulty fails closed",
                !mutable.GetSnapshot(3700, now).DefinitionValid);
            mutableDungeon = SyntheticDungeon(4123);
            mutableSchedule = DailyScheduleConfigFile.Parse(ScheduleConfig(TauDescriptor));
            check("resource schedule membership is required independently of a manual open window",
                !mutable.GetSnapshot(4123, now).IsOpen && !mutable.TryValidateEntry(4123, 50, 1, now, out _, out _));
            mutableSchedule = DailyScheduleConfigFile.Parse(ScheduleConfig(TauDescriptor + "\n" + SilverDescriptor));
            mutableSilver = GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig().Replace("4123\n", "808\n"));
            check("a differently configured silver dungeon does not inherit the combined dungeon opening",
                !mutable.GetSnapshot(4123, now).DefinitionValid);
            mutableSilver = GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig(
                "[dungeon index]\n4123\n[max enter count]\n3\n[dungeon enter cost]\n1 10\n[/dungeon enter cost]\n"));
            check("unproven entry-cost pairs do not silently admit an uncharged gold/silver entry",
                mutableSilver.IsValid && !mutable.TryValidateEntry(4123, 50, 1, now, out _, out var costReason)
                && costReason == "gold_silver_entry_cost_not_supported");
            var throwing = new EventDungeonOpeningService(_ => throw new IOException("missing resource"),
                () => GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig()),
                () => DailyScheduleConfigFile.Parse(ScheduleConfig(TauDescriptor + "\n" + SilverDescriptor)));
            throwing.SetWindow(3700, now, now.AddMinutes(1));
            check("resource-provider failures fail closed without a fabricated default dungeon",
                !throwing.GetSnapshot(3700, now).IsOpen && !throwing.TryValidateEntry(3700, 50, 1, now, out _, out _));

            check("opening configuration accepts explicit-zone absolute timestamps without changing process configuration",
                OpeningWindow.TryParseConfiguration("2026-10-04T10:00:00Z", "2026-10-04T18:05:00+08:00",
                    "START", "END", "Activity", out var parsedStart, out var parsedEnd, out _)
                && parsedStart == now && parsedEnd == now.AddMinutes(5));
            foreach (var pair in new[] { ("2026-10-04T10:00:00", "2026-10-04T10:05:00Z"),
                         ("2026-10-04T10:00:00Z", ""), ("", "2026-10-04T10:05:00Z"),
                         ("2026-10-04T10:00:00Z", "2026-10-04T09:59:59Z") })
                check("invalid opening configuration does not publish a partial interval: " + pair,
                    !OpeningWindow.TryParseConfiguration(pair.Item1, pair.Item2, "START", "END", "Activity",
                        out var invalidStart, out var invalidEnd, out var error)
                    && invalidStart == null && invalidEnd == null && !string.IsNullOrWhiteSpace(error));
        }

        private static void VerifyEntryIntegration(Action<string, bool> check)
        {
            using var fixture = new EntryFixture();
            var session = fixture.Session;
            var handler = fixture.Handler;
            var request = new SelectDungeonRequest(3700, 3, 5, 7, 0xFFFE, 2, true);
            var original = request;
            var ticketBefore = fixture.TicketCount;
            var recordBefore = fixture.Data.Number("SELECT COUNT(*) FROM dungeon_limit_records;");
            check("closed activity request preserves wire fields and does not debit a ticket or count",
                !handler.TryNormalizeEventDungeonRequest(session, ref request, out var closed)
                && closed == "activity_window_closed" && SameRequest(request, original)
                && fixture.TicketCount == ticketBefore
                && fixture.Data.Number("SELECT COUNT(*) FROM dungeon_limit_records;") == recordBefore);
            fixture.Open(3700);
            session.Player.Level = 26;
            check("actual SELECT_DUNGEON gate rejects a below-minimum tau character without normalization",
                !handler.TryNormalizeEventDungeonRequest(session, ref request, out var low)
                && low == "minimum_level_not_met" && SameRequest(request, original));
            session.Player.Level = 27;
            check("actual SELECT_DUNGEON gate derives tau difficulty while preserving all other request fields",
                handler.TryNormalizeEventDungeonRequest(session, ref request, out _)
                && request.Difficulty == 0 && SameRequest(request, new SelectDungeonRequest(3700, 0, 5, 7, 0xFFFE, 2, true))
                && fixture.TicketCount == ticketBefore);
            var created = fixture.Parties.CreateParty(new PartyMember { UserId = session.Player.UserId,
                CharacterId = session.Player.CharacterId, SessionId = session.SessionId });
            fixture.Parties.Join(created.Party.PartyId, new PartyMember { UserId = 12346,
                CharacterId = fixture.Data.CharacterId + 1, SessionId = Guid.NewGuid() });
            request = original;
            check("actual SELECT_DUNGEON gate reads the current party roster rather than assuming solo",
                !handler.TryNormalizeEventDungeonRequest(session, ref request, out var grouped)
                && grouped == "party_count_not_allowed" && SameRequest(request, original));
            fixture.Parties.Leave(12346);
            fixture.Parties.Leave(session.Player.UserId);
            fixture.Open(4123);
            session.Player.Level = 50;
            request = new SelectDungeonRequest(4123, 0, 6, 8);
            check("combined gold/silver SELECT_DUNGEON normalizes its current resource difficulty without spending an entry",
                handler.TryNormalizeEventDungeonRequest(session, ref request, out _)
                && request.Difficulty == 2 && request.Flag1 == 6 && request.Flag2 == 8
                && fixture.Data.Number("SELECT COUNT(*) FROM dungeon_limit_records;") == recordBefore);
            fixture.Data.Sql("UPDATE dungeon_limit_config SET limit_count=2 WHERE dgn_id=4123;");
            request = new SelectDungeonRequest(4123, 0, 6, 8);
            original = request;
            check("ETC and existing durable count configuration disagreement rejects the real entry gate",
                !handler.TryNormalizeEventDungeonRequest(session, ref request, out var mismatch)
                && mismatch == "gold_silver_count_definition_mismatch" && SameRequest(request, original));
            fixture.Data.Sql("UPDATE dungeon_limit_config SET limit_count=3, enabled=0 WHERE dgn_id=4123;");
            check("disabled or missing durable count configuration cannot admit combined gold/silver",
                !handler.TryNormalizeEventDungeonRequest(session, ref request, out mismatch)
                && mismatch == "gold_silver_count_definition_mismatch" && SameRequest(request, original));
            request = new SelectDungeonRequest(8014, 4, 9, 10);
            original = request;
            check("the new entry gate does not alter an unrelated old activity request",
                handler.TryNormalizeEventDungeonRequest(session, ref request, out _) && SameRequest(request, original));
        }

        private static EventDungeonOpeningService CreateOpeningService()
            => new(SyntheticDungeon, () => GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig()),
                () => DailyScheduleConfigFile.Parse(ScheduleConfig(TauDescriptor + "\n" + SilverDescriptor)));

        private static DungeonFile SyntheticDungeon(int dungeonId)
            => DungeonFile.Parse("[minimum required level]\n" + (dungeonId == 3700 ? 27 : 50)
                + "\n[limit party count]\n1\n[designate dungeon difficulty]\n"
                + (dungeonId == 3700 ? 0 : 2) + "\n[/designate dungeon difficulty]\n");

        private static bool SameRequest(SelectDungeonRequest left, SelectDungeonRequest right)
            => left.DungeonId == right.DungeonId && left.Difficulty == right.Difficulty
                && left.Flag1 == right.Flag1 && left.Flag2 == right.Flag2 && left.A21Sentinel == right.A21Sentinel
                && left.TrailingLength == right.TrailingLength && left.HasNonZeroTrailingBytes == right.HasNonZeroTrailingBytes;

        private static void VerifyDurableCounts(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var now = new DateTime(2026, 10, 4, 21, 59, 59, DateTimeKind.Utc);
            var service = new DungeonEntryLimitService(fixture.Database, () => now);
            var initial = service.LoadSpecialDungeonLimits(1, fixture.CharacterId).Single(row => row.DungeonId == 4123);
            check("new gold/silver uses the existing character-scoped three-entry owner",
                initial.ScopeType == DungeonLimitScopeType.Character && initial.LimitCount == 3
                && initial.CurrentCount == 3 && initial.UsedCount == 0);
            var rowsBeforeRead = fixture.Number("SELECT COUNT(*) FROM dungeon_limit_records;");
            service.TryCheckSpecialDungeonLimit(1, fixture.CharacterId, 4123, 1, out _);
            check("entry projection and validation do not consume a try",
                fixture.Number("SELECT COUNT(*) FROM dungeon_limit_records;") == rowsBeforeRead);
            check("first accepted entry commits one used try",
                service.TryConsumeSpecialDungeonLimit(1, fixture.CharacterId, 4123, 1, out var first)
                && first.CurrentCount == 2 && first.UsedCount == 1);
            var relog = new DungeonEntryLimitService(fixture.Database, () => now);
            check("relogin observes the persisted count and another character keeps its own tries",
                relog.LoadSpecialDungeonLimits(1, fixture.CharacterId).Single(row => row.DungeonId == 4123).CurrentCount == 2
                && relog.LoadSpecialDungeonLimits(1, fixture.CharacterId + 1).Single(row => row.DungeonId == 4123).CurrentCount == 3);
            check("the third accepted entry exhausts this character",
                relog.TryConsumeSpecialDungeonLimit(1, fixture.CharacterId, 4123, 2, out var third)
                && third.CurrentCount == 0 && third.UsedCount == 3);
            check("a fourth entry fails without changing the durable count",
                !relog.TryConsumeSpecialDungeonLimit(1, fixture.CharacterId, 4123, 1, out var rejected)
                && !rejected.Allowed && rejected.Reason == "entry_limit_exhausted"
                && relog.LoadSpecialDungeonLimits(1, fixture.CharacterId).Single(row => row.DungeonId == 4123).UsedCount == 3);
            now = now.AddSeconds(1);
            var nextDay = relog.LoadSpecialDungeonLimits(1, fixture.CharacterId).Single(row => row.DungeonId == 4123);
            check("the existing Beijing 06:00 boundary restores the next day's tries without a timer",
                nextDay.CurrentCount == 3 && nextDay.UsedCount == 0 && nextDay.DayId == DailyResetService.TodayId(now));
            using (var connection = fixture.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                check("entry-count owner supports the existing admission transaction",
                    relog.TryConsumeSpecialDungeonLimit(connection, transaction, 1, fixture.CharacterId, 4123, 1, out var inFlight)
                    && inFlight.CurrentCount == 2);
                transaction.Rollback();
            }
            check("rolled-back admission does not spend a next-day try",
                relog.LoadSpecialDungeonLimits(1, fixture.CharacterId).Single(row => row.DungeonId == 4123).CurrentCount == 3);
        }

        private static void VerifyCountProjection(Action<string, bool> check)
        {
            var definition = GoldSilverHornDungeonConfigFile.Parse(GoldSilverConfig());
            SpecialDungeonEntryLimitSnapshot Row() => new()
            {
                DungeonId = 4123, ScopeType = DungeonLimitScopeType.Character,
                LimitCount = 3, UsedCount = 1, CurrentCount = 2,
            };
            check("count projection reports used silver tries and leaves unconfigured gold at zero",
                GoldSilverHornCountProjection.TryCreate(definition, new[] { Row() },
                    out var gold, out var silver, out _) && gold == 0 && silver == 1);
            var countBody = EventDungeonOpeningBodyBuilder.BuildGoldSilverCounts(2, 1);
            check("gold/silver wire body is exactly gold-used U16 followed by silver-used U16",
                countBody.Length == 4 && BitConverter.ToUInt16(countBody, 0) == 2
                && BitConverter.ToUInt16(countBody, 2) == 1);
            var now = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
            var service = CreateOpeningService(); service.SetWindow(3700, now, now.AddSeconds(9));
            var stateBody = EventDungeonOpeningBodyBuilder.Build(service.GetSnapshot(3700, now));
            var closedBody = EventDungeonOpeningBodyBuilder.BuildClosed(4123);
            check("activity state body is category/state/dungeon/bounded-seconds as four U32 fields",
                stateBody.Length == 16 && BitConverter.ToUInt32(stateBody, 0) == 0
                && BitConverter.ToUInt32(stateBody, 4) == 1 && BitConverter.ToUInt32(stateBody, 8) == 3700
                && BitConverter.ToUInt32(stateBody, 12) == 9 && closedBody.Length == 16
                && BitConverter.ToUInt32(closedBody, 4) == 0 && BitConverter.ToUInt32(closedBody, 8) == 4123
                && BitConverter.ToUInt32(closedBody, 12) == 0);
            var invalidCases = new (string Label, Action<SpecialDungeonEntryLimitSnapshot> Change)[]
            {
                ("wrong durable maximum", row => row.LimitCount = 2),
                ("account scope", row => row.ScopeType = DungeonLimitScopeType.Account),
                ("unsupported scope", row => row.ScopeType = (DungeonLimitScopeType)7),
                ("negative used", row => row.UsedCount = -1),
                ("used above max", row => row.UsedCount = 4),
                ("unrepresentable extras", row => row.ExtraCount = 1),
                ("remaining mismatch", row => row.CurrentCount = 1),
            };
            foreach (var test in invalidCases)
            {
                var row = Row(); test.Change(row);
                check("count projection " + test.Label + " publishes no partial used values",
                    !GoldSilverHornCountProjection.TryCreate(definition, new[] { row },
                        out gold, out silver, out var reason)
                    && gold == 0 && silver == 0 && !string.IsNullOrWhiteSpace(reason));
            }
            check("missing or duplicate configured dungeon cannot publish fabricated counts",
                !GoldSilverHornCountProjection.TryCreate(definition, Array.Empty<SpecialDungeonEntryLimitSnapshot>(),
                    out gold, out silver, out _)
                && !GoldSilverHornCountProjection.TryCreate(definition, new[] { Row(), Row() }, out gold, out silver, out _)
                && !GoldSilverHornCountProjection.TryCreate(definition, null, out gold, out silver, out _));
            var bothDefinition = GoldSilverHornDungeonConfigFile.Parse(WorldMap()
                + "[gold horn dungeon]\n[dungeon index]\n707\n[max enter count]\n2\n[gold horn dungeon]\n"
                + "[silver horn dungeon]\n[dungeon index]\n808\n[max enter count]\n4\n[silver horn dungeon]\n");
            var bothRows = new[]
            {
                new SpecialDungeonEntryLimitSnapshot { DungeonId = 707, ScopeType = DungeonLimitScopeType.Character,
                    LimitCount = 2, UsedCount = 1, CurrentCount = 1 },
                new SpecialDungeonEntryLimitSnapshot { DungeonId = 808, ScopeType = DungeonLimitScopeType.Character,
                    LimitCount = 4, UsedCount = 3, CurrentCount = 1 },
            };
            check("both configured sides use their ETC dungeon IDs rather than an implicit historic gold ID",
                GoldSilverHornCountProjection.TryCreate(bothDefinition, bothRows, out gold, out silver, out _)
                && gold == 1 && silver == 3);
            bothRows[1].CurrentCount = 0;
            check("late silver-side failure does not publish an earlier gold-side partial value",
                !GoldSilverHornCountProjection.TryCreate(bothDefinition, bothRows, out gold, out silver, out _)
                && gold == 0 && silver == 0);
        }

        private static async Task VerifyPublicationAsync(Action<string, bool> check)
        {
            using var fixture = new PublicationFixture();
            var session = fixture.CreateSession();
            fixture.Coordinator.Start();
            using (await fixture.Transitions.AcquireIfCurrentAsync(session))
                await fixture.Coordinator.NotifyStateOnLoginWhileTransitionHeldAsync(session).WaitAsync(TimeSpan.FromSeconds(2));
            check("login under the existing transition gate projects two entry states and owned counts without a second gate",
                fixture.Packets.Count == 3 && ReadDungeon(fixture.Packets[0]) == 3700
                && ReadDungeon(fixture.Packets[1]) == 4123 && ReadState(fixture.Packets[0]) == 0
                && ReadState(fixture.Packets[1]) == 0 && Type(fixture.Packets[2]) == (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO
                && fixture.Packets[2].Length == 19 && BitConverter.ToUInt16(fixture.Packets[2], 17) == 0);
            await fixture.Coordinator.RefreshAsync();
            check("equivalent same-generation state and count snapshots are not published twice", fixture.Packets.Count == 3);
            var starts = fixture.Now;
            var ends = starts.AddSeconds(9);
            await fixture.Coordinator.OpenAsync(3700, starts, ends);
            await fixture.Coordinator.OpenAsync(4123, starts, ends);
            var opened = fixture.Packets.Count;
            await fixture.Coordinator.OpenAsync(3700, starts, ends);
            await fixture.Coordinator.RefreshAsync();
            check("opening both windows publishes current states once per revision and repeated equivalent opens are idempotent",
                opened == 9 && fixture.Packets.Count == opened && ReadState(fixture.Packets[opened - 3]) == 1
                && ReadState(fixture.Packets[opened - 2]) == 1 && BitConverter.ToInt32(fixture.Packets[opened - 2], 27) == 9);
            using (await fixture.Transitions.AcquireIfCurrentAsync(session))
            {
                check("committed real admission consumes one existing count before its projection",
                    fixture.EntryLimits.TryConsumeSpecialDungeonLimit(1, fixture.Data.CharacterId, 4123, 1, out var consumed)
                    && consumed.UsedCount == 1);
                await fixture.Coordinator.NotifyCountsAfterEntryWhileTransitionHeldAsync(session).WaitAsync(TimeSpan.FromSeconds(2));
            }
            check("entry-count refresh emits one used-count NOTI without replaying gameplay or opening states",
                fixture.Packets.Count == opened + 1 && Type(fixture.Packets[^1]) == (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO
                && BitConverter.ToUInt16(fixture.Packets[^1], 15) == 0 && BitConverter.ToUInt16(fixture.Packets[^1], 17) == 1);
            var beforeReads = fixture.Data.Number("SELECT SUM(used_count) FROM dungeon_limit_records;");
            await fixture.Coordinator.RefreshAsync();
            await fixture.Coordinator.RefreshCountsWhileTransitionHeldAsync(session);
            check("state/count refresh does not consume another try or repeat an unchanged count",
                fixture.Packets.Count == opened + 1
                && fixture.Data.Number("SELECT SUM(used_count) FROM dungeon_limit_records;") == beforeReads);
            fixture.Now = ends;
            var revision = fixture.Service.Revision;
            await fixture.Coordinator.OnWindowBoundaryAsync(revision);
            await fixture.Coordinator.OnWindowBoundaryAsync(revision);
            check("exact expiration projects one pair of closed states even with no clock timer",
                fixture.Packets.Count == opened + 3 && ReadState(fixture.Packets[^2]) == 0 && ReadState(fixture.Packets[^1]) == 0);
            var oldRevision = revision;
            await fixture.Coordinator.OpenAsync(3700, fixture.Now, fixture.Now.AddMinutes(5));
            var newerCount = fixture.Packets.Count;
            await fixture.Coordinator.OnWindowBoundaryAsync(oldRevision);
            check("obsolete window callbacks cannot overwrite a newer entry state",
                fixture.Service.GetSnapshot(3700, fixture.Now).IsOpen && fixture.Packets.Count == newerCount);
            var beforeExpired = fixture.Service.Revision;
            var rejected = false;
            try { await fixture.Coordinator.OpenAsync(4123, fixture.Now.AddMinutes(-1), fixture.Now); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check("expired management open cannot mutate windows or publish gameplay success",
                rejected && fixture.Service.Revision == beforeExpired && fixture.Packets.Count == newerCount
                && fixture.Packets.All(packet => packet[0] == 0
                    && (Type(packet) == (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE
                        || Type(packet) == (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO)));

            using var daily = new PublicationFixture();
            daily.CreateSession();
            daily.Now = new DateTimeOffset(2026, 10, 4, 21, 59, 59, TimeSpan.Zero);
            daily.EntryLimits.TryConsumeSpecialDungeonLimit(1, daily.Data.CharacterId, 4123, 2, out _);
            await daily.Coordinator.RefreshAsync();
            var priorDayCount = daily.Packets.Count;
            daily.Now = daily.Now.AddSeconds(1);
            await daily.Coordinator.OnWindowBoundaryAsync(daily.Service.Revision);
            check("next Beijing 06:00 count projection comes from the existing owner without a second daily ledger",
                daily.Packets.Count == priorDayCount + 1 && Type(daily.Packets[^1]) == (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO
                && BitConverter.ToUInt16(daily.Packets[^1], 17) == 0);
        }

        private static async Task VerifyPublicationFencesAsync(Action<string, bool> check)
        {
            using (var queued = new PublicationFixture())
            {
                var session = queued.CreateSession();
                Task pending;
                using (await queued.Transitions.AcquireIfCurrentAsync(session))
                {
                    pending = queued.Coordinator.RefreshAsync();
                    session.GameSession = CreateGameSessionIdentity();
                }
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                check("a notification queued at the transition gate rejects the prior character selection identity", queued.Packets.Count == 0);
                await queued.Coordinator.RefreshAsync();
                check("the new GameSession receives an independent entry/count projection", queued.Packets.Count == 3);
                session.GameSession = CreateGameSessionIdentity();
                await queued.Coordinator.RefreshAsync();
                check("same-character reselection is not suppressed by the old generation receipt", queued.Packets.Count == 6);
            }
            var fences = new (string Label, Action<PublicationFixture, EnhancedClientSession> Mutation)[]
            {
                ("account reference replacement", (_, session) => session.Account = new AccountRecord { AccountId = 1 }),
                ("account ID mutation", (_, session) => session.Account.AccountId = 2),
                ("network UID mutation", (_, session) => session.Player.UserId++),
                ("character identity mutation", (_, session) => session.Player.CharacterId++),
                ("GameSession replacement", (_, session) => session.GameSession = CreateGameSessionIdentity()),
                ("run generation change", (_, session) => session.Player.NextDungeonRunGeneration()),
                ("selection generation change", (_, session) => session.Player.BeginDungeonSelection(
                    new DungeonTownReturnAnchor(1, 1, 0, 0, 0, 0))),
                ("window revision change", (fixture, _) => fixture.Service.Close(3700)),
                ("directory session replacement", (fixture, _) => fixture.CreateSession(register: true)),
            };
            foreach (var fence in fences)
            {
                using var fixture = new PublicationFixture();
                var session = fixture.CreateSession();
                fixture.Service.SetWindow(3700, fixture.Now, fixture.Now.AddMinutes(1));
                fixture.BeforeWrite = () => fence.Mutation(fixture, session);
                await fixture.Coordinator.RefreshAsync();
                check("prepared batch revalidates " + fence.Label + " before its first frame", fixture.Packets.Count == 0);
            }
            using (var betweenFrames = new PublicationFixture())
            {
                var session = betweenFrames.CreateSession();
                betweenFrames.AfterFirstWrite = () => session.GameSession = CreateGameSessionIdentity();
                await betweenFrames.Coordinator.RefreshAsync();
                check("a generation change between frames stops the remainder of the batch", betweenFrames.Packets.Count == 1);
                await betweenFrames.Coordinator.RefreshAsync();
                check("a partial old-generation batch records no success receipt and the new source receives all fields", betweenFrames.Packets.Count == 4);
            }
            using (var deadline = new PublicationFixture())
            {
                deadline.CreateSession();
                deadline.Service.SetWindow(3700, deadline.Now, deadline.Now.AddSeconds(1));
                deadline.BeforeWrite = () => deadline.Now = deadline.Now.AddSeconds(1);
                await deadline.Coordinator.RefreshAsync();
                check("window ending after preparation rejects the queued open frame", deadline.Packets.Count == 0);
                await deadline.Coordinator.RefreshAsync();
                check("a later refresh publishes the authoritative expiration", deadline.Packets.Count == 3 && ReadState(deadline.Packets[0]) == 0);
            }
            using (var day = new PublicationFixture())
            {
                day.CreateSession(); day.Now = new DateTimeOffset(2026, 10, 4, 21, 59, 59, TimeSpan.Zero);
                day.BeforeWrite = () => day.Now = day.Now.AddSeconds(1);
                await day.Coordinator.RefreshAsync();
                check("a prepared prior-day count is rejected at the exact durable reset boundary", day.Packets.Count == 0);
                await day.Coordinator.RefreshAsync();
                check("fresh reset-day projection can be retried", day.Packets.Count == 3);
            }
            using (var rollback = new PublicationFixture())
            {
                rollback.CreateSession();
                var starts = rollback.Now;
                rollback.Service.SetWindow(3700, starts, starts.AddSeconds(1));
                var invalidObserved = false;
                rollback.AfterFirstWriteProbe = guard =>
                {
                    rollback.Now = starts.AddSeconds(1);
                    invalidObserved = !guard();
                    rollback.Now = starts;
                };
                await rollback.Coordinator.RefreshAsync();
                check("a batch whose guard observed expiration cannot resurrect after the clock moves backward",
                    invalidObserved && rollback.Packets.Count == 1);
                await rollback.Coordinator.RefreshAsync();
                check("the invalidated batch commits no receipt even after the clock returns to its former time",
                    rollback.Packets.Count == 4);
            }
            using (var readFence = new PublicationFixture())
            {
                var session = readFence.CreateSession();
                readFence.BeforeRead = () => session.GameSession = CreateGameSessionIdentity();
                await readFence.Coordinator.RefreshAsync();
                check("database read completion revalidates its captured GameSession before acquiring the send lock", readFence.Packets.Count == 0);
                await readFence.Coordinator.RefreshAsync();
                await readFence.Sessions.UnregisterAsync(readFence.Data.CharacterId, session);
                readFence.Sessions.Register(readFence.Data.CharacterId, session);
                await readFence.Coordinator.RefreshAsync();
                check("session ending clears receipt state for a later registered lifecycle", readFence.Packets.Count == 6);
                var beforeDispose = readFence.Packets.Count;
                readFence.Coordinator.Dispose();
                await readFence.Coordinator.RefreshAsync();
                await readFence.Coordinator.OnWindowBoundaryAsync(readFence.Service.Revision);
                check("disposed projection owner cannot write from a later refresh or timer callback", readFence.Packets.Count == beforeDispose);
            }
            using (var preparation = new PublicationFixture())
            {
                var session = preparation.CreateSession();
                preparation.BeforePrepare = () => session.GameSession = CreateGameSessionIdentity();
                await preparation.Coordinator.RefreshAsync();
                check("send-lock preparation rejects a replaced GameSession before producing any packet", preparation.Packets.Count == 0);
            }
        }

        private static async Task VerifyPublicationFailuresAsync(Action<string, bool> check)
        {
            using (var transport = new PublicationFixture())
            {
                transport.CreateSession(); transport.FailNextWrite = true;
                await transport.Coordinator.RefreshAsync();
                check("transport failure records no count or state receipt", transport.Packets.Count == 0);
                await transport.Coordinator.RefreshAsync();
                check("an authoritative full projection retries after the failed transport", transport.Packets.Count == 3);
            }
            using (var partial = new PublicationFixture())
            {
                partial.CreateSession(); partial.AfterFirstWrite = () => partial.FailNextWrite = true;
                await partial.Coordinator.RefreshAsync();
                await partial.Coordinator.RefreshAsync();
                check("a transport failure after one frame retries the whole projection without a partial receipt", partial.Packets.Count == 4);
            }
            using (var failedRead = new PublicationFixture())
            {
                var session = failedRead.CreateSession();
                failedRead.Service.SetWindow(3700, failedRead.Now, failedRead.Now.AddMinutes(1));
                failedRead.Service.SetWindow(4123, failedRead.Now, failedRead.Now.AddMinutes(1));
                failedRead.OverrideLimits = (_, _) => throw new IOException("count fixture failure");
                await failedRead.Coordinator.RefreshAsync();
                check("failed count read keeps tau entry independent, closes combined entry and emits no fake zero-count NOTI",
                    failedRead.Packets.Count == 2 && ReadState(failedRead.Packets[0]) == 1
                    && ReadState(failedRead.Packets[1]) == 0
                    && failedRead.Packets.All(packet => Type(packet) != (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO));
                failedRead.OverrideLimits = null;
                await failedRead.Coordinator.RefreshCountsWhileTransitionHeldAsync(session);
                check("count-owner recovery through its normal count refresh reopens combined entry and publishes its real count",
                    failedRead.Packets.Count == 5 && ReadState(failedRead.Packets[3]) == 1);
            }
            using (var mismatch = new PublicationFixture())
            {
                mismatch.CreateSession();
                mismatch.Service.SetWindow(4123, mismatch.Now, mismatch.Now.AddMinutes(1));
                mismatch.OverrideLimits = (account, character) => mismatch.EntryLimits.LoadSpecialDungeonLimits(account, character)
                    .Select(row => row.DungeonId == 4123 ? new SpecialDungeonEntryLimitSnapshot
                    {
                        DungeonId = 4123, DayId = row.DayId, ScopeType = DungeonLimitScopeType.Account,
                        LimitCount = 3, CurrentCount = 3, UsedCount = 0,
                    } : row).ToArray();
                await mismatch.Coordinator.RefreshAsync();
                check("durable scope mismatch cannot display an open combined gate or a fake remaining count",
                    mismatch.Packets.Count == 2 && ReadState(mismatch.Packets[1]) == 0);
            }
        }

        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);
        private static int ReadState(byte[] packet) => BitConverter.ToInt32(packet, 19);
        private static int ReadDungeon(byte[] packet) => BitConverter.ToInt32(packet, 23);

        private static bool RejectsMutation(Action mutation)
        {
            try { mutation(); return false; }
            catch (NotSupportedException) { return true; }
        }

        private static int[] Numbers(string raw) => raw.Split(new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value,
                System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        private static string WorldMap() => "[world map index]\n14\n[/world map index]\n";
        private static string SilverBlock(string fields = "[dungeon index]\n4123\n[max enter count]\n3\n")
            => "[silver horn dungeon]\n" + fields + "[silver horn dungeon]\n";
        private static string GoldSilverConfig(string fields = "[dungeon index]\n4123\n[max enter count]\n3\n")
            => WorldMap() + SilverBlock(fields);
        private static string ScheduleConfig(string rows) => "[dungeon]\n" + rows + "\n[/dungeon]\n";
        private static string Required(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content)) throw new FileNotFoundException(path);
            return content;
        }

        private sealed class PublicationFixture : IDisposable
        {
            internal readonly Fixture Data = new();
            internal readonly EventDungeonOpeningService Service = CreateOpeningService();
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly DungeonEntryLimitService EntryLimits;
            internal readonly EventDungeonOpeningCoordinator Coordinator;
            internal readonly List<byte[]> Packets = new();
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            internal DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
            internal Action BeforeRead, BeforePrepare, BeforeWrite, AfterFirstWrite;
            internal Action<Func<bool>> AfterFirstWriteProbe;
            internal bool FailNextWrite;
            internal Func<int, int, IReadOnlyList<SpecialDungeonEntryLimitSnapshot>> OverrideLimits;

            internal PublicationFixture()
            {
                Transitions = new CharacterTransitionCoordinator(Sessions);
                EntryLimits = new DungeonEntryLimitService(Data.Database, () => Now.UtcDateTime);
                Coordinator = new EventDungeonOpeningCoordinator(Service, Sessions, Transitions,
                    clock: null, entryLimits: EntryLimits, utcNow: () => Now,
                    sendBatch: Send, loadLimits: LoadLimits);
            }

            internal EnhancedClientSession CreateSession(bool register = true)
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 1 };
                session.Player.CharacterId = Data.CharacterId; session.Player.UserId = 12345;
                session.Player.Level = 50; session.GameSession = CreateGameSessionIdentity();
                _ownedSessions.Add(session);
                if (register) Sessions.Register(Data.CharacterId, session);
                return session;
            }

            private IReadOnlyList<SpecialDungeonEntryLimitSnapshot> LoadLimits(int account, int character)
            {
                var action = BeforeRead; BeforeRead = null; action?.Invoke();
                return OverrideLimits != null ? OverrideLimits(account, character)
                    : EntryLimits.LoadSpecialDungeonLimits(account, character);
            }

            private Task Send(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare, Func<bool> guard)
                => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(_sendLock, () =>
                {
                    var action = BeforePrepare; BeforePrepare = null; action?.Invoke();
                    var packets = prepare();
                    action = BeforeWrite; BeforeWrite = null; action?.Invoke();
                    return packets;
                }, packet =>
                {
                    if (FailNextWrite) { FailNextWrite = false; throw new IOException("event entry transport fixture"); }
                    Packets.Add(packet.ToArray());
                    var action = AfterFirstWrite; AfterFirstWrite = null; action?.Invoke();
                    var probe = AfterFirstWriteProbe; AfterFirstWriteProbe = null; probe?.Invoke(guard);
                    return Task.CompletedTask;
                }, guard);

            public void Dispose()
            {
                Coordinator.Dispose();
                foreach (var session in _ownedSessions) session.Close();
                _sendLock.Dispose(); Data.Dispose();
            }
        }

        private sealed class EntryFixture : IDisposable
        {
            internal readonly Fixture Data = new();
            internal readonly PartyManager Parties = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly EventDungeonOpeningService Service = CreateOpeningService();
            internal readonly DungeonEntryHandler Handler;
            internal readonly EnhancedClientSession Session;
            private readonly InventoryLease _lease;
            internal int TicketCount => _lease.Inventory.CountMainItem(690001556);

            internal EntryFixture()
            {
                var records = new SqliteCharacterRepository(Data.Database);
                var daily = new DailyResetService(Data.Database);
                var source = new SqliteSelectCharacterDataSource(Data.Database, records, dailyResetService: daily);
                var refresh = new InventoryRefreshSender(source, records, Data.Database);
                refresh.BindSessions(Sessions);
                var services = new DungeonSharedServices(new ReviveCoinService(daily), records, source, null,
                    Data.Database.ConnectionString, refresh, partyManager: Parties, sessions: Sessions,
                    database: Data.Database, dailyResetService: daily);
                services.ConfigureEventDungeons(Service, _ => Task.CompletedTask);
                Handler = new DungeonEntryHandler(services, null);
                Session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                Session.Account = new AccountRecord { AccountId = 1 };
                Session.Player.CharacterId = Data.CharacterId; Session.Player.UserId = 12345;
                Session.Player.Level = 27;
                Session.GameSession = CreateGameSessionIdentity();
                Sessions.Register(Data.CharacterId, Session);
                _lease = InventoryContext.Register(Session.SessionId,
                    new InventoryService(Data.CharacterId, 1, Data.Database));
                if (!InventoryRewardGrantService.TryCreateAndInsert(_lease, 690001556,
                        ItemCreateReason.Unknown, 2, out _)
                    || !OnlineInventoryMutationCommitCoordinator.TryCommit(_lease, "event-dungeon-opening-fixture"))
                    throw new InvalidOperationException("Current tau ticket fixture could not be persisted.");
            }

            internal void Open(int dungeonId)
            {
                var now = DateTimeOffset.UtcNow;
                Service.SetWindow(dungeonId, now.AddMinutes(-1), now.AddMinutes(5));
            }

            public void Dispose()
            {
                InventoryContext.Unregister(Session.SessionId);
                Session.Close(); Data.Dispose();
            }
        }

        private static GameSession CreateGameSessionIdentity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));

        private sealed class Fixture : IDisposable
        {
            private static int _nextCharacter = 76000000;
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "event-dungeon-opening-selftest-" + Guid.NewGuid().ToString("N")));
            internal readonly int CharacterId = Interlocked.Add(ref _nextCharacter, 2);
            internal readonly GameDatabase Database;
            internal Fixture()
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "event-entry.db"), ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'event-dungeon-entry','');"
                    + $"INSERT INTO characters(character_id,account_id,name,job,level) VALUES({CharacterId},1,'event-entry-a',0,50),"
                    + $"({CharacterId + 1},1,'event-entry-b',0,50);");
            }
            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = sql; command.ExecuteNonQuery();
            });
            internal long Number(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            });
            public void Dispose()
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: false);
            }
        }
    }
}
