using System;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Events.Joust;
using DfoServer.Game.Events.PcRoomTimePoint;
using DfoServer.Game.Events.TotalAttendance;
using DfoServer.Game.Events.SeriaLuck;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events
{
    internal sealed class EventManager
    {
        private readonly GameEventRepository _repository;

        internal EventManager(IGameDatabase database)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
            _repository = new GameEventRepository(Database);
            SeriaLuckUi = new SeriaLuckUiService(Database);
        }

        internal IGameDatabase Database { get; }

        internal GuardianAltarOpeningService GuardianAltarOpening { get; } = new();

        internal SeriaLuckUiService SeriaLuckUi { get; }

        internal void Initialize()
        {
            SeriaLuckUi.Initialize();
            if (!GuardianAltarOpening.TryConfigureEnvironment(out var openingError))
                FileLogger.Log("[GuardianAltarEntry] invalid entry-only window; disabled: "
                    + openingError);

            var joustConfig = JoustConfigProvider.Instance;
            joustConfig.Warmup();
            new JoustRepository(Database)
                .EnsureStaticConfigRows(joustConfig.Current);

            var pcRoomConfig = PcRoomTimePointConfigProvider.Instance;
            pcRoomConfig.Warmup();
            new PcRoomTimePointRepository(Database)
                .EnsureStaticConfigRows(pcRoomConfig.Current);

            var dailyAttendanceConfig =
                DailyAttendanceAnytimeConfigProvider.Instance;
            dailyAttendanceConfig.Warmup();
            new DailyAttendanceAnytimeRepository(Database)
                .EnsureStaticConfigRows(dailyAttendanceConfig.Current);

            var totalAttendanceConfig = TotalAttendanceConfigProvider.Instance;
            totalAttendanceConfig.Warmup();
            new TotalAttendanceRepository(Database)
                .EnsureStaticConfigRows(totalAttendanceConfig.Current);
        }

        internal GameEventInfoSnapshot LoadEventInfoSnapshot()
        {
            return _repository.LoadEventInfoSnapshot();
        }
    }
}
