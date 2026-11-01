using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.Game.Dungeon.Bio
{
    internal static class BioResultPlanner
    {
        internal static bool TryCreate(
            BioDungeonRuntime runtime,
            byte outcome,
            IReadOnlyList<BioScoreInput> rows,
            DateTime utcNow,
            out BioResultPlan plan)
        {
            plan = null;
            if (runtime == null || utcNow.Kind != DateTimeKind.Utc || outcome > 1
                || rows == null || rows.Count != runtime.Members.Count
                || rows.Any(row => row == null)
                || rows.Select(row => row.Seat).Distinct().Count() != rows.Count
                || rows.Any(row => !runtime.Members.Any(member => member.Seat == row.Seat)))
                return false;

            var elapsed = (utcNow - runtime.PlayStartedUtc).TotalMilliseconds;
            if (elapsed < 5000d || utcNow > runtime.PlayDeadlineUtc.AddSeconds(15))
                return false;
            var maximumSurvival = Math.Min(runtime.Definition.PlayMilliseconds, elapsed + 1000d);
            if (rows.Any(row => row.SurvivalMilliseconds > maximumSurvival
                || row.Points > 1_000_000 || row.Metric4 > 1_000_000))
                return false;

            var ranked = rows.OrderByDescending(row => row.Points)
                .ThenByDescending(row => row.SurvivalMilliseconds)
                .ThenBy(row => row.Seat)
                .Select((row, index) => new BioRankedScore(checked((byte)(index + 1)), row))
                .ToArray();
            plan = new BioResultPlan(
                Guid.NewGuid(),
                utcNow,
                outcome,
                new ReadOnlyCollection<BioRankedScore>(ranked));
            return true;
        }
    }
}
