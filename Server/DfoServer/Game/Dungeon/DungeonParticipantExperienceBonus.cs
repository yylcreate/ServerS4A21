using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonParticipantEquipmentBonusFacts
    {
        internal DungeonParticipantEquipmentBonusFacts(
            bool hasEquippedAvatar,
            bool hasEquippedCreature)
        {
            HasEquippedAvatar = hasEquippedAvatar;
            HasEquippedCreature = hasEquippedCreature;
        }

        internal bool HasEquippedAvatar { get; }
        internal bool HasEquippedCreature { get; }
    }

    internal readonly struct DungeonParticipantExperienceBonusSnapshot
    {
        internal DungeonParticipantExperienceBonusSnapshot(
            int partyMemberCount,
            bool partyHasEquippedAvatar,
            bool hasEquippedCreature,
            int storyExperienceBonusRatePercent = 0,
            int storyExperienceDifficulty = -1,
            int fatigueBurnExperienceBonusPercent = 0,
            int fatigueConsumptionMultiplier = 1)
        {
            IsCaptured = true;
            PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount));
            PartyHasEquippedAvatar = partyHasEquippedAvatar;
            HasEquippedCreature = hasEquippedCreature;
            StoryExperienceBonusRatePercent =
                storyExperienceBonusRatePercent > 0
                    ? storyExperienceBonusRatePercent
                    : 0;
            StoryExperienceDifficulty = storyExperienceDifficulty >= 0
                ? storyExperienceDifficulty
                : -1;
            FatigueBurnExperienceBonusPercent = Math.Max(0, fatigueBurnExperienceBonusPercent);
            FatigueConsumptionMultiplier = Math.Max(1, fatigueConsumptionMultiplier);
        }

        internal static DungeonParticipantExperienceBonusSnapshot None =>
            new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 1,
                partyHasEquippedAvatar: false,
                hasEquippedCreature: false);

        internal bool IsCaptured { get; }
        internal int PartyMemberCount { get; }
        internal bool PartyHasEquippedAvatar { get; }
        internal bool HasEquippedCreature { get; }
        internal int StoryExperienceBonusRatePercent { get; }
        internal int StoryExperienceDifficulty { get; }
        internal int FatigueBurnExperienceBonusPercent { get; }
        internal int FatigueConsumptionMultiplier { get; }

        // Pure interface for a future unified fatigue debit service. This does
        // not spend fatigue or change any persistent balance.
        internal uint ScaleFatigueConsumption(uint baseCost)
            => checked((uint)((ulong)baseCost * (uint)Math.Max(1, FatigueConsumptionMultiplier)));
        internal bool HasStoryExperienceProfile =>
            StoryExperienceDifficulty >= 0;

        internal int ResolveExperienceDifficulty(int fallbackDifficulty) =>
            HasStoryExperienceProfile
                ? StoryExperienceDifficulty
                : fallbackDifficulty;

        internal DungeonParticipantExperienceBonusSnapshot
            WithStoryExperienceProfile(
                int ratePercent,
                int experienceDifficulty)
            => new DungeonParticipantExperienceBonusSnapshot(
                PartyMemberCount,
                PartyHasEquippedAvatar,
                HasEquippedCreature,
                ratePercent,
                experienceDifficulty,
                FatigueBurnExperienceBonusPercent,
                FatigueConsumptionMultiplier);
    }

    internal readonly struct DungeonClearParticipantBonusResult
    {
        internal DungeonClearParticipantBonusResult(
            uint avatarBonusExperience,
            uint creatureBonusExperience)
        {
            AvatarBonusExperience = avatarBonusExperience;
            CreatureBonusExperience = creatureBonusExperience;
        }

        internal uint AvatarBonusExperience { get; }
        internal uint CreatureBonusExperience { get; }
        internal uint TotalBonusExperience => AddSaturating(
            AvatarBonusExperience,
            CreatureBonusExperience);

        private static uint AddSaturating(uint left, uint right)
        {
            var total = (ulong)left + right;
            return total >= uint.MaxValue ? uint.MaxValue : (uint)total;
        }
    }

    internal static class DungeonParticipantExperienceBonusSnapshotCapture
    {
        internal static bool TryCaptureOwned(
            Guid sessionId,
            int characterId,
            out DungeonParticipantEquipmentBonusFacts facts,
            out InventoryLease lease)
        {
            facts = default;
            if (!InventoryContext.TryGetOwnedLease(
                    sessionId,
                    characterId,
                    out lease))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        sessionId,
                        characterId))
                {
                    return false;
                }

                facts = CaptureInventory(lease.Inventory);
                return true;
            }
        }

        internal static DungeonParticipantEquipmentBonusFacts CaptureInventory(
            InventoryService inventory)
        {
            if (inventory == null)
                return default;

            var hasEquippedAvatar = false;
            for (var slot = (short)EquipmentType.HatAvatar;
                 slot <= (short)EquipmentType.AuroraIllusionAvatar;
                 slot++)
            {
                var item = inventory.GetItem(
                    InventoryListType.Equipment,
                    slot);
                if (item?.ItemKind == ItemCore.KindAvatar)
                {
                    hasEquippedAvatar = true;
                    break;
                }
            }

            var hasEquippedCreature = PetInventoryAccessor.TryGetEquippedCreature(
                inventory,
                out _,
                out _);
            return new DungeonParticipantEquipmentBonusFacts(
                hasEquippedAvatar,
                hasEquippedCreature);
        }
    }
}
