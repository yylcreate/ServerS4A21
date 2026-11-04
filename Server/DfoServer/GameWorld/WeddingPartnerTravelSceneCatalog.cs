using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal static class WeddingPartnerTravelSceneCatalog
    {
        private static readonly Lazy<HashSet<(byte Town, byte Area)>> Current = new(() =>
        {
            var areas = new HashSet<(byte, byte)>();
            foreach (var entry in LstFile.Parse(PvfArchiveAccessor.ReadText("town/town.lst")).Entries)
            {
                if (entry.Id is < 0 or > byte.MaxValue || entry.Id == 17) continue;
                foreach (var area in TownFile.Parse(PvfArchiveAccessor.ReadText("town/" + entry.FilePath)).Areas)
                {
                    if (area.Id is < 0 or > byte.MaxValue) continue;
                    string type = area.AreaType?.Trim('[', ']', ' ');
                    string map = (area.MapPath ?? "").Replace('\\', '/');
                    if ((string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "dungeon gate", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "couple room", StringComparison.OrdinalIgnoreCase))
                        && !map.StartsWith("Church/", StringComparison.OrdinalIgnoreCase)
                        && !map.StartsWith("Guildagit/", StringComparison.OrdinalIgnoreCase))
                        areas.Add(((byte)entry.Id, (byte)area.Id));
                }
            }
            return areas;
        });
        internal static bool IsPublicOrCoupleRoom(byte town, byte area) => Current.Value.Contains((town, area));
    }
}
