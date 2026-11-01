using System;

namespace DfoServer.Game.Dungeon.Bio
{
    internal readonly record struct BioQueueIdentity(
        Guid SessionId,
        int AccountId,
        int CharacterId,
        ushort UserId,
        int ListenerPort);
}
