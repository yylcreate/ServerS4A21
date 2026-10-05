using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.EpicRoad
{
    internal sealed class EpicRoadEntryService
    {
        internal const ushort EventId = 286;
        internal const int DungeonId = 6004;
        internal const int EntranceNpcId = 1125;
        internal const int DestinationNpcId = 1126;
        internal const int TargetTownId = 28;
        internal const int TargetAreaId = 0;
        internal const int GateAreaId = 35;
        internal const string EntryEnvironmentVariable = "DFO_EPIC_ROAD_ENTRY";

        private readonly GameEventRepository _repository;
        private readonly Func<EpicRoadEntryDefinition> _definitionLoader;

        internal EpicRoadEntryService(IGameDatabase database,
            Func<EpicRoadEntryDefinition> definition = null)
        {
            _repository = new GameEventRepository(database);
            _definitionLoader = definition ?? EpicRoadEntryDefinition.LoadCurrentPvf;
        }

        internal EpicRoadEntryDefinition Definition { get; private set; }
            = EpicRoadEntryDefinition.Unavailable("epic_road_not_initialized");

        internal void Initialize(string entrySetting)
        {
            var validSetting = TryParseEntrySetting(entrySetting, out var enabled);
            if (!validSetting)
            {
                enabled = false;
                FileLogger.Log("[EpicRoad] invalid entry setting; entry closed");
            }
            try
            {
                Definition = _definitionLoader()
                    ?? EpicRoadEntryDefinition.Unavailable("epic_road_definition_missing");
            }
            catch (Exception exception)
            {
                Definition = EpicRoadEntryDefinition.Unavailable(exception.Message);
            }
            if (!Definition.IsValid)
            {
                enabled = false;
                FileLogger.Log("[EpicRoad] PVF definition unavailable; entry closed: "
                    + Definition.InvalidReason);
            }

            _repository.EnsureCatalogEntry(EventId, enabled,
                "史诗之路入口已开放。", "史诗之路入口已关闭。", EventId);
        }

        internal bool IsEnabled()
        {
            if (!Definition.IsValid)
                return false;
            try
            {
                return _repository.IsEnabled(EventId);
            }
            catch (SqliteException exception)
            {
                FileLogger.Log("[EpicRoad] entry read failed; entry closed: "
                    + exception.Message);
                return false;
            }
        }

        internal bool TryValidateDungeon(int level, int partyCount,
            int townId, int areaId, byte requestedDifficulty,
            out byte difficulty, out string reason)
        {
            difficulty = 0;
            reason = null;
            if (!Definition.IsValid)
                reason = "definition_unavailable";
            else if (!IsEnabled())
                reason = "entry_closed";
            else if (townId != TargetTownId || areaId != TargetAreaId)
                reason = "not_in_epic_road_town";
            else if (level < Definition.MinimumRequiredLevel)
                reason = "level_too_low";
            else if (partyCount <= 0 || partyCount > Definition.LimitPartyCount)
                reason = "party_count_not_allowed";
            else if (requestedDifficulty > 3)
                reason = "difficulty_invalid";
            if (reason != null)
                return false;

            // The PVF designated difficulty owns this value. The caller must
            // still use existing town quest permission and entry admission.
            difficulty = Definition.Difficulty;
            return true;
        }

        internal static bool TryParseEntrySetting(string setting, out bool? enabled)
        {
            enabled = null;
            if (string.IsNullOrWhiteSpace(setting))
                return true;
            if (setting == "0" || setting == "1")
            {
                enabled = setting == "1";
                return true;
            }
            return false;
        }
    }
}
