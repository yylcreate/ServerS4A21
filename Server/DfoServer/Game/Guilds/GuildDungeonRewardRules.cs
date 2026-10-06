using System;
using System.Collections.Generic;
using System.Linq;
using PvfLib;

namespace DfoServer.Game.Guilds
{
    internal sealed record GuildDungeonRewardChoice(int GaugeRatio, int ItemId, int Count, int Contribute);

    internal static class GuildDungeonRewardRules
    {
        // A21 02A4A6E0 selects the first ascending resource key >= the
        // truncated, clamped purification percentage. It is an upper bound.
        internal static GuildDungeonRewardChoice Select(GuildDungeonEntry rule, int clearCount)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (clearCount < 0 || rule.GaugeLimit <= 0 || rule.GaugeOnClear < 0)
                throw new ArgumentOutOfRangeException(nameof(clearCount));
            return Select(Freeze(rule.Rewards), rule.GaugeLimit, rule.GaugeOnClear, clearCount);
        }

        internal static List<GuildDungeonRewardChoice> Freeze(IReadOnlyList<GuildDungeonGaugeReward> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var frozen = rows.Select(row => row?.Item == null ? null
                : new GuildDungeonRewardChoice(row.GaugeRatio, row.Item.ItemId, row.Item.Count, row.Contribute)).ToList();
            Validate(frozen);
            return frozen.OrderBy(row => row.GaugeRatio).ToList();
        }

        internal static void Validate(IReadOnlyList<GuildDungeonRewardChoice> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var keys = new HashSet<int>();
            if (rows.Count == 0)
                throw new InvalidOperationException("Guild reward table is empty.");
            foreach (var row in rows)
                if (row == null || row.GaugeRatio < 0 || row.ItemId <= 0
                    || row.Count <= 0 || row.Contribute < 0 || !keys.Add(row.GaugeRatio))
                    throw new InvalidOperationException("Guild reward definition is incomplete or duplicated.");
        }

        internal static GuildDungeonRewardChoice Select(
            IReadOnlyList<GuildDungeonRewardChoice> rows,
            int gaugeLimit, int gaugeOnClear, int clearCount)
        {
            Validate(rows);
            if (gaugeLimit <= 0 || gaugeOnClear < 0 || clearCount < 0)
                throw new ArgumentOutOfRangeException(nameof(clearCount));
            int percent = (int)Math.Min(100m, decimal.Truncate(
                (decimal)clearCount * gaugeOnClear * 100m / gaugeLimit));
            GuildDungeonRewardChoice selected = null;
            foreach (var row in rows)
            {
                if (row.GaugeRatio >= percent
                    && (selected == null || row.GaugeRatio < selected.GaugeRatio))
                    selected = row;
            }
            return selected ?? throw new InvalidOperationException("Guild reward table does not cover the current purification percentage.");
        }
    }
}
