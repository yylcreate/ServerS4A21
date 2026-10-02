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
            GuardianAltarSummonDataFile summonData = null)
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

        internal bool ContainsDungeon(int dungeonId) => _knownDungeonIds.Contains(dungeonId);

        internal GuardianAltarDungeonStartPlan CreateStartPlan(int dungeonId, byte difficulty,
            GuardianAltarProgressSnapshot progress)
        {
            ValidateProgress(progress);
            if (!ContainsDungeon(dungeonId) || difficulty > 2
                || !progress.Stages.Any(x => x.DungeonId == dungeonId))
                throw new InvalidOperationException("Guardian stage is not unlocked or difficulty is invalid.");
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
                    (byte)purchased.Level, (byte)monster.Type));
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
            return new GuardianAltarDungeonStartPlan(dungeonId, difficulty, selected.MapId,
                selected.MapBasisLevel, guardianMap.TotalDurationMilliseconds, summaries, centers, progress,
                _energy, guardianMap, _summonData, units);
        }

        internal void ValidateProgress(GuardianAltarProgressSnapshot snapshot)
        {
            GuardianAltarProgressValidator.Validate(snapshot);
            if (snapshot.Header.RidableId != RidableId
                || snapshot.Stages.Any(x => !_knownDungeonIds.Contains(x.DungeonId))
                || snapshot.Purchases.Any(x => !_knownPurchases.Contains((x.ShopType, x.ResourceId, x.Level, x.Price)))
                || snapshot.Achievements.Any(x => !_knownAchievementIds.Contains(x.AchievementId)))
                throw new InvalidOperationException("Guardian progress does not match the current PVF catalog.");
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
            var achievementIds = GuardianAltarConfigFile.ParseAchievementIds(
                PvfArchiveAccessor.ReadText("etc/advancealtar/advancealtarachievementreward.etc"));
            return Create(config, stages, id =>
                DungeonCatalog.LoadDungeonList().GetById(id) != null
                && !string.IsNullOrWhiteSpace(DungeonCatalog.GetDungeonFile(id)?.AdvanceAltarType),
                achievementIds,
                id => GuardianAltarDungeonFile.Parse(DungeonCatalog.GetDungeonFile(id)),
                LoadRegisteredMap,
                GuardianAltarSummonDataFile.Parse(PvfArchiveAccessor.ReadText(
                    "etc/advancealtar/advancealtarsummondata.etc")));
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
            GuardianAltarSummonDataFile summonData = null)
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
                GuardianAltarEnergyConfigFile.Parse(config), summonData);
        }
    }
}
