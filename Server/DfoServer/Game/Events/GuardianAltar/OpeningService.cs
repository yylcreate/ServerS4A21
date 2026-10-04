using System;
using System.Globalization;
using System.Text.RegularExpressions;

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

        private const string TimestampPattern =
            @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z";
        private const long MaximumWindowTicks =
            (long)int.MaxValue * TimeSpan.TicksPerSecond;

        private readonly object _sync = new object();
        private DateTimeOffset? _startsAtUtc;
        private DateTimeOffset? _endsAtUtc;
        private long _revision;

        internal long Revision
        {
            get
            {
                lock (_sync)
                    return _revision;
            }
        }

        internal long SetWindow(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc)
        {
            startsAtUtc = startsAtUtc.ToUniversalTime();
            endsAtUtc = endsAtUtc.ToUniversalTime();
            if (!TryValidateWindow(startsAtUtc, endsAtUtc, out var error))
                throw new ArgumentOutOfRangeException(nameof(endsAtUtc), error);

            lock (_sync)
            {
                if (_startsAtUtc == startsAtUtc && _endsAtUtc == endsAtUtc)
                    return _revision;

                var revision = checked(_revision + 1);
                _startsAtUtc = startsAtUtc;
                _endsAtUtc = endsAtUtc;
                _revision = revision;
                return _revision;
            }
        }

        internal long Close()
        {
            lock (_sync)
            {
                if (!_startsAtUtc.HasValue && !_endsAtUtc.HasValue)
                    return _revision;

                var revision = checked(_revision + 1);
                _startsAtUtc = null;
                _endsAtUtc = null;
                _revision = revision;
                return _revision;
            }
        }

        internal GuardianAltarOpeningSnapshot GetSnapshot(
            DateTimeOffset utcNow)
        {
            utcNow = utcNow.ToUniversalTime();
            lock (_sync)
            {
                if (!_startsAtUtc.HasValue || !_endsAtUtc.HasValue)
                    return ClosedSnapshot(null);

                if (utcNow < _startsAtUtc.Value)
                    return ClosedSnapshot(_startsAtUtc.Value);

                if (utcNow >= _endsAtUtc.Value)
                    return ClosedSnapshot(null);

                var remainingTicks = (_endsAtUtc.Value - utcNow).Ticks;
                var remainingSeconds = (int)((remainingTicks
                    + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
                return new GuardianAltarOpeningSnapshot(
                    _revision,
                    true,
                    remainingSeconds,
                    _endsAtUtc.Value);
            }
        }

        internal bool TryConfigureEnvironment(out string error)
        {
            if (!TryParseConfiguration(
                    Environment.GetEnvironmentVariable(StartEnvironmentVariable),
                    Environment.GetEnvironmentVariable(EndEnvironmentVariable),
                    out var startsAtUtc,
                    out var endsAtUtc,
                    out error))
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

        internal static bool TryParseConfiguration(
            string start,
            string end,
            out DateTimeOffset? parsedStart,
            out DateTimeOffset? parsedEnd,
            out string error)
        {
            parsedStart = null;
            parsedEnd = null;
            error = string.Empty;
            var hasStart = !string.IsNullOrWhiteSpace(start);
            var hasEnd = !string.IsNullOrWhiteSpace(end);
            if (!hasStart && !hasEnd)
                return true;

            if (!hasStart || !hasEnd)
            {
                error = StartEnvironmentVariable + " and "
                    + EndEnvironmentVariable + " must be configured together.";
                return false;
            }

            if (!TryParseTimestamp(start.Trim(), out var startsAtUtc)
                || !TryParseTimestamp(end.Trim(), out var endsAtUtc))
            {
                error = "Guardian Altar entry timestamps must use ISO-8601 "
                    + "with seconds and an explicit timezone, for example "
                    + "2026-10-02T10:00:00Z or 2026-10-02T18:00:00+08:00.";
                return false;
            }

            if (!TryValidateWindow(startsAtUtc, endsAtUtc, out error))
                return false;

            parsedStart = startsAtUtc;
            parsedEnd = endsAtUtc;
            return true;
        }

        private GuardianAltarOpeningSnapshot ClosedSnapshot(
            DateTimeOffset? nextTransitionUtc)
        {
            return new GuardianAltarOpeningSnapshot(
                _revision,
                false,
                0,
                nextTransitionUtc);
        }

        private static bool TryParseTimestamp(
            string value,
            out DateTimeOffset utc)
        {
            utc = default;
            if (!Regex.IsMatch(value, TimestampPattern, RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return false;
            }

            utc = parsed.ToUniversalTime();
            return true;
        }

        private static bool TryValidateWindow(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            out string error)
        {
            if (endsAtUtc <= startsAtUtc)
            {
                error = "Guardian Altar entry end must be later than its start.";
                return false;
            }

            if ((endsAtUtc - startsAtUtc).Ticks > MaximumWindowTicks)
            {
                error = "Guardian Altar entry window exceeds the Int32 seconds budget.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
