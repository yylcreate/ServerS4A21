using System;
using System.Collections.Generic;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Wedding
{
    internal static class WeddingQuestClearEligibility
    {
        internal static WeddingQuestClearEvidence Capture(IGameDatabase database, Guid source,
            bool ordinaryDungeon, int entryMemberCount, IReadOnlyList<int> characterIds)
        {
            var denied = new WeddingQuestClearEvidence(source, 0, 0, 0, false);
            if (!ordinaryDungeon || entryMemberCount != 2 || characterIds?.Count != 2
                || characterIds[0] <= 0 || characterIds[1] <= 0 || characterIds[0] == characterIds[1]) return denied;
            return database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var first = WeddingRepository.Load(connection, transaction, characterIds[0]);
                var second = WeddingRepository.Load(connection, transaction, characterIds[1]);
                var a = WeddingRepository.LoadActor(connection, transaction, characterIds[0]);
                var b = WeddingRepository.LoadActor(connection, transaction, characterIds[1]);
                bool eligible = a != null && b != null && !a.Deleted && !b.Deleted
                    && first.Status == WeddingStatus.Engaged && second.Status == WeddingStatus.Engaged
                    && first.PairId > 0 && WeddingRules.IsReciprocal(first, second);
                transaction.Commit();
                return new WeddingQuestClearEvidence(source, eligible ? first.PairId : 0, characterIds[0], characterIds[1], eligible);
            });
        }
    }
}
