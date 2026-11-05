using System;

namespace DfoServer.Network.Parsers.Wedding
{
    internal static class WeddingCommandPayload
    {
        internal const int NativeStructPrefixLength = 14;

        // Some A21 wedding senders pass their entire stack structure to
        // 274CED0, including 14 bytes before the initialized business fields.
        // These bytes remain INSIDE the body after the transport header is
        // removed. The live NPC proposal capture confirms a 21-byte body.
        // The prefix is opaque stack data, never a header or an identity.
        internal static bool TryFixed(byte[] body, int length, out ReadOnlySpan<byte> fields)
        {
            fields = body;
            if (fields.Length != NativeStructPrefixLength + length) return false;
            fields = fields[NativeStructPrefixLength..];
            return true;
        }
    }
}
