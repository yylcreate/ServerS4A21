using DfoServer.Game.Inventory;
using DfoServer.Game.Shop;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.CeraShop;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class CeraShopCommunityItemSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-community-shop-" + Guid.NewGuid().ToString("N") + ".db");
            var sessions = new Dictionary<int, Guid> { [64601] = Guid.NewGuid(), [64602] = Guid.NewGuid() };
            try
            {
                int ticket = WeddingCatalog.Get().PaidTicket.ItemId;
                Check("reported A21 discounted wedding commodity resolves its own PVF price",
                    CeraShopProductCatalog.TryResolve(102401, out var discounted) && discounted.ItemTemplateId == ticket
                    && discounted.Section == "community item" && discounted.Count == 1 && discounted.GoldPrice == 0 && discounted.CoinPrice == 14888);
                Check("reported A21 regular wedding commodity retains its distinct price",
                    CeraShopProductCatalog.TryResolve(102463, out var regular) && regular.ItemTemplateId == ticket
                    && regular.Section == "community item" && regular.Count == 1 && regular.GoldPrice == 0 && regular.CoinPrice == 32740);
                Check("existing standard commodity still resolves independently",
                    CeraShopProductCatalog.TryResolve(100050, out var ordinary) && ordinary.Section == "item"
                    && ordinary.ItemTemplateId == 37 && ordinary.Count == 1 && ordinary.CoinPrice == 80);
                byte[] captured = Convert.FromHexString("0000010000FFFF019001000000000000000000");
                Check("actual failed purchase body names the discounted commodity",
                    captured.Length == 19 && CeraShopPurchaseRequest.TryParse(captured, out var request) && request.ProductId == 102401
                    && request.PaymentMode == 0 && !request.CouponSelected);
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) =>
                { using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c =>
                { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash,cera) VALUES(64601,'community-funded','',500000),(64602,'community-empty','',0);"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(64601,64601,'community-a',11,80),(64602,64602,'community-b',0,86);");
                InventoryLease Lease(int cid)
                {
                    if (InventoryContext.TryGetOwnedLease(sessions[cid], cid, out var current)) return current;
                    using var c = db.OpenConnection();
                    return InventoryContext.Register(sessions[cid], InventoryService.LoadFromDb(c, cid, cid, db));
                }
                int StoredTickets(int cid) => db.Read(c => InventoryService.LoadFromDb(c, cid, cid, db).CountMainItem(ticket));
                bool Buy(int cid, int commodity, out InventoryMutationResult result, out CeraShopPurchaseFailure failure)
                    => CeraShopRuntimePurchaseCommitService.TryPurchase(Lease(cid), cid, commodity,
                        0, 0, 0, -1, new CeraShopPurchaseOptions(), null, out result, out failure);
                Check("discounted wedding ticket uses the owned purchase transaction and reaches storage",
                    Buy(64601, 102401, out var first, out _) && first != null && first.ItemTemplateId == ticket
                    && StoredTickets(64601) == 1 && Scalar("SELECT cera FROM accounts WHERE account_id=64601;") == 500000 - discounted.CoinPrice);
                int remaining = 500000 - discounted.CoinPrice - regular.CoinPrice;
                Check("regular wedding ticket charges its own commodity and stacks the same real item",
                    Buy(64601, 102463, out var second, out _) && second != null && second.ItemTemplateId == ticket
                    && StoredTickets(64601) == 2 && Scalar("SELECT cera FROM accounts WHERE account_id=64601;") == remaining);
                Check("insufficient points do not grant a ticket or alter the wallet",
                    !Buy(64602, 102463, out _, out var insufficient) && insufficient == CeraShopPurchaseFailure.InsufficientCera
                    && StoredTickets(64602) == 0 && Scalar("SELECT cera FROM accounts WHERE account_id=64602;") == 0);
                Check("unknown commodities cannot become free purchases",
                    !Buy(64601, int.MaxValue, out _, out _) && StoredTickets(64601) == 2
                    && Scalar("SELECT cera FROM accounts WHERE account_id=64601;") == remaining);
                Sql("CREATE TRIGGER reject_community_delivery BEFORE UPDATE ON character_inventory_items WHEN NEW.character_id=64601 BEGIN SELECT RAISE(ABORT,'community ticket delivery rollback'); END;");
                Check("late inventory persistence failure rolls back points and ticket together",
                    !Buy(64601, 102463, out _, out _) && StoredTickets(64601) == 2
                    && Scalar("SELECT cera FROM accounts WHERE account_id=64601;") == remaining);
                Sql("DROP TRIGGER reject_community_delivery;");
                Check("purchase can retry through the recovered owned lease after rollback",
                    Buy(64601, 102463, out _, out _) && StoredTickets(64601) == 3
                    && Scalar("SELECT cera FROM accounts WHERE account_id=64601;") == remaining - regular.CoinPrice);
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                foreach (var entry in sessions) InventoryContext.Unregister(entry.Value, entry.Key);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"CERASHOP_COMMUNITY_ITEM: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }
    }
}
