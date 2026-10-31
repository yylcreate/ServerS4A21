using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class AuctionServiceInitBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.AUCTION_NOTIFY_AUCTION_SERVICE;
        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = null;
            if (occurrenceIndex is < 0 or > 1) return false;
            body = new byte[] { checked((byte)occurrenceIndex), 1 };
            return true;
        }
    }
}
