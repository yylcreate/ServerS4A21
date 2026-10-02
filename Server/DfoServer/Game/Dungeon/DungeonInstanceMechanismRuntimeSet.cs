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
        private Events.GuardianAltar.GuardianAltarEnergyRuntime _guardianAltarEnergy;
        private Events.GuardianAltar.GuardianAltarCombatRuntime _guardianAltarCombat;
        private DungeonBossRouteRuntime _bossRoute;
        private AntonAwakeningRewardRuntime _antonAwakeningReward;
        private bool _conditionalBossSpawned;
        private int _conditionalBossCode;

        internal DungeonDynamicActorRegistry DynamicActors { get; } =
            new DungeonDynamicActorRegistry();

        // The common Run/Room loading barrier still owns loading and ending.
        // Energy is a disposable, per-instance elapsed-time projection.
        internal Events.GuardianAltar.GuardianAltarDungeonStartPlan GuardianAltar
        {
            get { lock (_syncRoot) return _guardianAltar; }
        }

        internal bool TryAttachGuardianAltar(Events.GuardianAltar.GuardianAltarDungeonStartPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            lock (_syncRoot)
            {
                if (_guardianAltar == null)
                {
                    var energy = new Events.GuardianAltar.GuardianAltarEnergyRuntime(plan.Energy);
                    var combat = new Events.GuardianAltar.GuardianAltarCombatRuntime(
                        plan.CombatMap, plan.SummonData, Math.Max(1, plan.Centers.Count));
                    _guardianAltar = plan;
                    _guardianAltarEnergy = energy;
                    _guardianAltarCombat = combat;
                    return true;
                }
                return ReferenceEquals(_guardianAltar, plan);
            }
        }

        internal Events.GuardianAltar.GuardianAltarEnergyRuntime GuardianAltarEnergy
        {
            get { lock (_syncRoot) return _guardianAltarEnergy; }
        }

        internal Events.GuardianAltar.GuardianAltarCombatRuntime GuardianAltarCombat
        {
            get { lock (_syncRoot) return _guardianAltarCombat; }
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
            Events.GuardianAltar.GuardianAltarEnergyRuntime guardianEnergy;
            Events.GuardianAltar.GuardianAltarCombatRuntime guardianCombat;
            lock (_syncRoot)
            {
                bloodAltar = _bloodAltar;
                guardianEnergy = _guardianAltarEnergy;
                guardianCombat = _guardianAltarCombat;
            }

            bloodAltar?.Timers.CancelAll();
            guardianEnergy?.Close();
            guardianCombat?.Close();
        }
    }
}
