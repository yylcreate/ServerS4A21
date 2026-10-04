using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Events;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        private static GuardianAltarProgressSnapshot SurvivalProgress(GuardianAltarMenuDefinition definition,
            GuardianAltarProgressSnapshot source)
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var entry = list.GetById(definition.RidableId)
                ?? throw new InvalidDataException("Current Guardian stage map is not registered.");
            var catalog = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText("stagemap/" + entry.FilePath));
            if (!catalog.IsValid) throw new InvalidDataException(catalog.InvalidReason);
            return Copy(source, stages: catalog.Stages.Select(stage =>
                new GuardianAltarStage(stage.DungeonId, stage.IsSurvival ? -1 : 0)));
        }

        private static byte[] SurvivalRequest(int dungeonId)
        {
            var body = new byte[17];
            BinaryPrimitives.WriteInt32LittleEndian(body, dungeonId);
            body[5] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), uint.MaxValue);
            return body;
        }

        private static void CheckSurvivalPlans(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            var progress = SurvivalProgress(definition, Fresh(definition, 1));
            var dungeonId = definition.GetEligibleSurvivalDungeonIds(progress).Single();
            var source = GuardianAltarDungeonFile.Parse(DungeonCatalog.GetDungeonFile(dungeonId));
            if (!source.IsValid) throw new InvalidDataException(source.InvalidReason);
            var plan = definition.CreateStartPlan(dungeonId, 0, progress);
            check("the real survival DGN freezes all 99 source MAP references, retaining repeated resources",
                source.IsSurvival && source.SurvivalMapIds.Count == 99 && plan.IsSurvival
                && plan.SurvivalRounds.Select(round => round.MapId).SequenceEqual(source.SurvivalMapIds)
                && plan.SurvivalRounds.Select(round => round.MapId).Distinct().Count() == 15
                && plan.SurvivalRounds.Sum(round => round.Phases.Count) > byte.MaxValue);
            check("survival has its own rewards and no ordinary difficulty, next-stage or ordinary clear plan",
                plan.Difficulty == 0 && plan.ClearPlan == null && plan.SurvivalRewards != null
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(dungeonId, 1, progress))
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(dungeonId, 2, progress))
                && Throws<InvalidOperationException>(() => definition.CreateClearProgressPlan(dungeonId, 0)));
            var first = plan.SurvivalRounds[0];
            var firstBody = GuardianAltarDungeonPacketBuilder.BuildStartMap(plan);
            var centerOffset = 24 + 12 * first.Phases.Count;
            check("real first survival START_MAP carries stage one and round one before its complete center list",
                I32(firstBody, 0) == 1 && I32(firstBody, 4) == 1
                && I32(firstBody, 8) == first.MapId && I32(firstBody, 12) == first.DurationMilliseconds
                && I32(firstBody, 16) == first.MapBasisLevel && U32(firstBody, 20) == first.Phases.Count
                && U32(firstBody, centerOffset) == plan.Centers.Count
                && firstBody.Length == centerOffset + 4 + 13 * plan.Centers.Count
                && ReferenceEquals(plan.CombatMap, first.CombatMap));
            check("real first survival resources are MAP 25129 with five phases and 75 seconds",
                first.MapId == 25129 && first.RoundNumber == 1 && first.MapBasisLevel == 0
                && first.Phases.Count == 5 && first.DurationMilliseconds == 75000);
            check("all entry centers have the original sequential IDs, with the failure center retained",
                plan.Centers.Where((center, index) => U16(firstBody, centerOffset + 8 + 13 * index) != index
                    || U16(firstBody, centerOffset + 10 + 13 * index) != center.MonsterCode).Count() == 0
                && plan.Centers.Any(center => center.IsFailureCenter));
            check("a first round cannot use the continuation serializer to omit required centers",
                Throws<ArgumentOutOfRangeException>(() => GuardianAltarDungeonPacketBuilder.BuildSurvivalRound(first)));
            foreach (var round in plan.SurvivalRounds.Skip(1))
            {
                var body = GuardianAltarDungeonPacketBuilder.BuildSurvivalRound(round);
                check("survival round " + round.RoundNumber + " consumes only the 20B header and its independent phase vector",
                    body.Length == 24 + 12 * round.Phases.Count
                    && I32(body, 0) == 1 && I32(body, 4) == round.RoundNumber
                    && I32(body, 8) == round.MapId && I32(body, 12) == round.DurationMilliseconds
                    && I32(body, 16) == round.MapBasisLevel && U32(body, 20) == round.Phases.Count
                    && round.Phases.Where((phase, index) => I32(body, 24 + index * 12) != phase.PhaseIndex
                        || I32(body, 28 + index * 12) != phase.StartsAtMilliseconds
                        || I32(body, 32 + index * 12) != phase.PhaseType).Count() == 0
                    && round.Phases[0].PhaseIndex == 0 && round.Phases[0].StartsAtMilliseconds == 0);
            }
        }

        private static async Task CheckSurvivalEntryAsync(Action<string, bool> check,
            GuardianAltarMenuDefinition definition)
        {
            var progress = SurvivalProgress(definition, Fresh(definition, 1));
            var survivalId = definition.GetEligibleSurvivalDungeonIds(progress).Single();
            var request = SurvivalRequest(survivalId);
            check("current 17B survival selector is accepted with a separate typed mode marker",
                GuardianAltarSelectDungeonRequestParser.TryParse(request, out var command)
                && command.DungeonId == survivalId && command.Difficulty == 0 && command.SurvivalSelector);
            for (var offset = 4; offset < request.Length; offset++)
            {
                var malformed = request.ToArray();
                malformed[offset] = offset is >= 7 and <= 10 ? (byte)0 : (byte)1;
                check("survival selector rejects changed reserved/sentinel byte " + offset,
                    !GuardianAltarSelectDungeonRequestParser.TryParse(malformed, out _));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                fixture.Progress.Transform = value => SurvivalProgress(definition, value);
                var session = await fixture.CreateConnectedSessionAsync(701);
                var handled = await fixture.DispatchAsync(session, request);
                var run = fixture.LastRun;
                var room = fixture.LastRoom;
                check("legal survival SELECT now creates the authoritative Guardian run instead of a failure ACK",
                    handled && run?.RunState == DungeonRunState.Active && room != null
                    && run.Instance.Mechanisms.GuardianAltar.IsSurvival
                    && fixture.Packets.Select(Type).SequenceEqual(new[] {
                        (ushort)NotiPacketTypeA21.USER_STATE, (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_STAGE_INFO,
                        (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP }));
                if (run == null || room == null) throw new InvalidOperationException("Survival entry did not create a run and room.");
                var frozenProgress = run.Instance.Mechanisms.GuardianAltar.Progress;
                var packetCount = fixture.Packets.Count;
                var identity = run.CaptureIdentity();
                await fixture.DispatchAsync(session, request);
                check("duplicate survival SELECT retains its run, first centers, ticket snapshot and single loading barrier",
                    ReferenceEquals(session.Player.CurrentRun, run) && run.Matches(identity)
                    && fixture.Packets.Count == packetCount && fixture.Progress.Reads == 1
                    && fixture.LoadingRegistrations == 1 && frozenProgress.Header.FreeTickets == definition.DailyRefillTicket);
                await fixture.DispatchAsync(session, Request(survivalId));
                check("an active survival run cannot treat the ordinary selector shape as a valid duplicate start",
                    IsRejection(fixture.Packets.Skip(packetCount).ToArray())
                    && ReferenceEquals(session.Player.CurrentRun, run) && run.Matches(identity)
                    && fixture.LoadingRegistrations == 1 && fixture.Progress.Reads == 1);
                var released = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
                GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, released.Generation);
                var pauseHeader = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE };
                await fixture.Coordinator.HandlePauseAsync(session, pauseHeader, new byte[15]);
                fixture.MonotonicMilliseconds = 2500;
                var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
                var before = energy.ReadSnapshot(fixture.MonotonicMilliseconds);
                var loadingGeneration = fixture.LoadingGeneration;
                var round = run.Instance.Mechanisms.GuardianAltar.SurvivalRounds[1];
                GuardianAltarDungeonPacketBuilder.BuildSurvivalRound(round);
                check("continuation projection serialization cannot reset the live room, energy, loading generation or persisted progress",
                    energy.ReadSnapshot(fixture.MonotonicMilliseconds) == before && before.EnergyFixed > 0
                    && fixture.LoadingGeneration == loadingGeneration && fixture.LoadingRegistrations == 1
                    && run.CaptureRoomIdentity().Equals(room.Identity)
                    && ReferenceEquals(run.Instance.Mechanisms.GuardianAltar.Progress, frozenProgress)
                    && run.RewardPolicy.Kind == DungeonRewardPolicyKind.GuardianAltar
                    && !run.RewardPolicy.AllowsMonsterExperience && !run.RewardPolicy.AllowsSettlement);
            }
            foreach (var body in new[] { Request(survivalId), SurvivalRequest(definition.FirstDungeonId) })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.Progress.Transform = value => SurvivalProgress(definition, value);
                var session = fixture.CreateSession(702);
                check("syntactically valid selector mode must match the typed PVF dungeon before lifecycle admission",
                    await fixture.DispatchAsync(session, body) && IsRejection(fixture.Packets)
                    && session.Player.CurrentRun == null && fixture.LoadingRegistrations == 0);
            }
            foreach (var transform in new Func<GuardianAltarProgressSnapshot, GuardianAltarProgressSnapshot>[] {
                value => value,
                value => Copy(SurvivalProgress(definition, value),
                    header: value.Header with { FreeTickets = 0, SecondaryTickets = 0 }) })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.Progress.Transform = transform;
                var session = fixture.CreateSession(703);
                check("locked or ticketless survival selection still fails without ordinary fallback or a new loading barrier",
                    await fixture.DispatchAsync(session, request) && IsRejection(fixture.Packets)
                    && session.Player.CurrentRun == null && fixture.LoadingRegistrations == 0);
            }
            await CheckSurvivalRoundProjectionAsync(check, definition);
        }

        private static async Task CheckSurvivalRoundProjectionAsync(Action<string, bool> check,
            GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            fixture.Progress.Transform = value => SurvivalProgress(definition, value);
            var progress = SurvivalProgress(definition, Fresh(definition, 704));
            var survivalId = definition.GetEligibleSurvivalDungeonIds(progress).Single();
            var session = await fixture.CreateConnectedSessionAsync(704);
            await fixture.DispatchAsync(session, SurvivalRequest(survivalId));
            var run = fixture.LastRun ?? throw new InvalidOperationException("Survival round projection requires an admitted run.");
            var room = fixture.LastRoom;
            var identity = run.CaptureParticipantRoomIdentity();
            var plan = run.Instance.Mechanisms.GuardianAltar;
            var combat = run.Instance.Mechanisms.GuardianAltarCombat;
            var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
            var released = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
            GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, released.Generation);
            await fixture.Coordinator.HandlePauseAsync(session, new GamePacketHeader {
                cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE }, new byte[15]);
            var sourceFence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run, room.Identity);
            bool FirstRoundFence() => sourceFence() && combat.CurrentRound == 1;
            var initialRun = run.CaptureIdentity();
            var initialRoom = room.Identity;
            var initialLoading = fixture.LoadingGeneration;
            var initialProgress = plan.Progress;
            var firstRoundEnd = plan.SurvivalRounds[0].DurationMilliseconds;
            var steps = 0;
            var killed = 0;
            RunTimerTicket finalRoundTicket = default;

            // The fixture uses an unstarted private ClockService. Drive the
            // real Coordinator with its current tickets and monotonic clock,
            // and accept enemy deaths through the real shared room ledger.
            while (combat.CurrentRound == 1 && combat.NextDueActiveMilliseconds is { } due)
            {
                if (++steps > 1000 || due > firstRoundEnd)
                    throw new InvalidOperationException("The current first-round schedule stalled or exceeded its frozen duration.");
                if (!combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var ticket))
                    throw new InvalidOperationException("The Coordinator did not retain a current round timer.");
                fixture.MonotonicMilliseconds = due;
                if (due == firstRoundEnd)
                {
                    finalRoundTicket = ticket;
                    fixture.PauseSendError = new IOException("survival round START_MAP transport failure");
                }
                await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, released.Generation, ticket, FirstRoundFence)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var actor in run.Instance.Mechanisms.DynamicActors.Capture(room.Identity,
                    GuardianAltarCombatRuntime.DynamicActorProvider, released.Generation)
                    .Where(actor => DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, actor)))
                {
                    var source = new DungeonEventEnvelope(Guid.NewGuid(), run.CaptureIdentity(), room.Identity.RoomInstanceId,
                        session.Player.CharacterId, session.Player.CharacterId, actor.SequenceId, actor.ActorCode,
                        "guardian-survival-round-selftest", fixture.MonotonicMilliseconds);
                    var death = room.TryRecordActorDeath(source, actor.SequenceId, actor.ActorCode, actor.ActorType);
                    if (!death.Accepted || !death.Created
                        || !DungeonKillApplicationService.TryApplyGuardianActorDeath(run, actor, death.Fact.Source))
                        throw new InvalidOperationException("The shared room ledger refused a registered survival actor death.");
                    killed++;
                }
                if (due == firstRoundEnd) break;
            }
            var failedAt = energy.ReadSnapshot(fixture.MonotonicMilliseconds);
            check("real first-round enemies and canonical deaths reach one cleared round without creating a terminal result",
                steps > 1 && killed > 0 && combat.AliveCount == 0 && combat.CurrentRound == 1
                && combat.ClearedRounds == 1 && combat.TerminalFact == null
                && failedAt.ActiveMilliseconds == firstRoundEnd && !failedAt.Paused);
            check("failed round-two START_MAP retains the original run, room, loading, controls and completed round",
                fixture.Packets.Count(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP) == 1
                && fixture.LoadingRegistrations == 1 && fixture.LoadingGeneration == initialLoading
                && run.Matches(initialRun) && run.CaptureRoomIdentity().Equals(initialRoom)
                && ReferenceEquals(plan.Progress, initialProgress)
                && combat.TryReserveRoundAdvance(identity, released.Generation, failedAt.ActiveMilliseconds, out var token)
                && combat.IsCurrent(token) && token.CompletedRound == 1 && token.NextRoundPlan.RoundNumber == 2
                && !combat.TryAllocateSequence(identity, released.Generation, out _));
            if (!combat.TryReserveRoundAdvance(identity, released.Generation, failedAt.ActiveMilliseconds, out var retained))
                throw new InvalidOperationException("The failed next-round projection lost its retry token.");
            var packetCount = fixture.Packets.Count;
            await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
            check("a second failed completion reuses the exact pending round token without projecting or consuming progress",
                fixture.Packets.Count == packetCount && combat.IsCurrent(retained)
                && combat.CurrentRound == 1 && combat.ClearedRounds == 1
                && combat.TerminalFact == null && energy.ReadSnapshot(fixture.MonotonicMilliseconds) == failedAt);

            fixture.PauseSendError = null;
            fixture.MonotonicMilliseconds = firstRoundEnd + 500;
            var retryClock = energy.ReadSnapshot(fixture.MonotonicMilliseconds);
            await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
            var continued = energy.ReadSnapshot(fixture.MonotonicMilliseconds);
            var roundFrames = fixture.Packets.Where(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP).ToArray();
            check("successful retry emits exactly the frozen round-two timeline and anchors it to the successful shared-clock instant",
                combat.CurrentRound == 2 && combat.ClearedRounds == 1 && !combat.IsCurrent(retained)
                && combat.NextDueActiveMilliseconds == retryClock.ActiveMilliseconds
                && roundFrames.Length == 2 && roundFrames[^1][0] == 0
                && roundFrames[^1].AsSpan(15).SequenceEqual(GuardianAltarDungeonPacketBuilder.BuildSurvivalRound(plan.SurvivalRounds[1])));
            check("the Coordinator continuation preserves energy, the original room and loading without an intermediate clear or another stage projection",
                continued == retryClock && continued.ActiveMilliseconds == firstRoundEnd + 500
                && continued.Level == failedAt.Level && !continued.Paused
                && run.Matches(initialRun) && run.CaptureRoomIdentity().Equals(initialRoom)
                && fixture.LoadingRegistrations == 1 && fixture.LoadingGeneration == initialLoading
                && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarEnergy, energy)
                && ReferenceEquals(run.Instance.Mechanisms.GuardianAltar.Progress, initialProgress)
                && fixture.Packets.Count(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_STAGE_INFO) == 1
                && fixture.Packets.All(packet => Type(packet) != (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE
                    && Type(packet) != (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE
                    && Type(packet) != (ushort)NotiPacketTypeA21.FINISH_LOADING)
                && combat.TerminalFact == null && !combat.IsTerminal);
            packetCount = fixture.Packets.Count;
            await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
            check("replaying completion after the successful continuation cannot restart round two or add another START_MAP",
                fixture.Packets.Count == packetCount && combat.CurrentRound == 2
                && combat.NextDueActiveMilliseconds == retryClock.ActiveMilliseconds);
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var roundTwoTicket);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, released.Generation, finalRoundTicket, FirstRoundFence)
                .WaitAsync(TimeSpan.FromSeconds(5));
            check("the old round-one timer cannot project or change the now-current round-two schedule",
                fixture.Packets.Count == packetCount && combat.CurrentRound == 2 && combat.ClearedRounds == 1
                && !combat.Timers.IsCurrent(finalRoundTicket) && combat.Timers.IsCurrent(roundTwoTicket)
                && combat.NextDueActiveMilliseconds == retryClock.ActiveMilliseconds);
        }
    }
}
