using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理缺陷修复 ⑨（FIX-12）：一次制作任务占满所有同类工作台，饿死队列里的其他任务。
    ///
    /// <para><b>根因（反编译实锤，CompBillAutomation.SyncLines / ProbeAll）</b>：三处叠加 ——</para>
    /// <list type="bullet">
    /// <item><b>贪心分配</b>：每次扫描，每个「要开工」的计划把范围内所有空闲且能做该配方的台子
    /// 全部占线（与实际需求量无关）——「做 1 件」也占 5 个台子；</item>
    /// <item><b>存量线永不重排</b>：线只在「计划失效/台子损毁/配方不匹配」时释放 ——
    /// 先建的配方占住的台子永远不会让给后来饿着的配方（这是"拆台重建才恢复"的原因）；
    /// <item><b>取活预算按计划顺序吃</b>：ProbeAll 每 tick 最多 2 次取活，排在前面的计划的
    /// 堵塞线先吃预算，排在后面的计划的线长期轮不到取活（界面恒显示"等待中"）。</item>
    /// </list>
    ///
    /// <para><b>修法</b>（开关 <c>craftFairBenchAlloc</c>，四处挂点）：</para>
    /// <list type="number">
    /// <item><b>按需封顶</b>：计数模式最多占 remaining 条线、维持模式最多占"还差的件数"条线，
    /// 永久模式不封顶；</item>
    /// <item><b>线少者优先 + 超编让台</b>：分配按"现有线数升序"轮转；取活瞬间（此刻必然无
    /// 在制工作，让出零损失）若存在"0 条线且范围内确有它能用台子"的饿着计划，超编计划
    /// （&gt;1 条线）的这条线直接释放让台；扫描时再平衡 —— 超出封顶的闲置线、以及
    /// 饿着计划可用的闲置线都会被回收重分；</item>
    /// <item><b>取活预算公平轮转</b>：Prefix 替换 ProbeAll，按计划轮转发放取活预算，
    /// 不再按计划顺序。</item>
    /// </list>
    ///
    /// <para><b>与 Patch_BillIdleReclaim 的关系</b>：两个 Prefix 挂同一方法 SyncLines，
    /// IdleReclaim 先装（回收「维持达标」的闲置线），本补丁后装（重写分配段）。
    /// 与 Patch_CraftIngredientClaims 的关系：本补丁的 TryAcquire 前缀只管"让台"，
    /// 认领的登记/释放由对方负责，互不重叠。</para>
    ///
    /// <para><b>降级</b>：成员定位失败 → 安装时跳过 + 告警日志；运行期异常 →
    /// 放行原方法（原行为）。释放/重建两段是幂等的，中途异常回退原方法不会重复建线。</para>
    /// </summary>
    internal static class Patch_SyncLinesFairAlloc
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.syncfair";

        // ── CompBillAutomation（public 类，编译期引用；private 成员反射）──
        private static FieldInfo PlansField;              // plans (private List<CraftPlan>)
        private static FieldInfo BenchesField;            // benches (private readonly List<Thing>)
        private static FieldInfo UsedBenchesField;        // usedBenches (private readonly HashSet<Thing>)
        private static MethodInfo ReleaseLineMethod;      // ReleaseLine(CraftPlan, int) (private)
        private static MethodInfo MakeLineMethod;         // MakeLine(CraftPlan, Thing) (private)
        private static MethodInfo PickBenchMethod;        // PickBench(RecipeDef) (private)
        private static MethodInfo TotalLinesMethod;       // TotalLines() (private)
        private static MethodInfo RefreshTargetMethod;    // RefreshTargetStateIfStale(CraftPlan, Map) (internal)
        private static MethodInfo BenchUsableMethod;      // BenchUsable(Thing) (internal static)
        private static MethodInfo CanCraftMethod;         // CanCraft(Thing, RecipeDef) (internal static)
        private static MethodInfo ProbeAllMethod;         // ProbeAll(Map, Pawn, int) (private)
        private static MethodInfo TryAcquireMethod;       // BillProbe.TryAcquire(...) (internal static)

        // ── CraftPlan / CraftLine（internal sealed，全部反射）──
        private static Type CraftPlanType;
        private static FieldInfo PlanRecipeField;         // recipe
        private static FieldInfo PlanModeField;           // mode
        private static FieldInfo PlanRemainingField;      // remaining
        private static FieldInfo PlanTargetCountField;    // targetCount
        private static FieldInfo PlanCountedCountField;   // countedCount
        private static FieldInfo PlanLinesField;          // lines
        private static PropertyInfo PlanMaintainedProp;   // Maintained
        private static PropertyInfo PlanCanStartProp;     // CanStartNewWork
        private static FieldInfo LineBenchField;          // CraftLine.Bench
        private static PropertyInfo LineHasWorkProp;      // CraftLine.HasWork
        private static MethodInfo LineClearWorkMethod;    // CraftLine.ClearWork（反射调用，仅异常路径用）
        private static FieldInfo LineBlockKeyField;       // CraftLine.BlockKey
        private static FieldInfo LineNextAcquireTickField;// CraftLine.NextAcquireTick

        private static MethodInfo TryClaimMethod;         // BillBenchClaims.TryClaim(Map, Thing, CompBillAutomation) (internal static)

        public static void Install()
        {
            try
            {
                Type compType = typeof(CompBillAutomation);
                CraftPlanType = AccessTools.TypeByName("DigitalStorage.AI.CraftPlan");
                Type lineType = AccessTools.TypeByName("DigitalStorage.AI.CraftLine");
                Type claimsType = AccessTools.TypeByName("DigitalStorage.AI.BillBenchClaims");
                Type probeType = AccessTools.TypeByName("DigitalStorage.AI.BillProbe");
                if (CraftPlanType == null || lineType == null || claimsType == null || probeType == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理工作台分配：找不到 CraftPlan/CraftLine/BillBenchClaims/BillProbe"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                PlansField = AccessTools.Field(compType, "plans");
                BenchesField = AccessTools.Field(compType, "benches");
                UsedBenchesField = AccessTools.Field(compType, "usedBenches");
                ReleaseLineMethod = AccessTools.Method(compType, "ReleaseLine");
                MakeLineMethod = AccessTools.Method(compType, "MakeLine");
                PickBenchMethod = AccessTools.Method(compType, "PickBench");
                TotalLinesMethod = AccessTools.Method(compType, "TotalLines");
                RefreshTargetMethod = AccessTools.Method(compType, "RefreshTargetStateIfStale");
                BenchUsableMethod = AccessTools.Method(compType, "BenchUsable");
                CanCraftMethod = AccessTools.Method(compType, "CanCraft");
                ProbeAllMethod = AccessTools.Method(compType, "ProbeAll");
                TryAcquireMethod = AccessTools.Method(probeType, "TryAcquire");

                PlanRecipeField = AccessTools.Field(CraftPlanType, "recipe");
                PlanModeField = AccessTools.Field(CraftPlanType, "mode");
                PlanRemainingField = AccessTools.Field(CraftPlanType, "remaining");
                PlanTargetCountField = AccessTools.Field(CraftPlanType, "targetCount");
                PlanCountedCountField = AccessTools.Field(CraftPlanType, "countedCount");
                PlanLinesField = AccessTools.Field(CraftPlanType, "lines");
                PlanMaintainedProp = AccessTools.Property(CraftPlanType, "Maintained");
                PlanCanStartProp = AccessTools.Property(CraftPlanType, "CanStartNewWork");
                LineBenchField = AccessTools.Field(lineType, "Bench");
                LineHasWorkProp = AccessTools.Property(lineType, "HasWork");
                LineClearWorkMethod = AccessTools.Method(lineType, "ClearWork");
                LineBlockKeyField = AccessTools.Field(lineType, "BlockKey");
                LineNextAcquireTickField = AccessTools.Field(lineType, "NextAcquireTick");

                TryClaimMethod = AccessTools.Method(claimsType, "TryClaim");

                if (PlansField == null || BenchesField == null || UsedBenchesField == null
                    || ReleaseLineMethod == null || MakeLineMethod == null || PickBenchMethod == null
                    || TotalLinesMethod == null || RefreshTargetMethod == null
                    || BenchUsableMethod == null || CanCraftMethod == null
                    || ProbeAllMethod == null || TryAcquireMethod == null
                    || PlanRecipeField == null || PlanModeField == null || PlanRemainingField == null
                    || PlanTargetCountField == null || PlanCountedCountField == null
                    || PlanLinesField == null || PlanMaintainedProp == null || PlanCanStartProp == null
                    || LineBenchField == null || LineHasWorkProp == null
                    || LineClearWorkMethod == null || LineBlockKeyField == null
                    || LineNextAcquireTickField == null || TryClaimMethod == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理工作台分配：成员定位不完整"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);

                MethodInfo syncLines = AccessTools.Method(compType, "SyncLines");
                if (syncLines == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理工作台分配：找不到 CompBillAutomation.SyncLines，本项跳过。");
                    return;
                }

                // ① Prefix 替换 SyncLines：按需封顶 + 线少者优先 + 再平衡
                harmony.Patch(syncLines, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_SyncLinesFairAlloc), nameof(Prefix_SyncLines))));

                // ② Prefix 替换 ProbeAll：取活预算按计划公平轮转
                harmony.Patch(ProbeAllMethod, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_SyncLinesFairAlloc), nameof(Prefix_ProbeAll))));

                // ③ Prefix 挂 BillProbe.TryAcquire：超编计划的线在取活瞬间让台（零进度损失）
                harmony.Patch(TryAcquireMethod, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_SyncLinesFairAlloc), nameof(Prefix_TryAcquireGuard))));

                Log.Message("[数字存储补丁] 已挂载：制作代理工作台按需分配、计划间公平共享"
                            + "（设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理工作台分配挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── ① SyncLines：Prefix 替换 ──

        private static bool Prefix_SyncLines(CompBillAutomation __instance, Map map)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftFairBenchAlloc || __instance == null || map == null)
            {
                return true;
            }

            if (PlansField == null || ReleaseLineMethod == null)
            {
                return true;
            }

            try
            {
                RunFairSync(__instance, map);
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理工作台分配执行异常（本次回退原版分配逻辑）："
                              + ex.GetType().Name + ": " + ex.Message, 8853119);
                return true;
            }
        }

        /// <summary>
        /// SyncLines 的等价复刻（释放判定 / usedBenches 重建 / 槽位预算逐行对齐原版），
        /// 分配段改为「按需封顶 + 线少者优先轮转 + 再平衡」。
        /// </summary>
        private static void RunFairSync(CompBillAutomation comp, Map map)
        {
            Patch_CraftIngredientClaims.SweepStaleClaims();   // 顺手清扫陈旧认领（防跨代理堵堆）

            IList plans = PlansField.GetValue(comp) as IList;
            if (plans == null)
            {
                throw new InvalidOperationException("plans 字段不可用");
            }

            // ── 1) 释放判定（逐行对齐原版：!Maintained || 台子无效 || 配方不匹配 ⇒ 释放）──
            for (int p = plans.Count - 1; p >= 0; p--)
            {
                object plan = plans[p];
                if (plan == null)
                {
                    continue;
                }

                IList lines = PlanLinesField.GetValue(plan) as IList;
                if (lines == null)
                {
                    continue;
                }

                bool maintained = BoolProp(PlanMaintainedProp, plan);
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    Thing bench = LineBenchField.GetValue(lines[i]) as Thing;
                    if (!maintained || bench == null || bench.Destroyed || !bench.Spawned
                        || !BenchUsable(bench) || !CanCraft(bench, RecipeOf(plan)))
                    {
                        ReleaseLineMethod.Invoke(comp, new object[] { plan, i });
                    }
                }
            }

            // ── 2) 重建 usedBenches（逐行对齐原版）──
            ICollection<Thing> usedBenches = UsedBenchesField.GetValue(comp) as ICollection<Thing>;
            if (usedBenches == null)
            {
                throw new InvalidOperationException("usedBenches 字段不可用");
            }

            usedBenches.Clear();
            for (int p = 0; p < plans.Count; p++)
            {
                IList lines = PlanLinesField.GetValue(plans[p]) as IList;
                if (lines == null)
                {
                    continue;
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    Thing bench = LineBenchField.GetValue(lines[i]) as Thing;
                    if (bench != null)
                    {
                        usedBenches.Add(bench);
                    }
                }
            }

            // ── 3) 收集待开工计划（先刷新维持模式的目标状态，再判 CanStartNewWork —— 对齐原版顺序）──
            List<object> active = new List<object>();
            for (int p = 0; p < plans.Count; p++)
            {
                object plan = plans[p];
                if (plan == null || !BoolProp(PlanMaintainedProp, plan))
                {
                    continue;
                }

                RefreshTargetMethod.Invoke(comp, new object[] { plan, map });
                if (!BoolProp(PlanCanStartProp, plan))
                {
                    continue;
                }

                active.Add(plan);
            }

            // ── 4) 再平衡：超封顶的闲置线与"饿着计划可用"的闲置线让出台子 ──
            Rebalance(comp, active, usedBenches);

            // ── 5) 槽位预算（逐行对齐原版：max(1, maxSlots) − TotalLines）──
            int budget = Math.Max(1, comp.Props.maxSlots) - (int)TotalLinesMethod.Invoke(comp, null);

            // ── 6) 公平轮转分配：每轮按"现有线数升序"发放，线少的计划优先拿台子 ──
            bool progressed = true;
            while (budget > 0 && progressed)
            {
                progressed = false;
                List<object> ordered = OrderByLineCount(active);
                for (int a = 0; a < ordered.Count; a++)
                {
                    if (budget <= 0)
                    {
                        break;
                    }

                    object plan = ordered[a];
                    IList lines = PlanLinesField.GetValue(plan) as IList;
                    int lineCount = lines?.Count ?? 0;
                    if (lineCount >= CapFor(plan))
                    {
                        continue;   // 该计划的需求已封顶
                    }

                    Thing bench = PickBenchMethod.Invoke(comp, new object[] { RecipeOf(plan) }) as Thing;
                    if (bench == null)
                    {
                        continue;   // 没有空闲且能用的台子了
                    }

                    if (!(bool)TryClaimMethod.Invoke(null, new object[] { map, bench, comp }))
                    {
                        usedBenches.Add(bench);     // 被其它代理占着：跳过这个台子（对齐原版）
                        continue;
                    }

                    usedBenches.Add(bench);
                    object line = MakeLineMethod.Invoke(comp, new object[] { plan, bench });
                    lines.Add(line);
                    budget--;
                    progressed = true;
                }
            }
        }

        /// <summary>
        /// 再平衡（原 mod 没有：存量线只在不失效时释放 ⇒ 先建的配方永久霸台）：
        /// <list type="number">
        /// <item>计数/维持模式需求下降后，超出封顶的**闲置**线释放（在做的不动）；</item>
        /// <item>存在"0 条线"的饿着计划时，从其它计划（&gt;1 条线）回收一条
        /// **该饿着计划能用**的闲置线给它腾台子（每个饿着计划每次扫描最多腾一条）。</item>
        /// </list>
        /// 正在制作的线不会被本逻辑释放（进度零损失）；在制线全部繁忙时，
        /// 由 TryAcquire 前缀在"取活瞬间"（无在制工作）继续让台。
        /// </summary>
        private static void Rebalance(CompBillAutomation comp, List<object> active, ICollection<Thing> usedBenches)
        {
            if (active.Count < 2)
            {
                return;
            }

            // a) 超出封顶的闲置线（计数/维持模式需求下降）
            foreach (object plan in active)
            {
                int cap = CapFor(plan);
                if (cap == int.MaxValue)
                {
                    continue;
                }

                int keep = Math.Max(cap, 1);
                IList lines = PlanLinesField.GetValue(plan) as IList;
                if (lines == null)
                {
                    continue;
                }

                for (int i = lines.Count - 1; i >= 0 && lines.Count > keep; i--)
                {
                    if (HasWorkOf(lines[i]))
                    {
                        continue;   // 在制的不动
                    }

                    Thing bench = LineBenchField.GetValue(lines[i]) as Thing;
                    ReleaseLineMethod.Invoke(comp, new object[] { plan, i });
                    if (bench != null)
                    {
                        usedBenches.Remove(bench);
                    }
                }
            }

            // b) 饿着计划（0 条线）→ 从其它计划（>1 条线）腾一条它能用的闲置线
            for (int s = 0; s < active.Count; s++)
            {
                object starved = active[s];
                IList starvedLines = PlanLinesField.GetValue(starved) as IList;
                if (starvedLines == null || starvedLines.Count != 0)
                {
                    continue;
                }

                RecipeDef need = RecipeOf(starved);
                bool helped = false;
                for (int p = 0; p < active.Count && !helped; p++)
                {
                    if (p == s)
                    {
                        continue;
                    }

                    object donor = active[p];
                    IList donorLines = PlanLinesField.GetValue(donor) as IList;
                    if (donorLines == null || donorLines.Count <= 1)
                    {
                        continue;
                    }

                    for (int i = donorLines.Count - 1; i >= 0; i--)
                    {
                        object line = donorLines[i];
                        if (HasWorkOf(line))
                        {
                            continue;
                        }

                        Thing bench = LineBenchField.GetValue(line) as Thing;
                        if (bench == null || !CanCraft(bench, need))
                        {
                            continue;
                        }

                        ReleaseLineMethod.Invoke(comp, new object[] { donor, i });
                        usedBenches.Remove(bench);
                        helped = true;
                        break;
                    }
                }
            }
        }

        // ── ② ProbeAll：Prefix 替换 —— 取活预算按计划公平轮转（原版按计划顺序，排后的饿死）──

        private static bool Prefix_ProbeAll(CompBillAutomation __instance, Map map, Pawn w, int now)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftFairBenchAlloc || __instance == null)
            {
                return true;
            }

            try
            {
                RunFairProbe(__instance, map, w, now);
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理取活轮转执行异常（本次回退原版顺序）："
                              + ex.GetType().Name + ": " + ex.Message, 8853121);
                return true;
            }
        }

        private static void RunFairProbe(CompBillAutomation comp, Map map, Pawn w, int now)
        {
            int budget = Math.Max(1, comp.Props.maxProbesPerTick);
            IList plans = PlansField.GetValue(comp) as IList;
            if (plans == null || plans.Count == 0)
            {
                return;
            }

            List<object> hungry = new List<object>();
            for (int p = 0; p < plans.Count; p++)
            {
                object plan = plans[p];
                if (plan != null && BoolProp(PlanMaintainedProp, plan) && BoolProp(PlanCanStartProp, plan))
                {
                    hungry.Add(plan);
                }
            }

            bool progressed = true;
            while (budget > 0 && progressed)
            {
                progressed = false;
                for (int p = 0; p < hungry.Count && budget > 0; p++)
                {
                    object plan = hungry[p];
                    IList lines = PlanLinesField.GetValue(plan) as IList;
                    if (lines == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < lines.Count; j++)
                    {
                        object line = lines[j];
                        if (line == null || HasWorkOf(line) || (int)LineNextAcquireTickField.GetValue(line) > now)
                        {
                            continue;
                        }

                        budget--;
                        progressed = true;
                        try
                        {
                            TryAcquireMethod.Invoke(null, new object[] { comp, plan, line, map, w, now });
                        }
                        catch (Exception ex)
                        {
                            Log.ErrorOnce("[数字存储补丁] 制作代理取活异常（已跳过这条线，5 秒后重试）："
                                          + ex.GetType().Name + ": " + ex.Message, 881133);
                            LineClearWorkMethod.Invoke(line, null);
                            LineBlockKeyField.SetValue(line, "DS_BA_NoBill");
                            LineNextAcquireTickField.SetValue(line, now + 300);
                        }

                        break;  // 本计划本轮只取一条，轮转给其它计划
                    }
                }
            }
        }

        // ── ③ TryAcquire 前缀：超编计划的线在取活瞬间让台（此刻必然无在制工作，零进度损失）──

        private static bool Prefix_TryAcquireGuard(CompBillAutomation comp, object plan, object line)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftFairBenchAlloc || comp == null || plan == null || line == null)
            {
                return true;
            }

            try
            {
                IList lines = PlanLinesField.GetValue(plan) as IList;
                if (lines == null || lines.Count <= 1)
                {
                    return true;    // 自己不超编（≤1 条线），谈不上让
                }

                IList plans = PlansField.GetValue(comp) as IList;
                if (plans == null)
                {
                    return true;
                }

                // 是否存在"0 条线、且范围内确有它能用的台子"的饿着计划
                bool starvedExists = false;
                for (int p = 0; p < plans.Count; p++)
                {
                    object other = plans[p];
                    if (other == null || other == plan
                        || !BoolProp(PlanMaintainedProp, other) || !BoolProp(PlanCanStartProp, other))
                    {
                        continue;
                    }

                    IList otherLines = PlanLinesField.GetValue(other) as IList;
                    if (otherLines == null || otherLines.Count != 0)
                    {
                        continue;
                    }

                    if (HasUsableBenchFor(comp, RecipeOf(other)))
                    {
                        // 还有空闲且能用的台子时不用让 —— 分配段（线少者优先）本轮就会给它
                        Thing freeBench = PickBenchMethod.Invoke(comp, new object[] { RecipeOf(other) }) as Thing;
                        if (freeBench == null)
                        {
                            starvedExists = true;
                            break;
                        }
                    }
                }

                if (!starvedExists)
                {
                    return true;
                }

                // 释放本线（ProbeAll 只取活 !HasWork 的线 ⇒ 此刻零进度损失），台子下一轮分给饿着的计划
                int idx = lines.IndexOf(line);
                if (idx < 0)
                {
                    return true;
                }

                ReleaseLineMethod.Invoke(comp, new object[] { plan, idx });
                if (DsPatchMod.Settings.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁] 制作代理：存在饿着的配方，超编线在取活时让出工作台。");
                }

                return false;   // 跳过本次取活
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理让台判定异常（本次放行原逻辑）："
                              + ex.GetType().Name + ": " + ex.Message, 8853120);
                return true;
            }
        }

        private static bool HasUsableBenchFor(CompBillAutomation comp, RecipeDef recipe)
        {
            IEnumerable benches = BenchesField.GetValue(comp) as IEnumerable;
            if (benches == null)
            {
                return false;
            }

            foreach (Thing bench in benches)
            {
                if (bench != null && BenchUsable(bench) && CanCraft(bench, recipe))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<object> OrderByLineCount(List<object> plans)
        {
            List<object> ordered = new List<object>(plans);
            ordered.Sort((a, b) =>
            {
                int ca = (PlanLinesField.GetValue(a) as IList)?.Count ?? 0;
                int cb = (PlanLinesField.GetValue(b) as IList)?.Count ?? 0;
                return ca.CompareTo(cb);
            });
            return ordered;
        }

        private static bool HasWorkOf(object line)
        {
            object v = LineHasWorkProp.GetValue(line, null);
            return v is bool b && b;
        }

        /// <summary>
        /// 本计划允许持有的**总线数**上限（按需封顶）：
        /// 永久模式 = 不封顶；计数模式 = <c>remaining</c>（做几件最多占几条线）；
        /// 维持模式 = <c>targetCount − countedCount</c>（还差的件数）。
        /// 已有线是可复用的生产通道（做完一件自动再取活），只限"占台子的通道数"。
        /// </summary>
        private static int CapFor(object plan)
        {
            int mode = (int)PlanModeField.GetValue(plan);
            if (mode == 0)
            {
                return int.MaxValue;
            }

            if (mode == 1)
            {
                return Math.Max(0, (int)PlanRemainingField.GetValue(plan));
            }

            // mode 2（维持）：还差的件数
            int target = (int)PlanTargetCountField.GetValue(plan);
            int counted = (int)PlanCountedCountField.GetValue(plan);
            return Math.Max(0, target - counted);
        }

        private static RecipeDef RecipeOf(object plan)
        {
            return PlanRecipeField.GetValue(plan) as RecipeDef;
        }

        private static bool BenchUsable(Thing bench)
        {
            return (bool)BenchUsableMethod.Invoke(null, new object[] { bench });
        }

        private static bool CanCraft(Thing bench, RecipeDef recipe)
        {
            return (bool)CanCraftMethod.Invoke(null, new object[] { bench, recipe });
        }

        private static bool BoolProp(PropertyInfo prop, object target)
        {
            object v = prop.GetValue(target, null);
            return v is bool b && b;
        }
    }
}
