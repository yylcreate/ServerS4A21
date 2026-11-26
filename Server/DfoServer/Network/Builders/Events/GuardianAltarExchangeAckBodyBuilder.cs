using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarExchangeAckBodyBuilder
    {
        internal static byte[] BuildRejected() => CommonPacketBodyBuilder.BuildCmdError(0x04);

        internal static byte[] Build(GuardianAltarExchangeRequest request,
            GuardianAltarSlot first, GuardianAltarSlot second)
        {
            var a = request.First;
            var b = request.Second;
            if (request.RidableId <= 0 || a.SlotType < 1 || a.SlotType > 2 || a.SlotType != b.SlotType
                || a.SlotIndex > 5 || b.SlotIndex > 5 || a.SlotIndex == b.SlotIndex
                || a.ResourceId < 0 || b.ResourceId < 0 || a.ResourceId == b.ResourceId
                || first == null || second == null || first.Locked != 0 || second.Locked != 0
                || first.SlotType != a.SlotType || second.SlotType != b.SlotType
                || first.SlotIndex != a.SlotIndex || second.SlotIndex != b.SlotIndex
                || first.ResourceId != b.ResourceId || second.ResourceId != a.ResourceId)
                throw new ArgumentException("Guardian exchange ACK must match both committed endpoints.");
            // 0x1105710 reads 26B after outer success. 0x146ACE0 swaps the
            // two supplied pre-state resources into their opposite UI slots.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(0);
            writer.WriteInt32(request.RidableId);
            writer.WriteInt32(a.SlotType);
            writer.WriteByte(a.SlotIndex);
            writer.WriteInt32(a.ResourceId);
            writer.WriteInt32(b.SlotType);
            writer.WriteByte(b.SlotIndex);
            writer.WriteInt32(b.ResourceId);
            return writer.ToArray();
        }
    }
}
