namespace DfoServer.Network.Parsers.Wedding
{
    internal static class WeddingCapsuleCommandParser
    {
        // A21 C7962F/C796A5/C93E7C construct the command header and send it
        // without writing a payload for Make, Give and Take respectively.
        internal static bool TryEmpty(byte[] body) => body == null || body.Length == 0;
    }
}
