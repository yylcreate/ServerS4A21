using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Buffs
{
    internal sealed record VariousBuffEffect(string Name, IReadOnlyList<int> Parameters)
    {
        internal IReadOnlyList<IReadOnlyList<int>> ParameterBlocks { get; init; }
            = Array.AsReadOnly(Array.Empty<IReadOnlyList<int>>());
    }
    internal sealed record VariousBuffDefinition(int Index, string SystemGroup, IReadOnlyList<VariousBuffEffect> Effects);

    internal static class VariousBuffCatalog
    {
        private static readonly object Sync = new object();
        private static Dictionary<int, VariousBuffDefinition> _cached;
        internal static VariousBuffDefinition Resolve(int index)
            => index > 0 && Load().TryGetValue(index, out var row) ? row : null;
        internal static IReadOnlyCollection<VariousBuffDefinition> All => Load().Values;

        private static Dictionary<int, VariousBuffDefinition> Load()
        {
            lock (Sync)
                return _cached ??= Parse(PvfArchiveAccessor.ReadText("etc/variousbufflist.etc"));
        }

        internal static Dictionary<int, VariousBuffDefinition> Parse(string text)
        {
            var file = VariousBuffConfigFile.Parse(text);
            if (!file.IsValid) throw new InvalidOperationException(file.InvalidReason);
            return file.Definitions.ToDictionary(row => row.Index, row => new VariousBuffDefinition(row.Index,
                row.SystemGroup, Array.AsReadOnly(row.Effects.Select(effect =>
                    new VariousBuffEffect(effect.Name, effect.Parameters)
                    { ParameterBlocks = effect.ParameterBlocks }).ToArray())));
        }

        internal static void ResetCacheForTests() { lock (Sync) _cached = null; }
    }
}
