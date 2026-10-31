using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class AuctionHandler
    {
        private readonly AuctionService _auction;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> _send;
        private readonly ConditionalWeakTable<EnhancedClientSession, SearchViewState> _searchViews = new();

        internal AuctionHandler(AuctionService auction, CharacterTransitionCoordinator transitions,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> send = null)
        {
            _auction = auction ?? throw new ArgumentNullException(nameof(auction));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _send = send ?? ((session, prepare, guard) => session.SendPreparedPacketBatchAsync(prepare, guard));
        }

        internal Task HandleRegistItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var frozenSource = Source.Capture(session, _transitions);
            if (frozenSource == null) return Task.CompletedTask;
            bool goldWire = body?.Length > 0 && body[0] == 1;
            AuctionRegisterRequest request = default;
            AuctionGoldRegisterRequest goldRequest = default;
            string parserReason;
            bool valid = goldWire
                ? AuctionRequestParser.GoldRegister(body, out goldRequest, out parserReason)
                : AuctionRequestParser.Register(body, out request, out parserReason);
            bool gold = goldWire || (valid && AuctionRepository.IsGoldItem(request.ItemId));
            bool crystal = valid && request.ListType == InventoryListType.Main
                && InventoryExchangeCommitService.IsCrystalSlot(request.Slot)
                && InventoryService.TryResolveMainVirtualItemId(request.Slot, out int fixedId) && fixedId == request.ItemId;
            byte[] expectedCore = null;
            if (valid && !gold)
                lock (frozenSource.Lease.SyncRoot)
                    if (frozenSource.IsCurrent())
                        expectedCore = crystal
                            ? InventoryExchangeCommitService.ReadSource(frozenSource.Lease.Inventory, request.Slot)?.ToBytes()
                            : frozenSource.Lease.Inventory.GetItem(request.ListType, request.Slot)?.ToBytes();
            return Dispatch(session, header, CmdPacketTypeA21.AUCTION_REGIST_ITEM, body, async source =>
            {
                if (!valid || (!gold && expectedCore == null))
                {
                    if (gold)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, body?.Length ?? 0,
                            valid ? "source-slot-empty" : parserReason);
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, AuctionError.InvalidRequest,
                            body?.Length ?? 0, valid ? "source-slot-empty" : parserReason);
                    return;
                }
                if (gold && !goldWire && (request.ListType != InventoryListType.Main
                    || request.Slot != InventoryService.MainVirtualCurrencySlotStart))
                { await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, body?.Length ?? 0, "gold-source-slot"); return; }
                if (!gold && request.ListType == InventoryListType.Main
                    && ((!crystal && InventoryService.IsVirtualMainSlot(request.Slot)) || InventoryService.IsReservedMainSlot(request.Slot)))
                { await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, AuctionError.ItemNotTradable, reason: "virtual-reserved-slot"); return; }
                var actor = new AuctionActor(source.Lease, source.IsCurrent);
                AuctionRegisterResult result = gold
                    ? _auction.Repository.RegisterGoldListing(actor,
                        goldWire ? goldRequest.ItemId : request.ItemId,
                        goldWire ? goldRequest.Count : request.Count,
                        goldWire ? goldRequest.BuyoutPrice : request.BuyoutPrice, AuctionPolicy.GoldListingDuration,
                        PremiumService.HasActiveAuctionCoupon(
                            _auction.Repository.ConnectionString, source.Lease.AccountId)
                            ? AuctionPolicy.MaxGoldListingsWithCoupon
                            : AuctionPolicy.MaxGoldListingsWithoutCoupon)
                    : _auction.Repository.RegisterListing(actor, request.ListType, request.Slot,
                        request.ItemId, request.BuyoutPrice, request.StartingPrice,
                        TimeSpan.FromHours(request.DurationHours), request.Count, expectedCore);
                if (!result.Success)
                {
                    if (gold)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, body?.Length ?? 0, "domain");
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_ITEM, result.Error, body?.Length ?? 0, "domain");
                    return;
                }
                source.Committed = true;
                FileLogger.Log($"[Auction] register committed cid={source.CharacterId} listing={result.ListingId} count={(goldWire ? goldRequest.Count : request.Count)}");
                await Send(source, () =>
                {
                    var packets = new List<byte[]>();
                    lock (source.Lease.SyncRoot)
                    {
                        if (!source.IsCurrent()) return Array.Empty<byte[]>();
                        if (!gold)
                        {
                            // Reuse the inventory wire owners and current lease, including the
                            // remaining stack. Never clear a partially listed slot by force.
                            packets.Add(Update(source, request.ListType, request.Slot));
                            if (!crystal) packets.Add(List(source, request.ListType));
                        }
                        packets.Add(Update(source, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart));
                    }
                    packets.Add(AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_REGIST_ITEM, gold ? (byte)1 : (byte)0));
                    packets.Add(AuctionPacketBuilder.AveragePrice(1, -1, 0, 0)); // Clear pending selected item.
                    return packets;
                });
            }, frozenSource);
        }

        internal Task HandleRegistCancel(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, body, async source =>
            {
                bool gold = AuctionRequestParser.GoldCancel(body, out long goldId);
                int ordinaryId = 0;
                bool ordinary = !gold && AuctionRequestParser.Cancel(body, out ordinaryId);
                long id = gold ? goldId : ordinaryId;
                if (!gold && !ordinary)
                {
                    if (body?.Length > 0 && body[0] == 1)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, body.Length, "cancel-body");
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, AuctionError.InvalidRequest, body?.Length ?? 0, "cancel-body");
                    return;
                }
                var existing = _auction.Repository.GetListing(id);
                bool existingIsGold = existing != null
                    && AuctionRepository.IsGoldItem(existing.Item.ItemTemplateId);
                if (existing == null || existingIsGold != gold)
                {
                    if (gold || existingIsGold)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, body?.Length ?? 0, "cancel-market");
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, AuctionError.InvalidRequest, body?.Length ?? 0, "cancel-market");
                    return;
                }
                var result = _auction.Repository.Cancel(id, new AuctionActor(source.Lease, source.IsCurrent));
                if (!result.Success)
                {
                    if (gold)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, body?.Length ?? 0, "domain");
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_REGIST_CANCEL, result.Error, body?.Length ?? 0, "domain");
                    return;
                }
                source.Committed = true;
                // Settlement consumes an already committed durable listing. A stale source
                // suppresses only its UI; it must not leave the listing's delivery unfinished.
                ContinueSettlement(() => _auction.SettleCancelled(id), id);
                var view = CurrentSearchView(source);
                await Send(source, () =>
                {
                    var packets = new List<byte[]>();
                    // Gold cancellation is returned through the durable system
                    // mail settlement, not by mutating the online wallet in the
                    // request projection.  Do not send a fictitious slot update.
                    // Ordinary item cancellation continues to use its existing
                    // mail/refresh path.
                    packets.Add(AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_REGIST_CANCEL,
                        result.IsGold ? (byte)1 : (byte)0));
                    return packets;
                });
                await RefreshSearchView(source, view);
            });

        internal Task HandleBidding(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            bool gold = AuctionRequestParser.GoldBid(body, out var goldRequest);
            AuctionBidRequest request = default;
            bool ordinary = !gold && AuctionRequestParser.Bid(body, out request);
            return Dispatch(session, header, CmdPacketTypeA21.AUCTION_BIDDING, body, async source =>
            {
                if (!gold && !ordinary)
                {
                    if (body?.Length > 0 && body[0] == 1)
                        await FailGold(source, CmdPacketTypeA21.AUCTION_BIDDING, body.Length, "bid-body");
                    else
                        await Fail(source, CmdPacketTypeA21.AUCTION_BIDDING, AuctionError.InvalidRequest, body?.Length ?? 0, "bid-body");
                    return;
                }

                if (gold)
                {
                    var listing = _auction.Repository.GetListing(goldRequest.ListingId);
                    if (listing == null)
                    { await FailGold(source, CmdPacketTypeA21.AUCTION_BIDDING, body?.Length ?? 0, "listing-not-found"); return; }
                    if (!AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId)
                        || listing.Item.ItemTemplateId != goldRequest.GoldItemId
                        || listing.BuyoutPrice != goldRequest.Amount)
                    { await FailGold(source, CmdPacketTypeA21.AUCTION_BIDDING, body?.Length ?? 0, "gold-identity"); return; }

                    var actor = new AuctionActor(source.Lease, source.IsCurrent);
                    var result = _auction.Repository.Buyout(goldRequest.ListingId, actor);
                    if (!result.Success)
                    { await FailGold(source, CmdPacketTypeA21.AUCTION_BIDDING, body?.Length ?? 0, result.Error.ToString()); return; }
                    source.Committed = true;
                    ContinueSettlement(() => _auction.SettleSold(goldRequest.ListingId), goldRequest.ListingId);
                    var view = CurrentSearchView(source);
                    await Send(source, () =>
                    {
                        var packets = new List<byte[]>();
                        packets.Add(AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_BIDDING, 1));
                        var wallet = _auction.Repository.ReadWallet(actor);
                        if (wallet == null) return Array.Empty<byte[]>();
                        packets.Add(GamePacketEnvelopeBuilder.Build(0,
                            (ushort)NotiPacketTypeA21.CERA,
                            CeraUpdateBuilder.Build(wallet.Cera, wallet.TokenCera, wallet.HappyTokenCera)));
                        return packets;
                    });
                    await RefreshSearchView(source, view);
                    return;
                }

                var ordinaryListing = _auction.Repository.GetListing(request.ListingId);
                if (ordinaryListing == null)
                { await Fail(source, CmdPacketTypeA21.AUCTION_BIDDING, AuctionError.ListingNotFound); return; }
                if (AuctionRepository.IsGoldItem(ordinaryListing.Item.ItemTemplateId))
                {
                    // A gold row must be addressed through the market=1 body.
                    // Never let the ordinary bid layout reach the gold purchase
                    // owner, even if the listing id happens to be valid.
                    await FailGold(source, CmdPacketTypeA21.AUCTION_BIDDING, body?.Length ?? 0,
                        "gold-market-body-required");
                    return;
                }
                long total = (long)ordinaryListing.BuyoutPrice * ordinaryListing.Item.ItemCount;
                var ordinaryActor = new AuctionActor(source.Lease, source.IsCurrent);
                if (ordinaryListing.BuyoutPrice > 0 && request.Amount >= total)
                {
                    var result = _auction.Repository.Buyout(request.ListingId, ordinaryActor);
                    if (!result.Success)
                    { await Fail(source, CmdPacketTypeA21.AUCTION_BIDDING, result.Error); return; }
                    source.Committed = true;
                    ContinueSettlement(() => _auction.SettleSold(request.ListingId), request.ListingId);
                    var view = CurrentSearchView(source);
                    // A21's success callback deducts displayed gold optimistically. The
                    // transaction already updated our owned lease: do not send another deduction.
                    await Send(source, () => new[]
                    {
                        AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_BIDDING, 0)
                    });
                    await RefreshSearchView(source, view);
                }
                else
                {
                    var result = _auction.Repository.PlaceBid(request.ListingId, ordinaryActor, request.Amount);
                    if (!result.Success)
                    { await Fail(source, CmdPacketTypeA21.AUCTION_BIDDING, result.Error); return; }
                    source.Committed = true;
                    var view = CurrentSearchView(source);
                    if (result.OutbidBidId > 0)
                        ContinueSettlement(() =>
                        {
                            if (!_auction.RefundOutbidGold(result.OutbidCharacterId,
                                result.OutbidAmount, request.ListingId, result.OutbidBidId)) return false;
                            _auction.Repository.MarkBidRefunded(result.OutbidBidId);
                            return true;
                        }, request.ListingId);
                    await Send(source, () => new[] { AuctionPacketBuilder.Success(CmdPacketTypeA21.AUCTION_BIDDING, 0) });
                    await RefreshSearchView(source, view);
                }
            });
        }

        internal Task HandleAskAveragePrice(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE, body, async source =>
            {
                if (!AuctionRequestParser.AveragePrice(body, out int itemId, out bool goldRequest)
                    || (goldRequest && !AuctionRepository.IsGoldItem(itemId)))
                { await Fail(source, CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE, AuctionError.InvalidRequest, body?.Length ?? 0, "average-body"); return; }
                await Send(source, () =>
                {
                    bool gold = goldRequest || AuctionRepository.IsGoldItem(itemId);
                    var listings = gold ? _auction.Repository.SearchActiveGoldListings(itemId, 64, 0, source.CharacterId)
                        : _auction.Repository.SearchActiveListings(itemId, 64, 0, source.CharacterId);
                    var priced = listings.Where(l => l.BuyoutPrice > 0).ToArray();
                    int minimum = priced.Select(l => l.BuyoutPrice).DefaultIfEmpty(0).Min();
                    if (goldRequest)
                    {
                        // The native gold-market opener sends the six-byte
                        // request and consumes one mode=1 58-byte reply.
                        // Do not prepend the ordinary mode=0 projection.
                        return new[] { AuctionPacketBuilder.AveragePrice(1, minimum, 0, 0) };
                    }
                    // Source ResolveKeyAndUnused explicitly keeps the ordinary
                    // [panel+0x1a4] Ready/reference field positive (01455900).
                    // With no market observations,1 is only our minimal Ready
                    // compatibility value; all actual price/count vectors stay0.
                    // Gold's documented no-reference branch deliberately forces0.
                    int readyReference = minimum > 0 ? minimum : gold ? 0 : 1;
                    // No historical-price ledger exists. Publish current reference data only,
                    // not a fabricated average, and retain the source's mode0 -> mode1 order.
                    return new[] { AuctionPacketBuilder.AveragePrice(0, readyReference, minimum, priced.Length),
                        AuctionPacketBuilder.AveragePrice(1, readyReference, minimum, priced.Length) };
                });
            });

        internal Task HandleSearchByItemKey(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleSearch(session, header, body, true);
        internal Task HandleSearchByNoItemKey(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleSearch(session, header, body, false);
        private Task HandleSearch(EnhancedClientSession session, GamePacketHeader header, byte[] body, bool byItem)
        {
            var command = byItem ? CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY : CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY;
            if (header.cmd != 1 || header.type != (ushort)command) return Task.CompletedTask;
            var frozenSource = Source.Capture(session, _transitions);
            if (frozenSource == null) return Task.CompletedTask;
            bool valid = AuctionRequestParser.Search(body, byItem, out var request);
            var state = _searchViews.GetValue(session, _ => new SearchViewState());
            // Reserve the request order before transition/send queues. A later
            // valid search makes older queued replies and refreshes obsolete.
            bool validMode = valid && (request.GoldMarket
                ? request.Mode == 1
                : ((request.Category is >= 0x9CA5 and <= 0x9CB5) == (request.Mode == 1)));
            var view = validMode ? state.Begin(frozenSource, command, request) : null;
            return Dispatch(session, header, command, body, async source =>
            {
                if (!valid)
                { await Fail(source, command, AuctionError.InvalidRequest, body?.Length ?? 0, "search-body"); return; }
                if (!validMode)
                { await Fail(source, command, AuctionError.InvalidRequest, reason: "search-mode-category"); return; }
                await SendSearch(source, state, view, publish: true);
            }, frozenSource);
        }

        private byte[] BuildSearchView(Source source, SearchView view)
        {
            var request = view.Request;
            IReadOnlyList<AuctionListing> listings;
            // Public visibility includes the seller's own active listings.
            // Purchase and bid ownership remain enforced by the repository.
            if (request.Mode == 1)
                listings = _auction.Repository.SearchActiveGoldListings(request.ItemIds, 64, 0, 0);
            else
            {
                var f = ResolveCategoryFilter(request.Category);
                listings = f.group == UnmappedCategoryGroup ? Array.Empty<AuctionListing>()
                    : _auction.Repository.SearchActiveListings(request.ItemIds, 64, 0, 0,
                        f.kind, f.group, f.groupPrefix, f.filterJob, f.filterEquipType, f.filterExpertType,
                        StackableCategory(request.Category));
            }
            return AuctionPacketBuilder.Search(view.Command, request.Mode, listings, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        private SearchView CurrentSearchView(Source source)
        {
            if (!_searchViews.TryGetValue(source.Session, out var state)) return null;
            return state.CurrentPublished();
        }

        private Task RefreshSearchView(Source source, SearchView view)
        {
            if (view == null || !_searchViews.TryGetValue(source.Session, out var state)) return Task.CompletedTask;
            return SendSearch(source, state, view, publish: false);
        }

        private async Task SendSearch(Source source, SearchViewState state, SearchView view, bool publish)
        {
            bool Current() => source.IsCurrent() && view.Source.IsCurrent() && state.IsLatest(view);
            try
            {
                await _send(source.Session, () =>
                {
                    if (!Current()) return Array.Empty<byte[]>();
                    var packet = BuildSearchView(source, view);
                    if (publish && !state.Publish(view)) return Array.Empty<byte[]>();
                    return new[] { packet };
                }, Current);
            }
            catch (OperationCanceledException) when (!Current())
            {
                // A newer query may supersede this projection between prepare
                // and the actual frame write while the asset source stays valid.
            }
        }

        // Query projection only: no prices, balances or asset permissions live
        // here. The weak session key and frozen source bound its lifetime.
        private sealed record SearchView(Source Source, CmdPacketTypeA21 Command, AuctionSearchRequest Request, long Version);
        private sealed class SearchViewState
        {
            private long _version;
            private SearchView _published;
            internal SearchView Begin(Source source, CmdPacketTypeA21 command, AuctionSearchRequest request)
            {
                lock (this) return new SearchView(source, command, request, ++_version);
            }
            internal bool IsLatest(SearchView view)
            {
                lock (this) return view != null && view.Version == _version;
            }
            internal bool Publish(SearchView view)
            {
                lock (this)
                {
                    if (!IsLatest(view) || !view.Source.IsCurrent()) return false;
                    _published = view;
                    return true;
                }
            }
            internal SearchView CurrentPublished()
            {
                lock (this) return _published != null && IsLatest(_published) && _published.Source.IsCurrent() ? _published : null;
            }
        }

        internal Task HandleMyRegistedItemInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO, body, async source =>
            {
                if (!AuctionRequestParser.List(body, out byte mode))
                { await Fail(source, CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO, AuctionError.InvalidRequest, body?.Length ?? 0, "list-mode"); return; }
                await Send(source, () =>
                {
                    var listings = _auction.Repository.LoadListingsBySeller(source.CharacterId, AuctionListingStatus.Active)
                        .Where(l => AuctionRepository.IsGoldItem(l.Item.ItemTemplateId) == (mode == 1)).ToArray();
                    return new[] { AuctionPacketBuilder.Seller(mode, listings, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) };
                });
            });

        internal Task HandleMyBiddingInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO, body, async source =>
            {
                if (!AuctionRequestParser.List(body, out byte mode))
                { await Fail(source, CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO, AuctionError.InvalidRequest, body?.Length ?? 0, "list-mode"); return; }
                await Send(source, () =>
                {
                    var entries = new List<(AuctionListing Listing, AuctionBidRecord Bid)>();
                    var seen = new HashSet<long>();
                    foreach (var bid in _auction.Repository.LoadMyActiveBids(source.CharacterId))
                    {
                        if (bid.Status is < 0 or > 1 || !seen.Add(bid.ListingId)) continue;
                        var listing = _auction.Repository.GetListing(bid.ListingId);
                        if (listing?.Status == AuctionListingStatus.Active
                            && AuctionRepository.IsGoldItem(listing.Item.ItemTemplateId) == (mode == 1))
                            entries.Add((listing, bid));
                    }
                    return new[] { AuctionPacketBuilder.Bids(mode, entries, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) };
                });
            });

        // Source admits these branches are not verified. Never borrow another
        // list format or interpret a guessed listing-id offset to spend currency.
        internal Task HandleMyAuctionHistory(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY, body,
                source => Fail(source, CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY, AuctionError.InvalidRequest, body?.Length ?? 0, "unverified-history"));
        internal Task HandleBuyItemApiece(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => Dispatch(session, header, CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, body,
                source => Fail(source, CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE, AuctionError.InvalidRequest, body?.Length ?? 0, "unverified-apiece"));

        private async Task Dispatch(EnhancedClientSession session, GamePacketHeader header,
            CmdPacketTypeA21 command, byte[] body, Func<Source, Task> action, Source frozenSource = null)
        {
            if (header.cmd != 1 || header.type != (ushort)command) return;
            var source = frozenSource ?? Source.Capture(session, _transitions);
            if (source == null)
            {
                // Gold-market requests have a dedicated native failure body.
                // Do not silently drop a stale/invalid-session retry: the
                // client otherwise keeps the old listing row enabled and may
                // submit the same sold/self listing repeatedly.
                if (IsGoldWire(command, body))
                    await SendGoldFailureDirect(session, command, body?.Length ?? 0, "source-capture");
                return;
            }
            source.BodyLength = body?.Length ?? 0;
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!source.IsCurrent()) return;
                try
                {
                    if (!SessionDirectory.IsTownPresence(session.Player) || session.Player.DungeonSelectionPending
                        || session.Player.CurrentDungeonSelection != null)
                    {
                        if (IsGoldWire(command, body))
                            await FailGold(source, command, body?.Length ?? 0, "town-admission");
                        else
                            await Fail(source, command, AuctionError.InvalidRequest, reason: "town-admission");
                        return;
                    }
                    _auction.Maintain(DateTime.UtcNow);
                    await action(source);
                }
                catch (OperationCanceledException) when (!source.IsCurrent()) { }
                catch (Exception exception) when (exception is SqliteException or InvalidOperationException
                    or InvalidDataException or OverflowException or ArgumentOutOfRangeException)
                {
                    FileLogger.Log($"[Auction] request failed cid={source.CharacterId} command={command} error={exception.Message}");
                    // Do not retry a mutation. Any committed result remains owned by the
                    // durable repository/mail settlement; this is only a failure projection.
                    if (!source.Committed)
                    {
                        if (IsGoldWire(command, body))
                            await FailGold(source, command, body?.Length ?? 0, "request-exception");
                        else
                            await Fail(source, command, AuctionError.ServerBusy, reason: "request-exception");
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
                { FileLogger.Log($"[Auction] transport failed cid={source.CharacterId} command={command} error={exception.Message}"); }
            });
        }

        private static bool IsGoldWire(CmdPacketTypeA21 command, byte[] body)
            => body != null && body.Length > 0 && body[0] == 1
                && command is CmdPacketTypeA21.AUCTION_REGIST_ITEM
                    or CmdPacketTypeA21.AUCTION_REGIST_CANCEL
                    or CmdPacketTypeA21.AUCTION_BIDDING;

        private static Task SendGoldFailureDirect(
            EnhancedClientSession session, CmdPacketTypeA21 command, int bodyLength, string reason)
        {
            FileLogger.Log($"[Auction] rejected gold-market before source command={command} bodyLength={bodyLength} reason={reason}");
            if (session == null)
                return Task.CompletedTask;
            return session.SendPacketAsync(AuctionPacketBuilder.GoldFailure(command));
        }

        private Task Fail(Source source, CmdPacketTypeA21 command, AuctionError error = AuctionError.InvalidRequest,
            int bodyLength = -1, string reason = "domain")
        {
            if (bodyLength < 0) bodyLength = source.BodyLength;
            FileLogger.Log($"[Auction] rejected cid={source.CharacterId} command={command} error={error} bodyLength={bodyLength} reason={reason}");
            if (command == CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY)
                command = CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY;
            return Send(source, () =>
            {
                var notice = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.Build(FailureMessage(error, reason)));
                // No BE client consumer is verified: publish an existing notice
                // without inventing a command reply or changing any asset.
                return command == CmdPacketTypeA21.AUCTION_MY_AUCTION_HISTORY
                    ? new[] { notice } : new[] { AuctionPacketBuilder.Failure(command), notice };
            });
        }

        private Task FailGold(Source source, CmdPacketTypeA21 command,
            int bodyLength, string reason)
        {
            FileLogger.Log($"[Auction] rejected gold-market cid={source.CharacterId} command={command} " +
                $"bodyLength={bodyLength} reason={reason}");
            return Send(source, () =>
            {
                var packets = new List<byte[]> { AuctionPacketBuilder.GoldFailure(command) };
                var notice = GoldFailureNotice(reason);
                if (notice != null)
                    packets.Add(GamePacketEnvelopeBuilder.Build(
                        0,
                        (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                        ServerNoticeMessageBuilder.Build(notice)));
                return packets.ToArray();
            });
        }

        private static string GoldFailureNotice(string reason)
            => reason switch
            {
                // PVF dstr.dat 60121: 无法竞拍自己上架的物品
                "OwnListing" => "无法竞拍自己上架的物品",
                // PVF dstr.dat 46036: 因物品已售完， 购买失败
                "ListingNotActive" => "因物品已售完， 购买失败",
                // PVF dstr.dat 46037: 不存在的物品
                "listing-not-found" => "不存在的物品",
                // PVF dstr.dat 46034: 不符合购买条件
                "gold-identity" => "不符合购买条件",
                _ => null
            };

        private static string FailureMessage(AuctionError error, string reason)
            => reason.StartsWith("unverified-", StringComparison.Ordinal) ? "拍卖行：此操作暂未适配，未扣除物品或货币。"
                : error switch
                {
                    AuctionError.ItemNotTradable => "拍卖行：物品绑定、锁定、交易限制或该来源暂不支持，无法上架。",
                    AuctionError.InsufficientGold => "拍卖行：金币或点券不足，操作未提交。",
                    AuctionError.ItemNotFound or AuctionError.InventorySlotEmpty => "拍卖行：来源槽中的物品已变化，请重新放入物品。",
                    AuctionError.PriceOutOfRange => "拍卖行：价格或总额超出允许范围，请重新输入。",
                    AuctionError.ListingLimitReached => "拍卖行：当前上架数量已达到上限。",
                    AuctionError.GoldLimitExceeded => "拍卖行：金币或点券余额达到上限，操作未提交。",
                    AuctionError.ListingNotFound or AuctionError.ListingNotActive => "拍卖行：该拍卖品已成交、下架或到期，请刷新列表。",
                    AuctionError.OwnListing => "拍卖行：不能购买或竞价自己的拍卖品。",
                    AuctionError.BidTooLow => "拍卖行：出价未达到当前最低要求，请刷新后重新输入。",
                    AuctionError.ServerBusy => "拍卖行：当前无法提交操作，请稍后重试。",
                    _ => "拍卖行：请求字段、数量或来源无效，请重新操作。"
                };
        private static void ContinueSettlement(Func<bool> settle, long listingId)
        {
            try
            {
                if (!settle()) FileLogger.Log($"[Auction] committed listing awaits settlement retry listing={listingId}");
            }
            catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidDataException)
            {
                // The transaction already froze the asset owner. The existing
                // maintenance path retries this same durable listing/receipt.
                FileLogger.Log($"[Auction] committed settlement deferred listing={listingId} error={exception.Message}");
            }
        }
        private Task Send(Source source, Func<IReadOnlyList<byte[]>> prepare)
            => _send(source.Session, () => source.IsCurrent() ? prepare() : Array.Empty<byte[]>(), source.IsCurrent);
        private static byte[] Update(Source source, InventoryListType list, short slot)
            => GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST,
                ItemListUpdateBuilder.BuildItemSpaceUpdateBody(source.Lease.Inventory, list, new[] { slot }));
        private static byte[] List(Source source, InventoryListType list)
            => GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ITEM_LIST,
                ItemListPacketBuilder.BuildItemSpaceListBody(source.Lease.Inventory, list));
        private static string StackableCategory(ushort category) => category switch
        { 0x32C9 => "consumable", 0x32CB => "throw", 0x32CC => "gacha", 0x32CE => "misc", _ => null };

        private sealed record Source(EnhancedClientSession Session, InventoryLease Lease, int CharacterId, Func<bool> IsCurrent)
        {
            internal bool Committed { get; set; }
            internal int BodyLength { get; set; }
            internal static Source Capture(EnhancedClientSession session, CharacterTransitionCoordinator transitions)
            {
                var player = session?.Player; var account = session?.Account; var game = session?.GameSession;
                if (player == null || account == null || game == null || player.CharacterId <= 0 || player.UserId == 0
                    || account.AccountId <= 0 || !InventoryContext.TryGetOwnedLease(session.SessionId, player.CharacterId, out var lease)
                    || lease.AccountId != account.AccountId) return null;
                int cid = player.CharacterId, aid = account.AccountId; ushort uid = player.UserId;
                long run = player.CurrentDungeonRunGeneration, selection = player.CurrentDungeonSelectionGeneration;
                long leaseVersion = lease.Version;
                byte userState = player.UserState; bool townPresence = player.TownPresenceReady;
                bool Current() => ReferenceEquals(session.Player, player) && ReferenceEquals(session.Account, account)
                    && ReferenceEquals(session.GameSession, game) && player.CharacterId == cid && player.UserId == uid
                    && account.AccountId == aid && player.CurrentDungeonRunGeneration == run
                    && player.CurrentDungeonSelectionGeneration == selection && lease.Version == leaseVersion
                    && player.UserState == userState && player.TownPresenceReady == townPresence
                    && InventoryContext.IsCurrentLease(lease, session.SessionId, cid) && transitions.IsCurrent(session);
                return Current() ? new Source(session, lease, cid, Current) : null;
            }
        }
    }
}
