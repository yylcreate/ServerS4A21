using DfoServer.Game.Dungeon;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class WeddingClearEffectSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-clear-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63201,'wedding-clear-a',''),(63202,'wedding-clear-b','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63201,63201,'通关甲',0,60),(63202,63202,'通关乙',1,60);"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(1,63201,63202,4,1800000000,1800000000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63201,1,0),(63202,1,1);");
                var capsules = new WeddingCapsuleRepository(db);
                var weddings = new WeddingRepository(db);
                var service = new DungeonPersistentEffectApplicationService(db.ConnectionString, database: db);
                var rules = WeddingCatalog.Get(); var capsule = WeddingCapsuleCatalog.Get();
                DungeonEffectId Effect(Guid source, long run) => new(source, DungeonPersistentEffectKinds.WeddingClearProgress, DungeonEffectScope.Player, run);
                var solo = Effect(Guid.NewGuid(), 11);
                Check("solo recommended clear advances own capsule only", service.TryApplyWeddingClear(solo, 63201, 63201, 1, 63202, false, true,
                    out var soloResult, out _) && soloResult.Applied && soloResult.CapsuleProgress == capsule.GaugePerClear
                    && weddings.Load(63201).Love == 0 && capsules.LoadProjection(63202).State.Progress == 0);
                Check("repeated player effect replays its committed result", service.TryApplyWeddingClear(solo, 63201, 63201, 1, 63202, false, true,
                    out var replay, out _) && replay.CapsuleProgress == soloResult.CapsuleProgress
                    && capsules.LoadProjection(63201).State.Progress == capsule.GaugePerClear);
                Check("same effect cannot change captured roster eligibility", !service.TryApplyWeddingClear(solo, 63201, 63201, 1, 63202, true, true, out _, out _));
                Check("same effect cannot change captured recommendation eligibility", !service.TryApplyWeddingClear(solo, 63201, 63201, 1, 63202, false, false, out _, out _));
                var nonrecommended = Effect(Guid.NewGuid(), 12);
                Check("nonrecommended clear does not advance capsule gauge", service.TryApplyWeddingClear(nonrecommended, 63202, 63202, 1, 63201, false, false,
                    out var ignored, out _) && ignored.Applied && !ignored.CapsuleProgressChanged
                    && capsules.LoadProjection(63202).State.Progress == 0);
                Guid sharedSource = Guid.NewGuid();
                var sharedA = Effect(sharedSource, 21); var sharedB = Effect(sharedSource, 22);
                Check("frozen spouse roster grants shared love once", service.TryApplyWeddingClear(sharedA, 63201, 63201, 1, 63202, true, true,
                    out var first, out _) && first.LoveChanged && first.Love == rules.LovePerClear);
                Check("second spouse gets own capsule but shares one love receipt", service.TryApplyWeddingClear(sharedB, 63202, 63202, 1, 63201, true, true,
                    out var second, out _) && second.Applied && !second.LoveChanged && second.Love == rules.LovePerClear
                    && capsules.LoadProjection(63202).State.Progress == capsule.GaugePerClear
                    && Scalar("SELECT COUNT(*) FROM wedding_progress_events WHERE pair_id=1;") == 1);
                var pending = Effect(Guid.NewGuid(), 31);
                int beforeCapsule = capsules.LoadProjection(63202).State.Progress;
                int beforeLove = weddings.Load(63202).Love;
                Sql("CREATE TRIGGER reject_wedding_clear BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'wedding clear rollback test'); END;");
                Check("SQL failure rolls back capsule, love and pair receipt", !service.TryApplyWeddingClear(pending, 63202, 63202, 1, 63201, true, true,
                    out _, out _) && capsules.LoadProjection(63202).State.Progress == beforeCapsule
                    && weddings.Load(63202).Love == beforeLove && Scalar("SELECT COUNT(*) FROM wedding_progress_events;") == 1);
                Sql("DROP TRIGGER reject_wedding_clear;");
                var recovery = service.RecoverCharacter(63202);
                Check("offline recovery commits the original durable clear", recovery.CommittedCount == 1 && recovery.FailedCount == 0
                    && capsules.LoadProjection(63202).State.Progress == Math.Min(capsule.MaximumGauge, beforeCapsule + capsule.GaugePerClear)
                    && weddings.Load(63202).Love == beforeLove + rules.LovePerClear);
                Check("recovery replay cannot repeat progress", service.RecoverCharacter(63202).CommittedCount == 0
                    && weddings.Load(63202).Love == beforeLove + rules.LovePerClear);
                var old = Effect(Guid.NewGuid(), 41);
                Sql("CREATE TRIGGER reject_old_wedding_clear BEFORE UPDATE ON wedding_pairs BEGIN SELECT RAISE(ABORT,'old wedding clear retry'); END;");
                Check("old marriage effect remains retryable after transactional failure", !service.TryApplyWeddingClear(old, 63201, 63201, 1, 63202, true, true,
                    out _, out _));
                Sql("DROP TRIGGER reject_old_wedding_clear; UPDATE wedding_pairs SET status=0 WHERE pair_id=1; DELETE FROM wedding_members;"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(2,63201,63202,4,1800001000,1800001000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63201,2,0),(63202,2,1);");
                recovery = service.RecoverCharacter(63201);
                Check("remarriage consumes old effect as no-op", recovery.CommittedCount == 1 && recovery.FailedCount == 0
                    && weddings.Load(63201).PairId == 2 && weddings.Load(63201).Love == 0
                    && capsules.LoadProjection(63201).State.Progress == 0);
                Check("old committed effect result preserves no-op identity", service.TryApplyWeddingClear(old, 63201, 63201, 1, 63202, true, true,
                    out var stale, out _) && !stale.Applied && stale.PairId == 1 && capsules.LoadProjection(63201).State.Progress == 0);
                var wrongOwner = Effect(Guid.NewGuid(), 51);
                Check("wrong account cannot accumulate wedding progress", service.TryApplyWeddingClear(wrongOwner, 63201, 63202, 2, 63202, true, true,
                    out var denied, out _) && !denied.Applied && weddings.Load(63201).Love == 0);
                Check("spouse love eligibility is independent of recommendation", service.TryApplyWeddingClear(Effect(Guid.NewGuid(), 61),
                    63201, 63201, 2, 63202, true, false, out var loveOnly, out _) && loveOnly.LoveChanged
                    && loveOnly.Love == rules.LovePerClear && !loveOnly.CapsuleProgressChanged
                    && capsules.LoadProjection(63201).State.Progress == 0);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_CLEAR_EFFECT: {passed} PASS / {failed} FAIL");
            return failed == 0 ? 0 : 1;
        }
    }
}
