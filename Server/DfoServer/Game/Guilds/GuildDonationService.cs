using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildDonationService
    {
        private readonly GuildRepository _repository;
        private readonly int _donateUnit;

        internal GuildDonationService(GuildRepository repository) : this(repository, ReadDonationUnit()) { }

        internal GuildDonationService(GuildRepository repository, int donateUnit)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            if (donateUnit <= 0) throw new ArgumentOutOfRangeException(nameof(donateUnit));
            _donateUnit = donateUnit;
        }

        internal static int ReadDonationUnit()
        {
            var config = GuildAdditionalContentsConfigFile.Parse(PvfArchiveAccessor.ReadText("etc/(r)guild.etc"));
            if (!config.IsValid || config.DonationUnit == null)
                throw new InvalidDataException("PVF guild donation unit is missing or invalid: " + config.InvalidReason);
            return config.DonationUnit.Value;
        }

        internal GuildResult Donate(InventoryLease lease, uint amount, int expectedGuildId = 0)
        {
            if (amount == 0 || amount % (uint)_donateUnit != 0) return GuildResult.InvalidAmount;
            if (lease == null || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return GuildResult.StaleSession;
            if (!string.Equals(lease.Inventory.Database?.ConnectionString, _repository.Database.ConnectionString,
                    StringComparison.Ordinal)) return GuildResult.PersistenceFailed;
            lock (lease.SyncRoot)
                return DonateLocked(lease, amount, expectedGuildId);
        }

        private GuildResult DonateLocked(InventoryLease lease, uint amount, int expectedGuildId)
        {
            // The existing rollback coordinator reloads persisted inventory. Save
            // previous dirty changes first and retain the same lease lock through
            // commit/recovery so another accepted mutation cannot enter that gap.
            if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return GuildResult.StaleSession;
            if (!_repository.Database.Read(c => GuildRepository.IsActiveOwnedCharacter(
                    c, null, lease.CharacterId, lease.Inventory.AccountId))) return GuildResult.MissingCharacter;
            if (!InventoryPersistenceService.SaveDirty(lease)) return GuildResult.PersistenceFailed;

            var result = GuildResult.PersistenceFailed;
            bool committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "guild-donate", (c, t) =>
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
                    else
                    {
                        int gold = lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                        uint points = amount / (uint)_donateUnit;
                        if ((long)amount > gold) result = GuildResult.InsufficientGold;
                        else if ((ulong)guild.Points + points > uint.MaxValue) result = GuildResult.InvalidAmount;
                        else
                        {
                            if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart,
                                    0, gold - checked((int)amount))) return false;
                            result = GuildRepository.AddPoints(c, t, guild.Id, points);
                            // The debit and the full converted credit have one commit.
                            if (result != GuildResult.Success) return false;
                        }
                    }
                }
                return true;
            });
            return committed ? result : GuildResult.PersistenceFailed;
        }
    }
}
