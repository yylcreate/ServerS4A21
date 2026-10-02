using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    internal static class DungeonLoadingFenceSelfTest
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        internal static int Run()
        {
            Console.WriteLine("=== DUNGEON_LOADING_FENCE selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Func<Task> action)
            {
                try { action().GetAwaiter().GetResult(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("final loading writes", () => CheckLoadingWritesAsync(Check));
            Section("tower stage fence", () => CheckTowerStageAsync(Check));
            Section("delayed exit routing", () => CheckDelayedExitsAsync(Check));
            Console.WriteLine($"DUNGEON_LOADING_FENCE assertions={assertions} failures={failures}");
            return failures;
        }

        private static async Task CheckLoadingWritesAsync(Action<string, bool> check)
        {
            foreach (var change in SourceChanges())
            {
                using var fixture = new SourceFixture();
                var fence = fixture.Fence();
                var followedUp = 0;
                var forwardedFence = false;
                check(change.Label + " begins with a current released loading generation", fence());
                await fixture.SendGate.WaitAsync();
                Task<bool> pending;
                try
                {
                    pending = TownHandler.SendFinishLoadingCompletionCoreAsync(fence,
                        (packet, finalFence) =>
                        {
                            forwardedFence = ReferenceEquals(finalFence, fence);
                            return fixture.Session.TrySendPacketAsync(packet, CancellationToken.None, finalFence);
                        }, () => { followedUp++; return Task.CompletedTask; });
                    check(change.Label + " waits on the real session send lock", !pending.IsCompleted);
                    change.Apply(fixture);
                }
                finally { fixture.SendGate.Release(); }
                var sent = await pending.WaitAsync(TestTimeout);
                check(change.Label + " suppresses the old final write and follow-up",
                    !sent && forwardedFence && followedUp == 0 && fixture.Peer.Available == 0
                    && fixture.SendGate.CurrentCount == 1);
            }

            using (var fixture = new SourceFixture())
            {
                var followedUp = 0;
                var sent = await TownHandler.SendFinishLoadingCompletionCoreAsync(fixture.Fence(),
                    (packet, fence) => fixture.Session.TrySendPacketAsync(packet, CancellationToken.None, fence),
                    () => { followedUp++; return Task.CompletedTask; }).WaitAsync(TestTimeout);
                var expected = GamePacketEnvelopeBuilder.Build(0,
                    (ushort)NotiPacketTypeA21.FINISH_LOADING, FinishLoadingBuilder.BuildNotification());
                var received = new byte[expected.Length];
                using var cancellation = new CancellationTokenSource(TestTimeout);
                await fixture.Peer.GetStream().ReadExactlyAsync(received, cancellation.Token);
                check("a valid generation writes the complete existing FINISH_LOADING envelope",
                    sent && followedUp == 1 && received.SequenceEqual(expected));
            }

            using (var fixture = new SourceFixture())
            {
                var followedUp = 0;
                var sent = await TownHandler.SendFinishLoadingCompletionCoreAsync(fixture.Fence(),
                    async (packet, fence) =>
                    {
                        var accepted = await fixture.Session.TrySendPacketAsync(packet, CancellationToken.None, fence);
                        fixture.Session.GameSession = GameSessionIdentity();
                        return accepted;
                    }, () => { followedUp++; return Task.CompletedTask; }).WaitAsync(TestTimeout);
                var expected = GamePacketEnvelopeBuilder.Build(0,
                    (ushort)NotiPacketTypeA21.FINISH_LOADING, FinishLoadingBuilder.BuildNotification());
                var received = new byte[expected.Length];
                using var cancellation = new CancellationTokenSource(TestTimeout);
                await fixture.Peer.GetStream().ReadExactlyAsync(received, cancellation.Token);
                check("a source that expires after the accepted frame cannot start its optional follow-up",
                    !sent && followedUp == 0 && received.SequenceEqual(expected));
            }

            using (var fixture = new SourceFixture())
            {
                var dungeonSource = DungeonLoadingCoordinator.CaptureFinishLoadingSourceFence(
                    fixture.Session, fixture.Sessions, fixture.Run);
                check("a current dungeon completion can call its existing loaded owner", dungeonSource());
                fixture.ReplaceRun();
                check("a replaced dungeon cannot receive the previous request's loaded callback", !dungeonSource());
                fixture.Session.Player.CurrentRun = null;
                var townSource = DungeonLoadingCoordinator.CaptureFinishLoadingSourceFence(
                    fixture.Session, fixture.Sessions, null);
                check("a still-current runless town completion retains its loaded callback contract", townSource());
                fixture.ReplaceRun();
                check("a runless town request cannot become a new dungeon's loaded callback", !townSource());
            }
        }

        private static async Task CheckTowerStageAsync(Action<string, bool> check)
        {
            using var fixture = new SourceFixture();
            var tower = new DeathTowerSession(new DeathTowerData.TowerConfig(
                104, new[] { 100001, 100002, 100003 }, 1, 0, false, false, false,
                DeathTowerRewardProfile.Standard, Array.Empty<DeathTowerData.TowerEntryItem>(), Array.Empty<DeathTowerData.TowerEntryItem>()),
                new[] { fixture.Session.Player.UserId });
            fixture.Run.Tower = tower;
            tower.SetFighting();
            check("tower fixture advances to a deferred second-floor loading release",
                tower.TryAdvanceStage() && tower.CurrentStage == 1
                && tower.TryDeferStageLoadingRelease(fixture.Run.CaptureIdentity(), tower.CurrentStage));
            var stage = tower.CurrentStage;
            check("tower loading ticket is consumed once before the pure send fence",
                tower.TryConsumeStageLoadingRelease(fixture.Run.CaptureIdentity(), stage)
                && !tower.HasPendingStageLoadingRelease(fixture.Run.CaptureIdentity(), stage));
            var run = fixture.Run;
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(
                fixture.Session, fixture.Sessions, run,
                additional: () => ReferenceEquals(run.Tower, tower) && tower.CurrentStage == stage);
            check("consuming the tower ticket does not invalidate the current-stage send fence", fence());
            await fixture.SendGate.WaitAsync();
            Task<bool> pending;
            try
            {
                pending = TownHandler.SendFinishLoadingCompletionCoreAsync(fence,
                    (packet, finalFence) => fixture.Session.TrySendPacketAsync(packet, CancellationToken.None, finalFence));
                tower.SetFighting();
                check("tower can advance while its old completion is waiting for the send lock",
                    !pending.IsCompleted && tower.TryAdvanceStage() && tower.CurrentStage == 2);
            }
            finally { fixture.SendGate.Release(); }
            check("an old tower floor cannot release loading in the next floor",
                !await pending.WaitAsync(TestTimeout) && fixture.Peer.Available == 0);
        }

        private static async Task CheckDelayedExitsAsync(Action<string, bool> check)
        {
            // These injected actions exercise the production gate/fence/claim
            // core. They do not perform player persistence or full town return.
            foreach (var change in SourceChanges())
            {
                using var fixture = new SourceFixture();
                var source = fixture.Run;
                var expected = source.CaptureParticipantRoomIdentity();
                var fence = fixture.Fence();
                var cohort = fixture.AttachCohort();
                var invoked = 0;
                var ensured = 0;
                await cohort.TransitionGate.WaitAsync();
                Task<bool> pending;
                try
                {
                    pending = TownHandler.TryHandleDelayedDungeonGiveupCoreAsync(
                        fixture.Session, expected, fence,
                        _ => { invoked++; return Task.CompletedTask; },
                        _ => { ensured++; return Task.CompletedTask; });
                    check(change.Label + " delayed cleanup waits on the existing cohort gate", !pending.IsCompleted);
                    change.Apply(fixture);
                }
                finally { cohort.TransitionGate.Release(); }
                var routed = await pending.WaitAsync(TestTimeout);
                check(change.Label + " stale cleanup cannot claim or route the replacement",
                    !routed && invoked == 0 && ensured == 0 && cohort.TransitionGate.CurrentCount == 1
                    && (change.Label == "run ending" || source.RunState == DungeonRunState.Active));
            }

            using (var fixture = new SourceFixture())
            {
                var source = fixture.Run;
                var expected = source.CaptureParticipantRoomIdentity();
                var routedSource = false;
                var ensured = 0;
                var routed = await TownHandler.TryHandleDelayedDungeonGiveupCoreAsync(
                    fixture.Session, expected, fixture.Fence(),
                    run =>
                    {
                        routedSource = ReferenceEquals(run, source) && run.RunState == DungeonRunState.Ending;
                        fixture.Session.Player.CurrentRun = null;
                        return Task.CompletedTask;
                    }, _ => { ensured++; return Task.CompletedTask; }).WaitAsync(TestTimeout);
                check("valid cleanup synchronously claims Active-to-Ending before routing the captured run",
                    routed && routedSource && ensured == 0 && fixture.Session.Player.CurrentRun == null);
                var repeated = await TownHandler.TryHandleDelayedDungeonGiveupCoreAsync(
                    fixture.Session, expected, () => true,
                    _ => throw new InvalidOperationException("must not reroute an ended source"),
                    _ => throw new InvalidOperationException("must not repeat source cleanup"));
                check("repeated cleanup after the source detached is a no-op", !repeated);
            }

            using (var fixture = new SourceFixture())
            {
                var expected = fixture.Run.CaptureParticipantRoomIdentity();
                fixture.Run.TryBeginEnding();
                var repeated = await TownHandler.TryHandleDelayedDungeonGiveupCoreAsync(
                    fixture.Session, expected, () => true,
                    _ => throw new InvalidOperationException("must not route an already Ending source"),
                    _ => throw new InvalidOperationException("must not claim another owner's ending"));
                check("an already Ending run cannot enter the delayed owner again", !repeated);
            }

            foreach (var replace in new[] { false, true })
            {
                using var fixture = new SourceFixture();
                var source = fixture.Run;
                var expected = source.CaptureParticipantRoomIdentity();
                var ensured = 0;
                var error = await ObserveAsync(() => TownHandler.TryHandleDelayedDungeonGiveupCoreAsync(
                    fixture.Session, expected, fixture.Fence(),
                    _ =>
                    {
                        if (replace) fixture.ReplaceRun();
                        throw new InvalidOperationException("opaque town continuation failure");
                    }, run =>
                    {
                        ensured++;
                        if (!ReferenceEquals(run, source))
                            throw new InvalidOperationException("cleanup received a replacement run");
                        fixture.Session.Player.CurrentRun = null;
                        return Task.CompletedTask;
                    }));
                check("opaque continuation failure retains its error and only ensures the attached source: replace=" + replace,
                    error is InvalidOperationException && ensured == (replace ? 0 : 1)
                    && (replace ? !ReferenceEquals(fixture.Session.Player.CurrentRun, source)
                        && fixture.Session.Player.CurrentRun.RunState == DungeonRunState.Active
                        : fixture.Session.Player.CurrentRun == null));
            }
        }

        private static (string Label, Action<SourceFixture> Apply)[] SourceChanges() => new[]
        {
            ("new run generation", (Action<SourceFixture>)(f => f.ReplaceRun())),
            ("new room", (Action<SourceFixture>)(f => f.MoveRoom())),
            ("same room new loading generation", (Action<SourceFixture>)(f => f.NextLoadingGeneration())),
            ("GameSession replaced", (Action<SourceFixture>)(f => f.Session.GameSession = GameSessionIdentity())),
            ("character changed", (Action<SourceFixture>)(f => f.Session.Player.CharacterId++)),
            ("UID changed", (Action<SourceFixture>)(f => f.Session.Player.UserId++)),
            ("account changed", (Action<SourceFixture>)(f => f.Session.Account = new AccountRecord { AccountId = 12 })),
            ("directory displaced", (Action<SourceFixture>)(f => f.DisplaceDirectory())),
            ("directory disconnected", (Action<SourceFixture>)(f => f.Sessions.UnregisterAsync(
                f.Session.Player.CharacterId, f.Session).GetAwaiter().GetResult())),
            ("transport disconnected", (Action<SourceFixture>)(f => f.Session.TcpClient.Close())),
            ("run ending", (Action<SourceFixture>)(f => f.Run.TryBeginEnding())),
            ("room closed", (Action<SourceFixture>)(f => f.Room.TryClose())),
        };

        private static GameSession GameSessionIdentity() =>
            (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));

        private static async Task<Exception> ObserveAsync(Func<Task> action)
        {
            try { await action().WaitAsync(TestTimeout); return null; }
            catch (Exception exception) { return exception; }
        }

        private sealed class SourceFixture : IDisposable
        {
            private readonly List<TcpClient> _extraClients = new();
            internal readonly SessionDirectory Sessions = new SessionDirectory((_, _) => true);
            internal EnhancedClientSession Session { get; }
            internal TcpClient Peer { get; }
            internal SemaphoreSlim SendGate { get; }
            internal DungeonRun Run { get; private set; }
            internal DungeonInstanceRoom Room { get; private set; }
            internal long LoadingGeneration { get; private set; }
            private long _projectionId;

            internal SourceFixture()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Peer = new TcpClient();
                TcpClient accepted = null;
                try
                {
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    var connecting = Peer.ConnectAsync(endpoint.Address, endpoint.Port);
                    accepted = listener.AcceptTcpClientAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult();
                    connecting.WaitAsync(TestTimeout).GetAwaiter().GetResult();
                    Session = new EnhancedClientSession(accepted, new GamePacketHeader());
                    Session.Account = new AccountRecord { AccountId = 11 };
                    Session.GameSession = GameSessionIdentity();
                    Session.Player.CharacterId = 71009001;
                    Session.Player.UserId = 12001;
                    Sessions.Register(Session.Player.CharacterId, Session);
                    SendGate = (SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock",
                        BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Session)
                        ?? throw new InvalidOperationException("The real session send lock was not found."));
                    ReplaceRun();
                }
                catch
                {
                    accepted?.Close();
                    Peer.Close();
                    throw;
                }
                finally { listener.Stop(); }
            }

            internal void ReplaceRun()
            {
                var instance = new DungeonInstance(3511, 0, DungeonRewardPolicy.GuardianAltar,
                    DungeonDropDefinition.CreateGuardianAltar(3511),
                    DungeonExperienceDefinition.CreateUnavailable(3511),
                    SequentialDungeonDefinitionCatalog.Parse("", _ => (byte?)null));
                Run = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(),
                    Session.Player.NextDungeonRunGeneration(), DungeonRunState.Active);
                Session.Player.CurrentRun = Run;
                MoveRoom();
            }

            internal void MoveRoom()
            {
                var key = new RoomKey((int)(++_projectionId), 0, 100001);
                var maze = new DfoServer.GameWorld.Dungeon.MazeSumInfo { Index = key.OverrideMapId, X = key.X, Y = key.Y };
                Room = Run.Instance.GetOrCreateRoom(key,
                    id => new DungeonInstanceRoom(id, key, maze, 0), out _);
                Room.TryActivate();
                Run.SetCurrentRoom(Room);
                NextLoadingGeneration();
            }

            internal void NextLoadingGeneration()
            {
                var projection = ++_projectionId;
                if (!Run.TryClaimLoadingProjection(projection) || !Run.TryBeginLoadingProjection(projection))
                    throw new InvalidOperationException("The fixture could not reserve a real loading projection.");
                var identity = Run.CaptureIdentity();
                LoadingGeneration = Room.RegisterLoadingProjection(identity, projection, new[] { identity }, out _);
                var ready = Room.MarkLoadingReady(identity, new[] { identity });
                if (LoadingGeneration <= 0 || !ready.Accepted || !ready.Released)
                    throw new InvalidOperationException("The fixture did not release its real room loading barrier.");
            }

            internal Func<bool> Fence()
            {
                var room = Room;
                var generation = LoadingGeneration;
                return DungeonLoadingCoordinator.CaptureContinuationFence(Session, Sessions, Run,
                    room.Identity, () => room.IsCurrentLoadingGeneration(generation));
            }

            internal DungeonPartySelectionCohort AttachCohort()
            {
                var cohort = new DungeonPartySelectionCohort(1, 1, Session.Player.UserId,
                    new[]
                    {
                        new DungeonPartySelectionParticipant(Session.Player.UserId,
                            Session.Player.CharacterId, Session.SessionId, 0),
                        new DungeonPartySelectionParticipant(12002, 71009002, Guid.NewGuid(), 1),
                    });
                Run.EntryPartySelectionCohort = cohort;
                return cohort;
            }

            internal void DisplaceDirectory()
            {
                var client = new TcpClient();
                _extraClients.Add(client);
                var replacement = new EnhancedClientSession(client, new GamePacketHeader());
                Sessions.Register(Session.Player.CharacterId, replacement);
            }

            public void Dispose()
            {
                Run?.Timers.CancelAll();
                Session?.TcpClient.Close();
                Peer?.Close();
                foreach (var client in _extraClients) client.Close();
            }
        }
    }
}
