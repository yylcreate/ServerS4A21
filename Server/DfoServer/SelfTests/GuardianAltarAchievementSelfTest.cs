using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarAchievementSelfTest
    {
        private const string AchievementPath = "etc/advancealtar/advancealtarachievementreward.etc";
        private const string TextPath = "etc/hardcodetexttag_chn.etc";
        private const string SyntheticText = "`title` `achievement title` `postal` `mail title` `letter` `mail body`";

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ACHIEVEMENT selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("current PVF reward catalog", () => VerifyResources(Check));
            Section("strict typed reward parsing", () => VerifyParsing(Check));
            Section("durable eligibility and mail delivery", () => VerifyClaims(Check));
            Section("claim and mail atomic rollback", () => VerifyRollback(Check));
            Section("concurrent durable claim", () => VerifyConcurrency(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_ACHIEVEMENT assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyResources(Action<string, bool> check)
        {
            RequireArchive();
            var typed = GuardianAltarAchievementRewardFile.Parse(
                PvfArchiveAccessor.ReadText(AchievementPath), PvfArchiveAccessor.ReadText(TextPath));
            check("current PVF exposes all thirteen typed achievement rows", typed.IsValid && typed.Rewards.Count == 13);
            if (!typed.IsValid) throw new InvalidDataException(typed.InvalidReason);
            // Frozen resource assertions; production reads these values from PVF.
            var expected = new[]
            {
                (0, 4, 2, 8), (0, 8, 3, 10), (0, 12, 5, 12),
                (1, 4, 4, 12), (1, 8, 6, 14), (1, 12, 10, 16),
                (2, 1, 5, 10), (2, 2, 6, 15), (2, 4, 7, 20), (2, 6, 8, 25),
                (2, 8, 9, 30), (2, 10, 10, 40), (2, 12, 20, 100)
            };
            for (var index = 0; index < expected.Length; index++)
            {
                var row = typed.Rewards[index];
                var (difficulty, count, jars, souls) = expected[index];
                check($"achievement type {index + 1} retains current difficulty, distinct-stage threshold and item quantities",
                    row.Type == index + 1 && row.Difficulty == difficulty && row.ClearCount == count
                    && row.Items.Any(x => x.ItemId == 2749213 && x.Count == jars && x.Kind == "normal")
                    && row.Items.Any(x => x.ItemId == 2749211 && x.Count == souls && x.Kind == "normal"));
                check($"achievement type {row.Type} resolves its resource-owned title and postal text",
                    row.RewardTitle.Contains("关卡", StringComparison.Ordinal)
                    && row.PostalTitle == "祭坛成就奖励" && row.PostalLetter.Contains("相应成就", StringComparison.Ordinal));
            }
            check("the two advanced chest milestones and final equipment title are retained",
                typed.Rewards.Single(x => x.Type == 6).Items.Any(x => x.ItemId == 2749214 && x.Count == 1)
                && typed.Rewards.Single(x => x.Type == 12).Items.Any(x => x.ItemId == 2749214 && x.Count == 1)
                && typed.Rewards.Single(x => x.Type == 13).Items.Any(x => x.ItemId == 2749215 && x.Count == 2)
                && typed.Rewards.Single(x => x.Type == 13).Items.Any(x => x.ItemId == 2747020 && x.Count == 1));
            var definition = GuardianAltarMenuDefinition.Load();
            check("the production definition owns the same typed achievement catalog",
                definition.AchievementRewards.Select(x => (x.Type, x.Difficulty, x.ClearCount))
                    .SequenceEqual(typed.Rewards.Select(x => (x.Type, x.Difficulty, x.ClearCount))));
        }

        private static void VerifyParsing(Action<string, bool> check)
        {
            var valid = SyntheticRow();
            var parsed = GuardianAltarAchievementRewardFile.Parse(valid, SyntheticText);
            check("typed reward parsing retains item kinds, quantities and resolved postal values",
                parsed.IsValid && parsed.Rewards.Single().Items.Single().ItemId == 2749211
                && parsed.Rewards.Single().Items.Single().Count == 3
                && parsed.Rewards.Single().PostalTitle == "mail title" && parsed.Rewards.Single().PostalLetter == "mail body");
            var multiline = GuardianAltarAchievementRewardFile.Parse(valid,
                SyntheticText.Replace("`mail body`", "`first line\nsecond line`", StringComparison.Ordinal));
            check("postal localization preserves complete multiline backtick values",
                multiline.IsValid && multiline.Rewards.Single().PostalLetter == "first line\nsecond line");
            foreach (var (label, source, text) in new[]
            {
                ("duplicate type", valid + valid, SyntheticText),
                ("unknown difficulty", valid.Replace("`easy`", "`hero`", StringComparison.Ordinal), SyntheticText),
                ("zero stage count", valid.Replace("[stage clear count]\n4", "[stage clear count]\n0", StringComparison.Ordinal), SyntheticText),
                ("unsupported kind", valid.Replace("`normal`", "`gold`", StringComparison.Ordinal), SyntheticText),
                ("incomplete triple", valid.Replace("2749211 3", "2749211", StringComparison.Ordinal), SyntheticText),
                ("zero quantity", valid.Replace("2749211 3", "2749211 0", StringComparison.Ordinal), SyntheticText),
                ("overflow quantity", valid.Replace("2749211 3", "2749211 2147483648", StringComparison.Ordinal), SyntheticText),
                ("missing reward closing tag", valid.Replace("[/reward item]", "", StringComparison.Ordinal), SyntheticText),
                ("missing postal field", valid.Replace("[postal letter]\n`letter`", "", StringComparison.Ordinal), SyntheticText),
                ("unknown field", valid.Replace("[/reward data]", "[unknown]\n1\n[/reward data]", StringComparison.Ordinal), SyntheticText),
                ("unresolved text key", valid, "`title` `achievement title` `postal` `mail title`"),
                ("ambiguous text key", valid, SyntheticText + " `postal` `other mail title`"),
                ("unquoted localization value", valid, SyntheticText.Replace("`mail title`", "mailtitle", StringComparison.Ordinal)),
                ("missing localization", valid, "")
            })
            {
                var invalid = GuardianAltarAchievementRewardFile.Parse(source, text);
                check(label + " fails closed without partial rewards", !invalid.IsValid
                    && invalid.Rewards.Count == 0 && !string.IsNullOrWhiteSpace(invalid.InvalidReason));
            }
        }

        private static string SyntheticRow() => "[reward data]\n[type]\n1\n[stage difficulty]\n`easy`\n"
            + "[stage clear count]\n4\n[icon image]\n2\n[reward item]\n`normal` 2749211 3\n[/reward item]\n"
            + "[reward title]\n`title`\n[postal title]\n`postal`\n[postal letter]\n`letter`\n[/reward data]\n";

        private static void VerifyClaims(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            foreach (var rule in fixture.Definition.AchievementRewards)
            {
                var role = fixture.NewRole();
                fixture.Complete(role, rule.ClearCount - 1, rule.Difficulty);
                var below = fixture.Progress.LoadMenu(role, Fixture.Anchor);
                fixture.Complete(role, rule.ClearCount, rule.Difficulty);
                var exact = fixture.Progress.LoadMenu(role, Fixture.Anchor);
                fixture.Complete(role, rule.ClearCount, 2);
                var higher = fixture.Progress.LoadMenu(role, Fixture.Anchor);
                check($"type {rule.Type} honors its exact distinct-stage threshold and higher difficulty inheritance",
                    !fixture.Definition.IsAchievementEligible(below, rule)
                    && fixture.Definition.IsAchievementEligible(exact, rule)
                    && fixture.Definition.IsAchievementEligible(higher, rule));
                if (rule.Difficulty > 0)
                {
                    var lowerRole = fixture.NewRole();
                    fixture.Complete(lowerRole, rule.ClearCount, rule.Difficulty - 1);
                    check($"type {rule.Type} cannot be satisfied by lower difficulty stage clears",
                        !fixture.Definition.IsAchievementEligible(fixture.Progress.LoadMenu(lowerRole, Fixture.Anchor), rule));
                }
            }
            var cid = fixture.NewRole();
            var before = fixture.Progress.LoadMenu(cid, Fixture.Anchor);
            check("a fresh role and unknown type cannot claim a reward or create mail",
                !fixture.Progress.ClaimAchievement(cid, 1, 1).Accepted
                && !fixture.Progress.ClaimAchievement(cid, 1, 99999).Accepted
                && fixture.MailCount(cid) == 0 && fixture.ClaimCount(cid) == 0);
            fixture.Complete(cid, 3, 0);
            check("three different ordinary stages do not meet the four-stage achievement",
                !fixture.Progress.ClaimAchievement(cid, 1, 1).Accepted && fixture.MailCount(cid) == 0);
            fixture.CompleteSurvival(cid);
            check("the separate survival stage cannot supply the missing ordinary stage",
                !fixture.Progress.ClaimAchievement(cid, 1, 1).Accepted && fixture.MailCount(cid) == 0);
            fixture.Complete(cid, 4, 2);
            var result = fixture.Progress.ClaimAchievement(cid, 1, 1);
            check("higher cleared difficulty satisfies the lower milestone and mail commits with claimed state",
                result.Accepted && result.Changed && result.Progress.Achievements.Single() == new GuardianAltarAchievement(1, 1)
                && fixture.MailCount(cid) == 1 && fixture.ClaimCount(cid) == 1);
            check("the first milestone mails the exact PVF amounts and permanent localized system message",
                fixture.ItemCount(cid, 2749213) == 2 && fixture.ItemCount(cid, 2749211) == 8
                && fixture.Scalar($"SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id={cid} AND unlimited_flag=1 AND mail_type=1 AND title='祭坛成就奖励';") == 1
                && fixture.Scalar($"SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE receiver_character_id={cid};") == 1);
            var replay = fixture.Progress.ClaimAchievement(cid, 1, 1);
            var reloaded = fixture.NewOwner().LoadMenu(cid, Fixture.Anchor);
            check("duplicate and new-owner reload keep one mail and the durable claimed bit",
                replay.Accepted && !replay.Changed && fixture.MailCount(cid) == 1
                && reloaded.Achievements.Single() == new GuardianAltarAchievement(1, 1));
            check("mail delivery leaves tickets, stars, purchases and deployed slots unchanged",
                result.Progress.Header == before.Header && result.Progress.Purchases.SequenceEqual(before.Purchases)
                && result.Progress.Slots.SequenceEqual(before.Slots) && !InventoryContext.TryGetLease(cid, out _));
            fixture.Sql($"DELETE FROM mailbox_messages WHERE receiver_character_id={cid};");
            replay = fixture.NewOwner().ClaimAchievement(cid, 1, 1);
            check("removing old mail cannot reopen the durable achievement grant",
                replay.Accepted && !replay.Changed && fixture.MailCount(cid) == 0 && fixture.ClaimCount(cid) == 1);
            check("wrong character ownership and stale generation cannot grant mail",
                Throws<InvalidOperationException>(() => fixture.Progress.ClaimAchievement(cid, 2, 4))
                && Throws<InvalidOperationException>(() => fixture.Progress.ClaimAchievement(cid, 1, 4, () => false))
                && fixture.MailCount(cid) == 0 && fixture.ClaimCount(cid) == 1);

            var final = fixture.NewRole();
            fixture.Complete(final, 12, 2);
            result = fixture.Progress.ClaimAchievement(final, 1, 13);
            check("the final distinct-stage milestone delivers every PVF item including its equipment title",
                result.Accepted && result.Changed && fixture.ItemCount(final, 2749213) == 20
                && fixture.ItemCount(final, 2749211) == 100 && fixture.ItemCount(final, 2749215) == 2
                && fixture.ItemCount(final, 2747020) == 1);
            check("the title attachment retains a complete equipment core rather than a forced stackable kind",
                fixture.Scalar($"SELECT COUNT(*) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.receiver_character_id={final} AND a.item_template_id=2747020 AND length(a.item_core)={ItemCore.Size} AND a.item_kind='equipment';") == 1);
            check("claim support uses the existing schema version without a cumulative-clear table",
                fixture.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion);
        }

        private static void VerifyRollback(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            foreach (var (label, table) in new[]
            {
                ("message insertion", "mailbox_messages"),
                ("attachment insertion", "mailbox_attachments"),
                ("system audit insertion", "mailbox_system_mail_audit")
            })
            {
                var cid = fixture.NewRole();
                fixture.Complete(cid, 4, 0);
                fixture.Sql($"CREATE TRIGGER guardian_mail_failure BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'guardian-mail-test'); END;");
                check(label + " failure rolls back claimed state and every mail table",
                    Throws<SqliteException>(() => fixture.Progress.ClaimAchievement(cid, 1, 1))
                    && fixture.MailCount(cid) == 0 && fixture.ClaimCount(cid) == 0
                    && fixture.Scalar("SELECT COUNT(*) FROM mailbox_attachments;") == 0);
                fixture.Sql("DROP TRIGGER guardian_mail_failure;");
                check(label + " retry succeeds once after the failed transaction",
                    fixture.Progress.ClaimAchievement(cid, 1, 1).Changed && fixture.MailCount(cid) == 1);
                fixture.Sql($"DELETE FROM mailbox_messages WHERE receiver_character_id={cid};");
            }
            var expired = fixture.NewRole();
            fixture.Complete(expired, 4, 0);
            var checks = 0;
            check("identity expiry at the final transaction fence rolls back a created mail and claimed bit",
                Throws<InvalidOperationException>(() => fixture.Progress.ClaimAchievement(expired, 1, 1,
                    () => ++checks < 3)) && checks == 3 && fixture.MailCount(expired) == 0
                && fixture.ClaimCount(expired) == 0);
            var noMail = fixture.NewOwner(mailEnabled: false);
            check("missing mailbox authority does not persist a successful claim",
                Throws<InvalidOperationException>(() => noMail.ClaimAchievement(expired, 1, 1))
                && fixture.MailCount(expired) == 0 && fixture.ClaimCount(expired) == 0);
            check("a valid retry after source expiry can deliver exactly once",
                fixture.Progress.ClaimAchievement(expired, 1, 1).Changed && fixture.MailCount(expired) == 1
                && !fixture.Progress.ClaimAchievement(expired, 1, 1).Changed && fixture.MailCount(expired) == 1);
        }

        private static void VerifyConcurrency(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var cid = fixture.NewRole();
            fixture.Complete(cid, 4, 0);
            var second = fixture.NewOwner();
            var results = Task.WhenAll(Task.Run(() => fixture.Progress.ClaimAchievement(cid, 1, 1)),
                Task.Run(() => second.ClaimAchievement(cid, 1, 1))).GetAwaiter().GetResult();
            check("two durable owners serialize the same claim to one mail and one claimed transition",
                results.All(x => x.Accepted) && results.Count(x => x.Changed) == 1
                && fixture.MailCount(cid) == 1 && fixture.ClaimCount(cid) == 1);
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }

        private static void RequireArchive()
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", path);
        }

        private sealed class Fixture : IDisposable
        {
            internal static readonly DateTime Anchor = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            private readonly string _path = Path.Combine(Path.GetTempPath(), "guardian-achievement-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly int[] _ordinary;
            private readonly int _survival;
            private int _nextCharacterId = 78000;
            internal GameDatabase Database { get; }
            internal GuardianAltarMenuDefinition Definition { get; }
            internal MailboxService Mailbox { get; }
            internal GuardianAltarProgressService Progress { get; }

            internal Fixture()
            {
                RequireArchive();
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-achievement-test','');");
                Definition = GuardianAltarMenuDefinition.Load();
                var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
                var map = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText("stagemap/"
                    + (list.GetById(Definition.RidableId)?.FilePath ?? throw new InvalidDataException("Guardian stage map is not registered."))));
                if (!map.IsValid) throw new InvalidDataException(map.InvalidReason);
                _ordinary = map.Stages.Where(x => !x.IsSurvival).Select(x => x.DungeonId).OrderBy(x => x).ToArray();
                _survival = map.Stages.Single(x => x.IsSurvival).DungeonId;
                Mailbox = new MailboxService(new MailboxRepository(Database));
                Progress = NewOwner();
            }

            internal GuardianAltarProgressService NewOwner(bool mailEnabled = true)
                => new(Database, new DailyResetService(Database), Definition, mailEnabled ? Mailbox : null);

            internal int NewRole()
            {
                var cid = ++_nextCharacterId;
                Sql($"INSERT INTO characters(character_id,account_id,name,level) VALUES({cid},1,'guardian-achievement-{cid}',70);");
                Progress.LoadMenu(cid, Anchor);
                return cid;
            }

            internal void Complete(int cid, int count, int difficulty)
            {
                foreach (var dungeonId in _ordinary.Take(count))
                    SetStage(cid, dungeonId, difficulty);
            }

            internal void CompleteSurvival(int cid) => SetStage(cid, _survival, 2);
            private void SetStage(int cid, int dungeonId, int difficulty)
                => Sql($"INSERT INTO character_guardian_altar_stages(character_id,ridable_id,dungeon_id,progress) VALUES({cid},{Definition.RidableId},{dungeonId},{difficulty}) ON CONFLICT(character_id,ridable_id,dungeon_id) DO UPDATE SET progress=excluded.progress;");
            internal long MailCount(int cid) => Scalar($"SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id={cid};");
            internal long ClaimCount(int cid) => Scalar($"SELECT COUNT(*) FROM character_guardian_altar_achievements WHERE character_id={cid} AND claimed=1;");
            internal long ItemCount(int cid, int itemId) => Scalar($"SELECT COALESCE(SUM(a.item_count),0) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.receiver_character_id={cid} AND a.item_template_id={itemId};");
            internal long Scalar(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });
            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                return command.ExecuteNonQuery();
            });
            public void Dispose()
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
