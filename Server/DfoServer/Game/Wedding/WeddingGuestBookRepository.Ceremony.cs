using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal static class WeddingCeremonyCommentSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_ceremony_comments (
    comment_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(comment_id BETWEEN 1 AND 4294967294),
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    ceremony_start_at INTEGER NOT NULL CHECK(ceremony_start_at > 0),
    author_id INTEGER NOT NULL REFERENCES characters(character_id),
    author_name BLOB NOT NULL CHECK(length(author_name) BETWEEN 1 AND 29),
    created_at INTEGER NOT NULL CHECK(created_at BETWEEN 0 AND 4294967295),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 255)
);
CREATE INDEX IF NOT EXISTS idx_wedding_ceremony_comments_event
ON wedding_ceremony_comments(pair_id,ceremony_start_at,comment_id);";
    }

    internal sealed partial class WeddingGuestBookRepository
    {
        // A completed marriage has one non-refunded reservation. Move its
        // blessings into the shared home board atomically on first access,
        // including marriages completed before this projection was available.
        // Reassigning board IDs avoids collisions between the two native lists.
        private static void ArchiveCompletedCeremonyComments(SqliteConnection connection,
            SqliteTransaction transaction, WeddingState marriage)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT ceremony_start_at FROM wedding_reservation_receipts
WHERE pair_id=@pair AND refunded=0 AND ceremony_start_at<=@married;";
            command.Parameters.AddWithValue("@pair", marriage.PairId);
            command.Parameters.AddWithValue("@married", marriage.MarriedAt);
            long start;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return;
                start = reader.GetInt64(0);
                if (reader.Read()) throw new InvalidOperationException("Completed wedding has ambiguous reservation receipts.");
            }
            command.Parameters.AddWithValue("@start", start);
            command.CommandText = @"INSERT INTO wedding_guest_comments(pair_id,author_id,author_name,created_at,content)
SELECT pair_id,author_id,author_name,created_at,content FROM wedding_ceremony_comments
WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY comment_id;";
            int moved = command.ExecuteNonQuery();
            if (moved == 0) return;
            command.CommandText = "DELETE FROM wedding_ceremony_comments WHERE pair_id=@pair AND ceremony_start_at=@start;";
            if (command.ExecuteNonQuery() != moved) throw new InvalidOperationException("Wedding blessing archive changed concurrently.");
            command.CommandText = @"DELETE FROM wedding_guest_comments WHERE pair_id=@pair AND comment_id NOT IN
