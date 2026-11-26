using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public readonly struct DungeonObjectExperienceEntry
    {
        public DungeonObjectExperienceEntry(uint objectKey, uint experience)
        {
            ObjectKey = objectKey;
            Experience = experience;
        }

        public uint ObjectKey { get; }
        public uint Experience { get; }
    }

    internal readonly record struct DungeonGuildExperienceBonusPlan(
        long Revision,
        uint Experience,
        decimal CarryAfter);

    internal readonly record struct DungeonEquipmentExperienceBonusPlan(
        long Revision,
        uint Experience,
        decimal CarryAfter);

    internal readonly struct DungeonParticipantExperienceSnapshot
    {
        internal DungeonParticipantExperienceSnapshot(
            uint monsterBaseExperience,
            uint monsterGrowthContractBonusExperience,
            uint bossBaseExperience,
            uint championBaseExperience,
            uint superChampionBaseExperience,
            uint namedMonsterBaseExperience,
            IReadOnlyList<DungeonObjectExperienceEntry> objectExperienceEntries = null,
            uint monsterEquipmentBonusExperience = 0,
            uint monsterFatigueBurnBonusExperience = 0,
            uint monsterGuildBonusExperience = 0)
        {
            MonsterBaseExperience = monsterBaseExperience;
            MonsterGrowthContractBonusExperience =
                monsterGrowthContractBonusExperience;
            MonsterEquipmentBonusExperience = monsterEquipmentBonusExperience;
            MonsterFatigueBurnBonusExperience = monsterFatigueBurnBonusExperience;
            MonsterGuildBonusExperience = monsterGuildBonusExperience;
            BossBaseExperience = bossBaseExperience;
            ChampionBaseExperience = championBaseExperience;
            SuperChampionBaseExperience = superChampionBaseExperience;
            NamedMonsterBaseExperience = namedMonsterBaseExperience;
            ObjectExperienceEntries = objectExperienceEntries
                ?? Array.Empty<DungeonObjectExperienceEntry>();
        }

        internal uint MonsterBaseExperience { get; }
        internal uint MonsterGrowthContractBonusExperience { get; }
        internal uint MonsterEquipmentBonusExperience { get; }
        internal uint MonsterFatigueBurnBonusExperience { get; }
        // A21 has no separate guild line. Keep its actual award in the
        // aggregate monster total without reusing the equipment/contract slots.
        internal uint MonsterGuildBonusExperience { get; }
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                AddSaturating(
                    MonsterEquipmentBonusExperience,
                    AddSaturating(
                        MonsterFatigueBurnBonusExperience,
                        MonsterGuildBonusExperience))));
        internal uint BossBaseExperience { get; }
        internal uint ChampionBaseExperience { get; }
        internal uint SuperChampionBaseExperience { get; }
        internal uint NamedMonsterBaseExperience { get; }
        internal IReadOnlyList<DungeonObjectExperienceEntry>
            ObjectExperienceEntries { get; }

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }

    // Per-participant awarded EXP ledger. World kill counts remain on
    // DungeonInstance; this runtime only records this player's projection.
    internal sealed class DungeonParticipantExperienceRuntime
    {
        private DungeonParticipantExperienceBonusSnapshot _bonusSnapshot =
            DungeonParticipantExperienceBonusSnapshot.None;
        private bool _bonusSnapshotFrozen;
        private readonly List<DungeonObjectExperienceEntry>
            _objectExperienceEntries = new List<DungeonObjectExperienceEntry>();

        internal uint MonsterBaseExperience { get; private set; }
        internal uint MonsterGrowthContractBonusExperience { get; private set; }
        internal uint MonsterEquipmentBonusExperience { get; private set; }
        internal uint MonsterFatigueBurnBonusExperience { get; private set; }
        internal uint MonsterGuildBonusExperience { get; private set; }
        // 纹章加成的小数零头(decimal 避免双精度漂移吞掉进位)。
        // 逐只 floor 会在等级惩罚的小基数下吞掉全部加成,
        // 零头带进下一只, 整局合计 ≈ 基础总额 × 百分比。
        private decimal _equipmentBonusCarry;
        private long _equipmentBonusRevision;
        private decimal _guildBonusCarry;
        private long _guildBonusRevision;
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                AddSaturating(
                    MonsterEquipmentBonusExperience,
                    AddSaturating(
                        MonsterFatigueBurnBonusExperience,
                        MonsterGuildBonusExperience))));
        internal uint BossBaseExperience { get; private set; }
        internal uint ChampionBaseExperience { get; private set; }
        internal uint SuperChampionBaseExperience { get; private set; }
        internal uint NamedMonsterBaseExperience { get; private set; }

        internal bool TryFreezeBonusSnapshot(
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (_bonusSnapshotFrozen || !snapshot.IsCaptured)
                return false;

            _bonusSnapshot = snapshot;
            _bonusSnapshotFrozen = true;
            return true;
        }

        internal DungeonParticipantExperienceBonusSnapshot
            CaptureBonusSnapshot() => _bonusSnapshot;

        internal bool TryFreezeStoryExperienceProfile(
            int ratePercent,
            int experienceDifficulty)
        {
            if (!_bonusSnapshotFrozen
                || _bonusSnapshot.HasStoryExperienceProfile
                || ratePercent < 0
                || experienceDifficulty < 0)
            {
                return false;
            }

            _bonusSnapshot = _bonusSnapshot
                .WithStoryExperienceProfile(
                    ratePercent,
                    experienceDifficulty);
            return true;
        }

        internal void RecordMonster(
            uint baseExperience,
            uint growthContractBonusExperience,
            bool isBoss,
            bool isChampion,
            bool isSuperChampion,
            bool isNamedMonster,
            ushort actorSequenceId = 0,
            uint equipmentBonusExperience = 0,
            uint fatigueBurnBonusExperience = 0,
            uint guildBonusExperience = 0)
        {
            MonsterBaseExperience = AddSaturating(
                MonsterBaseExperience,
                baseExperience);
            MonsterGrowthContractBonusExperience = AddSaturating(
                MonsterGrowthContractBonusExperience,
                growthContractBonusExperience);
            MonsterEquipmentBonusExperience = AddSaturating(
                MonsterEquipmentBonusExperience,
                equipmentBonusExperience);
            MonsterFatigueBurnBonusExperience = AddSaturating(
                MonsterFatigueBurnBonusExperience,
                fatigueBurnBonusExperience);
            MonsterGuildBonusExperience = AddSaturating(
                MonsterGuildBonusExperience,
                guildBonusExperience);
            if (isBoss)
                BossBaseExperience = AddSaturating(
                    BossBaseExperience,
                    baseExperience);
            if (isChampion)
                ChampionBaseExperience = AddSaturating(
                    ChampionBaseExperience,
                    baseExperience);
            if (isSuperChampion)
                SuperChampionBaseExperience = AddSaturating(
                    SuperChampionBaseExperience,
                    baseExperience);
            if (isNamedMonster)
                NamedMonsterBaseExperience = AddSaturating(
                    NamedMonsterBaseExperience,
                    baseExperience);
            if (actorSequenceId != 0)
                _objectExperienceEntries.Add(
                    new DungeonObjectExperienceEntry(
                        actorSequenceId,
                        baseExperience));
        }

        internal DungeonParticipantExperienceSnapshot Capture()
            => new DungeonParticipantExperienceSnapshot(
                MonsterBaseExperience,
                MonsterGrowthContractBonusExperience,
                BossBaseExperience,
                ChampionBaseExperience,
                SuperChampionBaseExperience,
                NamedMonsterBaseExperience,
                _objectExperienceEntries.ToArray(),
                MonsterEquipmentBonusExperience,
                MonsterFatigueBurnBonusExperience,
                MonsterGuildBonusExperience);

        // Compatibility setters keep existing fixture/setup APIs usable. New
        // production code records awards only through RecordMonster.
        internal void SetMonsterTotalForCompatibility(uint value)
        {
            MonsterBaseExperience = value;
            MonsterGrowthContractBonusExperience = 0;
            MonsterFatigueBurnBonusExperience = 0;
            MonsterGuildBonusExperience = 0;
        }

        internal void SetBossBaseForCompatibility(uint value) =>
            BossBaseExperience = value;

        internal void SetChampionBaseForCompatibility(uint value) =>
            ChampionBaseExperience = value;

        internal void SetSuperChampionBaseForCompatibility(uint value) =>
            SuperChampionBaseExperience = value;

        internal void SetNamedMonsterBaseForCompatibility(uint value) =>
            NamedMonsterBaseExperience = value;

        internal void SetGrowthContractBonusForCompatibility(uint value) =>
            MonsterGrowthContractBonusExperience = value;

        internal void SetEquipmentBonusForCompatibility(uint value) =>
            MonsterEquipmentBonusExperience = value;

        internal void SetGuildBonusForCompatibility(uint value) =>
            MonsterGuildBonusExperience = value;

        // 快捷栏纹章加成: 按百分比+进位零头计算本次应发整数, 累计由
        // RecordMonster 的 equipmentBonusExperience 完成。仅在 run.SyncRoot 下调用。
        internal uint ApplyEquipmentBonusRate(uint baseExperience, int percent)
        {
            var plan = PreviewEquipmentBonusRate(baseExperience, percent);
            if (!TryCommitEquipmentBonusRate(plan))
                throw new InvalidOperationException("Equipment bonus carry plan was replaced before commit.");
            return plan.Experience;
        }

        internal DungeonEquipmentExperienceBonusPlan PreviewEquipmentBonusRate(uint baseExperience, int percent)
        {
            if (baseExperience == 0 || percent <= 0)
                return new DungeonEquipmentExperienceBonusPlan(_equipmentBonusRevision, 0, _equipmentBonusCarry);

            var exact = baseExperience * (percent / 100m) + _equipmentBonusCarry;
            var grant = exact >= uint.MaxValue
                ? uint.MaxValue
                : (uint)decimal.Floor(exact);
            return new DungeonEquipmentExperienceBonusPlan(_equipmentBonusRevision, grant, exact - grant);
        }

        internal bool TryCommitEquipmentBonusRate(DungeonEquipmentExperienceBonusPlan plan)
        {
            if (plan.Revision != _equipmentBonusRevision)
                return false;
            long revision = checked(_equipmentBonusRevision + 1);
            _equipmentBonusCarry = plan.CarryAfter;
            _equipmentBonusRevision = revision;
            return true;
        }

        // Carry belongs to this participant's run and must stay independent
        // from equipment. Discard an integer overflow instead of carrying it
        // forward as a later monster award.
        internal uint ApplyGuildBonusRate(uint baseExperience, int percent)
        {
            var plan = PreviewGuildBonusRate(baseExperience, percent);
            if (!TryCommitGuildBonusRate(plan))
                throw new InvalidOperationException("Guild bonus carry plan was replaced before commit.");
            return plan.Experience;
        }

        internal DungeonGuildExperienceBonusPlan PreviewGuildBonusRate(uint baseExperience, int percent)
        {
            if (baseExperience == 0 || percent <= 0)
                return new DungeonGuildExperienceBonusPlan(_guildBonusRevision, 0, _guildBonusCarry);

            var exact = baseExperience * (percent / 100m) + _guildBonusCarry;
            var integral = decimal.Floor(exact);
            return new DungeonGuildExperienceBonusPlan(_guildBonusRevision,
                integral >= uint.MaxValue ? uint.MaxValue : (uint)integral, exact - integral);
        }

        // Preview must be consumed under run.SyncRoot only after the accepted
        // monster grant. Failed attempts leave both revision and carry intact.
        internal bool TryCommitGuildBonusRate(DungeonGuildExperienceBonusPlan plan)
        {
            if (plan.Revision != _guildBonusRevision)
                return false;
            long revision = checked(_guildBonusRevision + 1);
            _guildBonusCarry = plan.CarryAfter;
            _guildBonusRevision = revision;
            return true;
        }

        // Burn is an additive component of the already planned award. Record
        // only the amount that fits in RawGain so the UI cannot claim a bonus
        // that was discarded by the UInt32 saturation boundary.
        internal static uint CalculateFatigueBurnBonus(
            uint baseExperience,
            uint totalWithoutBurnExperience,
            int percent)
        {
            if (baseExperience == 0 || percent <= 0)
                return 0;

            var requested = (ulong)baseExperience * (ulong)percent / 100;
            var available = (ulong)uint.MaxValue - totalWithoutBurnExperience;
            return (uint)Math.Min(requested, available);
        }

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }
}
