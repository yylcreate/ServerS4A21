using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        internal void RegisterRoom(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.COUPLE_INVEN_OPEN_CLOSE] = RoomOpen;
            group[(ushort)CmdPacketTypeA21.COUPLE_ROOM_MOVE_ITEM] = RoomMove;
            group[(ushort)CmdPacketTypeA21.COUPLE_INVEN_DELETE_ITEM] = RoomDelete;
            group[(ushort)CmdPacketTypeA21.COUPLE_TREE_PLANT] = TreePlant;
            group[(ushort)CmdPacketTypeA21.COUPLE_TREE_WATER] = TreeWater;
            group[(ushort)CmdPacketTypeA21.COUPLE_TREE_HARVEST] = TreeHarvest;
            group[(ushort)CmdPacketTypeA21.COUPLE_TREE_REMOVE] = TreeRemove;
        }
        private bool OwnRoomReady(EnhancedClientSession session, long pair)
        {
            var home = WeddingSceneCatalog.Get().CoupleRoom;
            return Current(session) && TownReady(session) && session.Player.CurTownId == home.Town && session.Player.CurAreaId == home.Area
                && session.Player.WeddingRoomPairId == pair && pair > 0;
        }
        private Task RoomOpen(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryOpen(body, out byte open)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_INVEN_OPEN_CLOSE, new(WeddingRoomOperation.Query), open)
                : Task.CompletedTask;
        private Task RoomMove(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryMove(body, out var command)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_ROOM_MOVE_ITEM, command) : Task.CompletedTask;
        private Task RoomDelete(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryDelete(body, out var command)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_INVEN_DELETE_ITEM, command) : Task.CompletedTask;
        private Task TreeWater(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryWater(body, out var command)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_TREE_WATER, command) : Task.CompletedTask;
        private Task TreePlant(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryPlant(body, out var command)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_TREE_PLANT, command) : Task.CompletedTask;
        private Task TreeHarvest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryHarvest(body)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_TREE_HARVEST, new(WeddingRoomOperation.Harvest)) : Task.CompletedTask;
        private Task TreeRemove(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => WeddingRoomCommandParser.TryRemove(body)
                ? RoomExecute(session, CmdPacketTypeA21.COUPLE_TREE_REMOVE, new(WeddingRoomOperation.Remove)) : Task.CompletedTask;

        private async Task RoomExecute(EnhancedClientSession session, CmdPacketTypeA21 packet, WeddingRoomCommand command, byte? open = null)
        {
            var state = _repository.Load(session.Player.CharacterId);
            if (state.Status != WeddingStatus.Married || !OwnRoomReady(session, state.PairId))
            { await session.SendPacketAsync(WeddingRoomPacketBuilder.Ack(packet, -2, open)); return; }
            await WithPair(session, state.PartnerId, async (actor, partner, peer) =>
            {
                var result = new WeddingRoomCommitService(WeddingRoomCatalog.Get()).Commit(actor, partner, state.PartnerId, command,
                    () => OwnRoomReady(session, state.PairId) && (peer == null || Current(peer, state.PartnerId)));
                await session.SendPacketAsync(WeddingRoomPacketBuilder.Ack(packet, result.Error, open));
                if (!result.Success) return;
                if (command.Operation == WeddingRoomOperation.Query)
                { if (Current(session)) await session.SendPacketAsync(WeddingRoomPacketBuilder.Cargo(result.Room)); return; }
                if (command.Operation == WeddingRoomOperation.Move && Current(session))
                    await session.SendPacketAsync(WeddingRoomPacketBuilder.Update(result.Room, command));
                await PublishRoomAsync(session, state.PartnerId, result, command.Operation);
            });
        }

        internal async Task RestoreRoomAsync(EnhancedClientSession session)
        {
            int cid = session.Player?.CharacterId ?? 0;
            if (!Current(session, cid)) return;
            var room = new WeddingRoomRepository(_database).Load(cid, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await session.SendPacketAsync(WeddingRoomPacketBuilder.Refresh(room));
            if (Current(session, cid)) await session.SendPacketAsync(WeddingRoomPacketBuilder.State(room));
        }

        // Shared room changes are persisted before notification. A disconnected
        // spouse receives the authoritative projection on its next login.
        internal async Task PublishRoomAsync(EnhancedClientSession actor, int partnerId, WeddingRoomResult result, WeddingRoomOperation operation)
        {
            foreach (int cid in result.MailedCharacters ?? Array.Empty<int>()) await NotifyMail(cid);
            _sessions.TryGet(partnerId, out var partner);
            foreach (var session in new[] { actor, partner })
            {
                if (!Current(session)) continue;
                var state = _repository.Load(session.Player.CharacterId);
                if (state.PairId != result.Room.PairId || state.Status != WeddingStatus.Married) continue;
                if (operation is WeddingRoomOperation.Buy or WeddingRoomOperation.Move or WeddingRoomOperation.Delete)
                {
                    await session.SendPacketAsync(WeddingRoomPacketBuilder.Refresh(result.Room));
                    if (Current(session)) await session.SendPacketAsync(WeddingRoomPacketBuilder.State(result.Room));
                }
                if (operation is WeddingRoomOperation.Plant or WeddingRoomOperation.Water or WeddingRoomOperation.Harvest or WeddingRoomOperation.Remove)
                    await session.SendPacketAsync(WeddingRoomPacketBuilder.Tree(result.Room));
                if (result.AssetsChanged && Current(session) && (session == actor || operation == WeddingRoomOperation.Harvest))
                    await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                if (result.Reward != null && Current(session)) await session.SendPacketAsync(WeddingRoomPacketBuilder.Harvest(result.Reward));
            }
        }
    }
}
