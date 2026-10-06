using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Guilds
{
    internal sealed partial class GuildRepository
    {
        // The shared clear's existing outbox owns deduplication. This method
        // participates in its transaction and never creates a separate receipt.
        internal static GuildDungeonContributionApplyResult ApplyDungeonContributions(
            SqliteConnection connection, SqliteTransaction transaction, int guildId, int monthId,
            IReadOnlyList<GuildDungeonContributionParticipant> participants, int amount, int currentMonthId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(participants);
            if (!ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("Guild contributions must use the clear owner's transaction.", nameof(transaction));
            if (guildId <= 0 || amount < 0 || !GuildDungeonMonth.IsValid(monthId)
                || !GuildDungeonMonth.IsValid(currentMonthId))
                throw new ArgumentOutOfRangeException(nameof(guildId));
            var frozen = participants.ToArray();
            var seen = new HashSet<int>();
            if (frozen.Length < 1 || frozen.Length > 4)
                throw new InvalidDataException("Guild contribution requires one to four frozen participants.");
            foreach (var participant in frozen)
                if (participant == null || participant.CharacterId <= 0 || participant.AccountId <= 0
                    || !seen.Add(participant.CharacterId))
                    throw new InvalidDataException("Guild contribution participant identity is invalid or duplicated.");

            var noMembers = Array.Empty<GuildMemberContributionApplied>();
            if (!TryReadGuildContribution(connection, transaction, guildId, out var guild))
                return new(guildId, currentMonthId, amount, false, false, 0, 0, 0, 0, noMembers);
            int monthBefore = ProjectContribution(guild, currentMonthId).MonthValue;
            if (monthId != currentMonthId)
                return new(guildId, currentMonthId, amount, false, false,
                    guild.Total, guild.Total, monthBefore, monthBefore, noMembers);
            if (guild.MonthId > currentMonthId)
                throw new InvalidDataException("Guild contribution cannot overwrite a future operating month.");

            // The accepted fact remains owned by its original guild. Leaving
            // does not retract that guild's contribution, but a new membership
            // must never receive the former guild's personal contribution.
            var eligible = new List<(GuildDungeonContributionParticipant Participant, GuildContributionSnapshot Before)>();
            foreach (var participant in frozen.OrderBy(row => row.CharacterId))
            {
                if (!TryReadCurrentMemberContribution(connection, transaction, guildId, participant, out var member))
                    continue;
                if (member.MonthId > currentMonthId)
                    throw new InvalidDataException("Guild member contribution cannot overwrite a future operating month.");
                eligible.Add((participant, member));
            }
            if (amount == 0)
                return new(guildId, currentMonthId, amount, true, false,
                    guild.Total, guild.Total, monthBefore, monthBefore, noMembers);

            long guildAmount = (long)amount * frozen.Length;
            int totalAfter = AddContribution(guild.Total, guildAmount);
            int monthAfter = AddContribution(monthBefore, guildAmount);
            var appliedMembers = new List<GuildMemberContributionApplied>();
            foreach (var member in eligible)
            {
                int memberMonthBefore = ProjectContribution(member.Before, currentMonthId).MonthValue;
                appliedMembers.Add(new GuildMemberContributionApplied(member.Participant.CharacterId,
                    member.Participant.AccountId, member.Before.Total, AddContribution(member.Before.Total, amount),
                    memberMonthBefore, AddContribution(memberMonthBefore, amount)));
            }

            // Validate every owner and every resulting value before the first
            // write, so an overflow or future member leaves the whole clear retryable.
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE guilds SET contribution_total=@total,
contribution_month_id=@month, contribution_month_value=@value WHERE guild_id=@guild;";
                command.Parameters.AddWithValue("@total", totalAfter);
                command.Parameters.AddWithValue("@month", currentMonthId);
                command.Parameters.AddWithValue("@value", monthAfter);
                command.Parameters.AddWithValue("@guild", guildId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The frozen guild contribution owner disappeared before commit.");
            }
            foreach (var member in appliedMembers)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"UPDATE guild_members SET contribution_total=@total,
contribution_month_id=@month, contribution_month_value=@value
WHERE guild_id=@guild AND character_id=@character
AND EXISTS(SELECT 1 FROM characters c WHERE c.character_id=@character AND c.account_id=@account AND c.delete_flag=0);";
                command.Parameters.AddWithValue("@total", member.TotalAfter);
                command.Parameters.AddWithValue("@month", currentMonthId);
                command.Parameters.AddWithValue("@value", member.MonthAfter);
                command.Parameters.AddWithValue("@guild", guildId);
                command.Parameters.AddWithValue("@character", member.CharacterId);
                command.Parameters.AddWithValue("@account", member.AccountId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("A frozen guild member contribution owner changed before commit.");
            }
            return new(guildId, currentMonthId, amount, true, true,
                guild.Total, totalAfter, monthBefore, monthAfter, appliedMembers.AsReadOnly());
        }

        private static bool TryReadGuildContribution(SqliteConnection connection, SqliteTransaction transaction,
            int guildId, out GuildContributionSnapshot contribution)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT contribution_total, contribution_month_id, contribution_month_value,
typeof(contribution_total), typeof(contribution_month_id), typeof(contribution_month_value)
FROM guilds WHERE guild_id=@guild;";
            command.Parameters.AddWithValue("@guild", guildId);
            using var reader = command.ExecuteReader();
            contribution = reader.Read() ? ReadContribution(reader, 0, 3) : null;
            return contribution != null;
        }

        private static bool TryReadCurrentMemberContribution(SqliteConnection connection, SqliteTransaction transaction,
            int guildId, GuildDungeonContributionParticipant participant, out GuildContributionSnapshot contribution)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT m.contribution_total, m.contribution_month_id, m.contribution_month_value,
typeof(m.contribution_total), typeof(m.contribution_month_id), typeof(m.contribution_month_value),
c.account_id, c.delete_flag, typeof(c.account_id), typeof(c.delete_flag)
FROM guild_members m JOIN characters c ON c.character_id=m.character_id
WHERE m.guild_id=@guild AND m.character_id=@character AND c.account_id=@account AND c.delete_flag=0;";
            command.Parameters.AddWithValue("@guild", guildId);
            command.Parameters.AddWithValue("@character", participant.CharacterId);
            command.Parameters.AddWithValue("@account", participant.AccountId);
            using var reader = command.ExecuteReader();
            contribution = null;
            if (!reader.Read()) return false;
            if (ReadContributionInteger(reader, 6, 8) != participant.AccountId
                || ReadContributionInteger(reader, 7, 9) != 0)
                throw new InvalidDataException("Guild member contribution identity is malformed.");
            contribution = ReadContribution(reader, 0, 3);
            return true;
        }

        private static GuildContributionSnapshot ReadContribution(SqliteDataReader reader, int valueOrdinal, int typeOrdinal)
        {
            int total = ReadContributionInteger(reader, valueOrdinal, typeOrdinal);
            int monthId = ReadContributionInteger(reader, valueOrdinal + 1, typeOrdinal + 1);
            int value = ReadContributionInteger(reader, valueOrdinal + 2, typeOrdinal + 2);
            if (!GuildDungeonMonth.IsValid(monthId))
                throw new InvalidDataException("Guild contribution has an invalid operating month.");
            return new(total, monthId, value);
        }

        private static int ReadContributionInteger(SqliteDataReader reader, int ordinal, int typeOrdinal)
        {
            if (reader.IsDBNull(ordinal) || reader.IsDBNull(typeOrdinal) || reader.GetString(typeOrdinal) != "integer")
                throw new InvalidDataException("Guild contribution storage must use SQLite INTEGER values.");
            long value = reader.GetInt64(ordinal);
            if (value < 0 || value > int.MaxValue)
                throw new InvalidDataException("Guild contribution is outside the client's signed Int32 range.");
            return (int)value;
        }

        private static GuildContributionSnapshot ProjectContribution(GuildContributionSnapshot contribution, int monthId)
        {
            if (!GuildDungeonMonth.IsValid(monthId))
                throw new ArgumentOutOfRangeException(nameof(monthId));
            return new(contribution.Total, monthId, contribution.MonthId == monthId ? contribution.MonthValue : 0);
        }

        private static int AddContribution(int before, long amount)
        {
            long after = before + amount;
            if (after < 0 || after > int.MaxValue)
                throw new InvalidDataException("Guild contribution cannot exceed the client's signed Int32 range.");
            return (int)after;
        }
    }
}
