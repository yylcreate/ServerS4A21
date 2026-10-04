using System;
using System.Collections.Generic;
using System.Linq;

namespace PvfLib
{
    public sealed class GuardianAltarSummonObject
    {
        public int Id { get; }
        public bool IsEnemy { get; }
        public string Type { get; }
        public int PrototypeIndex { get; }
        public int Level { get; }
        public int RawParameter { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        internal GuardianAltarSummonObject(int id, bool isEnemy, string type,
            int prototypeIndex, int level, int rawParameter, int x, int y, int z)
        {
            Id = id;
            IsEnemy = isEnemy;
            Type = type;
            PrototypeIndex = prototypeIndex;
            Level = level;
            RawParameter = rawParameter;
            X = x;
            Y = y;
            Z = z;
        }
    }

    public sealed class GuardianAltarSummonActionEntry
    {
        public int SummonObjectId { get; }
        public int FirstDelayMilliseconds { get; }
        public int RepeatDelayMilliseconds { get; }
        // Zero means periodic summons without a configured count limit.
        public int MaximumCount { get; }

        internal GuardianAltarSummonActionEntry(int summonObjectId, int firstDelayMilliseconds,
            int repeatDelayMilliseconds, int maximumCount)
        {
            SummonObjectId = summonObjectId;
            FirstDelayMilliseconds = firstDelayMilliseconds;
            RepeatDelayMilliseconds = repeatDelayMilliseconds;
            MaximumCount = maximumCount;
        }
    }

    public sealed class GuardianAltarSummonAction
    {
        public int Id { get; }
        public string Type { get; }
        public IReadOnlyList<GuardianAltarSummonActionEntry> Entries { get; }

        internal GuardianAltarSummonAction(int id, string type,
            List<GuardianAltarSummonActionEntry> entries)
        {
            Id = id;
            Type = type;
            Entries = entries.AsReadOnly();
        }
    }

    /// <summary>
    /// Current Guardian summon object and action definitions. This resource
    /// reader preserves source order; phase choice, clocks, sequence allocation
    /// and spawning belong to the instance runtime, not this static model.
    /// </summary>
    public sealed class GuardianAltarSummonDataFile : PvfModelBase
    {
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;
        public IReadOnlyList<GuardianAltarSummonObject> Objects
            { get; private set; } = Array.Empty<GuardianAltarSummonObject>();
        public IReadOnlyList<GuardianAltarSummonAction> Actions
            { get; private set; } = Array.Empty<GuardianAltarSummonAction>();

