using System;
using System.Collections.Generic;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal readonly record struct WeddingClearQuestTarget(int DungeonId, int MinimumDifficulty, int RequiredCount, int ChannelIndex)
    {
        internal bool Matches(int dungeonId, int difficulty) => dungeonId > 0
            && (DungeonId == -1 || DungeonId == dungeonId)
            && (MinimumDifficulty == -1 || difficulty >= MinimumDifficulty);
    }

    // condition-under-clear 22 uses triples, unlike the generic four-value form.
    internal static class WeddingClearQuestDefinition
    {
        internal static bool IsWeddingClear(QuestFile quest) => quest != null
            && QuestData.NormalizeQuestTag(quest.Type) == "condition under clear" && quest.SubType == 22;

        internal static bool CanRepairLegacyDefaultOne(QuestFile quest, uint remaining) => remaining == 1
            && TryParse(quest, out var targets) && targets.Count == 1 && targets[0].RequiredCount > 1;

        internal static bool TryParse(QuestFile quest, out IReadOnlyList<WeddingClearQuestTarget> targets)
        {
            targets = Array.Empty<WeddingClearQuestTarget>();
            if (!IsWeddingClear(quest)) return false;
            var words = (quest.IntData ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 9 || words.Length % 3 != 0) return false;
            var values = new int[words.Length];
            for (int i = 0; i < words.Length; i++)
                if (!int.TryParse(words[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return false;
            var parsed = new List<WeddingClearQuestTarget>();
            for (int i = 0; i < values.Length; i += 3)
            {
                if ((values[i] != -1 && values[i] <= 0) || values[i + 1] < -1 || values[i + 2] is < 1 or > 511) return false;
                parsed.Add(new(values[i], values[i + 1], values[i + 2], i / 3));
            }
            targets = parsed.AsReadOnly();
            return true;
        }
    }
}
