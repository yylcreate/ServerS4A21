using System;
using DfoServer.Game.Events.GuardianAltar;

namespace DfoServer.Network.Builders
{
    internal static class GuardianAltarPurchaseAckBodyBuilder
    {
        internal static byte[] BuildRejected() => CommonPacketBodyBuilder.BuildCmdError(0x04);

        internal static byte[] Build(GuardianAltarPurchaseRequest request,
            GuardianAltarPurchaseOutcome outcome, short newLevel, int remainingStars)
        {
            if (request.RidableId <= 0 || request.ShopType < 0 || request.ShopType > 2
                || request.ResourceId <= 0 || request.StartLevel <= 0
                || request.TargetLevel < request.StartLevel || newLevel < 0 || remainingStars < 0)
                throw new ArgumentOutOfRangeException(nameof(request));
            var result = outcome switch
            {
                GuardianAltarPurchaseOutcome.Success => 0,
                GuardianAltarPurchaseOutcome.Closed => 1,
                GuardianAltarPurchaseOutcome.InsufficientStars => 2,
                GuardianAltarPurchaseOutcome.CannotPurchase => 3,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            if (result == 0 && newLevel != request.TargetLevel)
                throw new InvalidOperationException("Guardian purchase ACK must reflect its committed target.");
            // sub_11053A0 always consumes all 24B after outer success, even
            // for domain errors. sub_146A480 ignores payload+16, takes the new
            // level at +18 and the authoritative remaining stars at +20.
            // Echo the verified requested start at +16 without naming it price.
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result);
            writer.WriteInt32(request.RidableId);
            writer.WriteInt32(request.ShopType);
            writer.WriteInt32(request.ResourceId);
            writer.WriteInt16(request.StartLevel);
            writer.WriteInt16(newLevel);
            writer.WriteInt32(remainingStars);
            return writer.ToArray();
        }
    }
}
