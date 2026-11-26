namespace DfoServer.Game.Dungeon.Bio
{
    internal sealed record BioScoreInput(
        ushort Seat,
        byte Metric2,
        byte Metric3,
        bool ActorState,
        uint SurvivalMilliseconds,
        uint Metric4,
        uint Points);
}
