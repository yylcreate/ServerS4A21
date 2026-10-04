using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarDungeonPacketBuilder
    {
        internal static byte[] BuildStageInfo(GuardianAltarDungeonStartPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var writer = new GamePacketWriter();
            writer.WriteInt32(plan.DungeonId);
            writer.WriteInt32(plan.Difficulty);
            return writer.ToArray();
        }

        internal static byte[] BuildStartMap(GuardianAltarDungeonStartPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var writer = new GamePacketWriter();
            // A21 consumer 0x115C180: 20-byte header, phaseCount/12-byte
            // summaries, centerCount/13-byte centers. Not ordinary START_MAP.
            writer.WriteInt32(plan.IsSurvival ? 1 : 0);
            writer.WriteInt32(plan.IsSurvival ? plan.SurvivalRounds[0].RoundNumber : 0);
            writer.WriteInt32(plan.MapId);
            writer.WriteInt32(plan.DurationMilliseconds);
            writer.WriteInt32(plan.MapBasisLevel);
            writer.WriteUInt32((uint)plan.Phases.Count);
            foreach (var phase in plan.Phases)
            {
                writer.WriteInt32(phase.PhaseIndex);
                writer.WriteInt32(phase.StartsAtMilliseconds);
                writer.WriteInt32(phase.PhaseType);
            }
            writer.WriteUInt32((uint)plan.Centers.Count);
            for (var i = 0; i < plan.Centers.Count; i++)
            {
                var center = plan.Centers[i];
                writer.WriteInt32(0); // ordinary map source default; no NPC override
                writer.WriteUInt16((ushort)i); // first load starts at 0; registration increments per center
                writer.WriteUInt16(center.MonsterCode);
                writer.WriteByte(center.Level);
                writer.WriteByte(center.MonsterType);
                writer.WriteByte(0); // ordinary center has no parent passive index
                writer.WriteByte(0); // not spawned by a special passive object
                writer.WriteByte(0); // zero-initialized unused extension
            }
            return writer.ToArray();
        }

        internal static byte[] BuildSurvivalRound(GuardianAltarSurvivalRoundPlan round)
        {
            ArgumentNullException.ThrowIfNull(round);
            if (round.RoundNumber <= 1)
                throw new ArgumentOutOfRangeException(nameof(round), "The first survival round requires the entry center projection.");
            var writer = new GamePacketWriter();
            // A21 0x115C3BF..0x115C3FD takes the continuation branch for
            // survival rounds after one. It consumes the header and phases,
            // then returns without reading a center count or loading a MAP.
            writer.WriteInt32(1);
            writer.WriteInt32(round.RoundNumber);
            writer.WriteInt32(round.MapId);
            writer.WriteInt32(round.DurationMilliseconds);
            writer.WriteInt32(round.MapBasisLevel);
            writer.WriteUInt32((uint)round.Phases.Count);
            foreach (var phase in round.Phases)
            {
                writer.WriteInt32(phase.PhaseIndex);
                writer.WriteInt32(phase.StartsAtMilliseconds);
                writer.WriteInt32(phase.PhaseType);
            }
            return writer.ToArray();
        }
    }
}
