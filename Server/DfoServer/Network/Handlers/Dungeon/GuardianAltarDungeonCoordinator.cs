using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Resource-classified special entry, not a replacement SELECT handler or
    // another run/loading state machine. Dedicated combat shares its energy
    // clock; dedicated terminal facts authorize dedicated persistent rewards.
    // It must never enter ordinary maze/hell/card paths.
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        private readonly GuardianAltarOpeningService _opening;
        private readonly IGuardianAltarProgressService _progress;
        private readonly Func<GuardianAltarMenuDefinition> _definition;
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly DungeonInstanceRegistry _instances;
        private readonly Func<EnhancedClientSession, bool> _hasTrade;
        private readonly IMercenaryRestrictionService _mercenaries;
        private readonly Func<ushort, bool> _hasParty;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<int, bool> _isGuardianDungeon;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Task> _send;
        private readonly Action<DungeonRun, DungeonInstanceRoom, long> _registerLoading;
        private readonly Func<EnhancedClientSession, DungeonRunIdentity, Task> _returnToTown;
        private readonly Func<EnhancedClientSession, DungeonRunIdentity, Task> _onEntered;

        internal GuardianAltarDungeonCoordinator(GuardianAltarOpeningService opening,
            IGuardianAltarProgressService progress, Func<GuardianAltarMenuDefinition> definition,
            ISessionDirectory sessions, CharacterTransitionCoordinator transitions,
            DungeonInstanceRegistry instances, Func<EnhancedClientSession, bool> hasTrade,
            IMercenaryRestrictionService mercenaries, Func<ushort, bool> hasParty,
            Action<DungeonRun, DungeonInstanceRoom, long> registerLoading,
            Func<EnhancedClientSession, DungeonRunIdentity, Task> returnToTown,
            Func<DateTimeOffset> utcNow = null,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Task> send = null,
            Func<int, bool> isGuardianDungeon = null,
            Func<EnhancedClientSession, DungeonRunIdentity, Task> onEntered = null,
            Func<long> monotonicMilliseconds = null,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> sendPause = null,
            ClockService combatClock = null,
            DungeonPersistentEffectApplicationService persistentEffects = null)
        {
            _opening = opening ?? throw new ArgumentNullException(nameof(opening));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _instances = instances ?? throw new ArgumentNullException(nameof(instances));
            _hasTrade = hasTrade ?? throw new ArgumentNullException(nameof(hasTrade));
            _mercenaries = mercenaries ?? throw new ArgumentNullException(nameof(mercenaries));
            _hasParty = hasParty ?? throw new ArgumentNullException(nameof(hasParty));
            _registerLoading = registerLoading ?? throw new ArgumentNullException(nameof(registerLoading));
            _returnToTown = returnToTown ?? throw new ArgumentNullException(nameof(returnToTown));
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _send = send ?? SendFencedBatchAsync;
            _isGuardianDungeon = isGuardianDungeon ?? (id =>
            {
                var dungeon = DungeonCatalog.GetDungeonFile(id);
                return dungeon != null && (string.Equals(dungeon.DungeonType, "advance altar",
                    StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(dungeon.AdvanceAltarType));
            });
            _onEntered = onEntered ?? ((_, _) => Task.CompletedTask);
            _monotonicMilliseconds = monotonicMilliseconds ?? (() => Environment.TickCount64);
            _sendPause = sendPause ?? ((session, prepare, current) =>
                session.SendPreparedPacketBatchAsync(prepare, current));
            _combatClock = combatClock ?? ClockService.Instance;
            _persistentEffects = persistentEffects;
        }

        internal async Task<bool> TryHandleSelectDungeonAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.SELECT_DUNGEON
                || body == null || body.Length < 4)
                return false;
            var wireId = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (wireId == 0 || wireId > short.MaxValue)
                return false;
            var dungeonId = (int)wireId;
            try
            {
                if (!_isGuardianDungeon(dungeonId)) return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or ArgumentException)
            {
                // A failed resource probe is not permission to start any mode.
                FileLogger.Log($"[GuardianAltarDungeon] classification failed dungeon={dungeonId}: {ex.Message}");
                return false; // the unchanged ordinary admission remains fail closed
            }
            var cid = session?.Player?.CharacterId ?? 0;
            var uid = session?.Player?.UserId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            var gameSession = session?.GameSession;
            bool Current() => cid > 0 && uid > 0 && accountId > 0 && gameSession != null
                && session?.Player?.CharacterId == cid && session.Player.UserId == uid
                && session.Account?.AccountId == accountId
                && ReferenceEquals(session.GameSession, gameSession)
                && _sessions.TryGet(cid, out var current) && ReferenceEquals(current, session);
            if (!Current()) return true;
            var parsed = GuardianAltarSelectDungeonRequestParser.TryParse(body, out var command);
            DungeonRun failedRun = null;
            // Party admission also takes the participant run gate before the
            // character gate. Keep that order for cross-session transitions.
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                try
                {
                    if (!Current()) return;
                    var existing = session.Player.CurrentRun;
                    if (parsed && existing?.Instance?.Mechanisms.GuardianAltar is { } existingPlan
                        && existingPlan.DungeonId == command.DungeonId
                        && existingPlan.Difficulty == command.Difficulty
                        && existing.RunState is DungeonRunState.Selecting or DungeonRunState.Active)
                    {
                        FileLogger.Log($"[GuardianAltarDungeon] duplicate start ignored cid={cid} run={existing.RunId}");
                        return; // never restart map/client clock or spend another ticket
                    }
                    var reason = parsed ? CheckEligibility(session) : "InvalidRequest";
                    if (reason != null)
                    {
                        await RejectAsync(session, Current, dungeonId, reason);
                        return;
                    }
                    var anchorTime = _utcNow();
                    var revision = _opening.Revision;
                    var generation = session.Player.CurrentDungeonRunGeneration;
                    bool SourceCurrent() => Current()
                        && _opening.Revision == revision && _opening.GetSnapshot(_utcNow()).IsOpen
                        && DailyResetService.TodayId(_utcNow().UtcDateTime) == DailyResetService.TodayId(anchorTime.UtcDateTime)
                        && session.Player.CurrentDungeonRunGeneration == generation
                        && SessionDirectory.IsTownPresence(session.Player)
                        && !session.Player.DungeonSelectionPending
                        && session.Player.CurrentDungeonSelection == null
                        && session.Player.Level >= GuardianAltarEntryRules.MinimumLevel
                        && !_hasParty(uid);
                    GuardianAltarDungeonStartPlan plan;
                    try
                    {
                        var progress = _progress.LoadMenu(cid, anchorTime.UtcDateTime, SourceCurrent);
                        if (progress.CharacterId != cid)
                            throw new InvalidOperationException("Guardian progress belongs to a different character.");
                        plan = _definition().CreateStartPlan(command.DungeonId, command.Difficulty, progress);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or SqliteException
                        or OverflowException or ArgumentException)
                    {
                        FileLogger.Log($"[GuardianAltarDungeon] preparation failed cid={cid} dungeon={dungeonId}: {ex}");
                        await RejectAsync(session, Current, dungeonId, "ProgressOrResourcesUnavailable");
                        return;
                    }
                    // Restriction reads and resource preparation remain outside
                    // lifecycle/send locks. The character transition serializes
                    // this use case; the final fence is pure identity/time/state.
                    reason = CheckEligibility(session);
                    if (reason != null || !SourceCurrent())
                    {
                        await RejectAsync(session, Current, dungeonId, reason ?? "SourceExpired");
                        return;
                    }
                    var instance = new DungeonInstance((short)plan.DungeonId, plan.Difficulty,
                        DungeonRewardPolicy.GuardianAltar, DungeonDropDefinition.CreateGuardianAltar(plan.DungeonId));
                    instance.Mechanisms.TryAttachGuardianAltar(plan);
                    var anchor = new DungeonTownReturnAnchor(session.Player.CurTownId, session.Player.CurAreaId,
                        session.Player.CurPosX, session.Player.CurPosY, session.Player.CurDirection, session.Player.CurAreaState);
                    if (!DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, instance, anchor,
                            generation, SourceCurrent, out var run))
                    {
                        await RejectAsync(session, Current, dungeonId, "SourceExpired");
                        return;
                    }
                    failedRun = run; // retained until the complete batch is written
                    var identity = run.CaptureIdentity();
                    var key = new RoomKey(0, 0, plan.MapId);
                    var maze = new GameWorld.Dungeon.MazeSumInfo
                    {
                        Index = plan.MapId, X = 0, Y = 0,
                        Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                    };
                    var room = instance.GetOrCreateRoom(key,
                        id => new DungeonInstanceRoom(id, key, maze, seed: 0), out _);
                    run.SetCurrentRoom(room);
                    run.RoomKey = key;
                    run.RoomStates.Add(key, new RoomState { InstanceRoom = room, Maze = maze,
                        KilledSeqIds = new HashSet<ushort>(), Lcg = new DnfLcg(0) });
                    if (!Current())
                        throw new InvalidOperationException("Guardian participant identity changed before registration.");
                    var attachment = _instances.RegisterActive(new DungeonParticipantRegistration(
                        accountId, cid, uid, 0, session.SessionId, run));
                    instance.RegisterParticipantLife(attachment.RunIdentity);
                    var projectionId = DungeonMapHandler.CreateLoadingProjectionId();
                    if (!run.TryActivate() || !run.TryClaimLoadingProjection(projectionId)
                        || !run.TryBeginLoadingProjection(projectionId))
                        throw new InvalidOperationException("Guardian loading projection cannot be reserved.");
                    _registerLoading(run, room, projectionId);
                    bool RunCurrent() => Current() && session.Player.IsCurrentDungeonRun(identity)
                        && session.Player.IsCurrentDungeonRoom(room.Identity)
                        && run.IsCurrentLoadingProjection(projectionId);
                    var projected = false;
                    await _send(session, () =>
                    {
                        if (!RunCurrent() || _opening.Revision != revision
                            || !_opening.GetSnapshot(_utcNow()).IsOpen
                            || DailyResetService.TodayId(_utcNow().UtcDateTime) != DailyResetService.TodayId(anchorTime.UtcDateTime)
                            || _hasParty(uid) || _hasTrade(session)
                            || session.Player.Level < GuardianAltarEntryRules.MinimumLevel)
                            return Array.Empty<byte[]>();
                        // A21 menu already owns the client activity scene. Only
                        // the dedicated stage/map projection starts its loading.
                        session.Player.UserState = 1;
                        room.TryActivate();
                        // Mark the possible client override before the batch is
                        // written, so a partial START_MAP write can also restore.
                        session.GuardianAltarControls = new EnhancedClientSession.GuardianAltarControlProjectionSource(
                            session.GameSession, session.Account, cid, accountId, uid, identity,
                            session.Player.CurrentDungeonSelectionGeneration);
                        projected = true;
                        return new[]
                        {
                            GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.USER_STATE,
                                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)),
                            GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_STAGE_INFO,
                                GuardianAltarDungeonPacketBuilder.BuildStageInfo(plan)),
                            GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP,
                                GuardianAltarDungeonPacketBuilder.BuildStartMap(plan)),
                        };
                    });
                    if (projected && RunCurrent())
                    {
                        failedRun = null;
                        try { await _onEntered(session, identity); }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[GuardianAltarDungeon] town departure projection failed cid={cid}: {ex.Message}");
                        }
                        FileLogger.Log($"[GuardianAltarDungeon] start projected cid={cid} dungeon={plan.DungeonId} "
                            + $"difficulty={plan.Difficulty} map={plan.MapId} phases={plan.Phases.Count} centers={plan.Centers.Count} "
                            + $"run={identity.RunId}/{identity.RunGeneration} ticketsUnchanged=true wavesEnabled=true");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException or ObjectDisposedException
                    or InvalidOperationException or ArgumentException or SqliteException or OverflowException
                    or OperationCanceledException)
                {
                    FileLogger.Log($"[GuardianAltarDungeon] start failed cid={cid} dungeon={dungeonId}: {ex}");
                    if (failedRun == null && Current())
                        await RejectAsync(session, Current, dungeonId, "StartUnavailable");
                }
            });
            }
            finally
            {
            gate.Release();
            // Reuse the expected-run return owner outside the character/run
            // gates; a delayed failure cannot terminate a replacement run.
            if (failedRun != null)
            {
                var failedIdentity = failedRun.CaptureIdentity();
                try
                {
                    if (Current() && session.Player.IsCurrentDungeonRun(failedIdentity))
                        await _returnToTown(session, failedIdentity);
                }
                finally
                {
                    if (session.Player.IsCurrentDungeonRun(failedIdentity))
                        await DungeonRunLifecycle.EndRunAsync(session, DungeonRunEndReason.EntryRejected,
                            failedIdentity, _instances);
                    // Even teardown between registration and prepare can leave
                    // this late registration. Never terminate a newer identity.
                    _instances.Terminate(cid, failedIdentity, DungeonRunEndReason.EntryRejected.ToString());
                }
            }
            }
            return true;
        }

        private string CheckEligibility(EnhancedClientSession session)
        {
            if (!_opening.GetSnapshot(_utcNow()).IsOpen) return "Closed";
            if (!SessionDirectory.IsTownPresence(session.Player) || session.Player.DungeonSelectionPending
                || session.Player.CurrentDungeonSelection != null) return "InvalidPresence";
            if (_hasParty(session.Player.UserId)) return "PartyNotAllowed";
            if (_hasTrade(session)) return "Trading";
            if (_mercenaries.IsAssigned(session.Player.CharacterId)) return "MercenaryAssigned";
            if (session.Player.Level < GuardianAltarEntryRules.MinimumLevel) return "LevelTooLow";
            return null;
        }

        private Task SendFencedBatchAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
        {
            var cid = session.Player.CharacterId;
            var uid = session.Player.UserId;
            var aid = session.Account?.AccountId;
            var gameSession = session.GameSession;
            var sourceRun = session.Player.CurrentRun;
            var runIdentity = sourceRun?.CaptureIdentity();
            var isGuardianRun = sourceRun?.Instance?.Mechanisms.GuardianAltar != null;
            var revision = _opening.Revision;
            var anchor = DailyResetService.TodayId(_utcNow().UtcDateTime);
            return session.SendPreparedPacketBatchAsync(prepare, () =>
                session.Player.CharacterId == cid && session.Player.UserId == uid
                && session.Account?.AccountId == aid && ReferenceEquals(session.GameSession, gameSession)
                && _sessions.TryGet(cid, out var current) && ReferenceEquals(current, session)
                && _opening.Revision == revision
                && (!isGuardianRun || (sourceRun.RunState == DungeonRunState.Active
                    && _opening.GetSnapshot(_utcNow()).IsOpen))
                && DailyResetService.TodayId(_utcNow().UtcDateTime) == anchor
                && (!runIdentity.HasValue || session.Player.IsCurrentDungeonRun(runIdentity.Value)));
        }

        private async Task RejectAsync(EnhancedClientSession session, Func<bool> current, int dungeonId, string reason)
        {
            await _send(session, () => current() ? new[]
            {
                GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.SELECT_DUNGEON,
                    DungeonAdmissionRejectBuilder.Build(DungeonAdmissionReject.DungeonUnavailable)),
                GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.BuildRaidNotice("无法开始守护者祭坛，请检查开放时间、角色状态、关卡解锁和剩余龟甲。")),
            } : Array.Empty<byte[]>());
            FileLogger.Log($"[GuardianAltarDungeon] start rejected cid={session.Player.CharacterId} dungeon={dungeonId} reason={reason}");
        }
    }
}
