using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuildDungeonProgressSelfTest
    {
        private const int Leader = 85101;
        private const int Member = 85102;
        private const int OtherLeader = 85103;
        private const int Outsider = 85104;
        private const int HeaderLength = 15;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        internal static int Run()
        {
            Console.WriteLine("=== GUILD_DUNGEON_PROGRESS selftest ===");
            int assertions = 0, failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                if (!passed) failures++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            }
            void Section(string label, Action<Fixture> action)
            {
                try { using var fixture = new Fixture(); action(fixture); }
                catch (Exception exception) { Check(label + ": " + exception, false); }
            }
            Section("strict current resource definitions", VerifyResources);
            Section("registered schema36 month migration", VerifyMigration);
            Section("registered schema37 guild contribution migration", f => CheckContributionMigration(f, Check));
            Section("persistent guild ownership and prerequisite thresholds", VerifyProgress);
            Section("malformed storage fails closed", VerifyMalformedStorage);
            Section("final zero-cost entry transaction", VerifyEntryCommit);
            Section("shared clear fact and durable idempotency", VerifySharedClear);
            Section("one-to-four frozen guild clear participants", VerifyWeightedClear);
            Section("current PVF reward bounds and frozen v4 receipts", VerifyGuildRewards);
            Section("guild and personal contribution transaction", f => CheckContributions(f, Check));
            Section("remaining participants preserve canonical clear owner", f => CheckRemainingGuildParticipant(f, Check));
            Section("production clear roster preserves registered and fallback accounts", f => CheckCapturedAccountsAsync(f, Check).GetAwaiter().GetResult());
            Section("owned guild free-card grants and recovery", VerifyGuildRewardCards);
            Section("calendar month ownership and legacy durable receipts", VerifyMonth);
            Section("atomic rollback and existing registered recovery", VerifyRecovery);
            Section("disband cascade and accepted no-op clear", VerifyDisband);
            Section("real socket progress and current-source fences", VerifyNotifier);
            Section("real socket contribution refresh and source roster", f => CheckContributionNotifierAsync(f, Check).GetAwaiter().GetResult());
            Section("real socket calendar month reprojection", VerifyMonthNotifier);
            Section("real selection continuation after guild progress", VerifyEntrySelection);
            try { VerifyPacket(Check); }
            catch (Exception exception) { Check("progress packet: " + exception, false); }
            Console.WriteLine($"GUILD_DUNGEON_PROGRESS assertions={assertions} failures={failures}");
            return failures;

            void VerifyResources(Fixture f) => CheckResources(f, Check);
            void VerifyMigration(Fixture f) => CheckMigration(f, Check);
            void VerifyProgress(Fixture f) => CheckProgress(f, Check);
            void VerifyMalformedStorage(Fixture f) => CheckMalformedStorage(f, Check);
            void VerifyEntryCommit(Fixture f) => CheckEntryCommit(f, Check);
            void VerifySharedClear(Fixture f) => CheckSharedClear(f, Check);
            void VerifyWeightedClear(Fixture f) => CheckWeightedClear(f, Check);
            void VerifyGuildRewards(Fixture f) => CheckGuildRewards(f, Check);
            void VerifyGuildRewardCards(Fixture f) => CheckGuildRewardCards(f, Check);
            void VerifyMonth(Fixture f) => CheckMonth(f, Check);
            void VerifyRecovery(Fixture f) => CheckRecovery(f, Check);
            void VerifyDisband(Fixture f) => CheckDisband(f, Check);
            void VerifyNotifier(Fixture f) => CheckNotifierAsync(f, Check).GetAwaiter().GetResult();
            void VerifyMonthNotifier(Fixture f) => CheckMonthNotifierAsync(f, Check).GetAwaiter().GetResult();
            void VerifyEntrySelection(Fixture f) => CheckEntrySelectionAsync(f, Check).GetAwaiter().GetResult();
        }

        private static void CheckResources(Fixture f, Action<string, bool> check)
        {
            var expected = new[] { 8500, 8503, 8504, 8505, 8507, 8508 };
            var config = f.Service.Catalog.Config;
            check("current ETC, six typed DGN markers and physical directory agree",
                config.IsValid && config.Dungeons.Select(row => row.DungeonId).OrderBy(id => id).SequenceEqual(expected)
                && WorldMap.GetAreaById(f.Service.Catalog.WorldMapAreaId).Dungeons.Select(row => row.DungeonId)
                    .OrderBy(id => id).SequenceEqual(expected)
                && expected.All(id => DungeonCatalog.GetDungeonFile(id).GuildDungeon == true
                    && DungeonCatalog.GetDungeonFile(id).GuildDungeonDefinitionValid
                    && DungeonCatalog.GetDungeonFile(id).GetIntValue("guild dungeon") == 1));
            check("8500 alone has an empty prerequisite list in this resource batch",
                config.Dungeons.Where(row => row.RequiredDungeonConquest.Count == 0).Select(row => row.DungeonId)
                    .SequenceEqual(new[] { 8500 }));
            foreach (var (label, text) in new[]
            {
                ("empty marker", "[guild dungeon]\n"),
                ("quoted marker", "[guild dungeon]\n`1`\n"),
                ("extra marker value", "[guild dungeon]\n1 0\n"),
                ("out-of-range marker", "[guild dungeon]\n2\n"),
                ("invalid marker token", "[guild dungeon]\nbad\n"),
                ("overflow marker", "[guild dungeon]\n2147483648\n"),
                ("nested marker", "[guild dungeon]\n[nested]\n1\n[/nested]\n[/guild dungeon]\n"),
                ("duplicate marker", "[guild dungeon]\n1\n[guild dungeon]\n1\n")
            })
            {
                var dungeon = DungeonFile.Parse(text);
                check(label + " cannot produce a valid guild classification",
                    !dungeon.GuildDungeonDefinitionValid && dungeon.GuildDungeon == null);
            }
            var absent = DungeonFile.Parse("[name]\n`ordinary`\n");
            var zero = DungeonFile.Parse("[guild dungeon]\n0\n");
            var one = DungeonFile.Parse("[guild dungeon]\n1\n");
            check("absent, explicit zero and explicit one remain distinct typed facts",
                absent.GuildDungeonDefinitionValid && absent.GuildDungeon == null
                && zero.GuildDungeonDefinitionValid && zero.GuildDungeon == false
                && one.GuildDungeonDefinitionValid && one.GuildDungeon == true);
            check("current guild ownership is excluded from account difficulty persistence",
                expected.All(id => !DungeonPermissionScopePolicy.IsAccountDifficulty(id)));

            const string minimal = "[weekly reward]\n[final dungeon]\n8500\n[/weekly reward]\n"
                + "[dungeon]\n[dungeon index]\n8500\n[gauge limit]\n10000\n[gauge on clear]\n50\n"
                + "[conquest gauge ratio]\n50\n[color]\n`red`\n[type]\n`normal`\n"
                + "[reward]\n[gauge ratio]\n0\n[item]\n10154439 1\n[contribute]\n0\n[/gauge ratio]\n[/reward]\n[/dungeon]\n"
                + "[repeat clear bonus reward]\n10154439 1\n[level difference]\n10\n[delete item]\n10154439\n"
                + "[party buff list]\n2 205\n3 206\n4 207\n[/party buff list]\n";
            check("a minimal complete numeric ETC retains all party-buff rows", GuildDungeonConfigFile.Parse(minimal).IsValid
                && GuildDungeonConfigFile.Parse(minimal).PartyBuffList.SequenceEqual(new[] { 2, 205, 3, 206, 4, 207 }));
            foreach (var (label, input) in new[]
            {
                ("quoted numeric field", minimal.Replace("[gauge limit]\n10000", "[gauge limit]\n`10000`")),
                ("bad numeric field", minimal.Replace("[gauge on clear]\n50", "[gauge on clear]\nbad")),
                ("incomplete reward pair", minimal.Replace("[item]\n10154439 1", "[item]\n10154439")),
                ("duplicate scalar", minimal.Replace("[gauge limit]\n10000", "[gauge limit]\n10000\n[gauge limit]\n10000")),
                ("missing required scalar", minimal.Replace("[gauge limit]\n10000\n", string.Empty)),
                ("unclosed container", minimal.Replace("[/reward]\n", string.Empty)),
                ("stray closing tag", minimal + "[/unknown]\n"),
                ("unknown nested field", minimal.Replace("[gauge on clear]\n50", "[gauge on clear]\n[nested]\n50\n[/nested]\n[/gauge on clear]"))
            })
            {
                var parsed = GuildDungeonConfigFile.Parse(input);
                check(label + " rejects the complete ETC without exposing partial dungeon rows",
                    !parsed.IsValid && parsed.Dungeons.Count == 0 && !string.IsNullOrWhiteSpace(parsed.InvalidReason));
            }
        }

        private static void CheckProgress(Fixture f, Action<string, bool> check)
        {
            check("new baseline includes registered schema36 and a mandatory explicit calendar month",
                f.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && SqliteMigrations.CurrentVersion >= 36
                && f.Scalar("SELECT COUNT(*) FROM guild_dungeon_progress;") == 0
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_dungeon_progress') WHERE name='month_id' AND \"notnull\"=1 AND dflt_value IS NULL;") == 1);
            foreach (var rule in f.Service.Catalog.Rules.Values)
                check("empty guild progress admits only the resource's prerequisite-free dungeon " + rule.DungeonId,
                    f.Prepare(Leader, rule.DungeonId, out _) == (rule.RequiredDungeonConquest.Count == 0));
            var target = f.Service.Catalog.Rules.Values.First(row => row.RequiredDungeonConquest.Count == 1);
            var prerequisite = f.Service.Catalog.Rules[target.RequiredDungeonConquest[0]];
            int threshold = RequiredClears(prerequisite, f.Service.RequiredRatio(prerequisite));
            if (threshold <= 0) throw new InvalidDataException("Current prerequisite threshold must be positive.");
            f.SetCount(f.GuildA.Id, prerequisite.DungeonId, threshold - 1);
            check("one clear below the resource-derived purification threshold remains locked",
                !f.Prepare(Leader, target.DungeonId, out _));
            f.SetCount(f.GuildA.Id, prerequisite.DungeonId, threshold);
            check("the exact resource-derived threshold opens the dependent dungeon",
                f.Prepare(Leader, target.DungeonId, out _));
            check("the default operating gate and the client both use the prerequisite's current PVF threshold",
                f.Service.RequiredRatio(prerequisite) == prerequisite.ConquestGaugeRatio
                && prerequisite.ConquestGaugeRatio == 50 && threshold == 100
                && prerequisite.GaugeOnClear * 100m / prerequisite.GaugeLimit == 0.5m);
            f.SetCount(f.GuildA.Id, prerequisite.DungeonId, 2);
            var normalPolicy = new GuildDungeonService(f.Database, () => f.UtcNow);
            check("one-percent progress no longer opens the ordinary service's fifty-percent resource prerequisite",
                normalPolicy.RequiredRatio(prerequisite) == 50
                && !f.Prepare(Leader, target.DungeonId, out _, normalPolicy));
            f.SetCount(f.GuildA.Id, prerequisite.DungeonId, threshold);
            var shared = f.Service.LoadForMember(Member, out int memberGuild);
            var separate = f.Service.LoadForMember(OtherLeader, out int otherGuild);
            check("same-guild characters share raw clears while another guild and a nonmember do not",
                memberGuild == f.GuildA.Id && shared[prerequisite.DungeonId] == threshold
                && otherGuild == f.GuildB.Id && separate.Count == 0
                && f.Service.LoadForMember(Outsider, out int noGuild).Count == 0 && noGuild == 0);
            f.Reopen();
            check("reopened database and recomposed service preserve the same guild count and gate",
                f.Service.LoadForMember(Leader, out int restoredGuild)[prerequisite.DungeonId] == threshold
                && restoredGuild == f.GuildA.Id && f.Prepare(Leader, target.DungeonId, out _));

            var conjunctive = f.Service.Catalog.Rules.Values.First(row => row.RequiredDungeonConquest.Count > 1);
            foreach (int id in conjunctive.RequiredDungeonConquest)
                f.SetCount(f.GuildA.Id, id, RequiredClears(f.Service.Catalog.Rules[id], f.Service.RequiredRatio(f.Service.Catalog.Rules[id])));
            check("all configured prerequisites together open the conjunctive dungeon",
                f.Prepare(Leader, conjunctive.DungeonId, out _));
            foreach (int id in conjunctive.RequiredDungeonConquest)
            {
                int needed = RequiredClears(f.Service.Catalog.Rules[id], f.Service.RequiredRatio(f.Service.Catalog.Rules[id]));
                if (needed <= 0) throw new InvalidDataException("Current AND prerequisite must have a positive threshold.");
                f.SetCount(f.GuildA.Id, id, needed - 1);
                check("an incomplete prerequisite " + id + " closes the AND gate despite completed siblings",
                    !f.Prepare(Leader, conjunctive.DungeonId, out _));
                f.SetCount(f.GuildA.Id, id, needed);
            }
            var resourcePolicy = f.CreateService(f.Database, unlockRatio: null);
            foreach (int id in conjunctive.RequiredDungeonConquest)
                f.SetCount(f.GuildA.Id, id, RequiredClears(f.Service.Catalog.Rules[id], resourcePolicy.RequiredRatio(f.Service.Catalog.Rules[id])));
            check("explicit resource policy restores the original fifty-percent conjunctive prerequisite",
                conjunctive.RequiredDungeonConquest.All(id => resourcePolicy.RequiredRatio(f.Service.Catalog.Rules[id]) == 50)
                && f.Prepare(Leader, conjunctive.DungeonId, out _, resourcePolicy));
            foreach (int id in conjunctive.RequiredDungeonConquest)
            {
                int needed = RequiredClears(f.Service.Catalog.Rules[id], resourcePolicy.RequiredRatio(f.Service.Catalog.Rules[id]));
                f.SetCount(f.GuildA.Id, id, needed - 1);
                check("resource policy preserves AND when one fifty-percent prerequisite " + id + " is incomplete",
                    !f.Prepare(Leader, conjunctive.DungeonId, out _, resourcePolicy));
                f.SetCount(f.GuildA.Id, id, needed);
            }
            var oldAdmission = f.RequireAdmission(Member);
            check("member withdrawal succeeds before switching persistent guild ownership",
                f.Guilds.RemoveMember(Member, Member) == GuildResult.Success);
            f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildB.Id, Member));
            check("joining another guild cannot inherit old clears or validate the old instance admission",
                f.Service.LoadForMember(Member, out int newGuild).Count == 0 && newGuild == f.GuildB.Id
                && !f.Service.ValidateParticipant(Member, Fixture.Account(Member), oldAdmission));
        }

        private static void CheckMigration(Fixture f, Action<string, bool> check)
        {
            f.DropContributionColumns();
            // Recreate the exact registered v35 shape: lowering its version
            // while retaining the already-added v36 column is not an upgrade.
            f.Sql("DROP TABLE guild_dungeon_progress; CREATE TABLE guild_dungeon_progress ("
                + "guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,"
                + "dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),"
                + "clear_count INTEGER NOT NULL DEFAULT 0 CHECK(clear_count>=0),PRIMARY KEY(guild_id,dungeon_id));"
                + $"INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count) VALUES({f.GuildA.Id},{f.FirstDungeon},7),({f.GuildB.Id},{f.FirstDungeon},19);"
                + "UPDATE schema_metadata SET schema_version=35 WHERE singleton_id=1; PRAGMA user_version=35;");
            check("the v35 fixture has original counts and genuinely lacks the calendar month column",
                f.Scalar("PRAGMA user_version;") == 35
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == 35
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_dungeon_progress') WHERE name='month_id';") == 0
                && f.Scalar("SELECT SUM(clear_count) FROM guild_dungeon_progress;") == 26);
            f.Sql("CREATE TRIGGER reject_guild_month_migration BEFORE UPDATE OF schema_version ON schema_metadata "
                + "WHEN NEW.schema_version=36 BEGIN SELECT RAISE(ABORT,'guild-month-migration-fault'); END;");
            bool rejected = false;
            try { using var connection = f.Database.OpenConnection(); SqliteMigrations.Apply(connection); }
            catch (SqliteException) { rejected = true; }
            check("a failed month migration rolls back its column and retains both schema35 versions and counts",
                rejected && f.Scalar("PRAGMA user_version;") == 35
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == 35
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_dungeon_progress') WHERE name='month_id';") == 0
                && f.Scalar("SELECT SUM(clear_count) FROM guild_dungeon_progress;") == 26);
            f.Sql("DROP TRIGGER reject_guild_month_migration;");
            int beforeMigrationMonth = GuildDungeonMonth.MonthId(DateTimeOffset.UtcNow);
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            int afterMigrationMonth = GuildDungeonMonth.MonthId(DateTimeOffset.UtcNow);
            check("registered v35 to v36 migration assigns preexisting progress to the adoption month without losing counts",
                f.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == SqliteMigrations.CurrentVersion
                && f.Scalar($"SELECT COUNT(*) FROM guild_dungeon_progress WHERE month_id IN ({beforeMigrationMonth},{afterMigrationMonth});") == 2
                && f.Scalar("SELECT SUM(clear_count) FROM guild_dungeon_progress;") == 26
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_dungeon_progress') WHERE name='month_id' AND \"notnull\"=1 AND dflt_value='197001';") == 1
                && f.Guilds.GetForMember(Member)?.Id == f.GuildA.Id && f.Guilds.GetForMember(OtherLeader)?.Id == f.GuildB.Id);
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            check("reapplying v36 preserves adopted progress and does not reassign its calendar month",
                f.Scalar($"SELECT COUNT(*) FROM guild_dungeon_progress WHERE month_id IN ({beforeMigrationMonth},{afterMigrationMonth});") == 2
                && f.Scalar("SELECT SUM(clear_count) FROM guild_dungeon_progress;") == 26);
            // Only this isolated fixture removes the v35 addition, retaining
            // the initialized baseline and every table belonging to v34.
            f.DropContributionColumns();
            f.Sql("DROP TABLE guild_dungeon_progress; UPDATE schema_metadata SET schema_version=34 WHERE singleton_id=1; PRAGMA user_version=34;");
            check("the isolated upgrade fixture really starts at schema34 without the guild progress table",
                f.Scalar("PRAGMA user_version;") == 34
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == 34
                && f.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_dungeon_progress';") == 0);
            // Bootstrap intentionally initializes each path once per process.
            // Reopening an already initialized fixture is not a fresh startup;
            // invoke the same registered migration entry used by Bootstrap.
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            f.Reopen();
            check("registered upgrade from the prior schema creates the guild table and advances both versions",
                f.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT COUNT(*) FROM guild_dungeon_progress;") == 0);
            check("migration preserves existing guilds and member ownership",
                f.Guilds.GetForMember(Leader)?.Id == f.GuildA.Id
                && f.Guilds.GetForMember(Member)?.Id == f.GuildA.Id
                && f.Guilds.GetForMember(OtherLeader)?.Id == f.GuildB.Id);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 7);
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            f.Reopen();
            check("reapplying registered migrations preserves both versions, progress and member ownership",
                f.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == SqliteMigrations.CurrentVersion
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 7
                && f.Scalar("SELECT COUNT(*) FROM guild_dungeon_progress;") == 1
                && f.Guilds.GetForMember(Leader)?.Id == f.GuildA.Id
                && f.Guilds.GetForMember(Member)?.Id == f.GuildA.Id
                && f.Guilds.GetForMember(OtherLeader)?.Id == f.GuildB.Id);
        }

        private static void CheckMalformedStorage(Fixture f, Action<string, bool> check)
        {
            foreach (object value in new object[] { "broken", 1.5, (long)int.MaxValue + 1, -1 })
            {
                f.SetCount(f.GuildA.Id, f.FirstDungeon, value, bypassChecks: value is int number && number < 0);
                check("invalid raw clear storage " + value + " fails both projection read and admission",
                    ThrowsInvalidData(() => f.Service.LoadForMember(Leader, out _))
                    && ThrowsInvalidData(() => f.Prepare(Leader, f.FirstDungeon, out _)));
                f.DeleteCount(f.GuildA.Id, f.FirstDungeon);
            }
            var admission = f.RequireAdmission(Leader);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, int.MaxValue);
            check("Int32 maximum clear count rejects increment instead of wrapping or capping to UI percent",
                ThrowsInvalidData(() => f.Database.Write((c, t) => GuildDungeonProgressRepository.ApplyClear(c, t, admission)))
                && f.Count(f.GuildA.Id, f.FirstDungeon) == int.MaxValue);
        }

        private static void CheckEntryCommit(Fixture f, Action<string, bool> check)
        {
            var leader = f.Lease(Leader);
            var member = f.Lease(Member);
            var canonical = f.RequireAdmission(Leader);
            var leaderPlan = new DungeonEntryCostPlan("guild-selftest-zero-cost") { GuildDungeonAdmission = canonical };
            var memberPlan = new DungeonEntryCostPlan("guild-selftest-zero-cost-member")
                { GuildDungeonAdmission = canonical.ForParticipant(Member, Fixture.Account(Member)) };
            var requests = new[] { new DungeonEntryCostCommitRequest(leader, leaderPlan), new DungeonEntryCostCommitRequest(member, memberPlan) };
            check("the existing zero-cost owner accepts both current guild participants",
                f.Costs.TryCommitPlans(requests, out var accepted) && accepted.Count == 2 && accepted.All(row => row.Success));
            int leaderGold = Gold(leader), memberGold = Gold(member);
            check("preparation succeeds before the asynchronous-window membership change",
                f.Costs.TryValidatePlan(member, memberPlan).Success);
            check("a current follower can withdraw while holding a previously prepared admission",
                f.Guilds.RemoveMember(Member, Member) == GuildResult.Success);
            check("final zero-cost group commit rejects a withdrawn follower before any inventory write",
                !f.Costs.TryCommitPlans(requests, out _) && Gold(leader) == leaderGold && Gold(member) == memberGold
                && f.StoredGold(Leader) == leaderGold && f.StoredGold(Member) == memberGold);
            f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildA.Id, Member));
            check("the restored member can prepare the same zero-cost plan before an account change",
                f.Costs.TryValidatePlan(member, memberPlan).Success);
            f.Sql("UPDATE characters SET account_id=205 WHERE character_id=" + Member + ";");
            check("the single-entry final transaction rejects a changed persistent account without a debit",
                !f.Costs.TryCommitPlan(member, memberPlan).Success && Gold(leader) == leaderGold && Gold(member) == memberGold
                && f.StoredGold(Member) == memberGold);
            f.Sql("UPDATE characters SET account_id=" + Fixture.Account(Member) + " WHERE character_id=" + Member + ";");
            check("unmodified peers can commit again after restoring the exact identity",
                f.Costs.TryCommitPlans(requests, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 0);
            var target = f.Service.Catalog.Rules.Values.First(rule =>
                rule.RequiredDungeonConquest.Count == 1 && rule.RequiredDungeonConquest[0] == f.FirstDungeon);
            var prerequisite = f.Service.Catalog.Rules[f.FirstDungeon];
            int requiredClears = RequiredClears(prerequisite, prerequisite.ConquestGaugeRatio);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, requiredClears);
            if (!f.Prepare(Leader, target.DungeonId, out var unlocked))
                throw new InvalidOperationException("The resource threshold fixture could not prepare the unlocked entry.");
            var thresholdPlan = new DungeonEntryCostPlan("guild-selftest-pvf-threshold-final") { GuildDungeonAdmission = unlocked };
            check("a prepared follow-up dungeon uses the original PVF prerequisite threshold",
                f.Costs.TryValidatePlan(leader, thresholdPlan).Success
                && unlocked.Prerequisites.Single().RequiredRatio == prerequisite.ConquestGaugeRatio);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, requiredClears - 1);
            check("the final zero-cost transaction rejects a prerequisite that changed below the PVF threshold after preparation",
                !f.Costs.TryCommitPlan(leader, thresholdPlan).Success && Gold(leader) == leaderGold
                && f.StoredGold(Leader) == leaderGold && f.Count(f.GuildA.Id, f.FirstDungeon) == requiredClears - 1);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 0);
            leaderPlan.GuildDungeonSourceCurrent = () => false;
            check("a stale source rejects zero-cost entry despite unchanged membership and progress",
                !f.Costs.TryCommitPlan(leader, leaderPlan).Success && Gold(leader) == leaderGold
                && f.StoredGold(Leader) == leaderGold && f.Count(f.GuildA.Id, f.FirstDungeon) == 0);
            int zeroFenceCalls = 0;
            leaderPlan.GuildDungeonSourceCurrent = () => ++zeroFenceCalls < 2;
            check("a zero-cost source lost at final commit is rechecked after the transaction work",
                !f.Costs.TryCommitPlan(leader, leaderPlan).Success && zeroFenceCalls == 2
                && Gold(leader) == leaderGold && f.StoredGold(Leader) == leaderGold);
            int paidFenceCalls = 0;
            var paid = new DungeonEntryCostPlan("guild-selftest-final-fence")
            {
                GuildDungeonAdmission = canonical,
                GuildDungeonSourceCurrent = () => ++paidFenceCalls < 2
            };
            if (!paid.TryAddGoldCost(17)) throw new InvalidOperationException("Fixture gold plan is invalid.");
            check("a source lost after saving an actual debit rolls back both online and stored inventory",
                !f.Costs.TryCommitPlan(leader, paid).Success && paidFenceCalls == 2
                && Gold(leader) == leaderGold && f.StoredGold(Leader) == leaderGold
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 0);
        }

        private static void CheckSharedClear(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            var instance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
            var left = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
            var right = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
            var fact = instance.GetOrCreateClearedFact(new DungeonClearIntent(
                DungeonEventEnvelope.Create(left, Leader, "guild-progress-selftest"), "selftest", 0), out bool first);
            var again = instance.GetOrCreateClearedFact(new DungeonClearIntent(
                DungeonEventEnvelope.Create(right, Member, "duplicate-participant-clear"), "selftest", 0), out bool second);
            check("two participants resolve one stable shared clear event and canonical source",
                first && !second && ReferenceEquals(fact, again)
                && fact.SourceEventId == again.SourceEventId
                && fact.Source.ForAffectedPlayer(right.CaptureIdentity(), null, Member).SourceEventId == fact.SourceEventId);
            var effect = f.Effect(admission, fact.SourceEventId);
            check("the first shared effect advances exactly one raw clear",
                f.Apply(effect, admission, out var committed) && committed.PreviousClearCount == 0 && committed.ClearCount == 1);
            string resultJson = f.Effects.Outbox.Get(effect).ResultJson;
            check("each participant retry using the canonical payload reads the same commit without another clear",
                f.Apply(effect, admission, out var replay) && replay.ClearCount == 1
                && f.Apply(effect, admission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 1
                && f.Effects.Outbox.Get(effect).AttemptCount == 1 && f.Effects.Outbox.Get(effect).ResultJson == resultJson);
            check("a caller cannot replace the canonical payload with its follower identity under the same effect key",
                !f.Apply(effect, admission.ForParticipant(Member, Fixture.Account(Member)), out _)
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 1 && f.Effects.Outbox.Get(effect).ResultJson == resultJson);
            check("a distinct shared fact increments normally and stays isolated from another guild",
                f.Apply(f.Effect(admission), admission, out var next) && next.ClearCount == 2
                && f.Count(f.GuildB.Id, f.FirstDungeon) == 0);
            f.Reopen();
            check("a reopened outbox returns the earlier frozen result while keeping subsequent progress",
                f.Apply(effect, admission, out var historical) && historical.ClearCount == 1
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 2 && f.Effects.Outbox.Get(effect).ResultJson == resultJson);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 10001);
            check("raw counts continue above 100 percent instead of being clamped to the displayed gauge",
                f.Apply(f.Effect(admission), admission, out var high) && high.PreviousClearCount == 10001 && high.ClearCount == 10002);
        }

        private static void CheckWeightedClear(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            int[] characters = { Leader, Member, Leader + 10, Leader + 11 };
            foreach (int cid in characters.Skip(2))
            {
                f.Sql($"INSERT INTO characters(character_id,account_id,name,level) VALUES({cid},{Fixture.Account(Leader)},'guild-weight-{cid}',70);");
                f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildA.Id, cid));
            }
            int total = 0;
            for (int members = 1; members <= 4; members++)
            {
                var instance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
                if (!instance.TryFreezeGuildDungeonAdmission(admission))
                    throw new InvalidOperationException("Weighted clear could not freeze its original guild admission.");
                var runs = Enumerable.Range(0, members).Select(_ =>
                    new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active)).ToArray();
                var fact = instance.GetOrCreateClearedFact(new DungeonClearIntent(
                    DungeonEventEnvelope.Create(runs[0], Leader, "guild-weight-selftest"), "selftest", 0), out _);
                if (members == 1)
                {
                    bool RejectsRoster(IReadOnlyList<DungeonParticipantRosterEntry> invalidRoster)
                    {
                        try { instance.GetOrCreateGuildDungeonContribution(fact, f.MonthId, invalidRoster); return false; }
                        catch (InvalidOperationException) { return true; }
                    }
                    check("an empty roster cannot freeze a contribution for the actual shared clear",
                        RejectsRoster(Array.Empty<DungeonParticipantRosterEntry>()));
                    check("a shared guild fact cannot infer an absent original participant account from current membership",
                        RejectsRoster(new[] { new DungeonParticipantRosterEntry(Leader, 100, runs[0], runs[0].CaptureIdentity(),
                            new DungeonRoomIdentity(runs[0].CaptureIdentity().InstanceIdentity, 1), 1) }));
                    var oversizedRoster = Enumerable.Range(0, 5).Select(i => new DungeonParticipantRosterEntry(
                        Leader + i, checked((ushort)(100 + i)), runs[0], runs[0].CaptureIdentity(),
                        new DungeonRoomIdentity(runs[0].CaptureIdentity().InstanceIdentity, 1), 1)).ToArray();
                    check("five distinct characters cannot freeze a contribution for the actual shared clear",
                        RejectsRoster(oversizedRoster));
                    var foreignInstance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
                    var foreignRun = new DungeonRun(foreignInstance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
                    var foreignRoster = new[] { new DungeonParticipantRosterEntry(Leader, 100, foreignRun,
                        foreignRun.CaptureIdentity(), new DungeonRoomIdentity(foreignRun.CaptureIdentity().InstanceIdentity, 1), 1) };
                    check("a participant from another physical instance cannot freeze the actual shared clear contribution",
                        RejectsRoster(foreignRoster));
                }
                var roster = runs.Select((run, i) => new DungeonParticipantRosterEntry(characters[i], checked((ushort)(100 + i)), run,
                    run.CaptureIdentity(), new DungeonRoomIdentity(run.CaptureIdentity().InstanceIdentity, 1), 1,
                    byte.MaxValue, i >= 2 ? Fixture.Account(Leader) : Fixture.Account(characters[i]))).ToList();
                // The effect journal deduplicates participant-run identity. A
                // repeated character represented by another run still weighs once.
                var duplicateRun = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
                roster.Add(new DungeonParticipantRosterEntry(Leader, 100, duplicateRun, duplicateRun.CaptureIdentity(),
                    new DungeonRoomIdentity(duplicateRun.CaptureIdentity().InstanceIdentity, 1), 1,
                    byte.MaxValue, Fixture.Account(Leader)));
                instance.ParticipantEffects.TryFreeze(fact.Source, DungeonParticipantEffectAudience.Instance, roster, out var frozenRoster);
                var contribution = instance.GetOrCreateGuildDungeonContribution(fact, f.MonthId, frozenRoster);
                var repeated = instance.GetOrCreateGuildDungeonContribution(fact,
                    GuildDungeonMonth.MonthId(f.UtcNow.AddMonths(1)), frozenRoster.Take(1).ToArray());
                check("the shared clear freezes " + members + " distinct guild characters and retains its original month on retry",
                    contribution.MemberCount == members && contribution.MonthId == f.MonthId
                    && contribution.SourceEventId == fact.SourceEventId && ReferenceEquals(contribution, repeated));
                var effect = f.Effect(admission, fact.SourceEventId);
                int before = total; total += members;
                check("a " + members + "-member shared fact commits its raw weight once for the entire guild",
                    f.Apply(effect, admission, out var committed, contribution)
                    && committed.PreviousClearCount == before && committed.ClearCount == total && committed.MonthId == f.MonthId
                    && f.Apply(effect, admission, out var replay, contribution) && replay.ClearCount == total
                    && f.Effects.Outbox.Get(effect).AttemptCount == 1 && f.Count(f.GuildA.Id, f.FirstDungeon) == total
                    && f.Service.LoadForMember(Member, out int guild)[f.FirstDungeon] == total && guild == f.GuildA.Id);
                decimal ratio = members * admission.GaugeOnClear * 100m / admission.GaugeLimit;
                check("the current injury-city resource projects " + members + " members as " + ratio + " percent without replacing its gauge rule",
                    admission.GaugeOnClear == 50 && ratio == members * 0.5m);
                check("a retry cannot change a frozen shared fact's accepted weight or month",
                    !f.Apply(effect, admission, out _, contribution with { MemberCount = members == 4 ? 3 : members + 1 })
                    && !f.Apply(effect, admission, out _, contribution with { MonthId = GuildDungeonMonth.MonthId(f.UtcNow.AddMonths(1)) })
                    && f.Count(f.GuildA.Id, f.FirstDungeon) == total);
            }
            var missing = f.Effect(admission);
            check("a new effect without a frozen shared contribution is rejected before an outbox receipt exists",
                !f.Effects.TryApplyGuildDungeonClear(missing, admission, out _, out _)
                && f.Effects.Outbox.Get(missing) == null && f.Count(f.GuildA.Id, f.FirstDungeon) == total);
            foreach (int invalid in new[] { 0, 5 })
            {
                var effect = f.Effect(admission);
                check("an invalid frozen contribution count " + invalid + " cannot create a durable clear",
                    !f.Apply(effect, admission, out _, new GuildDungeonClearContribution(effect.SourceEventId, f.MonthId, invalid))
                    && f.Effects.Outbox.Get(effect) == null);
            }
        }

        private static void CheckContributionMigration(Fixture f, Action<string, bool> check)
        {
            check("the new baseline gives every guild and member typed zero contribution owners",
                f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 0
                && f.Guilds.GetRosterForMember(Member, f.MonthId).RequesterContribution.Contribution.Total == 0);
            f.DropContributionColumns();
            f.Sql("UPDATE schema_metadata SET schema_version=36 WHERE singleton_id=1; PRAGMA user_version=36;"
                + "CREATE TRIGGER reject_guild_contribution_migration BEFORE UPDATE OF schema_version ON schema_metadata "
                + "WHEN NEW.schema_version=37 BEGIN SELECT RAISE(ABORT,'guild-contribution-migration-fault'); END;");
            bool rejected = false;
            try { using var connection = f.Database.OpenConnection(); SqliteMigrations.Apply(connection); }
            catch (SqliteException exception) when (exception.Message.Contains("guild-contribution-migration-fault")) { rejected = true; }
            check("schema37 failure rolls all six new contribution columns back and retains schema36 guild membership",
                rejected && f.Scalar("PRAGMA user_version;") == 36
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == 36
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guilds') WHERE name LIKE 'contribution_%';") == 0
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_members') WHERE name LIKE 'contribution_%';") == 0
                && f.Scalar("SELECT COUNT(*) FROM guild_members;") == 3);
            f.Sql("DROP TRIGGER reject_guild_contribution_migration;");
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            check("the registered schema36 upgrade adds both durable contribution owners without deleting guilds or members",
                f.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT schema_version FROM schema_metadata WHERE singleton_id=1;") == SqliteMigrations.CurrentVersion
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guilds') WHERE name LIKE 'contribution_%';") == 3
                && f.Scalar("SELECT COUNT(*) FROM pragma_table_info('guild_members') WHERE name LIKE 'contribution_%';") == 3
                && f.Guilds.GetRosterForMember(Member, f.MonthId).Guild.Id == f.GuildA.Id);
            f.Sql("UPDATE guilds SET contribution_total=40,contribution_month_value=40,contribution_month_id=" + f.MonthId + " WHERE guild_id=" + f.GuildA.Id + ";");
            using (var connection = f.Database.OpenConnection()) SqliteMigrations.Apply(connection);
            check("a repeated schema37 migration preserves committed cumulative and monthly contributions",
                f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution == new GuildContributionSnapshot(40, f.MonthId, 40));
        }

        private static void CheckContributions(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            var effect = f.Effect(admission);
            var participants = new[] { new GuildDungeonContributionParticipant(Leader, Fixture.Account(Leader)),
                new GuildDungeonContributionParticipant(Member, Fixture.Account(Member)) };
            var contribution = new GuildDungeonClearContribution(effect.SourceEventId, f.MonthId, 2) { Participants = participants };
            if (!f.Apply(effect, admission, out var result, contribution))
                throw new InvalidOperationException("The first real contribution clear could not commit.");
            var leader = f.Guilds.GetRosterForMember(Leader, f.MonthId);
            var member = f.Guilds.GetRosterForMember(Member, f.MonthId);
            check("one shared two-member clear commits PVF20 per member and PVF40 to its original guild with purification",
                result.Contribution?.Applied == true && result.Contribution.TotalBefore == 0 && result.Contribution.TotalAfter == 40
                && result.Contribution.AppliedMembers.Count == 2 && result.Contribution.AppliedMembers.All(row => row.TotalAfter == 20)
                && leader.GuildContribution == new GuildContributionSnapshot(40, f.MonthId, 40)
                && member.GuildContribution == leader.GuildContribution
                && leader.RequesterContribution.Contribution == new GuildContributionSnapshot(20, f.MonthId, 20)
                && member.RequesterContribution.Contribution == leader.RequesterContribution.Contribution
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 2);
            var history = f.Guilds.GetContributionHistoryForMember(Member, Fixture.Account(Member), 50);
            var receipt = f.Effects.Outbox.Get(effect);
            check("contribution details use the original committed clear and both frozen contributors rather than current totals",
                history?.GuildId == f.GuildA.Id && history.CharacterId == Member
                && history.Rows.Count == 2 && history.Rows.All(row => row.SourceEventId == effect.SourceEventId
                    && row.AccountId == Fixture.Account(row.CharacterId) && row.Amount == 20
                    && row.GrantedAt == DateTimeOffset.FromUnixTimeMilliseconds(receipt.CommittedAt.Value))
                && history.Rows.Select(row => row.CharacterId).OrderBy(id => id).SequenceEqual(new[] { Leader, Member }));
            check("contribution history never exposes another account or an absent membership",
                f.Guilds.GetContributionHistoryForMember(Member, 205, 50) == null
                && f.Guilds.GetContributionHistoryForMember(Outsider, Fixture.Account(Outsider), 50) == null);
            check("the original durable v4 result replays without adding either personal or guild contribution again",
                f.Apply(effect, admission, out var replay, contribution) && replay.Contribution.TotalAfter == 40
                && f.Guilds.GetRosterForMember(Member, f.MonthId).GuildContribution.Total == 40
                && f.Effects.Outbox.Get(effect).AttemptCount == 1
                && f.Guilds.GetContributionHistoryForMember(Member, Fixture.Account(Member), 50).Rows.Count == 2);
            var replaced = contribution with { Participants = new[] { participants[0], new GuildDungeonContributionParticipant(Member, 205) } };
            check("the same shared receipt rejects replacement of an original participant's account",
                !f.Apply(effect, admission, out _, replaced)
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 40);
            var solo = f.Effect(admission);
            if (!f.Apply(solo, admission, out _)) throw new InvalidOperationException("Solo contribution fixture could not commit.");
            leader = f.Guilds.GetRosterForMember(Leader, f.MonthId);
            member = f.Guilds.GetRosterForMember(Member, f.MonthId);
            var leaderPacket = GuildInfoPacketBuilder.Build(leader);
            var memberPacket = GuildInfoPacketBuilder.Build(member);
            check("the GUILD_INFO native tail maps shared guild totals but keeps each receiver's actual personal contribution",
                Tail(leaderPacket, 1) == 60 && Tail(leaderPacket, 2) == 60 && Tail(leaderPacket, 3) == 40 && Tail(leaderPacket, 4) == 40
                && Tail(memberPacket, 1) == 60 && Tail(memberPacket, 2) == 60 && Tail(memberPacket, 3) == 20 && Tail(memberPacket, 4) == 20
                && Enumerable.Range(5, 4).All(index => Tail(leaderPacket, index) == 0));

            f.SetCount(f.GuildA.Id, f.FirstDungeon, 200);
            if (!f.Apply(f.Effect(admission), admission, out var full)) throw new InvalidOperationException("Full contribution fixture could not commit.");
            check("full purification uses PVF15 for contribution rather than the certificate quantity or generic proper-clear10",
                full.RewardContribute == 15 && full.Contribution.Amount == 15
                && full.Contribution.TotalBefore == 60 && full.Contribution.TotalAfter == 75
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).RequesterContribution.Contribution.Total == 55);
            history = f.Guilds.GetContributionHistoryForMember(Leader, Fixture.Account(Leader), 50);
            check("contribution details preserve original 20 and 15 amounts even after current purification is changed",
                history.Rows.Count == 4 && history.Rows.Sum(row => row.Amount) == 75
                && history.Rows.Count(row => row.Amount == 15) == 1);
            f.Sql("CREATE TRIGGER reject_guild_score_receipt BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='guild-dungeon-clear-commit' AND NEW.state=2 BEGIN SELECT RAISE(ABORT,'guild-score-rollback'); END;");
            var pending = f.Effect(admission);
            var pendingContribution = contribution with { SourceEventId = pending.SourceEventId };
            check("a failure after personal and guild score writes rolls the scores and purification back together",
                !f.Apply(pending, admission, out _, pendingContribution) && f.Count(f.GuildA.Id, f.FirstDungeon) == 201
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 75
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).RequesterContribution.Contribution.Total == 55
                && f.Guilds.GetContributionHistoryForMember(Leader, Fixture.Account(Leader), 50).Rows.Count == 4);
            f.Sql("DROP TRIGGER reject_guild_score_receipt;");
            if (f.Guilds.RemoveMember(Member, Member) != GuildResult.Success) throw new InvalidOperationException("Member could not leave its original guild.");
            f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildB.Id, Member));
            f.Reopen();
            var recovered = f.Effects.RecoverCharacter(Leader);
            check("offline recovery credits the accepted guild once while a withdrawn participant cannot gain points in a new guild",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && f.Apply(pending, admission, out var restored, pendingContribution) && restored.Contribution.AppliedMembers.Count == 1
                && restored.Contribution.TotalBefore == 75 && restored.Contribution.TotalAfter == 105
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).RequesterContribution.Contribution.Total == 70
                && f.Guilds.GetRosterForMember(Member, f.MonthId).RequesterContribution.Contribution.Total == 0
                && f.Guilds.GetRosterForMember(Member, f.MonthId).GuildContribution.Total == 0
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 203);
            check("recovery adds only committed original-guild details and cannot move the former guild's history to a new membership",
                f.Guilds.GetContributionHistoryForMember(Leader, Fixture.Account(Leader), 50).Rows.Count == 6
                && f.Guilds.GetContributionHistoryForMember(Leader, Fixture.Account(Leader), 50).Rows.Sum(row => row.Amount) == 105
                && f.Guilds.GetContributionHistoryForMember(Member, Fixture.Account(Member), 50).GuildId == f.GuildB.Id
                && f.Guilds.GetContributionHistoryForMember(Member, Fixture.Account(Member), 50).Rows.Count == 0);

            int oldMonth = f.MonthId;
            f.UtcNow = f.UtcNow.AddMonths(1);
            leader = f.Guilds.GetRosterForMember(Leader, f.MonthId);
            check("the new operating month hides old monthly contribution while preserving cumulative guild and personal values",
                leader.GuildContribution == new GuildContributionSnapshot(105, f.MonthId, 0)
                && leader.RequesterContribution.Contribution == new GuildContributionSnapshot(70, f.MonthId, 0));
            check("a monthly projection change preserves real historical contribution details",
                f.Guilds.GetContributionHistoryForMember(Leader, Fixture.Account(Leader), 50).Rows.Count == 6);
            var late = f.Effect(admission);
            check("an old-month durable clear is a terminal no-op for both purification and contribution",
                f.Apply(late, admission, out var stale, new GuildDungeonClearContribution(late.SourceEventId, oldMonth, 1))
                && !stale.HasReward && stale.Contribution == null
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 105);
            var currentAdmission = f.RequireAdmission(Leader);
            f.Sql("UPDATE guild_members SET contribution_month_id=" + GuildDungeonMonth.MonthId(f.UtcNow.AddMonths(1))
                + " WHERE character_id=" + Leader + ";");
            var future = f.Effect(currentAdmission);
            check("a future personal contribution row rejects the entire current clear without overwriting it or incrementing purification",
                !f.Apply(future, currentAdmission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 0
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 105
                && f.Effects.Outbox.Get(future).State == DungeonPersistentEffectState.Failed);
            f.Sql("UPDATE guild_members SET contribution_month_id=" + oldMonth + ",contribution_total=2147483647 WHERE character_id=" + Leader + ";");
            var overflow = f.Effect(currentAdmission);
            check("personal contribution overflow fails the shared transaction without saturating or losing the original guild total",
                !f.Apply(overflow, currentAdmission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 0
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution.Total == 105
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).RequesterContribution.Contribution.Total == int.MaxValue);

            static int Tail(byte[] packet, int field) => BitConverter.ToInt32(packet, packet.Length - 32 + (field - 1) * 4);
        }

        private static void CheckRemainingGuildParticipant(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            var instance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
            if (!instance.TryFreezeGuildDungeonAdmission(admission)) throw new InvalidOperationException("Original admission could not freeze.");
            var run = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
            var fact = instance.GetOrCreateClearedFact(new DungeonClearIntent(
                DungeonEventEnvelope.Create(run, Member, "remaining-guild-participant"), "selftest", 0), out _);
            var roster = new[] { new DungeonParticipantRosterEntry(Member, 100, run, run.CaptureIdentity(),
                new DungeonRoomIdentity(instance.Identity, 1), 1, 0, Fixture.Account(Member)) };
            instance.ParticipantEffects.TryFreeze(fact.Source, DungeonParticipantEffectAudience.Instance, roster, out var frozen);
            var contribution = instance.GetOrCreateGuildDungeonContribution(fact, f.MonthId, frozen);
            var effect = f.Effect(admission, fact.SourceEventId);
            check("a remaining member can clear without rewarding the absent entry leader or changing the canonical outbox source",
                contribution.Participants.Count == 1 && contribution.Participants[0].CharacterId == Member
                && f.Apply(effect, admission, out var result, contribution)
                && result.Contribution.TotalAfter == 20 && result.Contribution.AppliedMembers.Single().CharacterId == Member
                && f.Effects.Outbox.Get(effect).CharacterId == Leader && f.Count(f.GuildA.Id, f.FirstDungeon) == 1
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).RequesterContribution.Contribution.Total == 0
                && f.Guilds.GetRosterForMember(Member, f.MonthId).RequesterContribution.Contribution.Total == 20);
        }

        private static async Task CheckCapturedAccountsAsync(Fixture f, Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            using var leader = await Peer.CreateAsync(f, sessions, Leader);
            using var member = await Peer.CreateAsync(f, sessions, Member);
            using var registry = new DungeonInstanceRegistry();
            var records = new SqliteCharacterRepository(f.Database);
            var daily = new DailyResetService(f.Database);
            var source = new SqliteSelectCharacterDataSource(f.Database, records, dailyResetService: daily);
            var refresh = new InventoryRefreshSender(source, records, f.Database);
            refresh.BindSessions(sessions);
            var services = new DungeonSharedServices(new ReviveCoinService(daily), records, source, null,
                f.Database.ConnectionString, refresh, sessions: sessions, database: f.Database,
                dailyResetService: daily, instanceRegistry: registry);
            var handler = new DungeonSettlementHandler(services, null);
            var capture = typeof(DungeonSettlementHandler).GetMethod("CaptureClearParticipantRoster",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("The production clear capture owner was not found.");
            var admission = f.RequireAdmission(Leader);
            var instance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
            if (!instance.TryFreezeGuildDungeonAdmission(admission))
                throw new InvalidOperationException("The production roster fixture could not freeze admission.");
            var key = new RoomKey(0, 0, 33900);
            var maze = new DfoServer.GameWorld.Dungeon.MazeSumInfo { Index = key.OverrideMapId, X = key.X, Y = key.Y };
            var room = instance.GetOrCreateRoom(key, id => new DungeonInstanceRoom(id, key, maze, 0), out _);
            room.TryActivate();
            var runs = new List<DungeonRun>();
            foreach (var peer in new[] { leader, member })
            {
                var run = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
                run.RoomKey = key;
                run.RoomStates[key] = new RoomState { InstanceRoom = room, Maze = maze };
                run.SetCurrentRoom(room);
                runs.Add(run);
                var fallback = (IReadOnlyList<DungeonParticipantRosterEntry>)capture.Invoke(handler, new object[] { peer.Session, run });
                check("production single-player fallback captures the real session account: " + peer.Session.Player.CharacterId,
                    fallback.Count == 1 && fallback[0].CharacterId == peer.Session.Player.CharacterId
                    && fallback[0].AccountId == peer.Session.Account.AccountId && fallback[0].RunIdentity.Equals(run.CaptureIdentity()));
            }
            for (int index = 0; index < runs.Count; index++)
            {
                var peer = index == 0 ? leader : member;
                registry.RegisterActive(new DungeonParticipantRegistration(peer.Session.Account.AccountId,
                    peer.Session.Player.CharacterId, peer.Session.Player.UserId, 0, peer.Session.SessionId, runs[index]));
            }
            var registered = registry.CaptureParticipantRoster(room.Identity);
            var captured = (IReadOnlyList<DungeonParticipantRosterEntry>)capture.Invoke(handler, new object[] { leader.Session, runs[0] });
            check("production room and instance captures preserve each registered account through settlement cloning",
                registered.Count == 2 && captured.Count == 2
                && registered.All(row => row.AccountId == Fixture.Account(row.CharacterId))
                && captured.All(row => row.AccountId == Fixture.Account(row.CharacterId) && row.AttachmentGeneration == 1));
            var fact = instance.GetOrCreateClearedFact(new DungeonClearIntent(
                DungeonEventEnvelope.Create(runs[0], Leader, "production-guild-roster"), "selftest", 0), out _);
            instance.ParticipantEffects.TryFreeze(fact.Source, DungeonParticipantEffectAudience.Instance, captured, out var frozen);
            var contribution = instance.GetOrCreateGuildDungeonContribution(fact, f.MonthId, frozen);
            check("the captured production roster can freeze and commit both original personal contributions",
                contribution.Participants.Count == 2 && f.Apply(f.Effect(admission, fact.SourceEventId), admission,
                    out var result, contribution) && result.Contribution.TotalAfter == 40
                && result.Contribution.AppliedMembers.Select(row => row.CharacterId).OrderBy(id => id)
                    .SequenceEqual(new[] { Leader, Member }));
        }

        private static void CheckGuildRewards(Fixture f, Action<string, bool> check)
        {
            foreach (var expected in new[]
            {
                (Dungeon: 8500, GaugeOnClear: 50, At99: 198, Before100: 199, At100: 200,
                    At99Percent: 99m, Before100Percent: 99.5m, At100Percent: 100m, LowerCount: 3, FullCount: 1),
                (Dungeon: 8504, GaugeOnClear: 25, At99: 396, Before100: 399, At100: 400,
                    At99Percent: 99m, Before100Percent: 99.75m, At100Percent: 100m, LowerCount: 3, FullCount: 1),
                (Dungeon: 8507, GaugeOnClear: 25, At99: 396, Before100: 399, At100: 400,
                    At99Percent: 99m, Before100Percent: 99.75m, At100Percent: 100m, LowerCount: 5, FullCount: 2),
                (Dungeon: 8505, GaugeOnClear: 7, At99: 1415, Before100: 1428, At100: 1429,
                    At99Percent: 99.05m, Before100Percent: 99.96m, At100Percent: 100.03m, LowerCount: 5, FullCount: 2),
                (Dungeon: 8503, GaugeOnClear: 7, At99: 1415, Before100: 1428, At100: 1429,
                    At99Percent: 99.05m, Before100Percent: 99.96m, At100Percent: 100.03m, LowerCount: 3, FullCount: 1),
                (Dungeon: 8508, GaugeOnClear: 7, At99: 1415, Before100: 1428, At100: 1429,
                    At99Percent: 99.05m, Before100Percent: 99.96m, At100Percent: 100.03m, LowerCount: 7, FullCount: 3)
            })
            {
                var rule = f.Service.Catalog.Rules[expected.Dungeon];
                decimal oneClearPercent = rule.GaugeOnClear * 100m / rule.GaugeLimit;
                decimal at99Percent = expected.At99 * oneClearPercent;
                decimal before100Percent = expected.Before100 * oneClearPercent;
                decimal at100Percent = expected.At100 * oneClearPercent;
                check("current PVF dungeon " + expected.Dungeon + " preserves the exact resource units and expected boundary percentages",
                    rule.GaugeLimit == 10000 && rule.GaugeOnClear == expected.GaugeOnClear
                    && at99Percent == expected.At99Percent && before100Percent == expected.Before100Percent
                    && at100Percent == expected.At100Percent
                    && decimal.Truncate(at99Percent) == 99 && decimal.Truncate(before100Percent) == 99
                    && decimal.Truncate(at100Percent) == 100
                    && (expected.At99 - 1) * oneClearPercent < 99
                    && before100Percent < 100 && (expected.At100 - 1) * oneClearPercent < 100);
                var lower = new GuildDungeonRewardChoice(99, 10154439, expected.LowerCount, 20);
                var full = new GuildDungeonRewardChoice(100, 10154439, expected.FullCount, 15);
                check("current PVF dungeon " + expected.Dungeon + " selects its 99 upper-bound row at zero, 99 and a fractional value below 100",
                    GuildDungeonRewardRules.Select(rule, 0) == lower
                    && GuildDungeonRewardRules.Select(rule, expected.At99) == lower
                    && GuildDungeonRewardRules.Select(rule, expected.Before100) == lower);
                check("current PVF dungeon " + expected.Dungeon + " selects its original 100 row at full purification and safely clamps large counts",
                    GuildDungeonRewardRules.Select(rule, expected.At100) == full
                    && GuildDungeonRewardRules.Select(rule, int.MaxValue) == full);
            }
            var rule0 = f.Service.Catalog.Rules[f.FirstDungeon];
            var reversed = GuildDungeonRewardRules.Freeze(rule0.Rewards).AsEnumerable().Reverse().ToArray();
            check("reward lookup orders resource keys and rejects incomplete or duplicated definitions without a guessed fallback",
                GuildDungeonRewardRules.Select(reversed, 10000, 50, 199) == new GuildDungeonRewardChoice(99, 10154439, 3, 20)
                && Throws<ArgumentOutOfRangeException>(() => GuildDungeonRewardRules.Select(rule0, -1))
                && Throws<InvalidOperationException>(() => GuildDungeonRewardRules.Select(
                    new[] { reversed[1] }, 10000, 50, 200))
                && Throws<InvalidOperationException>(() => GuildDungeonRewardRules.Validate(
                    new[] { reversed[0], reversed[0] })));
            var admission = f.RequireAdmission(Leader);
            var sourceRows = admission.Rewards.ToList();
            var frozenAdmission = WithRewards(sourceRows);
            sourceRows.Clear();
            check("real admission freezes the current typed reward rows and participant projection retains them",
                frozenAdmission.Rewards.Count == 2 && frozenAdmission.Rewards.SequenceEqual(rule0.Rewards)
                && frozenAdmission.ForParticipant(Member, Fixture.Account(Member)).Rewards.SequenceEqual(frozenAdmission.Rewards));
            var invalid = f.Effect(admission);
            check("an absent reward table rejects a new v4 intent before any durable receipt",
                !f.Apply(invalid, WithRewards(Array.Empty<GuildDungeonGaugeReward>()), out _)
                && f.Effects.Outbox.Get(invalid) == null);

            f.SetCount(f.GuildA.Id, f.FirstDungeon, 199);
            var effect = f.Effect(frozenAdmission);
            var contribution = new GuildDungeonClearContribution(effect.SourceEventId, f.MonthId, 4);
            if (!f.Apply(effect, frozenAdmission, out var crossing, contribution))
                throw new InvalidOperationException("Reward fixture could not commit its crossing clear.");
            check("a four-member clear crossing full purification awards one pre-clear row per participant rather than multiplying its item quantity",
                crossing.PreviousClearCount == 199 && crossing.ClearCount == 203 && crossing.HasReward
                && crossing.SourceEventId == effect.SourceEventId
                && crossing.RewardGaugeRatio == 99 && crossing.RewardItemId == 10154439
                && crossing.RewardCount == 3 && crossing.RewardContribute == 20);
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var accepted = f.Effects.Outbox.Get(effect);
            var acceptedPayload = JsonSerializer.Deserialize<GuildDungeonClearEffectPayload>(accepted.PayloadJson, jsonOptions);
            check("the v4 durable payload freezes both resource rows with the shared month and complete original identities",
                accepted.PayloadVersion == 4 && acceptedPayload.MonthId == f.MonthId && acceptedPayload.MemberCount == 4
                && acceptedPayload.Participants.Count == 4 && acceptedPayload.Participants[0].CharacterId == Leader
                && acceptedPayload.Rewards.SequenceEqual(GuildDungeonRewardRules.Freeze(rule0.Rewards)));
            string frozenResult = accepted.ResultJson;
            var changedRows = WithRewards(f.Service.Catalog.Rules[8507].Rewards);
            check("the same shared key rejects a changed resource reward table and returns its original result on an identical retry",
                !f.Apply(effect, changedRows, out _, contribution)
                && f.Apply(effect, frozenAdmission, out var replay, contribution)
                && replay.RewardGaugeRatio == 99 && replay.RewardCount == 3
                && f.Effects.Outbox.Get(effect).PayloadJson == accepted.PayloadJson
                && f.Effects.Outbox.Get(effect).ResultJson == frozenResult
                && f.Effects.Outbox.Get(effect).AttemptCount == 1 && f.Count(f.GuildA.Id, f.FirstDungeon) == 203);
            check("the next distinct full-purification clear selects the original 100 row",
                f.Apply(f.Effect(admission), admission, out var fullClear) && fullClear.PreviousClearCount == 203
                && fullClear.HasReward && fullClear.RewardGaugeRatio == 100
                && fullClear.RewardItemId == 10154439 && fullClear.RewardCount == 1 && fullClear.RewardContribute == 15);

            f.SetCount(f.GuildA.Id, f.FirstDungeon, 199);
            var pending = f.Effect(admission);
            f.Sql("CREATE TRIGGER reject_guild_reward_choice BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='guild-dungeon-clear-commit' AND NEW.state=2 BEGIN SELECT RAISE(ABORT,'guild-reward-choice'); END;");
            check("a failure after reward selection rolls the progress transaction back while retaining its accepted v4 resource table",
                !f.Apply(pending, admission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 199
                && f.Effects.Outbox.Get(pending).State == DungeonPersistentEffectState.Failed
                && f.Effects.Outbox.Get(pending).PayloadVersion == 4);
            string pendingPayload = f.Effects.Outbox.Get(pending).PayloadJson;
            check("a pending retry cannot replace its frozen resource rows before recovery",
                !f.Apply(pending, changedRows, out _) && f.Effects.Outbox.Get(pending).PayloadJson == pendingPayload
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 199);
            f.Sql("DROP TRIGGER reject_guild_reward_choice;");
            f.Reopen();
            var recovered = f.Effects.RecoverCharacter(Leader);
            check("registered recovery uses the accepted reward rows once and persists the same pre-clear choice",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && f.Effects.TryApplyGuildDungeonClear(pending, admission, out var recoveredResult, out _)
                && recoveredResult.PreviousClearCount == 199 && recoveredResult.ClearCount == 200
                && recoveredResult.HasReward && recoveredResult.RewardGaugeRatio == 99 && recoveredResult.RewardCount == 3
                && f.Effects.Outbox.Get(pending).PayloadJson == pendingPayload
                && f.Effects.RecoverCharacter(Leader).CommittedCount == 0);

            var legacy = f.Effect(admission);
            var legacyPayload = JsonSerializer.Serialize(new { admission.GuildId, CharacterId = admission.SourceCharacterId,
                AccountId = admission.SourceAccountId, admission.DungeonId, admission.Difficulty, admission.GaugeLimit,
                admission.GaugeOnClear, MonthId = f.MonthId, MemberCount = 4 }, jsonOptions);
            f.Effects.Outbox.Enqueue(new DungeonPersistentEffectDefinition { EffectId = legacy,
                CharacterId = Leader, AccountId = Fixture.Account(Leader), PayloadVersion = 2, PayloadJson = legacyPayload });
            var legacyRecovered = f.Effects.RecoverCharacter(Leader);
            check("a pending v2 receipt retains its original weighted progress contract without retroactive resource rewards",
                legacyRecovered.CommittedCount == 1 && legacyRecovered.FailedCount == 0
                && f.Effects.TryApplyGuildDungeonClear(legacy, admission, out var legacyResult, out _)
                && legacyResult.PreviousClearCount == 200 && legacyResult.ClearCount == 204 && !legacyResult.HasReward
                && f.Effects.Outbox.Get(legacy).PayloadVersion == 2 && f.Effects.Outbox.Get(legacy).PayloadJson == legacyPayload);
            check("an existing v2 receipt rejects a different frozen month or member count without promotion to v4",
                !f.Apply(legacy, admission, out _, new GuildDungeonClearContribution(legacy.SourceEventId, f.MonthId, 3))
                && !f.Apply(legacy, admission, out _, new GuildDungeonClearContribution(legacy.SourceEventId,
                    GuildDungeonMonth.MonthId(f.UtcNow.AddMonths(1)), 4))
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 204 && f.Effects.Outbox.Get(legacy).PayloadVersion == 2);
            var historic = f.Effect(admission);
            f.Effects.Outbox.Enqueue(new DungeonPersistentEffectDefinition { EffectId = historic,
                CharacterId = Leader, AccountId = Fixture.Account(Leader), PayloadVersion = 2, PayloadJson = legacyPayload });
            string historicJson = JsonSerializer.Serialize(new { admission.GuildId, admission.DungeonId,
                PreviousClearCount = 180, ClearCount = 184, MonthId = f.MonthId }, jsonOptions);
            f.Database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "UPDATE dungeon_persistent_effect_outbox SET state=2,result_version=1,result_json=@result,committed_at=@now WHERE source_event_id=@source;";
                command.Parameters.AddWithValue("@result", historicJson); command.Parameters.AddWithValue("@now", f.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@source", historic.SourceEventId.ToString("N")); command.ExecuteNonQuery();
            });
            check("a committed v2 result replays its historical count without inventing rewards or rewriting JSON",
                f.Effects.TryApplyGuildDungeonClear(historic, admission, out var historicResult, out _)
                && historicResult.ClearCount == 184 && !historicResult.HasReward
                && historicResult.SourceEventId == historic.SourceEventId
                && f.Effects.Outbox.Get(historic).ResultJson == historicJson && f.Count(f.GuildA.Id, f.FirstDungeon) == 204);
            var previousContribution = f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution;
            var priorV3 = f.Effect(admission);
            var priorPayload = new GuildDungeonClearEffectPayload
            {
                GuildId = admission.GuildId, CharacterId = admission.SourceCharacterId, AccountId = admission.SourceAccountId,
                DungeonId = admission.DungeonId, Difficulty = admission.Difficulty,
                GaugeLimit = admission.GaugeLimit, GaugeOnClear = admission.GaugeOnClear,
                MonthId = f.MonthId, MemberCount = 1, Rewards = GuildDungeonRewardRules.Freeze(admission.Rewards)
            };
            string priorJson = JsonSerializer.Serialize(priorPayload, jsonOptions);
            f.Effects.Outbox.Enqueue(new DungeonPersistentEffectDefinition { EffectId = priorV3, CharacterId = Leader,
                AccountId = Fixture.Account(Leader), PayloadVersion = 3, PayloadJson = priorJson });
            var priorRecovery = f.Effects.RecoverCharacter(Leader);
            check("a previously accepted v3 intent retains its item reward but never adds new retroactive guild contribution",
                priorRecovery.CommittedCount == 1 && priorRecovery.FailedCount == 0
                && f.Apply(priorV3, admission, out var priorResult) && priorResult.HasReward && priorResult.Contribution == null
                && f.Guilds.GetRosterForMember(Leader, f.MonthId).GuildContribution == previousContribution
                && f.Effects.Outbox.Get(priorV3).PayloadVersion == 3 && f.Effects.Outbox.Get(priorV3).PayloadJson == priorJson);

            GuildDungeonAdmissionSnapshot WithRewards(IReadOnlyList<GuildDungeonGaugeReward> rows)
                => new(admission.GuildId, admission.SourceCharacterId, admission.SourceAccountId, admission.DungeonId,
                    admission.Difficulty, admission.GaugeLimit, admission.GaugeOnClear, admission.Prerequisites, admission.MonthId, rows);
        }

        private static void CheckGuildRewardCards(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            var instance = new DungeonInstance(checked((short)admission.DungeonId), admission.Difficulty);
            if (!instance.TryFreezeGuildDungeonAdmission(admission))
                throw new InvalidOperationException("Card fixture could not freeze its real guild admission.");
            var left = CreateCardRun(instance, Leader, 0);
            var right = CreateCardRun(instance, Member, 1);
            var roster = new[] { Participant(left, Leader, 0), Participant(right, Member, 1) };
            instance.ParticipantEffects.TryFreeze(instance.ClearedFact.Source,
                DungeonParticipantEffectAudience.Instance, roster, out var frozenRoster);
            var contribution = instance.GetOrCreateGuildDungeonContribution(instance.ClearedFact, f.MonthId, frozenRoster);
            var shared = f.Effect(admission, instance.ClearedFact.SourceEventId);
            if (!f.Apply(shared, admission, out var result, contribution))
                throw new InvalidOperationException("Card fixture could not commit its shared guild result.");
            left.SettlementRuntime.FreezeGuildDungeonReward(left, result);
            var unreadySeats = DungeonSettlementHandler.BuildClearRewardFreeCardSeats(left, left.SettlementRuntime, frozenRoster);
            check("the production seat builder projects the frozen owner but leaves an unready guild peer completely empty",
                unreadySeats[0].ItemId == 10154439 && unreadySeats[0].ItemCount == 3 && unreadySeats[0].Gold == 50
                && unreadySeats[1].ItemId == 0 && unreadySeats[1].ItemCount == 0 && unreadySeats[1].Gold == 0);
            right.SettlementRuntime.FreezeGuildDungeonReward(right, result);
            var readySeats = DungeonSettlementHandler.BuildClearRewardFreeCardSeats(left, left.SettlementRuntime, frozenRoster);
            var peerView = DungeonSettlementHandler.BuildClearRewardFreeCardSeats(right, right.SettlementRuntime, frozenRoster);
            check("both real participant views use the same committed certificate row once the peer is frozen",
                readySeats[0].ItemId == 10154439 && readySeats[0].ItemCount == 3
                && readySeats[1].ItemId == 10154439 && readySeats[1].ItemCount == 3
                && peerView[0].ItemCount == 3 && peerView[1].ItemCount == 3
                && left.CardRewards[1].ItemId == 10154439 && left.CardRewards[1].StackCount == 3
                && right.CardRewards[1].StackCount == 3
                && !ReferenceEquals(left.SettlementRuntime.GuildDungeonClear, result));
            var leaderLease = f.Lease(Leader);
            var memberLease = f.Lease(Member);
            var cards = new CardRewardService(f.Effects);
            var leftEffect = CardRewardRules.GetEffectId(left, CardRewardSide.Free);
            var rightEffect = CardRewardRules.GetEffectId(right, CardRewardSide.Free);
            var leftGrant = cards.Deliver(Leader, leaderLease, left, CardRewardSide.Free);
            var rightGrant = cards.Deliver(Member, memberLease, right, CardRewardSide.Free);
            check("owned CardRewardService grants the shared row independently once per character while shared purification advances once",
                leftGrant.Committed && rightGrant.Committed
                && leaderLease.Inventory.CountMainItem(10154439) == 3 && memberLease.Inventory.CountMainItem(10154439) == 3
                && f.StoredItemCount(Leader, 10154439) == 3 && f.StoredItemCount(Member, 10154439) == 3
                && f.StoredGold(Leader) == 1050 && f.StoredGold(Member) == 1050
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 2 && f.Effects.Outbox.Get(shared).AttemptCount == 1
                && f.Effects.Outbox.Get(leftEffect).AttemptCount == 1 && f.Effects.Outbox.Get(rightEffect).AttemptCount == 1);
            left.SettlementRuntime.FreezeGuildDungeonReward(left, result);
            check("a completed card layout accepts the same frozen result on reentry without reopening rewards",
                left.CardRewards == null && left.SettlementRuntime.FreeItem.StackCount == 3
                && !cards.Deliver(Leader, leaderLease, left, CardRewardSide.Free).Committed
                && !cards.Deliver(Member, memberLease, right, CardRewardSide.Free).Committed
                && f.StoredItemCount(Leader, 10154439) == 3 && f.StoredItemCount(Member, 10154439) == 3);
            var replacement = new GuildDungeonClearEffectResult
            {
                SourceEventId = result.SourceEventId,
                GuildId = result.GuildId, DungeonId = result.DungeonId, MonthId = result.MonthId,
                PreviousClearCount = result.PreviousClearCount, ClearCount = result.ClearCount,
                RewardGaugeRatio = result.RewardGaugeRatio, RewardItemId = result.RewardItemId,
                RewardCount = result.RewardCount + 1, RewardContribute = result.RewardContribute
            };
            check("a later caller cannot replace the runtime's durable reward choice after card delivery",
                Throws<InvalidOperationException>(() => left.SettlementRuntime.FreezeGuildDungeonReward(left, replacement))
                && left.SettlementRuntime.GuildDungeonClear.RewardCount == 3 && left.CardRewards == null);
            replacement.SourceEventId = Guid.NewGuid();
            replacement.RewardCount = result.RewardCount;
            check("the same guild and dungeon cannot import another shared clear's otherwise identical result",
                Throws<InvalidOperationException>(() => right.SettlementRuntime.FreezeGuildDungeonReward(right, replacement))
                && right.SettlementRuntime.GuildDungeonClear.SourceEventId == instance.ClearedFact.SourceEventId
                && right.SettlementRuntime.FreeItem.StackCount == 3);

            f.SetCount(f.GuildA.Id, f.FirstDungeon, 199);
            var failing = CreateSingle(admission, Leader);
            var failingEffect = CardRewardRules.GetEffectId(failing, CardRewardSide.Free);
            f.Sql("CREATE TRIGGER reject_guild_card BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='" + DungeonPersistentEffectKinds.CardRewardFreeCommit
                + "' AND NEW.state=2 BEGIN SELECT RAISE(ABORT,'guild-card-rollback'); END;");
            check("a real card outbox failure rolls certificate and gold back in both the owned inventory and database",
                !cards.Deliver(Leader, leaderLease, failing, CardRewardSide.Free).Committed
                && leaderLease.Inventory.CountMainItem(10154439) == 3 && f.StoredItemCount(Leader, 10154439) == 3
                && f.StoredGold(Leader) == 1050 && f.Effects.Outbox.Get(failingEffect).State == DungeonPersistentEffectState.Failed);
            string failingPayload = f.Effects.Outbox.Get(failingEffect).PayloadJson;
            f.Sql("DROP TRIGGER reject_guild_card;");
            InventoryContext.Unregister(leaderLease.SessionId, Leader);
            f.Reopen();
            var recovered = f.Effects.RecoverCharacter(Leader);
            var recoveredLease = f.Lease(Leader, seedGold: false);
            var recoveryCards = new CardRewardService(f.Effects);
            check("offline registered recovery applies the accepted card once and its resumed runtime only projects that commit",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && f.StoredItemCount(Leader, 10154439) == 6 && f.StoredGold(Leader) == 1100
                && recoveryCards.Deliver(Leader, recoveredLease, failing, CardRewardSide.Free).Committed
                && !recoveryCards.Deliver(Leader, recoveredLease, failing, CardRewardSide.Free).Committed
                && recoveredLease.Inventory.CountMainItem(10154439) == 6
                && f.Effects.Outbox.Get(failingEffect).PayloadJson == failingPayload
                && f.Effects.Outbox.Get(failingEffect).AttemptCount == 2);
            var staleRun = CreateSingle(admission, Leader);
            var staleEffect = CardRewardRules.GetEffectId(staleRun, CardRewardSide.Free);
            check("a replaced inventory lease cannot accept a new certificate card intent",
                !recoveryCards.Deliver(Leader, leaderLease, staleRun, CardRewardSide.Free).Committed
                && f.Effects.Outbox.Get(staleEffect) == null && f.StoredItemCount(Leader, 10154439) == 6);
            var currentTime = f.UtcNow;
            f.UtcNow = currentTime.AddMonths(-1);
            var oldAdmission = f.RequireAdmission(Leader);
            var oldInstance = new DungeonInstance(checked((short)oldAdmission.DungeonId), oldAdmission.Difficulty);
            oldInstance.TryFreezeGuildDungeonAdmission(oldAdmission);
            var oldRun = CreateCardRun(oldInstance, Leader, 0);
            var oldEffect = f.Effect(oldAdmission, oldInstance.ClearedFact.SourceEventId);
            int oldMonth = f.MonthId;
            f.UtcNow = currentTime;
            if (!f.Apply(oldEffect, oldAdmission, out var noReward,
                    new GuildDungeonClearContribution(oldEffect.SourceEventId, oldMonth, 1)))
                throw new InvalidOperationException("Card fixture could not submit its late old-month fact.");
            oldRun.SettlementRuntime.FreezeGuildDungeonReward(oldRun, noReward);
            check("an old-month terminal no-op removes the ordinary free-item candidate instead of creating a certificate card",
                !noReward.HasReward && noReward.RewardGaugeRatio == 0 && noReward.RewardContribute == 0
                && noReward.RewardItemId == 0 && noReward.RewardCount == 0
                && oldRun.SettlementRuntime.FreeItem.ItemId == 0 && oldRun.CardRewards[1].ItemId == 0
                && f.StoredItemCount(Leader, 10154439) == 6);

            var overflowAdmission = f.RequireAdmission(OtherLeader);
            var overflowLease = f.Lease(OtherLeader);
            lock (overflowLease.SyncRoot)
            {
                // Capacity-only fixture, using the same complete ItemCore layout
                // as the established normal-card overflow SelfTest.
                for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
                    if (!overflowLease.Inventory.SetItem(InventoryListType.Main, slot, new ItemCore
                    {
                        ItemKind = ItemCore.KindEquipment, ItemId = 200000000 + slot,
                        InstanceValue = 1, Marker16 = -1, RandomOptionChangedIndex = 0xFF
                    }))
                        throw new InvalidOperationException("Guild card capacity fixture could not fill slot " + slot);
                if (!InventoryPersistenceService.SaveDirty(overflowLease))
                    throw new InvalidOperationException("Guild card capacity fixture could not persist its baseline.");
            }
            var mailbox = new MailboxService(new MailboxRepository(f.Database));
            var overflowEffects = new DungeonPersistentEffectApplicationService(f.Database.ConnectionString,
                f.Effects.Outbox, database: f.Database, overflowRewardSink: new MailboxInventoryOverflowRewardSink(mailbox));
            var overflowCards = new CardRewardService(overflowEffects);
            var overflowRun = CreateSingle(overflowAdmission, OtherLeader);
            var mailEffect = CardRewardRules.GetEffectId(overflowRun, CardRewardSide.Free);
            var mailGrant = overflowCards.Deliver(OtherLeader, overflowLease, overflowRun, CardRewardSide.Free);
            var inbox = mailbox.LoadInbox(OtherLeader, 20);
            check("a full owned inventory delivers the actual certificate row through the existing transactional card mailbox path once",
                mailGrant.Committed && inbox.Count == 1 && inbox[0].Attachments.Count == 1
                && inbox[0].Attachments[0].ItemTemplateId == 10154439 && inbox[0].Attachments[0].ItemCount == 3
                && inbox[0].Attachments[0].ItemCoreData.Length == ItemCore.Size
                && f.StoredItemCount(OtherLeader, 10154439) == 0
                && !overflowCards.Deliver(OtherLeader, overflowLease, overflowRun, CardRewardSide.Free).Committed
                && mailbox.LoadInbox(OtherLeader, 20).Count == 1
                && overflowEffects.Outbox.Get(mailEffect).AttemptCount == 1);
            var failingMailRun = CreateSingle(overflowAdmission, OtherLeader);
            var failingMailEffect = CardRewardRules.GetEffectId(failingMailRun, CardRewardSide.Free);
            f.Sql("CREATE TRIGGER reject_guild_mail_card BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='" + DungeonPersistentEffectKinds.CardRewardFreeCommit
                + "' AND NEW.state=2 BEGIN SELECT RAISE(ABORT,'guild-mail-card-rollback'); END;");
            check("failure after overflow mail creation rolls the mail, gold and card receipt back together",
                !overflowCards.Deliver(OtherLeader, overflowLease, failingMailRun, CardRewardSide.Free).Committed
                && mailbox.LoadInbox(OtherLeader, 20).Count == 1 && f.StoredGold(OtherLeader) == 1050
                && overflowEffects.Outbox.Get(failingMailEffect).State == DungeonPersistentEffectState.Failed);
            f.Sql("DROP TRIGGER reject_guild_mail_card;");
            InventoryContext.Unregister(overflowLease.SessionId, OtherLeader);
            f.Reopen();
            var mailRecovered = overflowEffects.RecoverCharacter(OtherLeader);
            var finalInbox = mailbox.LoadInbox(OtherLeader, 20);
            check("registered offline recovery retries the frozen overflow intent once without duplicating its earlier mail",
                mailRecovered.CommittedCount == 1 && mailRecovered.FailedCount == 0 && !mailRecovered.HasRemaining
                && finalInbox.Count == 2 && finalInbox.Sum(mail => mail.Attachments.Sum(item => item.ItemCount)) == 6
                && f.StoredGold(OtherLeader) == 1100
                && overflowEffects.Outbox.Get(failingMailEffect).State == DungeonPersistentEffectState.Committed
                && overflowEffects.RecoverCharacter(OtherLeader).CommittedCount == 0 && mailbox.LoadInbox(OtherLeader, 20).Count == 2);

            DungeonRun CreateSingle(GuildDungeonAdmissionSnapshot owner, int characterId)
            {
                var single = new DungeonInstance(checked((short)owner.DungeonId), owner.Difficulty);
                if (!single.TryFreezeGuildDungeonAdmission(owner))
                    throw new InvalidOperationException("Single-card fixture could not freeze its admission.");
                var run = CreateCardRun(single, characterId, 0);
                var effect = f.Effect(owner, single.ClearedFact.SourceEventId);
                if (!f.Apply(effect, owner, out var committed))
                    throw new InvalidOperationException("Single-card fixture could not commit its guild result.");
                run.SettlementRuntime.FreezeGuildDungeonReward(run, committed);
                return run;
            }
            DungeonRun CreateCardRun(DungeonInstance physical, int characterId, byte slot)
            {
                var run = new DungeonRun(physical, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
                var fact = physical.GetOrCreateClearedFact(new DungeonClearIntent(
                    DungeonEventEnvelope.Create(run, characterId, "guild-certificate-card-selftest"), "selftest", 0), out _);
                if (!run.TryBeginClearCommit(fact) || !run.TryCompleteClearCommit(fact))
                    throw new InvalidOperationException("Card fixture could not bind its original clear fact.");
                run.Phase = DungeonRunPhase.CardsRevealed;
                run.EntryPartySlotIndex = slot;
                var runtime = new DungeonSettlementRuntime
                {
                    ShouldScheduleCardRewardFlow = true,
                    FreeGold = new ClearRewardGenerator.CardReward { IsGold = true, GoldAmount = 50 },
                    FreeItem = new ClearRewardGenerator.CardReward { ItemId = 3309, StackCount = 1 }
                };
                run.SettlementRuntime = runtime;
                run.CardRewards = Enumerable.Repeat(default(ClearRewardGenerator.CardReward), 8).ToList();
                run.CardRewards[0] = runtime.FreeGold; run.CardRewards[1] = runtime.FreeItem;
                run.FreeCardSlots = new byte[] { 0, 0xFF, 0xFF, 0xFF };
                run.PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                return run;
            }
            DungeonParticipantRosterEntry Participant(DungeonRun run, int characterId, byte slot)
                => new(characterId, checked((ushort)(100 + slot)), run, run.CaptureIdentity(),
                    new DungeonRoomIdentity(instance.Identity, 1), 1, partySlot: slot, accountId: Fixture.Account(characterId));
        }

        private static void CheckMonth(Fixture f, Action<string, bool> check)
        {
            check("the operating month changes exactly at UTC+8 day-one 06:00 and retains the previous period before that anchor",
                GuildDungeonMonth.MonthId(new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero)) == 202610
                && GuildDungeonMonth.MonthId(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero)) == 202611
                && GuildDungeonMonth.MonthId(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)) == 202610
                && GuildDungeonMonth.MonthId(new DateTimeOffset(2026, 12, 31, 21, 59, 59, TimeSpan.Zero)) == 202612
                && GuildDungeonMonth.MonthId(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero)) == 202701);
            check("month keys require valid calendar months throughout the supported range",
                new[] { 197001, 202610, 999912 }.All(GuildDungeonMonth.IsValid)
                && new[] { 0, 196912, 197000, 197013, 202600, 202613, 999913, int.MaxValue }.All(value => !GuildDungeonMonth.IsValid(value)));
            check("the countdown is an absolute Unix second and shares the purification six-hour month anchor",
                GuildDungeonMonth.NextResetUnixSeconds(new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero))
                    == (uint)new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
                && GuildDungeonMonth.NextResetUnixSeconds(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero))
                    == (uint)new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
                && GuildDungeonMonth.NextResetUnixSeconds(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero))
                    == (uint)new DateTimeOffset(2027, 1, 31, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
                && GuildDungeonMonth.NextResetUnixSeconds(new DateTimeOffset(2028, 2, 1, 0, 0, 0, TimeSpan.Zero))
                    == (uint)new DateTimeOffset(2028, 2, 29, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
            f.UtcNow = new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 17);
            f.SetCount(f.GuildB.Id, f.FirstDungeon, 9);
            var originalAdmission = f.RequireAdmission(Leader);
            f.UtcNow = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
            check("the first new-month read hides the prior guild counters without a timer or destructive read",
                f.Service.LoadForMember(Leader, out int guild).Count == 0 && guild == f.GuildA.Id
                && f.Service.LoadForMember(Member, out _).Count == 0 && f.Count(f.GuildB.Id, f.FirstDungeon) == 0
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 17 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202610
                && !f.Service.ValidateParticipant(Leader, Fixture.Account(Leader), originalAdmission));
            f.Reopen();
            check("reopening offline in a new month preserves the same read policy without inheriting previous unlocks",
                f.Service.LoadForMember(Leader, out _).Count == 0 && f.MonthId == 202611
                && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202610);
            var admission = f.RequireAdmission(Leader);
            var first = f.Effect(admission);
            var firstContribution = new GuildDungeonClearContribution(first.SourceEventId, f.MonthId, 2);
            check("the first current-month clear replaces the old count with only the frozen current contribution",
                f.Apply(first, admission, out var reset, firstContribution) && reset.PreviousClearCount == 0 && reset.ClearCount == 2
                && reset.MonthId == 202611 && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 2
                && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202611 && f.StoredCount(f.GuildB.Id, f.FirstDungeon) == 9);
            var late = f.Effect(originalAdmission);
            check("an October fact arriving in November commits a terminal no-op without redirecting its period",
                f.Apply(late, originalAdmission, out var lateResult, new GuildDungeonClearContribution(late.SourceEventId, 202610, 4))
                && lateResult.MonthId == 202610 && lateResult.PreviousClearCount == 2 && lateResult.ClearCount == 2
                && !lateResult.HasReward
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 2 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202611);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 23, monthId: 202612);
            var backward = f.Effect(admission);
            check("a future row is invisible and a current-month clear cannot overwrite it while the clock is behind",
                f.Service.LoadForMember(Leader, out _).Count == 0
                && f.Apply(backward, admission, out var protectedRow) && protectedRow.ClearCount == 0
                && !protectedRow.HasReward
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 23 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202612);
            var futureEvent = f.Effect(admission);
            check("a future-month durable event cannot write into an earlier active month",
                f.Apply(futureEvent, admission, out var futureResult, new GuildDungeonClearContribution(futureEvent.SourceEventId, 202612, 3))
                && !futureResult.HasReward
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 23 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202612);
            f.UtcNow = new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero);
            check("when the calendar catches up it reveals the preserved future row without recreating lost progress",
                f.Service.LoadForMember(Leader, out _)[f.FirstDungeon] == 23
                && f.Service.LoadForMember(Member, out _)[f.FirstDungeon] == 23);
            CheckMonthlyPendingRecovery(f, check);
            CheckLegacyMonthlyReceipt(f, check);
            foreach (object bad in new object[] { "bad-month", 202612.5, 0, 197000, 197013, 999913, (long)int.MaxValue + 1 })
            {
                f.SetCount(f.GuildA.Id, f.FirstDungeon, 1, monthId: 202610);
                f.SetMonth(f.GuildA.Id, f.FirstDungeon, bad);
                check("an invalid calendar-month storage " + bad + " fails closed even when its count would be filtered as old",
                    ThrowsInvalidData(() => f.Service.LoadForMember(Leader, out _))
                    && ThrowsInvalidData(() => f.Prepare(Leader, f.FirstDungeon, out _)));
                f.DeleteCount(f.GuildA.Id, f.FirstDungeon);
            }
        }

        private static void CheckMonthlyPendingRecovery(Fixture f, Action<string, bool> check)
        {
            f.UtcNow = new DateTimeOffset(2026, 12, 31, 21, 59, 59, TimeSpan.Zero);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 11);
            var admission = f.RequireAdmission(Leader);
            var effect = f.Effect(admission);
            var contribution = new GuildDungeonClearContribution(effect.SourceEventId, f.MonthId, 4);
            f.Sql("CREATE TRIGGER reject_month_pending BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='guild-dungeon-clear-commit' AND NEW.state=2 BEGIN SELECT RAISE(ABORT,'guild-month-pending'); END;");
            check("a December accepted four-member intent rolls back progress and retains its original durable payload",
                !f.Apply(effect, admission, out _, contribution) && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 11
                && f.Effects.Outbox.Get(effect).State == DungeonPersistentEffectState.Failed
                && f.Effects.Outbox.Get(effect).PayloadVersion == 4);
            f.Sql("DROP TRIGGER reject_month_pending;");
            string frozenPayload = f.Effects.Outbox.Get(effect).PayloadJson;
            f.UtcNow = new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 3);
            f.Reopen();
            var recovered = f.Effects.RecoverCharacter(Leader);
            check("new-year recovery commits an old-month intent as a terminal no-op and leaves January counters intact",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && f.Effects.TryApplyGuildDungeonClear(effect, admission, out var result, out _)
                && result.MonthId == 202612 && result.PreviousClearCount == 3 && result.ClearCount == 3
                && !result.HasReward
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 3 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202701
                && f.Effects.Outbox.Get(effect).PayloadJson == frozenPayload && f.Effects.RecoverCharacter(Leader).CommittedCount == 0);
        }

        private static void CheckLegacyMonthlyReceipt(Fixture f, Action<string, bool> check)
        {
            var legacyJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            f.UtcNow = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var admission = f.RequireAdmission(Leader);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 5);
            var current = EnqueueLegacy();
            var recovered = f.Effects.RecoverCharacter(Leader);
            check("a v1 pending receipt derives its creation month and preserves its original single-clear weight",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0
                && f.Effects.TryApplyGuildDungeonClear(current, admission, out var currentResult, out _)
                && currentResult.MonthId == 202701 && currentResult.PreviousClearCount == 5 && currentResult.ClearCount == 6
                && !currentResult.HasReward
                && f.Effects.Outbox.Get(current).PayloadVersion == 1 && f.Count(f.GuildA.Id, f.FirstDungeon) == 6);
            f.UtcNow = new DateTimeOffset(2026, 12, 31, 21, 59, 59, TimeSpan.Zero);
            var old = EnqueueLegacy();
            f.UtcNow = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
            f.Reopen();
            var oldRecovered = f.Effects.RecoverCharacter(Leader);
            check("a v1 previous-month pending receipt cannot gain a new period from recovery time",
                oldRecovered.CommittedCount == 1 && oldRecovered.FailedCount == 0
                && f.Effects.TryApplyGuildDungeonClear(old, admission, out var oldResult, out _)
                && oldResult.MonthId == 202612 && oldResult.ClearCount == 6 && !oldResult.HasReward
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 6);
            var historic = EnqueueLegacy();
            var historicResult = JsonSerializer.Serialize(new { GuildId = admission.GuildId, DungeonId = admission.DungeonId,
                PreviousClearCount = 4, ClearCount = 5 }, legacyJsonOptions);
            f.Database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "UPDATE dungeon_persistent_effect_outbox SET state=2,result_version=1,result_json=@result,committed_at=@now WHERE source_event_id=@source;";
                command.Parameters.AddWithValue("@result", historicResult); command.Parameters.AddWithValue("@now", f.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@source", historic.SourceEventId.ToString("N")); command.ExecuteNonQuery();
            });
            check("an already committed v1 result acquires creation-month metadata in memory without rewriting its durable JSON or count",
                f.Effects.TryApplyGuildDungeonClear(historic, admission, out var replay, out _)
                && replay.MonthId == 202701 && replay.ClearCount == 5 && !replay.HasReward
                && replay.SourceEventId == historic.SourceEventId
                && f.Effects.Outbox.Get(historic).ResultJson == historicResult && f.Count(f.GuildA.Id, f.FirstDungeon) == 6);

            DungeonEffectId EnqueueLegacy()
            {
                var effect = f.Effect(admission);
                var payload = JsonSerializer.Serialize(new { admission.GuildId, CharacterId = admission.SourceCharacterId,
                    AccountId = admission.SourceAccountId, admission.DungeonId, admission.Difficulty, admission.GaugeLimit, admission.GaugeOnClear }, legacyJsonOptions);
                f.Effects.Outbox.Enqueue(new DungeonPersistentEffectDefinition { EffectId = effect,
                    CharacterId = admission.SourceCharacterId, AccountId = admission.SourceAccountId, PayloadVersion = 1, PayloadJson = payload });
                return effect;
            }
        }

        private static void CheckRecovery(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            var effect = f.Effect(admission);
            f.Sql("CREATE TRIGGER guild_reject_outbox BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='guild-dungeon-clear-commit' AND NEW.state=2 "
                + "BEGIN SELECT RAISE(ABORT,'guild-outbox-fault'); END;");
            check("failure after the progress write rolls back its increment and retains one failed durable intent",
                !f.Apply(effect, admission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 0
                && f.Effects.Outbox.Get(effect).State == DungeonPersistentEffectState.Failed);
            check("canonical source can leave after its accepted intent without redirecting the durable guild owner",
                f.Guilds.ChangeRank(Leader, Member, 1) == GuildResult.Success
                && f.Guilds.RemoveMember(Leader, Leader) == GuildResult.Success);
            f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildB.Id, Leader));
            f.Sql("DROP TRIGGER guild_reject_outbox;");
            f.Reopen();
            var recovered = f.Effects.RecoverCharacter(Leader);
            var duplicate = f.Effects.RecoverCharacter(Leader);
            check("registered recovery commits the original guild once despite the source joining another guild",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && duplicate.CommittedCount == 0 && f.Count(f.GuildA.Id, f.FirstDungeon) == 1
                && f.Count(f.GuildB.Id, f.FirstDungeon) == 0
                && f.Effects.Outbox.Get(effect).State == DungeonPersistentEffectState.Committed
                && !f.Service.ValidateParticipant(Leader, Fixture.Account(Leader), admission));
        }

        private static void CheckDisband(Fixture f, Action<string, bool> check)
        {
            var admission = f.RequireAdmission(Leader);
            check("a guild has one committed progress row before dissolution",
                f.Apply(f.Effect(admission), admission, out _) && f.Count(f.GuildA.Id, f.FirstDungeon) == 1);
            var late = f.Effect(admission);
            f.Sql("CREATE TRIGGER guild_disband_pending BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + "WHEN NEW.effect_kind='guild-dungeon-clear-commit' AND NEW.state=2 "
                + "BEGIN SELECT RAISE(ABORT,'guild-disband-pending'); END;");
            check("an accepted durable clear can remain pending before the guild is dissolved",
                !f.Apply(late, admission, out _) && f.Effects.Outbox.Get(late).State == DungeonPersistentEffectState.Failed
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 1);
            f.Sql("DROP TRIGGER guild_disband_pending;");
            check("disband commits the original owner and cascades its progress",
                f.Guilds.Disband(Leader) == GuildResult.Success
                && f.Scalar("SELECT COUNT(*) FROM guild_dungeon_progress WHERE guild_id=" + f.GuildA.Id + ";") == 0);
            f.Reopen();
            var recovery = f.Effects.RecoverCharacter(Leader);
            check("an accepted old clear becomes a committed terminal no-op rather than resurrecting the guild",
                recovery.CommittedCount == 1 && recovery.FailedCount == 0 && !recovery.HasRemaining
                && f.Apply(late, admission, out var noOp) && noOp.PreviousClearCount == 0 && noOp.ClearCount == 0
                && f.Effects.Outbox.Get(late).State == DungeonPersistentEffectState.Committed
                && f.Guilds.Get(f.GuildA.Id) == null && f.Count(f.GuildA.Id, f.FirstDungeon) == 0);
            var replacement = f.Database.Write((c, t) => GuildRepository.Insert(c, t, Leader, f.GuildA.Name, string.Empty));
            check("recreating the same name receives a different guild ID and no old clear count",
                replacement.Id != f.GuildA.Id && f.Apply(late, admission, out _)
                && f.Service.LoadForMember(Leader, out int newGuild).Count == 0 && newGuild == replacement.Id);
        }

        private static void VerifyPacket(Action<string, bool> check)
        {
            var counts = new Dictionary<int, int> { [8500] = 10001, [8503] = 1 };
            uint resetEnd = checked((uint)new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
            var packet = GuildDungeonPacketBuilder.Progress(counts, resetEnd);
            check("GUILD_DUNGEON_INFO encodes registered raw counts with its native list header",
                packet.Length == HeaderLength + 17 + 8 * counts.Count && packet[0] == 0
                && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.GUILD_DUNGEON_INFO
                && packet[HeaderLength] == 0 && BitConverter.ToUInt32(packet, HeaderLength + 1) == uint.MaxValue
                && BitConverter.ToUInt32(packet, HeaderLength + 5) == resetEnd && BitConverter.ToUInt32(packet, HeaderLength + 9) == 0
                && BitConverter.ToUInt32(packet, HeaderLength + 13) == 2
                && BitConverter.ToUInt32(packet, HeaderLength + 17) == 8500
                && BitConverter.ToUInt32(packet, HeaderLength + 21) == 10001
                && BitConverter.ToUInt32(packet, HeaderLength + 25) == 8503
                && BitConverter.ToUInt32(packet, HeaderLength + 29) == 1);
            var empty = GuildDungeonPacketBuilder.Progress(new Dictionary<int, int>(), 0);
            check("a missing progress row is represented by an empty list instead of six fabricated unlocks",
                empty.Length == HeaderLength + 17 && BitConverter.ToUInt32(empty, HeaderLength + 13) == 0);
            var repeat = GuildDungeonPacketBuilder.RepeatBonus();
            check("GUILD_DUNGEON_REPEAT_BONUS_INFO carries the verified four-byte zero bonus",
                repeat.Length == HeaderLength + 4 && repeat[0] == 0
                && BitConverter.ToUInt16(repeat, 1) == (ushort)NotiPacketTypeA21.GUILD_DUNGEON_REPEAT_BONUS_INFO
                && BitConverter.ToUInt32(repeat, HeaderLength) == 0);
        }

        private static async Task CheckNotifierAsync(Fixture f, Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            var notifier = new GuildDungeonProgressNotifier(f.Database, f.Service, sessions);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 10001);
            f.SetCount(f.GuildB.Id, f.FirstDungeon, 19);
            var expectedA = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, id => id == f.FirstDungeon ? 10001 : 0);
            var expectedB = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, id => id == f.FirstDungeon ? 19 : 0);
            var expectedEmpty = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, _ => 0);
            using var leader = await Peer.CreateAsync(f, sessions, Leader);
            using var member = await Peer.CreateAsync(f, sessions, Member);
            using var other = await Peer.CreateAsync(f, sessions, OtherLeader);
            using var outsider = await Peer.CreateAsync(f, sessions, Outsider);
            foreach (var target in new[] { (Peer: leader, Rows: expectedA, Label: "leader"),
                (Peer: member, Rows: expectedA, Label: "same-guild member"),
                (Peer: other, Rows: expectedB, Label: "different-guild member"),
                (Peer: outsider, Rows: expectedEmpty, Label: "nonmember") })
            {
                await notifier.SendAsync(target.Peer.Session, "guild-progress-selftest").WaitAsync(Timeout);
                check("real socket " + target.Label + " receives all six registered raw counts and zero repeat bonus",
                    ProjectionMatches(await target.Peer.DrainAsync(), target.Rows,
                        target.Label == "nonmember" ? 0 : GuildDungeonMonth.NextResetUnixSeconds(f.UtcNow)));
            }

            var oldGameSession = member.Session.GameSession;
            await QueuedAsync("GameSession replacement", () => member.Session.GameSession =
                new GameSession(member.Session, f.Database, sessionDirectory: sessions));
            member.Session.GameSession = oldGameSession;
            ushort oldUid = member.Session.Player.UserId;
            await QueuedAsync("UID replacement", () => member.Session.Player.UserId++);
            member.Session.Player.UserId = oldUid;
            var oldAccount = member.Session.Account;
            await QueuedAsync("account object replacement", () => member.Session.Account =
                new AccountRecord { AccountId = Fixture.Account(Member) });
            member.Session.Account = oldAccount;
            await QueuedAsync("owned lease replacement", member.ReloadLease);
            using (var replacementClient = new TcpClient())
            {
                var replacement = new EnhancedClientSession(replacementClient, new GamePacketHeader());
                await QueuedAsync("current directory replacement", () => sessions.Register(Member, replacement));
                sessions.Register(Member, member.Session);
                replacement.Close();
            }
            await QueuedAsync("guild departure", () =>
            {
                if (f.Guilds.RemoveMember(Member, Member) != GuildResult.Success)
                    throw new InvalidOperationException("Notifier fixture member could not leave its guild.");
            });
            f.Database.Write((c, t) => GuildRepository.AddMember(c, t, f.GuildA.Id, Member));

            InventoryContext.Unregister(member.Session.SessionId, Member);
            await notifier.SendAsync(member.Session, "guild-progress-no-lease").WaitAsync(Timeout);
            check("an unowned inventory cannot publish even with a current registered socket",
                (await member.DrainAsync()).Count == 0);
            member.ReloadLease();

            var admission = f.RequireAdmission(Leader);
            await member.SendGate.WaitAsync().WaitAsync(Timeout);
            Task pending;
            try
            {
                pending = notifier.SendAsync(member.Session, "guild-progress-queued-clear");
                check("progress notification really waits behind the current socket send lock", !pending.IsCompleted);
                if (!f.Apply(f.Effect(admission), admission, out var committed) || committed.ClearCount != 10002)
                    throw new InvalidOperationException("Notifier fixture could not commit the queued real clear.");
            }
            finally { member.SendGate.Release(); }
            await pending.WaitAsync(Timeout);
            expectedA[f.FirstDungeon] = 10002;
            check("a real clear committed while queued is reloaded by prepare and sends the latest raw count",
                ProjectionMatches(await member.DrainAsync(), expectedA));

            var boundaryDatabase = new ReadBoundaryDatabase(f.Database);
            var boundaryNotifier = new GuildDungeonProgressNotifier(boundaryDatabase,
                f.CreateService(boundaryDatabase), sessions);
            // The first read captures the source; the prepare guard reads its
            // membership, then the third read materializes the packet counters.
            // Mutate only after that read has disposed its real SQLite snapshot.
            boundaryDatabase.AfterRead = read =>
            {
                if (read != 3) return;
                boundaryDatabase.AfterRead = null;
                if (!f.Apply(f.Effect(admission), admission, out var committed) || committed.ClearCount != 10003)
                    throw new InvalidOperationException("Notifier fixture could not commit the prepare-boundary clear.");
                boundaryDatabase.Mutated = true;
            };
            await boundaryNotifier.SendAsync(member.Session, "guild-progress-prepared-clear").WaitAsync(Timeout);
            expectedA[f.FirstDungeon] = 10003;
            check("SameProgress retries a prepare-boundary clear and this call sends only the newly committed count",
                boundaryDatabase.Mutated && boundaryDatabase.ReadCount >= 7
                && ProjectionMatches(await member.DrainAsync(), expectedA) && f.Count(f.GuildA.Id, f.FirstDungeon) == 10003);
            await notifier.SendAsync(member.Session, "guild-progress-after-reprepared-clear").WaitAsync(Timeout);
            check("repreparing progress releases the real send lock and the next publication still has the committed count",
                ProjectionMatches(await member.DrainAsync(), expectedA));

            var racingDatabase = new ReadBoundaryDatabase(f.Database);
            var racingNotifier = new GuildDungeonProgressNotifier(racingDatabase,
                f.CreateService(racingDatabase), sessions);
            int racingClears = 0;
            racingDatabase.AfterRead = read =>
            {
                if (!f.Apply(f.Effect(admission), admission, out _))
                    throw new InvalidOperationException("Notifier fixture could not commit the continuous competing clear.");
                racingClears++;
            };
            await racingNotifier.SendAsync(member.Session, "guild-progress-continuous-clear").WaitAsync(Timeout);
            racingDatabase.AfterRead = null;
            // One initial progress snapshot, then prepare and SameProgress
            // snapshots for each of the three rejected publication attempts.
            check("continuous legitimate progress competition exhausts three prepares and returns normally without either packet",
                racingClears >= 3 && racingDatabase.ProgressSnapshotReadCount == 1 + 2 * 3
                && (await member.DrainAsync()).Count == 0
                && f.Count(f.GuildA.Id, f.FirstDungeon) == 10003 + racingClears);
            expectedA[f.FirstDungeon] = 10003 + racingClears;
            await racingNotifier.SendAsync(member.Session, "guild-progress-stable-after-deferred-clear").WaitAsync(Timeout);
            check("a stable call after progress retry exhaustion reacquires the send lock and sends the latest raw count",
                ProjectionMatches(await member.DrainAsync(), expectedA));

            var identityDatabase = new ReadBoundaryDatabase(f.Database);
            var identityNotifier = new GuildDungeonProgressNotifier(identityDatabase,
                f.CreateService(identityDatabase), sessions);
            var preparedGameSession = member.Session.GameSession;
            identityDatabase.AfterRead = read =>
            {
                if (read != 3) return;
                identityDatabase.AfterRead = null;
                member.Session.GameSession = new GameSession(member.Session, f.Database, sessionDirectory: sessions);
                identityDatabase.Mutated = true;
            };
            bool identityCanceled = false;
            try { await identityNotifier.SendAsync(member.Session, "guild-progress-prepared-identity-expired").WaitAsync(Timeout); }
            catch (OperationCanceledException) { identityCanceled = true; }
            finally { member.Session.GameSession = preparedGameSession; }
            check("identity expired after preparation still propagates cancellation without retrying as a progress race",
                identityDatabase.Mutated && identityCanceled && identityDatabase.ReadCount == 3
                && (await member.DrainAsync()).Count == 0);
            await notifier.SendAsync(member.Session, "guild-progress-after-identity-cancellation").WaitAsync(Timeout);
            check("identity cancellation releases the real send lock while committed guild progress stays available",
                ProjectionMatches(await member.DrainAsync(), expectedA));

            var partialDatabase = new ReadBoundaryDatabase(f.Database);
            var partialNotifier = new GuildDungeonProgressNotifier(partialDatabase,
                f.CreateService(partialDatabase), sessions);
            var beforePartialClear = new Dictionary<int, int>(expectedA);
            int partialClearCount = checked(beforePartialClear[f.FirstDungeon] + 1);
            // Reads 4/5 fence the first INFO. Read 6 is the membership fence
            // for the second frame, after that first socket write completed.
            partialDatabase.AfterRead = read =>
            {
                if (read != 6) return;
                partialDatabase.AfterRead = null;
                if (!f.Apply(f.Effect(admission), admission, out var committed) || committed.ClearCount != partialClearCount)
                    throw new InvalidOperationException("Notifier fixture could not commit the partial-batch competing clear.");
                partialDatabase.Mutated = true;
            };
            await partialNotifier.SendAsync(member.Session, "guild-progress-clear-after-first-info").WaitAsync(Timeout);
            expectedA[f.FirstDungeon] = partialClearCount;
            var partialPackets = await member.DrainAsync();
            check("a real clear after the first INFO writes old INFO then latest INFO and REPEAT0 without failing the caller",
                partialDatabase.Mutated && partialPackets.Count == 3
                && ProjectionMatches(new[] { partialPackets[0], partialPackets[2] }, beforePartialClear)
                && ProjectionMatches(new[] { partialPackets[1], partialPackets[2] }, expectedA)
                && f.Count(f.GuildA.Id, f.FirstDungeon) == partialClearCount);
            await notifier.SendAsync(member.Session, "guild-progress-after-partial-batch-retry").WaitAsync(Timeout);
            check("a partial-batch progress retry releases the send lock for a later complete current projection",
                ProjectionMatches(await member.DrainAsync(), expectedA));

            async Task QueuedAsync(string label, Action change)
            {
                await member.SendGate.WaitAsync().WaitAsync(Timeout);
                Task sending;
                try
                {
                    sending = notifier.SendAsync(member.Session, "guild-progress-queued-" + label);
                    check("real progress send is queued before " + label, !sending.IsCompleted);
                    change();
                }
                finally { member.SendGate.Release(); }
                await sending.WaitAsync(Timeout);
                check("queued progress suppresses stale " + label + " before writing either packet",
                    (await member.DrainAsync()).Count == 0);
            }
        }

        private static async Task CheckContributionNotifierAsync(Fixture f, Action<string, bool> check)
        {
            var sessions = new SessionDirectory();
            using var leader = await Peer.CreateAsync(f, sessions, Leader);
            using var member = await Peer.CreateAsync(f, sessions, Member);
            var admission = f.RequireAdmission(Leader);
            if (!f.Apply(f.Effect(admission), admission, out _))
                throw new InvalidOperationException("Contribution refresh could not commit its original clear.");
            var notifier = new GuildDungeonProgressNotifier(f.Database, f.Service, sessions);
            foreach (var peer in new[] { leader, member })
            {
                await notifier.SendAsync(peer.Session, "guild-contribution-socket", includeContribution: true).WaitAsync(Timeout);
                var packets = await peer.DrainAsync();
                var expected = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, id => id == f.FirstDungeon ? 1 : 0);
                int ownScore = peer.Session.Player.CharacterId == Leader ? 20 : 0;
                var info = packets.Count == 3 ? packets[2] : Array.Empty<byte>();
                check("the real contribution refresh sends the two progress frames then the receiving member's own GUILD_INFO: " + peer.Session.Player.CharacterId,
                    packets.Count == 3 && ProjectionMatches(packets.Take(2).ToArray(), expected,
                        GuildDungeonMonth.NextResetUnixSeconds(f.UtcNow))
                    && BitConverter.ToUInt16(info, 1) == (ushort)NotiPacketTypeA21.GUILD_INFO
                    && BitConverter.ToInt32(info, info.Length - 32) == 20 && BitConverter.ToInt32(info, info.Length - 28) == 20
                    && BitConverter.ToInt32(info, info.Length - 24) == ownScore && BitConverter.ToInt32(info, info.Length - 20) == ownScore);
            }
            var gate = member.SendGate;
            await gate.WaitAsync().WaitAsync(Timeout);
            var pending = notifier.SendAsync(member.Session, "guild-contribution-queued", includeContribution: true);
            if (f.Guilds.RemoveMember(Member, Member) != GuildResult.Success) throw new InvalidOperationException("Queued member could not leave.");
            gate.Release();
            try { await pending.WaitAsync(Timeout); } catch (OperationCanceledException) { }
            check("a queued contribution projection cannot publish an old guild or canonical person's score after withdrawal",
                (await member.DrainAsync()).Count == 0);
        }

        private static async Task CheckMonthNotifierAsync(Fixture f, Action<string, bool> check)
        {
            f.UtcNow = new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero);
            f.SetCount(f.GuildA.Id, f.FirstDungeon, 101);
            var sessions = new SessionDirectory();
            using var member = await Peer.CreateAsync(f, sessions, Member);
            var boundary = new ReadBoundaryDatabase(f.Database);
            var notifier = new GuildDungeonProgressNotifier(boundary, f.CreateService(boundary), sessions);
            boundary.AfterRead = read =>
            {
                if (read != 3) return;
                boundary.AfterRead = null;
                f.UtcNow = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
                boundary.Mutated = true;
            };
            await notifier.SendAsync(member.Session, "guild-month-boundary-prepare").WaitAsync(Timeout);
            var zero = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, _ => 0);
            check("a month crossing after prepare suppresses the old counters and this socket call sends only the new-month zero directory",
                boundary.Mutated && ProjectionMatches(await member.DrainAsync(), zero,
                    (uint)new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds())
                && f.StoredCount(f.GuildA.Id, f.FirstDungeon) == 101 && f.StoredMonth(f.GuildA.Id, f.FirstDungeon) == 202610);
            var admission = f.RequireAdmission(Leader);
            var effect = f.Effect(admission);
            if (!f.Apply(effect, admission, out var committed, new GuildDungeonClearContribution(effect.SourceEventId, f.MonthId, 3))
                || committed.ClearCount != 3)
                throw new InvalidOperationException("Month notifier fixture could not submit the first November clear.");
            zero[f.FirstDungeon] = 3;
            await notifier.SendAsync(member.Session, "guild-month-stable-after-rollover").WaitAsync(Timeout);
            check("the next month socket refresh uses the committed current contribution and the released send lock",
                ProjectionMatches(await member.DrainAsync(), zero,
                    (uint)new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

            // Even an empty guild has a period. Equal zero dictionaries must
            // not let the previous month's countdown pass the send fence.
            f.UtcNow = new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero);
            using var emptyGuild = await Peer.CreateAsync(f, sessions, OtherLeader);
            var emptyBoundary = new ReadBoundaryDatabase(f.Database);
            var emptyNotifier = new GuildDungeonProgressNotifier(emptyBoundary, f.CreateService(emptyBoundary), sessions);
            emptyBoundary.AfterRead = read =>
            {
                if (read != 3) return;
                emptyBoundary.AfterRead = null;
                f.UtcNow = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
                emptyBoundary.Mutated = true;
            };
            await emptyNotifier.SendAsync(emptyGuild.Session, "guild-empty-month-boundary").WaitAsync(Timeout);
            check("a guild with unchanged empty progress still retries a stale period and sends the new Unix deadline",
                emptyBoundary.Mutated && ProjectionMatches(await emptyGuild.DrainAsync(),
                    f.Service.Catalog.Rules.Keys.ToDictionary(id => id, _ => 0),
                    (uint)new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
        }

        private static async Task CheckEntrySelectionAsync(Fixture f, Action<string, bool> check)
        {
            foreach (var scenario in new[] { "current", "GameSession replacement", "selection replacement" })
            {
                var sessions = new SessionDirectory();
                using var peer = await Peer.CreateAsync(f, sessions, Member);
                var player = peer.Session.Player;
                if (!Town.TryGetDungeonGateReturnInfo(GuildAgitAccessPolicy.TownId, 1, out var gate))
                    throw new InvalidDataException("The current guild selection gate disappeared.");
                player.CurTownId = gate.Town; player.CurAreaId = gate.Area;
                player.CurPosX = gate.X; player.CurPosY = gate.Y;
                player.TownPresenceReady = true;
                var boundary = new ReadBoundaryDatabase(f.Database);
                var records = new SqliteCharacterRepository(boundary);
                var daily = new DailyResetService(boundary);
                var source = new SqliteSelectCharacterDataSource(boundary, records, dailyResetService: daily);
                var refresh = new InventoryRefreshSender(source, records, boundary);
                refresh.BindSessions(sessions);
                var services = new DungeonSharedServices(new ReviveCoinService(daily), records, source, null,
                    boundary.ConnectionString, refresh, sessions: sessions, database: boundary, dailyResetService: daily);
                var handler = new DungeonEntryHandler(services, null);
                var notifierFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int initialSnapshots = boundary.ProgressSnapshotReadCount;
                bool sendGateHeld = false;
                var originalGameSession = peer.Session.GameSession;
                DungeonSelectionContext originalSelection = null, replacementSelection = null;
                if (scenario != "current")
                {
                    boundary.AfterResult = result =>
                    {
                        // INFO and REPEAT have both crossed the real socket by
                        // this last membership read. Take the released send lock
                        // so the following ENTER_SELECT write must queue again.
                        if (result is not GuildRecord || boundary.ProgressSnapshotReadCount != initialSnapshots + 4)
                            return;
                        boundary.AfterResult = null;
                        originalSelection = player.CurrentDungeonSelection;
                        if (!peer.SendGate.Wait(0))
                            throw new InvalidOperationException("The progress notifier still holds its send lock after the batch.");
                        sendGateHeld = true;
                        notifierFinished.TrySetResult(true);
                    };
                }
                Task handling = null;
                try
                {
                    handling = handler.HandleEnterSelectDungeon(peer.Session,
                        new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.ENTER_SELECT_DUNGEON },
                        BitConverter.GetBytes(f.FirstDungeon));
                    if (scenario != "current")
                    {
                        await notifierFinished.Task.WaitAsync(Timeout);
                        check("actual selection continuation queues after both progress frames before " + scenario,
                            sendGateHeld && !handling.IsCompleted && originalSelection != null);
                        if (scenario == "GameSession replacement")
                            peer.Session.GameSession = new GameSession(peer.Session, f.Database, sessionDirectory: sessions);
                        else
                        {
                            player.ClearDungeonSelection();
                            replacementSelection = player.BeginDungeonSelection(originalSelection.ReturnAnchor);
                            if (replacementSelection == null || ReferenceEquals(replacementSelection, originalSelection))
                                throw new InvalidOperationException("The actual selection fixture did not advance its selection identity.");
                        }
                        peer.SendGate.Release();
                        sendGateHeld = false;
                    }
                    await handling.WaitAsync(Timeout);
                    var packets = await peer.DrainAsync();
                    int infoIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.GUILD_DUNGEON_INFO));
                    int repeatIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.GUILD_DUNGEON_REPEAT_BONUS_INFO));
                    int selectionIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.ENTER_SELECT_DUNGEON));
                    var expected = f.Service.Catalog.Rules.Keys.ToDictionary(id => id, _ => 0);
                    check("actual entry " + scenario + " sends the complete current guild progress before its continuation",
                        infoIndex >= 0 && repeatIndex == infoIndex + 1
                        && ProjectionMatches(new[] { packets[infoIndex], packets[repeatIndex] }, expected));
                    if (scenario == "current")
                    {
                        check("a current actual entry sends ENTER_SELECT after progress and reaches its existing wedding projections",
                            selectionIndex == repeatIndex + 1 && packets.Count == selectionIndex + 4
                            && IsNoti(packets[selectionIndex + 1], NotiPacketTypeA21.WEDDING_INFO)
                            && packets[selectionIndex + 2][0] == 1
                            && BitConverter.ToUInt16(packets[selectionIndex + 2], 1) == (ushort)CmdPacketTypeA21.WEDDING_CHARAC
                            && IsNoti(packets[selectionIndex + 3], NotiPacketTypeA21.COUPLE_ROOM)
                            && player.CurrentDungeonSelection != null && player.CurrentRun == null);
                    }
                    else
                    {
                        check("the real send-lock guard suppresses ENTER_SELECT and all later projection after " + scenario,
                            selectionIndex < 0 && repeatIndex == packets.Count - 1 && player.CurrentRun == null);
                        check("an expired entry cannot consume or replace the current selection after " + scenario,
                            ReferenceEquals(player.CurrentDungeonSelection,
                                scenario == "selection replacement" ? replacementSelection : originalSelection));
                    }
                }
                finally
                {
                    boundary.AfterResult = null;
                    if (sendGateHeld) peer.SendGate.Release();
                    if (handling != null && !handling.IsCompleted) await handling.WaitAsync(Timeout);
                    peer.Session.GameSession = originalGameSession;
                }
                await peer.SendGate.WaitAsync().WaitAsync(Timeout);
                peer.SendGate.Release();
                check("actual entry " + scenario + " releases both its send and lifecycle gates",
                    player.DungeonRunTransitionGate.CurrentCount == 1);
            }

            static bool IsNoti(byte[] packet, NotiPacketTypeA21 type)
                => packet.Length >= HeaderLength && packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)type;
        }

        private static bool ProjectionMatches(IReadOnlyList<byte[]> packets, IReadOnlyDictionary<int, int> expected,
            uint? expectedResetAt = null)
        {
            if (packets.Count != 2) return false;
            var info = packets[0]; var repeat = packets[1];
            if (info.Length != HeaderLength + 17 + 8 * expected.Count || info[0] != 0
                || BitConverter.ToUInt16(info, 1) != (ushort)NotiPacketTypeA21.GUILD_DUNGEON_INFO
                || info[HeaderLength] != 0 || BitConverter.ToUInt32(info, HeaderLength + 1) != uint.MaxValue
                || (expectedResetAt.HasValue ? BitConverter.ToUInt32(info, HeaderLength + 5) != expectedResetAt.Value
                    : BitConverter.ToUInt32(info, HeaderLength + 5) == 0)
                || BitConverter.ToUInt32(info, HeaderLength + 9) != 0
                || BitConverter.ToUInt32(info, HeaderLength + 13) != expected.Count
                || repeat.Length != HeaderLength + 4 || repeat[0] != 0
                || BitConverter.ToUInt16(repeat, 1) != (ushort)NotiPacketTypeA21.GUILD_DUNGEON_REPEAT_BONUS_INFO
                || BitConverter.ToUInt32(repeat, HeaderLength) != 0)
                return false;
            var received = new Dictionary<int, int>();
            for (int i = 0; i < expected.Count; i++)
            {
                uint id = BitConverter.ToUInt32(info, HeaderLength + 17 + 8 * i);
                uint count = BitConverter.ToUInt32(info, HeaderLength + 21 + 8 * i);
                if (id > int.MaxValue || count > int.MaxValue || !received.TryAdd((int)id, (int)count)) return false;
            }
            return expected.All(row => received.TryGetValue(row.Key, out int count) && count == row.Value);
        }

        private sealed class ReadBoundaryDatabase : IGameDatabase
        {
            private readonly IGameDatabase _inner;
            internal ReadBoundaryDatabase(IGameDatabase inner) => _inner = inner;
            internal Action<int> AfterRead { get; set; }
            internal Action<object> AfterResult { get; set; }
            internal int ReadCount { get; private set; }
            internal int ProgressSnapshotReadCount { get; private set; }
            internal bool Mutated { get; set; }
            public string DatabasePath => _inner.DatabasePath;
            public string SchemaFilePath => _inner.SchemaFilePath;
            public string ConnectionString => _inner.ConnectionString;
            public SqliteConnection OpenConnection() => _inner.OpenConnection();
            public T Read<T>(Func<SqliteConnection, T> action)
            {
                T result = _inner.Read(action);
                ReadCount++;
                if (result is ValueTuple<int, IReadOnlyDictionary<int, int>>) ProgressSnapshotReadCount++;
                AfterRead?.Invoke(ReadCount);
                AfterResult?.Invoke(result);
                return result;
            }
            public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool immediate = true)
                => _inner.Write(action, immediate);
            public void Write(Action<SqliteConnection, SqliteTransaction> action, bool immediate = true)
                => _inner.Write(action, immediate);
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            private readonly Fixture _fixture;
            private readonly SessionDirectory _sessions;
            private readonly int _characterId;
            internal EnhancedClientSession Session { get; }
            internal SemaphoreSlim SendGate { get; }
            private Peer(TcpClient reader, EnhancedClientSession session, Fixture fixture, SessionDirectory sessions, int cid)
            {
                _reader = reader; Session = session; _fixture = fixture; _sessions = sessions; _characterId = cid;
                SendGate = (SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session)
                    ?? throw new InvalidOperationException("The real session send lock was not found."));
            }
            internal static async Task<Peer> CreateAsync(Fixture f, SessionDirectory sessions, int cid)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                var reader = new TcpClient();
                TcpClient writer = null;
                EnhancedClientSession session = null;
                try
                {
                    listener.Start();
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
                    await connecting.WaitAsync(Timeout);
                    reader.NoDelay = true; writer.NoDelay = true;
                    session = new EnhancedClientSession(writer, new GamePacketHeader(), GameNetworkConfig.NormalGamePort)
                    { Account = new AccountRecord { AccountId = Fixture.Account(cid) } };
                    session.Player.CharacterId = cid; session.Player.UserId = checked((ushort)(cid - Leader + 100));
                    session.Player.Level = 70;
                    session.GameSession = new GameSession(session, f.Database, sessionDirectory: sessions);
                    var peer = new Peer(reader, session, f, sessions, cid);
                    peer.ReloadLease();
                    sessions.Register(cid, session);
                    return peer;
                }
                catch
                {
                    if (session != null)
                    {
                        sessions.UnregisterAsync(cid, session).GetAwaiter().GetResult();
                        InventoryContext.Unregister(session.SessionId, cid);
                        session.Close();
                    }
                    else writer?.Dispose();
                    reader.Dispose();
                    throw;
                }
                finally { listener.Stop(); }
            }
            internal void ReloadLease()
            {
                using var connection = _fixture.Database.OpenConnection();
                InventoryContext.Register(Session.SessionId, _characterId,
                    InventoryService.LoadFromDb(connection, _characterId, Fixture.Account(_characterId), _fixture.Database));
            }
            internal async Task<List<byte[]>> DrainAsync()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                using var cancellation = new CancellationTokenSource(Timeout);
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new IOException("Guild progress selftest socket unexpectedly closed.");
                    var header = new byte[HeaderLength];
                    await stream.ReadExactlyAsync(header, cancellation.Token);
                    int length = BitConverter.ToInt32(header, 3);
                    if (length < HeaderLength || length > 65536) throw new InvalidDataException("Invalid guild progress packet length.");
                    var packet = new byte[length]; header.CopyTo(packet, 0);
                    await stream.ReadExactlyAsync(packet.AsMemory(HeaderLength), cancellation.Token);
                    packets.Add(packet);
                }
                return packets;
            }
            public void Dispose()
            {
                _sessions.UnregisterAsync(_characterId, Session).GetAwaiter().GetResult();
                InventoryContext.Unregister(Session.SessionId, _characterId);
                Session.Close(); _reader.Dispose();
            }
        }

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); return false; }
            catch (TException) { return true; }
        }

        private static int RequiredClears(GuildDungeonEntry rule, int requiredRatio)
        {
            if (rule.GaugeLimit <= 0 || rule.GaugeOnClear <= 0 || requiredRatio < 0 || requiredRatio > 100)
                throw new InvalidDataException("Current guild prerequisite units are outside the verified contract.");
            return checked((int)decimal.Ceiling((decimal)requiredRatio * rule.GaugeLimit / (rule.GaugeOnClear * 100m)));
        }

        private static bool ThrowsInvalidData(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }

        private static int Gold(InventoryLease lease) => lease.Inventory.GetMainVirtualCount(0)?.Count ?? 0;

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "guild-progress-" + Guid.NewGuid().ToString("N")));
            private readonly List<InventoryLease> _leases = new();
            internal Fixture()
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 PVF before this selftest.", archive);
                Directory.CreateDirectory(_directory);
                try
                {
                    Database = new GameDatabase(Path.Combine(_directory, "progress.db"), ServerPaths.SchemaFilePath);
                    Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES "
                        + "(201,'guild-progress-a',''),(202,'guild-progress-member',''),(203,'guild-progress-b',''),"
                        + "(204,'guild-progress-outsider',''),(205,'guild-progress-changed','');");
                    foreach (int id in new[] { Leader, Member, OtherLeader, Outsider })
                        Sql($"INSERT INTO characters(character_id,account_id,name,level) VALUES({id},{Account(id)},'guild-progress-{id}',70);");
                    Compose();
                    GuildA = Database.Write((c, t) => GuildRepository.Insert(c, t, Leader, "GuildProgressA", string.Empty));
                    Database.Write((c, t) => GuildRepository.AddMember(c, t, GuildA.Id, Member));
                    GuildB = Database.Write((c, t) => GuildRepository.Insert(c, t, OtherLeader, "GuildProgressB", string.Empty));
                    if (!Service.Catalog.IsValid || !Town.TryGetDungeonGateReturnInfo(GuildAgitAccessPolicy.TownId, 1, out var gate)
                        || gate.WorldMapAreaId != Service.Catalog.WorldMapAreaId)
                        throw new InvalidDataException("Current guild resource directory is closed: " + Service.Catalog.Error);
                    FirstDungeon = Service.Catalog.Rules.Values.Single(row => row.RequiredDungeonConquest.Count == 0).DungeonId;
                }
                catch { Dispose(); throw; }
            }
            internal GameDatabase Database { get; private set; }
            internal GuildRepository Guilds { get; private set; }
            internal GuildDungeonService Service { get; private set; }
            internal DungeonEntryCostService Costs { get; private set; }
            internal DungeonPersistentEffectApplicationService Effects { get; private set; }
            internal GuildRecord GuildA { get; }
            internal GuildRecord GuildB { get; }
            internal int FirstDungeon { get; }
            internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
            internal int MonthId => GuildDungeonMonth.MonthId(UtcNow);
            internal static int Account(int characterId) => characterId - Leader + 201;
            internal GuildDungeonService CreateService(IGameDatabase database, int? unlockRatio = null)
                => new(database, () => UtcNow, unlockRatio);
            private void Compose()
            {
                Guilds = new GuildRepository(Database);
                Service = CreateService(Database);
                Costs = new DungeonEntryCostService(Database);
                Effects = new DungeonPersistentEffectApplicationService(Database.ConnectionString,
                    new DungeonPersistentEffectOutbox(Database.ConnectionString, Guid.NewGuid(), () => UtcNow.ToUnixTimeMilliseconds()), database: Database);
            }
            internal void Reopen()
            {
                SqliteConnection.ClearAllPools();
                Database = new GameDatabase(Database.DatabasePath, ServerPaths.SchemaFilePath);
                Compose();
            }
            internal bool Prepare(int characterId, int dungeonId, out GuildDungeonAdmissionSnapshot admission,
                GuildDungeonService service = null)
            {
                var difficulty = checked((byte)DungeonCatalog.GetDungeonFile(dungeonId).DesignateDungeonDifficulty.Single());
                return (service ?? Service).TryPrepare(characterId, Account(characterId), dungeonId, difficulty,
                    GuildAgitAccessPolicy.TownId, 1, out admission, out _);
            }
            internal GuildDungeonAdmissionSnapshot RequireAdmission(int characterId)
                => Prepare(characterId, FirstDungeon, out var admission) && admission != null ? admission
                    : throw new InvalidOperationException("Fixture could not obtain the real guild admission.");
            internal DungeonEffectId Effect(GuildDungeonAdmissionSnapshot admission, Guid source = default)
                => new(source == Guid.Empty ? Guid.NewGuid() : source, DungeonPersistentEffectKinds.GuildDungeonClearCommit,
                    DungeonEffectScope.Persistent, admission.GuildId);
            internal bool Apply(DungeonEffectId effect, GuildDungeonAdmissionSnapshot admission, out GuildDungeonClearEffectResult result,
                GuildDungeonClearContribution contribution = null)
            {
                contribution ??= new GuildDungeonClearContribution(effect.SourceEventId, MonthId, 1);
                if (contribution.Participants == null && contribution.MemberCount >= 1 && contribution.MemberCount <= 4)
                {
                    var others = new[] { Leader, Member, OtherLeader, Outsider }.Where(id => id != admission.SourceCharacterId);
                    var ids = new[] { admission.SourceCharacterId }.Concat(others).Take(contribution.MemberCount);
                    contribution = contribution with { Participants = ids.Select(id =>
                        new GuildDungeonContributionParticipant(id, id == admission.SourceCharacterId ? admission.SourceAccountId : Account(id))).ToArray() };
                }
                return Effects.TryApplyGuildDungeonClear(effect, admission, out result, out _, contribution);
            }
            internal InventoryLease Lease(int characterId, bool seedGold = true)
            {
                using var connection = Database.OpenConnection();
                var inventory = InventoryService.LoadFromDb(connection, characterId, Account(characterId), Database);
                var lease = InventoryContext.Register(Guid.NewGuid(), characterId, inventory);
                _leases.Add(lease);
                if (seedGold && (!inventory.SetMainVirtualCount(0, 0, 1000) || !InventoryPersistenceService.SaveDirty(lease)))
                    throw new InvalidOperationException("Fixture could not seed and persist owned inventory gold.");
                return lease;
            }
            internal int StoredGold(int characterId) => Database.Read(c =>
                InventoryService.LoadFromDb(c, characterId, Account(characterId), Database).GetMainVirtualCount(0)?.Count ?? 0);

            internal void DropContributionColumns()
                => Sql(MigrationSelfTestFixture.RemoveGuildContributionColumns);
            internal int StoredItemCount(int characterId, int itemId) => Database.Read(c =>
                InventoryService.LoadFromDb(c, characterId, Account(characterId), Database).CountMainItem(itemId));
            internal int Count(int guildId, int dungeonId) => Database.Read(c =>
            {
                var counts = GuildDungeonProgressRepository.Load(c, null, guildId, MonthId);
                return counts.TryGetValue(dungeonId, out int count) ? count : 0;
            });
            internal long StoredCount(int guildId, int dungeonId)
                => Scalar($"SELECT clear_count FROM guild_dungeon_progress WHERE guild_id={guildId} AND dungeon_id={dungeonId};");
            internal long StoredMonth(int guildId, int dungeonId)
                => Scalar($"SELECT month_id FROM guild_dungeon_progress WHERE guild_id={guildId} AND dungeon_id={dungeonId};");
            internal void SetMonth(int guildId, int dungeonId, object monthId)
                => Database.Write((c, t) =>
                {
                    using var command = c.CreateCommand(); command.Transaction = t;
                    try
                    {
                        command.CommandText = "PRAGMA ignore_check_constraints=ON;"; command.ExecuteNonQuery();
                        command.CommandText = "UPDATE guild_dungeon_progress SET month_id=@month WHERE guild_id=@guild AND dungeon_id=@dungeon;";
                        command.Parameters.AddWithValue("@month", monthId); command.Parameters.AddWithValue("@guild", guildId);
                        command.Parameters.AddWithValue("@dungeon", dungeonId); command.ExecuteNonQuery();
                    }
                    finally { command.CommandText = "PRAGMA ignore_check_constraints=OFF;"; command.ExecuteNonQuery(); }
                });
            internal void SetCount(int guildId, int dungeonId, object value, bool bypassChecks = false, int? monthId = null)
                => Database.Write((c, t) =>
                {
                    using var command = c.CreateCommand(); command.Transaction = t;
                    try
                    {
                        if (bypassChecks) { command.CommandText = "PRAGMA ignore_check_constraints=ON;"; command.ExecuteNonQuery(); }
                        command.CommandText = "INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,month_id) VALUES(@guild,@dungeon,@value,@month) "
                            + "ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET clear_count=excluded.clear_count,month_id=excluded.month_id;";
                        command.Parameters.AddWithValue("@guild", guildId); command.Parameters.AddWithValue("@dungeon", dungeonId);
                        command.Parameters.AddWithValue("@value", value);
                        command.Parameters.AddWithValue("@month", monthId ?? MonthId); command.ExecuteNonQuery();
                    }
                    finally
                    {
                        if (bypassChecks) { command.CommandText = "PRAGMA ignore_check_constraints=OFF;"; command.ExecuteNonQuery(); }
                    }
                });
            internal void DeleteCount(int guildId, int dungeonId)
                => Sql($"DELETE FROM guild_dungeon_progress WHERE guild_id={guildId} AND dungeon_id={dungeonId};");
            internal long Scalar(string sql) => Database.Read(c =>
            {
                using var command = c.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });
            internal void Sql(string sql) => Database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
                command.ExecuteNonQuery();
            });
            public void Dispose()
            {
                foreach (var lease in _leases) InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    string path = Path.GetFullPath(Path.Combine(_directory, "progress.db") + suffix);
                    if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Guild progress fixture cleanup escaped its temporary directory.");
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
        }
    }
}
