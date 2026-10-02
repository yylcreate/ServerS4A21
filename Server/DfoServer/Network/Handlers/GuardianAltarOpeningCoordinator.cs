using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    // Entry/menu projection only. Dungeon combat, ticket consumption and
    // rewards are not owned by this coordinator.
    internal sealed class GuardianAltarOpeningCoordinator : IDisposable
    {
        private readonly GuardianAltarOpeningService _service;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ClockService _clock;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly IGuardianAltarProgressService _progress;
        private readonly Func<GuardianAltarMenuDefinition> _definition;
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
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Task> sendBatch = null,
            IGuardianAltarProgressService progress = null,
            Func<GuardianAltarMenuDefinition> definition = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _clock = clock;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _progress = progress;
            _definition = definition;
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

        internal void RegisterEntryHandlers(
            GameCommandRegistry.GameCommandRegistrationGroup handlers,
            Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            ArgumentNullException.ThrowIfNull(hasActiveTrade);
            ArgumentNullException.ThrowIfNull(mercenaryRestrictions);
            handlers[(ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME] =
                (session, header, body) => HandleStartGameAsync(
                    session, header, body, hasActiveTrade, mercenaryRestrictions);
            handlers[(ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM] =
                (session, header, body) => HandleBuyItemAsync(
                    session, header, body, hasActiveTrade, mercenaryRestrictions);
        }

        internal void ConfigureDungeonEntry(DungeonHandler dungeon,
            Func<EnhancedClientSession, bool> hasTrade, IMercenaryRestrictionService mercenaries)
        {
            if (_progress != null && _definition != null)
                dungeon.ConfigureGuardianAltarEntry(_service, _progress, _definition,
                    _transitions, hasTrade, mercenaries);
        }

        private async Task HandleStartGameAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME)
                return;
            var characterId = session?.Player?.CharacterId ?? 0;
            var userId = session?.Player?.UserId ?? 0;
            var generation = session?.GameSession;
            if (userId == 0 || !IsCurrentGeneration(session, characterId, generation))
                return;
            var validRequest = GuardianAltarStartGameRequestParser.TryParse(body, out _);

            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                GuardianAltarEntryRejection? preparedReason = null;
                var preparedMenu = false;
                GuardianAltarProgressSnapshot menu = null;
                GuardianAltarEntryRejection? menuFailure = null;
                var menuAnchor = _utcNow();
                var menuRevision = _service.Revision;
                if (_progress != null && validRequest
                    && IsCurrentGeneration(session, characterId, generation)
                    && session.Player.UserId == userId)
                {
                    try
                    {
                        var opening = _service.GetSnapshot(menuAnchor);
                        var townReady = IsTownReady(session);
                        var trading = opening.IsOpen && townReady && hasActiveTrade(session);
                        var assigned = opening.IsOpen && townReady && !trading
                            && mercenaryRestrictions.IsAssigned(characterId);
                        if (GuardianAltarEntryRules.Evaluate(_service.GetSnapshot(_utcNow()),
                                session.Player.Level, IsTownReady(session), trading, assigned)
                                == GuardianAltarEntryRejection.ProgressAuthorityUnavailable)
                        {
                            // Database I/O and atomic first-use/refill happen before
                            // acquiring the send lock. The transaction is fenced to
                            // this identity/window and is rechecked by the owner.
                            menu = _progress.LoadMenu(characterId, menuAnchor.UtcDateTime,
                                () => IsCurrentGeneration(session, characterId, generation)
                                    && session.Player.UserId == userId
                                    && _service.Revision == menuRevision
                                    && _service.GetSnapshot(_utcNow()).IsOpen
                                    && Game.DailyReset.DailyResetService.TodayId(_utcNow().UtcDateTime)
                                        == Game.DailyReset.DailyResetService.TodayId(menuAnchor.UtcDateTime)
                                    && IsTownReady(session)
                                    && session.Player.Level >= GuardianAltarEntryRules.MinimumLevel);
                            if (menu.CharacterId != characterId)
                                throw new InvalidOperationException("Guardian menu belongs to another character.");
                            GuardianAltarProgressValidator.Validate(menu);
                        }
                    }
                    catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                        or InvalidDataException or IOException or OverflowException)
                    {
                        menuFailure = GuardianAltarEntryRejection.StateReadFailed;
                        FileLogger.Log($"[GuardianAltarEntry] menu load failed cid={characterId}: {ex}");
                    }
                }
                try
                {
                    await _sendBatch(session, () =>
                    {
                        if (!IsCurrentGeneration(session, characterId, generation)
                            || session.Player.UserId != userId)
                            return Array.Empty<byte[]>();

                        var reason = GuardianAltarEntryRejection.InvalidRequest;
                        if (validRequest)
                        {
                            var opening = _service.GetSnapshot(_utcNow());
                            var player = session.Player;
                            var townReady = SessionDirectory.IsTownPresence(player)
                                && !player.DungeonSelectionPending
                                && player.CurrentDungeonSelection == null;
                            try
                            {
                                var trading = opening.IsOpen && townReady && hasActiveTrade(session);
                                var assigned = opening.IsOpen && townReady && !trading
                                    && mercenaryRestrictions.IsAssigned(characterId);
                                // A repository read may outlive the window or a displaced
                                // session. Recheck immediately before projecting the reply.
                                if (!IsCurrentGeneration(session, characterId, generation)
                                    || session.Player.UserId != userId)
                                    return Array.Empty<byte[]>();
                                opening = _service.GetSnapshot(_utcNow());
                                townReady = SessionDirectory.IsTownPresence(player)
                                    && !player.DungeonSelectionPending
                                    && player.CurrentDungeonSelection == null;
                                reason = GuardianAltarEntryRules.Evaluate(opening, player.Level,
                                    townReady, trading, assigned);
                            }
                            catch (SqliteException ex)
                            {
                                reason = GuardianAltarEntryRejection.StateReadFailed;
                                FileLogger.Log($"[GuardianAltarEntry] state read failed cid={characterId}: {ex}");
                            }
                        }
                        // Also cover a state reader that displaced this session
                        // before throwing, and the malformed-request path.
                        if (!IsCurrentGeneration(session, characterId, generation)
                            || session.Player.UserId != userId)
                            return Array.Empty<byte[]>();
                        if (reason == GuardianAltarEntryRejection.ProgressAuthorityUnavailable
                            && menu != null && !menuFailure.HasValue
                            && _service.Revision == menuRevision
                            && Game.DailyReset.DailyResetService.TodayId(_utcNow().UtcDateTime)
                                == Game.DailyReset.DailyResetService.TodayId(menuAnchor.UtcDateTime))
                        {
                            preparedMenu = true;
                            return new[]
                            {
                                GamePacketEnvelopeBuilder.Build(1,
                                    (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME,
                                    GuardianAltarMenuBodyBuilder.Build(menu))
                            };
                        }
                        if (reason == GuardianAltarEntryRejection.ProgressAuthorityUnavailable
                            && menuFailure.HasValue)
                            reason = menuFailure.Value;
                        else if (reason == GuardianAltarEntryRejection.ProgressAuthorityUnavailable
                            && menu != null)
                            reason = GuardianAltarEntryRejection.MenuSnapshotExpired;
                        preparedReason = reason;
                        var ack = GamePacketEnvelopeBuilder.Build(1,
                                (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME,
                                GuardianAltarEntryAckBodyBuilder.BuildRejection(reason));
                        if (_progress != null && (reason is GuardianAltarEntryRejection.StateReadFailed
                            or GuardianAltarEntryRejection.MenuSnapshotExpired
                            or GuardianAltarEntryRejection.ProgressAuthorityUnavailable))
                        {
                            // The generic error ACK has no Guardian UI text. Reuse
                            // the established notice projection, not a guessed
                            // SHOW_DIALOG or a false activity-closed result.
                            return new[] { ack, GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                                ServerNoticeMessageBuilder.Build("守护者祭坛状态暂不可用，请重新进入活动。")) };
                        }
                        return new[] { ack };
                    });
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[GuardianAltarEntry] request reply failed cid={characterId}: {ex.Message}");
                    return;
                }
                if (preparedReason.HasValue
                    && IsCurrentGeneration(session, characterId, generation)
                    && session.Player.UserId == userId)
                    FileLogger.Log($"[GuardianAltarEntry] START_GAME rejected cid={characterId} "
                        + $"reason={preparedReason.Value} entryOnly=true");
                else if (preparedMenu && menu != null && IsCurrentGeneration(session, characterId, generation)
                    && session.Player.UserId == userId)
                    FileLogger.Log($"[GuardianAltarEntry] START_GAME menu projected cid={characterId} "
                        + $"ridable={menu.Header.RidableId} stages={menu.Stages.Count} "
                        + $"tickets={menu.Header.FreeTickets}+{menu.Header.SecondaryTickets} combatAdmission=false");
            });
        }

        private static bool IsTownReady(EnhancedClientSession session)
            => SessionDirectory.IsTownPresence(session?.Player)
                && !session.Player.DungeonSelectionPending
                && session.Player.CurrentDungeonSelection == null;

        private async Task HandleBuyItemAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM)
                return;
            var characterId = session?.Player?.CharacterId ?? 0;
            var userId = session?.Player?.UserId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            var generation = session?.GameSession;
            bool IsIdentityCurrent() => IsCurrentGeneration(session, characterId, generation)
                && session.Player.UserId == userId && session.Account?.AccountId == accountId;
            if (userId == 0 || accountId <= 0 || !IsIdentityCurrent())
                return;
            var validRequest = GuardianAltarPurchaseRequestParser.TryParse(body, out var request);
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                var revision = _service.Revision;
                GuardianAltarPurchaseResult committed = null;
                GuardianAltarPurchaseOutcome? rejection = null;
                var unavailable = false;
                if (validRequest && IsIdentityCurrent())
                {
                    try
                    {
                        var opening = _service.GetSnapshot(_utcNow());
                        if (!opening.IsOpen)
                            rejection = GuardianAltarPurchaseOutcome.Closed;
                        else if (!IsTownReady(session)
                            || session.Player.Level < GuardianAltarEntryRules.MinimumLevel
                            || hasActiveTrade(session) || mercenaryRestrictions.IsAssigned(characterId))
                            rejection = GuardianAltarPurchaseOutcome.CannotPurchase;
                        else if (_progress is IGuardianAltarPurchaseService purchases)
                        {
                            // This is the existing progress owner's single role
                            // transaction, outside the packet send lock. No ticket
                            // refill, Inventory mutation or combat-plan mutation.
                            committed = purchases.Buy(characterId, accountId, request,
                                () => !hasActiveTrade(session) && IsIdentityCurrent() && _service.Revision == revision
                                    && _service.GetSnapshot(_utcNow()).IsOpen && IsTownReady(session)
                                    && session.Player.Level >= GuardianAltarEntryRules.MinimumLevel);
                            if (committed?.Progress?.CharacterId != characterId)
                                throw new InvalidOperationException("Guardian purchase result has no current character owner.");
                            GuardianAltarProgressValidator.Validate(committed.Progress);
                            var learned = committed.Progress.Purchases.SingleOrDefault(x =>
                                x.ShopType == request.ShopType && x.ResourceId == request.ResourceId);
                            if (committed.NewLevel != (learned?.Level ?? 0)
                                || (committed.Outcome == GuardianAltarPurchaseOutcome.Success
                                    && (committed.Progress.Header.RidableId != request.RidableId
                                        || committed.NewLevel != request.TargetLevel)))
                                throw new InvalidOperationException("Guardian purchase result disagrees with its durable snapshot.");
                        }
                        else
                            unavailable = true;
                    }
                    catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                        or InvalidDataException or IOException or OverflowException)
                    {
                        unavailable = true;
                        FileLogger.Log($"[GuardianAltarPurchase] state/commit failed cid={characterId}: {ex}");
                    }
                }
                try
                {
                    await _sendBatch(session, () =>
                    {
                        if (!IsIdentityCurrent())
                            return Array.Empty<byte[]>();
                        var expired = validRequest && _service.Revision != revision;
                        var invalidPresence = validRequest && !IsTownReady(session);
                        if (!unavailable && !expired && !invalidPresence && validRequest)
                        {
                            try
                            {
                                // Sending cannot open another purchase transaction.
                                // A state change after commit requires a fresh menu
                                // projection; it never refunds or repeats the debit.
                                if (committed != null && (!_service.GetSnapshot(_utcNow()).IsOpen
                                    || session.Player.Level < GuardianAltarEntryRules.MinimumLevel
                                    || hasActiveTrade(session) || mercenaryRestrictions.IsAssigned(characterId)))
                                    expired = true;
                                if (!IsIdentityCurrent())
                                    return Array.Empty<byte[]>();
                                expired |= _service.Revision != revision
                                    || (committed != null && (!_service.GetSnapshot(_utcNow()).IsOpen
                                        || !IsTownReady(session)));
                                if (!expired && (committed != null || rejection.HasValue))
                                {
                                    var ack = GuardianAltarPurchaseAckBodyBuilder.Build(request,
                                        committed?.Outcome ?? rejection.Value,
                                        committed?.NewLevel ?? 0,
                                        committed?.Progress.Header.AvailableStars ?? 0);
                                    return new[] { GamePacketEnvelopeBuilder.Build(1,
                                        (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM, ack) };
                                }
                            }
                            catch (SqliteException ex)
                            {
                                unavailable = true;
                                FileLogger.Log($"[GuardianAltarPurchase] reply state read failed cid={characterId}: {ex}");
                            }
                        }
                        if (!IsIdentityCurrent())
                            return Array.Empty<byte[]>();
                        var failure = GamePacketEnvelopeBuilder.Build(1,
                            (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM,
                            GuardianAltarPurchaseAckBodyBuilder.BuildRejected());
                        if (unavailable || expired)
                            return new[] { failure, GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                                ServerNoticeMessageBuilder.Build("守护者祭坛购买状态暂不可用，请重新进入活动。")) };
                        return new[] { failure };
                    });
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[GuardianAltarPurchase] reply failed cid={characterId}: {ex.Message}");
                }
                if (committed != null)
                    FileLogger.Log($"[GuardianAltarPurchase] BUY_ITEM cid={characterId} type={request.ShopType} "
                        + $"resource={request.ResourceId} levels={request.StartLevel}..{request.TargetLevel} "
                        + $"result={committed.Outcome} changed={committed.Changed} "
                        + $"stars={committed.Progress.Header.AvailableStars} reason={committed.RejectionReason}");
            });
        }

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
