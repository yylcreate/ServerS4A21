using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events;
using DfoServer.Game.Events.EpicRoad;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class EpicRoadEntrySelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== EPIC_ROAD_ENTRY selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
                if (!passed) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("current PVF route", () => VerifyCurrentResources(Check));
            Section("native EpicRoad monsters versus manual hell admission", () => VerifyHellModeAdmission(Check));
            Section("typed cross-resource route rejection", () => VerifyDefinitions(Check));
            Section("catalog authority and transaction", () => VerifyCatalog(Check));
            Section("dungeon admission", () => VerifyAdmission(Check));
            Section("real town handler NPC entry gate", () => VerifyTownEntryGate(Check));
            Section("account counter persistence", () => VerifyCounts(Check));
            Section("account debit revalidates its configured authority", () => VerifyAccountCommitRevalidation(Check));
            Section("account projection and source fences", () => VerifyNotificationAsync(Check).GetAwaiter().GetResult());
            Console.WriteLine($"EPIC_ROAD_ENTRY assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCurrentResources(Action<string, bool> check)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 PVF.", path);
            var definition = EpicRoadEntryDefinition.LoadCurrentPvf();
            check("real NPC1125 resolves to NPC1126 in Town28 area0 gate35",
                definition.IsValid && definition.NeedQuestId == 8598
                && definition.TargetMapPath.Replace('\\', '/').Equals(
                    "Event/16_epicroad_town/16_epicroad_town.map", StringComparison.OrdinalIgnoreCase)
                && definition.TargetNpcX == 441 && definition.TargetNpcY == 177
                && definition.ExitTownRoute == 0);
            check("real 6004 resource defines level17 solo normal difficulty and account maximum10",
                definition.IsValid && definition.MinimumRequiredLevel == 17
                && definition.LimitPartyCount == 1 && definition.Difficulty == 0
                && definition.AccountMaximumCount == 10);
            check("returned permission is an independent value rather than shared mutable PVF state",
                definition.IsValid && MutatingPermissionDoesNotChangeDefinition(definition));
        }

        private static bool MutatingPermissionDoesNotChangeDefinition(EpicRoadEntryDefinition definition)
        {
            var original = definition.TargetPermission;
            original.NeedLevel = 1;
            original.NeedQuest = 0;
            return definition.TargetPermission.NeedLevel == 17
                && definition.TargetPermission.NeedQuest == 8598;
        }

        private static void VerifyHellModeAdmission(Action<string, bool> check)
        {
            var dungeon = DfoServer.GameWorld.Dungeon.GetDungeonFile(6004);
            check("real 6004 uses its three native boss mazes even when the client sends hell flag1",
                dungeon != null && dungeon.Mazes.Count == 3
                && dungeon.Mazes.All(maze => maze.BossMap != null && maze.BossMap.Length >= 2)
                && !DungeonEntryHandler.ShouldUseManualHellParty(6004, false, 1));
            check("ordinary dungeon104 keeps its requested manual hell path",
                DungeonEntryHandler.ShouldUseManualHellParty(104, false, 1));
            check("tutorial and unrequested hell paths remain disabled",
                !DungeonEntryHandler.ShouldUseManualHellParty(104, true, 1)
                && !DungeonEntryHandler.ShouldUseManualHellParty(104, false, 0));
        }

        private static void VerifyDefinitions(Action<string, bool> check)
        {
            check("a complete typed resource route is valid", new DefinitionInputs().Create().IsValid);
            void Reject(string label, Action<DefinitionInputs> mutate)
            {
                var input = new DefinitionInputs();
                mutate(input);
                var invalid = input.Create();
                check(label, !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.AccountMaximumCount == 0);
            }
            Reject("destination NPC cannot be treated as an unrelated town number", input =>
                input.Entrance = Entrance(28));
            Reject("a different event cannot authorize the EpicRoad entry", input =>
                input.Entrance = NpcActivityEntryDefinition.Parse(EntranceSource.Replace("286", "93")));
            Reject("source and destination account-count keys must agree", input =>
                input.Destination = NpcActivityEntryDefinition.Parse(DestinationSource.Replace("6004", "6003")));
            Reject("an unsupported exit route cannot be assumed to restore the source", input =>
                input.Destination = NpcActivityEntryDefinition.Parse(DestinationSource.Replace("`[exit town]` 0", "`[exit town]` 1")));
            Reject("a new party definition requires an explicit account admission design", input => input.Dungeon.LimitPartyCount = 2);
            Reject("a new item fee cannot use separated count and inventory commits", input =>
                input.Dungeon.RequiredItems.Add(new DungeonRequiredItem { ItemId = 690001556, Count = 1, ConsumeOnEntry = true }));
            Reject("town gate mismatch cannot use a fallback route", input => input.Town.Areas[0].LinkedId = 14);
            Reject("duplicate area zero is ambiguous", input => input.Town.Areas.Add(input.Town.Areas[0]));
            Reject("missing resource quest permission cannot silently admit", input => input.Town.Areas[0].Permission = null);
            Reject("missing destination NPC placement cannot synthesize coordinates", input => input.Map.Npcs.Clear());
            Reject("another world-map dungeon cannot reuse the EpicRoad gate", input => input.World.Dungeons.Clear());
            Reject("missing designated difficulty cannot default to normal", input => input.Dungeon.DesignateDungeonDifficulty = null);
            Reject("multiple designated difficulties cannot become one guessed difficulty", input =>
                input.Dungeon.DesignateDungeonDifficulty = new[] { 0, 1 });
            Reject("character-scoped count rows do not replace the account row", input =>
                input.Counts = CountConfig("0 3524 1 0"));
            Reject("an unsupported account maximum is rejected before byte truncation", input =>
                input.Counts = CountConfig("0 6004 256 0"));
        }

        private static void VerifyCatalog(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var service = fixture.Service;
            var db = fixture.Database;
            var repository = new GameEventRepository(db);
            var schema = Scalar(db, "PRAGMA user_version;");
            service.Initialize(null);
            service.Initialize(" ");
            check("missing settings default closed and add exactly one state/detail row",
                !service.IsEnabled() && Scalar(db, "SELECT COUNT(*) FROM game_event_state WHERE event_id=286;") == 1
                && Scalar(db, "SELECT COUNT(*) FROM game_event_info_details WHERE event_id=286;") == 1);
            service.Initialize("1");
            Sql(db, "UPDATE game_event_info_details SET unknown0=42,start_notice='operator',end_notice='saved-end',"
                + "title='saved-title',flag_a=17,sort_order=777 WHERE event_id=286;");
            var metadata = Text(db, "SELECT unknown0||':'||start_notice||':'||end_notice||':'||title||':'||flag_a||':'||sort_order "
                + "FROM game_event_info_details WHERE event_id=286;");
            service.Initialize(null);
            var replacement = new EpicRoadEntryService(db, () => new DefinitionInputs().Create());
            replacement.Initialize(null);
            check("missing settings preserve saved enablement and all existing operator metadata",
                replacement.IsEnabled() && metadata == Text(db, "SELECT unknown0||':'||start_notice||':'||end_notice||':'||title||':'||flag_a||':'||sort_order "
                    + "FROM game_event_info_details WHERE event_id=286;"));
            replacement.Initialize("0");
            replacement.Initialize(null);
            check("explicit disable survives optional initialization", !replacement.IsEnabled());
            replacement.Initialize("1");
            replacement.Initialize("true");
            check("invalid operator settings close a saved enabled entry", !replacement.IsEnabled());
            replacement.Initialize("1");
            new EpicRoadEntryService(db, () => EpicRoadEntryDefinition.Unavailable("bad-fixture")).Initialize("1");
            check("unavailable definitions override explicit enablement and persist closure", !repository.IsEnabled(286));
            replacement.Initialize("1");
            new EpicRoadEntryService(db, () => throw new IOException("fixture resource failure")).Initialize("1");
            check("definition read failures cannot leave an advertised entry enabled", !repository.IsEnabled(286));
            replacement.Initialize("1");
            repository.EnsureCatalogEntry(93, true, "other activity", "other end", 1);
            Sql(db, "INSERT INTO game_event_info_extra(event_id,param7) VALUES(93,777);");
            var snapshot = repository.LoadEventInfoSnapshot();
            var body = EventInfoBodyBuilder.Build(snapshot);
            using (var reader = new BinaryReader(new MemoryStream(body)))
            {
                var ids = new List<ushort>();
                var count = reader.ReadUInt16();
                for (var i = 0; i < count; i++)
                {
                    ids.Add(reader.ReadUInt16());
                    reader.ReadUInt32();
                    reader.ReadBytes(reader.ReadInt32());
                    reader.ReadBytes(reader.ReadInt32());
                    if (reader.ReadByte() != 0) throw new InvalidDataException("Unexpected fixture detail row.");
                }
                var extraCount = reader.ReadByte();
                var extraId = reader.ReadUInt16();
                var values = Enumerable.Range(0, 12).Select(_ => reader.ReadUInt32()).ToArray();
                check("full EVENT_INFO preserves other members, extras and the raid member",
                    ids.OrderBy(id => id).SequenceEqual(new ushort[] { 93, EventInfoBodyBuilder.RaidChannelEventId, 286 }.OrderBy(id => id))
                    && extraCount == 1 && extraId == 93 && values[7] == 777
                    && reader.BaseStream.Position == reader.BaseStream.Length);
            }
            Sql(db, "DELETE FROM game_event_state WHERE event_id=286;"
                + "CREATE TRIGGER reject_epic_detail BEFORE INSERT ON game_event_info_details WHEN NEW.event_id=286 "
                + "BEGIN SELECT RAISE(ABORT,'epic-test-rollback'); END;");
            var rejected = false;
            try { replacement.Initialize("1"); } catch (SqliteException) { rejected = true; }
            check("catalog detail failure rolls back its state insertion and preserves unrelated activity",
                rejected && Scalar(db, "SELECT COUNT(*) FROM game_event_state WHERE event_id=286;") == 0
                && Scalar(db, "SELECT COUNT(*) FROM game_event_info_details WHERE event_id=286;") == 0
                && repository.IsEnabled(93));
            Sql(db, "DROP TRIGGER reject_epic_detail;");
            replacement.Initialize("1");
            check("catalog retry succeeds without schema changes", replacement.IsEnabled() && Scalar(db, "PRAGMA user_version;") == schema);
        }

        private static void VerifyAdmission(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Service.Initialize("1");
            check("level17 solo entry normalizes the requested choice to PVF difficulty0",
                fixture.Service.TryValidateDungeon(17, 1, 28, 0, 3, out var difficulty, out _)
                && difficulty == 0);
            foreach (var item in new (string Label, int Level, int Count, int Town, int Area, byte Difficulty)[]
            {
                ("below minimum level", 16, 1, 28, 0, (byte)0),
                ("party exceeds solo limit", 17, 2, 28, 0, (byte)0),
                ("empty party", 17, 0, 28, 0, (byte)0),
                ("different town", 17, 1, 11, 0, (byte)0),
                ("different area", 17, 1, 28, 1, (byte)0),
                ("unknown difficulty", 17, 1, 28, 0, (byte)255),
            })
            {
                check(item.Label + " cannot start the activity dungeon",
                    !fixture.Service.TryValidateDungeon(item.Level, item.Count, item.Town,
                        item.Area, item.Difficulty, out _, out var reason) && !string.IsNullOrWhiteSpace(reason));
            }
            check("the shared town permission requires the actual cleared quest8598",
                !TownAreaPermissionPolicy.IsSatisfied(fixture.Service.Definition.TargetPermission, 17, _ => false)
                && TownAreaPermissionPolicy.IsSatisfied(fixture.Service.Definition.TargetPermission, 17, id => id == 8598));
            fixture.Service.Initialize("0");
            check("closing the catalog rejects a previously valid route",
                !fixture.Service.TryValidateDungeon(17, 1, 28, 0, 0, out _, out var closed) && closed == "entry_closed");
        }

        private static void VerifyCounts(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            SeedAccounts(fixture.Database);
            var now = new DateTime(2026, 10, 3, 21, 59, 59, DateTimeKind.Utc);
            var limits = new DungeonEntryLimitService(fixture.Database, () => now);
            limits.EnsureAccountDungeonLimit(6004, 10);
            check("both characters initially project the account's same ten-entry budget",
                Projection(limits, 9001, 9101, out var initial) && initial.MaximumCount == 10 && initial.UsedCount == 0
                && initial.RemainingCount == 10
                && Projection(limits, 9001, 9102, out var sibling) && sibling == initial);
            for (var i = 0; i < 10; i++)
            {
                var character = i % 2 == 0 ? 9101 : 9102;
                check("account entry " + (i + 1) + " persists one shared debit",
                    limits.TryConsumeAccountDungeonLimit(9001, character, 6004, 10, out var consumed)
                    && consumed.Allowed && consumed.IsLimited && consumed.UsedCount == i + 1
                    && Projection(limits, 9001, character == 9101 ? 9102 : 9101, out var other)
                    && other.UsedCount == i + 1 && other.RemainingCount == 9 - i);
            }
            check("both account characters are denied the eleventh entry",
                !limits.TryCheckSpecialDungeonLimit(9001, 9101, 6004, 1, out _)
                && !limits.TryConsumeAccountDungeonLimit(9001, 9102, 6004, 10, out _));
            check("another account remains isolated and persistence uses only character zero",
                Projection(limits, 9002, 9201, out var isolated) && isolated.UsedCount == 0 && isolated.RemainingCount == 10
                && Scalar(fixture.Database, "SELECT COUNT(*) FROM dungeon_limit_records WHERE dgn_id=6004 AND character_id<>0;") == 0);
            var restored = new DungeonEntryLimitService(new GameDatabase(
                fixture.Database.DatabasePath, fixture.Database.SchemaFilePath), () => now);
            check("new owners recover the shared exhausted budget", Projection(restored, 9001, 9102, out var exhausted)
                && exhausted.UsedCount == 10 && exhausted.RemainingCount == 0);
            now = now.AddSeconds(1);
            check("the existing project day policy resets at UTC+8 06:00",
                Projection(limits, 9001, 9101, out var nextDay) && nextDay.UsedCount == 0 && nextDay.RemainingCount == 10
                && limits.TryConsumeAccountDungeonLimit(9001, 9102, 6004, 10, out var nextConsume)
                && nextConsume.UsedCount == 1);
            var rollback = false;
            try
            {
                fixture.Database.Write((connection, transaction) =>
                {
                    if (!limits.TryConsumeSpecialDungeonLimit(connection, transaction, 9001, 9101, 6004, 1, out _))
                        throw new InvalidOperationException("Unexpected fixture admission rejection.");
                    throw new IOException("fixture entry transaction rollback");
                });
            }
            catch (IOException) { rollback = true; }
            check("a caller transaction rollback restores the account counter",
                rollback && Projection(limits, 9001, 9102, out var rolledBack)
                && rolledBack.UsedCount == 1 && rolledBack.RemainingCount == 9);
            Sql(fixture.Database, "UPDATE dungeon_limit_config SET limit_count=8,enabled=0,sort_order=77 WHERE dgn_id=6004;");
            limits.EnsureAccountDungeonLimit(6004, 10);
            check("reinitialization preserves an operator's configured count, enablement and order",
                Text(fixture.Database, "SELECT scope_type||':'||limit_count||':'||enabled||':'||sort_order FROM dungeon_limit_config WHERE dgn_id=6004;") == "account:8:0:77");
            Sql(fixture.Database, "UPDATE dungeon_limit_config SET scope_type='charac',limit_count=10,enabled=1 WHERE dgn_id=6004;");
            check("account-scope mismatch provides no authorized account projection", !Projection(limits, 9001, 9101, out _));
            var duplicate = new[] { CountRow(), CountRow() };
            var inconsistent = CountRow();
            inconsistent.UsedCount = 4;
            check("duplicate and inconsistent snapshots cannot advertise extra entries",
                !AccountDungeonCountProjection.TryCreate(6004, 10, duplicate, out _, out _)
                && !AccountDungeonCountProjection.TryCreate(6004, 10, new[] { inconsistent }, out _, out _));
        }

        private static void VerifyTownEntryGate(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            SeedAccounts(fixture.Database);
            var service = new EpicRoadEntryService(fixture.Database);
            service.Initialize("1");
            var handler = new TownHandler(new SqliteCharacterRepository(fixture.Database),
                selectDataSource: null, partyManager: null, sessions: fixture.Sessions,
                refresh: null, dungeonInstances: null, raidManager: null, database: fixture.Database);
            handler.ConfigureEpicRoadEntry(service);
            var source = FindTownArea(service.Definition.EntranceHiddenTownIds, hidden: false);
            var hiddenSource = FindTownArea(service.Definition.EntranceHiddenTownIds, hidden: true);
            var session = fixture.CreateSession();
            session.Player.CurTownId = source.Town;
            session.Player.CurAreaId = source.Area;
            session.Player.TownPresenceReady = true;
            session.Player.UserState = 0;
            check("the real TownHandler uses a valid town area and the NPC's event visibility policy",
                Town.TryGetArea(source.Town, source.Area, out _)
                && service.Definition.EntranceHiddenTownIds.SequenceEqual(new[] { 17, 19 })
                && !handler.TryValidateEpicRoadTownEntry(session, 0, out var missingQuest)
                && !string.IsNullOrWhiteSpace(missingQuest));
            Sql(fixture.Database, "INSERT INTO character_quest_completions(character_id,quest_id,completion_value) VALUES(9101,8598,0);");
            check("an incomplete zero-valued quest record does not authorize the town entry",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            Sql(fixture.Database, "UPDATE character_quest_completions SET completion_value=1 WHERE character_id=9101 AND quest_id=8598;");
            session.Player.Level = 16;
            check("the real town gate still rejects level16 with quest8598 cleared",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Player.Level = 17;
            check("level17 with cleared8598 in a source town permitted by the resource can enter Town28 area0",
                handler.TryValidateEpicRoadTownEntry(session, 0, out var allowed) && allowed == null);
            service.Initialize("0");
            check("closing the persisted event rejects a new town entry",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            service.Initialize("1");
            check("the same NPC does not authorize another target area",
                !handler.TryValidateEpicRoadTownEntry(session, 1, out _));
            session.Player.CurTownId = hiddenSource.Town;
            session.Player.CurAreaId = hiddenSource.Area;
            check("a town hidden by the NPC resource cannot spoof the activity route",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out var wrongSource)
                && wrongSource == "epic_road_town_source_not_allowed");
            session.Player.CurTownId = source.Town;
            session.Player.CurAreaId = byte.MaxValue;
            check("an undefined source area cannot spoof a town NPC entry",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Player.CurAreaId = source.Area;
            var account = session.Account;
            session.Account = null;
            check("a missing account cannot start the activity town route",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Account = account;
            session.Player.TownPresenceReady = false;
            check("an unpublished town presence cannot initiate NPC activity entry",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Player.TownPresenceReady = true;
            session.Player.DungeonSelectionPending = true;
            check("an existing dungeon selection cannot reuse the town route",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Player.DungeonSelectionPending = false;
            session.Player.CurrentRun = new DungeonRun();
            check("a current dungeon run cannot be bypassed through the NPC town gate",
                !handler.TryValidateEpicRoadTownEntry(session, 0, out _));
            session.Player.CurrentRun = null;
            check("read-only gate checks leave dungeon counters and persistent position unchanged",
                Scalar(fixture.Database, "SELECT COUNT(*) FROM dungeon_limit_records WHERE dgn_id=6004;") == 0
                && Text(fixture.Database, "SELECT town_id||':'||area_id FROM characters WHERE character_id=9101;") == "0:0");
        }

        private static (byte Town, byte Area) FindTownArea(IReadOnlyList<int> hiddenTownIds, bool hidden)
        {
            var towns = DungeonCatalog.LoadListFile("town/town.lst");
            foreach (var entry in towns.Entries.OrderBy(candidate => candidate.Id))
            {
                if (entry.Id <= 0 || entry.Id > byte.MaxValue || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;
                if (hiddenTownIds.Contains(entry.Id) != hidden || entry.Id == EpicRoadEntryService.TargetTownId)
                    continue;
                var town = TownFile.Parse(PvfArchiveAccessor.ReadText(ResourcePath("town", entry.FilePath)));
                foreach (var area in town.Areas)
                {
                    if (area.Id < 0 || area.Id > byte.MaxValue || string.IsNullOrWhiteSpace(area.MapPath))
                        continue;
                    return ((byte)entry.Id, (byte)area.Id);
                }
            }
            throw new InvalidDataException("Current PVF has no matching town area for the NPC hide rule: hidden=" + hidden);
        }

        private static string ResourcePath(string category, string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            var prefix = category + "/";
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalized : prefix + normalized;
        }

        private static void VerifyAccountCommitRevalidation(Action<string, bool> check)
        {
            var cases = new (string Label, string Mutation)[]
            {
                ("disabled configuration", "UPDATE dungeon_limit_config SET enabled=0 WHERE dgn_id=6004;"),
                ("removed configuration", "DELETE FROM dungeon_limit_config WHERE dgn_id=6004;"),
                ("changed character scope", "UPDATE dungeon_limit_config SET scope_type='charac' WHERE dgn_id=6004;"),
                ("changed maximum", "UPDATE dungeon_limit_config SET limit_count=9 WHERE dgn_id=6004;"),
                ("unsupported extra entries", "UPDATE dungeon_limit_records SET extra_count=1 WHERE dgn_id=6004;"),
                ("inconsistent existing balance", "UPDATE dungeon_limit_records SET current_count=10 WHERE dgn_id=6004;"),
            };
            foreach (var item in cases)
            {
                using var fixture = new Fixture();
                SeedAccounts(fixture.Database);
                var limits = new DungeonEntryLimitService(fixture.Database);
                limits.EnsureAccountDungeonLimit(6004, 10);
                if (!limits.TryConsumeAccountDungeonLimit(9001, 9101, 6004, 10, out _))
                    throw new InvalidOperationException("Fixture account debit could not establish a record.");
                Sql(fixture.Database, item.Mutation);
                var before = CounterFingerprint(fixture.Database);
                var allowed = limits.TryConsumeAccountDungeonLimit(9001, 9102, 6004, 10, out var rejected);
                check(item.Label + " is rejected without writing or using the unlimited fallback",
                    !allowed && rejected?.Allowed == false && CounterFingerprint(fixture.Database) == before);
            }
        }

        private static string CounterFingerprint(GameDatabase database)
            => Text(database, "SELECT COALESCE(group_concat(account_id||':'||character_id||':'||day_id||':'||current_count||':'||extra_count||':'||used_count,'|'),'') "
                + "FROM (SELECT account_id,character_id,day_id,current_count,extra_count,used_count "
                + "FROM dungeon_limit_records WHERE dgn_id=6004 ORDER BY account_id,character_id);");

        private static async Task VerifyNotificationAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            SeedAccounts(fixture.Database);
            var now = new DateTime(2026, 10, 3, 21, 59, 59, DateTimeKind.Utc);
            var limits = new DungeonEntryLimitService(fixture.Database, () => now);
            limits.EnsureAccountDungeonLimit(6004, 10);
            var session = fixture.CreateSession();
            var directory = fixture.Sessions;
            var packets = new List<byte[]>();
            using var sendLock = new SemaphoreSlim(1, 1);
            Action beforePrepare = null;
            Action beforeWrite = null;
            Action afterFirstWrite = null;
            var throwWrite = false;
            var throwSecondWrite = false;
            async Task Send(EnhancedClientSession target, Func<IReadOnlyList<byte[]>> prepare, Func<bool> guard)
            {
                await EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () =>
                {
                    var action = beforePrepare; beforePrepare = null; action?.Invoke();
                    var batch = prepare();
                    action = beforeWrite; beforeWrite = null; action?.Invoke();
                    return batch;
                }, packet =>
                {
                    if (throwWrite) { throwWrite = false; throw new IOException("fixture transport failure"); }
                    if (throwSecondWrite && packets.Count == 1)
                    {
                        throwSecondWrite = false;
                        throw new IOException("fixture remaining-count transport failure");
                    }
                    packets.Add(packet.ToArray());
                    if (packets.Count == 1)
                    {
                        var action = afterFirstWrite; afterFirstWrite = null; action?.Invoke();
                    }
                    return Task.CompletedTask;
                }, guard);
            }
            bool HasCountPair(short used)
                => packets.Count == 2
                    && packets[0].Length == 24 && packets[0][0] == 0
                    && BitConverter.ToUInt16(packets[0], 1) == (ushort)NotiPacketTypeA21.DUNGEON_ENTER_COUNT_INFO_INACCOUNT
                    && packets[0][15] == 1 && BitConverter.ToUInt32(packets[0], 16) == 6004
                    && BitConverter.ToInt16(packets[0], 20) == 10 && BitConverter.ToInt16(packets[0], 22) == used
                    && packets[1].Length == 20 && packets[1][0] == 0
                    && BitConverter.ToUInt16(packets[1], 1) == (ushort)NotiPacketTypeA21.ACCOUNT_DUNGEON_INOUT_COUNT
                    && BitConverter.ToUInt32(packets[1], 15) == 6004 && packets[1][19] == 10 - used;
            bool HasOnlyLegacyPacket()
                => packets.Count == 1 && packets[0].Length == 24
                    && BitConverter.ToUInt16(packets[0], 1) == (ushort)NotiPacketTypeA21.DUNGEON_ENTER_COUNT_INFO_INACCOUNT;
            void ReplaceSelection()
            {
                session.Player.ClearDungeonSelection();
                if (session.Player.BeginDungeonSelection(new DungeonTownReturnAnchor(28, 0, 0, 0, 5, 3)) == null)
                    throw new InvalidOperationException("Fixture could not create a replacement selection generation.");
            }

            foreach (var used in new short[] { 0, 1, 10 })
            {
                var count = new AccountDungeonCountProjection(6004, 10, used);
                var body = AccountDungeonCountPacketBuilder.BuildRemainingBody(count);
                check("A21 fixed remaining-count body projects used " + used + " as " + (10 - used),
                    count.RemainingCount == 10 - used
                    && body.SequenceEqual(new byte[] { 0x74, 0x17, 0, 0, (byte)(10 - used) }));
            }
            var notifier = new AccountDungeonCountNotifier(limits, directory, 6004, 10, send: Send);
            var initialFingerprint = CounterFingerprint(fixture.Database);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("account ratio and head-label NOTIs share one unused ledger snapshot and their distinct A21 bodies",
                HasCountPair(0) && CounterFingerprint(fixture.Database) == initialFingerprint);
            packets.Clear();
            beforePrepare = () => session.GameSession = new GameSession(session, fixture.Database);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("a replaced session generation while queued suppresses both account packets", packets.Count == 0);
            beforeWrite = () => session.Player.NextDungeonRunGeneration();
            var canceled = false;
            try { await notifier.PublishWhileTransitionHeldAsync(session); }
            catch (OperationCanceledException) { canceled = true; }
            check("a replaced run after preparation suppresses both packets without uncaught cancellation",
                !canceled && packets.Count == 0);
            throwWrite = true;
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("transport failure leaves no delivered account packet", packets.Count == 0);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("a failed account projection retries both consumers without consuming entry",
                HasCountPair(0) && CounterFingerprint(fixture.Database) == initialFingerprint);
            packets.Clear();
            if (!limits.TryConsumeAccountDungeonLimit(9001, 9101, 6004, 10, out _))
                throw new InvalidOperationException("Fixture account debit failed before the day-boundary test.");
            var usedFingerprint = CounterFingerprint(fixture.Database);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("an actual committed debit projects limit10 used1 and head-label remaining9",
                HasCountPair(1) && CounterFingerprint(fixture.Database) == usedFingerprint);
            packets.Clear();
            throwSecondWrite = true;
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("second-packet transport failure leaves only the legacy projection without consuming entry",
                HasOnlyLegacyPacket() && CounterFingerprint(fixture.Database) == usedFingerprint);
            packets.Clear();
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("retry after a partial write projects the same remaining9 without another debit",
                HasCountPair(1) && CounterFingerprint(fixture.Database) == usedFingerprint);
            packets.Clear();
            foreach (var replacement in new (string Label, Action Change)[]
            {
                ("session", () => session.GameSession = new GameSession(session, fixture.Database)),
                ("run", () => session.Player.NextDungeonRunGeneration()),
                ("selection", ReplaceSelection),
            })
            {
                afterFirstWrite = replacement.Change;
                await notifier.PublishWhileTransitionHeldAsync(session);
                check("a replaced " + replacement.Label + " between writes suppresses the stale head-label packet",
                    HasOnlyLegacyPacket() && CounterFingerprint(fixture.Database) == usedFingerprint);
                packets.Clear();
            }
            beforePrepare = ReplaceSelection;
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("a selection replaced while queued suppresses both consumers", packets.Count == 0);
            beforeWrite = () => now = now.AddSeconds(1);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("both prepared previous-day packets are suppressed across UTC+8 06:00", packets.Count == 0);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("the next publication refreshes both consumers from the new day's unused account budget",
                HasCountPair(0) && CounterFingerprint(fixture.Database) == usedFingerprint);
            packets.Clear();
            afterFirstWrite = () => now = now.AddDays(1);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("a day rollover between writes suppresses the old head-label packet",
                HasOnlyLegacyPacket() && CounterFingerprint(fixture.Database) == usedFingerprint);
            packets.Clear();
            for (var index = 0; index < 10; index++)
                if (!limits.TryConsumeAccountDungeonLimit(9001, index % 2 == 0 ? 9101 : 9102, 6004, 10, out _))
                    throw new InvalidOperationException("Fixture could not exhaust the shared current-day budget.");
            var exhaustedFingerprint = CounterFingerprint(fixture.Database);
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("the exhausted shared account projects ratio10/10 and an explicit head-label zero without a debit",
                HasCountPair(10) && CounterFingerprint(fixture.Database) == exhaustedFingerprint);
            packets.Clear();
            Sql(fixture.Database, "UPDATE dungeon_limit_config SET scope_type='charac' WHERE dgn_id=6004;");
            await notifier.PublishWhileTransitionHeldAsync(session);
            check("account-scope mismatch cannot publish a character counter as an account counter", packets.Count == 0);
        }

        private static bool Projection(DungeonEntryLimitService limits, int account, int character,
            out AccountDungeonCountProjection count)
            => AccountDungeonCountProjection.TryCreate(6004, 10,
                limits.LoadSpecialDungeonLimits(account, character), out count, out _);

        private static SpecialDungeonEntryLimitSnapshot CountRow() => new()
        {
            DungeonId = 6004,
            ScopeType = DungeonLimitScopeType.Account,
            LimitCount = 10,
            CurrentCount = 10,
        };

        private const string EntranceSource = "[role]\n`[enter town]` 1126\n[/role]\n[open visible event]\n286\n[account dungeon incount display]\n6004\n";
        private const string DestinationSource = "[role]\n`[exit town]` 0 `[shop]` 174\n[/role]\n[account dungeon incount display]\n6004\n";
        private static NpcActivityEntryDefinition Entrance(int destination = 1126)
            => NpcActivityEntryDefinition.Parse(EntranceSource.Replace("1126", destination.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static DungeonIncountConfigFile CountConfig(string accountRow = "0 6004 10 0")
            => DungeonIncountConfigFile.Parse("[dungeon incount reset info]\n[reset info]\n0 -1\n[/reset info]\n[/dungeon incount reset info]\n"
                + "[dungeon incount info]\n0 11006 3 1\n[/dungeon incount info]\n"
                + "[enter add incount dungeon index]\n8014\n[/enter add incount dungeon index]\n"
                + "[account dungeon incount info]\n" + accountRow + "\n[/account dungeon incount info]\n");

        private sealed class DefinitionInputs
        {
            internal NpcActivityEntryDefinition Entrance = EpicRoadEntrySelfTest.Entrance();
            internal NpcActivityEntryDefinition Destination = NpcActivityEntryDefinition.Parse(DestinationSource);
            internal readonly TownFile Town = new()
            {
                Areas = new List<TownArea>
                {
                    new() { Id = 0, MapPath = "Event/16_epicroad_town/16_epicroad_town.map", AreaType = "dungeon gate", LinkedId = 35,
                        Permission = new TownPermission { NeedLevel = 17, NeedQuest = 8598 } },
                },
            };
            internal readonly MapFile Map = new()
            {
                Npcs = new List<MapNpcInfo> { new() { NpcId = 1126, X = 441, Y = 177, Direction = "[right]" } },
            };
            internal readonly DungeonFile Dungeon = new()
            {
                MinimumRequiredLevel = 17, LimitPartyCount = 1, DesignateDungeonDifficulty = new[] { 0 },
                Mazes = new List<MazeInfo> { new() { Width = 3, Height = 1 } },
            };
            internal DungeonIncountConfigFile Counts = CountConfig();
            internal readonly WorldMapArea World = new() { AreaId = 35 };
            internal DefinitionInputs() => World.Dungeons.Add(new WorldMapDungeonEntry { DungeonId = 6004 });
            internal EpicRoadEntryDefinition Create()
                => EpicRoadEntryDefinition.FromResources(Entrance, Destination, Town, Map, Dungeon, Counts, World);
        }

        private static void SeedAccounts(GameDatabase database)
            => Sql(database, "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(9001,'epic-account-a',''),(9002,'epic-account-b','');"
                + "INSERT INTO characters(character_id,account_id,name,job) VALUES(9101,9001,'epic-a',0),(9102,9001,'epic-b',0),(9201,9002,'epic-c',0);");
        private static void Sql(GameDatabase database, string sql)
            => database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            });
        private static string Text(GameDatabase database, string sql)
            => database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            });
        private static long Scalar(GameDatabase database, string sql)
            => long.Parse(Text(database, sql), System.Globalization.CultureInfo.InvariantCulture);

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "epic-entry-selftest-" + Guid.NewGuid().ToString("N")));
            private readonly List<EnhancedClientSession> _sessions = new();
            internal GameDatabase Database { get; }
            internal EpicRoadEntryService Service { get; }
            internal SessionDirectory Sessions { get; } = new();
            internal Fixture()
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "entry.db"), ServerPaths.SchemaFilePath);
                Service = new EpicRoadEntryService(Database, () => new DefinitionInputs().Create());
            }
            internal EnhancedClientSession CreateSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 9001 };
                session.Player.CharacterId = 9101;
                session.Player.UserId = 12345;
                session.Player.Level = 17;
                session.GameSession = new GameSession(session, Database);
                Sessions.Register(9101, session);
                _sessions.Add(session);
                return session;
            }
            public void Dispose()
            {
                foreach (var session in _sessions) session.Close();
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: false);
            }
        }
    }
}
