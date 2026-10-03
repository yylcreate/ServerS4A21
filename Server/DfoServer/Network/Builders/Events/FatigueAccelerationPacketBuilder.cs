using System;
using DfoServer.Game.Events.FatigueAcceleration;

namespace DfoServer.Network.Builders.Events
{
    internal static class FatigueAccelerationPacketBuilder
    {
        internal static byte[] BuildAck(FatigueAccelerationEntryResult result)
        {
            var body = result == FatigueAccelerationEntryResult.Stopped
                ? CommonPacketBodyBuilder.BuildSuccessAck()
                : CommonPacketBodyBuilder.BuildCmdError(result switch
                {
                    FatigueAccelerationEntryResult.NotInTown => 19,
                    FatigueAccelerationEntryResult.InvalidRequest => 4,
                    FatigueAccelerationEntryResult.EntryUnavailable
                        or FatigueAccelerationEntryResult.DefinitionUnavailable
                        or FatigueAccelerationEntryResult.EffectsUnavailable => 4,
                    FatigueAccelerationEntryResult.EntryClosed => 179,
                    _ => throw new ArgumentOutOfRangeException(nameof(result)),
                });
            return GamePacketEnvelopeBuilder.Build(1,
                (ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE, body);
        }

        // The saved A21 consumer ignores the U16 on state0. Preserve the
        // validated inactive initialization; do not invent an active value.
        internal static byte[] BuildInactiveBody() => new byte[3];

        internal static byte[] BuildInactivePacket() => GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.USER_FATIGUE_ACCELERATION_STATE, BuildInactiveBody());
    }
}
