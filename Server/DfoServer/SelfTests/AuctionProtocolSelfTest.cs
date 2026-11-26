using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class AuctionProtocolSelfTest
    {
        internal static int Run()
        {
            int checks = 0, failures = 0;
            void Check(string label, bool ok)
            { checks++; if (!ok) failures++; Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}"); }
            try
            {
                CheckParsers(Check); CheckBuilders(Check); CheckPvfClassification(Check);
                CheckRuntime(Check).GetAwaiter().GetResult();
                CheckGoldPurchaseRejections(Check).GetAwaiter().GetResult();
                CheckTokenCeraMailClaim(Check).GetAwaiter().GetResult();
                CheckTokenCeraProjectionFence(Check).GetAwaiter().GetResult();
                CheckRegistrationRejections(Check).GetAwaiter().GetResult();
                CheckSearchProjection(Check).GetAwaiter().GetResult();
                CheckCrystalRegistration(Check).GetAwaiter().GetResult();
                CheckMailboxFences(Check).GetAwaiter().GetResult();
            }
            catch (Exception exception) { Check("auction protocol unexpected exception: " + exception, false); }
            Console.WriteLine($"AUCTION_PROTOCOL checks={checks} failures={failures}");
            return failures;
        }

        private static void CheckParsers(Action<string, bool> check)
        {
            var register = RegisterBody(3027, 3);
            check("register parses confirmed28-byte body and exact source slot/template/count",
                AuctionRequestParser.Register(register, out var request)
                && request.ListType == InventoryListType.Main && request.Slot == 30 && request.ItemId == 3027
                && request.Count == 3 && request.BuyoutPrice == 100 && request.StartingPrice == 0 && request.DurationHours == 24);
            var padded = new byte[37]; register.CopyTo(padded, 0);
            check("register accepts the complete37-byte form", AuctionRequestParser.Register(padded, out _));
            padded[36] = 1;
            check("the exact37-byte suffix is opaque, but unknown body widths remain rejected",
                AuctionRequestParser.Register(padded, out _) && !AuctionRequestParser.Register(register[..27], out _)
                && !AuctionRequestParser.Register(new byte[38], out _));
            var realBody = CurrentEquipmentRegisterBody();
            check("currentA21 captured register replay preserves all business fields despite its nonzero9-byte suffix",
                AuctionRequestParser.Register(realBody, out var real) && real.ListType == InventoryListType.Main
                && real.Slot == 43 && real.ItemId == 100170173 && real.Count == 1 && real.StartingPrice == 0
                && real.BuyoutPrice == 100000 && real.DurationHours == 24);
            foreach (var change in new Action<byte[]>[] { b => Put(b, 20, 101), b => b[1] = (byte)InventoryListType.Equipment,
                b => Put(b, 8, 0), b => Put(b, 24, 7), b => b[0] = 1 })
            { var bad = register.ToArray(); change(bad); check("invalid register never forms a mutation request", !AuctionRequestParser.Register(bad, out _)); }
            foreach (var list in new[] { InventoryListType.PersonalCargo, InventoryListType.AccountCargo })
            { var b = register.ToArray(); b[1] = (byte)list; check("confirmed cargo source remains explicit: " + list, AuctionRequestParser.Register(b, out var r) && r.ListType == list); }
            var capturedGoldRegister = Convert.FromHexString(
                "010000007DEB280001000000FFFFFFFFFFFFFFFF0000000018000000");
            var pricedGoldRegister = capturedGoldRegister.ToArray(); Put(pricedGoldRegister, 16, 500);
            check("gold registration consumes its own buyout field without ordinary price echo",
                AuctionRequestParser.GoldRegister(pricedGoldRegister, out var goldRegister, out _)
                && goldRegister.ItemId == AuctionRepository.GoldTemplateId
                && goldRegister.Count == 1 && goldRegister.BuyoutPrice == 500
                && !AuctionRequestParser.GoldRegister(capturedGoldRegister, out _, out var noPriceReason)
                && noPriceReason == "gold-price"
                && !AuctionRequestParser.GoldRegister(pricedGoldRegister[..27], out _, out _));
            var average = new byte[8]; Put(average, 1, 3027);
            var goldAverage = new byte[] { 1, 0x7D, 0xEB, 0x28, 0, 0 };
            var goldAverageOpaque = goldAverage.ToArray(); goldAverageOpaque[0] = 0; goldAverageOpaque[5] = 1;
            check("average-price keeps ordinary8-byte and native gold6-byte layouts distinct",
                AuctionRequestParser.AveragePrice(average, out int id) && id == 3027
                && !AuctionRequestParser.AveragePrice(average[..7], out _)
                && AuctionRequestParser.AveragePrice(goldAverage, out int goldId, out bool goldMarket)
                && goldId == AuctionRepository.GoldTemplateId && goldMarket
                && AuctionRequestParser.AveragePrice(goldAverageOpaque, out goldId, out goldMarket)
                && goldId == AuctionRepository.GoldTemplateId && goldMarket);
            var cancel = new byte[9]; Put(cancel, 1, 33); Put(cancel, 5, 1_800_000_000);
            check("cancel requires the complete9-byte identity/auxiliary body", AuctionRequestParser.Cancel(cancel, out id) && id == 33
                && !AuctionRequestParser.Cancel(cancel[..5], out _));
            var bid = BidBody(33, 300);
            check("bid requires the complete26-byte body and positive amount/id", AuctionRequestParser.Bid(bid, out var parsedBid)
                && parsedBid.ListingId == 33 && parsedBid.Amount == 300 && !AuctionRequestParser.Bid(bid[..9], out _));
            var goldBid = GoldBidBody(33, 300, AuctionRepository.GoldTemplateId);
            check("gold bid uses the distinct17-byte market/item layout and accepts an opaque tail",
                AuctionRequestParser.GoldBid(goldBid, out var parsedGoldBid)
                && parsedGoldBid.ListingId == 33 && parsedGoldBid.Amount == 300
                && parsedGoldBid.GoldItemId == AuctionRepository.GoldTemplateId
                && AuctionRequestParser.GoldBid(goldBid.Concat(new byte[] { 0xA5, 0x5A }).ToArray(), out _)
                && !AuctionRequestParser.GoldBid(goldBid[..16], out _)
                && !AuctionRequestParser.GoldBid(bid, out _));
            var goldCancel = GoldCancelBody(33, 0x6AE4E966);
            check("gold cancel uses market one plus u32 listing identity and opaque expiry",
                AuctionRequestParser.GoldCancel(goldCancel, out long parsedGoldCancel)
                && parsedGoldCancel == 33
                && !AuctionRequestParser.GoldCancel(CancelBody(33), out _)
                && !AuctionRequestParser.GoldCancel(goldCancel[..8], out _));
            check("list mode has exact one-byte0/1 width", AuctionRequestParser.List(new byte[] { 0 }, out byte mode) && mode == 0
                && AuctionRequestParser.List(new byte[] { 1 }, out mode) && mode == 1
                && !AuctionRequestParser.List(Array.Empty<byte>(), out _) && !AuctionRequestParser.List(new byte[] { 2 }, out _)
                && !AuctionRequestParser.List(new byte[] { 1, 0 }, out _));
            var byItem = SearchBody(new[] { 3027 }, category: 0x32CA); byItem[17] = 9;
            var byCategory = new byte[20]; byCategory[5] = 0xCA; byCategory[6] = 0x32; byCategory[15] = 7;
            var goldByItem = GoldSearchBody(new[] { AuctionRepository.GoldTemplateId });
            var goldByCategory = new byte[14]; goldByCategory[0] = 1;
            var goldLegacyItem = SearchBody(new[] { AuctionRepository.GoldTemplateId }, mode: 1, category: 0x9CA5);
            var goldOpaqueCategory = goldByCategory.ToArray(); goldOpaqueCategory[0] = 0xA5;
            check("search preserves unknown nonzero UI fields without guessing filter/pagination semantics",
                AuctionRequestParser.Search(byItem, true, out var itemSearch) && itemSearch.ItemId == 3027 && itemSearch.Category == 0x32CA
                && AuctionRequestParser.Search(byCategory, false, out var categorySearch) && categorySearch.ItemId == 0 && categorySearch.Category == 0x32CA
                && AuctionRequestParser.Search(goldByItem, true, out var goldItemSearch)
                && goldItemSearch.GoldMarket && goldItemSearch.Mode == 1
                && goldItemSearch.ItemId == AuctionRepository.GoldTemplateId
                && AuctionRequestParser.Search(goldByCategory, false, out var goldCategorySearch)
                && goldCategorySearch.GoldMarket && goldCategorySearch.Mode == 1
                && AuctionRequestParser.Search(goldOpaqueCategory, false, out var opaqueGoldCategory)
                && opaqueGoldCategory.GoldMarket && opaqueGoldCategory.Mode == 1
                && AuctionRequestParser.Search(goldLegacyItem, true, out var legacyGoldItem)
                && !legacyGoldItem.GoldMarket && legacyGoldItem.Mode == 1 && legacyGoldItem.Category == 0x9CA5
                && !AuctionRequestParser.Search(byCategory, true, out _) && !AuctionRequestParser.Search(new byte[23], true, out _));
            foreach (var sample in new[]
            {
                (Hex: "0000000000001F03B79CFB0C000028790000DE8500000000000000000008", Ids: new[] { 3323, 31016, 34270 }),
                (Hex: "0000000000001F04B79CAB17FB05A017FB05C5B60600C6B606000000000000000008", Ids: new[] { 100341675, 100341664, 440005, 440006 }),
                (Hex: "0000000000001F07B79C670200007AD327007BD327007CD3270080D3270000E928004B9A05060000000000000008", Ids: new[] { 615, 2610042, 2610043, 2610044, 2610048, 2681088, 101030475 })
            })
            {
                var captured = Convert.FromHexString(sample.Hex);
                check("real search request replays every candidate without truncation: " + sample.Ids.Length,
                    captured.Length == 18 + 4 * sample.Ids.Length
                    && AuctionRequestParser.Search(captured, true, out var parsed)
                    && parsed.Mode == 0 && parsed.Category == 0x9CB7 && parsed.ItemIds.SequenceEqual(sample.Ids));
            }
            foreach (var change in new Action<byte[]>[] { b => b[7] = 0, b => b[7] = 2,
                b => b[7] = byte.MaxValue, b => Put(b, 10, 0), b => Put(b, 10, -1) })
            {
                var bad = byItem.ToArray(); change(bad);
                check("invalid search count/template cannot become a broad query", !AuctionRequestParser.Search(bad, true, out _));
            }
            check("candidate width is exact and duplicate templates remain valid candidates",
                !AuctionRequestParser.Search(byItem[..21], true, out _)
                && !AuctionRequestParser.Search(byItem.Concat(new byte[] { 0 }).ToArray(), true, out _)
                && AuctionRequestParser.Search(SearchBody(new[] { 3027, 3027 }), true, out var duplicates)
                && duplicates.ItemIds.SequenceEqual(new[] { 3027, 3027 }));
            check("maximum byte-sized candidate vector remains bounded and complete",
                AuctionRequestParser.Search(SearchBody(Enumerable.Repeat(3027, byte.MaxValue).ToArray()), true, out var maximum)
                && maximum.ItemIds.Count == byte.MaxValue);
            foreach (short slot in Enumerable.Range(354, 6).Select(i => (short)i))
            {
                InventoryService.TryResolveMainVirtualItemId(slot, out int itemId);
                var crystal = CrystalRegisterBody(slot, itemId, 3);
                check("wire crystal alias resolves only its fixed Main wallet identity: " + slot,
                    AuctionRequestParser.Register(crystal, out var parsed) && parsed.ListType == InventoryListType.Main
                    && parsed.WireSourceList == (byte)InventoryListType.CrystalWarehouse && parsed.Slot == slot && parsed.ItemId == itemId);
            }
            foreach (var bad in new[] { CrystalRegisterBody(358, 3033, 3), CrystalRegisterBody(360, 10100115, 3),
                CrystalRegisterBody(0, AuctionRepository.GoldTemplateId, 1), CrystalRegisterBody(30, 3027, 1) })
                check("crystal wire alias never authorizes another wallet/container", !AuctionRequestParser.Register(bad, out _));
        }

        private static void CheckBuilders(Action<string, bool> check)
        {
            var init = new AuctionServiceInitBodyBuilder();
            check("login has two exactA21 service flags and no invented extra occurrences",
                init.NotiType == (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE
                && init.TryBuild(null, 0, out var first) && first.SequenceEqual(new byte[] { 0, 1 })
                && init.TryBuild(null, 1, out var second) && second.SequenceEqual(new byte[] { 1, 1 })
                && !init.TryBuild(null, 2, out _));
            var sequence = NewCharacterInitSequence.Build();
            ushort serviceType = (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE;
            int auctionIndex = sequence.FindIndex(entry => entry.Type == serviceType);
            check("actual character-init sequence wires precisely two adjacent auctionNOTIs in occurrence order",
                auctionIndex >= 0 && auctionIndex + 1 < sequence.Count
                && sequence.Count(entry => entry.Type == serviceType) == 2
                && sequence[auctionIndex].Kind == SelectCharacterPacketTemplateKind.Raw
                && sequence[auctionIndex].Command == 0 && sequence[auctionIndex].OccurrenceIndex == 0
                && sequence[auctionIndex + 1].Kind == SelectCharacterPacketTemplateKind.Raw
                && sequence[auctionIndex + 1].Type == serviceType
                && sequence[auctionIndex + 1].Command == 0 && sequence[auctionIndex + 1].OccurrenceIndex == 1);
            var openings = AuctionServiceNotificationBuilder.BuildOpenPackets();
            check("direct service opening sharesA21 CMD/NOTI taxonomy", openings.Length == 2
                && Envelope(openings[0], 0, (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE)
                && Body(openings[0]).SequenceEqual(new byte[] { 0, 1 }) && Body(openings[1]).SequenceEqual(new byte[] { 1, 1 }));
            var core = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3027, Count = 3, Attr = 5, Durability = 35, Rune = 101 };
            var before = core.ToBytes(); var listing = Listing(core); long now = listing.ExpiresAtUnix - 3600;
            var search = AuctionPacketBuilder.Search(CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY, 0, new[] { listing }, now);
            check("BB classification answers through the registeredBA callback with one168-byte row",
                Envelope(search, 1, (ushort)CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY) && search.Length == 15 + 8 + 168
                && U16(Body(search), 6) == 1);
            var searchRow = Body(search).AsSpan(8, 168);
            check("search carries total initial bid, buyout and the83-byte attr/value remap",
                I32(searchRow, 9) == listing.StartingPrice * core.Count && I32(searchRow, 13) == listing.BuyoutPrice * core.Count
                && I32(searchRow, 44) == 3027 && searchRow[48] == 5 && I32(searchRow, 49) == 3
                && searchRow[42] == 1 && I32(searchRow, 156) == 3600);
            var goldListing = Listing(new ItemCore
                { ItemKind = ItemCore.KindMaterial, ItemId = AuctionRepository.GoldTemplateId, Count = 2 });
            var goldSearchRow = Body(AuctionPacketBuilder.Search(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY,
                1, new[] { goldListing }, now)).AsSpan(8, AuctionPacketBuilder.SearchRecordSize);
            var goldSellerRow = Body(AuctionPacketBuilder.Seller(1, new[] { goldListing }, now))
                .AsSpan(3, AuctionPacketBuilder.SellerRecordSize);
            check("gold search and seller rows disable bidding while retaining the full buyout price",
                I32(goldSearchRow, 9) == -1 && I32(goldSellerRow, 8) == -1
                && I32(goldSearchRow, 13) == goldListing.BuyoutPrice
                && I32(goldSellerRow, 12) == goldListing.BuyoutPrice);
            var seller = AuctionPacketBuilder.Seller(0, new[] { listing }, now);
            var sellerRow = Body(seller).AsSpan(3, 151);
            check("seller uses u8 count and item offset30, not search offset43",
                seller.Length == 15 + 3 + 151 && Body(seller)[2] == 1 && I32(sellerRow, 31) == 3027
                && sellerRow[35] == 5 && I32(sellerRow, 36) == 3 && sellerRow[29] == 1 && I32(sellerRow, 143) == 3600);
            var bid = new AuctionBidRecord { ListingId = listing.ListingId, BidAmount = 77, Status = 1 };
            var bids = AuctionPacketBuilder.Bids(0, new[] { (listing, bid) }, now);
            var bidRow = Body(bids).AsSpan(4, 159);
            check("bids use u16 count,159-byte rows, item38 and remaining-hour37",
                bids.Length == 15 + 4 + 159 && U16(Body(bids), 2) == 1 && I32(bidRow, 8) == 77
                && bidRow[37] == 1 && I32(bidRow, 39) == 3027 && bidRow[43] == 5 && I32(bidRow, 44) == 3);
            foreach (var sample in new[] { (Seconds: -1, Hours: 0), (Seconds: 0, Hours: 0),
                (Seconds: 1, Hours: 0), (Seconds: 3599, Hours: 0), (Seconds: 3600, Hours: 1),
                (Seconds: 3601, Hours: 1), (Seconds: 86399, Hours: 23), (Seconds: 86400, Hours: 24),
                (Seconds: 172799, Hours: 47), (Seconds: 172800, Hours: 48) })
            {
                long projectedAt = listing.ExpiresAtUnix - sample.Seconds;
                var searchTime = Body(AuctionPacketBuilder.Search(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY,
                    0, new[] { listing }, projectedAt)).AsSpan(8, AuctionPacketBuilder.SearchRecordSize);
                var sellerTime = Body(AuctionPacketBuilder.Seller(0, new[] { listing }, projectedAt))
                    .AsSpan(3, AuctionPacketBuilder.SellerRecordSize);
                bid.Status = 0;
                var bidTime = Body(AuctionPacketBuilder.Bids(0, new[] { (listing, bid) }, projectedAt))
                    .AsSpan(4, AuctionPacketBuilder.BidRecordSize);
                check("all auction lists project actual whole hours independently of bid status: " + sample.Seconds,
                    searchTime[42] == sample.Hours && sellerTime[29] == sample.Hours && bidTime[37] == sample.Hours
                    && I32(searchTime, 156) == Math.Max(0, sample.Seconds)
                    && I32(sellerTime, 143) == Math.Max(0, sample.Seconds)
                    && I32(bidTime, 151) == Math.Max(0, sample.Seconds)
                    && I32(searchTime, 4) == listing.ExpiresAtUnix
                    && listing.Item.ItemCoreData.SequenceEqual(before));
            }
            check("expiry changes only auction wire, never committed durable item properties",
                listing.Item.ItemCoreData.SequenceEqual(before) && U16(searchRow, 43 + ItemCore.DurabilityOffset) == 0
                && I32(searchRow, 43 + ItemCore.RuneOffset) == listing.ExpiresAtUnix);
            var many = Enumerable.Repeat(listing, 260).ToArray();
            var capped = AuctionPacketBuilder.Seller(0, many, now);
            check("seller count and emitted row count are capped together at255", Body(capped)[2] == 255 && capped.Length == 15 + 3 + 255 * 151);
            var emptySeller = AuctionPacketBuilder.Seller(1, Array.Empty<AuctionListing>(), now);
            var emptyBids = AuctionPacketBuilder.Bids(0, Array.Empty<(AuctionListing, AuctionBidRecord)>(), now);
            check("empty list replies retain exact native width", Body(emptySeller).SequenceEqual(new byte[] { 1, 1, 0 })
                && Body(emptyBids).SequenceEqual(new byte[] { 1, 0, 0, 0 }));
            var average = AuctionPacketBuilder.AveragePrice(1, 123, 456, 2);
            check("average reply is58-byte native body with no probe prefixes", average.Length == 15 + 58
                && Body(average)[0] == 1 && Body(average)[1] == 1 && I32(Body(average), 2) == 123
                && I32(Body(average), 10) == 456 && I32(Body(average), 34) == 2);
            check("mutation callbacks receive flag plus mode and failures only valid client codes",
                Body(AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_BIDDING, 0)).SequenceEqual(new byte[] { 1, 0 }));
            check("gold market failure bodies remain distinct from ordinary safety failure",
                Body(AuctionPacketBuilder.GoldFailure(CmdPacketTypeA21.AUCTION_REGIST_ITEM))
                    .SequenceEqual(new byte[] { 0, 0x98, 0 })
                && Body(AuctionPacketBuilder.GoldFailure(CmdPacketTypeA21.AUCTION_REGIST_CANCEL))
                    .SequenceEqual(new byte[] { 0, 0x98, 1 })
                && Body(AuctionPacketBuilder.GoldFailure(CmdPacketTypeA21.AUCTION_BIDDING))
                    .SequenceEqual(new byte[] { 0, 1 }));
            foreach (var command in new[] { CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                CmdPacketTypeA21.AUCTION_REGIST_ITEM, CmdPacketTypeA21.AUCTION_REGIST_CANCEL,
                CmdPacketTypeA21.AUCTION_BIDDING, CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY,
                CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO, CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO,
                CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE })
            {
                var failure = AuctionPacketBuilder.Failure(command);
                check("independently evidenced failure has exact command/body: " + command,
                    Envelope(failure, 1, (ushort)command) && Body(failure).SequenceEqual(new byte[] { 0, 0x7E }));
            }
            bool historyRefused = false;
            try { AuctionPacketBuilder.Failure(CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY); }
            catch (ArgumentOutOfRangeException) { historyRefused = true; }
            check("unverified history cannot acquire a guessed failure command reply", historyRefused);
            var invalid = Listing(core); invalid.Item.ItemCoreData = Array.Empty<byte>();
            bool refused = false;
            try { AuctionPacketBuilder.Seller(0, new[] { invalid }, now); } catch (InvalidOperationException) { refused = true; }
            check("missing escrow ItemCore fails instead of synthesizing a fake material row", refused);
            var title = AuctionHandler.ResolveCategoryFilter(0x2EE4); var expert = AuctionHandler.ResolveCategoryFilter(0x80EA);
            check("auction categories reuse typedPVF dimensions rather than adding new persisted kinds",
                title.filterEquipType == "[title name]" && expert.filterExpertType == "[doll_controller]"
                && AuctionHandler.ResolveCategoryFilter(0x32CB).kind == ItemCore.KindConsumable);
        }

        private static void CheckPvfClassification(Action<string, bool> check)
        {
            const string ordinary = "[stackable type]\n`[waste]`\n[cool time]\n3500\n";
            var before = PvfLib.StackableItemFile.Parse(ordinary);
            check("missing expert andaltar-energy definitions remain absent rather than acquiring defaults",
                before.ExpertType == null && before.AdvanceAltarNeedGage == null && before.CoolTime == 3500);
            foreach (string expert in new[] { "[alchemist]", "[doll_controller]", "[enchanter]" })
            {
                var parsed = PvfLib.StackableItemFile.Parse(ordinary + "[expert type]\n`" + expert
                    + "`\n[advance altar need gage]\n12\n");
                check("typed expert-resource field coexists with existing guardianenergy parsing: " + expert,
                    parsed.ExpertType == expert && parsed.StackableType == before.StackableType
                    && parsed.AdvanceAltarNeedGage == 12 && parsed.CoolTime == before.CoolTime);
                var originalMetadata = new ItemMetadata { ItemKind = "stackable", StackableType = before.StackableType };
                var parsedMetadata = new ItemMetadata { ItemKind = "stackable", StackableType = parsed.StackableType };
                check("expert classification does not redefine globalpersistedItemKind: " + expert,
                    ItemMetadataResolver.TryResolveItemKind(5005, originalMetadata, out byte oldKind)
                    && ItemMetadataResolver.TryResolveItemKind(5005, parsedMetadata, out byte parsedKind)
                    && oldKind == ItemCore.KindConsumable && parsedKind == oldKind);
            }
            bool invalidEnergyRefused = false;
            try { PvfLib.StackableItemFile.Parse(ordinary + "[expert type]\n`[alchemist]`\n[advance altar need gage]\n-1\n"); }
            catch (InvalidOperationException) { invalidEnergyRefused = true; }
            check("the new expert-resource tag does not bypass existing negativealtar-energy validation", invalidEnergyRefused);
        }

        private static async Task CheckRuntime(Action<string, bool> check)
        {
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                var initRegistry = new InitPacketBuilderRegistry(f.Database, f.Sessions);
                ushort serviceType = (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE;
                check("actual initbuilder registry resolves both auction occurrences through registeredA21 owner",
                    initRegistry.HasBuilder(serviceType)
                    && initRegistry.TryBuild(serviceType, null, 0, out var first) && first.SequenceEqual(new byte[] { 0, 1 })
                    && initRegistry.TryBuild(serviceType, null, 1, out var second) && second.SequenceEqual(new byte[] { 1, 1 })
                    && !initRegistry.TryBuild(serviceType, null, 2, out _));
                await f.Handler.HandleMyRegistedItemInfo(peer.Session, Header(CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO), new byte[] { 0 });
                check("real current-session seller request writes the native empty reply", peer.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 1, 0, 0 }));
                foreach (var query in new[]
                {
                    (ItemId: 3027, Gold: false, Ready: 1),
                    (ItemId: AuctionRepository.GoldTemplateId, Gold: true, Ready: 0)
                })
                {
                    var request = query.Gold ? new byte[6] : new byte[8];
                    if (query.Gold) request[0] = 1;
                    Put(request, 1, query.ItemId);
                    await f.Handler.HandleAskAveragePrice(peer.Session, Header(CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE), request);
                    var responses = peer.Drain();
                    check(query.Ready == 1
                            ? "empty ordinaryauction enables both Ready phases without inventing price or samplecount"
                            : "gold window opener accepts six-byte request and returns one mode-one reply",
                        query.Gold
                            ? responses.Count == 1 && responses[0].Length == 15 + 58
                                && Envelope(responses[0], 1, (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE)
                                && Body(responses[0])[0] == 1 && Body(responses[0])[1] == 1
                                && I32(Body(responses[0]), 2) == query.Ready
                                && Body(responses[0]).Skip(10).All(value => value == 0)
                            : responses.Count == 2 && responses[0].Length == 15 + 58 && responses[1].Length == 15 + 58
                                && Body(responses[0])[1] == 0 && Body(responses[1])[1] == 1
                                && responses.All(packet => Envelope(packet, 1, (ushort)CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE)
                                    && I32(Body(packet), 2) == query.Ready && Body(packet).Skip(10).All(value => value == 0)));
                }
                var legacyGoldAverage = new byte[8]; Put(legacyGoldAverage, 1, AuctionRepository.GoldTemplateId);
                await f.Handler.HandleAskAveragePrice(peer.Session,
                    Header(CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE), legacyGoldAverage);
                var legacyGoldResponses = peer.Drain();
                check("ordinary-width query for a gold template retains the existing two-phase reply",
                    legacyGoldResponses.Count == 2
                    && Body(legacyGoldResponses[0])[1] == 0 && Body(legacyGoldResponses[1])[1] == 1);
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), new byte[27]);
                check("malformed register returns nonsecurityfailure plus its realnotice without changing assets",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM, "请求字段")
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(0).Count == 100000);
                await f.Handler.HandleMyAuctionHistory(peer.Session, Header(CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY), new byte[] { 0 });
                var historyPackets = peer.Drain();
                check("unverified history publishes only the existing notice and never a guessed commandACK",
                    historyPackets.Count == 1 && NoticeContains(historyPackets[0], "未适配")
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(0).Count == 100000);
                await f.Handler.HandleBuyItemApiece(peer.Session, Header(CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE), new byte[34]);
                check("unverifiedAPIECE request uses its evidencedfailure plus notice without spending currency",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, "未适配")
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(0).Count == 100000);
                foreach (var mutation in new (string Label, Action<Peer> Change)[]
                {
                    ("UID", p => p.Session.Player.UserId++),
                    ("run", p => p.Session.Player.NextDungeonRunGeneration()),
                    ("selection", p => { p.Session.Player.BeginDungeonSelection(default); p.Session.Player.ClearDungeonSelection(); }),
                    ("GameSession", p => p.Session.GameSession = null),
                    ("account", p => p.Session.Account = new AccountRecord { AccountId = 1 }),
                })
                {
                    var current = await f.Peer();
                    await current.SendGate.WaitAsync(); Task handling;
                    try
                    {
                        handling = f.Handler.HandleMyRegistedItemInfo(current.Session,
                            Header(CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO), new byte[] { 0 });
                        check("read request truly waits on real send lock before changing " + mutation.Label, !handling.IsCompleted);
                        mutation.Change(current);
                    }
                    finally { current.SendGate.Release(); }
                    await handling.WaitAsync(TimeSpan.FromSeconds(5));
                    check("send-lock preparation suppresses old " + mutation.Label + " source", current.Drain().Count == 0);
                }
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                var capturedNoPrice = Convert.FromHexString(
                    "010000007DEB280001000000FFFFFFFFFFFFFFFF0000000018000000");
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), capturedNoPrice);
                check("captured unpriced gold registration fails with native gold ACK without changing assets",
                    GoldRejected(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                    && f.CountListings() == 0 && peer.Lease.Inventory.GetMainVirtualCount(0).Count == 100000);

                var priced = capturedNoPrice.ToArray(); Put(priced, 16, 500);
                var unknown = priced.ToArray(); Put(unknown, 4, 3027);
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), unknown);
                check("gold registration rejects an unknown denomination without changing assets",
                    GoldRejected(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                    && f.CountListings() == 0 && peer.Lease.Inventory.GetMainVirtualCount(0).Count == 100000);
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), priced);
                check("gold registration rejects insufficient wallet balance without creating a listing",
                    GoldRejected(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                    && f.CountListings() == 0 && peer.Lease.Inventory.GetMainVirtualCount(0).Count == 100000);

                // A rejected domain transaction may reload the lease's inventory.
                // Mutate and verify its current owner, not the fixture's old instance.
                peer.Lease.Inventory.SetMainVirtualCount(0, 1_100_000);
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), priced);
                var registered = peer.Drain();
                check("priced gold registration escrows the denomination through the owned wallet",
                    f.CountListings() == 1 && peer.Lease.Inventory.GetMainVirtualCount(0).Count == 100000
                    && registered.Any(packet => Envelope(packet, 1, (ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                        && Body(packet).SequenceEqual(new byte[] { 1, 1 })));
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                using (await f.Transitions.AcquireAsync(peer.CharacterId))
                {
                    var queued = f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), RegisterBody(3027, 3));
                    check("register freezes original slot while waiting on real character transition gate", !queued.IsCompleted);
                    lock (peer.Lease.SyncRoot)
                    { var replacement = ItemCore.FromBytes(peer.Inventory.GetItem(InventoryListType.Main, 30).ToBytes()); replacement.Count = 9; peer.Inventory.SetItem(InventoryListType.Main, 30, replacement); }
                    // Finish after releasing the gate, below.
                    f.Pending = queued;
                }
                await f.Pending.WaitAsync(TimeSpan.FromSeconds(5));
                check("same-template slot replacement cannot authorize original register request",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM, "来源槽")
                    && f.CountListings() == 0 && peer.Inventory.GetItem(InventoryListType.Main, 30).Count == 9);
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                await peer.SendGate.WaitAsync(); Task handling;
                try
                {
                    handling = f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), RegisterBody(3027, 3));
                    check("register commits before waiting on actual socket send lock", !handling.IsCompleted && f.CountListings() == 1);
                    peer.Session.Player.UserId++;
                }
                finally { peer.SendGate.Release(); }
                await handling.WaitAsync(TimeSpan.FromSeconds(5));
                check("expired postcommit source suppresses UI but does not undo escrow or deduct twice", peer.Drain().Count == 0
                    && f.CountListings() == 1 && peer.Inventory.GetItem(InventoryListType.Main, 30).Count == 5);
            }
            foreach (string label in new[] { "directory", "lease" })
            {
                using var f = new Fixture(); var peer = await f.Peer();
                await peer.SendGate.WaitAsync(); Task handling;
                try
                {
                    handling = f.Handler.HandleMyRegistedItemInfo(peer.Session, Header(CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO), new byte[] { 0 });
                    check("seller projection is queued on actual send lock before " + label, !handling.IsCompleted);
                    if (label == "directory") f.Sessions.Register(peer.CharacterId, new EnhancedClientSession(new TcpClient(), new GamePacketHeader()));
                    else InventoryContext.Unregister(peer.Session.SessionId);
                }
                finally { peer.SendGate.Release(); }
                await handling.WaitAsync(TimeSpan.FromSeconds(5));
                check("queued seller reply rejects replacement " + label, peer.Drain().Count == 0);
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                await peer.SendGate.WaitAsync(); Task handling;
                try
                {
                    handling = f.Handler.HandleMyRegistedItemInfo(peer.Session, Header(CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO), new byte[] { 0 });
                    check("seller query preparation waits for actual send lock rather than freezing an earlyempty list", !handling.IsCompleted);
                    var repository = new AuctionRepository(f.Database);
                    var core = peer.Inventory.GetItem(InventoryListType.Main, 30).ToBytes();
                    var added = repository.RegisterListing(new AuctionActor(peer.Lease, () => true), InventoryListType.Main,
                        30, 3027, 100, 0, TimeSpan.FromHours(24), 3, core);
                    check("a concurrent committed listing is available before queued list preparation", added.Success && f.CountListings() == 1);
                }
                finally { peer.SendGate.Release(); }
                await handling.WaitAsync(TimeSpan.FromSeconds(5));
                var reply = peer.Drain().Single();
                check("queued seller read publishes the latest committed listing at send-lock preparation", Body(reply)[2] == 1 && reply.Length == 15 + 3 + 151);
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer(); int written = 0;
                var handler = new AuctionHandler(new AuctionService(new AuctionRepository(f.Database), f.Mailbox), f.Transitions,
                    (session, prepare, guard) => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(peer.SendGate, prepare,
                        async packet =>
                        {
                            await session.Stream.WriteAsync(packet);
                            if (++written == 1) session.Player.UserId++;
                        }, guard));
                var request = new byte[8]; Put(request, 1, 3027);
                await handler.HandleAskAveragePrice(peer.Session, Header(CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE), request);
                var sent = peer.Drain();
                check("actual send-lock batch rechecks the source between mode0 andmode1",
                    written == 1 && sent.Count == 1 && Body(sent[0])[1] == 0);
            }
            using (var f = new Fixture())
            {
                var seller = await f.Peer(); var buyer = await f.Peer();
                lock (seller.Lease.SyncRoot) seller.Inventory.SetMainVirtualCount(0, 1_500_000);
                InventoryPersistenceService.SaveDirty(seller.Lease);
                f.Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "UPDATE accounts SET cera=1000,token_cera=5,happy_token_cera=7 WHERE account_id=1;"; cmd.ExecuteNonQuery(); });
                var repository = new AuctionRepository(f.Database);
                var registration = repository.RegisterGoldListing(new AuctionActor(seller.Lease, () => true),
                    AuctionRepository.GoldTemplateId, 1, 200, TimeSpan.FromHours(24));
                check("gold currency display projects a real committed escrow listing", registration.Success);
                await f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING),
                    GoldBidBody(checked((int)registration.ListingId), 200, AuctionRepository.GoldTemplateId));
                var packets = buyer.Drain();
                var wallet = repository.ReadWallet(new AuctionActor(buyer.Lease, () => true));
                check("gold purchase ACK refreshes only payment currency while gold remains in mail",
                    packets.Count == 2
                    && Envelope(packets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_BIDDING)
                    && Body(packets[0]).SequenceEqual(new byte[] { 1, 1 })
                    && Envelope(packets[1], 0, (ushort)NotiPacketTypeA21.CERA)
                    && Body(packets[1]).Length == 13
                    && I32(Body(packets[1]), 1) == wallet.Cera && I32(Body(packets[1]), 5) == wallet.TokenCera
                    && I32(Body(packets[1]), 9) == wallet.HappyTokenCera
                    && buyer.Lease.Inventory.GetMainVirtualCount(0).Count == 100_000);
            }
        }

        private static async Task CheckGoldPurchaseRejections(Action<string, bool> check)
        {
            using var f = new Fixture();
            var seller = await f.Peer(); var buyer = await f.Peer(); var competitor = await f.Peer();
            seller.Lease.Inventory.SetMainVirtualCount(0, 1_500_000);
            f.Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "UPDATE accounts SET cera=1000 WHERE account_id=1;"; cmd.ExecuteNonQuery(); });
            var repository = new AuctionRepository(f.Database);
            var registration = repository.RegisterGoldListing(new AuctionActor(seller.Lease, () => true),
                AuctionRepository.GoldTemplateId, 1, 200, TimeSpan.FromHours(24));
            var request = GoldBidBody(checked((int)registration.ListingId), 200, AuctionRepository.GoldTemplateId);
            await f.Handler.HandleBidding(seller.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), request);
            check("self purchase returns the gold failure ACK and native self-purchase notice without spending currency",
                GoldRejectedWithNotice(seller.Drain(), CmdPacketTypeA21.AUCTION_BIDDING, "无法竞拍自己上架的物品")
                && repository.ReadWallet(new AuctionActor(seller.Lease, () => true)).Cera == 1000
                && repository.GetListing(registration.ListingId).Status == AuctionListingStatus.Active);

            await Task.WhenAll(
                Task.Run(() => f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), request)),
                Task.Run(() => f.Handler.HandleBidding(competitor.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), request)));
            var responses = new[] { buyer.Drain(), competitor.Drain() };
            check("concurrent gold purchases have one success and one native failure ACK",
                responses.Count(packets => packets.Count == 2
                    && Body(packets[0]).SequenceEqual(new byte[] { 1, 1 })) == 1
                && responses.Count(packets => GoldRejected(packets, CmdPacketTypeA21.AUCTION_BIDDING)) == 1
                && repository.ReadWallet(new AuctionActor(buyer.Lease, () => true)).Cera == 800
                && buyer.Lease.Inventory.GetMainVirtualCount(0).Count == 100000
                && competitor.Lease.Inventory.GetMainVirtualCount(0).Count == 100000);
            foreach (var peer in new[] { buyer, competitor })
            {
                await f.Handler.HandleBidding(peer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), request);
                check("a stale sold row returns a failure ACK and sold-out notice without a second charge or delivery",
                    GoldRejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_BIDDING, "因物品已售完")
                    && repository.ReadWallet(new AuctionActor(peer.Lease, () => true)).Cera == 800);
            }

            // A completed gold sale must not poison the next registration or
            // leave the market handler with a stale settlement/search state.
            seller.Lease.Inventory.SetMainVirtualCount(0, 1_500_000);
            var second = repository.RegisterGoldListing(
                new AuctionActor(seller.Lease, () => true),
                AuctionRepository.GoldTemplateId, 1, 200, TimeSpan.FromHours(24));
            var secondRequest = GoldBidBody(
                checked((int)second.ListingId), 200, AuctionRepository.GoldTemplateId);
            await f.Handler.HandleBidding(competitor.Session,
                Header(CmdPacketTypeA21.AUCTION_BIDDING), secondRequest);
            var secondPackets = competitor.Drain();
            check("a later gold registration remains purchasable after the first sale settles",
                second.Success
                && secondPackets.Count == 2
                && Body(secondPackets[0]).SequenceEqual(new byte[] { 1, 1 })
                && repository.GetListing(second.ListingId).Status != AuctionListingStatus.Active);
            await f.Handler.HandleBidding(buyer.Session,
                Header(CmdPacketTypeA21.AUCTION_BIDDING), secondRequest);
            check("a later sold row still returns the native failure ACK and sold-out notice",
                GoldRejectedWithNotice(buyer.Drain(), CmdPacketTypeA21.AUCTION_BIDDING, "因物品已售完"));

        }

        private static async Task CheckTokenCeraMailClaim(Action<string, bool> check)
        {
            foreach (bool replaceSource in new[] { false, true })
            {
                using var f = new Fixture(); var peer = await f.Peer();
                var mail = f.Mailbox.SendSystemMail(new MailboxSendRequest
                {
                    SenderCharacterId = peer.CharacterId, ReceiverCharacterId = peer.CharacterId,
                    Text = "token voucher", Unlimited = true, IdempotencyKey = "auction-protocol-token",
                    Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = 2681762, ItemCount = 198 } }
                });
                var characters = new SqliteCharacterRepository(f.Database);
                var refresh = new InventoryRefreshSender(new SqliteSelectCharacterDataSource(f.Database, characters), characters, f.Database);
                var handler = new MailboxHandler(characters, f.Mailbox, f.Sessions, refresh,
                    sendSystemNotice: (session, prepare, guard) =>
                    {
                        if (replaceSource) session.Player.UserId++;
                        return session.SendPreparedPacketBatchAsync(prepare, guard);
                    }, registerMaintenance: false);
                var request = new byte[4]; Put(request, 0, checked((int)mail.MessageId));
                await handler.HandleClaimMailbox(peer.Session, Header(CmdPacketTypeA21.MAILBOX_EXTRACT_ITEM), request);
                var packets = peer.Drain();
                    check(replaceSource
                        ? "token voucher claim keeps direct currency projection independent of the old best-effort hook"
                        : "token voucher claim projects the current wallet without the legacy zero-result ACK",
                    mail.Success && packets.Count == 1
                    && Envelope(packets[0], 0, (ushort)NotiPacketTypeA21.CERA)
                    && I32(Body(packets[0]), 5) == 198
                    && !packets.Any(packet => Envelope(packet, 1, (ushort)CmdPacketTypeA21.MAILBOX_EXTRACT_ITEM))
                    && new AuctionRepository(f.Database).ReadWallet(new AuctionActor(peer.Lease, () => true)).TokenCera == 198
                    && peer.Lease.Inventory.CountMainItem(2681762) == 0);
            }
        }

        private static async Task CheckTokenCeraProjectionFence(Action<string, bool> check)
        {
            foreach (string change in new[] { "wallet", "uid", "directory", "lease" })
            {
                using var f = new Fixture();
                var peer = await f.Peer();
                var mail = f.Mailbox.SendSystemMail(new MailboxSendRequest
                {
                    SenderCharacterId = peer.CharacterId, ReceiverCharacterId = peer.CharacterId,
                    Text = "queued token voucher", Unlimited = true, IdempotencyKey = "queued-token",
                    Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = 2681762, ItemCount = 99 } }
                });
                var characters = new SqliteCharacterRepository(f.Database);
                var refresh = new InventoryRefreshSender(new SqliteSelectCharacterDataSource(f.Database, characters), characters, f.Database);
                var handler = new MailboxHandler(characters, f.Mailbox, f.Sessions, refresh, registerMaintenance: false);
                var request = new byte[4]; Put(request, 0, checked((int)mail.MessageId));
                await peer.SendGate.WaitAsync();
                Task handling;
                try
                {
                    handling = handler.HandleClaimMailbox(peer.Session, Header(CmdPacketTypeA21.MAILBOX_EXTRACT_ITEM), request);
                    check("token claim is durable while waiting for send lock: " + change,
                        mail.Success && !handling.IsCompleted
                        && new AuctionRepository(f.Database).ReadWallet(new AuctionActor(peer.Lease, () => true)).TokenCera == 99);
                    if (change == "wallet")
                        f.Database.Write((connection, transaction) => DfoServer.Game.Currency.CurrencyService.GrantTokenCera(
                            connection, transaction, peer.CharacterId, 1));
                    else if (change == "uid") peer.Session.Player.UserId++;
                    else if (change == "directory")
                        f.Sessions.Register(peer.CharacterId, new EnhancedClientSession(new TcpClient(), new GamePacketHeader()));
                    else InventoryContext.Unregister(peer.Session.SessionId);
                }
                finally { peer.SendGate.Release(); }
                await handling.WaitAsync(TimeSpan.FromSeconds(5));
                var packets = peer.Drain();
                check("queued token projection revalidates source and reads current wallet: " + change,
                    change == "wallet"
                        ? packets.Count == 1 && Envelope(packets[0], 0, (ushort)NotiPacketTypeA21.CERA)
                            && I32(Body(packets[0]), 5) == 100
                        : packets.Count == 0);
            }
        }

        private static async Task CheckMailboxFences(Action<string, bool> check)
        {
            foreach (string label in new[] { "directory", "lease" })
            {
                using var f = new Fixture(); var peer = await f.Peer();
                var characters = new SqliteCharacterRepository(f.Database);
                var refresh = new InventoryRefreshSender(new SqliteSelectCharacterDataSource(f.Database, characters), characters, f.Database);
                var handler = new MailboxHandler(characters, f.Mailbox, f.Sessions, refresh, registerMaintenance: false);
                await peer.SendGate.WaitAsync(); Task notice;
                try
                {
                    notice = handler.NotifySystemMailArrived(peer.CharacterId);
                    check("actual mailbox notification waits on actual session send lock before " + label, !notice.IsCompleted);
                    if (label == "directory") f.Sessions.Register(peer.CharacterId, new EnhancedClientSession(new TcpClient(), new GamePacketHeader()));
                    else InventoryContext.Unregister(peer.Session.SessionId);
                }
                finally { peer.SendGate.Release(); }
                await notice.WaitAsync(TimeSpan.FromSeconds(5));
                check("system-mail projection cannot target replacement " + label + " or invent assets", peer.Drain().Count == 0);
            }
        }

        private static async Task CheckRegistrationRejections(Action<string, bool> check)
        {
            // The current user packet is verified against the current PVF's
            // sealing equipment100170173. An unsealed instance is not tradable.
            foreach (byte seal in new byte[] { 1, 0 })
            {
                using var f = new Fixture(); var peer = await f.Peer();
                var equipment = new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 100170173, Uid = 0x1234, SealFlag = seal };
                lock (peer.Lease.SyncRoot) peer.Inventory.SetItem(InventoryListType.Main, 43, equipment);
                if (!InventoryPersistenceService.SaveDirty(peer.Lease)) throw new InvalidOperationException("Equipment fixture did not persist.");
                var original = equipment.ToBytes();
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), CurrentEquipmentRegisterBody());
                var packets = peer.Drain();
                if (seal == 1)
                    check("real37-byte capture reaches the domain and commits sealed equipment without relaxing sourceidentity",
                        f.CountListings() == 1 && peer.Inventory.GetItem(InventoryListType.Main, 43) == null
                        && peer.Inventory.GetMainVirtualCount(0).Count == 99000
                        && packets.Any(p => Envelope(p, 1, (ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                            && Body(p).SequenceEqual(new byte[] { 1, 0 }))
                        && !packets.Any(p => Envelope(p, 1, (ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM) && Body(p)[0] == 0));
                else
                    check("the same captured request still refuses unsealedbound equipment with a nonsecurityfailure",
                        RejectedWithNotice(packets, CmdPacketTypeA21.AUCTION_REGIST_ITEM, "绑定") && f.CountListings() == 0
                        && peer.Inventory.GetMainVirtualCount(0).Count == 100000
                        && peer.Inventory.GetItem(InventoryListType.Main, 43).ToBytes().SequenceEqual(original));
            }
            foreach (string rejection in new[] { "invalid-field", "invalid-count", "quantity-exceeds-stack", "instance-locked", "trade-restricted", "insufficient-gold" })
            {
                using var f = new Fixture(); var peer = await f.Peer(); var request = RegisterBody(3027, 3);
                lock (peer.Lease.SyncRoot)
                {
                    if (rejection == "instance-locked" || rejection == "trade-restricted")
                    {
                        var core = peer.Inventory.GetItem(InventoryListType.Main, 30).Copy();
                        if (rejection == "instance-locked") core.SortLockFlag = 1; else core.TradeRestriction = 1;
                        peer.Inventory.SetItem(InventoryListType.Main, 30, core);
                    }
                    if (rejection == "insufficient-gold") peer.Inventory.SetMainVirtualCount(0, 0);
                }
                if (rejection == "invalid-field") Put(request, 24, 7);
                if (rejection == "invalid-count") Put(request, 8, 0);
                if (rejection == "quantity-exceeds-stack") Put(request, 8, 9);
                if (!InventoryPersistenceService.SaveDirty(peer.Lease)) throw new InvalidOperationException("Rejection fixture did not persist.");
                var before = peer.Inventory.GetItem(InventoryListType.Main, 30).ToBytes();
                int gold = peer.Inventory.GetMainVirtualCount(0).Count;
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), request);
                string notice = rejection == "insufficient-gold" ? "不足"
                    : rejection is "instance-locked" or "trade-restricted" ? "交易限制" : "数量";
                check("true " + rejection + " fails without safety-mode or successACK and leaves all assetsunchanged",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM, notice)
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(0).Count == gold
                    && peer.Inventory.GetItem(InventoryListType.Main, 30).ToBytes().SequenceEqual(before));
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                await f.Handler.HandleRegistCancel(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_CANCEL), new byte[5]);
                check("B8 uses its own evidenced7E failure branch plus notice, notB9's callback orsecuritymode",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_CANCEL, "请求字段")
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(0).Count == 100000);
            }
        }

        private static async Task CheckSearchProjection(Action<string, bool> check)
        {
            using (var f = new Fixture())
            {
                var seller = await f.Peer(); var buyer = await f.Peer();
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterListing(new AuctionActor(seller.Lease, () => true), InventoryListType.Main,
                    30, 3027, 100, 10, TimeSpan.FromHours(24), 3, seller.Inventory.GetItem(InventoryListType.Main, 30).ToBytes());
                if (!added.Success) throw new InvalidOperationException("Search listing fixture failed.");
                await f.Handler.HandleSearchByItemKey(seller.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY),
                    SearchBody(new[] { 3323, 3027, 3027 }));
                check("public item search includes the seller's own active listing", SearchHasRows(seller.Drain().Single(), added.ListingId));
                int sellerGold = seller.Inventory.GetMainVirtualCount(0).Count;
                foreach (int amount in new[] { 60, 300 })
                {
                    await f.Handler.HandleBidding(seller.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), BidBody((int)added.ListingId, amount));
                    var rejected = repository.GetListing(added.ListingId);
                    check($"visible own listing still rejects {(amount == 300 ? "buyout" : "bid")} without asset changes",
                        RejectedWithNotice(seller.Drain(), CmdPacketTypeA21.AUCTION_BIDDING, "不能购买或竞价")
                        && rejected.Status == AuctionListingStatus.Active && rejected.CurrentBid == 0
                        && rejected.CurrentBidderId == 0 && rejected.BidCount == 0 && rejected.BuyerCharacterId == 0
                        && rejected.SettleMailCount == 0 && seller.Inventory.GetMainVirtualCount(0).Count == sellerGold);
                }
                await f.Handler.HandleSearchByItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY),
                    SearchBody(new[] { 3323, 3027, 3027 }));
                check("multi-template search includes a later candidate exactly once", SearchHasRows(buyer.Drain().Single(), added.ListingId));
                await f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), BidBody((int)added.ListingId, 60));
                var bidPackets = buyer.Drain();
                check("successful bid keeps the listing visible and projects its latest committed price",
                    bidPackets.Count == 2 && Envelope(bidPackets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_BIDDING)
                    && Body(bidPackets[0]).SequenceEqual(new byte[] { 1, 0 }) && SearchHasRows(bidPackets[1], added.ListingId)
                    && I32(Body(bidPackets[1]), 17) == 60 && buyer.Inventory.GetMainVirtualCount(0).Count == 99940);
                await f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), BidBody((int)added.ListingId, 300));
                var boughtPackets = buyer.Drain();
                var bought = repository.GetListing(added.ListingId);
                check("buyout ACK is followed by the same real query with the sold row removed, without another gold deduction",
                    boughtPackets.Count == 2 && Envelope(boughtPackets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_BIDDING)
                    && Body(boughtPackets[0]).SequenceEqual(new byte[] { 1, 0 }) && SearchHasRows(boughtPackets[1])
                    && bought.Status == AuctionListingStatus.Settled && bought.BuyerCharacterId == buyer.CharacterId
                    && bought.ActualDealPrice == 300 && bought.SettleMailCount == 2
                    // The earlier 60-gold bid escrow is refunded by its durable
                    // mail receipt, while this buyout makes its own 300 payment.
                    && buyer.Inventory.GetMainVirtualCount(0).Count == 99640);
                foreach (var request in new[] { SearchBody(new[] { 3027 }, mode: 1),
                    SearchBody(new[] { AuctionRepository.GoldTemplateId }, category: 0x9CA5) })
                {
                    await f.Handler.HandleSearchByItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), request);
                    check("gold and ordinary windows cannot consume each other's category response",
                        RejectedWithNotice(buyer.Drain(), CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY, "请求字段"));
                }
            }
            using (var f = new Fixture())
            {
                var seller = await f.Peer();
                lock (seller.Lease.SyncRoot) seller.Inventory.SetMainVirtualCount(0, 2_000_000);
                if (!InventoryPersistenceService.SaveDirty(seller.Lease)) throw new InvalidOperationException("Gold search fixture did not persist.");
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterGoldListing(new AuctionActor(seller.Lease, () => true),
                    AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
                if (!added.Success) throw new InvalidOperationException("Gold search listing fixture failed.");
                await f.Handler.HandleSearchByItemKey(seller.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY),
                    GoldSearchBody(new[] { AuctionRepository.GoldTemplateId }));
                var ownSearch = seller.Drain().Single();
                check("public gold search includes the seller's own active listing in mode one",
                    SearchHasRows(ownSearch, added.ListingId) && Body(ownSearch)[1] == 1);
                await f.Handler.HandleSearchByItemKey(seller.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY),
                    SearchBody(new[] { AuctionRepository.GoldTemplateId }, mode: 1, category: 0x9CA5));
                var legacySearch = seller.Drain().Single();
                check("existing long mode-one search remains compatible with gold listings",
                    SearchHasRows(legacySearch, added.ListingId) && Body(legacySearch)[1] == 1);
                var goldBrowse = new byte[14]; goldBrowse[0] = 1;
                await f.Handler.HandleSearchByNoItemKey(seller.Session,
                    Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY), goldBrowse);
                var browseReply = seller.Drain().Single();
                check("gold window filter uses the native14-byte market body",
                    SearchHasRows(browseReply, added.ListingId) && Body(browseReply)[1] == 1);
                var actor = new AuctionActor(seller.Lease, () => true);
                var before = repository.ReadWallet(actor);
                int gold = seller.Inventory.GetMainVirtualCount(0).Count;
                await f.Handler.HandleBidding(seller.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING),
                    GoldBidBody((int)added.ListingId, 500, AuctionRepository.GoldTemplateId));
                var rejected = repository.GetListing(added.ListingId);
                var after = repository.ReadWallet(actor);
                check("visible own gold consignment cannot be purchased or change either wallet",
                    GoldRejectedWithNotice(seller.Drain(), CmdPacketTypeA21.AUCTION_BIDDING, "无法竞拍自己")
                    && rejected.Status == AuctionListingStatus.Active && rejected.BuyerCharacterId == 0
                    && rejected.SettleMailCount == 0 && seller.Inventory.GetMainVirtualCount(0).Count == gold
                    && before.Cera == after.Cera && before.TokenCera == after.TokenCera);
            }
            using (var f = new Fixture())
            {
                var seller = await f.Peer(); var buyer = await f.Peer();
                lock (seller.Lease.SyncRoot) seller.Inventory.SetItem(InventoryListType.Main, 43,
                    new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 100170173, Uid = 0x1234, SealFlag = 1 });
                if (!InventoryPersistenceService.SaveDirty(seller.Lease)) throw new InvalidOperationException("Parent category fixture did not persist.");
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterListing(new AuctionActor(seller.Lease, () => true), InventoryListType.Main,
                    43, 100170173, 100, 0, TimeSpan.FromHours(24), 1, seller.Inventory.GetItem(InventoryListType.Main, 43).ToBytes());
                if (!added.Success) throw new InvalidOperationException("Parent category listing fixture failed.");
                await f.Handler.HandleSearchByNoItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY), CategoryBody(0x2AF8));
                check("real armor parent category includes its resource-defined shoulder leaf", SearchHasRows(buyer.Drain().Single(), added.ListingId));
                await f.Handler.HandleSearchByNoItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY), CategoryBody(0x2775));
                check("weapon parent does not broaden into unrelated armor", SearchHasRows(buyer.Drain().Single()));
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterListing(new AuctionActor(peer.Lease, () => true), InventoryListType.Main,
                    30, 3027, 100, 0, TimeSpan.FromHours(24), 3, peer.Inventory.GetItem(InventoryListType.Main, 30).ToBytes());
                if (!added.Success) throw new InvalidOperationException("Cancel search fixture failed.");
                await f.Handler.HandleSearchByItemKey(peer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3027 }));
                check("seller's public browse includes its own listing before cancellation", SearchHasRows(peer.Drain().Single(), added.ListingId));
                await f.Handler.HandleRegistCancel(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_CANCEL), CancelBody((int)added.ListingId));
                var packets = peer.Drain();
                var cancelled = repository.GetListing(added.ListingId);
                check("cancel preserves the verified ACK and re-reads the original browse query",
                    packets.Count == 2 && Envelope(packets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_REGIST_CANCEL)
                    && Body(packets[0]).SequenceEqual(new byte[] { 1, 0 }) && SearchHasRows(packets[1])
                    && cancelled.Status == AuctionListingStatus.Settled && cancelled.BuyerCharacterId == 0
                    && cancelled.SettleMailCount == 1);
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer(); Task older, newer;
                await peer.SendGate.WaitAsync();
                try
                {
                    older = f.Handler.HandleSearchByItemKey(peer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3027 }));
                    newer = f.Handler.HandleSearchByItemKey(peer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3323 }));
                    check("two real searches reserve their order before transition/send queues", !older.IsCompleted && !newer.IsCompleted);
                }
                finally { peer.SendGate.Release(); }
                await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(5));
                var packets = peer.Drain();
                check("an older queued search cannot replace a later search view", packets.Count == 1 && SearchHasRows(packets[0]));
            }
            using (var f = new Fixture())
            {
                var seller = await f.Peer(); var buyer = await f.Peer();
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterListing(new AuctionActor(seller.Lease, () => true), InventoryListType.Main,
                    30, 3027, 100, 0, TimeSpan.FromHours(24), 3, seller.Inventory.GetItem(InventoryListType.Main, 30).ToBytes());
                if (!added.Success) throw new InvalidOperationException("Queued buyout fixture failed.");
                await f.Handler.HandleSearchByItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3027 }));
                buyer.Drain();
                Task purchase, newer;
                await buyer.SendGate.WaitAsync();
                try
                {
                    purchase = f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), BidBody((int)added.ListingId, 300));
                    newer = f.Handler.HandleSearchByItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3323 }));
                    var bought = repository.GetListing(added.ListingId);
                    check("a committed buyout can wait while a newer view request reserves its sequence",
                        !purchase.IsCompleted && !newer.IsCompleted && bought.Status == AuctionListingStatus.Settled
                        && bought.BuyerCharacterId == buyer.CharacterId && bought.ActualDealPrice == 300 && bought.SettleMailCount == 2);
                }
                finally { buyer.SendGate.Release(); }
                await Task.WhenAll(purchase, newer).WaitAsync(TimeSpan.FromSeconds(5));
                var packets = buyer.Drain();
                check("post-buyout refresh cannot overwrite the newer query and does not repeat its committed debit",
                    packets.Count == 2 && Envelope(packets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_BIDDING)
                    && SearchHasRows(packets[1]) && buyer.Inventory.GetMainVirtualCount(0).Count == 99700);
            }
            foreach (string identity in new[] { "UID", "run" })
            {
                using var f = new Fixture(); var seller = await f.Peer(); var buyer = await f.Peer();
                var repository = new AuctionRepository(f.Database);
                var added = repository.RegisterListing(new AuctionActor(seller.Lease, () => true), InventoryListType.Main,
                    30, 3027, 100, 0, TimeSpan.FromHours(24), 3, seller.Inventory.GetItem(InventoryListType.Main, 30).ToBytes());
                if (!added.Success) throw new InvalidOperationException("Cached source fixture failed.");
                await f.Handler.HandleSearchByItemKey(buyer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3027 }));
                buyer.Drain();
                if (identity == "UID") buyer.Session.Player.UserId++; else buyer.Session.Player.NextDungeonRunGeneration();
                await f.Handler.HandleBidding(buyer.Session, Header(CmdPacketTypeA21.AUCTION_BIDDING), BidBody((int)added.ListingId, 300));
                var packets = buyer.Drain();
                var bought = repository.GetListing(added.ListingId);
                check("a valid new mutation source cannot reuse a stale cached " + identity + " search",
                    packets.Count == 1 && Envelope(packets[0], 1, (ushort)CmdPacketTypeA21.AUCTION_BIDDING)
                    && bought.Status == AuctionListingStatus.Settled && bought.BuyerCharacterId == buyer.CharacterId
                    && bought.ActualDealPrice == 300 && bought.SettleMailCount == 2);
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer(); bool superseded = false; Task newer = null;
                AuctionHandler handler = null;
                handler = new AuctionHandler(new AuctionService(new AuctionRepository(f.Database), f.Mailbox), f.Transitions,
                    (session, prepare, guard) => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(peer.SendGate, () =>
                    {
                        var packets = prepare();
                        if (!superseded)
                        {
                            superseded = true;
                            // Reserve a new valid query after old preparation,
                            // before the real send core's per-frame guard.
                            newer = handler.HandleSearchByItemKey(session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3323 }));
                        }
                        return packets;
                    }, packet => session.Stream.WriteAsync(packet, 0, packet.Length), guard));
                await handler.HandleSearchByItemKey(peer.Session, Header(CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY), SearchBody(new[] { 3027 }));
                if (newer == null) throw new InvalidOperationException("Prepared search was not superseded.");
                await newer.WaitAsync(TimeSpan.FromSeconds(5));
                var packets = peer.Drain();
                check("a newer query between prepare and write cancels only the stale projection without escaping as a request exception",
                    superseded && packets.Count == 1 && SearchHasRows(packets[0]) && f.CountListings() == 0
                    && peer.Inventory.GetMainVirtualCount(0).Count == 100000);
            }
        }

        private static async Task CheckCrystalRegistration(Action<string, bool> check)
        {
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                lock (peer.Lease.SyncRoot) peer.Inventory.SetMainVirtualCount(358, 8);
                if (!InventoryPersistenceService.SaveDirty(peer.Lease)) throw new InvalidOperationException("Crystal fixture did not persist.");
                await f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), CrystalRegisterBody(358, 3037, 3));
                var packets = peer.Drain();
                check("wire36 crystal registration debits its existing virtual wallet without creating a container core",
                    f.CountListings() == 1 && peer.Inventory.GetMainVirtualCount(358).Count == 5
                    && peer.Inventory.GetItem(InventoryListType.Main, 358) == null
                    && peer.Inventory.GetMainVirtualCount(0).Count == 99999
                    && packets.Any(p => Envelope(p, 1, (ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM)
                        && Body(p).SequenceEqual(new byte[] { 1, 0 })));
                check("crystal registration projects Main wallet slot358 instead of a fictitious wire36 container list",
                    packets.Count == 4 && Envelope(packets[0], 0, (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST)
                    && Body(packets[0])[0] == (byte)InventoryListType.Main && U16(Body(packets[0]), 1) == 1
                    && U16(Body(packets[0]), 3) == 358
                    && !packets.Any(p => Envelope(p, 0, (ushort)NotiPacketTypeA21.ITEM_LIST)));
            }
            using (var f = new Fixture())
            {
                var peer = await f.Peer();
                lock (peer.Lease.SyncRoot) peer.Inventory.SetMainVirtualCount(358, 8);
                if (!InventoryPersistenceService.SaveDirty(peer.Lease)) throw new InvalidOperationException("Crystal CAS fixture did not persist.");
                Task queued;
                using (await f.Transitions.AcquireAsync(peer.CharacterId))
                {
                    queued = f.Handler.HandleRegistItem(peer.Session, Header(CmdPacketTypeA21.AUCTION_REGIST_ITEM), CrystalRegisterBody(358, 3037, 3));
                    check("crystal quote is frozen before waiting on the actual transition gate", !queued.IsCompleted);
                    lock (peer.Lease.SyncRoot) peer.Inventory.SetMainVirtualCount(358, 9);
                }
                await queued.WaitAsync(TimeSpan.FromSeconds(5));
                check("a changed crystal quote refuses escrow without discarding the newer wallet state",
                    RejectedWithNotice(peer.Drain(), CmdPacketTypeA21.AUCTION_REGIST_ITEM, "拍卖行")
                    && f.CountListings() == 0 && peer.Inventory.GetMainVirtualCount(358).Count == 9
                    && peer.Inventory.GetMainVirtualCount(0).Count == 100000 && peer.Inventory.GetItem(InventoryListType.Main, 358) == null);
            }
        }

        private static bool SearchHasRows(byte[] packet, params long[] listingIds)
        {
            if (!Envelope(packet, 1, (ushort)CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY)) return false;
            var body = Body(packet);
            if (body.Length < 8 || body[0] != 1 || U16(body, 6) != listingIds.Length || body.Length != 8 + 168 * listingIds.Length) return false;
            return listingIds.OrderBy(id => id).SequenceEqual(Enumerable.Range(0, listingIds.Length)
                .Select(i => (long)I32(body, 8 + 168 * i)).OrderBy(id => id));
        }

        private static bool RejectedWithNotice(IReadOnlyList<byte[]> packets, CmdPacketTypeA21 command, string text)
        {
            if (packets.Count != 2 || !Envelope(packets[0], 1, (ushort)command)
                || !Body(packets[0]).SequenceEqual(new byte[] { 0, 0x7E })
                || !NoticeContains(packets[1], text)) return false;
            return true;
        }

        private static bool GoldRejected(IReadOnlyList<byte[]> packets, CmdPacketTypeA21 command)
        {
            return packets.Count >= 1
                && Envelope(packets[0], 1, (ushort)command)
                && Body(packets[0]).SequenceEqual(command switch
                {
                    CmdPacketTypeA21.AUCTION_REGIST_ITEM => new byte[] { 0, 0x98, 0 },
                    CmdPacketTypeA21.AUCTION_REGIST_CANCEL => new byte[] { 0, 0x98, 1 },
                    CmdPacketTypeA21.AUCTION_BIDDING => new byte[] { 0, 1 },
                    _ => Array.Empty<byte>()
                });
        }

        private static bool GoldRejectedWithNotice(
            IReadOnlyList<byte[]> packets, CmdPacketTypeA21 command, string text)
            => GoldRejected(packets, command)
                && packets.Count == 2
                && NoticeContains(packets[1], text);

        private static bool NoticeContains(byte[] packet, string text)
        {
            if (!Envelope(packet, 0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE)) return false;
            var notice = Body(packet);
            if (notice.Length < 5) return false;
            int count = I32(notice, 1);
            return count > 0 && count == notice.Length - 5
                && ClientTextEncoding.Encoding.GetString(notice, 5, count).Contains(text, StringComparison.Ordinal);
        }

        private static byte[] CurrentEquipmentRegisterBody() => Convert.FromHexString(
            "00002B00BD79F80501000000FFFFFFFFA0860100A086010018000000307500307500307500");

        private static AuctionListing Listing(ItemCore core) => new()
        {
            ListingId = 33, StartingPrice = 20, BuyoutPrice = 100, ExpiresAtUnix = 1_800_000_000,
            Item = new AuctionItemSnapshot { ItemTemplateId = core.ItemId, ItemCount = core.Count, ItemCoreData = core.ToBytes() }
        };
        private static byte[] RegisterBody(int itemId, int count)
        { var b = new byte[28]; b[2] = 30; Put(b, 4, itemId); Put(b, 8, count); Put(b, 12, -1); Put(b, 16, 100); Put(b, 20, 100); Put(b, 24, 24); return b; }
        private static byte[] BidBody(int listingId, int amount)
        { var b = new byte[26]; Put(b, 1, amount); Put(b, 5, listingId); Put(b, 9, 1_800_000_000); return b; }
        private static byte[] GoldBidBody(int listingId, int amount, int goldItemId)
        {
            var b = new byte[17];
            b[0] = 1;
            Put(b, 1, amount);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5, 4), checked((uint)listingId));
            Put(b, 13, goldItemId);
            return b;
        }
        private static byte[] GoldCancelBody(long listingId, uint expiry)
        {
            var b = new byte[9];
            b[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1, 4), checked((uint)listingId));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5, 4), expiry);
            return b;
        }
        private static byte[] CancelBody(int listingId)
        { var b = new byte[9]; Put(b, 1, listingId); Put(b, 5, 1_800_000_000); return b; }
        private static byte[] SearchBody(int[] ids, byte mode = 0, ushort category = 0x9CB7)
        {
            var b = new byte[18 + 4 * ids.Length]; b[0] = mode; b[6] = 0x1F; b[7] = checked((byte)ids.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8, 2), category);
            for (int i = 0; i < ids.Length; i++) Put(b, 10 + 4 * i, ids[i]);
            b[^1] = 8; return b;
        }
        private static byte[] GoldSearchBody(int[] ids)
        {
            var b = new byte[10 + 4 * ids.Length]; b[0] = 1; b[7] = checked((byte)ids.Length);
            for (int i = 0; i < ids.Length; i++) Put(b, 10 + 4 * i, ids[i]);
            return b;
        }
        private static byte[] CategoryBody(ushort category, byte mode = 0)
        { var b = new byte[20]; b[0] = mode; BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5, 2), category); b[7] = 7; b[9] = 0x1F; b[^1] = 8; return b; }
        private static byte[] CrystalRegisterBody(short slot, int itemId, int count)
        { var b = RegisterBody(itemId, count); b[1] = (byte)InventoryListType.CrystalWarehouse; BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2, 2), slot); return b; }
        private static GamePacketHeader Header(CmdPacketTypeA21 command) => new() { cmd = 1, type = (ushort)command };
        private static bool Envelope(byte[] packet, byte command, ushort type) => packet != null && packet.Length >= 15
            && packet[0] == command && U16(packet, 1) == type && I32(packet, 3) == packet.Length;
        private static byte[] Body(byte[] packet) => packet.AsSpan(15).ToArray();
        private static int I32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(offset, 4));
        private static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(offset, 2));
        private static void Put(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset, 4), value);

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "auction-protocol-" + Guid.NewGuid().ToString("N"));
            private readonly List<Peer> _peers = new();
            internal readonly GameDatabase Database;
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly MailboxService Mailbox;
            internal readonly AuctionHandler Handler;
            internal Task Pending;
            internal Fixture()
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "test.db"), ServerPaths.SchemaFilePath);
                Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'auction-protocol','');"; cmd.ExecuteNonQuery(); });
                Mailbox = new MailboxService(new MailboxRepository(Database));
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Handler = new AuctionHandler(new AuctionService(new AuctionRepository(Database), Mailbox), Transitions);
            }
            internal async Task<Peer> Peer()
            { var peer = await AuctionProtocolSelfTest.Peer.Create(this); _peers.Add(peer); return peer; }
            internal long CountListings() => Database.Read(c => { using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM auction_listings;"; return Convert.ToInt64(cmd.ExecuteScalar()); });
            public void Dispose()
            {
                foreach (var peer in _peers) peer.Dispose();
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
            }
        }

        private sealed class Peer : IDisposable
        {
            private static int _next = 62000;
            private readonly TcpClient _reader;
            internal readonly EnhancedClientSession Session;
            internal readonly InventoryService Inventory;
            internal readonly InventoryLease Lease;
            internal readonly SemaphoreSlim SendGate;
            internal int CharacterId => Lease.CharacterId;
            private Peer(TcpClient reader, EnhancedClientSession session, InventoryService inventory, InventoryLease lease)
            {
                _reader = reader; Session = session; Inventory = inventory; Lease = lease;
                SendGate = (SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(session) ?? throw new InvalidOperationException("Actual session send lock unavailable."));
            }
            internal static async Task<Peer> Create(Fixture f)
            {
                int cid = Interlocked.Increment(ref _next);
                f.Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "INSERT INTO characters(character_id,account_id,name,level) VALUES(@cid,1,@name,50);";
                    cmd.Parameters.AddWithValue("@cid", cid); cmd.Parameters.AddWithValue("@name", "auction-peer-" + cid); cmd.ExecuteNonQuery(); });
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var reader = new TcpClient { ReceiveTimeout = 3000 }; TcpClient writer;
                try { var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync(); await connecting; }
                finally { listener.Stop(); }
                var session = new EnhancedClientSession(writer, new GamePacketHeader()) { Account = new AccountRecord { AccountId = 1 } };
                session.Player.CharacterId = cid; session.Player.UserId = checked((ushort)cid); session.Player.Level = 50;
                session.Player.UserState = 0; session.Player.TownPresenceReady = true;
                session.GameSession = new GameSession(session, f.Database, sessionDirectory: f.Sessions);
                var inventory = new InventoryService(cid, 1, f.Database);
                inventory.SetMainVirtualCount(0, 100000);
                inventory.SetItem(InventoryListType.Main, 30, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3027, Count = 8 });
                var lease = InventoryContext.Register(session.SessionId, inventory);
                InventoryPersistenceService.SaveDirty(lease);
                f.Sessions.Register(cid, session);
                return new Peer(reader, session, inventory, lease);
            }
            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new InvalidOperationException("Auction test peer closed.");
                    var header = new byte[15]; stream.ReadExactly(header); int length = I32(header, 3);
                    if (length < 15 || length > 65536) throw new InvalidOperationException("Invalid auction test frame length.");
                    var packet = new byte[length]; header.CopyTo(packet, 0); stream.ReadExactly(packet.AsSpan(15)); packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { InventoryContext.Unregister(Session.SessionId); Session.Close(); _reader.Dispose(); }
        }
    }
}
