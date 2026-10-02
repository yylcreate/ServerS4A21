using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarCombatRuntimeSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_COMBAT_RUNTIME selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }

            Section("phase timing and count", () => CheckTiming(Check));
            Section("phase alive choice", () => CheckSelection(Check));
            Section("scope and projection retry", () => CheckRetry(Check));
            Section("partial batch projection", () => CheckPartialProjection(Check));
            Section("signed sequence allocation", () => CheckSequence(Check));
            Section("shared active clock", () => CheckEnergyClock(Check));
            Section("definition admission and close", () => CheckBoundary(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_COMBAT_RUNTIME assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckTiming(Action<string, bool> check)
        {
            var identity = Identity();
            var finite = Runtime(Phases((5000, "1 -1 -1")), "1 1 1000 1000 2", firstSequence: 3);
            check("unbound combat has no scheduled work", finite.NextDueActiveMilliseconds == null);
            finite.BindLoadingRelease(identity, 1);
            check("initial phase boundary chooses a future first delay without reserving an empty batch",
                finite.NextDueActiveMilliseconds == 0
                && !finite.TryReserveDue(0, identity, 1, out var empty, out _)
                && empty == null && finite.NextDueActiveMilliseconds == 1000 && finite.AliveCount == 0);
            check("a timer before first delay does not materialize a unit",
                !finite.TryReserveDue(999, identity, 1, out _, out _)
                && finite.NextDueActiveMilliseconds == 1000);
            var first = Reserve(finite, 1000);
            check("first due freezes the configured object with a sequence after all static centers",
                first.PhaseIndex == 0 && first.DueActiveMilliseconds == 1000 && first.Spawns.Count == 1
                && first.Spawns[0].SequenceId == 3 && first.Spawns[0].Object.Id == 1
                && finite.AliveCount == 0);
            Publish(finite, first);
            check("registered first unit becomes alive and the repeat delay stays relative to its scheduled time",
                finite.AliveCount == 1 && finite.NextDueActiveMilliseconds == 2000
                && !finite.TryReserveDue(1999, identity, 1, out _, out _));
            var second = Reserve(finite, 2000);
            Publish(finite, second);
            check("finite maximum counts committed occurrences and stops after the second unit",
                second.Spawns[0].SequenceId == 4 && finite.AliveCount == 2
                && finite.NextDueActiveMilliseconds == 5000
                && !finite.TryReserveDue(3000, identity, 1, out _, out _));
            check("final phase end stops scheduling while existing actors can still report canonical deaths",
                !finite.TryReserveDue(5000, identity, 1, out _, out _)
                && finite.NextDueActiveMilliseconds == null
                && finite.CanAcceptActorDeath(identity, 1, first.Spawns[0].SequenceId));
            finite.Close();

            var periodic = Runtime(Phases((5000, "1 -1 -1")), "1 1 1000 1000 0");
            periodic.BindLoadingRelease(identity, 1);
            for (var active = 1000; active < 5000; active += 1000)
                Publish(periodic, Reserve(periodic, active));
            check("maximum count zero means unlimited periodic units strictly before phase end",
                periodic.AliveCount == 4 && periodic.NextDueActiveMilliseconds == 5000
                && !periodic.TryReserveDue(5000, identity, 1, out _, out _)
                && periodic.NextDueActiveMilliseconds == null);
            periodic.Close();

            var late = Runtime(Phases((2000, "1 -1 -1"), (2000, "2 -1 -1")),
                "1 1 500 500 0", "2 2 1000 500 1");
            late.BindLoadingRelease(identity, 1);
            check("late initial callback skips all expired-phase actions and advances to the current phase deadline",
                !late.TryReserveDue(2500, identity, 1, out _, out _)
                && late.NextDueActiveMilliseconds == 3000 && late.AliveCount == 0);
            var current = Reserve(late, 3000);
            Publish(late, current);
            check("the late callback cannot project an actor from the previous map phase",
                current.PhaseIndex == 1 && current.DueActiveMilliseconds == 3000
                && current.Spawns[0].Object.Id == 2 && late.AliveCount == 1);
            check("a backward elapsed sample cannot spawn another occurrence",
                !late.TryReserveDue(1000, identity, 1, out _, out _) && late.AliveCount == 1);
            late.Close();
        }

        private static void CheckSelection(Action<string, bool> check)
        {
            const string choices = "1 -1 -1 2 -1 -1 3 1 3 4 1 1";
            var map = Phases((1000, "1 -1 -1"), (2000, choices));
            var actions = new[] { "1 1 0 500 1", "2 2 0 500 1", "3 3 0 500 1", "4 4 0 500 2" };
            var identity = Identity();
            var matching = Runtime(map, actions);
            matching.BindLoadingRelease(identity, 1);
            var original = Reserve(matching, 0);
            Publish(matching, original);
            var matched = Reserve(matching, 1000);
            Publish(matching, matched);
            check("phase start chooses the last inclusive alive range over every default",
                matched.Spawns[0].Object.Id == 4 && matched.PhaseIndex == 1);
            matching.TryApplyDeath(identity, 1, original.Spawns[0].SequenceId, out _);
            matching.TryApplyDeath(identity, 1, matched.Spawns[0].SequenceId, out _);
            var frozen = Reserve(matching, 1500);
            Publish(matching, frozen);
            check("alive changes inside a phase never reselect its frozen action",
                frozen.Spawns[0].Object.Id == 4 && matching.AliveCount == 1);
            matching.Close();

            var fallback = Runtime(map, actions);
            fallback.BindLoadingRelease(identity, 1);
            var dead = Reserve(fallback, 0);
            Publish(fallback, dead);
            fallback.TryApplyDeath(identity, 1, dead.Spawns[0].SequenceId, out _);
            var defaultBatch = Reserve(fallback, 1000);
            check("no matching range selects the last default rather than the first one",
                defaultBatch.Spawns[0].Object.Id == 2);
            fallback.Close();

            var noChoice = Runtime(Phases((1000, "1 -1 -1"), (1000, "2 0 0")),
                "1 1 0 500 1", "2 2 0 500 1");
            noChoice.BindLoadingRelease(identity, 1);
            Publish(noChoice, Reserve(noChoice, 0));
            check("a phase without matching or default action advances directly to its end",
                !noChoice.TryReserveDue(1000, identity, 1, out _, out _)
                && noChoice.NextDueActiveMilliseconds == 2000 && noChoice.AliveCount == 1);
            noChoice.Close();
        }

        private static void CheckRetry(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = Runtime(Phases((5000, "1 -1 -1")), "1 1 0 500 2");
            check("invalid loading scopes cannot bind combat", !runtime.BindLoadingRelease(default, 1)
                && !runtime.BindLoadingRelease(identity, 0));
            runtime.BindLoadingRelease(identity, 7);
            check("same loading release is idempotent and another generation cannot replace it",
                runtime.BindLoadingRelease(identity, 7) && !runtime.BindLoadingRelease(identity, 8));
            foreach (var mismatch in new[]
            {
                Identity(instanceId: 2), Identity(runId: 2), Identity(runGeneration: 2), Identity(roomId: 2)
            })
                check("old instance, run, generation or room cannot advance combat time",
                    !runtime.TryReserveDue(1000000, mismatch, 7, out _, out _)
                    && runtime.NextDueActiveMilliseconds == 0);
            check("stale loading generation and negative active elapsed cannot reserve actors",
                !runtime.TryReserveDue(1000000, identity, 8, out _, out _)
                && !runtime.TryReserveDue(-1, identity, 7, out _, out _)
                && runtime.NextDueActiveMilliseconds == 0);
            var first = Reserve(runtime, 0, generation: 7);
            var sequence = first.Spawns[0].SequenceId;
            check("unregistered reservation cannot receive a death or be marked as projected",
                !runtime.CanAcceptActorDeath(identity, 7, sequence)
                && !runtime.TryMarkProjected(first) && !runtime.TryRecordSpawnProjected(first, 0)
                && runtime.GetProjectionOffset(first) == 0 && runtime.AliveCount == 0);
            check("failed registration retains the same reservation and actor identity on retry",
                runtime.RetainForRetry(first)
                && runtime.TryReserveDue(100, identity, 7, out var retry, out _)
                && ReferenceEquals(first, retry) && retry.Spawns[0].SequenceId == sequence);
            check("registered batch commit is idempotent and activates exactly one live unit",
                runtime.TryCommitRegistered(first) && runtime.TryCommitRegistered(first)
                && runtime.AliveCount == 1 && runtime.CanAcceptActorDeath(identity, 7, sequence));
            check("failed projection retries the existing registered batch without reallocating or recounting",
                runtime.RetainForRetry(first)
                && runtime.TryReserveDue(200, identity, 7, out var projectionRetry, out _)
                && ReferenceEquals(first, projectionRetry) && runtime.IsCurrent(first)
                && runtime.TryCommitRegistered(projectionRetry) && runtime.AliveCount == 1);
            var forged = new GuardianAltarSpawnBatch(identity, 7, first.WaveIdentity, 0, 0, first.Spawns.ToArray());
            check("a forged reservation cannot commit or project the real actor batch",
                !runtime.TryCommitRegistered(forged) && !runtime.TryMarkProjected(forged));
            check("completed projection clears pending work once and duplicate completion is idempotent",
                !runtime.TryMarkProjected(first) && runtime.TryRecordSpawnProjected(first, 0)
                && runtime.TryMarkProjected(first) && runtime.TryMarkProjected(first)
                && runtime.TryCommitRegistered(first) && !runtime.IsCurrent(first)
                && runtime.NextDueActiveMilliseconds == 500 && runtime.AliveCount == 1);
            check("different room, loading generation and unknown sequence cannot apply deaths",
                !runtime.TryApplyDeath(Identity(roomId: 2), 7, sequence, out _)
                && !runtime.TryApplyDeath(identity, 8, sequence, out _)
                && !runtime.TryApplyDeath(identity, 7, 0, out _) && runtime.AliveCount == 1);
            check("canonical death updates the live roster once and replay succeeds without another decrement",
                runtime.TryApplyDeath(identity, 7, sequence, out var firstDeath) && firstDeath
                && runtime.TryApplyDeath(identity, 7, sequence, out var duplicateDeath) && !duplicateDeath
                && runtime.AliveCount == 0 && !runtime.CanAcceptActorDeath(identity, 7, sequence));
            runtime.Close();
        }

        private static void CheckPartialProjection(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = Runtime(Phases((5000, "1 -1 -1")), "1 1 0 500 1 2 0 500 1");
            runtime.BindLoadingRelease(identity, 1);
            var batch = Reserve(runtime, 0);
            runtime.TryCommitRegistered(batch);
            check("projection checkpoint requires ordered, in-range registered frame writes",
                batch.Spawns.Count == 2 && runtime.GetProjectionOffset(batch) == 0
                && !runtime.TryRecordSpawnProjected(batch, 1)
                && !runtime.TryRecordSpawnProjected(batch, -1)
                && !runtime.TryRecordSpawnProjected(batch, 2));
            check("an unknown first write outcome keeps the same batch and first retry offset",
                runtime.RetainForRetry(batch)
                && runtime.TryReserveDue(100, identity, 1, out var firstRetry, out _)
                && ReferenceEquals(firstRetry, batch) && runtime.GetProjectionOffset(firstRetry) == 0);
            check("a completed first frame advances once and repeated recording is idempotent",
                runtime.TryRecordSpawnProjected(batch, 0) && runtime.TryRecordSpawnProjected(batch, 0)
                && runtime.GetProjectionOffset(batch) == 1 && !runtime.TryMarkProjected(batch));
            check("failed second frame retries the stable batch from offset one without resending frame zero",
                runtime.RetainForRetry(batch)
                && runtime.TryReserveDue(200, identity, 1, out var secondRetry, out _)
                && ReferenceEquals(secondRetry, batch) && runtime.GetProjectionOffset(secondRetry) == 1
                && runtime.TryCommitRegistered(secondRetry) && runtime.AliveCount == 2);
            check("batch completion requires the final frame checkpoint and rejects subsequent frame mutations",
                runtime.TryRecordSpawnProjected(batch, 1) && runtime.GetProjectionOffset(batch) == 2
                && runtime.TryMarkProjected(batch) && runtime.TryMarkProjected(batch)
                && runtime.GetProjectionOffset(batch) == -1 && !runtime.TryRecordSpawnProjected(batch, 0));
            var forged = new GuardianAltarSpawnBatch(identity, 1, batch.WaveIdentity,
                batch.PhaseIndex, batch.DueActiveMilliseconds, batch.Spawns.ToArray());
            check("foreign and completed batches cannot borrow another projection checkpoint",
                runtime.GetProjectionOffset(forged) == -1 && !runtime.TryRecordSpawnProjected(forged, 0));
            runtime.Close();
            check("closed runtime invalidates every pending or completed projection checkpoint",
                runtime.GetProjectionOffset(batch) == -1 && !runtime.TryRecordSpawnProjected(batch, 1));
        }

        private static void CheckSequence(Action<string, bool> check)
        {
            var map = Phases((5000, "1 -1 -1"));
            var identity = Identity();
            check("zero and unsigned-only sequence seeds are rejected before creating actors",
                Throws<ArgumentOutOfRangeException>(() => Runtime(map, "1 1 0 500 1", firstSequence: 0))
                && Throws<ArgumentOutOfRangeException>(() => Runtime(map, "1 1 0 500 1", firstSequence: 32768)));
            var last = Runtime(map, "1 1 0 500 0", firstSequence: short.MaxValue);
            last.BindLoadingRelease(identity, 1);
            var validLast = Reserve(last, 0);
            Publish(last, validLast);
            check("maximum positive signed sequence is valid and a later occurrence cannot wrap or truncate",
                validLast.Spawns[0].SequenceId == short.MaxValue
                && !last.TryReserveDue(500, identity, 1, out var exhausted, out var reason)
                && exhausted == null && reason == "sequence-space-exhausted"
                && last.NextDueActiveMilliseconds == null && last.AliveCount == 1);
            last.Close();

            var atomic = Runtime(map, "1 1 0 500 1 2 0 500 1", firstSequence: short.MaxValue);
            atomic.BindLoadingRelease(identity, 1);
            check("a simultaneous batch without enough signed sequence space allocates no partial actors",
                !atomic.TryReserveDue(0, identity, 1, out _, out var atomicReason)
                && atomicReason == "sequence-space-exhausted" && atomic.AliveCount == 0
                && atomic.TryAllocateSequence(identity, 1, out var retainedLast) && retainedLast == short.MaxValue
                && !atomic.TryAllocateSequence(identity, 1, out _));
            atomic.Close();

            var shared = Runtime(map, "1 1 0 500 1", firstSequence: 4);
            shared.BindLoadingRelease(identity, 1);
            shared.TryAllocateSequence(identity, 1, out var friend);
            var enemy = Reserve(shared, 0);
            Publish(shared, enemy);
            shared.TryAllocateSequence(identity, 1, out var anotherFriend);
            check("friendly reservations and enemy batches use the same instance allocator without changing enemy count",
                friend == 4 && enemy.Spawns[0].SequenceId == 5 && anotherFriend == 6
                && shared.AliveCount == 1 && !shared.CanAcceptActorDeath(identity, 1, friend));
            shared.Close();
        }

        private static void CheckEnergyClock(Action<string, bool> check)
        {
            var identity = Identity();
            var energyDefinition = GuardianAltarEnergyConfigFile.Parse(
                "[gage up rate]\n0 1 1000\n[/gage up rate]\n"
                + "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n");
            var energy = new GuardianAltarEnergyRuntime(energyDefinition);
            var combat = Runtime(Phases((5000, "1 -1 -1")), "1 1 3000 500 1");
            energy.BindLoadingRelease(identity, 1);
            combat.BindLoadingRelease(identity, 1);
            energy.TrySetPaused(identity, 1, false, 0, out _);
            var saturated = energy.ReadSnapshot(3500);
            var due = Reserve(combat, saturated.ActiveMilliseconds);
            Publish(combat, due);
            check("full energy does not stop the injected active clock used by enemy waves",
                saturated.EnergyFixed == 1000 && saturated.ActiveMilliseconds == 3500
                && due.DueActiveMilliseconds == 3000 && combat.AliveCount == 1);
            energy.TrySetPaused(identity, 1, true, 3500, out _);
            check("combat consumes the frozen shared active time without running another pause clock",
                !combat.TryReserveDue(energy.ReadSnapshot(100000).ActiveMilliseconds, identity, 1, out _, out _)
                && combat.NextDueActiveMilliseconds == 5000);
            energy.TrySetPaused(identity, 1, false, 100000, out _);
            check("shared resumed time reaches the original phase end without any independent timer counter",
                !combat.TryReserveDue(energy.ReadSnapshot(101500).ActiveMilliseconds, identity, 1, out _, out _)
                && combat.NextDueActiveMilliseconds == null);
            energy.Close();
            combat.Close();
        }

        private static void CheckBoundary(Action<string, bool> check)
        {
            var map = GuardianAltarMapFile.Parse(Phases((1000, "1 -1 -1")));
            var action = "1 1 0 500 1";
            var largePrototype = GuardianAltarSummonDataFile.Parse(Definition(
                "1 `enemy` `normal` 2147483647 255 -5 -100 200 0", action));
            var preserved = new GuardianAltarCombatRuntime(map, largePrototype, 1);
            preserved.BindLoadingRelease(Identity(), 1);
            var fullWidth = Reserve(preserved, 0).Spawns[0].Object;
            check("runtime preserves full-width prototype, level and resource coordinates without wire truncation",
                fullWidth.PrototypeIndex == int.MaxValue && fullWidth.Level == 255
                && fullWidth.RawParameter == -5 && fullWidth.X == -100);
            preserved.Close();
            var overLevel = GuardianAltarSummonDataFile.Parse(Definition("1 `enemy` `normal` 64599 256 1 10 20 0", action));
            var friend = GuardianAltarSummonDataFile.Parse(Definition("1 `friend` `normal` 64599 1 1 10 20 0", action));
            check("unsupported registry levels and friend-only action entries fail closed during enemy admission",
                overLevel.IsValid && friend.IsValid
                && Throws<InvalidDataException>(() => new GuardianAltarCombatRuntime(map, overLevel, 1))
                && Throws<InvalidDataException>(() => new GuardianAltarCombatRuntime(map, friend, 1)));
            check("unknown action references cannot create fallback enemy templates",
                Throws<InvalidDataException>(() => Runtime(Phases((1000, "9 -1 -1")), action)));

            var closed = Runtime(Phases((1000, "1 -1 -1")), action);
            var identity = Identity();
            closed.BindLoadingRelease(identity, 1);
            var pending = Reserve(closed, 0);
            closed.TryCommitRegistered(pending);
            var timer = closed.Timers.Begin(new RunTimerKey("guardian-altar", "wave-test"));
            closed.Close();
            closed.Close();
            check("close cancels the instance timer ticket and invalidates all deferred actor work",
                closed.IsClosed && closed.NextDueActiveMilliseconds == null && closed.AliveCount == 0
                && !closed.Timers.IsCurrent(timer) && !closed.BindLoadingRelease(identity, 1)
                && !closed.TryReserveDue(0, identity, 1, out _, out _)
                && !closed.TryCommitRegistered(pending) && !closed.TryMarkProjected(pending)
                && !closed.RetainForRetry(pending) && !closed.TryAllocateSequence(identity, 1, out _)
                && !closed.TryApplyDeath(identity, 1, pending.Spawns[0].SequenceId, out _));
        }

        private static GuardianAltarCombatRuntime Runtime(string map, string action, int firstSequence = 1)
            => Runtime(map, new[] { action }, firstSequence);

        private static GuardianAltarCombatRuntime Runtime(string map, string firstAction, string secondAction)
            => Runtime(map, new[] { firstAction, secondAction });

        private static GuardianAltarCombatRuntime Runtime(string map, string[] actions, int firstSequence = 1)
            => new GuardianAltarCombatRuntime(GuardianAltarMapFile.Parse(map),
                GuardianAltarSummonDataFile.Parse(Definition(
                    "1 `enemy` `normal` 64599 1 1 1682 265 0\n"
                    + "2 `enemy` `normal` 64600 1 1 1682 265 0\n"
                    + "3 `enemy` `named` 64601 1 1 1682 265 0\n"
                    + "4 `enemy` `boss` 64602 1 1 1682 265 0", actions)), firstSequence);

        private static string Definition(string objects, params string[] actions)
            => "[summon object]\n" + objects + "\n[/summon object]\n[action define]\n"
                + string.Concat(actions.Select(x => "[normal]\n" + x + "\n[/normal]\n"))
                + "[/action define]\n";

        private static string Phases(params (int Duration, string Selections)[] phases)
            => "[center index]\n64597\n[/center index]\n[time line]\n"
                + string.Concat(phases.Select(x => "[phase]\n[duration]\n" + x.Duration
                    + "\n[/duration]\n[type]\n`normal`\n[/type]\n[action assign]\n"
                    + x.Selections + "\n[/action assign]\n[/phase]\n")) + "[/time line]\n";

        private static GuardianAltarSpawnBatch Reserve(GuardianAltarCombatRuntime runtime, long active,
            long generation = 1)
        {
            if (!runtime.TryReserveDue(active, Identity(), generation, out var batch, out var reason))
                throw new InvalidOperationException("Expected a due Guardian batch: " + reason);
            return batch;
        }

        private static void Publish(GuardianAltarCombatRuntime runtime, GuardianAltarSpawnBatch batch)
        {
            if (!runtime.TryCommitRegistered(batch))
                throw new InvalidOperationException("Expected Guardian batch registration to complete.");
            for (var index = runtime.GetProjectionOffset(batch); index < batch.Spawns.Count; index++)
                if (!runtime.TryRecordSpawnProjected(batch, index))
                    throw new InvalidOperationException("Expected Guardian frame projection to complete.");
            if (!runtime.TryMarkProjected(batch))
                throw new InvalidOperationException("Expected registered Guardian batch projection to complete.");
        }

        private static DungeonParticipantRoomIdentity Identity(long instanceId = 1, long runId = 1,
            long runGeneration = 1, long roomId = 1) => new DungeonParticipantRoomIdentity(
                new DungeonRunIdentity(instanceId, runId, runGeneration),
                new DungeonRoomIdentity(new DungeonInstanceIdentity(instanceId), roomId));

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return true; }
            return false;
        }
    }
}
