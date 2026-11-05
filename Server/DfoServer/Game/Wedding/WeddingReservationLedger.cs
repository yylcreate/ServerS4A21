using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Wedding
{
    // A reservation stores what was actually paid, not a later PVF quote or an
    // assumption about which member of the pair sent the original request.
    internal static class WeddingReservationLedger
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_reservation_receipts (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    ceremony_start_at INTEGER NOT NULL CHECK(ceremony_start_at > 0),
    payer_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    gold INTEGER NOT NULL CHECK(gold >= 0),
    ticket_item_id INTEGER NOT NULL CHECK(ticket_item_id >= 0),
    ticket_count INTEGER NOT NULL CHECK(ticket_count >= 0),
    refunded INTEGER NOT NULL DEFAULT 0 CHECK(refunded IN (0,1)),
    PRIMARY KEY(pair_id,ceremony_start_at)
);";

        internal static void Record(SqliteConnection connection, SqliteTransaction transaction,
            WeddingState reservation, int payer, WeddingCatalog catalog)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (reservation.Status != WeddingStatus.Reserved || reservation.StartAt <= 0
                || payer != reservation.CharacterId && payer != reservation.PartnerId)
                throw new ArgumentException("A committed reservation payer is required.");
            bool ticket = reservation.Ceremony == catalog.PaidCeremonyType;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_reservation_receipts
(pair_id,ceremony_start_at,payer_character_id,gold,ticket_item_id,ticket_count)
VALUES(@pair,@start,@payer,@gold,@item,@count);";
            command.Parameters.AddWithValue("@pair", reservation.PairId);
            command.Parameters.AddWithValue("@start", reservation.StartAt);
            command.Parameters.AddWithValue("@payer", payer);
            command.Parameters.AddWithValue("@gold", catalog.Ceremonies[reservation.Ceremony].Gold);
            command.Parameters.AddWithValue("@item", ticket ? catalog.PaidTicket.ItemId : 0);
            command.Parameters.AddWithValue("@count", ticket ? catalog.PaidTicket.Count : 0);
            command.ExecuteNonQuery();
        }

        internal static int? Refund(SqliteConnection connection, SqliteTransaction transaction,
            IGameDatabase database, long pair, long start)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT payer_character_id,gold,ticket_item_id,ticket_count,refunded
FROM wedding_reservation_receipts WHERE pair_id=@pair AND ceremony_start_at=@start;";
            command.Parameters.AddWithValue("@pair", pair); command.Parameters.AddWithValue("@start", start);
            int payer, gold, item, count;
            using (var reader = command.ExecuteReader())
            {
                // No payer is inferred for an earlier reservation without a receipt.
                if (!reader.Read() || reader.GetInt32(4) != 0) return null;
                payer = reader.GetInt32(0); gold = reader.GetInt32(1);
                item = reader.GetInt32(2); count = reader.GetInt32(3);
            }
            var actor = WeddingRepository.LoadActor(connection, transaction, payer);
            if (actor == null || actor.Deleted) throw new InvalidOperationException("Reservation payer is no longer active.");
            var requests = new List<MailboxSendRequest>();
            int remaining = gold, part = 0;
            do
            {
                int portion = Math.Min(100_000_000, remaining); remaining -= portion;
                requests.Add(new MailboxSendRequest
                {
                    SenderCharacterId = payer, SenderAccountId = actor.AccountId,
                    ReceiverCharacterId = payer, ReceiverAccountId = actor.AccountId,
                    ReceiverName = actor.DisplayName, ReceiverLevel = actor.Level,
                    Gold = portion, Title = "婚礼预约退款", Text = "婚礼未能开始，返还本次预约实际支付的费用。",
                    MailType = 1, Unlimited = true, AuditActor = "婚礼系统",
                    AuditReason = "wedding-reservation-refund:" + pair,
                    IdempotencyKey = $"wedding-reservation-refund:{pair}:{start}:{payer}:{part}",
                    Attachments = part == 0 && item > 0 && count > 0
                        ? new[] { new MailboxSendAttachmentRequest { ItemId = item, ItemCount = count } }
                        : Array.Empty<MailboxSendAttachmentRequest>(),
                });
                part++;
            } while (remaining > 0);
            var sent = new MailboxRepository(database).SendSystemMails(connection, transaction, requests);
            if (!sent.Success) throw new InvalidOperationException("Reservation refund mail failed: " + sent.Error);
            command.CommandText = @"UPDATE wedding_reservation_receipts SET refunded=1
WHERE pair_id=@pair AND ceremony_start_at=@start AND refunded=0;";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Reservation refund changed concurrently.");
            return payer;
        }
    }
}
