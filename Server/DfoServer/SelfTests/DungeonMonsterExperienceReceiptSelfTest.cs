using System;
using System.IO;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    internal static class DungeonMonsterExperienceReceiptSelfTest
    {
        internal static void Run(ref int failures)
        {
            VerifyJournalRetry(ref failures);
            VerifyAccountProjection(ref failures);
        }

        private static void VerifyJournalRetry(ref int failures)
        {
            var journal = CreateJournal(out var source, out var participant);
            var actor = new DungeonMonsterDropIdentity(7, 101);
            Check("EXP receipt freezes the real room participant roster",
                journal.TryFreeze(source, DungeonParticipantEffectAudience.Room,
                    new[] { participant }, out var roster) && roster.Count == 1,
                ref failures);
            Check("EXP receipt starts through a real kill reservation",
                Begin(journal, source, participant, out var failedReservation), ref failures);

            var applyCalls = 0;
            var threw = false;
            try
            {
                journal.TryApplyMonsterExperienceOutcome(failedReservation, actor, () =>
                {
                    applyCalls++;
                    throw new InvalidOperationException("injected failure before EXP acceptance");
                }, out _);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Check("a throwing EXP apply leaves no accepted receipt",
                threw && applyCalls == 1
                    && journal.ResolveMonsterExperienceOutcome(failedReservation, actor, out _)
                        == DungeonMonsterExperienceOutcomeResolution.Absent,
                ref failures);
            var failed = journal.TryFail(failedReservation);
            var began = Begin(journal, source, participant, out var acceptedReservation);
            Check("an unaccepted EXP failure can begin again with a new lease",
                failed && began, ref failures);
            Check("a failed attempt's old lease cannot apply, commit, or fail the retry",
                journal.ResolveMonsterExperienceOutcome(failedReservation, actor, out _)
                        == DungeonMonsterExperienceOutcomeResolution.IdentityMismatch
                    && !journal.TryApplyMonsterExperienceOutcome(failedReservation, actor, () =>
                    {
                        applyCalls++;
                        return CreateOutcome();
                    }, out _)
                    && !journal.TryCommit(failedReservation)
                    && !journal.TryFail(failedReservation) && applyCalls == 1,
                ref failures);

            var supplied = CreateOutcome();
            Check("the retried EXP apply records one accepted grant",
                journal.TryApplyMonsterExperienceOutcome(acceptedReservation, actor, () =>
                {
                    applyCalls++;
                    return supplied;
                }, out var returned) && applyCalls == 2,
                ref failures);
            supplied.Grant.RawGain = 999;
            supplied.Grant.Honor.TotalHonorExp = 999;
            supplied.Grant.GrowthCapsule.TotalExp = 999;
            returned.Grant.Honor.HonorExp = 999;
            returned.Grant.GrowthCapsule.RequiredExp = 999;
            var resolved = journal.ResolveMonsterExperienceOutcome(acceptedReservation, actor, out var stored);
            Check("accepted EXP deep-copies both input and returned account summaries",
                resolved == DungeonMonsterExperienceOutcomeResolution.Resolved
                    && MatchesOriginal(stored), ref failures);
            stored.Grant.Honor.HonorGrade = 99;
            stored.Grant.Honor.FullLevelCharacterCount = 99;
            stored.Grant.GrowthCapsule.TotalExp = 99;
            Check("mutating a resolved nested summary cannot alter the journal receipt",
                journal.ResolveMonsterExperienceOutcome(acceptedReservation, actor, out stored)
                        == DungeonMonsterExperienceOutcomeResolution.Resolved
                    && MatchesOriginal(stored), ref failures);

            var wrongActor = new DungeonMonsterDropIdentity(8, 101);
            var wrongCode = new DungeonMonsterDropIdentity(7, 102);
            Check("an accepted EXP receipt rejects a different actor sequence or monster code",
                journal.ResolveMonsterExperienceOutcome(acceptedReservation, wrongActor, out _)
                        == DungeonMonsterExperienceOutcomeResolution.IdentityMismatch
                    && journal.ResolveMonsterExperienceOutcome(acceptedReservation, wrongCode, out _)
                        == DungeonMonsterExperienceOutcomeResolution.IdentityMismatch
                    && !journal.TryApplyMonsterExperienceOutcome(acceptedReservation, wrongCode, () =>
                    {
                        applyCalls++;
                        return CreateOutcome();
                    }, out _) && applyCalls == 2,
                ref failures);

            Check("a network failure preserves the accepted EXP receipt for recovery",
                journal.TryFail(acceptedReservation)
                    && journal.GetRecoverableForParticipant(participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectAudience.Room, DungeonParticipantEffectKinds.MonsterKill).Count == 1,
                ref failures);
            Check("a failed network projection begins with a new reservation",
                Begin(journal, source, participant, out var projectionReservation), ref failures);
            Check("projection recovery reuses EXP without invoking the grant callback",
                journal.TryApplyMonsterExperienceOutcome(projectionReservation, actor, () =>
                {
                    applyCalls++;
                    throw new InvalidOperationException("an accepted EXP grant must not run again");
                }, out var replayed) && applyCalls == 2 && MatchesOriginal(replayed)
                    && !journal.TryCommit(acceptedReservation), ref failures);
            var committed = journal.TryCommit(projectionReservation);
            var duplicate = journal.TryBegin(source.SourceEventId, DungeonParticipantEffectAudience.Room,
                participant, DungeonParticipantEffectKinds.MonsterKill, out _, out var state);
            Check("committed EXP recovery rejects duplicate execution and leaves no pending work",
                committed && !duplicate && state == DungeonParticipantEffectState.Committed
                    && journal.GetRecoverableForParticipant(participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectAudience.Room, DungeonParticipantEffectKinds.MonsterKill).Count == 0,
                ref failures);
        }

        private static void VerifyAccountProjection(ref int failures)
        {
            var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "dungeon-exp-receipt-" + Guid.NewGuid().ToString("N")));
            GameDatabase database = null;
            try
            {
                Directory.CreateDirectory(directory);
                database = new GameDatabase(Path.Combine(directory, "receipt.db"), ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash,honor_exp,growth_capsule_exp)
VALUES(95101,'dungeon-exp-receipt','',100,0);
INSERT INTO characters(character_id,account_id,name,level)
VALUES(95101,95101,'dungeon-exp-receipt',@level);";
                    command.Parameters.AddWithValue("@level", ExpTableProvider.MaxLevel);
                    command.ExecuteNonQuery();
                });
                var progress = new AccountExperienceProgressService(new SqliteCharacterRepository(database), database);
                var experience = new CharacterExperienceService(progress, database);
                var initial = progress.LoadCurrentSummary(95101);
                var grant = CreateOutcome().Grant;
                grant.Honor = initial.Honor;
                grant.GrowthCapsule = initial.GrowthCapsule;
                grant.TotalHonorExp = initial.Honor.TotalHonorExp;
                grant.TotalGrowthCapsuleExp = initial.GrowthCapsule.TotalExp;
                var journal = CreateJournal(out var source, out var participant);
                var actor = new DungeonMonsterDropIdentity(7, 101);
                journal.TryFreeze(source, DungeonParticipantEffectAudience.Room, new[] { participant }, out _);
                Begin(journal, source, participant, out var reservation);
                journal.TryApplyMonsterExperienceOutcome(reservation, actor,
                    () => CreateOutcome() with { Grant = grant }, out var accepted);
                journal.TryFail(reservation);
                Begin(journal, source, participant, out reservation);
                journal.ResolveMonsterExperienceOutcome(reservation, actor, out var replayed);

                // A later grant belongs to the existing account owner, while
                // the kill receipt keeps the original accepted measurements.
                var latest = progress.AddHonorAndGrowthCapsuleExp(95101, 500);
                experience.RefreshAccountProgressForProjection(replayed.Grant, 95101);
                Check("EXP retry refreshes current honor and capsule through the account owner",
                    latest.Honor.TotalHonorExp > initial.Honor.TotalHonorExp
                        && latest.GrowthCapsule.TotalExp > initial.GrowthCapsule.TotalExp
                        && SameSummary(latest.Honor, replayed.Grant.Honor)
                        && SameSummary(latest.GrowthCapsule, replayed.Grant.GrowthCapsule),
                    ref failures);
                Check("refreshing EXP display preserves the original grant measurements",
                    SameAmounts(accepted.Grant, replayed.Grant), ref failures);
                var current = progress.LoadCurrentSummary(95101);
                Check("refreshing an EXP projection cannot regrant account experience",
                    SameSummary(latest.Honor, current.Honor)
                        && SameSummary(latest.GrowthCapsule, current.GrowthCapsule), ref failures);
                Check("the refreshed projection cannot rewrite the frozen journal account summaries",
                    journal.ResolveMonsterExperienceOutcome(reservation, actor, out var frozen)
                            == DungeonMonsterExperienceOutcomeResolution.Resolved
                        && SameAmounts(accepted.Grant, frozen.Grant)
                        && SameSummary(initial.Honor, frozen.Grant.Honor)
                        && SameSummary(initial.GrowthCapsule, frozen.Grant.GrowthCapsule),
                    ref failures);
            }
            catch (Exception exception)
            {
                Check("isolated EXP account projection fixture completes: " + exception.Message, false, ref failures);
            }
            finally
            {
                if (database != null)
                {
                    using var connection = new SqliteConnection(database.ConnectionString);
                    SqliteConnection.ClearPool(connection);
                }
                // This path is the unique directory created by this fixture;
                // never recurse into a caller-provided or runtime database path.
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        private static DungeonParticipantEffectJournal CreateJournal(
            out DungeonEventEnvelope source, out DungeonParticipantRosterEntry participant)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(instance, DungeonIdentityGenerator.NextRunId(), 1, DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            participant = new DungeonParticipantRosterEntry(95101, 101, run,
                run.CaptureIdentity(), room, 1);
            source = new DungeonEventEnvelope(Guid.NewGuid(), run.CaptureIdentity(), room.RoomInstanceId,
                95101, 95101, 7, 101, "monster-exp-receipt-selftest", 1);
            return instance.ParticipantEffects;
        }

        private static bool Begin(DungeonParticipantEffectJournal journal, DungeonEventEnvelope source,
            DungeonParticipantRosterEntry participant, out DungeonParticipantEffectReservation reservation)
            => journal.TryBegin(source.SourceEventId, DungeonParticipantEffectAudience.Room, participant,
                DungeonParticipantEffectKinds.MonsterKill, out reservation, out _);

        private static DungeonMonsterExperienceOutcome CreateOutcome()
            => new DungeonMonsterExperienceOutcome(100, 20, 30, 40, 10, 10, 15, 200,
                new ExperienceGrantResult
                {
                    RawGain = 200, NormalExpGain = 150, HonorExpGain = 50,
                    PreviousLevel = 85, PreviousExp = 1000, NewLevel = 86, NewExp = 1150,
                    Persisted = true, NormalizedMaxLevelExp = true,
                    GrowthCapsuleExpGain = 50, TotalHonorExp = 500, TotalGrowthCapsuleExp = 1000,
                    Honor = new HonorLevelSummary { TotalHonorExp = 500, HonorExp = 100,
                        HonorLevel = 2, HonorGrade = 1, FullLevelCharacterCount = 3 },
                    GrowthCapsule = new GrowthCapsuleSummary { TotalExp = 1000, RequiredExp = 5000 },
                });

        private static bool MatchesOriginal(DungeonMonsterExperienceOutcome actual)
        {
            var expected = CreateOutcome();
            return actual.MonsterBaseExperience == expected.MonsterBaseExperience
                && actual.GrowthContractBonusExperience == expected.GrowthContractBonusExperience
                && actual.EquipmentBonusExperience == expected.EquipmentBonusExperience
                && actual.FatigueBurnBonusExperience == expected.FatigueBurnBonusExperience
                && actual.GuildBonusExperience == expected.GuildBonusExperience
                && actual.GuildBonusPercent == expected.GuildBonusPercent
                && actual.EliteDisplayBonusExperience == expected.EliteDisplayBonusExperience
                && actual.RawExperience == expected.RawExperience && SameAmounts(expected.Grant, actual.Grant)
                && SameSummary(expected.Grant.Honor, actual.Grant?.Honor)
                && SameSummary(expected.Grant.GrowthCapsule, actual.Grant?.GrowthCapsule);
        }

        private static bool SameAmounts(ExperienceGrantResult expected, ExperienceGrantResult actual)
            => expected != null && actual != null && expected.RawGain == actual.RawGain
                && expected.NormalExpGain == actual.NormalExpGain && expected.HonorExpGain == actual.HonorExpGain
                && expected.PreviousLevel == actual.PreviousLevel && expected.PreviousExp == actual.PreviousExp
                && expected.NewLevel == actual.NewLevel && expected.NewExp == actual.NewExp
                && expected.Persisted == actual.Persisted
                && expected.NormalizedMaxLevelExp == actual.NormalizedMaxLevelExp
                && expected.GrowthCapsuleExpGain == actual.GrowthCapsuleExpGain
                && expected.TotalHonorExp == actual.TotalHonorExp
                && expected.TotalGrowthCapsuleExp == actual.TotalGrowthCapsuleExp;

        private static bool SameSummary(HonorLevelSummary expected, HonorLevelSummary actual)
            => expected != null && actual != null && expected.TotalHonorExp == actual.TotalHonorExp
                && expected.HonorExp == actual.HonorExp && expected.HonorLevel == actual.HonorLevel
                && expected.HonorGrade == actual.HonorGrade
                && expected.FullLevelCharacterCount == actual.FullLevelCharacterCount;

        private static bool SameSummary(GrowthCapsuleSummary expected, GrowthCapsuleSummary actual)
            => expected != null && actual != null && expected.TotalExp == actual.TotalExp
                && expected.RequiredExp == actual.RequiredExp;

        private static void Check(string label, bool passed, ref int failures)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed) failures++;
        }
    }
}
