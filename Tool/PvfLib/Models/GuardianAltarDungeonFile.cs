using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PvfLib
{
    public sealed class GuardianAltarDungeonMapEntry
    {
        public string DifficultyName { get; }
        public int MapId { get; }
        public int MapBasisLevel { get; }

        internal GuardianAltarDungeonMapEntry(
            string difficultyName, int mapId, int mapBasisLevel)
        {
            DifficultyName = difficultyName;
            MapId = mapId;
            MapBasisLevel = mapBasisLevel;
        }
    }

    /// <summary>
    /// Guardian-specific DGN resource projection. Registered-map validation,
    /// difficulty authorization and survival admission belong to the server.
    /// </summary>
    public sealed class GuardianAltarDungeonFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public string StageType { get; private set; } = string.Empty;
        public bool IsSurvival => StageType == "survival";
        public IReadOnlyList<GuardianAltarDungeonMapEntry> Maps
            { get; private set; } = Array.Empty<GuardianAltarDungeonMapEntry>();
        public IReadOnlyList<int> SurvivalMapIds
            { get; private set; } = Array.Empty<int>();

        public static GuardianAltarDungeonFile Parse(string content)
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
            catch (Exception ex)
            {
                file.InvalidReason = ex.Message;
            }
            return file;
        }

        public static GuardianAltarDungeonFile Parse(DungeonFile dungeon)
        {
            var file = Create(dungeon?.Content, dungeon?.Root);
            if (dungeon?.Root == null || string.IsNullOrWhiteSpace(dungeon.Content))
                return file;
            try
            {
                file.ParseCore();
                file.IsValid = true;
            }
            catch (Exception ex)
            {
                file.InvalidReason = ex.Message;
            }
            return file;
        }

        private static GuardianAltarDungeonFile Create(string content, ScriptNode root)
        {
            return new GuardianAltarDungeonFile
            {
                Content = content ?? string.Empty,
                Root = root ?? new ScriptNode { Tag = "ROOT" },
                InvalidReason = "Guardian Altar dungeon is empty or lacks its parsed root",
            };
        }

        private void ParseCore()
        {
            // Other ordinary DGN fields remain owned by DungeonFile. Only the
            // Guardian resource namespace is restricted by this projection.
            var guardianTags = new HashSet<string>(new[]
            {
                "advance altar type", "advance altar map", "advance altar clear reward",
                "advance altar survival map", "advance altar survival clear reward",
            }, StringComparer.OrdinalIgnoreCase);
            if (Root.Children.Any(x => x.Tag.StartsWith("advance altar",
                    StringComparison.OrdinalIgnoreCase) && !guardianTags.Contains(x.Tag)))
                throw new InvalidOperationException(
                    "Guardian Altar dungeon contains an unsupported Guardian tag");

            var dungeonType = ReadSingleQuoted(
                GuardianAltarScriptReader.SingleChild(Root, "dungeon type"));
            if (!string.Equals(dungeonType, "advance altar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Dungeon type is not Guardian Altar");
            var stageType = ReadSingleQuoted(
                GuardianAltarScriptReader.SingleChild(Root, "advance altar type"));
            if (!string.Equals(stageType, "normal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(stageType, "survival", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Guardian Altar stage type must be normal or survival");

            var mapsNode = GuardianAltarScriptReader.SingleChild(
                Root, "advance altar map", required: false);
            var survivalNode = GuardianAltarScriptReader.SingleChild(
                Root, "advance altar survival map", required: false);
            var maps = ParseMaps(mapsNode);
            var survivalMaps = ParseSurvivalMaps(survivalNode);
            var isSurvival = string.Equals(stageType, "survival", StringComparison.OrdinalIgnoreCase);
            if (isSurvival)
            {
                if (maps.Count != 0 || survivalMaps.Count == 0)
                    throw new InvalidOperationException(
                        "Guardian Altar survival requires only a non-empty ordered survival map list");
            }
            else if (maps.Count != 3 || survivalMaps.Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar normal stage requires easy, medium and hard maps only");

            StageType = stageType.ToLowerInvariant();
            Maps = maps.AsReadOnly();
            // Repeated survival MAP IDs and their source order are meaningful;
            // do not deduplicate this list or reinterpret it as difficulties.
            SurvivalMapIds = survivalMaps.AsReadOnly();
            InvalidReason = string.Empty;
        }

        private string ReadSingleQuoted(ScriptNode node)
        {
            GuardianAltarScriptReader.RequireLeaf(node);
            var tokens = GuardianAltarScriptReader.Tokens(node, Content);
            if (tokens.Count != 1)
                throw new InvalidOperationException(
                    $"Guardian Altar [{node.Tag}] requires one quoted value");
            return GuardianAltarScriptReader.Quoted(tokens[0]);
        }

        private List<GuardianAltarDungeonMapEntry> ParseMaps(ScriptNode node)
        {
            var result = new List<GuardianAltarDungeonMapEntry>();
            if (node == null)
                return result;
            GuardianAltarScriptReader.RequireClosed(node);
            GuardianAltarScopedStructureValidator.RequireBalanced(node, Content);
            GuardianAltarScriptReader.RequireLeaf(node);
            var tokens = GuardianAltarScriptReader.Tokens(node, Content);
            if (tokens.Count % 3 != 0)
                throw new InvalidOperationException(
                    "Guardian Altar map rows require difficulty, map and basis level");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var offset = 0; offset < tokens.Count; offset += 3)
            {
                var difficulty = GuardianAltarScriptReader.Quoted(tokens[offset]);
                if ((!string.Equals(difficulty, "easy", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(difficulty, "medium", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(difficulty, "hard", StringComparison.OrdinalIgnoreCase))
                    || !seen.Add(difficulty))
                    throw new InvalidOperationException(
                        "Guardian Altar map has an unsupported or duplicate difficulty");
                var mapId = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                var basisLevel = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                if (mapId <= 0 || basisLevel < 0)
                    throw new InvalidOperationException(
                        "Guardian Altar map or basis level is out of range");
                result.Add(new GuardianAltarDungeonMapEntry(
                    difficulty.ToLowerInvariant(), mapId, basisLevel));
            }
            return result;
        }

        private List<int> ParseSurvivalMaps(ScriptNode node)
        {
            var result = new List<int>();
            if (node == null)
                return result;
            GuardianAltarScriptReader.RequireClosed(node);
            GuardianAltarScopedStructureValidator.RequireBalanced(node, Content);
            GuardianAltarScriptReader.RequireLeaf(node);
            foreach (var token in GuardianAltarScriptReader.Tokens(node, Content))
            {
                var mapId = GuardianAltarScriptReader.Integer(token);
                if (mapId <= 0)
                    throw new InvalidOperationException(
                        "Guardian Altar survival map ID must be positive");
                result.Add(mapId);
            }
            return result;
        }
    }

    /// <summary>
    /// Validates closing-tag order only inside selected Guardian sections.
    /// ScriptParser remains the sole source of nodes and resource data.
    /// </summary>
    internal static class GuardianAltarScopedStructureValidator
    {
        internal static void RequireBalanced(ScriptNode node, string content)
        {
            GuardianAltarScriptReader.RequireClosed(node);
            var tags = new Stack<string>();
            var rootSeen = false;
            using var reader = new StringReader(node.GetContent(content));
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var text = line.Trim();
                if (text.Length < 2 || text[0] != '[' || text[text.Length - 1] != ']')
                    continue;
                var tag = text.Substring(1, text.Length - 2);
                if (tag.Length == 0)
                    throw new InvalidOperationException(
                        $"Guardian Altar [{node.Tag}] contains an empty tag");
                if (tag[0] == '/')
                {
                    if (tags.Count == 0 || !string.Equals(tags.Pop(), tag.Substring(1),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Guardian Altar [{node.Tag}] contains a mismatched closing tag");
                }
                else
                {
                    if (tags.Count == 0)
                    {
                        if (rootSeen || !string.Equals(tag, node.Tag, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"Guardian Altar [{node.Tag}] has invalid section boundaries");
                        rootSeen = true;
                    }
                    tags.Push(tag);
                }
            }
            if (!rootSeen || tags.Count != 0)
                throw new InvalidOperationException(
                    $"Guardian Altar [{node.Tag}] has unclosed nested tags");
        }
    }
}
