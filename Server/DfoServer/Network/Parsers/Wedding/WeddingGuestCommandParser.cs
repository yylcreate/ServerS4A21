using System;
using System.Buffers.Binary;
using DfoServer.Infrastructure;
using System.Linq;

namespace DfoServer.Network.Parsers.Wedding
{
    internal sealed record WeddingGuestLetterRequest(byte Operation, int TargetCharacterId, ushort UserIdHint, byte ChannelHint);
    internal sealed record WeddingMoneyGiftRequest(long PairId, int Gold, byte[] Blessing);

    internal static class WeddingGuestCommandParser
    {
        // C8DBA0 configures 20; constraint 288B780 compares narrow GBK byte
        // lengths at 288B88F..89C and avoids splitting lead bytes at 288B953..96F.
        internal const int MaximumBlessingBytes = 20;
        // C85590 copies a 22-byte BODY: 14 opaque bytes, then operation/CID/UID/
        // channel hint. Operation one at C85602 clears all seven target bytes;
        // C856D0 maps it to SendWeddingCardWindow.xui's random-send button 5.
        internal static bool TrySendLetter(byte[] body, out WeddingGuestLetterRequest request)
        {
            request = null;
            if (!WeddingCommandPayload.TryFixed(body, 8, out var fields) || fields[0] > 1) return false;
            int target = BinaryPrimitives.ReadInt32LittleEndian(fields[1..]);
            ushort uid = BinaryPrimitives.ReadUInt16LittleEndian(fields[5..]);
            if (fields[0] == 1 ? target != 0 || uid != 0 || fields[7] != 0 : target <= 0) return false;
            request = new(fields[0], target, uid, fields[7]); return true;
        }

        // C89EB0: pair u32, gold u32, byte-length u32, GBK without a terminator.
        internal static bool TryMoneyGift(byte[] body, out WeddingMoneyGiftRequest request)
        {
            request = null;
            if (body == null || body.Length < 12) return false;
            uint pair = BinaryPrimitives.ReadUInt32LittleEndian(body), gold = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            if (pair is 0 or uint.MaxValue || gold > int.MaxValue || length > MaximumBlessingBytes || length != body.Length - 12) return false;
            var blessing = body.AsSpan(12).ToArray();
            if (blessing.Contains((byte)0) || !ClientTextEncoding.TryGetStringStrict(blessing, out _)) return false;
            request = new(pair, (int)gold, blessing); return true;
        }
    }
}
