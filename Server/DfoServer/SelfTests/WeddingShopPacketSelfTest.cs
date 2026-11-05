using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.Network.Builders;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingShopPacketSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            try
            {
                var room = new WeddingRoomState { PairId = 1 }; room.Cargo[39] = new(123456, 7);
                var committed = new WeddingRoomResult(0, room, true, PurchasedSlot: 39, ShopWallet: new(11, 22, 33));
                var body = WeddingShopPacketBuilder.SuccessBody(committed, 123456, 2, true);
                // Native 11357DE has four dword reads, 113581B has read(101),
                // 11359AA reads one byte, then the count loop reads paired dwords.
                Check("A21 furniture BUY ACK uses four wallets, one 101-byte entry and one purchase-count row", body.Length == 127
                    && body.Take(17).SequenceEqual(Convert.FromHexString("010B000000160000000000000021000000")));
                Check("A21 furniture ACK carries the real shared slot without an inventory-list byte", BitConverter.ToUInt16(body, 17) == 39
                    && BitConverter.ToInt32(body, 19) == 123456 && body[118] == 1 && BitConverter.ToInt32(body, 119) == 123456
                    && BitConverter.ToInt32(body, 123) == 2);
                Check("native existing-slot ACK receives purchased quantity rather than the post-purchase stack", BitConverter.ToInt32(body, 23) == 2
                    && room.Cargo[39].Count == 7 && committed.BuyMutation == null);
                bool rejects = false;
                try { WeddingShopPacketBuilder.SuccessBody(committed, 123457, 2, true); } catch (ArgumentException) { rejects = true; }
                Check("furniture ACK cannot project a product different from committed storage", rejects);
                rejects = false;
                try { WeddingShopPacketBuilder.SuccessBody(committed, 123456, 1, false); } catch (ArgumentException) { rejects = true; }
                Check("ring ACK requires the real committed grant snapshot", rejects);
                var ringCore = new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = 98765, Count = 5, ExpireTime = 123 };
                var grant = new InventoryMutationResult { ListType = InventoryListType.Main, SlotIndex = 65, ItemTemplateId = ringCore.ItemId,
                    CoreSnapshot = ringCore, RequestedCount = 1, AppliedCount = 1, UpdatedGold = 11, UpdatedSp = 22, UpdatedCoin = 33 };
                var ring = WeddingShopPacketBuilder.SuccessBody(committed with { BuyMutation = grant }, ringCore.ItemId, 1, false);
                Check("ring ACK preserves the standard A21 item projection and purchase quantity", ring.Length == 127
                    && BitConverter.ToInt16(ring, 17) == 65 && BitConverter.ToInt32(ring, 19) == ringCore.ItemId && BitConverter.ToInt32(ring, 23) == 1);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_SHOP_PACKET: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }
    }
}
