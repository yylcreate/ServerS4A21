using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    // Source A21 auction implementation: callbacks 0112CBB0, 0111FC90,
    // 0111FE70 and the 0232DDC4/0232E073 row remappers. These are auction
    // wire projections, not ItemCore persistence or generic inventory layouts.
    internal static class AuctionPacketBuilder
    {
        internal const int SearchRecordSize = 168;
        internal const int SellerRecordSize = 151;
        internal const int BidRecordSize = 159;
        internal const int WireItemSize = 83;
        internal const int SellerCapacity = byte.MaxValue;
        internal const int BidCapacity = 1000;

        // Source client callbacks confirm 7E failure termination for these
        // commands. D2 opens safety mode; BE has no verified consumer.
        internal static byte[] Failure(CmdPacketTypeA21 command)
        {
            if (command is not (CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE
                or CmdPacketTypeA21.AUCTION_REGIST_ITEM
                or CmdPacketTypeA21.AUCTION_REGIST_CANCEL
                or CmdPacketTypeA21.AUCTION_BIDDING
                or CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY
                or CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO
                or CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO
                or CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE))
                throw new ArgumentOutOfRangeException(nameof(command));
            return Command(command, new byte[] { 0, 0x7E });
        }

        internal static byte[] Success(CmdPacketTypeA21 command, byte mode)
        {
            ValidateMode(mode);
            return Command(command, new byte[] { 1, mode });
        }

        // The gold market has dedicated native failure bodies.  Keep the
        // ordinary 0x7E safety/failure projection above unchanged.
        internal static byte[] GoldFailure(CmdPacketTypeA21 command)
        {
            return command switch
            {
                CmdPacketTypeA21.AUCTION_REGIST_ITEM => Command(command, new byte[] { 0, 0x98, 0 }),
                CmdPacketTypeA21.AUCTION_REGIST_CANCEL => Command(command, new byte[] { 0, 0x98, 1 }),
                CmdPacketTypeA21.AUCTION_BIDDING => Command(command, new byte[] { 0, 1 }),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        }

        internal static byte[] AveragePrice(byte mode, int referencePrice, int price, int count)
        {
            ValidateMode(mode);
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(mode);
            writer.WriteInt32(referencePrice);
            writer.WriteInt32(0);
            for (int i = 0; i < 6; i++) writer.WriteInt32(price);
            for (int i = 0; i < 6; i++) writer.WriteInt32(count);
            return Command(CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE, writer.ToArray());
        }

        internal static byte[] Search(CmdPacketTypeA21 command, byte mode,
            IReadOnlyList<AuctionListing> listings, long nowUnix)
        {
            if (command is not (CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY
                or CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY))
                throw new ArgumentOutOfRangeException(nameof(command));
            ValidateMode(mode);
            ArgumentNullException.ThrowIfNull(listings);
            int count = Math.Min(listings.Count, ushort.MaxValue);
            var writer = new GamePacketWriter();
            writer.WriteByte(1); writer.WriteByte(mode); writer.WriteInt32(0);
            writer.WriteUInt16(checked((ushort)count));
            for (int i = 0; i < count; i++)
            {
                var listing = listings[i];
                var row = Row(listing, SearchRecordSize);
                // Reference gold rows use -1 for the unsupported bid price;
                // zero renders a misleading "current bid 0 points" control.
                Put(row, 9, AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId) ? -1
                    : listing.CurrentBid > 0 ? listing.CurrentBid : checked(listing.StartingPrice * listing.Item.ItemCount));
                Put(row, 13, BuyoutTotal(listing));
                row[42] = RemainingHours(listing, nowUnix);
                CopyItem(row, 43, listing);
                Put(row, 156, RemainingSeconds(listing, nowUnix));
                writer.WriteBytes(row);
            }
            // The source client's active callback table has BA, not BB.
            // Classification requests therefore return through the BA renderer.
            return Command(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, writer.ToArray());
        }

        internal static byte[] Seller(byte mode, IReadOnlyList<AuctionListing> listings, long nowUnix)
        {
            ValidateMode(mode);
            ArgumentNullException.ThrowIfNull(listings);
            int count = Math.Min(listings.Count, SellerCapacity);
            var writer = new GamePacketWriter();
            writer.WriteByte(1); writer.WriteByte(mode); writer.WriteByte(checked((byte)count));
            for (int i = 0; i < count; i++)
            {
                var listing = listings[i];
                var row = Row(listing, SellerRecordSize);
                Put(row, 8, AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId) ? -1 : listing.BuyoutPrice);
                Put(row, 12, BuyoutTotal(listing));
                row[29] = RemainingHours(listing, nowUnix);
                CopyItem(row, 30, listing);
                Put(row, 143, RemainingSeconds(listing, nowUnix));
                writer.WriteBytes(row);
            }
            return Command(CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO, writer.ToArray());
        }

        internal static byte[] Bids(byte mode,
            IReadOnlyList<(AuctionListing Listing, AuctionBidRecord Bid)> entries, long nowUnix)
        {
            ValidateMode(mode);
            ArgumentNullException.ThrowIfNull(entries);
            int count = Math.Min(entries.Count, BidCapacity);
            var writer = new GamePacketWriter();
            writer.WriteByte(1); writer.WriteByte(mode); writer.WriteUInt16(checked((ushort)count));
            for (int i = 0; i < count; i++)
            {
                var (listing, bid) = entries[i];
                if (bid == null || bid.ListingId != listing?.ListingId || bid.Status is < 0 or > 1)
                    throw new InvalidOperationException("Auction bid projection has no valid active source.");
                var row = Row(listing, BidRecordSize);
                Put(row, 8, bid.BidAmount); Put(row, 12, BuyoutTotal(listing));
                row[37] = RemainingHours(listing, nowUnix);
                CopyItem(row, 38, listing);
                Put(row, 151, RemainingSeconds(listing, nowUnix));
                writer.WriteBytes(row);
            }
            return Command(CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO, writer.ToArray());
        }

        private static byte[] Row(AuctionListing listing, int length)
        {
            if (listing == null || listing.ListingId is <= 0 or > int.MaxValue
                || listing.ExpiresAtUnix is < 0 or > int.MaxValue)
                throw new InvalidOperationException("Auction row identity is outside the confirmed A21 wire range.");
            var row = new byte[length];
            Put(row, 0, checked((int)listing.ListingId));
            Put(row, 4, checked((int)listing.ExpiresAtUnix));
            return row;
        }

        private static int BuyoutTotal(AuctionListing listing)
        {
            if (listing.Item == null || listing.Item.ItemCount <= 0 || listing.BuyoutPrice < 0)
                throw new InvalidOperationException("Auction row has an invalid quantity or price.");
            if (listing.BuyoutPrice == 0) return -1; // Client sentinel: pure auction, no buyout.
            // Gold-market rows store the full point price once per listing.
            // ItemCount is the number of face-value gold coupons and must not
            // multiply the buyer's point charge.
            if (AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId))
                return listing.BuyoutPrice;
            return checked(listing.BuyoutPrice * listing.Item.ItemCount);
        }

        private static int RemainingSeconds(AuctionListing listing, long nowUnix)
            => checked((int)Math.Clamp(listing.ExpiresAtUnix - nowUnix, 0L, int.MaxValue));

        // A21 02327610 formats unified row42 as dstr60159 "%d小时";
        // 0232DF0E/0232E1BD remap seller29/bid37 to that byte. Official
        // immediate list replies for 24-hour registrations display23, so
        // this is remaining whole hours, separate from the tail seconds.
        private static byte RemainingHours(AuctionListing listing, long nowUnix)
            => checked((byte)(RemainingSeconds(listing, nowUnix) / 3600));

        private static void CopyItem(byte[] row, int offset, AuctionListing listing)
        {
            var source = listing.Item?.ItemCoreData;
            if (source == null || source.Length != ItemCore.Size
                || source[ItemCore.ItemKindOffset] == ItemCore.KindUnknown
                || BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(ItemCore.ItemIdOffset, 4))
                    != listing.Item.ItemTemplateId)
                throw new InvalidOperationException("Auction projection requires the committed full ItemCore snapshot.");
            var item = source.AsSpan(0, WireItemSize).ToArray();
            int value = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(ItemCore.ValueOffset, 4));
            item[5] = source[ItemCore.AttrOffset];
            Put(item, 6, value);
            // Auction-only expiry projection. Never mutate the escrow snapshot.
            item[ItemCore.DurabilityOffset] = item[ItemCore.DurabilityOffset + 1] = 0;
            Put(item, ItemCore.RuneOffset, checked((int)listing.ExpiresAtUnix));
            item.CopyTo(row, offset);
        }

        private static void Put(byte[] bytes, int offset, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        private static void ValidateMode(byte mode)
        { if (mode > 1) throw new ArgumentOutOfRangeException(nameof(mode)); }
        private static byte[] Command(CmdPacketTypeA21 command, byte[] body)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)command, body);
    }
}
