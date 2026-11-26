using System;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarMenuBodyBuilder
    {
        internal static byte[] Build(GuardianAltarProgressSnapshot snapshot)
        {
            GuardianAltarProgressValidator.Validate(snapshot);
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(0);
            writer.WriteUInt32(1);
            WriteSnapshot(writer, snapshot);
            return writer.ToArray();
        }

        internal static byte[] BuildUpdateRidable(GuardianAltarProgressSnapshot snapshot, byte rawPrefix)
        {
            GuardianAltarProgressValidator.Validate(snapshot);
            var writer = new GamePacketWriter();
            // A21 sub_1156CA0 reads one byte at 0, then the 33-byte header.
            // There is no entry-CMD success/result or UInt32 record count.
            // The consumer does not use this raw prefix later in the function.
            writer.WriteByte(rawPrefix);
            WriteSnapshot(writer, snapshot);
            return writer.ToArray();
        }

        private static void WriteSnapshot(GamePacketWriter writer, GuardianAltarProgressSnapshot snapshot)
        {
            var header = snapshot.Header;
            // A21 sub_1108400: exactly 33 bytes, not eight Int32 fields.
            writer.WriteInt32(header.RidableId);
            writer.WriteInt32(header.FreeTickets);
            writer.WriteInt32(header.SecondaryTickets);
            writer.WriteInt32(header.StageStars);
            writer.WriteInt32(header.OtherStars);
            writer.WriteInt32(header.AvailableStars);
            writer.WriteInt16(header.SurvivalBestPhase);
            writer.WriteInt16(header.StarResetCount);
            writer.WriteUInt32(header.Reserved28);
            writer.WriteByte(header.MenuHintFlag);
            writer.WriteUInt32(checked((uint)snapshot.Stages.Count));
            foreach (var stage in snapshot.Stages)
            {
                writer.WriteInt32(stage.DungeonId);
                writer.WriteInt32(stage.Progress);
            }
            for (var type = 0; type <= 2; type++)
            {
                var purchases = snapshot.Purchases.Where(x => x.ShopType == type).ToArray();
                writer.WriteUInt32(checked((uint)purchases.Length));
                foreach (var purchase in purchases)
                {
                    writer.WriteInt32(purchase.ShopType);
                    writer.WriteInt32(purchase.ResourceId);
                    writer.WriteInt16(purchase.Level);
                    writer.WriteInt16(purchase.Price);
                }
            }
            for (var type = 1; type <= 2; type++)
            {
                var slots = snapshot.Slots.Where(x => x.SlotType == type).ToArray();
                writer.WriteUInt32(checked((uint)slots.Length));
                foreach (var slot in slots)
                {
                    writer.WriteByte(slot.Locked);
                    writer.WriteInt32(slot.SlotType);
                    writer.WriteByte(slot.SlotIndex);
                    writer.WriteInt32(slot.ResourceId);
                }
            }
            writer.WriteUInt32(checked((uint)snapshot.Achievements.Count));
            foreach (var achievement in snapshot.Achievements)
            {
                writer.WriteInt32(achievement.AchievementId);
                writer.WriteByte(achievement.Claimed);
            }
        }
    }
}
