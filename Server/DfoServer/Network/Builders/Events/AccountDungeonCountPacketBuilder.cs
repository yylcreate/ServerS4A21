using DfoServer.Game.Dungeon;

namespace DfoServer.Network.Builders
{
    internal static class AccountDungeonCountPacketBuilder
    {
        // A21 account-count consumer: u8 count, then u32 key/i16 limit/i16 used.
        // This is not the character's six-byte USERINFO1 dimension row.
        internal static byte[] BuildBody(AccountDungeonCountProjection count)
        {
            Validate(count);
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt32(count.DungeonId);
            writer.WriteInt16(count.MaximumCount);
            writer.WriteInt16(count.UsedCount);
            return writer.ToArray();
        }

        // ACCOUNT_DUNGEON_INOUT_COUNT updates the NPC account-count plaque.
        // Its consumer reads one key and one remaining byte, without a list header.
        internal static byte[] BuildRemainingBody(AccountDungeonCountProjection count)
        {
            Validate(count);
            if (count.RemainingCount > byte.MaxValue)
                throw new System.ArgumentOutOfRangeException(nameof(count));
            var writer = new GamePacketWriter();
            writer.WriteUInt32(count.DungeonId);
            writer.WriteByte((byte)count.RemainingCount);
            return writer.ToArray();
        }

        private static void Validate(AccountDungeonCountProjection count)
        {
            if (count.DungeonId == 0 || count.MaximumCount <= 0 || count.UsedCount < 0
                || count.UsedCount > count.MaximumCount)
                throw new System.ArgumentOutOfRangeException(nameof(count));
        }
    }
}
