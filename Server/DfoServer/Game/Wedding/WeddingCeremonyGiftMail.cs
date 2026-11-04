using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Wedding
{
    // Online overflow and offline recovery share one durable gift identity.
    internal static class WeddingCeremonyGiftMail
    {
        internal static void Send(IGameDatabase database, SqliteConnection connection,
            SqliteTransaction transaction, long pairId, int characterId, WeddingItemCost gift)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            var actor = WeddingRepository.LoadActor(connection, transaction, characterId);
            if (actor == null || actor.Deleted) throw new InvalidOperationException("Wedding participant is no longer active.");
            var request = new MailboxSendRequest
            {
                SenderCharacterId = characterId, SenderAccountId = actor.AccountId, SenderName = string.Empty,
                ReceiverCharacterId = characterId, ReceiverAccountId = actor.AccountId, ReceiverName = actor.DisplayName,
                ReceiverLevel = actor.Level, Title = "婚礼赠礼", Text = "婚礼已完成，祝你们幸福美满。请领取婚礼赠礼。",
                MailType = 1, Unlimited = true, AuditActor = "婚礼系统", AuditReason = "wedding-ceremony:" + pairId,
                IdempotencyKey = $"wedding-ceremony:{pairId}:{characterId}",
                Attachments = new[] { new MailboxSendAttachmentRequest { ItemId = gift.ItemId, ItemCount = gift.Count } },
            };
            var sent = new MailboxRepository(database).SendSystemMails(connection, transaction, new[] { request });
            if (!sent.Success) throw new InvalidOperationException("Wedding gift mail was not committed: " + sent.Error);
        }
    }
}
