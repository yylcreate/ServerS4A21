using DfoServer.Game.Buffs;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // guild_contents owns eligibility/absolute expiry. The client resolves
    // resource buff indices; this projection never writes character stats.
    internal static class GuildStatBuffNotifier
    {
        internal static IReadOnlyList<byte[]> BuildProjection(IReadOnlyList<GuildPurchasedContent> contents)
        {
            var buff = GuildStatBuffRule.Resolve(contents, CurrentUnix());
            if (buff != null && VariousBuffCatalog.Resolve(buff.BuffIndex) == null)
                throw new InvalidOperationException("Guild attribute buff is absent from the current PVF buff list.");
            var stale = GuildStatBuffRule.AllStatBuffIndices().Where(index => index != buff?.BuffIndex).ToArray();
            var packets = new List<byte[]>();
            if (stale.Length != 0)
                packets.Add(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF,
                    SpecialDungeonNotificationBuilder.BuildCharacterRemoveBuff(stale)));
            if (buff != null)
            {
                // Captured guild-attribute state is 1/0/0, unlike potion 0/0/0.
                packets.Add(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF,
                    SpecialDungeonNotificationBuilder.BuildCharacterAddBuff(buff.BuffIndex, 1, 0, 0)));
            }
            return packets;
        }

        // Called while the existing character transition gate is held. Frames
        // are prepared and rechecked at the session's actual send lock.
        internal static async Task ApplyForCharacterAsync(EnhancedClientSession session,
            IGameDatabase database, int characterId, CharacterTransitionCoordinator transitions)
        {
            if (!TryBind(session, characterId, transitions, out var current)) return;
            var repository = new GuildRepository(database);
            GuildRoster prepared = null;
            await SessionDirectory.TrySendBestEffortAsync(async ct =>
            {
                await session.SendPreparedPacketBatchAsync(() =>
                {
                    if (ct.IsCancellationRequested || !current()) return Array.Empty<byte[]>();
                    prepared = repository.GetRosterForMember(characterId);
                    return BuildProjection(prepared?.PurchasedContents);
                }, () => !ct.IsCancellationRequested && current()
                    && SameContents(prepared, repository.GetRosterForMember(characterId))).WaitAsync(ct);
            }, $"guild attribute buff cid={characterId}");
            if (current()) ScheduleForCharacter(session, database, characterId, transitions);
        }

        internal static void ScheduleForCharacter(EnhancedClientSession session, IGameDatabase database,
            int characterId, CharacterTransitionCoordinator transitions)
        {
            if (!TryBind(session, characterId, transitions, out var current)) return;
            var roster = new GuildRepository(database).GetRosterForMember(characterId);
            var buff = GuildStatBuffRule.Resolve(roster?.PurchasedContents, CurrentUnix());
            if (!current()) return;
            var timerName = "guild-stat-buff:" + session.SessionId + ":" + characterId;
            if (buff == null || buff.ExpiresAt >= GuildContentDefinition.PermanentExpiryUnix)
            {
                ClockService.Instance.CancelOneShot(timerName);
                return;
            }
            // Character-owned session effect: a room/run change does not expire
            // its timer. The captured player, UID, account and lease must still
            // be current before scheduling or sending on the character gate.
            var timerSession = new WeakReference<EnhancedClientSession>(session);
            ClockService.Instance.ScheduleOneShotAsync(timerName,
                DateTimeOffset.FromUnixTimeSeconds(buff.ExpiresAt).UtcDateTime, async _ =>
                {
                    if (!current() || !timerSession.TryGetTarget(out var owner)) return;
                    await transitions.RunIfCurrentAsync(owner, async () =>
                    {
                        if (current()) await ApplyForCharacterAsync(owner, database, characterId, transitions);
                    });
                });
        }

        private static uint CurrentUnix() => checked((uint)InventoryItemLifecycleService.UtcNowUnixSeconds());

        internal static void CancelForSession(EnhancedClientSession session)
        {
            if (session != null)
                ClockService.Instance.CancelOneShotsByPrefix("guild-stat-buff:" + session.SessionId + ":");
        }

        internal static void CancelForCharacter(EnhancedClientSession session, int characterId)
        {
            if (session != null && characterId > 0)
                ClockService.Instance.CancelOneShot("guild-stat-buff:" + session.SessionId + ":" + characterId);
        }

        private static bool SameContents(GuildRoster expected, GuildRoster actual)
            => expected == null ? actual == null : actual != null
                && expected.Guild.Id == actual.Guild.Id
                && (expected.PurchasedContents ?? Array.Empty<GuildPurchasedContent>())
                    .SequenceEqual(actual.PurchasedContents ?? Array.Empty<GuildPurchasedContent>());

        private static bool TryBind(EnhancedClientSession session, int id,
            CharacterTransitionCoordinator transitions, out Func<bool> current)
        {
            current = null;
            var player = session?.Player;
            var account = session?.Account;
            var gameSession = session?.GameSession;
            if (player == null || player.CharacterId != id || player.UserId == 0
                || account == null || account.AccountId <= 0 || transitions == null
                || !InventoryContext.TryGetOwnedLease(session.SessionId, id, out var lease)
                || lease.AccountId != account.AccountId) return false;
            int accountId = account.AccountId;
            ushort uid = player.UserId;
            // ClockService lazily discards cancelled queue nodes. Their guard
            // and callback must not retain the departed session or inventory.
            var boundSession = new WeakReference<EnhancedClientSession>(session);
            var boundPlayer = new WeakReference<object>(player);
            var boundAccount = new WeakReference<object>(account);
            var boundGameSession = gameSession == null ? null : new WeakReference<object>(gameSession);
            var boundLease = new WeakReference<InventoryLease>(lease);
            current = () => boundSession.TryGetTarget(out var owner)
                && boundPlayer.TryGetTarget(out var expectedPlayer) && ReferenceEquals(owner.Player, expectedPlayer)
                && boundAccount.TryGetTarget(out var expectedAccount) && ReferenceEquals(owner.Account, expectedAccount)
                && (boundGameSession == null ? owner.GameSession == null
                    : boundGameSession.TryGetTarget(out var expectedGameSession) && ReferenceEquals(owner.GameSession, expectedGameSession))
                && owner.Player.CharacterId == id && owner.Player.UserId == uid && owner.Account.AccountId == accountId
                && boundLease.TryGetTarget(out var expectedLease)
                && InventoryContext.IsCurrentLease(expectedLease, owner.SessionId, id) && transitions.IsCurrent(owner);
            return current();
        }
    }
}
