using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Buffs;
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
            var handler=new GuildManagementHandler(repository,transitions,publisher,appearance);
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
            // ---- 公会捐献 DONATE_GUILD_FUND (CMD 0x015A, body = u32 小端金额) ----
            // 换算规则: 金额必须是 PVF etc/(r)guild.etc [guild donate] 的整数倍;
            // 金币按全额扣除, 公会积分按 金额/汇率 累加。
            const uint donateUnit=1000;
            var donateHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND};
            check("PVF guild donate unit matches the in-game rate",
                GuildDonationService.ReadDonationUnit()==(int)donateUnit);
            check("donation parser accepts captured amounts and rejects zero/wrong length",
                !GuildManagementHandler.TryDonationAmount(new byte[4],out var zero) && zero==0
                && !GuildManagementHandler.TryDonationAmount(Array.Empty<byte>(),out _)
                && !GuildManagementHandler.TryDonationAmount(new byte[]{1,0,0,0,0},out _)
                && !GuildManagementHandler.TryDonationAmount(null,out _)
                && GuildManagementHandler.TryDonationAmount(Convert.FromHexString("40420F00"),out var oneMillion) && oneMillion==1_000_000u
                && GuildManagementHandler.TryDonationAmount(Convert.FromHexString("80969800"),out var tenMillion) && tenMillion==10_000_000u);
            InventoryContext.TryGetOwnedLease(replacement.Session.SessionId,201,out var leaderLease);
            leaderLease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart,0,5_000_000);
            replacement.Drain(); member.Drain(); outsider.Drain();
            await QueuedContentBeforeGateAsync(donateHeader, BitConverter.GetBytes(donateUnit),
                () => replacement.Session.Player.NextDungeonRunGeneration());
            // 非整数倍(1500)先被换算规则拒绝, 金币与积分都必须原样不动。
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(1500u));
            check("non-multiple donation is rejected without touching points or gold",
                repository.Get(guild.Id).Points==0
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==5_000_000
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND && p[15]==0));
            // 用整数倍投递, 这样命中失败的是成员校验而不是换算规则。
            await handler.Handle(outsider.Session,donateHeader,BitConverter.GetBytes(donateUnit));
            check("nonmember donation is rejected without touching points or gold",
                repository.Get(guild.Id).Points==0
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==5_000_000
                && outsider.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND && p[15]==0));
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(6_000_000u));
            check("donation above held gold is rejected and changes nothing",
                repository.Get(guild.Id).Points==0
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==5_000_000
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND && p[15]==0));
            await handler.Handle(replacement.Session,donateHeader,new byte[4]);
            await handler.Handle(replacement.Session,donateHeader,new byte[3]);
            check("zero and truncated donation bodies are rejected",
                repository.Get(guild.Id).Points==0
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==5_000_000
                && replacement.Drain().Count(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND && p[15]==0)==2);
            replacement.Drain(); member.Drain();
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(1_000_000u));
            var donorPackets=replacement.Drain();
            var memberPackets=member.Drain();
            // 捐 1,000,000 金币: 全额扣币, 积分 = 1,000,000 / 1000 = 1000。
            check("successful donation charges full gold and stores converted points",
                repository.Get(guild.Id).Points==1_000_000u/donateUnit
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==4_000_000
                && donorPackets.Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.DONATE_GUILD_FUND && p[15]==1));
            // 积分必须落在探针实测的那一格: 包体偏移 223..226, 即相对包尾 end-42..end-39。
            // 注意这里断言的是换算后的积分, 不是原始金币数。
            check("guild info broadcast carries the converted points at the probed offset",
                memberPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO
                    && p.Length>=42+4 && BitConverter.ToUInt32(p,p.Length-42)==1_000_000u/donateUnit));
            replacement.Drain(); member.Drain();
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(2_500_000u));
            check("second donation accumulates points and charges gold again",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit
                && leaderLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==1_500_000);
            // 租约过期: 注销在线背包后必须被拒, 且资产不变。
            InventoryContext.Unregister(replacement.Session.SessionId);
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(donateUnit));
            check("stale inventory lease cannot donate", repository.Get(guild.Id).Points==3_500_000u/donateUnit);
            InventoryContext.Register(replacement.Session.SessionId,new InventoryService(201,100,database));
            replacement.Drain(); member.Drain();
            // ---- 购买工会内容 BUY_GUILD_CONTENTS (CMD 0x02F8, body = u32 内容行号) ----
            // 行号来自客户端抓包(买"(1天)工会属性值"发 02 00 00 00), 而类别码/天数/
            // 价格只认 PVF etc/(r)guild.etc。行号与类别码是两回事: 行 2 的类别码是 3。
            var buyHeader=new GamePacketHeader{type=(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS};
            await QueuedContentBeforeGateAsync(buyHeader, BitConverter.GetBytes(2u),
                () => { replacement.Session.Player.BeginDungeonSelection(default); replacement.Session.Player.ClearDungeonSelection(); });
            check("content row parser keeps the captured body and rejects malformed ones",
                GuildManagementHandler.TryContentRowNumber(Convert.FromHexString("02000000"),out var capturedRow) && capturedRow==2
                && !GuildManagementHandler.TryContentRowNumber(new byte[4],out var emptyRow) && emptyRow==0
                && !GuildManagementHandler.TryContentRowNumber(Array.Empty<byte>(),out _)
                && !GuildManagementHandler.TryContentRowNumber(new byte[]{2,0,0,0,0},out _)
                && !GuildManagementHandler.TryContentRowNumber(null,out _));
            static bool Throws(Action action)
            {
                try { action(); return false; }
                catch (Exception exception) when (exception is InvalidOperationException || exception is InvalidDataException)
                { return true; }
            }
            check("content catalog rejects malformed PVF text instead of defaulting",
                Throws(()=>GuildContentCatalog.Parse(""))
                && Throws(()=>GuildContentCatalog.Parse("[guild contents list]\n1 5 169 -1 0 10150602 0 36000\n"))
                && Throws(()=>GuildContentCatalog.Parse("[guild contents list]\n1 5 169 -1 0 10150602 0\n[/guild contents list]\n[additional guild contents list]\n22 20 -1 0 0 490001375 0 10000000 1\n[/additional guild contents list]\n"))
                && Throws(()=>GuildContentCatalog.Parse("[guild contents list]\n1 5 169 -1 0 10150602 0 36000\n[/guild contents list]\n[additional guild contents list]\n22 20 -1 0 0 490001375 0 10000000 1 23 21 300 1 0 490001373 0 3000000\n[/additional guild contents list]\n")));
            var synthetic=GuildContentCatalog.Parse("[guild contents list]\n1 5 169 -1 0 10150602 0 36000 `hot`\n[/guild contents list]\n[additional guild contents list]\n22 20 -1 0 0 490001375 0 10000000 `` 1\n[/additional guild contents list]\n");
            check("content catalog reads the additional resource row and preserves its category",
                synthetic.Count==2 && synthetic[1] is {Category:5,Price:36_000u,IsPermanent:true}
                && synthetic[22] is {Category:20,Price:10_000_000u,IsPermanent:true});
            check("PVF guild content catalog parses 16+3 rows and maps row number to category",
                GuildContentCatalog.All.Count==19
                && GuildContentCatalog.Resolve(2) is {Category:3,Days:1,Parameter:167,Price:200u,CostsGold:false}
                && GuildContentCatalog.Resolve(23) is {Category:21,Days:1,Price:3_000_000u,CostsGold:false}
                && GuildContentCatalog.Resolve(22) is {Category:20,Price:10_000_000u,CostsGold:true,IsPermanent:true}
                && GuildContentCatalog.Resolve(1) is {Category:5,Price:36_000u,IsPermanent:true}
                && GuildContentCatalog.Resolve(0)==null && GuildContentCatalog.Resolve(20)==null);
            replacement.Drain(); member.Drain(); outsider.Drain();
            // 购买位是 GUILD_INFO 成员权限的 bit 8, 实机二分确认只有会长拿到该位。
            await handler.Handle(member.Session,buyHeader,BitConverter.GetBytes(2u));
            check("non-leader cannot buy guild content and points stay untouched",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit
                && member.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[15]==0 && p[16]==0x22));
            await handler.Handle(outsider.Session,buyHeader,BitConverter.GetBytes(2u));
            check("nonmember cannot buy guild content",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit
                && outsider.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[16]==0x22));
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(9999u));
            await handler.Handle(replacement.Session,buyHeader,new byte[4]);
            await handler.Handle(replacement.Session,buyHeader,new byte[3]);
            check("unknown row, zero row and truncated bodies are rejected without charging points",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit
                && repository.GetRosterForMember(201).PurchasedContents.Count==0
                && replacement.Drain().Count(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[16]==0x22)==3);
            replacement.Drain(); member.Drain();
            long purchaseStart=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(2u));
            var buyerPackets=replacement.Drain();
            var purchaseMemberPackets=member.Drain();
            var guildInfoPackets=purchaseMemberPackets.Where(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO).ToList();
            var bought=repository.GetRosterForMember(201).PurchasedContents;
            check("buying a timed content charges guild points and stores one record",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit-200
                && bought.Count==1 && bought[0].Category==3
                && bought[0].ExpiresAt>=(uint)(purchaseStart+86400-5) && bought[0].ExpiresAt<=(uint)(purchaseStart+86400+300)
                && buyerPackets.Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[15]==1));
            // 已购内容条目紧跟 1 字节计数之后: 计数在 end-58, 条目 10 字节从 end-57
            // 开始(类别码 1B / 到期 4B / 保留 1B / 保留 4B), 之后是 u32、联盟计数、
            // 公会积分(end-42, 实机探针锚点)。按包尾定位, 因为公会名和公告长度可变。
            check("guild info carries the purchased entry directly after its count byte",
                guildInfoPackets.Count==1 && guildInfoPackets[0].Length>=58
                && guildInfoPackets[0][guildInfoPackets[0].Length-58]==1
                && guildInfoPackets[0][guildInfoPackets[0].Length-57]==3
                && BitConverter.ToUInt32(guildInfoPackets[0],guildInfoPackets[0].Length-56)==bought[0].ExpiresAt
                && guildInfoPackets[0][guildInfoPackets[0].Length-52]==0
                && BitConverter.ToUInt32(guildInfoPackets[0],guildInfoPackets[0].Length-51)==0
                && BitConverter.ToUInt32(guildInfoPackets[0],guildInfoPackets[0].Length-42)==3_500_000u/donateUnit-200);
            // 同时给在线成员下发 CHARACTER_ADD_BUFF: u8 count=1 + i32 buffId + 三个 i32
            // 状态字段(共 17B, 前面 15B 是 A21 发送头)。buffId 取 PVF 参数列, 不是
            // 服务端自己算的数值; 状态字段照抄抓包样本 1/0/0。
            check("buying guild attribute content pushes CHARACTER_ADD_BUFF to online members",
                purchaseMemberPackets.Any(p=>p[0]==0
                    && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF
                    && p.Length==15+17 && p[15]==1
                    && BitConverter.ToInt32(p,16)==167
                    && BitConverter.ToInt32(p,20)==1
                    && BitConverter.ToInt32(p,24)==0
                    && BitConverter.ToInt32(p,28)==0));
            var staleBuffTimer = ClockService.Instance.ScheduleOneShot(
                "guild-stat-buff:" + leader.Session.SessionId + ":201", DateTime.UtcNow.AddDays(1), _ => { });
            CharacterSessionLifecycleCoordinator.EnterCharacterSelectionState(leader.Session);
            check("leaving a stale session cancels its guild reminder without cancelling the replacement session",
                !staleBuffTimer.Cancel()
                && ClockService.Instance.CancelOneShot("guild-stat-buff:" + replacement.Session.SessionId + ":201"));
            GuildStatBuffNotifier.ScheduleForCharacter(replacement.Session, database, 201, transitions);
            await member.SendGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Task queuedContentProjection;
            try
            {
                queuedContentProjection = publisher.RefreshAsync(new[] {202});
                check("content projection waits on the existing session send lock", !queuedContentProjection.IsCompleted);
                database.Write((c,t)=>GuildRepository.UpsertPurchasedContent(c,t,guild.Id,5,-1));
            }
            finally { member.SendGate.Release(); }
            await queuedContentProjection.WaitAsync(TimeSpan.FromSeconds(5));
            var latestContentPackets = member.Drain();
            check("queued guild projection rereads the newest purchased contents and matching attribute buff",
                latestContentPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO
                    && p[p.Length-68]==2)
                && latestContentPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF
                    && BitConverter.ToInt32(p,16)==169)
                && !latestContentPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF
                    && BitConverter.ToInt32(p,16)==167));
            database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand(); cmd.Transaction=t;
                cmd.CommandText="DELETE FROM guild_contents WHERE guild_id=@guild AND category=5;";
                cmd.Parameters.AddWithValue("@guild",guild.Id); cmd.ExecuteNonQuery();
            });
            // 计数制内容(类别码 7 组队传送)的两个尾字段: +5 标志写 1、+6 写剩余次数。
            // 条目 +0 在 end-57, 所以 +5 在 end-52、+6 在 end-51。这个映射来自字段宽度
            // 推定(PVF 参数是 10/100/1000, u8 装不下, 次数只可能在 u32 那格), 实机待确认。
            var countRoster=repository.GetRosterForMember(201) with {
                PurchasedContents=new[]{ new GuildPurchasedContent(7,GuildContentDefinition.PermanentExpiryUnix,10) } };
            var countInfo=Network.Builders.Guilds.GuildInfoPacketBuilder.Build(countRoster);
            check("count-based guild content carries its remaining uses in the entry tail",
                countInfo.Length>=58
                && countInfo[countInfo.Length-58]==1
                && countInfo[countInfo.Length-57]==7
                && BitConverter.ToUInt32(countInfo,countInfo.Length-56)==GuildContentDefinition.PermanentExpiryUnix
                && countInfo[countInfo.Length-52]==1
                && BitConverter.ToUInt32(countInfo,countInfo.Length-51)==10);
            // 组队传送扣一次之后的刷新链路: 只改库不会让客户端购买内容栏变, 必须重发
            // GUILD_INFO(实机复现: 不推就得重开公会窗口)。这里验证扣减 + 推送之后
            // 报文里带的是新次数 —— 条目 +0 在 end-57+i*10, +6 是剩余次数。
            database.Write((c,t)=>GuildRepository.UpsertPurchasedContent(c,t,guild.Id,7,-1,10));
            member.Drain();
            var consumed=GuildRepository.ConsumeContentUse(database,202,7);
            await publisher.RefreshAsync(new[]{202});
            var refreshedInfo=member.Drain()
                .Where(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_INFO).ToList();
            // 条目区之后是固定 47 字节(4B 保留 + 1B 联盟计数 + 10B 积分区 + 32B 尾数组),
            // 所以 1 字节的条目计数在 end-48-10*N —— N=1 时就是 end-58(既有断言用的那个)。
            var refreshedRows=repository.GetRosterForMember(202).PurchasedContents;
            var sawNewCount=false;
            var countMatches=false;
            if(refreshedInfo.Count==1 && refreshedInfo[0].Length>=48+10*refreshedRows.Count){
                int countAt=refreshedInfo[0].Length-48-10*refreshedRows.Count;
                countMatches=refreshedInfo[0][countAt]==refreshedRows.Count;
                for(int i=0;i<refreshedRows.Count;i++){
                    int at=countAt+1+i*10;
                    if(refreshedInfo[0][at]==7 && BitConverter.ToUInt32(refreshedInfo[0],at+6)==9)
                        sawNewCount=true;
                }
            }
            check("consuming a guild content use shows up in the republished guild info",
                consumed==9 && countMatches && sawNewCount);
            // 收尾: 清掉这条只为本条断言插入的记录, 不干扰后面的已购内容计数。
            database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="DELETE FROM guild_contents WHERE guild_id=@guild AND category=7;";
                cmd.Parameters.AddWithValue("@guild",guild.Id);
                cmd.ExecuteNonQuery();
            });
            replacement.Drain(); member.Drain();
            // 行 16(类别码 6, 永久, 100000 积分)买不起: 拒绝且不写任何记录。
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(16u));
            check("purchase above held points is rejected and writes nothing",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit-200
                && repository.GetRosterForMember(201).PurchasedContents.All(x=>x.Category!=6)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[15]==0 && p[16]==0x22));
            replacement.Drain(); member.Drain();
            // 到期值由 SQLite 的 unixepoch('now') 按秒算, 同一秒内买两次会得到
            // 相同的到期值; 隔开一秒才能验证"取较晚到期"真的生效。
            await Task.Delay(1100);
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(2u));
            var repeated=repository.GetRosterForMember(201).PurchasedContents;
            check("repeat purchase charges again and keeps one row per category",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit-400
                && repeated.Count==1 && repeated[0].Category==3);
            check("repeat purchase extends the expiry to the later purchase",
                repeated[0].ExpiresAt>bought[0].ExpiresAt);
            // 永久内容需要 5000 积分(行 12, 类别码 4), 先补一笔捐献。
            InventoryContext.TryGetOwnedLease(replacement.Session.SessionId,201,out var buyerLease);
            buyerLease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart,0,12_000_000);
            replacement.Drain(); member.Drain();
            await handler.Handle(replacement.Session,donateHeader,BitConverter.GetBytes(2_000_000u));
            check("donation tops the guild up for the permanent content purchase",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit-400+2_000
                && buyerLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==10_000_000);
            replacement.Drain(); member.Drain();
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(12u));
            check("permanent content stores the non-expiring sentinel instead of an already expired one",
                repository.Get(guild.Id).Points==3_500_000u/donateUnit-400+2_000-5_000
                && GuildContentCatalog.Resolve(12) is {Category:4,IsPermanent:true,Days:-1}
                && repository.GetRosterForMember(201).PurchasedContents
                    .Any(x=>x.Category==4 && x.ExpiresAt==GuildContentDefinition.PermanentExpiryUnix));
            replacement.Drain(); member.Drain();
            // 改名卡(行 22, 类别码 20)扣的是游戏币而不是公会积分。
            uint pointsBeforeGold=repository.Get(guild.Id).Points;
            var goldBefore=buyerLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count;
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(22u));
            check("rename card charges game gold and leaves guild points alone",
                repository.Get(guild.Id).Points==pointsBeforeGold
                && goldBefore==10_000_000
                && buyerLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==goldBefore-10_000_000
                && repository.GetRosterForMember(201).PurchasedContents
                    .Any(x=>x.Category==20 && x.ExpiresAt==GuildContentDefinition.PermanentExpiryUnix)
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[15]==1));
            replacement.Drain();
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(22u));
            check("rename card above held gold is rejected and keeps the earlier record",
                buyerLease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count==0
                && repository.GetRosterForMember(201).PurchasedContents.Count(x=>x.Category==20)==1
                && replacement.Drain().Any(p=>p[0]==1 && BitConverter.ToUInt16(p,1)==(ushort)CmdPacketTypeA21.BUY_GUILD_CONTENTS && p[15]==0 && p[16]==0x22));
            // 公会属性内容(类别码 3/5)的参数列是 etc/variousbufflist.etc 的增益索引,
            // 不是数值: 167 = `Guild_Status`(限时公会属性), 169 = `Guild_Status1`
            // (永久公会属性), 两者效果都是 physical_attack / physical_defence /
            // magical_attack / magical_defence 各 +60 —— 即道具说明里的"力量、体力、
            // 智力、精神增加60点"。服务端不复制这个 60, 只负责决定下发哪个索引;
            // 索引在客户端查不到时 ADD_BUFF 会静默无反应, 所以这里把"PVF 里确实存在
            // 这两条增益"钉成自测契约。
            var nowUnix=(uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var activeStatBuff=GuildStatBuffRule.Resolve(repository.GetRosterForMember(201).PurchasedContents,nowUnix);
            var timedBuff=VariousBuffCatalog.Resolve(167);
            var permanentBuff=VariousBuffCatalog.Resolve(169);
            const string syntheticBuff = "[index]\n167\n[system group]\n`Guild_Status`\n[effect]\n[name]\n`physical_attack`\n[param]\n60 -1 0\n[/param]\n[/effect]\n[/index]\n";
            var parsedBuff = VariousBuffCatalog.Parse(syntheticBuff)[167];
            var tooltipBuff = VariousBuffCatalog.Parse(syntheticBuff.Replace("[effect]",
                "[tooltip text]\n`description\n[effect]\n[not a tag]\nmore description`\n[effect]"))[167];
            check("buff definitions use the PvfLib script model with signed parameters and reject duplicates",
                parsedBuff.SystemGroup == "Guild_Status" && parsedBuff.Effects.Single().Parameters.SequenceEqual(new[] {60,-1,0})
                && tooltipBuff.Effects.Single().Parameters.SequenceEqual(new[] {60,-1,0})
                && Throws(() => VariousBuffCatalog.Parse(syntheticBuff + syntheticBuff))
                && Throws(() => VariousBuffCatalog.Parse(syntheticBuff.Replace("60 -1 0", "60 invalid"))));
            check("current buff resource's immediate redundant index close is accepted without ignoring orphan closes",
                VariousBuffCatalog.Parse(syntheticBuff + "\n[/index]\n")[167].Effects.Count == 1
                && Throws(() => VariousBuffCatalog.Parse("[/index]\n" + syntheticBuff))
                && Throws(() => VariousBuffCatalog.Parse(syntheticBuff + "\n0\n[/index]\n"))
                && Throws(() => VariousBuffCatalog.Parse(syntheticBuff + "\n[unexpected]\n[/index]\n"))
                && Throws(() => VariousBuffCatalog.Parse(syntheticBuff + "\n[/index]\n[/index]\n"))
                && Throws(() => VariousBuffCatalog.Parse("[index]\n10\n" + syntheticBuff + "[/index]\n")));
            const string certificateBuff = "[index]\n1151\n[system group]\n`Guild_Dungeon_Event`\n[effect]\n[name]\n`guild_certificate`\n[param]\n10154439\n[/param]\n[param]\n1\n[/param]\n[/effect]\n[/index]\n";
            var certificateEffect = VariousBuffCatalog.Parse(certificateBuff)[1151].Effects.Single();
            var malformedCertificate = PvfLib.VariousBuffConfigFile.Parse(certificateBuff.Replace("guild_certificate", "physical_attack"));
            check("the current guild_certificate resource preserves two ordered scalar blocks without flattening other effects",
                certificateEffect.Name == "guild_certificate" && certificateEffect.Parameters.Count == 0
                && certificateEffect.ParameterBlocks.Count == 2
                && certificateEffect.ParameterBlocks[0].SequenceEqual(new[] {10154439})
                && certificateEffect.ParameterBlocks[1].SequenceEqual(new[] {1})
                && VariousBuffCatalog.Resolve(1151).Effects.Single(effect=>effect.Name=="guild_certificate").ParameterBlocks.Count == 2
                && !malformedCertificate.IsValid && malformedCertificate.InvalidReason.Contains("Buff index 1151 effect at line 5")
                && Throws(() => VariousBuffCatalog.Parse(certificateBuff.Replace("[param]\n1\n[/param]\n", "")))
                && Throws(() => VariousBuffCatalog.Parse(certificateBuff.Replace("[param]\n1\n", "[param]\n1 2\n")))
                && Throws(() => VariousBuffCatalog.Parse(certificateBuff.Replace("[param]\n1\n", "[param]\n0\n")))
                && Throws(() => VariousBuffCatalog.Parse(certificateBuff.Replace("[name]\n`guild_certificate`\n", "")))
                && Throws(() => VariousBuffCatalog.Parse(certificateBuff.Replace("[name]\n`guild_certificate`\n", "[name]\n``\n"))));
            check("guild attribute categories map to the PVF buff indices 167 and 169",
                GuildStatBuffRule.IsStatCategory(3) && GuildStatBuffRule.IsStatCategory(5)
                && !GuildStatBuffRule.IsStatCategory(4) && !GuildStatBuffRule.IsStatCategory(20)
                && GuildStatBuffRule.BuffIndexForCategory(3)==167
                && GuildStatBuffRule.BuffIndexForCategory(5)==169
                && GuildStatBuffRule.BuffIndexForCategory(6)==0
                && timedBuff is {SystemGroup:"Guild_Status"}
                && permanentBuff is {SystemGroup:"Guild_Status1"}
                && timedBuff.Effects.Count==4
                && timedBuff.Effects.All(e=>e.Parameters.Count>=1 && e.Parameters[0]==60)
                && permanentBuff.Effects.Select(e=>e.Name).OrderBy(x=>x).SequenceEqual(
                    new[]{"magical_attack","magical_defence","physical_attack","physical_defence"}));
            check("active guild attribute content resolves to the timed buff, expired and absent ones do not",
                activeStatBuff is {BuffIndex:167,Category:3}
                && GuildStatBuffRule.Resolve(null,nowUnix)==null
                && GuildStatBuffRule.Resolve(new[]{new GuildPurchasedContent(3,0)},0)==null
                && GuildStatBuffRule.Resolve(new[]{new GuildPurchasedContent(3,(uint)(nowUnix-1))},nowUnix)==null
                && GuildStatBuffRule.Resolve(new[]{new GuildPurchasedContent(4,GuildContentDefinition.PermanentExpiryUnix)},nowUnix)==null);
            // 公会经验值内容(类别码 1): 参数列是百分比(行 3/6/9 都是 10), 既不是增益索引
            // 也不是使用次数 —— 它没有对应的 variousbufflist 索引, 只能服务端算。
            check("guild experience content exposes the PVF percentage, not a buff index or a count",
                GuildContentCatalog.Resolve(3) is {Category:1,Parameter:10,CountFlag:0}
                && GuildContentCatalog.Resolve(3).ExperienceBonusPercent==10
                && GuildContentCatalog.Resolve(9).ExperienceBonusPercent==10
                && GuildContentCatalog.Resolve(2).ExperienceBonusPercent==0
                && GuildContentCatalog.Resolve(13).ExperienceBonusPercent==0
                && GuildExperienceBonusProvider.ExperiencePercentForCategory(1)==10
                && GuildExperienceBonusProvider.ExperiencePercentForCategory(7)==0
                && GuildExperienceBonusProvider.ExperiencePercentForCategory(5)==0);
            check("guild experience bonus applies only while the content is active",
                GuildExperienceBonusProvider.ResolvePercent(null,nowUnix)==0
                && GuildExperienceBonusProvider.ResolvePercent(
                    new[]{new GuildPurchasedContent(1,(uint)(nowUnix+60))},nowUnix)==10
                && GuildExperienceBonusProvider.ResolvePercent(
                    new[]{new GuildPurchasedContent(1,(uint)(nowUnix-1))},nowUnix)==0
                && GuildExperienceBonusProvider.ResolvePercent(
                    new[]{new GuildPurchasedContent(3,(uint)(nowUnix+60))},nowUnix)==0
                && GuildExperienceBonusProvider.ResolvePercent(
                    new[]{
                        new GuildPurchasedContent(1,GuildContentDefinition.PermanentExpiryUnix),
                        new GuildPurchasedContent(3,(uint)(nowUnix+60))},nowUnix)==10);
            // 计数制内容(类别码 7 组队传送): PVF 第 5 列标志位是 1、天数列写 9999。
            // 9999 是"不过期"哨兵而不是 27 年 —— 早期实现没读标志列, 直接按 9999 天
            // 存, 客户端就显示成"公会组队传送 9999日"(实机复现)。
            check("count-based guild content stores the permanent sentinel instead of 9999 days",
                GuildContentCatalog.Resolve(13) is {Category:7,Days:9999,CountFlag:1,IsCountBased:true,IsPermanent:true}
                && GuildContentCatalog.Resolve(13).StorageDays==-1
                && GuildContentCatalog.Resolve(13).UseCount==10
                && GuildContentCatalog.Resolve(15) is {Category:7,Parameter:1000,CountFlag:1,IsPermanent:true}
                && GuildContentCatalog.Resolve(15).UseCount==1000
                && GuildContentCatalog.Resolve(3) is {Category:1,Days:1,CountFlag:0,IsCountBased:false,IsPermanent:false}
                && GuildContentCatalog.Resolve(3).StorageDays==1
                && GuildContentCatalog.Resolve(3).UseCount==0
                // 组队传送的类别码必须与"唯一计数制类别"这件事一致, 否则扣减会扣错东西。
                && GuildContentCatalog.PartyTeleportCategory==7
                && GuildContentCatalog.All.Where(d=>d.Category==GuildContentCatalog.PartyTeleportCategory)
                    .All(d=>d.IsCountBased && d.UseCount>0));
            // 永久内容(类别码 5)严格覆盖限时内容: 两者效果相同, 同时下发会重复叠加。
            check("permanent guild attribute content takes precedence over the timed one",
                GuildStatBuffRule.Resolve(new[]{
                    new GuildPurchasedContent(3,(uint)(nowUnix+3600)),
                    new GuildPurchasedContent(5,GuildContentDefinition.PermanentExpiryUnix)},nowUnix)
                    is {BuffIndex:169,Category:5});
            // 生效判定必须每次重新读库, 不能缓存在会话里: 这里直接写一条永久内容记录,
            // 再走一次下发, 断言拿到的是 169 而不是库里原有的限时 167。
            // 并且必须把被取代的 167 显式删掉 —— 早期实现只下发胜出的那一条, 结果客户端
            // 增益栏里限时和永久两条同时挂着, 各加一次 60(实机复现)。
            database.Write((c,t)=>GuildRepository.UpsertPurchasedContent(c,t,guild.Id,5,-1));
            member.Drain();
            await GuildStatBuffNotifier.ApplyForCharacterAsync(member.Session,database,202,transitions);
            var switchPackets=member.Drain();
            check("apply resolves the active guild attribute buff from durable state on every call",
                switchPackets.Any(p=>p[0]==0
                    && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF
                    && p.Length==15+17 && BitConverter.ToInt32(p,16)==169));
            // 删除体: u8 count + i32 buffId × count。被取代的 167 必须删, 而当轮的 169 不能被删。
            check("switching to the permanent guild attribute buff removes the superseded timed one",
                switchPackets.Any(p=>p[0]==0
                    && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF
                    && p.Length==15+1+4 && p[15]==1 && BitConverter.ToInt32(p,16)==167)
                && !switchPackets.Any(p=>p[0]==0
                    && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF
                    && p.Length==15+1+4 && BitConverter.ToInt32(p,16)==169));
            database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="DELETE FROM guild_contents WHERE guild_id=@guild AND category=5;";
                cmd.Parameters.AddWithValue("@guild",guild.Id);
                cmd.ExecuteNonQuery();
            });
            check("removing the permanent record falls back to the timed buff",
                GuildStatBuffRule.Resolve(repository.GetRosterForMember(201).PurchasedContents,nowUnix)
                    is {BuffIndex:167,Category:3});
            // 租约过期: 注销在线背包后处理器直接返回, 不扣任何货币也不写记录。
            uint pointsBeforeStale=repository.Get(guild.Id).Points;
            InventoryContext.Unregister(replacement.Session.SessionId);
            await handler.Handle(replacement.Session,buyHeader,BitConverter.GetBytes(2u));
            check("stale inventory lease cannot buy guild content",
                repository.Get(guild.Id).Points==pointsBeforeStale
                && repository.GetRosterForMember(201).PurchasedContents.Count==3);
            InventoryContext.Register(replacement.Session.SessionId,new InventoryService(201,100,database));
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
            var disbandPackets = member.Drain();
            check("disband survives initiator disconnect and clears member identity", repository.Get(guild.Id)==null
                && member.Session.Player.Subtype0Tail.GuildId==0
                && disbandPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.GUILD_DISMISS));
            check("disband explicitly removes both possible guild attribute buff indices",
                disbandPackets.Any(p=>p[0]==0 && BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF
                    && p.Length==24 && p[15]==2 && BitConverter.ToInt32(p,16)==167 && BitConverter.ToInt32(p,20)==169));
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

            async Task QueuedContentBeforeGateAsync(GamePacketHeader command, byte[] request, Action expireSource)
            {
                InventoryContext.TryGetOwnedLease(replacement.Session.SessionId, 201, out var original);
                int gold = original.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count;
                uint points = repository.Get(guild.Id).Points;
                var gate = await transitions.AcquireAsync(201);
                Task handling;
                try
                {
                    handling = handler.Handle(replacement.Session, command, request);
                    check("guild currency request waits on the existing character gate", !handling.IsCompleted);
                    expireSource();
                }
                finally { gate.Dispose(); }
                await handling.WaitAsync(TimeSpan.FromSeconds(5));
                check("queued expired guild currency source cannot charge or project",
                    original.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart).Count == gold
                    && repository.Get(guild.Id).Points == points
                    && replacement.Drain().Count == 0 && member.Drain().Count == 0);
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
            public void Dispose()
            {
                GuildStatBuffNotifier.CancelForSession(Session);
                InventoryContext.Unregister(Session.SessionId);Session.Close();_reader.Dispose();
            }
        }
    }
}
