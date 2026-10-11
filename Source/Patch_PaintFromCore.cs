using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「涂装建筑 / 涂装地板」能用数字核心里的染料（v0.46，染料同族 ② ③）。
    ///
    /// ── 现象（与 F2 同源）──
    /// 染料全存数字核心时，涂装的**判定**说没有染料（`NoIngredient`），小人不去涂。
    ///
    /// ── 根因（反编译取证）──
    /// <list type="bullet">
    ///   <item><c>WorkGiver_PaintBuilding.ShouldPaintThing(pawn, t, forced, checkDye)</c>：
    ///         <c>checkDye</c> 为真时遍历 <c>listerThings.ThingsOfDef(Dye)</c>，找不到就返回
    ///         <c>NoIngredient</c> ⇒ <c>HasJobOnThing</c> 返回 false；</item>
    ///   <item><c>WorkGiver_PaintFloor.ShouldPaintCell(pawn, c, forced, checkDye)</c>：同上（第 125 行判 <c>checkDye</c>）；</item>
    ///   <item>两者的 <c>JobOnThing / JobOnCell</c> 又都靠 <c>PaintUtility.FindNearbyDyes</c>
    ///         （同样是 <c>listerThings</c>）来凑染料 ⇒ 取用端一样看不到核心。</item>
    /// </list>
    ///
    /// ── 做法（★ 不改判定，而是"让染料出现在地上"）──
    /// 在这两个 <c>ShouldPaint*</c> 上挂 **Prefix**：当
    /// <c>checkDye == true</c>（真正要查染料的那一侧）且**地图上一点染料都没有**、而核心里有，
    /// 就把染料取一份到小人脚下。原版随后的判定与 <c>FindNearbyDyes</c> 自然命中，
    /// 后面"走过去 → 拿起 → 涂"整条链完全归原版，**一字不改**。
    ///
    /// <para>⚠️ **必须只在 <c>checkDye == true</c> 时介入**：
    /// <c>JobOnThing / JobOnCell</c> 内部会**用 <c>checkDye: false</c> 循环调用上百次**
    /// （<c>WorkGiver_PaintBuilding.cs</c> 第 68 行、<c>PaintFloor</c> 第 64 行）——
    /// 若不判这个开关，一次作业就会把核心里的染料成堆掏出来。</para>
    ///
    /// <para>另：地图上**已经有**染料时不干预（<c>listerThings</c> 计数 &gt; 0），避免重复落地。</para>
    ///
    /// 关闭 = 完全回到原版行为（判定没有染料、不去涂）。
    /// </summary>
    internal static class Patch_PaintFromCore
    {
        internal static int FixCount;

        /// <summary>
        /// 一次往地上补多少染料。取原版"单次作业最多涂 10 个目标"的量级
        /// （<c>num2 = Mathf.Min(10, stackCount)</c>），不足时下一轮检查会再补。
        /// </summary>
        private const int DyePerTrip = 10;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.dyeFromCore;
        }

        /// <summary>必要时把核心里的染料取一份到小人脚下（供原版自己去找）。</summary>
        private static void EnsureDyeOnGround(Pawn pawn, Map map, int need)
        {
            if (pawn == null || map == null || need <= 0 || !Enabled())
            {
                return;
            }

            // 地图上已经有 ⇒ 原版自己能找到，不干预、不重复落地。
            if (map.listerThings.ThingsOfDef(ThingDefOf.Dye).Count > 0)
            {
                return;
            }

            try
            {
                Thing taken = CoreContentAccess.ExtractOne(pawn, ThingDefOf.Dye, need, map);
                if (taken == null)
                {
                    return;   // 核心也没有 ⇒ 放行原版
                }

                FixCount++;

                DsPatchSettings s = DsPatchMod.Settings;
                if (s != null && s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][涂装] 地图上没有染料，已从数字核心取出 "
                                + taken.stackCount + " 份到脚下 @ " + taken.Position);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 从数字核心取染料（涂装）时出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510340);
            }
        }

        // ════════════════════════════ ② 涂装建筑 ════════════════════════════

        [HarmonyPatch(typeof(WorkGiver_PaintBuilding), "ShouldPaintThing")]
        internal static class Patch_ShouldPaintThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn, Thing t, bool forced, bool checkDye)
            {
                // ★ 只有"真正要查染料"这一侧才补；checkDye=false 是 JobOnThing 的百次内部循环。
                if (!checkDye)
                {
                    return;
                }
                EnsureDyeOnGround(pawn, pawn?.Map, DyePerTrip);
            }
        }

        // ════════════════════════════ ③ 涂装地板 ════════════════════════════

        [HarmonyPatch(typeof(WorkGiver_PaintFloor), "ShouldPaintCell")]
        internal static class Patch_ShouldPaintCell
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn, IntVec3 c, bool forced, bool checkDye)
            {
                if (!checkDye)
                {
                    return;
                }
                EnsureDyeOnGround(pawn, pawn?.Map, DyePerTrip);
            }
        }
    }
}
