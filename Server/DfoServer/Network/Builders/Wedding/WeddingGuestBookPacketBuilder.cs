using DfoServer.Game.Wedding;
using System;

namespace DfoServer.Network.Builders
{
    internal static class WeddingGuestBookPacketBuilder
    {
        internal static byte[] Update(int error) => WeddingPacketBuilder.ItemResult(CmdPacketTypeA21.UPDATE_GUEST_COMMENT, error);
        internal static byte[] Delete(int error, uint comment)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(error); w.WriteUInt32(comment);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.DELETE_GUEST_COMMENT, w.ToArray());
        }
        // C95070: result, unused count hint, actual count; each fixed header is 42 bytes.
        internal static byte[] Load(WeddingGuestBookResult result)
        {
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(result.Error);
            if (result.Success)
            {
                int count = result.Entries?.Count ?? 0;
                w.WriteInt32(count); w.WriteInt32(count);
                if (result.Entries != null) foreach (var entry in result.Entries)
                {
                    if (!WeddingGuestBookRepository.ValidText(entry.Text)) throw new ArgumentException("Invalid stored A21 guest comment.");
                    w.WriteUInt32(entry.Id); WeddingPacketBuilder.WriteName(w, entry.AuthorName);
                    w.WriteUInt32(checked((uint)entry.CreatedAt)); w.WriteUInt32((uint)entry.Text.Length); w.WriteBytes(entry.Text);
                }
            }
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT, w.ToArray());
        }
    }
}
