using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.EpicRoad;
using DfoServer.Game.Friends;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Parsers.Town;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class TownHandler
    {
        private const string PartyTeleportMemberLockedMessage =
            "存在无法前往该地区的队员。";
        private const string PartyTeleportRemoteMemberMessage =
            "存在与队长不在同一房间的队员。";

        private static readonly TimeSpan PositionPersistThrottle = TimeSpan.FromSeconds(5);

        private readonly struct TownProjectionGuard
        {
            private TownProjectionGuard(
                DungeonRunIdentity endedRun,
                DungeonSelectionContext selection,
                Func<bool> authorizedReturnCurrent = null,
                Func<bool> clientTransitionCurrent = null)
            {
                EndedRun = endedRun;
                Selection = selection;
                AuthorizedReturnCurrent = authorizedReturnCurrent;
                ClientTransitionCurrent = clientTransitionCurrent;
            }

            internal DungeonRunIdentity EndedRun { get; }
            internal DungeonSelectionContext Selection { get; }
            internal Func<bool> AuthorizedReturnCurrent { get; }
            internal Func<bool> ClientTransitionCurrent { get; }

            internal static TownProjectionGuard ForClientTransition(Func<bool> current) =>
                new TownProjectionGuard(default(DungeonRunIdentity), null, clientTransitionCurrent: current);

            internal static TownProjectionGuard ForAuthorizedReturn(Func<bool> current) =>
                new TownProjectionGuard(default(DungeonRunIdentity), null, current);

            internal static TownProjectionGuard ForEndedRun(
                DungeonRunIdentity identity) =>
                new TownProjectionGuard(identity, null);

            internal static TownProjectionGuard ForSelection(
                DungeonSelectionContext selection) =>
                new TownProjectionGuard(default(DungeonRunIdentity), selection);
        }

        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly Game.SelectCharacter.SqliteSelectCharacterDataSource _selectDataSource;
        private readonly SqlitePvpSkillRepository _pvpSkillRepository;
        private readonly Game.Party.PartyManager _partyManager;   // 可空: 副本退出/回城时把队员一起拉回城(跟随退出)
        // 可空: 会话目录(charId→session)。同屏区域查询与队员定位共用这一份注册表, 不另设区域广播器。
        private readonly Game.Session.ISessionDirectory _sessions;
        private readonly DungeonInstanceRegistry _dungeonInstances;
        private readonly Game.Raid.RaidManager _raidManager;
        private readonly IGameDatabase _database;
        private Func<EnhancedClientSession, ushort, Guid, int, Task>
            _dungeonGiveupPartyDeparture;
        private Func<Task> _publishTownPartyLists;
        private Func<EnhancedClientSession, byte, byte, Task> _raidTownAreaChanged;
        private Func<IEnumerable<int>, Task> _publishGuildState;
        private Func<EnhancedClientSession, Func<bool>, Task<bool>> _restoreGuardianControls;
        private Func<EnhancedClientSession, Task> _accountDungeonCountProjection;
        private EpicRoadEntryService _epicRoadEntry;
        private GuildRepository _guilds;
        private CharacterTransitionCoordinator _guildTransitions;

        internal void ConfigureGuildEntrance(GuildRepository guilds, CharacterTransitionCoordinator transitions)
        {
            _guilds = guilds ?? throw new ArgumentNullException(nameof(guilds));
            _guildTransitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        }

        internal void ConfigureEpicRoadEntry(EpicRoadEntryService entry)
            => _epicRoadEntry = entry ?? throw new ArgumentNullException(nameof(entry));

        internal void ConfigureAccountDungeonCountProjection(Func<EnhancedClientSession, Task> projection)
            => _accountDungeonCountProjection = projection ?? throw new ArgumentNullException(nameof(projection));

        internal Task ProjectAccountDungeonCountsAsync(EnhancedClientSession session)
            => _accountDungeonCountProjection?.Invoke(session) ?? Task.CompletedTask;

        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource = null,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            InventoryRefreshSender refresh = null)
            : this(
                characterRepository,
                selectDataSource,
                partyManager,
                sessions,
                refresh,
                dungeonInstances: null,
                raidManager: null,
                database: null)
        {
        }

        internal TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource,
            Game.Party.PartyManager partyManager,
            Game.Session.ISessionDirectory sessions,
            InventoryRefreshSender refresh,
            DungeonInstanceRegistry dungeonInstances,
            Game.Raid.RaidManager raidManager,
            IGameDatabase database = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database ?? GameDatabase.CreateDefault();
            _honorLevel = new HonorLevelSyncService(
                _characterRepository,
                _database);
            _growthCapsule = new GrowthCapsuleSyncService(
                _characterRepository,
                _database);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(_database);
            _refresh = refresh;
            _selectDataSource = selectDataSource;  // 可空: PVP 房间仍需构建完整 USERINFO subtype1。
            _pvpSkillRepository = new SqlitePvpSkillRepository(_database);
            _partyManager = partyManager;          // 可空: 组队副本收尾 fan-out(跟随退出); 与副本共享同一 PartyManager
            _sessions = sessions;                  // 可空: 未注入时退化为单人(不广播)
            _dungeonInstances = dungeonInstances;
            _raidManager = raidManager;
        }

        internal void ConfigureDungeonGiveupPartyDeparture(
            Func<EnhancedClientSession, ushort, Guid, int, Task> handler)
        {
            _dungeonGiveupPartyDeparture = handler;
        }

        internal void ConfigureTownPartyListPublisher(Func<Task> publisher)
        {
            _publishTownPartyLists = publisher;
        }

        internal void ConfigureGuildStatePublisher(Func<IEnumerable<int>, Task> publisher)
            => _publishGuildState = publisher;

        internal void ConfigureRaidTownAreaChanged(
            Func<EnhancedClientSession, byte, byte, Task> handler)
        {
            _raidTownAreaChanged = handler;
        }

        internal void ConfigureGuardianControlsRestore(
            Func<EnhancedClientSession, Func<bool>, Task<bool>> restore)
            => _restoreGuardianControls = restore ?? throw new ArgumentNullException(nameof(restore));

        // 构建某在线会话玩家的完整 USERINFO subtype1（属性/装备/当前频道技能）。
        // 同屏时仅推 subtype0(精简外观)客户端能渲染但判定"对方不在城镇/不可邀请"; self 进游戏收的是 subtype0+subtype1
        // 两份, 故给同屏他人补 subtype1。A21 单条记录的 UID 位于 body +18。
        internal byte[] BuildFullUserInfoPacket(EnhancedClientSession s)
        {
            if (_selectDataSource == null || s?.Player == null || s.Player.CharacterId <= 0)
                return null;
            try
            {
                var snap = _selectDataSource.Load(s.Player.CharacterId, s.Account?.AccountId ?? 1);
                if (snap?.CharacterRecord == null || snap.InitializationSnapshot?.UserInfoAddition == null)
                    return null;
                if (GameNetworkConfig.IsPvpListener(s.ListenerPort))
                {
                    snap.InitializationSnapshot.SkillInfo = SkillStateService.LoadPvpAndSync(
                        _pvpSkillRepository, snap.CharacterRecord, s.Player.Level).Skills;
                }
                if (s.Player.Subtype0Tail != null)
                    snap.InitializationSnapshot.UserInfoAddition.SkillTreeIndex = s.Player.Subtype0Tail.SkillTreeIndex;
                if (!new Network.Builders.UserInfoBodyBuilder().TryBuild(snap, 1, out var fullBody) || fullBody == null || fullBody.Length < 20)
                    return null;
                BitConverter.GetBytes(s.Player.UserId).CopyTo(fullBody, 18);
                return GamePacketEnvelopeBuilder.Build(0x00, 0x0002, fullBody);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] BuildFullUserInfoPacket cid={s.Player.CharacterId} 失败: {ex.Message}");
                return null;
            }
        }

        public void PersistPosition(EnhancedClientSession session, bool forceImmediate, string source)
        {
            try
            {
                var player = session?.Player;
                if (player == null || player.CharacterId <= 0)
                    return;
                var characterId = player.CharacterId;
                var townId = player.CurTownId;
                var areaId = player.CurAreaId;
                var x = player.CurPosX;
                var y = player.CurPosY;
                var direction = player.CurDirection;
                var areaState = player.CurAreaState;
                if (!GameChannelSpawnPolicy.ShouldPersistPosition(
                        session.ListenerPort,
                        townId,
                        areaId))
                    return;

                var now = DateTime.UtcNow;
                if (!forceImmediate)
                {
                    if (now - player.LastPositionPersistAt < PositionPersistThrottle)
                        return;
                }

                var gate = GameWorld.Town.GetCeraRoomInfo(townId);
                if (gate.Town <= 0 || !ReferenceEquals(session.Player, player)
                    || player.CharacterId != characterId)
                    return;

                _characterRepository.UpdatePosition(
                    characterId,
                    townId,
                    areaId,
                    x,
                    y,
                    direction,
                    areaState);
                if (ReferenceEquals(session.Player, player) && player.CharacterId == characterId)
                    player.LastPositionPersistAt = now;
                FileLogger.Log($"[{ProtocolName}] Persisted position ({source}) character_id={characterId} town={townId} area={areaId} pos=({x},{y})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Persist position ({source}) failed: {ex.Message}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_POSITION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 7) return;
            var controlsSource = session.GuardianAltarControls;
            var movementSequence = session.Player.NextTownMovementSequence();
            var gotoPosX = BitConverter.ToInt16(body, 0);
            var gotoPosY = BitConverter.ToInt16(body, 2);
            var direction = body[4];
            var motionState = BitConverter.ToUInt16(body, 5);
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = direction;
            PersistPosition(session, forceImmediate: false, source: "set_user_position");

            var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
            var positionPacket = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0016,
                TownAreaNotificationBuilder.BuildUserPosition(snap, motionState));

            // A21 回包会回到发起 SET_USER_POSITION 的客户端本身；单机时
            // _sessions 没有其它目标，不能只做“给别人广播”。
            await session.SendPacketAsync(positionPacket);

            // A21 fresh-login flow reports SET_USER_POSITION without first
            // reporting SET_USER_AREA. Treat the first position of each
            // hydrated character as the one-time town-arrival projection so
            // both clients receive appearance + area + a self-first roster.
            // Later movement remains the lightweight USER_POSITION broadcast.
            if (ShouldProjectInitialAreaRoster(
                    session.Player,
                    movementSequence))
            {
                await BroadcastAreaRosterAsync(session, snap);
            }
            else if (_sessions != null && session.Player.CharacterId > 0)
            {
                await _sessions.BroadcastToAreaAsync(
                    session.Player.CurTownId, session.Player.CurAreaId, session.Player.CharacterId,
                    positionPacket,
                    session.ListenerPort);
            }
            if (controlsSource != null && header.cmd == 1
                && header.type == (ushort)CmdPacketTypeA21.SET_USER_POSITION)
                await TryRestoreGuardianControlsAsync(session, default(TownProjectionGuard),
                    controlsSource);
        }

        public async Task Handle_ENUM_CMDPACKET_GET_PCROOM_TIME_POINT_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            // A21 capture: request body is 15B and the server returns the same
            // CMD opcode with a fixed 6B zero body during town-return recovery.
            if (body == null || body.Length < 15)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GET_PCROOM_TIME_POINT_ITEM rejected " +
                    $"bodyLength={body?.Length ?? 0} (expected >=15B)");
                return;
            }

            await session.SendPacketAsync(
                BuildGetPcRoomTimePointItemResponsePacket());
        }

        internal static byte[] BuildGetPcRoomTimePointItemResponsePacket() =>
            GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM,
                new byte[6]);

        public Task Handle_ENUM_CMDPACKET_SET_USER_AREA(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            // Guild mutations take these same gates. Entry and exit cannot
            // race a queued removal, reconnect or character selection.
            if (_guildTransitions != null && body?.Length >= 6
                && (body[0] == GuildAgitAccessPolicy.TownId
                    || session.Player.CurTownId == GuildAgitAccessPolicy.TownId))
            {
                var player = session.Player;
                var account = session.Account;
                var gameSession = session.GameSession;
                int cid = player.CharacterId;
                ushort uid = player.UserId;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                bool Current() => ReferenceEquals(session.Player, player)
                    && ReferenceEquals(session.Account, account) && ReferenceEquals(session.GameSession, gameSession)
                    && player.CharacterId == cid && player.UserId == uid
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && _guildTransitions.IsCurrent(session)
                    && InventoryContext.TryGetOwnedLease(session.SessionId, cid, out _);
                return _guildTransitions.RunIfCurrentAsync(session, () => Current()
                    ? SetUserAreaCoreAsync(session, body, TownProjectionGuard.ForClientTransition(Current))
                    : Task.CompletedTask);
            }
            return SetUserAreaCoreAsync(session, body, default(TownProjectionGuard));
        }

        private async Task SetUserAreaCoreAsync(
            EnhancedClientSession session,
            byte[] body,
            TownProjectionGuard projectionGuard,
            EnhancedClientSession.GuardianAltarControlProjectionSource expectedControlsSource = null,
            Action controlsRestored = null,
            TownUserSnapshot committedTeleportSource = null)
        {
            if (body == null || body.Length < 6) return;
            var controlsSource = expectedControlsSource ?? session?.GuardianAltarControls;
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var gotoTownId = body[0];
            var gotoAreaId = body[1];
            var gotoPosX = BitConverter.ToInt16(body, 2);
            var gotoPosY = BitConverter.ToInt16(body, 4);

            if (gotoTownId == GuildAgitAccessPolicy.TownId)
            {
                if (!CanRestoreGuildAgitArea(session, gotoAreaId, projectionGuard)
                    && !TryValidateGuildAgitEntry(session, gotoAreaId, out var guildReason))
                {
                    FileLogger.Log($"[GuildAgit] entry rejected cid={session.Player.CharacterId}: {guildReason}");
                    await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                    return;
                }
                var player = session.Player;
                var account = session.Account;
                var gameSession = session.GameSession;
                int cid = player.CharacterId;
                ushort uid = player.UserId;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                var sourceGuard = projectionGuard;
                projectionGuard = TownProjectionGuard.ForAuthorizedReturn(() =>
                    CanContinueTownProjection(session, sourceGuard)
                    && ReferenceEquals(session.Player, player) && ReferenceEquals(session.Account, account)
                    && ReferenceEquals(session.GameSession, gameSession)
                    && player.CharacterId == cid && player.UserId == uid
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && _guildTransitions.IsCurrent(session)
                    && InventoryContext.TryGetOwnedLease(session.SessionId, cid, out _));
            }

            // The resource NPC's A21 menu sends SET_USER_AREA. Validate only
            // the external transition into this event town; existing run/selection
            // returns retain their authorized source and remain available after closure.
            if (gotoTownId == EpicRoadEntryService.TargetTownId
                && session.Player.CurTownId != EpicRoadEntryService.TargetTownId
                && !projectionGuard.EndedRun.IsValid && projectionGuard.Selection == null
                && projectionGuard.AuthorizedReturnCurrent == null)
            {
                if (!TryValidateEpicRoadTownEntry(session, gotoAreaId, out var epicReason))
                {
                    FileLogger.Log($"[EpicRoad] town entry rejected cid={session.Player.CharacterId} reason={epicReason}");
                    await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                    return;
                }
                var sourcePlayer = session.Player;
                var sourceAccount = session.Account;
                var sourceGameSession = session.GameSession;
                var sourceCharacterId = sourcePlayer.CharacterId;
                var sourceUserId = sourcePlayer.UserId;
                var sourceAccountId = sourceAccount.AccountId;
                var sourceRunGeneration = sourcePlayer.CurrentDungeonRunGeneration;
                var sourceSelectionGeneration = sourcePlayer.CurrentDungeonSelectionGeneration;
                projectionGuard = TownProjectionGuard.ForAuthorizedReturn(() =>
                    ReferenceEquals(session.Player, sourcePlayer) && ReferenceEquals(session.Account, sourceAccount)
                    && ReferenceEquals(session.GameSession, sourceGameSession)
                    && sourcePlayer.CharacterId == sourceCharacterId && sourcePlayer.UserId == sourceUserId
                    && sourceAccount.AccountId == sourceAccountId
                    && sourcePlayer.CurrentDungeonRunGeneration == sourceRunGeneration
                    && sourcePlayer.CurrentDungeonSelectionGeneration == sourceSelectionGeneration
                    && (_sessions == null || (_sessions.TryGet(sourceCharacterId, out var registered)
                        && ReferenceEquals(registered, session))));
            }

            if (!CanChangeRaidArea(session, gotoTownId, gotoAreaId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_USER_AREA rejected for non-raid member: " +
                    $"cid={session.Player.CharacterId} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"target={gotoTownId}:{gotoAreaId}");
                await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                return;
            }

            if (!GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    gotoTownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_USER_AREA rejected by channel policy: " +
                    $"cid={session.Player.CharacterId} listener={session.ListenerPort} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"target={gotoTownId}:{gotoAreaId}");
                await ChannelTownRestrictionSender.SendAsync(session, gotoTownId);
                return;
            }

            var previousTownId = committedTeleportSource?.TownId ?? session.Player.CurTownId;
            var previousAreaId = committedTeleportSource?.AreaId ?? session.Player.CurAreaId;

            session.Player.CurTownId = gotoTownId;
            session.Player.CurAreaId = gotoAreaId;
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = 0x05;
            // A21 USER_AREA/AREA_USERS samples use state=0 for town arrival.
            // The client-provided body has additional fields, but its legacy
            // state byte is not authoritative for the server projection.
            session.Player.CurAreaState = 0x00;

            var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            // A21 USER_AREA(0x0017) 远程分支：包内 town/area 与接收者当前区域不同时
            // 只移除该 UID 场景投影，不碰队伍槽。不能用 0x0006：会清队伍槽。
            // 不能用 0x0018：已在场客户端会 setDrawLoadingMode 并切到 TOWN 模块。
            if (_sessions != null
                && session.Player.CharacterId > 0
                && (previousTownId != gotoTownId
                    || previousAreaId != gotoAreaId))
            {
                await _sessions.BroadcastToAreaAsync(
                    previousTownId,
                    previousAreaId,
                    session.Player.CharacterId,
                    BuildAreaTransitionDeparturePacket(selfSnapshot),
                    session.ListenerPort);
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;

                FileLogger.Log(
                    $"[{ProtocolName}] AREA departure projection: " +
                    $"uid={session.Player.UserId} " +
                    $"from={previousTownId}:{previousAreaId} " +
                    $"to={gotoTownId}:{gotoAreaId} " +
                    $"listener={session.ListenerPort}");
            }

            await SendTownProjectionPacketAsync(session, projectionGuard,
                GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USER_AREA,
                    TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            // 联机同屏: 名册含同区域其它玩家, 并让已在场玩家看到新来的自己。
            await BroadcastAreaRosterAsync(
                session,
                selfSnapshot,
                projectionGuard,
                async () =>
                {
                    if (CanContinueTownProjection(session, projectionGuard)
                        && ShouldNotifyRaidTownLoaded(session.ListenerPort, session.Player))
                    {
                        await session.SendPacketAsync(BuildRaidUserFinishLoadPacket());
                        if (CanContinueTownProjection(session, projectionGuard)
                            && _raidTownAreaChanged != null)
                        {
                            await _raidTownAreaChanged(session, previousTownId, previousAreaId);
                        }
                    }
                });
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            if (committedTeleportSource == null)
                PersistPosition(session, forceImmediate: true, source: "set_user_area");
            if (await TryRestoreGuardianControlsAsync(session, projectionGuard, controlsSource))
                controlsRestored?.Invoke();
            if (gotoTownId == EpicRoadEntryService.TargetTownId && CanContinueTownProjection(session, projectionGuard))
                await ProjectAccountDungeonCountsAsync(session);
        }

        private bool IsCurrentGuardianControlsSource(EnhancedClientSession session,
            TownProjectionGuard guard, EnhancedClientSession.GuardianAltarControlProjectionSource source)
            => source != null && ReferenceEquals(session.GuardianAltarControls, source)
                && IsCurrentGuardianControlsScope(session, guard, source);

        private bool IsCurrentGuardianControlsScope(EnhancedClientSession session,
            TownProjectionGuard guard, EnhancedClientSession.GuardianAltarControlProjectionSource source)
            => source != null && CanContinueTownProjection(session, guard)
                && ReferenceEquals(session.GameSession, source.Generation)
                && ReferenceEquals(session.Account, source.Account)
                && session.Player.CharacterId == source.CharacterId
                && session.Account?.AccountId == source.AccountId
                && session.Player.UserId == source.UserId
                && SessionDirectory.IsTownPresence(session.Player)
                && session.Player.CurrentDungeonSelection == null && !session.Player.DungeonSelectionPending
                && session.Player.CurrentDungeonRunGeneration == source.RunGeneration
                && session.Player.CurrentDungeonSelectionGeneration == source.SelectionGeneration
                && (_sessions == null || (_sessions.TryGet(source.CharacterId, out var current)
                    && ReferenceEquals(current, session)));

        private async Task<EnhancedClientSession.GuardianAltarControlProjectionSource> TryReleaseGuardianActivityAsync(
            EnhancedClientSession session, TownProjectionGuard guard,
            EnhancedClientSession.GuardianAltarControlProjectionSource source)
        {
            bool Current() => IsCurrentGuardianControlsSource(session, guard, source);
            if (!Current()) return null;
            if (!source.ActivityReleasePending) return source;
            var prepared = false;
            // A21 0x1147D8B: type3 skips the result UI; 0x1147E46..6F
            // stops the activity clocks and clears manager+497 synchronously.
            // This is a control release, not a successful terminal fact.
            var packet = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE,
                GuardianAltarClearBodyBuilder.BuildEplpState(3, 0));
            await session.SendPreparedPacketBatchAsync(() =>
            {
                if (!Current()) return Array.Empty<byte[]>();
                prepared = true;
                return new[] { packet };
            }, Current);
            if (!prepared || !Current()) return null;
            var released = source with { ActivityReleasePending = false };
            if (!session.TryReplaceGuardianControlsSource(source, released)) return null;
            FileLogger.Log($"[GuardianAltarEntry] activity controls released cid={source.CharacterId} "
                + $"runGeneration={source.RunGeneration}");
            return released;
        }

        private async Task<bool> TryRestoreGuardianControlsAsync(
            EnhancedClientSession session, TownProjectionGuard guard,
            EnhancedClientSession.GuardianAltarControlProjectionSource source)
        {
            source = await TryReleaseGuardianActivityAsync(session, guard, source);
            bool Current() => IsCurrentGuardianControlsSource(session, guard, source);
            if (!Current())
                return false;
            var restore = _restoreGuardianControls;
            if (restore == null || !await restore(session, Current))
            {
                if (Current())
                    throw new InvalidOperationException("Guardian normal controls projection is incomplete.");
                return false;
            }
            if (Current() && session.TryCompleteGuardianControlsRestore(source))
            {
                FileLogger.Log($"[GuardianAltarEntry] normal controls restored cid={source.CharacterId} "
                    + $"runGeneration={source.RunGeneration} selectionGeneration={source.SelectionGeneration}");
                return true;
            }
            return false;
        }

        // 进本离开城镇的 USER_AREA 远程移除见 TownAreaRosterDepartureNotifier。
        // 城镇切图已在上面向旧区域发过离开者 USER_AREA，不再重复。

        // 同屏插入他人用 USER_AREA(0x0017)。A21 客户端 AREA_USERS(0x0018)
        // 会 setDrawLoadingMode 并把模块切到 TOWN，只发给正在进该区域的本人。
        // 已在场玩家只收到达者 USERINFO0 + USER_AREA；把 0x18 发给他人会关掉界面、丢掉特效。
        private bool CanChangeRaidArea(
            EnhancedClientSession session,
            byte targetTownId,
            byte targetAreaId)
        {
            if (!GameNetworkConfig.IsRaidListener(session.ListenerPort)
                || session.Player.CurTownId != GameChannelSpawnPolicy.RaidTownId
                || targetTownId != GameChannelSpawnPolicy.RaidTownId
                || targetAreaId == session.Player.CurAreaId)
            {
                return true;
            }

            if (session.Player.CurAreaId == 1 && targetAreaId == 2)
            {
                return _raidManager != null
                    && _raidManager.TryGetByUser(session.Player.UserId, out _);
            }

            return true;
        }

        private static byte[] BuildCoPresenceInsert(TownUserSnapshot snap) =>
            GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snap));

        internal static byte[] BuildAreaTransitionDeparturePacket(
            TownUserSnapshot snapshot)
            => BuildCoPresenceInsert(snapshot);

        /// 城镇同屏核心。
        /// 到达者本人：AREA_USERS(0x0018) 进场景，再自己 USERINFO0，
        /// 然后对每个已在场他人发 USERINFO0 + USER_AREA（与插入到达者相同）。
        /// 已在场他人：只发到达者 USERINFO0 + USER_AREA，不得发 0x18。
        /// _sessions 为空(单人/未注入)时退化为只发自己 —— 与既有单机行为等价。
        /// </summary>
        internal async Task<bool> AnnouncePvpTownArrivalWithinTransitionAsync(
            EnhancedClientSession session)
        {
            bool IsCurrentArrival() =>
                session?.Player != null
                && GameNetworkConfig.IsPvpListener(session.ListenerPort)
                && IsTownArrivalStateEligible(session.Player)
                && _sessions != null
                && _sessions.TryGet(session.Player.CharacterId, out var current)
                && ReferenceEquals(current, session);

            if (!IsCurrentArrival())
                return false;
            await BroadcastAreaRosterAsync(
                session, TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player));
            return IsCurrentArrival();
        }

        private async Task BroadcastAreaRosterAsync(
            EnhancedClientSession session,
            TownUserSnapshot selfSnapshot,
            TownProjectionGuard projectionGuard = default(TownProjectionGuard),
            Func<Task> onRecipientReady = null)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var townId = session.Player.CurTownId;
            var areaId = session.Player.CurAreaId;

            IReadOnlyList<EnhancedClientSession> others = _sessions?.GetSessionsInArea(
                    townId,
                    areaId,
                    session.Player.CharacterId,
                    session.ListenerPort)
                ?? System.Array.Empty<EnhancedClientSession>();

            FileLogger.Log(
                $"[{ProtocolName}] AREA co-presence: uid={session.Player.UserId} " +
                $"town={townId} area={areaId} listener={session.ListenerPort} " +
                $"others={others.Count}");

            // 全体名册(自己 + 其它人)。
            var roster = new List<TownUserSnapshot>(others.Count + 1) { selfSnapshot };
            foreach (var o in others)
                roster.Add(TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player));

            // 到达者本人：0x18 进场景后先消费自己的 USERINFO0(+47)，
            // 再按远程插入顺序发他人 USERINFO0 + USER_AREA。
            // 14932：0x17(无 USERINFO) -> 0x18 -> 他人 USERINFO 看不到先到的人；
            // 已在场玩家能看到后来者，是因为收的是 USERINFO0 + 0x17。
            await SendTownProjectionPacketAsync(session, projectionGuard,
                GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.AREA_USERS,
                TownAreaNotificationBuilder.BuildAreaUsers(townId, areaId, roster)));
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            var selfAppearance = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USERINFO,
                AppearanceService.BuildNoti2Body(session.Player, _database));
            await SendTownProjectionPacketAsync(session, projectionGuard, selfAppearance);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            foreach (var o in others)
            {
                await SendTownProjectionPacketAsync(session, projectionGuard,
                    GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USERINFO,
                    AppearanceService.BuildNoti2Body(
                        o.Player,
                        _database)));
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
                await SendTownProjectionPacketAsync(session, projectionGuard,
                    BuildCoPresenceInsert(
                        TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player)));
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
            }

            if (onRecipientReady != null
                && CanContinueTownProjection(session, projectionGuard))
            {
                await onRecipientReady();
            }

            var selfArea = BuildCoPresenceInsert(selfSnapshot);
            var peerProjections = new List<Task>(others.Count);
            foreach (var o in others)
            {
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;

                var recipient = o;
                peerProjections.Add(SessionDirectory.TrySendBestEffortAsync(
                    async cancellationToken =>
                    {
                        if (!CanContinueTownProjection(
                                session,
                                projectionGuard))
                        {
                            return;
                        }
                        await recipient.SendPacketAsync(
                            selfAppearance,
                            cancellationToken);
                        if (!CanContinueTownProjection(
                                session,
                                projectionGuard))
                        {
                            return;
                        }
                        // 已在场玩家只插入到达者，不能跟 AREA_USERS：
                        // 42596/46364 实机 0x18 会 setDrawLoadingMode 并切到 TOWN 模块。
                        await recipient.SendPacketAsync(
                            selfArea,
                            cancellationToken);
                    },
                    $"town-presence characterId=" +
                    $"{recipient.Player?.CharacterId ?? 0}"));
            }
            if (peerProjections.Count > 0)
                await Task.WhenAll(peerProjections);
            if (_publishTownPartyLists != null && CanContinueTownProjection(session, projectionGuard))
                await _publishTownPartyLists();
        }

        /// <summary>
        /// 联机同屏: 会话真正离开时通知同区域其它玩家移除该分身。
        /// A21 客户端会把 USER_LEAVE(0x0006) 同时应用到队伍槽，普通切图不得发送。
        /// </summary>
        public async Task NotifyLeaveAsync(EnhancedClientSession session)
        {
            if (_sessions == null || session?.Player == null || session.Player.CharacterId <= 0)
                return;
            await _sessions.BroadcastToAreaAsync(
                session.Player.CurTownId,
                session.Player.CurAreaId,
                session.Player.CharacterId,
                BuildUserLeavePacket(session.Player.UserId),
                session.ListenerPort);
        }

        public async Task Handle_ENUM_CMDPACKET_FINISH_LOADING(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // A21 dungeon loading has no CMD 37 response; the client consumes
            // NOTI 30 as the completion notification. Keep the town response
            // for callers that are not attached to a live DungeonRun.
            if (session?.Player?.CurrentRun == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0025, CommonPacketBodyBuilder.BuildSuccessAck()));
            }
            await SendFinishLoadingCompletionAsync(session);
        }

        internal async Task SendFinishLoadingCompletionAsync(
            EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, FinishLoadingBuilder.BuildNotification()));
            await _growthCapsule.SendExpProgressAsync(session, "finish-loading");
        }

        internal Task<bool> TrySendFinishLoadingCompletionAsync(
            EnhancedClientSession session,
            Func<bool> canContinue,
            Action onReleased = null)
            => SendFinishLoadingCompletionCoreAsync(
                canContinue,
                (packet, fence) => session.TrySendPacketAsync(
                    packet, CancellationToken.None, fence),
                () => _growthCapsule.SendExpProgressAsync(session, "finish-loading"), onReleased);

        // Shared guarded path: the transport must evaluate the same source
        // fence inside its send lock. The ordinary unguarded town call above
        // retains its existing wire and follow-up behavior.
        internal static async Task<bool> SendFinishLoadingCompletionCoreAsync(
            Func<bool> canContinue,
            Func<byte[], Func<bool>, Task<bool>> send,
            Func<Task> afterCompletion = null,
            Action onReleased = null)
        {
            ArgumentNullException.ThrowIfNull(canContinue);
            ArgumentNullException.ThrowIfNull(send);
            if (!canContinue())
                return false;
            var packet = GamePacketEnvelopeBuilder.Build(
                0, (ushort)NotiPacketTypeA21.FINISH_LOADING,
                FinishLoadingBuilder.BuildNotification());
            if (!await send(packet, canContinue) || !canContinue())
                return false;
            // A local mechanism observes the successful release before an
            // unrelated follow-up. Growth notification failure cannot undo a
            // FINISH_LOADING frame that the client has already received.
            onReleased?.Invoke();
            if (afterCompletion != null)
                await afterCompletion();
            return true;
        }

        public async Task Handle_ENUM_CMDPACKET_TELEPORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ItemTeleportRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid body: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"length={body?.Length ?? 0} " +
                    $"raw={(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            var (cid, _) = InventoryHandler.ResolveOwner(session);
            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    cid,
                    out var lease))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected missing owned inventory: " +
                    $"cid={cid} item=0x{request.ItemTemplateId:X8}");
                return;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        cid)
                    || lease.Inventory.CountMainItem(
                        request.ItemTemplateId) < 1)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TELEPORT rejected item not owned: " +
                        $"cid={cid} item=0x{request.ItemTemplateId:X8}");
                    return;
                }
            }

            if (!TeleportConsumableDefinitionProvider.TryResolve(
                    request.ItemTemplateId,
                    out var definition)
                || !definition.IsValid
                || definition.Kind
                    != TeleportConsumableKind.TownSelection)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid item definition: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                return;
            }

            if (request.TargetTownId > byte.MaxValue)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid town id: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                return;
            }

            var targetTownId = (int)request.TargetTownId;
            if (targetTownId == GuildAgitAccessPolicy.TownId)
            {
                // A town-selection item is not a guild-entrance authorization.
                // Reject before consuming it or persisting an agit gate anchor.
                await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                return;
            }
            if (!GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    targetTownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected by channel policy: " +
                    $"cid={session.Player.CharacterId} listener={session.ListenerPort} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"targetTown={request.TargetTownId} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                await ChannelTownRestrictionSender.SendAsync(session, targetTownId);
                return;
            }

            CeraRoomInfo ceraRoomInfo;
            try
            {
                ceraRoomInfo = Town.GetCeraRoomInfo(targetTownId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid target: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId} error={ex.Message}");
                return;
            }
            if (ceraRoomInfo.Town != request.TargetTownId)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected target without gate: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId}");
                return;
            }

            var player = session.Player;
            var previousTownId = player.CurTownId;
            var previousAreaId = player.CurAreaId;
            var persistPosition = GameChannelSpawnPolicy.ShouldPersistPosition(
                session.ListenerPort,
                ceraRoomInfo.Town,
                ceraRoomInfo.Area);
            if (!TeleportConsumableCommitService.TryCommit(
                    lease,
                    request.ItemTemplateId,
                    ceraRoomInfo.Town,
                    ceraRoomInfo.Area,
                    ceraRoomInfo.X,
                    ceraRoomInfo.Y,
                    direction: 0,
                    areaState: 3,
                    persistPosition,
                    out var consumeResult))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT commit failed: " +
                    $"cid={cid} item=0x{request.ItemTemplateId:X8} " +
                    $"target={ceraRoomInfo.Town}:{ceraRoomInfo.Area}");
                return;
            }

            session.Player.CurTownId = ceraRoomInfo.Town;
            session.Player.CurAreaId = ceraRoomInfo.Area;
            session.Player.CurPosX = ceraRoomInfo.X;
            session.Player.CurPosY = ceraRoomInfo.Y;
            session.Player.CurDirection = 0;
            session.Player.CurAreaState = 3;
            if (persistPosition)
                session.Player.LastPositionPersistAt = DateTime.UtcNow;

            // A later SET_USER_AREA can no longer recover the source area after
            // this commit. Reuse normal town departure; USER_LEAVE clears party slots.
            var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            if (_sessions != null)
            {
                await _sessions.BroadcastToAreaAsync(
                    previousTownId,
                    previousAreaId,
                    cid,
                    BuildAreaTransitionDeparturePacket(selfSnapshot),
                    session.ListenerPort);
                if (!ReferenceEquals(session.Player, player)
                    || !InventoryContext.IsCurrentLease(lease, session.SessionId, cid))
                    return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] TELEPORT: consumed item=" +
                $"0x{request.ItemTemplateId:X8} slot={consumeResult.SlotIndex} " +
                $"remaining={consumeResult.RemainingCount}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.LOAD_COOLTIME_ITEM_INFO,
                TeleportPacketBuilder.BuildTeleportNotification(
                    request.ItemTemplateId)));
            if (_refresh != null)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    consumeResult.SlotIndex);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(selfSnapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00CA,
                new byte[] { 0x00 }));

        }

        public async Task Handle_ENUM_CMDPACKET_PARTY_TELEPORT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PartyTeleportRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected invalid body: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"length={body?.Length ?? 0}");
                return;
            }

            if (request.TownId == GuildAgitAccessPolicy.TownId)
            {
                await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                return;
            }

            if (!GameChannelTeleportPolicy.CanUsePartyTeleport(
                    session.ListenerPort)
                || !GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    request.TownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected by channel policy: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"listener={session?.ListenerPort ?? 0} " +
                    $"target={request.TownId}:{request.AreaId}");
                await ChannelTownRestrictionSender.SendAsync(session, request.TownId);
                return;
            }

            if (session?.Player == null
                || session.Player.CurrentRun != null
                || _partyManager == null
                || _sessions == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected unavailable state: " +
                    $"cid={session?.Player?.CharacterId ?? 0}");
                return;
            }

            var party = _partyManager.GetPartyByUser(
                session.Player.UserId);
            var snapshot = party == null
                ? null
                : _partyManager.GetPartySnapshot(party.PartyId);
            if (snapshot == null
                || !snapshot.IsLeader(session.Player.UserId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected non-leader: " +
                    $"cid={session.Player.CharacterId} " +
                    $"uid={session.Player.UserId}");
                return;
            }

            var areaBody = new byte[6];
            Buffer.BlockCopy(body, 0, areaBody, 0, areaBody.Length);
            var eligible = new List<EnhancedClientSession>();
            foreach (var member in snapshot.MembersBySlot())
            {
                EnhancedClientSession memberSession;
                if (member.UserId == session.Player.UserId)
                {
                    memberSession = session;
                }
                else if (!_sessions.TryGet(
                             member.CharacterId,
                             out memberSession))
                {
                    continue;
                }

                if (memberSession?.Player == null
                    || memberSession.SessionId != member.SessionId
                    || memberSession.ListenerPort != session.ListenerPort
                    || memberSession.Player.CurrentRun != null
                    || !GameChannelTeleportPolicy.CanUsePartyTeleport(
                        memberSession.ListenerPort)
                    || !GameChannelSpawnPolicy.CanEnterTown(
                        memberSession.ListenerPort,
                        request.TownId))
                {
                    continue;
                }

                eligible.Add(memberSession);
            }

            // 全有或全无: 任一可传送队员与队长不在同一房间则整队不传送。
            // 此时队长尚未移动, CurTownId/CurAreaId 即发起时的房间。
            foreach (var memberSession in eligible)
            {
                if (IsPartyTeleportSameRoom(
                        session.Player,
                        memberSession.Player))
                {
                    continue;
                }

                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT blocked by remote member: " +
                    $"leaderCid={session.Player.CharacterId} " +
                    $"memberCid={memberSession.Player.CharacterId} " +
                    $"leader={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"member={memberSession.Player.CurTownId}:{memberSession.Player.CurAreaId}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.Build(
                        PartyTeleportRemoteMemberMessage)));
                return;
            }

            // 全有或全无: 任一队员未解锁目标区域(等级/任务门槛)则整队不传送。
            // 客户端对未解锁区域的房间出口有本地门禁, 把未解锁队员带进去会
            // 困在房间里; 逐人跳过又会静默拆散队伍。
            foreach (var memberSession in eligible)
            {
                if (MeetsTownAreaPermission(
                        memberSession,
                        request.TownId,
                        request.AreaId,
                        out var permissionReason))
                {
                    continue;
                }

                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT blocked by locked member: " +
                    $"leaderCid={session.Player.CharacterId} " +
                    $"memberCid={memberSession.Player.CharacterId} " +
                    $"target={request.TownId}:{request.AreaId} " +
                    $"reason={permissionReason}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                    ServerNoticeMessageBuilder.Build(
                        PartyTeleportMemberLockedMessage)));
                return;
            }

            // Serialize with character changes, town/guild transitions and
            // relogin. Acquire the existing gates in character order; no
            // second lock registry or online guild-content balance is needed.
            if (_guildTransitions == null || _guilds == null
                || !Town.TryGetArea(request.TownId, request.AreaId, out _))
                return;
            // Capture the request's original participants BEFORE waiting on
            // any gate. A queued request must not acquire a newly selected
            // Player/account/lease and reinterpret the old packet for it.
            var sources = new List<(EnhancedClientSession Session, PlayerContext Player,
                InventoryLease Lease, TownUserSnapshot Area, Func<bool> Current, bool Persist)>();
            foreach (var memberSession in eligible)
            {
                var player = memberSession.Player;
                var account = memberSession.Account;
                var gameSession = memberSession.GameSession;
                if (player == null || account == null || gameSession == null
                    || !snapshot.Members.Any(member => member.CharacterId == player.CharacterId
                        && member.UserId == player.UserId && member.SessionId == memberSession.SessionId)
                    || !SessionDirectory.IsTownPresence(player)
                    || player.DungeonSelectionPending || player.CurrentDungeonSelection != null
                    || !InventoryContext.TryGetOwnedLease(memberSession.SessionId,
                        player.CharacterId, out var inventoryLease)
                    || inventoryLease.AccountId != account.AccountId
                    || !string.Equals(inventoryLease.Inventory.Database?.ConnectionString,
                        _guilds.Database.ConnectionString, StringComparison.Ordinal)
                    || !IsPartyTeleportSameRoom(session.Player, player)
                    || !MeetsTownAreaPermission(memberSession, request.TownId, request.AreaId, out _)
                    || !CanChangeRaidArea(memberSession, request.TownId, request.AreaId))
                    return;
                if (player.CurTownId != request.TownId
                    && request.TownId == EpicRoadEntryService.TargetTownId
                    && !TryValidateEpicRoadTownEntry(memberSession, request.AreaId, out _))
                    return;
                var characterId = player.CharacterId;
                var userId = player.UserId;
                var runGeneration = player.CurrentDungeonRunGeneration;
                var selectionGeneration = player.CurrentDungeonSelectionGeneration;
                bool Current() => ReferenceEquals(memberSession.Player, player)
                    && ReferenceEquals(memberSession.Account, account)
                    && ReferenceEquals(memberSession.GameSession, gameSession)
                    && player.CharacterId == characterId && player.UserId == userId
                    && inventoryLease.AccountId == account.AccountId
                    && player.CurrentDungeonRunGeneration == runGeneration
                    && player.CurrentDungeonSelectionGeneration == selectionGeneration
                    && SessionDirectory.IsTownPresence(player)
                    && !player.DungeonSelectionPending && player.CurrentDungeonSelection == null
                    && _guildTransitions.IsCurrent(memberSession)
                    && InventoryContext.IsCurrentLease(inventoryLease, memberSession.SessionId, characterId);
                sources.Add((memberSession, player, inventoryLease,
                    TownAreaNotificationBuilder.CreateCurrentSnapshot(player), Current,
                    GameChannelSpawnPolicy.ShouldPersistPosition(memberSession.ListenerPort,
                        request.TownId, request.AreaId)));
            }
            var transitionLeases = new List<IDisposable>();
            IReadOnlyList<int> guildRefreshMembers = null;
            try
            {
                foreach (var source in sources.OrderBy(source => source.Lease.CharacterId))
                {
                    if (!source.Current()) return;
                    var transition = await _guildTransitions.AcquireIfCurrentAsync(source.Session);
                    if (transition == null) return;
                    transitionLeases.Add(transition);
                    if (!source.Current()) return;
                }
                bool PartyCurrent()
                {
                    var current = _partyManager.GetPartySnapshot(snapshot.PartyId);
                    return current != null && current.LeaderUserId == snapshot.LeaderUserId
                        && current.Count == snapshot.Count
                        && snapshot.Members.All(member => current.Members.Any(candidate =>
                            candidate.CharacterId == member.CharacterId
                            && candidate.UserId == member.UserId
                            && candidate.SessionId == member.SessionId));
                }
                if (!PartyCurrent()) return;

                bool SourcesCurrent() => PartyCurrent() && sources.All(source => source.Current()
                    && source.Player.CurTownId == source.Area.TownId
                    && source.Player.CurAreaId == source.Area.AreaId
                    && source.Player.CurPosX == source.Area.PosX
                    && source.Player.CurPosY == source.Area.PosY);
                if (!SourcesCurrent()) return;

                // A repeat after a committed move is a projection retry. It
                // must not consume another use, including the last use.
                var alreadyAtTarget = sources.All(source =>
                    source.Player.CurTownId == request.TownId
                    && source.Player.CurAreaId == request.AreaId
                    && source.Player.CurPosX == request.X && source.Player.CurPosY == request.Y);
                var remainingUses = -1;
                var guild = _guilds.GetForMember(session.Player.CharacterId);
                if (!alreadyAtTarget)
                {
                    var leaderSource = sources.FirstOrDefault(source => ReferenceEquals(source.Session, session));
                    if (guild == null || leaderSource.Lease == null
                        || !GuildPartyTeleportCommitService.TryCommit(leaderSource.Lease, guild.Id,
                            sources.Select(source => new GuildPartyTeleportTarget(source.Lease, source.Persist)).ToArray(),
                            request.TownId, request.AreaId, request.X, request.Y, SourcesCurrent, out remainingUses))
                    {
                        FileLogger.Log($"[{ProtocolName}] PARTY_TELEPORT rejected guild quota or stale source: "
                            + $"cid={session.Player.CharacterId} party={snapshot.PartyId}");
                        await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                        return;
                    }
                    // The transaction has committed every durable position.
                    // Apply all online locations before the first await so a
                    // projection failure cannot split the logical movement.
                    foreach (var source in sources)
                    {
                        source.Player.CurTownId = request.TownId;
                        source.Player.CurAreaId = request.AreaId;
                        source.Player.CurPosX = request.X;
                        source.Player.CurPosY = request.Y;
                        source.Player.CurDirection = 5;
                        source.Player.CurAreaState = 0;
                        if (source.Persist) source.Player.LastPositionPersistAt = DateTime.UtcNow;
                    }
                }
                foreach (var source in sources)
                {
                    bool ProjectionCurrent() => source.Current()
                        && source.Player.CurTownId == request.TownId && source.Player.CurAreaId == request.AreaId
                        && source.Player.CurPosX == request.X && source.Player.CurPosY == request.Y;
                    try
                    {
                        await SetUserAreaCoreAsync(source.Session, areaBody,
                            TownProjectionGuard.ForAuthorizedReturn(ProjectionCurrent),
                            committedTeleportSource: source.Area);
                    }
                    catch (Exception exception)
                    {
                        FileLogger.Log($"[{ProtocolName}] PARTY_TELEPORT committed projection failed: "
                            + $"cid={source.Player.CharacterId}: {exception.Message}");
                    }
                }
                if (remainingUses >= 0 && _publishGuildState != null && guild != null)
                    guildRefreshMembers = _guilds.GetMemberIds(guild.Id);
                FileLogger.Log($"[{ProtocolName}] PARTY_TELEPORT: leaderCid={session.Player.CharacterId} "
                    + $"party={snapshot.PartyId} target={request.TownId}:{request.AreaId} "
                    + $"pos=({request.X},{request.Y}) direction={request.Direction} flag={request.Flag} "
                    + $"moved={sources.Count}/{snapshot.Count} guildUses={remainingUses} retry={alreadyAtTarget}");
            }
            finally
            {
                for (var index = transitionLeases.Count - 1; index >= 0; index--)
                    transitionLeases[index].Dispose();
            }
            // The existing publisher acquires the same character gates.
            // Release the movement leases before its guild-wide refresh.
            if (guildRefreshMembers != null)
                await _publishGuildState(guildRefreshMembers);
        }

        private bool MeetsTownAreaPermission(
            EnhancedClientSession memberSession,
            int townId,
            int areaId,
            out string reason)
        {
            reason = null;
            if (!GameWorld.Town.TryGetAreaPermission(
                    townId,
                    areaId,
                    out var permission))
            {
                return true;
            }

            var cid = memberSession.Player.CharacterId;
            reason = GameWorld.TownAreaPermissionPolicy.GetDenyReason(
                permission,
                memberSession.Player.Level,
                questId => _database != null
                    && new Game.Quests.QuestRepository(_database.ConnectionString)
                        .LoadClearedFlags(cid)
                        .ContainsKey(questId));
            return reason == null;
        }

        private bool CanRestoreGuildAgitArea(EnhancedClientSession session, byte targetAreaId,
            TownProjectionGuard guard)
        {
            // The return owner already applied its exact frozen anchor. A
            // selection is still present until the final town projection, so
            // this restoration must not be mistaken for a new client entry.
            // A removed member can restore that anchor and then leave the agit.
            var player = session?.Player;
            return (guard.Selection != null || guard.EndedRun.IsValid)
                && CanContinueTownProjection(session, guard)
                && player != null && player.CurTownId == GuildAgitAccessPolicy.TownId
                && player.CurAreaId == targetAreaId
                && _guildTransitions?.IsCurrent(session) == true
                && InventoryContext.TryGetOwnedLease(session.SessionId, player.CharacterId, out _)
                && Town.TryGetArea(GuildAgitAccessPolicy.TownId, targetAreaId, out _);
        }

        internal bool TryValidateGuildAgitEntry(EnhancedClientSession session, byte targetAreaId, out string reason)
        {
            reason = "guild_agit_source_unavailable";
            var player = session?.Player;
            if (_guilds == null || _guildTransitions == null || player == null
                || !_guildTransitions.IsCurrent(session) || player.CharacterId <= 0 || player.UserId == 0
                || session.Account?.AccountId <= 0 || session.Account == null || session.GameSession == null
                || !InventoryContext.TryGetOwnedLease(session.SessionId, player.CharacterId, out _)
                || !SessionDirectory.IsTownPresence(player) || player.DungeonSelectionPending
                || player.CurrentDungeonSelection != null)
                return false;
            if (_guilds.GetForMember(player.CharacterId) == null)
            {
                reason = "guild_agit_not_member";
                return false;
            }
            var fromPersonalRoom = Town.IsCeraRoom(player.CurTownId, player.CurAreaId)
                && targetAreaId == GuildAgitAccessPolicy.BasicAreaId;
            var withinAgit = player.CurTownId == GuildAgitAccessPolicy.TownId
                && Town.TryGetArea(GuildAgitAccessPolicy.TownId, player.CurAreaId, out _)
                && Town.TryGetArea(GuildAgitAccessPolicy.TownId, targetAreaId, out _)
                && (player.CurAreaId == targetAreaId
                    || Town.HasDirectAreaConnection(GuildAgitAccessPolicy.TownId, player.CurAreaId, targetAreaId));
            if (!fromPersonalRoom && !withinAgit)
            {
                reason = "guild_agit_route_unavailable";
                return false;
            }
            if (!Town.TryGetAreaPermission(GuildAgitAccessPolicy.TownId, targetAreaId, out var permission))
                return false;
            var cleared = new Game.Quests.QuestRepository(_database.ConnectionString)
                .LoadClearedFlags(player.CharacterId);
            reason = TownAreaPermissionPolicy.GetDenyReason(permission, player.Level, cleared.ContainsKey);
            return reason == null;
        }

        internal bool TryValidateEpicRoadTownEntry(EnhancedClientSession session, byte targetAreaId, out string reason)
        {
            reason = "epic_road_entry_unavailable";
            var player = session?.Player;
            if (player == null || player.CharacterId <= 0 || player.UserId == 0
                || (session.Account?.AccountId ?? 0) <= 0 || session.GameSession == null
                || player.CurrentRun != null || player.DungeonSelectionPending || player.UserState != 0
                || !player.TownPresenceReady || _epicRoadEntry?.IsEnabled() != true
                || targetAreaId != EpicRoadEntryService.TargetAreaId)
                return false;
            if (_sessions != null && (!_sessions.TryGet(player.CharacterId, out var current)
                    || !ReferenceEquals(current, session)))
            {
                reason = "epic_road_stale_town_source";
                return false;
            }
            // Visibility is owned by the event member and NPC hide-town rule.
            // This NPC is not in the static MAP NPC table; requiring a static
            // placement would reject every legal event entry in this archive.
            if (_epicRoadEntry.Definition.EntranceHiddenTownIds.Contains(player.CurTownId)
                || !Town.TryGetArea(player.CurTownId, player.CurAreaId, out _))
            {
                reason = "epic_road_town_source_not_allowed";
                return false;
            }
            try
            {
                var cleared = new Game.Quests.QuestRepository(_database.ConnectionString)
                    .LoadClearedFlags(player.CharacterId);
                reason = TownAreaPermissionPolicy.GetDenyReason(_epicRoadEntry.Definition.TargetPermission,
                    player.Level, quest => cleared.ContainsKey(quest));
                return reason == null;
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException
                or InvalidOperationException or System.IO.IOException)
            {
                FileLogger.Log($"[EpicRoad] town permission unavailable cid={player.CharacterId}: {exception.Message}");
                reason = "epic_road_town_permission_unavailable";
                return false;
            }
        }

        // 组队传送同房间判定: 队员当前 town/area 必须与队长完全一致。
        internal static bool IsPartyTeleportSameRoom(
            PlayerContext leader,
            PlayerContext member)
            => leader != null
               && member != null
               && leader.CurTownId == member.CurTownId
               && leader.CurAreaId == member.CurAreaId;

        public async Task Handle_ENUM_CMDPACKET_SOLO_TELEPOART(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!SoloTeleportRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SOLO_TELEPOART rejected invalid body: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"length={body?.Length ?? 0}");
                return;
            }

            if (!GameChannelTeleportPolicy.CanUsePartyTeleport(
                    session.ListenerPort)
                || !GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    request.TownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SOLO_TELEPOART rejected by channel policy: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"listener={session?.ListenerPort ?? 0} " +
                    $"target={request.TownId}:{request.AreaId}");
                await ChannelTownRestrictionSender.SendAsync(session, request.TownId);
                return;
            }

            if (session?.Player == null
                || session.Player.CurrentRun != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SOLO_TELEPOART rejected unavailable state: " +
                    $"cid={session?.Player?.CharacterId ?? 0}");
                return;
            }

            var areaBody = new byte[6];
            areaBody[0] = request.TownId;
            areaBody[1] = request.AreaId;
            BitConverter.TryWriteBytes(areaBody.AsSpan(2), request.X);
            BitConverter.TryWriteBytes(areaBody.AsSpan(4), request.Y);
            await SetUserAreaCoreAsync(
                session,
                areaBody,
                default(TownProjectionGuard));

            FileLogger.Log(
                $"[{ProtocolName}] SOLO_TELEPOART: " +
                $"cid={session.Player.CharacterId} " +
                $"target={request.TownId}:{request.AreaId} " +
                $"pos=({request.X},{request.Y}) direction={request.Direction}");
        }

        public async Task Handle_ENUM_CMDPACKET_GIVEUP_GAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var sourceRun = session?.Player?.CurrentRun;
            if (sourceRun == null)
            {
                var selection = session?.Player?.CurrentDungeonSelection;
                var transitionGate = selection?.PartyCohort?.TransitionGate;
                var rerouteToRun = false;
                if (transitionGate != null)
                    await transitionGate.WaitAsync();
                try
                {
                    // sourceRun was sampled before taking the cohort gate. If
                    // SELECT won while this command waited, restart against
                    // the now-current run instead of returning a stale
                    // selection context.
                    if (session?.Player?.CurrentRun != null)
                    {
                        rerouteToRun = true;
                    }
                    else if (selection == null || !selection.TryBeginReturn())
                    {
                        if (!await TryFinishRaidTownReturnAsync(session, header.type))
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] RETURN_TO_TOWN rejected without run: " +
                                $"type=0x{header.type:X4} cid={session?.Player?.CharacterId ?? 0} " +
                                $"selection={(selection?.SelectionId ?? 0)}");
                        }
                        return;
                    }
                    else
                    {
                        var selectionGuard = TownProjectionGuard.ForSelection(selection);
                        try
                        {
                            if (!await ReturnSelectionToTownAsync(
                                    session,
                                    selection,
                                    selectionGuard,
                                    header.type,
                                    sendCommandResponse: true))
                            {
                                selection.CancelReturn();
                                return;
                            }
                            if (ShouldFanOutPartySelectionReturn(header.type))
                            {
                                await TryFanOutPartySelectionReturnAsync(
                                    session,
                                    selection,
                                    header.type);
                            }
                            if (!CanContinueTownProjection(session, selectionGuard))
                                return;
                            await SendTownAccountStateAsync(
                                session,
                                "leave-dungeon-selection",
                                selectionGuard);
                            if (!CanContinueTownProjection(session, selectionGuard))
                                return;
                            if (CanContinueTownProjection(session, selectionGuard))
                                session.Player.CompleteDungeonSelection(selection);
                            FileLogger.Log(
                                $"[{ProtocolName}] RETURN_TO_TOWN from selection: " +
                                $"type=0x{header.type:X4} cid={session.Player.CharacterId} " +
                                $"selection={selection.SelectionId}");
                        }
                        catch
                        {
                            if (session?.Player?.IsCurrentDungeonSelection(selection) == true)
                                selection.CancelReturn();
                            throw;
                        }
                    }
                }
                finally
                {
                    transitionGate?.Release();
                }
                if (rerouteToRun)
                {
                    await Handle_ENUM_CMDPACKET_GIVEUP_GAME(session, header, body);
                    return;
                }
                return;
            }

            var runTransitionGate =
                sourceRun.EntryPartySelectionCohort?.TransitionGate;
            var rerouteAfterRunGate = false;
            if (runTransitionGate != null)
                await runTransitionGate.WaitAsync();
            try
            {
                var currentRun = session?.Player?.CurrentRun;
                if (currentRun == null
                    || !currentRun.Matches(sourceRun.CaptureIdentity()))
                {
                    rerouteAfterRunGate = true;
                }
                else
                {
                    await HandleGiveupFromRunAsync(
                        session,
                        header,
                        currentRun);
                }
            }
            finally
            {
                runTransitionGate?.Release();
            }
            if (rerouteAfterRunGate)
                await Handle_ENUM_CMDPACKET_GIVEUP_GAME(session, header, body);
        }

        internal Task<bool> TryHandleDelayedDungeonGiveupAsync(
            EnhancedClientSession session,
            DungeonParticipantRoomIdentity expectedRoom,
            Func<bool> canContinue)
            => TryHandleDelayedDungeonGiveupCoreAsync(
                session, expectedRoom, canContinue,
                sourceRun => HandleGiveupFromRunAsync(session,
                    new GamePacketHeader
                    {
                        cmd = 1,
                        type = (ushort)CmdPacketTypeA21.GIVEUP_GAME,
                    }, sourceRun),
                _ => Dungeon.DungeonRunLifecycle.EndRunAsync(
                    session, DungeonRunEndReason.EntryRejected,
                    expectedRoom.Run, _dungeonInstances));

        // Timer/projection cleanup is not a client's request to exit whatever
        // run happens to be current. Keep the source room through the cohort
        // wait, then claim the existing ending transition before releasing the
        // run lock. The normal client entry keeps its rerouting semantics.
        internal static async Task<bool> TryHandleDelayedDungeonGiveupCoreAsync(
            EnhancedClientSession session,
            DungeonParticipantRoomIdentity expectedRoom,
            Func<bool> canContinue,
            Func<DungeonRun, Task> giveupFromRun,
            Func<DungeonRun, Task> ensureEnded)
        {
            ArgumentNullException.ThrowIfNull(canContinue);
            ArgumentNullException.ThrowIfNull(giveupFromRun);
            ArgumentNullException.ThrowIfNull(ensureEnded);
            var player = session?.Player;
            var sourceRun = player?.CurrentRun;
            if (!expectedRoom.IsValid || sourceRun == null
                || !player.IsCurrentDungeonParticipantRoom(expectedRoom)
                || !canContinue())
                return false;

            var gate = sourceRun.EntryPartySelectionCohort?.TransitionGate;
            if (gate != null)
                await gate.WaitAsync();
            try
            {
                // Never hold this monitor over persistence or network awaits.
                // MOVE_MAP publishes its room under the same run lock and
                // cannot reserve another projection after TryBeginEnding.
                lock (sourceRun.SyncRoot)
                {
                    if (!ReferenceEquals(player.CurrentRun, sourceRun)
                        || !player.IsCurrentDungeonParticipantRoom(expectedRoom)
                        || !canContinue()
                        || !sourceRun.TryBeginEnding())
                        return false;
                }
                try
                {
                    await giveupFromRun(sourceRun);
                    return true;
                }
                finally
                {
                    // An exception before the normal EndRun must not leave a
                    // claimed Ending run attached. Production uses the same
                    // expected-run ending owner; a replacement is never ended.
                    if (ReferenceEquals(player.CurrentRun, sourceRun))
                        await ensureEnded(sourceRun);
                }
            }
            finally
            {
                gate?.Release();
            }
        }

        private async Task HandleGiveupFromRunAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DungeonRun sourceRun)
        {
            var sourceRunIdentity = sourceRun.CaptureIdentity();
            var deferTutorialVillageObjectList = sourceRun.IsA21TutorialEntry;
            var expectedUserId = session?.Player?.UserId ?? (ushort)0;
            var expectedSessionId = session?.SessionId ?? Guid.Empty;
            var expectedPartyId = 0;
            if (header.type == 0x002A && expectedUserId != 0)
            {
                var liveParty = _partyManager?.GetPartyByUser(
                    expectedUserId);
                var currentParty = liveParty == null
                    ? null
                    : _partyManager.GetPartySnapshot(liveParty.PartyId);
                var currentMember = currentParty?.GetMember(
                    expectedUserId);
                if (currentMember?.SessionId == expectedSessionId)
                    expectedPartyId = currentParty.PartyId;
            }
            Func<Task> afterRunEnded = null;
            if (header.type == 0x002A &&
                _dungeonGiveupPartyDeparture != null)
            {
                afterRunEnded = () => _dungeonGiveupPartyDeparture(
                    session,
                    expectedUserId,
                    expectedSessionId,
                    expectedPartyId);
            }
            if (!await ReturnSelfToTownAsync(
                    session,
                    header,
                    sourceRunIdentity,
                    sourceRun.TownReturnAnchor,
                    sendCommandResponse: true,
                    afterRunEnded: afterRunEnded))
            {
                return;
            }

            if (deferTutorialVillageObjectList)
            {
                session.A21TutorialReturnNeedsVillageObjectList = true;
                FileLogger.Log(
                    $"[{ProtocolName}] A21 tutorial return defers " +
                    $"VILLAGE_OBJECT_LIST until town STORY_PAUSE cid={session.Player.CharacterId}");
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x00CA,
                    new byte[] { 0x00 }));
            }

            // 跟随退出只在通关回城 BACK_2_VILLAGE 0x84 触发。
            // GIVEUP_GAME 0x2A 只让本人回城，不拉仍在副本的队员；
            // 回城成功后在当前 cohort gate 内提交本人离队/末人保队。
            if (header.type == 0x0084)
                await TryFanOutLeaderReturnToTownAsync(
                    session,
                    header,
                    sourceRunIdentity);
            else
                FileLogger.Log(
                    $"[{ProtocolName}] GIVEUP_GAME(type=0x{header.type:X2}): " +
                    $"cid={session.Player?.CharacterId} 独自回城并等待离队提交, " +
                    $"不拉仍在副本的队员");

            // A21 CMD 成功响应已在 USER_STATE 之前发送；回城尾部不再追加
            // 第二个 ACK 或 subtype0。客户端随后继续发送教程
            // SYNC_ITEM_SPACE、STORY_PAUSE、GET_PCROOM_TIME_POINT_ITEM
            // 和 SET_USER_POSITION。
        }

        // 把【单个会话】自己拉回城镇(EndRun + 城镇区域同步)。队长/队员复用同一序列。
        private async Task<bool> ReturnSelfToTownAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity runIdentity,
            DungeonTownReturnAnchor returnAnchor,
            bool sendCommandResponse,
            Func<Task> afterRunEnded = null)
        {
            var controlsSource = session?.GuardianAltarControls;
            var releaseGuardianControls = header.type == (ushort)CmdPacketTypeA21.GIVEUP_GAME
                && session?.Player?.CurrentRun?.Instance.Mechanisms.GuardianAltar != null
                && session.Player.IsCurrentDungeonRun(runIdentity)
                && controlsSource != null
                && ReferenceEquals(session.GameSession, controlsSource.Generation)
                && ReferenceEquals(session.Account, controlsSource.Account)
                && session.Account?.AccountId == controlsSource.AccountId
                && session.Player.CharacterId == controlsSource.CharacterId
                && session.Player.UserId == controlsSource.UserId
                && controlsSource.RunGeneration == runIdentity.RunGeneration
                && controlsSource.SelectionGeneration == session.Player.CurrentDungeonSelectionGeneration;
            if (!await Dungeon.DungeonRunLifecycle.EndRunAsync(
                    session,
                    DfoServer.Game.Dungeon.DungeonRunEndReason.ReturnToTown,
                    runIdentity,
                    _dungeonInstances))
            {
                return false;
            }
            try
            {
                var endedRunGuard = TownProjectionGuard.ForEndedRun(runIdentity);
                var projectionGuard = endedRunGuard;
                var controlsRestoreCompleted = false;
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
                if (releaseGuardianControls)
                {
                    var pending = controlsSource with { ActivityReleasePending = true };
                    if (!session.TryReplaceGuardianControlsSource(controlsSource, pending)) return false;
                    controlsSource = pending;
                }
                Dungeon.DungeonRunLifecycle.ApplyTownReturnAnchor(
                    session.Player,
                    returnAnchor,
                    session.ListenerPort);
                session.Player.UserState = 0x00;

                if (releaseGuardianControls)
                {
                    // Keep this return's precise source through every queued
                    // Town write. Its successful restore may clear that source;
                    // a replacement menu or epoch must still stop the old tail.
                    projectionGuard = TownProjectionGuard.ForAuthorizedReturn(() =>
                        IsCurrentGuardianControlsScope(session, endedRunGuard, controlsSource)
                        && (ReferenceEquals(session.GuardianAltarControls, controlsSource)
                            || (controlsRestoreCompleted && session.GuardianAltarControls == null)));
                    if (!CanContinueTownProjection(session, projectionGuard)) return false;
                }

                // A21 客户端抓包中，GIVEUP_GAME/BACK_2_VILLAGE 的 CMD 成功响应
                // body=[01] 位于 USER_STATE 之前。客户端先用 CMD 响应结束副本
                // 请求状态，再开始消费城镇 USER_STATE/USER_AREA/AREA_USERS。
                if (sendCommandResponse)
                {
                    await SendTownProjectionPacketAsync(session, projectionGuard,
                        BuildReturnToTownSuccessPacket(header.type));
                    if (!CanContinueTownProjection(session, projectionGuard))
                    {
                        return false;
                    }
                }

                if (releaseGuardianControls)
                {
                    var released = await TryReleaseGuardianActivityAsync(session, projectionGuard, controlsSource);
                    if (released == null) return false;
                    controlsSource = released;
                }

                // A21 回城顺序的第一个城镇投影包是 USER_STATE(0x0003)。
                // 该包不能只更新 PlayerContext 后省略；客户端会以它确认
                // 角色已离开副本，再消费 USER_AREA/AREA_USERS 的城镇坐标。
                await SendTownProjectionPacketAsync(session, projectionGuard, GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.USER_STATE,
                    EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }

                await SetUserAreaCoreAsync(
                    session,
                    BuildTownAreaProjectionBody(session.Player),
                    projectionGuard,
                    releaseGuardianControls ? controlsSource : null,
                    releaseGuardianControls ? () => controlsRestoreCompleted = true : null);
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
                if (ShouldRefreshPartyProjectionAfterTownReturn(header.type))
                {
                    await RefreshPartyProjectionAfterTownReturnAsync(
                        session,
                        projectionGuard);
                }
                // 回城过图后客户端重置结婚属性 UI：城镇 USER_STATE/USER_AREA
                // 投影之后补发婚礼回放三包。只覆盖进/出本触发点，
                // 不挂城镇内每次过图。
                await InventoryRefreshSender.SendWeddingReplayRefresh(session);
                return CanContinueTownProjection(
                    session,
                    projectionGuard);
            }
            finally
            {
                if (afterRunEnded != null)
                {
                    try
                    {
                        await afterRunEnded();
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] post-EndRun party policy failed: " +
                            $"cid={session?.Player?.CharacterId ?? 0} " +
                            $"error={ex.Message}");
                    }
                }
            }
        }

        internal static bool ShouldRefreshPartyProjectionAfterTownReturn(
            ushort responsePacketType)
        {
            // GIVEUP_GAME is an explicit abandoned-run exit. The party
            // handler commits the departure immediately after town return,
            // so restoring the old roster here would briefly re-form the
            // returning player with members who are still in the dungeon.
            return responsePacketType != 0x002A;
        }

        private async Task<bool> ReturnSelectionToTownAsync(
            EnhancedClientSession session,
            DungeonSelectionContext selection,
            TownProjectionGuard projectionGuard,
            ushort responsePacketType,
            bool sendCommandResponse)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;

            Dungeon.DungeonRunLifecycle.ApplyTownReturnAnchor(
                session.Player,
                selection.ReturnAnchor,
                session.ListenerPort);
            session.Player.UserState = 0x00;
            // 回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            // （与 DungeonTownReturnCoordinator.ReturnAsync 一致，补齐 GIVEUP_GAME/BACK_2_VILLAGE 路径。）
            if (_sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _sessions);

            if (sendCommandResponse)
            {
                await session.SendPacketAsync(
                    BuildReturnToTownSuccessPacket(responsePacketType));
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
            }

            return await ProjectTownReturnCoreAsync(session, projectionGuard);
        }

        // The menu owner already acknowledged a current BACK_2_VILLAGE under
        // the send lock and holds the existing character transition gate.
        // Reuse ordinary selection return projections without fabricating a
        // DungeonSelectionContext or changing the current town anchor.
        internal async Task<bool> ProjectGuardianMenuReturnAsync(
            EnhancedClientSession session, Func<bool> current)
        {
            ArgumentNullException.ThrowIfNull(current);
            var guard = TownProjectionGuard.ForAuthorizedReturn(current);
            if (!CanContinueTownProjection(session, guard)
                || !CanChangeRaidArea(session, session.Player.CurTownId, session.Player.CurAreaId)
                || !GameChannelSpawnPolicy.CanEnterTown(session.ListenerPort, session.Player.CurTownId))
                return false;
            if (!await ProjectTownReturnCoreAsync(session, guard))
                return false;
            return await SendTownAccountStateAsync(session, "guardian-menu-return", guard)
                && CanContinueTownProjection(session, guard);
        }

        private async Task<bool> ProjectTownReturnCoreAsync(
            EnhancedClientSession session, TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;
            await SendTownProjectionPacketAsync(session, projectionGuard, GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!CanContinueTownProjection(session, projectionGuard))
            {
                return false;
            }

            await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
            if (!CanContinueTownProjection(session, projectionGuard))
            {
                return false;
            }
            await RefreshPartyProjectionAfterTownReturnAsync(
                session,
                projectionGuard);
            return CanContinueTownProjection(session, projectionGuard);
        }

        internal async Task ReturnRejectedPartySelectionToTownAsync(
            EnhancedClientSession leader,
            DungeonSelectionContext leaderSelection)
        {
            if (leader?.Player == null
                || leaderSelection == null
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection))
            {
                return;
            }

            await TryFanOutPartySelectionReturnAsync(
                leader,
                leaderSelection,
                responsePacketType: 0);
            if (!leader.Player.IsCurrentDungeonSelection(leaderSelection)
                || !leaderSelection.TryBeginReturn())
            {
                return;
            }

            var guard = TownProjectionGuard.ForSelection(leaderSelection);
            if (!await ReturnSelectionToTownAsync(
                    leader,
                    leaderSelection,
                    guard,
                    responsePacketType: 0,
                    sendCommandResponse: false))
            {
                leaderSelection.CancelReturn();
                return;
            }
            await SendTownAccountStateAsync(
                leader,
                "party-entry-rejected",
                guard);
            if (CanContinueTownProjection(leader, guard))
                leader.Player.CompleteDungeonSelection(leaderSelection);
        }

        internal static bool ShouldFanOutPartySelectionReturn(
            ushort responsePacketType)
            => responsePacketType == 0x0084;

        private async Task TryFanOutPartySelectionReturnAsync(
            EnhancedClientSession leader,
            DungeonSelectionContext leaderSelection,
            ushort responsePacketType)
        {
            if (Environment.GetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP") == "0"
                || leader?.Player == null
                || leaderSelection == null
                || _sessions == null)
            {
                return;
            }

            var cohort = leaderSelection.PartyCohort;
            if (cohort == null
                || cohort.LeaderUserId != leader.Player.UserId
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection))
            {
                return;
            }

            var returned = 0;
            foreach (var participant in cohort.Participants)
            {
                if (participant.UserId == cohort.LeaderUserId)
                    continue;
                if (!_sessions.TryGet(
                        participant.CharacterId, out var follower)
                    || follower?.Player == null
                    || follower.SessionId != participant.SessionId
                    || follower.Player.UserId != participant.UserId
                    || follower.ListenerPort != leader.ListenerPort
                    || follower.TcpClient == null
                    || !follower.TcpClient.Connected
                    || follower.Player.CurrentRun != null)
                {
                    continue;
                }

                var followerSelection =
                    follower.Player.CurrentDungeonSelection;
                if (!follower.Player.IsCurrentDungeonSelection(
                        followerSelection)
                    || !ReferenceEquals(
                        followerSelection.PartyCohort,
                        cohort)
                    || !followerSelection.TryBeginReturn())
                {
                    continue;
                }

                var followerGuard =
                    TownProjectionGuard.ForSelection(followerSelection);
                try
                {
                    if (!await ReturnSelectionToTownAsync(
                            follower,
                            followerSelection,
                            followerGuard,
                            responsePacketType,
                            sendCommandResponse: false))
                    {
                        followerSelection.CancelReturn();
                        continue;
                    }
                    await SendTownAccountStateAsync(
                        follower,
                        "party-leave-dungeon-selection",
                        followerGuard);
                    if (!CanContinueTownProjection(
                            follower,
                            followerGuard))
                    {
                        continue;
                    }
                    follower.Player.CompleteDungeonSelection(
                        followerSelection);
                    returned++;
                }
                catch (Exception ex)
                {
                    if (follower.Player.IsCurrentDungeonSelection(
                            followerSelection))
                    {
                        followerSelection.CancelReturn();
                    }
                    FileLogger.Log(
                        $"[{ProtocolName}] PARTY_SELECTION_RETURN failed: " +
                        $"uid={participant.UserId} " +
                        $"party={cohort.PartyId} " +
                        $"projection={cohort.ProjectionId} " +
                        $"error={ex.Message}");
                }
            }

            FileLogger.Log(
                $"[{ProtocolName}] PARTY_SELECTION_RETURN: " +
                $"leader={leader.Player.CharacterId} " +
                $"party={cohort.PartyId} projection={cohort.ProjectionId} " +
                $"returned={returned}/{cohort.Participants.Count - 1}");
        }

        private async Task RefreshPartyProjectionAfterTownReturnAsync(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            var player = session?.Player;
            var party = player == null
                ? null
                : _partyManager?.GetPartyByUser(player.UserId);
            var snapshot = party == null
                ? null
                : _partyManager.GetPartySnapshot(party.PartyId);
            var packets = BuildTownReturnPartyProjectionPackets(snapshot);
            foreach (var packet in packets)
            {
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
                await SendTownProjectionPacketAsync(session, projectionGuard, packet);
            }

            if (packets.Length > 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RETURN_TO_TOWN party projection: " +
                    $"cid={player.CharacterId} party={snapshot.PartyId} " +
                    $"members={snapshot.Count}");
            }
        }

        internal async Task ProjectDungeonTownPresenceAsync(
            EnhancedClientSession session,
            DfoServer.Game.Dungeon.DungeonRunIdentity runIdentity)
        {
            var projectionGuard = TownProjectionGuard.ForEndedRun(runIdentity);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
        }

        internal static bool ShouldProjectInitialAreaRoster(
            PlayerContext player,
            long movementSequence)
            => movementSequence == 1
               && IsTownArrivalStateEligible(player)
               && !player.DungeonSelectionPending;

        internal static byte[][] BuildTownReturnPartyProjectionPackets(
            Game.Party.Party party)
        {
            if (party == null || party.Count <= 1)
                return Array.Empty<byte[]>();

            return new[]
            {
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.PARTY_INFO,
                    PartyInfoNotiBuilder.Build(party, 0)),
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party)),
            };
        }

        private static byte[] BuildTownAreaProjectionBody(PlayerContext player)
        {
            var list = new List<byte>();
            list.Add(player.CurTownId);
            list.Add(player.CurAreaId);
            list.AddRange(BitConverter.GetBytes(player.CurPosX));
            list.AddRange(BitConverter.GetBytes(player.CurPosY));
            list.Add(player.CurDirection);
            list.Add(player.CurTownId);
            list.Add(player.CurAreaState);
            list.Add(player.CurAreaId);
            return list.ToArray();
        }

        internal static byte[] BuildReturnToTownSuccessPacket(ushort packetType) =>
            GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                CommonPacketBodyBuilder.BuildSuccessAck());

        // ★组队副本收尾 fan-out(⚠️协议/渲染, 待真机)。仅当【队长】+开 DFO_PARTY_DUNGEON_COOP + 队伍>1:
        //   把每个仍在副本内(CurrentRun!=null)的在线队员也拉回其城镇 → 客户端呈现"跟着队长退出"。
        //   非队长放弃(item16 个人退出)不 fan-out, 只回自己, 其余人继续留本。
        private async Task TryFanOutLeaderReturnToTownAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity leaderRunIdentity)
        {
            var leaderGuard = TownProjectionGuard.ForEndedRun(leaderRunIdentity);
            if (!CanContinueTownProjection(leader, leaderGuard)) return;
            if (Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            if (_partyManager == null || _sessions == null || leader?.Player == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = _partyManager.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} → fan-out 跟随退出");
            foreach (var m in party.MembersBySlot())
            {
                if (!CanContinueTownProjection(leader, leaderGuard)) return;
                if (m.UserId == leaderUid) continue;
                _sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected) continue;
                var memberRun = bs.Player.CurrentRun;
                if (memberRun == null
                    || memberRun.PartyDungeonInstanceId
                        != leaderRunIdentity.PartyDungeonInstanceId)
                {
                    continue;
                }
                var memberRunIdentity = memberRun.CaptureIdentity();
                try
                {
                    if (!await ReturnSelfToTownAsync(
                            bs,
                            header,
                            memberRunIdentity,
                            memberRun.TownReturnAnchor,
                            sendCommandResponse: false))
                    {
                        continue;
                    }
                    await SendTownAccountStateAsync(
                        bs,
                        "party-return-village",
                        TownProjectionGuard.ForEndedRun(memberRunIdentity));
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member cid={bs.Player.CharacterId} 跟随退出→城镇");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member uid={m.UserId} 跟随异常: {ex.Message}");
                }
            }
        }

        private async Task<bool> SendTownAccountStateAsync(
            EnhancedClientSession session,
            string reason,
            TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;
            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0 || characterId <= 0)
                return false;

            var summary = _honorLevel.LoadSummary(accountId);
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;
            if (projectionGuard.AuthorizedReturnCurrent != null)
            {
                async Task SendCurrentAsync(byte[] packet)
                {
                    if (!await TrySendTownProjectionPacketAsync(session, projectionGuard, packet))
                        throw new OperationCanceledException("Town return source expired before write.");
                }
                var sent = await UserInfoBroadcastService.SendSubtype0Async(
                    session.Player, accountId,
                    body => SendCurrentAsync(GamePacketEnvelopeBuilder.Build(0,
                        (ushort)NotiPacketTypeA21.USERINFO, body)),
                    _characterRepository, _subtype0Repository, _honorLevel,
                    $"{reason} subtype0", summary);
                if (!sent || !CanContinueTownProjection(session, projectionGuard))
                    return false;
                try
                {
                    await _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary, SendCurrentAsync);
                }
                catch (OperationCanceledException) when (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
                return CanContinueTownProjection(session, projectionGuard);
            }
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0",
                summary);
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;

            await _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
            return CanContinueTownProjection(session, projectionGuard);
        }

        private static bool CanContinueTownProjection(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            if (projectionGuard.ClientTransitionCurrent != null
                && !projectionGuard.ClientTransitionCurrent())
                return false;
            if (projectionGuard.AuthorizedReturnCurrent != null)
                return projectionGuard.AuthorizedReturnCurrent();

            if (projectionGuard.Selection != null)
            {
                return projectionGuard.Selection.IsReturning
                    && session?.Player?.IsCurrentDungeonSelection(
                        projectionGuard.Selection) == true;
            }

            return !projectionGuard.EndedRun.IsValid
                || Dungeon.DungeonRunLifecycle.CanProjectTownState(
                    session,
                    projectionGuard.EndedRun);
        }

        private static async Task SendTownProjectionPacketAsync(
            EnhancedClientSession session, TownProjectionGuard guard, byte[] packet)
        {
            await TrySendTownProjectionPacketAsync(session, guard, packet);
        }

        private static async Task<bool> TrySendTownProjectionPacketAsync(
            EnhancedClientSession session, TownProjectionGuard guard, byte[] packet)
        {
            if (guard.AuthorizedReturnCurrent == null && guard.ClientTransitionCurrent == null)
            {
                await session.SendPacketAsync(packet);
                return true;
            }
            var prepared = false;
            await session.SendPreparedPacketBatchAsync(() =>
            {
                if (!CanContinueTownProjection(session, guard))
                    return Array.Empty<byte[]>();
                prepared = true;
                return new[] { packet };
            }, () => CanContinueTownProjection(session, guard));
            return prepared;
        }

        internal static byte[] BuildUserLeavePacket(ushort userId)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0006,
                TownAreaNotificationBuilder.BuildUserLeave(userId));

        internal static bool IsTownArrivalStateEligible(
            PlayerContext player)
            => player != null
               && player.TownPresenceReady
               && player.CharacterId > 0
               && player.CurrentRun == null
               && player.UserState == 0x00;

        internal static bool ShouldNotifyRaidTownLoaded(
            int listenerPort,
            PlayerContext player)
            => GameNetworkConfig.IsRaidListener(listenerPort)
               && IsTownArrivalStateEligible(player)
               && !player.DungeonSelectionPending
               && player.CurTownId == GameChannelSpawnPolicy.RaidTownId;

        internal static byte[] BuildRaidUserFinishLoadPacket()
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.RAID_USER_FINISH_LOAD,
                Array.Empty<byte>());

        internal static bool IsCompletedRaidTownReturn(
            int listenerPort,
            PlayerContext player,
            Guid sessionId,
            RaidSnapshot raid)
        {
            if (!ShouldNotifyRaidTownLoaded(listenerPort, player)
                || player.CurrentDungeonSelection != null
                || player.DungeonSelectionPending
                || raid == null
                || raid.PhaseIndex != 0
                || raid.State != 5)
            {
                return false;
            }

            foreach (var member in raid.Members)
            {
                if (member.UserId == player.UserId
                    && member.CharacterId == player.CharacterId
                    && member.SessionId == sessionId)
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<bool> TryFinishRaidTownReturnAsync(
            EnhancedClientSession session,
            ushort packetType)
        {
            if (packetType != (ushort)CmdPacketTypeA21.GIVEUP_GAME
                || _raidManager == null
                || session?.Player == null
                || !_raidManager.TryGetCompletedTownReturn(
                    session.Player.UserId, session.SessionId, out var raid)
                || !IsCompletedRaidTownReturn(
                    session.ListenerPort, session.Player, session.SessionId, raid))
            {
                return false;
            }

            var player = session.Player;
            var generation = player.CurrentDungeonRunGeneration;
            var guard = TownProjectionGuard.ForAuthorizedReturn(Current);
            if (!Current())
                return true;
            await session.SendPacketAsync(BuildReturnToTownSuccessPacket(packetType));
            if (!Current())
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(player)));
            if (!Current())
                return true;
            await SetUserAreaCoreAsync(session, BuildTownAreaProjectionBody(player), guard);
            if (!Current())
                return true;
            await RefreshPartyProjectionAfterTownReturnAsync(session, guard);
            return true;

            bool Current()
                => ReferenceEquals(player, session.Player)
                   && generation == player.CurrentDungeonRunGeneration
                   && _raidManager.TryGetCompletedTownReturn(
                       player.UserId, session.SessionId, out var currentRaid)
                   && currentRaid.InstanceId == raid.InstanceId
                   && IsCompletedRaidTownReturn(
                       session.ListenerPort, player, session.SessionId, currentRaid);
        }
    }
}
