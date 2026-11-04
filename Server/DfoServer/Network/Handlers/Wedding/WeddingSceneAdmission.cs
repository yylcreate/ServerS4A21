using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System.Linq;
using System;

namespace DfoServer.Network.Handlers
{
    // Private scenes share PVF coordinates, not their occupants. Check the
    // durable marriage for admission; the pair on PlayerContext is only a
    // projection key for area fanout, never permission to mutate assets.
    internal sealed class WeddingSceneAdmission
    {
        private readonly WeddingRepository _weddings;
        private readonly ISessionDirectory _sessions;
        private readonly WeddingGuestInvitationRepository _guests;
        internal WeddingSceneAdmission(IGameDatabase database, ISessionDirectory sessions)
        { _weddings = new WeddingRepository(database); _sessions = sessions; _guests = new(database); }

        internal bool CanEnter(EnhancedClientSession session, byte town, byte area)
        {
            var player = session?.Player;
            if (player == null) return false;
            player.WeddingAreaAdmission = FindEntry(session, town, area);
            return player.WeddingAreaAdmission != null;
        }

        private WeddingSceneEntry FindEntry(EnhancedClientSession session, byte town, byte area)
        {
            var scenes = WeddingSceneCatalog.Get();
            bool home = scenes.CoupleRoom.Town == town && scenes.CoupleRoom.Area == area;
            bool church = scenes.ChurchScenes.Any(x => x.Town == town && x.Area == area);
            var player = session?.Player;
            if (player == null || !_sessions.TryGet(player.CharacterId, out var current)
                || !ReferenceEquals(current, session)) return null;
            if (!home && !church)
                return new(player.CharacterId, town, area, 0, null);
            var state = _weddings.Load(player.CharacterId);
            if (home)
            {
                if (state.Status != WeddingStatus.Married || state.PartnerId <= 0
                    || !WeddingRules.IsReciprocal(state, _weddings.Load(state.PartnerId))) return null;
                return new(player.CharacterId, town, area, state.PairId, null, state);
            }
            int channel = GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var endpoint) ? endpoint.ChannelId : 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool alreadyInside = player.CurTownId == town && player.CurAreaId == area;
            if (state.PartnerId > 0 && WeddingRules.IsReciprocal(state, _weddings.Load(state.PartnerId))
                && state.Room == area && state.ChannelId == channel
                && (state.Status == WeddingStatus.Reserved && now < state.StartAt
                    || alreadyInside && state.Status == WeddingStatus.Ceremony
                        && IsCeremony(player, state.PairId, state.StartAt)
                        && now < checked(state.StartAt + WeddingCatalog.Get().CeremonyEndSeconds)))
            {
                return new(player.CharacterId, town, area, 0, new(state.PairId, state.StartAt));
            }
            // Invitation and paid receipt are read in one snapshot. The native
            // guest button enforces level 17 independently of the host's level 30.
            var invitation = player.Level < 17 ? null : _guests.FindForArea(player.CharacterId, session.Account?.AccountId ?? 0,
                channel, area, now, alreadyInside,
                (connection, transaction, invitation) => WeddingMoneyGiftCommitService.HasPayment(connection, transaction,
                    invitation.PairId, invitation.StartAt, invitation.GuestCharacterId));
            if (invitation == null || invitation.State.Status == WeddingStatus.Ceremony
                && !IsCeremony(player, invitation.PairId, invitation.StartAt)) return null;
            return new(player.CharacterId, town, area, 0, new(invitation.PairId, invitation.StartAt));
        }

        // Capture before the first asynchronous departure. Admission must not
        // change the old audience until that departure has been sent.
        internal Func<bool> CaptureEntryCommit(EnhancedClientSession session, byte town, byte area)
        {
            var player = session.Player;
            var entry = player.WeddingAreaAdmission;
            return () =>
            {
                if (entry == null || !ReferenceEquals(session.Player, player)
                    || !ReferenceEquals(player.WeddingAreaAdmission, entry)
                    || player.CharacterId != entry.CharacterId || entry.Town != town || entry.Area != area
                    || player.CurTownId != town || player.CurAreaId != area
                    || !_sessions.TryGet(entry.CharacterId, out var current) || !ReferenceEquals(current, session)) return false;
                var confirmed = FindEntry(session, town, area);
                if (confirmed == null || confirmed.HomePairId != entry.HomePairId || confirmed.Ceremony != entry.Ceremony) return false;
                player.WeddingAreaAdmission = null;
                player.WeddingRoomPairId = entry.HomePairId;
                if (confirmed.HomeState != null) player.Wedding = confirmed.HomeState;
                if (entry.Ceremony == null) player.ClearWeddingCeremony();
                else player.BindWeddingCeremony(entry.Ceremony.PairId, entry.Ceremony.StartAt);
                return true;
            };
        }

        internal static bool IsCeremony(PlayerContext player, long pairId, long startAt)
        {
            var attendance = player?.WeddingCeremony;
            return pairId > 0 && startAt > 0 && attendance?.PairId == pairId && attendance.StartAt == startAt;
        }

        internal static Func<PlayerContext, bool> CaptureCohort(PlayerContext source)
        {
            long homePair = source?.WeddingRoomPairId ?? 0;
            var ceremony = source?.WeddingCeremony;
            return candidate => candidate != null && SameCohort(candidate.CurTownId, candidate.CurAreaId,
                homePair, ceremony?.PairId ?? 0, ceremony?.StartAt ?? 0, candidate);
        }

        internal static bool SameCohort(PlayerContext source, PlayerContext candidate)
        {
            if (candidate == null) return false;
            var ceremony = source?.WeddingCeremony;
            return SameCohort(candidate.CurTownId, candidate.CurAreaId, source?.WeddingRoomPairId ?? 0,
                ceremony?.PairId ?? 0, ceremony?.StartAt ?? 0, candidate);
        }

        // Shared objects retain the private audience captured at creation even
        // after the owner's position or live pair projection has been cleared.
        internal static bool SameCohort(byte town, byte area, long pairId, PlayerContext candidate)
            => SameCohort(town, area, pairId, 0, 0, candidate);

        internal static bool SameCohort(byte town, byte area, long pairId,
            long ceremonyPairId, long ceremonyStartAt, PlayerContext candidate)
        {
            if (candidate == null) return false;
            if (town != WeddingSceneCatalog.NativeCoupleTown && town != WeddingSceneCatalog.NativeChurchTown) return true;
            var scenes = WeddingSceneCatalog.Get();
            if (town == scenes.CoupleRoom.Town && area == scenes.CoupleRoom.Area)
                return pairId > 0 && pairId == candidate.WeddingRoomPairId;
            if (scenes.ChurchScenes.Any(x => x.Town == town && x.Area == area))
                return IsCeremony(candidate, ceremonyPairId, ceremonyStartAt);
            return true;
        }
    }
}
