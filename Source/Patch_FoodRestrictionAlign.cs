using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 取食判据对齐（v0.43，**实验性，默认关**）：从数字核心拿食物时，
    /// 补上"饮食方案（食物管制方案）之外"的食物规则。
    ///
    /// <para>── 背景（报告 38 §2/§8，全部反编译核实）──</para>
    /// <para>原 mod 的四条取食入口**都检查了饮食方案本身** —— 方案不是漏洞。
    /// 缺的是方案之外的几条规则：</para>
    /// <list type="bullet">
    /// <item>喂食路径（<c>Patch_FeedFromStorage</c>）是手工过滤，只判了
    /// "营养可食 + 政策筛选器 + 种族能吃"，**没有**头衔食物规则、动物崇拜戒律、mutant 覆盖、婴儿规则；</item>
    /// <item>自取/娱乐路径（<c>ConsumptionHelper</c> → <c>DS_Consume</c> 作业）用的是
    /// <c>WillEat</c> 的 **def 重载**，没有"饭菜原料级"的戒律检查（用崇拜动物肉做的饭会被吃掉）。</item>
    /// </list>
    ///
    /// <para>── 做法：复核，而不是重写 ──</para>
    /// <para>在原 mod **得出结果之后**再挂一层，用原版**同款**判据
    /// <c>FoodUtility.WillEat(Thing)</c> 复核一次。该重载内部已包含：def 级政策、头衔食物规则、
    /// 戒律的 def 级**与原料级**、物品级政策筛选（含腐烂等特殊筛选）、baby/mutant 判定。
    /// 判据与原版逐字相同 ⇒ **只会更严、不会更松**：本来能吃的东西照旧能吃，只有原版也会拒绝的才被拦下。
    /// 刻意**不复制**原版判定逻辑（那样将来原版改判据会失同步）。</para>
    ///
    /// <para>── 两个挂点 ──</para>
    /// <list type="number">
    /// <item>挂在**原版** <c>FoodUtility.TryFindBestFoodSourceFor</c> 之上、优先级
    /// <c>Priority.Last</c>（原 mod 的同类 postfix 是默认优先级 ⇒ 本补丁一定在它**之后**复核）。
    /// —— v0.52 修正：原先挂的是原 mod 的 <c>Patch_FeedFromStorage.Postfix</c>，但那是个 **void** 方法，
    /// 补丁里的 <c>__result</c> 会被 Harmony 当成"原方法返回值" ⇒ 抛
    /// <c>Cannot get result from void method</c>，**本项整体安装失败**（实测）。</item>
    /// <item>挂原 mod <c>ConsumptionHelper.TryCreateJob(Pawn, Predicate&lt;Thing&gt;)</c>
    /// （DS_Consume 作业的唯一出口，娱乐进食走同一入口）；作业里 <c>TargetIndex.A</c> 就是食物。</item>
    /// </list>
    ///
    /// <para>── 已知副作用 ──</para>
    /// <para>喂食路径若被判不通过，原 mod **已经把食物取到了小人脚下** ⇒ 地上会短暂留一堆。
    /// 该堆带"刚取出"标记（<c>CompAutoIngest.MarkWithdrawn</c>），若干 tick 后被自动收纳吸回核心，
    /// **不会丢东西**。这是"事后复核"的代价，也是本项默认关的原因之一。</para>
    ///
    /// <para>── 不动的地方 ──</para>
    /// <para>药品/化学需求路径不归食物政策管（原版亦然）⇒ 只对
    /// <c>IsNutritionGivingIngestible</c> 的物品复核；不碰原版 <c>FoodUtility</c> 本身（全局影响）。</para>
    /// </summary>
    internal static class Patch_FoodRestrictionAlign
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.foodrestrictionalign";

        private static bool loggedBlocked;

        public static void Install()
        {
            try
            {
                // ★ v0.52 修正：**不再挂原 mod 的 Patch_FeedFromStorage.Postfix**。
                //   那个方法是 void —— 它自己是原版方法的 postfix，`__result` 只是它的一个 ref 参数；
                //   而 Harmony 把补丁里名为 `__result` 的参数解释成"原方法的返回值" ⇒
                //   给 void 方法挂带 `__result` 的补丁会当场抛
                //   "Cannot get result from void method"（实测：本项因此**整体安装失败**，行为静默保留原样）。
                //   改为挂**原版** FoodUtility.TryFindBestFoodSourceFor，并声明 Priority.Last ——
                //   原 mod 的 postfix 是默认优先级（Normal），所以本补丁必然在它**之后**运行，
                //   复核时机与原来完全一致，而且不再依赖原 mod 的内部实现（它重构也不会失效）。
                MethodInfo feed = AccessTools.Method(typeof(FoodUtility), "TryFindBestFoodSourceFor",
                    new[] { typeof(Pawn), typeof(Pawn), typeof(bool),
                            typeof(Thing).MakeByRefType(), typeof(ThingDef).MakeByRefType(),
                            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                            typeof(FoodPreferability) });
                if (feed == null)
                {
                    // 兜底：原版签名变了就退回按名字找
                    feed = AccessTools.Method(typeof(FoodUtility), "TryFindBestFoodSourceFor");
                }
                Type consumeType = AccessTools.TypeByName("DigitalStorage.AI.ConsumptionHelper");
                if (feed == null || consumeType == null)
                {
                    Log.Warning("[数字存储补丁] 取食判据对齐：找不到挂点"
                                + "（原版 TryFindBestFoodSourceFor="
                                + (feed != null ? "找到" : "缺失")
                                + "，原 mod ConsumptionHelper="
                                + (consumeType != null ? "找到" : "缺失")
                                + "），本项跳过（原版/原 mod 可能已改结构）。");
                    return;
                }

                MethodInfo consume = AccessTools.Method(
                    consumeType, "TryCreateJob", new[] { typeof(Pawn), typeof(Predicate<Thing>) });

                if (consume == null)
                {
                    Log.Warning("[数字存储补丁] 取食判据对齐：定位方法失败"
                                + "（TryCreateJob=缺失），本项跳过。");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(feed, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FoodRestrictionAlign), nameof(Postfix_FeedFromStorage))));
                harmony.Patch(consume, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FoodRestrictionAlign), nameof(Postfix_TryCreateJob))));

                Log.Message("[数字存储补丁] 已挂载：取食判据对齐（实验性，默认关）——"
                            + "从核心取食时补上头衔食物规则与动物崇拜戒律（含原料级）；设置 →「存储核心」可开。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 取食判据对齐安装失败（行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.foodRestrictionAlign;
        }

        /// <summary>
        /// 挂在**原版** <c>FoodUtility.TryFindBestFoodSourceFor</c> 之后（<see cref="HarmonyPriority"/> = Last，
        /// 因此晚于原 mod 默认优先级的 postfix）：原 mod 刚把候选塞进
        /// <c>foodSource</c>，这里用原版判据复核；不通过就撤销这次"找到食物"。
        /// </summary>
        [HarmonyPriority(Priority.Last)]
        private static void Postfix_FeedFromStorage(
            Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (!Enabled() || !__result || foodSource == null || eater == null)
            {
                return;
            }
            if (foodSource.def == null || !foodSource.def.IsNutritionGivingIngestible)
            {
                return;   // 药品等不归食物政策管（原版亦然）
            }
            if (FoodUtility.WillEat(eater, foodSource, getter, true, false))
            {
                return;
            }

            __result = false;
            foodSource = null;
            foodDef = null;
            Report(eater);
        }

        /// <summary>
        /// 挂在原 mod <c>ConsumptionHelper.TryCreateJob</c> 之后：
        /// 作业里的 <c>TargetIndex.A</c> 就是被选中的食物，不通过就把作业作废。
        /// </summary>
        private static void Postfix_TryCreateJob(Pawn pawn, ref Job __result)
        {
            if (!Enabled() || __result == null || pawn == null)
            {
                return;
            }
            Thing food = __result.GetTarget(TargetIndex.A).Thing;
            if (food == null || food.def == null || !food.def.IsNutritionGivingIngestible)
            {
                return;
            }
            if (FoodUtility.WillEat(pawn, food, pawn, true, false))
            {
                return;
            }

            __result = null;
            Report(pawn);
        }

        private static void Report(Pawn pawn)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.diagnosticsEnabled || loggedBlocked)
            {
                return;
            }
            loggedBlocked = true;
            Log.Message("[数字存储补丁] 取食判据对齐：拦下了一次从核心取食"
                        + "（该食物不被原版规则接受：头衔限制 / 崇拜动物戒律 / 饮食方案）。"
                        + "若命中频繁，可在设置里关掉本项回到原 mod 行为。（本条只记一次）");
        }
    }
}
