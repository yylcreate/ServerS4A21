using DfoServer.Game.Inventory;
using DfoServer.Game.Currency;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using System;
using System.Threading;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// Durable listing/bid owners drive settlement. ClockService only prompts a
    /// reconciliation that is also performed at startup and legitimate accesses.
    /// Mailbox idempotency keys protect each independently committed delivery.
    /// </summary>
    public sealed class AuctionService
    {
        private readonly AuctionRepository _repository;
        private readonly MailboxService _mailbox;
        private readonly object _clockSync = new object();
        private bool _clockRegistered;
        private int _maintaining;

        public AuctionService(AuctionRepository repository, MailboxService mailbox)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        }

        public AuctionRepository Repository => _repository;

        public void RegisterClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));
            lock (_clockSync)
            {
                if (_clockRegistered)
                    return;
                _clockRegistered = true;
                clock.RegisterMinuteTick("auction:expiry", utcNow => Maintain(utcNow));
            }
            Maintain(DateTime.UtcNow);
        }

        public AuctionExpirationResult Maintain(DateTime utcNow)
        {
            var result = new AuctionExpirationResult();
            if (Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0)
                return result;
            try
            {
                result.ExpiredCount = _repository.MarkExpiredListings(
                    new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds());
                RefundPendingOutbids();
                foreach (var listing in _repository.LoadPendingSettlements(32))
                {
                    try
                    {
                        if (Settle(listing))
                            result.SettledCount++;
                        else
                            result.FailedCount++;
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        FileLogger.Log($"[Auction] settlement failed listing={listing.ListingId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                FileLogger.Log($"[Auction] reconciliation failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _maintaining, 0);
            }
            return result;
        }

        private void RefundPendingOutbids()
        {
            foreach (var bid in _repository.LoadPendingOutbidRefunds(32))
            {
                try
                {
                    RefundOutbidGold(bid.BidderCharacterId, bid.BidAmount, bid.ListingId, bid.BidId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Auction] escrow refund failed bid={bid.BidId}: {ex.Message}");
                }
            }
        }

        public bool SettleSold(long listingId)
        {
            RefundPendingOutbids();
            var listing = _repository.GetListing(listingId);
            return listing != null && (listing.Status == AuctionListingStatus.Settled
                || listing.Status == AuctionListingStatus.Sold && Settle(listing));
        }

        public bool SettleCancelled(long listingId)
        {
            RefundPendingOutbids();
            var listing = _repository.GetListing(listingId);
            return listing != null && (listing.Status == AuctionListingStatus.Settled
                || (listing.Status == AuctionListingStatus.Cancelled || listing.Status == AuctionListingStatus.Expired)
                    && Settle(listing));
        }

        public bool RefundOutbidGold(int bidderCharacterId, int amount, long listingId, long bidId = 0)
        {
            // Only a persisted displaced escrow authorizes a refund. Request/body
            // fields or a missing bid ID can never mint a new system-mail balance.
            var bid = bidId > 0 ? _repository.GetPendingRefund(bidId) : null;
            if (bid == null || bid.BidderCharacterId != bidderCharacterId
                || bid.BidAmount != amount || bid.ListingId != listingId || amount <= 0)
                return false;
            var result = _mailbox.SendSystemMail(BuildSystemMail(
                bidderCharacterId, "拍卖行竞价返还", "竞价托管金币返还。", amount, null, $"auction:outbid:bid:{bidId}"));
            if (!result.Success)
                return false;
            _repository.MarkBidRefunded(bidId);
            return true;
        }

        private bool Settle(AuctionListing listing)
        {
            if (listing.Status == AuctionListingStatus.Sold)
            {
                if (listing.BuyerCharacterId <= 0 || listing.ActualDealPrice <= 0)
                    return false;
                return AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId)
                    ? SettleGoldSold(listing) : SettleSoldListing(listing);
            }
            if (listing.Status != AuctionListingStatus.Cancelled && listing.Status != AuctionListingStatus.Expired)
                return listing.Status == AuctionListingStatus.Settled;
            if (AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId))
            {
                if (!AuctionRepository.TryGetGoldValue(listing.Item.ItemTemplateId, out var face)
                    || listing.Item.ItemCount <= 0 || (long)face * listing.Item.ItemCount > int.MaxValue)
                    return false;
                var result = _mailbox.SendSystemMail(BuildSystemMail(
                    listing.SellerCharacterId, "金币寄售返还", "未成交托管金币返还。",
                    face * listing.Item.ItemCount, null, $"auction:goldreturn:{listing.ListingId}"));
                if (!result.Success)
                    return false;
                _repository.MarkMailStage(listing.ListingId, 1, complete: true);
                return true;
            }
            var returned = _mailbox.SendSystemMail(BuildSystemMail(
                listing.SellerCharacterId, "拍卖行物品返还", "未成交托管物品返还。",
                0, listing.Item, $"auction:return:{listing.ListingId}"));
            if (!returned.Success)
                return false;
            _repository.MarkMailStage(listing.ListingId, 1, complete: true);
            return true;
        }

        private bool SettleGoldSold(AuctionListing listing)
        {
            if (!AuctionRepository.TryGetGoldValue(listing.Item.ItemTemplateId, out var face)
                || listing.Item.ItemCount <= 0 || (long)face * listing.Item.ItemCount > int.MaxValue)
                return false;
            if (!AuctionGoldDelivery.TryReadMail(listing.Item.ExtraJson, out bool mail))
                return false;
            if (mail)
            {
                if (listing.SettleMailCount == 0)
                {
                    var buyer = _mailbox.SendSystemMail(BuildSystemMail(
                        listing.BuyerCharacterId, "金币寄售购买成功", "请领取购买的金币。",
                        face * listing.Item.ItemCount, null, $"auction:goldbuy:{listing.ListingId}"));
                    if (!buyer.Success)
                        return false;
                    _repository.MarkMailStage(listing.ListingId, 1, complete: false);
                }
                var proceeds = AuctionPolicy.CalculateGoldSellerProceeds(listing.ActualDealPrice);
                var request = BuildSystemMail(
                    listing.SellerCharacterId, "金币寄售已售出",
                    $"您寄售的 {face * listing.Item.ItemCount} 金币已售出，成交价 {listing.ActualDealPrice} 点券，手续费 {listing.ActualDealPrice / 100} 点券（1%），请领取 {proceeds} 代币券。",
                    0, null, $"auction:goldproceeds:{listing.ListingId}");
                request.Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemId = CurrencyService.TokenCeraVoucherItemId,
                        ItemCount = proceeds
                    }
                };
                var seller = _mailbox.SendSystemMail(request);
                if (!seller.Success)
                    return false;
                _repository.MarkMailStage(listing.ListingId, 2, complete: true);
                return true;
            }
            // Existing unmarked sales already credited both wallets. Preserve
            // their notification key/content; never mail the principal again.
            var result = _mailbox.SendSystemMail(BuildSystemMail(
                listing.SellerCharacterId, "金币寄售已售出",
                $"您寄售的 {face * listing.Item.ItemCount} 金币已售出，成交价 {listing.ActualDealPrice} 点券，手续费 {listing.ActualDealPrice / 100} 点券（1%），实际到账 {AuctionPolicy.CalculateGoldSellerProceeds(listing.ActualDealPrice)} 点券。点券已直接存入账号，本邮件仅为成交通知。",
                0, null, $"auction:gold-sold:{listing.ListingId}"));
            if (!result.Success)
                return false;
            _repository.MarkMailStage(listing.ListingId, 1, complete: true);
            return true;
        }

        private bool SettleSoldListing(AuctionListing listing)
        {
            if (listing.SettleMailCount == 0)
            {
                var attachment = CompletedTransfer(listing.Item);
                if (attachment == null)
                    return false;
                var buyer = _mailbox.SendSystemMail(BuildSystemMail(
                    listing.BuyerCharacterId, "拍卖行购买成功", "购买的物品已到账。",
                    0, attachment, $"auction:item:{listing.ListingId}"));
                if (!buyer.Success)
                    return false;
                _repository.MarkMailStage(listing.ListingId, 1, complete: false);
            }
            var proceeds = AuctionPolicy.CalculateSellerProceeds(listing.ActualDealPrice);
            if (proceeds > 0)
            {
                var seller = _mailbox.SendSystemMail(BuildSystemMail(
                    listing.SellerCharacterId, "拍卖行出售成功", "物品成交金币已到账。",
                    proceeds, null, $"auction:gold:{listing.ListingId}"));
                if (!seller.Success)
                    return false;
            }
            _repository.MarkMailStage(listing.ListingId, 2, complete: true);
            return true;
        }

        private static AuctionItemSnapshot CompletedTransfer(AuctionItemSnapshot source)
        {
            if (source?.ItemCoreData == null || source.ItemCoreData.Length != ItemCore.Size)
                return null;
            var core = ItemCore.FromBytes(source.ItemCoreData);
            if (core == null || core.ItemId != source.ItemTemplateId)
                return null;
            if (MailboxSendPolicy.IsTradeLimitItem(ItemMetadataResolver.Resolve(core.ItemId)))
            {
                if (core.StackTradeCount == 0)
                    return null;
                core.StackTradeCount--;
            }
            return new AuctionItemSnapshot
            {
                ItemType = source.ItemType, ItemTemplateId = source.ItemTemplateId,
                ItemCount = source.ItemCount, InstanceValue = core.Value,
                Durability = core.Durability, SealFlag = core.SealFlag, OptionValue = core.AbilityNo,
                ExpireTime = core.ExpireTime, Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid, ItemCoreData = core.ToBytes(), DetailJson = source.DetailJson
            };
        }

        private static MailboxSendRequest BuildSystemMail(
            int receiver, string title, string text, int gold, AuctionItemSnapshot attachment, string key)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = receiver, SenderName = "拍卖行", ReceiverCharacterId = receiver,
                Gold = gold, Title = string.Empty, Text = title + "：" + text, MailType = 1,
                SourceProtocol = 0, Unlimited = true, IdempotencyKey = key,
                AuditActor = "auction-system", AuditReason = key,
                Attachments = attachment == null ? Array.Empty<MailboxSendAttachmentRequest>() : new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = attachment.ItemType, ItemId = attachment.ItemTemplateId,
                        ItemCount = attachment.ItemCount, InstanceValue = attachment.InstanceValue,
                        Durability = attachment.Durability, SealFlag = attachment.SealFlag,
                        OptionValue = attachment.OptionValue, ExpireTime = attachment.ExpireTime,
                        Marker16 = attachment.Marker16, PetSerialOrHandle = attachment.PetSerialOrHandle,
                        ItemCoreData = attachment.ItemCoreData, DetailJson = attachment.DetailJson
                    }
                }
            };
        }
    }
}
