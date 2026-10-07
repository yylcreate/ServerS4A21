using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Guilds
{
    internal readonly record struct GuildPartyTeleportTarget(
        InventoryLease Lease,
        bool PersistPosition);

    // The town owner validates the live party and resource route. This commit
    // boundary consumes one guild use and saves every durable destination in
    // the same transaction, before any town projection can fail or disconnect.
    internal static class GuildPartyTeleportCommitService
    {
        internal static bool TryCommit(
            InventoryLease leaderLease,
            int expectedGuildId,
            IReadOnlyList<GuildPartyTeleportTarget> targets,
            byte townId,
            byte areaId,
            short x,
            short y,
            Func<bool> isCurrent,
            out int remainingUses)
        {
            remainingUses = -1;
            if (leaderLease == null || expectedGuildId <= 0
                || targets == null || targets.Count == 0
                || isCurrent == null || !isCurrent())
                return false;

            var ordered = targets.OrderBy(target => target.Lease?.CharacterId ?? 0).ToArray();
            if (ordered.Any(target => target.Lease == null)
                || ordered.Select(target => target.Lease.CharacterId).Distinct().Count() != ordered.Length
                || !ordered.Any(target => ReferenceEquals(target.Lease, leaderLease)))
                return false;

            var committedUses = -1;
            bool CommitLocked(int index)
            {
                if (index < ordered.Length)
                {
                    lock (ordered[index].Lease.SyncRoot)
                        return CommitLocked(index + 1);
                }

                var database = leaderLease.Inventory.Database;
                if (database == null || !isCurrent()
                    || ordered.Any(target => target.Lease.AccountId <= 0
                        || !InventoryContext.IsCurrentLease(target.Lease,
                            target.Lease.SessionId, target.Lease.CharacterId)
                        || !string.Equals(target.Lease.Inventory.Database?.ConnectionString,
                            database.ConnectionString, StringComparison.Ordinal)))
                    return false;

                // The existing commit coordinator reloads on rollback. Save
                // the leader's previously accepted dirty state first, as the
                // auction owner does, so a refused teleport cannot erase it.
                if (!InventoryPersistenceService.SaveDirty(leaderLease) || !isCurrent())
                    return false;

                return OnlineInventoryMutationCommitCoordinator.TryCommit(
                    leaderLease, "guild-party-teleport", (connection, transaction) =>
                    {
                        if (!isCurrent()) return false;
                        foreach (var target in ordered)
                            if (!GuildRepository.IsActiveOwnedCharacter(connection, transaction,
                                    target.Lease.CharacterId, target.Lease.AccountId))
                                return false;

                        committedUses = GuildRepository.ConsumeContentUseInTransaction(
                            connection, transaction, leaderLease.CharacterId,
                            expectedGuildId, GuildContentCatalog.PartyTeleportCategory, DateTime.UtcNow);
                        if (committedUses < 0) return false;

                        foreach (var target in ordered)
                            if (target.PersistPosition
                                && !SqliteCharacterRepository.UpdatePositionInTransaction(
                                    connection, transaction, target.Lease.CharacterId,
                                    townId, areaId, x, y, direction: 5, areaState: 0))
                                return false;
                        return isCurrent();
                    });
            }

            if (!CommitLocked(0)) return false;
            remainingUses = committedUses;
            return true;
        }
    }
}
