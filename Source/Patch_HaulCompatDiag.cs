using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// S4 —— **只读**诊断：搬运 / 入库兼容性（报告 31 §4）。
    ///
    /// ★ 它**什么都不改**：只有一个 void Postfix，不干预返回值、不跳过原方法、不改任何 out 参数。
    ///   设置里「诊断日志」关闭时，第一行就 return（一次 bool 读 + 整数比较，平时零开销）。
    ///
    /// ── 为什么挂这里 ──
    /// `StoreUtility.TryFindBestBetterStorageFor` 是**原版**找仓库的唯一总入口，
    ///   Pick Up And Haul 与 Hauler's Dream 的卸货都调它（报告 31 §2 / §3）。
    ///   所以在这里统计"选中了什么"，就能一次回答：
    ///     · 搬运 mod 到底能不能找到数字核心？（coreHits > 0 ⇒ 能）
    ///     · 还是压根找不到目的地？（misses 高 ⇒ 会退化成"丢在地上 / 留在背包"）
    ///     · 有没有走上容器路径（核心必然是容器路径，不是格子路径）
    ///
    /// ── 四类结果 ──
    ///   核心容器  = 目的地是 <see cref="Building_StorageCore"/>  ⇒ 走 HaulToContainer
    ///   其它容器  = 非槽位父级的容器（墓穴、运输舱之类的原版容器）
    ///   槽位格子  = foundCell 有效 ⇒ 走 HaulToCell（HD 的批量搬运只认这条）
    ///   没找到    = 返回 false ⇒ 搬运 mod 会退化处理
    /// </summary>
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStorageFor))]
    internal static class Patch_HaulCompatDiag
    {
        /// <summary>汇总输出间隔（tick）。600 ≈ 10 秒。</summary>
        private const int FlushEveryTicks = 600;

        /// <summary>开关缓存的刷新间隔：避免每次调用都去查 Mod 设置（该方法调用很频繁）。</summary>
        private const int CacheTicks = 60;

        private static int coreHits;
        private static int otherContainerHits;
        private static int cellHits;
        private static int misses;

        /// <summary>
        /// 原版**常规查询**次数（`carrier == null`）。
        ///
        /// ★ 为什么必须分开统计（v0.35b，旧存档报告暴露）：
        ///   原版 `ListerHaulables.ShouldBeHaulable` → `StoreUtility.IsInValidBestStorage`
        ///   会调 `TryFindBestBetterStorageFor(t, **null**, map, …)` 来判"这件东西是不是已经在最好的存储里"。
        ///   旧存档里这一条链路每 3,617 tick 就贡献了 46,003 次调用 ——
        ///   混在一起会让"找仓库 7682 次、没找到 7671 次"看起来像"有 7671 件货搬不动"，
        ///   实际上绝大部分只是**原版每 tick 的例行体检**，根本不是搬运需求。
        ///   小人 / 搬运 mod 真正发起的搬运，`carrier` 一定非 null。
        /// </summary>
        private static int passiveChecks;

        /// <summary>
        /// "没找到"按 def 分组计数。
        ///
        /// ★ 为什么必须分组：第一版只记"最后一件"，结果被一具 Corpse_Human 刷屏
        ///   （核心不收尸体、图里又没有墓地 ⇒ 同一个物品被反复查询、反复失败）。
        ///   不分组会误以为"有 240 件东西进不去"，实际可能只是几件东西被查了很多次。
        /// </summary>
        private static readonly Dictionary<ThingDef, int> missByDef = new Dictionary<ThingDef, int>();

        private static string lastMissLabel;
        private static int lastFlushTick = -1000000;

        private static bool cachedActive;
        private static int cachedActiveTick = -1000000;

        /// <summary>诊断是否开启（带缓存，调用频繁也不能拖慢原版）。</summary>
        private static bool Active()
        {
            int now = Now;
            if (now - cachedActiveTick >= CacheTicks)
            {
                cachedActiveTick = now;
                DsPatchSettings s = DsPatchMod.Settings;
                cachedActive = s != null && s.diagnosticsEnabled;
            }
            return cachedActive;
        }

        private static int Now => Find.TickManager != null ? Find.TickManager.TicksGame : 0;

        private static void Postfix(Thing t, Pawn carrier, bool __result, IntVec3 foundCell, IHaulDestination haulDestination)
        {
            if (!Active())
            {
                return;
            }

            try
            {
                // 原版例行的"是否已在最佳存储"体检 ⇒ 单独计数，不掺进搬运统计。
                if (carrier == null)
                {
                    passiveChecks++;
                    MaybeFlush();
                    return;
                }

                if (!__result || (haulDestination == null && !foundCell.IsValid))
                {
                    misses++;
                    if (t != null && t.def != null)
                    {
                        lastMissLabel = t.def.defName;
                        int n;
                        missByDef.TryGetValue(t.def, out n);
                        missByDef[t.def] = n + 1;
                    }
                }
                else if (haulDestination is Building_StorageCore)
                {
                    coreHits++;
                }
                else if (haulDestination != null && foundCell.IsValid)
                {
                    cellHits++;
                }
                else if (haulDestination != null)
                {
                    otherContainerHits++;
                }
                else
                {
                    cellHits++;
                }

                MaybeFlush();
            }
            catch
            {
                // 诊断绝不能影响游戏：吞掉一切异常。
            }
        }

        /// <summary>到点就输出一次汇总。收货架的 Tick 也会调它（没有搬运活动时也能出数）。</summary>
        internal static void MaybeFlush()
        {
            if (!Active())
            {
                return;
            }
            int now = Now;
            if (now - lastFlushTick < FlushEveryTicks)
            {
                return;
            }
            lastFlushTick = now;

            int total = coreHits + otherContainerHits + cellHits + misses;
            string shelf = "  收货架：入库 " + Building_ReceivingShelf.DiagMoved
                           + " 堆 / 容量挡 " + Building_ReceivingShelf.DiagBlockedBySpace
                           + " / 筛选挡 " + Building_ReceivingShelf.DiagBlockedByFilter
                           + "\n    收货架转移分段耗时：" + DsShelfTiming.Describe();

            // "没找到"按 def 取前 3，避免被一两件反复失败的东西刷屏（见字段注释）。
            string top = "";
            if (misses > 0 && missByDef.Count > 0)
            {
                List<KeyValuePair<ThingDef, int>> top3 = new List<KeyValuePair<ThingDef, int>>();
                foreach (KeyValuePair<ThingDef, int> kv in missByDef)
                {
                    top3.Add(kv);
                }
                top3.Sort(delegate (KeyValuePair<ThingDef, int> a, KeyValuePair<ThingDef, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                int shown = top3.Count < 3 ? top3.Count : 3;
                for (int i = 0; i < shown; i++)
                {
                    top += (i == 0 ? "" : "、") + top3[i].Key.defName + "×" + top3[i].Value;
                }
                if (top3.Count > shown)
                {
                    top += " 等 " + top3.Count + " 种";
                }
                int distinct = missByDef.Count;
                if (distinct > 0 && misses >= distinct * 5)
                {
                    top += "  ⚠ 只有 " + distinct + " 种物品却失败 " + misses
                           + " 次 ⇒ 多半是**同一批东西被反复查询**（没有能收它们的地方），不是每次都有新货";
                }
            }

            Log.Message("[数字存储补丁][搬运诊断] 真实搬运 " + total + " 次 ⇒ "
                        + "核心容器 " + coreHits
                        + " ｜ 其它容器 " + otherContainerHits
                        + " ｜ 槽位格子 " + cellHits
                        + " ｜ 没找到 " + misses
                        + (misses > 0 && lastMissLabel != null ? "（最后一件：" + lastMissLabel + "）" : "")
                        + "\n    没找到明细：" + (top.Length > 0 ? top : "无")
                        + "  核心让位 " + Patch_AvoidCoreDirectHaul.VetoCount
                        + "\n    原版常规查询（不算搬运需求，ListerHaulables 的例行体检）：" + passiveChecks
                        + "\n    " + shelf);

            coreHits = 0;
            otherContainerHits = 0;
            cellHits = 0;
            misses = 0;
            passiveChecks = 0;
            lastMissLabel = null;
            missByDef.Clear();
            Building_ReceivingShelf.DiagMoved = 0;
            Building_ReceivingShelf.DiagBlockedBySpace = 0;
            Building_ReceivingShelf.DiagBlockedByFilter = 0;
            DsShelfTiming.Clear();
            Patch_AvoidCoreDirectHaul.VetoCount = 0;
        }
    }
}
