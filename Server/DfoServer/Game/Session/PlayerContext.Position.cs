using System;
using System.Threading;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        private int _townPresenceReady = 1;
        private int _dungeonSelectionPending;
        private long _townMovementSequence;

        public byte CurTownId { get; set; }
        internal DfoServer.Game.Wedding.WeddingState Wedding { get; set; }
        internal long WeddingRoomPairId { get; set; }
        private WeddingCeremonyAttendance _weddingCeremony;
        internal WeddingCeremonyAttendance WeddingCeremony => Volatile.Read(ref _weddingCeremony);
        internal long WeddingCeremonyPairId => WeddingCeremony?.PairId ?? 0;
        internal long WeddingCeremonyStartAt => WeddingCeremony?.StartAt ?? 0;
        private WeddingSceneEntry _weddingAreaAdmission;
        internal WeddingSceneEntry WeddingAreaAdmission
        {
            get => Volatile.Read(ref _weddingAreaAdmission);
            set => Volatile.Write(ref _weddingAreaAdmission, value);
        }

        internal void BindWeddingCeremony(long pairId, long startAt)
        {
            if (pairId <= 0 || startAt <= 0) throw new ArgumentOutOfRangeException(nameof(pairId));
            Volatile.Write(ref _weddingCeremony, new WeddingCeremonyAttendance(pairId, startAt));
        }
        internal void ClearWeddingCeremony() => Volatile.Write(ref _weddingCeremony, null);
        public byte CurAreaId { get; set; }
        public short CurPosX { get; set; }
        public short CurPosY { get; set; }
        public byte CurDirection { get; set; } = 0x05;
        public byte CurAreaState { get; set; } = 0x03;

        // Kept separate from the protocol-level UserState. A replacement
        // session is published to the character directory before asynchronous
        // teardown of the old generation completes, but must not enter a town
        // roster until the old avatar's USER_LEAVE has been delivered.
        public bool TownPresenceReady
        {
            get => Volatile.Read(ref _townPresenceReady) != 0;
            set => Volatile.Write(
                ref _townPresenceReady, value ? 1 : 0);
        }

        // Party dungeon selection keeps UserState at town until every member
        // has received the mandatory shared-roster packets. This independent
        // gate prevents a queued town-arrival task from republishing a member
        // during that window.
        public bool DungeonSelectionPending
        {
            get => Volatile.Read(ref _dungeonSelectionPending) != 0;
            internal set => Volatile.Write(
                ref _dungeonSelectionPending, value ? 1 : 0);
        }

        internal long NextTownMovementSequence()
            => Interlocked.Increment(ref _townMovementSequence);

        internal long LatestTownMovementSequence
            => Interlocked.Read(ref _townMovementSequence);

        internal void ResetTownMovementSequence()
            => Interlocked.Exchange(ref _townMovementSequence, 0);

        /// <summary>
        /// </summary>
        public DateTime LastPositionPersistAt { get; set; } = DateTime.MinValue;
    }

    // Both values form one identity: a failed ceremony may reserve again with
    // the same pair and physical church room but a different start time.
    internal sealed record WeddingCeremonyAttendance(long PairId, long StartAt);
    internal sealed record WeddingSceneEntry(int CharacterId, byte Town, byte Area,
        long HomePairId, WeddingCeremonyAttendance Ceremony, DfoServer.Game.Wedding.WeddingState HomeState = null);
}
