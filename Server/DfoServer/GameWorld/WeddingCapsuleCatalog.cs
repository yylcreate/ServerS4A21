using PvfLib;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal sealed record WeddingCapsuleCatalog(byte MaximumOwn, byte MaximumTaken, int GaugePerClear, int MaximumGauge, int ItemId)
    {
        internal const string PvfPath = "etc/couplegrowthcapsule.etc";
        private static readonly Lazy<WeddingCapsuleCatalog> Current = new(() => Parse(PvfArchiveAccessor.ReadText(PvfPath)));
        internal static WeddingCapsuleCatalog Get() => Current.Value;

        internal static WeddingCapsuleCatalog Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            var root = new ScriptParser().Parse(text);
            int Read(string tag)
            {
                var nodes = root.GetChildren(tag);
                if (nodes.Count != 1 || nodes[0].Children.Count != 0) throw Bad(tag);
                var values = nodes[0].DataItems.SelectMany(x => ScriptValueTokenizer.Tokenize(x.GetContent(text))).ToArray();
                if (values.Length != 1 || !int.TryParse(values[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value) || value <= 0) throw Bad(tag);
                return value;
            }
            int own = Read("max own count"), taken = Read("max taken count");
            int gauge = Read("gage"), maximum = Read("max gage"), item = Read("capsule id");
            if (own > byte.MaxValue || taken > byte.MaxValue || gauge > maximum) throw Bad("limits");
            return new((byte)own, (byte)taken, gauge, maximum, item);
        }

        private static InvalidDataException Bad(string tag) => new($"{PvfPath}: invalid [{tag}]");
    }
}
