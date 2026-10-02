using System;
using System.Globalization;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.GuardianAltar
{
    // Durable role progress is the only menu-state authority. Opening windows
    // and socket projections remain separate; this service never enters a run,
    // spends tickets, creates combat state or grants rewards.
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

        private static void RequireCurrentGeneration(Func<bool> isCurrent)
        {
            if (isCurrent != null && !isCurrent())
                throw new InvalidOperationException("Guardian progress request belongs to an obsolete generation.");
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
