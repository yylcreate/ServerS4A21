using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class GuardianAltarStageDefinition
    {
        public int AreaId { get; }
        public int DistrictId { get; }
        public int StageOrder { get; }
        public int DungeonId { get; }
        public bool IsSurvival { get; }

        internal GuardianAltarStageDefinition(
            int areaId, int districtId, int stageOrder, int dungeonId,
            bool isSurvival)
        {
            AreaId = areaId;
            DistrictId = districtId;
            StageOrder = stageOrder;
            DungeonId = dungeonId;
            IsSurvival = isSurvival;
        }
    }

    /// <summary>
    /// Static stagemap ordering only. Dungeon authorization, progress and
    /// registered-DGN validation remain with the server catalog/application.
    /// </summary>
    public sealed class GuardianAltarStageMapFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuardianAltarStageDefinition> Stages
            { get; private set; } = Array.Empty<GuardianAltarStageDefinition>();

        public static GuardianAltarStageMapFile Parse(string content)
        {
            var file = new GuardianAltarStageMapFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
            };
            if (string.IsNullOrWhiteSpace(content))
            {
                file.InvalidReason = "Guardian Altar stagemap is empty";
                return file;
            }
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

        private void ParseCore()
        {
            var rootTags = new HashSet<string>(
                new[] { "map image", "area", "name" },
                StringComparer.OrdinalIgnoreCase);
            foreach (var child in Root.Children)
                if (!rootTags.Contains(child.Tag))
                    throw new InvalidOperationException(
                        "Guardian Altar stagemap contains an unsupported root tag");
            if (GuardianAltarScriptReader.Tokens(Root, Content).Count != 0)
                throw new InvalidOperationException(
                    "Guardian Altar stagemap has data outside its root tags");
            var mapImage = GuardianAltarScriptReader.SingleChild(
                Root, "map image", required: false);
            if (mapImage != null)
            {
                GuardianAltarScriptReader.RequireLeaf(mapImage);
                var imageTokens = GuardianAltarScriptReader.Tokens(mapImage, Content);
                if (imageTokens.Count != 2)
                    throw new InvalidOperationException(
                        "Guardian Altar map image requires an image/index pair");
                GuardianAltarScriptReader.Quoted(imageTokens[0]);
                GuardianAltarScriptReader.Integer(imageTokens[1]);
            }
            var name = GuardianAltarScriptReader.SingleChild(
                Root, "name", required: false);
            if (name != null)
            {
                GuardianAltarScriptReader.RequireLeaf(name);
                var nameTokens = GuardianAltarScriptReader.Tokens(name, Content);
                if (nameTokens.Count != 1)
                    throw new InvalidOperationException(
                        "Guardian Altar stagemap name requires one quoted string");
                GuardianAltarScriptReader.Quoted(nameTokens[0]);
            }
            var areaNodes = Root.GetChildren("area");
            if (areaNodes.Count == 0)
                throw new InvalidOperationException(
                    "Guardian Altar stagemap has no area");
            var seenAreas = new HashSet<int>();
            var seenDungeons = new HashSet<int>();
            var stages = new List<GuardianAltarStageDefinition>();
            foreach (var areaNode in areaNodes)
            {
                GuardianAltarScriptReader.RequireClosed(areaNode);
                var allowedTags = new HashSet<string>(
                    new[] { "slot image", "slot type", "district", "append image", "stage", "survival" },
                    StringComparer.OrdinalIgnoreCase);
                foreach (var child in areaNode.Children)
                    if (!allowedTags.Contains(child.Tag))
                        throw new InvalidOperationException(
                            "Guardian Altar area contains an unsupported tag");
                GuardianAltarScriptReader.SingleChild(areaNode, "slot image", required: false);
                GuardianAltarScriptReader.SingleChild(areaNode, "slot type", required: false);
                var areaId = GuardianAltarScriptReader.SingleInteger(areaNode, Content);
                if (areaId < 0 || !seenAreas.Add(areaId))
                    throw new InvalidOperationException(
                        "Guardian Altar stagemap has an invalid or duplicate area");
                var seenDistricts = new HashSet<int>();
                var stagedDistricts = new HashSet<int>();
                var districtId = 0;
                var survivalSeen = false;
                foreach (var node in areaNode.Children)
                {
                    if (string.Equals(node.Tag, "district",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        GuardianAltarScriptReader.RequireLeaf(node);
                        districtId = GuardianAltarScriptReader.SingleInteger(node, Content);
                        if (districtId <= 0 || !seenDistricts.Add(districtId))
                            throw new InvalidOperationException(
                                "Guardian Altar stagemap has an invalid or duplicate district");
                    }
                    else if (string.Equals(node.Tag, "stage",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        GuardianAltarScriptReader.RequireClosed(node);
                        GuardianAltarScriptReader.RequireLeaf(node);
                        var values = GuardianAltarScriptReader.Tokens(node, Content);
                        if (districtId <= 0 || !stagedDistricts.Add(districtId)
                            || values.Count == 0 || values.Count % 4 != 0)
                            throw new InvalidOperationException(
                                "Guardian Altar stage requires one non-empty four-field table per district");
                        for (var offset = 0; offset < values.Count; offset += 4)
                        {
                            var dungeonId = GuardianAltarScriptReader.Integer(values[offset]);
                            // Coordinates and the final flag remain resource data;
                            // none of them defines a difficulty or progress fact.
                            GuardianAltarScriptReader.Integer(values[offset + 1]);
                            GuardianAltarScriptReader.Integer(values[offset + 2]);
                            GuardianAltarScriptReader.Integer(values[offset + 3]);
                            if (dungeonId <= 0 || !seenDungeons.Add(dungeonId))
                                throw new InvalidOperationException(
                                    "Guardian Altar stagemap has an invalid or duplicate dungeon");
                            stages.Add(new GuardianAltarStageDefinition(
                                areaId, districtId, offset / 4, dungeonId, false));
                        }
                    }
                    else if (string.Equals(node.Tag, "survival",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        GuardianAltarScriptReader.RequireClosed(node);
                        GuardianAltarScriptReader.RequireLeaf(node);
                        var values = GuardianAltarScriptReader.Tokens(node, Content);
                        if (survivalSeen || values.Count == 0)
                            throw new InvalidOperationException(
                                "Guardian Altar area has invalid or duplicate survival data");
                        survivalSeen = true;
                        for (var order = 0; order < values.Count; ++order)
                        {
                            var dungeonId = GuardianAltarScriptReader.Integer(values[order]);
                            if (dungeonId <= 0 || !seenDungeons.Add(dungeonId))
                                throw new InvalidOperationException(
                                    "Guardian Altar stagemap has an invalid or duplicate survival dungeon");
                            // [survival] is area-level, not part of the preceding district.
                            stages.Add(new GuardianAltarStageDefinition(
                                areaId, 0, order, dungeonId, true));
                        }
                    }
                }
                if (seenDistricts.Count == 0
                    || !seenDistricts.SetEquals(stagedDistricts))
                    throw new InvalidOperationException(
                        "Guardian Altar district lacks its stage table");
            }
            Stages = stages.AsReadOnly();
        }
    }
}
