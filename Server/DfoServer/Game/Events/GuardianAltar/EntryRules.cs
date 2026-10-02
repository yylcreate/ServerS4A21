using System;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal readonly record struct GuardianAltarStartGameCommand;

    internal enum GuardianAltarEntryRejection
    {
        InvalidRequest,
        InvalidPresence,
        Closed,
        Trading,
        MercenaryAssigned,
        LevelTooLow,
        ProgressAuthorityUnavailable,
        StateReadFailed,
    }

    internal static class GuardianAltarEntryRules
    {
        // A21 startAdvanceAltar and START_GAME's level-rejection consumer both
        // use 50. The DGN minimum level is not the activity's entry requirement.
        internal const int MinimumLevel = 50;

        internal static GuardianAltarEntryRejection Evaluate(
            GuardianAltarOpeningSnapshot opening,
            int level,
            bool isTownReady,
            bool isTrading,
            bool isMercenaryAssigned)
        {
            ArgumentNullException.ThrowIfNull(opening);
            if (!opening.IsOpen)
                return GuardianAltarEntryRejection.Closed;
            if (!isTownReady)
                return GuardianAltarEntryRejection.InvalidPresence;
            if (isTrading)
                return GuardianAltarEntryRejection.Trading;
            if (isMercenaryAssigned)
                return GuardianAltarEntryRejection.MercenaryAssigned;
            if (level < MinimumLevel)
                return GuardianAltarEntryRejection.LevelTooLow;

            // Entry visibility does not establish ticket/progression authority.
            // Until that durable owner and the full snapshot are implemented,
            // do not manufacture a successful menu or admit a dungeon run.
            return GuardianAltarEntryRejection.ProgressAuthorityUnavailable;
        }
    }
}
