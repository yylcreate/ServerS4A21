using System;
using System.IO;
using DfoServer.Game.Tutorials;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

public static class AccountTutorialDatabaseSelfTest
{
    public static int Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "account-tutorial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0, failed = 0;
        void Check(string name, bool good) { Console.WriteLine($"[{(good ? "PASS" : "FAIL")}] {name}"); if (good) passed++; else failed++; }
        bool Refuses(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } catch (SqliteException) { return true; } }
        try
        {
            string path = Path.Combine(root, "tutorial.db");
            var database = AccountTutorialDatabase.Open(path);
            var flags = new AccountTutorialFlagStore(database);
            Check("new account starts unseen", flags.Load(1).Count == 0);
            Check("close and Finish persist the same idempotent choice", flags.Mark(1, 8) && !flags.Mark(1, 8));
            Check("reopen remembers account choice", new AccountTutorialFlagStore(AccountTutorialDatabase.Open(path)).Load(1)[0] == 8);
            Check("account choices stay isolated", flags.Load(2).Count == 0 && flags.Mark(2, 9) && flags.Load(1).Count == 1);
            Check("independent baseline is explicit", database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT baseline_id FROM feature_metadata;"; return (string)cmd.ExecuteScalar() == "a21-account-tutorial-v1"; }));
            string foreign = Path.Combine(root, "foreign.db");
            FeatureDatabaseBootstrap.Open(foreign, "different-feature-v1", "CREATE TABLE other(value TEXT);");
            Check("foreign feature database is never adopted", Refuses(() => AccountTutorialDatabase.Open(foreign)));
            string empty = Path.Combine(root, "unmarked.db"); File.WriteAllBytes(empty, Array.Empty<byte>());
            Check("preexisting unmarked file remains unchanged", Refuses(() => AccountTutorialDatabase.Open(empty)) && new FileInfo(empty).Length == 0);
            database.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "CREATE TABLE unknown(value TEXT);"; cmd.ExecuteNonQuery(); });
            Check("unknown same-version schema refused", Refuses(() => AccountTutorialDatabase.Open(path)));
            Check("schema refusal does not erase the choice", flags.Load(1)[0] == 8);
        }
        catch (Exception ex) { Check(ex.ToString(), false); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"ACCOUNT_TUTORIAL_DATABASE: {passed} PASS / {failed} FAIL");
        return failed == 0 ? 0 : 1;
    }
}
