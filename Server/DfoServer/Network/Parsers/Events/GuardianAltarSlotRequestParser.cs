using System;
using System.Buffers.Binary;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Parsers.Events
{
    internal static class GuardianAltarSlotRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarSlotRequest request)
        {
            request = default;
            // A21 sub_14A6F30 sends a packed 28B aggregate. The byte after
            // ridable is equip/unequip; it is not the menu slot's Locked byte.
            if (body == null || body.Length != 28)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0)
                    return false;
            var ridable = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            var equip = body[18];
            var type = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(19, 4));
            var slot = body[23];
            var resource = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(24, 4));
            if (ridable <= 0 || equip > 1 || type < 1 || type > 2 || slot > 5 || resource <= 0)
                return false;
            request = new GuardianAltarSlotRequest(ridable, equip != 0, type, slot, resource);
            return true;
        }
    }
}
