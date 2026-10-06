using System;
using System.Collections.Generic;
using DfoServer.Game.Guilds;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildContributionHistoryPacketBuilder
    {
        internal const int Capacity = 50;
        internal const int RowWidth = 44;
        // Native 019ED360 case 8 is a valid display slot whose current DSTR is
        // "dnf_44507". Mapping our committed guild-clear rows to it is a server
        // display policy, not a claim about an official activity classification.
        private const uint NativeUnlabelledDisplaySlot = 8;

        // 0111F180 reads a fixed 2200 bytes, without a row count or DSTRs.
        // 00778E70: Unix seconds, ANSI name[32], contribution:u32, kind:u32.
        internal static byte[] Build(GuildContributionHistory history)
        {
            ArgumentNullException.ThrowIfNull(history);
            if (history.GuildId <= 0 || history.CharacterId <= 0 || history.AccountId <= 0
                || history.Rows == null || history.Rows.Count > Capacity)
                throw new InvalidOperationException("Guild contribution history requires a current member snapshot.");
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            var seen = new HashSet<(Guid Source, int Character)>();
            foreach (var row in history.Rows)
            {
                if (row == null || row.SourceEventId == Guid.Empty || row.CharacterId <= 0 || row.AccountId <= 0
                    || row.Amount <= 0 || row.Name == null || row.Name.IndexOf('\0') >= 0
                    || !seen.Add((row.SourceEventId, row.CharacterId)))
                    throw new InvalidOperationException("Guild contribution history contains an invalid committed row.");
                long seconds = row.GrantedAt.ToUnixTimeSeconds();
                // The client's date helper consumes a signed 32-bit time_t.
                if (seconds < 0 || seconds > int.MaxValue)
                    throw new InvalidOperationException("Guild contribution history timestamp exceeds the client's date range.");
                var name = ClientTextEncoding.GetBytes(row.Name);
                if (name.Length >= 32)
                    throw new InvalidOperationException("Guild contribution history names must fit the native NUL-terminated field.");
                writer.WriteUInt32((uint)seconds);
                writer.WriteBytes(name);
                writer.WriteZeroBytes(32 - name.Length);
                writer.WriteInt32(row.Amount);
                writer.WriteUInt32(NativeUnlabelledDisplaySlot);
            }
            writer.WriteZeroBytes((Capacity - history.Rows.Count) * RowWidth);
            return GamePacketEnvelopeBuilder.Build(1, (ushort)CmdPacketTypeA21.GUILD_CONTRIBUTE_HISTORY, writer.ToArray());
        }
    }
}
