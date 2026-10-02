using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        private readonly Func<long> _monotonicMilliseconds;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> _sendPause;

        internal void RegisterRuntimeHandlers(GameCommandRegistry.GameCommandRegistrationGroup handlers)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            handlers[(ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE] = HandlePauseAsync;
            handlers[(ushort)CmdPacketTypeA21.ADVANCE_ALTAR_UPGRADE_GAGE] = HandleUpgradeAsync;
            handlers[(ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SUMMON_UNIT] = HandleSummonAsync;
        }

        internal static void OnLoadingReleased(DungeonRun run, DungeonRoomIdentity roomIdentity,
            long loadingGeneration)
        {
            if (run?.Instance?.Mechanisms.GuardianAltarEnergy is not { } energy
                || run.RunState != DungeonRunState.Active
                || !run.Instance.TryGetRoom(roomIdentity.RoomInstanceId, out var room)
                || !room.Identity.Equals(roomIdentity)
                || !room.IsCurrentLoadingGeneration(loadingGeneration))
                return;
            var source = new DungeonParticipantRoomIdentity(run.CaptureIdentity(), roomIdentity);
            if (run.Matches(source))
            {
                energy.BindLoadingRelease(source, loadingGeneration);
                run.Instance.Mechanisms.GuardianAltarCombat?.BindLoadingRelease(source, loadingGeneration);
            }
            // Binding records successful release only. The first client
            // PAUSE(false), not loading time, starts natural regeneration.
        }

        internal async Task HandlePauseAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE
                || session?.Player == null)
                return;
            var run = session.Player.CurrentRun;
            var sourceRoom = run?.CaptureParticipantRoomIdentity() ?? default;
            var energy = run?.Instance?.Mechanisms.GuardianAltarEnergy;
            // The shared continuation fence includes account/cid/UID,
            // GameSession, connection, session directory, run and room.
            var sourceCurrent = run == null
                ? DungeonLoadingCoordinator.CaptureFinishLoadingSourceFence(session, _sessions, null)
                : DungeonLoadingCoordinator.CaptureContinuationFence(
                    session, _sessions, run, sourceRoom.IsValid ? sourceRoom.Room : null);
            bool Current() => sourceCurrent()
                && (run == null || ReferenceEquals(session.Player.CurrentRun, run));
            if (!Current()) return;
            var parsed = GuardianAltarPauseRequestParser.TryParse(body, out var request);
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    if (!Current()) return;
                    // Loading can finish while this request waits for the run
                    // gate. Read the released generation here from the same
                    // captured runtime; never reject it using an earlier zero.
                    var snapshot = energy?.ReadSnapshot(_monotonicMilliseconds()) ?? default;
                    var generation = snapshot.LoadingGeneration;
                    bool GuardianCurrent() => Current() && sourceRoom.IsValid
                        && run.RunState == DungeonRunState.Active
                        && run.Matches(sourceRoom)
                        && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarEnergy, energy)
                        && run.Instance.TryGetRoom(sourceRoom.Room.RoomInstanceId, out var room)
                        && room.IsCurrentLoadingGeneration(generation);
                    var accepted = false;
                    if (parsed && energy != null && GuardianCurrent())
                        accepted = energy.TrySetPaused(sourceRoom, generation, request.Paused,
                            _monotonicMilliseconds(), out snapshot);
                    if (accepted)
                        ScheduleCombat(session, run, snapshot);
                    bool ReplyCurrent() => accepted
                        ? GuardianCurrent() && energy.IsCurrent(snapshot)
                        : Current();
                    var reply = GamePacketEnvelopeBuilder.Build(1,
                        (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE,
                        GuardianAltarPauseAckBodyBuilder.Build(accepted, accepted ? snapshot.Paused : false));
                    try
                    {
                        // No activity-window/day fence: a menu admission
                        // revision must not revoke time in an already live run.
                        await _sendPause(session,
                            () => ReplyCurrent() ? new[] { reply } : Array.Empty<byte[]>(), ReplyCurrent);
                    }
                    catch (Exception ex)
                    {
                        // A legal elapsed-time transition is not a durable
                        // reward and is not reset when an ACK write fails.
                        FileLogger.Log($"[GuardianAltarEnergy] pause reply failed cid={session.Player.CharacterId}: {ex.Message}");
                    }
                    FileLogger.Log($"[GuardianAltarEnergy] pause parsed={parsed} accepted={accepted} "
                        + $"cid={session.Player.CharacterId} run={run?.RunId ?? 0}/{run?.RunGeneration ?? 0} "
                        + $"generation={generation} paused={snapshot.Paused} energy={snapshot.EnergyFixed}/{snapshot.Scale} "
                        + $"level={snapshot.Level} clientClock=true wavesEnabled=true");
                });
            }
            finally { gate.Release(); }
        }
    }
}
