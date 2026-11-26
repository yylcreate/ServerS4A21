using System;

namespace DfoServer.Game.Dungeon.Bio
{
    internal readonly record struct BioCaller(
        Guid SessionId,
        int CharacterId,
        ushort UserId,
        DungeonRun Run);
}
