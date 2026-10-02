using System;
using System.Collections.Generic;

namespace PvfLib
{
    public sealed class GuardianAltarEnergyLevel
    {
        public int Level { get; }
        public int Capacity { get; }
        public int CapacityFixed { get; }
        public int RegenerationFixedPerTick { get; }

        internal GuardianAltarEnergyLevel(
            int level, int capacity, int capacityFixed, int regenerationFixedPerTick)
        {
            Level = level;
            Capacity = capacity;
            CapacityFixed = capacityFixed;
            RegenerationFixedPerTick = regenerationFixedPerTick;
        }
    }

    /// <summary>
    /// Static Guardian energy rows from [gage up rate]. The initial row's
    /// regeneration value is the fixed-unit scale used by the verified owner.
    /// Tick cadence, elapsed time and current energy belong to the server runtime,
    /// not to an invented PVF tag. This projection does not enable upgrades.
    /// </summary>
    public sealed class GuardianAltarEnergyConfigFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public int Scale { get; private set; }
        public IReadOnlyList<GuardianAltarEnergyLevel> Levels
            { get; private set; } = Array.Empty<GuardianAltarEnergyLevel>();

        public static GuardianAltarEnergyConfigFile Parse(string content)
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

        public static GuardianAltarEnergyConfigFile Parse(GuardianAltarConfigFile config)
        {
            var file = Create(config?.Content, config?.Root);
            if (config?.Root == null || string.IsNullOrWhiteSpace(config.Content))
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

        private static GuardianAltarEnergyConfigFile Create(string content, ScriptNode root)
        {
            return new GuardianAltarEnergyConfigFile
            {
                Content = content ?? string.Empty,
                Root = root ?? new ScriptNode { Tag = "ROOT" },
                InvalidReason = "Guardian Altar energy config is empty or lacks its parsed root",
            };
        }

        private void ParseCore()
        {
            // Only this resource section is projected. Other config/shop fields
            // remain with their existing owner and original Root/Content.
            var node = GuardianAltarScriptReader.SingleChild(Root, "gage up rate");
            GuardianAltarScriptReader.RequireClosed(node);
            GuardianAltarScopedStructureValidator.RequireBalanced(node, Content);
            GuardianAltarScriptReader.RequireLeaf(node);
            var tokens = GuardianAltarScriptReader.Tokens(node, Content);
            if (tokens.Count == 0 || tokens.Count % 3 != 0)
                throw new InvalidOperationException(
                    "Guardian Altar gage up rate requires non-empty level/capacity/regeneration triples");

            var rows = new List<(int Level, int Capacity, int Regeneration)>();
            for (var offset = 0; offset < tokens.Count; offset += 3)
            {
                var level = GuardianAltarScriptReader.Integer(tokens[offset]);
                var capacity = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                var regeneration = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                if (level != rows.Count || capacity <= 0 || regeneration <= 0)
                    throw new InvalidOperationException(
                        "Guardian Altar energy levels must be contiguous from zero with positive capacity and regeneration");
                rows.Add((level, capacity, regeneration));
            }

            var scale = rows[0].Regeneration;
            var levels = new List<GuardianAltarEnergyLevel>(rows.Count);
            foreach (var row in rows)
                levels.Add(new GuardianAltarEnergyLevel(row.Level, row.Capacity,
                    checked(row.Capacity * scale), row.Regeneration));

            // Publish only after every row and fixed-unit product is validated;
            // a malformed later row cannot expose a partial energy definition.
            Scale = scale;
            Levels = levels.AsReadOnly();
            InvalidReason = string.Empty;
        }
    }
}
