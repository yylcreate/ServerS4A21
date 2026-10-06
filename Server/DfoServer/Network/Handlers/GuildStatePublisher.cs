using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;

namespace DfoServer.Network.Handlers
{
    // Read durable state again under each current character gate. Call only AFTER
    // releasing mutation gates; queued refreshes never replay an old guild snapshot.
    internal sealed class GuildStatePublisher
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;
        private readonly InventoryRefreshSender _appearance;
        internal GuildStatePublisher(GuildRepository repository, CharacterTransitionCoordinator transitions,
            ISessionDirectory sessions, InventoryRefreshSender appearance)
        { _repository = repository; _transitions = transitions; _sessions = sessions; _appearance = appearance; }

        internal async Task RefreshAsync(IEnumerable<int> characters, bool disbanded = false)
        {
            foreach (int id in characters.Distinct().OrderBy(x => x))
            {
                if (!_sessions.TryGet(id, out var session)) continue;
                var player = session.Player;
                var account = session.Account;
                var gameSession = session.GameSession;
                if (player == null || player.CharacterId != id || player.UserId == 0
                    || account == null || account.AccountId <= 0
                    || !InventoryContext.TryGetOwnedLease(session.SessionId, id, out var lease)
                    || lease.AccountId != account.AccountId) continue;
                int accountId = account.AccountId;
                ushort userId = player.UserId;
                long runGeneration = player.CurrentDungeonRunGeneration;
                long selectionGeneration = player.CurrentDungeonSelectionGeneration;
                bool SourceCurrent() => ReferenceEquals(session.Player, player)
                    && ReferenceEquals(session.Account, account) && ReferenceEquals(session.GameSession, gameSession)
                    && player.CharacterId == id && player.UserId == userId && account.AccountId == accountId
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && InventoryContext.IsCurrentLease(lease, session.SessionId, id)
                    && _transitions.IsCurrent(session);
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    if (!SourceCurrent()) return;
                    await SessionDirectory.TrySendBestEffortAsync(async ct =>
                    {
                        bool identityChanged = false, clearOldGuild = false;
                        const int maximumAttempts = 3;
                        for (int attempt = 0; attempt < maximumAttempts; attempt++)
                        {
                            GuildRoster published = null;
                            GuildPendingApplication pending = null;
                            Dictionary<int, byte?> onlineChannels = null;
                            byte[] appearancePacket = null;
                            int guildId = 0;
                            bool prepared = false, snapshotChanged = false;
                            bool CanWrite()
                            {
                                if (ct.IsCancellationRequested || !SourceCurrent()) return false;
                                var current = _repository.GetRosterForMember(id);
                                var currentPending = current == null ? _repository.GetPendingApplication(id) : null;
                                // OfflineSeconds comes from the current query time. Compare
                                // every durable member field and the captured online channel
                                // separately so a clock tick cannot invalidate this batch.
                                if (!SameRoster(published, current)
                                    || currentPending != pending
                                    || onlineChannels.Any(row => FindOnlineChannel(row.Key) != row.Value))
                                {
                                    snapshotChanged = true;
                                    return false;
                                }
                                if (!SourceCurrent()) return false;
                                if (player.Subtype0Tail?.GuildId != (uint)guildId)
                                {
                                    snapshotChanged = true;
                                    return false;
                                }
                                return true;
                            }
                            async Task SendPreparedAsync()
                            {
                                try
                                {
                                    await session.SendPreparedPacketBatchAsync(() =>
                                    {
                                        // This also prevents a timed-out send-lock waiter
                                        // from preparing or writing after its caller moves on.
                                        if (ct.IsCancellationRequested || !SourceCurrent()) return Array.Empty<byte[]>();
                                        published = _repository.GetRosterForMember(id);
                                        // A refresh projects this current character's latest
                                        // membership, including removal while it waited. Only
                                        // mutation requests bind their guild before queuing.
                                        guildId = published?.Guild.Id ?? 0;
                                        pending = published == null ? _repository.GetPendingApplication(id) : null;
                                        player.Subtype0Tail ??= new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                                        clearOldGuild |= player.Subtype0Tail.GuildId != 0;
                                        identityChanged |= player.Subtype0Tail.GuildId != (uint)guildId;
                                        onlineChannels = published == null ? new Dictionary<int, byte?>()
                                            : published.Members.ToDictionary(row => row.CharacterId, row => FindOnlineChannel(row.CharacterId));
                                        var projection = BuildProjection(published, pending, id, clearOldGuild, disbanded,
                                            memberId => onlineChannels[memberId]);
                                        if (ct.IsCancellationRequested || !SourceCurrent()) return Array.Empty<byte[]>();
                                        GuildIdentityProjection.Apply(published?.Guild, player.Subtype0Tail);
                                        var packets = projection.ToList();
                                        if (identityChanged)
                                        {
                                            appearancePacket = _appearance.PrepareNoti2AppearanceUpdate(session);
                                            packets.Insert(0, appearancePacket);
                                        }
                                        prepared = true;
                                        return packets;
                                    }, CanWrite);
                                }
                                catch (OperationCanceledException) when (ct.IsCancellationRequested || !SourceCurrent() || snapshotChanged)
                                {
                                    // Expired recipients suppress their old frames while the
                                    // publisher continues with other current guild members.
                                }
                            }
                            // The existing prepared sender does not accept a token. Bound
                            // this recipient with the existing best-effort token; its prepare
                            // and each subsequent frame still validate that same token.
                            await SendPreparedAsync().WaitAsync(ct);
                            if (ct.IsCancellationRequested || !SourceCurrent()) return;
                            if (snapshotChanged) continue;
                            if (prepared && appearancePacket != null && CanWrite())
                                await _appearance.BroadcastNoti2AppearanceUpdateToArea(session, appearancePacket).WaitAsync(ct);
                            return;
                        }
                        FileLogger.Log($"[Guild] refresh deferred cid={id} attempts={maximumAttempts}");
                    }, $"guild refresh cid={id}");
                });
            }
        }

        private IReadOnlyList<byte[]> BuildProjection(GuildRoster roster, GuildPendingApplication pending,
            int characterId, bool clearOldGuild, bool disbanded, Func<int, byte?> findOnlineChannel)
        {
            if (roster != null)
                return new[]
                {
                    GuildJoinPacketBuilder.Rank(roster.Members.Single(m => m.CharacterId == characterId).Rank),
                    GuildInfoPacketBuilder.Build(roster),
                    GuildMemberPacketBuilder.Build(roster, findOnlineChannel)
                };
            var packets = new List<byte[]>();
            if (clearOldGuild) packets.Add(GuildManagementPacketBuilder.Cleared(disbanded));
            packets.Add(GuildManagementPacketBuilder.Pending(pending));
            return packets;
        }

        private static bool SameRoster(GuildRoster expected, GuildRoster current)
            => expected == null ? current == null : current != null
                && current.Guild == expected.Guild && current.LeaderName == expected.LeaderName
                && current.RequestCharacterId == expected.RequestCharacterId
                && current.GuildContribution == expected.GuildContribution
                && current.RequesterContribution == expected.RequesterContribution
                && current.Members.Select(row => row with { OfflineSeconds = 0 })
                    .SequenceEqual(expected.Members.Select(row => row with { OfflineSeconds = 0 }));

        internal byte? FindOnlineChannel(int id)
            => _sessions.TryGet(id, out var session) && _transitions.IsCurrent(session)
                && InventoryContext.TryGetOwnedLease(session.SessionId, id, out _)
                && GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel)
                ? checked((byte)channel.ChannelId) : null;
    }
}
