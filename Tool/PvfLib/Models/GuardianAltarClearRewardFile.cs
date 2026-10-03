using System;
using System.Collections.Generic;
using System.Linq;

namespace PvfLib
{
    public sealed class GuardianAltarClearRewardItem
    {
        public string Kind { get; }
        public int ItemId { get; }
        public int Count { get; }

        internal GuardianAltarClearRewardItem(string kind, int itemId, int count)
        {
            Kind = kind;
            ItemId = itemId;
            Count = count;
        }
    }

    public sealed class GuardianAltarDifficultyClearReward
    {
        public string DifficultyName { get; }
        public IReadOnlyList<GuardianAltarClearRewardItem> Items { get; }

        internal GuardianAltarDifficultyClearReward(string difficultyName,
            IReadOnlyList<GuardianAltarClearRewardItem> items)
        {
            DifficultyName = difficultyName;
            Items = items;
        }
    }

    public sealed class GuardianAltarSurvivalClearReward
    {
        public int FirstRound { get; }
        public int LastRound { get; }
        public IReadOnlyList<GuardianAltarClearRewardItem> Items { get; }

        internal GuardianAltarSurvivalClearReward(int firstRound, int lastRound,
            IReadOnlyList<GuardianAltarClearRewardItem> items)
        {
            FirstRound = firstRound;
            LastRound = lastRound;
            Items = items;
        }
    }

    /// <summary>
    /// Read-only Guardian clear-reward projections. Stage completion,
    /// difficulty admission and inventory grants belong to their server owners.
    /// Ordinary and survival sections have explicit, independent parse contracts.
    /// </summary>
    public sealed class GuardianAltarClearRewardFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuardianAltarDifficultyClearReward> Difficulties
            { get; private set; } = Array.Empty<GuardianAltarDifficultyClearReward>();
        public IReadOnlyList<GuardianAltarSurvivalClearReward> SurvivalRanges
            { get; private set; } = Array.Empty<GuardianAltarSurvivalClearReward>();

