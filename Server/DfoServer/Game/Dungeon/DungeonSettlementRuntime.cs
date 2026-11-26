using DfoServer.Game.Progression;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    // Frozen settlement inputs and outputs for one participant. Retries reuse
    // this object so random rewards and irreversible grants are not recomputed.
    internal sealed class DungeonSettlementRuntime
    {
        internal bool IsTowerOfDespair;
        internal int TowerOfDespairFloor;
        internal bool ShouldScheduleCardRewardFlow;
        internal BloodAltar.BloodAltarParticipantSettlementRuntime BloodAltar;
        internal LicensedDungeonRewardRuntime LicensedDungeon;
        internal GuildDungeonClearEffectResult GuildDungeonClear;

        internal void FreezeGuildDungeonReward(DungeonRun run, GuildDungeonClearEffectResult result)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(result);
            var admission = run.Instance.GuildDungeonAdmission;
            if (admission == null || admission.GuildId != result.GuildId
                || run.DungeonId != result.DungeonId || run.ClearedFact == null
                || run.ClearedFact.SourceEventId != result.SourceEventId
                || result.PreviousClearCount < 0 || result.ClearCount < result.PreviousClearCount
                || !Game.Guilds.GuildDungeonMonth.IsValid(result.MonthId)
                || (result.HasReward && (result.ClearCount == result.PreviousClearCount
                    || result.RewardGaugeRatio < 0 || result.RewardContribute < 0))
                || (!result.HasReward && (result.RewardItemId != 0 || result.RewardCount != 0)))
                throw new InvalidOperationException("Guild card reward does not match the accepted clear owner.");
            var reward = default(ClearRewardGenerator.CardReward);
            if (result.HasReward)
            {
                var metadata = ItemMetadataResolver.Resolve(result.RewardItemId);
                if (metadata?.ItemKind != "stackable" && metadata?.ItemKind != "equipment")
                    throw new InvalidOperationException("Guild clear reward has no registered item metadata.");
                reward = new ClearRewardGenerator.CardReward
                {
                    ItemId = result.RewardItemId, StackCount = result.RewardCount,
                    IsEquipment = metadata.ItemKind == "equipment", Durability = metadata.Durability
                };
            }
            lock (run.SyncRoot)
            {
                if (!ReferenceEquals(run.SettlementRuntime, this))
                    throw new InvalidOperationException("Guild clear reward requires the original settlement runtime.");
                if (GuildDungeonClear != null)
                {
                    if (GuildDungeonClear.SourceEventId != result.SourceEventId
                        || GuildDungeonClear.GuildId != result.GuildId || GuildDungeonClear.DungeonId != result.DungeonId
                        || GuildDungeonClear.MonthId != result.MonthId || GuildDungeonClear.PreviousClearCount != result.PreviousClearCount
                        || GuildDungeonClear.ClearCount != result.ClearCount || GuildDungeonClear.RewardGaugeRatio != result.RewardGaugeRatio
                        || GuildDungeonClear.RewardItemId != result.RewardItemId || GuildDungeonClear.RewardCount != result.RewardCount
                        || GuildDungeonClear.RewardContribute != result.RewardContribute)
                        throw new InvalidOperationException("Guild reward cannot replace its original durable choice.");
                    return;
                }
                if (!ShouldScheduleCardRewardFlow || run.CardRewards == null || run.CardRewards.Count < 2)
                    throw new InvalidOperationException("Guild clear reward requires the original prepared card layout.");
                GuildDungeonClear = new GuildDungeonClearEffectResult
                {
                    SourceEventId = result.SourceEventId,
                    GuildId = result.GuildId, DungeonId = result.DungeonId, MonthId = result.MonthId,
                    PreviousClearCount = result.PreviousClearCount, ClearCount = result.ClearCount,
                    RewardGaugeRatio = result.RewardGaugeRatio, RewardItemId = result.RewardItemId,
                    RewardCount = result.RewardCount, RewardContribute = result.RewardContribute,
                    Contribution = result.Contribution == null ? null : result.Contribution with
                    { AppliedMembers = Array.AsReadOnly(new List<Game.Guilds.GuildMemberContributionApplied>(result.Contribution.AppliedMembers).ToArray()) }
                };
                FreeItem = reward;
                run.CardRewards[1] = reward;
            }
        }

        // The current protocol supplies a bounded, first-write-wins rank via
        // SET_PLAY_RESULT. It can adjust score EXP; independent server-side
        // score validation remains a separate authority boundary.
        internal byte ClientRankPoint;
        internal int PresentationRankPoint;
        internal byte PresentationRankGrade;
        internal int PresentationRankBonusIndex;
        internal bool AuthoritativeRankCaptured;
        internal int TimeBonusPoint;
        internal int RankPoint;
        internal byte RankGrade;
        internal int RankBonusIndex;

        internal uint ClearBaseExp;
        internal uint ScoreBonusExp;
        internal uint PartyClearBreakdownExp;
        internal uint AvatarBonusExp;
        internal uint CreatureBonusExp;
        internal uint GrowthContractBonusExp;
        internal uint BlackDiamondBonusExp;
        internal uint AdventureGroupBonusExp;
        internal int ExperiencePotionBonusRate;
        internal uint ExperiencePotionBonusExp;
        internal uint FatigueBurnBonusExp;
        internal uint ClearBonusExp;
        internal uint ClearTotalExp;
        internal byte PreviousLevel;
        internal uint PreviousExp;
        internal ExperienceGrantResult ExperienceGrant;
        internal ExperienceGrantResult ScoreAdjustmentExperienceGrant;

        internal int DungeonLevel;
        internal int PaidCardCost;
        internal bool PaidCardUsesDevilContract;
        internal ClearRewardGenerator.CardReward FreeGold;
        internal ClearRewardGenerator.CardReward FreeItem;
        internal ClearRewardGenerator.CardReward PaidGold;
        internal ClearRewardGenerator.CardReward PaidItem;
        internal IReadOnlyList<ClearRewardGenerator.CardReward>
            TowerRewardCandidates = Array.Empty<ClearRewardGenerator.CardReward>();
        internal IReadOnlyList<TowerOfDespairGrantedReward>
            TowerGrantedRewards = Array.Empty<TowerOfDespairGrantedReward>();

        // Dungeon permission is a persistent effect followed by a separate
        // client projection. Keeping the plan on the runtime lets a retry
        // resend the projection after the database write already succeeded.
        internal bool DungeonPermissionPlanReady;
        internal bool DungeonPermissionChanged;
        internal int DungeonPermissionAccountId;
        internal IReadOnlyList<DungeonPermissionEntrySnapshot>
            DungeonPermissionEntries = Array.Empty<DungeonPermissionEntrySnapshot>();

        internal uint MonsterTotalExp;
        internal uint BossTotalExp;
        internal uint ChampionTotalExp;
        internal uint SuperChampionTotalExp;
        internal uint NamedMonsterTotalExp;
        internal uint MonsterGrowthContractBonusExp;
        internal uint MonsterEquipmentBonusExp;
        // Guild kill experience is included in MonsterTotalExp. It has no
        // dedicated A21 display slot and must not replace another bonus line.
        internal uint MonsterGuildBonusExp;
        internal uint MonsterFatigueBurnBonusExp;
        internal IReadOnlyList<DungeonObjectExperienceEntry>
            ObjectExperienceEntries = Array.Empty<DungeonObjectExperienceEntry>();
        internal int ClearTimeMilliseconds;
    }
}
