using System;
using System.Collections.Generic;
using System.IO;

namespace PvfLib
{
    public sealed class VillageSaveArea
    {
        internal VillageSaveArea(int townId, int areaId)
        {
            TownId = townId;
            AreaId = areaId;
        }

        public int TownId { get; }
        public int AreaId { get; }
    }

    /// <summary>
    /// Read-only [ignore village save] town/area pairs in server parameters.
    /// Unrelated parameters remain in Root/Content. No town classification,
    /// activity state, wildcard or default exclusion is inferred.
    /// </summary>
    public sealed class VillageSaveConfigFile : PvfModelBase
    {
        private HashSet<(int TownId, int AreaId)> _excludedAreas = new();

        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<VillageSaveArea> ExcludedAreas { get; private set; }
            = Array.Empty<VillageSaveArea>();

        private VillageSaveConfigFile() { }

        public static VillageSaveConfigFile Parse(string content)
        {
            var file = new VillageSaveConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("Server parameters are empty.");
                var root = new ScriptParser().Parse(content);
                ScriptNode table = null;
                foreach (var node in root.Children)
                {
                    if (!ActivityConfigScriptReader.TagIs(node, "ignore village save"))
                        continue;
                    if (table != null || !node.HasEndTag)
                        throw new InvalidOperationException("[ignore village save] must be unique and closed.");
                    table = node;
                }
                ValidateTableBoundaries(table, content);

                var entries = new List<VillageSaveArea>();
                var pairs = new HashSet<(int TownId, int AreaId)>();
                if (table != null)
                {
                    var values = ActivityConfigScriptReader.Integers(table, content);
                    if (values.Count % 2 != 0)
                        throw new InvalidOperationException("[ignore village save] requires complete town/area pairs.");
                    for (var offset = 0; offset < values.Count; offset += 2)
                    {
                        var town = values[offset];
                        var area = values[offset + 1];
                        if (town <= 0 || town > byte.MaxValue || area < 0 || area > byte.MaxValue)
                            throw new InvalidOperationException("[ignore village save] town/area is outside the A21 byte range.");
                        if (!pairs.Add((town, area)))
                            throw new InvalidOperationException("[ignore village save] contains a duplicate town/area pair.");
                        entries.Add(new VillageSaveArea(town, area));
                    }
                }

                // Missing or empty tables exclude nothing. Publish no partial
                // list if a later row or the section itself is malformed.
                file.Root = root;
                file.ExcludedAreas = entries.AsReadOnly();
                file._excludedAreas = pairs;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public bool IsExcluded(int townId, int areaId)
            => IsValid && _excludedAreas.Contains((townId, areaId));

        private static void ValidateTableBoundaries(ScriptNode table, string content)
        {
            using var reader = new StringReader(content);
            string line;
            var index = 0;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.Length != 0 && text[0] != '#')
                {
                    var isTableBoundary = text.StartsWith("[ignore village save", StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith("[/ignore village save", StringComparison.OrdinalIgnoreCase);
                    if (isTableBoundary && (table == null
                        || !(index == table.StartLineIndex && string.Equals(text, "[" + table.Tag + "]", StringComparison.OrdinalIgnoreCase))
                            && !(index == table.EndLineIndex && string.Equals(text, "[/" + table.Tag + "]", StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidOperationException("[ignore village save] has an unmatched or malformed boundary.");
                    if (table != null && index > table.StartLineIndex && index < table.EndLineIndex
                        && (text[0] == '[' || text[text.Length - 1] == ']'))
                        throw new InvalidOperationException("[ignore village save] cannot contain nested or unmatched tags.");
                }
                index++;
            }
        }
    }
}
