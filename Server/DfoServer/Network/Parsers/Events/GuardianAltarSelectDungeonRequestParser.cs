using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Events
{
    internal readonly record struct GuardianAltarSelectDungeonCommand(int DungeonId, byte Difficulty);

    internal static class GuardianAltarSelectDungeonRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarSelectDungeonCommand command)
        {
            command = default;
            // Current A21 menu Start sends 15 bytes, not the ordinary hell
            // selection meaning of flags. Require its observed source shape.
            if (body == null || body.Length != 15 || body[4] > 2 || body[5] != 3
                || body[6] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7)) != ushort.MaxValue)
                return false;
            for (var i = 9; i < body.Length; i++)
                if (body[i] != 0) return false;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (id == 0 || id > short.MaxValue) return false;
            command = new GuardianAltarSelectDungeonCommand((int)id, body[4]);
            return true;
        }
    }
}
