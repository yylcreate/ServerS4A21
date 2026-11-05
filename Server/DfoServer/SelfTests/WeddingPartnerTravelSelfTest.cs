using DfoServer.Game.Wedding;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingPartnerTravelSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            try
            {
                byte[] check = new byte[8]; BitConverter.GetBytes(123u).CopyTo(check, 0); BitConverter.GetBytes(300).CopyTo(check, 4);
                Check("A21 partner check carries opaque stack prefix then pair and inventory slot", WeddingPartnerTravelCommandParser.TryCheck(Native(check), out uint pair, out int slot)
                    && pair == 123 && slot == 300);
                Check("partner check rejects unprefixed legacy fields and abnormal lengths", !WeddingPartnerTravelCommandParser.TryCheck(check, out _, out _)
                    && !WeddingPartnerTravelCommandParser.TryCheck(Native(check).Concat(new byte[] { 0xEF }).ToArray(), out _, out _)
                    && !WeddingPartnerTravelCommandParser.TryCheck(Native(check)[..^1], out _, out _));
                var destination = new WeddingPartnerTravelDestination(11, 2, 0, -12, 345);
                var request = new WeddingPartnerTravelMove(65, 2, 0, -12, 345);
                byte[] move = new byte[20]; BitConverter.GetBytes(65).CopyTo(move, 0); BitConverter.GetBytes(2).CopyTo(move, 4);
                BitConverter.GetBytes(-12).CopyTo(move, 12); BitConverter.GetBytes(345).CopyTo(move, 16);
                Check("A21 manual-copy move reads source slot and widened signed positions after its stack prefix", WeddingPartnerTravelCommandParser.TryMove(Native(move), out var parsed) && parsed == request);
                Check("partner move rejects unprefixed legacy fields and abnormal lengths", !WeddingPartnerTravelCommandParser.TryMove(move, out _)
                    && !WeddingPartnerTravelCommandParser.TryMove(Native(move).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingPartnerTravelCommandParser.TryMove(Native(move)[..^1], out _));
                BitConverter.GetBytes(32768).CopyTo(move, 12);
                Check("partner move rejects signed coordinate overflow even at the exact native body length", !WeddingPartnerTravelCommandParser.TryMove(Native(move), out _));
                byte[] ack = WeddingPartnerTravelPacketBuilder.Check(0, destination);
                Check("partner check ACK matches the 22-byte native block", ack.Length == 38 && ack[15] == 1
                    && BitConverter.ToInt16(ack, 20) == 11 && BitConverter.ToInt32(ack, 26) == 2
                    && BitConverter.ToInt32(ack, 30) == 0 && BitConverter.ToInt16(ack, 34) == -12 && BitConverter.ToInt16(ack, 36) == 345);
                Check("failure check still provides the fixed native read block", WeddingPartnerTravelPacketBuilder.Check(-1).Length == 38);
                Check("travel scenes come from actual PVF and exclude Seria, church and unknown areas",
                    WeddingPartnerTravelSceneCatalog.IsPublicOrCoupleRoom(2, 4) && !WeddingPartnerTravelSceneCatalog.IsPublicOrCoupleRoom(2, 0)
                    && !WeddingPartnerTravelSceneCatalog.IsPublicOrCoupleRoom(2, 12) && !WeddingPartnerTravelSceneCatalog.IsPublicOrCoupleRoom(255, 255));
                var sid = Guid.NewGuid(); var peerSid = Guid.NewGuid();
                var actor = new WeddingState(123, 1, 2, WeddingStatus.Married, 0, 0, 0, 1800000000, 0, 11, 0, 4);
                var partner = actor with { CharacterId = 2, PartnerId = 1 };
                var ticket = new WeddingPartnerTravelTicket(sid, 1, peerSid, 2, 123, 4, 15011, destination, 2000, 65, 10002915, new byte[99]);
                bool Matches(Guid a, Guid b, WeddingState x, WeddingState y, WeddingPartnerTravelDestination current,
                    WeddingPartnerTravelMove requested, long tick = 1000) => ticket.Matches(a, 1, b, 2, x, y, 15011, current, requested, tick);
                Check("exact current identities can consume a valid partner destination", Matches(sid, peerSid, actor, partner, destination, request));
                Check("actor or spouse replacement invalidates travel ticket", !Matches(Guid.NewGuid(), peerSid, actor, partner, destination, request)
                    && !Matches(sid, Guid.NewGuid(), actor, partner, destination, request));
                Check("marriage version and divorce invalidate pending travel", !Matches(sid, peerSid, actor with { Version = 5 }, partner with { Version = 5 }, destination, request)
                    && !Matches(sid, peerSid, actor with { Status = WeddingStatus.None }, partner, destination, request));
                Check("spouse movement or forged destination cannot teleport actor", !Matches(sid, peerSid, actor, partner, destination with { X = 1 }, request)
                    && !Matches(sid, peerSid, actor, partner, destination, request with { Town = 101 }));
                Check("confirmation cannot substitute another inventory slot", !Matches(sid, peerSid, actor, partner, destination, request with { Slot = 66 }));
                Check("expired confirmation is rejected at its exact deadline", !Matches(sid, peerSid, actor, partner, destination, request, 2000));
                TransactionCases(Check);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_PARTNER_TRAVEL: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }

        private static byte[] Native(byte[] fields) => Convert.FromHexString("9192939495969798999A9B9C9D9E").Concat(fields).ToArray();

        private static void TransactionCases(Action<string, bool> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "a21-partner-travel-" + Guid.NewGuid().ToString("N") + ".db");
            var sid = Guid.NewGuid();
            try
            {
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63351,'travel-a',''),(63352,'travel-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level,town_id,area_id,pos_x,pos_y) VALUES(63351,63351,'传送甲',0,60,2,1,1,2),(63352,63352,'传送乙',1,60,2,4,50,60);"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(1,63351,63352,4,1800000000,1800000000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63351,1,0),(63352,1,1);");
                using var connection = db.OpenConnection();
                var lease = InventoryContext.Register(sid, InventoryService.LoadFromDb(connection, 63351, 63351, db));
                const int travelItem = 10002915; // Current A21 PVF fixture; production identifies the action tag.
                if (!InventoryRewardGrantService.TryCreateAndInsert(lease.Inventory, travelItem, ItemCreateReason.Unknown, 3, out _))
                    throw new InvalidOperationException("A21 partner-travel fixture was not granted.");
                int slot = lease.Inventory.GetItems(InventoryListType.Main).Single(x => x.Value.ItemId == travelItem).Key;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool readable = WeddingPartnerTravelCommitService.TryReadSource(lease.Inventory, slot, now, out int id, out var core);
                check("CHECK recognizes PVF teleport action without consuming its stack", readable && id == travelItem && lease.Inventory.CountMainItem(travelItem) == 3);
                var ticket = new WeddingPartnerTravelTicket(sid, 63351, Guid.NewGuid(), 63352, 1, 0, 15011,
                    new(11, 2, 4, 50, 60), 5000, slot, id, core);
                bool Commit(WeddingPartnerTravelTicket value, Func<bool> guard = null) => WeddingPartnerTravelCommitService.TryCommit(lease, value, 5, 3,
                    guard ?? (() => true), () => now, () => 1000);
                check("expired confirmation cannot consume its item", !Commit(ticket with { ExpiresTick = 1000 }) && lease.Inventory.CountMainItem(travelItem) == 3);
                var changed = ItemCore.FromBytes(core); changed.Count = 2; lease.Inventory.SetItem(InventoryListType.Main, (short)slot, changed);
                check("changed stack core invalidates confirmation without consumption", !Commit(ticket) && lease.Inventory.CountMainItem(travelItem) == 2);
                lease.Inventory.SetItem(InventoryListType.Main, (short)slot, ItemCore.FromBytes(core));
                changed = ItemCore.FromBytes(core); changed.ExpireTime = checked((int)(now - 1));
                lease.Inventory.SetItem(InventoryListType.Main, (short)slot, changed);
                check("expired source coupon is rejected", !WeddingPartnerTravelCommitService.TryReadSource(lease.Inventory, slot, now, out _, out _) && !Commit(ticket));
                lease.Inventory.SetItem(InventoryListType.Main, (short)slot, ItemCore.FromBytes(core));
                check("old session guard cannot consume a valid coupon", !Commit(ticket, () => false) && lease.Inventory.CountMainItem(travelItem) == 3);
                lease.Inventory.SetMainVirtualCount(0, 42);
                Sql("CREATE TRIGGER reject_partner_position BEFORE UPDATE OF town_id,area_id,pos_x,pos_y ON characters BEGIN SELECT RAISE(ABORT,'partner position rollback'); END;");
                check("position SQL failure rolls back coupon and preserves unrelated dirty wallet", !Commit(ticket)
                    && lease.Inventory.CountMainItem(travelItem) == 3 && lease.Inventory.GetMainVirtualCount(0).Count == 42
                    && Scalar("SELECT area_id FROM characters WHERE character_id=63351;") == 1);
                Sql("DROP TRIGGER reject_partner_position;");
                check("MOVE commits one coupon and the durable destination atomically", Commit(ticket) && lease.Inventory.CountMainItem(travelItem) == 2
                    && Scalar("SELECT area_id FROM characters WHERE character_id=63351;") == 4
                    && Scalar("SELECT pos_x FROM characters WHERE character_id=63351;") == 50);
                check("repeated old confirmation cannot charge a second coupon", !Commit(ticket) && lease.Inventory.CountMainItem(travelItem) == 2);
                var restored = InventoryService.LoadFromDb(connection, 63351, 63351, db);
                check("persisted state retains the consumed count and normal town anchor", restored.CountMainItem(travelItem) == 2
                    && Scalar("SELECT pos_y FROM characters WHERE character_id=63351;") == 60);
                now += 2;
                WeddingPartnerTravelCommitService.TryReadSource(lease.Inventory, slot, now, out id, out var homeCore);
                var home = WeddingSceneCatalog.Get().CoupleRoom;
                var homeTicket = ticket with { Destination = new(11, home.Town, home.Area, 474, 234), ItemCore = homeCore };
                check("temporary home travel consumes its coupon while preserving the normal return town", Commit(homeTicket)
                    && lease.Inventory.CountMainItem(travelItem) == 1 && Scalar("SELECT town_id FROM characters WHERE character_id=63351;") == 2
                    && Scalar("SELECT area_id FROM characters WHERE character_id=63351;") == 4);
                InventoryContext.Unregister(sid, 63351);
                check("released inventory generation cannot commit travel", !Commit(ticket));
            }
            finally
            {
                InventoryContext.Unregister(sid, 63351); SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }
    }
}
