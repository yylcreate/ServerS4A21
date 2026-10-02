using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarEntryRequestSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ENTRY_REQUEST selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }

            try
            {
                CheckParserAndBuilder(Check);
                CheckRegisteredRejectionsAsync(Check).GetAwaiter().GetResult();
                CheckPreparedStateAsync(Check).GetAwaiter().GetResult();
                CheckQueuedIdentityAsync(Check).GetAwaiter().GetResult();
                CheckPostReadStateAsync(Check).GetAwaiter().GetResult();
                CheckReadFailureAndRetryAsync(Check).GetAwaiter().GetResult();
                CheckSuccessfulMenuAsync(Check).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Check("Guardian entry request selftest exception: " + exception, false);
            }
            Console.WriteLine($"GUARDIAN_ALTAR_ENTRY_REQUEST assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckParserAndBuilder(Action<string, bool> check)
        {
            check("914 parser accepts the observed zero-body request as null or an empty array",
                GuardianAltarStartGameRequestParser.TryParse(null, out _)
                && GuardianAltarStartGameRequestParser.TryParse(Array.Empty<byte>(), out _));
            foreach (var length in new[] { 1, 14, 1024 * 1024 })
                check($"914 parser rejects unproven {length}B request bodies",
                    !GuardianAltarStartGameRequestParser.TryParse(new byte[length], out _));

            var domainCases = new[]
            {
                (GuardianAltarEntryRejection.Closed, 1),
                (GuardianAltarEntryRejection.Trading, 2),
                (GuardianAltarEntryRejection.MercenaryAssigned, 3),
                (GuardianAltarEntryRejection.LevelTooLow, 4)
            };
            foreach (var item in domainCases)
            {
                var body = GuardianAltarEntryAckBodyBuilder.BuildRejection(item.Item1);
                check($"{item.Item1} reply is transport-success plus nonzero Int32 domain result",
                    body.Length == 5 && body[0] == 1 && BitConverter.ToInt32(body, 1) == item.Item2);
            }
            foreach (var reason in new[]
            {
                GuardianAltarEntryRejection.InvalidRequest,
                GuardianAltarEntryRejection.InvalidPresence,
                GuardianAltarEntryRejection.ProgressAuthorityUnavailable,
                GuardianAltarEntryRejection.StateReadFailed
            })
                check($"{reason} uses the common failure wrapper, not a fabricated Guardian success",
                    GuardianAltarEntryAckBodyBuilder.BuildRejection(reason).SequenceEqual(new byte[] { 0, 4 }));
            check("common failure code 4 is distinct from Guardian level-rejection result 4",
                !GuardianAltarEntryAckBodyBuilder.BuildRejection(GuardianAltarEntryRejection.InvalidRequest)
                    .SequenceEqual(GuardianAltarEntryAckBodyBuilder.BuildRejection(GuardianAltarEntryRejection.LevelTooLow)));
            var unknownRejected = false;
            try { GuardianAltarEntryAckBodyBuilder.BuildRejection((GuardianAltarEntryRejection)int.MaxValue); }
            catch (ArgumentOutOfRangeException) { unknownRejected = true; }
            check("unknown rejection reasons cannot silently become a success or guessed fallback", unknownRejected);

            var service = new GuardianAltarOpeningService();
            var now = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            check("actual closed schedule takes the proven closed rejection branch",
                GuardianAltarEntryRules.Evaluate(service.GetSnapshot(now), 50, true, false, false)
                    == GuardianAltarEntryRejection.Closed);
            service.SetWindow(now, now.AddMinutes(5));
            var open = service.GetSnapshot(now);
            check("minimum-level boundary rejects 49 but does not pretend to admit level 50",
                GuardianAltarEntryRules.Evaluate(open, 49, true, false, false)
                    == GuardianAltarEntryRejection.LevelTooLow
                && GuardianAltarEntryRules.Evaluate(open, 50, true, false, false)
                    == GuardianAltarEntryRejection.ProgressAuthorityUnavailable);
            check("domain presence gate precedes trade, mercenary and level rejection",
                GuardianAltarEntryRules.Evaluate(open, 49, false, true, true)
                    == GuardianAltarEntryRejection.InvalidPresence);
        }

        private static async Task CheckRegisteredRejectionsAsync(Action<string, bool> check)
        {
            using var fixture = new RequestFixture();
            var session = fixture.CreateSession(71000001);
            check("real command registry registers only the A21 Guardian START_GAME command",
                fixture.Registry.Count == 1
                && fixture.Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME, out _)
                && !fixture.Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM, out _)
                && (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME == 914);

            await fixture.DispatchAsync(session, null);
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            check("null and empty requests each get an explicit closed ACK, without notification deduplication",
                fixture.Packets.Count == 2 && fixture.Packets.All(packet => IsDomainReply(packet, 1)));
            check("914 ACK has the A21 CMD type and a complete 15B S2C envelope",
                fixture.Packets.All(packet => IsEnvelope(packet) && packet.Length == 20));
            var domainReads = fixture.TradeReads + fixture.Mercenary.Reads;
            foreach (var length in new[] { 1, 14, 1024 * 1024 })
            {
                await fixture.DispatchAsync(session, new byte[length]);
                check($"registered 914 rejects {length}B tails with common failure, not the closed domain result",
                    IsCommonFailure(fixture.Packets[^1]));
            }
            check("malformed and closed requests do not consult trade or mercenary storage",
                fixture.TradeReads + fixture.Mercenary.Reads == domainReads && domainReads == 0);
            var count = fixture.Packets.Count;
            await fixture.DispatchAsync(session, Array.Empty<byte>(), command: 0);
            await fixture.DispatchAsync(session, Array.Empty<byte>(), type: (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_BUY_ITEM);
            check("registered callback does not process a NOTI command or another command's header",
                fixture.Packets.Count == count);

            fixture.Open();
            session.Player.Level = 49;
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            check("registered 49-level request receives the proven domain level rejection", IsDomainReply(fixture.Packets[^1], 4));
            session.Player.Level = 50;
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            check("open eligible level-50 request fails explicitly until progression authority exists", IsCommonFailure(fixture.Packets[^1]));
            fixture.Trading = true;
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            check("current trade provider drives the proven trading rejection", IsDomainReply(fixture.Packets[^1], 2));
            fixture.Trading = false;
            fixture.Mercenary.Assigned = true;
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            check("current assigned mercenary provider drives the proven assignment rejection",
                IsDomainReply(fixture.Packets[^1], 3) && fixture.Mercenary.LastCharacterId == session.Player.CharacterId);
            fixture.Mercenary.Assigned = false;

            var revision = fixture.Service.Revision;
            var opening = fixture.Service.GetSnapshot(fixture.Now);
            var runGeneration = session.Player.CurrentDungeonRunGeneration;
            var hadInventory = InventoryContext.TryGetLease(session.Player.CharacterId, out var inventoryBefore);
            count = fixture.Packets.Count;
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            await fixture.DispatchAsync(session, Array.Empty<byte>());
            var hasInventory = InventoryContext.TryGetLease(session.Player.CharacterId, out var inventoryAfter);
            check("repeated valid requests each receive a failure ACK and never enter a successful menu",
                fixture.Packets.Count == count + 2 && fixture.Packets.Skip(count).All(IsCommonFailure));
            check("request rejection does not change the window, enter a run, create selection or acquire inventory",
                fixture.Service.Revision == revision && fixture.Service.GetSnapshot(fixture.Now).RemainingSeconds == opening.RemainingSeconds
                && session.Player.CurrentRun == null && session.Player.CurrentDungeonSelection == null
                && session.Player.CurrentDungeonRunGeneration == runGeneration
                && hadInventory == hasInventory && ReferenceEquals(inventoryBefore, inventoryAfter));
        }

        private static async Task CheckPreparedStateAsync(Action<string, bool> check)
        {
            var cases = new (string Label, Action<RequestFixture, EnhancedClientSession> Change, int Result)[]
            {
                ("window closed", (f, _) => f.Service.Close(), 1),
                ("window expires at exact current time", (f, _) => f.Now = f.Now.AddMinutes(5), 1),
                ("level lowered", (_, s) => s.Player.Level = 49, 4),
                ("trade started", (f, _) => f.Trading = true, 2),
                ("mercenary assigned", (f, _) => f.Mercenary.Assigned = true, 3),
                ("town presence not ready", (_, s) => s.Player.TownPresenceReady = false, 0),
                ("character no longer in town", (_, s) => s.Player.UserState = 1, 0),
                ("current dungeon run installed", (_, s) => s.Player.CurrentRun = new DungeonRun(), 0),
                ("dungeon selection pending", (_, s) => s.Player.DungeonSelectionPending = true, 0),
                ("current dungeon selection installed", (_, s) => s.Player.BeginDungeonSelection(
                    new DungeonTownReturnAnchor(1, 1, 0, 0, 5, 3)), 0)
            };
            foreach (var item in cases)
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000002);
                fixture.Open();
                fixture.BeforePrepare = () => item.Change(fixture, session);
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("request preparation reevaluates " + item.Label,
                    fixture.Packets.Count == 1 && (item.Result == 0
                        ? IsCommonFailure(fixture.Packets[0]) : IsDomainReply(fixture.Packets[0], item.Result)));
                if (item.Result == 0)
                    check("invalid presence does not read trade or mercenary state: " + item.Label,
                        fixture.TradeReads == 0 && fixture.Mercenary.Reads == 0);
                if (session.Player.CurrentRun != null)
                {
                    var run = session.Player.CurrentRun;
                    await fixture.DispatchAsync(session, Array.Empty<byte>());
                    check("entry rejection preserves an existing dungeon run instead of clearing or replacing it",
                        ReferenceEquals(session.Player.CurrentRun, run) && IsCommonFailure(fixture.Packets[^1]));
                }
                if (session.Player.CurrentDungeonSelection != null)
                {
                    var selection = session.Player.CurrentDungeonSelection;
                    await fixture.DispatchAsync(session, Array.Empty<byte>());
                    check("entry rejection preserves existing selection ownership",
                        ReferenceEquals(session.Player.CurrentDungeonSelection, selection) && IsCommonFailure(fixture.Packets[^1]));
                }
            }
        }

        private static async Task CheckQueuedIdentityAsync(Action<string, bool> check)
        {
            var changes = new (string Label, Action<RequestFixture, EnhancedClientSession> Change)[]
            {
                ("same TCP character changed", (_, s) => s.Player.CharacterId++),
                ("same TCP UID changed", (_, s) => s.Player.UserId++),
                ("same TCP and character reselected into a new GameSession", (_, s) => s.GameSession = CreateGameSessionIdentity()),
                ("directory session replaced", (f, s) => f.CreateSession(s.Player.CharacterId)),
                ("directory session removed", (f, s) => f.Sessions.UnregisterAsync(s.Player.CharacterId, s).GetAwaiter().GetResult())
            };
            foreach (var item in changes)
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000003);
                fixture.Open();
                Task queued;
                using (await fixture.Transitions.AcquireIfCurrentAsync(session))
                {
                    queued = fixture.DispatchAsync(session, Array.Empty<byte>());
                    item.Change(fixture, session);
                }
                await queued.WaitAsync(TimeSpan.FromSeconds(2));
                check("queued request cannot cross its original identity: " + item.Label, fixture.Packets.Count == 0);
            }
            foreach (var item in changes.Take(4))
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000004);
                fixture.Open();
                fixture.BeforePrepare = () => item.Change(fixture, session);
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("send preparation rejects changed identity: " + item.Label, fixture.Packets.Count == 0);
            }
            using var staleFixture = new RequestFixture();
            var old = staleFixture.CreateSession(71000005);
            staleFixture.CreateSession(71000005);
            await staleFixture.DispatchAsync(old, Array.Empty<byte>());
            await staleFixture.DispatchAsync(null, Array.Empty<byte>());
            var current = staleFixture.CreateSession(71000006);
            current.Player.UserId = 0;
            await staleFixture.DispatchAsync(current, Array.Empty<byte>());
            current.Player.UserId = 1;
            current.GameSession = null;
            await staleFixture.DispatchAsync(current, Array.Empty<byte>());
            check("already stale directory, null session, zero UID and missing GameSession are inert",
                staleFixture.Packets.Count == 0);
        }

        private static async Task CheckPostReadStateAsync(Action<string, bool> check)
        {
            foreach (var source in new[] { "trade", "mercenary" })
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000009);
                fixture.Open();
                Action expire = () => fixture.Now = fixture.Now.AddMinutes(5);
                if (source == "trade") fixture.DuringTradeRead = expire;
                else fixture.Mercenary.DuringRead = expire;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("request rechecks the actual window after " + source + " state read",
                    fixture.Packets.Count == 1 && IsDomainReply(fixture.Packets[0], 1));
            }
            var changes = new (string Label, Action<RequestFixture, EnhancedClientSession> Change)[]
            {
                ("GameSession replaced", (_, s) => s.GameSession = CreateGameSessionIdentity()),
                ("UID changed", (_, s) => s.Player.UserId++),
                ("directory displaced", (f, s) => f.CreateSession(s.Player.CharacterId)),
            };
            foreach (var source in new[] { "trade", "mercenary" })
            foreach (var throws in new[] { false, true })
            foreach (var item in changes)
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000010);
                fixture.Open();
                Action change = () => item.Change(fixture, session);
                var error = throws ? new SqliteException("Guardian displaced read", 5) : null;
                if (source == "trade")
                {
                    fixture.DuringTradeRead = change;
                    fixture.TradeReadError = error;
                }
                else
                {
                    fixture.Mercenary.DuringRead = change;
                    fixture.Mercenary.ReadError = error;
                }
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check($"{source} state reader {(throws ? "failure" : "return")} cannot reply after {item.Label}",
                    fixture.Packets.Count == 0);
            }
            using var tradingFixture = new RequestFixture();
            var tradingSession = tradingFixture.CreateSession(71000011);
            tradingFixture.Open();
            tradingFixture.Trading = true;
            tradingFixture.Mercenary.ReadError = new SqliteException("must not read while trading", 5);
            await tradingFixture.DispatchAsync(tradingSession, Array.Empty<byte>());
            check("trade rejection is not masked by an unrelated mercenary read failure",
                tradingFixture.Packets.Count == 1 && IsDomainReply(tradingFixture.Packets[0], 2)
                && tradingFixture.Mercenary.Reads == 0);
        }

        private static async Task CheckReadFailureAndRetryAsync(Action<string, bool> check)
        {
            foreach (var source in new[] { "trade", "mercenary" })
            {
                using var fixture = new RequestFixture();
                var session = fixture.CreateSession(71000007);
                fixture.Open();
                if (source == "trade")
                    fixture.TradeReadError = new SqliteException("Guardian test read unavailable", 5);
                else
                    fixture.Mercenary.ReadError = new SqliteException("Guardian test read unavailable", 5);
                var revision = fixture.Service.Revision;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("SQLite " + source + " read failure is explicit common failure, not fabricated closed status",
                    fixture.Packets.Count == 1 && IsCommonFailure(fixture.Packets[0])
                    && fixture.Service.Revision == revision && fixture.Service.GetSnapshot(fixture.Now).IsOpen);
                fixture.TradeReadError = null;
                fixture.Mercenary.ReadError = null;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("request can be retried after recovered " + source + " state read",
                    fixture.Packets.Count == 2 && IsCommonFailure(fixture.Packets[1]));
            }
            using var retryFixture = new RequestFixture();
            var retrySession = retryFixture.CreateSession(71000008);
            var revisionBefore = retryFixture.Service.Revision;
            retryFixture.FailNextWrite = true;
            await retryFixture.DispatchAsync(retrySession, Array.Empty<byte>());
            check("failed request ACK transport does not record a successful write or change the schedule",
                retryFixture.Packets.Count == 0 && retryFixture.Service.Revision == revisionBefore);
            await retryFixture.DispatchAsync(retrySession, Array.Empty<byte>());
            await retryFixture.DispatchAsync(retrySession, Array.Empty<byte>());
            check("request retry after ACK write failure is not suppressed as an already-notified state",
                retryFixture.Packets.Count == 2 && retryFixture.Packets.All(packet => IsDomainReply(packet, 1)));
            retryFixture.Coordinator.Dispose();
            await retryFixture.DispatchAsync(retrySession, Array.Empty<byte>());
            check("disposed request owner cannot reply using the retained registry callback", retryFixture.Packets.Count == 2);
        }

        private static async Task CheckSuccessfulMenuAsync(Action<string, bool> check)
        {
            var definition = GuardianAltarMenuDefinition.Load();
            using (var fixture = new RequestFixture(new ProgressStub(definition)))
            {
                var session = fixture.CreateSession(71000020);
                var loader = (ProgressStub)fixture.Progress;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                fixture.Open();
                session.Player.Level = 49;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                session.Player.Level = 50;
                fixture.Trading = true;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                fixture.Trading = false;
                fixture.Mercenary.Assigned = true;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                fixture.Mercenary.Assigned = false;
                await fixture.DispatchAsync(session, new byte[] { 1 });
                check("rejected requests never initialize/refill a Guardian role", loader.Reads == 0);
                fixture.Packets.Clear();
                var generation = session.Player.CurrentDungeonRunGeneration;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("eligible registered requests return complete, identical menus, not empty success",
                    loader.Reads == 2 && fixture.Packets.Count == 2
                    && fixture.Packets.All(p => IsEnvelope(p) && p.Length == 285
                        && p[15] == 1 && BitConverter.ToInt32(p, 16) == 0 && BitConverter.ToUInt32(p, 20) == 1)
                    && fixture.Packets[0].SequenceEqual(fixture.Packets[1]));
                check("snapshot authority runs outside the packet prepare callback", !loader.InPrepare);
                check("menu projection does not create a run, alter generation or select a dungeon",
                    session.Player.CurrentRun == null && session.Player.CurrentDungeonSelection == null
                    && session.Player.CurrentDungeonRunGeneration == generation);
                fixture.Packets.Clear();
                fixture.FailNextWrite = true;
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("successful menu write failure is retryable without notification deduplication",
                    fixture.Packets.Count == 1 && fixture.Packets[0].Length == 285);
            }

            var mutations = new (string Label, Action<RequestFixture, EnhancedClientSession> Change, int Result)[]
            {
                ("window closed", (f, _) => f.Service.Close(), 1),
                ("level lowered", (_, s) => s.Player.Level = 49, 4),
                ("trade started", (f, _) => f.Trading = true, 2),
                ("mercenary assigned", (f, _) => f.Mercenary.Assigned = true, 3),
                ("selection started", (_, s) => s.Player.DungeonSelectionPending = true, 0),
                ("GameSession replaced", (_, s) => s.GameSession = CreateGameSessionIdentity(), -1),
                ("UID changed", (_, s) => s.Player.UserId++, -1),
                ("directory displaced", (f, s) => f.CreateSession(s.Player.CharacterId), -1),
            };
            foreach (var item in mutations)
            {
                var loader = new ProgressStub(definition);
                using var fixture = new RequestFixture(loader);
                var session = fixture.CreateSession(71000021);
                fixture.Open();
                fixture.BeforePrepare = () => item.Change(fixture, session);
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("loaded menu cannot bypass prepared " + item.Label,
                    loader.Reads == 1 && (item.Result < 0 ? fixture.Packets.Count == 0
                        : fixture.Packets.Count == 1 && (item.Result == 0
                            ? IsCommonFailure(fixture.Packets[0]) : IsDomainReply(fixture.Packets[0], item.Result))));
            }
            foreach (var mode in new[] { "load throws", "invalid PVF data", "wrong character", "window revision", "day rollover" })
            {
                var loader = new ProgressStub(definition);
                using var fixture = new RequestFixture(loader);
                var session = fixture.CreateSession(71000022);
                fixture.Service.SetWindow(fixture.Now, fixture.Now.AddDays(1));
                if (mode == "load throws") loader.Error = new SqliteException("test Guardian owner failure", 5);
                if (mode == "invalid PVF data") loader.Error = new InvalidDataException("test Guardian PVF definition failure");
                if (mode == "wrong character") loader.WrongCharacter = true;
                if (mode == "window revision") fixture.BeforePrepare = () => fixture.Service.SetWindow(fixture.Now, fixture.Now.AddDays(2));
                if (mode == "day rollover") fixture.BeforePrepare = () => fixture.Now = fixture.Now.AddHours(12);
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("menu unavailable/expired failure is visible and does not fabricate success: " + mode,
                    fixture.Packets.Count == 2 && IsCommonFailure(fixture.Packets[0])
                    && fixture.Packets[1][0] == 0
                    && BitConverter.ToUInt16(fixture.Packets[1], 1) == (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE
                    && fixture.Packets[1].AsSpan(15).SequenceEqual(ServerNoticeMessageBuilder.Build(
                        "守护者祭坛状态暂不可用，请重新进入活动。")));
            }
            foreach (var throws in new[] { false, true })
            {
                var loader = new ProgressStub(definition);
                using var fixture = new RequestFixture(loader);
                var session = fixture.CreateSession(71000023);
                fixture.Open();
                loader.DuringLoad = () => session.GameSession = CreateGameSessionIdentity();
                if (throws) loader.Error = new SqliteException("displaced owner read", 5);
                await fixture.DispatchAsync(session, Array.Empty<byte>());
                check("old identity cannot project owner return or exception; commit fence rejects: " + throws,
                    loader.Reads == 1 && fixture.Packets.Count == 0 && (throws || loader.FenceRejected));
            }
        }

        private sealed class ProgressStub : IGuardianAltarProgressService
        {
            private readonly GuardianAltarMenuDefinition _definition;
            internal ProgressStub(GuardianAltarMenuDefinition definition) => _definition = definition;
            internal int Reads;
            internal bool InPrepare;
            internal bool Preparing;
            internal bool WrongCharacter;
            internal bool FenceRejected;
            internal Action DuringLoad;
            internal Exception Error;
            public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow, Func<bool> isCurrent = null)
            {
                Reads++;
                InPrepare |= Preparing;
                DuringLoad?.Invoke();
                if (Error != null) throw Error;
                if (isCurrent != null && !isCurrent())
                {
                    FenceRejected = true;
                    throw new InvalidOperationException("old Guardian menu identity");
                }
                return new GuardianAltarProgressSnapshot(characterId + (WrongCharacter ? 1 : 0),
                    new GuardianAltarHeader(_definition.RidableId, _definition.DailyRefillTicket, 0, 0, 0, 0, 0, 0, 0, 0),
                    new[] { new GuardianAltarStage(_definition.FirstDungeonId, -1) },
                    _definition.InitialPurchases, _definition.InitialSlots, Array.Empty<GuardianAltarAchievement>());
            }
        }

        private static bool IsEnvelope(byte[] packet)
            => packet != null && packet.Length >= 15 && packet[0] == 1
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME
                && BitConverter.ToInt32(packet, 3) == packet.Length
                && packet.AsSpan(7, 8).SequenceEqual(new byte[8]);

        private static bool IsDomainReply(byte[] packet, int result)
            => IsEnvelope(packet) && packet.Length == 20 && packet[15] == 1
                && BitConverter.ToInt32(packet, 16) == result;

        private static bool IsCommonFailure(byte[] packet)
            => IsEnvelope(packet) && packet.Length == 17 && packet[15] == 0 && packet[16] == 4;

        private static GameSession CreateGameSessionIdentity()
        {
            // An identity token only. Never call its members: the production
            // constructor initializes QuestManager and attaches a database.
            return (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        }

        private sealed class MercenaryStub : IMercenaryRestrictionService
        {
            internal bool Assigned;
            internal Exception ReadError;
            internal int Reads;
            internal int LastCharacterId;
            internal Action DuringRead;
            public bool IsAssigned(int characterId)
            {
                Reads++;
                LastCharacterId = characterId;
                DuringRead?.Invoke();
                if (ReadError != null)
                    throw ReadError;
                return Assigned;
            }
            public bool CanDelete(int characterId) => !IsAssigned(characterId);
            public bool CanMutateAppearance(int characterId) => !IsAssigned(characterId);
            public bool CanEnterContent(int characterId) => !IsAssigned(characterId);
        }

        private sealed class RequestFixture : IDisposable
        {
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            internal readonly GuardianAltarOpeningService Service = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly GuardianAltarOpeningCoordinator Coordinator;
            internal readonly MercenaryStub Mercenary = new();
            internal readonly GameCommandRegistry Registry = new();
            internal readonly List<byte[]> Packets = new();
            internal DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            internal bool Trading;
            internal int TradeReads;
            internal Exception TradeReadError;
            internal Action DuringTradeRead;
            internal Action BeforePrepare;
            internal bool FailNextWrite;
            internal readonly IGuardianAltarProgressService Progress;

            internal RequestFixture(IGuardianAltarProgressService progress = null)
            {
                Progress = progress;
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Coordinator = new GuardianAltarOpeningCoordinator(Service, Sessions, Transitions,
                    clock: null, utcNow: () => Now, sendBatch: SendBatchAsync, progress: progress);
                Registry.RegisterGroup("guardian-entry-request-test", group =>
                    Coordinator.RegisterEntryHandlers(group, HasActiveTrade, Mercenary));
            }

            internal void Open() => Service.SetWindow(Now, Now.AddMinutes(5));

            internal EnhancedClientSession CreateSession(int characterId)
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Player.CharacterId = characterId;
                session.Player.UserId = 12001;
                session.Player.Level = 50;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.GameSession = CreateGameSessionIdentity();
                _ownedSessions.Add(session);
                Sessions.Register(characterId, session);
                return session;
            }

            internal Task DispatchAsync(EnhancedClientSession session, byte[] body, byte command = 1, ushort? type = null)
            {
                if (!Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME, out var handler))
                    throw new InvalidOperationException("Guardian entry command is not registered.");
                return handler(session, new GamePacketHeader
                {
                    cmd = command,
                    type = type ?? (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME,
                    length = checked((uint)(14 + (body?.Length ?? 0)))
                }, body);
            }

            private bool HasActiveTrade(EnhancedClientSession session)
            {
                TradeReads++;
                DuringTradeRead?.Invoke();
                if (TradeReadError != null)
                    throw TradeReadError;
                return Trading;
            }

            private Task SendBatchAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
            {
                var beforePrepare = BeforePrepare;
                BeforePrepare = null;
                beforePrepare?.Invoke();
                IReadOnlyList<byte[]> packets;
                var progress = Progress as ProgressStub;
                if (progress != null) progress.Preparing = true;
                try { packets = prepare(); }
                finally { if (progress != null) progress.Preparing = false; }
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    throw new IOException("Guardian request test ACK transport failure");
                }
                foreach (var packet in packets)
                    Packets.Add(packet.ToArray());
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                foreach (var session in _ownedSessions)
                    session.Close();
            }
        }
    }
}
