using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.GameWorld
{
    /// <summary>Frozen position definition: explicit area rows and registered guild destination roles.</summary>
    internal sealed class TownPositionSaveDefinition
    {
        private readonly VillageSaveConfigFile _areas;
        private readonly HashSet<int> _guildTownIds;

        private TownPositionSaveDefinition(VillageSaveConfigFile areas, HashSet<int> guildTownIds)
        {
            _areas = areas;
            _guildTownIds = guildTownIds;
        }

        internal bool IsExcluded(int townId, int areaId)
            => _guildTownIds.Contains(townId) || _areas.IsExcluded(townId, areaId);

        internal static TownPositionSaveDefinition Load(
            string serverParameters, string townList, Func<string, string> readTextIfPresent,
            int requiredGuildTownId)
        {
            ArgumentNullException.ThrowIfNull(readTextIfPresent);
            var areas = VillageSaveConfigFile.Parse(serverParameters);
            if (!areas.IsValid)
                throw new InvalidDataException("etc/serverparameter_chn.etc: " + areas.InvalidReason);
            var entries = ReadTownRegistrations(townList);

            var towns = new Dictionary<int, TownFile>();
            foreach (var entry in entries)
            {
                var path = Path.Combine("town", entry.FilePath);
                var content = readTextIfPresent(path);
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidDataException("Registered town is missing: " + path);
                var town = TownFile.Parse(content);
                var declarations = new ScriptParser().Parse(content).Children.Where(node =>
                    string.Equals(node.Tag, "area", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (town.Areas.Count == 0 || declarations.Length != town.Areas.Count || declarations.Any(node => !node.HasEndTag)
                    || town.Areas.Select(area => area.Id).Distinct().Count() != town.Areas.Count
                    || town.Areas.Any(area => area.Id < 0 || area.Id > byte.MaxValue || string.IsNullOrWhiteSpace(area.MapPath)))
                    throw new InvalidDataException("Registered town has an invalid or duplicate area: " + path);
                ValidateAreaBoundaries(declarations, content, path);
                towns.Add(entry.Id, town);
            }
            if (!towns.ContainsKey(requiredGuildTownId))
                throw new InvalidDataException("The current guild entrance owner has no registered town.");

            var guildTownIds = new HashSet<int>();
            var maps = new Dictionary<string, GuildAgitEntranceInfo>(StringComparer.OrdinalIgnoreCase);
            var absentMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var town in towns.Values)
            {
                foreach (var area in town.Areas)
                {
                    var path = Path.Combine("map", area.MapPath).Replace('\\', '/');
                    if (maps.ContainsKey(path) || absentMaps.Contains(path)) continue;
                    var content = readTextIfPresent(path);
                    // Several current ordinary Seria gates are virtual TWN
                    // paths with no archive file. They cannot declare a target.
                    if (string.IsNullOrWhiteSpace(content)) { absentMaps.Add(path); continue; }
                    var entrance = GuildAgitEntranceInfo.Parse(content);
                    if (!entrance.IsValid)
                        throw new InvalidDataException(path + ": " + entrance.InvalidReason);
                    maps.Add(path, entrance);
                    if (!entrance.HasPhysicalDestination) continue;
                    if (!towns.TryGetValue(entrance.TargetTownId, out var target)
                        || !target.Areas.Any(candidate => candidate.Id == entrance.TargetAreaId))
                        throw new InvalidDataException(path + ": guild entrance target is not a registered town/area.");
                    // The target town owns all its areas, including its login
                    // gate; the ordinary source containing the entrance does not.
                    guildTownIds.Add(entrance.TargetTownId);
                }
            }
            if (!guildTownIds.Contains(requiredGuildTownId))
                throw new InvalidDataException("The current guild entrance owner has no positive registered MAP target.");
            foreach (var guildTownId in guildTownIds)
                if (towns[guildTownId].Areas.Any(area => absentMaps.Contains(
                        Path.Combine("map", area.MapPath).Replace('\\', '/'))))
                    throw new InvalidDataException("A registered guild destination area has no MAP resource.");
            return new TownPositionSaveDefinition(areas, guildTownIds);
        }

        private static IReadOnlyList<LstEntry> ReadTownRegistrations(string content)
        {
            content ??= string.Empty;
            var entries = new List<LstEntry>();
            var ids = new HashSet<int>();
            var position = 0;
            void SkipSeparators()
            {
                while (position < content.Length)
                {
                    if (char.IsWhiteSpace(content[position])) { position++; continue; }
                    if (content[position] != '#') break;
                    while (position < content.Length && content[position] != '\n') position++;
                }
            }
            while (true)
            {
                SkipSeparators();
                if (position == content.Length) break;
                var start = position;
                while (position < content.Length && !char.IsWhiteSpace(content[position]) && content[position] != '#') position++;
                var token = content.Substring(start, position - start);
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id <= 0 || id > byte.MaxValue || !ids.Add(id))
                    throw new InvalidDataException("town/town.lst has an invalid or duplicate ID: " + token);
                SkipSeparators();
                if (position == content.Length || content[position] != '`')
                    throw new InvalidDataException("town/town.lst requires a quoted path after each ID.");
                var end = content.IndexOf('`', position + 1);
                if (end < 0)
                    throw new InvalidDataException("town/town.lst contains an unclosed quoted path.");
                var path = content.Substring(position + 1, end - position - 1).Replace('\\', '/');
                position = end + 1;
                if (position < content.Length && !char.IsWhiteSpace(content[position]) && content[position] != '#'
                    || !path.EndsWith(".twn", StringComparison.OrdinalIgnoreCase) || path.Contains(':')
                    || path.Any(char.IsControl) || path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
                    throw new InvalidDataException("town/town.lst has an invalid quoted relative TWN path.");
                entries.Add(new LstEntry { Id = id, FilePath = path });
            }
            if (entries.Count == 0)
                throw new InvalidDataException("town/town.lst has no registrations.");
            return entries;
        }

        private static void ValidateAreaBoundaries(IEnumerable<ScriptNode> declarations, string content, string path)
        {
            var boundaries = new Dictionary<int, string>();
            foreach (var node in declarations)
            {
                boundaries.Add(node.StartLineIndex, "[" + node.Tag + "]");
                boundaries.Add(node.EndLineIndex, "[/" + node.Tag + "]");
            }
            using var reader = new StringReader(content);
            string line;
            var index = 0;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.StartsWith("[area", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("[/area", StringComparison.OrdinalIgnoreCase))
                {
                    if (!boundaries.TryGetValue(index, out var expected)
                        || !string.Equals(text, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Registered town has a nested, unmatched or malformed area boundary: " + path);
                }
                index++;
            }
        }
    }
}
