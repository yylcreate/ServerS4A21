using DfoServer.GameWorld;
using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.Wedding
{
    internal sealed record WeddingRoomItem(int ItemId, int Count);
    internal sealed class WeddingRoomState
    {
        internal long PairId { get; init; }
        internal int[] Installed { get; } = new int[28];
        internal WeddingRoomItem[] Cargo { get; } = new WeddingRoomItem[40];
        internal int TreeId { get; set; }
        internal long TreeExpires { get; set; }
        internal int TreeGrowth { get; set; }
        internal int WaterDay { get; set; }
        internal byte WaterCount { get; set; }
        internal long HarvestSequence { get; set; }
        internal long Version { get; set; }
        internal void Settle(long now)
        {
            if (TreeId != 0 && now >= TreeExpires) { TreeId = 0; TreeExpires = 0; TreeGrowth = 0; }
            var date = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(8));
            int day = date.Year * 10000 + date.Month * 100 + date.Day;
            if (WaterDay != day) { WaterDay = day; WaterCount = 0; }
        }
    }
    internal enum WeddingRoomOperation { Query, Move, Delete, Plant, Water, Harvest, Remove, Buy }
    internal sealed record WeddingRoomCommand(WeddingRoomOperation Operation, int Slot = -1, int ItemId = 0, int Count = 0,
        byte From = 0, int FromSlot = -1, int FromItem = 0, byte To = 0, int ToSlot = -1, int ToItem = 0,
        int Gold = 0, int Love = 0, WeddingItemCost[] Materials = null);
    internal sealed record WeddingRoomResult(int Error, WeddingRoomState Room = null, bool AssetsChanged = false,
        WeddingItemCost Reward = null, int PurchasedSlot = -1, InventoryMutationResult BuyMutation = null,
        WeddingShopWallet ShopWallet = null, int[] MailedCharacters = null)
    { internal bool Success => Error == 0; }
    internal sealed record WeddingShopWallet(int Gold, int Sp, int Cera);
}
