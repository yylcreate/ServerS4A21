using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace DfoServer.SelfTests
{
    internal static class GuildContentSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string label, bool result)
            {
                Console.WriteLine($"[{(result ? "PASS" : "FAIL")}] {label}");
                if (result) passed++; else failed++;
            }
            var path = Path.Combine(Path.GetTempPath(), $"guild-content-{Guid.NewGuid():N}.db");
            var sessionId = Guid.NewGuid();
            var memberSessionId = Guid.NewGuid();
            try
            {
                string source = GameWorld.PvfArchiveAccessor.ReadText("etc/(r)guild.etc");
                var resource = GuildAdditionalContentsConfigFile.Parse(source);
                Check("donation unit uses the nested guild contribute resource definition", resource.IsValid
                    && resource.DonationUnit > 0 && GuildDonationService.ReadDonationUnit() == resource.DonationUnit);
                var malformedUnit = GuildAdditionalContentsConfigFile.Parse(source.Replace("[guild donate]",
                    "[guild donate]\n0\n[guild donate]", StringComparison.Ordinal));
                Check("a duplicated donation scalar invalidates the complete resource", !malformedUnit.IsValid);
                var catalog = GuildContentCatalog.Parse(source);
                Check("both complete content tables are adapted through PvfLib", catalog.Count ==
                    resource.Contents.Count + resource.AdditionalContents.Count);
                var exp = catalog.Values.First(row => row.Category == GuildContentCatalog.ExperienceCategory);
                var teleport = catalog.Values.First(row => row.Category == GuildContentCatalog.PartyTeleportCategory);
                var stat = catalog.Values.First(row => row.Category == GuildStatBuffRule.TemporaryStatCategory);
                var goldProduct = catalog.Values.First(row => row.CostsGold);
                Check("count products use configured counts and a signed-client-safe permanent expiry",
                    teleport.UseCount > 0 && teleport.StorageDays == -1
                    && GuildContentDefinition.PermanentExpiryUnix == int.MaxValue);

                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((c, t) => Execute(c, t, @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(951,'guild-content','');
INSERT INTO characters(character_id,account_id,name) VALUES
(951,951,'guild-content-leader'),(952,951,'guild-content-member'),(953,951,'guild-content-next');"));
                var repository = new GuildRepository(database);
                var guild = database.Write((c, t) => GuildRepository.Insert(c, t, 951, "guild-content", ""));
                repository.Apply(guild.Id, 952);
                repository.Approve(guild.Id, 951, 952);
                var nextGuild = database.Write((c, t) => GuildRepository.Insert(c, t, 953, "guild-content-next", ""));
                var inventory = new InventoryService(951, 951, database);
                var lease = InventoryContext.Register(sessionId, inventory);
                var memberLease = InventoryContext.Register(memberSessionId, new InventoryService(952, 951, database));
                Check("inventory baseline saves through the existing lifecycle", SetGold(lease, 20000000)
                    && InventoryPersistenceService.SaveDirty(lease));
                var donation = new GuildDonationService(repository, resource.DonationUnit.Value);
                var purchase = new GuildContentPurchaseService(repository);
                uint amount = checked((uint)resource.DonationUnit.Value * 10000u);
                Check("donation debits complete gold and credits converted spendable points", donation.Donate(lease, amount, guild.Id)
                    == GuildResult.Success && Gold(lease) == 20000000 - amount
                    && repository.Get(guild.Id).Points == 10000);
                Check("donation rejects nonmultiples and zero without losing gold", donation.Donate(lease, 0) == GuildResult.InvalidAmount
                    && donation.Donate(lease, 1) == GuildResult.InvalidAmount && repository.Get(guild.Id).Points == 10000);
                Check("a captured different membership rejects donation", donation.Donate(lease,
                    (uint)resource.DonationUnit.Value, nextGuild.Id) == GuildResult.StaleSession);
                Check("member rank does not gain leader purchase authority", purchase.Purchase(memberLease,
                    (uint)exp.RowNumber, guild.Id) == GuildResult.NotLeader);
                Check("purchase resolves server resource price and persistent category", purchase.Purchase(lease,
                    (uint)exp.RowNumber, guild.Id) == GuildResult.Success
                    && repository.Get(guild.Id).Points == 10000 - exp.Price
                    && repository.GetRosterForMember(951).PurchasedContents.Any(row => row.Category == exp.Category));
                var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Check("guild experience is configured and does not stack duplicate source contents",
                    GuildExperienceBonusProvider.ResolvePercent(new[] {
                        new GuildPurchasedContent((byte)exp.Category, now + 100),
                        new GuildPurchasedContent((byte)exp.Category, now + 100) }, now) == exp.ExperienceBonusPercent);
                Check("expired guild experience contributes zero", GuildExperienceBonusProvider.ResolvePercent(new[] {
                    new GuildPurchasedContent((byte)exp.Category, now) }, now) == 0);
                Check("experience reads current member and purchased contents together", GuildExperienceBonusProvider
                    .GetMonsterExpBonusPercent(database.ConnectionString, 952) == exp.ExperienceBonusPercent);

                Check("count purchase and repeated purchase accumulate exact configured uses", purchase.Purchase(lease,
                    (uint)teleport.RowNumber, guild.Id) == GuildResult.Success
                    && purchase.Purchase(lease, (uint)teleport.RowNumber, guild.Id) == GuildResult.Success
                    && repository.GetRosterForMember(951).PurchasedContents.Single(row => row.Category == teleport.Category)
                        .RemainingUses == (uint)teleport.UseCount * 2);
                Check("wrong captured guild cannot consume a use", GuildRepository.ConsumeContentUse(database, 951,
                    teleport.Category, nextGuild.Id) == -1);
                database.Write((c, t) => Execute(c, t, "UPDATE guild_contents SET expires_at=1 WHERE category=7;"));
                Check("expired purchased contents cannot be consumed or projected", GuildRepository.ConsumeContentUse(database,
                    951, teleport.Category, guild.Id) == -1
                    && repository.GetRosterForMember(951).PurchasedContents.All(row => row.Category != teleport.Category));
                Check("renewal discards expired uses instead of reviving them", purchase.Purchase(lease,
                    (uint)teleport.RowNumber, guild.Id) == GuildResult.Success
                    && repository.GetRosterForMember(951).PurchasedContents.Single(row => row.Category == teleport.Category)
                        .RemainingUses == teleport.UseCount);
                for (int index = teleport.UseCount - 1; index >= 0; index--)
                    Check("one committed use advances the durable count to " + index,
                        GuildRepository.ConsumeContentUse(database, 951, teleport.Category, guild.Id) == index);
                Check("last use removes the client eligibility row and rejects further consumption",
                    repository.GetRosterForMember(951).PurchasedContents.All(row => row.Category != teleport.Category)
                    && GuildRepository.ConsumeContentUse(database, 951, teleport.Category, guild.Id) == -1);

                uint pointsBefore = repository.Get(guild.Id).Points;
                int dirtyGold = Gold(lease) + 1234;
                SetGold(lease, dirtyGold);
                database.Write((c, t) => Execute(c, t, @"CREATE TRIGGER reject_content BEFORE INSERT ON guild_contents
WHEN NEW.category=3 BEGIN SELECT RAISE(ABORT,'guild content test failure'); END;"));
                Check("failed purchase rolls back points and preserves prior dirty inventory", purchase.Purchase(lease,
                    (uint)stat.RowNumber, guild.Id) == GuildResult.PersistenceFailed
                    && repository.Get(guild.Id).Points == pointsBefore && Gold(lease) == dirtyGold
                    && repository.GetRosterForMember(951).PurchasedContents.All(row => row.Category != stat.Category));
                database.Write((c, t) => Execute(c, t, "DROP TRIGGER reject_content;"));
                dirtyGold += 5678;
                SetGold(lease, dirtyGold);
                database.Write((c, t) => Execute(c, t, @"CREATE TRIGGER reject_points BEFORE UPDATE OF points ON guilds
BEGIN SELECT RAISE(ABORT,'guild point test failure'); END;"));
                Check("failed donation rolls back the debit and preserves prior dirty gold", donation.Donate(lease,
                    (uint)resource.DonationUnit.Value, guild.Id) == GuildResult.PersistenceFailed
                    && repository.Get(guild.Id).Points == pointsBefore && Gold(lease) == dirtyGold);
                database.Write((c, t) => Execute(c, t, "DROP TRIGGER reject_points;"));

                database.Write((c, t) => Execute(c, t, "UPDATE guilds SET points=4294967295 WHERE guild_id=" + guild.Id));
                Check("point overflow rejects the entire donation instead of clipping the credit", donation.Donate(lease,
                    (uint)resource.DonationUnit.Value, guild.Id) == GuildResult.InvalidAmount
                    && repository.Get(guild.Id).Points == uint.MaxValue && Gold(lease) == dirtyGold);
                database.Write((c, t) => Execute(c, t, "UPDATE guilds SET points=" + pointsBefore + " WHERE guild_id=" + guild.Id));
                SetGold(lease, checked((int)goldProduct.Price) + 100);
                Check("gold-priced content is charged in gold rather than guild points", purchase.Purchase(lease,
                    (uint)goldProduct.RowNumber, guild.Id) == GuildResult.Success && Gold(lease) == 100
                    && repository.Get(guild.Id).Points == pointsBefore);

                var previous = lease;
                lease = InventoryContext.Register(sessionId, inventory);
                Check("a replaced owned inventory lease cannot donate or buy", donation.Donate(previous,
                    (uint)resource.DonationUnit.Value, guild.Id) == GuildResult.StaleSession
                    && purchase.Purchase(previous, (uint)exp.RowNumber, guild.Id) == GuildResult.StaleSession);
                var mismatchedInventory = new InventoryService(951, 999, database);
                var mismatchedLease = InventoryContext.Register(sessionId, mismatchedInventory);
                SetGold(mismatchedLease, 999999);
                Check("an account mismatch cannot flush a forged inventory baseline or change content state",
                    donation.Donate(mismatchedLease, (uint)resource.DonationUnit.Value, guild.Id) == GuildResult.MissingCharacter
                    && purchase.Purchase(mismatchedLease, (uint)exp.RowNumber, guild.Id) == GuildResult.MissingCharacter
                    && database.Read(c => InventoryService.LoadFromDb(c, 951, 951))
                        .GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count == 100);
                // This test's forged inventory has no legitimate dirty owner. Do not
                // let unregister persist it as part of fixture cleanup.
                mismatchedInventory.ClearDirtyState();
                lease = InventoryContext.Register(sessionId, inventory);
                Check("content points preserve existing contribution and recommended channel owners",
                    repository.GetRosterForMember(951).GuildContribution.Total == 0
                    && repository.Get(guild.Id).RecommendedServerId == 0 && repository.Get(guild.Id).RecommendedChannelId == 0);

                VerifyFailureBoundaryExcludesConcurrentMutation(database, resource.DonationUnit.Value,
                    stat, Check);
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
                failed++;
            }
            finally
            {
                InventoryContext.Unregister(sessionId);
                InventoryContext.Unregister(memberSessionId);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"GUILD_CONTENT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static int Gold(InventoryLease lease)
        {
            lock (lease.SyncRoot)
                return lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static bool SetGold(InventoryLease lease, int gold)
        {
            lock (lease.SyncRoot)
                return lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, gold);
        }

        private static void Execute(SqliteConnection c, SqliteTransaction t, string sql)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void VerifyFailureBoundaryExcludesConcurrentMutation(GameDatabase database,
            int donationUnit, GuildContentDefinition product, Action<string, bool> check)
        {
            var probeSession = Guid.NewGuid();
            try
            {
                database.Write((c, t) => Execute(c, t, @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(954,'guild-content-lock-probe','');
INSERT INTO characters(character_id,account_id,name) VALUES(954,954,'guild-content-lock-probe');"));
                int probeGuildId = database.Write((c, t) =>
                    GuildRepository.Insert(c, t, 954, "guild-lock-probe", "").Id);
                uint pointsBefore = checked(product.Price + 1000000u);
                database.Write((c, t) => Execute(c, t,
                    "UPDATE guilds SET points=" + pointsBefore + " WHERE guild_id=" + probeGuildId));
                var probeDatabase = new CommitBoundaryProbeDatabase(database);
                var probeLease = InventoryContext.Register(probeSession,
                    new InventoryService(954, 954, probeDatabase));
                var repository = new GuildRepository(probeDatabase);
                var donation = new GuildDonationService(repository, donationUnit);
                var purchase = new GuildContentPurchaseService(repository);
                const int dirtyGold = 12345678;
                SetGold(probeLease, dirtyGold);
                database.Write((c, t) => Execute(c, t, @"
CREATE TRIGGER reject_lock_probe_donation BEFORE UPDATE OF points ON guilds
WHEN OLD.guild_id=" + probeGuildId + @"
BEGIN SELECT RAISE(ABORT,'guild donation boundary failure'); END;"));
                probeDatabase.Arm(probeLease);
                var donationResult = donation.Donate(probeLease, checked((uint)donationUnit), probeGuildId);
                check("donation baseline, debit and rollback exclude a competing online mutation",
                    donationResult == GuildResult.PersistenceFailed && probeDatabase.ProbeCount == 1
                    && !probeDatabase.CompetingMutationEntered && Gold(probeLease) == dirtyGold
                    && repository.Get(probeGuildId).Points == pointsBefore);
                database.Write((c, t) => Execute(c, t, "DROP TRIGGER reject_lock_probe_donation;"));

                SetGold(probeLease, dirtyGold + 1);
                database.Write((c, t) => Execute(c, t, @"
CREATE TRIGGER reject_lock_probe_purchase BEFORE INSERT ON guild_contents
WHEN NEW.guild_id=" + probeGuildId + @"
BEGIN SELECT RAISE(ABORT,'guild purchase boundary failure'); END;"));
                probeDatabase.Arm(probeLease);
                var purchaseResult = purchase.Purchase(probeLease, checked((uint)product.RowNumber), probeGuildId);
                check("purchase baseline, charge and rollback exclude a competing online mutation",
                    purchaseResult == GuildResult.PersistenceFailed && probeDatabase.ProbeCount == 1
                    && !probeDatabase.CompetingMutationEntered && Gold(probeLease) == dirtyGold + 1
                    && repository.Get(probeGuildId).Points == pointsBefore
                    && repository.GetRosterForMember(954).PurchasedContents.Count == 0);
                database.Write((c, t) => Execute(c, t, "DROP TRIGGER reject_lock_probe_purchase;"));
            }
            finally
            {
                InventoryContext.Unregister(probeSession);
            }
        }

        // Observe the real database-open boundary after SaveDirty has committed
        // its baseline and before the existing mutation coordinator begins.
        // Read/Write delegate unchanged; no production timing hook is needed.
        private sealed class CommitBoundaryProbeDatabase : IGameDatabase
        {
            private readonly IGameDatabase _inner;
            private InventoryLease _lease;
            private int _openCount;
            internal int ProbeCount { get; private set; }
            internal bool CompetingMutationEntered { get; private set; }

            internal CommitBoundaryProbeDatabase(IGameDatabase inner) => _inner = inner;
            public string DatabasePath => _inner.DatabasePath;
            public string SchemaFilePath => _inner.SchemaFilePath;
            public string ConnectionString => _inner.ConnectionString;

            internal void Arm(InventoryLease lease)
            {
                _lease = lease;
                _openCount = 0;
                ProbeCount = 0;
                CompetingMutationEntered = false;
            }

            public SqliteConnection OpenConnection()
            {
                if (_lease != null && ++_openCount == 2)
                {
                    ProbeCount++;
                    Exception error = null;
                    var contender = new Thread(() =>
                    {
                        bool entered = false;
                        try
                        {
                            Monitor.TryEnter(_lease.SyncRoot, 0, ref entered);
                            CompetingMutationEntered = entered;
                            if (entered)
                            {
                                int gold = Gold(_lease);
                                if (!SetGold(_lease, checked(gold + 777)))
                                    throw new InvalidOperationException("The competing mutation was refused.");
                            }
                        }
                        catch (Exception exception) { error = exception; }
                        finally { if (entered) Monitor.Exit(_lease.SyncRoot); }
                    }) { IsBackground = true };
                    contender.Start();
                    if (!contender.Join(5000))
                        throw new InvalidOperationException("The nonblocking mutation probe did not finish.");
                    if (error != null) throw new InvalidOperationException("The mutation probe failed.", error);
                }
                return _inner.OpenConnection();
            }

            public T Read<T>(Func<SqliteConnection, T> action) => _inner.Read(action);
            public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool immediate = true)
                => _inner.Write(action, immediate);
            public void Write(Action<SqliteConnection, SqliteTransaction> action, bool immediate = true)
                => _inner.Write(action, immediate);
        }
    }
}