        public static GuardianAltarClearRewardFile Parse(string content)
        {
            var file = Create(content, null);
            if (string.IsNullOrWhiteSpace(content))
                return file;
            try
            {
                file.Root = new ScriptParser().Parse(content);
                file.ParseCore();
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public static GuardianAltarClearRewardFile Parse(DungeonFile shared)
        {
            var file = Create(shared?.Content, shared?.Root);
            if (shared?.Root == null || string.IsNullOrWhiteSpace(shared.Content))
                return file;
            try
            {
                file.ParseCore();
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public static GuardianAltarClearRewardFile ParseSurvival(string content)
        {
            var file = Create(content, null);
            if (string.IsNullOrWhiteSpace(content))
                return file;
            try
            {
                file.Root = new ScriptParser().Parse(content);
                file.ParseSurvivalCore();
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public static GuardianAltarClearRewardFile ParseSurvival(DungeonFile shared)
        {
            var file = Create(shared?.Content, shared?.Root);
            if (shared?.Root == null || string.IsNullOrWhiteSpace(shared.Content))
                return file;
            try
            {
                file.ParseSurvivalCore();
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        public IReadOnlyList<GuardianAltarClearRewardItem> GetRewards(int clearedRounds)
        {
            if (!IsValid || SurvivalRanges.Count == 0)
                throw new InvalidOperationException("Guardian survival rewards are not available.");
            if (clearedRounds < 0)
                throw new ArgumentOutOfRangeException(nameof(clearedRounds));
            if (clearedRounds == 0)
                return Array.Empty<GuardianAltarClearRewardItem>();
            var range = SurvivalRanges.SingleOrDefault(row =>
                clearedRounds >= row.FirstRound && clearedRounds <= row.LastRound);
            return range?.Items ?? throw new ArgumentOutOfRangeException(nameof(clearedRounds),
                "Cleared rounds are outside the current Guardian survival reward resource.");
        }

        private static GuardianAltarClearRewardFile Create(string content, ScriptNode root)
        {
            return new GuardianAltarClearRewardFile
            {
                Content = content ?? string.Empty,
                Root = root ?? new ScriptNode { Tag = "ROOT" },
                InvalidReason = "Guardian Altar clear reward is empty or lacks its parsed root",
            };
        }

        private void ParseCore()
        {
            var section = GuardianAltarScriptReader.SingleChild(Root, "advance altar clear reward");
            GuardianAltarScriptReader.RequireClosed(section);
            GuardianAltarScopedStructureValidator.RequireBalanced(section, Content);
            var allowed = new HashSet<string>(new[] { "easy", "medium", "hard" },
                StringComparer.OrdinalIgnoreCase);
            if (section.Children.Count != allowed.Count
                || section.Children.Any(child => !allowed.Contains(child.Tag))
                || GuardianAltarScriptReader.Tokens(section, Content).Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar clear reward requires only easy, medium and hard sections");

            var difficulties = new List<GuardianAltarDifficultyClearReward>(allowed.Count);
            foreach (var child in section.Children)
            {
                // Validate uniqueness without changing the source order or tree.
                GuardianAltarScriptReader.SingleChild(section, child.Tag);
                GuardianAltarScriptReader.RequireClosed(child);
                GuardianAltarScriptReader.RequireLeaf(child);
                var tokens = GuardianAltarScriptReader.Tokens(child, Content);
                difficulties.Add(new GuardianAltarDifficultyClearReward(
                    child.Tag.ToLowerInvariant(), ParseItems(tokens)));
            }

            // No earlier difficulty becomes observable if a later row is invalid.
            Difficulties = difficulties.AsReadOnly();
            InvalidReason = string.Empty;
        }

        private void ParseSurvivalCore()
        {
            var section = GuardianAltarScriptReader.SingleChild(Root, "advance altar survival clear reward");
            GuardianAltarScriptReader.RequireClosed(section);
            GuardianAltarScopedStructureValidator.RequireBalanced(section, Content);
            if (section.Children.Count == 0
                || section.Children.Any(child => !string.Equals(child.Tag, "type", StringComparison.OrdinalIgnoreCase))
                || GuardianAltarScriptReader.Tokens(section, Content).Count != 0)
                throw new InvalidOperationException("Guardian survival clear reward requires a non-empty type list only.");

            var ranges = new List<GuardianAltarSurvivalClearReward>();
            foreach (var child in section.Children)
            {
                GuardianAltarScriptReader.RequireClosed(child);
                if (child.Children.Count != 2
                    || child.Children.Any(field => !string.Equals(field.Tag, "round", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(field.Tag, "list", StringComparison.OrdinalIgnoreCase))
                    || GuardianAltarScriptReader.Tokens(child, Content).Count != 0)
                    throw new InvalidOperationException("Guardian survival reward type requires round and list fields only.");
                var round = GuardianAltarScriptReader.SingleChild(child, "round");
                var list = GuardianAltarScriptReader.SingleChild(child, "list");
                GuardianAltarScriptReader.RequireClosed(round);
                GuardianAltarScriptReader.RequireLeaf(round);
                GuardianAltarScriptReader.RequireClosed(list);
                GuardianAltarScriptReader.RequireLeaf(list);
                var values = GuardianAltarScriptReader.Tokens(round, Content);
                if (values.Count != 2)
                    throw new InvalidOperationException("Guardian survival round range requires exactly two integers.");
                var first = GuardianAltarScriptReader.Integer(values[0]);
                var last = GuardianAltarScriptReader.Integer(values[1]);
                if (first <= 0 || last < first || (ranges.Count == 0 && first != 1)
                    || (ranges.Count > 0 && (long)ranges[^1].LastRound + 1 != first))
                    throw new InvalidOperationException("Guardian survival reward ranges must cover consecutive rounds starting at one.");
                ranges.Add(new GuardianAltarSurvivalClearReward(first, last,
                    ParseItems(GuardianAltarScriptReader.Tokens(list, Content))));
            }

            SurvivalRanges = ranges.AsReadOnly();
            InvalidReason = string.Empty;
        }

        private static IReadOnlyList<GuardianAltarClearRewardItem> ParseItems(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0 || tokens.Count % 3 != 0)
                throw new InvalidOperationException("Guardian clear rewards require non-empty kind/item/count triples.");
            var items = new List<GuardianAltarClearRewardItem>(tokens.Count / 3);
            for (var offset = 0; offset < tokens.Count; offset += 3)
            {
                var kind = GuardianAltarScriptReader.Quoted(tokens[offset]);
                if (!string.Equals(kind, "normal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Guardian clear reward supports only the normal item kind.");
                var itemId = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                var count = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                if (itemId <= 0 || count <= 0)
                    throw new InvalidOperationException("Guardian clear reward item and count must be positive Int32 values.");
                items.Add(new GuardianAltarClearRewardItem(kind.ToLowerInvariant(), itemId, count));
            }
            return items.AsReadOnly();
        }
    }
}
