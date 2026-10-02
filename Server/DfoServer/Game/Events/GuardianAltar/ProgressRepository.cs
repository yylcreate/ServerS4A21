using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.GuardianAltar
{
    // SQL only. The application service owns initialization/refill policy and
    // supplies the one transaction shared by every part of the menu snapshot.
    internal sealed class GuardianAltarProgressRepository
    {
        internal void ValidateRefillReceipt(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, string refillKey)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT period, value FROM character_daily_counters
WHERE character_id=@cid AND counter_key=@key;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@key", refillKey);
            using var reader = command.ExecuteReader();
            if (reader.Read() && (reader.GetString(0) != "day"
                || reader.GetInt64(1) < 0 || reader.GetInt64(1) > 1))
                throw new InvalidOperationException("Guardian refill receipt has an invalid period or value.");
        }

        internal void RequireCharacter(SqliteConnection connection, SqliteTransaction transaction,
            int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM characters WHERE character_id=@cid AND delete_flag=0;";
            command.Parameters.AddWithValue("@cid", characterId);
            if (command.ExecuteScalar() == null)
                throw new InvalidOperationException("Guardian progress requires an existing, non-deleted character.");
        }

        internal bool TryInitialize(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, GuardianAltarMenuDefinition definition)
        {
            var inserted = Execute(connection, transaction, @"
INSERT INTO character_guardian_altar (
    character_id, ridable_id, free_tickets, secondary_tickets,
    stage_stars, other_stars, available_stars, survival_best_phase,
    star_reset_count, reserved28, menu_hint_flag
) VALUES (@cid, @ridable, @tickets, 0, 0, 0, 0, 0, 0, 0, 0)
ON CONFLICT(character_id, ridable_id) DO NOTHING;",
                ("@cid", characterId), ("@ridable", definition.RidableId),
                ("@tickets", definition.DailyRefillTicket));
            if (inserted == 0)
                return false;

            Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_stages (character_id, ridable_id, dungeon_id, progress)
VALUES (@cid, @ridable, @dungeon, -1);",
                ("@cid", characterId), ("@ridable", definition.RidableId),
                ("@dungeon", definition.FirstDungeonId));
            foreach (var purchase in definition.InitialPurchases)
                Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_purchases (
    character_id, ridable_id, shop_type, resource_id, level, price
) VALUES (@cid, @ridable, @type, @resource, @level, @price);",
                    ("@cid", characterId), ("@ridable", definition.RidableId),
                    ("@type", purchase.ShopType), ("@resource", purchase.ResourceId),
                    ("@level", purchase.Level), ("@price", purchase.Price));
            foreach (var slot in definition.InitialSlots)
                Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_slots (
    character_id, ridable_id, slot_type, slot_index, locked, resource_id
) VALUES (@cid, @ridable, @type, @slot, @locked, @resource);",
                    ("@cid", characterId), ("@ridable", definition.RidableId),
                    ("@type", slot.SlotType), ("@slot", slot.SlotIndex),
                    ("@locked", slot.Locked), ("@resource", slot.ResourceId));
            // Fresh achievements are genuinely empty; no claim is synthesized.
            return true;
        }

        internal void ReplaceFreeTickets(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, int freeTickets)
        {
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar
SET free_tickets=@tickets, updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid AND ridable_id=@ridable;",
                    ("@tickets", freeTickets), ("@cid", characterId), ("@ridable", ridableId)) != 1)
                throw new InvalidOperationException("Guardian refill has no current character progress row.");
        }

        internal GuardianAltarProgressSnapshot Read(SqliteConnection connection,
            SqliteTransaction transaction, int characterId, int ridableId)
        {
            GuardianAltarHeader header;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT ridable_id, free_tickets, secondary_tickets, stage_stars, other_stars,
       available_stars, survival_best_phase, star_reset_count, reserved28, menu_hint_flag
FROM character_guardian_altar
WHERE character_id=@cid AND ridable_id=@ridable;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@ridable", ridableId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("Guardian progress row could not be established.");
                header = new GuardianAltarHeader(reader.GetInt32(0), reader.GetInt32(1),
                    reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                    checked((short)reader.GetInt32(6)), checked((short)reader.GetInt32(7)),
                    checked((uint)reader.GetInt64(8)), checked((byte)reader.GetInt32(9)));
            }
            var stages = ReadRows(connection, transaction, characterId, ridableId, @"
SELECT dungeon_id, progress FROM character_guardian_altar_stages
WHERE character_id=@cid AND ridable_id=@ridable ORDER BY dungeon_id;",
                reader => new GuardianAltarStage(reader.GetInt32(0), reader.GetInt32(1)));
            var purchases = ReadRows(connection, transaction, characterId, ridableId, @"
SELECT shop_type, resource_id, level, price FROM character_guardian_altar_purchases
WHERE character_id=@cid AND ridable_id=@ridable ORDER BY shop_type, resource_id;",
                reader => new GuardianAltarPurchase(reader.GetInt32(0), reader.GetInt32(1),
                    checked((short)reader.GetInt32(2)), checked((short)reader.GetInt32(3))));
            var slots = ReadRows(connection, transaction, characterId, ridableId, @"
SELECT locked, slot_type, slot_index, resource_id FROM character_guardian_altar_slots
WHERE character_id=@cid AND ridable_id=@ridable ORDER BY slot_type, slot_index;",
                reader => new GuardianAltarSlot(checked((byte)reader.GetInt32(0)), reader.GetInt32(1),
                    checked((byte)reader.GetInt32(2)), reader.GetInt32(3)));
            var achievements = ReadRows(connection, transaction, characterId, ridableId, @"
SELECT achievement_id, claimed FROM character_guardian_altar_achievements
WHERE character_id=@cid AND ridable_id=@ridable ORDER BY achievement_id;",
                reader => new GuardianAltarAchievement(reader.GetInt32(0), checked((byte)reader.GetInt32(1))));
            return new GuardianAltarProgressSnapshot(characterId, header, stages, purchases, slots, achievements);
        }

        private static List<T> ReadRows<T>(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, string sql, Func<SqliteDataReader, T> read)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@ridable", ridableId);
            var result = new List<T>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(read(reader));
            return result;
        }

        private static int Execute(SqliteConnection connection, SqliteTransaction transaction,
            string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return command.ExecuteNonQuery();
        }
    }
}
