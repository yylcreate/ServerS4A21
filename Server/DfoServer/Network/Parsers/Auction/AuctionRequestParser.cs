using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.Auction
{
    internal readonly record struct AuctionRegisterRequest(InventoryListType ListType,
        short Slot, int ItemId, int Count, int StartingPrice, int BuyoutPrice, int DurationHours, byte WireSourceList);
    internal readonly record struct AuctionGoldRegisterRequest(int ItemId, int Count, int BuyoutPrice);
    internal readonly record struct AuctionBidRequest(int Amount, int ListingId);
    internal readonly record struct AuctionGoldBidRequest(int Amount, long ListingId, int GoldItemId);
    internal readonly record struct AuctionSearchRequest(
        byte Mode,
        ushort Category,
        IReadOnlyList<int> ItemIds,
        bool GoldMarket)
    {
        internal int ItemId => ItemIds.Count == 1 ? ItemIds[0] : 0;
    }

    internal static class AuctionRequestParser
    {
        // Source A21 constructor 0232F519 writes a 28B register body;
        // current A21 capture includes an additional nine opaque bytes. They
        // are not zero padding and do not authorize price, quantity or source.
        internal static bool Register(byte[] body, out AuctionRegisterRequest request)
            => Register(body, out request, out _);

        internal static bool Register(byte[] body, out AuctionRegisterRequest request, out string reason)
        {
            request = default;
            reason = null;
            if (body == null || (body.Length != 28 && body.Length != 37))
            { reason = "body-width"; return false; }
            if (body[0] != 0) { reason = "leading-byte"; return false; }
            byte wireList = body[1];
            var list = (InventoryListType)wireList;
            short slot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(2, 2));
            int id = I32(body, 4), count = I32(body, 8), bid = I32(body, 12);
            int buyout = I32(body, 16), echo = I32(body, 20), hours = I32(body, 24);
            if (list == InventoryListType.CrystalWarehouse)
            {
                if (!InventoryExchangeCommitService.IsCrystalSlot(slot)
                    || !InventoryService.TryResolveMainVirtualItemId(slot, out int fixedItemId) || fixedItemId != id)
                { reason = "crystal-source-identity"; return false; }
                list = InventoryListType.Main;
            }
            if (list is not (InventoryListType.Main or InventoryListType.PersonalCargo or InventoryListType.AccountCargo))
            { reason = "source-list"; return false; }
            if (slot < 0 || id <= 0) { reason = "source-identity"; return false; }
            if (count <= 0) { reason = "quantity"; return false; }
            if (bid < -1 || buyout < -1 || echo < -1 || buyout != echo || (bid <= 0 && buyout <= 0))
            { reason = "price-or-echo"; return false; }
            if (hours is not (6 or 12 or 24 or 48)) { reason = "duration"; return false; }
            request = new(list, slot, id, count, Math.Max(0, bid), Math.Max(0, buyout), hours, wireList);
            return true;
        }

        // The current gold-market capture has a 28B body with market byte 1.
        // The reference gold path consumes the template, count and total
        // buyout at offsets 4, 8 and 16. The reference gold path does not
        // consume offset 20; its A21 semantics remain unconfirmed. The other
        // fields do not authorize an asset source: gold always comes from the
        // current owned wallet.
        internal static bool GoldRegister(byte[] body, out AuctionGoldRegisterRequest request, out string reason)
        {
            request = default;
            reason = null;
            if (body == null || body.Length != 28 || body[0] != 1)
            { reason = "gold-body"; return false; }
            int id = I32(body, 4), count = I32(body, 8), buyout = I32(body, 16);
            if (id <= 0) { reason = "gold-identity"; return false; }
            if (count <= 0) { reason = "gold-quantity"; return false; }
            if (buyout <= 0) { reason = "gold-price"; return false; }
            request = new(id, count, buyout);
            return true;
        }

        // Source capture: leading byte + listing id + expiry auxiliary dword.
        internal static bool Cancel(byte[] body, out int listingId)
        {
            listingId = 0;
            if (body == null || body.Length != 9 || body[0] != 0) return false;
            listingId = I32(body, 1);
            return listingId > 0;
        }

        // Gold-market cancellation uses the same nine-byte shape captured from
        // the A21 client as the ordinary cancellation request: market byte (1),
        // u32 listing identity, then an opaque/expiry u32.  The listing id is
        // the first four bytes after the market byte; treating the trailing
        // expiry as part of a u64 id rejects every real cancellation request.
        internal static bool GoldCancel(byte[] body, out long listingId)
        {
            listingId = 0;
            if (body == null || body.Length != 9 || body[0] != 1) return false;
            uint wireId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1, 4));
            if (wireId == 0 || wireId > int.MaxValue) return false;
            listingId = wireId;
            return true;
        }

        // Source A21 capture: lead + amount + id + expiry + thirteen opaque bytes.
        internal static bool Bid(byte[] body, out AuctionBidRequest request)
        {
            request = default;
            if (body == null || body.Length != 26 || body[0] != 0) return false;
            int amount = I32(body, 1), id = I32(body, 5);
            if (amount <= 0 || id <= 0) return false;
            request = new(amount, id); return true;
        }

        // Gold purchase request: market byte + price + u32 listing id +
        // gold item id.  The reference parser accepts opaque bytes after the
        // confirmed 17-byte prefix; retain that compatibility without
        // interpreting the tail as authorization data.
        internal static bool GoldBid(byte[] body, out AuctionGoldBidRequest request)
        {
            request = default;
            if (body == null || body.Length < 17 || body[0] != 1) return false;
            int amount = I32(body, 1);
            uint wireListingId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
            int goldItemId = I32(body, 13);
            if (amount <= 0 || wireListingId == 0 || wireListingId > int.MaxValue || goldItemId <= 0)
                return false;
            request = new(amount, wireListingId, goldItemId);
            return true;
        }

        internal static bool AveragePrice(byte[] body, out int itemId)
            => AveragePrice(body, out itemId, out _);

        // The gold-market window uses the native six-byte request
        // (one opaque/flag byte + item id + one trailing byte).  Ordinary
        // auction price queries remain the confirmed eight-byte shape.
        internal static bool AveragePrice(byte[] body, out int itemId, out bool goldMarket)
        {
            itemId = 0;
            goldMarket = false;
            if (body == null) return false;
            if (body.Length == 6)
            {
                itemId = I32(body, 1);
                goldMarket = itemId > 0;
                return goldMarket;
            }
            if (body.Length != 8 || body[0] != 0 || !Zeros(body, 5, 8)) return false;
            itemId = I32(body, 1);
            return itemId > 0;
        }

        internal static bool List(byte[] body, out byte mode)
        {
            mode = 0;
            if (body == null || body.Length != 1 || body[0] > 1) return false;
            mode = body[0]; return true;
        }

        internal static bool Search(byte[] body, bool byItem, out AuctionSearchRequest request)
        {
            request = default;
            if (body == null || body.Length == 0) return false;
            if (byItem)
            {
                // Ordinary item searches are 18 + 4 * count.  The gold
                // market omits the ordinary category/filter prefix and uses
                // 10 + 4 * count, while retaining the same count byte and
                // candidate-id offset.
                if (body.Length < 10 || body[0] > 1) return false;
                int count = body[7];
                if (count == 0) return false;
                bool goldMarket = body[0] == 1 && body.Length == 10 + 4 * count;
                if (!goldMarket && body.Length != 18 + 4 * count) return false;
                var ids = new int[count];
                for (int i = 0; i < count; i++)
                {
                    ids[i] = I32(body, 10 + 4 * i);
                    if (ids[i] <= 0) return false;
                }
                request = new(body[0],
                    goldMarket ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8, 2)),
                    Array.AsReadOnly(ids), goldMarket);
            }
            else
            {
                // Reference identifies the gold filter by the 14B shape;
                // no current A21 sample establishes its first byte's meaning.
                if (body.Length == 14)
                {
                    request = new(1, 0, Array.Empty<int>(), true);
                    return true;
                }
                if (body.Length != 20 || body[0] > 1) return false;
                request = new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5, 2)),
                    Array.Empty<int>(), false);
            }
            // Remaining fields are opaque in the supplied implementation.
            // Nonzero UI filters are not malformed padding; do not guess their
            // sorting/page semantics or use them to authorize asset mutations.
            return true;
        }

        private static int I32(byte[] body, int offset)
            => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
        private static bool Zeros(byte[] bytes, int start, int end)
        { for (int i = start; i < end; i++) if (bytes[i] != 0) return false; return true; }
    }
}
