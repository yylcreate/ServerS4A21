using System;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarCombatRuntimeSelfTest
    {
        private static void CheckSurvivalRounds(Action<string, bool> check)
        {
            var first = GuardianAltarMapFile.Parse(Phases((1000, "1 -1 -1")));
            var second = GuardianAltarMapFile.Parse(Phases((1000, "2 -1 -1")));
            var rounds = new[] { SurvivalRound(1, 50001, first), SurvivalRound(2, 50002, second),
                SurvivalRound(3, 50001, first) };
            var runtime = SurvivalRuntime(rounds);
            var identity = Identity();
            check("a survival sequence begins at round one without an earned clear or a second loading scope",
                runtime.CurrentRound == 1 && runtime.ClearedRounds == 0
                && ReferenceEquals(runtime.CurrentRoundPlan, rounds[0])
                && !runtime.TryReserveRoundAdvance(identity, 1, 10000, out _));
            runtime.BindLoadingRelease(identity, 1);
            var enemy = Reserve(runtime, 0);
            runtime.TryCommitRegistered(enemy);
            runtime.TryApplyDeath(identity, 1, enemy.Spawns[0].SequenceId, out _);
            check("a pending spawn projection prevents both a round transition and a terminal reward fact",
                !runtime.TryReserveRoundAdvance(identity, 1, 1000, out _)
                && !runtime.TryComplete(identity, 1, 1000, out _) && runtime.ClearedRounds == 0);
            Publish(runtime, enemy);
            runtime.TryReserveDue(1000, identity, 1, out _, out _);
            check("the first completed timeline reserves round two instead of completing the whole run",
                runtime.TryReserveRoundAdvance(identity, 1, 1000, out var token)
                && token.CompletedRound == 1 && ReferenceEquals(token.NextRoundPlan, rounds[1])
                && runtime.ClearedRounds == 1 && runtime.CurrentRound == 1
                && !runtime.TryComplete(identity, 1, 1000, out _) && runtime.TerminalFact == null);
            runtime.TryReserveRoundAdvance(identity, 1, 1100, out token);
            check("a failed next-round write retains the exact token and blocks actors without losing the cleared round",
                runtime.TryReserveRoundAdvance(identity, 1, 1500, out var retry)
                && ReferenceEquals(token, retry) && runtime.IsCurrent(token)
                && !runtime.TryReserveDue(1500, identity, 1, out _, out _)
                && !runtime.TryAllocateSequence(identity, 1, out _)
                && runtime.CurrentRound == 1 && runtime.ClearedRounds == 1);
            var forged = new GuardianAltarRoundAdvanceToken(identity, 1, 1, rounds[1]);
            check("a forged or wrong-generation token cannot activate the frozen round",
                !runtime.IsCurrent(forged) && !runtime.TryMarkRoundProjected(forged, 1500)
                && !runtime.TryReserveRoundAdvance(identity, 2, 99999, out _)
                && !runtime.TryReserveRoundAdvance(Identity(roomId: 2), 1, 99999, out _)
                && !runtime.TryMarkRoundProjected(token, -1) && runtime.CurrentRound == 1);
            check("only a completed round write activates its relative timeline at the current shared clock",
                runtime.TryMarkRoundProjected(token, 2000) && !runtime.IsCurrent(token)
                && runtime.CurrentRound == 2 && runtime.NextDueActiveMilliseconds == 2000
                && runtime.TryMarkRoundProjected(token, 90000) && runtime.NextDueActiveMilliseconds == 2000
                && !runtime.TryCommitRegistered(enemy) && !runtime.TryMarkProjected(enemy));
            var next = Reserve(runtime, 2000);
            Publish(runtime, next);
            check("round two retains phase-zero identity while using its own action and a strictly later actor sequence",
                next.PhaseIndex == 0 && next.DueActiveMilliseconds == 2000
                && next.Spawns[0].Object.Id == 2
                && next.Spawns[0].SequenceId > enemy.Spawns[0].SequenceId);
            runtime.TryReserveDue(3000, identity, 1, out _, out _);
            check("a completed timeline with an authoritative living enemy cannot advance or earn a round",
                !runtime.TryReserveRoundAdvance(identity, 1, 3000, out _)
                && runtime.ClearedRounds == 1 && !runtime.TryComplete(identity, 1, 3000, out _));
            runtime.TryApplyDeath(identity, 1, next.Spawns[0].SequenceId, out _);
            runtime.TryReserveRoundAdvance(identity, 1, 3500, out var finalToken);
            runtime.TryMarkRoundProjected(finalToken, 4000);
            var finalEnemy = Reserve(runtime, 4000);
            Publish(runtime, finalEnemy);
            check("a repeated MAP is a new ordered round without reusing actor IDs or the previous round token",
                runtime.CurrentRound == 3 && runtime.ClearedRounds == 2
                && ReferenceEquals(runtime.CurrentRoundPlan, rounds[2])
                && finalEnemy.Spawns[0].Object.Id == 1
                && finalEnemy.Spawns[0].SequenceId > next.Spawns[0].SequenceId
                && !runtime.TryMarkRoundProjected(token, 99999)
                && runtime.TryApplyDeath(identity, 1, enemy.Spawns[0].SequenceId, out var changed) && !changed
                && runtime.AliveCount == 1);
            runtime.TryApplyDeath(identity, 1, finalEnemy.Spawns[0].SequenceId, out _);
            runtime.TryReserveDue(5000, identity, 1, out _, out _);
            check("only the last fully cleared round freezes one successful terminal fact and its exact score",
                !runtime.TryReserveRoundAdvance(identity, 1, 5000, out _)
                && runtime.TryComplete(identity, 1, 5000, out var success)
                && success.ClearedRounds == 3 && runtime.ClearedRounds == 3
                && runtime.TryComplete(identity, 1, 90000, out var replay) && ReferenceEquals(success, replay)
                && success.ActiveMilliseconds == 5000 && runtime.NextDueActiveMilliseconds == null);
            runtime.Close();
        }

        private static void CheckSurvivalFailureAndClock(Action<string, bool> check)
        {
            var map = GuardianAltarMapFile.Parse(Phases((1000, "1 -1 -1")));
            var rounds = Enumerable.Range(1, 3).Select(round => SurvivalRound(round, 50001, map)).ToArray();
            var identity = Identity();
            var runtime = SurvivalRuntime(rounds, centers: new ushort[] { 0, 1 });
            var energy = new GuardianAltarEnergyRuntime(GuardianAltarEnergyConfigFile.Parse(
                "[gage up rate]\n0 2 1000\n1 4 2000\n[/gage up rate]\n"
                + "[gage upgrade cost percent]\n0.5\n[/gage upgrade cost percent]\n"));
            runtime.BindLoadingRelease(identity, 7);
            energy.BindLoadingRelease(identity, 7);
            energy.TrySetPaused(identity, 7, false, 0, out _);
            var first = Reserve(runtime, 0, 7);
            Publish(runtime, first);
            runtime.TryApplyDeath(identity, 7, first.Spawns[0].SequenceId, out _);
            var upgraded = energy.TryUpgrade(identity, 7, 0, 1000, out _, out _);
            runtime.TryReserveDue(1000, identity, 7, out _, out _);
            runtime.TryReserveRoundAdvance(identity, 7, 1000, out var token);
            energy.TrySetPaused(identity, 7, true, 1000, out var paused);
            runtime.TryMarkRoundProjected(token, paused.ActiveMilliseconds);
            var unchanged = energy.ReadSnapshot(9000);
            check("round projection preserves the single energy owner, upgraded level, balance and pause clock",
                upgraded && unchanged.Level == 1 && unchanged.Identity.Equals(identity)
                && unchanged.LoadingGeneration == 7 && unchanged.Paused
                && unchanged.ActiveMilliseconds == paused.ActiveMilliseconds && unchanged.Level == paused.Level
                && unchanged.EnergyFixed == paused.EnergyFixed
                && runtime.NextDueActiveMilliseconds == paused.ActiveMilliseconds);
            runtime.TryFailCenter(identity, 7, 0, paused.ActiveMilliseconds, out _);
            check("a previously dead center remains dead in a later round while another center still survives",
                !runtime.CanAcceptCenterDeath(identity, 7, 0) && runtime.CanAcceptCenterDeath(identity, 7, 1)
                && runtime.TerminalFact == null && runtime.CurrentRound == 2);
            var enemy = Reserve(runtime, paused.ActiveMilliseconds, 7);
            Publish(runtime, enemy);
            var ticket = runtime.Timers.Begin(DfoServer.Game.Dungeon.DungeonRunTimerKeys.GuardianAltarWaveSchedule);
            check("death during round two fails the existing run with only round one earned and cancels its timer",
                runtime.TryFailCharacterDeath(identity, 7, paused.ActiveMilliseconds, out var failed)
                && failed.ClearedRounds == 1 && failed.Result == GuardianAltarTerminalResult.Failed
                && !runtime.Timers.IsCurrent(ticket) && runtime.AliveCount == 1
                && !runtime.TryReserveRoundAdvance(identity, 7, 99999, out _)
                && !runtime.TryMarkRoundProjected(token, 99999));
            var frozen = runtime.TerminalFact;
            runtime.Close();
            check("closing survival after failure retains the same event and previously earned score",
                ReferenceEquals(frozen, runtime.TerminalFact) && runtime.TerminalFact.ClearedRounds == 1);
            energy.Close();

            var abort = SurvivalRuntime(rounds);
            abort.BindLoadingRelease(identity, 1);
            var batch = Reserve(abort, 0);
            Publish(abort, batch);
            abort.TryApplyDeath(identity, 1, batch.Spawns[0].SequenceId, out _);
            abort.TryReserveDue(1000, identity, 1, out _, out _);
            abort.TryReserveRoundAdvance(identity, 1, 1000, out var retained);
            abort.Close();
            check("exit during a retained next-round write records Aborted without inventing the next clear",
                abort.TerminalFact is { Result: GuardianAltarTerminalResult.Aborted, ClearedRounds: 1 }
                && !abort.IsCurrent(retained) && !abort.TryMarkRoundProjected(retained, 2000)
                && !abort.TryComplete(identity, 1, 99999, out _));
        }

        private static void CheckRealSurvivalSequence(Action<string, bool> check)
        {
            var definition = GuardianAltarMenuDefinition.Load();
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var entry = list.GetById(definition.RidableId)
                ?? throw new InvalidOperationException("Current Guardian stage-map registration is missing.");
            var stages = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText("stagemap/" + entry.FilePath));
            if (!stages.IsValid)
                throw new InvalidOperationException(stages.InvalidReason);
            var survival = stages.Stages.Single(stage => stage.IsSurvival);
            var progress = new GuardianAltarProgressSnapshot(1,
                new GuardianAltarHeader(definition.RidableId, 3, 0, 0, 0, 0, 0, 0, 0, 0),
                stages.Stages.Select(stage => new GuardianAltarStage(stage.DungeonId, stage.IsSurvival ? -1 : 0)),
                definition.InitialPurchases, definition.InitialSlots, Array.Empty<GuardianAltarAchievement>());
            var plan = definition.CreateStartPlan(survival.DungeonId, 0, progress);
            var runtime = new GuardianAltarCombatRuntime(plan.CombatMap, plan.SummonData,
                Math.Max(1, plan.Centers.Count), plan.Centers.Select((center, index) => (center, index))
                    .Where(pair => pair.center.IsFailureCenter).Select(pair => checked((ushort)pair.index)),
                plan.SurvivalRounds);
            var identity = Identity();
            runtime.BindLoadingRelease(identity, 1);
            check("real survival compiles all 99 repeated resource entries with independent byte-sized phase indices",
                plan.SurvivalRounds.Count == 99 && plan.SurvivalRounds.Sum(round => round.Phases.Count) > byte.MaxValue
                && runtime.CurrentRound == 1 && plan.SurvivalRounds.All(round => round.Phases.Count <= byte.MaxValue + 1));
            long active = 0;
            var advanced = 0;
            var spawned = 0;
            var steps = 0;
            foreach (var round in plan.SurvivalRounds)
            {
                if (!ReferenceEquals(runtime.CurrentRoundPlan, round))
                    throw new InvalidOperationException("Real survival source ordering was lost.");
                var end = checked(active + round.DurationMilliseconds);
                while (runtime.NextDueActiveMilliseconds is { } due && due <= end)
                {
                    if (++steps > 100000)
                        throw new InvalidOperationException("Real survival produced an unbounded or stalled wave schedule.");
                    if (runtime.TryReserveDue(due, identity, 1, out var batch, out var reason))
                    {
                        Publish(runtime, batch);
                        foreach (var spawn in batch.Spawns)
                        {
                            runtime.TryApplyDeath(identity, 1, spawn.SequenceId, out _);
                            spawned++;
                        }
                    }
                    else if (!string.IsNullOrEmpty(reason))
                        throw new InvalidOperationException("Real survival wave admission failed: " + reason);
                    // A fully ended round exposes its immediate continuation
                    // deadline. Project it below before scheduling more actors.
                    if (due == end)
                        break;
                }
                active = end;
                runtime.TryReserveDue(active, identity, 1, out _, out _);
                if (round.RoundNumber < plan.SurvivalRounds.Count)
                {
                    if (!runtime.TryReserveRoundAdvance(identity, 1, active, out var token)
                        || !runtime.TryMarkRoundProjected(token, active) || runtime.TerminalFact != null)
                        throw new InvalidOperationException("Real survival must continue without an intermediate terminal fact.");
                    advanced++;
                }
            }
            check("the full real ordered sequence continues 98 times and ends once at exactly 99 cleared rounds",
                spawned > 0 && advanced == 98 && runtime.CurrentRound == 99
                && runtime.TryComplete(identity, 1, active, out var fact) && fact.ClearedRounds == 99
                && fact.ActiveMilliseconds == active && fact.Result == GuardianAltarTerminalResult.Succeeded);
            Console.WriteLine($"GUARDIAN_ALTAR_SURVIVAL_ROUNDS rounds={plan.SurvivalRounds.Count} "
                + $"phases={plan.SurvivalRounds.Sum(round => round.Phases.Count)} actors={spawned} "
                + $"scheduleSteps={steps} activeMs={active}");
            runtime.Close();
        }

        private static GuardianAltarSurvivalRoundPlan SurvivalRound(int round, int mapId, GuardianAltarMapFile map)
        {
            var start = 0;
            return new GuardianAltarSurvivalRoundPlan(round, mapId, round - 1, map,
                map.Phases.Select((phase, index) =>
                {
                    var summary = new GuardianAltarPhaseSummary(index, start, 0);
                    start += phase.DurationMilliseconds;
                    return summary;
                }).ToArray());
        }

        private static GuardianAltarCombatRuntime SurvivalRuntime(GuardianAltarSurvivalRoundPlan[] rounds,
            ushort[] centers = null) => new GuardianAltarCombatRuntime(rounds[0].CombatMap,
                GuardianAltarSummonDataFile.Parse(Definition(
                    "1 `enemy` `normal` 64599 1 1 1682 265 0\n2 `enemy` `normal` 64600 1 1 1682 265 0",
                    "1 1 0 500 1", "2 2 0 500 1")), centers?.Length ?? 1,
                centers ?? new ushort[] { 0 }, rounds);
    }
}
