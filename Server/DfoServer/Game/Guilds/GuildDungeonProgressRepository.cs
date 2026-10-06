using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildDungeonProgressRepository
    {
        private readonly IGameDatabase _database;

        internal GuildDungeonProgressRepository(IGameDatabase database)
            => _database = database ?? throw new ArgumentNullException(nameof(database));

        internal IReadOnlyDictionary<int, int> LoadForMember(int characterId, out int guildId,
            int monthId = 0)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            int resolvedMonthId = GuildDungeonMonth.ResolveMonthId(monthId);

            var snapshot = _database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var guild = GuildRepository.IsActiveCharacter(connection, transaction, characterId)
                    ? GuildRepository.GetForMember(connection, transaction, characterId) : null;
                var progress = guild == null
                    ? new ReadOnlyDictionary<int, int>(new Dictionary<int, int>())
                    : Load(connection, transaction, guild.Id, resolvedMonthId);
                return (GuildId: guild?.Id ?? 0, Progress: progress);
            });
            guildId = snapshot.GuildId;
            return snapshot.Progress;
        }

        internal static IReadOnlyDictionary<int, int> Load(
            SqliteConnection connection, SqliteTransaction transaction, int guildId,
            int monthId = 0)
        {
            int resolvedMonthId = GuildDungeonMonth.ResolveMonthId(monthId);
            var rows = ReadRows(connection, transaction, guildId);
            var result = new Dictionary<int, int>();
            foreach (var row in rows)
                if (row.Value.MonthId == resolvedMonthId)
                    result.Add(row.Key, row.Value.Count);
            return new ReadOnlyDictionary<int, int>(result);
        }

        private static Dictionary<int, (int Count, int MonthId)> ReadRows(
            SqliteConnection connection, SqliteTransaction transaction, int guildId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (guildId <= 0)
                throw new ArgumentOutOfRangeException(nameof(guildId));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT dungeon_id, clear_count, month_id,
       typeof(dungeon_id), typeof(clear_count), typeof(month_id)
FROM guild_dungeon_progress
WHERE guild_id = @guildId
ORDER BY dungeon_id;";
            command.Parameters.AddWithValue("@guildId", guildId);
            var result = new Dictionary<int, (int Count, int MonthId)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int dungeonId = ReadInteger(reader, 0, 3, "dungeon_id");
                int clearCount = ReadInteger(reader, 1, 4, "clear_count");
                int storedMonthId = ReadInteger(reader, 2, 5, "month_id");
                if (dungeonId <= 0 || clearCount < 0 || !GuildDungeonMonth.IsValid(storedMonthId)
                    || !result.TryAdd(dungeonId, (clearCount, storedMonthId)))
                    throw new InvalidDataException("Guild dungeon progress has an invalid or duplicate row.");
            }
            return result;
        }

        internal static bool ValidateAdmission(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, GuildDungeonAdmissionSnapshot snapshot, int monthId = 0)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ValidateSnapshot(snapshot);
            int resolvedMonthId = GuildDungeonMonth.ResolveMonthId(monthId);
            if (characterId <= 0 || snapshot.SourceCharacterId != characterId
                || snapshot.MonthId != resolvedMonthId)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT account_id, delete_flag, typeof(account_id), typeof(delete_flag)
