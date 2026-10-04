using System;
using System.Buffers.Binary;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Parsers.Events
{
    internal static class GuardianAltarPurchaseRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarPurchaseRequest request)
        {
            request = default;
            // A21 sub_149F4B0: zero-initialized 30B aggregate; ridable/type/id
            // follow its fourteen zero bytes. The two unsigned words are
            // currentLevel+1 and currentLevel+UI count, not price or count.
            if (body == null || body.Length != 30)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0)
                    return false;
            var ridable = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            var type = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18, 4));
            var resource = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(22, 4));
            var start = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(26, 2));
            var target = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(28, 2));
            if (ridable <= 0 || type < 0 || type > 2 || resource <= 0
                || start == 0 || target < start || target > short.MaxValue)
                return false;
            request = new GuardianAltarPurchaseRequest(ridable, type, resource, (short)start, (short)target);
            return true;
        }
    }
}
