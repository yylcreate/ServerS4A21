using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingRoomSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-room-" + Guid.NewGuid().ToString("N") + ".db");
            Guid sessionA = Guid.NewGuid(), sessionB = Guid.NewGuid();
            try
            {
                var catalog = WeddingRoomCatalog.Get();
                Check("A21 furniture and tree catalogs are populated from current PVF", catalog.FurniturePositions.Count > 0 && catalog.Seeds.Count > 0
                    && catalog.Trees.Count > 0 && catalog.Seeds.Values.All(catalog.Trees.ContainsKey));
                string trees = PvfArchiveAccessor.ReadText(WeddingRoomCatalog.TreePath), furniture = PvfArchiveAccessor.ReadText(WeddingRoomCatalog.FurniturePath);
                bool rejects = false;
                try { WeddingRoomCatalog.Parse(trees.Replace("[seed]", "[seed]\n-1 1"), furniture); } catch (InvalidDataException) { rejects = true; }
                Check("invalid seed references fail closed", rejects);
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63101,'wedding-room-a',''),(63102,'wedding-room-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63101,63101,'新郎',0,60),(63102,63102,'新娘',1,60);");
                long now = 1800000000;
                long pair = db.Write((c, t) => { var state = WeddingRepository.Create(c, t, 63101, 63102, now);
                    if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Married, MarriedAt = now })) throw new InvalidOperationException(); return state.PairId; });
                using var connection = db.OpenConnection();
                var a = InventoryContext.Register(sessionA, InventoryService.LoadFromDb(connection, 63101, 63101, db));
                var b = InventoryContext.Register(sessionB, InventoryService.LoadFromDb(connection, 63102, 63102, db));
                var service = new WeddingRoomCommitService(catalog, () => now, _ => 0);
                var repository = new WeddingRoomRepository(db);
                var item = catalog.FurniturePositions.First(); int position = item.Value.First();
                Sql($"INSERT INTO wedding_room_items(pair_id,container,slot,item_id,count) VALUES({pair},1,0,{item.Key},2);");
                WeddingRoomResult Commit(WeddingRoomCommand command) => service.Commit(a, b, b.CharacterId, command, () => true);
                var placed = Commit(new(WeddingRoomOperation.Move, From: 1, FromSlot: 0, FromItem: item.Key, To: 0, ToSlot: position));
                Check("placement consumes one shared decoration and is visible to both spouses", placed.Success
                    && repository.Load(a.CharacterId, now).Installed[position] == item.Key && repository.Load(b.CharacterId, now).Cargo[0]?.Count == 1);
                Check("replayed move with stale target identity is rejected", !Commit(new(WeddingRoomOperation.Move, From: 1, FromSlot: 0,
                    FromItem: item.Key, To: 0, ToSlot: position)).Success);
                var noop = Commit(new(WeddingRoomOperation.Move, From: 1, FromSlot: 0, FromItem: item.Key, To: 0, ToSlot: position, ToItem: item.Key));
                Check("replacing identical furniture preserves shared total", noop.Success && noop.Room.Cargo[0].Count == 1 && noop.Room.Installed[position] == item.Key);
                var returned = Commit(new(WeddingRoomOperation.Move, From: 0, FromSlot: position, FromItem: item.Key, To: 1, ToSlot: 0, ToItem: item.Key));
                Check("uninstall returns the item to shared storage without duplication", returned.Success && returned.Room.Cargo[0].Count == 2 && returned.Room.Installed[position] == 0);
                Check("invalid installed positions are rejected", !Commit(new(WeddingRoomOperation.Move, From: 1, FromSlot: 0, FromItem: item.Key, To: 0, ToSlot: 28)).Success);
                Check("negative deletion cannot create furniture", !Commit(new(WeddingRoomOperation.Delete, Slot: 0, ItemId: item.Key, Count: -1)).Success);
                Check("deletion checks both ID and count", !Commit(new(WeddingRoomOperation.Delete, Slot: 0, ItemId: item.Key + 1, Count: 1)).Success
                    && Commit(new(WeddingRoomOperation.Delete, Slot: 0, ItemId: item.Key, Count: 1)).Success && repository.Load(a.CharacterId, now).Cargo[0].Count == 1);
                var seed = catalog.Seeds.First(); var tree = catalog.Trees[seed.Value]; var water = tree.WaterItems.First();
                NeedGrant(a.Inventory, seed.Key, 2); NeedGrant(a.Inventory, water.Key, 20);
                int FindSlot(int id) => a.Inventory.GetItems(InventoryListType.Main).First(x => x.Value.ItemId == id).Key;
                int seedSlot = FindSlot(seed.Key), waterSlot = FindSlot(water.Key);
                Sql("CREATE TRIGGER reject_wedding_room BEFORE INSERT ON wedding_rooms BEGIN SELECT RAISE(ABORT,'room rollback'); END;");
                int beforeSeed = a.Inventory.CountMainItem(seed.Key);
                var rejected = Commit(new(WeddingRoomOperation.Plant, Slot: seedSlot, ItemId: seed.Key));
                Check("SQL failure after seed consumption restores lease and room", !rejected.Success && a.Inventory.CountMainItem(seed.Key) == beforeSeed
                    && repository.Load(a.CharacterId, now).TreeId == 0);
                Sql("DROP TRIGGER reject_wedding_room;");
                var planted = Commit(new(WeddingRoomOperation.Plant, Slot: seedSlot, ItemId: seed.Key));
                Check("plant debits the real seed and persists PVF expiry", planted.Success && planted.Room.TreeId == tree.Index
                    && planted.Room.TreeExpires == now + (long)tree.ExpireDays * 86400 && a.Inventory.CountMainItem(seed.Key) == beforeSeed - 1);
                Check("duplicate planting cannot consume another seed", !Commit(new(WeddingRoomOperation.Plant, Slot: seedSlot, ItemId: seed.Key)).Success
                    && a.Inventory.CountMainItem(seed.Key) == beforeSeed - 1);
                Check("immature harvest does not grant rewards", !Commit(new(WeddingRoomOperation.Harvest)).Success);
                int watering = 0;
                while (watering < tree.DailyWater && repository.Load(a.CharacterId, now).TreeGrowth < tree.MaximumGrowth)
                { if (!Commit(new(WeddingRoomOperation.Water, Slot: waterSlot, ItemId: water.Key)).Success) throw new InvalidOperationException("water failed"); watering++; }
                Check("water follows the PVF daily count and growth cap", repository.Load(a.CharacterId, now).WaterCount == watering
                    && repository.Load(a.CharacterId, now).TreeGrowth == Math.Min(tree.MaximumGrowth, water.Value * watering));
                Check("daily water exhaustion cannot debit another item", !Commit(new(WeddingRoomOperation.Water, Slot: waterSlot, ItemId: water.Key)).Success);
                now += 86400;
                int attempts = 0;
                while (repository.Load(a.CharacterId, now).TreeGrowth < tree.MaximumGrowth && attempts++ < 50)
                {
                    var result = Commit(new(WeddingRoomOperation.Water, Slot: waterSlot, ItemId: water.Key));
                    if (!result.Success) { now += 86400; continue; }
                }
                Check("daily reset is computed from persistent UTC+8 dates", attempts < 50 && repository.Load(a.CharacterId, now).TreeGrowth == tree.MaximumGrowth);
                var reward = tree.Rewards[0]; int beforeA = a.Inventory.CountMainItem(reward.ItemId), beforeB = b.Inventory.CountMainItem(reward.ItemId);
                Sql("CREATE TRIGGER reject_wedding_harvest BEFORE INSERT ON wedding_rooms BEGIN SELECT RAISE(ABORT,'harvest rollback'); END;");
                var failedHarvest = Commit(new(WeddingRoomOperation.Harvest));
                Check("late harvest SQL failure restores both rewards and the mature tree", !failedHarvest.Success
                    && a.Inventory.CountMainItem(reward.ItemId) == beforeA && b.Inventory.CountMainItem(reward.ItemId) == beforeB
                    && repository.Load(a.CharacterId, now).TreeGrowth == tree.MaximumGrowth);
                Sql("DROP TRIGGER reject_wedding_harvest;");
                var harvested = Commit(new(WeddingRoomOperation.Harvest));
                Check("harvest grants one PVF weighted result to both spouses atomically", harvested.Success && harvested.Reward.ItemId == reward.ItemId
                    && a.Inventory.CountMainItem(reward.ItemId) == beforeA + reward.Count && b.Inventory.CountMainItem(reward.ItemId) == beforeB + reward.Count
                    && repository.Load(a.CharacterId, now).TreeGrowth == 0);
                Check("replayed harvest cannot repeat the reward", !Commit(new(WeddingRoomOperation.Harvest)).Success && a.Inventory.CountMainItem(reward.ItemId) == beforeA + reward.Count);
                now += 86400; attempts = 0;
                while (repository.Load(a.CharacterId, now).TreeGrowth < tree.MaximumGrowth && attempts++ < 50)
                {
                    var result = Commit(new(WeddingRoomOperation.Water, Slot: waterSlot, ItemId: water.Key));
                    if (!result.Success) now += 86400;
                }
                InventoryContext.Unregister(sessionB, b.CharacterId);
                int offlineCount = b.Inventory.CountMainItem(reward.ItemId);
                var offlineHarvest = service.Commit(a, null, b.CharacterId, new(WeddingRoomOperation.Harvest), () => true);
                Check("offline spouse harvest uses atomic system mail without modifying an unowned inventory", offlineHarvest.Success
                    && b.Inventory.CountMainItem(reward.ItemId) == offlineCount && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 1);
                Check("replayed offline harvest cannot duplicate its durable mail", !service.Commit(a, null, b.CharacterId,
                    new(WeddingRoomOperation.Harvest), () => true).Success && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 1);
                now += 86400; attempts = 0;
                while (repository.Load(a.CharacterId, now).TreeGrowth < tree.MaximumGrowth && attempts++ < 50)
                {
                    var result = service.Commit(a, null, b.CharacterId, new(WeddingRoomOperation.Water, Slot: waterSlot, ItemId: water.Key), () => true);
                    if (!result.Success) now += 86400;
                }
                for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
                    a.Inventory.SetItem(InventoryListType.Main, slot, new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = 10004024, Count = 1 });
                int rolls = 0;
                var overflowService = new WeddingRoomCommitService(catalog, () => now, max => { rolls++; return 0; });
                var overflow = overflowService.Commit(a, null, b.CharacterId, new(WeddingRoomOperation.Harvest), () => true);
                Check("full bags cannot filter random rewards by rolling back an unwanted harvest", overflow.Success
                    && overflow.MailedCharacters.Contains(a.CharacterId) && overflow.MailedCharacters.Contains(b.CharacterId)
                    && repository.Load(a.CharacterId, now).TreeGrowth == 0 && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 3 && rolls == 1);
                Check("repeated overflow harvest cannot reroll or duplicate either recipient's mail", !overflowService.Commit(a, null, b.CharacterId,
                    new(WeddingRoomOperation.Harvest), () => true).Success && rolls == 1 && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 3);
                var empty = new WeddingRoomState();
                Check("A21 room wire field widths follow native readers", WeddingRoomPacketBuilder.StateBody(empty).Length == 17
                    && WeddingRoomPacketBuilder.TreeBody(empty).Length == 13 && WeddingRoomPacketBuilder.CargoBody(empty).Length == 8
                    && WeddingRoomPacketBuilder.RefreshBody(empty).Length == 4 + 68 * 11);
                var wireTree = new WeddingRoomState { TreeId = 1, TreeExpires = 2, TreeGrowth = 3, WaterCount = 4 };
                Check("A21 tree fields match the independent packed native fixture", WeddingRoomPacketBuilder.TreeBody(wireTree)
                    .SequenceEqual(Convert.FromHexString("01000000020000000300000004")));
                byte[] openFields = { 1 };
                byte[] moveFields = Convert.FromHexString("0100000100000000000000000000");
                byte[] deleteFields = Convert.FromHexString("03000100000002000000");
                byte[] treeFields = Convert.FromHexString("030001000000");
                Check("A21 manual-copy room bodies retain the opaque 14-byte stack prefix", WeddingRoomCommandParser.TryOpen(Native(openFields), out byte open) && open == 1
                    && WeddingRoomCommandParser.TryMove(Native(moveFields), out var move)
                    && move.From == 1 && move.FromSlot == 0 && move.FromItem == 1 && move.To == 0 && move.ToSlot == 0);
                Check("A21 delete reads slot, item and count after the opaque stack prefix", WeddingRoomCommandParser.TryDelete(Native(deleteFields), out var deleted)
                    && deleted.Slot == 3 && deleted.ItemId == 1 && deleted.Count == 2);
                Check("manual-copy room requests reject legacy unprefixed short fields", !WeddingRoomCommandParser.TryOpen(openFields, out _)
                    && !WeddingRoomCommandParser.TryMove(moveFields, out _) && !WeddingRoomCommandParser.TryDelete(deleteFields, out _));
                Check("A21 room requests reject invalid bounds and abnormal lengths", !WeddingRoomCommandParser.TryDelete(Native(deleteFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingRoomCommandParser.TryMove(Native(moveFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingRoomCommandParser.TryOpen(Native(openFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingRoomCommandParser.TryDelete(Native(deleteFields)[..^1], out _)
                    && !WeddingRoomCommandParser.TryMove(Native(new byte[14]), out _)
                    && !WeddingRoomCommandParser.TryOpen(Native(new byte[] { 2 }), out _));
                Check("A21 tree requests match native slot widths and empty harvest", WeddingRoomCommandParser.TryWater(
                    Native(treeFields), out var waterRequest) && waterRequest.Slot == 3 && waterRequest.ItemId == 1
                    && WeddingRoomCommandParser.TryHarvest(Array.Empty<byte>())
                    && !WeddingRoomCommandParser.TryHarvest(new byte[1]) && !WeddingRoomCommandParser.TryHarvest(Native(Array.Empty<byte>())));
                Check("A21 plant and removal requests follow their own verified senders", WeddingRoomCommandParser.TryPlant(
                    Native(treeFields), out var plantRequest) && plantRequest.Operation == WeddingRoomOperation.Plant
                    && plantRequest.Slot == 3 && plantRequest.ItemId == 1
                    && WeddingRoomCommandParser.TryRemove(Array.Empty<byte>()) && !WeddingRoomCommandParser.TryRemove(new byte[1])
                    && !WeddingRoomCommandParser.TryRemove(Native(Array.Empty<byte>())));
                Check("plant and water require the exact prefixed manual-copy body", !WeddingRoomCommandParser.TryWater(treeFields, out _)
                    && !WeddingRoomCommandParser.TryPlant(treeFields, out _)
                    && !WeddingRoomCommandParser.TryWater(Native(treeFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingRoomCommandParser.TryPlant(Native(treeFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingRoomCommandParser.TryWater(Native(treeFields)[..^1], out _)
                    && !WeddingRoomCommandParser.TryPlant(Native(treeFields)[..^1], out _));
                Check("stale owner callback cannot mutate shared room", service.Commit(a, null, b.CharacterId,
                    new(WeddingRoomOperation.Remove), () => false).Error != 0 && repository.Load(a.CharacterId, now).TreeId != 0);
                now = planted.Room.TreeExpires;
                Check("tree expiry is authoritative even after offline time", repository.Load(a.CharacterId, now).TreeId == 0 && repository.Load(a.CharacterId, now).TreeGrowth == 0);
                WeddingRoomCommand quote = null;
                foreach (int product in catalog.FurniturePositions.Keys)
                    if (WeddingShopPricing.TryResolve(product, 1, out var candidate, out bool decoration) && decoration
                        && candidate.Materials.Length == 0 && candidate.Love > 0 && candidate.Love <= WeddingCatalog.Get().MaximumLove)
                    { quote = candidate; break; }
                Check("current PVF contains a native shared-love decoration quote", quote != null);
                if (quote != null)
                {
                    int startingLove = WeddingCatalog.Get().MaximumLove;
                    Sql($"UPDATE wedding_pairs SET love={startingLove} WHERE pair_id={pair};");
                    a.Inventory.SetMainVirtualCount(0, checked(quote.Gold * 3 + 100));
                    a.Inventory.SetMainVirtualCount(2, 321);
                    int beforeGold = a.Inventory.GetMainVirtualCount(0).Count;
                    var purchased = service.Commit(a, null, b.CharacterId, quote, () => true);
                    var weddingRepository = new WeddingRepository(db);
                    Check("decoration purchase debits the single reciprocal love balance and uses shared cargo", purchased.Success
                        && weddingRepository.Load(a.CharacterId).Love == startingLove - quote.Love
                        && weddingRepository.Load(b.CharacterId).Love == startingLove - quote.Love
                        && purchased.Room.Cargo[purchased.PurchasedSlot].ItemId == quote.ItemId
                        && a.Inventory.CountMainItem(quote.ItemId) == 0 && a.Inventory.GetMainVirtualCount(0).Count == beforeGold - quote.Gold
                        && purchased.ShopWallet.Sp == 321 && a.Inventory.GetMainVirtualCount(2).Count == 321);
                    int onceLove = weddingRepository.Load(a.CharacterId).Love;
                    int onceCount = repository.Load(a.CharacterId, now).Cargo.Where(x => x?.ItemId == quote.ItemId).Sum(x => x.Count);
                    var repeated = service.Commit(a, null, b.CharacterId, quote, () => true);
                    bool canRepeat = onceLove >= quote.Love;
                    Check("repeated native purchases either pay again or preserve the previous balance", repeated.Success == canRepeat
                        && weddingRepository.Load(a.CharacterId).Love == onceLove - (canRepeat ? quote.Love : 0)
                        && repository.Load(a.CharacterId, now).Cargo.Where(x => x?.ItemId == quote.ItemId).Sum(x => x.Count) == onceCount + (canRepeat ? quote.Count : 0));
                    int loved = weddingRepository.Load(a.CharacterId).Love;
                    int bought = repository.Load(a.CharacterId, now).Cargo.Where(x => x?.ItemId == quote.ItemId).Sum(x => x.Count);
                    var forged = service.Commit(a, null, b.CharacterId, quote with { Love = 0, Gold = 0 }, () => true);
                    Check("a replay with a forged free quote cannot bypass PVF costs", !forged.Success
                        && weddingRepository.Load(a.CharacterId).Love == loved
                        && repository.Load(a.CharacterId, now).Cargo.Where(x => x?.ItemId == quote.ItemId).Sum(x => x.Count) == bought);
                    Sql($"UPDATE wedding_pairs SET love={startingLove} WHERE pair_id={pair}; DELETE FROM wedding_room_items WHERE pair_id={pair} AND container=1;");
                    for (int slot = 0; slot < 40; slot++) Sql($"INSERT INTO wedding_room_items(pair_id,container,slot,item_id,count) VALUES({pair},1,{slot},{quote.ItemId},65535);");
                    int fullGold = a.Inventory.GetMainVirtualCount(0).Count;
                    var full = service.Commit(a, null, b.CharacterId, quote, () => true);
                    Check("full shared cargo rolls back gold and both spouses' love", !full.Success
                        && a.Inventory.GetMainVirtualCount(0).Count == fullGold && weddingRepository.Load(a.CharacterId).Love == startingLove
                        && weddingRepository.Load(b.CharacterId).Love == startingLove);
                }
                WeddingRoomCommand ringQuote = null;
                foreach (var ticket in WeddingCatalog.Get().RingTickets.Skip(1))
                    if (WeddingShopPricing.TryResolve(ticket.ItemId, 1, out var candidate, out bool decoration) && !decoration
                        && candidate.Materials.Length == 0 && candidate.Love <= WeddingCatalog.Get().MaximumLove)
                    { ringQuote = candidate; break; }
                Check("current PVF exposes at least one native ring-ticket quote", ringQuote != null);
                if (ringQuote != null)
                {
                    for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++) a.Inventory.RemoveItem(InventoryListType.Main, slot);
                    a.Inventory.SetMainVirtualCount(0, checked(ringQuote.Gold + 100));
                    Sql($"UPDATE wedding_pairs SET love={WeddingCatalog.Get().MaximumLove} WHERE pair_id={pair};");
                    var boughtRing = service.Commit(a, null, b.CharacterId, ringQuote, () => true);
                    Check("ring shop returns the actual granted core and owned slot for A21 ACK", boughtRing.Success
                        && boughtRing.BuyMutation?.CoreSnapshot?.ItemId == ringQuote.ItemId && boughtRing.BuyMutation.ListType == InventoryListType.Main
                        && a.Inventory.GetItem(InventoryListType.Main, boughtRing.BuyMutation.SlotIndex)?.ItemId == ringQuote.ItemId
                        && a.Inventory.CountMainItem(ringQuote.ItemId) == 1 && WeddingShopPacketBuilder.SuccessBody(boughtRing, ringQuote.ItemId, 1, false).Length == 127);
                }
                Check("room is one canonical pair row", Scalar("SELECT COUNT(*) FROM wedding_rooms;") == 1);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sessionA, 63101); InventoryContext.Unregister(sessionB, 63102);
                SqliteConnection.ClearAllPools(); foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_ROOM: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }
        private static byte[] Native(byte[] fields) => Convert.FromHexString("9192939495969798999A9B9C9D9E").Concat(fields).ToArray();

        private static void NeedGrant(InventoryService inventory, int item, int count)
        { if (!InventoryRewardGrantService.TryCreateAndInsert(inventory, item, ItemCreateReason.Unknown, count, out _)) throw new InvalidOperationException("Test item grant failed."); }
    }
}
