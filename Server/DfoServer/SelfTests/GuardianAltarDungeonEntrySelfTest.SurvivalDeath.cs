using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        private static async Task CheckSurvivalDeathAsync(Action<string, bool> check,
            GuardianAltarMenuDefinition definition)
        {
            foreach (var scenario in new[] { (Rounds: 0, Center: false), (Rounds: 6, Center: false),
                (Rounds: 6, Center: true) })
            {
                using var fixture = new SurvivalDeathFixture(definition);
                await fixture.EnterAsync(801, scenario.Rounds);
                await fixture.FailAsync(scenario.Center);
                var fact = fixture.Combat.TerminalFact;
                var presentation = fixture.Combat.Presentation;
                var label = $"survival {(scenario.Center ? "center" : "character")} death after {scenario.Rounds} cleared rounds";
                check(label + " freezes Failed through the actual Coordinator and durable owner",
                    fact is { Result: GuardianAltarTerminalResult.Failed }
                    && fact.ClearedRounds == scenario.Rounds
                    && fact.Reason == (scenario.Center ? "all-failure-centers-defeated" : "guardian-character-death")
                    && fixture.Effects.Outbox.Get(fixture.EffectId).State == DungeonPersistentEffectState.Committed
                    && fixture.Run.Effects.GetState(fixture.EffectId) == DungeonEffectState.Committed
                    && fixture.RewardsMatch(scenario.Rounds));
                check(label + " projects inventory then packed survival CLEAR without rebuilding the battle menu",
                    presentation.InventoryProjectionSent && !presentation.RequiresMenuProjection
                    && !presentation.MenuProjectionSent && presentation.ClearProjectionSent
                    && !presentation.EplpProjectionSent && fixture.ClearMatches(scenario.Rounds)
                    && fixture.Packets.All(packet => Type(packet) != (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE)
                    && fixture.Packets.Select(Type).SequenceEqual((scenario.Center
                        ? new[] { (ushort)NotiPacketTypeA21.DIE_MONSTER }
                        : Array.Empty<ushort>()).Concat(scenario.Rounds == 0 ? Array.Empty<ushort>()
                            : new[] { (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST })
                        .Append((ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE)));
                check(label + " preserves ordinary stages, skills, slots, achievements and the owned coin wallet",
                    fixture.OwnedRowsUnchanged() && fixture.CoinCount == 7 && fixture.PersistedCoinCount == 7
                    && !fixture.Run.IsWaitingDeathRespawn
                    && !fixture.Run.Instance.IsParticipantDead(fixture.Run.CaptureIdentity())
                    && !fixture.Run.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.CombatDeathRespawn, out _));
                var packetCount = fixture.Packets.Count;
                var committed = fixture.Effects.Outbox.Get(fixture.EffectId);
                await fixture.FailAsync(scenario.Center);
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check(label + " repeated terminal input keeps the original fact and never repeats rewards or CLEAR",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact)
                    && fixture.Packets.Skip(packetCount).All(packet => scenario.Center
                        && Type(packet) == (ushort)NotiPacketTypeA21.DIE_MONSTER)
                    && fixture.Effects.Outbox.Get(fixture.EffectId).PayloadJson == committed.PayloadJson
                    && fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson == committed.ResultJson
                    && fixture.RewardsMatch(scenario.Rounds));
                packetCount = fixture.Packets.Count;
                fixture.Memory.Now = new DateTimeOffset(presentation.EplpDeadlineUtc.AddMilliseconds(-1));
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check(label + " cannot release result controls before CLEAR's four-second deadline",
                    fixture.Packets.Count == packetCount && !presentation.EplpProjectionSent);
                fixture.Memory.Now = new DateTimeOffset(presentation.EplpDeadlineUtc);
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check(label + " releases controls once at the deadline using the committed remaining tickets",
                    fixture.Packets.Count == packetCount + 1 && presentation.EplpProjectionSent
                    && fixture.EplpMatches() && fixture.RewardsMatch(scenario.Rounds)
                    && !presentation.MenuProjectionSent);
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check(label + " repeated continuation cannot resend CLEAR/EPLP or consume another ticket",
                    fixture.Packets.Count == packetCount + 1 && fixture.RewardsMatch(scenario.Rounds));
            }

            using (var fixture = new SurvivalDeathFixture(definition))
            {
                await fixture.EnterAsync(802, 6);
                fixture.Sql("CREATE TRIGGER guardian_survival_death_reject BEFORE UPDATE OF survival_best_phase "
                    + "ON character_guardian_altar BEGIN SELECT RAISE(ABORT,'guardian-survival-death-test'); END;");
                await fixture.FailAsync(false);
                var fact = fixture.Combat.TerminalFact;
                var frozen = fixture.Effects.Outbox.Get(fixture.EffectId).PayloadJson;
                check("real survival death transaction failure retains its terminal fact and rolls back rewards, best and tickets",
                    fact is { Result: GuardianAltarTerminalResult.Failed, ClearedRounds: 6 }
                    && fixture.Combat.Presentation.CommitResult == null && fixture.Packets.Count == 0
                    && fixture.Effects.Outbox.Get(fixture.EffectId).State == DungeonPersistentEffectState.Failed
                    && fixture.RewardsMatch(0) && fixture.OwnedRowsUnchanged());
                fixture.Sql("DROP TRIGGER guardian_survival_death_reject;");
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check("real survival death transaction retry commits the same frozen event before any result projection",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact)
                    && fixture.Effects.Outbox.Get(fixture.EffectId).PayloadJson == frozen
                    && fixture.Effects.Outbox.Get(fixture.EffectId).State == DungeonPersistentEffectState.Committed
                    && fixture.RewardsMatch(6) && fixture.ClearMatches(6)
                    && !fixture.Combat.Presentation.MenuProjectionSent);
            }

            foreach (var failedOpcode in new[] { NotiPacketTypeA21.UPDATE_ITEM_LIST,
                NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE, NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE })
            {
                using var fixture = new SurvivalDeathFixture(definition);
                await fixture.EnterAsync(803, 6);
                fixture.FailedOpcode = (ushort)failedOpcode;
                await fixture.FailAsync(false);
                var fact = fixture.Combat.TerminalFact;
                var presentation = fixture.Combat.Presentation;
                if (failedOpcode == NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE)
                {
                    fixture.Memory.Now = new DateTimeOffset(presentation.EplpDeadlineUtc);
                    await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                }
                check("survival " + failedOpcode + " write failure cannot roll back a committed score or bypass its checkpoint",
                    fixture.WriteFailures == 1 && fixture.RewardsMatch(6)
                    && fixture.Effects.Outbox.Get(fixture.EffectId).State == DungeonPersistentEffectState.Committed
                    && !presentation.MenuProjectionSent && !presentation.EplpProjectionSent
                    && (failedOpcode == NotiPacketTypeA21.UPDATE_ITEM_LIST
                        ? !presentation.InventoryProjectionSent && !presentation.ClearProjectionSent
                        : presentation.InventoryProjectionSent
                            && presentation.ClearProjectionSent == (failedOpcode == NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE)));
                var before = fixture.Packets.Count;
                fixture.FailedOpcode = null;
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                var expected = failedOpcode == NotiPacketTypeA21.UPDATE_ITEM_LIST
                    ? new[] { (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST, (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE }
                    : new[] { (ushort)failedOpcode };
                check("survival " + failedOpcode + " retry resumes only unfinished projections over the same durable event",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact) && fixture.RewardsMatch(6)
                    && fixture.Packets.Skip(before).Select(Type).SequenceEqual(expected)
                    && presentation.ClearProjectionSent && !presentation.MenuProjectionSent);
            }

            foreach (var change in new[] { "loading generation", "session directory" })
            {
                using var fixture = new SurvivalDeathFixture(definition);
                await fixture.EnterAsync(804, 0);
                if (change == "loading generation") fixture.ReplaceLoadingGeneration();
                else fixture.Memory.CreateSession(fixture.Session.Player.CharacterId);
                await fixture.FailAsync(false);
                check("obsolete survival " + change + " death cannot freeze or commit a replacement-scope result",
                    fixture.Combat.TerminalFact == null && fixture.Packets.Count == 0
                    && fixture.Scalar("SELECT COUNT(*) FROM dungeon_persistent_effect_outbox;") == 0
                    && fixture.RewardsMatch(0));
            }
            using (var fixture = new SurvivalDeathFixture(definition))
            {
                await fixture.EnterAsync(805, 6);
                await fixture.FailAsync(false);
                var before = fixture.Packets.Count;
                var fact = fixture.Combat.TerminalFact;
                fixture.ReplaceLoadingGeneration();
                fixture.Memory.Now = new DateTimeOffset(fixture.Combat.Presentation.EplpDeadlineUtc);
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check("old-loading survival continuation keeps its durable score but cannot deliver EPLP into a new loading scope",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact) && fixture.Packets.Count == before
                    && !fixture.Combat.Presentation.EplpProjectionSent && fixture.RewardsMatch(6));
            }
        }

        // Reuse the entry session/run fixture, but compose the real progress,
        // owned inventory and persistent-effect owners against an isolated
        // current-baseline database. The private clock is never started:
        // drive real Coordinator continuations with explicit UTC/tickets.
        private sealed class SurvivalDeathFixture : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "guardian-survival-death-"
                + Guid.NewGuid().ToString("N") + ".db");
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private readonly GuardianAltarMenuDefinition _definition;
            private readonly GameDatabase _database;
            private readonly GuardianAltarProgressService _progress;
            private readonly SqliteCharacterRepository _characters;
            private readonly DailyResetService _daily;
            private InventoryLease _lease;
            private DungeonCombatHandler _handler;
            private GuardianAltarProgressSnapshot _before;
            internal readonly Fixture Memory;
            internal readonly DungeonPersistentEffectApplicationService Effects;
            internal readonly GuardianAltarDungeonCoordinator Coordinator;
            internal EnhancedClientSession Session;
            internal ushort? FailedOpcode;
            internal int WriteFailures;
            internal List<byte[]> Packets => Memory.Packets;
            internal DungeonRun Run => Memory.LastRun;
            internal GuardianAltarCombatRuntime Combat => Run.Instance.Mechanisms.GuardianAltarCombat;
            private GuardianAltarDungeonStartPlan Plan => Run.Instance.Mechanisms.GuardianAltar;
            internal DungeonEffectId EffectId => new(Combat.TerminalFact.EventId,
                DungeonPersistentEffectKinds.GuardianAltarSurvivalCommit, DungeonEffectScope.Player, Run.RunId);
            internal int CoinCount => _lease.Inventory.GetMainVirtualCount(ReviveCoinService.WalletSlot)?.Count ?? 0;
            internal int PersistedCoinCount => _database.Read(connection =>
                InventoryMainVirtualCountRepository.LoadCurrencyCount(connection, null,
                    Session.Player.CharacterId, ReviveCoinService.WalletSlot));

            internal SurvivalDeathFixture(GuardianAltarMenuDefinition definition)
            {
                _definition = definition;
                Memory = new Fixture(definition);
                Memory.Open();
                _database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(11,'guardian-survival-death','');");
                _characters = new SqliteCharacterRepository(_database);
                _daily = new DailyResetService(_database);
                _progress = new GuardianAltarProgressService(_database, _daily, definition);
                Effects = new DungeonPersistentEffectApplicationService(_database.ConnectionString,
                    new DungeonPersistentEffectOutbox(_database.ConnectionString, Guid.NewGuid()), database: _database,
                    overflowRewardSink: new MailboxInventoryOverflowRewardSink(
                        new MailboxService(new MailboxRepository(_database))));
                Effects.BindGuardianAltarProgressService(_progress);
                Coordinator = new GuardianAltarDungeonCoordinator(Memory.Opening, _progress, () => definition,
                    Memory.Sessions, Memory.Transitions, Memory.Instances, _ => false, Memory.Mercenary, _ => false,
                    (run, room, projection) =>
                    {
                        Memory.LastRun = run; Memory.LastRoom = room; Memory.LoadingRegistrations++;
                        Memory.LoadingGeneration = room.RegisterLoadingProjection(run.CaptureIdentity(), projection,
                            new[] { run.CaptureIdentity() }, out _);
                    }, (session, identity) =>
                    {
                        Memory.EndMemoryRun(session, identity);
                        return Task.CompletedTask;
                    }, utcNow: () => Memory.Now, send: (session, prepare) => SendAsync(session, prepare, () => true),
                    isGuardianDungeon: definition.ContainsDungeon, monotonicMilliseconds: () => Memory.MonotonicMilliseconds,
                    sendPause: SendAsync, combatClock: new ClockService(), persistentEffects: Effects);
            }

            internal async Task EnterAsync(int characterId, int clearedRounds)
            {
                Session = await Memory.CreateConnectedSessionAsync(characterId);
                _characters.Create(new CharacterRecord { CharacterId = characterId, AccountId = 11,
                    Name = Encoding.ASCII.GetBytes("guardian-survival-death"), Level = 50,
                    TownId = Session.Player.CurTownId, AreaId = Session.Player.CurAreaId,
                    PosX = Session.Player.CurPosX, PosY = Session.Player.CurPosY });
                var initial = _progress.LoadMenu(characterId, Memory.Now.UtcDateTime);
                var catalog = SurvivalProgress(_definition, initial);
                var ordinary = catalog.Stages.Where(stage => stage.Progress >= 0).ToArray();
                foreach (var stage in ordinary)
                    Sql("INSERT INTO character_guardian_altar_stages(character_id,ridable_id,dungeon_id,progress) "
                        + $"VALUES({characterId},{_definition.RidableId},{stage.DungeonId},0) "
                        + "ON CONFLICT(character_id,ridable_id,dungeon_id) DO UPDATE SET progress=excluded.progress;");
                var stars = ordinary.Length * 3;
                Sql($"UPDATE character_guardian_altar SET stage_stars={stars},available_stars={stars},menu_hint_flag=0 "
                    + $"WHERE character_id={characterId};");
                _before = _progress.LoadMenu(characterId, Memory.Now.UtcDateTime);
                using (var connection = _database.OpenConnection())
                    _lease = InventoryContext.Register(Session.SessionId, characterId,
                        InventoryService.LoadFromDb(connection, characterId, 11, _database));
                lock (_lease.SyncRoot)
                    if (!_lease.Inventory.SetMainVirtualCount(ReviveCoinService.WalletSlot, 7))
                        throw new InvalidOperationException("Could not initialize survival fixture wallet.");
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(_lease, "guardian-survival-death-fixture"))
                    throw new InvalidOperationException("Could not commit survival fixture wallet.");
                var source = new SqliteSelectCharacterDataSource(_database, _characters, dailyResetService: _daily);
                var refresh = new InventoryRefreshSender(source, _characters, _database);
                refresh.BindSessions(Memory.Sessions);
                var services = new DungeonSharedServices(new ReviveCoinService(_daily), _characters, source, null,
                    _database.ConnectionString, refresh, sessions: Memory.Sessions, instanceRegistry: Memory.Instances,
                    database: _database, dailyResetService: _daily);
                var settlement = new DungeonSettlementHandler(services, null);
                _handler = new DungeonCombatHandler(services, settlement, new TournamentDungeonCoordinator(services, settlement),
                    new BloodAltarDungeonCoordinator(services, settlement));
                _handler.ConfigureGuardianAltar(Coordinator.TryCompleteAsync, Coordinator.TryHandleCenterDeathAsync,
                    Coordinator.TryHandleCharacterDeathAsync, Coordinator.TryRejectUseCoinAsync);
                var dungeonId = _definition.GetEligibleSurvivalDungeonIds(_before).Single();
                await Coordinator.TryHandleSelectDungeonAsync(Session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.SELECT_DUNGEON }, SurvivalRequest(dungeonId));
                if (Run == null || !Plan.IsSurvival)
                    throw new InvalidOperationException("Real persistent survival admission failed.");
                var release = Memory.LastRoom.MarkLoadingReady(Run.CaptureIdentity(), new[] { Run.CaptureIdentity() });
                GuardianAltarDungeonCoordinator.OnLoadingReleased(Run, Memory.LastRoom.Identity, release.Generation);
                await Coordinator.HandlePauseAsync(Session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE }, new byte[15]);
                await AdvanceAsync(clearedRounds);
                Packets.Clear();
            }

            private async Task AdvanceAsync(int rounds)
            {
                var identity = Run.CaptureParticipantRoomIdentity();
                var room = Memory.LastRoom;
                var fence = DungeonLoadingCoordinator.CaptureContinuationFence(Session, Memory.Sessions, Run, room.Identity);
                var steps = 0;
                while (Combat.ClearedRounds < rounds)
                {
                    if (++steps > 5000 || Combat.NextDueActiveMilliseconds is not { } due
                        || !Combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var ticket))
                        throw new InvalidOperationException("Survival Coordinator schedule stalled before the requested terminal score.");
                    Memory.MonotonicMilliseconds = due;
                    await Coordinator.OnCombatTimerAsync(Session, Run, identity, Memory.LoadingGeneration, ticket, fence)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var actor in Run.Instance.Mechanisms.DynamicActors.Capture(room.Identity,
                        GuardianAltarCombatRuntime.DynamicActorProvider, Memory.LoadingGeneration)
                        .Where(actor => DungeonKillApplicationService.CanAcceptGuardianActorDeath(Run, actor)))
                    {
                        var source = new DungeonEventEnvelope(Guid.NewGuid(), Run.CaptureIdentity(), room.Identity.RoomInstanceId,
                            Session.Player.CharacterId, Session.Player.CharacterId, actor.SequenceId, actor.ActorCode,
                            "guardian-survival-death-selftest", Memory.MonotonicMilliseconds);
                        var death = room.TryRecordActorDeath(source, actor.SequenceId, actor.ActorCode, actor.ActorType);
                        if (!death.Accepted || !death.Created
                            || !DungeonKillApplicationService.TryApplyGuardianActorDeath(Run, actor, death.Fact.Source))
                            throw new InvalidOperationException("Canonical survival enemy death was not accepted.");
                    }
                }
            }

            internal async Task FailAsync(bool center)
            {
                if (center)
                {
                    foreach (var sequence in Plan.Centers.Select((value, index) => (value, index))
                        .Where(row => row.value.IsFailureCenter).Select(row => checked((ushort)row.index)))
                        await Coordinator.TryHandleCenterDeathAsync(Session, Run, sequence).WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                    await _handler.HandleDieCharacter(Session, new GamePacketHeader {
                        cmd = 1, type = (ushort)CmdPacketTypeA21.DIE_CHARACTER }, Array.Empty<byte>())
                        .WaitAsync(TimeSpan.FromSeconds(5));
            }

            internal void ReplaceLoadingGeneration() => Memory.LastRoom.RegisterLoadingProjection(Run.CaptureIdentity(),
                DungeonMapHandler.CreateLoadingProjectionId(), new[] { Run.CaptureIdentity() }, out _);

            private Task SendAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare, Func<bool> current)
                => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(_sendLock, prepare, packet =>
                {
                    if (Type(packet) == FailedOpcode)
                    {
                        WriteFailures++;
                        throw new IOException("injected survival terminal " + FailedOpcode + " write failure");
                    }
                    Packets.Add(packet.ToArray());
                    return Task.CompletedTask;
                }, current);

            internal bool RewardsMatch(int rounds)
            {
                var after = _progress.ReadExistingMenu(Session.Player.CharacterId, 11);
                var rewards = Plan.SurvivalRewards.GetRewards(rounds);
                var allItems = Plan.SurvivalRewards.GetRewards(6).Select(item => item.ItemId).Distinct();
                using var connection = _database.OpenConnection();
                var stored = InventoryService.LoadFromDb(connection, Session.Player.CharacterId, 11, _database);
                return after.Header == (_before.Header with {
                    FreeTickets = _before.Header.FreeTickets - (rewards.Count > 0 ? 1 : 0),
                    SurvivalBestPhase = checked((short)rounds) })
                    && allItems.All(item => stored.CountMainItem(item) == rewards.Where(row => row.ItemId == item).Sum(row => row.Count))
                    && CoinCount == 7 && PersistedCoinCount == 7;
            }

            internal bool OwnedRowsUnchanged()
            {
                var after = _progress.ReadExistingMenu(Session.Player.CharacterId, 11);
                return after.Stages.SequenceEqual(_before.Stages) && after.Purchases.SequenceEqual(_before.Purchases)
                    && after.Slots.SequenceEqual(_before.Slots) && after.Achievements.SequenceEqual(_before.Achievements);
            }

            internal bool ClearMatches(int rounds)
            {
                var clear = Packets.Where(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE).ToArray();
                if (clear.Length != 1) return false;
                var body = clear[0].AsSpan(15);
                var rewards = Plan.SurvivalRewards.GetRewards(rounds);
                if (body.Length != 37 + rewards.Count * 12
                    || BinaryPrimitives.ReadInt32LittleEndian(body) != 1
                    || BinaryPrimitives.ReadInt32LittleEndian(body[4..]) != rounds
                    || BinaryPrimitives.ReadInt32LittleEndian(body[8..]) != rounds
                    || body[12] != (rounds > 0 ? 1 : 0)
                    || BinaryPrimitives.ReadInt32LittleEndian(body[13..]) != Plan.DungeonId
                    || BinaryPrimitives.ReadInt32LittleEndian(body[17..]) != Plan.Difficulty
                    || BinaryPrimitives.ReadInt32LittleEndian(body[21..]) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(body[25..]) != rewards.Count)
                    return false;
                var offset = 29;
                foreach (var reward in rewards)
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(body[offset..]) != 0
                        || BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 4)..]) != reward.ItemId
                        || BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 8)..]) != reward.Count)
                        return false;
                    offset += 12;
                }
                return BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]) == 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]) == 0;
            }

            internal bool EplpMatches()
            {
                var body = Packets[^1].AsSpan(15);
                var header = _progress.ReadExistingMenu(Session.Player.CharacterId, 11).Header;
                return Type(Packets[^1]) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE && body.Length == 5
                    && BinaryPrimitives.ReadInt32LittleEndian(body) == 3
                    && body[4] == ((long)header.FreeTickets + header.SecondaryTickets > 0 ? 1 : 0);
            }

            internal long Scalar(string sql) => _database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            });
            internal void Sql(string sql) => _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                return command.ExecuteNonQuery();
            });
            public void Dispose()
            {
                Memory.Dispose();
                if (_lease != null) InventoryContext.Unregister(_lease.SessionId, _lease.CharacterId);
                _sendLock.Dispose();
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
