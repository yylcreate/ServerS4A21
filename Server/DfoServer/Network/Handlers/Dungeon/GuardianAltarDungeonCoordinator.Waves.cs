using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        private readonly ClockService _combatClock;
        private const int WaveRetryMilliseconds = 1000;

        private void ScheduleCombat(EnhancedClientSession session, DungeonRun run,
            GuardianAltarEnergySnapshot snapshot, int minimumDelayMilliseconds = 0)
        {
            var combat = run?.Instance?.Mechanisms.GuardianAltarCombat;
            if (combat == null) return;
            if (!snapshot.IsReady || snapshot.IsClosed || snapshot.Paused
                || run.RunState != DungeonRunState.Active || !run.Matches(snapshot.Identity))
            {
                combat.Timers.Cancel(DungeonRunTimerKeys.GuardianAltarWaveSchedule);
                return;
            }
            var due = combat.NextDueActiveMilliseconds;
            if (!due.HasValue)
            {
                combat.Timers.Cancel(DungeonRunTimerKeys.GuardianAltarWaveSchedule);
                return;
            }
            var delay = Math.Max(minimumDelayMilliseconds, Math.Max(0, due.Value - snapshot.ActiveMilliseconds));
            var deadline = _utcNow().UtcDateTime.AddMilliseconds(delay);
            var ticket = combat.Timers.Begin(DungeonRunTimerKeys.GuardianAltarWaveSchedule,
                deadline, RunTimerDetachPolicy.Cancel);
            var identity = snapshot.Identity;
            var generation = snapshot.LoadingGeneration;
            // Session/account/run/room are all captured before this reminder;
            // energy Version is deliberately not its identity. Spending or
            // upgrading cannot cancel a still-current enemy wave.
            var sourceFence = DungeonLoadingCoordinator.CaptureContinuationFence(
                session, _sessions, run, identity.Room);
            var sourceRound = combat.CurrentRound;
            bool RoundFence() => sourceFence() && combat.CurrentRound == sourceRound;
            var handle = _combatClock.ScheduleOneShotAsync(
                $"guardian-altar:wave:{identity.Run.PartyDungeonInstanceId}:{identity.Run.RunId}:{ticket.Generation}",
                deadline, _ => OnCombatTimerAsync(session, run, identity, generation, ticket, RoundFence));
            combat.Timers.Attach(ticket, handle);
        }

        internal async Task OnCombatTimerAsync(EnhancedClientSession session, DungeonRun run,
            DungeonParticipantRoomIdentity identity, long generation, RunTimerTicket ticket,
            Func<bool> sourceFence)
        {
            var combat = run?.Instance?.Mechanisms.GuardianAltarCombat;
            var energy = run?.Instance?.Mechanisms.GuardianAltarEnergy;
            bool Current() => sourceFence() && run.RunState == DungeonRunState.Active
                && run.Matches(identity) && combat != null && !combat.IsClosed
                && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarCombat, combat)
                && energy != null && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarEnergy, energy)
                && run.Instance.TryGetRoom(identity.Room.RoomInstanceId, out var room)
                && room.IsCurrentLoadingGeneration(generation);
            if (!Current() || !combat.Timers.IsCurrent(ticket)) return;
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    if (!Current() || !combat.Timers.IsCurrent(ticket)) return;
                    var snapshot = energy.ReadSnapshot(_monotonicMilliseconds());
                    if (!snapshot.IsReady || snapshot.Paused || snapshot.LoadingGeneration != generation)
                        return;
                    // Keep this ticket current while awaiting transport. A
                    // pause/end cancels it and therefore every pending frame.
                    if (!combat.TryReserveDue(snapshot.ActiveMilliseconds, identity, generation,
                            out var batch, out var failure))
                    {
                        if (!string.IsNullOrEmpty(failure))
                            FileLogger.Log($"[GuardianAltarWave] deferred run={run.RunId} reason={failure}");
                        await TryCompleteWithinTransitionAsync(session, run, Current);
                        if (combat.Timers.TryComplete(ticket) && Current())
                            ScheduleCombat(session, run, energy.ReadSnapshot(_monotonicMilliseconds()),
                                string.IsNullOrEmpty(failure) ? 0 : WaveRetryMilliseconds);
                        return;
                    }
                    var definitions = new List<DungeonDynamicActorDefinition>(batch.Spawns.Count);
                    var packets = new List<byte[]>(batch.Spawns.Count);
                    foreach (var spawn in batch.Spawns)
                    {
                        var actor = spawn.Object;
                        var type = actor.Type switch { "normal" => 0, "named" => 1, "boss" => 2,
                            _ => throw new InvalidOperationException("Unknown frozen Guardian unit type.") };
                        definitions.Add(new DungeonDynamicActorDefinition(spawn.SequenceId,
                            actor.PrototypeIndex, (byte)type, checked((byte)actor.Level), identity.Room,
                            GuardianAltarCombatRuntime.DynamicActorProvider, generation, batch.WaveIdentity,
                            DungeonDynamicActorPolicy.GuardianAltar));
                        packets.Add(GamePacketEnvelopeBuilder.Build(0,
                            (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_SPAWN_UNIT,
                            GuardianAltarCombatBodyBuilder.BuildSpawnUnit(new GuardianAltarSpawnUnitFields(
                                checked((short)spawn.SequenceId), actor.Id, actor.IsEnemy, type,
                                actor.PrototypeIndex, checked((short)actor.Level), actor.RawParameter,
                                actor.X, actor.Y, actor.Z))));
                    }
                    if (!run.Instance.Mechanisms.DynamicActors.TryRegisterBatch(definitions, out failure)
                        || !combat.TryCommitRegistered(batch))
                    {
                        FileLogger.Log($"[GuardianAltarWave] registration deferred run={run.RunId} reason={failure}");
                        if (combat.Timers.TryComplete(ticket) && Current())
                            ScheduleCombat(session, run, energy.ReadSnapshot(_monotonicMilliseconds()), WaveRetryMilliseconds);
                        return;
                    }
                    bool SendCurrent() => Current() && combat.Timers.IsCurrent(ticket)
                        && combat.IsCurrent(batch) && !energy.ReadSnapshot(_monotonicMilliseconds()).Paused;
                    try
                    {
                        var start = combat.GetProjectionOffset(batch);
                        if (start < 0) return;
                        for (var index = start; index < packets.Count; index++)
                        {
                            var packet = packets[index];
                            await _sendPause(session, () => SendCurrent() ? new[] { packet }
                                : Array.Empty<byte[]>(), SendCurrent);
                            if (!SendCurrent() || !combat.TryRecordSpawnProjected(batch, index)) return;
                        }
                        if (SendCurrent() && combat.TryMarkProjected(batch))
                            FileLogger.Log($"[GuardianAltarWave] projected run={run.RunId}/{run.RunGeneration} "
                                + $"map={combat.CurrentRoundPlan?.MapId ?? run.Instance.Mechanisms.GuardianAltar.MapId} "
                                + $"round={combat.CurrentRound} phase={batch.PhaseIndex} wave={batch.WaveIdentity} "
                                + $"actors={batch.Spawns.Count} activeMs={snapshot.ActiveMilliseconds}");
                    }
                    catch (Exception exception)
                    {
                        combat.RetainForRetry(batch);
                        FileLogger.Log($"[GuardianAltarWave] projection deferred run={run.RunId} "
                            + $"wave={batch.WaveIdentity}: {exception.Message}");
                    }
                    if (combat.Timers.TryComplete(ticket) && Current())
                        ScheduleCombat(session, run, energy.ReadSnapshot(_monotonicMilliseconds()),
                            combat.IsCurrent(batch) ? WaveRetryMilliseconds : 0);
                    if (Current()) await TryCompleteWithinTransitionAsync(session, run, Current);
                });
            }
            finally { gate.Release(); }
        }
    }
}
