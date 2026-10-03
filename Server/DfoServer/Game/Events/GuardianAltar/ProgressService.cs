using System;
using System.Globalization;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.GuardianAltar
{
    // Durable role progress is the only menu-state authority. Ordinary clears
    // use the caller's inventory/outbox transaction; socket projections and
    // combat state remain with the Guardian coordinator/runtime.
    internal sealed class GuardianAltarProgressService : IGuardianAltarProgressService,
        IGuardianAltarPurchaseService, IGuardianAltarSlotService, IGuardianAltarAchievementService,
        IGuardianAltarExchangeService, IGuardianAltarExistingMenuService
    {
        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;
        private readonly Lazy<GuardianAltarMenuDefinition> _definition;
        private readonly GuardianAltarProgressRepository _repository = new();
        private readonly MailboxService _mailbox;

        internal GuardianAltarProgressService(IGameDatabase database, DailyResetService dailyReset,
            GuardianAltarMenuDefinition definition, MailboxService mailbox = null)
            : this(database, dailyReset, definition == null
                ? throw new ArgumentNullException(nameof(definition)) : () => definition, mailbox)
        {
        }

        internal GuardianAltarProgressService(IGameDatabase database, DailyResetService dailyReset,
            Func<GuardianAltarMenuDefinition> definitionLoader, MailboxService mailbox = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset ?? throw new ArgumentNullException(nameof(dailyReset));
            _mailbox = mailbox;
            ArgumentNullException.ThrowIfNull(definitionLoader);
            _definition = new Lazy<GuardianAltarMenuDefinition>(() => definitionLoader()
                ?? throw new InvalidOperationException("Guardian menu definition loader returned no definition."));
        }

        public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow,
            Func<bool> isCurrent = null)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Guardian progress requires an explicit UTC anchor.", nameof(utcNow));
            var definition = _definition.Value;
            var refillKey = "guardian_altar_refill_v1:"
                + definition.RidableId.ToString(CultureInfo.InvariantCulture);
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacter(connection, transaction, characterId);
                _repository.ValidateRefillReceipt(connection, transaction, characterId, refillKey);
                _repository.TryInitialize(connection, transaction, characterId, definition);

                // Validate owned facts before deriving an earned unlock.
                // Missing ordinary rows, purchases and slots are not recreated.
                var existing = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(existing, definition);
                if (TryUnlockEligibleSurvivalStages(connection, transaction, existing, definition))
                    _repository.UpdateClearHeader(connection, transaction, characterId,
                        existing.Header with { MenuHintFlag = 1 });
                if (!_dailyReset.TryGetCounter(connection, transaction, characterId, refillKey,
                        DailyResetService.PeriodDay, utcNow, out var receipt))
                    throw new InvalidOperationException("Guardian refill anchor is older than persisted daily state.");
                if (receipt < 0 || receipt > 1)
                    throw new InvalidOperationException("Guardian daily refill receipt is inconsistent.");
                if (receipt == 0)
                {
                    if (!_dailyReset.TryClaimFlag(connection, transaction, characterId, refillKey,
                            DailyResetService.PeriodDay, utcNow))
                        throw new InvalidOperationException("Guardian daily refill could not reserve its current-day receipt.");
                    // This is the verified daily replacement rule, not += and
                    // not a reset of secondary tickets or any persistent stars.
                    _repository.ReplaceFreeTickets(connection, transaction, characterId,
                        definition.RidableId, definition.DailyRefillTicket);
                }
                var result = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(result, definition);
                RequireCurrentGeneration(isCurrent);
                return result;
            });
        }

        internal string DatabaseConnectionString => _database.ConnectionString;

        public GuardianAltarProgressSnapshot ReadExistingMenu(int characterId, int accountId,
            Func<bool> isCurrent = null)
        {
            if (characterId <= 0 || accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var definition = _definition.Value;
            return _database.Read(connection =>
            {
                // One deferred read snapshot keeps the header and all vectors
                // consistent. It never creates progress or changes a daily receipt.
                using var transaction = connection.BeginTransaction(deferred: true);
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
                var result = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(result, definition);
                RequireCurrentGeneration(isCurrent);
                transaction.Commit();
                return result;
            });
        }

        public GuardianAltarPurchaseResult Buy(int characterId, int accountId,
            GuardianAltarPurchaseRequest request, Func<bool> isCurrent = null)
        {
            if (characterId <= 0 || accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            // Prepare the immutable resource owner before entering SQLite.
            // Buying does not initialize/refill tickets or normalize progress.
            var definition = _definition.Value;
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
                var before = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(before, definition);
                var previous = before.Purchases.SingleOrDefault(x => x.ShopType == request.ShopType
                    && x.ResourceId == request.ResourceId);
                if (!definition.TryCreatePurchasePlan(before, request, out var plan, out var reason))
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarPurchaseResult(GuardianAltarPurchaseOutcome.CannotPurchase,
                        before, previous?.Level ?? 0, previous?.Level ?? 0, false, reason);
                }
                if (before.Header.AvailableStars < plan.Cost)
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarPurchaseResult(GuardianAltarPurchaseOutcome.InsufficientStars,
                        before, plan.Previous.Level, plan.Previous.Level, false, "insufficient stars");
                }
                if (!plan.IsReplay)
                {
                    _repository.UpdatePurchase(connection, transaction, characterId, definition.RidableId, plan);
                    _repository.SpendAvailableStars(connection, transaction, characterId, definition.RidableId,
                        before.Header.AvailableStars, plan.Cost);
                }
                var after = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(after, definition);
                RequireCurrentGeneration(isCurrent);
                return new GuardianAltarPurchaseResult(GuardianAltarPurchaseOutcome.Success,
                    after, plan.Previous.Level, plan.Updated.Level, !plan.IsReplay);
            });
        }

        public GuardianAltarSlotResult SetSlot(int characterId, int accountId,
            GuardianAltarSlotRequest request, Func<bool> isCurrent = null)
        {
            if (characterId <= 0 || accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var definition = _definition.Value;
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
                var before = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(before, definition);
                if (!definition.TryCreateSlotPlan(before, request, out var plan, out var reason))
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarSlotResult(false, before, null, false, reason);
                }
                if (!plan.IsReplay)
                    _repository.UpdateSlot(connection, transaction, characterId, definition.RidableId, plan);
                var after = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(after, definition);
                RequireCurrentGeneration(isCurrent);
                return new GuardianAltarSlotResult(true, after, plan.Updated, !plan.IsReplay);
            });
        }

        public GuardianAltarExchangeResult ExchangeSlots(int characterId, int accountId,
            GuardianAltarExchangeRequest request, Func<bool> isCurrent = null)
        {
            if (characterId <= 0 || accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var definition = _definition.Value;
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
                var before = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(before, definition);
                if (!definition.TryCreateExchangePlan(before, request, out var plan, out var reason))
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarExchangeResult(false, before, null, null, false, reason);
                }
                if (!plan.IsReplay)
                {
                    // Both existing slot CAS writes share this progress transaction.
                    // A failed second endpoint or final identity fence rolls back both.
                    _repository.UpdateSlot(connection, transaction, characterId, definition.RidableId, plan.First);
                    _repository.UpdateSlot(connection, transaction, characterId, definition.RidableId, plan.Second);
                }
                var after = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(after, definition);
                RequireCurrentGeneration(isCurrent);
                return new GuardianAltarExchangeResult(true, after, plan.First.Updated,
                    plan.Second.Updated, !plan.IsReplay);
            });
        }

        public GuardianAltarAchievementResult ClaimAchievement(int characterId, int accountId,
            int achievementType, Func<bool> isCurrent = null)
        {
            if (characterId <= 0 || accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            var definition = _definition.Value;
            var reward = definition.GetAchievementReward(achievementType);
            // Item metadata, stack splitting and complete equipment cores use
            // the existing item owner outside the SQLite write transaction.
            System.Collections.Generic.List<MailboxSendAttachmentRequest> attachments = null;
            var prepared = reward != null && MailboxInventoryOverflowRewardSink.TryBuildAttachmentRequests(
                reward.Items.Select(x => InventoryRewardGrantRequest.Create(x.ItemId, x.Count,
                    ItemCreateReason.MailAttachment)).ToArray(), out attachments)
                && attachments.Count > 0 && attachments.Count <= 10;
            return _database.Write((connection, transaction) =>
            {
                RequireCurrentGeneration(isCurrent);
                _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
                var before = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(before, definition);
                if (reward == null)
                    return new GuardianAltarAchievementResult(false, false, before, "unknown achievement");
                if (before.Achievements.Any(x => x.AchievementId == achievementType && x.Claimed == 1))
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarAchievementResult(true, false, before);
                }
                if (!definition.IsAchievementEligible(before, reward))
                {
                    RequireCurrentGeneration(isCurrent);
                    return new GuardianAltarAchievementResult(false, false, before, "achievement condition is not met");
                }
                if (!prepared || _mailbox == null)
                    throw new InvalidOperationException("Guardian achievement mail authority or attachments are unavailable.");
                RequireCurrentGeneration(isCurrent);
                _repository.MarkAchievementClaimed(connection, transaction, characterId,
                    definition.RidableId, achievementType);
                var sent = _mailbox.SendSystemMails(connection, transaction, new[]
                {
                    new MailboxSendRequest
                    {
                        SenderCharacterId = characterId, SenderAccountId = accountId,
                        SenderName = "DNF管理员", ReceiverCharacterId = characterId, ReceiverAccountId = accountId,
                        Title = reward.PostalTitle, Text = reward.PostalLetter,
                        MailType = 1, SourceProtocol = 0, Unlimited = true,
                        AuditActor = "guardian-altar", AuditReason = $"achievement {achievementType}",
                        IdempotencyKey = $"guardian-achievement-v1:{characterId}:{definition.RidableId}:{achievementType}",
                        Attachments = attachments,
                    }
                });
                // GameDatabase.Write commits normal returns. A delivery failure
                // must throw so claimed state and every mail row roll back.
                if (!sent.Success)
                    throw new InvalidOperationException("Guardian achievement mail failed: " + sent.Error);
                var after = _repository.Read(connection, transaction, characterId, definition.RidableId);
                ValidateComplete(after, definition);
                RequireCurrentGeneration(isCurrent);
                return new GuardianAltarAchievementResult(true, true, after);
            });
        }

        // Call before the inventory lock/SQLite transaction. Recovery may be
        // this owner's first operation and must not perform lazy PVF I/O there.
        internal void PrepareClearDefinition(GuardianAltarClearProgressPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            RequireMatchingClearPlan(plan, _definition.Value);
        }

        private static void RequireMatchingClearPlan(GuardianAltarClearProgressPlan plan,
            GuardianAltarMenuDefinition definition)
        {
            var expected = definition.CreateClearProgressPlan(plan.DungeonId, plan.Difficulty);
            if (plan.RidableId != expected.RidableId || plan.StarsOnImprovement != expected.StarsOnImprovement
                || !plan.NextDungeonIds.SequenceEqual(expected.NextDungeonIds)
                || !plan.UnlockPurchases.SequenceEqual(expected.UnlockPurchases))
                throw new InvalidOperationException("Guardian frozen clear progress plan does not match its resource owner.");
        }

        private static void RequireCurrentGeneration(Func<bool> isCurrent)
        {
            if (isCurrent != null && !isCurrent())
                throw new InvalidOperationException("Guardian progress request belongs to an obsolete generation.");
        }

        internal GuardianAltarClearProgressCommitResult ApplyClearInTransaction(
            SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId,
            GuardianAltarClearProgressPlan plan, Func<bool> isCurrent = null)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(plan);
            RequireCurrentGeneration(isCurrent);
            if (!_definition.IsValueCreated)
                throw new InvalidOperationException("Guardian clear definition must be prepared before the transaction.");
            var definition = _definition.Value;
            RequireMatchingClearPlan(plan, definition);
            _repository.RequireCharacterOwnership(connection, transaction, characterId, accountId);
            var before = _repository.Read(connection, transaction, characterId, plan.RidableId);
            ValidateComplete(before, definition);
            var currentStage = before.Stages.SingleOrDefault(x => x.DungeonId == plan.DungeonId)
                ?? throw new InvalidOperationException("Guardian clear stage is not unlocked for this participant.");
            if (currentStage.Progress > 2 || plan.Difficulty > currentStage.Progress + 1)
                throw new InvalidOperationException("Guardian ordinary clear cannot skip the current difficulty progression.");
            var header = before.Header;
            if ((long)header.FreeTickets + header.SecondaryTickets <= 0)
                throw new InvalidOperationException("Guardian successful clear requires one available ticket.");

            var firstClear = currentStage.Progress == -1;
            var stars = currentStage.Progress < plan.Difficulty ? plan.StarsOnImprovement : 0;
            if (stars > 0)
                _repository.UpdateClearedStage(connection, transaction, characterId, plan.RidableId,
                    plan.DungeonId, currentStage.Progress, plan.Difficulty);
            var purchases = new System.Collections.Generic.List<GuardianAltarPurchase>();
            var unlocked = false;
            if (firstClear)
            {
                foreach (var next in plan.NextDungeonIds)
                    unlocked |= _repository.TryUnlockStage(connection, transaction, characterId, plan.RidableId, next);
                foreach (var purchase in plan.UnlockPurchases)
                    if (_repository.TryUnlockPurchase(connection, transaction, characterId, plan.RidableId, purchase))
                        purchases.Add(purchase);
            }
            // Keep the accepted ordinary NextDungeonIds/outbox plan unchanged.
            // Derive survival eligibility from this transaction's persisted
            // stage facts, including this clear, independently of firstClear.
            unlocked |= TryUnlockEligibleSurvivalStages(connection, transaction,
                _repository.Read(connection, transaction, characterId, plan.RidableId), definition);
            var afterHeader = header with
            {
                FreeTickets = header.FreeTickets > 0 ? header.FreeTickets - 1 : 0,
                SecondaryTickets = header.FreeTickets > 0 ? header.SecondaryTickets : header.SecondaryTickets - 1,
                StageStars = checked(header.StageStars + stars),
                AvailableStars = checked(header.AvailableStars + stars),
                MenuHintFlag = unlocked ? (byte)1 : header.MenuHintFlag,
            };
            _repository.UpdateClearHeader(connection, transaction, characterId, afterHeader);
            var after = _repository.Read(connection, transaction, characterId, plan.RidableId);
            ValidateComplete(after, definition);
            RequireCurrentGeneration(isCurrent);
            return new GuardianAltarClearProgressCommitResult
            {
                StarsGained = stars,
                FirstClear = firstClear,
                NewPurchases = purchases.AsReadOnly(),
                ProgressSnapshot = after,
            };
        }

        private bool TryUnlockEligibleSurvivalStages(SqliteConnection connection, SqliteTransaction transaction,
            GuardianAltarProgressSnapshot progress, GuardianAltarMenuDefinition definition)
        {
            var changed = false;
            foreach (var dungeonId in definition.GetEligibleSurvivalDungeonIds(progress))
                changed |= _repository.TryUnlockStage(connection, transaction,
                    progress.CharacterId, progress.Header.RidableId, dungeonId);
            return changed;
        }

        private static void ValidateComplete(GuardianAltarProgressSnapshot snapshot,
            GuardianAltarMenuDefinition definition)
        {
            definition.ValidateProgress(snapshot);
            if (snapshot.Header.RidableId != definition.RidableId
                || !snapshot.Stages.Any(x => x.DungeonId == definition.FirstDungeonId)
                || definition.InitialPurchases.Any(initial => !snapshot.Purchases.Any(x =>
                    x.ShopType == initial.ShopType && x.ResourceId == initial.ResourceId))
                || definition.InitialSlots.Any(initial => !snapshot.Slots.Any(x =>
                    x.SlotType == initial.SlotType && x.SlotIndex == initial.SlotIndex)))
                throw new InvalidOperationException("Guardian character progress is missing required definition records.");
        }
    }
}
