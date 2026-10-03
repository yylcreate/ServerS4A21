using System;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.FatigueAcceleration
{
    internal enum FatigueAccelerationEntryResult
    {
        Stopped,
        InvalidRequest,
        NotInTown,
        EntryClosed,
        DefinitionUnavailable,
        EntryUnavailable,
        EffectsUnavailable,
    }

    // The catalog entry and stopped projection do not authorize a burn.
    // Active state requires the shared fatigue/experience owners, not a UI flag.
    internal sealed class FatigueAccelerationEntryService
    {
        internal const ushort EventId = 115;
        internal const string EntryEnvironmentVariable = "DFO_FATIGUE_ACCELERATION_ENTRY";
        internal const string CostPath = "event/chn_event/chn_fatiguespeedup.evt";
        internal const string DisabledDungeonPath = "etc/chn_fatigueaccelchkdungeon.etc";

        private readonly FatigueAccelerationEntryRepository _repository;
        private readonly Lazy<FatigueAccelerationConfigFile> _definition;

        internal FatigueAccelerationEntryService(IGameDatabase database,
            Func<FatigueAccelerationConfigFile> definition = null)
        {
            _repository = new FatigueAccelerationEntryRepository(database);
            _definition = new Lazy<FatigueAccelerationConfigFile>(definition ?? LoadDefinition);
        }

        internal FatigueAccelerationConfigFile Definition => _definition.Value;

        internal void Initialize(string entrySetting)
        {
            var validSetting = TryParseEntrySetting(entrySetting, out var enabled);
            if (!validSetting)
            {
                enabled = false;
                FileLogger.Log("[FatigueAcceleration] invalid entry setting; closed");
            }
            if (!Definition.IsValid)
            {
                enabled = false;
                FileLogger.Log("[FatigueAcceleration] PVF definition unavailable; entry closed: "
                    + Definition.InvalidReason);
            }
            _repository.EnsureEntry(enabled);
        }

        internal FatigueAccelerationEntryResult Evaluate(bool validRequest,
            bool enable, bool townReady)
        {
            if (!validRequest)
                return FatigueAccelerationEntryResult.InvalidRequest;
            if (!townReady)
                return FatigueAccelerationEntryResult.NotInTown;
            // The only currently implemented player state is stopped. Stopping
            // is idempotent and remains available after the catalog is closed.
            if (!enable)
                return FatigueAccelerationEntryResult.Stopped;
            if (!Definition.IsValid)
                return FatigueAccelerationEntryResult.DefinitionUnavailable;
            try
            {
                if (!_repository.IsEnabled())
                    return FatigueAccelerationEntryResult.EntryClosed;
            }
            catch (SqliteException ex)
            {
                FileLogger.Log("[FatigueAcceleration] entry read failed: " + ex.Message);
                return FatigueAccelerationEntryResult.EntryUnavailable;
            }
            return FatigueAccelerationEntryResult.EffectsUnavailable;
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

        private static FatigueAccelerationConfigFile LoadDefinition()
        {
            try
            {
                return FatigueAccelerationConfigFile.Parse(
                    PvfArchiveAccessor.ReadText(CostPath),
                    PvfArchiveAccessor.ReadText(DisabledDungeonPath));
            }
            catch (Exception ex)
            {
                FileLogger.Log("[FatigueAcceleration] PVF read failed: " + ex.Message);
                return FatigueAccelerationConfigFile.Parse(null, null);
            }
        }
    }
}
