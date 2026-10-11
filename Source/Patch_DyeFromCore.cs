using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「染发」能用数字核心里的染料（v0.45，「上·2」批，F2）。
    ///
    /// ── 现象（用户实测）──
    /// 染料全存数字核心时，设置新发色后：**弹窗不提示缺染料（UI 认为够），但染不了** —— 小人取不到染料。
    ///
    /// ── 根因（★ 半通陷阱，见报告 ARCH-12）──
    /// 染料在原版里是**两条不一致的链路**：
    /// <list type="bullet">
    ///   <item>**计数/UI 端**：<c>Dialog_StylingStation</c> 用 <c>map.resourceCounter.GetCount(Dye)</c> 判断够不够
    ///         —— 而 4.0 的 <c>Patch_ResourceCounter_HaulSources</c> **把核心内容物计入了 resourceCounter**
    ///         ⇒ UI **能读到核心里的染料**（所以不报缺）；</item>
    ///   <item>**取用端**：<c>JobGiver_DyeHair.TryGiveJob</c> 用 <c>listerThings.ThingsOfDef(Dye)</c> 找染料
    ///         —— **看不到核心** ⇒ 拿不出来 ⇒ 染色失败。</item>
    /// </list>
    /// 这就是"UI 承诺、实际做不出"的半通陷阱：**不是误报，是真的做不出**。
    ///
    /// ── 做法 ──
    /// Postfix <c>TryGiveJob</c>：原版返回 null 时复刻原版前置（意识形态启用、已设新发色且与当前色不同），
    /// 找到风格台（原版同款查询），从可用数字核心取 1 份染料到脚下，构造**与原版同款**的
    /// <c>DyeHair</c> 作业（<c>count = 1</c>）。后续走到风格台染色全归原版。
    ///
    /// <para>⚠️ 验收要**两端都测**：UI 说够 **且** 发色真的改变 —— 半通陷阱只测一端会误判。</para>
    ///
    /// 关闭 = 完全回到原版行为（UI 仍说够，但染不了）。
    /// </summary>
    internal static class Patch_DyeFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.dyeFromCore;
        }

        [HarmonyPatch(typeof(JobGiver_DyeHair), "TryGiveJob")]
        internal static class Patch_TryGiveJob
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, ref Job __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                if (!ModsConfig.IdeologyActive)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || pawn.style == null || pawn.story == null)
                {
                    return;
                }

                // 复刻原版前置：已设新发色且与当前发色不同。
                Color? nextHairColor = pawn.style.nextHairColor;
                if (!nextHairColor.HasValue || nextHairColor.Value == pawn.story.HairColor)
                {
                    return;
                }

                try
                {
                    // 找风格台（原版同款查询）。
                    Thing station = GenClosest.ClosestThing_Global_Reachable(
                        pawn.Position, map,
                        map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.StylingStation),
                        PathEndMode.InteractionCell, TraverseParms.For(pawn), 9999f,
                        (Thing x) => !x.IsForbidden(pawn) && pawn.CanReserve(x));
                    if (station == null)
                    {
                        return;   // 没有风格台 ⇒ 原版也是 null
                    }

                    Thing dye = CoreContentAccess.ExtractOne(pawn, ThingDefOf.Dye, 1, map);
                    if (dye == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版
                    }

                    Job job = JobMaker.MakeJob(JobDefOf.DyeHair, station, dye);
                    job.count = 1;
                    __result = job;
                    FixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][染发] 地图上没有染料，已从数字核心取出 1 份到脚下 @ "
                                    + dye.Position);
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取染料（染发）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510324);
                }
            }
        }
    }
}
