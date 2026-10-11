using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复（配套 <see cref="Patch_PsychicRitualOfferingFromCore"/>）：
    /// **仪式启动界面**判定"祭品不足"导致仪式根本没法启动。
    ///
    /// <para><b>机制（原版反编译实锤）</b>：仪式启动前的阻挡检查由
    /// <c>PsychicRitualDef_InvocationCircle.BlockingIssues</c> 收尾时调用：</para>
    /// <code>
    /// if (RequiredOffering != null
    ///     &amp;&amp; !PsychicRitualDef.OfferingReachable(map, 收集者们, RequiredOffering, out var reachableCount))
    ///     yield return "PsychicRitualOfferingsInsufficient".Translate(...);   // ← 界面显示"祭品不足"
    /// </code>
    /// <para>而 <c>OfferingReachable</c> 只扫**地图上的物品**：</para>
    /// <code>
    /// list = map.listerThings.ThingsMatchingFilter(offering.filter);      // ★ listerThings 只含已生成的物品
    /// foreach (thing in list) foreach (pawn in pawns)
    ///     if (pawn.CanReserveAndReach(thing, ...) &amp;&amp; !forbidden &amp;&amp; !fogged) { num -= stackCount; ... }
    /// return num &lt;= 0f;
    /// </code>
    /// <para>数字核心里的祭品是未生成的容器内容物 ⇒ 计数恒为 0 ⇒ 永远"不足" ⇒ 界面报错、无法启动。</para>
    ///
    /// <para><b>修法</b>：Postfix 挂 <c>OfferingReachable</c> —— 原判定为"不足"时，
    /// 把数字核心里符合筛选的祭品数量补进 <c>reachableCount</c>；补足后判定改为"足够"，
    /// 仪式即可启动。</para>
    ///
    /// <para><b>为什么这里可以"补数量"，而实际取货不能</b>：本方法只回答"够不够用"这一个问题
    /// （用于界面显示与是否放行启动），实际取物由
    /// <see cref="Patch_PsychicRitualOfferingFromCore"/> 在收集阶段从核心取出并让小人捡起。
    /// 两者**共用同一个开关**（<c>ritualOfferingFromCore</c>）：开则"能启动 + 能取到"，
    /// 关则两者一并回到原版 —— 避免出现"能启动却永远取不到祭品"的坏组合。</para>
    /// </summary>
    [HarmonyPatch(typeof(PsychicRitualDef), "OfferingReachable")]
    internal static class Patch_PsychicRitualStartCheck
    {
        [HarmonyPostfix]
        private static void Postfix(
            Map map,
            List<Pawn> pawns,
            IngredientCount offering,
            ref int reachableCount,
            ref bool __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.ritualOfferingFromCore)
            {
                return;
            }

            // 地图上的祭品已经够 ⇒ 不干预。
            if (__result || map == null || offering == null || offering.filter == null)
            {
                return;
            }

            try
            {
                float need = offering.GetBaseCount() - reachableCount;
                if (need <= 0f)
                {
                    return;
                }

                int fromCores = CoreContentAccess.CountMatching(map, offering.filter, pawns, need);
                if (fromCores <= 0)
                {
                    return;
                }

                reachableCount += fromCores;
                if ((float)reachableCount >= offering.GetBaseCount())
                {
                    __result = true;   // 允许启动：实际取货由收集阶段的补丁从核心取出
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 仪式启动检查补核心祭品失败（本次不干预）："
                              + ex.GetType().Name + ": " + ex.Message, 8830155);
            }
        }
    }
}
