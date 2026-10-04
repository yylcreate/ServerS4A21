using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Dungeon;
using PvfLib;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal readonly record struct GuardianAltarEnergySnapshot(
        DungeonParticipantRoomIdentity Identity,
        long LoadingGeneration,
        long Version,
        bool IsReady,
        bool Paused,
        int EnergyFixed,
        int Scale,
        int Level,
        bool IsClosed,
        long ActiveMilliseconds);

    internal enum GuardianAltarEnergyOperationResult
    {
        Success,
        NotReady,
        Paused,
        StaleLevel,
        MaximumLevel,
        InsufficientEnergy,
        Cooldown,
    }

    // The common Room owns loading. This instance owns one unsaturated active
    // clock for energy and wave scheduling, plus the current energy balance.
    // Regeneration settles elapsed time at its current level before a command
    // changes the balance or level; past time is never priced at a new rate.
    internal sealed class GuardianAltarEnergyRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly int _scale;
        private readonly GuardianAltarEnergyLevel[] _levels;
        private readonly int[] _upgradeCostsFixed;
        private DungeonParticipantRoomIdentity _identity;
        private long _loadingGeneration;
        private bool _bound;
        private bool _closed;
        private bool _paused = true;
        private long _version = 1;
        private long _lastObservedMilliseconds = -1;
        private long _activeMilliseconds;
        private int _energyFixed;
        private int _level;
        private int _regenerationRemainderMilliseconds;
        private readonly Dictionary<string, long> _unitCooldowns = new(StringComparer.Ordinal);

        internal GuardianAltarEnergyRuntime(GuardianAltarEnergyConfigFile config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (!config.IsValid || config.Scale <= 0)
                throw new InvalidDataException("Guardian natural energy definition is invalid: " + config.InvalidReason);
            _scale = config.Scale;
            _levels = config.Levels.ToArray();
            if (_levels.Length == 0 || !float.IsFinite(config.UpgradeCostPercent)
                || config.UpgradeCostPercent < 0 || config.UpgradeCostPercent > 1)
                throw new InvalidDataException("Guardian energy has no valid levels or upgrade cost.");
            _upgradeCostsFixed = new int[_levels.Length - 1];
            for (var index = 0; index < _levels.Length; index++)
            {
                var row = _levels[index];
                if (row.Level != index || row.CapacityFixed <= 0 || row.RegenerationFixedPerTick <= 0)
                    throw new InvalidDataException("Guardian energy levels are inconsistent.");
                if (index < _upgradeCostsFixed.Length)
                {
                    try
                    {
                        // The resource percentage uses float arithmetic: first
                        // truncate display units, then convert to fixed units.
                        _upgradeCostsFixed[index] = checked((int)(row.Capacity * config.UpgradeCostPercent) * _scale);
                    }
                    catch (OverflowException exception)
                    {
                        throw new InvalidDataException("Guardian energy upgrade cost overflows fixed units.", exception);
                    }
                }
            }
        }

        internal bool BindLoadingRelease(DungeonParticipantRoomIdentity identity, long loadingGeneration)
        {
            lock (_syncRoot)
            {
                if (_closed || !identity.IsValid || loadingGeneration <= 0)
                    return false;
                if (_bound)
                    return _identity.Equals(identity) && _loadingGeneration == loadingGeneration;
                _identity = identity;
                _loadingGeneration = loadingGeneration;
                _bound = true;
                return true;
            }
        }

        internal bool TrySetPaused(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            bool paused, long monotonicMilliseconds, out GuardianAltarEnergySnapshot snapshot)
        {
            lock (_syncRoot)
            {
                if (!_bound || _closed || monotonicMilliseconds < 0
                    || !_identity.Equals(identity) || _loadingGeneration != loadingGeneration)
                {
                    snapshot = CaptureLocked();
                    return false;
                }

                SettleClockLocked(monotonicMilliseconds);
                if (_paused != paused)
                {
                    var nextVersion = checked(_version + 1);
                    _paused = paused;
                    _version = nextVersion;
                }
                snapshot = CaptureLocked();
                return true;
            }
        }

        internal bool TryUpgrade(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            int expectedLevel, long monotonicMilliseconds, out GuardianAltarEnergySnapshot snapshot,
            out GuardianAltarEnergyOperationResult result)
        {
            lock (_syncRoot)
            {
                result = CheckOperationLocked(identity, loadingGeneration, expectedLevel, monotonicMilliseconds);
                if (result == GuardianAltarEnergyOperationResult.Success)
                {
                    if (_level == _levels.Length - 1)
                        result = GuardianAltarEnergyOperationResult.MaximumLevel;
                    else if (_energyFixed < _upgradeCostsFixed[_level])
                        result = GuardianAltarEnergyOperationResult.InsufficientEnergy;
                    else
                    {
                        var nextVersion = checked(_version + 1);
                        _energyFixed -= _upgradeCostsFixed[_level];
                        _level++;
                        _energyFixed = Math.Min(_energyFixed, _levels[_level].CapacityFixed);
                        if (_energyFixed == _levels[_level].CapacityFixed)
                            _regenerationRemainderMilliseconds = 0;
                        _version = nextVersion;
                    }
                }
                snapshot = CaptureLocked();
                return result == GuardianAltarEnergyOperationResult.Success;
            }
        }

        internal bool TrySpend(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            int expectedLevel, int costFixed, long monotonicMilliseconds,
            out GuardianAltarEnergySnapshot snapshot, out GuardianAltarEnergyOperationResult result)
        {
            if (costFixed < 0)
                throw new ArgumentOutOfRangeException(nameof(costFixed));
            lock (_syncRoot)
            {
                result = CheckOperationLocked(identity, loadingGeneration, expectedLevel, monotonicMilliseconds);
                if (result == GuardianAltarEnergyOperationResult.Success)
                {
                    if (_energyFixed < costFixed)
                        result = GuardianAltarEnergyOperationResult.InsufficientEnergy;
                    else
                    {
                        var nextVersion = checked(_version + 1);
                        _energyFixed -= costFixed;
                        _version = nextVersion;
                    }
                }
                snapshot = CaptureLocked();
                return result == GuardianAltarEnergyOperationResult.Success;
            }
        }

        internal GuardianAltarEnergySnapshot ReadSnapshot(long monotonicMilliseconds)
        {
            if (monotonicMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds),
                    "Guardian natural energy requires a nonnegative monotonic clock.");
            lock (_syncRoot)
            {
                if (!_closed)
                    SettleClockLocked(monotonicMilliseconds);
                return CaptureLocked();
            }
        }

        // Virtual activity items never enter the online Inventory. Their
        // cooldown and energy spend share the activity's authoritative lock
        // and active clock, so duplicate requests cannot spend or summon twice.
        internal bool TrySummon(DungeonParticipantRoomIdentity identity, long loadingGeneration,
            int expectedLevel, GuardianAltarUnitDefinition unit, long monotonicMilliseconds,
            out GuardianAltarEnergySnapshot snapshot, out GuardianAltarEnergyOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(unit);
            if (unit.EnergyCostFixed < 0 || unit.CooldownMilliseconds <= 0
                || string.IsNullOrWhiteSpace(unit.CooldownGroup))
                throw new ArgumentException("Guardian unit cost or cooldown is invalid.", nameof(unit));
            lock (_syncRoot)
            {
                result = CheckOperationLocked(identity, loadingGeneration, expectedLevel, monotonicMilliseconds);
                if (result == GuardianAltarEnergyOperationResult.Success)
                {
                    if (_unitCooldowns.TryGetValue(unit.CooldownGroup, out var deadline)
                        && _activeMilliseconds < deadline)
                        result = GuardianAltarEnergyOperationResult.Cooldown;
                    else if (_energyFixed < unit.EnergyCostFixed)
                        result = GuardianAltarEnergyOperationResult.InsufficientEnergy;
                    else
                    {
                        var nextVersion = checked(_version + 1);
                        var nextDeadline = checked(_activeMilliseconds + unit.CooldownMilliseconds);
                        _energyFixed -= unit.EnergyCostFixed;
                        _unitCooldowns[unit.CooldownGroup] = nextDeadline;
                        _version = nextVersion;
                    }
                }
                snapshot = CaptureLocked();
                return result == GuardianAltarEnergyOperationResult.Success;
            }
        }

        // Natural regeneration does not expire an ACK. Pause, spend, upgrade
        // and close do, so a previous command cannot project a newer balance.
        internal bool IsCurrent(GuardianAltarEnergySnapshot snapshot)
        {
            lock (_syncRoot)
                return _bound && !_closed && snapshot.IsReady && !snapshot.IsClosed
                    && snapshot.Version == _version && snapshot.Paused == _paused
                    && snapshot.Identity.Equals(_identity)
                    && snapshot.LoadingGeneration == _loadingGeneration;
        }

        internal void Close()
        {
            lock (_syncRoot)
            {
                if (_closed)
                    return;
                var nextVersion = checked(_version + 1);
                _paused = true;
                _closed = true;
                _version = nextVersion;
            }
        }

        private GuardianAltarEnergyOperationResult CheckOperationLocked(
            DungeonParticipantRoomIdentity identity, long loadingGeneration,
            int expectedLevel, long monotonicMilliseconds)
        {
            if (!_bound || _closed || monotonicMilliseconds < 0
                || !_identity.Equals(identity) || _loadingGeneration != loadingGeneration)
                return GuardianAltarEnergyOperationResult.NotReady;
            SettleClockLocked(monotonicMilliseconds);
            if (_paused)
                return GuardianAltarEnergyOperationResult.Paused;
            return expectedLevel == _level
                ? GuardianAltarEnergyOperationResult.Success
                : GuardianAltarEnergyOperationResult.StaleLevel;
        }

        private void SettleClockLocked(long monotonicMilliseconds)
        {
            if (monotonicMilliseconds <= _lastObservedMilliseconds)
                return;
            var previous = _lastObservedMilliseconds;
            _lastObservedMilliseconds = monotonicMilliseconds;
            if (!_bound || _paused || previous < 0)
                return;
            var elapsed = monotonicMilliseconds - previous;
            var remaining = long.MaxValue - _activeMilliseconds;
            _activeMilliseconds = elapsed >= remaining
                ? long.MaxValue
                : _activeMilliseconds + elapsed;

            var row = _levels[_level];
            if (_energyFixed == row.CapacityFixed)
            {
                _regenerationRemainderMilliseconds = 0;
                return;
            }
            var remainder = (int)(elapsed % 1000) + _regenerationRemainderMilliseconds;
            var wholeSeconds = elapsed / 1000 + remainder / 1000;
            var deficit = (long)row.CapacityFixed - _energyFixed;
            var secondsToCapacity = (deficit + row.RegenerationFixedPerTick - 1) / row.RegenerationFixedPerTick;
            if (wholeSeconds >= secondsToCapacity)
            {
                _energyFixed = row.CapacityFixed;
                // Overflow, including fractional time while full, never becomes
                // credit available after a later summon or upgrade.
                _regenerationRemainderMilliseconds = 0;
            }
            else
            {
                _energyFixed += (int)(wholeSeconds * row.RegenerationFixedPerTick);
                _regenerationRemainderMilliseconds = remainder % 1000;
            }
        }

        private GuardianAltarEnergySnapshot CaptureLocked()
        {
            return new GuardianAltarEnergySnapshot(_identity, _loadingGeneration, _version,
                _bound && !_closed, _paused, _energyFixed, _scale, _level, _closed, _activeMilliseconds);
        }
    }
}
