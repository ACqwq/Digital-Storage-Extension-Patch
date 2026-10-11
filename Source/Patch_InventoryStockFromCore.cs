using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「携带」栏设定的随身库存目标（<c>Pawn.inventoryStock</c>）也能用数字核心里的货（v0.44）。
    ///
    /// ── 现象 ──
    /// 玩家在「携带」栏给小人设定「随身带 5 个闪耀世界药物 / 10 份包装生存食品」，
    /// 这些货全存在数字核心里时，小人**永远补不满**，且每隔一会儿又去试一次，反复空跑。
    ///
    /// ── 根因（反编译取证）──
    /// <c>JobGiver_TakeForInventoryStock.TryGiveJob</c> → <c>FindThingFor</c>：
    /// <code>
    /// private Thing FindThingFor(Pawn pawn, ThingDef thingDef)
    /// {
    ///     return GenClosest.ClosestThingReachable(
    ///         pawn.Position, pawn.Map, ThingRequest.ForDef(thingDef),
    ///         PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
    ///         x => ThingValidator(pawn, x));      // ★ 没传 lookInHaulSources
    /// }
    /// </code>
    /// <c>GenClosest.ClosestThingReachable</c> 有个 <c>lookInHaulSources</c> 参数（默认 <c>false</c>），
    /// 只有显式传 <c>true</c> 才会把 <c>IHaulSource</c>（数字核心正是）的内容物算进搜索。
    /// 原版全库只有 7 处传了 <c>true</c>，这里没传 ⇒ **只搜地图上已生成的 Thing**。
    /// 东西全在核心里 ⇒ 找不到 ⇒ 原版把它自己的节流（<c>nextInventoryStockTick</c>）
    /// 推到 6000–9000 tick 之后、返回 null，于是玩家看到「设定永远填不满」。
    ///
    /// ── 为什么挂 <c>TryGiveJob</c> 而不是 <c>FindThingFor</c> ──
    /// <c>FindThingFor</c> 方法体只有一行。**方法体很小的目标会被 Mono JIT 内联**，
    /// 挂上去的 Prefix/Postfix 会静默失效（本项目踩坑目录已记录该现象）。
    /// <c>TryGiveJob</c> 是 <c>ThinkNode_JobGiver</c> 的 override，体量足够，挂得住。
    ///
    /// ── 做法 ──
    /// Postfix <c>TryGiveJob</c>：**只在原版返回 null 时**复刻一遍原版的判定链
    /// （节流 / <c>AnyThingsRequiredNow</c> / <c>UnloadEverything</c> / 逐项比对缺口），
    /// 找到缺的那一项就从可用数字核心取一份到小人脚下，返回一个**与原版同款**的
    /// <c>TakeCountToInventory</c> 作业。原版给得出作业时我们一字不动。
    ///
    /// ── 节流：为什么不用原版那个字段判断 ──
    /// 原版在返回 null 之前**已经把 <c>nextInventoryStockTick</c> 推到了未来**，
    /// 若复用它做"是否该介入"的判断，永远会得到"还没到时间"⇒ 永远不介入。
    /// 改用**差值**判断，它恰好能区分两条返回路径：
    /// <list type="bullet">
    ///   <item>原版跑完一轮没找到 ⇒ 刚设的值，差值 = 6000~9000（≥ 6000）⇒ **该我介入**；</item>
    ///   <item>原版因节流提前 return（<c>TicksGame &lt; next</c>）⇒ 字段没被改，差值必然 &lt; 6000 ⇒ **不介入**。</item>
    /// </list>
    /// 该判据依赖原版常量 6000，故提为具名常量并注明来源。
    /// 若将来原版把间隔调**小**（比如 3000），判据不成立 ⇒ 本功能静默失效（但不会造成取货风暴）；
    /// 调大则依然成立。**失效方向是安全的**。
    ///
    /// ── 失败时仍然照旧 ──
    /// 没有可用核心 / 核心没这种货 / 脚下放不下 ⇒ 原样返回 null，原版行为不变
    /// （小人照旧补不满，与本修复前一致，不掩盖真实缺货）。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_TakeForInventoryStock), "TryGiveJob")]
    internal static class Patch_InventoryStockFromCore
    {
        /// <summary>
        /// 原版节流下限，来源 <c>JobGiver_TakeForInventoryStock.InventoryStockCheckIntervalMin = 6000</c>。
        /// 用于区分"原版刚设完节流"与"原版因节流早退"（见类注释）。
        /// </summary>
        private const int VanillaInventoryStockIntervalMin = 6000;

        /// <summary>兜底取出次数（只读诊断用）。</summary>
        internal static int ExtractCount;

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            // 原版给得出作业 ⇒ 一律不动（优先用地图上的现成货，与修复前完全一致）。
            if (__result != null || pawn == null)
            {
                return;
            }

            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.inventoryStockFromCore)
            {
                return;
            }

            // ★ 只在"原版刚跑完一轮检查并设了新节流"这一刻介入（见类注释的差值判据）。
            if (pawn.mindState == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (pawn.mindState.nextInventoryStockTick - now < VanillaInventoryStockIntervalMin)
            {
                return;
            }

            // 复刻原版前置条件（顺序与原版一致，任一条不满足就等同于原版结果）。
            if (pawn.inventory == null || pawn.inventoryStock == null)
            {
                return;
            }
            if (!pawn.inventoryStock.AnyThingsRequiredNow())
            {
                return;
            }
            if (pawn.inventory.UnloadEverything)
            {
                return;
            }

            try
            {
                foreach (InventoryStockEntry entry in pawn.inventoryStock.stockEntries.Values)
                {
                    if (entry?.thingDef == null || entry.count <= 0)
                    {
                        continue;
                    }

                    // 这一项不缺 ⇒ 看下一项（原版同款判断）。
                    if (pawn.inventory.Count(entry.thingDef) >= entry.count)
                    {
                        continue;
                    }

                    // ★ 按缺口取（见 ExtractFromCores 的注释）——不要整堆取，否则会超额取物。
                    int missing = entry.count - pawn.inventory.Count(entry.thingDef);
                    if (missing <= 0)
                    {
                        continue;
                    }

                    Thing taken = ExtractFromCores(pawn, entry.thingDef, missing, map);
                    if (taken == null)
                    {
                        continue;   // 核心也没有 ⇒ 看下一项
                    }

                    // ★ 与原版同款地构造作业：目标 = 刚取出来的那份（就在小人脚下）。
                    //   取出后原版整条链（Reserve → GotoThing → StartCarryThing → 入包）一字不改。
                    Job job = JobMaker.MakeJob(JobDefOf.TakeCountToInventory, taken);
                    job.count = Mathf.Min(taken.stackCount, missing);
                    __result = job;
                    ExtractCount++;

                    if (s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][携带补货兜底] 地图上没有，已从数字核心取出："
                                    + taken.LabelShort + " x" + job.count
                                    + "（目标 " + entry.count + "）@ " + taken.Position);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                // 兜底失败不该把"地图上没货"升级成红字异常，也不该阻断原版流程。
                __result = null;
                Log.ErrorOnce("[数字存储补丁] 从数字核心为「携带」栏补货时出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510289);
            }
        }

        /// <summary>
        /// 在可用核心（含本图投影核心绑定的核心）里找一件 <paramref name="thingDef"/>，
        /// **按 <paramref name="count"/> 取需要的数量**到 <paramref name="pawn"/> 脚下，
        /// 返回取出来的那份。
        ///
        /// ── 为什么必须按需取，而不是整堆取 ──
        /// <c>HaulSourceContents.ExtractTo</c> 自身的逻辑是：
        /// <code>
        /// int num = Math.Min(count, t.stackCount);
        /// if (num >= t.stackCount) { directlyHeldThings.Remove(t); val = t; }
        /// else { val = t.SplitOff(num); }
        /// </code>
        /// 这与 4.0 自己的虚空取物实现 <c>HediffComp_Backpack.TryAbsorb(source, count)</c>
        /// 是**同一套逻辑**（那边是
        /// <c>int num = count &lt; source.stackCount ? count : source.stackCount;</c>
        /// 再加 <c>SplitOff</c>）。
        ///
        /// 也就是说 <c>ExtractTo</c> **本来就支持按需取** —— 只要把 <c>count</c> 传对。
        /// 曾经这里传的是 <c>found.stackCount</c>（整堆），结果核心里每种物品都被甩出一整堆到地上，
        /// 既浪费又会被原 mod 的自动收纳反复吸回（实测反馈）。**改成按需取即可。**
        /// </summary>
        private static Thing ExtractFromCores(Pawn pawn, ThingDef thingDef, int count, Map map)
        {
            if (count <= 0)
            {
                return null;
            }

            List<Building_StorageCore> cores = tmpCores;
            cores.Clear();
            List<Building_StorageCore> mapCores = CoreContentAccess.UsableCores(map);
            if (mapCores != null)
            {
                cores.AddRange(mapCores);
            }

            // 本图投影核心绑定的核心也认（与补丁其它跨图逻辑同一套口径，自动去重）。
            try
            {
                ProjectionHelper.AppendProjectedCores(map, cores);
            }
            catch (Exception)
            {
                // 投影读取失败不影响本图核心这条主路径。
            }

            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.Spawned)
                {
                    continue;
                }

                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null)
                {
                    continue;
                }

                // ★ 先抓快照再取：ExtractTo 会把物品从 owner 里移除，边遍历边改会抛
                //   "Collection was modified"（本项目踩坑目录第 9 条的同类陷阱）。
                Thing found = null;
                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t != null && !t.Destroyed && t.stackCount > 0 && t.def == thingDef)
                    {
                        found = t;
                        break;
                    }
                }

                if (found == null)
                {
                    continue;
                }

                // ★ 按需取：与 TryAbsorb 同款，取 min(需要, 这一堆) ——不再整堆甩出来。
                int want = Math.Min(count, found.stackCount);
                if (want <= 0)
                {
                    continue;
                }

                Thing taken = HaulSourceContents.ExtractTo(found, want, pawn.Position, map);
                if (taken != null)
                {
                    cores.Clear();
                    return taken;
                }

                // 取不出（脚下放不下等）：若这一堆不够，继续找下一个核心；
                // 若够却取不出，继续找别的核心也没意义，但代价很低，保持简单。
                // ExtractTo 返回 null 时它已把货放回核心，不会丢东西。
            }

            cores.Clear();
            return null;
        }

        private static readonly List<Building_StorageCore> tmpCores = new List<Building_StorageCore>();
    }
}
