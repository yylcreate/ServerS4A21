using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.Bio
{
    internal sealed record BioResultPlan(
        Guid EventId,
        DateTime CompletedUtc,
        byte OutcomeRaw,
        IReadOnlyList<BioRankedScore> Rows);
}
