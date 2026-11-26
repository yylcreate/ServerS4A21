using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Network;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildDungeonPacketBuilder
    {
        internal static byte[] RepeatBonus() => GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.GUILD_DUNGEON_REPEAT_BONUS_INFO, new byte[4]);
        internal static byte[] Progress(IReadOnlyDictionary<int, int> clearCounts, uint resetEndAtSeconds)
        {
            ArgumentNullException.ThrowIfNull(clearCounts);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            // No optional NPC marker. 0271E970 -> 028A2F20 consumes offset 5
            // as the absolute Unix reset second; offset 9 remains unassigned.
            writer.Write((byte)0);
            writer.Write(uint.MaxValue);
            writer.Write(resetEndAtSeconds);
            writer.Write(0u);
            writer.Write(checked((uint)clearCounts.Count));
            foreach (var row in clearCounts)
            {
                if (row.Key <= 0 || row.Value < 0)
                    throw new ArgumentException("Guild dungeon progress rows must be nonnegative registered counters.");
                writer.Write(checked((uint)row.Key));
                writer.Write(checked((uint)row.Value));
            }
            return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.GUILD_DUNGEON_INFO, stream.ToArray());
        }
    }
}
