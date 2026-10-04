using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class DailyScheduleDungeonDescriptor
    {
        internal DailyScheduleDungeonDescriptor(IReadOnlyList<int> rawFields)
        {
            RawFields = rawFields;
            DungeonId = rawFields[0];
        }

        public int DungeonId { get; }

        // Complete 17-value resource row, including DungeonId at index zero.
        // The remaining fields are deliberately not assigned time semantics.
        public IReadOnlyList<int> RawFields { get; }
    }

    /// <summary>
    /// Read-only etc/chn_schedule.etc dungeon descriptors. Other current root
    /// tables are retained in Root/Content; this model does not interpret their
    /// values or decide when a schedule is open.
    /// </summary>
    public sealed class DailyScheduleConfigFile : PvfModelBase
    {
        private const int DungeonRowWidth = 17;
        private IReadOnlyDictionary<int, DailyScheduleDungeonDescriptor> _dungeons
            = new Dictionary<int, DailyScheduleDungeonDescriptor>();

        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<DailyScheduleDungeonDescriptor> Dungeons { get; private set; }
            = Array.Empty<DailyScheduleDungeonDescriptor>();

        private DailyScheduleConfigFile() { }

        public static DailyScheduleConfigFile Parse(string content)
        {
            var file = new DailyScheduleConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var root = ActivityConfigScriptReader.ReadRoot(content);
                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ScriptNode dungeonTable = null;
                foreach (var node in root.Children)
                {
                    if (!tags.Add(node.Tag))
                        throw new InvalidOperationException("Daily schedule root tables must be unique.");
                    if (!ActivityConfigScriptReader.TagIs(node, "dungeon")
                        && !ActivityConfigScriptReader.TagIs(node, "quest")
                        && !ActivityConfigScriptReader.TagIs(node, "common schedule")
                        && !ActivityConfigScriptReader.TagIs(node, "dungeon minimap"))
                        throw new InvalidOperationException("Unexpected daily schedule table: " + node.Tag);
                    if (!node.HasEndTag || node.Children.Count != 0)
                        throw new InvalidOperationException("Daily schedule tables must be closed leaves.");
                    if (ActivityConfigScriptReader.TagIs(node, "dungeon")) dungeonTable = node;
                    else ActivityConfigScriptReader.Integers(node, content);
                }
                if (dungeonTable == null)
                    throw new InvalidOperationException("Daily schedule [dungeon] table is missing.");

                var values = ActivityConfigScriptReader.Integers(dungeonTable, content);
                if (values.Count == 0 || values.Count % DungeonRowWidth != 0)
                    throw new InvalidOperationException("Daily schedule dungeons require complete 17-integer rows.");
                var entries = new List<DailyScheduleDungeonDescriptor>();
                var byId = new Dictionary<int, DailyScheduleDungeonDescriptor>();
                for (var offset = 0; offset < values.Count; offset += DungeonRowWidth)
                {
                    var row = values.GetRange(offset, DungeonRowWidth).AsReadOnly();
                    var entry = new DailyScheduleDungeonDescriptor(row);
                    if (entry.DungeonId <= 0 || !byId.TryAdd(entry.DungeonId, entry))
                        throw new InvalidOperationException("Daily schedule dungeon IDs must be positive and unique.");
                    entries.Add(entry);
                }

                // A late duplicate/malformed row must not publish an earlier one.
                file.Root = root;
                file.Dungeons = entries.AsReadOnly();
                file._dungeons = byId;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public bool TryGetDungeon(int dungeonId, out DailyScheduleDungeonDescriptor descriptor)
        {
            descriptor = null;
            return IsValid && _dungeons.TryGetValue(dungeonId, out descriptor);
        }
    }
}