        public static GuardianAltarSummonDataFile Parse(string content)
        {
            var file = new GuardianAltarSummonDataFile
            {
                Content = content ?? string.Empty,
                Root = new ScriptNode { Tag = "ROOT" },
                InvalidReason = "Guardian Altar summon definition is empty",
            };
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

        private void ParseCore()
        {
            if (Root.Children.Any(x => !string.Equals(x.Tag, "summon object", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(x.Tag, "action define", StringComparison.OrdinalIgnoreCase))
                || GuardianAltarScriptReader.Tokens(Root, Content).Count != 0)
                throw new InvalidOperationException("Guardian Altar summon definition contains unsupported root data");
            var objectNode = GuardianAltarScriptReader.SingleChild(Root, "summon object");
            GuardianAltarScriptReader.RequireClosed(objectNode);
            GuardianAltarScopedStructureValidator.RequireBalanced(objectNode, Content);
            GuardianAltarScriptReader.RequireLeaf(objectNode);
            var objectTokens = GuardianAltarScriptReader.Tokens(objectNode, Content);
            if (objectTokens.Count == 0 || objectTokens.Count % 9 != 0)
                throw new InvalidOperationException("Guardian Altar summon objects require non-empty nine-field rows");
            var objects = new List<GuardianAltarSummonObject>();
            var objectIds = new HashSet<int>();
            for (var offset = 0; offset < objectTokens.Count; offset += 9)
            {
                var id = GuardianAltarScriptReader.Integer(objectTokens[offset]);
                var side = GuardianAltarScriptReader.Quoted(objectTokens[offset + 1]).ToLowerInvariant();
                var type = ReadType(GuardianAltarScriptReader.Quoted(objectTokens[offset + 2]));
                var prototypeIndex = GuardianAltarScriptReader.Integer(objectTokens[offset + 3]);
                var level = GuardianAltarScriptReader.Integer(objectTokens[offset + 4]);
                var rawParameter = GuardianAltarScriptReader.Integer(objectTokens[offset + 5]);
                var x = GuardianAltarScriptReader.Integer(objectTokens[offset + 6]);
                var y = GuardianAltarScriptReader.Integer(objectTokens[offset + 7]);
                var z = GuardianAltarScriptReader.Integer(objectTokens[offset + 8]);
                if (id <= 0 || !objectIds.Add(id) || prototypeIndex <= 0
                    || level <= 0 || level > ushort.MaxValue || (side != "enemy" && side != "friend"))
                    throw new InvalidOperationException("Guardian Altar summon object has invalid or duplicate fields");
                // Column six is preserved as a resource integer. Its business
                // name is not proven by the current consumer; do not invent one.
                objects.Add(new GuardianAltarSummonObject(id, side == "enemy", type,
                    prototypeIndex, level, rawParameter, x, y, z));
            }

            var actionsNode = GuardianAltarScriptReader.SingleChild(Root, "action define");
            GuardianAltarScriptReader.RequireClosed(actionsNode);
            GuardianAltarScopedStructureValidator.RequireBalanced(actionsNode, Content);
            if (actionsNode.Children.Count == 0 || GuardianAltarScriptReader.Tokens(actionsNode, Content).Count != 0)
                throw new InvalidOperationException("Guardian Altar action definitions require a non-empty action list only");
            var actions = new List<GuardianAltarSummonAction>();
            var actionIds = new HashSet<int>();
            foreach (var node in actionsNode.Children)
            {
                var type = ReadType(node.Tag);
                GuardianAltarScriptReader.RequireClosed(node);
                GuardianAltarScriptReader.RequireLeaf(node);
                var tokens = GuardianAltarScriptReader.Tokens(node, Content);
                if (tokens.Count < 5 || (tokens.Count - 1) % 4 != 0)
                    throw new InvalidOperationException("Guardian Altar action requires an ID and non-empty four-field entries");
                var id = GuardianAltarScriptReader.Integer(tokens[0]);
                if (id <= 0 || !actionIds.Add(id))
                    throw new InvalidOperationException("Guardian Altar action ID is invalid or duplicated");
                var entries = new List<GuardianAltarSummonActionEntry>();
                for (var offset = 1; offset < tokens.Count; offset += 4)
                {
                    var objectId = GuardianAltarScriptReader.Integer(tokens[offset]);
                    var firstDelay = GuardianAltarScriptReader.Integer(tokens[offset + 1]);
                    var repeatDelay = GuardianAltarScriptReader.Integer(tokens[offset + 2]);
                    var maximumCount = GuardianAltarScriptReader.Integer(tokens[offset + 3]);
                    if (!objectIds.Contains(objectId) || firstDelay < 0 || repeatDelay < 500
                        || maximumCount < 0 || maximumCount > short.MaxValue)
                        throw new InvalidOperationException("Guardian Altar action has an unknown object or invalid delay/count");
                    entries.Add(new GuardianAltarSummonActionEntry(objectId, firstDelay, repeatDelay, maximumCount));
                }
                actions.Add(new GuardianAltarSummonAction(id, type, entries));
            }

            // Publish together only after every later row, reference and bound
            // is validated; a broken final action must expose neither list.
            Objects = objects.AsReadOnly();
            Actions = actions.AsReadOnly();
            InvalidReason = string.Empty;
        }

        private static string ReadType(string type)
        {
            var normalized = type.ToLowerInvariant();
            if (normalized != "normal" && normalized != "named" && normalized != "boss")
                throw new InvalidOperationException("Guardian Altar summon type must be normal, named or boss");
            return normalized;
        }
    }
}
