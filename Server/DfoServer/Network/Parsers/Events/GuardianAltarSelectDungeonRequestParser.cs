using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Events
{
    internal readonly record struct GuardianAltarSelectDungeonCommand(int DungeonId, byte Difficulty,
        bool SurvivalSelector);

    internal static class GuardianAltarSelectDungeonRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarSelectDungeonCommand command)
        {
            command = default;
            // Current captures distinguish ordinary 15B (two FF bytes)
            // from survival 17B (four FF bytes). The latter is not a generic
            // extension; admission must match its typed PVF stage mode.
            if (body == null || body.Length is not (15 or 17) || body[4] > 2 || body[5] != 3
                || body[6] != 0)
                return false;
            var survival = body.Length == 17;
            if (survival && body[4] != 0) return false;
            var sentinelEnd = survival ? 11 : 9;
            for (var i = 7; i < sentinelEnd; i++)
                if (body[i] != byte.MaxValue) return false;
            for (var i = sentinelEnd; i < body.Length; i++)
                if (body[i] != 0) return false;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (id == 0 || id > short.MaxValue) return false;
            command = new GuardianAltarSelectDungeonCommand((int)id, body[4], survival);
            return true;
        }
    }
}
