using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Wedding
{
    internal sealed class WeddingRepository
    {
        private readonly IGameDatabase _database;
        internal WeddingRepository(IGameDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));
        internal WeddingState Load(int characterId) => _database.Read(c => Load(c, null, characterId));
        internal bool IsBound(int characterId) => _database.Read(c => IsBound(c, null, characterId));

        internal static bool IsBound(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM wedding_members WHERE character_id=@cid);";
            command.Parameters.AddWithValue("@cid", characterId);
            return Convert.ToInt32(command.ExecuteScalar()) != 0;
        }

        internal IReadOnlySet<byte> LoadReservedRooms(int channelId) => _database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT room FROM wedding_pairs WHERE status IN (2,3) AND channel_id=@channel;";
            command.Parameters.AddWithValue("@channel", channelId);
            var rooms = new HashSet<byte>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) rooms.Add(checked((byte)reader.GetInt32(0)));
            return rooms;
        });

        internal IReadOnlyList<(int CharacterId, long PairId, long StartAt)> LoadDueCeremonies(long now, int endSeconds, int limit)
        {
            if (endSeconds <= 0 || limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
            return _database.Read(c =>
            {
                using var command = c.CreateCommand();
                command.CommandText = @"SELECT left_character_id,pair_id,start_at FROM wedding_pairs
WHERE (status=2 AND start_at<=@now) OR (status=3 AND start_at<=@endBefore)
ORDER BY start_at + CASE status WHEN 3 THEN @endSeconds ELSE 0 END,pair_id LIMIT @limit;";
                command.Parameters.AddWithValue("@now", now);
                command.Parameters.AddWithValue("@endBefore", checked(now - endSeconds));
                command.Parameters.AddWithValue("@endSeconds", endSeconds);
                command.Parameters.AddWithValue("@limit", limit);
                var result = new List<(int CharacterId, long PairId, long StartAt)>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) result.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));
                return result;
            });
        }

        internal static CharacterRecord LoadActor(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT account_id,level,job,delete_flag,name FROM characters WHERE character_id=@cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new CharacterRecord { CharacterId = characterId, AccountId = reader.GetInt32(0),
                Level = (byte)reader.GetInt32(1), Job = (byte)reader.GetInt32(2), Deleted = reader.GetInt32(3) != 0,
                Name = reader.GetValue(4) is byte[] name ? name : ClientTextEncoding.GetBytes(reader.GetString(4)) } : null;
        }

        internal static (long DivorcedAt, long NextWeddingAt) LoadCooldown(
            SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT divorced_at,next_wedding_at FROM wedding_character_cooldowns WHERE character_id=@cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }

        internal static void SaveCooldown(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, long? divorcedAt, long? nextWeddingAt)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_character_cooldowns(character_id,divorced_at,next_wedding_at)
VALUES(@cid,COALESCE(@divorce,0),COALESCE(@next,0)) ON CONFLICT(character_id) DO UPDATE SET
divorced_at=COALESCE(@divorce,divorced_at),next_wedding_at=COALESCE(@next,next_wedding_at);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@divorce", (object)divorcedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@next", (object)nextWeddingAt ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        internal static void RemoveMembers(SqliteConnection connection, SqliteTransaction transaction, long pairId)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM wedding_members WHERE pair_id=@pair;";
            command.Parameters.AddWithValue("@pair", pairId);
            if (command.ExecuteNonQuery() != 2) throw new InvalidOperationException("Wedding membership changed during breakup.");
        }

        internal static WeddingState Load(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT p.pair_id, other.character_id, p.status, p.ceremony,
p.ring, p.love, p.married_at, p.start_at, p.channel_id, p.room, p.version
FROM wedding_members owner JOIN wedding_pairs p ON p.pair_id=owner.pair_id
JOIN wedding_members other ON other.pair_id=p.pair_id AND other.character_id<>owner.character_id
WHERE owner.character_id=@cid AND p.status<>0;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new WeddingState(reader.GetInt64(0), characterId, reader.GetInt32(1), (WeddingStatus)reader.GetInt32(2),
                    (byte)reader.GetInt32(3), (byte)reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6),
                    reader.GetInt64(7), reader.GetInt32(8), (byte)reader.GetInt32(9), reader.GetInt64(10))
                : WeddingState.Empty(characterId);
        }

        internal static WeddingState Create(SqliteConnection connection, SqliteTransaction transaction,
            int actorId, int partnerId, long now)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (actorId <= 0 || partnerId <= 0 || actorId == partnerId) throw new ArgumentException("Two distinct characters are required.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_pairs(left_character_id,right_character_id,status,created_at)
VALUES(@left,@right,1,@now) RETURNING pair_id;";
            command.Parameters.AddWithValue("@left", Math.Min(actorId, partnerId));
            command.Parameters.AddWithValue("@right", Math.Max(actorId, partnerId));
            command.Parameters.AddWithValue("@now", now);
            long id = Convert.ToInt64(command.ExecuteScalar());
            command.CommandText = "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(@left,@pair,0),(@right,@pair,1);";
            command.Parameters.AddWithValue("@pair", id);
            command.ExecuteNonQuery();
            return Load(connection, transaction, actorId);
        }

        internal static bool Save(SqliteConnection connection, SqliteTransaction transaction, WeddingState before, WeddingState after)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (before.PairId <= 0 || before.PairId != after.PairId || before.CharacterId != after.CharacterId
                || before.PartnerId != after.PartnerId) throw new ArgumentException("Wedding identity cannot change during an update.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE wedding_pairs SET status=@status,ceremony=@ceremony,ring=@ring,love=@love,
married_at=@married,start_at=@start,channel_id=@channel,room=@room,version=version+1
WHERE pair_id=@pair AND version=@version AND status=@previous;";
            command.Parameters.AddWithValue("@status", (int)after.Status);
            command.Parameters.AddWithValue("@ceremony", after.Ceremony);
            command.Parameters.AddWithValue("@ring", after.Ring);
            command.Parameters.AddWithValue("@love", after.Love);
            command.Parameters.AddWithValue("@married", after.MarriedAt);
            command.Parameters.AddWithValue("@start", after.StartAt);
            command.Parameters.AddWithValue("@channel", after.ChannelId);
            command.Parameters.AddWithValue("@room", after.Room);
            command.Parameters.AddWithValue("@pair", before.PairId);
            command.Parameters.AddWithValue("@version", before.Version);
            command.Parameters.AddWithValue("@previous", (int)before.Status);
            return command.ExecuteNonQuery() == 1;
        }
    }
}
