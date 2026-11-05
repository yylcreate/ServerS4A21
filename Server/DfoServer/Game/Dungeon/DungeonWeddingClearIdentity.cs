namespace DfoServer.Game.Dungeon
{
    internal sealed record DungeonWeddingClearIdentity(long PairId, int PartnerId, bool PartnerInFrozenRoster, bool RecommendedDungeonClear);
}
