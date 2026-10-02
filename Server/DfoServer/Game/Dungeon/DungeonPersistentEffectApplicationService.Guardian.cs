using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal sealed partial class DungeonPersistentEffectApplicationService
    {
        private GuardianAltarProgressService _guardianAltarProgress;

        internal void BindGuardianAltarProgressService(GuardianAltarProgressService owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (owner.DatabaseConnectionString != _connectionString)
                throw new InvalidOperationException("Guardian progress and persistent effect owners use different databases.");
            lock (_dependencySync)
            {
                if (_guardianAltarProgress != null && !ReferenceEquals(_guardianAltarProgress, owner))
                    throw new InvalidOperationException("Guardian persistent progress owner is already bound.");
                _guardianAltarProgress = owner;
            }
        }

        private GuardianAltarProgressService GetGuardianAltarProgressService()
        {
            lock (_dependencySync)
                return _guardianAltarProgress
                    ?? throw new InvalidOperationException("Guardian persistent clear progress owner is not bound.");
        }

        internal bool TryApplyGuardianAltarClear(DungeonEffectId effectId,
            int characterId, int accountId, InventoryLease lease, Guid ownerSessionId,
            GuardianAltarClearCommitPlan plan, Func<bool> isCurrent,
            out GuardianAltarClearCommitResult result, out string error)
        {
            result = null;
            error = null;
            try
            {
                ValidateEffectIdentity(effectId, DungeonPersistentEffectKinds.GuardianAltarClearCommit, characterId);
                ArgumentNullException.ThrowIfNull(plan);
                RequireGuardianLiveLease(lease, ownerSessionId, characterId, accountId, isCurrent);
                GetGuardianAltarProgressService().PrepareClearDefinition(plan.ProgressPlan);
                var payload = BuildGuardianAltarClearPayload(characterId, accountId, plan);
                ValidateGuardianAltarClearPayload(payload, characterId, accountId);
                // The existing outbox fixes the accepted terminal fact and its
                // frozen plan before execution, and rejects changed retry input.
                _outbox.Enqueue(CreateDefinition(effectId, characterId, accountId, payload));
                RequireGuardianLiveLease(lease, ownerSessionId, characterId, accountId, isCurrent);
                return TryExecuteGuardianAltarClear(_outbox.Get(effectId), lease,
                    ownerSessionId, isCurrent, requireOwnedLease: true, out result, out error);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private bool TryExecuteGuardianAltarClear(DungeonPersistentEffectRecord initialRecord,
            InventoryLease lease, Guid ownerSessionId, Func<bool> isCurrent, bool requireOwnedLease,
            out GuardianAltarClearCommitResult result, out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(initialRecord.EffectId, LeaseDuration, out var reservation, out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadGuardianAltarClearResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            InventoryService inventory = null;
            DungeonItemGrantBatchPlan snapshotPlan = null;
            DungeonItemGrantMutationSnapshot rollback = null;
            var inventoryMutated = false;
            var committed = false;
            try
            {
                var payload = DeserializePayload<GuardianAltarClearEffectPayload>(record,
                    DungeonPersistentEffectKinds.GuardianAltarClearCommit);
                ValidateGuardianAltarClearPayload(payload, record.CharacterId, record.AccountId);
                var progressOwner = GetGuardianAltarProgressService();
                var progressPlan = RestoreGuardianAltarProgressPlan(payload.ProgressPlan);
                progressOwner.PrepareClearDefinition(progressPlan);
                var effectiveLease = lease;
                if (effectiveLease == null)
                {
                    // Recovery runs through the same owner before the role's
                    // online lease is attached, as with existing dungeon effects.
                    using (var connection = _database.OpenConnection())
                        inventory = InventoryService.LoadFromDb(connection, payload.CharacterId,
                            payload.AccountId, _database);
                    effectiveLease = new InventoryLease(Guid.NewGuid(), payload.CharacterId, inventory, version: 1);
                }
                else inventory = effectiveLease.Inventory;

                lock (effectiveLease.SyncRoot)
                {
                    if (effectiveLease.CharacterId != payload.CharacterId || effectiveLease.AccountId != payload.AccountId
                        || effectiveLease.Inventory.Database?.ConnectionString != _connectionString)
                        throw new InvalidOperationException("Guardian clear inventory does not belong to its durable participant.");
                    if (requireOwnedLease)
                        RequireGuardianLiveLease(effectiveLease, ownerSessionId, payload.CharacterId, payload.AccountId, isCurrent);

                    // Reuse the registered grant planner, batch application,
                    // rollback snapshot and transaction-bound overflow path.
                    var rewardPayload = new BloodAltarRewardEffectPayload
                    {
                        Items = payload.Rewards.Select(x => new BloodAltarRewardEffectItem
                            { ItemId = x.ItemId, StackCount = x.Count }).ToList(),
                    };
                    if (!TryBuildBloodAltarRewardPlan(inventory, rewardPayload,
                            out var inventoryPlan, out var overflowRewards, out _, out var planError)
                        || inventoryPlan.Entries.Any(x => x.Kind != InventoryRewardGrantKind.InventoryItem
                            || x.ListType != InventoryListType.Main))
                        throw new InvalidOperationException("Guardian clear reward planning failed: " + planError);
                    snapshotPlan = new DungeonItemGrantBatchPlan { Success = true, InventoryPlan = inventoryPlan };
                    if (!DungeonItemGrantMutationSnapshot.TryCapture(inventory, snapshotPlan, out rollback))
                        throw new InvalidOperationException("Guardian clear reward snapshot failed.");
                    inventoryMutated = inventoryPlan.Entries.Count > 0;
                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, inventoryPlan, out var grantBatch)
                        || !grantBatch.Success)
                        throw new InvalidOperationException("Guardian clear reward application failed: " + grantBatch?.Error);
                    inventoryMutated |= grantBatch.Changes.HasChanges;

                    using (var connection = _database.OpenConnection())
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var progress = progressOwner.ApplyClearInTransaction(connection, transaction,
                            payload.CharacterId, payload.AccountId, progressPlan, requireOwnedLease ? isCurrent : null);
                        if (overflowRewards.Count > 0)
                        {
                            var overflowSink = GetOverflowRewardSink();
                            if (overflowSink is not DfoServer.Game.Mailbox.MailboxInventoryOverflowRewardSink)
                                throw new InvalidOperationException("Guardian clear requires transaction-bound mailbox overflow delivery.");
                            var sink = new TransactionBoundInventoryOverflowRewardSink(connection, transaction,
                                overflowSink, "守护者祭坛通关奖励", "背包空间不足，通关奖励已通过邮件送达。");
                            if (!sink.TryDeliver(inventory, overflowRewards, out _))
                                throw new InvalidOperationException("Guardian clear overflow reward delivery failed.");
                        }
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, effectiveLease))
                            throw new InvalidOperationException("Guardian clear inventory persistence returned false.");
                        if (requireOwnedLease)
                            RequireGuardianLiveLease(effectiveLease, ownerSessionId, payload.CharacterId, payload.AccountId, isCurrent);
                        var persisted = BuildGuardianAltarClearResult(progress, grantBatch.Changes, overflowRewards.Count);
                        if (!_outbox.TryCommitInTransaction(connection, transaction, reservation, ResultVersion,
                                Serialize(persisted), _outbox.UtcNowMilliseconds))
                            throw new InvalidOperationException("Guardian clear effect lease was lost before commit.");
                        if (requireOwnedLease)
                            RequireGuardianLiveLease(effectiveLease, ownerSessionId, payload.CharacterId, payload.AccountId, isCurrent);
                        transaction.Commit();
                        committed = true;
                        inventory.ClearDirtyState();
                        result = ToGuardianAltarClearCommitResult(persisted, record);
                    }
                }
                return true;
            }
            catch (PermanentPersistentEffectException exception)
            {
                if (!committed)
                    RecoverLicensedDungeonInventoryAfterFailure(lease, inventory, snapshotPlan, rollback, inventoryMutated);
                _outbox.TryDeadLetter(reservation, exception.Message);
                error = exception.Message;
                result = null;
                return false;
            }
            catch (Exception exception)
            {
                if (TryReadCommittedGuardianAltarClearAfterError(initialRecord.EffectId, out result))
                {
                    inventory?.ClearDirtyState();
                    return true;
                }
                if (!committed)
                    RecoverLicensedDungeonInventoryAfterFailure(lease, inventory, snapshotPlan, rollback, inventoryMutated);
                _outbox.TryFail(reservation, exception.Message);
                error = exception.Message;
                result = null;
                return false;
            }
        }

        private static void RequireGuardianLiveLease(InventoryLease lease, Guid ownerSessionId,
            int characterId, int accountId, Func<bool> isCurrent)
        {
            if (characterId <= 0 || accountId <= 0 || lease == null || ownerSessionId == Guid.Empty
                || lease.CharacterId != characterId || lease.AccountId != accountId
                || !InventoryContext.IsCurrentLease(lease, ownerSessionId, characterId)
                || isCurrent == null || !isCurrent())
                throw new InvalidOperationException("Guardian clear belongs to an obsolete participant or inventory generation.");
        }

        private static GuardianAltarClearEffectPayload BuildGuardianAltarClearPayload(
            int characterId, int accountId, GuardianAltarClearCommitPlan plan)
        {
            var progress = plan.ProgressPlan;
            return new GuardianAltarClearEffectPayload
            {
                CharacterId = characterId,
                AccountId = accountId,
                ProgressPlan = new GuardianAltarClearEffectProgressPlan
                {
                    RidableId = progress.RidableId, DungeonId = progress.DungeonId,
                    Difficulty = progress.Difficulty, StarsOnImprovement = progress.StarsOnImprovement,
                    NextDungeonIds = progress.NextDungeonIds.ToList(), UnlockPurchases = progress.UnlockPurchases.ToList(),
                },
                Rewards = plan.Rewards.Select(x => new GuardianAltarClearEffectItem
                    { ItemId = x.ItemId, Count = x.Count }).ToList(),
            };
        }

        private static GuardianAltarClearProgressPlan RestoreGuardianAltarProgressPlan(
            GuardianAltarClearEffectProgressPlan plan) => new(plan.RidableId, plan.DungeonId,
                plan.Difficulty, plan.StarsOnImprovement, plan.NextDungeonIds, plan.UnlockPurchases);

        private static void ValidateGuardianAltarClearPayload(GuardianAltarClearEffectPayload payload,
            int characterId, int accountId)
        {
            if (payload == null || payload.CharacterId != characterId || payload.AccountId != accountId
                || characterId <= 0 || accountId <= 0 || payload.ProgressPlan == null
                || payload.Rewards == null || payload.Rewards.Count == 0)
                throw new PermanentPersistentEffectException("Guardian clear effect participant or frozen plan is invalid.");
            try { RestoreGuardianAltarProgressPlan(payload.ProgressPlan); }
            catch (Exception exception)
            {
                throw new PermanentPersistentEffectException("Guardian clear progress payload is invalid.", exception);
            }
            foreach (var reward in payload.Rewards)
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0
                    || StackableItemProvider.Load(reward.ItemId) == null)
                    throw new PermanentPersistentEffectException("Guardian ordinary clear reward is not a registered stackable item.");
        }

        private static GuardianAltarClearEffectResult BuildGuardianAltarClearResult(
            GuardianAltarClearProgressCommitResult progress, InventoryMutationSet changes, int mailedRewardCount)
        {
            var snapshot = progress.ProgressSnapshot;
            return new GuardianAltarClearEffectResult
            {
                CharacterId = snapshot.CharacterId, StarsGained = progress.StarsGained, FirstClear = progress.FirstClear,
                Progress = new GuardianAltarClearEffectSnapshot
                {
                    Header = snapshot.Header, Stages = snapshot.Stages.ToList(), Purchases = snapshot.Purchases.ToList(),
                    Slots = snapshot.Slots.ToList(), Achievements = snapshot.Achievements.ToList(),
                },
                NewPurchases = progress.NewPurchases.ToList(), MailedRewardCount = mailedRewardCount,
                Changes = changes.Slots.Select(x => new GuardianAltarClearEffectMutation
                    { ListType = (int)x.ListType, Slot = x.SlotIndex }).ToList(),
            };
        }

        private static GuardianAltarClearCommitResult ToGuardianAltarClearCommitResult(
            GuardianAltarClearEffectResult persisted, DungeonPersistentEffectRecord record)
        {
            if (persisted == null || persisted.CharacterId != record.CharacterId || persisted.StarsGained < 0
                || persisted.StarsGained > 3 || persisted.MailedRewardCount < 0 || persisted.Progress?.Header == null
                || persisted.Progress.Stages == null || persisted.Progress.Purchases == null
                || persisted.Progress.Slots == null || persisted.Progress.Achievements == null
                || persisted.NewPurchases == null || persisted.Changes == null)
                throw new InvalidOperationException("Guardian committed effect result is inconsistent.");
            var snapshot = new GuardianAltarProgressSnapshot(persisted.CharacterId, persisted.Progress.Header,
                persisted.Progress.Stages, persisted.Progress.Purchases, persisted.Progress.Slots, persisted.Progress.Achievements);
            GuardianAltarProgressValidator.Validate(snapshot);
            if (persisted.NewPurchases.Any(x => x == null || !snapshot.Purchases.Contains(x))
                || persisted.NewPurchases.Select(x => (x.ShopType, x.ResourceId)).Distinct().Count() != persisted.NewPurchases.Count
                || persisted.Changes.Any(x => x == null || x.ListType != (int)InventoryListType.Main
                    || x.Slot < InventoryService.MainSlotStart || x.Slot > InventoryService.MainSlotEnd))
                throw new InvalidOperationException("Guardian committed effect changes are inconsistent.");
            return new GuardianAltarClearCommitResult
            {
                StarsGained = persisted.StarsGained, FirstClear = persisted.FirstClear, ProgressSnapshot = snapshot,
                NewPurchases = persisted.NewPurchases.AsReadOnly(), MailedRewardCount = persisted.MailedRewardCount,
                Changes = persisted.Changes.Select(x => new InventorySlotMutation((InventoryListType)x.ListType, x.Slot)).ToArray(),
            };
        }

        private static bool TryReadGuardianAltarClearResult(DungeonPersistentEffectRecord record,
            out GuardianAltarClearCommitResult result, out string error)
        {
            result = null;
            if (!TryDeserializeResult(record, out GuardianAltarClearEffectResult persisted, out error))
                return false;
            try
            {
                result = ToGuardianAltarClearCommitResult(persisted, record);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private bool TryReadCommittedGuardianAltarClearAfterError(DungeonEffectId id,
            out GuardianAltarClearCommitResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(id);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadGuardianAltarClearResult(record, out result, out _);
            }
            catch { return false; }
        }
    }
}
