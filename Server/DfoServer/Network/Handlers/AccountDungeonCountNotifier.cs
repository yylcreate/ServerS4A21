using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    // A projection adapter, without a second count cache or timer. Counts and
    // day rollover remain owned by DungeonEntryLimitService.
    internal sealed class AccountDungeonCountNotifier
    {
        private readonly ISessionDirectory _sessions;
        private readonly int _dungeonId;
        private readonly int _maximumCount;
        private readonly Func<int, int, IReadOnlyList<SpecialDungeonEntryLimitSnapshot>> _load;
        private readonly Func<int> _dayId;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> _send;

        internal AccountDungeonCountNotifier(DungeonEntryLimitService limits,
            ISessionDirectory sessions, int dungeonId, int maximumCount,
            Func<int, int, IReadOnlyList<SpecialDungeonEntryLimitSnapshot>> load = null,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> send = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _dungeonId = dungeonId;
            _maximumCount = maximumCount;
            _dayId = limits == null
                ? (() => Game.DailyReset.DailyResetService.TodayId(DateTime.UtcNow)) : limits.CurrentDayId;
            _load = load ?? (limits ?? throw new ArgumentNullException(nameof(limits))).LoadSpecialDungeonLimits;
            _send = send ?? ((session, prepare, guard) => session.SendPreparedPacketBatchAsync(prepare, guard));
        }

        internal async Task PublishWhileTransitionHeldAsync(EnhancedClientSession session)
        {
            var player = session?.Player;
            var account = session?.Account;
            var generation = session?.GameSession;
            if (player == null || account == null || generation == null
                || player.CharacterId <= 0 || account.AccountId <= 0 || _maximumCount <= 0)
                return;
            var characterId = player.CharacterId;
            var accountId = account.AccountId;
            var uid = player.UserId;
            var runGeneration = player.CurrentDungeonRunGeneration;
            var selectionGeneration = player.CurrentDungeonSelectionGeneration;
            var dayId = _dayId();
            bool Current() => ReferenceEquals(session.Player, player)
                && ReferenceEquals(session.Account, account) && ReferenceEquals(session.GameSession, generation)
                && player.CharacterId == characterId && player.UserId == uid && account.AccountId == accountId
                && player.CurrentDungeonRunGeneration == runGeneration
                && player.CurrentDungeonSelectionGeneration == selectionGeneration
                && _dayId() == dayId
                && _sessions.TryGet(characterId, out var registered) && ReferenceEquals(registered, session);
            if (!Current())
                return;
            try
            {
                var limits = _load(accountId, characterId);
                if (limits == null)
                    return;
                foreach (var row in limits)
                    if (row?.DungeonId == _dungeonId && row.DayId != dayId)
                        return;
                if (!Current() || !AccountDungeonCountProjection.TryCreate(_dungeonId, _maximumCount,
                        limits, out var count, out var reason))
                {
                    FileLogger.Log($"[AccountDungeonCount] projection unavailable cid={characterId} dungeon={_dungeonId}");
                    return;
                }
                await _send(session, () => Current()
                    ? new[]
                    {
                        GamePacketEnvelopeBuilder.Build(0,
                            (ushort)NotiPacketTypeA21.DUNGEON_ENTER_COUNT_INFO_INACCOUNT,
                            AccountDungeonCountPacketBuilder.BuildBody(count)),
                        GamePacketEnvelopeBuilder.Build(0,
                            (ushort)NotiPacketTypeA21.ACCOUNT_DUNGEON_INOUT_COUNT,
                            AccountDungeonCountPacketBuilder.BuildRemainingBody(count)),
                    }
                    : Array.Empty<byte[]>(), Current);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or SocketException
                or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                FileLogger.Log($"[AccountDungeonCount] projection failed cid={characterId} dungeon={_dungeonId}: {exception.Message}");
            }
        }
    }
}
