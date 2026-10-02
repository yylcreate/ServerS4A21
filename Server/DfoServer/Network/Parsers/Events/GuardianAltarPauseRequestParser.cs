namespace DfoServer.Network.Parsers.Events
{
    internal readonly record struct GuardianAltarPauseCommand(bool Paused);

    internal static class GuardianAltarPauseRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarPauseCommand command)
        {
            command = default;
            // A21 sub_E0EF30: fourteen reserved zero bytes, then the desired
            // pause value. The observed request is a set, not a toggle.
            if (body == null || body.Length != 15 || body[14] > 1)
                return false;
            for (var index = 0; index < 14; index++)
                if (body[index] != 0) return false;
            command = new GuardianAltarPauseCommand(body[14] == 1);
            return true;
        }
    }
}
