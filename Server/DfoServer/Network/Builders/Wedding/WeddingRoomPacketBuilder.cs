using DfoServer.Game.Wedding;
using System.Linq;

namespace DfoServer.Network.Builders
{
    internal static class WeddingRoomPacketBuilder
    {
        // A21 C820F0: u32 entries, entries of u16 position/u32 item, then 13-byte tree.
        internal static byte[] StateBody(WeddingRoomState room)
        {
            var w = new GamePacketWriter(); w.WriteUInt32((uint)room.Installed.Count(x => x != 0));
            for (int i = 0; i < room.Installed.Length; i++) if (room.Installed[i] != 0)
            { w.WriteUInt16((ushort)i); w.WriteUInt32((uint)room.Installed[i]); }
            w.WriteBytes(TreeBody(room)); return w.ToArray();
        }
        internal static byte[] State(WeddingRoomState room) => WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_ROOM, StateBody(room));

        // A21 C82240 clears 40 slots, reads a u32 count and 10-byte rows, followed by a u32 trailer.
        internal static byte[] CargoBody(WeddingRoomState room)
        {
            var w = new GamePacketWriter(); w.WriteUInt32((uint)room.Cargo.Count(x => x != null));
            for (int i = 0; i < room.Cargo.Length; i++) if (room.Cargo[i] is { } item)
            { w.WriteUInt16((ushort)i); w.WriteUInt32((uint)item.ItemId); w.WriteUInt32((uint)item.Count); }
            w.WriteUInt32(0); return w.ToArray();
        }
        internal static byte[] Cargo(WeddingRoomState room) => WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_INVEN, CargoBody(room));

        // A21 C82670 consumes 11-byte rows. Include empty installed rows to clear stale client objects.
        internal static byte[] RefreshBody(WeddingRoomState room)
        {
            var w = new GamePacketWriter(); w.WriteUInt32((uint)(room.Installed.Length + room.Cargo.Length));
            for (int i = 0; i < room.Installed.Length; i++)
                Row(w, 0, i, room.Installed[i], room.Installed[i] == 0 ? 0 : 1);
            for (int i = 0; i < room.Cargo.Length; i++)
                Row(w, 1, i, room.Cargo[i]?.ItemId ?? 0, room.Cargo[i]?.Count ?? 0);
            return w.ToArray();
        }
        internal static byte[] Refresh(WeddingRoomState room) => WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_ROOM_REFRESH, RefreshBody(room));
        internal static byte[] Update(WeddingRoomState room, WeddingRoomCommand move)
        {
            var w = new GamePacketWriter(); w.WriteByte(0);
            foreach (var endpoint in new[] { (move.From, move.FromSlot), (move.To, move.ToSlot) })
            {
                int id = endpoint.Item1 == 0 ? room.Installed[endpoint.Item2] : room.Cargo[endpoint.Item2]?.ItemId ?? 0;
                int count = endpoint.Item1 == 0 ? id == 0 ? 0 : 1 : room.Cargo[endpoint.Item2]?.Count ?? 0;
                Row(w, endpoint.Item1, endpoint.Item2, id, count);
            }
            return WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_ROOM_UPDATE, w.ToArray());
        }
        private static void Row(GamePacketWriter w, byte kind, int slot, int item, int count)
        { w.WriteByte(kind); w.WriteUInt32((uint)item); w.WriteUInt16((ushort)slot); w.WriteUInt32((uint)count); }

        // A21 C7E840 reads 13 bytes and sign-extends the final water-count byte.
        internal static byte[] TreeBody(WeddingRoomState room)
        {
            var w = new GamePacketWriter(); w.WriteUInt32((uint)room.TreeId); w.WriteUInt32(checked((uint)room.TreeExpires));
            w.WriteUInt32((uint)room.TreeGrowth); w.WriteByte(room.WaterCount); return w.ToArray();
        }
        internal static byte[] Tree(WeddingRoomState room) => WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_TREE, TreeBody(room));
        internal static byte[] Harvest(DfoServer.GameWorld.WeddingItemCost item)
        {
            var w = new GamePacketWriter(); w.WriteUInt16(1); w.WriteUInt32((uint)item.ItemId); w.WriteUInt32((uint)item.Count);
            return WeddingPacketBuilder.Notify(NotiPacketTypeA21.COUPLE_TREE_HARVEST_REWARD, w.ToArray());
        }
        internal static byte[] Ack(CmdPacketTypeA21 command, int error, byte? open = null)
        {
            // C7E580 is a return-only reset handler and consumes no result body.
            if (command == CmdPacketTypeA21.COUPLE_INVEN_DELETE_ITEM)
                return GamePacketEnvelopeBuilder.Build(1, (ushort)command, new[] { error == 0 ? (byte)1 : (byte)0 });
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error); if (open.HasValue) w.WriteByte(open.Value);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)command, w.ToArray());
        }
    }
}
