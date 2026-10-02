using System;
using System.IO;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarEnergyRuntimeSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ENERGY_RUNTIME selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string name, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(name + " exception: " + exception, false); }
            }

            Section("real PVF natural energy", () => CheckRealPvf(Check));
            Section("natural elapsed and pause", () => CheckElapsed(Check));
            Section("energy identity fencing", () => CheckIdentity(Check));
            Section("saturation and monotonic clock", () => CheckClock(Check));
            Section("energy upgrade checkpoints", () => CheckUpgrade(Check));
            Section("spending and capacity overflow", () => CheckSpend(Check));
            Section("energy operation fencing", () => CheckOperations(Check));
            Section("energy config fail-closed", () => CheckInvalidDefinitions(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_ENERGY_RUNTIME assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealPvf(Action<string, bool> check)
        {
            var config = GuardianAltarEnergyConfigFile.Parse(
                PvfArchiveAccessor.ReadText("etc/advancealtar/advancealtarconfig.etc"));
            check("real PVF exposes a valid positive level-zero capacity and natural fixed-unit scale",
                config.IsValid && config.Levels.Count > 0 && config.Levels[0].Level == 0
                && config.Scale > 0 && config.Levels[0].RegenerationFixedPerTick == config.Scale
                && config.Levels[0].CapacityFixed == (long)config.Levels[0].Capacity * config.Scale);
            var runtime = new GuardianAltarEnergyRuntime(config);
            var identity = Identity();
            check("real PVF runtime binds only the common released participant room",
                runtime.BindLoadingRelease(identity, 1));
            check("real PVF first resume begins at zero, without a fake initial energy grant",
                runtime.TrySetPaused(identity, 1, false, 0, out var start) && start.EnergyFixed == 0);
            var atOneSecond = runtime.ReadSnapshot(1000);
            check("real PVF level-zero one-second natural energy equals its configured regeneration",
                atOneSecond.Level == 0 && atOneSecond.Scale == config.Scale
                && atOneSecond.EnergyFixed == Math.Min(config.Levels[0].CapacityFixed,
                    config.Levels[0].RegenerationFixedPerTick));
            runtime.Close();
        }

        private static void CheckElapsed(Action<string, bool> check)
        {
            var config = Synthetic();
            var runtime = new GuardianAltarEnergyRuntime(config);
            var identity = Identity();
            var unbound = runtime.ReadSnapshot(0);
            check("unbound runtime is waiting and paused with zero energy and fixed level zero",
                !unbound.IsReady && unbound.Paused && unbound.EnergyFixed == 0
                && unbound.Level == 0 && unbound.Scale == 7 && !unbound.IsClosed);
            check("successful loading release binds readiness but does not start the active clock",
                runtime.BindLoadingRelease(identity, 3)
                && runtime.ReadSnapshot(5000) is { IsReady: true, Paused: true, EnergyFixed: 0 });
            var ready = runtime.ReadSnapshot(5000);
            check("same loading identity and generation is idempotent without changing pause version",
                runtime.BindLoadingRelease(identity, 3) && runtime.ReadSnapshot(5000).Version == ready.Version);
            check("initial paused request remains a no-op and does not accumulate loaded waiting time",
                runtime.TrySetPaused(identity, 3, true, 10000, out var waiting)
                && waiting.Version == ready.Version && waiting.EnergyFixed == 0 && waiting.Paused);
            check("first explicit resume starts its own monotonic anchor exactly once",
                runtime.TrySetPaused(identity, 3, false, 10000, out var resumed)
                && resumed.Version == ready.Version + 1 && !resumed.Paused && resumed.EnergyFixed == 0);
            check("0 through 999 active milliseconds produce no whole-second natural increment",
                runtime.ReadSnapshot(10000).EnergyFixed == 0 && runtime.ReadSnapshot(10999).EnergyFixed == 0);
            var oneSecond = runtime.ReadSnapshot(11000);
            check("1000 active milliseconds produce one configured fixed-unit increment",
                oneSecond.EnergyFixed == 7 && oneSecond.Version == resumed.Version);
            check("repeated resume does not restart the energy clock or advance pause version",
                runtime.TrySetPaused(identity, 3, false, 11500, out var repeated)
                && repeated.Version == resumed.Version && repeated.EnergyFixed == 7
                && runtime.ReadSnapshot(12000).EnergyFixed == 14);
            check("pause checkpoints both whole active seconds and the sub-second remainder",
                runtime.TrySetPaused(identity, 3, true, 12500, out var paused)
                && paused.EnergyFixed == 14 && paused.Paused && paused.Version == resumed.Version + 1);
            check("paused reads and duplicate pause cannot regenerate or reset their checkpoint",
                runtime.ReadSnapshot(30000).EnergyFixed == 14
                && runtime.TrySetPaused(identity, 3, true, 40000, out var duplicatePause)
                && duplicatePause.Version == paused.Version && duplicatePause.EnergyFixed == 14);
            check("resume preserves prior active fractional remainder instead of rounding it away",
                runtime.TrySetPaused(identity, 3, false, 40000, out var secondResume)
                && secondResume.Version == paused.Version + 1
                && runtime.ReadSnapshot(40499).EnergyFixed == 14 && runtime.ReadSnapshot(40500).EnergyFixed == 21);
            var stableVersion = runtime.ReadSnapshot(40500).Version;
            check("time reads do not change the source version used by pause ACK fencing",
                runtime.ReadSnapshot(41000).Version == stableVersion && runtime.ReadSnapshot(42000).Version == stableVersion
                && runtime.IsCurrent(secondResume) && !runtime.IsCurrent(paused));
            runtime.Close();
        }

        private static void CheckIdentity(Action<string, bool> check)
        {
            var runtime = new GuardianAltarEnergyRuntime(Synthetic());
            var identity = Identity();
            check("unbound pause or resume cannot create a ready runtime or energy anchor",
                !runtime.TrySetPaused(identity, 1, false, 0, out var unbound)
                && !unbound.IsReady && unbound.Paused && unbound.EnergyFixed == 0);
            check("invalid identities and nonpositive loading generations cannot bind",
                !runtime.BindLoadingRelease(default, 1)
                && !runtime.BindLoadingRelease(identity, 0) && !runtime.BindLoadingRelease(identity, -1));
            runtime.BindLoadingRelease(identity, 2);
            runtime.TrySetPaused(identity, 2, false, 0, out var start);
            var mismatches = new[]
            {
                Identity(instanceId: 2), Identity(runId: 2), Identity(runGeneration: 2), Identity(roomId: 2)
            };
            foreach (var mismatch in mismatches)
                check("an old or different run/room identity cannot rebind or mutate an existing runtime",
                    !runtime.BindLoadingRelease(mismatch, 2)
                    && !runtime.TrySetPaused(mismatch, 2, true, 100000, out _)
                    && runtime.ReadSnapshot(1000).EnergyFixed == 7 && runtime.IsCurrent(start));
            check("newer or older loading generation cannot steal the runtime clock",
                !runtime.BindLoadingRelease(identity, 1) && !runtime.BindLoadingRelease(identity, 3)
                && !runtime.TrySetPaused(identity, 1, true, 100000, out _)
                && !runtime.TrySetPaused(identity, 3, true, 100000, out _)
                && runtime.ReadSnapshot(1000).EnergyFixed == 7 && runtime.IsCurrent(start));
            var beforeClose = runtime.ReadSnapshot(1250);
            runtime.Close();
            var closed = runtime.ReadSnapshot(100000);
            check("close invalidates every prior ACK and freezes natural energy at the last accepted clock",
                closed.IsClosed && !closed.IsReady && closed.Paused && closed.EnergyFixed == beforeClose.EnergyFixed
                && closed.ActiveMilliseconds == beforeClose.ActiveMilliseconds
                && closed.Version == beforeClose.Version + 1 && !runtime.IsCurrent(beforeClose));
            runtime.Close();
            check("duplicate close is idempotent and no later request can reopen the owner",
                runtime.ReadSnapshot(long.MaxValue).Version == closed.Version
                && !runtime.BindLoadingRelease(identity, 2)
                && !runtime.TrySetPaused(identity, 2, false, long.MaxValue, out var rejected)
                && rejected.IsClosed && rejected.Version == closed.Version);
            var closedUnbound = new GuardianAltarEnergyRuntime(Synthetic());
            closedUnbound.Close();
            check("closing before binding forbids a late loading callback from starting the clock",
                !closedUnbound.BindLoadingRelease(identity, 1) && closedUnbound.ReadSnapshot(1000).EnergyFixed == 0);
        }

        private static void CheckClock(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = new GuardianAltarEnergyRuntime(Synthetic());
            runtime.BindLoadingRelease(identity, 1);
            check("negative clock cannot resume and negative reads fail explicitly",
                !runtime.TrySetPaused(identity, 1, false, -1, out var negative)
                && negative.Paused && negative.EnergyFixed == 0
                && Throws<ArgumentOutOfRangeException>(() => runtime.ReadSnapshot(-1)));
            runtime.TrySetPaused(identity, 1, false, 1000, out _);
            var progressed = runtime.ReadSnapshot(2500);
            check("backward reads never erase or repeat already observed natural energy",
                progressed.EnergyFixed == 7 && runtime.ReadSnapshot(1500).EnergyFixed == 7
                && runtime.ReadSnapshot(2500).EnergyFixed == 7 && runtime.ReadSnapshot(3000).EnergyFixed == 14);
            runtime.TrySetPaused(identity, 1, true, 2000, out var backPaused);
            runtime.TrySetPaused(identity, 1, false, 1500, out var backResume);
            check("backward pause/resume anchors at the accepted high-water mark instead of duplicating elapsed time",
                backPaused.EnergyFixed == 14 && backResume.EnergyFixed == 14
                && runtime.ReadSnapshot(3000).EnergyFixed == 14 && runtime.ReadSnapshot(3999).EnergyFixed == 14
                && runtime.ReadSnapshot(4000).EnergyFixed == 21);
            var maximum = runtime.ReadSnapshot(long.MaxValue);
            check("Int64 clock spans saturate at configured capacity without intermediate overflow",
                maximum.EnergyFixed == 35 && maximum.Level == 0
                && runtime.ReadSnapshot(long.MaxValue).EnergyFixed == 35
                && runtime.ReadSnapshot(0).EnergyFixed == 35);
            runtime.Close();

            var nearMax = new GuardianAltarEnergyRuntime(Synthetic());
            nearMax.BindLoadingRelease(identity, 1);
            nearMax.TrySetPaused(identity, 1, false, long.MaxValue - 1000, out _);
            check("a resume anchor near Int64 maximum still computes the final one-second delta exactly",
                nearMax.ReadSnapshot(long.MaxValue).EnergyFixed == 7);
            nearMax.Close();

            var largeScale = GuardianAltarEnergyConfigFile.Parse(
                "[gage up rate]\n0 1 2147483647\n[/gage up rate]\n"
                + "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n");
            var large = new GuardianAltarEnergyRuntime(largeScale);
            large.BindLoadingRelease(identity, 1);
            large.TrySetPaused(identity, 1, false, 0, out _);
            check("maximum positive fixed-unit scale multiplies only bounded whole elapsed seconds",
                large.ReadSnapshot(999).EnergyFixed == 0
                && large.ReadSnapshot(1000).EnergyFixed == int.MaxValue
                && large.ReadSnapshot(long.MaxValue).EnergyFixed == int.MaxValue);
            large.Close();
        }

        private static void CheckUpgrade(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = new GuardianAltarEnergyRuntime(UpgradeDefinition());
            runtime.BindLoadingRelease(identity, 1);
            runtime.TrySetPaused(identity, 1, false, 0, out var start);
            check("insufficient upgrade settles natural energy but does not change level or version",
                !runtime.TryUpgrade(identity, 1, 0, 7000, out var insufficient, out var insufficientResult)
                && insufficientResult == GuardianAltarEnergyOperationResult.InsufficientEnergy
                && insufficient.EnergyFixed == 7000 && insufficient.Level == 0
                && insufficient.Version == start.Version);
            check("level zero upgrade deducts exactly 8000 fixed units and invalidates the preceding ACK",
                runtime.TryUpgrade(identity, 1, 0, 10000, out var first, out var firstResult)
                && firstResult == GuardianAltarEnergyOperationResult.Success
                && first.Level == 1 && first.EnergyFixed == 2000 && first.ActiveMilliseconds == 10000
                && first.Version == start.Version + 1 && !runtime.IsCurrent(start));
            check("duplicate expected level cannot upgrade or charge twice",
                !runtime.TryUpgrade(identity, 1, 0, 10000, out var duplicate, out var duplicateResult)
                && duplicateResult == GuardianAltarEnergyOperationResult.StaleLevel
                && duplicate.Level == 1 && duplicate.EnergyFixed == 2000 && duplicate.Version == first.Version);
            check("new level regeneration prices only the next whole second at its new rate",
                runtime.ReadSnapshot(11000) is { Level: 1, EnergyFixed: 3100, ActiveMilliseconds: 11000 });
            check("level one upgrade deducts exactly 24000 fixed units without retroactive regeneration",
                runtime.TryUpgrade(identity, 1, 1, 30000, out var second, out var secondResult)
                && secondResult == GuardianAltarEnergyOperationResult.Success
                && second.Level == 2 && second.EnergyFixed == 0 && second.Version == first.Version + 1);
            check("level two upgrade deducts exactly 56000 fixed units",
                runtime.TryUpgrade(identity, 1, 2, 62000, out var third, out var thirdResult)
                && thirdResult == GuardianAltarEnergyOperationResult.Success
                && third.Level == 3 && third.EnergyFixed == 0 && third.Version == second.Version + 1);
            check("highest configured level regenerates normally and refuses another upgrade without charging",
                !runtime.TryUpgrade(identity, 1, 3, 63000, out var maximum, out var maximumResult)
                && maximumResult == GuardianAltarEnergyOperationResult.MaximumLevel
                && maximum.Level == 3 && maximum.EnergyFixed == 2500 && maximum.Version == third.Version);
            runtime.Close();

            var remainder = new GuardianAltarEnergyRuntime(Synthetic());
            remainder.BindLoadingRelease(identity, 1);
            remainder.TrySetPaused(identity, 1, false, 0, out _);
            check("upgrade truncates display cost before scale multiplication and retains its fractional tick",
                remainder.TryUpgrade(identity, 1, 0, 4500, out var fractional, out _)
                && fractional.EnergyFixed == 0 && fractional.Level == 1
                && remainder.ReadSnapshot(4999).EnergyFixed == 0
                && remainder.ReadSnapshot(5000).EnergyFixed == 11);
            remainder.Close();

            var twoLevels = new GuardianAltarEnergyRuntime(Synthetic());
            twoLevels.BindLoadingRelease(identity, 1);
            twoLevels.TrySetPaused(identity, 1, false, 0, out _);
            twoLevels.TryUpgrade(identity, 1, 0, 5000, out _, out _);
            check("maximum level comes from the configured row count rather than a fixed level-three limit",
                !twoLevels.TryUpgrade(identity, 1, 1, 5000, out _, out var twoLevelResult)
                && twoLevelResult == GuardianAltarEnergyOperationResult.MaximumLevel);
            twoLevels.Close();
        }

        private static void CheckSpend(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = new GuardianAltarEnergyRuntime(UpgradeDefinition());
            runtime.BindLoadingRelease(identity, 1);
            runtime.TrySetPaused(identity, 1, false, 0, out _);
            var full = runtime.ReadSnapshot(100000);
            check("wave active time keeps advancing after energy has reached capacity",
                full.EnergyFixed == 10000 && full.ActiveMilliseconds == 100000
                && runtime.ReadSnapshot(100500).ActiveMilliseconds == 100500);
            check("spending after a long full-capacity wait cannot consume stored overflow or regain it immediately",
                runtime.TrySpend(identity, 1, 0, 5000, 100500, out var spent, out var result)
                && result == GuardianAltarEnergyOperationResult.Success && spent.EnergyFixed == 5000
                && spent.Version == full.Version + 1 && !runtime.IsCurrent(full)
                && runtime.ReadSnapshot(100500).EnergyFixed == 5000
                && runtime.ReadSnapshot(101499).EnergyFixed == 5000
                && runtime.ReadSnapshot(101500).EnergyFixed == 6000);
            check("insufficient spending cannot change the balance or operation version",
                !runtime.TrySpend(identity, 1, 0, 6001, 101500, out var insufficient, out var insufficientResult)
                && insufficientResult == GuardianAltarEnergyOperationResult.InsufficientEnergy
                && insufficient.EnergyFixed == 6000 && insufficient.Version == spent.Version);
            check("a successful zero-cost command retains the balance and fences its own receipt",
                runtime.TrySpend(identity, 1, 0, 0, 101500, out var free, out _)
                && free.EnergyFixed == 6000 && free.Version == spent.Version + 1);
            runtime.TrySetPaused(identity, 1, true, 101750, out var paused);
            check("paused wave time and energy freeze together while retaining the partial regeneration tick",
                runtime.ReadSnapshot(200000).ActiveMilliseconds == paused.ActiveMilliseconds
                && runtime.ReadSnapshot(200000).EnergyFixed == paused.EnergyFixed
                && runtime.TrySetPaused(identity, 1, false, 200000, out _)
                && runtime.ReadSnapshot(200749).EnergyFixed == 6000
                && runtime.ReadSnapshot(200750).EnergyFixed == 7000
                && runtime.ReadSnapshot(200750).ActiveMilliseconds == 102500);
            runtime.Close();

            var concurrent = new GuardianAltarEnergyRuntime(UpgradeDefinition());
            concurrent.BindLoadingRelease(identity, 1);
            concurrent.TrySetPaused(identity, 1, false, 0, out _);
            var before = concurrent.ReadSnapshot(10000);
            var acceptedFirst = false;
            var acceptedSecond = false;
            System.Threading.Tasks.Parallel.Invoke(
                () => acceptedFirst = concurrent.TrySpend(identity, 1, 0, 7000, 10000, out _, out _),
                () => acceptedSecond = concurrent.TrySpend(identity, 1, 0, 7000, 10000, out _, out _));
            var after = concurrent.ReadSnapshot(10000);
            check("concurrent spending validates and deducts atomically against one balance",
                acceptedFirst != acceptedSecond && after.EnergyFixed == 3000
                && after.Version == before.Version + 1);
            concurrent.Close();
        }

        private static void CheckOperations(Action<string, bool> check)
        {
            var identity = Identity();
            var runtime = new GuardianAltarEnergyRuntime(UpgradeDefinition());
            check("unbound spend and upgrade cannot start time or mutate readiness",
                !runtime.TrySpend(identity, 1, 0, 0, 100000, out var unbound, out var unboundResult)
                && unboundResult == GuardianAltarEnergyOperationResult.NotReady
                && !unbound.IsReady && unbound.ActiveMilliseconds == 0
                && !runtime.TryUpgrade(identity, 1, 0, 100000, out _, out var unboundUpgradeResult)
                && unboundUpgradeResult == GuardianAltarEnergyOperationResult.NotReady);
            runtime.BindLoadingRelease(identity, 1);
            check("loading-bound but paused commands cannot spend or upgrade",
                !runtime.TrySpend(identity, 1, 0, 0, 100000, out _, out var pausedSpend)
                && pausedSpend == GuardianAltarEnergyOperationResult.Paused
                && !runtime.TryUpgrade(identity, 1, 0, 100000, out _, out var pausedUpgrade)
                && pausedUpgrade == GuardianAltarEnergyOperationResult.Paused);
            runtime.TrySetPaused(identity, 1, false, 100000, out var start);
            foreach (var mismatch in new[]
            {
                Identity(instanceId: 2), Identity(runId: 2), Identity(runGeneration: 2), Identity(roomId: 2)
            })
                check("different run or room operation cannot move the clock, deduct energy or change level",
                    !runtime.TrySpend(mismatch, 1, 0, 0, 1000000, out _, out var wrongSpend)
                    && wrongSpend == GuardianAltarEnergyOperationResult.NotReady
                    && !runtime.TryUpgrade(mismatch, 1, 0, 1000000, out _, out var wrongUpgrade)
                    && wrongUpgrade == GuardianAltarEnergyOperationResult.NotReady
                    && runtime.ReadSnapshot(100000).ActiveMilliseconds == 0 && runtime.IsCurrent(start));
            check("old loading generation and negative time fail before advancing the clock",
                !runtime.TrySpend(identity, 2, 0, 0, 1000000, out _, out var generationResult)
                && generationResult == GuardianAltarEnergyOperationResult.NotReady
                && !runtime.TryUpgrade(identity, 2, 0, 1000000, out _, out _)
                && !runtime.TrySpend(identity, 1, 0, 0, -1, out _, out _)
                && !runtime.TryUpgrade(identity, 1, 0, -1, out _, out _)
                && runtime.ReadSnapshot(100000).ActiveMilliseconds == 0);
            check("stale spending level refuses a debit while preserving naturally settled time",
                !runtime.TrySpend(identity, 1, 1, 0, 101000, out var stale, out var staleResult)
                && staleResult == GuardianAltarEnergyOperationResult.StaleLevel
                && stale.EnergyFixed == 1000 && stale.ActiveMilliseconds == 1000
                && stale.Version == start.Version);
            check("negative spending cannot create energy",
                Throws<ArgumentOutOfRangeException>(() => runtime.TrySpend(identity, 1, 0, -1, 101000, out _, out _)));
            runtime.Close();
            check("closed operations cannot restart the run or advance the clock",
                !runtime.TrySpend(identity, 1, 0, 0, long.MaxValue, out var closed, out var closedResult)
                && closedResult == GuardianAltarEnergyOperationResult.NotReady && closed.IsClosed
                && closed.ActiveMilliseconds == 1000 && closed.EnergyFixed == 1000
                && !runtime.TryUpgrade(identity, 1, 0, long.MaxValue, out _, out _));
        }

        private static void CheckInvalidDefinitions(Action<string, bool> check)
        {
            check("missing energy definition is rejected without fabricated rates",
                Throws<ArgumentNullException>(() => new GuardianAltarEnergyRuntime(null)));
            foreach (var content in new[]
            {
                "", "[gage up rate]\n[/gage up rate]\n",
                "[gage up rate]\n1 5 7\n[/gage up rate]\n",
                "[gage up rate]\n0 0 7\n[/gage up rate]\n",
                "[gage up rate]\n0 5 0\n[/gage up rate]\n",
                "[gage up rate]\n0 2147483647 2\n[/gage up rate]\n"
            })
            {
                var invalid = GuardianAltarEnergyConfigFile.Parse(content
                    + "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n");
                check("malformed or overflowing parsed energy rows cannot initialize runtime authority",
                    !invalid.IsValid && Throws<InvalidDataException>(() => new GuardianAltarEnergyRuntime(invalid)));
            }
            var missingCost = GuardianAltarEnergyConfigFile.Parse("[gage up rate]\n0 5 7\n[/gage up rate]\n");
            check("an absent upgrade fraction cannot create a fabricated runtime cost",
                !missingCost.IsValid && Throws<InvalidDataException>(() => new GuardianAltarEnergyRuntime(missingCost)));
            var parsed = Synthetic();
            check("synthetic capacity and regeneration levels come only from their declared resource rows",
                parsed.IsValid && parsed.Levels.Count == 2 && parsed.Scale == 7
                && parsed.Levels[0].CapacityFixed == 35 && parsed.Levels[1].CapacityFixed == 70);
        }

        private static GuardianAltarEnergyConfigFile Synthetic() => GuardianAltarEnergyConfigFile.Parse(
            "[gage up rate]\n0 5 7\n1 10 11\n[/gage up rate]\n"
            + "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n");

        private static GuardianAltarEnergyConfigFile UpgradeDefinition() => GuardianAltarEnergyConfigFile.Parse(
            "[gage up rate]\n0 10 1000\n1 30 1100\n2 70 1750\n3 150 2500\n[/gage up rate]\n"
            + "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n");

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
