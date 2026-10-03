using System;
using System.Collections.Generic;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal readonly record struct GuardianAltarClearHeader(
        int StageType,
        int ClearedRound,
        int BestRound,
        bool Improved,
        int DungeonId,
        int Difficulty,
        int AwardedStars);

    internal readonly record struct GuardianAltarClearRewardRow(
        int EventFlag,
        int ItemId,
        int Count);

    internal static class GuardianAltarClearBodyBuilder
    {
        // A bounded serialization guard, not a claim about the client's UInt32
        // count width or the size of any particular resource catalog.
        internal const int MaximumRowsPerArray = 4096;

        internal static byte[] BuildClearStage(
            GuardianAltarClearHeader header,
            IReadOnlyList<GuardianAltarClearRewardRow> rewards,
            IReadOnlyList<GuardianAltarPurchase> purchases,
            IReadOnlyList<GuardianAltarAchievement> achievements)
        {
            ValidateHeader(header);
            var rewardRows = Snapshot(rewards, nameof(rewards));
            var purchaseRows = Snapshot(purchases, nameof(purchases));
            var achievementRows = Snapshot(achievements, nameof(achievements));
            foreach (var row in rewardRows)
            {
                if (row.EventFlag != 0 && row.EventFlag != 1)
                    throw new ArgumentOutOfRangeException(nameof(rewards));
                if (row.ItemId <= 0 || row.Count <= 0)
                    throw new ArgumentOutOfRangeException(nameof(rewards));
            }
            foreach (var row in purchaseRows)
            {
                if (row == null)
                    throw new ArgumentException("Null Guardian purchase row.", nameof(purchases));
                if (row.ShopType < 0 || row.ShopType > 2 || row.ResourceId <= 0
                    || row.Level < 0 || row.Price < 0)
                    throw new ArgumentOutOfRangeException(nameof(purchases));
            }
            foreach (var row in achievementRows)
            {
                if (row == null)
                    throw new ArgumentException("Null Guardian achievement row.", nameof(achievements));
                if (row.AchievementId <= 0 || row.Claimed > 1)
                    throw new ArgumentOutOfRangeException(nameof(achievements));
            }

            var expectedLength = checked(37 + 12 * rewardRows.Length
                + 12 * purchaseRows.Length + 5 * achievementRows.Length);
            var writer = new GamePacketWriter();
            // A21 sub_1150820 reads a packed 25-byte header. The byte at 12
            // does not have native-structure padding before DungeonId at 13.
            writer.WriteInt32(header.StageType);
            writer.WriteInt32(header.ClearedRound);
            writer.WriteInt32(header.BestRound);
            writer.WriteByte(header.Improved ? (byte)1 : (byte)0);
            writer.WriteInt32(header.DungeonId);
            writer.WriteInt32(header.Difficulty);
            writer.WriteInt32(header.AwardedStars);

            writer.WriteUInt32(checked((uint)rewardRows.Length));
            foreach (var row in rewardRows)
            {
                // sub_10C57B0 copies the full 12-byte record via sub_9D8EF0,
                // then constructs the item display from ItemId and Count.
                // EventFlag is not a PVF reward-kind enum or a star amount;
                // normal item rewards use the established zero-event field.
                writer.WriteInt32(row.EventFlag);
                writer.WriteInt32(row.ItemId);
                writer.WriteInt32(row.Count);
            }
            writer.WriteUInt32(checked((uint)purchaseRows.Length));
            foreach (var row in purchaseRows)
            {
                writer.WriteInt32(row.ShopType);
                writer.WriteInt32(row.ResourceId);
                writer.WriteInt16(row.Level);
                writer.WriteInt16(row.Price);
            }
            writer.WriteUInt32(checked((uint)achievementRows.Length));
            foreach (var row in achievementRows)
            {
                writer.WriteInt32(row.AchievementId);
                writer.WriteByte(row.Claimed);
            }
            var body = writer.ToArray();
            if (body.Length != expectedLength)
                throw new InvalidOperationException("Guardian clear body length is inconsistent.");
            return body;
        }

        internal static byte[] BuildEplpState(int endType, int availableTicket)
        {
            // sub_1147D10 consumes exactly five bytes. This batch exposes only
            // the confirmed failure and control-release projections. Type3
            // follows a successful clear or an explicit GIVEUP; it does not
            // create success, rewards, or a next-stage unlock on its own.
            if (endType != 0 && endType != 3)
                throw new ArgumentOutOfRangeException(nameof(endType));
            if (availableTicket < 0 || availableTicket > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(availableTicket));
            var writer = new GamePacketWriter();
            writer.WriteInt32(endType);
            writer.WriteByte(checked((byte)availableTicket));
            return writer.ToArray();
        }

        private static void ValidateHeader(GuardianAltarClearHeader header)
        {
            if (header.StageType != 0 && header.StageType != 1)
                throw new ArgumentOutOfRangeException(nameof(header.StageType));
            if (header.ClearedRound < 0 || header.BestRound < 0 || header.AwardedStars < 0)
                throw new ArgumentOutOfRangeException(nameof(header));
            if (header.DungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(header.DungeonId));
            if (header.Difficulty < 0 || header.Difficulty > 2)
                throw new ArgumentOutOfRangeException(nameof(header.Difficulty));
        }

        private static T[] Snapshot<T>(IReadOnlyList<T> rows, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(rows, parameterName);
            var count = rows.Count;
            if (count < 0 || count > MaximumRowsPerArray)
                throw new ArgumentOutOfRangeException(parameterName);
            var result = new T[count];
            for (var index = 0; index < count; index++)
                result[index] = rows[index];
            return result;
        }
    }
}
