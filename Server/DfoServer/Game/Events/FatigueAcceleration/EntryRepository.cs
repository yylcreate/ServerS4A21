using System;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.FatigueAcceleration
{
    internal sealed class FatigueAccelerationEntryRepository
    {
        private readonly IGameDatabase _database;
        private readonly GameEventRepository _events;

        internal FatigueAccelerationEntryRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _events = new GameEventRepository(database);
        }

        internal bool IsEnabled() => _events.IsEnabled(FatigueAccelerationEntryService.EventId);

        // Uses the current event schema and one transaction. Missing environment
        // settings preserve the operator's saved switch and existing metadata.
        internal void EnsureEntry(bool? enabled)
        {
            _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO game_event_state(event_id, state) VALUES(@eventId, 0);
INSERT OR IGNORE INTO game_event_info_details(
    event_id, unknown0, start_notice, end_notice, detail_flag, sort_order)
VALUES(@eventId, 0, @startNotice, @endNotice, 0, 115);";
                command.Parameters.AddWithValue("@eventId", FatigueAccelerationEntryService.EventId);
                command.Parameters.AddWithValue("@startNotice", "疲劳值燃烧入口已开放。");
                command.Parameters.AddWithValue("@endNotice", "疲劳值燃烧入口已关闭。");
                command.ExecuteNonQuery();
                if (!enabled.HasValue)
                    return;
                command.Parameters.Clear();
                command.CommandText = "UPDATE game_event_state SET state=@state WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@state", enabled.Value ? 1 : 0);
                command.Parameters.AddWithValue("@eventId", FatigueAccelerationEntryService.EventId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Fatigue entry switch update failed.");
            });
        }
    }
}
