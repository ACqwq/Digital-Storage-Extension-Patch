using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「给生化塑体舱（BiosculpterPod）补营养」能从数字核心取食物（v0.47，C4）。
    ///
    /// ── 现象 ──
    /// 生化塑体舱需要补充营养时，若食物**全存数字核心**，小人会说"没有食物"、不去送料。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/WorkGiver_HaulToBiosculpterPod.cs</c>）──
    /// <code>
    /// public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    /// {
    ///     ...门禁（意识形态 / 可预约 / 未拆除 / 通电 / 状态=LoadingNutrition / 未着火）...
    ///     if (comp.RequiredNutritionRemaining &gt; 0f)
    ///     {
    ///         if (FindNutrition(pawn, comp).Thing == null) { JobFailReason.Is("NoFood"); return false; }
    ///         return true;
    ///     }
    ///     return false;
    /// }
    ///
    /// private ThingCount FindNutrition(Pawn pawn, CompBiosculpterPod pod)
    /// {
    ///     Thing thing = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree),
    ///         PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f, Validator);   // ★ 只查已生成
    ///     if (thing == null) return default(ThingCount);
    ///     int num = Mathf.CeilToInt(pod.RequiredNutritionRemaining / thing.GetStatValue(StatDefOf.Nutrition));
    ///     return new ThingCount(thing, Mathf.Min(thing.stackCount, num));
    /// }
    /// </code>
    /// <c>FindNutrition</c> 带局部校验函数、返回结构体，**且会被内联** ⇒ 不挂它，挂两个 override。
    ///
    /// ── 做法 ──
    /// <list type="bullet">
    ///   <item><c>HasJobOnThing</c>（模板乙）：原版 false 时复刻其全部门禁；且"地图上没有**原版用得上的**食物"
    ///         时，核心里有能被该舱接受的食物就翻 true；</item>
    ///   <item><c>JobOnThing</c>（模板甲）：原版 null 时，从核心按**营养值折算的份数**取食物到脚下，
    ///         再用原版自己的 <c>HaulAIUtility.HaulToContainerJob</c> 造作业（搬运链全归原版）。</item>
    /// </list>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_BiosculpterFromCore
    {
        internal static int HasJobFixCount;
        internal static int JobFixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.biosculpterFromCore;
        }

        /// <summary>原版真正会接受的食物要求（只读）。</summary>
        private static bool PodAccepts(Pawn pawn, CompBiosculpterPod pod, Thing x)
        {
            return x != null && x.def != null
                   && !x.IsForbidden(pawn)
                   && pawn.CanReserve(x)
                   && pod.CanAcceptNutrition(x);
        }

        /// <summary>复刻原版 FindNutrition 的那次查找（只读）：返回 true 就说明原版自己找得到。</summary>
        private static bool HasUsableNutritionOnMap(Pawn pawn, Map map, CompBiosculpterPod pod)
        {
            return GenClosest.ClosestThingReachable(
                       pawn.Position, map, ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree),
                       PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                       x => PodAccepts(pawn, pod, x)) != null;
        }

        [HarmonyPatch(typeof(WorkGiver_HaulToBiosculpterPod), "HasJobOnThing")]
        internal static class Patch_HasJobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
            {
                if (__result || pawn == null || !Enabled())
                {
                    return;
                }

                if (!ModLister.CheckIdeology("Biosculpting"))
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !pawn.CanReserve(t, 1, -1, null, forced))
                {
                    return;
                }
                if (map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) != null)
                {
                    return;
                }

                CompBiosculpterPod pod = t.TryGetComp<CompBiosculpterPod>();
                if (pod == null || !pod.PowerOn
                    || pod.State != BiosculpterPodState.LoadingNutrition
                    || (!forced && !pod.autoLoadNutrition))
                {
                    return;
                }
                if (t.IsBurning() || pod.RequiredNutritionRemaining <= 0f)
                {
                    return;   // 不需要营养 ⇒ 原版拒的是别的原因
                }

                // 地图上有原版用得上的食物 ⇒ 原版会成功，false 是别因 ⇒ 不翻。
                if (HasUsableNutritionOnMap(pawn, map, pod))
                {
                    return;
                }

                try
                {
                    if (CoreContentAccess.CountMatching(map, x => PodAccepts(pawn, pod, x), 1) > 0)
                    {
                        __result = true;
                        HasJobFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][生化舱] 地图上没有可用食物，核心里有 ⇒ 该工作可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心食物（生化舱）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510352);
                }
            }
        }

        [HarmonyPatch(typeof(WorkGiver_HaulToBiosculpterPod), "JobOnThing")]
        internal static class Patch_JobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                CompBiosculpterPod pod = t.TryGetComp<CompBiosculpterPod>();
                if (pod == null || pod.RequiredNutritionRemaining <= 0f)
                {
                    return;
                }

                try
                {
                    ThingRequest foodReq = ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);

                    // 份数由"这件食物的营养值"决定（与原版 FindNutrition 的口径一致）。
                    Thing taken = CoreContentAccess.ExtractOne(
                        pawn,
                        x => foodReq.Accepts(x) && PodAccepts(pawn, pod, x),
                        x =>
                        {
                            float nutrition = x.GetStatValue(StatDefOf.Nutrition);
                            if (nutrition <= 0.0001f)
                            {
                                return 1;
                            }
                            return Mathf.CeilToInt(pod.RequiredNutritionRemaining / nutrition);
                        },
                        map);

                    if (taken == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版（返回 null）
                    }

                    // 与原版同款：走原版的搬运作业构造，之后整条搬运链全归原版。
                    Job job = HaulAIUtility.HaulToContainerJob(pawn, taken, t);
                    job.count = Mathf.Min(job.count, taken.stackCount);
                    __result = job;
                    JobFixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][生化舱] 地图上没有食物，已从数字核心取出 "
                                    + taken.LabelShort + " x" + job.count + " 到脚下 @ " + taken.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取食物（生化舱）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510353);
                }
            }
        }
    }
}
