using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal sealed record GuildContentDefinition(
        int RowNumber, int Category, int Parameter, int Days, int CountFlag,
        int ItemId, uint Price, bool CostsGold)
    {
        // The client computes the remaining duration as a signed 32-bit value.
        internal const uint PermanentExpiryUnix = int.MaxValue;
        internal bool IsCountBased => CountFlag != 0;
        internal bool IsPermanent => Days <= 0 || IsCountBased;
        internal int StorageDays => IsPermanent ? -1 : Days;
        internal int UseCount => IsCountBased ? Parameter : 0;
        internal int ExperienceBonusPercent => Category == GuildContentCatalog.ExperienceCategory ? Parameter : 0;
    }

    internal static class GuildContentCatalog
    {
        internal const int GoldCurrencyCategory = 20;
        internal const int PartyTeleportCategory = 7;
        internal const int ExperienceCategory = 1;
        private static readonly object Sync = new object();
        private static Dictionary<int, GuildContentDefinition> _cached;

        internal static GuildContentDefinition Resolve(int rowNumber)
            => Load().TryGetValue(rowNumber, out var definition) ? definition : null;
        internal static IReadOnlyCollection<GuildContentDefinition> All => Load().Values;

        private static Dictionary<int, GuildContentDefinition> Load()
        {
            lock (Sync)
                return _cached ??= Parse(PvfArchiveAccessor.ReadText("etc/(r)guild.etc"));
        }

        internal static Dictionary<int, GuildContentDefinition> Parse(string text)
        {
            var config = GuildAdditionalContentsConfigFile.Parse(text);
            if (!config.IsValid) throw new InvalidDataException(config.InvalidReason);
            if (config.Contents.Count == 0 || config.AdditionalContents.Count == 0)
                throw new InvalidDataException("Both PVF guild content tables must contain complete rows.");
            var result = new Dictionary<int, GuildContentDefinition>();
            foreach (var row in config.Contents.Concat(config.AdditionalContents))
            {
                var columns = row.Columns;
                if (row.ContentType > byte.MaxValue || columns[7] <= 0
                    || columns[3] < -1 || (columns[4] != 0 && columns[4] != 1)
                    || (columns[4] != 0 && columns[2] <= 0))
                    throw new InvalidDataException($"PVF guild content row {row.Index} cannot be represented by the content owner.");
                var definition = new GuildContentDefinition(row.Index, row.ContentType, columns[2],
                    columns[3], columns[4], row.ItemId, checked((uint)columns[7]),
                    row.ContentType == GoldCurrencyCategory);
                if (!result.TryAdd(row.Index, definition))
                    throw new InvalidDataException($"PVF guild content row {row.Index} is duplicated across tables.");
            }
            return result;
        }

        internal static void ResetCacheForTests()
        {
            lock (Sync) _cached = null;
        }
    }
}
