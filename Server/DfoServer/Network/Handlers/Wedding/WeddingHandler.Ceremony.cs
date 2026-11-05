using DfoServer.Game.Inventory;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        private sealed class CeremonyTimer
        {
            internal long Start;
            internal long Due;
            internal ClockService.ClockTimerHandle Timer;
        }
        private readonly Dictionary<long, CeremonyTimer> _ceremonies = new();
        private ClockService.ClockTimerHandle _recovery;

        internal void StartRecovery()
        {
            lock (_sync)
            {
                if (_disposed || _recovery != null) return;
                _recovery = ClockService.Instance.ScheduleOneShotAfterAsync(_timerPrefix + "recovery", TimeSpan.FromSeconds(1), _ => Recover());
            }
        }
        private void ScheduleCeremony(WeddingState state)
        {
            long due = state.StartAt + (state.Status == WeddingStatus.Ceremony ? WeddingCatalog.Get().CeremonyEndSeconds : 0);
            lock (_sync)
            {
                if (_disposed) return;
                if (_ceremonies.TryGetValue(state.PairId, out var scheduled) && scheduled.Start == state.StartAt && scheduled.Due == due) return;
                if (_ceremonies.Remove(state.PairId, out var old)) old.Timer?.Cancel();
                var registration = new CeremonyTimer { Start = state.StartAt, Due = due };
                _ceremonies[state.PairId] = registration;
                registration.Timer = ClockService.Instance.ScheduleOneShotAsync(_timerPrefix + "ceremony:" + state.PairId,
                    DateTimeOffset.FromUnixTimeSeconds(due).UtcDateTime, async _ =>
                    {
                        lock (_sync)
                        {
                            if (_disposed || !_ceremonies.TryGetValue(state.PairId, out var current)
                                || !ReferenceEquals(current, registration)) return;
                            _ceremonies.Remove(state.PairId);
                        }
                        await Advance(state.CharacterId, state.PairId, state.StartAt);
                    });
            }
        }
        private void CancelCeremony(long pairId, long start)
        {
            lock (_sync)
                if (_ceremonies.TryGetValue(pairId, out var current) && current.Start == start)
                {
                    _ceremonies.Remove(pairId);
                    current.Timer?.Cancel();
                }
        }
        private async Task Recover()
        {
            try
            {
                var due = _repository.LoadDueCeremonies(DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    WeddingCatalog.Get().CeremonyEndSeconds, 32);
                foreach (var row in due) if (!_disposed) await Advance(row.CharacterId, row.PairId, row.StartAt);
            }
            catch (Exception ex) { FileLogger.Log("[Wedding] recovery deferred: " + ex); }
            lock (_sync)
                if (!_disposed) _recovery = ClockService.Instance.ScheduleOneShotAfterAsync(_timerPrefix + "recovery", TimeSpan.FromMinutes(1), _ => Recover());
        }
        private async Task Advance(int characterId, long pairId, long start)
        {
            if (_disposed) return;
            var before = _repository.Load(characterId);
            if (before.PairId != pairId || before.StartAt != start || before.Status is not (WeddingStatus.Reserved or WeddingStatus.Ceremony))
            { CancelCeremony(pairId, start); return; }
            int partnerId = before.PartnerId;
            using var first = await _transitions.AcquireAsync(Math.Min(characterId, before.PartnerId));
            using var second = await _transitions.AcquireAsync(Math.Max(characterId, before.PartnerId));
            try
            {
                before = _repository.Load(characterId);
                if (_disposed || before.PairId != pairId || before.StartAt != start || before.PartnerId != partnerId
                    || before.Status is not (WeddingStatus.Reserved or WeddingStatus.Ceremony))
                { CancelCeremony(pairId, start); return; }
                _sessions.TryGet(characterId, out var a); _sessions.TryGet(before.PartnerId, out var b);
                WeddingState after;
                if (before.Status == WeddingStatus.Reserved && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= before.StartAt
                    && !CeremonyHostsPresent(before, a, b))
                {
                    var cancelled = new WeddingCeremonyCancellationService(_database)
                        .Cancel(characterId, pairId, start, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (cancelled.State.Status == WeddingStatus.Engaged)
                    {
                        if (Current(a, characterId)) await Publish(a, partnerId, 2, false);
                        else if (Current(b, partnerId)) await Publish(b, characterId, 2, false);
                        await CeremonyNotice(before, true);
                        await Task.WhenAll(cancelled.MailedCharacters.Distinct().Select(NotifyMail));
                    }
                    CancelCeremony(pairId, start);
                    return;
                }
                if (Current(a, characterId) && Current(b, partnerId) && Lease(a, out var la) && Lease(b, out var lb))
                {
                    byte operation = before.Status == WeddingStatus.Reserved ? (byte)3 : (byte)4;
                    var result = new WeddingCommitService(WeddingCatalog.Get()).Commit(la, lb, before.PartnerId,
                        new(operation), () => Current(a, characterId) && Current(b, before.PartnerId));
                    if (!result.Success) return; // durable pending state is retried by recovery
                    after = result.State;
                    await Publish(a, before.PartnerId, operation, result.AssetsChanged);
                    if (result.GiftsMailed)
                    { await NotifyMail(characterId); await NotifyMail(partnerId); }
                    await Task.WhenAll((result.MailedCharacters ?? Array.Empty<int>()).Distinct().Select(NotifyMail));
                }
                else
                {
                    after = new WeddingCeremonyRecoveryService(_database).Advance(characterId, pairId, start, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (Current(a, characterId)) await Publish(a, partnerId, (byte)after.Status, false);
                    else if (Current(b, partnerId)) await Publish(b, characterId, (byte)after.Status, false);
                    if (after.Status == WeddingStatus.Married)
                    { await NotifyMail(characterId); await NotifyMail(partnerId); }
                }
                if (after.Status == WeddingStatus.Ceremony && before.Status == WeddingStatus.Reserved)
                    await CeremonyNotice(before, false);
                if (after.Status == WeddingStatus.Married && before.Status != WeddingStatus.Married)
                    await CeremonyNotice(before, true);
                if (after.Status is WeddingStatus.Reserved or WeddingStatus.Ceremony) ScheduleCeremony(after);
                else CancelCeremony(pairId, start);
            }
            catch (Exception ex) { FileLogger.Log($"[Wedding] ceremony {pairId} deferred: {ex}"); }
        }
        private bool CeremonyHostsPresent(WeddingState state, EnhancedClientSession first, EnhancedClientSession second)
        {
            var scene = WeddingSceneCatalog.Get().CeremonyRooms.FirstOrDefault(x => x.Area == state.Room);
            bool Present(EnhancedClientSession session, int cid) => Current(session, cid) && TownReady(session)
                && scene != null && Channel(session) == state.ChannelId
                && session.Player.CurTownId == scene.Town && session.Player.CurAreaId == scene.Area
                && WeddingSceneAdmission.IsCeremony(session.Player, state.PairId, state.StartAt);
            return Present(first, state.CharacterId) && Present(second, state.PartnerId);
        }
        private async Task CeremonyNotice(WeddingState state, bool finished)
        {
            var a = _characters.GetById(Math.Min(state.CharacterId, state.PartnerId));
            var b = _characters.GetById(Math.Max(state.CharacterId, state.PartnerId));
            if (a == null || b == null) return;
            var scene = WeddingSceneCatalog.Get().CeremonyRooms.FirstOrDefault(x => x.Area == state.Room);
            if (scene == null) return;
            var audience = _sessions.GetAllGameSessions().Where(s => s.Player.CurTownId == scene.Town
                && s.Player.CurAreaId == scene.Area && Channel(s) == state.ChannelId
                && WeddingSceneAdmission.IsCeremony(s.Player, state.PairId, state.StartAt)).ToArray();
            foreach (var s in audience)
            {
                if (!Current(s)) continue;
                var w = new GamePacketWriter();
                if (finished) { w.WriteByte(1); w.WriteByte(state.Ceremony); }
                else { w.WriteUInt32((uint)state.PairId); w.WriteUInt32(state.Room); }
                WeddingPacketBuilder.WriteName(w, a.Name); WeddingPacketBuilder.WriteName(w, b.Name);
                // C9825C reads 0x43 bytes. The reference's trailing channel u32
                // does not exist in A21's end notice.
                if (finished) { w.WriteUInt32(state.Room); w.WriteByte(state.Ceremony); }
                int cid = s.Player.CharacterId;
                await s.TrySendPacketAsync(WeddingPacketBuilder.Notify(finished ? NotiPacketTypeA21.WEDDING_AVATAR : NotiPacketTypeA21.WEDDING_CEREMONY_BEGIN,
                    w.ToArray()), System.Threading.CancellationToken.None, () => Current(s, cid)
                        && s.Player.CurTownId == scene.Town && s.Player.CurAreaId == scene.Area && Channel(s) == state.ChannelId
                        && WeddingSceneAdmission.IsCeremony(s.Player, state.PairId, state.StartAt));
            }
        }
        private async Task NotifyMail(int characterId)
        {
            if (_mailNotifier == null || !_sessions.TryGet(characterId, out var session) || !Current(session, characterId)) return;
            try { await _mailNotifier(characterId); }
            catch (Exception ex) { FileLogger.Log($"[Wedding] mail notification deferred cid={characterId}: {ex}"); }
        }
    }
}
