using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    internal static class GuildRuntimeSelfTest
    {
        internal static async Task RunAsync(IGameDatabase database, Action<string, bool> check)
        {
            database.Write((c,t) =>
            {
                using var cmd=c.CreateCommand(); cmd.Transaction=t;
                cmd.CommandText="INSERT INTO characters(character_id,account_id,name) VALUES(201,100,'runtime-leader'),(202,100,'runtime-member'),(203,100,'runtime-outsider'),(204,100,'runtime-raid');";
                cmd.ExecuteNonQuery();
            });
            var repository=new GuildRepository(database);
            var guild=database.Write((c,t)=>GuildRepository.Insert(c,t,201,"runtime-guild",""));
            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            var sessions=new SessionDirectory();
            var transitions=new CharacterTransitionCoordinator(sessions);
            using var leader=await Peer.Create(database,sessions,201,10010);
            using var member=await Peer.Create(database,sessions,202,10011);
            using var outsider=await Peer.Create(database,sessions,203,10010);
            using var chat=new ChatHandler(sessions,new PartyManager(),transitions);
            chat.ConfigureGuilds(repository,transitions);
            var characters=new SqliteCharacterRepository(database);
            var appearance=new InventoryRefreshSender(new SqliteSelectCharacterDataSource(database,characters),characters,database);
            appearance.BindSessions(sessions);
            var publisher=new GuildStatePublisher(repository,transitions,sessions,appearance);
            var handler=new GuildManagementHandler(repository,transitions,publisher);
            var memberHandler=new GuildMemberHandler(repository,transitions,sessions);
            var historyHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.GUILD_CONTRIBUTE_HISTORY};
            await memberHandler.HandleContributionHistory(leader.Session,historyHeader,Array.Empty<byte>());
            var emptyHistory=leader.Drain().Single();
            check("guild history returns the native fifty-row empty array without inventing prior awards",
                emptyHistory[0]==1 && BitConverter.ToUInt16(emptyHistory,1)==(ushort)CmdPacketTypeA21.GUILD_CONTRIBUTE_HISTORY
                && emptyHistory.Length==15+1+50*44 && emptyHistory[15]==1 && emptyHistory.Skip(16).All(value=>value==0));
            await memberHandler.HandleContributionHistory(outsider.Session,historyHeader,Array.Empty<byte>());
            check("outsider cannot read another guild's contribution history", outsider.Drain().Single()[15]==0);
            await memberHandler.HandleContributionHistory(member.Session,historyHeader,BitConverter.GetBytes(guild.Id));
            check("history rejects a supplied guild identifier instead of crossing its current membership", member.Drain().Single()[15]==0);
            var historySource=Guid.NewGuid();
            var historyInstant=DateTimeOffset.FromUnixTimeSeconds(1791262800);
            var oneHistory=GuildContributionHistoryPacketBuilder.Build(new GuildContributionHistory(guild.Id,201,100,
                new[]{new GuildContributionHistoryRow(historySource,202,100,"公会成员",historyInstant,20)}));
            var historyName=DfoServer.Infrastructure.ClientTextEncoding.GetBytes("公会成员");
            check("history exposes the native Unix, NUL-terminated GBK name, committed amount and display slot fields",
                oneHistory.Length==15+1+2200 && oneHistory[15]==1 && BitConverter.ToUInt32(oneHistory,16)==1791262800
                && oneHistory.AsSpan(20,historyName.Length).SequenceEqual(historyName)
                && oneHistory.Skip(20+historyName.Length).Take(32-historyName.Length).All(value=>value==0)
                && BitConverter.ToInt32(oneHistory,52)==20 && BitConverter.ToUInt32(oneHistory,56)==8
                && oneHistory.Skip(60).All(value=>value==0));
            var originalUid=member.Session.Player.UserId;
            await QueuedHistoryAsync("UID replacement",()=>member.Session.Player.UserId++);
            member.Session.Player.UserId=originalUid;
            var originalAccount=member.Session.Account;
            await QueuedHistoryAsync("account reference replacement",()=>member.Session.Account=new AccountRecord{AccountId=100});
            member.Session.Account=originalAccount;
            var originalGameSession=member.Session.GameSession;
            await QueuedHistoryAsync("GameSession replacement",()=>member.Session.GameSession=new GameSession(member.Session,database,sessionDirectory:sessions));
            member.Session.GameSession=originalGameSession;
            await QueuedHistoryAsync("owned lease replacement",()=>InventoryContext.Register(member.Session.SessionId,new InventoryService(202,100,database)));
            await QueuedHistoryAsync("run generation change",()=>member.Session.Player.NextDungeonRunGeneration());
            using(var replacedClient=new TcpClient())
            {
                var replacedSession=new EnhancedClientSession(replacedClient,new GamePacketHeader());
                replacedSession.Player.CharacterId=202;
                await QueuedHistoryAsync("session directory replacement",()=>sessions.Register(202,replacedSession));
                sessions.Register(202,member.Session);
                replacedSession.Close();
            }
            await QueuedHistoryAsync("guild departure",()=>
            {
                if(repository.RemoveMember(202,202)!=GuildResult.Success) throw new InvalidOperationException("History member could not leave.");
            });
            database.Write((c,t)=>GuildRepository.AddMember(c,t,guild.Id,202));
            await QueuedHistoryAsync("failure ACK source replacement",()=>member.Session.Player.UserId++,new byte[]{0});
            member.Session.Player.UserId=originalUid;
            var message=new GamePacketWriter();message.WriteByte(6);message.WriteUInt16(0);message.WriteUInt32(0);message.WriteClientDstr("公会测试");
            await chat.Handle_SEND_MESSAGE(leader.Session,new GamePacketHeader(),message.ToArray());
            check("guild chat crosses channels and excludes outsiders", leader.Drain().Count==1 && member.Drain().Count==1 && outsider.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(outsider.Session,new GamePacketHeader(),message.ToArray());
            check("nonmember cannot send guild chat or echo", leader.Drain().Count==0 && member.Drain().Count==0 && outsider.Drain().Count==0);

            var memo=new GamePacketWriter();memo.WriteClientDstr("在线备注");
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.WRITE_GUILD_MEMBER_MEMO},memo.ToArray());
            check("own memo edit refreshes online guild members", repository.GetRosterForMember(202).Members.Single(m=>m.CharacterId==202).Memo=="在线备注"
                && leader.Drain().Any(p=>BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST)
                && member.Drain().Any(p=>BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST));
            var rank=new GamePacketWriter();rank.WriteClientDstr("runtime-member");rank.WriteByte(2);
            await handler.Handle(leader.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SET_SUB_GUILD_MASTER},rank.ToArray());
            check("rank handler updates online recipient permissions", repository.GetRosterForMember(202).Members.Single(m=>m.CharacterId==202).Rank==2
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_MEMBER_INFO && p[15]==2));
            leader.Drain();
            // Wait on an existing transition, replace the sender generation, then
            // ensure the queued command cannot mutate the new connection's state.
            var gate=await transitions.AcquireAsync(201);
            var staleEdit=handler.Handle(leader.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},memo.ToArray());
            var staleHistory=memberHandler.HandleContributionHistory(leader.Session,historyHeader,Array.Empty<byte>());
            using var replacement=await Peer.Create(database,sessions,201,GameNetworkConfig.NormalGamePort);
            gate.Dispose(); await staleEdit; await staleHistory;
            check("queued stale generation cannot edit or refresh replacement", repository.GetForMember(201).Announcement==""
                && replacement.Drain().Count==0 && leader.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(leader.Session,new GamePacketHeader(),message.ToArray());
            check("replaced sender cannot broadcast guild chat", member.Drain().Count==0 && replacement.Drain().Count==0);
            var channelHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL};
            await handler.Handle(member.Session,channelHeader,Array.Empty<byte>());
            var deniedChannel=member.Drain().Single();
            check("a persistent deputy title cannot change the leader-owned guild channel",
                repository.Get(guild.Id).RecommendedServerId==0 && deniedChannel[15]==0 && deniedChannel[16]==19);
            await handler.Handle(replacement.Session,channelHeader,BitConverter.GetBytes(200));
            check("guild channel rejects a client-supplied target instead of using it as a channel",
                repository.Get(guild.Id).RecommendedServerId==0 && replacement.Drain().Single()[15]==0);
            await handler.Handle(replacement.Session,channelHeader,Array.Empty<byte>());
            var channelPackets=replacement.Drain();
            var memberChannelPackets=member.Drain();
            check("setting the current real ordinary channel persists and publishes its native server/channel keys to the guild",
                new GuildRepository(database).Get(guild.Id).RecommendedServerId==GameNetworkConfig.ChannelServerIndex
                && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex
                && channelPackets.Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL && p[15]==1)
                && channelPackets.Concat(memberChannelPackets).Count(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO
                    && BitConverter.ToUInt32(p,p.Length-16)==GameNetworkConfig.ChannelServerIndex
                    && BitConverter.ToUInt32(p,p.Length-12)==GameNetworkConfig.NormalChannelIndex)==2);
            check("a mismatched account cannot modify the existing guild channel",
                repository.SetRecommendedChannel(201,101,GameNetworkConfig.ChannelServerIndex,10)==GuildResult.MissingCharacter
                && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex);
            using(var changedClient=new TcpClient())
            {
                var changedSession=new EnhancedClientSession(changedClient,new GamePacketHeader());
                changedSession.Player.CharacterId=201;
                await QueuedChannelAckAsync("directory replacement",()=>sessions.Register(201,changedSession));
                sessions.Register(201,replacement.Session);
                changedSession.Close();
            }
            await QueuedChannelAckAsync("owned lease replacement",()=>
                InventoryContext.Register(replacement.Session.SessionId,new InventoryService(201,100,database)));
            var channelGate=await transitions.AcquireAsync(201);
            Task oldGuildChannel;
            GuildRecord temporaryGuild;
            try
            {
                oldGuildChannel=handler.Handle(replacement.Session,channelHeader,Array.Empty<byte>());
                check("guild channel request freezes its original guild before waiting for transition gates",!oldGuildChannel.IsCompleted);
                temporaryGuild=database.Write((c,t)=>
                {
                    // Keep both guilds legal: a character cannot lead two guilds.
                    // The old guild must transfer leadership before its member moves.
                    using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText="UPDATE guilds SET leader_character_id=202 WHERE guild_id=@guild AND leader_character_id=201; "
                        + "DELETE FROM guild_members WHERE character_id=201;";
                    cmd.Parameters.AddWithValue("@guild",guild.Id);
                    cmd.ExecuteNonQuery();
                    return GuildRepository.Insert(c,t,201,"runtime-channel-other","");
                });
            }
            finally { channelGate.Dispose(); }
            await oldGuildChannel.WaitAsync(TimeSpan.FromSeconds(5));
            check("an old guild channel request cannot change a new guild with the same character account and owned lease",
                repository.Get(temporaryGuild.Id).RecommendedServerId==0 && replacement.Drain().Count==0 && member.Drain().Count==0);
            repository.Disband(201,out _);
            database.Write((c,t)=>
            {
                GuildRepository.AddMember(c,t,guild.Id,201);
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="UPDATE guilds SET leader_character_id=201 WHERE guild_id=@guild AND leader_character_id=202;";
                cmd.Parameters.AddWithValue("@guild",guild.Id);
                cmd.ExecuteNonQuery();
            });
            using(var changedClient=new TcpClient())
            {
                var changedSession=new EnhancedClientSession(changedClient,new GamePacketHeader());
                changedSession.Player.CharacterId=201;
                await QueuedPublisherAsync("directory replacement",()=>sessions.Register(201,changedSession));
                sessions.Register(201,replacement.Session);
                changedSession.Close();
            }
            await QueuedPublisherAsync("owned lease replacement",()=>
                InventoryContext.Register(replacement.Session.SessionId,new InventoryService(201,100,database)));
            await QueuedPublisherAsync("a newer committed channel",()=>{},false);
            await replacement.SendGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var blockedPublisher=publisher.RefreshAsync(new[]{201,202});
                check("guild refresh waits on the real receiver send lock",!blockedPublisher.IsCompleted);
                await blockedPublisher.WaitAsync(TimeSpan.FromSeconds(8));
                check("a best-effort send-lock timeout releases the recipient transition and still publishes the other current member",
                    replacement.Drain().Count==0 && HasCurrentChannel(member.Drain()));
            }
            finally { replacement.SendGate.Release(); }
            // Queue after the expired prepared sender so its late preparation
            // has finished before checking that it could not write an old frame.
            await replacement.Session.TrySendPacketAsync(Array.Empty<byte>(),CancellationToken.None,()=>false)
                .WaitAsync(TimeSpan.FromSeconds(5));
            check("timed-out guild refresh cannot write late after its send lock becomes available",replacement.Drain().Count==0);
            using (var raid=await Peer.Create(database,sessions,204,GameNetworkConfig.RaidGamePort))
            {
                await handler.Handle(raid.Session,channelHeader,Array.Empty<byte>());
                var rejectedChannel=raid.Drain().Single();
                check("a raid environment cannot be stored as the guild's ordinary recommended channel",
                    rejectedChannel[15]==0 && rejectedChannel[16]==7
                    && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex);
            }
            database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="CREATE TRIGGER reject_guild_channel_write BEFORE UPDATE OF recommended_channel_id ON guilds "
                    + "WHEN NEW.guild_id="+guild.Id+" BEGIN SELECT RAISE(ABORT,'guild-channel-write-fault'); END;";
                cmd.ExecuteNonQuery();
            });
            await handler.Handle(replacement.Session,channelHeader,Array.Empty<byte>());
            check("a failed guild channel transaction sends failure without broadcasting a successful projection",
                replacement.Drain().Single()[15]==0 && member.Drain().Count==0
                && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex);
            database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER reject_guild_channel_write;";cmd.ExecuteNonQuery();});
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE},new byte[4]);
            check("leave clears current identity and refreshes survivor", !repository.IsMember(202) && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_SECEDE_TO_USER)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.GUILD_MEMER_LIST));
            await chat.Handle_SEND_MESSAGE(member.Session,new GamePacketHeader(),message.ToArray());
            check("former member immediately loses guild chat", replacement.Drain().Count==0 && member.Drain().Count==0);

            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            await publisher.RefreshAsync(new[]{201,202}); replacement.Drain(); member.Drain();
            var kick=new GamePacketWriter();kick.WriteClientDstr("runtime-member");
            var kickHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE};
            await handler.Handle(outsider.Session,kickHeader,kick.ToArray());
            check("outsider cannot expel a named member", repository.IsMember(202)
                && outsider.Drain().Any(p=>p[0]==1 && p[15]==0));
            var leaderName=new GamePacketWriter();leaderName.WriteClientDstr("runtime-leader");
            await handler.Handle(member.Session,kickHeader,leaderName.ToArray());
            check("member cannot expel named leader", repository.IsMember(201)
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray().Concat(new byte[]{0}).ToArray());
            check("kick rejects trailing bytes without deleting membership", repository.IsMember(202)
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray());
            check("named kick clears online target and refreshes leader", !repository.IsMember(202)
                && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_SECEDE_TO_USER)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.REQ_GUILD_SECEDE && p[15]==1 && p[16]==2));
            await handler.Handle(replacement.Session,kickHeader,kick.ToArray());
            check("repeated named kick fails without affecting leader", repository.IsMember(201) && !repository.IsMember(202)
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0) && member.Drain().Count==0);
            await chat.Handle_SEND_MESSAGE(member.Session,new GamePacketHeader(),message.ToArray());
            check("expelled member immediately loses guild chat", replacement.Drain().Count==0 && member.Drain().Count==0);

            repository.Apply(guild.Id,202); repository.Approve(guild.Id,201,202);
            await publisher.RefreshAsync(new[]{201,202}); replacement.Drain(); member.Drain();
            repository.Apply(guild.Id,203,"申请留言");
            await handler.Handle(outsider.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.JOIN_GUILD_INFO},Array.Empty<byte>());
            check("pending application is restored from durable state", outsider.Drain().Any(p=>p[0]==0
                && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==1));
            await handler.Handle(outsider.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.CANCEL_JOIN_GUILD},BitConverter.GetBytes(guild.Id));
            check("cancel application clears applicant UI immediately", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));
            repository.Apply(guild.Id,203,"再次申请");
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.DENY_JOIN_GUILD},BitConverter.GetBytes(203));
            check("denial clears online applicant UI", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));
            replacement.Drain(); member.Drain();
            var announcement=new GamePacketWriter();announcement.WriteClientDstr("欢迎加入");
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},announcement.ToArray());
            check("announcement persists and refreshes online members", new GuildRepository(database).Get(guild.Id).Announcement=="欢迎加入"
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO));
            replacement.Drain();
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.NOTIFY_MESSAGE_TO_GUILD},memo.ToArray());
            check("member cannot overwrite announcement", repository.Get(guild.Id).Announcement=="欢迎加入"
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.MODIFY_GUILD_PROMOTE_MSG},announcement.ToArray());
            check("promotion update is visible in subsequent guild search", repository.FindByName("runtime-guild").Guild.Promotion=="欢迎加入");
            replacement.Drain(); member.Drain();
            var transfer=new GamePacketWriter();transfer.WriteClientDstr("runtime-member");transfer.WriteByte(1);
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.SET_SUB_GUILD_MASTER},transfer.ToArray());
            check("rank request cannot activate excluded leadership transfer", repository.Get(guild.Id).LeaderId==201
                && replacement.Drain().Any(p=>p[0]==1 && p[15]==0));
            repository.Apply(guild.Id,203,"等待审批");
            await handler.Handle(member.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.BREAK_GUILD},Array.Empty<byte>());
            check("member cannot disband guild", repository.Get(guild.Id)!=null && repository.GetPendingApplication(203)!=null
                && member.Drain().Any(p=>p[0]==1 && p[15]==0));
            // Fail the initiator's ACK and first broadcast target while leaving
            // its generation current: all other durable state still needs publishing.
            replacement.Session.Stream.Dispose();
            await handler.Handle(replacement.Session,new GamePacketHeader{type=(ushort)CmdPacketTypeA21.BREAK_GUILD},Array.Empty<byte>());
            check("disband survives initiator disconnect and clears member identity", repository.Get(guild.Id)==null
                && member.Session.Player.Subtype0Tail.GuildId==0
                && member.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_DISMISS));
            check("disband clears online pending applicants after cascade", repository.GetPendingApplication(203)==null
                && outsider.Drain().Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.JOIN_GUILD_INFO && p[15]==0));

            async Task QueuedHistoryAsync(string label,Action change,byte[] request=null)
            {
                var timeout=TimeSpan.FromSeconds(5);
                await member.SendGate.WaitAsync().WaitAsync(timeout);
                Task sending;
                try
                {
                    sending=memberHandler.HandleContributionHistory(member.Session,historyHeader,request??Array.Empty<byte>());
                    check("history is waiting on the real send lock before "+label,!sending.IsCompleted);
                    change();
                }
                finally { member.SendGate.Release(); }
                await sending.WaitAsync(timeout);
                check("queued history suppresses stale "+label+" without writing a frame",member.Drain().Count==0);
            }

            async Task QueuedChannelAckAsync(string label,Action change)
            {
                if(repository.SetRecommendedChannel(201,100,GameNetworkConfig.ChannelServerIndex,200)!=GuildResult.Success)
                    throw new InvalidOperationException("Could not arrange the previous guild channel.");
                var timeout=TimeSpan.FromSeconds(5);
                await replacement.SendGate.WaitAsync().WaitAsync(timeout);
                Task handling;
                try
                {
                    handling=handler.Handle(replacement.Session,channelHeader,Array.Empty<byte>());
                    check("guild channel has committed and its ACK waits on the real send lock before "+label,
                        !handling.IsCompleted && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex);
                    change();
                }
                finally { replacement.SendGate.Release(); }
                await handling.WaitAsync(timeout);
                var actorPackets=replacement.Drain();
                check("committed guild channel suppresses its expired ACK after "+label+" and still publishes the other member",
                    !actorPackets.Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.SET_GUILD_RECOMMAND_CHANNEL)
                    && HasCurrentChannel(member.Drain())
                    && repository.Get(guild.Id).RecommendedChannelId==GameNetworkConfig.NormalChannelIndex);
            }

            async Task QueuedPublisherAsync(string label,Action change,bool staleSource=true)
            {
                if(repository.SetRecommendedChannel(201,100,GameNetworkConfig.ChannelServerIndex,199)!=GuildResult.Success)
                    throw new InvalidOperationException("Could not arrange the queued guild channel snapshot.");
                var timeout=TimeSpan.FromSeconds(5);
                await replacement.SendGate.WaitAsync().WaitAsync(timeout);
                Task refreshing;
                try
                {
                    refreshing=publisher.RefreshAsync(new[]{201,202});
                    check("guild publisher is waiting on the real receiver send lock before "+label,!refreshing.IsCompleted);
                    if(repository.SetRecommendedChannel(201,100,GameNetworkConfig.ChannelServerIndex,GameNetworkConfig.NormalChannelIndex)!=GuildResult.Success)
                        throw new InvalidOperationException("Could not commit the latest guild channel.");
                    change();
                }
                finally { replacement.SendGate.Release(); }
                await refreshing.WaitAsync(timeout);
                var actorPackets=replacement.Drain();
                check(staleSource
                        ? "queued guild publisher skips its expired "+label+" and another current member receives the latest GINFO"
                        : "queued guild publisher reads "+label+" under the send lock for both current members",
                    (staleSource ? actorPackets.Count==0 : HasCurrentChannel(actorPackets)) && HasCurrentChannel(member.Drain()));
            }

            bool HasCurrentChannel(IEnumerable<byte[]> packets) => packets.Any(p=>p[0]==0
                && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO
                && BitConverter.ToUInt32(p,p.Length-16)==GameNetworkConfig.ChannelServerIndex
                && BitConverter.ToUInt32(p,p.Length-12)==GameNetworkConfig.NormalChannelIndex);
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }
            internal SemaphoreSlim SendGate { get; }
            private Peer(TcpClient reader, EnhancedClientSession session)
            {
                _reader=reader; Session=session;
                SendGate=(SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(session)
                    ?? throw new InvalidOperationException("The real session send lock was not found."));
            }
            internal static async Task<Peer> Create(IGameDatabase database,SessionDirectory sessions,int id,int port)
            {
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                var reader=new TcpClient{ReceiveTimeout=3000}; TcpClient writer;
                try
                {
                    var connecting=reader.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
                    writer=await listener.AcceptTcpClientAsync();await connecting;
                }
                finally { listener.Stop(); }
                var session=new EnhancedClientSession(writer,new GamePacketHeader(),port){Account=new AccountRecord{AccountId=100}};
                var record=new SqliteCharacterRepository(database).GetById(id);
                session.Player.CharacterId=id; session.Player.UserId=checked((ushort)id);session.Player.Name=record.Name;
                session.Player.Level=record.Level;session.Player.Job=record.Job;session.Player.GrowType=record.GrowType;
                session.Player.Subtype0Tail=new UserInfoMinimumTailSnapshot();
                session.GameSession=new GameSession(session,database,sessionDirectory:sessions);
                GuildIdentityProjection.Apply(new GuildRepository(database).GetForMember(id),session.Player.Subtype0Tail);
                InventoryContext.Register(session.SessionId,new InventoryService(id,100,database));
                sessions.Register(id,session);
                return new Peer(reader,session);
            }
            internal List<byte[]> Drain()
            {
                var packets=new List<byte[]>();var stream=_reader.GetStream();
                while(_reader.Client.Poll(20000,SelectMode.SelectRead))
                {
                    if(_reader.Available==0) throw new InvalidOperationException("guild test connection closed");
                    var header=new byte[15];stream.ReadExactly(header);int length=BitConverter.ToInt32(header,3);
                    if(length<15 || length>65536) throw new InvalidOperationException("invalid guild packet length");
                    var packet=new byte[length];header.CopyTo(packet,0);stream.ReadExactly(packet.AsSpan(15));packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { InventoryContext.Unregister(Session.SessionId);Session.Close();_reader.Dispose(); }
        }
    }
}
