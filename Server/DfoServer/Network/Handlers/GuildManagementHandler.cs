using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildManagementHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly GuildStatePublisher _publisher;
        internal GuildManagementHandler(GuildRepository repository, CharacterTransitionCoordinator transitions, GuildStatePublisher publisher)
        { _repository = repository; _transitions = transitions; _publisher = publisher; }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var command = (CmdPacketTypeA21)header.type;
            int actor = session.Player.CharacterId;
            if (actor <= 0) return;
            bool recommendedChannel = command == CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL;
            var originalPlayer = session.Player;
            var originalAccount = session.Account;
            var originalGameSession = session.GameSession;
            int accountId = originalAccount?.AccountId ?? 0;
            ushort userId = originalPlayer.UserId;
            long runGeneration = originalPlayer.CurrentDungeonRunGeneration;
            long selectionGeneration = originalPlayer.CurrentDungeonSelectionGeneration;
            InventoryLease originalLease = null;
            int originalGuildId = 0;
            if (recommendedChannel)
            {
                if (accountId <= 0 || userId == 0
                    || !InventoryContext.TryGetOwnedLease(session.SessionId, actor, out originalLease)
                    || originalLease.AccountId != accountId) return;
                originalGuildId = _repository.GetForMember(actor)?.Id ?? 0;
            }
            bool ChannelSourceCurrent() => ReferenceEquals(session.Player, originalPlayer)
                && ReferenceEquals(session.Account, originalAccount)
                && ReferenceEquals(session.GameSession, originalGameSession)
                && originalPlayer.CharacterId == actor && originalPlayer.UserId == userId
                && originalAccount.AccountId == accountId
                && originalPlayer.CurrentDungeonRunGeneration == runGeneration
                && originalPlayer.CurrentDungeonSelectionGeneration == selectionGeneration
                && InventoryContext.IsCurrentLease(originalLease, session.SessionId, actor)
                && _transitions.IsCurrent(session)
                && (_repository.GetForMember(actor)?.Id ?? 0) == originalGuildId;
            var refresh = new HashSet<int>();
            var gates = await AcquireAffectedAsync(actor);
            if (gates == null) return;
            try
            {
                if (!_transitions.IsCurrent(session)) return;
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, actor, out var lease)) return;
                if (recommendedChannel && !ChannelSourceCurrent()) return;
                if (command == CmdPacketTypeA21.JOIN_GUILD_INFO)
                {
                    if (body == null || body.Length == 0)
                        await session.SendPacketAsync(GuildManagementPacketBuilder.Pending(_repository.GetPendingApplication(actor)));
                    return;
                }
                var roster = _repository.GetRosterForMember(actor);
                var guild = roster?.Guild;
                GuildResult result = GuildResult.NotFound;
                byte[] success = GuildCreationPacketBuilder.Ack(command);
                int target = 0;
                try
                {
                    switch (command)
                    {
                        case CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL:
                            // The captured request is empty. 019EC520's confirmation
                            // says to select the sender's current channel, not a client ID.
                            if (body == null || body.Length == 0)
                            {
                                if (!ChannelSourceCurrent()) return;
                                result = GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel)
                                    && channel.ChannelType == GameNetworkConfig.GeneralChannelEnvironment
                                    ? _repository.SetRecommendedChannel(actor, lease.AccountId,
                                        GameNetworkConfig.ChannelServerIndex, channel.ChannelId)
                                    : GuildResult.InvalidChannel;
                            }
                            break;
                        case CmdPacketTypeA21.CANCEL_JOIN_GUILD:
                            if (TryId(body, out int guildId) && guildId > 0)
                                result = _repository.CancelApplication(actor, guildId);
                            break;
                        case CmdPacketTypeA21.DENY_JOIN_GUILD:
                            if (TryId(body, out target) && target > 0)
                                result = _repository.DenyApplication(actor, target);
                            break;
                        case CmdPacketTypeA21.REQ_GUILD_SECEDE:
                            // A21 captures use one name DSTR. Empty means self;
                            // a nonempty name selects a member of this guild only.
                            if (GuildTextRequest.TryParse(body, 62, out _, out var removedName))
                            {
                                var member = roster?.Members.FirstOrDefault(m => removedName.Length == 0
                                    ? m.CharacterId == actor : m.Name == removedName);
                                if (member != null)
                                {
                                    target = member.CharacterId;
                                    result = _repository.RemoveMember(actor, target);
                                    success = GuildManagementPacketBuilder.Left(member.Name, guild.Name, target != actor);
                                }
                            }
                            break;
                        case CmdPacketTypeA21.SET_SUB_GUILD_MASTER:
                            if (body?.Length >= 5 && body[^1] >= 2 && body[^1] <= 5
                                && GuildTextRequest.TryParse(body[..^1], 62, out _, out var name))
                            {
                                var member = roster?.Members.FirstOrDefault(m => m.Name == name);
                                if (member != null)
                                {
                                    target = member.CharacterId;
                                    result = _repository.ChangeRank(actor, target, body[^1]);
                                    success = GuildManagementPacketBuilder.RankChanged(member.Name, body[^1]);
                                }
                            }
                            break;
                        case CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD:
                        case CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG:
                        case CmdPacketTypeA21.WRITE_GUILD_MEMBER_MEMO:
                            var field = command == CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD ? GuildTextField.Announcement
                                : command == CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG ? GuildTextField.Promotion : GuildTextField.Memo;
                            if (GuildTextRequest.TryParse(body, GuildManagementRules.MaximumBytes(field), out _, out var text))
                                result = _repository.EditText(actor, field, text);
                            break;
                        case CmdPacketTypeA21.BREAK_GUILD:
                            if (body == null || body.Length == 0)
                            {
                                result = _repository.Disband(actor, out var affected);
                                if (result == GuildResult.Success) refresh.UnionWith(affected);
                            }
                            break;
                    }
                }
                catch (SqliteException ex)
                {
                    FileLogger.Log($"[Guild] management persistence failed cid={actor} command={command} code={ex.SqliteErrorCode}");
                    result = GuildResult.PersistenceFailed;
                }
                // A disconnected initiator must not prevent committed state
                // from being published to the other online members/applicants.
                var ack = result == GuildResult.Success ? success : GuildCreationPacketBuilder.Ack(command,
                        command == CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL
                            ? (result == GuildResult.InvalidChannel ? (byte)7 : (byte)19) : (byte)0x22);
                await SessionDirectory.TrySendBestEffortAsync(async ct =>
                    {
                        if (recommendedChannel)
                            await session.TrySendPacketAsync(ack, ct, ChannelSourceCurrent);
                        else
                            await session.SendPacketAsync(ack, ct);
                    },
                    $"guild management ack cid={actor}");
                if (result == GuildResult.Success)
                {
                    refresh.Add(actor);
                    if (target > 0) refresh.Add(target);
                    if (roster != null) foreach (var member in roster.Members) refresh.Add(member.CharacterId);
                }
                FileLogger.Log($"[Guild] management cid={actor} command={command} target={target} result={result}");
            }
            finally { for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); }
            await _publisher.RefreshAsync(refresh.Where(id => id > 0), command == CmdPacketTypeA21.BREAK_GUILD);
        }

        private async Task<List<IDisposable>> AcquireAffectedAsync(int actor)
        {
            // Joining takes leader+applicant in the same ascending order. If a
            // join committed while we were waiting, release and include that member.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var guild = _repository.GetForMember(actor);
                var ids = new HashSet<int>(guild == null ? Array.Empty<int>() : _repository.GetMemberIds(guild.Id));
                ids.Add(actor);
                var gates = new List<IDisposable>();
                bool retained = false;
                try
                {
                    foreach (int id in ids.OrderBy(x => x)) gates.Add(await _transitions.AcquireAsync(id));
                    var current = _repository.GetForMember(actor);
                    if (current == null || _repository.GetMemberIds(current.Id).All(ids.Contains))
                    { retained = true; return gates; }
                }
                finally { if (!retained) for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); }
            }
            return null;
        }

        internal static bool TryId(byte[] body, out int id)
        { id = body?.Length == 4 ? BitConverter.ToInt32(body, 0) : 0; return body?.Length == 4; }
    }
}
