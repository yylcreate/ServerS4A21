using DfoServer.Game.Wedding;
using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingGuestProtocolSelfTest
    {
        // Independent fixture for C274CED0's complete native struct copy.
        // These bytes are opaque stack prefix bytes, not an inner header.
        private static byte[] ManualBody(byte[] fields)
            => new byte[] { 0xA7, 0x31, 0xD2, 0x64, 0x89, 0xBE, 0x45, 0x73, 0x19, 0xCC, 0xE6, 0x52, 0x8F, 0xB4 }.Concat(fields).ToArray();

        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            try
            {
                byte[] invite = new byte[8]; BitConverter.GetBytes(67001).CopyTo(invite, 1);
                BitConverter.GetBytes((ushort)123).CopyTo(invite, 5); invite[7] = 11;
                Check("A21 invite carries canonical CID and untrusted online hints after its opaque 14-byte prefix", WeddingGuestCommandParser.TrySendLetter(ManualBody(invite), out var request)
                    && request.Operation == 0 && request.TargetCharacterId == 67001 && request.UserIdHint == 123 && request.ChannelHint == 11);
                invite[5] = 0; invite[6] = 0; invite[7] = 255;
                Check("offline channel sentinel does not replace target identity", WeddingGuestCommandParser.TrySendLetter(ManualBody(invite), out request)
                    && request.TargetCharacterId == 67001 && request.UserIdHint == 0 && request.ChannelHint == 255);
                Check("A21 operation one clears all target fields", WeddingGuestCommandParser.TrySendLetter(ManualBody(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }), out request)
                    && request.Operation == 1 && request.TargetCharacterId == 0);
                Check("operation one cannot smuggle a target and single invite cannot target zero", !WeddingGuestCommandParser.TrySendLetter(ManualBody(new byte[] { 1, 1, 0, 0, 0, 0, 0, 0 }), out _)
                    && !WeddingGuestCommandParser.TrySendLetter(ManualBody(new byte[8]), out _));
                Check("letter parser rejects short-field pseudowire", !WeddingGuestCommandParser.TrySendLetter(invite, out _)
                    && !WeddingGuestCommandParser.TrySendLetter(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, out _));
                Check("letter parser rejects truncation, tails and unknown operations", !WeddingGuestCommandParser.TrySendLetter(ManualBody(invite).AsSpan(0, 21).ToArray(), out _)
                    && !WeddingGuestCommandParser.TrySendLetter(ManualBody(invite).Concat(new byte[] { 0xA5 }).ToArray(), out _)
                    && !WeddingGuestCommandParser.TrySendLetter(ManualBody(new byte[] { 2, 1, 0, 0, 0, 0, 0, 0 }), out _));
                var state = new WeddingState(12, 1, 2, WeddingStatus.Reserved, 2, 0, 0, 0, 1800000000, 11, 13, 7);
                byte[] a = ClientTextEncoding.GetBytes("新郎"), b = ClientTextEncoding.GetBytes("新娘");
                byte[] body = WeddingGuestPacketBuilder.LetterBody(state, a, b, false);
                Check("native letter is exactly 78 bytes with captured ceremony fields", body.Length == 78
                    && BitConverter.ToUInt32(body, 0) == 12 && BitConverter.ToUInt32(body, 4) == 13
                    && BitConverter.ToUInt32(body, 8) == 1800000000 && BitConverter.ToUInt32(body, 12) == 11 && body[16] == 2);
                Check("native names are NUL padded GBK fields", body.AsSpan(17, a.Length).SequenceEqual(a)
                    && body.AsSpan(47, b.Length).SequenceEqual(b) && body[17 + a.Length] == 0 && body[47 + b.Length] == 0);
                byte[] paid = WeddingGuestPacketBuilder.LetterBody(state, a, b, true);
                Check("last letter byte is the native paid marker", body[77] == 0 && paid[77] == 1 && paid.AsSpan(0, 77).SequenceEqual(body.AsSpan(0, 77)));
                byte[] ids = WeddingGuestPacketBuilder.InvitedGuestIds(new[] { 67001, 67002 });
                Check("invited IDs use one-byte count and canonical u32 entries", ids.Length == 24 && ids[15] == 2
                    && BitConverter.ToUInt32(ids, 16) == 67001 && BitConverter.ToUInt32(ids, 20) == 67002);
                Check("empty list is a valid native snapshot", WeddingGuestPacketBuilder.InvitedGuestIds(Array.Empty<int>()).Length == 16);
                byte[] success = WeddingGuestPacketBuilder.SendLetterResult(0, 17), rejection = WeddingGuestPacketBuilder.SendLetterResult(-1);
                Check("send letter ACK contains remaining u16 only on success", success.Length == 22 && success[15] == 1
                    && BitConverter.ToUInt16(success, 20) == 17 && rejection.Length == 20 && BitConverter.ToInt32(rejection, 16) == -1);
                bool overflow = false;
                try { WeddingGuestPacketBuilder.InvitedGuestIds(Enumerable.Range(1, 256).ToArray()); } catch (ArgumentOutOfRangeException) { overflow = true; }
                Check("one native list frame cannot overflow its count byte", overflow);
                byte[] Gift(byte[] blessing)
                {
                    var bytes = new byte[12 + blessing.Length]; BitConverter.GetBytes(12u).CopyTo(bytes, 0);
                    BitConverter.GetBytes(10000).CopyTo(bytes, 4); BitConverter.GetBytes(blessing.Length).CopyTo(bytes, 8);
                    blessing.CopyTo(bytes, 12); return bytes;
                }
                var wish = ClientTextEncoding.GetBytes("祝你们永结同心幸福美满");
                var twenty = ClientTextEncoding.GetBytes("百年好合百年好合百年");
                Check("native blessing limit counts GBK bytes rather than UTF16 characters", twenty.Length == 20
                    && WeddingGuestCommandParser.TryMoneyGift(Gift(twenty), out var gift) && gift.Gold == 10000 && gift.PairId == 12
                    && !WeddingGuestCommandParser.TryMoneyGift(Gift(wish), out _));
                Check("empty native blessing remains a valid length-prefixed string", WeddingGuestCommandParser.TryMoneyGift(Gift(Array.Empty<byte>()), out _));
                Check("gift parser rejects trailing data, NUL and malformed GBK", !WeddingGuestCommandParser.TryMoneyGift(Gift(new byte[] { 65, 0, 66 }), out _)
                    && !WeddingGuestCommandParser.TryMoneyGift(Gift(new byte[] { 0x81 }), out _)
                    && !WeddingGuestCommandParser.TryMoneyGift(Gift(Array.Empty<byte>()).Concat(new byte[] { 0xA5 }).ToArray(), out _)
                    && !WeddingGuestCommandParser.TryMoneyGift(new byte[11], out _));
                var giftSuccess = WeddingGuestPacketBuilder.MoneyGiftResult(0, 12);
                Check("native gift ACK carries the pair only on success", giftSuccess.Length == 24 && BitConverter.ToUInt32(giftSuccess, 20) == 12
                    && WeddingGuestPacketBuilder.MoneyGiftResult(-1, 12).Length == 20);
                RunWireAsync(Check).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_GUEST_PROTOCOL: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }

        private static async Task RunWireAsync(Action<string, bool> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-guest-wire-" + Guid.NewGuid().ToString("N") + ".db");
            var directory = new SessionDirectory();
            try
            {
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                db.Write((c, t) =>
                {
                    using var q = c.CreateCommand(); q.Transaction = t;
                    q.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(64501,'guest-wire-host',''),(64502,'guest-wire-spouse',''),(64503,'guest-wire-online',''),(64504,'guest-wire-offline',''),(64505,'guest-wire-stranger','');";
                    q.ExecuteNonQuery();
                    string[] names = { "宴主甲", "宴主乙", "在线宾客", "离线好友", "无关角色" };
                    for (int i = 0; i < names.Length; i++)
                    {
                        q.Parameters.Clear();
                        q.CommandText = "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(@id,@id,@name,@job,60);";
                        q.Parameters.AddWithValue("@id", 64501 + i); q.Parameters.AddWithValue("@name", ClientTextEncoding.GetBytes(names[i]));
                        q.Parameters.AddWithValue("@job", i == 0 ? 0 : 1); q.ExecuteNonQuery();
                    }
                });
                int port = GameNetworkConfig.GetGameChannels().First(x => x.ChannelId == GameNetworkConfig.NormalChannelIndex).ListenerGamePort;
                using var host = await Peer.Create(directory, 64501, "宴主甲", port);
                using var spouse = await Peer.Create(directory, 64502, "宴主乙", port);
                using var guest = await Peer.Create(directory, 64503, "在线宾客", port);
                using var offline = await Peer.Create(directory, 64504, "离线好友", port, register: false);
                using var randomGuest = await Peer.Create(directory, 64505, "无关角色", port, register: false);
                using var retired = await Peer.Create(directory, 64501, "宴主甲", port, register: false);
                foreach (var p in new[] { host, spouse, guest, offline, randomGuest, retired })
                {
                    p.Session.Account = new AccountRecord { AccountId = p.Session.Player.CharacterId };
                    p.Session.Player.Level = 60;
                }
                var characters = new SqliteCharacterRepository(db);
                check("friend authorization fixture stores the same GBK BLOB names as character creation", characters.GetById(64501).DisplayName == "宴主甲"
                    && characters.GetById(64504).DisplayName == "离线好友");
                var source = new SqliteSelectCharacterDataSource(db, characters);
                var refresh = new InventoryRefreshSender(source, characters, db);
                var town = new TownHandler(characters, source, null, directory, refresh,
                    dungeonInstances: null, raidManager: null, database: db);
                var transitions = new CharacterTransitionCoordinator(directory);
                using var wedding = new WeddingHandler(directory, transitions, db, characters, refresh, town);
                wedding.ConfigureGuestFriendLookup((a, b) => a == "宴主甲" && b == "离线好友");
                int mailNotices = 0;
                wedding.ConfigureMailNotifier(_ => { mailNotices++; return Task.CompletedTask; });
                var routes = new GameCommandRegistry(); routes.RegisterGroup("wedding-guest-wire", wedding.Register);
                var repo = new WeddingGuestInvitationRepository(db);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long start = now + WeddingCatalog.Get().ReservationDelaySeconds[0];
                int channel = GameNetworkConfig.TryResolveGameChannel(host.Session.ListenerPort, out var endpoint) ? endpoint.ChannelId : 0;
                byte room = WeddingSceneCatalog.Get().CeremonyRooms[0].Area;
                long pair = db.Write((c, t) =>
                {
                    var old = WeddingRepository.Create(c, t, 64501, 64502, now);
                    if (!WeddingRepository.Save(c, t, old, old with { Status = WeddingStatus.Reserved, StartAt = start, ChannelId = channel, Room = room }))
                        throw new InvalidOperationException("Guest wire fixture reservation failed.");
                    return old.PairId;
                });
                using (var c = db.OpenConnection()) InventoryContext.Register(guest.Session.SessionId,
                    InventoryService.LoadFromDb(c, 64503, 64503, db));
                bool Is(byte[] p, bool cmd, ushort type) => p[0] == (cmd ? 1 : 0) && BitConverter.ToUInt16(p, 1) == type;
                async Task Send(Peer p, CmdPacketTypeA21 type, byte[] body, ushort sequence = 0)
                {
                    if (!routes.TryGetValue((ushort)type, out var handler)) throw new InvalidOperationException("Missing guest route " + type);
                    await handler(p.Session, new GamePacketHeader { cmd = 1, type = (ushort)type, seq = sequence, checksum = sequence }, body);
                }
                byte[] Invite(int target)
                {
                    var b = new byte[8]; BitConverter.GetBytes(target).CopyTo(b, 1);
                    BitConverter.GetBytes((ushort)65000).CopyTo(b, 5); b[7] = 255; return ManualBody(b);
                }
                try
                {
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64503).AsSpan(14).ToArray());
                    check("registered invitation route rejects short-field pseudowire without mutation", host.Drain().Count == 0
                        && guest.Drain().Count == 0 && repo.LoadForGuest(64503, now).Count == 0);
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64503));
                    var ack = host.Drain(); var received = guest.Drain();
                    check("registered single invitation commits canonical CID despite forged online hints", ack.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER) && BitConverter.ToInt32(p, 16) == 0)
                        && repo.LoadForGuest(64503, now).Count == 1 && received.Count(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_LETTER) && p[92] == 0) == 1);
                    byte[] invited = WeddingGuestPacketBuilder.InvitedGuestIds(new[] { 64503 });
                    check("both hosts receive the same committed guest IDs while retaining independent quotas",
                        ack.Count(p => p.SequenceEqual(invited)) == 1 && spouse.Drain().Single().SequenceEqual(invited)
                        && repo.LoadForHost(64501, now).Remaining == repo.LoadForHost(64502, now).Remaining - 1);
                    int remaining = repo.LoadForHost(64501, now).Remaining;
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64503)); host.Drain(); guest.Drain();
                    check("replayed invitation neither spends capacity twice nor changes identity", repo.LoadForHost(64501, now).Remaining == remaining);
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64504)); host.Drain();
                    check("offline friend invitation is durable without requiring an actor UID", repo.LoadForGuest(64504, now).Count == 1 && offline.Drain().Count == 0);
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64505));
                    check("unrelated offline CID cannot be authorized by fabricated hints", host.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER) && BitConverter.ToInt32(p, 16) == -1)
                        && repo.LoadForGuest(64505, now).Count == 0);
                    byte[] randomRequest = ManualBody(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, randomRequest, 77);
                    check("random send with no eligible candidates succeeds without spending quota", host.Drain().Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER) && BitConverter.ToInt32(p, 16) == 0)
                        && repo.LoadForGuest(64505, now).Count == 0);
                    directory.Register(64505, randomGuest.Session);
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, randomRequest, 77); host.Drain();
                    check("an empty random command retry cannot select a player who joined later", repo.LoadForGuest(64505, now).Count == 0 && randomGuest.Drain().Count == 0);
                    randomGuest.Session.Player.Level = 16;
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, randomRequest, 78); host.Drain();
                    check("random invitation does not select players below the native guest level", repo.LoadForGuest(64505, now).Count == 0);
                    randomGuest.Session.Player.Level = 17;
                    await Send(host, CmdPacketTypeA21.WEDDING_SEND_LETTER, randomRequest, 79); host.Drain();
                    check("a new random command selects an eligible same-channel canonical player", repo.LoadForGuest(64505, now).Count == 1
                        && randomGuest.Drain().Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_LETTER)));
                    directory.Register(64504, offline.Session);
                    await wedding.RestoreAsync(offline.Session);
                    check("login recovery restores the durable unpaid invitation", offline.Drain().Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_LETTER) && p[92] == 0));
                    InventoryContext.TryGetOwnedLease(guest.Session.SessionId, 64503, out var lease);
                    int amount = WeddingCatalog.Get().GiftMinimumGold, wallet = amount + 12345;
                    lease.Inventory.SetMainVirtualCount(0, wallet);
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new InvalidOperationException("Cannot prepare gift wallet.");
                    var gift = new byte[12]; BitConverter.GetBytes((uint)pair).CopyTo(gift, 0); BitConverter.GetBytes(amount).CopyTo(gift, 4);
                    await Send(guest, CmdPacketTypeA21.WEDDING_MONEY_GIFT, gift);
                    var giftPackets = guest.Drain();
                    check("registered gift commits gold and only then publishes ACK and paid letter", lease.Inventory.GetMainVirtualCount(0).Count == wallet - amount
                        && giftPackets.Any(p => Is(p, true, (ushort)CmdPacketTypeA21.WEDDING_MONEY_GIFT) && BitConverter.ToInt32(p, 16) == 0)
                        && giftPackets.Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_LETTER) && p[92] == 1));
                    bool PendingGiftWithoutMail() => db.Read(c =>
                    {
                        using var command = c.CreateCommand();
                        command.CommandText = "SELECT (SELECT COUNT(*) FROM mailbox_messages),"
                            + "(SELECT COUNT(*) FROM wedding_money_gifts WHERE pair_id=@pair AND guest_character_id=64503 AND state=0);";
                        command.Parameters.AddWithValue("@pair", pair);
                        using var reader = command.ExecuteReader();
                        return reader.Read() && reader.GetInt32(0) == 0 && reader.GetInt32(1) == 1;
                    });
                    check("payment escrows the gift without promising mail before ceremony settlement",
                        mailNotices == 0 && PendingGiftWithoutMail());
                    await Send(guest, CmdPacketTypeA21.WEDDING_MONEY_GIFT, gift); guest.Drain();
                    check("gift retry restores paid state without another debit or a false mail alarm",
                        lease.Inventory.GetMainVirtualCount(0).Count == wallet - amount && mailNotices == 0 && PendingGiftWithoutMail());
                    await wedding.RestoreAsync(guest.Session);
                    check("recovery derives paid state from the committed receipt", guest.Drain().Any(p => Is(p, false, (ushort)NotiPacketTypeA21.WEDDING_LETTER) && p[92] == 1));
                    await Send(retired, CmdPacketTypeA21.WEDDING_SEND_LETTER, Invite(64505));
                    check("a retired session is rejected even for an existing eligible target", retired.Drain().Any(p => Is(p, true,
                        (ushort)CmdPacketTypeA21.WEDDING_SEND_LETTER) && BitConverter.ToInt32(p, 16) == -1)
                        && repo.LoadForHost(64501, now).Remaining == remaining - 2);
                }
                finally
                {
                    wedding.Dispose();
                    InventoryContext.Unregister(guest.Session.SessionId, 64503);
                    await directory.UnregisterAsync(64501, host.Session);
                    await directory.UnregisterAsync(64502, spouse.Session);
                    await directory.UnregisterAsync(64503, guest.Session);
                    await directory.UnregisterAsync(64504, offline.Session);
                    await directory.UnregisterAsync(64505, randomGuest.Session);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }
    }
}
