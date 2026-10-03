using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // Covers the real Town owner and socket writes. Menu receipt admission,
    // duplicate commands and command registration belong to MenuReturnSelfTest.
    internal static class GuardianAltarMenuTownProjectionSelfTest
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private const int HeaderLength = 15;

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_MENU_TOWN_PROJECTION selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Func<Task> action)
            {
                try { action().GetAwaiter().GetResult(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("real town projection", () => VerifyTownProjectionAsync(Check));
            Section("town loading response", () => VerifyFinishLoadingAsync(Check));
            Section("stale and queued scope", () => VerifyScopeAsync(Check));
            Section("account write scope", () => VerifyAccountWriteScopeAsync(Check));
            Section("missing account character and retry", () => VerifyMissingCharacterAsync(Check));
            Section("real normal controls restore", () => VerifyControlsRestoreAsync(Check));
            Section("normal controls failure and retry", () => VerifyControlsRetryAsync(Check));
            Section("normal controls source and send fences", () => VerifyControlsFencesAsync(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_MENU_TOWN_PROJECTION assertions={assertions} failures={failures}");
            return failures;
        }

        private static async Task VerifyTownProjectionAsync(Action<string, bool> check)
        {
            foreach (var level in new[] { (byte)ExpTableProvider.MaxLevel, (byte)(ExpTableProvider.MaxLevel - 1) })
            {
                using var fixture = new Fixture(level);
                var player = fixture.Session.Player;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
                var packets = await fixture.DrainAsync();
                var expectedTypes = TownTypes(includeAccount: true,
                    includeHonor: level == ExpTableProvider.MaxLevel);
                check($"level {level} projects the existing town and account sequence through a real socket",
                    completed && packets.Select(Type).SequenceEqual(expectedTypes)
                    && packets.All(packet => packet[0] == 0 && BitConverter.ToInt32(packet, 3) == packet.Length));
                VerifyTownBodies(packets, fixture, check);
                check($"level {level} keeps its actual town owner and never creates a run or selection",
                    player.CurrentRun == null && player.CurrentDungeonSelection == null
                    && !player.DungeonSelectionPending && player.TownPresenceReady && player.UserState == 0
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && fixture.Records.GetById(player.CharacterId)?.TownId == player.CurTownId
                    && fixture.GuardianRows() == 0 && fixture.AccountHonorExp() == 0);
                check($"level {level} releases the real send lock and retains the roster publisher",
                    fixture.SendGate.CurrentCount == 1 && fixture.PartyPublicationCount == 1);
                if (level == ExpTableProvider.MaxLevel)
                {
                    var honor = packets.Single(packet => Type(packet) == (ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO);
                    check("max-level account honor has the established two-u32 body and zero fixture experience",
                        honor.Length == HeaderLength + 8 && BitConverter.ToUInt32(honor, HeaderLength + 4) == 0);
                }
                else
                {
                    check("a level below the current honor threshold preserves the original no-banner rule",
                        !packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO));
                }
            }
        }

        private static void VerifyTownBodies(IReadOnlyList<byte[]> packets, Fixture fixture,
            Action<string, bool> check)
        {
            if (packets.Count < 4)
            {
                check("complete town body sequence is available", false);
                return;
            }
            var player = fixture.Session.Player;
            var state = packets[0];
            check("USER_STATE carries exactly the current UID and idle town state",
                Type(state) == (ushort)NotiPacketTypeA21.USER_STATE && state.Length == HeaderLength + 4
                && state[HeaderLength] == 1 && BitConverter.ToUInt16(state, HeaderLength + 1) == player.UserId
                && state[HeaderLength + 3] == 0);
            var area = packets[1];
            check("USER_AREA carries the unchanged town anchor with the normal arrival state",
                Type(area) == (ushort)NotiPacketTypeA21.USER_AREA && area.Length == HeaderLength + 10
                && BitConverter.ToUInt16(area, HeaderLength) == player.UserId
                && area[HeaderLength + 2] == player.CurTownId && area[HeaderLength + 3] == player.CurAreaId
                && BitConverter.ToInt16(area, HeaderLength + 4) == player.CurPosX
                && BitConverter.ToInt16(area, HeaderLength + 6) == player.CurPosY
                && area[HeaderLength + 9] == 0);
            var roster = packets[2];
            check("AREA_USERS contains the current avatar as the only roster entry",
                Type(roster) == (ushort)NotiPacketTypeA21.AREA_USERS && roster.Length == HeaderLength + 12
                && roster[HeaderLength] == player.CurTownId && roster[HeaderLength + 1] == player.CurAreaId
                && BitConverter.ToUInt16(roster, HeaderLength + 2) == 1
                && BitConverter.ToUInt16(roster, HeaderLength + 4) == player.UserId);
            var appearance = packets[3];
            check("the roster is followed by the actual self USERINFO subtype0",
                Type(appearance) == (ushort)NotiPacketTypeA21.USERINFO && appearance.Length > HeaderLength + 43
                && appearance[HeaderLength] == 0 && BitConverter.ToUInt16(appearance, HeaderLength + 1) == 1
                && BitConverter.ToUInt16(appearance, HeaderLength + 41) == player.UserId);
            if (packets.Count > 4 && Type(packets[4]) == (ushort)NotiPacketTypeA21.USERINFO)
                check("account subtype0 preserves the same live character identity",
                    packets[4].Length > HeaderLength + 43 && packets[4][HeaderLength] == 0
                    && BitConverter.ToUInt16(packets[4], HeaderLength + 41) == player.UserId);
        }

        private static async Task VerifyFinishLoadingAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture((byte)(ExpTableProvider.MaxLevel - 1));
            check("real menu town projection completes before its normal loading request",
                await fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session,
                    fixture.CaptureFence()).WaitAsync(Timeout));
            await fixture.DrainAsync();
            await fixture.Town.Handle_ENUM_CMDPACKET_FINISH_LOADING(fixture.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.FINISH_LOADING, length = 14 },
                Array.Empty<byte>()).WaitAsync(Timeout);
            var packets = await fixture.DrainAsync();
            check("runless FINISH_LOADING retains its command ACK followed by the existing completion notification",
                packets.Count == 2 && packets[0][0] == 1
                && Type(packets[0]) == (ushort)CmdPacketTypeA21.FINISH_LOADING
                && packets[0].Length == HeaderLength + 1 && packets[0][HeaderLength] == 1
                && packets[1][0] == 0 && Type(packets[1]) == (ushort)NotiPacketTypeA21.FINISH_LOADING);
            check("loading completion neither creates a run nor consumes Guardian progression",
                fixture.Session.Player.CurrentRun == null && fixture.Session.Player.CurrentDungeonSelection == null
                && fixture.GuardianRows() == 0 && fixture.SendGate.CurrentCount == 1);
        }

        private static async Task VerifyScopeAsync(Action<string, bool> check)
        {
            using (var fixture = new Fixture())
            {
                var oldFence = fixture.CaptureFence();
                fixture.Session.Player.NextDungeonRunGeneration();
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, oldFence).WaitAsync(Timeout);
                check("an already stale town scope projects no frames and invokes no roster follow-up",
                    !completed && (await fixture.DrainAsync()).Count == 0
                    && fixture.PartyPublicationCount == 0 && fixture.GuardianRows() == 0);
            }
            using (var fixture = new Fixture())
            {
                var oldFence = fixture.CaptureFence();
                await fixture.SendGate.WaitAsync();
                Task<bool> pending;
                try
                {
                    pending = fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, oldFence);
                    check("the real first town write waits on the session send lock", !pending.IsCompleted);
                    fixture.Session.Player.NextDungeonRunGeneration();
                }
                finally { fixture.SendGate.Release(); }
                var completed = await pending.WaitAsync(Timeout);
                check("scope expiry while queued suppresses the old write and returns an incomplete projection",
                    !completed && (await fixture.DrainAsync()).Count == 0
                    && fixture.PartyPublicationCount == 0 && fixture.SendGate.CurrentCount == 1);
            }
        }

        private static async Task VerifyAccountWriteScopeAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendGateHeld = false;
            fixture.Town.ConfigureTownPartyListPublisher(async () =>
            {
                // Existing roster hook: pause the later account write on the
                // actual send lock after the four town frames have been sent.
                await fixture.SendGate.WaitAsync();
                sendGateHeld = true;
                reached.TrySetResult(true);
            });
            var oldFence = fixture.CaptureFence();
            var pending = fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, oldFence);
            try
            {
                await reached.Task.WaitAsync(Timeout);
                check("account subtype0 waits after complete real town projection", !pending.IsCompleted);
                fixture.Session.Player.NextDungeonRunGeneration();
            }
            finally
            {
                if (sendGateHeld)
                    fixture.SendGate.Release();
            }
            var completed = await pending.WaitAsync(Timeout);
            var packets = await fixture.DrainAsync();
            check("scope expiry while account write is queued suppresses subtype0 and honor and cannot mark completion",
                !completed && packets.Select(Type).SequenceEqual(TownTypes(includeAccount: false, includeHonor: false))
                && fixture.SendGate.CurrentCount == 1 && fixture.GuardianRows() == 0);
        }

        private static async Task VerifyMissingCharacterAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Town.ConfigureTownPartyListPublisher(() =>
            {
                fixture.Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM characters WHERE character_id=@id;";
                    command.Parameters.AddWithValue("@id", fixture.Record.CharacterId);
                    command.ExecuteNonQuery();
                });
                return Task.CompletedTask;
            });
            var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var partial = await fixture.DrainAsync();
            check("missing durable character makes account projection fail after the existing town frames",
                !completed && partial.Select(Type).SequenceEqual(TownTypes(includeAccount: false, includeHonor: false))
                && fixture.Records.GetById(fixture.Record.CharacterId) == null);
            fixture.Records.Create(fixture.Record);
            fixture.Town.ConfigureTownPartyListPublisher(() => Task.CompletedTask);
            completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var retry = await fixture.DrainAsync();
            check("restoring a legitimate fixture record permits a complete retry without a fabricated run or selection",
                completed && retry.Select(Type).SequenceEqual(TownTypes(includeAccount: true, includeHonor: true))
                && fixture.Session.Player.CurrentRun == null && fixture.Session.Player.CurrentDungeonSelection == null
                && fixture.GuardianRows() == 0 && fixture.AccountHonorExp() == 0);
        }

        private static async Task VerifyControlsRestoreAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.ConfigureRealControls();
            var lease = fixture.RegisterControlsInventory();
            var originalSkills = SkillInfoBodyBuilder.BuildFrom(fixture.Progress.LoadSkills(fixture.Record.CharacterId));
            var expectedSkills = SkillInfoBodyBuilder.BuildFrom(fixture.Projector.LoadSyncedSkillState(
                fixture.Record.CharacterId, fixture.Record.Level, persist: false).Skills);
            byte[] expectedItems;
            lock (lease.SyncRoot)
                expectedItems = ItemListPacketBuilder.BuildItemSpaceListBody(lease.Inventory, InventoryListType.Main);
            var source = fixture.MarkControlsSource();
            fixture.RejectSkillWrites();
            var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var packets = await fixture.DrainAsync();
            var expectedTypes = TownTypes(true, true).ToList();
            expectedTypes.InsertRange(4, new[] { (ushort)NotiPacketTypeA21.SKILLINFO, (ushort)NotiPacketTypeA21.ITEM_LIST });
            check("real Town restores normal controls after AREA_USERS and self appearance before account state",
                completed && packets.Select(Type).SequenceEqual(expectedTypes)
                && fixture.Session.GuardianAltarControls == null);
            var skillPacket = packets.Single(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO);
            check("SKILLINFO rebinds both persisted skill pages through the real sync owner",
                skillPacket.AsSpan(HeaderLength).SequenceEqual(expectedSkills)
                && ReadSkillRows(skillPacket.AsSpan(HeaderLength).ToArray()).SequenceEqual(
                    SkillRows(fixture.Progress.LoadSkills(fixture.Record.CharacterId))));
            check("normal Main ITEM_LIST restores the actual owned inventory and quick item configuration",
                packets.Single(x => Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST).AsSpan(HeaderLength).SequenceEqual(expectedItems)
                && fixture.ReadPersistedItemCount() == 7 && lease.Inventory.GetItem(InventoryListType.Main, 3)?.Count == 7);
            check("normal controls projection cannot persist derived mirrors or rewrite saved skill slots",
                originalSkills.SequenceEqual(SkillInfoBodyBuilder.BuildFrom(fixture.Progress.LoadSkills(fixture.Record.CharacterId)))
                && !fixture.Session.TryCompleteGuardianControlsRestore(source));
            completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
            var repeated = await fixture.DrainAsync();
            check("a later unmarked Town arrival retains its ordinary packets without repeating control restore",
                completed && repeated.Select(Type).SequenceEqual(TownTypes(true, true)));
        }

        private static async Task VerifyControlsRetryAsync(Action<string, bool> check)
        {
            foreach (var failure in new[] { "missing owned lease", "skill read" })
            {
                using var fixture = new Fixture();
                fixture.ConfigureRealControls();
                if (failure == "skill read") fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                if (failure == "skill read") fixture.Sql("ALTER TABLE character_skills RENAME TO held_guardian_skills;");
                Exception error;
                try
                {
                    error = await ObserveAsync(() => fixture.Town.ProjectGuardianMenuReturnAsync(
                        fixture.Session, fixture.CaptureFence()));
                }
                finally
                {
                    if (failure == "skill read") fixture.Sql("ALTER TABLE held_guardian_skills RENAME TO character_skills;");
                }
                var partial = await fixture.DrainAsync();
                check(failure + " remains an observable failure and keeps the pending restore source",
                    error != null && ReferenceEquals(fixture.Session.GuardianAltarControls, source)
                    && !partial.Any(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO
                        || Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST));
                if (failure == "missing owned lease") fixture.RegisterControlsInventory();
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
                var retry = await fixture.DrainAsync();
                check(failure + " recovery reprojects through real repositories and clears only the completed source",
                    completed && fixture.Session.GuardianAltarControls == null
                    && retry.Count(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO) == 1
                    && retry.Count(x => Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST) == 1
                    && fixture.ReadPersistedItemCount() == 7);
            }
            using (var fixture = new Fixture())
            {
                fixture.ConfigureRealControls();
                fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                // Shutdown disconnects the real local socket. The session's
                // Stream getter fails in TcpClient.GetStream before WriteAsync;
                // neither that failure nor retry may complete the pending source.
                fixture.Session.TcpClient.Client.Shutdown(SocketShutdown.Send);
                var error = await ObserveAsync(() => fixture.Projector.RestoreGuardianControlsAsync(
                    fixture.Session, () => ReferenceEquals(fixture.Session.GuardianAltarControls, source)));
                Console.WriteLine("[GuardianAltarMenuTownProjection] Shutdown(Send) restore failure: " + error);
                check("a disconnected real transport fails at GetStream without restoring controls or clearing the source",
                    error is InvalidOperationException && !fixture.Session.TcpClient.Connected
                    && error.StackTrace?.Contains("TcpClient.GetStream", StringComparison.Ordinal) == true
                    && fixture.PeerAvailableBytes == 0
                    && ReferenceEquals(fixture.Session.GuardianAltarControls, source));
                error = await ObserveAsync(() => fixture.Projector.RestoreGuardianControlsAsync(
                    fixture.Session, () => ReferenceEquals(fixture.Session.GuardianAltarControls, source)));
                check("retry on the same disconnected transport fails at GetStream without completing the source",
                    error is InvalidOperationException && !fixture.Session.TcpClient.Connected
                    && error.StackTrace?.Contains("TcpClient.GetStream", StringComparison.Ordinal) == true
                    && fixture.PeerAvailableBytes == 0
                    && ReferenceEquals(fixture.Session.GuardianAltarControls, source));
            }
        }

        private static async Task VerifyControlsFencesAsync(Action<string, bool> check)
        {
            foreach (var stale in new[] { "generation", "account", "uid", "run epoch", "selection epoch" })
            {
                using var fixture = new Fixture();
                fixture.ConfigureRealControls();
                fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                fixture.Session.GuardianAltarControls = stale switch {
                    "generation" => source with { Generation = null },
                    "account" => source with { Account = new AccountRecord { AccountId = fixture.Record.AccountId } },
                    "uid" => source with { UserId = 0 },
                    "run epoch" => source with { RunIdentity = new DungeonRunIdentity(1, 1, source.RunIdentity.RunGeneration + 1) },
                    _ => source with { SelectionGeneration = source.SelectionGeneration + 1 }
                };
                var pendingSource = fixture.Session.GuardianAltarControls;
                var completed = await fixture.Town.ProjectGuardianMenuReturnAsync(
                    fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
                var packets = await fixture.DrainAsync();
                check("stale control " + stale + " emits no restore while ordinary Town remains available",
                    completed && packets.Select(Type).SequenceEqual(TownTypes(true, true))
                    && ReferenceEquals(fixture.Session.GuardianAltarControls, pendingSource));
            }
            using (var fixture = new Fixture())
            {
                fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var sendGateHeld = false;
                fixture.Town.ConfigureGuardianControlsRestore(async (session, current) =>
                {
                    await fixture.SendGate.WaitAsync();
                    sendGateHeld = true;
                    entered.TrySetResult(true);
                    return await fixture.Projector.RestoreGuardianControlsAsync(session, current);
                });
                var pending = fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, fixture.CaptureFence());
                EnhancedClientSession.GuardianAltarControlProjectionSource replacement;
                try
                {
                    await entered.Task.WaitAsync(Timeout);
                    replacement = source with { };
                    fixture.Session.GuardianAltarControls = replacement;
                    check("real skill restore waits on the actual send lock after the town roster", !pending.IsCompleted);
                }
                finally { if (sendGateHeld) fixture.SendGate.Release(); }
                await pending.WaitAsync(Timeout);
                var packets = await fixture.DrainAsync();
                check("send-lock source replacement suppresses old skill and Main frames without clearing the new source",
                    !packets.Any(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO
                        || Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST)
                    && ReferenceEquals(fixture.Session.GuardianAltarControls, replacement)
                    && !fixture.Session.TryCompleteGuardianControlsRestore(source));
            }
            using (var fixture = new Fixture())
            {
                var lease = fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                await fixture.SendGate.WaitAsync();
                Task<bool> pending;
                EnhancedClientSession.GuardianAltarControlProjectionSource replacement;
                try
                {
                    pending = InventoryRefreshSender.TrySendOwnedItemListRefresh(fixture.Session,
                        lease, InventoryListType.Main, () => ReferenceEquals(fixture.Session.GuardianAltarControls, source));
                    check("the real owned Main refresh waits on the actual send lock", !pending.IsCompleted);
                    replacement = source with { };
                    fixture.Session.GuardianAltarControls = replacement;
                }
                finally { fixture.SendGate.Release(); }
                check("owned Main final send fence rejects a replaced source and cannot clear its replacement",
                    !await pending.WaitAsync(Timeout) && (await fixture.DrainAsync()).Count == 0
                    && ReferenceEquals(fixture.Session.GuardianAltarControls, replacement)
                    && !fixture.Session.TryCompleteGuardianControlsRestore(source) && fixture.SendGate.CurrentCount == 1);
            }
            using (var fixture = new Fixture())
            {
                fixture.RegisterControlsInventory();
                var source = fixture.MarkControlsSource();
                EnhancedClientSession.GuardianAltarControlProjectionSource replacement = null;
                fixture.Town.ConfigureGuardianControlsRestore(async (session, current) =>
                {
                    var restored = await fixture.Projector.RestoreGuardianControlsAsync(session, current);
                    replacement = source with { };
                    session.GuardianAltarControls = replacement;
                    return restored;
                });
                await fixture.Town.ProjectGuardianMenuReturnAsync(fixture.Session, fixture.CaptureFence()).WaitAsync(Timeout);
                var packets = await fixture.DrainAsync();
                check("a replacement published after real restore cannot be cleared by the old completion checkpoint",
                    packets.Count(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO) == 1
                    && packets.Count(x => Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST) == 1
                    && replacement != null && ReferenceEquals(fixture.Session.GuardianAltarControls, replacement)
                    && !fixture.Session.TryCompleteGuardianControlsRestore(source));
            }
        }

        private static IEnumerable<string> SkillRows(SkillInfoSnapshot skills)
            => skills.Pages.SelectMany((page, index) => page.Entries.Select(entry =>
                $"{index}:{entry.Slot}:{entry.SkillId}:{entry.Level}:{Convert.ToHexString(entry.ExtraValues.ToArray())}"));

        private static IReadOnlyList<string> ReadSkillRows(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            var rows = new List<string>();
            for (var page = 0; page < 2; page++)
            {
                reader.ReadUInt16();
                var count = reader.ReadByte();
                for (var index = 0; index < count; index++)
                {
                    var slot = reader.ReadByte();
                    var id = reader.ReadUInt16();
                    var level = reader.ReadByte();
                    var extra = reader.ReadBytes(reader.ReadByte());
                    rows.Add($"{page}:{slot}:{id}:{level}:{Convert.ToHexString(extra)}");
                }
            }
            reader.ReadUInt16();
            reader.ReadUInt16();
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Restored SKILLINFO did not contain exactly two pages and two tails.");
            return rows;
        }

        private static async Task<Exception> ObserveAsync(Func<Task> action)
        {
            try { await action().WaitAsync(Timeout); return null; }
            catch (Exception exception) { return exception; }
        }

        private static ushort[] TownTypes(bool includeAccount, bool includeHonor)
        {
            var types = new List<ushort>
            {
                (ushort)NotiPacketTypeA21.USER_STATE,
                (ushort)NotiPacketTypeA21.USER_AREA,
                (ushort)NotiPacketTypeA21.AREA_USERS,
                (ushort)NotiPacketTypeA21.USERINFO
            };
            if (includeAccount) types.Add((ushort)NotiPacketTypeA21.USERINFO);
            if (includeHonor) types.Add((ushort)NotiPacketTypeA21.HONOR_LEVEL_INFO);
            return types.ToArray();
        }

        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "guardian-menu-town-" + Guid.NewGuid().ToString("N"));
            private readonly TcpClient _peer = new();
            internal readonly SessionDirectory Sessions = new((_, _) => true);
            internal GameDatabase Database { get; }
            internal SqliteCharacterRepository Records { get; }
            internal CharacterRecord Record { get; }
            internal EnhancedClientSession Session { get; }
            internal TownHandler Town { get; }
            internal SqliteCharacterProgressRepository Progress { get; }
            internal DungeonProgressNotificationProjector Projector { get; }
            internal SemaphoreSlim SendGate { get; }
            internal int PeerAvailableBytes => _peer.Available;
            internal int PartyPublicationCount { get; private set; }

            internal Fixture(byte level = (byte)ExpTableProvider.MaxLevel)
            {
                var archive = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
                if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
                    throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current PVF before this selftest.", archive);
                Directory.CreateDirectory(_directory);
                TcpClient accepted = null;
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    Database = new GameDatabase(Path.Combine(_directory, "inventory.db"), ServerPaths.SchemaFilePath);
                    Database.Write((connection, transaction) =>
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(11,'guardian-menu-town-test','');";
                        command.ExecuteNonQuery();
                    });
                    Records = new SqliteCharacterRepository(Database);
                    var spawn = GameChannelSpawnPolicy.Resolve(GameNetworkConfig.NormalGamePort, 0);
                    Record = new CharacterRecord
                    {
                        CharacterId = 62021, AccountId = 11, Name = Encoding.ASCII.GetBytes("guardian-town"),
                        Level = level, Appearance = Array.Empty<CharacterAppearanceEntry>()
                    };
                    spawn.ApplyTo(Record);
                    Record.AreaState = 0;
                    Records.Create(Record);
                    listener.Start();
                    var endpoint = (IPEndPoint)listener.LocalEndpoint;
                    var connecting = _peer.ConnectAsync(endpoint.Address, endpoint.Port);
                    accepted = listener.AcceptTcpClientAsync().WaitAsync(Timeout).GetAwaiter().GetResult();
                    connecting.WaitAsync(Timeout).GetAwaiter().GetResult();
                    accepted.NoDelay = true;
                    _peer.NoDelay = true;
                    Session = new EnhancedClientSession(accepted, new GamePacketHeader(), GameNetworkConfig.NormalGamePort)
                    {
                        Account = new AccountRecord { AccountId = Record.AccountId }
                    };
                    var player = Session.Player;
                    player.CharacterId = Record.CharacterId;
                    player.UserId = (ushort)Record.CharacterId;
                    player.Name = Record.Name;
                    player.Level = Record.Level;
                    player.Job = Record.Job;
                    player.GrowType = Record.GrowType;
                    player.UserState = 0;
                    player.TownPresenceReady = true;
                    player.CurTownId = Record.TownId;
                    player.CurAreaId = Record.AreaId;
                    player.CurPosX = Record.PosX;
                    player.CurPosY = Record.PosY;
                    player.CurDirection = Record.Direction;
                    player.CurAreaState = Record.AreaState;
                    player.AppearanceEntries = Record.Appearance;
                    player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                    Session.GameSession = new GameSession(Session, Database, Records, sessionDirectory: Sessions);
                    Sessions.Register(player.CharacterId, Session);
                    SendGate = (SemaphoreSlim)(typeof(EnhancedClientSession).GetField("_sendLock",
                        BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Session)
                        ?? throw new InvalidOperationException("The real session send lock was not found."));
                    Town = new TownHandler(Records, selectDataSource: null, partyManager: null, sessions: Sessions,
                        refresh: null, dungeonInstances: null, raidManager: null, database: Database);
                    Town.ConfigureTownPartyListPublisher(() =>
                    {
                        PartyPublicationCount++;
                        return Task.CompletedTask;
                    });
                    Progress = new SqliteCharacterProgressRepository(Database);
                    Projector = new DungeonProgressNotificationProjector(Database.ConnectionString, Records,
                        new SqliteSubtype1Repository(Database), Progress, new SqliteSubtype0FieldsRepository(Database),
                        new HonorLevelSyncService(Records, Database), new AccountExperienceProgressService(Records, Database), Sessions);
                }
                catch
                {
                    accepted?.Close();
                    _peer.Dispose();
                    DeleteDatabase();
                    throw;
                }
                finally { listener.Stop(); }
            }

            internal Func<bool> CaptureFence()
            {
                var player = Session.Player;
                var generation = Session.GameSession;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                return () => Sessions.TryGet(Record.CharacterId, out var current)
                    && ReferenceEquals(current, Session) && ReferenceEquals(Session.GameSession, generation)
                    && SessionDirectory.IsTownPresence(player) && player.CurrentDungeonSelection == null
                    && !player.DungeonSelectionPending && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration;
            }

            internal void ConfigureRealControls()
                => Town.ConfigureGuardianControlsRestore(Projector.RestoreGuardianControlsAsync);

            internal EnhancedClientSession.GuardianAltarControlProjectionSource MarkControlsSource()
            {
                var generation = Session.Player.CurrentDungeonRunGeneration;
                if (generation == 0) generation = Session.Player.NextDungeonRunGeneration();
                // Synthetic ended-run identity only. Restore authority comes
                // from the real session epochs, repositories and owned lease.
                var source = new EnhancedClientSession.GuardianAltarControlProjectionSource(Session.GameSession,
                    Session.Account, Record.CharacterId, Record.AccountId, Session.Player.UserId,
                    new DungeonRunIdentity(62021, 62021, generation), Session.Player.CurrentDungeonSelectionGeneration);
                Session.GuardianAltarControls = source;
                return source;
            }

            internal InventoryLease RegisterControlsInventory()
            {
                var catalog = CharacterSkillProfile.BuildSnapshot(Record.Job, 0, 0, Record.Level);
                var known = catalog.Pages[0].Entries.First(entry => SkillDataProvider.GetSkill(Record.Job, entry.SkillId)
                    is { IsActive: true } skill && skill.IsAvailableFor(0, 0));
                var saved = new SkillInfoSnapshot();
                foreach (var slot in new byte[] { 5, 9 })
                {
                    var page = new SkillInfoPageSnapshot();
                    page.Entries.Add(new SkillInfoEntrySnapshot { Slot = slot, SkillId = known.SkillId, Level = known.Level });
                    saved.Pages.Add(page);
                }
                Progress.SaveSkillProgress(Record.CharacterId, saved);
                InventoryService inventory;
                using (var connection = Database.OpenConnection())
                    inventory = InventoryService.LoadFromDb(connection, Record.CharacterId, Record.AccountId, Database);
                var item = ItemCore.Create(ItemCore.KindConsumable, 1031);
                item.Count = 7;
                inventory.SetItem(InventoryListType.Main, 3, item);
                var lease = InventoryContext.Register(Session.SessionId, Record.CharacterId, inventory);
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "selftest-guardian-normal-controls"))
                    throw new InvalidOperationException("The real controls fixture inventory could not be committed.");
                return lease;
            }

            internal int ReadPersistedItemCount()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(connection, Record.CharacterId, Record.AccountId, Database).CountMainItem(1031);
            }

            internal void RejectSkillWrites()
            {
                foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                    Sql($"CREATE TRIGGER reject_restore_{operation} BEFORE {operation} ON character_skills BEGIN SELECT RAISE(ABORT,'restore must remain read-only'); END;");
            }

            internal void Sql(string sql) => Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            });

            internal async Task<List<byte[]>> DrainAsync()
            {
                var packets = new List<byte[]>();
                var stream = _peer.GetStream();
                using var cancellation = new CancellationTokenSource(Timeout);
                while (_peer.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_peer.Available == 0)
                        throw new IOException("The town projection unexpectedly closed its real connection.");
                    var header = new byte[HeaderLength];
                    await stream.ReadExactlyAsync(header, cancellation.Token);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < HeaderLength || length > 65536)
                        throw new InvalidDataException("Invalid A21 town envelope length.");
                    var packet = new byte[length];
                    header.CopyTo(packet, 0);
                    await stream.ReadExactlyAsync(packet.AsMemory(HeaderLength), cancellation.Token);
                    packets.Add(packet);
                }
                return packets;
            }

            internal long GuardianRows() => Scalar("SELECT COUNT(*) FROM character_guardian_altar;");
            internal long AccountHonorExp() => Scalar("SELECT honor_exp FROM accounts WHERE account_id=11;");
            private long Scalar(string sql) => Database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });

            public void Dispose()
            {
                InventoryContext.Unregister(Session.SessionId, Record.CharacterId);
                Session?.Close();
                _peer.Dispose();
                DeleteDatabase();
            }

            private void DeleteDatabase()
            {
                SqliteConnection.ClearAllPools();
                var path = Path.Combine(_directory, "inventory.db");
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
        }
    }
}
