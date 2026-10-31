using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class AuctionMigrationSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== AUCTION_MIGRATION selftest ===");
            int assertions = 0, failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                if (!passed) failures++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            }
            void Section(string label, Action<Fixture> verify)
            {
                try { using var fixture = new Fixture(); verify(fixture); }
                catch (Exception exception) { Check(label + ": " + exception, false); }
            }
            Section("fresh current database", fixture => CheckFresh(fixture, Check));
            Section("schema38 upgrade and atomic rollback", fixture => CheckMigration(fixture, Check));
            Section("strict auction constraints", fixture => CheckConstraints(fixture, Check));
            Section("inconsistent v38 storage", fixture => CheckInconsistentStorage(fixture, Check));
            Console.WriteLine($"AUCTION_MIGRATION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckFresh(Fixture fixture, Action<string, bool> check)
        {
            check("the current fresh baseline has matching schema versions and no player or auction seeds",
                SqliteMigrations.CurrentVersion >= 41 && fixture.Version() == SqliteMigrations.CurrentVersion
                && fixture.MetadataVersion() == SqliteMigrations.CurrentVersion
                && fixture.Scalar("SELECT COUNT(*) FROM accounts;") == 0
                && fixture.Scalar("SELECT COUNT(*) FROM characters;") == 0
                && fixture.Scalar("SELECT COUNT(*) FROM auction_listings;") == 0
                && fixture.Scalar("SELECT COUNT(*) FROM auction_bids;") == 0);
            check("both auction owners and all seven query indexes exist",
                fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('auction_listings','auction_bids');") == 2
                && fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'idx_auction_%';") == 7);
            check("the listing requires a99-byte blob and real absolute listing and expiry timestamps",
                fixture.Scalar(@"SELECT COUNT(*) FROM pragma_table_info('auction_listings')
WHERE (name='item_core_data' AND type='BLOB' AND ""notnull""=1 AND dflt_value IS NULL)
   OR (name IN ('listed_at_unix','expires_at_unix') AND type='INTEGER' AND ""notnull""=1 AND dflt_value IS NULL);") == 3);
            fixture.SeedExistingOwners();
            fixture.Sql(ValidListing);
            check("fresh listings have typed empty buyer,bid and mail progress defaults without inventing a deal",
                fixture.Scalar(@"SELECT COUNT(*) FROM auction_listings WHERE listing_id=1
AND status=0 AND buyer_character_id=0 AND buyer_name='' AND sold_at_unix IS NULL
AND settle_mail_count=0 AND current_bid=0 AND current_bidder_id=0 AND current_bidder_name=''
AND bid_count=0 AND actual_deal_price=0 AND starting_price=0 AND item_count=1
AND typeof(item_core_data)='blob' AND length(item_core_data)=99;") == 1);
            fixture.Sql(ValidBid);
            check("fresh bid escrow defaults to current and unrefunded with its own persisted timestamp",
                fixture.Scalar("SELECT COUNT(*) FROM auction_bids WHERE bid_id=1 AND status=0 AND refund_mail_sent=0 AND bid_at_unix=101;") == 1);
            check("auction foreign keys remain enabled and have no broken references",
                fixture.Scalar("PRAGMA foreign_keys;") == 1 && fixture.ForeignKeyErrors() == 0);
            check("three escrow foreign keys restrict physical deletion rather than cascade away asset owners",
                fixture.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('auction_listings') WHERE on_delete='RESTRICT';") == 1
                && fixture.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('auction_bids') WHERE on_delete='RESTRICT';") == 2);
        }

        private static void CheckMigration(Fixture fixture, Action<string, bool> check)
        {
            fixture.SeedExistingOwners();
            string currentAuctionSchema = fixture.Schema(true);
            string originalOtherSchema = fixture.Schema(false);
            string originalOwners = fixture.OwnerSnapshot();
            fixture.Sql(MigrationSelfTestFixture.RemoveAuctionTables
                + "UPDATE schema_metadata SET schema_version=38 WHERE singleton_id=1; PRAGMA user_version=38;"
                + "CREATE TRIGGER reject_auction_migration BEFORE UPDATE OF schema_version ON schema_metadata "
                + "WHEN NEW.schema_version=39 BEGIN SELECT RAISE(ABORT,'auction-migration-fault'); END;");
            check("the isolated v38 fixture keeps guild contribution,recommended channel and its history index",
                fixture.Version() == 38 && fixture.MetadataVersion() == 38 && fixture.AuctionObjectCount() == 0
                && fixture.OwnerSnapshot() == originalOwners
                && fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('guilds') WHERE name IN ('points','flag_shape_first','flag_shape_second');") == 0
                && fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_support_rentals') WHERE name='selected_skill_id';") == 0
                && fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='idx_dungeon_effect_outbox_guild_history';") == 1);
            bool rejected = ThrowsConstraint(() => fixture.Apply(), "auction-migration-fault");
            check("a version-write failure rolls both full auction tables,all indexes and versions back",
                rejected && fixture.Version() == 38 && fixture.MetadataVersion() == 38
                && fixture.AuctionObjectCount() == 0 && fixture.OwnerSnapshot() == originalOwners);
            fixture.Sql("DROP TRIGGER reject_auction_migration;");
            fixture.Apply();
            check("retry of the registered migrations matches complete new-database DDL",
                fixture.Version() == SqliteMigrations.CurrentVersion && fixture.MetadataVersion() == SqliteMigrations.CurrentVersion
                && fixture.Schema(true) == currentAuctionSchema && fixture.Schema(false) == originalOtherSchema
                && fixture.OwnerSnapshot() == originalOwners && fixture.ForeignKeyErrors() == 0);
            fixture.Sql(ValidListing + ValidBid);
            fixture.Sql(@"UPDATE auction_listings SET status=1,buyer_character_id=502,buyer_name='auction-buyer',
sold_at_unix=150,current_bid=25,current_bidder_id=502,bid_count=1,actual_deal_price=100,settle_mail_count=1 WHERE listing_id=1;
UPDATE auction_bids SET status=1,refund_mail_sent=0 WHERE bid_id=1;");
            fixture.Apply();
            fixture.Apply();
            check("reapplying current migrations preserves partial settlement,frozen actual price and pending refund",
                fixture.Version() == SqliteMigrations.CurrentVersion && fixture.MetadataVersion() == SqliteMigrations.CurrentVersion
                && fixture.Scalar("SELECT COUNT(*) FROM auction_listings WHERE listing_id=1 AND status=1 AND current_bid=25 AND actual_deal_price=100 AND settle_mail_count=1;") == 1
                && fixture.Scalar("SELECT COUNT(*) FROM auction_bids WHERE bid_id=1 AND status=1 AND refund_mail_sent=0;") == 1
                && fixture.OwnerSnapshot() == originalOwners && fixture.Schema(true) == currentAuctionSchema);
        }

        private static void CheckConstraints(Fixture fixture, Action<string, bool> check)
        {
            fixture.SeedExistingOwners();
            fixture.Sql(ValidListing + ValidBid);
            foreach (var (label, update) in new (string, string)[]
            {
                ("zero quantity", "item_count=0"),
                ("negative quantity", "item_count=-1"),
                ("quantity overflow", "item_count=2147483648"),
                ("fractional quantity", "item_count=1.5"),
                ("text quantity", "item_count='invalid'"),
                ("negative buyout", "buyout_price=-1"),
                ("buyout overflow", "buyout_price=2147483648"),
                ("fractional starting price", "starting_price=1.5"),
                ("no buyout and no starting price", "buyout_price=0,starting_price=0"),
                ("negative actual deal", "actual_deal_price=-1"),
                ("actual deal overflow", "actual_deal_price=2147483648"),
                ("fractional actual deal", "actual_deal_price=0.5"),
                ("invalid listing status", "status=5"),
                ("fractional listing status", "status=0.5"),
                ("negative mail progress", "settle_mail_count=-1"),
                ("mail progress beyond two legs", "settle_mail_count=3"),
                ("fractional mail progress", "settle_mail_count=0.5"),
                ("negative current bid", "current_bid=-1"),
                ("bid count overflow", "bid_count=2147483648"),
                ("negative source slot", "source_slot_index=-1"),
                ("source list overflow", "source_list_type=256"),
                ("invalid durability", "durability=65536"),
                ("invalid seal flag", "seal_flag=256"),
                ("negative buyer id", "buyer_character_id=-1"),
                ("equal expiry and listing time", "expires_at_unix=listed_at_unix"),
                ("earlier expiry", "expires_at_unix=99"),
                ("fractional expiry", "expires_at_unix=200.5"),
                ("wire expiry overflow", "expires_at_unix=2147483648"),
                ("negative sale time", "sold_at_unix=-1"),
                ("null snapshot", "item_core_data=NULL"),
                ("legacy82-byte snapshot", "item_core_data=zeroblob(82)"),
                ("oversize100-byte snapshot", "item_core_data=zeroblob(100)"),
                ("text pretending to be a99-byte snapshot", "item_core_data=printf('%099d',1)"),
            })
            {
                check(label + " is rejected by listing storage",
                    ThrowsConstraint(() => fixture.Sql("UPDATE auction_listings SET " + update + " WHERE listing_id=1;")));
            }
            foreach (var (label, update) in new (string, string)[]
            {
                ("zero bid amount", "bid_amount=0"),
                ("overflow bid amount", "bid_amount=2147483648"),
                ("fractional bid amount", "bid_amount=1.5"),
                ("invalid bid status", "status=4"),
                ("invalid refund flag", "refund_mail_sent=2"),
                ("fractional refund flag", "refund_mail_sent=0.5"),
                ("negative bid timestamp", "bid_at_unix=-1"),
                ("missing listing", "listing_id=123456"),
                ("missing bidder", "bidder_character_id=123456"),
            })
            {
                check(label + " is rejected by bid storage",
                    ThrowsConstraint(() => fixture.Sql("UPDATE auction_bids SET " + update + " WHERE bid_id=1;")));
            }
            check("a listing cannot reference a nonexistent escrow owner",
                ThrowsConstraint(() => fixture.Sql("UPDATE auction_listings SET seller_character_id=123456 WHERE listing_id=1;")));
            check("physical deletion cannot cascade away a seller's owned escrow",
                ThrowsConstraint(() => fixture.Sql("DELETE FROM characters WHERE character_id=501;")));
            check("physical deletion cannot cascade away a bidder's refund history",
                ThrowsConstraint(() => fixture.Sql("DELETE FROM characters WHERE character_id=502;")));
            check("physical deletion cannot erase a listing while bid escrow still references it",
                ThrowsConstraint(() => fixture.Sql("DELETE FROM auction_listings WHERE listing_id=1;")));
            fixture.Sql("UPDATE auction_listings SET buyout_price=0,starting_price=1,instance_value=-1,"
                + "expire_time=-1,marker16=-1,option_value=-2147483648,pet_serial_or_handle=-1 WHERE listing_id=1;");
            check("pure bidding and existing signed ItemCore sentinels remain valid without rewriting snapshots",
                fixture.Scalar("SELECT COUNT(*) FROM auction_listings WHERE listing_id=1 AND buyout_price=0 AND starting_price=1 AND expire_time=-1 AND marker16=-1 AND length(item_core_data)=99;") == 1);
            fixture.Sql("UPDATE auction_listings SET status=1,settle_mail_count=1,actual_deal_price=5 WHERE listing_id=1;");
            check("partial sold settlement remains representable without forcing settled status",
                fixture.Scalar("SELECT COUNT(*) FROM auction_listings WHERE listing_id=1 AND status=1 AND settle_mail_count=1 AND actual_deal_price=5;") == 1);
            check("failed writes retain both original owners and foreign-key integrity",
                fixture.Scalar("SELECT COUNT(*) FROM auction_listings;") == 1
                && fixture.Scalar("SELECT COUNT(*) FROM auction_bids;") == 1 && fixture.ForeignKeyErrors() == 0);
        }

        private static void CheckInconsistentStorage(Fixture fixture, Action<string, bool> check)
        {
            fixture.SeedExistingOwners();
            string owners = fixture.OwnerSnapshot();
            fixture.Sql(MigrationSelfTestFixture.RemoveAuctionTables
                + "CREATE TABLE auction_listings(incompatible_column TEXT);"
                + "UPDATE schema_metadata SET schema_version=38 WHERE singleton_id=1; PRAGMA user_version=38;");
            bool rejected = ThrowsConstraint(() => fixture.Apply(), "already exists");
            check("v39 does not silently mark a pre-existing incompatible auction table as upgraded",
                rejected && fixture.Version() == 38 && fixture.MetadataVersion() == 38
                && fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('auction_listings') WHERE name='incompatible_column';") == 1
                && fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='auction_bids';") == 0
                && fixture.OwnerSnapshot() == owners);
            fixture.Sql("DROP TABLE auction_listings;");
            fixture.Apply();
            check("correcting only the isolated inconsistent fixture permits a clean retry",
                fixture.Version() == SqliteMigrations.CurrentVersion && fixture.MetadataVersion() == SqliteMigrations.CurrentVersion
                && fixture.AuctionObjectCount() == 9 && fixture.OwnerSnapshot() == owners);
            fixture.Sql("UPDATE schema_metadata SET baseline_id='foreign-auction-baseline';");
            check("an incorrect database baseline cannot be adopted by auction migration",
                ThrowsInvalid(() => fixture.Apply()) && fixture.AuctionObjectCount() == 9 && fixture.OwnerSnapshot() == owners);
            fixture.Sql($"UPDATE schema_metadata SET baseline_id='86jp-database-v1',schema_version={SqliteMigrations.CurrentVersion + 1}; PRAGMA user_version={SqliteMigrations.CurrentVersion + 1};");
            check("future versions fail closed without deleting auction or guild storage",
                ThrowsInvalid(() => fixture.Apply()) && fixture.AuctionObjectCount() == 9 && fixture.OwnerSnapshot() == owners);
            fixture.Sql("UPDATE schema_metadata SET schema_version=38; PRAGMA user_version=39;");
            check("mismatched versions fail before any auction schema change",
                ThrowsInvalid(() => fixture.Apply()) && fixture.AuctionObjectCount() == 9 && fixture.OwnerSnapshot() == owners);
        }

        private const string ValidListing = @"
INSERT INTO auction_listings(listing_id,seller_character_id,item_template_id,item_core_data,buyout_price,listed_at_unix,expires_at_unix)
VALUES(1,501,3033,zeroblob(99),100,100,200);";
        private const string ValidBid = @"
INSERT INTO auction_bids(bid_id,listing_id,bidder_character_id,bid_amount,bid_at_unix)
VALUES(1,1,502,25,101);";

        private static bool ThrowsConstraint(Action action, string text = null)
        {
            try { action(); }
            catch (SqliteException exception) when (text == null
                ? exception.SqliteErrorCode == 19
                : exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return true; }
            return false;
        }

        private static bool ThrowsInvalid(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return true; }
            return false;
        }

        private sealed class Fixture : IDisposable
        {
            internal Fixture()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "auction-migration-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(Path, ServerPaths.SchemaFilePath);
            }
            private string Path { get; }
            private GameDatabase Database { get; }
            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = sql; command.ExecuteNonQuery();
            });
            internal long Scalar(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });
            internal void Apply() { using var connection = Database.OpenConnection(); SqliteMigrations.Apply(connection); }
            internal long Version() => Scalar("PRAGMA user_version;");
            internal long MetadataVersion() => Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;");
            internal long AuctionObjectCount() => Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('auction_listings','auction_bids') OR name LIKE 'idx_auction_%';");
            internal int ForeignKeyErrors() => Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_key_check;";
                using var reader = command.ExecuteReader(); int count = 0;
                while (reader.Read()) count++;
                return count;
            });
            internal void SeedExistingOwners() => Sql(@"
INSERT INTO accounts(account_id,m_id,password_hash,cera,token_cera) VALUES(500,'auction-schema-test','',123,456);
INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,pos_x,pos_y,exp) VALUES
(501,500,'auction-seller',70,11,3,474,234,12345),(502,500,'auction-buyer',40,11,3,474,234,54321),
(503,500,'preserved-guild-founder',50,11,3,474,234,32100);
INSERT INTO guilds(guild_id,name,leader_character_id,announcement,recommended_server_id,recommended_channel_id,
contribution_total,contribution_month_id,contribution_month_value)
VALUES(700,'auction-preserve-guild',503,'preserve guild state',1,7,13,202610,11);
INSERT INTO guild_members(character_id,guild_id,rank,memo,contribution_total,contribution_month_id,contribution_month_value)
VALUES(503,700,4,'preserve member state',17,202610,15);
INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,month_id) VALUES(700,8500,23,202610);");
            internal string OwnerSnapshot() => Database.Read(connection =>
            {
                var rows = new List<string>();
                foreach (string table in new[] { "accounts", "characters", "guilds", "guild_members", "guild_dungeon_progress" })
                {
                    using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new List<string> { table };
                        for (int index = 0; index < reader.FieldCount; index++)
                        {
                            // Compare only owners already present at v38.
                            // The isolated downgrade correctly removes later
                            // points/flag fields; their defaults and preservation
                            // belong to the dedicated v41/v44 migration tests.
                            if (table == "guilds" && reader.GetName(index)
                                is "points" or "flag_shape_first" or "flag_shape_second") continue;
                            row.Add(reader.IsDBNull(index) ? "<null>" : reader.GetValue(index) is byte[] bytes
                                ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture));
                        }
                        rows.Add(string.Join("|", row));
                    }
                }
                return string.Join("\n", rows);
            });
            internal string Schema(bool auction) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL AND "
                    + (auction ? "(name IN ('auction_listings','auction_bids') OR name LIKE 'idx_auction_%')"
                        : "name NOT IN ('auction_listings','auction_bids') AND name NOT LIKE 'idx_auction_%'")
                    + " ORDER BY type,name;";
                using var reader = command.ExecuteReader(); var rows = new List<string>();
                while (reader.Read())
                {
                    string sql = Regex.Replace(reader.GetString(2).Replace("IF NOT EXISTS ", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ");
                    // ALTER TABLE adds formatting around its appended column.
                    // Compare the same constraints regardless of that spacing.
                    sql = Regex.Replace(sql, @"\s*([(),])\s*", "$1");
                    rows.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + sql);
                }
                return string.Join("\n", rows);
            });
            public void Dispose()
            {
                using (var connection = Database.OpenConnection()) SqliteConnection.ClearPool(connection);
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
            }
        }
    }
}
