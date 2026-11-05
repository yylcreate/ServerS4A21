using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Linq;

namespace DfoServer.Game.Wedding
{
    // This is a typed quote from current PVF, never prices submitted by the client.
    internal static class WeddingShopPricing
    {
        // A21 E29720 substitutes the wedding manager's 161AEA0 getter (+6Ch)
        // for this PVF material. It is reciprocal love, not a physical stack.
        internal const int LovePointMaterial = 10002606;
        internal static bool IsWeddingProduct(int itemId) => WeddingRoomCatalog.Get().FurniturePositions.ContainsKey(itemId)
            || WeddingCatalog.Get().RingTickets.Skip(1).Any(x => x.ItemId == itemId);
        internal static bool TryResolve(int itemId, int count, out WeddingRoomCommand command, out bool furniture)
        {
            command = null; furniture = WeddingRoomCatalog.Get().FurniturePositions.ContainsKey(itemId);
            if (!IsWeddingProduct(itemId) || count is < 1 or > short.MaxValue || (!furniture && count != 1)) return false;
            var metadata = ItemMetadataResolver.Resolve(itemId);
            if (metadata == null || metadata.BuyGold < 0 || metadata.BuyCoin != 0) return false;
            // EE6DC0 routes native [couple room item] metadata to its dedicated
            // container. A mismatched PVF type must not create a main-bag ghost.
            if (furniture && (!metadata.IsStackable || !(metadata.StackableType ?? string.Empty).Replace("`", string.Empty).TrimStart()
                .StartsWith("[couple room item]", StringComparison.OrdinalIgnoreCase))) return false;
            try
            {
                int gold = checked(metadata.BuyGold * count), love = 0;
                var materials = metadata.NeedMaterials.Select(x => new WeddingItemCost(x.ItemId, checked(x.Count * count))).ToArray();
                if (materials.Any(x => x.ItemId <= 0 || x.Count <= 0)) return false;
                foreach (var item in materials.Where(x => x.ItemId == LovePointMaterial)) love = checked(love + item.Count);
                command = new(WeddingRoomOperation.Buy, ItemId: itemId, Count: count, Gold: gold, Love: love,
                    Materials: materials.Where(x => x.ItemId != LovePointMaterial).ToArray());
                return gold > 0 || love > 0 || command.Materials.Length > 0;
            }
            catch (OverflowException) { return false; }
        }
    }
}
