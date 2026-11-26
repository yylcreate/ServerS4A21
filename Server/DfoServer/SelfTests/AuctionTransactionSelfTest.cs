using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionTransactionSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool value)
            {
                Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {name}");
                if (value) passed++; else failed++;
            }
            var path = Path.Combine(Path.GetTempPath(), "a21-auction-assets-" + Guid.NewGuid().ToString("N") + ".db");
            var sessions = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            try
            {
                using var f = new Fixture(path, sessions);
                RunCases(f, Check);
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                foreach (var session in sessions) InventoryContext.Unregister(session);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"AUCTION_TRANSACTION: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static void RunCases(Fixture f, Action<string, bool> check)
        {
            var seller = f.Seller;
            var buyer = f.Buyer;
            var peer = f.Peer;
            var owned = new AuctionActor(seller, () => true);
            var buy = new AuctionActor(buyer, () => true);
            var third = new AuctionActor(peer, () => true);
            check("PVF free material fixture is current", MailboxSendPolicy.ValidateAttachment(
                new MailboxSendRequest { SenderCharacterId = seller.CharacterId }, seller.Inventory.GetItem(InventoryListType.Main, 121))
                == MailboxSendError.None);
            check("resource search families do not alter persistent kinds",
                AuctionRepository.MatchesStackableCategory("[set]", "throw")
                && AuctionRepository.MatchesStackableCategory("[random reward item]", "gacha")
                && AuctionRepository.MatchesStackableCategory("[contract]", "misc")
                && AuctionRepository.MatchesStackableCategory("[waste]", "consumable")
                && !AuctionRepository.MatchesStackableCategory("[waste]", "misc")
                && !AuctionRepository.MatchesStackableCategory("[throw]", "consumable"));
            AuctionRegisterResult Register(int count, int unit = 1000, int starting = 100, AuctionActor actor = null) =>
                f.Repository.RegisterListing(actor ?? owned, InventoryListType.Main, 121, 3030,
                    unit, starting, TimeSpan.FromHours(6), count, seller.Inventory.GetItem(InventoryListType.Main, 121)?.ToBytes());
            var source = seller.Inventory.GetItem(InventoryListType.Main, 121).ToBytes();
            check("quantity, price-total overflow, and expected template are strict",
                !Register(61).Success && !Register(0).Success && !Register(6, 400000000, 0).Success
                && !f.Repository.RegisterListing(owned, InventoryListType.Main, 121, 3242,
                    1000, 0, TimeSpan.FromHours(6), 1, source).Success
                && f.Rows("auction_listings") == 0 && seller.Inventory.CountMainItem(3030) == 60);
            var wrong = (byte[])source.Clone(); wrong[19] ^= 1;
            check("same-template instance replacement is not accepted",
                !f.Repository.RegisterListing(owned, InventoryListType.Main, 121, 3030,
                    1000, 0, TimeSpan.FromHours(6), 1, wrong).Success && f.Rows("auction_listings") == 0);
            var lockCore = seller.Inventory.GetItem(InventoryListType.Main, 121).Copy();
            lockCore.EquipmentLockId = 55;
            seller.Inventory.SetItem(InventoryListType.Main, 121, lockCore);
            check("invalid equipment-lock reference fails closed", !Register(1).Success);
            lockCore.EquipmentLockId = 0;
            seller.Inventory.SetItem(InventoryListType.Main, 121, lockCore);
            seller.Inventory.ItemStates.Upsert(ItemStateKinds.Effect, 3030, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600));
            f.Sql("CREATE TRIGGER fail_auction BEFORE INSERT ON auction_listings BEGIN SELECT RAISE(ABORT,'auction-fixture'); END;");
            var before = Gold(seller);
            check("listing write failure restores assets but preserves prior dirty item state",
                !Register(3).Success && Gold(seller) == before && seller.Inventory.CountMainItem(3030) == 60
                && f.Scalar("SELECT COUNT(*) FROM character_item_states WHERE character_id=64001") == 1
                && f.Rows("auction_listings") == 0);
            f.Sql("DROP TRIGGER fail_auction;");
            var guardCalls = 0;
            check("final source fence rolls back the actual mutation",
                !Register(3, actor: new AuctionActor(seller, () => ++guardCalls < 5)).Success
                && Gold(seller) == before && seller.Inventory.CountMainItem(3030) == 60 && f.Rows("auction_listings") == 0);
            var registered = Register(3);
            var expected = ItemCore.FromBytes(source); expected.Count = 3;
            check("listing holds exact full 99-byte instance and only requested quantity",
                registered.Success && f.Repository.GetListing(registered.ListingId).Item.ItemCoreData.SequenceEqual(expected.ToBytes())
                && seller.Inventory.CountMainItem(3030) == 57 && Gold(seller) == before - 10);
            buyer.Inventory.SetMainVirtualCount(0, Gold(buyer) + 37);
            var buyerGold = Gold(buyer);
            var purchased = f.Repository.Buyout(registered.ListingId, buy);
            check("online dirty gold is the spending truth and deal price is frozen",
                purchased.Success && Gold(buyer) == buyerGold - 3000
                && f.Repository.GetListing(registered.ListingId).ActualDealPrice == 3000);
            check("duplicate buyout cannot charge again", !f.Repository.Buyout(registered.ListingId, buy).Success
                && Gold(buyer) == buyerGold - 3000);
            f.Sql("CREATE TRIGGER fail_seller_mail BEFORE INSERT ON mailbox_messages "
                + $"WHEN NEW.idempotency_key='auction:gold:{registered.ListingId}' BEGIN SELECT RAISE(ABORT,'seller-mail-fixture'); END;");
            var sellerDeferred = false;
            try
            {
                sellerDeferred = !f.Service.SettleSold(registered.ListingId);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19
                && ex.Message.Contains("seller-mail-fixture", StringComparison.Ordinal))
            {
                // The command handler observes and defers a real mailbox write
                // exception. Durable stage 1 must remain recoverable, not succeed.
                sellerDeferred = true;
            }
            check("failed second delivery stays pending after committed buyer mail",
                sellerDeferred
                && f.Repository.GetListing(registered.ListingId).Status == AuctionListingStatus.Sold
                && f.Repository.GetListing(registered.ListingId).SettleMailCount == 1
                && f.MailCount("auction:item:" + registered.ListingId) == 1);
            f.Sql("DROP TRIGGER fail_seller_mail;");
            var recomposed = new AuctionService(new AuctionRepository(f.Database), new MailboxService(new MailboxRepository(f.Database)));
            recomposed.Maintain(DateTime.UtcNow);
            check("restart-style reconciliation completes only the missing seller delivery",
                f.Repository.GetListing(registered.ListingId).Status == AuctionListingStatus.Settled
                && f.MailCount("auction:item:" + registered.ListingId) == 1
                && f.MailCount("auction:gold:" + registered.ListingId) == 1
                && f.MailGold("auction:gold:" + registered.ListingId) == 2850);
            recomposed.Maintain(DateTime.UtcNow);
            check("repeated reconciliation is idempotent", f.MailCount("auction:item:" + registered.ListingId) == 1
                && f.MailCount("auction:gold:" + registered.ListingId) == 1);

            var auction = Register(2);
            var first = f.Repository.PlaceBid(auction.ListingId, buy, 250);
            var raised = f.Repository.PlaceBid(auction.ListingId, buy, 300);
            var bids = f.Repository.LoadBidsByListing(auction.ListingId);
            check("self-raise keeps only the new bid leading and refunds old escrow ID",
                first.Success && raised.Success && raised.OutbidBidId == bids[0].BidId
                && bids.Count(x => x.Status == 0) == 1 && bids.Last().BidAmount == 300);
            f.Sql($"UPDATE auction_bids SET bid_at_unix=1700000000 WHERE listing_id={auction.ListingId};");
            var sameSecond = f.Repository.LoadMyActiveBids(buyer.CharacterId).First(x => x.ListingId == auction.ListingId);
            check("same-second bid history projects the newest actual bid deterministically",
                sameSecond.BidAmount == 300 && sameSecond.Status == 0 && sameSecond.BidId == bids.Last().BidId);
            check("untrusted refund inputs cannot mint currency",
                !f.Service.RefundOutbidGold(buyer.CharacterId, 999, auction.ListingId, bids[0].BidId));
            check("old escrow refund is durable and unique",
                f.Service.RefundOutbidGold(buyer.CharacterId, 250, auction.ListingId, bids[0].BidId)
                && f.MailGold("auction:outbid:bid:" + bids[0].BidId) == 250);
            var over = f.Repository.PlaceBid(auction.ListingId, third, 400);
            var deal = f.Repository.Buyout(auction.ListingId, buy);
            check("buyout with bids freezes buyout total rather than old highest bid",
                over.Success && deal.Success && f.Repository.GetListing(auction.ListingId).ActualDealPrice == 2000);
            f.Service.SettleSold(auction.ListingId);
            check("buyout refunds displaced escrows and pays actual total",
                f.Repository.LoadPendingOutbidRefunds(64).Count == 0
                && f.MailGold("auction:gold:" + auction.ListingId) == 1900);
            var cancel = Register(1);
            f.Repository.PlaceBid(cancel.ListingId, third, 200);
            seller.Inventory.SetMainVirtualCount(0, Gold(seller) + 123);
            var beforeCancel = Gold(seller);
            check("cancel preserves unrelated dirty inventory while making bid refund durable",
                f.Repository.Cancel(cancel.ListingId, owned).Success && Gold(seller) == beforeCancel
                && f.Repository.LoadPendingOutbidRefunds(64).Count == 1);
            f.Service.SettleCancelled(cancel.ListingId);
            check("cancellation returns item and bid once via mailbox",
                f.MailCount("auction:return:" + cancel.ListingId) == 1
                && f.Repository.LoadPendingOutbidRefunds(64).Count == 0);
            var expiring = Register(1);
            f.Repository.PlaceBid(expiring.ListingId, third, 350);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            f.Sql($"UPDATE auction_listings SET listed_at_unix={now-100},expires_at_unix={now-10} WHERE listing_id={expiring.ListingId};");
            f.Repository.MarkExpiredListings(now);
            check("expiration freezes winner directly, with no seller-return intermediate state",
                f.Repository.GetListing(expiring.ListingId).Status == AuctionListingStatus.Sold
                && f.Repository.GetListing(expiring.ListingId).BuyerCharacterId == peer.CharacterId
                && f.Repository.GetListing(expiring.ListingId).ActualDealPrice == 350
                && f.Repository.LoadBidsByListing(expiring.ListingId).Single().Status == 2);
            f.Service.Maintain(DateTime.UtcNow);
            check("bid expiration settles buyer and seller once",
                f.MailCount("auction:item:" + expiring.ListingId) == 1 && f.MailCount("auction:return:" + expiring.ListingId) == 0);

            var goldListing = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
            check("gold consignment is a complete display snapshot, never a bag gold item",
                goldListing.Success && f.Repository.GetListing(goldListing.ListingId).Item.ItemCoreData.Length == ItemCore.Size
                && f.Repository.GetListing(goldListing.ListingId).ExpiresAtUnix
                    - f.Repository.GetListing(goldListing.ListingId).ListedAtUnix == 24 * 60 * 60
                && seller.Inventory.CountMainItem(AuctionRepository.GoldTemplateId) == 0);
            var cera = f.Scalar("SELECT cera FROM accounts WHERE account_id=64002");
            buyer.Inventory.SetMainVirtualCount(0, Gold(buyer) + 41);
            var online = Gold(buyer);
            check("gold consignment charges Cera while both sale rewards await mail",
                f.Repository.Buyout(goldListing.ListingId, buy).Success && Gold(buyer) == online
                && f.Scalar("SELECT cera FROM accounts WHERE account_id=64002") == cera - 500
                && f.Scalar("SELECT cera FROM accounts WHERE account_id=64001") == 10000
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 0);
            f.Service.SettleSold(goldListing.ListingId);
            check("gold sale mails the buyer principal and seller net token voucher exactly once",
                f.MailGold("auction:goldbuy:" + goldListing.ListingId) == 1_000_000
                && f.MailItemCount("auction:goldproceeds:" + goldListing.ListingId, 2681762) == 495
                && f.Repository.GetListing(goldListing.ListingId).SettleMailCount == 2);
            var buyerMail = f.MailId("auction:goldbuy:" + goldListing.ListingId);
            var sellerMail = f.MailId("auction:goldproceeds:" + goldListing.ListingId);
            var mailbox = new MailboxRepository(f.Database);
            check("buyer gold is credited only when the purchase mail is claimed",
                mailbox.ClaimMail(buyer.CharacterId, buyerMail, buyer).Success && Gold(buyer) == online + 1_000_000);
            check("seller voucher claim credits token balance without a physical stack or Cera credit",
                mailbox.ClaimMail(seller.CharacterId, sellerMail, seller).Success
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 495
                && f.Scalar("SELECT cera FROM accounts WHERE account_id=64001") == 10000
                && seller.Inventory.CountMainItem(2681762) == 0);
            check("mail settlement and repeated claims never duplicate either sale reward",
                f.Service.SettleSold(goldListing.ListingId)
                && !mailbox.ClaimMail(buyer.CharacterId, buyerMail, buyer).Success
                && !mailbox.ClaimMail(seller.CharacterId, sellerMail, seller).Success
                && Gold(buyer) == online + 1_000_000
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 495
                && f.MailCount("auction:goldbuy:" + goldListing.ListingId) == 1
                && f.MailCount("auction:goldproceeds:" + goldListing.ListingId) == 1);

            var deferredGold = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(24));
            f.Repository.Buyout(deferredGold.ListingId, buy);
            f.Sql("CREATE TRIGGER fail_gold_proceeds BEFORE INSERT ON mailbox_messages "
                + $"WHEN NEW.idempotency_key='auction:goldproceeds:{deferredGold.ListingId}' BEGIN SELECT RAISE(ABORT,'gold-mail-fixture'); END;");
            bool goldDeferred = false;
            try { goldDeferred = !f.Service.SettleSold(deferredGold.ListingId); }
            catch (SqliteException ex) when (ex.Message.Contains("gold-mail-fixture", StringComparison.Ordinal)) { goldDeferred = true; }
            check("seller mail failure leaves a durable sold order and a single committed buyer mail",
                goldDeferred && f.Repository.GetListing(deferredGold.ListingId).Status == AuctionListingStatus.Sold
                && f.Repository.GetListing(deferredGold.ListingId).SettleMailCount == 1
                && f.MailCount("auction:goldbuy:" + deferredGold.ListingId) == 1
                && f.MailCount("auction:goldproceeds:" + deferredGold.ListingId) == 0);
            f.Sql("DROP TRIGGER fail_gold_proceeds;");
            var recoveredGold = new AuctionService(new AuctionRepository(f.Database), new MailboxService(new MailboxRepository(f.Database)));
            recoveredGold.Maintain(DateTime.UtcNow);
            recoveredGold.Maintain(DateTime.UtcNow);
            check("restart recovery completes only the missing gold-sale mail",
                f.Repository.GetListing(deferredGold.ListingId).Status == AuctionListingStatus.Settled
                && f.MailCount("auction:goldbuy:" + deferredGold.ListingId) == 1
                && f.MailCount("auction:goldproceeds:" + deferredGold.ListingId) == 1);
            var deferredClaim = f.MailId("auction:goldproceeds:" + deferredGold.ListingId);
            f.Sql("CREATE TRIGGER fail_token_claim BEFORE UPDATE OF claimed_flag ON mailbox_attachments "
                + $"WHEN OLD.message_id={deferredClaim} AND NEW.claimed_flag=1 BEGIN SELECT RAISE(ABORT,'token-claim-fixture'); END;");
            bool claimRolledBack = false;
            try { claimRolledBack = !mailbox.ClaimMail(seller.CharacterId, deferredClaim, seller).Success; }
            catch (SqliteException ex) when (ex.Message.Contains("token-claim-fixture", StringComparison.Ordinal)) { claimRolledBack = true; }
            check("mail claim failure rolls back the currency credit and claim reservation together",
                claimRolledBack && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 495
                && f.Scalar($"SELECT COUNT(*) FROM mailbox_attachments WHERE message_id={deferredClaim} AND claimed_flag=0") == 1);
            f.Sql("DROP TRIGGER fail_token_claim;");
            check("rolled-back currency mail can be claimed exactly once",
                mailbox.ClaimMail(seller.CharacterId, deferredClaim, seller).Success
                && !mailbox.ClaimMail(seller.CharacterId, deferredClaim, seller).Success
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 990);

            var overflowing = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
            f.Sql("UPDATE accounts SET token_cera=2147483647 WHERE account_id=64001;");
            int buyerCap;
            using (var connection = f.Database.OpenConnection())
                buyerCap = DfoServer.Game.Currency.CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                    connection, null, buyer.CharacterId);
            buyer.Inventory.SetMainVirtualCount(0, buyerCap);
            check("seller token cap does not destroy a mail-backed sale",
                overflowing.Success && f.Repository.Buyout(overflowing.ListingId, buy).Success
                && f.Service.SettleSold(overflowing.ListingId));
            var overflowMail = f.MailId("auction:goldproceeds:" + overflowing.ListingId);
            check("token cap rejects mail claim and leaves the full attachment available",
                !mailbox.ClaimMail(seller.CharacterId, overflowMail, seller).Success
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == int.MaxValue
                && f.Scalar($"SELECT COUNT(*) FROM mailbox_attachments WHERE message_id={overflowMail} AND claimed_flag=0") == 1);
            var cappedGoldMail = f.MailId("auction:goldbuy:" + overflowing.ListingId);
            check("buyer carry cap delays only gold mail claim, not the purchase",
                !mailbox.ClaimMail(buyer.CharacterId, cappedGoldMail, buyer).Success && Gold(buyer) == buyerCap);
            buyer.Inventory.SetMainVirtualCount(0, online);
            check("gold mail remains retryable after carry capacity is restored",
                mailbox.ClaimMail(buyer.CharacterId, cappedGoldMail, buyer).Success && Gold(buyer) == online + 1_000_000);
            f.Sql("UPDATE accounts SET token_cera=495 WHERE account_id=64001;");
            check("token claim remains retryable after capacity is restored",
                mailbox.ClaimMail(seller.CharacterId, overflowMail, seller).Success
                && f.Scalar("SELECT token_cera FROM accounts WHERE account_id=64001") == 990);
            var legacyGold = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(24));
            f.Repository.Buyout(legacyGold.ListingId, buy);
            f.Sql($"UPDATE auction_listings SET extra_json='{{}}' WHERE listing_id={legacyGold.ListingId};");
            check("unmarked legacy direct-credit sales only resume their old notification",
                f.Service.SettleSold(legacyGold.ListingId)
                && f.MailCount("auction:gold-sold:" + legacyGold.ListingId) == 1
                && f.MailCount("auction:goldbuy:" + legacyGold.ListingId) == 0
                && f.MailCount("auction:goldproceeds:" + legacyGold.ListingId) == 0);
            var cancelGold = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
            int cap;
            using (var connection = f.Database.OpenConnection())
                cap = DfoServer.Game.Currency.CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                    connection, null, seller.CharacterId);
            seller.Inventory.SetMainVirtualCount(0, cap);
            var cancelledAtCap = f.Repository.Cancel(cancelGold.ListingId, owned);
            check("gold cancellation does not require online carry capacity because return is mail-backed",
                cancelledAtCap.Success
                && Gold(seller) == cap && f.Repository.GetListing(cancelGold.ListingId).Status == AuctionListingStatus.Cancelled);
            check("gold cancellation returns the complete principal exactly once by system mail",
                f.Service.SettleCancelled(cancelGold.ListingId)
                && Gold(seller) == cap
                && f.MailCount("auction:goldreturn:" + cancelGold.ListingId) == 1
                && f.MailGold("auction:goldreturn:" + cancelGold.ListingId) == 1_000_000
                && f.Repository.GetListing(cancelGold.ListingId).Status == AuctionListingStatus.Settled
                && !f.Repository.Cancel(cancelGold.ListingId, owned).Success);
            seller.Inventory.SetMainVirtualCount(0, 2000000);
            var goldExpired = f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
            seller.Inventory.SetMainVirtualCount(0, Gold(seller) + 41);
            var dirtyAtExpiry = Gold(seller);
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            f.Sql($"UPDATE auction_listings SET listed_at_unix={now-100},expires_at_unix={now-10} WHERE listing_id={goldExpired.ListingId};");
            f.Service.Maintain(DateTime.UtcNow);
            check("gold flow-off without bids returns exact principal by mail without overwriting online dirty gold",
                goldExpired.Success && f.MailGold("auction:goldreturn:" + goldExpired.ListingId) == 1000000
                && Gold(seller) == dirtyAtExpiry
                && f.Repository.GetListing(goldExpired.ListingId).Status == AuctionListingStatus.Settled);
            f.Service.Maintain(DateTime.UtcNow);
            check("gold expiry reconciliation cannot duplicate its principal mail",
                f.MailCount("auction:goldreturn:" + goldExpired.ListingId) == 1);

            var noBid = Register(1);
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            f.Sql($"UPDATE auction_listings SET listed_at_unix={now-100},expires_at_unix={now-10} WHERE listing_id={noBid.ListingId};");
            f.Service.Maintain(DateTime.UtcNow);
            check("ordinary expiration without bids returns only the original item once",
                noBid.Success && f.MailCount("auction:return:" + noBid.ListingId) == 1
                && f.MailCount("auction:item:" + noBid.ListingId) == 0
                && f.Repository.GetListing(noBid.ListingId).Status == AuctionListingStatus.Settled);

            var old = seller;
            using (var connection = f.Database.OpenConnection())
                f.Seller = InventoryContext.Register(old.SessionId,
                    InventoryService.LoadFromDb(connection, old.CharacterId, old.AccountId, f.Database));
            check("replaced lease cannot issue further commands",
                !f.Repository.RegisterGoldListing(owned, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6)).Success);

            // Gold-market quota and ordinary-auction quota are independent.  Use
            // the repository entry point here so the test covers the same
            // category filter that production registration uses.
            var quotaActor = new AuctionActor(f.Seller, () => true);
            f.Seller.Inventory.SetMainVirtualCount(0, 7_000_000);
            var goldQuotaListings = Enumerable.Range(0, AuctionPolicy.MaxGoldListingsWithoutCoupon)
                .Select(_ => f.Repository.RegisterGoldListing(
                    quotaActor, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6)))
                .ToArray();
            var ordinaryAfterGoldQuota = f.Repository.RegisterListing(
                quotaActor, InventoryListType.Main, 121, 3030, 1000, 0,
                TimeSpan.FromHours(6), 1, f.Seller.Inventory.GetItem(InventoryListType.Main, 121)?.ToBytes());
            var goldQuotaOverflow = f.Repository.RegisterGoldListing(
                quotaActor, AuctionRepository.GoldTemplateId, 1, 500, TimeSpan.FromHours(6));
            check("gold quota is independent from ordinary listing quota",
                goldQuotaListings.All(x => x.Success)
                && ordinaryAfterGoldQuota.Success
                && !goldQuotaOverflow.Success
                && goldQuotaOverflow.Error == AuctionError.ListingLimitReached);
            CheckSharedAccount(f, check);
            CheckDetailBoundary(f, check);
        }

        private static void CheckSharedAccount(Fixture f, Action<string, bool> check)
        {
            var core = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3030, Count = 9 };
            var siblingId = 64004;
            f.Sql("INSERT INTO characters(character_id,account_id,name,level) VALUES(64004,64001,'auction-sibling',85);");
            var siblingSession = Guid.NewGuid();
            InventoryLease sibling;
            f.Seller.Inventory.AccountCargo.SelectionKey = 64;
            f.Seller.Inventory.SetItem(InventoryListType.AccountCargo, 0, core);
            InventoryPersistenceService.SaveDirty(f.Seller);
            using (var c = f.Database.OpenConnection())
                sibling = InventoryContext.Register(siblingSession, InventoryService.LoadFromDb(c, siblingId, 64001, f.Database));
            try
            {
                var before = f.Rows("auction_listings");
                var left = f.Repository.RegisterListing(new AuctionActor(f.Seller, () => true),
                    InventoryListType.AccountCargo, 0, 3030, 1000, 0, TimeSpan.FromHours(6), 9, core.ToBytes());
                var right = f.Repository.RegisterListing(new AuctionActor(sibling, () => true),
                    InventoryListType.AccountCargo, 0, 3030, 1000, 0, TimeSpan.FromHours(6), 9, core.ToBytes());
                check("unowned shared account-cargo copies cannot escrow the same item twice",
                    !left.Success && !right.Success && f.Rows("auction_listings") == before
                    && f.Seller.Inventory.GetItem(InventoryListType.AccountCargo, 0).Count == 9
                    && sibling.Inventory.GetItem(InventoryListType.AccountCargo, 0).Count == 9);
            }
            finally { InventoryContext.Unregister(siblingSession); }
        }

        private static void CheckDetailBoundary(Fixture f, Action<string, bool> check)
        {
            var lease = f.Seller;
            var core = new ItemCore { ItemKind = ItemCore.KindCreature, ItemId = 63000, CreatureUid = 99001, SealFlag = 1 };
            lease.Inventory.Cargo.Capacity = 100;
            lease.Inventory.SetItem(InventoryListType.PersonalCargo, 1, core);
            lease.Inventory.CreatureDetails.Attach(new CreatureDetail
            {
                Uid = 99001, Name = "auction-fixture", Field04 = 78, ProgressValue32 = 1234,
                FieldAfterValue32 = 3, ModeFlag = 1, TailFlag = 1
            });
            lease.Inventory.CreatureDetails.MarkDirty(99001);
            InventoryPersistenceService.SaveDirty(lease);
            var snapshot = AuctionRepository.BuildSnapshot(lease.Inventory, InventoryListType.PersonalCargo, 1, core, 1);
            check("pure escrow snapshot retains full pet core and detail without editing source",
                snapshot.ItemCoreData.SequenceEqual(core.ToBytes())
                && MailboxItemDetailCodec.BuildCreateOptions(snapshot.DetailJson)?.CreatureDetailTemplate?.Name == "auction-fixture"
                && snapshot.ItemType == 3 && lease.Inventory.CreatureDetails.GetDetail(99001) != null);
            f.Sql("CREATE TRIGGER fail_pet_remove BEFORE DELETE ON character_creatures "
                + "WHEN OLD.character_id=64001 BEGIN SELECT RAISE(ABORT,'pet-removal-fixture'); END;");
            check("unified pet deletion failure rolls back core and detail together",
                !OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "auction-pet-delete-fixture", (_, _) =>
                    InventoryDeleteService.TryConsumeFromSlot(lease.Inventory, InventoryListType.PersonalCargo, 1, 63000, 1, out _))
                && lease.Inventory.GetItem(InventoryListType.PersonalCargo, 1)?.CreatureUid == 99001
                && lease.Inventory.CreatureDetails.GetDetail(99001)?.ProgressValue32 == 1234);
            f.Sql("DROP TRIGGER fail_pet_remove;");
            check("unified pet deletion commits both core and detail in one transaction",
                OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "auction-pet-delete-fixture", (_, _) =>
                    InventoryDeleteService.TryConsumeFromSlot(lease.Inventory, InventoryListType.PersonalCargo, 1, 63000, 1, out _))
                && lease.Inventory.GetItem(InventoryListType.PersonalCargo, 1) == null
                && lease.Inventory.CreatureDetails.GetDetail(99001) == null);
        }

        private static int Gold(InventoryLease lease) => lease.Inventory.GetMainVirtualCount(0)?.Count ?? 0;

        private sealed class Fixture : IDisposable
        {
            internal readonly GameDatabase Database;
            internal readonly AuctionRepository Repository;
            internal readonly AuctionService Service;
            internal InventoryLease Seller;
            internal readonly InventoryLease Buyer;
            internal readonly InventoryLease Peer;
            internal Fixture(string path, Guid[] sessions)
            {
                Database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash,cera) VALUES"
                    + "(64001,'auction-seller','',10000),(64002,'auction-buyer','',10000),(64003,'auction-peer','',10000);"
                    + "INSERT INTO characters(character_id,account_id,name,level) VALUES"
                    + "(64001,64001,'auction-seller',85),(64002,64002,'auction-buyer',85),(64003,64003,'auction-peer',85);");
                InventoryLease Load(Guid session, int id)
                {
                    using var connection = Database.OpenConnection();
                    var inventory = InventoryService.LoadFromDb(connection, id, id, Database);
                    inventory.SetMainVirtualCount(0, 10000000);
                    return InventoryContext.Register(session, inventory);
                }
                Seller = Load(sessions[0], 64001); Buyer = Load(sessions[1], 64002); Peer = Load(sessions[2], 64003);
                Seller.Inventory.SetItem(InventoryListType.Main, 121,
                    new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3030, Count = 60, Marker16 = -1, Durability = 17 });
                InventoryPersistenceService.SaveDirty(Seller);
                InventoryPersistenceService.SaveDirty(Buyer);
                InventoryPersistenceService.SaveDirty(Peer);
                Repository = new AuctionRepository(Database);
                Service = new AuctionService(Repository, new MailboxService(new MailboxRepository(Database)));
            }
            internal void Sql(string sql) => Database.Write((c,t) =>
            { using var command=c.CreateCommand();command.Transaction=t;command.CommandText=sql;command.ExecuteNonQuery(); });
            internal long Scalar(string sql)
            { using var c=Database.OpenConnection();using var command=c.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar()); }
            internal long Rows(string table) => Scalar("SELECT COUNT(*) FROM " + table);
            internal long MailCount(string key) => Scalar("SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key='" + key + "'");
            internal long MailGold(string key) => Scalar("SELECT COALESCE(SUM(gold),0) FROM mailbox_messages WHERE idempotency_key='" + key + "'");
            internal long MailId(string key) => Scalar("SELECT message_id FROM mailbox_messages WHERE idempotency_key='" + key + "'");
            internal long MailItemCount(string key, int itemId) => Scalar(
                $"SELECT COALESCE(SUM(a.item_count),0) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.idempotency_key='{key}' AND a.item_template_id={itemId}");
            public void Dispose() { }
        }
    }
}
