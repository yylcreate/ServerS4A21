using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingCommitCommand(byte Operation, byte Ceremony = 0, byte Delay = 0,
        byte Room = 0, int ChannelId = 0, int Slot = -1, byte Ring = 0, bool PartnerPresent = false);
    internal sealed record WeddingCommitResult(int Error, WeddingState State, bool AssetsChanged, bool GiftsMailed = false,
        int[] MailedCharacters = null)
    {
        internal bool Success => Error == 0;
        internal static WeddingCommitResult Failed(int error) => new(error, null, false);
    }

    internal sealed class WeddingCommitService
    {
        private readonly WeddingCatalog _catalog;
        private readonly Func<long> _now;
        internal WeddingCommitService(WeddingCatalog catalog, Func<long> now = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        // Caller holds both character transition gates. The spouse may be offline for
        // unilateral actions; no offline inventory is loaded or written in this path.
        internal WeddingCommitResult Commit(InventoryLease actor, InventoryLease partner, int partnerId,
            WeddingCommitCommand command, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (actor == null || command == null || partnerId <= 0 || partnerId == actor.CharacterId
                || (partner != null && partner.CharacterId != partnerId)) return WeddingCommitResult.Failed(-103);
            var first = partner != null && partner.CharacterId < actor.CharacterId ? partner : actor;
            var second = partner == null ? first : ReferenceEquals(first, actor) ? partner : actor;
            lock (first.SyncRoot)
            lock (second.SyncRoot)
            {
                if (!Current(actor) || (partner != null && !Current(partner)) || !stillCurrent())
                    return WeddingCommitResult.Failed(-101);
                if (!InventoryPersistenceService.SaveDirty(actor)
                    || (partner != null && !InventoryPersistenceService.SaveDirty(partner)))
                    return WeddingCommitResult.Failed(-102);
                InventoryService baselineActor = null, baselinePartner = null;
                bool mutated = false;
                try
                {
                    var database = actor.Inventory.Database;
                    if (partner != null && database.ConnectionString != partner.Inventory.Database.ConnectionString)
                        return WeddingCommitResult.Failed(-103);
                    using var connection = database.OpenConnection();
                    baselineActor = InventoryService.LoadFromDb(connection, actor.CharacterId, actor.AccountId, database);
                    if (partner != null)
                        baselinePartner = InventoryService.LoadFromDb(connection, partner.CharacterId, partner.AccountId, database);
                    using var transaction = connection.BeginTransaction();
                    using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
                    var a = WeddingRepository.LoadActor(connection, transaction, actor.CharacterId);
                    var b = WeddingRepository.LoadActor(connection, transaction, partnerId);
                    Require(a != null && b != null && !a.Deleted && !b.Deleted && a.AccountId == actor.AccountId
                        && (partner == null || b.AccountId == partner.AccountId), -101);
                    var before = WeddingRepository.Load(connection, transaction, actor.CharacterId);
                    var other = WeddingRepository.Load(connection, transaction, partnerId);
                    long now = _now();
                    var cooldownA = WeddingRepository.LoadCooldown(connection, transaction, actor.CharacterId);
                    var cooldownB = WeddingRepository.LoadCooldown(connection, transaction, partnerId);
                    WeddingState after = before;
                    bool assets = false;
                    bool giftsMailed = false;
                    int[] mailedCharacters = Array.Empty<int>();
                    mutated = true;
                    if (command.Operation == 1)
                    {
                        Require(partner != null, -101);
                        var eligibility = WeddingRules.CanEngage(a, b, before, other, _catalog,
                            cooldownA.DivorcedAt, cooldownB.DivorcedAt, now);
                        Require(eligibility == WeddingError.Success, (int)eligibility);
                        after = WeddingRepository.Create(connection, transaction, actor.CharacterId, partnerId, now);
                    }
                    else
                    {
                        Require(WeddingRules.IsReciprocal(before, other), -103);
                        switch (command.Operation)
                        {
                            case 2:
                                Require(partner != null && before.Status == WeddingStatus.Engaged
                                    && command.Ceremony <= 2 && command.Delay <= 2 && command.Room > 0, -103);
                                Require(now >= cooldownA.NextWeddingAt && now >= cooldownB.NextWeddingAt, (int)WeddingError.CeremonyCooldown);
                                foreach (int cid in new[] { actor.CharacterId, partnerId })
                                foreach (int quest in _catalog.RequiredQuests)
                                {
                                    using var prerequisite = connection.CreateCommand();
                                    prerequisite.Transaction = transaction;
                                    prerequisite.CommandText = "SELECT COUNT(*) FROM character_quest_completions WHERE character_id=@cid AND quest_id=@quest AND completion_value<>0;";
                                    prerequisite.Parameters.AddWithValue("@cid", cid);
                                    prerequisite.Parameters.AddWithValue("@quest", quest);
                                    Require(Convert.ToInt32(prerequisite.ExecuteScalar()) == 1, -9);
                                }
                                var gift = _catalog.Ceremonies[command.Ceremony].Gift;
                                var rewards = new[] { InventoryRewardGrantRequest.Create(gift.ItemId, gift.Count, ItemCreateReason.Unknown) };
                                Require(InventoryRewardGrantService.TryPlanBatch(actor.Inventory, rewards, out _)
                                    && InventoryRewardGrantService.TryPlanBatch(partner.Inventory, rewards, out _), -6);
                                DebitGold(actor.Inventory, _catalog.Ceremonies[command.Ceremony].Gold, -16);
                                if (command.Ceremony == _catalog.PaidCeremonyType)
                                    Consume(actor.Inventory, _catalog.PaidTicket, now);
                                after = before with { Status = WeddingStatus.Reserved, Ceremony = command.Ceremony,
                                    StartAt = checked(now + _catalog.ReservationDelaySeconds[command.Delay]),
                                    Room = command.Room, ChannelId = command.ChannelId };
                                long next = checked(now + (long)_catalog.WeddingCooldownDays * 86400);
                                WeddingRepository.SaveCooldown(connection, transaction, actor.CharacterId, null, next);
                                WeddingRepository.SaveCooldown(connection, transaction, partnerId, null, next);
                                assets = true;
                                break;
                            case 3:
                                Require(before.Status == WeddingStatus.Reserved && now >= before.StartAt, -103);
                                after = before with { Status = WeddingStatus.Ceremony };
                                break;
                            case 4:
                                Require(partner != null && before.Status == WeddingStatus.Ceremony
                                    && now >= checked(before.StartAt + _catalog.CeremonyEndSeconds), -103);
                                var ceremonyGift = _catalog.Ceremonies[before.Ceremony].Gift;
                                giftsMailed = !DeliverGift(connection, transaction, actor.Inventory, before, ceremonyGift);
                                giftsMailed |= !DeliverGift(connection, transaction, partner.Inventory, before, ceremonyGift);
                                after = before with { Status = WeddingStatus.Married, StartAt = 0, Room = 0,
                                    Ring = _catalog.Ceremonies[before.Ceremony].Ring, MarriedAt = now };
                                assets = true;
                                break;
                            case 5:
                            case 6:
                            case 7:
                                Require(before.Status == (command.Operation == 5 ? WeddingStatus.Engaged : WeddingStatus.Married), -103);
                                if (command.Operation == 5)
                                {
                                    // C94990 quotes [alimony][0] for engagement;
                                    // the native op-5 confirmation displays it.
                                    int cancellationCost = _catalog.Alimony[0];
                                    DebitGold(actor.Inventory, cancellationCost, -7);
                                    assets = cancellationCost != 0;
                                }
                                else
                                {
                                    // For operation 7 the network owner sets this only
                                    // after accepting the current bound invitation.
                                    Require(command.Operation != 7 || (partner != null && command.PartnerPresent), -103);
                                    // Both forms dissolve a completed marriage.
                                    // C94990 uses the married-stage fee [1].
                                    int cost = _catalog.Alimony[1];
                                    DebitGold(actor.Inventory, cost, -7);
                                    if (command.Operation == 7) DebitGold(partner.Inventory, cost, -7);
                                    WeddingRepository.SaveCooldown(connection, transaction, actor.CharacterId, now, null);
                                    WeddingRepository.SaveCooldown(connection, transaction, partnerId, now, null);
                                    assets = cost != 0;
                                }
                                after = before with { Status = WeddingStatus.None, Room = 0, StartAt = 0 };
                                DfoServer.Game.Quests.WeddingQuestEligibility.CancelActive(connection, transaction, actor.CharacterId, partnerId);
                                break;
                            case 8:
                                Require(before.Status == WeddingStatus.Married && command.Ring is >= 1 and <= 5
                                    && command.Ring == before.Ring + 1, -2);
                                ConsumeSlot(actor.Inventory, command.Slot, _catalog.RingTickets[command.Ring], now);
                                after = before with { Ring = command.Ring };
                                assets = true;
                                break;
                            case 9:
                                Require(before.Status == WeddingStatus.Married && command.Slot is >= 3 and < 352, -2);
                                var source = actor.Inventory.GetItem(InventoryListType.Main, checked((short)command.Slot));
                                int points = 0;
                                Require(source != null && _catalog.LoveItems.TryGetValue(source.ItemId, out points)
                                    && points > 0 && before.Love <= _catalog.MaximumLove - points, -2);
                                ConsumeSlot(actor.Inventory, command.Slot, new WeddingItemCost(source.ItemId, 1), now);
                                after = before with { Love = checked(before.Love + points) };
                                assets = true;
                                break;
                            default: throw new WeddingFailure(-103);
                        }
                        Require(WeddingRepository.Save(connection, transaction, before, after), -102);
                        if (command.Operation == 2)
                            WeddingReservationLedger.Record(connection, transaction, after, actor.CharacterId, _catalog);
                        if (command.Operation == 3)
                            mailedCharacters = new WeddingMoneyGiftCommitService(_catalog, _now)
                                .Settle(connection, transaction, database, before.PairId, before.StartAt);
                        if (after.Status == WeddingStatus.None) WeddingRepository.RemoveMembers(connection, transaction, before.PairId);
                        after = after.Status == WeddingStatus.None ? WeddingState.Empty(actor.CharacterId)
                            : WeddingRepository.Load(connection, transaction, actor.CharacterId);
                    }
                    Require(stillCurrent() && Current(actor) && (partner == null || Current(partner)), -101);
                    Require(InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, actor)
                        && (partner == null || InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, partner)), -102);
                    transaction.Commit();
                    actor.Inventory.ClearDirtyState();
                    partner?.Inventory.ClearDirtyState();
                    return new(0, after, assets, giftsMailed, mailedCharacters);
                }
                catch (Exception ex)
                {
                    if (mutated)
                    {
                        if (baselineActor != null) InventoryContext.TryReplaceCurrentLease(actor, baselineActor, out _);
                        if (partner != null && baselinePartner != null) InventoryContext.TryReplaceCurrentLease(partner, baselinePartner, out _);
                    }
                    if (ex is WeddingFailure failure) return WeddingCommitResult.Failed(failure.Code);
                    FileLogger.Log($"[Wedding] transaction failed cid={actor.CharacterId}/{partnerId} operation={command.Operation}: {ex}");
                    return WeddingCommitResult.Failed(-102);
                }
            }
        }

        private static bool Current(InventoryLease lease) => lease != null
            && InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);
        private static void Require(bool condition, int code) { if (!condition) throw new WeddingFailure(code); }
        private static void DebitGold(InventoryService inventory, int gold, int error)
        {
            int available = inventory.GetMainVirtualCount(0)?.Count ?? 0;
            Require(gold >= 0 && available >= gold && inventory.SetMainVirtualCount(0, available - gold), error);
        }
        private static void ConsumeSlot(InventoryService inventory, int slot, WeddingItemCost cost, long now)
        {
            Require(slot is >= 3 and < 352, -4);
            var item = inventory.GetItem(InventoryListType.Main, (short)slot);
            Require(item != null && !InventoryItemExpirationService.IsExpired(item, null, now)
                && InventoryDeleteService.TryConsumeFromSlot(inventory, InventoryListType.Main, (short)slot, cost.ItemId, cost.Count, out _), -4);
        }
        private static void Consume(InventoryService inventory, WeddingItemCost cost, long now)
        {
            int remaining = cost.Count;
            foreach (var pair in inventory.GetItems(InventoryListType.Main).OrderBy(x => x.Key).ToArray())
            {
                if (pair.Value.ItemId != cost.ItemId || pair.Key is < 3 or >= 352
                    || InventoryItemExpirationService.IsExpired(pair.Value, null, now)) continue;
                int amount = Math.Min(remaining, pair.Value.Count);
                ConsumeSlot(inventory, pair.Key, new(cost.ItemId, amount), now);
                remaining -= amount;
                if (remaining == 0) return;
            }
            throw new WeddingFailure(-14);
        }
        // A bag can fill after a reservation. Capacity must not hold the marriage
        // in Ceremony forever; the same transaction delivers overflow by mail.
        private static bool DeliverGift(SqliteConnection connection, SqliteTransaction transaction,
            InventoryService inventory, WeddingState state, WeddingItemCost item)
        {
            var rewards = new[] { InventoryRewardGrantRequest.Create(item.ItemId, item.Count, ItemCreateReason.Unknown) };
            if (InventoryRewardGrantService.TryPlanBatch(inventory, rewards, out _))
            {
                Require(InventoryRewardGrantService.TryCreateAndInsert(inventory, item.ItemId, ItemCreateReason.Unknown, item.Count, out _), -6);
                return true;
            }
            WeddingCeremonyGiftMail.Send(inventory.Database, connection, transaction, state.PairId, inventory.CharacterId, item);
            return false;
        }
        private sealed class WeddingFailure : Exception
        {
            internal int Code { get; }
            internal WeddingFailure(int code) => Code = code;
        }
    }
}