FROM characters WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return false;
                int accountId = ReadInteger(reader, 0, 2, "account_id");
                int deleteFlag = ReadInteger(reader, 1, 3, "delete_flag");
                if (accountId <= 0 || deleteFlag < 0)
                    throw new InvalidDataException("Guild dungeon admission character identity is malformed.");
                if (accountId != snapshot.SourceAccountId || deleteFlag != 0)
                    return false;
            }

            if (GuildRepository.GetForMember(connection, transaction, characterId)?.Id != snapshot.GuildId)
                return false;

            var progress = Load(connection, transaction, snapshot.GuildId, resolvedMonthId);
            var seen = new HashSet<int>();
            foreach (var prerequisite in snapshot.Prerequisites)
            {
                if (prerequisite == null || prerequisite.DungeonId <= 0 || prerequisite.GaugeLimit <= 0
                    || prerequisite.GaugeOnClear <= 0 || prerequisite.RequiredRatio < 0
                    || prerequisite.RequiredRatio > 100 || !seen.Add(prerequisite.DungeonId))
                    throw new InvalidDataException("Guild dungeon prerequisite definition is malformed or duplicated.");
                progress.TryGetValue(prerequisite.DungeonId, out int clearCount);
                if ((decimal)clearCount * prerequisite.GaugeOnClear * 100
                    < (decimal)prerequisite.RequiredRatio * prerequisite.GaugeLimit)
                    return false;
            }
            return true;
        }

        internal static (int previous, int current) ApplyClear(SqliteConnection connection,
            SqliteTransaction transaction, GuildDungeonAdmissionSnapshot snapshot,
            int monthId = 0, int contributionCount = 1, int currentMonthId = 0)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            if (!ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("Guild progress must use the caller's transaction.", nameof(transaction));
            ValidateSnapshot(snapshot);
            if (snapshot.GaugeOnClear <= 0)
                throw new InvalidDataException("Guild dungeon clear gauge must be positive.");
            if (contributionCount < 1 || contributionCount > 4)
                throw new ArgumentOutOfRangeException(nameof(contributionCount));
            int resolvedCurrentMonthId = GuildDungeonMonth.ResolveMonthId(currentMonthId);
            int resolvedEventMonthId = monthId == 0 ? resolvedCurrentMonthId
                : GuildDungeonMonth.ResolveMonthId(monthId);

            var rows = ReadRows(connection, transaction, snapshot.GuildId);
            rows.TryGetValue(snapshot.DungeonId, out var stored);
            int previous = stored.MonthId == resolvedCurrentMonthId ? stored.Count : 0;
            // A delayed durable clear is terminal outside its original month.
            // Future rows also remain untouched when the wall clock moves back.
            if (resolvedEventMonthId != resolvedCurrentMonthId
                || stored.MonthId > resolvedCurrentMonthId)
                return (previous, previous);
            if (GuildRepository.Get(connection, transaction, snapshot.GuildId) == null)
                throw new InvalidDataException("The frozen clear's guild no longer exists.");

            if (previous > int.MaxValue - contributionCount)
                throw new InvalidDataException("Guild dungeon clear count cannot advance beyond the Int32 range.");
            int current = checked(previous + contributionCount);

            // The accepted durable fact belongs to its frozen guild. Membership
            // changes cannot redirect it; the existing outbox owns deduplication.
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO guild_dungeon_progress(guild_id, dungeon_id, clear_count, month_id)
VALUES(@guildId, @dungeonId, @clearCount, @monthId)
ON CONFLICT(guild_id, dungeon_id) DO UPDATE SET
    clear_count = excluded.clear_count, month_id = excluded.month_id;";
            command.Parameters.AddWithValue("@guildId", snapshot.GuildId);
            command.Parameters.AddWithValue("@dungeonId", snapshot.DungeonId);
            command.Parameters.AddWithValue("@clearCount", current);
            command.Parameters.AddWithValue("@monthId", resolvedCurrentMonthId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Guild dungeon progress was not persisted.");
            return (previous, current);
        }

        private static void ValidateSnapshot(GuildDungeonAdmissionSnapshot snapshot)
        {
            if (snapshot == null || snapshot.GuildId <= 0 || snapshot.SourceCharacterId <= 0
                || snapshot.SourceAccountId <= 0 || snapshot.DungeonId <= 0
                || snapshot.GaugeLimit <= 0 || snapshot.Prerequisites == null
                || !GuildDungeonMonth.IsValid(snapshot.MonthId))
                throw new InvalidDataException("Guild dungeon admission snapshot is incomplete.");
        }

        private static int ReadInteger(SqliteDataReader reader, int valueOrdinal,
            int typeOrdinal, string field)
        {
            if (reader.IsDBNull(valueOrdinal)
                || !string.Equals(reader.GetString(typeOrdinal), "integer", StringComparison.Ordinal))
                throw new InvalidDataException("Guild dungeon " + field + " must be stored as an integer.");
            long value = reader.GetInt64(valueOrdinal);
            if (value < int.MinValue || value > int.MaxValue)
                throw new InvalidDataException("Guild dungeon " + field + " exceeds the Int32 range.");
            return (int)value;
        }
    }
}
