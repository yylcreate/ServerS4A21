using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    // SQLite 版共享自测库：同一临时库跨场景复用，Reset 清空 fixture 写入的表并
    // 删除 fixture 新建的触发器，使下一个场景与独立建库等价。
    internal sealed class SelfTestSharedDatabase : IDisposable
    {
        private readonly GameDatabase _database;
        private readonly List<string> _tables;

        private SelfTestSharedDatabase(GameDatabase database, List<string> tables)
        {
            _database = database;
            _tables = tables;
        }

        public GameDatabase Database => _database;

        public static SelfTestSharedDatabase Create(string hint)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "s4a21-selftest-" + hint + "-" + Guid.NewGuid().ToString("N") + ".db");
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);

            var tables = new List<string>();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name FROM sqlite_master " +
                    "WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }

            return new SelfTestSharedDatabase(database, tables);
        }

        public void Reset()
        {
            using (var connection = _database.OpenConnection())
            {
                // 先删触发器：残留的失败注入触发器会阻断后续场景的普通写入。
                var triggers = new List<string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT name FROM sqlite_master " +
                        "WHERE type = 'trigger' AND name NOT LIKE 'sqlite_%';";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        triggers.Add(reader.GetString(0));
                }
                foreach (var trigger in triggers)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"DROP TRIGGER IF EXISTS \"{trigger}\";";
                    command.ExecuteNonQuery();
                }

                foreach (var table in _tables)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"DELETE FROM \"{table}\";";
                    command.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_database.DatabasePath);
            }
            catch (IOException)
            {
            }
        }
    }
}
