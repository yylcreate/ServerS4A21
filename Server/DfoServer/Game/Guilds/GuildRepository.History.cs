using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        private static readonly JsonSerializerOptions HistoryJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
        };

        // The committed clear result is the history owner. Reading it never
        // creates a second receipt or reconstructs awards from current totals.
        internal GuildContributionHistory GetContributionHistoryForMember(int characterId, int accountId,
            int maximumRows = 50) => Database.Read(connection =>
        {
            if (characterId <= 0 || accountId <= 0 || maximumRows < 1 || maximumRows > 50)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            using var command = connection.CreateCommand();
            // Membership, original accepted participants, committed amounts,
            // timestamps and current names share this one SQLite read snapshot.
            command.CommandText = @"SELECT self.guild_id,requester.account_id,
o.source_event_id,o.payload_json,o.result_json,o.committed_at,
p.value,c.name
FROM guild_members self
JOIN characters requester ON requester.character_id=self.character_id
    AND requester.account_id=@account AND requester.delete_flag=0
LEFT JOIN dungeon_persistent_effect_outbox o ON o.scope_target=self.guild_id
    AND o.effect_kind=@kind AND o.effect_scope=@scope AND o.state=@committed
    AND o.payload_version=4 AND o.result_version=1
    AND json_extract(o.result_json,'$.contribution.applied')=1
LEFT JOIN json_each(COALESCE(o.payload_json,'{}'),'$.participants') p
LEFT JOIN characters c ON c.character_id=json_extract(p.value,'$.characterId')
    AND c.account_id=json_extract(p.value,'$.accountId')
WHERE self.character_id=@character
ORDER BY o.committed_at DESC,o.source_event_id DESC,p.key
LIMIT @limit;";
            command.Parameters.AddWithValue("@character", characterId);
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@kind", DungeonPersistentEffectKinds.GuildDungeonClearCommit);
            command.Parameters.AddWithValue("@scope", (int)DungeonEffectScope.Persistent);
            command.Parameters.AddWithValue("@committed", (int)DungeonPersistentEffectState.Committed);
            command.Parameters.AddWithValue("@limit", maximumRows);
            using var reader = command.ExecuteReader();
            int guildId = 0;
            var rows = new List<GuildContributionHistoryRow>();
            var seen = new HashSet<(Guid Source, int Character)>();
            while (reader.Read())
            {
                guildId = reader.GetInt32(0);
                if (reader.GetInt32(1) != accountId)
                    throw new InvalidDataException("Guild contribution history requester identity changed.");
                if (reader.IsDBNull(2)) continue;
                if (!Guid.TryParse(reader.GetString(2), out var source) || source == Guid.Empty
                    || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6))
                    throw new InvalidDataException("Committed guild contribution history is incomplete.");
                var payload = JsonSerializer.Deserialize<GuildDungeonClearEffectPayload>(reader.GetString(3), HistoryJsonOptions);
                var result = JsonSerializer.Deserialize<GuildDungeonClearEffectResult>(reader.GetString(4), HistoryJsonOptions);
                var participant = JsonSerializer.Deserialize<GuildDungeonContributionParticipant>(reader.GetString(6), HistoryJsonOptions);
                var contribution = result?.Contribution;
                if (payload?.GuildId != guildId || result?.GuildId != guildId || result.SourceEventId != source
                    || contribution?.GuildId != guildId || !contribution.Applied || !contribution.Eligible
                    || contribution.MonthId != payload.MonthId || result.MonthId != payload.MonthId
                    || !GuildDungeonMonth.IsValid(payload.MonthId)
                    || payload.DungeonId <= 0 || result.DungeonId != payload.DungeonId
                    || result.PreviousClearCount < 0
                    || (long)result.ClearCount - result.PreviousClearCount != payload.MemberCount
                    || payload.Participants == null || payload.Participants.Count != payload.MemberCount
                    || payload.MemberCount < 1 || payload.MemberCount > 4
                    || payload.Participants.Any(row => row == null || row.CharacterId <= 0 || row.AccountId <= 0)
                    || payload.Participants.Select(row => row.CharacterId).Distinct().Count() != payload.MemberCount
                    || contribution.Amount <= 0 || result.RewardContribute != contribution.Amount
                    || contribution.TotalBefore < 0 || contribution.MonthBefore < 0
                    || (long)contribution.TotalAfter - contribution.TotalBefore != (long)contribution.Amount * payload.MemberCount
                    || (long)contribution.MonthAfter - contribution.MonthBefore != (long)contribution.Amount * payload.MemberCount
                    || participant == null || !payload.Participants.Contains(participant)
                    || !seen.Add((source, participant.CharacterId)))
                    throw new InvalidDataException("Committed guild contribution history does not match its accepted clear.");
                long committedAt = reader.GetInt64(5);
                if (committedAt < 0)
                    throw new InvalidDataException("Guild contribution history has an invalid commit timestamp.");
                rows.Add(new GuildContributionHistoryRow(source, participant.CharacterId, participant.AccountId,
                    reader.IsDBNull(7) ? string.Empty : ReadCharacterName(reader, 7),
                    DateTimeOffset.FromUnixTimeMilliseconds(committedAt), contribution.Amount));
            }
            return guildId == 0 ? null : new GuildContributionHistory(guildId, characterId, accountId, rows.AsReadOnly());
        });
    }
}
