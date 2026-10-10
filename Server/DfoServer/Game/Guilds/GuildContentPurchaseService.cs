using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildContentPurchaseService
    {
        private readonly GuildRepository _repository;
        internal GuildContentPurchaseService(GuildRepository repository)
            => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        internal GuildResult Purchase(InventoryLease lease, uint rowNumber, int expectedGuildId = 0)
        {
            if (lease == null || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return GuildResult.StaleSession;
            if (rowNumber == 0 || rowNumber > int.MaxValue) return GuildResult.NotFound;
            GuildContentDefinition definition;
            try { definition = GuildContentCatalog.Resolve((int)rowNumber); }
            catch (Exception exception)
            {
                FileLogger.Log($"[GuildContent] definition rejected row={rowNumber}: {exception.Message}");
                return GuildResult.PersistenceFailed;
            }
            if (definition == null) return GuildResult.NotFound;
            if (!string.Equals(lease.Inventory.Database?.ConnectionString, _repository.Database.ConnectionString,
                    StringComparison.Ordinal)) return GuildResult.PersistenceFailed;
            lock (lease.SyncRoot)
                return PurchaseLocked(lease, definition, expectedGuildId);
        }

        private GuildResult PurchaseLocked(InventoryLease lease, GuildContentDefinition definition, int expectedGuildId)
        {
            // Keep the baseline and failure recovery under one existing lease
            // lock; concurrent lifecycle mutations resume after the outcome.
            if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return GuildResult.StaleSession;
            if (!_repository.Database.Read(c => GuildRepository.IsActiveOwnedCharacter(
                    c, null, lease.CharacterId, lease.Inventory.AccountId))) return GuildResult.MissingCharacter;
            if (!InventoryPersistenceService.SaveDirty(lease)) return GuildResult.PersistenceFailed;

            var result = GuildResult.PersistenceFailed;
            bool committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "guild-content-buy", (c, t) =>
            {
                if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                    result = GuildResult.StaleSession;
                else if (!GuildRepository.IsActiveOwnedCharacter(c, t, lease.CharacterId, lease.Inventory.AccountId))
                    result = GuildResult.MissingCharacter;
                else
                {
                    var guild = GuildRepository.GetForMember(c, t, lease.CharacterId);
                    if (guild == null) result = GuildResult.NotFound;
                    else if (expectedGuildId > 0 && guild.Id != expectedGuildId) result = GuildResult.StaleSession;
                    else if (guild.LeaderId != lease.CharacterId) result = GuildResult.NotLeader;
                    else
                    {
                        result = ChargeCurrency(lease, c, t, guild, definition);
                        if (result == GuildResult.PersistenceFailed) return false;
                        if (result == GuildResult.Success)
                        {
                            result = GuildRepository.UpsertPurchasedContent(c, t, guild.Id,
                                definition.Category, definition.StorageDays, definition.UseCount);
                            if (result != GuildResult.Success) return false;
                        }
                    }
                }
                return true;
            });
            return committed ? result : GuildResult.PersistenceFailed;
        }

        private static GuildResult ChargeCurrency(InventoryLease lease, SqliteConnection c,
            SqliteTransaction t, GuildRecord guild, GuildContentDefinition definition)
        {
            if (!definition.CostsGold) return GuildRepository.TrySpendPoints(c, t, guild.Id, definition.Price);
            int gold = lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if ((long)definition.Price > gold) return GuildResult.InsufficientGold;
            return lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart,
                0, gold - checked((int)definition.Price)) ? GuildResult.Success : GuildResult.PersistenceFailed;
        }
    }
}
