using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「麦芽汁」能从数字核心倒进发酵桶（v0.45，「上·2」批，C3）。
    ///
    /// ── 现象（用户实测）──
    /// 麦芽汁全存数字核心 ⇒ 小人读不到核心里的麦汁，**倒不进发酵桶**（提示没有麦汁），酿酒链断掉。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/WorkGiver_FillFermentingBarrel.cs</c>）──
    /// <code>
    /// public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    /// {
    ///     ...（桶状态/温度/可预约/未拆除）...
    ///     if (FindWort(pawn, building_FermentingBarrel) == null) { JobFailReason.Is(NoWortTrans); return false; }
    ///     if (t.IsBurning()) return false;
    ///     return true;
    /// }
    ///
    /// private Thing FindWort(Pawn pawn, Building_FermentingBarrel barrel)
    /// {
    ///     return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForDef(ThingDefOf.Wort), ... 9999f, validator);   // ★ 没传 lookInHaulSources
    /// }
    /// </code>
    /// <c>FindWort</c> 是**一行的私有方法**（会被 JIT 内联）⇒ **不挂它，改挂两个 override**。
    ///
    /// ── 做法 ──
    /// <list type="number">
    ///   <item><c>HasJobOnThing</c>（模板乙·保守翻盘）：原版 false 且"地图上没有麦汁"时，
    ///         复刻原版全部前置条件（桶未发酵/有余量/温度合格/可预约/未拆除/未着火），
    ///         通过且核心有麦汁 ⇒ 翻成 true；</item>
    ///   <item><c>JobOnThing</c>（模板甲）：作业**缺麦汁目标**时，从可用数字核心按**桶还需的量**取一份到小人脚下，
    ///         构造/补上 <c>FillFermentingBarrel</c> 作业的麦汁目标（作业的 <c>count</c> 由驱动自己按
    ///         <c>SpaceLeftForWort</c> 设置，无需在此赋值）。</item>
    /// </list>
    ///
    /// <para>⚠️ **本项独有的坑**：原版 <c>JobOnThing</c> 找不到麦汁时**仍会返回作业**
    /// （<c>JobMaker.MakeJob(def, t, null)</c> ⇒ targetB 为 null），**不是返回 null**。
    /// 因此这里**不能用 <c>__result != null</c> 早退** —— 否则会在 targetB=null 的作业上什么也不补，
    /// 作业拿去预约 null 麦汁 ⇒ 报 <c>TryMakePreToilReservations() returned false</c> 黄字
    /// （v0.45 首次实测即此现象，v0.45a 修正）。判据是"作业是否缺麦汁目标"。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_FillBarrelFromCore
    {
        internal static int HasJobFixCount;
        internal static int JobFixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.fillBarrelFromCore;
        }

        // ════════════════════════════ ① 可用性判定 ════════════════════════════

        [HarmonyPatch(typeof(WorkGiver_FillFermentingBarrel), "HasJobOnThing")]
        internal static class Patch_HasJobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
            {
                if (__result || pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !(t is Building_FermentingBarrel barrel))
                {
                    return;
                }

                // 复刻原版前置条件（顺序同原版；任一不满足即等同原版结果，不翻）。
                if (barrel.Fermented || barrel.SpaceLeftForWort <= 0)
                {
                    return;
                }

                CompProperties_TemperatureRuinable cpt = barrel.def.GetCompProperties<CompProperties_TemperatureRuinable>();
                if (cpt != null)
                {
                    float ambient = barrel.AmbientTemperature;
                    if (ambient < cpt.minSafeTemperature + 2f || ambient > cpt.maxSafeTemperature - 2f)
                    {
                        return;
                    }
                }

                if (!pawn.CanReserve(t, 1, -1, null, forced))
                {
                    return;
                }
                if (map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) != null)
                {
                    return;
                }

                // 地图上其实有可见麦汁却仍被拒 ⇒ 拒的是别的原因 ⇒ 不翻。
                if (map.listerThings.ThingsOfDef(ThingDefOf.Wort).Count > 0)
                {
                    return;
                }

                // 原版把"着火"判在麦汁之后；翻盘前必须排除，避免对着火的桶派活。
                if (t.IsBurning())
                {
                    return;
                }

                try
                {
                    if (CoreContentAccess.CountMatching(map, x => x.def == ThingDefOf.Wort, 1) > 0)
                    {
                        __result = true;
                        HasJobFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][发酵桶] 地图上没有麦汁，核心里有 ⇒ 该工作可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心麦汁时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510322);
                }
            }
        }

        // ════════════════════════════ ② 作业构造 ════════════════════════════

        [HarmonyPatch(typeof(WorkGiver_FillFermentingBarrel), "JobOnThing")]
        internal static class Patch_JobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
            {
                if (pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !(t is Building_FermentingBarrel barrel))
                {
                    return;
                }

                // ★ 原版 JobOnThing 在本场景**总会返回一个作业**：找不到麦汁时 targetB 为 null。
                //   ⇒ 不能用 "__result != null" 早退 —— 否则会在一个 targetB=null 的作业上什么也不补，
                //     作业拿去预约 null 麦汁 ⇒ 报 "TryMakePreToilReservations() returned false" 黄字。
                //   判据改为"作业是否缺麦汁目标"。
                Job job = __result;
                if (job != null && job.GetTarget(TargetIndex.B).Thing != null)
                {
                    return;   // 地图上已找到麦汁 ⇒ 一字不动
                }

                try
                {
                    int want = barrel.SpaceLeftForWort;
                    if (want <= 0)
                    {
                        return;
                    }

                    // 按"桶还需的量"取（与原版驱动把 count 设为 SpaceLeftForWort 的口径一致）。
                    Thing wort = CoreContentAccess.ExtractOne(pawn, ThingDefOf.Wort, want, map);
                    if (wort == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版（作业维持 targetB=null）
                    }

                    if (job == null)
                    {
                        job = JobMaker.MakeJob(JobDefOf.FillFermentingBarrel, t, wort);
                        __result = job;
                    }
                    else
                    {
                        job.SetTarget(TargetIndex.B, wort);   // 补上缺失的麦汁目标
                    }
                    JobFixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][发酵桶] 地图上没有麦汁，已从数字核心取出 "
                                    + wort.stackCount + " 份到脚下 @ " + wort.Position);
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取麦汁时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510323);
                }
            }
        }
    }
}
