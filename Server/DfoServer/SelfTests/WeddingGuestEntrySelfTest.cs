using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingGuestEntrySelfTest
    {
        private static byte[] ManualBody(byte[] fields)
            => new byte[] { 0xD3, 0x47, 0x8A, 0xB1, 0x6D, 0xE9, 0x35, 0x72, 0xC6, 0xA4, 0x1F, 0x93, 0x58, 0xBE }.Concat(fields).ToArray();

        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-guest-entry-" + Guid.NewGuid().ToString("N") + ".db");
            try { RunAsync(path, Check).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(ex.ToString(), false); }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_GUEST_ENTRY: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }

        private static async Task RunAsync(string path, Action<string, bool> check)
        {
            const int hostId = 64701, spouseId = 64702, guestId = 64703, secondId = 64704, strangerId = 64705, wrongChannelId = 64706, guestSpouseId = 64707;
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var names = new Dictionary<int, string> { [hostId] = "堂主甲", [spouseId] = "堂主乙", [guestId] = "已婚宾客", [secondId] = "另一宾客", [strangerId] = "未邀角色", [wrongChannelId] = "跨频宾客", [guestSpouseId] = "宾客配偶" };
            db.Write((c, t) =>
            {
                foreach (var entry in names)
                {
                    using var q = c.CreateCommand(); q.Transaction = t;
                    q.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@id,@login,''); INSERT INTO characters(character_id,account_id,name,job,level) VALUES(@id,@id,@name,@job,60);";
                    q.Parameters.AddWithValue("@id", entry.Key); q.Parameters.AddWithValue("@login", "guest-entry-" + entry.Key);
                    q.Parameters.AddWithValue("@name", ClientTextEncoding.GetBytes(entry.Value)); q.Parameters.AddWithValue("@job", entry.Key % 2); q.ExecuteNonQuery();
                }
            });
            long Scalar(string sql) => db.Read(c => { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); });
            var normal = GameNetworkConfig.GetGameChannels().First(x => x.ChannelId == GameNetworkConfig.NormalChannelIndex);
            var otherChannel = GameNetworkConfig.GetGameChannels().First(x => x.ChannelId != normal.ChannelId);
            var directory = new SessionDirectory(); directory.ConfigureTownCohort(WeddingSceneAdmission.SameCohort, WeddingSceneAdmission.CaptureCohort);
            using var host = await Peer.Create(directory, hostId, names[hostId], normal.ListenerGamePort);
            using var guest = await Peer.Create(directory, guestId, names[guestId], normal.ListenerGamePort);
            using var second = await Peer.Create(directory, secondId, names[secondId], normal.ListenerGamePort);
            using var stranger = await Peer.Create(directory, strangerId, names[strangerId], normal.ListenerGamePort);
            using var wrongChannel = await Peer.Create(directory, wrongChannelId, names[wrongChannelId], otherChannel.ListenerGamePort);
            var peers = new[] { host, guest, second, stranger, wrongChannel };
            foreach (var p in peers)
            { p.Session.Account = new AccountRecord { AccountId = p.Session.Player.CharacterId }; p.Session.Player.Level = 60; }
            var characters = new SqliteCharacterRepository(db);
            var source = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(source, characters, db); refresh.BindSessions(directory);
            var town = new TownHandler(characters, source, null, directory, refresh, null, null, db);
            var admission = new WeddingSceneAdmission(db, directory);
            town.ConfigureWeddingAreaAdmission(admission.CanEnter, null, admission.CaptureEntryCommit);
            var transitions = new CharacterTransitionCoordinator(directory);
            using var wedding = new WeddingHandler(directory, transitions, db, characters, refresh, town);
            var routes = new GameCommandRegistry(); routes.RegisterGroup("wedding-guest-entry", wedding.Register);
            var repository = new WeddingRepository(db); var invitations = new WeddingGuestInvitationRepository(db);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), start = now + WeddingCatalog.Get().ReservationDelaySeconds[0];
            var scene = WeddingSceneCatalog.Get().CeremonyRooms[0];
            long pair = db.Write((c, t) =>
            {
                var state = WeddingRepository.Create(c, t, hostId, spouseId, now);
                if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Reserved, StartAt = start, Room = scene.Area, ChannelId = normal.ChannelId })) throw new InvalidOperationException();
                return state.PairId;
            });
            long guestPair = db.Write((c, t) =>
            {
                var state = WeddingRepository.Create(c, t, guestId, guestSpouseId, now);
                if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Married, MarriedAt = now })) throw new InvalidOperationException();
                return state.PairId;
            });
            guest.Session.Player.Wedding = repository.Load(guestId);
            var initialGuestMarriage = guest.Session.Player.Wedding;
            foreach (var p in new[] { guest, second, wrongChannel })
            { using var c = db.OpenConnection(); InventoryContext.Register(p.Session.SessionId, InventoryService.LoadFromDb(c, p.Session.Player.CharacterId, p.Session.Player.CharacterId, db)); }
            bool Is(byte[] p, bool command, ushort type) => p[0] == (command ? 1 : 0) && BitConverter.ToUInt16(p, 1) == type;
            async Task Send(Peer p, CmdPacketTypeA21 type, byte[] body)
            {
                if (!routes.TryGetValue((ushort)type, out var handler)) throw new InvalidOperationException("Missing route " + type);
                await handler(p.Session, new GamePacketHeader { cmd = 1, type = (ushort)type }, body);
            }
            byte[] Enter(long id, byte room)
            { var b = new byte[9]; BitConverter.GetBytes((uint)id).CopyTo(b, 0); BitConverter.GetBytes((uint)room).CopyTo(b, 4); b[8] = 1; return ManualBody(b); }
            byte[] Area(byte townId, byte area)
            { var b = new byte[6]; b[0] = townId; b[1] = area; BitConverter.GetBytes((short)500).CopyTo(b, 2); BitConverter.GetBytes((short)230).CopyTo(b, 4); return b; }
            byte[] Load(long id, bool home)
            { var b = new byte[5]; BitConverter.GetBytes((uint)id).CopyTo(b, 0); b[4] = home ? (byte)1 : (byte)0; return ManualBody(b); }
            byte[] Update(long id, string text)
            { var value = ClientTextEncoding.GetBytes(text); var b = new byte[8 + value.Length]; BitConverter.GetBytes((uint)id).CopyTo(b, 0); BitConverter.GetBytes(value.Length).CopyTo(b, 4); value.CopyTo(b, 8); return b; }
            byte[] Delete(long id, uint comment, bool home)
            { var b = new byte[9]; BitConverter.GetBytes((uint)id).CopyTo(b, 0); BitConverter.GetBytes(comment).CopyTo(b, 4); b[8] = home ? (byte)1 : (byte)0; return ManualBody(b); }
            bool Refused(Peer p) => p.Drain().Any(b => Is(b, true, (ushort)CmdPacketTypeA21.WEDDING_ENTER_CEREMONY) && BitConverter.ToInt32(b, 16) != 0)
                && p.Session.Player.CurTownId != scene.Town && p.Session.Player.WeddingCeremony == null;
            bool ContainsComment(byte[] packet, string text)
            {
                if (packet.Length < 28 || BitConverter.ToInt32(packet, 16) != 0) return false;
                int count = BitConverter.ToInt32(packet, 24), cursor = 28; bool found = false;
                if (count is < 0 or > WeddingGuestBookRepository.MaximumEntries) return false;
                for (int i = 0; i < count; i++)
                {
                    if (cursor + 42 > packet.Length) return false;
                    int length = BitConverter.ToInt32(packet, cursor + 38);
                    if (length < 1 || length > packet.Length - cursor - 42) return false;
                    found |= ClientTextEncoding.GetString(packet, cursor + 42, length) == text;
                    cursor += 42 + length;
                }
                return found && cursor == packet.Length;
            }
            async Task Pay(Peer p, string text)
            {
                InventoryContext.TryGetOwnedLease(p.Session.SessionId, p.Session.Player.CharacterId, out var lease);
                int amount = WeddingCatalog.Get().GiftMinimumGold;
                lease.Inventory.SetMainVirtualCount(0, amount + 9876);
                if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Gift fixture wallet failed.");
                var value = ClientTextEncoding.GetBytes(text); var b = new byte[12 + value.Length];
                BitConverter.GetBytes((uint)pair).CopyTo(b, 0); BitConverter.GetBytes(amount).CopyTo(b, 4); BitConverter.GetBytes(value.Length).CopyTo(b, 8); value.CopyTo(b, 12);
                if (p == wrongChannel)
                {
                    // Domain setup isolates the entry channel check from the
                    // separate money UI's same-channel precondition.
                    var result = new WeddingMoneyGiftCommitService(WeddingCatalog.Get()).Pay(lease, pair, start, amount, value, () => true);
                    if (!result.Success) throw new InvalidOperationException("Cross-channel entry fixture payment failed.");
                }
                else
                {
                    await Send(p, CmdPacketTypeA21.WEDDING_MONEY_GIFT, b);
                    if (!p.Drain().Any(q => Is(q, true, (ushort)CmdPacketTypeA21.WEDDING_MONEY_GIFT) && BitConverter.ToInt32(q, 16) == 0)) throw new InvalidOperationException("Gift route fixture payment failed.");
                }
            }
            try
            {
                await Send(stranger, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area).AsSpan(14).ToArray());
                check("registered guest entry rejects short-field pseudowire", stranger.Drain().Count == 0
                    && stranger.Session.Player.CurTownId == 1 && stranger.Session.Player.WeddingCeremony == null);
                await Send(stranger, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area).AsSpan(0, 22).ToArray());
                await Send(stranger, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area).Concat(new byte[] { 0xA5 }).ToArray());
                var invalidFlag = Enter(pair, scene.Area); invalidFlag[22] = 2;
                await Send(stranger, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, invalidFlag);
                check("registered guest entry rejects truncated, tailed and malformed complete bodies", stranger.Drain().Count == 0
                    && stranger.Session.Player.CurTownId == 1 && stranger.Session.Player.WeddingCeremony == null);
                await Send(stranger, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area));
                check("registered guest entry rejects an uninvited character", Refused(stranger));
                var invited = invitations.Invite(hostId, hostId, pair, start, new[] { guestId, secondId, wrongChannelId }, () => true);
                if (!invited.Success) throw new InvalidOperationException("Guest entry fixture invitation failed.");
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area));
                check("an invitation without its financial receipt cannot enter", Refused(guest));
                await town.Handle_ENUM_CMDPACKET_SET_USER_AREA(second.Session, default, Area(scene.Town, scene.Area)); second.Drain();
                check("generic SET_USER_AREA cannot bypass the paid receipt", second.Session.Player.CurTownId == 1 && second.Session.Player.WeddingCeremony == null);
                await Pay(guest, "百年好合");
                check("payment stores its blessing in the same durable event", Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id={pair} AND ceremony_start_at={start} AND author_id={guestId};") == 1);
                guest.Session.Player.Level = 16;
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area));
                check("a paid guest below native level 17 is still rejected", Refused(guest)); guest.Session.Player.Level = 17;
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair + 100, scene.Area));
                check("paid entry cannot substitute a different pair", Refused(guest));
                byte wrongRoom = WeddingSceneCatalog.Get().CeremonyRooms.First(x => x.Area != scene.Area).Area;
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, wrongRoom));
                check("paid entry cannot substitute a different chapel", Refused(guest));
                await Pay(wrongChannel, "永结同心");
                await Send(wrongChannel, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area));
                check("a valid paid invitation does not authorize a different listener channel", Refused(wrongChannel));
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area));
                var entered = guest.Drain();
                int areaIndex = entered.FindIndex(p => Is(p, false, (ushort)NotiPacketTypeA21.USER_AREA));
                int ackIndex = entered.FindIndex(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_ENTER_CEREMONY) && BitConverter.ToInt32(p, 16) == 0);
                check("guest entry publishes town position before native success ACK", guest.Session.Player.CurTownId == scene.Town && guest.Session.Player.CurAreaId == scene.Area
                    && areaIndex >= 0 && ackIndex > areaIndex && WeddingSceneAdmission.IsCeremony(guest.Session.Player, pair, start));
                check("attendance does not overwrite the guest's own marriage", repository.Load(guestId) == initialGuestMarriage && guest.Session.Player.Wedding == initialGuestMarriage
                    && repository.Load(hostId).Status == WeddingStatus.Reserved && guest.Session.Player.WeddingRoomPairId == 0);
                InventoryContext.TryGetOwnedLease(guest.Session.SessionId, guestId, out var guestLease);
                int balance = guestLease.Inventory.GetMainVirtualCount(0).Count;
                await Send(guest, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area)); guest.Drain();
                check("entry retry cannot debit the gift again", guestLease.Inventory.GetMainVirtualCount(0).Count == balance
                    && Scalar($"SELECT COUNT(*) FROM wedding_money_gifts WHERE pair_id={pair} AND ceremony_start_at={start} AND guest_character_id={guestId};") == 1);
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, false));
                check("chapel flag zero loads the payment blessing for this event", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT)
                    // The paid cross-channel fixture has also left a blessing:
                    // entry refusal does not undo an already committed gift.
                    && BitConverter.ToInt32(p, 16) == 0 && BitConverter.ToInt32(p, 24) == 2 && ContainsComment(p, "百年好合")));
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, false).AsSpan(14).ToArray());
                check("chapel load rejects a short-field pseudowire body", guest.Drain().Count == 0);
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, false).AsSpan(0, 18).ToArray());
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, false).Concat(new byte[] { 0xA5 }).ToArray());
                check("chapel load requires exactly the complete native struct length", guest.Drain().Count == 0);
                await Send(guest, CmdPacketTypeA21.UPDATE_GUEST_COMMENT, Update(pair, "白头偕老"));
                check("in-chapel update writes this ceremony board", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.UPDATE_GUEST_COMMENT) && BitConverter.ToInt32(p, 16) == 0)
                    && Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id={pair} AND ceremony_start_at={start} AND author_id={guestId};") == 2);
                uint comment = checked((uint)Scalar($"SELECT MAX(comment_id) FROM wedding_ceremony_comments WHERE pair_id={pair} AND author_id={guestId};"));
                await Send(guest, CmdPacketTypeA21.DELETE_GUEST_COMMENT, Delete(pair, comment, false).AsSpan(14).ToArray());
                check("chapel delete rejects short-field pseudowire without deleting the comment", guest.Drain().Count == 0
                    && Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE comment_id={comment};") == 1);
                await Pay(second, "新婚快乐");
                await Send(second, CmdPacketTypeA21.WEDDING_ENTER_CEREMONY, Enter(pair, scene.Area)); second.Drain(); guest.Drain();
                await Send(second, CmdPacketTypeA21.DELETE_GUEST_COMMENT, Delete(pair, comment, false)); second.Drain();
                check("another paid guest cannot delete someone else's ceremony comment", Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE comment_id={comment};") == 1);
                await Send(guest, CmdPacketTypeA21.DELETE_GUEST_COMMENT, Delete(pair, comment, false)); guest.Drain();
                check("the ceremony author can delete their own comment", Scalar($"SELECT COUNT(*) FROM wedding_ceremony_comments WHERE comment_id={comment};") == 0);
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(guestPair, false));
                check("chapel load cannot read a different pair's board", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT) && BitConverter.ToInt32(p, 16) != 0));
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, true));
                check("home flag one cannot read the host's ceremony board", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT) && BitConverter.ToInt32(p, 16) != 0));
                var home = WeddingSceneCatalog.Get().CoupleRoom;
                await town.Handle_ENUM_CMDPACKET_SET_USER_AREA(guest.Session, default, Area(home.Town, home.Area)); guest.Drain(); second.Drain();
                check("moving to the guest's own home clears ceremony audience only", guest.Session.Player.CurTownId == home.Town
                    && guest.Session.Player.WeddingCeremony == null && guest.Session.Player.WeddingRoomPairId == guestPair
                    && guest.Session.Player.Wedding == initialGuestMarriage);
                await Send(guest, CmdPacketTypeA21.UPDATE_GUEST_COMMENT, Update(guestPair, "我的婚房")); guest.Drain();
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(guestPair, true));
                check("home board remains independently writable and readable", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT)
                    && BitConverter.ToInt32(p, 16) == 0 && BitConverter.ToInt32(p, 24) == 1)
                    && Scalar($"SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id={guestPair};") == 1);
                await Send(guest, CmdPacketTypeA21.LOAD_GUEST_COMMENT, Load(pair, false));
                check("leaving the chapel revokes ceremony board access", guest.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.LOAD_GUEST_COMMENT) && BitConverter.ToInt32(p, 16) != 0));
            }
            finally
            {
                wedding.Dispose();
                foreach (var p in peers)
                { InventoryContext.Unregister(p.Session.SessionId, p.Session.Player.CharacterId); await directory.UnregisterAsync(p.Session.Player.CharacterId, p.Session); }
            }
        }
    }
}
