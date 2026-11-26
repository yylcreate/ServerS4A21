using System;
using DfoServer.Game.Events.ScheduledDungeons;

namespace DfoServer.Network.Builders
{
    internal static class EventDungeonOpeningBodyBuilder
    {
        // A21 sub_1192C10 reads category/state/id/seconds as four U32s.
        // Zero seconds is only used with a closed state, never as permanence.
        internal static byte[] Build(EventDungeonOpeningSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.WireState != (snapshot.IsOpen ? 1 : 0))
                throw new ArgumentException("Activity state is inconsistent.", nameof(snapshot));
            return BuildState(snapshot.DungeonId, snapshot.WireState, snapshot.RemainingSeconds);
        }

        internal static byte[] BuildClosed(int dungeonId)
            => BuildState(dungeonId, 0, 0);

        private static byte[] BuildState(int dungeonId, int state, int seconds)
        {
            if (dungeonId != EventDungeonOpeningService.TauDungeonId
                && dungeonId != EventDungeonOpeningService.GoldSilverDungeonId)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            if (state == 1
                ? seconds <= 0 || seconds > EventDungeonOpeningService.MaximumWireSeconds
                : state != 0 || seconds != 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Invalid activity time projection.");

            var writer = new GamePacketWriter();
            writer.WriteUInt32(EventDungeonOpeningService.ScheduleCategory);
            writer.WriteUInt32((uint)state);
            writer.WriteUInt32((uint)dungeonId);
            writer.WriteUInt32((uint)seconds);
            return writer.ToArray();
        }

        // sub_971730 consumes gold used first, then silver used. The ETC owns
        // each maximum; these are not remaining counts or an activity switch.
        internal static byte[] BuildGoldSilverCounts(ushort goldUsed, ushort silverUsed)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(goldUsed);
            writer.WriteUInt16(silverUsed);
            return writer.ToArray();
        }
    }
}
