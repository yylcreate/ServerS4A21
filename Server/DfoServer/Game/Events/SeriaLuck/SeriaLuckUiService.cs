using System;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Events.SeriaLuck
{
    // Only projects the existing account counter; never grants or authorizes rewards.
    internal sealed class SeriaLuckUiService
    {
        // Client 13A28A4 queries this EVENT_INFO main member before showing the gauge.
        internal const ushort EventId = 2037;
        internal const string DefinitionPath = "etc/boostergage.etc";
        private readonly IGameDatabase _database;
        private readonly Lazy<BoosterGageConfigFile> _definition;

        internal SeriaLuckUiService(IGameDatabase database, Func<BoosterGageConfigFile> definition = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _definition = new Lazy<BoosterGageConfigFile>(definition ?? LoadDefinition);
        }

        internal BoosterGageConfigFile Definition => _definition.Value;

        internal void Initialize()
        {
            var valid = TryGetGauge(SqliteAccountRepository.SeriaLuckValueMax, out _);
            // Merge with the complete catalog, preserving saved switches and metadata.
            // This transaction has no schema, inventory, luck or reward mutations.
            _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO game_event_state(event_id,state) VALUES(@eventId,@state);
INSERT OR IGNORE INTO game_event_info_details(
    event_id,unknown0,start_notice,end_notice,detail_flag,sort_order)
VALUES(@eventId,0,'','',0,@eventId);";
                command.Parameters.AddWithValue("@eventId", EventId);
                command.Parameters.AddWithValue("@state", valid ? 1 : 0);
                command.ExecuteNonQuery();
                if (valid) return;
                command.CommandText = "UPDATE game_event_state SET state=0 WHERE event_id=@eventId;";
                command.ExecuteNonQuery();
            });
            if (!valid)
                FileLogger.Log("[SeriaLuckUi] PVF gauge incompatible with the account counter; UI closed: "
                    + (Definition?.InvalidReason ?? "definition unavailable"));
        }

        internal bool TryGetGauge(int luckValue, out byte gauge)
        {
            gauge = 0;
            var definition = Definition;
            if (luckValue < 0 || luckValue > SqliteAccountRepository.SeriaLuckValueMax
                || definition?.IsValid != true
                || !definition.TryGetBoosterPoint(SeriaLuckItemConstants.ItemTemplateId, out var point)
                || point <= 0
                || (long)point * SqliteAccountRepository.SeriaLuckValueMax != definition.MaxGage)
                return false;
            gauge = checked((byte)(luckValue * point));
            return true;
        }

        private static BoosterGageConfigFile LoadDefinition()
        {
            try { return BoosterGageConfigFile.Parse(PvfArchiveAccessor.ReadText(DefinitionPath)); }
            catch (Exception ex)
            {
                FileLogger.Log("[SeriaLuckUi] PVF read failed: " + ex.Message);
                return BoosterGageConfigFile.Parse(null);
            }
        }
    }
}
