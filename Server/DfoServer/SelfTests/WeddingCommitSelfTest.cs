using DfoServer.Game.Inventory;
using DfoServer.Game.Characters;
using DfoServer.Game.Wedding;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingCommitSelfTest
    {
        internal static int Run()
        {
            int failed = 0, passed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-" + Guid.NewGuid().ToString("N") + ".db");
            var sessionA = Guid.NewGuid(); var sessionB = Guid.NewGuid(); var replacementSessionA = Guid.NewGuid();
            try
            {
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63001,'wedding-a',''),(63002,'wedding-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63001,63001,'新郎',0,60),(63002,63002,'新娘',1,60);");
                using var connection = db.OpenConnection();
                var a = InventoryContext.Register(sessionA, InventoryService.LoadFromDb(connection, 63001, 63001, db));
                var b = InventoryContext.Register(sessionB, InventoryService.LoadFromDb(connection, 63002, 63002, db));
                var catalog = WeddingCatalog.Get();
                long now = 1800000000;
                var service = new WeddingCommitService(catalog, () => now);
                var repository = new WeddingRepository(db);
                byte room = WeddingSceneCatalog.Get().CeremonyRooms[0].Area;
                int initialGold = checked(catalog.Ceremonies.Max(x => x.Gold) + catalog.Alimony.Max() * 2 + 10000);
                a.Inventory.SetMainVirtualCount(0, initialGold);
                b.Inventory.SetMainVirtualCount(0, initialGold);
                Check("new schema contains wedding storage at the current migration", Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='wedding_pairs';") == 1);
                Check("A21 gender mapping follows native job 13 and excludes later job 15",
                    WeddingRules.IsMale(13) && !WeddingRules.IsMale(15) && WeddingRules.IsMale(0) && !WeddingRules.IsMale(1));
                byte[] NativeFields(string hex)
                {
                    var fields = Convert.FromHexString(hex);
                    var body = new byte[14 + fields.Length];
                    for (int i = 0; i < 14; i++) body[i] = checked((byte)(0xA0 + i));
                    fields.CopyTo(body, 14); return body;
                }
                var nativeProposal = NativeFields("19F60000010000");
                Check("A21 fixed request retains fourteen opaque nonzero body bytes before its fields", WeddingCommandParser.TryRequest(
                    nativeProposal, out var request) && request.Peer == 63001 && request.Operation == 1
                    && !WeddingCommandParser.TryRequest(Convert.FromHexString("19F60000010000"), out _));
                Check("the real NPC proposal capture parses fields after its opaque stack prefix", WeddingCommandParser.TryRequest(
                    Convert.FromHexString("48EB3A2D94A1B60360F7190067B401000000010000"), out var captured)
                    && captured.Peer == 1 && captured.Operation == 1 && captured.Ceremony == 0 && captured.Delay == 0);
                Check("A21 fixed request rejects truncation, trailing bytes and invalid operation",
                    !WeddingCommandParser.TryRequest(nativeProposal[..^1], out _)
                    && !WeddingCommandParser.TryRequest(nativeProposal.Concat(new byte[] { 0 }).ToArray(), out _)
                    && !WeddingCommandParser.TryRequest(NativeFields("19F60000000000"), out _));
                Check("only reservation carries kind/delay and native mutual-divorce acceptance is recognized",
                    !WeddingCommandParser.TryRequest(NativeFields("19F60000070100"), out _)
                    && WeddingCommandParser.TryRequest(NativeFields("19F60000020202"), out _)
                    && WeddingCommandParser.TryResponse(NativeFields("19F6000007000000"), out var mutual)
                    && mutual.Request.Operation == 7 && mutual.Decision == 0);
                Check("A21 fixed response rejects short forms and unsupported decisions",
                    !WeddingCommandParser.TryResponse(Convert.FromHexString("19F6000007000000"), out _)
                    && !WeddingCommandParser.TryResponse(NativeFields("19F6000007000003"), out _));
                var empty = WeddingPacketBuilder.StateBody(repository.Load(a.CharacterId));
                Check("unmarried A21 projection uses native pair and ring sentinels", empty.Length == 15
                    && BitConverter.ToUInt32(empty) == uint.MaxValue && empty[4] == 0 && empty[14] == 255);
                var result = service.Commit(a, b, b.CharacterId, new(1), () => true);
                var x = repository.Load(a.CharacterId); var y = repository.Load(b.CharacterId);
                Check("engagement is reciprocal and persists a single pair", result.Success && WeddingRules.IsReciprocal(x, y)
                    && x.Status == WeddingStatus.Engaged && Scalar("SELECT COUNT(*) FROM wedding_pairs;") == 1);
                var characters = new SqliteCharacterRepository(db);
                var partnerRecord = characters.GetById(b.CharacterId);
                partnerRecord.Appearance = Array.Empty<CharacterAppearanceEntry>();
                var queryReply = WeddingPacketBuilder.Partner(x, partnerRecord);
                Check("explicit partner query uses native minus-one defaults for every absent appearance slot",
                    queryReply.Length == 113 && queryReply[15] == 1 && BitConverter.ToInt32(queryReply, 16) == 0
                    && BitConverter.ToUInt32(queryReply, 20) == x.PairId
                    && Enumerable.Range(0, 13).All(slot => BitConverter.ToInt32(queryReply, 61 + 4 * slot) == -1));
                partnerRecord.Appearance = new[] { new CharacterAppearanceEntry(12, 123456, 4, new byte[4], 0, 0, 0, 0) };
                queryReply = WeddingPacketBuilder.Partner(x, partnerRecord);
                Check("explicit partner query preserves an equipped display code without zero-filling absent slots",
                    BitConverter.ToInt32(queryReply, 109) == 123456
                    && Enumerable.Range(0, 12).All(slot => BitConverter.ToInt32(queryReply, 61 + 4 * slot) == -1));
                var initRegistry = new InitPacketBuilderRegistry(db);
                var initSource = new SqliteSelectCharacterDataSource(db, characters);
                var initSnapshot = initSource.Load(a.CharacterId, a.AccountId);
                var weddingInitTemplates = NewCharacterInitSequence.Build().Where(t => t.Kind == SelectCharacterPacketTemplateKind.Raw
                    && (t.Command == 1 && t.Type == (ushort)CmdPacketTypeA21.WEDDING_CHARAC
                        || t.Command == 0 && (t.Type == (ushort)NotiPacketTypeA21.WEDDING_INFO
                            || t.Type == (ushort)NotiPacketTypeA21.COUPLE_ROOM
                            || t.Type == (ushort)NotiPacketTypeA21.DIMENSION_GATE_ENTRANCE_INFO))).ToList();
                var weddingInitPackets = SelectCharacterPacketBuilder.BuildPacketStream(initSource, a.CharacterId, a.AccountId,
                    weddingInitTemplates, registry: initRegistry).ToArray();
                Check("real init marriage templates publish state and rooms without a fabricated partner ACK",
                    weddingInitPackets.Count(p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.WEDDING_INFO) == 1
                    && weddingInitPackets.Count(p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.COUPLE_ROOM) == 2
                    && !weddingInitPackets.Any(p => p[0] == 1 && BitConverter.ToUInt16(p, 1) == (ushort)CmdPacketTypeA21.WEDDING_CHARAC));
                Check("init registry has no proactive partner-query builder",
                    !initRegistry.TryBuildCmd((ushort)CmdPacketTypeA21.WEDDING_CHARAC, initSnapshot, out _));
                bool refusedSimpleDelete = false, refusedCompactingDelete = false;
                try { characters.SoftDelete(a.CharacterId); } catch (InvalidOperationException) { refusedSimpleDelete = true; }
                try { characters.SoftDeleteAndCompactSlots(b.AccountId, b.CharacterId, 0); }
                catch (InvalidOperationException) { refusedCompactingDelete = true; }
                Check("both character-deletion transactions reject an active wedding membership",
                    refusedSimpleDelete && refusedCompactingDelete && repository.IsBound(a.CharacterId)
                    && Scalar("SELECT SUM(delete_flag) FROM characters WHERE character_id IN (63001,63002);") == 0);
                Check("duplicate engagement does not create another pair", !service.Commit(a, b, b.CharacterId, new(1), () => true).Success
                    && Scalar("SELECT COUNT(*) FROM wedding_pairs;") == 1 && Scalar("SELECT COUNT(*) FROM wedding_members;") == 2);
                Check("reservation without configured prerequisites preserves money and engagement",
                    !service.Commit(a, b, b.CharacterId, new(2, 0, 0, room, 1), () => true).Success
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Engaged
                    && a.Inventory.GetMainVirtualCount(0).Count == initialGold);
                foreach (int cid in new[] { a.CharacterId, b.CharacterId })
                foreach (int quest in catalog.RequiredQuests)
                    Sql($"INSERT INTO character_quest_completions(character_id,quest_id,completion_value) VALUES({cid},{quest},1);");
                byte kind = Enumerable.Range(0, 3).Select(i => (byte)i).First(i => i != catalog.PaidCeremonyType);
                Sql("CREATE TRIGGER reject_wedding_reservation BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'wedding rollback test'); END;");
                var rejected = service.Commit(a, b, b.CharacterId, new(2, kind, 0, room, 1), () => true);
                Check("late SQL failure rolls back fee, cooldown and both online projections", !rejected.Success
                    && a.Inventory.GetMainVirtualCount(0).Count == initialGold && b.Inventory.GetMainVirtualCount(0).Count == initialGold
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Engaged
                    && Scalar("SELECT COUNT(*) FROM wedding_character_cooldowns;") == 0);
                Sql("DROP TRIGGER reject_wedding_reservation;");
                result = service.Commit(a, b, b.CharacterId, new(2, kind, 0, room, 1), () => true);
                x = repository.Load(a.CharacterId);
                Check("reservation atomically charges the proposer and records the configured deadline", result.Success
                    && x.Status == WeddingStatus.Reserved && x.StartAt == now + catalog.ReservationDelaySeconds[0]
                    && a.Inventory.GetMainVirtualCount(0).Count == initialGold - catalog.Ceremonies[kind].Gold);
                Check("duplicate reservation cannot charge again", !service.Commit(a, b, b.CharacterId, new(2, kind, 0, room, 1), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == initialGold - catalog.Ceremonies[kind].Gold);
                Check("ceremony cannot start before the durable deadline", !service.Commit(a, b, b.CharacterId, new(3), () => true).Success);
                now = x.StartAt;
                Check("ceremony starts at the persisted deadline", service.Commit(a, b, b.CharacterId, new(3), () => true).Success);
                Check("ceremony cannot complete early", !service.Commit(a, b, b.CharacterId, new(4), () => true).Success);
                now += catalog.CeremonyEndSeconds;
                result = service.Commit(a, b, b.CharacterId, new(4), () => true);
                var gift = catalog.Ceremonies[kind].Gift;
                int giftCountA = a.Inventory.CountMainItem(gift.ItemId), giftCountB = b.Inventory.CountMainItem(gift.ItemId);
                Check("completion persists marriage, rings and both configured gifts together", result.Success
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Married
                    && a.Inventory.CountMainItem(gift.ItemId) == gift.Count && b.Inventory.CountMainItem(gift.ItemId) == gift.Count);
                Check("duplicate completion cannot repeat ceremony gifts", !service.Commit(a, b, b.CharacterId, new(4), () => true).Success
                    && a.Inventory.CountMainItem(gift.ItemId) == giftCountA && b.Inventory.CountMainItem(gift.ItemId) == giftCountB);
                byte nextRing = checked((byte)(repository.Load(a.CharacterId).Ring + 1));
                var ringTicket = catalog.RingTickets[nextRing];
                Check("ring upgrade without its configured ticket cannot change the shared ring",
                    !service.Commit(a, b, b.CharacterId, new(8, Slot: 16, Ring: nextRing), () => true).Success
                    && repository.Load(a.CharacterId).Ring == nextRing - 1);
                a.Inventory.SetItem(InventoryListType.Main, 16, new ItemCore
                    { ItemKind = ItemCore.KindConsumable, ItemId = ringTicket.ItemId, Count = ringTicket.Count });
                int currentChecks = 0;
                Check("role replacement before commit restores consumed ring tickets and both projections",
                    service.Commit(a, b, b.CharacterId, new(8, Slot: 16, Ring: nextRing), () => ++currentChecks == 1).Error == -101
                    && a.Inventory.CountMainItem(ringTicket.ItemId) == ringTicket.Count
                    && repository.Load(a.CharacterId).Ring == nextRing - 1);
                Check("ring upgrade consumes the configured ticket and changes both spouses together",
                    service.Commit(a, b, b.CharacterId, new(8, Slot: 16, Ring: nextRing), () => true).Success
                    && repository.Load(a.CharacterId).Ring == nextRing && repository.Load(b.CharacterId).Ring == nextRing
                    && a.Inventory.CountMainItem(ringTicket.ItemId) == 0);
                Check("replayed ring upgrade cannot consume another item or skip a grade",
                    !service.Commit(a, b, b.CharacterId, new(8, Slot: 16, Ring: nextRing), () => true).Success
                    && repository.Load(a.CharacterId).Ring == nextRing);
                var loveItem = catalog.LoveItems.First();
                a.Inventory.SetItem(InventoryListType.Main, 17, new ItemCore
                    { ItemKind = ItemCore.KindConsumable, ItemId = loveItem.Key, Count = 2, ExpireTime = checked((int)now - 1) });
                Check("expired love-point items cannot be used",
                    !service.Commit(a, b, b.CharacterId, new(9, Slot: 17), () => true).Success
                    && a.Inventory.CountMainItem(loveItem.Key) == 2 && repository.Load(a.CharacterId).Love == 0);
                a.Inventory.SetItem(InventoryListType.Main, 17, new ItemCore
                    { ItemKind = ItemCore.KindConsumable, ItemId = loveItem.Key, Count = 2 });
                Sql("CREATE TRIGGER reject_wedding_love BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'love rollback test'); END;");
                Check("SQL failure after love-item consumption restores the item and shared love",
                    !service.Commit(a, b, b.CharacterId, new(9, Slot: 17), () => true).Success
                    && a.Inventory.CountMainItem(loveItem.Key) == 2 && repository.Load(a.CharacterId).Love == 0);
                Sql("DROP TRIGGER reject_wedding_love;");
                Check("love item adds its PVF amount once to the shared pair",
                    service.Commit(a, b, b.CharacterId, new(9, Slot: 17), () => true).Success
                    && a.Inventory.CountMainItem(loveItem.Key) == 1
                    && repository.Load(a.CharacterId).Love == loveItem.Value && repository.Load(b.CharacterId).Love == loveItem.Value);
                Sql($"UPDATE wedding_pairs SET love={catalog.MaximumLove},version=version+1 WHERE pair_id={repository.Load(a.CharacterId).PairId};");
                Check("love cap rejects the item without consuming it",
                    !service.Commit(a, b, b.CharacterId, new(9, Slot: 17), () => true).Success
                    && a.Inventory.CountMainItem(loveItem.Key) == 1 && repository.Load(a.CharacterId).Love == catalog.MaximumLove);
                int beforeDivorceA = a.Inventory.GetMainVirtualCount(0).Count, beforeDivorceB = b.Inventory.GetMainVirtualCount(0).Count;
                Check("mutual divorce cannot debit the spouse without validated invitation acceptance", !service.Commit(a, b, b.CharacterId, new(7), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforeDivorceA && b.Inventory.GetMainVirtualCount(0).Count == beforeDivorceB);
                result = service.Commit(a, null, b.CharacterId, new(6), () => true);
                Check("unilateral divorce updates both memberships and only the payer's wallet", result.Success
                    && repository.Load(a.CharacterId).Status == WeddingStatus.None && repository.Load(b.CharacterId).Status == WeddingStatus.None
                    && a.Inventory.GetMainVirtualCount(0).Count == beforeDivorceA - catalog.Alimony[1]
                    && b.Inventory.GetMainVirtualCount(0).Count == beforeDivorceB && Scalar("SELECT COUNT(*) FROM wedding_members;") == 0);
                Check("divorce cooldown blocks a new engagement", service.Commit(a, b, b.CharacterId, new(1), () => true).Error == (int)WeddingError.Cooldown);
                Check("stale session cannot write wedding state", service.Commit(a, b, b.CharacterId, new(1), () => false).Error == -101);

                now += checked((long)Math.Max(catalog.RemarryDays, catalog.WeddingCooldownDays) * 86400 + 1);
                Check("engagement becomes available after the configured divorce cooldown",
                    service.Commit(a, b, b.CharacterId, new(1), () => true).Success);
                int beforePaidGold = a.Inventory.GetMainVirtualCount(0).Count;
                byte paidKind = catalog.PaidCeremonyType;
                Check("paid ceremony without a ticket rolls back its fee",
                    !service.Commit(a, b, b.CharacterId, new(2, paidKind, 0, room, 1), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforePaidGold
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Engaged);
                a.Inventory.SetItem(InventoryListType.Main, 16, new ItemCore { ItemKind = ItemCore.KindConsumable,
                    ItemId = catalog.PaidTicket.ItemId, Count = catalog.PaidTicket.Count, ExpireTime = checked((int)now - 1) });
                Check("expired paid ceremony tickets cannot satisfy the reservation",
                    !service.Commit(a, b, b.CharacterId, new(2, paidKind, 0, room, 1), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforePaidGold
                    && a.Inventory.CountMainItem(catalog.PaidTicket.ItemId) == catalog.PaidTicket.Count);
                a.Inventory.SetItem(InventoryListType.Main, 16, new ItemCore { ItemKind = ItemCore.KindConsumable,
                    ItemId = catalog.PaidTicket.ItemId, Count = catalog.PaidTicket.Count });
                Check("paid ceremony consumes exactly its configured fee and ticket",
                    service.Commit(a, b, b.CharacterId, new(2, paidKind, 0, room, 1), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforePaidGold - catalog.Ceremonies[paidKind].Gold
                    && a.Inventory.CountMainItem(catalog.PaidTicket.ItemId) == 0);
                x = repository.Load(a.CharacterId);
                long paidStart = x.StartAt, paidPair = x.PairId;
                // Reservation preview succeeded with free space; filling the bag afterwards
                // must not make the timed marriage impossible to complete.
                for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
                    a.Inventory.SetItem(InventoryListType.Main, slot, new ItemCore
                        { ItemKind = ItemCore.KindConsumable, ItemId = 10004024, Count = 1 });
                now = paidStart;
                Check("ceremony can start even when a reserved participant later fills the bag",
                    service.Commit(a, b, b.CharacterId, new(3), () => true).Success);
                now += catalog.CeremonyEndSeconds;
                var paidGift = catalog.Ceremonies[paidKind].Gift;
                int previousPeerGift = b.Inventory.CountMainItem(paidGift.ItemId);
                Sql("CREATE TRIGGER reject_wedding_mail BEFORE INSERT ON mailbox_system_mail_audit BEGIN SELECT RAISE(ABORT,'mail rollback test'); END;");
                Check("overflow mail failure rolls back ceremony completion and both rewards",
                    !service.Commit(a, b, b.CharacterId, new(4), () => true).Success
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Ceremony
                    && b.Inventory.CountMainItem(paidGift.ItemId) == previousPeerGift
                    && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 0);
                Sql("DROP TRIGGER reject_wedding_mail;");
                result = service.Commit(a, b, b.CharacterId, new(4), () => true);
                Check("full bag completes marriage using one durable gift mail while the peer receives the direct gift",
                    result.Success && result.GiftsMailed && repository.Load(a.CharacterId).Status == WeddingStatus.Married
                    && a.Inventory.CountMainItem(paidGift.ItemId) == 0
                    && b.Inventory.CountMainItem(paidGift.ItemId) == previousPeerGift + paidGift.Count
                    && Scalar($"SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE idempotency_key='wedding-ceremony:{paidPair}:{a.CharacterId}';") == 1);
                var recovery = new WeddingCeremonyRecoveryService(db);
                recovery.Advance(a.CharacterId, paidPair, paidStart, now);
                Check("old completion and recovery callbacks cannot repeat overflow gifts",
                    !service.Commit(a, b, b.CharacterId, new(4), () => true).Success
                    && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 1);
                beforeDivorceA = a.Inventory.GetMainVirtualCount(0).Count;
                beforeDivorceB = b.Inventory.GetMainVirtualCount(0).Count;
                Check("mutual divorce with validated invitation acceptance atomically debits both spouses",
                    service.Commit(a, b, b.CharacterId, new(7, PartnerPresent: true), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforeDivorceA - catalog.Alimony[1]
                    && b.Inventory.GetMainVirtualCount(0).Count == beforeDivorceB - catalog.Alimony[1]);

                for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
                    a.Inventory.RemoveItem(InventoryListType.Main, slot);
                a.Inventory.SetMainVirtualCount(0, initialGold); b.Inventory.SetMainVirtualCount(0, initialGold);
                now += checked((long)Math.Max(catalog.RemarryDays, catalog.WeddingCooldownDays) * 86400 + 1);
                Check("another ceremony can be reserved after both durable cooldowns",
                    service.Commit(a, b, b.CharacterId, new(1), () => true).Success
                    && service.Commit(a, b, b.CharacterId, new(2, kind, 0, room, 1), () => true).Success);
                x = repository.Load(a.CharacterId);
                long offlinePair = x.PairId, offlineStart = x.StartAt;
                Check("recovery rejects callbacks from another pair or reservation deadline",
                    recovery.Advance(a.CharacterId, paidPair, paidStart, now).Status == WeddingStatus.Reserved
                    && recovery.Advance(a.CharacterId, offlinePair, offlineStart + 1, now).Status == WeddingStatus.Reserved);
                Check("offline recovery cannot start before its persisted deadline",
                    recovery.Advance(a.CharacterId, offlinePair, offlineStart, offlineStart - 1).Status == WeddingStatus.Reserved);
                now = offlineStart;
                Check("recovery of a completed ceremony requires a previously accepted start",
                    service.Commit(a, b, b.CharacterId, new(3), () => true).Success);
                now = offlineStart + catalog.CeremonyEndSeconds;
                Sql("CREATE TRIGGER reject_second_wedding_mail BEFORE INSERT ON mailbox_system_mail_audit WHEN NEW.receiver_character_id=63002 BEGIN SELECT RAISE(ABORT,'second recipient rollback test'); END;");
                bool failedRecovery = false;
                try { recovery.Advance(a.CharacterId, offlinePair, offlineStart, now); }
                catch (SqliteException) { failedRecovery = true; }
                Check("second spouse mail failure rolls back the first mail and all overdue ceremony transitions",
                    failedRecovery && repository.Load(a.CharacterId).Status == WeddingStatus.Ceremony
                    && Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 1);
                Sql("DROP TRIGGER reject_second_wedding_mail;");
                int offlineGiftA = a.Inventory.CountMainItem(gift.ItemId), offlineGiftB = b.Inventory.CountMainItem(gift.ItemId);
                result = null;
                var recovered = recovery.Advance(a.CharacterId, offlinePair, offlineStart, now);
                Check("overdue recovery commits reciprocal marriage and two independently addressed Chinese-name gifts",
                    recovered.Status == WeddingStatus.Married && WeddingRules.IsReciprocal(recovered, repository.Load(b.CharacterId))
                    && Scalar($"SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE idempotency_key IN ('wedding-ceremony:{offlinePair}:63001','wedding-ceremony:{offlinePair}:63002') AND receiver_name IN ('新郎','新娘');") == 2
                    && a.Inventory.CountMainItem(gift.ItemId) == offlineGiftA && b.Inventory.CountMainItem(gift.ItemId) == offlineGiftB);
                recovery.Advance(b.CharacterId, offlinePair, offlineStart, now + 10);
                Check("repeated recovery from either spouse cannot send duplicate ceremony mails",
                    Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit;") == 3);
                var feeSection = new System.Text.RegularExpressions.Regex(@"(?s)(\[alimony\]\s*)\d+\s+\d+\s*(\[/alimony\])");
                string feeScript = PvfArchiveAccessor.ReadText(WeddingCatalog.PvfPath);
                if (feeSection.Matches(feeScript).Count != 1)
                    throw new InvalidOperationException("Cannot locate the PVF fee fixture.");
                var feeCatalog = WeddingCatalog.Parse(feeSection.Replace(feeScript, "${1}91001 270003\n${2}", 1));
                var feeService = new WeddingCommitService(feeCatalog, () => now);
                beforeDivorceA = a.Inventory.GetMainVirtualCount(0).Count;
                beforeDivorceB = b.Inventory.GetMainVirtualCount(0).Count;
                Check("different configured marriage fees use the married stage for both consenting spouses",
                    feeService.Commit(a, b, b.CharacterId, new(7, PartnerPresent: true), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == beforeDivorceA - 270003
                    && b.Inventory.GetMainVirtualCount(0).Count == beforeDivorceB - 270003);
                db.Write((c, t) =>
                {
                    WeddingRepository.Create(c, t, a.CharacterId, b.CharacterId, now);
                });
                int cancellationGold = 91001 + 12345;
                a.Inventory.SetMainVirtualCount(0, cancellationGold);
                b.Inventory.SetMainVirtualCount(0, 98765);
                if (!InventoryPersistenceService.SaveDirty(a) || !InventoryPersistenceService.SaveDirty(b))
                    throw new InvalidOperationException("Cannot prepare cancellation wallets.");
                Sql("CREATE TRIGGER reject_wedding_cancellation BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'cancellation rollback'); END;");
                Check("late cancellation failure restores the charged wallet and both reciprocal memberships",
                    !feeService.Commit(a, b, b.CharacterId, new(5), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == cancellationGold
                    && b.Inventory.GetMainVirtualCount(0).Count == 98765
                    && WeddingRules.IsReciprocal(repository.Load(a.CharacterId), repository.Load(b.CharacterId))
                    && repository.Load(a.CharacterId).Status == WeddingStatus.Engaged);
                Sql("DROP TRIGGER reject_wedding_cancellation;");
                Check("engagement cancellation can charge the actor while the partner is offline",
                    feeService.Commit(a, null, b.CharacterId, new(5), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == 12345
                    && b.Inventory.GetMainVirtualCount(0).Count == 98765
                    && repository.Load(a.CharacterId).Status == WeddingStatus.None
                    && repository.Load(b.CharacterId).Status == WeddingStatus.None);
                Check("repeating cancellation cannot charge its actor twice",
                    !feeService.Commit(a, null, b.CharacterId, new(5), () => true).Success
                    && a.Inventory.GetMainVirtualCount(0).Count == 12345);
                InventoryContext.Register(replacementSessionA, InventoryService.LoadFromDb(connection, a.CharacterId, a.AccountId, db));
                Check("replaced owned lease is rejected even when the caller's predicate says current",
                    service.Commit(a, b, b.CharacterId, new(9, Slot: 17), () => true).Error == -101);
                Sql("INSERT INTO characters(character_id,account_id,name,job,level,slot_index) VALUES(63003,63001,'可删除',0,1,1);");
                characters.SoftDeleteAndCompactSlots(63001, 63003, 1);
                Check("character deletion remains available to unbound characters",
                    Scalar("SELECT delete_flag FROM characters WHERE character_id=63003;") == 1);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                InventoryContext.Unregister(sessionA, 63001); InventoryContext.Unregister(sessionB, 63002);
                InventoryContext.Unregister(replacementSessionA, 63001);
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_COMMIT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }
    }
}
