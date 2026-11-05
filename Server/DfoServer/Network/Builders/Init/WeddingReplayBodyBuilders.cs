using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Wedding;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class WeddingInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.WEDDING_INFO;

        // A21's unbound pair/ring sentinels; never publish a fictional marriage.
        public static byte[] BuildBody()
        {
            return WeddingPacketBuilder.StateBody(WeddingState.Empty(0));
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = WeddingPacketBuilder.StateBody(snapshot?.Wedding ?? WeddingState.Empty(snapshot?.CharacterRecord?.CharacterId ?? 0));
            return true;
        }
    }

    public static class CoupleRoomBodyBuilder
    {
        public static byte[] BuildBody()
        {
            return WeddingRoomPacketBuilder.StateBody(new WeddingRoomState());
        }
    }
}
