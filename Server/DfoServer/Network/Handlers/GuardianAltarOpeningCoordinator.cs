using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    // Entry-only projection. This does not admit a player, deduct tickets or ACK START_GAME.
    internal sealed class GuardianAltarOpeningCoordinator : IDisposable
    {
        private readonly GuardianAltarOpeningService _service;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ClockService _clock;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<EnhancedClientSession,
            Func<IReadOnlyList<byte[]>>, Task> _sendBatch;
        private readonly object _timerSync = new object();
        private readonly object _notificationSync = new object();
        private readonly Dictionary<Guid, ProjectionKey> _lastSent = new();
        private readonly string _timerName = "guardian-altar-entry:" + Guid.NewGuid();
        private ClockService.ClockTimerHandle _timer;
        private volatile bool _disposed;
        private bool _started;

        internal GuardianAltarOpeningCoordinator(
            GuardianAltarOpeningService service,
            ISessionDirectory sessions,
            CharacterTransitionCoordinator transitions,
            ClockService clock,
            Func<DateTimeOffset> utcNow = null,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Task> sendBatch = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _clock = clock;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _sendBatch = sendBatch ?? ((session, prepare) =>
                session.SendPreparedPacketBatchAsync(prepare));
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

        // Management entry point: do not await while holding a character transition
        // gate, because the broadcast acquires each online character's existing gate.
        internal async Task OpenAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
        {
            long revision;
            lock (_timerSync)
            {
                ThrowIfDisposed();
                if (endsAtUtc <= _utcNow())
                    throw new ArgumentOutOfRangeException(nameof(endsAtUtc),
                        "The entry test window must end in the future.");
                revision = _service.SetWindow(startsAtUtc, endsAtUtc);
                _started = true;
                ScheduleNextTransitionLocked();
            }
            await PublishRevisionAsync(revision);
        }

        // Same management/gate constraint as OpenAsync; login uses the separate path below.
        internal async Task CloseAsync()
        {
            long revision;
            lock (_timerSync)
            {
                ThrowIfDisposed();
                revision = _service.Close();
                ScheduleNextTransitionLocked();
            }
            await PublishRevisionAsync(revision);
        }

        internal Task RefreshAsync() => PublishRevisionAsync(_service.Revision);

        // SELECT_CHARACTER already owns the character transition gate here.
        // Acquiring that same SemaphoreSlim again would deadlock the login chain.
        internal Task NotifyStateOnLoginWhileTransitionHeldAsync(EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            var generation = session?.GameSession;
            return SendCurrentAsync(session, characterId, generation, _service.Revision);
        }

        internal async Task OnWindowBoundaryAsync(long expectedRevision)
        {
            if (_disposed || _service.Revision != expectedRevision)
                return;

            // A timer only requests projection; current UTC/window remains authoritative.
            await PublishRevisionAsync(expectedRevision);
            lock (_timerSync)
            {
                if (!_disposed && _service.Revision == expectedRevision)
                    ScheduleNextTransitionLocked();
            }
        }

        private void ScheduleNextTransitionLocked()
        {
            _timer?.Cancel();
            _timer = null;
            if (!_started || _clock == null || _disposed)
                return;

            var snapshot = _service.GetSnapshot(_utcNow());
            if (snapshot.NextTransitionUtc is not DateTimeOffset next)
                return;

            var revision = snapshot.Revision;
            _timer = _clock.ScheduleOneShotAsync(
                _timerName,
                next.UtcDateTime,
                _ => OnWindowBoundaryAsync(revision));
        }

        private async Task PublishRevisionAsync(long expectedRevision)
        {
            if (_disposed || _service.Revision != expectedRevision)
                return;

            foreach (var session in _sessions.GetAllGameSessions())
            {
                var characterId = session?.Player?.CharacterId ?? 0;
                var generation = session?.GameSession;
                if (characterId <= 0 || generation == null)
                    continue;

                await _transitions.RunIfCurrentAsync(session, () =>
                    SendCurrentAsync(session, characterId, generation, expectedRevision));
            }
        }

        private async Task SendCurrentAsync(
            EnhancedClientSession session,
            int characterId,
            GameSession generation,
            long expectedRevision)
        {
            if (!IsCurrentGeneration(session, characterId, generation))
                return;

            ProjectionKey prepared = null;
            int remainingSeconds = 0;
            try
            {
                await _sendBatch(session, () =>
                {
                    if (!IsCurrentGeneration(session, characterId, generation))
                        return Array.Empty<byte[]>();

                    var latest = _service.GetSnapshot(_utcNow());
                    if (latest.Revision != expectedRevision)
                        return Array.Empty<byte[]>();

                    var key = new ProjectionKey(generation, characterId,
                        latest.Revision, latest.WireState);
                    lock (_notificationSync)
                    {
                        if (_lastSent.TryGetValue(session.SessionId, out var last)
                            && last.Matches(key))
                            return Array.Empty<byte[]>();
                    }

                    prepared = key;
                    remainingSeconds = latest.RemainingSeconds;
                    return new[]
                    {
                        GamePacketEnvelopeBuilder.Build(0x00,
                            (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE,
                            GuardianAltarOpeningBodyBuilder.Build(latest))
                    };
                });
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                FileLogger.Log($"[GuardianAltarEntry] state send failed cid={characterId}: {ex.Message}");
                return;
            }

            if (prepared == null || _service.Revision != prepared.Revision
                || !IsCurrentGeneration(session, characterId, generation))
                return;
            lock (_notificationSync)
                _lastSent[session.SessionId] = prepared;
            FileLogger.Log($"[GuardianAltarEntry] entryOnly=true cid={characterId} "
                + $"state={prepared.State} remainingSeconds={remainingSeconds} revision={prepared.Revision}");
        }

        private bool IsCurrentGeneration(EnhancedClientSession session,
            int characterId, GameSession generation)
            => !_disposed && characterId > 0 && generation != null
                && session?.Player?.CharacterId == characterId
                && ReferenceEquals(session.GameSession, generation)
                && _sessions.TryGet(characterId, out var current)
                && ReferenceEquals(current, session);

        private Task OnSessionEndingAsync(int characterId, EnhancedClientSession session)
        {
            if (session != null)
                lock (_notificationSync)
                    _lastSent.Remove(session.SessionId);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_timerSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _timer?.Cancel();
                _timer = null;
            }
            _sessions.SessionEnding -= OnSessionEndingAsync;
            lock (_notificationSync)
                _lastSent.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GuardianAltarOpeningCoordinator));
        }

        private sealed record ProjectionKey(GameSession Generation,
            int CharacterId, long Revision, int State)
        {
            internal bool Matches(ProjectionKey other)
                => ReferenceEquals(Generation, other.Generation)
                    && CharacterId == other.CharacterId
                    && Revision == other.Revision && State == other.State;
        }
    }
}
