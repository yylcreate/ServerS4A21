using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingReplaySelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-replay-" + Guid.NewGuid().ToString("N") + ".db");
            try { RunAsync(path, Check).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_REPLAY: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            db.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63201,'wedding-replay-a',''),(63202,'wedding-replay-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,grow_type,level) VALUES(63201,63201,@a,0,0,60),(63202,63202,@b,1,5,60);";
                cmd.Parameters.AddWithValue("@a", ClientTextEncoding.GetBytes("回放甲"));
                cmd.Parameters.AddWithValue("@b", ClientTextEncoding.GetBytes("回放乙")); cmd.ExecuteNonQuery();
                var pair = WeddingRepository.Create(c, t, 63201, 63202, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!WeddingRepository.Save(c, t, pair, pair with { Status = WeddingStatus.Married }))
                    throw new InvalidOperationException("Cannot prepare replay marriage.");
            });
            var characters = new SqliteCharacterRepository(db);
            characters.UpdateAppearance(63202, new[] { new CharacterAppearanceEntry(12, 123456, 4, new byte[4], 0, 0, 0, 0) });
            var directory = new SessionDirectory();
            using var peer = await Peer.Create(directory, 63201, "回放甲");
            void LoadLease()
            {
                using var c = db.OpenConnection();
                InventoryContext.Register(peer.Session.SessionId, InventoryService.LoadFromDb(c, 63201, 63201, db));
            }
            LoadLease();
            var weddings = new WeddingRepository(db);
            long pairId = weddings.Load(63201).PairId;
            var furniture = WeddingRoomCatalog.Get().FurniturePositions.First();
            int position = furniture.Value.First();
            void EditRoom(Action<WeddingRoomState> edit) => db.Write((c, t) =>
            {
                var room = WeddingRoomRepository.Load(c, t, pairId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                edit(room); WeddingRoomRepository.Save(c, t, room);
            });
            EditRoom(room => room.Installed[position] = furniture.Key);
            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession)
                .GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(peer.Session);
            async Task<bool> RejectQueued(Action mutation)
            {
                await sendLock.WaitAsync();
                Task refresh = null;
                try
                {
                    // The method loads its snapshot synchronously, then waits on
                    // this send lock. Mutate after capture and before publication.
                    refresh = InventoryRefreshSender.SendWeddingReplayRefresh(peer.Session);
                    mutation();
                }
                finally { sendLock.Release(); }
                await refresh;
                return peer.Drain().Count == 0;
            }
            try
            {
                await InventoryRefreshSender.SendWeddingReplayRefresh(peer.Session);
                var packets = peer.Drain();
                check("ordinary refresh replays marriage and room without an unsolicited partner-query response",
                    packets.Count == 2 && packets[0][0] == 0
                    && BitConverter.ToUInt16(packets[0], 1) == (ushort)NotiPacketTypeA21.WEDDING_INFO
                    && packets[0][19] == (byte)WeddingStatus.Married && BitConverter.ToUInt32(packets[0], 15) == pairId
                    && packets[1][0] == 0 && BitConverter.ToUInt16(packets[1], 1) == (ushort)NotiPacketTypeA21.COUPLE_ROOM
                    && !packets.Any(p => p[0] == 1 && BitConverter.ToUInt16(p, 1) == (ushort)CmdPacketTypeA21.WEDDING_CHARAC));
                var partnerRecord = characters.GetById(63202);
                var partner = WeddingPacketBuilder.Partner(weddings.Load(63201), partnerRecord);
                check("native partner-query response preserves full growth and actual appearance slot 12",
                    partner.Length == 113 && partner[0] == 1
                    && BitConverter.ToUInt16(partner, 1) == (ushort)CmdPacketTypeA21.WEDDING_CHARAC
                    && partner[15] == 1 && partner[60] == 5
                    && Enumerable.Range(0, 12).All(slot => BitConverter.ToUInt32(partner, 61 + slot * 4) == uint.MaxValue)
                    && BitConverter.ToUInt32(partner, 109) == 123456);
                partnerRecord.Appearance = Array.Empty<CharacterAppearanceEntry>();
                var unequippedPartner = WeddingPacketBuilder.Partner(weddings.Load(63201), partnerRecord);
                check("an unequipped partner uses the native no-equipment sentinel in all thirteen code fields",
                    unequippedPartner.Length == 113
                    && Enumerable.Range(0, 13).All(slot => BitConverter.ToUInt32(unequippedPartner, 61 + slot * 4) == uint.MaxValue));
                await VerifyActorPresence(db, directory, peer, check);
                check("queued room replay is discarded after another committed furniture version",
                    await RejectQueued(() => EditRoom(room => room.Installed[position] = 0)));
                check("queued wedding snapshot is discarded after a partner appearance change even though the marriage version did not change",
                    await RejectQueued(() => characters.UpdateAppearance(63202,
                        new[] { new CharacterAppearanceEntry(12, 654321, 4, new byte[4], 0, 0, 0, 0) })));
                check("queued wedding replay cannot cross an owned-lease replacement", await RejectQueued(LoadLease));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                EditRoom(room => { room.TreeId = 1; room.TreeExpires = now + 1; room.TreeGrowth = 10; });
                var beforeExpiry = WeddingReplaySnapshot.Load(db, 63201, now);
                check("absolute tree expiry invalidates a queued room projection without depending on a timer",
                    beforeExpiry.Room.TreeId == 1 && !beforeExpiry.MatchesCurrent(db, 63201, now + 2)
                    && WeddingReplaySnapshot.Load(db, 63201, now + 2).Room.TreeId == 0);
                check("queued marriage replay is discarded after divorce even with the same current lease", await RejectQueued(() => db.Write((c, t) =>
                {
                    var before = WeddingRepository.Load(c, t, 63201);
                    if (!WeddingRepository.Save(c, t, before, before with { Status = WeddingStatus.None }))
                        throw new InvalidOperationException("Cannot prepare replay divorce.");
                    WeddingRepository.RemoveMembers(c, t, before.PairId);
                })));
                await InventoryRefreshSender.SendWeddingReplayRefresh(peer.Session);
                packets = peer.Drain();
                check("refresh after divorce uses unbound state and room projections without an unsolicited partner response",
                    packets.Count == 2 && packets[0][19] == 0 && BitConverter.ToUInt32(packets[0], 15) == uint.MaxValue
                    && packets[1][0] == 0 && BitConverter.ToUInt16(packets[1], 1) == (ushort)NotiPacketTypeA21.COUPLE_ROOM
                    && !packets.Any(p => p[0] == 1 && BitConverter.ToUInt16(p, 1) == (ushort)CmdPacketTypeA21.WEDDING_CHARAC));
            }
            finally
            {
                InventoryContext.Unregister(peer.Session.SessionId, 63201);
                await directory.UnregisterAsync(63201, peer.Session);
            }
        }

        private static async Task VerifyActorPresence(GameDatabase database, SessionDirectory sessions, Peer actor, Action<string, bool> check)
        {
            database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63203,'wedding-observer','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63203,63203,@name,0,60);";
                command.Parameters.AddWithValue("@name", ClientTextEncoding.GetBytes("观察者")); command.ExecuteNonQuery();
            });
            using var observer = await Peer.Create(sessions, 63203, "观察者");
            var characters = new SqliteCharacterRepository(database);
            var data = new SqliteSelectCharacterDataSource(database, characters);
            var refresh = new InventoryRefreshSender(data, characters, database); refresh.BindSessions(sessions);
            var town = new TownHandler(characters, data, null, sessions, refresh, null, null, database);
            using var wedding = new WeddingHandler(sessions, new CharacterTransitionCoordinator(sessions), database, characters, refresh, town);
            town.ConfigureWeddingAreaAdmission(new WeddingSceneAdmission(database, sessions).CanEnter, wedding.SendActorStateAsync);
            refresh.BindWeddingActorProjection(wedding.SendActorStateAsync);
            actor.Session.Player.Level = observer.Session.Player.Level = 60;
            actor.Session.Player.CurTownId = observer.Session.Player.CurTownId = 2;
            actor.Session.Player.CurAreaId = 1; observer.Session.Player.CurAreaId = 2;
            bool MarriedActor(byte[] p) => p.Length == 29 && p[0] == 0
                && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.COUPLE_SIMPLE_INFO
                && BitConverter.ToUInt32(p, 15) == actor.Session.Player.UserId && p[23] == (byte)WeddingStatus.Married;
            byte[] Area(byte area)
            {
                var body = new byte[6]; body[0] = 2; body[1] = area;
                BitConverter.GetBytes((short)500).CopyTo(body, 2); BitConverter.GetBytes((short)230).CopyTo(body, 4); return body;
            }
            try
            {
                await town.Handle_ENUM_CMDPACKET_SET_USER_AREA(observer.Session, default, Area(1));
                check("a newly arriving observer receives the existing actor's 14-byte marital state after insertion",
                    observer.Drain().Any(MarriedActor)); actor.Drain();
                await town.Handle_ENUM_CMDPACKET_SET_USER_AREA(observer.Session, default, Area(2)); observer.Drain(); actor.Drain();
                await town.Handle_ENUM_CMDPACKET_SET_USER_AREA(observer.Session, default, Area(1));
                check("returning to the area restores the actor's real marriage instead of depending on the original marriage broadcast",
                    observer.Drain().Any(MarriedActor)); actor.Drain();
                await refresh.SendNoti2AppearanceUpdate(actor.Session);
                check("appearance rebuilding restores marital actor state to self and the current audience",
                    actor.Drain().Any(MarriedActor) && observer.Drain().Any(MarriedActor));
                var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession)
                    .GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(observer.Session);
                await sendLock.WaitAsync();
                Task stale = null;
                try
                {
                    stale = wedding.SendActorStateAsync(actor.Session, observer.Session, CancellationToken.None);
                    database.Write((c, t) =>
                    {
                        var before = WeddingRepository.Load(c, t, actor.Session.Player.CharacterId);
                        if (!WeddingRepository.Save(c, t, before, before with { Love = before.Love + 1 }))
                            throw new InvalidOperationException("Cannot prepare actor-version race.");
                    });
                }
                finally { sendLock.Release(); }
                await stale;
                check("queued marital actor state cannot overwrite a newer committed pair version", observer.Drain().Count == 0);
            }
            finally { await sessions.UnregisterAsync(63203, observer.Session); }
        }
    }
}
