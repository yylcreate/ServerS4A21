using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class AuctionHandler
    {
        private const string UnmappedCategoryGroup = "\u0000__unmapped_category__";
        internal const string CreatureEggFilterSentinel = "\u0001__creature_egg__";

        // Client category tree extracted in the source A21 implementation.
        // These are UI node identities, never new persistent ItemCore kinds.
        internal static (int kind, string group, bool groupPrefix, string filterJob,
            string filterEquipType, string filterExpertType) ResolveCategoryFilter(int category)
        {
            switch (category)
            {
                case 0x9CB7: return (-1, null, false, null, null, null);
                // Source A21's category tree supplies parent identities and the
                // established leaf resource dimensions. Parents query their leaf
                // union; an unknown identity still fails closed below.
                case 0x2710: return (-1,
                    "ssword|katana|club|lswd|beamswd|knuckle|gauntlet|claw|bglove|tonfa"
                    + "|revolver|automatic|musket|hcannon|bowgun|spear|pole|rod|staff|broom"
                    + "|cross|rosary|totem|scythe|axe|dagger|twinswd|wand|chakraweapon|pike|halberd",
                    false, null, null, null);
                case 0x2775: return (-1, "ssword|katana|club|lswd|beamswd", false, null, null, null);
                case 0x27D9: return (-1, "knuckle|gauntlet|claw|bglove|tonfa", false, null, null, null);
                case 0x283D: return (-1, "revolver|automatic|musket|hcannon|bowgun", false, null, null, null);
                case 0x28A1: return (-1, "spear|pole|rod|staff|broom", false, null, null, null);
                case 0x2905: return (-1, "cross|rosary|totem|scythe|axe", false, null, null, null);
                case 0x2969: return (-1, "dagger|twinswd|wand|chakraweapon", false, null, null, null);
                case 0x2A31: return (-1, "pike|halberd", false, null, null, null);
                case 0x2AF8: return (-1, "cl|lt|la|ha|mt", true, null, null, null);
                case 0x2EE0: return (-1, AuctionRepository.AccessoryParentFilter, false, null, null, null);
                case 0x32C8: return (-1, AuctionRepository.StackableParentFilter, false, null, null, null);
                case 0x7D00: return (-1, "support|magic stone", false, null, null, null);
                case 0x7D01: return (-1, "support", false, null, null, null);
                case 0x7D64: return (-1, "magic stone", false, null, null, null);
                case 0x80E8: return (-1, AuctionRepository.ExpertParentFilter, false, null, null, null);
                case 0x32C9: case 0x32CB: case 0x32CC: case 0x32CE:
                    return (ItemCore.KindConsumable, null, false, null, null, null);
                case 0x32CA: return (ItemCore.KindMaterial, null, false, null, null, null);
                case 0x32CD: return (ItemCore.KindQuest, null, false, null, null, null);
                case 0x36B0: return (ItemCore.KindCreature, null, false, null, CreatureEggFilterSentinel, null);
                case 0x36B1: return (-1, null, false, null, "[artifact red]", null);
                case 0x36B2: return (-1, null, false, null, "[artifact blue]", null);
                case 0x36B3: return (-1, null, false, null, "[artifact green]", null);
                case 0x2AF9: return (-1, "cl", true, null, null, null);
                case 0x2B5C: return (-1, "lt", true, null, null, null);
                case 0x2BC0: return (-1, "la", true, null, null, null);
                case 0x2C24: return (-1, "ha", true, null, null, null);
                case 0x2C88: return (-1, "mt", true, null, null, null);
                case 0x2EE1: return (-1, "amulet", false, null, null, null);
                case 0x2EE2: return (-1, "ring", false, null, null, null);
                case 0x2EE3: return (-1, "wrist", false, null, null, null);
                case >= 0x2EE4 and <= 0x2EF0: return (-1, null, false, null, "[title name]", null);
                case 0x80E9: return (-1, null, false, null, null, "[alchemist]");
                case 0x80EA: return (-1, null, false, null, null, "[doll_controller]");
                case 0x80EB: return (-1, null, false, null, null, "[enchanter]");
                case 0x80EC: return (ItemCore.KindExpertJobMaterial, null, false, null, null, null);
            }

            string weapon = Range(category, 0x2776, new[] { "ssword", "katana", "club", "lswd", "beamswd" })
                ?? Range(category, 0x27DA, new[] { "knuckle", "gauntlet", "claw", "bglove", "tonfa" })
                ?? Range(category, 0x283E, new[] { "revolver", "automatic", "musket", "hcannon", "bowgun" })
                ?? Range(category, 0x28A2, new[] { "spear", "pole", "rod", "staff", "broom" })
                ?? Range(category, 0x2906, new[] { "cross", "rosary", "totem", "scythe", "axe" })
                ?? Range(category, 0x296A, new[] { "dagger", "twinswd", "wand", "chakraweapon" })
                ?? Range(category, 0x2A32, new[] { "pike", "halberd" });
            if (weapon != null) return (-1, weapon, false, null, null, null);

            var armorMaterials = new[] { (0x2AFA, "cl"), (0x2B5D, "lt"), (0x2BC1, "la"), (0x2C25, "ha"), (0x2C89, "mt") };
            foreach (var (start, material) in armorMaterials)
            {
                string part = Range(category, start, new[] { "coat", "shoulder", "pants", "shoes", "waist" });
                if (part != null) return (-1, material + " " + part, false, null, null, null);
            }

            var jobs = new[] { "[swordman]", "[at swordman]", "[demonic swordman]", "[fighter]",
                "[gunner]", "[mage]", "[creator mage]", "[at mage]", "[priest]", "[at gunner]",
                "[thief]", "[at fighter]", "[knight]", "[demonic lancer]" };
            string job = Range(category, 0x7D02, jobs);
            if (job != null) return (-1, "support", false, job, null, null);
            job = Range(category, 0x7D65, jobs);
            if (job != null) return (-1, "magic stone", false, job, null, null);
            return (-1, UnmappedCategoryGroup, false, null, null, null);
        }

        private static string Range(int value, int start, string[] values)
            => value >= start && value - start < values.Length ? values[value - start] : null;
    }
}
