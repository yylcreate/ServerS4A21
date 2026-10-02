using System;
using System.Collections.Generic;
using System.Linq;

namespace PvfLib
{
    public sealed class GuardianAltarTimelinePhase
    {
        public int DurationMilliseconds { get; }
        public string Type { get; }
        public IReadOnlyList<int> ActionAssignments { get; }

        internal GuardianAltarTimelinePhase(
            int durationMilliseconds, string type, List<int> actionAssignments)
        {
            DurationMilliseconds = durationMilliseconds;
            Type = type;
            ActionAssignments = actionAssignments.AsReadOnly();
        }
    }

    /// <summary>
    /// Guardian-specific MAP projection over the existing ScriptParser tree.
    /// Static monster and passive-object records remain owned by MapFile.
    /// </summary>
    public sealed class GuardianAltarMapFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<int> CenterIndices
            { get; private set; } = Array.Empty<int>();
        public IReadOnlyList<GuardianAltarTimelinePhase> Phases
            { get; private set; } = Array.Empty<GuardianAltarTimelinePhase>();
        public int TotalDurationMilliseconds { get; private set; }

        public static GuardianAltarMapFile Parse(string content)
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

        public static GuardianAltarMapFile Parse(MapFile map)
        {
            var file = Create(map?.Content, map?.Root);
            if (map?.Root == null || string.IsNullOrWhiteSpace(map.Content))
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

        private static GuardianAltarMapFile Create(string content, ScriptNode root)
        {
            return new GuardianAltarMapFile
            {
                Content = content ?? string.Empty,
                Root = root ?? new ScriptNode { Tag = "ROOT" },
                InvalidReason = "Guardian Altar map is empty or lacks its parsed root",
            };
        }

        private void ParseCore()
        {
            var centerNode = GuardianAltarScriptReader.SingleChild(Root, "center index");
            GuardianAltarScriptReader.RequireClosed(centerNode);
            GuardianAltarScopedStructureValidator.RequireBalanced(centerNode, Content);
            GuardianAltarScriptReader.RequireLeaf(centerNode);
            var centers = new List<int>();
            var seen = new HashSet<int>();
            foreach (var token in GuardianAltarScriptReader.Tokens(centerNode, Content))
            {
                var index = GuardianAltarScriptReader.Integer(token);
                if (index <= 0 || !seen.Add(index))
                    throw new InvalidOperationException(
                        "Guardian Altar center index is invalid or duplicated");
                centers.Add(index);
            }
            if (centers.Count == 0)
                throw new InvalidOperationException(
                    "Guardian Altar map requires at least one center index");

            var timelineNode = GuardianAltarScriptReader.SingleChild(Root, "time line");
            GuardianAltarScriptReader.RequireClosed(timelineNode);
            GuardianAltarScopedStructureValidator.RequireBalanced(timelineNode, Content);
            if (GuardianAltarScriptReader.Tokens(timelineNode, Content).Count != 0
                || timelineNode.Children.Any(x => !string.Equals(
                    x.Tag, "phase", StringComparison.OrdinalIgnoreCase))
                || timelineNode.Children.Count == 0)
                throw new InvalidOperationException(
                    "Guardian Altar time line requires a non-empty phase list only");
            var phases = new List<GuardianAltarTimelinePhase>();
            var totalDuration = 0;
            foreach (var node in timelineNode.Children)
            {
                var phase = ParsePhase(node);
                totalDuration = checked(totalDuration + phase.DurationMilliseconds);
                phases.Add(phase);
            }

            CenterIndices = centers.AsReadOnly();
            Phases = phases.AsReadOnly();
            TotalDurationMilliseconds = totalDuration;
            InvalidReason = string.Empty;
        }

        private GuardianAltarTimelinePhase ParsePhase(ScriptNode node)
        {
            GuardianAltarScriptReader.RequireClosed(node);
            var tags = new HashSet<string>(new[]
            {
                "duration", "type", "action assign",
            }, StringComparer.OrdinalIgnoreCase);
            if (GuardianAltarScriptReader.Tokens(node, Content).Count != 0
                || node.Children.Any(x => !tags.Contains(x.Tag)))
                throw new InvalidOperationException(
                    "Guardian Altar phase contains unsupported data");
            var durationNode = RequiredPhaseField(node, "duration");
            var duration = GuardianAltarScriptReader.SingleInteger(durationNode, Content);
            if (duration <= 0)
                throw new InvalidOperationException(
                    "Guardian Altar phase duration must be positive");
            var typeNode = RequiredPhaseField(node, "type");
            var typeTokens = GuardianAltarScriptReader.Tokens(typeNode, Content);
            if (typeTokens.Count != 1)
                throw new InvalidOperationException(
                    "Guardian Altar phase type requires one quoted value");
            var type = GuardianAltarScriptReader.Quoted(typeTokens[0]);
            if (!string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "named", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "boss", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Guardian Altar phase type must be normal, named or boss");

            var actionNode = RequiredPhaseField(node, "action assign");
            var actionTokens = GuardianAltarScriptReader.Tokens(actionNode, Content);
            if (actionTokens.Count != 3)
                throw new InvalidOperationException(
                    "Guardian Altar phase action assign requires three integers");
            var assignments = actionTokens.Select(GuardianAltarScriptReader.Integer).ToList();
            // Keep all three resource positions, including -1 sentinels. The
            // projection does not choose a spawn action or schedule a phase.
            return new GuardianAltarTimelinePhase(
                duration, type.ToLowerInvariant(), assignments);
        }

        private static ScriptNode RequiredPhaseField(ScriptNode phase, string tag)
        {
            var node = GuardianAltarScriptReader.SingleChild(phase, tag);
            GuardianAltarScriptReader.RequireClosed(node);
            GuardianAltarScriptReader.RequireLeaf(node);
            return node;
        }
    }
}
