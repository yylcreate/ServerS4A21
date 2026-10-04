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
        private async Task HandleAchievementRewardAsync(EnhancedClientSession session,
            GamePacketHeader header, byte[] body, Func<EnhancedClientSession, bool> hasActiveTrade,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            if (header.cmd != 1
                || header.type != (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD)
                return;
            var characterId = session?.Player?.CharacterId ?? 0;
            var userId = session?.Player?.UserId ?? 0;
            var account = session?.Account;
            var accountId = account?.AccountId ?? 0;
            var generation = session?.GameSession;
            bool IdentityCurrent() => IsCurrentGeneration(session, characterId, generation)
                && session.Player.UserId == userId && ReferenceEquals(session.Account, account)
                && session.Account?.AccountId == accountId;
            if (userId == 0 || accountId <= 0 || !IdentityCurrent())
                return;
            var valid = GuardianAltarAchievementRequestParser.TryParse(body, out var achievementType);
            var revision = _service.Revision;
            var runGeneration = session.Player.CurrentDungeonRunGeneration;
            var selectionGeneration = session.Player.CurrentDungeonSelectionGeneration;
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                bool Allowed()
                {
                    bool Current() => IdentityCurrent() && _service.Revision == revision
                        && _service.GetSnapshot(_utcNow()).IsOpen && IsTownReady(session)
                        && session.Player.Level >= GuardianAltarEntryRules.MinimumLevel
                        && session.Player.CurrentDungeonRunGeneration == runGeneration
                        && session.Player.CurrentDungeonSelectionGeneration == selectionGeneration;
                    return Current() && !hasActiveTrade(session)
                        && !mercenaryRestrictions.IsAssigned(characterId) && Current();
                }
                GuardianAltarAchievementResult result = null;
                var unavailable = false;
                try
                {
                    if (valid && Allowed() && _progress is IGuardianAltarAchievementService achievements)
                    {
                        // The progress owner atomically commits the claim and its
                        // system mails. No online inventory mutation or ticket refill.
                        result = achievements.ClaimAchievement(characterId, accountId, achievementType, Allowed);
                        if (result?.Progress?.CharacterId != characterId)
                            throw new InvalidOperationException("Guardian achievement reply has no current owner.");
                        GuardianAltarProgressValidator.Validate(result.Progress);
                        if (result.Accepted && !result.Progress.Achievements.Any(x =>
                                x.AchievementId == achievementType && x.Claimed == 1))
                            throw new InvalidOperationException("Guardian achievement reply disagrees with committed claim.");
                    }
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                    or InvalidDataException or IOException or OverflowException)
                {
                    unavailable = true;
                    FileLogger.Log($"[GuardianAltarAchievement] claim failed cid={characterId} type={achievementType}: {ex}");
                }
                try
                {
                    var successProjected = false;
                    await _sendBatch(session, () =>
                    {
                        if (!IdentityCurrent())
                            return Array.Empty<byte[]>();
                        var accepted = false;
                        try { accepted = valid && !unavailable && result?.Accepted == true && Allowed(); }
                        catch (SqliteException ex)
                        {
                            FileLogger.Log($"[GuardianAltarAchievement] reply restriction failed cid={characterId}: {ex}");
                        }
                        if (!IdentityCurrent())
                            return Array.Empty<byte[]>();
                        var ack = GamePacketEnvelopeBuilder.Build(1,
                            (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_GET_ACHIEVEMENT_REWARD,
                            accepted ? GuardianAltarAchievementAckBodyBuilder.BuildSuccess(achievementType)
                                : GuardianAltarAchievementAckBodyBuilder.BuildRejected());
                        successProjected = accepted;
                        return new[] { ack };
                    });
                    if (successProjected)
                    {
                        // The ACK only marks an existing client achievement row.
                        // A first claim inserts that row in the durable snapshot,
                        // so publish it through the existing full menu update.
                        await _sendBatch(session, () =>
                        {
                            if (!IdentityCurrent())
                                return Array.Empty<byte[]>();
                            try
                            {
                                if (!Allowed())
                                    return Array.Empty<byte[]>();
                            }
                            catch (SqliteException ex)
                            {
                                FileLogger.Log($"[GuardianAltarAchievement] menu update restriction failed cid={characterId}: {ex}");
                                return Array.Empty<byte[]>();
                            }
                            return new[] { GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE,
                                GuardianAltarMenuBodyBuilder.BuildUpdateRidable(result.Progress, 0)) };
                        });
                        await _sendBatch(session, () =>
                        {
                            if (!IdentityCurrent() || !IsTownReady(session)
                                || session.Player.CurrentDungeonRunGeneration != runGeneration
                                || session.Player.CurrentDungeonSelectionGeneration != selectionGeneration)
                                return Array.Empty<byte[]>();
                            // A retry of an already committed claim re-notifies mail,
                            // never creates a second delivery. Login also loads inbox.
                            return new[] { GamePacketEnvelopeBuilder.Build(0,
                                (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                                MailboxHandler.BuildMailboxAlarmNotification(1)) };
                        });
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    FileLogger.Log($"[GuardianAltarAchievement] reply failed cid={characterId}: {ex.Message}");
                }
                if (result != null)
                    FileLogger.Log($"[GuardianAltarAchievement] claim cid={characterId} type={achievementType} "
                        + $"accepted={result.Accepted} changed={result.Changed} reason={result.RejectionReason}");
            });
        }
    }
}
