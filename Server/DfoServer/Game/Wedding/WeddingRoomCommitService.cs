using DfoServer.Game.Inventory;
using DfoServer.Game.Currency;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed class WeddingRoomCommitService
    {
        private readonly WeddingRoomCatalog _catalog;
        private readonly Func<long> _now;
        private readonly Func<int, int> _roll;
        internal WeddingRoomCommitService(WeddingRoomCatalog catalog, Func<long> now = null, Func<int, int> roll = null)
        { _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()); _roll = roll ?? ServerRandom.Next; }

        // Caller holds both canonical character transition gates. Assets are only
        // modified through current leases. An offline spouse's harvest uses mail.
        internal WeddingRoomResult Commit(InventoryLease actor, InventoryLease partner, int partnerId,
            WeddingRoomCommand command, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (actor == null || command == null || partnerId <= 0 || actor.CharacterId == partnerId
                || partner != null && partner.CharacterId != partnerId) return new(-2);
            var first = partner != null && partner.CharacterId < actor.CharacterId ? partner : actor;
            var second = partner == null ? first : ReferenceEquals(first, actor) ? partner : actor;
            lock (first.SyncRoot)
            lock (second.SyncRoot)
            {
                if (!Current(actor) || partner != null && !Current(partner) || !stillCurrent()) return new(-2);
                if (!InventoryPersistenceService.SaveDirty(actor) || partner != null && !InventoryPersistenceService.SaveDirty(partner)) return new(-3);
                InventoryService baselineA = null, baselineB = null;
                bool mutated = false;
                try
                {
                    var database = actor.Inventory.Database;
                    if (partner != null && database.ConnectionString != partner.Inventory.Database.ConnectionString) return new(-2);
                    using var connection = database.OpenConnection();
                    baselineA = InventoryService.LoadFromDb(connection, actor.CharacterId, actor.AccountId, database);
                    if (partner != null) baselineB = InventoryService.LoadFromDb(connection, partner.CharacterId, partner.AccountId, database);
                    using var transaction = connection.BeginTransaction();
                    using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                    var a = WeddingRepository.LoadActor(connection, transaction, actor.CharacterId);
                    var b = WeddingRepository.LoadActor(connection, transaction, partnerId);
                    Need(a != null && b != null && !a.Deleted && !b.Deleted && a.AccountId == actor.AccountId
                        && (partner == null || b.AccountId == partner.AccountId), -2);
                    var wedding = WeddingRepository.Load(connection, transaction, actor.CharacterId);
                    Need(wedding.Status == WeddingStatus.Married && wedding.PartnerId == partnerId
                        && WeddingRules.IsReciprocal(wedding, WeddingRepository.Load(connection, transaction, partnerId)), -2);
                    long now = _now();
                    var room = WeddingRoomRepository.Load(connection, transaction, wedding.PairId, now);
                    bool assets = false;
                    WeddingItemCost reward = null;
                    InventoryMutationResult buyMutation = null;
                    WeddingShopWallet shopWallet = null;
                    var mailed = new List<int>();
                    int boughtSlot = -1;
                    mutated = true;
                    switch (command.Operation)
                    {
                        case WeddingRoomOperation.Query: break;
                        case WeddingRoomOperation.Move: Move(room, command); break;
                        case WeddingRoomOperation.Delete:
                            Need(command.Slot is >= 0 and < 40 && command.Count > 0, -2);
                            var deleted = room.Cargo[command.Slot];
                            Need(deleted != null && deleted.ItemId == command.ItemId && deleted.Count >= command.Count, -2);
                            room.Cargo[command.Slot] = deleted.Count == command.Count ? null : deleted with { Count = deleted.Count - command.Count };
                            break;
                        case WeddingRoomOperation.Plant:
                            Need(room.TreeId == 0 && _catalog.Seeds.TryGetValue(command.ItemId, out _), -4);
                            var tree = _catalog.Trees[_catalog.Seeds[command.ItemId]];
                            ConsumeSlot(actor.Inventory, command.Slot, command.ItemId, 1, now);
                            room.TreeId = tree.Index; room.TreeExpires = checked(now + (long)tree.ExpireDays * 86400);
                            Need(room.TreeExpires <= uint.MaxValue, -4);
                            room.TreeGrowth = 0; assets = true;
                            break;
                        case WeddingRoomOperation.Water:
                            Need(_catalog.Trees.TryGetValue(room.TreeId, out var growing), -4);
                            Need(growing.WaterItems.TryGetValue(command.ItemId, out var points) && room.TreeGrowth < growing.MaximumGrowth, -9);
                            Need(room.WaterCount < growing.DailyWater, -6);
                            ConsumeSlot(actor.Inventory, command.Slot, command.ItemId, 1, now);
                            room.TreeGrowth = (int)Math.Min(growing.MaximumGrowth, (long)room.TreeGrowth + points);
                            room.WaterCount++; assets = true;
                            break;
                        case WeddingRoomOperation.Harvest:
                            Need(_catalog.Trees.TryGetValue(room.TreeId, out var harvest), -4);
                            Need(room.TreeGrowth >= harvest.MaximumGrowth, -6);
                            int roll = _roll(harvest.Rewards.Sum(x => x.Weight));
                            Need(roll >= 0 && roll < harvest.Rewards.Sum(x => x.Weight), -3);
                            foreach (var candidate in harvest.Rewards)
                            { roll -= candidate.Weight; if (roll < 0) { reward = new(candidate.ItemId, candidate.Count); break; } }
                            Need(reward != null, -3);
                            if (!DeliverTree(database, connection, transaction, actor, actor.CharacterId, room, reward)) mailed.Add(actor.CharacterId);
                            if (!DeliverTree(database, connection, transaction, partner, partnerId, room, reward)) mailed.Add(partnerId);
                            room.HarvestSequence = checked(room.HarvestSequence + 1); room.TreeGrowth = 0; assets = true;
                            break;
                        case WeddingRoomOperation.Remove:
                            Need(room.TreeId != 0, -4); room.TreeId = 0; room.TreeExpires = 0; room.TreeGrowth = 0;
                            break;
                        case WeddingRoomOperation.Buy:
                            // Re-resolve at the transaction boundary; the caller cannot quote itself a free price.
                            Need(WeddingShopPricing.TryResolve(command.ItemId, command.Count, out var quote, out bool furniture), -2);
                            Need(command.Gold == quote.Gold && command.Love == quote.Love
                                && (command.Materials ?? Array.Empty<WeddingItemCost>()).SequenceEqual(quote.Materials), -2);
                            int gold = actor.Inventory.GetMainVirtualCount(0)?.Count ?? 0;
                            Need(gold >= quote.Gold && actor.Inventory.SetMainVirtualCount(0, gold - quote.Gold), -7);
                            Need(wedding.Love >= quote.Love, -9);
                            foreach (var cost in quote.Materials) Consume(actor.Inventory, cost, now);
                            if (quote.Love > 0) Need(WeddingRepository.Save(connection, transaction, wedding, wedding with { Love = wedding.Love - quote.Love }), -3);
                            if (furniture) boughtSlot = AddCargo(room, command.ItemId, command.Count);
                            else
                            {
                                Need(InventoryRewardGrantService.TryCreateAndInsert(actor.Inventory, command.ItemId, ItemCreateReason.NpcShopPurchase, command.Count, out var granted), -20);
                                boughtSlot = granted?.SlotIndex ?? -1;
                                buyMutation = InventoryMutationResultFactory.FromGrant(actor.Inventory, granted);
                                var purchasedCore = actor.Inventory.GetItem(granted.ListType, granted.SlotIndex) ?? granted.Core;
                                Need(buyMutation != null && purchasedCore != null, -3);
                                buyMutation.CoreSnapshot = purchasedCore.Copy();
                            }
                            var wallet = CurrencyService.LoadWallet(connection, transaction, actor.CharacterId);
                            // Native BUY 1135BA0 calls 16ED5A0, the main bag's
                            // virtual slot 2 getter. WalletSnapshot.Sp is not loaded.
                            shopWallet = new(actor.Inventory.GetMainVirtualCount(0)?.Count ?? 0,
                                actor.Inventory.GetMainVirtualCount(2)?.Count ?? 0, wallet.Cera);
                            if (buyMutation != null)
                            { buyMutation.UpdatedGold = shopWallet.Gold; buyMutation.UpdatedSp = shopWallet.Sp;
                              buyMutation.UpdatedCoin = shopWallet.Cera; buyMutation.GoldSpent = quote.Gold > 0; }
                            assets = true;
                            break;
                        default: throw new RoomFailure(-2);
                    }
                    if (command.Operation != WeddingRoomOperation.Query) WeddingRoomRepository.Save(connection, transaction, room);
                    Need(stillCurrent() && Current(actor) && (partner == null || Current(partner)), -2);
                    Need(InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, actor)
                        && (partner == null || InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, partner)), -3);
                    transaction.Commit(); actor.Inventory.ClearDirtyState(); partner?.Inventory.ClearDirtyState();
                    return new(0, room, assets, reward, boughtSlot, buyMutation, shopWallet, mailed.ToArray());
                }
                catch (Exception ex)
                {
                    if (mutated)
                    {
                        if (baselineA != null) InventoryContext.TryReplaceCurrentLease(actor, baselineA, out _);
                        if (baselineB != null) InventoryContext.TryReplaceCurrentLease(partner, baselineB, out _);
                    }
                    if (ex is RoomFailure failure) return new(failure.Code);
                    FileLogger.Log($"[WeddingRoom] transaction failed cid={actor.CharacterId}/{partnerId} operation={command.Operation}: {ex}");
                    return new(-3);
                }
            }
        }

        private void Move(WeddingRoomState room, WeddingRoomCommand command)
        {
            Need(command.From <= 1 && command.To <= 1 && !(command.From == command.To && command.FromSlot == command.ToSlot), -2);
            Need(command.FromSlot >= 0 && command.ToSlot >= 0 && command.FromSlot < (command.From == 0 ? 28 : 40)
                && command.ToSlot < (command.To == 0 ? 28 : 40), -2);
            var source = command.From == 0 ? room.Installed[command.FromSlot] == 0 ? null : new WeddingRoomItem(room.Installed[command.FromSlot], 1) : room.Cargo[command.FromSlot];
            var target = command.To == 0 ? room.Installed[command.ToSlot] == 0 ? null : new WeddingRoomItem(room.Installed[command.ToSlot], 1) : room.Cargo[command.ToSlot];
            Need(source != null && source.ItemId == command.FromItem && (target == null || target.ItemId == command.ToItem), -2);
            Need(command.From != 0 || command.To != 0, -2);
            if (command.To == 0) Need(_catalog.FurniturePositions.TryGetValue(source.ItemId, out var allowed) && allowed.Contains(command.ToSlot), -20);
            if (command.From == 1 && command.To == 1)
            { room.Cargo[command.FromSlot] = target; room.Cargo[command.ToSlot] = source; return; }
            if (command.From == 0)
            {
                Need(target == null || target.ItemId == source.ItemId && target.Count < ushort.MaxValue, -20);
                room.Cargo[command.ToSlot] = new(source.ItemId, (target?.Count ?? 0) + 1); room.Installed[command.FromSlot] = 0;
                return;
            }
            if (target?.ItemId == source.ItemId) return;
            if (target != null)
            {
                if (source.Count == 1) room.Cargo[command.FromSlot] = target;
                else { AddCargo(room, target.ItemId, 1); room.Cargo[command.FromSlot] = source with { Count = source.Count - 1 }; }
            }
            else room.Cargo[command.FromSlot] = source.Count == 1 ? null : source with { Count = source.Count - 1 };
            room.Installed[command.ToSlot] = source.ItemId;
        }
        private static int AddCargo(WeddingRoomState room, int itemId, int count)
        {
            Need(itemId > 0 && count is > 0 and <= ushort.MaxValue, -2);
            int slot = Array.FindIndex(room.Cargo, x => x?.ItemId == itemId && x.Count <= ushort.MaxValue - count);
            if (slot < 0) slot = Array.FindIndex(room.Cargo, x => x == null);
            Need(slot >= 0, -20); room.Cargo[slot] = new(itemId, (room.Cargo[slot]?.Count ?? 0) + count); return slot;
        }
        private static bool Current(InventoryLease lease) => InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);
        private static void Need(bool value, int error) { if (!value) throw new RoomFailure(error); }
        private static void ConsumeSlot(InventoryService inventory, int slot, int itemId, int count, long now)
        {
            Need(slot is >= 3 and < 352, -4);
            var item = inventory.GetItem(InventoryListType.Main, (short)slot);
            Need(item != null && !InventoryItemExpirationService.IsExpired(item, null, now)
                && InventoryDeleteService.TryConsumeFromSlot(inventory, InventoryListType.Main, (short)slot, itemId, count, out _), -9);
        }
        private static void Consume(InventoryService inventory, WeddingItemCost cost, long now)
        {
            int remaining = cost.Count;
            foreach (var entry in inventory.GetItems(InventoryListType.Main).OrderBy(x => x.Key).ToArray())
            {
                if (entry.Key is < 3 or >= 352 || entry.Value.ItemId != cost.ItemId || InventoryItemExpirationService.IsExpired(entry.Value, null, now)) continue;
                int count = Math.Min(remaining, entry.Value.Count); ConsumeSlot(inventory, entry.Key, cost.ItemId, count, now); remaining -= count;
                if (remaining == 0) return;
            }
            throw new RoomFailure(-9);
        }
        private static void Grant(InventoryService inventory, WeddingItemCost reward)
            => Need(InventoryRewardGrantService.TryCreateAndInsert(inventory, reward.ItemId, ItemCreateReason.Unknown, reward.Count, out _), -20);
        private static bool DeliverTree(IGameDatabase database, SqliteConnection connection, SqliteTransaction transaction,
            InventoryLease lease, int cid, WeddingRoomState room, WeddingItemCost reward)
        {
            // Capacity is checked before applying an insertion. Overflow must still
            // finish the harvest, otherwise full bags can filter random outcomes.
            var requests = new[] { InventoryRewardGrantRequest.Create(reward.ItemId, reward.Count, ItemCreateReason.Unknown) };
            if (lease != null && InventoryRewardGrantService.TryPlanBatch(lease.Inventory, requests, out _))
            { Grant(lease.Inventory, reward); return true; }
            var owner = WeddingRepository.LoadActor(connection, transaction, cid);
            Need(owner != null && !owner.Deleted, -2);
            var request = new MailboxSendRequest
            {
                SenderCharacterId = cid, SenderAccountId = owner.AccountId, SenderName = string.Empty,
                ReceiverCharacterId = cid, ReceiverAccountId = owner.AccountId, ReceiverName = owner.DisplayName, ReceiverLevel = owner.Level,
                Title = "夫妻树收获", Text = "夫妻树已收获，请领取属于你的奖励。", MailType = 1, Unlimited = true,
                AuditActor = "婚房系统", AuditReason = $"wedding-tree:{room.PairId}:{room.HarvestSequence}",
                IdempotencyKey = $"wedding-tree:{room.PairId}:{room.HarvestSequence}:{cid}",
                Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = reward.ItemId, ItemCount = reward.Count } },
            };
            Need(new MailboxRepository(database).SendSystemMails(connection, transaction, new[] { request }).Success, -3);
            return false;
        }
        private sealed class RoomFailure : Exception
        { internal int Code { get; } internal RoomFailure(int code) => Code = code; }
    }
}
