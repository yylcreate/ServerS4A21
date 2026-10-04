using System;

namespace DfoServer.Game.Events.FatigueAcceleration
{
    internal readonly struct FatigueAccelerationEffectProfile
    {
        internal FatigueAccelerationEffectProfile(int experienceBonusPercent,
            int fatigueConsumptionMultiplier)
        {
            if (experienceBonusPercent < 0)
                throw new ArgumentOutOfRangeException(nameof(experienceBonusPercent));
            if (fatigueConsumptionMultiplier < 1)
                throw new ArgumentOutOfRangeException(nameof(fatigueConsumptionMultiplier));
            ExperienceBonusPercent = experienceBonusPercent;
            FatigueConsumptionMultiplier = fatigueConsumptionMultiplier;
        }

        // Server policy for this implementation batch; the current PVF tables
        // define costs/exclusions, not these rates. A future fatigue owner must
        // consume the multiplier and own balance, persistence and actual debit.
        internal static FatigueAccelerationEffectProfile ServerDefault => new(100, 2);
        internal static FatigueAccelerationEffectProfile Inactive => new(0, 1);
        internal int ExperienceBonusPercent { get; }
        internal int FatigueConsumptionMultiplier { get; }
    }

    // Owned by one GameSession. Character selection replaces that owner; no
    // static player dictionary or separate fatigue balance is maintained.
    internal sealed class FatigueAccelerationRuntime
    {
        private readonly object _syncRoot = new();
        private int _characterId;
        private bool _enabled;

        internal bool IsEnabled(int characterId)
        {
            lock (_syncRoot)
                return characterId > 0 && _characterId == characterId && _enabled;
        }

        internal void SetEnabled(int characterId, bool enabled)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            lock (_syncRoot)
            {
                _characterId = characterId;
                _enabled = enabled;
            }
        }
    }
}
