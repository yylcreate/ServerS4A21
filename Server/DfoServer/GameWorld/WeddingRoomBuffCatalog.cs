using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal sealed record WeddingRoomBuff(int HpMax = 0, int MpMax = 0, int VillageMovingSpeedPercent = 0,
        int AllElementalAttack = 0, int EquipmentPhysicalAttack = 0, int EquipmentMagicalAttack = 0, int SeparateAttack = 0);
    internal sealed record WeddingRoomBuffTier(int MinimumDecoration, WeddingRoomBuff Buff);

    internal sealed class WeddingRoomBuffCatalog
    {
        private static readonly Lazy<WeddingRoomBuffCatalog> Current = new(() => Parse(
            PvfArchiveAccessor.ReadText(WeddingRoomCatalog.FurniturePath)));
        internal static WeddingRoomBuffCatalog Get() => Current.Value;
        internal IReadOnlyList<WeddingRoomBuffTier> Tiers { get; private init; }

        internal WeddingRoomBuff Evaluate(int decoration)
        {
            if (decoration < 0) throw new ArgumentOutOfRangeException(nameof(decoration));
            WeddingRoomBuff result = new();
            foreach (var tier in Tiers)
            {
                if (decoration < tier.MinimumDecoration) break;
                result = tier.Buff;
            }
            return result;
        }

        internal static WeddingRoomBuffCatalog Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            var nodes = new ScriptParser().Parse(text).GetChildren("couple room buff");
            if (nodes.Count != 1 || !nodes[0].HasEndTag || nodes[0].Children.Count != 0) throw Bad("section");
            var tokens = ScriptValueTokenizer.Tokenize(string.Join("\n", nodes[0].DataItems.Select(x => x.GetContent(text)))).ToArray();
            var tiers = new List<WeddingRoomBuffTier>();
            int cursor = 0, previous = -1;
            int Read()
            {
                if (cursor >= tokens.Length || !int.TryParse(tokens[cursor++], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value) || value < 0) throw Bad("integer");
                return value;
            }
            while (cursor < tokens.Length)
            {
                int threshold = Read(), count = Read();
                if (threshold <= previous || count is < 1 or > 7) throw Bad("tier");
                var buff = new WeddingRoomBuff();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    if (cursor >= tokens.Length) throw Bad("effect");
                    string effect = tokens[cursor++];
                    if (!seen.Add(effect)) throw Bad("duplicate effect");
                    int value = Read();
                    buff = effect switch
                    {
                        "hp max" => buff with { HpMax = value },
                        "mp max" => buff with { MpMax = value },
                        "village moving speed" => buff with { VillageMovingSpeedPercent = value },
                        "all elemental attack" => buff with { AllElementalAttack = value },
                        "equipment physical attack" => buff with { EquipmentPhysicalAttack = value },
                        "equipment magical attack" => buff with { EquipmentMagicalAttack = value },
                        "separate attack" => buff with { SeparateAttack = value },
                        _ => throw Bad("unsupported effect " + effect),
                    };
                }
                tiers.Add(new(threshold, buff)); previous = threshold;
            }
            if (tiers.Count == 0) throw Bad("empty tiers");
            return new WeddingRoomBuffCatalog { Tiers = tiers.AsReadOnly() };
        }

        private static InvalidDataException Bad(string field) => new($"{WeddingRoomCatalog.FurniturePath}: invalid [couple room buff] {field}.");
    }
}
