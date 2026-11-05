using DfoServer.Game.Wedding;
using System;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Wedding
{
    internal static class WeddingGuestBookCommandParser
    {
        // C75630 + 274D410: pair u32 followed by a byte-counted, non-NUL GBK string.
        internal static bool TryUpdate(byte[] body, out uint pair, out byte[] text)
        {
            pair = 0; text = null; if (body == null || body.Length < 9) return false;
            pair = BinaryPrimitives.ReadUInt32LittleEndian(body);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            if (pair is 0 or uint.MaxValue || count is < 1 or > WeddingGuestBookRepository.MaximumTextBytes || count != body.Length - 8) return false;
            text = body.AsSpan(8).ToArray(); return WeddingGuestBookRepository.ValidText(text);
        }
        // C77A20 copies a 19-byte body (14 opaque + 5 fields).
        // C91590 inversion means 0=inside a chapel, 1=outside. The server
        // additionally requires the actor's own home for the outside branch.
        internal static bool TryLoad(byte[] body, out uint pair)
            => TryLoad(body, out pair, out bool home) && home;
        internal static bool TryLoad(byte[] body, out uint pair, out bool home)
        {
            pair = 0; home = false;
            if (!WeddingCommandPayload.TryFixed(body, 5, out var fields) || fields[4] > 1) return false;
            home = fields[4] == 1;
            pair = BinaryPrimitives.ReadUInt32LittleEndian(fields); return pair is > 0 and < uint.MaxValue;
        }
        // C736B0 copies a 23-byte body: 14 opaque + pair/comment/home.
        internal static bool TryDelete(byte[] body, out uint pair, out uint comment)
            => TryDelete(body, out pair, out comment, out bool home) && home;
        internal static bool TryDelete(byte[] body, out uint pair, out uint comment, out bool home)
        {
            pair = 0; comment = 0; home = false;
            if (!WeddingCommandPayload.TryFixed(body, 9, out var fields) || fields[8] > 1) return false;
            home = fields[8] == 1;
            pair = BinaryPrimitives.ReadUInt32LittleEndian(fields); comment = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
            return pair is > 0 and < uint.MaxValue && comment is > 0 and < uint.MaxValue;
        }
    }
}
