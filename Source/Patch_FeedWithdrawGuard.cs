using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 喂食抽取护栏（报告 INVEST-20；设计演化史见该报告 §10–§14）。
    ///
    /// <para>── 防什么 ──</para>
    /// <para>原 mod 的取食兜底 <c>Patch_FeedFromStorage.Postfix</c>（挂
    /// <c>FoodUtility.TryFindBestFoodSourceFor</c>）在原版找不到食物时，从核心抽一份落到
    /// 看护者脚下。它的 validator 缺「婴儿口径」检查 —— vanilla <c>FoodUtility.WillEat</c>
    /// 规定婴儿只能吃 <c>ingestible.babiesCanIngest == true</c> 的食物（全库只有奶类与
    /// 婴儿食品等少数 def；简单食物/精致餐/包装生存食品/米莉拉旅行口粮全都是 false）
    /// ⇒ 婴儿饿着且核心里没有婴儿食品时，它会抽出<b>婴儿根本吃不了</b>的食物塞给看护者，
    /// 喂食作业必败，每轮工作扫描再抽一份 ⇒ 脚边与背包的食物无限堆积
    /// （实测 ×2→×53；原版小人 + 原版婴儿同样复现）。这个缺口对一切「有特殊进食
    /// 规则的生物」都成立 —— 突变体、其它 mod 自定义种族等，婴儿只是最先暴露的例子，
    /// 因此本护栏的判定统一走 vanilla 的通用「能否吃」口径而非婴儿特判。</para>
    ///
    /// <para>── 五层机制 ──</para>
    /// <list type="number">
    /// <item><b>H' 抽取拦截（根修）</b>：[ThreadStatic] 守卫标记"当前线程正处于
    ///       <c>TryFindBestFoodSourceFor</c> 栈内"，对原 mod <c>HaulSourceContents.ExtractTo</c>
    ///       挂 Prefix：栈内且 <b>eater 吃不了该食物</b>（vanilla <c>WillEat</c> 通用口径：
    ///       婴儿 babiesCanIngest、突变体 overrideFoodType、RaceProps.CanEverEat、
    ///       饮食方案等）⇒ 阻止抽取（返回 null）⇒ 原 mod 拿到 null ⇒ 调用方"没找到"，
    ///       <b>什么都不产生</b>。吃得了的照常放行。</item>
    /// <item><b>S' 评分口径</b>：挂原 mod <c>FoodScoring.Score</c>，eater 吃不了的
    ///       def 评分垫底 ⇒ 原 mod 的选择器（只认评分最优、无次优回退）在有
    ///       「能吃的」候选时<b>自动改选能吃的</b>，不必走到 H' 拦截那一步。
    ///       婴儿、突变体与其它 mod 生物通用。</item>
    /// <item><b>G4' 总开关</b>：关闭时栈内一切食物抽取都被阻止，并禁掉原 mod 的
    ///       <c>DS_Consume</c> 自取作业 ⇒ 完整回到游戏原版行为（核心只当仓库）。</item>
    /// <item><b>婴儿主动供给</b>：S' 失效（如原 mod 改结构）时的兜底 —— 挂
    ///       <c>ChildcareUtility.FindBabyFoodForBaby</c> postfix，原版给不出（null）时
    ///       按婴儿口径主动从核心取一份（沿用 <see cref="Patch_FeedPatientReservable"/>
    ///       的主动供给模式，先复用地上「刚取出」的那份防高频重复抽取）。
    ///       它同时覆盖自动喂养与 WorkGiver 两条调用路 —— 原版
    ///       <c>WorkGiver_Scanner.HasJobOnThing</c> 转调 <c>JobOnThing</c>，
    ///       后者内部就调这个方法 ⇒ 供给成功则工作扫描自然放行，无需另挂 postfix。</item>
    /// <item><b>G1' 撤销层</b>（第二道防线，Priority.Last）：万一有漏网的"刚抽出"结果，
    ///       且当前小人自己能拿到同类（背包里 / 手上 / 可预留且可达；被别人预留的不算）
    ///       ⇒ 撤销结果。</item>
    /// </list>
    ///
    /// <para>── 两条守则（从回归中沉淀）──</para>
    /// <list type="bullet">
    /// <item>喂食查询的返回值决定"派不派活"，任何"这次不给"的拦截都会直接表现为
    ///       "喂不了" ⇒ 只能对「抽什么」做语义减法（如 H'），不能对「抽不抽/抽多快」
    ///       做减法（节流曾造成"婴儿食物在核心时无法喂食"的回归）。</item>
    /// <item>"被别人预留"对当前小人而言 ≠ "已有替代" —— 把它算作替代会误撤正当供给。</item>
    /// </list>
    ///
    /// <para>守卫标记只在本调用栈内有效，原 mod 其它取料链（药物/血袋/建造/燃料，
    /// 均不在 <c>TryFindBestFoodSourceFor</c> 栈内）零影响。</para>
    /// </summary>
    internal static class Patch_FeedWithdrawGuard
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.feedwithdrawguard";

        /// <summary>护栏累计拦截次数（只读诊断用）。</summary>
        internal static int RevokedCount;

        // ── [ThreadStatic] 守卫：标记"当前线程正在 TryFindBestFoodSourceFor 栈内" ──
        // RimWorld 游戏逻辑单线程；用深度计数容忍原版内部的递归调用（allowVenerated 分支）。
        [ThreadStatic]
        private static int guardDepth;

        [ThreadStatic]
        private static Pawn guardEater;

        [ThreadStatic]
        private static Pawn guardGetter;

        public static void Install()
        {
            try
            {
                MethodInfo find = AccessTools.Method(typeof(FoodUtility), "TryFindBestFoodSourceFor");
                MethodInfo extract = AccessTools.Method(typeof(HaulSourceContents), "ExtractTo");
                if (find == null || extract == null)
                {
                    Log.Warning("[数字存储补丁] 喂食抽取护栏：找不到挂点（TryFindBestFoodSourceFor="
                                + (find != null ? "找到" : "缺失")
                                + "，HaulSourceContents.ExtractTo="
                                + (extract != null ? "找到" : "缺失")
                                + "），本项跳过。");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);

                // ① 登记守卫（原 mod 的 FeedFromStorage postfix 在原方法之后跑，都在守卫栈内）
                harmony.Patch(find,
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_FeedWithdrawGuard), nameof(GuardEnter))),
                    finalizer: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_FeedWithdrawGuard), nameof(GuardLeave))));

                // ② 抽取拦截（H' baby 校验 + G4' 总开关）
                HarmonyMethod extractPrefix = new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FeedWithdrawGuard), nameof(ExtractToPrefix)));
                extractPrefix.priority = Priority.First;
                harmony.Patch(extract, prefix: extractPrefix);

                // ③ 撤销层作为第二道防线
                HarmonyMethod revokePostfix = new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FeedWithdrawGuard), nameof(LookupPostfix)));
                revokePostfix.priority = Priority.Last;
                harmony.Patch(find, postfix: revokePostfix);

                // ④ G4'：总开关关闭时也禁掉原 mod 的 DS_Consume 自取作业（回原版语义要完整）。
                InstallConsumeGate(harmony);

                // ⑤ 婴儿主动供给（模式先例 = Patch_FeedPatientReservable）。
                //    H' 拦掉"婴儿吃不了的"之后，原 mod 的取食兜底只认「评分最优的那一个、
                //    无次优回退」⇒ 最优往往是口粮/精致餐（被我们拦）⇒ 它直接放弃，
                //    即使核心里还有婴儿食品 ⇒ 右键报「没有婴儿可用的食物」。
                //    由我们按「婴儿口径」主动供给补上这一环。
                InstallBabySupply(harmony);

                // ⑥ 取食评分的进食口径（S'，通用化）：吃不了的食物评分垫底，
                //    让原 mod 的选择器自动改选能吃的（婴儿/突变体/其它 mod 生物通用）。
                InstallFoodScoreGate(harmony);

                Log.Message("[数字存储补丁] 已挂载：喂食抽取护栏（吃不了的食物不再从核心抽取——"
                            + "含婴儿口径与特殊生物，婴儿食物照常供给；另有「从核心取食」应急总开关。"
                            + "设置 →「核心库存可用性」）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 喂食抽取护栏安装失败（行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>G4' 关闭时禁掉 <c>ConsumptionHelper.TryCreateJob</c>（两个重载都挂）。</summary>
        private static void InstallConsumeGate(Harmony harmony)
        {
            try
            {
                Type consume = AccessTools.TypeByName("DigitalStorage.AI.ConsumptionHelper");
                if (consume == null)
                {
                    Log.Warning("[数字存储补丁] 喂食抽取护栏：找不到 ConsumptionHelper（原 mod 可能已改结构），"
                                + "「从核心取食」总开关对自取链不生效（查询链不受影响）。");
                    return;
                }
                MethodInfo gate = AccessTools.Method(typeof(Patch_FeedWithdrawGuard), nameof(ConsumeGatePrefix));
                int patched = 0;
                MethodInfo[] methods = consume.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == "TryCreateJob")
                    {
                        harmony.Patch(methods[i], prefix: new HarmonyMethod(gate));
                        patched++;
                    }
                }
                if (patched == 0)
                {
                    Log.Warning("[数字存储补丁] 喂食抽取护栏：ConsumptionHelper 里没有 TryCreateJob，自取链总开关不生效。");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 喂食抽取护栏：自取链总开关安装失败（查询链不受影响）："
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ConsumeGatePrefix(ref Job __result)
        {
            __result = null;
            DsPatchSettings s = DsPatchMod.Settings;
            if (s != null && !s.coreFeedFromStorage)
            {
                return false;   // G4' 关 ⇒ 不产生自取作业，回原版
            }
            return true;
        }

        // ────────────────── 守卫登记 ──────────────────

        private static void GuardEnter(Pawn getter, Pawn eater)
        {
            guardDepth++;
            if (guardDepth == 1)
            {
                // 喂食场景 eater=婴儿/病人；自取场景 eater==getter。以 eater 为准。
                guardEater = eater ?? getter;
                guardGetter = getter;
            }
        }

        private static Exception GuardLeave(Exception __exception)
        {
            guardDepth--;
            if (guardDepth <= 0)
            {
                guardDepth = 0;
                guardEater = null;
                guardGetter = null;
            }
            return __exception;
        }

        // ────────────────── 抽取拦截（核心） ──────────────────

        /// <summary>
        /// 挂原 mod <c>HaulSourceContents.ExtractTo</c>。只在喂食查找栈内、目标是可食物时干预；
        /// 药物/血袋/建造/燃料等其它取料链不在此栈内 ⇒ 零影响。
        /// <para>★ 守则：喂食查询必须每次都给得出结果 —— 抽出的每一份要么被喂掉、
        /// 要么 300t 后被自动收纳吸回，本身不会堆积；因此这里只做「抽什么」的语义判定，
        /// 不做「抽多快」的限流。</para>
        /// </summary>
        private static bool ExtractToPrefix(Thing t, ref int extracted, ref Thing __result)
        {
            extracted = 0;
            __result = null;
            if (guardDepth <= 0 || t == null || t.def == null || guardEater == null)
            {
                return true;   // 不在喂食查找栈内 ⇒ 与本护栏无关
            }
            if (!t.def.IsNutritionGivingIngestible || t.def.ingestible == null)
            {
                return true;   // 只管"食物"
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null)
            {
                return true;
            }

            // G4'：应急总开关关闭 ⇒ 喂食查找链不碰核心。
            if (!s.coreFeedFromStorage)
            {
                CountIt(s, "总开关关闭");
                return false;
            }

            // H'（通用"能否吃"口径，根修）：原 mod validator 缺失的进食判定由 vanilla
            // WillEat 统一补齐 —— 内含婴儿 babiesCanIngest、突变体 overrideFoodType、
            // RaceProps.CanEverEat、饮食方案与物品级政策等，覆盖婴儿、突变体与其它
            // mod 自定义生物。只拦「生理上吃不了」的；偏好性的（头衔/心情）不拦。
            // 异常时放行：宁可回到原 mod 行为（可能堆积），也不饿着。
            try
            {
                if (!guardEater.WillEat(t.def, guardGetter, false, false))
                {
                    CountIt(s, "吃不了(" + t.def.defName + ")");
                    return false;
                }
            }
            catch (Exception)
            {
                // 原版判定不可用 ⇒ 放行，回到原 mod 行为
            }

            return true;   // 放行：eater 吃得了 ⇒ 让原 mod 正常供给
        }

        // ────────────────── S'：取食评分的进食口径（通用化） ──────────────────

        /// <summary>
        /// 挂原 mod <c>FoodScoring.Score</c>（Priority.Last，晚于
        /// <see cref="Patch_CoreFoodScoring"/> 的心情修正）：eater
        /// <b>生理上吃不了</b>该食物（<c>WillEat</c> = false）时把评分压到
        /// -9999（低于原 mod 的一切拒斥语义）⇒ 原 mod 的选择器
        /// （<c>FindBestIncludingRemote</c>，第一个通过 validator 的无条件占据、
        /// 之后只有更高分才替换）在有「能吃的」候选时<b>自动改选能吃的</b>。
        /// <para>★ 这是对 H' 的上游补强：H' 只能在「最优已选错」时拦下抽取，
        /// S' 让选择器一开始就跳过吃不了的，核心里同时有「能吃的」与「吃不了的」时
        /// 不再需要走到拦截那一步（婴儿场景与其它 mod 生物通用）。</para>
        /// <para>口径与 H' 一致：careIfNotAcceptableForTitle=false、allowVenerated=false
        /// ⇒ 只拦生理性不可食，偏好性的（头衔/心情）不拦。</para>
        /// </summary>
        private static void InstallFoodScoreGate(Harmony harmony)
        {
            try
            {
                Type scoring = AccessTools.TypeByName("DigitalStorage.AI.FoodScoring");
                MethodInfo score = scoring == null
                    ? null
                    : AccessTools.Method(scoring, "Score", new[] { typeof(Pawn), typeof(ThingDef) });
                if (score == null)
                {
                    Log.Warning("[数字存储补丁] 喂食抽取护栏：找不到 FoodScoring.Score（原 mod 可能已改结构），"
                                + "取食评分的进食口径不可用（H' 抽取拦截仍生效）。");
                    return;
                }

                HarmonyMethod postfix = new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FeedWithdrawGuard), nameof(ScoreGatePostfix)));
                postfix.priority = Priority.Last;
                harmony.Patch(score, postfix: postfix);

                Log.Message("[数字存储补丁] 已挂载：取食评分进食口径（吃不了的食物评分垫底，"
                            + "原 mod 会自动改选能吃的；覆盖婴儿、突变体与其它 mod 生物）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 取食评分进食口径安装失败（H' 抽取拦截仍生效）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>原 mod 评分类里的「拒斥」哨兵量级；压到它以下即视为"不要选"。</summary>
        private const float ScoreRejectedFloor = -9999f;

        private static void ScoreGatePostfix(Pawn eater, ThingDef def, ref float __result)
        {
            if (eater == null || def == null || def.ingestible == null)
            {
                return;
            }
            if (__result <= ScoreRejectedFloor)
            {
                return;   // 已有更低的拒斥判定 ⇒ 不动
            }
            try
            {
                if (!eater.WillEat(def, null, false, false))
                {
                    __result = ScoreRejectedFloor;
                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][喂食护栏] 评分口径：eater 吃不了 "
                                    + def.defName + "，评分已垫底（选择器将改选能吃的）。");
                    }
                }
            }
            catch (Exception)
            {
                // 原版判定不可用 ⇒ 不改评分
            }
        }

        // ────────────────── 撤销层（第二道防线） ──────────────────

        [HarmonyPriority(Priority.Last)]
        private static void LookupPostfix(Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (!__result || foodSource == null || foodSource.def == null)
            {
                return;
            }
            // 只处理"刚从核心抽出"的（原 mod ExtractToFeet 打的 300 tick 标记）；
            // 被 H' 拦下的抽取根本不会走到这里。
            if (!CompAutoIngest.IsRecentlyWithdrawn(foodSource))
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.coreFeedFromStorage || !s.feedWithdrawGuard)
            {
                return;   // 总开关或撤销层关闭 ⇒ 不做第二道防线（H' 仍在）
            }
            if (HasAlternative(getter, eater, foodSource))
            {
                foodSource = null;
                foodDef = null;
                __result = false;
                RevokedCount++;
                if (s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][喂食护栏] 撤销层：手上/地图上已有同类可用，"
                                + "结果置空（累计 " + RevokedCount + " 次）。");
                }
            }
        }

        /// <summary>
        /// 「小人自己能拿到同类」判据：(a) 背包里有 <paramref name="eater"/> 能吃的
        /// （原版 <c>BestFoodInInventory</c>，内部走 WillEat ⇒ 自动含婴儿口径）；
        /// (b) 手上正拿着同类；(c) 地图上有同种食物且我可预留且可达。
        /// 被别人预留的<b>不算</b>替代 —— 对当前小人而言它不可用，算上会误撤正当供给。
        /// </summary>
        private static bool HasAlternative(Pawn getter, Pawn eater, Thing justPlaced)
        {
            if (getter == null || getter.MapHeld == null)
            {
                return false;
            }

            if (getter.inventory != null && eater != null && !eater.Destroyed)
            {
                try
                {
                    if (FoodUtility.BestFoodInInventory(getter, eater) != null)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 原版签名差异时退化为只查地图。
                }
            }

            if (getter.carryTracker != null && getter.carryTracker.CarriedThing != null
                && getter.carryTracker.CarriedThing.def == justPlaced.def)
            {
                return true;   // 手上正拿着同类 ⇒ 先处理手上的
            }

            Map map = getter.MapHeld;
            List<Thing> candidates = map.listerThings.ThingsOfDef(justPlaced.def);
            if (candidates == null)
            {
                return false;
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                Thing t = candidates[i];
                if (t == null || t == justPlaced || t.Destroyed || !t.Spawned)
                {
                    continue;
                }
                if (t.IsForbidden(getter))
                {
                    continue;
                }
                // 被预留的食物当前 getter 本来就用不了，不算替代（算上会误撤正当供给）
                if (getter.CanReserve(t)
                    && getter.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly))
                {
                    return true;   // 我自己下一轮就能拿到 ⇒ 不用抽
                }
            }
            return false;
        }

        // ────────────────── 工具 ──────────────────

        private static void CountIt(DsPatchSettings s, string why)
        {
            RevokedCount++;
            if (s.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁][喂食护栏] 已阻止一次从核心的取食（" + why
                            + "，累计 " + RevokedCount + " 次）——该食物喂不进去，避免产生堆积。");
            }
        }

        // ────────────────── 婴儿主动供给 ──────────────────
        //
        // H' 拦掉「婴儿吃不了的」之后暴露了原 mod 的一个结构缺口：
        // Patch_FeedFromStorage 只认 FindBestIncludingRemote 返回的「评分最优的那一个」，
        // 没有「次优回退」。核心里同时有口粮（MealSimple，评分高）和婴儿食品
        // （MealTerrible，评分低但 babiesCanIngest=true）时，它永远选口粮 ⇒ 被 H' 拦 ⇒
        // 直接放弃 ⇒ 即使核心里囤着婴儿食品，右键仍报「没有婴儿可用的食物」。
        //
        // 修法沿用本项目先例 Patch_FeedPatientReservable 的「主动供给」模式：
        // 在原 mod 放弃之后，由我们按「婴儿口径」从核心取食物并补上作业。
        //   · ChildcareUtility.FindBabyFoodForBaby 的 postfix：原版给不出（null）时主动供给；
        //     覆盖自动喂养与 WorkGiver 两条调用路。
        // 防重复：地图上若已有「刚从核心取出」的婴儿可食食物（IsRecentlyWithdrawn），
        // 直接返回它，不再抽新的（自动喂养的思考评估会高频调用本查找）。
        //
        // 曾对 WorkGiver_BottleFeedBaby.HasJobOnThing 另挂一个 postfix（想在"工作扫描
        // 阶段就被否"之前放行），但它从未生效：该方法由基类 WorkGiver_Scanner 声明并
        // 实现（`public virtual bool HasJobOnThing(...) => JobOnThing(...) != null;`），
        // BottleFeedBaby 与 FeedBabyManually 都只重写了 JobOnThing ⇒
        // AccessTools.Method 取回的是继承来的基类方法，Harmony 一律拒绝
        // （ArgumentException: You can only patch implemented methods/constructors），
        // 每次启动刷一条红字。而它本就是**冗余**的：原版 HasJobOnThing 转调 JobOnThing，
        // JobOnThing 内部又调 FindBabyFoodForBaby —— 我们已 postfix 了它，
        // 供给成功 ⇒ JobOnThing 非 null ⇒ HasJobOnThing 自然为 true。故直接撤掉。
        // （原挂点错误记入踩坑目录，见 docs/05-开发参考/05-踩坑目录.md）

        private static void InstallBabySupply(Harmony harmony)
        {
            try
            {
                MethodInfo find = AccessTools.Method(typeof(ChildcareUtility), "FindBabyFoodForBaby");
                if (find == null)
                {
                    Log.Warning("[数字存储补丁] 喂食抽取护栏：找不到婴儿供给挂点"
                                + "（ChildcareUtility.FindBabyFoodForBaby 缺失），婴儿主动供给不可用。");
                    return;
                }

                harmony.Patch(find, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_FeedWithdrawGuard), nameof(SupplyBabyFoodPostfix))));

                Log.Message("[数字存储补丁] 已挂载：婴儿食物主动供给（核心里没有婴儿食品时不干预；"
                            + "有的话即使原 mod 选中了婴儿吃不了的其它食物，也会改供婴儿食品）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 婴儿食物主动供给安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 挂 <c>ChildcareUtility.FindBabyFoodForBaby</c>。原版给不出（null）时：
        /// 先看地图上有没有「刚从核心取出、还没被消费」的婴儿可食食物（防高频重复抽取），
        /// 有就直接返回它；没有再从核心按婴儿口径取一份到喂食者脚下并返回。
        /// </summary>
        private static void SupplyBabyFoodPostfix(ref Thing __result, Pawn feeder, Pawn baby)
        {
            if (__result != null || feeder == null || baby == null || baby.Destroyed)
            {
                return;
            }
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.coreFeedFromStorage)
            {
                return;   // G4' 关 ⇒ 不从核心供给（回原版语义）
            }
            Map map = feeder.MapHeld;
            if (map == null)
            {
                return;
            }
            // 抢不到婴儿（被别人预留/抱走）⇒ 喂不成，供给只会变成地上堆积 ⇒ 不供给
            if (!feeder.CanReserve(baby))
            {
                return;
            }

            try
            {
                // 防重复：上一份刚抽出的还在地上 ⇒ 直接复用，不再抽新的
                Thing placed = FindPlacedBabyFood(feeder, baby, map);
                if (placed != null)
                {
                    __result = placed;
                    return;
                }

                Thing src = FindBabyEdibleInCore(baby, feeder, map);
                if (src == null)
                {
                    return;   // 核心里也没有婴儿食品 ⇒ 保持原版"没有"的结论
                }

                int count = 1;
                try
                {
                    count = FoodUtility.WillIngestStackCountOf(
                        baby, src.def, FoodUtility.GetNutrition(baby, src, src.def));
                    if (count < 1)
                    {
                        count = 1;
                    }
                }
                catch (Exception)
                {
                    count = 1;
                }

                Thing got = HaulSourceContents.ExtractToFeet(src, count, feeder);
                if (got == null)
                {
                    return;   // 取出失败（脚下放不下等）⇒ 保持"没有"
                }

                __result = got;
                if (s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][喂食护栏] 婴儿供给：原 mod 选了婴儿吃不了的食物被拦后，"
                                + "改从核心供给 " + got.LabelShortCap + " ×" + got.stackCount
                                + " 给 " + baby.LabelShortCap + "。");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 婴儿主动供给失败（本次保持原行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510501);
            }
        }

        /// <summary>地图上「刚从核心取出、婴儿可食、喂食者可用」的未消费供给（防高频重复抽取）。</summary>
        private static Thing FindPlacedBabyFood(Pawn getter, Pawn baby, Map map)
        {
            List<Thing> foods = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSource);
            if (foods == null)
            {
                return null;
            }
            for (int i = 0; i < foods.Count; i++)
            {
                Thing t = foods[i];
                if (t == null || t.Destroyed || !t.Spawned || t.def?.ingestible == null)
                {
                    continue;
                }
                if (!t.def.ingestible.babiesCanIngest || !baby.RaceProps.Eats(t.def.ingestible.foodType))
                {
                    continue;
                }
                if (!CompAutoIngest.IsRecentlyWithdrawn(t))
                {
                    continue;   // 不是我们刚取出的（原版自己的婴儿食物不归我们管）
                }
                if (t.IsForbidden(getter) || !getter.CanReserve(t))
                {
                    continue;
                }
                return t;
            }
            return null;
        }

        /// <summary>
        /// 按婴儿口径在核心里找一份可喂的食物：复用原 mod 的
        /// <c>HaulSourceContents.FindBestIncludingRemote</c>（含跨图核心），validator 用
        /// vanilla <c>WillEat</c> 的婴儿口径（babiesCanIngest + RaceProps + 饮食方案）。
        /// </summary>
        private static Thing FindBabyEdibleInCore(Pawn baby, Pawn getter, Map map)
        {
            ThingFilter filter = null;
            if (baby.foodRestriction != null)
            {
                FoodPolicy policy = baby.foodRestriction.GetCurrentRespectedRestriction(getter);
                if (policy != null)
                {
                    filter = policy.filter;
                }
            }
            ThingFilter captured = filter;
            return HaulSourceContents.FindBestIncludingRemote(map, null, delegate (Thing t)
            {
                if (t == null || t.def == null || t.def.ingestible == null)
                {
                    return false;
                }
                if (!t.def.IsNutritionGivingIngestible || !t.def.IsIngestible)
                {
                    return false;
                }
                if (!t.def.ingestible.babiesCanIngest)
                {
                    return false;   // ★ 婴儿口径：与 vanilla WillEat 同款
                }
                if (!baby.RaceProps.Eats(t.def.ingestible.foodType))
                {
                    return false;
                }
                return captured == null || captured.Allows(t.def);
            });
        }
    }
}
