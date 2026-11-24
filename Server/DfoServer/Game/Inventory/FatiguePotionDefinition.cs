using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class FatiguePotionUseDefinition
    {
        internal int ItemTemplateId { get; set; }

        internal int RestorePoints { get; set; }
    }

    internal static class FatiguePotionDefinition
    {
        // 客户端 0x2C ACK 处理器（0x11235B0）把错误码 0x5F 映射到官方文案 0x5396
        // 「消耗一定数值(该道具可恢复的疲劳值数值)以上的疲劳值后， 才能使用该道具」。
        internal const byte ErrorInsufficientUsed = 0x5F;

        internal static bool TryResolve(int itemTemplateId, out FatiguePotionUseDefinition definition)
        {
            definition = null;
            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null
                || !string.Equals(
                    StackableItemProvider.NormalizeType(stackable.ActionTypeName),
                    "[add fatigue]",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (stackable.ActionTypeParams.Count != 1 || stackable.ActionTypeParams[0] <= 0)
                return false;

            definition = new FatiguePotionUseDefinition
            {
                ItemTemplateId = itemTemplateId,
                RestorePoints = stackable.ActionTypeParams[0],
            };
            return true;
        }
    }
}
