using DfoServer.Game.Wedding;
using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Wedding
{
    internal static class WeddingPartnerTravelCommandParser
    {
        // 15FDBE7 copies a 22-byte body: 14 opaque + pair u32 / slot i32.
        // 16EF580 resolves an item to its inventory index, not a channel number.
        internal static bool TryCheck(byte[] body, out uint pair, out int slot)
        {
            pair = 0; slot = 0; if (!WeddingCommandPayload.TryFixed(body, 8, out var fields)) return false;
            pair = BinaryPrimitives.ReadUInt32LittleEndian(fields);
            slot = BinaryPrimitives.ReadInt32LittleEndian(fields[4..]);
            return pair is > 0 and < uint.MaxValue && slot is >= 3 and < 352;
        }
        // C943E0 copies a 34-byte body (14 opaque + 20 fields);
        // positions are widened signed shorts.
        internal static bool TryMove(byte[] body, out WeddingPartnerTravelMove destination)
        {
            destination = null; if (!WeddingCommandPayload.TryFixed(body, 20, out var fields)) return false;
            int slot = BinaryPrimitives.ReadInt32LittleEndian(fields);
            int town = BinaryPrimitives.ReadInt32LittleEndian(fields[4..]), area = BinaryPrimitives.ReadInt32LittleEndian(fields[8..]);
            int x = BinaryPrimitives.ReadInt32LittleEndian(fields[12..]), y = BinaryPrimitives.ReadInt32LittleEndian(fields[16..]);
            if (slot is < 3 or >= 352 || town is < 0 or > byte.MaxValue || area is < 0 or > byte.MaxValue
                || x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue) return false;
            destination = new(slot, (byte)town, (byte)area, (short)x, (short)y); return true;
        }
    }
}
