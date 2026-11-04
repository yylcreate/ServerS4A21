using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingCeremonyCancellationResult(WeddingState State, int[] MailedCharacters);

    // Caller holds both canonical character gates. Refunds create system mail,
    // so neither a stale online bag nor an offline inventory is written here.
    internal sealed class WeddingCeremonyCancellationService
    {
        private readonly IGameDatabase _database;
        internal WeddingCeremonyCancellationService(IGameDatabase database) => _database = database;

        internal WeddingCeremonyCancellationResult Cancel(int characterId, long pair, long start, long now)
            => _database.Write((connection, transaction) =>
            {
                using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                var before = WeddingRepository.Load(connection, transaction, characterId);
                if (before.PairId != pair || before.StartAt != start || before.Status != WeddingStatus.Reserved || now < start)
                    return new WeddingCeremonyCancellationResult(before, Array.Empty<int>());
                if (!WeddingRules.IsReciprocal(before, WeddingRepository.Load(connection, transaction, before.PartnerId)))
                    throw new InvalidOperationException("Wedding membership is not reciprocal.");
                var notified = new WeddingMoneyGiftCommitService(WeddingCatalog.Get(), () => now)
                    .Refund(connection, transaction, _database, pair, start);
                int? payer = WeddingReservationLedger.Refund(connection, transaction, _database, pair, start);
                if (!WeddingRepository.Save(connection, transaction, before,
                    before with { Status = WeddingStatus.Engaged, Room = 0, StartAt = 0, ChannelId = 0 }))
                    throw new InvalidOperationException("Wedding cancellation changed concurrently.");
                return new WeddingCeremonyCancellationResult(WeddingRepository.Load(connection, transaction, characterId),
                    notified.Concat(payer.HasValue ? new[] { payer.Value } : Array.Empty<int>()).Distinct().ToArray());
            });
    }
}
