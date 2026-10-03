using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed class GuardianAltarClearProgressPlan
    {
        internal GuardianAltarClearProgressPlan(int ridableId, int dungeonId, byte difficulty,
            int starsOnImprovement, IEnumerable<int> nextDungeonIds,
            IEnumerable<GuardianAltarPurchase> unlockPurchases)
        {
            if (ridableId <= 0 || dungeonId <= 0 || difficulty > 2 || starsOnImprovement <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            var next = (nextDungeonIds ?? throw new ArgumentNullException(nameof(nextDungeonIds))).ToArray();
            var purchases = (unlockPurchases ?? throw new ArgumentNullException(nameof(unlockPurchases))).ToArray();
            if (next.Any(x => x <= 0 || x == dungeonId) || next.Distinct().Count() != next.Length
                || purchases.Any(x => x == null || x.ShopType < 0 || x.ShopType > 2
                    || x.ResourceId <= 0 || x.Level < 0 || x.Level > 1 || x.Price < 0)
                || purchases.Select(x => (x.ShopType, x.ResourceId)).Distinct().Count() != purchases.Length)
                throw new ArgumentException("Guardian ordinary clear progress plan is inconsistent.");
            RidableId = ridableId;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            StarsOnImprovement = starsOnImprovement;
            NextDungeonIds = Array.AsReadOnly(next);
            UnlockPurchases = Array.AsReadOnly(purchases);
        }

        internal int RidableId { get; }
        internal int DungeonId { get; }
        internal byte Difficulty { get; }
        internal int StarsOnImprovement { get; }
        internal IReadOnlyList<int> NextDungeonIds { get; }
        internal IReadOnlyList<GuardianAltarPurchase> UnlockPurchases { get; }
    }

    internal sealed class GuardianAltarClearCommitPlan
    {
        internal GuardianAltarClearCommitPlan(GuardianAltarClearProgressPlan progressPlan,
            IEnumerable<GuardianAltarClearRewardItem> rewards)
        {
            ProgressPlan = progressPlan ?? throw new ArgumentNullException(nameof(progressPlan));
            var rows = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (rows.Length == 0 || rows.Any(x => x == null || x.Kind != "normal" || x.ItemId <= 0 || x.Count <= 0))
                throw new ArgumentException("Guardian ordinary clear requires its frozen normal item rewards.");
            Rewards = Array.AsReadOnly(rows);
        }

        internal GuardianAltarClearProgressPlan ProgressPlan { get; }
        internal IReadOnlyList<GuardianAltarClearRewardItem> Rewards { get; }
    }

    internal sealed class GuardianAltarClearProgressCommitResult
    {
        internal int ClearedRounds { get; init; }
        internal int BestRound { get; init; }
        internal bool Improved { get; init; }
        internal int StarsGained { get; init; }
        internal bool FirstClear { get; init; }
        internal GuardianAltarProgressSnapshot ProgressSnapshot { get; init; }
        internal IReadOnlyList<GuardianAltarPurchase> NewPurchases { get; init; }
            = Array.Empty<GuardianAltarPurchase>();
    }

    internal sealed class GuardianAltarClearCommitResult
    {
        internal int ClearedRounds { get; init; }
        internal int BestRound { get; init; }
        internal bool Improved { get; init; }
        internal int StarsGained { get; init; }
        internal bool FirstClear { get; init; }
        internal GuardianAltarProgressSnapshot ProgressSnapshot { get; init; }
        internal IReadOnlyList<GuardianAltarPurchase> NewPurchases { get; init; }
            = Array.Empty<GuardianAltarPurchase>();
        internal IReadOnlyList<InventorySlotMutation> Changes { get; init; }
            = Array.Empty<InventorySlotMutation>();
        internal int MailedRewardCount { get; init; }
    }

    // Versioned outbox values, independent of socket/runtime state. Public
    // properties are required by the existing persistent effect serializer.
    internal sealed class GuardianAltarClearEffectProgressPlan
    {
        public int RidableId { get; set; }
        public int DungeonId { get; set; }
        public byte Difficulty { get; set; }
        public int StarsOnImprovement { get; set; }
        public List<int> NextDungeonIds { get; set; } = new();
        public List<GuardianAltarPurchase> UnlockPurchases { get; set; } = new();
    }

    internal sealed class GuardianAltarClearEffectItem
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    internal sealed class GuardianAltarClearEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public GuardianAltarClearEffectProgressPlan ProgressPlan { get; set; }
        public List<GuardianAltarClearEffectItem> Rewards { get; set; } = new();
    }

    internal sealed class GuardianAltarClearEffectSnapshot
    {
        public GuardianAltarHeader Header { get; set; }
        public List<GuardianAltarStage> Stages { get; set; } = new();
        public List<GuardianAltarPurchase> Purchases { get; set; } = new();
        public List<GuardianAltarSlot> Slots { get; set; } = new();
        public List<GuardianAltarAchievement> Achievements { get; set; } = new();
    }

    internal sealed class GuardianAltarClearEffectMutation
    {
        public int ListType { get; set; }
        public short Slot { get; set; }
    }

    internal sealed class GuardianAltarClearEffectResult
    {
        public int ClearedRounds { get; set; }
        public int BestRound { get; set; }
        public bool Improved { get; set; }
        public int CharacterId { get; set; }
        public int StarsGained { get; set; }
        public bool FirstClear { get; set; }
        public GuardianAltarClearEffectSnapshot Progress { get; set; }
        public List<GuardianAltarPurchase> NewPurchases { get; set; } = new();
        public List<GuardianAltarClearEffectMutation> Changes { get; set; } = new();
        public int MailedRewardCount { get; set; }
    }
}
