using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Handlers;
using DfoServer.Network.Builders;
using DfoServer.Network;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingQuestSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-quest-" + Guid.NewGuid().ToString("N") + ".db");
            const ushort quest = 20737;
            const int first = 63401, second = 63402, stranger = 63403;
            Guid sessionId = Guid.NewGuid(), partnerSessionId = Guid.NewGuid();
            try
            {
                var definition = QuestData.GetQuestFile(quest);
                Check("86 wedding prerequisite is typed 22 with five clears", WeddingClearQuestDefinition.IsWeddingClear(definition)
                    && WeddingClearQuestDefinition.TryParse(definition, out var parsed) && parsed.Count == 1
                    && parsed[0] == new WeddingClearQuestTarget(-1, -1, 5, 0) && QuestData.GetInitTrigger(quest) == 5);
                Check("second prerequisite remains ordinary seeking", QuestData.NormalizeQuestTag(QuestData.GetQuestFile(20738)?.Type) == "seeking"
                    && !WeddingClearQuestDefinition.IsWeddingClear(QuestData.GetQuestFile(20738)));
                Check("client decrement and increment are echo only", QuestClientTriggerAuthority.Resolve(quest, 0x10) == QuestClientTriggerDisposition.EchoOnly
                    && QuestClientTriggerAuthority.Resolve(quest, 0x10, true) == QuestClientTriggerDisposition.EchoOnly);
                foreach (string malformed in new[] { "-1 -1", "-1 -1 0", "-1 -1 512", "0 -1 5", "-1 -2 5", "-1 -1 bad" })
                    Check("invalid wedding triples fail closed: " + malformed, !WeddingClearQuestDefinition.TryParse(
                        new QuestFile { Type = "condition under clear", SubType = 22, IntData = malformed }, out _));
                Check("selectors and difficulty are typed per channel", WeddingClearQuestDefinition.TryParse(
                    new QuestFile { Type = "condition under clear", SubType = 22, IntData = "-1 -1 5 8 2 3" }, out var channels)
                    && channels.Count == 2 && channels[1].ChannelIndex == 1 && channels[1].Matches(8, 2)
                    && !channels[1].Matches(8, 1) && !channels[1].Matches(9, 2));
                Check("legacy repair never resets completed or multi-channel objectives",
                    !WeddingClearQuestDefinition.CanRepairLegacyDefaultOne(definition, 0)
                    && !WeddingClearQuestDefinition.CanRepairLegacyDefaultOne(new QuestFile
                    { Type = "condition under clear", SubType = 22, IntData = "-1 -1 5 -1 -1 3" }, 1)
                    && !WeddingClearQuestDefinition.CanRepairLegacyDefaultOne(new QuestFile
                    { Type = "condition under clear", SubType = 22, IntData = "-1 -1 1" }, 1));

                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63401,'wedding-quest-a',''),(63402,'wedding-quest-b',''),(63403,'wedding-quest-c','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63401,63401,'任务甲',0,60),(63402,63402,'任务乙',1,60),(63403,63403,'任务丙',0,60);"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,created_at) VALUES(1,63401,63402,1,1800000000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63401,1,0),(63402,1,1);");
                // Formal character creation initializes this owner before any
                // selection snapshot can load completion flags and other state.
                var characterState = new SqliteCharacterStateRepository(db);
                foreach (int cid in new[] { first, second, stranger })
                    characterState.InitializeEmptyState(cid);
                QuestActivationId Activate(int cid, uint remaining = 5)
                    => db.Write((c, t) => QuestRepository.InsertActiveQuest(c, t, cid, 0, quest, remaining));
                uint Remaining(int cid) => new QuestRepository(db.ConnectionString).LoadActiveQuests(cid)[0].TriggerValue;
                using (var connection = db.OpenConnection())
                {
                    InventoryContext.Register(sessionId, InventoryService.LoadFromDb(connection, first, first, db));
                    InventoryContext.Register(partnerSessionId, InventoryService.LoadFromDb(connection, second, second, db));
                }
                QuestCommandOwnerContext Owner(int cid)
                {
                    var session = cid == first ? sessionId : partnerSessionId;
                    InventoryContext.TryGetOwnedLease(session, cid, out var lease);
                    return new(cid, cid, session, lease);
                }
                bool Eligible() => db.Read(c => { using var t = c.BeginTransaction(deferred: true);
                    bool ok = WeddingQuestEligibility.Load(c, t, first); t.Commit(); return ok; });
                bool Offered(ushort qid, int level = 60) => QuestData.ComputeAcceptableQuests(level, 0, 0,
                    new HashSet<int>(), new Dictionary<int, int>(), null, Eligible()).Contains(qid);
                var lifecycle = new QuestService(db.ConnectionString);
                Check("unqualified callers do not receive wedding quests by default", !QuestData.ComputeAcceptableQuests(60, 0, 0,
                    new HashSet<int>(), new Dictionary<int, int>()).Contains(quest));
                foreach (int status in new[] { 0, 2, 3, 4 })
                {
                    Sql($"UPDATE wedding_pairs SET status={status} WHERE pair_id=1;");
                    Check("nonengaged status cannot see or formally accept wedding quests: " + status,
                        !Offered(quest) && !lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(quest)).Success);
                }
                Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1; DELETE FROM wedding_members WHERE character_id=63402;");
                Check("a missing reciprocal member blocks listing and formal acceptance", !Offered(quest)
                    && !lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(quest)).Success);
                Sql("INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63402,1,1); UPDATE characters SET delete_flag=1 WHERE character_id=63402;");
                Check("a deleted counterpart blocks listing and formal acceptance", !Offered(quest)
                    && !lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(quest)).Success);
                Sql("UPDATE characters SET delete_flag=0 WHERE character_id=63402;");
                Check("engaged visibility retains prerequisite and minimum-level rules", Offered(quest) && !Offered(20738) && !Offered(quest, 29)
                    && !lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(20738)).Success);
                Sql("UPDATE characters SET level=29 WHERE character_id=63401;");
                Check("formal acceptance rechecks the persisted minimum level", !lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(quest)).Success);
                Sql("UPDATE characters SET level=60 WHERE character_id=63401;");
                var initSource = new SqliteSelectCharacterDataSource(db, new SqliteCharacterRepository(db));
                var snapshot = initSource.Load(first, first);
                new QuestListBodyBuilder().TryBuild(snapshot, 0, out var offeredBody);
                Check("actual initialization builder offers the engaged preparation quest", Listed(offeredBody, quest) && !Listed(offeredBody, 20738));
                Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1;");
                new QuestListBodyBuilder().TryBuild(initSource.Load(first, first), 0, out var unboundList);
                Check("actual initialization hides wedding quests after a relationship is removed", !Listed(unboundList, quest) && !Listed(unboundList, 20738));
                Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1;");
                Check("both real acceptance owners initialize the PVF five-clear objective",
                    lifecycle.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(quest)).Success
                    && lifecycle.HandleAcceptQuest(Owner(second), new QuestAcceptCommand(quest)).Success
                    && Remaining(first) == 5 && Remaining(second) == 5);
                var a = new QuestRepository(db.ConnectionString).LoadActiveQuests(first).Single(x => x.QuestId == quest).ActivationId;
                var b = new QuestRepository(db.ConnectionString).LoadActiveQuests(second).Single(x => x.QuestId == quest).ActivationId;
                IReadOnlyDictionary<ushort, QuestActivationId> Activation(QuestActivationId id) => new Dictionary<ushort, QuestActivationId> { [quest] = id };
                var service = new QuestProgressApplicationService(db.ConnectionString);
                WeddingQuestClearEvidence Capture(Guid source, bool ordinary = true, int entryCount = 2, int[] ids = null)
                    => WeddingQuestClearEligibility.Capture(db, source, ordinary, entryCount, ids ?? new[] { first, second });
                QuestProgressApplicationRequest Request(int cid, Guid source, WeddingQuestClearEvidence evidence, QuestActivationId activation)
                    => new() { CharacterId = cid, Operation = QuestProgressOperation.ClearDungeon, DungeonId = 1,
                        SourceEventId = source, WeddingClear = evidence, EligibleQuestActivations = Activation(activation) };
                Guid deniedSource = Guid.NewGuid();
                Check("two reciprocal engaged characters qualify", Capture(deniedSource).Allows(deniedSource, first));
                Check("solo and initial three-member entry cannot qualify", !Capture(deniedSource, entryCount: 1, ids: new[] { first }).Eligible
                    && !Capture(deniedSource, entryCount: 3).Eligible && !Capture(deniedSource, ids: new[] { first, second, stranger }).Eligible);
                Check("nonordinary and stranger clears cannot qualify", !Capture(deniedSource, false).Eligible
                    && !Capture(deniedSource, ids: new[] { first, stranger }).Eligible);
                foreach (int status in new[] { 0, 2, 3, 4 })
                {
                    Sql($"UPDATE wedding_pairs SET status={status} WHERE pair_id=1;");
                    Check("only engagement qualifies, not status " + status, !Capture(deniedSource).Eligible);
                }
                Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1; UPDATE characters SET delete_flag=1 WHERE character_id=63402;");
                Check("deleted spouse cannot qualify", !Capture(deniedSource).Eligible);
                Sql("UPDATE characters SET delete_flag=0 WHERE character_id=63402;");
                var valid = Capture(deniedSource);
                var denied = service.Apply(Request(first, deniedSource, valid with { Eligible = false }, a));
                Check("ineligible clear has no progress or receipt", denied.Success && Remaining(first) == 5
                    && Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;") == 0);
                var mismatch = service.Apply(Request(first, Guid.NewGuid(), valid, a));
                Check("evidence must match canonical source and recipient", mismatch.Success && Remaining(first) == 5
                    && !valid.Allows(deniedSource, stranger));
                var mapOnly = Request(first, deniedSource, valid, a); mapOnly.Operation = QuestProgressOperation.ClearMap;
                Check("room clear is not dungeon completion", service.Apply(mapOnly).Success && Remaining(first) == 5);
                Check("legacy public clear API cannot manufacture marriage eligibility", QuestService.SyncClearMapQuestProgressCore(
                    db.ConnectionString, first, 1, 0, (_, _, _) => true, Guid.NewGuid()) == 0 && Remaining(first) == 5);

                using (var c = db.OpenConnection())
                {
                    var lease = InventoryContext.Register(sessionId, first, InventoryService.LoadFromDb(c, first, first, db));
                    var forged = new QuestProgressApplicationRequest { CharacterId = first, QuestId = quest,
                        Operation = QuestProgressOperation.ClientTrigger, TriggerType = 0x10,
                        CommandOwner = new QuestCommandOwnerContext(first, first, sessionId, lease) };
                    var echo = service.Apply(forged);
                    Check("owned client trigger only echoes current remaining count", echo.Success && echo.Changes.Count == 1 && Remaining(first) == 5);
                }

                var runtime = new DungeonWeddingQuestRuntime();
                Guid shared = Guid.NewGuid(); int captures = 0;
                var frozen = runtime.Freeze(shared, () => { captures++; return Capture(shared); });
                Check("first canonical clear decrements one of five", service.Apply(Request(first, shared, frozen, a)).Success && Remaining(first) == 4);
                Check("duplicate canonical clear cannot decrement twice", service.Apply(Request(first, shared, frozen, a)).DuplicateEvent && Remaining(first) == 4);
                Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1;");
                var replay = runtime.Freeze(shared, () => { captures++; return Capture(shared); });
                Check("first recipient leaving or cancelling does not recapture second eligibility", captures == 1 && ReferenceEquals(frozen, replay)
                    && service.Apply(Request(second, shared, replay, b)).Success && Remaining(second) == 4);
                Check("other canonical identity cannot reuse evidence", runtime.Get(Guid.NewGuid()) == null);
                Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1;");
                for (int i = 0; i < 4; i++)
                {
                    Guid source = Guid.NewGuid(); var evidence = Capture(source);
                    service.Apply(Request(first, source, evidence, a)); service.Apply(Request(second, source, evidence, b));
                }
                Check("five distinct actual clears finish both quests", Remaining(first) == 0 && Remaining(second) == 0);
                Guid after = Guid.NewGuid(); service.Apply(Request(first, after, Capture(after), a));
                Check("completed objective never underflows", Remaining(first) == 0);

                Sql("DELETE FROM character_active_quests WHERE character_id=63401;");
                var newer = Activate(first);
                Guid stale = Guid.NewGuid();
                service.Apply(Request(first, stale, Capture(stale), a));
                Check("abandon and reaccept invalidate old captured activation", !newer.Equals(a) && Remaining(first) == 5);
                Guid rollback = Guid.NewGuid(); var pending = Request(first, rollback, Capture(rollback), newer);
                long receipts = Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;");
                Sql("CREATE TRIGGER reject_wedding_quest BEFORE UPDATE ON character_active_quests BEGIN SELECT RAISE(ABORT,'quest rollback'); END;");
                bool threw = false;
                try { service.Apply(pending); } catch (SqliteException) { threw = true; }
                Check("SQL failure rolls back counter and receipt together", threw && Remaining(first) == 5
                    && Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;") == receipts);
                Sql("DROP TRIGGER reject_wedding_quest;");
                Check("same canonical event retries after rollback", service.Apply(pending).Success && Remaining(first) == 4
                    && Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;") == receipts + 1);
                Sql("DELETE FROM character_active_quests WHERE character_id=63401;");
                var legacy = Activate(first, 1); Guid repair = Guid.NewGuid();
                long beforeLegacy = Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;");
                Guid soloSource = Guid.NewGuid();
                service.Apply(Request(first, soloSource, Capture(soloSource, entryCount: 1, ids: new[] { first }), legacy));
                Guid strangerSource = Guid.NewGuid();
                service.Apply(Request(first, strangerSource, Capture(strangerSource, ids: new[] { first, stranger }), legacy));
                Check("ineligible legacy clears neither repair nor poison its receipt", Remaining(first) == 1
                    && Scalar("SELECT COUNT(*) FROM quest_progress_event_inbox;") == beforeLegacy);
                service.Apply(Request(first, repair, Capture(repair), legacy));
                Check("old stride-four initial one is repaired on first authoritative clear", Remaining(first) == 4);
                for (int i = 0; i < 3; i++) { Guid source = Guid.NewGuid(); service.Apply(Request(first, source, Capture(source), legacy)); }
                Guid last = Guid.NewGuid(); service.Apply(Request(first, last, Capture(last), legacy));
                Check("legitimate remaining one with receipt is not repaired again", Remaining(first) == 0);
                var retryRuntime = new DungeonWeddingQuestRuntime(); Guid retrySource = Guid.NewGuid();
                try { retryRuntime.Freeze(retrySource, () => throw new InvalidOperationException("temporary read failure")); } catch (InvalidOperationException) { }
                Check("failed eligibility read remains retriable", retryRuntime.Get(retrySource) == null
                    && retryRuntime.Freeze(retrySource, () => Capture(retrySource)).Eligible);
                VerifyLifecycle(db, sessionId, partnerSessionId, Check);
                Sql("DELETE FROM character_active_quests WHERE character_id IN (63401,63402);");
                Activate(first); Activate(second);
                VerifyBridge(db, Check);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sessionId, first);
                InventoryContext.Unregister(partnerSessionId, second);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_QUEST: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static bool Listed(byte[] body, ushort quest)
        {
            if (body == null || body.Length < 3) return false;
            int count = BitConverter.ToUInt16(body, 1);
            return body.Length == 3 + 2 * count && Enumerable.Range(0, count).Any(i => BitConverter.ToUInt16(body, 3 + 2 * i) == quest);
        }

        private static void VerifyLifecycle(GameDatabase db, Guid firstSession, Guid secondSession, Action<string, bool> check)
        {
            const int first = 63401, second = 63402;
            const ushort quest = 20737, seeking = 20738;
            void Sql(string text) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = text; cmd.ExecuteNonQuery(); });
            long Count(string text) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = text; return Convert.ToInt64(cmd.ExecuteScalar()); });
            QuestCommandOwnerContext Owner(int cid)
            {
                Guid session = cid == first ? firstSession : secondSession;
                InventoryContext.TryGetOwnedLease(session, cid, out var lease); return new(cid, cid, session, lease);
            }
            var service = new QuestService(db.ConnectionString);
            var finish = new QuestFinishCommand(quest, false, 0, 1);
            Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1;");
            check("formal finish cannot reward a completed objective after breakup", !service.HandleFinishQuest(Owner(first), finish).Success
                && Count("SELECT COUNT(*) FROM character_active_quests WHERE character_id=63401 AND quest_id=20737 AND trigger_value=0;") == 1);
            Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1;");
            check("formal finish under reciprocal engagement completes the actual first prerequisite", service.HandleFinishQuest(Owner(first), finish).Success
                && new QuestRepository(db.ConnectionString).IsQuestCleared(first, quest));
            var source = new SqliteSelectCharacterDataSource(db, new SqliteCharacterRepository(db));
            var completedSnapshot = source.Load(first, first);
            check("real initialization carries the first wedding quest completion flag",
                completedSnapshot.InitializationSnapshot.CharacInvisibleFalgs.Any(x => x.SlotIndex == quest && x.FlagValue != 0));
            new QuestListBodyBuilder().TryBuild(completedSnapshot, 0, out var nextList);
            check("first completion exposes the second wedding quest through the real init list", Listed(nextList, seeking) && !Listed(nextList, quest));
            if (!InventoryRewardGrantService.TryCreateAndInsert(Owner(first).InventoryLease.Inventory, 10002610,
                ItemCreateReason.Unknown, 1, out _)) throw new InvalidOperationException("Cannot prepare actual wedding seeking material.");
            check("second formal acceptance reuses real seeking inventory progress", service.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(seeking)).Success
                && Count("SELECT trigger_value FROM character_active_quests WHERE character_id=63401 AND quest_id=20738;") == 0);
            Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1;");
            check("seeking completion cannot consume material or reward outside an engagement",
                !service.HandleFinishQuest(Owner(first), new QuestFinishCommand(seeking, false, 0, 1)).Success
                && Owner(first).InventoryLease.Inventory.CountMainItem(10002610) == 1);
            Sql("UPDATE wedding_pairs SET status=1 WHERE pair_id=1;");
            ushort regular = checked((ushort)QuestCatalog.OrderedIds.First(id => id > 0 && id <= ushort.MaxValue
                && QuestData.GetQuestFile(id) != null && !WeddingQuestEligibility.IsWeddingQuest(id)));
            db.Write((c, t) => QuestRepository.InsertActiveQuest(c, t, first, 3, regular, 1));
            new QuestNotifySelectionRepository(db.ConnectionString).Replace(first, new int[] { seeking, regular });
            new QuestNotifySelectionRepository(db.ConnectionString).Replace(second, new int[] { quest });
            var oldActivation = new QuestRepository(db.ConnectionString).LoadActiveQuests(second).Single(x => x.QuestId == quest).ActivationId;
            Sql("CREATE TRIGGER reject_wedding_quest_cancel BEFORE DELETE ON character_active_quests WHEN OLD.character_id=63402 AND OLD.quest_id=20737 BEGIN SELECT RAISE(ABORT,'cancel rollback'); END;");
            bool failed = false;
            try { db.Write((c, t) => WeddingQuestEligibility.CancelActive(c, t, first, second)); } catch (SqliteException) { failed = true; }
            check("cancellation failure rolls back both activations and notification selections", failed
                && Count("SELECT COUNT(*) FROM character_active_quests WHERE quest_id IN (20737,20738);") == 2
                && Count("SELECT COUNT(*) FROM character_quest_notify_selections;") == 3);
            Sql("DROP TRIGGER reject_wedding_quest_cancel;");
            int removed = db.Write((c, t) =>
            {
                int n = WeddingQuestEligibility.CancelActive(c, t, first, second);
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "UPDATE wedding_pairs SET status=0 WHERE pair_id=1; DELETE FROM wedding_members;"; cmd.ExecuteNonQuery(); return n;
            });
            check("breakup retires only wedding activations and their notification selections", removed == 2
                && Count("SELECT COUNT(*) FROM character_active_quests;") == 1
                && Count($"SELECT COUNT(*) FROM character_active_quests WHERE quest_id={regular};") == 1
                && Count("SELECT COUNT(*) FROM character_quest_notify_selections;") == 1
                && new QuestRepository(db.ConnectionString).IsQuestCleared(first, quest));
            db.Write((c, t) => WeddingRepository.Create(c, t, first, second, 1800000010));
            check("a new engagement accepts a fresh five-clear activation", service.HandleAcceptQuest(Owner(second), new QuestAcceptCommand(quest)).Success
                && Count("SELECT trigger_value FROM character_active_quests WHERE character_id=63402 AND quest_id=20737;") == 5);
            Guid stale = Guid.NewGuid();
            var evidence = WeddingQuestClearEligibility.Capture(db, stale, true, 2, new[] { first, second });
            var result = new QuestProgressApplicationService(db.ConnectionString).Apply(new QuestProgressApplicationRequest
            { CharacterId = second, Operation = QuestProgressOperation.ClearDungeon, SourceEventId = stale, DungeonId = 1,
                WeddingClear = evidence, EligibleQuestActivations = new Dictionary<ushort, QuestActivationId> { [quest] = oldActivation } });
            check("the previous engagement's captured activation cannot continue the new quest", result.Success
                && Count("SELECT trigger_value FROM character_active_quests WHERE character_id=63402 AND quest_id=20737;") == 5);
            check("the seeking prerequisite still uses normal acceptance and finish after reengagement",
                service.HandleAcceptQuest(Owner(first), new QuestAcceptCommand(seeking)).Success
                && service.HandleFinishQuest(Owner(first), new QuestFinishCommand(seeking, false, 0, 1)).Success
                && Owner(first).InventoryLease.Inventory.CountMainItem(10002610) == 0);
        }

        private static void VerifyBridge(GameDatabase db, Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            using var first = Peer.Create(sessions, 63401, "任务甲").GetAwaiter().GetResult();
            using var second = Peer.Create(sessions, 63402, "任务乙").GetAwaiter().GetResult();
            using var registry = new DungeonInstanceRegistry(ClockService.Instance);
            var characters = new SqliteCharacterRepository(db);
            var data = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(data, characters, db);
            var services = new DungeonSharedServices(new Game.ReviveCoin.ReviveCoinService(new Game.DailyReset.DailyResetService(db)),
                characters, data, null, db.ConnectionString, refresh, sessions: sessions, instanceRegistry: registry, database: db);
            var handler = new DungeonSettlementHandler(services, null);
            try
            {
                first.Session.GameSession = new GameSession(first.Session, db);
                second.Session.GameSession = new GameSession(second.Session, db);
                long livePair = new WeddingRepository(db).Load(63401).PairId;
                second.Session.GameSession.QuestManager.SendAcceptableQuestListAsync().GetAwaiter().GetResult();
                var offered = second.Drain().Single(p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST);
                check("runtime quest notification reads current engaged eligibility", Listed(offered.AsSpan(15).ToArray(), 20737));
                var instance = new DungeonInstance(1, 0);
                var a = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active) { EntryPartyMemberCount = 2 };
                var b = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active) { EntryPartyMemberCount = 2 };
                var entryCohort = new DungeonPartySelectionCohort(1, 1, first.Session.Player.UserId, new[]
                {
                    new DungeonPartySelectionParticipant(first.Session.Player.UserId, 63401, Guid.NewGuid(), 0),
                    new DungeonPartySelectionParticipant(second.Session.Player.UserId, 63402, Guid.NewGuid(), 1),
                });
                a.EntryPartySelectionCohort = b.EntryPartySelectionCohort = entryCohort;
                a.QuestSnapshot = QuestRunSnapshot.Capture(new QuestRepository(db.ConnectionString).LoadActiveQuests(63401));
                b.QuestSnapshot = QuestRunSnapshot.Capture(new QuestRepository(db.ConnectionString).LoadActiveQuests(63402));
                first.Session.Player.CurrentRun = a; second.Session.Player.CurrentRun = b;
                var source = DungeonEventEnvelope.Create(a, 63401, "wedding-quest-test");
                var fact = instance.GetOrCreateClearedFact(new DungeonClearIntent(source, "test", 0), out _);
                var room = new DungeonRoomIdentity(instance.Identity, 1);
                instance.ParticipantEffects.TryFreeze(source, DungeonParticipantEffectAudience.Instance, new[]
                {
                    new DungeonParticipantRosterEntry(63401, first.Session.Player.UserId, a, a.CaptureIdentity(), room, 0),
                    new DungeonParticipantRosterEntry(63402, second.Session.Player.UserId, b, b.CaptureIdentity(), room, 0),
                }, out _);
                handler.FreezeWeddingQuestClear(a, fact);
                check("canonical settlement freezes current owners despite retired selection-session tickets", instance.Mechanisms.WeddingQuest.Get(fact.SourceEventId)?.Eligible == true);
                DungeonClearMapQuestSync.SyncAsync(first.Session, 1, 0, "wire-test", source).GetAwaiter().GetResult();
                uint Remaining(int cid) => new QuestRepository(db.ConnectionString).LoadActiveQuests(cid)[0].TriggerValue;
                check("canonical clear sync and quest bridge reach authoritative owner", Remaining(63401) == 4 && first.Drain().Count > 0);
                first.Session.Player.CurrentRun = null;
                db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "UPDATE wedding_pairs SET status=0 WHERE pair_id=@pair;";
                    cmd.Parameters.AddWithValue("@pair", livePair); cmd.ExecuteNonQuery(); });
                second.Session.GameSession.QuestManager.SendAcceptableQuestListAsync().GetAwaiter().GetResult();
                var retired = second.Drain().Single(p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST);
                check("runtime quest notification removes wedding offers after breakup", !Listed(retired.AsSpan(15).ToArray(), 20737));
                var peerSource = source.ForAffectedPlayer(b.CaptureIdentity(), null, 63402);
                handler.FreezeWeddingQuestClear(b, fact);
                DungeonClearMapQuestSync.SyncAsync(second.Session, 1, 0, "wire-test", peerSource).GetAwaiter().GetResult();
                check("second wire recipient survives first returning and cancelling", Remaining(63402) == 4 && second.Drain().Count > 0);
                DungeonClearMapQuestSync.SyncAsync(second.Session, 1, 0, "wire-test", peerSource).GetAwaiter().GetResult();
                check("wire duplicate is ignored", Remaining(63402) == 4);
                var unrelated = DungeonEventEnvelope.Create(b, 63402, "ordinary-map-command");
                DungeonQuestBridge.ApplyAsync(second.Session, DungeonQuestProgressEvent.ClearMap(unrelated, 1, 0)).GetAwaiter().GetResult();
                check("unrelated dungeon event cannot claim frozen couple eligibility", Remaining(63402) == 4);
            }
            finally
            {
                sessions.UnregisterAsync(63401, first.Session).GetAwaiter().GetResult();
                sessions.UnregisterAsync(63402, second.Session).GetAwaiter().GetResult();
            }
        }
    }
}
