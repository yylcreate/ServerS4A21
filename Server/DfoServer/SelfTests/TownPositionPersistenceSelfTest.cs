using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class TownPositionPersistenceSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private const int HeaderLength = 15;

        internal static int Run()
        {
            Console.WriteLine("=== TOWN_POSITION_PERSISTENCE selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                if (!passed) failures++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("typed village-save configuration", () => VerifyConfig(Check));
            Section("persistent town policy", () => VerifyPolicy(Check));
            Section("real town projection and repository", () => VerifyTownAsync(Check).GetAwaiter().GetResult());
            Section("safe ordinary login gates", () => VerifyRecovery(Check));
            Section("real character selection rejects unresolved login spawn", () => VerifyRejectedSelectionAsync(Check).GetAwaiter().GetResult());
            Console.WriteLine($"TOWN_POSITION_PERSISTENCE assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private const string CurrentConfig = "[ignore village save]\n2 65\n2 67 2 68\n102 0\n103 0\n[/ignore village save]";

        private static void VerifyConfig(Action<string, bool> check)
        {
            var config = VillageSaveConfigFile.Parse(CurrentConfig);
            check("the typed model preserves all five town-area pairs across data lines",
                config.IsValid && config.ExcludedAreas.Count == 5
                && config.ExcludedAreas.Select(area => ((int)area.TownId, (int)area.AreaId))
                    .SequenceEqual(new[] { (2, 65), (2, 67), (2, 68), (102, 0), (103, 0) }));
            check("one excluded area cannot exclude its town's ordinary gate or adjacent areas",
                config.IsExcluded(2, 65) && config.IsExcluded(2, 67) && config.IsExcluded(2, 68)
                && !config.IsExcluded(2, 0) && !config.IsExcluded(2, 1) && !config.IsExcluded(2, 66));
            var extended = VillageSaveConfigFile.Parse(CurrentConfig.Replace("[/ignore village save]", "28 0\n28 1\n[/ignore village save]"));
            check("adding event-area pairs in configuration alone changes the typed decision without a town-ID special case",
                extended.IsValid && extended.ExcludedAreas.Count == 7 && extended.IsExcluded(28, 0)
                && extended.IsExcluded(28, 1) && !extended.IsExcluded(28, 2) && !config.IsExcluded(28, 0));
            var unrelatedCaseDifference = VillageSaveConfigFile.Parse(
                "[fatigue event notice]\n1\n[/fatigue event Notice]\n" + CurrentConfig);
            check("an unrelated legacy field's end-tag case difference cannot invalidate the well-formed save table",
                unrelatedCaseDifference.IsValid && unrelatedCaseDifference.ExcludedAreas.Count == 5
                && unrelatedCaseDifference.IsExcluded(2, 65) && unrelatedCaseDifference.IsExcluded(103, 0));
            foreach (var (label, input) in new[]
            {
                ("missing optional table", "[other]\n1\n"),
                ("empty optional table", "[ignore village save]\n[/ignore village save]")
            })
            {
                var empty = VillageSaveConfigFile.Parse(input);
                check(label + " is a valid definition that excludes no areas",
                    empty.IsValid && empty.ExcludedAreas.Count == 0 && !empty.IsExcluded(2, 65));
            }
            foreach (var (label, input) in new[]
            {
                ("empty file", ""),
                ("duplicated table", CurrentConfig + "\n" + CurrentConfig),
                ("duplicated pair", "[ignore village save]\n2 65\n2 65\n[/ignore village save]"),
                ("incomplete final pair", "[ignore village save]\n2 65 28\n[/ignore village save]"),
                ("nonnumeric token", "[ignore village save]\n2 bad\n[/ignore village save]"),
                ("quoted token", "[ignore village save]\n2 `65`\n[/ignore village save]"),
                ("nested table", "[ignore village save]\n2 65\n[nested]\n2 67\n[/nested]\n[/ignore village save]"),
                ("unclosed table", "[ignore village save]\n2 65"),
                ("additional table end", CurrentConfig + "\n[/ignore village save]"),
                ("table nested in another section", "[outer]\n" + CurrentConfig + "\n[/outer]"),
                ("isolated table end", "[/ignore village save]"),
                ("malformed table start", "[ignore village save\n2 65\n[/ignore village save]"),
                ("malformed table end", "[ignore village save]\n2 65\n[/ignore village save"),
                ("town outside the wire range", "[ignore village save]\n256 0\n[/ignore village save]"),
                ("area outside the wire range", "[ignore village save]\n2 256\n[/ignore village save]"),
                ("negative town", "[ignore village save]\n-1 0\n[/ignore village save]"),
                ("negative area", "[ignore village save]\n2 -1\n[/ignore village save]"),
                ("invalid row after a valid prefix", "[ignore village save]\n2 65\n102 bad\n[/ignore village save]")
            })
            {
                var invalid = VillageSaveConfigFile.Parse(input);
                check(label + " rejects the whole definition without exposing a valid partial prefix",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && invalid.ExcludedAreas.Count == 0 && !invalid.IsExcluded(2, 65));
            }
        }

        private static void VerifyPolicy(Action<string, bool> check)
        {
            var raw = LoadCurrentConfig();
            check("the actual ETC retains the five baseline exclusions and permits additional configured pairs",
                raw.IsValid && raw.ExcludedAreas.Count >= 5
                && new[] { (2, 65), (2, 67), (2, 68), (102, 0), (103, 0) }.All(pair =>
                    raw.IsExcluded(pair.Item1, pair.Item2)
                    && Town.IsPositionSaveExcluded(pair.Item1, pair.Item2)
                    && !GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, pair.Item1, pair.Item2)));
            check("other areas in town2 are still allowed to save",
                GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 2, 0)
                && GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 2, 1)
                && GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 2, 66));
            check("ordinary town11 still persists on a normal channel",
                GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 11, 0));
            check("town28 area0 and area1 saving follows the current PVF exclusions, including operator additions",
                Town.IsPositionSaveExcluded(28, 0) == raw.IsExcluded(28, 0)
                && Town.IsPositionSaveExcluded(28, 1) == raw.IsExcluded(28, 1)
                && GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 28, 0) == !raw.IsExcluded(28, 0)
                && GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 28, 1) == !raw.IsExcluded(28, 1));
            check("unrelated town29 retains its existing persistence rule",
                GameChannelSpawnPolicy.ShouldPersistPosition(GameNetworkConfig.NormalGamePort, 29, 0));
            foreach (var port in new[] { GameNetworkConfig.FreeDuelGamePort, GameNetworkConfig.Channel100GamePort, GameNetworkConfig.RaidGamePort })
            {
                check($"special listener {port} remains transient for ordinary and excluded destinations",
                    !GameChannelSpawnPolicy.ShouldPersistPosition(port, 11, 0)
                    && !GameChannelSpawnPolicy.ShouldPersistPosition(port, 2, 65));
            }
        }

        private static async Task VerifyTownAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var player = fixture.Session.Player;
            var before = fixture.Records.GetById(player.CharacterId);
            var oldPersistTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            player.LastPositionPersistAt = oldPersistTime;

            // Start in a resource-excluded area; admission and client scene
            // rendering are outside this position-saving test's scope.
            player.CurTownId = 2;
            player.CurAreaId = 65;
            await fixture.AreaAsync(2, 65, 441, 177);
            var packets = await fixture.DrainAsync();
            check("excluded area2:65 updates and sends the live scene without replacing the saved normal town",
                player.CurTownId == 2 && player.CurAreaId == 65 && player.CurPosX == 441 && player.CurPosY == 177
                && packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_AREA)
                && SamePosition(before, fixture.Reload()));
            check("skipping an excluded-area arrival does not advance the position-save throttle", player.LastPositionPersistAt == oldPersistTime);

            await fixture.AreaAsync(2, 67, 474, 234);
            await fixture.DrainAsync();
            check("excluded area2:67 also preserves every saved position field",
                player.CurAreaId == 67 && SamePosition(before, fixture.Reload()));
            await fixture.AreaAsync(2, 68, 475, 235);
            await fixture.DrainAsync();
            check("excluded area2:68 remains controlled by the same current ETC definition",
                player.CurAreaId == 68 && SamePosition(before, fixture.Reload()));

            await fixture.PositionAsync(600, 250, 3);
            packets = await fixture.DrainAsync();
            check("excluded-area movement remains projected while x y and direction stay out of storage",
                player.CurPosX == 600 && player.CurPosY == 250 && player.CurDirection == 3
                && packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_POSITION)
                && SamePosition(before, fixture.Reload()) && player.LastPositionPersistAt == oldPersistTime);

            // These are the common saving calls used by lifecycle teardown;
            // this is not a complete disconnect or character-select exchange.
            foreach (var source in new[] { "disconnect", "return_select", "select_character", "select-displaced", "select-rollback" })
            {
                fixture.Town.PersistPosition(fixture.Session, forceImmediate: true, source);
                check(source + " forced save still preserves the prior normal-town record and throttle",
                    SamePosition(before, fixture.Reload()) && player.LastPositionPersistAt == oldPersistTime);
            }

            DungeonRunLifecycle.ApplyTownReturnAnchor(player,
                new DungeonTownReturnAnchor(2, 65, 441, 177, 5, 3), fixture.Session.ListenerPort);
            fixture.Town.PersistPosition(fixture.Session, forceImmediate: true, source: "excluded-dungeon-return");
            check("the existing excluded dungeon-return anchor stays live without becoming a login position",
                player.CurTownId == 2 && player.CurAreaId == 65 && player.CurAreaState == 0
                && SamePosition(before, fixture.Reload()) && player.LastPositionPersistAt == oldPersistTime);

            await fixture.AreaAsync(11, 0, 700, 260);
            await fixture.DrainAsync();
            check("leaving for an ordinary town resumes immediate saving of the new complete position",
                MatchesPlayer(fixture.Reload(), player) && player.CurTownId == 11 && player.CurPosX == 700
                && player.LastPositionPersistAt > oldPersistTime);

            player.LastPositionPersistAt = oldPersistTime;
            await fixture.PositionAsync(710, 261, 4);
            await fixture.DrainAsync();
            check("ordinary town movement still writes through the same repository",
                MatchesPlayer(fixture.Reload(), player) && player.CurPosX == 710 && player.CurDirection == 4);
        }

        private static void VerifyRecovery(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var expected = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, GameChannelSpawnPolicy.NormalTownFallbackId);
            var spawn = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 2);
            check("town2's excluded subareas cannot discard its legitimate ordinary login gate",
                !spawn.IsTransient && spawn.TownId == 2 && !Town.IsPositionSaveExcluded(spawn.TownId, spawn.AreaId));
            var epicSpawn = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 28);
            var raw = LoadCurrentConfig();
            var epicGate = Town.GetCeraRoomInfo(28);
            var excludedEpicGate = raw.IsExcluded(epicGate.Town, epicGate.Area);
            check("town28 login follows the configured native-gate exclusion: excluded gates use the normal fallback",
                !epicSpawn.IsTransient && (excludedEpicGate
                    ? SameSpawn(expected, epicSpawn)
                    : epicSpawn.TownId == epicGate.Town && epicSpawn.AreaId == epicGate.Area
                        && epicSpawn.X == epicGate.X && epicSpawn.Y == epicGate.Y));
            check("the existing normal fallback gate is allowed by the current resource table",
                !Town.IsPositionSaveExcluded(expected.TownId, expected.AreaId));
            check("a normal saved town retains its ordinary login gate",
                GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 11).TownId == 11);

            // Exercise the same production Resolve branch with standalone
            // parsed configurations; no PVF mutation or global override occurs.
            var extended = VillageSaveConfigFile.Parse(CurrentConfig.Replace("[/ignore village save]", "28 0\n28 1\n[/ignore village save]"));
            var configuredRecovery = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 28, extended.IsExcluded);
            check("synthetic resource rows28:0 and28:1 send the native event gate through the existing fallback",
                extended.IsValid && epicGate.Town == 28 && epicGate.Area == 1 && SameSpawn(expected, configuredRecovery));
            var areaZeroOnly = VillageSaveConfigFile.Parse("[ignore village save]\n28 0\n[/ignore village save]");
            var unchangedGate = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 28, areaZeroOnly.IsExcluded);
            check("excluding only28:0 does not exclude the different native gate28:1",
                areaZeroOnly.IsValid && unchangedGate.TownId == epicGate.Town && unchangedGate.AreaId == epicGate.Area
                && unchangedGate.X == epicGate.X && unchangedGate.Y == epicGate.Y);
            var fallbackGate = Town.GetCeraRoomInfo(GameChannelSpawnPolicy.NormalTownFallbackId);
            var noSafeGate = VillageSaveConfigFile.Parse(CurrentConfig.Replace("[/ignore village save]",
                $"28 0\n28 1\n{fallbackGate.Town} {fallbackGate.Area}\n[/ignore village save]"));
            var rejectedUnsafeFallback = false;
            try { GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 28, noSafeGate.IsExcluded); }
            catch (InvalidOperationException) { rejectedUnsafeFallback = true; }
            check("an excluded fallback gate is rejected instead of returning another forbidden login destination",
                noSafeGate.IsValid && rejectedUnsafeFallback);

            foreach (var port in new[] { GameNetworkConfig.FreeDuelGamePort, GameNetworkConfig.Channel100GamePort, GameNetworkConfig.RaidGamePort })
            {
                var ordinarySource = GameChannelSpawnPolicy.Resolve(port, 11);
                var excludedSource = GameChannelSpawnPolicy.Resolve(port, 102);
                check($"listener {port} keeps its own transient spawn ahead of ignored-area resolution",
                    ordinarySource.IsTransient && excludedSource.IsTransient && SameSpawn(ordinarySource, excludedSource));
                var predicateCalled = false;
                var transient = GameChannelSpawnPolicy.Resolve(port, 28, (_, _) =>
                {
                    predicateCalled = true;
                    throw new InvalidOperationException("A special channel must not consult ordinary-town exclusions.");
                });
                check($"listener {port} returns its own spawn without invoking the ordinary exclusion predicate",
                    !predicateCalled && SameSpawn(ordinarySource, transient));
            }

            // Seed an old ignored-area save deliberately. Recovery uses the same
            // Resolve -> Hydrate -> repository save as selection, without
            // claiming to cover its full packet, lease, and teardown sequence.
            fixture.Records.UpdatePosition(fixture.Session.Player.CharacterId, 2, 65, 600, 250, 3, 0);
            var record = fixture.Reload();
            var selectedSpawn = GameChannelSpawnPolicy.Resolve(fixture.Session.ListenerPort, record.TownId);
            fixture.Session.Player.HydrateFrom(record, selectedSpawn);
            check("hydrating a saved excluded subarea still uses the same town's safe ordinary gate",
                fixture.Session.Player.CurTownId == spawn.TownId && fixture.Session.Player.CurAreaId == spawn.AreaId
                && fixture.Session.Player.CurPosX == spawn.X && fixture.Session.Player.CurPosY == spawn.Y);
            selectedSpawn.ApplyTo(record);
            check("the initialization-record override and live character use one recovered position",
                MatchesPlayer(record, fixture.Session.Player));
            if (GameChannelSpawnPolicy.ShouldPersistPosition(fixture.Session.ListenerPort,
                    fixture.Session.Player.CurTownId, fixture.Session.Player.CurAreaId))
            {
                var player = fixture.Session.Player;
                fixture.Records.UpdatePosition(player.CharacterId, player.CurTownId, player.CurAreaId,
                    player.CurPosX, player.CurPosY, player.CurDirection, player.CurAreaState);
            }
            check("a rebuilt repository reads the ordinary gate without retaining the ignored subarea",
                MatchesPlayer(fixture.Reload(), fixture.Session.Player) && fixture.Reload().TownId == 2
                && fixture.Reload().AreaId != 65 && !Town.IsPositionSaveExcluded(fixture.Reload().TownId, fixture.Reload().AreaId));
        }

        private static VillageSaveConfigFile LoadCurrentConfig()
            => VillageSaveConfigFile.Parse(PvfArchiveAccessor.ReadText("etc/serverparameter_chn.etc"));

        private static async Task VerifyRejectedSelectionAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Records.UpdatePosition(fixture.Session.Player.CharacterId, byte.MaxValue, 0, 600, 250, 3, 0);
            var invalidRecord = fixture.Reload();
            var dataSource = new SqliteSelectCharacterDataSource(fixture.Database, fixture.Records);
            var handler = new CharacterSelectHandler(dataSource, fixture.Records, getUserInfoTemplate: null,
                database: fixture.Database);
            Exception failure = null;
            try { await handler.HandleResolvedSelectCharacterAsync(fixture.Session, invalidRecord, invalidRecord.SlotIndex).WaitAsync(Timeout); }
            catch (Exception exception) { failure = exception; }
            var packets = await fixture.DrainAsync();
            check("the real selection handler propagates a missing-town255 spawn failure before a successful init stream",
                failure != null && failure.Message.Contains("255", StringComparison.Ordinal) && packets.Count == 0);
            check("a rejected selection leaves the original persisted position intact for the existing outer rollback owner",
                SamePosition(invalidRecord, fixture.Reload()));
            // This invokes the actual selection handler's failure branch, but
            // does not invoke CharacterSessionLifecycleCoordinator rollback.
        }

        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);
        private static bool SamePosition(CharacterRecord expected, CharacterRecord actual)
            => expected != null && actual != null && expected.TownId == actual.TownId && expected.AreaId == actual.AreaId
               && expected.PosX == actual.PosX && expected.PosY == actual.PosY
               && expected.Direction == actual.Direction && expected.AreaState == actual.AreaState;
        private static bool MatchesPlayer(CharacterRecord record, PlayerContext player)
            => record != null && record.TownId == player.CurTownId && record.AreaId == player.CurAreaId
               && record.PosX == player.CurPosX && record.PosY == player.CurPosY
               && record.Direction == player.CurDirection && record.AreaState == player.CurAreaState;
        private static bool SameSpawn(GameChannelSpawn expected, GameChannelSpawn actual)
            => expected.TownId == actual.TownId && expected.AreaId == actual.AreaId && expected.X == actual.X && expected.Y == actual.Y
               && expected.Direction == actual.Direction && expected.AreaState == actual.AreaState && expected.IsTransient == actual.IsTransient;

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "town-position-" + Guid.NewGuid().ToString("N")));
            private readonly TcpClient _peer = new();
            internal GameDatabase Database { get; }
            internal SqliteCharacterRepository Records { get; }
            internal EnhancedClientSession Session { get; }
            internal TownHandler Town { get; }

            internal Fixture()
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 PVF.", archive);
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "position.db"), ServerPaths.SchemaFilePath);
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(11,'town-position-test','');";
                    command.ExecuteNonQuery();
                });
                Records = new SqliteCharacterRepository(Database);
                var record = new CharacterRecord
                {
                    CharacterId = 62022, AccountId = 11, Name = Encoding.ASCII.GetBytes("town-position"), Level = 70,
                    TownId = 11, AreaId = 0, PosX = 103, PosY = 214, Direction = 3, AreaState = 0,
                    Appearance = Array.Empty<CharacterAppearanceEntry>()
                };
                Records.Create(record);
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    var connecting = _peer.ConnectAsync(endpoint.Address, endpoint.Port);
                    var accepted = listener.AcceptTcpClientAsync().WaitAsync(Timeout).GetAwaiter().GetResult();
                    connecting.WaitAsync(Timeout).GetAwaiter().GetResult();
                    accepted.NoDelay = true;
                    _peer.NoDelay = true;
                    Session = new EnhancedClientSession(accepted, new GamePacketHeader(), GameNetworkConfig.NormalGamePort)
                    {
                        Account = new AccountRecord { AccountId = record.AccountId }
                    };
                    Session.Player.HydrateIdentityFrom(record);
                    Session.Player.CurTownId = record.TownId;
                    Session.Player.CurAreaId = record.AreaId;
                    Session.Player.CurPosX = record.PosX;
                    Session.Player.CurPosY = record.PosY;
                    Session.Player.CurDirection = record.Direction;
                    Session.Player.CurAreaState = record.AreaState;
                    Session.Player.AppearanceEntries = record.Appearance;
                    Session.GameSession = new GameSession(Session, Database, Records);
                    Town = new TownHandler(Records, selectDataSource: null, partyManager: null, sessions: null,
                        refresh: null, dungeonInstances: null, raidManager: null, database: Database);
                }
                finally { listener.Stop(); }
            }

            internal CharacterRecord Reload() => new SqliteCharacterRepository(Database).GetById(Session.Player.CharacterId);

            internal Task AreaAsync(byte town, byte area, short x, short y)
            {
                var body = new byte[16];
                body[0] = town;
                body[1] = area;
                BitConverter.TryWriteBytes(body.AsSpan(2), x);
                BitConverter.TryWriteBytes(body.AsSpan(4), y);
                body[6] = 5;
                BitConverter.TryWriteBytes(body.AsSpan(7), (ushort)Session.Player.CurTownId);
                BitConverter.TryWriteBytes(body.AsSpan(9), (ushort)Session.Player.CurAreaId);
                return Town.Handle_ENUM_CMDPACKET_SET_USER_AREA(Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_USER_AREA }, body).WaitAsync(Timeout);
            }

            internal Task PositionAsync(short x, short y, byte direction)
            {
                var body = new byte[7];
                BitConverter.TryWriteBytes(body.AsSpan(0), x);
                BitConverter.TryWriteBytes(body.AsSpan(2), y);
                body[4] = direction;
                return Town.Handle_ENUM_CMDPACKET_SET_USER_POSITION(Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_USER_POSITION }, body).WaitAsync(Timeout);
            }

            internal async Task<List<byte[]>> DrainAsync()
            {
                var packets = new List<byte[]>();
                var stream = _peer.GetStream();
                using var cancellation = new CancellationTokenSource(Timeout);
                while (_peer.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_peer.Available == 0) throw new IOException("The town handler unexpectedly closed the loopback connection.");
                    var header = new byte[HeaderLength];
                    await stream.ReadExactlyAsync(header, cancellation.Token);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < HeaderLength || length > 65536) throw new InvalidDataException("Invalid town packet envelope length.");
                    var packet = new byte[length];
                    header.CopyTo(packet, 0);
                    await stream.ReadExactlyAsync(packet.AsMemory(HeaderLength), cancellation.Token);
                    packets.Add(packet);
                }
                return packets;
            }

            public void Dispose()
            {
                Session?.Close();
                _peer.Dispose();
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
        }
    }
}
