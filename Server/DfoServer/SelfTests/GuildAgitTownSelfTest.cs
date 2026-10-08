using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.EpicRoad;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class GuildAgitTownSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private const int HeaderLength = 15;
        private const int LeaderId = 62111;
        private const int MemberId = 62112;
        private const int OtherLeaderId = 62113;
        private const int OutsiderId = 62114;

        public static int Run()
        {
            Console.WriteLine("=== GUILD_AGIT_TOWN selftest ===");
            int assertions = 0, failures = 0;
            void Check(string label, bool passed)
            {
                assertions++;
                if (!passed) failures++;
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
            }
            void Section(string label, Func<Task> run)
            {
                try { run().GetAwaiter().GetResult(); }
                catch (Exception exception) { Check(label + ": " + exception, false); }
            }
            Section("typed route and current resource definitions", () => { VerifyResources(Check); return Task.CompletedTask; });
            Section("admission and rejected entry", () => VerifyAdmissionAsync(Check));
            Section("internal route and authorized selection return", () => VerifyRoutesAsync(Check));
            Section("authorized ended-run return", () => VerifyRunReturnsAsync(Check));
            Section("real arrival, isolation and former-member exit", () => VerifyArrivalAsync(Check));
            Section("queued stale session and removed member", () => VerifyQueuedEntryAsync(Check));
            Section("dynamic stores", () => { VerifyStores(Check); return Task.CompletedTask; });
            Console.WriteLine($"=== GUILD_AGIT_TOWN: {assertions} checks, {failures} failures ===");
            return failures;
        }

        private static void VerifyResources(Action<string, bool> check)
        {
            const string first = "1527 154 50 250 8 1";
            const string second = "39 149 130 130 -2 -2";
            const string third = "839 188 50 270 -1 -1";
            var expected = new[] { 1527, 154, 50, 250, 8, 1, 39, 149, 130, 130, -2, -2, 839, 188, 50, 270, -1, -1 };
            var synthetic = MapFile.Parse("[town movable area]\n" + first + "\n" + second + "\n" + third + "\n[/town movable area]");
            check("typed MAP parsing preserves every exit row and both signed client sentinels",
                synthetic.TownMovableArea != null && synthetic.TownMovableArea.SequenceEqual(expected));
            foreach (var (label, input) in new[]
            {
                ("empty table", "[town movable area]\n\n[/town movable area]"),
                ("incomplete row after a valid prefix", "[town movable area]\n" + first + "\n39 149 130 130 -2\n[/town movable area]"),
                ("nonnumeric token after a valid prefix", "[town movable area]\n" + first + "\n39 bad 130 130 -2 -2\n[/town movable area]"),
                ("quoted integer", "[town movable area]\n" + first + "\n39 `149` 130 130 -2 -2\n[/town movable area]"),
                ("overflow integer", "[town movable area]\n" + first + "\n39 2147483648 130 130 -2 -2\n[/town movable area]"),
                ("nested exit table", "[town movable area]\n" + first + "\n[nested]\n" + second + "\n[/nested]\n[/town movable area]"),
                ("duplicate exit table", "[town movable area]\n" + first + "\n[/town movable area]\n[town movable area]\n" + second + "\n[/town movable area]")
            })
                check(label + " rejects the whole exit table without exposing a partial route prefix",
                    MapFile.Parse(input).TownMovableArea == null);

            var areaExits = new Dictionary<int, int[]>
            {
                [0] = new[] { 1527, 154, 50, 250, 8, 1, 39, 149, 130, 130, -2, -2 },
                [1] = new[] { 303, 443, 1250, 50, -2, -2, 45, 401, 200, 100, 8, 0, 839, 188, 50, 270, -1, -1 },
                [2] = new[] { 301, 443, 250, 50, 8, 0 },
                [3] = new[] { 261, 407, 1250, 50, -2, -2, 34, 126, 165, 230, -2, -2, 188, 233, 1300, 185, 0, 0 },
                [4] = new[] { 330, 324, 289, 24, 2, 2, 833, 196, 78, 61, -2, -2 }
            };
            foreach (var (areaId, exits) in areaExits)
            {
                if (!Town.TryGetArea(GuildAgitAccessPolicy.TownId, areaId, out var area))
                    throw new InvalidDataException("Current guild area is missing: " + areaId);
                var map = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", area.MapPath)));
                check("current guild MAP " + areaId + " preserves all typed positive exits and sentinel rows",
                    map.TownMovableArea != null && map.TownMovableArea.SequenceEqual(exits));
            }
            check("the physical resource graph admits 0-to-1, 1-to-0 and 2-to-0",
                Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 0, 1)
                && Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 1, 0)
                && Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 2, 0));
            check("the physical graph rejects absent edges and unregistered destinations",
                !Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 0, 2)
                && !Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 0, 3)
                && !Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, 1, byte.MaxValue)
                && !Town.TryGetAreaPermission(GuildAgitAccessPolicy.TownId, byte.MaxValue, out _));
            check("the final sentinel row still resolves the real guild selection gate and return anchor",
                Town.TryGetDungeonGateReturnInfo(GuildAgitAccessPolicy.TownId, 1, out var gate)
                && gate.Town == GuildAgitAccessPolicy.TownId && gate.Area == 1 && gate.WorldMapAreaId == 37);

            var definitions = new[] { (Id: 8500, Mazes: 3, Maps: 24), (Id: 8503, Mazes: 1, Maps: 5),
                (Id: 8504, Mazes: 1, Maps: 5), (Id: 8505, Mazes: 1, Maps: 6),
                (Id: 8507, Mazes: 1, Maps: 5), (Id: 8508, Mazes: 1, Maps: 10) };
            var worldMap = WorldMap.GetAreaById(37);
            check("the guild selection directory contains exactly the six current resource dungeons",
                worldMap != null && worldMap.Dungeons.Select(entry => entry.DungeonId).OrderBy(id => id)
                    .SequenceEqual(definitions.Select(entry => entry.Id).OrderBy(id => id)));
            var parsedMapIds = new HashSet<int>();
            foreach (var definition in definitions)
            {
                var dungeon = DungeonData.GetDungeonFile(definition.Id);
                check("current DGN " + definition.Id + " parses its actual maze and fixed resource requirements",
                    dungeon != null && dungeon.Mazes.Count == definition.Mazes
                    && dungeon.MinimumRequiredLevel == 50 && dungeon.ProhibitPractice == 1
                    && dungeon.DesignateDungeonDifficulty?.SequenceEqual(new[] { 2 }) == true
                    && dungeon.GetIntValue("guild dungeon") == 1);
                if (dungeon == null) continue;
                var ids = dungeon.Mazes.SelectMany(maze => maze.MapSpecifications)
                    .SelectMany(specification => (specification.MapCandidates ?? Array.Empty<int>())
                        .Concat(specification.LayeredMapIds ?? Array.Empty<int>()).Append(specification.Index))
                    .Where(id => id > 0).Distinct().ToArray();
                check("DGN " + definition.Id + " retains all registered MAP candidates", ids.Length == definition.Maps);
                bool allMapsParse = true;
                foreach (int id in ids)
                {
                    var map = DungeonMapCatalog.GetMapFile(id);
                    allMapsParse &= map?.Root != null && map.Npcs != null && map.Monsters != null;
                    parsedMapIds.Add(id);
                }
                check("all referenced MAPs for DGN " + definition.Id + " parse with the production parser", allMapsParse);
            }
            check("all 55 distinct guild dungeon MAPs were parsed without starting a dungeon or inventing progression",
                parsedMapIds.Count == 55);
            foreach (int id in new[] { 33908, 33916, 33923, 33933, 33943, 33953, 33973, 33985 })
                check("production parsing accepts the existing empty NPC block in MAP " + id,
                    parsedMapIds.Contains(id) && DungeonMapCatalog.GetMapFile(id).Npcs.Count == 0);
        }

        private static async Task VerifyAdmissionAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var peer = await fixture.AddPeerAsync(MemberId);
            var player = peer.Session.Player;
            check("the actual source is a registered private Seria room",
                Town.IsCeraRoom(player.CurTownId, player.CurAreaId));
            check("a durable current member with an owned lease can use the basic resource area",
                fixture.Town.TryValidateGuildAgitEntry(peer.Session, GuildAgitAccessPolicy.BasicAreaId, out var reason)
                && reason == null);

            async Task Denied(string label, Action arrange, Action restore, byte targetArea = GuildAgitAccessPolicy.BasicAreaId)
            {
                arrange();
                try
                {
                    var onlineBefore = OnlinePosition(peer.Session);
                    var savedBefore = SavedPosition(fixture.Records.GetById(MemberId));
                    check(label + " fails the actual validator",
                        !fixture.Town.TryValidateGuildAgitEntry(peer.Session, targetArea, out _));
                    await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, targetArea, 139, 262);
                    var packets = await peer.DrainAsync();
                    check(label + " cannot mutate online or durable position or start an arrival",
                        onlineBefore.SequenceEqual(OnlinePosition(peer.Session))
                        && savedBefore == SavedPosition(fixture.Records.GetById(MemberId))
                        && !packets.Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
                }
                finally { restore(); }
            }

            var sourceTown = player.CurTownId;
            var sourceArea = player.CurAreaId;
            await Denied("public-town source", () => player.CurAreaId = 1, () => player.CurAreaId = sourceArea);
            await Denied("unregistered source", () => player.CurTownId = byte.MaxValue, () => player.CurTownId = sourceTown);
            await Denied("unpublished town presence", () => player.TownPresenceReady = false, () => player.TownPresenceReady = true);
            await Denied("non-town user state", () => player.UserState = 1, () => player.UserState = 0);
            await Denied("active dungeon", () => player.CurrentRun = new DungeonRun(), () => player.CurrentRun = null);
            await Denied("pending dungeon selection", () => player.DungeonSelectionPending = true, () => player.DungeonSelectionPending = false);
            await Denied("live selection without a pending flag", () => player.BeginDungeonSelection(
                new DungeonTownReturnAnchor(sourceTown, sourceArea, player.CurPosX, player.CurPosY, 5, 0)),
                player.ClearDungeonSelection);
            var gameSession = peer.Session.GameSession;
            await Denied("missing GameSession", () => peer.Session.GameSession = null, () => peer.Session.GameSession = gameSession);
            var account = peer.Session.Account;
            await Denied("missing account", () => peer.Session.Account = null, () => peer.Session.Account = account);
            var uid = player.UserId;
            await Denied("missing UID", () => player.UserId = 0, () => player.UserId = uid);
            var level = player.Level;
            await Denied("below the actual minimum level", () => player.Level = 0, () => player.Level = level);
            await Denied("missing owned InventoryLease", () => InventoryContext.Unregister(peer.Session.SessionId, MemberId),
                () => InventoryContext.Register(peer.Session.SessionId, peer.Inventory));
            foreach (byte area in new byte[] { 1, 2, 3, 4, byte.MaxValue })
                await Denied("external personal-room request directly targeting area " + area, () => { }, () => { }, area);

            var outsider = await fixture.AddPeerAsync(OutsiderId);
            outsider.Session.Player.Subtype0Tail.GuildId = checked((uint)fixture.Guild.Id);
            check("a forged display guild ID cannot substitute for durable membership",
                !fixture.Town.TryValidateGuildAgitEntry(outsider.Session, GuildAgitAccessPolicy.BasicAreaId, out reason)
                && reason == "guild_agit_not_member");
            var outsiderBefore = OnlinePosition(outsider.Session);
            await fixture.MoveAsync(outsider, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            check("the actual outsider request remains at its source without AREA_USERS",
                outsiderBefore.SequenceEqual(OnlinePosition(outsider.Session))
                && !(await outsider.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));

            var partyBody = new byte[8];
            partyBody[0] = GuildAgitAccessPolicy.TownId;
            partyBody[1] = GuildAgitAccessPolicy.BasicAreaId;
            BitConverter.TryWriteBytes(partyBody.AsSpan(2), (short)139);
            BitConverter.TryWriteBytes(partyBody.AsSpan(4), (short)262);
            partyBody[6] = 5;
            var partySource = OnlinePosition(peer.Session);
            await fixture.Town.Handle_ENUM_CMDPACKET_PARTY_TELEPORT(peer.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.PARTY_TELEPORT },
                partyBody).WaitAsync(Timeout);
            var partyRejected = await peer.DrainAsync();
            check("a guild-target party teleport is refused before party lookup and returns the current area",
                partySource.SequenceEqual(OnlinePosition(peer.Session))
                && partyRejected.Any(packet => IsNoti(packet, NotiPacketTypeA21.USER_AREA))
                && !partyRejected.Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
        }

        private static async Task VerifyRoutesAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var peer = await fixture.AddPeerAsync(MemberId);
            var player = peer.Session.Player;
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 0, 139, 262);
            await peer.DrainAsync();

            async Task RefuseInternal(byte target, string label)
            {
                var before = OnlinePosition(peer.Session);
                var persisted = SavedPosition(fixture.Records.GetById(MemberId));
                check(label + " has no current authorization",
                    !fixture.Town.TryValidateGuildAgitEntry(peer.Session, target, out _));
                await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, target, 145, 451);
                check(label + " leaves both positions intact without beginning an arrival",
                    before.SequenceEqual(OnlinePosition(peer.Session))
                    && persisted == SavedPosition(fixture.Records.GetById(MemberId))
                    && !(await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
            }

            await RefuseInternal(2, "an absent area0-to-area2 edge");
            await RefuseInternal(3, "an absent area0-to-area3 edge");
            await RefuseInternal(byte.MaxValue, "an unregistered guild destination");
            var originalLevel = player.Level;
            player.Level = 0;
            try { await RefuseInternal(1, "the real area1 minimum-level requirement"); }
            finally { player.Level = originalLevel; }

            check("the actual member validator admits the MAP's area0-to-area1 route",
                fixture.Town.TryValidateGuildAgitEntry(peer.Session, 1, out var reason) && reason == null);
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 1, 145, 451);
            var arrived = await peer.DrainAsync();
            check("the current client-shaped area1 destination reaches the selection-gate street without starting a run",
                player.CurTownId == GuildAgitAccessPolicy.TownId && player.CurAreaId == 1
                && player.CurPosX == 145 && player.CurPosY == 451
                && arrived.Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)
                    && packet[HeaderLength + 1] == 1 && BitConverter.ToUInt16(packet, HeaderLength + 2) == 1)
                && player.CurrentRun == null && player.CurrentDungeonSelection == null);
            check("the actual area1 resource is a dungeon gate for the registered WorldMap",
                Town.TryGetArea(GuildAgitAccessPolicy.TownId, 1, out var gateArea)
                && gateArea.AreaType == "dungeon gate" && gateArea.LinkedId == 37);

            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 1, 145, 451);
            check("a current registered area1 presence can reproject the same area without a synthetic connection",
                player.CurAreaId == 1 && (await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));

            async Task ReturnSelection(string label, bool removeMember)
            {
                var expectedPosition = OnlinePosition(peer.Session);
                var selection = player.BeginDungeonSelection(new DungeonTownReturnAnchor(
                    GuildAgitAccessPolicy.TownId, 1, 145, 451, 5, 0));
                if (selection == null) throw new InvalidOperationException("The real player selection owner rejected the fixture anchor.");
                player.UserState = 1;
                check(label + " does not authorize a bare client area request during selection",
                    !fixture.Town.TryValidateGuildAgitEntry(peer.Session, 1, out _));
                await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 1, 145, 451);
                check(label + " retains the live selection when a client tries to reproject the area directly",
                    ReferenceEquals(player.CurrentDungeonSelection, selection) && player.UserState == 1
                    && !(await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
                await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 2, 731, 219);
                check(label + " cannot substitute another registered guild area for the frozen return anchor",
                    ReferenceEquals(player.CurrentDungeonSelection, selection) && player.UserState == 1
                    && expectedPosition.SequenceEqual(OnlinePosition(peer.Session))
                    && !(await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
                if (removeMember)
                {
                    check("withdrawal can commit while the player has an existing selection",
                        fixture.Guilds.RemoveMember(MemberId, MemberId) == GuildResult.Success);
                    GuildIdentityProjection.Apply(null, player.Subtype0Tail);
                }
                // A real return must apply the frozen anchor; starting and
                // finishing at identical coordinates would not prove that.
                Place(peer.Session, GuildAgitAccessPolicy.TownId, 3, 731, 219);
                player.CurDirection = 3;
                player.CurAreaState = 2;
                // Exercise the real selection-return owner. This creates no
                // dungeon and does not stand in for guild conquest/UI protocol.
                await fixture.Town.Handle_ENUM_CMDPACKET_GIVEUP_GAME(peer.Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.BACK_2_VILLAGE },
                    Array.Empty<byte>()).WaitAsync(Timeout);
                var returned = await peer.DrainAsync();
                int ack = returned.FindIndex(packet => packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.BACK_2_VILLAGE
                    && packet.Length > HeaderLength && packet[HeaderLength] == 1);
                int state = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USER_STATE));
                int area = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USER_AREA));
                int roster = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS));
                check(label + " restores its exact area1 anchor and completes the original selection",
                    player.CurTownId == GuildAgitAccessPolicy.TownId && player.CurAreaId == 1
                    && player.CurPosX == 145 && player.CurPosY == 451
                    && player.CurDirection == 5 && player.CurAreaState == 0 && player.UserState == 0
                    && player.CurrentDungeonSelection == null && player.CurrentRun == null);
                check(label + " receives the ordinary success, state, area and roster return sequence",
                    ack >= 0 && state > ack && area > state && roster > area
                    && returned[roster][HeaderLength] == GuildAgitAccessPolicy.TownId
                    && returned[roster][HeaderLength + 1] == 1
                    && BitConverter.ToUInt16(returned[roster], HeaderLength + 2) == 1);
                check(label + " projects the full original anchor after position, direction and state were disturbed",
                    area >= 0 && returned[area].AsSpan(HeaderLength).SequenceEqual(expectedPosition));
            }

            await ReturnSelection("a current member's authorized selection exit", removeMember: false);
            check("the current MAP also authorizes the reverse area1-to-area0 route",
                fixture.Town.TryValidateGuildAgitEntry(peer.Session, 0, out reason) && reason == null);
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 0, 139, 262);
            check("the reverse route restores the basic room through the actual town handler",
                player.CurAreaId == 0 && player.CurPosX == 139 && player.CurPosY == 262
                && (await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 1, 145, 451);
            await peer.DrainAsync();
            await ReturnSelection("an existing selection whose member was removed", removeMember: true);
            await RefuseInternal(0, "a former member's new internal move");
            await fixture.MoveAsync(peer, fixture.Source.Town, fixture.Source.Area, fixture.Source.X, fixture.Source.Y);
            check("a removed member can still leave the restored anchor for its ordinary personal room",
                player.CurTownId == fixture.Source.Town && player.CurAreaId == fixture.Source.Area
                && (await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
        }

        private static async Task VerifyRunReturnsAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var peer = await fixture.AddPeerAsync(MemberId);
            var player = peer.Session.Player;
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 0, 139, 262);
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 1, 145, 451);
            await peer.DrainAsync();

            async Task ReturnRun(string label, bool removeMember)
            {
                var expectedPosition = OnlinePosition(peer.Session);
                var anchor = new DungeonTownReturnAnchor(GuildAgitAccessPolicy.TownId, 1, 145, 451, 5, 0);
                var selection = player.BeginDungeonSelection(anchor);
                // Use the existing selection-to-run owner and a real identity.
                // This fixture does not claim to implement guild conquest or
                // enter a client dungeon; it exercises the real ending path.
                if (selection == null || !player.TryReplaceDungeonSelectionWithRun(selection,
                        generation => new DungeonRun(new DungeonInstance(8500, 2),
                            DungeonIdentityGenerator.NextRunId(), generation, DungeonRunState.Active)
                        { TownReturnAnchor = anchor }, out var run))
                    throw new InvalidOperationException("The real selection-to-run owner rejected the return fixture.");
                player.UserState = 1;
                await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, 2, 731, 219);
                check(label + " rejects a client request to a different registered area while its run owns the return",
                    ReferenceEquals(player.CurrentRun, run) && player.UserState == 1
                    && expectedPosition.SequenceEqual(OnlinePosition(peer.Session))
                    && !(await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
                if (removeMember)
                {
                    check("withdrawal can commit while the player has an existing run",
                        fixture.Guilds.RemoveMember(MemberId, MemberId) == GuildResult.Success);
                    GuildIdentityProjection.Apply(null, player.Subtype0Tail);
                }
                Place(peer.Session, GuildAgitAccessPolicy.TownId, 3, 731, 219);
                player.CurDirection = 3;
                player.CurAreaState = 2;
                await fixture.Town.Handle_ENUM_CMDPACKET_GIVEUP_GAME(peer.Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.BACK_2_VILLAGE },
                    Array.Empty<byte>()).WaitAsync(Timeout);
                var returned = await peer.DrainAsync();
                int ack = returned.FindIndex(packet => packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.BACK_2_VILLAGE
                    && packet.Length > HeaderLength && packet[HeaderLength] == 1);
                int state = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USER_STATE));
                int area = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USER_AREA));
                int roster = returned.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS));
                check(label + " ends its original run and restores the complete frozen guild anchor",
                    run.RunState == DungeonRunState.Ended && player.CurrentRun == null
                    && player.CurrentDungeonSelection == null && player.UserState == 0
                    && player.CurTownId == GuildAgitAccessPolicy.TownId && player.CurAreaId == 1
                    && player.CurPosX == 145 && player.CurPosY == 451
                    && player.CurDirection == 5 && player.CurAreaState == 0);
                check(label + " projects success then state, full anchor and private roster",
                    ack >= 0 && state > ack && area > state && roster > area
                    && returned[area].AsSpan(HeaderLength).SequenceEqual(expectedPosition)
                    && returned[roster][HeaderLength] == GuildAgitAccessPolicy.TownId
                    && returned[roster][HeaderLength + 1] == 1
                    && BitConverter.ToUInt16(returned[roster], HeaderLength + 2) == 1);
            }

            await ReturnRun("a current member's ended-run return", removeMember: false);
            await ReturnRun("a removed member's already-owned ended-run return", removeMember: true);
            check("restoring an ended run does not grant the former member a new guild-area route",
                !fixture.Town.TryValidateGuildAgitEntry(peer.Session, 0, out _));
            await fixture.MoveAsync(peer, fixture.Source.Town, fixture.Source.Area, fixture.Source.X, fixture.Source.Y);
            check("the former member can leave its ended-run return anchor through the ordinary town owner",
                player.CurTownId == fixture.Source.Town && player.CurAreaId == fixture.Source.Area
                && (await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
        }

        private static async Task VerifyArrivalAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var peer = await fixture.AddPeerAsync(MemberId);
            var sameGuild = await fixture.AddPeerAsync(LeaderId);
            var otherGuild = await fixture.AddPeerAsync(OtherLeaderId);
            Place(sameGuild.Session, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 200, 262);
            Place(otherGuild.Session, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 300, 262);
            var savedBefore = SavedPosition(fixture.Records.GetById(MemberId));
            await fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            var packets = await peer.DrainAsync();
            var areaIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USER_AREA));
            var rosterIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS));
            var appearanceIndex = packets.FindIndex(packet => IsNoti(packet, NotiPacketTypeA21.USERINFO));
            check("the actual member request moves to the basic guild resource",
                peer.Session.Player.CurTownId == GuildAgitAccessPolicy.TownId
                && peer.Session.Player.CurAreaId == GuildAgitAccessPolicy.BasicAreaId
                && peer.Session.Player.CurPosX == 139 && peer.Session.Player.CurPosY == 262);
            check("arrival uses the existing USER_AREA, self roster, appearance order",
                areaIndex >= 0 && rosterIndex > areaIndex && appearanceIndex > rosterIndex);
            check("the basic room roster contains exactly the arriving self, despite both kinds of guild peers",
                rosterIndex >= 0 && packets[rosterIndex].Length == HeaderLength + 12
                && packets[rosterIndex][HeaderLength] == GuildAgitAccessPolicy.TownId
                && BitConverter.ToUInt16(packets[rosterIndex], HeaderLength + 2) == 1
                && BitConverter.ToUInt16(packets[rosterIndex], HeaderLength + 4) == peer.Session.Player.UserId);
            check("same-guild and other-guild peers receive no arrival projection",
                (await sameGuild.DrainAsync()).Count == 0 && (await otherGuild.DrainAsync()).Count == 0);
            var savedAfter = fixture.Records.GetById(MemberId);
            check("entering the registered guild destination keeps the prior permanent normal-town position",
                !GameChannelSpawnPolicy.ShouldPersistPosition(peer.Session.ListenerPort,
                    GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId)
                && savedBefore == SavedPosition(savedAfter));

            var agitPosition = OnlinePosition(peer.Session);
            var savedAgitPosition = SavedPosition(savedAfter);
            await fixture.MoveAsync(peer, EpicRoadEntryService.TargetTownId, EpicRoadEntryService.TargetAreaId, 139, 262);
            var closedEvent = await peer.DrainAsync();
            check("a client exit from Town8 cannot impersonate an authorized return into a closed epic-road town",
                agitPosition.SequenceEqual(OnlinePosition(peer.Session))
                && savedAgitPosition == SavedPosition(fixture.Records.GetById(MemberId))
                && !closedEvent.Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));

            foreach (byte area in new byte[] { 0, 1, 2, 3, 4 })
                check("all guild-resource areas remain nonshared at area " + area,
                    !SessionDirectory.IsShareableTownArea(GuildAgitAccessPolicy.TownId, area));
            await fixture.Town.Handle_ENUM_CMDPACKET_SET_USER_POSITION(peer.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_USER_POSITION },
                new byte[] { 150, 0, 6, 1, 5, 0, 0 }).WaitAsync(Timeout);
            check("the mover gets a position reply while private peers get no movement projection",
                (await peer.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.USER_POSITION))
                && (await sameGuild.DrainAsync()).Count == 0 && (await otherGuild.DrainAsync()).Count == 0);
            await fixture.Town.NotifyLeaveAsync(peer.Session).WaitAsync(Timeout);
            check("the private room also suppresses departure fanout",
                (await sameGuild.DrainAsync()).Count == 0 && (await otherGuild.DrainAsync()).Count == 0);

            check("the durable owner commits the member's own withdrawal",
                fixture.Guilds.RemoveMember(MemberId, MemberId) == GuildResult.Success);
            GuildIdentityProjection.Apply(null, peer.Session.Player.Subtype0Tail);
            check("a former member cannot renew its Town8 authorization",
                !fixture.Town.TryValidateGuildAgitEntry(peer.Session, GuildAgitAccessPolicy.BasicAreaId, out _));
            await fixture.MoveAsync(peer, fixture.Source.Town, fixture.Source.Area, fixture.Source.X, fixture.Source.Y);
            var returned = await peer.DrainAsync();
            check("losing membership never prevents return to the original valid personal room",
                peer.Session.Player.CurTownId == fixture.Source.Town && peer.Session.Player.CurAreaId == fixture.Source.Area
                && returned.Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
            check("guild exit neither starts a dungeon nor recreates membership",
                peer.Session.Player.CurrentRun == null && peer.Session.Player.CurrentDungeonSelection == null
                && !fixture.Guilds.IsMember(MemberId));

            Place(peer.Session, 1, 1, 100, 234);
            Place(sameGuild.Session, 1, 1, 200, 234);
            Place(otherGuild.Session, 1, 1, 300, 234);
            check("ordinary public-town co-presence continues to use the same directory",
                fixture.Sessions.GetSessionsInArea(1, 1, MemberId, peer.Session.ListenerPort).Count == 2);
            Place(sameGuild.Session, fixture.Source.Town, fixture.Source.Area, 200, 234);
            Place(otherGuild.Session, fixture.Source.Town, fixture.Source.Area, 300, 234);
            check("ordinary personal-room privacy also remains intact",
                fixture.Sessions.GetSessionsInArea(fixture.Source.Town, fixture.Source.Area, MemberId,
                    peer.Session.ListenerPort).Count == 0);
        }

        private static async Task VerifyQueuedEntryAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var peer = await fixture.AddPeerAsync(MemberId);
            var before = OnlinePosition(peer.Session);
            var savedBefore = SavedPosition(fixture.Records.GetById(MemberId));
            var gate = await fixture.Transitions.AcquireAsync(MemberId);
            Task queued = fixture.MoveAsync(peer, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            Peer replacement;
            try { replacement = await fixture.AddPeerAsync(MemberId); }
            finally { gate.Dispose(); }
            await queued;
            check("an entry queued behind a replaced generation cannot move or notify either connection",
                before.SequenceEqual(OnlinePosition(peer.Session))
                && replacement.Session.Player.CurTownId == fixture.Source.Town
                && savedBefore == SavedPosition(fixture.Records.GetById(MemberId))
                && (await peer.DrainAsync()).Count == 0 && (await replacement.DrainAsync()).Count == 0);
            check("the old connection cannot validate entry after replacement",
                !fixture.Town.TryValidateGuildAgitEntry(peer.Session, GuildAgitAccessPolicy.BasicAreaId, out _));

            var originalGameSession = replacement.Session.GameSession;
            gate = await fixture.Transitions.AcquireAsync(MemberId);
            queued = fixture.MoveAsync(replacement, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            try { replacement.Session.GameSession = new GameSession(replacement.Session, fixture.Database, fixture.Records); }
            finally { gate.Dispose(); }
            try
            {
                await queued;
                check("a queued request cannot carry authorization across a GameSession replacement on the same socket",
                    before.SequenceEqual(OnlinePosition(replacement.Session))
                    && savedBefore == SavedPosition(fixture.Records.GetById(MemberId))
                    && (await replacement.DrainAsync()).Count == 0);
            }
            finally { replacement.Session.GameSession = originalGameSession; }

            var player = replacement.Session.Player;
            var oldUid = player.UserId;
            gate = await fixture.Transitions.AcquireAsync(MemberId);
            queued = fixture.MoveAsync(replacement, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            try
            {
                // Player is deliberately reused. The socket is current for the
                // new identity, so reference equality alone cannot authorize it.
                player.CharacterId = LeaderId; player.UserId = LeaderId;
                fixture.Sessions.Register(LeaderId, replacement.Session);
            }
            finally { gate.Dispose(); }
            try
            {
                await queued;
                check("a queued request cannot mutate a different identity on the same Player object and socket",
                    player.CharacterId == LeaderId && player.CurTownId == fixture.Source.Town
                    && player.CurAreaId == fixture.Source.Area && player.CurPosX == fixture.Source.X
                    && savedBefore == SavedPosition(fixture.Records.GetById(MemberId))
                    && (await replacement.DrainAsync()).Count == 0);
            }
            finally
            {
                await fixture.Sessions.UnregisterAsync(LeaderId, replacement.Session);
                player.CharacterId = MemberId; player.UserId = oldUid;
            }

            gate = await fixture.Transitions.AcquireAsync(MemberId);
            queued = fixture.MoveAsync(replacement, GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId, 139, 262);
            try { check("removal commits before the queued entry is admitted",
                fixture.Guilds.RemoveMember(MemberId, MemberId) == GuildResult.Success); }
            finally { gate.Dispose(); }
            await queued;
            check("queued entry re-reads the durable member relation after its gate opens",
                replacement.Session.Player.CurTownId == fixture.Source.Town
                && savedBefore == SavedPosition(fixture.Records.GetById(MemberId))
                && !(await replacement.DrainAsync()).Any(packet => IsNoti(packet, NotiPacketTypeA21.AREA_USERS)));
        }

        private static void VerifyStores(Action<string, bool> check)
        {
            var stores = new ExpertJobStoreRuntimeService();
            var command = new ExpertJobStoreCreateCommand { Kind = ExpertJobStoreKind.DisjointMachine,
                NameBytes = Encoding.ASCII.GetBytes("agit-store"), Cost = 0, PositionX = 100, PositionY = 234 };
            var state = new DisjointMachineState { MachineGrade = 1, Endurance = 100 };
            check("ordinary-town dynamic stores remain available through their real runtime owner",
                stores.TryCreate(Guid.NewGuid(), LeaderId, LeaderId, ExpertJobStateCodec.DisjointerType,
                    1, 1, false, false, command, state, out var ordinary, out _)
                && ordinary != null && stores.GetStoresInArea(1, 1).Count == 1);
            check("the same valid create request is rejected in the private guild resource",
                !stores.TryCreate(Guid.NewGuid(), MemberId, MemberId, ExpertJobStateCodec.DisjointerType,
                    GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId,
                    false, false, command, state, out var denied, out var error)
                && denied == null && error == ExpertJobStoreRuntimeService.ErrorInvalidState && stores.Count == 1);
            // Model a stale pre-policy store without adding a second runtime registry.
            ordinary.TownId = GuildAgitAccessPolicy.TownId;
            ordinary.AreaId = GuildAgitAccessPolicy.BasicAreaId;
            check("a stale guild-area store is invisible to the authoritative area lookup",
                stores.GetStoresInArea(GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId).Count == 0
                && !stores.TryGetStoreInArea(GuildAgitAccessPolicy.TownId, GuildAgitAccessPolicy.BasicAreaId,
                    ordinary.OwnerUserId, out _));
        }

        private static bool IsNoti(byte[] packet, NotiPacketTypeA21 type)
            => packet.Length >= HeaderLength && packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)type;
        private static byte[] OnlinePosition(EnhancedClientSession session)
            => TownAreaNotificationBuilder.BuildUserArea(TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player));
        private static string SavedPosition(CharacterRecord record)
            => $"{record.TownId}:{record.AreaId}:{record.PosX}:{record.PosY}:{record.Direction}:{record.AreaState}";
        private static void Place(EnhancedClientSession session, byte town, byte area, short x, short y)
        {
            session.Player.CurTownId = town; session.Player.CurAreaId = area;
            session.Player.CurPosX = x; session.Player.CurPosY = y;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "guild-agit-town-" + Guid.NewGuid().ToString("N")));
            private readonly List<Peer> _peers = new();
            internal GameDatabase Database { get; }
            internal SqliteCharacterRepository Records { get; }
            internal GuildRepository Guilds { get; }
            internal GuildRecord Guild { get; }
            internal SessionDirectory Sessions { get; } = new();
            internal CharacterTransitionCoordinator Transitions { get; }
            internal TownHandler Town { get; }
            internal CeraRoomInfo Source { get; }

            internal Fixture()
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 PVF.", archive);
                Source = GameWorld.Town.GetCeraRoomInfo(1);
                if (Source.Town != 1 || !GameWorld.Town.TryGetArea(GuildAgitAccessPolicy.TownId,
                        GuildAgitAccessPolicy.BasicAreaId, out _))
                    throw new InvalidDataException("The current PVF lacks the validated Seria or basic guild resource.");
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "agit.db"), ServerPaths.SchemaFilePath);
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(811,'guild-agit-test','');";
                    command.ExecuteNonQuery();
                });
                Records = new SqliteCharacterRepository(Database);
                foreach (int id in new[] { LeaderId, MemberId, OtherLeaderId, OutsiderId })
                    Records.Create(new CharacterRecord { CharacterId = id, AccountId = 811,
                        Name = Encoding.ASCII.GetBytes("agit-" + id), Level = 70, UserState = 0,
                        TownId = Source.Town, AreaId = Source.Area, PosX = Source.X, PosY = Source.Y,
                        Direction = 5, AreaState = 0, Appearance = Array.Empty<CharacterAppearanceEntry>() });
                Guilds = new GuildRepository(Database);
                Guild = Database.Write((connection, transaction) =>
                {
                    var guild = GuildRepository.Insert(connection, transaction, LeaderId, "AgitA", "");
                    GuildRepository.AddMember(connection, transaction, guild.Id, MemberId);
                    GuildRepository.Insert(connection, transaction, OtherLeaderId, "AgitB", "");
                    return guild;
                });
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Town = new TownHandler(Records, null, null, Sessions, null, null, null, Database);
                Town.ConfigureGuildEntrance(Guilds, Transitions);
            }

            internal async Task<Peer> AddPeerAsync(int id)
            {
                var peer = await Peer.CreateAsync(this, id);
                _peers.Add(peer);
                return peer;
            }

            internal Task MoveAsync(Peer peer, byte town, byte area, short x, short y)
            {
                var body = new byte[16];
                body[0] = town; body[1] = area;
                BitConverter.TryWriteBytes(body.AsSpan(2), x); BitConverter.TryWriteBytes(body.AsSpan(4), y);
                body[6] = 5;
                BitConverter.TryWriteBytes(body.AsSpan(7), (ushort)peer.Session.Player.CurTownId);
                BitConverter.TryWriteBytes(body.AsSpan(9), (ushort)peer.Session.Player.CurAreaId);
                return Town.Handle_ENUM_CMDPACKET_SET_USER_AREA(peer.Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.SET_USER_AREA }, body).WaitAsync(Timeout);
            }

            public void Dispose()
            {
                foreach (var peer in _peers) peer.Dispose();
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var path = Path.GetFullPath(Database.DatabasePath + suffix);
                    if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Guild selftest cleanup escaped its temporary directory.");
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            private readonly Fixture _fixture;
            private readonly int _characterId;
            internal EnhancedClientSession Session { get; }
            internal InventoryService Inventory { get; }
            private Peer(TcpClient reader, EnhancedClientSession session, InventoryService inventory, Fixture fixture, int id)
            { _reader = reader; Session = session; Inventory = inventory; _fixture = fixture; _characterId = id; }

            internal static async Task<Peer> CreateAsync(Fixture fixture, int id)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                var reader = new TcpClient();
                listener.Start();
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    var writer = await listener.AcceptTcpClientAsync().WaitAsync(Timeout);
                    await connecting.WaitAsync(Timeout);
                    writer.NoDelay = true; reader.NoDelay = true;
                    var session = new EnhancedClientSession(writer, new GamePacketHeader(), GameNetworkConfig.NormalGamePort)
                    { Account = new AccountRecord { AccountId = 811 } };
                    session.Player.HydrateIdentityFrom(fixture.Records.GetById(id));
                    Place(session, fixture.Source.Town, fixture.Source.Area, fixture.Source.X, fixture.Source.Y);
                    session.Player.CurDirection = 5; session.Player.CurAreaState = 0;
                    session.Player.TownPresenceReady = true; session.Player.UserState = 0;
                    session.Player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                    GuildIdentityProjection.Apply(fixture.Guilds.GetForMember(id), session.Player.Subtype0Tail);
                    session.GameSession = new GameSession(session, fixture.Database, fixture.Records,
                        sessionDirectory: fixture.Sessions);
                    var inventory = new InventoryService(id, 811, fixture.Database);
                    InventoryContext.Register(session.SessionId, inventory);
                    fixture.Sessions.Register(id, session);
                    return new Peer(reader, session, inventory, fixture, id);
                }
                catch { reader.Dispose(); throw; }
                finally { listener.Stop(); }
            }

            internal async Task<List<byte[]>> DrainAsync()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                using var cancellation = new CancellationTokenSource(Timeout);
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new IOException("Guild selftest connection unexpectedly closed.");
                    var header = new byte[HeaderLength];
                    await stream.ReadExactlyAsync(header, cancellation.Token);
                    int length = BitConverter.ToInt32(header, 3);
                    if (length < HeaderLength || length > 65536) throw new InvalidDataException("Invalid guild town packet length.");
                    var packet = new byte[length]; header.CopyTo(packet, 0);
                    await stream.ReadExactlyAsync(packet.AsMemory(HeaderLength), cancellation.Token);
                    packets.Add(packet);
                }
                return packets;
            }

            public void Dispose()
            {
                _fixture.Sessions.UnregisterAsync(_characterId, Session).GetAwaiter().GetResult();
                InventoryContext.Unregister(Session.SessionId, _characterId);
                Session.Close(); _reader.Dispose();
            }
        }
    }
}
