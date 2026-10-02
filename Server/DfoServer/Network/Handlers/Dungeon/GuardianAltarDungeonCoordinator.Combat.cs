using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed partial class GuardianAltarDungeonCoordinator
    {
        internal Task HandleUpgradeAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleCombatCommandAsync(session, header, body, upgrade: true);

        internal Task HandleSummonAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => HandleCombatCommandAsync(session, header, body, upgrade: false);

        private async Task HandleCombatCommandAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, bool upgrade)
        {
            var opcode = upgrade ? CmdPacketTypeA21.ADVANCE_ALTAR_UPGRADE_GAGE
                : CmdPacketTypeA21.ADVANCE_ALTAR_SUMMON_UNIT;
            if (header.cmd != 1 || header.type != (ushort)opcode || session?.Player == null)
                return;
            var run = session.Player.CurrentRun;
            var identity = run?.CaptureParticipantRoomIdentity() ?? default;
            var energy = run?.Instance?.Mechanisms.GuardianAltarEnergy;
            var fence = run == null
                ? DungeonLoadingCoordinator.CaptureFinishLoadingSourceFence(session, _sessions, null)
                : DungeonLoadingCoordinator.CaptureContinuationFence(session, _sessions, run,
                    identity.IsValid ? identity.Room : null);
            bool Current() => fence() && ReferenceEquals(session.Player.CurrentRun, run);
            if (!Current()) return;
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            try
            {
                await _transitions.RunIfCurrentAsync(session, async () =>
                {
                    if (!Current()) return;
                    var snapshot = energy?.ReadSnapshot(_monotonicMilliseconds()) ?? default;
                    var generation = snapshot.LoadingGeneration;
                    bool GuardianCurrent() => Current() && energy != null && identity.IsValid
                        && run.RunState == DungeonRunState.Active && run.Matches(identity)
                        && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarEnergy, energy)
                        && run.Instance.TryGetRoom(identity.Room.RoomInstanceId, out var room)
                        && room.IsCurrentLoadingGeneration(generation);
                    var result = GuardianAltarEnergyOperationResult.NotReady;
                    var accepted = false;
                    var previousLevel = snapshot.Level;
                    var replyBody = GuardianAltarCombatBodyBuilder.BuildRejected();
                    if (upgrade && GuardianAltarCombatRequestParser.TryParseUpgrade(body, out var command)
                        && GuardianCurrent())
                    {
                        // ClientGauge is diagnostic only; the server clock and
                        // balance authorize the operation, never client energy.
                        accepted = energy.TryUpgrade(identity, generation, command.ClientLevel,
                            _monotonicMilliseconds(), out snapshot, out result);
                        if (accepted || result is GuardianAltarEnergyOperationResult.MaximumLevel
                            or GuardianAltarEnergyOperationResult.InsufficientEnergy)
                            replyBody = GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true,
                                accepted ? 0 : result == GuardianAltarEnergyOperationResult.MaximumLevel ? 2 : 3,
                                previousLevel, snapshot.Level, snapshot.EnergyFixed);
                        else if (result == GuardianAltarEnergyOperationResult.StaleLevel
                            && command.ClientLevel < snapshot.Level)
                            // A committed upgrade whose ACK was lost must be
                            // recoverable. Reproject current authority without
                            // charging again or applying another upgrade.
                            replyBody = GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true,
                                0, command.ClientLevel, snapshot.Level, snapshot.EnergyFixed);
                    }
                    else if (!upgrade && GuardianAltarCombatRequestParser.TryParseSummon(body, out var summon)
                        && GuardianCurrent())
                    {
                        var unit = run.Instance.Mechanisms.GuardianAltar.Units.SingleOrDefault(x =>
                            x.Slot == summon.Slot && x.PrototypeId == summon.UnitPrototypeId);
                        if (unit != null)
                        {
                            accepted = energy.TrySummon(identity, generation, summon.ClientLevel, unit,
                                _monotonicMilliseconds(), out snapshot, out result);
                            if (accepted)
                                replyBody = GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(
                                    true, 0, unit.Slot, unit.PrototypeId, snapshot.EnergyFixed);
                        }
                    }
                    bool ReplyCurrent() => Current() && (energy == null
                        || !snapshot.IsReady || GuardianCurrent() && energy.IsCurrent(snapshot));
                    var reply = GamePacketEnvelopeBuilder.Build(1, (ushort)opcode, replyBody);
                    try
                    {
                        await _sendPause(session, () => ReplyCurrent() ? new[] { reply }
                            : Array.Empty<byte[]>(), ReplyCurrent);
                    }
                    catch (Exception exception)
                    {
                        // There is no client command nonce or delivery ACK.
                        // Keep the spend/cooldown; never replay a summon blindly.
                        FileLogger.Log($"[GuardianAltarCombat] command reply failed cid={session.Player.CharacterId} "
                            + $"opcode={opcode} accepted={accepted}: {exception.Message}");
                    }
                    FileLogger.Log($"[GuardianAltarCombat] command={opcode} accepted={accepted} result={result} "
                        + $"cid={session.Player.CharacterId} run={run?.RunId ?? 0}/{run?.RunGeneration ?? 0} "
                        + $"generation={generation} level={snapshot.Level} energy={snapshot.EnergyFixed}/{snapshot.Scale}");
                });
            }
            finally { gate.Release(); }
        }
    }
}
