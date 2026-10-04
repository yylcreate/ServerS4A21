using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarMenuTownProjectionSelfTest
    {
        private static async Task VerifyExistingReturnMenuReadAsync(Action<string, bool> check)
        {
            using (var fixture = new Fixture())
            {
                fixture.ConfigureMenuOwner(open: false);
                var error = await ObserveAsync(() => Task.Run(() => fixture.ReadExistingGuardianProgress()));
                check("existing-menu capability rejects missing progress without initializing any Guardian or daily state",
                    error is InvalidOperationException && fixture.GuardianRows() == 0
                    && fixture.DailyReceiptSnapshot().Length == 0);
                error = await ObserveAsync(() => Task.Run(() => fixture.GuardianProgress.ReadExistingMenu(
                    fixture.Record.CharacterId, fixture.Record.AccountId + 1)));
                check("existing-menu read requires the real durable account owner before creating or reading progress",
                    error is InvalidOperationException && fixture.GuardianRows() == 0
                    && fixture.DailyReceiptSnapshot().Length == 0);
            }
            using (var fixture = new Fixture())
            {
                fixture.ConfigureMenuOwner();
                await fixture.MenuAsync();
                await fixture.DrainAsync();
                fixture.Sql("UPDATE character_guardian_altar SET free_tickets=1, secondary_tickets=2, "
                    + "stage_stars=3, other_stars=1, available_stars=2 WHERE character_id=" + fixture.Record.CharacterId + ";");
                var before = fixture.ReadExistingGuardianProgress();
                var receipt = fixture.DailyReceiptSnapshot();
                fixture.ForbidGuardianAndDailyWrites();
                fixture.AdvanceMenuDay();
                var after = fixture.ReadExistingGuardianProgress();
                check("real existing-menu read across a day boundary preserves all durable tickets, stars and vectors",
                    SameGuardianProgress(before, after) && after.Header.FreeTickets == 1
                    && after.Header.SecondaryTickets == 2 && after.Header.StageStars == 3 && after.Header.AvailableStars == 2);
                check("read-only existing menu cannot rewrite the daily anchor or refill receipt",
                    receipt.SequenceEqual(fixture.DailyReceiptSnapshot()));
                var calls = 0;
                var error = await ObserveAsync(() => Task.Run(() => fixture.GuardianProgress.ReadExistingMenu(
                    fixture.Record.CharacterId, fixture.Record.AccountId, () => ++calls == 1)));
                check("real existing-menu final source fence rejects expiry without partial record or daily writes",
                    error is InvalidOperationException && calls == 2
                    && SameGuardianProgress(before, fixture.ReadExistingGuardianProgress())
                    && receipt.SequenceEqual(fixture.DailyReceiptSnapshot()));
                error = await ObserveAsync(() => Task.Run(() => fixture.GuardianProgress.ReadExistingMenu(
                    fixture.Record.CharacterId, fixture.Record.AccountId + 1)));
                check("initialized existing-menu state cannot be read through another durable account owner",
                    error is InvalidOperationException && SameGuardianProgress(before, fixture.ReadExistingGuardianProgress())
                    && receipt.SequenceEqual(fixture.DailyReceiptSnapshot()));
            }
        }

        private static async Task VerifyActualClosedContinuationAsync(Action<string, bool> check)
        {
            foreach (var option in new byte[] { 1, 2 })
            {
                using var fixture = new Fixture();
                fixture.ConfigureRealControls();
                fixture.RegisterControlsInventory();
                fixture.ConfigureMenuOwner();
                await fixture.MenuAsync();
                await fixture.DrainAsync();
                var run = await fixture.PrepareActualGuardianTerminalAsync();
                await fixture.DrainAsync();
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                check("the continuation test obtains a real resource-bound center failure and dedicated result UI",
                    combat.TerminalFact is { Result: GuardianAltarTerminalResult.Failed }
                    && combat.Presentation.EplpProjectionSent && combat.Presentation.CommitResult == null);
                var before = fixture.ReadExistingGuardianProgress();
                var receipt = fixture.DailyReceiptSnapshot();
                fixture.CloseMenuWindow();
                fixture.ForbidGuardianAndDailyWrites();
                var consumed = await fixture.ActualGuardianEplpAsync(2, option);
                var packets = await fixture.DrainAsync();
                check("a closed-window actual EPLP hover only acknowledges and leaves the real run attached",
                    consumed && packets.Count == 1 && Type(packets[0]) == (ushort)CmdPacketTypeA21.EPLP_COMMAND
                    && packets[0].AsSpan(HeaderLength).SequenceEqual(new byte[] { 1, 2, option })
                    && ReferenceEquals(fixture.Session.Player.CurrentRun, run));
                consumed = await fixture.ActualGuardianEplpAsync(1, option);
                packets = await fixture.DrainAsync();
                check("actual result commitment ACK precedes expected-run EndRun and the real Town restore",
                    consumed && packets.Count > 1 && Type(packets[0]) == (ushort)CmdPacketTypeA21.EPLP_COMMAND
                    && packets[0].AsSpan(HeaderLength).SequenceEqual(new byte[] { 1, 1, option })
                    && packets.Count(x => Type(x) == (ushort)NotiPacketTypeA21.SKILLINFO) == 1
                    && packets.Count(x => Type(x) == (ushort)NotiPacketTypeA21.ITEM_LIST) == 1
                    && fixture.Session.Player.CurrentRun == null && fixture.Session.GuardianAltarControls == null
                    && fixture.Session.Player.CurrentDungeonRunGeneration == run.RunGeneration);
                fixture.AdvanceMenuDay();
                await fixture.MenuAsync();
                packets = await fixture.DrainAsync();
                if (option == 1)
                {
                    check("the real ACKed option1 continuation answers Closed START_GAME once using full existing durable state",
                        packets.Count == 1 && Type(packets[0]) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME
                        && packets[0].AsSpan(HeaderLength).SequenceEqual(GuardianAltarMenuBodyBuilder.Build(before))
                        && fixture.Session.GuardianAltarControls is { } source && source.RunGeneration == run.RunGeneration);
                    check("the resumed Closed menu permits user BACK through real Town and restores current normal controls",
                        await fixture.ReturnFromMenuAsync()
                        && (await fixture.DrainAsync()).Select(Type).SequenceEqual(MenuControlsTypes())
                        && fixture.Session.GuardianAltarControls == null);
                    await fixture.MenuAsync();
                    packets = await fixture.DrainAsync();
                    check("after one successful continuation a new Closed menu request stays refused",
                        packets.Count == 1 && packets[0].AsSpan(HeaderLength).SequenceEqual(Convert.FromHexString("0101000000")));
                }
                else
                    check("an actual option2 return does not authorize a later Closed START_GAME",
                        packets.Count == 1 && packets[0].AsSpan(HeaderLength).SequenceEqual(Convert.FromHexString("0101000000"))
                        && fixture.Session.GuardianAltarControls == null);
                check("real Closed result selection never changes Guardian progression, daily receipts or reward inventory",
                    SameGuardianProgress(before, fixture.ReadExistingGuardianProgress())
                    && receipt.SequenceEqual(fixture.DailyReceiptSnapshot()) && fixture.ReadPersistedItemCount() == 7);
            }
        }

        private sealed partial class Fixture
        {
            internal GuardianAltarProgressSnapshot ReadExistingGuardianProgress()
                => GuardianProgress.ReadExistingMenu(Record.CharacterId, Record.AccountId);

            internal void CloseMenuWindow() => _opening.Close();
            internal void AdvanceMenuDay() => _menuNow = _menuNow.AddDays(1);

            internal string[] DailyReceiptSnapshot()
                => Database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 'anchor:' || day_id || ':' || week_id FROM character_daily_reset WHERE character_id=@cid "
                        + "UNION ALL SELECT counter_key || ':' || period || ':' || value FROM character_daily_counters "
                        + "WHERE character_id=@cid ORDER BY 1;";
                    command.Parameters.AddWithValue("@cid", Record.CharacterId);
                    using var reader = command.ExecuteReader();
                    var rows = new System.Collections.Generic.List<string>();
                    while (reader.Read()) rows.Add(reader.GetString(0));
                    return rows.ToArray();
                });

            internal void ForbidGuardianAndDailyWrites()
            {
                foreach (var table in new[] { "character_guardian_altar", "character_guardian_altar_stages",
                    "character_guardian_altar_purchases", "character_guardian_altar_slots", "character_guardian_altar_achievements",
                    "character_daily_reset", "character_daily_counters" })
                    foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                        Sql($"CREATE TRIGGER forbid_return_{table}_{operation} BEFORE {operation} ON {table} "
                            + "BEGIN SELECT RAISE(ABORT,'returned menu must be read-only'); END;");
            }

            internal async Task<DungeonRun> PrepareActualGuardianTerminalAsync()
            {
                if (!await StartGuardianStageAsync())
                    throw new InvalidOperationException("The actual PVF Guardian entry was not consumed.");
                await DrainAsync();
                var run = Session.Player.CurrentRun
                    ?? throw new InvalidOperationException("The actual PVF Guardian entry did not create a run.");
                var identity = run.CaptureParticipantRoomIdentity();
                if (!run.Instance.TryGetRoom(identity.Room.RoomInstanceId, out var room))
                    throw new InvalidOperationException("The actual Guardian room was not registered.");
                var release = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
                GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, release.Generation);
                _guardianEntry.ConfigureStageSelectionReturn((session, expectedRun, current) =>
                    _menuOwner.ReturnToStageSelectionAsync(session, expectedRun, current,
                        () => _townReturn.ReturnAsync(session, expectedRun)));
                var centers = run.Instance.Mechanisms.GuardianAltar.Centers;
                for (ushort sequence = 0; sequence < centers.Count; sequence++)
                    if (centers[sequence].IsFailureCenter)
                        await _guardianEntry.TryHandleCenterDeathAsync(Session, run, sequence).WaitAsync(Timeout);
                return run;
            }

            internal Task<bool> ActualGuardianEplpAsync(byte resultEvent, byte option)
                => _guardianEntry.TryHandleEplpCommandAsync(Session, new GamePacketHeader { cmd = 1,
                    type = (ushort)CmdPacketTypeA21.EPLP_COMMAND, length = 16 }, new[] { resultEvent, option }).WaitAsync(Timeout);
        }
    }
}
