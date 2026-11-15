using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using System;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Globalization;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonFatiguePolicyKind : byte
    {
        // 规则免费：不检查余额、不发生消费。
        Free = 0,
        // 普通收费副本：每首次进入一个唯一房间扣 Cost（默认 1）。
        PerRoom = 1,
        // 仅进本扣：在入场提交时一次性扣 Cost，房间内不再扣。
        StartOnly = 2,
        // 大会副本：每完成一轮扣 Cost，0 成本直接成功。
        PerTournamentRound = 3,
    }

    // 从当前 PVF DGN 冻结的疲劳策略。无网络、无数据库依赖，
    // 只读 DungeonCatalog.GetDungeonFile 的已解析字段。
    internal sealed class DungeonFatiguePolicy
    {
        internal DungeonFatiguePolicy(
            DungeonFatiguePolicyKind kind,
            int cost,
            bool enterWithoutFatigue)
        {
            Kind = kind;
            Cost = cost;
            EnterWithoutFatigue = enterWithoutFatigue;
        }

        internal DungeonFatiguePolicyKind Kind { get; }
        internal int Cost { get; }
        // 只表示 0 疲劳也允许进本，不表示免费。
        internal bool EnterWithoutFatigue { get; }
    }

    internal static class DungeonFatiguePolicyResolver
    {
        internal const int DefaultPerRoomCost = 1;

        // 优先级：NoFatigue / Fatigue==0 => 免费；TournamentRoundFatigue>=0 => 按轮；
        // UseFatigueOnlyStartDungeon => 仅进本（数值取 UseFatigueOnlyStartDungeonData）；
        // 其余 => 每首次进入一个唯一房间扣 1（Fatigue>0 时按该值扣）。
        // 不维护任何 ID/名称业务名单。
        internal static DungeonFatiguePolicy Resolve(int dungeonId)
        {
            var dungeon = GameWorld.Dungeon.GetDungeonFile(dungeonId)
                ?? throw new InvalidOperationException(
                    $"dungeon definition is missing dungeon={dungeonId}");

            if (dungeon.NoFatigue || dungeon.Fatigue == 0)
                return new DungeonFatiguePolicy(
                    DungeonFatiguePolicyKind.Free,
                    cost: 0,
                    dungeon.EnterWithoutFatigue);

            if (dungeon.TournamentRoundFatigue >= 0)
                return new DungeonFatiguePolicy(
                    DungeonFatiguePolicyKind.PerTournamentRound,
                    dungeon.TournamentRoundFatigue,
                    dungeon.EnterWithoutFatigue);

            if (dungeon.UseFatigueOnlyStartDungeon)
                return new DungeonFatiguePolicy(
                    DungeonFatiguePolicyKind.StartOnly,
                    ParseStartOnlyCost(dungeonId, dungeon),
                    dungeon.EnterWithoutFatigue);

            return new DungeonFatiguePolicy(
                DungeonFatiguePolicyKind.PerRoom,
                dungeon.Fatigue > 0
                    ? dungeon.Fatigue
                    : DefaultPerRoomCost,
                dungeon.EnterWithoutFatigue);
        }

        private static int ParseStartOnlyCost(int dungeonId, PvfLib.DungeonFile dungeon)
        {
            var data = dungeon.UseFatigueOnlyStartDungeonData;
            if (string.IsNullOrWhiteSpace(data))
                throw new InvalidOperationException(
                    "start-only fatigue cost is missing " +
                    $"dungeon={dungeonId}");
            var tokens = data.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0
                || !int.TryParse(tokens[0], out var cost)
                || cost < 0)
            {
                throw new InvalidOperationException(
                    "start-only fatigue cost is invalid " +
                    $"dungeon={dungeonId} data='{data}'");
            }
            return cost;
        }
    }

    internal readonly struct CharacterFatigueSnapshot
    {
        internal CharacterFatigueSnapshot(long used, long max)
        {
            UsedMicros = used * CharacterFatigueService.StorageScale;
            MaxMicros = max * CharacterFatigueService.StorageScale;
            RemainingMicros = Math.Max(0, MaxMicros - UsedMicros);
        }

        private CharacterFatigueSnapshot(long usedMicros, bool stored)
        {
            UsedMicros = usedMicros;
            MaxMicros = CharacterFatigueService.MaxStoredFatigue;
            RemainingMicros = Math.Max(0, MaxMicros - UsedMicros);
        }

        internal long UsedMicros { get; }
        internal long MaxMicros { get; }
        internal long RemainingMicros { get; }
        internal decimal Used =>
            UsedMicros / (decimal)CharacterFatigueService.StorageScale;
        internal decimal Max =>
            MaxMicros / (decimal)CharacterFatigueService.StorageScale;
        internal decimal Remaining =>
            RemainingMicros / (decimal)CharacterFatigueService.StorageScale;

        internal static CharacterFatigueSnapshot FromStored(long usedMicros)
        {
            var clamped = Math.Min(
                Math.Max(0, usedMicros),
                CharacterFatigueService.MaxStoredFatigue);
            return new CharacterFatigueSnapshot(clamped, stored: true);
        }
    }

    internal enum FatigueConsumeOutcome : byte
    {
        // 规则免费（策略免费或成本为 0），未发生消费。
        Free = 0,
        // 已成功扣减并持久化。
        Consumed = 1,
        // 余额不足（达到当日上限），未消费。
        Insufficient = 2,
        // 数据库异常，未消费；调用方必须按失败处理，不得伪装成免费。
        PersistenceFailed = 3,
    }

    // 角色疲劳真源：character_daily_counters 的 fatigue_used_micros 日计数器，
    // 以整数微点数保存小数疲劳；旧 fatigue_used 整数计数器仅作首次兼容回填。
    internal sealed class CharacterFatigueService
    {
        internal const string EnvironmentVariableName = "DUNGEON_FATIGUE_RATE";
        internal const string CounterKey = "fatigue_used";
        internal const string StoredCounterKey = "fatigue_used_micros";
        internal const long StorageScale = 1_000_000;
        internal const int MaxFatigue = 188;
        internal const long MaxStoredFatigue = MaxFatigue * StorageScale;

        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;
        private readonly long _consumptionRateMicros;

        internal CharacterFatigueService(IGameDatabase database)
            : this(database, new DailyResetService(database), null)
        {
        }

        internal CharacterFatigueService(
            IGameDatabase database,
            DailyResetService dailyReset)
            : this(database, dailyReset, null)
        {
        }

        internal CharacterFatigueService(
            IGameDatabase database,
            DailyResetService dailyReset,
            long? consumptionRateMicros)
        {
            _database = database
                ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset
                ?? throw new ArgumentNullException(nameof(dailyReset));
            _consumptionRateMicros = consumptionRateMicros
                ?? ReadConsumptionRateMicros();
            if (_consumptionRateMicros < 0)
                throw new ArgumentOutOfRangeException(nameof(consumptionRateMicros));
        }

        internal static long ParseConsumptionRateMicros(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            if (!decimal.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var rate)
                || rate < 0
                || rate > long.MaxValue / (decimal)StorageScale)
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariableName} must be a non-negative decimal.");
            }
            return (long)Math.Round(
                rate * StorageScale,
                MidpointRounding.AwayFromZero);
        }

        private static long ReadConsumptionRateMicros()
            => ParseConsumptionRateMicros(
                Environment.GetEnvironmentVariable(EnvironmentVariableName));

        internal CharacterFatigueSnapshot GetSnapshot(int characterId)
        {
            if (characterId <= 0)
                return CharacterFatigueSnapshot.FromStored(0);
            var usedMicros = _dailyReset.GetCounter(
                characterId,
                StoredCounterKey);
            if (usedMicros == 0)
            {
                var legacyUsed = _dailyReset.GetCounter(characterId, CounterKey);
                if (legacyUsed > 0 && legacyUsed <= MaxFatigue)
                    usedMicros = legacyUsed * StorageScale;
            }
            return CharacterFatigueSnapshot.FromStored(usedMicros);
        }

        // 入场准入：按策略要求检查余额。返回 false 时 failureKind 区分
        // InsufficientFatigue（余额不足）与 InvalidState（读取失败，fail closed）。
        internal bool TryValidateAdmission(
            int characterId,
            int dungeonId,
            out EntryCostFailureKind failureKind,
            out string failureReason)
        {
            failureKind = EntryCostFailureKind.None;
            failureReason = string.Empty;
            DungeonFatiguePolicy policy;
            try
            {
                policy = DungeonFatiguePolicyResolver.Resolve(dungeonId);
            }
            catch (Exception ex)
            {
                failureKind = EntryCostFailureKind.Unavailable;
                failureReason = "fatigue policy unavailable: " + ex.Message;
                return false;
            }
            if (policy.Kind == DungeonFatiguePolicyKind.Free)
                return true;

            CharacterFatigueSnapshot snapshot;
            try
            {
                snapshot = GetSnapshot(characterId);
            }
            catch (Exception ex)
            {
                failureKind = EntryCostFailureKind.InvalidState;
                failureReason = "fatigue snapshot unavailable: " + ex.Message;
                return false;
            }

            return TryValidateAdmission(
                snapshot,
                policy,
                out failureKind,
                out failureReason);
        }

        internal bool TryValidateAdmission(
            CharacterFatigueSnapshot snapshot,
            DungeonFatiguePolicy policy,
            out EntryCostFailureKind failureKind,
            out string failureReason)
        {
            failureKind = EntryCostFailureKind.None;
            failureReason = string.Empty;
            if (policy == null || policy.Kind == DungeonFatiguePolicyKind.Free)
                return true;

            var baseCost = policy.Kind == DungeonFatiguePolicyKind.PerRoom
                ? Math.Max(1, policy.Cost)
                : Math.Max(0, policy.Cost);
            var requiredMicros = ScaleCost(baseCost);
            if (requiredMicros == 0
                || snapshot.RemainingMicros >= requiredMicros
                || policy.EnterWithoutFatigue)
                return true;

            failureKind = EntryCostFailureKind.InsufficientFatigue;
            failureReason = "fatigue insufficient " +
                $"used={snapshot.Used} remaining={snapshot.Remaining} " +
                $"required={requiredMicros / (decimal)StorageScale} policy={policy.Kind}";
            return false;
        }

        // 仅进本扣副本的入场消费额：EnterWithoutFatigue 且余额不足整个入场成本时
        // 允许进本且不消费（0 疲劳进本）；其余情况按成本足额消费，
        // 由 TryConsumeInTransaction 的原子守卫最终兜底。
        internal int ResolveStartOnlyEntryCost(int characterId, int dungeonId)
        {
            var policy = DungeonFatiguePolicyResolver.Resolve(dungeonId);
            if (policy.Kind != DungeonFatiguePolicyKind.StartOnly
                || policy.Cost <= 0
                || _consumptionRateMicros == 0)
                return 0;
            if (!policy.EnterWithoutFatigue)
                return policy.Cost;

            var snapshot = GetSnapshot(characterId);
            return snapshot.RemainingMicros >= ScaleCost(policy.Cost)
                ? policy.Cost
                : 0;
        }

        internal FatigueConsumeOutcome TryChargeRoomEntry(
            int characterId,
            int dungeonId,
            bool roomEntryTransitioned)
        {
            if (!roomEntryTransitioned)
                return FatigueConsumeOutcome.Free;
            var policy = DungeonFatiguePolicyResolver.Resolve(dungeonId);
            if (policy.Kind != DungeonFatiguePolicyKind.PerRoom
                || policy.Cost <= 0)
                return FatigueConsumeOutcome.Free;
            return TryConsume(characterId, policy.Cost);
        }

        internal FatigueConsumeOutcome TryChargeTournamentRound(
            int characterId,
            int dungeonId)
        {
            var policy = DungeonFatiguePolicyResolver.Resolve(dungeonId);
            if (policy.Kind != DungeonFatiguePolicyKind.PerTournamentRound
                || policy.Cost <= 0)
                return FatigueConsumeOutcome.Free;
            return TryConsume(characterId, policy.Cost);
        }

        internal FatigueConsumeOutcome TryConsume(int characterId, int cost)
        {
            var scaledCost = ScaleCost(cost);
            if (scaledCost == 0)
                return FatigueConsumeOutcome.Free;
            try
            {
                using (var conn = _database.OpenConnection())
                using (var tx = conn.BeginTransaction())
                {
                    var consumed = TryConsumeScaledInTransaction(
                        conn,
                        tx,
                        characterId,
                        scaledCost);
                    tx.Commit();
                    return consumed
                        ? FatigueConsumeOutcome.Consumed
                        : FatigueConsumeOutcome.Insufficient;
                }
            }
            catch (Exception)
            {
                return FatigueConsumeOutcome.PersistenceFailed;
            }
        }

        // 入场物品/金币同事务变体。cost 是 PVF 基础点数，此处统一应用倍率。
        internal bool TryConsumeInTransaction(
            DbConnection conn,
            DbTransaction tx,
            int characterId,
            int cost)
        {
            var scaledCost = ScaleCost(cost);
            if (scaledCost == 0)
                return true;
            return TryConsumeScaledInTransaction(
                (SqliteConnection)conn,
                (SqliteTransaction)tx,
                characterId,
                scaledCost);
        }

        private bool TryConsumeScaledInTransaction(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            long scaledCost)
        {
            LockCharacter(conn, tx, characterId);
            var stored = _dailyReset.GetCounter(conn, tx, characterId, StoredCounterKey);
            var legacyBase = 0L;
            if (stored == 0)
            {
                var legacy = _dailyReset.GetCounter(conn, tx, characterId, CounterKey);
                if (legacy > 0 && legacy <= MaxFatigue)
                    legacyBase = legacy * StorageScale;
            }
            return DailyResetService.TryAddCounterAtomic(
                conn,
                tx,
                characterId,
                StoredCounterKey,
                legacyBase + scaledCost,
                MaxStoredFatigue);
        }

        private long ScaleCost(int baseCost)
        {
            if (baseCost <= 0 || _consumptionRateMicros == 0)
                return 0;
            return (long)(baseCost * (decimal)_consumptionRateMicros);
        }

        private static void LockCharacter(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId)
        {
            using (var command = conn.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText =
                    "SELECT character_id FROM characters " +
                    "WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                if (command.ExecuteScalar() == null)
                    throw new InvalidOperationException(
                        $"fatigue character is missing cid={characterId}");
            }
        }
    }
}
