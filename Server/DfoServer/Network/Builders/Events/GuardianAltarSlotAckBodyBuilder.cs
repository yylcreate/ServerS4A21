using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarSlotAckBodyBuilder
    {
        internal static byte[] BuildRejected() => CommonPacketBodyBuilder.BuildCmdError(0x04);

        internal static byte[] Build(GuardianAltarSlotRequest request, GuardianAltarSlot committed)
        {
            if (request.RidableId <= 0 || request.SlotType < 1 || request.SlotType > 2
                || request.SlotIndex > 5 || request.ResourceId <= 0 || committed == null
                || committed.Locked != 0 || committed.SlotType != request.SlotType
                || committed.SlotIndex != request.SlotIndex
                || committed.ResourceId != (request.Equip ? request.ResourceId : 0))
                throw new ArgumentException("Guardian slot ACK must match its committed configuration.");
            // A21 sub_11054B0 consumes 18B only after outer success.
            // sub_146A850 treats byte+8 as the action, fixes Locked=0, and
            // writes resource=0 on unequip. Echo the original resource key.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(0);
            writer.WriteInt32(request.RidableId);
            writer.WriteByte(request.Equip ? (byte)1 : (byte)0);
            writer.WriteInt32(request.SlotType);
            writer.WriteByte(request.SlotIndex);
            writer.WriteInt32(request.ResourceId);
            return writer.ToArray();
        }
    }
}
