using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingMoneyGiftResult(int Error, bool Replayed = false)
    { internal bool Success => Error == 0; }

    internal sealed class WeddingMoneyGiftCommitService
    {
        internal const int MaximumBlessingBytes = 20;
        internal const int MaximumMailGold = 100000000;
        private readonly WeddingCatalog _catalog;
        private readonly Func<long> _now;
        internal WeddingMoneyGiftCommitService(WeddingCatalog catalog, Func<long> now = null)
        { _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }

        internal static bool ValidBlessing(byte[] text) => text != null && text.Length <= MaximumBlessingBytes
            && !text.Contains((byte)0) && ClientTextEncoding.TryGetStringStrict(text, out _);

        // Caller owns the host/guest transition gates in CID order. Only the
        // guest lease is locked; all competing terminal operations serialize at
        // SQLite's write transaction, with no offline inventory replacement.
        internal WeddingMoneyGiftResult Pay(InventoryLease guest, long pair, long start, int amount,
            byte[] blessing, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (guest == null || pair is <= 0 or >= uint.MaxValue || start <= 0 || blessing == null) return new(-1);
            blessing = blessing.ToArray();
            if (!ValidBlessing(blessing)) return new(-1);
            lock (guest.SyncRoot)
            {
                if (!Current(guest) || !stillCurrent()) return new(-1);
                if (!InventoryPersistenceService.SaveDirty(guest)) return new(-1);
                InventoryService baseline = null; bool mutated = false;
                try
                {
                    var database = guest.Inventory.Database;
                    using var connection = database.OpenConnection();
                    baseline = InventoryService.LoadFromDb(connection, guest.CharacterId, guest.AccountId, database);
                    using var transaction = connection.BeginTransaction(deferred: false);
                    var actor = WeddingRepository.LoadActor(connection, transaction, guest.CharacterId);
                    Need(actor != null && !actor.Deleted && actor.AccountId == guest.AccountId, -1);
                    var old = Load(connection, transaction, pair, start).SingleOrDefault(x => x.Guest == guest.CharacterId);
                    if (old != null)
                    {
                        Need(old.State is 0 or 1, -4);
                        Need(old.Amount == amount && old.Blessing.SequenceEqual(blessing), -4);
                        Need(Current(guest) && stillCurrent(), -1);
                        transaction.Commit();
                        return new(0, true);
                    }
                    long now = _now();
                    var invitation = WeddingGuestInvitationRepository.FindForPayment(connection, transaction,
                        guest.CharacterId, guest.AccountId, pair, start, now);
                    Need(invitation != null, -3);
                    Need(amount >= _catalog.GiftMinimumGold, -6);
                    Need(amount <= _catalog.GiftMaximumGold, -7);
                    var wedding = invitation.State;
                    var tier = _catalog.GuestGifts.FirstOrDefault(x => amount <= x.MaximumGold);
                    Need(tier != null && tier.Gifts.TryGetValue(wedding.Ceremony, out var gifts) && gifts.Count > 0, -1);
                    var frozenItems = tier.Gifts[wedding.Ceremony].ToArray();
                    long charge = checked((long)amount * _catalog.GiftChargePercent / 100);
                    long net = checked(amount - charge);
                    int wallet = guest.Inventory.GetMainVirtualCount(0)?.Count ?? 0;
                    Need(wallet >= amount, -8);
                    mutated = true;
                    Need(guest.Inventory.SetMainVirtualCount(0, wallet - amount), -8);
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO wedding_money_gifts
(pair_id,ceremony_start_at,guest_character_id,left_character_id,right_character_id,ceremony,
 amount,charge_percent,charge_gold,net_gold,blessing,created_at)
VALUES(@pair,@start,@guest,@left,@right,@ceremony,@amount,@rate,@charge,@net,@text,@now);";
                        AddEvent(command, pair, start); command.Parameters.AddWithValue("@guest", guest.CharacterId);
                        command.Parameters.AddWithValue("@left", wedding.CharacterId); command.Parameters.AddWithValue("@right", wedding.PartnerId);
                        command.Parameters.AddWithValue("@ceremony", wedding.Ceremony); command.Parameters.AddWithValue("@amount", amount);
                        command.Parameters.AddWithValue("@rate", _catalog.GiftChargePercent); command.Parameters.AddWithValue("@charge", charge);
                        command.Parameters.AddWithValue("@net", net); command.Parameters.AddWithValue("@text", blessing);
                        command.Parameters.AddWithValue("@now", now); command.ExecuteNonQuery();
                    }
                    for (int i = 0; i < frozenItems.Length; i++)
                    {
                        using var command = connection.CreateCommand(); command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO wedding_money_gift_items
(pair_id,ceremony_start_at,guest_character_id,ordinal,item_id,item_count) VALUES(@pair,@start,@guest,@ordinal,@item,@count);";
                        AddEvent(command, pair, start); command.Parameters.AddWithValue("@guest", guest.CharacterId);
                        command.Parameters.AddWithValue("@ordinal", i); command.Parameters.AddWithValue("@item", frozenItems[i].ItemId);
                        command.Parameters.AddWithValue("@count", frozenItems[i].Count); command.ExecuteNonQuery();
                    }
                    WeddingGuestBookRepository.AddCeremonyBlessing(connection, transaction, pair, start,
                        guest.CharacterId, actor.Name, now, blessing);
                    Need(Current(guest) && stillCurrent(), -1);
                    Need(InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, guest), -1);
                    transaction.Commit(); guest.Inventory.ClearDirtyState();
                    return new(0);
                }
                catch (Exception ex)
                {
                    if (mutated && baseline != null) InventoryContext.TryReplaceCurrentLease(guest, baseline, out _);
                    if (ex is GiftFailure failure) return new(failure.Code);
                    FileLogger.Log($"[WeddingMoneyGift] payment failed pair={pair} start={start} guest={guest.CharacterId}: {ex}");
                    return new(-1);
                }
            }
        }

        // These methods never commit or swallow errors. The ceremony owner
        // commits its state transition, all mail and the ledger together.
        internal int[] Settle(SqliteConnection connection, SqliteTransaction transaction, IGameDatabase database, long pair, long start)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            var rows = Load(connection, transaction, pair, start);
            if (!Pending(rows, 1)) return Array.Empty<int>();
            long now = _now();
            var state = WeddingGuestInvitationRepository.LoadEvent(connection, transaction, pair, start);
            Need(state != null && state.StartAt == start && state.Status is WeddingStatus.Reserved or WeddingStatus.Ceremony
                && now >= start, -1);
            Need(rows.All(x => x.Left == state.CharacterId && x.Right == state.PartnerId), -1);
            foreach (int cid in new[] { state.CharacterId, state.PartnerId })
            {
                var host = WeddingRepository.LoadActor(connection, transaction, cid);
                Need(host != null && !host.Deleted, -1);
            }
            long each = checked(rows.Sum(x => x.Net) / 2);
            var mails = new List<MailboxSendRequest>();
            foreach (int cid in new[] { state.CharacterId, state.PartnerId })
                AddGoldMails(connection, transaction, mails, cid, each, $"wedding-money:{pair}:{start}:hosts:{cid}",
                    "婚礼礼金", "感谢亲友的祝福，请领取扣除手续费后平均分配的婚礼礼金。");
            foreach (var row in rows)
            {
                var items = LoadItems(connection, transaction, row);
                Need(items.Count > 0, -1);
                for (int offset = 0; offset < items.Count; offset += 10)
                {
                    var mail = Mail(connection, transaction, row.Guest, $"wedding-money:{pair}:{start}:guest:{row.Guest}:{offset / 10}",
                        "婚礼回礼", "感谢你参加婚礼并送上祝福，请领取婚礼回礼。");
                    mail.Attachments = items.Skip(offset).Take(10).Select(x => new MailboxSendAttachmentRequest { ItemId = x.ItemId, ItemCount = x.Count }).ToArray();
                    mails.Add(mail);
                }
            }
            Send(connection, transaction, database, mails);
            Complete(connection, transaction, pair, start, 1, rows.Count, now);
            return mails.Select(x => x.ReceiverCharacterId).Distinct().ToArray();
        }

        internal int[] Refund(SqliteConnection connection, SqliteTransaction transaction, IGameDatabase database, long pair, long start)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            var rows = Load(connection, transaction, pair, start);
            if (!Pending(rows, 2)) return Array.Empty<int>();
            var state = WeddingGuestInvitationRepository.LoadEvent(connection, transaction, pair, start);
            Need(state != null && state.StartAt == start && state.Status is WeddingStatus.Reserved or WeddingStatus.Ceremony, -1);
            var mails = new List<MailboxSendRequest>();
            foreach (var row in rows)
                AddGoldMails(connection, transaction, mails, row.Guest, row.Amount,
                    $"wedding-money:{pair}:{start}:refund:{row.Guest}", "婚礼礼金退还", "婚礼已取消，你支付的礼金已全额退还。");
            Send(connection, transaction, database, mails);
            Complete(connection, transaction, pair, start, 2, rows.Count, _now());
            return mails.Select(x => x.ReceiverCharacterId).Distinct().ToArray();
        }

        internal static bool HasPendingEscrow(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM wedding_money_gifts WHERE guest_character_id=@cid AND state=0);";
            command.Parameters.AddWithValue("@cid", characterId); return Convert.ToInt32(command.ExecuteScalar()) != 0;
        }

        internal static bool HasPayment(SqliteConnection connection, SqliteTransaction transaction, long pair, long start, int characterId)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM wedding_money_gifts WHERE pair_id=@pair AND ceremony_start_at=@start AND guest_character_id=@cid AND state IN (0,1));";
            AddEvent(command, pair, start); command.Parameters.AddWithValue("@cid", characterId); return Convert.ToInt32(command.ExecuteScalar()) != 0;
        }

        private sealed record Row(long Pair, long Start, int Guest, int Left, int Right, int Amount, long Net, byte[] Blessing, int State);
        private static List<Row> Load(SqliteConnection connection, SqliteTransaction transaction, long pair, long start)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT guest_character_id,left_character_id,right_character_id,amount,net_gold,blessing,state
