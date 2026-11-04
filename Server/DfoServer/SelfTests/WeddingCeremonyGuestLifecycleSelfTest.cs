using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class WeddingCeremonyGuestLifecycleSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            bool Rejects(Action action)
            {
                try { action(); return false; }
                catch (SqliteException) { return true; }
                catch (InvalidOperationException) { return true; }
            }
            try
            {
                string quote = PvfArchiveAccessor.ReadText(WeddingCatalog.PvfPath);
                quote = Regex.Replace(quote, @"(\[ceremony\]\s*0\s+)\d+", "${1}543210");
                quote = Regex.Replace(quote, @"(\[money gift charge\]\s*)\d+", "${1}37");
                var originalQuote = WeddingCatalog.Parse(quote);
                using (var f = new Fixture(originalQuote))
                {
                    var eventOne = f.Reserve(0, Fixture.Right);
                    long start = eventOne.StartAt;
                    Check("real reservation debits its actual right-side payer and records that receipt",
                        f.Gold(Fixture.Right) == Fixture.InitialGold - 543210 && f.Gold(Fixture.Left) == Fixture.InitialGold
                        && f.Scalar("SELECT payer_character_id FROM wedding_reservation_receipts;") == Fixture.Right
                        && f.Scalar("SELECT gold FROM wedding_reservation_receipts;") == 543210);
                    f.PayGuest(eventOne);
                    Check("actual guest payment stores frozen fee and event-scoped blessing", f.Gold(Fixture.Guest) == Fixture.InitialGold - 10001
                        && f.Scalar("SELECT charge_percent FROM wedding_money_gifts;") == 37
                        && f.Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id={eventOne.PairId} AND ceremony_start_at={start};") == 1);
                    var cancel = new WeddingCeremonyCancellationService(f.Database);
                    var recovery = new WeddingCeremonyRecoveryService(f.Database);
                    var unchanged = f.State;
                    Check("early cancellation is a no-op", cancel.Cancel(Fixture.Right, eventOne.PairId, start, start - 1).MailedCharacters.Length == 0 && f.State == unchanged);
                    Check("wrong pair and start cannot cancel the real reservation",
                        cancel.Cancel(Fixture.Right, eventOne.PairId + 1, start, start + 1).MailedCharacters.Length == 0
                        && cancel.Cancel(Fixture.Right, eventOne.PairId, start + 1, start + 1).MailedCharacters.Length == 0 && f.State == unchanged);
                    Check("early and wrong-event recovery cannot advance or refund",
                        recovery.Advance(Fixture.Left, eventOne.PairId, start, start - 1) == unchanged
                        && recovery.Advance(Fixture.Left, eventOne.PairId, start + 1, start + 100) == unchanged
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0);
                    f.Now = start + 1;
                    var result = cancel.Cancel(Fixture.Right, eventOne.PairId, start, f.Now);
                    Check("due unstarted reservation becomes reciprocal engagement and releases its church",
                        result.State.Status == WeddingStatus.Engaged && f.State.Status == WeddingStatus.Engaged
                        && f.State.StartAt == 0 && f.State.Room == 0 && f.State.ChannelId == 0
                        && WeddingRules.IsReciprocal(f.State, f.PartnerState));
                    Check("cancellation refunds the full guest amount and actual frozen payer cost",
                        result.MailedCharacters.OrderBy(x => x).SequenceEqual(new[] { Fixture.Right, Fixture.Guest })
                        && f.MailGold(Fixture.Guest) == 10001 && f.MailGold(Fixture.Right) == 543210 && f.MailGold(Fixture.Left) == 0
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 2
                        && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 1);
                    Check("refund does not rewrite an online wallet or recalculate the later PVF quote",
                        originalQuote.Ceremonies[0].Gold != WeddingCatalog.Get().Ceremonies[0].Gold
                        && originalQuote.GiftChargePercent != WeddingCatalog.Get().GiftChargePercent
                        && f.Gold(Fixture.Right) == Fixture.InitialGold - 543210 && f.Gold(Fixture.Guest) == Fixture.InitialGold - 10001);
                    long firstMails = f.Scalar("SELECT COUNT(*) FROM mailbox_messages;");
                    Check("cancel and recover replays never refund twice",
                        cancel.Cancel(Fixture.Left, eventOne.PairId, start, f.Now).MailedCharacters.Length == 0
                        && recovery.Advance(Fixture.Left, eventOne.PairId, start, f.Now).Status == WeddingStatus.Engaged
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == firstMails);

                    f.Now = start + Math.Max(1, (long)originalQuote.WeddingCooldownDays * 86400) + 1;
                    var eventTwo = f.Reserve(0, Fixture.Left);
                    f.PayGuest(eventTwo);
                    Check("same pair can reserve again with independent payer and event identities",
                        eventTwo.PairId == eventOne.PairId && eventTwo.StartAt != start
                        && f.Scalar("SELECT COUNT(*) FROM wedding_reservation_receipts;") == 2
                        && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts;") == 2
                        && f.Scalar($"SELECT payer_character_id FROM wedding_reservation_receipts WHERE ceremony_start_at={eventTwo.StartAt};") == Fixture.Left
                        && f.Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE ceremony_start_at={eventTwo.StartAt};") == 1);
                    var secondBefore = f.State;
                    Check("late callbacks from the previous event cannot touch the new escrow",
                        cancel.Cancel(Fixture.Right, eventOne.PairId, start, eventTwo.StartAt + 1).MailedCharacters.Length == 0
                        && recovery.Advance(Fixture.Left, eventOne.PairId, start, eventTwo.StartAt + 1) == secondBefore
                        && f.Scalar($"SELECT state FROM wedding_money_gifts WHERE ceremony_start_at={eventTwo.StartAt};") == 0
                        && f.Scalar($"SELECT refunded FROM wedding_reservation_receipts WHERE ceremony_start_at={eventTwo.StartAt};") == 0);
                    f.Now = eventTwo.StartAt + WeddingCatalog.Get().CeremonyEndSeconds + 10;
                    Check("offline Reserved recovery cancels instead of inventing attendance and marriage",
                        recovery.Advance(Fixture.Left, eventTwo.PairId, eventTwo.StartAt, f.Now).Status == WeddingStatus.Engaged
                        && f.State.MarriedAt == 0 && f.MailGold(Fixture.Guest) == 20002
                        && f.MailGold(Fixture.Left) == 543210 && f.Scalar("SELECT COUNT(*) FROM wedding_money_gifts WHERE state=2;") == 2);
                }

                using (var f = new Fixture())
                {
                    var reservation = f.Reserve(0, Fixture.Right); f.PayGuest(reservation); f.Now = reservation.StartAt;
                    var before = f.State; var cancellation = new WeddingCeremonyCancellationService(f.Database);
                    f.Sql($"CREATE TRIGGER fail_late_reservation_refund BEFORE INSERT ON mailbox_messages WHEN NEW.receiver_character_id={Fixture.Right} BEGIN SELECT RAISE(ABORT,'late payer mail failure'); END;");
                    Check("late payer mail failure rolls back already prepared guest refunds and every state",
                        Rejects(() => cancellation.Cancel(Fixture.Left, reservation.PairId, reservation.StartAt, f.Now))
                        && f.State == before && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 0
                        && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 0);
                    f.Sql("DROP TRIGGER fail_late_reservation_refund; CREATE TRIGGER fail_cancel_pair BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'late pair failure'); END;");
                    Check("late pair failure also rolls back mail, gift and payer receipts",
                        Rejects(() => cancellation.Cancel(Fixture.Left, reservation.PairId, reservation.StartAt, f.Now))
                        && f.State == before && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == 0
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 0
                        && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 0);
                    f.Sql("DROP TRIGGER fail_cancel_pair;");
                    Check("the same event retries successfully after cancellation rollback",
                        cancellation.Cancel(Fixture.Left, reservation.PairId, reservation.StartAt, f.Now).State.Status == WeddingStatus.Engaged
                        && f.MailGold(Fixture.Guest) == 10001 && f.MailGold(Fixture.Right) == f.Catalog.Ceremonies[0].Gold
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 2 && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 1);
                }

                using (var f = new Fixture())
                {
                    byte kind = f.Catalog.PaidCeremonyType;
                    f.GiveTicket(Fixture.Right);
                    var reservation = f.Reserve(kind, Fixture.Right); f.PayGuest(reservation);
                    Check("luxury receipt records the actual consumed ticket", f.Lease(Fixture.Right).Inventory.CountMainItem(f.Catalog.PaidTicket.ItemId) == 0
                        && f.Scalar("SELECT ticket_item_id FROM wedding_reservation_receipts;") == f.Catalog.PaidTicket.ItemId
                        && f.Scalar("SELECT ticket_count FROM wedding_reservation_receipts;") == f.Catalog.PaidTicket.Count);
                    f.Now = reservation.StartAt;
                    var result = new WeddingCeremonyCancellationService(f.Database).Cancel(Fixture.Left, reservation.PairId, reservation.StartAt, f.Now);
                    Check("luxury cancellation returns the ticket only to its actual payer",
                        result.State.Status == WeddingStatus.Engaged
                        && f.Scalar($"SELECT COALESCE(SUM(a.item_count),0) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.receiver_character_id={Fixture.Right} AND a.item_template_id={f.Catalog.PaidTicket.ItemId};") == f.Catalog.PaidTicket.Count
                        && f.Scalar($"SELECT COUNT(*) FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id WHERE m.receiver_character_id={Fixture.Left};") == 0
                        && f.MailGold(Fixture.Guest) == 10001);
                }

                using (var f = new Fixture())
                {
                    var reservation = f.Reserve(0, Fixture.Right); f.PayGuest(reservation);
                    // A v42 reservation has no authoritative payer receipt.
                    f.Sql("DELETE FROM wedding_reservation_receipts;"); f.Now = reservation.StartAt;
                    Check("legacy reservation without receipt refunds guests but never guesses a payer",
                        new WeddingCeremonyRecoveryService(f.Database).Advance(Fixture.Left, reservation.PairId, reservation.StartAt, f.Now).Status == WeddingStatus.Engaged
                        && f.MailGold(Fixture.Guest) == 10001 && f.MailGold(Fixture.Right) == 0 && f.MailGold(Fixture.Left) == 0);
                }

                using (var f = new Fixture())
                {
                    var reservation = f.Reserve(0, Fixture.Right); f.PayGuest(reservation); f.Now = reservation.StartAt;
                    var start = new WeddingCommitService(f.Catalog, () => f.Now).Commit(f.Lease(Fixture.Left), f.Lease(Fixture.Right), Fixture.Right, new(3), () => true);
                    Check("accepted real start settles guest escrow with ceremony transition", start.Success && f.State.Status == WeddingStatus.Ceremony
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 1 && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 0);
                    long startedMails = f.Scalar("SELECT COUNT(*) FROM mailbox_messages;");
                    var recovery = new WeddingCeremonyRecoveryService(f.Database); f.GoOffline();
                    long finish = reservation.StartAt + f.Catalog.CeremonyEndSeconds;
                    Check("persisted ceremony still waits for its actual end", recovery.Advance(Fixture.Left, reservation.PairId, reservation.StartAt, finish - 1).Status == WeddingStatus.Ceremony
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == startedMails);
                    f.Sql($"CREATE TRIGGER fail_second_ceremony_gift BEFORE INSERT ON mailbox_messages WHEN NEW.receiver_character_id={Fixture.Right} AND NEW.title='婚礼赠礼' BEGIN SELECT RAISE(ABORT,'second ceremony gift failure'); END;");
                    Check("offline gift failure preserves started ceremony and previous financial settlement",
                        Rejects(() => recovery.Advance(Fixture.Left, reservation.PairId, reservation.StartAt, finish))
                        && f.State.Status == WeddingStatus.Ceremony && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == startedMails
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 1 && f.Scalar("SELECT refunded FROM wedding_reservation_receipts;") == 0);
                    f.Sql("DROP TRIGGER fail_second_ceremony_gift;");
                    var married = recovery.Advance(Fixture.Left, reservation.PairId, reservation.StartAt, finish);
                    Check("offline recovery completes only a previously accepted ceremony", married.Status == WeddingStatus.Married
                        && married.StartAt == 0 && married.Room == 0 && married.MarriedAt == finish
                        && WeddingRules.IsReciprocal(f.State, f.PartnerState)
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages WHERE title='婚礼赠礼';") == 2);
                    long finalMails = f.Scalar("SELECT COUNT(*) FROM mailbox_messages;");
                    Check("finished recovery and cancellation cannot replay gifts or refund settled money",
                        recovery.Advance(Fixture.Right, reservation.PairId, reservation.StartAt, finish + 1).Status == WeddingStatus.Married
                        && new WeddingCeremonyCancellationService(f.Database).Cancel(Fixture.Left, reservation.PairId, reservation.StartAt, finish + 1).MailedCharacters.Length == 0
                        && f.Scalar("SELECT COUNT(*) FROM mailbox_messages;") == finalMails
                        && f.Scalar("SELECT state FROM wedding_money_gifts;") == 1);
                }
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_GUEST_LIFECYCLE: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }

        private sealed class Fixture : IDisposable
        {
            internal const int Left = 65101, Right = 65102, Guest = 65103, InitialGold = 10000000;
            private readonly string _path = Path.Combine(Path.GetTempPath(), "a21-wedding-lifecycle-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly Dictionary<int, Guid> _sessions = new();
            internal GameDatabase Database { get; }
            internal WeddingCatalog Catalog { get; }
            internal long Now = 1800000000;
            internal WeddingState State => new WeddingRepository(Database).Load(Left);
            internal WeddingState PartnerState => new WeddingRepository(Database).Load(Right);
            internal Fixture(WeddingCatalog catalog = null)
            {
                Catalog = catalog ?? WeddingCatalog.Get(); Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                for (int cid = Left; cid <= Guest; cid++)
                    Sql($"INSERT INTO accounts(account_id,m_id,password_hash) VALUES({cid},'lifecycle-{cid}','');"
                        + $"INSERT INTO characters(character_id,account_id,name,job,level) VALUES({cid},{cid},'流程{cid}',{(cid == Right ? 1 : 0)},60);");
                foreach (int cid in new[] { Left, Right, Guest }) Lease(cid).Inventory.SetMainVirtualCount(0, InitialGold);
                var engaged = new WeddingCommitService(Catalog, () => Now).Commit(Lease(Left), Lease(Right), Right, new(1), () => true);
                if (!engaged.Success) throw new InvalidOperationException("Cannot engage lifecycle fixture: " + engaged.Error);
                foreach (int cid in new[] { Left, Right })
                foreach (int quest in Catalog.RequiredQuests)
                    Sql($"INSERT INTO character_quest_completions(character_id,quest_id,completion_value) VALUES({cid},{quest},1);");
            }
            internal WeddingState Reserve(byte kind, int payer)
            {
                int peer = payer == Left ? Right : Left;
                byte room = WeddingSceneCatalog.Get().CeremonyRooms[0].Area;
                var result = new WeddingCommitService(Catalog, () => Now).Commit(Lease(payer), Lease(peer), peer, new(2, kind, 0, room, 1), () => true);
                if (!result.Success) throw new InvalidOperationException("Cannot reserve lifecycle fixture: " + result.Error);
                return State;
            }
            internal void PayGuest(WeddingState reservation)
            {
                var invitation = new WeddingGuestInvitationRepository(Database, () => Now).Invite(Left, Left,
                    reservation.PairId, reservation.StartAt, new[] { Guest }, () => true);
                var payment = new WeddingMoneyGiftCommitService(Catalog, () => Now).Pay(Lease(Guest), reservation.PairId,
                    reservation.StartAt, 10001, ClientTextEncoding.GetBytes("永结同心"), () => true);
                if (!invitation.Success || !payment.Success) throw new InvalidOperationException("Cannot pay lifecycle guest: " + invitation.Error + "/" + payment.Error);
            }
            internal void GiveTicket(int cid)
            {
                if (!InventoryRewardGrantService.TryCreateAndInsert(Lease(cid).Inventory, Catalog.PaidTicket.ItemId,
                    ItemCreateReason.Unknown, Catalog.PaidTicket.Count, out _)) throw new InvalidOperationException("Cannot give the actual PVF ceremony ticket.");
            }
            internal InventoryLease Lease(int cid)
            {
                if (_sessions.TryGetValue(cid, out var session) && InventoryContext.TryGetOwnedLease(session, cid, out var lease)) return lease;
                if (session == Guid.Empty) _sessions[cid] = session = Guid.NewGuid();
                using var c = Database.OpenConnection(); return InventoryContext.Register(session, InventoryService.LoadFromDb(c, cid, cid, Database));
            }
            internal int Gold(int cid) => Lease(cid).Inventory.GetMainVirtualCount(0).Count;
            internal long MailGold(int cid) => Scalar($"SELECT COALESCE(SUM(gold),0) FROM mailbox_messages WHERE receiver_character_id={cid};");
            internal void Sql(string sql) => Database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
            internal long Scalar(string sql) => Database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                var value = cmd.ExecuteScalar(); return value == null || value is DBNull ? 0 : Convert.ToInt64(value); });
            internal void GoOffline()
            { foreach (var pair in _sessions) InventoryContext.Unregister(pair.Value, pair.Key); _sessions.Clear(); }
            public void Dispose()
            {
                GoOffline(); SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
