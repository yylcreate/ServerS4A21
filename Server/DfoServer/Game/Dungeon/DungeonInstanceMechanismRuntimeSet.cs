using DfoServer.Game.Dungeon.Tournament;
using DfoServer.Game.Dungeon.BloodAltar;
using System;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonInstanceMechanismRuntimeSet
    {
        private readonly object _syncRoot = new object();
        private TournamentDungeonRuntime _tournament;
        private BloodAltarDungeonRuntime _bloodAltar;
        private Events.GuardianAltar.GuardianAltarDungeonStartPlan _guardianAltar;
        private DungeonBossRouteRuntime _bossRoute;
        private AntonAwakeningRewardRuntime _antonAwakeningReward;
        private bool _conditionalBossSpawned;
        private int _conditionalBossCode;

        internal DungeonDynamicActorRegistry DynamicActors { get; } =
            new DungeonDynamicActorRegistry();

        // Frozen entry only; the common Run/Room loading barrier owns loading
        // and ending. Do not introduce a second Guardian run state machine.
        internal Events.GuardianAltar.GuardianAltarDungeonStartPlan GuardianAltar
        {
            get { lock (_syncRoot) return _guardianAltar; }
        }

        internal bool TryAttachGuardianAltar(Events.GuardianAltar.GuardianAltarDungeonStartPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            lock (_syncRoot)
            {
                if (_guardianAltar == null) { _guardianAltar = plan; return true; }
                return ReferenceEquals(_guardianAltar, plan);
            }
        }

        internal bool ConditionalBossSpawned
        {
            get
            {
                lock (_syncRoot)
                    return _conditionalBossSpawned;
            }
        }

        internal int ConditionalBossCode
        {
            get
            {
                lock (_syncRoot)
                    return _conditionalBossCode;
            }
        }

        internal bool TryRegisterConditionalBossSpawn(int bossCode)
        {
            if (bossCode <= 0)
                return false;

            lock (_syncRoot)
            {
                if (_conditionalBossSpawned)
                    return false;

                _conditionalBossSpawned = true;
                _conditionalBossCode = bossCode;
                return true;
            }
        }

        internal void ResetConditionalBossSpawn()
        {
            lock (_syncRoot)
            {
                _conditionalBossSpawned = false;
                _conditionalBossCode = 0;
            }
        }

        internal TournamentDungeonRuntime Tournament
        {
            get
            {
                lock (_syncRoot)
                    return _tournament;
            }
        }

        internal bool TryAttachTournament(TournamentDungeonRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_tournament == null)
                {
                    _tournament = runtime;
                    return true;
                }
                return ReferenceEquals(_tournament, runtime);
            }
        }

        internal BloodAltarDungeonRuntime BloodAltar
        {
            get
            {
                lock (_syncRoot)
                    return _bloodAltar;
            }
        }

        internal bool TryAttachBloodAltar(BloodAltarDungeonRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_bloodAltar == null)
                {
                    _bloodAltar = runtime;
                    return true;
                }
                return ReferenceEquals(_bloodAltar, runtime);
            }
        }

        internal DungeonBossRouteRuntime BossRoute
        {
            get
            {
                lock (_syncRoot)
                    return _bossRoute;
            }
        }

        internal bool TryAttachBossRoute(DungeonBossRouteRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_bossRoute == null)
                {
                    _bossRoute = runtime;
                    return true;
                }
                return ReferenceEquals(_bossRoute, runtime);
            }
        }

        internal AntonAwakeningRewardRuntime AntonAwakeningReward
        {
            get
            {
                lock (_syncRoot)
                    return _antonAwakeningReward;
            }
        }

        internal bool TryAttachAntonAwakeningReward(
            AntonAwakeningRewardRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_syncRoot)
            {
                if (_antonAwakeningReward == null)
                {
                    _antonAwakeningReward = runtime;
                    return true;
                }
                return ReferenceEquals(_antonAwakeningReward, runtime);
            }
        }

        internal void OnInstanceEnding()
        {
            BloodAltarDungeonRuntime bloodAltar;
            lock (_syncRoot)
                bloodAltar = _bloodAltar;

            bloodAltar?.Timers.CancelAll();
        }
    }
}
