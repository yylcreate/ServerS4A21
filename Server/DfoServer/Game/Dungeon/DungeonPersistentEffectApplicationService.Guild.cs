using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal sealed class GuildDungeonClearEffectPayload
    {
        public int GuildId { get; set; }
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int DungeonId { get; set; }
        public byte Difficulty { get; set; }
        public int GaugeLimit { get; set; }
        public int GaugeOnClear { get; set; }
        public int MonthId { get; set; }
        public int MemberCount { get; set; }
        public List<GuildDungeonRewardChoice> Rewards { get; set; }
        public List<GuildDungeonContributionParticipant> Participants { get; set; }
    }

    internal sealed class GuildDungeonClearEffectResult
    {
        public Guid SourceEventId { get; set; }
        public int GuildId { get; set; }
        public int DungeonId { get; set; }
        public int PreviousClearCount { get; set; }
        public int ClearCount { get; set; }
        public int MonthId { get; set; }
        public int RewardGaugeRatio { get; set; }
        public int RewardItemId { get; set; }
        public int RewardCount { get; set; }
        public int RewardContribute { get; set; }
        public GuildDungeonContributionApplyResult Contribution { get; set; }
        public bool HasReward => RewardItemId > 0 && RewardCount > 0;
    }

    internal sealed partial class DungeonPersistentEffectApplicationService
    {
        internal bool TryApplyGuildDungeonClear(DungeonEffectId effectId,
            GuildDungeonAdmissionSnapshot admission, out GuildDungeonClearEffectResult result, out string error,
            GuildDungeonClearContribution contribution = null)
        {
            result = null;
            error = null;
            try
            {
                if (admission == null || effectId.SourceEventId == Guid.Empty
                    || effectId.EffectKind != DungeonPersistentEffectKinds.GuildDungeonClearCommit
                    || effectId.Scope != DungeonEffectScope.Persistent || effectId.ScopeTarget != admission.GuildId)
                    throw new InvalidOperationException("Guild clear effect does not match its frozen instance owner.");
                var existing = _outbox.Get(effectId);
                var existingPayload = existing == null ? null : ReadGuildDungeonPayload(existing);
                if (existing != null && existing.PayloadVersion < 4)
                {
                    if (!SameGuildAdmission(existingPayload, admission))
                        throw new InvalidOperationException("Legacy guild clear cannot replace its accepted owner.");
                    if (existing.PayloadVersion >= 2 && contribution != null
                        && (contribution.SourceEventId != effectId.SourceEventId
                            || contribution.MonthId != existingPayload.MonthId
                            || contribution.MemberCount != existingPayload.MemberCount))
                        throw new InvalidOperationException("Legacy guild clear cannot replace its accepted contribution.");
                    if (existing.PayloadVersion == 3 && !existingPayload.Rewards.SequenceEqual(GuildDungeonRewardRules.Freeze(admission.Rewards)))
                        throw new InvalidOperationException("Legacy guild clear cannot replace its accepted reward table.");
                    return TryExecuteGuildDungeonClear(existing, out result, out error);
                }
                if (contribution == null && existingPayload == null)
                    throw new InvalidOperationException("A new guild effect requires the original shared clear's frozen month and roster contribution.");
                contribution ??= new GuildDungeonClearContribution(effectId.SourceEventId,
                    existingPayload.MonthId, existingPayload.MemberCount) { Participants = existingPayload.Participants };
                if (contribution.SourceEventId != effectId.SourceEventId || !GuildDungeonMonth.IsValid(contribution.MonthId)
                    || contribution.MemberCount < 1 || contribution.MemberCount > 4)
                    throw new InvalidOperationException("Guild clear contribution does not match the frozen shared fact.");
                var participants = FreezeGuildContributionParticipants(contribution.Participants, contribution.MemberCount);
                var payload = new GuildDungeonClearEffectPayload
                {
                    GuildId = admission.GuildId, CharacterId = admission.SourceCharacterId,
                    AccountId = admission.SourceAccountId, DungeonId = admission.DungeonId,
                    Difficulty = admission.Difficulty, GaugeLimit = admission.GaugeLimit, GaugeOnClear = admission.GaugeOnClear,
                    MonthId = contribution.MonthId, MemberCount = contribution.MemberCount,
                    Rewards = GuildDungeonRewardRules.Freeze(admission.Rewards), Participants = participants
                };
                // Every participant uses the same canonical admission source,
                // shared clear event and payload. The existing outbox is the
                // sole durable receipt; no parallel guild receipt is created.
                var definition = CreateDefinition(effectId, payload.CharacterId, payload.AccountId, payload);
                definition.PayloadVersion = 4;
                _outbox.Enqueue(definition);
                return TryExecuteGuildDungeonClear(_outbox.Get(effectId), out result, out error);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private bool TryExecuteGuildDungeonClear(DungeonPersistentEffectRecord initialRecord,
            out GuildDungeonClearEffectResult result, out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(initialRecord.EffectId, LeaseDuration, out var reservation, out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
            {
                if (!TryDeserializeResult(record, out result, out error)) return false;
                if (result.MonthId == 0 && record.PayloadVersion == 1)
                    result.MonthId = ReadGuildDungeonPayload(record).MonthId;
                if (result.SourceEventId == Guid.Empty && record.PayloadVersion < 3)
                    result.SourceEventId = record.EffectId.SourceEventId;
                if (result.SourceEventId != record.EffectId.SourceEventId)
                { error = "Guild clear result has a different shared source."; return false; }
                return true;
            }
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            { error = "Guild clear effect claim returned " + claim; return false; }
            try
            {
                var payload = ReadGuildDungeonPayload(record);
                if (payload.GuildId <= 0 || payload.CharacterId != record.CharacterId || payload.AccountId != record.AccountId
                    || record.EffectId.Scope != DungeonEffectScope.Persistent || record.EffectId.ScopeTarget != payload.GuildId
                    || payload.DungeonId <= 0 || payload.GaugeLimit <= 0 || payload.GaugeOnClear <= 0
                    || !GuildDungeonMonth.IsValid(payload.MonthId) || payload.MemberCount < 1 || payload.MemberCount > 4)
                    throw new PermanentPersistentEffectException("Guild clear payload is incomplete or has a different owner.");
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                // A dissolved guild cannot receive or redirect the accepted
                // fact. Commit a terminal no-op instead of resurrecting it.
                bool guildExists = GuildRepository.Get(connection, transaction, payload.GuildId) != null;
                var counts = guildExists
                    ? GuildDungeonProgressRepository.ApplyClear(connection, transaction,
                        new GuildDungeonAdmissionSnapshot(payload.GuildId, payload.CharacterId, payload.AccountId,
                            payload.DungeonId, payload.Difficulty, payload.GaugeLimit, payload.GaugeOnClear,
                            Array.Empty<GuildDungeonPrerequisite>(), payload.MonthId), payload.MonthId, payload.MemberCount,
                        GuildDungeonMonth.MonthId(DateTimeOffset.FromUnixTimeMilliseconds(_outbox.UtcNowMilliseconds)))
                    : (previous: 0, current: 0);
                result = new GuildDungeonClearEffectResult
                {
                    SourceEventId = record.EffectId.SourceEventId,
                    GuildId = payload.GuildId, DungeonId = payload.DungeonId,
                    PreviousClearCount = counts.previous, ClearCount = counts.current, MonthId = payload.MonthId
                };
                if (record.PayloadVersion >= 3 && counts.current > counts.previous)
                {
                    // The server policy samples the pre-clear state, matching
                    // the reward preview before this accepted clear advances it.
                    // All participants consume this one durable choice.
                    var reward = GuildDungeonRewardRules.Select(payload.Rewards,
                        payload.GaugeLimit, payload.GaugeOnClear, counts.previous);
                    result.RewardGaugeRatio = reward.GaugeRatio;
                    result.RewardItemId = reward.ItemId;
                    result.RewardCount = reward.Count;
                    result.RewardContribute = reward.Contribute;
                    if (record.PayloadVersion == 4)
                    {
                        result.Contribution = GuildRepository.ApplyDungeonContributions(connection, transaction,
                            payload.GuildId, payload.MonthId, payload.Participants, reward.Contribute,
                            GuildDungeonMonth.MonthId(DateTimeOffset.FromUnixTimeMilliseconds(_outbox.UtcNowMilliseconds)));
                        if (!result.Contribution.Eligible)
                            throw new InvalidOperationException("Guild contribution period changed inside the clear transaction.");
                    }
                }
                if (!_outbox.TryCommitInTransaction(connection, transaction, reservation, ResultVersion,
                    Serialize(result), _outbox.UtcNowMilliseconds))
                    throw new InvalidOperationException("Guild clear outbox lease expired before commit.");
                transaction.Commit();
                return true;
            }
            catch (PermanentPersistentEffectException exception)
            {
                _outbox.TryDeadLetter(reservation, exception.Message);
                result = null; error = exception.Message; return false;
            }
            catch (Exception exception)
            {
                var committed = _outbox.Get(initialRecord.EffectId);
                if (committed?.State == DungeonPersistentEffectState.Committed
                    && TryDeserializeResult(committed, out result, out _))
                {
                    if (result.MonthId == 0 && committed.PayloadVersion == 1)
                        result.MonthId = ReadGuildDungeonPayload(committed).MonthId;
                    if (result.SourceEventId == Guid.Empty && committed.PayloadVersion < 3)
                        result.SourceEventId = committed.EffectId.SourceEventId;
                    if (result.SourceEventId != committed.EffectId.SourceEventId)
                    { error = "Guild clear result has a different shared source."; result = null; return false; }
                    return true;
                }
                _outbox.TryFail(reservation, exception.Message);
                result = null; error = exception.Message; return false;
            }
        }

        private static bool SameGuildAdmission(GuildDungeonClearEffectPayload payload, GuildDungeonAdmissionSnapshot admission)
            => payload.GuildId == admission.GuildId && payload.CharacterId == admission.SourceCharacterId
                && payload.AccountId == admission.SourceAccountId && payload.DungeonId == admission.DungeonId
                && payload.Difficulty == admission.Difficulty && payload.GaugeLimit == admission.GaugeLimit
                && payload.GaugeOnClear == admission.GaugeOnClear;

        private static List<GuildDungeonContributionParticipant> FreezeGuildContributionParticipants(
            IReadOnlyList<GuildDungeonContributionParticipant> participants, int memberCount)
        {
            if (participants == null || participants.Count != memberCount || memberCount < 1 || memberCount > 4
                || participants.Any(row => row == null || row.CharacterId <= 0 || row.AccountId <= 0)
                || participants.Select(row => row.CharacterId).Distinct().Count() != memberCount)
                throw new InvalidOperationException("Guild contribution requires the complete original participant identities.");
            return participants.OrderBy(row => row.CharacterId).ToList();
        }

        private static GuildDungeonClearEffectPayload ReadGuildDungeonPayload(DungeonPersistentEffectRecord record)
        {
            if (record == null || record.EffectId.EffectKind != DungeonPersistentEffectKinds.GuildDungeonClearCommit
                || (record.PayloadVersion != 1 && record.PayloadVersion != 2 && record.PayloadVersion != 3 && record.PayloadVersion != 4))
                throw new PermanentPersistentEffectException("Unsupported guild clear payload kind or version.");
            GuildDungeonClearEffectPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<GuildDungeonClearEffectPayload>(record.PayloadJson, JsonOptions)
                    ?? throw new PermanentPersistentEffectException("Guild clear payload is empty.");
            }
            catch (JsonException exception)
            {
                throw new PermanentPersistentEffectException("Guild clear payload is invalid JSON.", exception);
            }
            if (record.PayloadVersion == 1)
            {
                // Preserve the previously accepted single-clear contract. Its
                // durable creation time assigns it to the original month.
                payload.MonthId = GuildDungeonMonth.MonthId(DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAt));
                payload.MemberCount = 1;
            }
            if (record.PayloadVersion >= 3)
            {
                try { GuildDungeonRewardRules.Validate(payload.Rewards); }
                catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
                { throw new PermanentPersistentEffectException("Guild clear reward snapshot is invalid.", exception); }
            }
            if (record.PayloadVersion == 4)
            {
                try { FreezeGuildContributionParticipants(payload.Participants, payload.MemberCount); }
                catch (InvalidOperationException exception)
                { throw new PermanentPersistentEffectException("Guild contribution participant snapshot is invalid.", exception); }
            }
            return payload;
        }
    }
}
