using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class WeddingMoneyGiftSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            bool Rejected(Action action)
            {
                try { action(); return false; }
                catch (InvalidOperationException) { return true; }
                catch (SqliteException) { return true; }
            }
            try
            {
                Check("blessing permits empty and exactly twenty GBK bytes", WeddingMoneyGiftCommitService.ValidBlessing(Array.Empty<byte>())
                    && WeddingMoneyGiftCommitService.ValidBlessing(ClientTextEncoding.GetBytes(new string('喜', 10))));
                Check("blessing rejects oversized, NUL and truncated GBK", !WeddingMoneyGiftCommitService.ValidBlessing(new byte[21])
                    && !WeddingMoneyGiftCommitService.ValidBlessing(new byte[] { 65, 0, 66 })
                    && !WeddingMoneyGiftCommitService.ValidBlessing(new byte[] { 0x81 }));
                using (var f = new Fixture())
                {
                    var service = f.Service;
                    f.Fund(64503, 100000); f.Fund(64504, 100000); f.Fund(64505, 100000);
                    var blessing = ClientTextEncoding.GetBytes("百年好合");
                    Check("native PVF payment limits are preserved", f.Catalog.GiftMinimumGold == 10000 && f.Catalog.GiftMaximumGold == 400000000);
                    Check("uninvited character cannot pay", service.Pay(f.Lease(64530), 1, f.Start, 10000, blessing, () => true).Error == -3);
                    Check("hosts cannot pay their own ceremony", service.Pay(f.Lease(64501), 1, f.Start, 10000, blessing, () => true).Error == -3);
                    Check("stale ceremony start cannot charge", !service.Pay(f.Lease(64503), 1, f.Start - 1, 10000, blessing, () => true).Success
                        && f.Gold(64503) == 100000);
                    Check("PVF minimum and maximum reject without changes", service.Pay(f.Lease(64503), 1, f.Start, 9999, blessing, () => true).Error == -6
                        && service.Pay(f.Lease(64503), 1, f.Start, 400000001, blessing, () => true).Error == -7 && f.Gold(64503) == 100000);
                    f.Fund(64503, 9999);
                    Check("insufficient gold leaves no escrow", service.Pay(f.Lease(64503), 1, f.Start, 10000, blessing, () => true).Error == -8
                        && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts;") == 0);
                    f.Fund(64503, 100000);
                    Check("first payment only debits guest and escrows funds", service.Pay(f.Lease(64503), 1, f.Start, 10001, blessing, () => true).Success
                        && f.Gold(64503) == 89999 && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0
                        && f.Scalar("SELECT charge_gold FROM wedding_money_gifts WHERE guest_character_id=64503;") == 5000
                        && f.Scalar("SELECT net_gold FROM wedding_money_gifts WHERE guest_character_id=64503;") == 5001);
                    Check("payment atomically creates the church blessing", f.Scalar("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE author_id=64503;") == 1);
                    Check("identical replay cannot debit or duplicate blessing", service.Pay(f.Lease(64503), 1, f.Start, 10001, blessing, () => true).Replayed
                        && f.Gold(64503) == 89999 && f.Scalar("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE author_id=64503;") == 1);
                    Check("different amount and blessing cannot replace receipt", !service.Pay(f.Lease(64503), 1, f.Start, 10002, blessing, () => true).Success
                        && !service.Pay(f.Lease(64503), 1, f.Start, 10001, Array.Empty<byte>(), () => true).Success && f.Gold(64503) == 89999);
                    Check("pending guest is discoverable by deletion guard", f.Database.Read(c => WeddingMoneyGiftCommitService.HasPendingEscrow(c, null, 64503)));
                    var characters = new SqliteCharacterRepository(f.Database);
                    Check("both real soft-delete transactions reject a pending guest", Rejected(() => characters.SoftDelete(64503))
                        && Rejected(() => characters.SoftDeleteAndCompactSlots(64503, 64503, 0)));
                    Check("second payment is independent", service.Pay(f.Lease(64504), 1, f.Start, 10001, Array.Empty<byte>(), () => true).Success);
                    f.Fund(64505, 123456); // Pre-existing dirty wallet must survive a failed transaction.
                    f.Sql("CREATE TRIGGER fail_gift_comment BEFORE INSERT ON wedding_ceremony_comments WHEN NEW.author_id=64505 BEGIN SELECT RAISE(ABORT,'gift rollback'); END;");
                    Check("late blessing failure rolls back debit, receipt and frozen rewards", !service.Pay(f.Lease(64505), 1, f.Start, 20000, blessing, () => true).Success
                        && f.Gold(64505) == 123456 && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE guest_character_id=64505;") == 0
                        && f.Scalar("SELECT COUNT(*) FROM wedding_money_gift_items WHERE guest_character_id=64505;") == 0
                        && f.Scalar("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE author_id=64505;") == 0);
                    using (var c = f.Database.OpenConnection())
                        Check("rollback preserves the earlier dirty wallet in storage", InventoryService.LoadFromDb(c, 64505, 64505, f.Database).GetMainVirtualCount(0).Count == 123456);
                    f.Sql("DROP TRIGGER fail_gift_comment;");
                    Check("payment retry after rollback commits once", service.Pay(f.Lease(64505), 1, f.Start, 20000, blessing, () => true).Success
                        && f.Gold(64505) == 103456 && f.Scalar("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE author_id=64505;") == 1);
                    f.Fund(64506, 10000); int checks = 0;
                    Check("late session invalidation rolls back wallet, blessing and receipt", !service.Pay(f.Lease(64506), 1, f.Start, 10000, blessing, () => ++checks == 1).Success
                        && f.Gold(64506) == 10000 && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE guest_character_id=64506;") == 0
                        && f.Scalar("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE author_id=64506;") == 0);
                    Check("early settlement cannot release escrow", Rejected(() => f.Settle()) && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0);
                    f.Now = f.Start;
                    Check("payment window closes at start", !service.Pay(f.Lease(64506), 1, f.Start, 10000, blessing, () => true).Success);
                    f.Sql("CREATE TRIGGER fail_host_mail BEFORE INSERT ON mailbox_messages WHEN NEW.receiver_character_id=64502 BEGIN SELECT RAISE(ABORT,'mail rollback'); END;");
                    Check("mail failure rolls back all earlier mail and terminal states", Rejected(() => f.Settle())
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0 && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=0;") == 3);
                    f.Sql("DROP TRIGGER fail_host_mail; UPDATE characters SET delete_flag=1 WHERE character_id=64504;");
                    Check("missing active guest keeps the entire settlement retriable", Rejected(() => f.Settle())
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0 && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=0;") == 3);
                    f.Sql("UPDATE characters SET delete_flag=0 WHERE character_id=64504;");
                    string changed = Regex.Replace(PvfArchiveAccessor.ReadText(WeddingCatalog.PvfPath), @"(\[money gift charge\]\s*)\d+", "${1}100");
                    var changedService = new WeddingMoneyGiftCommitService(WeddingCatalog.Parse(changed), () => f.Now);
                    var mailed = f.Database.Write((c, t) => changedService.Settle(c, t, f.Database, 1, f.Start));
                    Check("settlement aggregates frozen net before equal division despite new fee configuration", mailed.Length == 5
                        && f.Scalar("SELECT SUM(gold) FROM mailbox_messages WHERE receiver_character_id=64501;") == 10001
                        && f.Scalar("SELECT SUM(gold) FROM mailbox_messages WHERE receiver_character_id=64502;") == 10001);
                    Check("guests receive the frozen tier's actual item quantities", f.Scalar("SELECT COUNT(*) FROM mailbox_attachments WHERE item_template_id=10002623 AND item_count=2;") == 3
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_attachments WHERE item_template_id=10002622 AND item_count=3;") == 3);
                    long count = f.Scalar("SELECT COUNT(*) FROM mailbox_messages;");
                    Check("settlement and opposite refund replays cannot create mail", f.Settle().Length == 0 && f.Refund().Length == 0
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == count && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=1;") == 3);
                    Check("settled same-payload replay remains noncharging", service.Pay(f.Lease(64503), 1, f.Start, 10001, blessing, () => true).Replayed && f.Gold(64503) == 89999);
                    Check("settlement releases pending deletion obligation", !f.Database.Read(c => WeddingMoneyGiftCommitService.HasPendingEscrow(c, null, 64503)));
                    characters.SoftDelete(64503);
                    Check("settled guest can be soft deleted normally", f.Scalar("SELECT delete_flag FROM characters WHERE character_id=64503;") == 1);
                }
                using (var f = new Fixture())
                {
                    f.Fund(64503, 600000000);
                    Check("large refundable payment is accepted", f.Service.Pay(f.Lease(64503), 1, f.Start, 400000000, Array.Empty<byte>(), () => true).Success);
                    f.Sql("CREATE TRIGGER fail_refund_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'refund rollback'); END;");
                    Check("refund mail failure preserves full pending balance", Rejected(() => f.Refund())
                        && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=0;") == 1 && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0);
                    f.Sql("DROP TRIGGER fail_refund_mail;");
                    Check("cancellation refunds original full amount in bounded chunks", f.Refund().SequenceEqual(new[] { 64503 })
                        && f.Scalar("SELECT SUM(gold) FROM mailbox_messages;") == 400000000 && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 4
                        && f.Scalar("SELECT MAX(gold) FROM mailbox_messages;") == 100000000 && f.Scalar("SELECT COUNT(*) FROM mailbox_attachments;") == 0);
                    Check("refunded replay is rejected without recharging", !f.Service.Pay(f.Lease(64503), 1, f.Start, 400000000, Array.Empty<byte>(), () => true).Success
                        && f.Gold(64503) == 200000000 && f.Refund().Length == 0
                        && !f.Database.Read(c => WeddingMoneyGiftCommitService.HasPayment(c, null, 1, f.Start, 64503)));
                    f.Now = f.Start;
                    Check("refunded event never later pays hosts", f.Settle().Length == 0 && f.Scalar("SELECT SUM(gold) FROM mailbox_messages WHERE receiver_character_id IN (64501,64502);") == 0);
                }
                using (var f = new Fixture())
                {
                    for (int cid = 64503; cid < 64525; cid++)
                    {
                        f.Fund(cid, 400000000);
                        if (!f.Service.Pay(f.Lease(cid), 1, f.Start, 400000000, Array.Empty<byte>(), () => true).Success)
                            throw new InvalidOperationException("Cannot prepare aggregate-overflow fixture.");
                    }
                    f.Now = f.Start;
                    Check("pooled net above Int32 is paid with Int64 aggregation", f.Settle().Length == 24
                        && f.Scalar("SELECT SUM(gold) FROM mailbox_messages WHERE receiver_character_id=64501;") == 2200000000L
                        && f.Scalar("SELECT SUM(gold) FROM mailbox_messages WHERE receiver_character_id=64502;") == 2200000000L);
                    Check("every payout mail stays within one hundred million", f.Scalar("SELECT MAX(gold) FROM mailbox_messages;") == 100000000
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id=64501;") == 22
                        && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=1;") == 22);
                }
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_MONEY_GIFT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "a21-wedding-money-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly Dictionary<int, Guid> _sessions = new();
            internal GameDatabase Database { get; }
            internal WeddingCatalog Catalog { get; } = WeddingCatalog.Get();
            internal long Now = 1800000000;
            internal long Start { get; }
            internal WeddingMoneyGiftCommitService Service { get; }
            internal Fixture()
            {
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                // Explicit isolated fixture setup; production schema is migration-owned.
                Sql(WeddingGuestSchema.Ddl + WeddingMoneyGiftSchema.Ddl + WeddingCeremonyCommentSchema.Ddl);
                for (int cid = 64501; cid <= 64530; cid++)
                    Sql($"INSERT INTO accounts(account_id,m_id,password_hash) VALUES({cid},'money-{cid}','');"
                        + $"INSERT INTO characters(character_id,account_id,name,job,level) VALUES({cid},{cid},'礼金{cid}',{(cid == 64502 ? 1 : 0)},60);");
                Start = Now + 100;
                Sql($"INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,ceremony,start_at,created_at) VALUES(1,64501,64502,2,2,{Start},{Now});"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(64501,1,0),(64502,1,1);");
                var invited = new WeddingGuestInvitationRepository(Database, () => Now).Invite(64501, 64501, 1, Start,
                    Enumerable.Range(64503, 22).ToArray(), () => true);
                if (!invited.Success) throw new InvalidOperationException("Cannot prepare guest invitations.");
                Service = new(Catalog, () => Now);
            }
            internal void Sql(string sql) => Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
            internal long Scalar(string sql) => Database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                var value = cmd.ExecuteScalar(); return value == null || value is DBNull ? 0 : Convert.ToInt64(value); });
            internal InventoryLease Lease(int cid)
            {
                if (_sessions.TryGetValue(cid, out var session) && InventoryContext.TryGetOwnedLease(session, cid, out var lease)) return lease;
                if (session == Guid.Empty) _sessions[cid] = session = Guid.NewGuid();
                using var connection = Database.OpenConnection();
                return InventoryContext.Register(session, InventoryService.LoadFromDb(connection, cid, cid, Database));
            }
            internal void Fund(int cid, int gold) => Lease(cid).Inventory.SetMainVirtualCount(0, gold);
            internal int Gold(int cid) => Lease(cid).Inventory.GetMainVirtualCount(0).Count;
            internal int[] Settle() => Database.Write((c, t) => Service.Settle(c, t, Database, 1, Start));
            internal int[] Refund() => Database.Write((c, t) => Service.Refund(c, t, Database, 1, Start));
            public void Dispose()
            {
                foreach (var session in _sessions) InventoryContext.Unregister(session.Value, session.Key);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
