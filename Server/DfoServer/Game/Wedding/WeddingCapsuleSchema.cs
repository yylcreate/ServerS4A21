namespace DfoServer.Game.Wedding
{
    internal static class WeddingCapsuleSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_capsules (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id) ON DELETE CASCADE,
    character_id INTEGER NOT NULL REFERENCES characters(character_id) ON DELETE CASCADE,
    progress INTEGER NOT NULL DEFAULT 0 CHECK(progress BETWEEN 0 AND 2147483647),
    own_count INTEGER NOT NULL DEFAULT 0 CHECK(own_count BETWEEN 0 AND 255),
    taken_count INTEGER NOT NULL DEFAULT 0 CHECK(taken_count BETWEEN 0 AND 255),
    PRIMARY KEY(pair_id,character_id)
);";
    }
}
