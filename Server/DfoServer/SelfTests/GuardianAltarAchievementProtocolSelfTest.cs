using System;
using System.Buffers.Binary;
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
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarAchievementProtocolSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ACHIEVEMENT_PROTOCOL selftest ===");
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
            VerifyWire(Check);
            try
            {
                var definition = GuardianAltarMenuDefinition.Load();
                Section("registered achievement authority", () => VerifyAdmissionAsync(definition, Check));
                Section("committed result projection", () => VerifyResultsAsync(definition, Check));
                Section("ACK and mailbox retry", () => VerifyTransportAsync(definition, Check));
                Section("queued and final-source fencing", () => VerifyFencesAsync(definition, Check));
            }
            catch (Exception exception) { Check("current Guardian PVF: " + exception, false); }
            Console.WriteLine($"GUARDIAN_ALTAR_ACHIEVEMENT_PROTOCOL assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyWire(Action<string, bool> check)
        {
            var captured = Convert.FromHexString("000000000000000000000000000001000000");
            check("actual 18B achievement click has a zero prefix and one Int32 type",
                GuardianAltarAchievementRequestParser.TryParse(captured, out var type) && type == 1);
            foreach (var invalid in new byte[][] { null, Array.Empty<byte>(), captured[..17],
                captured.Concat(new byte[1]).ToArray(), Mutate(captured, 0, 1), Mutate(captured, 13, 1),
                Body(0), Body(-1) })
                check("achievement parser rejects unproven length, prefix or type",
                    !GuardianAltarAchievementRequestParser.TryParse(invalid, out _));
            check("positive Int32 type is preserved without a fabricated known-type range",
                GuardianAltarAchievementRequestParser.TryParse(Body(int.MaxValue), out var maximum) && maximum == int.MaxValue);
            check("success ACK exactly matches outer success plus result and claimed type",
                GuardianAltarAchievementAckBodyBuilder.BuildSuccess(1)
                    .SequenceEqual(Convert.FromHexString("010000000001000000")));
            check("achievement rejection has only the existing common error wrapper",
                GuardianAltarAchievementAckBodyBuilder.BuildRejected().SequenceEqual(new byte[] { 0, 4 }));
            check("invalid achievement type cannot be serialized as success",
                Throws<ArgumentOutOfRangeException>(() => GuardianAltarAchievementAckBodyBuilder.BuildSuccess(0)));
        }

        private static async Task VerifyAdmissionAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using (var fixture = new Fixture(definition, capability: false))
            {
                fixture.Open();
                check("real registry adds the achievement command to the same menu owner",
                    fixture.Registry.Count == 4 && fixture.Registry.TryGetValue(
                        (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD, out _));
                await fixture.DispatchAsync();
                check("a menu-only provider cannot claim rewards or trigger mailbox alarm",
                    fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]) && fixture.Menu.Reads == 0);
            }
            foreach (var restriction in new[] { "closed", "trade", "mercenary", "level", "presence", "combat", "selection" })
            {
                using var fixture = new Fixture(definition);
                if (restriction != "closed") fixture.Open();
                if (restriction == "trade") fixture.Trading = true;
                if (restriction == "mercenary") fixture.Mercenary.Assigned = true;
                if (restriction == "level") fixture.Session.Player.Level = 49;
                if (restriction == "presence") fixture.Session.Player.TownPresenceReady = false;
                if (restriction == "combat") fixture.Session.Player.CurrentRun = new DungeonRun();
                if (restriction == "selection") fixture.Session.Player.BeginDungeonSelection(Anchor(fixture.Session));
                var run = fixture.Session.Player.CurrentRun;
                var selection = fixture.Session.Player.CurrentDungeonSelection;
                await fixture.DispatchAsync();
                check(restriction + " admission rejects before the reward capability and preserves ordinary owners",
                    fixture.Claims.Calls == 0 && fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0])
                    && ReferenceEquals(run, fixture.Session.Player.CurrentRun)
                    && ReferenceEquals(selection, fixture.Session.Player.CurrentDungeonSelection));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.DispatchAsync(new byte[17]);
                check("registered malformed click does not call durable reward authority",
                    fixture.Claims.Calls == 0 && fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]));
                var count = fixture.Packets.Count;
                await fixture.DispatchAsync(command: 0);
                await fixture.DispatchAsync(type: (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_RESET_STAR);
                check("the retained achievement callback ignores NOTI and another opcode",
                    fixture.Packets.Count == count && fixture.Claims.Calls == 0);
            }
            foreach (var identity in new[] { "missing account", "zero account", "zero uid" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                if (identity == "missing account") fixture.Session.Account = null;
                if (identity == "zero account") fixture.Session.Account.AccountId = 0;
                if (identity == "zero uid") fixture.Session.Player.UserId = 0;
                await fixture.DispatchAsync();
                check(identity + " cannot authorize a durable claim or receive a copied owner response",
                    fixture.Claims.Calls == 0 && fixture.Packets.Count == 0);
            }
        }

        private static async Task VerifyResultsAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                await fixture.DispatchAsync();
                check("success projects the committed type before the mailbox alarm in separate send prepares",
                    fixture.Claims.Calls == 1 && fixture.Claims.Changes == 1
                    && fixture.Packets.Count == 2 && IsAccepted(fixture.Packets[0], 1)
                    && IsAlarm(fixture.Packets[1]) && fixture.Prepares == 2 && !fixture.Claims.CalledInPrepare);
                check("claim receives the current character and account without refilling menu state or starting a run",
                    fixture.Claims.LastCharacterId == fixture.Session.Player.CharacterId && fixture.Claims.LastAccountId == 11
                    && fixture.Menu.Reads == 0 && fixture.Session.Player.CurrentRun == null
                    && fixture.Session.Player.CurrentDungeonSelection == null);
                await fixture.DispatchAsync();
                check("an accepted already-committed receipt reprojects success and alarm without a second owner mutation",
                    fixture.Claims.Calls == 2 && fixture.Claims.Changes == 1 && fixture.Packets.Count == 4
                    && IsAccepted(fixture.Packets[2], 1) && IsAlarm(fixture.Packets[3]));
            }
            foreach (var result in new[] { "rejected", "null", "wrong character", "missing committed claim", "invalid snapshot", "SQLite failure" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                if (result == "SQLite failure") fixture.Claims.Error = new SqliteException("injected reward authority unavailable", 5);
                else fixture.Claims.ResultMode = result;
                await fixture.DispatchAsync();
                check(result + " cannot become a successful ACK or mailbox notification",
                    fixture.Claims.Calls == 1 && fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]));
            }
        }

        private static async Task VerifyTransportAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            foreach (var failedWrite in new[] { 0, 1 })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.FailWriteNumber = failedWrite;
                await fixture.DispatchAsync();
                check((failedWrite == 0 ? "ACK" : "mailbox alarm") + " transport failure cannot undo the owner's committed claim",
                    fixture.Claims.Changes == 1 && fixture.Packets.Count == failedWrite && fixture.SendGate.CurrentCount == 1);
                fixture.FailWriteNumber = -1;
                await fixture.DispatchAsync();
                check("recovered transport retries committed success then alarm without another delivery: " + failedWrite,
                    fixture.Claims.Changes == 1 && IsAccepted(fixture.Packets[^2], 1) && IsAlarm(fixture.Packets[^1]));
            }
            foreach (var change in new[] { "GameSession", "run", "selection", "account object" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.AfterWrite = () => ChangeSource(fixture, change);
                await fixture.DispatchAsync();
                check("replacement " + change + " after the ACK suppresses the old mailbox alarm without copying or reverting claim",
                    fixture.Claims.Changes == 1 && fixture.Packets.Count == 1 && IsAccepted(fixture.Packets[0], 1)
                    && fixture.SendGate.CurrentCount == 1);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                fixture.Coordinator.Dispose();
                await fixture.DispatchAsync();
                check("a disposed owner cannot claim or project using its registered callback",
                    fixture.Claims.Calls == 0 && fixture.Packets.Count == 0);
            }
        }

        private static async Task VerifyFencesAsync(GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            foreach (var change in new[] { "GameSession", "account", "account object", "uid", "character", "directory" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                Task pending;
                using (await fixture.Transitions.AcquireAsync(fixture.Session.Player.CharacterId))
                {
                    pending = fixture.DispatchAsync();
                    ChangeSource(fixture, change);
                }
                await pending.WaitAsync(Timeout);
                check("queued old " + change + " cannot claim or reply as the replacement owner",
                    fixture.Claims.Calls == 0 && fixture.Packets.Count == 0);
            }
            foreach (var change in new[] { "run epoch", "selection epoch", "run", "selection", "closed" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                Task pending;
                using (await fixture.Transitions.AcquireAsync(fixture.Session.Player.CharacterId))
                {
                    pending = fixture.DispatchAsync();
                    ChangeSource(fixture, change);
                }
                await pending.WaitAsync(Timeout);
                check("queued " + change + " invalidates claim admission before the owner runs",
                    fixture.Claims.Calls == 0 && fixture.Claims.Changes == 0
                    && fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]));
            }
            foreach (var change in new[] { "run epoch", "selection epoch", "trade", "closed" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.Claims.DuringClaim = () => ChangeSource(fixture, change);
                await fixture.DispatchAsync();
                check("the final durable owner fence rejects " + change + " before mutation",
                    fixture.Claims.Calls == 1 && fixture.Claims.Changes == 0
                    && fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]));
            }
            foreach (var change in new[] { "GameSession", "run epoch", "selection epoch", "trade" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                fixture.BeforePrepare = () => ChangeSource(fixture, change);
                await fixture.DispatchAsync();
                check("send-lock source change " + change + " cannot project successful claim into a new scope",
                    fixture.Claims.Changes == 1 && (change == "GameSession" ? fixture.Packets.Count == 0
                        : fixture.Packets.Count == 1 && IsRejected(fixture.Packets[0]))
                    && fixture.SendGate.CurrentCount == 1);
            }
        }

        private static void ChangeSource(Fixture fixture, string change)
        {
            var session = fixture.Session;
            switch (change)
            {
                case "GameSession": session.GameSession = GameSessionIdentity(); break;
                case "account": session.Account.AccountId++; break;
                case "account object": session.Account = new AccountRecord { AccountId = 11 }; break;
                case "uid": session.Player.UserId++; break;
                case "character": session.Player.CharacterId++; break;
                case "directory": fixture.ReplaceDirectorySession(); break;
                case "run epoch": session.Player.NextDungeonRunGeneration(); break;
                case "run": session.Player.CurrentRun = new DungeonRun(); break;
                case "selection": session.Player.BeginDungeonSelection(Anchor(session)); break;
                case "selection epoch":
                    var selection = session.Player.BeginDungeonSelection(Anchor(session));
                    session.Player.TryInvalidateDungeonSelection(selection);
                    break;
                case "trade": fixture.Trading = true; break;
                case "closed": fixture.Service.Close(); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        }

        private class MenuStub : IGuardianAltarProgressService
        {
            protected readonly GuardianAltarMenuDefinition Definition;
            internal int Reads;
            internal MenuStub(GuardianAltarMenuDefinition definition) => Definition = definition;
            public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow, Func<bool> isCurrent = null)
            { Reads++; return Snapshot(characterId, Array.Empty<GuardianAltarAchievement>()); }
            protected GuardianAltarProgressSnapshot Snapshot(int characterId, IEnumerable<GuardianAltarAchievement> achievements)
                => new(characterId, new GuardianAltarHeader(Definition.RidableId, Definition.DailyRefillTicket,
                        0, 0, 0, 0, 0, 0, 0, 0), new[] { new GuardianAltarStage(Definition.FirstDungeonId, -1) },
                    Definition.InitialPurchases, Definition.InitialSlots, achievements);
        }

        // Protocol probe only: transactional eligibility and actual durable
        // system-mail exactly-once checks belong to the domain SelfTest.
        private sealed class ClaimStub : MenuStub, IGuardianAltarAchievementService
        {
            private readonly HashSet<(int CharacterId, int Type)> _committed = new();
            internal int Calls;
            internal int Changes;
            internal int LastCharacterId;
            internal int LastAccountId;
            internal bool CalledInPrepare;
            internal Func<bool> InPrepare;
            internal Action DuringClaim;
            internal Exception Error;
            internal string ResultMode;
            internal ClaimStub(GuardianAltarMenuDefinition definition) : base(definition) { }
            public GuardianAltarAchievementResult ClaimAchievement(int characterId, int accountId,
                int achievementType, Func<bool> isCurrent = null)
            {
                Calls++;
                LastCharacterId = characterId;
                LastAccountId = accountId;
                CalledInPrepare |= InPrepare?.Invoke() == true;
                if (isCurrent != null && !isCurrent()) throw new InvalidOperationException("old claim owner");
                DuringClaim?.Invoke();
                if (isCurrent != null && !isCurrent()) throw new InvalidOperationException("claim source expired before commit");
                if (Error != null) throw Error;
                if (ResultMode == "null") return null;
                var accepted = ResultMode != "rejected";
                var changed = accepted && _committed.Add((characterId, achievementType));
                if (changed) Changes++;
                var claims = accepted && ResultMode != "missing committed claim"
                    ? new[] { new GuardianAltarAchievement(achievementType, ResultMode == "invalid snapshot" ? (byte)2 : (byte)1) }
                    : Array.Empty<GuardianAltarAchievement>();
                return new GuardianAltarAchievementResult(accepted, changed,
                    Snapshot(characterId + (ResultMode == "wrong character" ? 1 : 0), claims),
                    accepted ? null : "fixture domain rejection");
            }
        }

        private sealed class MercenaryStub : IMercenaryRestrictionService
        {
            internal bool Assigned;
            public bool IsAssigned(int characterId) => Assigned;
            public bool CanDelete(int characterId) => !Assigned;
            public bool CanMutateAppearance(int characterId) => !Assigned;
            public bool CanEnterContent(int characterId) => !Assigned;
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly SemaphoreSlim SendGate = new(1, 1);
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly GuardianAltarOpeningService Service = new();
            internal readonly GuardianAltarOpeningCoordinator Coordinator;
            internal readonly GameCommandRegistry Registry = new();
            internal readonly MercenaryStub Mercenary = new();
            internal readonly ClaimStub Claims;
            internal readonly MenuStub Menu;
            internal readonly EnhancedClientSession Session;
            internal readonly List<byte[]> Packets = new();
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            private readonly DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
            private bool _preparing;
            private int _writeNumber;
            internal int Prepares;
            internal int FailWriteNumber = -1;
            internal bool Trading;
            internal Action BeforePrepare;
            internal Action AfterWrite;
            internal Fixture(GuardianAltarMenuDefinition definition, bool capability = true)
            {
                Claims = new ClaimStub(definition) { InPrepare = () => _preparing };
                Menu = capability ? Claims : new MenuStub(definition);
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Coordinator = new GuardianAltarOpeningCoordinator(Service, Sessions, Transitions,
                    clock: null, utcNow: () => _now, sendBatch: SendBatchAsync, progress: Menu);
                Registry.RegisterGroup("guardian-achievement-protocol", group =>
                    Coordinator.RegisterEntryHandlers(group, _ => Trading, Mercenary));
                Session = CreateSession();
            }
            internal void Open() => Service.SetWindow(_now, _now.AddMinutes(5));
            private EnhancedClientSession CreateSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 11 };
                session.Player.CharacterId = 71000102;
                session.Player.UserId = 12001;
                session.Player.Level = 70;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.Player.CurTownId = 1;
                session.Player.CurAreaId = 2;
                session.GameSession = GameSessionIdentity();
                _ownedSessions.Add(session);
                Sessions.Register(session.Player.CharacterId, session);
                return session;
            }
            internal void ReplaceDirectorySession() => CreateSession();
            internal Task DispatchAsync(byte[] body = null, byte command = 1, ushort? type = null)
            {
                if (!Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD, out var handler))
                    throw new InvalidOperationException("achievement handler not registered");
                body ??= Body(1);
                return handler(Session, new GamePacketHeader { cmd = command,
                    type = type ?? (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD,
                    length = checked((uint)(14 + body.Length)) }, body).WaitAsync(Timeout);
            }
            private Task SendBatchAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
                => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(SendGate, () =>
                {
                    var before = BeforePrepare; BeforePrepare = null; before?.Invoke();
                    Prepares++;
                    _preparing = true;
                    try { return prepare(); }
                    finally { _preparing = false; }
                }, packet =>
                {
                    if (_writeNumber++ == FailWriteNumber) throw new IOException("injected achievement transport failure");
                    Packets.Add(packet.ToArray());
                    var after = AfterWrite; AfterWrite = null; after?.Invoke();
                    return Task.CompletedTask;
                });
            public void Dispose()
            {
                Coordinator.Dispose();
                SendGate.Dispose();
                foreach (var session in _ownedSessions) session.Close();
            }
        }

        private static byte[] Body(int type)
        {
            var body = new byte[18];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14), type);
            return body;
        }
        private static byte[] Mutate(byte[] source, int offset, byte value)
        { var copy = source.ToArray(); copy[offset] = value; return copy; }
        private static bool IsAccepted(byte[] packet, int type) => packet.Length == 24 && packet[0] == 1
            && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD
            && packet.AsSpan(15).SequenceEqual(GuardianAltarAchievementAckBodyBuilder.BuildSuccess(type));
        private static bool IsRejected(byte[] packet) => packet.Length == 17 && packet[0] == 1
            && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD
            && packet.AsSpan(15).SequenceEqual(GuardianAltarAchievementAckBodyBuilder.BuildRejected());
        private static bool IsAlarm(byte[] packet) => packet[0] == 0
            && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.MAILBOX_ALARM
            && packet.AsSpan(15).SequenceEqual(MailboxHandler.BuildMailboxAlarmNotification(1));
        private static DungeonTownReturnAnchor Anchor(EnhancedClientSession session)
            => new(session.Player.CurTownId, session.Player.CurAreaId, 0, 0, 5, 0);
        private static GameSession GameSessionIdentity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        private static bool Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return true; } return false; }
    }
}
