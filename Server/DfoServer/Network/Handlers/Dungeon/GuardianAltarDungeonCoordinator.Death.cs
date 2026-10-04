using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        internal async Task<bool> TryHandleCharacterDeathAsync(EnhancedClientSession session, DungeonRun run)
        {
            if (run?.Instance?.Mechanisms.GuardianAltarCombat == null)
                return false;
            var identity = run.CaptureParticipantRoomIdentity();
            var account = session?.Account;
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run,
                identity.IsValid ? identity.Room : null,
                () => ReferenceEquals(session.Account, account));
            var player = session?.Player;
            if (player == null || !identity.IsValid || !fence())
                return true;
            var gate = player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                    var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
                    var snapshot = energy.ReadSnapshot(_monotonicMilliseconds());
                    bool Current() => fence() && run.RunState == DungeonRunState.Active
                        && run.Matches(identity) && !combat.IsClosed
                        && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarCombat, combat)
                        && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarEnergy, energy)
                        && run.Instance.TryGetRoom(identity.Room.RoomInstanceId, out var room)
                        && room.IsCurrentLoadingGeneration(snapshot.LoadingGeneration);
                    // A not-ready or obsolete Guardian request is consumed;
                    // it cannot fall through to ordinary death/coin/party-wipe.
                    if (!Current() || !snapshot.IsReady || snapshot.IsClosed)
                        return;
                    DungeonRunLifecycle.CancelDeathRespawn(run);
                    if (!combat.TryFailCharacterDeath(identity, snapshot.LoadingGeneration,
                            snapshot.ActiveMilliseconds, out var fact))
                        return;
                    FileLogger.Log($"[GuardianAltarClear] character death cid={player.CharacterId} "
                        + $"run={run.RunId}/{run.RunGeneration} result={fact.Result} event={fact.EventId:N}");
                    // Ordinary failure has no reward; survival settles only
                    // the already completed rounds through its terminal owner.
                    // No fake center sequence or ordinary actor death packet.
                    await TryCompleteWithinTransitionAsync(session, run, Current);
                });
            }
            finally { gate.Release(); }
            return true;
        }

        internal async Task<bool> TryRejectUseCoinAsync(EnhancedClientSession session, DungeonRun run, ushort targetId)
        {
            if (run?.Instance?.Mechanisms.GuardianAltarCombat == null)
                return false;
            var identity = run.CaptureParticipantRoomIdentity();
            var account = session?.Account;
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run,
                identity.IsValid ? identity.Room : null,
                () => ReferenceEquals(session.Account, account));
            bool Current() => fence() && ReferenceEquals(session.Player.CurrentRun, run)
                && run.Matches(identity) && run.RunState == DungeonRunState.Active;
            if (!identity.IsValid || !Current())
                return true;
            var rejection = GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.USE_COIN,
                DungeonCombatHandler.BuildUseCoinRejectionBody(targetId));
            await _sendPause(session, () => Current() ? new[] { rejection } : Array.Empty<byte[]>(), Current);
            // This request never proves character death and never consumes a
            // wallet item, revives an actor or creates a terminal fact itself.
            return true;
        }
    }
}
