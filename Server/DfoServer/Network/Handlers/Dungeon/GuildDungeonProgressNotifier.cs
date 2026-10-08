using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class GuildDungeonProgressNotifier
    {
        private readonly GuildDungeonService _guildDungeons;
        private readonly GuildRepository _guilds;
        private readonly ISessionDirectory _sessions;
        internal GuildDungeonProgressNotifier(IGameDatabase database, GuildDungeonService service,
            ISessionDirectory sessions = null)
        {
            _guildDungeons = service ?? throw new ArgumentNullException(nameof(service));
            _guilds = new GuildRepository(database);
            _sessions = sessions;
        }

        internal async Task SendAsync(EnhancedClientSession session, string reason, bool includeContribution = false)
        {
            var player = session?.Player;
            if (player == null || player.CharacterId <= 0 || player.UserId == 0
                || session.Account?.AccountId <= 0 || session.Account == null || session.GameSession == null)
                return;
            var account = session.Account;
            var gameSession = session.GameSession;
            int cid = player.CharacterId, accountId = account.AccountId;
            ushort uid = player.UserId;
            long runGeneration = player.CurrentDungeonRunGeneration, selectionGeneration = player.CurrentDungeonSelectionGeneration;
            if (!_guildDungeons.Catalog.IsValid
                || !InventoryContext.TryGetOwnedLease(session.SessionId, cid, out var lease))
                return;
            _guildDungeons.LoadForMember(cid, out var guildId);
            // Empty resets are sent for a real nonmember character too, so
            // switching characters cannot retain a previous guild's counters.
            bool SourceCurrent() => ReferenceEquals(session.Player, player) && ReferenceEquals(session.Account, account)
                && ReferenceEquals(session.GameSession, gameSession) && player.CharacterId == cid && player.UserId == uid
                && account.AccountId == accountId && player.CurrentDungeonRunGeneration == runGeneration
                && player.CurrentDungeonSelectionGeneration == selectionGeneration
                && InventoryContext.IsCurrentLease(lease, session.SessionId, cid)
                && (_sessions == null || (_sessions.TryGet(cid, out var current) && ReferenceEquals(current, session)))
                && (_guilds.GetForMember(cid)?.Id ?? 0) == guildId;
            // Another instance can legitimately commit this guild's next clear
            // while a packet is being prepared. Retry the projection, without
            // treating that progress race as failed admission or settlement.
            const int maximumAttempts = 3;
            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                GuildDungeonProjection published = null;
                GuildRoster publishedRoster = null;
                bool prepared = false, progressChanged = false;
                bool CanWrite()
                {
                    if (!SourceCurrent()) return false;
                    if (SameProgress(published, cid, guildId)
                        && (!includeContribution || SameContribution(publishedRoster, cid, published.MonthId))) return true;
                    progressChanged = true;
                    return false;
                }
                try
                {
                    await session.SendPreparedPacketBatchAsync(() =>
                    {
                        if (!SourceCurrent()) return Array.Empty<byte[]>();
                        published = _guildDungeons.LoadProjectionForMember(cid);
                        if (published.GuildId != guildId) return Array.Empty<byte[]>();
                        var registered = _guildDungeons.Catalog.Config.Dungeons.ToDictionary(row => row.DungeonId,
                            row => published.ClearCounts.TryGetValue(row.DungeonId, out var count) ? count : 0);
                        if (includeContribution && guildId > 0)
                        {
                            publishedRoster = _guilds.GetRosterForMember(cid, published.MonthId);
                            if (publishedRoster?.Guild.Id != guildId || publishedRoster.RequestCharacterId != cid)
                                return Array.Empty<byte[]>();
                        }
                        prepared = true;
                        var packets = new List<byte[]> { GuildDungeonPacketBuilder.Progress(registered, published.ResetEndAtSeconds), GuildDungeonPacketBuilder.RepeatBonus() };
                        if (publishedRoster != null) packets.Add(GuildInfoPacketBuilder.Build(publishedRoster));
                        return packets.ToArray();
                    }, CanWrite);
                    if (prepared && SourceCurrent())
                        FileLogger.Log($"[GuildDungeon] progress projected cid={cid} guild={guildId} month={published.MonthId} resetEnd={published.ResetEndAtSeconds} counts=[{string.Join(",", published.ClearCounts.OrderBy(row => row.Key).Select(row => row.Key + ":" + row.Value))}] reason={reason}");
                    return;
                }
                catch (OperationCanceledException) when (progressChanged && SourceCurrent())
                {
                    // The send lock has been released by the batch sender. Each
                    // retry keeps the original identity and reloads raw counts.
                }
            }
            FileLogger.Log($"[GuildDungeon] progress projection deferred cid={cid} guild={guildId} attempts={maximumAttempts} reason={reason}");
        }

        private bool SameProgress(GuildDungeonProjection expected, int characterId, int guildId)
        {
            var current = _guildDungeons.LoadProjectionForMember(characterId);
            return current.GuildId == guildId && current.MonthId == expected.MonthId
                && current.ResetEndAtSeconds == expected.ResetEndAtSeconds
                && current.ClearCounts.Count == expected.ClearCounts.Count
                && expected.ClearCounts.All(row => current.ClearCounts.TryGetValue(row.Key, out var value) && value == row.Value);
        }

        private bool SameContribution(GuildRoster expected, int characterId, int monthId)
        {
            var current = _guilds.GetRosterForMember(characterId, monthId);
            if (expected == null) return current == null;
            return current?.Guild == expected.Guild && current.RequestCharacterId == characterId
                && current.GuildContribution == expected.GuildContribution
                && current.RequesterContribution == expected.RequesterContribution;
        }
    }
}
