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
                "[gage up rate]\n0 1 2147483647\n[/gage up rate]\n");
            var large = new GuardianAltarEnergyRuntime(largeScale);
            large.BindLoadingRelease(identity, 1);
            large.TrySetPaused(identity, 1, false, 0, out _);
            check("maximum positive fixed-unit scale multiplies only bounded whole elapsed seconds",
                large.ReadSnapshot(999).EnergyFixed == 0
                && large.ReadSnapshot(1000).EnergyFixed == int.MaxValue
                && large.ReadSnapshot(long.MaxValue).EnergyFixed == int.MaxValue);
            large.Close();
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
                var invalid = GuardianAltarEnergyConfigFile.Parse(content);
                check("malformed or overflowing parsed energy rows cannot initialize runtime authority",
                    !invalid.IsValid && Throws<InvalidDataException>(() => new GuardianAltarEnergyRuntime(invalid)));
            }
            var parsed = Synthetic();
            check("synthetic rows explicitly exercise level zero, not unlocked upgrades or current real PVF values",
                parsed.IsValid && parsed.Levels.Count == 2 && parsed.Scale == 7
                && parsed.Levels[0].CapacityFixed == 35 && parsed.Levels[1].CapacityFixed == 70);
        }

        private static GuardianAltarEnergyConfigFile Synthetic() => GuardianAltarEnergyConfigFile.Parse(
            "[gage up rate]\n0 5 7\n1 10 11\n[/gage up rate]\n");

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
