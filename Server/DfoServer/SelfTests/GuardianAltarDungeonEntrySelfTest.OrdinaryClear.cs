using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        // This composes the real current-baseline progress, owned inventory,
        // durable effect and Coordinator. The canonical resource combat loop
        // establishes success; no fabricated commit result or terminal fact.
        // These server assertions do not execute the client's scene/UI code.
        private static async Task CheckOrdinaryClearAsync(Action<string, bool> check,
            GuardianAltarMenuDefinition definition)
        {
            using (var fixture = new SurvivalDeathFixture(definition, ordinaryMode: true))
            {
                await fixture.EnterAsync(851, 0);
                var initial = fixture.Before;
                check("ordinary first-clear fixture keeps the real fresh one-stage and zero-star menu",
                    fixture.Plan.DungeonId == 3511 && !fixture.Plan.IsSurvival && fixture.Plan.Difficulty == 0
                    && initial.Stages.SequenceEqual(new[] { new GuardianAltarStage(3511, -1) })
                    && initial.Header.StageStars == 0 && initial.Header.AvailableStars == 0
                    && initial.Header.FreeTickets == 3 && initial.Header.MenuHintFlag == 0);
                await fixture.CompleteOrdinaryAsync();
                var fact = fixture.Combat.TerminalFact;
                var first = fixture.Combat.Presentation.CommitResult;
                var firstProgress = fixture.ReadProgress();
                check("ordinary first clear uses canonical enemy success and commits original outbox before results",
                    fact is { Result: GuardianAltarTerminalResult.Succeeded }
                    && first != null && first.FirstClear && first.StarsGained == 3
                    && fixture.Effects.Outbox.Get(fixture.EffectId).State == DungeonPersistentEffectState.Committed
                    && fixture.Run.Effects.GetState(fixture.EffectId) == DungeonEffectState.Committed);
                check("ordinary first clear persists next 3512, unit 3000 and skill stars without losing existing slots",
                    firstProgress.Header.FreeTickets == 2 && firstProgress.Header.StageStars == 3
                    && firstProgress.Header.AvailableStars == 3 && firstProgress.Header.MenuHintFlag == 1
                    && firstProgress.Stages.SequenceEqual(new[] {
                        new GuardianAltarStage(3511, 0), new GuardianAltarStage(3512, -1) })
                    && first.NewPurchases.SequenceEqual(new[] { new GuardianAltarPurchase(0, 3000, 1, 0) })
                    && firstProgress.Purchases.Contains(first.NewPurchases.Single())
                    && firstProgress.Slots.SequenceEqual(initial.Slots)
                    && firstProgress.Achievements.SequenceEqual(initial.Achievements));
                check("ordinary first clear projects inventory then CLEAR without rebuilding combat menu",
                    OrdinaryTerminalTypes(fixture).SequenceEqual(new[] {
                        (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST,
                        (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE })
                    && fixture.Combat.Presentation.InventoryProjectionSent
                    && fixture.Combat.Presentation.ClearProjectionSent
                    && !fixture.Combat.Presentation.EplpProjectionSent
                    && OrdinaryClearMatches(fixture, 3, first.NewPurchases)
                    && fixture.ItemCount(2749211) == 4 && fixture.ItemCount(10006059) == 1);
                var firstBody = fixture.Packets.Single(packet => Type(packet)
                    == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE).AsSpan(15);
                check("actual ordinary first clear matches the independent 73-byte 3511 resource golden",
                    firstBody.SequenceEqual(Convert.FromHexString(
                        "00000000000000000000000000B70D00000000000003000000"
                        + "02000000000000001BF3290004000000000000002BAE980001000000"
                        + "0100000000000000B80B00000100000000000000")));
                await CheckOrdinaryDeadlineAsync(fixture, check, "first clear");
                var committedJson = fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson;
                var packetCount = fixture.Packets.Count;
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check("same ordinary terminal retry keeps its durable grants and successful projection checkpoints",
                    fixture.Packets.Count == packetCount && fixture.ItemCount(2749211) == 4
                    && fixture.ItemCount(10006059) == 1
                    && fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson == committedJson
                    && fixture.ReadProgress().Header.FreeTickets == 2);

                await fixture.RepeatOrdinaryAsync();
                await fixture.CompleteOrdinaryAsync();
                var repeat = fixture.Combat.Presentation.CommitResult;
                var repeatProgress = fixture.ReadProgress();
                check("a second real ordinary run grants its items once without new stars or purchase unlocks",
                    repeat != null && !repeat.FirstClear && repeat.StarsGained == 0 && repeat.NewPurchases.Count == 0
                    && repeatProgress.Header.FreeTickets == 1 && repeatProgress.Header.StageStars == 3
                    && repeatProgress.Header.AvailableStars == 3
                    && repeatProgress.Stages.SequenceEqual(firstProgress.Stages)
                    && repeatProgress.Purchases.SequenceEqual(firstProgress.Purchases)
                    && fixture.ItemCount(2749211) == 8 && fixture.ItemCount(10006059) == 2);
                check("repeated ordinary clear follows the same result sequence with an empty unlock array",
                    OrdinaryTerminalTypes(fixture).SequenceEqual(new[] {
                        (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST,
                        (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE })
                    && OrdinaryClearMatches(fixture, 0, Array.Empty<GuardianAltarPurchase>()));
                await CheckOrdinaryDeadlineAsync(fixture, check, "repeated clear");
                var menuStart = fixture.Packets.Count;
                await fixture.ShowOrdinaryMenuAsync();
                var menu = fixture.Packets.Skip(menuStart).SingleOrDefault(packet => packet[0] == 1
                    && Type(packet) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME);
                check("next actual registered START_GAME reads the committed stage, purchase and stars snapshot",
                    menu != null && menu.AsSpan(15).SequenceEqual(
                        GuardianAltarMenuBodyBuilder.Build(fixture.ReadProgress()))
                    && fixture.ReadProgress().Stages.SequenceEqual(firstProgress.Stages)
                    && fixture.ReadProgress().Purchases.SequenceEqual(firstProgress.Purchases)
                    && !fixture.Packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE));
            }

            using (var fixture = new SurvivalDeathFixture(definition, ordinaryMode: true))
            {
                await fixture.EnterAsync(852, 0);
                fixture.FailedOpcode = (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE;
                await fixture.CompleteOrdinaryAsync();
                var fact = fixture.Combat.TerminalFact;
                var saved = fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson;
                check("ordinary CLEAR write failure retains real first-clear grants and pending result authority",
                    fixture.WriteFailures > 0 && fixture.Combat.Presentation.InventoryProjectionSent
                    && !fixture.Combat.Presentation.ClearProjectionSent
                    && !fixture.Combat.Presentation.EplpProjectionSent
                    && fixture.ItemCount(2749211) == 4 && fixture.ReadProgress().Header.FreeTickets == 2
                    && OrdinaryTerminalTypes(fixture).SequenceEqual(new[] { (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST }));
                fixture.FailedOpcode = null;
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check("ordinary CLEAR retry resumes from committed inventory without another grant or menu refresh",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact)
                    && fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson == saved
                    && OrdinaryTerminalTypes(fixture).SequenceEqual(new[] {
                        (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST,
                        (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE })
                    && OrdinaryClearMatches(fixture, 3, fixture.Combat.Presentation.CommitResult.NewPurchases)
                    && fixture.ItemCount(2749211) == 4 && fixture.ItemCount(10006059) == 1
                    && fixture.ReadProgress().Header.FreeTickets == 2);
            }

            using (var fixture = new SurvivalDeathFixture(definition, ordinaryMode: true))
            {
                await fixture.EnterAsync(853, 0);
                fixture.FailedOpcode = (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE;
                await fixture.CompleteOrdinaryAsync();
                var fact = fixture.Combat.TerminalFact;
                var packetCount = fixture.Packets.Count;
                var saved = fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson;
                fixture.FailedOpcode = null;
                fixture.ReplaceLoadingGeneration();
                await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
                check("a displaced loading generation cannot resume the old ordinary CLEAR or rewrite its durable result",
                    ReferenceEquals(fact, fixture.Combat.TerminalFact) && fixture.Packets.Count == packetCount
                    && !fixture.Combat.Presentation.ClearProjectionSent
                    && !fixture.Combat.Presentation.EplpProjectionSent
                    && fixture.Effects.Outbox.Get(fixture.EffectId).ResultJson == saved
                    && fixture.ItemCount(2749211) == 4 && fixture.ReadProgress().Header.FreeTickets == 2
                    && !fixture.Packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE));
            }
        }

        private static ushort[] OrdinaryTerminalTypes(SurvivalDeathFixture fixture)
            => fixture.Packets.Select(Type).Where(type => type == (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST
                || type == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_UPDATE_RIDABLE
                || type == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE
                || type == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE).ToArray();

        private static bool OrdinaryClearMatches(SurvivalDeathFixture fixture, int stars,
            System.Collections.Generic.IReadOnlyList<GuardianAltarPurchase> purchases)
        {
            var packets = fixture.Packets.Where(packet => Type(packet)
                == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE).ToArray();
            if (packets.Length != 1) return false;
            var body = packets[0].AsSpan(15);
            var rewards = fixture.Plan.ClearPlan.Rewards;
            if (body.Length != 37 + rewards.Count * 12 + purchases.Count * 12
                || BinaryPrimitives.ReadInt32LittleEndian(body) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(body[4..]) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(body[8..]) != 0 || body[12] != 0
                || BinaryPrimitives.ReadInt32LittleEndian(body[13..]) != 3511
                || BinaryPrimitives.ReadInt32LittleEndian(body[17..]) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(body[21..]) != stars
                || BinaryPrimitives.ReadUInt32LittleEndian(body[25..]) != rewards.Count)
                return false;
            var offset = 29;
            foreach (var reward in rewards)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(body[offset..]) != 0
                    || BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 4)..]) != reward.ItemId
                    || BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 8)..]) != reward.Count)
                    return false;
                offset += 12;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]) != purchases.Count) return false;
            offset += 4;
            foreach (var purchase in purchases)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(body[offset..]) != purchase.ShopType
                    || BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 4)..]) != purchase.ResourceId
                    || BinaryPrimitives.ReadInt16LittleEndian(body[(offset + 8)..]) != purchase.Level
                    || BinaryPrimitives.ReadInt16LittleEndian(body[(offset + 10)..]) != purchase.Price)
                    return false;
                offset += 12;
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]) == 0 && offset + 4 == body.Length;
        }

        private static async Task CheckOrdinaryDeadlineAsync(SurvivalDeathFixture fixture,
            Action<string, bool> check, string label)
        {
            var presentation = fixture.Combat.Presentation;
            var before = fixture.Packets.Count;
            fixture.Memory.Now = new DateTimeOffset(presentation.EplpDeadlineUtc.AddMilliseconds(-1));
            await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
            check(label + " retains the four-second result window without rebuilding any menu",
                fixture.Packets.Count == before && !presentation.EplpProjectionSent);
            fixture.Memory.Now = new DateTimeOffset(presentation.EplpDeadlineUtc);
            await fixture.Coordinator.TryCompleteAsync(fixture.Session, fixture.Run);
            check(label + " releases result controls at the original deadline with committed tickets",
                fixture.Packets.Count == before + 1 && presentation.EplpProjectionSent && fixture.EplpMatches()
                && OrdinaryTerminalTypes(fixture)[^1] == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE);
        }
    }
}
