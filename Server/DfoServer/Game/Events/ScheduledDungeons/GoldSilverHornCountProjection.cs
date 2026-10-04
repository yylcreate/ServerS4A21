using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using PvfLib;

namespace DfoServer.Game.Events.ScheduledDungeons
{
    // One validation shared by admission and the UI. The durable counter
    // remains owned by DungeonEntryLimitService; this only reads its snapshot.
    internal static class GoldSilverHornCountProjection
    {
        internal static bool TryCreate(GoldSilverHornDungeonConfigFile config,
            IReadOnlyList<SpecialDungeonEntryLimitSnapshot> limits,
            out ushort goldUsed, out ushort silverUsed, out string reason)
        {
            goldUsed = silverUsed = 0;
            reason = string.Empty;
            if (config?.IsValid != true || limits == null)
            {
                reason = "gold_silver_count_definition_unavailable";
                return false;
            }
            if (!TryRead(config.Gold, limits, out var gold, out reason)
                || !TryRead(config.Silver, limits, out var silver, out reason))
                return false;
            goldUsed = gold;
            silverUsed = silver;
            return true;
        }

        private static bool TryRead(GoldSilverHornDungeonEntry entry,
            IReadOnlyList<SpecialDungeonEntryLimitSnapshot> limits,
            out ushort used, out string reason)
        {
            used = 0;
            reason = string.Empty;
            if (entry == null)
                return true;
            SpecialDungeonEntryLimitSnapshot matched = null;
            foreach (var limit in limits)
            {
                if (limit?.DungeonId != entry.DungeonId)
                    continue;
                if (matched != null)
                {
                    reason = "gold_silver_count_definition_duplicate";
                    return false;
                }
                matched = limit;
            }
            // The current client only knows configured max minus used. Extra
            // allowances or a different scope cannot be represented by this UI.
            if (matched == null || matched.ScopeType != DungeonLimitScopeType.Character
                || matched.LimitCount != entry.MaxEnterCount
                || matched.ExtraCount != 0 || matched.UsedCount < 0
                || matched.UsedCount > entry.MaxEnterCount
                || matched.UsedCount > ushort.MaxValue
                || matched.CurrentCount != entry.MaxEnterCount - matched.UsedCount)
            {
                reason = "gold_silver_count_definition_mismatch";
                return false;
            }
            used = checked((ushort)matched.UsedCount);
            return true;
        }
    }
}
