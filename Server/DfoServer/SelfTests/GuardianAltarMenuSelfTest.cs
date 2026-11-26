using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarMenuSelfTest
    {
        private const string SyntheticConfig = "[daily refill ticket]\n3\n[/refill ticket]\n";
        private const string SyntheticStages =
            "[area]\n0\n[district]\n1\n[stage]\n700001 0 0 0\n700002 1 0 0\n[/stage]\n"
            + "[survival]\n700003\n[/survival]\n[/area]\n";

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_MENU selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }
            void Section(string name, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(name + " exception: " + exception, false); }
            }

            Section("real PVF menu definition and wire", () => CheckRealDefinition(Check));
            Section("synthetic definition and fail-closed parsing", () => CheckDefinitions(Check));
            Section("snapshot validation and bounded wire reads", () => CheckSnapshots(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_MENU assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealDefinition(Action<string, bool> check)
        {
            // Missing or incompatible PVF must fail this section, never become
            // a skip or a fabricated successful resource definition.
            var definition = GuardianAltarMenuDefinition.Load();
            check("current PVF defines ridable 1 and three daily refill tickets",
                definition.RidableId == 1 && definition.DailyRefillTicket == 3);
            var fresh = Fresh(definition);
            var expectedPurchases = new[]
            {
                new GuardianAltarPurchase(0, 1000, 1, 0),
                new GuardianAltarPurchase(0, 2000, 1, 0),
                new GuardianAltarPurchase(1, 100, 1, 0),
                new GuardianAltarPurchase(1, 1001, 1, 0),
                new GuardianAltarPurchase(1, 1501, 1, 0),
                new GuardianAltarPurchase(2, 64597, 1, 0)
            };
            check("PVF initial purchases are exactly two units, three skills and one tower at level one",
                fresh.Purchases.SequenceEqual(expectedPurchases));
            check("fresh progress exposes only the registered first stage, not all later stages",
                fresh.Stages.Count == 1
                && fresh.Stages[0] == new GuardianAltarStage(definition.FirstDungeonId, -1));
            check("fresh stars, secondary tickets, survival phase, resets and reserved flags remain zero",
                fresh.Header == new GuardianAltarHeader(1, 3, 0, 0, 0, 0, 0, 0, 0, 0));
            check("initial unit slots deploy only units 1000 and 2000 in six unlocked slots",
                fresh.Slots.Where(x => x.SlotType == 1).SequenceEqual(
                    Enumerable.Range(0, 6).Select(i => new GuardianAltarSlot(
                        0, 1, (byte)i, i == 0 ? 1000 : i == 1 ? 2000 : 0))));
            check("passive skills do not occupy the four unlocked skill slots; final two remain locked",
                fresh.Slots.Where(x => x.SlotType == 2).SequenceEqual(
                    Enumerable.Range(0, 6).Select(i => new GuardianAltarSlot(
                        (byte)(i < 4 ? 0 : 1), 2, (byte)i, i == 0 ? 1501 : 0))));
            check("fresh achievement claims are empty", fresh.Achievements.Count == 0);

            var body = GuardianAltarMenuBodyBuilder.Build(fresh);
            var decoded = ReadMenu(body);
            check("fresh successful A21 menu body is exactly 270 bytes without a character-id prefix",
                body.Length == 270 && decoded.Success == 1 && decoded.Result == 0 && decoded.RecordCount == 1);
            check("fresh header is exactly 33 bytes and seven array counts follow it",
                decoded.HeaderBytes == 33
                && decoded.Counts.SequenceEqual(new[] { 1, 2, 3, 1, 6, 6, 0 })
                && decoded.SegmentBytes.SequenceEqual(new[] { 12, 28, 40, 16, 64, 64, 4 }));
            CheckRoundTrip(check, "real PVF fresh menu", fresh, decoded);
        }

        private static void CheckDefinitions(Action<string, bool> check)
        {
            var shop = SyntheticShop();
            var config = GuardianAltarConfigFile.Parse(SyntheticConfig, shop);
            var stages = GuardianAltarStageMapFile.Parse(SyntheticStages);
            check("synthetic config and stage fixtures pass the same PVF parsers", config.IsValid && stages.IsValid);
            var visitedDungeons = new List<int>();
            var definition = GuardianAltarMenuDefinition.Create(config, stages, id =>
            {
                visitedDungeons.Add(id);
                return true;
            });
            check("definition verifies every registered stage, including the survival dungeon",
                visitedDungeons.SequenceEqual(new[] { 700001, 700002, 700003 }));
            check("definition starts from the first ordinary stage instead of the survival table",
                definition.FirstDungeonId == 700001);
            check("initial level zero takes precedence and a free higher level is not automatically learned",
                definition.InitialPurchases.Contains(new GuardianAltarPurchase(0, 100000001, 0, 0))
                && definition.InitialPurchases.Contains(new GuardianAltarPurchase(0, 100000002, 1, 123))
                && !definition.InitialPurchases.Any(x => x.Level > 1));
            check("nonzero Int16 price and large Int32 resource id survive the static definition",
                definition.InitialPurchases.Contains(new GuardianAltarPurchase(2, 100000006, 1, 789)));
            check("later-unlock entries are not initial purchases or deployments",
                !definition.InitialPurchases.Any(x => x.ResourceId == 100000005)
                && !definition.InitialSlots.Any(x => x.ResourceId == 100000005));
            check("level-zero units and passive skills do not consume initial slots",
                definition.InitialSlots.Count(x => x.ResourceId != 0) == 2
                && definition.InitialSlots.Contains(new GuardianAltarSlot(0, 1, 0, 100000002))
                && definition.InitialSlots.Contains(new GuardianAltarSlot(0, 2, 0, 100000004)));

            var reorderedStages = GuardianAltarStageMapFile.Parse(
                "[area]\n2\n[district]\n3\n[stage]\n700010 0 0 0\n[/stage]\n[/area]\n"
                + SyntheticStages);
            check("ordinary first-stage choice follows area/district/order, not resource file order",
                reorderedStages.IsValid
                && GuardianAltarMenuDefinition.Create(config, reorderedStages, _ => true).FirstDungeonId == 700001);
            check("unregistered or non-Guardian ordinary stage fails closed",
                Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(config, stages, _ => false)));
            check("an invalid survival dungeon also denies the entire menu definition",
                Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(config, stages, id => id != 700003)));
            var unknownUnlock = GuardianAltarConfigFile.Parse(SyntheticConfig,
                shop.Replace("[unlock stage index]\n700001", "[unlock stage index]\n999"));
            check("shop unlock references must belong to the current registered stage map",
                unknownUnlock.IsValid && Throws<InvalidDataException>(() =>
                    GuardianAltarMenuDefinition.Create(unknownUnlock, stages, _ => true)));
            var wrongRidable = GuardianAltarConfigFile.Parse(SyntheticConfig, SyntheticShop(2));
            check("valid catalog without the A21 default ridable cannot initialize menu progress",
                wrongRidable.IsValid
                && Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(wrongRidable, stages, _ => true)));
            check("null definition dependencies are rejected",
                Throws<ArgumentNullException>(() => GuardianAltarMenuDefinition.Create(null, stages, _ => true))
                && Throws<ArgumentNullException>(() => GuardianAltarMenuDefinition.Create(config, null, _ => true))
                && Throws<ArgumentNullException>(() => GuardianAltarMenuDefinition.Create(config, stages, null)));

            foreach (var item in new (string Label, string Content)[]
            {
                ("missing config", null), ("empty config", ""),
                ("missing refill tag", "[gage up rate]\n1\n"),
                ("zero refill", "[daily refill ticket]\n0\n"),
                ("negative refill", "[daily refill ticket]\n-1\n"),
                ("nonnumeric refill", "[daily refill ticket]\nthree\n"),
                ("duplicate refill", SyntheticConfig + SyntheticConfig),
                ("unknown config tag", SyntheticConfig + "[unknown]\n1\n")
            })
            {
                var invalid = GuardianAltarConfigFile.Parse(item.Content, shop);
                check(item.Label + " is invalid and cannot authorize a menu definition",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(invalid, stages, _ => true)));
            }

            foreach (var item in new (string Label, string Content)[]
            {
                ("missing shop", null), ("empty shop", ""),
                ("unclosed ridable", SyntheticShop(closeRidable: false)),
                ("missing tower category", SyntheticShop(towerEntries: "")),
                ("duplicate unit id", SyntheticShop(unitEntries: Entry(100000001) + Entry(100000001))),
                ("duplicate initial level", SyntheticShop(unitEntries: Entry(100000001, rows: "1 1 0\n1 2 0\n"))),
                ("no initial level", SyntheticShop(unitEntries: Entry(100000001, rows: "2 1 0\n"))),
                ("negative level", SyntheticShop(unitEntries: Entry(100000001, rows: "-1 1 0\n"))),
                ("overflow price", SyntheticShop(unitEntries: Entry(100000001, rows: "1 1 32768\n"))),
                ("negative index", SyntheticShop(unitEntries: Entry(100000001, rows: "1 -1 0\n"))),
                ("wrong row width", SyntheticShop(unitEntries: Entry(100000001, rows: "1 1\n"))),
                ("unknown settable flag", SyntheticShop(unitEntries: Entry(100000001, settable: 2)))
            })
            {
                var invalid = GuardianAltarConfigFile.Parse(SyntheticConfig, item.Content);
                check(item.Label + " fails closed without synthesizing purchases",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(invalid, stages, _ => true)));
            }
            var repeatedCatalogLevel = GuardianAltarConfigFile.Parse(SyntheticConfig,
                SyntheticShop(unitEntries: Entry(100000001, rows: "1 1 0\n3 2 0\n3 3 0\n")));
            check("known higher catalog-level duplicates remain diagnostic, not duplicate initial ownership",
                repeatedCatalogLevel.IsValid && repeatedCatalogLevel.Diagnostics.Count == 1
                && GuardianAltarMenuDefinition.Create(repeatedCatalogLevel, stages, _ => true)
                    .InitialPurchases.Count(x => x.ShopType == 0 && x.ResourceId == 100000001) == 1);
            var excessUnits = string.Concat(Enumerable.Range(0, 7).Select(i => Entry(100000010 + i)));
            var excessConfig = GuardianAltarConfigFile.Parse(SyntheticConfig, SyntheticShop(unitEntries: excessUnits));
            check("initial learned deployment exceeding six unit slots is rejected rather than truncated",
                excessConfig.IsValid
                && Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(excessConfig, stages, _ => true)));

            foreach (var item in new (string Label, string Content)[]
            {
                ("missing stagemap", null), ("empty stagemap", ""),
                ("no area", "[district]\n1\n"),
                ("unclosed area", "[area]\n0\n[district]\n1\n[stage]\n1 0 0 0\n[/stage]\n"),
                ("district without stage", "[area]\n0\n[district]\n1\n[/area]\n"),
                ("stage without district", "[area]\n0\n[stage]\n1 0 0 0\n[/stage]\n[/area]\n"),
                ("empty stage", StageTable("")), ("wrong stage width", StageTable("1 0 0")),
                ("nonnumeric coordinate", StageTable("1 x 0 0")),
                ("nonpositive dungeon", StageTable("0 0 0 0")),
                ("duplicate dungeon", StageTable("1 0 0 0\n1 1 0 0")),
                ("duplicate survival dungeon", StageTable("1 0 0 0", "2 2")),
                ("survival reuses ordinary dungeon", StageTable("1 0 0 0", "1")),
                ("empty survival", StageTable("1 0 0 0", "")),
                ("unclosed stage", "[area]\n0\n[district]\n1\n[stage]\n1 0 0 0\n[/area]\n"),
                ("duplicate area", SyntheticStages + SyntheticStages)
            })
            {
                var invalid = GuardianAltarStageMapFile.Parse(item.Content);
                check(item.Label + " fails closed without opening all stages",
                    !invalid.IsValid && !string.IsNullOrWhiteSpace(invalid.InvalidReason)
                    && Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.Create(config, invalid, _ => true)));
            }
        }

        private static void CheckSnapshots(Action<string, bool> check)
        {
            // This independent fixture exercises domain/wire validation; it is
            // not a fallback for the required real-PVF resource checks above.
            var definition = GuardianAltarMenuDefinition.Create(
                GuardianAltarConfigFile.Parse(SyntheticConfig, SyntheticShop()),
                GuardianAltarStageMapFile.Parse(SyntheticStages), _ => true);
            var fresh = Fresh(definition);
            var widened = Copy(fresh,
                header: fresh.Header with
                {
                    FreeTickets = 100000, SecondaryTickets = 200000,
                    StageStars = 50, OtherStars = 10, AvailableStars = 40,
                    SurvivalBestPhase = 0x1234, StarResetCount = 0x2345,
                    Reserved28 = 0xDEADBEEF, MenuHintFlag = 1
                },
                stages: fresh.Stages.Concat(new[] { new GuardianAltarStage(int.MaxValue, 1) }),
                purchases: fresh.Purchases.Concat(new[] { new GuardianAltarPurchase(2, 100000099, 7, 3210) }),
                achievements: new[] { new GuardianAltarAchievement(123456789, 1), new GuardianAltarAchievement(101, 0) });
            var body = GuardianAltarMenuBodyBuilder.Build(widened);
            var decoded = ReadMenu(body);
            CheckRoundTrip(check, "nonzero extended menu", widened, decoded);
            check("header offsets 24 and 26 preserve distinct Int16 values, not a combined business DWORD",
                decoded.HeaderBytes == 33
                && BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(9 + 24, 2)) == 0x1234
                && BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(9 + 26, 2)) == 0x2345
                && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(9 + 28, 4)) == 0xDEADBEEF
                && body[9 + 32] == 1);
            check("purchase rows keep Int32 resource ids and distinct nonzero final Int16 prices",
                decoded.Purchases.Contains(new GuardianAltarPurchase(2, 100000099, 7, 3210))
                && decoded.Purchases.Contains(new GuardianAltarPurchase(2, 100000006, 1, 789)));

            var mutableStages = fresh.Stages.ToList();
            var mutablePurchases = fresh.Purchases.ToList();
            var mutableSlots = fresh.Slots.ToList();
            var mutableAchievements = new List<GuardianAltarAchievement>();
            var copied = new GuardianAltarProgressSnapshot(9, fresh.Header, mutableStages,
                mutablePurchases, mutableSlots, mutableAchievements);
            mutableStages.Clear(); mutablePurchases.Clear(); mutableSlots.Clear();
            mutableAchievements.Add(new GuardianAltarAchievement(1, 1));
            check("snapshot owns immutable copies rather than mutable caller collections",
                copied.Stages.SequenceEqual(fresh.Stages) && copied.Purchases.SequenceEqual(fresh.Purchases)
                && copied.Slots.SequenceEqual(fresh.Slots) && copied.Achievements.Count == 0);
            check("nonpositive character identity and null headers are rejected before building",
                Throws<ArgumentOutOfRangeException>(() => new GuardianAltarProgressSnapshot(0, fresh.Header,
                    fresh.Stages, fresh.Purchases, fresh.Slots, fresh.Achievements))
                && Throws<ArgumentNullException>(() => new GuardianAltarProgressSnapshot(1, null,
                    fresh.Stages, fresh.Purchases, fresh.Slots, fresh.Achievements))
                && Throws<ArgumentNullException>(() => GuardianAltarMenuBodyBuilder.Build(null)));

            foreach (var item in new (string Label, GuardianAltarHeader Header)[]
            {
                ("wrong ridable", fresh.Header with { RidableId = 2 }),
                ("negative free tickets", fresh.Header with { FreeTickets = -1 }),
                ("negative secondary tickets", fresh.Header with { SecondaryTickets = -1 }),
                ("ticket sum overflow", fresh.Header with { FreeTickets = int.MaxValue, SecondaryTickets = 1 }),
                ("negative stage stars", fresh.Header with { StageStars = -1 }),
                ("negative other stars", fresh.Header with { OtherStars = -1 }),
                ("negative available stars", fresh.Header with { AvailableStars = -1 }),
                ("available stars exceed ownership", fresh.Header with { StageStars = 1, AvailableStars = 2 }),
                ("star sum overflow", fresh.Header with { StageStars = int.MaxValue, OtherStars = 1 }),
                ("negative survival phase", fresh.Header with { SurvivalBestPhase = -1 }),
                ("negative reset count", fresh.Header with { StarResetCount = -1 }),
                ("invalid menu hint", fresh.Header with { MenuHintFlag = 2 })
            })
                check(item.Label + " is rejected, not serialized as a successful menu",
                    Throws<InvalidOperationException>(() => GuardianAltarMenuBodyBuilder.Build(Copy(fresh, header: item.Header))));

            var invalidSnapshots = new (string Label, GuardianAltarProgressSnapshot Snapshot)[]
            {
                ("empty stages", Copy(fresh, stages: Array.Empty<GuardianAltarStage>())),
                ("nonpositive stage", Copy(fresh, stages: new[] { new GuardianAltarStage(0, -1) })),
                ("invalid stage progress", Copy(fresh, stages: new[] { new GuardianAltarStage(700001, -2) })),
                ("duplicate stages", Copy(fresh, stages: fresh.Stages.Concat(fresh.Stages))),
                ("negative purchase type", Copy(fresh, purchases: fresh.Purchases.Append(new GuardianAltarPurchase(-1, 9, 1, 0)))),
                ("unknown purchase type", Copy(fresh, purchases: fresh.Purchases.Append(new GuardianAltarPurchase(3, 9, 1, 0)))),
                ("nonpositive purchase id", Copy(fresh, purchases: fresh.Purchases.Append(new GuardianAltarPurchase(2, 0, 1, 0)))),
                ("negative purchase level", Copy(fresh, purchases: fresh.Purchases.Append(new GuardianAltarPurchase(2, 9, -1, 0)))),
                ("negative purchase price", Copy(fresh, purchases: fresh.Purchases.Append(new GuardianAltarPurchase(2, 9, 1, -1)))),
                ("duplicate purchases", Copy(fresh, purchases: fresh.Purchases.Append(fresh.Purchases[0]))),
                ("incomplete slots", Copy(fresh, slots: fresh.Slots.Take(11))),
                ("duplicate slot coordinates", Copy(fresh, slots: fresh.Slots.Skip(1).Append(fresh.Slots[1]))),
                ("invalid lock flag", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { Locked = 2 }))),
                ("unknown slot type", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { SlotType = 3 }))),
                ("out-of-range slot index", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { SlotIndex = 6 }))),
                ("negative slot resource", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { ResourceId = -1 }))),
                ("locked deployed slot", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { Locked = 1 }))),
                ("unowned slot resource", Copy(fresh, slots: ReplaceSlot(fresh, 1, 0, x => x with { ResourceId = int.MaxValue }))),
                ("unlearned deployed resource", Copy(fresh, purchases: fresh.Purchases.Select(x =>
                    x.ResourceId == 100000002 ? x with { Level = 0 } : x))),
                ("nonpositive achievement", Copy(fresh, achievements: new[] { new GuardianAltarAchievement(0, 0) })),
                ("invalid achievement claim", Copy(fresh, achievements: new[] { new GuardianAltarAchievement(1, 2) })),
                ("duplicate achievements", Copy(fresh, achievements: new[] {
                    new GuardianAltarAchievement(1, 0), new GuardianAltarAchievement(1, 1) }))
            };
            foreach (var item in invalidSnapshots)
                check(item.Label + " fails closed before any successful wire body is produced",
                    Throws<InvalidOperationException>(() => GuardianAltarMenuBodyBuilder.Build(item.Snapshot)));

            foreach (var length in new[] { 0, 8, 9, 41, body.Length - 1 })
                check($"bounded test reader rejects a truncated {length}B body",
                    Throws<InvalidDataException>(() => ReadMenu(body.AsSpan(0, length).ToArray())));
            var hugeCount = body.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(42, 4), uint.MaxValue);
            check("bounded reader validates count against remaining bytes before any array allocation",
                Throws<InvalidDataException>(() => ReadMenu(hugeCount)));
            check("bounded reader rejects unexplained trailing bytes",
                Throws<InvalidDataException>(() => ReadMenu(body.Append((byte)0).ToArray())));
        }

        private static GuardianAltarProgressSnapshot Fresh(GuardianAltarMenuDefinition definition) =>
            new GuardianAltarProgressSnapshot(7,
                new GuardianAltarHeader(definition.RidableId, definition.DailyRefillTicket,
                    0, 0, 0, 0, 0, 0, 0, 0),
                new[] { new GuardianAltarStage(definition.FirstDungeonId, -1) },
                definition.InitialPurchases, definition.InitialSlots, Array.Empty<GuardianAltarAchievement>());

        private static GuardianAltarProgressSnapshot Copy(GuardianAltarProgressSnapshot source,
            GuardianAltarHeader header = null, IEnumerable<GuardianAltarStage> stages = null,
            IEnumerable<GuardianAltarPurchase> purchases = null, IEnumerable<GuardianAltarSlot> slots = null,
            IEnumerable<GuardianAltarAchievement> achievements = null) =>
            new GuardianAltarProgressSnapshot(source.CharacterId, header ?? source.Header,
                stages ?? source.Stages, purchases ?? source.Purchases, slots ?? source.Slots,
                achievements ?? source.Achievements);

        private static IEnumerable<GuardianAltarSlot> ReplaceSlot(GuardianAltarProgressSnapshot snapshot,
            int type, int index, Func<GuardianAltarSlot, GuardianAltarSlot> change) =>
            snapshot.Slots.Select(x => x.SlotType == type && x.SlotIndex == index ? change(x) : x);

        private static string Entry(int id, int unlock = 0, int? settable = null, string rows = null) =>
            $"[id]\n{id}\n[unlock stage index]\n{unlock}\n"
            + (settable.HasValue ? $"[is settable slot]\n{settable.Value}\n" : "")
            + "[field name]\n`level` `index` `price`\n[/field name]\n[list]\n"
            + (rows ?? $"1 {id} 0\n") + "[/list]\n[/id]\n";

        private static string SyntheticShop(int ridable = 1, string unitEntries = null,
            string towerEntries = null, bool closeRidable = true) =>
            $"[ridable id]\n{ridable}\n[unit]\n"
            + (unitEntries ?? Entry(100000001, rows: "0 100000001 0\n1 100000001 0\n")
                + Entry(100000002, rows: "1 100000002 123\n5 100000002 0\n"))
            + "[/unit]\n[skill]\n" + Entry(100000003, settable: 0)
            + Entry(100000004, settable: 1) + Entry(100000005, unlock: 700001)
            + "[/skill]\n[tower]\n"
            + (towerEntries ?? Entry(100000006, rows: "1 100000006 789\n"))
            + "[/tower]\n" + (closeRidable ? "[/ridable id]\n" : "");

        private static string StageTable(string rows, string survival = null) =>
            "[area]\n0\n[district]\n1\n[stage]\n" + rows + "\n[/stage]\n"
            + (survival == null ? "" : "[survival]\n" + survival + "\n[/survival]\n")
            + "[/area]\n";

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return true; }
            return false;
        }

        private static void CheckRoundTrip(Action<string, bool> check, string label,
            GuardianAltarProgressSnapshot expected, DecodedMenu actual)
        {
            check(label + " roundtrips every typed header field", actual.Header == expected.Header);
            check(label + " roundtrips A stage/progress rows", actual.Stages.SequenceEqual(expected.Stages));
            check(label + " roundtrips B/C/D type/id/level/price rows", actual.Purchases.SequenceEqual(expected.Purchases));
            check(label + " roundtrips E/F locked/type/index/resource rows", actual.Slots.SequenceEqual(expected.Slots));
            check(label + " roundtrips G achievement/claimed rows with no trailing bytes",
                actual.Achievements.SequenceEqual(expected.Achievements));
        }

        private sealed record DecodedMenu(byte Success, int Result, uint RecordCount,
            GuardianAltarHeader Header, int HeaderBytes, int[] Counts, int[] SegmentBytes,
            List<GuardianAltarStage> Stages, List<GuardianAltarPurchase> Purchases,
            List<GuardianAltarSlot> Slots, List<GuardianAltarAchievement> Achievements);

        private static DecodedMenu ReadMenu(byte[] body)
        {
            var reader = new BoundedWireReader(body);
            var success = reader.Byte();
            var result = reader.Int32();
            var recordCount = reader.UInt32();
            if (recordCount != 1)
                throw new InvalidDataException("This menu fixture requires exactly one record.");
            var headerStart = reader.Position;
            var header = new GuardianAltarHeader(reader.Int32(), reader.Int32(), reader.Int32(),
                reader.Int32(), reader.Int32(), reader.Int32(), reader.Int16(), reader.Int16(),
                reader.UInt32(), reader.Byte());
            var headerBytes = reader.Position - headerStart;
            var counts = new int[7];
            var segmentBytes = new int[7];
            var stages = new List<GuardianAltarStage>();
            var purchases = new List<GuardianAltarPurchase>();
            var slots = new List<GuardianAltarSlot>();
            var achievements = new List<GuardianAltarAchievement>();
            for (var segment = 0; segment < 7; segment++)
            {
                var start = reader.Position;
                var width = segment == 0 ? 8 : segment <= 3 ? 12 : segment <= 5 ? 10 : 5;
                counts[segment] = reader.Count(width);
                for (var index = 0; index < counts[segment]; index++)
                {
                    if (segment == 0)
                        stages.Add(new GuardianAltarStage(reader.Int32(), reader.Int32()));
                    else if (segment <= 3)
                        purchases.Add(new GuardianAltarPurchase(reader.Int32(), reader.Int32(), reader.Int16(), reader.Int16()));
                    else if (segment <= 5)
                        slots.Add(new GuardianAltarSlot(reader.Byte(), reader.Int32(), reader.Byte(), reader.Int32()));
                    else
                        achievements.Add(new GuardianAltarAchievement(reader.Int32(), reader.Byte()));
                }
                segmentBytes[segment] = reader.Position - start;
            }
            if (reader.Remaining != 0)
                throw new InvalidDataException("Menu body has unexplained trailing bytes.");
            return new DecodedMenu(success, result, recordCount, header, headerBytes, counts,
                segmentBytes, stages, purchases, slots, achievements);
        }

        private sealed class BoundedWireReader
        {
            private readonly byte[] _body;
            internal BoundedWireReader(byte[] body) => _body = body ?? throw new ArgumentNullException(nameof(body));
            internal int Position { get; private set; }
            internal int Remaining => _body.Length - Position;
            internal byte Byte() => Take(1)[0];
            internal short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
            internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            internal int Count(int width)
            {
                var count = UInt32();
                if (width <= 0 || count > (uint)(Remaining / width))
                    throw new InvalidDataException("Menu count exceeds the remaining complete rows.");
                return checked((int)count);
            }
            private ReadOnlySpan<byte> Take(int width)
            {
                if (width < 0 || width > Remaining)
                    throw new InvalidDataException("Menu body is truncated.");
                var result = _body.AsSpan(Position, width);
                Position += width;
                return result;
            }
        }
    }
}
