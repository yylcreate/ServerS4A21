using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Bio;

namespace DfoServer.SelfTests
{
    public static class BioDomainSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== BIO_DOMAIN selftest ===");
            var failures = 0;
            VerifyQueue(ref failures);
            VerifyRuntime(ref failures);
            VerifyCurrentPvfWhenConfigured(ref failures);
            Console.WriteLine(failures == 0
                ? "BIO_DOMAIN selftest passed."
                : $"BIO_DOMAIN selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyQueue(ref int failures)
        {
            var definition = BioDefinition.CreateForSelfTest();
            var coordinator = new BioMatchingCoordinator(definition);
            var now = DateTime.UtcNow;
            var tickets = Enumerable.Range(1, 5).Select(index => coordinator.Join(
                new BioQueueIdentity(Guid.NewGuid(), index, index, checked((ushort)(100 + index)), 19),
                now.AddMilliseconds(index))).ToArray();
            Check("five tickets are visible", coordinator.Snapshot(19).Count == 5, ref failures);
            Check("duplicate join is idempotent", ReferenceEquals(
                coordinator.Join(tickets[0].Identity, now), tickets[0]), ref failures);
            Check("freeze creates one cohort", coordinator.TryFreeze(19, out var cohort)
                && cohort.Generation > 0 && cohort.Participants.Count == 5
                && coordinator.Snapshot(19).Count == 0, ref failures);
            Check("old ticket cannot be cancelled after freeze", !coordinator.Cancel(tickets[0]), ref failures);

            var differentListener = coordinator.Join(
                new BioQueueIdentity(Guid.NewGuid(), 99, 99, 199, 20), now);
            Check("different listener remains isolated", coordinator.Snapshot(19).Count == 0
                && coordinator.Snapshot(20).Count == 1, ref failures);
            coordinator.Cancel(differentListener);
        }

        private static void VerifyRuntime(ref int failures)
        {
            var definition = BioDefinition.CreateForSelfTest(5, 5, 0, 10000);
            var instance = new DungeonInstance(3101, 0);
            var members = new List<BioMember>();
            for (var i = 0; i < 5; i++)
            {
                var run = new DungeonRun(instance, 7000 + i, 1, DungeonRunState.Active);
                members.Add(new BioMember(
                    checked((byte)i),
                    2000 + i,
                    checked((ushort)(300 + i)),
                    Guid.NewGuid(),
                    run,
                    run.CaptureIdentity()));
            }
            var runtime = new BioDungeonRuntime(instance, definition, 7, members);
            var now = DateTime.UtcNow;
            foreach (var member in members)
            {
                var caller = new BioCaller(member.SessionId, member.CharacterId, member.UserId, member.Run);
                Check("entry is accepted once", runtime.TryClaimEntry(caller) == BioDecision.Applied, ref failures);
                Check("entry replay is harmless", runtime.TryClaimEntry(caller) == BioDecision.Replayed, ref failures);
                Check("load is accepted once", runtime.MarkLoaded(caller, 7, now) == BioDecision.Applied, ref failures);
            }
            Check("all participants reach ready", runtime.Phase == BioPhase.Ready, ref failures);
            var host = members[0];
            var hostCaller = new BioCaller(host.SessionId, host.CharacterId, host.UserId, host.Run);
            Check("stale generation cannot select", runtime.TryCommitSelection(hostCaller, 8, new byte[] { 1 }, now) == BioDecision.Rejected, ref failures);
            Check("selection starts play", runtime.TryCommitSelection(hostCaller, 7, new byte[] { 1 }, now) == BioDecision.Applied, ref failures);
            Check("infection is idempotent", runtime.TryRecordInfection(hostCaller, 7, 0, now.AddSeconds(1)) == BioDecision.Applied
                && runtime.TryRecordInfection(hostCaller, 7, 0, now.AddSeconds(1)) == BioDecision.Replayed, ref failures);
            Check("caller cannot infect another seat", runtime.TryRecordInfection(hostCaller, 7, 1, now.AddSeconds(1)) == BioDecision.Rejected, ref failures);

            var rows = members.Select(member => new BioScoreInput(
                member.Seat, 0, 0, false, 5000, 0, (uint)(100 + member.Seat))).ToArray();
            Check("host can commit a validated result", runtime.TryCommitResult(hostCaller, 0, rows, now.AddSeconds(5)) == BioDecision.Applied, ref failures);
            Check("result commit is replayed", runtime.TryCommitResult(hostCaller, 0, rows, now.AddSeconds(5)) == BioDecision.Replayed, ref failures);
            Check("result is not shown before settlement", runtime.Phase == BioPhase.ResultPending, ref failures);
            Check("result can be marked shown", runtime.TryMarkResults(runtime.ResultPlan), ref failures);
            runtime.End();
            Check("ended runtime rejects stale calls", runtime.TryClaimEntry(hostCaller) == BioDecision.Rejected, ref failures);
        }

        private static void VerifyCurrentPvfWhenConfigured(ref int failures)
        {
            var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
            {
                Console.WriteLine("BIO_DOMAIN PVF parse skipped: PVF_ARCHIVE_PATH is not configured.");
                return;
            }
            Check("current PVF exposes Bio definition", BioDefinitionCatalog.TryLoadCurrent(out var definition, out var error)
                && definition.MinimumPlayers >= 2 && definition.MaximumPlayers <= 8
                && definition.PlayModes.Count > 0, ref failures);
            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"BIO_DOMAIN PVF parse error: {error}");
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (!condition)
            {
                failures++;
                Console.WriteLine("FAIL: " + name);
            }
        }
    }
}
