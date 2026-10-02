using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    /// <summary>
    /// Owns the shared dungeon loading barrier and its bounded timeout.
    /// GameProtocolHandler only registers the wire entry points.
    /// </summary>
    internal sealed class DungeonLoadingCoordinator
    {
        private static readonly TimeSpan LoadingTimeout =
            TimeSpan.FromSeconds(45);

        private readonly TownHandler _town;
        private readonly DungeonHandler _dungeon;
        private readonly DungeonInstanceRegistry _instances;
        private readonly ISessionDirectory _sessions;
        private readonly RaidHandler _raid;

        internal DungeonLoadingCoordinator(
            TownHandler town,
            DungeonHandler dungeon,
            DungeonInstanceRegistry instances,
            ISessionDirectory sessions,
            RaidHandler raid)
        {
            _town = town ?? throw new ArgumentNullException(nameof(town));
            _dungeon = dungeon
                ?? throw new ArgumentNullException(nameof(dungeon));
            _instances = instances
                ?? throw new ArgumentNullException(nameof(instances));
            _sessions = sessions
                ?? throw new ArgumentNullException(nameof(sessions));
            _raid = raid ?? throw new ArgumentNullException(nameof(raid));

            _dungeon.ConfigureLoadingProjectionStarted(
                ScheduleLoadingTimeout,
                HandleLoadingProjectionRejectedAsync);
        }

        internal async Task HandleFinishLoadingAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var sourceCurrent = CaptureFinishLoadingSourceFence(session, _sessions, run);
            if (run?.Tower != null)
            {
                await HandleDeathTowerFinishLoadingAsync(session, run);
            }
            else if (run == null)
            {
                await _town.Handle_ENUM_CMDPACKET_FINISH_LOADING(
                    session,
                    header,
                    body);
            }
            else
            {
                await HandleDungeonFinishLoadingAsync(session, run);
            }

            if (sourceCurrent())
                await _raid.HandleDungeonLoadedAsync(session);
        }

        private async Task HandleDeathTowerFinishLoadingAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var identity = run.CaptureIdentity();
            var tower = run.Tower;
            var stage = tower?.CurrentStage ?? -1;
            if (session?.Player == null
                || tower == null
                || !identity.IsValid
                || !ReferenceEquals(session.Player.CurrentRun, run)
                || !session.Player.IsCurrentDungeonRun(identity)
                || !ReferenceEquals(run.Tower, tower)
                || !tower.TryConsumeStageLoadingRelease(identity, stage))
            {
                FileLogger.Log(
                    "[DeathTower] FINISH_LOADING ignored stale/duplicate: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"run={identity.RunId}/{identity.RunGeneration} " +
                    $"stage={stage}");
                return;
            }

            var canContinue = CaptureContinuationFence(session, _sessions, run,
                additional: () => ReferenceEquals(run.Tower, tower) && tower.CurrentStage == stage);
            if (!await _town.TrySendFinishLoadingCompletionAsync(session, canContinue))
                return;
            FileLogger.Log(
                "[DeathTower] SENT FINISH_LOADING release after client ready: " +
                $"cid={session.Player.CharacterId} " +
                $"run={identity.RunId}/{identity.RunGeneration} " +
                $"stage={stage}");
        }

        internal async Task HandleGiveupGameAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var dungeonId = run?.DungeonId ?? 0;
            await _town.Handle_ENUM_CMDPACKET_GIVEUP_GAME(
                session,
                header,
                body);
            if (run != null && session?.Player?.CurrentRun == null)
            {
                await _raid.HandleDungeonAbortedAsync(
                    session,
                    dungeonId,
                    "giveup");
            }
        }

        private async Task HandleDungeonFinishLoadingAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var participantRoom = run.CaptureParticipantRoomIdentity();
            if (!participantRoom.IsValid
                || !run.Instance.TryGetRoom(
                    participantRoom.Room.RoomInstanceId,
                    out var room))
            {
                FileLogger.Log(
                    "[DungeonLoading] fallback: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"run={run?.RunId ?? 0} " +
                    $"room={run?.CurrentRoomInstanceId ?? 0}");
                await _town.TrySendFinishLoadingCompletionAsync(session,
                    CaptureContinuationFence(session, _sessions, run,
                        participantRoom.IsValid ? participantRoom.Room : null));
                return;
            }

            var activeParticipants = CaptureLoadingParticipants(
                participantRoom.Room.Instance);
            var ready = room.MarkLoadingReady(
                participantRoom.Run,
                activeParticipants);
            if (!ready.Accepted)
            {
                FileLogger.Log(
                    "[DungeonLoading] ignored stale/duplicate: " +
                    $"cid={session.Player.CharacterId} " +
                    $"instance={participantRoom.Room.Instance.PartyDungeonInstanceId} " +
                    $"room={participantRoom.Room.RoomInstanceId}");
                return;
            }

            FileLogger.Log(
                "[DungeonLoading] ready: " +
                $"cid={session.Player.CharacterId} " +
                $"instance={participantRoom.Room.Instance.PartyDungeonInstanceId} " +
                $"room={participantRoom.Room.RoomInstanceId} " +
                $"generation={ready.Generation} release={ready.Released}");
            if (ready.Released)
            {
                await SendLoadingCompletionAsync(
                    participantRoom.Room,
                    ready.Generation,
                    ready.Participants,
                    "all-ready");
            }
        }

        private List<DungeonRunIdentity> CaptureLoadingParticipants(
            DungeonInstanceIdentity instanceIdentity)
        {
            var result = new List<DungeonRunIdentity>();
            foreach (var participant in _instances
                         .CaptureInstanceParticipantRoster(instanceIdentity))
            {
                if (participant.RunIdentity.IsValid
                    && !result.Contains(participant.RunIdentity))
                {
                    result.Add(participant.RunIdentity);
                }
            }
            return result;
        }

        private async Task SendLoadingCompletionAsync(
            DungeonRoomIdentity roomIdentity,
            long loadingGeneration,
            IReadOnlyList<DungeonRunIdentity> participants,
            string reason)
        {
            if (!roomIdentity.IsValid || participants == null)
                return;

            var releases = new List<Task<bool>>();
            var roster = _instances.CaptureParticipantRoster(roomIdentity);
            foreach (var participant in roster)
            {
                if (!ContainsRunIdentity(
                        participants,
                        participant.RunIdentity)
                    || !_sessions.TryGet(
                        participant.CharacterId,
                        out var candidate)
                    || candidate?.Player == null
                    || candidate.TcpClient == null
                    || !candidate.TcpClient.Connected
                    || !candidate.Player.IsCurrentDungeonParticipantRoom(
                        new DungeonParticipantRoomIdentity(
                            participant.RunIdentity,
                            roomIdentity)))
                {
                    continue;
                }

                var instance = participant.Run.Instance;
                if (!instance.TryGetRoom(roomIdentity.RoomInstanceId, out var room))
                    continue;
                var canContinue = CaptureContinuationFence(candidate, _sessions,
                    participant.Run, roomIdentity,
                    () => room.IsCurrentLoadingGeneration(loadingGeneration));
                if (!canContinue())
                    continue;
                DungeonMechanismTimerCoordinator.StartElevator(
                    instance, room, _instances, _sessions);
                releases.Add(TrySendLoadingCompletionAsync(candidate, canContinue));
            }

            var releaseResults = await Task.WhenAll(releases);
            var releasedCount = 0;
            foreach (var released in releaseResults)
            {
                if (released)
                    releasedCount++;
            }
            FileLogger.Log(
                "[DungeonLoading] released: " +
                $"instance={roomIdentity.Instance.PartyDungeonInstanceId} " +
                $"room={roomIdentity.RoomInstanceId} " +
                $"reason={reason} " +
                $"participants={releasedCount}/{participants.Count}");
        }

        private async Task<bool> TrySendLoadingCompletionAsync(
            EnhancedClientSession session,
            Func<bool> canContinue)
        {
            try
            {
                return await _town.TrySendFinishLoadingCompletionAsync(session, canContinue);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DungeonLoading] release failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"error={ex.Message}");
                return false;
            }
        }

        private void ScheduleLoadingTimeout(
            DungeonRoomIdentity roomIdentity,
            DungeonInstanceRoom room,
            long generation)
        {
            var timerName =
                $"a21-dungeon-load-{roomIdentity.Instance.PartyDungeonInstanceId}-" +
                $"{roomIdentity.RoomInstanceId}-{generation}";
            ClockService.Instance.ScheduleOneShotAfterAsync(
                timerName,
                LoadingTimeout,
                async _ => await HandleLoadingTimeoutAsync(
                    roomIdentity,
                    room,
                    generation));
        }

        private async Task HandleLoadingProjectionRejectedAsync(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            DungeonRoomIdentity roomIdentity)
        {
            var sourceRun = session?.Player?.CurrentRun;
            var expected = new DungeonParticipantRoomIdentity(runIdentity, roomIdentity);
            var canContinue = CaptureContinuationFence(session, _sessions, sourceRun, roomIdentity);
            if (sourceRun == null || !sourceRun.Matches(expected) || !canContinue())
                return;
            await HandleDelayedGiveupAsync(session, sourceRun, expected, canContinue);
        }

        private async Task HandleLoadingTimeoutAsync(
            DungeonRoomIdentity roomIdentity,
            DungeonInstanceRoom room,
            long generation)
        {
            var activeParticipants = CaptureLoadingParticipants(
                roomIdentity.Instance);
            var timeout = room.ForceLoadingCompletion(
                generation,
                activeParticipants);
            if (!timeout.Accepted)
                return;

            FileLogger.Log(
                "[DungeonLoading] timeout: " +
                $"instance={roomIdentity.Instance.PartyDungeonInstanceId} " +
                $"room={roomIdentity.RoomInstanceId} " +
                $"generation={generation} " +
                $"ready={timeout.ReadyParticipants.Count} " +
                $"missing={timeout.MissingParticipants.Count}");

            var missingCandidates = new List<(
                EnhancedClientSession Session,
                DungeonRun Run,
                DungeonParticipantRoomIdentity ParticipantRoom,
                int CharacterId,
                Func<bool> CanContinue)>();
            var instanceRoster = _instances
                .CaptureInstanceParticipantRoster(roomIdentity.Instance);
            foreach (var participant in instanceRoster)
            {
                if (!ContainsRunIdentity(
                        timeout.MissingParticipants,
                        participant.RunIdentity)
                    || !_sessions.TryGet(
                        participant.CharacterId,
                        out var candidate)
                    || candidate?.Player == null
                    || !candidate.Player.IsCurrentDungeonRun(
                        participant.RunIdentity))
                {
                    continue;
                }

                var candidateRun = candidate.Player.CurrentRun;
                var participantRoom = new DungeonParticipantRoomIdentity(
                    participant.RunIdentity, roomIdentity);
                var sourceCurrent = CaptureContinuationFence(candidate, _sessions,
                    candidateRun, roomIdentity,
                    () => room.IsCurrentLoadingGeneration(timeout.Generation));
                if (candidateRun != null
                    && candidateRun.Matches(participantRoom)
                    && sourceCurrent()
                    && candidateRun.TryCancelLoadingProjection(
                        timeout.ProjectionId))
                {
                    missingCandidates.Add((
                        candidate,
                        candidateRun,
                        participantRoom,
                        participant.CharacterId,
                        () => sourceCurrent() && candidateRun.IsLoadingProjectionCanceled(timeout.ProjectionId)));
                }
            }

            if (timeout.Released)
            {
                await SendLoadingCompletionAsync(
                    roomIdentity,
                    timeout.Generation,
                    timeout.ReadyParticipants,
                    "timeout");
            }

            foreach (var missing in missingCandidates)
            {
                var candidate = missing.Session;
                if (!missing.CanContinue())
                {
                    continue;
                }

                try
                {
                    await HandleDelayedGiveupAsync(candidate, missing.Run,
                        missing.ParticipantRoom, missing.CanContinue);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[DungeonLoading] missing cleanup failed: " +
                        $"cid={missing.CharacterId} " +
                        $"instance={roomIdentity.Instance.PartyDungeonInstanceId} " +
                        $"room={roomIdentity.RoomInstanceId} " +
                        $"error={ex.Message}");
                }
            }
        }

        private async Task HandleDelayedGiveupAsync(
            EnhancedClientSession session,
            DungeonRun sourceRun,
            DungeonParticipantRoomIdentity expectedRoom,
            Func<bool> canContinue)
        {
            var sessionCurrent = CaptureSessionFence(session, _sessions);
            if (await _town.TryHandleDelayedDungeonGiveupAsync(session, expectedRoom, canContinue)
                && sessionCurrent() && session.Player.CurrentRun == null
                && session.Player.CurrentDungeonRunGeneration == expectedRoom.Run.RunGeneration)
                await _raid.HandleDungeonAbortedAsync(session, sourceRun.DungeonId, "loading-rejected");
        }

        // A continuation captures its source once. These predicates only read
        // identity/state: no lifecycle/character/cohort gate or database I/O.
        internal static Func<bool> CaptureSessionFence(
            EnhancedClientSession session,
            ISessionDirectory sessions)
        {
            var player = session?.Player;
            var characterId = player?.CharacterId ?? 0;
            var userId = player?.UserId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            var gameSession = session?.GameSession;
            var sessionId = session?.SessionId ?? Guid.Empty;
            return () => session != null && sessions != null && player != null
                && characterId > 0 && userId > 0 && accountId > 0 && gameSession != null
                && ReferenceEquals(session.Player, player) && session.SessionId == sessionId
                && player.CharacterId == characterId && player.UserId == userId
                && session.Account?.AccountId == accountId
                && ReferenceEquals(session.GameSession, gameSession)
                && session.TcpClient != null && session.TcpClient.Connected
                && sessions.TryGet(characterId, out var current) && ReferenceEquals(current, session);
        }

        internal static Func<bool> CaptureContinuationFence(
            EnhancedClientSession session,
            ISessionDirectory sessions,
            DungeonRun sourceRun,
            DungeonRoomIdentity? expectedRoom = null,
            Func<bool> additional = null)
        {
            var sessionCurrent = CaptureSessionFence(session, sessions);
            var runIdentity = sourceRun?.CaptureIdentity() ?? default(DungeonRunIdentity);
            return () => sessionCurrent() && runIdentity.IsValid
                && ReferenceEquals(session.Player.CurrentRun, sourceRun)
                && session.Player.IsCurrentDungeonRun(runIdentity)
                && sourceRun.RunState != DungeonRunState.Ending && sourceRun.RunState != DungeonRunState.Ended
                && (!expectedRoom.HasValue || session.Player.IsCurrentDungeonRoom(expectedRoom.Value))
                && (additional == null || additional());
        }

        internal static Func<bool> CaptureFinishLoadingSourceFence(
            EnhancedClientSession session,
            ISessionDirectory sessions,
            DungeonRun sourceRun)
        {
            if (sourceRun != null)
                return CaptureContinuationFence(session, sessions, sourceRun);
            var sessionCurrent = CaptureSessionFence(session, sessions);
            return () => sessionCurrent() && session.Player.CurrentRun == null;
        }

        private static bool ContainsRunIdentity(
            IReadOnlyList<DungeonRunIdentity> participants,
            DungeonRunIdentity candidate)
        {
            if (participants == null)
                return false;
            foreach (var participant in participants)
            {
                if (participant.Equals(candidate))
                    return true;
            }
            return false;
        }
    }
}
