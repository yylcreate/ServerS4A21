using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class SelectCharacterAckBodyBuilder
    {
        public static byte[] BuildRejected(byte errorCode)
        {
            return CommonPacketBodyBuilder.BuildCmdError(errorCode);
        }

        public static bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            Infrastructure.IGameDatabase database,
            out byte[] body)
        {
            return TryBuild(snapshot, database, connectionString: null, out body);
        }

        private static short ReadUsedFatigue(
            Infrastructure.IGameDatabase database,
            int characterId)
        {
            if (database == null || characterId <= 0)
                return 0;
            try
            {
                var snapshot =
                    new Game.Dungeon.CharacterFatigueService(database)
                        .GetSnapshot(characterId);
                return (short)Math.Ceiling(Math.Min(
                    snapshot.Used,
                    (decimal)Game.Dungeon.CharacterFatigueService.MaxFatigue));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[SelectCharacterAck] used fatigue read failed: " +
                    $"cid={characterId} error={ex.Message}");
                return 0;
            }
        }

        internal static bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            Infrastructure.IGameDatabase database,
            string connectionString,
            out byte[] body)
        {
            var initSnap = snapshot.InitializationSnapshot;
            var record = snapshot.CharacterRecord;

            if (record == null)
            {
                body = null;
                return false;
            }

            var writer = new GamePacketWriter();

            // [0] u8 resultCode
            writer.WriteByte(1);

            // [1] u32 accountRegTime — seed=0
            writer.WriteInt32(0);

            // [5] u32 characterCreatedTime
            if (record != null)
                writer.WriteInt32((int)((DateTimeOffset)record.CreatedAt).ToUnixTimeSeconds());
            else
                writer.WriteInt32(initSnap.AckCharCreatedTime);

            // [9] u16 uniqueId
            writer.WriteInt16(record != null ? (short)record.CharacterId : (short)initSnap.AckUniqueId);

            // 0136A187 的疲劳 UI 用 max 减去 019DDB80 写入的已用值；
            // 019DDB80 的输入来自 ACK [11]，不能只更新 [15]。
            var usedFatigue = ReadUsedFatigue(
                database ?? Infrastructure.GameDatabase.CreateDefault(),
                record.CharacterId);
            writer.WriteInt16(usedFatigue);

            writer.WriteInt16(Game.Dungeon.CharacterFatigueService.MaxFatigue);

            // [15] i16 usedFatigue — 微点真源向上取整到整数协议字段；
            // 读取失败保留 0 并记录日志，不阻断进号（准入检查在读图时 fail closed）。
            writer.WriteInt16(usedFatigue);

            // [17] u8 premiumCount + N × (u8 type + u8[8] endTime)
            var premiums = initSnap.AckPremiums;
            writer.WriteByte((byte)premiums.Count);
            for (int i = 0; i < premiums.Count; i++)
            {
                writer.WriteByte(premiums[i].PremiumType);
                writer.WriteBytes(premiums[i].EndTime);
            }

            // u32 cera
            writer.WriteInt32(initSnap.AckCera);

            // 30 x (u16 questId + u32 triggerValue) - fixed active quest slots
            List<ActiveQuest> activeQuests = null;
            if (record != null && record.CharacterId > 0)
            {
                try
                {
                    var connStr = !string.IsNullOrWhiteSpace(connectionString)
                        ? connectionString
                        : Infrastructure.GameDatabase.CreateDefault().ConnectionString;
                    activeQuests = QuestService.LoadActiveQuests(connStr, record.CharacterId);
                }
                catch { }
            }
            var projectedActiveQuests = QuestDungeonPresentationPlanner
                .ProjectActiveQuests(activeQuests);
            var activeQuestSlots = QuestSlotLayout.ProjectFixedSlots(
                projectedActiveQuests);
            for (int i = 0; i < QuestSlotLayout.ActiveSlotCount; i++)
            {
                var activeQuest = activeQuestSlots[i];
                if (activeQuest != null)
                {
                    writer.WriteUInt16(activeQuest.QuestId);
                    writer.WriteUInt32(activeQuest.TriggerValue);
                }
                else
                {
                    writer.WriteUInt16(0xFFFF);
                    writer.WriteInt32(0);
                }
            }

            // Four fixed int32 slots restored by CMD 0x01FB SAVE_QUEST_NOTIFY.
            for (var index = 0; index < QuestNotifySelectionService.MaxSlots; index++)
            {
                writer.WriteInt32(index < initSnap.QuestNotifyIds.Count
                    ? initSnap.QuestNotifyIds[index]
                    : 0);
            }

            writer.WriteByte(initSnap.AckCharSlotIndex);

            if (record.Level <= 1 && initSnap.AckTutorialSkipable == 0)
            {
                writer.WriteByte(0x00);  // v15 flag
                writer.WriteByte(0x00);  // v16 count = 0
            }
            else
            {
                writer.WriteByte(0x00);  // v15 flag
                writer.WriteByte(0x01);  // v16 count = 1
                writer.WriteByte(0x4E);  // flagIndex = 78
            }

            writer.WriteUInt16(initSnap.AckFatigueBattery);
            writer.WriteUInt16(initSnap.AckFatigueGrownUpBuff);
            writer.WriteByte(initSnap.AckTradePunishFlag);
            writer.WriteUInt16(initSnap.AckExtraField86JP);
            // reserved 8B: 客户端读取边界(264B)之后的尾巴, handler 不读(CMD_PACKET/4.md);
            // 旧列存的其实是抓包切片错位的教程标记残渣, 固定写零
            for (int j = 0; j < 8; j++)
                writer.WriteByte(0);
            writer.WriteByte(initSnap.AckTutorialSkipable);
            // ack_post_tutorial_u16: seed=0
            writer.WriteUInt16(0);
            // ack_unread_tail: seed=3B all-zero; original code defaulted to 22B when null
            for (int j = 0; j < 22; j++)
                writer.WriteByte(0);

            body = writer.ToArray();
            return true;
        }
    }
}
