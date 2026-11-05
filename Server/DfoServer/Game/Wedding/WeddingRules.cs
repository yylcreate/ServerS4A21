using DfoServer.Game.Characters;
using DfoServer.GameWorld;
using System;

namespace DfoServer.Game.Wedding
{
    internal static class WeddingRules
    {
        // A21 sub_C916B0, called for both actors at C8AC3E/C8AC52.
        // The 90 reference's additional job 15 branch does not exist in A21.
        internal static bool IsMale(byte job) => job is 0 or 2 or 4 or 7 or 8 or 9 or 13;

        internal static WeddingError CanEngage(CharacterRecord actor, CharacterRecord partner,
            WeddingState actorState, WeddingState partnerState, WeddingCatalog catalog,
            long actorDivorcedAt, long partnerDivorcedAt, long now)
        {
            if (actor == null || partner == null || actor.Deleted || partner.Deleted
                || actor.CharacterId <= 0 || partner.CharacterId <= 0 || actor.CharacterId == partner.CharacterId)
                return WeddingError.Missing;
            if (actorState.CharacterId != actor.CharacterId || partnerState.CharacterId != partner.CharacterId)
                return WeddingError.Invalid;
            if (actorState.Status != WeddingStatus.None || partnerState.Status != WeddingStatus.None)
                return WeddingError.AlreadyBound;
            if (actor.Level < catalog.MinimumLevel || partner.Level < catalog.MinimumLevel
                || IsMale(actor.Job) == IsMale(partner.Job)) return WeddingError.Ineligible;
            long cooldown = checked((long)catalog.RemarryDays * 86400);
            if ((actorDivorcedAt > 0 && now < checked(actorDivorcedAt + cooldown))
                || (partnerDivorcedAt > 0 && now < checked(partnerDivorcedAt + cooldown)))
                return WeddingError.Cooldown;
            return WeddingError.Success;
        }

        internal static bool IsReciprocal(WeddingState actor, WeddingState partner)
            => actor.PairId > 0 && actor.PairId == partner.PairId && actor.CharacterId == partner.PartnerId
            && partner.CharacterId == actor.PartnerId && actor.Status == partner.Status && actor.Version == partner.Version;
    }
}
