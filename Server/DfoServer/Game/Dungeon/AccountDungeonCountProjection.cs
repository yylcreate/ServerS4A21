using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal readonly record struct AccountDungeonCountProjection(
        uint DungeonId, short MaximumCount, short UsedCount)
    {
        internal int RemainingCount => MaximumCount - UsedCount;

        internal static bool TryCreate(int dungeonId, int maximumCount,
            IReadOnlyList<SpecialDungeonEntryLimitSnapshot> limits,
            out AccountDungeonCountProjection projection, out string reason)
        {
            projection = default;
            reason = "account_count_unavailable";
            if (dungeonId <= 0 || maximumCount <= 0 || maximumCount > byte.MaxValue || limits == null)
                return false;
            SpecialDungeonEntryLimitSnapshot found = null;
            foreach (var row in limits)
            {
                if (row?.DungeonId != dungeonId)
                    continue;
                if (found != null)
                {
                    reason = "account_count_duplicate";
                    return false;
                }
                found = row;
            }
            if (found == null || found.ScopeType != DungeonLimitScopeType.Account
                || found.LimitCount != maximumCount || found.ExtraCount != 0
                || found.UsedCount < 0 || found.UsedCount > maximumCount
                || found.CurrentCount + found.UsedCount != maximumCount)
                return false;
            projection = new((uint)dungeonId, (short)maximumCount, (short)found.UsedCount);
            reason = string.Empty;
            return true;
        }
    }
}
