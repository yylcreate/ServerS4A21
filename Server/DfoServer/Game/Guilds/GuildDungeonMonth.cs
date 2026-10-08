using System;

namespace DfoServer.Game.Guilds
{
    internal static class GuildDungeonMonth
    {
        private static readonly TimeSpan OperatingOffset = TimeSpan.FromHours(8);
        private static readonly TimeSpan ResetHour = TimeSpan.FromHours(6);

        // Automatic monthly reset is the operating policy. The 06:00 boundary
        // follows the current client's purification text (DSTR 48453).
        internal static int MonthId(DateTimeOffset now)
        {
            var local = now.ToOffset(OperatingOffset).Subtract(ResetHour);
            int monthId = checked(local.Year * 100 + local.Month);
            if (!IsValid(monthId))
                throw new ArgumentOutOfRangeException(nameof(now), "Guild dungeon month is outside the supported calendar.");
            return monthId;
        }

        internal static bool IsValid(int monthId)
            => monthId >= 197001 && monthId <= 999912
                && monthId % 100 >= 1 && monthId % 100 <= 12;

        internal static uint NextResetUnixSeconds(DateTimeOffset now)
        {
            int monthId = MonthId(now);
            var periodStart = new DateTimeOffset(monthId / 100, monthId % 100, 1,
                6, 0, 0, OperatingOffset);
            // INFO offset 5 is an absolute second on the same Unix clock as
            // the initial login notice, not a remaining duration or month ID.
            return checked((uint)periodStart.AddMonths(1).ToUnixTimeSeconds());
        }

        internal static int ResolveMonthId(int monthId)
        {
            int resolved = monthId == 0 ? MonthId(DateTimeOffset.UtcNow) : monthId;
            if (!IsValid(resolved))
                throw new ArgumentOutOfRangeException(nameof(monthId), "Guild dungeon month must be a valid yyyyMM value.");
            return resolved;
        }
    }
}
