using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class FatigueNotificationSender
    {
        internal static async Task SendAsync(
            EnhancedClientSession session,
            CharacterFatigueService fatigue)
        {
            if (session?.Player == null || fatigue == null)
                return;
            try
            {
                var snapshot = fatigue.GetSnapshot(session.Player.CharacterId);
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.FATIGUE,
                        FatigueNotificationBuilder.Build(snapshot)));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonFatigue] notification failed: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }
    }
}
