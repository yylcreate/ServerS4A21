using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.GuardianAltar;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarProgressSelfTest
    {
        private static readonly string[] Tables =
        {
            "character_guardian_altar_stages", "character_guardian_altar_purchases",
            "character_guardian_altar_slots", "character_guardian_altar_achievements",
            "character_guardian_altar",
        };

        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_PROGRESS selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string name, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {name}");
                if (!success) failures++;
            }
            var token = Guid.NewGuid().ToString("N");
            var paths = new[]
            {
                Path.Combine(Path.GetTempPath(), "guardian-progress-migration-" + token + ".db"),
                Path.Combine(Path.GetTempPath(), "guardian-progress-state-" + token + ".db"),
            };
            try
            {
                VerifyMigration(paths[0], Check);
                VerifyProgress(paths[1], Check);
            }
            catch (Exception ex)
            {
                Check("Guardian progress unexpected exception: " + ex, false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var path in paths)
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                        if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"GUARDIAN_ALTAR_PROGRESS assertions={assertions} failures={failures}");
            return failures;
        }

        private static void VerifyMigration(string path, Action<string, bool> check)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            var freshSchema = Schema(database);
            check("fresh database contains all five typed Guardian tables at schema v34",
                SqliteMigrations.CurrentVersion >= 34 && CountTables(database) == 5
                && Scalar(database, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && Scalar(database, "SELECT schema_version FROM schema_metadata;") == SqliteMigrations.CurrentVersion);
            check("fresh schema does not seed accounts, characters or Guardian progress",
                Scalar(database, "SELECT COUNT(*) FROM accounts;") == 0
                && Scalar(database, "SELECT COUNT(*) FROM characters;") == 0
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar;") == 0);
            Sql(database, "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-migration','');"
                + "INSERT INTO characters(character_id,account_id,name,level) VALUES(1,1,'guardian',70);");
            foreach (var table in Tables) Sql(database, "DROP TABLE " + table + ";");
            Sql(database, "PRAGMA user_version=33; UPDATE schema_metadata SET schema_version=33;"
                + "CREATE TRIGGER reject_guardian_migration BEFORE UPDATE OF schema_version ON schema_metadata "
                + "WHEN NEW.schema_version=34 BEGIN SELECT RAISE(ABORT,'guardian-migration-test'); END;");
            check("failed v34 migration rolls back every table and both schema versions",
                Throws<SqliteException>(() => Apply(database)) && CountTables(database) == 0
                && Scalar(database, "PRAGMA user_version;") == 33
                && Scalar(database, "SELECT schema_version FROM schema_metadata;") == 33);
            Sql(database, "DROP TRIGGER reject_guardian_migration;");
            Apply(database);
            Apply(database);
            check("v33 upgrade is repeatable, preserves characters and matches full fresh DDL",
                CountTables(database) == 5 && Schema(database) == freshSchema
                && Scalar(database, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && Scalar(database, "SELECT schema_version FROM schema_metadata;") == SqliteMigrations.CurrentVersion
                && Scalar(database, "SELECT level FROM characters WHERE character_id=1;") == 70);
            Sql(database, "UPDATE schema_metadata SET baseline_id='wrong-guardian-baseline';");
            check("wrong database baseline is rejected without altering schema",
                Throws<InvalidOperationException>(() => Apply(database)) && CountTables(database) == 5);
            Sql(database, $"UPDATE schema_metadata SET baseline_id='86jp-database-v1', schema_version={SqliteMigrations.CurrentVersion + 1}; PRAGMA user_version={SqliteMigrations.CurrentVersion + 1};");
            check("future database version is rejected without altering Guardian tables",
                Throws<InvalidOperationException>(() => Apply(database)) && CountTables(database) == 5);
            Sql(database, $"UPDATE schema_metadata SET schema_version={SqliteMigrations.CurrentVersion}; PRAGMA user_version={SqliteMigrations.CurrentVersion};");
        }

        private static void VerifyProgress(string path, Action<string, bool> check)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            Sql(database, "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'guardian-progress','');");
            for (var id = 1; id <= 9; id++)
                Sql(database, $"INSERT INTO characters(character_id,account_id,name,level) VALUES({id},1,'guardian-{id}',70);");
            var definition = Definition();
            var daily = new DailyResetService(database);
            var service = new GuardianAltarProgressService(database, daily, definition);
            var before = new DateTime(2026, 10, 1, 21, 59, 59, DateTimeKind.Utc);
            var boundary = before.AddSeconds(1); // Beijing 2026-10-02 06:00.
            var receiptKey = "guardian_altar_refill_v1:1";
            var first = service.LoadMenu(1, before);
            check("fresh role header uses the proven initializer and configured free-ticket refill",
                first.Header == new GuardianAltarHeader(1, 3, 0, 0, 0, 0, 0, 0, 0, 0));
            check("fresh role owns only the first stage, typed purchases, all slots and no achievements",
                first.Stages.SequenceEqual(new[] { new GuardianAltarStage(3511, -1) })
                && first.Purchases.SequenceEqual(definition.InitialPurchases.OrderBy(x => x.ShopType).ThenBy(x => x.ResourceId))
                && first.Slots.SequenceEqual(definition.InitialSlots.OrderBy(x => x.SlotType).ThenBy(x => x.SlotIndex))
                && first.Achievements.Count == 0);
            var repeated = service.LoadMenu(1, before);
            check("same-day menu reads are idempotent and claim exactly one refill receipt",
                Same(first, repeated) && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar WHERE character_id=1;") == 1
                && Scalar(database, $"SELECT value FROM character_daily_counters WHERE character_id=1 AND counter_key='{receiptKey}';") == 1);
            check("another role in the same account has independent fresh progress",
                service.LoadMenu(2, before).Header == first.Header
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar;") == 2);

            Sql(database, @"
UPDATE character_guardian_altar SET free_tickets=1, secondary_tickets=2,
    stage_stars=4, other_stars=5, available_stars=7, survival_best_phase=9,
    star_reset_count=2, reserved28=4294967295, menu_hint_flag=1 WHERE character_id=1;
UPDATE character_guardian_altar_stages SET progress=2 WHERE character_id=1;
UPDATE character_guardian_altar_purchases SET level=2, price=5 WHERE character_id=1 AND shop_type=0 AND resource_id=102;
UPDATE character_guardian_altar_slots SET resource_id=102 WHERE character_id=1 AND slot_type=1 AND slot_index=1;
INSERT INTO character_guardian_altar_achievements VALUES(1,1,17,1);");
            var progressed = service.LoadMenu(1, before);
            check("same-day reads preserve existing tickets, stars, purchased levels, slots and claimed achievements",
                progressed.Header == new GuardianAltarHeader(1, 1, 2, 4, 5, 7, 9, 2, uint.MaxValue, 1)
                && progressed.Stages.Single().Progress == 2
                && progressed.Purchases.Single(x => x.ShopType == 0 && x.ResourceId == 102).Level == 2
                && progressed.Slots.Single(x => x.SlotType == 1 && x.SlotIndex == 1).ResourceId == 102
                && progressed.Achievements.Single() == new GuardianAltarAchievement(17, 1));
            var restarted = new GuardianAltarProgressService(new GameDatabase(path, ServerPaths.SchemaFilePath),
                new DailyResetService(database), definition);
            check("a newly composed service reads the durable role instead of a runtime cache", Same(progressed, restarted.LoadMenu(1, before)));
            var nextDay = restarted.LoadMenu(1, boundary);
            check("06:00 replaces only free tickets once while preserving all persistent role facts",
                nextDay.Header == (progressed.Header with { FreeTickets = 3 })
                && SameRows(progressed, nextDay)
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=1;") == DailyResetService.TodayId(boundary));
            Sql(database, "UPDATE character_guardian_altar SET free_tickets=0 WHERE character_id=1;");
            check("repeated next-day requests never add or reapply already-claimed refill",
                restarted.LoadMenu(1, boundary.AddSeconds(1)).Header.FreeTickets == 0);
            check("stale UTC anchors cannot refill or rewind daily state",
                Throws<InvalidOperationException>(() => service.LoadMenu(1, before))
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=1;") == 0
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=1;") == DailyResetService.TodayId(boundary));
            check("an unspecified clock value is rejected before touching progress",
                Throws<ArgumentException>(() => service.LoadMenu(1, DateTime.SpecifyKind(boundary, DateTimeKind.Unspecified))));
            var rolloverFenceCalls = 0;
            check("a displaced generation also rolls back an existing role's next-day refill",
                Throws<InvalidOperationException>(() => service.LoadMenu(1, boundary.AddDays(1), () => ++rolloverFenceCalls < 2))
                && rolloverFenceCalls == 2
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=1;") == 0
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=1;") == DailyResetService.TodayId(boundary));

            Sql(database, "CREATE TRIGGER reject_guardian_child BEFORE INSERT ON character_guardian_altar_purchases "
                + "WHEN NEW.character_id=3 BEGIN SELECT RAISE(ABORT,'guardian-child-test'); END;");
            check("a failed initial child insert rolls back header, all children and daily receipt",
                Throws<SqliteException>(() => service.LoadMenu(3, before))
                && RoleRowCount(database, 3) == 0
                && Scalar(database, "SELECT COUNT(*) FROM character_daily_reset WHERE character_id=3;") == 0
                && Scalar(database, "SELECT COUNT(*) FROM character_daily_counters WHERE character_id=3;") == 0);
            Sql(database, "DROP TRIGGER reject_guardian_child;");
            check("retry after failed initialization creates exactly one complete role", service.LoadMenu(3, before).Slots.Count == 12);

            service.LoadMenu(4, before);
            Sql(database, "UPDATE character_guardian_altar SET free_tickets=0,secondary_tickets=2 WHERE character_id=4;"
                + "CREATE TRIGGER reject_guardian_refill_receipt BEFORE INSERT ON character_daily_counters "
                + $"WHEN NEW.character_id=4 AND NEW.counter_key='{receiptKey}' BEGIN SELECT RAISE(ABORT,'guardian-receipt-test'); END;");
            check("failed rollover receipt rolls back day deletion, anchor and free-ticket replacement",
                Throws<SqliteException>(() => service.LoadMenu(4, boundary))
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=4;") == DailyResetService.TodayId(before)
                && Scalar(database, $"SELECT value FROM character_daily_counters WHERE character_id=4 AND counter_key='{receiptKey}';") == 1
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=4;") == 0);
            Sql(database, "DROP TRIGGER reject_guardian_refill_receipt;"
                + "CREATE TRIGGER reject_guardian_ticket_update BEFORE UPDATE OF free_tickets ON character_guardian_altar "
                + "WHEN NEW.character_id=4 BEGIN SELECT RAISE(ABORT,'guardian-ticket-test'); END;");
            check("failed ticket write also rolls back the already-reserved receipt and day anchor",
                Throws<SqliteException>(() => service.LoadMenu(4, boundary))
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=4;") == DailyResetService.TodayId(before)
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=4;") == 0);
            Sql(database, "DROP TRIGGER reject_guardian_ticket_update;");
            var recovered = service.LoadMenu(4, boundary);
            check("rollover retry replaces free tickets and retains secondary tickets", recovered.Header.FreeTickets == 3 && recovered.Header.SecondaryTickets == 2);

            check("an old generation at transaction start cannot initialize any role facts",
                Throws<InvalidOperationException>(() => service.LoadMenu(5, before, () => false)) && RoleRowCount(database, 5) == 0);
            var fenceCalls = 0;
            check("a displaced generation before commit rolls back initialization and refill",
                Throws<InvalidOperationException>(() => service.LoadMenu(5, before, () => ++fenceCalls < 2))
                && fenceCalls == 2 && RoleRowCount(database, 5) == 0
                && Scalar(database, "SELECT COUNT(*) FROM character_daily_reset WHERE character_id=5;") == 0);
            check("current generation can retry after the fenced transaction rollback", service.LoadMenu(5, before, () => true).Header.FreeTickets == 3);

            service.LoadMenu(6, before);
            Sql(database, "DELETE FROM character_guardian_altar_purchases WHERE character_id=6 AND shop_type=2;"
                + "UPDATE character_guardian_altar SET free_tickets=0 WHERE character_id=6;");
            check("missing required purchase is rejected, never silently restored or refilled",
                Throws<InvalidOperationException>(() => service.LoadMenu(6, boundary))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_purchases WHERE character_id=6 AND shop_type=2;") == 0
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=6;") == 0
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=6;") == DailyResetService.TodayId(before));
            Sql(database, "INSERT INTO character_guardian_altar_purchases VALUES(6,1,2,301,0,0);"
                + "DELETE FROM character_guardian_altar_slots WHERE character_id=6 AND slot_type=1 AND slot_index=5;");
            check("missing persisted slot is not recreated during menu reads",
                Throws<InvalidOperationException>(() => service.LoadMenu(6, boundary))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_slots WHERE character_id=6;") == 11);

            var concurrent = Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => service.LoadMenu(7, before)))).GetAwaiter().GetResult();
            check("concurrent first-menu requests initialize one complete role and one receipt",
                concurrent.All(x => Same(concurrent[0], x)) && RoleRowCount(database, 7) == 18
                && Scalar(database, $"SELECT value FROM character_daily_counters WHERE character_id=7 AND counter_key='{receiptKey}';") == 1);
            Sql(database, "DELETE FROM character_guardian_altar_stages WHERE character_id=7;");
            check("an existing role missing its initial stage is rejected without seeding the stage again",
                Throws<InvalidOperationException>(() => service.LoadMenu(7, before))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_stages WHERE character_id=7;") == 0);

            Sql(database, "UPDATE characters SET delete_flag=1 WHERE character_id=8;");
            check("missing and soft-deleted characters cannot create orphan progress",
                Throws<InvalidOperationException>(() => service.LoadMenu(999, before))
                && Throws<InvalidOperationException>(() => service.LoadMenu(8, before)) && RoleRowCount(database, 8) == 0);
            var lazyLoads = 0;
            var lazy = new GuardianAltarProgressService(database, daily, () => { lazyLoads++; return definition; });
            check("Guardian static resources remain lazy rather than blocking runtime composition", lazyLoads == 0);
            lazy.LoadMenu(9, before);
            lazy.LoadMenu(9, before);
            check("lazy immutable definition is loaded once without caching character state", lazyLoads == 1);
            Sql(database, $"UPDATE character_daily_counters SET period='week',value=0 WHERE character_id=9 AND counter_key='{receiptKey}';"
                + "UPDATE character_guardian_altar SET free_tickets=0 WHERE character_id=9;");
            check("corrupted weekly Guardian receipt cannot be claimed as an unclaimed daily refill",
                Throws<InvalidOperationException>(() => service.LoadMenu(9, boundary))
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=9;") == 0
                && Scalar(database, "SELECT day_id FROM character_daily_reset WHERE character_id=9;") == DailyResetService.TodayId(before));
            Sql(database, $"UPDATE character_daily_counters SET period='day',value=2 WHERE character_id=9 AND counter_key='{receiptKey}';");
            check("corrupt receipt values are rejected before rollover can hide the damage",
                Throws<InvalidOperationException>(() => service.LoadMenu(9, boundary))
                && Scalar(database, "SELECT free_tickets FROM character_guardian_altar WHERE character_id=9;") == 0);
            var unavailable = new GuardianAltarProgressService(database, daily,
                () => throw new InvalidDataException("test Guardian definition unavailable"));
            check("invalid resources have no fabricated fallback or orphan role initialization",
                Throws<InvalidDataException>(() => unavailable.LoadMenu(8, before)) && RoleRowCount(database, 8) == 0);
            VerifyCatalogRejection(database, service, before, check);
            VerifyDeploymentRejection(database, daily, service, before, check);
            VerifyConstraints(database, check);
            Sql(database, "DELETE FROM characters WHERE character_id=2;");
            check("hard character deletion cascades all five Guardian tables", RoleRowCount(database, 2) == 0
                && Throws<InvalidOperationException>(() => service.LoadMenu(2, before)));
        }

        private static void VerifyCatalogRejection(GameDatabase database, GuardianAltarProgressService service,
            DateTime utcNow, Action<string, bool> check)
        {
            Sql(database, "UPDATE character_guardian_altar_purchases SET level=32767 WHERE character_id=2 AND shop_type=0 AND resource_id=101;");
            check("a numerically valid but unknown purchased level is rejected without repair",
                Throws<InvalidOperationException>(() => service.LoadMenu(2, utcNow))
                && Scalar(database, "SELECT level FROM character_guardian_altar_purchases WHERE character_id=2 AND shop_type=0 AND resource_id=101;") == 32767);
            Sql(database, "UPDATE character_guardian_altar_purchases SET level=1,price=32767 WHERE character_id=2 AND shop_type=0 AND resource_id=101;");
            check("unknown catalog price is rejected rather than projected to the client",
                Throws<InvalidOperationException>(() => service.LoadMenu(2, utcNow)));
            Sql(database, "UPDATE character_guardian_altar_purchases SET price=0 WHERE character_id=2 AND shop_type=0 AND resource_id=101;"
                + "INSERT INTO character_guardian_altar_stages VALUES(2,1,9999,0);");
            check("unknown stored dungeon is rejected and is not silently removed",
                Throws<InvalidOperationException>(() => service.LoadMenu(2, utcNow))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_stages WHERE character_id=2 AND dungeon_id=9999;") == 1);
            Sql(database, "DELETE FROM character_guardian_altar_stages WHERE character_id=2 AND dungeon_id=9999;"
                + "INSERT INTO character_guardian_altar_achievements VALUES(2,1,9999,0);");
            check("unknown achievement is rejected before client menu projection",
                Throws<InvalidOperationException>(() => service.LoadMenu(2, utcNow)));
            Sql(database, "DELETE FROM character_guardian_altar_achievements WHERE character_id=2 AND achievement_id=9999;");
            check("explicitly repaired catalog fixture can be read without losing its stored progress",
                service.LoadMenu(2, utcNow).Purchases.Count == 4);
        }

        private static void VerifyConstraints(GameDatabase database, Action<string, bool> check)
        {
            foreach (var statement in new[]
            {
                "INSERT INTO character_guardian_altar_stages VALUES(2,1,3511,-1);",
                "INSERT INTO character_guardian_altar_purchases VALUES(2,1,0,101,1,0);",
                "INSERT INTO character_guardian_altar_slots VALUES(2,1,1,0,0,101);",
                "INSERT INTO character_guardian_altar_achievements VALUES(1,1,17,1);",
            })
                check("typed child keys reject duplicate persistent records", Throws<SqliteException>(() => Sql(database, statement)));
            foreach (var statement in new[]
            {
                "UPDATE character_guardian_altar SET free_tickets=-1 WHERE character_id=2;",
                "UPDATE character_guardian_altar SET free_tickets=2147483647,secondary_tickets=1 WHERE character_id=2;",
                "UPDATE character_guardian_altar SET available_stars=1 WHERE character_id=2;",
                "UPDATE character_guardian_altar SET survival_best_phase=32768 WHERE character_id=2;",
                "UPDATE character_guardian_altar SET reserved28=4294967296 WHERE character_id=2;",
                "UPDATE character_guardian_altar SET menu_hint_flag=2 WHERE character_id=2;",
                "UPDATE character_guardian_altar_purchases SET level=32768 WHERE character_id=2;",
                "UPDATE character_guardian_altar_purchases SET price=-1 WHERE character_id=2;",
                "UPDATE character_guardian_altar_slots SET locked=2 WHERE character_id=2;",
                "UPDATE character_guardian_altar_slots SET slot_index=6 WHERE character_id=2;",
                "UPDATE character_guardian_altar_achievements SET claimed=2 WHERE character_id=1;",
                "INSERT INTO character_guardian_altar_stages VALUES(999,1,3511,-1);",
            })
                check("schema rejects invalid role widths, flags, sums and composite owners", Throws<SqliteException>(() => Sql(database, statement)));
        }

        private static void VerifyDeploymentRejection(GameDatabase database, DailyResetService daily,
            GuardianAltarProgressService service, DateTime utcNow, Action<string, bool> check)
        {
            var before = service.LoadMenu(2, utcNow);
            var passiveCatalog = new GuardianAltarProgressService(database, daily, Definition(skillSettable: false));
            check("persisted learned skill made passive by its typed catalog is rejected before menu projection",
                Throws<InvalidOperationException>(() => passiveCatalog.LoadMenu(2, utcNow))
                && Scalar(database, "SELECT resource_id FROM character_guardian_altar_slots WHERE character_id=2 AND slot_type=2 AND slot_index=0;") == 201
                && Same(before, service.LoadMenu(2, utcNow)));
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=101 WHERE character_id=2 AND slot_type=1 AND slot_index=1;");
            check("duplicate persisted deployment is rejected without silently removing either slot",
                Throws<InvalidOperationException>(() => service.LoadMenu(2, utcNow))
                && Scalar(database, "SELECT COUNT(*) FROM character_guardian_altar_slots WHERE character_id=2 AND slot_type=1 AND resource_id=101;") == 2);
            Sql(database, "UPDATE character_guardian_altar_slots SET resource_id=0 WHERE character_id=2 AND slot_type=1 AND slot_index=1;");
            check("explicit repair of the deployment fixture retains purchases, tickets and other durable facts",
                Same(before, service.LoadMenu(2, utcNow)));
            var ambiguousCatalog = new GuardianAltarProgressService(database, daily,
                Definition(ambiguousUnitRow: true));
            check("ambiguous higher catalog rows of an undeployed resource do not block valid menu progress",
                Same(before, ambiguousCatalog.LoadMenu(2, utcNow)));
            Sql(database, "UPDATE character_guardian_altar_purchases SET level=2,price=5 WHERE character_id=2 AND shop_type=0 AND resource_id=102;"
                + "UPDATE character_guardian_altar_slots SET resource_id=102 WHERE character_id=2 AND slot_type=1 AND slot_index=1;");
            check("a deployed learned row must resolve one catalog index instead of choosing an ambiguous row",
                Throws<InvalidOperationException>(() => ambiguousCatalog.LoadMenu(2, utcNow))
                && Scalar(database, "SELECT resource_id FROM character_guardian_altar_slots WHERE character_id=2 AND slot_type=1 AND slot_index=1;") == 102);
            Sql(database, "UPDATE character_guardian_altar_purchases SET level=0,price=1 WHERE character_id=2 AND shop_type=0 AND resource_id=102;"
                + "UPDATE character_guardian_altar_slots SET resource_id=0 WHERE character_id=2 AND slot_type=1 AND slot_index=1;");
            check("restoring the isolated ambiguous deployment fixture preserves the original complete role",
                Same(before, service.LoadMenu(2, utcNow)));
        }

        private static GuardianAltarMenuDefinition Definition(bool skillSettable = true, bool ambiguousUnitRow = false)
        {
            // The fixture uses the same typed PvfLib catalog as production.
            // Its initial slots and all accepted learned rows come from that
            // owner rather than an empty shop bypass for direct construction.
            static string Entry(int id, string rows, bool settable = true)
                => $"[id]\n{id}\n[unlock stage index]\n0\n"
                    + $"[is settable slot]\n{(settable ? 1 : 0)}\n"
                    + "[field name]\n`level` `index` `price`\n[/field name]\n[list]\n"
                    + rows + "[/list]\n[/id]\n";
            var config = GuardianAltarConfigFile.Parse("[daily refill ticket]\n3\n[/refill ticket]\n",
                "[ridable id]\n1\n[unit]\n" + Entry(101, "1 101 0\n")
                    + Entry(102, "0 102 1\n2 102 5\n" + (ambiguousUnitRow ? "2 103 5\n" : ""))
                    + "[/unit]\n[skill]\n"
                    + Entry(201, "1 201 0\n", skillSettable) + "[/skill]\n[tower]\n"
                    + Entry(301, "0 301 0\n") + "[/tower]\n[/ridable id]\n");
            var stages = GuardianAltarStageMapFile.Parse("[area]\n0\n[district]\n1\n[stage]\n"
                + "3511 0 0 0\n3512 1 0 0\n[/stage]\n[/area]\n");
            return GuardianAltarMenuDefinition.Create(config, stages, _ => true,
                knownAchievementIds: new[] { 17 });
        }

        private static bool Same(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.CharacterId == right.CharacterId && left.Header == right.Header && SameRows(left, right);

        private static bool SameRows(GuardianAltarProgressSnapshot left, GuardianAltarProgressSnapshot right)
            => left.Stages.SequenceEqual(right.Stages) && left.Purchases.SequenceEqual(right.Purchases)
                && left.Slots.SequenceEqual(right.Slots) && left.Achievements.SequenceEqual(right.Achievements);

        private static long RoleRowCount(GameDatabase database, int characterId)
            => Tables.Sum(table => Scalar(database, $"SELECT COUNT(*) FROM {table} WHERE character_id={characterId};"));

        private static long CountTables(GameDatabase database)
            => Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'character_guardian_altar%';");

        private static string Schema(GameDatabase database) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name LIKE 'character_guardian_altar%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var result = new System.Text.StringBuilder();
            while (reader.Read()) result.AppendLine(reader.GetString(0));
            return result.ToString();
        });

        private static long Scalar(GameDatabase database, string sql) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        });

        private static void Sql(GameDatabase database, string sql) => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });

        private static void Apply(GameDatabase database)
        {
            using var connection = database.OpenConnection();
            SqliteMigrations.Apply(connection);
        }

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); return false; }
            catch (TException) { return true; }
        }
    }
}
