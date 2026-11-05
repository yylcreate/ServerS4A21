using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler : IDisposable
    {
        private readonly ISessionDirectory _sessions;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly IGameDatabase _database;
        private readonly ICharacterRepository _characters;
        private readonly WeddingRepository _repository;
        private readonly InventoryRefreshSender _refresh;
        private readonly TownHandler _town;
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Invitation> _invitations = new();
        private readonly string _timerPrefix = "wedding:" + Guid.NewGuid().ToString("N") + ":";
        private volatile bool _disposed;
        private Func<int, Task> _mailNotifier;

        // Invoked with the pair's character transition gates already held.
        // The notifier sends the existing mailbox projection without reacquiring a gate.
        internal void ConfigureMailNotifier(Func<int, Task> notifier)
            => _mailNotifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

        private sealed class Invitation
        {
            internal Guid Id = Guid.NewGuid();
            internal EnhancedClientSession Proposer, Recipient;
            internal int ProposerId, RecipientId;
            internal ushort ProposerUid, RecipientUid;
            internal WeddingRequest Request;
            internal DateTime Expires;
            internal ClockService.ClockTimerHandle Timer;
        }

        internal WeddingHandler(ISessionDirectory sessions, CharacterTransitionCoordinator transitions,
            IGameDatabase database, ICharacterRepository characters, InventoryRefreshSender refresh, TownHandler town)
        {
            _sessions = sessions; _transitions = transitions; _database = database;
            _characters = characters; _refresh = refresh; _town = town;
            _repository = new WeddingRepository(database);
            _sessions.SessionEnding += Ending;
        }

        internal void Register(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketTypeA21.WEDDING_REQUEST] = Request;
            d[(ushort)CmdPacketTypeA21.WEDDING_RESPONSE] = Response;
            d[(ushort)CmdPacketTypeA21.WEDDING_CHARAC] = Partner;
            d[(ushort)CmdPacketTypeA21.WEDDING_UPGRADE_RING] = Ring;
            d[(ushort)CmdPacketTypeA21.WEDDING_USE_LOVE_POINT_ITEM] = Love;
            d[(ushort)CmdPacketTypeA21.WEDDING_ENTER_CEREMONY] = EnterCeremony;
            RegisterCapsule(d);
            RegisterRoom(d);
            RegisterGuestBook(d);
            RegisterPartnerTravel(d);
            RegisterGuests(d);
        }

        private bool Current(EnhancedClientSession s, int cid) => !_disposed && s?.Player?.CharacterId == cid
            && _sessions.TryGet(cid, out var owner) && ReferenceEquals(owner, s);
        private bool Current(EnhancedClientSession s) => s != null && Current(s, s.Player?.CharacterId ?? 0);
        private bool Eligible(EnhancedClientSession a, EnhancedClientSession b) => Current(a) && Current(b) && a != b
            && a.ListenerPort == b.ListenerPort && a.Player.CurTownId == b.Player.CurTownId && a.Player.CurAreaId == b.Player.CurAreaId
            && a.Player.UserState == 0 && b.Player.UserState == 0 && a.Player.TownPresenceReady && b.Player.TownPresenceReady
            && a.Player.CurrentRun == null && b.Player.CurrentRun == null
            && !a.Player.DungeonSelectionPending && !b.Player.DungeonSelectionPending;
        private static bool Lease(EnhancedClientSession s, out InventoryLease lease)
            => InventoryContext.TryGetOwnedLease(s.SessionId, s.Player.CharacterId, out lease);
        private EnhancedClientSession FindPeer(EnhancedClientSession s, uint uid)
        {
            var found = _sessions.GetAllGameSessions().Where(x => x.Player.UserId == uid && Eligible(s, x)).Take(2).ToArray();
            return found.Length == 1 ? found[0] : null;
        }

        private async Task Request(EnhancedClientSession s, GamePacketHeader header, byte[] body)
        {
            if (!WeddingCommandParser.TryRequest(body, out var request)) return;
            if (!Current(s) || !TownReady(s))
            { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, -101)); return; }
            if (request.Operation is 1 or 2 or 7) { await Propose(s, request); return; }
            if (request.Operation is not (5 or 6))
            { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, -103)); return; }
            var state = _repository.Load(s.Player.CharacterId);
            if (state.Status == WeddingStatus.None)
            { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, -103)); return; }
            await WithPair(s, state.PartnerId, async (a, b, peer) =>
            {
                if (!TownReady(s)) return;
                var result = new WeddingCommitService(WeddingCatalog.Get()).Commit(a, b, state.PartnerId,
                    new(request.Operation), () => Current(s, a.CharacterId)
                        && (peer == null || Current(peer, state.PartnerId)));
                await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, result.Error));
                if (result.Success) { await Publish(s, state.PartnerId, request.Operation, result.AssetsChanged); }
            });
        }

        private async Task Propose(EnhancedClientSession s, WeddingRequest request)
        {
            var target = FindPeer(s, request.Peer);
            if (target == null)
            { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, -101)); return; }
            int proposerId = s.Player.CharacterId, recipientId = target.Player.CharacterId;
            ushort proposerUid = s.Player.UserId, recipientUid = target.Player.UserId;
            await _transitions.RunIfBothCurrentAsync(s, target, async () =>
            {
                if (!Current(s, proposerId) || !Current(target, recipientId)
                    || s.Player.UserId != proposerUid || target.Player.UserId != recipientUid) return;
                if (!Eligible(s, target) || !Lease(s, out _) || !Lease(target, out _))
                { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, -101)); return; }
                var a = _characters.GetById(s.Player.CharacterId); var b = _characters.GetById(target.Player.CharacterId);
                var x = _repository.Load(s.Player.CharacterId); var y = _repository.Load(target.Player.CharacterId);
                int error = request.Operation == 1
                    ? _database.Read(c => (int)WeddingRules.CanEngage(a, b, x, y, WeddingCatalog.Get(),
                        WeddingRepository.LoadCooldown(c, null, s.Player.CharacterId).DivorcedAt,
                        WeddingRepository.LoadCooldown(c, null, target.Player.CharacterId).DivorcedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                    : WeddingRules.IsReciprocal(x, y)
                        && x.Status == (request.Operation == 7 ? WeddingStatus.Married : WeddingStatus.Engaged) ? 0 : -103;
                if (a == null || b == null || a.Deleted || b.Deleted) error = -101;
                Invitation pending = null;
                lock (_sync)
                {
                    if (_invitations.ContainsKey(s.SessionId) || _invitations.ContainsKey(target.SessionId)) error = -102;
                    if (_disposed) error = -101;
                    if (error == 0)
                    {
                        pending = new Invitation { Proposer = s, Recipient = target, ProposerId = a.CharacterId,
                            RecipientId = b.CharacterId, ProposerUid = s.Player.UserId, RecipientUid = target.Player.UserId,
                            Request = request, Expires = DateTime.UtcNow.AddSeconds(6) };
                        _invitations[s.SessionId] = _invitations[target.SessionId] = pending;
                        var captured = pending;
                        pending.Timer = ClockService.Instance.ScheduleOneShotAsync(_timerPrefix + pending.Id,
                            pending.Expires, _ => Expire(captured));
                    }
                }
                await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_REQUEST, request, error));
                await SendCooldownNotice(s, request, error);
                if (pending != null) await target.SendPacketAsync(WeddingPacketBuilder.Invitation(s.Player.UserId, request));
            });
        }

        private static Task SendCooldownNotice(EnhancedClientSession session, WeddingRequest request, int error)
        {
            if (WeddingPacketBuilder.IsRemarryCooldown(request, error))
                return session.SendPacketAsync(WeddingPacketBuilder.RemarryCooldownNotice(WeddingCatalog.Get().RemarryDays));
            if (WeddingPacketBuilder.IsCeremonyCooldown(request, error))
                return session.SendPacketAsync(WeddingPacketBuilder.CeremonyCooldownNotice(WeddingCatalog.Get().WeddingCooldownDays));
            return Task.CompletedTask;
        }

        private async Task Response(EnhancedClientSession s, GamePacketHeader header, byte[] body)
        {
            if (!WeddingCommandParser.TryResponse(body, out var response)) return;
            Invitation pending;
            lock (_sync)
            {
                _invitations.TryGetValue(s.SessionId, out pending);
                if (pending == null || pending.Recipient != s || pending.RecipientId != s.Player.CharacterId
                    || response.Request.Operation != pending.Request.Operation
                    || (response.Request.Peer != pending.ProposerUid && !(response.Request.Peer == 0 && response.Decision != 0)))
                    pending = null;
                if (pending != null) Remove(pending);
            }
            if (pending == null)
            { await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_RESPONSE, response.Request, -101, response.Decision)); return; }
            await _transitions.RunIfBothCurrentAsync(pending.Proposer, pending.Recipient, async () =>
            {
                byte decision = DateTime.UtcNow >= pending.Expires ? (byte)2 : response.Decision;
                int error = 0;
                WeddingCommitResult committed = null;
                bool Valid() => Current(pending.Proposer, pending.ProposerId) && Current(pending.Recipient, pending.RecipientId)
                    && pending.Proposer.Player.UserId == pending.ProposerUid && pending.Recipient.Player.UserId == pending.RecipientUid
                    && Eligible(pending.Proposer, pending.Recipient);
                if (!Valid()) error = -101;
                if (error == 0 && decision == 0)
                {
                    if (!Lease(pending.Proposer, out var a) || !Lease(s, out var b)) error = -101;
                    else
                    {
                        var command = new WeddingCommitCommand(pending.Request.Operation);
                        if (pending.Request.Operation == 2)
                            command = new(2, pending.Request.Ceremony, pending.Request.Delay, FreeRoom(s), Channel(s));
                        // C89E50 confirms that operation 7 has the same native
                        // invitation/acceptance round trip as engagement/reservation.
                        else if (pending.Request.Operation == 7)
                            command = new(7, PartnerPresent: true);
                        committed = new WeddingCommitService(WeddingCatalog.Get()).Commit(a, b, pending.RecipientId, command,
                            Valid);
                        error = committed.Error;
                    }
                }
                // C95D50 calls the native C8A780 stub, which clears an already
                // open congratulations dialog without restoring its text.
                // Publish the committed reservation before success opens it.
                bool publishReservationFirst = committed?.Success == true && pending.Request.Operation == 2;
                if (publishReservationFirst)
                    await Publish(pending.Proposer, pending.RecipientId, pending.Request.Operation, committed.AssetsChanged);
                if (Current(s, pending.RecipientId))
                {
                    await s.SendPacketAsync(WeddingPacketBuilder.Result(CmdPacketTypeA21.WEDDING_RESPONSE,
                        pending.Request with { Peer = pending.ProposerUid }, error, decision));
                    await SendCooldownNotice(s, pending.Request, error);
                }
                if (Current(pending.Proposer, pending.ProposerId))
                {
                    await pending.Proposer.SendPacketAsync(WeddingPacketBuilder.InvitationResult(pending.RecipientUid, pending.Request, error, decision));
                    await SendCooldownNotice(pending.Proposer, pending.Request, error);
                }
                if (committed?.Success == true && !publishReservationFirst)
                    await Publish(pending.Proposer, pending.RecipientId, pending.Request.Operation, committed.AssetsChanged);
            });
        }

        private void Remove(Invitation item)
        {
            foreach (var s in new[] { item.Proposer, item.Recipient })
                if (_invitations.TryGetValue(s.SessionId, out var current) && ReferenceEquals(current, item)) _invitations.Remove(s.SessionId);
            item.Timer?.Cancel();
        }
        private async Task Expire(Invitation item)
        {
            lock (_sync)
            {
                if (!_invitations.TryGetValue(item.Recipient.SessionId, out var current) || !ReferenceEquals(current, item)) return;
                Remove(item);
            }
            foreach (var s in new[] { item.Proposer, item.Recipient })
            {
                int cid = s == item.Proposer ? item.ProposerId : item.RecipientId;
                if (Current(s, cid)) await _transitions.RunIfCurrentAsync(s, () => Current(s, cid)
                    ? s.SendPacketAsync(WeddingPacketBuilder.InvitationResult(
                        s == item.Proposer ? item.RecipientUid : item.ProposerUid, item.Request, 0, 2))
                    : Task.CompletedTask);
            }
        }
        private Task Ending(int cid, EnhancedClientSession s)
        {
            RemovePartnerTravelTicket(s.SessionId, cid);
            Invitation pending;
            lock (_sync)
            {
                _invitations.TryGetValue(s.SessionId, out pending);
                if (pending != null)
                {
                    int boundCharacter = pending.Proposer == s ? pending.ProposerId : pending.RecipientId;
                    if (boundCharacter != cid) pending = null;
                    else Remove(pending);
                }
            }
            if (pending != null)
            {
                var other = pending.Proposer == s ? pending.Recipient : pending.Proposer;
                int expected = pending.Proposer == s ? pending.RecipientId : pending.ProposerId;
                ushort peer = pending.Proposer == s ? pending.ProposerUid : pending.RecipientUid;
                // SessionEnding can run with the departing character gate held. Notify
                // the peer after that transition instead of acquiring its gate here.
                ClockService.Instance.ScheduleOneShotAfterAsync(_timerPrefix + "ending:" + pending.Id, TimeSpan.Zero,
                    _ => !Current(other, expected) ? Task.CompletedTask : _transitions.RunIfCurrentAsync(other,
                        () => Current(other, expected) ? other.SendPacketAsync(WeddingPacketBuilder.InvitationResult(peer, pending.Request, -101, 2)) : Task.CompletedTask));
            }
            return Task.CompletedTask;
        }

        private async Task WithPair(EnhancedClientSession s, int partnerId,
            Func<InventoryLease, InventoryLease, EnhancedClientSession, Task> action)
        {
            int self = s.Player.CharacterId;
            if (self <= 0 || partnerId <= 0 || self == partnerId) return;
            using var first = await _transitions.AcquireAsync(Math.Min(self, partnerId));
            using var second = await _transitions.AcquireAsync(Math.Max(self, partnerId));
            if (!Current(s, self) || !Lease(s, out var a)) return;
            _sessions.TryGet(partnerId, out var peer);
            InventoryLease b = null;
            if (peer != null && (!Current(peer, partnerId) || !Lease(peer, out b))) return;
            await action(a, b, peer);
        }
        private static int Channel(EnhancedClientSession s) => GameNetworkConfig.TryResolveGameChannel(s.ListenerPort, out var c) ? c.ChannelId : 0;
        private byte FreeRoom(EnhancedClientSession s)
        {
            var rooms = WeddingSceneCatalog.Get().CeremonyRooms;
            var used = _repository.LoadReservedRooms(Channel(s));
            var occupied = _sessions.GetAllGameSessions()
                .Where(x => Current(x) && x.Player.TownPresenceReady && Channel(x) == Channel(s))
                .Select(x => (x.Player.CurTownId, x.Player.CurAreaId)).ToHashSet();
            return rooms.FirstOrDefault(x => !used.Contains(x.Area) && !occupied.Contains((x.Town, x.Area)))?.Area ?? 0;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                ClearPartnerTravelTickets();
                _recovery?.Cancel();
                foreach (var timer in _ceremonies.Values) timer.Timer?.Cancel();
                _ceremonies.Clear();
                foreach (var item in _invitations.Values.Distinct()) item.Timer?.Cancel();
                _invitations.Clear();
            }
            _sessions.SessionEnding -= Ending;
        }
    }
}
