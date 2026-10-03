using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarMenuReturnSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_MENU_RETURN selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string name, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {name}");
                if (!success) failures++;
            }
            void Section(string name, Func<Task> action)
            {
                try { action().GetAwaiter().GetResult(); }
                catch (Exception exception) { Check(name + " exception: " + exception, false); }
            }
            try
            {
                // This is the actual PVF menu catalog, not a fabricated empty menu.
                var definition = GuardianAltarMenuDefinition.Load();
                Section("menu receipt authority", () => VerifyAdmissionAsync(definition, Check));
                Section("menu return and subsequent operation gates", () => VerifyReturnAsync(definition, Check));
                Section("retry after uncertain projection", () => VerifyRetriesAsync(definition, Check));
                Section("menu receipt source epochs", () => VerifySourcesAsync(definition, Check));
                Section("queued and final-send source fences", () => VerifyQueuedAsync(definition, Check));
                Section("new menu replaces the previous receipt", () => VerifyRenewalAsync(definition, Check));
            }
            catch (Exception exception) { Check("current PVF menu definition: " + exception, false); }
            Console.WriteLine($"GUARDIAN_ALTAR_MENU_RETURN assertions={assertions} failures={failures}");
            failures += GuardianAltarMenuTownProjectionSelfTest.Run();
            return failures;
        }

        private static async Task VerifyAdmissionAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using (var fixture = new Fixture(definition))
            {
                check("a town session without a successful Guardian menu does not own BACK_2_VILLAGE",
                    !await fixture.ReturnAsync() && fixture.Packets.Count == 0 && fixture.ProjectionCalls == 0);
                fixture.Open();
                check("an open schedule alone does not create a menu-return receipt",
                    !await fixture.ReturnAsync() && fixture.Packets.Count == 0);
                fixture.Session.Player.Level = 49;
                await fixture.MenuAsync();
                var count = fixture.Packets.Count;
                check("a rejected START_GAME reply cannot authorize menu return",
                    !await fixture.ReturnAsync() && fixture.Packets.Count == count && fixture.ProjectionCalls == 0);
            }
            foreach (var partial in new[] { false, true })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.FailNextWrite = !partial;
                fixture.FailAfterWrite = partial;
                await fixture.MenuAsync();
                var count = fixture.Packets.Count;
                check((partial ? "an uncertain full-frame write" : "a failed menu write")
                    + " cannot publish a successful menu-return receipt",
                    !await fixture.ReturnAsync() && fixture.Packets.Count == count && fixture.ProjectionCalls == 0);
                await fixture.MenuAsync();
                check("a later complete successful menu write authorizes return after transport recovery",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                fixture.AfterWrite = () => fixture.Session.Account = new AccountRecord { AccountId = 11 };
                await fixture.MenuAsync();
                check("source displacement after the menu frame cannot publish a return receipt",
                    IsMenu(fixture.Packets.Single()) && !await fixture.ReturnAsync() && fixture.ProjectionCalls == 0);
                await fixture.MenuAsync();
                fixture.FailNextWrite = true;
                await fixture.MenuAsync();
                check("a failed replacement menu preserves the preceding completely sent menu receipt",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.MenuAsync();
                foreach (var request in new[] {
                    (Command: (byte)0, Type: (ushort)CmdPacketTypeA21.BACK_2_VILLAGE, Body: Array.Empty<byte>()),
                    (Command: (byte)1, Type: (ushort)CmdPacketTypeA21.GIVEUP_GAME, Body: Array.Empty<byte>()),
                    (Command: (byte)1, Type: (ushort)CmdPacketTypeA21.BACK_2_VILLAGE, Body: new byte[] { 0 }) })
                {
                    var count = fixture.Packets.Count;
                    check("only the exact empty CMD BACK_2_VILLAGE route may consume a menu receipt",
                        !await fixture.ReturnAsync(request.Body, request.Command, request.Type)
                        && fixture.Packets.Count == count && fixture.ProjectionCalls == 0);
                }
                check("rejected headers or tails do not destroy the legitimate receipt",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1);
            }
        }

        private static async Task VerifyReturnAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            await fixture.MenuAsync();
            check("START_GAME uses a complete real menu without creating an ordinary run or selection",
                fixture.Progress.Reads == 1 && IsMenu(fixture.Packets.Single())
                && fixture.Session.Player.CurrentRun == null && fixture.Session.Player.CurrentDungeonSelection == null);
            // Position reports may arrive while the menu is open. Only movement
            // after a completed return expires its replay scope.
            fixture.Session.Player.NextTownMovementSequence();
            var count = fixture.Packets.Count;
            var consumed = await fixture.ReturnAsync().WaitAsync(Timeout);
            check("a first menu return accepts prior movement and sends the one-byte success before town projection",
                consumed && IsReturnAck(fixture.Packets[count]) && fixture.AckBeforeProjection
                && fixture.ProjectionCalls == 1 && fixture.ProjectionOutsideSendLock
                && fixture.Session.Player.CurrentRun == null && fixture.Session.Player.UserState == 0
                && fixture.Session.Player.TownPresenceReady);
            check("the injected town projection uses the existing USER_STATE and town-area envelopes",
                fixture.Packets.Skip(count + 1).Select(Type).SequenceEqual(new[] {
                    (ushort)NotiPacketTypeA21.USER_STATE, (ushort)NotiPacketTypeA21.USER_AREA,
                    (ushort)NotiPacketTypeA21.AREA_USERS }));
            var fence = fixture.LastProjectionFence;
            var loading = await TownHandler.SendFinishLoadingCompletionCoreAsync(fence,
                fixture.SendProjectionAsync).WaitAsync(Timeout);
            check("the shared guarded FINISH_LOADING core can send after menu return",
                loading && Type(fixture.Packets[^1]) == (ushort)NotiPacketTypeA21.FINISH_LOADING
                && fixture.Packets[^1].AsSpan(15).SequenceEqual(FinishLoadingBuilder.BuildNotification()));
            count = fixture.Packets.Count;
            check("same-scope duplicate return re-ACKs without another town projection",
                await fixture.ReturnAsync().WaitAsync(Timeout)
                && fixture.Packets.Count == count + 1 && IsReturnAck(fixture.Packets[^1])
                && fixture.ProjectionCalls == 1);
            fixture.Session.Player.NextTownMovementSequence();
            count = fixture.Packets.Count;
            check("movement after a completed return expires its duplicate scope",
                !await fixture.ReturnAsync() && fixture.Packets.Count == count && fixture.ProjectionCalls == 1);
            using (var cancellation = new CancellationTokenSource(Timeout))
            using (await fixture.Transitions.AcquireAsync(fixture.Session.Player.CharacterId, cancellation.Token))
                check("completed return releases the shared character transition lease", fixture.Transitions.IsCurrent(fixture.Session));
            var acquiredRunGate = await fixture.Session.Player.DungeonRunTransitionGate.WaitAsync(Timeout);
            if (acquiredRunGate) fixture.Session.Player.DungeonRunTransitionGate.Release();
            check("completed return leaves the ordinary run gate and send gate available",
                acquiredRunGate && fixture.SendGate.CurrentCount == 1);
            await fixture.MenuAsync().WaitAsync(Timeout);
            check("a subsequent real START_GAME request completes through the same character and send gates",
                fixture.Progress.Reads == 2 && IsMenu(fixture.Packets[^1]) && await fixture.ReturnAsync().WaitAsync(Timeout)
                && fixture.ProjectionCalls == 2);
        }

        private static async Task VerifyRetriesAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.MenuAsync();
                fixture.Service.Close();
                check("closing the admission schedule does not revoke an already opened menu's exit",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1);
                check("completed menu exit can be retried while admission remains closed",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1 && IsReturnAck(fixture.Packets[^1]));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.MenuAsync();
                fixture.FailNextWrite = true;
                var count = fixture.Packets.Count;
                check("a failed return ACK consumes its owner route without starting town projection",
                    await fixture.ReturnAsync() && fixture.Packets.Count == count && fixture.ProjectionCalls == 0);
                check("return ACK transport recovery reuses the still-pending receipt",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1 && fixture.AckBeforeProjection);
            }
            foreach (var failure in new[] { "false", "partial", "exception" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                await fixture.MenuAsync();
                fixture.ProjectionSucceeds = failure != "false";
                fixture.FailTownAfterFrames = failure == "partial" ? 1 : -1;
                fixture.ProjectionError = failure == "exception" ? new IOException("injected town projection failure") : null;
                check(failure + " town projection remains consumed without marking the receipt completed",
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 1);
                fixture.ProjectionSucceeds = true;
                fixture.FailTownAfterFrames = -1;
                fixture.ProjectionError = null;
                check(failure + " projection can retry its ACK and finish town without duplicate durable effects",
                    await fixture.ReturnAsync().WaitAsync(Timeout) && fixture.ProjectionCalls == 2 && fixture.AckBeforeProjection);
                check("successful retry becomes a projection-free duplicate: " + failure,
                    await fixture.ReturnAsync() && fixture.ProjectionCalls == 2);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.MenuAsync();
                fixture.ProjectionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.ProjectionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var first = fixture.ReturnAsync();
                await fixture.ProjectionEntered.Task.WaitAsync(Timeout);
                var second = fixture.ReturnAsync();
                try
                {
                    check("a concurrent duplicate waits for the same character gate while town projection is active",
                        !first.IsCompleted && !second.IsCompleted && fixture.ProjectionCalls == 1);
                }
                finally { fixture.ProjectionRelease.TrySetResult(true); }
                var results = await Task.WhenAll(first, second).WaitAsync(Timeout);
                check("concurrent menu returns produce two ACKs and exactly one completed town projection",
                    results.All(x => x) && fixture.Packets.Count(IsReturnAck) == 2
                    && fixture.ProjectionCalls == 1 && fixture.SendGate.CurrentCount == 1);
            }
        }

        private static (string Label, Action<Fixture> Change)[] SourceChanges() => new[] {
            ("account", (Action<Fixture>)(f => f.Session.Account.AccountId++)),
            ("account object with the same id", (Action<Fixture>)(f => f.Session.Account = new AccountRecord { AccountId = 11 })),
            ("missing account", (Action<Fixture>)(f => f.Session.Account = null)),
            ("character", (Action<Fixture>)(f => f.Session.Player.CharacterId++)),
            ("uid", (Action<Fixture>)(f => f.Session.Player.UserId++)),
            ("GameSession", (Action<Fixture>)(f => f.Session.GameSession = GameSessionIdentity())),
            ("directory session", (Action<Fixture>)(f => f.CreateSession(f.Session.Player.CharacterId))),
            ("directory removal", (Action<Fixture>)(f => f.Sessions.UnregisterAsync(
                f.Session.Player.CharacterId, f.Session).GetAwaiter().GetResult())),
            ("town", (Action<Fixture>)(f => f.Session.Player.CurTownId++)),
            ("area", (Action<Fixture>)(f => f.Session.Player.CurAreaId++)),
            ("run epoch", (Action<Fixture>)(f => f.Session.Player.NextDungeonRunGeneration())),
            ("ordinary run", (Action<Fixture>)(f => f.Session.Player.CurrentRun = new DungeonRun())),
            ("ordinary selection", (Action<Fixture>)(f => f.Session.Player.BeginDungeonSelection(Anchor(f.Session)))),
            ("selection epoch after cancellation", (Action<Fixture>)(f => {
                var selection = f.Session.Player.BeginDungeonSelection(Anchor(f.Session));
                f.Session.Player.TryInvalidateDungeonSelection(selection);
            })),
            ("selection pending", (Action<Fixture>)(f => f.Session.Player.DungeonSelectionPending = true)),
            ("presence", (Action<Fixture>)(f => f.Session.Player.TownPresenceReady = false)),
            ("user state", (Action<Fixture>)(f => f.Session.Player.UserState = 1))
        };

        private static async Task VerifySourcesAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            foreach (var change in SourceChanges())
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                await fixture.MenuAsync();
                change.Change(fixture);
                var run = fixture.Session.Player.CurrentRun;
                var selection = fixture.Session.Player.CurrentDungeonSelection;
                var count = fixture.Packets.Count;
                check("a stale menu receipt cannot swallow the ordinary return route after " + change.Label,
                    !await fixture.ReturnAsync() && fixture.Packets.Count == count && fixture.ProjectionCalls == 0
                    && ReferenceEquals(run, fixture.Session.Player.CurrentRun)
                    && ReferenceEquals(selection, fixture.Session.Player.CurrentDungeonSelection));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var selection = fixture.Session.Player.BeginDungeonSelection(Anchor(fixture.Session));
                await fixture.MenuAsync();
                check("a rejected menu over an ordinary selection cannot steal its BACK_2_VILLAGE owner",
                    !await fixture.ReturnAsync() && ReferenceEquals(selection, fixture.Session.Player.CurrentDungeonSelection)
                    && fixture.ProjectionCalls == 0);
            }
        }

        private static async Task VerifyQueuedAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            foreach (var change in SourceChanges())
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                await fixture.MenuAsync();
                var count = fixture.Packets.Count;
                Task<bool> pending;
                using (await fixture.Transitions.AcquireAsync(fixture.Session.Player.CharacterId))
                {
                    pending = fixture.ReturnAsync();
                    check("owned menu exit waits for the existing character transition gate: " + change.Label,
                        !pending.IsCompleted);
                    change.Change(fixture);
                }
                var run = fixture.Session.Player.CurrentRun;
                var selection = fixture.Session.Player.CurrentDungeonSelection;
                check("an owned exit displaced while queued is consumed and suppresses old projection: " + change.Label,
                    await pending.WaitAsync(Timeout) && fixture.Packets.Count == count && fixture.ProjectionCalls == 0
                    && ReferenceEquals(run, fixture.Session.Player.CurrentRun)
                    && ReferenceEquals(selection, fixture.Session.Player.CurrentDungeonSelection));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.MenuAsync();
                var count = fixture.Packets.Count;
                Task<bool> pending;
                await fixture.SendGate.WaitAsync();
                try
                {
                    pending = fixture.ReturnAsync();
                    check("menu exit uses the existing packet send lock before preparing its ACK", !pending.IsCompleted);
                    fixture.Session.GameSession = GameSessionIdentity();
                }
                finally { fixture.SendGate.Release(); }
                check("the final send-lock fence rejects an old menu without ACK or town frames",
                    await pending.WaitAsync(Timeout) && fixture.Packets.Count == count && fixture.ProjectionCalls == 0
                    && fixture.SendGate.CurrentCount == 1);
            }
            foreach (var boundary in new[] { "after ACK", "during town projection" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                await fixture.MenuAsync();
                Action replace = () => fixture.Session.Player.CurrentRun = new DungeonRun();
                if (boundary == "after ACK") fixture.AfterWrite = replace;
                else fixture.DuringProjection = replace;
                var count = fixture.Packets.Count;
                check("source replacement " + boundary + " cannot end the new run or finish old town projection",
                    await fixture.ReturnAsync().WaitAsync(Timeout)
                    && fixture.Session.Player.CurrentRun != null && fixture.Packets.Count == count + 1
                    && IsReturnAck(fixture.Packets[^1]));
                count = fixture.Packets.Count;
                check("the replaced scope " + boundary + " no longer owns the next return",
                    !await fixture.ReturnAsync() && fixture.Packets.Count == count);
            }
        }

        private static async Task VerifyRenewalAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            await fixture.MenuAsync();
            await fixture.ReturnAsync();
            var oldFence = fixture.LastProjectionFence;
            await fixture.MenuAsync();
            check("a newer complete START_GAME menu invalidates the previous projection fence",
                oldFence != null && !oldFence());
            check("a renewed menu is pending even when identity and movement sequence are unchanged",
                await fixture.ReturnAsync() && fixture.ProjectionCalls == 2);
            check("the renewed menu has its own completed idempotent return",
                await fixture.ReturnAsync() && fixture.ProjectionCalls == 2);
            fixture.Coordinator.Dispose();
            var count = fixture.Packets.Count;
            check("disposing the menu owner invalidates retained return credentials",
                !await fixture.ReturnAsync() && fixture.Packets.Count == count);
        }

        private sealed class ProgressStub : IGuardianAltarProgressService
        {
            private readonly GuardianAltarMenuDefinition _definition;
            internal int Reads;
            internal ProgressStub(GuardianAltarMenuDefinition definition) => _definition = definition;
            public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow, Func<bool> isCurrent = null)
            {
                if (isCurrent != null && !isCurrent()) throw new InvalidOperationException("old menu fixture source");
                Reads++;
                return new GuardianAltarProgressSnapshot(characterId,
                    new GuardianAltarHeader(_definition.RidableId, _definition.DailyRefillTicket, 0, 0, 0, 0, 0, 0, 0, 0),
                    new[] { new GuardianAltarStage(_definition.FirstDungeonId, -1) },
                    _definition.InitialPurchases, _definition.InitialSlots, Array.Empty<GuardianAltarAchievement>());
            }
        }

        private sealed class MercenaryStub : IMercenaryRestrictionService
        {
            public bool IsAssigned(int characterId) => false;
            public bool CanDelete(int characterId) => true;
            public bool CanMutateAppearance(int characterId) => true;
            public bool CanEnterContent(int characterId) => true;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<EnhancedClientSession> _sessions = new();
            internal readonly SemaphoreSlim SendGate = new(1, 1);
            internal readonly GuardianAltarOpeningService Service = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly GuardianAltarOpeningCoordinator Coordinator;
            internal readonly GameCommandRegistry Registry = new();
            internal readonly ProgressStub Progress;
            internal readonly List<byte[]> Packets = new();
            internal readonly EnhancedClientSession Session;
            internal bool FailNextWrite;
            internal bool FailAfterWrite;
            internal Action AfterWrite;
            internal Action DuringProjection;
            internal TaskCompletionSource<bool> ProjectionEntered;
            internal TaskCompletionSource<bool> ProjectionRelease;
            internal bool ProjectionSucceeds = true;
            internal Exception ProjectionError;
            internal int FailTownAfterFrames = -1;
            internal int ProjectionCalls;
            internal bool AckBeforeProjection = true;
            internal bool ProjectionOutsideSendLock = true;
            internal Func<bool> LastProjectionFence;
            private readonly DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

            internal Fixture(GuardianAltarMenuDefinition definition)
            {
                Progress = new ProgressStub(definition);
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Coordinator = new GuardianAltarOpeningCoordinator(Service, Sessions, Transitions,
                    clock: null, utcNow: () => _now, sendBatch: SendBatchAsync, progress: Progress);
                Registry.RegisterGroup("guardian-menu-return-test", group =>
                    Coordinator.RegisterEntryHandlers(group, _ => false, new MercenaryStub()));
                Session = CreateSession(71000101);
            }
            internal void Open() => Service.SetWindow(_now, _now.AddMinutes(5));
            internal EnhancedClientSession CreateSession(int characterId)
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 11 };
                session.GameSession = GameSessionIdentity();
                session.Player.CharacterId = characterId;
                session.Player.UserId = 12001;
                session.Player.Level = 70;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.Player.CurTownId = 1;
                session.Player.CurAreaId = 2;
                session.Player.CurPosX = 100;
                session.Player.CurPosY = 200;
                _sessions.Add(session);
                Sessions.Register(characterId, session);
                return session;
            }
            internal Task MenuAsync()
            {
                if (!Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME, out var handler))
                    throw new InvalidOperationException("Guardian menu handler not registered");
                return handler(Session, new GamePacketHeader { cmd = 1,
                    type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME, length = 14 }, Array.Empty<byte>()).WaitAsync(Timeout);
            }
            internal Task<bool> ReturnAsync(byte[] body = null, byte command = 1, ushort? type = null)
                => Coordinator.TryHandleMenuReturnAsync(Session, new GamePacketHeader { cmd = command,
                    type = type ?? (ushort)CmdPacketTypeA21.BACK_2_VILLAGE,
                    length = checked((uint)(14 + (body?.Length ?? 0))) }, body ?? Array.Empty<byte>(), ProjectTownAsync).WaitAsync(Timeout);
            private Task SendBatchAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
                => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(SendGate, prepare, packet =>
                {
                    if (FailNextWrite)
                    {
                        FailNextWrite = false;
                        throw new IOException("injected menu-return ACK write failure");
                    }
                    Packets.Add(packet.ToArray());
                    var after = AfterWrite; AfterWrite = null; after?.Invoke();
                    if (FailAfterWrite)
                    {
                        FailAfterWrite = false;
                        throw new IOException("injected uncertain full-frame write");
                    }
                    return Task.CompletedTask;
                });

            // Injected Town contract verifies owner/send-gate ordering. It does
            // not replace full Town persistence, roster, raid or client UI tests.
            private async Task<bool> ProjectTownAsync(EnhancedClientSession session, Func<bool> current)
            {
                ProjectionCalls++;
                AckBeforeProjection &= Packets.Count > 0 && IsReturnAck(Packets[^1]);
                ProjectionOutsideSendLock &= SendGate.CurrentCount == 1;
                LastProjectionFence = current;
                var during = DuringProjection; DuringProjection = null; during?.Invoke();
                if (ProjectionRelease != null)
                {
                    ProjectionEntered.TrySetResult(true);
                    await ProjectionRelease.Task.WaitAsync(Timeout);
                }
                if (!current()) return false;
                if (ProjectionError != null) throw ProjectionError;
                if (!ProjectionSucceeds) return false;
                var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                var packets = new[] {
                    GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.USER_STATE,
                        EnterSelectDungeonStateBuilder.BuildUserState(session.Player)),
                    GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.USER_AREA,
                        TownAreaNotificationBuilder.BuildUserArea(snapshot)),
                    GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.AREA_USERS,
                        TownAreaNotificationBuilder.BuildAreaUsers(snapshot)) };
                for (var index = 0; index < packets.Length; index++)
                {
                    if (index == FailTownAfterFrames) throw new IOException("injected partial town projection");
                    if (!await SendProjectionAsync(packets[index], current)) return false;
                }
                return current();
            }
            internal async Task<bool> SendProjectionAsync(byte[] packet, Func<bool> current)
            {
                var sent = false;
                await EnhancedClientSession.SendPreparedPacketBatchCoreAsync(SendGate,
                    () => current() ? new[] { packet } : Array.Empty<byte[]>(), body =>
                    { Packets.Add(body.ToArray()); sent = true; return Task.CompletedTask; });
                return sent;
            }
            public void Dispose()
            {
                Coordinator.Dispose();
                SendGate.Dispose();
                foreach (var session in _sessions) session.Close();
            }
        }

        private static bool IsMenu(byte[] packet)
            => packet.Length > 20 && packet[0] == 1
                && Type(packet) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME
                && BitConverter.ToUInt32(packet, 3) == packet.Length
                && packet[15] == 1 && BitConverter.ToInt32(packet, 16) == 0;
        private static bool IsReturnAck(byte[] packet)
            => packet.Length == 16 && packet[0] == 1
                && Type(packet) == (ushort)CmdPacketTypeA21.BACK_2_VILLAGE
                && BitConverter.ToUInt32(packet, 3) == packet.Length && packet[15] == 1;
        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);
        private static DungeonTownReturnAnchor Anchor(EnhancedClientSession session)
            => new(session.Player.CurTownId, session.Player.CurAreaId, session.Player.CurPosX,
                session.Player.CurPosY, session.Player.CurDirection, session.Player.CurAreaState);
        private static GameSession GameSessionIdentity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
    }
}
