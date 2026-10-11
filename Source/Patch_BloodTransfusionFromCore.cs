using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「输血」手术把数字核心里的血原质包算在内（v0.44）。
    ///
    /// ── 现象 ──
    /// 血原质包全存进数字核心后，给失血的小人做输血手术时，
    /// 手术在列表里**直接不可用**（或可用但需要的包数算错，导致材料凑不齐）。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/Recipe_BloodTransfusion.cs</c>）──
    /// <code>
    /// public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
    /// {
    ///     if (thing.MapHeld == null) return false;
    ///     if (thing.MapHeld.listerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count == 0)   // ★ 只数地图上已生成的
    ///         return false;
    ///     ...
    /// }
    ///
    /// public override float GetIngredientCount(IngredientCount ing, Bill bill)
    /// {
    ///     ...
    ///     return Mathf.Min((float)bill.Map.listerThings.ThingsOfDef(ThingDefOf.HemogenPack)
    ///                         .Sum(x => x.stackCount),                      // ★ 同样漏了核心
    ///                      firstHediffOfDef.Severity / 0.35f);
    /// }
    /// </code>
    /// <c>listerThings</c> 只索引 <c>Spawned == true</c> 的 Thing ⇒ 核心里的包数不到
    /// ⇒ <c>AvailableOnNow</c> 判定「没有血原质」⇒ **手术直接不可用**。
    /// 这是「判定为无」型硬缺口：不是"找不到最近的"，而是功能直接不给用。
    ///
    /// ── 为什么可以放心修：取料链路本来就是通的 ──
    /// 本类<strong>只修「判定」与「数量」两处</strong>，完全不碰取料：
    /// <list type="number">
    ///   <item><c>WorkGiver_DoBill.TryFindBestIngredientsHelper</c> 内部**本身就遍历
    ///         <c>map.haulDestinationManager.AllHaulSourcesListForReading</c>**，
    ///         对每个启用的 <c>IHaulSource</c> 调 <c>GetAllThingsRecursively</c> 把内容物加入候选。
    ///         数字核心实现了 <c>IHaulSource</c> 且 <c>HaulSourceEnabled</c> 默认 true
    ///         ⇒ **本图核心里的材料天然进候选**；</item>
    ///   <item>该路径用的是 <c>pawn.CanReserve(t)</c>，而原版 <c>ReservationManager.CanReserve</c>
    ///         **不要求目标已生成**（只查 <c>IsValid</c> / <c>ThingDestroyed</c>）
    ///         ⇒ 核心物品可以被选中；</item>
    ///   <item>选中之后的「搬运」由 4.0 自己的背包机制完成 —— 它在
    ///         <c>JobDriver_DoBill.MakeNewToils</c> 里 Prepend 一个 toil，
    ///         用 <c>HediffComp_Backpack</c> 按**需要的数量**吸取材料；
    ///         而 4.0 会**自动给小人植入数字背包**（<c>BackpackImplant.Sync</c>）
    ///         ⇒ 人人都有，这条路默认就是通的。</item>
    /// </list>
    /// ⇒ 所以本类**只需修判定**：判定修好后手术可用，材料由 4.0 自己的机制搬，数量也准。
    ///
    /// <para>⚠️ v0.44 曾额外加过一个「自己把材料取到地上」的补丁，实测证明**多余且有害**：
    /// 它按**整堆**取（<c>ExtractTo(stackCount)</c>），于是核心里每种材料都甩出一整堆到地上
    /// （实测装木腿时地上留下 74 木头 + 23 医药）；更糟的是那些地上的材料会被原 mod 的
    /// 自动收纳再次吸回，反复中断手术，形成「取出→被收→再取出」的循环，
    /// 且麻醉已消耗药品 ⇒ 可瞬间清空库存药品。**该补丁已删除。**
    /// 教训：「能选中」与「搬得动」确实是两件事，但不要假设后者没人管 ——
    /// 先查清原 mod 是否已有机制，再决定要不要补。</para>
    ///
    /// ── 保守性 ──
    /// 两处都只在「**确实是因为地图上没有血包才被拒/才算少**」时才介入：
    /// <list type="bullet">
    ///   <item><c>AvailableOnNow</c>：若原版返回 true ⇒ 不动；若因「没有失血」被拒 ⇒ 不推翻
    ///         （那是正确拒绝）；若地图上其实有血包却仍被拒 ⇒ 说明是 <c>base.AvailableOnNow</c>
    ///         拒的，也不推翻。**只有"地图没血包 + 核心有"这一种情形才翻盘**；</item>
    ///   <item><c>GetIngredientCount</c>：原版结果 = <c>min(地图包数, 失血量/0.35)</c>。
    ///         若原版结果已达上限（<c>&gt;= 失血量/0.35</c>）⇒ 加多少都一样，不改；
    ///         否则把核心里的包数补进去，再与上限取小。</item>
    /// </list>
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_BloodTransfusionFromCore
    {
        /// <summary>兜底修正次数（只读诊断用）。</summary>
        internal static int AvailableFixCount;
        internal static int CountFixCount;

        /// <summary>
        /// 统计可用核心（含本图投影核心绑定的核心）里的血原质包总数。
        /// </summary>
        private static int CountHemogenInCores(Map map)
        {
            if (map == null)
            {
                return 0;
            }

            List<Building_StorageCore> cores = tmpCores;
            cores.Clear();
            List<Building_StorageCore> mapCores = CoreContentAccess.UsableCores(map);
            if (mapCores != null)
            {
                cores.AddRange(mapCores);
            }
            try
            {
                ProjectionHelper.AppendProjectedCores(map, cores);
            }
            catch (Exception)
            {
                // 投影读取失败不影响本图核心这条主路径。
            }

            int total = 0;
            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.Spawned)
                {
                    continue;
                }
                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null)
                {
                    continue;
                }
                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t != null && !t.Destroyed && t.stackCount > 0
                        && t.def == ThingDefOf.HemogenPack)
                    {
                        total += t.stackCount;
                    }
                }
            }

            cores.Clear();
            return total;
        }

        private static readonly List<Building_StorageCore> tmpCores = new List<Building_StorageCore>();

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.bloodTransfusionFromCore;
        }

        // ════════════════════════════ ① 可用性判定 ════════════════════════════

        [HarmonyPatch(typeof(Recipe_BloodTransfusion), "AvailableOnNow")]
        internal static class Patch_AvailableOnNow
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Thing thing, ref bool __result)
            {
                if (__result || thing == null)
                {
                    return;
                }

                Map map = thing.MapHeld;
                if (map == null)
                {
                    return;
                }

                if (!Enabled())
                {
                    return;
                }

                // 原版拒绝原因之一：目标没有失血 ⇒ 这是**正确**的拒绝，不推翻。
                if (thing is Pawn pawn)
                {
                    if (pawn.health?.hediffSet == null
                        || !pawn.health.hediffSet.HasHediff(HediffDefOf.BloodLoss))
                    {
                        return;
                    }
                }

                // 地图上其实有血包却仍被拒 ⇒ 说明是 base.AvailableOnNow 拒的（别的原因），不推翻。
                if (map.listerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count > 0)
                {
                    return;
                }

                // ★ 到这里可确定：拒绝原因就是「地图上没有血原质包」。看核心。
                try
                {
                    if (CountHemogenInCores(map) > 0)
                    {
                        __result = true;
                        AvailableFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][输血] 地图上没有血原质包，核心里有 ⇒ 手术可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 兜底失败不该把"不可用"升级成红字异常。
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心血原质时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510290);
                }
            }
        }

        // ════════════════════════════ ② 用量计算 ════════════════════════════

        [HarmonyPatch(typeof(Recipe_BloodTransfusion), "GetIngredientCount")]
        internal static class Patch_GetIngredientCount
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(IngredientCount ing, Bill bill, ref float __result)
            {
                if (bill?.Map == null || ing?.filter == null)
                {
                    return;
                }

                // 只处理血原质这一项配料（该配方只有这一项，但保持一般性）。
                if (!ing.filter.Allows(ThingDefOf.HemogenPack))
                {
                    return;
                }

                if (!Enabled())
                {
                    return;
                }

                // 原版只在这两种情况下才走"min(地图包数, 失血量/0.35)"这条路径；
                // 其余情况它返回的是 base 值，我们不该动。
                if (!(bill.billStack?.billGiver is Pawn pawn) || pawn.health?.hediffSet == null)
                {
                    return;
                }
                Hediff loss = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
                if (loss == null)
                {
                    return;
                }

                try
                {
                    // 上限 = 治好当前失血所需的包数。
                    float cap = loss.Severity / Recipe_BloodTransfusion.BloodlossHealedPerPack;

                    // 原版结果已达上限 ⇒ 加多少都一样，不改（也避免把已正确的结果改坏）。
                    if (__result >= cap)
                    {
                        return;
                    }

                    int coreCount = CountHemogenInCores(bill.Map);
                    if (coreCount <= 0)
                    {
                        return;
                    }

                    float before = __result;
                    __result = Mathf.Min(__result + coreCount, cap);
                    CountFixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][输血] 用量把核心算在内：" + before
                                    + " → " + __result + "（核心 " + coreCount + "，上限 " + cap + "）");
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 计算输血用量时出错（本次放行原版结果）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510291);
                }
            }
        }
    }
}
