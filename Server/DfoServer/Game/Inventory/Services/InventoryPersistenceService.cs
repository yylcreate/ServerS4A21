using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Currency;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryPersistenceService
    {
        private const string ClockTickName = "inventory:save-dirty";
        private static readonly object RegisterSync = new object();
        private static bool _clockRegistered;
        private static int _savingAll;

        public static void RegisterClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            lock (RegisterSync)
            {
                if (_clockRegistered)
                    return;

                _clockRegistered = true;
                clock.RegisterMinuteTick(ClockTickName, _ => SaveAllDirty());
            }
        }

        public static void SaveAllDirty()
        {
            if (Interlocked.Exchange(ref _savingAll, 1) != 0)
                return;

            try
            {
                foreach (var lease in InventoryContext.GetLeasesSnapshot())
                    SaveDirty(lease);
            }
            finally
            {
                Interlocked.Exchange(ref _savingAll, 0);
            }
        }

        public static bool SaveDirty(InventoryLease lease)
        {
            if (lease == null || lease.Inventory == null)
                return false;

            try
            {
                lock (lease.SyncRoot)
                {
                    var inventory = lease.Inventory;
                    if (!HasDirtyData(inventory))
                        return true;

                    var database = inventory.Database
                        ?? GameDatabase.CreateDefault();
                    using (var connection = database.OpenConnection())
                    {
                        using (var transaction = connection.BeginTransaction())
                        {
                            SaveDirtyInTransaction(connection, transaction, lease);
                            transaction.Commit();
                        }
                    }

                    inventory.ClearDirtyState();
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] SaveDirty failed cid={lease.CharacterId} aid={lease.AccountId}: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveDirtyAndLoadWallet(InventoryLease lease, out WalletSnapshot wallet)
        {
            wallet = null;
            if (lease == null || lease.Inventory == null)
                return false;

            try
            {
                lock (lease.SyncRoot)
                {
                    var inventory = lease.Inventory;
                    var hasDirtyData = HasDirtyData(inventory);
                    var database = inventory.Database
                        ?? GameDatabase.CreateDefault();
                    using (var connection = database.OpenConnection())
                    {
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (hasDirtyData
                                && !SaveDirtyInTransaction(connection, transaction, lease))
                                return false;

                            wallet = CurrencyService.LoadWallet(
                                connection,
                                transaction,
                                inventory.CharacterId);
                            transaction.Commit();
                        }
                    }

                    if (hasDirtyData)
                        inventory.ClearDirtyState();
                }

                return wallet != null;
            }
            catch (Exception ex)
            {
                wallet = null;
                FileLogger.Log($"[InventoryPersistence] SaveDirtyAndLoadWallet failed cid={lease.CharacterId} aid={lease.AccountId}: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveDirtyInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (lease == null || lease.Inventory == null)
                return false;

            var inventory = lease.Inventory;
            if (!HasDirtyData(inventory))
                return true;

            SaveDirtyItemAudits(connection, transaction, lease, inventory);
            SaveDirtyMainVirtualCountAudits(connection, transaction, lease, inventory);
            SaveDirtyItems(connection, transaction, inventory);
            SaveDirtyAvatarDetails(connection, transaction, inventory);
            SaveDirtyCreatureDetails(connection, transaction, inventory);
            SaveDirtyTitleBook(connection, transaction, inventory);
            SaveDirtyAchievements(connection, transaction, inventory);
            SaveDirtyCollectBox(connection, transaction, inventory);
            SaveDirtyEpicPieceBook(connection, transaction, inventory);
            SaveDirtyItemStates(connection, transaction, inventory);
            SaveDirtyMainVirtualCounts(connection, transaction, lease, inventory);
            SaveDirtyContainerStates(connection, transaction, inventory);
            SavePendingAccountCurrencyGrants(connection, transaction, inventory);
            return true;
        }

        public static bool SaveAvatarDetailImmediately(
            AvatarDetail detail,
            IGameDatabase database = null)
        {
            if (detail == null || detail.AvatarUid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        AvatarDetailRepository.Upsert(connection, transaction, detail);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] avatar detail save failed avatarUid={detail.AvatarUid}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteAvatarDetailImmediately(
            long avatarUid,
            IGameDatabase database = null)
        {
            if (avatarUid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        AvatarDetailRepository.Delete(connection, transaction, avatarUid);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] avatar detail delete failed avatarUid={avatarUid}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteCreatureDetailImmediately(
            int characterId,
            int creatureKey,
            IGameDatabase database = null)
        {
            if (characterId <= 0 || creatureKey <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        CreatureDetailRepository.Delete(connection, transaction, characterId, creatureKey);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] creature detail delete failed cid={characterId} uid={creatureKey}: {ex.Message}");
                return false;
            }
        }

        public static bool SaveCreatureDetailImmediately(
            int characterId,
            CreatureDetail detail,
            IGameDatabase database = null)
        {
            if (characterId <= 0 || detail == null || detail.Uid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var updated = CreatureDetailRepository.Upsert(connection, transaction, characterId, detail);
                        transaction.Commit();
                        return updated;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] creature detail save failed cid={characterId} uid={detail.Uid}: {ex.Message}");
                return false;
            }
        }

        private static bool HasDirtyData(InventoryService inventory)
        {
            if (inventory.PendingHappyTokenCeraGrant > 0)
                return true;
            if (inventory.DirtyMainVirtualCountSlots.Count > 0 || inventory.DirtyListParams.Count > 0)
                return true;
            if (inventory.AvatarDetails.DirtyDetailUids.Count > 0
                || inventory.AvatarDetails.DeletedDetailUids.Count > 0
                || inventory.CreatureDetails.DirtyDetailUids.Count > 0
                || inventory.CreatureDetails.DeletedDetailUids.Count > 0)
                return true;
            if (inventory.Cargo.DirtySlots.Count > 0
                || inventory.Cargo.IsStateDirty
                || inventory.AccountCargo.DirtySlots.Count > 0
                || inventory.AccountCargo.IsStateDirty)
                return true;
            if (inventory.TitleBook.HasDirtySlots
                || inventory.Achievements.DirtyQuestIds.Count > 0)
                return true;
            if (inventory.CollectBox.HasDirtySlots)
                return true;
            if (inventory.EpicPieces.IsDirty)
                return true;
            if (inventory.ItemStates.IsDirty)
                return true;

            foreach (var _ in inventory.DirtyListTypes)
                return true;

            return false;
        }

        private static void SavePendingAccountCurrencyGrants(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (inventory.PendingHappyTokenCeraGrant <= 0)
                return;

            CurrencyService.GrantHappyTokenCera(
                connection,
                transaction,
                inventory.CharacterId,
                inventory.PendingHappyTokenCeraGrant);
        }

        private static void SaveDirtyItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var pair in CollectDirtyItemSlots(inventory))
                foreach (var slotIndex in pair.Value)
                    SaveSlot(connection, transaction, inventory, pair.Key, slotIndex);
        }

        private static void SaveDirtyItemAudits(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease,
            InventoryService inventory)
        {
            foreach (var pair in CollectDirtyItemSlots(inventory))
            {
                var listType = pair.Key;
                foreach (var slotIndex in pair.Value)
                {
                    if (listType == InventoryListType.Main
                        && (InventoryService.IsVirtualMainSlot(slotIndex)
                            || InventoryService.IsReservedMainSlot(slotIndex)))
                        continue;

                    var before = InventoryAuditRepository.LoadPersistedSlotCore(
                        connection,
                        transaction,
                        inventory,
                        listType,
                        slotIndex);
                    var after = inventory.GetItem(listType, slotIndex);
                    var auditEvent = InventoryAuditEvent.FromSlotChange(
                        lease.SessionId,
                        inventory,
                        listType,
                        slotIndex,
                        before,
                        after);
                    InventoryAuditRepository.Insert(connection, transaction, auditEvent);
                }
            }
        }

        private static void SaveDirtyMainVirtualCountAudits(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease,
            InventoryService inventory)
        {
            foreach (var slotIndex in inventory.DirtyMainVirtualCountSlots)
            {
                // Crystal audit values come from the atomic account delta below,
                // not from the stale character projection's absolute count.
                if (CurrencyService.IsCubeFragmentSlot(slotIndex)) continue;
                var item = inventory.GetMainVirtualCount(slotIndex);
                if (item == null)
                    continue;

                var beforeCount = InventoryAuditRepository.LoadPersistedVirtualCount(
                    connection,
                    transaction,
                    inventory,
                    slotIndex);
                var auditEvent = InventoryAuditEvent.FromVirtualCountChange(
                    lease.SessionId,
                    inventory,
                    slotIndex,
                    item.ItemId,
                    beforeCount,
                    item.Count);
                InventoryAuditRepository.Insert(connection, transaction, auditEvent);
            }
        }

        private static Dictionary<InventoryListType, HashSet<short>> CollectDirtyItemSlots(InventoryService inventory)
        {
            var listTypes = new HashSet<InventoryListType>(inventory.DirtyListTypes);
            if (inventory.Cargo.DirtySlots.Count > 0)
                listTypes.Add(InventoryListType.PersonalCargo);
            if (inventory.AccountCargo.DirtySlots.Count > 0)
                listTypes.Add(InventoryListType.AccountCargo);

            var result = new Dictionary<InventoryListType, HashSet<short>>();
            foreach (var listType in listTypes)
            {
                var slots = new HashSet<short>(inventory.GetDirtySlots(listType));
                if (listType == InventoryListType.PersonalCargo)
                    slots.UnionWith(inventory.Cargo.DirtySlots);
                else if (listType == InventoryListType.AccountCargo)
                    slots.UnionWith(inventory.AccountCargo.DirtySlots);

                result[listType] = slots;
            }

            return result;
        }

        private static void SaveSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex)
        {
            if (listType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(slotIndex)
                    || InventoryService.IsReservedMainSlot(slotIndex)))
                return;

            var core = inventory.GetItem(listType, slotIndex);
            if (listType == InventoryListType.AccountCargo)
            {
                if (core == null)
                    InventoryItemRepository.DeleteAccountCargoSlot(connection, transaction, inventory.AccountId, slotIndex);
                else
                    InventoryItemRepository.UpsertAccountCargoSlot(connection, transaction, inventory.AccountId, inventory.CharacterId, slotIndex, core);
                return;
            }

            if (core == null)
            {
                InventoryItemRepository.DeleteCharacterSlot(connection, transaction, inventory.CharacterId, listType, slotIndex);
                return;
            }

            InventoryItemRepository.UpsertCharacterSlot(
                connection,
                transaction,
                inventory.CharacterId,
                listType,
                slotIndex,
                core);
        }

        private static void SaveDirtyAvatarDetails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var avatarUid in inventory.AvatarDetails.DeletedDetailUids)
                AvatarDetailRepository.Delete(connection, transaction, avatarUid);

            foreach (var detail in inventory.AvatarDetails.GetDirtyDetails())
                AvatarDetailRepository.Upsert(connection, transaction, detail);
        }

        private static void SaveDirtyCreatureDetails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var creatureKey in inventory.CreatureDetails.DeletedDetailUids)
                CreatureDetailRepository.Delete(connection, transaction, inventory.CharacterId, creatureKey);

            foreach (var detail in inventory.CreatureDetails.GetDirtyDetails())
                CreatureDetailRepository.Upsert(connection, transaction, inventory.CharacterId, detail);
        }

        private static void SaveDirtyTitleBook(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var item in inventory.TitleBook.GetDirtyItems())
            {
                CharacterTitleBookRepository.SaveSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    item.Key.Category,
                    item.Key.SlotIndex,
                    item.Value);
            }
        }

        private static void SaveDirtyAchievements(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var entry in inventory.Achievements.GetDirtyEntries())
                CharacterAchievementRepository.UpsertEntry(connection, transaction, inventory.CharacterId, entry);
        }

        private static void SaveDirtyCollectBox(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var slot in inventory.CollectBox.GetDirtySlots())
            {
                CollectBoxProgressRepository.SaveSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    slot.BoxIndex,
                    slot.SlotIndex,
                    slot.ItemId);
            }
        }

        private static void SaveDirtyEpicPieceBook(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (!inventory.EpicPieces.IsDirty)
                return;

            EpicPieceBookRepository.SaveBlob(
                connection,
                transaction,
                inventory.AccountId,
                inventory.EpicPieces.ToBlob());
        }

        private static void SaveDirtyItemStates(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (!inventory.ItemStates.IsDirty)
                return;

            CharacterItemStateRepository.SaveAll(
                connection,
                transaction,
                inventory.CharacterId,
                inventory.ItemStates);
        }

        private static void SaveDirtyMainVirtualCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease,
            InventoryService inventory)
        {
            foreach (var slotIndex in inventory.DirtyMainVirtualCountSlots.ToArray())
            {
                var item = inventory.GetMainVirtualCount(slotIndex);
                if (item == null)
                    continue;

                if (slotIndex >= InventoryService.MainVirtualCurrencySlotStart
                    && slotIndex <= InventoryService.MainVirtualCurrencySlotEnd)
                {
                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        slotIndex,
                        item.Count);
                    continue;
                }

                if (slotIndex >= InventoryService.MainVirtualCubeSlotStart
                    && slotIndex <= InventoryService.MainVirtualCubeSlotEnd)
                {
                    SaveCrystalCount(connection, transaction, lease, inventory, slotIndex, item.ItemId);
                    continue;
                }

                if (slotIndex >= InventoryService.MainVirtualSoulSlotStart
                    && slotIndex <= InventoryService.MainVirtualSoulSlotEnd)
                {
                    CurrencyService.SetSoulWarehouseCount(
                        connection,
                        transaction,
                        inventory.AccountId,
                        item.ItemId,
                        item.Count);
                }
            }
        }

        private static void SaveCrystalCount(SqliteConnection connection, SqliteTransaction transaction,
            InventoryLease lease, InventoryService inventory, short slotIndex, int itemId)
        {
            var checkpoint = inventory.GetCrystalPersistence(slotIndex);
            if (checkpoint != null && !checkpoint.IsFor(transaction))
            {
                if (HasCrystalReceipt(connection, transaction, checkpoint))
                    inventory.AcceptCrystalPersistence(slotIndex);
                else
                    inventory.DiscardCrystalPersistence(slotIndex);
                checkpoint = null;
            }
            var delta = inventory.GetCrystalPersistenceDelta(slotIndex, transaction);
            if (delta == 0 && checkpoint != null) return;
            if (!CurrencyService.TryApplyCubeFragmentDelta(connection, transaction,
                    inventory.AccountId, itemId, delta, out var beforeCount, out var afterCount))
                throw new InvalidOperationException("Account crystal balance cannot accept the pending change.");
            var receipt = checkpoint?.Receipt ?? Guid.NewGuid().ToString("N");
            var auditEvent = InventoryAuditEvent.FromVirtualCountChange(
                lease.SessionId, inventory, slotIndex, itemId, beforeCount, afterCount)
                ?? new InventoryAuditEvent
                {
                    SessionId = lease.SessionId, OwnerScope = "account", OwnerId = inventory.AccountId,
                    CharacterId = inventory.CharacterId, AccountId = inventory.AccountId,
                    ActionName = "inventory_crystal_count_observe", ListType = InventoryListType.Main,
                    SlotIndex = slotIndex, ItemId = itemId, ItemKind = ItemCore.KindSpecialMaterial,
                    ValueBefore = beforeCount, ValueAfter = afterCount,
                    CountBefore = beforeCount, CountAfter = afterCount,
                };
            if (checkpoint == null)
                auditEvent.PayloadJson = CrystalReceiptPayload(receipt);
            InventoryAuditRepository.Insert(connection, transaction, auditEvent);
            var auditId = checkpoint?.AuditId ?? LastAuditId(connection, transaction);
            inventory.RecordCrystalPersistence(slotIndex, transaction, connection.ConnectionString,
                receipt, auditId, delta, afterCount);
        }

        private static long LastAuditId(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static string CrystalReceiptPayload(string receipt)
            => "{\"crystalCommit\":\"" + receipt + "\"}";

        private static bool HasCrystalReceipt(SqliteConnection connection, SqliteTransaction transaction,
            CrystalCountPersistenceCheckpoint checkpoint)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT payload_json FROM inventory_audit_log WHERE audit_id=@id;";
            command.Parameters.AddWithValue("@id", checkpoint.AuditId);
            return command.ExecuteScalar() is string payload
                && string.Equals(payload, CrystalReceiptPayload(checkpoint.Receipt), StringComparison.Ordinal);
        }

        internal static IReadOnlySet<short> CompleteCrystalPersistence(InventoryService inventory)
        {
            var outstanding = new HashSet<short>();
            for (short slot = InventoryService.MainVirtualCubeSlotStart;
                 slot <= InventoryService.MainVirtualCubeSlotEnd; slot++)
            {
                var checkpoint = inventory.GetCrystalPersistence(slot);
                if (checkpoint == null) continue;
                // A separate reader observes durable rows even if a legacy rollback
                // caller invokes ClearDirtyState with its original transaction alive.
                // The indexed row + nonce distinguishes rollback and row-id reuse.
                try
                {
                    using var connection = new SqliteConnection(checkpoint.ConnectionString);
                    connection.Open();
                    if (HasCrystalReceipt(connection, null, checkpoint))
                    {
                        if (inventory.AcceptCrystalPersistence(slot)) outstanding.Add(slot);
                    }
                    else
                    {
                        // The receipt can also be invisible because the caller's
                        // transaction is still active. Preserve its checkpoint and
                        // baseline so a second save in that transaction cannot debit
                        // twice. A subsequent transaction reconciles rollback first.
                        outstanding.Add(slot);
                    }
                }
                catch (SqliteException exception)
                {
                    // This postcommit observation is not part of the business
                    // commit. Keep the exact checkpoint for a later read rather
                    // than reporting an already committed mutation as failed.
                    FileLogger.Log($"[InventoryPersistence] crystal receipt observation deferred "
                        + $"cid={inventory.CharacterId} slot={slot} sqlite={exception.SqliteErrorCode}: {exception.Message}");
                    outstanding.Add(slot);
                }
            }
            return outstanding;
        }

        private static void SaveDirtyContainerStates(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var listType in inventory.DirtyListParams)
            {
                if (listType == InventoryListType.AccountCargo
                    || listType == InventoryListType.PersonalCargo)
                    continue;

                InventoryContainerStateRepository.UpsertCharacterContainerState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    listType,
                    inventory.GetListParam16(listType));
            }

            if (inventory.Cargo.IsStateDirty
                || inventory.DirtyListParams.Contains(InventoryListType.PersonalCargo))
            {
                InventoryContainerStateRepository.UpsertCharacterContainerState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    InventoryListType.PersonalCargo,
                    inventory.Cargo.Capacity);
            }

            if (inventory.DirtyListParams.Contains(InventoryListType.AccountCargo)
                || inventory.AccountCargo.IsStateDirty
                || inventory.AccountCargo.DirtySlots.Count > 0)
            {
                InventoryContainerStateRepository.UpsertAccountCargoState(
                    connection,
                    transaction,
                    inventory.AccountId,
                    inventory.AccountCargo.SelectionKey,
                    inventory.AccountCargo.Money,
                    (ushort)inventory.AccountCargo.GetItems().Count);
            }
        }
    }
}
