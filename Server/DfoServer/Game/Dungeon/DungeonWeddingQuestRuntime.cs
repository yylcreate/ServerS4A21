using System;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonWeddingQuestRuntime
    {
        private readonly object _sync = new();
        private WeddingQuestClearEvidence _evidence;

        internal WeddingQuestClearEvidence Freeze(Guid source, Func<WeddingQuestClearEvidence> capture)
        {
            if (source == Guid.Empty) throw new ArgumentException("Canonical clear identity is required.", nameof(source));
            lock (_sync)
            {
                if (_evidence != null)
                {
                    if (_evidence.SourceEventId != source) throw new InvalidOperationException("Instance canonical clear changed.");
                    return _evidence;
                }
                var captured = capture(); // A failed read is retriable; never freeze an infrastructure failure as ineligible.
                if (captured == null || captured.SourceEventId != source) throw new InvalidOperationException("Invalid wedding clear evidence.");
                return _evidence = captured;
            }
        }

        internal WeddingQuestClearEvidence Get(Guid source)
        {
            lock (_sync) return _evidence?.SourceEventId == source ? _evidence : null;
        }
    }
}
