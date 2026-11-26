using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.Game.Dungeon.Bio
{
    // The queue is the only owner of waiting tickets. A ticket is frozen into
    // a cohort before any run or room is created.
    internal sealed class BioMatchingQueue
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, BioQueueTicket> _waiting = new Dictionary<int, BioQueueTicket>();
        private readonly int _minimum;
        private readonly int _maximum;
        private long _generation;

        internal BioMatchingQueue(int minimum, int maximum)
        {
            if (minimum < 2 || maximum < minimum || maximum > 8)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            _minimum = minimum;
            _maximum = maximum;
        }

        internal BioQueueTicket Join(BioQueueIdentity identity, DateTime utcNow)
        {
            if (identity.SessionId == Guid.Empty || identity.AccountId <= 0
                || identity.CharacterId <= 0 || identity.UserId == 0
                || identity.UserId == ushort.MaxValue || identity.ListenerPort <= 0
                || utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Invalid matching identity.", nameof(identity));

            lock (_sync)
            {
                if (_waiting.TryGetValue(identity.CharacterId, out var existing)
                    && existing.Identity == identity)
                    return existing;
                if (_waiting.Values.Any(ticket => ticket.Identity.CharacterId != identity.CharacterId
                    && (ticket.Identity.SessionId == identity.SessionId
                        || ticket.Identity.AccountId == identity.AccountId
                        || ticket.Identity.UserId == identity.UserId)))
                    throw new InvalidOperationException("Duplicate matching owner.");

                var ticket = new BioQueueTicket(checked(++_generation), identity, utcNow);
                _waiting[identity.CharacterId] = ticket;
                return ticket;
            }
        }

        internal bool Cancel(BioQueueTicket expected)
        {
            if (expected == null)
                return false;
            lock (_sync)
            {
                return _waiting.TryGetValue(expected.Identity.CharacterId, out var current)
                    && ReferenceEquals(current, expected)
                    && _waiting.Remove(expected.Identity.CharacterId);
            }
        }

        internal IReadOnlyList<BioQueueTicket> Snapshot(int listenerPort)
        {
            lock (_sync)
            {
                return new ReadOnlyCollection<BioQueueTicket>(_waiting.Values
                    .Where(ticket => ticket.Identity.ListenerPort == listenerPort)
                    .OrderBy(ticket => ticket.Generation)
                    .Take(_maximum)
                    .ToArray());
            }
        }

        internal BioQueueTicket Find(BioQueueIdentity identity)
        {
            lock (_sync)
            {
                return _waiting.TryGetValue(identity.CharacterId, out var ticket)
                    && ticket.Identity == identity ? ticket : null;
            }
        }

        internal bool TryFreeze(IReadOnlyList<BioQueueTicket> expected, out BioMatchingCohort cohort)
        {
            cohort = null;
            if (expected == null || expected.Count < _minimum || expected.Count > _maximum)
                return false;

            lock (_sync)
            {
                var ordered = expected.OrderBy(ticket => ticket?.Generation ?? long.MaxValue).ToArray();
                if (ordered.Any(ticket => ticket == null)
                    || ordered.Select(ticket => ticket.Identity.CharacterId).Distinct().Count() != ordered.Length
                    || ordered.Select(ticket => ticket.Identity.ListenerPort).Distinct().Count() != 1
                    || ordered.Any(ticket => !_waiting.TryGetValue(ticket.Identity.CharacterId, out var current)
                        || !ReferenceEquals(current, ticket)))
                    return false;

                foreach (var ticket in ordered)
                    _waiting.Remove(ticket.Identity.CharacterId);
                cohort = new BioMatchingCohort(
                    checked(++_generation),
                    new ReadOnlyCollection<BioQueueTicket>(ordered));
                return true;
            }
        }

        internal void Clear()
        {
            lock (_sync)
                _waiting.Clear();
        }
    }
}
