using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 交易付款补齐：让 <c>TradeUtility.LaunchThingsOfType</c> 在**信号覆盖范围的地面凑不够**时，
    /// 从数字核心里扣（v0.37）。目标是 Universal Trade Hub，但顺带修好原版轨道贸易的同款不一致。
    ///
    /// ── 问题（静态分析，见报告 32） ──
    /// 「读」与「扣」是两条**各自独立**的链路，原 mod 只补了「读」：
    ///
    ///   读：UTH 的可用银统计 → <c>TradeUtility.AllLaunchableThingsForTrade(map)</c>
    ///        → 原 mod 的 <c>Patch_AllLaunchableThingsForTrade</c> 已把核心内容物追加进去 ✅
    ///   扣：UTH 下单 → <c>TradeUtility.LaunchSilver</c> → <c>LaunchThingsOfType</c>
    ///        → **自己独立实现**，只扫「已通电轨道贸易信标」范围内**地面**上的物品 ❌ 无人补
    ///
    /// ⇒ 表现为：界面显示可用银够（含核心），一付款却
    ///   <c>Log.Error("Could not find any Silver to transfer to trader.")</c> 且**扣不掉核心里的银**。
    ///   同一个不一致在原版轨道贸易上也存在（原版 <c>ColonyHasEnoughSilver</c> 走的是"读"那条链）。
    ///
    /// ── 做法 ──
    /// Prefix **只在必要时**接管（其余情况完全放行原版）：
    ///   ① 开关开着；② 地图上确实有**可用核心**；③ 原版信标地面**凑不够** debt。
    /// 接管时**逐行复刻原版语义**（信标地面优先 → 核心兜底），
    /// 扣除方式与原版完全一致：<c>trader.GiveSoldThingToTrader</c> 或 <c>SplitOff(num).Destroy()</c>。
    ///
    /// ── 为什么不用"抄到地面再让原版扣" ──
    /// 那会把物品真的生成到地图上、触发一大串 lister/区域事件，还可能被别的 mod 看到。
    /// 直接从容器里 <c>SplitOff</c> 才是与原 mod <c>HaulSourceContents.ExtractTo</c> 一致的取法。
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.LaunchThingsOfType))]
    internal static class Patch_TradeLaunchFromCore
    {
        /// <summary>
        /// ★ 必须用**最高优先级**（实测发现，见下）。
        ///
        /// `Map Wide Orbital Trade Beacon`（`Supes.MapWideOrbitalTradeBeacon`）也 Prefix 了同一个方法，
        /// 而且它的 <c>LaunchThingOfType</c> **无条件 <c>return false</c>**（完全接管），
        /// 自己那一套只扫"全图 / Home 区域的地面"，同样**不看容器**。
        ///
        /// Harmony 的规则：**任何一个 Prefix 返回 false，剩下的 Prefix 就全部被跳过**。
        /// 实测日志里报错来自 <c>MapWideTradeBeacon.Patcher.LaunchThingOfType</c>，
        /// 说明那一个先跑并接管了 —— 我们的 Prefix 压根没执行。
        /// ⇒ 用 <see cref="Priority.First"/> 抢到最前，才能拿到"先看一眼"的机会。
        ///
        /// 抢到最前也**不会破坏它**：只有在我们**确实能付清**（信标地面 + 核心 ≥ debt）时才接管；
        /// 否则照常放行，它的全图口径照旧生效。
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (debt <= 0 || map == null || resDef == null)
            {
                return true;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.tradePayFromCore)
            {
                return true;
            }

            // ★ v0.62 上游对齐：10 月版起原 mod 自带「地面 → 核心」付款（含断电门）⇒ 本项让位；
            //   旧版上游（无该类型）继续由本补丁兜底。
            if (UpstreamCorePaymentPresent())
            {
                return true;
            }

            // ★ 只有在"原版口径信标地面**凑不够**"时才考虑介入 —— 够就完全走原版，行为逐位一致。
            int ground = VanillaCoverage(resDef, map);
            if (ground >= debt)
            {
                return true;
            }

            List<Building_StorageCore> cores = CoreContentAccess.UsableCores(map);
            if (cores == null || cores.Count == 0)
            {
                return true;   // 没有可用核心 ⇒ 让原版（或别的补丁）照常处理
            }

            // ★★ 只在"我们确实能付清"时才接管：信标地面 + 核心 ≥ debt。
            //   凑不够就放行 —— 让 MapWideTradeBeacon 的全图口径 / 原版照旧去试、去报错。
            //   这样既补上了核心，又不会把别人的能力抢掉。
            int inCores = CoreCoverage(resDef, cores);
            if (ground + inCores < debt)
            {
                return true;
            }

            try
            {
                Launch(resDef, debt, map, trader, cores);
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 从数字核心付款时抛异常（本次付款未完成）：" + ex);
            }
            return false;
        }

        // ── v0.62 上游对齐：上游 10 月版自带核心付款，检测到即让位 ──
        private static bool upstreamPaymentResolved;
        private static bool upstreamPaymentPresent;

        /// <summary>
        /// 原 mod 10 月版起自带「地面 → 核心」付款（<c>DigitalStorage.HarmonyPatches.DigitalStorageTradeUtility</c>
        /// + 对 <c>LaunchThingsOfType</c> 的 Transpiler，含断电门）。存在时本补丁的付款项**整体让位**，
        /// 避免两套实现互相打架；旧版上游（无此类型）仍由本补丁兜底。
        /// </summary>
        private static bool UpstreamCorePaymentPresent()
        {
            if (!upstreamPaymentResolved)
            {
                upstreamPaymentResolved = true;
                try
                {
                    upstreamPaymentPresent =
                        AccessTools.TypeByName("DigitalStorage.HarmonyPatches.DigitalStorageTradeUtility") != null;
                }
                catch
                {
                    upstreamPaymentPresent = false;
                }
            }
            return upstreamPaymentPresent;
        }

        /// <summary>
        /// 复刻原版 <c>LaunchThingsOfType</c> 的循环，只把"找物品"换成**信标地面 → 核心**两级查找。
        /// 扣除方式与原版一字不差。
        /// </summary>
        private static void Launch(ThingDef resDef, int debt, Map map, TradeShip trader,
                                   List<Building_StorageCore> cores)
        {
            while (debt > 0)
            {
                Thing thing = FindOnBeaconGround(resDef, map);
                if (thing == null)
                {
                    thing = FindInCores(resDef, cores);
                }
                if (thing == null)
                {
                    Log.Error("Could not find any " + resDef?.ToString() + " to transfer to trader.");
                    return;
                }

                int num = Math.Min(debt, thing.stackCount);

                if (trader != null)
                {
                    trader.GiveSoldThingToTrader(thing, num, TradeSession.playerNegotiator);
                }
                else
                {
                    // 与 UTH 的用法一致（它传 trader = null）。
                    // SplitOff 会正确处理容器归属：部分拆分时造新 Thing 并把原堆减掉。
                    thing.SplitOff(num).Destroy();
                }

                debt -= num;
            }
        }

        /// <summary>原版口径：已通电轨道贸易信标覆盖范围内、地面上的第一件同类物品。</summary>
        private static Thing FindOnBeaconGround(ThingDef resDef, Map map)
        {
            IEnumerable<Building_OrbitalTradeBeacon> beacons = Building_OrbitalTradeBeacon.AllPowered(map);
            if (beacons == null)
            {
                return null;
            }

            foreach (Building_OrbitalTradeBeacon beacon in beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                IEnumerable<IntVec3> cells = beacon.TradeableCells;
                if (cells == null)
                {
                    continue;
                }

                foreach (IntVec3 cell in cells)
                {
                    List<Thing> things = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i].def == resDef)
                        {
                            return things[i];
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>本图可用核心覆盖范围内、地面上的同类物品总数（不含容器，和原版口径完全一致）。</summary>
        private static int VanillaCoverage(ThingDef resDef, Map map)
        {
            IEnumerable<Building_OrbitalTradeBeacon> beacons = Building_OrbitalTradeBeacon.AllPowered(map);
            if (beacons == null)
            {
                return 0;
            }

            int total = 0;
            foreach (Building_OrbitalTradeBeacon beacon in beacons)
            {
                if (beacon == null)
                {
                    continue;
                }

                IEnumerable<IntVec3> cells = beacon.TradeableCells;
                if (cells == null)
                {
                    continue;
                }

                foreach (IntVec3 cell in cells)
                {
                    List<Thing> things = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i].def == resDef)
                        {
                            total += things[i].stackCount;
                        }
                    }
                }
            }

            return total;
        }

        /// <summary>可用核心内容物里该资源的总数（用来判断"我们到底能不能付清"）。</summary>
        private static int CoreCoverage(ThingDef resDef, List<Building_StorageCore> cores)
        {
            int total = 0;
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

                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t != null && !t.Destroyed && t.stackCount > 0 && t.def == resDef)
                    {
                        total += t.stackCount;
                    }
                }
            }

            return total;
        }

        /// <summary>在可用核心的内容物里找第一件同类物品。</summary>
        private static Thing FindInCores(ThingDef resDef, List<Building_StorageCore> cores)
        {
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

                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t != null && !t.Destroyed && t.stackCount > 0 && t.def == resDef)
                    {
                        return t;
                    }
                }
            }

            return null;
        }
    }
}
