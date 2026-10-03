using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // Covers the real Town owner and socket writes. Menu receipt admission,
    // duplicate commands and command registration belong to MenuReturnSelfTest.
    internal static class GuardianAltarMenuTownProjectionSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private const int HeaderLength = 15;

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_MENU_TOWN_PROJECTION selftest ===");
            var assertions = 0;
            var failures = 0;
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
            Section("real town projection", () => VerifyTownProjectionAsync(Check));
            Section("town loading response", () => VerifyFinishLoadingAsync(Check));
            Section("stale and queued scope", () => VerifyScopeAsync(Check));
            Section("account write scope", () => VerifyAccountWriteScopeAsync(Check));
            Section("missing account character and retry", () => VerifyMissingCharacterAsync(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_MENU_TOWN_PROJECTION assertions={assertions} failures={failures}");
            return failures;
        }

        private static async Task VerifyTownProjectionAsync(Action<string, bool> check)
        {
            foreach (var level in new[] { (byte)ExpTableProvider.MaxLevel, (byte)(ExpTableProvider.MaxLevel - 1) })
            {
                using var fixture = new Fixture(level);
                var player = fixture.Session.Player;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
                var packets = await fixture.DrainAsync();
                var expectedTypes = TownTypes(includeAccount: true,
                    includeHonor: level == ExpTableProvider.MaxLevel);
                check($"level {level} projects the existing town and account sequence through a real socket",
                    completed && packets.Select(Type).SequenceEqual(expectedTypes)
                    && packets.All(packet => packet[0] == 0 && BitConverter.ToInt32(packet, 3) == packet.Length));
                VerifyTownBodies(packets, fixture, check);
                check($"level {level} keeps its actual town owner and never creates a run or selection",
                    player.CurrentRun == null && player.CurrentDungeonSelection == null
                    && !player.DungeonSelectionPending && player.TownPresenceReady && player.UserState == 0
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && fixture.Records.GetById(player.CharacterId)?.TownId == player.CurTownId
                    && fixture.GuardianRows() == 0 && fixture.AccountHonorExp() == 0);
                check($"level {level} releases the real send lock and retains the roster publisher",
                    fixture.SendGate.CurrentCount == 1 && fixture.PartyPublicationCount == 1);
                if (level == ExpTableProvider.MaxLevel)
                {
                    var honor = packets.Single(packet => Type(packet) == (ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO);
                    check("max-level account honor has the established two-u32 body and zero fixture experience",
                        honor.Length == HeaderLength + 8 && BitConverter.ToUInt32(honor, HeaderLength + 4) == 0);
                }
                else
                {
                    check("a level below the current honor threshold preserves the original no-banner rule",
                        !packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO));
                }
            }
        }

        private static void VerifyTownBodies(IReadOnlyList<byte[]> packets, Fixture fixture,
            Action<string, bool> check)
        {
            if (packets.Count < 4)
            {
                check("complete town body sequence is available", false);
                return;
            }
            var player = fixture.Session.Player;
            var state = packets[0];
            check("USER_STATE carries exactly the current UID and idle town state",
                Type(state) == (ushort)NotiPacketTypeA21.USER_STATE && state.Length == HeaderLength + 4
                && state[HeaderLength] == 1 && BitConverter.ToUInt16(state, HeaderLength + 1) == player.UserId
                && state[HeaderLength + 3] == 0);
            var area = packets[1];
            check("USER_AREA carries the unchanged town anchor with the normal arrival state",
                Type(area) == (ushort)NotiPacketTypeA21.USER_AREA && area.Length == HeaderLength + 10
                && BitConverter.ToUInt16(area, HeaderLength) == player.UserId
                && area[HeaderLength + 2] == player.CurTownId && area[HeaderLength + 3] == player.CurAreaId
                && BitConverter.ToInt16(area, HeaderLength + 4) == player.CurPosX
                && BitConverter.ToInt16(area, HeaderLength + 6) == player.CurPosY
                && area[HeaderLength + 9] == 0);
            var roster = packets[2];
            check("AREA_USERS contains the current avatar as the only roster entry",
                Type(roster) == (ushort)NotiPacketTypeA21.AREA_USERS && roster.Length == HeaderLength + 12
                && roster[HeaderLength] == player.CurTownId && roster[HeaderLength + 1] == player.CurAreaId
                && BitConverter.ToUInt16(roster, HeaderLength + 2) == 1
                && BitConverter.ToUInt16(roster, HeaderLength + 4) == player.UserId);
            var appearance = packets[3];
            check("the roster is followed by the actual self USERINFO subtype0",
                Type(appearance) == (ushort)NotiPacketTypeA21.USERINFO && appearance.Length > HeaderLength + 43
                && appearance[HeaderLength] == 0 && BitConverter.ToUInt16(appearance, HeaderLength + 1) == 1
                && BitConverter.ToUInt16(appearance, HeaderLength + 41) == player.UserId);
            if (packets.Count > 4 && Type(packets[4]) == (ushort)NotiPacketTypeA21.USERINFO)
                check("account subtype0 preserves the same live character identity",
                    packets[4].Length > HeaderLength + 43 && packets[4][HeaderLength] == 0
                    && BitConverter.ToUInt16(packets[4], HeaderLength + 41) == player.UserId);
        }

        private static async Task VerifyFinishLoadingAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture((byte)(ExpTableProvider.MaxLevel - 1));
            check("real menu town projection completes before its normal loading request",
                await fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session,
                    fixture.CaptureFence()).WaitAsync(Timeout));
            await fixture.DrainAsync();
            await fixture.Town.Handle_ENUM_CMDPACKET_FINISH_LOADING(fixture.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.FINISH_LOADING, length = 14 },
                Array.Empty<byte>()).WaitAsync(Timeout);
            var packets = await fixture.DrainAsync();
            check("runless FINISH_LOADING retains its command ACK followed by the existing completion notification",
                packets.Count == 2 && packets[0][0] == 1
                && Type(packets[0]) == (ushort)CmdPacketTypeA21.FINISH_LOADING
                && packets[0].Length == HeaderLength + 1 && packets[0][HeaderLength] == 1
                && packets[1][0] == 0 && Type(packets[1]) == (ushort)NotiPacketTypeA21.FINISH_LOADING);
            check("loading completion neither creates a run nor consumes Guardian progression",
                fixture.Session.Player.CurrentRun == null && fixture.Session.Player.CurrentDungeonSelection == null
                && fixture.GuardianRows() == 0 && fixture.SendGate.CurrentCount == 1);
        }

        private static async Task VerifyScopeAsync(Action<string, bool> check)
        {
            using (var fixture = new Fixture())
            {
                var oldFence = fixture.CaptureFence();
                fixture.Session.Player.NextDungeonRunGeneration();
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, oldFence).WaitAsync(Timeout);
                check("an already stale town scope projects no frames and invokes no roster follow-up",
                    !completed && (await fixture.DrainAsync()).Count == 0
                    && fixture.PartyPublicationCount == 0 && fixture.GuardianRows() == 0);
            }
            using (var fixture = new Fixture())
            {
                var oldFence = fixture.CaptureFence();
                await fixture.SendGate.WaitAsync();
                Task<bool> pending;
                try
                {
                    pending = fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, oldFence);
                    check("the real first town write waits on the session send lock", !pending.IsCompleted);
                    fixture.Session.Player.NextDungeonRunGeneration();
                }
                finally { fixture.SendGate.Release(); }
                var completed = await pending.WaitAsync(Timeout);
                check("scope expiry while queued suppresses the old write and returns an incomplete projection",
                    !completed && (await fixture.DrainAsync()).Count == 0
                    && fixture.PartyPublicationCount == 0 && fixture.SendGate.CurrentCount == 1);
            }
        }

        private static async Task VerifyAccountWriteScopeAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendGateHeld = false;
            fixture.Town.ConfigureTownPartyListPublisher(async () =>
            {
                // Existing roster hook: pause the later account write on the
                // actual send lock after the four town frames have been sent.
                await fixture.SendGate.WaitAsync();
                sendGateHeld = true;
                reached.TrySetResult(true);
            });
            var oldFence = fixture.CaptureFence();
            var pending = fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, oldFence);
            try
            {
                await reached.Task.WaitAsync(Timeout);
                check("account subtype0 waits after complete real town projection", !pending.IsCompleted);
                fixture.Session.Player.NextDungeonRunGeneration();
            }
            finally
            {
                if (sendGateHeld)
                    fixture.SendGate.Release();
            }
            var completed = await pending.WaitAsync(Timeout);
            var packets = await fixture.DrainAsync();
            check("scope expiry while account write is queued suppresses subtype0 and honor and cannot mark completion",
                !completed && packets.Select(Type).SequenceEqual(TownTypes(includeAccount: false, includeHonor: false))
                && fixture.SendGate.CurrentCount == 1 && fixture.GuardianRows() == 0);
        }

        private static async Task VerifyMissingCharacterAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Town.ConfigureTownPartyListPublisher(() =>
            {
                fixture.Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM characters WHERE character_id=@id;";
                    command.Parameters.AddWithValue("@id", fixture.Record.CharacterId);
                    command.ExecuteNonQuery();
                });
                return Task.CompletedTask;
            });
            var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var partial = await fixture.DrainAsync();
            check("missing durable character makes account projection fail after the existing town frames",
                !completed && partial.Select(Type).SequenceEqual(TownTypes(includeAccount: false, includeHonor: false))
                && fixture.Records.GetById(fixture.Record.CharacterId) == null);
            fixture.Records.Create(fixture.Record);
            fixture.Town.ConfigureTownPartyListPublisher(() => Task.CompletedTask);
            completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var retry = await fixture.DrainAsync();
            check("restoring a legitimate fixture record permits a complete retry without a fabricated run or selection",
                completed && retry.Select(Type).SequenceEqual(TownTypes(includeAccount: true, includeHonor: true))
                && fixture.Session.Player.CurrentRun == null && fixture.Session.Player.CurrentDungeonSelection == null
                && fixture.GuardianRows() == 0 && fixture.AccountHonorExp() == 0);
        }

        private static ushort[] TownTypes(bool includeAccount, bool includeHonor)
        {
            var types = new List<ushort>
            {
                (ushort)NotiPacketTypeA21.USER_STATE,
                (ushort)NotiPacketTypeA21.USER_AREA,
                (ushort)NotiPacketTypeA21.AREA_USERS,
                (ushort)NotiPacketTypeA21.USERINFO
            };
            if (includeAccount) types.Add((ushort)NotiPacketTypeA21.USERINFO);
            if (includeHonor) types.Add((ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO);
            return types.ToArray();
        }

        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "guardian-menu-town-" + Guid.NewGuid().ToString("N"));
            private readonly TcpClient _peer = new();
            internal readonly SessionDirectory Sessions = new((_, _) => true);
            internal GameDatabase Database { get; }
            internal SqliteCharacterRepository Records { get; }
            internal CharacterRecord Record { get; }
            internal EnhancedClientSession Session { get; }
            internal TownHandler Town { get; }
            internal SemaphoreSlim SendGate { get; }
            internal int PartyPublicationCount { get; private set; }

            internal Fixture(byte level = (byte)ExpTableProvider.MaxLevel)
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archive);
                Directory.CreateDirectory(_directory);
                TcpClient accepted = null;
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    Database = new GameDatabase(Path.Combine(_directory, "inventory.db"), ServerPaths.SchemaFilePath);
                    Database.Write((connection, transaction) =>
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(11,'guardian-menu-town-test','');";
                        command.ExecuteNonQuery();
                    });
                    Records = new SqliteCharacterRepository(Database);
                    var spawn = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 0);
                    Record = new CharacterRecord
                    {
                        CharacterId = 62021, AccountId = 11, Name = Encoding.ASCII.GetBytes("guardian-town"),
                        Level = level, Appearance = Array.Empty<CharacterAppearanceEntry>()
                    };
                    spawn.ApplyTo(Record);
                    Record.AreaState = 0;
                    Records.Create(Record);
                    listener.Start();
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    var connecting = _peer.ConnectAsync(endpoint.Address, endpoint.Port);
                    accepted = listener.AcceptTcpClientAsync().WaitAsync(Timeout).GetAwaiter().GetResult();
                    connecting.WaitAsync(Timeout).GetAwaiter().GetResult();
                    accepted.NoDelay = true;
                    _peer.NoDelay = true;
                    Session = new EnhancedClientSession(accepted, new GamePacketHeader(), GameNetworkConfig.NormalGamePort)
                    {
                        Account = new AccountRecord { AccountId = Record.AccountId }
                    };
                    var player = Session.Player;
                    player.CharacterId = Record.CharacterId;
                    player.UserId = (ushort)Record.CharacterId;
                    player.Name = Record.Name;
                    player.Level = Record.Level;
                    player.Job = Record.Job;
                    player.GrowType = Record.GrowType;
                    player.UserState = 0;
                    player.TownPresenceReady = true;
                    player.CurTownId = Record.TownId;
                    player.CurAreaId = Record.AreaId;
                    player.CurPosX = Record.PosX;
                    player.CurPosY = Record.PosY;
                    player.CurDirection = Record.Direction;
                    player.CurAreaState = Record.AreaState;
                    player.AppearanceEntries = Record.Appearance;
                    player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                    Session.GameSession = new GameSession(Session, Database, Records, sessionDirectory: Sessions);
                    Sessions.Register(player.CharacterId, Session);
                    SendGate = (SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock",
                        BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Session)
                        ?? throw new InvalidOperationException("The real session send lock was not found."));
                    Town = new TownHandler(Records, selectDataSource: null, partyManager: null, sessions: Sessions,
                        refresh: null, dungeonInstances: null, raidManager: null, database: Database);
                    Town.ConfigureTownPartyListPublisher(() =>
                    {
                        PartyPublicationCount++;
                        return Task.CompletedTask;
                    });
                }
                catch
                {
                    accepted?.Close();
                    _peer.Dispose();
                    DeleteDatabase();
                    throw;
                }
                finally { listener.Stop(); }
            }

            internal Func<bool> CaptureFence()
            {
                var player = Session.Player;
                var generation = Session.GameSession;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                return () => Sessions.TryGet(Record.CharacterId, out var current)
                    && ReferenceEquals(current, Session) && ReferenceEquals(Session.GameSession, generation)
                    && SessionDirectory.IsTownPresence(player) && player.CurrentDungeonSelection == null
                    && !player.DungeonSelectionPending && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration;
            }

            internal async Task<List<byte[]>> DrainAsync()
            {
                var packets = new List<byte[]>();
                var stream = _peer.GetStream();
                using var cancellation = new CancellationTokenSource(Timeout);
                while (_peer.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_peer.Available == 0)
                        throw new IOException("The town projection unexpectedly closed its real connection.");
                    var header = new byte[HeaderLength];
                    await stream.ReadExactlyAsync(header, cancellation.Token);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < HeaderLength || length > 65536)
                        throw new InvalidDataException("Invalid A21 town envelope length.");
                    var packet = new byte[length];
                    header.CopyTo(packet, 0);
                    await stream.ReadExactlyAsync(packet.AsMemory(HeaderLength), cancellation.Token);
                    packets.Add(packet);
                }
                return packets;
            }

            internal long GuardianRows() => Scalar("SELECT COUNT(*) FROM character_guardian_altar;");
            internal long AccountHonorExp() => Scalar("SELECT honor_exp FROM accounts WHERE account_id=11;");
            private long Scalar(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });

            public void Dispose()
            {
                Session?.Close();
                _peer.Dispose();
                DeleteDatabase();
            }

            private void DeleteDatabase()
            {
                SqliteConnection.ClearAllPools();
                var path = Path.Combine(_directory, "inventory.db");
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
        }
    }
}