(SELECT comment_id FROM wedding_guest_comments WHERE pair_id=@pair ORDER BY created_at DESC,comment_id DESC LIMIT @limit);";
            command.Parameters.AddWithValue("@limit", MaximumEntries);
            command.ExecuteNonQuery();
        }

        // The caller holds both host gates and the author's gate in CID order,
        // and guards the author's actual church position throughout this call.
        internal WeddingGuestBookResult ExecuteCeremony(int characterId, int accountId,
            WeddingState expected, byte[] text, uint? deleteId, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (expected == null || characterId <= 0 || accountId <= 0
                || text != null && (deleteId.HasValue || !ValidText(text))) return new(-3);
            try
            {
                return _database.Write((connection, transaction) =>
                {
                    if (!stillCurrent()) throw new GuestBookFailure(-3);
                    long now = _now();
                    var state = WeddingGuestInvitationRepository.LoadEvent(connection, transaction, expected.PairId, expected.StartAt);
                    var actor = WeddingRepository.LoadActor(connection, transaction, characterId);
                    var first = state == null ? null : WeddingRepository.LoadActor(connection, transaction, state.CharacterId);
                    var second = state == null ? null : WeddingRepository.LoadActor(connection, transaction, state.PartnerId);
                    bool host = state != null && (state.CharacterId == characterId || state.PartnerId == characterId);
                    if (state == null || actor == null || actor.Deleted || actor.AccountId != accountId
                        || first == null || second == null || first.Deleted || second.Deleted
                        || state.ChannelId != expected.ChannelId || state.Room != expected.Room
                        || !LiveCeremony(state, now)
                        || !host && !HasInvitation(connection, transaction, characterId, state))
                        throw new GuestBookFailure(-3);
                    uint id = 0;
                    if (text != null)
                    {
                        var latest = LoadCeremonyComments(connection, transaction, state.PairId, state.StartAt).FirstOrDefault();
                        id = latest?.AuthorId == characterId && latest.Text.SequenceEqual(text) ? latest.Id
                            : AddCeremonyBlessing(connection, transaction, state.PairId, state.StartAt, characterId, actor.Name, now, text);
                    }
                    else if (deleteId.HasValue)
                    {
                        using var command = connection.CreateCommand(); command.Transaction = transaction;
                        command.CommandText = @"DELETE FROM wedding_ceremony_comments
WHERE pair_id=@pair AND ceremony_start_at=@start AND comment_id=@id AND (@host=1 OR author_id=@cid);";
                        AddCommentEvent(command, state.PairId, state.StartAt);
                        command.Parameters.AddWithValue("@id", deleteId.Value);
                        command.Parameters.AddWithValue("@host", host ? 1 : 0); command.Parameters.AddWithValue("@cid", characterId);
                        command.ExecuteNonQuery(); id = deleteId.Value;
                    }
                    if (!stillCurrent()) throw new GuestBookFailure(-3);
                    return new WeddingGuestBookResult(0, LoadCeremonyComments(connection, transaction, state.PairId, state.StartAt), id);
                });
            }
            catch (GuestBookFailure ex) { return new(ex.Code); }
            catch (Exception ex)
            { FileLogger.Log($"[WeddingGuestBook] church transaction failed cid={characterId} pair={expected.PairId}: {ex}"); return new(-3); }
        }

        internal static bool LiveCeremony(WeddingState state, long now) => state != null
            && (state.Status == WeddingStatus.Reserved && now < state.StartAt
                || state.Status == WeddingStatus.Ceremony && now < checked(state.StartAt + WeddingCatalog.Get().CeremonyEndSeconds));

        internal static bool HasInvitation(SqliteConnection connection, SqliteTransaction transaction, int cid, WeddingState state)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT EXISTS(SELECT 1 FROM wedding_guest_invitations
WHERE pair_id=@pair AND ceremony_start_at=@start AND guest_character_id=@cid);";
            AddCommentEvent(command, state.PairId, state.StartAt); command.Parameters.AddWithValue("@cid", cid);
            return Convert.ToInt32(command.ExecuteScalar()) != 0
                && WeddingMoneyGiftCommitService.HasPayment(connection, transaction, state.PairId, state.StartAt, cid);
        }

        // Used by the gift payment owner in its existing transaction, after it
        // has authorized the invitation. Empty payment blessings add no comment.
        internal static uint AddCeremonyBlessing(SqliteConnection connection, SqliteTransaction transaction,
            long pair, long start, int cid, byte[] authorName, long now, byte[] text)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (!ValidText(text)) return 0;
            authorName ??= Array.Empty<byte>();
            int nul = Array.IndexOf(authorName, (byte)0);
            var name = (nul >= 0 ? authorName.AsSpan(0, nul) : authorName.AsSpan()).ToArray();
            if (name.Length is < 1 or > 29) throw new ArgumentException("Invalid wedding comment author.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_ceremony_comments
(pair_id,ceremony_start_at,author_id,author_name,created_at,content)
VALUES(@pair,@start,@cid,@name,@now,@text) RETURNING comment_id;";
            AddCommentEvent(command, pair, start); command.Parameters.AddWithValue("@cid", cid);
            command.Parameters.AddWithValue("@name", name); command.Parameters.AddWithValue("@now", now);
            command.Parameters.AddWithValue("@text", text);
            uint id = checked((uint)Convert.ToInt64(command.ExecuteScalar()));
            command.CommandText = @"DELETE FROM wedding_ceremony_comments
WHERE pair_id=@pair AND ceremony_start_at=@start AND comment_id NOT IN
(SELECT comment_id FROM wedding_ceremony_comments WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY comment_id DESC LIMIT @limit);";
            command.Parameters.AddWithValue("@limit", MaximumEntries); command.ExecuteNonQuery();
            return id;
        }

        private static IReadOnlyList<WeddingGuestComment> LoadCeremonyComments(SqliteConnection connection,
            SqliteTransaction transaction, long pair, long start)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT comment_id,author_id,author_name,created_at,content
FROM wedding_ceremony_comments WHERE pair_id=@pair AND ceremony_start_at=@start ORDER BY comment_id DESC LIMIT @limit;";
            AddCommentEvent(command, pair, start); command.Parameters.AddWithValue("@limit", MaximumEntries);
            var result = new List<WeddingGuestComment>(); using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt32(1), (byte[])reader.GetValue(2), reader.GetInt64(3), (byte[])reader.GetValue(4)));
            return result.AsReadOnly();
        }

        private static void AddCommentEvent(SqliteCommand command, long pair, long start)
        { command.Parameters.AddWithValue("@pair", pair); command.Parameters.AddWithValue("@start", start); }
    }
}
