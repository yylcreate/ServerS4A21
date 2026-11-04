using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        private void RegisterCapsule(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.MAKE_COUPLE_GROWTHCAPSULE] = (s, h, b) => Capsule(s, b,
                CmdPacketTypeA21.MAKE_COUPLE_GROWTHCAPSULE, WeddingCapsuleAction.Make);
            group[(ushort)CmdPacketTypeA21.GIVE_COUPLE_GROWTHCAPSULE] = (s, h, b) => Capsule(s, b,
                CmdPacketTypeA21.GIVE_COUPLE_GROWTHCAPSULE, WeddingCapsuleAction.Give);
            group[(ushort)CmdPacketTypeA21.TAKE_COUPLE_GROWTHCAPSULE] = (s, h, b) => Capsule(s, b,
                CmdPacketTypeA21.TAKE_COUPLE_GROWTHCAPSULE, WeddingCapsuleAction.Take);
        }

        private bool CapsuleScene(EnhancedClientSession session, WeddingCapsuleAction action)
        {
            if (!Current(session) || !TownReady(session)) return false;
            if (action != WeddingCapsuleAction.Take) return true;
            var home = WeddingSceneCatalog.Get().CoupleRoom;
            return session.Player.CurTownId == home.Town && session.Player.CurAreaId == home.Area;
        }

        private async Task Capsule(EnhancedClientSession session, byte[] body, CmdPacketTypeA21 command, WeddingCapsuleAction action)
        {
            if (!WeddingCapsuleCommandParser.TryEmpty(body)) return;
            int sceneError = action == WeddingCapsuleAction.Make ? -6 : action == WeddingCapsuleAction.Give ? -7 : -8;
            if (!CapsuleScene(session, action))
            { await session.SendPacketAsync(WeddingCapsulePacketBuilder.Result(command, action, sceneError)); return; }
            var wedding = _repository.Load(session.Player.CharacterId);
            if (wedding.Status != WeddingStatus.Married)
            { await session.SendPacketAsync(WeddingCapsulePacketBuilder.Result(command, action, -1)); return; }
            await WithPair(session, wedding.PartnerId, async (actorLease, _, peer) =>
            {
                if (!CapsuleScene(session, action))
                { await session.SendPacketAsync(WeddingCapsulePacketBuilder.Result(command, action, sceneError)); return; }
                var result = new WeddingCapsuleCommitService(WeddingCapsuleCatalog.Get()).Commit(actorLease,
                    wedding.PartnerId, action, () => CapsuleScene(session, action) && (peer == null || Current(peer, wedding.PartnerId)));
                await session.SendPacketAsync(WeddingCapsulePacketBuilder.Result(command, action, result.Error, result.Actor));
                if (!result.Success) return;
                if (result.Granted > 0 && Current(session)) await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                if (action != WeddingCapsuleAction.Make)
                {
                    await RestoreCapsuleAsync(session);
                    if (peer != null && Current(peer, wedding.PartnerId)) await RestoreCapsuleAsync(peer);
                }
            });
        }

        internal Task RestoreCapsuleAsync(EnhancedClientSession session)
        {
            if (!Current(session)) return Task.CompletedTask;
            var projection = new WeddingCapsuleRepository(_database).LoadProjection(session.Player.CharacterId);
            return session.SendPacketAsync(WeddingCapsulePacketBuilder.State(projection));
        }
    }
}
