using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Wedding
{
    internal readonly record struct WeddingRequest(uint Peer, byte Operation, byte Ceremony, byte Delay);
    internal readonly record struct WeddingResponse(WeddingRequest Request, byte Decision);
    internal readonly record struct WeddingRingRequest(int Slot, byte Ring);
    internal readonly record struct WeddingEnterRequest(uint PairId, uint Room, byte Reserved);

    internal static class WeddingCommandParser
    {
        // C8ADB2/C8EE58 copy 21 bytes as the BODY (14 opaque + 7 fields).
        // C89D90/C89DF0/C89E50 similarly copy 22 bytes for responses.
        internal static bool TryRequest(byte[] body, out WeddingRequest value)
        {
            value = default;
            if (!WeddingCommandPayload.TryFixed(body, 7, out var fields)) return false;
            value = ReadRequest(fields);
            return value.Operation is >= 1 and <= 9 && value.Ceremony <= 2 && value.Delay <= 2
                && (value.Operation == 2 || value.Ceremony == 0 && value.Delay == 0);
        }

        internal static bool TryResponse(byte[] body, out WeddingResponse value)
        {
            value = default;
            if (!WeddingCommandPayload.TryFixed(body, 8, out var fields)) return false;
            value = new(ReadRequest(fields), fields[7]);
            return value.Request.Operation is 1 or 2 or 7 && value.Request.Ceremony == 0
                && value.Request.Delay == 0 && value.Decision <= 2;
        }

        internal static bool TryPartner(byte[] body, out uint character)
        {
            character = 0;
            if (!WeddingCommandPayload.TryFixed(body, 4, out var fields)) return false;
            character = BinaryPrimitives.ReadUInt32LittleEndian(fields);
            return true;
        }

        internal static bool TryRing(byte[] body, out WeddingRingRequest value)
        {
            value = default;
            if (!WeddingCommandPayload.TryFixed(body, 5, out var fields)) return false;
            value = new(BinaryPrimitives.ReadInt32LittleEndian(fields), fields[4]);
            return value.Slot is >= 3 and < 352 && value.Ring is >= 1 and <= 5;
        }

        internal static bool TryLoveItem(byte[] body, out int slot)
        {
            slot = -1;
            if (!WeddingCommandPayload.TryFixed(body, 4, out var fields)) return false;
            slot = BinaryPrimitives.ReadInt32LittleEndian(fields);
            return slot is >= 3 and < 352;
        }

        internal static bool TryEnter(byte[] body, out WeddingEnterRequest value)
        {
            value = default;
            if (!WeddingCommandPayload.TryFixed(body, 9, out var fields)) return false;
            value = new(BinaryPrimitives.ReadUInt32LittleEndian(fields), BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]), fields[8]);
            return value.PairId is > 0 and < uint.MaxValue && value.Room <= byte.MaxValue && value.Reserved <= 1;
        }

        private static WeddingRequest ReadRequest(ReadOnlySpan<byte> body)
            => new(BinaryPrimitives.ReadUInt32LittleEndian(body), body[4], body[5], body[6]);
    }
}
