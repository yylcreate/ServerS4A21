using System;
using System.Globalization;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.GuardianAltar
{
    // Durable role progress is the only menu-state authority. Ordinary clears
    // use the caller's inventory/outbox transaction; socket projections and
    // combat state remain with the Guardian coordinator/runtime.
    internal sealed class GuardianAltarProgressService : IGuardianAltarProgressService
    {
        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;
        private readonly Lazy<GuardianAltarMenuDefinition> _definition;
        private readonly GuardianAltarProgressRepository _repository = new();

        internal GuardianAltarProgressService(IGameDatabase database, DailyResetService dailyReset,
            GuardianAltarMenuDefinition definition)
            : this(database, dailyReset, definition == null
                ? throw new ArgumentNullException(nameof(definition)) : () => definition)
        {
        }

        internal GuardianAltarProgressService(IGameDatabase database, DailyResetService dailyReset,
            Func<GuardianAltarMenuDefinition> definitionLoader)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset ?? throw new ArgumentNullException(nameof(dailyReset));
            ArgumentNullException.ThrowIfNull(definitionLoader);
            _definition = new Lazy<GuardianAltarMenuDefinition>(() => definitionLoader()
                ?? throw new InvalidOperationException("Guardian menu definition loader returned no definition."));
        }

        public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow,
            Func<bool> isCurrent = null)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Guardian progress requires an explicit UTC anchor.", nameof(utcNow));
            var definition = _definition.Value;
            var refillKey = "guardian_altar_refill_v1:"
                + definition.RidableId.ToString(CultureInfo.InvariantCulture);
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacter(connection, transaction, characterId);
                _repository.ValidateRefillReceipt(connection, transaction, characterId, refillKey);
                _repository.TryInitialize(connection, transaction, characterId, definition);

                // Validate existing facts before normalization. Reopening a menu
                // must not silently recreate missing purchases, slots or stages.
                var existing = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(existing, definition);
                if (!_dailyReset.TryGetCounter(connection, transaction, characterId, refillKey,
                        DailyResetService.PeriodDay, utcNow, out var receipt))
                    throw new InvalidOperationException("Guardian refill anchor is older than persisted daily state.");
                if (receipt < 0 || receipt > 1)
                    throw new InvalidOperationException("Guardian daily refill receipt is inconsistent.");
                if (receipt == 0)
                {
                    if (!_dailyReset.TryClaimFlag(connection, transaction, characterId, refillKey,
                            DailyResetService.PeriodDay, utcNow))
                        throw new InvalidOperationException("Guardian daily refill could not reserve its current-day receipt.");
                    // This is the verified daily replacement rule, not += and
                    // not a reset of secondary tickets or any persistent stars.
                    _repository.ReplaceFreeTickets(connection, transaction, characterId,
                        definition.RidableId, definition.DailyRefillTicket);
                }
                var result = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(result, definition);
                RequireCurrentGeneration(isCurrent);
                return result;
            });
        }

        internal string DatabaseConnectionString => _database.ConnectionString;

        // Call before the inventory lock/SQLite transaction. Recovery may be
        // this owner's first operation and must not perform lazy PVF I/O there.
        internal void PrepareClearDefinition(GuardianAltarClearProgressPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            RequireMatchingClearPlan(plan, _definition.Value);
        }

        private static void RequireMatchingClearPlan(GuardianAltarClearProgressPlan plan,
            GuardianAltarMenuDefinition definition)
        {
            var expected = definition.CreateClearProgressPlan(plan.DungeonId, plan.Difficulty);
            if (plan.RidableId != expected.RidableId || plan.StarsOnImprovement != expected.StarsOnImprovement
                || !plan.NextDungeonIds.SequenceEqual(expected.NextDungeonIds)
                || !plan.UnlockPurchases.SequenceEqual(expected.UnlockPurchases))
                throw new InvalidOperationException("Guardian frozen clear progress plan does not match its resource owner.");
        }

        private static void RequireCurrentGeneration(Func<bool> isCurrent)
        {
            if (isCurrent != null && !isCurrent())
                throw new InvalidOperationException("Guardian progress request belongs to an obsolete generation.");
        }

        internal GuardianAltarClearProgressCommitResult ApplyClearInTransaction(
            SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId,
            GuardianAltarClearProgressPlan plan, Func<bool> isCurrent = null)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(plan);
            RequireCurrentGeneration(isCurrent);
            if (!_definition.IsValueCreated)
                throw new InvalidOperationException("Guardian clear definition must be prepared before the transaction.");
            var definition = _definition.Value;
            RequireMatchingClearPlan(plan, definition);
            _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
            var before = _repository.Read(connection, transaction, characterId, plan.RidableId);
            ValidateComplete(before, definition);
            var currentStage = before.Stages.SingleOrDefault(x => x.DungeonId == plan.DungeonId)
                ?? throw new InvalidOperationException("Guardian clear stage is not unlocked for this participant.");
            if (currentStage.Progress > 2 || plan.Difficulty > currentStage.Progress + 1)
                throw new InvalidOperationException("Guardian ordinary clear cannot skip the current difficulty progression.");
            var header = before.Header;
            if ((long)header.FreeTickets + header.SecondaryTickets <= 0)
                throw new InvalidOperationException("Guardian successful clear requires one available ticket.");

            var firstClear = currentStage.Progress == -1;
            var stars = currentStage.Progress < plan.Difficulty ? plan.StarsOnImprovement : 0;
            if (stars > 0)
                _repository.UpdateClearedStage(connection, transaction, characterId, plan.RidableId,
                    plan.DungeonId, currentStage.Progress, plan.Difficulty);
            var purchases = new System.Collections.Generic.List<GuardianAltarPurchase>();
            var unlocked = false;
            if (firstClear)
            {
                foreach (var next in plan.NextDungeonIds)
                    unlocked |= _repository.TryUnlockStage(connection, transaction, characterId, plan.RidableId, next);
                foreach (var purchase in plan.UnlockPurchases)
                    if (_repository.TryUnlockPurchase(connection, transaction, characterId, plan.RidableId, purchase))
                        purchases.Add(purchase);
            }
            var afterHeader = header with
            {
                FreeTickets = header.FreeTickets > 0 ? header.FreeTickets - 1 : 0,
                SecondaryTickets = header.FreeTickets > 0 ? header.SecondaryTickets : header.SecondaryTickets - 1,
                StageStars = checked(header.StageStars + stars),
                AvailableStars = checked(header.AvailableStars + stars),
                MenuHintFlag = unlocked ? (byte)1 : header.MenuHintFlag,
            };
            _repository.UpdateClearHeader(connection, transaction, characterId, afterHeader);
            var after = _repository.Read(connection, transaction, characterId, plan.RidableId);
            ValidateComplete(after, definition);
            RequireCurrentGeneration(isCurrent);
            return new GuardianAltarClearProgressCommitResult
            {
                StarsGained = stars,
                FirstClear = firstClear,
                NewPurchases = purchases.AsReadOnly(),
                ProgressSnapshot = after,
            };
        }

        private static void ValidateComplete(GuardianAltarProgressSnapshot snapshot,
            GuardianAltarMenuDefinition definition)
        {
            definition.ValidateProgress(snapshot);
            if (snapshot.Header.RidableId != definition.RidableId
                || !snapshot.Stages.Any(x => x.DungeonId == definition.FirstDungeonId)
                || definition.InitialPurchases.Any(initial => !snapshot.Purchases.Any(x =>
                    x.ShopType == initial.ShopType && x.ResourceId == initial.ResourceId))
                || definition.InitialSlots.Any(initial => !snapshot.Slots.Any(x =>
                    x.SlotType == initial.SlotType && x.SlotIndex == initial.SlotIndex)))
                throw new InvalidOperationException("Guardian character progress is missing required definition records.");
        }
    }
}
