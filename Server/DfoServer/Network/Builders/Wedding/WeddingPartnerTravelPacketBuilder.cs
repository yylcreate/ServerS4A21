using DfoServer.Game.Wedding;

namespace DfoServer.Network.Builders
{
    internal static class WeddingPartnerTravelPacketBuilder
    {
        // C93830 always reads 22 bytes after upper success, even for a nonzero error.
        internal static byte[] Check(int error, WeddingPartnerTravelDestination destination = null)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error);
            w.WriteInt16((short)(destination?.Channel ?? 0));
            w.WriteUInt32(0); // Native reads the full block but does not use this reserved field.
            w.WriteInt32(destination?.Town ?? 0); w.WriteInt32(destination?.Area ?? 0);
            w.WriteInt16(destination?.X ?? 0); w.WriteInt16(destination?.Y ?? 0);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.CHECK_MOVE_TO_PARTNER, w.ToArray());
        }
    }
}
