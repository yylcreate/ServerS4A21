using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    internal static class WeddingSchema
    {
        internal const string Sql = @"
CREATE TABLE IF NOT EXISTS wedding_pairs (
    pair_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(pair_id BETWEEN 1 AND 4294967294),
    left_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    right_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),
    ceremony INTEGER NOT NULL DEFAULT 0 CHECK(ceremony BETWEEN 0 AND 2),
    ring INTEGER NOT NULL DEFAULT 0 CHECK(ring BETWEEN 0 AND 5),
    love INTEGER NOT NULL DEFAULT 0 CHECK(love >= 0),
    married_at INTEGER NOT NULL DEFAULT 0,
    start_at INTEGER NOT NULL DEFAULT 0,
    channel_id INTEGER NOT NULL DEFAULT 0,
    room INTEGER NOT NULL DEFAULT 0 CHECK(room BETWEEN 0 AND 255),
    version INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    CHECK(left_character_id < right_character_id)
);
CREATE TABLE IF NOT EXISTS wedding_members (
    character_id INTEGER PRIMARY KEY REFERENCES characters(character_id) ON DELETE CASCADE,
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    seat INTEGER NOT NULL CHECK(seat IN (0,1)),
    UNIQUE(pair_id, seat)
);
CREATE TABLE IF NOT EXISTS wedding_character_cooldowns (
    character_id INTEGER PRIMARY KEY REFERENCES characters(character_id) ON DELETE CASCADE,
    divorced_at INTEGER NOT NULL DEFAULT 0,
    next_wedding_at INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_wedding_reserved_room
    ON wedding_pairs(channel_id, room) WHERE status IN (2,3);
CREATE INDEX IF NOT EXISTS idx_wedding_pending_ceremony
    ON wedding_pairs(status, start_at) WHERE status IN (2,3);
CREATE TABLE IF NOT EXISTS wedding_progress_events (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    source_event_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY(pair_id, source_event_id)
);";

        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Sql + WeddingRoomSchema.Ddl + Game.Wedding.WeddingCapsuleSchema.Ddl
                + Game.Wedding.WeddingGuestBookSchema.Ddl;
            command.ExecuteNonQuery();
        }
    }
}
