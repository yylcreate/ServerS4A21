using System;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal sealed partial class DungeonPersistentEffectApplicationService
    {
        private GuardianAltarSurvivalCommitPlan RestoreGuardianSurvivalPlan(
            GuardianAltarSurvivalEffectPayload payload, int characterId, int accountId)
        {
            if (payload == null || payload.CharacterId != characterId || payload.AccountId != accountId
                || characterId <= 0 || accountId <= 0 || payload.Rewards == null)
                throw new PermanentPersistentEffectException("Guardian survival participant or reward payload is invalid.");
            if (payload.Rewards.Any(x => x == null || x.ItemId <= 0 || x.Count <= 0
                || StackableItemProvider.Load(x.ItemId) == null))
                throw new PermanentPersistentEffectException("Guardian survival reward is not a registered item.");
            try
            {
                var expected = GetGuardianAltarProgressService().CreateSurvivalCommitPlan(
                    payload.DungeonId, payload.ClearedRounds);
                if (payload.RidableId != expected.RidableId || payload.RoundCount != expected.RoundCount
                    || !payload.Rewards.Select(x => (x.ItemId, x.Count))
                        .SequenceEqual(expected.Rewards.Select(x => (x.ItemId, x.Count))))
                    throw new PermanentPersistentEffectException("Guardian frozen survival plan does not match its resource owner.");
                return expected;
            }
            catch (ArgumentException exception)
            {
                throw new PermanentPersistentEffectException("Guardian survival terminal plan is invalid.", exception);
            }
        }
    }
}
