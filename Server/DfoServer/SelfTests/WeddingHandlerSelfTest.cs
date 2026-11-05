using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingHandlerSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-wire-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunAsync(path, Check).GetAwaiter().GetResult();
                CheckReservationNoticeAsync(path + ".reservation", Check).GetAwaiter().GetResult();
                CheckRingNoticeAsync(path + ".ring", Check).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string database in new[] { path, path + ".reservation", path + ".ring" })
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(database + suffix)) File.Delete(database + suffix);
            }
            Console.WriteLine($"WEDDING_HANDLER: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task CheckRingNoticeAsync(string path, Action<string, bool> check)
        {
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var catalog = WeddingCatalog.Get();
            db.Write((c, t) =>
            {
                using var q = c.CreateCommand(); q.Transaction = t;
                q.CommandText = "INSERT INTO accounts(account_id,m_id) VALUES(64901,'ring-a'),(64902,'ring-b'),(64903,'ring-unmarried');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(64901,64901,'ring-a',0,60),(64902,64902,'ring-b',1,60),(64903,64903,'ring-unmarried',0,60);";
                q.ExecuteNonQuery();
                var state = WeddingRepository.Create(c, t, 64901, 64902, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Married, Ring = 2 }))
                    throw new InvalidOperationException("Cannot prepare ring fixture.");
            });
            var directory = new SessionDirectory();
            using var host = await Peer.Create(directory, 64901, "ring-a");
            using var spouse = await Peer.Create(directory, 64902, "ring-b");
            using var unmarried = await Peer.Create(directory, 64903, "ring-unmarried");
            var characters = new SqliteCharacterRepository(db);
            var data = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(data, characters, db);
            var town = new TownHandler(characters, data, null, directory, refresh,
                dungeonInstances: null, raidManager: null, database: db);
            using var wedding = new WeddingHandler(directory, new CharacterTransitionCoordinator(directory), db, characters, refresh, town);
            var routes = new GameCommandRegistry(); routes.RegisterGroup("ring-upgrade", wedding.Register);
            try
            {
                foreach (var peer in new[] { host, spouse })
                {
                    int cid = peer.Session.Player.CharacterId;
                    peer.Session.Account = new AccountRecord { AccountId = cid };
                    using var c = db.OpenConnection();
                    var lease = InventoryContext.Register(peer.Session.SessionId, InventoryService.LoadFromDb(c, cid, cid, db));
                    lease.Inventory.SetMainVirtualCount(0, 1000000);
                    if (peer == host) lease.Inventory.SetItem(InventoryListType.Main, 16,
                        new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = catalog.RingTickets[3].ItemId, Count = catalog.RingTickets[3].Count });
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Cannot fund ring fixture.");
                }
                var repository = new WeddingRepository(db);
                long pair = repository.Load(64901).PairId;
                db.Write((c, t) =>
                {
                    var room = WeddingRoomRepository.Load(c, t, pair, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var furniture = WeddingRoomCatalog.Get().FurniturePositions.First();
                    room.Installed[furniture.Value.First()] = furniture.Key;
                    WeddingRoomRepository.Save(c, t, room);
                });
                bool Is(byte[] p, bool command, ushort type) => p[0] == (command ? 1 : 0) && BitConverter.ToUInt16(p, 1) == type;
                bool RingAck(byte[] p) => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_UPGRADE_RING);
                bool RingNotice(byte[] p) => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_UPGRADE_RING);
                byte[] request = Convert.FromHexString("9192939495969798999A9B9C9D9E1000000003");
                async Task Send(Peer peer, byte[] body)
                {
                    if (!routes.TryGetValue((ushort)CmdPacketTypeA21.WEDDING_UPGRADE_RING, out var route)) throw new InvalidOperationException("Missing ring route");
                    await route(peer.Session, new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.WEDDING_UPGRADE_RING }, body);
                }
                check("native ring ACK includes the ring byte on both success and failure",
                    WeddingPacketBuilder.RingResult(0, 3).AsSpan(15).SequenceEqual(Convert.FromHexString("010000000003"))
                    && WeddingPacketBuilder.RingResult(-2, 3).AsSpan(15).SequenceEqual(Convert.FromHexString("01FEFFFFFF03"))
                    && WeddingPacketBuilder.ItemResult(CmdPacketTypeA21.WEDDING_USE_LOVE_POINT_ITEM, 0).Length == 20);
                await Send(host, request.AsSpan(14).ToArray());
                check("ring route rejects an unprefixed request without publishing or consuming", host.Drain().Count == 0
                    && spouse.Drain().Count == 0 && repository.Load(64901).Ring == 2);
                await Send(host, request);
                var actorPackets = host.Drain(); var spousePackets = spouse.Drain();
                var observerPackets = unmarried.Drain();
                check("ordinary observers receive no private ring success or marriage-room replay",
                    !observerPackets.Any(p => RingAck(p) || RingNotice(p)
                        || Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO)
                        || Is(p, false, (ushort)NotiPacketTypeA21.COUPLE_ROOM)));
                var actorAck = actorPackets.Single(RingAck);
                check("initiator gets one complete ring success and no duplicate success notification",
                    actorAck.Length == 21 && actorAck.AsSpan(15).SequenceEqual(Convert.FromHexString("010000000003"))
                    && !actorPackets.Any(RingNotice));
                var spouseNotice = spousePackets.Single(RingNotice);
                check("spouse gets the native single-byte ring notification without a forged CMD ACK",
                    spouseNotice.Length == 16 && spouseNotice[15] == 3 && !spousePackets.Any(RingAck));
                foreach (var entry in new[] { (Peer: host, Packets: actorPackets), (Peer: spouse, Packets: spousePackets) })
                {
                    int mainIndex = entry.Packets.FindLastIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.ITEM_LIST));
                    int stateIndex = entry.Packets.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO));
                    int roomIndex = entry.Packets.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.COUPLE_ROOM));
                    var state = repository.Load(entry.Peer.Session.Player.CharacterId);
                    var snapshot = WeddingReplaySnapshot.Load(db, entry.Peer.Session.Player.CharacterId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    check("ring upgrade restores authoritative marriage and furnished room after the last inventory refresh: " + state.CharacterId,
                        mainIndex >= 0 && stateIndex > mainIndex && roomIndex > stateIndex
                        && entry.Packets.Count(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO)) == 1
                        && entry.Packets[stateIndex].Length == 30 && entry.Packets[stateIndex][29] == 3
                        && entry.Packets[roomIndex].SequenceEqual(WeddingRoomPacketBuilder.State(snapshot.Room))
                        && snapshot.Room.Installed.Any(x => x != 0) && entry.Peer.Session.Player.Wedding.Ring == 3);
                }
                InventoryContext.TryGetOwnedLease(host.Session.SessionId, 64901, out var paidLease);
                check("ring upgrade commits the shared level and consumes exactly the configured ticket",
                    repository.Load(64901).Ring == 3 && repository.Load(64902).Ring == 3
                    && paidLease.Inventory.CountMainItem(catalog.RingTickets[3].ItemId) == 0);
                long version = repository.Load(64901).Version;
                await Send(host, request);
                var rejected = host.Drain();
                check("replayed ring command returns the complete failure body without rewards or peer publication",
                    rejected.Count == 1 && RingAck(rejected[0]) && rejected[0].Length == 21
                    && BitConverter.ToInt32(rejected[0], 16) != 0 && rejected[0][20] == 3
                    && spouse.Drain().Count == 0 && repository.Load(64901).Version == version);
                host.Session.Player.UserState = 1;
                await Send(host, request);
                var unavailable = host.Drain().Single();
                host.Session.Player.UserState = 0;
                check("not-town-ready rejection still supplies the ring byte required by the native error reader",
                    RingAck(unavailable) && unavailable.Length == 21 && BitConverter.ToInt32(unavailable, 16) == -2 && unavailable[20] == 3);
                await Send(unmarried, request);
                var unbound = unmarried.Drain().Single();
                check("unmarried rejection has a complete ignored ring sentinel and no success publication",
                    RingAck(unbound) && unbound.Length == 21 && BitConverter.ToInt32(unbound, 16) == -2 && unbound[20] == 255
                    && spouse.Drain().Count == 0);
            }
            finally
            {
                wedding.Dispose();
                foreach (var peer in new[] { host, spouse, unmarried })
                {
                    InventoryContext.Unregister(peer.Session.SessionId, peer.Session.Player.CharacterId);
                    await directory.UnregisterAsync(peer.Session.Player.CharacterId, peer.Session);
                }
            }
        }

        private static async Task CheckReservationNoticeAsync(string path, Action<string, bool> check)
        {
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var catalog = WeddingCatalog.Get();
            db.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(64801,'booking-a',''),(64802,'booking-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(64801,64801,'booking-a',0,60),(64802,64802,'booking-b',1,60);";
                command.ExecuteNonQuery();
                foreach (int cid in new[] { 64801, 64802 })
                foreach (int quest in catalog.RequiredQuests)
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO character_quest_completions(character_id,quest_id,completion_value) VALUES(@cid,@quest,1);";
                    command.Parameters.AddWithValue("@cid", cid); command.Parameters.AddWithValue("@quest", quest); command.ExecuteNonQuery();
                }
                WeddingRepository.Create(c, t, 64801, 64802, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            });
            var directory = new SessionDirectory();
            using var host = await Peer.Create(directory, 64801, "booking-a");
            using var partner = await Peer.Create(directory, 64802, "booking-b");
            var characters = new SqliteCharacterRepository(db);
            var data = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(data, characters, db);
            var town = new TownHandler(characters, data, null, directory, refresh,
                dungeonInstances: null, raidManager: null, database: db);
            using var wedding = new WeddingHandler(directory, new CharacterTransitionCoordinator(directory), db, characters, refresh, town);
            var routes = new GameCommandRegistry(); routes.RegisterGroup("booking-order", wedding.Register);
            try
            {
                foreach (var peer in new[] { host, partner })
                {
                    peer.Session.Account = new AccountRecord { AccountId = peer.Session.Player.CharacterId };
                    using var c = db.OpenConnection();
                    var lease = InventoryContext.Register(peer.Session.SessionId,
                        InventoryService.LoadFromDb(c, peer.Session.Player.CharacterId, peer.Session.Player.CharacterId, db));
                    lease.Inventory.SetMainVirtualCount(0, checked(catalog.Ceremonies[0].Gold + 10000));
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Cannot fund reservation fixture.");
                }
                async Task Send(Peer peer, CmdPacketTypeA21 command, byte[] fields)
                {
                    if (!routes.TryGetValue((ushort)command, out var handler)) throw new InvalidOperationException("Missing booking route");
                    await handler(peer.Session, new GamePacketHeader { cmd = 1, type = (ushort)command }, new byte[14].Concat(fields).ToArray());
                }
                var request = new GamePacketWriter(); request.WriteUInt32(64802); request.WriteByte(2); request.WriteByte(0); request.WriteByte(0);
                await Send(host, CmdPacketTypeA21.WEDDING_REQUEST, request.ToArray()); host.Drain(); partner.Drain();
                var response = new GamePacketWriter(); response.WriteUInt32(64801); response.WriteByte(2); response.WriteByte(0); response.WriteByte(0); response.WriteByte(0);
                await Send(partner, CmdPacketTypeA21.WEDDING_RESPONSE, response.ToArray());
                foreach (var peer in new[] { host, partner })
                {
                    var packets = peer.Drain();
                    bool Is(byte[] p, bool command, ushort type) => p[0] == (command ? 1 : 0) && BitConverter.ToUInt16(p, 1) == type;
                    int stateIndex = packets.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO));
                    int reservationIndex = packets.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_CEREMONY));
                    int resultIndex = packets.FindIndex(p => peer == host
                        ? Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_RESPONSE)
                        : Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_RESPONSE));
                    // Replay the independently verified native dialog behavior:
                    // success C8A6B0 writes 11D0F; later C8A780 clears it.
                    bool dialogOpen = false, instructionsVisible = false;
                    foreach (var packet in packets)
                    {
                        if (Is(packet, false, (ushort)NotiPacketTypeA21.WEDDING_CEREMONY) && dialogOpen) instructionsVisible = false;
                        bool success = peer == host
                            ? Is(packet, false, (ushort)NotiPacketTypeA21.WEDDING_RESPONSE) && BitConverter.ToInt32(packet, 15) == 0
                            : Is(packet, true, (ushort)CmdPacketTypeA21.WEDDING_RESPONSE) && packet[15] == 1 && BitConverter.ToInt32(packet, 16) == 0;
                        if (success) { dialogOpen = true; instructionsVisible = true; }
                    }
                    check("reservation data precedes congratulations success for each host: " + peer.Session.Player.CharacterId,
                        stateIndex >= 0 && reservationIndex > stateIndex && resultIndex > reservationIndex
                        && packets.Count(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_CEREMONY)) == 1
                        && packets[reservationIndex].Length == 94 && dialogOpen && instructionsVisible);
                }
                check("notification ordering follows a committed reservation with one actual charge",
                    new WeddingRepository(db).Load(64801).Status == WeddingStatus.Reserved
                    && db.Read(c => CurrencyService.LoadWallet(c, null, 64801).Gold) == 10000);
            }
            finally
            {
                wedding.Dispose();
                foreach (var peer in new[] { host, partner })
                {
                    InventoryContext.Unregister(peer.Session.SessionId, peer.Session.Player.CharacterId);
                    await directory.UnregisterAsync(peer.Session.Player.CharacterId, peer.Session);
                }
            }
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            // Actual NPC confirmation capture: transport header 14, body 21.
            // The first 14 body bytes are opaque; peer/op/ceremony/delay follow.
            byte[] captured = Convert.FromHexString("01F003230000008D4C536600160048EB3A2D94A1B60360F7190067B401000000010000");
            var processor = new FlexiblePacketProcessor();
            var wireId = Guid.NewGuid();
            processor.SetClientPacketStructure(wireId, new GamePacketHeader());
            var partial = captured[..19];
            check("captured NPC proposal remains buffered until its full wire body arrives",
                processor.ProcessReceivedData(wireId, partial, partial.Length).Count == 0);
            var tail = captured[19..];
            var decoded = processor.ProcessReceivedData(wireId, tail, tail.Length).Single();
            check("actual 35-byte NPC frame decodes its 21-byte body at the native field offset",
                decoded.GetHeader<GamePacketHeader>().type == (ushort)CmdPacketTypeA21.WEDDING_REQUEST
                && decoded.BodyData.Length == 21
                && WeddingCommandParser.TryRequest(decoded.BodyData, out var proposal)
                && proposal == new WeddingRequest(1, 1, 0, 0));
            byte[] Native(byte[] fields) => captured.AsSpan(14, 14).ToArray().Concat(fields).ToArray();
            check("native proposal rejects wrong lengths and invalid operations instead of searching for a plausible suffix",
                !WeddingCommandParser.TryRequest(Native(new byte[] { 1, 0, 0, 0, 0, 0, 0 }), out _)
                && !WeddingCommandParser.TryRequest(decoded.BodyData.Concat(new byte[1]).ToArray(), out _)
                && !WeddingCommandParser.TryRequest(decoded.BodyData[1..], out _));
            check("native response retains exact length, decision and reservation field validation",
                WeddingCommandParser.TryResponse(Native(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }), out var accepted)
                && accepted.Request == new WeddingRequest(1, 1, 0, 0) && accepted.Decision == 0
                && !WeddingCommandParser.TryResponse(Native(new byte[] { 1, 0, 0, 0, 1, 0, 0, 3 }), out _)
                && !WeddingCommandParser.TryResponse(Native(new byte[] { 1, 0, 0, 0, 2, 1, 0, 0 }), out _));
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            db.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63101,'wedding-wire-a',''),(63102,'wedding-wire-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63101,63101,'婚姻甲',0,60),(63102,63102,'婚姻乙',1,60),(128638,63102,'高位角色',1,60);";
                cmd.ExecuteNonQuery();
                foreach (var row in new[] { (Id: 63101, Name: "婚姻甲"), (Id: 63102, Name: "婚姻乙"), (Id: 128638, Name: "高位角色") })
                {
                    cmd.Parameters.Clear(); cmd.CommandText = "UPDATE characters SET name=@name WHERE character_id=@id;";
                    cmd.Parameters.AddWithValue("@name", ClientTextEncoding.GetBytes(row.Name)); cmd.Parameters.AddWithValue("@id", row.Id); cmd.ExecuteNonQuery();
                }
                cmd.Parameters.Clear(); cmd.CommandText = "UPDATE characters SET level=73,grow_type=18 WHERE character_id=63102;"; cmd.ExecuteNonQuery();
            });
            var directory = new SessionDirectory();
            using var a = await Peer.Create(directory, 63101, "婚姻甲");
            using var b = await Peer.Create(directory, 63102, "婚姻乙");
            using var replacement = await Peer.Create(directory, 63102, "婚姻乙", register: false);
            var characters = new SqliteCharacterRepository(db);
            var data = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(data, characters, db);
            var town = new TownHandler(characters, data, null, directory, refresh,
                dungeonInstances: null, raidManager: null, database: db);
            var transitions = new CharacterTransitionCoordinator(directory);
            using var wedding = new WeddingHandler(directory, transitions, db, characters, refresh, town);
            var routes = new GameCommandRegistry(); routes.RegisterGroup("wedding-test", wedding.Register);
            var repository = new WeddingRepository(db);
            InventoryLease Load(Peer p)
            {
                using var c = db.OpenConnection();
                return InventoryContext.Register(p.Session.SessionId, InventoryService.LoadFromDb(c,
                    p.Session.Player.CharacterId, p.Session.Player.CharacterId, db));
            }
            Load(a); Load(b);
            a.Session.Account = new AccountRecord { AccountId = 63101 };
            b.Session.Account = new AccountRecord { AccountId = 63102 };
            a.Session.GameSession = new GameSession(a.Session, db, characters, data, directory);
            b.Session.GameSession = new GameSession(b.Session, db, characters, data, directory);
            async Task Send(Peer peer, CmdPacketTypeA21 type, byte[] body)
            {
                if (!routes.TryGetValue((ushort)type, out var handler)) throw new InvalidOperationException("Missing wedding route " + type);
                await handler(peer.Session, new GamePacketHeader { cmd = 1, type = (ushort)type }, body);
            }
            byte[] Request(uint uid, byte operation)
            { var w = new GamePacketWriter(); w.WriteUInt32(uid); w.WriteByte(operation); w.WriteByte(0); w.WriteByte(0); return Native(w.ToArray()); }
            byte[] Accept(uint uid, byte operation, byte decision = 0) => Request(uid, operation).Concat(new[] { decision }).ToArray();
            bool Is(byte[] p, bool command, ushort type) => p[0] == (command ? 1 : 0) && BitConverter.ToUInt16(p, 1) == type;
            bool HasAvailable(byte[] packet, ushort quest)
            {
                if (!Is(packet, false, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST) || packet.Length < 18) return false;
                int count = BitConverter.ToUInt16(packet, 16);
                return packet.Length == 18 + count * 2
                    && Enumerable.Range(0, count).Any(i => BitConverter.ToUInt16(packet, 18 + i * 2) == quest);
            }
            bool CooldownNotice(byte[] packet, int days)
            {
                if (!Is(packet, false, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE)
                    || packet.Length < 20 || packet[15] != 0) return false;
                int length = BitConverter.ToInt32(packet, 16);
                if (length < 0 || packet.Length != 20 + length) return false;
                string text = ClientTextEncoding.GetString(packet, 20, length);
                return text.Contains("离婚后的再婚冷却期") && text.Contains($"离婚满{days}天")
                    && !text.Contains("名花") && !text.Contains("凌晨");
            }
            void SetDivorceTime(int cid, long at)
                => db.Write((c, t) => WeddingRepository.SaveCooldown(c, t, cid, at, null));
            void SetReservationTime(int cid, long at)
                => db.Write((c, t) => WeddingRepository.SaveCooldown(c, t, cid, null, at));
            bool CeremonyCooldownNotice(byte[] packet, int days)
            {
                if (!Is(packet, false, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE)
                    || packet.Length < 20 || packet[15] != 0) return false;
                int length = BitConverter.ToInt32(packet, 16);
                if (length < 0 || packet.Length != 20 + length) return false;
                string text = ClientTextEncoding.GetString(packet, 20, length);
                return text.Contains("婚礼预约冷却期") && text.Contains($"上次预约满{days}天") && !text.Contains("维护");
            }
            try
            {
                int remarryDays = WeddingCatalog.Get().RemarryDays;
                foreach (int coolingCharacter in new[] { 63101, 63102 })
                {
                    SetDivorceTime(coolingCharacter, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 1));
                    var cooling = a.Drain();
                    check("either character's divorce cooldown sends a silent failure and configured notice: " + coolingCharacter,
                        cooling.Count == 2 && Is(cooling[0], true, (ushort)CmdPacketTypeA21.WEDDING_REQUEST)
                        && cooling[0].Length == 27 && cooling[0][15] == 1 && BitConverter.ToInt32(cooling[0], 16) == -4
                        && CooldownNotice(cooling[1], remarryDays) && b.Drain().Count == 0
                        && repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None);
                    SetDivorceTime(coolingCharacter, 0);
                }
                check("cooldown notice follows configured days instead of a native fixed deadline",
                    CooldownNotice(WeddingPacketBuilder.RemarryCooldownNotice(3), 3));
                var ordinaryError = new WeddingRequest(63102, 1, 0, 0);
                check("noncooldown and nonengagement result mappings stay unchanged",
                    BitConverter.ToInt32(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST,
                        ordinaryError, (int)WeddingError.AlreadyBound), 16) == (int)WeddingError.AlreadyBound
                    && BitConverter.ToInt32(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST,
                        ordinaryError with { Operation = 2 }, (int)WeddingError.Cooldown), 16) == (int)WeddingError.Cooldown);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 1));
                a.Drain(); b.Drain();
                SetDivorceTime(63102, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1));
                var coolingProposer = a.Drain(); var coolingRecipient = b.Drain();
                check("cooldown discovered at acceptance rejects both response paths with accurate notices",
                    coolingProposer.Count == 2 && coolingRecipient.Count == 2
                    && Is(coolingProposer[0], false, (ushort)NotiPacketTypeA21.WEDDING_RESPONSE)
                    && BitConverter.ToInt32(coolingProposer[0], 15) == -4
                    && Is(coolingRecipient[0], true, (ushort)CmdPacketTypeA21.WEDDING_RESPONSE)
                    && BitConverter.ToInt32(coolingRecipient[0], 16) == -4
                    && CooldownNotice(coolingProposer[1], remarryDays) && CooldownNotice(coolingRecipient[1], remarryDays)
                    && repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None);
                SetDivorceTime(63102, 0);
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1));
                var rejected = b.Drain().Single();
                check("unsolicited wedding acceptance cannot create a relationship", BitConverter.ToInt32(rejected, 16) == -101
                    && repository.Load(63101).Status == WeddingStatus.None && a.Drain().Count == 0);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, new byte[8]);
                check("malformed wedding command is rejected before publication", a.Drain().Count == 0 && b.Drain().Count == 0);
                b.Session.Player.CurAreaId = 2;
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 1));
                check("proposal requires the selected pair to share an eligible scene", BitConverter.ToInt32(a.Drain().Single(), 16) == -101
                    && b.Drain().Count == 0);
                b.Session.Player.CurAreaId = 1;
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 1));
                var ack = a.Drain().Single(); var invitation = b.Drain().Single();
                check("registered request produces the native ACK and 11-byte invitation body", ack.Length == 27 && ack[15] == 1
                    && BitConverter.ToInt32(ack, 16) == 0 && invitation.Length == 26
                    && Is(invitation, false, (ushort)NotiPacketTypeA21.WEDDING_REQUEST)
                    && BitConverter.ToUInt32(invitation, 19) == 63101);
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 2));
                check("another operation cannot consume the pending proposal", BitConverter.ToInt32(b.Drain().Single(), 16) == -101
                    && repository.Load(63101).Status == WeddingStatus.None);
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1));
                var mine = a.Drain(); var theirs = b.Drain();
                check("matching acceptance commits one reciprocal engagement and publishes both state projections",
                    WeddingRules.IsReciprocal(repository.Load(63101), repository.Load(63102))
                    && repository.Load(63101).Status == WeddingStatus.Engaged
                    && mine.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_RESPONSE))
                    && theirs.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_RESPONSE))
                    && mine.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO))
                    && theirs.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO)));
                long pair = repository.Load(63101).PairId;
                ushort firstWeddingQuest = checked((ushort)WeddingCatalog.Get().RequiredQuests[0]);
                check("accepting engagement refreshes both real quest managers after the marital state without relogging",
                    mine.Any(p => HasAvailable(p, firstWeddingQuest)) && theirs.Any(p => HasAvailable(p, firstWeddingQuest))
                    && mine.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO))
                        < mine.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST))
                    && theirs.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO))
                        < theirs.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST)));
                check("acceptance publishes one State per client and leaves Partner to the native follow-up query",
                    mine.Count(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO)) == 1
                    && theirs.Count(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO)) == 1
                    && !mine.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_CHARAC))
                    && !theirs.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_CHARAC)));
                int ceremonyDays = WeddingCatalog.Get().WeddingCooldownDays;
                foreach (int coolingHost in new[] { 63101, 63102 })
                {
                    SetReservationTime(coolingHost, checked(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)ceremonyDays * 86400));
                    await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 2));
                    var reservationRequest = a.Drain().Single(); var reservationInvitation = b.Drain().Single();
                    await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 2));
                    var reservationProposer = a.Drain(); var reservationRecipient = b.Drain();
                    check("either host's reservation cooldown rejects acceptance without a maintenance alert: " + coolingHost,
                        BitConverter.ToInt32(reservationRequest, 16) == 0
                        && Is(reservationInvitation, false, (ushort)NotiPacketTypeA21.WEDDING_REQUEST)
                        && reservationProposer.Count == 2 && reservationRecipient.Count == 2
                        && Is(reservationProposer[0], false, (ushort)NotiPacketTypeA21.WEDDING_RESPONSE)
                        && BitConverter.ToInt32(reservationProposer[0], 15) == -106
                        && Is(reservationRecipient[0], true, (ushort)CmdPacketTypeA21.WEDDING_RESPONSE)
                        && BitConverter.ToInt32(reservationRecipient[0], 16) == -106
                        && CeremonyCooldownNotice(reservationProposer[1], ceremonyDays)
                        && CeremonyCooldownNotice(reservationRecipient[1], ceremonyDays)
                        && repository.Load(63101).Status == WeddingStatus.Engaged && repository.Load(63102).Status == WeddingStatus.Engaged
                        && repository.Load(63101).StartAt == 0 && repository.Load(63101).PairId == pair);
                    SetReservationTime(coolingHost, 0);
                }
                check("request reservation cooldown uses the same silent rejection and respects configured days",
                    BitConverter.ToInt32(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST,
                        new WeddingRequest(63102, 2, 0, 0), (int)WeddingError.CeremonyCooldown), 16) == -106
                    && CeremonyCooldownNotice(WeddingPacketBuilder.CeremonyCooldownNotice(3), 3));
                await Send(a, CmdPacketTypeA21.WEDDING_CHARAC, Native(BitConverter.GetBytes(0u)));
                await Send(b, CmdPacketTypeA21.WEDDING_CHARAC, Native(BitConverter.GetBytes(0u)));
                byte[] previewA = a.Drain().Single(), previewB = b.Drain().Single();
                bool PartnerFields(byte[] packet, int cid, string name, byte level, byte job, byte grow)
                    => Is(packet, true, (ushort)CmdPacketTypeA21.WEDDING_CHARAC) && packet.Length == 113 && packet[15] == 1
                        && BitConverter.ToInt32(packet, 16) == 0 && BitConverter.ToUInt32(packet, 20) == pair
                        && BitConverter.ToUInt32(packet, 24) == cid && ClientTextEncoding.GetString(packet, 28, 30) == name
                        && packet[58] == level && packet[59] == job && packet[60] == grow;
                check("native query zero receives the paired 97-byte record with canonical names and full growth fields",
                    PartnerFields(previewA, 63102, "婚姻乙", 73, 1, 18) && PartnerFields(previewB, 63101, "婚姻甲", 60, 0, 0));
                check("all thirteen empty preview equipment codes use the native FFFFFFFF default sentinel",
                    Enumerable.Range(0, 13).All(i => BitConverter.ToUInt32(previewA, 61 + i * 4) == uint.MaxValue
                        && BitConverter.ToUInt32(previewB, 61 + i * 4) == uint.MaxValue));
                characters.UpdateAppearance(63102, new[]
                {
                    new CharacterAppearanceEntry(12, 123456, 4, new byte[4], 0, 0, 0, 0),
                    new CharacterAppearanceEntry(4, 0, 4, new byte[4], 0, 0, 0, 0),
                });
                await Send(a, CmdPacketTypeA21.WEDDING_CHARAC, Native(BitConverter.GetBytes(0u)));
                var dressed = a.Drain().Single();
                check("positive display codes keep their original preview slot while zero and missing slots use defaults",
                    PartnerFields(dressed, 63102, "婚姻乙", 73, 1, 18)
                    && BitConverter.ToUInt32(dressed, 61 + 12 * 4) == 123456
                    && Enumerable.Range(0, 12).All(i => BitConverter.ToUInt32(dressed, 61 + i * 4) == uint.MaxValue));
                await wedding.RestoreAsync(a.Session);
                var restored = a.Drain(); b.Drain();
                check("engagement recovery sends State without an unsolicited preview record",
                    restored.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_INFO))
                    && !restored.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_CHARAC)));
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1));
                check("duplicate acceptance preserves the existing pair", BitConverter.ToInt32(b.Drain().Single(), 16) == -101
                    && repository.Load(63101).PairId == pair && a.Drain().Count == 0);
                foreach (var peer in new[] { a, b })
                {
                    await peer.Session.GameSession.QuestManager.HandleAcceptQuestAsync((ushort)CmdPacketTypeA21.ACCEPT_QUEST,
                        BitConverter.GetBytes(firstWeddingQuest), peer.Session.SessionId);
                    peer.Drain();
                }
                check("both clients can formally accept the newly advertised marriage quest",
                    db.Read(c => QuestRepository.LoadActiveQuests(c, null, 63101).Any(q => q.QuestId == firstWeddingQuest)
                        && QuestRepository.LoadActiveQuests(c, null, 63102).Any(q => q.QuestId == firstWeddingQuest)));
                InventoryContext.TryGetOwnedLease(a.Session.SessionId, 63101, out var cancellingActor);
                InventoryContext.TryGetOwnedLease(b.Session.SessionId, 63102, out var cancelledPartner);
                int cancellationCost = WeddingCatalog.Get().Alimony[0], partnerBalance = 98765;
                cancellingActor.Inventory.SetMainVirtualCount(0, 0);
                cancelledPartner.Inventory.SetMainVirtualCount(0, partnerBalance);
                if (!InventoryPersistenceService.SaveDirty(cancellingActor) || !InventoryPersistenceService.SaveDirty(cancelledPartner))
                    throw new InvalidOperationException("Cannot prepare engagement cancellation wallets.");
                if (cancellationCost > 0)
                {
                    await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(0, 5));
                    var unpaid = a.Drain().Single();
                    check("insufficient engagement cancellation gold preserves the reciprocal relationship and both wallets",
                        Is(unpaid, true, (ushort)CmdPacketTypeA21.WEDDING_REQUEST) && BitConverter.ToInt32(unpaid, 16) == -7
                        && b.Drain().Count == 0 && repository.Load(63101).PairId == pair
                        && repository.Load(63101).Status == WeddingStatus.Engaged
                        && WeddingRules.IsReciprocal(repository.Load(63101), repository.Load(63102))
                        && cancellingActor.Inventory.GetMainVirtualCount(0).Count == 0
                        && cancelledPartner.Inventory.GetMainVirtualCount(0).Count == partnerBalance);
                }
                int cancellationBalance = checked(cancellationCost + 34567);
                cancellingActor.Inventory.SetMainVirtualCount(0, cancellationBalance);
                if (!InventoryPersistenceService.SaveDirty(cancellingActor))
                    throw new InvalidOperationException("Cannot fund engagement cancellation.");
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(0, 5));
                mine = a.Drain(); theirs = b.Drain();
                check("cancelling engagement publishes the native one-byte divorce operation to both clients",
                    repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None
                    && mine.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_DIVORCE) && p.Length == 16 && p[15] == 5)
                    && theirs.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_DIVORCE) && p.Length == 16 && p[15] == 5));
                check("successful engagement cancellation charges only its initiator the configured pre-marriage fee",
                    mine.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_REQUEST) && BitConverter.ToInt32(p, 16) == 0)
                    && cancellingActor.Inventory.GetMainVirtualCount(0).Count == cancellationBalance - cancellationCost
                    && cancelledPartner.Inventory.GetMainVirtualCount(0).Count == partnerBalance);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(0, 5));
                var cancellationRetry = a.Drain().Single();
                check("cancellation atomically retires both marriage quests and refreshes accepted and available lists",
                    db.Read(c => !QuestRepository.LoadActiveQuests(c, null, 63101).Any(q => q.QuestId == firstWeddingQuest)
                        && !QuestRepository.LoadActiveQuests(c, null, 63102).Any(q => q.QuestId == firstWeddingQuest))
                    && mine.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTED_QUEST))
                    && theirs.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTED_QUEST))
                    && mine.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST))
                    && theirs.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST))
                    && !mine.Any(p => HasAvailable(p, firstWeddingQuest)) && !theirs.Any(p => HasAvailable(p, firstWeddingQuest)));
                check("replayed cancellation cannot charge either former partner again",
                    Is(cancellationRetry, true, (ushort)CmdPacketTypeA21.WEDDING_REQUEST) && BitConverter.ToInt32(cancellationRetry, 16) == -103
                    && b.Drain().Count == 0 && cancellingActor.Inventory.GetMainVirtualCount(0).Count == cancellationBalance - cancellationCost
                    && cancelledPartner.Inventory.GetMainVirtualCount(0).Count == partnerBalance
                    && repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 1)); a.Drain(); b.Drain();
                using (await transitions.AcquireAsync(63101))
                using (await transitions.AcquireAsync(63102))
                {
                    var replacing = directory.RegisterReplacingAsync(63102, replacement.Session);
                    bool returned = await Task.WhenAny(replacing, Task.Delay(2000)) == replacing;
                    check("ending an invited session while its character gate is held does not wait for the peer gate",
                        returned);
                    if (!returned) throw new TimeoutException("Wedding SessionEnding waited for a held character gate.");
                    await replacing;
                }
                Load(replacement);
                await Send(replacement, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1)); replacement.Drain();
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 1)); b.Drain();
                check("neither a replacement session nor the displaced session can accept the retired invitation",
                    repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None);
                a.Drain();
                db.Write((c, t) =>
                {
                    var marriage = WeddingRepository.Create(c, t, 63101, 63102, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (!WeddingRepository.Save(c, t, marriage, marriage with { Status = WeddingStatus.Married }))
                        throw new InvalidOperationException("Cannot prepare mutual-divorce fixture.");
                });
                InventoryContext.TryGetOwnedLease(a.Session.SessionId, 63101, out var left);
                InventoryContext.TryGetOwnedLease(replacement.Session.SessionId, 63102, out var right);
                int marriedDivorceCost = WeddingCatalog.Get().Alimony[1];
                int balance = checked(marriedDivorceCost * 2 + 10000);
                left.Inventory.SetMainVirtualCount(0, balance); right.Inventory.SetMainVirtualCount(0, balance);
                if (!InventoryPersistenceService.SaveDirty(left) || !InventoryPersistenceService.SaveDirty(right))
                    throw new InvalidOperationException("Cannot prepare divorce wallets.");
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 7)); a.Drain(); replacement.Drain();
                check("mutual divorce request does not debit either spouse before acceptance",
                    repository.Load(63101).Status == WeddingStatus.Married
                    && left.Inventory.GetMainVirtualCount(0).Count == balance && right.Inventory.GetMainVirtualCount(0).Count == balance);
                await Send(replacement, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 7, 1)); a.Drain(); replacement.Drain();
                check("refusing mutual divorce preserves marriage and both wallets",
                    repository.Load(63101).Status == WeddingStatus.Married
                    && left.Inventory.GetMainVirtualCount(0).Count == balance && right.Inventory.GetMainVirtualCount(0).Count == balance);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 7)); a.Drain(); replacement.Drain();
                await Send(replacement, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 7, 2)); a.Drain(); replacement.Drain();
                check("native timeout decision does not accept or charge mutual divorce",
                    repository.Load(63101).Status == WeddingStatus.Married
                    && left.Inventory.GetMainVirtualCount(0).Count == balance && right.Inventory.GetMainVirtualCount(0).Count == balance);
                await Send(a, CmdPacketTypeA21.WEDDING_REQUEST, Request(63102, 7)); a.Drain(); replacement.Drain();
                await Send(b, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 7)); b.Drain();
                check("the displaced session cannot accept the current spouse's divorce invitation",
                    repository.Load(63101).Status == WeddingStatus.Married
                    && left.Inventory.GetMainVirtualCount(0).Count == balance && right.Inventory.GetMainVirtualCount(0).Count == balance);
                await Send(replacement, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 7)); a.Drain(); replacement.Drain();
                int afterDivorce = balance - marriedDivorceCost;
                check("only accepting the bound divorce invitation commits both deductions and removes both memberships",
                    repository.Load(63101).Status == WeddingStatus.None && repository.Load(63102).Status == WeddingStatus.None
                    && left.Inventory.GetMainVirtualCount(0).Count == afterDivorce && right.Inventory.GetMainVirtualCount(0).Count == afterDivorce);
                await Send(replacement, CmdPacketTypeA21.WEDDING_RESPONSE, Accept(63101, 7)); replacement.Drain();
                check("replayed divorce acceptance cannot charge either spouse again",
                    left.Inventory.GetMainVirtualCount(0).Count == afterDivorce && right.Inventory.GetMainVirtualCount(0).Count == afterDivorce);
                await directory.UnregisterAsync(63102, replacement.Session);
                replacement.Session.Player.CharacterId = 128638; // same native u16 identity, distinct canonical character.
                directory.Register(128638, replacement.Session);
                db.Write((c, t) =>
                {
                    var created = WeddingRepository.Create(c, t, 63101, 128638, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (!WeddingRepository.Save(c, t, created, created with { Status = WeddingStatus.Married }))
                        throw new InvalidOperationException("Cannot prepare canonical-identity fixture.");
                });
                await Send(a, CmdPacketTypeA21.WEDDING_CHARAC, Native(BitConverter.GetBytes(63102u)));
                var partner = a.Drain().Single();
                check("partner query resolves the current native UID to its full canonical character rather than querying that UID as a database ID",
                    partner.Length == 113 && partner[15] == 1 && BitConverter.ToInt32(partner, 16) == 0
                    && BitConverter.ToUInt32(partner, 24) == 63101);
                using var collision = await Peer.Create(directory, 63102, "碰撞角色");
                await Send(a, CmdPacketTypeA21.WEDDING_CHARAC, Native(BitConverter.GetBytes(63102u)));
                check("ambiguous native UIDs cannot disclose an arbitrary character's spouse", a.Drain().Single()[15] == 0);
                await directory.UnregisterAsync(63102, collision.Session);
            }
            finally
            {
                wedding.Dispose();
                InventoryContext.Unregister(a.Session.SessionId, 63101);
                InventoryContext.Unregister(b.Session.SessionId, 63102);
                InventoryContext.Unregister(replacement.Session.SessionId, 63102);
                await directory.UnregisterAsync(63101, a.Session);
                await directory.UnregisterAsync(63102, b.Session);
                await directory.UnregisterAsync(128638, replacement.Session);
            }
        }
    }
}
