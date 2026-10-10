using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Guilds
{
    internal static class GuildExperienceBonusProvider
    {
        internal static int GetMonsterExpBonusPercent(string connectionString, int characterId)
        {
            if (string.IsNullOrWhiteSpace(connectionString) || characterId <= 0) return 0;
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var guild = GuildRepository.GetForMember(connection, transaction, characterId);
            if (guild == null) return 0;
            var percent = ResolvePercent(GuildRepository.GetPurchasedContents(connection, guild.Id, transaction),
                checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            transaction.Commit();
            // Read failures propagate to the existing reward owner. Returning zero on
            // failure would commit permanently reduced experience for this kill.
            return percent;
        }

        internal static int ResolvePercent(IReadOnlyList<GuildPurchasedContent> contents, uint nowUnix)
        {
            if (contents == null) return 0;
            int result = 0;
            foreach (var content in contents)
                if (content.ExpiresAt > nowUnix)
                    result = Math.Max(result, ExperiencePercentForCategory(content.Category));
            return result;
        }

        internal static int ExperiencePercentForCategory(int category)
        {
            if (category != GuildContentCatalog.ExperienceCategory) return 0;
            int? resolved = null;
            foreach (var definition in GuildContentCatalog.All)
            {
                if (definition.Category != category) continue;
                if (definition.ExperienceBonusPercent <= 0
                    || (resolved.HasValue && resolved.Value != definition.ExperienceBonusPercent))
                    throw new InvalidDataException($"PVF guild experience category {category} has an invalid or ambiguous percentage.");
                resolved = definition.ExperienceBonusPercent;
            }
            return resolved ?? 0;
        }
    }
}
