using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;

namespace DfoServer.GameWorld
{
    internal sealed record WeddingScene(byte Town, byte Area, string MapPath);

    internal sealed class WeddingSceneCatalog
    {
        internal const byte NativeCoupleTown = 101;
        internal const byte NativeChurchTown = 2;
        private static readonly Lazy<WeddingSceneCatalog> Current = new(() => Load(PvfArchiveAccessor.ReadText));
        internal static WeddingSceneCatalog Get() => Current.Value;
        internal IReadOnlyList<WeddingScene> CeremonyRooms { get; private init; }
        internal IReadOnlyList<WeddingScene> ChurchScenes { get; private init; }
        internal WeddingScene CoupleRoom { get; private init; }
        private readonly ConcurrentDictionary<string, (short X, short Y)> _guestLandings = new();

        // A guest does not occupy either ceremonial host pose. Pick a stable
        // walkable landing from the current map's first movable rectangle.
        internal (short X, short Y) GuestLanding(WeddingScene scene) => _guestLandings.GetOrAdd(scene.MapPath, path =>
        {
            var map = MapFile.Parse(PvfArchiveAccessor.ReadText("map/" + path));
            var rectangles = map.VirtualMovableArea;
            if (rectangles == null || rectangles.Length < 4 || rectangles.Length % 4 != 0
                || rectangles[2] <= 0 || rectangles[3] <= 0)
                throw new InvalidDataException("Wedding church has no valid guest landing rectangle.");
            int x = checked(rectangles[0] + rectangles[2] / 2), y = checked(rectangles[1] + rectangles[3] / 2);
            return (checked((short)x), checked((short)y));
        });

        internal static WeddingSceneCatalog Load(Func<string, string> read)
        {
            var ceremonies = new List<WeddingScene>();
            var homes = new List<WeddingScene>();
            foreach (var entry in LstFile.Parse(read("town/town.lst")).Entries)
            {
                if (entry.Id is < 0 or > byte.MaxValue) continue;
                foreach (var area in TownFile.Parse(read("town/" + entry.FilePath)).Areas)
                {
                    if (area.Id is < 0 or > byte.MaxValue) continue;
                    string map = (area.MapPath ?? "").Replace('\\', '/').TrimStart('/');
                    var scene = new WeddingScene((byte)entry.Id, (byte)area.Id, map);
                    if (map.Equals("Church/Church.map", StringComparison.OrdinalIgnoreCase)) ceremonies.Add(scene);
                    if (map.Equals("CoupleRoom/CoupleRoom.map", StringComparison.OrdinalIgnoreCase)) homes.Add(scene);
                }
            }
            if (ceremonies.Count == 0 || ceremonies.Select(x => x.Town).Distinct().Count() != 1 || homes.Count != 1
                || ceremonies.Select(x => (x.Town, x.Area)).Distinct().Count() != ceremonies.Count)
                throw new InvalidDataException("A21 wedding church or couple-room scenes are missing or ambiguous in town/town.lst.");
            // C8220A/C7E8AB only update the room/tree entities in native town 101.
            if (homes[0].Town != NativeCoupleTown) throw new InvalidDataException("The PVF couple home does not fit A21's native town-101 room/tree consumers.");
            // C97FB8..C97FFB only triggers the A21 automatic ceremony exit in
            // town 2, areas 12..62. The current PVF also has a Church at 63;
            // keep it private, but never allocate an incompatible ceremony.
            var usable = ceremonies.Where(x => x.Town == NativeChurchTown && x.Area is >= 12 and <= 62).OrderBy(x => x.Area).ToList();
            if (usable.Count == 0) throw new InvalidDataException("PVF Church scenes do not fit the A21 client's ceremony-exit bounds.");
            return new WeddingSceneCatalog { CeremonyRooms = usable.AsReadOnly(), ChurchScenes = ceremonies.AsReadOnly(), CoupleRoom = homes[0] };
        }
    }
}