FROM wedding_money_gifts WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY guest_character_id;";
            AddEvent(command, pair, start); var rows = new List<Row>(); using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(new(pair, start, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), (byte[])reader.GetValue(5), reader.GetInt32(6)));
            return rows;
        }

        private static List<WeddingItemCost> LoadItems(SqliteConnection connection, SqliteTransaction transaction, Row row)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT item_id,item_count FROM wedding_money_gift_items WHERE pair_id=@pair AND ceremony_start_at=@start AND guest_character_id=@guest ORDER BY ordinal;";
            AddEvent(command, row.Pair, row.Start); command.Parameters.AddWithValue("@guest", row.Guest);
            var items = new List<WeddingItemCost>(); using var reader = command.ExecuteReader();
            while (reader.Read()) items.Add(new(reader.GetInt32(0), reader.GetInt32(1)));
            return items;
        }

        private static bool Pending(List<Row> rows, int requestedState)
        {
            if (rows.Count == 0 || rows.All(x => x.State == requestedState)) return false;
            // Opposite terminal results are immutable. No refund can follow a
            // successful payout, and no paid ceremony can consume a refund.
            if (rows.All(x => x.State == 3 - requestedState)) return false;
            Need(rows.All(x => x.State == 0), -1);
            return true;
        }

        private static void Complete(SqliteConnection connection, SqliteTransaction transaction, long pair, long start, int state, int count, long now)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE wedding_money_gifts SET state=@state,completed_at=@now WHERE pair_id=@pair AND ceremony_start_at=@start AND state=0;";
            AddEvent(command, pair, start); command.Parameters.AddWithValue("@state", state); command.Parameters.AddWithValue("@now", now);
            Need(command.ExecuteNonQuery() == count, -1);
        }

        private static void AddGoldMails(SqliteConnection connection, SqliteTransaction transaction, List<MailboxSendRequest> mails,
            int characterId, long gold, string key, string title, string text)
        {
            int part = 0;
            while (gold > 0)
            {
                int chunk = (int)Math.Min(MaximumMailGold, gold);
                var mail = Mail(connection, transaction, characterId, key + ":" + part++, title, text);
                mail.Gold = chunk; mails.Add(mail); gold -= chunk;
            }
        }

        private static MailboxSendRequest Mail(SqliteConnection connection, SqliteTransaction transaction, int cid, string key, string title, string text)
        {
            var actor = WeddingRepository.LoadActor(connection, transaction, cid);
            Need(actor != null && !actor.Deleted, -1);
            return new() { SenderCharacterId = cid, SenderAccountId = actor.AccountId, ReceiverCharacterId = cid,
                ReceiverAccountId = actor.AccountId, ReceiverName = actor.DisplayName, ReceiverLevel = actor.Level,
                Title = title, Text = text, MailType = 1, Unlimited = true, IdempotencyKey = key,
                AuditActor = "婚礼系统", AuditReason = key };
        }

        private static void Send(SqliteConnection connection, SqliteTransaction transaction, IGameDatabase database, List<MailboxSendRequest> mails)
        {
            if (mails.Count == 0) return;
            using var allocation = InventoryUidAllocationContext.Enter(connection, transaction);
            var result = new MailboxRepository(database).SendSystemMails(connection, transaction, mails);
            if (!result.Success) throw new InvalidOperationException("Wedding money mail failed: " + result.Error);
        }

        private static void AddEvent(SqliteCommand command, long pair, long start)
        { command.Parameters.AddWithValue("@pair", pair); command.Parameters.AddWithValue("@start", start); }
        private static bool Current(InventoryLease lease) => InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId);
        private static void Need(bool condition, int error) { if (!condition) throw new GiftFailure(error); }
        private sealed class GiftFailure : InvalidOperationException
        { internal int Code { get; } internal GiftFailure(int code) : base("Wedding money gift rule rejected: " + code) => Code = code; }
    }
}
