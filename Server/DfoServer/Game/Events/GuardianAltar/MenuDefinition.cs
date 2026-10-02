using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.GameWorld;
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

        internal GuardianAltarMenuDefinition(int dailyRefillTicket, int ridableId,
            int firstDungeonId, IEnumerable<GuardianAltarPurchase> initialPurchases,
            IEnumerable<GuardianAltarSlot> initialSlots,
            IEnumerable<int> knownDungeonIds = null,
            IEnumerable<GuardianAltarPurchase> knownPurchases = null,
            IEnumerable<int> knownAchievementIds = null)
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
                achievementIds);
        }

        internal static GuardianAltarMenuDefinition Create(GuardianAltarConfigFile config,
            GuardianAltarStageMapFile stageMap, Func<int, bool> isRegisteredGuardianDungeon,
            IEnumerable<int> knownAchievementIds = null)
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
                    new GuardianAltarPurchase(x.ShopType, x.ResourceId, level.Level, level.Price))), knownAchievementIds);
        }
    }
}
