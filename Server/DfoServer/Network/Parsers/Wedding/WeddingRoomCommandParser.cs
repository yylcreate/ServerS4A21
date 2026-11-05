using DfoServer.Game.Wedding;
using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Wedding
{
    internal static class WeddingRoomCommandParser
    {
        // A21 C7E720 copies 15 body bytes: 14 opaque + the open flag.
        internal static bool TryOpen(byte[] body, out byte open)
        { open = WeddingCommandPayload.TryFixed(body, 1, out var fields) ? fields[0] : byte.MaxValue; return open <= 1; }

        // C7E5E0/C80E20 copy 28 body bytes: 14 opaque + two 7-byte endpoints.
        internal static bool TryMove(byte[] body, out WeddingRoomCommand command)
        {
            command = null; if (!WeddingCommandPayload.TryFixed(body, 14, out var data)) return false;
            int fromSlot = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), toSlot = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            int fromItem = BinaryPrimitives.ReadInt32LittleEndian(data[3..]), toItem = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
            if (data[0] > 1 || data[7] > 1 || fromSlot >= (data[0] == 0 ? 28 : 40) || toSlot >= (data[7] == 0 ? 28 : 40)
                || fromItem <= 0 || toItem < 0 || data[0] == data[7] && fromSlot == toSlot) return false;
            command = new(WeddingRoomOperation.Move, From: data[0], FromSlot: fromSlot, FromItem: fromItem,
                To: data[7], ToSlot: toSlot, ToItem: toItem); return true;
        }

        // A21 C7BFC0 copies 24 body bytes: 14 opaque + slot/ID/count.
        internal static bool TryDelete(byte[] body, out WeddingRoomCommand command)
        {
            command = null; if (!WeddingCommandPayload.TryFixed(body, 10, out var fields)) return false;
            int slot = BinaryPrimitives.ReadUInt16LittleEndian(fields), item = BinaryPrimitives.ReadInt32LittleEndian(fields[2..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(fields[6..]);
            if (slot >= 40 || item <= 0 || count is <= 0 or > ushort.MaxValue) return false;
            command = new(WeddingRoomOperation.Delete, Slot: slot, ItemId: item, Count: count); return true;
        }

        // A21 1449E40 copies 20 body bytes: 14 opaque + main slot/item.
        internal static bool TryWater(byte[] body, out WeddingRoomCommand command)
        {
            command = null; if (!WeddingCommandPayload.TryFixed(body, 6, out var fields)) return false;
            int slot = BinaryPrimitives.ReadUInt16LittleEndian(fields), item = BinaryPrimitives.ReadInt32LittleEndian(fields[2..]);
            if (slot is < 3 or >= 352 || item <= 0) return false;
            command = new(WeddingRoomOperation.Water, Slot: slot, ItemId: item); return true;
        }
        // A21 1448420 has the same packed slot/ID layout for the seed.
        internal static bool TryPlant(byte[] body, out WeddingRoomCommand command)
        {
            if (!TryWater(body, out command)) return false;
            command = command with { Operation = WeddingRoomOperation.Plant }; return true;
        }
        // A21 1448780 sends the header immediately, without a body.
        internal static bool TryHarvest(byte[] body) => body == null || body.Length == 0;
        // A21 1447AA0 also sends just the header for removal.
        internal static bool TryRemove(byte[] body) => body == null || body.Length == 0;
    }
}
