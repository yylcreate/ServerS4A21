using System;
using DfoServer.Game.Events.FatigueAcceleration;

namespace DfoServer.Network.Builders.Events
{
    internal static class FatigueAccelerationPacketBuilder
    {
        internal static byte[] BuildAck(FatigueAccelerationEntryResult result)
        {
            var body = result is FatigueAccelerationEntryResult.Stopped
                or FatigueAccelerationEntryResult.Started
                ? CommonPacketBodyBuilder.BuildSuccessAck()
                : CommonPacketBodyBuilder.BuildCmdError(result switch
                {
                    FatigueAccelerationEntryResult.NotInTown => 19,
                    FatigueAccelerationEntryResult.InvalidRequest => 4,
                    FatigueAccelerationEntryResult.EntryUnavailable
                        or FatigueAccelerationEntryResult.DefinitionUnavailable => 4,
                    FatigueAccelerationEntryResult.EntryClosed => 179,
                    _ => throw new ArgumentOutOfRangeException(nameof(result)),
                });
            return GamePacketEnvelopeBuilder.Build(1,
                (ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE, body);
        }

        // A21 sub_11D83E0 stores state separately from U16; sub_1283AD0
        // checks the box on state1. The U16 balance/threshold is unproven,
        // so preserve zero rather than invent a fatigue balance.
        internal static byte[] BuildStateBody(bool enabled)
            => new byte[] { 0, 0, enabled ? (byte)1 : (byte)0 };
        internal static byte[] BuildInactiveBody() => BuildStateBody(false);

        internal static byte[] BuildStatePacket(bool enabled) => GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.USER_FATIGUE_ACCELERATION_STATE, BuildStateBody(enabled));

        internal static byte[] BuildInactivePacket() => BuildStatePacket(false);
    }
}
