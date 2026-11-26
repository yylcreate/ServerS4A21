using System;
using System.Linq;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction
{
    public sealed partial class AuctionRepository
    {
        internal static bool IsCrystalSource(InventoryListType listType, short slotIndex, int itemId)
            => listType == InventoryListType.Main
                && CurrencyService.IsCubeFragmentSlot(slotIndex)
                && CurrencyService.GetCubeFragmentSlot(itemId) == slotIndex;

        internal AuctionRegisterResult RegisterCrystalListing(
            AuctionActor actor, InventoryListType listType, short slotIndex, int expectedItemId,
            int buyoutPrice, int startingPrice, TimeSpan duration, int listingCount,
            byte[] expectedItemCore)
        {
            if (actor == null || !IsCrystalSource(listType, slotIndex, expectedItemId)
                || listingCount <= 0 || expectedItemCore == null || expectedItemCore.Length != ItemCore.Size
                || !ValidDuration(duration) || !ValidPrices(buyoutPrice, startingPrice, listingCount))
                return AuctionRegisterResult.Fail(AuctionError.InvalidRequest);
            AuctionRegisterResult result = null;
            var error = Execute(actor, "auction-crystal-register", (connection, transaction) =>
            {
                var inventory = actor.Lease.Inventory;
                var quote = InventoryExchangeCommitService.ReadSource(inventory, slotIndex);
                if (quote == null || quote.ItemId != expectedItemId
                    || !quote.ToBytes().SequenceEqual(expectedItemCore))
                    return AuctionError.ItemNotFound;
                if (quote.Count < listingCount
                    || !CurrencyService.TryLoadCubeFragmentCount(connection, transaction,
                        inventory.AccountId, expectedItemId, out var sharedBalance)
                    || sharedBalance < listingCount)
                    return AuctionError.InvalidRequest;
                var policy = ValidateAuctionTransfer(inventory, quote);
                if (policy != AuctionError.None) return policy;
                if (CountActiveListings(connection, transaction, actor.CharacterId, ordinaryOnly: true)
                    >= AuctionPolicy.MaxActiveListingsPerCharacter)
                    return AuctionError.ListingLimitReached;
                var fee = AuctionPolicy.CalculateListingFee(Math.Max(buyoutPrice, startingPrice));
                var gold = Gold(inventory);
                if (gold < fee) return AuctionError.InsufficientGold;

                // The quote exists only in the escrow snapshot. The six account
                // crystal balances remain virtual; no ordinary slot/core is created.
                var snapshot = BuildSnapshot(inventory, InventoryListType.Main, slotIndex, quote, listingCount);
                if (!inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, gold - fee)
                    || !inventory.TryConsumeMainItem(expectedItemId, listingCount, out var removed)
                    || !removed.Success)
                    return AuctionError.ServerBusy;
                var id = InsertListing(connection, transaction, actor.CharacterId,
                    LoadCharacterName(connection, transaction, actor.CharacterId),
                    snapshot, buyoutPrice, startingPrice, duration);
                result = new AuctionRegisterResult
                {
                    Success = true, ListingId = id, FeeGold = fee, GoldAfter = gold - fee,
                };
                return AuctionError.None;
            });
            return error == AuctionError.None ? result : AuctionRegisterResult.Fail(error);
        }
    }
}
