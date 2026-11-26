using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class AuctionSearchSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool value)
            {
                Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {label}");
                if (value) passed++; else failed++;
            }
            try
            {
                using var fixture = new Fixture();
                CheckCaptures(fixture, Check);
                CheckPagination(fixture, Check);
                CheckGoldIsolation(fixture, Check);
                CheckResourceFilters(fixture, Check);
            }
            catch (Exception exception)
            {
                Check("auction search fixture: " + exception, false);
            }
            Console.WriteLine($"AUCTION_SEARCH passed={passed} failed={failed}");
            return failed == 0 ? 0 : 1;
        }

        private static void CheckCaptures(Fixture fixture, Action<string, bool> check)
        {
            // Current A21 requests: a text search sends all local matching item
            // keys. A later candidate has both the viewer's and another seller's
            // listings in each isolated case.
            var captures = new[]
            {
                (Hex: "0000000000001F03B79CFB0C000028790000DE8500000000000000000008", Count: 3),
                (Hex: "0000000000001F04B79CAB17FB05A017FB05C5B60600C6B606000000000000000008", Count: 4),
                (Hex: "0000000000001F07B79C670200007AD327007BD327007CD3270080D3270000E928004B9A05060000000000000008", Count: 7)
            };
            foreach (var capture in captures)
            {
                fixture.Clear();
                var body = Convert.FromHexString(capture.Hex);
                bool parsed = AuctionRequestParser.Search(body, true, out var request);
                check($"real {body.Length}B search preserves all {capture.Count} keys",
                    parsed && request.Mode == 0 && request.Category == 0x9CB7
                    && request.ItemIds.Count == capture.Count);
                if (!parsed) continue;
                int matching = request.ItemIds[^1];
                long expected = fixture.Add(matching, ItemCore.KindMaterial, 20, 7);
                long own = fixture.Add(matching, ItemCore.KindMaterial, 20, 3, seller: Fixture.Viewer);
                fixture.Add(3030, ItemCore.KindMaterial, 1);
                var rows = fixture.Repository.SearchActiveListings(request.ItemIds, 64, 0);
                check($"real {body.Length}B public search includes own and other non-first candidate listings",
                    rows.Count == 2 && rows[0].ListingId == expected && rows[1].ListingId == own
                    && rows.All(row => row.Item.ItemTemplateId == matching)
                    && rows[0].Item.ItemCount == 7 && rows[1].Item.ItemCount == 3);
                var duplicateKeys = new[] { matching, matching, request.ItemIds[0], matching };
                check($"real {body.Length}B duplicate keys do not duplicate listing rows",
                    fixture.Repository.SearchActiveListings(duplicateKeys, 64, 0)
                        .Select(row => row.ListingId).SequenceEqual(new[] { expected, own }));
            }
        }

        private static void CheckPagination(Fixture fixture, Action<string, bool> check)
        {
            fixture.Clear();
            var expected = new List<long>();
            for (int i = 0; i < 130; i++)
                expected.Add(fixture.Add(i % 2 == 0 ? 3030 : 3031, ItemCore.KindMaterial, 100));
            fixture.Add(3030, ItemCore.KindMaterial, 1, seller: Fixture.Viewer);
            fixture.Add(3031, ItemCore.KindMaterial, 1, status: AuctionListingStatus.Sold);
            var keys = new[] { 3031, 3030, 3031 };
            var first = fixture.Repository.SearchActiveListings(keys, 64, 0, Fixture.Viewer);
            var second = fixture.Repository.SearchActiveListings(keys, 64, 64, Fixture.Viewer);
            var third = fixture.Repository.SearchActiveListings(keys, 64, 128, Fixture.Viewer);
            check("multi-key query applies one stable global first page",
                first.Select(row => row.ListingId).SequenceEqual(expected.Take(64)));
            check("multi-key query second page has no per-key truncation or repeated rows",
                second.Select(row => row.ListingId).SequenceEqual(expected.Skip(64).Take(64)));
            check("multi-key query retains final two rows beyond 128",
                third.Select(row => row.ListingId).SequenceEqual(expected.Skip(128)));
            check("candidate order cannot change query order",
                fixture.Repository.SearchActiveListings(new[] { 3030, 3031 }, 64, 0, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(first.Select(row => row.ListingId)));
            check("single-key compatibility delegates to the same query",
                fixture.Repository.SearchActiveListings(3030, 64, 0, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(
                        fixture.Repository.SearchActiveListings(new[] { 3030 }, 64, 0, Fixture.Viewer)
                            .Select(row => row.ListingId)));
            check("explicit seller exclusion remains available independently of candidate keys",
                first.Concat(second).Concat(third).All(row => row.SellerCharacterId != Fixture.Viewer)
                && fixture.Repository.SearchActiveListings(keys, 64, 0, Fixture.Seller).Count == 1);
            check("query does not rewrite status, quantities or ownership",
                fixture.ListingCount() == 132 && fixture.Repository.GetListing(expected[0]).Item.ItemCount == 1);
        }

        private static void CheckGoldIsolation(Fixture fixture, Action<string, bool> check)
        {
            fixture.Clear();
            long ordinary = fixture.Add(3030, ItemCore.KindMaterial, 100);
            long firstGold = fixture.Add(2681725, ItemCore.KindMaterial, 2);
            long secondGold = fixture.Add(2683071, ItemCore.KindMaterial, 1);
            long ownGold = fixture.Add(2681725, ItemCore.KindMaterial, 1, seller: Fixture.Viewer);
            check("ordinary multi-key search never leaks gold-family listings",
                fixture.Repository.SearchActiveListings(new[] { 3030, 2681725, 2683071 }, 64, 0, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(new[] { ordinary }));
            check("ordinary exact key cannot enter the gold panel",
                fixture.Repository.SearchActiveGoldListings(3030, 64, 0, Fixture.Viewer).Count == 0);
            check("non-gold candidates do not become an unfiltered gold browse",
                fixture.Repository.SearchActiveGoldListings(new[] { 3030, 3031 }, 64, 0, Fixture.Viewer).Count == 0);
            check("mixed candidates query only requested valid gold denominations",
                fixture.Repository.SearchActiveGoldListings(new[] { 3030, 2681725, 2681725 }, 64, 0, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(new[] { firstGold }));
            check("explicit seller filtering preserves the gold denomination union",
                fixture.Repository.SearchActiveGoldListings(Array.Empty<int>(), 64, 0, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(new[] { firstGold, secondGold }));
            check("public gold browse includes the viewer's and other sellers' active listings",
                fixture.Repository.SearchActiveGoldListings(Array.Empty<int>(), 64, 0)
                    .Select(row => row.ListingId).SequenceEqual(new[] { ownGold, firstGold, secondGold }));
            check("gold candidates have stable denomination order and global pagination",
                fixture.Repository.SearchActiveGoldListings(new[] { 2683071, 2681725 }, 1, 1, Fixture.Viewer)
                    .Select(row => row.ListingId).SequenceEqual(new[] { secondGold }));
        }

        private static void CheckResourceFilters(Fixture fixture, Action<string, bool> check)
        {
            fixture.Clear();
            check("PVF category auxiliary numbers do not change their bracket label",
                AuctionRepository.MatchesStackableCategory("`[set]` 1", "throw")
                && AuctionRepository.MatchesStackableCategory("[random reward item] 0", "gacha")
                && AuctionRepository.MatchesStackableCategory("[contract] 2", "misc")
                && !AuctionRepository.MatchesStackableCategory("[throw] 0", "consumable"));
            int shortSword = FindWeapon("ssword"), katana = FindWeapon("katana"), claw = FindWeapon("claw");
            for (int i = 0; i < 70; i++) fixture.Add(shortSword, ItemCore.KindEquipment, 1);
            var expected = new List<long>();
            for (int i = 0; i < 70; i++) expected.Add(fixture.Add(katana, ItemCore.KindEquipment, 2));
            fixture.Add(3030, ItemCore.KindMaterial, 1);
            fixture.Add(claw, ItemCore.KindEquipment, 1);
            var keys = new[] { shortSword, katana, claw, 3030 };
            var filtered = fixture.Repository.SearchActiveListings(keys, 64, 64, Fixture.Viewer,
                filterGroup: "katana", filterUsableJob: "[all]");
            check("resource group and job filters both run before one pagination step",
                filtered.Select(row => row.ListingId).SequenceEqual(expected.Skip(64)));
            var parent = AuctionHandler.ResolveCategoryFilter(0x2775);
            var parentRows = QueryCategory(fixture, keys, parent, 64);
            check("weapon parent searches the leaf union without material leakage",
                parentRows.Count == 64 && parentRows.Count(row => row.Item.ItemTemplateId == shortSword) == 6
                && parentRows.Count(row => row.Item.ItemTemplateId == katana) == 58);
            var weapons = AuctionHandler.ResolveCategoryFilter(0x2710);
            check("all-weapons parent includes the other job's weapon family",
                QueryCategory(fixture, keys, weapons, 64).Count(row => row.Item.ItemTemplateId == claw) == 1);
            var armor = AuctionHandler.ResolveCategoryFilter(0x2AF8);
            check("armor material prefix cl cannot match the weapon group claw",
                QueryCategory(fixture, keys, armor, 0).Count == 0);
            var leaf = AuctionHandler.ResolveCategoryFilter(0x2777);
            check("weapon leaf retains its exact resource filter",
                QueryCategory(fixture, keys, leaf, 0).Select(row => row.ListingId).SequenceEqual(expected.Take(64)));
            var consumables = AuctionHandler.ResolveCategoryFilter(0x32C8);
            check("stackable parent includes its material leaf and excludes equipment",
                QueryCategory(fixture, keys, consumables, 0).All(row => row.Item.ItemTemplateId == 3030)
                && QueryCategory(fixture, keys, consumables, 0).Count == 1);
            var unknown = AuctionHandler.ResolveCategoryFilter(0xFFFF);
            check("unknown category remains empty rather than becoming all items",
                QueryCategory(fixture, keys, unknown, 0).Count == 0);
        }

        private static IReadOnlyList<AuctionListing> QueryCategory(Fixture fixture, IReadOnlyCollection<int> keys,
            (int kind, string group, bool groupPrefix, string filterJob, string filterEquipType, string filterExpertType) filter,
            int offset)
            => fixture.Repository.SearchActiveListings(keys, 64, offset, Fixture.Viewer, filter.kind, filter.group,
                filter.groupPrefix, filter.filterJob, filter.filterEquipType, filter.filterExpertType);

        private static int FindWeapon(string group)
        {
            foreach (var entry in ItemMetadataResolver.EquipmentList.Value.Entries
                .Where(entry => entry.FilePath.IndexOf("/" + group + "/", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (ItemMetadataResolver.TryLoadEquipmentFile(entry.Id, out var equipment)
                    && string.Equals(equipment.ItemGroupName, group, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(equipment.UsableJob)) return entry.Id;
            }
            throw new InvalidOperationException("Current PVF has no weapon fixture for group " + group);
        }

        private sealed class Fixture : IDisposable
        {
            internal const int Seller = 68001, Viewer = 68002;
            private readonly string _path = Path.Combine(Path.GetTempPath(), "a21-auction-search-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly long _listedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            internal readonly GameDatabase Database;
            internal readonly AuctionRepository Repository;

            internal Fixture()
            {
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES"
                        + "(68001,'auction-search-seller',''),(68002,'auction-search-viewer','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES"
                        + "(68001,68001,'auction-search-seller',85),(68002,68002,'auction-search-viewer',85);";
                    command.ExecuteNonQuery();
                });
                Repository = new AuctionRepository(Database);
            }

            internal long Add(int itemId, byte kind, int price, int count = 1, int seller = Seller,
                AuctionListingStatus status = AuctionListingStatus.Active)
            {
                return Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO auction_listings(seller_character_id,item_template_id,
                        item_kind,item_count,item_core_data,buyout_price,status,listed_at_unix,expires_at_unix)
                        VALUES(@seller,@item,@kind,@count,@core,@price,@status,@listed,@expires);
                        SELECT last_insert_rowid();";
                    command.Parameters.AddWithValue("@seller", seller);
                    command.Parameters.AddWithValue("@item", itemId);
                    command.Parameters.AddWithValue("@kind", kind.ToString());
                    command.Parameters.AddWithValue("@count", count);
                    command.Parameters.AddWithValue("@core", new ItemCore { ItemKind = kind, ItemId = itemId, Count = count }.ToBytes());
                    command.Parameters.AddWithValue("@price", price);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@listed", _listedAt);
                    command.Parameters.AddWithValue("@expires", _listedAt + 86400);
                    return Convert.ToInt64(command.ExecuteScalar());
                });
            }

            internal void Clear() => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM auction_listings;"; command.ExecuteNonQuery();
            });

            internal long ListingCount() => Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM auction_listings";
                return Convert.ToInt64(command.ExecuteScalar());
            });

            public void Dispose()
            {
                using (var connection = Database.OpenConnection()) SqliteConnection.ClearPool(connection);
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
