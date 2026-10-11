using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「药物暴食（Binging）」能用数字核心里的成瘾品（v0.45，D4 + N1）。
    ///
    /// ── 现象（用户实测）──
    /// 成瘾品全存进数字核心后：① 小人不会去拿核心里的成瘾品；② 即便用开发模式/控制台
    /// **强制触发药物暴食的精神状态，也触发不了**（会被直接判定为"附近没药"）。
    ///
    /// ── 根因（反编译取证）──
    /// <list type="number">
    ///   <item>D4 <c>JobGiver_BingeDrug.BestIngestTarget</c>：
    ///         <c>GenClosest.ClosestThingReachable(..., ThingRequestGroup.Drug, ...)</c> 不带钩子 ⇒ 看不到核心；</item>
    ///   <item>N1 <c>AddictionUtility.CanBingeOnNow</c>：<c>pawn.Map.listerThings.ThingsInGroup(Drug)</c> 直接查，
    ///         **返回 bool** —— 这是"能不能进入暴食"的**触发门**。</item>
    /// </list>
    /// 两者是**两条不同的链路**：N1 决定"进不进得了这个精神状态"，D4 决定"进去后找不找得到药"。
    /// **只修其一都不完整**，故合并为一个开关下的两处 Postfix。
    /// （注：4.0 覆盖了按政策服药的 <c>JobGiver_TakeDrugsForDrugPolicy</c> 等，但**都不是**这两处。）
    ///
    /// ── 做法 ──
    /// <list type="bullet">
    ///   <item><c>CanBingeOnNow</c>：原版返回 false 时，仅在 <c>chemical.canBinge</c>、已生成、
    ///         且核心里有"化学物质匹配 + 类别匹配"的药时翻成 true（判据镜像原版：类别用 <c>==</c> 或 Any）；</item>
    ///   <item><c>BestIngestTarget</c>：原版返回 null 时，从当前暴食精神状态取出 <c>chemical</c>/<c>drugCategory</c>，
    ///         在核心里找同化学物质 + 同类别、**且不会致过量**的药（镜像原版 <c>0.786f</c> 判据），按需取 1 到脚下。</item>
    /// </list>
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_BingeDrugFromCore
    {
        internal static int TriggerFixCount;   // N1
        internal static int TargetFixCount;    // D4

        /// <summary>原版 <c>JobGiver_BingeDrug.OverdoseSeverityToAvoid</c>（private const 0.786f），镜像用。</summary>
        private const float OverdoseSeverityToAvoid = 0.786f;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.bingeDrugFromCore;
        }

        // ════════════════════════════ N1 · 暴食触发门 ════════════════════════════

        [HarmonyPatch(typeof(AddictionUtility), "CanBingeOnNow")]
        internal static class Patch_CanBingeOnNow
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ChemicalDef chemical, DrugCategory drugCategory, ref bool __result)
            {
                if (__result || pawn == null || chemical == null || !Enabled())
                {
                    return;
                }

                // 镜像原版：不能暴食的化学物质、未生成 ⇒ 原版就是对的，不翻。
                if (!chemical.canBinge || !pawn.Spawned)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                try
                {
                    bool matched = CoreContentAccess.CountMatching(map, t =>
                    {
                        if (t.def?.ingestible == null)
                        {
                            return false;
                        }
                        // 原版此处用「类别相等」或 Any（与 D4 的 IncludedIn 略有不同，按原版各自镜像）。
                        if (drugCategory != DrugCategory.Any && t.def.ingestible.drugCategory != drugCategory)
                        {
                            return false;
                        }
                        CompDrug cd = t.TryGetComp<CompDrug>();
                        return cd != null && cd.Props.chemical == chemical;
                    }, 1) > 0;

                    if (matched)
                    {
                        __result = true;
                        TriggerFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][药物暴食] 地图上没有可用药，核心里有 ⇒ 允许进入暴食。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心成瘾品（暴食触发）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510313);
                }
            }
        }

        // ════════════════════════════ D4 · 暴食取药 ════════════════════════════

        [HarmonyPatch(typeof(JobGiver_BingeDrug), "BestIngestTarget")]
        internal static class Patch_BestIngestTarget
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ref Thing __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                if (!(pawn.MentalState is MentalState_BingingDrug binge))
                {
                    return;
                }

                ChemicalDef chemical = binge.chemical;
                DrugCategory drugCategory = binge.drugCategory;
                if (chemical == null)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                try
                {
                    Hediff overdose = pawn.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.DrugOverdose);

                    Thing drug = CoreContentAccess.ExtractOne(pawn, t =>
                    {
                        if (t.def?.ingestible == null)
                        {
                            return false;
                        }
                        // 类别：原版用 IncludedIn（= 枚举值大小比较）。
                        if (!t.def.ingestible.drugCategory.IncludedIn(drugCategory))
                        {
                            return false;
                        }
                        CompDrug cd = t.TryGetComp<CompDrug>();
                        if (cd == null || cd.Props.chemical != chemical)
                        {
                            return false;
                        }
                        // 镜像原版：会致过量则跳过。
                        if (overdose != null && cd.Props.CanCauseOverdose
                            && overdose.Severity + cd.Props.overdoseSeverityOffset.max >= OverdoseSeverityToAvoid)
                        {
                            return false;
                        }
                        return true;
                    }, 1, map);

                    if (drug != null)
                    {
                        __result = drug;
                        TargetFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][药物暴食] 已从数字核心取出成瘾品到脚下 @ " + drug.Position);
                        }
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取成瘾品（暴食）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510314);
                }
            }
        }
    }
}
