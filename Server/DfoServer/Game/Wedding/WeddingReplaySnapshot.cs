using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using System;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingReplaySnapshot(WeddingState Wedding, CharacterRecord Partner, WeddingRoomState Room)
    {
        internal static WeddingReplaySnapshot Load(IGameDatabase database, int characterId, long now)
            => database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var state = WeddingRepository.Load(connection, transaction, characterId);
                var peer = state.PartnerId > 0 ? WeddingRepository.Load(connection, transaction, state.PartnerId) : null;
                bool reciprocal = peer != null && WeddingRules.IsReciprocal(state, peer);
                var partner = reciprocal ? SqliteCharacterRepository.LoadById(connection, transaction, state.PartnerId) : null;
                if (partner?.Deleted == true) partner = null;
                var room = state.Status == WeddingStatus.Married && reciprocal
                    ? WeddingRoomRepository.Load(connection, transaction, state.PairId, now) : new WeddingRoomState();
                transaction.Commit();
                return new WeddingReplaySnapshot(state, partner, room);
            });

        // Called at the session send-lock linearization point. A committed
        // divorce, new marriage, appearance or room update invalidates the old
        // projection even when the character still owns exactly the same lease.
        internal bool MatchesCurrent(IGameDatabase database, int characterId, long now)
        {
            var current = Load(database, characterId, now);
            return Wedding == current.Wedding && SamePartner(Partner, current.Partner)
                && Room.PairId == current.Room.PairId && Room.Version == current.Room.Version
                && Room.TreeId == current.Room.TreeId && Room.TreeExpires == current.Room.TreeExpires
                && Room.TreeGrowth == current.Room.TreeGrowth && Room.WaterDay == current.Room.WaterDay
                && Room.WaterCount == current.Room.WaterCount
                && Room.Installed.SequenceEqual(current.Room.Installed) && Room.Cargo.SequenceEqual(current.Room.Cargo);
        }

        private static bool SamePartner(CharacterRecord before, CharacterRecord after)
        {
            if (before == null || after == null) return before == after;
            return before.CharacterId == after.CharacterId && before.Level == after.Level && before.Job == after.Job
                && before.GrowType == after.GrowType && before.Deleted == after.Deleted
                && (before.Name ?? Array.Empty<byte>()).SequenceEqual(after.Name ?? Array.Empty<byte>())
                && Enumerable.Range(0, 13).All(slot => Display(before, slot) == Display(after, slot));
        }

        private static int Display(CharacterRecord character, int slot)
            => Array.Find(character.Appearance ?? Array.Empty<CharacterAppearanceEntry>(), x => x.Slot == slot)?.DisplayItemId ?? 0;
    }
}
