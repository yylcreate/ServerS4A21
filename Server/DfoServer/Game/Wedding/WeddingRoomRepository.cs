using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Wedding
{
    internal sealed class WeddingRoomRepository
    {
        private readonly IGameDatabase _database;
        internal WeddingRoomRepository(IGameDatabase database) => _database = database;
        internal WeddingRoomState Load(int characterId, long now) => _database.Read(connection =>
        {
            var state = WeddingRepository.Load(connection, null, characterId);
            if (state.Status != WeddingStatus.Married) return new WeddingRoomState();
            var peer = WeddingRepository.Load(connection, null, state.PartnerId);
            if (!WeddingRules.IsReciprocal(state, peer)) return new WeddingRoomState();
            return Load(connection, null, state.PairId, now);
        });
        internal static WeddingRoomState Load(SqliteConnection connection, SqliteTransaction transaction, long pairId, long now)
        {
            var room = new WeddingRoomState { PairId = pairId };
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT tree_id,tree_expires,tree_growth,water_day,water_count,harvest_sequence,version FROM wedding_rooms WHERE pair_id=@pair;";
            command.Parameters.AddWithValue("@pair", pairId);
            using (var reader = command.ExecuteReader())
                if (reader.Read())
                { room.TreeId = reader.GetInt32(0); room.TreeExpires = reader.GetInt64(1); room.TreeGrowth = reader.GetInt32(2);
                  room.WaterDay = reader.GetInt32(3); room.WaterCount = checked((byte)reader.GetInt32(4));
                  room.HarvestSequence = reader.GetInt64(5); room.Version = reader.GetInt64(6); }
            command.CommandText = "SELECT container,slot,item_id,count FROM wedding_room_items WHERE pair_id=@pair;";
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    int kind = reader.GetInt32(0), slot = reader.GetInt32(1);
                    if (kind == 0) room.Installed[slot] = reader.GetInt32(2);
                    else room.Cargo[slot] = new(reader.GetInt32(2), reader.GetInt32(3));
                }
            room.Settle(now);
            return room;
        }
        internal static void Save(SqliteConnection connection, SqliteTransaction transaction, WeddingRoomState room)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_rooms(pair_id,tree_id,tree_expires,tree_growth,water_day,water_count,harvest_sequence,version)
VALUES(@pair,@tree,@expires,@growth,@day,@water,@sequence,1)
ON CONFLICT(pair_id) DO UPDATE SET tree_id=@tree,tree_expires=@expires,tree_growth=@growth,water_day=@day,water_count=@water,
harvest_sequence=@sequence,version=version+1 WHERE version=@version;";
            command.Parameters.AddWithValue("@pair", room.PairId); command.Parameters.AddWithValue("@tree", room.TreeId);
            command.Parameters.AddWithValue("@expires", room.TreeExpires); command.Parameters.AddWithValue("@growth", room.TreeGrowth);
            command.Parameters.AddWithValue("@day", room.WaterDay); command.Parameters.AddWithValue("@water", room.WaterCount);
            command.Parameters.AddWithValue("@sequence", room.HarvestSequence); command.Parameters.AddWithValue("@version", room.Version);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Wedding room changed concurrently.");
            command.CommandText = "DELETE FROM wedding_room_items WHERE pair_id=@pair;"; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO wedding_room_items(pair_id,container,slot,item_id,count) VALUES(@pair,@kind,@slot,@item,@count);";
            command.Parameters.AddWithValue("@kind", 0); command.Parameters.AddWithValue("@slot", 0);
            command.Parameters.AddWithValue("@item", 0); command.Parameters.AddWithValue("@count", 0);
            void Add(int kind, int slot, int item, int count)
            { command.Parameters["@kind"].Value = kind; command.Parameters["@slot"].Value = slot;
              command.Parameters["@item"].Value = item; command.Parameters["@count"].Value = count; command.ExecuteNonQuery(); }
            for (int i = 0; i < room.Installed.Length; i++) if (room.Installed[i] != 0) Add(0, i, room.Installed[i], 1);
            for (int i = 0; i < room.Cargo.Length; i++) if (room.Cargo[i] is { } item) Add(1, i, item.ItemId, item.Count);
            room.Version++;
        }
    }
}
