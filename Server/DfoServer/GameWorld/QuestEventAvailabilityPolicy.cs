using System;
using PvfLib;

namespace DfoServer.GameWorld
{
    // The quest resource identifies its event; the existing EVENT_INFO catalog
    // owns whether that event is advertised. The [event] flag is not an event ID.
    internal static class QuestEventAvailabilityPolicy
    {
        internal static bool IsAvailable(QuestFile quest, Func<int, bool> isEventAdvertised)
        {
            if (quest == null || !quest.IsEventVisibilityValid)
                return false;
            if (quest.ShowQuestOnEventId > 0)
                return isEventAdvertised?.Invoke(quest.ShowQuestOnEventId) == true;
            return !quest.IsEvent;
        }
    }
}
