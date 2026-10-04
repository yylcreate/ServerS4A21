using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarOpeningBodyBuilder
    {
        internal static byte[] Build(GuardianAltarOpeningSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var writer = new GamePacketWriter();
            writer.WriteInt32(GuardianAltarOpeningSnapshot.ScheduleCategory);
            writer.WriteInt32(snapshot.WireState);
            writer.WriteInt32(GuardianAltarOpeningSnapshot.ScheduleContentId);
            writer.WriteInt32(snapshot.RemainingSeconds);
            return writer.ToArray();
        }
    }
}
