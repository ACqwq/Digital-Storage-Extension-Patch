using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// G · 远行队 / 据点交易使用数字核心库存。
    ///
    /// ── 问题（反编译 1.6 核实的完整链路） ──
    /// 交易窗口里"玩家能卖什么"只有一个来源：<c>ITrader.ColonyThingsWillingToBuy(playerNegotiator)</c>，
    /// 由 <c>TradeDeal.AddAllTradeables()</c> 枚举。而它的 4 个实现全都是
    /// "只给**远行队背包** / **地图上可到达的散落物**"：
    /// <code>
    /// Settlement_TraderTracker.ColonyThingsWillingToBuy  → CaravanInventoryUtility.AllInventoryItems(caravan)
    /// Caravan_TraderTracker.ColonyThingsWillingToBuy     → 同上
    /// Pawn_TraderTracker.ColonyThingsWillingToBuy        → map.listerThings.AllThings（地图上的散落物）
    /// TradeShip.ColonyThingsWillingToBuy                 → TradeUtility.AllLaunchableThingsForTrade(map)
    /// </code>
    /// 核心里的物品在 <c>innerContainer</c> 里、未 Spawn ⇒ 一个都不在 ⇒ 交易窗口看不到它们。
    ///
    /// ── 为什么转移路径**天然安全**（这是 4.0 比 3.0 简单得多的原因） ──
    /// 成交走 <c>Tradeable.ResolveTrade()</c> → <c>TransferableUtility.TransferNoSplit</c>
    /// → <c>ITrader.GiveSoldThingToTrader(thing, count, negotiator)</c>，4 个实现全部是：
    /// <code>
    /// Thing thing = toGive.SplitOff(countToGive);   // Verse/Thing.cs:1616
    ///   ⇒ count >= stackCount 时：DeSpawnOrDeselect(); holdingOwner?.Remove(this);
    /// thing.PreTraded(...);                          // ThingWithComps 只通知各 comp
    /// stock.TryAdd(thing, false) / things.TryAdd(thing)   // 进对方的库存
    /// </code>
    /// <c>holdingOwner.Remove(this)</c> 就是**正确的容器移除**（ThingOwner.Remove 会通知容器），
    /// 部分出售时 <c>SplitOff</c> 会造一个新 Thing 并把原堆数量减掉，核心里的余堆保持完整。
    /// ⇒ 不需要 3.0 那套「凭空造物 + WithdrawSlip + Rollback」（那是因为 3.0 的账本里不是真 Thing）。
    ///
    /// ── 剩下唯一的阻碍 ──
    /// <c>TradeDeal.InSellablePosition(t, out reason)</c> 要求 <c>t.Spawned</c>
    /// （特例只放了 Genepack / Book / OutfitStand），核心物品过不去 ⇒ 用 Prefix 放行。
    /// 原版自己也有同类先例：<c>Pawn_TraderTracker.ColonyThingsWillingToBuy</c> 的基因库分支
    /// 就是直接 yield 一个**未 Spawn、装在容器里**的 Genepack。
    ///
    /// ── 门槛（3.0 是"终端芯片植入物"，4.0 已无对应物） ──
    /// 远行队/据点交易发生在世界地图上，**没有"当前地图"可用**，
    /// 所以 B 项那套"必须有投影核心绑定"的判据在这里根本不成立。
    /// ⇒ 用一个设置开关（默认关）：开了就把**所有可用核心**（含投影核心授权的）的库存交给交易。
    ///
    /// ★ 4.0.1 起部分让位：原作者加了据点/远行队交易适配
    ///   （<c>Patch_SettlementTrade_FromCore</c> / <c>Patch_CaravanTrade_FromCore</c>，
    ///   共用 <c>WorldTradeFromCore.Append</c>，**无开关、常开**）。检测到它时本补丁
    ///   **不再**给 <c>Settlement_TraderTracker</c> / <c>Caravan_TraderTracker</c> 挂 postfix
    ///   —— 否则同一件核心物品会被两边各追加一次，交易列表出现重复条目。
    ///   原作者**没覆盖**的两类仍由本补丁负责（开了开关才生效）：
    ///   地图访客商人（Pawn_TraderTracker）与轨道商船（TradeShip，覆写了 ColonyThingsWillingToBuy，
    ///   给基类挂补丁管不到它）。作者补丁存在与否在静态构造时解析一次，回退到旧版 4.0 也能用。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_TradeFromCore
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.tradefromcore";

        /// <summary>
        /// 原 mod 4.0.1+ 是否自带据点/远行队交易适配。只在静态构造时解析一次；
        /// 为 true 时本补丁跳过那两类（避免重复追加），其余两类照旧。
        /// </summary>
        private static readonly bool authorWorldTradePresent =
            AccessTools.TypeByName("DigitalStorage.HarmonyPatches.WorldTradeFromCore") != null;

        /// <summary>设置开关（默认关）。开着才把核心库存交给交易窗口。</summary>
        internal static bool Enabled
        {
            get
            {
                DsPatchSettings s = DsPatchMod.Settings;
                return s != null && s.tradeFromCore;
            }
        }

        /// <summary>该物品是否装在数字存储核心里（未 Spawn 的容器物品）。</summary>
        internal static bool IsCoreItem(Thing t)
        {
            return t != null && t.ParentHolder is Building_StorageCore;
        }

        static Patch_TradeFromCore()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                int ok = 0;

                // ── 4 个 ITrader 实现：把核心库存追加进"玩家可卖物品" ──
                // 据点 / 远行队：4.0.1+ 作者已原生适配（常开），本补丁让位防重复；
                // 回退到旧版 4.0（无 WorldTradeFromCore）时仍由本补丁覆盖。
                if (authorWorldTradePresent)
                {
                    Log.Message("[数字存储补丁] 检测到原 mod 自带据点/远行队交易适配，"
                                + "这两类由原 mod 负责，本补丁不再挂载（防止交易列表重复）。");
                }
                else
                {
                    ok += PatchEnumerable(harmony, typeof(Settlement_TraderTracker), "据点交易");
                    ok += PatchEnumerable(harmony, typeof(Caravan_TraderTracker), "远行队 ↔ 远行队交易");
                }
                // 以下两类原作者没覆盖，始终由本补丁负责：
                ok += PatchEnumerable(harmony, typeof(Pawn_TraderTracker), "地图访客商人");
                ok += PatchEnumerable(harmony, typeof(TradeShip), "轨道商船");

                // ── TradeDeal.InSellablePosition：核心物品未 Spawn，原版会拒掉 ⇒ 放行 ──
                MethodInfo inSellable = AccessTools.Method(typeof(TradeDeal), "InSellablePosition");
                if (inSellable == null)
                {
                    Log.Error("[数字存储补丁] 找不到 TradeDeal.InSellablePosition，"
                              + "核心物品将无法进入交易列表（原 mod 可能已改结构）。");
                }
                else
                {
                    harmony.Patch(inSellable, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_TradeFromCore), nameof(Prefix_InSellablePosition))));
                    ok++;
                }

                Log.Message("[数字存储补丁] 已挂载：远行队 / 据点交易可使用数字核心库存（本次挂载 "
                            + ok + " 个入口"
                            + (authorWorldTradePresent ? "，据点/远行队由原 mod 负责" : "")
                            + "；功能默认关闭，在 Mod 设置里开启）。");
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 交易补丁安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int PatchEnumerable(Harmony harmony, System.Type type, string label)
        {
            MethodInfo m = AccessTools.Method(type, "ColonyThingsWillingToBuy");
            if (m == null)
            {
                Log.Warning("[数字存储补丁] 找不到 " + type.Name + ".ColonyThingsWillingToBuy，"
                            + label + "将看不到数字核心库存。");
                return 0;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(Patch_TradeFromCore), nameof(Postfix_ColonyThingsWillingToBuy))));
            return 1;
        }

        /// <summary>把所有可用核心的库存追加到"玩家可卖物品"里（复制一份再替换，不改原迭代器）。</summary>
        private static void Postfix_ColonyThingsWillingToBuy(Pawn playerNegotiator, ref IEnumerable<Thing> __result)
        {
            if (!Enabled || __result == null)
            {
                return;
            }

            List<Thing> combined;
            try
            {
                // 原返回值可能是惰性迭代器：先枚举进新列表，别在原地改。
                combined = new List<Thing>(__result);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[数字存储补丁] 枚举原有可卖物品失败（本次不加数字核心库存）："
                            + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return;
            }

            bool added = false;
            for (int i = 0; i < maps.Count; i++)
            {
                List<IHaulSource> sources =
                    maps[i]?.haulDestinationManager?.AllHaulSourcesListForReading;
                if (sources == null)
                {
                    continue;
                }
                for (int j = 0; j < sources.Count; j++)
                {
                    if (!(sources[j] is Building_StorageCore core) || !CoreFinder.IsUsable(core))
                    {
                        continue;
                    }
                    ThingOwner held = core.GetDirectlyHeldThings();
                    if (held == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < held.Count; k++)
                    {
                        Thing t = held[k];
                        if (t == null || t.Destroyed)
                        {
                            continue;
                        }
                        if (!combined.Contains(t))
                        {
                            combined.Add(t);
                            added = true;
                        }
                    }
                }
            }

            if (added)
            {
                __result = combined;
            }
        }

        /// <summary>核心物品未 Spawn，原版 <c>InSellablePosition</c> 会拒掉 ⇒ 直接放行。</summary>
        private static bool Prefix_InSellablePosition(Thing t, ref string reason, ref bool __result)
        {
            if (!Enabled || !IsCoreItem(t))
            {
                return true; // 走原版
            }
            reason = null;
            __result = true;
            return false;
        }
    }
}
