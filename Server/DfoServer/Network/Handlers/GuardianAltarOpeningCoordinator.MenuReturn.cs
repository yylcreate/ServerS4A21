using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class GuardianAltarOpeningCoordinator
    {
        // A successful menu projection authorizes its exit, not combat admission.
        // The existing PlayerContext epochs invalidate it after ordinary selection
        // (including selection then cancellation) or a different dungeon run.
        private readonly Dictionary<Guid, MenuProjectionReceipt> _menuProjections = new();

        private sealed record MenuProjectionReceipt(
            PlayerContext Player, GameSession Generation, AccountRecord Account,
            int CharacterId, int AccountId, ushort UserId, byte TownId, byte AreaId,
            long RunGeneration, long SelectionGeneration)
        {
            internal long? CompletedMovementSequence { get; set; }
        }

        private MenuProjectionReceipt CaptureMenuProjection(EnhancedClientSession session)
        {
            var player = session.Player;
            var account = session.Account;
            if (account == null || account.AccountId <= 0 || !IsTownReady(session))
                return null;
            return new MenuProjectionReceipt(player, session.GameSession, account,
                player.CharacterId, account.AccountId, player.UserId,
                player.CurTownId, player.CurAreaId, player.CurrentDungeonRunGeneration,
                player.CurrentDungeonSelectionGeneration);
        }

        private bool IsMenuScopeCurrent(EnhancedClientSession session, MenuProjectionReceipt receipt)
            => receipt != null && ReferenceEquals(session?.Player, receipt.Player)
                && ReferenceEquals(session.Account, receipt.Account)
                && session.Account?.AccountId == receipt.AccountId
                && session.Player.UserId == receipt.UserId
                && IsCurrentGeneration(session, receipt.CharacterId, receipt.Generation)
                && IsTownReady(session) && session.Player.CurrentRun == null
                && session.Player.CurTownId == receipt.TownId
                && session.Player.CurAreaId == receipt.AreaId
                && session.Player.CurrentDungeonRunGeneration == receipt.RunGeneration
                && session.Player.CurrentDungeonSelectionGeneration == receipt.SelectionGeneration;

        private void RememberMenuProjection(EnhancedClientSession session, MenuProjectionReceipt receipt)
        {
            // Called only after the complete successful menu write, never from a
            // prepare callback, schedule notification or rejected/failed reply.
            if (!IsMenuScopeCurrent(session, receipt))
                return;
            lock (_notificationSync)
            {
                if (!_disposed)
                    _menuProjections[session.SessionId] = receipt;
            }
        }

        internal async Task<bool> TryHandleMenuReturnAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body,
            Func<EnhancedClientSession, Func<bool>, Task<bool>> projectTown)
        {
            if (session == null || header.cmd != 1
                || header.type != (ushort)CmdPacketTypeA21.BACK_2_VILLAGE
                || (body?.Length ?? 0) != 0)
                return false;
            ArgumentNullException.ThrowIfNull(projectTown);
            MenuProjectionReceipt receipt;
            lock (_notificationSync)
                _menuProjections.TryGetValue(session.SessionId, out receipt);
            bool Current()
            {
                if (!IsMenuScopeCurrent(session, receipt))
                    return false;
                lock (_notificationSync)
                    return _menuProjections.TryGetValue(session.SessionId, out var current)
                        && ReferenceEquals(current, receipt)
                        && (!receipt.CompletedMovementSequence.HasValue
                            || receipt.CompletedMovementSequence.Value
                                == session.Player.LatestTownMovementSequence);
            }
            if (!Current())
                return false;

            // Capture before awaiting the shared character gate. If this menu
            // becomes stale while waiting, suppress it instead of falling through
            // into an exit handler that could end the replacement run.
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!Current())
                    return;
                try
                {
                    var prepared = false;
                    await _sendBatch(session, () =>
                    {
                        if (!Current())
                            return Array.Empty<byte[]>();
                        prepared = true;
                        return new[] { TownHandler.BuildReturnToTownSuccessPacket(header.type) };
                    });
                    if (!prepared || !Current())
                        return;
                    // Lost ACK retries are harmless. Only incomplete returns
                    // replay the common town projection; no ticket/progress write.
                    if (receipt.CompletedMovementSequence.HasValue)
                        return;
                    if (!await projectTown(session, Current) || !Current())
                        return;
                    lock (_notificationSync)
                    {
                        if (_menuProjections.TryGetValue(session.SessionId, out var current)
                            && ReferenceEquals(current, receipt))
                            receipt.CompletedMovementSequence = session.Player.LatestTownMovementSequence;
                    }
                    FileLogger.Log($"[GuardianAltarEntry] menu BACK_2_VILLAGE completed "
                        + $"cid={receipt.CharacterId} runGeneration={receipt.RunGeneration} "
                        + $"selectionGeneration={receipt.SelectionGeneration}");
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // Keep the same receipt for a retry. A partially written town
                    // projection does not create a new menu or persistent effect.
                    FileLogger.Log($"[GuardianAltarEntry] menu return projection failed "
                        + $"cid={receipt.CharacterId}: {ex.Message}");
                }
            });
            return true;
        }
    }
}
