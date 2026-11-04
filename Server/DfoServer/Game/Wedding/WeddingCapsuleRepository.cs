using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingCapsuleState(int Progress = 0, byte Own = 0, byte Taken = 0);
    internal sealed record WeddingCapsuleProjection(WeddingCapsuleState State, bool SpouseHasPending);

    internal sealed class WeddingCapsuleRepository
    {
        private readonly IGameDatabase _database;
        internal WeddingCapsuleRepository(IGameDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

        internal WeddingCapsuleProjection LoadProjection(int characterId) => _database.Read(c =>
        {
            using var transaction = c.BeginTransaction(deferred: true);
            return LoadProjection(c, transaction, characterId);
        });

        internal static WeddingCapsuleProjection LoadProjection(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var actor = WeddingRepository.Load(connection, transaction, characterId);
            if (actor.Status != WeddingStatus.Married) return new(new(), false);
            var spouse = WeddingRepository.Load(connection, transaction, actor.PartnerId);
            if (!WeddingRules.IsReciprocal(actor, spouse)) throw new InvalidOperationException("Nonreciprocal wedding capsule owner.");
            var own = Load(connection, transaction, actor.PairId, characterId);
            var other = Load(connection, transaction, actor.PairId, actor.PartnerId);
            return new(own, other.Taken > 0);
        }

        internal static WeddingCapsuleState Load(SqliteConnection connection, SqliteTransaction transaction, long pairId, int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT progress,own_count,taken_count FROM wedding_capsules WHERE pair_id=@pair AND character_id=@cid;";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetInt32(0), checked((byte)reader.GetInt32(1)), checked((byte)reader.GetInt32(2))) : new();
        }

        internal static void Save(SqliteConnection connection, SqliteTransaction transaction, long pairId, int characterId, WeddingCapsuleState state)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO wedding_capsules(pair_id,character_id,progress,own_count,taken_count)
VALUES(@pair,@cid,@progress,@own,@taken) ON CONFLICT(pair_id,character_id)
DO UPDATE SET progress=excluded.progress,own_count=excluded.own_count,taken_count=excluded.taken_count;";
            command.Parameters.AddWithValue("@pair", pairId); command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@progress", state.Progress); command.Parameters.AddWithValue("@own", state.Own);
            command.Parameters.AddWithValue("@taken", state.Taken);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Capsule state was not saved.");
        }

        // The caller owns the accepted, receipted recommended-clear transaction.
        // This method does not issue an item and must not be invoked per kill or before settlement acceptance.
        internal static bool AccumulateAcceptedClear(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, WeddingCapsuleCatalog catalog)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(catalog);
            var actor = WeddingRepository.Load(connection, transaction, characterId);
            if (actor.Status != WeddingStatus.Married) return false;
            var spouse = WeddingRepository.Load(connection, transaction, actor.PartnerId);
            if (!WeddingRules.IsReciprocal(actor, spouse)) throw new InvalidOperationException("Nonreciprocal wedding capsule owner.");
            var owner = WeddingRepository.LoadActor(connection, transaction, characterId);
            if (owner == null || owner.Deleted) return false;
            var state = Load(connection, transaction, actor.PairId, characterId);
            int progress = (int)Math.Min(catalog.MaximumGauge, (long)state.Progress + catalog.GaugePerClear);
            if (progress == state.Progress) return false;
            Save(connection, transaction, actor.PairId, characterId, state with { Progress = progress });
            return true;
        }
    }
}
