using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        // Spendable content points are independent of cumulative/monthly contributions.
        private static uint ReadUnsigned(SqliteDataReader reader, int ordinal, string field)
        {
            if (reader.GetValue(ordinal) is not long value || value < 0 || value > uint.MaxValue)
                throw new InvalidDataException("Guild " + field + " storage is not an unsigned 32-bit integer.");
            return checked((uint)value);
        }

        internal static bool IsActiveOwnedCharacter(SqliteConnection c, SqliteTransaction t, int characterId, int accountId)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM characters WHERE character_id=@character AND account_id=@account AND delete_flag=0);";
            command.Parameters.AddWithValue("@character", characterId);
            command.Parameters.AddWithValue("@account", accountId);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        internal static IReadOnlyList<GuildPurchasedContent> GetPurchasedContents(
            SqliteConnection c, int guildId, SqliteTransaction t = null)
        {
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "SELECT category,expires_at,remaining_uses FROM guild_contents WHERE guild_id=@guild ORDER BY category;";
            command.Parameters.AddWithValue("@guild", guildId);
            using var reader = command.ExecuteReader();
            var result = new List<GuildPurchasedContent>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            while (reader.Read())
            {
                var category = ReadUnsigned(reader, 0, "content category");
                var expiry = ReadUnsigned(reader, 1, "content expiry");
                var uses = ReadUnsigned(reader, 2, "content remaining uses");
                if (category == 0 || category > byte.MaxValue || expiry == 0 || uses > int.MaxValue)
                    throw new InvalidDataException("Guild purchased content cannot be represented by its protocol.");
                if (expiry > now && (category != GuildContentCatalog.PartyTeleportCategory || uses > 0))
                    result.Add(new GuildPurchasedContent((byte)category, expiry, uses));
            }
            return result.AsReadOnly();
        }

        internal static GuildResult AddPoints(SqliteConnection c, SqliteTransaction t, int guildId, uint points)
        {
            var guild = Get(c, t, guildId);
            if (guild == null) return GuildResult.NotFound;
            ulong total = (ulong)guild.Points + points;
            if (total > uint.MaxValue) return GuildResult.InvalidAmount;
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "UPDATE guilds SET points=@after WHERE guild_id=@guild AND points=@before;";
            command.Parameters.AddWithValue("@after", (long)total);
            command.Parameters.AddWithValue("@before", (long)guild.Points);
            command.Parameters.AddWithValue("@guild", guildId);
            return command.ExecuteNonQuery() == 1 ? GuildResult.Success : GuildResult.PersistenceFailed;
        }

        internal static GuildResult TrySpendPoints(SqliteConnection c, SqliteTransaction t, int guildId, uint points)
        {
            var guild = Get(c, t, guildId);
            if (guild == null) return GuildResult.NotFound;
            if (guild.Points < points) return GuildResult.InsufficientGold;
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "UPDATE guilds SET points=@after WHERE guild_id=@guild AND points=@before;";
            command.Parameters.AddWithValue("@after", (long)(guild.Points - points));
            command.Parameters.AddWithValue("@before", (long)guild.Points);
            command.Parameters.AddWithValue("@guild", guildId);
            return command.ExecuteNonQuery() == 1 ? GuildResult.Success : GuildResult.PersistenceFailed;
        }

        internal static GuildResult UpsertPurchasedContent(SqliteConnection c, SqliteTransaction t,
            int guildId, int category, int days, int remainingUses = 0)
        {
            if (guildId <= 0 || category < 1 || category > byte.MaxValue || days < -1 || remainingUses < 0)
                throw new ArgumentOutOfRangeException(nameof(category));
            long expiry = days < 0 ? GuildContentDefinition.PermanentExpiryUnix
                : checked(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)days * 86400L);
            if (expiry <= 0 || expiry > int.MaxValue) return GuildResult.InvalidAmount;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint oldExpiry = 0;
            uint oldUses = 0;
            using (var read = c.CreateCommand())
            {
                read.Transaction = t;
                read.CommandText = "SELECT expires_at,remaining_uses FROM guild_contents WHERE guild_id=@guild AND category=@category;";
                read.Parameters.AddWithValue("@guild", guildId);
                read.Parameters.AddWithValue("@category", category);
                using var reader = read.ExecuteReader();
                if (reader.Read())
                {
                    oldExpiry = ReadUnsigned(reader, 0, "content expiry");
                    oldUses = ReadUnsigned(reader, 1, "content remaining uses");
                    if (oldExpiry == 0 || oldUses > int.MaxValue)
                        throw new InvalidDataException("Guild purchased content storage is invalid.");
                    if (oldExpiry <= now) oldUses = 0;
                }
            }
            long uses = (long)oldUses + remainingUses;
            if (uses > int.MaxValue) return GuildResult.InvalidAmount;
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = @"INSERT INTO guild_contents(guild_id,category,expires_at,remaining_uses)
VALUES(@guild,@category,@expiry,@uses)
ON CONFLICT(guild_id,category) DO UPDATE SET expires_at=excluded.expires_at,remaining_uses=excluded.remaining_uses;";
            command.Parameters.AddWithValue("@guild", guildId);
            command.Parameters.AddWithValue("@category", category);
            command.Parameters.AddWithValue("@expiry", Math.Max(expiry, oldExpiry));
            command.Parameters.AddWithValue("@uses", uses);
            return command.ExecuteNonQuery() == 1 ? GuildResult.Success : GuildResult.PersistenceFailed;
        }

        // The caller supplies the captured membership, not an arbitrary guild key.
        internal static int ConsumeContentUse(IGameDatabase database, int characterId, int category,
            int expectedGuildId = 0)
        {
            if (database == null || characterId <= 0 || category <= 0 || category > byte.MaxValue) return -1;
            return database.Write((c, t) => ConsumeContentUse(c, t, characterId, category, expectedGuildId));
        }

        internal static int ConsumeContentUse(SqliteConnection c, SqliteTransaction t, int characterId,
            int category, int expectedGuildId = 0)
            => ConsumeContentUseInTransaction(c, t, characterId, expectedGuildId, category, DateTime.UtcNow);

        internal static int ConsumeContentUseInTransaction(SqliteConnection c, SqliteTransaction t,
            int characterId, int expectedGuildId, int category, DateTime utcNow)
        {
            if (c == null || t == null || !ReferenceEquals(t.Connection, c))
                throw new ArgumentException("Guild content use must participate in its operation's transaction.");
            if (characterId <= 0 || category < 1 || category > byte.MaxValue || utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var guild = GetForMember(c, t, characterId);
            if (guild == null || !IsActiveCharacter(c, t, characterId)
                || (expectedGuildId > 0 && guild.Id != expectedGuildId)) return -1;
            int remaining;
            using (var command = c.CreateCommand())
            {
                command.Transaction = t;
                command.CommandText = @"UPDATE guild_contents SET remaining_uses=remaining_uses-1
WHERE guild_id=@guild AND category=@category AND expires_at>@now AND remaining_uses>0
RETURNING remaining_uses;";
                command.Parameters.AddWithValue("@guild", guild.Id);
                command.Parameters.AddWithValue("@category", category);
                command.Parameters.AddWithValue("@now", new DateTimeOffset(utcNow).ToUnixTimeSeconds());
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value) return -1;
                remaining = checked(Convert.ToInt32(value));
            }
            if (remaining == 0)
            {
                using var command = c.CreateCommand();
                command.Transaction = t;
                command.CommandText = "DELETE FROM guild_contents WHERE guild_id=@guild AND category=@category AND remaining_uses=0;";
                command.Parameters.AddWithValue("@guild", guild.Id);
                command.Parameters.AddWithValue("@category", category);
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The consumed guild content did not reach its committed empty state.");
            }
            return remaining;
        }
    }
}
