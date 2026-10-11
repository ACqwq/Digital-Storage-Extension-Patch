using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「按药品政策把药备到背包」也能用数字核心里的库存（v0.45，A3）。
    ///
    /// ── 现象（用户实测）──
    /// 给小人设了药品政策里的「随身携带 N 个某某」（预支数量）后，若该药全存数字核心，
    /// 小人**不会从核心取出任何一份**装进背包。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/JobGiver_MoveDrugsToInventory.cs</c>）──
    /// <code>
    /// private Thing FindDrugFor(Pawn pawn, ThingDef drugDef, int desired)
    /// {
    ///     return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForDef(drugDef), PathEndMode.ClosestTouch,
    ///         TraverseParms.For(pawn), 9999f, x =&gt; DrugValidator(pawn, x, desired));   // ★ 没传 lookInHaulSources
    /// }
    /// </code>
    /// <c>FindDrugFor</c> 方法体只有一行 ⇒ **会被 Mono JIT 内联**，直接挂它会静默失效
    /// （本项目踩坑目录已记录）。故改挂体量足够的公共入口 <c>TryGiveJob</c>。
    ///
    /// ── 做法 ──
    /// Postfix <c>TryGiveJob</c>：<c>__result == null</c> 时复刻原版循环 ——
    /// 遍历 <c>pawn.drugs.CurrentPolicy</c>，找"允许带入背包"（<c>AllowedToTakeToInventory</c>）的项，
    /// 算出缺口 <c>takeToInventory - 背包里已有数量</c>，从可用数字核心**按需**取到脚下，
    /// 构造**与原版同款**的 <c>TakeInventory</c> 作业（<c>count = min(缺口, 堆数)</c>）。
    /// 后续进入背包的全过程归原版。
    ///
    /// <para>⚠️ **已知限制（2026-10-08 实测确认）：本项对勾了「定时使用」的药不生效。**
    /// 原版 <c>Pawn_DrugPolicyTracker.AllowedToTakeToInventory</c> 开头就是
    /// <c>if (drugPolicyEntry.allowScheduled) return false;</c> —— 把定时服用的药**直接排除**。
    /// 而"吃完药顺手补齐携带量"那条链（<c>JobDriver_Ingest.TakeExtraIngestibles</c>，
    /// 其 <c>takeExtraIngestibles</c> **全库只有 <c>DrugAIUtility</c> 一处赋值**）又
    /// **只在 <c>allowScheduled</c> 为真时才触发**。
    /// ⇒ **两条链互斥**：定时药只能靠后者、非定时药只能靠本项。
    /// （后者的补丁曾实现过，v0.46a 后**移除** —— 实测不稳且与本项目标重复）
    /// ⇒ **结论：药品政策里勾了「定时使用」的药，目前没有从数字核心补货的路径**（已知限制，不是 bug）。</para>
    ///
    /// 关闭 = 完全回到原版行为（背包补不上）。
    /// </summary>
    internal static class Patch_MoveDrugsToInventoryFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.moveDrugsToInventoryFromCore;
        }

        [HarmonyPatch(typeof(JobGiver_MoveDrugsToInventory), "TryGiveJob")]
        internal static class Patch_TryGiveJob
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ref Job __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || pawn.inventory?.innerContainer == null)
                {
                    return;
                }

                Pawn_DrugPolicyTracker drugs = pawn.drugs;
                DrugPolicy policy = drugs?.CurrentPolicy;
                if (policy == null)
                {
                    return;
                }

                try
                {
                    for (int i = 0; i < policy.Count; i++)
                    {
                        ThingDef def = policy[i]?.drug;
                        if (def == null)
                        {
                            continue;
                        }

                        if (!drugs.AllowedToTakeToInventory(def))
                        {
                            continue;
                        }

                        int want = policy[i].takeToInventory
                                   - pawn.inventory.innerContainer.TotalStackCountOfDef(def);
                        if (want <= 0)
                        {
                            continue;   // 这一项不缺 ⇒ 看下一项（原版同款）
                        }

                        Thing taken = CoreContentAccess.ExtractOne(pawn, def, want, map);
                        if (taken == null)
                        {
                            continue;   // 核心也没有 ⇒ 看下一项
                        }

                        Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, taken);
                        job.count = Mathf.Min(want, taken.stackCount);
                        __result = job;
                        FixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][备药到背包] 地图上没有，已从数字核心取出："
                                        + taken.LabelShort + " x" + job.count
                                        + "（目标 " + policy[i].takeToInventory + "）@ " + taken.Position);
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心为「药品政策」备药到背包时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510315);
                }
            }
        }
    }
}
