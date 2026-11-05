using DfoServer.Game.Wedding;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    internal static class WeddingGuestsMigration
    {
        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = WeddingGuestSchema.Ddl + WeddingMoneyGiftSchema.Ddl
                + WeddingReservationLedger.Ddl + WeddingCeremonyCommentSchema.Ddl;
            command.ExecuteNonQuery();
        }
    }
}
