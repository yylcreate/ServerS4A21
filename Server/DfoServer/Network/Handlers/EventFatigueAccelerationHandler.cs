using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Events.FatigueAcceleration;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventFatigueAccelerationHandler
    {
        private readonly FatigueAccelerationEntryService _service;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>,
            Func<bool>, Task> _send;

        internal EventFatigueAccelerationHandler(FatigueAccelerationEntryService service,
            CharacterTransitionCoordinator transitions,
            Func<EnhancedClientSession, Func<IReadOnlyList<byte[]>>, Func<bool>, Task> send = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _send = send ?? ((session, prepare, guard) =>
                session.SendPreparedPacketBatchAsync(prepare, guard));
        }

        internal async Task HandleAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body)
        {
            if (header.cmd != 1
                || header.type != (ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE)
                return;
            var player = session?.Player;
            var account = session?.Account;
            var generation = session?.GameSession;
            var cid = player?.CharacterId ?? 0;
            var uid = player?.UserId ?? 0;
            var accountId = account?.AccountId ?? 0;
            var runEpoch = player?.CurrentDungeonRunGeneration ?? 0;
            var selectionEpoch = player?.CurrentDungeonSelectionGeneration ?? 0;
            if (cid <= 0 || uid == 0 || accountId <= 0 || generation == null
                || !InventoryContext.TryGetOwnedLease(session.SessionId, cid, out var inventory))
            {
                FileLogger.Log("[FatigueAcceleration] request dropped: no current character/lease");
                return;
            }
            var inventoryVersion = inventory.Version;

            bool IsCurrent() => _transitions.IsCurrent(session)
                && ReferenceEquals(session.Player, player) && player.CharacterId == cid
                && player.UserId == uid && ReferenceEquals(session.Account, account)
                && account.AccountId == accountId && ReferenceEquals(session.GameSession, generation)
                && player.CurrentDungeonRunGeneration == runEpoch
                && player.CurrentDungeonSelectionGeneration == selectionEpoch
                && inventory.Version == inventoryVersion
                && InventoryContext.IsCurrentLease(inventory, session.SessionId, cid);

            var valid = FatigueAccelerationRequestParser.TryParse(body, out var enable);
            if (!IsCurrent())
                return;
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!IsCurrent())
                    return;
                var townReady = IsTownReady(player);
                var result = _service.Evaluate(valid, enable, townReady);
                try
                {
                    await _send(session, () =>
                    {
                        if (!IsCurrent() || IsTownReady(player) != townReady)
                            return Array.Empty<byte[]>();
                        var packets = new List<byte[]> { FatigueAccelerationPacketBuilder.BuildAck(result) };
                        if (townReady)
                            packets.Add(FatigueAccelerationPacketBuilder.BuildInactivePacket());
                        if (result == FatigueAccelerationEntryResult.EffectsUnavailable
                            || result == FatigueAccelerationEntryResult.DefinitionUnavailable
                            || result == FatigueAccelerationEntryResult.EntryUnavailable)
                        {
                            packets.Add(GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                                ServerNoticeMessageBuilder.BuildRaidNotice("疲劳值燃烧暂不可用，本次未开启。")));
                        }
                        return packets;
                    }, () => IsCurrent() && IsTownReady(player) == townReady);
                }
                catch (OperationCanceledException) when (!IsCurrent() || IsTownReady(player) != townReady)
                {
                    FileLogger.Log($"[FatigueAcceleration] stale response cancelled cid={cid}");
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[FatigueAcceleration] response write failed cid={cid}: {ex.Message}");
                    return;
                }
                FileLogger.Log($"[FatigueAcceleration] cid={cid} enable={enable} valid={valid} "
                    + $"bodyLength={body?.Length ?? 0} result={result} effectsReady=false");
            });
        }

        private static bool IsTownReady(PlayerContext player)
            => SessionDirectory.IsTownPresence(player) && !player.DungeonSelectionPending
                && player.CurrentDungeonSelection == null;
    }
}
