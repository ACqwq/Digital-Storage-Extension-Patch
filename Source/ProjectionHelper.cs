using System.Collections.Generic;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 投影核心注册表（**事件驱动**，不做任何每 tick 全图检测）。
    ///
    /// 两个职责：
    ///   1. 记录场内所有投影核心（供"这张图上有哪些投影"的查询用）——
    ///      投影核心**不是** IHaulSource，所以不能靠搬运源列表去反查它们；
    ///   2. 维持「同一张图上，绑同一个核心的多个投影里，谁是主源」。
    ///      主源唯一，保证统计/交易/自动收取只发生一次。
    ///
    /// 重算时机**只有**这几个事件：生成 / 销毁 / 换图 / 玩家改绑。
    /// 平时读取的只是实例上的一个 bool 字段，零计算。
    ///
    /// ★★ 关于「僵尸登记项」（v0.18 修复的刷屏报错）★★
    /// 本表是**静态**列表，会跨地图生命周期持有投影核心实例。
    /// 一旦地图集合变动（重力飞船起飞/落地、地图被移除、跨图传送），
    /// 某个实例的 <c>mapIndex</c> 可能残留一个已不存在的下标，
    /// 于是读 <c>p.Map</c> 会抛 ArgumentOutOfRangeException（<c>get_Map</c> 内部的 List 索引越界）。
    /// 而这张表是所有「找核心」路径的必经之处 ⇒ **每 tick 抛一次**，日志秒刷几百条，
    /// 并且连带把绑定菜单（走 AllUsableCores）和卸载（走 DeSpawn→Unregister）一起搞崩。
    ///
    /// 因此本表现在遵守两条铁律：
    ///   1. **任何**对登记项 <c>.Map</c> 的读取一律走 <see cref="DsSafe.MapOf"/>，永不直接读；
    ///   2. 遍历时**顺手自愈**：发现地图失效的登记项就地移除并记一条日志，
    ///      下一次遍历就干净了（不需要等玩家做任何操作）。
    /// </summary>
    internal static class ProjectionRegistry
    {
        private static readonly List<Building_CoreProjector> all = new List<Building_CoreProjector>();

        /// <summary>清理日志的节流时间戳（避免刷屏）。</summary>
        private static int lastPruneReportTick = -1000000;

        public static void Register(Building_CoreProjector p)
        {
            if (p == null || all.Contains(p))
            {
                return;
            }
            // 先把可能存在的僵尸清掉，再登记：保证随后的 Reelect 不会碰到坏项。
            Prune();
            all.Add(p);
            Reelect(DsSafe.MapOf(p));
        }

        public static void Unregister(Building_CoreProjector p)
        {
            if (p == null)
            {
                return;
            }
            // ★ 这里是 DeSpawn 的第一行，绝不能抛 —— 否则连"拆掉投影核心"这个 job 都会崩。
            Map map = DsSafe.MapOf(p);
            if (all.Remove(p))
            {
                Reelect(map);
            }
        }

        /// <summary>绑定变化后调用（该投影所在图重算一次）。</summary>
        public static void NotifyBindingChanged(Building_CoreProjector p)
        {
            Reelect(DsSafe.MapOf(p));
        }

        /// <summary>
        /// 读档 / 开新局时整体清空。
        /// 静态列表不会随存档切换而重置，不清的话上一局的登记项会污染下一局
        /// （更糟的情况：旧实例的 mapIndex 恰好在新局有效 ⇒ 变成"幽灵投影"）。
        /// 读档时各地图 FinalizeLoading 会重新 Spawn 投影核心 ⇒ 它们会自行重新登记。
        /// </summary>
        public static void Reset()
        {
            all.Clear();
        }

        /// <summary>
        /// 把 map 上的投影核心收集进 outList（不做去重，调用方自行处理）。
        /// 顺带自愈：地图已失效的登记项会被就地移除（倒序遍历，安全）。
        /// </summary>
        public static void CollectOn(Map map, List<Building_CoreProjector> outList)
        {
            if (map == null || outList == null)
            {
                return;
            }
            int pruned = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Building_CoreProjector p = all[i];
                if (p == null)
                {
                    all.RemoveAt(i);
                    continue;
                }
                Map pm = DsSafe.MapOf(p);
                if (pm == null)
                {
                    all.RemoveAt(i);
                    pruned++;
                    continue;
                }
                if (pm == map)
                {
                    outList.Add(p);
                }
            }
            ReportPrune(pruned);
        }

        /// <summary>该图上登记了多少个投影核心（不论是否可用/是否绑定）—— 诊断用。</summary>
        public static int CountOn(Map map)
        {
            if (map == null)
            {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Building_CoreProjector p = all[i];
                if (p == null)
                {
                    continue;
                }
                Map pm = DsSafe.MapOf(p);
                if (pm != null && pm == map)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 该核心是否被**本图的某个可用投影**投影到这里。
        /// 用于"这张图上能不能用那个核心"的判定（例如制作代理跨图取料）。
        /// </summary>
        /// <remarks>
        /// 只读实例字段与一个极小规模的列表（投影核心通常只有个位数），不做缓存也足够便宜。
        /// </remarks>
        public static bool IsProjectedTo(Map map, Building_StorageCore core)
        {
            if (map == null || core == null)
            {
                return false;
            }
            for (int i = 0; i < all.Count; i++)
            {
                Building_CoreProjector p = all[i];
                if (p == null)
                {
                    continue;
                }
                Map pm = DsSafe.MapOf(p);
                if (pm != map)
                {
                    continue;
                }
                // 用 IsUsable（= 绑定核心存在/已生成/自身可用），不要求 isPrimarySource：
                // "这个核心在这张图能不能用"与"谁是统计主源"是两回事。
                if (p.IsUsable && !p.MutedBySameMap && p.BoundCore == core)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 只重算**这一张图**：先把该图所有投影的主源标记清零，
        /// 再对每个被绑定的核心，取 thingIDNumber 最小的那个投影为主源。
        /// </summary>
        private static void Reelect(Map map)
        {
            // ★ 必须先清僵尸：Reelect 由 Register/Unregister 调用，
            //   而 Unregister 正是 DeSpawn 的第一行 —— 这里一抛，卸载 job 就崩。
            Prune();
            if (map == null)
            {
                return;
            }
            for (int i = 0; i < all.Count; i++)
            {
                Building_CoreProjector p = all[i];
                if (p != null && DsSafe.MapOf(p) == map)
                {
                    p.isPrimarySource = false;
                }
            }
            for (int i = 0; i < all.Count; i++)
            {
                Building_CoreProjector p = all[i];
                if (p == null || DsSafe.MapOf(p) != map)
                {
                    continue;
                }
                Building_StorageCore core = p.boundCore;
                if (core == null)
                {
                    continue;
                }
                bool takenByLowerId = false;
                for (int j = 0; j < all.Count; j++)
                {
                    Building_CoreProjector q = all[j];
                    if (q == null || q == p || DsSafe.MapOf(q) != map)
                    {
                        continue;
                    }
                    if (q.boundCore == core && q.thingIDNumber < p.thingIDNumber)
                    {
                        takenByLowerId = true;
                        break;
                    }
                }
                p.isPrimarySource = !takenByLowerId;
            }
        }

        /// <summary>清掉所有「地图已失效」的残魂登记项，返回清理数量。</summary>
        private static int Prune()
        {
            int n = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Building_CoreProjector p = all[i];
                if (p == null)
                {
                    all.RemoveAt(i);
                    continue;
                }
                if (DsSafe.MapOf(p) == null)
                {
                    all.RemoveAt(i);
                    n++;
                }
            }
            ReportPrune(n);
            return n;
        }

        private static void ReportPrune(int n)
        {
            if (n <= 0)
            {
                return;
            }
            // 节流：半天（2500 tick）最多报一次。正常情况下一局只会触发一两次。
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (now - lastPruneReportTick < 2500)
            {
                return;
            }
            lastPruneReportTick = now;
            Log.Warning("[数字存储补丁] 已自动清理 " + n + " 条失效的投影核心登记"
                        + "（多见于重力飞船/跨地图传送造成地图变动之后）。"
                        + "若投影核心显示未绑定，重新绑定一次即可正常使用。");
        }
    }

    /// <summary>
    /// 「投影核心」的地图级查询：把某张地图上**可用主源投影**所绑定的核心，
    /// 作为「该地图可用的核心 / 该地图可见的内容物来源」对外暴露。
    /// </summary>
    internal static class ProjectionHelper
    {
        private static readonly List<Building_CoreProjector> tmp = new List<Building_CoreProjector>();

        /// <summary>
        /// 把 map 上"代表某个核心"的投影所绑定的核心追加进 outList。
        /// 按核心**去重**：同图多个投影绑同一核心时只加一次（避免数量翻倍）。
        /// </summary>
        public static void AppendProjectedCores(Map map, List<Building_StorageCore> outList)
        {
            if (map == null || outList == null)
            {
                return;
            }
            tmp.Clear();
            ProjectionRegistry.CollectOn(map, tmp);
            for (int i = 0; i < tmp.Count; i++)
            {
                Building_CoreProjector p = tmp[i];
                if (!p.IsActiveForMap)
                {
                    continue;
                }
                Building_StorageCore core = p.BoundCore;
                if (core != null && !outList.Contains(core))
                {
                    outList.Add(core);
                }
            }
            tmp.Clear();
        }
    }
}
