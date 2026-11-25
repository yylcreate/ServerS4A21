using DfoServer.Network;

namespace DfoServer.Network.Builders.Auction
{
    public static class AuctionServiceNotificationBuilder
    {
        // Client handler 0x01189FB0 reads `u8 serviceType, u8 state` and then forks on
        // serviceType. The two branches write DIFFERENT globals, so one notice alone
        // cannot open both windows:
        //
        //   serviceType != 1 -> 0x0118A033  call 0x02335A10(state)
        //                       0x02335A16  mov byte [0x3B71101], al
        //   serviceType == 1 -> 0x0118A025  mov byte [0x3A42F85], dl
        //
        // Those two globals are what the window entry points read:
        //   ordinary auction  0x02345AD1  cmp byte [0x3B71101], 0 / je -> refuse to open
        //   gold consignment  0x01455BE3  cmp byte [0x3A42F85], 0 / jne -> continue
        //
        // Each global has exactly ONE writer (verified by scanning the image for
        // dword references), and it is the branch above. A missing notice is therefore
        // indistinguishable from "service closed": the window never opens at all.
        // Emitting only {1, state} left 0x3B71101 at its initial 0, which broke the
        // ordinary auction window outright.
        private const byte ServiceTypeAuction = 0x00;
        private const byte ServiceTypeConsignment = 0x01;
        private const byte OpenState = 0x01;
        private const byte ClosedState = 0x00;

        /// <summary>
        /// Builds the service-state notifications sent on character select.
        ///
        /// BOTH notices are always returned: the client keeps one availability global
        /// per feature and only the matching serviceType ever writes it, so omitting
        /// either packet silently closes that window.
        ///
        /// Gold consignment has no operator switch and is always advertised open.
        /// <paramref name="auctionEnabled"/> is the ordinary auction's runtime entry
        /// state, which is true unless the PVF catalog genuinely failed to load, so
        /// that a broken catalog still tells the client the truth instead of
        /// pretending the window works.
        /// </summary>
        public static byte[][] BuildPackets(bool auctionEnabled)
        {
            return new[]
            {
                BuildPacket(ServiceTypeAuction, auctionEnabled ? OpenState : ClosedState),
                BuildPacket(ServiceTypeConsignment, OpenState)
            };
        }

        private static byte[] BuildPacket(byte serviceType, byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(serviceType);
            writer.WriteByte(state);
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.AUCTION_NOTIFY_AUCTION_SERVICE,
                writer.ToArray());
        }
    }
}
