using System;
using System.Buffers.Binary;
using System.Linq;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarCombatProtocolSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_COMBAT_PROTOCOL selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }

            Section("A21 upgrade requests", () => CheckUpgradeRequests(Check));
            Section("A21 summon requests", () => CheckSummonRequests(Check));
            Section("A21 upgrade acknowledgements", () => CheckUpgradeAcknowledgements(Check));
            Section("A21 summon acknowledgements", () => CheckSummonAcknowledgements(Check));
            Section("A21 spawn consumption", () => CheckSpawnBodies(Check));
            // These tests verify parsing and client-consumed bytes only. They
            // do not claim energy debit, cooldown, actor creation or UI success.
            Console.WriteLine($"GUARDIAN_ALTAR_COMBAT_PROTOCOL assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckUpgradeRequests(Action<string, bool> check)
        {
            var live = Convert.FromHexString("0000000000000000000000000000000000000A000000");
            check("live upgrade preserves level zero and ten display units",
                GuardianAltarCombatRequestParser.TryParseUpgrade(live, out var parsed)
                && parsed == new GuardianAltarUpgradeGageCommand(0, 10));
            var claim = new byte[22];
            PutInt32(claim, 14, int.MaxValue);
            PutInt32(claim, 18, int.MaxValue);
            check("upgrade retains complete signed positive claims for the owner to validate",
                GuardianAltarCombatRequestParser.TryParseUpgrade(claim, out parsed)
                && parsed.ClientLevel == int.MaxValue && parsed.ClientGauge == int.MaxValue);
            check("upgrade parser leaves the input untouched",
                claim.AsSpan(0, 14).ToArray().All(x => x == 0)
                && ReadInt32(claim, 14) == int.MaxValue && ReadInt32(claim, 18) == int.MaxValue);
            check("null upgrade fails with a default command",
                !GuardianAltarCombatRequestParser.TryParseUpgrade(null, out parsed) && parsed == default);
            for (var length = 0; length <= 24; length++)
            {
                if (length == 22)
                    continue;
                check("upgrade rejects body length " + length,
                    !GuardianAltarCombatRequestParser.TryParseUpgrade(new byte[length], out parsed)
                    && parsed == default);
            }
            for (var index = 0; index < 14; index++)
            {
                var malformed = (byte[])live.Clone();
                malformed[index] = 1;
                check("upgrade rejects reserved byte " + index,
                    !GuardianAltarCombatRequestParser.TryParseUpgrade(malformed, out parsed)
                    && parsed == default);
            }
            foreach (var offset in new[] { 14, 18 })
            {
                var malformed = (byte[])live.Clone();
                PutInt32(malformed, offset, int.MinValue);
                check("upgrade rejects negative claim at " + offset,
                    !GuardianAltarCombatRequestParser.TryParseUpgrade(malformed, out parsed)
                    && parsed == default);
            }
        }

        private static void CheckSummonRequests(Action<string, bool> check)
        {
            var first = Convert.FromHexString("000000000000000000000000000000F3F2290000000000");
            var second = Convert.FromHexString("000000000000000000000000000001F8F2290000000000");
            check("live slot zero identifies stackable unit prototype, not a monster id",
                GuardianAltarCombatRequestParser.TryParseSummon(first, out var parsed)
                && parsed == new GuardianAltarSummonUnitCommand(0, 2749171, 0));
            check("live slot one retains its distinct stackable prototype",
                GuardianAltarCombatRequestParser.TryParseSummon(second, out parsed)
                && parsed == new GuardianAltarSummonUnitCommand(1, 2749176, 0));
            var claim = (byte[])first.Clone();
            claim[14] = byte.MaxValue;
            PutInt32(claim, 15, int.MaxValue);
            PutInt32(claim, 19, int.MaxValue);
            check("summon preserves wire slot and complete claims for resource-owned validation",
                GuardianAltarCombatRequestParser.TryParseSummon(claim, out parsed)
                && parsed.Slot == byte.MaxValue && parsed.UnitPrototypeId == int.MaxValue
                && parsed.ClientLevel == int.MaxValue);
            check("null summon fails with a default command",
                !GuardianAltarCombatRequestParser.TryParseSummon(null, out parsed) && parsed == default);
            for (var length = 0; length <= 25; length++)
            {
                if (length == 23)
                    continue;
                check("summon rejects body length " + length,
                    !GuardianAltarCombatRequestParser.TryParseSummon(new byte[length], out parsed)
                    && parsed == default);
            }
            for (var index = 0; index < 14; index++)
            {
                var malformed = (byte[])first.Clone();
                malformed[index] = byte.MaxValue;
                check("summon rejects reserved byte " + index,
                    !GuardianAltarCombatRequestParser.TryParseSummon(malformed, out parsed)
                    && parsed == default);
            }
            foreach (var invalid in new[] { 0, -1, int.MinValue })
            {
                var malformed = (byte[])first.Clone();
                PutInt32(malformed, 15, invalid);
                check("summon rejects prototype " + invalid,
                    !GuardianAltarCombatRequestParser.TryParseSummon(malformed, out parsed)
                    && parsed == default);
            }
            var negativeLevel = (byte[])first.Clone();
            PutInt32(negativeLevel, 19, -1);
            check("summon rejects a negative client level",
                !GuardianAltarCombatRequestParser.TryParseSummon(negativeLevel, out parsed)
                && parsed == default);
        }

        private static void CheckUpgradeAcknowledgements(Action<string, bool> check)
        {
            var body = GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, 1, 2, 12345);
            check("upgrade success has outer success and all sixteen client-consumed bytes",
                body.Length == 17 && body[0] == 1 && ReadInt32(body, 1) == 0);
            check("upgrade preserves previous level, new level and fixed remaining energy offsets",
                ReadInt32(body, 5) == 1 && ReadInt32(body, 9) == 2 && ReadInt32(body, 13) == 12345);
            check("upgrade fixed remaining energy is not divided or rounded by the body builder",
                body.SequenceEqual(Convert.FromHexString("0100000000010000000200000039300000")));
            foreach (var result in new[] { 2, 3 })
            {
                var rejected = GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, result, 0, 0, 10000);
                check("upgrade verified result " + result + " retains the complete consumer payload",
                    rejected.Length == 17 && rejected[0] == 1 && ReadInt32(rejected, 1) == result
                    && ReadInt32(rejected, 13) == 10000);
            }
            check("upgrade unknown-state rejection takes only the outer-failure branch",
                GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(false, -1, -1, -1, -1)
                    .SequenceEqual(new byte[] { 0 }));
            foreach (var result in new[] { -1, 1, 4, int.MaxValue })
                check("upgrade does not invent result " + result,
                    ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(
                        true, result, 0, 0, 0)));
            check("upgrade rejects negative previous level",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, -1, 0, 0)));
            check("upgrade rejects negative new level",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, 0, -1, 0)));
            check("upgrade rejects negative remaining energy",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, 0, 0, -1)));
        }

        private static void CheckSummonAcknowledgements(Action<string, bool> check)
        {
            var body = GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(true, 0, 1, 2749176, 5678);
            check("summon success has outer success and all thirteen client-consumed bytes",
                body.Length == 14 && body[0] == 1 && ReadInt32(body, 1) == 0);
            check("summon preserves the requested slot and actual stackable prototype",
                body[5] == 1 && ReadInt32(body, 6) == 2749176);
            check("summon preserves full fixed-unit remaining energy at unaligned offset ten",
                ReadInt32(body, 10) == 5678
                && body.SequenceEqual(Convert.FromHexString("010000000001F8F229002E160000")));
            foreach (var result in new[] { 2, 3 })
            {
                var rejected = GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(true, result, 0, 2749171, 10000);
                check("summon verified result " + result + " retains all thirteen internal bytes",
                    rejected.Length == 14 && rejected[0] == 1 && ReadInt32(rejected, 1) == result
                    && ReadInt32(rejected, 6) == 2749171 && ReadInt32(rejected, 10) == 10000);
            }
            check("summon unknown-state rejection does not dispatch a success item-use event",
                GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(false, -1, byte.MaxValue, -1, -1)
                    .SequenceEqual(new byte[] { 0 }));
            foreach (var result in new[] { -1, 1, 4, int.MaxValue })
                check("summon does not invent result " + result,
                    ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(
                        true, result, 0, 2749171, 0)));
            foreach (var prototype in new[] { 0, -1, int.MinValue })
                check("summon acknowledgement rejects prototype " + prototype,
                    ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(
                        true, 0, 0, prototype, 0)));
            check("summon acknowledgement rejects negative remaining energy",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(true, 0, 0, 2749171, -1)));
        }

        private static void CheckSpawnBodies(Action<string, bool> check)
        {
            var fields = new GuardianAltarSpawnUnitFields(
                0x1234, 0x01020304, true, 2, 64605, 37, 0x55667788, -1682, 265, -1);
            var body = GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields);
            check("A21 spawn is thirty-eight bytes without the legacy leading struct padding",
                body.Length == 38 && body.SequenceEqual(Convert.FromHexString(
                    "34120403020101000000020000005DFC000025000000887766556EF9FFFF09010000FFFFFFFF")));
            check("spawn consumer receives signed unit index at zero and monster at fourteen",
                BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(0, 2)) == fields.UnitIndex
                && ReadInt32(body, 14) == fields.MonsterId);
            check("enemy marker is the full Int32 resource field at offset six",
                ReadInt32(body, 2) == fields.SummonObjectId && ReadInt32(body, 6) == 1
                && ReadInt32(body, 10) == fields.MonsterType);
            check("level stays Int16 with exactly two padding bytes before the sixth raw resource value",
                BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(18, 2)) == fields.Level
                && body[20] == 0 && body[21] == 0 && ReadInt32(body, 22) == fields.RawValue);
            check("spawn preserves signed X Y Z at the A21 argument assembly offsets",
                ReadInt32(body, 26) == fields.X && ReadInt32(body, 30) == fields.Y
                && ReadInt32(body, 34) == fields.Z);
            var friend = GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { IsEnemy = false, RawValue = int.MinValue });
            check("friend flag and opaque raw values are preserved without guessing their meaning",
                friend.Length == 38 && ReadInt32(friend, 6) == 0 && ReadInt32(friend, 22) == int.MinValue);
            var boundary = GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with
            {
                UnitIndex = short.MaxValue, MonsterId = int.MaxValue, Level = short.MaxValue,
                X = int.MinValue, Y = int.MaxValue, Z = 0
            });
            check("spawn retains full signed wire boundaries instead of truncating monster ids or positions",
                BinaryPrimitives.ReadInt16LittleEndian(boundary.AsSpan(0, 2)) == short.MaxValue
                && ReadInt32(boundary, 14) == int.MaxValue
                && BinaryPrimitives.ReadInt16LittleEndian(boundary.AsSpan(18, 2)) == short.MaxValue
                && ReadInt32(boundary, 26) == int.MinValue && ReadInt32(boundary, 30) == int.MaxValue);
            check("spawn rejects negative runtime unit index",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { UnitIndex = -1 })));
            check("spawn rejects absent summon object reference",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { SummonObjectId = 0 })));
            foreach (var type in new[] { -1, 3 })
                check("spawn rejects unsupported monster type " + type,
                    ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { MonsterType = type })));
            check("spawn rejects absent monster reference",
                ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { MonsterId = 0 })));
            foreach (short level in new short[] { 0, -1 })
                check("spawn rejects invalid monster level " + level,
                    ThrowsOutOfRange(() => GuardianAltarCombatBodyBuilder.BuildSpawnUnit(fields with { Level = level })));
        }

        private static bool ThrowsOutOfRange(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { return true; }
            return false;
        }

        private static int ReadInt32(byte[] body, int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));

        private static void PutInt32(byte[] body, int offset, int value) =>
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset, 4), value);
    }
}
