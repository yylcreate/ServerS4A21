using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Events
{
    internal static class GuardianAltarAchievementRequestParser
    {
        internal static bool TryParse(byte[] body, out int achievementType)
        {
            achievementType = 0;
            // Current A21 clicks carry a 14B zero prefix followed by the
            // resource achievement type, not a ridable or a client reward list.
            if (body == null || body.Length != 18)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0)
                    return false;
            achievementType = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14, 4));
            return achievementType > 0;
        }
    }
}
