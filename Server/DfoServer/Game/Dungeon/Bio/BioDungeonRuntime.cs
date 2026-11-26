using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.Game.Dungeon.Bio
{
    // Shared owner for one frozen Bio cohort. Network adapters must first
    // authenticate to this object, then submit typed intents here.
    internal sealed class BioDungeonRuntime
    {
        private readonly object _sync = new object();
        private readonly DungeonInstance _instance;
        private readonly HashSet<byte> _entered = new HashSet<byte>();
        private readonly HashSet<byte> _loaded = new HashSet<byte>();
        private readonly HashSet<byte> _infected = new HashSet<byte>();
        private IReadOnlyList<byte> _initialZombies = Array.Empty<byte>();
        private BioPhase _phase = BioPhase.Loading;
        private DateTime _readyDeadlineUtc;
        private DateTime _playDeadlineUtc;
        private BioResultPlan _resultPlan;
        private bool _closed;

        internal BioDungeonRuntime(
            DungeonInstance instance,
            BioDefinition definition,
            long generation,
            IEnumerable<BioMember> members,
            byte hostSeat = 0)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            if (generation <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation));

            var frozen = (members ?? throw new ArgumentNullException(nameof(members)))
                .OrderBy(member => member.Seat).ToArray();
            if (frozen.Length < definition.MinimumPlayers || frozen.Length > definition.MaximumPlayers)
                throw new ArgumentException("Bio cohort size is outside the PVF bounds.", nameof(members));
            var seats = new HashSet<byte>();
            var characters = new HashSet<int>();
            var users = new HashSet<ushort>();
            foreach (var member in frozen)
            {
                if (member == null || member.Seat >= 8 || member.CharacterId <= 0
                    || member.UserId == 0 || member.UserId == ushort.MaxValue
                    || member.SessionId == Guid.Empty || member.Run == null
                    || member.Run.Instance != instance
                    || !member.RunIdentity.IsValid
                    || !member.RunIdentity.Equals(member.Run.CaptureIdentity())
                    || member.Run.RunState != DungeonRunState.Active
                    || !seats.Add(member.Seat) || !characters.Add(member.CharacterId)
                    || !users.Add(member.UserId))
                    throw new ArgumentException("Invalid frozen Bio participant identity.", nameof(members));
            }
            Members = new ReadOnlyCollection<BioMember>(frozen);
            Generation = generation;
            if (frozen.All(member => member.Seat != hostSeat))
                throw new ArgumentException("Bio host seat is absent.", nameof(hostSeat));
            HostSeat = hostSeat;
        }

        internal BioDefinition Definition { get; }
        internal IReadOnlyList<BioMember> Members { get; }
        internal long Generation { get; }
        internal byte HostSeat { get; }
        internal BioPhase Phase { get { lock (_sync) return _phase; } }
        internal DateTime ReadyDeadlineUtc { get { lock (_sync) return _readyDeadlineUtc; } }
        internal DateTime PlayDeadlineUtc { get { lock (_sync) return _playDeadlineUtc; } }
        internal DateTime PlayStartedUtc
        {
            get { lock (_sync) return _playDeadlineUtc.AddMilliseconds(-Definition.PlayMilliseconds); }
        }
        internal IReadOnlyList<byte> InitialZombieSeats
        {
            get { lock (_sync) return _initialZombies; }
        }
        internal BioResultPlan ResultPlan { get { lock (_sync) return _resultPlan; } }

        internal bool TryGetMember(BioCaller caller, long generation, out BioMember member)
        {
            lock (_sync)
                return Authenticate(caller, generation, out member);
        }

        internal BioDecision TryClaimEntry(BioCaller caller)
        {
            lock (_sync)
            {
                if (!Authenticate(caller, Generation, out var member) || _phase != BioPhase.Loading)
                    return BioDecision.Rejected;
                return _entered.Add(member.Seat) ? BioDecision.Applied : BioDecision.Replayed;
            }
        }

        internal BioDecision MarkLoaded(BioCaller caller, long generation, DateTime utcNow)
        {
            RequireUtc(utcNow);
            lock (_sync)
            {
                if (!Authenticate(caller, generation, out var member)
                    || (_phase != BioPhase.Loading && _phase != BioPhase.Ready))
                    return BioDecision.Rejected;
                if (!_loaded.Add(member.Seat))
                    return BioDecision.Replayed;
                if (_loaded.Count == Members.Count)
                {
                    _phase = BioPhase.Ready;
                    _readyDeadlineUtc = utcNow.AddMilliseconds(Definition.ReadyMilliseconds);
                }
                return BioDecision.Applied;
            }
        }

        internal BioDecision TryCommitSelection(
            BioCaller caller,
            long generation,
            IReadOnlyList<byte> initialZombieSeats,
            DateTime utcNow)
        {
            RequireUtc(utcNow);
            lock (_sync)
            {
                if (!Authenticate(caller, generation, out _))
                    return BioDecision.Rejected;
                if (_phase == BioPhase.Playing)
                    return BioDecision.Replayed;
                if (_phase != BioPhase.Ready || utcNow < _readyDeadlineUtc || initialZombieSeats == null)
                    return BioDecision.Rejected;
                var seats = initialZombieSeats.ToArray();
                if (seats.Length == 0 || seats.Length >= Members.Count
                    || seats.Distinct().Count() != seats.Length
                    || seats.Any(seat => Members.All(member => member.Seat != seat)))
                    return BioDecision.Rejected;
                _initialZombies = new ReadOnlyCollection<byte>(seats);
                foreach (var seat in seats)
                    _infected.Add(seat);
                _phase = BioPhase.Playing;
                _playDeadlineUtc = utcNow.AddMilliseconds(Definition.PlayMilliseconds);
                return BioDecision.Applied;
            }
        }

        internal BioDecision TryRecordInfection(
            BioCaller caller,
            long generation,
            byte targetSeat,
            DateTime utcNow)
        {
            RequireUtc(utcNow);
            lock (_sync)
            {
                if (!Authenticate(caller, generation, out var member)
                    || _phase != BioPhase.Playing || utcNow >= _playDeadlineUtc
                    || targetSeat != member.Seat)
                    return BioDecision.Rejected;
                return _infected.Add(targetSeat) ? BioDecision.Applied : BioDecision.Replayed;
            }
        }

        internal BioDecision TryCommitResult(
            BioCaller caller,
            byte outcome,
            IReadOnlyList<BioScoreInput> rows,
            DateTime utcNow)
        {
            lock (_sync)
            {
                if (!Authenticate(caller, Generation, out var member)
                    || member.Seat != HostSeat)
                    return BioDecision.Rejected;
                if (_resultPlan != null)
                    return BioDecision.Replayed;
                if (_phase != BioPhase.Playing
                    || !BioResultPlanner.TryCreate(this, outcome, rows, utcNow, out var plan))
                    return BioDecision.Rejected;
                _resultPlan = plan;
                _phase = BioPhase.ResultPending;
                return BioDecision.Applied;
            }
        }

        internal bool TryMarkResults(BioResultPlan expected)
        {
            lock (_sync)
            {
                if (_closed || _phase != BioPhase.ResultPending || !ReferenceEquals(expected, _resultPlan))
                    return false;
                _phase = BioPhase.Results;
                return true;
            }
        }

        internal void End()
        {
            lock (_sync)
            {
                if (_closed)
                    return;
                _closed = true;
                _phase = BioPhase.Ended;
            }
        }

        private bool Authenticate(BioCaller caller, long generation, out BioMember member)
        {
            member = null;
            if (_closed || generation != Generation || _instance.State is DungeonInstanceState.Ending or DungeonInstanceState.Ended
                || caller.Run == null || caller.Run.RunState != DungeonRunState.Active)
                return false;
            member = Members.FirstOrDefault(candidate => candidate.SessionId == caller.SessionId
                && candidate.CharacterId == caller.CharacterId
                && candidate.UserId == caller.UserId
                && ReferenceEquals(candidate.Run, caller.Run)
                && candidate.RunIdentity.Equals(caller.Run.CaptureIdentity()));
            return member != null;
        }

        private static void RequireUtc(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC timestamp required.", nameof(utcNow));
        }
    }
}
