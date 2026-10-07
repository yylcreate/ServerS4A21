using System;
using System.Collections.Generic;

namespace DfoServer.Game.Guilds
{
    // 公会"属性"内容对应的客户端增益: 哪个增益索引当前对这个公会生效, 以及到期时间。
    internal sealed record GuildStatBuff(int BuffIndex, int Category, uint ExpiresAt);

    // 公会属性内容的真源与语义。
    //
    // 证据链(当前 Script.pvf 实测):
    //   etc/(r)guild.etc [guild contents list] 第 3 列(参数)对类别码 3/5 不是数值,
    //   而是 etc/variousbufflist.etc 的增益索引:
    //     行 1     类别码 5  参数 169 -> [index] 169 `Guild_Status1` `永久公会属性`
    //     行 2/5/8 类别码 3  参数 167 -> [index] 167 `Guild_Status`  `限时公会属性`
    //   两条增益的效果相同: physical_attack / physical_defence / magical_attack /
    //   magical_defence 各 +60, 与道具说明 stackable/10149667.stk 和 10150602.stk 的
    //   "力量、体力、智力、精神增加60点"一致。力量/体力/智力/精神在 PVF 里就是
    //   character/*.chr [initial value] 段里的 physical attack / physical defense /
    //   magical attack / magical defense 四项。
    //
    // 所以服务端不自己算 +60, 也不把 60 写进代码: 数值只存在于增益定义中, 由客户端
    // 按同一份 PVF 解析。服务端只决定"下发哪个索引、还能持续多久"。
    //
    // 已购记录只存 (类别码, 到期时间), 不存行号, 所以这里按类别码回查内容表取参数。
    // 同一类别码出现两种参数时直接抛: 那种 PVF 下无法确定要下发哪个增益, 静默挑一个
    // 会把配置错误表现成"属性加得不对"。
    internal static class GuildStatBuffRule
    {
        // 类别码 3 = 限时公会属性, 5 = 永久公会属性。
        internal const int TemporaryStatCategory = 3;
        internal const int PermanentStatCategory = 5;

        internal static bool IsStatCategory(int category)
            => category == TemporaryStatCategory || category == PermanentStatCategory;

        // 该类别码对应的增益索引; 表里没有这个类别码时返回 0。
        internal static int BuffIndexForCategory(int category)
        {
            if (!IsStatCategory(category))
                return 0;

            int resolved = 0;
            foreach (var definition in GuildContentCatalog.All)
            {
                if (definition.Category != category)
                    continue;
                if (resolved != 0 && resolved != definition.Parameter)
                    throw new InvalidOperationException(
                        $"PVF guild content category {category} has more than one parameter " +
                        $"({resolved} and {definition.Parameter}); the buff index to send is ambiguous.");
                resolved = definition.Parameter;
            }

            return resolved;
        }

        // 所有可能下发过的"公会属性"增益索引。
        // 退会、被踢、公会解散、内容到期时要用它下发 CHARACTER_DEL_BUFF: 客户端的
        // 运行时增益表里可能残留其中任意一条, 而会话对象不记录"发过哪一条"。删除一个
        // 不存在的索引是无操作, 所以整组删比在会话里另存一份状态更不容易出错。
        internal static IReadOnlyList<int> AllStatBuffIndices()
        {
            var result = new List<int>(2);
            foreach (int category in new[] { TemporaryStatCategory, PermanentStatCategory })
            {
                int buffIndex = BuffIndexForCategory(category);
                if (buffIndex > 0 && !result.Contains(buffIndex))
                    result.Add(buffIndex);
            }

            return result;
        }

        // 该公会当前生效的属性增益。没有生效内容时返回 null。
        //
        // 同一公会可能同时存在一条限时记录和一条永久记录(GuildContentPurchaseService
        // 按 (guild_id, category) 各存一条, 到期时间取较晚者)。两者效果相同, 只下发
        // 其中一条以免客户端重复叠加: 永久(类别码 5)优先于限时 —— 永久内容不会到期,
        // 严格覆盖限时内容。这是服务端的取舍, 尚未实机验证客户端对两条同时生效时的
        // 表现, 因此不做"两条都发"的尝试。
        internal static GuildStatBuff Resolve(IReadOnlyList<GuildPurchasedContent> contents, uint nowUnix)
        {
            if (contents == null)
                return null;

            GuildStatBuff temporary = null;
            foreach (var content in contents)
            {
                if (!IsStatCategory(content.Category) || content.ExpiresAt <= nowUnix)
                    continue;

                int buffIndex = BuffIndexForCategory(content.Category);
                if (buffIndex <= 0)
                    continue;

                if (content.Category == PermanentStatCategory)
                    return new GuildStatBuff(buffIndex, content.Category, content.ExpiresAt);

                // 同一类别码在库里只有一条, 这里取更晚的到期时间只是为了在数据被手工
                // 改出重复行时仍然确定。
                if (temporary == null || content.ExpiresAt > temporary.ExpiresAt)
                    temporary = new GuildStatBuff(buffIndex, content.Category, content.ExpiresAt);
            }

            return temporary;
        }
    }
}
