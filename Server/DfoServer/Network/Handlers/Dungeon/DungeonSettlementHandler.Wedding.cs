using DfoServer.Game.Dungeon;
using DfoServer.Game.Wedding;
using DfoServer.Network.Builders;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class DungeonSettlementHandler
    {
        private async Task<bool> ApplyWeddingClearProgressAsync(EnhancedClientSession session, DungeonRun run,
            DungeonRunIdentity identity, DungeonClearedFact fact)
        {
            if (!session.Player.IsCurrentDungeonRun(identity)) return false;
            int cid = session.Player.CharacterId;
            var repository = new WeddingRepository(_svc.Database);
            if (run.Settlement.Wedding == null)
            {
                var wedding = repository.Load(cid);
                var roster = run.Instance.ParticipantEffects.GetRoster(fact.SourceEventId, DungeonParticipantEffectAudience.Instance);
                run.Settlement.Wedding = new(wedding.Status == WeddingStatus.Married ? wedding.PairId : 0,
                    wedding.PartnerId, roster.Any(x => x.CharacterId == wedding.PartnerId),
                    GameWorld.Dungeon.IsSuitableLevelDungeon(run.DungeonId, session.Player.Level));
            }
            var captured = run.Settlement.Wedding;
            if (captured.PairId <= 0) return true;
            return await ExecuteClearEffectAsync(session, run, identity, fact, DungeonPersistentEffectKinds.WeddingClearProgress, async () =>
            {
                var effect = new DungeonEffectId(fact.SourceEventId, DungeonPersistentEffectKinds.WeddingClearProgress,
                    DungeonEffectScope.Player, run.RunId);
                if (!_svc.PersistentEffects.TryApplyWeddingClear(effect, cid, session.Account?.AccountId ?? 0,
                    captured.PairId, captured.PartnerId, captured.PartnerInFrozenRoster, captured.RecommendedDungeonClear,
                    out var result, out var error)) throw new InvalidOperationException("Wedding clear progress persistence failed: " + error);
                if (!result.Applied || !session.Player.IsCurrentDungeonRun(identity)) return;
                var current = repository.Load(cid);
                if (current.Status != WeddingStatus.Married || current.PairId != captured.PairId) return;
                if (captured.RecommendedDungeonClear)
                {
                    var capsule = new WeddingCapsuleRepository(_svc.Database).LoadProjection(cid);
                    await session.SendPacketAsync(WeddingCapsulePacketBuilder.Progress(capsule.State.Progress));
                }
                if (!captured.PartnerInFrozenRoster) return;
                foreach (int target in new[] { cid, captured.PartnerId })
                {
                    if (_svc.Sessions == null || !_svc.Sessions.TryGet(target, out var peer)) continue;
                    var state = repository.Load(target);
                    if (state.PairId != captured.PairId || state.Status != WeddingStatus.Married) continue;
                    var w = new GamePacketWriter(); w.WriteUInt32(peer.Player.UserId); w.WriteUInt32((uint)state.Love);
                    await peer.TrySendPacketAsync(WeddingPacketBuilder.Notify(NotiPacketTypeA21.CHARAC_LOVE_POINT, w.ToArray()), default,
                        () => peer.Player.CharacterId == target && _svc.Sessions.TryGet(target, out var owner) && ReferenceEquals(owner, peer));
                }
            });
        }
    }
}
