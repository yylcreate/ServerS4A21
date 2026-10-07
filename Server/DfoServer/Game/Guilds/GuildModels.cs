namespace DfoServer.Game.Guilds
{
    internal enum GuildResult
    {
        Success, InvalidName, InvalidPromotion, DuplicateName, AlreadyMember,
        MissingCharacter, InsufficientGold, NotFound, NotLeader, NotApplicant,
        LeaderCannotLeave, StaleSession, PersistenceFailed, InvalidRank, InvalidChannel,
        InvalidAmount
    }
    internal sealed record GuildRecord(int Id, string Name, string Promotion, int LeaderId, string Announcement = "",
        int RecommendedServerId = 0, int RecommendedChannelId = 0, uint Points = 0);
    internal sealed record GuildCreateResult(GuildResult Status, GuildRecord Guild = null, int GoldAfter = 0);
    internal sealed record GuildMemberRecord(int CharacterId, int AccountId, string Name,
        ushort Level, byte Job, byte GrowType, bool IsLeader, uint OfflineSeconds, byte MemberRank = 4, string Memo = "")
    {
        internal byte Rank => IsLeader ? (byte)1 : MemberRank;
    }
    internal sealed record GuildRoster(GuildRecord Guild, string LeaderName,
        System.Collections.Generic.IReadOnlyList<GuildMemberRecord> Members,
        int RequestCharacterId = 0, GuildContributionSnapshot GuildContribution = null,
        GuildMemberContributionSnapshot RequesterContribution = null,
        System.Collections.Generic.IReadOnlyList<GuildPurchasedContent> PurchasedContents = null);
    internal sealed record GuildPurchasedContent(byte Category, uint ExpiresAt, uint RemainingUses = 0);
    internal sealed record GuildContributionSnapshot(int Total, int MonthId, int MonthValue);
    internal sealed record GuildMemberContributionSnapshot(int CharacterId, int AccountId,
        GuildContributionSnapshot Contribution);
    internal sealed record GuildDungeonContributionParticipant(int CharacterId, int AccountId);
    internal sealed record GuildMemberContributionApplied(int CharacterId, int AccountId,
        int TotalBefore, int TotalAfter, int MonthBefore, int MonthAfter);
    internal sealed record GuildDungeonContributionApplyResult(int GuildId, int MonthId, int Amount,
        bool Eligible, bool Applied,
        int TotalBefore, int TotalAfter, int MonthBefore, int MonthAfter,
        System.Collections.Generic.IReadOnlyList<GuildMemberContributionApplied> AppliedMembers);
    internal sealed record GuildContributionHistoryRow(System.Guid SourceEventId, int CharacterId,
        int AccountId, string Name, System.DateTimeOffset GrantedAt, int Amount);
    internal sealed record GuildContributionHistory(int GuildId, int CharacterId, int AccountId,
        System.Collections.Generic.IReadOnlyList<GuildContributionHistoryRow> Rows);
    internal sealed record GuildSearchResult(GuildRecord Guild, ushort MemberCount);
    internal sealed record GuildApplication(int CharacterId, string Name, byte Job, byte GrowType,
        byte Level, string Message, uint ElapsedSeconds);
    internal sealed record GuildPendingApplication(GuildRecord Guild, string Message);
}
