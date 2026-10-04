using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarClearProtocolSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_CLEAR_PROTOCOL selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }
            Section("packed clear consumer", () => CheckClearBodies(Check));
            Section("clear validation", () => CheckRejectedValues(Check));
            Section("EPLP consumer", () => CheckEplp(Check));
            Section("ridable snapshot consumer", () => CheckUpdateRidable(Check));
            // Byte consumption tests do not enable survival, grant rewards,
            // authorize client clear facts or prove the client's UI timing.
            Console.WriteLine($"GUARDIAN_ALTAR_CLEAR_PROTOCOL assertions={assertions} failures={failures}");
            return failures;
        }

        private static GuardianAltarClearHeader NormalHeader => new(0, 0, 0, false, 3511, 0, 0);

        private static byte[] Clear(GuardianAltarClearHeader header,
            IReadOnlyList<GuardianAltarClearRewardRow> rewards)
            => GuardianAltarClearBodyBuilder.BuildClearStage(header, rewards,
                Array.Empty<GuardianAltarPurchase>(), Array.Empty<GuardianAltarAchievement>());

        private static void CheckClearBodies(Action<string, bool> check)
        {
            var empty = Clear(NormalHeader, Array.Empty<GuardianAltarClearRewardRow>());
            check("normal empty projection is 25 packed header bytes and three explicit zero counts",
                empty.Length == 37 && empty.SequenceEqual(Convert.FromHexString(
                    "00000000000000000000000000B70D00000000000000000000"
                    + "000000000000000000000000")));
            check("normal projection preserves the unaligned dungeon id and header stars",
                Int32(empty, 0) == 0 && empty[12] == 0 && Int32(empty, 13) == 3511
                && Int32(empty, 17) == 0 && Int32(empty, 21) == 0);

            var header = new GuardianAltarClearHeader(1, 0x01020304, 0x11223344,
                true, 3511, 2, 0x01020304);
            var rewards = new[] {
                new GuardianAltarClearRewardRow(0, 2749211, 4),
                new GuardianAltarClearRewardRow(1, 10006059, 1) };
            var purchases = new[] { new GuardianAltarPurchase(1, 3, 1, 0) };
            var achievements = new[] {
                new GuardianAltarAchievement(7, 0), new GuardianAltarAchievement(9, 1) };
            var body = GuardianAltarClearBodyBuilder.BuildClearStage(header, rewards, purchases, achievements);
            var expected = Convert.FromHexString(
                "01000000040302014433221101B70D00000200000004030201"
                + "02000000000000001BF3290004000000010000002BAE980001000000"
                + "01000000010000000300000001000000"
                + "0200000007000000000900000001");
            check("A21 three arrays follow their own counts without a legacy 28-byte header",
                body.Length == 83 && body.SequenceEqual(expected));
            check("round, best and improved occupy their current consumer offsets",
                Int32(body, 4) == 0x01020304 && Int32(body, 8) == 0x11223344 && body[12] == 1);
            check("reward event flag is a full Int32 followed by full item id and count",
                UInt32(body, 25) == 2 && Int32(body, 29) == 0 && Int32(body, 33) == 2749211
                && Int32(body, 37) == 4 && Int32(body, 41) == 1
                && Int32(body, 45) == 10006059 && Int32(body, 49) == 1);
            check("purchase rows retain the two Int16 tail values",
                UInt32(body, 53) == 1 && Int32(body, 57) == 1 && Int32(body, 61) == 3
                && Int16(body, 65) == 1 && Int16(body, 67) == 0);
            check("achievement rows are five bytes, not padded native records",
                UInt32(body, 69) == 2 && Int32(body, 73) == 7 && body[77] == 0
                && Int32(body, 78) == 9 && body[82] == 1);
            check("independent consumer walker exhausts both normal and survival wire bodies",
                ConsumeClearArrays(empty) && ConsumeClearArrays(body));
            check("consumer walker rejects a truncated row and native header padding",
                !ConsumeClearArrays(body[..^1])
                && !ConsumeClearArrays(body[..25].Concat(new byte[3]).Concat(body[25..]).ToArray()));

            var wide = Clear(NormalHeader with { AwardedStars = int.MaxValue }, new[] {
                new GuardianAltarClearRewardRow(0, int.MaxValue, int.MaxValue) });
            check("positive Int32 reward and stars fields are not truncated to words or bytes",
                Int32(wide, 21) == int.MaxValue && Int32(wide, 33) == int.MaxValue
                && Int32(wide, 37) == int.MaxValue && ConsumeClearArrays(wide));
            var shorts = GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                Array.Empty<GuardianAltarClearRewardRow>(),
                new[] { new GuardianAltarPurchase(2, 3, short.MaxValue, short.MaxValue) },
                Array.Empty<GuardianAltarAchievement>());
            check("purchase tail preserves the complete nonnegative signed word range",
                Int16(shorts, 41) == short.MaxValue && Int16(shorts, 43) == short.MaxValue);
        }

        private static void CheckRejectedValues(Action<string, bool> check)
        {
            foreach (var header in new[] {
                NormalHeader with { StageType = -1 }, NormalHeader with { StageType = 2 },
                NormalHeader with { DungeonId = 0 }, NormalHeader with { DungeonId = -1 },
                NormalHeader with { Difficulty = -1 }, NormalHeader with { Difficulty = 3 },
                NormalHeader with { ClearedRound = -1 }, NormalHeader with { BestRound = -1 },
                NormalHeader with { AwardedStars = -1 } })
                check("malformed clear header rejects " + header,
                    Throws<ArgumentOutOfRangeException>(() => Clear(header, Array.Empty<GuardianAltarClearRewardRow>())));
            check("null reward array is not projected as an empty successful reward",
                Throws<ArgumentNullException>(() => Clear(NormalHeader, null)));
            check("null purchase and achievement arrays are rejected",
                Throws<ArgumentNullException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                    Array.Empty<GuardianAltarClearRewardRow>(), null, Array.Empty<GuardianAltarAchievement>()))
                && Throws<ArgumentNullException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                    Array.Empty<GuardianAltarClearRewardRow>(), Array.Empty<GuardianAltarPurchase>(), null)));
            foreach (var row in new[] {
                new GuardianAltarClearRewardRow(-1, 1, 1), new GuardianAltarClearRewardRow(2, 1, 1),
                new GuardianAltarClearRewardRow(0, 0, 1), new GuardianAltarClearRewardRow(0, -1, 1),
                new GuardianAltarClearRewardRow(0, 1, 0), new GuardianAltarClearRewardRow(0, 1, -1) })
                check("malformed late reward row rejects " + row,
                    Throws<ArgumentOutOfRangeException>(() => Clear(NormalHeader, new[] {
                        new GuardianAltarClearRewardRow(0, 2749211, 4), row })));
            foreach (var row in new[] {
                new GuardianAltarPurchase(-1, 1, 0, 0), new GuardianAltarPurchase(3, 1, 0, 0),
                new GuardianAltarPurchase(0, 0, 0, 0), new GuardianAltarPurchase(0, 1, -1, 0),
                new GuardianAltarPurchase(0, 1, 0, -1), null })
                check("malformed purchase row is rejected",
                    Throws<ArgumentException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                        Array.Empty<GuardianAltarClearRewardRow>(), new[] { row }, Array.Empty<GuardianAltarAchievement>())));
            foreach (var row in new[] {
                new GuardianAltarAchievement(0, 0), new GuardianAltarAchievement(-1, 0),
                new GuardianAltarAchievement(1, 2), null })
                check("malformed achievement row is rejected",
                    Throws<ArgumentException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                        Array.Empty<GuardianAltarClearRewardRow>(), Array.Empty<GuardianAltarPurchase>(), new[] { row })));

            foreach (var count in new[] { GuardianAltarClearBodyBuilder.MaximumRowsPerArray + 1, int.MaxValue })
            {
                var oversized = new CountOnlyRows<GuardianAltarClearRewardRow>(count);
                check("oversized reward count is rejected before reading any rows: " + count,
                    Throws<ArgumentOutOfRangeException>(() => Clear(NormalHeader, oversized)) && oversized.Reads == 0);
            }
            var oversizedPurchases = new CountOnlyRows<GuardianAltarPurchase>(int.MaxValue);
            var oversizedAchievements = new CountOnlyRows<GuardianAltarAchievement>(int.MaxValue);
            check("purchase and achievement count guards do not enumerate oversized collections",
                Throws<ArgumentOutOfRangeException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                    Array.Empty<GuardianAltarClearRewardRow>(), oversizedPurchases, Array.Empty<GuardianAltarAchievement>()))
                && Throws<ArgumentOutOfRangeException>(() => GuardianAltarClearBodyBuilder.BuildClearStage(NormalHeader,
                    Array.Empty<GuardianAltarClearRewardRow>(), Array.Empty<GuardianAltarPurchase>(), oversizedAchievements))
                && oversizedPurchases.Reads == 0 && oversizedAchievements.Reads == 0);
        }

        private static void CheckEplp(Action<string, bool> check)
        {
            check("center failure is the full five-byte EPLP body",
                GuardianAltarClearBodyBuilder.BuildEplpState(0, 0)
                    .SequenceEqual(Convert.FromHexString("0000000000")));
            check("success type three preserves the separate available-ticket byte",
                GuardianAltarClearBodyBuilder.BuildEplpState(3, 1)
                    .SequenceEqual(Convert.FromHexString("0300000001"))
                && GuardianAltarClearBodyBuilder.BuildEplpState(3, byte.MaxValue)
                    .SequenceEqual(Convert.FromHexString("03000000FF")));
            foreach (var endType in new[] { -1, 1, 2, 4, int.MaxValue })
                check("unimplemented EPLP end type rejects " + endType,
                    Throws<ArgumentOutOfRangeException>(() => GuardianAltarClearBodyBuilder.BuildEplpState(endType, 0)));
            foreach (var tickets in new[] { -1, 256, int.MaxValue })
                check("EPLP ticket value cannot wrap to a byte: " + tickets,
                    Throws<ArgumentOutOfRangeException>(() => GuardianAltarClearBodyBuilder.BuildEplpState(3, tickets)));
        }

        private static void CheckUpdateRidable(Action<string, bool> check)
        {
            var slots = Enumerable.Range(1, 2).SelectMany(type => Enumerable.Range(0, 6)
                .Select(index => type == 2 && index == 0
                    ? new GuardianAltarSlot(0, type, (byte)index, 3)
                    : new GuardianAltarSlot(1, type, (byte)index, 0))).ToArray();
            var snapshot = new GuardianAltarProgressSnapshot(1,
                new GuardianAltarHeader(1, 3, 2, 7, 9, 10, 11, 1, 0x12345678, 1),
                new[] { new GuardianAltarStage(3511, 0) },
                new[] { new GuardianAltarPurchase(1, 3, 1, 2) }, slots,
                new[] { new GuardianAltarAchievement(7, 0), new GuardianAltarAchievement(9, 1) });
            var entry = GuardianAltarMenuBodyBuilder.Build(snapshot);
            var update = GuardianAltarMenuBodyBuilder.BuildUpdateRidable(snapshot, 0x7F);
            check("entry retains its success/result and UInt32 ridable-count prefix",
                entry[0] == 1 && Int32(entry, 1) == 0 && UInt32(entry, 5) == 1
                && Int32(entry, 9) == snapshot.Header.RidableId);
            check("update starts with exactly one raw byte and the 33-byte header",
                update[0] == 0x7F && Int32(update, 1) == snapshot.Header.RidableId
                && Int32(update, 5) == 3 && Int32(update, 9) == 2
                && Int16(update, 25) == 11 && Int16(update, 27) == 1
                && UInt32(update, 29) == 0x12345678 && update[33] == 1);
            check("update stage count follows immediately at 34, not after a UInt32 ridable count",
                UInt32(update, 34) == 1 && Int32(update, 38) == 3511 && Int32(update, 42) == 0);
            check("update purchase data keeps catalog type, resource id and the signed word tail order",
                UInt32(update, 46) == 0 && UInt32(update, 50) == 1
                && Int32(update, 54) == 1 && Int32(update, 58) == 3
                && Int16(update, 62) == 1 && Int16(update, 64) == 2 && UInt32(update, 66) == 0);
            check("update slot row starts after its own count and preserves the unaligned type and resource fields",
                UInt32(update, 70) == 6 && UInt32(update, 134) == 6
                && update[138] == 0 && Int32(update, 139) == 2
                && update[143] == 0 && Int32(update, 144) == 3);
            check("update achievement count and five-byte records finish the complete snapshot",
                UInt32(update, 198) == 2 && Int32(update, 202) == 7 && update[206] == 0
                && Int32(update, 207) == 9 && update[211] == 1);
            check("entry and update share exactly the same header and seven-array serializer",
                entry.Length == 220 && update.Length == 212 && entry.AsSpan(9).SequenceEqual(update.AsSpan(1)));
            check("independent snapshot consumer consumes stage, three purchases, two slots and achievements",
                ConsumeSnapshotArrays(update, 34) && ConsumeSnapshotArrays(entry, 42));
            check("truncated snapshot is not accepted by the current consumer shape",
                !ConsumeSnapshotArrays(update[..^1], 34));
            check("invalid current snapshot fails before either projection",
                Throws<ArgumentNullException>(() => GuardianAltarMenuBodyBuilder.BuildUpdateRidable(null, 0)));
        }

        private static bool ConsumeClearArrays(byte[] body)
        {
            if (body.Length < 25) return false;
            var offset = 25;
            foreach (var width in new[] { 12, 12, 5 })
                if (!ConsumeArray(body, ref offset, width)) return false;
            return offset == body.Length;
        }

        private static bool ConsumeSnapshotArrays(byte[] body, int offset)
        {
            foreach (var width in new[] { 8, 12, 12, 12, 10, 10, 5 })
                if (!ConsumeArray(body, ref offset, width)) return false;
            return offset == body.Length;
        }

        private static bool ConsumeArray(byte[] body, ref int offset, int width)
        {
            if (offset < 0 || offset > body.Length - 4) return false;
            var count = UInt32(body, offset);
            offset += 4;
            var bytes = (long)count * width;
            if (bytes > body.Length - offset) return false;
            offset += (int)bytes;
            return true;
        }

        private static int Int32(byte[] body, int offset) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
        private static uint UInt32(byte[] body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
        private static short Int16(byte[] body, int offset) => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset, 2));
        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return true; }
            return false;
        }

        private sealed class CountOnlyRows<T> : IReadOnlyList<T>
        {
            internal CountOnlyRows(int count) => Count = count;
            public int Count { get; }
            internal int Reads;
            public T this[int index]
            {
                get { Reads++; throw new InvalidOperationException("Oversized rows must not be read."); }
            }
            public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Oversized rows must not be enumerated.");
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
