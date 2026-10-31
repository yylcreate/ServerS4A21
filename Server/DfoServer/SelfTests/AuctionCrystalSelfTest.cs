using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Auction;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionCrystalSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool value)
            {
                Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {name}");
                if (value) passed++; else failed++;
            }
            void Cases(string name, Action<Fixture, Action<string, bool>> run)
            {
                try
                {
                    using var fixture = new Fixture();
                    run(fixture, Check);
                }
                catch (Exception exception) { Check(name + ": " + exception, false); }
            }
            Cases("fixed crystal sources", FixedSources);
            Cases("rejected requests", Rejections);
            Cases("failed escrow", EscrowRollback);
            Cases("shared account projections", SharedAccount);
            Cases("stale successful escrow", StaleSuccessfulEscrow);
            Cases("transaction checkpoints", TransactionCheckpoints);
            Cases("stale postcommit bounds", StalePostcommitBounds);
            Cases("postcommit receipt read failure", ReceiptReadFailure);
            Cases("malformed shared crystal storage", MalformedCrystalStorage);
            Cases("failed persistence retry", PersistenceRetry);
            Cases("crystal mailbox settlement", Settlement);
            Console.WriteLine($"AUCTION_CRYSTAL: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static void FixedSources(Fixture f, Action<string, bool> check)
        {
            foreach (var itemId in Fixture.CrystalIds)
            {
                var slot = (short)CurrencyService.GetCubeFragmentSlot(itemId);
                var beforeGold = Gold(f.Seller);
                var partial = f.Register(f.Seller, slot, itemId, 4);
                var listing = f.Repository.GetListing(partial.ListingId);
                check($"crystal {itemId} partial escrow preserves the remaining account balance",
                    partial.Success && f.Balance(itemId) == 6 && Count(f.Seller, slot) == 6
                    && Gold(f.Seller) == beforeGold - 10);
                check($"crystal {itemId} quote is escrow-only and records the exact requested count",
                    listing?.Item.ItemCount == 4
                    && ItemCore.FromBytes(listing.Item.ItemCoreData)?.Count == 4
                    && listing.Item.SourceListType == (int)InventoryListType.Main
                    && listing.Item.SourceSlotIndex == slot && f.Seller.Inventory.GetItem(InventoryListType.Main, slot) == null
                    && f.Scalar($"SELECT COUNT(*) FROM character_inventory_items WHERE character_id={Fixture.SellerId} AND slot_index={slot}") == 0);
                var all = f.Register(f.Seller, slot, itemId, 6);
                check($"crystal {itemId} full escrow reaches zero without creating a normal item",
                    all.Success && f.Balance(itemId) == 0 && Count(f.Seller, slot) == 0);
                beforeGold = Gold(f.Seller);
                check($"crystal {itemId} an empty balance cannot be listed again",
                    !f.Register(f.Seller, slot, itemId, 1).Success && f.Balance(itemId) == 0
                    && Gold(f.Seller) == beforeGold);
            }
        }

        private static void Rejections(Fixture f, Action<string, bool> check)
        {
            const short slot = 358;
            var quote = f.Quote(f.Seller, slot);
            var gold = Gold(f.Seller);
            var wrong = (byte[])quote.Clone(); wrong[19] ^= 1;
            check("wrong fixed template and a forged quote do not authorize crystal escrow",
                !f.Register(f.Seller, slot, 3036, 1, quote).Success
                && !f.Register(f.Seller, slot, 3037, 1, wrong).Success);
            check("invalid quantities, total-price overflow and duration are rejected",
                !f.Register(f.Seller, slot, 3037, 0).Success
                && !f.Register(f.Seller, slot, 3037, 11).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.Main, slot,
                    3037, 400000000, 0, TimeSpan.FromHours(6), 6, quote).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.Main, slot,
                    3037, 1000, 0, TimeSpan.FromHours(7), 1, quote).Success);
            check("crystal alias cannot authorize reserved slots, souls, virtual gold or account cargo",
                !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.Main, 352,
                    3037, 1000, 0, TimeSpan.FromHours(6), 1, quote).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.Main, 360,
                    10100115, 1000, 0, TimeSpan.FromHours(6), 1, quote).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.Main, 0,
                    AuctionRepository.GoldTemplateId, 1000, 0, TimeSpan.FromHours(6), 1, quote).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.AccountCargo, slot,
                    3037, 1000, 0, TimeSpan.FromHours(6), 1, quote).Success
                && !f.Repository.RegisterCrystalListing(f.Actor(f.Seller), InventoryListType.CrystalWarehouse, slot,
                    3037, 1000, 0, TimeSpan.FromHours(6), 1, quote).Success);
            check("all rejected identity requests preserve balance, fee and listings",
                f.Balance(3037) == 10 && Count(f.Seller, slot) == 10
                && Gold(f.Seller) == gold && f.Rows("auction_listings") == 0);
            f.Seller.Inventory.SetMainVirtualCount(0, 9);
            var insufficient = f.Register(f.Seller, slot, 3037, 1);
            check("insufficient listing fee cannot debit or escrow a crystal",
                !insufficient.Success && insufficient.Error == AuctionError.InsufficientGold
                && f.Balance(3037) == 10 && Gold(f.Seller) == 9 && f.Rows("auction_listings") == 0);
            f.Seller.Inventory.SetMainVirtualCount(0, gold);
            check("fee failure remains retryable after a legitimate gold change",
                f.Register(f.Seller, slot, 3037, 1).Success && f.Balance(3037) == 9);
            var obsolete = f.Seller;
            var actor = f.Actor(obsolete);
            var obsoleteQuote = f.Quote(obsolete, slot);
            f.ReloadSeller();
            check("an obsolete owned lease cannot list the same account balance",
                !f.Repository.RegisterListing(actor, InventoryListType.Main, slot, 3037,
                    1000, 0, TimeSpan.FromHours(6), 1, obsoleteQuote).Success && f.Balance(3037) == 9);
        }

        private static void EscrowRollback(Fixture f, Action<string, bool> check)
        {
            f.Seller.Inventory.ItemStates.Upsert(ItemStateKinds.Effect, 3030,
                checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600));
            f.Sql("CREATE TRIGGER crystal_fail_listing BEFORE INSERT ON auction_listings BEGIN SELECT RAISE(ABORT,'crystal-listing-fixture'); END;");
            var beforeGold = Gold(f.Seller);
            var rejected = f.Register(f.Seller, 358, 3037, 3);
            check("listing insert failure rolls back fee and crystals while retaining earlier accepted dirty state",
                !rejected.Success && f.Balance(3037) == 10 && Count(f.Seller, 358) == 10
                && Gold(f.Seller) == beforeGold && f.Rows("auction_listings") == 0
                && f.Scalar($"SELECT COUNT(*) FROM character_item_states WHERE character_id={Fixture.SellerId}") == 1);
            f.Sql("DROP TRIGGER crystal_fail_listing;");
            var calls = 0;
            check("a lost final lifecycle fence rolls back the actual crystal mutation",
                !f.Register(f.Seller, 358, 3037, 3, actor: new AuctionActor(f.Seller, () => ++calls < 5)).Success
                && f.Balance(3037) == 10 && Gold(f.Seller) == beforeGold && f.Rows("auction_listings") == 0);
            check("failed escrow can be retried without reapplying its rolled-back debit",
                f.Register(f.Seller, 358, 3037, 3).Success && f.Balance(3037) == 7
                && Gold(f.Seller) == beforeGold - 10 && f.Rows("auction_listings") == 1);
        }

        private static void SharedAccount(Fixture f, Action<string, bool> check)
        {
            check("both roles begin with independent projections of the same account",
                f.Seller.AccountId == f.Peer.AccountId && Count(f.Seller, 358) == 10 && Count(f.Peer, 358) == 10);
            check("first role escrows five units", f.Register(f.Seller, 358, 3037, 5).Success && f.Balance(3037) == 5);
            var peerGold = Gold(f.Peer);
            var tooMany = f.Register(f.Peer, 358, 3037, 6);
            check("a stale role cannot escrow more than the durable remaining account balance",
                !tooMany.Success && f.Balance(3037) == 5 && Gold(f.Peer) == peerGold && f.Rows("auction_listings") == 1);
            check("failed stale quote restores the current role and permits a valid retry",
                Count(f.Peer, 358) == 5 && f.Register(f.Peer, 358, 3037, 1).Success
                && f.Balance(3037) == 4 && Count(f.Peer, 358) == 4);
            check("saving the other clean old projection cannot restore already escrowed crystals",
                Count(f.Seller, 358) == 5 && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 4);
            f.Seller.Inventory.SetMainVirtualCount(358, 4);
            check("old projection five minus one commits against account four and becomes three",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 3 && Count(f.Seller, 358) == 3);
            check("a second save of the same change cannot debit twice",
                InventoryPersistenceService.SaveDirty(f.Seller) && InventoryPersistenceService.SaveDirty(f.Seller)
                && f.Balance(3037) == 3 && Count(f.Seller, 358) == 3);
            f.Peer.Inventory.SetMainVirtualCount(358, 5);
            check("old peer projection four plus one increases shared three to four",
                InventoryPersistenceService.SaveDirty(f.Peer) && f.Balance(3037) == 4 && Count(f.Peer, 358) == 4);

            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 2);
                f.Peer.Inventory.SetMainVirtualCount(358, 3);
                check("two account owners in one transaction apply only their separate accepted deltas",
                    InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller)
                    && InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Peer)
                    && f.Balance(connection, transaction, 3037) == 2);
                transaction.Commit();
            }
            f.Seller.Inventory.ClearDirtyState();
            f.Peer.Inventory.ClearDirtyState();
            check("multi-owner postcommit clear and later saves cannot restore an earlier transaction stage",
                InventoryPersistenceService.SaveDirty(f.Seller) && InventoryPersistenceService.SaveDirty(f.Peer)
                && f.Balance(3037) == 2);
        }

        private static void TransactionCheckpoints(Fixture f, Action<string, bool> check)
        {
            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 9);
                var first = InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                f.Seller.Inventory.SetMainVirtualCount(358, 8);
                var second = InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                var third = InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                check("repeated saves within one transaction debit only newly accepted differences",
                    first && second && third && f.Balance(connection, transaction, 3037) == 8);
                transaction.Commit();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 7);
            check("new transaction before postcommit clear reconciles its durable receipt without replay",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 7 && Count(f.Seller, 358) == 7);
            check("audit reflects real atomic account changes once per delta",
                f.Scalar($"SELECT SUM(count_delta) FROM inventory_audit_log WHERE account_id={Fixture.AccountId} AND slot_index=358") == -3);

            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 6);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                transaction.Commit();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 5);
            f.Seller.Inventory.ClearDirtyState();
            check("postcommit clear retains a later accepted unsaved delta",
                Count(f.Seller, 358) == 5 && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358)
                && f.Balance(3037) == 6 && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 5);

            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 4);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                // Legacy rollback cleanup can restore the value and call ClearDirtyState.
                transaction.Rollback();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 5);
            f.Seller.Inventory.ClearDirtyState();
            check("rollback clear cannot adopt an uncommitted account projection",
                Count(f.Seller, 358) == 5 && f.Balance(3037) == 5
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 5);

            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 4);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                f.Seller.Inventory.ClearDirtyState();
                check("clear while the transaction is active cannot declare its receipt durable or forget its delta",
                    f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358)
                    && InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller)
                    && f.Balance(connection, transaction, 3037) == 4);
                transaction.Rollback();
            }
            // The rolled-back audit sequence value can be reused by another row.
            f.Sql("INSERT INTO inventory_audit_log(action_name) VALUES('crystal-unrelated-receipt-fixture');");
            check("a reused audit row id with another nonce cannot be mistaken for the rolled-back commit",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 4 && Count(f.Seller, 358) == 4
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 4);
        }

        private static void StalePostcommitBounds(Fixture f, Action<string, bool> check)
        {
            check("peer can consume nine real crystals while the seller retains its old ten projection",
                f.Register(f.Peer, 358, 3037, 9).Success && f.Balance(3037) == 1 && Count(f.Seller, 358) == 10);
            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 9);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                transaction.Commit();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 8);
            f.Seller.Inventory.ClearDirtyState();
            check("postcommit stale underflow keeps the new delta without throwing or losing the durable debit",
                f.Balance(3037) == 0 && Count(f.Seller, 358) == 8
                && f.Seller.Inventory.GetCrystalPersistence(358) == null
                && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            check("the later unpayable delta is rejected against the account instead of clipping units",
                !InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 0 && Count(f.Seller, 358) == 8);
            f.Peer.Inventory.SetMainVirtualCount(358, 2);
            check("a real shared credit lets the retained underflow intent retry once",
                InventoryPersistenceService.SaveDirty(f.Peer) && f.Balance(3037) == 1
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 0 && Count(f.Seller, 358) == 0
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 0);

            // Both local projections are now independent observations of zero/one.
            // A legitimate peer credit puts the shared account near the upper bound.
            f.Peer.Inventory.SetMainVirtualCount(358, int.MaxValue);
            check("a legitimate peer credit can make the seller's zero projection stale at the upper bound",
                InventoryPersistenceService.SaveDirty(f.Peer) && f.Balance(3037) == int.MaxValue - 1
                && Count(f.Seller, 358) == 0);
            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 1);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                transaction.Commit();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 2);
            f.Seller.Inventory.ClearDirtyState();
            check("postcommit stale overflow preserves the new credit separately from the already committed credit",
                f.Balance(3037) == int.MaxValue && Count(f.Seller, 358) == 2
                && f.Seller.Inventory.GetCrystalPersistence(358) == null
                && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            check("the retained overflow delta fails without forgetting its accepted unit",
                !InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == int.MaxValue && Count(f.Seller, 358) == 2);
            f.Peer.Inventory.SetMainVirtualCount(358, Count(f.Peer, 358) - 1);
            check("a real shared debit makes the preserved overflow intent payable exactly once",
                InventoryPersistenceService.SaveDirty(f.Peer) && f.Balance(3037) == int.MaxValue - 1
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == int.MaxValue
                && Count(f.Seller, 358) == int.MaxValue
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == int.MaxValue);
        }

        private static void ReceiptReadFailure(Fixture f, Action<string, bool> check)
        {
            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                f.Seller.Inventory.SetMainVirtualCount(358, 9);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                transaction.Commit();
            }
            var checkpoint = f.Seller.Inventory.GetCrystalPersistence(358);
            f.Seller.Inventory.SetMainVirtualCount(358, 8);
            // Rename only this temporary database's receipt table to inject a real
            // SQLite read error after the preceding write has already committed.
            f.Sql("ALTER TABLE inventory_audit_log RENAME TO crystal_receipt_unavailable_fixture;");
            try
            {
                f.Seller.Inventory.ClearDirtyState();
                check("a postcommit receipt-reader SQL error defers cleanup without failing the durable mutation",
                    f.Balance(3037) == 9 && Count(f.Seller, 358) == 8
                    && ReferenceEquals(checkpoint, f.Seller.Inventory.GetCrystalPersistence(358))
                    && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            }
            finally { f.Sql("ALTER TABLE crystal_receipt_unavailable_fixture RENAME TO inventory_audit_log;"); }
            check("a restored receipt reader reconciles the committed stage and applies only the later new delta",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 8 && Count(f.Seller, 358) == 8
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 8);
        }

        private static void MalformedCrystalStorage(Fixture f, Action<string, bool> check)
        {
            var auditsBefore = f.Rows("inventory_audit_log");
            f.Sql($"UPDATE accounts SET cube_clear=10.5 WHERE account_id={Fixture.AccountId};");
            using (var connection = f.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                check("a noninteger shared crystal row is not rounded into a valid quote or debit",
                    !CurrencyService.TryLoadCubeFragmentCount(connection, transaction, Fixture.AccountId, 3037, out _)
                    && !CurrencyService.TryApplyCubeFragmentDelta(connection, transaction, Fixture.AccountId, 3037, -1, out _, out _));
                transaction.Rollback();
            }
            f.Seller.Inventory.SetMainVirtualCount(358, 9);
            check("normal persistence refuses malformed crystal storage without issuing a receipt",
                !InventoryPersistenceService.SaveDirty(f.Seller) && f.Rows("inventory_audit_log") == auditsBefore
                && f.Scalar($"SELECT COUNT(*) FROM accounts WHERE account_id={Fixture.AccountId} "
                    + "AND typeof(cube_clear)='real' AND cube_clear=10.5") == 1
                && Count(f.Seller, 358) == 9
                && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            f.Sql($"UPDATE accounts SET cube_clear=10 WHERE account_id={Fixture.AccountId};");
            check("repairing the isolated malformed row allows the original delta to commit once",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 9
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 9);
        }

        private static void StaleSuccessfulEscrow(Fixture f, Action<string, bool> check)
        {
            check("first role can reduce shared ten to five while the peer still observes ten",
                f.Register(f.Seller, 358, 3037, 5).Success && f.Balance(3037) == 5 && Count(f.Peer, 358) == 10);
            var listing = f.Register(f.Peer, 358, 3037, 1);
            check("stale peer ten minus one becomes durable four rather than restoring an absolute nine",
                listing.Success && f.Balance(3037) == 4 && Count(f.Peer, 358) == 4
                && f.Repository.GetListing(listing.ListingId).Item.ItemCount == 1);
            check("clean stale owner save and consecutive peer saves do not replenish escrowed units",
                InventoryPersistenceService.SaveDirty(f.Seller) && InventoryPersistenceService.SaveDirty(f.Peer)
                && InventoryPersistenceService.SaveDirty(f.Peer) && f.Balance(3037) == 4);
        }

        private static void PersistenceRetry(Fixture f, Action<string, bool> check)
        {
            f.Seller.Inventory.SetMainVirtualCount(358, 9);
            f.Seller.Inventory.SetListParam16(InventoryListType.Main, 16);
            f.Sql($"CREATE TRIGGER crystal_fail_container BEFORE INSERT ON character_container_state WHEN NEW.character_id={Fixture.SellerId} BEGIN SELECT RAISE(ABORT,'crystal-container-fixture'); END;");
            var failed = InventoryPersistenceService.SaveDirty(f.Seller);
            check("a later SQL failure rolls back the crystal delta and keeps the original dirty baseline",
                !failed && f.Balance(3037) == 10 && Count(f.Seller, 358) == 9
                && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            f.Sql("DROP TRIGGER crystal_fail_container;");
            check("retry after later SQL failure applies the original accepted delta exactly once",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 9 && Count(f.Seller, 358) == 9
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 9);

            f.Sql("CREATE TABLE crystal_deferred_fixture (account_id INTEGER REFERENCES accounts(account_id) DEFERRABLE INITIALLY DEFERRED);");
            var commitFailed = false;
            try
            {
                using var connection = f.Database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                f.Seller.Inventory.SetMainVirtualCount(358, 8);
                InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, f.Seller);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO crystal_deferred_fixture(account_id) VALUES(999999999);";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { commitFailed = true; }
            check("an actual deferred commit failure leaves the accepted delta uncommitted and retryable",
                commitFailed && f.Balance(3037) == 9 && Count(f.Seller, 358) == 8
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 8);
            f.Sql("DROP TABLE crystal_deferred_fixture;");

            f.Sql($"UPDATE accounts SET cube_clear=0 WHERE account_id={Fixture.AccountId};");
            f.Seller.Inventory.SetMainVirtualCount(358, 7);
            check("a stale debit cannot underflow the shared account or erase its dirty intent",
                !InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 0
                && Count(f.Seller, 358) == 7 && f.Seller.Inventory.DirtyMainVirtualCountSlots.Contains((short)358));
            f.Sql($"UPDATE accounts SET cube_clear=8 WHERE account_id={Fixture.AccountId};");
            check("a previously rejected debit remains retryable once the account has funds",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 7 && Count(f.Seller, 358) == 7);
            f.Sql($"UPDATE accounts SET cube_clear={int.MaxValue} WHERE account_id={Fixture.AccountId};");
            f.Seller.Inventory.SetMainVirtualCount(358, 8);
            check("account delta overflow fails without silently clamping away accepted units",
                !InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == int.MaxValue
                && Count(f.Seller, 358) == 8);
            f.Sql($"UPDATE accounts SET cube_clear=7 WHERE account_id={Fixture.AccountId};");
            check("overflow failure can retry without duplicate grants",
                InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 8
                && InventoryPersistenceService.SaveDirty(f.Seller) && f.Balance(3037) == 8);
        }

        private static void Settlement(Fixture f, Action<string, bool> check)
        {
            var registered = f.Register(f.Seller, 358, 3037, 3);
            var cancelled = f.Repository.Cancel(registered.ListingId, f.Actor(f.Seller));
            check("cancelled crystal escrow creates exactly one system return mail",
                registered.Success && cancelled.Success && f.Service.SettleCancelled(registered.ListingId)
                && f.Service.SettleCancelled(registered.ListingId) && f.Balance(3037) == 7
                && f.Scalar($"SELECT COUNT(*) FROM mailbox_messages WHERE idempotency_key='auction:return:{registered.ListingId}'") == 1);
            var returned = f.Mailbox.LoadInbox(Fixture.SellerId, 20).Single();
            var returnId = MailboxRepository.AttachmentClaimFlag + returned.Attachments.Single().AttachmentId;
            var claim = f.Mailbox.ClaimMail(Fixture.SellerId, returnId, f.Seller);
            check("claiming returned crystals restores the account wallet rather than a material slot",
                claim.Success && f.Balance(3037) == 10 && Count(f.Seller, 358) == 10
                && f.Seller.Inventory.GetItems(InventoryListType.Main).All(x => x.Value.ItemId != 3037));
            check("repeated crystal return claims cannot grant again",
                !f.Mailbox.ClaimMail(Fixture.SellerId, returnId, f.Seller).Success && f.Balance(3037) == 10);
            var sold = f.Register(f.Seller, 354, 3033, 2);
            var buyerBefore = f.Balance(3033, Fixture.BuyerAccountId);
            check("crystal purchase settles through the existing durable listing and mailbox owners",
                sold.Success && f.Repository.Buyout(sold.ListingId, f.Actor(f.Buyer)).Success
                && f.Service.SettleSold(sold.ListingId) && f.Service.SettleSold(sold.ListingId));
            var purchase = f.Mailbox.LoadInbox(Fixture.BuyerId, 20).Single();
            var purchaseId = MailboxRepository.AttachmentClaimFlag + purchase.Attachments.Single().AttachmentId;
            check("buyer crystal claim increments the account projection once without a normal ItemCore",
                f.Mailbox.ClaimMail(Fixture.BuyerId, purchaseId, f.Buyer).Success
                && f.Balance(3033, Fixture.BuyerAccountId) == buyerBefore + 2 && Count(f.Buyer, 354) == buyerBefore + 2
                && f.Buyer.Inventory.GetItems(InventoryListType.Main).All(x => x.Value.ItemId != 3033)
                && !f.Mailbox.ClaimMail(Fixture.BuyerId, purchaseId, f.Buyer).Success
                && f.Balance(3033, Fixture.BuyerAccountId) == buyerBefore + 2);
        }

        private static int Count(InventoryLease lease, short slot) => lease.Inventory.GetMainVirtualCount(slot)?.Count ?? 0;
        private static int Gold(InventoryLease lease) => Count(lease, 0);

        private sealed class Fixture : IDisposable
        {
            internal const int SellerId = 65001, BuyerId = 65002, PeerId = 65003;
            internal const int AccountId = 65001, BuyerAccountId = 65002;
            internal static readonly int[] CrystalIds = { 3033, 3034, 3035, 3036, 3037, 3262 };
            private readonly string _path = Path.Combine(Path.GetTempPath(), "a21-auction-crystals-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly Guid[] _sessions = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            internal GameDatabase Database { get; }
            internal AuctionRepository Repository { get; }
            internal AuctionService Service { get; }
            internal MailboxService Mailbox { get; }
            internal InventoryLease Seller => Current(SellerId);
            internal InventoryLease Buyer => Current(BuyerId);
            internal InventoryLease Peer => Current(PeerId);

            internal Fixture()
            {
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash,cube_black,cube_white,cube_red,cube_blue,cube_clear,cube_gold) VALUES"
                    + $"({AccountId},'crystal-seller','',10,10,10,10,10,10),({BuyerAccountId},'crystal-buyer','',10,10,10,10,10,10);"
                    + "INSERT INTO characters(character_id,account_id,name,level) VALUES"
                    + $"({SellerId},{AccountId},'crystal-seller',85),({BuyerId},{BuyerAccountId},'crystal-buyer',85),({PeerId},{AccountId},'crystal-peer',85);");
                Load(_sessions[0], SellerId, AccountId);
                Load(_sessions[1], BuyerId, BuyerAccountId);
                Load(_sessions[2], PeerId, AccountId);
                Repository = new AuctionRepository(Database);
                Mailbox = new MailboxService(new MailboxRepository(Database));
                Service = new AuctionService(Repository, Mailbox);
            }

            private void Load(Guid session, int characterId, int accountId)
            {
                using var connection = Database.OpenConnection();
                var inventory = InventoryService.LoadFromDb(connection, characterId, accountId, Database);
                var lease = InventoryContext.Register(session, inventory);
                inventory.SetMainVirtualCount(0, 100000);
                if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Fixture gold persistence failed.");
            }

            private static InventoryLease Current(int characterId)
                => InventoryContext.TryGetLease(characterId, out var lease) ? lease
                    : throw new InvalidOperationException("Fixture lost its owned lease.");

            internal AuctionActor Actor(InventoryLease lease) => new(lease, () => true);
            internal byte[] Quote(InventoryLease lease, short slot) => InventoryExchangeCommitService.ReadSource(lease.Inventory, slot)?.ToBytes();
            internal AuctionRegisterResult Register(InventoryLease lease, short slot, int itemId, int count,
                byte[] quote = null, AuctionActor actor = null)
                => Repository.RegisterListing(actor ?? Actor(lease), InventoryListType.Main, slot, itemId,
                    1000, 0, TimeSpan.FromHours(6), count, quote ?? Quote(lease, slot));

            internal int Balance(int itemId, int accountId = AccountId)
            {
                using var connection = Database.OpenConnection();
                return Balance(connection, null, itemId, accountId);
            }

            internal int Balance(SqliteConnection connection, SqliteTransaction transaction, int itemId, int accountId = AccountId)
                => CurrencyService.TryLoadCubeFragmentCount(connection, transaction, accountId, itemId, out var count)
                    ? count : throw new InvalidOperationException("Fixture account crystal balance was missing.");

            internal void ReloadSeller()
            {
                using var connection = Database.OpenConnection();
                InventoryContext.Register(_sessions[0], InventoryService.LoadFromDb(connection, SellerId, AccountId, Database));
            }

            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery();
            });

            internal long Scalar(string sql)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }

            internal long Rows(string table) => Scalar("SELECT COUNT(*) FROM " + table);

            public void Dispose()
            {
                foreach (var session in _sessions)
                    if (!InventoryContext.Unregister(session))
                    {
                        var characterId = session == _sessions[0] ? SellerId : session == _sessions[1] ? BuyerId : PeerId;
                        if (InventoryContext.TryGetOwnedLease(session, characterId, out var lease))
                        {
                            // A failed assertion may leave an intentionally unpayable
                            // fixture delta. Recover only this temporary owned database
                            // before removing its lease and deleting its artifacts.
                            InventoryRollbackRecoveryService.ReloadOnlineInventory(Database.ConnectionString, lease);
                            InventoryContext.Unregister(session);
                        }
                    }
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
