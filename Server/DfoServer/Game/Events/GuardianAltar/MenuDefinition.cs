using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using DfoServer.GameWorld;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed class GuardianAltarMenuDefinition
    {
        // A21's manager current key and the verified legacy initializer use 1.
        // It must exist in the current shop; this is not a character ID.
        internal const int DefaultRidableId = 1;
        private readonly HashSet<int> _knownDungeonIds;
        private readonly HashSet<(int Type, int Id, short Level, short Price)> _knownPurchases;
        private readonly HashSet<int> _knownAchievementIds;
        private readonly IReadOnlyList<GuardianAltarShopEntry> _shopEntries;
        private readonly Func<int, GuardianAltarDungeonFile> _dungeonLoader;
        private readonly Func<int, MapFile> _mapLoader;
        private readonly GuardianAltarEnergyConfigFile _energy;
        private readonly GuardianAltarSummonDataFile _summonData;
        private readonly IReadOnlyList<GuardianAltarStageDefinition> _orderedStages;
        private readonly IReadOnlyList<IReadOnlyList<GuardianAltarStageDefinition>> _survivalAreas;
        private readonly Func<int, GuardianAltarClearRewardFile> _clearRewardLoader;
        private readonly GuardianAltarAchievementRewardFile _achievementRewards;

        internal GuardianAltarMenuDefinition(int dailyRefillTicket, int ridableId,
            int firstDungeonId, IEnumerable<GuardianAltarPurchase> initialPurchases,
            IEnumerable<GuardianAltarSlot> initialSlots,
            IEnumerable<int> knownDungeonIds = null,
            IEnumerable<GuardianAltarPurchase> knownPurchases = null,
            IEnumerable<int> knownAchievementIds = null,
            IEnumerable<GuardianAltarShopEntry> shopEntries = null,
            Func<int, GuardianAltarDungeonFile> dungeonLoader = null,
            Func<int, MapFile> mapLoader = null,
            GuardianAltarEnergyConfigFile energy = null,
            GuardianAltarSummonDataFile summonData = null,
            IEnumerable<GuardianAltarStageDefinition> orderedStages = null,
            Func<int, GuardianAltarClearRewardFile> clearRewardLoader = null,
            GuardianAltarAchievementRewardFile achievementRewards = null)
        {
            if (dailyRefillTicket < 0 || ridableId <= 0 || firstDungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(dailyRefillTicket));
            DailyRefillTicket = dailyRefillTicket;
            RidableId = ridableId;
            FirstDungeonId = firstDungeonId;
            InitialPurchases = Array.AsReadOnly(initialPurchases.ToArray());
            InitialSlots = Array.AsReadOnly(initialSlots.ToArray());
            _knownDungeonIds = new HashSet<int>(knownDungeonIds ?? new[] { firstDungeonId });
            _knownPurchases = new HashSet<(int, int, short, short)>(
                (knownPurchases ?? InitialPurchases).Select(x => (x.ShopType, x.ResourceId, x.Level, x.Price)));
            _knownAchievementIds = new HashSet<int>(knownAchievementIds ?? Array.Empty<int>());
            _shopEntries = Array.AsReadOnly((shopEntries ?? Array.Empty<GuardianAltarShopEntry>()).ToArray());
            _dungeonLoader = dungeonLoader;
            _mapLoader = mapLoader;
            _energy = energy;
            _summonData = summonData;
            var stages = (orderedStages ?? Array.Empty<GuardianAltarStageDefinition>()).ToArray();
            _orderedStages = Array.AsReadOnly(stages
                .Where(x => !x.IsSurvival).OrderBy(x => x.AreaId).ThenBy(x => x.DistrictId)
                .ThenBy(x => x.StageOrder).ToArray());
            _survivalAreas = Array.AsReadOnly(stages.GroupBy(x => x.AreaId).OrderBy(x => x.Key)
                .Where(area => area.Any(x => x.IsSurvival))
                .Select(area => (IReadOnlyList<GuardianAltarStageDefinition>)Array.AsReadOnly(area.ToArray())).ToArray());
            if (_survivalAreas.Any(area => !area.Any(x => !x.IsSurvival)
                    || area.Any(x => !_knownDungeonIds.Contains(x.DungeonId))))
                throw new InvalidDataException("Guardian survival area lacks its registered ordinary stage prerequisites.");
            _clearRewardLoader = clearRewardLoader;
            if (achievementRewards != null && (!achievementRewards.IsValid
                || achievementRewards.Rewards.Any(x => !_knownAchievementIds.Contains(x.Type)
                    || x.ClearCount > _orderedStages.Count)))
                throw new InvalidDataException("Guardian achievement rewards do not match the current stage catalog.");
            _achievementRewards = achievementRewards;
            ValidateProgress(new GuardianAltarProgressSnapshot(1,
                new GuardianAltarHeader(ridableId, dailyRefillTicket, 0, 0, 0, 0, 0, 0, 0, 0),
                new[] { new GuardianAltarStage(firstDungeonId, -1) },
                InitialPurchases, InitialSlots, Array.Empty<GuardianAltarAchievement>()));
        }

        internal int DailyRefillTicket { get; }
        internal int RidableId { get; }
        internal int FirstDungeonId { get; }
        internal IReadOnlyList<GuardianAltarPurchase> InitialPurchases { get; }
        internal IReadOnlyList<GuardianAltarSlot> InitialSlots { get; }
        internal IReadOnlyList<GuardianAltarShopEntry> ShopEntries => _shopEntries;
        internal IReadOnlyList<GuardianAltarAchievementReward> AchievementRewards
            => _achievementRewards?.Rewards ?? Array.Empty<GuardianAltarAchievementReward>();

        internal bool ContainsDungeon(int dungeonId) => _knownDungeonIds.Contains(dungeonId);

        internal IReadOnlyList<int> GetEligibleSurvivalDungeonIds(GuardianAltarProgressSnapshot progress)
        {
            ValidateProgress(progress);
            var cleared = progress.Stages.ToDictionary(x => x.DungeonId, x => x.Progress);
            if (_orderedStages.Any(stage => cleared.TryGetValue(stage.DungeonId, out var difficulty) && difficulty > 2))
                throw new InvalidOperationException("Guardian ordinary difficulty progress is invalid.");
            // Current A21 text requires every ordinary stage at easy or above.
            // Survival is area-level PVF data; a missing/unplayed stage cannot
            // count, and an already present survival row is not a prerequisite.
            return Array.AsReadOnly(_survivalAreas
                .Where(area => area.Where(x => !x.IsSurvival).All(stage =>
                    cleared.TryGetValue(stage.DungeonId, out var difficulty) && difficulty >= 0))
                .SelectMany(area => area.Where(x => x.IsSurvival).OrderBy(x => x.StageOrder))
                .Select(stage => stage.DungeonId).ToArray());
        }

        internal GuardianAltarAchievementReward GetAchievementReward(int type)
            => AchievementRewards.SingleOrDefault(x => x.Type == type);

        internal bool IsAchievementEligible(GuardianAltarProgressSnapshot progress,
            GuardianAltarAchievementReward reward)
        {
            ValidateProgress(progress);
            if (reward == null || !ReferenceEquals(GetAchievementReward(reward.Type), reward))
                return false;
            if (progress.Stages.Any(x => _orderedStages.Any(stage => stage.DungeonId == x.DungeonId)
                && x.Progress > 2))
                throw new InvalidOperationException("Guardian ordinary difficulty progress is invalid.");
            // Current A21 149E9C0 counts distinct ordinary stage rows whose
            // highest cleared difficulty is >= the resource threshold. It
            // does not count repeated clears or the separate survival vector.
            return progress.Stages.Count(x => x.Progress >= reward.Difficulty
                && _orderedStages.Any(stage => stage.DungeonId == x.DungeonId)) >= reward.ClearCount;
        }

        internal bool TryCreatePurchasePlan(GuardianAltarProgressSnapshot progress,
            GuardianAltarPurchaseRequest request, out GuardianAltarPurchasePlan plan,
            out string rejectionReason)
        {
            plan = null;
            rejectionReason = null;
            ValidateProgress(progress);
            if (request.RidableId != RidableId || request.ShopType < 0 || request.ShopType > 2
                || request.ResourceId <= 0 || request.StartLevel <= 0
                || request.TargetLevel < request.StartLevel)
            {
                rejectionReason = "invalid resource or target";
                return false;
            }
            var previous = progress.Purchases.SingleOrDefault(x => x.ShopType == request.ShopType
                && x.ResourceId == request.ResourceId);
            var shop = _shopEntries.SingleOrDefault(x => x.ShopType == request.ShopType
                && x.ResourceId == request.ResourceId);
            if (previous == null || shop == null)
            {
                rejectionReason = "resource is locked or absent from catalog";
                return false;
            }
            if (shop.Levels.Count(x => x.Level == previous.Level && x.Price == previous.Price) != 1)
            {
                rejectionReason = "current purchase row is ambiguous";
                return false;
            }
            var replay = request.TargetLevel == previous.Level;
            if (!replay && (request.StartLevel != (int)previous.Level + 1
                || request.TargetLevel < previous.Level))
            {
                rejectionReason = "purchase must begin at the next owned level";
                return false;
            }
            // PvfLib preserves repeated higher-level source rows. A duplicate
            // cannot authorize picking the first row or guessing an index.
            var rows = shop.Levels.Where(x => x.Level >= request.StartLevel
                && x.Level <= request.TargetLevel).ToArray();
            if (rows.Length != (int)request.TargetLevel - request.StartLevel + 1
                || rows.Select(x => x.Level).Distinct().Count() != rows.Length)
            {
                rejectionReason = "purchase interval contains missing or ambiguous levels";
                return false;
            }
            if (replay)
            {
                plan = new GuardianAltarPurchasePlan(previous, previous, 0);
                return true;
            }
            var target = rows.Single(x => x.Level == request.TargetLevel);
            var cost = rows.Aggregate(0, (sum, row) => checked(sum + row.Price));
            plan = new GuardianAltarPurchasePlan(previous,
                new GuardianAltarPurchase(request.ShopType, request.ResourceId,
                    target.Level, target.Price), cost);
            return true;
        }

        internal bool TryCreateSlotPlan(GuardianAltarProgressSnapshot progress,
            GuardianAltarSlotRequest request, out GuardianAltarSlotPlan plan, out string rejectionReason)
        {
            plan = null;
            rejectionReason = null;
            ValidateProgress(progress);
            if (request.RidableId != RidableId || request.SlotType < 1 || request.SlotType > 2
                || request.SlotIndex > 5 || request.ResourceId <= 0)
            {
                rejectionReason = "invalid slot or resource";
                return false;
            }
            var slot = progress.Slots.Single(x => x.SlotType == request.SlotType
                && x.SlotIndex == request.SlotIndex);
            var learned = progress.Purchases.SingleOrDefault(x => x.ShopType == request.SlotType - 1
                && x.ResourceId == request.ResourceId);
            var shop = _shopEntries.SingleOrDefault(x => x.ShopType == request.SlotType - 1
                && x.ResourceId == request.ResourceId);
            if (slot.Locked != 0 || learned == null || learned.Level <= 0
                || shop == null || !shop.IsSettableSlot
                || shop.Levels.Count(x => x.Level == learned.Level && x.Price == learned.Price) != 1)
            {
                rejectionReason = "slot is locked or resource cannot be deployed";
                return false;
            }
            var otherDeployment = progress.Slots.Any(x => x.SlotType == request.SlotType
                && x.SlotIndex != request.SlotIndex && x.ResourceId == request.ResourceId);
            // The menu sender chooses an empty, unlocked slot when equipping;
            // an already deployed resource generates an explicit unequip.
            // Do not overwrite another resource or migrate a second slot.
            if (otherDeployment || (slot.ResourceId != 0 && slot.ResourceId != request.ResourceId))
            {
                rejectionReason = "resource no longer belongs to the requested slot";
                return false;
            }
            plan = new GuardianAltarSlotPlan(slot,
                slot with { ResourceId = request.Equip ? request.ResourceId : 0 });
            return true;
        }

        internal bool TryCreateExchangePlan(GuardianAltarProgressSnapshot progress,
            GuardianAltarExchangeRequest request, out GuardianAltarExchangePlan plan, out string rejectionReason)
        {
            plan = null;
            rejectionReason = null;
            ValidateProgress(progress);
            var first = request.First;
            var second = request.Second;
            if (request.RidableId != RidableId || first.SlotType < 1 || first.SlotType > 2
                || first.SlotType != second.SlotType || first.SlotIndex > 5 || second.SlotIndex > 5
                || first.SlotIndex == second.SlotIndex || first.ResourceId < 0 || second.ResourceId < 0
                || first.ResourceId == second.ResourceId)
            {
                rejectionReason = "invalid exchange endpoints";
                return false;
            }
            var firstSlot = progress.Slots.Single(x => x.SlotType == first.SlotType && x.SlotIndex == first.SlotIndex);
            var secondSlot = progress.Slots.Single(x => x.SlotType == second.SlotType && x.SlotIndex == second.SlotIndex);
            bool ResourceAllowed(int resource)
            {
                if (resource == 0) return true;
                var learned = progress.Purchases.SingleOrDefault(x => x.ShopType == first.SlotType - 1
                    && x.ResourceId == resource);
                var shop = _shopEntries.SingleOrDefault(x => x.ShopType == first.SlotType - 1
                    && x.ResourceId == resource);
                return learned?.Level > 0 && shop?.IsSettableSlot == true
                    && shop.Levels.Count(x => x.Level == learned.Level && x.Price == learned.Price) == 1;
            }
            if (firstSlot.Locked != 0 || secondSlot.Locked != 0
                || !ResourceAllowed(first.ResourceId) || !ResourceAllowed(second.ResourceId))
            {
                rejectionReason = "exchange slot is locked or resource cannot be deployed";
                return false;
            }
            var beforeMatches = firstSlot.ResourceId == first.ResourceId && secondSlot.ResourceId == second.ResourceId;
            var afterMatches = firstSlot.ResourceId == second.ResourceId && secondSlot.ResourceId == first.ResourceId;
            if (!beforeMatches && !afterMatches)
            {
                rejectionReason = "exchange resources no longer match either authoritative endpoint state";
                return false;
            }
            // The request has no operation nonce. An exact post-state is a retry,
            // never a reason to reverse a previously committed exchange.
            plan = new GuardianAltarExchangePlan(
                new GuardianAltarSlotPlan(firstSlot, firstSlot with { ResourceId = second.ResourceId }),
                new GuardianAltarSlotPlan(secondSlot, secondSlot with { ResourceId = first.ResourceId }));
            return true;
        }

        internal GuardianAltarDungeonStartPlan CreateStartPlan(int dungeonId, byte difficulty,
            GuardianAltarProgressSnapshot progress)
        {
            ValidateProgress(progress);
            if (!ContainsDungeon(dungeonId) || difficulty > 2
                || !progress.Stages.Any(x => x.DungeonId == dungeonId))
                throw new InvalidOperationException("Guardian stage is not unlocked or difficulty is invalid.");
            if (difficulty > progress.Stages.Single(x => x.DungeonId == dungeonId).Progress + 1)
                throw new InvalidOperationException("Guardian difficulties must be cleared in sequence.");
            if ((long)progress.Header.FreeTickets + progress.Header.SecondaryTickets <= 0)
                throw new InvalidOperationException("Guardian stage requires an available ticket.");
            if (_dungeonLoader == null || _mapLoader == null)
                throw new InvalidOperationException("Guardian combat resource definitions are unavailable.");
            if (_energy == null || !_energy.IsValid)
                throw new InvalidDataException("Guardian energy resource is unavailable: " + _energy?.InvalidReason);
            if (_summonData == null || !_summonData.IsValid)
                throw new InvalidDataException("Guardian summon resource is unavailable: " + _summonData?.InvalidReason);
            var dungeon = _dungeonLoader(dungeonId);
            if (dungeon == null || !dungeon.IsValid)
                throw new InvalidDataException(dungeon?.InvalidReason ?? "Guardian DGN is missing.");
            if (dungeon.IsSurvival)
                throw new InvalidOperationException("Guardian survival combat is not enabled by the start/loading batch.");
            var difficultyName = new[] { "easy", "medium", "hard" }[difficulty];
            var selected = dungeon.Maps.Single(x => x.DifficultyName == difficultyName);
            var map = _mapLoader(selected.MapId)
                ?? throw new InvalidDataException("Guardian MAP is missing.");
            var guardianMap = GuardianAltarMapFile.Parse(map);
            if (!guardianMap.IsValid || map.MonsterCount != map.Monsters.Count
                || map.MonsterConditionMonsters.Count != 0 || map.ConditionalSummonMonsters.Count != 0)
                throw new InvalidDataException("Guardian MAP is invalid or has unsupported conditional centers: "
                    + guardianMap.InvalidReason);
            var summaries = new List<GuardianAltarPhaseSummary>();
            var startsAt = 0;
            foreach (var phase in guardianMap.Phases)
            {
                var phaseType = phase.Type switch { "normal" => 0, "named" => 1, "boss" => 2,
                    _ => throw new InvalidDataException("Unknown Guardian phase type.") };
                summaries.Add(new GuardianAltarPhaseSummary(summaries.Count, startsAt, phaseType));
                startsAt = checked(startsAt + phase.DurationMilliseconds);
            }
            var centers = new List<GuardianAltarCenterSpawn>();
            var learnedCenters = new HashSet<int>();
            foreach (var monster in map.Monsters)
            {
                if (!monster.MonsterId.HasValue || monster.NpcId.HasValue)
                    throw new InvalidDataException("Guardian center MAP contains an unsupported NPC record.");
                var purchased = progress.Purchases.SingleOrDefault(x => x.ShopType == 2
                    && x.ResourceId == monster.MonsterId.Value);
                if (purchased == null || purchased.Level == 0)
                    continue;
                var shop = _shopEntries.SingleOrDefault(x => x.ShopType == 2
                    && x.ResourceId == purchased.ResourceId)
                    ?? throw new InvalidDataException("Guardian learned tower is not in the current shop.");
                var rows = shop.Levels.Where(x => x.Level == purchased.Level && x.Price == purchased.Price).ToArray();
                if (rows.Length != 1 || rows[0].Index <= 0 || rows[0].Index > ushort.MaxValue
                    || purchased.Level > byte.MaxValue || (int)monster.Type > 3)
                    throw new InvalidDataException("Guardian center upgrade cannot be represented by the current protocol.");
                centers.Add(new GuardianAltarCenterSpawn((ushort)rows[0].Index,
                    (byte)purchased.Level, (byte)monster.Type,
                    guardianMap.CenterIndices.Contains(monster.MonsterId.Value)));
                learnedCenters.Add(purchased.ResourceId);
            }
            if (guardianMap.CenterIndices.Any(x => !learnedCenters.Contains(x)))
                throw new InvalidDataException("Guardian MAP has no learned entry for a required center.");
            if (guardianMap.SummonStartArea == null
                || guardianMap.Phases.SelectMany(x => x.ActionSelections)
                    .Any(x => !_summonData.Actions.Any(a => a.Id == x.ActionId)))
                throw new InvalidDataException("Guardian combat MAP or actor references are invalid.");
            var units = new List<GuardianAltarUnitDefinition>();
            foreach (var slot in progress.Slots.Where(x => x.SlotType == 1 && x.Locked == 0 && x.ResourceId > 0))
            {
                var learned = progress.Purchases.Single(x => x.ShopType == 0 && x.ResourceId == slot.ResourceId);
                var shop = _shopEntries.Single(x => x.ShopType == 0 && x.ResourceId == slot.ResourceId);
                var row = shop.Levels.Single(x => x.Level == learned.Level && x.Price == learned.Price);
                var item = StackableItemProvider.Load(row.Index);
                var cooldownGroup = StackableItemProvider.ResolveCooltimeGroup(row.Index);
                if (learned.Level <= 0 || !shop.IsSettableSlot
                    || item == null
                    || !item.AdvanceAltarNeedGage.HasValue || item.CoolTime <= 0
                    || cooldownGroup <= 0)
                    throw new InvalidDataException("Guardian slotted unit has no valid summon item definition.");
                units.Add(new GuardianAltarUnitDefinition(slot.SlotIndex, row.Index,
                    checked(item.AdvanceAltarNeedGage.Value * _energy.Scale),
                    item.CoolTime, cooldownGroup.ToString(CultureInfo.InvariantCulture)));
            }
            GuardianAltarClearCommitPlan clearPlan = null;
            if (_clearRewardLoader != null)
            {
                var reward = _clearRewardLoader(dungeonId);
                if (reward == null || !reward.IsValid)
                    throw new InvalidDataException("Guardian clear rewards are unavailable: " + reward?.InvalidReason);
                clearPlan = new GuardianAltarClearCommitPlan(CreateClearProgressPlan(dungeonId, difficulty),
                    reward.Difficulties.Single(x => x.DifficultyName == difficultyName).Items);
            }
            return new GuardianAltarDungeonStartPlan(dungeonId, difficulty, selected.MapId,
                selected.MapBasisLevel, guardianMap.TotalDurationMilliseconds, summaries, centers, progress,
                _energy, guardianMap, _summonData, units, clearPlan);
        }

        // The current ordinary catalog owns stage order. Neither coordinates,
        // numeric dungeon adjacency nor a capture authorizes the next stage.
        internal GuardianAltarClearProgressPlan CreateClearProgressPlan(int dungeonId, byte difficulty)
        {
            var index = _orderedStages.ToList().FindIndex(x => x.DungeonId == dungeonId);
            if (index < 0 || difficulty > 2)
                throw new InvalidOperationException("Guardian ordinary clear has no catalog stage.");
            var next = index + 1 < _orderedStages.Count
                ? new[] { _orderedStages[index + 1].DungeonId } : Array.Empty<int>();
            var purchases = _shopEntries.Where(x => x.UnlockStageIndex == dungeonId).Select(x =>
            {
                var level = x.Levels.SingleOrDefault(row => row.Level == 0)
                    ?? x.Levels.SingleOrDefault(row => row.Level == 1)
                    ?? throw new InvalidDataException("Guardian unlock purchase has no initial level.");
                return new GuardianAltarPurchase(x.ShopType, x.ResourceId, level.Level, level.Price);
            });
            // Verified StageDifficulty order easy/medium/hard and kRewardStar.
            // Each new difficulty grants its own stars; replays grant none.
            return new GuardianAltarClearProgressPlan(RidableId, dungeonId, difficulty,
                new[] { 3, 2, 1 }[difficulty], next, purchases);
        }

        internal void ValidateProgress(GuardianAltarProgressSnapshot snapshot)
        {
            GuardianAltarProgressValidator.Validate(snapshot);
            if (snapshot.Header.RidableId != RidableId
                || snapshot.Stages.Any(x => !_knownDungeonIds.Contains(x.DungeonId))
                || snapshot.Purchases.Any(x => !_knownPurchases.Contains((x.ShopType, x.ResourceId, x.Level, x.Price)))
                || snapshot.Achievements.Any(x => !_knownAchievementIds.Contains(x.AchievementId)))
                throw new InvalidOperationException("Guardian progress does not match the current PVF catalog.");

            var deployed = snapshot.Slots.Where(x => x.ResourceId > 0).ToArray();
            if (deployed.GroupBy(x => (x.SlotType, x.ResourceId)).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Guardian resource is deployed in more than one slot.");
            foreach (var slot in deployed)
            {
                var shop = _shopEntries.SingleOrDefault(x => x.ShopType == slot.SlotType - 1
                    && x.ResourceId == slot.ResourceId);
                var learned = snapshot.Purchases.Single(x => x.ShopType == slot.SlotType - 1
                    && x.ResourceId == slot.ResourceId);
                // Validate persisted deployments on every owner read, not only
                // the current SET_SLOT request. Passive or changed catalog
                // entries must never reach menu or frozen combat projections.
                if (shop == null || !shop.IsSettableSlot
                    || shop.Levels.Count(x => x.Level == learned.Level && x.Price == learned.Price) != 1)
                    throw new InvalidOperationException("Guardian deployed resource is not valid in the current PVF catalog.");
            }
        }

        internal static GuardianAltarMenuDefinition Load()
        {
            var config = GuardianAltarConfigFile.Parse(
                PvfArchiveAccessor.ReadText("etc/advancealtar/advancealtarconfig.etc"),
                PvfArchiveAccessor.ReadText("etc/advancealtar/advancealtarshop.etc"));
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var entry = list.GetById(DefaultRidableId)
                ?? throw new InvalidDataException("Guardian ridable has no registered stage map.");
            var stages = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText(
                "stagemap/" + entry.FilePath));
            var achievements = GuardianAltarAchievementRewardFile.Parse(
                PvfArchiveAccessor.ReadText("etc/advancealtar/advancealtarachievementreward.etc"),
                PvfArchiveAccessor.ReadText("etc/hardcodetexttag_chn.etc"));
            if (!achievements.IsValid)
                throw new InvalidDataException(achievements.InvalidReason);
            return Create(config, stages, id =>
                DungeonCatalog.LoadDungeonList().GetById(id) != null
                && !string.IsNullOrWhiteSpace(DungeonCatalog.GetDungeonFile(id)?.AdvanceAltarType),
                achievements.Rewards.Select(x => x.Type),
                id => GuardianAltarDungeonFile.Parse(DungeonCatalog.GetDungeonFile(id)),
                LoadRegisteredMap,
                GuardianAltarSummonDataFile.Parse(PvfArchiveAccessor.ReadText(
                    "etc/advancealtar/advancealtarsummondata.etc")),
                id => GuardianAltarClearRewardFile.Parse(DungeonCatalog.GetDungeonFile(id)), achievements);
        }

        internal static MapFile LoadRegisteredMap(int mapId)
        {
            if (DungeonMapCatalog.LoadMapList().GetById(mapId) == null)
                throw new InvalidDataException("Guardian MAP is absent from the current registered MAP list.");
            return DungeonMapCatalog.GetMapFile(mapId);
        }

        internal static GuardianAltarMenuDefinition Create(GuardianAltarConfigFile config,
            GuardianAltarStageMapFile stageMap, Func<int, bool> isRegisteredGuardianDungeon,
            IEnumerable<int> knownAchievementIds = null,
            Func<int, GuardianAltarDungeonFile> dungeonLoader = null,
            Func<int, MapFile> mapLoader = null,
            GuardianAltarSummonDataFile summonData = null,
            Func<int, GuardianAltarClearRewardFile> clearRewardLoader = null,
            GuardianAltarAchievementRewardFile achievementRewards = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(stageMap);
            ArgumentNullException.ThrowIfNull(isRegisteredGuardianDungeon);
            if (!config.IsValid || !stageMap.IsValid)
                throw new InvalidDataException(config.InvalidReason + "; " + stageMap.InvalidReason);
            var ridable = config.Ridables.SingleOrDefault(x => x.RidableId == DefaultRidableId)
                ?? throw new InvalidDataException("The current shop lacks the A21 default ridable.");
            if (stageMap.Stages.Any(x => !isRegisteredGuardianDungeon(x.DungeonId)))
                throw new InvalidDataException("Guardian stage map refers to an unregistered/non-Guardian dungeon.");
            var first = stageMap.Stages.Where(x => !x.IsSurvival)
                .OrderBy(x => x.AreaId).ThenBy(x => x.DistrictId).ThenBy(x => x.StageOrder)
                .FirstOrDefault() ?? throw new InvalidDataException("Guardian stage map has no ordinary first stage.");
            var knownDungeons = stageMap.Stages.Select(x => x.DungeonId).ToHashSet();
            if (ridable.Entries.Any(x => x.UnlockStageIndex != 0 && !knownDungeons.Contains(x.UnlockStageIndex)))
                throw new InvalidDataException("Guardian shop unlock stage is absent from the registered stage map.");

            var purchases = new List<GuardianAltarPurchase>();
            var slots = new List<GuardianAltarSlot>();
            for (byte index = 0; index < 6; index++)
            {
                slots.Add(new GuardianAltarSlot(0, 1, index, 0));
                slots.Add(new GuardianAltarSlot((byte)(index < 4 ? 0 : 1), 2, index, 0));
            }
            // Legacy setDefaultBuyItem -> setUnlockBuyItem uses catalog order,
            // unlock stage 0, and level 0 if present, otherwise 1. Price alone
            // never authorizes a learned level. Only units/skills occupy slots.
            foreach (var item in ridable.Entries.Where(x => x.UnlockStageIndex == 0)
                         .OrderBy(x => x.ShopType))
            {
                var level = item.Levels.SingleOrDefault(x => x.Level == 0)
                    ?? item.Levels.SingleOrDefault(x => x.Level == 1)
                    ?? throw new InvalidDataException("An initial Guardian shop item has no level 0/1.");
                purchases.Add(new GuardianAltarPurchase(item.ShopType, item.ResourceId,
                    level.Level, level.Price));
                if (level.Level <= 0 || !item.IsSettableSlot || item.ShopType > 1)
                    continue;
                var slot = slots.Where(x => x.SlotType == item.ShopType + 1
                    && x.Locked == 0 && x.ResourceId == 0).OrderBy(x => x.SlotIndex).FirstOrDefault();
                if (slot == null)
                    throw new InvalidDataException("Initial Guardian deployment exceeds the available slots.");
                slots[slots.IndexOf(slot)] = slot with { ResourceId = item.ResourceId };
            }
            return new GuardianAltarMenuDefinition(config.DailyRefillTicket, ridable.RidableId,
                first.DungeonId, purchases, slots, knownDungeons,
                ridable.Entries.SelectMany(x => x.Levels.Select(level =>
                    new GuardianAltarPurchase(x.ShopType, x.ResourceId, level.Level, level.Price))),
                knownAchievementIds, ridable.Entries, dungeonLoader, mapLoader,
                GuardianAltarEnergyConfigFile.Parse(config), summonData, stageMap.Stages, clearRewardLoader,
                achievementRewards);
        }
    }
}
