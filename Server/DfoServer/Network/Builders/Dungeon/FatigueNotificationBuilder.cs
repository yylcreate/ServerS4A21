using DfoServer.Game.Dungeon;
using System;

namespace DfoServer.Network.Builders
{
    internal static class FatigueNotificationBuilder
    {
        internal const int BodyLength = 8;

        internal static byte[] Build(CharacterFatigueSnapshot snapshot)
        {
            var used = (ushort)Math.Ceiling(snapshot.Used);
            var writer = new GamePacketWriter();
            writer.WriteUInt16(used);
            writer.WriteUInt16(CharacterFatigueService.MaxFatigue);
            writer.WriteUInt16(used);
            writer.WriteUInt16(0);
            return writer.ToArray();
        }
    }
}
