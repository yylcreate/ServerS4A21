using DfoServer.Game.Wedding;
using System;

namespace DfoServer.Network.Builders
{
    internal static class WeddingCapsulePacketBuilder
    {
        // A21 C92580 writes progress/+110h, own/+118h, pending/+114h, then the spouse hint.
        internal static byte[] StateBody(WeddingCapsuleProjection projection)
        {
            ArgumentNullException.ThrowIfNull(projection);
            var w = new GamePacketWriter();
            w.WriteUInt32(checked((uint)projection.State.Progress));
            w.WriteByte(projection.State.Own);
            w.WriteByte(projection.State.Taken);
            w.WriteByte(projection.SpouseHasPending ? (byte)1 : (byte)0);
            return w.ToArray();
        }

        internal static byte[] State(WeddingCapsuleProjection projection)
            => WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_GROWTH_CAPSULE, StateBody(projection));

        // A21 C92640 consumes exactly one u32, independently of the full stock notification.
        internal static byte[] Progress(int progress)
        {
            var w = new GamePacketWriter(); w.WriteUInt32(checked((uint)progress));
            return WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_GROWTH_CAPSULE_GAGE, w.ToArray());
        }

        internal static byte[] Result(CmdPacketTypeA21 command, WeddingCapsuleAction action, int error, WeddingCapsuleState state = null)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error);
            if (error == 0)
            {
                ArgumentNullException.ThrowIfNull(state);
                switch (action)
                {
                    // C926C9 and C926D4 consume u32 progress and u8 own after result zero.
                    case WeddingCapsuleAction.Make: w.WriteUInt32(checked((uint)state.Progress)); w.WriteByte(state.Own); break;
                    // C927D9 and C928A7 respectively consume u8 own and u8 pending.
                    case WeddingCapsuleAction.Give: w.WriteByte(state.Own); break;
                    case WeddingCapsuleAction.Take: w.WriteByte(state.Taken); break;
                    default: throw new ArgumentOutOfRangeException(nameof(action));
                }
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)command, w.ToArray());
        }
    }
}
