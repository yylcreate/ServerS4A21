using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class GuardianAltarOpeningCoordinator
    {
        private async Task HandleExchangeSlotAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_EXCHANGE_SLOT)
                return;
            var characterId = session?.Player?.CharacterId ?? 0;
            var player = session?.Player;
            var userId = player?.UserId ?? 0;
            var account = session?.Account;
            var accountId = account?.AccountId ?? 0;
            var generation = session?.GameSession;
            var runGeneration = player?.CurrentDungeonRunGeneration ?? 0;
            var selectionGeneration = player?.CurrentDungeonSelectionGeneration ?? 0;
            bool IdentityCurrent() => IsCurrentGeneration(session, characterId, generation)
                && ReferenceEquals(session.Player, player) && player.UserId == userId
                && ReferenceEquals(session.Account, account) && account.AccountId == accountId
                && player.CurrentDungeonRunGeneration == runGeneration
                && player.CurrentDungeonSelectionGeneration == selectionGeneration;
            if (userId == 0 || accountId <= 0 || !IdentityCurrent())
                return;
            var valid = GuardianAltarExchangeRequestParser.TryParse(body, out var request);
            var revision = _service.Revision;
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                bool Allowed()
                {
                    bool Current() => IdentityCurrent() && _service.Revision == revision
                        && _service.GetSnapshot(_utcNow()).IsOpen && IsTownReady(session)
                        && player.Level >= GuardianAltarEntryRules.MinimumLevel;
                    return Current() && !hasActiveTrade(session)
                        && !mercenaryRestrictions.IsAssigned(characterId) && Current();
                }
                GuardianAltarExchangeResult result = null;
                var unavailable = false;
                try
                {
                    if (valid && Allowed())
                    {
                        if (_progress is not IGuardianAltarExchangeService exchanges)
                            unavailable = true;
                        else
                        {
                            result = exchanges.ExchangeSlots(characterId, accountId, request, Allowed);
                            if (result?.Progress?.CharacterId != characterId)
                                throw new InvalidOperationException("Guardian exchange result has no current character owner.");
                            GuardianAltarProgressValidator.Validate(result.Progress);
                            if (result.Accepted)
                            {
                                if (result.Progress.Header.RidableId != request.RidableId
                                    || !result.Progress.Slots.Contains(result.First)
                                    || !result.Progress.Slots.Contains(result.Second))
                                    throw new InvalidOperationException("Guardian exchange ACK disagrees with durable progress.");
                                _ = GuardianAltarExchangeAckBodyBuilder.Build(request, result.First, result.Second);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                    or InvalidDataException or IOException or OverflowException or ArgumentException)
                {
                    unavailable = true;
                    FileLogger.Log($"[GuardianAltarExchange] commit failed cid={characterId}: {ex}");
                }
                try
                {
                    var sendNotice = false;
                    await _sendBatch(session, () =>
                    {
                        if (!IdentityCurrent()) return Array.Empty<byte[]>();
                        var accepted = false;
                        try { accepted = valid && !unavailable && result?.Accepted == true && Allowed(); }
                        catch (SqliteException ex)
                        {
                            unavailable = true;
                            FileLogger.Log($"[GuardianAltarExchange] reply restriction failed cid={characterId}: {ex}");
                        }
                        if (!IdentityCurrent()) return Array.Empty<byte[]>();
                        var ack = GamePacketEnvelopeBuilder.Build(1,
                            (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_EXCHANGE_SLOT,
                            accepted ? GuardianAltarExchangeAckBodyBuilder.Build(request, result.First, result.Second)
                                : GuardianAltarExchangeAckBodyBuilder.BuildRejected());
                        sendNotice = !accepted && (unavailable || _service.Revision != revision);
                        return new[] { ack };
                    });
                    if (sendNotice)
                        await _sendBatch(session, () =>
                        {
                            // The first ACK write can outlive or displace its
                            // source. The optional notice needs its own prepare.
                            if (!IdentityCurrent()) return Array.Empty<byte[]>();
                            return new[] { GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                                ServerNoticeMessageBuilder.Build("守护者祭坛配置状态暂不可用，请重新进入活动。")) };
                        });
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[GuardianAltarExchange] reply failed cid={characterId}: {ex.Message}");
                }
                if (result != null)
                    FileLogger.Log($"[GuardianAltarExchange] EXCHANGE_SLOT cid={characterId} "
                        + $"first={request.First} second={request.Second} accepted={result.Accepted} "
                        + $"changed={result.Changed} reason={result.RejectionReason}");
            });
        }
    }
}
