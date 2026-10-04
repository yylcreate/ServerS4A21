namespace DfoServer.Network.Parsers.Events
{
    internal static class FatigueAccelerationRequestParser
    {
        internal static bool TryParse(byte[] body, out bool enable)
        {
            enable = false;
            if (body == null || body.Length != 1 || body[0] > 1)
                return false;
            enable = body[0] == 1;
            return true;
        }
    }
}
