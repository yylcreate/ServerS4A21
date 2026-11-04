using System;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingPartnerTravelDestination(int Channel, byte Town, byte Area, short X, short Y);
    internal sealed record WeddingPartnerTravelMove(int Slot, byte Town, byte Area, short X, short Y);
    internal sealed record WeddingPartnerTravelTicket(Guid ActorSession, int ActorId, Guid PartnerSession, int PartnerId,
        long PairId, long PairVersion, int ListenerPort, WeddingPartnerTravelDestination Destination, long ExpiresTick,
        int Slot, int ItemId, byte[] ItemCore)
    {
        // Confirmation lifetime is a server policy. It is not a client/PVF timer.
        internal const int LifetimeMilliseconds = 30000;
        internal bool Matches(Guid actorSession, int actorId, Guid partnerSession, int partnerId,
            WeddingState actor, WeddingState partner, int listenerPort, WeddingPartnerTravelDestination currentDestination,
            WeddingPartnerTravelMove requested, long nowTick)
            => nowTick < ExpiresTick && ActorSession == actorSession && ActorId == actorId
                && PartnerSession == partnerSession && PartnerId == partnerId && ListenerPort == listenerPort
                && actor?.Status == WeddingStatus.Married && actor.PairId == PairId && actor.Version == PairVersion
                && WeddingRules.IsReciprocal(actor, partner) && actor.PartnerId == partnerId
                && Destination == currentDestination && requested?.Slot == Slot && Destination.Town == requested.Town
                && Destination.Area == requested.Area && Destination.X == requested.X && Destination.Y == requested.Y;
    }
}
