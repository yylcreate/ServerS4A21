using DfoServer.Game.Guilds;
using DfoServer.Infrastructure;
using System;
using System.Linq;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildInfoPacketBuilder
    {
        // DNF.exe 1F8244D3...B20B00DD, 011959B0..0119681B. Optional systems
        // (alliance, attendance/rewards) remain empty/disabled.
        internal static byte[] Build(GuildRoster roster)
        {
            ArgumentNullException.ThrowIfNull(roster);
            var guildContribution = roster.GuildContribution;
            var requester = roster.RequesterContribution;
            var memberContribution = requester?.Contribution;
            if (roster.RequestCharacterId <= 0 || requester?.CharacterId != roster.RequestCharacterId
                || !roster.Members.Any(member => member.CharacterId == requester.CharacterId
                    && member.AccountId == requester.AccountId)
                || guildContribution == null || memberContribution == null
                || guildContribution.Total < 0 || memberContribution.Total < 0
                || guildContribution.MonthValue < 0 || memberContribution.MonthValue < 0
                || !GuildDungeonMonth.IsValid(guildContribution.MonthId)
                || guildContribution.MonthId != memberContribution.MonthId)
                throw new InvalidOperationException("Guild contribution projection requires the current requesting member's snapshot.");
            var w = new GamePacketWriter();
            w.WriteClientDstr(roster.Guild.Name);
            w.WriteByte(1); // Membership-present state, getter 019E9E60.
            w.WriteByte(0);
            w.WriteUInt16(checked((ushort)roster.Members.Count));
            w.WriteByte(1); // Initial guild level; no upgrade feature.
            w.WriteUInt32(0); w.WriteByte(0);
            w.WriteClientDstr(roster.Guild.Announcement);
            w.WriteByte(0);
            // 01195C1E/01195D38: hasAgit creates or releases the entrance state.
            // A durable member roster owns a basic agit (native default type 1).
            w.WriteByte(1);
            w.WriteByte(0);
            w.WriteByte(1); // Guild model level, 0119602F -> 019F5070.
            w.WriteUInt32(0);
            w.WriteClientDstr(""); w.WriteClientDstr("");
            w.WriteUInt16(0);
            w.WriteClientDstr(roster.Guild.Promotion);
            // Exactly 6 * (u32 permission mask + 24-byte NUL-terminated GBK label).
            // Bit 1 controls application approval (00775C69, applicant control +7B0).
            for (int rank = 0; rank < 6; rank++)
            {
                w.WriteUInt32(GuildManagementRules.Permissions(rank));
                var name = ClientTextEncoding.GetBytes(GuildManagementRules.RankName(rank));
                w.WriteBytes(name); w.WriteZeroBytes(24 - name.Length);
            }
            // 011962A1/011962D0: u8 count, then u8 category / u32 expiry /
            // u8 count-mode / u32 remaining uses. MR95's count-mode mapping
            // still requires a client UI check; widths and field order are native.
            var purchased = roster.PurchasedContents ?? Array.Empty<GuildPurchasedContent>();
            if (purchased.Count > byte.MaxValue)
                throw new InvalidOperationException("Guild purchased-content projection exceeds the native count field.");
            w.WriteByte(checked((byte)purchased.Count));
            foreach (var content in purchased)
            {
                w.WriteByte(content.Category);
                w.WriteUInt32(content.ExpiresAt);
                w.WriteByte(content.RemainingUses > 0 ? (byte)1 : (byte)0);
                w.WriteUInt32(content.RemainingUses);
            }
            w.WriteUInt32(0);
            w.WriteByte(0); // Alliance count, 01196350.
            w.WriteUInt32(roster.Guild.Points); w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0);
            // 0119671F..01196743 -> 019E9E90..019E9EC0, then the
            // GuildContributeTabWindow guild/member cumulative and month values.
            w.WriteInt32(guildContribution.Total);
            w.WriteInt32(guildContribution.MonthValue);
            w.WriteInt32(memberContribution.Total);
            w.WriteInt32(memberContribution.MonthValue);
            // 011967A6/011967B2 -> 019E9F80: actual server/channel catalog keys.
            // 019E9FB0 looks up this pair; the command's successful ACK does not set it.
            if (roster.Guild.RecommendedServerId < 0 || roster.Guild.RecommendedServerId > byte.MaxValue
                || roster.Guild.RecommendedChannelId < 0 || roster.Guild.RecommendedChannelId > byte.MaxValue
                || (roster.Guild.RecommendedServerId == 0 && roster.Guild.RecommendedChannelId != 0))
                throw new InvalidOperationException("Guild recommended channel projection is invalid.");
            w.WriteUInt32((uint)roster.Guild.RecommendedServerId);
            w.WriteUInt32((uint)roster.Guild.RecommendedChannelId);
            w.WriteUInt32(0); w.WriteUInt32(0); // Remaining tail fields are unconfirmed.
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_INFO, w.ToArray());
        }
    }
}
