using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.Bio
{
    internal sealed record BioMatchingCohort(
        long Generation,
        IReadOnlyList<BioQueueTicket> Participants);
}
