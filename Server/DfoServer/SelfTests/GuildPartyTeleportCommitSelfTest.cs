using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class GuildPartyTeleportCommitSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool result)
            {
                Console.WriteLine($"[{(result ? "PASS" : "FAIL")}] {label}");
                if (result) passed++; else failed++;
            }
            string path = Path.Combine(Path.GetTempPath(), $"guild-party-teleport-{Guid.NewGuid():N}.db");
            var leaderSession = Guid.NewGuid();
            var memberSession = Guid.NewGuid();
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) => Execute(connection, transaction, @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(961,'guild-party-teleport','');
INSERT INTO characters(character_id,account_id,name,town_id,area_id,pos_x,pos_y) VALUES
(961,961,'guild-teleport-leader',1,1,10,20),(962,961,'guild-teleport-member',1,1,10,20);"));
                int guildId = database.Write((connection, transaction) =>
                    GuildRepository.Insert(connection, transaction, 961, "guild-party-teleport", "").Id);
                var leader = InventoryContext.Register(leaderSession, new InventoryService(961, 961, database));
                var member = InventoryContext.Register(memberSession, new InventoryService(962, 961, database));
                var targets = new[] {
                    new GuildPartyTeleportTarget(leader, true),
                    new GuildPartyTeleportTarget(member, true)
                };
                void SetUses(uint uses, uint expiry = int.MaxValue) => database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO guild_contents(guild_id,category,expires_at,remaining_uses)
VALUES(@guild,7,@expiry,@uses) ON CONFLICT(guild_id,category)
DO UPDATE SET expires_at=excluded.expires_at,remaining_uses=excluded.remaining_uses;";
                    command.Parameters.AddWithValue("@guild", guildId);
                    command.Parameters.AddWithValue("@expiry", (long)expiry);
                    command.Parameters.AddWithValue("@uses", (long)uses);
                    command.ExecuteNonQuery();
                });
                long Uses() => database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT remaining_uses FROM guild_contents WHERE guild_id=@guild AND category=7;";
                    command.Parameters.AddWithValue("@guild", guildId);
                    return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                });
                (int Town, int Area, int X, int Y) Position(int id) => database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT town_id,area_id,pos_x,pos_y FROM characters WHERE character_id=@id;";
                    command.Parameters.AddWithValue("@id", id);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) throw new InvalidOperationException("Teleport fixture character is missing.");
                    return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                });
                bool Commit(short x, Func<bool> current, out int remaining)
                    => GuildPartyTeleportCommitService.TryCommit(leader, guildId, targets,
                        townId: 2, areaId: 2, x, y: 30, current, out remaining);

                SetUses(2);
                lock (leader.SyncRoot)
                    leader.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, 54321);
                Check("one use and all durable party destinations commit together",
                    Commit(100, () => true, out int remaining) && remaining == 1 && Uses() == 1
                    && Position(961) == (2, 2, 100, 30) && Position(962) == (2, 2, 100, 30));
                Check("the commit preserves previously accepted online dirty gold",
                    Gold(leader) == 54321);
                Check("an old source cannot spend quota or move anyone",
                    !Commit(200, () => false, out _) && Uses() == 1
                    && Position(961) == (2, 2, 100, 30) && Position(962) == (2, 2, 100, 30));

                SetUses(2, expiry: 1);
                Check("expired content refuses the entire movement",
                    !Commit(200, () => true, out _) && Uses() == 2
                    && Position(961) == (2, 2, 100, 30) && Position(962) == (2, 2, 100, 30));
                SetUses(2);
                Check("another captured guild cannot pay for this party",
                    !GuildPartyTeleportCommitService.TryCommit(leader, guildId + 1, targets,
                        2, 2, 200, 30, () => true, out _) && Uses() == 2
                    && Position(961) == (2, 2, 100, 30));
                database.Write((connection, transaction) => Execute(connection, transaction,
                    "UPDATE characters SET delete_flag=1 WHERE character_id=962;"));
                Check("every recipient must still be an active character of its lease account",
                    !Commit(200, () => true, out _) && Uses() == 2
                    && Position(961) == (2, 2, 100, 30));
                database.Write((connection, transaction) => Execute(connection, transaction,
                    "UPDATE characters SET delete_flag=0 WHERE character_id=962;"));

                database.Write((connection, transaction) => Execute(connection, transaction, @"
CREATE TRIGGER reject_party_destination BEFORE UPDATE OF town_id ON characters
WHEN NEW.character_id=962 BEGIN SELECT RAISE(ABORT,'guild party teleport failure'); END;"));
                lock (leader.SyncRoot)
                    leader.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, 67890);
                Check("a recipient write failure rolls back the payer use and all positions",
                    !Commit(200, () => true, out _) && Uses() == 2
                    && Position(961) == (2, 2, 100, 30) && Position(962) == (2, 2, 100, 30)
                    && Gold(leader) == 67890);
                database.Write((connection, transaction) => Execute(connection, transaction, "DROP TRIGGER reject_party_destination;"));

                var transientTargets = new[] {
                    new GuildPartyTeleportTarget(leader, false),
                    new GuildPartyTeleportTarget(member, false)
                };
                Check("a policy-excluded town can consume a use without persisting transient coordinates",
                    GuildPartyTeleportCommitService.TryCommit(leader, guildId, transientTargets,
                        31, 1, 999, 999, () => true, out remaining) && remaining == 1
                    && Position(961) == (2, 2, 100, 30) && Position(962) == (2, 2, 100, 30));
                Check("the last use commits then removes the eligibility row",
                    Commit(300, () => true, out remaining) && remaining == 0 && Uses() == 0
                    && Position(961) == (2, 2, 300, 30) && Position(962) == (2, 2, 300, 30));
                Check("missing quota cannot move any member",
                    !Commit(400, () => true, out _) && Position(961) == (2, 2, 300, 30)
                    && Position(962) == (2, 2, 300, 30));

                SetUses(2);
                using var queuedSourceRelease = new SemaphoreSlim(0, 1);
                async Task<bool> ResumeCapturedRequestAsync()
                {
                    await queuedSourceRelease.WaitAsync();
                    // targets were captured before this wait. A replacement
                    // lease must not be substituted into the old request.
                    return Commit(400, () => true, out _);
                }
                var queuedRequest = ResumeCapturedRequestAsync();
                InventoryContext.Register(memberSession, new InventoryService(962, 961, database));
                queuedSourceRelease.Release();
                Check("a queued request resumes with its original captured member lease and refuses replacement",
                    !queuedRequest.GetAwaiter().GetResult() && Uses() == 2
                    && Position(961) == (2, 2, 300, 30) && Position(962) == (2, 2, 300, 30));
                Check("a replaced member inventory lease rejects the complete party operation",
                    !Commit(400, () => true, out _) && Uses() == 2
                    && Position(961) == (2, 2, 300, 30));
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                failed++;
            }
            finally
            {
                InventoryContext.Unregister(leaderSession);
                InventoryContext.Unregister(memberSession);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"GUILD_PARTY_TELEPORT_COMMIT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static int Gold(InventoryLease lease)
        {
            lock (lease.SyncRoot)
                return lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
