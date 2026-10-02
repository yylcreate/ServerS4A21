namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarPauseAckBodyBuilder
    {
        internal static byte[] Build(bool success, bool paused)
        {
            // A21 sub_1106160 always reads the five internal bytes, even when
            // transport success is false. A common two-byte error is too short.
            // The internal Int32 is ignored by this consumer; keep it zero,
            // rather than inventing an unverified Guardian result code.
            var writer = new GamePacketWriter();
            writer.WriteByte(success ? (byte)1 : (byte)0);
            writer.WriteInt32(0);
            writer.WriteByte(paused ? (byte)1 : (byte)0);
            return writer.ToArray();
        }
    }
}
