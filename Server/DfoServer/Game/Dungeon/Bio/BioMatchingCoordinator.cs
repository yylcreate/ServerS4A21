using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon.Bio
{
    // Application-side matching owner. Session/network adapters are added in a
    // later batch after the Bio listener and wire evidence are verified.
    internal sealed class BioMatchingCoordinator
    {
        private readonly object _sync = new object();
        private readonly BioMatchingQueue _queue;

        internal BioMatchingCoordinator(BioDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _queue = new BioMatchingQueue(definition.MinimumPlayers, definition.MaximumPlayers);
        }

        internal BioDefinition Definition { get; }

        internal BioQueueTicket Join(BioQueueIdentity identity, DateTime utcNow)
        {
            lock (_sync)
                return _queue.Join(identity, utcNow);
        }

        internal bool Cancel(BioQueueTicket ticket)
        {
            lock (_sync)
                return _queue.Cancel(ticket);
        }

        internal IReadOnlyList<BioQueueTicket> Snapshot(int listenerPort)
        {
            lock (_sync)
                return _queue.Snapshot(listenerPort);
        }

        internal bool TryFreeze(int listenerPort, out BioMatchingCohort cohort)
        {
            lock (_sync)
                return _queue.TryFreeze(_queue.Snapshot(listenerPort), out cohort);
        }

        internal void Clear()
        {
            lock (_sync)
                _queue.Clear();
        }
    }
}
