using System;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarClearPresentationSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_CLEAR_PRESENTATION selftest ===");
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

            Section("terminal binding and commit", () => CheckBinding(Check));
            Section("successful projection order and deadlines", () => CheckSuccess(Check));
            Section("failed direct EPLP", () => CheckFailure(Check));
            Section("survival failure presents committed completed rounds", () => CheckSurvivalFailure(Check));
            Section("projection retry and stale events", () => CheckRetry(Check));
            Section("explicit UTC and deadline overflow", () => CheckClock(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_CLEAR_PRESENTATION assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckBinding(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact();
            var now = Utc();
            check("unbound presentation cannot commit rewards or advance a projection checkpoint",
                runtime.Fact == null && runtime.CommitResult == null
                && !runtime.TryCommit(Result()) && !runtime.TryMarkInventoryProjectionSent(fact.EventId)
                && !runtime.TryMarkMenuProjectionSent(fact.EventId)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now));
            var invalid = new[]
            {
                fact with { EventId = Guid.Empty }, fact with { Identity = default },
                fact with { LoadingGeneration = 0 }, fact with { ActiveMilliseconds = -1 },
                fact with { Result = GuardianAltarTerminalResult.Aborted },
                fact with { Result = (GuardianAltarTerminalResult)99 }, fact with { Reason = " " },
            };
            check("missing terminal facts cannot initialize presentation authority", !runtime.TryBind(null));
            foreach (var candidate in invalid)
                check("invalid or aborted terminal fields are rejected without binding",
                    !runtime.TryBind(candidate) && runtime.Fact == null);
            check("first terminal binding keeps the original immutable fact and identical retry is accepted",
                runtime.TryBind(fact) && runtime.TryBind(fact with { }) && ReferenceEquals(fact, runtime.Fact));
            var conflicting = new[]
            {
                fact with { EventId = Guid.NewGuid() }, fact with { Identity = Identity(runId: 2) },
                fact with { Identity = Identity(roomId: 2) }, fact with { LoadingGeneration = 2 },
                fact with { ActiveMilliseconds = fact.ActiveMilliseconds + 1 },
                fact with { Result = GuardianAltarTerminalResult.Failed }, fact with { Reason = "forged-result" },
            };
            foreach (var candidate in conflicting)
                check("another event, scope or changed terminal fact cannot replace the first binding",
                    !runtime.TryBind(candidate) && ReferenceEquals(fact, runtime.Fact));
            check("success without a committed result cannot authorize any UI projection",
                !runtime.TryCommit(null) && !runtime.TryMarkInventoryProjectionSent(fact.EventId)
                && !runtime.TryMarkMenuProjectionSent(fact.EventId)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now)
                && !runtime.InventoryProjectionSent && !runtime.MenuProjectionSent
                && !runtime.ClearProjectionSent && !runtime.EplpProjectionSent);
            check("structurally incomplete commit result cannot enable a fake successful presentation",
                !runtime.TryCommit(new GuardianAltarClearCommitResult())
                && !runtime.TryCommit(new GuardianAltarClearCommitResult { ProgressSnapshot = Progress(), StarsGained = -1 })
                && !runtime.TryCommit(new GuardianAltarClearCommitResult { ProgressSnapshot = Progress(), MailedRewardCount = -1 })
                && !runtime.TryCommit(new GuardianAltarClearCommitResult { ProgressSnapshot = Progress(), Changes = null })
                && !runtime.TryCommit(new GuardianAltarClearCommitResult { ProgressSnapshot = Progress(), NewPurchases = null }));
            var committed = Result();
            check("first commit saves its existing result once and retry cannot overwrite it",
                runtime.TryCommit(committed) && runtime.TryCommit(committed)
                && !runtime.TryCommit(Result()) && ReferenceEquals(committed, runtime.CommitResult));
        }

        private static void CheckSuccess(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact();
            var now = Utc();
            runtime.TryBind(fact);
            runtime.TryCommit(Result());
            check("successful menu, clear and EPLP projections wait for the earlier checkpoint",
                !runtime.TryMarkMenuProjectionSent(fact.EventId)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now)
                && runtime.EplpDeadlineUtc == default && runtime.ReturnDeadlineUtc == default);
            check("inventory write completion enables only the next menu checkpoint",
                runtime.TryMarkInventoryProjectionSent(fact.EventId) && runtime.InventoryProjectionSent
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now));
            check("menu write completion enables clear but cannot skip clear into EPLP",
                runtime.TryMarkMenuProjectionSent(fact.EventId) && runtime.MenuProjectionSent
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now));
            check("first clear projection freezes the four-second EPLP deadline",
                runtime.TryMarkClearProjectionSent(fact.EventId, now) && runtime.ClearProjectionSent
                && runtime.EplpDeadlineUtc == now.AddSeconds(4) && runtime.EplpDeadlineUtc.Kind == DateTimeKind.Utc
                && runtime.ReturnDeadlineUtc == default);
            check("duplicate clear completion preserves its original absolute deadline",
                runtime.TryMarkClearProjectionSent(fact.EventId, now.AddSeconds(30))
                && runtime.EplpDeadlineUtc == now.AddSeconds(4));
            check("EPLP cannot be recorded before the frozen clear deadline",
                !runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddMilliseconds(3999))
                && !runtime.EplpProjectionSent && runtime.ReturnDeadlineUtc == default);
            check("first EPLP completion freezes its fifty-second return deadline",
                runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddSeconds(4)) && runtime.EplpProjectionSent
                && runtime.ReturnDeadlineUtc == now.AddSeconds(54) && runtime.ReturnDeadlineUtc.Kind == DateTimeKind.Utc);
            check("duplicate EPLP completion preserves return time and prior checkpoints remain monotonic",
                runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddMinutes(3))
                && runtime.TryMarkInventoryProjectionSent(fact.EventId)
                && runtime.TryMarkMenuProjectionSent(fact.EventId)
                && runtime.TryMarkClearProjectionSent(fact.EventId, now.AddMinutes(3))
                && runtime.ReturnDeadlineUtc == now.AddSeconds(54)
                && runtime.EplpDeadlineUtc == now.AddSeconds(4)
                && runtime.InventoryProjectionSent && runtime.MenuProjectionSent && runtime.ClearProjectionSent);
        }

        private static void CheckFailure(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact(GuardianAltarTerminalResult.Failed);
            var now = Utc();
            runtime.TryBind(fact);
            check("failure needs no reward commit and cannot acquire a successful result",
                runtime.TryCommit(null) && !runtime.TryCommit(Result()) && runtime.CommitResult == null);
            check("failure cannot record inventory, menu or clear projections",
                !runtime.TryMarkInventoryProjectionSent(fact.EventId)
                && !runtime.TryMarkMenuProjectionSent(fact.EventId)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && !runtime.InventoryProjectionSent && !runtime.MenuProjectionSent && !runtime.ClearProjectionSent);
            check("failed battle projects EPLP directly without a fabricated clear deadline",
                runtime.TryMarkEplpProjectionSent(fact.EventId, now) && runtime.EplpProjectionSent
                && runtime.EplpDeadlineUtc == default && runtime.ReturnDeadlineUtc == now.AddSeconds(50));
            check("failed EPLP retry preserves the same fact and return deadline",
                runtime.TryBind(fact) && runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddMinutes(1))
                && ReferenceEquals(fact, runtime.Fact) && runtime.ReturnDeadlineUtc == now.AddSeconds(50));
        }

        private static void CheckSurvivalFailure(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact() with { Result = GuardianAltarTerminalResult.Failed, ClearedRounds = 2 };
            var now = Utc();
            var progress = Progress();
            var result = new GuardianAltarClearCommitResult
            {
                ClearedRounds = 2, BestRound = 2, Improved = true,
                ProgressSnapshot = new GuardianAltarProgressSnapshot(progress.CharacterId,
                    progress.Header with { SurvivalBestPhase = 2 }, progress.Stages,
                    progress.Purchases, progress.Slots, progress.Achievements),
            };
            check("survival failure binds earned rounds but cannot send EPLP before a committed result",
                runtime.TryBind(fact, survivalResult: true) && !runtime.TryCommit(null)
                && !runtime.TryBind(fact, survivalResult: false)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, now));
            check("survival result preserves the frozen score and rejects an ordinary successful star result",
                !runtime.TryCommit(Result()) && runtime.TryCommit(result)
                && runtime.TryMarkInventoryProjectionSent(fact.EventId)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, now)
                && runtime.TryMarkMenuProjectionSent(fact.EventId)
                && runtime.TryMarkClearProjectionSent(fact.EventId, now));
            check("survival result has the existing clear-to-EPLP deadline and repeats do not restart it",
                !runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddMilliseconds(3999))
                && runtime.TryMarkClearProjectionSent(fact.EventId, now.AddSeconds(1))
                && runtime.EplpDeadlineUtc == now.AddSeconds(4)
                && runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddSeconds(4))
                && runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddSeconds(5))
                && runtime.ReturnDeadlineUtc == now.AddSeconds(54));
        }

        private static void CheckRetry(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact();
            var committed = Result();
            var now = Utc();
            runtime.TryBind(fact);
            runtime.TryCommit(committed);
            runtime.TryMarkInventoryProjectionSent(fact.EventId);
            check("an unrecorded failed menu write leaves the saved commit and inventory checkpoint for retry",
                ReferenceEquals(runtime.CommitResult, committed) && runtime.InventoryProjectionSent
                && !runtime.MenuProjectionSent && !runtime.ClearProjectionSent && !runtime.EplpProjectionSent);
            var stale = Guid.NewGuid();
            check("stale event cannot advance any checkpoint or establish a deadline",
                !runtime.TryMarkInventoryProjectionSent(stale) && !runtime.TryMarkMenuProjectionSent(stale)
                && !runtime.TryMarkClearProjectionSent(stale, now)
                && !runtime.TryMarkEplpProjectionSent(stale, now)
                && runtime.InventoryProjectionSent && !runtime.MenuProjectionSent
                && runtime.EplpDeadlineUtc == default && runtime.ReturnDeadlineUtc == default);
            runtime.TryMarkMenuProjectionSent(fact.EventId);
            check("retry resumes after the earlier completed inventory checkpoint without losing commit authority",
                runtime.InventoryProjectionSent && runtime.MenuProjectionSent && !runtime.ClearProjectionSent
                && runtime.TryMarkClearProjectionSent(fact.EventId, now));
            var deadline = runtime.EplpDeadlineUtc;
            check("EPLP write retry keeps its original due time and does not resend or reset successful clear",
                runtime.ClearProjectionSent && !runtime.EplpProjectionSent
                && runtime.TryMarkClearProjectionSent(fact.EventId, now.AddSeconds(2))
                && runtime.EplpDeadlineUtc == deadline
                && runtime.TryMarkEplpProjectionSent(fact.EventId, deadline));
            var returnAt = runtime.ReturnDeadlineUtc;
            check("empty event and conflicting terminal cannot rewind completed presentation",
                !runtime.TryMarkClearProjectionSent(Guid.Empty, now)
                && !runtime.TryMarkEplpProjectionSent(Guid.Empty, now)
                && !runtime.TryBind(fact with { Result = GuardianAltarTerminalResult.Failed })
                && runtime.EplpProjectionSent && runtime.ReturnDeadlineUtc == returnAt
                && runtime.EplpDeadlineUtc == deadline && ReferenceEquals(runtime.CommitResult, committed));
        }

        private static void CheckClock(Action<string, bool> check)
        {
            var runtime = new GuardianAltarClearPresentationRuntime();
            var fact = Fact();
            var now = Utc();
            runtime.TryBind(fact);
            runtime.TryCommit(Result());
            runtime.TryMarkInventoryProjectionSent(fact.EventId);
            runtime.TryMarkMenuProjectionSent(fact.EventId);
            var unspecified = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);
            var local = DateTime.SpecifyKind(now, DateTimeKind.Local);
            check("implicit or local dates cannot create UTC presentation deadlines",
                !runtime.TryMarkClearProjectionSent(fact.EventId, unspecified)
                && !runtime.TryMarkClearProjectionSent(fact.EventId, local)
                && !runtime.ClearProjectionSent && runtime.EplpDeadlineUtc == default);
            var maximum = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            check("clear deadline overflow leaves every pending checkpoint unchanged",
                !runtime.TryMarkClearProjectionSent(fact.EventId, maximum)
                && !runtime.ClearProjectionSent && runtime.EplpDeadlineUtc == default);
            runtime.TryMarkClearProjectionSent(fact.EventId, now);
            check("EPLP date validation and overflow cannot commit a partial return deadline",
                !runtime.TryMarkEplpProjectionSent(fact.EventId, unspecified)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, local)
                && !runtime.TryMarkEplpProjectionSent(fact.EventId, maximum)
                && !runtime.EplpProjectionSent && runtime.ReturnDeadlineUtc == default
                && runtime.EplpDeadlineUtc == now.AddSeconds(4));
            check("retry after an invalid date can use the original valid absolute deadline",
                runtime.TryMarkEplpProjectionSent(fact.EventId, now.AddSeconds(4))
                && runtime.ReturnDeadlineUtc == now.AddSeconds(54));
        }

        private static GuardianAltarTerminalFact Fact(GuardianAltarTerminalResult result = GuardianAltarTerminalResult.Succeeded)
            => new GuardianAltarTerminalFact(Guid.NewGuid(), Identity(), 1, 145000, result,
                result == GuardianAltarTerminalResult.Succeeded ? "all-phases-and-enemies-complete" : "all-failure-centers-defeated");

        private static GuardianAltarClearCommitResult Result()
            => new GuardianAltarClearCommitResult { StarsGained = 1, FirstClear = true, ProgressSnapshot = Progress() };

        private static GuardianAltarProgressSnapshot Progress()
            => new GuardianAltarProgressSnapshot(1, new GuardianAltarHeader(1, 3, 0, 1, 0, 1, 0, 0, 0, 0),
                new[] { new GuardianAltarStage(3511, 0) }, Array.Empty<GuardianAltarPurchase>(),
                Enumerable.Range(0, 12).Select(x => new GuardianAltarSlot(1, x / 6 + 1, (byte)(x % 6), 0)),
                Array.Empty<GuardianAltarAchievement>());

        private static DungeonParticipantRoomIdentity Identity(long runId = 1, long roomId = 1)
            => new DungeonParticipantRoomIdentity(new DungeonRunIdentity(1, runId, 1),
                new DungeonRoomIdentity(new DungeonInstanceIdentity(1), roomId));

        private static DateTime Utc() => new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
