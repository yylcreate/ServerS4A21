using DfoServer.GameWorld;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class WeddingCatalogSelfTest
    {
        internal static int Run()
        {
            int failures = 0;
            void Check(string label, bool ok)
            {
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}");
                if (!ok) failures++;
            }
            void Reject(string label, string text)
            {
                try { WeddingCatalog.Parse(text); Check(label, false); }
                catch (InvalidDataException) { Check(label, true); }
            }
            try
            {
                const string fixture = """
                    [quest]
                    12001 12002
                    [/quest]
                    [ceremony]
                    0 123 0 4 1 456 1 5 2 0 2 6
                    [/ceremony]
                    [wedding gift]
                    0 21001 2 1 21002 3 2 21003 4
                    [/wedding gift]
                    [minute selection]
                    13 29 41
                    [/minute selection]
                    [noti end]
                    17
                    [/noti end]
                    [force move]
                    10 20 0 30 40 0
                    [/force move]
                    [minimum level]
                    42
                    [/minimum level]
                    [money gift limit]
                    10 1000
                    [/money gift limit]
                    [money gift charge]
                    25
                    [/money gift charge]
                    [guest gift]
                    1000 0 22001 2 1 22002 3 2 22003 4
                    [/guest gift]
                    [alimony]
                    77 88
                    [/alimony]
                    [once how many days]
                    3
                    [/once how many days]
                    [days after divorce]
                    4
                    [/days after divorce]
                    [love point]
                    2
                    [/love point]
                    [max love point]
                    777
                    [/max love point]
                    [charged wedding ticket]
                    2 23001 3
                    [/charged wedding ticket]
                    [upgrade ring ticket]
                    0 0 24001 1 24002 2 24003 3 24004 4 24005 5
                    [/upgrade ring ticket]
                    [love point item]
                    25001 7 25002 9
                    [/love point item]
                    [ceremony gift]
                    26001 2 26002 3
                    [/ceremony gift]
                    """;
                var rule = WeddingCatalog.Parse(fixture);
                Check("wedding level, cooldown and love rules come from configuration",
                    rule.MinimumLevel == 42 && rule.RemarryDays == 4 && rule.WeddingCooldownDays == 3
                    && rule.MaximumLove == 777 && rule.LovePerClear == 2);
                Check("wedding quests, fees, materials and rewards come from configuration",
                    rule.RequiredQuests[0] == 12001 && rule.Ceremonies[0].Gold == 123
                    && rule.Ceremonies[1].Gift == new WeddingItemCost(21002, 3)
                    && rule.PaidTicket == new WeddingItemCost(23001, 3)
                    && rule.RingTickets[5] == new WeddingItemCost(24005, 5)
                    && rule.CeremonyGifts[1] == new WeddingItemCost(26002, 3));
                Check("wedding scheduling, guest gifts and love items use configured values",
                    rule.ReservationDelaySeconds[1] == 29 && rule.CeremonyEndSeconds == 17
                    && rule.GiftChargePercent == 25 && rule.GuestGifts[0].Gifts[2][0].ItemId == 22003
                    && rule.LoveItems[25002] == 9);
                var changed = WeddingCatalog.Parse(fixture.Replace("0 123 0 4", "0 321 0 8"));
                Check("edited PVF ceremony cost and invitation count are recognized",
                    changed.Ceremonies[0].Gold == 321 && changed.Ceremonies[0].Invitations == 8);
                Reject("duplicate wedding rules are rejected", fixture + "\n[minimum level]\n30\n[/minimum level]");
                Reject("missing wedding rules are rejected", fixture.Replace("[minimum level]", "[unknown]"));
                Reject("negative wedding cost is rejected", fixture.Replace("0 123 0 4", "0 -1 0 4"));
                Reject("level settings outside the native request UI are rejected", fixture.Replace("42", "29"));
                Reject("incomplete wedding row is rejected", fixture.Replace("0 123 0 4", "0 123 0"));
                Reject("repeated love item ID is rejected", fixture.Replace("25002 9", "25001 9"));
                Reject("missing guest reward coverage is rejected", fixture.Replace("1000 0 22001", "999 0 22001"));
                var actual = WeddingCatalog.Get();
                Check("actual A21 PVF wedding rules load", actual.Ceremonies.Count == 3
                    && actual.RingTickets.Count == 6 && actual.RequiredQuests.Count == 2 && actual.LoveItems.Count > 0);
            }
            catch (Exception ex) { Console.WriteLine(ex); failures++; }
            Console.WriteLine($"WEDDING_CATALOG: failures={failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
