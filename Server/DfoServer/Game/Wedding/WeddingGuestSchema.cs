namespace DfoServer.Game.Wedding
{
    internal static class WeddingGuestSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_guest_invitations (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    ceremony_start_at INTEGER NOT NULL CHECK(ceremony_start_at BETWEEN 1 AND 4294967295),
    guest_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    inviter_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    created_at INTEGER NOT NULL CHECK(created_at BETWEEN 0 AND 4294967295),
    CHECK(guest_character_id<>inviter_character_id),
    PRIMARY KEY(pair_id,ceremony_start_at,guest_character_id)
);
CREATE INDEX IF NOT EXISTS idx_wedding_guest_invitations_host
    ON wedding_guest_invitations(pair_id,ceremony_start_at,inviter_character_id,guest_character_id);
CREATE INDEX IF NOT EXISTS idx_wedding_guest_invitations_guest
    ON wedding_guest_invitations(guest_character_id,pair_id,ceremony_start_at);";
    }
}
