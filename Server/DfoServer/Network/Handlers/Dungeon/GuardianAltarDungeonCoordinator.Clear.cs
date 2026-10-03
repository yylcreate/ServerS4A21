using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        private readonly DungeonPersistentEffectApplicationService _persistentEffects;
        private Func<EnhancedClientSession, DungeonRunIdentity, Func<bool>, Task> _returnToStageSelection;

        internal void ConfigureStageSelectionReturn(
            Func<EnhancedClientSession, DungeonRunIdentity, Func<bool>, Task> returnToStageSelection)
        {
            if (_returnToStageSelection != null)
                throw new InvalidOperationException("Guardian stage-selection return has already been configured.");
            _returnToStageSelection = returnToStageSelection ?? throw new ArgumentNullException(nameof(returnToStageSelection));
        }

        internal async Task TryCompleteAsync(EnhancedClientSession session, DungeonRun run)
        {
            if (session?.Player == null || run?.Instance?.Mechanisms.GuardianAltarCombat == null)
                return;
            var identity = run.CaptureParticipantRoomIdentity();
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run, identity.Room);
            if (!fence()) return;
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, () => TryCompleteWithinTransitionAsync(session, run, fence));
            }
            finally { gate.Release(); }
        }

        // Called with the participant and character transition gates already
        // held, including from the existing phase-boundary reminder.
        private async Task TryCompleteWithinTransitionAsync(EnhancedClientSession session, DungeonRun run,
            Func<bool> sourceFence)
        {
            var combat = run.Instance.Mechanisms.GuardianAltarCombat;
            var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
            var snapshot = energy.ReadSnapshot(_monotonicMilliseconds());
            bool Current() => sourceFence() && run.RunState == DungeonRunState.Active
                && !combat.IsClosed && run.Matches(snapshot.Identity)
                && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarCombat, combat)
                && run.Instance.TryGetRoom(snapshot.Identity.Room.RoomInstanceId, out var room)
                && room.IsCurrentLoadingGeneration(snapshot.LoadingGeneration);
            if (!snapshot.IsReady || snapshot.IsClosed || !Current()) return;
            var plan = run.Instance.Mechanisms.GuardianAltar;
            var fact = combat.TerminalFact;
            if (fact == null)
            {
                if (snapshot.Paused) return;
                if (plan.IsSurvival && combat.TryReserveRoundAdvance(snapshot.Identity,
                        snapshot.LoadingGeneration, snapshot.ActiveMilliseconds, out var advance))
                {
                    try
                    {
                        bool RoundCurrent() => Current() && combat.IsCurrent(advance)
                            && !energy.ReadSnapshot(_monotonicMilliseconds()).Paused;
                        var body = GuardianAltarDungeonPacketBuilder.BuildSurvivalRound(advance.NextRoundPlan);
                        if (!await SendClearFrameAsync(session, NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP,
                                body, RoundCurrent)) return;
                        if (combat.TryMarkRoundProjected(advance,
                                energy.ReadSnapshot(_monotonicMilliseconds()).ActiveMilliseconds))
                        {
                            FileLogger.Log($"[GuardianAltarSurvival] round projected cid={session.Player.CharacterId} "
                                + $"run={run.RunId}/{run.RunGeneration} completed={advance.CompletedRound} "
                                + $"round={advance.NextRoundPlan.RoundNumber} map={advance.NextRoundPlan.MapId} "
                                + $"sameRoom={snapshot.Identity.Room.RoomInstanceId} loading={snapshot.LoadingGeneration}");
                            ScheduleCombat(session, run, energy.ReadSnapshot(_monotonicMilliseconds()));
                        }
                    }
                    catch (Exception exception)
                    {
                        FileLogger.Log($"[GuardianAltarSurvival] round projection deferred run={run.RunId}: {exception.Message}");
                        if (Current()) ScheduleCombat(session, run,
                            energy.ReadSnapshot(_monotonicMilliseconds()), WaveRetryMilliseconds);
                    }
                    return;
                }
                combat.TryComplete(snapshot.Identity, snapshot.LoadingGeneration, snapshot.ActiveMilliseconds, out fact);
            }
            if (fact == null || fact.Result == GuardianAltarTerminalResult.Aborted
                || !fact.Identity.Equals(snapshot.Identity) || fact.LoadingGeneration != snapshot.LoadingGeneration)
                return;
            var presentation = combat.Presentation;
            if (!presentation.TryBind(fact, plan.IsSurvival)) return;
            // A terminal combat cannot resume natural energy or consume it.
            energy.TrySetPaused(fact.Identity, fact.LoadingGeneration, true, _monotonicMilliseconds(), out _);
            try
            {
                var commit = presentation.CommitResult;
                InventoryLease lease = null;
                if (fact.Result == GuardianAltarTerminalResult.Succeeded || plan.IsSurvival)
                {
                    if (!InventoryContext.TryGetLease(session.Player.CharacterId, out lease)
                        || !lease.IsOwnedBy(session.SessionId) || !Current())
                        throw new InvalidOperationException("Guardian clear has no current owned inventory lease.");
                    if (commit == null)
                    {
                        var effectId = new DungeonEffectId(fact.EventId, plan.IsSurvival
                            ? DungeonPersistentEffectKinds.GuardianAltarSurvivalCommit
                            : DungeonPersistentEffectKinds.GuardianAltarClearCommit, DungeonEffectScope.Player, run.RunId);
                        if (_persistentEffects == null || (!plan.IsSurvival && plan.ClearPlan == null))
                            throw new InvalidOperationException("Guardian clear owner or frozen resource plan is missing.");
                        string error;
                        var applied = plan.IsSurvival
                            ? _persistentEffects.TryApplyGuardianAltarSurvival(effectId, session.Player.CharacterId,
                                session.Account.AccountId, lease, session.SessionId,
                                new GuardianAltarSurvivalCommitPlan(plan.Progress.Header.RidableId, plan.DungeonId,
                                    plan.SurvivalRounds.Count, fact.ClearedRounds,
                                    plan.SurvivalRewards.GetRewards(fact.ClearedRounds)), Current, out commit, out error)
                            : _persistentEffects.TryApplyGuardianAltarClear(effectId, session.Player.CharacterId,
                                session.Account.AccountId, lease, session.SessionId, plan.ClearPlan, Current,
                                out commit, out error);
                        if (!applied)
                            throw new InvalidOperationException("Guardian durable clear failed: " + error);
                        if (!Current() || !presentation.TryCommit(commit)) return;
                        run.Effects.ProjectCommitted(effectId);
                        FileLogger.Log($"[GuardianAltarClear] committed cid={session.Player.CharacterId} "
                            + $"run={run.RunId}/{run.RunGeneration} dungeon={plan.DungeonId} difficulty={plan.Difficulty} "
                            + $"event={fact.EventId:N} stars={commit.StarsGained} mailed={commit.MailedRewardCount} "
                            + $"survival={plan.IsSurvival} rounds={commit.ClearedRounds} best={commit.BestRound}");
                    }
                    bool OwnedCurrent() => Current()
                        && InventoryContext.IsCurrentLease(lease, session.SessionId, session.Player.CharacterId);
                    if (!presentation.InventoryProjectionSent)
                    {
                        foreach (var changes in commit.Changes.GroupBy(x => x.ListType))
                        {
                            byte[] body;
                            lock (lease.SyncRoot)
                            {
                                if (!OwnedCurrent()) return;
                                body = ItemListUpdateBuilder.BuildItemSpaceUpdateBody(lease.Inventory, changes.Key,
                                    changes.Select(x => x.SlotIndex).Distinct().ToArray());
                            }
                            if (!await SendClearFrameAsync(session, NotiPacketTypeA21.UPDATE_ITEM_LIST, body, OwnedCurrent)) return;
                        }
                        if (!presentation.TryMarkInventoryProjectionSent(fact.EventId)) return;
                    }
                    // UPDATE_RIDABLE rebuilds the menu, purchases and deployed
                    // controls. Survival has no unlock/purchase/star changes;
                    // its result and available tickets are in CLEAR/EPLP.
                    // Read the committed menu on the next real START_GAME,
                    // instead of rebuilding those controls on a dead actor.
                    if (presentation.RequiresMenuProjection && !presentation.MenuProjectionSent)
                    {
                        if (!await SendClearFrameAsync(session, NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE,
                                GuardianAltarMenuBodyBuilder.BuildUpdateRidable(commit.ProgressSnapshot, 0), Current)) return;
                        if (!presentation.TryMarkMenuProjectionSent(fact.EventId)) return;
                    }
                    if (!presentation.ClearProjectionSent)
                    {
                        var body = GuardianAltarClearBodyBuilder.BuildClearStage(
                            new GuardianAltarClearHeader(plan.IsSurvival ? 1 : 0, commit.ClearedRounds,
                                commit.BestRound, commit.Improved, plan.DungeonId, plan.Difficulty, commit.StarsGained),
                            (plan.IsSurvival ? plan.SurvivalRewards.GetRewards(commit.ClearedRounds) : plan.ClearPlan.Rewards)
                                .Select(x => new GuardianAltarClearRewardRow(0, x.ItemId, x.Count)).ToArray(),
                            commit.NewPurchases, Array.Empty<GuardianAltarAchievement>());
                        if (!await SendClearFrameAsync(session, NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE, body, Current)) return;
                        if (!presentation.TryMarkClearProjectionSent(fact.EventId, _utcNow().UtcDateTime)) return;
                    }
                }
                if (!presentation.EplpProjectionSent)
                {
                    if ((fact.Result == GuardianAltarTerminalResult.Succeeded || plan.IsSurvival)
                        && _utcNow().UtcDateTime < presentation.EplpDeadlineUtc)
                    {
                        ScheduleClearContinuation(session, run, fact, presentation.EplpDeadlineUtc);
                        return;
                    }
                    var progress = commit?.ProgressSnapshot ?? plan.Progress;
                    var available = (long)progress.Header.FreeTickets + progress.Header.SecondaryTickets > 0 ? 1 : 0;
                    var body = GuardianAltarClearBodyBuilder.BuildEplpState(
                        fact.Result == GuardianAltarTerminalResult.Succeeded || plan.IsSurvival ? 3 : 0, available);
                    if (!await SendClearFrameAsync(session, NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE, body, Current)) return;
                    if (!presentation.TryMarkEplpProjectionSent(fact.EventId, _utcNow().UtcDateTime)) return;
                    FileLogger.Log($"[GuardianAltarClear] eplp projected cid={session.Player.CharacterId} "
                        + $"run={run.RunId} result={fact.Result} event={fact.EventId:N}");
                }
                ScheduleGuardianReturn(session, run, fact, presentation.ReturnDeadlineUtc);
            }
            catch (Exception exception)
            {
                FileLogger.Log($"[GuardianAltarClear] deferred cid={session.Player.CharacterId} run={run.RunId} "
                    + $"event={fact.EventId:N}: {exception}");
                if (Current()) ScheduleClearContinuation(session, run, fact, _utcNow().UtcDateTime.AddMilliseconds(WaveRetryMilliseconds));
            }
        }

        private async Task<bool> SendClearFrameAsync(EnhancedClientSession session, NotiPacketTypeA21 opcode,
            byte[] body, Func<bool> current)
        {
            var prepared = false;
            var packet = GamePacketEnvelopeBuilder.Build(0, (ushort)opcode, body);
            await _sendPause(session, () =>
            {
                if (!current()) return Array.Empty<byte[]>();
                prepared = true;
                return new[] { packet };
            }, current);
            return prepared && current();
        }

        internal async Task<bool> TryHandleCenterDeathAsync(EnhancedClientSession session, DungeonRun run, ushort sequenceId)
        {
            var plan = run?.Instance?.Mechanisms.GuardianAltar;
            if (plan == null || sequenceId >= plan.Centers.Count || !plan.Centers[sequenceId].IsFailureCenter)
                return false;
            var identity = run.CaptureParticipantRoomIdentity();
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run, identity.Room);
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
                    var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                    var snapshot = energy.ReadSnapshot(_monotonicMilliseconds());
                    bool Current() => fence() && run.RunState == DungeonRunState.Active
                        && run.Matches(identity) && !combat.IsClosed
                        && run.Instance.TryGetRoom(identity.Room.RoomInstanceId, out var room)
                        && room.IsCurrentLoadingGeneration(snapshot.LoadingGeneration);
                    if (!Current() || !snapshot.IsReady || snapshot.IsClosed) return;
                    if (!combat.TryFailCenter(identity, snapshot.LoadingGeneration, sequenceId,
                            snapshot.ActiveMilliseconds, out _)) return;
                    try
                    {
                        await SendClearFrameAsync(session, NotiPacketTypeA21.DIE_MONSTER,
                            DungeonNotificationBuilder.BuildMonsterDie(sequenceId, null, session.Player.UserId), Current);
                    }
                    catch (Exception exception)
                    {
                        // The accepted center failure is already immutable.
                        // A failed death projection cannot suppress its EPLP.
                        FileLogger.Log($"[GuardianAltarClear] center death projection deferred run={run.RunId}: {exception}");
                    }
                    await TryCompleteWithinTransitionAsync(session, run, Current);
                });
            }
            finally { gate.Release(); }
            return true;
        }

        private void ScheduleClearContinuation(EnhancedClientSession session, DungeonRun run,
            GuardianAltarTerminalFact fact, DateTime deadline)
            => ScheduleTerminalReminder(session, run, fact, DungeonRunTimerKeys.GuardianAltarClearPresentation,
                deadline, async () => await TryCompleteAsync(session, run));

        private void ScheduleGuardianReturn(EnhancedClientSession session, DungeonRun run,
            GuardianAltarTerminalFact fact, DateTime deadline)
            => ScheduleTerminalReminder(session, run, fact, DungeonRunTimerKeys.GuardianAltarReturnToTown,
                deadline, async () => await _returnToTown(session, fact.Identity.Run));

        private void ScheduleTerminalReminder(EnhancedClientSession session, DungeonRun run,
            GuardianAltarTerminalFact fact, RunTimerKey key, DateTime deadline, Func<Task> action)
        {
            var combat = run.Instance.Mechanisms.GuardianAltarCombat;
            if (combat.IsClosed || combat.Timers.TryGetCurrentTicket(key, out _)) return;
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run, fact.Identity.Room);
            bool Current() => fence() && !combat.IsClosed && ReferenceEquals(combat.TerminalFact, fact)
                && run.RunState == DungeonRunState.Active && run.Matches(fact.Identity)
                && run.Instance.TryGetRoom(fact.Identity.Room.RoomInstanceId, out var room)
                && room.IsCurrentLoadingGeneration(fact.LoadingGeneration);
            if (!Current()) return;
            var ticket = combat.Timers.Begin(key, deadline, RunTimerDetachPolicy.Cancel);
            var handle = _combatClock.ScheduleOneShotAsync(
                $"guardian-altar:{key.Purpose}:{fact.EventId:N}:{ticket.Generation}", deadline, async _ =>
                {
                    if (!Current() || !combat.Timers.TryComplete(ticket)) return;
                    await action();
                });
            combat.Timers.Attach(ticket, handle);
        }

        // A21 sub_10C3060 writes event, then button index. Event 2 selects a
        // button; sub_10C3E00 applies exit actions only for event 1. Guardian
        // never enters ordinary card/retry preparation.
        internal async Task<bool> TryHandleEplpCommandAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var combat = run?.Instance?.Mechanisms.GuardianAltarCombat;
            if (combat == null) return false;
            var fact = combat.TerminalFact;
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.EPLP_COMMAND
                || body == null || body.Length != 2 || body[0] is not (1 or 2) || body[1] is not (1 or 2)
                || fact == null || !combat.Presentation.EplpProjectionSent)
                return true;
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run, fact.Identity.Room);
            bool Current() => fence() && !combat.IsClosed && run.RunState == DungeonRunState.Active
                && ReferenceEquals(combat.TerminalFact, fact)
                && run.Instance.TryGetRoom(fact.Identity.Room.RoomInstanceId, out var room)
                && room.IsCurrentLoadingGeneration(fact.LoadingGeneration);
            var prepared = false;
            // The common A21 EPLP acknowledgement is already shared by the
            // validated tower/altar consumers: success, state, option.
            var packet = GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.EPLP_COMMAND,
                DeathTowerPacketBuilder.BuildEplpCommandAck(body[0], body[1]));
            await _sendPause(session, () =>
            {
                if (!Current()) return Array.Empty<byte[]>();
                prepared = true;
                return new[] { packet };
            }, Current);
            if (body[0] == 1 && prepared && Current())
            {
                // sub_E10700 reads the accepted option after the town fade:
                // option 1 sends START_GAME itself; option 2 enters town.
                // End only this run and let the existing menu owner respond.
                if (body[1] == 1 && _returnToStageSelection != null)
                    await _returnToStageSelection(session, fact.Identity.Run, Current);
                else
                    await _returnToTown(session, fact.Identity.Run);
            }
            return true;
        }
    }
}
