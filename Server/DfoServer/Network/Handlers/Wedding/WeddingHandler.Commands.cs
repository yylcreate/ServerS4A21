using DfoServer.Game.Inventory;
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
        private static bool TownReady(EnhancedClientSession s) => s?.Player != null && s.Player.UserState == 0
            && s.Player.TownPresenceReady && s.Player.CurrentRun == null && !s.Player.DungeonSelectionPending;

        private Task Partner(EnhancedClientSession s, GamePacketHeader header, byte[] body)
        {
            if (!WeddingCommandParser.TryPartner(body, out uint target) || target > ushort.MaxValue) return Task.CompletedTask;
            int expected = s.Player.CharacterId;
            return _transitions.RunIfCurrentAsync(s, async () =>
            {
                if (!Current(s, expected)) return;
                var online = target == 0 ? s : CharacterSelectHandler.FindInspectableOnlineByUserId(_sessions, s, (ushort)target);
                if (online == null || !Current(online))
                { await s.SendPacketAsync(WeddingPacketBuilder.Partner(WeddingState.Empty(expected), null)); return; }
                int cid = online.Player.CharacterId;
                var state = _repository.Load(cid);
                var partner = state.PartnerId > 0 ? _characters.GetById(state.PartnerId) : null;
                if (state.PartnerId > 0 && !WeddingRules.IsReciprocal(state, _repository.Load(state.PartnerId))) partner = null;
                await s.SendPacketAsync(WeddingPacketBuilder.Partner(state, partner));
            });
        }

        private Task Ring(EnhancedClientSession s, GamePacketHeader header, byte[] body)
            => WeddingCommandParser.TryRing(body, out var request)
                ? UseItem(s, CmdPacketTypeA21.WEDDING_UPGRADE_RING, new(8, Slot: request.Slot, Ring: request.Ring))
                : Task.CompletedTask;
        private Task Love(EnhancedClientSession s, GamePacketHeader header, byte[] body)
            => WeddingCommandParser.TryLoveItem(body, out int slot)
                ? UseItem(s, CmdPacketTypeA21.WEDDING_USE_LOVE_POINT_ITEM, new(9, Slot: slot))
                : Task.CompletedTask;
        private async Task UseItem(EnhancedClientSession s, CmdPacketTypeA21 type, WeddingCommitCommand command)
        {
            var state = _repository.Load(s.Player.CharacterId);
            byte[] ResultPacket(int error, WeddingState current) => type == CmdPacketTypeA21.WEDDING_UPGRADE_RING
                ? WeddingPacketBuilder.RingResult(error, current.Ring) : WeddingPacketBuilder.ItemResult(type, error);
            if (!TownReady(s) || state.Status != WeddingStatus.Married)
            { await s.SendPacketAsync(ResultPacket(-2, state)); return; }
            await WithPair(s, state.PartnerId, async (a, b, peer) =>
            {
                var result = new WeddingCommitService(WeddingCatalog.Get()).Commit(a, b, state.PartnerId, command,
                    () => Current(s, a.CharacterId) && TownReady(s) && (peer == null || Current(peer, state.PartnerId)));
                await s.SendPacketAsync(ResultPacket(result.Error, result.State ?? state));
                if (result.Success) await Publish(s, state.PartnerId, command.Operation, result.AssetsChanged);
            });
        }

        private async Task Publish(EnhancedClientSession actor, int partnerId, byte operation, bool assetsChanged)
        {
            _sessions.TryGet(partnerId, out var partner);
            foreach (var s in new[] { actor, partner })
            {
                if (!Current(s)) continue;
                var state = _repository.Load(s.Player.CharacterId);
                s.Player.Wedding = state;
                if (operation is 5 or 6 or 7)
                    await s.SendPacketAsync(WeddingPacketBuilder.Notify(NotiPacketTypeA21.WEDDING_DIVORCE, new[] { operation }));
                // The initiating CMD already runs C92960. Notify only the
                // spouse, so each host receives one native success dialog.
                if (operation == 8)
                {
                    if (!ReferenceEquals(s, actor)) await s.SendPacketAsync(WeddingPacketBuilder.Ring(state.Ring));
                }
                else if (operation == 9)
                {
                    var w = new GamePacketWriter(); w.WriteUInt32(s.Player.UserId); w.WriteUInt32((uint)state.Love);
                    await s.SendPacketAsync(WeddingPacketBuilder.Notify(NotiPacketTypeA21.CHARAC_LOVE_POINT, w.ToArray()));
                }
                else
                {
                    await s.SendPacketAsync(WeddingPacketBuilder.State(state));
                    if (state.Status == WeddingStatus.Reserved)
                    {
                        var a = _characters.GetById(Math.Min(state.CharacterId, state.PartnerId));
                        var b = _characters.GetById(Math.Max(state.CharacterId, state.PartnerId));
                        await s.SendPacketAsync(WeddingPacketBuilder.Reservation(state, a, b,
                            checked((ushort)(new WeddingGuestInvitationRepository(_database)
                                .LoadForHost(s.Player.CharacterId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())?.Remaining ?? 0))));
                    }
                }
                if (Current(s)) await _sessions.BroadcastToAreaAsync(s.Player.CurTownId, s.Player.CurAreaId,
                    s.Player.CharacterId, WeddingPacketBuilder.ActorState(s.Player.UserId, state), s.ListenerPort);
                if (assetsChanged && Current(s)) await _refresh.SendItemListRefresh(s, InventoryListType.Main);
                if (operation == 8 && Current(s)) await InventoryRefreshSender.SendWeddingReplayRefresh(s);
                if (operation is 4 or 5 or 6 or 7 && Current(s)) await RestoreCapsuleAsync(s);
                if (operation is 4 or 5 or 6 or 7 && Current(s))
                {
                    await ReconcileRoomPresence(s, state);
                    if (Current(s)) await RestoreRoomAsync(s);
                }
                if (operation is >= 1 and <= 7 && Current(s) && s.GameSession?.QuestManager is { } quests)
                {
                    if (operation is 5 or 6 or 7) await quests.SendActiveQuestListAsync();
                    if (Current(s))
                        await quests.SendAcceptableQuestListAsync();
                }
                if (state.Status is WeddingStatus.Reserved or WeddingStatus.Ceremony) ScheduleCeremony(state);
            }
        }

        internal async Task RestoreAsync(EnhancedClientSession s)
        {
            int expected = s?.Player?.CharacterId ?? 0;
            if (!Current(s, expected)) return;
            // Settle existing absolute deadlines without holding just one of the
            // pair gates. Advance itself takes the two gates in canonical order.
            for (int step = 0; step < 2 && Current(s, expected); step++)
            {
                var pending = _repository.Load(expected);
                if (pending.Status is not (WeddingStatus.Reserved or WeddingStatus.Ceremony)) break;
                long deadline = pending.StartAt + (pending.Status == WeddingStatus.Ceremony ? WeddingCatalog.Get().CeremonyEndSeconds : 0);
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < deadline) break;
                await Advance(expected, pending.PairId, pending.StartAt);
                if (_repository.Load(expected).Version == pending.Version) break;
            }
            await _transitions.RunIfCurrentAsync(s, async () =>
            {
                if (!Current(s, expected)) return;
                var state = _repository.Load(expected);
                s.Player.Wedding = state;
                await ReconcileRoomPresence(s, state);
                if (!Current(s, expected)) return;
                await s.SendPacketAsync(WeddingPacketBuilder.State(state));
                if (!Current(s, expected)) return;
                if (state.Status == WeddingStatus.Reserved)
                {
                    var a = _characters.GetById(Math.Min(state.CharacterId, state.PartnerId));
                    var b = _characters.GetById(Math.Max(state.CharacterId, state.PartnerId));
                    if (a != null && b != null) await s.SendPacketAsync(WeddingPacketBuilder.Reservation(state, a, b,
                        checked((ushort)(new WeddingGuestInvitationRepository(_database)
                            .LoadForHost(expected, DateTimeOffset.UtcNow.ToUnixTimeSeconds())?.Remaining ?? 0))));
                }
                if (!Current(s, expected)) return;
                await RestoreCapsuleAsync(s);
                if (Current(s, expected)) await RestoreRoomAsync(s);
                if (Current(s, expected)) await RestoreGuestInvitationsAsync(s);
                if (state.Status is WeddingStatus.Reserved or WeddingStatus.Ceremony) ScheduleCeremony(state);
            });
        }

        private async Task ReconcileRoomPresence(EnhancedClientSession session, WeddingState state)
        {
            int expected = state.CharacterId;
            if (!Current(session, expected)) return;
            var home = WeddingSceneCatalog.Get().CoupleRoom;
            bool inHome = session.Player.CurTownId == home.Town && session.Player.CurAreaId == home.Area;
            if (!inHome) { session.Player.WeddingRoomPairId = 0; return; }
            if (state.Status == WeddingStatus.Married && WeddingRules.IsReciprocal(state, _repository.Load(state.PartnerId)))
            { session.Player.WeddingRoomPairId = state.PairId; return; }

            var persisted = _characters.GetById(expected);
            int town = persisted?.TownId ?? 0;
            var scenes = WeddingSceneCatalog.Get();
            if (town <= 0 || town == home.Town) town = scenes.CeremonyRooms[0].Town;
            var spawn = GameChannelSpawnPolicy.Resolve(session.ListenerPort, town);
            byte[] move = new byte[6]; move[0] = spawn.TownId; move[1] = spawn.AreaId;
            BitConverter.GetBytes(spawn.X).CopyTo(move, 2); BitConverter.GetBytes(spawn.Y).CopyTo(move, 4);
            // Keep the former private cohort through the departure broadcast.
            await _town.Handle_ENUM_CMDPACKET_SET_USER_AREA(session, default, move);
            if (Current(session, expected) && (session.Player.CurTownId != home.Town || session.Player.CurAreaId != home.Area))
                session.Player.WeddingRoomPairId = 0;
        }

        private Task EnterCeremony(EnhancedClientSession s, GamePacketHeader header, byte[] body)
        {
            if (!WeddingCommandParser.TryEnter(body, out var request)) return Task.CompletedTask;
            if (request.Reserved == 1) return EnterGuestCeremony(s, request);
            int expected = s.Player.CharacterId;
            return _transitions.RunIfCurrentAsync(s, async () =>
            {
                if (!Current(s, expected)) return;
                var state = _repository.Load(s.Player.CharacterId);
                int error = -6;
                if (TownReady(s) && state.Status == WeddingStatus.Reserved && state.PairId == request.PairId
                    && state.Room == request.Room && state.ChannelId == Channel(s)
                    && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < state.StartAt)
                {
                    var room = WeddingSceneCatalog.Get().CeremonyRooms.FirstOrDefault(x => x.Area == state.Room);
                    if (room != null)
                    {
                        int index = state.CharacterId < state.PartnerId ? 0 : 3;
                        var position = WeddingCatalog.Get().CeremonyPositions;
                        byte[] move = new byte[6]; move[0] = room.Town; move[1] = room.Area;
                        BitConverter.GetBytes((short)position[index]).CopyTo(move, 2);
                        BitConverter.GetBytes((short)position[index + 1]).CopyTo(move, 4);
                        await _town.Handle_ENUM_CMDPACKET_SET_USER_AREA(s, default, move);
                        if (s.Player.CurTownId == room.Town && s.Player.CurAreaId == room.Area) error = 0;
                    }
                }
                await s.SendPacketAsync(WeddingPacketBuilder.ItemResult(CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, error));
            });
        }
    }
}
