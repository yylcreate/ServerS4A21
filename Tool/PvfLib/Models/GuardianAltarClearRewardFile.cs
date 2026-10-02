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

    /// <summary>
    /// Read-only ordinary Guardian clear-reward projection. Stage completion,
    /// difficulty admission and inventory grants belong to their server owners.
    /// Achievement and survival rewards are outside this section's contract.
    /// </summary>
    public sealed class GuardianAltarClearRewardFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuardianAltarDifficultyClearReward> Difficulties
            { get; private set; } = Array.Empty<GuardianAltarDifficultyClearReward>();

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
                if (tokens.Count == 0 || tokens.Count % 3 != 0)
                    throw new InvalidOperationException(
                        "Guardian Altar clear rewards require non-empty kind/item/count triples");
                var items = new List<GuardianAltarClearRewardItem>(tokens.Count / 3);
                for (var offset = 0; offset < tokens.Count; offset += 3)
                {
                    var kind = GuardianAltarScriptReader.Quoted(tokens[offset]);
                    if (!string.Equals(kind, "normal", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Guardian Altar ordinary clear reward supports only the normal item kind");
                    var itemId = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                    var count = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                    if (itemId <= 0 || count <= 0)
                        throw new InvalidOperationException(
                            "Guardian Altar clear reward item and count must be positive Int32 values");
                    items.Add(new GuardianAltarClearRewardItem(kind.ToLowerInvariant(), itemId, count));
                }
                difficulties.Add(new GuardianAltarDifficultyClearReward(
                    child.Tag.ToLowerInvariant(), items.AsReadOnly()));
            }

            // No earlier difficulty becomes observable if a later row is invalid.
            Difficulties = difficulties.AsReadOnly();
            InvalidReason = string.Empty;
        }
    }
}
