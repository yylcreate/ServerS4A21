using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.GuardianAltar
{
    internal sealed record GuardianAltarPhaseSummary(
        int PhaseIndex, int StartsAtMilliseconds, int PhaseType);

    // The current ordinary MAP entries use the verified zero defaults of
    // STAssignMonster/_mapMonster. Conditional/NPC centers are not admitted by
    // this batch; never reinterpret drop counts as parent-object fields.
    internal sealed record GuardianAltarCenterSpawn(
        ushort MonsterCode, byte Level, byte MonsterType, bool IsFailureCenter = false);

    internal sealed record GuardianAltarUnitDefinition(byte Slot, int PrototypeId,
        int EnergyCostFixed, int CooldownMilliseconds, string CooldownGroup);

    internal sealed class GuardianAltarSurvivalRoundPlan
    {
        internal GuardianAltarSurvivalRoundPlan(int roundNumber, int mapId, int mapBasisLevel,
            PvfLib.GuardianAltarMapFile combatMap, IEnumerable<GuardianAltarPhaseSummary> phases)
        {
            if (roundNumber <= 0 || roundNumber > short.MaxValue || mapId <= 0 || mapBasisLevel < 0)
                throw new ArgumentOutOfRangeException(nameof(roundNumber));
            if (combatMap == null || !combatMap.IsValid)
                throw new InvalidOperationException("Guardian survival round requires its valid frozen MAP.");
            RoundNumber = roundNumber;
            MapId = mapId;
            MapBasisLevel = mapBasisLevel;
            CombatMap = combatMap;
            DurationMilliseconds = combatMap.TotalDurationMilliseconds;
            Phases = Array.AsReadOnly(phases.ToArray());
            GuardianAltarDungeonStartPlan.ValidatePhases(Phases, DurationMilliseconds);
            if (Phases.Count != combatMap.Phases.Count)
                throw new InvalidOperationException("Guardian survival round summary does not match its MAP.");
            var startsAt = 0;
            for (var index = 0; index < Phases.Count; index++)
            {
                var source = combatMap.Phases[index];
                var type = source.Type switch { "normal" => 0, "named" => 1, "boss" => 2, _ => -1 };
                if (Phases[index].StartsAtMilliseconds != startsAt || Phases[index].PhaseType != type)
                    throw new InvalidOperationException("Guardian survival phase summary differs from the frozen MAP.");
                startsAt = checked(startsAt + source.DurationMilliseconds);
            }
        }

        internal int RoundNumber { get; }
        internal int MapId { get; }
        internal int MapBasisLevel { get; }
        internal int DurationMilliseconds { get; }
        internal IReadOnlyList<GuardianAltarPhaseSummary> Phases { get; }
        internal PvfLib.GuardianAltarMapFile CombatMap { get; }
    }

    internal sealed class GuardianAltarDungeonStartPlan
    {
        internal GuardianAltarDungeonStartPlan(int dungeonId, byte difficulty,
            int mapId, int mapBasisLevel, int durationMilliseconds,
            IEnumerable<GuardianAltarPhaseSummary> phases,
            IEnumerable<GuardianAltarCenterSpawn> centers,
            GuardianAltarProgressSnapshot progress,
            PvfLib.GuardianAltarEnergyConfigFile energy = null,
            PvfLib.GuardianAltarMapFile combatMap = null,
            PvfLib.GuardianAltarSummonDataFile summonData = null,
            IEnumerable<GuardianAltarUnitDefinition> units = null,
            GuardianAltarClearCommitPlan clearPlan = null,
            IEnumerable<GuardianAltarSurvivalRoundPlan> survivalRounds = null,
            PvfLib.GuardianAltarClearRewardFile survivalRewards = null)
        {
            if (dungeonId <= 0 || dungeonId > short.MaxValue || difficulty > 2
                || mapId <= 0 || mapBasisLevel < 0 || durationMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            DungeonId = dungeonId;
            Difficulty = difficulty;
            MapId = mapId;
            MapBasisLevel = mapBasisLevel;
            DurationMilliseconds = durationMilliseconds;
            Progress = progress ?? throw new ArgumentNullException(nameof(progress));
            Energy = energy;
            CombatMap = combatMap;
            SummonData = summonData;
            ClearPlan = clearPlan;
            SurvivalRounds = Array.AsReadOnly((survivalRounds
                ?? Array.Empty<GuardianAltarSurvivalRoundPlan>()).ToArray());
            SurvivalRewards = survivalRewards;
            Units = Array.AsReadOnly((units ?? Array.Empty<GuardianAltarUnitDefinition>()).ToArray());
            Phases = Array.AsReadOnly(phases.ToArray());
            Centers = Array.AsReadOnly(centers.ToArray());
            if (Phases.Count == 0 || Centers.Count == 0 || Centers.Count > ushort.MaxValue
                || Centers.Any(x => x.MonsterCode == 0 || x.Level == 0 || x.MonsterType > 3))
                throw new InvalidOperationException("Guardian start plan has no valid timeline or learned center.");
            ValidatePhases(Phases, durationMilliseconds);
            if (IsSurvival)
            {
                if (difficulty != 0 || clearPlan != null || SurvivalRounds.Count > short.MaxValue
                    || SurvivalRounds.Where((round, index) => round == null || round.RoundNumber != index + 1).Any()
                    || SurvivalRounds[0].MapId != mapId || SurvivalRounds[0].MapBasisLevel != mapBasisLevel
                    || !ReferenceEquals(SurvivalRounds[0].CombatMap, combatMap)
                    || !SurvivalRounds[0].Phases.SequenceEqual(Phases)
                    || SurvivalRounds[0].DurationMilliseconds != durationMilliseconds
                    || survivalRewards == null || !survivalRewards.IsValid
                    || survivalRewards.SurvivalRanges.Count == 0
                    || survivalRewards.SurvivalRanges[0].FirstRound != 1
                    || survivalRewards.SurvivalRanges[^1].LastRound != SurvivalRounds.Count)
                    throw new InvalidOperationException("Guardian survival start plan is incomplete or mixes ordinary clear rules.");
            }
            else if (survivalRewards != null)
                throw new InvalidOperationException("Guardian ordinary plan cannot carry survival rewards.");
        }

        internal static void ValidatePhases(IReadOnlyList<GuardianAltarPhaseSummary> phases,
            int durationMilliseconds)
        {
            if (durationMilliseconds <= 0 || phases.Count == 0)
                throw new InvalidOperationException("Guardian phase summary has no valid duration or phase.");
            for (var i = 0; i < phases.Count; i++)
            {
                var phase = phases[i];
                // A21 reads an Int32 summary index but its phase consumer uses
                // only the low byte (0x115C22D -> 0x1463E90). Do not wrap 256.
                if (phase.PhaseIndex != i || phase.PhaseIndex > byte.MaxValue
                    || phase.PhaseType < 0 || phase.PhaseType > 2
                    || phase.StartsAtMilliseconds < 0 || phase.StartsAtMilliseconds >= durationMilliseconds
                    || (i == 0 && phase.StartsAtMilliseconds != 0)
                    || (i > 0 && phase.StartsAtMilliseconds <= phases[i - 1].StartsAtMilliseconds))
                    throw new InvalidOperationException("Guardian start plan phase summary is inconsistent.");
            }
        }

        internal int DungeonId { get; }
        internal byte Difficulty { get; }
        internal int MapId { get; }
        internal int MapBasisLevel { get; }
        internal int DurationMilliseconds { get; }
        internal IReadOnlyList<GuardianAltarPhaseSummary> Phases { get; }
        internal IReadOnlyList<GuardianAltarCenterSpawn> Centers { get; }
        internal GuardianAltarProgressSnapshot Progress { get; }
        internal PvfLib.GuardianAltarEnergyConfigFile Energy { get; }
        internal PvfLib.GuardianAltarMapFile CombatMap { get; }
        internal PvfLib.GuardianAltarSummonDataFile SummonData { get; }
        internal IReadOnlyList<GuardianAltarUnitDefinition> Units { get; }
        internal GuardianAltarClearCommitPlan ClearPlan { get; }
        internal bool IsSurvival => SurvivalRounds.Count != 0;
        internal IReadOnlyList<GuardianAltarSurvivalRoundPlan> SurvivalRounds { get; }
        internal PvfLib.GuardianAltarClearRewardFile SurvivalRewards { get; }
    }
}
