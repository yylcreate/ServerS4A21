using System;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Game.Wedding;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class DungeonSettlementHandler
    {
        internal void FreezeWeddingQuestClear(DungeonRun run, DungeonClearedFact fact)
        {
            run.Instance.Mechanisms.WeddingQuest.Freeze(fact.SourceEventId, () =>
            {
                var roster = run.Instance.ParticipantEffects.GetRoster(fact.SourceEventId, DungeonParticipantEffectAudience.Instance);
                bool current = roster.Count == 2 && roster.All(x => x.Run != null
                    && x.Run.Matches(x.RunIdentity) && ReferenceEquals(x.Run.Instance, run.Instance)
                    && _svc.Sessions != null && _svc.Sessions.TryGet(x.CharacterId, out var session)
                    && session.Player?.CharacterId == x.CharacterId && session.Player.UserId == x.ParticipantUserId
                    && ReferenceEquals(session.Player.CurrentRun, x.Run));
                var cohort = run.EntryPartySelectionCohort;
                if (cohort != null)
                    // Entry identities prove the initial two-person cohort.
                    // Its session ticket is only for selection: a legitimate
                    // reattachment is validated above by the current run owner.
                    current &= cohort.Participants.Count == 2 && cohort.Participants.All(x => roster.Any(p => p.CharacterId == x.CharacterId));
                return WeddingQuestClearEligibility.Capture(_svc.Database, fact.SourceEventId,
                    current && IsOrdinaryWeddingQuestDungeon(run), run.EntryPartyMemberCount,
                    roster.Select(x => x.CharacterId).ToArray());
            });
        }

        internal static bool IsOrdinaryWeddingQuestDungeon(DungeonRun run)
        {
            if (run?.Instance == null || run.RewardPolicy.Kind != DungeonRewardPolicyKind.Standard
                || run.Tower != null || run.Selection.IsA21TutorialEntry || run.Selection.GuildDungeon != null
                || run.Instance.Mechanisms.Tournament != null || run.Instance.Mechanisms.BloodAltar != null) return false;
            var definition = GameWorld.Dungeon.GetDungeonFile(run.DungeonId);
            if (definition == null || definition.NoFatigue) return false;
            var type = GameWorld.QuestData.NormalizeQuestTag(definition.DungeonType);
            return type.Length == 0 || type == "normal";
        }
    }
}
