using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「装填（可装填的武器 / 装备）」能从数字核心取弹药（v0.50，N2 收窄版）。
    ///
    /// ── 现象 ──
    /// 给**可装填的物品**（原版 Anomaly 里的几件可装填装备/武器）装填时，
    /// 若弹药**全存数字核心**，会判"凑不够"，装不上。
    ///
    /// ── 根因（反编译取证）──
    /// <code>
    /// // RimWorld.Utility/ReloadableUtility.cs
    /// public static List&lt;Thing&gt; FindEnoughAmmo(Pawn pawn, IntVec3 rootCell, IReloadableComp reloadable, bool forceReload)
    /// {
    ///     IntRange desiredQuantity = new IntRange(reloadable.MinAmmoNeeded(forceReload), reloadable.MaxAmmoNeeded(forceReload));
    ///     return RefuelWorkGiverUtility.FindEnoughReservableThings(pawn, rootCell, desiredQuantity, t =&gt; t.def == reloadable.AmmoDef);
    /// }
    /// </code>
    /// 下游 <c>FindEnoughReservableThings</c> 是**区域遍历型**（<c>RegionTraverser</c> + <c>region.ListerThings</c>）
    /// ⇒ 核心里的东西**不在任何区域里**，故凑不够。
    ///
    /// ── ★ v0.50 的两处收窄（据实测反馈）──
    /// <list type="number">
    ///   <item>**不再挂共用的 <c>FindEnoughReservableThings</c>，改挂装填入口 <c>FindEnoughAmmo</c>。**
    ///         原因：那个共用函数同时被**补燃料**（<c>FindBestFuel</c>，**4.0 的补丁早已覆盖**）使用，
    ///         挂在它上面会与 4.0 抢同一条链、**表现为"开关无区别"**（实测反馈）。
    ///         收窄到装填入口后，补燃料完全交回 4.0，本项只作用于装填。</item>
    ///   <item>**取量按小人负重上限收紧**（<c>MassUtility.CountToPickUpUntilOverEncumbered</c>），
    ///         不再一次把"所需全量"拽出来 —— 避免"取出的比小人能拿的还多、多出来的掉在地上"。</item>
    /// </list>
    ///
    /// ── 做法 ──
    /// Postfix：原版返回 null（凑不够）时，把（按负重收紧后的）缺量从核心取到 <c>rootCell</c>
    /// —— 必须是 <c>rootCell</c>（区域遍历的起点），落小人脚下没用；
    /// 然后**再调一次 <c>FindEnoughAmmo</c>**，这次区域遍历会带上刚落地的东西、用它自己的逻辑算。
    /// 用**重入标志**防递归。
    ///
    /// <para>⚠️ **本项不覆盖炮塔/运输仓的补燃料或装弹**：那些分别由 4.0 的
    /// <c>Patch_RefuelFromStorage</c> 与本补丁的 C1（<c>JobDriver_ManTurret.FindAmmoForTurret</c>）负责。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_ReloadAmmoFromCore
    {
        internal static int FixCount;
        internal static int ExtractCount;

        /// <summary>重入标志：复跑原方法时置位，使本 Postfix 立刻返回（防无限递归）。</summary>
        private static bool reentered;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.ammoFromCore;
        }

        [HarmonyPatch(typeof(ReloadableUtility), "FindEnoughAmmo")]
        internal static class Patch_FindEnoughAmmo
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, IntVec3 rootCell, IReloadableComp reloadable, bool forceReload,
                ref List<Thing> __result)
            {
                // 原版凑够了 / 正处在我们的复跑里 / 未启用 ⇒ 一律不动。
                if (__result != null || reentered || pawn == null || reloadable == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !rootCell.IsValid || reloadable.AmmoDef == null)
                {
                    return;
                }

                try
                {
                    // 与 ReloadableUtility 口径一致：至少要把"这次最少需要"的量凑出来。
                    int need = Math.Max(1, reloadable.MinAmmoNeeded(forceReload));

                    // ★ 落在 rootCell（区域遍历的起点）；取量在 ExtractOneAt 内部已按负重上限收紧。
                    Thing taken = CoreContentAccess.ExtractOneAt(
                        pawn, t => t.def == reloadable.AmmoDef && !t.IsForbidden(pawn), need, rootCell, map);

                    if (taken == null)
                    {
                        return;   // 核心没这种弹药 ⇒ 放行原版
                    }

                    ExtractCount++;

                    // ★ 复跑原方法（重入标志让本 Postfix 不再介入）：这次它自己会带上刚落地的东西。
                    reentered = true;
                    List<Thing> again;
                    try
                    {
                        again = ReloadableUtility.FindEnoughAmmo(pawn, rootCell, reloadable, forceReload);
                    }
                    finally
                    {
                        reentered = false;
                    }

                    if (again != null)
                    {
                        __result = again;
                        FixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][装填] 地图上凑不够，已从数字核心取出 "
                                        + taken.LabelShort + " x" + taken.stackCount + " 到 " + rootCell);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取「装填」弹药时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510380);
                }
            }
        }
    }
}
