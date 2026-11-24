using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class FatiguePotionActionRequest
    {
        internal short SlotIndex { get; set; }
    }

    internal static class FatiguePotionActionRequestParser
    {
        // A21 客户端动作 id：[add fatigue]（客户端动作名→id 链 0x293667F；
        // 2026-11-15 实机抓包偏移 7 实测值，证据见 .analysis/plans/fatigue-potion-fix-plan.md）。
        internal const int AddFatigueAction = 0x3A;

        // 与 TransformationActionRequestParser 同构的 19 字节动作布局：
        // slot:i16, list:u8(=Main), reserved:i32(=0), action:i32, p1:i32, p2:i32。
        // p1/p2 客户端语义未知，不读取。
        internal static bool TryParse(byte[] body, out FatiguePotionActionRequest request)
        {
            request = null;
            if (body == null
                || body.Length != 19
                || body[2] != 0)
            {
                return false;
            }

            var slot = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(0, 2));
            if (slot < 0
                || BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(3, 4)) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(7, 4)) != AddFatigueAction)
            {
                return false;
            }

            request = new FatiguePotionActionRequest { SlotIndex = slot };
            return true;
        }
    }
}
