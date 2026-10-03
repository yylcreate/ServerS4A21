using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Events;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarSlotSelfTest
    {
        private static readonly DateTime Anchor = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
        private const int LearnedUnit = 100002;
        private const int ActiveSkill = 100012;
        private const int InitialSkill = 100011;

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_SLOT selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string name, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {name}");
                if (!success) failures++;
            }
            var path = Path.Combine(Path.GetTempPath(), "guardian-slot-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                VerifyWire(Check);
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                var definition = Definition();
                var service = Seed(database, definition);
                VerifyConfiguration(database, service, definition, path, Check);
                VerifyRollback(database, service, Check);
                VerifyCorruptedSlots(database, service, Check);
                VerifyRequests(database, service, path, definition, Check).GetAwaiter().GetResult();
                VerifyRealStartPlan(database, Check);
                VerifyExchangeWire(Check);
                VerifyExchangeDomain(database, service, definition, path, Check);
                VerifyExchangeRequests(database, service, Check).GetAwaiter().GetResult();
                VerifyRealExchangePlan(database, Check);
            }
            catch (Exception ex)
            {
                Check("Guardian slot unexpected exception: " + ex, false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"GUARDIAN_ALTAR_SLOT assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyWire(Action<string, bool> check)
        {
            // Current A21 sender/consumer evidence. These are packed fields,
            // not the menu's 10B Locked/type/index/resource slot record.
            var skill = new GuardianAltarSlotRequest(1, true, 2, 1, 1501);
            var unit = new GuardianAltarSlotRequest(1, true, 1, 5, 1000);
            var remove = new GuardianAltarSlotRequest(1, false, 2, 1, 1501);
            var skillBody = Convert.FromHexString("000000000000000000000000000001000000010200000001DD050000");
            var unitBody = Convert.FromHexString("000000000000000000000000000001000000010100000005E8030000");
            var removeBody = Convert.FromHexString("000000000000000000000000000001000000000200000001DD050000");
            foreach (var example in new[] { (skill, skillBody), (unit, unitBody), (remove, removeBody) })
                check("fixed 28B slot request decodes independent unit/skill/action golden: " + example.Item1,
                    GuardianAltarSlotRequestParser.TryParse(example.Item2, out var actual)
                    && actual == example.Item1 && RequestBody(example.Item1).SequenceEqual(example.Item2));
            check("equip success echoes action one while committed Locked remains zero",
                GuardianAltarSlotAckBodyBuilder.Build(skill, new GuardianAltarSlot(0, 2, 1, 1501))
                    .SequenceEqual(Convert.FromHexString("010000000001000000010200000001DD050000")));
            check("unit ACK preserves its separate type/index/resource fields",
                GuardianAltarSlotAckBodyBuilder.Build(unit, new GuardianAltarSlot(0, 1, 5, 1000))
                    .SequenceEqual(Convert.FromHexString("010000000001000000010100000005E8030000")));
            check("unequip ACK echoes the requested resource while durable slot becomes empty",
                GuardianAltarSlotAckBodyBuilder.Build(remove, new GuardianAltarSlot(0, 2, 1, 0))
                    .SequenceEqual(Convert.FromHexString("010000000001000000000200000001DD050000")));
            check("outer slot failure is exactly two bytes and has no success payload",
                GuardianAltarSlotAckBodyBuilder.BuildRejected().SequenceEqual(new byte[] { 0, 4 }));
            foreach (var invalid in new byte[][] { null, skillBody[..27],
                skillBody.Concat(new byte[1]).ToArray(), Mutate(skillBody, 0, 1),
                Mutate(skillBody, 14, 0, 0, 0, 0), Mutate(skillBody, 18, 2),
                Mutate(skillBody, 19, 0, 0, 0, 0), Mutate(skillBody, 19, 3, 0, 0, 0),
                Mutate(skillBody, 23, 6), Mutate(skillBody, 24, 0, 0, 0, 0),
                Mutate(skillBody, 24, 0xFF, 0xFF, 0xFF, 0xFF) })
                check("slot parser rejects malformed length/prefix/action/type/index/resource",
                    !GuardianAltarSlotRequestParser.TryParse(invalid, out _));
            check("ACK cannot disguise a locked, different or uncommitted slot as success",
                Throws<ArgumentException>(() => GuardianAltarSlotAckBodyBuilder.Build(skill, new GuardianAltarSlot(1, 2, 1, 1501)))
                && Throws<ArgumentException>(() => GuardianAltarSlotAckBodyBuilder.Build(skill, new GuardianAltarSlot(0, 1, 1, 1501)))
                && Throws<ArgumentException>(() => GuardianAltarSlotAckBodyBuilder.Build(remove, new GuardianAltarSlot(0, 2, 1, 1501))));
        }

        private static void VerifyRealStartPlan(GameDatabase database, Action<string, bool> check)
        {
            // A separate fixture role uses the current catalog, never a player database.
            var definition = GuardianAltarMenuDefinition.Load();
            Sql(database, "INSERT INTO characters(character_id,account_id,name,level) VALUES(7,1,'guardian-real-slot',70);");
            var service = new GuardianAltarProgressService(database, new DailyResetService(database), definition);
            var before = service.LoadMenu(7, Anchor);
            var source = before.Slots.First(x => x.SlotType == 1 && x.Locked == 0 && x.ResourceId > 0);
            var target = before.Slots.First(x => x.SlotType == source.SlotType && x.Locked == 0 && x.ResourceId == 0);
            var purchase = before.Purchases.Single(x => x.ShopType == 0 && x.ResourceId == source.ResourceId);
            var row = definition.ShopEntries.Single(x => x.ShopType == 0 && x.ResourceId == source.ResourceId)
                .Levels.Single(x => x.Level == purchase.Level && x.Price == purchase.Price);
            var oldPlan = definition.CreateStartPlan(definition.FirstDungeonId, 0, before);
            var oldUnits = oldPlan.Units.ToArray();
            var oldMenu = GuardianAltarMenuBodyBuilder.Build(oldPlan.Progress);
            var removed = service.SetSlot(7, 1, new GuardianAltarSlotRequest(definition.RidableId,
                false, source.SlotType, source.SlotIndex, source.ResourceId));
            var equipped = service.SetSlot(7, 1, new GuardianAltarSlotRequest(definition.RidableId,
                true, target.SlotType, target.SlotIndex, source.ResourceId));
            var persisted = service.LoadMenu(7, Anchor);
            var newPlan = definition.CreateStartPlan(definition.FirstDungeonId, 0, persisted);
            var expectedUnits = oldUnits.Select(x => x.Slot == source.SlotIndex
                ? x with { Slot = target.SlotIndex } : x).OrderBy(x => x.Slot).ToArray();
            check("real catalog slot commits map the learned level/price to the next run's unit prototype",
                removed.Accepted && removed.Changed && equipped.Accepted && equipped.Changed
                && Slot(persisted, 1, source.SlotIndex).ResourceId == 0
                && newPlan.Units.Single(x => x.Slot == target.SlotIndex).PrototypeId == row.Index
                && newPlan.Units.SequenceEqual(expectedUnits)
                && Same(newPlan.Progress, persisted));
            check("real catalog slot commits cannot mutate an already captured run's progress or units",
                oldPlan.Units.SequenceEqual(oldUnits)
                && oldPlan.Units.Single(x => x.Slot == source.SlotIndex).PrototypeId == row.Index
                && Slot(oldPlan.Progress, 1, source.SlotIndex).ResourceId == source.ResourceId
                && Slot(oldPlan.Progress, 1, target.SlotIndex).ResourceId == 0
                && oldMenu.SequenceEqual(GuardianAltarMenuBodyBuilder.Build(oldPlan.Progress)));
        }

        private static GuardianAltarProgressService Seed(GameDatabase database, GuardianAltarMenuDefinition definition)
        {
            Sql(database, "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-slot',''),(2,'guardian-other','');");
            var service = new GuardianAltarProgressService(database, new DailyResetService(database), definition);
            for (var id = 1; id <= 6; id++)
            {
                Sql(database, $"INSERT INTO characters(character_id,account_id,name,level) VALUES({id},1,'guardian-slot-{id}',70);");
                service.LoadMenu(id, Anchor);
            }
            Sql(database, "UPDATE character_guardian_altar SET stage_stars=100,other_stars=20,available_stars=120;");
            for (var id = 1; id <= 6; id++)
            {
                service.Buy(id, 1, new GuardianAltarPurchaseRequest(1, 0, LearnedUnit, 1));
                service.Buy(id, 1, new GuardianAltarPurchaseRequest(1, 1, ActiveSkill, 1));
            }
            return service;
        }

        private static void VerifyConfiguration(GameDatabase database, GuardianAltarProgressService service,
            GuardianAltarMenuDefinition definition, string path, Action<string, bool> check)
        {
            var original = service.LoadMenu(1, Anchor);
            var receiptCount = Scalar(database, "SELECT COUNT(*) FROM character_daily_counters;");
            var unit = new GuardianAltarSlotRequest(1, true, 1, 5, LearnedUnit);
            var skill = new GuardianAltarSlotRequest(1, true, 2, 2, ActiveSkill);
            var occupied = service.SetSlot(1, 1, skill with { SlotIndex = 0 });
            check("a new learned resource cannot replace a different occupied resource",
                !occupied.Accepted && Same(original, occupied.Progress));
            var unitResult = service.SetSlot(1, 1, unit);
            check("learned unit config changes exactly its empty unlocked target slot",
                unitResult.Accepted && unitResult.Changed && unitResult.Slot == new GuardianAltarSlot(0, 1, 5, LearnedUnit)
                && SameExceptSlot(original, unitResult.Progress, 1, 5) && Slot(unitResult.Progress, 1, 5).ResourceId == LearnedUnit);
            var skillResult = service.SetSlot(1, 1, skill);
            check("learned active skill config uses type2 without changing unit slots or progress balances",
                skillResult.Accepted && skillResult.Changed && skillResult.Slot == new GuardianAltarSlot(0, 2, 2, ActiveSkill)
                && SameExceptSlot(unitResult.Progress, skillResult.Progress, 2, 2));
            var immutableMenu = GuardianAltarMenuBodyBuilder.Build(original);
            check("previously captured progress remains immutable after later slot commits",
                Slot(original, 1, 5).ResourceId == 0 && Slot(original, 2, 2).ResourceId == 0
                && immutableMenu.SequenceEqual(GuardianAltarMenuBodyBuilder.Build(original)));

            // A repeat must perform no slot update, even when an update would fail.
            Sql(database, "CREATE TRIGGER reject_replayed_guardian_slot BEFORE UPDATE OF resource_id ON character_guardian_altar_slots "
                + "WHEN NEW.character_id=1 BEGIN SELECT RAISE(ABORT,'unexpected replay write'); END;");
            try
            {
                var replay = service.SetSlot(1, 1, skill);
                check("same-resource same-slot retry is accepted without a durable rewrite",
                    replay.Accepted && !replay.Changed && Same(skillResult.Progress, replay.Progress));
            }
            finally { Sql(database, "DROP TRIGGER reject_replayed_guardian_slot;"); }

            foreach (var invalid in new[] {
                new GuardianAltarSlotRequest(2, true, 2, 3, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 0, 3, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 3, 3, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 2, 6, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 2, 3, 0),
                new GuardianAltarSlotRequest(1, true, 2, 3, -1),
                new GuardianAltarSlotRequest(1, true, 2, 3, 999999),
                new GuardianAltarSlotRequest(1, true, 2, 3, 100010), // learned passive
                new GuardianAltarSlotRequest(1, true, 2, 3, 100013), // stage-locked resource
                new GuardianAltarSlotRequest(1, true, 2, 3, 100015), // level-zero resource
                new GuardianAltarSlotRequest(1, true, 2, 3, LearnedUnit), // wrong catalog type
                new GuardianAltarSlotRequest(1, true, 1, 4, 100020), // tower is not a unit
                new GuardianAltarSlotRequest(1, true, 2, 4, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 2, 5, ActiveSkill),
                new GuardianAltarSlotRequest(1, true, 2, 3, ActiveSkill), // duplicate deployment
                new GuardianAltarSlotRequest(1, true, 2, 0, ActiveSkill),
                new GuardianAltarSlotRequest(1, false, 2, 0, ActiveSkill), // wrong resource on remove
            })
            {
                var before = service.LoadMenu(1, Anchor);
                var result = service.SetSlot(1, 1, invalid);
                check("invalid, passive, locked, duplicate or occupied target rejects without changing any fact: " + invalid,
                    !result.Accepted && !result.Changed && Same(before, result.Progress));
            }
            check("wrong account cannot configure another role's deployment",
                Throws<InvalidOperationException>(() => service.SetSlot(1, 2, skill))
                && Same(skillResult.Progress, service.LoadMenu(1, Anchor)));
            var ambiguousPurchases = original.Purchases.Select(x => x.ResourceId == 100003
                ? x with { Level = 3, Price = 9 } : x);
            var ambiguous = new GuardianAltarProgressSnapshot(original.CharacterId, original.Header,
                original.Stages, ambiguousPurchases,
                original.Slots.Select(x => x.ResourceId == 100003 ? x with { ResourceId = 0 } : x),
                original.Achievements);
            check("unconfigured ambiguous catalog rows do not reject the entire menu",
                GuardianAltarMenuBodyBuilder.Build(ambiguous).Length > 0
                && Same(skillResult.Progress, service.LoadMenu(1, Anchor)));
            check("ambiguous unconfigured current level rejects only the candidate slot deployment",
                !definition.TryCreateSlotPlan(ambiguous,
                    new GuardianAltarSlotRequest(1, true, 1, 1, 100003), out _, out _));

            var remove = skill with { Equip = false };
            var cleared = service.SetSlot(1, 1, remove);
            var clearReplay = service.SetSlot(1, 1, remove);
            check("matching unequip clears one slot and empty-slot retry remains idempotent",
                cleared.Accepted && cleared.Changed && cleared.Slot.ResourceId == 0
                && clearReplay.Accepted && !clearReplay.Changed && Same(cleared.Progress, clearReplay.Progress)
                && SameExceptSlot(skillResult.Progress, cleared.Progress, 2, 2));
            var moved = service.SetSlot(1, 1, skill with { SlotIndex = 3 });
            var staleRemove = service.SetSlot(1, 1, remove);
            check("late unequip for the old slot cannot clear a resource now deployed elsewhere",
                moved.Accepted && !staleRemove.Accepted && Same(moved.Progress, staleRemove.Progress)
                && Slot(staleRemove.Progress, 2, 3).ResourceId == ActiveSkill);
            service.SetSlot(1, 1, skill with { SlotIndex = 3, Equip = false });
            var clearInitial = service.SetSlot(1, 1, new GuardianAltarSlotRequest(1, false, 2, 0, InitialSkill));
            var reused = service.SetSlot(1, 1, skill with { SlotIndex = 0 });
            check("a different resource can occupy the target only after its prior resource is explicitly removed",
                clearInitial.Accepted && reused.Accepted && Slot(reused.Progress, 2, 0).ResourceId == ActiveSkill
                && !reused.Progress.Slots.Any(x => x.ResourceId == InitialSkill));
            check("configuration never refills tickets, debits stars, buys resources or changes daily receipt",
                original.Header == reused.Progress.Header && original.Purchases.SequenceEqual(reused.Progress.Purchases)
                && original.Stages.SequenceEqual(reused.Progress.Stages) && original.Achievements.SequenceEqual(reused.Progress.Achievements)
                && Scalar(database, "SELECT COUNT(*) FROM character_daily_counters;") == receiptCount);
            var restartedDatabase = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var restarted = new GuardianAltarProgressService(restartedDatabase, new DailyResetService(restartedDatabase), definition);
            check("recreated progress owner and menu read preserve the committed configuration",
                Same(reused.Progress, restarted.LoadMenu(1, Anchor)));
            var nextDay = restarted.LoadMenu(1, Anchor.AddDays(1));
            check("daily ticket replacement leaves slot configuration and learned levels intact",
                reused.Progress.Slots.SequenceEqual(nextDay.Slots) && reused.Progress.Purchases.SequenceEqual(nextDay.Purchases));

            var beforeConcurrent = service.LoadMenu(4, Anchor);
            var concurrent = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => service.SetSlot(4, 1, skill))))
                .GetAwaiter().GetResult();
            var afterConcurrent = service.LoadMenu(4, Anchor);
            check("concurrent identical configs commit one slot mutation and do not change balances",
                concurrent.All(x => x.Accepted) && concurrent.Count(x => x.Changed) == 1
                && SameExceptSlot(beforeConcurrent, afterConcurrent, 2, 2)
                && Slot(afterConcurrent, 2, 2).ResourceId == ActiveSkill);
            service.Buy(4, 1, new GuardianAltarPurchaseRequest(1, 1, 100015, 1));
            service.Buy(4, 1, new GuardianAltarPurchaseRequest(1, 1, 100016, 1));
            service.SetSlot(4, 1, new GuardianAltarSlotRequest(1, true, 2, 3, 100015));
            var full = service.LoadMenu(4, Anchor);
            var fullResults = Enumerable.Range(0, 4).Select(index =>
                service.SetSlot(4, 1, new GuardianAltarSlotRequest(1, true, 2, (byte)index, 100016))).ToArray();
            check("full unlocked skill slots reject every new deployment without overwriting a resource",
                full.Slots.Count(x => x.SlotType == 2 && x.Locked == 0 && x.ResourceId > 0) == 4
                && fullResults.All(x => !x.Accepted && Same(full, x.Progress)));
        }

        private static void VerifyRollback(GameDatabase database, GuardianAltarProgressService service, Action<string, bool> check)
        {
            var request = new GuardianAltarSlotRequest(1, true, 2, 2, ActiveSkill);
            var before = service.LoadMenu(2, Anchor);
            check("obsolete generation rejects before slot mutation",
                Throws<InvalidOperationException>(() => service.SetSlot(2, 1, request, () => false))
                && Same(before, service.LoadMenu(2, Anchor)));
            var fenceCalls = 0;
            check("generation change at the final commit fence rolls back the written slot",
                Throws<InvalidOperationException>(() => service.SetSlot(2, 1, request, () => ++fenceCalls == 1))
                && fenceCalls == 2 && Same(before, service.LoadMenu(2, Anchor)));
            Sql(database, "CREATE TRIGGER reject_guardian_slot AFTER UPDATE OF resource_id ON character_guardian_altar_slots "
                + "WHEN NEW.character_id=2 BEGIN SELECT RAISE(ABORT,'guardian-slot-test'); END;");
            try
            {
                check("SQLite failure after slot update restores the complete durable snapshot",
                    Throws<SqliteException>(() => service.SetSlot(2, 1, request))
                    && Same(before, service.LoadMenu(2, Anchor)));
            }
            finally { Sql(database, "DROP TRIGGER reject_guardian_slot;"); }
            var recovered = service.SetSlot(2, 1, request);
            check("request can commit normally after a failed transaction",
                recovered.Accepted && recovered.Changed && SameExceptSlot(before, recovered.Progress, 2, 2));
        }

        private static void VerifyCorruptedSlots(GameDatabase database, GuardianAltarProgressService service,
            Action<string, bool> check)
        {
            // Deliberate pollution exists only in this uniquely owned fixture DB.
            // It exercises validation of preexisting rows, never a mutation path.
            var request = new GuardianAltarSlotRequest(1, true, 2, 2, ActiveSkill);
            var passiveBefore = service.LoadMenu(5, Anchor);
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=100010 "
                + "WHERE character_id=5 AND slot_type=2 AND slot_index=2;");
            check("persisted passive deployment rejects both menu and mutation without repairing the row",
                Throws<InvalidOperationException>(() => service.LoadMenu(5, Anchor))
                && Throws<InvalidOperationException>(() => service.SetSlot(5, 1, request))
                && Scalar(database, "SELECT resource_id FROM character_guardian_altar_slots "
                    + "WHERE character_id=5 AND slot_type=2 AND slot_index=2;") == 100010);
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=0 "
                + "WHERE character_id=5 AND slot_type=2 AND slot_index=2;");
            check("an explicitly restored fixture validates without changing other progress", Same(passiveBefore, service.LoadMenu(5, Anchor)));

            var duplicateBefore = service.LoadMenu(6, Anchor);
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=100011 "
                + "WHERE character_id=6 AND slot_type=2 AND slot_index=2;");
            check("persisted duplicate deployment rejects menu and mutation without silently clearing a slot",
                Throws<InvalidOperationException>(() => service.LoadMenu(6, Anchor))
                && Throws<InvalidOperationException>(() => service.SetSlot(6, 1, request))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_slots "
                    + "WHERE character_id=6 AND slot_type=2 AND resource_id=100011;") == 2);
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=0 "
                + "WHERE character_id=6 AND slot_type=2 AND slot_index=2;");
            check("duplicate fixture restoration preserves the original slot vector", Same(duplicateBefore, service.LoadMenu(6, Anchor)));
        }

        private static async Task VerifyRequests(GameDatabase database, GuardianAltarProgressService progress,
            string path, GuardianAltarMenuDefinition definition, Action<string, bool> check)
        {
            using var fixture = new SlotRequestFixture(progress);
            var request = new GuardianAltarSlotRequest(1, true, 2, 2, ActiveSkill);
            var before = progress.LoadMenu(3, Anchor);
            check("production entry registration owns both SET_SLOT and EXCHANGE_SLOT",
                fixture.Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT, out _)
                && fixture.Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_EXCHANGE_SLOT, out _));
            await fixture.Dispatch(request);
            check("closed configuration returns rejection without entering its transaction",
                IsFailure(fixture.Packets.Single()) && Same(before, progress.LoadMenu(3, Anchor)) && fixture.Probe.Calls == 0);
            fixture.Open();
            fixture.Packets.Clear();
            await fixture.Dispatch(request, RequestBody(request)[..27]);
            check("malformed configuration rejects before invoking the slot owner",
                IsFailure(fixture.Packets.Single()) && Same(before, progress.LoadMenu(3, Anchor)) && fixture.Probe.Calls == 0);
            foreach (var restriction in new[] { "trade", "mercenary", "level", "presence", "combat" })
            {
                fixture.Packets.Clear();
                fixture.Trading = restriction == "trade";
                fixture.Mercenary.Assigned = restriction == "mercenary";
                fixture.Session.Player.Level = (byte)(restriction == "level" ? 49 : 70);
                fixture.Session.Player.TownPresenceReady = restriction != "presence";
                fixture.Session.Player.UserState = (byte)(restriction == "combat" ? 1 : 0);
                await fixture.Dispatch(request);
                check("configuration honors current entry/presence restrictions: " + restriction,
                    fixture.Packets.Count == 1 && IsFailure(fixture.Packets[0])
                    && fixture.Probe.Calls == 0 && Same(before, progress.LoadMenu(3, Anchor)));
            }
            fixture.Trading = false;
            fixture.Mercenary.Assigned = false;
            fixture.Session.Player.Level = 70;
            fixture.Session.Player.TownPresenceReady = true;
            fixture.Session.Player.UserState = 0;
            fixture.Packets.Clear();
            fixture.Probe.DuringFence = call =>
            {
                if (call == 2) fixture.Session.GameSession = GameSessionIdentity();
            };
            await fixture.Dispatch(request);
            check("old GameSession at the final transactional fence rolls back and receives no projection",
                fixture.Packets.Count == 0 && Same(before, progress.LoadMenu(3, Anchor)));
            fixture.Probe.DuringFence = null;
            fixture.Packets.Clear();
            fixture.Probe.DuringFence = call =>
            {
                if (call == 2) fixture.Open();
            };
            await fixture.Dispatch(request);
            check("opening revision change before commit rolls back and requests a fresh menu",
                fixture.Packets.Count == 2 && IsFailure(fixture.Packets[0])
                && Notice(fixture.Packets[1]) && Same(before, progress.LoadMenu(3, Anchor)));
            fixture.Probe.DuringFence = null;
            fixture.Packets.Clear();
            Sql(database, "CREATE TRIGGER reject_guardian_slot_dispatch AFTER UPDATE OF resource_id ON character_guardian_altar_slots "
                + "WHEN NEW.character_id=3 BEGIN SELECT RAISE(ABORT,'guardian-slot-dispatch'); END;");
            try
            {
                await fixture.Dispatch(request);
                check("registered handler reports durable failure and leaves the role unchanged",
                    fixture.Packets.Count == 2 && IsFailure(fixture.Packets[0]) && Notice(fixture.Packets[1])
                    && Same(before, progress.LoadMenu(3, Anchor)));
            }
            finally { Sql(database, "DROP TRIGGER reject_guardian_slot_dispatch;"); }
            fixture.Packets.Clear();
            fixture.FailNextWrite = true;
            await fixture.Dispatch(request);
            var committed = progress.LoadMenu(3, Anchor);
            check("lost ACK does not undo committed slot state or change balances",
                fixture.Packets.Count == 0 && Slot(committed, 2, 2).ResourceId == ActiveSkill
                && SameExceptSlot(before, committed, 2, 2));
            var restartedDatabase = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var restarted = new GuardianAltarProgressService(restartedDatabase, new DailyResetService(restartedDatabase), definition);
            check("relog owner reads a configuration whose success ACK was lost", Same(committed, restarted.LoadMenu(3, Anchor)));
            await fixture.Dispatch(request);
            check("retry after lost ACK returns the same committed 19B payload without another write",
                IsSuccess(fixture.Packets.Single(), request, Slot(committed, 2, 2)) && Same(committed, progress.LoadMenu(3, Anchor)));
            fixture.Packets.Clear();
            var remove = request with { Equip = false };
            await fixture.Dispatch(remove);
            var removed = progress.LoadMenu(3, Anchor);
            check("real unequip dispatch clears durable resource and echoes the old resource in its ACK",
                IsSuccess(fixture.Packets.Single(), remove, Slot(removed, 2, 2)) && Slot(removed, 2, 2).ResourceId == 0);
            fixture.Packets.Clear();
            fixture.BeforePrepare = fixture.Open;
            await fixture.Dispatch(request);
            var afterRevision = progress.LoadMenu(3, Anchor);
            check("window revision change after commit suppresses success but preserves configuration",
                fixture.Packets.Count == 2 && IsFailure(fixture.Packets[0]) && Notice(fixture.Packets[1])
                && Slot(afterRevision, 2, 2).ResourceId == ActiveSkill && SameExceptSlot(removed, afterRevision, 2, 2));
            fixture.Packets.Clear();
            fixture.BeforePrepare = fixture.ReplaceSession;
            await fixture.Dispatch(remove);
            var afterReplacement = progress.LoadMenu(3, Anchor);
            check("displaced session after commit cannot send an old-role ACK and relog sees the empty slot",
                fixture.Packets.Count == 0 && Slot(afterReplacement, 2, 2).ResourceId == 0
                && SameExceptSlot(afterRevision, afterReplacement, 2, 2));
            await fixture.Dispatch(request);
            check("new current session can configure and receive its committed projection",
                IsSuccess(fixture.Packets.Single(), request, Slot(progress.LoadMenu(3, Anchor), 2, 2)));
        }

        private sealed class SlotProgressProbe : IGuardianAltarProgressService, IGuardianAltarSlotService,
            IGuardianAltarExchangeService
        {
            private readonly GuardianAltarProgressService _inner;
            internal int Calls;
            internal Action<int> DuringFence;
            internal SlotProgressProbe(GuardianAltarProgressService inner) => _inner = inner;
            public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow, Func<bool> isCurrent = null)
                => _inner.LoadMenu(characterId, utcNow, isCurrent);
            public GuardianAltarSlotResult SetSlot(int characterId, int accountId, GuardianAltarSlotRequest request,
                Func<bool> isCurrent = null)
            {
                Calls++;
                var fenceCalls = 0;
                return _inner.SetSlot(characterId, accountId, request, () =>
                {
                    DuringFence?.Invoke(++fenceCalls);
                    return isCurrent == null || isCurrent();
                });
            }
            public GuardianAltarExchangeResult ExchangeSlots(int characterId, int accountId,
                GuardianAltarExchangeRequest request, Func<bool> isCurrent = null)
            {
                Calls++;
                var fenceCalls = 0;
                return _inner.ExchangeSlots(characterId, accountId, request, () =>
                {
                    DuringFence?.Invoke(++fenceCalls);
                    return isCurrent == null || isCurrent();
                });
            }
        }

        private sealed class SlotMercenaryStub : IMercenaryRestrictionService
        {
            internal bool Assigned;
            public bool IsAssigned(int characterId) => Assigned;
            public bool CanDelete(int characterId) => !Assigned;
            public bool CanMutateAppearance(int characterId) => !Assigned;
            public bool CanEnterContent(int characterId) => !Assigned;
        }

        private sealed class SlotRequestFixture : IDisposable
        {
            internal readonly GuardianAltarOpeningService Opening = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly GameCommandRegistry Registry = new();
            internal readonly SlotMercenaryStub Mercenary = new();
            internal readonly List<byte[]> Packets = new();
            internal readonly SlotProgressProbe Probe;
            private readonly GuardianAltarOpeningCoordinator _coordinator;
            private readonly List<EnhancedClientSession> _ownedSessions = new();
            private int _windowMinutes = 5;
            internal EnhancedClientSession Session { get; private set; }
            internal bool Trading;
            internal bool FailNextWrite;
            internal Action BeforePrepare;
            internal Action AfterWrite;
            internal SlotRequestFixture(GuardianAltarProgressService progress)
            {
                Probe = new SlotProgressProbe(progress);
                _coordinator = new GuardianAltarOpeningCoordinator(Opening, Sessions,
                    new CharacterTransitionCoordinator(Sessions), null, () => new DateTimeOffset(Anchor), Send, Probe);
                Registry.RegisterGroup("guardian-slot-test", group =>
                    _coordinator.RegisterEntryHandlers(group, _ => Trading, Mercenary));
                ReplaceSession();
            }
            internal void ReplaceSession()
            {
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 1 };
                session.Player.CharacterId = 3;
                session.Player.UserId = 12345;
                session.Player.Level = 70;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.GameSession = GameSessionIdentity();
                _ownedSessions.Add(session);
                Sessions.Register(3, session);
                Session = session;
            }
            internal void Open() => Opening.SetWindow(new DateTimeOffset(Anchor), new DateTimeOffset(Anchor).AddMinutes(_windowMinutes++));
            internal Task Dispatch(GuardianAltarSlotRequest request, byte[] body = null)
            {
                if (!Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT, out var handler))
                    throw new InvalidOperationException("Guardian SET_SLOT handler was not registered.");
                body ??= RequestBody(request);
                return handler(Session, new GamePacketHeader { cmd = 1,
                    type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT,
                    length = checked((uint)(14 + body.Length)) }, body);
            }
            internal Task DispatchExchange(GuardianAltarExchangeRequest request, byte[] body = null)
            {
                if (!Registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_EXCHANGE_SLOT, out var handler))
                    throw new InvalidOperationException("Guardian EXCHANGE_SLOT handler was not registered.");
                body ??= ExchangeBody(request);
                return handler(Session, new GamePacketHeader { cmd = 1,
                    type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_EXCHANGE_SLOT,
                    length = checked((uint)(14 + body.Length)) }, body);
            }
            private Task Send(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
            {
                var callback = BeforePrepare;
                BeforePrepare = null;
                callback?.Invoke();
                var packets = prepare();
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    throw new IOException("Guardian slot ACK transport selftest failure");
                }
                Packets.AddRange(packets.Select(x => x.ToArray()));
                var after = AfterWrite;
                AfterWrite = null;
                after?.Invoke();
                return Task.CompletedTask;
            }
            public void Dispose()
            {
                _coordinator.Dispose();
                foreach (var session in _ownedSessions) session.Close();
            }
        }

        private static GuardianAltarMenuDefinition Definition()
        {
            // Synthetic catalog IDs are fixtures, not production special cases.
            var shop = "[ridable id]\n1\n[unit]\n"
                + Entry(100001, "1 100001 0\n")
                + Entry(LearnedUnit, "0 100002 0\n1 100002 2\n")
                + Entry(100003, "1 100003 0\n2 100003 5\n3 100003 9\n3 100004 9\n")
                + "[/unit]\n[skill]\n"
                + Entry(100010, "1 0 0\n", settable: false)
                + Entry(InitialSkill, "1 4 0\n")
                + Entry(ActiveSkill, "0 5 0\n1 5 3\n")
                + Entry(100013, "1 6 0\n", unlock: 700001)
                + Entry(100014, "1 7 0\n")
                + Entry(100015, "0 8 0\n1 8 4\n")
                + Entry(100016, "0 9 0\n1 9 4\n")
                + "[/skill]\n[tower]\n" + Entry(100020, "1 65530 0\n")
                + "[/tower]\n[/ridable id]\n";
            var config = GuardianAltarConfigFile.Parse("[daily refill ticket]\n3\n[/refill ticket]\n", shop);
            var stages = GuardianAltarStageMapFile.Parse("[area]\n0\n[district]\n1\n[stage]\n"
                + "700001 0 0 0\n700002 1 0 0\n[/stage]\n[/area]\n");
            return GuardianAltarMenuDefinition.Create(config, stages, _ => true);
        }

        private static string Entry(int id, string rows, int unlock = 0, bool settable = true)
            => $"[id]\n{id}\n[unlock stage index]\n{unlock}\n[is settable slot]\n{(settable ? 1 : 0)}\n"
                + "[field name]\n`level` `index` `price`\n[/field name]\n[list]\n" + rows + "[/list]\n[/id]\n";

        private static GuardianAltarSlot Slot(GuardianAltarProgressSnapshot snapshot, int type, int index)
            => snapshot.Slots.Single(x => x.SlotType == type && x.SlotIndex == index);
        private static bool Same(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.CharacterId == right.CharacterId && left.Header == right.Header
                && left.Stages.SequenceEqual(right.Stages) && left.Purchases.SequenceEqual(right.Purchases)
                && left.Slots.SequenceEqual(right.Slots) && left.Achievements.SequenceEqual(right.Achievements);
        private static bool SameExceptSlot(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right,
            int type, byte index)
            => left.CharacterId == right.CharacterId && left.Header == right.Header
                && left.Stages.SequenceEqual(right.Stages) && left.Purchases.SequenceEqual(right.Purchases)
                && left.Achievements.SequenceEqual(right.Achievements)
                && left.Slots.Where(x => x.SlotType != type || x.SlotIndex != index)
                    .SequenceEqual(right.Slots.Where(x => x.SlotType != type || x.SlotIndex != index));
        private static bool IsSuccess(byte[] packet, GuardianAltarSlotRequest request, GuardianAltarSlot committed)
            => packet.Length == 34 && packet[0] == 1
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT
                && BitConverter.ToUInt32(packet, 3) == packet.Length
                && packet.AsSpan(15).SequenceEqual(GuardianAltarSlotAckBodyBuilder.Build(request, committed));
        private static bool IsFailure(byte[] packet)
            => packet.Length == 17 && packet[0] == 1
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SET_SLOT
                && packet[15] == 0 && packet[16] == 4;
        private static bool Notice(byte[] packet)
            => packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE;
        private static byte[] RequestBody(GuardianAltarSlotRequest request)
        {
            var body = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14), request.RidableId);
            body[18] = request.Equip ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(19), request.SlotType);
            body[23] = request.SlotIndex;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), request.ResourceId);
            return body;
        }
        private static byte[] Mutate(byte[] source, int offset, params byte[] values)
        {
            var result = source.ToArray();
            values.CopyTo(result, offset);
            return result;
        }
        private static GameSession GameSessionIdentity()
            => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return true; }
            return false;
        }
        private static void Sql(GameDatabase database, string sql) => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });
        private static long Scalar(GameDatabase database, string sql) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        });
    }
}
