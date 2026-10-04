using System;
using System.Buffers.Binary;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Parsers.Events
{
    internal static class GuardianAltarExchangeRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarExchangeRequest request)
        {
            request = default;
            // A21 sender 0x14AC300 explicitly copies 37 packed bytes. Raw byte18
            // is initialized to zero; it is not SET_SLOT's equip operation.
            if (body == null || body.Length != 37 || body[18] != 0)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0) return false;
            var ridable = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            var first = new GuardianAltarExchangeEndpoint(
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(19, 4)), body[23],
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(24, 4)));
            var second = new GuardianAltarExchangeEndpoint(
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(28, 4)), body[32],
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(33, 4)));
            if (ridable <= 0 || first.SlotType < 1 || first.SlotType > 2
                || first.SlotType != second.SlotType || first.SlotIndex > 5 || second.SlotIndex > 5
                || first.SlotIndex == second.SlotIndex || first.ResourceId < 0 || second.ResourceId < 0
                || first.ResourceId == second.ResourceId)
                return false;
            request = new GuardianAltarExchangeRequest(ridable, first, second);
            return true;
        }
    }
}
