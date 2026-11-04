using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.Game.Quests
{
    // Pure classification plus transaction-scoped permission. The quest owner
    // rechecks this before writes; PlayerContext's wedding projection is not an authority.
    internal static class WeddingQuestEligibility
    {
        internal static bool IsWeddingQuest(int questId)
            => GameWorld.QuestData.NormalizeQuestTag(GameWorld.QuestData.GetQuestFile(questId)?.Grade) == "wedding";

        internal static bool IsAllowed(SqliteConnection connection, SqliteTransaction transaction, int characterId, int questId)
            => !IsWeddingQuest(questId) || Load(connection, transaction, characterId);

        internal static bool Load(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var state = WeddingRepository.Load(connection, transaction, characterId);
            if (state.Status != WeddingStatus.Engaged || state.PairId <= 0 || state.PartnerId <= 0) return false;
            var peer = WeddingRepository.Load(connection, transaction, state.PartnerId);
            if (!WeddingRules.IsReciprocal(state, peer)) return false;
            var actor = WeddingRepository.LoadActor(connection, transaction, characterId);
            var partner = WeddingRepository.LoadActor(connection, transaction, state.PartnerId);
            return actor != null && partner != null && !actor.Deleted && !partner.Deleted;
        }

        internal static bool FromInitializationSnapshot(SelectCharacterDataSnapshot snapshot)
        {
            // SqliteSelectCharacterDataSource obtains these three values from
            // one WeddingReplaySnapshot: Partner is populated only after the
            // two wedding states pass reciprocal pair/status/version checks.
            var actor = snapshot?.CharacterRecord;
            var state = snapshot?.Wedding;
            var peer = snapshot?.WeddingPartner;
            return actor != null && !actor.Deleted && state?.Status == WeddingStatus.Engaged
                && state.PairId > 0 && state.CharacterId == actor.CharacterId
                && peer != null && !peer.Deleted && peer.CharacterId == state.PartnerId;
        }

        internal static int CancelActive(SqliteConnection connection, SqliteTransaction transaction, params int[] characterIds)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            int removed = 0;
            foreach (int cid in characterIds.Distinct())
            foreach (var quest in QuestRepository.LoadActiveQuests(connection, transaction, cid))
            {
                if (!IsWeddingQuest(quest.QuestId)) continue;
                if (!QuestRepository.TryDeleteActiveQuestCas(connection, transaction, cid, quest.QuestId,
                    quest.ActivationId, quest.Version, quest.TriggerValue))
                    throw new InvalidOperationException("Wedding quest activation changed during engagement cancellation.");
                QuestNotifySelectionRepository.RemoveQuest(connection, transaction, cid, quest.QuestId);
                // Old event receipts retain their original activation. Deleting
                // the active row retires it; future acceptance gets a new one.
                removed++;
            }
            return removed;
        }
    }
}
