using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonDefinitionSelfTest
    {
        private static void CheckSurvivalRoundDefinitions(Action<string, bool> check)
        {
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH before survival round validation.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var dungeonList = LstFile.Parse(ReadRequired(archive, "dungeon/dungeon.lst"));
            var entry = RegisteredEntry(dungeonList, 3523, "survival DGN");
            var shared = DungeonFile.Parse(ReadRequired(archive, "dungeon/" + entry.FilePath));
            var dungeon = GuardianAltarDungeonFile.Parse(shared);
            if (!dungeon.IsValid) throw new InvalidDataException(dungeon.InvalidReason);
            var mapList = LstFile.Parse(ReadRequired(archive, "map/map.lst"));
            var rounds = new List<GuardianAltarSurvivalRoundPlan>();
            foreach (var mapId in dungeon.SurvivalMapIds)
            {
                var mapEntry = RegisteredEntry(mapList, mapId, "survival MAP");
                var map = GuardianAltarMapFile.Parse(ReadRequired(archive, "map/" + mapEntry.FilePath));
                if (!map.IsValid) throw new InvalidDataException(map.InvalidReason);
                rounds.Add(new GuardianAltarSurvivalRoundPlan(rounds.Count + 1, mapId, rounds.Count,
                    map, SourcePhaseSummaries(map)));
            }
            check("all 99 source MAP references remain ordered rounds rather than unique maps",
                rounds.Count == 99 && rounds.Select(round => round.MapId).SequenceEqual(dungeon.SurvivalMapIds)
                && rounds.Select(round => round.MapId).Distinct().Count() == 15);
            check("survival keeps 291 phases in independent per-round byte-index domains",
                rounds.Sum(round => round.Phases.Count) == 291
                && rounds.All(round => round.Phases[0].PhaseIndex == 0 && round.Phases[0].StartsAtMilliseconds == 0
                    && round.Phases[^1].PhaseIndex <= byte.MaxValue));
            check("survival complete source duration is 4451000ms while the first round is 75000ms",
                rounds.Sum(round => (long)round.DurationMilliseconds) == 4_451_000
                && rounds[0].MapId == 25129 && rounds[0].DurationMilliseconds == 75_000);
            check("resource DGN basis remains one while the frozen legacy round basis is zero-based",
                shared.BasisLevel == 1 && rounds.Select(round => round.MapBasisLevel)
                    .SequenceEqual(Enumerable.Range(0, rounds.Count)));
            check("round source validation preserves read-only phase summaries and the PVF archive",
                rounds.All(round => round.Phases is IList<GuardianAltarPhaseSummary> phases && phases.IsReadOnly)
                && !archive.HasModifications);

            var fixture = GuardianAltarMapFile.Parse(Timeline(Phase(1000, "normal") + Phase(2000, "boss")));
            if (!fixture.IsValid) throw new InvalidDataException(fixture.InvalidReason);
            var summaries = SourcePhaseSummaries(fixture).ToList();
            var frozen = new GuardianAltarSurvivalRoundPlan(2, 100001, 1, fixture, summaries);
            summaries[0] = new GuardianAltarPhaseSummary(0, 0, 2);
            check("round snapshots copy caller summary lists and retain their typed combat MAP",
                frozen.Phases[0].PhaseType == 0 && ReferenceEquals(frozen.CombatMap, fixture));
            check("round summary rejects a phase type or timing inconsistent with its actual MAP",
                SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 1, 0, fixture, summaries))
                && SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 1, 0, fixture,
                    new[] { new GuardianAltarPhaseSummary(0, 0, 0), new GuardianAltarPhaseSummary(1, 999, 2) })));
            check("round admission rejects invalid identity, missing MAP and partial summaries",
                SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(0, 1, 0, fixture, frozen.Phases))
                && SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 0, 0, fixture, frozen.Phases))
                && SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 1, -1, fixture, frozen.Phases))
                && SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 1, 0, null, frozen.Phases))
                && SurvivalRoundRejects(() => new GuardianAltarSurvivalRoundPlan(1, 1, 0, fixture, frozen.Phases.Take(1))));
        }

        private static IReadOnlyList<GuardianAltarPhaseSummary> SourcePhaseSummaries(GuardianAltarMapFile map)
        {
            var rows = new List<GuardianAltarPhaseSummary>();
            var startsAt = 0;
            foreach (var phase in map.Phases)
            {
                var type = phase.Type switch { "normal" => 0, "named" => 1, "boss" => 2, _ => -1 };
                rows.Add(new GuardianAltarPhaseSummary(rows.Count, startsAt, type));
                startsAt = checked(startsAt + phase.DurationMilliseconds);
            }
            return rows.AsReadOnly();
        }

        private static bool SurvivalRoundRejects(Action create)
        {
            try { create(); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
            catch (InvalidOperationException) { return true; }
        }
    }
}
