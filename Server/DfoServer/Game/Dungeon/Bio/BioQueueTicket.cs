using System;

namespace DfoServer.Game.Dungeon.Bio
{
    internal sealed record BioQueueTicket(
        long Generation,
        BioQueueIdentity Identity,
        DateTime JoinedUtc);
}
