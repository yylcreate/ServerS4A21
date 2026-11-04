using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        private Func<string, string, bool> _guestFriendLookup;
        private readonly Dictionary<GuestRandomReceiptKey, WeddingGuestInvitationResult> _guestRandomReceipts = new();
        private readonly record struct GuestRandomReceiptKey(Guid SessionId, int CharacterId, long PairId,
            long StartAt, ushort Sequence, uint Checksum);
        private const int MaximumRandomInvitationReceipts = 4096;
        internal void ConfigureGuestFriendLookup(Func<string, string, bool> lookup)
            => _guestFriendLookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

        private void RegisterGuests(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER] = SendGuestLetter;
            group[(ushort)CmdPacketTypeA21.WEDDING_MONEY_GIFT] = GiveWeddingMoney;
        }

        // Canonical CID is authority. The actor UID/channel in C85590 are
        // optional hints and cannot authorize a different recipient.
        private bool CanInviteGuest(EnhancedClientSession actor, int target)
        {
            var host = _characters.GetById(actor.Player.CharacterId);
            var guest = _characters.GetById(target);
            if (host == null || host.Deleted || guest == null || guest.Deleted || host.CharacterId == target) return false;
            if (_sessions.TryGet(target, out var online) && Current(online, target)
                && Channel(actor) > 0 && Channel(actor) == Channel(online)) return true;
            var guilds = new GuildRepository(_database);
            var guild = guilds.GetForMember(host.CharacterId);
            if (guild != null && guilds.GetForMember(target)?.Id == guild.Id) return true;
            return _guestFriendLookup?.Invoke(host.DisplayName, guest.DisplayName) == true;
        }

        private async Task SendGuestLetter(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!WeddingGuestCommandParser.TrySendLetter(body, out var request)) return;
            int cid = session.Player?.CharacterId ?? 0;
            if (!Current(session, cid) || !TownReady(session))
            { await session.SendPacketAsync(WeddingGuestPacketBuilder.SendLetterResult(-1)); return; }
            var state = _repository.Load(cid);
            var guests = new WeddingGuestInvitationRepository(_database);
            WeddingGuestInvitationResult result = null;
            if (state.Status == WeddingStatus.Reserved && state.PartnerId > 0 && state.PartnerId != cid)
            {
                using var first = await _transitions.AcquireAsync(Math.Min(cid, state.PartnerId));
                using var second = await _transitions.AcquireAsync(Math.Max(cid, state.PartnerId));
                if (!Current(session, cid)) return;
                if (TownReady(session) && request.Operation == 0 && CanInviteGuest(session, request.TargetCharacterId))
                    result = guests.Invite(cid, session.Account?.AccountId ?? 0, state.PairId, state.StartAt,
                        new[] { request.TargetCharacterId }, () => Current(session, cid) && TownReady(session));
                else if (TownReady(session) && request.Operation == 1)
                    result = InviteRandomGuests(session, header, state, guests);
                await session.SendPacketAsync(WeddingGuestPacketBuilder.SendLetterResult(result?.Success == true ? 0 : -1,
                    result?.Host?.Remaining ?? 0));
                if (result?.Success == true)
                {
                    await PublishHostGuestIds(session, result.Host);
                    // The pair shares one invited-CID set, while each host
                    // retains its own remaining invitation budget.
                    if (_sessions.TryGet(state.PartnerId, out var partner) && Current(partner, state.PartnerId))
                        await PublishHostGuestIds(partner, result.Host);
                }
            }
            else await session.SendPacketAsync(WeddingGuestPacketBuilder.SendLetterResult(-1));
            // Host gates are released before taking any guest gate. A guest
            // can be another wedding's host; nesting these gates can deadlock.
            if (result?.Success == true)
                foreach (var entry in result.Invitations)
                    if (_sessions.TryGet(entry.GuestCharacterId, out var guest))
                        await _transitions.RunIfCurrentAsync(guest, () => SendGuestInvitation(guest, entry));
        }

        private WeddingGuestInvitationResult InviteRandomGuests(EnhancedClientSession session, GamePacketHeader header,
            WeddingState captured, WeddingGuestInvitationRepository guests)
        {
            int cid = session.Player.CharacterId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var host = guests.LoadForHost(cid, now);
            if (host.State?.Status != WeddingStatus.Reserved || host.PairId != captured.PairId
                || host.StartAt != captured.StartAt || now >= host.StartAt) return new(-3);
            var key = new GuestRandomReceiptKey(session.SessionId, cid, host.PairId, host.StartAt, header.seq, header.checksum);
            lock (_sync)
            {
                foreach (var expired in _guestRandomReceipts.Keys.Where(x => x.StartAt <= now).ToArray())
                    _guestRandomReceipts.Remove(expired);
                if (_guestRandomReceipts.TryGetValue(key, out var old))
                    return old with { Host = host, Added = 0 };
                // Fail closed instead of evicting a live retry identity and
                // selecting different recipients for that same packet.
                if (_guestRandomReceipts.Count >= MaximumRandomInvitationReceipts) return new(-102);
            }
            // SendWeddingCardWindow.xui button 5 is "随机发送"; C856D0
            // emits operation one. Filling the remaining per-host quota from
            // eligible town-ready players is the server's explicit policy.
            var excluded = host.Entries.Select(x => x.GuestCharacterId).Append(host.State.CharacterId)
                .Append(host.State.PartnerId).ToHashSet();
            int channel = Channel(session);
            var candidates = _sessions.GetAllGameSessions()
                .Where(x => Current(x) && TownReady(x) && x.Player.Level >= 17 && channel > 0
                    && Channel(x) == channel && !excluded.Contains(x.Player.CharacterId))
                .Select(x => x.Player.CharacterId).Distinct().OrderBy(x => x).ToArray();
            for (int i = candidates.Length - 1; i > 0; i--)
            { int j = ServerRandom.Next(i + 1); (candidates[i], candidates[j]) = (candidates[j], candidates[i]); }
            int[] targets = candidates.Take(host.Remaining).ToArray();
            var result = targets.Length == 0
                ? new WeddingGuestInvitationResult(0, host, Array.Empty<WeddingGuestInvitation>())
                : guests.Invite(cid, session.Account?.AccountId ?? 0, host.PairId, host.StartAt, targets,
                    () => Current(session, cid) && TownReady(session) && Channel(session) == channel);
            if (result.Success) lock (_sync) _guestRandomReceipts.Add(key, result);
            return result;
        }

        private async Task PublishHostGuestIds(EnhancedClientSession session, WeddingGuestHostList host)
        {
            int cid = session.Player?.CharacterId ?? 0;
            if (!Current(session, cid) || host?.State == null) return;
            var ids = host.Entries.Select(x => x.GuestCharacterId).Distinct().OrderBy(x => x).ToArray();
            if (ids.Length == 0) await session.SendPacketAsync(WeddingGuestPacketBuilder.InvitedGuestIds(ids));
            foreach (var chunk in ids.Chunk(byte.MaxValue))
            {
                if (!Current(session, cid)) return;
                await session.SendPacketAsync(WeddingGuestPacketBuilder.InvitedGuestIds(chunk));
            }
        }

        // Called with this character's transition gate already held. Paid is
        // projected from the financial receipt, never the invitation record.
        private async Task RestoreGuestInvitationsAsync(EnhancedClientSession session)
        {
            int cid = session.Player?.CharacterId ?? 0;
            if (!Current(session, cid)) return;
            var guests = new WeddingGuestInvitationRepository(_database);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await PublishHostGuestIds(session, guests.LoadForHost(cid, now));
            foreach (var entry in guests.LoadForGuest(cid, now))
            {
                if (!Current(session, cid)) return;
                await SendGuestInvitation(session, entry);
            }
        }

        private async Task SendGuestInvitation(EnhancedClientSession guest, WeddingGuestInvitation captured)
        {
            int cid = captured.GuestCharacterId;
            if (!Current(guest, cid)) return;
            var projection = _database.Read(connection =>
            {
                using var transaction = connection.BeginTransaction();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var entry = WeddingGuestInvitationRepository.FindForScope(connection, transaction, cid,
                    guest.Account?.AccountId ?? 0, captured.PairId, captured.StartAt, now);
                bool paid = entry != null && WeddingMoneyGiftCommitService.HasPayment(connection, transaction,
                    captured.PairId, captured.StartAt, cid);
                transaction.Commit(); return (Entry: entry, Paid: paid);
            });
            // C97411 discards letters once their start time has arrived.
            if (projection.Entry?.State.Status != WeddingStatus.Reserved
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= captured.StartAt) return;
            var left = _characters.GetById(projection.Entry.LeftCharacterId);
            var right = _characters.GetById(projection.Entry.RightCharacterId);
            if (left == null || right == null || left.Deleted || right.Deleted) return;
            byte[] packet = WeddingGuestPacketBuilder.Letter(projection.Entry.State, left.Name, right.Name, projection.Paid);
            await guest.TrySendPacketAsync(packet, CancellationToken.None, () => Current(guest, cid)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < captured.StartAt
                && new WeddingGuestInvitationRepository(_database).LoadEvent(captured.PairId, captured.StartAt)?.Status == WeddingStatus.Reserved);
        }

        private async Task GiveWeddingMoney(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!WeddingGuestCommandParser.TryMoneyGift(body, out var request)) return;
            int cid = session.Player?.CharacterId ?? 0;
            async Task Reject() => await session.SendPacketAsync(WeddingGuestPacketBuilder.MoneyGiftResult(-1, request.PairId));
            if (!Current(session, cid) || !TownReady(session)) { await Reject(); return; }
            var guests = new WeddingGuestInvitationRepository(_database);
            var entry = guests.LoadForGuest(cid, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).FirstOrDefault(x => x.PairId == request.PairId);
            if (entry == null) { await Reject(); return; }
            int[] ids = new[] { cid, entry.LeftCharacterId, entry.RightCharacterId }.Distinct().OrderBy(x => x).ToArray();
            if (ids.Length != 3) { await Reject(); return; }
            using var first = await _transitions.AcquireAsync(ids[0]);
            using var second = await _transitions.AcquireAsync(ids[1]);
            using var third = await _transitions.AcquireAsync(ids[2]);
            if (!Current(session, cid)) return;
            if (!TownReady(session) || Channel(session) != entry.State.ChannelId || !Lease(session, out var lease))
            { await Reject(); return; }
            var result = new WeddingMoneyGiftCommitService(WeddingCatalog.Get()).Pay(lease, entry.PairId, entry.StartAt,
                request.Gold, request.Blessing, () => Current(session, cid) && TownReady(session)
                    && Channel(session) == entry.State.ChannelId);
            await session.SendPacketAsync(WeddingGuestPacketBuilder.MoneyGiftResult(result.Error, request.PairId));
            if (result.Success && Current(session, cid))
            {
                await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                if (Current(session, cid)) await SendGuestInvitation(session, entry);
            }
        }
    }
}
