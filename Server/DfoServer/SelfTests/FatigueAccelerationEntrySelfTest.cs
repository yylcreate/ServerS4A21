using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events;
using DfoServer.Game.Events.FatigueAcceleration;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class FatigueAccelerationEntrySelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== FATIGUE_ACCELERATION_ENTRY selftest ===");
            var assertions = 0; var failures = 0;
            void Check(string name, bool passed)
            {
                assertions++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
                if (!passed) failures++;
            }
            void Section(string name, Action test)
            {
                try { test(); }
                catch (Exception ex) { Check(name + " exception: " + ex, false); }
            }
            Section("wire contract", () => VerifyWire(Check));
            Section("entry authority", () => VerifyRepository(Check));
            Section("registered runtime", () => VerifyRegistration(Check));
            Section("request behavior", () => VerifyRequestsAsync(Check).GetAwaiter().GetResult());
            Section("frozen effects and fatigue interface", () => VerifyEffects(Check));
            Section("identity fences", () => VerifyFencesAsync(Check).GetAwaiter().GetResult());
            Console.WriteLine($"FATIGUE_ACCELERATION_ENTRY assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyWire(Action<string, bool> check)
        {
            check("parser accepts exactly the two observed one-byte switches",
                FatigueAccelerationRequestParser.TryParse(new byte[] { 0 }, out var stopped) && !stopped
                && FatigueAccelerationRequestParser.TryParse(new byte[] { 1 }, out var started) && started);
            foreach (var body in new[] { null, Array.Empty<byte>(), new byte[] { 2 },
                new byte[] { 255 }, new byte[] { 1, 0 }, new byte[14], new byte[1024] })
                check("parser rejects malformed or unknown switch " + (body?.Length ?? -1),
                    !FatigueAccelerationRequestParser.TryParse(body, out var enabled) && !enabled);
            foreach (var item in new[] { (FatigueAccelerationEntryResult.Stopped, -1),
                (FatigueAccelerationEntryResult.Started, -1),
                (FatigueAccelerationEntryResult.InvalidRequest, 4), (FatigueAccelerationEntryResult.NotInTown, 19),
                (FatigueAccelerationEntryResult.EntryClosed, 179),
                (FatigueAccelerationEntryResult.DefinitionUnavailable, 4),
                (FatigueAccelerationEntryResult.EntryUnavailable, 4) })
                check("complete A21 command acknowledgement: " + item.Item1,
                    Ack(FatigueAccelerationPacketBuilder.BuildAck(item.Item1), item.Item2));
            var unknownRejected = false;
            try { FatigueAccelerationPacketBuilder.BuildAck((FatigueAccelerationEntryResult)int.MaxValue); }
            catch (ArgumentOutOfRangeException) { unknownRejected = true; }
            check("unknown domain results cannot fabricate a protocol response", unknownRejected);
            check("inactive state uses the independent NOTI enum and exactly U16 plus U8",
                Inactive(FatigueAccelerationPacketBuilder.BuildInactivePacket())
                && FatigueAccelerationPacketBuilder.BuildInactiveBody().SequenceEqual(new byte[3]));
            check("active state uses only the proven state1 and preserves zero U16",
                Active(FatigueAccelerationPacketBuilder.BuildStatePacket(true))
                && FatigueAccelerationPacketBuilder.BuildStateBody(true).SequenceEqual(new byte[] { 0, 0, 1 }));
            foreach (var setting in new[] { null, "", " \t", "0", "1" })
                check("entry setting accepts missing or exact switch " + setting,
                    FatigueAccelerationEntryService.TryParseEntrySetting(setting, out var value)
                    && value == (setting == "0" ? false : setting == "1" ? true : (bool?)null));
            foreach (var setting in new[] { "true", "2", " 1", "1 ", "00", "1.0" })
                check("entry setting rejects ambiguous value " + setting,
                    !FatigueAccelerationEntryService.TryParseEntrySetting(setting, out _));
        }

        private static void VerifyRepository(Action<string, bool> check)
        {
            using var fixture = new Fixture(initialize: false);
            var db = fixture.Database;
            var repo = new FatigueAccelerationEntryRepository(db);
            var schema = Scalar(db, "PRAGMA user_version;");
            repo.EnsureEntry(null); repo.EnsureEntry(null);
            check("a missing switch stays closed and initialization is idempotent",
                !repo.IsEnabled() && Scalar(db, "SELECT COUNT(*) FROM game_event_state WHERE event_id=115;") == 1
                && Scalar(db, "SELECT COUNT(*) FROM game_event_info_details WHERE event_id=115;") == 1);
            Sql(db, "UPDATE game_event_state SET state=1 WHERE event_id=115;"
                + "UPDATE game_event_info_details SET unknown0=42,start_notice='operator',title='saved',flag_a=17 WHERE event_id=115;");
            var metadata = Text(db, "SELECT unknown0 || ':' || start_notice || ':' || title || ':' || flag_a FROM game_event_info_details WHERE event_id=115;");
            fixture.Service.Initialize(null); fixture.Service.Initialize(" ");
            check("missing settings preserve the saved switch and operator metadata", repo.IsEnabled()
                && metadata == Text(db, "SELECT unknown0 || ':' || start_notice || ':' || title || ':' || flag_a FROM game_event_info_details WHERE event_id=115;"));
            fixture.Service.Initialize("0"); fixture.Service.Initialize(null);
            check("explicit disable persists across a new owner without replacing details", !repo.IsEnabled()
                && !new FatigueAccelerationEntryRepository(new GameDatabase(db.DatabasePath, db.SchemaFilePath)).IsEnabled()
                && metadata == Text(db, "SELECT unknown0 || ':' || start_notice || ':' || title || ':' || flag_a FROM game_event_info_details WHERE event_id=115;"));
            fixture.Service.Initialize("1"); fixture.Service.Initialize("invalid");
            check("invalid operator setting closes an existing enabled entry", !repo.IsEnabled());
            fixture.Service.Initialize("1");
            Sql(db, "INSERT INTO game_event_state(event_id,state) VALUES(93,1);"
                + "INSERT INTO game_event_info_details(event_id,start_notice,sort_order) VALUES(93,'other',1);"
                + "INSERT INTO game_event_info_extra(event_id,param7) VALUES(93,777);");
            var snapshot = new GameEventRepository(db).LoadEventInfoSnapshot();
            var body = EventInfoBodyBuilder.Build(snapshot);
            using (var reader = new BinaryReader(new MemoryStream(body)))
            {
                var ids = new List<ushort>();
                var count = reader.ReadUInt16();
                for (var i = 0; i < count; i++)
                {
                    ids.Add(reader.ReadUInt16()); reader.ReadUInt32();
                    for (var j = 0; j < 2; j++) reader.ReadBytes(reader.ReadInt32());
                    if (reader.ReadByte() != 0) throw new InvalidDataException("Unexpected fixture detail row.");
                }
                var extras = reader.ReadByte(); var extraId = reader.ReadUInt16();
                var parameters = Enumerable.Range(0, 12).Select(_ => reader.ReadUInt32()).ToArray();
                check("full EVENT_INFO preserves other activities, extras and the built-in raid entry",
                    ids.OrderBy(x => x).SequenceEqual(new ushort[] { 93, 115, EventInfoBodyBuilder.RaidChannelEventId })
                    && extras == 1 && extraId == 93 && parameters[7] == 777
                    && reader.BaseStream.Position == reader.BaseStream.Length);
            }
            Sql(db, "DELETE FROM game_event_state WHERE event_id=115;"
                + "CREATE TRIGGER reject_fatigue_detail BEFORE INSERT ON game_event_info_details WHEN NEW.event_id=115 "
                + "BEGIN SELECT RAISE(ABORT,'fatigue-test-rollback'); END;");
            var rejected = false;
            try { repo.EnsureEntry(true); } catch (SqliteException) { rejected = true; }
            check("detail failure rolls back both inserted rows and leaves unrelated events intact",
                rejected && Scalar(db, "SELECT COUNT(*) FROM game_event_state WHERE event_id=115;") == 0
                && Scalar(db, "SELECT COUNT(*) FROM game_event_info_details WHERE event_id=115;") == 0
                && Scalar(db, "SELECT state FROM game_event_state WHERE event_id=93;") == 1);
            Sql(db, "DROP TRIGGER reject_fatigue_detail;"); repo.EnsureEntry(true);
            check("rolled-back initialization can be retried without schema mutation",
                repo.IsEnabled() && Scalar(db, "PRAGMA user_version;") == schema);
            var invalid = new FatigueAccelerationEntryService(db, () => FatigueAccelerationConfigFile.Parse(null, null));
            invalid.Initialize("1");
            check("invalid resource definition overrides an enabled setting and fails closed",
                !repo.IsEnabled() && invalid.Evaluate(true, true, true) == FatigueAccelerationEntryResult.DefinitionUnavailable
                && invalid.Evaluate(true, false, true) == FatigueAccelerationEntryResult.Stopped);
        }

        private static void VerifyRegistration(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            using var runtime = new ServerRuntimeBuilder(fixture.Database);
            var protocol = runtime.BuildGameProtocolHandler(fixture.Sessions);
            var registry = (GameCommandRegistry)typeof(GameProtocolHandler)
                .GetField("_cmdDispatch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(protocol);
            check("real composition root binds the A21 fatigue command to its dedicated handler",
                registry.TryGetValue((ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE, out var handler)
                && handler.Target is EventFatigueAccelerationHandler && handler.Method.Name == "HandleAsync");
        }

        private static async Task VerifyRequestsAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture(); var session = fixture.CreateSession();
            var gold = fixture.Inventory.GetMainVirtualCount(0).Count;
            var schema = Scalar(fixture.Database, "PRAGMA user_version;");
            await fixture.Dispatch(session, new byte[] { 0 }); await fixture.Dispatch(session, new byte[] { 0 });
            check("stopping twice replies each time with success and inactive state",
                fixture.Packets.Count == 4 && Ack(fixture.Packets[0], -1) && Inactive(fixture.Packets[1])
                && Ack(fixture.Packets[2], -1) && Inactive(fixture.Packets[3]));
            fixture.Packets.Clear(); await fixture.Dispatch(session, new byte[] { 1 });
            check("valid start succeeds and projects the accepted session state",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], -1) && Active(fixture.Packets[1])
                && session.GameSession.FatigueAcceleration.IsEnabled(session.Player.CharacterId));
            fixture.Packets.Clear(); await fixture.Dispatch(session, new byte[] { 1 });
            check("duplicate start succeeds without a second effect owner or cost",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], -1) && Active(fixture.Packets[1]));
            fixture.Packets.Clear(); await fixture.Dispatch(session, new byte[] { 2 });
            check("invalid request preserves the previous accepted state",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], 4) && Active(fixture.Packets[1])
                && session.GameSession.FatigueAcceleration.IsEnabled(session.Player.CharacterId));
            fixture.Service.Initialize("0"); fixture.Packets.Clear();
            await fixture.Dispatch(session, new byte[] { 1 });
            check("closed catalog preserves an accepted preference without granting a new run bonus",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], 179) && Active(fixture.Packets[1])
                && fixture.Service.CaptureEffects(session.GameSession.FatigueAcceleration,
                    session.Player.CharacterId, 104, new DungeonFile()).ExperienceBonusPercent == 0);
            fixture.Packets.Clear();
            await fixture.Dispatch(session, new byte[] { 0 }); await fixture.Dispatch(session, new byte[] { 1 });
            check("closed entry still allows stopping but rejects starting without an active-state packet",
                fixture.Packets.Count == 4 && Ack(fixture.Packets[0], -1) && Inactive(fixture.Packets[1])
                && Ack(fixture.Packets[2], 179) && Inactive(fixture.Packets[3]));
            fixture.Packets.Clear(); await fixture.Dispatch(session, new byte[] { 2 });
            check("invalid requests get error4 and cannot activate", fixture.Packets.Count == 2
                && Ack(fixture.Packets[0], 4) && Inactive(fixture.Packets[1]));
            fixture.Packets.Clear(); session.Player.TownPresenceReady = false;
            await fixture.Dispatch(session, new byte[] { 0 });
            check("non-town request gets error19 without town state projection",
                fixture.Packets.Count == 1 && Ack(fixture.Packets[0], 19));
            session.Player.TownPresenceReady = true; fixture.Packets.Clear();
            foreach (var mode in new[] { "run", "user-state", "pending-selection", "selection" })
            {
                if (mode == "run") session.Player.CurrentRun = new DungeonRun();
                if (mode == "user-state") session.Player.UserState = 1;
                if (mode == "pending-selection") session.Player.DungeonSelectionPending = true;
                if (mode == "selection") session.Player.BeginDungeonSelection(default);
                await fixture.Dispatch(session, new byte[] { 0 });
                check("town readiness rejects " + mode, fixture.Packets.Count == 1 && Ack(fixture.Packets[0], 19));
                session.Player.CurrentRun = null; session.Player.UserState = 0;
                session.Player.DungeonSelectionPending = false; session.Player.ClearDungeonSelection(); fixture.Packets.Clear();
            }
            await fixture.Dispatch(session, new byte[] { 0 }, cmd: 0);
            await fixture.Dispatch(session, new byte[] { 0 }, type: (ushort)CmdPacketTypeA21.CHECK_USER_CONNECTION);
            check("wrong packet category and opcode are not processed", fixture.Packets.Count == 0);
            check("entry requests leave gold, inventory, characters and schema untouched",
                fixture.Inventory.GetMainVirtualCount(0).Count == gold
                && fixture.Inventory.DirtyMainVirtualCountSlots.Count == 0
                && fixture.Inventory.DirtyListTypes.Count == 0 && fixture.Inventory.DirtyListParams.Count == 0
                && Scalar(fixture.Database, "SELECT COUNT(*) FROM character_inventory_items;") == 0
                && Scalar(fixture.Database, "SELECT COUNT(*) FROM characters;") == 0
                && Scalar(fixture.Database, "PRAGMA user_version;") == schema);
            fixture.FailNextWrite = true;
            await fixture.Dispatch(session, new byte[] { 0 });
            var failed = fixture.Packets.Count == 0;
            await fixture.Dispatch(session, new byte[] { 0 });
            check("failed transport leaves no delivered record and permits retry", failed
                && fixture.Packets.Count == 2 && Ack(fixture.Packets[0], -1) && Inactive(fixture.Packets[1]));
            fixture.Service.Initialize("1"); fixture.Packets.Clear(); fixture.FailNextWrite = true;
            await fixture.Dispatch(session, new byte[] { 1 });
            check("an accepted start survives an ACK write failure for idempotent retry",
                fixture.Packets.Count == 0 && session.GameSession.FatigueAcceleration.IsEnabled(session.Player.CharacterId));
            await fixture.Dispatch(session, new byte[] { 1 });
            check("retry after accepted start projects success and the same active state",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], -1) && Active(fixture.Packets[1]));
            fixture.Packets.Clear(); await fixture.Dispatch(session, new byte[] { 0 });
            fixture.Packets.Clear(); fixture.BeforePrepare = () => fixture.Service.Initialize("0");
            await fixture.Dispatch(session, new byte[] { 1 });
            check("catalog closure while waiting for send rejects activation at preparation",
                fixture.Packets.Count == 2 && Ack(fixture.Packets[0], 179) && Inactive(fixture.Packets[1])
                && !session.GameSession.FatigueAcceleration.IsEnabled(session.Player.CharacterId));
            using var invalid = new Fixture(validDefinition: false); var invalidSession = invalid.CreateSession();
            await invalid.Dispatch(invalidSession, new byte[] { 1 });
            check("invalid definition returns failure, inactive projection and visible notice",
                invalid.Packets.Count == 3 && Ack(invalid.Packets[0], 4) && Inactive(invalid.Packets[1])
                && Envelope(invalid.Packets[2], 0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE));
            using var unavailable = new Fixture(); var unavailableSession = unavailable.CreateSession();
            // Deliberate isolated schema fault; never target a running/player database.
            Sql(unavailable.Database, "DROP TABLE game_event_state;");
            await unavailable.Dispatch(unavailableSession, new byte[] { 1 });
            check("entry read failure is generic failure plus notice, not a false closed-event result",
                unavailable.Packets.Count == 3 && Ack(unavailable.Packets[0], 4) && Inactive(unavailable.Packets[1])
                && Envelope(unavailable.Packets[2], 0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE));
        }

        private static void VerifyEffects(Action<string, bool> check)
        {
            using var fixture = new Fixture(); var session = fixture.CreateSession();
            var cid = session.Player.CharacterId;
            var runtime = session.GameSession.FatigueAcceleration;
            var dungeon = new DungeonFile();
            var stopped = fixture.Service.CaptureEffects(runtime, cid, 104, dungeon);
            check("inactive run has no extra experience and a neutral consumption interface",
                stopped.ExperienceBonusPercent == 0 && stopped.FatigueConsumptionMultiplier == 1);
            runtime.SetEnabled(cid, true);
            var active = fixture.Service.CaptureEffects(runtime, cid, 104, dungeon);
            check("server policy gives additive 100 percent and future double consumption",
                active.ExperienceBonusPercent == 100 && active.FatigueConsumptionMultiplier == 2);
            check("another character never inherits a session toggle",
                !runtime.IsEnabled(cid + 1)
                && fixture.Service.CaptureEffects(runtime, cid + 1, 104, dungeon).ExperienceBonusPercent == 0);
            check("PVF excluded dungeon and no-fatigue definitions remain neutral",
                fixture.Service.CaptureEffects(runtime, cid, 3557, dungeon).ExperienceBonusPercent == 0
                && fixture.Service.CaptureEffects(runtime, cid, 104, new DungeonFile { NoFatigue = true }).FatigueConsumptionMultiplier == 1
                && fixture.Service.CaptureEffects(runtime, cid, 104, null).ExperienceBonusPercent == 0);
            var snapshot = new DungeonParticipantExperienceBonusSnapshot(1, false, false,
                fatigueBurnExperienceBonusPercent: active.ExperienceBonusPercent,
                fatigueConsumptionMultiplier: active.FatigueConsumptionMultiplier);
            var story = snapshot.WithStoryExperienceProfile(50, 2);
            check("story profile preserves burn effects and the future fatigue-cost interface",
                story.FatigueBurnExperienceBonusPercent == 100 && story.FatigueConsumptionMultiplier == 2
                && story.StoryExperienceBonusRatePercent == 50 && story.ScaleFatigueConsumption(3) == 6
                && story.ScaleFatigueConsumption(0) == 0
                && DungeonParticipantExperienceBonusSnapshot.None.ScaleFatigueConsumption(3) == 3);
            var overflowRejected = false;
            try { story.ScaleFatigueConsumption(uint.MaxValue); }
            catch (OverflowException) { overflowRejected = true; }
            check("future fatigue consumer receives an overflow instead of a wrapped debit", overflowRejected);
            var plan = DungeonEntryExperienceBonusPlan.Capture(session, null, fixture.Sessions, 1,
                fixture.Service, 104);
            runtime.SetEnabled(cid, false);
            fixture.Service.Initialize("0");
            check("entry snapshot survives later stop and catalog closure",
                plan.ForParticipant(session).FatigueBurnExperienceBonusPercent == 100
                && plan.ForParticipant(session).FatigueConsumptionMultiplier == 2
                && fixture.Service.CaptureEffects(runtime, cid, 104, dungeon).ExperienceBonusPercent == 0);
            runtime.SetEnabled(cid, true);
            check("a closed catalog grants no effects to a newly captured run",
                fixture.Service.CaptureEffects(runtime, cid, 104, dungeon).ExperienceBonusPercent == 0);
            fixture.Service.Initialize("1");
            var oldLeasePlan = DungeonEntryExperienceBonusPlan.Capture(session, null, fixture.Sessions, 1,
                fixture.Service, 104);
            InventoryContext.Register(session.SessionId, fixture.Inventory);
            check("owned lease replacement invalidates the old entry snapshot",
                oldLeasePlan.ForParticipant(session).FatigueBurnExperienceBonusPercent == 0);
            var next = DungeonEntryExperienceBonusPlan.Capture(session, null, fixture.Sessions, 1,
                fixture.Service, 104);
            var oldOwner = session.GameSession;
            session.GameSession = new GameSession(session, fixture.Database);
            check("character selection owner replacement drops both old toggle and old entry plan",
                oldOwner.FatigueAcceleration.IsEnabled(cid)
                && !session.GameSession.FatigueAcceleration.IsEnabled(cid)
                && next.ForParticipant(session).FatigueBurnExperienceBonusPercent == 0);
            var custom = new FatigueAccelerationEntryService(fixture.Database, () => Definition(),
                new FatigueAccelerationEffectProfile(50, 3));
            session.GameSession.FatigueAcceleration.SetEnabled(cid, true);
            var configured = custom.CaptureEffects(session.GameSession.FatigueAcceleration, cid, 104, dungeon);
            check("rates belong to injectable server policy rather than copied PVF tables",
                configured.ExperienceBonusPercent == 50 && configured.FatigueConsumptionMultiplier == 3);
            var invalidPolicyRejected = false;
            try { _ = new FatigueAccelerationEntryService(fixture.Database, () => Definition(),
                default(FatigueAccelerationEffectProfile)); }
            catch (ArgumentException) { invalidPolicyRejected = true; }
            check("explicit default struct cannot bypass policy validation", invalidPolicyRejected);

            using var other = new Fixture(); var follower = other.CreateSession();
            follower.Player.UserId++;
            fixture.Sessions.Register(follower.Player.CharacterId, follower);
            var party = new Game.Party.Party(9);
            party.TryAddMember(new Game.Party.PartyMember { CharacterId = cid,
                UserId = session.Player.UserId, SessionId = session.SessionId });
            party.TryAddMember(new Game.Party.PartyMember { CharacterId = follower.Player.CharacterId,
                UserId = follower.Player.UserId, SessionId = follower.SessionId });
            var partyPlan = DungeonEntryExperienceBonusPlan.Capture(session, party, fixture.Sessions, 2,
                fixture.Service, 104);
            check("each party participant freezes their own switch instead of the leader's switch",
                party.Count == 2 && partyPlan.ForParticipant(session).FatigueBurnExperienceBonusPercent == 100
                && partyPlan.ForParticipant(follower).FatigueBurnExperienceBonusPercent == 0);
        }

        private static async Task VerifyFencesAsync(Action<string, bool> check)
        {
            var changes = new (string Name, Action<Fixture, EnhancedClientSession> Change)[]
            {
                ("UID", (_, s) => s.Player.UserId++),
                ("character", (_, s) => s.Player.CharacterId++),
                ("account identity", (_, s) => s.Account = new AccountRecord { AccountId = 1 }),
                ("account ID", (_, s) => s.Account.AccountId++),
                ("GameSession", (_, s) => s.GameSession = Identity()),
                ("directory and lease replacement", (f, _) => f.CreateSession()),
                ("owned lease removed", (_, s) => InventoryContext.Unregister(s.SessionId)),
                ("owned lease version replaced", (f, s) => InventoryContext.Register(s.SessionId, f.Inventory)),
                ("run epoch", (_, s) => s.Player.NextDungeonRunGeneration()),
                ("selection ABA", (_, s) => { s.Player.BeginDungeonSelection(default); s.Player.ClearDungeonSelection(); }),
                ("town left", (_, s) => s.Player.TownPresenceReady = false),
            };
            foreach (var change in changes)
            {
                using (var queued = new Fixture())
                {
                    var session = queued.CreateSession();
                    var gate = await queued.Transitions.AcquireAsync(session.Player.CharacterId);
                    Task request;
                    try { request = queued.Dispatch(session, new byte[] { 1 }); change.Change(queued, session); }
                    finally { gate.Dispose(); }
                    await request;
                    // Town is evaluated after the transition gate; its current rejection is legitimate.
                    check("queued request rechecks " + change.Name, change.Name == "town left"
                        ? queued.Packets.Count == 1 && Ack(queued.Packets[0], 19) : queued.Packets.Count == 0);
                }
                foreach (var phase in new[] { "prepare", "pre-send", "per-frame" })
                {
                    using var fixture = new Fixture(); var session = fixture.CreateSession();
                    Action mutation = () => change.Change(fixture, session);
                    if (phase == "prepare") fixture.BeforePrepare = mutation;
                    if (phase == "pre-send") fixture.BeforeWrite = mutation;
                    if (phase == "per-frame") fixture.AfterFirstWrite = mutation;
                    await fixture.Dispatch(session, new byte[] { 1 });
                    check(phase + " source fence rejects stale " + change.Name,
                        fixture.Packets.Count == (phase == "per-frame" ? 1 : 0)
                        && (phase == "prepare" || fixture.CancelledWrites == 1)
                        && (phase != "per-frame" || Ack(fixture.Packets[0], -1)));
                }
            }
            foreach (var missing in new[] { "UID", "character", "account", "GameSession", "lease" })
            {
                using var fixture = new Fixture(); var session = fixture.CreateSession();
                if (missing == "UID") session.Player.UserId = 0;
                if (missing == "character") session.Player.CharacterId = 0;
                if (missing == "account") session.Account = null;
                if (missing == "GameSession") session.GameSession = null;
                if (missing == "lease") InventoryContext.Unregister(session.SessionId);
                await fixture.Dispatch(session, new byte[] { 0 });
                check("unbound " + missing + " cannot receive a character-scoped reply", fixture.Packets.Count == 0);
            }
        }

        private static bool Envelope(byte[] packet, byte cmd, ushort type)
            => packet != null && packet.Length >= 15 && packet[0] == cmd
                && BitConverter.ToUInt16(packet, 1) == type && BitConverter.ToInt32(packet, 3) == packet.Length
                && packet.AsSpan(7, 8).SequenceEqual(new byte[8]);
        private static bool Ack(byte[] packet, int error)
            => Envelope(packet, 1, (ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE)
                && packet.AsSpan(15).SequenceEqual(error < 0 ? new byte[] { 1 } : new byte[] { 0, (byte)error });
        private static bool Inactive(byte[] packet)
            => Envelope(packet, 0, (ushort)NotiPacketTypeA21.USER_FATIGUE_ACCELERATION_STATE)
                && packet.AsSpan(15).SequenceEqual(new byte[3]);
        private static bool Active(byte[] packet)
            => Envelope(packet, 0, (ushort)NotiPacketTypeA21.USER_FATIGUE_ACCELERATION_STATE)
                && packet.AsSpan(15).SequenceEqual(new byte[] { 0, 0, 1 });
        private static GameSession Identity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        private static FatigueAccelerationConfigFile Definition(bool valid = true)
            => FatigueAccelerationConfigFile.Parse(valid ? "[level money]\n1 0 31 500\n[/level money]" : null,
                valid ? "[unable dungeon]\n3557\n[/unable dungeon]" : null);
        private static void Sql(GameDatabase db, string sql) => db.Write((connection, transaction) =>
        { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); });
        private static string Text(GameDatabase db, string sql) => db.Read(connection =>
        { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar()); });
        private static long Scalar(GameDatabase db, string sql) => long.Parse(Text(db, sql), System.Globalization.CultureInfo.InvariantCulture);

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fatigue-entry-selftest-" + Guid.NewGuid().ToString("N")));
            private readonly List<EnhancedClientSession> _sessions = new();
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private static int _nextCharacter = 74000000;
            private readonly int _character = Interlocked.Increment(ref _nextCharacter);
            internal readonly GameDatabase Database;
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly FatigueAccelerationEntryService Service;
            internal readonly EventFatigueAccelerationHandler Handler;
            internal readonly List<byte[]> Packets = new();
            internal InventoryService Inventory;
            internal Action BeforePrepare, BeforeWrite, AfterFirstWrite;
            internal bool FailNextWrite;
            internal int CancelledWrites;
            internal Fixture(bool validDefinition = true, bool initialize = true)
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "entry.db"), ServerPaths.SchemaFilePath);
                Service = new FatigueAccelerationEntryService(Database, () => Definition(validDefinition));
                if (initialize) Service.Initialize("1");
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Handler = new EventFatigueAccelerationHandler(Service, Transitions, Send);
            }
            internal EnhancedClientSession CreateSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 1 };
                session.Player.CharacterId = _character; session.Player.UserId = 12345;
                session.Player.Level = 50; session.Player.UserState = 0; session.Player.TownPresenceReady = true;
                session.GameSession = new GameSession(session, Database);
                _sessions.Add(session); Sessions.Register(_character, session);
                Inventory = new InventoryService(_character, 1, Database);
                Inventory.SetMainVirtualCount(0, 1000); Inventory.ClearDirtyState();
                InventoryContext.Register(session.SessionId, Inventory);
                return session;
            }
            internal Task Dispatch(EnhancedClientSession session, byte[] body, byte cmd = 1, ushort? type = null)
                => Handler.HandleAsync(session, new GamePacketHeader { cmd = cmd,
                    type = type ?? (ushort)CmdPacketTypeA21.FATIGUE_ACCELERATION_STATE_CHANGE,
                    length = checked((uint)(14 + (body?.Length ?? 0))) }, body);
            private async Task Send(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare, Func<bool> guard)
            {
                try
                {
                    await EnhancedClientSession.SendPreparedPacketBatchCoreAsync(_sendLock, () =>
                    {
                        var action = BeforePrepare; BeforePrepare = null; action?.Invoke();
                        var packets = prepare(); action = BeforeWrite; BeforeWrite = null; action?.Invoke();
                        return packets;
                    }, packet =>
                    {
                        if (FailNextWrite) { FailNextWrite = false; throw new IOException("fatigue transport fixture"); }
                        Packets.Add(packet.ToArray());
                        var action = AfterFirstWrite; AfterFirstWrite = null; action?.Invoke();
                        return Task.CompletedTask;
                    }, guard);
                }
                catch (OperationCanceledException) { CancelledWrites++; throw; }
            }
            public void Dispose()
            {
                foreach (var session in _sessions) { InventoryContext.Unregister(session.SessionId); session.Close(); }
                _sendLock.Dispose(); SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(Database.DatabasePath + suffix)) File.Delete(Database.DatabasePath + suffix);
                // Only this fixture's known files and now-empty directory; never recursive cleanup.
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: false);
            }
        }
    }
}
