using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Data.Sqlite;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.SelfTests
{
    public static class FatiguePotionSelfTest
    {
        private const int Potion50 = 416;
        private const int Potion30 = 10004513;
        private const short Slot = 84;

        public static int Run()
        {
            int checks = 0, failures = 0;
            void Check(string name, bool ok)
            {
                checks++;
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
                if (!ok) failures++;
            }

            try
            {
                VerifyDefinitions(Check);
                VerifyActionBodyParsing(Check);
                VerifyAckBodies(Check);
                VerifyCommittedUse(Check);
                VerifyCrossDayBoundary(Check);
            }
            catch (Exception ex)
            {
                Check("unexpected exception: " + ex, false);
            }

            Console.WriteLine($"FATIGUE_POTION checks={checks} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyDefinitions(Action<string, bool> check)
        {
            check("PVF 416 resolves [add fatigue] 50",
                FatiguePotionDefinition.TryResolve(Potion50, out var def50)
                && def50.ItemTemplateId == Potion50 && def50.RestorePoints == 50);
            check("PVF 10004513 resolves [add fatigue] 30",
                FatiguePotionDefinition.TryResolve(Potion30, out var def30)
                && def30.RestorePoints == 30);
            check("non-potion rejected: 2600655 [waste]",
                !FatiguePotionDefinition.TryResolve(2600655, out _));
            check("non-potion rejected: 2683466 [random recovery fatigue]",
                !FatiguePotionDefinition.TryResolve(2683466, out _));
            check("missing item rejected",
                !FatiguePotionDefinition.TryResolve(-1, out _));
            check("416 daily usable count limit is 3",
                UsableCountLimitService.GetUsableCountLimit(Potion50) == 3);
        }

        private static void VerifyActionBodyParsing(Action<string, bool> check)
        {
            check("live-captured body (slot 84) parses",
                FatiguePotionActionRequestParser.TryParse(
                    ActionBody(Slot), out var request)
                && request.SlotIndex == Slot);

            var lengths = new[] { 11, 18, 20 };
            foreach (var length in lengths)
            {
                var body = new byte[length];
                Array.Copy(ActionBody(Slot), body, Math.Min(length, 19));
                check($"length {length} rejected",
                    !FatiguePotionActionRequestParser.TryParse(body, out _));
            }

            var badReserved = ActionBody(Slot);
            badReserved[3] = 1;
            check("non-zero reserved rejected",
                !FatiguePotionActionRequestParser.TryParse(badReserved, out _));

            var wrongAction = ActionBody(Slot);
            BinaryPrimitives.WriteInt32LittleEndian(wrongAction.AsSpan(7, 4), 0x6B);
            check("action 0x6B (random recovery) rejected",
                !FatiguePotionActionRequestParser.TryParse(wrongAction, out _));

            var wrongList = ActionBody(Slot);
            wrongList[2] = 1;
            check("non-Main list rejected",
                !FatiguePotionActionRequestParser.TryParse(wrongList, out _));

            var negativeSlot = ActionBody(Slot);
            BinaryPrimitives.WriteInt16LittleEndian(negativeSlot.AsSpan(0, 2), -1);
            check("negative slot rejected",
                !FatiguePotionActionRequestParser.TryParse(negativeSlot, out _));

            // 普通布局（偏移 3 起是 instance value）不得命中动作解析。
            var normal = new byte[19];
            BinaryPrimitives.WriteInt16LittleEndian(normal.AsSpan(0, 2), Slot);
            normal[2] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(normal.AsSpan(3, 4), 0x00289940);
            BinaryPrimitives.WriteInt32LittleEndian(normal.AsSpan(7, 4), 2660671);
            check("ordinary 0x2C layout rejected",
                !FatiguePotionActionRequestParser.TryParse(normal, out _));
        }

        private static void VerifyAckBodies(Action<string, bool> check)
        {
            var insufficient = InventoryHandler.BuildFatiguePotionAckBody(
                FatigueRestoreOutcome.Insufficient, used: false, mutation: null,
                listType: InventoryListType.Main, slotIndex: Slot,
                instanceValue: 0, itemTemplateId: Potion50);
            check("insufficient ack carries 0x5F",
                insufficient.Length >= 2 && insufficient[0] == 0x00 && insufficient[1] == 0x5F);

            var restoreFailed = InventoryHandler.BuildFatiguePotionAckBody(
                FatigueRestoreOutcome.PersistenceFailed, used: false, mutation: null,
                listType: InventoryListType.Main, slotIndex: Slot,
                instanceValue: 0, itemTemplateId: Potion50);
            check("restore-failed ack carries default code",
                restoreFailed.Length >= 2 && restoreFailed[0] == 0x00
                && restoreFailed[1] == UseStackableAckBuilder.InvalidItemErrorCode);

            var genericFailed = InventoryHandler.BuildFatiguePotionAckBody(
                null, used: false, mutation: null,
                listType: InventoryListType.Main, slotIndex: Slot,
                instanceValue: 0, itemTemplateId: Potion50);
            check("generic-failed ack carries default code",
                genericFailed.Length >= 2 && genericFailed[0] == 0x00
                && genericFailed[1] == UseStackableAckBuilder.InvalidItemErrorCode);

            // 成功 ACK 必须回 Mutation.InstanceValue（剩余数量）而不是动作包的 reserved=0。
            var remainingTwo = InventoryHandler.BuildFatiguePotionAckBody(
                FatigueRestoreOutcome.Restored, used: true,
                mutation: new InventoryMutationResult { InstanceValue = 2 },
                listType: InventoryListType.Main, slotIndex: Slot,
                instanceValue: 0, itemTemplateId: Potion50);
            check("success ack echoes mutation remaining (2)",
                remainingTwo[0] == 0x01
                && BinaryPrimitives.ReadInt16LittleEndian(remainingTwo.AsSpan(1, 2)) == Slot
                && remainingTwo[3] == 0
                && BinaryPrimitives.ReadInt32LittleEndian(remainingTwo.AsSpan(4, 4)) == 2
                && BinaryPrimitives.ReadInt32LittleEndian(remainingTwo.AsSpan(8, 4)) == Potion50);

            var remainingZero = InventoryHandler.BuildFatiguePotionAckBody(
                FatigueRestoreOutcome.Restored, used: true,
                mutation: new InventoryMutationResult { InstanceValue = 0 },
                listType: InventoryListType.Main, slotIndex: Slot,
                instanceValue: 0, itemTemplateId: Potion50);
            check("success ack echoes mutation remaining (0)",
                remainingZero[0] == 0x01
                && BinaryPrimitives.ReadInt32LittleEndian(remainingZero.AsSpan(4, 4)) == 0);
        }

        private static void VerifyCommittedUse(Action<string, bool> check)
        {
            var dbPath = CreateTempDatabasePath("fatigue-potion-commit");
            try
            {
            var db = new GameDatabase(dbPath, ServerPaths.SchemaFilePath);
            var characterId = 99300;

            // 正常 3→2：消费、恢复、ACK 剩余数量同事务成立。
            characterId++;
            RunScenario(db, characterId, check, "normal 3->2", Potion50, 3, usedPoints: 100,
                expectSuccess: true, expectCount: 2, expectUsedAfter: 50, expectAckRemaining: 2);

            // 2→1、1→0 的 ACK 剩余数量依次为 1、0（各用独立角色）。
            characterId++;
            RunScenario(db, characterId, check, "normal 2->1", Potion50, 2, usedPoints: 100,
                expectSuccess: true, expectCount: 1, expectUsedAfter: 50, expectAckRemaining: 1);
            characterId++;
            RunScenario(db, characterId, check, "normal 1->0", Potion50, 1, usedPoints: 100,
                expectSuccess: true, expectCount: 0, expectUsedAfter: 50, expectAckRemaining: 0);

            // 已用 < 恢复量：Insufficient，整体回滚。
            characterId++;
            RunScenario(db, characterId, check, "insufficient used", Potion50, 3, usedPoints: 10,
                expectSuccess: false, expectCount: 3, expectUsedAfter: 10);

            // 恰好归零；再次使用变 Insufficient。
            characterId++;
            RunScenario(db, characterId, check, "exact zero", Potion50, 2, usedPoints: 50,
                expectSuccess: true, expectCount: 1, expectUsedAfter: 0, expectAckRemaining: 1);
            characterId++;
            VerifyZeroThenInsufficient(db, characterId, check);

            // 每日限用 3 次；第 4 次被 TryRecordUseIfLimited 拒绝（先于恢复裁决）。
            characterId++;
            VerifyDailyLimit(db, characterId, check);

            // 遗留整数计数回填：微点行缺失时按 fatigue_used 折算，恢复写绝对值。
            characterId++;
            VerifyLegacyBackfill(db, characterId, check);

            // 主动回滚：afterUse 拒绝 → 三项全不变。
            characterId++;
            VerifyActiveRollback(db, characterId, check);

            // 持久化失败注入：疲劳计数写入失败 → 道具、疲劳、限用计数全部不变。
            characterId++;
            VerifyPersistenceFailure(db, characterId, check);

            // 过期源：SourceExpiredDeleted 提交、不进入限用与恢复。
            characterId++;
            VerifyExpiredSource(db, characterId, check);

            // 过期 + 疲劳不足 + 每日限用达上限叠加：过期优先。
            characterId++;
            VerifyExpiredWinsOverLimitAndFatigue(db, characterId, check);
            }
            finally
            {
                DeleteTempDatabase(dbPath);
            }
        }

        private static void RunScenario(
            GameDatabase db,
            int characterId,
            Action<string, bool> check,
            string name,
            int itemId,
            int stock,
            int usedPoints,
            bool expectSuccess,
            int expectCount,
            int expectUsedAfter,
            int expectAckRemaining = -1)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-" + name);
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(itemId, stock));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, usedPoints);

                var result = CommitFatiguePotionUse(lease, characterId, itemId, out var restoreOutcome,
                    out var usedBefore, out var usedAfter);

                check(name + ": outcome",
                    restoreOutcome == (expectSuccess
                        ? FatigueRestoreOutcome.Restored
                        : FatigueRestoreOutcome.Insufficient));
                check(name + ": committed flag",
                    (result?.Used == true) == expectSuccess);
                check(name + ": count preserved",
                    (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == expectCount);
                check(name + ": used micros projected",
                    usedBefore == usedPoints * CharacterFatigueService.StorageScale
                    && usedAfter == expectUsedAfter * CharacterFatigueService.StorageScale);
                check(name + ": counter persisted",
                    ReadFatigue(db, characterId) == expectUsedAfter * CharacterFatigueService.StorageScale);
                if (expectAckRemaining >= 0)
                {
                    check(name + ": ack remaining",
                        result?.Mutation != null
                        && result.Mutation.InstanceValue == expectAckRemaining);
                }

                // 重登持久性。
                ReleaseLease(session, characterId, lease);
                session = Guid.NewGuid();
                lease = LoadAndRegister(db, session, accountId, characterId);
                check(name + ": relogin keeps count and counter",
                    (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == expectCount
                    && ReadFatigue(db, characterId) == expectUsedAfter * CharacterFatigueService.StorageScale);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyDailyLimit(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-limit");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(Potion50, 5));
                InventoryPersistenceService.SaveDirty(lease);

                var accepted = 0;
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    // 每轮补足已用疲劳，避开疲劳不足抢答，单独验证限用拒绝。
                    SeedFatigue(db, characterId, 188);
                    var result = CommitFatiguePotionUse(lease, characterId, Potion50,
                        out var restoreOutcome, out _, out _);
                    if (result?.Used == true && restoreOutcome == FatigueRestoreOutcome.Restored)
                        accepted++;
                    // 416 带 [cool time] 1000：连续使用会被 CooltimeActive 拒绝，
                    // 清除冷却态使限用验证不受冷却干扰（生产由客户端节流）。
                    lease.Inventory.ItemStates.Remove(ItemStateKinds.Cooltime, Potion50);
                    check("daily limit attempt " + attempt + " counter projection",
                        ReadFatigue(db, characterId) == (result?.Used == true
                            ? 138L * CharacterFatigueService.StorageScale
                            : 188L * CharacterFatigueService.StorageScale));
                }

                check("daily limit accepts exactly 3 uses",
                    accepted == 3
                    && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 2);

                var limitState = ReadUsableCount(db, characterId, Potion50);
                check("daily limit state records 3",
                    limitState.HasValue && limitState.Value.usedCount == 3 && limitState.Value.limit == 3);

                var rejected = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var rejectedOutcome, out _, out _);
                check("4th use rolls back entirely",
                    rejected == null && rejectedOutcome == null
                    && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 2
                    && ReadFatigue(db, characterId) == 188L * CharacterFatigueService.StorageScale);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyLegacyBackfill(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-legacy");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(Potion50, 2));
                InventoryPersistenceService.SaveDirty(lease);

                var dailyReset = new DailyResetService(db);
                dailyReset.AddCounter(characterId, CharacterFatigueService.CounterKey, 82);

                var result = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var restoreOutcome, out var usedBefore, out var usedAfter);
                check("legacy backfill restores from integer counter",
                    result?.Used == true
                    && restoreOutcome == FatigueRestoreOutcome.Restored
                    && usedBefore == 82L * CharacterFatigueService.StorageScale
                    && usedAfter == 32L * CharacterFatigueService.StorageScale
                    && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 1
                    && ReadFatigue(db, characterId) == 32L * CharacterFatigueService.StorageScale);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyActiveRollback(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-rollback");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(Potion50, 2));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, 100);

                var result = InventoryDeleteCommitService.TryCommitStackableUseDetailed(
                    lease,
                    InventoryListType.Main,
                    Slot,
                    Potion50,
                    afterUse: (connection, transaction, mutation) => false);

                check("afterUse veto rolls back item, fatigue and limit",
                    result == null
                    && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 2
                    && ReadFatigue(db, characterId) == 100L * CharacterFatigueService.StorageScale
                    && ReadUsableCount(db, characterId, Potion50) == null);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyPersistenceFailure(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-persist-fail");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(Potion50, 2));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, 100);

                using (var connection = db.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "CREATE TRIGGER fatigue_potion_fail BEFORE UPDATE ON character_daily_counters " +
                        "FOR EACH ROW WHEN NEW.counter_key = 'fatigue_used_micros' " +
                        "BEGIN SELECT RAISE(ABORT, 'fatigue restore rollback test'); END;";
                    command.ExecuteNonQuery();
                }
                try
                {
                    var result = CommitFatiguePotionUse(lease, characterId, Potion50,
                        out _, out _, out _);
                    check("counter write failure rolls back item, fatigue and limit",
                        result == null
                        && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 2
                        && ReadFatigue(db, characterId) == 100L * CharacterFatigueService.StorageScale
                        && ReadUsableCount(db, characterId, Potion50) == null);
                }
                finally
                {
                    using (var connection = db.OpenConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "DROP TRIGGER IF EXISTS fatigue_potion_fail;";
                        command.ExecuteNonQuery();
                    }
                }
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyExpiredSource(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-expired");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(
                    InventoryListType.Main,
                    Slot,
                    Stack(Potion50, 2, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, 100);

                var result = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var restoreOutcome, out _, out _);
                check("expired source deleted without entering limit/restore",
                    result?.SourceExpiredDeleted == true
                    && result.Used == false
                    && restoreOutcome == null
                    && lease.Inventory.GetItem(InventoryListType.Main, Slot) == null
                    && ReadFatigue(db, characterId) == 100L * CharacterFatigueService.StorageScale
                    && ReadUsableCount(db, characterId, Potion50) == null);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyCrossDayBoundary(Action<string, bool> check)
        {
            var dbPath = CreateTempDatabasePath("fatigue-potion-crossday");
            try
            {
            var db = new GameDatabase(dbPath, ServerPaths.SchemaFilePath);
            var characterId = 99400;
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-crossday");
            var fatigue = new CharacterFatigueService(db);
            var dayOne = new DateTime(2026, 8, 20, 21, 59, 59, DateTimeKind.Utc);
            var dayTwo = new DateTime(2026, 8, 20, 22, 0, 0, DateTimeKind.Utc);

            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                // 同日：100 恢复 50 → 50。
                var dailyReset = new DailyResetService(db);
                check("crossday seed anchors day one",
                    dailyReset.TrySetCounterValue(
                        connection,
                        transaction,
                        characterId,
                        CharacterFatigueService.StoredCounterKey,
                        100L * CharacterFatigueService.StorageScale,
                        dayOne));
                var sameDay = fatigue.TryRestoreInTransaction(
                    connection,
                    transaction,
                    characterId,
                    50,
                    dayOne,
                    out var before,
                    out var after);
                check("same frozen day restores 100->50",
                    sameDay == FatigueRestoreOutcome.Restored
                    && before == 100L * CharacterFatigueService.StorageScale
                    && after == 50L * CharacterFatigueService.StorageScale);

                // 重置回 100（同日锚点），跨日恢复：rollover 归零 → Insufficient，不得写负。
                dailyReset.TrySetCounterValue(
                    connection,
                    transaction,
                    characterId,
                    CharacterFatigueService.StoredCounterKey,
                    100L * CharacterFatigueService.StorageScale,
                    dayOne);
                var crossDay = fatigue.TryRestoreInTransaction(
                    connection,
                    transaction,
                    characterId,
                    50,
                    dayTwo,
                    out var crossBefore,
                    out var crossAfter);
                check("boundary restore rejects after rollover",
                    crossDay == FatigueRestoreOutcome.Insufficient
                    && crossBefore == 0
                    && crossAfter == 0);
                transaction.Commit();
            }

            check("no negative counter survives cross-day restore",
                ReadFatigue(db, characterId) >= 0
                && ReadFatigue(db, characterId) == 0);
            }
            finally
            {
                DeleteTempDatabase(dbPath);
            }
        }


        private static void VerifyZeroThenInsufficient(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            // “exact zero”场景已在 1->0 成功后结束；此处用独立角色钉住“归零后再用必拒”。
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-zero-then-reject");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(InventoryListType.Main, Slot, Stack(Potion50, 2));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, 50);

                var first = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var firstOutcome, out _, out _);
                check("zero-then-reject first use succeeds to zero",
                    first?.Used == true && firstOutcome == FatigueRestoreOutcome.Restored
                    && ReadFatigue(db, characterId) == 0);

                lease.Inventory.ItemStates.Remove(ItemStateKinds.Cooltime, Potion50);
                var second = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var secondOutcome, out _, out _);
                check("zero-then-reject second use is insufficient",
                    second == null && secondOutcome == FatigueRestoreOutcome.Insufficient
                    && (lease.Inventory.GetItem(InventoryListType.Main, Slot)?.Count ?? 0) == 1
                    && ReadFatigue(db, characterId) == 0);
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }

        private static void VerifyExpiredWinsOverLimitAndFatigue(
            GameDatabase db, int characterId, Action<string, bool> check)
        {
            var accountId = characterId + 100;
            SeedOwner(db, accountId, characterId, "potion-expired-combined");
            var session = Guid.NewGuid();
            var lease = LoadAndRegister(db, session, accountId, characterId);
            try
            {
                lease.Inventory.SetItem(
                    InventoryListType.Main,
                    Slot,
                    Stack(Potion50, 2, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
                InventoryPersistenceService.SaveDirty(lease);
                SeedFatigue(db, characterId, 10);

                using (var connection = db.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "INSERT INTO character_usable_count_limits " +
                        "(character_id, item_id, used_count, usable_count_limit, day_id, updated_at) VALUES " +
                        "(@cid, @iid, 3, 3, @today, CURRENT_TIMESTAMP);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@iid", Potion50);
                    command.Parameters.AddWithValue("@today", DailyResetService.TodayId());
                    command.ExecuteNonQuery();
                }

                var result = CommitFatiguePotionUse(lease, characterId, Potion50,
                    out var restoreOutcome, out _, out _);
                check("expired wins over limit and insufficient fatigue",
                    result?.SourceExpiredDeleted == true
                    && result.Used == false
                    && restoreOutcome == null
                    && lease.Inventory.GetItem(InventoryListType.Main, Slot) == null
                    && ReadFatigue(db, characterId) == 10L * CharacterFatigueService.StorageScale);

                using (var connection = db.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT used_count FROM character_usable_count_limits " +
                        "WHERE character_id = @cid AND item_id = @iid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@iid", Potion50);
                    check("expired path leaves usable-count state untouched",
                        Convert.ToInt32(command.ExecuteScalar()) == 3);
                }
            }
            finally
            {
                ReleaseLease(session, characterId, lease);
            }
        }
        /// <summary>以与 handler 相同的调用形态驱动：真实物品 ID + 事务内恢复钩子。</summary>
        private static InventoryStackableUseCommitResult CommitFatiguePotionUse(
            InventoryLease lease,
            int characterId,
            int itemId,
            out FatigueRestoreOutcome? restoreOutcome,
            out long usedBeforeMicros,
            out long usedAfterMicros)
        {
            FatiguePotionDefinition.TryResolve(itemId, out var definition);
            var fatigue = new CharacterFatigueService(lease.Inventory.Database);
            FatigueRestoreOutcome? outcomeCaptured = null;
            long beforeCaptured = 0;
            long afterCaptured = 0;
            var result = InventoryDeleteCommitService.TryCommitStackableUseDetailed(
                lease,
                InventoryListType.Main,
                Slot,
                definition.ItemTemplateId,
                afterUse: (connection, transaction, mutation) =>
                {
                    var outcome = fatigue.TryRestoreInTransaction(
                        connection,
                        transaction,
                        characterId,
                        definition.RestorePoints,
                        out beforeCaptured,
                        out afterCaptured);
                    outcomeCaptured = outcome;
                    return outcome == FatigueRestoreOutcome.Restored;
                });
            restoreOutcome = outcomeCaptured;
            usedBeforeMicros = beforeCaptured;
            usedAfterMicros = afterCaptured;
            return result;
        }

        private static void SeedFatigue(GameDatabase database, int characterId, int usedPoints)
        {
            var dailyReset = new DailyResetService(database);
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "DELETE FROM character_daily_counters WHERE character_id = @cid " +
                        "AND counter_key IN ('fatigue_used_micros', 'fatigue_used');";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.ExecuteNonQuery();
                }
                dailyReset.AddCounter(
                    connection,
                    transaction,
                    characterId,
                    CharacterFatigueService.StoredCounterKey,
                    usedPoints * (int)CharacterFatigueService.StorageScale);
                transaction.Commit();
            }
        }

        private static long ReadFatigue(GameDatabase database, int characterId)
        {
            var dailyReset = new DailyResetService(database);
            return dailyReset.GetCounter(characterId, CharacterFatigueService.StoredCounterKey);
        }

        private static (int usedCount, int limit)? ReadUsableCount(
            GameDatabase database, int characterId, int itemId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT used_count, usable_count_limit FROM character_usable_count_limits " +
                    "WHERE character_id = @cid AND item_id = @iid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@iid", itemId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;
                    return (reader.GetInt32(0), reader.GetInt32(1));
                }
            }
        }


        private static void SeedOwner(GameDatabase database, int accountId, int characterId, string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash, created_at)
VALUES (@aid, @mid, '', '');
INSERT INTO characters (character_id, account_id, name, job, created_at, updated_at)
VALUES (@cid, @aid, @name, 0, '', '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", name);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease LoadAndRegister(
            GameDatabase database,
            Guid sessionId,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
        }

        private static void ReleaseLease(Guid sessionId, int characterId, InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
            InventoryContext.Unregister(sessionId, characterId);
        }

        private static string CreateTempDatabasePath(string hint) =>
            Path.Combine(Path.GetTempPath(), $"dfo_fatigue_potion_{hint}_{Guid.NewGuid():N}.db");

        private static void DeleteTempDatabase(string path)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(path);
                File.Delete(path + "-wal");
                File.Delete(path + "-shm");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARN] temp db cleanup failed: " + ex.Message);
            }
        }

        private static byte[] ActionBody(short slot)
        {
            var body = new byte[19];
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(0, 2), slot);
            body[2] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(3, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7, 4),
                FatiguePotionActionRequestParser.AddFatigueAction);
            return body;
        }

        private static ItemCore Stack(int id, int count, int expiry = 0)
            => new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = id,
                Count = count,
                ExpireTime = expiry,
            };
    }
}
