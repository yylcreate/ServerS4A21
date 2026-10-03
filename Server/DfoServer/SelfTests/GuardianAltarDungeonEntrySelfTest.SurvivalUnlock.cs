using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        private static void CheckSurvivalUnlockEligibility(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var entry = list.GetById(definition.RidableId)
                ?? throw new InvalidDataException("Current Guardian stage-map registration is missing.");
            var catalog = GuardianAltarStageMapFile.Parse(PvfArchiveAccessor.ReadText("stagemap/" + entry.FilePath));
            if (!catalog.IsValid) throw new InvalidDataException(catalog.InvalidReason);
            var ordinary = catalog.Stages.Where(stage => !stage.IsSurvival)
                .OrderBy(stage => stage.AreaId).ThenBy(stage => stage.DistrictId).ThenBy(stage => stage.StageOrder).ToArray();
            var survival = catalog.Stages.Where(stage => stage.IsSurvival)
                .OrderBy(stage => stage.AreaId).ThenBy(stage => stage.StageOrder).Select(stage => stage.DungeonId).ToArray();
            check("the current registered PVF retains twelve ordinary stages and its separate area survival stage",
                ordinary.Length == 12 && survival.Length == 1);
            var fresh = Fresh(definition, 1);
            check("fresh progress and a lone completed final stage do not prove all ordinary prerequisites",
                definition.GetEligibleSurvivalDungeonIds(fresh).Count == 0
                && definition.GetEligibleSurvivalDungeonIds(Copy(fresh,
                    stages: new[] { new GuardianAltarStage(ordinary[^1].DungeonId, 2) })).Count == 0);
            foreach (var highest in new[] { 0, 1, 2 })
            {
                var completed = Copy(fresh, stages: ordinary.Select(stage => new GuardianAltarStage(stage.DungeonId, highest)));
                check("all ordinary highest difficulties " + highest + " qualify by the easy-or-above client rule",
                    definition.GetEligibleSurvivalDungeonIds(completed).SequenceEqual(survival));
            }
            var allEasy = ordinary.Select(stage => new GuardianAltarStage(stage.DungeonId, 0)).ToArray();
            check("a missing or unlocked-but-unplayed ordinary stage cannot be replaced by survival progress",
                definition.GetEligibleSurvivalDungeonIds(Copy(fresh, stages: allEasy.Take(allEasy.Length - 1))).Count == 0
                && definition.GetEligibleSurvivalDungeonIds(Copy(fresh, stages: allEasy.Take(allEasy.Length - 1)
                    .Append(new GuardianAltarStage(allEasy[^1].DungeonId, -1))
                    .Concat(survival.Select(id => new GuardianAltarStage(id, 23))))).Count == 0);
            check("mixed already-cleared difficulties and existing survival rows retain the same earned qualification",
                definition.GetEligibleSurvivalDungeonIds(Copy(fresh, stages: ordinary.Select((stage, index) =>
                    new GuardianAltarStage(stage.DungeonId, index % 3))
                    .Concat(survival.Select(id => new GuardianAltarStage(id, -1))))).SequenceEqual(survival));
            check("invalid ordinary difficulty is rejected rather than interpreted as stronger earned history",
                Throws<InvalidOperationException>(() => definition.GetEligibleSurvivalDungeonIds(Copy(fresh,
                    stages: allEasy.Take(allEasy.Length - 1).Append(new GuardianAltarStage(allEasy[^1].DungeonId, 3))))));
            check("survival unlock leaves the final ordinary frozen NextDungeonIds compatible with existing outbox intents",
                definition.CreateClearProgressPlan(ordinary[^1].DungeonId, 0).NextDungeonIds.Count == 0);
            check("a legally unlocked menu row does not silently enable the unimplemented survival combat contract",
                Throws<InvalidOperationException>(() => definition.CreateStartPlan(survival[0], 0,
                    Copy(fresh, stages: allEasy.Concat(survival.Select(id => new GuardianAltarStage(id, -1)))))));

            // This catalog-only fixture retains typed PVF area boundaries and
            // deliberately sparse IDs. Eligibility cannot rely on adjacency,
            // total stage count, or stages cleared in another area.
            var multiple = GuardianAltarStageMapFile.Parse(
                "[area]\n0\n[district]\n1\n[stage]\n9001 0 0 0\n7001 1 0 0\n[/stage]\n"
                + "[survival]\n9100 9101\n[/survival]\n[/area]\n"
                + "[area]\n1\n[district]\n2\n[stage]\n12001 0 0 0\n8001 1 0 0\n[/stage]\n"
                + "[survival]\n12100\n[/survival]\n[/area]\n");
            if (!multiple.IsValid) throw new InvalidDataException(multiple.InvalidReason);
            var multiDefinition = new GuardianAltarMenuDefinition(definition.DailyRefillTicket, definition.RidableId,
                9001, definition.InitialPurchases, definition.InitialSlots, multiple.Stages.Select(stage => stage.DungeonId),
                shopEntries: definition.ShopEntries, orderedStages: multiple.Stages);
            var multiFresh = Fresh(multiDefinition, 1);
            var firstArea = Copy(multiFresh, stages: new[] { new GuardianAltarStage(9001, 0),
                new GuardianAltarStage(7001, 2), new GuardianAltarStage(12001, -1), new GuardianAltarStage(8001, 0),
                new GuardianAltarStage(12100, 23) });
            check("area prerequisites unlock every configured survival entry only in that completed area",
                multiDefinition.GetEligibleSurvivalDungeonIds(firstArea).SequenceEqual(new[] { 9100, 9101 }));
            var secondArea = Copy(multiFresh, stages: new[] { new GuardianAltarStage(9001, -1),
                new GuardianAltarStage(12001, 0), new GuardianAltarStage(8001, 0) });
            check("another completed area qualifies independently without borrowing missing ordinary stages",
                multiDefinition.GetEligibleSurvivalDungeonIds(secondArea).SequenceEqual(new[] { 12100 }));
            var both = Copy(multiFresh, stages: multiple.Stages.Where(stage => !stage.IsSurvival)
                .Select(stage => new GuardianAltarStage(stage.DungeonId, 0)));
            check("all completed areas retain PVF survival ordering rather than DungeonId adjacency",
                multiDefinition.GetEligibleSurvivalDungeonIds(both).SequenceEqual(new[] { 9100, 9101, 12100 }));
        }
    }
}
