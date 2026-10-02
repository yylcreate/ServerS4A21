using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarPurchaseSelfTest
    {
        private static readonly DateTime Anchor = new(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_PURCHASE selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string name, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {name}");
                if (!success) failures++;
            }
            var path = Path.Combine(Path.GetTempPath(), "guardian-purchase-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                VerifyWire(Check);
                VerifyPurchases(path, Check);
            }
            catch (Exception ex)
            {
                Check("Guardian purchase unexpected exception: " + ex, false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"GUARDIAN_ALTAR_PURCHASE assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyPurchases(string path, Action<string, bool> check)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            Sql(database, "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-buy',''),(2,'guardian-other','');");
            var definition = Definition();
            var service = new GuardianAltarProgressService(database, new DailyResetService(database), definition);
            for (var id = 1; id <= 4; id++)
            {
                Sql(database, $"INSERT INTO characters(character_id,account_id,name,level) VALUES({id},1,'guardian-buy-{id}',70);");
                service.LoadMenu(id, Anchor);
            }
            Sql(database, "UPDATE character_guardian_altar SET stage_stars=100,other_stars=20,available_stars=120;");
            var original = service.LoadMenu(1, Anchor);
            var receiptCount = Scalar(database, "SELECT COUNT(*) FROM character_daily_counters;");

            foreach (var resource in new (int Type, int Id, short Level, int Cost)[] { (0, 100001, 2, 5),
                (0, 100002, (short)1, 2), (1, 100010, (short)2, 3),
                (1, 100011, (short)2, 4), (2, 100020, (short)2, 6) })
            {
                var before = service.LoadMenu(1, Anchor);
                var result = service.Buy(1, 1, new GuardianAltarPurchaseRequest(1,
                    resource.Type, resource.Id, resource.Level));
                var row = result.Progress.Purchases.Single(x => x.ShopType == resource.Type && x.ResourceId == resource.Id);
                check("one typed rule learns/upgrades unit, active/passive skill and tower: " + resource.Id,
                    result.Outcome == GuardianAltarPurchaseOutcome.Success && result.Changed
                    && row.Level == resource.Level && row.Price == resource.Cost
                    && result.Progress.Header.AvailableStars == before.Header.AvailableStars - resource.Cost);
                var replay = service.Buy(1, 1, new GuardianAltarPurchaseRequest(1,
                    resource.Type, resource.Id, resource.Level));
                check("same target replays its committed projection without another debit: " + resource.Id,
                    replay.Outcome == GuardianAltarPurchaseOutcome.Success && !replay.Changed
                    && Same(result.Progress, replay.Progress));
            }
            var after = service.LoadMenu(1, Anchor);
            check("buy changes only purchases and available stars, with no automatic deployment or daily refill",
                (original.Header with { AvailableStars = after.Header.AvailableStars }) == after.Header
                && original.Stages.SequenceEqual(after.Stages) && original.Slots.SequenceEqual(after.Slots)
                && original.Achievements.SequenceEqual(after.Achievements)
                && Scalar(database, "SELECT COUNT(*) FROM character_daily_counters;") == receiptCount);
            var restarted = new GuardianAltarProgressService(new GameDatabase(path, ServerPaths.SchemaFilePath),
                new DailyResetService(database), definition);
            check("recreated progress owner reads the committed purchases and balance", Same(after, restarted.LoadMenu(1, Anchor)));

            foreach (var invalid in new[]
            {
                new GuardianAltarPurchaseRequest(2, 1, 100010, 3),
                new GuardianAltarPurchaseRequest(1, 3, 100010, 3),
                new GuardianAltarPurchaseRequest(1, 1, 999999, 1),
                new GuardianAltarPurchaseRequest(1, 1, 100012, 1), // locked stage resource
                new GuardianAltarPurchaseRequest(1, 0, 100001, 4), // skip a level
                new GuardianAltarPurchaseRequest(1, 0, 100001, 1), // downgrade
                new GuardianAltarPurchaseRequest(1, 0, 100004, 2), // catalog gap
            })
            {
                var result = service.Buy(1, 1, invalid);
                check("invalid/locked/skipped target leaves every durable fact unchanged: " + invalid,
                    result.Outcome == GuardianAltarPurchaseOutcome.CannotPurchase && Same(after, result.Progress));
            }
            var duplicateFirst = service.Buy(1, 1, new GuardianAltarPurchaseRequest(1, 0, 100003, 2));
            var ambiguous = service.Buy(1, 1, new GuardianAltarPurchaseRequest(1, 0, 100003, 3));
            var independent = service.Buy(1, 1, new GuardianAltarPurchaseRequest(1, 1, 100010, 3));
            check("duplicate higher-level resource rows reject only the ambiguous target",
                duplicateFirst.Outcome == GuardianAltarPurchaseOutcome.Success
                && ambiguous.Outcome == GuardianAltarPurchaseOutcome.CannotPurchase
                && Same(duplicateFirst.Progress, ambiguous.Progress)
                && independent.Outcome == GuardianAltarPurchaseOutcome.Success);

            Sql(database, "UPDATE character_guardian_altar SET available_stars=0 WHERE character_id=2;");
            var poor = service.LoadMenu(2, Anchor);
            var insufficient = service.Buy(2, 1, new GuardianAltarPurchaseRequest(1, 2, 100020, 2));
            check("insufficient stars preserve level, catalog price and all role state",
                insufficient.Outcome == GuardianAltarPurchaseOutcome.InsufficientStars && Same(poor, insufficient.Progress));
            check("wrong account cannot read or mutate another character's purchase",
                Throws<InvalidOperationException>(() => service.Buy(2, 2, new GuardianAltarPurchaseRequest(1, 2, 100020, 2)))
                && Same(poor, service.LoadMenu(2, Anchor)));

            var beforeFailure = service.LoadMenu(3, Anchor);
            var batchRequest = new GuardianAltarPurchaseRequest(1, 0, 100001, 2, 3);
            var batch = service.Buy(3, 1, batchRequest);
            var repeatedBatch = service.Buy(3, 1, batchRequest);
            check("UI multi-level interval atomically charges every PVF level price, with an idempotent retry",
                batch.Outcome == GuardianAltarPurchaseOutcome.Success && batch.Changed
                && batch.Progress.Header.AvailableStars == beforeFailure.Header.AvailableStars - 12
                && batch.Progress.Purchases.Single(x => x.ResourceId == 100001).Price == 7
                && !repeatedBatch.Changed && Same(batch.Progress, repeatedBatch.Progress));
            beforeFailure = service.LoadMenu(3, Anchor);
            var fenceCalls = 0;
            check("generation replacement before commit rolls back purchase and debit together",
                Throws<InvalidOperationException>(() => service.Buy(3, 1,
                    new GuardianAltarPurchaseRequest(1, 2, 100020, 2), () => ++fenceCalls == 1))
                && fenceCalls == 2 && Same(beforeFailure, service.LoadMenu(3, Anchor)));
            Sql(database, "CREATE TRIGGER reject_guardian_purchase_balance BEFORE UPDATE OF available_stars ON character_guardian_altar "
                + "WHEN NEW.character_id=3 BEGIN SELECT RAISE(ABORT,'guardian-buy-test'); END;");
            check("header failure after purchase write rolls back both durable mutations",
                Throws<SqliteException>(() => service.Buy(3, 1, new GuardianAltarPurchaseRequest(1, 2, 100020, 2)))
                && Same(beforeFailure, service.LoadMenu(3, Anchor)));
            Sql(database, "DROP TRIGGER reject_guardian_purchase_balance;");
            Sql(database, "CREATE TRIGGER reject_guardian_purchase_row BEFORE UPDATE OF level ON character_guardian_altar_purchases "
                + "WHEN NEW.character_id=3 BEGIN SELECT RAISE(ABORT,'guardian-buy-row-test'); END;");
            check("purchase row failure cannot debit stars",
                Throws<SqliteException>(() => service.Buy(3, 1, new GuardianAltarPurchaseRequest(1, 2, 100020, 2)))
                && Same(beforeFailure, service.LoadMenu(3, Anchor)));
            Sql(database, "DROP TRIGGER reject_guardian_purchase_row;");

            var beforeConcurrent = service.LoadMenu(4, Anchor);
            var results = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => service.Buy(4, 1,
                new GuardianAltarPurchaseRequest(1, 1, 100010, 2))))).GetAwaiter().GetResult();
            var concurrent = service.LoadMenu(4, Anchor);
            check("concurrent identical targets commit one purchase and one debit",
                results.All(x => x.Outcome == GuardianAltarPurchaseOutcome.Success)
                && results.Count(x => x.Changed) == 1
                && concurrent.Header.AvailableStars == beforeConcurrent.Header.AvailableStars - 3
                && concurrent.Purchases.Single(x => x.ShopType == 1 && x.ResourceId == 100010).Level == 2);

            service.PrepareClearDefinition(definition.CreateClearProgressPlan(700001, 0));
            database.Write((connection, transaction) =>
            {
                service.ApplyClearInTransaction(connection, transaction,
                    4, 1, definition.CreateClearProgressPlan(700001, 0));
            });
            var unlocked = service.Buy(4, 1, new GuardianAltarPurchaseRequest(1, 1, 100012, 2));
            check("clear's typed unlock record participates in the same purchase path",
                unlocked.Outcome == GuardianAltarPurchaseOutcome.Success
                && unlocked.Progress.Purchases.Single(x => x.ResourceId == 100012).Level == 2);
            VerifyRequests(service, check).GetAwaiter().GetResult();
        }

        private static void VerifyWire(Action<string, bool> check)
        {
            // Frozen R009 current local request; not an official capture.
            var live = Convert.FromHexString("0000000000000000000000000000"
                + "01000000010000006400000002000200");
            check("A21 menu sender's fixed body30 decodes a level interval, not a price",
                GuardianAltarPurchaseRequestParser.TryParse(live, out var request)
                && request == new GuardianAltarPurchaseRequest(1, 1, 100, 2, 2));
            var multi = RequestBody(new GuardianAltarPurchaseRequest(1, 0, 100001, 2, 3));
            check("sender permits multiple consecutive purchased levels in one request",
                GuardianAltarPurchaseRequestParser.TryParse(multi, out var interval)
                && interval.StartLevel == 2 && interval.TargetLevel == 3);
            foreach (var invalid in new[] { live[..29], live.Concat(new byte[1]).ToArray(),
                Mutate(live, 0, 1), Mutate(live, 26, 0), Mutate(live, 28, 1),
                Mutate(live, 18, 3), Mutate(live, 28, 0xFF, 0xFF) })
                check("purchase parser rejects malformed length/prefix/type/interval",
                    !GuardianAltarPurchaseRequestParser.TryParse(invalid, out _));
            foreach (var outcome in Enum.GetValues<GuardianAltarPurchaseOutcome>())
            {
                var body = GuardianAltarPurchaseAckBodyBuilder.Build(
                    new GuardianAltarPurchaseRequest(1, 0, 100001, 2, 3), outcome,
                    outcome == GuardianAltarPurchaseOutcome.Success ? (short)3 : (short)1, 17);
                check("all domain outcomes keep the 24B read and dedicated newLevel word: " + outcome,
                    body.Length == 25 && body[0] == 1
                    && BitConverter.ToInt32(body, 1) == (int)outcome
                    && BitConverter.ToInt32(body, 5) == 1 && BitConverter.ToInt32(body, 9) == 0
                    && BitConverter.ToInt32(body, 13) == 100001 && BitConverter.ToInt16(body, 17) == 2
                    && BitConverter.ToInt16(body, 19) == (outcome == GuardianAltarPurchaseOutcome.Success ? 3 : 1)
                    && BitConverter.ToInt32(body, 21) == 17);
            }
            check("unknown state uses the existing common failure branch",
                GuardianAltarPurchaseAckBodyBuilder.BuildRejected().SequenceEqual(new byte[] { 0, 4 }));
        }

        private static async Task VerifyRequests(GuardianAltarProgressService progress, Action<string, bool> check)
        {
            using var fixture = new PurchaseRequestFixture(progress);
            var request = new GuardianAltarPurchaseRequest(1, 1, 100010, 2, 2);
            var before = progress.LoadMenu(3, Anchor);
            check("existing entry registry includes the dedicated A21 BUY_ITEM command",
                fixture.Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM, out _));
            await fixture.Dispatch(request);
            check("closed purchase returns a complete domain rejection without mutating progress",
                IsReply(fixture.Packets.Single(), 1) && Same(before, progress.LoadMenu(3, Anchor)));
            fixture.Open();
            fixture.Packets.Clear();
            await fixture.Dispatch(request, RequestBody(request)[..29]);
            check("malformed purchase rejects before any domain transaction",
                IsCommonFailure(fixture.Packets.Single()) && Same(before, progress.LoadMenu(3, Anchor)));
            foreach (var restriction in new[] { "trade", "mercenary", "level", "presence" })
            {
                fixture.Packets.Clear();
                fixture.Trading = restriction == "trade";
                fixture.Mercenary.Assigned = restriction == "mercenary";
                fixture.Session.Player.Level = (byte)(restriction == "level" ? 49 : 70);
                fixture.Session.Player.TownPresenceReady = restriction != "presence";
                await fixture.Dispatch(request);
                check("purchase honors the existing entry owner restriction: " + restriction,
                    fixture.Packets.Count == 1 && Same(before, progress.LoadMenu(3, Anchor))
                    && (restriction == "presence" ? IsCommonFailure(fixture.Packets[0]) : IsReply(fixture.Packets[0], 3)));
            }
            fixture.Trading = false;
            fixture.Mercenary.Assigned = false;
            fixture.Session.Player.Level = 70;
            fixture.Session.Player.TownPresenceReady = true;

            fixture.Packets.Clear();
            fixture.TradeReads = 0;
            fixture.DuringTradeRead = count =>
            {
                if (count == 3) fixture.Session.GameSession = GameSessionIdentity();
            };
            await fixture.Dispatch(request);
            check("old GameSession at the final commit fence rolls back and cannot project",
                fixture.Packets.Count == 0 && Same(before, progress.LoadMenu(3, Anchor)));
            fixture.DuringTradeRead = null;

            fixture.TradeReads = 0;
            fixture.DuringTradeRead = count =>
            {
                if (count == 3) fixture.Service.SetWindow(fixture.Now, fixture.Now.AddHours(1));
            };
            await fixture.Dispatch(request);
            check("opening revision replacement before commit rolls back both purchase facts",
                fixture.Packets.Count == 2 && IsCommonFailure(fixture.Packets[0])
                && Same(before, progress.LoadMenu(3, Anchor)));
            fixture.DuringTradeRead = null;

            fixture.Packets.Clear();
            fixture.FailNextWrite = true;
            await fixture.Dispatch(request);
            var committed = progress.LoadMenu(3, Anchor);
            check("socket failure after commit preserves the unique purchase and debit",
                fixture.Packets.Count == 0 && committed.Header.AvailableStars == before.Header.AvailableStars - 3
                && committed.Purchases.Single(x => x.ResourceId == 100010).Level == 2);
            await fixture.Dispatch(request);
            check("retry after lost ACK projects the same level and balance without charging again",
                IsReply(fixture.Packets.Single(), 0) && Same(committed, progress.LoadMenu(3, Anchor))
                && BitConverter.ToInt16(fixture.Packets[0], 34) == 2
                && BitConverter.ToInt32(fixture.Packets[0], 36) == committed.Header.AvailableStars);

            fixture.Packets.Clear();
            fixture.BeforePrepare = () => fixture.Service.SetWindow(fixture.Now, fixture.Now.AddHours(2));
            await fixture.Dispatch(new GuardianAltarPurchaseRequest(1, 1, 100010, 3, 3));
            var committedAfterWindow = progress.LoadMenu(3, Anchor);
            check("window change after commit requests a menu refresh without refund or a second purchase",
                fixture.Packets.Count == 2 && IsCommonFailure(fixture.Packets[0])
                && committedAfterWindow.Header.AvailableStars == committed.Header.AvailableStars - 4
                && committedAfterWindow.Purchases.Single(x => x.ResourceId == 100010).Level == 3);
            fixture.Packets.Clear();
            fixture.BeforePrepare = fixture.ReplaceSession;
            await fixture.Dispatch(new GuardianAltarPurchaseRequest(1, 2, 100020, 2, 2));
            var committedAfterReplacement = progress.LoadMenu(3, Anchor);
            check("displaced session after commit cannot send the old character projection",
                fixture.Packets.Count == 0
                && committedAfterReplacement.Header.AvailableStars == committedAfterWindow.Header.AvailableStars - 6
                && committedAfterReplacement.Purchases.Single(x => x.ResourceId == 100020).Level == 2);
        }

        private static byte[] RequestBody(GuardianAltarPurchaseRequest request)
        {
            var body = new byte[30];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14), request.RidableId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(18), request.ShopType);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(22), request.ResourceId);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(26), (ushort)request.StartLevel);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(28), (ushort)request.TargetLevel);
            return body;
        }

        private static byte[] Mutate(byte[] source, int offset, params byte[] values)
        {
            var copy = source.ToArray();
            values.CopyTo(copy, offset);
            return copy;
        }

        private static bool IsReply(byte[] packet, int result)
            => packet.Length == 40 && packet[0] == 1 && packet[15] == 1
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM
                && BitConverter.ToInt32(packet, 3) == packet.Length && BitConverter.ToInt32(packet, 16) == result;

        private static bool IsCommonFailure(byte[] packet)
            => packet.Length == 17 && packet[0] == 1 && packet[15] == 0 && packet[16] == 4
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM;

        private static GameSession GameSessionIdentity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));

        private sealed class PurchaseMercenaryStub : IMercenaryRestrictionService
        {
            internal bool Assigned;
            public bool IsAssigned(int characterId) => Assigned;
            public bool CanDelete(int characterId) => !Assigned;
            public bool CanMutateAppearance(int characterId) => !Assigned;
            public bool CanEnterContent(int characterId) => !Assigned;
        }

        private sealed class PurchaseRequestFixture : IDisposable
        {
            internal readonly GuardianAltarOpeningService Service = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly PurchaseMercenaryStub Mercenary = new();
            internal readonly GameCommandRegistry Registry = new();
            internal readonly List<byte[]> Packets = new();
            internal readonly DateTimeOffset Now = new(Anchor);
            internal EnhancedClientSession Session { get; private set; }
            internal bool Trading;
            internal int TradeReads;
            internal Action<int> DuringTradeRead;
            internal Action BeforePrepare;
            internal bool FailNextWrite;
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            private readonly GuardianAltarOpeningCoordinator _coordinator;

            internal PurchaseRequestFixture(GuardianAltarProgressService progress)
            {
                _coordinator = new GuardianAltarOpeningCoordinator(Service, Sessions,
                    new CharacterTransitionCoordinator(Sessions), null, () => Now, Send, progress);
                Registry.RegisterGroup("guardian-purchase-test", group =>
                    _coordinator.RegisterEntryHandlers(group, HasTrade, Mercenary));
                ReplaceSession();
            }

            internal void ReplaceSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 1 };
                session.Player.CharacterId = 3;
                session.Player.UserId = 12345;
                session.Player.Level = 70;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.GameSession = GameSessionIdentity();
                _ownedSessions.Add(session);
                Sessions.Register(3, session);
                Session = session;
            }

            internal void Open() => Service.SetWindow(Now, Now.AddMinutes(5));

            internal Task Dispatch(GuardianAltarPurchaseRequest request, byte[] body = null)
            {
                Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM, out var handler);
                body ??= RequestBody(request);
                return handler(Session, new GamePacketHeader { cmd = 1,
                    type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM,
                    length = checked((uint)(14 + body.Length)) }, body);
            }

            private bool HasTrade(EnhancedClientSession session)
            {
                TradeReads++;
                DuringTradeRead?.Invoke(TradeReads);
                return Trading;
            }

            private Task Send(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
            {
                var callback = BeforePrepare;
                BeforePrepare = null;
                callback?.Invoke();
                var batch = prepare();
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    throw new IOException("Guardian purchase transport selftest failure");
                }
                Packets.AddRange(batch.Select(x => x.ToArray()));
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                _coordinator.Dispose();
                foreach (var session in _ownedSessions) session.Close();
            }
        }

        private static GuardianAltarMenuDefinition Definition()
        {
            // IDs and rows here are fixtures. Production decisions consume the
            // current PvfLib shop owner, never these literal resource IDs.
            var shop = "[ridable id]\n1\n[unit]\n"
                + Entry(100001, "1 100001 0\n2 100001 5\n3 100001 7\n")
                + Entry(100002, "0 100002 0\n1 100002 2\n")
                + Entry(100003, "1 100003 0\n2 100003 1\n3 100003 2\n3 100004 3\n")
                + Entry(100004, "1 100004 0\n3 100004 2\n")
                + "[/unit]\n[skill]\n"
                + Entry(100010, "1 0 0\n2 0 3\n3 0 4\n", settable: false)
                + Entry(100011, "1 4 0\n2 4 4\n")
                + Entry(100012, "1 5 0\n2 5 5\n", unlock: 700001)
                + "[/skill]\n[tower]\n" + Entry(100020, "1 65530 0\n2 65531 6\n")
                + "[/tower]\n[/ridable id]\n";
            var config = GuardianAltarConfigFile.Parse("[daily refill ticket]\n3\n[/refill ticket]\n", shop);
            var stages = GuardianAltarStageMapFile.Parse("[area]\n0\n[district]\n1\n[stage]\n"
                + "700001 0 0 0\n700002 1 0 0\n[/stage]\n[/area]\n");
            return GuardianAltarMenuDefinition.Create(config, stages, _ => true);
        }

        private static string Entry(int id, string rows, int unlock = 0, bool settable = true)
            => $"[id]\n{id}\n[unlock stage index]\n{unlock}\n"
                + $"[is settable slot]\n{(settable ? 1 : 0)}\n"
                + "[field name]\n`level` `index` `price`\n[/field name]\n[list]\n"
                + rows + "[/list]\n[/id]\n";

        private static bool Same(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.CharacterId == right.CharacterId && left.Header == right.Header
                && left.Stages.SequenceEqual(right.Stages) && left.Purchases.SequenceEqual(right.Purchases)
                && left.Slots.SequenceEqual(right.Slots) && left.Achievements.SequenceEqual(right.Achievements);

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return true; }
            return false;
        }

        private static void Sql(GameDatabase database, string sql) => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });

        private static long Scalar(GameDatabase database, string sql) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        });
    }
}
