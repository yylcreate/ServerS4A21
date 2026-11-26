using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events
{
    internal sealed class GameEventRepository
    {
        private readonly IGameDatabase _database;

        internal GameEventRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        // Catalog metadata belongs to the operator once present. Ensuring a
        // member must not overwrite notices, details or the saved switch when
        // an optional startup setting is absent.
        internal void EnsureCatalogEntry(
            int eventId,
            bool? enabled,
            string startNotice,
            string endNotice,
            int sortOrder)
        {
            if (eventId <= 0 || eventId > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(eventId));

            _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO game_event_state(event_id, state) VALUES(@eventId, 0);
INSERT OR IGNORE INTO game_event_info_details(
    event_id, unknown0, start_notice, end_notice, detail_flag, sort_order)
VALUES(@eventId, 0, @startNotice, @endNotice, 0, @sortOrder);";
                command.Parameters.AddWithValue("@eventId", eventId);
                command.Parameters.AddWithValue("@startNotice", startNotice ?? string.Empty);
                command.Parameters.AddWithValue("@endNotice", endNotice ?? string.Empty);
                command.Parameters.AddWithValue("@sortOrder", sortOrder);
                command.ExecuteNonQuery();
                if (!enabled.HasValue)
                    return;

                command.Parameters.Clear();
                command.CommandText = @"
UPDATE game_event_state SET state=@state WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@state", enabled.Value ? 1 : 0);
                command.Parameters.AddWithValue("@eventId", eventId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Event catalog switch update failed.");
            });
        }

        internal bool IsEnabled(int eventId)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT state
FROM game_event_state
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@eventId", eventId);
                var value = command.ExecuteScalar();
                return value != null
                    && value != DBNull.Value
                    && Convert.ToInt32(value) != 0;
            }
        }

        internal static bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT state
FROM game_event_state
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@eventId", eventId);
                var value = command.ExecuteScalar();
                return value != null
                    && value != DBNull.Value
                    && Convert.ToInt32(value) != 0;
            }
        }

        internal GameEventInfoSnapshot LoadEventInfoSnapshot()
        {
            using (var connection = _database.OpenConnection())
            {
                return new GameEventInfoSnapshot
                {
                    Events = LoadEnabledDetails(connection),
                    ExtraEntries = LoadEnabledExtraEntries(connection),
                };
            }
        }

        internal HashSet<int> LoadAdvertisedEventIds()
        {
            using var connection = _database.OpenConnection();
            return LoadAdvertisedEventIds(connection, null);
        }

        internal static HashSet<int> LoadAdvertisedEventIds(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return LoadAdvertisedEventIds(connection, null);
        }

        internal static HashSet<int> LoadAdvertisedEventIds(
            SqliteConnection connection, SqliteTransaction transaction)
        {
            var result = new HashSet<int>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT d.event_id
FROM game_event_info_details d
JOIN game_event_state s ON s.event_id = d.event_id
WHERE s.state != 0;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetInt32(0));
            return result;
        }

        internal static bool IsAdvertised(
            SqliteConnection connection, SqliteTransaction transaction, int eventId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT 1
FROM game_event_info_details d
JOIN game_event_state s ON s.event_id = d.event_id
WHERE d.event_id=@eventId AND s.state != 0;";
            command.Parameters.AddWithValue("@eventId", eventId);
            return command.ExecuteScalar() != null;
        }

        private static IReadOnlyList<GameEventInfoEntry> LoadEnabledDetails(
            SqliteConnection connection)
        {
            var entries = new List<GameEventInfoEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT d.event_id,
       d.unknown0,
       d.start_notice,
       d.end_notice,
       d.detail_flag,
       d.flag_a,
       d.flag_b,
       d.title,
       d.short_name,
       d.reserved_or_icon,
       d.start_unix_time,
       d.end_unix_time,
       d.link_key,
       d.description,
       d.detail_enabled
FROM game_event_info_details d
JOIN game_event_state s ON s.event_id = d.event_id
WHERE s.state != 0
ORDER BY d.sort_order, d.event_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        entries.Add(new GameEventInfoEntry
                        {
                            EventId = (ushort)reader.GetInt32(0),
                            Unknown0 = (uint)reader.GetInt64(1),
                            StartNotice = ReadString(reader, 2),
                            EndNotice = ReadString(reader, 3),
                            HasDetail = reader.GetInt32(4) != 0,
                            FlagA = (byte)reader.GetInt32(5),
                            FlagB = (byte)reader.GetInt32(6),
                            Title = ReadString(reader, 7),
                            ShortName = ReadString(reader, 8),
                            ReservedOrIcon = ReadString(reader, 9),
                            StartUnixTime = (uint)reader.GetInt64(10),
                            EndUnixTime = (uint)reader.GetInt64(11),
                            LinkKey = ReadString(reader, 12),
                            Description = ReadString(reader, 13),
                            DetailEnabled = reader.GetInt32(14) != 0,
                        });
                    }
                }
            }

            return entries;
        }

        private static IReadOnlyList<GameEventExtraInfoEntry> LoadEnabledExtraEntries(
            SqliteConnection connection)
        {
            var entries = new List<GameEventExtraInfoEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT e.event_id,
       e.param0, e.param1, e.param2, e.param3,
       e.param4, e.param5, e.param6, e.param7,
       e.param8, e.param9, e.param10, e.param11
FROM game_event_info_extra e
JOIN game_event_state s ON s.event_id = e.event_id
WHERE s.state != 0
ORDER BY e.sort_order, e.event_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var parameters = new uint[12];
                        for (var index = 0; index < parameters.Length; index++)
                            parameters[index] = (uint)reader.GetInt64(index + 1);

                        entries.Add(new GameEventExtraInfoEntry
                        {
                            EventId = (ushort)reader.GetInt32(0),
                            Parameters = parameters,
                        });
                    }
                }
            }

            return entries;
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }
    }
}
