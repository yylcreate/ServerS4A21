using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.FatigueAcceleration;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Network adapter: resolves the frozen entry roster to session-owned
    // inventory facts. Domain calculation consumes only the resulting snapshot.
    internal sealed class DungeonEntryExperienceBonusPlan
    {
        private readonly Dictionary<int, CapturedParticipant> _participants;

        private DungeonEntryExperienceBonusPlan(
            int partyMemberCount,
            bool partyHasEquippedAvatar,
            Dictionary<int, CapturedParticipant> participants)
        {
            PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount));
            PartyHasEquippedAvatar = partyHasEquippedAvatar;
            _participants = participants
                ?? new Dictionary<int, CapturedParticipant>();
        }

        internal int PartyMemberCount { get; }
        internal bool PartyHasEquippedAvatar { get; }

        internal static DungeonEntryExperienceBonusPlan Capture(
            EnhancedClientSession leader,
            Party party,
            ISessionDirectory sessions,
            int partyMemberCount,
            FatigueAccelerationEntryService fatigueAcceleration = null,
            int dungeonId = 0)
        {
            var participants = new Dictionary<int, CapturedParticipant>();
            var dungeon = fatigueAcceleration == null ? null
                : GameWorld.Dungeon.GetDungeonFile(dungeonId);
            CaptureParticipant(leader, expectedSessionId: null, participants,
                fatigueAcceleration, dungeonId, dungeon);

            if (party != null)
            {
                foreach (var member in party.MembersBySlot())
                {
                    EnhancedClientSession candidate = null;
                    if (leader?.Player?.CharacterId == member.CharacterId)
                    {
                        candidate = leader;
                    }
                    else
                    {
                        sessions?.TryGet(member.CharacterId, out candidate);
                    }

                    CaptureParticipant(
                        candidate,
                        member.SessionId == Guid.Empty
                            ? null
                            : member.SessionId,
                        participants, fatigueAcceleration, dungeonId, dungeon);
                }
            }

            var partyHasEquippedAvatar = false;
            foreach (var participant in participants.Values)
            {
                if (!participant.Facts.HasEquippedAvatar)
                    continue;
                partyHasEquippedAvatar = true;
                break;
            }

            return new DungeonEntryExperienceBonusPlan(
                partyMemberCount,
                partyHasEquippedAvatar,
                participants);
        }

        internal DungeonParticipantExperienceBonusSnapshot ForParticipant(
            EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !_participants.TryGetValue(characterId, out var participant)
                || participant.SessionId != session.SessionId
                || !ReferenceEquals(participant.GameSession, session.GameSession)
                || (participant.FactsCaptured && !InventoryContext.IsCurrentLease(
                    participant.InventoryLease, session.SessionId, characterId)))
            {
                return DungeonParticipantExperienceBonusSnapshot.None;
            }

            var hasEquippedCreature = participant.FactsCaptured
                && participant.Facts.HasEquippedCreature;
            return new DungeonParticipantExperienceBonusSnapshot(
                PartyMemberCount,
                PartyHasEquippedAvatar,
                hasEquippedCreature,
                fatigueBurnExperienceBonusPercent: participant.Effects.ExperienceBonusPercent,
                fatigueConsumptionMultiplier: participant.Effects.FatigueConsumptionMultiplier);
        }

        private static void CaptureParticipant(
            EnhancedClientSession session,
            Guid? expectedSessionId,
            IDictionary<int, CapturedParticipant> participants,
            FatigueAccelerationEntryService fatigueAcceleration,
            int dungeonId,
            PvfLib.DungeonFile dungeon)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || (expectedSessionId.HasValue
                    && session.SessionId != expectedSessionId.Value)
                )
            {
                return;
            }

            var captured = DungeonParticipantExperienceBonusSnapshotCapture
                .TryCaptureOwned(
                    session.SessionId,
                    characterId,
                    out var facts,
                    out var lease);
            participants[characterId] = new CapturedParticipant(
                session.SessionId,
                session.GameSession,
                captured ? lease : null,
                captured ? facts : default,
                captured,
                captured && fatigueAcceleration != null
                    ? fatigueAcceleration.CaptureEffects(
                        session.GameSession?.FatigueAcceleration,
                        characterId, dungeonId, dungeon)
                    : FatigueAccelerationEffectProfile.Inactive);
        }

        private readonly struct CapturedParticipant
        {
            internal CapturedParticipant(
                Guid sessionId,
                GameSession gameSession,
                InventoryLease inventoryLease,
                DungeonParticipantEquipmentBonusFacts facts,
                bool factsCaptured,
                FatigueAccelerationEffectProfile effects)
            {
                SessionId = sessionId;
                GameSession = gameSession;
                InventoryLease = inventoryLease;
                Facts = facts;
                FactsCaptured = factsCaptured;
                Effects = effects;
            }

            internal Guid SessionId { get; }
            internal GameSession GameSession { get; }
            internal InventoryLease InventoryLease { get; }
            internal FatigueAccelerationEffectProfile Effects { get; }
            internal DungeonParticipantEquipmentBonusFacts Facts { get; }
            internal bool FactsCaptured { get; }
        }
    }
}
