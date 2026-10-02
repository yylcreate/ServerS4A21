using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarClearCommitSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_CLEAR_COMMIT selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action<Fixture> action)
            {
                try
                {
                    using var fixture = new Fixture();
                    action(fixture);
                }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("ordinary progress and durable replay", f => VerifyProgress(f, Check));
            Section("current participant fences", f => VerifyFences(f, Check));
            Section("atomic rollback and registered recovery", f => VerifyRollbackAndRecovery(f, Check));
            Section("full inventory rewards use the same mailbox transaction", f => VerifyOverflow(f, Check));
            Console.WriteLine($"GUARDIAN_ALTAR_CLEAR_COMMIT assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyProgress(Fixture f, Action<string, bool> check)
        {
            var role = f.NewRole();
            var before = f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor);
            var easy = f.Plan(0);
            var id = f.Effect(role.CharacterId);
            check("easy clear atomically grants three improvement stars, one ticket debit and all configured items",
                f.Apply(role, id, easy, out var first) && first.FirstClear && first.StarsGained == 3
                && first.ProgressSnapshot.Header.FreeTickets == 2
                && first.ProgressSnapshot.Header.StageStars == 3 && first.ProgressSnapshot.Header.AvailableStars == 3
                && first.ProgressSnapshot.Stages.Single(x => x.DungeonId == f.Definition.FirstDungeonId).Progress == 0
                && first.NewPurchases.SequenceEqual(easy.ProgressPlan.UnlockPurchases)
                && easy.ProgressPlan.NextDungeonIds.All(next => first.ProgressSnapshot.Stages.Any(x => x.DungeonId == next))
                && first.ProgressSnapshot.Slots.SequenceEqual(before.Slots)
                && first.ProgressSnapshot.Achievements.SequenceEqual(before.Achievements)
                && f.Items(role.CharacterId, 2749211) == 4 && f.Items(role.CharacterId, 10006059) == 1
                && f.Scalar($"SELECT exp FROM characters WHERE character_id={role.CharacterId};") == 0);
            var committedJson = f.Effects.Outbox.Get(id).ResultJson;
            var restored = f.ComposeEffects();
            check("a recomposed effect owner returns the identical committed result without grants or debit",
                restored.TryApplyGuardianAltarClear(id, role.CharacterId, 1, role, role.SessionId, easy, () => true,
                    out var replay, out _)
                && replay.StarsGained == 3 && replay.FirstClear && replay.ProgressSnapshot.Header.FreeTickets == 2
                && restored.Outbox.Get(id).ResultJson == committedJson && restored.Outbox.Get(id).AttemptCount == 1
                && f.Items(role.CharacterId, 2749211) == 4 && f.Items(role.CharacterId, 10006059) == 1);
            var changed = new GuardianAltarClearCommitPlan(easy.ProgressPlan, f.Plan(1).Rewards);
            check("a changed reward retry cannot replace the accepted plan or committed result",
                !f.Apply(role, id, changed, out _) && f.Effects.Outbox.Get(id).ResultJson == committedJson
                && f.Items(role.CharacterId, 2749211) == 4);
            check("medium and hard improvements award two and one stars in sequence",
                f.Apply(role, f.Effect(role.CharacterId), f.Plan(1), out var medium)
                && !medium.FirstClear && medium.StarsGained == 2 && medium.NewPurchases.Count == 0
                && f.Apply(role, f.Effect(role.CharacterId), f.Plan(2), out var hard)
                && hard.StarsGained == 1 && hard.ProgressSnapshot.Header.StageStars == 6
                && hard.ProgressSnapshot.Header.AvailableStars == 6 && hard.ProgressSnapshot.Header.FreeTickets == 0
                && hard.ProgressSnapshot.Stages.Single(x => x.DungeonId == f.Definition.FirstDungeonId).Progress == 2
                && f.Items(role.CharacterId, 10006059) == 3);
            f.Sql($"UPDATE character_guardian_altar SET secondary_tickets=2 WHERE character_id={role.CharacterId};");
            check("a distinct repeat run keeps higher progress/stars and consumes secondary tickets after free tickets",
                f.Apply(role, f.Effect(role.CharacterId), f.Plan(1), out var repeat)
                && repeat.StarsGained == 0 && !repeat.FirstClear && repeat.NewPurchases.Count == 0
                && repeat.ProgressSnapshot.Header.StageStars == 6 && repeat.ProgressSnapshot.Header.FreeTickets == 0
                && repeat.ProgressSnapshot.Header.SecondaryTickets == 1
                && repeat.ProgressSnapshot.Stages.Single(x => x.DungeonId == f.Definition.FirstDungeonId).Progress == 2
                && f.Items(role.CharacterId, 10006059) == 4);
            var other = f.NewRole();
            var otherId = new DungeonEffectId(id.SourceEventId, DungeonPersistentEffectKinds.GuardianAltarClearCommit,
                DungeonEffectScope.Player, other.CharacterId + 1000000);
            check("one source fact has independent participant targets and durable role progress",
                f.Apply(other, otherId, easy, out var otherResult) && otherResult.StarsGained == 3
                && otherResult.ProgressSnapshot.CharacterId == other.CharacterId
                && f.Items(other.CharacterId, 10006059) == 1 && f.Items(role.CharacterId, 10006059) == 4);
        }

        private static void VerifyFences(Fixture f, Action<string, bool> check)
        {
            var role = f.NewRole();
            var id = f.Effect(role.CharacterId);
            check("a missing optional frozen clear plan cannot authorize success or enqueue intent",
                !f.Effects.TryApplyGuardianAltarClear(id, role.CharacterId, 1, role, role.SessionId,
                    null, () => true, out _, out _) && f.Effects.Outbox.Get(id) == null
                && f.HeaderTickets(role.CharacterId) == 3 && f.Items(role.CharacterId, 10006059) == 0);
            check("an old generation cannot enqueue intent, grant items or consume a ticket",
                !f.Effects.TryApplyGuardianAltarClear(id, role.CharacterId, 1, role, role.SessionId,
                    f.Plan(0), () => false, out _, out _)
                && f.Effects.Outbox.Get(id) == null && f.Items(role.CharacterId, 10006059) == 0
                && f.HeaderTickets(role.CharacterId) == 3);
            check("a displaced session cannot use the current participant inventory lease",
                !f.Effects.TryApplyGuardianAltarClear(id, role.CharacterId, 1, role, Guid.NewGuid(),
                    f.Plan(0), () => true, out _, out _) && f.Effects.Outbox.Get(id) == null);
            var calls = 0;
            var lateId = f.Effect(role.CharacterId);
            check("a generation displaced after progress writes rolls back inventory, stage, stars and ticket",
                !f.Effects.TryApplyGuardianAltarClear(lateId, role.CharacterId, 1, role, role.SessionId,
                    f.Plan(0), () => ++calls < 5, out _, out _)
                && calls == 5 && f.Items(role.CharacterId, 10006059) == 0
                && f.HeaderTickets(role.CharacterId) == 3 && f.StageProgress(role.CharacterId) == -1
                && f.Effects.Outbox.Get(lateId).State == DungeonPersistentEffectState.Failed);
            var fresh = f.NewRole();
            check("ordinary clear owner rejects a skipped hard difficulty without any reward or progress",
                !f.Apply(fresh, f.Effect(fresh.CharacterId), f.Plan(2), out _)
                && f.HeaderTickets(fresh.CharacterId) == 3 && f.StageProgress(fresh.CharacterId) == -1
                && f.Items(fresh.CharacterId, 10006059) == 0);
        }

        private static void VerifyRollbackAndRecovery(Fixture f, Action<string, bool> check)
        {
            var role = f.NewRole();
            var id = f.Effect(role.CharacterId);
            f.Sql($"CREATE TRIGGER guardian_reject_inventory BEFORE INSERT ON character_inventory_items "
                + $"WHEN NEW.character_id={role.CharacterId} BEGIN SELECT RAISE(ABORT,'guardian-inventory-fault'); END;");
            check("inventory persistence failure rolls back prior progress and does not commit the outbox",
                !f.Apply(role, id, f.Plan(0), out _) && f.StageProgress(role.CharacterId) == -1
                && f.HeaderTickets(role.CharacterId) == 3 && f.Items(role.CharacterId, 10006059) == 0
                && f.Effects.Outbox.Get(id).State == DungeonPersistentEffectState.Failed);
            f.Sql("DROP TRIGGER guardian_reject_inventory;");
            check("the same durable intent succeeds once after the inventory fault is removed",
                f.Apply(role, id, f.Plan(0), out var retry) && retry.StarsGained == 3
                && f.Items(role.CharacterId, 10006059) == 1 && f.HeaderTickets(role.CharacterId) == 2);

            var interrupted = f.NewRole();
            var interruptedId = f.Effect(interrupted.CharacterId);
            f.Sql($"CREATE TRIGGER guardian_reject_outbox BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + $"WHEN NEW.character_id={interrupted.CharacterId} AND NEW.state=2 "
                + "BEGIN SELECT RAISE(ABORT,'guardian-outbox-fault'); END;");
            check("outbox commit failure rolls back the already-saved inventory and complete progress",
                !f.Apply(interrupted, interruptedId, f.Plan(0), out _) && f.StageProgress(interrupted.CharacterId) == -1
                && f.HeaderTickets(interrupted.CharacterId) == 3 && f.Items(interrupted.CharacterId, 10006059) == 0
                && f.Effects.Outbox.Get(interruptedId).State == DungeonPersistentEffectState.Failed);
            f.Sql("DROP TRIGGER guardian_reject_outbox;");
            check("interrupted accepted intent can hold the existing outbox reservation",
                f.Effects.Outbox.TryClaim(interruptedId, TimeSpan.FromMinutes(1), out _, out _)
                    == DungeonPersistentEffectClaimResult.Claimed);
            InventoryContext.Unregister(interrupted.SessionId, interrupted.CharacterId);
            var coldDefinitionLoads = 0;
            var coldProgress = new GuardianAltarProgressService(f.Database, new DailyResetService(f.Database), () =>
            {
                coldDefinitionLoads++;
                return f.Definition;
            });
            var restarted = f.ComposeEffects(coldProgress);
            var recovery = restarted.RecoverCharacter(interrupted.CharacterId);
            var again = restarted.RecoverCharacter(interrupted.CharacterId);
            check("registered recovery reclaims foreign-process leases and commits the frozen reward once",
                recovery.CommittedCount == 1 && recovery.FailedCount == 0 && !recovery.HasRemaining && coldDefinitionLoads == 1
                && again.CommittedCount == 0 && !again.HasRemaining
                && restarted.Outbox.Get(interruptedId).State == DungeonPersistentEffectState.Committed
                && f.StageProgress(interrupted.CharacterId) == 0 && f.HeaderTickets(interrupted.CharacterId) == 2
                && f.Items(interrupted.CharacterId, 10006059) == 1 && f.Items(interrupted.CharacterId, 2749211) == 4);
            var invalid = f.NewRole();
            var invalidId = f.Effect(invalid.CharacterId);
            f.Effects.Outbox.Enqueue(new DungeonPersistentEffectDefinition
            {
                EffectId = invalidId, CharacterId = invalid.CharacterId, AccountId = 1,
                PayloadVersion = 2, PayloadJson = "{}",
            });
            InventoryContext.Unregister(invalid.SessionId, invalid.CharacterId);
            f.ComposeEffects().RecoverCharacter(invalid.CharacterId);
            check("unknown Guardian payload versions are dead-lettered without business mutations",
                f.Effects.Outbox.Get(invalidId).State == DungeonPersistentEffectState.DeadLetter
                && f.HeaderTickets(invalid.CharacterId) == 3 && f.StageProgress(invalid.CharacterId) == -1
                && f.Items(invalid.CharacterId, 10006059) == 0);
        }

        private static void VerifyOverflow(Fixture f, Action<string, bool> check)
        {
            var role = f.NewRole();
            lock (role.SyncRoot)
            {
                for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
                {
                    if (!InventoryRewardGrantService.TryCreateOnly(3030, ItemCreateReason.DungeonDrop, 1,
                            out var filler) || filler.Core == null)
                        throw new InvalidOperationException("Guardian overflow fixture could not create its filler item.");
                    role.Inventory.SetItem(InventoryListType.Main, slot, filler.Core);
                }
                if (!InventoryPersistenceService.SaveDirty(role))
                    throw new InvalidOperationException("Guardian overflow fixture could not persist its full inventory.");
            }
            var id = f.Effect(role.CharacterId);
            f.Sql($"CREATE TRIGGER guardian_reject_mail_outbox BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + $"WHEN NEW.character_id={role.CharacterId} AND NEW.state=2 "
                + "BEGIN SELECT RAISE(ABORT,'guardian-mail-outbox-fault'); END;");
            check("a late outbox fault rolls back all overflow mail, ticket and star writes together",
                !f.Apply(role, id, f.Plan(0), out _) && f.Mails(role.CharacterId) == 0
                && f.MailItems(role.CharacterId, 10006059) == 0
                && f.HeaderTickets(role.CharacterId) == 3 && f.StageProgress(role.CharacterId) == -1);
            f.Sql("DROP TRIGGER guardian_reject_mail_outbox;");
            check("full inventory receives every frozen reward through transaction-bound mailbox delivery",
                f.Apply(role, id, f.Plan(0), out var result) && result.MailedRewardCount == f.Plan(0).Rewards.Count
                && result.Changes.Count == 0 && f.Items(role.CharacterId, 10006059) == 0
                && f.Mails(role.CharacterId) == 1 && f.MailItems(role.CharacterId, 10006059) == 1
                && f.MailItems(role.CharacterId, 2749211) == 4 && f.HeaderTickets(role.CharacterId) == 2);
            check("replaying overflow delivery does not create a second mail or ticket debit",
                f.Apply(role, id, f.Plan(0), out _) && f.Mails(role.CharacterId) == 1
                && f.MailItems(role.CharacterId, 10006059) == 1 && f.HeaderTickets(role.CharacterId) == 2);
        }

        private sealed class Fixture : IDisposable
        {
            internal static readonly DateTime Anchor = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
            private readonly string _path = Path.Combine(Path.GetTempPath(), "guardian-clear-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly List<InventoryLease> _leases = new();
            private int _nextCharacterId = 75000;
            internal Fixture()
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archive);
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-clear-test','');");
                Definition = GuardianAltarMenuDefinition.Load();
                Progress = new GuardianAltarProgressService(Database, new DailyResetService(Database), Definition);
                Effects = ComposeEffects();
            }
            internal GameDatabase Database { get; }
            internal GuardianAltarMenuDefinition Definition { get; }
            internal GuardianAltarProgressService Progress { get; }
            internal DungeonPersistentEffectApplicationService Effects { get; }
            internal InventoryLease NewRole()
            {
                var cid = ++_nextCharacterId;
                Sql($"INSERT INTO characters(character_id,account_id,name,level) VALUES({cid},1,'guardian-clear-{cid}',70);");
                Progress.LoadMenu(cid, Anchor);
                using var connection = Database.OpenConnection();
                var lease = InventoryContext.Register(Guid.NewGuid(), cid,
                    InventoryService.LoadFromDb(connection, cid, 1, Database));
                _leases.Add(lease);
                return lease;
            }
            internal DungeonPersistentEffectApplicationService ComposeEffects(GuardianAltarProgressService owner = null)
            {
                var effects = new DungeonPersistentEffectApplicationService(Database.ConnectionString,
                    new DungeonPersistentEffectOutbox(Database.ConnectionString, Guid.NewGuid()), database: Database,
                    overflowRewardSink: new MailboxInventoryOverflowRewardSink(new MailboxService(new MailboxRepository(Database))));
                effects.BindGuardianAltarProgressService(owner ?? Progress);
                return effects;
            }
            internal GuardianAltarClearCommitPlan Plan(byte difficulty)
            {
                var dgn = Definition.FirstDungeonId;
                var rewards = GuardianAltarClearRewardFile.Parse(DungeonCatalog.GetDungeonFile(dgn));
                if (!rewards.IsValid) throw new InvalidDataException(rewards.InvalidReason);
                return new GuardianAltarClearCommitPlan(Definition.CreateClearProgressPlan(dgn, difficulty),
                    rewards.Difficulties.Single(x => x.DifficultyName == new[] { "easy", "medium", "hard" }[difficulty]).Items);
            }
            internal DungeonEffectId Effect(int cid) => new(Guid.NewGuid(),
                DungeonPersistentEffectKinds.GuardianAltarClearCommit, DungeonEffectScope.Player, cid + 1000000);
            internal bool Apply(InventoryLease role, DungeonEffectId id, GuardianAltarClearCommitPlan plan,
                out GuardianAltarClearCommitResult result) => Effects.TryApplyGuardianAltarClear(id,
                    role.CharacterId, role.AccountId, role, role.SessionId, plan, () => true, out result, out _);
            internal int Items(int cid, int itemId)
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, cid, 1, Database).CountMainItem(itemId);
            }
            internal long HeaderTickets(int cid) => Scalar($"SELECT free_tickets+secondary_tickets FROM character_guardian_altar WHERE character_id={cid};");
            internal long StageProgress(int cid) => Scalar($"SELECT progress FROM character_guardian_altar_stages WHERE character_id={cid} AND dungeon_id={Definition.FirstDungeonId};");
            internal long Mails(int cid) => Scalar($"SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id={cid};");
            internal long MailItems(int cid, int itemId) => Scalar($"SELECT COALESCE(SUM(a.item_count),0) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.receiver_character_id={cid} AND a.item_template_id={itemId};");
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
                foreach (var lease in _leases) InventoryContext.Unregister(lease.SessionId, lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
