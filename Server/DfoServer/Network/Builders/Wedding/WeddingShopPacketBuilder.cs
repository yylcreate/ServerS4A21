using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    internal static class WeddingShopPacketBuilder
    {
        internal static byte[] Failure(int error)
        {
            byte code = error switch
            {
                -20 => BuyItemAckBuilder.ErrorCodeInventoryFull,
                -7 or -9 => BuyItemAckBuilder.ErrorCodeInsufficientGold,
                _ => BuyItemAckBuilder.ErrorCodeCannotBuy,
            };
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.BUY_ITEM, BuyItemAckBuilder.BuildError(code));
        }

        internal static byte[] Success(WeddingRoomResult result, int itemId, int count, bool furniture)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.BUY_ITEM, SuccessBody(result, itemId, count, furniture));

        internal static byte[] SuccessBody(WeddingRoomResult result, int itemId, int count, bool furniture)
        {
            if (result?.Success != true || result.ShopWallet == null || itemId <= 0 || count is < 1 or > short.MaxValue)
                throw new ArgumentException("A committed wedding purchase is required.");
            if (!furniture)
            {
                if (result.BuyMutation?.CoreSnapshot?.ItemId != itemId) throw new ArgumentException("The committed ring grant is missing.");
                return BuyItemAckBuilder.Build(result.BuyMutation,
                    new List<PurchaseCountUpdate> { new() { ItemTemplateId = itemId, RequestedCount = count } });
            }
            if (result.Room == null || result.PurchasedSlot is < 0 or >= 40
                || result.Room.Cargo[result.PurchasedSlot]?.ItemId != itemId || result.Room.Cargo[result.PurchasedSlot].Count < count)
                throw new ArgumentException("The committed decoration is missing from shared storage.");
            // A21 11357DE..1135983 reads four wallets and one 101-byte entry.
            // 1135B17 calls EE6D10(itemId), then 1648AC0: PVF couple-room type
            // maps to its own container. The ACK carries no inventory-list byte.
            // This temporary projection allocates neither a row nor an item UID.
            var core = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = itemId, Value = count };
            var w = new GamePacketWriter(); w.WriteByte(1); w.WriteInt32(result.ShopWallet.Gold);
            w.WriteInt32(result.ShopWallet.Sp); w.WriteInt32(0); w.WriteInt32(result.ShopWallet.Cera);
            ItemListProtocolWriter.WriteCommonEntry84(w, checked((short)result.PurchasedSlot), core, count);
            w.WriteByte(1); w.WriteInt32(itemId); w.WriteInt32(count);
            return w.ToArray();
        }
    }
}
