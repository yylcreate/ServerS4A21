using System;
using System.IO;
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
        bool IsClosed);

    // Temporary level-zero natural energy only. The common Room owns loading;
    // binding its released identity does not start a second loading state
    // machine or a clock. Explicit resume starts this runtime's active elapsed
    // anchor. The caller supplies the existing monotonic process clock.
    internal sealed class GuardianAltarEnergyRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly int _scale;
        private readonly int _capacityFixed;
        private readonly int _regenerationFixedPerSecond;
        private readonly long _maximumActiveMilliseconds;
        private DungeonParticipantRoomIdentity _identity;
        private long _loadingGeneration;
        private bool _bound;
        private bool _closed;
        private bool _paused = true;
        private long _version = 1;
        private long _lastObservedMilliseconds = -1;
        private long _resumeAtMilliseconds;
        private long _settledActiveMilliseconds;

        internal GuardianAltarEnergyRuntime(GuardianAltarEnergyConfigFile config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (!config.IsValid || config.Scale <= 0)
                throw new InvalidDataException("Guardian natural energy definition is invalid: " + config.InvalidReason);
            var initial = config.Levels.SingleOrDefault(x => x.Level == 0);
            if (initial == null || initial.CapacityFixed <= 0 || initial.RegenerationFixedPerTick <= 0)
                throw new InvalidDataException("Guardian natural energy has no valid level-zero row.");
            _scale = config.Scale;
            _capacityFixed = initial.CapacityFixed;
            _regenerationFixedPerSecond = initial.RegenerationFixedPerTick;
            // Saturation is sufficient for this non-spending level-zero batch.
            // Bound elapsed before addition/multiplication, even for Int64 clock
            // spans; retain every sub-second remainder until capacity is full.
            var secondsToCapacity = ((long)_capacityFixed + _regenerationFixedPerSecond - 1)
                / _regenerationFixedPerSecond;
            _maximumActiveMilliseconds = checked(secondsToCapacity * 1000);
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
                    snapshot = CaptureLocked(_lastObservedMilliseconds);
                    return false;
                }

                var now = ObserveClockLocked(monotonicMilliseconds);
                if (_paused != paused)
                {
                    var nextVersion = checked(_version + 1);
                    if (paused)
                        _settledActiveMilliseconds = ActiveMillisecondsLocked(now);
                    else
                        _resumeAtMilliseconds = now;
                    _paused = paused;
                    _version = nextVersion;
                }
                snapshot = CaptureLocked(now);
                return true;
            }
        }

        internal GuardianAltarEnergySnapshot ReadSnapshot(long monotonicMilliseconds)
        {
            if (monotonicMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds),
                    "Guardian natural energy requires a nonnegative monotonic clock.");
            lock (_syncRoot)
            {
                var now = _closed ? _lastObservedMilliseconds : ObserveClockLocked(monotonicMilliseconds);
                return CaptureLocked(now);
            }
        }

        // Projection fence only: naturally changing energy does not expire an
        // ACK, while binding, pause transitions and close cannot be conflated.
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
                _settledActiveMilliseconds = ActiveMillisecondsLocked(_lastObservedMilliseconds);
                _paused = true;
                _closed = true;
                _version = nextVersion;
            }
        }

        private long ObserveClockLocked(long monotonicMilliseconds)
        {
            if (monotonicMilliseconds > _lastObservedMilliseconds)
                _lastObservedMilliseconds = monotonicMilliseconds;
            // A backward clock never moves a resume anchor backwards or gives
            // back energy. Future time advances only past this high-water mark.
            return _lastObservedMilliseconds;
        }

        private long ActiveMillisecondsLocked(long now)
        {
            if (_paused || now < _resumeAtMilliseconds)
                return _settledActiveMilliseconds;
            var remaining = _maximumActiveMilliseconds - _settledActiveMilliseconds;
            var elapsed = now - _resumeAtMilliseconds;
            return elapsed >= remaining
                ? _maximumActiveMilliseconds
                : _settledActiveMilliseconds + elapsed;
        }

        private GuardianAltarEnergySnapshot CaptureLocked(long now)
        {
            var activeMilliseconds = ActiveMillisecondsLocked(now);
            var naturalEnergy = activeMilliseconds / 1000 * _regenerationFixedPerSecond;
            var energyFixed = (int)Math.Min(_capacityFixed, naturalEnergy);
            return new GuardianAltarEnergySnapshot(_identity, _loadingGeneration, _version,
                _bound && !_closed, _paused, energyFixed, _scale, 0, _closed);
        }
    }
}
