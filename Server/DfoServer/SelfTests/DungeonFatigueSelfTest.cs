using System;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    // 疲劳值聚焦自测：PVF 策略解析、按房消费、188 封顶、06:00 跨天、
    // 持久化重读、入场拒绝、startOnly/大会成本、数据库失败不伪造成功、
    // 选角 ACK usedFatigue 投影。全部使用隔离 dfo_st_* schema 与真实线上 PVF。
    public static class DungeonFatigueSelfTest
    {
        private const int CharacterId = 58301;
        private const int AccountId = 58300;

        // 真实当前 PVF 的代表副本：
        // 1=普通按房；210=免疲劳团本；2000=战场(Fatigue0)；62=异界 startOnly(成本8)；
        // 120/121=大会每轮2；122=黑龙大会每轮0 且 EnterWithoutFatigue；4119=天界皇家武斗大会每轮0。
        private const int OrdinaryDungeonId = 1;
        private const int FreeRaidDungeonId = 210;
        private const int BattlefieldDungeonId = 2000;
        private const int StartOnlyDungeonId = 62;
        private const int TournamentTwoCostDungeonId = 120;
        private const int TournamentOtherTwoCostDungeonId = 121;
        private const int TournamentZeroCostDungeonId = 122;
        private const int TournamentZeroCostExtraDungeonId = 4119;

        private static SelfTestSharedDatabase _shared;

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_FATIGUE selftest ===");
            try
            {
                return RunScenarios();
            }
            finally
            {
                var shared = _shared;
                _shared = null;
                shared?.Dispose();
            }
        }

        private static int RunScenarios()
        {
            var failures = 0;

            VerifyPolicyAgainstCurrentPvf(ref failures);
            VerifyRateParsing(ref failures);
            failures += WithDatabase(
                "rate",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyRateConsumption(database, ref local);
                    return local;
                });
            failures += WithDatabase(
                "legacy",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyLegacyCounterMigration(database, ref local);
                    return local;
                });
            failures += WithDatabase(
                "room-clear",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyOrdinaryMultiRoom(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "cap",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyCapAndInsufficient(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "reread",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyPersistenceReread(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "rollover",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyDailyRollover(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "admission",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyAdmission(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "start-only",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyStartOnlyCost(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "tournament",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyTournamentRoundCharging(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "persistence-failure",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyPersistenceFailure(database, fatigue, ref local);
                    return local;
                });
            failures += WithDatabase(
                "ack-projection",
                (database, fatigue) =>
                {
                    var local = 0;
                    VerifyAckUsedFatigueProjection(database, fatigue, ref local);
                    return local;
                });

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_FATIGUE selftest passed."
                    : $"DUNGEON_FATIGUE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRateParsing(ref int failures)
        {
            var invalidRejected = false;
            try
            {
                CharacterFatigueService.ParseConsumptionRateMicros("-0.5");
            }
            catch (InvalidOperationException)
            {
                invalidRejected = true;
            }
            Check(
                "fatigue rate defaults to 0 and parses decimal multipliers",
                CharacterFatigueService.ParseConsumptionRateMicros(null) == 0
                    && CharacterFatigueService.ParseConsumptionRateMicros("0.5")
                        == CharacterFatigueService.StorageScale / 2
                    && CharacterFatigueService.ParseConsumptionRateMicros("1")
                        == CharacterFatigueService.StorageScale,
                ref failures);
            Check(
                "negative fatigue rates are rejected",
                invalidRejected,
                ref failures);
        }

        private static void VerifyRateConsumption(
            IGameDatabase database,
            ref int failures)
        {
            var zero = CreateFatigue(database, 0);
            var half = CreateFatigue(
                database,
                CharacterFatigueService.StorageScale / 2);
            var zeroCharge = zero.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);
            var zeroAdmission = zero.TryValidateAdmission(
                new CharacterFatigueSnapshot(188, 188),
                new DungeonFatiguePolicy(
                    DungeonFatiguePolicyKind.PerRoom,
                    1,
                    enterWithoutFatigue: false),
                out _,
                out _);
            var zeroSnapshot = zero.GetSnapshot(CharacterId);
            var freeCharge = half.TryChargeRoomEntry(
                CharacterId,
                FreeRaidDungeonId,
                roomEntryTransitioned: true);
            var firstHalf = half.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);
            var secondHalf = half.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);
            var afterRooms = half.GetSnapshot(CharacterId);
            var tournament = half.TryChargeTournamentRound(
                CharacterId,
                TournamentTwoCostDungeonId);
            bool startOnly;
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                startOnly = half.TryConsumeInTransaction(
                    connection,
                    transaction,
                    CharacterId,
                    8);
                transaction.Commit();
            }
            var final = CreateFatigue(
                database,
                CharacterFatigueService.StorageScale / 2)
                .GetSnapshot(CharacterId);

            Check(
                "rate 0 disables charging and its admission requirement",
                zeroCharge == FatigueConsumeOutcome.Free
                    && zeroAdmission
                    && zeroSnapshot.Used == 0,
                ref failures);
            Check(
                "rate 0.5 persists half points and two rooms total one point",
                freeCharge == FatigueConsumeOutcome.Free
                    && firstHalf == FatigueConsumeOutcome.Consumed
                    && secondHalf == FatigueConsumeOutcome.Consumed
                    && afterRooms.Used == 1,
                ref failures);
            Check(
                "the rate also scales tournament and start-only base costs",
                tournament == FatigueConsumeOutcome.Consumed
                    && startOnly
                    && final.Used == 6,
                ref failures);
        }

        private static void VerifyLegacyCounterMigration(
            IGameDatabase database,
            ref int failures)
        {
            new DfoServer.Game.DailyReset.DailyResetService(database).AddCounter(
                CharacterId,
                CharacterFatigueService.CounterKey,
                5);
            var half = CreateFatigue(
                database,
                CharacterFatigueService.StorageScale / 2);
            var before = half.GetSnapshot(CharacterId);
            var consumed = half.TryConsume(CharacterId, 1);
            var after = half.GetSnapshot(CharacterId);
            Check(
                "legacy integer fatigue is retained when fractional charging starts",
                before.Used == 5
                    && consumed == FatigueConsumeOutcome.Consumed
                    && after.Used == 5.5m
                    && ReadCounterDirect(database) == 5_500_000,
                ref failures);
        }

        private static void VerifyPolicyAgainstCurrentPvf(ref int failures)
        {
            var ordinary = DungeonFatiguePolicyResolver.Resolve(
                OrdinaryDungeonId);
            Check(
                "ordinary dungeon resolves to per-room cost 1 " +
                "(Fatigue=-1 is not free)",
                ordinary.Kind == DungeonFatiguePolicyKind.PerRoom
                    && ordinary.Cost == 1,
                ref failures);

            var freeRaid = DungeonFatiguePolicyResolver.Resolve(
                FreeRaidDungeonId);
            Check(
                "NoFatigue raid dungeon resolves to free",
                freeRaid.Kind == DungeonFatiguePolicyKind.Free
                    && freeRaid.Cost == 0,
                ref failures);

            var battlefield = DungeonFatiguePolicyResolver.Resolve(
                BattlefieldDungeonId);
            Check(
                "battlefield with Fatigue==0 resolves to free",
                battlefield.Kind == DungeonFatiguePolicyKind.Free
                    && battlefield.Cost == 0,
                ref failures);

            var startOnly = DungeonFatiguePolicyResolver.Resolve(
                StartOnlyDungeonId);
            Check(
                "dimension dungeon resolves to start-only cost 8",
                startOnly.Kind == DungeonFatiguePolicyKind.StartOnly
                    && startOnly.Cost == 8,
                ref failures);

            var tournamentTwo = DungeonFatiguePolicyResolver.Resolve(
                TournamentTwoCostDungeonId);
            var tournamentOtherTwo = DungeonFatiguePolicyResolver.Resolve(
                TournamentOtherTwoCostDungeonId);
            Check(
                "yellow/blue dragon tournament resolves to per-round cost 2",
                tournamentTwo.Kind
                    == DungeonFatiguePolicyKind.PerTournamentRound
                    && tournamentTwo.Cost == 2
                    && tournamentOtherTwo.Kind
                        == DungeonFatiguePolicyKind.PerTournamentRound
                    && tournamentOtherTwo.Cost == 2,
                ref failures);

            var tournamentZero = DungeonFatiguePolicyResolver.Resolve(
                TournamentZeroCostDungeonId);
            var tournamentZeroExtra = DungeonFatiguePolicyResolver.Resolve(
                TournamentZeroCostExtraDungeonId);
            Check(
                "black dragon / celestial tournament resolves to per-round " +
                "cost 0 with EnterWithoutFatigue",
                tournamentZero.Kind
                    == DungeonFatiguePolicyKind.PerTournamentRound
                    && tournamentZero.Cost == 0
                    && tournamentZero.EnterWithoutFatigue
                    && tournamentZeroExtra.Kind
                        == DungeonFatiguePolicyKind.PerTournamentRound
                    && tournamentZeroExtra.Cost == 0,
                ref failures);
        }

        private static void VerifyOrdinaryMultiRoom(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            var firstRoomState = new RoomState();
            var secondRoomState = new RoomState();
            var firstRoom = fatigue.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                firstRoomState.TryActivate());
            var duplicateClear = fatigue.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                firstRoomState.TryActivate());
            var secondRoom = fatigue.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                secondRoomState.TryActivate());
            var freeRaid = fatigue.TryChargeRoomEntry(
                CharacterId,
                FreeRaidDungeonId,
                roomEntryTransitioned: true);
            var battlefield = fatigue.TryChargeRoomEntry(
                CharacterId,
                BattlefieldDungeonId,
                roomEntryTransitioned: true);
            var snapshot = fatigue.GetSnapshot(CharacterId);

            Check(
                "each newly activated room charges exactly 1 and " +
                "repeated activation or revisits charge nothing",
                firstRoom == FatigueConsumeOutcome.Consumed
                    && duplicateClear == FatigueConsumeOutcome.Free
                    && secondRoom == FatigueConsumeOutcome.Consumed
                    && snapshot.Used == 2
                    && snapshot.Remaining
                        == CharacterFatigueService.MaxFatigue - 2,
                ref failures);
            Check(
                "NoFatigue and Fatigue==0 dungeons never charge",
                freeRaid == FatigueConsumeOutcome.Free
                    && battlefield == FatigueConsumeOutcome.Free
                    && snapshot.Used == 2,
                ref failures);
        }

        private static void VerifyCapAndInsufficient(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            var filled = fatigue.TryConsume(
                CharacterId,
                CharacterFatigueService.MaxFatigue);
            var capped = fatigue.GetSnapshot(CharacterId);
            var over = fatigue.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);
            var after = fatigue.GetSnapshot(CharacterId);

            Check(
                "consuming the full 188 cap succeeds and stays capped",
                filled == FatigueConsumeOutcome.Consumed
                    && capped.Used == CharacterFatigueService.MaxFatigue
                    && capped.Remaining == 0,
                ref failures);
            Check(
                "charging past the cap reports Insufficient without moving " +
                "the stored value",
                over == FatigueConsumeOutcome.Insufficient
                    && after.Used == CharacterFatigueService.MaxFatigue,
                ref failures);
        }

        private static void VerifyPersistenceReread(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            fatigue.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);

            var recreated = CreateFatigue(database);
            var snapshot = recreated.GetSnapshot(CharacterId);
            Check(
                "a reconstructed service rereads the persisted used value",
                snapshot.Used == 1,
                ref failures);
        }

        private static void VerifyDailyRollover(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            fatigue.TryConsume(CharacterId, 40);
            var before = fatigue.GetSnapshot(CharacterId);

            ForcePreviousGameDay(database);

            var recreated = CreateFatigue(database);
            var after = recreated.GetSnapshot(CharacterId);
            var recharged = recreated.TryChargeRoomEntry(
                CharacterId,
                OrdinaryDungeonId,
                roomEntryTransitioned: true);
            var final = recreated.GetSnapshot(CharacterId);

            Check(
                "the Beijing 06:00 game-day rollover zeroes used fatigue and " +
                "charging works again on the new day",
                before.Used == 40
                    && after.Used == 0
                    && after.Remaining == CharacterFatigueService.MaxFatigue
                    && recharged == FatigueConsumeOutcome.Consumed
                    && final.Used == 1,
                ref failures);
        }

        private static void VerifyAdmission(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            fatigue.TryConsume(CharacterId, CharacterFatigueService.MaxFatigue);
            var exhausted = fatigue.GetSnapshot(CharacterId);
            Check(
                "setup reached the exhausted snapshot",
                exhausted.Remaining == 0,
                ref failures);

            var ordinaryAllowed = fatigue.TryValidateAdmission(
                CharacterId,
                OrdinaryDungeonId,
                out var ordinaryKind,
                out _);
            Check(
                "an exhausted character is rejected from an ordinary " +
                "charged dungeon with InsufficientFatigue",
                !ordinaryAllowed
                    && ordinaryKind == EntryCostFailureKind.InsufficientFatigue,
                ref failures);

            var freeAllowed = fatigue.TryValidateAdmission(
                CharacterId,
                FreeRaidDungeonId,
                out var freeKind,
                out _);
            Check(
                "rule-free dungeons admit an exhausted character",
                freeAllowed && freeKind == EntryCostFailureKind.None,
                ref failures);

            var enterWithoutFatigue = new DungeonFatiguePolicy(
                DungeonFatiguePolicyKind.PerRoom,
                cost: 1,
                enterWithoutFatigue: true);
            var bypassAllowed = fatigue.TryValidateAdmission(
                exhausted,
                enterWithoutFatigue,
                out var bypassKind,
                out _);
            var strictStartOnly = new DungeonFatiguePolicy(
                DungeonFatiguePolicyKind.StartOnly,
                cost: 8,
                enterWithoutFatigue: false);
            var shortStartOnly = fatigue.TryValidateAdmission(
                exhausted,
                strictStartOnly,
                out var shortKind,
                out _);
            var enoughForStartOnly = new CharacterFatigueSnapshot(180, 188);
            var affordStartOnly = fatigue.TryValidateAdmission(
                enoughForStartOnly,
                strictStartOnly,
                out _,
                out _);
            var enterWithoutFatigueStartOnly =
                fatigue.TryValidateAdmission(
                    exhausted,
                    new DungeonFatiguePolicy(
                        DungeonFatiguePolicyKind.StartOnly,
                        cost: 8,
                        enterWithoutFatigue: true),
                    out _,
                    out _);
            Check(
                "EnterWithoutFatigue only bypasses the admission gate and " +
                "startOnly still requires the full cost otherwise",
                bypassAllowed
                    && bypassKind == EntryCostFailureKind.None
                    && !shortStartOnly
                    && shortKind == EntryCostFailureKind.InsufficientFatigue
                    && affordStartOnly
                    && enterWithoutFatigueStartOnly,
                ref failures);

            var zeroRemainingTournament = fatigue.TryValidateAdmission(
                    exhausted,
                    new DungeonFatiguePolicy(
                        DungeonFatiguePolicyKind.PerTournamentRound,
                        cost: 0,
                        enterWithoutFatigue: true),
                    out _,
                    out _);
            Check(
                "a zero-cost tournament admits an exhausted character",
                zeroRemainingTournament,
                ref failures);
        }

        private static void VerifyStartOnlyCost(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            var fullCost = fatigue.ResolveStartOnlyEntryCost(
                CharacterId,
                StartOnlyDungeonId);
            fatigue.TryConsume(CharacterId, 181);
            var shortCost = fatigue.ResolveStartOnlyEntryCost(
                CharacterId,
                StartOnlyDungeonId);
            var ordinaryNoCost = fatigue.ResolveStartOnlyEntryCost(
                CharacterId,
                OrdinaryDungeonId);

            Check(
                "startOnly resolves the full PVF cost while affordable and " +
                "ordinary dungeons never charge at entry",
                fullCost == 8
                    && shortCost == 8
                    && ordinaryNoCost == 0,
                ref failures);

            // 入场提交在同一事务内消费：余额不足整单回滚，不出现物品已扣但
            // 疲劳未扣的部分提交。
            using (var conn = database.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                var consumed = fatigue.TryConsumeInTransaction(
                    conn,
                    tx,
                    CharacterId,
                    8);
                tx.Rollback();
                Check(
                    "the transactional entry consume reports Insufficient " +
                    "without partial state",
                    !consumed,
                    ref failures);
            }
        }

        private static void VerifyTournamentRoundCharging(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            var firstRound = fatigue.TryChargeTournamentRound(
                CharacterId,
                TournamentTwoCostDungeonId);
            var secondRound = fatigue.TryChargeTournamentRound(
                CharacterId,
                TournamentTwoCostDungeonId);
            var zeroCostRound = fatigue.TryChargeTournamentRound(
                CharacterId,
                TournamentZeroCostDungeonId);
            var snapshot = fatigue.GetSnapshot(CharacterId);

            Check(
                "each verified tournament round charges exactly the " +
                "per-round cost and zero-cost rounds charge nothing",
                firstRound == FatigueConsumeOutcome.Consumed
                    && secondRound == FatigueConsumeOutcome.Consumed
                    && zeroCostRound == FatigueConsumeOutcome.Free
                    && snapshot.Used == 4,
                ref failures);
        }

        private static void VerifyPersistenceFailure(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            InjectFatigueCounterFailure(database);

            var outcome = fatigue.TryConsume(CharacterId, 1);
            var snapshotAfterConsume = ReadCounterDirect(database);

            // 未来 day_id 让 DailyResetService 判定锚点 stale：读取路径按
            // InvalidState fail closed，这是读写时结算的真实失败语义。
            ForceFutureGameDay(database);
            var staleConsume = fatigue.TryConsume(CharacterId, 1);
            var admission = fatigue.TryValidateAdmission(
                CharacterId,
                OrdinaryDungeonId,
                out var admissionKind,
                out _);
            var admissionFree = fatigue.TryValidateAdmission(
                CharacterId,
                FreeRaidDungeonId,
                out _,
                out _);

            Check(
                "a database failure is reported as PersistenceFailed, never " +
                "as free or consumed",
                outcome == FatigueConsumeOutcome.PersistenceFailed
                    && snapshotAfterConsume == 0,
                ref failures);
            Check(
                "a stale daily-reset anchor fails closed the admission gate " +
                "but rule-free dungeons still admit",
                staleConsume == FatigueConsumeOutcome.PersistenceFailed
                    && !admission
                    && admissionKind == EntryCostFailureKind.InvalidState
                    && admissionFree,
                ref failures);
        }

        private static void VerifyAckUsedFatigueProjection(
            IGameDatabase database,
            CharacterFatigueService fatigue,
            ref int failures)
        {
            fatigue.TryConsume(CharacterId, 5);

            var snapshot = new SelectCharacterDataSnapshot
            {
                InitializationSnapshot = new SelectCharacterInitializationSnapshot(),
                CharacterRecord = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    AccountId = AccountId,
                    CreatedAt = DateTime.UnixEpoch,
                },
            };
            var built = SelectCharacterAckBodyBuilder.TryBuild(
                snapshot,
                database,
                out var body);
            var used = body != null && body.Length >= 17
                ? BitConverter.ToInt16(body, 15)
                : (short)-1;
            var max = body != null && body.Length >= 15
                ? BitConverter.ToInt16(body, 13)
                : (short)-1;
            var displayUsed = body != null && body.Length >= 13
                ? BitConverter.ToInt16(body, 11)
                : (short)-1;

            Check(
                "the select-character ACK projects the authoritative used " +
                "fatigue and keeps the verified 188 max layout",
                built
                    && used == 5
                    && displayUsed == 5
                    && max == CharacterFatigueService.MaxFatigue,
                ref failures);

            var tail = new UserInfoMinimumTailSnapshot();
            UserInfoSubtype0Builder.ProjectFatigueTailFields(
                tail,
                CharacterFatigueSnapshot.FromStored(5_500_000));
            var userInfoTail = UserInfoSubtype0Builder.BuildA21AfterAlive(tail);
            Check(
                "USERINFO projects the same rounded used fatigue",
                BitConverter.ToUInt16(
                    userInfoTail,
                    UserInfoSubtype0Builder.A21AfterAliveFatigueUpdateOffset) == 6,
                ref failures);

            var notification = FatigueNotificationBuilder.Build(
                CharacterFatigueSnapshot.FromStored(5_500_000));
            Check(
                "FATIGUE notification keeps the client-proven 8-byte layout",
                notification.Length == FatigueNotificationBuilder.BodyLength
                    && BitConverter.ToUInt16(notification, 0) == 6
                    && BitConverter.ToUInt16(notification, 2)
                        == CharacterFatigueService.MaxFatigue
                    && BitConverter.ToUInt16(notification, 4) == 6
                    && BitConverter.ToUInt16(notification, 6) == 0,
                ref failures);
        }

        private static int WithDatabase(
            string suffix,
            Func<IGameDatabase, CharacterFatigueService, int> action)
        {
            _shared ??= SelfTestSharedDatabase.Create("dungeon-fatigue");
            _shared.Reset();
            var database = _shared.Database;
            SeedCharacter(database, suffix);
            return action(database, CreateFatigue(database));
        }

        private static CharacterFatigueService CreateFatigue(
            IGameDatabase database,
            long consumptionRateMicros = CharacterFatigueService.StorageScale)
        {
            return new CharacterFatigueService(
                database,
                new DfoServer.Game.DailyReset.DailyResetService(database),
                consumptionRateMicros);
        }

        private static void SeedCharacter(IGameDatabase database, string suffix)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash, created_at)
VALUES (@aid, @mid, '', '');";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@mid", "dungeon-fatigue-" + suffix);
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job, created_at, updated_at)
VALUES (@cid, @aid, @name, 0, '', '');";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@name", "fatigue-" + suffix);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void ForcePreviousGameDay(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE character_daily_reset
SET day_id = 0
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void ForceFutureGameDay(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO character_daily_reset (character_id, day_id, week_id)
VALUES (@cid, 99999999, 0)
ON CONFLICT(character_id) DO UPDATE SET day_id = 99999999;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void InjectFatigueCounterFailure(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TRIGGER fail_fatigue_counter
BEFORE INSERT ON character_daily_counters
FOR EACH ROW WHEN NEW.counter_key = 'fatigue_used_micros'
BEGIN
    SELECT RAISE(ABORT, 'injected fatigue counter failure');
END;";
                command.ExecuteNonQuery();
            }
        }

        private static long ReadCounterDirect(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT value
FROM character_daily_counters
WHERE character_id = @cid AND counter_key = @key;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@key", CharacterFatigueService.StoredCounterKey);
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
