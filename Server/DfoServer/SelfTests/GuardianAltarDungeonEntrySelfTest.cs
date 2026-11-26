using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.SelfTests
{
    internal static partial class GuardianAltarDungeonEntrySelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_DUNGEON_ENTRY selftest ===");
            var assertions = 0;
            var failures = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success) failures++;
            }
            void Section(string name, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(name + " exception: " + exception, false); }
            }

            Section("strict Guardian selector", () => CheckParser(Check));
            Section("prepared batch per-packet fence", () => CheckPreparedBatchAsync(Check).GetAwaiter().GetResult());
            GuardianAltarMenuDefinition definition = null;
            Section("real PVF start plans", () =>
            {
                definition = GuardianAltarMenuDefinition.Load();
                CheckPlans(Check, definition);
            });
            if (definition != null)
            {
                Section("Guardian combat continuation", () => CheckCombatAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian wave loading-generation fence", () => CheckCombatGenerationAsync(Check, definition).GetAwaiter().GetResult());
                Section("authorized town lifecycle", () => CheckLifecycle(Check, definition));
                Section("Guardian shared lock ordering", () => CheckLockOrderAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian entry and rejection", () => CheckEntryAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian source fencing", () => CheckFencesAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian failure cleanup", () => CheckFailuresAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian energy coordinator", () => CheckEnergyAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian formal clear resource plans", () => CheckClearPlans(Check, definition));
                Section("Guardian ordinary first and repeated durable clear projection", () => CheckOrdinaryClearAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian survival menu unlock eligibility", () => CheckSurvivalUnlockEligibility(Check, definition));
                Section("Guardian survival start and continuation wire", () => CheckSurvivalPlans(Check, definition));
                Section("Guardian survival entry and selector boundaries", () => CheckSurvivalEntryAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian dedicated failed settlement", () => CheckFailedClearAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian dedicated character death and coin refusal", () => CheckCharacterDeathAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian survival death durable result and projection", () => CheckSurvivalDeathAsync(Check, definition).GetAwaiter().GetResult());
                Section("Guardian result-button selection and exit", () => CheckEplpSelectionAsync(Check, definition).GetAwaiter().GetResult());
            }
            Console.WriteLine($"GUARDIAN_ALTAR_DUNGEON_ENTRY assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckParser(Action<string, bool> check)
        {
            var good = Request(3511);
            check("observed 15B selector preserves stage and difficulty without hell semantics",
                GuardianAltarSelectDungeonRequestParser.TryParse(good, out var command)
                && command.DungeonId == 3511 && command.Difficulty == 0 && !command.SurvivalSelector);
            foreach (var difficulty in new byte[] { 0, 1, 2 })
                check($"ordinary Guardian difficulty {difficulty} is representable",
                    GuardianAltarSelectDungeonRequestParser.TryParse(Request(3511, difficulty), out var parsed)
                    && parsed.Difficulty == difficulty);
            check("null selector is rejected", !GuardianAltarSelectDungeonRequestParser.TryParse(null, out _));
            foreach (var length in new[] { 0, 3, 4, 9, 14, 16, 1024 })
                check($"unproven {length}B selector is rejected",
                    !GuardianAltarSelectDungeonRequestParser.TryParse(new byte[length], out _));
            foreach (var id in new uint[] { 0, (uint)short.MaxValue + 1U, uint.MaxValue })
            {
                var body = good.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(body, id);
                check($"invalid Guardian stage id {id} is rejected",
                    !GuardianAltarSelectDungeonRequestParser.TryParse(body, out _));
            }
            foreach (var change in new (int Offset, byte Value)[]
            {
                (4, 3), (5, 0), (5, 1), (6, 1), (7, 0), (8, 0),
                (9, 1), (10, 1), (11, 1), (12, 1), (13, 1), (14, 1)
            })
            {
                var body = good.ToArray();
                body[change.Offset] = change.Value;
                check($"selector rejects unproven byte {change.Offset}={change.Value}",
                    !GuardianAltarSelectDungeonRequestParser.TryParse(body, out _));
            }
        }

        private static void CheckPlans(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            // These assertions bind the test to the current real resource,
            // rather than silently replacing a missing PVF with fake maps.
            check("current PVF first registered Guardian stage is 3511", definition.FirstDungeonId == 3511);
            foreach (var unknownMapId in new[] { 0, int.MaxValue })
                check($"unregistered MAP {unknownMapId} raises the typed resource failure before a raw map loader",
                    Throws<InvalidDataException>(() => GuardianAltarMenuDefinition.LoadRegisteredMap(unknownMapId)));
            var progress = Fresh(definition, 1);
            var expectedMaps = new[] { 25081, 25082, 25083 };
            for (byte difficulty = 0; difficulty < 3; difficulty++)
            {
                var unlocked = Copy(progress, stages: new[]
                    { new GuardianAltarStage(definition.FirstDungeonId, difficulty - 1) });
                var plan = definition.CreateStartPlan(definition.FirstDungeonId, difficulty, unlocked);
                check($"real difficulty {difficulty} selects its own Guardian MAP",
                    plan.DungeonId == definition.FirstDungeonId && plan.Difficulty == difficulty
                    && plan.MapId == expectedMaps[difficulty] && ReferenceEquals(plan.Progress, unlocked));
            }
            check("fresh stage permits easy but rejects medium and hard without a completed earlier difficulty",
                Throws<InvalidOperationException>(() => definition.CreateStartPlan(definition.FirstDungeonId, 1, progress))
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(definition.FirstDungeonId, 2, progress)));
            var easyCleared = Copy(progress, stages: new[] { new GuardianAltarStage(definition.FirstDungeonId, 0) });
            check("completed easy stage permits medium but cannot skip directly to hard",
                definition.CreateStartPlan(definition.FirstDungeonId, 1, easyCleared).Difficulty == 1
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(definition.FirstDungeonId, 2, easyCleared)));
            var fresh = definition.CreateStartPlan(definition.FirstDungeonId, 0, progress);
            var stage = GuardianAltarDungeonPacketBuilder.BuildStageInfo(fresh);
            var map = GuardianAltarDungeonPacketBuilder.BuildStartMap(fresh);
            check("dedicated stage body has exactly two Int32 fields, not ordinary DUNGEON_INFO",
                stage.Length == 8 && I32(stage, 0) == fresh.DungeonId && I32(stage, 4) == fresh.Difficulty);
            check("fresh start body is 65B: 20B header, two 12B phases and one 13B center",
                fresh.Phases.Count == 2 && fresh.Centers.Count == 1 && map.Length == 65
                && I32(map, 0) == 0 && I32(map, 4) == 0 && I32(map, 8) == fresh.MapId
                && I32(map, 12) == fresh.DurationMilliseconds && I32(map, 16) == fresh.MapBasisLevel
                && U32(map, 20) == 2 && U32(map, 48) == 1);
            for (var index = 0; index < fresh.Phases.Count; index++)
            {
                var offset = 24 + index * 12;
                var phase = fresh.Phases[index];
                check($"phase {index} preserves typed index/start/type widths and ordering",
                    I32(map, offset) == phase.PhaseIndex && I32(map, offset + 4) == phase.StartsAtMilliseconds
                    && I32(map, offset + 8) == phase.PhaseType);
            }
            var center = fresh.Centers.Single();
            check("center fields preserve ushort code and zero native source extensions",
                I32(map, 52) == 0 && U16(map, 56) == 0 && U16(map, 58) == center.MonsterCode
                && map[60] == center.Level && map[61] == center.MonsterType
                && map.AsSpan(62, 3).SequenceEqual(new byte[3]));
            check("real failure center belongs to the dedicated START_MAP wire sequence zero",
                center.IsFailureCenter && U16(map, 56) == 0
                && fresh.CombatMap.CenterIndices.Contains(center.MonsterCode));

            // Serialization fixture only: current real fresh PVF has one center.
            // Copy its valid plan/center and vary the second row deliberately;
            // this does not claim that these two rows are a registered MAP.
            var syntheticCenters = new[]
            {
                center,
                center with
                {
                    MonsterCode = center.MonsterCode == ushort.MaxValue
                        ? (ushort)1 : (ushort)(center.MonsterCode + 1),
                    Level = center.Level == byte.MaxValue ? (byte)1 : (byte)(center.Level + 1),
                    MonsterType = center.MonsterType == 0 ? (byte)1 : (byte)0
                }
            };
            var twoCenterPlan = new GuardianAltarDungeonStartPlan(fresh.DungeonId, fresh.Difficulty,
                fresh.MapId, fresh.MapBasisLevel, fresh.DurationMilliseconds,
                fresh.Phases, syntheticCenters, fresh.Progress);
            var twoCenterBody = GuardianAltarDungeonPacketBuilder.BuildStartMap(twoCenterPlan);
            check("synthetic two-center serialization adds exactly one 13B row, with count two and 78B total",
                twoCenterBody.Length == map.Length + 13 && twoCenterBody.Length == 78 && U32(twoCenterBody, 48) == 2);
            for (var index = 0; index < syntheticCenters.Length; index++)
            {
                var rowOffset = 52 + index * 13;
                var expected = syntheticCenters[index];
                check($"synthetic center {index} preserves sequential ushort id, code, level and type",
                    I32(twoCenterBody, rowOffset) == 0 && U16(twoCenterBody, rowOffset + 4) == index
                    && U16(twoCenterBody, rowOffset + 6) == expected.MonsterCode
                    && twoCenterBody[rowOffset + 8] == expected.Level
                    && twoCenterBody[rowOffset + 9] == expected.MonsterType
                    && twoCenterBody.AsSpan(rowOffset + 10, 3).SequenceEqual(new byte[3]));
            }
            check("an unregistered or still-locked stage cannot create a start plan",
                Throws<InvalidOperationException>(() => definition.CreateStartPlan(1, 0, progress))
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(3512, 0, progress)));
            check("zero remaining tickets denies start without consuming or fabricating a ticket",
                Throws<InvalidOperationException>(() => definition.CreateStartPlan(3511, 0,
                    Copy(progress, header: progress.Header with { FreeTickets = 0, SecondaryTickets = 0 })))
                && progress.Header.FreeTickets == definition.DailyRefillTicket);
            check("survival cannot accidentally enter the ordinary three-difficulty start path",
                definition.ContainsDungeon(3523)
                && Throws<InvalidOperationException>(() => definition.CreateStartPlan(3523, 0,
                    Copy(progress, stages: new[] { new GuardianAltarStage(3511, -1), new GuardianAltarStage(3523, -1) }))));
            check("unknown Guardian difficulty fails closed",
                Throws<InvalidOperationException>(() => definition.CreateStartPlan(3511, 3, progress)));

            var maximumPhases = new GuardianAltarDungeonStartPlan(fresh.DungeonId, fresh.Difficulty,
                fresh.MapId, fresh.MapBasisLevel, 256,
                Enumerable.Range(0, 256).Select(i => new GuardianAltarPhaseSummary(i, i, 0)),
                fresh.Centers, fresh.Progress);
            var maximumBody = GuardianAltarDungeonPacketBuilder.BuildStartMap(maximumPhases);
            check("256 phases remain representable by the A21 low-byte phase-index consumer",
                maximumPhases.Phases.Count == 256 && U32(maximumBody, 20) == 256
                && maximumBody.Length == 20 + 4 + 256 * 12 + 4 + 13 * fresh.Centers.Count
                && I32(maximumBody, 24 + 255 * 12) == 255);
            check("257 phases are rejected instead of wrapping phase index 256 to client index zero",
                Throws<InvalidOperationException>(() => new GuardianAltarDungeonStartPlan(
                    fresh.DungeonId, fresh.Difficulty, fresh.MapId, fresh.MapBasisLevel, 257,
                    Enumerable.Range(0, 257).Select(i => new GuardianAltarPhaseSummary(i, i, 0)),
                    fresh.Centers, fresh.Progress)));
        }

        private static void CheckClearPlans(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            var stageList = PvfLib.LstFile.Parse(GameWorld.PvfArchiveAccessor.ReadText("stagemap/stagemap.lst"));
            var stageEntry = stageList.GetById(definition.RidableId)
                ?? throw new InvalidDataException("Current Guardian ridable has no formal stage-map registration.");
            var stageMap = PvfLib.GuardianAltarStageMapFile.Parse(
                GameWorld.PvfArchiveAccessor.ReadText("stagemap/" + stageEntry.FilePath));
            if (!stageMap.IsValid) throw new InvalidDataException(stageMap.InvalidReason);
            var ordered = stageMap.Stages.Where(x => !x.IsSurvival).OrderBy(x => x.AreaId)
                .ThenBy(x => x.DistrictId).ThenBy(x => x.StageOrder).ToArray();
            check("formal Guardian stage map supplies twelve ordinary clear-resource owners", ordered.Length == 12);
            var covered = 0;
            for (var stageIndex = 0; stageIndex < ordered.Length; stageIndex++)
            {
                var dungeonId = ordered[stageIndex].DungeonId;
                var shared = GameWorld.DungeonCatalog.GetDungeonFile(dungeonId);
                var rewards = PvfLib.GuardianAltarClearRewardFile.Parse(shared);
                if (!rewards.IsValid) throw new InvalidDataException(rewards.InvalidReason);
                for (byte difficulty = 0; difficulty < 3; difficulty++)
                {
                    var fresh = Fresh(definition, 1);
                    var stages = fresh.Stages.Where(x => x.DungeonId != dungeonId)
                        .Append(new GuardianAltarStage(dungeonId, difficulty - 1));
                    var progress = Copy(fresh, stages: stages);
                    var plan = definition.CreateStartPlan(dungeonId, difficulty, progress);
                    var expected = rewards.Difficulties.Single(x =>
                        x.DifficultyName == new[] { "easy", "medium", "hard" }[difficulty]);
                    var next = stageIndex + 1 < ordered.Length
                        ? new[] { ordered[stageIndex + 1].DungeonId } : Array.Empty<int>();
                    check($"formal DGN {dungeonId} difficulty {difficulty} freezes exactly its typed rewards and stage progress",
                        plan.ClearPlan != null && plan.ClearPlan.ProgressPlan.DungeonId == dungeonId
                        && plan.ClearPlan.ProgressPlan.Difficulty == difficulty
                        && plan.ClearPlan.ProgressPlan.StarsOnImprovement == new[] { 3, 2, 1 }[difficulty]
                        && plan.ClearPlan.ProgressPlan.NextDungeonIds.SequenceEqual(next)
                        && plan.ClearPlan.Rewards.Select(x => (x.Kind, x.ItemId, x.Count))
                            .SequenceEqual(expected.Items.Select(x => (x.Kind, x.ItemId, x.Count))));
                    var wire = GuardianAltarDungeonPacketBuilder.BuildStartMap(plan);
                    var centersOffset = 24 + plan.Phases.Count * 12 + 4;
                    check($"formal DGN {dungeonId} difficulty {difficulty} preserves the failure center's actual wire index",
                        plan.Centers.Count > 0 && plan.Centers.Where(x => x.IsFailureCenter).Any()
                        && plan.Centers.Select((center, index) => !center.IsFailureCenter
                            || U16(wire, centersOffset + index * 13 + 4) == index).All(x => x));
                    covered++;
                }
            }
            check("all twelve formal ordinary stages and three difficulties have a frozen clear plan", covered == 36);

            // Catalog-order fixture only: sparse and descending IDs deliberately
            // rule out numeric adjacency as a next-stage authorization source.
            var sparse = PvfLib.GuardianAltarStageMapFile.Parse(
                "[area]\n0\n[district]\n1\n[stage]\n9001 0 0 0\n12001 1 0 0\n7001 2 0 0\n[/stage]\n[/area]\n");
            if (!sparse.IsValid) throw new InvalidDataException(sparse.InvalidReason);
            var sparseDefinition = new GuardianAltarMenuDefinition(definition.DailyRefillTicket, definition.RidableId,
                9001, definition.InitialPurchases, definition.InitialSlots, new[] { 9001, 12001, 7001 },
                knownPurchases: definition.ShopEntries.SelectMany(shop => shop.Levels.Select(level =>
                    new GuardianAltarPurchase(shop.ShopType, shop.ResourceId, level.Level, level.Price))),
                shopEntries: definition.ShopEntries,
                orderedStages: sparse.Stages);
            check("next ordinary stage follows sparse catalog row order rather than DungeonId plus one",
                sparseDefinition.CreateClearProgressPlan(9001, 0).NextDungeonIds.SequenceEqual(new[] { 12001 })
                && sparseDefinition.CreateClearProgressPlan(12001, 1).NextDungeonIds.SequenceEqual(new[] { 7001 })
                && sparseDefinition.CreateClearProgressPlan(7001, 2).NextDungeonIds.Count == 0);
        }

        private static async Task CheckLockOrderAsync(Action<string, bool> check,
            GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            var session = fixture.CreateSession(1);
            var runGate = session.Player.DungeonRunTransitionGate;
            using var leaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            IDisposable characterLease = null;
            Task<bool> entry = null;
            var runGateHeld = false;
            var characterAcquired = false;
            try
            {
                await runGate.WaitAsync();
                runGateHeld = true;
                // Start synchronously until its first unavailable gate. If
                // Guardian takes the character gate before the run gate, this
                // controlled Party-shaped owner would deadlock without timeout.
                entry = fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
                try
                {
                    characterLease = await fixture.Transitions.AcquireAsync(
                        session.Player.CharacterId, leaseTimeout.Token);
                    characterAcquired = characterLease != null && fixture.Transitions.IsCurrent(session);
                }
                catch (OperationCanceledException) when (leaseTimeout.IsCancellationRequested)
                {
                    // Expected failure on the inverted implementation: release
                    // the controlled owner below so the request can still drain.
                }
            }
            finally
            {
                characterLease?.Dispose();
                leaseTimeout.Cancel();
                if (runGateHeld) runGate.Release();
            }
            check("a Party-shaped run-gate holder can acquire the shared character lease while Guardian waits",
                characterAcquired);
            Exception completionError = null;
            if (entry != null)
            {
                try { await entry.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) { completionError = exception; }
            }
            check("lock-order regression drains the Guardian request after releasing controlled gates, including timeout failure",
                entry != null && entry.IsCompleted && completionError == null
                && session.Player.CurrentRun?.RunState == DungeonRunState.Active
                && HasStartMap(fixture.Packets) && runGate.CurrentCount == 1);
        }

        private static void CheckLifecycle(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            var session = fixture.CreateSession(1);
            var anchor = Anchor(session);
            var generation = session.Player.CurrentDungeonRunGeneration;
            var policy = DungeonRewardPolicy.GuardianAltar;
            check("Guardian start policy disables every ordinary reward, quest and settlement path",
                policy.Kind == DungeonRewardPolicyKind.GuardianAltar
                && !policy.AllowsMonsterExperience && !policy.AllowsMonsterDrops && !policy.AllowsQuestDrops
                && !policy.AllowsQuestProgress && !policy.AllowsPetExperience
                && !policy.AllowsClearCommit && !policy.AllowsSettlement);
            DungeonInstance Instance() => new DungeonInstance((short)definition.FirstDungeonId, 0, policy);
            check("false source fence cannot create or advance a participant run",
                !DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, Instance(), anchor, generation, () => false, out _)
                && session.Player.CurrentRun == null && session.Player.CurrentDungeonRunGeneration == generation);
            check("stale generation cannot create a participant run",
                !DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, Instance(), anchor, generation + 1, () => true, out _));
            var ending = Instance();
            ending.TryBeginEnding();
            check("ending physical instance cannot accept an authorized town start",
                !DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, ending, anchor, generation, () => true, out _));
            var existingRun = new DungeonRun((short)definition.FirstDungeonId, 0);
            session.Player.CurrentRun = existingRun;
            check("authorized town start cannot replace an existing run",
                !DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, Instance(), anchor, generation, () => true, out _)
                && ReferenceEquals(session.Player.CurrentRun, existingRun));
            session.Player.CurrentRun = null;
            var selection = session.Player.BeginDungeonSelection(anchor, false);
            check("authorized town start cannot steal an ordinary selection",
                !DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, Instance(), anchor, generation, () => true, out _)
                && ReferenceEquals(session.Player.CurrentDungeonSelection, selection));
            session.Player.TryInvalidateDungeonSelection(selection);
            var accepted = DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, Instance(), anchor, generation, () => true, out var run);
            check("authorized start creates exactly one Selecting run with a new generation and real return anchor",
                accepted && ReferenceEquals(session.Player.CurrentRun, run)
                && run.RunState == DungeonRunState.Selecting && run.RunGeneration == generation + 1
                && session.Player.CurrentDungeonSelection == null && run.TownReturnAnchor.Equals(anchor)
                && ReferenceEquals(run.QuestSnapshot, QuestRunSnapshot.Empty)
                && run.CaptureExperienceBonusSnapshot().Equals(DungeonParticipantExperienceBonusSnapshot.None));
            check("lifecycle helper does not prematurely activate, leave town or project packets",
                session.Player.UserState == 0 && session.Player.TownPresenceReady && fixture.Packets.Count == 0);
            fixture.EndMemoryRun(session, run.CaptureIdentity());
        }

        private static async Task CheckEntryAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = fixture.CreateSession(1);
                var before = Fresh(definition, 1);
                var handled = await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
                var run = session.Player.CurrentRun;
                check("resource-classified selector creates the single authoritative active Guardian run",
                    handled && run?.RunState == DungeonRunState.Active
                    && run.Instance.Mechanisms.GuardianAltar?.DungeonId == definition.FirstDungeonId
                    && run.RewardPolicy.Kind == DungeonRewardPolicyKind.GuardianAltar
                    && run.QuestSnapshot == QuestRunSnapshot.Empty && session.Player.CurrentDungeonSelection == null);
                check("full entry projection is USER_STATE then dedicated STAGE_INFO then dedicated START_MAP",
                    fixture.Packets.Select(Type).SequenceEqual(new[] {
                        (ushort)NotiPacketTypeA21.USER_STATE, (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_STAGE_INFO,
                        (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP })
                    && fixture.Packets.All(x => x[0] == 0 && x.Length == I32(x, 3))
                    && fixture.Packets[1].Length == 15 + 8 && fixture.Packets[2].Length == 15 + 65);
                check("start freezes typed menu facts without spending tickets or executing waves",
                    fixture.Progress.Reads == 1 && !fixture.Progress.ReadInPrepare
                    && SameProgress(run.Instance.Mechanisms.GuardianAltar.Progress, before)
                    && run.Instance.Mechanisms.GuardianAltar.Progress.Header.FreeTickets == definition.DailyRefillTicket);
                check("entry registers a real shared loading room and one participant before projection",
                    fixture.LoadingRegistrations == 1 && fixture.LoadingGeneration > 0
                    && fixture.Instances.CaptureInstanceParticipantRoster(run.Instance.Identity).Count == 1
                    && run.CaptureParticipantRoomIdentity().IsValid
                    && session.Player.UserState == 1 && session.Player.TownPresenceReady
                    && !SessionDirectory.IsTownPresence(session.Player));
                var originalId = run.RunId;
                var originalGeneration = run.RunGeneration;
                var packetCount = fixture.Packets.Count;
                await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
                check("duplicate starts do not reset loading, allocate another run or resend the stage/map clock",
                    ReferenceEquals(session.Player.CurrentRun, run) && run.RunId == originalId
                    && run.RunGeneration == originalGeneration && fixture.Packets.Count == packetCount
                    && fixture.Progress.Reads == 1 && fixture.LoadingRegistrations == 1);
                var ready = fixture.LastRoom.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
                var duplicateReady = fixture.LastRoom.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
                check("dedicated room reuses common loading barrier and consumes ready only once",
                    ready.Accepted && ready.Released && !duplicateReady.Accepted);
                fixture.EndMemoryRun(session, run.CaptureIdentity());
            }

            foreach (var item in new (string Name, Action<Fixture, EnhancedClientSession> Change)[]
            {
                ("closed", (f, s) => f.Opening.Close()),
                ("non-town state", (f, s) => s.Player.UserState = 1),
                ("town presence not ready", (f, s) => s.Player.TownPresenceReady = false),
                ("ordinary selection pending", (f, s) => s.Player.DungeonSelectionPending = true),
                ("ordinary selection exists", (f, s) => s.Player.BeginDungeonSelection(Anchor(s), false)),
                ("party", (f, s) => f.InParty = true), ("trade", (f, s) => f.Trading = true),
                ("mercenary assigned", (f, s) => f.Mercenary.Assigned = true),
                ("level 49", (f, s) => s.Player.Level = 49)
            })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                item.Change(fixture, session);
                check(item.Name + " is consumed by Guardian with a proven failure ACK and no new run",
                    await fixture.DispatchAsync(session, Request(definition.FirstDungeonId))
                    && IsRejection(fixture.Packets) && session.Player.CurrentRun == null
                    && fixture.Progress.Reads == 0 && fixture.LoadingRegistrations == 0);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = fixture.CreateSession(1);
                var malformed = Request(definition.FirstDungeonId);
                malformed[5] = 1;
                check("malformed Guardian selector cannot fall through into ordinary hell selection",
                    await fixture.DispatchAsync(session, malformed) && IsRejection(fixture.Packets)
                    && fixture.Progress.Reads == 0 && session.Player.CurrentRun == null);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = fixture.CreateSession(1);
                check("ordinary/unregistered resources remain with the unchanged ordinary handler",
                    !await fixture.DispatchAsync(session, Request(17)) && fixture.Packets.Count == 0
                    && fixture.Progress.Reads == 0 && session.Player.CurrentRun == null);
                check("wrong command class or packet type cannot create Guardian state",
                    !await fixture.DispatchAsync(session, Request(3511), cmd: 0)
                    && !await fixture.DispatchAsync(session, Request(3511), type: (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_START_GAME)
                    && fixture.Packets.Count == 0 && session.Player.CurrentRun == null);
            }
        }

        private static async Task CheckFencesAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            foreach (var phase in new[] { "load", "prepare" })
            foreach (var change in new[] { "GameSession", "session directory", "account", "day", "window", "level", "party", "trade" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                Action mutation = () =>
                {
                    // A real displaced session is torn down by the lifecycle
                    // owner. Simulate that concurrent memory transition before
                    // changing its identity; do not let this no-DB fixture invoke
                    // production experience persistence on an uninitialized token.
                    if (phase == "prepare" && (change is "GameSession" or "session directory" or "account")
                        && session.Player.CurrentRun is { } oldRun)
                        fixture.EndMemoryRun(session, oldRun.CaptureIdentity());
                    switch (change)
                    {
                        case "GameSession": session.GameSession = GameSessionIdentity(); break;
                        case "session directory": fixture.CreateSession(1); break;
                        case "account": session.Account = new AccountRecord { AccountId = 12 }; break;
                        case "day": fixture.Now = fixture.Now.AddDays(1); break;
                        case "window": fixture.Opening.Close(); break;
                        case "level": session.Player.Level = 49; break;
                        case "party": fixture.InParty = true; break;
                        case "trade": fixture.Trading = true; break;
                    }
                };
                if (phase == "load") fixture.Progress.DuringLoad = mutation;
                else fixture.BeforePrepare = mutation;
                var exception = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                check(change + " changed during " + phase + " cannot project Guardian combat or retain an abandoned run",
                    exception == null && !HasStartMap(fixture.Packets) && session.Player.CurrentRun == null
                    && fixture.Instances.CaptureInstanceParticipantRoster(fixture.LastInstanceIdentity).Count == 0);
                if (change is "GameSession" or "session directory" or "account")
                    check(change + " changed during " + phase + " cannot send packets to a stale identity",
                        fixture.Packets.Count == 0);
            }
            foreach (var change in new[] { "GameSession", "session directory", "account", "window", "day" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                fixture.AfterFirstWrite = () =>
                {
                    if (change is "GameSession" or "session directory" or "account")
                        fixture.EndMemoryRun(session, session.Player.CurrentRun.CaptureIdentity());
                    switch (change)
                    {
                        case "GameSession": session.GameSession = GameSessionIdentity(); break;
                        case "session directory": fixture.CreateSession(1); break;
                        case "account": session.Account = new AccountRecord { AccountId = 12 }; break;
                        case "window": fixture.Opening.Close(); break;
                        case "day": fixture.Now = fixture.Now.AddDays(1); break;
                    }
                };
                var exception = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                check(change + " changed after first write cancels the remaining stage/map frames and cleans the old run",
                    exception == null && fixture.Packets.Count == 1 && Type(fixture.Packets[0]) == (ushort)NotiPacketTypeA21.USER_STATE
                    && !HasStartMap(fixture.Packets) && session.Player.CurrentRun == null
                    && fixture.Instances.CaptureInstanceParticipantRoster(fixture.LastInstanceIdentity).Count == 0);
            }
        }

        private static async Task CheckFailuresAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            foreach (var error in new Exception[]
            {
                new InvalidDataException("invalid Guardian resource"),
                new InvalidOperationException("inconsistent Guardian progress"),
                new NotSupportedException("opaque progress dependency failure")
            })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                fixture.Progress.Error = error;
                var observed = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                var visibleFailure = error is NotSupportedException
                    ? observed is NotSupportedException && fixture.Packets.Count == 0
                    : observed == null && IsRejection(fixture.Packets);
                check(error.GetType().Name + " while loading progress remains observable without fake success or run state",
                    visibleFailure && session.Player.CurrentRun == null
                    && fixture.LoadingRegistrations == 0);
            }
            foreach (var invalid in new[] { "wrong character", "locked stage", "zero tickets" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                if (invalid == "wrong character") fixture.Progress.WrongCharacter = true;
                else if (invalid == "locked stage") fixture.Progress.Transform = x => Copy(x, stages: new[] { new GuardianAltarStage(3512, -1) });
                else fixture.Progress.Transform = x => Copy(x, header: x.Header with { FreeTickets = 0, SecondaryTickets = 0 });
                var observed = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                check(invalid + " refuses start before allocating a participant run",
                    observed == null && IsRejection(fixture.Packets) && session.Player.CurrentRun == null);
            }
            foreach (var error in new Exception[]
            {
                new IOException("partial Guardian transport failure"),
                new NotSupportedException("opaque Guardian send failure")
            })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = fixture.CreateSession(1);
                fixture.FailAfterPackets = 1;
                fixture.SendError = error;
                var observed = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                check(error.GetType().Name + " after USER_STATE terminates only the failed run through the return owner",
                    (error is NotSupportedException ? observed is NotSupportedException : observed == null)
                    && fixture.Packets.Count == 1 && !HasStartMap(fixture.Packets)
                    && fixture.ReturnAttempts == 1 && fixture.ReturnApplied == 1
                    && session.Player.CurrentRun == null && fixture.LastRun.RunState == DungeonRunState.Ended
                    && fixture.Instances.CaptureInstanceParticipantRoster(fixture.LastInstanceIdentity).Count == 0);
                fixture.FailAfterPackets = -1;
                await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
                check(error.GetType().Name + " cleanup permits a fresh legitimate retry, without ticket mutation",
                    session.Player.CurrentRun?.RunState == DungeonRunState.Active
                    && fixture.Progress.Reads == 2
                    && session.Player.CurrentRun.Instance.Mechanisms.GuardianAltar.Progress.Header.FreeTickets == definition.DailyRefillTicket);
            }
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = fixture.CreateSession(1);
                fixture.FailAfterPackets = 1;
                fixture.SendError = new IOException("late failure");
                DungeonRun replacement = null;
                fixture.BeforeReturn = () =>
                {
                    var old = session.Player.CurrentRun;
                    fixture.EndMemoryRun(session, old.CaptureIdentity());
                    var instance = new DungeonInstance((short)definition.FirstDungeonId, 0, DungeonRewardPolicy.GuardianAltar);
                    if (!DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, instance, Anchor(session),
                        session.Player.CurrentDungeonRunGeneration, () => true, out replacement))
                        throw new InvalidOperationException("replacement test run could not begin");
                };
                var observed = await ObserveAsync(() => fixture.DispatchAsync(session, Request(definition.FirstDungeonId)));
                check("a delayed return callback cannot terminate a newer run with a different identity",
                    observed == null && replacement != null && ReferenceEquals(session.Player.CurrentRun, replacement)
                    && replacement.RunState == DungeonRunState.Selecting
                    && fixture.ReturnAttempts == 1 && fixture.ReturnApplied == 0);
            }
        }

        private static async Task CheckPreparedBatchAsync(Action<string, bool> check)
        {
            using var sendLock = new SemaphoreSlim(1, 1);
            var packets = new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 } };
            var written = new List<byte>();
            await EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => packets,
                packet => { written.Add(packet[0]); return Task.CompletedTask; });
            check("shared prepared batch keeps existing non-fenced callers and ordering unchanged",
                written.SequenceEqual(new byte[] { 1, 2, 3 }) && sendLock.CurrentCount == 1);
            written.Clear();
            var canceled = await ObserveAsync(() => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(
                sendLock, () => packets, packet => { written.Add(packet[0]); return Task.CompletedTask; }, () => false));
            check("an expired initial write fence emits no frame and releases the send lock",
                canceled is OperationCanceledException && written.Count == 0 && sendLock.CurrentCount == 1);
            written.Clear();
            var current = true;
            canceled = await ObserveAsync(() => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(
                sendLock, () => packets, async packet =>
                {
                    written.Add(packet[0]);
                    await Task.Yield();
                    current = false;
                }, () => current));
            check("a source displaced while first frame is awaited cannot write the remaining prepared frames",
                canceled is OperationCanceledException && written.SequenceEqual(new byte[] { 1 })
                && sendLock.CurrentCount == 1);
            written.Clear();
            await EnhancedClientSession.SendPreparedPacketBatchCoreAsync(sendLock, () => packets,
                packet => { written.Add(packet[0]); return Task.CompletedTask; }, () => true);
            check("canceling an old prepared batch does not poison a later valid batch",
                written.SequenceEqual(new byte[] { 1, 2, 3 }) && sendLock.CurrentCount == 1);
        }

        private static async Task CheckEnergyAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            var session = await fixture.CreateConnectedSessionAsync(301);
            var pauseHeader = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE };
            var registry = new GameCommandRegistry();
            registry.RegisterGroup("guardian-altar-runtime", fixture.Coordinator.RegisterRuntimeHandlers);
            check("the real Guardian runtime registration resolves the current A21 pause command",
                registry.Count == 3 && registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE, out _)
                && registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_UPGRADE_GAGE, out _)
                && registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SUMMON_UNIT, out _));
            registry.TryGetValue((ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE, out var pauseHandler);
            check("duplicate Guardian runtime registration fails instead of silently replacing a handler",
                Throws<InvalidOperationException>(() => registry.RegisterGroup("duplicate", fixture.Coordinator.RegisterRuntimeHandlers)));
            Task Pause(bool paused)
            {
                var body = new byte[15]; body[14] = paused ? (byte)1 : (byte)0;
                return pauseHandler(session, pauseHeader, body);
            }
            bool Ack(bool success, bool paused) => fixture.Packets.Count > 0
                && Type(fixture.Packets[^1]) == (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE
                && fixture.Packets[^1].AsSpan(15).SequenceEqual(GuardianAltarPauseAckBodyBuilder.Build(success, paused));

            await Pause(false);
            check("town pause returns a complete failure without creating an activity run",
                Ack(false, false) && session.Player.CurrentRun == null);
            fixture.Packets.Clear();
            await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
            var run = fixture.LastRun;
            var room = fixture.LastRoom;
            var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
            var progress = run.Instance.Mechanisms.GuardianAltar.Progress;
            await Pause(false);
            check("resume before loading release is denied without starting energy",
                Ack(false, false) && !energy.ReadSnapshot(fixture.MonotonicMilliseconds).IsReady);
            var ready = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
            check("Guardian uses the existing room barrier before energy binding", ready.Released);
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run,
                room.Identity, () => room.IsCurrentLoadingGeneration(ready.Generation));
            await Network.Handlers.TownHandler.SendFinishLoadingCompletionCoreAsync(fence,
                (_, _) => Task.FromResult(false), onReleased: () =>
                    GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, ready.Generation));
            check("failed FINISH_LOADING write does not bind Guardian energy",
                !energy.ReadSnapshot(fixture.MonotonicMilliseconds).IsReady);
            var transitionGate = session.Player.DungeonRunTransitionGate;
            await transitionGate.WaitAsync();
            var waitingPause = Pause(true);
            try
            {
                await Network.Handlers.TownHandler.SendFinishLoadingCompletionCoreAsync(fence,
                    (_, _) => Task.FromResult(true), onReleased: () =>
                        GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, ready.Generation));
            }
            finally { transitionGate.Release(); }
            await waitingPause.WaitAsync(TimeSpan.FromSeconds(5));
            check("a loading release during the run-gate wait is consumed with its current bound generation",
                Ack(true, true));
            var bound = energy.ReadSnapshot(fixture.MonotonicMilliseconds);
            check("successful release binds the exact run room and loading generation without initial energy",
                bound.IsReady && bound.Paused && bound.EnergyFixed == 0
                && bound.Identity.Equals(run.CaptureParticipantRoomIdentity())
                && bound.LoadingGeneration == ready.Generation);
            fixture.MonotonicMilliseconds = 10000;
            await Pause(false);
            var resumed = energy.ReadSnapshot(10000);
            check("first resume accepts the A21 six-byte ACK and starts an empty level-zero gauge",
                Ack(true, false) && !resumed.Paused && resumed.EnergyFixed == 0 && resumed.Level == 0);
            fixture.MonotonicMilliseconds = 11999;
            await Pause(false);
            check("duplicate resume does not reset elapsed time or the ACK version",
                Ack(true, false) && energy.ReadSnapshot(11999).EnergyFixed == 1000
                && energy.ReadSnapshot(11999).Version == resumed.Version);
            GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, ready.Generation);
            check("duplicate loading release cannot reset an active energy anchor",
                energy.ReadSnapshot(12000).EnergyFixed == 2000 && !energy.ReadSnapshot(12000).Paused);
            fixture.MonotonicMilliseconds = 12000;
            await Pause(true);
            fixture.MonotonicMilliseconds = 20000;
            check("accepted pause freezes natural energy", Ack(true, true) && energy.ReadSnapshot(20000).EnergyFixed == 2000);
            await Pause(false);
            fixture.MonotonicMilliseconds = 21000;
            check("resume continues from the stored value", Ack(true, false) && energy.ReadSnapshot(21000).EnergyFixed == 3000);
            var invalid = new byte[15]; invalid[3] = 1;
            await fixture.Coordinator.HandlePauseAsync(session, pauseHeader, invalid);
            check("malformed pause cannot change the valid runtime", Ack(false, false) && !energy.ReadSnapshot(21000).Paused);
            fixture.Opening.Close();
            await Pause(true);
            check("closing menu admission does not revoke a live run pause", Ack(true, true));
            fixture.PauseSendError = new IOException("test failed energy ACK");
            await Pause(false);
            var afterFailure = energy.ReadSnapshot(21000);
            fixture.PauseSendError = null;
            fixture.MonotonicMilliseconds = 22000;
            await Pause(false);
            check("failed ACK followed by retry does not restart energy or double-apply a transition",
                Ack(true, false) && energy.ReadSnapshot(22000).EnergyFixed == 4000
                && energy.ReadSnapshot(22000).Version == afterFailure.Version);
            var packetCount = fixture.Packets.Count;
            fixture.BeforePausePrepare = () => session.GameSession = GameSessionIdentity();
            await Pause(true);
            check("an ACK delayed across GameSession replacement is suppressed", fixture.Packets.Count == packetCount);
            check("energy requests do not mutate persisted menu progress or grant ordinary combat rewards",
                SameProgress(progress, Fresh(definition, session.Player.CharacterId))
                && run.RewardPolicy.Kind == DungeonRewardPolicyKind.GuardianAltar
                && !run.RewardPolicy.AllowsMonsterExperience && !run.RewardPolicy.AllowsSettlement
                && ReferenceEquals(run.Instance.Mechanisms.GuardianAltar.Progress, progress));
            fixture.EndMemoryRun(session, run.CaptureIdentity());
            check("ending the physical instance closes energy and invalidates old ACK snapshots",
                energy.ReadSnapshot(22000).IsClosed && !energy.IsCurrent(afterFailure));
            var closed = energy.ReadSnapshot(22000);
            fixture.Open();
            fixture.Packets.Clear();
            await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
            await Pause(false);
            check("a replacement run starts empty and cannot use an old loading release",
                Ack(false, false) && !fixture.LastRun.Instance.Mechanisms.GuardianAltarEnergy.ReadSnapshot(22000).IsReady
                && fixture.LastRun.Instance.Mechanisms.GuardianAltarEnergy.ReadSnapshot(22000).EnergyFixed == 0
                && energy.ReadSnapshot(22000) == closed);
        }

        private static async Task CheckCombatAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            var session = await fixture.CreateConnectedSessionAsync(302);
            await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
            var run = fixture.LastRun;
            var room = fixture.LastRoom;
            var identity = run.CaptureParticipantRoomIdentity();
            var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
            var combat = run.Instance.Mechanisms.GuardianAltarCombat;
            var plan = run.Instance.Mechanisms.GuardianAltar;
            check("current PVF freezes two learned unit templates and their actual item costs/cooldowns",
                plan.Units.Count == 2 && plan.Units[0] is { Slot: 0, PrototypeId: 2749171,
                    EnergyCostFixed: 5000, CooldownMilliseconds: 3500, CooldownGroup: "100" }
                && plan.Units[1] is { Slot: 1, PrototypeId: 2749176,
                    EnergyCostFixed: 8000, CooldownMilliseconds: 4500, CooldownGroup: "101" });
            var release = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
            GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, release.Generation);
            var pauseHeader = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE };
            Task Pause(bool paused)
            {
                var body = new byte[15]; body[14] = paused ? (byte)1 : (byte)0;
                return fixture.Coordinator.HandlePauseAsync(session, pauseHeader, body);
            }
            Task Upgrade(int level, int claimedEnergy = int.MaxValue)
            {
                var body = new byte[22];
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14), level);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(18), claimedEnergy);
                return fixture.Coordinator.HandleUpgradeAsync(session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_UPGRADE_GAGE }, body);
            }
            Task Summon(byte slot, int prototype, int level)
            {
                var body = new byte[23]; body[14] = slot;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(15), prototype);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(19), level);
                return fixture.Coordinator.HandleSummonAsync(session, new GamePacketHeader {
                    cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_SUMMON_UNIT }, body);
            }
            byte[] LastBody() => fixture.Packets[^1].AsSpan(15).ToArray();
            await Pause(false);
            await Upgrade(0);
            check("client energy claims cannot authorize a zero-balance upgrade",
                LastBody().SequenceEqual(GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 3, 0, 0, 0))
                && energy.ReadSnapshot(0).Level == 0);
            fixture.MonotonicMilliseconds = 5000;
            await Summon(0, 2749171, 0);
            check("registered summon dispatch spends PVF energy and returns the real full-width template",
                LastBody().SequenceEqual(GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(true, 0, 0, 2749171, 0))
                && energy.ReadSnapshot(5000).EnergyFixed == 0);
            var spent = energy.ReadSnapshot(5000);
            await Summon(0, 2749171, 0);
            await Summon(1, 2749171, 0);
            await Summon(5, 2749171, 0);
            check("duplicate cooldown, prototype mismatch and undeployed slots do not spend or project a summon",
                LastBody().SequenceEqual(new byte[] { 0 }) && energy.ReadSnapshot(5000) == spent
                && !fixture.Packets.Any(x => Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_SPAWN_UNIT));
            fixture.MonotonicMilliseconds = 15000;
            fixture.PauseSendError = new IOException("upgrade ACK transport failure");
            await Upgrade(0);
            fixture.PauseSendError = null;
            await Upgrade(0);
            check("upgrade preserves the remainder after the configured 80 percent cost",
                LastBody().SequenceEqual(GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, 0, 1, 2000)));
            await Upgrade(0);
            check("repeated stale-level upgrade cannot advance or charge the next level",
                LastBody().SequenceEqual(GuardianAltarCombatBodyBuilder.BuildUpgradeAcknowledgement(true, 0, 0, 1, 2000))
                && energy.ReadSnapshot(15000) is { Level: 1, EnergyFixed: 2000 });
            fixture.MonotonicMilliseconds = 18000;
            await Summon(0, 2749171, 1);
            check("summon after upgrade uses the new rate without repricing prior seconds",
                LastBody().SequenceEqual(GuardianAltarCombatBodyBuilder.BuildSummonAcknowledgement(true, 0, 0, 2749171, 300)));
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var ticket);
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run, room.Identity);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, ticket, fence);
            check("a spend or upgrade does not invalidate the scheduled enemy callback",
                combat.AliveCount == 1 && combat.NextDueActiveMilliseconds == 25000
                && Type(fixture.Packets[^1]) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_SPAWN_UNIT
                && LastBody().Length == 38 && U16(LastBody(), 0) == 1);
            var actors = run.Instance.Mechanisms.DynamicActors.Capture(room.Identity,
                GuardianAltarCombatRuntime.DynamicActorProvider, release.Generation);
            check("enemy actor has the isolated policy and current instance generation",
                actors.Count == 1 && ReferenceEquals(actors[0].Policy, DungeonDynamicActorPolicy.GuardianAltar)
                && !actors[0].Policy.GrantsMonsterExperience && !actors[0].Policy.GeneratesMonsterDrops
                && !actors[0].Policy.GeneratesQuestDrops && !actors[0].Policy.AdvancesQuestObjectives
                && !actors[0].Policy.CountsTowardRoomClear && !actors[0].Policy.AppliesGeneralMechanisms
                && !actors[0].Policy.TracksKillStatistics);
            var packetCount = fixture.Packets.Count;
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, ticket, fence);
            check("old timer ticket cannot spawn a duplicate enemy", fixture.Packets.Count == packetCount && combat.AliveCount == 1);
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var beforePause);
            await Pause(true);
            var pausedBalance = energy.ReadSnapshot(18000);
            fixture.MonotonicMilliseconds = 25000;
            await Summon(0, 2749171, 1);
            await Upgrade(1);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, beforePause, fence);
            check("pause rejects spends and cancels the pending wave without advancing active time",
                energy.ReadSnapshot(25000).EnergyFixed == pausedBalance.EnergyFixed
                && energy.ReadSnapshot(25000).ActiveMilliseconds == 18000 && combat.AliveCount == 1);
            await Pause(false);
            fixture.MonotonicMilliseconds = 32000;
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var retryTicket);
            fixture.PauseSendError = new IOException("wave transport failure");
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, retryTicket, fence);
            check("failed enemy projection retains one registered actor and a stable retry batch", combat.AliveCount == 2);
            fixture.PauseSendError = null;
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out retryTicket);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, retryTicket, fence);
            check("enemy retry reuses its original sequence and does not register another actor",
                combat.AliveCount == 2 && U16(LastBody(), 0) == 2 && combat.NextDueActiveMilliseconds == 40000);
            fixture.MonotonicMilliseconds = 132000; // active125s after the 7s pause
            fixture.AfterPauseWrite = () => fixture.PauseSendError = new IOException("second spawn frame failed");
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var partialTicket);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, partialTicket, fence);
            check("two-actor batch checkpoints the completed first frame before a second-frame failure",
                combat.AliveCount == 4 && U16(LastBody(), 0) == 3);
            fixture.PauseSendError = null;
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out partialTicket);
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, partialTicket, fence);
            var projectedSequences = fixture.Packets.Where(x => Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_SPAWN_UNIT)
                .Select(x => U16(x.AsSpan(15).ToArray(), 0)).ToArray();
            check("partial transport retry continues the missing frame without replaying the completed actor",
                combat.AliveCount == 4 && projectedSequences.SequenceEqual(new ushort[] { 1, 2, 3, 4 })
                && combat.NextDueActiveMilliseconds == 126000);
            var actor = actors[0];
            var deathSource = new DungeonEventEnvelope(Guid.NewGuid(), run.CaptureIdentity(), room.Identity.RoomInstanceId,
                session.Player.CharacterId, session.Player.CharacterId, actor.SequenceId, actor.ActorCode,
                "guardian-selftest-death", fixture.MonotonicMilliseconds);
            check("canonical death admission resolves the Guardian provider and its actual spawn",
                DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, actor));
            var death = room.TryRecordActorDeath(deathSource, actor.SequenceId, actor.ActorCode, actor.ActorType);
            check("the shared Room ledger commits once before the Guardian death callback",
                death.Accepted && death.Created && DungeonKillApplicationService.TryApplyGuardianActorDeath(run, actor, death.Fact.Source)
                && combat.AliveCount == 3 && !DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, actor));
            var repeat = room.TryRecordActorDeath(deathSource, actor.SequenceId, actor.ActorCode, actor.ActorType);
            check("canonical death replay preserves its fact and cannot decrement alive again",
                repeat.Accepted && !repeat.Created && ReferenceEquals(repeat.Fact, death.Fact)
                && DungeonKillApplicationService.TryApplyGuardianActorDeath(run, actor, repeat.Fact.Source)
                && combat.AliveCount == 3 && run.RoomKilledSeqIds.Count == 0);
            var wrongGeneration = new DungeonDynamicActorDefinition(actor.SequenceId, actor.ActorCode, actor.ActorType,
                actor.ActorLevel, actor.RoomIdentity, actor.Provider, actor.ProviderGeneration + 1,
                actor.WaveIdentity, actor.Policy);
            var wrongPolicy = new DungeonDynamicActorDefinition(actor.SequenceId, actor.ActorCode, actor.ActorType,
                actor.ActorLevel, actor.RoomIdentity, actor.Provider, actor.ProviderGeneration,
                actor.WaveIdentity, DungeonDynamicActorPolicy.BloodAltar);
            check("forged generation and another mechanism policy cannot pass Guardian death admission",
                !DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, wrongGeneration)
                && !DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, wrongPolicy));
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var oldSessionTicket);
            packetCount = fixture.Packets.Count;
            session.GameSession = GameSessionIdentity();
            fixture.MonotonicMilliseconds = 140000;
            await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, release.Generation, oldSessionTicket, fence);
            check("captured old GameSession cannot project any newer wave", fixture.Packets.Count == packetCount && combat.AliveCount == 3);
            fixture.EndMemoryRun(session, run.CaptureIdentity());
            check("physical ending closes both clocks and cancels the instance wave ticket",
                combat.IsClosed && energy.ReadSnapshot(140000).IsClosed && !combat.Timers.IsCurrent(oldSessionTicket));
        }

        private static async Task CheckCombatGenerationAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using var fixture = new Fixture(definition);
            fixture.Open();
            var session = await fixture.CreateConnectedSessionAsync(303);
            await fixture.DispatchAsync(session, Request(definition.FirstDungeonId));
            var run = fixture.LastRun;
            var room = fixture.LastRoom;
            var identity = run.CaptureParticipantRoomIdentity();
            var released = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
            GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, released.Generation);
            await fixture.Coordinator.HandlePauseAsync(session, new GamePacketHeader {
                cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE }, new byte[15]);
            var combat = run.Instance.Mechanisms.GuardianAltarCombat;
            combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var ticket);
            var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run, room.Identity);
            fixture.MonotonicMilliseconds = 10000;
            var packetCount = fixture.Packets.Count;
            var gate = session.Player.DungeonRunTransitionGate;
            await gate.WaitAsync();
            Task callback;
            long replacementGeneration;
            try
            {
                callback = fixture.Coordinator.OnCombatTimerAsync(session, run, identity, released.Generation, ticket, fence);
                check("old wave callback waits for the shared run gate", !callback.IsCompleted);
                replacementGeneration = room.RegisterLoadingProjection(run.CaptureIdentity(),
                    DungeonMapHandler.CreateLoadingProjectionId(), new[] { run.CaptureIdentity() }, out _);
            }
            finally { gate.Release(); }
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            check("a new loading generation in the same room invalidates the waiting old wave",
                replacementGeneration > released.Generation && fixture.Packets.Count == packetCount
                && combat.AliveCount == 0 && run.Instance.Mechanisms.DynamicActors.Capture(room.Identity,
                    GuardianAltarCombatRuntime.DynamicActorProvider, released.Generation).Count == 0);
        }

        private static async Task CheckFailedClearAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(401);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var room = fixture.LastRoom;
                var identity = run.CaptureParticipantRoomIdentity();
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var energy = run.Instance.Mechanisms.GuardianAltarEnergy;
                var eplpHeader = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.EPLP_COMMAND };
                var beforeEplp = fixture.Packets.Count;
                check("Guardian consumes manual return before its EPLP UI without acknowledgement or ordinary fallback",
                    await fixture.Coordinator.TryHandleEplpCommandAsync(session, eplpHeader, new byte[] { 1, 2 })
                        .WaitAsync(TimeSpan.FromSeconds(5))
                    && fixture.Packets.Count == beforeEplp && fixture.ReturnAttempts == 0
                    && ReferenceEquals(session.Player.CurrentRun, run));
                combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var oldWave);
                var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run, room.Identity);
                var before = fixture.Packets.Count;
                var applied = await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                var terminalPackets = fixture.Packets.Skip(before).ToArray();
                var fact = combat.TerminalFact;
                check("current failure-center zero enters dedicated DIE and EPLP without a persistent reward owner",
                    applied && fact?.Result == GuardianAltarTerminalResult.Failed
                    && terminalPackets.Length == 2
                    && Type(terminalPackets[0]) == (ushort)NotiPacketTypeA21.DIE_MONSTER
                    && Type(terminalPackets[1]) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE
                    && U16(terminalPackets[0], 15) == 0
                    && terminalPackets[1].AsSpan(15).SequenceEqual(GuardianAltarClearBodyBuilder.BuildEplpState(0, 1)));
                check("failed Guardian never creates a normal clear/card UI or a reward commit",
                    combat.Presentation.CommitResult == null && !combat.Presentation.InventoryProjectionSent
                    && !combat.Presentation.ClearProjectionSent
                    && combat.Presentation.EplpProjectionSent && energy.ReadSnapshot(0).Paused
                    && !run.RewardPolicy.AllowsClearCommit && !run.RewardPolicy.AllowsSettlement
                    && !combat.Timers.IsCurrent(oldWave));
                var returnAt = combat.Presentation.ReturnDeadlineUtc;
                before = fixture.Packets.Count;
                await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0).WaitAsync(TimeSpan.FromSeconds(5));
                check("repeated failed completion preserves event and return deadline without another EPLP",
                    ReferenceEquals(combat.TerminalFact, fact) && combat.Presentation.ReturnDeadlineUtc == returnAt
                    && fixture.Packets.Skip(before).All(x => Type(x) == (ushort)NotiPacketTypeA21.DIE_MONSTER));
                before = fixture.Packets.Count;
                fixture.MonotonicMilliseconds = run.Instance.Mechanisms.GuardianAltar.DurationMilliseconds + 1;
                await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, fixture.LoadingGeneration,
                    oldWave, fence).WaitAsync(TimeSpan.FromSeconds(5));
                check("old phase-end reminder after center failure cannot summon, commit or replace terminal authority",
                    fixture.Packets.Count == before && ReferenceEquals(combat.TerminalFact, fact)
                    && combat.Presentation.CommitResult == null && !combat.Timers.IsCurrent(oldWave));
                before = fixture.Packets.Count;
                var acknowledgedBeforeReturn = false;
                fixture.BeforeReturn = () => acknowledgedBeforeReturn = fixture.Packets.Count == before + 1
                    && fixture.Packets[^1][0] == 1 && Type(fixture.Packets[^1]) == (ushort)CmdPacketTypeA21.EPLP_COMMAND
                    && fixture.Packets[^1].AsSpan(15).SequenceEqual(new byte[] { 1, 1, 2 })
                    && ReferenceEquals(session.Player.CurrentRun, run) && run.Matches(identity.Run);
                var returned = await fixture.Coordinator.TryHandleEplpCommandAsync(session, eplpHeader, new byte[] { 1, 2 })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("valid failed-stage manual return acknowledges [1,1,2] before returning the original run identity",
                    returned && acknowledgedBeforeReturn && fixture.ReturnAttempts == 1 && fixture.ReturnApplied == 1
                    && session.Player.CurrentRun == null && run.RunState == DungeonRunState.Ended);
            }

            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(402);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var room = fixture.LastRoom;
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var replacement = room.RegisterLoadingProjection(run.CaptureIdentity(),
                    DungeonMapHandler.CreateLoadingProjectionId(), new[] { run.CaptureIdentity() }, out _);
                var before = fixture.Packets.Count;
                var consumed = await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("old loaded generation's center request is consumed without failing or projecting the newer room",
                    consumed && replacement > fixture.LoadingGeneration && fixture.Packets.Count == before
                    && combat.TerminalFact == null && !combat.Presentation.EplpProjectionSent);
            }

            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(403);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var before = fixture.Packets.Count;
                fixture.BeforePausePrepare = () => session.GameSession = GameSessionIdentity();
                await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0).WaitAsync(TimeSpan.FromSeconds(5));
                check("GameSession replacement before center DIE write fences the captured old terminal projection",
                    fixture.Packets.Count == before && combat.TerminalFact?.Result == GuardianAltarTerminalResult.Failed
                    && !combat.Presentation.EplpProjectionSent && combat.Presentation.CommitResult == null);
            }

            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(404);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                fixture.PauseSendError = new IOException("injected failure before center DIE projection");
                var error = await ObserveAsync(async () =>
                    await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0).WaitAsync(TimeSpan.FromSeconds(5)));
                check("failed first center write retains terminal authority and schedules the dedicated continuation",
                    error == null && combat.TerminalFact?.Result == GuardianAltarTerminalResult.Failed
                    && !combat.Presentation.EplpProjectionSent
                    && combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarClearPresentation, out _));
                var fact = combat.TerminalFact;
                var before = fixture.Packets.Count;
                fixture.PauseSendError = null;
                await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                check("authorized retry resumes failed EPLP without awarding items or entering ordinary settlement",
                    ReferenceEquals(combat.TerminalFact, fact) && combat.Presentation.EplpProjectionSent
                    && combat.Presentation.CommitResult == null && !combat.Presentation.ClearProjectionSent
                    && fixture.Packets.Skip(before).Any(x => Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE)
                    && fixture.Packets.Skip(before).All(x => Type(x) == (ushort)NotiPacketTypeA21.DIE_MONSTER
                        || Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE));
            }

            using (var fixture = new Fixture(definition))
            {
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(405);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var room = fixture.LastRoom;
                var identity = run.CaptureParticipantRoomIdentity();
                var combat = run.Instance.Mechanisms.GuardianAltarCombat;
                var fence = DungeonLoadingCoordinator.CaptureContinuationFence(session, fixture.Sessions, run, room.Identity);
                var callbacks = 0;
                while (!combat.IsTerminal && combat.NextDueActiveMilliseconds is { } due && callbacks++ < 32)
                {
                    fixture.MonotonicMilliseconds = due;
                    if (!combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarWaveSchedule, out var ticket))
                        throw new InvalidOperationException("Guardian end-boundary test lost its current reminder.");
                    await fixture.Coordinator.OnCombatTimerAsync(session, run, identity, fixture.LoadingGeneration,
                        ticket, fence).WaitAsync(TimeSpan.FromSeconds(5));
                    foreach (var actor in run.Instance.Mechanisms.DynamicActors.Capture(room.Identity,
                        GuardianAltarCombatRuntime.DynamicActorProvider, fixture.LoadingGeneration))
                        if (DungeonKillApplicationService.CanAcceptGuardianActorDeath(run, actor))
                        {
                            var death = room.TryRecordActorDeath(DungeonEventEnvelope.Create(run,
                                    session.Player.CharacterId, "guardian clear entry regression"),
                                actor.SequenceId, actor.ActorCode, actor.ActorType);
                            if (!death.Accepted || !DungeonKillApplicationService.TryApplyGuardianActorDeath(run, actor, death.Fact.Source))
                                throw new InvalidOperationException("Guardian end-boundary test could not apply canonical enemy death.");
                        }
                    await fixture.Coordinator.TryCompleteAsync(session, run).WaitAsync(TimeSpan.FromSeconds(5));
                }
                check("real phase-boundary coordinator reaches terminal success after the final canonical enemy death",
                    callbacks < 32 && combat.TerminalFact?.Result == GuardianAltarTerminalResult.Succeeded
                    && combat.AliveCount == 0 && combat.NextDueActiveMilliseconds == null);
                check("missing persistent owner cannot fabricate successful inventory, menu or clear projection",
                    combat.Presentation.CommitResult == null && !combat.Presentation.InventoryProjectionSent
                    && !combat.Presentation.ClearProjectionSent
                    && !combat.Presentation.EplpProjectionSent
                    && !fixture.Packets.Any(x => Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_CLEAR_STAGE
                        || Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_EPLP_STATE)
                    && combat.Timers.TryGetCurrentTicket(DungeonRunTimerKeys.GuardianAltarClearPresentation, out _));
            }

            foreach (var change in new[] { "GameSession and replacement run after ACK", "loading generation before ACK" })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(change.StartsWith("GameSession", StringComparison.Ordinal) ? 406 : 407);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var room = fixture.LastRoom;
                await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0).WaitAsync(TimeSpan.FromSeconds(5));
                var before = fixture.Packets.Count;
                DungeonRun replacement = null;
                long replacementGeneration = 0;
                if (change.StartsWith("GameSession", StringComparison.Ordinal))
                    fixture.AfterPauseWrite = () =>
                    {
                        session.GameSession = GameSessionIdentity();
                        fixture.EndMemoryRun(session, run.CaptureIdentity());
                        var instance = new DungeonInstance((short)definition.FirstDungeonId, 0, DungeonRewardPolicy.GuardianAltar);
                        if (!DungeonRunLifecycle.BeginRunFromAuthorizedTown(session, instance, Anchor(session),
                                session.Player.CurrentDungeonRunGeneration, () => true, out replacement))
                            throw new InvalidOperationException("Guardian EPLP replacement run could not begin.");
                    };
                else
                    fixture.BeforePausePrepare = () => replacementGeneration = room.RegisterLoadingProjection(
                        run.CaptureIdentity(), DungeonMapHandler.CreateLoadingProjectionId(), new[] { run.CaptureIdentity() }, out _);
                var consumed = await fixture.Coordinator.TryHandleEplpCommandAsync(session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.EPLP_COMMAND }, new byte[] { 1, 2 })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check(change + " invalidates the captured manual return and cannot terminate a newer owner",
                    consumed && fixture.ReturnAttempts == 0 && fixture.ReturnApplied == 0
                    && (replacement != null
                        ? ReferenceEquals(session.Player.CurrentRun, replacement) && replacement.RunState == DungeonRunState.Selecting
                            && fixture.Packets.Count == before + 1 && fixture.Packets[^1].AsSpan(15).SequenceEqual(new byte[] { 1, 1, 2 })
                        : replacementGeneration > fixture.LoadingGeneration && ReferenceEquals(session.Player.CurrentRun, run)
                            && fixture.Packets.Count == before));
            }
        }

        private static async Task ReadyGuardianAsync(Fixture fixture, EnhancedClientSession session,
            GuardianAltarMenuDefinition definition)
        {
            await fixture.DispatchAsync(session, Request(definition.FirstDungeonId)).WaitAsync(TimeSpan.FromSeconds(5));
            var run = fixture.LastRun;
            var room = fixture.LastRoom;
            var release = room.MarkLoadingReady(run.CaptureIdentity(), new[] { run.CaptureIdentity() });
            GuardianAltarDungeonCoordinator.OnLoadingReleased(run, room.Identity, release.Generation);
            await fixture.Coordinator.HandlePauseAsync(session, new GamePacketHeader
                { cmd = 1, type = (ushort)CmdPacketTypeA21.ADVANCE_ALTAR_PAUSE }, new byte[15])
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static async Task CheckEplpSelectionAsync(Action<string, bool> check, GuardianAltarMenuDefinition definition)
        {
            foreach (var option in new byte[] { 1, 2 })
            {
                using var fixture = new Fixture(definition);
                fixture.Open();
                var session = await fixture.CreateConnectedSessionAsync(410 + option);
                await ReadyGuardianAsync(fixture, session, definition);
                var run = fixture.LastRun;
                var header = new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.EPLP_COMMAND };
                await fixture.Coordinator.TryHandleCenterDeathAsync(session, run, 0).WaitAsync(TimeSpan.FromSeconds(5));
                var fact = run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact;
                var before = fixture.Packets.Count;
                foreach (var selection in new byte[] { 2, 1, 2 })
                {
                    var consumed = await fixture.Coordinator.TryHandleEplpCommandAsync(session, header,
                        new byte[] { 2, selection }).WaitAsync(TimeSpan.FromSeconds(5));
                    check("Guardian button hover receives its matching ACK without leaving the run",
                        consumed && fixture.Packets[^1][0] == 1
                        && Type(fixture.Packets[^1]) == (ushort)CmdPacketTypeA21.EPLP_COMMAND
                        && fixture.Packets[^1].AsSpan(15).SequenceEqual(new byte[] { 1, 2, selection })
                        && fixture.ReturnAttempts == 0 && ReferenceEquals(session.Player.CurrentRun, run)
                        && ReferenceEquals(run.Instance.Mechanisms.GuardianAltarCombat.TerminalFact, fact));
                }
                check("hover selection cannot create a reward or ordinary-card projection",
                    fixture.Packets.Count == before + 3
                    && run.Instance.Mechanisms.GuardianAltarCombat.Presentation.CommitResult == null
                    && fixture.Progress.Reads == 1);
                before = fixture.Packets.Count;
                foreach (var invalid in new[]
                {
                    Array.Empty<byte>(), new byte[] { 1 }, new byte[] { 1, 1, 0 },
                    new byte[] { 0, 1 }, new byte[] { 3, 2 }, new byte[] { 1, 0 }, new byte[] { 2, 3 },
                })
                    await fixture.Coordinator.TryHandleEplpCommandAsync(session, header, invalid)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                check("unproven result events, retry option and lengths cannot acknowledge or end Guardian",
                    fixture.Packets.Count == before && fixture.ReturnAttempts == 0
                    && ReferenceEquals(session.Player.CurrentRun, run));
                var acknowledged = false;
                fixture.BeforeReturn = () => acknowledged = fixture.Packets.Count == before + 1
                    && fixture.Packets[^1].AsSpan(15).SequenceEqual(new byte[] { 1, 1, option })
                    && ReferenceEquals(session.Player.CurrentRun, run);
                await fixture.Coordinator.TryHandleEplpCommandAsync(session, header, new byte[] { 1, option })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                check("committed selection " + option + " acknowledges before ending only the captured Guardian run",
                    acknowledged && fixture.ReturnAttempts == 1 && fixture.ReturnApplied == 1
                    && session.Player.CurrentRun == null && run.RunState == DungeonRunState.Ended);
                var count = fixture.Packets.Count;
                check("replayed result command after exit is no longer handled by the ended Guardian owner",
                    !await fixture.Coordinator.TryHandleEplpCommandAsync(session, header, new byte[] { 1, option })
                        .WaitAsync(TimeSpan.FromSeconds(5))
                    && fixture.Packets.Count == count && fixture.ReturnAttempts == 1);
            }
        }

        private static GuardianAltarProgressSnapshot Fresh(GuardianAltarMenuDefinition definition, int characterId) =>
            new GuardianAltarProgressSnapshot(characterId,
                new GuardianAltarHeader(definition.RidableId, definition.DailyRefillTicket, 0, 0, 0, 0, 0, 0, 0, 0),
                new[] { new GuardianAltarStage(definition.FirstDungeonId, -1) },
                definition.InitialPurchases, definition.InitialSlots, Array.Empty<GuardianAltarAchievement>());

        private static GuardianAltarProgressSnapshot Copy(GuardianAltarProgressSnapshot source,
            GuardianAltarHeader header = null, IEnumerable<GuardianAltarStage> stages = null) =>
            new GuardianAltarProgressSnapshot(source.CharacterId, header ?? source.Header, stages ?? source.Stages,
                source.Purchases, source.Slots, source.Achievements);

        private static bool SameProgress(GuardianAltarProgressSnapshot a, GuardianAltarProgressSnapshot b) =>
            a.CharacterId == b.CharacterId && a.Header == b.Header && a.Stages.SequenceEqual(b.Stages)
            && a.Purchases.SequenceEqual(b.Purchases) && a.Slots.SequenceEqual(b.Slots)
            && a.Achievements.SequenceEqual(b.Achievements);

        private static byte[] Request(int dungeonId, byte difficulty = 0)
        {
            var body = new byte[15];
            BinaryPrimitives.WriteInt32LittleEndian(body, dungeonId);
            body[4] = difficulty; body[5] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), ushort.MaxValue);
            return body;
        }
        private static int I32(byte[] body, int offset) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
        private static uint U32(byte[] body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
        private static ushort U16(byte[] body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
        private static ushort Type(byte[] packet) => U16(packet, 1);
        private static bool HasStartMap(IEnumerable<byte[]> packets) => packets.Any(x => Type(x) == (ushort)NotiPacketTypeA21.ADVANCE_ALTAR_START_MAP);
        private static bool IsRejection(IReadOnlyList<byte[]> packets) => packets.Count == 2
            && Type(packets[0]) == (ushort)CmdPacketTypeA21.SELECT_DUNGEON && packets[0][0] == 1
            && packets[0].AsSpan(15).SequenceEqual(DungeonAdmissionRejectBuilder.Build(DungeonAdmissionReject.DungeonUnavailable))
            && Type(packets[1]) == (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE && packets[1][0] == 0;
        private static DungeonTownReturnAnchor Anchor(EnhancedClientSession session) => new DungeonTownReturnAnchor(
            session.Player.CurTownId, session.Player.CurAreaId, session.Player.CurPosX, session.Player.CurPosY,
            session.Player.CurDirection, session.Player.CurAreaState);
        private static GameSession GameSessionIdentity() => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));
        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return true; }
            return false;
        }
        private static async Task<Exception> ObserveAsync(Func<Task> action)
        {
            try { await action(); return null; }
            catch (Exception exception) { return exception; }
        }

        private sealed class ProgressStub : IGuardianAltarProgressService
        {
            private readonly GuardianAltarMenuDefinition _definition;
            internal ProgressStub(GuardianAltarMenuDefinition definition) => _definition = definition;
            internal int Reads;
            internal bool Preparing;
            internal bool ReadInPrepare;
            internal bool WrongCharacter;
            internal Action DuringLoad;
            internal Exception Error;
            internal Func<GuardianAltarProgressSnapshot, GuardianAltarProgressSnapshot> Transform;
            public GuardianAltarProgressSnapshot LoadMenu(int characterId, DateTime utcNow, Func<bool> isCurrent = null)
            {
                Reads++;
                ReadInPrepare |= Preparing;
                DuringLoad?.Invoke();
                if (Error != null) throw Error;
                if (isCurrent != null && !isCurrent())
                    throw new InvalidOperationException("test progress source changed");
                var result = Fresh(_definition, characterId + (WrongCharacter ? 1 : 0));
                return Transform == null ? result : Transform(result);
            }
        }

        private sealed class MercenaryStub : IMercenaryRestrictionService
        {
            internal bool Assigned;
            public bool IsAssigned(int characterId) => Assigned;
            public bool CanDelete(int characterId) => !Assigned;
            public bool CanMutateAppearance(int characterId) => !Assigned;
            public bool CanEnterContent(int characterId) => !Assigned;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<EnhancedClientSession> _sessions = new();
            private readonly List<TcpClient> _peers = new();
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            internal readonly GuardianAltarOpeningService Opening = new();
            internal readonly SessionDirectory Sessions = new();
            internal readonly CharacterTransitionCoordinator Transitions;
            internal readonly DungeonInstanceRegistry Instances;
            internal readonly GuardianAltarDungeonCoordinator Coordinator;
            internal readonly ProgressStub Progress;
            internal readonly MercenaryStub Mercenary = new();
            internal readonly List<byte[]> Packets = new();
            internal DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
            internal bool Trading;
            internal bool InParty;
            internal Action BeforePrepare;
            internal Action BeforeReturn;
            internal Action AfterFirstWrite;
            internal Exception SendError;
            internal Exception PauseSendError;
            internal Action BeforePausePrepare;
            internal Action AfterPauseWrite;
            internal long MonotonicMilliseconds;
            internal int FailAfterPackets = -1;
            internal int ReturnAttempts;
            internal int ReturnApplied;
            internal int LoadingRegistrations;
            internal long LoadingGeneration;
            internal DungeonRun LastRun;
            internal DungeonInstanceRoom LastRoom;
            internal DungeonInstanceIdentity LastInstanceIdentity => LastRun?.Instance.Identity ?? default;

            internal Fixture(GuardianAltarMenuDefinition definition)
            {
                Progress = new ProgressStub(definition);
                Transitions = new CharacterTransitionCoordinator(Sessions);
                Instances = new DungeonInstanceRegistry(utcNow: () => Now.UtcDateTime);
                Coordinator = new GuardianAltarDungeonCoordinator(Opening, Progress, () => definition,
                    Sessions, Transitions, Instances,
                    _ => Trading, Mercenary, _ => InParty, RegisterLoading, ReturnAsync,
                    utcNow: () => Now, send: SendAsync, isGuardianDungeon: definition.ContainsDungeon,
                    monotonicMilliseconds: () => MonotonicMilliseconds, sendPause: SendPauseAsync,
                    combatClock: new DfoServer.Infrastructure.ClockService());
            }
            internal void Open() => Opening.SetWindow(Now, Now.AddMinutes(5));
            internal EnhancedClientSession CreateSession(int characterId, TcpClient tcp = null)
            {
                var session = new EnhancedClientSession(tcp ?? new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = 11 };
                session.Player.CharacterId = characterId;
                session.Player.UserId = 12001;
                session.Player.Level = 50;
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                session.Player.CurTownId = 1;
                session.Player.CurAreaId = 2;
                session.Player.CurPosX = 100;
                session.Player.CurPosY = 200;
                session.GameSession = GameSessionIdentity();
                _sessions.Add(session);
                Sessions.Register(characterId, session);
                return session;
            }
            internal async Task<EnhancedClientSession> CreateConnectedSessionAsync(int characterId)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var peer = new TcpClient();
                _peers.Add(peer);
                var accept = listener.AcceptTcpClientAsync();
                await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint).WaitAsync(TimeSpan.FromSeconds(5));
                return CreateSession(characterId, await accept.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            private Task SendPauseAsync(EnhancedClientSession session,
                Func<IReadOnlyList<byte[]>> prepare, Func<bool> current)
                => EnhancedClientSession.SendPreparedPacketBatchCoreAsync(_sendLock, () =>
                {
                    var before = BeforePausePrepare; BeforePausePrepare = null; before?.Invoke();
                    return prepare();
                }, packet =>
                {
                    if (PauseSendError != null) throw PauseSendError;
                    Packets.Add(packet.ToArray());
                    var after = AfterPauseWrite; AfterPauseWrite = null; after?.Invoke();
                    return Task.CompletedTask;
                }, current);
            internal Task<bool> DispatchAsync(EnhancedClientSession session, byte[] body, byte cmd = 1, ushort? type = null) =>
                Coordinator.TryHandleSelectDungeonAsync(session, new GamePacketHeader {
                    cmd = cmd, type = type ?? (ushort)CmdPacketTypeA21.SELECT_DUNGEON,
                    length = checked((uint)(14 + (body?.Length ?? 0))) }, body);

            private void RegisterLoading(DungeonRun run, DungeonInstanceRoom room, long projectionId)
            {
                LastRun = run; LastRoom = room;
                LoadingRegistrations++;
                LoadingGeneration = room.RegisterLoadingProjection(run.CaptureIdentity(), projectionId,
                    new[] { run.CaptureIdentity() }, out _);
                if (LoadingGeneration <= 0)
                    throw new InvalidOperationException("test dedicated barrier could not register");
            }
            private Task SendAsync(EnhancedClientSession session, Func<IReadOnlyList<byte[]>> prepare)
            {
                // Exercise the real shared batch core with the same immutable
                // source fence as the production sender; no socket or DB I/O.
                var cid = session.Player.CharacterId;
                var uid = session.Player.UserId;
                var aid = session.Account.AccountId;
                var gameSession = session.GameSession;
                var runIdentity = session.Player.CurrentRun?.CaptureIdentity();
                var isGuardianRun = session.Player.CurrentRun?.Instance.Mechanisms.GuardianAltar != null;
                var revision = Opening.Revision;
                var day = Game.DailyReset.DailyResetService.TodayId(Now.UtcDateTime);
                var written = 0;
                return EnhancedClientSession.SendPreparedPacketBatchCoreAsync(_sendLock, () =>
                {
                    var before = BeforePrepare; BeforePrepare = null; before?.Invoke();
                    Progress.Preparing = true;
                    try { return prepare(); }
                    finally { Progress.Preparing = false; }
                }, async packet =>
                {
                    if (FailAfterPackets >= 0 && written == FailAfterPackets)
                        throw SendError ?? new IOException("test partial write");
                    Packets.Add(packet.ToArray()); written++;
                    if (written == 1)
                    {
                        await Task.Yield();
                        var after = AfterFirstWrite; AfterFirstWrite = null; after?.Invoke();
                    }
                }, () => session.Player.CharacterId == cid && session.Player.UserId == uid
                    && session.Account.AccountId == aid && ReferenceEquals(session.GameSession, gameSession)
                    && Sessions.TryGet(cid, out var current) && ReferenceEquals(current, session)
                    && Opening.Revision == revision && (!isGuardianRun || Opening.GetSnapshot(Now).IsOpen)
                    && Game.DailyReset.DailyResetService.TodayId(Now.UtcDateTime) == day
                    && (!runIdentity.HasValue || session.Player.IsCurrentDungeonRun(runIdentity.Value)));
            }
            private Task ReturnAsync(EnhancedClientSession session, DungeonRunIdentity identity)
            {
                ReturnAttempts++;
                var before = BeforeReturn; BeforeReturn = null; before?.Invoke();
                if (EndMemoryRun(session, identity)) ReturnApplied++;
                return Task.CompletedTask;
            }
            // A bounded no-I/O test implementation of the injected expected-run
            // return contract. This does not replace production Town/EndRun
            // protocol, database persistence or pet recovery regression tests.
            internal bool EndMemoryRun(EnhancedClientSession session, DungeonRunIdentity identity)
            {
                DungeonRun run;
                lock (session.Player.DungeonRunLifecycleSyncRoot)
                {
                    run = session.Player.CurrentRun;
                    if (run == null || !run.Matches(identity)) return false;
                    run.TryBeginEnding();
                    session.Player.CurrentRun = null;
                    session.Player.UserState = 0;
                    session.Player.TownPresenceReady = true;
                }
                Instances.Terminate(session.Player.CharacterId, identity, "Guardian entry selftest return");
                DungeonRunLifecycle.CancelAllTimers(run);
                run.Instance.TryBeginEnding(); run.Instance.TryMarkEnded(); run.TryMarkEnded();
                return true;
            }
            public void Dispose()
            {
                foreach (var session in _sessions)
                {
                    var run = session.Player.CurrentRun;
                    if (run != null) EndMemoryRun(session, run.CaptureIdentity());
                }
                Instances.Dispose();
                _sendLock.Dispose();
                foreach (var session in _sessions) session.Close();
                foreach (var peer in _peers) peer.Dispose();
            }
        }
    }
}
