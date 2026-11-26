using System;
using System.Collections.Generic;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Events.ScheduledDungeons
{
    internal sealed class EventDungeonOpeningSnapshot
    {
        internal EventDungeonOpeningSnapshot(int dungeonId, long revision,
            bool definitionValid, string failureReason, OpeningWindowSnapshot window)
        {
            DungeonId = dungeonId;
            Revision = revision;
            DefinitionValid = definitionValid;
            FailureReason = failureReason ?? string.Empty;
            IsOpen = definitionValid && window.IsOpen;
            RemainingSeconds = IsOpen ? window.RemainingSeconds : 0;
            NextTransitionUtc = definitionValid ? window.NextTransitionUtc : null;
            ProjectionSegment = window.ProjectionSegment;
        }

        internal int DungeonId { get; }
        internal long Revision { get; }
        internal bool DefinitionValid { get; }
        internal string FailureReason { get; }
        internal bool IsOpen { get; }
        internal int WireState => IsOpen ? 1 : 0;
        internal int RemainingSeconds { get; }
        internal DateTimeOffset? NextTransitionUtc { get; }
        internal long ProjectionSegment { get; }
    }

    // Owns only finite opening windows and resource-backed entry rules. Entry
    // items, daily counts, instances and rewards keep their existing owners.
    internal sealed class EventDungeonOpeningService
    {
        internal const int TauDungeonId = 3700;
        internal const int GoldSilverDungeonId = 4123;
        internal const int ScheduleCategory = 0;

        // The client multiplies the seconds field by 1000 in signed Int32 UI
        // arithmetic. Longer windows are projected in renewable finite segments.
        internal const int MaximumWireSeconds = int.MaxValue / 1000;
        internal const string TauStartEnvironmentVariable =
            "DFO_TAU_KINGDOM_ENTRY_START_UTC";
        internal const string TauEndEnvironmentVariable =
            "DFO_TAU_KINGDOM_ENTRY_END_UTC";
        internal const string GoldSilverStartEnvironmentVariable =
            "DFO_GOLD_SILVER_HORN_ENTRY_START_UTC";
        internal const string GoldSilverEndEnvironmentVariable =
            "DFO_GOLD_SILVER_HORN_ENTRY_END_UTC";
        internal const string ScheduleDefinitionPath = "etc/chn_schedule.etc";
        internal const string GoldSilverDefinitionPath = "etc/chn_goldsilverhorndungeon.etc";

        internal static IReadOnlyList<int> ManagedDungeonIds { get; } =
            Array.AsReadOnly(new[] { TauDungeonId, GoldSilverDungeonId });

        private readonly object _sync = new();
        private readonly OpeningWindow _tauWindow = new("Tau Kingdom entry");
        private readonly OpeningWindow _goldSilverWindow = new("Gold/silver horn entry");
        private readonly Func<int, DungeonFile> _dungeon;
        private readonly Func<GoldSilverHornDungeonConfigFile> _goldSilver;
        private readonly Func<DailyScheduleConfigFile> _schedule;
        private long _revision;

        internal EventDungeonOpeningService(Func<int, DungeonFile> dungeon = null,
            Func<GoldSilverHornDungeonConfigFile> goldSilver = null,
            Func<DailyScheduleConfigFile> schedule = null)
        {
            _dungeon = dungeon ?? DungeonCatalog.GetDungeonFile;
            var goldSilverDefinition = new Lazy<GoldSilverHornDungeonConfigFile>(() =>
                GoldSilverHornDungeonConfigFile.Parse(
                    PvfArchiveAccessor.ReadText(GoldSilverDefinitionPath)));
            var scheduleDefinition = new Lazy<DailyScheduleConfigFile>(() =>
                DailyScheduleConfigFile.Parse(
                    PvfArchiveAccessor.ReadText(ScheduleDefinitionPath)));
            _goldSilver = goldSilver ?? (() => goldSilverDefinition.Value);
            _schedule = schedule ?? (() => scheduleDefinition.Value);
        }

        internal long Revision
        {
            get { lock (_sync) return _revision; }
        }

        internal bool IsManagedDungeon(int dungeonId)
            => dungeonId == TauDungeonId || dungeonId == GoldSilverDungeonId;

        internal GoldSilverHornDungeonConfigFile GoldSilverConfiguration
        {
            get
            {
                try { return _goldSilver() ?? GoldSilverHornDungeonConfigFile.Parse(null); }
                catch (Exception exception)
                {
                    FileLogger.Log("[EventDungeonOpening] gold/silver resource unavailable: "
                        + exception.Message);
                    return GoldSilverHornDungeonConfigFile.Parse(null);
                }
            }
        }

        internal long SetWindow(int dungeonId, DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc)
        {
            lock (_sync)
            {
                var window = ResolveWindow(dungeonId);
                var before = window.Revision;
                var revision = checked(_revision + 1);
                if (window.SetWindow(startsAtUtc, endsAtUtc) != before)
                    _revision = revision;
                return _revision;
            }
        }

        internal long Close(int dungeonId)
        {
            lock (_sync)
            {
                var window = ResolveWindow(dungeonId);
                var before = window.Revision;
                var revision = checked(_revision + 1);
                if (window.Close() != before)
                    _revision = revision;
                return _revision;
            }
        }

        internal EventDungeonOpeningSnapshot GetSnapshot(int dungeonId,
            DateTimeOffset utcNow)
        {
            var valid = TryValidateDefinition(dungeonId, out _, out var reason);
            lock (_sync)
            {
                var window = IsManagedDungeon(dungeonId)
                    ? ResolveWindow(dungeonId).GetSnapshot(utcNow, MaximumWireSeconds)
                    : default;
                return new EventDungeonOpeningSnapshot(dungeonId, _revision,
                    valid, reason, window);
            }
        }

        internal bool TryValidateEntry(int dungeonId, int characterLevel,
            int participantCount, DateTimeOffset utcNow,
            out byte difficulty, out string reason)
        {
            difficulty = 0;
            reason = string.Empty;
            if (!IsManagedDungeon(dungeonId))
                return true;
            // Revalidate the current typed resource at every request, rather
            // than treating a previously published UI-open flag as permission.
            if (!TryValidateDefinition(dungeonId, out var dungeon, out reason))
                return false;
            lock (_sync)
            {
                if (!ResolveWindow(dungeonId).GetSnapshot(utcNow, MaximumWireSeconds).IsOpen)
                {
                    reason = "activity_window_closed";
                    return false;
                }
            }
            if (characterLevel < dungeon.MinimumRequiredLevel)
            {
                reason = "minimum_level_not_met";
                return false;
            }
            if (participantCount <= 0 || participantCount > dungeon.LimitPartyCount)
            {
                reason = "party_count_not_allowed";
                return false;
            }
            difficulty = checked((byte)dungeon.DesignateDungeonDifficulty[0]);
            return true;
        }

        internal bool TryConfigureEnvironment(out string error)
        {
            var tauValid = ConfigureWindow(TauDungeonId,
                TauStartEnvironmentVariable, TauEndEnvironmentVariable, out var tauError);
            var goldSilverValid = ConfigureWindow(GoldSilverDungeonId,
                GoldSilverStartEnvironmentVariable, GoldSilverEndEnvironmentVariable,
                out var goldSilverError);
            error = string.Join(" ", new[] { tauError, goldSilverError }).Trim();
            return tauValid && goldSilverValid;
        }

        private bool ConfigureWindow(int dungeonId, string startVariable,
            string endVariable, out string error)
        {
            if (!OpeningWindow.TryParseConfiguration(
                    Environment.GetEnvironmentVariable(startVariable),
                    Environment.GetEnvironmentVariable(endVariable),
                    startVariable, endVariable, "Event dungeon entry",
                    out var startsAtUtc, out var endsAtUtc, out error))
            {
                Close(dungeonId);
                return false;
            }
            if (startsAtUtc.HasValue && endsAtUtc.HasValue)
                SetWindow(dungeonId, startsAtUtc.Value, endsAtUtc.Value);
            else
                Close(dungeonId);
            return true;
        }

        private OpeningWindow ResolveWindow(int dungeonId)
            => dungeonId switch
            {
                TauDungeonId => _tauWindow,
                GoldSilverDungeonId => _goldSilverWindow,
                _ => throw new ArgumentOutOfRangeException(nameof(dungeonId)),
            };

        private bool TryValidateDefinition(int dungeonId, out DungeonFile dungeon,
            out string reason)
        {
            dungeon = null;
            reason = string.Empty;
            if (!IsManagedDungeon(dungeonId))
            {
                reason = "unmanaged_dungeon";
                return false;
            }
            try
            {
                var schedule = _schedule();
                if (schedule?.IsValid != true
                    || !schedule.TryGetDungeon(dungeonId, out _))
                {
                    reason = "schedule_descriptor_unavailable";
                    return false;
                }
                dungeon = _dungeon(dungeonId);
                if (dungeon == null || dungeon.MinimumRequiredLevel <= 0
                    || dungeon.LimitPartyCount <= 0 || dungeon.LimitPartyCount > 4
                    || dungeon.DesignateDungeonDifficulty == null
                    || dungeon.DesignateDungeonDifficulty.Length != 1
                    || dungeon.DesignateDungeonDifficulty[0] < 0
                    || dungeon.DesignateDungeonDifficulty[0] > byte.MaxValue)
                {
                    reason = "dungeon_entry_definition_invalid";
                    return false;
                }
                if (dungeonId == GoldSilverDungeonId)
                {
                    var goldSilver = _goldSilver();
                    if (goldSilver?.IsValid != true || goldSilver.WorldMapIndex <= 0
                        || goldSilver.Silver?.DungeonId != GoldSilverDungeonId
                        || goldSilver.Silver.MaxEnterCount <= 0
                        || goldSilver.Silver.MaxEnterCount > ushort.MaxValue)
                    {
                        reason = "gold_silver_definition_invalid";
                        return false;
                    }
                    if (goldSilver.Silver.DungeonEnterCost?.Count > 0)
                    {
                        // Current ETC has no cost table. Its integer pairs do
                        // not establish a debit contract for a future resource.
                        reason = "gold_silver_entry_cost_not_supported";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                dungeon = null;
                reason = "activity_resource_unavailable:" + exception.Message;
                return false;
            }
        }
    }
}
