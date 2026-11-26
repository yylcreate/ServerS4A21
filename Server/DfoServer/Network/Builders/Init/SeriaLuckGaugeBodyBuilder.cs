using System;
using DfoServer.Game.Events.SeriaLuck;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    internal sealed class SeriaLuckGaugeBodyBuilder : IInitPacketBuilder
    {
        private readonly SeriaLuckUiService _service;

        internal SeriaLuckGaugeBodyBuilder(SeriaLuckUiService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.BOOSTER_GAGE;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex, out byte[] body)
        {
            body = null;
            var initialization = snapshot?.InitializationSnapshot;
            if (initialization == null
                || !_service.TryGetGauge(initialization.SeriaLuckValue, out var gauge))
            {
                return false;
            }

            // A21 2F453C7: 13A6BB0 reads two U8 values; 13A21F0 consumes
            // before/display then after/current. Login has no pending roll.
            body = new[] { gauge, gauge };
            return true;
        }
    }
}
