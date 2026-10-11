using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「按固定材料清单找料」也能认数字核心（v0.51，方尖碑执行侧 + 机械搬运体补料）。
    ///
    /// ── 涉及的功能 ──
    /// <list type="bullet">
    ///   <item>**方尖碑停用**（`CompObeliskDeactivationInteractor.OrderDeactivation`）—— 取 N 个碎片；</item>
    ///   <item>**机械搬运体补料**（`WorkGiver_HaulResourcesToCarrier`）—— 给 Carrier 机补固定材料；</item>
    ///   <item>通用 `InteractionUtility.OrderInteraction`（可选物品）。</item>
    /// </list>
    ///
    /// ── 根因（反编译取证，<c>Verse.AI/HaulAIUtility.cs</c>）──
    /// <code>
    /// public static List&lt;Thing&gt; FindFixedIngredientCount(Pawn pawn, ThingDef def, int maxCount)
    /// {
    ///     Region region = pawn.Position.GetRegion(pawn.Map);
    ///     ThingListProcessor(pawn.Position.GetThingList(region.Map), region);
    ///     if (countFound &lt; maxCount)
    ///         RegionTraverser.BreadthFirstTraverse(region, ..., (Region r) =&gt; ThingListProcessor(r.ListerThings.ThingsMatching(HaulableEver), r), 99999);
    ///     return chosenThings;
    /// }
    /// </code>
    /// ★ **区域遍历型**（`RegionTraverser` + `region.ListerThings`，与 N2 的 `FindEnoughReservableThings` 同族）
    /// ⇒ 核心里的东西**不在任何区域里**，找不到。
    ///
    /// ── 做法（★ 与 N2 同款）──
    /// Postfix：原版**返回空表**时，把缺的量从核心取到 **`pawn.Position`**
    /// —— 必须是这里（`FindFixedIngredientCount` 的区域遍历正是从 `pawn.Position.GetRegion` 起步），
    /// 落别处遍历够不到；然后**复跑一次原方法**，用它自己的区域逻辑算出结果。
    /// **重入标志**防递归；取量在 `ExtractOne` 内部**按负重上限收紧**。
    ///
    /// <para>**为什么敢挂这个共用工具**：它的 3 类调用点（方尖碑 / 通用 OrderInteraction / 机械搬运体）
    /// **全是"执行侧"**（都要拿这些东西去造作业），**没有只读检查** ⇒ 取货不会污染任何判定。
    /// （对比：G1 的 `TryFindReserveAndReachableOfDef` 两用，所以那边按调用点分开处理。）</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_FixedIngredientFromCore
    {
        internal static int FixCount;
        internal static int ExtractCount;

        /// <summary>重入标志：复跑原方法时置位，使本 Postfix 立刻返回（防无限递归）。</summary>
        private static bool reentered;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.fixedIngredientFromCore;
        }

        [HarmonyPatch(typeof(HaulAIUtility), "FindFixedIngredientCount")]
        internal static class Patch_FindFixedIngredientCount
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ThingDef def, int maxCount, ref List<Thing> __result)
            {
                // 原版找到了 / 正处在我们的复跑里 / 未启用 ⇒ 一律不动。
                if (reentered || pawn == null || def == null || maxCount <= 0 || !Enabled())
                {
                    return;
                }
                if (__result != null && __result.Count > 0)
                {
                    return;   // 原版已经凑到了
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                try
                {
                    // ★ 落在 pawn.Position（区域遍历的起点）；取量已按负重上限收紧。
                    Thing taken = CoreContentAccess.ExtractOne(pawn, def, maxCount, map);
                    if (taken == null)
                    {
                        return;   // 核心也没这种料 ⇒ 放行原版
                    }

                    ExtractCount++;

                    // ★ 复跑原方法（重入标志让本 Postfix 不再介入）：这次它自己会带上刚落地的东西。
                    reentered = true;
                    List<Thing> again;
                    try
                    {
                        again = HaulAIUtility.FindFixedIngredientCount(pawn, def, maxCount);
                    }
                    finally
                    {
                        reentered = false;
                    }

                    if (again != null && again.Count > 0)
                    {
                        __result = again;
                        FixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][固定材料] 地图上凑不到，已从数字核心取出 "
                                        + taken.LabelShort + " x" + taken.stackCount + " 到脚下 @ " + taken.Position);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取「固定材料」时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510393);
                }
            }
        }
    }
}
