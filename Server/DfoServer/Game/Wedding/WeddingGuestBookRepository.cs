using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingGuestComment(uint Id, int AuthorId, byte[] AuthorName, long CreatedAt, byte[] Text);
    internal sealed record WeddingGuestBookResult(int Error, IReadOnlyList<WeddingGuestComment> Entries = null, uint CommentId = 0)
    { internal bool Success => Error == 0; }

    internal static class WeddingGuestBookSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_guest_comments (
    comment_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(comment_id BETWEEN 1 AND 4294967294),
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id) ON DELETE CASCADE,
    author_id INTEGER NOT NULL REFERENCES characters(character_id),
    author_name BLOB NOT NULL CHECK(length(author_name) BETWEEN 1 AND 29),
    created_at INTEGER NOT NULL CHECK(created_at BETWEEN 0 AND 4294967295),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 255)
);
CREATE INDEX IF NOT EXISTS idx_wedding_guest_comments_pair ON wedding_guest_comments(pair_id,comment_id);";
    }

    internal sealed partial class WeddingGuestBookRepository
    {
        // Bounded storage policy, not a value read from the marriage PVF.
        internal const int MaximumEntries = 100;
        internal const int MaximumTextBytes = 255;
        private readonly IGameDatabase _database;
        private readonly Func<long> _now;
        internal WeddingGuestBookRepository(IGameDatabase database, Func<long> now = null)
        { _database = database ?? throw new ArgumentNullException(nameof(database)); _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }

        // Caller holds the two captured character transition gates. No inventory is involved.
        internal WeddingGuestBookResult Execute(int characterId, int accountId, long pairId, byte[] text,
            uint? deleteId, Func<bool> stillCurrent)
        {
            ArgumentNullException.ThrowIfNull(stillCurrent);
            if (characterId <= 0 || accountId <= 0 || pairId is <= 0 or >= uint.MaxValue
                || text != null && (deleteId.HasValue || !ValidText(text))) return new(-3);
            try
            {
                return _database.Write((connection, transaction) =>
                {
                    if (!stillCurrent()) throw new GuestBookFailure(-3);
                    var actor = WeddingRepository.LoadActor(connection, transaction, characterId);
                    var marriage = WeddingRepository.Load(connection, transaction, characterId);
                    var spouse = WeddingRepository.Load(connection, transaction, marriage.PartnerId);
                    if (actor == null || actor.Deleted || actor.AccountId != accountId || marriage.Status != WeddingStatus.Married
                        || marriage.PairId != pairId || !WeddingRules.IsReciprocal(marriage, spouse)) throw new GuestBookFailure(-3);
                    ArchiveCompletedCeremonyComments(connection, transaction, marriage);
                    uint id = 0;
                    if (text != null)
                    {
                        var entries = Load(connection, transaction, pairId);
                        var latest = entries.FirstOrDefault();
                        // Native update has no request ID. Replay the last identical write instead of appending it twice.
                        if (latest?.AuthorId == characterId && latest.Text.SequenceEqual(text)) id = latest.Id;
                        else
                        {
                            byte[] name = actor.Name;
                            int nul = Array.IndexOf(name, (byte)0);
                            if (nul >= 0) name = name.AsSpan(0, nul).ToArray();
                            if (name.Length is < 1 or > 29) throw new GuestBookFailure(-3);
                            using var command = connection.CreateCommand(); command.Transaction = transaction;
                            command.CommandText = "INSERT INTO wedding_guest_comments(pair_id,author_id,author_name,created_at,content) VALUES(@pair,@cid,@name,@now,@text) RETURNING comment_id;";
                            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@name", name); command.Parameters.AddWithValue("@now", _now()); command.Parameters.AddWithValue("@text", text);
                            id = checked((uint)Convert.ToInt64(command.ExecuteScalar()));
                            command.CommandText = "DELETE FROM wedding_guest_comments WHERE pair_id=@pair AND comment_id NOT IN (SELECT comment_id FROM wedding_guest_comments WHERE pair_id=@pair ORDER BY created_at DESC,comment_id DESC LIMIT @limit);";
                            command.Parameters.AddWithValue("@limit", MaximumEntries); command.ExecuteNonQuery();
                        }
                    }
                    else if (deleteId.HasValue)
                    {
                        // Both owners can manage their shared board; a different pair can never be addressed.
                        using var command = connection.CreateCommand(); command.Transaction = transaction;
                        command.CommandText = "DELETE FROM wedding_guest_comments WHERE pair_id=@pair AND comment_id=@id;";
                        command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@id", deleteId.Value);
                        command.ExecuteNonQuery(); id = deleteId.Value;
                    }
                    if (!stillCurrent()) throw new GuestBookFailure(-3);
                    return new WeddingGuestBookResult(0, Load(connection, transaction, pairId), id);
                });
            }
            catch (GuestBookFailure ex) { return new(ex.Code); }
            catch (Exception ex)
            { FileLogger.Log($"[WeddingGuestBook] transaction failed cid={characterId} pair={pairId}: {ex}"); return new(-3); }
        }

        internal static bool ValidText(byte[] text) => text?.Length is >= 1 and <= MaximumTextBytes
            && !text.Contains((byte)0) && ClientTextEncoding.TryGetStringStrict(text, out var decoded) && !string.IsNullOrWhiteSpace(decoded);

        private static IReadOnlyList<WeddingGuestComment> Load(SqliteConnection connection, SqliteTransaction transaction, long pairId)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT comment_id,author_id,author_name,created_at,content FROM wedding_guest_comments WHERE pair_id=@pair ORDER BY created_at DESC,comment_id DESC LIMIT @limit;";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@limit", MaximumEntries);
            var entries = new List<WeddingGuestComment>(); using var reader = command.ExecuteReader();
            while (reader.Read()) entries.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt32(1), (byte[])reader.GetValue(2), reader.GetInt64(3), (byte[])reader.GetValue(4)));
            return entries.AsReadOnly();
        }
        private sealed class GuestBookFailure : Exception
        { internal int Code { get; } internal GuestBookFailure(int code) => Code = code; }
    }
}
