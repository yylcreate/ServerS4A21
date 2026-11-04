using DfoServer.GameWorld;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Wedding
{
    // Offline ceremony completion does not write an online inventory. Gifts are
    // delivered through the existing system-mail transaction and permanent key.
    internal sealed class WeddingCeremonyRecoveryService
    {
        private readonly IGameDatabase _database;
        internal WeddingCeremonyRecoveryService(IGameDatabase database)
        { _database = database ?? throw new ArgumentNullException(nameof(database)); }

        internal WeddingState Advance(int characterId, long expectedPair, long expectedStart, long now)
        {
            var catalog = WeddingCatalog.Get();
            var pending = _database.Read(c => WeddingRepository.Load(c, null, characterId));
            // A persisted Ceremony proves that the two hosts already attended.
            // An offline Reserved row cannot establish attendance after a restart.
            if (pending.PairId == expectedPair && pending.StartAt == expectedStart
                && pending.Status == WeddingStatus.Reserved && now >= pending.StartAt)
                return new WeddingCeremonyCancellationService(_database)
                    .Cancel(characterId, expectedPair, expectedStart, now).State;
            return _database.Write((connection, transaction) =>
            {
                using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                var state = WeddingRepository.Load(connection, transaction, characterId);
                if (state.PairId != expectedPair || state.StartAt != expectedStart
                    || state.Status is not (WeddingStatus.Reserved or WeddingStatus.Ceremony)) return state;
                var peer = WeddingRepository.Load(connection, transaction, state.PartnerId);
                if (!WeddingRules.IsReciprocal(state, peer)) throw new InvalidOperationException("Wedding membership is not reciprocal.");
                if (state.Status != WeddingStatus.Ceremony || now < checked(state.StartAt + catalog.CeremonyEndSeconds)) return state;
                var gift = catalog.Ceremonies[state.Ceremony].Gift;
                foreach (int cid in new[] { state.CharacterId, state.PartnerId })
                    WeddingCeremonyGiftMail.Send(_database, connection, transaction, state.PairId, cid, gift);
                var married = state with { Status = WeddingStatus.Married, Ring = catalog.Ceremonies[state.Ceremony].Ring,
                    MarriedAt = now, StartAt = 0, Room = 0 };
                if (!WeddingRepository.Save(connection, transaction, state, married))
                    throw new InvalidOperationException("Wedding completion changed concurrently.");
                return WeddingRepository.Load(connection, transaction, characterId);
            });
        }
    }
}
