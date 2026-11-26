using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Events.SeriaLuck;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class SeriaLuckUiSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== SERIA_LUCK_UI selftest ===");
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

            Section("current PVF", () => VerifyCurrentResource(Check));
            Section("strict definition", () => VerifyDefinitions(Check));
            Section("gauge and packets", () => VerifyPackets(Check));
            Section("catalog ownership", () => VerifyCatalog(Check));
            Section("durable login hydration", () => VerifyHydration(Check));
            Section("recipient fences", () => VerifyFencesAsync(Check).GetAwaiter().GetResult());
            Section("committed booster progress", () => VerifyCommittedUsesAsync(Check).GetAwaiter().GetResult());
            Console.WriteLine($"SERIA_LUCK_UI assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCurrentResource(Action<string, bool> check)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", path);
            using var archive = PvfArchive.Open(path);
            var text = archive.GetFileContent(SeriaLuckUiService.DefinitionPath);
            var definition = BoosterGageConfigFile.Parse(text);
            check("current ETC retains the complete gauge, reward and random-box fields",
                definition.IsValid && definition.MaxGage == 40 && definition.SubGage == 40
                && definition.RewardMultiple == 2 && definition.RandomBoxType == 1
                && definition.BoosterPointsByItemId.Count == 5
                && definition.TryGetBoosterPoint(2682272, out var first) && first == 5
                && definition.TryGetBoosterPoint(2683808, out var second) && second == 5
                && new[] { 10096091, 10096092, 10096093 }.All(id =>
                    definition.TryGetBoosterPoint(id, out var point) && point == 0));
            check("typed gauge parsing leaves the resource unchanged",
                definition.Content == text && !archive.HasModifications);
            using var fixture = new Fixture();
            var service = new SeriaLuckUiService(fixture.Database, () => definition);
            check("real PVF projects existing account progress 0, 7 and 8 as 0, 35 and 40",
                service.TryGetGauge(0, out var empty) && empty == 0
                && service.TryGetGauge(7, out var nearFull) && nearFull == 35
                && service.TryGetGauge(8, out var full) && full == 40);
        }

        private static void VerifyDefinitions(Action<string, bool> check)
        {
            var valid = Config();
            var malformed = new (string Label, string Source)[]
            {
                ("null", null), ("empty", ""),
                ("duplicate scalar", valid + "[max gage]\n40\n[/max gage]\n"),
                ("duplicate item", Config(points: "2682272 5 2682272 5")),
                ("partial item pair", Config(points: "2682272 5 2683808")),
                ("late bad point", Config(points: "2682272 5 2683808 nope")),
                ("negative point", Config(points: "2682272 -1")),
                ("point above maximum", Config(points: "2682272 41")),
                ("invalid item", Config(points: "0 5")),
                ("integer overflow", Config(points: "2682272 2147483648")),
                ("quoted number", Config(points: "2682272 `5`")),
                ("fraction", Config(points: "2682272 5.0")),
                ("client byte overflow", Config(max: 256)),
                ("invalid subtraction", Config(sub: 41)),
                ("zero reward multiplier", Config(reward: 0)),
                ("random-box byte overflow", Config(type: 256)),
                ("missing close", valid.Replace("[/booster point]\n", "")),
                ("stray close", "[/unexpected]\n" + valid),
                ("nested field", Config(points: "[nested]\n2682272 5\n[/nested]")),
                ("root data", "999\n" + valid),
                ("extra tag", valid + "[unexpected]\n1\n[/unexpected]\n"),
            };
            foreach (var test in malformed)
            {
                var definition = BoosterGageConfigFile.Parse(test.Source);
                check(test.Label + " publishes no partial gauge rules",
                    !definition.IsValid && !string.IsNullOrWhiteSpace(definition.InvalidReason)
                    && definition.BoosterPointsByItemId.Count == 0
                    && !definition.TryGetBoosterPoint(2682272, out _));
            }
            using var fixture = new Fixture();
            foreach (var definition in new[]
            {
                BoosterGageConfigFile.Parse(Config(max: 48, sub: 48)),
                BoosterGageConfigFile.Parse(Config(points: "2683808 5")),
                BoosterGageConfigFile.Parse(Config(points: "2682272 0")),
            })
            {
                var service = new SeriaLuckUiService(fixture.Database, () => definition);
                check("valid ETC incompatible with the existing counter stays neutral",
                    definition.IsValid && !service.TryGetGauge(0, out _)
                    && !service.TryGetGauge(8, out _));
            }
        }

        private static void VerifyPackets(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var service = fixture.Ui;
            check("out-of-range progress does not become a plausible gauge",
                !service.TryGetGauge(-1, out _) && !service.TryGetGauge(9, out _)
                && !service.TryGetGauge(int.MaxValue, out _));
            var builder = new SeriaLuckGaugeBodyBuilder(service);
            foreach (var value in new[] { (Luck: 0, Gauge: (byte)0), (Luck: 7, Gauge: (byte)35), (Luck: 8, Gauge: (byte)40) })
            {
                var snapshot = new SelectCharacterDataSnapshot
                {
                    InitializationSnapshot = new SelectCharacterInitializationSnapshot { SeriaLuckValue = value.Luck },
                };
                check("login gauge has equal display/current bytes for durable progress " + value.Luck,
                    builder.NotiType == (ushort)NotiPacketTypeA21.BOOSTER_GAGE
                    && builder.TryBuild(snapshot, 0, out var body)
                    && body.SequenceEqual(new[] { value.Gauge, value.Gauge }));
            }
            check("missing initialization snapshot cannot emit a fabricated progress packet",
                !builder.TryBuild(null, 0, out _)
                && !builder.TryBuild(new SelectCharacterDataSnapshot { InitializationSnapshot = null }, 0, out _));
            foreach (var value in new[] { (Before: 0, After: 1, First: (byte)0, Last: (byte)5),
                (Before: 7, After: 8, First: (byte)35, Last: (byte)40),
                (Before: 8, After: 1, First: (byte)40, Last: (byte)5) })
            {
                check("committed gauge packet keeps before-display then after-current " + value.Before + "->" + value.After,
                    SeriaLuckGaugeProjection.TryBuildPacket(service, Result(value.Before, value.After), out var packet)
                    && GaugePacket(packet, value.First, value.Last));
            }
            var rejected = new List<BoosterUseResult> { null };
            var error = Result(0, 1); error.ErrorCode = BoosterUseResult.ErrorInvalidRequest; rejected.Add(error);
            var other = Result(0, 1); other.IsSeriaLuckValueSource = false; rejected.Add(other);
            var wrongItem = Result(0, 1); wrongItem.SourceItemTemplateId = 2683808; rejected.Add(wrongItem);
            var expired = Result(0, 1); expired.SourceExpiredDeleted = true; rejected.Add(expired);
            var unconsumed = Result(0, 1); unconsumed.ConsumedSourceCount = 0; rejected.Add(unconsumed);
            rejected.Add(Result(-1, 1)); rejected.Add(Result(0, 9));
            check("error, OTHER source, expired and invalid progress cannot emit a gauge packet",
                rejected.All(result => !SeriaLuckGaugeProjection.TryBuildPacket(service, result, out var packet) && packet == null)
                && !SeriaLuckGaugeProjection.TryBuildPacket(null, Result(0, 1), out _));
        }

        private static void VerifyCatalog(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Sql(@"INSERT INTO game_event_state(event_id,state) VALUES(777,1);
INSERT INTO game_event_info_details(event_id,unknown0,sort_order) VALUES(777,99,10);
INSERT INTO game_event_info_extra(event_id,param0,param11,sort_order) VALUES(777,123,456,10);");
            fixture.SetLuck(7);
            var schema = fixture.Text("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name);");
            var version = fixture.Number("PRAGMA user_version;");
            fixture.Ui.Initialize(); fixture.Ui.Initialize();
            var catalog = new GameEventRepository(fixture.Database).LoadEventInfoSnapshot();
            var seria = catalog.Events.Single(entry => entry.EventId == SeriaLuckUiService.EventId);
            check("initialization merges a single no-detail UI member with existing main and extra events",
                catalog.Events.Count == 2 && catalog.Events.Any(entry => entry.EventId == 777 && entry.Unknown0 == 99)
                && seria.Unknown0 == 0 && !seria.HasDetail && seria.StartNotice.Length == 0 && seria.EndNotice.Length == 0
                && catalog.ExtraEntries.Count == 1 && catalog.ExtraEntries[0].EventId == 777
                && catalog.ExtraEntries[0].Parameters[0] == 123 && catalog.ExtraEntries[0].Parameters[11] == 456);
            var builder = new EventInfoBodyBuilder(fixture.Database);
            check("complete EVENT_INFO contains the existing event, Seria UI, raid member and all extra parameters",
                builder.TryBuild(new SelectCharacterDataSnapshot(), 0, out var body)
                && VerifyEventInfoBody(body));
            check("UI initialization leaves schema version, schema contents and account reward progress untouched",
                fixture.Number("PRAGMA user_version;") == version && fixture.Luck == 7
                && fixture.Text("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name);") == schema
                && fixture.Number("SELECT COUNT(*) FROM character_inventory_items;") == 0);
            fixture.Sql(@"UPDATE game_event_state SET state=0 WHERE event_id=2037;
UPDATE game_event_info_details SET unknown0=17,title='saved title',description='saved description',
detail_flag=1,flag_a=7,sort_order=4,updated_at='saved metadata' WHERE event_id=2037;");
            var saved = fixture.Text("SELECT unknown0||'|'||title||'|'||description||'|'||detail_flag||'|'||flag_a||'|'||sort_order||'|'||updated_at FROM game_event_info_details WHERE event_id=2037;");
            fixture.Ui.Initialize(); fixture.Ui.Initialize();
            check("initialization preserves the saved UI switch and all existing detail metadata",
                fixture.Number("SELECT state FROM game_event_state WHERE event_id=2037;") == 0
                && fixture.Text("SELECT unknown0||'|'||title||'|'||description||'|'||detail_flag||'|'||flag_a||'|'||sort_order||'|'||updated_at FROM game_event_info_details WHERE event_id=2037;") == saved);
            fixture.Sql("UPDATE game_event_state SET state=1 WHERE event_id=2037;");
            var invalid = new SeriaLuckUiService(fixture.Database, () => BoosterGageConfigFile.Parse(null));
            invalid.Initialize();
            check("invalid resources close only the UI while preserving metadata and reward owner",
                fixture.Number("SELECT state FROM game_event_state WHERE event_id=2037;") == 0
                && new GameEventRepository(fixture.Database).IsEnabled(777) && fixture.Luck == 7
                && fixture.Text("SELECT unknown0||'|'||title||'|'||description||'|'||detail_flag||'|'||flag_a||'|'||sort_order||'|'||updated_at FROM game_event_info_details WHERE event_id=2037;") == saved);
        }

        private static void VerifyHydration(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var builder = new SeriaLuckGaugeBodyBuilder(fixture.Ui);
            foreach (var value in new[] { (Luck: 0, Gauge: (byte)0), (Luck: 7, Gauge: (byte)35), (Luck: 8, Gauge: (byte)40) })
            {
                fixture.SetLuck(value.Luck);
                var data = new SqliteSelectCharacterDataSource(fixture.Database, new SqliteCharacterRepository(fixture.Database));
                var first = data.Load(fixture.CharacterId, 1);
                var sameAccount = data.Load(fixture.CharacterId + 1, 1);
                var otherAccount = data.Load(fixture.CharacterId + 2, 2);
                check("fresh login snapshots hydrate account-owned progress " + value.Luck + " and isolate the other account",
                    first.InitializationSnapshot.SeriaLuckValue == value.Luck
                    && sameAccount.InitializationSnapshot.SeriaLuckValue == value.Luck
                    && otherAccount.InitializationSnapshot.SeriaLuckValue == 0
                    && builder.TryBuild(first, 0, out var body) && body.SequenceEqual(new[] { value.Gauge, value.Gauge }));
            }
        }

        private static async Task VerifyFencesAsync(Action<string, bool> check)
        {
            var changes = new (string Label, Action<Fixture, EnhancedClientSession> Change)[]
            {
                ("account object", (_, session) => session.Account = new AccountRecord { AccountId = 1 }),
                ("account ID", (_, session) => session.Account.AccountId++),
                ("UID", (_, session) => session.Player.UserId++),
                ("character", (_, session) => session.Player.CharacterId++),
                ("GameSession", (_, session) => session.GameSession = Identity()),
                ("lease removed", (_, session) => InventoryContext.Unregister(session.SessionId)),
                ("lease generation", (fixture, session) => InventoryContext.Register(session.SessionId,
                    new InventoryService(fixture.CharacterId, 1, fixture.Database))),
            };
            foreach (var change in changes)
            {
                using var fixture = new Fixture();
                var session = fixture.CreateSession();
                var source = SeriaLuckGaugeProjection.Capture(session, fixture.Lease);
                check("valid owned source is captured before " + change.Label, source?.IsCurrent() == true);
                var sendLock = SendLock(session);
                await sendLock.WaitAsync();
                Task<bool> pending;
                try
                {
                    pending = source.SendAsync(fixture.Ui, Result(7, 8));
                    change.Change(fixture, session);
                }
                finally { sendLock.Release(); }
                check("queued gauge suppresses stale " + change.Label + " without changing the reward owner",
                    !await pending && !source.IsCurrent() && fixture.Luck == 0
                    && fixture.Number("SELECT COUNT(*) FROM character_inventory_items;") == 0);
            }
            using var missing = new Fixture();
            var current = missing.CreateSession();
            check("missing and mismatched recipients are never captured",
                SeriaLuckGaugeProjection.Capture(null, missing.Lease) == null
                && SeriaLuckGaugeProjection.Capture(current, null) == null);
            current.GameSession = null;
            check("missing GameSession cannot own a gauge notification",
                SeriaLuckGaugeProjection.Capture(current, missing.Lease) == null);
        }

        private static async Task VerifyCommittedUsesAsync(Action<string, bool> check)
        {
            using (var fixture = new Fixture())
            {
                var session = fixture.CreateSession(); fixture.PrepareBoxes(12);
                BoosterUseResult last = null;
                for (var opening = 1; opening <= 8; opening++)
                {
                    var committed = fixture.Open(1, out last, out var failed);
                    check("actual single opening " + opening + " commits progress without premature double rewards",
                        committed && !failed && last?.ErrorCode == 0 && !last.SeriaLuckDoubleTriggered
                        && last.SeriaLuckValueAfter == opening && fixture.Luck == opening);
                }
                check("eighth opening projects the full 40 gauge",
                    SeriaLuckGaugeProjection.TryBuildPacket(fixture.Ui, last, out var full)
                    && GaugePacket(full, 35, 40));
                var source = SeriaLuckGaugeProjection.Capture(session, fixture.Lease);
                var ninth = fixture.Open(1, out last, out var persistenceFailed);
                Console.WriteLine($"SERIA_NINTH observed committed={ninth} persistenceFailed={persistenceFailed} "
                    + $"error={last?.ErrorCode} double={last?.SeriaLuckDoubleTriggered} rewards={last?.DoubleRewards.Count} "
                    + $"before={last?.SeriaLuckValueBefore} after={last?.SeriaLuckValueAfter} "
                    + $"durableLuck={fixture.Luck} remainingBoxes={fixture.Lease.Inventory.CountMainItem(2682272)}");
                check("ninth real opening consumes the full counter, commits double rewards and returns to progress one",
                    ninth && !persistenceFailed && last.SeriaLuckDoubleTriggered
                    && last.DoubleRewards.Count > 0 && last.SeriaLuckValueBefore == 8 && last.SeriaLuckValueAfter == 1
                    && fixture.Luck == 1 && fixture.Lease.Inventory.CountMainItem(2682272) == 3
                    && SeriaLuckGaugeProjection.TryBuildPacket(fixture.Ui, last, out var reset)
                    && GaugePacket(reset, 40, 5));
                var inventoryRows = fixture.Text("SELECT group_concat(hex(item_core),'|') FROM (SELECT item_core FROM character_inventory_items ORDER BY list_type,slot_index);");
                var mailboxRows = fixture.MailboxSnapshot();
                session.TcpClient.Dispose();
                check("transport failure after the reward transaction never regrants or advances progress",
                    !await source.SendAsync(fixture.Ui, last) && fixture.Luck == 1
                    && fixture.Text("SELECT group_concat(hex(item_core),'|') FROM (SELECT item_core FROM character_inventory_items ORDER BY list_type,slot_index);") == inventoryRows
                    && fixture.MailboxSnapshot() == mailboxRows);
            }
            using (var fixture = new Fixture())
            {
                fixture.CreateSession(); fixture.PrepareBoxes(4); fixture.SetLuck(7);
                var committed = fixture.Open(3, out var batch, out var failed);
                check("actual batch crossing full gauge commits one double trigger and final progress two",
                    committed && !failed && batch.ConsumedSourceCount == 3 && batch.SeriaLuckDoubleTriggered
                    && batch.DoubleRewards.Count > 0 && batch.SeriaLuckValueBefore == 7 && batch.SeriaLuckValueAfter == 2
                    && fixture.Luck == 2 && fixture.Lease.Inventory.CountMainItem(2682272) == 1
                    && SeriaLuckGaugeProjection.TryBuildPacket(fixture.Ui, batch, out var packet)
                    && GaugePacket(packet, 35, 10));
            }
            using (var fixture = new Fixture())
            {
                fixture.CreateSession(); fixture.PrepareBoxes(2); fixture.SetLuck(7);
                fixture.Sql(@"CREATE TRIGGER seria_ui_force_rollback BEFORE UPDATE OF seria_luck_value ON accounts
BEGIN SELECT RAISE(ABORT,'seria UI transaction fixture'); END;");
                var mailboxRows = fixture.MailboxSnapshot();
                var committed = fixture.Open(1, out _, out var failed);
                using var connection = fixture.Database.OpenConnection();
                var reloaded = InventoryService.LoadFromDb(connection, fixture.CharacterId, 1, fixture.Database);
                check("failed owner transaction reloads the source and keeps account progress unchanged",
                    !committed && failed && fixture.Luck == 7
                    && fixture.Lease.Inventory.CountMainItem(2682272) == 2 && reloaded.CountMainItem(2682272) == 2
                    && fixture.MailboxSnapshot() == mailboxRows);
            }
            using (var fixture = new Fixture())
            {
                using var peer = await Peer.Create();
                fixture.BindSession(peer.Session); fixture.PrepareBoxes(1);
                var source = SeriaLuckGaugeProjection.Capture(peer.Session, fixture.Lease);
                var committed = fixture.Open(1, out var result, out var failed);
                var sent = committed && !failed && await source.SendAsync(fixture.Ui, result);
                check("a successful owner transaction sends the exact two-byte gauge through the real transport",
                    sent && GaugePacket(await peer.ReadPacketAsync(), 0, 5) && fixture.Luck == 1);
            }
        }

        private static bool VerifyEventInfoBody(byte[] body)
        {
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            var count = reader.ReadUInt16();
            var ids = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
            {
                ids.Add(reader.ReadUInt16()); reader.ReadUInt32();
                SkipDstr(reader); SkipDstr(reader);
                if (reader.ReadByte() != 0) return false;
            }
            if (count != 3 || !ids.SetEquals(new ushort[] { 777, SeriaLuckUiService.EventId, EventInfoBodyBuilder.RaidChannelEventId })
                || reader.ReadByte() != 1 || reader.ReadUInt16() != 777) return false;
            for (var index = 0; index < 12; index++)
                if (reader.ReadUInt32() != (index == 0 ? 123u : index == 11 ? 456u : 0u)) return false;
            return stream.Position == stream.Length;
        }

        private static void SkipDstr(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("EVENT_INFO dstr exceeds its body.");
            reader.ReadBytes(length);
        }

        private static bool GaugePacket(byte[] packet, byte before, byte after)
            => packet?.Length == 17 && packet[0] == 0
                && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.BOOSTER_GAGE
                && BitConverter.ToUInt32(packet, 3) == 17
                && packet.AsSpan(7, 8).SequenceEqual(new byte[8])
                && packet[15] == before && packet[16] == after;

        private static BoosterUseResult Result(int before, int after)
            => new BoosterUseResult { ErrorCode = 0, SourceItemTemplateId = 2682272,
                ConsumedSourceCount = 1, IsSeriaLuckValueSource = true,
                SeriaLuckValueBefore = before, SeriaLuckValueAfter = after };

        private static GameSession Identity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));

        private static SemaphoreSlim SendLock(EnhancedClientSession session)
            => (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);

        private static string Config(int max = 40, int sub = 40, int reward = 2, int type = 1,
            string points = "2682272 5 2683808 5 10096091 0 10096092 0 10096093 0")
            => "[max gage]\n" + max + "\n[/max gage]\n[sub gage]\n" + sub
                + "\n[/sub gage]\n[reward multiple]\n" + reward
                + "\n[/reward multiple]\n[booster point]\n" + points
                + "\n[/booster point]\n[randombox type]\n" + type + "\n";

        private sealed class Fixture : IDisposable
        {
            private static int _nextCharacter = 75000000;
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "seria-luck-ui-selftest-" + Guid.NewGuid().ToString("N")));
            private readonly List<EnhancedClientSession> _sessions = new();
            internal readonly int CharacterId = Interlocked.Add(ref _nextCharacter, 3);
            internal readonly GameDatabase Database;
            internal readonly SeriaLuckUiService Ui;
            private readonly IInventoryOverflowRewardSink _overflowRewardSink;
            internal InventoryLease Lease;
            internal int Luck => checked((int)Number("SELECT seria_luck_value FROM accounts WHERE account_id=1;"));

            internal Fixture()
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "seria-ui.db"), ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'seria-ui-one',''),(2,'seria-ui-two','');"
                    + $"INSERT INTO characters(character_id,account_id,name,job,level) VALUES({CharacterId},1,'seria-ui-a',0,50),"
                    + $"({CharacterId + 1},1,'seria-ui-b',0,50),({CharacterId + 2},2,'seria-ui-c',0,50);");
                // The real runtime supplies its transaction-bound mailbox sink.
                // A null sink rejects legitimate multiple rewards whose PVF
                // stack limit is one, making this random-resource fixture flaky.
                _overflowRewardSink = new MailboxInventoryOverflowRewardSink(
                    new MailboxService(new MailboxRepository(Database)));
                Ui = new SeriaLuckUiService(Database, () => BoosterGageConfigFile.Parse(Config()));
            }

            internal EnhancedClientSession CreateSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                BindSession(session);
                return session;
            }

            internal void BindSession(EnhancedClientSession session)
            {
                session.Account = new AccountRecord { AccountId = 1 };
                session.Player.CharacterId = CharacterId; session.Player.UserId = 12345;
                session.Player.Level = 50; session.GameSession = new GameSession(session, Database);
                _sessions.Add(session);
                Lease = InventoryContext.Register(session.SessionId, new InventoryService(CharacterId, 1, Database));
            }

            internal void SetLuck(int value) => Database.Write((connection, transaction) =>
                SqliteAccountRepository.UpdateSeriaLuckValue(connection, transaction, 1, value));

            internal void PrepareBoxes(int count)
            {
                if (!InventoryRewardGrantService.TryCreateAndInsert(Lease, 2682272, ItemCreateReason.Unknown, count, out _))
                    throw new InvalidOperationException("Current PVF luck-box fixture could not be created.");
                var definition = StackableItemProvider.Load(2682272);
                if (definition == null) throw new InvalidDataException("Current PVF luck-box definition is unavailable.");
                InventoryPackageRewardResolver.ResolveNeedMaterial(2682272, definition, out var material, out var perUse);
                if (material > 0 && perUse > 0
                    && !InventoryRewardGrantService.TryCreateAndInsert(Lease, material, ItemCreateReason.Unknown,
                        checked(count * perUse), out _))
                    throw new InvalidOperationException("Current PVF luck-box material fixture could not be created.");
                if (!InventoryPersistenceService.SaveDirty(Lease))
                    throw new InvalidOperationException("Luck-box fixture could not be persisted.");
            }

            internal bool Open(int count, out BoosterUseResult result, out bool failed)
                => InventorySpecialConsumableCommitService.TryCommitBoosterItem(Lease,
                    new BoosterUseRequest { ExpectedItemTemplateId = 2682272, RequestedCount = count },
                    "[swordman]", _overflowRewardSink, out result, out failed);

            internal string MailboxSnapshot() => Database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var snapshot = new Dictionary<string, List<object[]>>();
                foreach (var table in new[] { "mailbox_messages", "mailbox_recipients", "mailbox_attachments",
                    "mailbox_campaigns", "mailbox_campaign_deliveries", "mailbox_system_mail_audit",
                    "mailbox_system_mail_audit_attachments" })
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
                    using var reader = command.ExecuteReader();
                    var rows = new List<object[]>();
                    while (reader.Read())
                    {
                        var row = new object[reader.FieldCount];
                        for (var index = 0; index < row.Length; index++)
                            row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                        rows.Add(row);
                    }
                    snapshot.Add(table, rows);
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT name,seq FROM sqlite_sequence WHERE name LIKE 'mailbox_%' ORDER BY name;";
                    using var reader = command.ExecuteReader();
                    var rows = new List<object[]>();
                    while (reader.Read()) rows.Add(new[] { reader.GetValue(0), reader.GetValue(1) });
                    snapshot.Add("mailbox_sequences", rows);
                }
                transaction.Commit();
                return JsonSerializer.Serialize(snapshot);
            });

            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = sql; command.ExecuteNonQuery();
            });

            internal string Text(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            });

            internal long Number(string sql) => long.Parse(Text(sql), System.Globalization.CultureInfo.InvariantCulture);

            public void Dispose()
            {
                foreach (var session in _sessions) { InventoryContext.Unregister(session.SessionId); session.Close(); }
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                // Delete only this fixture's known files and its now-empty unique directory.
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: false);
            }
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal readonly EnhancedClientSession Session;
            private Peer(TcpClient reader, TcpClient writer)
            {
                _reader = reader; Session = new EnhancedClientSession(writer, new GamePacketHeader());
            }

            internal static async Task<Peer> Create()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var reader = new TcpClient();
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    var writer = await listener.AcceptTcpClientAsync(); await connecting;
                    return new Peer(reader, writer);
                }
                catch { reader.Dispose(); throw; }
                finally { listener.Stop(); }
            }

            internal async Task<byte[]> ReadPacketAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var stream = _reader.GetStream(); var header = new byte[15];
                await stream.ReadExactlyAsync(header, timeout.Token);
                var length = BitConverter.ToInt32(header, 3);
                if (length < 15 || length > 65536) throw new InvalidDataException("Invalid gauge envelope size.");
                var packet = new byte[length]; header.CopyTo(packet, 0);
                await stream.ReadExactlyAsync(packet.AsMemory(15), timeout.Token);
                return packet;
            }

            public void Dispose() { Session.Close(); _reader.Dispose(); }
        }
    }
}
