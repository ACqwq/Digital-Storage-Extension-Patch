using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理缺陷修复 ⑩（FIX-13）：同配方多线并行时，一条线完成会把其余线的进度清零，四线并行=单线。
    ///
    /// <para><b>根因（反编译实锤）</b>：三段原 mod 逻辑叠加 ——</para>
    /// <list type="number">
    /// <item><b>取活无预留记账</b>（BillProbe.ExtractIngredients）：几条线取活时，原版选料器
    /// 对每条线都选中同一堆料，校验只有 <c>counts &gt; stackCount</c> —— 没有"这条线已认领多少"，
    /// 同一堆被重复记账；料要到完成时才真正扣。</item>
    /// <item><b>完成时扣堆本体</b>（BillCraftFunnel.TryComplete）：
    /// <c>num &gt;= stackCount</c> 拿走整堆销毁，否则 SplitOff 拆堆使原堆缩水（75→5）。</item>
    /// <item><b>每 tick 盯单一堆校验</b>（CompBillAutomation.StillValid）：
    /// <c>line.Counts[i] &gt; thing.stackCount</c> —— 兄弟线引用同一个堆，一条线完成把堆扣掉一块
    /// ⇒ 兄弟线同时校验失败 ⇒ ClearWork（进度归零）⇒ 重新取活。</item>
    /// </list>
    ///
    /// <para><b>修法（方案 A：取活认领记账 + 选料避让）</b>：</para>
    /// <list type="number">
    /// <item><b>登记</b>：Postfix 挂 <c>BillProbe.TryAcquire</c>，取活成功后把该线的
    /// （原料堆 → 数量）记入认领账本（按线明细 + 全局按堆汇总）。</item>
    /// <item><b>释放</b>：挂两个**大方法**（不会被 JIT 内联，见踩坑目录"内联使补丁静默失效"）——
    /// TryAcquire 前缀（释放本线旧认领，覆盖重新取活）与 BillCraftFunnel.TryComplete 前缀
    /// （覆盖完成结算）；再加**惰性清扫**：每次取活/分配时把 <c>!HasWork</c> 的线的认领作废
    /// （StillValid 失败、ReleaseLine 等小方法路径漏网的认领最迟一次取活内被清掉）。
    /// 刻意**不**挂 <c>CraftLine.ClearWork</c> —— 它是几行字段赋值的小方法，必然被内联。</item>
    /// <item><b>避让</b>：Postfix 挂原版 <c>ReservationManager.CanReserve</c> —— 探针问询时
    /// （仅限"当前正在取活的替身"这一精确身份），已被认领的堆返回 false。
    /// 原版选料器把核心内材料纳入候选的通道正是 <c>CanReserve</c>
    /// （WorkGiver_DoBill.TryFindBestIngredientsHelper 的 haul-source 递归枚举与区域扫描都过这道闸）
    /// ⇒ 后续线自动改选未被认领的堆，兄弟线不再共享堆，StillValid 不再误杀。
    /// 与原 mod 自带的 Patch_ReservationManager_CanReserve_ProxyClaims（数字工人占位）同型，互不干扰。</item>
    /// </list>
    ///
    /// <para><b>不触碰</b>：ExtractIngredients / StillValid / TryComplete 本体（产物/品质/记录的
    /// money path）全部保持原样 —— 共享堆在源头（选料）就被避免，中后段无需改动。</para>
    ///
    /// <para><b>已知边界</b>：核心里某种原料只剩一个堆时，第二条线会等第一条完成
    /// （NoCoreMaterial 退避重试），串行但进度不再清零；多堆则真并行。
    /// 认领只作用于"探针身份"的 CanReserve 问询，真实小人与其它系统完全不受影响。</para>
    ///
    /// <para><b>降级</b>：成员定位失败 → 安装时跳过 + 告警；运行期异常 → 不改变
    /// CanReserve 结果（原行为）。开关关闭时：新认领与避让立即停止，**存量认领靠清扫
    /// 与释放点照常回收**，不会留下永远堵住的死认领。</para>
    /// </summary>
    internal static class Patch_CraftIngredientClaims
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.ingredientclaims";

        // ── 认领账本 ──
        /// <summary>堆 → 在制线认领总量（全局，跨制作代理）。</summary>
        private static readonly Dictionary<Thing, int> ThingClaims = new Dictionary<Thing, int>();

        /// <summary>制作线 → 该线认领明细。普通字典（要枚举做清扫）；
        /// 孤儿线必然 !HasWork，会被清扫回收，不构成泄漏。</summary>
        private static readonly Dictionary<object, ClaimRecord> LineClaims =
            new Dictionary<object, ClaimRecord>();

        private sealed class ClaimRecord
        {
            public readonly List<Thing> Things = new List<Thing>();
            public readonly List<int> Counts = new List<int>();
        }

        /// <summary>当前正在取活的替身（TryAcquire 同步执行期间非空；仅主线程）。</summary>
        private static Pawn currentProbeWorker;

        // ── 反射成员（CraftLine / BillProbe / BillCraftFunnel 是 internal 类型）──
        private static FieldInfo LineIngredientsField;   // CraftLine.Ingredients (public Thing[])
        private static FieldInfo LineCountsField;        // CraftLine.Counts (public int[])
        private static PropertyInfo LineHasWorkProp;     // CraftLine.HasWork (public bool)

        public static void Install()
        {
            try
            {
                Type probeType = AccessTools.TypeByName("DigitalStorage.AI.BillProbe");
                Type lineType = AccessTools.TypeByName("DigitalStorage.AI.CraftLine");
                Type funnelType = AccessTools.TypeByName("DigitalStorage.AI.BillCraftFunnel");
                if (probeType == null || lineType == null || funnelType == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理取料认领：找不到 BillProbe/CraftLine/BillCraftFunnel"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                LineIngredientsField = AccessTools.Field(lineType, "Ingredients");
                LineCountsField = AccessTools.Field(lineType, "Counts");
                LineHasWorkProp = AccessTools.Property(lineType, "HasWork");
                MethodInfo tryAcquire = AccessTools.Method(probeType, "TryAcquire");
                MethodInfo tryComplete = AccessTools.Method(funnelType, "TryComplete");
                // 与原 mod 的 Patch_ReservationManager_CanReserve_ProxyClaims 同一重载（显式签名定位）
                MethodInfo canReserve = AccessTools.Method(typeof(ReservationManager), "CanReserve",
                    new Type[]
                    {
                        typeof(Pawn),
                        typeof(LocalTargetInfo),
                        typeof(int),
                        typeof(int),
                        typeof(ReservationLayerDef),
                        typeof(bool)
                    });

                if (LineIngredientsField == null || LineCountsField == null || LineHasWorkProp == null
                    || tryAcquire == null || tryComplete == null || canReserve == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理取料认领：成员定位不完整"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);

                // ① TryAcquire：进入 → 清扫 + 释放本线旧认领 + 标记探针身份；成功 → 登记
                harmony.Patch(tryAcquire,
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_CraftIngredientClaims), nameof(Prefix_TryAcquire))),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_CraftIngredientClaims), nameof(Postfix_TryAcquire))),
                    finalizer: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_CraftIngredientClaims), nameof(Finalizer_TryAcquire))));

                // ② TryComplete：完成结算前释放该线认领（大方法，不会被内联）
                harmony.Patch(tryComplete, prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_CraftIngredientClaims), nameof(Prefix_TryComplete))));

                // ③ CanReserve：探针问询已认领的堆 → 不可用（原版选料器据此改选其它堆）
                harmony.Patch(canReserve, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_CraftIngredientClaims), nameof(Postfix_CanReserve))));

                Log.Message("[数字存储补丁] 已挂载：制作代理取料按线认领"
                            + "（同配方多线并行不再互相清零进度；设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理取料认领挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── ① TryAcquire：清扫 / 释放旧认领 / 标记探针身份 / 成功后登记 ──

        private static void Prefix_TryAcquire(Pawn w, object line)
        {
            currentProbeWorker = w;

            try
            {
                SweepStaleClaims();
                if (line != null)
                {
                    ReleaseLineClaims(line);    // 重新取活前先还旧账，避免双重计数
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理取料认领清扫失败（本条忽略）："
                              + ex.GetType().Name + ": " + ex.Message, 8864123);
            }
        }

        private static void Postfix_TryAcquire(bool __result, object line)
        {
            try
            {
                if (!__result || line == null)
                {
                    return;
                }

                DsPatchSettings s = DsPatchMod.Settings;
                if (s == null || !s.craftIngredientClaims)
                {
                    return;
                }

                Thing[] things = LineIngredientsField.GetValue(line) as Thing[];
                int[] counts = LineCountsField.GetValue(line) as int[];
                if (things == null || counts == null)
                {
                    return;
                }

                ReleaseLineClaims(line);        // 防御：正常路径已在 Prefix 释放

                ClaimRecord record = new ClaimRecord();
                for (int i = 0; i < things.Length && i < counts.Length; i++)
                {
                    Thing thing = things[i];
                    int count = counts[i];
                    if (thing == null || thing.Destroyed || count <= 0)
                    {
                        continue;
                    }

                    record.Things.Add(thing);
                    record.Counts.Add(count);
                    ThingClaims.TryGetValue(thing, out int total);
                    ThingClaims[thing] = total + count;
                }

                if (record.Things.Count > 0)
                {
                    LineClaims[line] = record;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理取料认领登记失败（本条忽略，不影响取活）："
                              + ex.GetType().Name + ": " + ex.Message, 8864121);
            }
        }

        // Harmony Finalizer 的异常参数名必须是 __exception
        private static Exception Finalizer_TryAcquire(Exception __exception)
        {
            currentProbeWorker = null;  // 兜底复位（Postfix 已覆盖正常路径）
            return __exception;         // 原样上抛，不吞异常
        }

        // ── ② TryComplete：完成结算前释放该线认领 ──

        private static void Prefix_TryComplete(object line)
        {
            if (line == null)
            {
                return;
            }

            try
            {
                ReleaseLineClaims(line);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理取料认领释放失败（本条忽略）："
                              + ex.GetType().Name + ": " + ex.Message, 8864122);
            }
        }

        // ── 清扫：!HasWork 的线不再占有认领（覆盖 StillValid 失败 / ReleaseLine 等小方法路径）──

        internal static void SweepStaleClaims()
        {
            if (LineClaims.Count == 0)
            {
                return;
            }

            List<object> dead = null;
            foreach (KeyValuePair<object, ClaimRecord> kv in LineClaims)
            {
                bool hasWork = false;
                try
                {
                    object v = LineHasWorkProp.GetValue(kv.Key, null);
                    hasWork = v is bool b && b;
                }
                catch
                {
                    hasWork = false;    // 线对象异常按死线处理
                }

                if (!hasWork)
                {
                    (dead ??= new List<object>()).Add(kv.Key);
                }
            }

            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                {
                    ReleaseLineClaims(dead[i]);
                }
            }
        }

        private static void ReleaseLineClaims(object line)
        {
            if (!LineClaims.TryGetValue(line, out ClaimRecord record))
            {
                return;
            }

            for (int i = 0; i < record.Things.Count; i++)
            {
                Thing thing = record.Things[i];
                if (ThingClaims.TryGetValue(thing, out int total))
                {
                    total -= record.Counts[i];
                    if (total > 0)
                    {
                        ThingClaims[thing] = total;
                    }
                    else
                    {
                        ThingClaims.Remove(thing);   // 归零即删：不留陈旧引用
                    }
                }
            }

            LineClaims.Remove(line);
        }

        // ── ③ CanReserve：探针问询已认领的堆 → false（原版选料器据此改选其它堆）──

        private static void Postfix_CanReserve(Pawn claimant, LocalTargetInfo target,
            bool ignoreOtherReservations, ref bool __result)
        {
            if (!__result
                || ignoreOtherReservations
                || currentProbeWorker == null
                || claimant != currentProbeWorker)
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftIngredientClaims || ThingClaims.Count == 0)
            {
                return;
            }

            Thing thing = target.Thing;
            if (thing == null)
            {
                return;
            }

            if (ThingClaims.TryGetValue(thing, out int claimed) && claimed > 0)
            {
                __result = false;
            }
        }
    }
}
