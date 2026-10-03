using System;
using System.Collections.Generic;
using System.Linq;
using PvfLib;

namespace DfoServer.Game.Events.GuardianAltar
{
    // Frozen survival terminal progress and resources. Ordinary v1 plans are
    // unchanged; the existing effect/Inventory transaction applies both modes.
    internal sealed class GuardianAltarSurvivalCommitPlan
    {
        internal GuardianAltarSurvivalCommitPlan(int ridableId, int dungeonId,
            int roundCount, int clearedRounds, IEnumerable<GuardianAltarClearRewardItem> rewards)
        {
            if (ridableId <= 0 || dungeonId <= 0 || roundCount <= 0 || roundCount > short.MaxValue
                || clearedRounds < 0 || clearedRounds > roundCount)
                throw new ArgumentOutOfRangeException(nameof(clearedRounds));
            var rows = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if ((clearedRounds == 0 && rows.Length != 0) || (clearedRounds > 0 && rows.Length == 0)
                || rows.Any(x => x == null || x.Kind != "normal" || x.ItemId <= 0 || x.Count <= 0))
                throw new ArgumentException("Guardian survival rewards do not match the completed rounds.");
            RidableId = ridableId;
            DungeonId = dungeonId;
            RoundCount = roundCount;
            ClearedRounds = clearedRounds;
            Rewards = Array.AsReadOnly(rows);
        }

        internal int RidableId { get; }
        internal int DungeonId { get; }
        internal int RoundCount { get; }
        internal int ClearedRounds { get; }
        internal IReadOnlyList<GuardianAltarClearRewardItem> Rewards { get; }
    }

    internal sealed class GuardianAltarSurvivalEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int RidableId { get; set; }
        public int DungeonId { get; set; }
        public int RoundCount { get; set; }
        public int ClearedRounds { get; set; }
        public List<GuardianAltarClearEffectItem> Rewards { get; set; } = new();
    }
}
