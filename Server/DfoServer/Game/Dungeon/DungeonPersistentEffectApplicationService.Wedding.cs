using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Dungeon
{
    internal sealed class WeddingClearEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public long ExpectedPairId { get; set; }
        public int ExpectedPartnerId { get; set; }
        public bool PartnerInFrozenRoster { get; set; }
        public bool RecommendedDungeonClear { get; set; }
    }

    internal sealed class WeddingClearEffectResult
    {
        public bool Applied { get; set; }
        public long PairId { get; set; }
        public int CharacterId { get; set; }
        public int PartnerId { get; set; }
        public bool CapsuleProgressChanged { get; set; }
        public int CapsuleProgress { get; set; }
        public bool LoveChanged { get; set; }
        public int Love { get; set; }
    }

    internal sealed partial class DungeonPersistentEffectApplicationService
    {
        internal bool TryApplyWeddingClear(DungeonEffectId effectId, int characterId, int accountId,
            long expectedPairId, int expectedPartnerId, bool partnerInFrozenRoster, bool recommendedDungeonClear,
            out WeddingClearEffectResult result, out string error)
        {
            result = null; error = null;
            try
            {
                ValidateEffectIdentity(effectId, DungeonPersistentEffectKinds.WeddingClearProgress, characterId);
                if (accountId <= 0 || expectedPairId is <= 0 or >= uint.MaxValue
                    || expectedPartnerId <= 0 || expectedPartnerId == characterId)
                    throw new ArgumentException("Wedding clear requires its captured marriage identity.");
                var payload = new WeddingClearEffectPayload
                {
                    CharacterId = characterId, AccountId = accountId, ExpectedPairId = expectedPairId,
                    ExpectedPartnerId = expectedPartnerId, PartnerInFrozenRoster = partnerInFrozenRoster,
                    RecommendedDungeonClear = recommendedDungeonClear,
                };
                _outbox.Enqueue(CreateDefinition(effectId, characterId, accountId, payload));
                var record = _outbox.Get(effectId);
                var stored = DeserializePayload<WeddingClearEffectPayload>(record, DungeonPersistentEffectKinds.WeddingClearProgress);
                if (stored.CharacterId != characterId || stored.AccountId != accountId || stored.ExpectedPairId != expectedPairId
                    || stored.ExpectedPartnerId != expectedPartnerId || stored.PartnerInFrozenRoster != partnerInFrozenRoster
                    || stored.RecommendedDungeonClear != recommendedDungeonClear)
                    throw new InvalidOperationException("Wedding clear effect was retried with different inputs.");
                return TryExecuteWeddingClear(record, out result, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private bool TryExecuteWeddingClear(DungeonPersistentEffectRecord initialRecord,
            out WeddingClearEffectResult result, out string error)
        {
            result = null; error = null;
            var claim = _outbox.TryClaim(initialRecord.EffectId, LeaseDuration, out var reservation, out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryDeserializeResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            { error = $"Persistent effect claim returned {claim}."; return false; }
            try
            {
                var payload = DeserializePayload<WeddingClearEffectPayload>(record, DungeonPersistentEffectKinds.WeddingClearProgress);
                if (payload.CharacterId != record.CharacterId || payload.AccountId != record.AccountId
                    || payload.CharacterId <= 0 || payload.AccountId <= 0 || payload.ExpectedPairId is <= 0 or >= uint.MaxValue
                    || payload.ExpectedPartnerId <= 0 || payload.ExpectedPartnerId == payload.CharacterId)
                    throw new PermanentPersistentEffectException("Wedding clear payload has invalid ownership or marriage identity.");
                ValidateEffectIdentity(record.EffectId, DungeonPersistentEffectKinds.WeddingClearProgress, payload.CharacterId);
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(deferred: false);
                var actor = WeddingRepository.LoadActor(connection, transaction, payload.CharacterId);
                var spouse = WeddingRepository.LoadActor(connection, transaction, payload.ExpectedPartnerId);
                var wedding = WeddingRepository.Load(connection, transaction, payload.CharacterId);
                var partner = WeddingRepository.Load(connection, transaction, payload.ExpectedPartnerId);
                result = new WeddingClearEffectResult
                {
                    PairId = payload.ExpectedPairId, CharacterId = payload.CharacterId, PartnerId = payload.ExpectedPartnerId,
                };
                // A durable effect belongs to the marriage captured at clear acceptance.
                // Divorce/remarriage commits the old effect as an intentional no-op.
                if (actor != null && spouse != null && !actor.Deleted && !spouse.Deleted && actor.AccountId == payload.AccountId
                    && wedding.Status == WeddingStatus.Married && wedding.PairId == payload.ExpectedPairId
                    && wedding.PartnerId == payload.ExpectedPartnerId && WeddingRules.IsReciprocal(wedding, partner))
                {
                    result.Applied = true;
                    result.CapsuleProgressChanged = payload.RecommendedDungeonClear
                        && WeddingCapsuleRepository.AccumulateAcceptedClear(connection, transaction,
                            payload.CharacterId, WeddingCapsuleCatalog.Get());
                    result.CapsuleProgress = WeddingCapsuleRepository.Load(connection, transaction, wedding.PairId, payload.CharacterId).Progress;
                    result.Love = wedding.Love;
                    if (payload.PartnerInFrozenRoster)
                    {
                        using var receipt = connection.CreateCommand();
                        receipt.Transaction = transaction;
                        receipt.CommandText = "INSERT OR IGNORE INTO wedding_progress_events(pair_id,source_event_id,created_at) VALUES(@pair,@event,@now);";
                        receipt.Parameters.AddWithValue("@pair", wedding.PairId);
                        receipt.Parameters.AddWithValue("@event", record.EffectId.SourceEventId.ToString("N"));
                        receipt.Parameters.AddWithValue("@now", _outbox.UtcNowMilliseconds / 1000);
                        if (receipt.ExecuteNonQuery() == 1)
                        {
                            var rules = WeddingCatalog.Get();
                            int love = (int)Math.Min(rules.MaximumLove, (long)wedding.Love + rules.LovePerClear);
                            if (love != wedding.Love)
                            {
                                if (!WeddingRepository.Save(connection, transaction, wedding, wedding with { Love = love }))
                                    throw new InvalidOperationException("Wedding clear lost the captured pair version.");
                                result.LoveChanged = true;
                                result.Love = love;
                            }
                        }
                    }
                }
                if (!_outbox.TryCommitInTransaction(connection, transaction, reservation, ResultVersion,
                    Serialize(result), _outbox.UtcNowMilliseconds))
                    throw new InvalidOperationException("Wedding clear effect lease was lost before commit.");
                transaction.Commit();
                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                _outbox.TryDeadLetter(reservation, ex.Message);
                result = null; error = ex.Message; return false;
            }
            catch (Exception ex)
            {
                // Recover an ambiguous commit before marking the durable effect retryable.
                try
                {
                    var committed = _outbox.Get(initialRecord.EffectId);
                    if (committed?.State == DungeonPersistentEffectState.Committed
                        && TryDeserializeResult(committed, out result, out _)) return true;
                }
                catch { }
                _outbox.TryFail(reservation, ex.Message);
                result = null; error = ex.Message; return false;
            }
        }
    }
}
