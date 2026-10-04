using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarOpeningSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ENTRY selftest ===");
            var failures = 0;
            void Check(string label, bool success)
            {
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }

            try
            {
                CheckWindows(Check);
                CheckConfiguration(Check);
                CheckWireAndLegacyInitialization(Check);
                CheckCoordinatorAsync(Check).GetAwaiter().GetResult();
                CheckPublicationIdentityAsync(Check).GetAwaiter().GetResult();
                CheckFailedPublicationAsync(Check).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Check("Guardian Altar entry selftest exception: " + exception, false);
            }
            Console.WriteLine($"GUARDIAN_ALTAR_ENTRY failures={failures}");
            return failures;
        }

        private static void CheckWindows(Action<string, bool> check)
        {
            var start = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            var end = start.AddSeconds(10);
            var service = new GuardianAltarOpeningService();
            var initial = service.GetSnapshot(start);
            check("Guardian entry defaults to closed without a fabricated deadline",
                !initial.IsOpen && initial.WireState == 0
                && initial.RemainingSeconds == 0 && initial.NextTransitionUtc == null);
            check("closing an unconfigured entry is idempotent", service.Close() == initial.Revision);

            var revision = service.SetWindow(start, end);
            var before = service.GetSnapshot(start.AddTicks(-1));
            var atStart = service.GetSnapshot(start);
            var partial = service.GetSnapshot(start.AddMilliseconds(1));
            var lastTick = service.GetSnapshot(end.AddTicks(-1));
            var atEnd = service.GetSnapshot(end);
            check("entry is closed before the configured start and exposes its next boundary",
                !before.IsOpen && before.RemainingSeconds == 0 && before.NextTransitionUtc == start);
            check("entry includes the exact start and excludes the exact end",
                atStart.IsOpen && atStart.WireState == 1 && atStart.RemainingSeconds == 10
                && atStart.NextTransitionUtc == end && !atEnd.IsOpen
                && atEnd.RemainingSeconds == 0 && atEnd.NextTransitionUtc == null);
            check("countdown uses ceiling seconds, including the final fractional second",
                partial.RemainingSeconds == 10 && lastTick.IsOpen && lastTick.RemainingSeconds == 1);
            check("same absolute window does not change its revision",
                service.SetWindow(start.ToOffset(TimeSpan.FromHours(8)), end) == revision);
            check("snapshot remains correct when no timer executes or wall clock moves backwards",
                !service.GetSnapshot(end.AddHours(1)).IsOpen
                && service.GetSnapshot(start.AddSeconds(2)).RemainingSeconds == 8);

            var rejected = 0;
            foreach (var invalidEnd in new[] { start, start.AddTicks(-1), start.AddSeconds((long)int.MaxValue + 1) })
            {
                try { service.SetWindow(start, invalidEnd); }
                catch (ArgumentOutOfRangeException) { rejected++; }
            }
            check("invalid windows are rejected without changing the active window or revision",
                rejected == 3 && service.Revision == revision
                && service.GetSnapshot(start).IsOpen
                && service.GetSnapshot(start).RemainingSeconds == 10);
            var maximum = new GuardianAltarOpeningService();
            maximum.SetWindow(start, start.AddSeconds(int.MaxValue));
            check("maximum Int32 countdown is exact without overflow",
                maximum.GetSnapshot(start).RemainingSeconds == int.MaxValue);
            var closedRevision = service.Close();
            check("explicit close changes revision once and is idempotent thereafter",
                closedRevision > revision && service.Close() == closedRevision
                && !service.GetSnapshot(start).IsOpen);
        }

        private static void CheckConfiguration(Action<string, bool> check)
        {
            check("missing entry configuration means disabled, not an inferred weekend schedule",
                GuardianAltarOpeningService.TryParseConfiguration(null, " ", out var missingStart,
                    out var missingEnd, out var missingError)
                && missingStart == null && missingEnd == null && string.IsNullOrEmpty(missingError));
            check("entry configuration accepts explicit timezone and normalizes equivalent instants to UTC",
                GuardianAltarOpeningService.TryParseConfiguration("2026-10-02T18:00:00+08:00",
                    "2026-10-02T10:30:00Z", out var parsedStart, out var parsedEnd, out var parseError)
                && parsedStart == new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero)
                && parsedEnd == parsedStart.Value.AddMinutes(30) && string.IsNullOrEmpty(parseError));
            foreach (var invalid in new[]
            {
                ("2026-10-02T10:00:00Z", (string)null),
                ((string)null, "2026-10-02T10:30:00Z"),
                ("2026-10-02T10:00:00", "2026-10-02T10:30:00Z"),
                ("2026-10-02T10:00Z", "2026-10-02T10:30:00Z"),
                ("2026-13-02T10:00:00Z", "2026-10-02T10:30:00Z"),
                ("not-a-timestamp", "2026-10-02T10:30:00Z"),
                ("2026-10-02T10:30:00Z", "2026-10-02T10:30:00Z"),
                ("2026-10-02T11:00:00Z", "2026-10-02T10:30:00Z"),
                ("2026-10-02T10:00:00Z", "9999-10-02T10:30:00Z")
            })
            {
                check("malformed, incomplete, timezone-free or overflowing configuration fails closed",
                    !GuardianAltarOpeningService.TryParseConfiguration(invalid.Item1, invalid.Item2,
                        out var badStart, out var badEnd, out var error)
                    && badStart == null && badEnd == null && !string.IsNullOrWhiteSpace(error));
            }

            var startVariable = GuardianAltarOpeningService.StartEnvironmentVariable;
            var endVariable = GuardianAltarOpeningService.EndEnvironmentVariable;
            var previousStart = Environment.GetEnvironmentVariable(startVariable);
            var previousEnd = Environment.GetEnvironmentVariable(endVariable);
            try
            {
                var service = new GuardianAltarOpeningService();
                Environment.SetEnvironmentVariable(startVariable, null);
                Environment.SetEnvironmentVariable(endVariable, null);
                check("startup without test-window environment remains disabled",
                    service.TryConfigureEnvironment(out _) && !service.GetSnapshot(DateTimeOffset.UtcNow).IsOpen);
                Environment.SetEnvironmentVariable(startVariable, "2026-10-02T10:00:00Z");
                Environment.SetEnvironmentVariable(endVariable, "2026-10-02T10:30:00Z");
                var observed = new DateTimeOffset(2026, 10, 2, 10, 10, 0, TimeSpan.Zero);
                check("explicit startup window can be reconstructed without player database state",
                    service.TryConfigureEnvironment(out _) && service.GetSnapshot(observed).IsOpen);
                Environment.SetEnvironmentVariable(endVariable, "invalid");
                check("invalid environment closes a previously configured test window",
                    !service.TryConfigureEnvironment(out var error)
                    && !string.IsNullOrWhiteSpace(error) && !service.GetSnapshot(observed).IsOpen);
            }
            finally
            {
                Environment.SetEnvironmentVariable(startVariable, previousStart);
                Environment.SetEnvironmentVariable(endVariable, previousEnd);
            }
        }

        private static void CheckWireAndLegacyInitialization(Action<string, bool> check)
        {
            var start = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            var service = new GuardianAltarOpeningService();
            service.SetWindow(start, start.AddSeconds(37));
            var body = GuardianAltarOpeningBodyBuilder.Build(service.GetSnapshot(start));
            check("Guardian schedule body preserves four Int32LE fields in client-consumption order",
                body.SequenceEqual(new byte[] { 4, 0, 0, 0, 1, 0, 0, 0, 10, 0, 0, 0, 37, 0, 0, 0 }));
            check("entry projection uses the A21 schedule enum, not account tutorial or an entry ACK",
                (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE == 831
                && (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE
                    != (ushort)NotiPacketTypeA21.ACCOUNT_TUTORIAL_INFO
                && (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE
                    != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME);
            var frame = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE, body);
            check("Guardian projection has a 15B S2C envelope, NOTI command, exact length and body",
                frame.Length == 31 && frame[0] == 0
                && BitConverter.ToUInt16(frame, 1) == (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE
                && BitConverter.ToInt32(frame, 3) == frame.Length
                && frame.AsSpan(15).SequenceEqual(body));
            service.Close();
            check("closed projection keeps the content identity and writes zero state/countdown",
                GuardianAltarOpeningBodyBuilder.Build(service.GetSnapshot(start)).SequenceEqual(
                    new byte[] { 4, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0, 0 }));

            var legacy = new DailyScheduleBodyBuilder();
            var snapshot = new SelectCharacterDataSnapshot();
            snapshot.InitializationSnapshot.Unknown725Packets.Add(new Unknown725Snapshot
                { ParamA = 7, ModeOrState = 2, ContentId = 19, ParamB = 101 });
            check("legacy 725 snapshot builder keeps its account tutorial opcode and four original values",
                legacy.NotiType == (ushort)NotiPacketTypeA21.ACCOUNT_TUTORIAL_INFO
                && legacy.TryBuild(snapshot, 0, out var legacyBody)
                && legacyBody.Length == 16 && BitConverter.ToInt32(legacyBody, 0) == 7
                && BitConverter.ToInt32(legacyBody, 4) == 2 && BitConverter.ToInt32(legacyBody, 8) == 19
                && BitConverter.ToInt32(legacyBody, 12) == 101);
            var sequence = NewCharacterInitSequence.Build();
            var tutorials = sequence.Where(entry => entry.Kind == SelectCharacterPacketTemplateKind.Raw
                && entry.Command == 0 && entry.Type == (ushort)NotiPacketTypeA21.ACCOUNT_TUTORIAL_INFO).ToArray();
            check("existing initialization retains three tutorial occurrences and does not forge 914 success",
                tutorials.Select(entry => entry.OccurrenceIndex).SequenceEqual(new[] { 0, 1, 2 })
                && !sequence.Any(entry => entry.Command == 1
                    && entry.Type == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME));
        }

        private static async Task CheckCoordinatorAsync(Action<string, bool> check)
        {
            using var fixture = new PublicationFixture();
            var session = fixture.CreateSession(61001);
            var coordinator = fixture.Coordinator;
            coordinator.Start();
            using (await fixture.Transitions.AcquireIfCurrentAsync(session))
            {
                await coordinator.NotifyStateOnLoginWhileTransitionHeldAsync(session)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            check("login notification does not reacquire its already-held character transition gate",
                fixture.Packets.Count == 1 && ReadState(fixture.Packets[0]) == 0);
            await coordinator.RefreshAsync();
            check("same generation, window revision and state are not published twice", fixture.Packets.Count == 1);
            var starts = fixture.Now.AddSeconds(-1);
            var ends = fixture.Now.AddSeconds(9);
            await coordinator.OpenAsync(starts, ends);
            await coordinator.OpenAsync(starts, ends);
            await coordinator.RefreshAsync();
            check("opening a window publishes once and equivalent repeated opens are idempotent",
                fixture.Packets.Count == 2 && ReadState(fixture.Packets[1]) == 1
                && BitConverter.ToInt32(fixture.Packets[1], 27) == 9);
            var revision = fixture.Service.Revision;
            fixture.Now = ends;
            await coordinator.OnWindowBoundaryAsync(revision);
            await coordinator.OnWindowBoundaryAsync(revision);
            check("exact end boundary publishes one close even without a running timer",
                fixture.Packets.Count == 3 && ReadState(fixture.Packets[2]) == 0);
            var oldRevision = revision;
            await coordinator.OpenAsync(fixture.Now, fixture.Now.AddMinutes(5));
            var newerRevision = fixture.Service.Revision;
            var count = fixture.Packets.Count;
            await coordinator.OnWindowBoundaryAsync(oldRevision);
            check("obsolete window callback cannot close or publish over a newer open window",
                fixture.Service.Revision == newerRevision && fixture.Service.GetSnapshot(fixture.Now).IsOpen
                && fixture.Packets.Count == count);
            await coordinator.CloseAsync();
            await coordinator.CloseAsync();
            check("explicit close publishes once without duplicate closed notifications",
                fixture.Packets.Count == count + 1 && ReadState(fixture.Packets[^1]) == 0);
            var closeRevision = fixture.Service.Revision;
            var rejected = false;
            try { await coordinator.OpenAsync(fixture.Now.AddMinutes(-1), fixture.Now); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check("expired manual opening is rejected without mutation or notification",
                rejected && fixture.Service.Revision == closeRevision
                && fixture.Packets.Count == count + 1 && !fixture.Service.GetSnapshot(fixture.Now).IsOpen);
            check("entry-only coordinator publishes no gameplay success response",
                fixture.Packets.All(packet => packet[0] == 0
                    && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.DAILYSCHEDULE_CONTENTS_STATE));
        }

        private static async Task CheckPublicationIdentityAsync(Action<string, bool> check)
        {
            using var fixture = new PublicationFixture();
            var session = fixture.CreateSession(61002);
            fixture.Service.SetWindow(fixture.Now, fixture.Now.AddMinutes(5));
            Task queued;
            using (await fixture.Transitions.AcquireIfCurrentAsync(session))
            {
                queued = fixture.Coordinator.RefreshAsync();
                session.GameSession = CreateGameSessionIdentity();
            }
            await queued;
            check("queued notification cannot follow a same-character reselection through the transition gate",
                fixture.Packets.Count == 0);
            fixture.BeforePrepare = () => session.GameSession = CreateGameSessionIdentity();
            await fixture.Coordinator.RefreshAsync();
            check("same TCP and character with a replaced GameSession rejects queued old-generation projection",
                fixture.Packets.Count == 0);
            await fixture.Coordinator.RefreshAsync();
            check("new GameSession generation receives a fresh current-state projection",
                fixture.Packets.Count == 1 && ReadState(fixture.Packets[0]) == 1);
            session.GameSession = CreateGameSessionIdentity();
            await fixture.Coordinator.RefreshAsync();
            check("deduplication does not suppress same-character reselection on the same connection",
                fixture.Packets.Count == 2);

            fixture.Service.Close();
            var replacement = fixture.CreateSession(61002, register: false);
            fixture.BeforePrepare = () => fixture.Sessions.Register(61002, replacement);
            var oldCount = fixture.Packets.Count;
            await fixture.Coordinator.RefreshAsync();
            check("directory replacement rejects prepared projection to displaced connection",
                fixture.Packets.Count == oldCount);
            await fixture.Coordinator.RefreshAsync();
            check("replacement session receives current close independently of displaced-session cache",
                fixture.Packets.Count == oldCount + 1 && ReferenceEquals(fixture.Recipients[^1], replacement)
                && ReadState(fixture.Packets[^1]) == 0);

            fixture.Service.SetWindow(fixture.Now, fixture.Now.AddMinutes(5));
            fixture.BeforePrepare = () => fixture.Service.Close();
            var beforeChangedWindow = fixture.Packets.Count;
            await fixture.Coordinator.RefreshAsync();
            check("window revision changed before preparation rejects queued stale-open projection",
                fixture.Packets.Count == beforeChangedWindow && !fixture.Service.GetSnapshot(fixture.Now).IsOpen);
            await fixture.Coordinator.RefreshAsync();
            check("subsequent refresh projects the new authoritative close, not the queued open snapshot",
                fixture.Packets.Count == beforeChangedWindow + 1 && ReadState(fixture.Packets[^1]) == 0);
            await fixture.Sessions.UnregisterAsync(61002, replacement);
            fixture.Sessions.Register(61002, replacement);
            await fixture.Coordinator.NotifyStateOnLoginWhileTransitionHeldAsync(replacement);
            check("session ending clears projection deduplication for a later registered lifecycle",
                fixture.Packets.Count == beforeChangedWindow + 2);
            var beforeDispose = fixture.Packets.Count;
            fixture.Coordinator.Dispose();
            await fixture.Coordinator.RefreshAsync();
            await fixture.Coordinator.OnWindowBoundaryAsync(fixture.Service.Revision);
            check("disposed entry owner ignores later refresh and timer callback",
                fixture.Packets.Count == beforeDispose);
        }

        private static async Task CheckFailedPublicationAsync(Action<string, bool> check)
        {
            using var fixture = new PublicationFixture();
            fixture.CreateSession(61003);
            fixture.Service.SetWindow(fixture.Now, fixture.Now.AddMinutes(5));
            fixture.FailNextWrite = true;
            try { await fixture.Coordinator.RefreshAsync(); }
            catch (IOException) { }
            check("failed transport write has not been recorded as a successful notification",
                fixture.Packets.Count == 0);
            await fixture.Coordinator.RefreshAsync();
            check("refresh retries an authoritative notification after transport failure",
                fixture.Packets.Count == 1 && ReadState(fixture.Packets[0]) == 1);
        }

        private static int ReadState(byte[] packet) => BitConverter.ToInt32(packet, 19);

        private static GameSession CreateGameSessionIdentity()
        {
            // Only an identity token for reference-generation guards. Do not
            // call members: the real constructor owns QuestManager/database
            // initialization, which is deliberately outside this entry test.
            return (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        }

        private sealed class PublicationFixture : IDisposable
        {
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            internal readonly GuardianAltarOpeningService Service = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly GuardianAltarOpeningCoordinator Coordinator;
            internal readonly List<byte[]> Packets = new();
            internal readonly List<EnhancedClientSession> Recipients = new();
            internal DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            internal Action BeforePrepare;
            internal bool FailNextWrite;

            internal PublicationFixture()
            {
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Coordinator = new GuardianAltarOpeningCoordinator(Service, Sessions, Transitions,
                    clock: null, utcNow: () => Now, sendBatch: SendBatchAsync);
            }

            internal EnhancedClientSession CreateSession(int characterId, bool register = true)
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Player.CharacterId = characterId;
                session.Player.UserId = checked((ushort)characterId);
                session.GameSession = CreateGameSessionIdentity();
                _ownedSessions.Add(session);
                if (register)
                    Sessions.Register(characterId, session);
                return session;
            }

            private Task SendBatchAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
            {
                var beforePrepare = BeforePrepare;
                BeforePrepare = null;
                beforePrepare?.Invoke();
                var packets = prepare();
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    throw new IOException("Guardian entry test transport failure");
                }
                foreach (var packet in packets)
                {
                    Packets.Add(packet.ToArray());
                    Recipients.Add(session);
                }
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                foreach (var session in _ownedSessions)
                    session.Close();
            }
        }
    }
}
