using DfoServer.Game.Wedding;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        private readonly Dictionary<Guid, WeddingPartnerTravelTicket> _partnerTravel = new();
        internal void RegisterPartnerTravel(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.CHECK_MOVE_TO_PARTNER] = CheckPartnerTravel;
            group[(ushort)CmdPacketTypeA21.MOVE_TO_PARTNER] = MoveToPartner;
        }
        private static WeddingPartnerTravelDestination PartnerDestination(EnhancedClientSession peer)
            => new(Channel(peer), peer.Player.CurTownId, peer.Player.CurAreaId, peer.Player.CurPosX, peer.Player.CurPosY);

        private bool CanTravelTo(EnhancedClientSession actor, EnhancedClientSession spouse, WeddingState wedding)
        {
            if (!Current(actor, wedding.CharacterId) || !Current(spouse, wedding.PartnerId) || !TownReady(actor) || !TownReady(spouse)
                || actor.ListenerPort != spouse.ListenerPort || Channel(actor) <= 0 || Channel(actor) != Channel(spouse)) return false;
            if (!GameNetworkConfig.TryResolveGameChannel(actor.ListenerPort, out var channel)
                || channel.ChannelType != GameNetworkConfig.GeneralChannelEnvironment
                || !GameChannelSpawnPolicy.CanEnterTown(actor.ListenerPort, spouse.Player.CurTownId)
                || !WeddingPartnerTravelSceneCatalog.IsPublicOrCoupleRoom(spouse.Player.CurTownId, spouse.Player.CurAreaId)
                || Town.IsCeraRoom(spouse.Player.CurTownId, spouse.Player.CurAreaId)) return false;
            var scenes = WeddingSceneCatalog.Get();
            // C93830 refuses chapel destinations and town 17 before showing confirmation.
            if (spouse.Player.CurTownId == 17 || scenes.ChurchScenes.Any(x => x.Town == spouse.Player.CurTownId && x.Area == spouse.Player.CurAreaId)) return false;
            var home = scenes.CoupleRoom;
            bool targetHome = spouse.Player.CurTownId == home.Town && spouse.Player.CurAreaId == home.Area;
            if (actor.Player.CurTownId == home.Town && !targetHome) return false;
            if (targetHome && spouse.Player.WeddingRoomPairId != wedding.PairId) return false;
            if (Town.TryGetAreaPermission(spouse.Player.CurTownId, spouse.Player.CurAreaId, out var permission))
            {
                var cleared = new QuestRepository(_database.ConnectionString).LoadClearedFlags(actor.Player.CharacterId);
                if (TownAreaPermissionPolicy.GetDenyReason(permission, actor.Player.Level, quest => cleared.ContainsKey(quest)) != null) return false;
            }
            return true;
        }

        private async Task CheckPartnerTravel(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!WeddingPartnerTravelCommandParser.TryCheck(body, out uint pair, out int slot)) return;
            // C93B10 displays the native unavailable message for the zero-channel sentinel.
            if (!Current(s) || !TownReady(s)) { await s.SendPacketAsync(WeddingPartnerTravelPacketBuilder.Check(0)); return; }
            var wedding = _repository.Load(s.Player.CharacterId);
            if (wedding.Status != WeddingStatus.Married || wedding.PairId != pair)
            { await s.SendPacketAsync(WeddingPartnerTravelPacketBuilder.Check(0)); return; }
            await WithPair(s, wedding.PartnerId, async (lease, _, peer) =>
            {
                var current = _repository.Load(lease.CharacterId);
                var partner = _repository.Load(wedding.PartnerId);
                if (current.PairId != pair || current.Status != WeddingStatus.Married || !WeddingRules.IsReciprocal(current, partner)
                    || !CanTravelTo(s, peer, current))
                { await s.SendPacketAsync(WeddingPartnerTravelPacketBuilder.Check(0)); return; }
                int itemId; byte[] core;
                lock (lease.SyncRoot)
                {
                    if (!WeddingPartnerTravelCommitService.TryReadSource(lease.Inventory, slot, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out itemId, out core))
                    { itemId = 0; core = null; }
                }
                if (itemId == 0) { await s.SendPacketAsync(WeddingPartnerTravelPacketBuilder.Check(0)); return; }
                var destination = PartnerDestination(peer);
                lock (_sync) _partnerTravel[s.SessionId] = new(s.SessionId, lease.CharacterId, peer.SessionId, wedding.PartnerId,
                    current.PairId, current.Version, s.ListenerPort, destination, Environment.TickCount64 + WeddingPartnerTravelTicket.LifetimeMilliseconds,
                    slot, itemId, core);
                await s.SendPacketAsync(WeddingPartnerTravelPacketBuilder.Check(0, destination));
            });
        }

        private async Task MoveToPartner(EnhancedClientSession s, GamePacketHeader h, byte[] body)
        {
            if (!WeddingPartnerTravelCommandParser.TryMove(body, out var requested)) return;
            WeddingPartnerTravelTicket ticket;
            lock (_sync)
            {
                if (!_partnerTravel.TryGetValue(s.SessionId, out ticket) || ticket.ActorId != s.Player.CharacterId
                    || ticket.Slot != requested.Slot) return;
            }
            if (!Current(s, ticket.ActorId)) return;
            await WithPair(s, ticket.PartnerId, async (lease, _, peer) =>
            {
                if (peer == null) return;
                var wedding = _repository.Load(ticket.ActorId); var partner = _repository.Load(ticket.PartnerId);
                if (!CanTravelTo(s, peer, wedding) || !ticket.Matches(s.SessionId, ticket.ActorId, peer.SessionId, ticket.PartnerId,
                    wedding, partner, s.ListenerPort, PartnerDestination(peer), requested, Environment.TickCount64)) return;
                lock (_sync)
                {
                    if (!_partnerTravel.TryGetValue(s.SessionId, out var pending) || !ReferenceEquals(pending, ticket)) return;
                    _partnerTravel.Remove(s.SessionId);
                }
                if (!WeddingPartnerTravelCommitService.TryCommit(lease, ticket, s.Player.CurDirection, s.Player.CurAreaState,
                    () => CanTravelTo(s, peer, wedding) && ticket.Matches(s.SessionId, ticket.ActorId, peer.SessionId, ticket.PartnerId,
                        wedding, partner, s.ListenerPort, PartnerDestination(peer), requested, Environment.TickCount64))) return;
                byte[] move = new byte[6]; move[0] = requested.Town; move[1] = requested.Area;
                BitConverter.GetBytes(requested.X).CopyTo(move, 2); BitConverter.GetBytes(requested.Y).CopyTo(move, 4);
                await _town.Handle_ENUM_CMDPACKET_SET_USER_AREA(s, default, move);
                if (Current(s, ticket.ActorId)) await _refresh.SendItemListRefresh(s, InventoryListType.Main);
                // A21 MOVE_TO_PARTNER's registered reader is a return stub. The
                // authoritative SET_USER_AREA projection is the visible result.
            });
        }

        internal void RemovePartnerTravelTicket(Guid sessionId, int expectedCharacterId)
        {
            lock (_sync) if (_partnerTravel.TryGetValue(sessionId, out var ticket) && ticket.ActorId == expectedCharacterId)
                _partnerTravel.Remove(sessionId);
        }
        internal void ClearPartnerTravelTickets()
        { lock (_sync) _partnerTravel.Clear(); }
    }
}
