using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Parsers.Events
{
    internal static class GuardianAltarStartGameRequestParser
    {
        internal static bool TryParse(byte[] body, out GuardianAltarStartGameCommand command)
        {
            command = default;
            // Current A21 live requests contain only the C2S header. Do not
            // reinterpret that header as body, or accept unproven tail bytes.
            return body == null || body.Length == 0;
        }
    }
}
