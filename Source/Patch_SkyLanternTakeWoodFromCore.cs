using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复：Ideology「天灯节」（Sky Lanterns）**取不到数字核心里的原木**。
    ///
    /// <para><b>机制（原版反编译实锤，1.7）</b>：天灯节第一阶段的职责
    /// （<c>Ideology/Defs/DutyDefs/Duties_Rituals.xml → PrepareSkyLanterns</c>）由
    /// <c>JobGiver_TakeCountToInventory</c>（def=WoodLog, count=4）派活 —— 它只在地图上找
    /// **已生成**的原木：<c>GenClosest.ClosestThingReachable(…, ThingRequest.ForDef(WoodLog), …)</c>，
    /// 找不到就返回 null、不派活。</para>
    /// <para>核心里的原木是未生成的容器内容物 ⇒ 永远找不到 ⇒ 小人拿不到木 ⇒ 放灯阶段
    /// <c>ActionOnTick_ReleaseLantern</c> 因"随身 4 木"不足而**静默不放灯**。</para>
    ///
    /// <para><b>修法</b>（同 <see cref="Patch_PsychicRitualOfferingFromCore"/> 的异象仪式模式）：
    /// Postfix 挂 <c>TryGiveJob</c> —— 仅在原方法返回空（地图上确实没有）时接手：
    /// 从数字核心取出一堆原木到小人脚下，再返回**原版同一个** <c>TakeCountToInventory</c> job
    /// 让它捡起来 —— 保持"木真的进了随身"的原版语义，绝不是"只把数字算进检查"。</para>
    ///
    /// <para><b>幂等</b>：取出后原木就在地图上了，下一次原方法自己就能看到（返回 job 后本 Postfix
    /// 因 <c>__result != null</c> 不再干预）。</para>
    ///
    /// <para><b>范围</b>：本 giver 在整个原版数据里只被天灯节职责链使用；这里再用
    /// <c>def == WoodLog</c> 收窄一道，绝不干涉其它来源的调用（mod 调用也拦不着）。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_TakeCountToInventory), "TryGiveJob")]
    internal static class Patch_SkyLanternTakeWoodFromCore
    {
        [HarmonyPostfix]
        private static void Postfix(JobGiver_TakeCountToInventory __instance, Pawn pawn, ref Job __result)
        {
            if (__result != null || __instance == null || pawn == null || pawn.MapHeld == null)
            {
                return;   // 原方法已找到（地图上有）⇒ 完全不干预
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.skyLanternFromCore)
            {
                return;
            }

            // 全原版仅天灯节使用该 giver；再用 def 收窄，防其它来源被误服务。
            if (__instance.def != ThingDefOf.WoodLog)
            {
                return;
            }

            try
            {
                int toTake = Math.Max(__instance.count - pawn.inventory.Count(ThingDefOf.WoodLog), 0);
                if (toTake <= 0)
                {
                    return;
                }

                // 镜像原版"单堆足够"语义（vanilla 也要求同一堆 stackCount ≥ toTake）。
                Thing pick = CoreContentAccess.FindInCoresWhere(
                    pawn.MapHeld,
                    t => t.def == ThingDefOf.WoodLog,
                    new List<Pawn> { pawn },
                    toTake);
                if (pick == null)
                {
                    return;
                }

                int take = Math.Min(toTake, pick.stackCount);
                Thing extracted = CoreContentAccess.ExtractFromCoreCrossVersion(
                    pick, take, pawn.PositionHeld, pawn.MapHeld);
                if (extracted == null)
                {
                    return;
                }

                Job job = JobMaker.MakeJob(JobDefOf.TakeCountToInventory, extracted);
                job.count = Math.Min(take, extracted.stackCount);
                __result = job;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 天灯节从核心取木失败（本次不干预）："
                              + ex.GetType().Name + ": " + ex.Message, 8830161);
            }
        }
    }
}
