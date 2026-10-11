using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「输注血原质（administer hemogen）」也能用数字核心里的血原质包（v0.45）。
    ///
    /// ── 现象（用户实测）──
    /// 血原质包全存进数字核心后，医生给「血原质依赖者」输注血原质时，
    /// **判定为"没有血原质"**：右键菜单里不出现该选项（强制也会被拒），且血包不会被自动取出。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/Workgiver_AdministerHemogen.cs</c>）──
    /// <code>
    /// public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    /// {
    ///     ...（前置条件）...
    ///     if (GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(ThingDefOf.HemogenPack),
    ///             PathEndMode.OnCell, TraverseParms.For(pawn), 9999f,
    ///             (Thing pack) =&gt; !pack.IsForbidden(pawn) &amp;&amp; pawn.CanReserve(pack)) == null)
    ///     {
    ///         JobFailReason.Is("NoIngredient".Translate(ThingDefOf.HemogenPack));
    ///         return false;                                   // ★ 只数地图上已生成的血包
    ///     }
    ///     return true;
    /// }
    ///
    /// public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    /// {
    ///     Thing thing = GenClosest.ClosestThingReachable(... ThingDefOf.HemogenPack ...);  // ★ 同一查询
    ///     if (thing != null) { Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, thing, pawn2); job.count = 1; return job; }
    ///     return null;
    /// }
    /// </code>
    /// <c>ClosestThingReachable</c> 的 <c>lookInHaulSources</c> 默认为 <c>false</c> ⇒ 看不到核心。
    /// 这与 B1（输血手术）同属「**判定为无**」型硬缺口：不是"找不到最近的"，而是功能直接不给用。
    ///
    /// ── 做法（两个 override 各挂 Postfix；**不挂私有 Find\***：那是一行体，会被 JIT 内联）──
    /// <list type="number">
    ///   <item><c>HasJobOnThing</c>（模板乙·保守翻盘）：原版返回 false 时，**逐条排除"原版其实是对的"的拒绝理由** ——
    ///         若地图上其实有可见血包（说明原版是因别的原因拒的）⇒ 不翻；再复刻原版前置条件
    ///         （目标基因/血原质水平/ShouldBeFed/允许血包/需要摄取/可预约），全部通过才在"核心有包"时把结果翻成 true；</item>
    ///   <item><c>JobOnThing</c>（模板甲）：原版返回 null 时，从可用数字核心取 1 个血包到医生脚下，
    ///         构造**与原版同款**的 <c>FeedPatient</c> 作业（<c>job.count = 1</c>），后续搬运/喂食全归原版。</item>
    /// </list>
    /// 关闭 = 完全回到原版行为（判定为无、不取包）。
    /// </summary>
    internal static class Patch_AdministerHemogenFromCore
    {
        internal static int HasJobFixCount;
        internal static int JobFixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.hemogenFeedFromCore;
        }

        // ════════════════════════════ ① 可用性判定 ════════════════════════════

        [HarmonyPatch(typeof(Workgiver_AdministerHemogen), "HasJobOnThing")]
        internal static class Patch_HasJobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
            {
                if (__result || pawn == null || !(t is Pawn target) || target == pawn)
                {
                    return;
                }

                if (!Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                // 复刻原版前置条件（顺序同原版；任一不满足即等同原版结果，不翻）。
                Gene_Hemogen gene = target.genes?.GetFirstGeneOfType<Gene_Hemogen>();
                if (gene == null)
                {
                    return;
                }
                if (gene.ValuePercent >= 0.95f)
                {
                    return;
                }
                if (!forced && gene.Value >= 0.25f)
                {
                    return;
                }
                if (!FeedPatientUtility.ShouldBeFed(target))
                {
                    return;
                }
                if (!gene.hemogenPacksAllowed)
                {
                    return;
                }
                if (!gene.ShouldConsumeHemogenNow())
                {
                    return;
                }
                if (!pawn.CanReserve(t, 1, -1, null, forced))
                {
                    return;
                }

                // 地图上其实有可见血包却仍被拒 ⇒ 说明拒的是别的原因（如 base 的其它判定）⇒ 不翻。
                if (map.listerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count > 0)
                {
                    return;
                }

                // ★ 到这里可确定：拒绝原因就是「地图上没有血包」。看核心。
                try
                {
                    if (CoreContentAccess.CountMatching(map, x => x.def == ThingDefOf.HemogenPack, 1) > 0)
                    {
                        __result = true;
                        HasJobFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][输注血原质] 地图上没有血包，核心里有 ⇒ 该工作可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 兜底失败不该把"不可用"升级成红字异常。
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心血原质（输注）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510310);
                }
            }
        }

        // ════════════════════════════ ② 作业构造 ════════════════════════════

        [HarmonyPatch(typeof(Workgiver_AdministerHemogen), "JobOnThing")]
        internal static class Patch_JobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
            {
                if (__result != null || pawn == null || !(t is Pawn target))
                {
                    return;
                }

                if (!Enabled())
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
                    // 取 1 个血包到医生脚下（原版作业要求 targetA 能在地图上被走到/搬起）。
                    Thing pack = CoreContentAccess.ExtractOne(pawn, ThingDefOf.HemogenPack, 1, map);
                    if (pack == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版（返回 null）
                    }

                    // 与原版同款构造：targetA = 血包，targetB = 目标小人，count = 1。
                    Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, pack, target);
                    job.count = 1;
                    __result = job;
                    JobFixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][输注血原质] 已从数字核心取出 1 个血原质包到医生脚下 @ "
                                    + pack.Position);
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取血原质包（输注）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510311);
                }
            }
        }
    }
}
