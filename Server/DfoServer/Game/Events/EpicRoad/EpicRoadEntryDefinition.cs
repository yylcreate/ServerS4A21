using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Events.EpicRoad
{
    // Immutable entry facts. Runtime town/run state, costs and counters remain
    // owned by the existing town, dungeon and entry-limit services.
    internal sealed class EpicRoadEntryDefinition
    {
        internal bool IsValid { get; private init; }
        internal string InvalidReason { get; private init; } = string.Empty;
        internal int MinimumRequiredLevel { get; private init; }
        internal int LimitPartyCount { get; private init; }
        internal byte Difficulty { get; private init; }
        internal int AccountMaximumCount { get; private init; }
        internal int NeedQuestId { get; private init; }
        internal int TownMinimumLevel { get; private init; }
        internal int ExitTownRoute { get; private init; }
        internal IReadOnlyList<int> EntranceHiddenTownIds { get; private init; } = Array.AsReadOnly(Array.Empty<int>());
        internal string TargetMapPath { get; private init; } = string.Empty;
        internal short TargetNpcX { get; private init; }
        internal short TargetNpcY { get; private init; }
        internal string TargetNpcDirection { get; private init; } = string.Empty;

        internal TownPermission TargetPermission => new TownPermission
        {
            NeedLevel = TownMinimumLevel,
            NeedQuest = NeedQuestId,
        };

        private EpicRoadEntryDefinition() { }

        internal static EpicRoadEntryDefinition Unavailable(string reason)
            => new EpicRoadEntryDefinition
            {
                InvalidReason = string.IsNullOrWhiteSpace(reason)
                    ? "epic_road_definition_unavailable" : reason,
            };

        internal static EpicRoadEntryDefinition FromResources(
            NpcActivityEntryDefinition entranceNpc,
            NpcActivityEntryDefinition destinationNpc,
            TownFile town,
            MapFile destinationMap,
            DungeonFile dungeon,
            DungeonIncountConfigFile entryCounts,
            WorldMapArea worldMap)
        {
            if (entranceNpc?.IsValid != true
                || entranceNpc.EnterTownNpcId != EpicRoadEntryService.DestinationNpcId
                || entranceNpc.EventId != EpicRoadEntryService.EventId
                || entranceNpc.AccountDungeonId != EpicRoadEntryService.DungeonId)
            {
                return Unavailable("epic_road_entrance_npc_mismatch");
            }
            if (destinationNpc?.IsValid != true
                || destinationNpc.ExitTownRoute != 0
                || destinationNpc.AccountDungeonId != EpicRoadEntryService.DungeonId)
            {
                return Unavailable("epic_road_destination_npc_mismatch");
            }

            var areas = town?.Areas?.Where(area =>
                area.Id == EpicRoadEntryService.TargetAreaId).ToArray();
            if (areas == null || areas.Length != 1
                || string.IsNullOrWhiteSpace(areas[0].MapPath)
                || !string.Equals(areas[0].AreaType, "dungeon gate",
                    StringComparison.OrdinalIgnoreCase)
                || areas[0].LinkedId != EpicRoadEntryService.GateAreaId)
            {
                return Unavailable("epic_road_town_gate_mismatch");
            }
            var permission = areas[0].Permission ?? town.Permission;
            if (permission == null || permission.NeedLevel <= 0
                || permission.NeedQuest <= 0)
            {
                return Unavailable("epic_road_town_permission_missing");
            }
            var npcs = destinationMap?.Npcs?.Where(npc =>
                npc.NpcId == EpicRoadEntryService.DestinationNpcId).ToArray();
            if (npcs == null || npcs.Length != 1
                || npcs[0].X < short.MinValue || npcs[0].X > short.MaxValue
                || npcs[0].Y < short.MinValue || npcs[0].Y > short.MaxValue)
            {
                return Unavailable("epic_road_destination_placement_missing");
            }
            if (worldMap == null
                || worldMap.AreaId != EpicRoadEntryService.GateAreaId
                || worldMap.Dungeons.Count(entry =>
                    entry.DungeonId == EpicRoadEntryService.DungeonId) != 1)
            {
                return Unavailable("epic_road_worldmap_mismatch");
            }
            if (dungeon == null || dungeon.MinimumRequiredLevel <= 0
                || dungeon.LimitPartyCount != 1
                || dungeon.Mazes == null || dungeon.Mazes.Count == 0
                || dungeon.DesignateDungeonDifficulty == null
                || dungeon.DesignateDungeonDifficulty.Length != 1
                || dungeon.DesignateDungeonDifficulty[0] < 0
                || dungeon.DesignateDungeonDifficulty[0] > 3
                || dungeon.RequiredItems.Count != 0)
            {
                return Unavailable("epic_road_dungeon_definition_invalid");
            }
            if (entryCounts?.IsValid != true)
                return Unavailable("epic_road_account_count_definition_invalid");
            var counts = entryCounts.AccountEntries.Where(entry =>
                entry.DungeonId == EpicRoadEntryService.DungeonId).ToArray();
            if (counts.Length != 1 || counts[0].MaximumCount <= 0
                || counts[0].MaximumCount > byte.MaxValue)
            {
                return Unavailable("epic_road_account_count_missing");
            }

            return new EpicRoadEntryDefinition
            {
                IsValid = true,
                MinimumRequiredLevel = Math.Max(
                    dungeon.MinimumRequiredLevel, permission.NeedLevel),
                LimitPartyCount = dungeon.LimitPartyCount,
                Difficulty = (byte)dungeon.DesignateDungeonDifficulty[0],
                AccountMaximumCount = counts[0].MaximumCount,
                NeedQuestId = permission.NeedQuest,
                TownMinimumLevel = permission.NeedLevel,
                ExitTownRoute = destinationNpc.ExitTownRoute.Value,
                EntranceHiddenTownIds = Array.AsReadOnly(entranceNpc.HiddenTownIds.ToArray()),
                TargetMapPath = areas[0].MapPath,
                TargetNpcX = (short)npcs[0].X,
                TargetNpcY = (short)npcs[0].Y,
                TargetNpcDirection = npcs[0].Direction ?? string.Empty,
            };
        }

        internal static EpicRoadEntryDefinition LoadCurrentPvf()
        {
            var town = TownFile.Parse(ReadListedAsset(
                "town", EpicRoadEntryService.TargetTownId));
            var area = town.Areas.SingleOrDefault(candidate =>
                candidate.Id == EpicRoadEntryService.TargetAreaId);
            if (area == null || string.IsNullOrWhiteSpace(area.MapPath))
                return Unavailable("epic_road_destination_area_missing");

            return FromResources(
                NpcActivityEntryDefinition.Parse(ReadListedAsset(
                    "npc", EpicRoadEntryService.EntranceNpcId)),
                NpcActivityEntryDefinition.Parse(ReadListedAsset(
                    "npc", EpicRoadEntryService.DestinationNpcId)),
                town,
                MapFile.Parse(PvfArchiveAccessor.ReadText(
                    PrefixAsset("map", area.MapPath))),
                DungeonData.GetDungeonFile(EpicRoadEntryService.DungeonId),
                DungeonIncountConfigFile.Parse(PvfArchiveAccessor.ReadText(
                    "etc/dungeonetc/dungeonincountinfo.etc")),
                WorldMap.GetAreaById(EpicRoadEntryService.GateAreaId));
        }

        private static string ReadListedAsset(string category, int id)
        {
            var list = DungeonCatalog.LoadListFile(category + "/" + category + ".lst");
            var path = DungeonCatalog.ResolveFilePath(list, id, category);
            return PvfArchiveAccessor.ReadText(PrefixAsset(category, path));
        }

        private static string PrefixAsset(string category, string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            var prefix = category + "/";
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalized : prefix + normalized;
        }
    }
}
