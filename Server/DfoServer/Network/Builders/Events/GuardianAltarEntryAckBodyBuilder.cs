using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarEntryAckBodyBuilder
    {
        // This is the existing common failure wrapper, not a Guardian-specific
        // localization code. The client does not read menu arrays on this path.
        private const byte CommonFailure = 0x04;

        internal static byte[] BuildRejection(GuardianAltarEntryRejection reason)
        {
            var result = reason switch
            {
                GuardianAltarEntryRejection.Closed => 1,
                GuardianAltarEntryRejection.Trading => 2,
                GuardianAltarEntryRejection.MercenaryAssigned => 3,
                GuardianAltarEntryRejection.LevelTooLow => 4,
                GuardianAltarEntryRejection.InvalidRequest
                    or GuardianAltarEntryRejection.InvalidPresence
                    or GuardianAltarEntryRejection.ProgressAuthorityUnavailable
                    or GuardianAltarEntryRejection.StateReadFailed => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(reason)),
            };
            if (result == 0)
                return CommonPacketBodyBuilder.BuildCmdError(CommonFailure);

            // sub_1108400: transport-success byte, then nonzero Int32 domain
            // result. This terminates before reading the successful menu data.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result);
            return writer.ToArray();
        }
    }
}
