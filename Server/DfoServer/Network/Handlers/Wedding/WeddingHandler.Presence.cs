using DfoServer.Network.Builders;
using DfoServer.Game.Session;
using DfoServer.Game.Wedding;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class WeddingHandler
    {
        internal async Task SendActorStateAsync(EnhancedClientSession source, EnhancedClientSession recipient, CancellationToken cancellationToken)
        {
            int actor = source?.Player?.CharacterId ?? 0, observer = recipient?.Player?.CharacterId ?? 0;
            if (!Current(source, actor) || !Current(recipient, observer)) return;
            ushort uid = source.Player.UserId, observerUid = recipient.Player.UserId;
            byte town = source.Player.CurTownId, area = source.Player.CurAreaId;
            int listener = source.ListenerPort;
            var cohort = WeddingSceneAdmission.CaptureCohort(source.Player);
            bool Audience() => Current(source, actor) && Current(recipient, observer)
                && source.Player.UserId == uid && recipient.Player.UserId == observerUid
                && source.ListenerPort == listener && recipient.ListenerPort == listener
                && cohort(source.Player) && cohort(recipient.Player)
                && source.Player.CurTownId == town && source.Player.CurAreaId == area
                && (ReferenceEquals(source, recipient) || (SessionDirectory.IsTownPresence(source.Player)
                    && SessionDirectory.IsTownPresence(recipient.Player)
                    && recipient.Player.CurTownId == town && recipient.Player.CurAreaId == area
                    && WeddingSceneAdmission.SameCohort(source.Player, recipient.Player)));
            if (!Audience()) return;
            var state = _repository.Load(actor);
            // Fresh USERINFO actors have no marriage by default. Explicit
            // breakup publication already sends DIVORCE and the unbound state.
            if (state.Status == WeddingStatus.None) return;
            await recipient.TrySendPacketAsync(WeddingPacketBuilder.ActorState(uid, state), cancellationToken,
                () => Audience() && _repository.Load(actor) == state);
        }
    }
}
