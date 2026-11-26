using System;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarClearCommitSelfTest
    {
        private static void VerifySurvivalTerminalCommit(Fixture f, Action<string, bool> check)
        {
            var catalog = SurvivalCatalog(f);
            var dungeonId = catalog.Stages.Single(stage => stage.IsSurvival).DungeonId;
            InventoryLease Ready()
            {
                var role = f.NewRole();
                SeedOrdinaryHistory(f, catalog, role.CharacterId, 0);
                f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor);
                return role;
            }
            DungeonEffectId Effect(InventoryLease role) => new(Guid.NewGuid(),
                DungeonPersistentEffectKinds.GuardianAltarSurvivalCommit, DungeonEffectScope.Player, role.CharacterId + 1000000);
            bool Apply(InventoryLease role, DungeonEffectId effect, GuardianAltarSurvivalCommitPlan plan,
                out GuardianAltarClearCommitResult result) => f.Effects.TryApplyGuardianAltarSurvival(effect,
                    role.CharacterId, role.AccountId, role, role.SessionId, plan, () => true, out result, out _);

            var zero = Ready();
            var zeroBefore = f.Progress.ReadExistingMenu(zero.CharacterId, zero.AccountId);
            var zeroPlan = f.Definition.CreateSurvivalCommitPlan(dungeonId, 0);
            var zeroId = Effect(zero);
            check("zero completed survival rounds commit no reward, ticket, star, purchase or false best round",
                Apply(zero, zeroId, zeroPlan, out var empty) && empty.ClearedRounds == 0 && empty.BestRound == 0
                && !empty.Improved && empty.StarsGained == 0 && !empty.FirstClear
                && empty.Changes.Count == 0 && empty.NewPurchases.Count == 0
                && SameSurvivalSnapshot(zeroBefore, empty.ProgressSnapshot)
                && f.Effects.Outbox.Get(zeroId).State == DungeonPersistentEffectState.Committed);

            foreach (var completed in new[] { 1, 2, 3, 48, 49, 99 })
            {
                var role = Ready();
                var before = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
                var plan = f.Definition.CreateSurvivalCommitPlan(dungeonId, completed);
                var id = Effect(role);
                check("survival terminal " + completed + " grants its typed round range and persists best once without ordinary progress",
                    Apply(role, id, plan, out var result) && result.ClearedRounds == completed
                    && result.BestRound == completed && result.Improved && result.StarsGained == 0 && !result.FirstClear
                    && result.ProgressSnapshot.Header == (before.Header with
                        { FreeTickets = before.Header.FreeTickets - 1, SurvivalBestPhase = (short)completed })
                    && result.ProgressSnapshot.Stages.SequenceEqual(before.Stages)
                    && SameSurvivalOwnedRows(before, result.ProgressSnapshot)
                    && plan.Rewards.GroupBy(item => item.ItemId).All(group =>
                        f.Items(role.CharacterId, group.Key) == group.Sum(item => item.Count)));
                var committed = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
                var journal = f.Effects.Outbox.Get(id);
                check("survival terminal " + completed + " retry returns its committed result without a second reward or debit",
                    Apply(role, id, plan, out var replay) && replay.ClearedRounds == completed
                    && SameSurvivalSnapshot(committed, f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId))
                    && f.Effects.Outbox.Get(id).PayloadJson == journal.PayloadJson
                    && f.Effects.Outbox.Get(id).ResultJson == journal.ResultJson
                    && plan.Rewards.GroupBy(item => item.ItemId).All(group =>
                        f.Items(role.CharacterId, group.Key) == group.Sum(item => item.Count)));
            }

            var repeat = Ready();
            var higher = f.Definition.CreateSurvivalCommitPlan(dungeonId, 3);
            var lower = f.Definition.CreateSurvivalCommitPlan(dungeonId, 1);
            Apply(repeat, Effect(repeat), higher, out _);
            check("a later shorter survival grants only its own run range and never reduces the durable best",
                Apply(repeat, Effect(repeat), lower, out var shorter) && shorter.ClearedRounds == 1
                && shorter.BestRound == 3 && !shorter.Improved && shorter.ProgressSnapshot.Header.FreeTickets == 1
                && higher.Rewards.Concat(lower.Rewards).GroupBy(item => item.ItemId).All(group =>
                    f.Items(repeat.CharacterId, group.Key) == group.Sum(item => item.Count)));

            var failing = Ready();
            var original = f.Progress.ReadExistingMenu(failing.CharacterId, failing.AccountId);
            var failureId = Effect(failing);
            var terminal = f.Definition.CreateSurvivalCommitPlan(dungeonId, 2);
            f.Sql("CREATE TRIGGER guardian_reject_survival_terminal BEFORE UPDATE OF survival_best_phase ON character_guardian_altar "
                + $"WHEN NEW.character_id={failing.CharacterId} BEGIN SELECT RAISE(ABORT,'guardian-survival-terminal-test'); END;");
            check("survival best-row failure rolls back rewards, balance and outbox commit in their existing transaction",
                !Apply(failing, failureId, terminal, out _)
                && SameSurvivalSnapshot(original, f.Progress.ReadExistingMenu(failing.CharacterId, failing.AccountId))
                && terminal.Rewards.All(item => f.Items(failing.CharacterId, item.ItemId) == 0)
                && f.Effects.Outbox.Get(failureId).State == DungeonPersistentEffectState.Failed);
            f.Sql("DROP TRIGGER guardian_reject_survival_terminal;");
            var frozen = f.Effects.Outbox.Get(failureId).PayloadJson;
            InventoryContext.Unregister(failing.SessionId, failing.CharacterId);
            var recovery = f.ComposeEffects();
            var recovered = recovery.RecoverCharacter(failing.CharacterId);
            check("survival pending effect recovers with the same frozen terminal, reward range and single ticket",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && recovery.Outbox.Get(failureId).PayloadJson == frozen
                && f.Progress.ReadExistingMenu(failing.CharacterId, failing.AccountId).Header.SurvivalBestPhase == 2
                && f.HeaderTickets(failing.CharacterId) == 2
                && terminal.Rewards.GroupBy(item => item.ItemId).All(group =>
                    f.Items(failing.CharacterId, group.Key) == group.Sum(item => item.Count)));
            check("survival recovery repeats without another grant or debit",
                recovery.RecoverCharacter(failing.CharacterId).CommittedCount == 0 && f.HeaderTickets(failing.CharacterId) == 2);

            var invalid = Ready();
            var beforeInvalid = f.Progress.ReadExistingMenu(invalid.CharacterId, invalid.AccountId);
            var changed = new GuardianAltarSurvivalCommitPlan(terminal.RidableId, terminal.DungeonId,
                terminal.RoundCount + 1, terminal.ClearedRounds, terminal.Rewards);
            check("survival frozen reward rows must match the actual completed-round range before enqueue",
                !Apply(invalid, Effect(invalid), changed, out _)
                && SameSurvivalSnapshot(beforeInvalid, f.Progress.ReadExistingMenu(invalid.CharacterId, invalid.AccountId))
                && f.Scalar($"SELECT COUNT(*) FROM dungeon_persistent_effect_outbox WHERE character_id={invalid.CharacterId};") == 0);
            f.Sql($"DELETE FROM character_guardian_altar_stages WHERE character_id={invalid.CharacterId} AND dungeon_id={catalog.Stages.First(stage => !stage.IsSurvival).DungeonId};");
            check("a standalone survival row without complete ordinary prerequisites cannot claim a terminal reward",
                !Apply(invalid, Effect(invalid), terminal, out _)
                && terminal.Rewards.All(item => f.Items(invalid.CharacterId, item.ItemId) == 0));
        }
    }
}
