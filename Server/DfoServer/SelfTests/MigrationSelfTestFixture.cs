namespace DfoServer.SelfTests
{
    internal static class MigrationSelfTestFixture
    {
        // Legacy fixtures start from the complete current schema. Remove the
        // later columns in that isolated fixture before lowering its version;
        // production migrations must still reject an inconsistent schema.
        internal const string RemoveGuildRecommendedChannelColumns = @"
DROP INDEX IF EXISTS idx_dungeon_effect_outbox_guild_history;
ALTER TABLE guilds DROP COLUMN recommended_channel_id;
ALTER TABLE guilds DROP COLUMN recommended_server_id;
";
        internal const string RemoveGuildContributionColumns = RemoveGuildRecommendedChannelColumns + @"
ALTER TABLE guilds DROP COLUMN contribution_total;
ALTER TABLE guilds DROP COLUMN contribution_month_id;
ALTER TABLE guilds DROP COLUMN contribution_month_value;
ALTER TABLE guild_members DROP COLUMN contribution_total;
ALTER TABLE guild_members DROP COLUMN contribution_month_id;
ALTER TABLE guild_members DROP COLUMN contribution_month_value;
";
    }
}
