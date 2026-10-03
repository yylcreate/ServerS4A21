using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Dungeon;
using PvfLib;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed record GuardianAltarCombatSpawn(ushort SequenceId, GuardianAltarSummonObject Object);

    internal enum GuardianAltarTerminalResult
    {
        Succeeded,
        Failed,
        Aborted,
    }

    internal sealed record GuardianAltarTerminalFact(Guid EventId,
        DungeonParticipantRoomIdentity Identity, long LoadingGeneration,
        long ActiveMilliseconds, GuardianAltarTerminalResult Result, string Reason);

    internal sealed class GuardianAltarSpawnBatch
    {
        internal GuardianAltarSpawnBatch(DungeonParticipantRoomIdentity identity, long providerGeneration,
            int waveIdentity, int phaseIndex, long dueActiveMilliseconds, GuardianAltarCombatSpawn[] spawns)
        {
            ReservationId = Guid.NewGuid();
            Identity = identity;
            ProviderGeneration = providerGeneration;
            WaveIdentity = waveIdentity;
            PhaseIndex = phaseIndex;
            DueActiveMilliseconds = dueActiveMilliseconds;
            Spawns = Array.AsReadOnly(spawns);
        }

        internal Guid ReservationId { get; }
        internal DungeonParticipantRoomIdentity Identity { get; }
        internal long ProviderGeneration { get; }
        internal int WaveIdentity { get; }
        internal int PhaseIndex { get; }
        internal long DueActiveMilliseconds { get; }
        internal IReadOnlyList<GuardianAltarCombatSpawn> Spawns { get; }
    }

    // This instance consumes the energy owner's active elapsed time. It owns
    // no clock, loading state, timer, reward policy or canonical death ledger.
    internal sealed class GuardianAltarCombatRuntime
    {
        internal const string DynamicActorProvider = "guardian-altar";

        private sealed record Selection(int ActionId, int MinimumAlive, int MaximumAlive, bool IsDefault);
        private sealed record Entry(GuardianAltarSummonObject Object, int FirstDelay, int RepeatDelay, int MaximumCount);
        private sealed record Phase(long Start, long End, Selection[] Selections);

        private sealed class EntryState
        {
            internal Entry Definition;
            internal int Count;
            internal long NextDue;
        }

        private sealed class ActorState
        {
            internal bool Dead;
        }

        private readonly object _syncRoot = new object();
        private readonly Phase[] _phases;
        private readonly Dictionary<int, Entry[]> _actions;
        private readonly Dictionary<ushort, ActorState> _actors = new Dictionary<ushort, ActorState>();
        private readonly HashSet<ushort> _centerSequences;
        private readonly HashSet<ushort> _deadCenters = new HashSet<ushort>();
        private DungeonParticipantRoomIdentity _identity;
        private long _generation;
        private bool _bound;
        private bool _closed;
        private bool _exhausted;
        private bool _timelineEnded;
        private int _nextSequence;
        private int _nextWaveIdentity = 1;
        private int _phaseIndex = -1;
        private int _alive;
        private long _lastActiveMilliseconds;
        private EntryState[] _entries = Array.Empty<EntryState>();
        private GuardianAltarSpawnBatch _pending;
        private int[] _pendingEntries;
        private bool _pendingRegistered;
        private int _pendingProjectionOffset;
        private Guid _lastProjectedReservationId;
        private GuardianAltarTerminalFact _terminalFact;

        internal GuardianAltarCombatRuntime(GuardianAltarMapFile map,
            GuardianAltarSummonDataFile summonData, int firstSequence,
            IEnumerable<ushort> centerSequences = null)
        {
            ArgumentNullException.ThrowIfNull(map);
            ArgumentNullException.ThrowIfNull(summonData);
            if (!map.IsValid || !summonData.IsValid || map.Phases.Count == 0)
                throw new InvalidDataException("Guardian combat requires valid typed map and summon definitions.");
            if (firstSequence <= 0 || firstSequence > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(firstSequence));
            _nextSequence = firstSequence;
            var centers = (centerSequences ?? Array.Empty<ushort>()).ToArray();
            if (centers.Any(x => x >= firstSequence || x > short.MaxValue)
                || centers.Distinct().Count() != centers.Length)
                throw new InvalidDataException("Guardian failure-center sequences overlap dynamic actors or repeat.");
            // The caller freezes failure centers from the actual START_MAP
            // projection and typed center resource. Absence cannot authorize a
            // success or imply that sequence zero is a valid failure center.
            _centerSequences = new HashSet<ushort>(centers);

            var objects = summonData.Objects.ToDictionary(x => x.Id);
            var actions = summonData.Actions.ToDictionary(x => x.Id);
            _actions = new Dictionary<int, Entry[]>();
            _phases = new Phase[map.Phases.Count];
            long phaseStart = 0;
            for (var index = 0; index < map.Phases.Count; index++)
            {
                var phase = map.Phases[index];
                var phaseEnd = checked(phaseStart + phase.DurationMilliseconds);
                var selections = phase.ActionSelections.Select(x =>
                    new Selection(x.ActionId, x.MinimumAlive, x.MaximumAlive, x.IsDefault)).ToArray();
                if (phase.DurationMilliseconds <= 0 || selections.Length == 0)
                    throw new InvalidDataException("Guardian combat phase has no valid duration or selections.");
                foreach (var selection in selections)
                {
                    if (!actions.TryGetValue(selection.ActionId, out var action))
                        throw new InvalidDataException("Guardian combat phase references an unknown summon action.");
                    if (_actions.ContainsKey(action.Id))
                        continue;
                    var entries = new List<Entry>();
                    foreach (var entry in action.Entries)
                    {
                        if (!objects.TryGetValue(entry.SummonObjectId, out var definition)
                            || !definition.IsEnemy || definition.Level <= 0 || definition.Level > byte.MaxValue
                            || entry.FirstDelayMilliseconds < 0 || entry.RepeatDelayMilliseconds < 500
                            || entry.MaximumCount < 0)
                            throw new InvalidDataException("Guardian enemy action has an unsupported object, level, delay or count.");
                        // The typed resource objects have only get accessors.
                        // Their full-width prototype and coordinates stay intact.
                        entries.Add(new Entry(definition, entry.FirstDelayMilliseconds,
                            entry.RepeatDelayMilliseconds, entry.MaximumCount));
                    }
                    if (entries.Count == 0)
                        throw new InvalidDataException("Guardian enemy action has no summon entries.");
                    _actions.Add(action.Id, entries.ToArray());
                }
                _phases[index] = new Phase(phaseStart, phaseEnd, selections);
                phaseStart = phaseEnd;
            }
        }

        internal bool BindLoadingRelease(DungeonParticipantRoomIdentity identity, long loadingGeneration)
        {
            lock (_syncRoot)
            {
                if (_closed || _terminalFact != null || !identity.IsValid || loadingGeneration <= 0)
                    return false;
                if (_bound)
                    return _identity.Equals(identity) && _generation == loadingGeneration;
                _identity = identity;
                _generation = loadingGeneration;
                _bound = true;
                return true;
            }
        }

        internal int AliveCount { get { lock (_syncRoot) return _alive; } }
        internal bool IsClosed { get { lock (_syncRoot) return _closed; } }
        internal bool IsTerminal { get { lock (_syncRoot) return _terminalFact != null || _closed; } }
        internal GuardianAltarTerminalFact TerminalFact { get { lock (_syncRoot) return _terminalFact; } }
        internal RunTimerRegistry Timers { get; } = new RunTimerRegistry();
        internal GuardianAltarClearPresentationRuntime Presentation { get; } = new GuardianAltarClearPresentationRuntime();

        internal long? NextDueActiveMilliseconds
        {
            get
            {
                lock (_syncRoot)
                {
                    if (!_bound || _closed || _terminalFact != null || _exhausted || _timelineEnded)
                        return null;
                    if (_pending != null)
                        return _pending.DueActiveMilliseconds;
                    if (_phaseIndex < 0)
                        return 0;
                    var next = _phases[_phaseIndex].End;
                    foreach (var entry in _entries)
                        if (!IsFinished(entry) && entry.NextDue < next)
                            next = entry.NextDue;
                    return next;
                }
            }
        }

        internal bool TryReserveDue(long activeMilliseconds, DungeonParticipantRoomIdentity identity,
            long loadingGeneration, out GuardianAltarSpawnBatch batch, out string failureReason)
        {
            lock (_syncRoot)
            {
                batch = null;
                failureReason = string.Empty;
                if (!MatchesLocked(identity, loadingGeneration) || activeMilliseconds < 0)
                {
                    failureReason = "stale-combat-scope";
                    return false;
                }
                _lastActiveMilliseconds = Math.Max(_lastActiveMilliseconds, activeMilliseconds);
                if (_pending != null)
                {
                    batch = _pending;
                    return true;
                }
                if (_exhausted)
                {
                    failureReason = "sequence-space-exhausted";
                    return false;
                }
                if (_timelineEnded)
                    return false;

                // A delayed callback cannot materialize waves in an expired
                // phase. Enter only the current phase and freeze its choice.
                var targetPhase = Math.Max(0, _phaseIndex);
                while (targetPhase < _phases.Length
                    && _lastActiveMilliseconds >= _phases[targetPhase].End)
                    targetPhase++;
                if (targetPhase == _phases.Length)
                {
                    _timelineEnded = true;
                    _entries = Array.Empty<EntryState>();
                    return false;
                }
                if (targetPhase != _phaseIndex)
                    EnterPhaseLocked(targetPhase);

                var phaseEnd = _phases[_phaseIndex].End;
                var due = phaseEnd;
                foreach (var entry in _entries)
                    if (!IsFinished(entry) && entry.NextDue < due)
                        due = entry.NextDue;
                if (due >= phaseEnd || due > _lastActiveMilliseconds)
                    return false;
                var entryIndices = Enumerable.Range(0, _entries.Length)
                    .Where(x => !IsFinished(_entries[x]) && _entries[x].NextDue == due).ToArray();
                if (entryIndices.Length > short.MaxValue - _nextSequence + 1)
                {
                    _exhausted = true;
                    failureReason = "sequence-space-exhausted";
                    return false;
                }
                var spawns = new GuardianAltarCombatSpawn[entryIndices.Length];
                for (var index = 0; index < entryIndices.Length; index++)
                    spawns[index] = new GuardianAltarCombatSpawn((ushort)_nextSequence++,
                        _entries[entryIndices[index]].Definition.Object);
                _pending = new GuardianAltarSpawnBatch(_identity, _generation, _nextWaveIdentity++,
                    _phaseIndex, due, spawns);
                _pendingEntries = entryIndices;
                _pendingRegistered = false;
                _pendingProjectionOffset = 0;
                batch = _pending;
                return true;
            }
        }

        internal bool TryCommitRegistered(GuardianAltarSpawnBatch batch)
        {
            lock (_syncRoot)
            {
                if (!IsPendingLocked(batch))
                    return IsLastProjectedLocked(batch);
                if (_pendingRegistered)
                    return true;
                foreach (var spawn in batch.Spawns)
                    _actors.Add(spawn.SequenceId, new ActorState());
                _alive += batch.Spawns.Count;
                foreach (var index in _pendingEntries)
                {
                    var entry = _entries[index];
                    entry.Count++;
                    entry.NextDue += entry.Definition.RepeatDelay;
                }
                _pendingRegistered = true;
                return true;
            }
        }

        internal bool TryMarkProjected(GuardianAltarSpawnBatch batch)
        {
            lock (_syncRoot)
            {
                if (!IsPendingLocked(batch))
                    return IsLastProjectedLocked(batch);
                if (!_pendingRegistered || _pendingProjectionOffset != batch.Spawns.Count)
                    return false;
                _lastProjectedReservationId = batch.ReservationId;
                _pending = null;
                _pendingEntries = null;
                _pendingRegistered = false;
                _pendingProjectionOffset = 0;
                return true;
            }
        }

        internal int GetProjectionOffset(GuardianAltarSpawnBatch batch)
        {
            lock (_syncRoot) return IsPendingLocked(batch) ? _pendingProjectionOffset : -1;
        }

        // Record only a positively completed frame write. A failed or unknown
        // write leaves the offset unchanged and remains an at-least-once retry.
        internal bool TryRecordSpawnProjected(GuardianAltarSpawnBatch batch, int index)
        {
            lock (_syncRoot)
            {
                if (!IsPendingLocked(batch) || !_pendingRegistered || index < 0
                    || index >= batch.Spawns.Count || index > _pendingProjectionOffset)
                    return false;
                if (index == _pendingProjectionOffset)
                    _pendingProjectionOffset++;
                return true;
            }
        }

        internal bool RetainForRetry(GuardianAltarSpawnBatch batch)
        {
            lock (_syncRoot) return IsPendingLocked(batch);
        }

        internal bool IsCurrent(GuardianAltarSpawnBatch batch)
        {
            lock (_syncRoot) return IsPendingLocked(batch);
        }

        // Friendly units reserve from this same instance sequence space. Their
        // own unit owner registers them; they never count as living enemies.
        internal bool TryAllocateSequence(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            out ushort sequenceId)
        {
            lock (_syncRoot)
            {
                sequenceId = 0;
                if (!MatchesLocked(identity, loadingGeneration) || _nextSequence > short.MaxValue)
                    return false;
                sequenceId = (ushort)_nextSequence++;
                return true;
            }
        }

        internal bool CanAcceptActorDeath(DungeonParticipantRoomIdentity identity,
            long loadingGeneration, ushort sequenceId)
        {
            lock (_syncRoot)
                return MatchesLocked(identity, loadingGeneration)
                    && _actors.TryGetValue(sequenceId, out var actor) && !actor.Dead;
        }

        // Called after the shared Room has accepted the canonical death fact.
        // Replays only update this mechanism's living roster once.
        internal bool TryApplyDeath(DungeonParticipantRoomIdentity identity,
            long loadingGeneration, ushort sequenceId, out bool changed)
        {
            lock (_syncRoot)
            {
                changed = false;
                if (!MatchesLocked(identity, loadingGeneration)
                    || !_actors.TryGetValue(sequenceId, out var actor))
                    return false;
                if (actor.Dead)
                    return true;
                actor.Dead = true;
                _alive--;
                changed = true;
                return true;
            }
        }

        internal bool TryComplete(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            long activeMilliseconds, out GuardianAltarTerminalFact fact)
        {
            var created = false;
            lock (_syncRoot)
            {
                fact = null;
                if (!MatchesScopeLocked(identity, loadingGeneration) || activeMilliseconds < 0)
                    return false;
                if (_terminalFact != null)
                {
                    fact = _terminalFact;
                    return fact.Result == GuardianAltarTerminalResult.Succeeded;
                }
                _lastActiveMilliseconds = Math.Max(_lastActiveMilliseconds, activeMilliseconds);
                if (!_timelineEnded || _pending != null || _alive != 0 || _exhausted
                    || _centerSequences.Count == 0 || _deadCenters.Count == _centerSequences.Count)
                    return false;
                fact = new GuardianAltarTerminalFact(Guid.NewGuid(), _identity, _generation,
                    _lastActiveMilliseconds, GuardianAltarTerminalResult.Succeeded, "all-phases-and-enemies-complete");
                _terminalFact = fact;
                created = true;
            }
            if (created)
                Timers.CancelAll();
            return true;
        }

        internal bool CanAcceptCenterDeath(DungeonParticipantRoomIdentity identity,
            long loadingGeneration, ushort sequenceId)
        {
            lock (_syncRoot)
                return MatchesLocked(identity, loadingGeneration)
                    && _centerSequences.Contains(sequenceId) && !_deadCenters.Contains(sequenceId);
        }

        // A center may use wire sequence zero. It never enters the dynamic
        // enemy roster or the ordinary shared canonical actor-death ledger.
        // Accepted partial center deaths return true with no terminal fact;
        // failure is first-write-wins when no configured center survives.
        internal bool TryFailCenter(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            ushort sequenceId, long activeMilliseconds, out GuardianAltarTerminalFact fact)
        {
            var created = false;
            lock (_syncRoot)
            {
                fact = null;
                if (!MatchesScopeLocked(identity, loadingGeneration) || activeMilliseconds < 0
                    || !_centerSequences.Contains(sequenceId))
                    return false;
                if (_terminalFact != null)
                {
                    fact = _terminalFact;
                    return fact.Result == GuardianAltarTerminalResult.Failed;
                }
                _lastActiveMilliseconds = Math.Max(_lastActiveMilliseconds, activeMilliseconds);
                _deadCenters.Add(sequenceId);
                if (_deadCenters.Count != _centerSequences.Count)
                    return true;
                fact = new GuardianAltarTerminalFact(Guid.NewGuid(), _identity, _generation,
                    _lastActiveMilliseconds, GuardianAltarTerminalResult.Failed, "all-failure-centers-defeated");
                _terminalFact = fact;
                created = true;
            }
            if (created)
                Timers.CancelAll();
            return true;
        }

        internal bool TryFailCharacterDeath(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            long activeMilliseconds, out GuardianAltarTerminalFact fact)
        {
            var created = false;
            lock (_syncRoot)
            {
                fact = null;
                if (!MatchesScopeLocked(identity, loadingGeneration) || activeMilliseconds < 0)
                    return false;
                if (_terminalFact != null)
                {
                    fact = _terminalFact;
                    return fact.Result == GuardianAltarTerminalResult.Failed;
                }
                _lastActiveMilliseconds = Math.Max(_lastActiveMilliseconds, activeMilliseconds);
                fact = new GuardianAltarTerminalFact(Guid.NewGuid(), _identity, _generation,
                    _lastActiveMilliseconds, GuardianAltarTerminalResult.Failed, "guardian-character-death");
                _terminalFact = fact;
                created = true;
            }
            if (created)
                Timers.CancelAll();
            return true;
        }

        internal void Close()
        {
            lock (_syncRoot)
            {
                if (_closed)
                    return;
                if (_bound && _terminalFact == null)
                    _terminalFact = new GuardianAltarTerminalFact(Guid.NewGuid(), _identity, _generation,
                        _lastActiveMilliseconds, GuardianAltarTerminalResult.Aborted, "instance-closed");
                _closed = true;
                _pending = null;
                _pendingEntries = null;
                _pendingProjectionOffset = 0;
                _entries = Array.Empty<EntryState>();
                _actors.Clear();
                _alive = 0;
            }
            Timers.CancelAll();
        }

        private void EnterPhaseLocked(int phaseIndex)
        {
            _phaseIndex = phaseIndex;
            var phase = _phases[phaseIndex];
            Selection matching = null;
            Selection fallback = null;
            foreach (var selection in phase.Selections)
                if (selection.IsDefault)
                    fallback = selection;
                else if (_alive >= selection.MinimumAlive && _alive <= selection.MaximumAlive)
                    matching = selection;
            var chosen = matching ?? fallback;
            _entries = chosen == null ? Array.Empty<EntryState>()
                : _actions[chosen.ActionId].Select(x => new EntryState
                {
                    Definition = x,
                    NextDue = phase.Start + x.FirstDelay,
                }).ToArray();
        }

        private static bool IsFinished(EntryState entry)
            => entry.Definition.MaximumCount > 0 && entry.Count >= entry.Definition.MaximumCount;

        private bool MatchesLocked(DungeonParticipantRoomIdentity identity, long loadingGeneration)
            => _terminalFact == null && MatchesScopeLocked(identity, loadingGeneration);

        private bool MatchesScopeLocked(DungeonParticipantRoomIdentity identity, long loadingGeneration)
            => _bound && !_closed && identity.IsValid && _identity.Equals(identity)
                && _generation == loadingGeneration;

        private bool IsPendingLocked(GuardianAltarSpawnBatch batch)
            => batch != null && MatchesLocked(batch.Identity, batch.ProviderGeneration)
                && ReferenceEquals(_pending, batch);

        private bool IsLastProjectedLocked(GuardianAltarSpawnBatch batch)
            => batch != null && MatchesLocked(batch.Identity, batch.ProviderGeneration)
                && batch.ReservationId == _lastProjectedReservationId;
    }
}
