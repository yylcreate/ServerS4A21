using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class GuardianAltarOpeningCoordinator
    {
        // A successful menu projection authorizes its exit, not combat admission.
        // The existing PlayerContext epochs invalidate it after ordinary selection
        // (including selection then cancellation) or a different dungeon run.
        private readonly Dictionary<Guid, MenuProjectionReceipt> _menuProjections = new();
        private readonly Dictionary<Guid, RunMenuContinuationReceipt> _runMenuContinuations = new();

        private sealed record RunMenuContinuationReceipt(
            PlayerContext Player, GameSession Generation, AccountRecord Account,
            int CharacterId, int AccountId, ushort UserId, DungeonRunIdentity EndedRun,
            long SelectionGeneration, byte TownId, byte AreaId, long MovementSequence);

        // Only the dedicated result consumer calls this after an actually sent
        // event1/option1 ACK. A successful expected-run return can authorize its
        // pending menu response, but never a new run or progression mutation.
        internal async Task ReturnToStageSelectionAsync(EnhancedClientSession session,
            DungeonRunIdentity expectedRun, Func<bool> sourceCurrent, Func<Task<bool>> returnToTown)
        {
            ArgumentNullException.ThrowIfNull(sourceCurrent);
            ArgumentNullException.ThrowIfNull(returnToTown);
            var player = session?.Player;
            var account = session?.Account;
            var run = player?.CurrentRun;
            if (player == null || account == null || !expectedRun.IsValid
                || run == null || !run.Matches(expectedRun) || !sourceCurrent()
                || !IsCurrentGeneration(session, player.CharacterId, session.GameSession))
                return;
            var anchor = run.TownReturnAnchor;
            if (!anchor.IsValid || account.AccountId <= 0 || player.UserId == 0)
                return;
            var receipt = new RunMenuContinuationReceipt(player, session.GameSession, account,
                player.CharacterId, account.AccountId, player.UserId, expectedRun,
                player.CurrentDungeonSelectionGeneration, anchor.TownId, anchor.AreaId,
                player.LatestTownMovementSequence);
            if (!sourceCurrent() || !await returnToTown() || !IsRunMenuContinuationScopeCurrent(session, receipt))
                return;
            lock (_notificationSync)
            {
                if (!IsRunMenuContinuationScopeCurrent(session, receipt))
                    return;
                _runMenuContinuations[session.SessionId] = receipt;
            }
            FileLogger.Log($"[GuardianAltarEntry] stage-selection continuation returned cid={receipt.CharacterId} "
                + $"run={expectedRun.RunId}/{expectedRun.RunGeneration} selectionGeneration={receipt.SelectionGeneration}");
        }

        private bool IsRunMenuContinuationScopeCurrent(EnhancedClientSession session, RunMenuContinuationReceipt receipt)
            => receipt != null && ReferenceEquals(session?.Player, receipt.Player)
                && ReferenceEquals(session.Account, receipt.Account) && session.Account?.AccountId == receipt.AccountId
                && session.Player.UserId == receipt.UserId
                && IsCurrentGeneration(session, receipt.CharacterId, receipt.Generation)
                && IsTownReady(session) && session.Player.CurrentRun == null
                && session.Player.CurrentDungeonRunGeneration == receipt.EndedRun.RunGeneration
                && session.Player.CurrentDungeonSelectionGeneration == receipt.SelectionGeneration
                && session.Player.CurTownId == receipt.TownId && session.Player.CurAreaId == receipt.AreaId
                && session.Player.LatestTownMovementSequence == receipt.MovementSequence;

        private RunMenuContinuationReceipt CaptureRunMenuContinuation(EnhancedClientSession session)
        {
            lock (_notificationSync)
                return _runMenuContinuations.TryGetValue(session.SessionId, out var receipt)
                    && IsRunMenuContinuationScopeCurrent(session, receipt) ? receipt : null;
        }

        private bool IsRunMenuContinuationCurrent(EnhancedClientSession session, RunMenuContinuationReceipt receipt)
        {
            if (!IsRunMenuContinuationScopeCurrent(session, receipt))
                return false;
            lock (_notificationSync)
                return _runMenuContinuations.TryGetValue(session.SessionId, out var current)
                    && ReferenceEquals(current, receipt);
        }

        private void CompleteRunMenuContinuation(EnhancedClientSession session, RunMenuContinuationReceipt receipt)
        {
            lock (_notificationSync)
                if (_runMenuContinuations.TryGetValue(session.SessionId, out var current)
                    && ReferenceEquals(current, receipt))
                    _runMenuContinuations.Remove(session.SessionId);
        }

        // Already inside the character transition. This is a reply to the
        // client's real pending START_GAME, using existing durable state only.
        // It must not call LoadMenu, initialize records or claim a daily refill.
        private async Task<bool> TryProjectRunMenuContinuationAsync(EnhancedClientSession session,
            RunMenuContinuationReceipt receipt)
        {
            if (receipt == null)
                return false;
            if (!IsRunMenuContinuationCurrent(session, receipt))
                return true;
            if (_progress is not IGuardianAltarExistingMenuService existingMenus)
                return false;
            bool Current() => IsRunMenuContinuationCurrent(session, receipt);
            try
            {
                var menu = existingMenus.ReadExistingMenu(receipt.CharacterId, receipt.AccountId, Current);
                if (menu?.CharacterId != receipt.CharacterId)
                    throw new InvalidOperationException("Guardian returned menu has no current character owner.");
                GuardianAltarProgressValidator.Validate(menu);
                if (!Current())
                    return true;
                var prepared = false;
                MenuProjectionReceipt menuReceipt = null;
                await _sendBatch(session, () =>
                {
                    if (!Current())
                        return Array.Empty<byte[]>();
                    menuReceipt = CaptureMenuProjection(session);
                    if (menuReceipt == null)
                        return Array.Empty<byte[]>();
                    var packet = GamePacketEnvelopeBuilder.Build(1,
                        (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME, GuardianAltarMenuBodyBuilder.Build(menu));
                    session.GuardianAltarControls = new EnhancedClientSession.GuardianAltarControlProjectionSource(
                        receipt.Generation, receipt.Account, receipt.CharacterId, receipt.AccountId, receipt.UserId,
                        receipt.EndedRun.RunGeneration, receipt.SelectionGeneration);
                    prepared = true;
                    return new[] { packet };
                });
                if (!prepared || !Current())
                    return true;
                RememberMenuProjection(session, menuReceipt);
                CompleteRunMenuContinuation(session, receipt);
                FileLogger.Log($"[GuardianAltarEntry] stage-selection menu resumed cid={receipt.CharacterId} "
                    + $"run={receipt.EndedRun.RunId}/{receipt.EndedRun.RunGeneration} refill=false combatAdmission=false");
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                or InvalidDataException or IOException or SocketException or ObjectDisposedException or OverflowException)
            {
                // Keep the one-shot source for an intact retry. A stale source
                // cannot fall through and authorize a replacement menu/run.
                FileLogger.Log($"[GuardianAltarEntry] stage-selection continuation deferred "
                    + $"cid={receipt.CharacterId}: {ex}");
            }
            return true;
        }

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
