using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingGuestInvitationSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-invites-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; command.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); });
                long now = 1800000000, start = now + WeddingCatalog.Get().ReservationDelaySeconds[0];
                const int a = 64001, b = 64002, guestAccount = 64003, guestBase = 65000, channel = 1;
                byte room = WeddingSceneCatalog.Get().CeremonyRooms[0].Area;
                int quota = WeddingCatalog.Get().Ceremonies[0].Invitations;
                Check("current PVF supplies positive per-host invitation capacity", quota > 0 && quota <= ushort.MaxValue);
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(64001,'guest-host-a',''),(64002,'guest-host-b',''),(64003,'guest-targets','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(64001,64001,'新人甲',0,60),(64002,64002,'新人乙',1,60);");
                for (int i = 0; i < quota * 3 + 8; i++)
                    Sql($"INSERT INTO characters(character_id,account_id,name,job,level) VALUES({guestBase + i},{guestAccount},'宾客{i}',1,1);");
                long pair = db.Write((c, t) =>
                {
                    var state = WeddingRepository.Create(c, t, a, b, now);
                    if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Reserved, StartAt = start, Room = room, ChannelId = channel }))
                        throw new InvalidOperationException("Invitation fixture reservation failed.");
                    return state.PairId;
                });
                var repository = new WeddingGuestInvitationRepository(db, () => now);
                WeddingGuestInvitationResult Invite(int host, params int[] targets) => repository.Invite(host, host, pair, start, targets, () => true);
                var first = Invite(a, guestBase);
                Check("invitation is durable and uses canonical pair context", first.Success && first.Added == 1 && first.Host.Remaining == quota - 1
                    && first.Invitations.Count == 1 && first.Invitations[0].LeftCharacterId == a && first.Invitations[0].RightCharacterId == b
                    && first.Invitations[0].State.Room == room && first.Invitations[0].State.ChannelId == channel
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1);
                Check("guest invitation does not invent the marriage level requirement", repository.LoadForGuest(guestBase, now).Count == 1);
                var repeated = Invite(a, guestBase, guestBase);
                Check("replayed and duplicate batch targets do not consume extra invitations", repeated.Success && repeated.Added == 0
                    && repeated.Host.Remaining == quota - 1 && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1);
                var shared = Invite(b, guestBase);
                Check("the other host cannot steal an existing invitation or spend quota twice", shared.Success && shared.Added == 0
                    && shared.Host.Remaining == quota && shared.Invitations[0].InviterCharacterId == a);
                Check("self and spouse targets are rejected", !Invite(a, a).Success && !Invite(a, b).Success);
                Check("host account is verified inside the invitation transaction", !repository.Invite(a, guestAccount, pair, start,
                    new[] { guestBase + 1 }, () => true).Success && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1);
                Check("old session is rejected without consuming capacity", repository.Invite(a, a, pair, start,
                    new[] { guestBase + 1 }, () => false).Error == -101 && repository.LoadForHost(a, now).Remaining == quota - 1);
                int checks = 0;
                var changed = repository.Invite(a, a, pair, start, new[] { guestBase + 1 }, () => ++checks == 1);
                Check("a generation change after insertion rolls back the invitation and quota", !changed.Success
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1 && repository.LoadForHost(a, now).Remaining == quota - 1);
                Sql($"CREATE TRIGGER reject_second_guest BEFORE INSERT ON wedding_guest_invitations WHEN NEW.guest_character_id={guestBase + 2} BEGIN SELECT RAISE(ABORT,'guest transaction rollback'); END;");
                var failedBatch = Invite(a, guestBase + 1, guestBase + 2);
                Check("late batch SQL failure rolls back every guest and all capacity", !failedBatch.Success
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1 && repository.LoadForHost(a, now).Remaining == quota - 1);
                Sql("DROP TRIGGER reject_second_guest;");
                Sql($"UPDATE characters SET delete_flag=1 WHERE character_id={guestBase + quota * 3 + 7};");
                Check("a deleted target rejects the whole requested batch", !Invite(a, guestBase + 1, guestBase + quota * 3 + 7).Success
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_invitations;") == 1);
                if (quota > 1) Check("host can spend exactly its configured remaining quota", Invite(a,
                    Enumerable.Range(guestBase + 1, quota - 1).ToArray()).Success && repository.LoadForHost(a, now).Remaining == 0);
                Check("one additional unique target cannot exceed the first host's quota", Invite(a, guestBase + quota).Error == -6
                    && repository.LoadForHost(a, now).Remaining == 0);
                var secondHost = Invite(b, guestBase + quota);
                Check("the second host owns its independent half of the PVF capacity", secondHost.Success && secondHost.Added == 1
                    && secondHost.Host.Remaining == quota - 1 && repository.LoadForHost(a, now).Remaining == 0
                    && repository.LoadForHost(a, now).Entries.Count == quota + 1);
                Check("a full host can replay the other host's existing target without spending capacity", Invite(a, guestBase + quota).Success
                    && repository.LoadForHost(a, now).Remaining == 0);
                Sql($"UPDATE characters SET delete_flag=1 WHERE character_id={guestBase};");
                Check("deleted guests cannot load invitations and do not refund previously spent quota", repository.LoadForGuest(guestBase, now).Count == 0
                    && repository.LoadForHost(a, now).Remaining == 0);
                Sql($"UPDATE characters SET delete_flag=0 WHERE character_id={guestBase};");
                Check("church invitation authority validates account, event, channel and room", repository.FindForChurch(guestBase, guestAccount, pair, start, channel, room, now) != null
                    && repository.FindForChurch(guestBase, a, pair, start, channel, room, now) == null
                    && repository.FindForChurch(guestBase, guestAccount, pair, start + 1, channel, room, now) == null
                    && repository.FindForChurch(guestBase, guestAccount, pair, start, channel + 1, room, now) == null);
                Check("area lookup selects the authoritative active event and rejects unreserved room 63", repository.FindForArea(guestBase, guestAccount, channel, room, now) != null
                    && repository.FindForArea(guestBase, guestAccount, channel, 63, now) == null
                    && repository.FindForArea(guestBase, guestAccount, channel + 1, room, now) == null);
                Check("financial authority can deny entry in the same invitation read snapshot", repository.FindForArea(guestBase, guestAccount, channel, room, now,
                    additionalAuthority: (_, _, _) => false) == null);
                now = start;
                Check("invitation and new entry both close exactly at the start deadline", !Invite(b, guestBase + quota + 1).Success
                    && repository.FindForChurch(guestBase, guestAccount, pair, start, channel, room, now) == null
                    && repository.LoadForGuest(guestBase, now).Count == 0);
                db.Write((c, t) => { var state = WeddingGuestInvitationRepository.LoadEvent(c, t, pair, start);
                    if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Ceremony })) throw new InvalidOperationException(); });
                now++;
                Check("ongoing ceremony scope is distinct from new church entry", repository.LoadForGuest(guestBase, now).Count == 1
                    && repository.FindForChurch(guestBase, guestAccount, pair, start, channel, room, now) == null
                    && repository.FindForArea(guestBase, guestAccount, channel, room, now) == null
                    && repository.FindForArea(guestBase, guestAccount, channel, room, now, allowActiveCeremony: true) != null);
                Check("host query publishes zero send capacity once the ceremony starts", repository.LoadForHost(a, now).Remaining == 0);
                now = start + WeddingCatalog.Get().CeremonyEndSeconds;
                Check("expired event cannot authorize guest scope even before recovery executes", repository.LoadForGuest(guestBase, now).Count == 0
                    && repository.FindForArea(guestBase, guestAccount, channel, room, now, allowActiveCeremony: true) == null);
                long oldStart = start;
                db.Write((c, t) => { var state = WeddingGuestInvitationRepository.LoadEvent(c, t, pair, oldStart);
                    if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Engaged, StartAt = 0, Room = 0 })) throw new InvalidOperationException(); });
                Check("cancellation immediately invalidates old letters and event context", repository.LoadForGuest(guestBase, now).Count == 0
                    && repository.LoadEvent(pair, oldStart) == null);
                start = now + WeddingCatalog.Get().ReservationDelaySeconds[0];
                db.Write((c, t) => { var state = WeddingRepository.Load(c, t, a);
                    if (!WeddingRepository.Save(c, t, state, state with { Status = WeddingStatus.Reserved, StartAt = start, Room = room, ChannelId = channel })) throw new InvalidOperationException(); });
                var next = Invite(a, guestBase);
                Check("rebooking the same pair creates a new invitation identity and fresh host quota", next.Success && next.Added == 1
                    && next.Host.Remaining == quota - 1 && next.Invitations[0].StartAt == start
                    && repository.LoadForGuest(guestBase, now).Count == 1
                    && Scalar($"SELECT COUNT(*) FROM wedding_guest_invitations WHERE pair_id={pair} AND guest_character_id={guestBase};") == 2);
                Check("old callbacks cannot invite into or enter a later reservation", !repository.Invite(a, a, pair, oldStart,
                    new[] { guestBase + quota + 2 }, () => true).Success
                    && repository.FindForChurch(guestBase, guestAccount, pair, oldStart, channel, room, now) == null);
                Sql($"UPDATE characters SET delete_flag=1 WHERE character_id={b};");
                Check("deleted hosts disable invitations and guest entry", !Invite(a, guestBase + 1).Success
                    && repository.LoadForGuest(guestBase, now).Count == 0 && repository.FindForArea(guestBase, guestAccount, channel, room, now) == null);
                Sql($"UPDATE characters SET delete_flag=0 WHERE character_id={b}; DELETE FROM wedding_members WHERE character_id={b};");
                Check("broken reciprocal membership cannot publish or authorize letters", repository.LoadEvent(pair, start) == null
                    && repository.LoadForHost(a, now).Entries.Count == 0 && repository.LoadForGuest(guestBase, now).Count == 0);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"WEDDING_GUEST_INVITATION: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }
    }
}
