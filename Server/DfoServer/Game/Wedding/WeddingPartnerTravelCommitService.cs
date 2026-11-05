using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal static class WeddingPartnerTravelCommitService
    {
        internal static bool TryReadSource(InventoryService inventory, int slot, long now, out int itemId, out byte[] core)
        {
            itemId = 0; core = null;
            if (inventory == null || slot is < 3 or >= 352) return false;
            var source = inventory.GetItem(InventoryListType.Main, (short)slot);
            if (source == null || source.Count <= 0 || InventoryItemExpirationService.IsExpired(source, null, now)
                || !ItemMetadataResolver.TryLoadStackableFile(source.ItemId, out var definition)
                || !string.Equals(definition.ActionTypeName?.Trim('[', ']', ' '), "teleport to partner", StringComparison.OrdinalIgnoreCase)
                || definition.ActionTypeParams.Count != 1 || definition.ActionTypeParams[0] != 1) return false;
            itemId = source.ItemId; core = source.ToBytes(); return true;
        }

        // The caller holds both character gates and has checked current world admission.
        // Cost and a normal destination commit together, before network projection.
        // The temporary couple room preserves the existing town-return anchor.
        internal static bool TryCommit(InventoryLease actor, WeddingPartnerTravelTicket ticket, byte direction, byte areaState,
            Func<bool> stillCurrent, Func<long> unixNow = null, Func<long> monotonicNow = null)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (actor == null || ticket == null || ticket.ActorId != actor.CharacterId || ticket.ActorSession != actor.SessionId) return false;
            unixNow ??= () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(); monotonicNow ??= () => Environment.TickCount64;
            lock (actor.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(actor, ticket.ActorSession, ticket.ActorId) || !stillCurrent()
                    || monotonicNow() >= ticket.ExpiresTick || !TryReadSource(actor.Inventory, ticket.Slot, unixNow(), out int itemId, out var core)
                    || itemId != ticket.ItemId || !core.SequenceEqual(ticket.ItemCore ?? Array.Empty<byte>())) return false;
                // Preserve unrelated dirty inventory if this transaction must roll back.
                if (!InventoryPersistenceService.SaveDirty(actor)) return false;
                return OnlineInventoryMutationCommitCoordinator.TryCommit(actor, "wedding-partner-travel", (connection, transaction) =>
                {
                    if (!stillCurrent() || monotonicNow() >= ticket.ExpiresTick
                        || !TryReadSource(actor.Inventory, ticket.Slot, unixNow(), out itemId, out core)
                        || itemId != ticket.ItemId || !core.SequenceEqual(ticket.ItemCore ?? Array.Empty<byte>())) return false;
                    var marriage = WeddingRepository.Load(connection, transaction, ticket.ActorId);
                    var spouse = WeddingRepository.Load(connection, transaction, ticket.PartnerId);
                    var owner = WeddingRepository.LoadActor(connection, transaction, ticket.ActorId);
                    var peer = WeddingRepository.LoadActor(connection, transaction, ticket.PartnerId);
                    if (owner == null || peer == null || owner.Deleted || peer.Deleted || owner.AccountId != actor.AccountId
                        || marriage.Status != WeddingStatus.Married || marriage.PairId != ticket.PairId || marriage.Version != ticket.PairVersion
                        || !WeddingRules.IsReciprocal(marriage, spouse)) return false;
                    var use = InventoryItemLifecycleService.PrepareUse(actor.Inventory, InventoryListType.Main,
                        (short)ticket.Slot, ticket.ItemId, unixNow());
                    if (!use.Success || !UsableCountLimitService.TryRecordUseIfLimited(connection, transaction, actor.CharacterId,
                        ticket.ItemId, 1, out _)
                        || !InventoryDeleteService.TryUseStackableForClient(actor.Inventory, InventoryListType.Main,
                            (short)ticket.Slot, ticket.ItemId, out _)) return false;
                    InventoryItemLifecycleService.ApplyUseSuccess(actor.Inventory, use);
                    if (!stillCurrent()) return false;
                    var home = WeddingSceneCatalog.Get().CoupleRoom;
                    if (ticket.Destination.Town == home.Town && ticket.Destination.Area == home.Area) return true;
                    return SqliteCharacterRepository.UpdatePositionInTransaction(connection, transaction, actor.CharacterId,
                        ticket.Destination.Town, ticket.Destination.Area, ticket.Destination.X, ticket.Destination.Y, direction, areaState);
                }, immediate: true);
            }
        }
    }
}
