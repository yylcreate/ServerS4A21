using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DfoServer.GameWorld
{
    internal sealed record WeddingItemCost(int ItemId, int Count);
    internal sealed record WeddingCeremonyRule(int Gold, byte Ring, int Invitations, WeddingItemCost Gift);
    internal sealed record WeddingGuestGiftTier(int MaximumGold, IReadOnlyDictionary<byte, IReadOnlyList<WeddingItemCost>> Gifts);

    // Rules belong to the current A21 PVF. Reference-server constants are not defaults.
    internal sealed class WeddingCatalog
    {
        internal const string PvfPath = "etc/weddingsystem.etc";
        private static readonly Lazy<WeddingCatalog> Current = new(() => Parse(PvfArchiveAccessor.ReadText(PvfPath)));
        internal static WeddingCatalog Get() => Current.Value;

        internal int MinimumLevel { get; private init; }
        internal int RemarryDays { get; private init; }
        internal int WeddingCooldownDays { get; private init; }
        internal int MaximumLove { get; private init; }
        internal int LovePerClear { get; private init; }
        internal int CeremonyEndSeconds { get; private init; }
        internal int GiftMinimumGold { get; private init; }
        internal int GiftMaximumGold { get; private init; }
        internal int GiftChargePercent { get; private init; }
        internal byte PaidCeremonyType { get; private init; }
        internal WeddingItemCost PaidTicket { get; private init; }
        internal IReadOnlyList<int> RequiredQuests { get; private init; }
        internal IReadOnlyList<int> Alimony { get; private init; }
        internal IReadOnlyList<int> ReservationDelaySeconds { get; private init; }
        internal IReadOnlyList<int> CeremonyPositions { get; private init; }
        internal IReadOnlyList<WeddingCeremonyRule> Ceremonies { get; private init; }
        internal IReadOnlyList<WeddingItemCost> RingTickets { get; private init; }
        internal IReadOnlyList<WeddingItemCost> CeremonyGifts { get; private init; }
        internal IReadOnlyDictionary<int, int> LoveItems { get; private init; }
        internal IReadOnlyList<WeddingGuestGiftTier> GuestGifts { get; private init; }

        internal static WeddingCatalog Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            var root = new ScriptParser().Parse(text);
            int[] Field(string tag, int count)
            {
                var matches = root.GetChildren(tag);
                if (matches.Count != 1) throw Bad(tag);
                var values = Numbers(matches[0], text);
                if (count >= 0 && values.Length != count) throw Bad(tag);
                return values;
            }
            var ceremonies = Field("ceremony", 12);
            var gifts = Field("wedding gift", 9);
            var tickets = Field("upgrade ring ticket", 12);
            var loveItems = Field("love point item", -1);
            var ceremonyGifts = Field("ceremony gift", -1);
            var giftLimits = Field("money gift limit", 2);
            var paid = Field("charged wedding ticket", 3);
            var rules = new List<WeddingCeremonyRule>();
            var ringTickets = new List<WeddingItemCost>();
            for (int i = 0; i < 3; i++)
            {
                if (ceremonies[i * 4] != i || gifts[i * 3] != i || ceremonies[i * 4 + 2] > 5
                    || ceremonies[i * 4 + 3] > ushort.MaxValue) throw Bad("ceremony/wedding gift");
                rules.Add(new WeddingCeremonyRule(ceremonies[i * 4 + 1], (byte)ceremonies[i * 4 + 2],
                    ceremonies[i * 4 + 3], PositiveCost(gifts[i * 3 + 1], gifts[i * 3 + 2], "wedding gift")));
            }
            for (int i = 0; i < 6; i++)
                ringTickets.Add(i == 0 && tickets[0] == 0 && tickets[1] == 0
                    ? new WeddingItemCost(0, 0) : PositiveCost(tickets[i * 2], tickets[i * 2 + 1], "upgrade ring ticket"));
            var love = new Dictionary<int, int>();
            if (loveItems.Length % 2 != 0) throw Bad("love point item");
            for (int i = 0; i < loveItems.Length; i += 2)
                if (loveItems[i] <= 0 || loveItems[i + 1] <= 0 || !love.TryAdd(loveItems[i], loveItems[i + 1]))
                    throw Bad("love point item");
            var extraGifts = new List<WeddingItemCost>();
            if (ceremonyGifts.Length % 2 != 0) throw Bad("ceremony gift");
            for (int i = 0; i < ceremonyGifts.Length; i += 2)
                extraGifts.Add(PositiveCost(ceremonyGifts[i], ceremonyGifts[i + 1], "ceremony gift"));
            var guestTiers = new List<WeddingGuestGiftTier>();
            int previousLimit = -1;
            foreach (var node in root.GetChildren("guest gift"))
            {
                var values = Numbers(node, text);
                if (values.Length < 4 || (values.Length - 1) % 3 != 0 || values[0] <= previousLimit)
                    throw Bad("guest gift");
                var byCeremony = new Dictionary<byte, List<WeddingItemCost>>();
                for (int i = 1; i < values.Length; i += 3)
                {
                    if (values[i] > 2) throw Bad("guest gift");
                    var kind = (byte)values[i];
                    if (!byCeremony.TryGetValue(kind, out var items)) byCeremony[kind] = items = new();
                    items.Add(PositiveCost(values[i + 1], values[i + 2], "guest gift"));
                }
                guestTiers.Add(new(values[0], new ReadOnlyDictionary<byte, IReadOnlyList<WeddingItemCost>>(
                    byCeremony.ToDictionary(x => x.Key, x => (IReadOnlyList<WeddingItemCost>)x.Value.AsReadOnly()))));
                previousLimit = values[0];
            }
            var result = new WeddingCatalog
            {
                MinimumLevel = Field("minimum level", 1)[0],
                RemarryDays = Field("days after divorce", 1)[0],
                WeddingCooldownDays = Field("once how many days", 1)[0],
                MaximumLove = Field("max love point", 1)[0],
                LovePerClear = Field("love point", 1)[0],
                CeremonyEndSeconds = Field("noti end", 1)[0],
                GiftMinimumGold = giftLimits[0], GiftMaximumGold = giftLimits[1],
                GiftChargePercent = Field("money gift charge", 1)[0],
                PaidCeremonyType = checked((byte)paid[0]),
                PaidTicket = PositiveCost(paid[1], paid[2], "charged wedding ticket"),
                RequiredQuests = Array.AsReadOnly(Field("quest", 2)),
                Alimony = Array.AsReadOnly(Field("alimony", 2)),
                ReservationDelaySeconds = Array.AsReadOnly(Field("minute selection", 3)),
                CeremonyPositions = Array.AsReadOnly(Field("force move", 6)),
                Ceremonies = rules.AsReadOnly(), RingTickets = ringTickets.AsReadOnly(),
                CeremonyGifts = extraGifts.AsReadOnly(), LoveItems = new ReadOnlyDictionary<int, int>(love),
                GuestGifts = guestTiers.AsReadOnly(),
            };
            // C8AB55 enforces level 30 in the native request UI; its level reader is signed byte.
            if (result.MinimumLevel is < 30 or > sbyte.MaxValue || result.MaximumLove <= 0
                || result.CeremonyEndSeconds <= 0 || result.PaidCeremonyType > 2 || result.GiftChargePercent > 100
                || result.GiftMinimumGold > result.GiftMaximumGold || result.RequiredQuests.Any(x => x is < 1 or > ushort.MaxValue)
                || result.ReservationDelaySeconds.Any(x => x <= 0) || result.CeremonyPositions.Any(x => x > short.MaxValue)
                || guestTiers.Count == 0 || guestTiers[^1].MaximumGold < result.GiftMaximumGold)
                throw Bad("rules");
            return result;
        }

        private static WeddingItemCost PositiveCost(int id, int count, string tag)
            => id > 0 && count > 0 ? new(id, count) : throw Bad(tag);

        private static int[] Numbers(ScriptNode node, string text)
        {
            if (!node.HasEndTag || node.Children.Count != 0) throw Bad(node.Tag);
            var tokens = node.DataItems.SelectMany(x => ScriptValueTokenizer.Tokenize(x.GetContent(text))).ToArray();
            var values = new int[tokens.Length];
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                    throw Bad(node.Tag);
            return values;
        }

        private static InvalidDataException Bad(string tag) => new($"{PvfPath}: invalid [{tag}]");
    }
}
