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
        private async Task HandleSetSlotAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            if (header.cmd != 1 || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT)
                return;
            var characterId = session?.Player?.CharacterId ?? 0;
            var userId = session?.Player?.UserId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            var generation = session?.GameSession;
            bool IsIdentityCurrent() => IsCurrentGeneration(session, characterId, generation)
                && session.Player.UserId == userId && session.Account?.AccountId == accountId;
            if (userId == 0 || accountId <= 0 || !IsIdentityCurrent())
                return;
            var validRequest = GuardianAltarSlotRequestParser.TryParse(body, out var request);
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                var revision = _service.Revision;
                bool IsAllowed()
                {
                    if (!IsIdentityCurrent() || _service.Revision != revision
                        || !_service.GetSnapshot(_utcNow()).IsOpen || !IsTownReady(session)
                        || session.Player.Level < GuardianAltarEntryRules.MinimumLevel)
                        return false;
                    if (hasActiveTrade(session) || mercenaryRestrictions.IsAssigned(characterId))
                        return false;
                    // Restriction readers may outlive or displace this session.
                    return IsIdentityCurrent() && _service.Revision == revision
                        && _service.GetSnapshot(_utcNow()).IsOpen && IsTownReady(session)
                        && session.Player.Level >= GuardianAltarEntryRules.MinimumLevel;
                }

                GuardianAltarSlotResult committed = null;
                var unavailable = false;
                if (validRequest && IsIdentityCurrent())
                {
                    try
                    {
                        var allowed = IsAllowed();
                        if (allowed && _progress is IGuardianAltarSlotService slots)
                        {
                            // The existing progress transaction changes only slots.
                            // No ticket refill, purchase, Inventory or frozen run edit.
                            committed = slots.SetSlot(characterId, accountId, request, IsAllowed);
                            if (committed?.Progress?.CharacterId != characterId)
                                throw new InvalidOperationException("Guardian slot result has no current character owner.");
                            GuardianAltarProgressValidator.Validate(committed.Progress);
                            if (committed.Accepted && (committed.Progress.Header.RidableId != request.RidableId
                                || committed.Slot == null || committed.Slot.Locked != 0
                                || committed.Slot.SlotType != request.SlotType
                                || committed.Slot.SlotIndex != request.SlotIndex
                                || committed.Slot.ResourceId != (request.Equip ? request.ResourceId : 0)
                                || !committed.Progress.Slots.Contains(committed.Slot)))
                                throw new InvalidOperationException("Guardian slot ACK disagrees with durable progress.");
                        }
                        else if (allowed)
                            unavailable = true;
                    }
                    catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                        or InvalidDataException or IOException or OverflowException)
                    {
                        unavailable = true;
                        FileLogger.Log($"[GuardianAltarSlot] state/commit failed cid={characterId}: {ex}");
                    }
                }
                try
                {
                    await _sendBatch(session, () =>
                    {
                        if (!IsIdentityCurrent())
                            return Array.Empty<byte[]>();
                        var expired = _service.Revision != revision;
                        var canProject = false;
                        try
                        {
                            canProject = validRequest && !unavailable && committed?.Accepted == true && IsAllowed();
                        }
                        catch (SqliteException ex)
                        {
                            unavailable = true;
                            FileLogger.Log($"[GuardianAltarSlot] reply restriction read failed cid={characterId}: {ex}");
                        }
                        if (!IsIdentityCurrent())
                            return Array.Empty<byte[]>();
                        expired |= _service.Revision != revision;
                        if (canProject && !expired)
                            return new[] { GamePacketEnvelopeBuilder.Build(1,
                                (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT,
                                GuardianAltarSlotAckBodyBuilder.Build(request, committed.Slot)) };

                        var failure = GamePacketEnvelopeBuilder.Build(1,
                            (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT,
                            GuardianAltarSlotAckBodyBuilder.BuildRejected());
                        if (unavailable || expired)
                            return new[] { failure, GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                                ServerNoticeMessageBuilder.Build("守护者祭坛配置状态暂不可用，请重新进入活动。")) };
                        return new[] { failure };
                    });
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[GuardianAltarSlot] reply failed cid={characterId}: {ex.Message}");
                }
                if (committed != null)
                    FileLogger.Log($"[GuardianAltarSlot] SET_SLOT cid={characterId} equip={request.Equip} "
                        + $"type={request.SlotType} slot={request.SlotIndex} resource={request.ResourceId} "
                        + $"accepted={committed.Accepted} changed={committed.Changed} reason={committed.RejectionReason}");
            });
        }
    }
}
