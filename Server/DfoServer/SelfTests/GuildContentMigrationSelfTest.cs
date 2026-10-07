using System;
using System.IO;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class GuildContentMigrationSelfTest
    {
        internal static int Run()
        {
            int assertions = 0, failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                if (!passed) failures++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            }
            void Section(string label, Action<Fixture> test)
            {
                try { using var fixture = new Fixture(); test(fixture); }
                catch (Exception exception) { Check(label + ": " + exception, false); }
            }

            Section("fresh guild purchase schema", f =>
            {
                Check("new database is schema41 with typed zero points and no purchased contents",
                    SqliteMigrations.CurrentVersion == 41 && f.Version == 41
                    && f.MetadataVersion == 41 && f.Scalar("SELECT COUNT(*) FROM guild_contents;") == 0
                    && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guilds') WHERE name='points' AND type='INTEGER' AND \"notnull\"=1 AND dflt_value='0';") == 1);
                f.Seed();
                Check("purchase points are independent of guild and individual contribution",
                    f.Scalar("SELECT points FROM guilds WHERE guild_id=1;") == 0
                    && f.Scalar("SELECT contribution_total FROM guilds WHERE guild_id=1;") == 13
                    && f.Scalar("SELECT contribution_total FROM guild_members WHERE character_id=1;") == 17);
                foreach (string bad in new[] { "-1", "4294967296", "1.5", "'invalid'", "NULL" })
                    Check("points reject invalid value " + bad,
                        f.Reject($"UPDATE guilds SET points={bad} WHERE guild_id=1;"));
                f.Sql("UPDATE guilds SET points=4294967295 WHERE guild_id=1;"
                    + "INSERT INTO guild_contents(guild_id,category,expires_at,remaining_uses) VALUES(1,7,4294967295,2147483647);");
                Check("valid protocol upper bounds are stored without truncation",
                    f.Scalar("SELECT points FROM guilds WHERE guild_id=1;") == uint.MaxValue
                    && f.Scalar("SELECT expires_at FROM guild_contents;") == uint.MaxValue
                    && f.Scalar("SELECT remaining_uses FROM guild_contents;") == int.MaxValue);
                foreach (string update in new[] { "category=0", "category=256", "category=1.5",
                    "expires_at=0", "expires_at=4294967296", "expires_at=1.5",
                    "remaining_uses=-1", "remaining_uses=2147483648", "remaining_uses=1.5" })
                    Check("contents reject invalid field " + update, f.Reject("UPDATE guild_contents SET " + update + ";"));
                Check("a category can have only one entitlement per guild",
                    f.Reject("INSERT INTO guild_contents(guild_id,category,expires_at) VALUES(1,7,100);"));
                Check("contents cannot invent a guild owner",
                    f.Reject("INSERT INTO guild_contents(guild_id,category,expires_at) VALUES(999,1,100);"));
                f.Sql("DELETE FROM guilds WHERE guild_id=1;");
                Check("dissolution removes purchased contents with the original guild owner",
                    f.Scalar("SELECT COUNT(*) FROM guild_contents;") == 0);
            });

            Section("v40 migration rollback and retry", f =>
            {
                f.Seed();
                string owners = f.ExistingState;
                f.Sql(MigrationSelfTestFixture.RemoveGuildPurchasedContents
                    + "UPDATE schema_metadata SET schema_version=40; PRAGMA user_version=40;"
                    + "CREATE TRIGGER reject_guild_content_version BEFORE UPDATE OF schema_version ON schema_metadata "
                    + "WHEN NEW.schema_version=41 BEGIN SELECT RAISE(ABORT,'guild-content-migration-fault'); END;");
                Check("fixture starts at actual v40 without v41 fields",
                    f.Version == 40 && f.MetadataVersion == 40 && !f.HasContents && !f.HasPoints);
                Check("failed version commit rolls both points and contents back atomically",
                    f.RejectMigration("guild-content-migration-fault")
                    && f.Version == 40 && f.MetadataVersion == 40 && !f.HasContents && !f.HasPoints
                    && f.ExistingState == owners);
                f.Sql("DROP TRIGGER reject_guild_content_version;");
                f.Apply();
                Check("v40 upgrade preserves membership, contribution, channel, purification and feature state",
                    f.Version == 41 && f.MetadataVersion == 41 && f.HasContents && f.HasPoints
                    && f.ExistingState == owners && f.Scalar("SELECT points FROM guilds;") == 0
                    && f.Scalar("SELECT COUNT(*) FROM guild_contents;") == 0);
                f.Sql("UPDATE guilds SET points=250 WHERE guild_id=1;"
                    + "INSERT INTO guild_contents(guild_id,category,expires_at,remaining_uses) VALUES(1,7,2000000000,3);");
                f.Apply();
                f.Apply();
                Check("retry does not reset balance, expiry or remaining uses",
                    f.Scalar("SELECT points FROM guilds;") == 250
                    && f.Scalar("SELECT expires_at FROM guild_contents;") == 2000000000
                    && f.Scalar("SELECT remaining_uses FROM guild_contents;") == 3 && f.ExistingState == owners);
                Check("migration leaves foreign key ownership intact", f.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;") == 0);
            });

            Section("inconsistent v40 schema", f =>
            {
                f.Seed();
                f.Sql(MigrationSelfTestFixture.RemoveGuildPurchasedContents
                    + "CREATE TABLE guild_contents(incompatible_column TEXT);"
                    + "UPDATE schema_metadata SET schema_version=40; PRAGMA user_version=40;");
                Check("the incompatible v40 fixture contains its actual foreign contents table",
                    f.Version == 40 && !f.HasPoints
                    && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_contents') WHERE name='incompatible_column';") == 1);
                bool rejected = f.RejectMigration("already exists");
                Check("incompatible pre-existing contents fail without marking the migration complete",
                    rejected && f.Version == 40 && f.MetadataVersion == 40
                    && !f.HasPoints && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_contents') WHERE name='incompatible_column';") == 1);
                f.Sql("DROP TABLE guild_contents;");
                f.Apply();
                Check("repair of the isolated inconsistent fixture allows the registered migration",
                    f.Version == 41 && f.HasPoints && f.HasContents);
            });
            Console.WriteLine($"GUILD_CONTENT_MIGRATION assertions={assertions} failures={failures}");
            return failures;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "guild-content-migration-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly GameDatabase _database;
            internal Fixture() { _database = new GameDatabase(_path, ServerPaths.SchemaFilePath); }
            internal long Version => Scalar("PRAGMA user_version;");
            internal long MetadataVersion => Scalar("SELECT schema_version FROM schema_metadata;");
            internal bool HasPoints => Scalar("SELECT COUNT(*) FROM pragma_table_info('guilds') WHERE name='points';") == 1;
            internal bool HasContents => Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_contents';") == 1;
            internal string ExistingState => _database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT g.name || '|' || g.recommended_server_id || '|' || g.recommended_channel_id
|| '|' || g.contribution_total || '|' || g.contribution_month_id || '|' || g.contribution_month_value
|| '|' || m.character_id || '|' || m.contribution_total || '|' || p.clear_count || '|' || p.month_id
|| '|' || s.capacity || '|' || s.revision || '|' || hex(i.item_core)
FROM guilds g JOIN guild_members m USING(guild_id) JOIN guild_dungeon_progress p USING(guild_id)
JOIN guild_storage_state s USING(guild_id) JOIN guild_storage_items i USING(guild_id);";
                return Convert.ToString(command.ExecuteScalar());
            });
            internal void Seed() => Sql(@"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guild-migration','');
INSERT INTO characters(character_id,account_id,name) VALUES(1,1,'guild-migration');
INSERT INTO guilds(guild_id,name,leader_character_id,recommended_server_id,recommended_channel_id,
contribution_total,contribution_month_id,contribution_month_value) VALUES(1,'migration guild',1,1,7,13,202610,11);
INSERT INTO guild_members(character_id,guild_id,contribution_total) VALUES(1,1,17);
INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,month_id) VALUES(1,8500,23,202610);
INSERT INTO guild_storage_state(guild_id,capacity,revision) VALUES(1,10,9);
INSERT INTO guild_storage_items(guild_id,slot_index,item_core) VALUES(1,0,zeroblob(99));");
            internal long Scalar(string sql) => _database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });
            internal void Sql(string sql) => _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = sql; command.ExecuteNonQuery();
            });
            internal void Apply() { using var connection = _database.OpenConnection(); SqliteMigrations.Apply(connection); }
            internal bool Reject(string sql)
            {
                try { Sql(sql); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { return true; }
                return false;
            }
            internal bool RejectMigration(string text)
            {
                try { Apply(); }
                catch (SqliteException exception) when (exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                { return true; }
                catch (InvalidOperationException exception) when (exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                { return true; }
                return false;
            }
            public void Dispose()
            {
                using (var connection = _database.OpenConnection()) SqliteConnection.ClearPool(connection);
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
