using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingRoomBuffSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            bool Bad(string text) { try { WeddingRoomBuffCatalog.Parse(text); return false; } catch (InvalidDataException) { return true; } }
            try
            {
                string sample = "[couple room buff]\n10 1 `hp max` 100\n40 2 `hp max` 120 `mp max` 80\n[/couple room buff]";
                var catalog = WeddingRoomBuffCatalog.Parse(sample);
                Check("decoration below the first threshold has no bonus", catalog.Evaluate(0) == new WeddingRoomBuff()
                    && catalog.Evaluate(9) == new WeddingRoomBuff());
                Check("exact threshold chooses its configured tier", catalog.Evaluate(10) == new WeddingRoomBuff(HpMax: 100)
                    && catalog.Evaluate(39) == new WeddingRoomBuff(HpMax: 100));
                Check("higher tier replaces rather than stacks lower tier", catalog.Evaluate(40) == new WeddingRoomBuff(HpMax: 120, MpMax: 80)
                    && catalog.Evaluate(int.MaxValue) == new WeddingRoomBuff(HpMax: 120, MpMax: 80));
                Check("buff parser rejects duplicate or descending thresholds", Bad(sample.Replace("40 2", "10 2"))
                    && Bad(sample.Replace("40 2", "9 2")));
                Check("buff parser rejects duplicate and unknown effects", Bad(sample.Replace("`mp max`", "`hp max`"))
                    && Bad(sample.Replace("`mp max`", "`unknown effect`")));
                Check("buff parser rejects malformed count, negative bonus and missing end", Bad(sample.Replace("40 2", "40 3"))
                    && Bad(sample.Replace("`mp max` 80", "`mp max` -80")) && Bad(sample.Replace("[/couple room buff]", "")));
                var actual = WeddingRoomBuffCatalog.Get();
                Check("actual A21 PVF exposes every supported room bonus field", actual.Tiers.Count > 0
                    && actual.Tiers[^1].Buff.HpMax > 0 && actual.Tiers[^1].Buff.MpMax > 0
                    && actual.Tiers[^1].Buff.VillageMovingSpeedPercent > 0 && actual.Tiers[^1].Buff.AllElementalAttack > 0
                    && actual.Tiers[^1].Buff.EquipmentPhysicalAttack > 0 && actual.Tiers[^1].Buff.EquipmentMagicalAttack > 0
                    && actual.Tiers[^1].Buff.SeparateAttack > 0);
                var furniture = WeddingRoomCatalog.Get();
                var item = furniture.FurniturePositions.First(x => x.Value.Count >= 2);
                var positions = item.Value.OrderBy(x => x).Take(2).ToArray();
                var room = new WeddingRoomState { PairId = 1 };
                room.Cargo[0] = new(item.Key, 99);
                Check("stored furniture does not grant decoration", WeddingRoomBuffCalculator.Calculate(room, furniture, catalog, _ => 20)
                    == new WeddingRoomBuffEvaluation(0, new()));
                room.Installed[positions[0]] = item.Key;
                Check("one installed instance contributes its current PVF value", WeddingRoomBuffCalculator.Calculate(room, furniture, catalog, _ => 20)
                    == new WeddingRoomBuffEvaluation(20, new(HpMax: 100)));
                room.Installed[positions[1]] = item.Key;
                int reads = 0;
                var evaluated = WeddingRoomBuffCalculator.Calculate(room, furniture, catalog, _ => { reads++; return 20; });
                Check("multiple installed copies contribute separately without reloading metadata", evaluated.Decoration == 40
                    && evaluated.Buff == new WeddingRoomBuff(HpMax: 120, MpMax: 80) && reads == 1);
                bool negative = false, overflow = false;
                try { WeddingRoomBuffCalculator.Calculate(room, furniture, catalog, _ => -1); } catch (InvalidDataException) { negative = true; }
                try { WeddingRoomBuffCalculator.Calculate(room, furniture, catalog, _ => int.MaxValue); } catch (InvalidDataException) { overflow = true; }
                Check("invalid or overflowing decoration fails closed", negative && overflow);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            Console.WriteLine($"WEDDING_ROOM_BUFF: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }
    }
}
