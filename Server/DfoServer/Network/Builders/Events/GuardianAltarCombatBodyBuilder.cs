using System;

namespace DfoServer.Network.Builders
{
    internal readonly record struct GuardianAltarSpawnUnitFields(
        short UnitIndex,
        int SummonObjectId,
        bool IsEnemy,
        int MonsterType,
        int MonsterId,
        short Level,
        int RawValue,
        int X,
        int Y,
        int Z);

    internal static class GuardianAltarCombatBodyBuilder
    {
        internal static byte[] BuildRejected() => new byte[] { 0 };

        internal static byte[] BuildUpgradeAcknowledgement(
            bool outerSuccess, int result, int previousLevel, int newLevel, int remainingFixed)
        {
            // A21 sub_1105610 returns before its 16-byte read when the outer
            // success is false. Unknown state failures use that verified branch.
            if (!outerSuccess)
                return BuildRejected();
            ValidateResult(result);
            RequireNonNegative(previousLevel, nameof(previousLevel));
            RequireNonNegative(newLevel, nameof(newLevel));
            RequireNonNegative(remainingFixed, nameof(remainingFixed));
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result);
            writer.WriteInt32(previousLevel);
            writer.WriteInt32(newLevel);
            writer.WriteInt32(remainingFixed);
            return writer.ToArray();
        }

        internal static byte[] BuildSummonAcknowledgement(
            bool outerSuccess, int result, byte slot, int prototypeId, int remainingFixed)
        {
            // A21 sub_1106F00 consumes thirteen bytes after outer success.
            // Keep the real requested stackable prototype in the response;
            // success also dispatches the slot's ordinary client hotkey event.
            if (!outerSuccess)
                return BuildRejected();
            ValidateResult(result);
            if (prototypeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(prototypeId));
            RequireNonNegative(remainingFixed, nameof(remainingFixed));
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result);
            writer.WriteByte(slot);
            writer.WriteInt32(prototypeId);
            writer.WriteInt32(remainingFixed);
            return writer.ToArray();
        }

        internal static byte[] BuildSpawnUnit(GuardianAltarSpawnUnitFields fields)
        {
            if (fields.UnitIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(fields.UnitIndex));
            if (fields.SummonObjectId <= 0)
                throw new ArgumentOutOfRangeException(nameof(fields.SummonObjectId));
            if (fields.MonsterType < 0 || fields.MonsterType > 2)
                throw new ArgumentOutOfRangeException(nameof(fields.MonsterType));
            if (fields.MonsterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(fields.MonsterId));
            if (fields.Level <= 0)
                throw new ArgumentOutOfRangeException(nameof(fields.Level));
            var writer = new GamePacketWriter();
            // A21 sub_114BCC0 reads exactly 38 bytes; its assembly consumes
            // index@0, monster@14, level@18 and coordinates@26/30/34.
            // Preserve the resource's sixth raw value at 22 without assigning
            // it an unverified meaning. Do not copy the legacy 40-byte struct.
            writer.WriteInt16(fields.UnitIndex);
            writer.WriteInt32(fields.SummonObjectId);
            writer.WriteInt32(fields.IsEnemy ? 1 : 0);
            writer.WriteInt32(fields.MonsterType);
            writer.WriteInt32(fields.MonsterId);
            writer.WriteInt16(fields.Level);
            writer.WriteUInt16(0); // the two bytes between level and raw value
            writer.WriteInt32(fields.RawValue);
            writer.WriteInt32(fields.X);
            writer.WriteInt32(fields.Y);
            writer.WriteInt32(fields.Z);
            return writer.ToArray();
        }

        private static void ValidateResult(int result)
        {
            // Only success and the two consumer-confirmed rejection results
            // are exposed here. Their business meanings are command-specific.
            if (result != 0 && result != 2 && result != 3)
                throw new ArgumentOutOfRangeException(nameof(result));
        }

        private static void RequireNonNegative(int value, string parameterName)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
