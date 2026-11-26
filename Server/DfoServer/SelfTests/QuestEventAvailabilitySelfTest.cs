using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Events.EpicRoad;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class QuestEventAvailabilitySelfTest
    {
        private const int AccountId = 992801;
        private const int CharacterId = 992811;
        private const int EpicEventId = 286;
        private const int OtherEventId = 93;
        private const int OrphanEventId = 65000;
        private static readonly ushort[] IntroductionQuests = { 8597, 8598, 8599 };

        internal static int Run()
        {
            Console.WriteLine("=== QUEST_EVENT_AVAILABILITY selftest ===");
            var assertions = 0;
            var failures = 0;
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

            Section("typed event visibility and generic policy", () => VerifyPolicy(Check));
            Section("current introduction resources and task progression", () => VerifyCurrentQuests(Check));
            Section("directory authority and shared initialization snapshot", () => VerifyDirectorySnapshot(Check));
            Section("owned quest acceptance and completion transactions", () => VerifyQuestLifecycle(Check));
            Section("closed activity permits giveup and rejects stale inventory owners", () => VerifyGiveupAndOwner(Check));
            Console.WriteLine($"QUEST_EVENT_AVAILABILITY assertions={assertions} failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPolicy(Action<string, bool> check)
        {
            var ordinary = QuestFile.Parse("[name]\n`ordinary`\n[grade]\n`[normal]`\n");
            check("ordinary tasks do not require an activity directory",
                ordinary.IsEventVisibilityValid && ordinary.ShowQuestOnEventId == -1
                && QuestEventAvailabilityPolicy.IsAvailable(ordinary, null));
            check("a missing resource cannot be advertised as a task",
                !QuestEventAvailabilityPolicy.IsAvailable(null, _ => true));
            var unbound = QuestFile.Parse("[event]\n1\n");
            check("event flag one without a resource event ID remains closed",
                unbound.IsEvent && unbound.ShowQuestOnEventId == -1
                && !QuestEventAvailabilityPolicy.IsAvailable(unbound, _ => true));
            var generic = QuestFile.Parse("[event]\n1\n[show quest on event]\n93\n");
            check("the typed visibility ID is distinct from the event flag",
                generic.IsEventVisibilityValid && generic.ShowQuestOnEventId == OtherEventId);
            check("generic non-EpicRoad tasks require their own advertised event",
                !QuestEventAvailabilityPolicy.IsAvailable(generic, null)
                && !QuestEventAvailabilityPolicy.IsAvailable(generic, id => id == EpicEventId)
                && QuestEventAvailabilityPolicy.IsAvailable(generic, id => id == OtherEventId));
            var boundWithoutFlag = QuestFile.Parse("[show quest on event]\n93\n");
            check("a show-event resource remains gated when the event flag is omitted",
                !boundWithoutFlag.IsEvent
                && !QuestEventAvailabilityPolicy.IsAvailable(boundWithoutFlag, _ => false)
                && QuestEventAvailabilityPolicy.IsAvailable(boundWithoutFlag, id => id == OtherEventId));

            foreach (var malformed in new (string Label, string Value)[]
            {
                ("empty", ""), ("zero", "0"), ("negative", "-1"),
                ("overflow", "65536"), ("non-numeric", "bad"),
                ("quoted", "`286`"), ("multiple on one line", "286 287"),
                ("multiple across lines", "286\n287"),
                ("trailing invalid line", "286\nbad"),
                ("quoted second line", "286\n`287`"),
            })
            {
                var quest = QuestFile.Parse("[event]\n1\n[show quest on event]\n" + malformed.Value + "\n");
                check("malformed " + malformed.Label + " visibility cannot authorize an event task",
                    !quest.IsEventVisibilityValid && !QuestEventAvailabilityPolicy.IsAvailable(quest, _ => true));
            }
            var duplicate = QuestFile.Parse("[event]\n1\n[show quest on event]\n286\n[show quest on event]\n286\n");
            check("even equal duplicate visibility tags are rejected",
                !duplicate.IsEventVisibilityValid && !QuestEventAvailabilityPolicy.IsAvailable(duplicate, _ => true));
            var nested = QuestFile.Parse("[event]\n1\n[show quest on event]\n286\n[extra]\n1\n[/extra]\n[/show quest on event]\n");
            check("nested visibility data cannot authorize a scalar event ID",
                !nested.IsEventVisibilityValid && !QuestEventAvailabilityPolicy.IsAvailable(nested, _ => true));
        }

        private static void VerifyCurrentQuests(Action<string, bool> check)
        {
            var path = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Set PVF_ARCHIVE_PATH to the current A21 archive before this selftest.", path);
            for (var index = 0; index < IntroductionQuests.Length; index++)
            {
                var questId = IntroductionQuests[index];
                var quest = QuestCatalog.Get(questId);
                check("current quest " + questId + " is a visible special meet-NPC event286 task",
                    quest != null && quest.IsEvent && quest.IsEventVisibilityValid
                    && quest.ShowQuestOnEventId == EpicEventId && quest.Grade == "[special]"
                    && quest.Type == "[meet npc]" && quest.Level.SequenceEqual(new[] { 17, 99 })
                    && quest.NpcIndex == 1125 && quest.IntData.Trim() == (index == 2 ? "1126" : "1125"));
                check("current quest " + questId + " retains its resource prerequisite",
                    index == 0 ? string.IsNullOrWhiteSpace(quest?.PreRequiredQuest)
                        : quest?.PreRequiredQuest.Trim() == IntroductionQuests[index - 1].ToString());
            }
            var definition = EpicRoadEntryDefinition.LoadCurrentPvf();
            check("the event town requires the second introduction, leaving the third for the destination NPC",
                definition.IsValid && definition.NeedQuestId == 8598
                && definition.TargetPermission.NeedQuest == 8598);
            var flags = new Dictionary<int, int>();
            var advertised = new HashSet<int> { EpicEventId };
            for (var stage = 0; stage < IntroductionQuests.Length; stage++)
            {
                var acceptable = Acceptable(17, flags, advertised);
                var packet = ReadQuestIds(QuestListBodyBuilder.BuildBody(17, 0, 0, flags, null, advertised));
                var expected = IntroductionQuests[stage];
                check("stage " + stage + " exposes only introduction " + expected,
                    IntroductionQuests.Where(acceptable.Contains).SequenceEqual(new[] { expected })
                    && IntroductionQuests.Where(packet.Contains).SequenceEqual(new[] { expected }));
                check("stage " + stage + " directory closure removes the introduction chain",
                    !IntroductionQuests.Any(Acceptable(17, flags, null).Contains)
                    && !IntroductionQuests.Any(Acceptable(17, flags, new HashSet<int> { OtherEventId }).Contains));
                flags[expected] = 1;
            }
            check("completed introductions do not reappear when the event remains open",
                !IntroductionQuests.Any(Acceptable(17, flags, advertised).Contains));
            check("the event switch does not bypass the resource minimum level",
                !IntroductionQuests.Any(Acceptable(16, new Dictionary<int, int>(), advertised).Contains));
            var closed = Acceptable(17, new Dictionary<int, int>(), null);
            var open = Acceptable(17, new Dictionary<int, int>(), advertised);
            bool Ordinary(ushort id)
            {
                var quest = QuestCatalog.Get(id);
                return quest != null && !quest.IsEvent && quest.ShowQuestOnEventId < 0;
            }
            check("opening an event leaves the ordinary task set unchanged",
                new HashSet<ushort>(closed.Where(Ordinary)).SetEquals(open.Where(Ordinary)));
            check("unbound special quests are not globally exposed by enabling event286",
                !open.Any(id => QuestCatalog.Get(id).Grade == "[special]"
                    && QuestCatalog.Get(id).ShowQuestOnEventId <= 0));
        }

        private static List<ushort> Acceptable(int level, Dictionary<int, int> flags, ISet<int> events)
            => QuestRelationIndex.ComputeAcceptableQuests(level, 0, 0,
                new HashSet<int>(flags.Where(entry => entry.Value != 0).Select(entry => entry.Key)),
                flags, null, events);

        private static void VerifyDirectorySnapshot(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var events = new GameEventRepository(fixture.Database);
            events.EnsureCatalogEntry(EpicEventId, true, "epic", "", 0);
            events.EnsureCatalogEntry(OtherEventId, true, "other", "", 1);
            fixture.Sql("INSERT INTO game_event_state(event_id,state) VALUES(@event,1);", OrphanEventId);
            var advertised = events.LoadAdvertisedEventIds();
            var directory = events.LoadEventInfoSnapshot();
            check("advertised IDs match the enabled directory entries",
                advertised.SetEquals(directory.Events.Select(entry => (int)entry.EventId))
                && advertised.Contains(EpicEventId) && advertised.Contains(OtherEventId));
            check("an enabled state row without directory metadata is not advertised",
                events.IsEnabled(OrphanEventId) && !advertised.Contains(OrphanEventId));
            using (var connection = fixture.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                check("transactional acceptance authority uses the same advertised directory",
                    GameEventRepository.LoadAdvertisedEventIds(connection, transaction).SetEquals(advertised)
                    && GameEventRepository.IsAdvertised(connection, transaction, EpicEventId)
                    && !GameEventRepository.IsAdvertised(connection, transaction, OrphanEventId));
            }

            var eventBuilder = new EventInfoBodyBuilder(fixture.Database);
            var questBuilder = new QuestListBodyBuilder(fixture.Database);
            var eventFirst = Snapshot();
            check("event-first initialization builds the directory", eventBuilder.TryBuild(eventFirst, 0, out var eventBody));
            var frozenOpen = eventFirst.InitializationSnapshot.EventInfoSnapshot;
            events.EnsureCatalogEntry(EpicEventId, false, "", "", 0);
            check("quest initialization succeeds after the operator changes the live switch",
                questBuilder.TryBuild(eventFirst, 0, out var questBody));
            check("event-first packets share one frozen open directory and task list",
                ReferenceEquals(frozenOpen, eventFirst.InitializationSnapshot.EventInfoSnapshot)
                && ReadEventIds(eventBody).Contains(EpicEventId) && ReadQuestIds(questBody).Contains(8597)
                && eventBody.SequenceEqual(EventInfoBodyBuilder.Build(frozenOpen)));

            var questFirst = Snapshot();
            check("quest-first initialization builds the task list", questBuilder.TryBuild(questFirst, 0, out var closedQuestBody));
            var frozenClosed = questFirst.InitializationSnapshot.EventInfoSnapshot;
            events.EnsureCatalogEntry(EpicEventId, true, "", "", 0);
            check("directory initialization succeeds after reopening the live switch",
                eventBuilder.TryBuild(questFirst, 0, out var closedEventBody));
            check("quest-first packets share one frozen closed directory and task list",
                ReferenceEquals(frozenClosed, questFirst.InitializationSnapshot.EventInfoSnapshot)
                && !ReadEventIds(closedEventBody).Contains(EpicEventId)
                && !ReadQuestIds(closedQuestBody).Contains(8597)
                && closedEventBody.SequenceEqual(EventInfoBodyBuilder.Build(frozenClosed)));
            var nextSelection = Snapshot();
            check("a new selection reads the reopened catalog instead of an earlier global snapshot",
                eventBuilder.TryBuild(nextSelection, 0, out var nextEvent)
                && questBuilder.TryBuild(nextSelection, 0, out var nextQuests)
                && ReadEventIds(nextEvent).Contains(EpicEventId) && ReadQuestIds(nextQuests).Contains(8597)
                && !ReferenceEquals(frozenClosed, nextSelection.InitializationSnapshot.EventInfoSnapshot));
            check("the synthetic raid display entry does not create a persisted task authorization",
                ReadEventIds(nextEvent).Contains(EventInfoBodyBuilder.RaidChannelEventId)
                && !nextSelection.InitializationSnapshot.EventInfoSnapshot.Events
                    .Any(entry => entry.EventId == EventInfoBodyBuilder.RaidChannelEventId)
                && !events.LoadAdvertisedEventIds().Contains(EventInfoBodyBuilder.RaidChannelEventId));
        }

        private static SelectCharacterDataSnapshot Snapshot() => new SelectCharacterDataSnapshot
        {
            CharacterRecord = new CharacterRecord { Level = 17, Job = 0, GrowType = 0 },
        };

        private static void VerifyQuestLifecycle(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var handle = fixture.CreateOwner(CharacterId);
            var events = new GameEventRepository(fixture.Database);
            var quests = new QuestService(fixture.Database.ConnectionString);
            var repository = new QuestRepository(fixture.Database.ConnectionString);
            events.EnsureCatalogEntry(EpicEventId, false, "epic", "", 0);
            var initialGold = handle.Inventory.GetMainVirtualCount(0).Count;
            check("closed event rejects a forged real ACCEPT without quest or reward writes",
                !quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8597)).Success
                && repository.LoadActiveQuests(CharacterId).Count == 0
                && repository.ReadClearedFlagValue(CharacterId, 8597) == 0
                && handle.Inventory.GetMainVirtualCount(0).Count == initialGold);
            events.EnsureCatalogEntry(EpicEventId, true, "", "", 0);
            fixture.Sql("DELETE FROM game_event_info_details WHERE event_id=@event;", EpicEventId);
            check("state enabled without an advertised detail cannot accept the real event quest",
                events.IsEnabled(EpicEventId)
                && !quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8597)).Success
                && repository.LoadActiveQuests(CharacterId).Count == 0
                && handle.Inventory.GetMainVirtualCount(0).Count == initialGold);
            events.EnsureCatalogEntry(EpicEventId, true, "epic", "", 0);
            check("an advertised event cannot bypass the second task prerequisite",
                !quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8598)).Success
                && repository.LoadActiveQuests(CharacterId).Count == 0);
            check("real event8597 ACCEPT creates its resource initial trigger under the owned lease",
                quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8597)).Success
                && repository.LoadActiveQuests(CharacterId).Single().QuestId == 8597
                && repository.LoadActiveQuests(CharacterId).Single().TriggerValue == QuestData.GetInitTrigger(8597));
            events.EnsureCatalogEntry(EpicEventId, false, "", "", 0);
            Complete(quests, repository, handle, 8597, check);
            check("closing the activity permits existing completion but blocks the next new ACCEPT",
                repository.ReadClearedFlagValue(CharacterId, 8597) == 1
                && !quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8598)).Success);
            events.EnsureCatalogEntry(EpicEventId, true, "", "", 0);
            check("completed8597 makes the real second introduction acceptable",
                ReadQuestIds(QuestListBodyBuilder.BuildBody(17, 0, 0,
                    repository.LoadClearedFlags(CharacterId), null, events.LoadAdvertisedEventIds())).Contains(8598)
                && quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8598)).Success);
            Complete(quests, repository, handle, 8598, check);

            var entry = new EpicRoadEntryService(fixture.Database);
            entry.Initialize("1");
            var handler = new TownHandler(new SqliteCharacterRepository(fixture.Database),
                selectDataSource: null, partyManager: null, sessions: fixture.Sessions,
                refresh: null, dungeonInstances: null, raidManager: null, database: fixture.Database);
            handler.ConfigureEpicRoadEntry(entry);
            var source = FindSourceTownArea(entry.Definition.EntranceHiddenTownIds);
            handle.Session.Player.CurTownId = source.Town;
            handle.Session.Player.CurAreaId = source.Area;
            handle.Session.Player.TownPresenceReady = true;
            handle.Session.Player.UserState = 0;
            check("actual second-task completion unlocks the real town handler before completing8599",
                repository.ReadClearedFlagValue(CharacterId, 8599) == 0
                && handler.TryValidateEpicRoadTownEntry(handle.Session, 0, out _));
            check("the final introduction is available after actual second-task completion",
                ReadQuestIds(QuestListBodyBuilder.BuildBody(17, 0, 0,
                    repository.LoadClearedFlags(CharacterId), null, events.LoadAdvertisedEventIds())).Contains(8599)
                && quests.HandleAcceptQuest(handle.Owner, new QuestAcceptCommand(8599)).Success);
            Complete(quests, repository, handle, 8599, check);
            check("the introduction chain leaves three durable completion flags and no active tasks",
                IntroductionQuests.All(id => repository.ReadClearedFlagValue(CharacterId, id) == 1)
                && repository.LoadActiveQuests(CharacterId).Count == 0);
        }

        private static void Complete(QuestService service, QuestRepository repository,
            OwnerHandle handle, ushort questId, Action<string, bool> check)
        {
            var active = repository.LoadActiveQuests(handle.Owner.CharacterId).Single(entry => entry.QuestId == questId);
            if (active.TriggerValue > 1)
                throw new InvalidDataException("Current meet-NPC introduction has an unexpected initial trigger.");
            if (active.TriggerValue != 0)
            {
                var triggered = service.HandleSetTrigger(handle.Owner,
                    new[] { (byte)(questId & 0xFF), (byte)(questId >> 8), (byte)0 });
                check("quest " + questId + " uses the actual authorized meet-NPC trigger",
                    triggered.Success && triggered.TriggerValue == 0);
            }
            var gold = handle.Inventory.GetMainVirtualCount(0).Count;
            var command = new QuestFinishCommand(questId, false, ushort.MaxValue, 1);
            var finished = service.HandleFinishQuest(handle.Owner, command);
            check("quest " + questId + " completes once and grants its PVF gold reward",
                finished.Success && finished.FinishType == QuestFinishType.MeetNpc
                && repository.ReadClearedFlagValue(handle.Owner.CharacterId, questId) == 1
                && handle.Inventory.GetMainVirtualCount(0).Count == gold + 100);
            var completedGold = handle.Inventory.GetMainVirtualCount(0).Count;
            check("repeated FINISH " + questId + " cannot duplicate gold or completion",
                !service.HandleFinishQuest(handle.Owner, command).Success
                && handle.Inventory.GetMainVirtualCount(0).Count == completedGold
                && repository.ReadClearedFlagValue(handle.Owner.CharacterId, questId) == 1);
        }

        private static void VerifyGiveupAndOwner(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var original = fixture.CreateOwner(CharacterId + 1);
            var events = new GameEventRepository(fixture.Database);
            var service = new QuestService(fixture.Database.ConnectionString);
            var repository = new QuestRepository(fixture.Database.ConnectionString);
            events.EnsureCatalogEntry(EpicEventId, true, "epic", "", 0);
            check("the owned character can accept an advertised introduction",
                service.HandleAcceptQuest(original.Owner, new QuestAcceptCommand(8597)).Success);
            events.EnsureCatalogEntry(EpicEventId, false, "", "", 0);
            check("closing the activity does not trap an already accepted task",
                service.HandleGiveupQuest(original.Owner, new QuestGiveupCommand(8597)).Success
                && repository.LoadActiveQuests(CharacterId + 1).Count == 0
                && repository.ReadClearedFlagValue(CharacterId + 1, 8597) == 0);
            events.EnsureCatalogEntry(EpicEventId, true, "", "", 0);
            var replacement = fixture.CreateOwner(CharacterId + 1);
            check("replacement inventory ownership fences the old session ACCEPT",
                !service.HandleAcceptQuest(original.Owner, new QuestAcceptCommand(8597)).Success
                && repository.LoadActiveQuests(CharacterId + 1).Count == 0
                && replacement.Owner.IsCurrentInventoryOwner());
            check("the replacement session still accepts through the existing owned transaction",
                service.HandleAcceptQuest(replacement.Owner, new QuestAcceptCommand(8597)).Success);
        }

        private static (byte Town, byte Area) FindSourceTownArea(IReadOnlyList<int> hiddenTownIds)
        {
            foreach (var entry in DungeonCatalog.LoadListFile("town/town.lst").Entries.OrderBy(candidate => candidate.Id))
            {
                if (entry.Id <= 0 || entry.Id > byte.MaxValue || entry.Id == EpicRoadEntryService.TargetTownId
                    || hiddenTownIds.Contains(entry.Id) || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;
                var path = entry.FilePath.Replace('\\', '/').TrimStart('/');
                if (!path.StartsWith("town/", StringComparison.OrdinalIgnoreCase)) path = "town/" + path;
                foreach (var area in TownFile.Parse(PvfArchiveAccessor.ReadText(path)).Areas)
                    if (area.Id >= 0 && area.Id <= byte.MaxValue && !string.IsNullOrWhiteSpace(area.MapPath)
                        && Town.TryGetArea(entry.Id, area.Id, out _))
                        return ((byte)entry.Id, (byte)area.Id);
            }
            throw new InvalidDataException("Current PVF has no source town area permitted by NPC1125.");
        }

        private static HashSet<ushort> ReadQuestIds(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.ReadByte();
            var count = reader.ReadUInt16();
            var result = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
                if (!result.Add(reader.ReadUInt16())) throw new InvalidDataException("Duplicate acceptable task ID.");
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Acceptable task body has unconsumed data.");
            return result;
        }

        private static HashSet<int> ReadEventIds(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            var count = reader.ReadUInt16();
            var result = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                result.Add(reader.ReadUInt16());
                reader.ReadUInt32();
                SkipDstr(reader);
                SkipDstr(reader);
                if (reader.ReadByte() == 0) continue;
                reader.ReadByte();
                reader.ReadByte();
                SkipDstr(reader);
                SkipDstr(reader);
                SkipDstr(reader);
                reader.ReadUInt32();
                reader.ReadUInt32();
                SkipDstr(reader);
                SkipDstr(reader);
                reader.ReadByte();
            }
            var extraCount = reader.ReadByte();
            for (var index = 0; index < extraCount; index++)
            {
                reader.ReadUInt16();
                for (var parameter = 0; parameter < 12; parameter++) reader.ReadUInt32();
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Event directory body has unconsumed data.");
            return result;
        }

        private static void SkipDstr(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid event directory string length.");
            if (reader.ReadBytes(length).Length != length) throw new EndOfStreamException();
        }

        private sealed class OwnerHandle
        {
            internal EnhancedClientSession Session { get; init; }
            internal InventoryService Inventory { get; init; }
            internal QuestCommandOwnerContext Owner { get; init; }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "s4a21-quest-event-" + Guid.NewGuid().ToString("N") + ".db");
            private readonly List<OwnerHandle> _owners = new();
            internal GameDatabase Database { get; }
            internal SessionDirectory Sessions { get; } = new();

            internal Fixture()
            {
                Database = new GameDatabase(_path, ServerPaths.SchemaFilePath);
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,'quest-event-selftest','');";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.ExecuteNonQuery();
                });
            }

            internal void Sql(string sql, int eventId)
            {
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@event", eventId);
                    command.ExecuteNonQuery();
                });
            }

            internal OwnerHandle CreateOwner(int characterId)
            {
                Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR IGNORE INTO characters(character_id,account_id,name,job,level) VALUES(@cid,@aid,@name,0,17);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@name", "quest-event-" + characterId);
                    command.ExecuteNonQuery();
                });
                InventoryService inventory;
                using (var connection = Database.OpenConnection())
                    inventory = InventoryService.LoadFromDb(connection, characterId, AccountId, Database);
                var session = new EnhancedClientSession(new TcpClient(), new GamePacketHeader());
                session.Account = new AccountRecord { AccountId = AccountId };
                session.Player.CharacterId = characterId;
                session.Player.UserId = 321;
                session.Player.Level = 17;
                session.GameSession = new GameSession(session, Database);
                var lease = InventoryContext.Register(session.SessionId, characterId, inventory);
                var handle = new OwnerHandle
                {
                    Session = session,
                    Inventory = inventory,
                    Owner = new QuestCommandOwnerContext(characterId, AccountId, session.SessionId, lease),
                };
                Sessions.Register(characterId, session);
                _owners.Add(handle);
                return handle;
            }

            public void Dispose()
            {
                foreach (var owner in _owners)
                {
                    InventoryContext.Unregister(owner.Owner.SessionId, owner.Owner.CharacterId);
                    owner.Session.Close();
                }
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
        }
    }
}
