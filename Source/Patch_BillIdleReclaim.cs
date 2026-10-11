using System;
using System.Collections;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理缺陷修复 ②：「维持 xxx」达标后进入等待态，堵死后续配方。
    ///
    /// <para><b>根因</b>：维持模式（<c>CraftPlan.mode == 2</c>）达标后
    /// <c>plan.wantsWork = false</c> ⇒ <c>CanStartNewWork == false</c>，于是</para>
    /// <list type="bullet">
    /// <item><c>SyncLines</c>：<c>if (!plan.CanStartNewWork) continue;</c> —— 只停**新建**线；</item>
    /// <item><c>ProbeAll</c>：整块跳过该 plan —— 不再取活；</item>
    /// <item><b>但 <c>SyncLines</c> 的释放判定里没有"达标"这一条</b>：
    /// <c>if (!plan.Maintained || bench == null || !BenchUsable(bench) || !CanCraft(bench, recipe)) ReleaseLine(...)</c>
    /// ⇒ 已建立的线**被永久保留**。</item>
    /// </list>
    /// <para>这条闲置线造成三重占用：<c>usedBenches</c>（<c>SyncLines</c> 每次重建、含所有现有线的台子）
    /// ⇒ 后续 plan 的 <c>FreeBenchCountFor</c>/<c>PickBench</c> 拿不到这些台子；
    /// <c>BillBenchClaims</c>（只有 <c>ReleaseLine</c> 才归还）⇒ 连**其它**制作代理也用不了；
    /// <c>maxSlots</c>(64) 槽位 ⇒ <c>TotalLines()</c> 被闲置线占满。
    /// 表现：该配方显示"等待中"，后续配方显示"待处理/堵塞"且永不执行。</para>
    ///
    /// <para><b>修法</b>：Prefix 挂 <c>SyncLines</c>，在原逻辑跑之前回收
    /// "仍在计划内、但已不需要工作（<c>CanStartNewWork == false</c>）且无在制工作
    /// （<c>!line.HasWork</c>）"的线。放在 Prefix 使得同一次 <c>SyncLines</c> 重建
    /// <c>usedBenches</c> 时就能看到释放后的台子 ⇒ 后续 plan 立刻可用，无 1 tick 延迟。</para>
    ///
    /// <para><b>为什么"正在制作"的线要保留</b>：<c>AdvanceAll</c> 只看 <c>line.HasWork</c>、
    /// 不看 <c>CanStartNewWork</c>，所以保留它能**把当前这件做完**（符合"达标后不再开工、
    /// 但把手上这件完成"的语义）；做完后 <c>ClearWork()</c> 使其 <c>HasWork == false</c>，
    /// 下一轮 <c>SyncLines</c> 即被回收。</para>
    ///
    /// <para><b>恢复路径天然正确</b>：达标后库存降到 <c>ResumeAt</c>（targetCount 的 3/5）
    /// 以下时 <c>wantsWork</c> 自动回 true ⇒ <c>SyncLines</c> 会重新建线。</para>
    ///
    /// <para><b>反射说明</b>：<c>CraftPlan</c>/<c>CraftLine</c> 是原 mod 的
    /// <c>internal sealed</c> 类型，补丁不能编译期引用，故成员全部反射获得并缓存；
    /// 挂点 <c>SyncLines</c> 每 <c>scanIntervalTicks</c>(默认 60) 才执行一次，
    /// 反射开销可忽略。</para>
    /// </summary>
    internal static class Patch_BillIdleReclaim
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.billidle";

        private static FieldInfo PlansField;          // CompBillAutomation.plans (private)
        private static FieldInfo PlanLinesField;      // CraftPlan.lines (public readonly)
        private static PropertyInfo PlanMaintainedProp;   // CraftPlan.Maintained
        private static PropertyInfo PlanCanStartProp;     // CraftPlan.CanStartNewWork
        private static PropertyInfo LineHasWorkProp;      // CraftLine.HasWork
        private static MethodInfo ReleaseLineMethod;      // CompBillAutomation.ReleaseLine(CraftPlan, int)

        public static void Install()
        {
            try
            {
                Type compType = typeof(CompBillAutomation);
                Type planType = AccessTools.TypeByName("DigitalStorage.AI.CraftPlan");
                Type lineType = AccessTools.TypeByName("DigitalStorage.AI.CraftLine");
                if (planType == null || lineType == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理闲置线回收：找不到 CraftPlan/CraftLine"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                PlansField = AccessTools.Field(compType, "plans");
                PlanLinesField = AccessTools.Field(planType, "lines");
                PlanMaintainedProp = AccessTools.Property(planType, "Maintained");
                PlanCanStartProp = AccessTools.Property(planType, "CanStartNewWork");
                LineHasWorkProp = AccessTools.Property(lineType, "HasWork");
                ReleaseLineMethod = AccessTools.Method(compType, "ReleaseLine");
                MethodInfo syncLines = AccessTools.Method(compType, "SyncLines");

                if (PlansField == null || PlanLinesField == null || PlanMaintainedProp == null
                    || PlanCanStartProp == null || LineHasWorkProp == null
                    || ReleaseLineMethod == null || syncLines == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理闲置线回收：成员定位不完整"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                new Harmony(HarmonyId).Patch(syncLines, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_BillIdleReclaim), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：制作代理回收闲置线"
                            + "（「维持」达标后不再占着工作台堵住后续配方；设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理闲置线回收挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 只回收「仍在计划内（Maintained）但已不需要工作（!CanStartNewWork）
        /// 且无在制工作（!HasWork）」的线。
        /// <c>!Maintained</c>（已暂停/已完成）的情况原 <c>SyncLines</c> 的释放条件
        /// 已经覆盖，不在此重复处理。
        /// </summary>
        private static void Prefix(CompBillAutomation __instance)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftReclaimIdle || __instance == null)
            {
                return;
            }

            if (PlansField == null || ReleaseLineMethod == null)
            {
                return;
            }

            IList plans = PlansField.GetValue(__instance) as IList;
            if (plans == null || plans.Count == 0)
            {
                return;
            }

            int reclaimed = 0;
            for (int p = plans.Count - 1; p >= 0; p--)
            {
                object plan = plans[p];
                if (plan == null || !BoolProp(PlanMaintainedProp, plan))
                {
                    continue;
                }

                if (BoolProp(PlanCanStartProp, plan))
                {
                    continue;
                }

                IList lines = PlanLinesField.GetValue(plan) as IList;
                if (lines == null)
                {
                    continue;
                }

                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    object line = lines[i];
                    if (line == null || BoolProp(LineHasWorkProp, line))
                    {
                        continue;   // 正在制作：保留，做完再说
                    }

                    try
                    {
                        ReleaseLineMethod.Invoke(__instance, new object[] { plan, i });
                        reclaimed++;
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorOnce("[数字存储补丁] 制作代理回收闲置线失败（本条跳过）："
                                      + ex.GetType().Name + ": " + ex.Message, 8830117);
                        return;
                    }
                }
            }

            if (reclaimed > 0 && DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 制作代理：回收 " + reclaimed
                            + " 条闲置制作线（已达标/暂停，不再占用工作台）。");
            }
        }

        private static bool BoolProp(PropertyInfo prop, object target)
        {
            object v = prop.GetValue(target, null);
            return v is bool b && b;
        }
    }
}
