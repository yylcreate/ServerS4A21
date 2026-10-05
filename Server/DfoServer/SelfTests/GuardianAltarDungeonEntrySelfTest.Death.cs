using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        private static async Task CheckCharacterDeathAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(501);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var identity = run.CaptureIdentity();
                var beforeHeader = run.Instance.Mechanisms.GuardianAltar.Progress.Header;
                using var boundary = new DeathBoundaryFixture(fixture, session);
                var packetsBefore = fixture.Packets.Count;
                var coinBefore = boundary.CoinCount;
                var coinBody = new byte[] { 0xE1, 0x2E };
                var coinHeader = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.USE_COIN };
                check("the existing coin refusal body preserves its independent three-byte contract",
                    DungeonCombatHandler.BuildUseCoinRejectionBody(12001).SequenceEqual(new byte[] { 0, 0xE1, 0x2E }));
                var revived = await boundary.Handler.HandleUseCoin(session, coinHeader, coinBody)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("Guardian USE_COIN before a death event rejects without proving death or consuming an owned coin",
                    !revived && boundary.CoinCount == coinBefore && boundary.PersistedCoinCount == coinBefore
                    && combat.TerminalFact == null && fixture.Packets.Count == packetsBefore + 1
                    && Type(fixture.Packets[^1]) == (ushort)CmdPacketTypeA21.USE_COIN
                    && fixture.Packets[^1].AsSpan(15).SequenceEqual(new byte[] { 0, 0xE1, 0x2E }));
                packetsBefore = fixture.Packets.Count;
                await boundary.Handler.HandleDieCharacter(session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.DIE_CHARACTER }, Array.Empty<byte>())
                    .WaitAsync(TimeSpan.FromSeconds(5));
                var terminal = combat.TerminalFact;
                var output = fixture.Packets.Skip(packetsBefore).ToArray();
                check("the real common DIE_CHARACTER handler routes Guardian directly to its dedicated Failed EPLP",
                    terminal is { Result: GuardianAltarTerminalResult.Failed, Reason: "guardian-character-death" }
                    && output.Length == 1 && Type(output[0]) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE
                    && output[0].AsSpan(15).SequenceEqual(GuardianAltarClearBodyBuilder.BuildEplpState(0, 1))
                    && combat.Presentation.EplpProjectionSent && combat.Presentation.CommitResult == null);
                check("Guardian character failure does not enter ordinary actor life, wipe, clear, cards or reward mutation",
                    !run.IsWaitingDeathRespawn && !run.Instance.IsParticipantDead(identity)
                    && !run.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.CombatDeathRespawn, out _)
                    && !combat.Presentation.InventoryProjectionSent
                    && !combat.Presentation.ClearProjectionSent
                    && run.Instance.Mechanisms.GuardianAltar.Progress.Header == beforeHeader
                    && boundary.CoinCount == coinBefore && boundary.PersistedCoinCount == coinBefore
                    && run.Effects.GetState(new DungeonEffectId(terminal.EventId,
                        DungeonPersistentEffectKinds.GuardianAltarClearCommit, DungeonEffectScope.Player, run.RunId))
                        == DungeonEffectState.Absent);
                packetsBefore = fixture.Packets.Count;
                await boundary.Handler.HandleDieCharacter(session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.DIE_CHARACTER }, Array.Empty<byte>())
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("repeated character death neither recreates failure nor repeats EPLP",
                    ReferenceEquals(combat.TerminalFact, terminal) && fixture.Packets.Count == packetsBefore);
                revived = await boundary.Handler.HandleUseCoin(session, coinHeader, coinBody).WaitAsync(TimeSpan.FromSeconds(5));
                check("late USE_COIN after terminal failure cannot revive or debit the wallet",
                    !revived && ReferenceEquals(combat.TerminalFact, terminal)
                    && boundary.CoinCount == coinBefore && boundary.PersistedCoinCount == coinBefore
                    && !run.Instance.IsParticipantDead(identity)
                    && fixture.Packets.Count == packetsBefore + 1
                    && Type(fixture.Packets[^1]) == (ushort)CmdPacketTypeA21.USE_COIN);
                packetsBefore = fixture.Packets.Count;
                fixture.EndMemoryRun(session, identity);
                revived = await boundary.Handler.HandleUseCoin(session, coinHeader, coinBody).WaitAsync(TimeSpan.FromSeconds(5));
                check("a late coin request after expected-run return cannot revive or debit the remaining owned wallet",
                    !revived && session.Player.CurrentRun == null && fixture.Packets.Count == packetsBefore
                    && boundary.CoinCount == coinBefore && boundary.PersistedCoinCount == coinBefore);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(505);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                using var boundary = new DeathBoundaryFixture(fixture, session, configureGuardian: false);
                var before = fixture.Packets.Count;
                var revived = await boundary.Handler.HandleUseCoin(session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.USE_COIN }, new byte[] { 0xE1, 0x2E })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await boundary.Handler.HandleDieCharacter(session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.DIE_CHARACTER }, Array.Empty<byte>())
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("an unconfigured Guardian callback fails closed instead of ordinary death or owned coin spending",
                    !revived && fixture.Packets.Count == before && boundary.CoinCount == 7 && boundary.PersistedCoinCount == 7
                    && run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact == null
                    && !run.IsWaitingDeathRespawn && !run.Instance.IsParticipantDead(run.CaptureIdentity())
                    && !run.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.CombatDeathRespawn, out _));
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(502);
                await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
                var run = fixture.LastRun;
                using var boundary = new DeathBoundaryFixture(fixture, session);
                var before = fixture.Packets.Count;
                await boundary.Handler.HandleDieCharacter(session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.DIE_CHARACTER }, Array.Empty<byte>())
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("a not-ready Guardian death is consumed without a fake failure, ordinary DIE_STATE or death timer",
                    run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact == null && fixture.Packets.Count == before
                    && !run.IsWaitingDeathRespawn && !run.Instance.IsParticipantDead(run.CaptureIdentity()));
            }
            foreach (var change in new[] { "loading generation", "account reference", "GameSession" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(503);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var before = fixture.Packets.Count;
                if (change == "loading generation")
                    fixture.LastRoom.RegisterLoadingProjection(run.CaptureIdentity(), DungeonMapHandler.CreateLoadingProjectionId(),
                        new[] { run.CaptureIdentity() }, out _);
                else
                    fixture.BeforePausePrepare = () =>
                    {
                        if (change == "account reference") session.Account = new AccountRecord { AccountId = 11 };
                        else session.GameSession = GameSessionIdentity();
                    };
                await fixture.Coordinator.TryHandleCharacterDeathAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                check("stale " + change + " cannot project Guardian failure for a replacement scope",
                    fixture.Packets.Count == before && !combat.Presentation.EplpProjectionSent
                    && (change != "loading generation" || combat.TerminalFact == null));
            }
            foreach (var change in new[] { "account reference", "GameSession", "session directory" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(506);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                using var boundary = new DeathBoundaryFixture(fixture, session);
                var before = fixture.Packets.Count;
                fixture.BeforePausePrepare = () =>
                {
                    if (change == "account reference") session.Account = new AccountRecord { AccountId = 11 };
                    else if (change == "GameSession") session.GameSession = GameSessionIdentity();
                    else fixture.CreateSession(session.Player.CharacterId);
                };
                var revived = await boundary.Handler.HandleUseCoin(session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.USE_COIN }, new byte[] { 0xE1, 0x2E })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("coin rejection send preparation suppresses an old " + change + " without wallet mutation",
                    !revived && fixture.Packets.Count == before && boundary.CoinCount == 7 && boundary.PersistedCoinCount == 7
                    && run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact == null);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(504);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                fixture.PauseSendError = new IOException("injected character failure EPLP write");
                var before = fixture.Packets.Count;
                await fixture.Coordinator.TryHandleCharacterDeathAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                var fact = run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact;
                check("a lost Failed EPLP write retains only the existing immutable death fact for retry",
                    fact?.Result == GuardianAltarTerminalResult.Failed && fixture.Packets.Count == before
                    && !run.Instance.Mechanisms.GuardianAltarCombat.Presentation.EplpProjectionSent);
                fixture.PauseSendError = null;
                await fixture.Coordinator.TryHandleCharacterDeathAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                check("retry after failed projection emits one EPLP without inventing a center death or reward",
                    ReferenceEquals(fact, run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact)
                    && fixture.Packets.Count == before + 1
                    && Type(fixture.Packets[^1]) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE
                    && run.Instance.Mechanisms.GuardianAltarCombat.Presentation.CommitResult == null);
            }
        }

        private sealed class DeathBoundaryFixture : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "guardian-death-boundary-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly InventoryLease _lease;
            private readonly GameDatabase _database;
            internal DungeonCombatHandler Handler { get; }
            internal int CoinCount => _lease.Inventory.GetMainVirtualCount(ReviveCoinService.WalletSlot)?.Count ?? 0;
            internal int PersistedCoinCount => _database.Read(connection =>
                InventoryMainVirtualCountRepository.LoadCurrencyCount(connection, null,
                    _lease.CharacterId, ReviveCoinService.WalletSlot));

            internal DeathBoundaryFixture(Fixture fixture, EnhancedClientSession session, bool configureGuardian = true)
            {
                _database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                _database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(11,'guardian-death-boundary','');";
                    command.ExecuteNonQuery();
                });
                var records = new SqliteCharacterRepository(_database);
                records.Create(new CharacterRecord { CharacterId = session.Player.CharacterId, AccountId = 11,
                    Name = Encoding.ASCII.GetBytes("guardian-death-boundary"), Level = 50,
                    TownId = session.Player.CurTownId, AreaId = session.Player.CurAreaId,
                    PosX = session.Player.CurPosX, PosY = session.Player.CurPosY });
                var daily = new DailyResetService(_database);
                var source = new SqliteSelectCharacterDataSource(_database, records, dailyResetService: daily);
                var refresh = new InventoryRefreshSender(source, records, _database);
                refresh.BindSessions(fixture.Sessions);
                var services = new DungeonSharedServices(new ReviveCoinService(daily), records, source, null,
                    _database.ConnectionString, refresh, sessions: fixture.Sessions, instanceRegistry: fixture.Instances,
                    database: _database, dailyResetService: daily);
                var settlement = new DungeonSettlementHandler(services, null);
                Handler = new DungeonCombatHandler(services, settlement, new TournamentDungeonCoordinator(services, settlement),
                    new BloodAltarDungeonCoordinator(services, settlement));
                if (configureGuardian)
                    Handler.ConfigureGuardianAltar(fixture.Coordinator.TryCompleteAsync, fixture.Coordinator.TryHandleCenterDeathAsync,
                        fixture.Coordinator.TryHandleCharacterDeathAsync, fixture.Coordinator.TryRejectUseCoinAsync);
                using var connection = _database.OpenConnection();
                _lease = InventoryContext.Register(session.SessionId, session.Player.CharacterId,
                    InventoryService.LoadFromDb(connection, session.Player.CharacterId, 11, _database));
                lock (_lease.SyncRoot)
                    if (!_lease.Inventory.SetMainVirtualCount(ReviveCoinService.WalletSlot, 7))
                        throw new InvalidOperationException("Could not initialize the owned fixture coin wallet.");
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(_lease, "guardian-character-death-fixture"))
                    throw new InvalidOperationException("Could not persist the owned fixture coin wallet.");
            }

            public void Dispose()
            {
                InventoryContext.Unregister(_lease.SessionId, _lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
