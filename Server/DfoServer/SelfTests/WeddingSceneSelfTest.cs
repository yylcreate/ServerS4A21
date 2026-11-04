using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    internal static class WeddingSceneSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            try
            {
                var input = new Dictionary<string, string>
                {
                    ["town/town.lst"] = "2 `church.twn`\n101 `house.twn`",
                    ["town/church.twn"] = "[area]\n17 `Church/Church.map`\n`[normal]`\n[/area]",
                    ["town/house.twn"] = "[area]\n6 `CoupleRoom/CoupleRoom.map`\n`[couple room]` 474 234\n[/area]",
                };
                var custom = WeddingSceneCatalog.Load(p => input[p]);
                Check("scene ids follow PVF town and area entries rather than another client's ids",
                    custom.CoupleRoom.Town == 101 && custom.CoupleRoom.Area == 6
                    && custom.CeremonyRooms.Single().Town == 2 && custom.CeremonyRooms[0].Area == 17);
                input["town/town.lst"] += "\n78 `house.twn`";
                bool rejected = false; try { WeddingSceneCatalog.Load(p => input[p]); } catch (System.IO.InvalidDataException) { rejected = true; }
                Check("ambiguous private homes fail configuration validation", rejected);
                var actual = WeddingSceneCatalog.Get();
                Check("the actual A21 PVF supplies church rooms and a private couple home", actual.CeremonyRooms.Count > 0 && actual.CoupleRoom != null);
                Check("all allocated ceremonies support the native automatic-exit bounds", actual.CeremonyRooms.All(x => x.Town == 2 && x.Area is >= 12 and <= 62)
                    && actual.ChurchScenes.Any(x => x.Area == 63) && actual.CeremonyRooms.All(x => x.Area != 63));
                Check("a historic saved home returns through a real Seria gate", GameChannelSpawnPolicy.Resolve(0, actual.CoupleRoom.Town).TownId != actual.CoupleRoom.Town);
                Check("private home coordinates never replace the persisted return town", !GameChannelSpawnPolicy.ShouldPersistPosition(0, actual.CoupleRoom.Town));
                CheckCohorts(Check).GetAwaiter().GetResult();
                CheckActualPrivatePaths(Check).GetAwaiter().GetResult();
                CheckChurchAudiences(Check).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            Console.WriteLine($"WEDDING_SCENE: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }

        private static async Task CheckActualPrivatePaths(Action<string, bool> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "a21-private-paths-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                var sessions = new SessionDirectory(); sessions.ConfigureTownCohort(WeddingSceneAdmission.SameCohort);
                using var a = await Peer.Create(sessions, 63301, "私房甲");
                using var b = await Peer.Create(sessions, 63302, "私房乙");
                using var c = await Peer.Create(sessions, 63303, "别家角色");
                var home = WeddingSceneCatalog.Get().CoupleRoom;
                foreach (var p in new[] { a, b, c })
                { p.Session.Player.CurTownId = home.Town; p.Session.Player.CurAreaId = home.Area; p.Session.Player.WeddingRoomPairId = p == c ? 22 : 11; }
                var town = new TownHandler(new SqliteCharacterRepository(database), null, null, sessions, null,
                    dungeonInstances: null, raidManager: null, database: database);
                check("party teleport treats another couple's home as a different room",
                    TownHandler.IsPartyTeleportSameRoom(a.Session.Player, b.Session.Player)
                    && !TownHandler.IsPartyTeleportSameRoom(a.Session.Player, c.Session.Player));
                var context = PartyHandler.BuildCrossAreaPartyInviteContextPacket(a.Session, c.Session);
                check("a direct invitation across private homes receives global user context without a scene roster",
                    context != null && BitConverter.ToUInt16(context, 1) == (ushort)NotiPacketTypeA21.USERINFO
                    && PartyHandler.BuildCrossAreaPartyInviteContextPacket(a.Session, b.Session) == null);
                using (var chat = new ChatHandler(sessions, new PartyManager(), new CharacterTransitionCoordinator(sessions)))
                {
                    var recipients = chat.ResolveRecipients(a.Session, new ChatMessageRequest(3, 0, 0, 0,
                        ClientTextEncoding.GetBytes("婚房聊天"), Array.Empty<byte>()));
                    check("ordinary area chat has only the sender and spouse as recipients",
                        recipients.Count == 2 && recipients.Contains(a.Session) && recipients.Contains(b.Session) && !recipients.Contains(c.Session));
                }
                a.Session.Player.NextTownMovementSequence(); // Bypass the unrelated first-arrival hydration path.
                var position = new byte[7]; BitConverter.GetBytes((short)474).CopyTo(position, 0);
                BitConverter.GetBytes((short)234).CopyTo(position, 2); position[4] = 5;
                await town.Handle_ENUM_CMDPACKET_SET_USER_POSITION(a.Session, default, position);
                var selfMovement = a.Drain(); var spouseMovement = b.Drain();
                check("the real position handler echoes self and broadcasts only inside the private cohort",
                    selfMovement.Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_POSITION)
                    && spouseMovement.Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_POSITION)
                    && c.Drain().Count == 0);
                await sessions.UnregisterAsync(63301, a.Session);
                await town.NotifyLeaveAsync(a.Session); // The production order: unregister has already returned.
                check("the real leave handler retains the old home audience after unregister has returned",
                    b.Drain().Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_LEAVE)
                    && c.Drain().Count == 0);
                using var replacement = await Peer.Create(sessions, 63301, "新会话");
                replacement.Session.Player.CurTownId = home.Town; replacement.Session.Player.CurAreaId = home.Area;
                replacement.Session.Player.WeddingRoomPairId = 22;
                await town.NotifyLeaveAsync(a.Session); // Same CID now resolves to a different current pair.
                check("a late old-session leave uses its captured cohort rather than the replacement character's home",
                    b.Drain().Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_LEAVE)
                    && c.Drain().Count == 0 && replacement.Drain().Count == 0);
                await sessions.UnregisterAsync(63301, replacement.Session);
                await sessions.UnregisterAsync(63302, b.Session); await sessions.UnregisterAsync(63303, c.Session);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }

        private static async Task CheckChurchAudiences(Action<string, bool> check)
        {
            string path = Path.Combine(Path.GetTempPath(), "a21-church-cohorts-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var sql = connection.CreateCommand(); sql.Transaction = transaction;
                    sql.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63401,'church-cohort','');"
                        + "INSERT INTO characters(character_id,account_id,name) VALUES(63401,63401,'来宾甲');";
                    sql.ExecuteNonQuery();
                });
                var sessions = new SessionDirectory();
                sessions.ConfigureTownCohort(WeddingSceneAdmission.SameCohort, WeddingSceneAdmission.CaptureCohort);
                using var a = await Peer.Create(sessions, 63401, "来宾甲");
                using var b = await Peer.Create(sessions, 63402, "来宾乙");
                using var c = await Peer.Create(sessions, 63403, "下场来宾");
                var church = WeddingSceneCatalog.Get().CeremonyRooms[0];
                foreach (var peer in new[] { a, b, c })
                {
                    peer.Session.Player.CurTownId = church.Town; peer.Session.Player.CurAreaId = church.Area;
                    peer.Session.Player.BindWeddingCeremony(55, peer == c ? 2000 : 1000);
                }
                b.Session.Player.Wedding = WeddingState.Empty(63402);
                check("church audiences use pair and reservation start without changing a guest's own marriage",
                    sessions.GetSessionsInArea(church.Town, church.Area, 63401, a.Session.ListenerPort)
                        .Select(s => s.Player.CharacterId).SequenceEqual(new[] { 63402 })
                    && b.Session.Player.Wedding.Status == WeddingStatus.None);
                check("a church lookup without an attached source cannot reveal attendees",
                    sessions.GetSessionsInArea(church.Town, church.Area, 999, a.Session.ListenerPort).Count == 0);
                check("party teleport does not cross successive ceremonies reusing the same room and pair",
                    TownHandler.IsPartyTeleportSameRoom(a.Session.Player, b.Session.Player)
                    && !TownHandler.IsPartyTeleportSameRoom(a.Session.Player, c.Session.Player));

                var sendLock = (System.Threading.SemaphoreSlim)typeof(EnhancedClientSession)
                    .GetField("_sendLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(b.Session);
                var packet = GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.USER_LEAVE, new byte[] { 1, 0 });
                await sendLock.WaitAsync();
                Task pending;
                try
                {
                    pending = sessions.BroadcastToAreaForAsync(a.Session.Player, church.Town, church.Area,
                        63401, packet, a.Session.ListenerPort);
                    b.Session.Player.BindWeddingCeremony(55, 2000);
                }
                finally { sendLock.Release(); }
                await pending;
                check("a queued old ceremony departure is discarded after a recipient joins the next event",
                    b.Drain().Count == 0 && c.Drain().Count == 0);
                b.Session.Player.BindWeddingCeremony(55, 1000);
                var frozen = WeddingSceneAdmission.CaptureCohort(a.Session.Player);
                a.Session.Player.ClearWeddingCeremony();
                check("a frozen event audience survives clearing the source's live attendance",
                    frozen(b.Session.Player) && !frozen(c.Session.Player));
                a.Session.Player.BindWeddingCeremony(55, 1000);

                var admission = new WeddingSceneAdmission(database, sessions);
                var town = new TownHandler(new SqliteCharacterRepository(database), null, null, sessions, null,
                    dungeonInstances: null, raidManager: null, database: database);
                town.ConfigureWeddingAreaAdmission(admission.CanEnter, captureEntryCommit: admission.CaptureEntryCommit);
                await sendLock.WaitAsync();
                Task leaving;
                try
                {
                    leaving = town.Handle_ENUM_CMDPACKET_SET_USER_AREA(a.Session, default, new byte[] { 1, 0, 0, 0, 0, 0 });
                    check("public-area permission preserves the old ceremony key until its departure completes",
                        WeddingSceneAdmission.IsCeremony(a.Session.Player, 55, 1000));
                }
                finally { sendLock.Release(); }
                await leaving; a.Drain();
                check("the real town transition leaves only the old audience and clears attendance before arrival",
                    b.Drain().Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_AREA)
                    && c.Drain().Count == 0 && a.Session.Player.WeddingCeremony == null
                    && a.Session.Player.CurTownId == 1);

                // Hold the arrival socket after the roster operation starts.
                // A peer leaving in this interval must shrink the mandatory
                // AREA_USERS frame, rather than suppress it and strand loading.
                b.Session.Player.CurTownId = a.Session.Player.CurTownId;
                b.Session.Player.CurAreaId = a.Session.Player.CurAreaId;
                b.Session.Player.ClearWeddingCeremony();
                var arrivalLock = (System.Threading.SemaphoreSlim)typeof(EnhancedClientSession)
                    .GetField("_sendLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(a.Session);
                var rosterMethod = typeof(TownHandler).GetMethod("BroadcastAreaRosterAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                await arrivalLock.WaitAsync();
                Task loading;
                try
                {
                    loading = (Task)rosterMethod.Invoke(town, new object[] {
                        a.Session, DfoServer.Network.Builders.TownAreaNotificationBuilder.CreateCurrentSnapshot(a.Session.Player),
                        Activator.CreateInstance(rosterMethod.GetParameters()[2].ParameterType), null });
                    b.Session.Player.CurAreaId = (byte)(a.Session.Player.CurAreaId + 1);
                }
                finally { arrivalLock.Release(); }
                await loading;
                var arrivalFrames = a.Drain();
                var areaUsers = arrivalFrames.SingleOrDefault(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.AREA_USERS);
                int rosterIndex = arrivalFrames.FindIndex(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.AREA_USERS);
                int appearanceIndex = arrivalFrames.FindIndex(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USERINFO);
                check("a peer departure during arrival's send wait still sends a fresh self-only AREA_USERS before appearance",
                    areaUsers != null && BitConverter.ToUInt16(areaUsers, 17) == 1
                    && BitConverter.ToUInt16(areaUsers, 19) == a.Session.Player.UserId
                    && rosterIndex >= 0 && appearanceIndex > rosterIndex
                    && !arrivalFrames.Any(p => BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.USER_AREA
                        && BitConverter.ToUInt16(p, 15) == b.Session.Player.UserId));
                b.Drain();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int channel = GameNetworkConfig.TryResolveGameChannel(a.Session.ListenerPort, out var endpoint) ? endpoint.ChannelId : 0;
                var reserved = database.Write((connection, transaction) =>
                {
                    using var sql = connection.CreateCommand(); sql.Transaction = transaction;
                    sql.CommandText = "INSERT INTO characters(character_id,account_id,name) VALUES(63404,63401,'另一新人');";
                    sql.ExecuteNonQuery();
                    var engaged = WeddingRepository.Create(connection, transaction, 63401, 63404, now);
                    var next = engaged with { Status = WeddingStatus.Reserved, StartAt = now + 3600, Room = church.Area, ChannelId = channel };
                    if (!WeddingRepository.Save(connection, transaction, engaged, next)) throw new InvalidOperationException("Church admission fixture failed.");
                    return WeddingRepository.Load(connection, transaction, 63401);
                });
                // Seria (the preceding 1:0 arrival) is private per character,
                // so its peer socket cannot block a shared-area departure.
                // Start from the existing public town fixture area instead.
                a.Session.Player.CurTownId = 1; a.Session.Player.CurAreaId = 1;
                b.Session.Player.CurTownId = 1; b.Session.Player.CurAreaId = 1;
                b.Session.Player.ClearWeddingCeremony();
                var churchBody = new byte[] { church.Town, church.Area, 0, 0, 0, 0 };
                await sendLock.WaitAsync();
                Task revokedEntry;
                try
                {
                    revokedEntry = town.Handle_ENUM_CMDPACKET_SET_USER_AREA(a.Session, default, churchBody);
                    check("the cancellation fixture actually waits on a shared old-area departure before admission commits",
                        SessionDirectory.IsShareableTownArea(1, 1) && !revokedEntry.IsCompleted
                        && a.Session.Player.WeddingCeremony == null
                        && a.Session.Player.WeddingAreaAdmission?.Ceremony?.PairId == reserved.PairId
                        && a.Session.Player.WeddingAreaAdmission?.Ceremony?.StartAt == reserved.StartAt);
                    database.Write((connection, transaction) =>
                    {
                        if (!WeddingRepository.Save(connection, transaction, reserved,
                            reserved with { Status = WeddingStatus.Engaged, StartAt = 0, Room = 0, ChannelId = 0 }))
                            throw new InvalidOperationException("Church cancellation fixture failed.");
                    });
                }
                finally { sendLock.Release(); }
                await revokedEntry; var revokedFrames = a.Drain(); b.Drain();
                var otherEventFrames = c.Drain();
                bool safelyReturned = a.Session.Player.WeddingCeremony == null
                    && (a.Session.Player.CurTownId != church.Town || a.Session.Player.CurAreaId != church.Area)
                    && otherEventFrames.Count == 0;
                if (!safelyReturned)
                    Console.WriteLine($"[DIAG] revoked church entry: target={church.Town}:{church.Area} current={a.Session.Player.CurTownId}:{a.Session.Player.CurAreaId} "
                        + $"attendance={a.Session.Player.WeddingCeremony} ticket={a.Session.Player.WeddingAreaAdmission} "
                        + $"selfOpcodes={string.Join(',', revokedFrames.Select(p => BitConverter.ToUInt16(p, 1)))} otherEventFrames={otherEventFrames.Count}");
                check("cancellation while old departure is blocked rechecks authority and returns through the public spawn",
                    safelyReturned);
                a.Session.Player.BindWeddingCeremony(55, 1000);
                a.Session.Player.HydrateIdentityFrom(new CharacterRecord { CharacterId = 63405 });
                check("character hydration cannot inherit another character's ceremony identity",
                    a.Session.Player.WeddingCeremony == null && a.Session.Player.WeddingAreaAdmission == null
                    && a.Session.Player.WeddingRoomPairId == 0);
                a.Session.Player.CharacterId = 63401; // Directory teardown uses the original registered character.
                await sessions.UnregisterAsync(63401, a.Session);
                await sessions.UnregisterAsync(63402, b.Session); await sessions.UnregisterAsync(63403, c.Session);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }

        private static async Task CheckCohorts(Action<string, bool> check)
        {
            var sessions = new SessionDirectory(); sessions.ConfigureTownCohort(WeddingSceneAdmission.SameCohort);
            using var a = await Peer.Create(sessions, 63201, "婚房甲");
            using var b = await Peer.Create(sessions, 63202, "婚房乙");
            using var c = await Peer.Create(sessions, 63203, "另一婚房");
            var home = WeddingSceneCatalog.Get().CoupleRoom;
            foreach (var p in new[] { a, b, c })
            { p.Session.Player.CurTownId = home.Town; p.Session.Player.CurAreaId = home.Area; p.Session.Player.WeddingRoomPairId = p == c ? 2 : 1; }
            check("same physical home coordinates expose only the current couple",
                sessions.GetSessionsInArea(home.Town, home.Area, 63201, a.Session.ListenerPort).Select(x => x.Player.CharacterId).SequenceEqual(new[] { 63202 }));
            check("a home query with no current source cannot disclose occupants", sessions.GetSessionsInArea(home.Town, home.Area, 999, a.Session.ListenerPort).Count == 0);
            bool departure = false;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> inheritedContext = null;
            sessions.SessionEnding += (cid, old) =>
            {
                if (cid == 63201)
                {
                    departure = sessions.GetSessionsInArea(home.Town, home.Area, cid, old.ListenerPort)
                        .Select(x => x.Player.CharacterId).SequenceEqual(new[] { 63202 });
                    inheritedContext = Task.Run(async () =>
                    {
                        await release.Task;
                        return sessions.GetSessionsInArea(home.Town, home.Area, cid, old.ListenerPort).Count == 0;
                    });
                }
                return Task.CompletedTask;
            };
            await sessions.UnregisterAsync(63201, a.Session);
            check("disconnect departure retains its former private audience without leaking another pair", departure);
            check("departure context cannot outlive the lifecycle notification", sessions.GetSessionsInArea(home.Town, home.Area, 63201, a.Session.ListenerPort).Count == 0);
            release.SetResult();
            check("a background continuation cannot retain a retired departure audience", await inheritedContext);
            await sessions.UnregisterAsync(63202, b.Session); await sessions.UnregisterAsync(63203, c.Session);
        }
    }
}
