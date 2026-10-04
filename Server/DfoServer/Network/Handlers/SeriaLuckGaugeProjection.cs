using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Events.SeriaLuck;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    // Capture before the inventory transaction, retaining its recipient across awaits.
    internal sealed class SeriaLuckGaugeProjection
    {
        private readonly EnhancedClientSession _session;
        private readonly AccountRecord _account;
        private readonly GameSession _generation;
        private readonly InventoryLease _lease;
        private readonly int _characterId;
        private readonly int _accountId;
        private readonly ushort _userId;

        private SeriaLuckGaugeProjection(EnhancedClientSession session, InventoryLease lease)
        {
            _session = session; _account = session.Account; _generation = session.GameSession;
            _lease = lease; _characterId = session.Player.CharacterId;
            _accountId = session.Account.AccountId; _userId = session.Player.UserId;
        }

        internal static SeriaLuckGaugeProjection Capture(EnhancedClientSession session, InventoryLease lease)
        {
            if (session?.Account == null || session.GameSession == null
                || session.Account.AccountId <= 0 || session.Player.CharacterId <= 0
                || lease == null || lease.AccountId != session.Account.AccountId
                || lease.CharacterId != session.Player.CharacterId
                || !InventoryContext.IsCurrentLease(lease, session.SessionId, session.Player.CharacterId))
                return null;
            var source = new SeriaLuckGaugeProjection(session, lease);
            return source.IsCurrent() ? source : null;
        }

        internal bool IsCurrent()
            => ReferenceEquals(_session.Account, _account)
                && ReferenceEquals(_session.GameSession, _generation)
                && _session.Account.AccountId == _accountId
                && _session.Player.CharacterId == _characterId
                && _session.Player.UserId == _userId
                && _lease.AccountId == _accountId
                && InventoryContext.IsCurrentLease(_lease, _session.SessionId, _characterId);

        internal static bool TryBuildPacket(SeriaLuckUiService ui, BoosterUseResult result, out byte[] packet)
        {
            packet = null;
            if (ui == null || result?.ErrorCode != 0 || !result.IsSeriaLuckValueSource
                || result.SourceExpiredDeleted || result.ConsumedSourceCount <= 0
                || result.SourceItemTemplateId != SeriaLuckItemConstants.ItemTemplateId
                || !ui.TryGetGauge(result.SeriaLuckValueBefore, out var before)
                || !ui.TryGetGauge(result.SeriaLuckValueAfter, out var after))
                return false;
            // 13A6BB0 reads two U8; 13A21F0 stores before-display then after-current.
            // 13A2780 subsequently advances the first display value to current.
            packet = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.BOOSTER_GAGE, new byte[] { before, after });
            return true;
        }

        internal async Task<bool> SendAsync(SeriaLuckUiService ui, BoosterUseResult committedResult)
        {
            if (!IsCurrent() || !TryBuildPacket(ui, committedResult, out var packet)) return false;
            try { return await _session.TrySendPacketAsync(packet, CancellationToken.None, IsCurrent); }
            catch (Exception ex) when (ex is IOException || ex is SocketException
                || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // Already committed: reconnect restores projection; never repeat the reward.
                FileLogger.Log($"[SeriaLuckUi] committed gauge send failed cid={_characterId}: {ex.Message}");
                return false;
            }
        }
    }
}
