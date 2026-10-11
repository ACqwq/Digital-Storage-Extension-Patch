using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让血族 / 血原质依赖者能从数字核心里取血原质包回补血原质（v0.45）。
    ///
    /// ── 现象（用户实测）──
    /// 血原质见底时，小人**不会去拿数字核心里的血原质包食用**（血原质一直上不来）。
    /// 附注：本环境未出现"血族优先吸囚犯"——那是**条件未满足**（本图没有殖民者囚犯），非异常。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/JobGiver_GetHemogen.cs</c>）──
    /// <code>
    /// private Thing GetHemogenPack(Pawn pawn)
    /// {
    ///     ...
    ///     return GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map,
    ///         pawn.Map.listerThings.ThingsOfDef(ThingDefOf.HemogenPack),   // ★ 只索引已生成的 Thing
    ///         PathEndMode.OnCell, TraverseParms.For(pawn), 9999f, ...);
    /// }
    /// </code>
    /// 血包全在核心里 ⇒ <c>GetHemogenPack</c> 返回 null ⇒ <c>TryGiveJob</c> 返回 null ⇒ 小人不去取。
    ///
    /// ── 为什么挂 <c>TryGiveJob</c> 而不是一行的 <c>GetHemogenPack</c> ──
    /// <c>GetHemogenPack</c> 体量偏小，有被 Mono JIT 内联的风险（挂上会静默失效）。
    /// <c>TryGiveJob</c> 是 <c>ThinkNode_JobGiver</c> 的 override，挂得住，
    /// 且能直接在"原版拿不出作业"时补一个**与原版同款**的 <c>Ingest</c> 作业。
    ///
    /// ── 做法 ──
    /// Postfix <c>TryGiveJob</c>：<c>__result == null</c> 时复刻原版后半段（血包这条路）——
    /// 校验 <c>hemogenPacksAllowed</c> / <c>ShouldConsumeHemogenNow</c>，算出需要几个包
    /// （<c>floor((Max - Value) / HemogenPackHemogenGain)</c>），从可用核心**按需**取到脚下，
    /// 再照原版构造 <c>Ingest</c> 作业（<c>count = min(堆数, 需要量)</c>、<c>ingestTotalCount = true</c>）。
    /// 「血族先吸囚犯」那条分支由原版负责：它给出作业时我们一字不动。
    ///
    /// 关闭 = 完全回到原版行为（血包只在核心 ⇒ 不去取）。
    /// </summary>
    internal static class Patch_GetHemogenFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.hemogenPackFromCore;
        }

        [HarmonyPatch(typeof(JobGiver_GetHemogen), "TryGiveJob")]
        internal static class Patch_TryGiveJob
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ref Job __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                if (!ModsConfig.BiotechActive)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                // 复刻原版后半段的前置条件（任一不满足即等同原版结果）。
                Gene_Hemogen gene = pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>();
                if (gene == null || !gene.ShouldConsumeHemogenNow() || !gene.hemogenPacksAllowed)
                {
                    return;
                }

                float gain = JobGiver_GetHemogen.HemogenPackHemogenGain;
                if (gain <= 0f)
                {
                    return;
                }

                try
                {
                    int num = Mathf.FloorToInt((gene.Max - gene.Value) / gain);
                    if (num <= 0)
                    {
                        return;
                    }

                    Thing pack = CoreContentAccess.ExtractOne(pawn, ThingDefOf.HemogenPack, num, map);
                    if (pack == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版
                    }

                    Job job = JobMaker.MakeJob(JobDefOf.Ingest, pack);
                    job.count = Mathf.Min(pack.stackCount, num);
                    job.ingestTotalCount = true;
                    __result = job;
                    FixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][血原质] 地图上没有血包，已从数字核心取 "
                                    + job.count + " 个到脚下 @ " + pack.Position);
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取血原质包时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510312);
                }
            }
        }
    }
}
