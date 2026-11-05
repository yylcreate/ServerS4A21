namespace DfoServer.Game.Wedding
{
    internal static class WeddingMoneyGiftSchema
    {
        internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS wedding_money_gifts (
    pair_id INTEGER NOT NULL REFERENCES wedding_pairs(pair_id),
    ceremony_start_at INTEGER NOT NULL CHECK(ceremony_start_at>0),
    guest_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    left_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    right_character_id INTEGER NOT NULL REFERENCES characters(character_id),
    ceremony INTEGER NOT NULL CHECK(ceremony BETWEEN 0 AND 2),
    amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 2147483647),
    charge_percent INTEGER NOT NULL CHECK(charge_percent BETWEEN 0 AND 100),
    charge_gold INTEGER NOT NULL CHECK(charge_gold>=0),
    net_gold INTEGER NOT NULL CHECK(net_gold>=0),
    blessing BLOB NOT NULL CHECK(length(blessing)<=20),
    created_at INTEGER NOT NULL,
    state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 2),
    completed_at INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(pair_id,ceremony_start_at,guest_character_id),
    CHECK(left_character_id<right_character_id),
    CHECK(guest_character_id<>left_character_id AND guest_character_id<>right_character_id),
    CHECK(charge_gold+net_gold=amount)
);
CREATE INDEX IF NOT EXISTS idx_wedding_money_gifts_pending_guest ON wedding_money_gifts(guest_character_id) WHERE state=0;
CREATE TABLE IF NOT EXISTS wedding_money_gift_items (
    pair_id INTEGER NOT NULL,
    ceremony_start_at INTEGER NOT NULL,
    guest_character_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    item_id INTEGER NOT NULL CHECK(item_id>0),
    item_count INTEGER NOT NULL CHECK(item_count>0),
    PRIMARY KEY(pair_id,ceremony_start_at,guest_character_id,ordinal),
    FOREIGN KEY(pair_id,ceremony_start_at,guest_character_id)
      REFERENCES wedding_money_gifts(pair_id,ceremony_start_at,guest_character_id)
);";
    }
}
