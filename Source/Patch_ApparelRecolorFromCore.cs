using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「给穿戴中的衣物改色（RecolorApparel）」能用数字核心里的染料（v0.46b，染料同族 ①）。
    ///
    /// ── 现象（用户实测）──
    /// 染料全存数字核心时，**完全没有取染料的动作、也没有报错** —— 因为**菜单项压根不出现**。
    ///
    /// ── 根因（反编译取证）──
    /// <list type="number">
    ///   <item><c>JobGiver_OptimizeApparel.TryCreateRecolorJob(pawn, out job, dryRun)</c> 内部用
    ///         <c>pawn.Map.listerThings.ThingsOfDef(Dye)</c> 凑染料队列 ⇒ **看不到核心**；</item>
    ///   <item>★ 更关键的是 <c>Building_StylingStation.GetFloatMenuOptions</c>：
    ///         <code>
    ///         if (JobGiver_OptimizeApparel.TryCreateRecolorJob(selPawn, out var _, dryRun: true))
    ///             yield return new FloatMenuOption("RecolorApparel", ...);
    ///         </code>
    ///         它先用 **<c>dryRun: true</c> 做只读检查**来决定"改衣物颜色"这个菜单项**要不要显示**。
    ///         染料只在核心里 ⇒ 这次检查返回 false ⇒ **选项不出现 ⇒ 玩家点不到**（所以"连取染料都没有"）。</item>
    /// </list>
    ///
    /// ── 做法（两侧分开处理，这是本项的重点）──
    /// <list type="bullet">
    ///   <item>**Prefix（<c>dryRun == false</c>，真正要干活时）**：必要时把染料取一份到小人脚下
    ///         （"让染料先出现在地上"），原版随后的 <c>listerThings</c> 自然命中，队列与作业**一字不改**；</item>
    ///   <item>**Postfix（<c>dryRun == true</c>，只读检查时）**：**不取货**，但若"只差染料（在核心里）"，
    ///         就把这次检查结果翻成 true —— 这样菜单项/按钮才会出现，玩家点下去后才走上面那条真取货的路。</item>
    /// </list>
    ///
    /// <para>⚠️ **v0.46 首版只做了 Prefix 且直接跳过 dryRun** ⇒ 菜单项不出现 ⇒ 表现为"改色无效且无报错"。
    /// 这正是教训：**"只读检查"也是判据的一部分，光补"取货那一刻"不够，还要补"要不要给入口"**。</para>
    ///
    /// 关闭 = 完全回到原版行为（选项不出现 / 拿不到染料）。
    /// </summary>
    internal static class Patch_ApparelRecolorFromCore
    {
        /// <summary>真正从核心取出染料的次数。</summary>
        internal static int FixCount;

        /// <summary>仅让"改色"入口出现的次数（dryRun 侧的修正，不取货）。</summary>
        internal static int MenuFixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.dyeFromCore;
        }

        /// <summary>核心里有没有染料（只读）。</summary>
        private static bool CoreHasDye(Map map)
        {
            return map != null
                   && CoreContentAccess.CountMatching(map, t => t.def == ThingDefOf.Dye, 1) > 0;
        }

        /// <summary>需要改色的衣物件数（与原版 tmpApparelToRecolor 的口径一致）。</summary>
        private static int NeedCount(Pawn pawn)
        {
            int need = 0;
            if (pawn?.apparel?.WornApparel == null)
            {
                return 0;
            }
            foreach (Apparel a in pawn.apparel.WornApparel)
            {
                if (a.DesiredColor.HasValue)
                {
                    need++;
                }
            }
            return need;
        }

        /// <summary>地图上有没有"原版真的用得上"的染料（可达 + 未被禁止）。</summary>
        private static bool HasUsableDyeOnMap(Pawn pawn, Map map)
        {
            List<Thing> onMap = map?.listerThings?.ThingsOfDef(ThingDefOf.Dye);
            if (onMap == null)
            {
                return false;
            }
            for (int i = 0; i < onMap.Count; i++)
            {
                Thing d = onMap[i];
                if (d == null || d.Destroyed)
                {
                    continue;
                }
                if (d.IsForbidden(pawn))
                {
                    continue;
                }
                if (!pawn.CanReach(d, PathEndMode.Touch, Danger.Some))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryCreateRecolorJob")]
        internal static class Patch_TryCreateRecolorJob
        {
            // ══════════ ① 真要干活：必要时把染料取到脚下 ══════════

            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn, bool dryRun)
            {
                // dryRun = 只读检查（决定菜单/按钮要不要出现）⇒ 绝不取货，交给下面的 Postfix。
                if (dryRun || pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                // 地图上已有可用染料 ⇒ 原版自己能凑出队列，不干预。
                if (HasUsableDyeOnMap(pawn, map))
                {
                    return;
                }

                int need = NeedCount(pawn);
                if (need <= 0)
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
                        Log.Message("[数字存储补丁][衣物改色] 地图上没有染料，已从数字核心取出 "
                                    + taken.stackCount + " 份到脚下 @ " + taken.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取染料（衣物改色）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510341);
                }
            }

            // ══════════ ② 只读检查（dryRun）：只差染料时让入口出现，不取货 ══════════

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, bool dryRun, ref bool __result)
            {
                if (__result || !dryRun || pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                // 已经因为别的原因失败（没装意识形态 / 没有要改色的衣物 / 没有风格台）⇒ 不该我们翻。
                if (!ModsConfig.IdeologyActive)
                {
                    return;
                }
                if (pawn.apparel == null || !pawn.apparel.AnyApparelNeedsRecoloring)
                {
                    return;
                }

                // 地图上本来就有可用染料却仍失败 ⇒ 原因是别的（如没有风格台）⇒ 不翻。
                if (HasUsableDyeOnMap(pawn, map))
                {
                    return;
                }

                try
                {
                    if (CoreHasDye(map))
                    {
                        __result = true;   // 只表示"能做"，作业仍是 null（与 vanilla dryRun 成功时的形态一致）
                        MenuFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][衣物改色] 染料在数字核心里 ⇒ 让「改衣物颜色」入口可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心染料（衣物改色入口）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510342);
                }
            }
        }
    }
}
