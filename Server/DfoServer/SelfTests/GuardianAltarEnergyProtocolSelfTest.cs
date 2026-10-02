using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Events;
using PvfLib;

namespace DfoServer.SelfTests
{
    internal static class GuardianAltarEnergyProtocolSelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== GUARDIAN_ALTAR_ENERGY_PROTOCOL selftest ===");
            var failures = 0;
            var assertions = 0;
            void Check(string label, bool success)
            {
                assertions++;
                Console.WriteLine($"[{(success ? "PASS" : "FAIL")}] {label}");
                if (!success)
                    failures++;
            }
            void Section(string label, Action action)
            {
                try { action(); }
                catch (Exception exception) { Check(label + " exception: " + exception, false); }
            }

            Section("current PVF energy and shared projection", () => CheckRealResources(Check));
            Section("resource-owned row count and fixed units", () => CheckValidEnergy(Check));
            Section("energy fail-closed validation", () => CheckInvalidEnergy(Check));
            Section("A21 pause request validation", () => CheckPauseRequests(Check));
            Section("A21 pause acknowledgement consumption", () => CheckPauseAcknowledgements(Check));
            Console.WriteLine($"GUARDIAN_ALTAR_ENERGY_PROTOCOL assertions={assertions} failures={failures}");
            return failures;
        }

        private static void CheckRealResources(Action<string, bool> check)
        {
            // Current resources are required; synthetic tables below must not
            // become a fallback for missing or incompatible real resources.
            var archivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException(
                    "Set PVF_ARCHIVE_PATH to the current PVF before running this selftest.", archivePath);
            using var archive = PvfArchive.Open(archivePath);
            var configText = ReadRequired(archive, "etc/advancealtar/advancealtarconfig.etc");
            var shopText = ReadRequired(archive, "etc/advancealtar/advancealtarshop.etc");
            var direct = GuardianAltarEnergyConfigFile.Parse(configText);
            var expected = new[]
            {
                (Level: 0, Capacity: 10, CapacityFixed: 10000, Regeneration: 1000),
                (Level: 1, Capacity: 30, CapacityFixed: 30000, Regeneration: 1100),
                (Level: 2, Capacity: 70, CapacityFixed: 70000, Regeneration: 1750),
                (Level: 3, Capacity: 150, CapacityFixed: 150000, Regeneration: 2500)
            };
            check("current energy resource is valid with fixed-unit scale 1000",
                direct.IsValid && direct.InvalidReason.Length == 0 && direct.Scale == 1000);
            check("current energy resource retains all four source capacities and regeneration rows",
                EnergyRows(direct).SequenceEqual(expected));

            var shared = GuardianAltarConfigFile.Parse(configText, shopText);
            check("current shared config and shop are valid", shared.IsValid);
            if (!shared.IsValid)
                throw new InvalidDataException(shared.InvalidReason);
            var root = shared.Root;
            var content = shared.Content;
            var originalTree = CaptureTree(root);
            var ridables = shared.Ridables;
            var diagnostics = shared.Diagnostics;
            var dailyTickets = shared.DailyRefillTicket;
            var projected = GuardianAltarEnergyConfigFile.Parse(shared);
            var repeated = GuardianAltarEnergyConfigFile.Parse(shared);
            check("energy projections preserve the shared Root and Content references",
                ReferenceEquals(projected.Root, root) && ReferenceEquals(projected.Content, content)
                && ReferenceEquals(repeated.Root, root) && ReferenceEquals(repeated.Content, content)
                && ReferenceEquals(shared.Root, root) && ReferenceEquals(shared.Content, content));
            check("energy projections preserve all shared node, list and data-item identities and positions",
                originalTree.All(x => x.IsUnchanged()));
            check("energy projections leave refill, shop and diagnostic ownership unchanged",
                shared.DailyRefillTicket == dailyTickets && ReferenceEquals(shared.Ridables, ridables)
                && ReferenceEquals(shared.Diagnostics, diagnostics) && shared.IsValid);
            check("direct and repeated shared energy projections agree",
                projected.IsValid && repeated.IsValid
                && projected.Scale == direct.Scale && repeated.Scale == direct.Scale
                && EnergyRows(projected).SequenceEqual(expected)
                && EnergyRows(repeated).SequenceEqual(expected));
            check("energy validation leaves the PVF archive read-only", !archive.HasModifications);
        }

        private static void CheckValidEnergy(Action<string, bool> check)
        {
            var cases = new[]
            {
                (Name: "one level", Rows: new[] { (0, 3, 7) }),
                (Name: "three levels", Rows: new[] { (0, 4, 5), (1, 8, 6), (2, 12, 9) }),
                (Name: "five levels", Rows: new[]
                    { (0, 2, 3), (1, 4, 4), (2, 6, 5), (3, 8, 6), (4, 10, 7) })
            };
            foreach (var test in cases)
            {
                var text = EnergyTable(string.Join("\n", test.Rows.Select(
                    row => $"{row.Item1} {row.Item2} {row.Item3}")));
                var parsed = GuardianAltarEnergyConfigFile.Parse(text);
                var scale = test.Rows[0].Item3;
                check(test.Name + " retains the resource row count and initial-row scale",
                    parsed.IsValid && parsed.InvalidReason.Length == 0
                    && parsed.Scale == scale && parsed.Levels.Count == test.Rows.Length);
                check(test.Name + " converts capacities while retaining regeneration fixed units",
                    EnergyRows(parsed).SequenceEqual(test.Rows.Select(row =>
                        (row.Item1, row.Item2, row.Item2 * scale, row.Item3))));
            }

            var commented = GuardianAltarEnergyConfigFile.Parse(EnergyTable(
                "# source comment\n0\t3 7 # initial row\n1 9\n11\n"));
            check("energy rows tolerate source comments, tabs and line breaks",
                commented.IsValid && commented.Scale == 7 && EnergyRows(commented).SequenceEqual(
                    new[] { (0, 3, 21, 7), (1, 9, 63, 11) }));
            var unrelated = GuardianAltarEnergyConfigFile.Parse(
                "[gage upgrade cost percent]\n0.8\n[/gage upgrade cost percent]\n"
                + EnergyTable("0 3 7"));
            check("energy projection does not interpret the separate upgrade percentage",
                unrelated.IsValid && unrelated.Scale == 7
                && EnergyRows(unrelated).SequenceEqual(new[] { (0, 3, 21, 7) }));
            var unrelatedInvalidConfig = GuardianAltarConfigFile.Parse(
                EnergyTable("0 3 7"), "[unsupported]\n0\n");
            var selectedSection = GuardianAltarEnergyConfigFile.Parse(unrelatedInvalidConfig);
            check("energy projection validates its own section without claiming other config sections are valid",
                !unrelatedInvalidConfig.IsValid && selectedSection.IsValid
                && ReferenceEquals(selectedSection.Root, unrelatedInvalidConfig.Root)
                && ReferenceEquals(selectedSection.Content, unrelatedInvalidConfig.Content)
                && EnergyRows(selectedSection).SequenceEqual(new[] { (0, 3, 21, 7) }));
        }

        private static void CheckInvalidEnergy(Action<string, bool> check)
        {
            var cases = new (string Name, string Text)[]
            {
                ("null text", null),
                ("empty text", string.Empty),
                ("whitespace text", " \t\r\n"),
                ("missing energy tag", "[daily refill ticket]\n3\n[/refill ticket]\n"),
                ("empty energy table", EnergyTable(string.Empty)),
                ("duplicate energy sections", EnergyTable("0 3 7") + EnergyTable("0 3 7")),
                ("duplicate level", EnergyTable("0 3 7 0 5 9")),
                ("initial level one", EnergyTable("1 3 7")),
                ("level gap", EnergyTable("0 3 7 2 5 9")),
                ("reordered levels", EnergyTable("1 5 9 0 3 7")),
                ("negative level", EnergyTable("-1 3 7")),
                ("zero capacity", EnergyTable("0 0 7")),
                ("negative capacity", EnergyTable("0 -3 7")),
                ("zero regeneration", EnergyTable("0 3 0")),
                ("negative regeneration", EnergyTable("0 3 -7")),
                ("decimal level", EnergyTable("0.0 3 7")),
                ("decimal capacity", EnergyTable("0 3.5 7")),
                ("decimal regeneration", EnergyTable("0 3 7.5")),
                ("quoted integer", EnergyTable("0 `3` 7")),
                ("invalid integer token", EnergyTable("0 capacity 7")),
                ("unclosed quoted token", EnergyTable("0 `3 7")),
                ("integer overflow", EnergyTable("0 2147483648 7")),
                ("regeneration overflow", EnergyTable("0 3 2147483648")),
                ("partial first triple", EnergyTable("0 3")),
                ("partial later triple", EnergyTable("0 3 7 1 5")),
                ("unclosed energy section", "[gage up rate]\n0 3 7\n"),
                ("wrong energy closing tag", "[gage up rate]\n0 3 7\n[/gage]\n"),
                ("stray closing tag inside energy", "[gage up rate]\n0 3 7\n[/other]\n[/gage up rate]\n"),
                ("nested energy data", "[gage up rate]\n[values]\n0 3 7\n[/values]\n[/gage up rate]\n"),
                ("unbalanced nested energy", "[gage up rate]\n[values]\n0 3 7\n[/gage up rate]\n"),
                ("malformed late row", EnergyTable("0 3 7 1 5 9 2 bad 11")),
                ("negative late capacity", EnergyTable("0 3 7 1 5 9 2 -7 11")),
                ("first fixed capacity overflow", EnergyTable("0 2147483647 2")),
                ("late fixed capacity overflow", EnergyTable("0 3 7 1 2147483647 9")),
                ("scale product overflow", EnergyTable("0 2 2147483647"))
            };
            foreach (var test in cases)
                CheckFailedEnergy(check, test.Name, GuardianAltarEnergyConfigFile.Parse(test.Text));
            CheckFailedEnergy(check, "null shared config",
                GuardianAltarEnergyConfigFile.Parse((GuardianAltarConfigFile)null));
            CheckFailedEnergy(check, "empty shared config",
                GuardianAltarEnergyConfigFile.Parse(new GuardianAltarConfigFile()));
            CheckFailedEnergy(check, "invalid empty shared config",
                GuardianAltarEnergyConfigFile.Parse(GuardianAltarConfigFile.Parse(string.Empty, string.Empty)));
        }

        private static void CheckFailedEnergy(Action<string, bool> check, string label,
            GuardianAltarEnergyConfigFile parsed) =>
            check(label + " rejects without publishing partial energy rows or a scale",
                !parsed.IsValid && !string.IsNullOrWhiteSpace(parsed.InvalidReason)
                && parsed.Scale == 0 && parsed.Levels.Count == 0);

        private static void CheckPauseRequests(Action<string, bool> check)
        {
            foreach (var paused in new[] { false, true })
            {
                var body = new byte[15];
                body[14] = paused ? (byte)1 : (byte)0;
                var original = body.ToArray();
                check($"A21 pause={paused} request reads the desired value and preserves the body",
                    GuardianAltarPauseRequestParser.TryParse(body, out var command)
                    && command.Paused == paused && body.SequenceEqual(original));
            }
            CheckRejectedPause(check, "null body", null);
            foreach (var length in new[] { 0, 1, 12, 14, 16, 1024 })
                CheckRejectedPause(check, $"body length {length}", new byte[length]);
            for (var index = 0; index < 14; index++)
                foreach (var value in new byte[] { 1, 255 })
                {
                    var body = new byte[15];
                    body[index] = value;
                    body[14] = 1;
                    CheckRejectedPause(check, $"reserved offset {index}={value}", body);
                }
            foreach (var value in new byte[] { 2, 255 })
            {
                var body = new byte[15];
                body[14] = value;
                CheckRejectedPause(check, $"non-boolean pause value {value}", body);
            }
        }

        private static void CheckRejectedPause(Action<string, bool> check, string label, byte[] body)
        {
            var original = body?.ToArray();
            check(label + " rejects with a default command and unchanged request",
                !GuardianAltarPauseRequestParser.TryParse(body, out var command)
                && command == default && (body == null || body.SequenceEqual(original)));
        }

        private static void CheckPauseAcknowledgements(Action<string, bool> check)
        {
            foreach (var success in new[] { false, true })
                foreach (var paused in new[] { false, true })
                {
                    var body = GuardianAltarPauseAckBodyBuilder.Build(success, paused);
                    var expected = new byte[]
                        { success ? (byte)1 : (byte)0, 0, 0, 0, 0, paused ? (byte)1 : (byte)0 };
                    check($"pause acknowledgement success={success}, paused={paused} has exactly six A21 bytes",
                        body.SequenceEqual(expected));
                    // sub_1106160 reads the inner Int32 and boolean even when
                    // the outer success byte is zero. Decode unconditionally.
                    using var stream = new MemoryStream(body, writable: false);
                    using var reader = new BinaryReader(stream);
                    var outer = reader.ReadByte();
                    var result = reader.ReadInt32();
                    var pause = reader.ReadByte();
                    check($"pause acknowledgement success={success}, paused={paused} supports all bounded consumer reads",
                        outer == expected[0] && result == 0 && pause == expected[5]
                        && stream.Position == stream.Length);
                    if (!success)
                        check($"failed pause={paused} acknowledgement retains the five-byte inner structure",
                            body.Length == 6 && body.Length - 1 == sizeof(int) + sizeof(byte));
                }
        }

        private static string EnergyTable(string rows) =>
            "[gage up rate]\n" + rows + "\n[/gage up rate]\n";

        private static IEnumerable<(int Level, int Capacity, int CapacityFixed, int Regeneration)>
            EnergyRows(GuardianAltarEnergyConfigFile parsed) =>
            parsed.Levels.Select(x => (x.Level, x.Capacity, x.CapacityFixed, x.RegenerationFixedPerTick));

        private static string ReadRequired(PvfArchive archive, string path)
        {
            var content = archive.GetFileContent(path);
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("Required Guardian Altar resource is empty: " + path);
            return content;
        }

        private static List<NodeSnapshot> CaptureTree(ScriptNode root)
        {
            var snapshots = new List<NodeSnapshot>();
            void Capture(ScriptNode node)
            {
                snapshots.Add(new NodeSnapshot(node));
                foreach (var child in node.Children)
                    Capture(child);
            }
            Capture(root);
            return snapshots;
        }

        private sealed class NodeSnapshot
        {
            private readonly ScriptNode _node;
            private readonly string _tag;
            private readonly (int Start, int End, int StartLine, int EndLine, bool Closed) _positions;
            private readonly List<ScriptNode> _children;
            private readonly List<ScriptDataItem> _data;
            private readonly ScriptNode[] _childRows;
            private readonly (ScriptDataItem Item, int Start, int End, int Line)[] _dataRows;

            internal NodeSnapshot(ScriptNode node)
            {
                _node = node;
                _tag = node.Tag;
                _positions = (node.StartIndex, node.EndIndex, node.StartLineIndex,
                    node.EndLineIndex, node.HasEndTag);
                _children = node.Children;
                _data = node.DataItems;
                _childRows = node.Children.ToArray();
                _dataRows = node.DataItems.Select(x => (x, x.StartIndex, x.EndIndex, x.LineIndex)).ToArray();
            }

            internal bool IsUnchanged() =>
                ReferenceEquals(_node.Tag, _tag)
                && _positions == (_node.StartIndex, _node.EndIndex, _node.StartLineIndex,
                    _node.EndLineIndex, _node.HasEndTag)
                && ReferenceEquals(_node.Children, _children) && ReferenceEquals(_node.DataItems, _data)
                && _node.Children.SequenceEqual(_childRows)
                && _node.DataItems.Select(x => (x, x.StartIndex, x.EndIndex, x.LineIndex)).SequenceEqual(_dataRows);
        }
    }
}
