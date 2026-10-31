using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class VirtualCountItem
    {
        public short SlotIndex { get; set; }

        public int ItemId { get; set; }

        public int Count { get; set; }

        // Account crystals are character-owned projections of a shared balance.
        // Persistence submits only the change since the last committed observation.
        internal int CommittedCount { get; set; }

        internal CrystalCountPersistenceCheckpoint CrystalPersistence { get; set; }

        public VirtualCountItem Copy()
        {
            return new VirtualCountItem
            {
                SlotIndex = SlotIndex,
                ItemId = ItemId,
                Count = Count,
                CommittedCount = CommittedCount,
            };
        }
    }

    // This is an in-flight transaction checkpoint, never an account wallet.
    // Its audit receipt is committed in the same transaction as the balance.
    internal sealed class CrystalCountPersistenceCheckpoint
    {
        internal WeakReference<SqliteTransaction> Transaction { get; init; }
        internal string ConnectionString { get; init; }
        internal string Receipt { get; init; }
        internal long AuditId { get; init; }
        internal long AppliedDelta { get; set; }
        internal int CountAfter { get; set; }

        internal bool IsFor(SqliteTransaction transaction)
            => Transaction != null && Transaction.TryGetTarget(out var current)
                && ReferenceEquals(current, transaction);
    }
}
