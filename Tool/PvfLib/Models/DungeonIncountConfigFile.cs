using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class DungeonIncountEntry
    {
        internal DungeonIncountEntry(IReadOnlyList<int> rawFields)
        {
            RawFields = rawFields;
            DungeonId = rawFields[1];
            MaximumCount = rawFields[2];
        }

        public IReadOnlyList<int> RawFields { get; }
        public int DungeonId { get; }
        public int MaximumCount { get; }
    }

    public sealed class DungeonIncountResetRule
    {
        internal DungeonIncountResetRule(IReadOnlyList<int> rawFields)
            => RawFields = rawFields;

        // Preserve the two configured integers. Neither is assigned a timezone,
        // hour, scope or durable reset policy by this resource model.
        public IReadOnlyList<int> RawFields { get; }
    }

    /// <summary>
    /// Read-only etc/dungeonetc/dungeonincountinfo.etc definition. The account
    /// and character tables remain separate. This model does not debit entry,
    /// create a counter, choose a reset boundary or authorize a UI projection.
    /// </summary>
    public sealed class DungeonIncountConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<DungeonIncountEntry> CharacterEntries { get; private set; }
            = Array.AsReadOnly(Array.Empty<DungeonIncountEntry>());
        public IReadOnlyList<DungeonIncountEntry> AccountEntries { get; private set; }
            = Array.AsReadOnly(Array.Empty<DungeonIncountEntry>());
        public IReadOnlyList<DungeonIncountResetRule> ResetRules { get; private set; }
            = Array.AsReadOnly(Array.Empty<DungeonIncountResetRule>());
        public IReadOnlyList<int> EnterAddIncount { get; private set; }
            = Array.AsReadOnly(Array.Empty<int>());

        private DungeonIncountConfigFile() { }

        public static DungeonIncountConfigFile Parse(string content)
        {
            var file = new DungeonIncountConfigFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                var root = ActivityConfigScriptReader.ReadRoot(content);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<DungeonIncountEntry> character = null;
                IReadOnlyList<DungeonIncountEntry> account = null;
                IReadOnlyList<DungeonIncountResetRule> reset = null;
                IReadOnlyList<int> enterAdd = null;
                foreach (var node in root.Children)
                {
                    if (!node.HasEndTag || !seen.Add(node.Tag))
                        throw new InvalidOperationException("Dungeon entry-count sections must be unique and closed.");
                    if (ActivityConfigScriptReader.TagIs(node, "dungeon incount reset info"))
                        reset = ReadResetRules(node, content);
                    else if (ActivityConfigScriptReader.TagIs(node, "dungeon incount info"))
                        character = ReadEntries(node, content);
                    else if (ActivityConfigScriptReader.TagIs(node, "account dungeon incount info"))
                        account = ReadEntries(node, content);
                    else if (ActivityConfigScriptReader.TagIs(node, "enter add incount dungeon index"))
                    {
                        var values = ActivityConfigScriptReader.Integers(node, content);
                        if (values.Count == 0)
                            throw new InvalidOperationException("Entry-added dungeon indices cannot be empty.");
                        foreach (var value in values)
                            if (value <= 0)
                                throw new InvalidOperationException("Entry-added dungeon indices must be positive.");
                        // These relationships may intentionally repeat an index;
                        // preserve the original ordered array, not a set.
                        enterAdd = values.AsReadOnly();
                    }
                    else
                        throw new InvalidOperationException("Unexpected dungeon entry-count section: " + node.Tag);
                }
                if (character == null || account == null || reset == null || enterAdd == null)
                    throw new InvalidOperationException("Dungeon entry-count definition is missing a required section.");

                // No partially validated earlier table is exposed on a later
                // parse failure or an ambiguous duplicate dungeon definition.
                file.Root = root;
                file.CharacterEntries = character;
                file.AccountEntries = account;
                file.ResetRules = reset;
                file.EnterAddIncount = enterAdd;
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        private static IReadOnlyList<DungeonIncountEntry> ReadEntries(ScriptNode node, string content)
        {
            var values = ActivityConfigScriptReader.Integers(node, content);
            if (values.Count == 0 || values.Count % 4 != 0)
                throw new InvalidOperationException("Dungeon entry-count rows require four complete integers.");
            var entries = new List<DungeonIncountEntry>();
            var dungeonIds = new HashSet<int>();
            for (var offset = 0; offset < values.Count; offset += 4)
            {
                if (values[offset + 1] <= 0 || values[offset + 2] <= 0)
                    throw new InvalidOperationException("Dungeon entry-count index and maximum must be positive.");
                if (!dungeonIds.Add(values[offset + 1]))
                    throw new InvalidOperationException("Dungeon entry-count dungeon index is duplicated within its scope.");
                entries.Add(new DungeonIncountEntry(Array.AsReadOnly(new[]
                {
                    values[offset], values[offset + 1], values[offset + 2], values[offset + 3],
                })));
            }
            return entries.AsReadOnly();
        }

        private static IReadOnlyList<DungeonIncountResetRule> ReadResetRules(ScriptNode node, string content)
        {
            if (node.DataItems.Count != 0 || node.Children.Count == 0)
                throw new InvalidOperationException("Dungeon entry-count reset section requires explicit reset-info children.");
            var result = new List<DungeonIncountResetRule>();
            var pairs = new HashSet<(int, int)>();
            foreach (var child in node.Children)
            {
                if (!child.HasEndTag || !ActivityConfigScriptReader.TagIs(child, "reset info"))
                    throw new InvalidOperationException("Unexpected or unclosed entry-count reset field.");
                var values = ActivityConfigScriptReader.Integers(child, content);
                if (values.Count != 2 || !pairs.Add((values[0], values[1])))
                    throw new InvalidOperationException("Entry-count reset field requires one unique integer pair.");
                result.Add(new DungeonIncountResetRule(values.AsReadOnly()));
            }
            return result.AsReadOnly();
        }
    }
}
