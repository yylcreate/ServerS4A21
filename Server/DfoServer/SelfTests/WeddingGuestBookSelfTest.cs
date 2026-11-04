using DfoServer.Game.Wedding;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Wedding;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class WeddingGuestBookSelfTest
    {
        internal static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool ok)
            { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
            string path = Path.Combine(Path.GetTempPath(), "a21-wedding-book-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var text = ClientTextEncoding.GetBytes("白首不相离");
                byte[] Update(byte[] value)
                { byte[] body = new byte[8 + value.Length]; BitConverter.GetBytes(1u).CopyTo(body, 0); BitConverter.GetBytes(value.Length).CopyTo(body, 4); value.CopyTo(body, 8); return body; }
                Check("A21 update parses exact length-prefixed GBK bytes", WeddingGuestBookCommandParser.TryUpdate(Update(text), out uint pair, out var parsed)
                    && pair == 1 && parsed.SequenceEqual(text));
                Check("guest comments reject NUL, malformed GBK and unsafe native lengths", !WeddingGuestBookCommandParser.TryUpdate(Update(new byte[] { 65, 0, 66 }), out _, out _)
                    && !WeddingGuestBookCommandParser.TryUpdate(Update(new byte[] { 0x81 }), out _, out _)
                    && !WeddingGuestBookCommandParser.TryUpdate(Update(new byte[256]), out _, out _));
                byte[] loadFields = { 1, 0, 0, 0, 1 };
                byte[] deleteFields = { 1, 0, 0, 0, 2, 0, 0, 0, 1 };
                Check("guestbook manual-copy scope flag follows the opaque 14-byte prefix", WeddingGuestBookCommandParser.TryLoad(Native(loadFields), out uint loadedPair)
                    && loadedPair == 1 && !WeddingGuestBookCommandParser.TryLoad(Native(new byte[] { 1, 0, 0, 0, 0 }), out _));
                Check("native delete body retains stack prefix before pair, comment and home flag", WeddingGuestBookCommandParser.TryDelete(Native(deleteFields), out pair, out uint comment)
                    && pair == 1 && comment == 2);
                Check("fixed guestbook requests reject unprefixed legacy fields and abnormal lengths",
                    !WeddingGuestBookCommandParser.TryLoad(loadFields, out _)
                    && !WeddingGuestBookCommandParser.TryDelete(deleteFields, out _, out _)
                    && !WeddingGuestBookCommandParser.TryLoad(Native(loadFields).Concat(new byte[] { 0xEF }).ToArray(), out _)
                    && !WeddingGuestBookCommandParser.TryDelete(Native(deleteFields).Concat(new byte[] { 0xEF }).ToArray(), out _, out _)
                    && !WeddingGuestBookCommandParser.TryLoad(Native(loadFields)[..^1], out _)
                    && !WeddingGuestBookCommandParser.TryDelete(Native(deleteFields)[..^1], out _, out _));
                Check("string-written guestbook update does not accept a manual-copy stack prefix",
                    !WeddingGuestBookCommandParser.TryUpdate(Native(Update(text)), out _, out _));
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                void Sql(string sql) => db.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; cmd.ExecuteNonQuery(); });
                long Scalar(string sql) => db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Sql("INSERT INTO accounts(account_id,m_id,password_hash) VALUES(63301,'book-a',''),(63302,'book-b',''),(63303,'book-c',''),(63304,'book-d','');"
                    + "INSERT INTO characters(character_id,account_id,name,job,level) VALUES(63301,63301,'留言甲',0,60),(63302,63302,'留言乙',1,60),(63303,63303,'留言丙',0,60),(63304,63304,'留言丁',1,60);"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(1,63301,63302,4,1800000000,1800000000),(2,63303,63304,4,1800000000,1800000000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63301,1,0),(63302,1,1),(63303,2,0),(63304,2,1);");
                var book = new WeddingGuestBookRepository(db, () => 1800000000);
                var result = book.Execute(63301, 63301, 1, text, null, () => true);
                Check("married owner writes canonical GBK author and text atomically", result.Success && result.Entries.Count == 1
                    && ClientTextEncoding.GetString(result.Entries[0].AuthorName) == "留言甲" && result.Entries[0].Text.SequenceEqual(text));
                uint first = result.CommentId;
                Check("identical latest update is replayed without duplication", book.Execute(63301, 63301, 1, text, null, () => true).CommentId == first
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_comments;") == 1);
                result = book.Execute(63302, 63302, 1, null, null, () => true);
                Check("spouse reload sees authoritative shared board", result.Success && result.Entries.Count == 1 && result.Entries[0].Id == first);
                byte[] packet = WeddingGuestBookPacketBuilder.Load(result);
                Check("native load packet has 42-byte entry header followed by exact GBK text", packet.Length == 70 + text.Length && packet[15] == 1
                    && BitConverter.ToInt32(packet, 24) == 1 && BitConverter.ToUInt32(packet, 28) == first
                    && BitConverter.ToInt32(packet, 66) == text.Length && packet.AsSpan(70).SequenceEqual(text));
                Check("guest update and delete ACKs match native reader lengths", WeddingGuestBookPacketBuilder.Update(0).Length == 20
                    && WeddingGuestBookPacketBuilder.Delete(0, first).Length == 24 && WeddingGuestBookPacketBuilder.Load(new(-3)).Length == 20);
                Check("other pair cannot load this marriage's board", !book.Execute(63303, 63303, 1, null, null, () => true).Success);
                Check("delete cannot cross pair boundary", book.Execute(63303, 63303, 2, null, first, () => true).Success
                    && book.Execute(63301, 63301, 1, null, null, () => true).Entries.Count == 1);
                int checks = 0;
                Check("session replacement before commit rolls back new comment", !book.Execute(63301, 63301, 1,
                    ClientTextEncoding.GetBytes("不可保存"), null, () => ++checks == 1).Success && Scalar("SELECT COUNT(*) FROM wedding_guest_comments;") == 1);
                Sql("CREATE TRIGGER reject_guest_comment BEFORE INSERT ON wedding_guest_comments BEGIN SELECT RAISE(ABORT,'guest rollback'); END;");
                Check("SQL failure preserves existing board", !book.Execute(63301, 63301, 1,
                    ClientTextEncoding.GetBytes("失败"), null, () => true).Success && Scalar("SELECT COUNT(*) FROM wedding_guest_comments;") == 1);
                Sql("DROP TRIGGER reject_guest_comment;");
                Check("either spouse may delete a shared-board entry", book.Execute(63302, 63302, 1, null, first, () => true).Success
                    && book.Execute(63301, 63301, 1, null, null, () => true).Entries.Count == 0);
                Check("repeated delete remains an idempotent empty board", book.Execute(63302, 63302, 1, null, first, () => true).Success);
                Sql("WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<101) INSERT INTO wedding_guest_comments(pair_id,author_id,author_name,created_at,content) SELECT 1,63301,CAST('A' AS BLOB),1800000000,CAST(n AS BLOB) FROM seq;");
                result = book.Execute(63301, 63301, 1, text, null, () => true);
                Check("board storage is bounded by explicit server policy", result.Success && result.Entries.Count == WeddingGuestBookRepository.MaximumEntries
                    && Scalar("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=1;") == WeddingGuestBookRepository.MaximumEntries);
                Sql("UPDATE wedding_pairs SET status=0 WHERE pair_id=1; DELETE FROM wedding_members WHERE pair_id=1;"
                    + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(3,63301,63302,4,1800001000,1800001000);"
                    + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(63301,3,0),(63302,3,1);");
                Check("remarriage starts with a different board and rejects old pair", book.Execute(63301, 63301, 3, null, null, () => true).Entries.Count == 0
                    && !book.Execute(63301, 63301, 1, null, null, () => true).Success);
                RunArchiveTests(path + ".archive", Check);
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string database in new[] { path, path + ".archive" })
                foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(database + suffix)) File.Delete(database + suffix);
            }
            Console.WriteLine($"WEDDING_GUEST_BOOK: {passed} PASS / {failed} FAIL"); return failed == 0 ? 0 : 1;
        }
        private static void RunArchiveTests(string path, Action<string, bool> check)
        {
            var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
            void Sql(string sql) => db.Write((c, t) => { using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = sql; q.ExecuteNonQuery(); });
            long Count(string sql) => db.Read(c => { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); });
            Sql("INSERT INTO accounts(account_id,m_id) VALUES(62001,'archive-a'),(62002,'archive-b'),(62003,'archive-guest'),(62004,'archive-c'),(62005,'archive-d');"
                + "INSERT INTO characters(character_id,account_id,name) VALUES(62001,62001,'archive-a'),(62002,62002,'archive-b'),(62003,62003,'guest'),(62004,62004,'archive-c'),(62005,62005,'archive-d');"
                + "INSERT INTO wedding_pairs(pair_id,left_character_id,right_character_id,status,married_at,created_at) VALUES(1,62001,62002,4,1800002000,1800000000),(2,62004,62005,4,1800002000,1800000000);"
                + "INSERT INTO wedding_members(character_id,pair_id,seat) VALUES(62001,1,0),(62002,1,1),(62004,2,0),(62005,2,1);"
                + "INSERT INTO wedding_reservation_receipts(pair_id,ceremony_start_at,payer_character_id,gold,ticket_item_id,ticket_count,refunded) VALUES(1,1800000000,62001,0,0,0,1),(1,1800001000,62001,0,0,0,0),(2,1800001000,62004,0,0,0,0);"
                + "INSERT INTO wedding_ceremony_comments(pair_id,ceremony_start_at,author_id,author_name,created_at,content) VALUES"
                + "(1,1800000000,62003,CAST('guest' AS BLOB),1800000050,CAST('cancelled' AS BLOB)),"
                + "(1,1800001000,62003,CAST('guest' AS BLOB),1800001100,CAST('gift-blessing' AS BLOB)),"
                + "(1,1800001000,62003,CAST('guest' AS BLOB),1800001150,CAST('book-blessing' AS BLOB)),"
                + "(2,1800001000,62003,CAST('guest' AS BLOB),1800001100,CAST('other-pair' AS BLOB));"
                + "INSERT INTO wedding_guest_comments(pair_id,author_id,author_name,created_at,content) VALUES(1,62001,CAST('archive-a' AS BLOB),1800001900,CAST('home-note' AS BLOB));");
            var book = new WeddingGuestBookRepository(db);
            WeddingGuestBookResult Read(int cid = 62001, long pair = 1) => book.Execute(cid, cid, pair, null, null, () => true);
            long SourceCount() => Count("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id=1 AND ceremony_start_at=1800001000;");
            check("a non-owner cannot archive another marriage's blessings", !Read(62003).Success && SourceCount() == 2);
            Sql("CREATE TRIGGER fail_blessing_archive BEFORE INSERT ON wedding_guest_comments WHEN NEW.content=CAST('book-blessing' AS BLOB) BEGIN SELECT RAISE(ABORT,'archive insert'); END;");
            check("late archive insertion failure retains both source blessings and the existing home note",
                !Read().Success && SourceCount() == 2 && Count("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=1;") == 1);
            Sql("DROP TRIGGER fail_blessing_archive; CREATE TRIGGER fail_blessing_retire BEFORE DELETE ON wedding_ceremony_comments BEGIN SELECT RAISE(ABORT,'archive retire'); END;");
            check("source retirement failure also rolls back newly allocated home comment IDs",
                !Read().Success && SourceCount() == 2 && Count("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=1;") == 1);
            Sql("DROP TRIGGER fail_blessing_retire;");
            int currentChecks = 0;
            check("a displaced home session rolls the whole blessing archive back",
                !book.Execute(62001, 62001, 1, null, null, () => ++currentChecks == 1).Success
                && SourceCount() == 2 && Count("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=1;") == 1);
            var loaded = Read();
            check("completed marriage home board includes payment and guestbook blessings in original time order",
                loaded.Success && loaded.Entries.Select(x => ClientTextEncoding.GetString(x.Text)).SequenceEqual(new[] { "home-note", "book-blessing", "gift-blessing" })
                && loaded.Entries[1].CreatedAt == 1800001150 && loaded.Entries[1].AuthorId == 62003
                && ClientTextEncoding.GetString(loaded.Entries[1].AuthorName) == "guest"
                && loaded.Entries.Select(x => x.Id).Distinct().Count() == 3);
            check("archiving excludes cancelled ceremonies and other marriages",
                SourceCount() == 0 && Count("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE ceremony_start_at=1800000000 OR pair_id=2;") == 2);
            var ids = loaded.Entries.Select(x => x.Id).ToArray();
            var spouse = Read(62002);
            var reopened = new WeddingGuestBookRepository(new GameDatabase(path, ServerPaths.SchemaFilePath)).Execute(62001, 62001, 1, null, null, () => true);
            check("both spouses and a reopened database see the same archive without duplicate copies",
                spouse.Success && reopened.Success && spouse.Entries.Select(x => x.Id).SequenceEqual(ids)
                && reopened.Entries.Select(x => x.Id).SequenceEqual(ids) && Count("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=1;") == 3);
            uint archivedId = loaded.Entries.Single(x => ClientTextEncoding.GetString(x.Text) == "book-blessing").Id;
            var deleted = book.Execute(62002, 62002, 1, null, archivedId, () => true);
            check("archived IDs support ordinary spouse deletion without hitting a cancelled event",
                deleted.Success && deleted.Entries.Count == 2 && deleted.Entries.All(x => x.Id != archivedId)
                && Count("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id=1 AND ceremony_start_at=1800000000;") == 1);
            Sql("WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<100) INSERT INTO wedding_guest_comments(pair_id,author_id,author_name,created_at,content) SELECT 2,62004,CAST('archive-c' AS BLOB),1800000000,CAST(n AS BLOB) FROM seq;");
            var bounded = Read(62004, 2);
            check("archiving obeys the existing newest-100 policy while preserving a newer ceremony blessing",
                bounded.Success && bounded.Entries.Count == WeddingGuestBookRepository.MaximumEntries
                && ClientTextEncoding.GetString(bounded.Entries[0].Text) == "other-pair"
                && Count("SELECT COUNT(*) FROM wedding_guest_comments WHERE pair_id=2;") == 100);
            Sql("INSERT INTO wedding_reservation_receipts(pair_id,ceremony_start_at,payer_character_id,gold,ticket_item_id,ticket_count) VALUES(1,1800001500,62001,0,0,0);"
                + "INSERT INTO wedding_ceremony_comments(pair_id,ceremony_start_at,author_id,author_name,created_at,content) VALUES(1,1800001500,62003,CAST('guest' AS BLOB),1800001600,CAST('ambiguous' AS BLOB));");
            check("ambiguous unrefunded reservations fail closed instead of merging an unknown event",
                !Read().Success && Count("SELECT COUNT(*) FROM wedding_ceremony_comments WHERE pair_id=1 AND ceremony_start_at=1800001500;") == 1);
        }
        private static byte[] Native(byte[] fields) => Convert.FromHexString("9192939495969798999A9B9C9D9E").Concat(fields).ToArray();
    }
}
