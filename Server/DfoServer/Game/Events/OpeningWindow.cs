using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Events
{
    internal readonly struct OpeningWindowSnapshot
    {
        internal OpeningWindowSnapshot(long revision, bool isOpen,
            int remainingSeconds, DateTimeOffset? nextTransitionUtc,
            long projectionSegment = 0)
        {
            Revision = revision;
            IsOpen = isOpen;
            RemainingSeconds = remainingSeconds;
            NextTransitionUtc = nextTransitionUtc;
            ProjectionSegment = projectionSegment;
        }

        internal long Revision { get; }
        internal bool IsOpen { get; }
        internal int RemainingSeconds { get; }
        internal DateTimeOffset? NextTransitionUtc { get; }
        internal long ProjectionSegment { get; }
    }

    // A finite absolute UTC window. Timers request a fresh projection; this
    // state, rather than the execution of a timer, decides whether entry is open.
    internal sealed class OpeningWindow
    {
        private const string TimestampPattern =
            @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z";
        private const long MaximumWindowTicks =
            (long)int.MaxValue * TimeSpan.TicksPerSecond;

        private readonly object _sync = new();
        private readonly string _name;
        private DateTimeOffset? _startsAtUtc;
        private DateTimeOffset? _endsAtUtc;
        private long _revision;

        internal OpeningWindow(string name)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
        }

        internal long Revision
        {
            get { lock (_sync) return _revision; }
        }

        internal long SetWindow(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
        {
            startsAtUtc = startsAtUtc.ToUniversalTime();
            endsAtUtc = endsAtUtc.ToUniversalTime();
            if (!TryValidateWindow(startsAtUtc, endsAtUtc, _name, out var error))
                throw new ArgumentOutOfRangeException(nameof(endsAtUtc), error);

            lock (_sync)
            {
                if (_startsAtUtc == startsAtUtc && _endsAtUtc == endsAtUtc)
                    return _revision;
                var revision = checked(_revision + 1);
                _startsAtUtc = startsAtUtc;
                _endsAtUtc = endsAtUtc;
                return _revision = revision;
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
                return _revision = revision;
            }
        }

        internal OpeningWindowSnapshot GetSnapshot(DateTimeOffset utcNow,
            int maximumProjectionSeconds = int.MaxValue)
        {
            if (maximumProjectionSeconds < 2)
                throw new ArgumentOutOfRangeException(nameof(maximumProjectionSeconds));
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
                var windowTicks = (_endsAtUtc.Value - _startsAtUtc.Value).Ticks;
                var budgetTicks = (long)maximumProjectionSeconds * TimeSpan.TicksPerSecond;
                if (windowTicks <= budgetTicks)
                    return new OpeningWindowSnapshot(_revision, true,
                        CeilingSeconds(remainingTicks), _endsAtUtc.Value);

                // Refresh long windows before the client's countdown expires.
                // Segment identity is anchored to the configured start, not
                // to when a possibly late timer happened to execute.
                var segmentTicks = (long)(maximumProjectionSeconds / 2)
                    * TimeSpan.TicksPerSecond;
                var elapsedTicks = (utcNow - _startsAtUtc.Value).Ticks;
                var segment = elapsedTicks / segmentTicks;
                var withinSegmentTicks = elapsedTicks % segmentTicks;
                var projectionTicks = Math.Min(remainingTicks,
                    2 * segmentTicks - withinSegmentTicks);
                var nextTicks = Math.Min(remainingTicks,
                    segmentTicks - withinSegmentTicks);
                return new OpeningWindowSnapshot(_revision, true,
                    CeilingSeconds(projectionTicks), utcNow.AddTicks(nextTicks),
                    segment + 1);
            }
        }

        private OpeningWindowSnapshot ClosedSnapshot(DateTimeOffset? nextTransitionUtc)
            => new(_revision, false, 0, nextTransitionUtc);

        private static int CeilingSeconds(long ticks)
            => checked((int)((ticks + TimeSpan.TicksPerSecond - 1)
                / TimeSpan.TicksPerSecond));

        internal static bool TryParseConfiguration(string start, string end,
            string startVariable, string endVariable, string name,
            out DateTimeOffset? parsedStart, out DateTimeOffset? parsedEnd,
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
                error = startVariable + " and " + endVariable
                    + " must be configured together.";
                return false;
            }
            if (!TryParseTimestamp(start.Trim(), out var startsAtUtc)
                || !TryParseTimestamp(end.Trim(), out var endsAtUtc))
            {
                error = name + " timestamps must use ISO-8601 with seconds "
                    + "and an explicit timezone, for example "
                    + "2026-10-02T10:00:00Z or 2026-10-02T18:00:00+08:00.";
                return false;
            }
            if (!TryValidateWindow(startsAtUtc, endsAtUtc, name, out error))
                return false;
            parsedStart = startsAtUtc;
            parsedEnd = endsAtUtc;
            return true;
        }

        private static bool TryParseTimestamp(string value, out DateTimeOffset utc)
        {
            utc = default;
            if (!Regex.IsMatch(value, TimestampPattern, RegexOptions.CultureInvariant)
                || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return false;
            utc = parsed.ToUniversalTime();
            return true;
        }

        private static bool TryValidateWindow(DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc, string name, out string error)
        {
            if (endsAtUtc <= startsAtUtc)
            {
                error = name + " end must be later than its start.";
                return false;
            }
            if ((endsAtUtc - startsAtUtc).Ticks > MaximumWindowTicks)
            {
                error = name + " window exceeds the Int32 seconds budget.";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }
}
