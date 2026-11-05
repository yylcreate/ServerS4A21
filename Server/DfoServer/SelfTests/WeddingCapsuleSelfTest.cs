using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class WeddingCapsuleSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            bool Bad(string text) { try { WeddingCapsuleCatalog.Parse(text); return false; } catch (InvalidDataException) { return true; } }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-capsule-" + Guid.NewGuid().ToString("N") + ".db");
            var sidA = Guid.NewGuid(); var sidB = Guid.NewGuid();
            try
            {
                var catalog = WeddingCapsuleCatalog.Get();
                Check("native capsule commands accept only the empty body", WeddingCapsuleCommandParser.TryEmpty(Array.Empty<byte>())
                    && WeddingCapsuleCommandParser.TryEmpty(null) && !WeddingCapsuleCommandParser.TryEmpty(new byte[1])
                    && !WeddingCapsuleCommandParser.TryEmpty(new byte[] { 0x91, 0x27, 0xBD, 0x46, 0xE3, 0x58, 0x6A, 0xC2, 0xF7, 0x39, 0x84, 0xD6, 0x15, 0xAB }));
                foreach (var command in new[] { CmdPacketTypeA21.MAKE_COUPLE_GROWTHCAPSULE,
                    CmdPacketTypeA21.GIVE_COUPLE_GROWTHCAPSULE, CmdPacketTypeA21.TAKE_COUPLE_GROWTHCAPSULE,
                    CmdPacketTypeA21.COUPLE_TREE_HARVEST, CmdPacketTypeA21.COUPLE_TREE_REMOVE })
                {
                    // Construct the captured sender's header-only frame
                    // independently of all production packet builders.
                    var wire = new byte[14]; wire[0] = 1;
                    BitConverter.GetBytes((ushort)command).CopyTo(wire, 1);
                    BitConverter.GetBytes(14u).CopyTo(wire, 3);
                    var processor = new FlexiblePacketProcessor(); var client = Guid.NewGuid();
                    processor.SetClientPacketStructure(client, new GamePacketHeader());
                    var first = processor.ProcessReceivedData(client, wire.AsSpan(0, 7).ToArray(), 7);
                    var complete = processor.ProcessReceivedData(client, wire.AsSpan(7).ToArray(), 7);
                    bool accepted = complete.Count == 1 && complete[0].BodyData == null
                        && complete[0].GetHeader<GamePacketHeader>().type == (ushort)command
                        && (command == CmdPacketTypeA21.COUPLE_TREE_HARVEST ? WeddingRoomCommandParser.TryHarvest(complete[0].BodyData)
                            : command == CmdPacketTypeA21.COUPLE_TREE_REMOVE ? WeddingRoomCommandParser.TryRemove(complete[0].BodyData)
                            : WeddingCapsuleCommandParser.TryEmpty(complete[0].BodyData));
                    Check($"real header-only {command} is framed as null body and accepted", first.Count == 0 && accepted);
                    var padded = new byte[28]; wire.CopyTo(padded, 0); BitConverter.GetBytes(28u).CopyTo(padded, 3);
                    for (int i = 14; i < padded.Length; i++) padded[i] = (byte)(0x91 + i);
                    var fake = processor.ProcessReceivedData(client, padded, padded.Length);
                    Check($"{command} does not reinterpret a 14-byte body as an empty frame", fake.Count == 1 && fake[0].BodyData?.Length == 14
                        && (command == CmdPacketTypeA21.COUPLE_TREE_HARVEST ? !WeddingRoomCommandParser.TryHarvest(fake[0].BodyData)
                            : command == CmdPacketTypeA21.COUPLE_TREE_REMOVE ? !WeddingRoomCommandParser.TryRemove(fake[0].BodyData)
                            : !WeddingCapsuleCommandParser.TryEmpty(fake[0].BodyData)));
                }
                var body = WeddingCapsulePacketBuilder.StateBody(new(new(12345, 2, 3), true));
                Check("A21 capsule notification preserves native own and pending order", body.Length == 7
                    && BitConverter.ToUInt32(body) == 12345 && body[4] == 2 && body[5] == 3 && body[6] == 1);
                var make = WeddingCapsulePacketBuilder.Result(CmdPacketTypeA21.MAKE_COUPLE_GROWTHCAPSULE,
                    WeddingCapsuleAction.Make, 0, new(9, 2, 3));
                var give = WeddingCapsulePacketBuilder.Result(CmdPacketTypeA21.GIVE_COUPLE_GROWTHCAPSULE,
                    WeddingCapsuleAction.Give, 0, new(9, 2, 3));
                var take = WeddingCapsulePacketBuilder.Result(CmdPacketTypeA21.TAKE_COUPLE_GROWTHCAPSULE,
                    WeddingCapsuleAction.Take, 0, new(9, 2, 3));
                Check("native capsule ACK success tails have exact reader lengths", make.Length == 25 && make[15] == 1
                    && BitConverter.ToUInt32(make, 20) == 9 && make[24] == 2 && give.Length == 21 && give[20] == 2
                    && take.Length == 21 && take[20] == 3);
                Check("capsule failure ACK omits success-only stock tail", WeddingCapsulePacketBuilder.Result(
                    CmdPacketTypeA21.MAKE_COUPLE_GROWTHCAPSULE, WeddingCapsuleAction.Make, -4).Length == 20);
                string sample = "[max own count]\n3\n[max taken count]\n4\n[gage]\n7\n[max gage]\n21\n[capsule id]\n10002910\n";
                var configured = WeddingCapsuleCatalog.Parse(sample);
                Check("capsule catalog honors the five A21 PVF fields", configured == new WeddingCapsuleCatalog(3, 4, 7, 21, 10002910));
                Check("capsule catalog rejects duplicate and overflowing counts", Bad(sample + "[max own count]\n2\n")
                    && Bad(sample.Replace("\n3\n", "\n256\n")));
                Check("capsule catalog rejects negative and impossible gauge", Bad(sample.Replace("\n7\n", "\n-7\n"))
                    && Bad(sample.Replace("\n7\n", "\n22\n")));
                Check("capsule catalog rejects missing item and malformed scalar", Bad(sample.Replace("[capsule id]\n10002910\n", ""))
                    && Bad(sample.Replace("\n7\n", "\n7 8\n")));
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63101,'capsule-a',''),(63102,'capsule-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63101,63101,'胶囊甲',0,60),(63102,63102,'胶囊乙',1,60);"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(1,63101,63102,4,1800000000,1800000000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63101,1,0),(63102,1,1);");
                using var connection = db.OpenConnection();
                var a = InventoryContext.Register(sidA, InventoryService.LoadFromDb(connection, 63101, 63101, db));
                var b = InventoryContext.Register(sidB, InventoryService.LoadFromDb(connection, 63102, 63102, db));
                var repository = new WeddingCapsuleRepository(db);
                var service = new WeddingCapsuleCommitService(catalog);
                Check("new schema creates capsule storage without runtime DDL", Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='wedding_capsules';") == 1);
                Check("a new pair starts with empty stocks and gauge", repository.LoadProjection(63101) == new WeddingCapsuleProjection(new(), false));
                Check("make rejects empty energy without inventing an item", service.Commit(a, 63102, WeddingCapsuleAction.Make, () => true).Error == -4
                    && a.Inventory.CountMainItem(catalog.ItemId) == 0);
                void Clear(int cid) => db.Write((c, t) => WeddingCapsuleRepository.AccumulateAcceptedClear(c, t, cid, catalog));
                Clear(63101);
                Check("accepted recommended clear uses configured gauge", repository.LoadProjection(63101).State.Progress == catalog.GaugePerClear);
                for (int i = 0; i < catalog.MaximumGauge / catalog.GaugePerClear + 2; i++) Clear(63101);
                Check("progress saturates at configured maximum", repository.LoadProjection(63101).State.Progress == catalog.MaximumGauge);
                var result = service.Commit(a, 63102, WeddingCapsuleAction.Make, () => true);
                Check("make converts a full gauge into gift stock instead of a bag item", result.Success
                    && result.Actor.Progress == 0 && result.Actor.Own == 1 && a.Inventory.CountMainItem(catalog.ItemId) == 0);
                Check("duplicate make cannot consume gauge twice", service.Commit(a, 63102, WeddingCapsuleAction.Make, () => true).Error == -4);
                InventoryContext.Unregister(sidB, 63102);
                result = service.Commit(a, 63102, WeddingCapsuleAction.Give, () => true);
                Check("give atomically moves all stock to an offline spouse", result.Success && result.Actor.Own == 0 && result.Partner.Taken == 1
                    && repository.LoadProjection(63101).SpouseHasPending && repository.LoadProjection(63102).State.Taken == 1);
                Check("duplicate give cannot duplicate pending gifts", service.Commit(a, 63102, WeddingCapsuleAction.Give, () => true).Error == -4
                    && repository.LoadProjection(63102).State.Taken == 1);
                b = InventoryContext.Register(sidB, InventoryService.LoadFromDb(connection, 63102, 63102, db));
                Sql("CREATE TRIGGER reject_capsule_take BEFORE UPDATE ON wedding_capsules WHEN OLD.character_id=63102 BEGIN SELECT RAISE(ABORT,'capsule rollback test'); END;");
                result = service.Commit(b, 63101, WeddingCapsuleAction.Take, () => true);
                Check("SQL failure after item grant restores bag and pending stock", !result.Success && b.Inventory.CountMainItem(catalog.ItemId) == 0
                    && repository.LoadProjection(63102).State.Taken == 1);
                Sql("DROP TRIGGER reject_capsule_take;");
                result = service.Commit(b, 63101, WeddingCapsuleAction.Take, () => true);
                Check("take grants the configured item and clears pending atomically", result.Success && result.Granted == 1
                    && b.Inventory.CountMainItem(catalog.ItemId) == 1 && repository.LoadProjection(63102).State.Taken == 0
                    && !repository.LoadProjection(63101).SpouseHasPending);
                Check("repeated take cannot repeat the item reward", !service.Commit(b, 63101, WeddingCapsuleAction.Take, () => true).Success
                    && b.Inventory.CountMainItem(catalog.ItemId) == 1);
                var reloaded = InventoryService.LoadFromDb(connection, 63102, 63102, db);
                Check("relogin observes both reward and cleared pending", reloaded.CountMainItem(catalog.ItemId) == 1
                    && repository.LoadProjection(63102).State.Taken == 0);
                Sql($"UPDATE wedding_capsules SET progress={catalog.MaximumGauge},own_count={catalog.MaximumOwn} WHERE character_id=63101;");
                Check("make capacity failure preserves full gauge", service.Commit(a, 63102, WeddingCapsuleAction.Make, () => true).Error == -5
                    && repository.LoadProjection(63101).State.Progress == catalog.MaximumGauge);
                Sql($"UPDATE wedding_capsules SET own_count=1 WHERE character_id=63101; UPDATE wedding_capsules SET taken_count={catalog.MaximumTaken} WHERE character_id=63102;");
                Check("give capacity failure preserves both stocks", service.Commit(a, 63102, WeddingCapsuleAction.Give, () => true).Error == -6
                    && repository.LoadProjection(63101).State.Own == 1 && repository.LoadProjection(63102).State.Taken == catalog.MaximumTaken);
                Check("stale session cannot make or transfer stocks", !service.Commit(a, 63102, WeddingCapsuleAction.Make, () => false).Success
                    && repository.LoadProjection(63101).State.Own == 1);
                Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1; DELETE FROM wedding_members;"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(2,63101,63102,4,1800001000,1800001000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63101,2,0),(63102,2,1);");
                Check("remarriage starts fresh without inheriting historical stocks", repository.LoadProjection(63101).State == new WeddingCapsuleState()
                    && repository.LoadProjection(63102).State == new WeddingCapsuleState());
                Check("historical marriage stocks cannot be claimed through the new pair", !service.Commit(b, 63101, WeddingCapsuleAction.Take, () => true).Success
                    && b.Inventory.CountMainItem(catalog.ItemId) == 1);
                InventoryContext.Unregister(sidA, 63101);
                Check("released inventory generation cannot mutate capsule storage", !service.Commit(a, 63102, WeddingCapsuleAction.Make, () => true).Success
                    && repository.LoadProjection(63101).State == new WeddingCapsuleState());
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sidA, 63101); InventoryContext.Unregister(sidB, 63102);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_CAPSULE: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }
    }
}
