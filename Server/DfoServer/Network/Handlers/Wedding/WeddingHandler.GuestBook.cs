using DfoServer.Game.Wedding;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        internal void RegisterGuestBook(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.UPDATE_GUEST_COMMENT] = GuestUpdate;
            group[(ushort)CmdPacketTypeA21.DELETE_GUEST_COMMENT] = GuestDelete;
            group[(ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT] = GuestLoad;
        }
        private Task GuestUpdate(EnhancedClientSession s, GamePacketHeader h, byte[] body)
            => WeddingGuestBookCommandParser.TryUpdate(body, out var pair, out var text)
                ? GuestExecute(s, pair, text, null, CmdPacketTypeA21.UPDATE_GUEST_COMMENT) : Task.CompletedTask;
        private Task GuestDelete(EnhancedClientSession s, GamePacketHeader h, byte[] body)
            => WeddingGuestBookCommandParser.TryDelete(body, out var pair, out var id, out var home)
                ? GuestExecute(s, pair, null, id, CmdPacketTypeA21.DELETE_GUEST_COMMENT, home) : Task.CompletedTask;
        private Task GuestLoad(EnhancedClientSession s, GamePacketHeader h, byte[] body)
            => WeddingGuestBookCommandParser.TryLoad(body, out var pair, out var home)
                ? GuestExecute(s, pair, null, null, CmdPacketTypeA21.LOAD_GUEST_COMMENT, home) : Task.CompletedTask;

        private async Task GuestExecute(EnhancedClientSession s, uint pair, byte[] text, uint? deleted, CmdPacketTypeA21 type, bool? home = null)
        {
            async Task Reply(WeddingGuestBookResult result)
            {
                if (type == CmdPacketTypeA21.UPDATE_GUEST_COMMENT) await s.SendPacketAsync(WeddingGuestBookPacketBuilder.Update(result.Error));
                else if (type == CmdPacketTypeA21.DELETE_GUEST_COMMENT) await s.SendPacketAsync(WeddingGuestBookPacketBuilder.Delete(result.Error, deleted ?? 0));
                else await s.SendPacketAsync(WeddingGuestBookPacketBuilder.Load(result));
            }
            if (!Current(s)) { await Reply(new(-3)); return; }
            if (home == false || home == null && !OwnRoomReady(s, pair))
            { await GuestExecuteCeremony(s, pair, text, deleted, Reply); return; }
            if (!OwnRoomReady(s, pair)) { await Reply(new(-3)); return; }
            var wedding = _repository.Load(s.Player.CharacterId);
            if (wedding.Status != WeddingStatus.Married || wedding.PairId != pair) { await Reply(new(-3)); return; }
            await WithPair(s, wedding.PartnerId, async (lease, _, peer) =>
            {
                var result = new WeddingGuestBookRepository(_database).Execute(lease.CharacterId, lease.AccountId, pair, text, deleted,
                    () => Current(s, lease.CharacterId) && OwnRoomReady(s, pair));
                await Reply(result);
            });
        }

        private async Task GuestExecuteCeremony(EnhancedClientSession s, uint pair, byte[] text, uint? deleted,
            Func<WeddingGuestBookResult, Task> reply)
        {
            int cid = s.Player.CharacterId;
            var guests = new WeddingGuestInvitationRepository(_database);
            var own = _repository.Load(cid);
            WeddingState state = own.PairId == pair ? guests.LoadEvent(pair, own.StartAt) : null;
            if (state == null)
                state = guests.FindForArea(cid, s.Account?.AccountId ?? 0, Channel(s), s.Player.CurAreaId,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true,
                    (connection, transaction, invitation) => invitation.PairId == pair
                        && WeddingMoneyGiftCommitService.HasPayment(connection, transaction,
                            invitation.PairId, invitation.StartAt, cid))?.State;
            if (state == null || state.PairId != pair) { await reply(new(-3)); return; }
            bool InScene() => Current(s, cid) && TownReady(s) && Channel(s) == state.ChannelId
                && s.Player.CurTownId == WeddingSceneCatalog.NativeChurchTown && s.Player.CurAreaId == state.Room;
            if (!InScene()) { await reply(new(-3)); return; }
            var gates = new List<IDisposable>();
            try
            {
                foreach (int id in new[] { cid, state.CharacterId, state.PartnerId }.Distinct().OrderBy(x => x))
                    gates.Add(await _transitions.AcquireAsync(id));
                if (!InScene()) return;
                var result = new WeddingGuestBookRepository(_database).ExecuteCeremony(cid, s.Account?.AccountId ?? 0,
                    state, text, deleted, InScene);
                if (Current(s, cid)) await reply(result);
            }
            finally { for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); }
        }
    }
}
