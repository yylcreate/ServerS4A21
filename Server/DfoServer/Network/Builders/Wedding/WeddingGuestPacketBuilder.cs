using DfoServer.Game.Wedding;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    internal static class WeddingGuestPacketBuilder
    {
        // C94030 reads the pair only after result zero, then marks its cached letter paid.
        internal static byte[] MoneyGiftResult(int error, long pairId)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error);
            if (error == 0) w.WriteUInt32(checked((uint)pairId));
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.WEDDING_MONEY_GIFT, w.ToArray());
        }

        // C91EC0 consumes i32 result first and u16 remaining only for result zero.
        internal static byte[] SendLetterResult(int error, int remaining = 0)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error);
            if (error == 0) w.WriteUInt16(checked((ushort)remaining));
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER, w.ToArray());
        }

        // C911D0 consumes u8 count, then marks each u32 canonical character ID
        // in the two invitation lists (C838E0 compares entry+2Ch).
        internal static byte[] InvitedGuestIds(IReadOnlyCollection<int> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(ids));
            var w = new GamePacketWriter(); w.WriteByte((byte)ids.Count);
            foreach (int id in ids)
            {
                if (id <= 0) throw new ArgumentOutOfRangeException(nameof(ids));
                w.WriteUInt32((uint)id);
            }
            return WeddingPacketBuilder.Notify(NotiPacketTypeA21.WEDDING_INVITED_GUESTS, w.ToArray());
        }

        // C97230 reads exactly 78 bytes. StartAt must remain in the future for
        // native to retain the letter. The last byte maps to record+54h (paid).
        internal static byte[] LetterBody(WeddingState state, byte[] firstName, byte[] secondName, bool paid)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.PairId is <= 0 or >= uint.MaxValue || state.Room == 0 || state.StartAt <= 0
                || state.Ceremony > 2 || state.ChannelId is <= 0 or > byte.MaxValue)
                throw new ArgumentException("A21 wedding letter requires the captured ceremony identity.", nameof(state));
            var w = new GamePacketWriter();
            w.WriteUInt32((uint)state.PairId); w.WriteUInt32(state.Room);
            w.WriteUInt32(checked((uint)state.StartAt)); w.WriteUInt32((uint)state.ChannelId); w.WriteByte(state.Ceremony);
            WeddingPacketBuilder.WriteName(w, firstName); WeddingPacketBuilder.WriteName(w, secondName); w.WriteByte(paid ? (byte)1 : (byte)0);
            return w.ToArray();
        }
        internal static byte[] Letter(WeddingState state, byte[] firstName, byte[] secondName, bool paid)
            => WeddingPacketBuilder.Notify(NotiPacketTypeA21.WEDDING_LETTER, LetterBody(state, firstName, secondName, paid));
    }
}
