using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Events
{
    internal readonly record struct GuardianAltarUpgradeGageCommand(int ClientLevel, int ClientGauge);

    internal readonly record struct GuardianAltarSummonUnitCommand(
        byte Slot, int UnitPrototypeId, int ClientLevel);

    internal static class GuardianAltarCombatRequestParser
    {
        internal static bool TryParseUpgrade(byte[] body, out GuardianAltarUpgradeGageCommand command)
        {
            command = default;
            // A21 sub_E0EE80 and the current live request: fourteen reserved
            // zero bytes, Int32 client level, Int32 client gauge in display units.
            // These client claims never authorize an energy credit or level change.
            if (!HasReservedPrefix(body, 22))
                return false;
            var clientLevel = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            var clientGauge = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18, 4));
            if (clientLevel < 0 || clientGauge < 0)
                return false;
            command = new GuardianAltarUpgradeGageCommand(clientLevel, clientGauge);
            return true;
        }

        internal static bool TryParseSummon(byte[] body, out GuardianAltarSummonUnitCommand command)
        {
            command = default;
            // A21 sub_1460EB0: fourteen reserved zero bytes, slot byte,
            // Int32 stackable unit prototype, Int32 client energy level.
            // Slot ownership and configured bounds belong to the current run owner.
            if (!HasReservedPrefix(body, 23))
                return false;
            var prototypeId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(15, 4));
            var clientLevel = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(19, 4));
            if (prototypeId <= 0 || clientLevel < 0)
                return false;
            command = new GuardianAltarSummonUnitCommand(body[14], prototypeId, clientLevel);
            return true;
        }

        private static bool HasReservedPrefix(byte[] body, int expectedLength)
        {
            if (body == null || body.Length != expectedLength)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0)
                    return false;
            return true;
        }
    }
}
