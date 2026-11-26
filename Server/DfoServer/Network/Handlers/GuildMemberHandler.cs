using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Network.Builders.Guilds;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildMemberHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;

        internal GuildMemberHandler(GuildRepository repository, CharacterTransitionCoordinator transitions, ISessionDirectory sessions)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        internal Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, session.Player.CharacterId, out var lease)) return;
                if (body != null && body.Length != 0)
                {
                    await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(CmdPacketTypeA21.GUILD_MEMER_LIST));
                    return;
                }
                var roster = _repository.GetRosterForMember(lease.CharacterId);
                if (roster == null)
                {
                    await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(CmdPacketTypeA21.GUILD_MEMER_LIST));
                    return;
                }
                var packet = GuildMemberPacketBuilder.Build(roster, FindOnlineChannel);
                await session.SendPacketAsync(packet);
                FileLogger.Log($"[Guild] members cid={lease.CharacterId} guild={roster.Guild.Id} count={roster.Members.Count}");
            });

        internal Task HandleContributionHistory(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var player = session?.Player;
            var account = session?.Account;
            var gameSession = session?.GameSession;
            if (player == null || account == null || gameSession == null || player.CharacterId <= 0
                || player.UserId == 0 || account.AccountId <= 0
                || !InventoryContext.TryGetOwnedLease(session.SessionId, player.CharacterId, out var lease)
                || lease.AccountId != account.AccountId)
                return Task.CompletedTask;
            int characterId = player.CharacterId, accountId = account.AccountId;
            ushort userId = player.UserId;
            long runGeneration = player.CurrentDungeonRunGeneration;
            long selectionGeneration = player.CurrentDungeonSelectionGeneration;
            bool SourceCurrent() => ReferenceEquals(session.Player, player) && ReferenceEquals(session.Account, account)
                && ReferenceEquals(session.GameSession, gameSession) && player.CharacterId == characterId
                && player.UserId == userId && account.AccountId == accountId
                && player.CurrentDungeonRunGeneration == runGeneration
                && player.CurrentDungeonSelectionGeneration == selectionGeneration
                && InventoryContext.IsCurrentLease(lease, session.SessionId, characterId)
                && _transitions.IsCurrent(session);
            return _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!SourceCurrent()) return;
                int guildId = _repository.GetForMember(characterId)?.Id ?? 0;
                bool SourceGuildCurrent() => SourceCurrent() && (_repository.GetForMember(characterId)?.Id ?? 0) == guildId;
                var command = CmdPacketTypeA21.GUILD_CONTRIBUTE_HISTORY;
                try
                {
                    // Prepare only after acquiring the existing session send lock.
                    // Both data and failure ACKs belong to this original request source.
                    await session.SendPreparedPacketBatchAsync(() =>
                    {
                        if (!SourceGuildCurrent()) return Array.Empty<byte[]>();
                        if (body != null && body.Length != 0)
                            return new[] { GuildCreationPacketBuilder.Failure(command) };
                        try
                        {
                            var history = _repository.GetContributionHistoryForMember(characterId, accountId,
                                GuildContributionHistoryPacketBuilder.Capacity);
                            if ((history?.GuildId ?? 0) != guildId) return Array.Empty<byte[]>();
                            var packet = history == null ? GuildCreationPacketBuilder.Failure(command)
                                : GuildContributionHistoryPacketBuilder.Build(history);
                            FileLogger.Log($"[Guild] contribution history cid={characterId} guild={history?.GuildId ?? 0} rows={history?.Rows.Count ?? 0}");
                            return new[] { packet };
                        }
                        catch (Exception exception) when (exception is SqliteException or InvalidDataException or JsonException or InvalidOperationException or ArgumentOutOfRangeException)
                        {
                            FileLogger.Log($"[Guild] contribution history failed cid={characterId} error={exception.Message}");
                            return new[] { GuildCreationPacketBuilder.Failure(command) };
                        }
                    }, SourceGuildCurrent);
                }
                catch (OperationCanceledException) when (!SourceGuildCurrent())
                {
                    // Expired source is a suppressed projection, not a new failure
                    // to send to the replacement role, lease or guild.
                }
            });
        }

        private byte? FindOnlineChannel(int characterId)
        {
            if (!_sessions.TryGet(characterId, out var session)
                || !InventoryContext.TryGetOwnedLease(session.SessionId, characterId, out _)
                || !GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel)) return null;
            return checked((byte)channel.ChannelId);
        }
    }
}
