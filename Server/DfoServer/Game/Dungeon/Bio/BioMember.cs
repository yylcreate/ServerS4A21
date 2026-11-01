using System;

namespace DfoServer.Game.Dungeon.Bio
{
    internal sealed record BioMember(
        byte Seat,
        int CharacterId,
        ushort UserId,
        Guid SessionId,
        DungeonRun Run,
        DungeonRunIdentity RunIdentity);
}
