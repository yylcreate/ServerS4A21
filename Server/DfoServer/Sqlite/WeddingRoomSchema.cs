namespace DfoServer.Sqlite
{
    internal static class WeddingRoomSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_rooms (
    pair_id INTEGER PRIMARY KEY REFERENCES wedding_pairs(pair_id) ON DELETE CASCADE,
    tree_id INTEGER NOT NULL DEFAULT 0 CHECK(tree_id>=0),
    tree_expires INTEGER NOT NULL DEFAULT 0 CHECK(tree_expires BETWEEN 0 AND 4294967295),
    tree_growth INTEGER NOT NULL DEFAULT 0 CHECK(tree_growth>=0),
    water_day INTEGER NOT NULL DEFAULT 0 CHECK(water_day>=0),
    water_count INTEGER NOT NULL DEFAULT 0 CHECK(water_count BETWEEN 0 AND 255),
    harvest_sequence INTEGER NOT NULL DEFAULT 0 CHECK(harvest_sequence>=0),
    version INTEGER NOT NULL DEFAULT 0 CHECK(version>=0)
);
CREATE TABLE IF NOT EXISTS wedding_room_items (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id) ON DELETE CASCADE,
    container INTEGER NOT NULL CHECK(container IN (0,1)),
    slot INTEGER NOT NULL CHECK((container=0 AND slot BETWEEN 0 AND 27) OR (container=1 AND slot BETWEEN 0 AND 39)),
    item_id INTEGER NOT NULL CHECK(item_id>0),
    count INTEGER NOT NULL CHECK((container=0 AND count=1) OR (container=1 AND count BETWEEN 1 AND 65535)),
    PRIMARY KEY(pair_id,container,slot)
);";
    }
}
