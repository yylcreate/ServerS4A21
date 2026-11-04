using System;

namespace DfoServer.Game.Quests
{
    // Created only from the canonical instance clear and its frozen participants.
    // This is not a client-supplied condition and is shared by both recipients.
    internal sealed record WeddingQuestClearEvidence(Guid SourceEventId, long PairId, int FirstCharacterId, int SecondCharacterId, bool Eligible)
    {
        internal bool Allows(Guid source, int characterId) => Eligible && PairId > 0
            && source != Guid.Empty && SourceEventId == source
            && FirstCharacterId > 0 && SecondCharacterId > 0 && FirstCharacterId != SecondCharacterId
            && (characterId == FirstCharacterId || characterId == SecondCharacterId);
    }
}
