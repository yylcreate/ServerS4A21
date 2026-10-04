using System;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarAchievementAckBodyBuilder
    {
        internal static byte[] BuildSuccess(int achievementType)
        {
            if (achievementType <= 0)
                throw new ArgumentOutOfRangeException(nameof(achievementType));
            // A21 1105880 consumes result and type as two Int32 after outer
            // success. The current committed type updates the client's claim row.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(0);
            writer.WriteInt32(achievementType);
            return writer.ToArray();
        }

        internal static byte[] BuildRejected() => CommonPacketBodyBuilder.BuildCmdError(0x04);
    }
}
