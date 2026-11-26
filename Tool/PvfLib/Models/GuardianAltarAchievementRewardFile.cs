using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class GuardianAltarAchievementReward
    {
        internal GuardianAltarAchievementReward(int type, byte difficulty, int clearCount,
            int iconImage, IReadOnlyList<GuardianAltarClearRewardItem> items,
            string rewardTitle, string postalTitle, string postalLetter)
        {
            Type = type;
            Difficulty = difficulty;
            ClearCount = clearCount;
            IconImage = iconImage;
            Items = items;
            RewardTitle = rewardTitle;
            PostalTitle = postalTitle;
            PostalLetter = postalLetter;
        }

        public int Type { get; }
        public byte Difficulty { get; }
        public int ClearCount { get; }
        public int IconImage { get; }
        public IReadOnlyList<GuardianAltarClearRewardItem> Items { get; }
        public string RewardTitle { get; }
        public string PostalTitle { get; }
        public string PostalLetter { get; }
    }

    // The PVF owns conditions, item triples and localized postal text. Claim
    // authorization and delivery are performed by the durable progress owner.
    public sealed class GuardianAltarAchievementRewardFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuardianAltarAchievementReward> Rewards { get; private set; }
            = Array.Empty<GuardianAltarAchievementReward>();

        public static GuardianAltarAchievementRewardFile Parse(string content, string textTagContent)
        {
            var file = new GuardianAltarAchievementRewardFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            try
            {
                // Reuse the existing structural/ID contract instead of relaxing
                // malformed persisted achievements to support reward delivery.
                GuardianAltarConfigFile.ParseAchievementIds(content);
                file.Root = new ScriptParser().Parse(content);
                file.ParseCore(textTagContent);
                file.IsValid = true;
            }
            catch (Exception exception)
            {
                file.InvalidReason = exception.Message;
            }
            return file;
        }

        private void ParseCore(string textTagContent)
        {
            var textTags = ReadTextTags(textTagContent);
            var rewards = new List<GuardianAltarAchievementReward>();
            foreach (var row in Root.GetChildren("reward data"))
            {
                var type = GuardianAltarScriptReader.SingleInteger(
                    GuardianAltarScriptReader.SingleChild(row, "type"), Content);
                var difficultyName = ReadString(row, "stage difficulty");
                var difficulty = difficultyName switch
                {
                    "easy" => (byte)0,
                    "medium" => (byte)1,
                    "hard" => (byte)2,
                    _ => throw new InvalidOperationException("Guardian achievement difficulty is unsupported."),
                };
                var clearCount = GuardianAltarScriptReader.SingleInteger(
                    GuardianAltarScriptReader.SingleChild(row, "stage clear count"), Content);
                var icon = GuardianAltarScriptReader.SingleInteger(
                    GuardianAltarScriptReader.SingleChild(row, "icon image"), Content);
                var itemNode = GuardianAltarScriptReader.SingleChild(row, "reward item");
                GuardianAltarScriptReader.RequireClosed(itemNode);
                GuardianAltarScriptReader.RequireLeaf(itemNode);
                GuardianAltarScopedStructureValidator.RequireBalanced(itemNode, Content);
                var tokens = GuardianAltarScriptReader.Tokens(itemNode, Content);
                if (tokens.Count == 0 || tokens.Count % 3 != 0)
                    throw new InvalidOperationException("Guardian achievement rewards require kind/item/count triples.");
                var items = new List<GuardianAltarClearRewardItem>();
                for (var offset = 0; offset < tokens.Count; offset += 3)
                {
                    var kind = GuardianAltarScriptReader.Quoted(tokens[offset]);
                    var item = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                    var count = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                    if (kind != "normal" || item <= 0 || count <= 0)
                        throw new InvalidOperationException("Guardian achievement item triples are invalid or unsupported.");
                    items.Add(new GuardianAltarClearRewardItem(kind, item, count));
                }
                rewards.Add(new GuardianAltarAchievementReward(type, difficulty, clearCount, icon,
                    items.AsReadOnly(), ResolveText(textTags, ReadString(row, "reward title")),
                    ResolveText(textTags, ReadString(row, "postal title")),
                    ResolveText(textTags, ReadString(row, "postal letter"))));
            }
            Rewards = rewards.AsReadOnly();
        }

        private string ReadString(ScriptNode row, string tag)
        {
            var node = GuardianAltarScriptReader.SingleChild(row, tag);
            GuardianAltarScriptReader.RequireLeaf(node);
            var tokens = GuardianAltarScriptReader.Tokens(node, Content);
            if (tokens.Count != 1)
                throw new InvalidOperationException($"Guardian achievement [{tag}] needs one string key.");
            return GuardianAltarScriptReader.Quoted(tokens[0]);
        }

        private static Dictionary<string, string> ReadTextTags(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Guardian postal text resource is missing.");
            var root = new ScriptParser().Parse(content);
            GuardianAltarScriptReader.RequireLeaf(root);
            // This text resource has multiline backtick values. ScriptParser's
            // line-scoped DataItems cannot individually tokenize an open quote;
            // reuse the strict token reader over one full resource data span.
            var textSpan = new ScriptNode { Tag = "postal text" };
            textSpan.DataItems.Add(new ScriptDataItem { StartIndex = 0, EndIndex = content.Length });
            var tokens = GuardianAltarScriptReader.Tokens(textSpan, content);
            if (tokens.Count == 0 || tokens.Count % 2 != 0)
                throw new InvalidOperationException("Guardian postal text requires quoted key/value pairs.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var offset = 0; offset < tokens.Count; offset += 2)
            {
                var key = GuardianAltarScriptReader.Quoted(tokens[offset]);
                var value = GuardianAltarScriptReader.Quoted(tokens[offset + 1]);
                if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, value))
                    throw new InvalidOperationException("Guardian postal text key is empty or ambiguous.");
            }
            return result;
        }

        private static string ResolveText(IReadOnlyDictionary<string, string> tags, string key)
            => tags.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text : throw new InvalidOperationException($"Guardian reward text key is not defined: {key}");
    }
}
