using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class GuildEntranceSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private const int AccountId = 410;

        public static int Run()
        {
            int failures = 0;
            void Check(string label, bool passed)
            {
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
                if (!passed) failures++;
            }

            string path = Path.Combine(Path.GetTempPath(), $"guild-entrance-{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                RunAsync(database, Check).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"Guild entrance: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }

        private static async Task RunAsync(IGameDatabase database, Action<string, bool> check)
        {
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(410,'guild-entrance-test','');
INSERT INTO characters(character_id,account_id,name) VALUES
(411,410,'entrance-leader'),(412,410,'entrance-member'),(413,410,'entrance-outsider'),
(414,410,'entrance-founder'),(415,410,'entrance-poor'),(416,410,'entrance-rollback'),
(417,410,'entrance-applicant'),(418,410,'entrance-swap-from'),
(419,410,'entrance-swap-to'),(420,410,'entrance-new-generation');";
                cmd.ExecuteNonQuery();
            });
            int cost = GuildCreationService.ReadCreationCost();
            int funded = checked(cost * 2);
            var repository = new GuildRepository(database);
            var guild = database.Write((c, t) => GuildRepository.Insert(c, t, 411, "入口公会", "一起冒险"));
            if (repository.Apply(guild.Id, 412) != GuildResult.Success
                || repository.Approve(guild.Id, 411, 412) != GuildResult.Success
                || repository.EditText(411, GuildTextField.Announcement, "入口公告") != GuildResult.Success)
                throw new InvalidOperationException("Unable to seed guild entrance membership.");

            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            var records = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, records);
            var refresh = new InventoryRefreshSender(source, records, database);
            refresh.BindSessions(sessions);
            var selection = new CharacterSelectHandler(source, records, getUserInfoTemplate: null,
                sessions: sessions, database: database);
            var publisher = new GuildStatePublisher(repository, transitions, sessions, refresh);
            var join = new GuildJoinHandler(repository, transitions, sessions, refresh, publisher);
            var creation = new GuildCreationHandler(repository, transitions, refresh);
            using var leader = await Peer.CreateAsync(database, sessions, 411, funded);
            using var member = await Peer.CreateAsync(database, sessions, 412, funded);
            using var outsider = await Peer.CreateAsync(database, sessions, 413, funded);
            using var founder = await Peer.CreateAsync(database, sessions, 414, funded);
            using var poor = await Peer.CreateAsync(database, sessions, 415, cost - 1);
            using var rollback = await Peer.CreateAsync(database, sessions, 416, funded);
            using var applicant = await Peer.CreateAsync(database, sessions, 417, funded);

            // Exercise the real final selection projection under the same character
            // transition used by initialization, rather than just testing the builder.
            await Wait(transitions.RunIfCurrentAsync(leader.Session,
                () => selection.ProjectGuildEntranceAsync(leader.Session)));
            check("member initialization sends rank then a basic agit in current guild info",
                IsRankThenInfo(leader.Drain(), 1, "入口公会", "入口公告"));
            await Wait(transitions.RunIfCurrentAsync(member.Session,
                () => selection.ProjectGuildEntranceAsync(member.Session)));
            check("ordinary member initialization uses own rank and the same basic entrance",
                IsRankThenInfo(member.Drain(), 4, "入口公会", "入口公告"));
            await Wait(selection.ProjectGuildEntranceAsync(outsider.Session));
            // This checks server projection only. Client cleanup on member -> outsider
            // character selection remains a separate client initialization/UI boundary.
            check("nonmember initialization sends no guild entrance or delete-agit prompt",
                outsider.Drain().Count == 0 && !repository.IsMember(413));
            await Wait(selection.ProjectGuildEntranceAsync(leader.Session));
            await Wait(selection.ProjectGuildEntranceAsync(leader.Session));
            var repeatedInit = leader.Drain();
            check("repeated initialization only republishes and never charges or changes membership",
                repeatedInit.Count == 4
                && IsRankThenInfo(repeatedInit.Take(2).ToArray(), 1, "入口公会", "入口公告")
                && IsRankThenInfo(repeatedInit.Skip(2).ToArray(), 1, "入口公会", "入口公告")
                && leader.Gold == funded && repository.GetMemberIds(guild.Id).Count == 2);

            await Wait(join.Handle(member.Session, Header(CmdPacketTypeA21.REFRESH_GUILD_INFO), Array.Empty<byte>()));
            check("empty refresh request projects rank before valid entrance info",
                IsRankThenInfo(member.Drain(), 4, "入口公会", "入口公告"));
            await Wait(join.Handle(member.Session, Header(CmdPacketTypeA21.REFRESH_GUILD_INFO), new byte[] { 0 }));
            await Wait(join.Handle(outsider.Session, Header(CmdPacketTypeA21.REFRESH_GUILD_INFO), Array.Empty<byte>()));
            check("malformed refresh and nonmember refresh cannot open the entrance",
                member.Drain().Count == 0 && outsider.Drain().Count == 0);

            check("member inventory lease is removed for ownership regression",
                InventoryContext.Unregister(member.Session.SessionId));
            await Wait(selection.ProjectGuildEntranceAsync(member.Session));
            await Wait(join.Handle(member.Session, Header(CmdPacketTypeA21.REFRESH_GUILD_INFO), Array.Empty<byte>()));
            await Wait(publisher.RefreshAsync(new[] { 412 }));
            check("initialization, refresh and publisher all refuse a missing owned lease",
                member.Drain().Count == 0 && repository.IsMember(412));
            member.RestoreLease();

            await Wait(PrepareCreationAsync(creation, founder, "新入口公会", "新公会宣传"));
            await Wait(creation.Handle(founder.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            var creationPackets = founder.Drain();
            int createdIndex = IndexOfNoti(creationPackets, NotiPacketTypeA21.GUILD_CREATE);
            int ackIndex = IndexOfCommand(creationPackets, CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT);
            int rankIndex = IndexOfNoti(creationPackets, NotiPacketTypeA21.GUILD_MEMBER_INFO);
            int infoIndex = IndexOfNoti(creationPackets, NotiPacketTypeA21.GUILD_INFO);
            check("committed guild creation keeps create and ACK before rank and entrance info",
                createdIndex >= 0 && ackIndex > createdIndex
                && creationPackets[ackIndex].AsSpan(15).SequenceEqual(new byte[] { 1 })
                && rankIndex == ackIndex + 1 && infoIndex == rankIndex + 1
                && IsRankThenInfo(creationPackets.Skip(rankIndex).ToArray(), 1, "新入口公会", ""));
            check("creation entrance projection follows one durable guild and currency commit",
                new GuildRepository(database).GetForMember(414)?.Name == "新入口公会"
                && repository.GetForMember(414)?.Promotion == "新公会宣传"
                && founder.Session.Player.Subtype0Tail.GuildId == (uint)repository.GetForMember(414).Id
                && founder.Gold == cost && ReadSavedGold(database, 414) == cost);
            await Wait(creation.Handle(founder.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            check("repeated creation request cannot charge again or republish an entrance",
                IsFailureOnly(founder.Drain(), CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT)
                && founder.Gold == cost && ReadSavedGold(database, 414) == cost
                && CountGuilds(database) == 2);

            await Wait(PrepareCreationAsync(creation, poor, "余额不足公会", ""));
            await Wait(creation.Handle(poor.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            check("insufficient gold rejects creation without entrance info or membership",
                IsFailureOnly(poor.Drain(), CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT)
                && poor.Gold == cost - 1 && ReadSavedGold(database, 415) == cost - 1
                && !repository.IsMember(415) && !repository.NameExists("余额不足公会"));

            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = @"CREATE TRIGGER guild_entrance_fail_member BEFORE INSERT ON guild_members
WHEN NEW.character_id=416 BEGIN SELECT RAISE(ABORT,'guild entrance rollback test'); END;";
                cmd.ExecuteNonQuery();
            });
            await Wait(PrepareCreationAsync(creation, rollback, "事务失败公会", ""));
            await Wait(creation.Handle(rollback.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            check("failed membership transaction rolls back gold and never opens an entrance",
                IsFailureOnly(rollback.Drain(), CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT)
                && rollback.Gold == funded && ReadSavedGold(database, 416) == funded
                && !repository.IsMember(416) && !repository.NameExists("事务失败公会")
                && rollback.Session.Player.Subtype0Tail.GuildId == 0);

            if (repository.Apply(guild.Id, 417, "入口申请") != GuildResult.Success)
                throw new InvalidOperationException("Unable to seed entrance application.");
            await Wait(join.Handle(leader.Session, Header(CmdPacketTypeA21.APPROVE_JOIN_GUILD), BitConverter.GetBytes(417)));
            var approvedPackets = applicant.Drain();
            check("online approval immediately projects a basic entrance for the new durable member",
                repository.IsMember(417) && applicant.Session.Player.Subtype0Tail.GuildId == (uint)guild.Id
                && approvedPackets.Any(p => IsNoti(p, NotiPacketTypeA21.GUILD_INFO)
                    && HasBasicEntrance(p, "入口公会", "入口公告"))
                && !approvedPackets.Any(p => IsNoti(p, NotiPacketTypeA21.DELETE_GUILD_AGIT)));
            leader.Drain();
            member.Drain();

            await Wait(publisher.RefreshAsync(new[] { 412, 412 }));
            var published = member.Drain();
            check("publisher deduplicates recipients and sends rank then current basic entrance",
                published.Count == 3 && IsRankThenInfo(published.Take(2).ToArray(), 4, "入口公会", "入口公告")
                && IsCommand(published[2], CmdPacketTypeA21.GUILD_MEMER_LIST));
            await Wait(publisher.RefreshAsync(new[] { 413 }));
            var nonmember = outsider.Drain();
            check("nonmember publisher restores pending state without guild entrance or agit deletion",
                nonmember.Count == 1 && IsNoti(nonmember[0], NotiPacketTypeA21.JOIN_GUILD_INFO)
                && nonmember[0].AsSpan(15).SequenceEqual(new byte[] { 0 }));

            using (var gate = await transitions.AcquireAsync(412))
            {
                var pending = publisher.RefreshAsync(new[] { 412 });
                if (repository.EditText(411, GuildTextField.Announcement, "最新入口公告") != GuildResult.Success)
                    throw new InvalidOperationException("Unable to change announcement before queued projection.");
                gate.Dispose();
                await Wait(pending);
            }
            published = member.Drain();
            check("queued publisher reads the committed current announcement instead of an old snapshot",
                published.Count == 3 && IsRankThenInfo(published.Take(2).ToArray(), 4, "入口公会", "最新入口公告"));

            using (var gate = await transitions.AcquireAsync(412))
            {
                var pending = publisher.RefreshAsync(new[] { 412 });
                if (repository.RemoveMember(411, 412) != GuildResult.Success)
                    throw new InvalidOperationException("Unable to remove member before queued projection.");
                gate.Dispose();
                await Wait(pending);
            }
            var removed = member.Drain();
            check("queued publisher sees lost membership, clears identity and cannot reopen the entrance",
                !repository.IsMember(412) && member.Session.Player.Subtype0Tail.GuildId == 0
                && removed.Any(p => IsNoti(p, NotiPacketTypeA21.GUILD_SECEDE_TO_USER) && p.Length == 15)
                && !removed.Any(p => IsNoti(p, NotiPacketTypeA21.GUILD_INFO)
                    || IsNoti(p, NotiPacketTypeA21.DELETE_GUILD_AGIT)));
            await Wait(publisher.RefreshAsync(new[] { 412 }));
            check("repeated removed-member publication cannot recreate an entrance or repeat leave notice",
                member.Drain().All(p => !IsNoti(p, NotiPacketTypeA21.GUILD_INFO)
                    && !IsNoti(p, NotiPacketTypeA21.GUILD_SECEDE_TO_USER)));
            if (repository.Apply(guild.Id, 412) != GuildResult.Success
                || repository.Approve(guild.Id, 411, 412) != GuildResult.Success)
                throw new InvalidOperationException("Unable to restore member before generation regression.");
            await Wait(publisher.RefreshAsync(new[] { 412 }));
            member.Drain();

            Peer replacement;
            using (var gate = await transitions.AcquireAsync(412))
            {
                var pendingPublisher = publisher.RefreshAsync(new[] { 412 });
                var pendingRefresh = join.Handle(member.Session, Header(CmdPacketTypeA21.REFRESH_GUILD_INFO), Array.Empty<byte>());
                replacement = await Peer.CreateAsync(database, sessions, 412);
                gate.Dispose();
                await Wait(pendingPublisher);
                await Wait(pendingRefresh);
            }
            using (replacement)
            {
                await Wait(selection.ProjectGuildEntranceAsync(member.Session));
                check("queued publisher, refresh and initialization cannot project an old session onto its replacement",
                    member.Drain().Count == 0 && replacement.Drain().Count == 0
                    && repository.IsMember(412));
                await Wait(selection.ProjectGuildEntranceAsync(replacement.Session));
                check("replacement initialization restores its own rank and current entrance from durable membership",
                    IsRankThenInfo(replacement.Drain(), 4, "入口公会", "最新入口公告"));
                check("owned replacement retains persisted gold and does not revive the old inventory lease",
                    replacement.Gold == funded
                    && !InventoryContext.TryGetOwnedLease(member.Session.SessionId, 412, out _));
            }

            await Wait(PrepareCreationAsync(creation, outsider, "旧会话公会", ""));
            using (var gate = await transitions.AcquireAsync(413))
            {
                var pending = creation.Handle(outsider.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 });
                using var replacementCreator = await Peer.CreateAsync(database, sessions, 413);
                gate.Dispose();
                await Wait(pending);
                check("queued creation from a replaced session cannot commit, charge or publish",
                    outsider.Drain().Count == 0 && replacementCreator.Drain().Count == 0
                    && !repository.IsMember(413) && !repository.NameExists("旧会话公会")
                    && replacementCreator.Gold == funded && ReadSavedGold(database, 413) == funded);
            }

            using var switching = await Peer.CreateAsync(database, sessions, 418, funded);
            await Wait(PrepareCreationAsync(creation, switching, "换角旧公会", ""));
            int guildCountBeforeSwitch = CountGuilds(database);
            using (var gate = await transitions.AcquireAsync(418))
            {
                // Both requests use the same EnhancedClientSession and Player object.
                // B gets its real owned lease and a complete permitted draft before
                // A's queued final request resumes; guarding only the socket would
                // otherwise let A consume B's draft and gold.
                var pending = creation.Handle(switching.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 });
                await Wait(switching.SwitchCharacterAsync(419, funded));
                await Wait(PrepareCreationAsync(creation, switching, "换角新公会", ""));
                gate.Dispose();
                await Wait(pending);
                check("queued final creation cannot consume another character's valid draft on the same socket",
                    switching.Drain().Count == 0 && switching.Session.Player.CharacterId == 419
                    && switching.Session.Player.UserId == 419
                    && !repository.IsMember(418) && !repository.IsMember(419)
                    && !repository.NameExists("换角旧公会") && !repository.NameExists("换角新公会")
                    && CountGuilds(database) == guildCountBeforeSwitch
                    && switching.Gold == funded
                    && ReadSavedGold(database, 418) == funded && ReadSavedGold(database, 419) == funded);
            }
            await Wait(creation.Handle(switching.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            var switchedPackets = switching.Drain();
            int switchedInfoIndex = IndexOfNoti(switchedPackets, NotiPacketTypeA21.GUILD_INFO);
            check("new character's permitted draft remains usable after the old same-socket request is fenced",
                repository.GetForMember(419)?.Name == "换角新公会"
                && !repository.IsMember(418) && !repository.NameExists("换角旧公会")
                && CountGuilds(database) == guildCountBeforeSwitch + 1
                && switching.Gold == cost && ReadSavedGold(database, 419) == cost
                && ReadSavedGold(database, 418) == funded
                && switchedInfoIndex >= 1
                && IsRankThenInfo(switchedPackets.Skip(switchedInfoIndex - 1).ToArray(), 1, "换角新公会", ""));

            using var generation = await Peer.CreateAsync(database, sessions, 420, funded);
            generation.Session.GameSession = new GameSession(generation.Session, database);
            await Wait(PrepareCreationAsync(creation, generation, "代际测试公会", ""));
            int guildCountBeforeGeneration = CountGuilds(database);
            using (var gate = await transitions.AcquireAsync(420))
            {
                var pending = creation.Handle(generation.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 });
                generation.Session.GameSession = new GameSession(generation.Session, database);
                gate.Dispose();
                await Wait(pending);
                check("queued creation rejects a changed GameSession even with unchanged character, socket and owned lease",
                    generation.Drain().Count == 0 && !repository.IsMember(420)
                    && !repository.NameExists("代际测试公会") && CountGuilds(database) == guildCountBeforeGeneration
                    && generation.Gold == funded && ReadSavedGold(database, 420) == funded);
            }
            await Wait(creation.Handle(generation.Session, Header(CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT), new byte[] { 1 }));
            var generationPackets = generation.Drain();
            check("current GameSession can still use its valid draft after rejecting an older queued generation",
                repository.GetForMember(420)?.Name == "代际测试公会"
                && CountGuilds(database) == guildCountBeforeGeneration + 1
                && generation.Gold == cost && ReadSavedGold(database, 420) == cost
                && generationPackets.Any(p => IsCommand(p, CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT)
                    && p.AsSpan(15).SequenceEqual(new byte[] { 1 }))
                && generationPackets.Any(p => IsNoti(p, NotiPacketTypeA21.GUILD_INFO)
                    && HasBasicEntrance(p, "代际测试公会", "")));
        }

        private static Task Wait(Task task) => task.WaitAsync(Timeout);

        private static GamePacketHeader Header(CmdPacketTypeA21 command)
            => new GamePacketHeader { type = (ushort)command };

        private static async Task PrepareCreationAsync(GuildCreationHandler handler, Peer peer, string name, string promotion)
        {
            byte[] Dstr(string value)
            {
                var writer = new GamePacketWriter();
                writer.WriteClientDstr(value);
                return writer.ToArray();
            }
            await Wait(handler.Handle(peer.Session, Header(CmdPacketTypeA21.CHECK_GUILD_NAME_DOUBLE), Dstr(name)));
            await Wait(handler.Handle(peer.Session, Header(CmdPacketTypeA21.CHECK_GUILD_CREATE_PROMOTE_MSG), Dstr(promotion)));
            await Wait(handler.Handle(peer.Session, Header(CmdPacketTypeA21.REQUEST_GUILD_CREATE_PERMIT), Dstr("solo")));
            var packets = peer.Drain();
            if (packets.Count != 4
                || !IsCommand(packets[0], CmdPacketTypeA21.CHECK_GUILD_NAME_DOUBLE) || packets[0][15] != 1
                || !IsCommand(packets[1], CmdPacketTypeA21.CHECK_GUILD_CREATE_PROMOTE_MSG) || packets[1][15] != 1
                || !IsCommand(packets[2], CmdPacketTypeA21.REQUEST_GUILD_CREATE_PERMIT) || packets[2][15] != 1
                || !IsNoti(packets[3], NotiPacketTypeA21.REPLY_GUILD_CREATE_PERMIT) || packets[3][15] != 1)
                throw new InvalidOperationException("Actual guild creation request chain did not obtain a permit.");
        }

        private static bool IsRankThenInfo(IReadOnlyList<byte[]> packets, byte rank, string name, string announcement)
            => packets.Count == 2 && IsNoti(packets[0], NotiPacketTypeA21.GUILD_MEMBER_INFO)
                && packets[0].AsSpan(15).SequenceEqual(new[] { rank })
                && IsNoti(packets[1], NotiPacketTypeA21.GUILD_INFO)
                && HasBasicEntrance(packets[1], name, announcement);

        private static bool HasBasicEntrance(byte[] packet, string expectedName, string expectedAnnouncement)
        {
            using var stream = new MemoryStream(packet, writable: false);
            using var reader = new BinaryReader(stream);
            stream.Position = 15;
            string ReadDstr()
            {
                int length = reader.ReadInt32();
                if (length < 0 || length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid guild DSTR length.");
                return ClientTextEncoding.GetString(reader.ReadBytes(length));
            }
            string name = ReadDstr();
            byte membership = reader.ReadByte();
            reader.ReadByte();
            ushort count = reader.ReadUInt16();
            reader.ReadByte();
            reader.ReadUInt32();
            reader.ReadByte();
            string announcement = ReadDstr();
            reader.ReadByte();
            byte hasAgit = reader.ReadByte();
            byte independentState = reader.ReadByte();
            // Current client: announcement + second u8 creates guild+48;
            // the next u8 is an unrelated field, not a purchased agit type.
            return name == expectedName && announcement == expectedAnnouncement
                && membership == 1 && count > 0 && hasAgit == 1 && independentState == 0;
        }

        private static bool IsFailureOnly(IReadOnlyList<byte[]> packets, CmdPacketTypeA21 command)
            => packets.Count == 1 && IsCommand(packets[0], command)
                && packets[0].AsSpan(15).SequenceEqual(new byte[] { 0, 0 });

        private static bool IsNoti(byte[] packet, NotiPacketTypeA21 type)
            => packet.Length >= 15 && packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)type;

        private static bool IsCommand(byte[] packet, CmdPacketTypeA21 type)
            => packet.Length >= 15 && packet[0] == 1 && BitConverter.ToUInt16(packet, 1) == (ushort)type;

        private static int IndexOfNoti(IReadOnlyList<byte[]> packets, NotiPacketTypeA21 type)
            => Enumerable.Range(0, packets.Count).FirstOrDefault(i => IsNoti(packets[i], type), -1);

        private static int IndexOfCommand(IReadOnlyList<byte[]> packets, CmdPacketTypeA21 type)
            => Enumerable.Range(0, packets.Count).FirstOrDefault(i => IsCommand(packets[i], type), -1);

        private static int ReadSavedGold(IGameDatabase database, int characterId)
            => LoadSavedInventory(database, characterId)
                .GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static InventoryService LoadSavedInventory(IGameDatabase database, int characterId)
        {
            // Constructors initialize defaults; only the production hydration path
            // with a newly opened connection can prove committed/reconnected state.
            using var connection = database.OpenConnection();
            return InventoryService.LoadFromDb(connection, characterId, AccountId, database);
        }

        private static int CountGuilds(IGameDatabase database) => database.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM guilds;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            private readonly IGameDatabase _database;
            private readonly SessionDirectory _sessions;
            internal EnhancedClientSession Session { get; }

            private Peer(TcpClient reader, EnhancedClientSession session, IGameDatabase database, SessionDirectory sessions)
            {
                _reader = reader;
                Session = session;
                _database = database;
                _sessions = sessions;
            }

            internal int Gold
            {
                get
                {
                    if (!InventoryContext.TryGetOwnedLease(Session.SessionId, Session.Player.CharacterId, out var lease))
                        throw new InvalidOperationException("Guild test peer no longer owns its inventory.");
                    lock (lease.SyncRoot)
                        return lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                }
            }

            internal void RestoreLease()
                => InventoryContext.Register(Session.SessionId, LoadSavedInventory(_database, Session.Player.CharacterId));

            internal async Task SwitchCharacterAsync(int characterId, int gold)
            {
                int previous = Session.Player.CharacterId;
                if (!await _sessions.UnregisterAsync(previous, Session))
                    throw new InvalidOperationException("Unable to unregister old same-socket test character.");
                var record = new SqliteCharacterRepository(_database).GetById(characterId);
                Session.Player.CharacterId = characterId;
                Session.Player.UserId = checked((ushort)characterId);
                Session.Player.Name = record.Name;
                Session.Player.Level = record.Level;
                Session.Player.Job = record.Job;
                Session.Player.GrowType = record.GrowType;
                Session.Player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                GuildIdentityProjection.Apply(new GuildRepository(_database).GetForMember(characterId), Session.Player.Subtype0Tail);
                var lease = InventoryContext.Register(Session.SessionId, LoadSavedInventory(_database, characterId));
                lock (lease.SyncRoot)
                {
                    if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, gold)
                        || !InventoryPersistenceService.SaveDirty(lease))
                        throw new InvalidOperationException("Unable to persist switched guild test character gold.");
                }
                _sessions.Register(characterId, Session);
            }

            internal static async Task<Peer> CreateAsync(IGameDatabase database, SessionDirectory sessions, int characterId, int? gold = null)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var reader = new TcpClient { NoDelay = true, ReceiveTimeout = 3000 };
                TcpClient writer;
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
                    await connecting.WaitAsync(Timeout);
                }
                finally { listener.Stop(); }
                writer.NoDelay = true;
                var session = new EnhancedClientSession(writer, new GamePacketHeader(), 10010)
                {
                    Account = new AccountRecord { AccountId = AccountId, MId = "guild-entrance-test" }
                };
                var peer = new Peer(reader, session, database, sessions);
                try
                {
                    var record = new SqliteCharacterRepository(database).GetById(characterId);
                    session.Player.CharacterId = characterId;
                    session.Player.UserId = checked((ushort)characterId);
                    session.Player.Name = record.Name;
                    session.Player.Level = record.Level;
                    session.Player.Job = record.Job;
                    session.Player.GrowType = record.GrowType;
                    session.Player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                    GuildIdentityProjection.Apply(new GuildRepository(database).GetForMember(characterId), session.Player.Subtype0Tail);
                    var lease = InventoryContext.Register(session.SessionId, LoadSavedInventory(database, characterId));
                    if (gold.HasValue)
                    {
                        lock (lease.SyncRoot)
                        {
                            if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, gold.Value)
                                || !InventoryPersistenceService.SaveDirty(lease))
                                throw new InvalidOperationException("Unable to persist guild test gold.");
                        }
                    }
                    sessions.Register(characterId, session);
                    return peer;
                }
                catch
                {
                    peer.Dispose();
                    throw;
                }
            }

            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new InvalidOperationException("Guild entrance test connection closed.");
                    var header = new byte[15];
                    stream.ReadExactly(header);
                    int length = BitConverter.ToInt32(header, 3);
                    if (length < 15 || length > 65536) throw new InvalidDataException("Invalid guild entrance packet length.");
                    var packet = new byte[length];
                    header.CopyTo(packet, 0);
                    stream.ReadExactly(packet.AsSpan(15));
                    packets.Add(packet);
                }
                return packets;
            }

            public void Dispose()
            {
                InventoryContext.Unregister(Session.SessionId);
                _sessions.UnregisterAsync(Session.Player.CharacterId, Session).GetAwaiter().GetResult();
                Session.Close();
                _reader.Dispose();
            }
        }
    }
}
