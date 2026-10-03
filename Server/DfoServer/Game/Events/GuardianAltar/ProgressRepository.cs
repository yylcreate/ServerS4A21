using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.GuardianAltar
{
    // SQL only. The progress/effect application owners supply the transaction
    // shared by menu progress operations or an ordinary clear and its rewards.
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

        internal void RequireCharacterOwnership(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM characters WHERE character_id=@cid AND account_id=@aid AND delete_flag=0;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@aid", accountId);
            if (command.ExecuteScalar() == null)
                throw new InvalidOperationException("Guardian clear participant is not the existing character owner.");
        }

        internal void UpdateClearedStage(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, int dungeonId, int previousProgress, int newProgress)
        {
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar_stages SET progress=@new
WHERE character_id=@cid AND ridable_id=@ridable AND dungeon_id=@dungeon AND progress=@previous;",
                    ("@cid", characterId), ("@ridable", ridableId), ("@dungeon", dungeonId),
                    ("@previous", previousProgress), ("@new", newProgress)) != 1)
                throw new InvalidOperationException("Guardian clear stage progress changed before commit.");
        }

        internal bool TryUnlockStage(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, int dungeonId) => Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_stages (character_id,ridable_id,dungeon_id,progress)
VALUES (@cid,@ridable,@dungeon,-1)
ON CONFLICT(character_id,ridable_id,dungeon_id) DO NOTHING;",
                ("@cid", characterId), ("@ridable", ridableId), ("@dungeon", dungeonId)) == 1;

        internal bool TryUnlockPurchase(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, GuardianAltarPurchase purchase) => Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_purchases (character_id,ridable_id,shop_type,resource_id,level,price)
VALUES (@cid,@ridable,@type,@resource,@level,@price)
ON CONFLICT(character_id,ridable_id,shop_type,resource_id) DO NOTHING;",
                ("@cid", characterId), ("@ridable", ridableId), ("@type", purchase.ShopType),
                ("@resource", purchase.ResourceId), ("@level", purchase.Level), ("@price", purchase.Price)) == 1;

        internal void UpdateClearHeader(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, GuardianAltarHeader header)
        {
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar SET free_tickets=@free,secondary_tickets=@secondary,
    stage_stars=@stageStars,available_stars=@availableStars,menu_hint_flag=@hint,updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid AND ridable_id=@ridable;",
                    ("@cid", characterId), ("@ridable", header.RidableId), ("@free", header.FreeTickets),
                    ("@secondary", header.SecondaryTickets), ("@stageStars", header.StageStars),
                    ("@availableStars", header.AvailableStars), ("@hint", header.MenuHintFlag)) != 1)
                throw new InvalidOperationException("Guardian clear has no current character progress header.");
        }

        internal void UpdatePurchase(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, GuardianAltarPurchasePlan plan)
        {
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar_purchases SET level=@newLevel,price=@newPrice
WHERE character_id=@cid AND ridable_id=@ridable AND shop_type=@type AND resource_id=@resource
    AND level=@previousLevel AND price=@previousPrice;",
                    ("@cid", characterId), ("@ridable", ridableId),
                    ("@type", plan.Previous.ShopType), ("@resource", plan.Previous.ResourceId),
                    ("@previousLevel", plan.Previous.Level), ("@previousPrice", plan.Previous.Price),
                    ("@newLevel", plan.Updated.Level), ("@newPrice", plan.Updated.Price)) != 1)
                throw new InvalidOperationException("Guardian purchase changed before commit.");
        }

        internal void UpdateSlot(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, GuardianAltarSlotPlan plan)
        {
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar_slots SET resource_id=@new
WHERE character_id=@cid AND ridable_id=@ridable AND slot_type=@type AND slot_index=@slot
    AND locked=@locked AND resource_id=@previous;",
                    ("@cid", characterId), ("@ridable", ridableId),
                    ("@type", plan.Previous.SlotType), ("@slot", plan.Previous.SlotIndex),
                    ("@locked", plan.Previous.Locked), ("@previous", plan.Previous.ResourceId),
                    ("@new", plan.Updated.ResourceId)) != 1)
                throw new InvalidOperationException("Guardian slot changed before commit.");
        }

        internal void MarkAchievementClaimed(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, int achievementType)
        {
            if (Execute(connection, transaction, @"
INSERT INTO character_guardian_altar_achievements(character_id,ridable_id,achievement_id,claimed)
VALUES(@cid,@ridable,@type,1)
ON CONFLICT(character_id,ridable_id,achievement_id) DO UPDATE SET claimed=1 WHERE claimed=0;",
                ("@cid", characterId), ("@ridable", ridableId), ("@type", achievementType)) != 1)
                throw new InvalidOperationException("Guardian achievement changed before its mail commit.");
        }

        internal void SpendAvailableStars(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int ridableId, int previousAvailable, int cost)
        {
            if (cost < 0 || previousAvailable < cost)
                throw new ArgumentOutOfRangeException(nameof(cost));
            if (Execute(connection, transaction, @"
UPDATE character_guardian_altar SET available_stars=@remaining,updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid AND ridable_id=@ridable AND available_stars=@previous;",
                    ("@cid", characterId), ("@ridable", ridableId),
                    ("@previous", previousAvailable), ("@remaining", previousAvailable - cost)) != 1)
                throw new InvalidOperationException("Guardian available stars changed before purchase commit.");
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
