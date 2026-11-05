using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.SelfTests
{
    internal static class WeddingSchemaSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-schema-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                using var connection = database.OpenConnection();
                void Sql(string text) { using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
                long Scalar(string text) { using var command = connection.CreateCommand(); command.CommandText = text; return Convert.ToInt64(command.ExecuteScalar()); }
                string[] Definitions()
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT name,sql FROM sqlite_master WHERE name LIKE 'wedding_%' OR name LIKE 'idx_wedding_%' ORDER BY name;";
                    using var reader = command.ExecuteReader(); var result = new List<string>();
                    while (reader.Read()) result.Add(reader.GetString(0) + ":" + Regex.Replace(reader.GetString(1), @"\s+", " ").Trim());
                    return result.ToArray();
                }
                var baseline = Definitions();
                Check("fresh baseline contains all wedding tables at schema v43", SqliteMigrations.ReadVersion(connection) == 43
                    && Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'wedding_%';") == 13);
                Sql("INSERT INTO accounts(account_id,m_id) VALUES(63401,'wedding-migration');"
                    + "INSERT INTO characters(character_id,account_id,name) VALUES(63401,63401,'迁移甲'),(63402,63401,'迁移乙');");
                const string dropGuests = @"DROP TABLE wedding_ceremony_comments; DROP TABLE wedding_reservation_receipts;
DROP TABLE wedding_money_gift_items; DROP TABLE wedding_money_gifts; DROP TABLE wedding_guest_invitations;";
                Sql(dropGuests + "UPDATE schema_metadata SET schema_version=42 WHERE singleton_id=1; PRAGMA user_version=42;");
                Check("the v42 fixture retains only the original wedding schema",
                    Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'wedding_%';") == 8);
                SqliteMigrations.Apply(connection);
                Check("v42 migration creates exactly the same guest and gift schema as a fresh database",
                    Definitions().SequenceEqual(baseline) && SqliteMigrations.ReadVersion(connection) == 43);
                Sql(dropGuests + @"DROP TABLE wedding_guest_comments; DROP TABLE wedding_capsules; DROP TABLE wedding_room_items;
DROP TABLE wedding_rooms; DROP TABLE wedding_progress_events; DROP TABLE wedding_character_cooldowns;
DROP TABLE wedding_members; DROP TABLE wedding_pairs;
UPDATE schema_metadata SET schema_version=41 WHERE singleton_id=1; PRAGMA user_version=41;");
                Check("the v41 fixture contains no wedding tables", Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'wedding_%';") == 0);
                SqliteMigrations.Apply(connection);
                Check("v41 migration creates exactly the same wedding schema as a fresh database", Definitions().SequenceEqual(baseline)
                    && SqliteMigrations.ReadVersion(connection) == 43 && Scalar("SELECT schema_version FROM schema_metadata;") == 43);
                Check("migration preserves existing accounts and characters", Scalar("SELECT COUNT(*) FROM characters WHERE account_id=63401;") == 2
                    && Scalar("SELECT COUNT(*) FROM accounts WHERE m_id='wedding-migration';") == 1);
                Sql(@"INSERT INTO wedding_pairs(left_character_id,right_character_id,status,created_at,love) VALUES(63401,63402,4,1,123);
INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63401,1,0),(63402,1,1);
INSERT INTO wedding_rooms(pair_id,tree_id,tree_growth) VALUES(1,1,20);
INSERT INTO wedding_capsules(pair_id,character_id,progress,own_count,taken_count) VALUES(1,63401,6,2,1);
UPDATE schema_metadata SET schema_version=41 WHERE singleton_id=1; PRAGMA user_version=41;");
                SqliteMigrations.Apply(connection);
                SqliteMigrations.Apply(connection);
                Check("repeated migration does not recreate or reset relationship, room or capsule data", Scalar("SELECT love FROM wedding_pairs WHERE pair_id=1;") == 123
                    && Scalar("SELECT COUNT(*) FROM wedding_members WHERE pair_id=1;") == 2
                    && Scalar("SELECT tree_growth FROM wedding_rooms WHERE pair_id=1;") == 20
                    && Scalar("SELECT progress*100+own_count*10+taken_count FROM wedding_capsules WHERE pair_id=1;") == 621);
                Check("migration preserves foreign-key integrity", Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;") == 0);
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_SCHEMA: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }
    }
}
