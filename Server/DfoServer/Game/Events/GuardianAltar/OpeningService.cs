using System;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed class GuardianAltarOpeningSnapshot
    {
        internal const int ScheduleCategory = 4;
        internal const int ScheduleContentId = 10;

        internal GuardianAltarOpeningSnapshot(
            long revision,
            bool isOpen,
            int remainingSeconds,
            DateTimeOffset? nextTransitionUtc)
        {
            Revision = revision;
            IsOpen = isOpen;
            RemainingSeconds = remainingSeconds;
            NextTransitionUtc = nextTransitionUtc;
        }

        internal long Revision { get; }
        internal bool IsOpen { get; }
        internal int WireState => IsOpen ? 1 : 0;
        internal int RemainingSeconds { get; }
        internal DateTimeOffset? NextTransitionUtc { get; }
    }

    // This window controls the entry projection only. It does not authorize
    // Guardian Altar gameplay, consume tickets or own persistent rewards.
    internal sealed class GuardianAltarOpeningService
    {
        internal const string StartEnvironmentVariable =
            "DFO_GUARDIAN_ALTAR_ENTRY_START_UTC";
        internal const string EndEnvironmentVariable =
            "DFO_GUARDIAN_ALTAR_ENTRY_END_UTC";

        private readonly OpeningWindow _window = new("Guardian Altar entry");

        internal long Revision => _window.Revision;

        internal long SetWindow(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
            => _window.SetWindow(startsAtUtc, endsAtUtc);

        internal long Close() => _window.Close();

        internal GuardianAltarOpeningSnapshot GetSnapshot(DateTimeOffset utcNow)
        {
            var snapshot = _window.GetSnapshot(utcNow);
            return new GuardianAltarOpeningSnapshot(snapshot.Revision,
                snapshot.IsOpen, snapshot.RemainingSeconds,
                snapshot.NextTransitionUtc);
        }

        internal bool TryConfigureEnvironment(out string error)
        {
            if (!TryParseConfiguration(
                    Environment.GetEnvironmentVariable(StartEnvironmentVariable),
                    Environment.GetEnvironmentVariable(EndEnvironmentVariable),
                    out var startsAtUtc, out var endsAtUtc, out error))
            {
                Close();
                return false;
            }
            if (startsAtUtc.HasValue && endsAtUtc.HasValue)
                SetWindow(startsAtUtc.Value, endsAtUtc.Value);
            else
                Close();
            return true;
        }

        internal static bool TryParseConfiguration(string start, string end,
            out DateTimeOffset? parsedStart, out DateTimeOffset? parsedEnd,
            out string error)
            => OpeningWindow.TryParseConfiguration(start, end,
                StartEnvironmentVariable, EndEnvironmentVariable,
                "Guardian Altar entry", out parsedStart, out parsedEnd, out error);
    }
}
