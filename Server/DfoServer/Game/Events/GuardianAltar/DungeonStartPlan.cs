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
        ushort MonsterCode, byte Level, byte MonsterType);

    internal sealed record GuardianAltarUnitDefinition(byte Slot, int PrototypeId,
        int EnergyCostFixed, int CooldownMilliseconds, string CooldownGroup);

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
            IEnumerable<GuardianAltarUnitDefinition> units = null)
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
            Units = Array.AsReadOnly((units ?? Array.Empty<GuardianAltarUnitDefinition>()).ToArray());
            Phases = Array.AsReadOnly(phases.ToArray());
            Centers = Array.AsReadOnly(centers.ToArray());
            if (Phases.Count == 0 || Centers.Count == 0 || Centers.Count > ushort.MaxValue
                || Centers.Any(x => x.MonsterCode == 0 || x.Level == 0 || x.MonsterType > 3))
                throw new InvalidOperationException("Guardian start plan has no valid timeline or learned center.");
            for (var i = 0; i < Phases.Count; i++)
            {
                var phase = Phases[i];
                // A21 reads an Int32 summary index but its phase consumer uses
                // only the low byte (0x115C22D -> 0x1463E90). Do not wrap 256.
                if (phase.PhaseIndex != i || phase.PhaseIndex > byte.MaxValue
                    || phase.PhaseType < 0 || phase.PhaseType > 2
                    || phase.StartsAtMilliseconds < 0 || phase.StartsAtMilliseconds >= durationMilliseconds
                    || (i == 0 && phase.StartsAtMilliseconds != 0)
                    || (i > 0 && phase.StartsAtMilliseconds <= Phases[i - 1].StartsAtMilliseconds))
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
    }
}
