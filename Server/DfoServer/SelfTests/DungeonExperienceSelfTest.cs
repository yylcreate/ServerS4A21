using System;
using System.Reflection;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Progression;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    // 快捷栏纹章 [exp advantage] 杀怪经验加成 + 秘药/纹章的 0x0023 槽位投影。
    // 纹章扫描用注入解析器, 不加载 PVF。
    public static class DungeonExperienceSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_EXPERIENCE selftest ===");
            var failures = 0;

            VerifyParticipantRuntimeEquipmentBonus(ref failures);
            VerifyCharmExpAdvantageScan(ref failures);
            VerifyClearRewardSlotProjection(ref failures);
            VerifyFatigueBurnAwardArithmetic(ref failures);
            VerifyFatigueBurnFrozenSnapshot(ref failures);
            VerifyFatigueBurnLedger(ref failures);
            VerifyFatigueBurnExpProjection(ref failures);
            VerifyFatigueBurnClearProjection(ref failures);
            VerifyFatigueBurnLateRankIsolation(ref failures);

            Console.WriteLine(failures == 0
                ? "DUNGEON_EXPERIENCE selftest passed"
                : $"DUNGEON_EXPERIENCE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyParticipantRuntimeEquipmentBonus(ref int failures)
        {
            var participantRuntime = new DungeonParticipantExperienceRuntime();
            participantRuntime.RecordMonster(
                2469,
                493,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false);
            participantRuntime.RecordMonster(
                4939,
                987,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false,
                equipmentBonusExperience: 60);
            var participantSnapshot = participantRuntime.Capture();
            Check(
                "participant runtime separates base, bonus, total, and type projection",
                participantSnapshot.MonsterBaseExperience == 7408
                    && participantSnapshot.MonsterGrowthContractBonusExperience == 1480
                    && participantSnapshot.MonsterEquipmentBonusExperience == 60
                    && participantSnapshot.MonsterTotalExperience == 8948
                    && participantSnapshot.ChampionBaseExperience == 7408,
                ref failures);

            // 进位零头(decimal): 逐只 floor 会在小基数下吞掉全部加成,
            // 零头带进下一只, 整局合计 ≈ 基础总额 × 百分比。
            var carryRuntime = new DungeonParticipantExperienceRuntime();
            var firstGrant = carryRuntime.ApplyEquipmentBonusRate(3, 30);
            var secondGrant = carryRuntime.ApplyEquipmentBonusRate(3, 30);
            uint carryTotal = firstGrant + secondGrant;
            for (var k = 2; k < 10; k++)
                carryTotal += carryRuntime.ApplyEquipmentBonusRate(3, 30);
            Check(
                "equipment bonus carry keeps tiny bases from flooring to zero",
                firstGrant == 0 && secondGrant == 1 && carryTotal == 9,
                ref failures);
            Check(
                "equipment bonus carry ignores zero base and zero rate",
                carryRuntime.ApplyEquipmentBonusRate(0, 30) == 0
                    && new DungeonParticipantExperienceRuntime()
                        .ApplyEquipmentBonusRate(100, 0) == 0,
                ref failures);
            Check(
                "equipment bonus saturates at uint max",
                new DungeonParticipantExperienceRuntime()
                    .ApplyEquipmentBonusRate(uint.MaxValue, 100) == uint.MaxValue,
                ref failures);
        }

        // 快捷栏区间与 CharmExpAdvantageService 一致: Main 背包槽位 3-8。
        private static void VerifyCharmExpAdvantageScan(ref int failures)
        {
            const int CharmItemId = 400360007;
            EquipmentType TypeOf(int itemId)
                => itemId == CharmItemId ? EquipmentType.Charm : EquipmentType.Unknown;
            int AdvantageOf(int itemId)
                => itemId == CharmItemId ? 30 : 0;

            var inventory = new InventoryService(
                characterId: 990882,
                accountId: 990882);
            inventory.SetItem(
                InventoryListType.Main,
                slotIndex: 5,
                ItemCore.Create(ItemCore.KindEquipment, itemId: CharmItemId));
            Check(
                "charm in a quick slot exposes its exp advantage percent",
                CharmExpAdvantageService.ScanQuickSlot(inventory, TypeOf, AdvantageOf) == 30,
                ref failures);

            var outsideQuickSlot = new InventoryService(
                characterId: 990883,
                accountId: 990883);
            outsideQuickSlot.SetItem(
                InventoryListType.Main,
                slotIndex: 12,
                ItemCore.Create(ItemCore.KindEquipment, itemId: CharmItemId));
            Check(
                "charm outside quick slots gives no bonus",
                CharmExpAdvantageService.ScanQuickSlot(outsideQuickSlot, TypeOf, AdvantageOf) == 0,
                ref failures);

            var noCharm = new InventoryService(
                characterId: 990884,
                accountId: 990884);
            noCharm.SetItem(
                InventoryListType.Main,
                slotIndex: 4,
                ItemCore.Create(ItemCore.KindConsumable, itemId: 1001));
            Check(
                "consumables in quick slots are ignored",
                CharmExpAdvantageService.ScanQuickSlot(noCharm, TypeOf, AdvantageOf) == 0,
                ref failures);

            Check(
                "charm exp bonus scales with the monster base (30%)",
                DungeonExperienceCalculator.FloorToUInt32(4939 * 0.30) == 1481,
                ref failures);
        }

        // 0x0023 主区块: 4u32 + 1u8 头之后每槽位一个 i32, 槽位 i 偏移 = 17 + i*4。
        private static void VerifyClearRewardSlotProjection(ref int failures)
        {
            const int MonsterGrowthBonus = 39484;
            const int MonsterEquipmentBonus = 59268;
            var body = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 78261,
                partyClearBreakdownExp: 39130,
                avatarExp: 3913,
                growthContractExp: 15652,
                monsterGrowthContractExp: MonsterGrowthBonus,
                experiencePotionExp: 7777,
                monsterEquipmentExp: MonsterEquipmentBonus);

            const int AvatarBonusOffset = 3 * sizeof(int);
            const int ExperiencePotionBonusOffset = 17 + 1 * sizeof(int);
            const int MonsterGrowthBonusOffset = 17 + 18 * sizeof(int);
            const int MonsterEquipmentBonusOffset = 17 + 20 * sizeof(int);
            Check(
                "0x0023 projects avatar bonus in slot four",
                BitConverter.ToInt32(body, AvatarBonusOffset) == 3913,
                ref failures);
            Check(
                "0x0023 projects experience potion bonus in slot two",
                BitConverter.ToInt32(body, ExperiencePotionBonusOffset) == 7777,
                ref failures);
            Check(
                "0x0023 projects monster growth contract bonus in slot 19",
                BitConverter.ToInt32(body, MonsterGrowthBonusOffset) == MonsterGrowthBonus,
                ref failures);
            Check(
                "0x0023 projects equipment(charm) bonus in slot 21",
                BitConverter.ToInt32(body, MonsterEquipmentBonusOffset) == MonsterEquipmentBonus,
                ref failures);
        }

        private static void VerifyFatigueBurnAwardArithmetic(ref int failures)
        {
            var cases = new[]
            {
                (Base: 131u, Contract: 26u, Equipment: 39u, Percent: 100,
                    Burn: 131u, Total: 327u),
                (Base: 131u, Contract: 26u, Equipment: 39u, Percent: 200,
                    Burn: 262u, Total: 458u),
                (Base: 0u, Contract: 7u, Equipment: 9u, Percent: 100,
                    Burn: 0u, Total: 16u),
                (Base: 3u, Contract: 1u, Equipment: 0u, Percent: 50,
                    Burn: 1u, Total: 5u),
                (Base: uint.MaxValue - 10, Contract: 5u, Equipment: 2u, Percent: 100,
                    Burn: 3u, Total: uint.MaxValue),
                (Base: uint.MaxValue, Contract: 0u, Equipment: 0u, Percent: 100,
                    Burn: 0u, Total: uint.MaxValue),
                (Base: 1u, Contract: 0u, Equipment: 0u, Percent: int.MaxValue,
                    Burn: 21474836u, Total: 21474837u),
                (Base: 131u, Contract: 26u, Equipment: 39u, Percent: 0,
                    Burn: 0u, Total: 196u),
                (Base: 131u, Contract: 26u, Equipment: 39u, Percent: -100,
                    Burn: 0u, Total: 196u),
            };
            foreach (var value in cases)
            {
                var snapshot = new DungeonParticipantExperienceBonusSnapshot(
                    partyMemberCount: 1,
                    partyHasEquippedAvatar: false,
                    hasEquippedCreature: false,
                    fatigueBurnExperienceBonusPercent: value.Percent);
                var total = DungeonKillApplicationService.CalculateMonsterAwardExperience(
                    value.Base, value.Contract, value.Equipment, snapshot, out var burn);
                Check(
                    $"burn award stays additive and bounded base={value.Base} rate={value.Percent}",
                    burn == value.Burn && total == value.Total,
                    ref failures);
            }

            var uncapturedTotal = DungeonKillApplicationService.CalculateMonsterAwardExperience(
                131, 26, 39, default, out var uncapturedBurn);
            var disabledTotal = DungeonKillApplicationService.CalculateMonsterAwardExperience(
                131, 26, 39, DungeonParticipantExperienceBonusSnapshot.None, out var disabledBurn);
            Check(
                "uncaptured and disabled bonus snapshots preserve the original raw award",
                uncapturedTotal == 196 && disabledTotal == 196
                    && uncapturedBurn == 0 && disabledBurn == 0,
                ref failures);
            Check(
                "shared burn arithmetic ignores nonpositive rates and respects remaining headroom",
                DungeonParticipantExperienceRuntime.CalculateFatigueBurnBonus(0, 7, 100) == 0
                    && DungeonParticipantExperienceRuntime.CalculateFatigueBurnBonus(100, 100, -1) == 0
                    && DungeonParticipantExperienceRuntime.CalculateFatigueBurnBonus(
                        uint.MaxValue, uint.MaxValue - 3, int.MaxValue) == 3,
                ref failures);
        }

        private static void VerifyFatigueBurnFrozenSnapshot(ref int failures)
        {
            var runtime = new DungeonParticipantExperienceRuntime();
            var original = new DungeonParticipantExperienceBonusSnapshot(
                partyMemberCount: 2,
                partyHasEquippedAvatar: true,
                hasEquippedCreature: true,
                fatigueBurnExperienceBonusPercent: 100,
                fatigueConsumptionMultiplier: 2);
            var captured = runtime.TryFreezeBonusSnapshot(original);
            var replacementRejected = !runtime.TryFreezeBonusSnapshot(
                DungeonParticipantExperienceBonusSnapshot.None);
            var storyCaptured = runtime.TryFreezeStoryExperienceProfile(50, 1);
            var frozen = runtime.CaptureBonusSnapshot();
            Check(
                "late story capture preserves the independently frozen participant burn policy",
                captured && replacementRejected && storyCaptured
                    && frozen.PartyMemberCount == 2 && frozen.PartyHasEquippedAvatar
                    && frozen.HasEquippedCreature && frozen.StoryExperienceBonusRatePercent == 50
                    && frozen.StoryExperienceDifficulty == 1
                    && frozen.FatigueBurnExperienceBonusPercent == 100
                    && frozen.FatigueConsumptionMultiplier == 2
                    && !runtime.TryFreezeStoryExperienceProfile(90, 2),
                ref failures);
            var unmodified = original.WithStoryExperienceProfile(50, 1);
            Check(
                "story profile copying retains burn fields without mutating its source",
                unmodified.FatigueBurnExperienceBonusPercent == 100
                    && unmodified.FatigueConsumptionMultiplier == 2
                    && original.StoryExperienceDifficulty == -1,
                ref failures);
        }

        private static void VerifyFatigueBurnLedger(ref int failures)
        {
            var runtime = new DungeonParticipantExperienceRuntime();
            runtime.RecordMonster(130, 26, false, false, false, false,
                actorSequenceId: 7, equipmentBonusExperience: 39,
                fatigueBurnBonusExperience: 130);
            runtime.RecordMonster(390, 78, false, false, false, true,
                actorSequenceId: 8, equipmentBonusExperience: 117,
                fatigueBurnBonusExperience: 390);
            var snapshot = runtime.Capture();
            Check(
                "participant ledger counts burn once while keeping object and named EXP at base",
                snapshot.MonsterBaseExperience == 520
                    && snapshot.MonsterGrowthContractBonusExperience == 104
                    && snapshot.MonsterEquipmentBonusExperience == 156
                    && snapshot.MonsterFatigueBurnBonusExperience == 520
                    && snapshot.MonsterTotalExperience == 1300
                    && runtime.MonsterTotalExperience == 1300
                    && snapshot.NamedMonsterBaseExperience == 390
                    && snapshot.BossBaseExperience == 0
                    && snapshot.ChampionBaseExperience == 0
                    && snapshot.SuperChampionBaseExperience == 0
                    && snapshot.ObjectExperienceEntries.Count == 2
                    && snapshot.ObjectExperienceEntries[0].ObjectKey == 7
                    && snapshot.ObjectExperienceEntries[0].Experience == 130
                    && snapshot.ObjectExperienceEntries[1].ObjectKey == 8
                    && snapshot.ObjectExperienceEntries[1].Experience == 390,
                ref failures);
            runtime.RecordMonster(1, 0, true, false, false, false,
                actorSequenceId: 9, fatigueBurnBonusExperience: 1);
            Check(
                "captured burn and object ledger remain immutable after later kills",
                snapshot.MonsterFatigueBurnBonusExperience == 520
                    && snapshot.MonsterTotalExperience == 1300
                    && snapshot.ObjectExperienceEntries.Count == 2
                    && runtime.MonsterFatigueBurnBonusExperience == 521
                    && runtime.MonsterTotalExperience == 1302,
                ref failures);
        }

        private static void VerifyFatigueBurnExpProjection(ref int failures)
        {
            byte[] Build(uint burn) => ExpNotificationBuilder.Build(
                level: 26,
                totalExp: 1300,
                skillPoints: default,
                honorLevel: null,
                pvpVictoryPointSnapshot: 99,
                fatigueBuffBonusExp: 101,
                seriaBlessingBonusExp: 202,
                growthContractBonusExp: 303,
                fatigueBurnBonusExp: burn,
                eliteMonsterKillBonusExp: 404,
                growthCapsuleExp: 505);
            var baseline = Build(0);
            var body = Build(520);
            Check(
                "EXP burn occupies its existing u32 field without shifting the fixed body",
                body.Length == 83 && baseline.Length == body.Length
                    && BitConverter.ToUInt32(body, 0x26) == 520
                    && SameOutsideRanges(baseline, body, 0x26, 4)
                    && BitConverter.ToUInt32(body, 0x15) == 101
                    && BitConverter.ToUInt32(body, 0x1A) == 202
                    && BitConverter.ToUInt32(body, 0x1E) == 303
                    && BitConverter.ToUInt32(body, 0x22) == 0
                    && BitConverter.ToUInt32(body, 0x2A) == 0
                    && body[ExpNotificationBuilder.VariableEntryCountOffset] == 0
                    && BitConverter.ToUInt32(body, 0x2F) == 404
                    && BitConverter.ToUInt32(body, ExpNotificationBuilder.GrowthCapsuleExpOffset) == 505
                    && BitConverter.ToUInt32(body, ExpNotificationBuilder.RemovedChannelExpOffset) == 0,
                ref failures);
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00, (ushort)NotiPacketTypeA21.EXP, body);
            Check(
                "EXP remains a complete fixed NOTI frame with the enum-owned opcode",
                packet.Length == 98 && packet[0] == 0
                    && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.EXP
                    && BitConverter.ToInt32(packet, 3) == packet.Length,
                ref failures);
        }

        private static void VerifyFatigueBurnClearProjection(ref int failures)
        {
            var entries = new[] { new DungeonObjectExperienceEntry(7, 130) };
            byte[] Build(int clearBurn, int monsterBurn)
                => DungeonNotificationBuilder.BuildClearDungeonReward(
                    clearBaseExp: 1000,
                    experiencePotionExp: 110,
                    monsterGrowthContractExp: 104,
                    monsterEquipmentExp: 156,
                    objectExperienceEntries: entries,
                    fatigueBurnExp: clearBurn,
                    monsterFatigueBurnExp: monsterBurn);
            var baseline = Build(0, 0);
            var body = Build(1000, 520);
            var clearOffset = DungeonNotificationBuilder.RewardSlotBlockOffset
                + DungeonNotificationBuilder.FatigueBurnClearBonusSlotIndex * sizeof(int);
            var monsterOffset = DungeonNotificationBuilder.RewardSlotBlockOffset
                + DungeonNotificationBuilder.MonsterFatigueBurnBonusSlotIndex * sizeof(int);
            Check(
                "clear body separates current clear burn from historical awarded kill burn",
                clearOffset == 57 && monsterOffset == 81
                    && BitConverter.ToInt32(baseline, clearOffset) == 0
                    && BitConverter.ToInt32(baseline, monsterOffset) == 0
                    && BitConverter.ToInt32(body, clearOffset) == 1000
                    && BitConverter.ToInt32(body, monsterOffset) == 520
                    && SameOutsideRanges(baseline, body, clearOffset, 4, monsterOffset, 4)
                    && BitConverter.ToUInt32(body, DungeonNotificationBuilder.ObjectExperienceCountOffset) == 1
                    && BitConverter.ToUInt32(body, DungeonNotificationBuilder.ObjectExperienceEntriesOffset) == 7
                    && BitConverter.ToUInt32(body, DungeonNotificationBuilder.ObjectExperienceEntriesOffset + 4) == 130,
                ref failures);
        }

        private static void VerifyFatigueBurnLateRankIsolation(ref int failures)
        {
            const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
            var owner = typeof(DungeonSettlementHandler);
            var rankType = owner.GetNestedType("ClearRankParts", BindingFlags.NonPublic);
            var apply = owner.GetMethod("ApplyRankFields", PrivateStatic);
            var notification = owner.GetMethod("GetSettlementNotificationFatigueBurnBonus", PrivateStatic);
            Check("late rank and EXP projection exercise the actual settlement owner",
                rankType != null && apply != null && notification != null, ref failures);
            if (rankType == null || apply == null || notification == null)
                return;

            var rank = Activator.CreateInstance(rankType,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, args: new object[] { (byte)1, 0, 1, (byte)0, 0 }, culture: null);
            var settlement = new DungeonSettlementRuntime
            {
                ClearBaseExp = 1000,
                FatigueBurnBonusExp = 1000,
                MonsterFatigueBurnBonusExp = 9999,
            };
            apply.Invoke(null, new object[] { settlement, rank, (uint)100 });
            Check(
                "late rank retains frozen clear burn and never grants historical kill burn again",
                settlement.ClearBonusExp == 1100 && settlement.ClearTotalExp == 2100
                    && settlement.FatigueBurnBonusExp == 1000
                    && settlement.MonsterFatigueBurnBonusExp == 9999,
                ref failures);
            settlement.ExperiencePotionBonusRate = ExperienceBonusEffectService.RateScale / 10;
            apply.Invoke(null, new object[] { settlement, rank, (uint)100 });
            Check(
                "potion applies to its original pre-burn base during late rank adjustment",
                settlement.ExperiencePotionBonusExp == 110
                    && settlement.ClearBonusExp == 1210 && settlement.ClearTotalExp == 2210
                    && settlement.FatigueBurnBonusExp == 1000
                    && settlement.MonsterFatigueBurnBonusExp == 9999,
                ref failures);

            uint ProjectBurn() => (uint)notification.Invoke(null, new object[] { settlement });
            Check("clear EXP with no grant projects no burn increment", ProjectBurn() == 0, ref failures);
            settlement.ExperienceGrant = new ExperienceGrantResult { RawGain = 2210 };
            Check("initial clear EXP projects only this clear burn", ProjectBurn() == 1000, ref failures);
            settlement.ScoreAdjustmentExperienceGrant = new ExperienceGrantResult { RawGain = 100 };
            Check("independent score adjustment does not replace the initial grant projection",
                ProjectBurn() == 1000, ref failures);
            settlement.ExperienceGrant = settlement.ScoreAdjustmentExperienceGrant;
            Check("late score delta EXP does not project the previously awarded burn again",
                ProjectBurn() == 0, ref failures);
        }

        private static bool SameOutsideRanges(byte[] left, byte[] right,
            int firstOffset, int firstLength, int secondOffset = -1, int secondLength = 0)
        {
            if (left.Length != right.Length)
                return false;
            for (var i = 0; i < left.Length; i++)
            {
                if ((i >= firstOffset && i < firstOffset + firstLength)
                    || (i >= secondOffset && i < secondOffset + secondLength))
                    continue;
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}
