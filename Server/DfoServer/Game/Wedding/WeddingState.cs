namespace DfoServer.Game.Wedding
{
    internal enum WeddingStatus : byte { None, Engaged, Reserved, Ceremony, Married }

    internal sealed record WeddingState(
        long PairId, int CharacterId, int PartnerId, WeddingStatus Status,
        byte Ceremony, byte Ring, int Love, long MarriedAt, long StartAt,
        int ChannelId, byte Room, long Version)
    {
        internal static WeddingState Empty(int characterId) => new(0, characterId, 0, WeddingStatus.None, 0, 255, 0, 0, 0, 0, 0, 0);
    }

    internal enum WeddingError
    {
        Success = 0, Missing = -101, Busy = -102, Invalid = -103,
        AlreadyBound = -2, Ineligible = -3, MissingGold = -7, Cooldown = -151,
        CeremonyCooldown = -199,
    }
}
