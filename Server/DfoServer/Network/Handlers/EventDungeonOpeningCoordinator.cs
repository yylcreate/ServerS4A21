using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.ScheduledDungeons;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    // Projects the existing window/count owners. No activity rewards, entry
    // debits or dungeon lifecycle transitions belong to this adapter.
    internal sealed class EventDungeonOpeningCoordinator : IDisposable
    {
        private readonly EventDungeonOpeningService _service;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ClockService _clock;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<int, int, IReadOnlyList<SpecialDungeonEntryLimitSnapshot>> _loadLimits;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> _sendBatch;
        private readonly object _timerSync = new();
        private readonly object _notificationSync = new();
        private readonly Dictionary<Guid, StateProjectionKey> _lastStates = new();
        private readonly Dictionary<Guid, CountProjectionKey> _lastCounts = new();
        private readonly string _timerName = "event-dungeon-opening:" + Guid.NewGuid();
        private ClockService.ClockTimerHandle _timer;
        private volatile bool _disposed;
        private bool _started;

        internal EventDungeonOpeningCoordinator(EventDungeonOpeningService service,
            ISessionDirectory sessions, CharacterTransitionCoordinator transitions,
            ClockService clock, DungeonEntryLimitService entryLimits,
            Func<DateTimeOffset> utcNow = null,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> sendBatch = null,
            Func<int, int, IReadOnlyList<SpecialDungeonEntryLimitSnapshot>> loadLimits = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _clock = clock;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _loadLimits = loadLimits ?? (entryLimits ?? throw new ArgumentNullException(nameof(entryLimits)))
                .LoadSpecialDungeonLimits;
            _sendBatch = sendBatch ?? ((session, prepare, guard) =>
                session.SendPreparedPacketBatchAsync(prepare, guard));
            _sessions.SessionEnding += OnSessionEndingAsync;
        }

        internal void Start()
        {
            lock (_timerSync)
            {
                ThrowIfDisposed();
                _started = true;
                ScheduleNextTransitionLocked();
            }
        }

        // Management callers must not already own a character transition gate:
        // the ensuing broadcast acquires each recipient's existing gate.
        internal async Task OpenAsync(int dungeonId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
        {
            long revision;
            lock (_timerSync)
            {
                ThrowIfDisposed();
                if (endsAtUtc <= _utcNow())
                    throw new ArgumentOutOfRangeException(nameof(endsAtUtc), "Activity window must end in the future.");
                revision = _service.SetWindow(dungeonId, startsAtUtc, endsAtUtc);
                _started = true;
                ScheduleNextTransitionLocked();
            }
            await PublishRevisionAsync(revision);
        }

        internal async Task CloseAsync(int dungeonId)
        {
            long revision;
            lock (_timerSync)
            {
                ThrowIfDisposed();
                revision = _service.Close(dungeonId);
                _started = true;
                ScheduleNextTransitionLocked();
            }
            await PublishRevisionAsync(revision);
        }

        internal Task RefreshAsync() => PublishRevisionAsync(_service.Revision);

        // Login already holds the character transition gate. Committed entry
        // callers hold the entry/run owner's gate; both use the strong source
        // fence below without acquiring another lifecycle gate.
        internal Task NotifyStateOnLoginWhileTransitionHeldAsync(EnhancedClientSession session)
            => SendProjectionAsync(Capture(session), _service.Revision);

        internal Task NotifyCountsAfterEntryWhileTransitionHeldAsync(EnhancedClientSession session)
            => RefreshCountsWhileTransitionHeldAsync(session);

        internal Task RefreshCountsWhileTransitionHeldAsync(EnhancedClientSession session)
            => SendProjectionAsync(Capture(session), _service.Revision);

        internal async Task OnWindowBoundaryAsync(long expectedRevision)
        {
            if (_disposed || _service.Revision != expectedRevision)
                return;
            try
            {
                await PublishRevisionAsync(expectedRevision);
            }
            finally
            {
                lock (_timerSync)
                    if (!_disposed && _service.Revision == expectedRevision)
                        ScheduleNextTransitionLocked();
            }
        }

        private void ScheduleNextTransitionLocked()
        {
            _timer?.Cancel();
            _timer = null;
            if (!_started || _disposed || _clock == null)
                return;

            var now = _utcNow();
            var revision = _service.Revision;
            var resetUtc = DailyResetService.GetDailyResetBoundaryUtc(now.UtcDateTime);
            if (resetUtc <= now.UtcDateTime)
                resetUtc = resetUtc.AddDays(1);
            DateTimeOffset? next = new DateTimeOffset(DateTime.SpecifyKind(resetUtc, DateTimeKind.Utc));
            foreach (var dungeonId in EventDungeonOpeningService.ManagedDungeonIds)
            {
                var snapshot = _service.GetSnapshot(dungeonId, now);
                if (snapshot.Revision != revision)
                    return;
                if (snapshot.NextTransitionUtc is DateTimeOffset boundary && boundary > now
                    && (!next.HasValue || boundary < next.Value))
                    next = boundary;
            }
            if (next.HasValue && _service.Revision == revision)
                _timer = _clock.ScheduleOneShotAsync(_timerName, next.Value.UtcDateTime,
                    _ => OnWindowBoundaryAsync(revision));
        }

        private async Task PublishRevisionAsync(long expectedRevision)
        {
            if (_disposed || _service.Revision != expectedRevision)
                return;
            foreach (var session in _sessions.GetAllGameSessions())
            {
                if (_disposed || _service.Revision != expectedRevision)
                    return;
                var source = Capture(session);
                if (source == null)
                    continue;
                await _transitions.RunIfCurrentAsync(session,
                    () => SendProjectionAsync(source, expectedRevision));
            }
        }

        private async Task SendProjectionAsync(ProjectionIdentity source, long expectedRevision)
        {
            if (!IsCurrent(source) || _service.Revision != expectedRevision)
                return;

            // The existing limit owner reads the database before the send lock.
            // A failed/mismatched count never becomes an invented zero or open gate.
            var now = _utcNow();
            var dayId = DailyResetService.TodayId(now.UtcDateTime);
            var counts = LoadCounts(source, dayId);
            if (!IsCurrent(source) || _service.Revision != expectedRevision)
                return;

            EventDungeonOpeningSnapshot preparedTau = null;
            EventDungeonOpeningSnapshot preparedSilver = null;
            StateProjectionKey preparedStates = null;
            CountProjectionKey preparedCounts = null;
            var queued = false;
            var sourceExpired = false;

            bool CanWrite()
            {
                // Preparation precedes each real guard. Once a queued batch
                // loses its source/window/day, recovery requires a new batch.
                if (!queued || sourceExpired)
                    return false;
                var currentNow = _utcNow();
                var current = IsCurrent(source) && _service.Revision == expectedRevision
                    && DailyResetService.TodayId(currentNow.UtcDateTime) == dayId
                    && MatchesWindow(preparedTau,
                        _service.GetSnapshot(EventDungeonOpeningService.TauDungeonId, currentNow))
                    && MatchesWindow(preparedSilver,
                        _service.GetSnapshot(EventDungeonOpeningService.GoldSilverDungeonId, currentNow));
                sourceExpired = !current;
                return current;
            }

            try
            {
                await _sendBatch(source.Session, () =>
                {
                    if (!IsCurrent(source) || _service.Revision != expectedRevision)
                        return Array.Empty<byte[]>();
                    var projectionTime = _utcNow();
                    if (DailyResetService.TodayId(projectionTime.UtcDateTime) != dayId)
                        return Array.Empty<byte[]>();
                    var tau = _service.GetSnapshot(EventDungeonOpeningService.TauDungeonId, projectionTime);
                    var silver = _service.GetSnapshot(EventDungeonOpeningService.GoldSilverDungeonId, projectionTime);
                    if (tau.Revision != expectedRevision || silver.Revision != expectedRevision)
                        return Array.Empty<byte[]>();

                    var packets = new List<byte[]>();
                    // Count recovery must also restore an entry previously
                    // projected closed. Dedup keeps ordinary count refreshes
                    // from replaying unchanged opening states.
                    var stateKey = new StateProjectionKey(source, expectedRevision, tau.WireState,
                        counts.Valid ? silver.WireState : 0, tau.ProjectionSegment, silver.ProjectionSegment);
                    lock (_notificationSync)
                    {
                        if (!_lastStates.TryGetValue(source.Session.SessionId, out var last) || !last.Matches(stateKey))
                        {
                            packets.Add(GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE,
                                EventDungeonOpeningBodyBuilder.Build(tau)));
                            packets.Add(GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE,
                                counts.Valid ? EventDungeonOpeningBodyBuilder.Build(silver)
                                    : EventDungeonOpeningBodyBuilder.BuildClosed(EventDungeonOpeningService.GoldSilverDungeonId)));
                            preparedStates = stateKey;
                        }
                    }
                    if (counts.Valid)
                    {
                        var countKey = new CountProjectionKey(source, expectedRevision, dayId, counts.GoldUsed, counts.SilverUsed);
                        lock (_notificationSync)
                        {
                            if (!_lastCounts.TryGetValue(source.Session.SessionId, out var last) || !last.Matches(countKey))
                            {
                                packets.Add(GamePacketEnvelopeBuilder.Build(0,
                                    (ushort)NotiPacketTypeA21.GOLD_SILVER_HORN_INFO,
                                    EventDungeonOpeningBodyBuilder.BuildGoldSilverCounts(counts.GoldUsed, counts.SilverUsed)));
                                preparedCounts = countKey;
                            }
                        }
                    }
                    preparedTau = tau;
                    preparedSilver = silver;
                    queued = packets.Count != 0;
                    return packets;
                }, CanWrite);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                or OperationCanceledException or InvalidOperationException or ArgumentException or OverflowException)
            {
                // A partial batch has no completion receipt. Safe projections can
                // be retried; neither this path nor a retry reconsumes entry counts.
                FileLogger.Log($"[EventDungeonOpening] projection failed cid={source.CharacterId} revision={expectedRevision}: {exception.Message}");
                return;
            }

            if (!CanWrite())
                return;
            lock (_notificationSync)
            {
                if (!IsCurrent(source) || _service.Revision != expectedRevision)
                    return;
                if (preparedStates != null) _lastStates[source.Session.SessionId] = preparedStates;
                if (preparedCounts != null) _lastCounts[source.Session.SessionId] = preparedCounts;
            }
            FileLogger.Log($"[EventDungeonOpening] projected cid={source.CharacterId} revision={expectedRevision} "
                + $"tau={preparedTau.WireState}/{preparedTau.RemainingSeconds}s segment={preparedTau.ProjectionSegment} "
                + $"silver={(counts.Valid ? preparedSilver.WireState : 0)}/{(counts.Valid ? preparedSilver.RemainingSeconds : 0)}s "
                + $"segment={preparedSilver.ProjectionSegment} counts={(counts.Valid ? $"{counts.GoldUsed}/{counts.SilverUsed}" : "unavailable")} "
                + $"reason={counts.Reason} clientEntryExit=manual_pending");
        }

        private CountsProjection LoadCounts(ProjectionIdentity source, int dayId)
        {
            try
            {
                var configuration = _service.GoldSilverConfiguration;
                var limits = _loadLimits(source.AccountId, source.CharacterId);
                if (!GoldSilverHornCountProjection.TryCreate(configuration, limits,
                        out var goldUsed, out var silverUsed, out var reason))
                    return CountsProjection.Unavailable(reason);
                foreach (var limit in limits)
                    if (limit != null && (limit.DungeonId == configuration.Gold?.DungeonId
                            || limit.DungeonId == configuration.Silver?.DungeonId)
                        && limit.DayId != dayId)
                        return CountsProjection.Unavailable("entry_count_day_changed");
                return new CountsProjection(true, goldUsed, silverUsed, "owner_snapshot");
            }
            catch (Exception exception) when (exception is SqliteException or IOException or ObjectDisposedException
                or InvalidOperationException or ArgumentException or OverflowException)
            {
                FileLogger.Log($"[EventDungeonOpening] count read failed cid={source.CharacterId}: {exception.Message}");
                return CountsProjection.Unavailable("entry_count_read_failed");
            }
        }

        private ProjectionIdentity Capture(EnhancedClientSession session)
        {
            var account = session?.Account;
            var player = session?.Player;
            var generation = session?.GameSession;
            if (_disposed || account == null || account.AccountId <= 0 || player == null
                || player.CharacterId <= 0 || player.UserId == 0 || generation == null)
                return null;
            var source = new ProjectionIdentity(session, account, player, generation,
                account.AccountId, player.CharacterId, player.UserId,
                player.CurrentDungeonRunGeneration, player.CurrentDungeonSelectionGeneration);
            return IsCurrent(source) ? source : null;
        }

        private bool IsCurrent(ProjectionIdentity source)
            => !_disposed && source != null
                && ReferenceEquals(source.Session.Account, source.Account)
                && source.Account.AccountId == source.AccountId
                && ReferenceEquals(source.Session.Player, source.Player)
                && source.Player.CharacterId == source.CharacterId && source.Player.UserId == source.UserId
                && ReferenceEquals(source.Session.GameSession, source.Generation)
                && source.Player.CurrentDungeonRunGeneration == source.RunGeneration
                && source.Player.CurrentDungeonSelectionGeneration == source.SelectionGeneration
                && _sessions.TryGet(source.CharacterId, out var current) && ReferenceEquals(current, source.Session);

        private static bool MatchesWindow(EventDungeonOpeningSnapshot expected, EventDungeonOpeningSnapshot current)
            => expected != null && current != null && expected.Revision == current.Revision
                && expected.DungeonId == current.DungeonId && expected.WireState == current.WireState
                && expected.ProjectionSegment == current.ProjectionSegment;

        private Task OnSessionEndingAsync(int characterId, EnhancedClientSession session)
        {
            if (session != null)
                lock (_notificationSync)
                {
                    _lastStates.Remove(session.SessionId);
                    _lastCounts.Remove(session.SessionId);
                }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_timerSync)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Cancel();
                _timer = null;
            }
            _sessions.SessionEnding -= OnSessionEndingAsync;
            lock (_notificationSync)
            {
                _lastStates.Clear();
                _lastCounts.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EventDungeonOpeningCoordinator));
        }

        private sealed record CountsProjection(bool Valid, ushort GoldUsed, ushort SilverUsed, string Reason)
        {
            internal static CountsProjection Unavailable(string reason) => new(false, 0, 0, reason);
        }

        private sealed record ProjectionIdentity(EnhancedClientSession Session, AccountRecord Account,
            PlayerContext Player, GameSession Generation, int AccountId, int CharacterId, ushort UserId,
            long RunGeneration, long SelectionGeneration)
        {
            internal bool Matches(ProjectionIdentity other) => other != null
                && ReferenceEquals(Session, other.Session) && ReferenceEquals(Account, other.Account)
                && ReferenceEquals(Player, other.Player) && ReferenceEquals(Generation, other.Generation)
                && AccountId == other.AccountId && CharacterId == other.CharacterId && UserId == other.UserId;
        }

        private sealed record StateProjectionKey(ProjectionIdentity Source, long Revision,
            int TauState, int SilverState, long TauSegment, long SilverSegment)
        {
            internal bool Matches(StateProjectionKey other) => Source.Matches(other.Source)
                && Revision == other.Revision && TauState == other.TauState && SilverState == other.SilverState
                && TauSegment == other.TauSegment && SilverSegment == other.SilverSegment;
        }

        private sealed record CountProjectionKey(ProjectionIdentity Source, long Revision, int DayId,
            ushort GoldUsed, ushort SilverUsed)
        {
            internal bool Matches(CountProjectionKey other) => Source.Matches(other.Source)
                && Revision == other.Revision && DayId == other.DayId
                && GoldUsed == other.GoldUsed && SilverUsed == other.SilverUsed;
        }
    }
}
