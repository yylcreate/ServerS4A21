using System;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarClearCommitSelfTest
    {
        private static void VerifySurvivalMenuRepair(Fixture f, Action<string, bool> check)
        {
            var catalog = SurvivalCatalog(f);
            var survivalIds = catalog.Stages.Where(stage => stage.IsSurvival).Select(stage => stage.DungeonId).ToArray();
            foreach (var difficulty in new[] { 0, 1, 2 })
            {
                var role = f.NewRole();
                SeedOrdinaryHistory(f, catalog, role.CharacterId, difficulty);
                // The same-day receipt was already claimed by NewRole. Zero
                // tickets must not prevent repair or authorize another refill.
                if (difficulty == 0)
                    f.Sql($"UPDATE character_guardian_altar SET free_tickets=0,secondary_tickets=0,available_stars=0 WHERE character_id={role.CharacterId};");
                var before = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
                var readOnly = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
                check("existing returned-menu reads leave fully earned difficulty " + difficulty + " survival unlock unchanged",
                    SameSurvivalSnapshot(before, readOnly)
                    && !readOnly.Stages.Any(stage => survivalIds.Contains(stage.DungeonId)));
                var after = f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor);
                check("open menu repairs fully earned difficulty " + difficulty + " history with only persistent survival rows and a hint",
                    after.Header == (before.Header with { MenuHintFlag = 1 })
                    && after.Stages.Where(stage => !survivalIds.Contains(stage.DungeonId)).SequenceEqual(before.Stages)
                    && survivalIds.All(id => after.Stages.Single(stage => stage.DungeonId == id).Progress == -1)
                    && SameSurvivalOwnedRows(before, after)
                    && f.HeaderTickets(role.CharacterId) == before.Header.FreeTickets + before.Header.SecondaryTickets
                    && f.Items(role.CharacterId, 10006059) == 0 && f.Mails(role.CharacterId) == 0
                    && f.Scalar($"SELECT COUNT(*) FROM dungeon_persistent_effect_outbox WHERE character_id={role.CharacterId};") == 0);
                check("same-day survival repair is idempotent and a new owner reads the same durable menu",
                    SameSurvivalSnapshot(after, f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor))
                    && SameSurvivalSnapshot(after, new GuardianAltarProgressService(f.Database,
                        new DailyResetService(f.Database), f.Definition).ReadExistingMenu(role.CharacterId, role.AccountId)));
                var update = GuardianAltarMenuBodyBuilder.BuildUpdateRidable(after, 0);
                var projected = Enumerable.Range(0, checked((int)BitConverter.ToUInt32(update, 34)))
                    .Select(index => new GuardianAltarStage(BitConverter.ToInt32(update, 38 + index * 8),
                        BitConverter.ToInt32(update, 42 + index * 8))).ToArray();
                check("the real UPDATE_RIDABLE stage vector carries each earned survival id with unplayed progress",
                    projected.SequenceEqual(after.Stages)
                    && survivalIds.All(id => projected.Any(stage => stage.DungeonId == id && stage.Progress == -1)));
            }
            var displaced = f.NewRole();
            SeedOrdinaryHistory(f, catalog, displaced.CharacterId, 0);
            var original = f.Progress.ReadExistingMenu(displaced.CharacterId, displaced.AccountId);
            var fenceCalls = 0;
            check("a menu generation displaced after unlock writes rolls back survival rows and hint without rewards",
                ThrowsSurvival<InvalidOperationException>(() => f.Progress.LoadMenu(displaced.CharacterId, Fixture.Anchor,
                    () => ++fenceCalls < 2)) && fenceCalls == 2
                && SameSurvivalSnapshot(original, f.Progress.ReadExistingMenu(displaced.CharacterId, displaced.AccountId))
                && f.Items(displaced.CharacterId, 10006059) == 0 && f.Mails(displaced.CharacterId) == 0);
            check("a fresh menu retry derives that same earned unlock without another ticket or star grant",
                f.Progress.LoadMenu(displaced.CharacterId, Fixture.Anchor).Stages
                    .Count(stage => survivalIds.Contains(stage.DungeonId)) == survivalIds.Length
                && f.HeaderTickets(displaced.CharacterId) == original.Header.FreeTickets + original.Header.SecondaryTickets);

            var incomplete = f.NewRole();
            var ordinary = OrderedOrdinary(catalog);
            SeedOrdinaryHistory(f, catalog, incomplete.CharacterId, 0, lastUnplayed: true);
            var waiting = f.Progress.LoadMenu(incomplete.CharacterId, Fixture.Anchor);
            check("an explicitly unplayed last ordinary row stays locked instead of being normalized by reopening",
                !waiting.Stages.Any(stage => survivalIds.Contains(stage.DungeonId))
                && waiting.Stages.Single(stage => stage.DungeonId == ordinary[^1].DungeonId).Progress == -1);
            f.Sql($"DELETE FROM character_guardian_altar_stages WHERE character_id={incomplete.CharacterId} AND dungeon_id={ordinary[^1].DungeonId};");
            check("a missing ordinary prerequisite cannot be silently recreated or replaced with survival",
                !f.Progress.LoadMenu(incomplete.CharacterId, Fixture.Anchor).Stages.Any(stage => survivalIds.Contains(stage.DungeonId))
                && f.Scalar($"SELECT COUNT(*) FROM character_guardian_altar_stages WHERE character_id={incomplete.CharacterId} AND dungeon_id={ordinary[^1].DungeonId};") == 0);
        }

        private static void VerifySurvivalClearUnlock(Fixture f, Action<string, bool> check)
        {
            var catalog = SurvivalCatalog(f);
            var ordinary = OrderedOrdinary(catalog);
            var survivalIds = catalog.Stages.Where(stage => stage.IsSurvival).Select(stage => stage.DungeonId).ToArray();
            var role = f.NewRole();
            SeedOrdinaryHistory(f, catalog, role.CharacterId, 0, lastUnplayed: true);
            var before = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
            var plan = f.Plan(0, ordinary[^1].DungeonId);
            var id = f.Effect(role.CharacterId);
            f.Sql("CREATE TRIGGER guardian_reject_earned_survival BEFORE INSERT ON character_guardian_altar_stages "
                + $"WHEN NEW.character_id={role.CharacterId} AND NEW.dungeon_id={survivalIds[0]} "
                + "BEGIN SELECT RAISE(ABORT,'guardian-survival-test'); END;");
            check("an earned survival insert fault rolls back the real final-stage clear, rewards, stars and ticket together",
                !f.Apply(role, id, plan, out _)
                && SameSurvivalSnapshot(before, f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId))
                && SurvivalRewardsMatch(f, role.CharacterId, plan, 0)
                && f.Effects.Outbox.Get(id).State == DungeonPersistentEffectState.Failed);
            var frozenPayload = f.Effects.Outbox.Get(id).PayloadJson;
            f.Sql("DROP TRIGGER guardian_reject_earned_survival;");
            check("the final easy first clear commits the earned survival row in the existing reward/outbox transaction",
                f.Apply(role, id, plan, out var result) && result.FirstClear && result.StarsGained == 3
                && result.ProgressSnapshot.Header.FreeTickets == before.Header.FreeTickets - 1
                && result.ProgressSnapshot.Header.StageStars == before.Header.StageStars + 3
                && result.ProgressSnapshot.Header.SurvivalBestPhase == before.Header.SurvivalBestPhase
                && survivalIds.All(dungeon => result.ProgressSnapshot.Stages.Single(stage => stage.DungeonId == dungeon).Progress == -1)
                && SurvivalRewardsMatch(f, role.CharacterId, plan, 1)
                && plan.ProgressPlan.NextDungeonIds.Count == 0
                && f.Effects.Outbox.Get(id).PayloadJson == frozenPayload && f.Effects.Outbox.Get(id).PayloadVersion == 1);
            var committed = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
            var committedJson = f.Effects.Outbox.Get(id).ResultJson;
            check("repeating the same final-clear effect or menu cannot debit another ticket, grant another reward or rewrite its result",
                f.Apply(role, id, plan, out _)
                && SameSurvivalSnapshot(committed, f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor))
                && SurvivalRewardsMatch(f, role.CharacterId, plan, 1)
                && f.Effects.Outbox.Get(id).ResultJson == committedJson);

            var late = f.NewRole();
            SeedOrdinaryHistory(f, catalog, late.CharacterId, 0, lastUnplayed: true);
            var lateBefore = f.Progress.ReadExistingMenu(late.CharacterId, late.AccountId);
            var lateId = f.Effect(late.CharacterId);
            var calls = 0;
            check("a participant fence failure after final-clear unlock writes rolls back every new survival fact and reward",
                !f.Effects.TryApplyGuardianAltarClear(lateId, late.CharacterId, late.AccountId, late, late.SessionId,
                    plan, () => ++calls < 5, out _, out _) && calls == 5
                && SameSurvivalSnapshot(lateBefore, f.Progress.ReadExistingMenu(late.CharacterId, late.AccountId))
                && SurvivalRewardsMatch(f, late.CharacterId, plan, 0)
                && f.Effects.Outbox.Get(lateId).State == DungeonPersistentEffectState.Failed);

            var alreadyCleared = f.NewRole();
            SeedOrdinaryHistory(f, catalog, alreadyCleared.CharacterId, 0);
            var repeatPlan = f.Plan(1, ordinary[^1].DungeonId);
            check("an existing final stage's next difficulty also repairs earned survival independently of firstClear",
                f.Apply(alreadyCleared, f.Effect(alreadyCleared.CharacterId), repeatPlan, out var improvement)
                && !improvement.FirstClear && improvement.StarsGained == 2
                && survivalIds.All(dungeon => improvement.ProgressSnapshot.Stages.Any(stage => stage.DungeonId == dungeon))
                && improvement.ProgressSnapshot.Header.FreeTickets == 2
                && SurvivalRewardsMatch(f, alreadyCleared.CharacterId, repeatPlan, 1));
        }

        private static void VerifySurvivalHistoricalEffects(Fixture f, Action<string, bool> check)
        {
            var catalog = SurvivalCatalog(f);
            var ordinary = OrderedOrdinary(catalog);
            var survivalIds = catalog.Stages.Where(stage => stage.IsSurvival).Select(stage => stage.DungeonId).ToArray();
            // Recompose the prior ordinary-only unlock policy with the exact
            // same registered IDs, shop and frozen ordinary clear plans.
            var legacyDefinition = new GuardianAltarMenuDefinition(f.Definition.DailyRefillTicket,
                f.Definition.RidableId, f.Definition.FirstDungeonId, f.Definition.InitialPurchases, f.Definition.InitialSlots,
                catalog.Stages.Select(stage => stage.DungeonId),
                knownPurchases: f.Definition.ShopEntries.SelectMany(shop => shop.Levels.Select(level =>
                    new GuardianAltarPurchase(shop.ShopType, shop.ResourceId, level.Level, level.Price))),
                shopEntries: f.Definition.ShopEntries, orderedStages: ordinary);
            var oldProgress = new GuardianAltarProgressService(f.Database, new DailyResetService(f.Database), legacyDefinition);
            var oldEffects = f.ComposeEffects(oldProgress);
            var role = f.NewRole();
            SeedOrdinaryHistory(f, catalog, role.CharacterId, 0, lastUnplayed: true);
            var plan = f.Plan(0, ordinary[^1].DungeonId);
            var id = f.Effect(role.CharacterId);
            check("the prior policy produces an actual committed final clear with no survival row",
                oldEffects.TryApplyGuardianAltarClear(id, role.CharacterId, role.AccountId, role, role.SessionId,
                    plan, () => true, out var prior, out _) && prior.FirstClear
                && !prior.ProgressSnapshot.Stages.Any(stage => survivalIds.Contains(stage.DungeonId))
                && SurvivalRewardsMatch(f, role.CharacterId, plan, 1));
            var previous = f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId);
            var originalPayload = oldEffects.Outbox.Get(id).PayloadJson;
            var originalResult = oldEffects.Outbox.Get(id).ResultJson;
            check("closed-window read capability preserves the actual old committed history without writing an unlock",
                SameSurvivalSnapshot(previous, f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId))
                && !previous.Stages.Any(stage => survivalIds.Contains(stage.DungeonId)));
            var repaired = f.Progress.LoadMenu(role.CharacterId, Fixture.Anchor);
            check("an open menu repairs actual old committed history without rerunning its reward or changing its frozen journal",
                repaired.Header == (previous.Header with { MenuHintFlag = 1 })
                && survivalIds.All(dungeon => repaired.Stages.Single(stage => stage.DungeonId == dungeon).Progress == -1)
                && SameSurvivalOwnedRows(previous, repaired) && SurvivalRewardsMatch(f, role.CharacterId, plan, 1)
                && f.Effects.Outbox.Get(id).PayloadJson == originalPayload && f.Effects.Outbox.Get(id).ResultJson == originalResult);
            check("old committed v1 ordinary intent still replays its historical result without losing the repaired durable menu",
                f.Apply(role, id, plan, out var replay) && !replay.ProgressSnapshot.Stages.Any(stage => survivalIds.Contains(stage.DungeonId))
                && SameSurvivalSnapshot(repaired, f.Progress.ReadExistingMenu(role.CharacterId, role.AccountId))
                && SurvivalRewardsMatch(f, role.CharacterId, plan, 1)
                && f.Effects.Outbox.Get(id).PayloadJson == originalPayload && f.Effects.Outbox.Get(id).ResultJson == originalResult);

            var interrupted = f.NewRole();
            SeedOrdinaryHistory(f, catalog, interrupted.CharacterId, 0, lastUnplayed: true);
            var interruptedId = f.Effect(interrupted.CharacterId);
            f.Sql("CREATE TRIGGER guardian_reject_old_final_outbox BEFORE UPDATE OF state ON dungeon_persistent_effect_outbox "
                + $"WHEN NEW.character_id={interrupted.CharacterId} AND NEW.state=2 "
                + "BEGIN SELECT RAISE(ABORT,'guardian-old-survival-outbox'); END;");
            check("the old policy's accepted v1 final-stage intent survives an injected commit failure",
                !oldEffects.TryApplyGuardianAltarClear(interruptedId, interrupted.CharacterId, interrupted.AccountId,
                    interrupted, interrupted.SessionId, plan, () => true, out _, out _)
                && oldEffects.Outbox.Get(interruptedId).State == DungeonPersistentEffectState.Failed
                && SurvivalRewardsMatch(f, interrupted.CharacterId, plan, 0));
            var oldPendingPayload = oldEffects.Outbox.Get(interruptedId).PayloadJson;
            f.Sql("DROP TRIGGER guardian_reject_old_final_outbox;");
            InventoryContext.Unregister(interrupted.SessionId, interrupted.CharacterId);
            var restarted = f.ComposeEffects();
            var recovered = restarted.RecoverCharacter(interrupted.CharacterId);
            var afterRecovery = f.Progress.ReadExistingMenu(interrupted.CharacterId, interrupted.AccountId);
            var tickets = f.HeaderTickets(interrupted.CharacterId);
            check("new owner recovers the unchanged old v1 intent once and derives survival in that same transaction",
                recovered.CommittedCount == 1 && recovered.FailedCount == 0 && !recovered.HasRemaining
                && restarted.Outbox.Get(interruptedId).State == DungeonPersistentEffectState.Committed
                && restarted.Outbox.Get(interruptedId).PayloadVersion == 1
                && restarted.Outbox.Get(interruptedId).PayloadJson == oldPendingPayload
                && survivalIds.All(dungeon => afterRecovery.Stages.Single(stage => stage.DungeonId == dungeon).Progress == -1)
                && afterRecovery.Header.SurvivalBestPhase == 0 && tickets == 2
                && SurvivalRewardsMatch(f, interrupted.CharacterId, plan, 1));
            var again = restarted.RecoverCharacter(interrupted.CharacterId);
            check("repeated old-intent recovery and menu reads do not create another survival row, ticket debit or reward",
                again.CommittedCount == 0 && !again.HasRemaining
                && SameSurvivalSnapshot(afterRecovery, f.Progress.LoadMenu(interrupted.CharacterId, Fixture.Anchor))
                && f.HeaderTickets(interrupted.CharacterId) == tickets
                && SurvivalRewardsMatch(f, interrupted.CharacterId, plan, 1));
        }

        private static GuardianAltarStageMapFile SurvivalCatalog(Fixture f)
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var entry = list.GetById(f.Definition.RidableId)
                ?? throw new InvalidDataException("Current Guardian stage map is not registered.");
            var result = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText("stagemap/" + entry.FilePath));
            if (!result.IsValid || !result.Stages.Any(stage => stage.IsSurvival))
                throw new InvalidDataException("The real Guardian survival catalog is unavailable: " + result.InvalidReason);
            return result;
        }

        private static GuardianAltarStageDefinition[] OrderedOrdinary(GuardianAltarStageMapFile catalog)
            => catalog.Stages.Where(stage => !stage.IsSurvival).OrderBy(stage => stage.AreaId)
                .ThenBy(stage => stage.DistrictId).ThenBy(stage => stage.StageOrder).ToArray();

        private static void SeedOrdinaryHistory(Fixture f, GuardianAltarStageMapFile catalog,
            int characterId, int difficulty, bool lastUnplayed = false)
        {
            var ordinary = OrderedOrdinary(catalog);
            foreach (var stage in ordinary)
                f.Sql("INSERT INTO character_guardian_altar_stages(character_id,ridable_id,dungeon_id,progress) "
                    + $"VALUES({characterId},{f.Definition.RidableId},{stage.DungeonId},{(lastUnplayed && stage == ordinary[^1] ? -1 : difficulty)}) "
                    + "ON CONFLICT(character_id,ridable_id,dungeon_id) DO UPDATE SET progress=excluded.progress;");
            var stars = (ordinary.Length - (lastUnplayed ? 1 : 0)) * new[] { 3, 5, 6 }[difficulty];
            f.Sql($"UPDATE character_guardian_altar SET stage_stars={stars},available_stars={stars},menu_hint_flag=0 WHERE character_id={characterId};");
        }

        private static bool SurvivalRewardsMatch(Fixture f, int characterId, GuardianAltarClearCommitPlan plan, int grants)
            => plan.Rewards.GroupBy(item => item.ItemId).All(group =>
                f.Items(characterId, group.Key) == group.Sum(item => item.Count) * grants);

        private static bool SameSurvivalOwnedRows(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.Purchases.SequenceEqual(right.Purchases) && left.Slots.SequenceEqual(right.Slots)
                && left.Achievements.SequenceEqual(right.Achievements);

        private static bool SameSurvivalSnapshot(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.CharacterId == right.CharacterId && left.Header == right.Header
                && left.Stages.SequenceEqual(right.Stages) && SameSurvivalOwnedRows(left, right);

        private static bool ThrowsSurvival<TException>(Action action) where TException : Exception
        {
            try { action(); return false; }
            catch (TException) { return true; }
        }
    }
}
