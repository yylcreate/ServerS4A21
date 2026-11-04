using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        private async Task EnterGuestCeremony(EnhancedClientSession session, WeddingEnterRequest request)
        {
            int cid = session.Player?.CharacterId ?? 0;
            async Task Reject() => await session.SendPacketAsync(WeddingPacketBuilder.ItemResult(CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, -6));
            if (!Current(session, cid) || !TownReady(session) || session.Player.Level < 17)
            { await Reject(); return; }
            var guests = new WeddingGuestInvitationRepository(_database);
            var entry = guests.LoadForGuest(cid, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .FirstOrDefault(x => x.PairId == request.PairId && x.State.Room == request.Room);
            if (entry == null) { await Reject(); return; }
            int[] ids = new[] { cid, entry.LeftCharacterId, entry.RightCharacterId }.Distinct().OrderBy(x => x).ToArray();
            if (ids.Length != 3) { await Reject(); return; }
            using var first = await _transitions.AcquireAsync(ids[0]);
            using var second = await _transitions.AcquireAsync(ids[1]);
            using var third = await _transitions.AcquireAsync(ids[2]);
            if (!Current(session, cid) || !TownReady(session)) return;
            entry = guests.FindForChurch(cid, session.Account?.AccountId ?? 0, request.PairId, entry.StartAt,
                Channel(session), checked((byte)request.Room), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                (connection, transaction, invitation) => WeddingMoneyGiftCommitService.HasPayment(connection, transaction,
                    invitation.PairId, invitation.StartAt, invitation.GuestCharacterId));
            if (entry == null) { await Reject(); return; }
            var scene = WeddingSceneCatalog.Get().CeremonyRooms.FirstOrDefault(x => x.Area == entry.State.Room);
            if (scene == null) { await Reject(); return; }
            var landing = WeddingSceneCatalog.Get().GuestLanding(scene);
            byte[] move = new byte[6]; move[0] = scene.Town; move[1] = scene.Area;
            BitConverter.GetBytes(landing.X).CopyTo(move, 2); BitConverter.GetBytes(landing.Y).CopyTo(move, 4);
            await _town.Handle_ENUM_CMDPACKET_SET_USER_AREA(session, default, move);
            if (!Current(session, cid)) return;
            bool entered = session.Player.CurTownId == scene.Town && session.Player.CurAreaId == scene.Area;
            // C924B0 tests the current church scene before closing the guest UI.
            await session.SendPacketAsync(WeddingPacketBuilder.ItemResult(CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, entered ? 0 : -6));
        }
    }
}
