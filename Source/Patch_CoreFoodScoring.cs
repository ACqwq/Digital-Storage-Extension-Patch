using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复「从数字核心取食时，成年人被塞了**婴儿食物**」—— 会掉心情，而核心里明明还有包装食品。
    /// （实测 2026-10-08；数字仓库里同时有婴儿食品与包装生存食品时，必拿婴儿食品。）
    ///
    /// <para>── 根因（反编译 + Def 数值核实）──</para>
    /// <para>原 mod 选食物靠自己的 <c>FoodScoring.Score(Pawn, ThingDef)</c>，它只看：</para>
    /// <list type="bullet">
    /// <item>preferability（<c>MealTerrible</c> **不在任何扣分分支里**）；</item>
    /// <item>optimalityOffset（种族 / 特质）；</item>
    /// <item>两条特例：人肉、生人肉。</item>
    /// </list>
    /// <para>**完全不看吃下去的心情后果（<c>tasteThought</c>）。** 而：</para>
    /// <list type="bullet">
    /// <item><c>BabyFood</c>：<c>preferability = MealTerrible</c>（无扣分）、
    /// <c>tasteThought = AteBabyFood</c>（成年人吃了掉心情）、<c>babiesCanIngest = true</c> ⇒ **得 300 分**；</item>
    /// <item><c>MealSurvivalPack</c>（包装生存食品）：<c>MealSimple</c> + <c>optimalityOffsetHumanlikes = -5</c>
    /// ⇒ **得 295 分**。</item>
    /// </list>
    /// <para>⇒ 300 &gt; 295 ⇒ **每次都挑婴儿食物**。原版自取时不会这样，因为原版的
    /// <c>BestFoodSourceOnMap</c> 会把心情后果算进排序。</para>
    ///
    /// <para>── 修法：给评分补上心情这一项（不改选择逻辑本身）──</para>
    /// <para>Postfix 挂在原 mod 的 <c>FoodScoring.Score</c> 上，把该食物的
    /// <c>tasteThought</c> 心情值**用原 mod 自己的 <c>EvalMoodCurve</c> 换算后加进得分**
    /// （反射调用私有方法 ⇒ 不复制曲线，原 mod 改曲线也跟着变）。<br/>
    /// 例：<c>AteBabyFood</c> 走曲线得 −70 ⇒ 婴儿食物 300 → 230，低于包装食品 295 ⇒ **改选包装食品**。</para>
    ///
    /// <para>── 刻意不为 ──</para>
    /// <list type="bullet">
    /// <item>不"一刀切拒绝婴儿食物"：核心里只有婴儿食物时也让人吃（总比饿死好），
    /// 只是**排在更合适的食物之后**；</item>
    /// <item>不动 preferability 分支（那是原 mod 的取舍）；</item>
    /// <item>不复制原版任何判定逻辑（铁律 ⑪）。</item>
    /// </list>
    /// </summary>
    internal static class Patch_CoreFoodScoring
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.corefoodscoring";

        /// <summary>原 mod 的私有评分曲线（反射取一次，做成委托以免每次反射调用）。</summary>
        private static Func<float, float> moodCurve;

        /// <summary>原 mod 判定"不可食用"时的哨兵值（低于它就不用再算）。</summary>
        private const float Rejected = -9990f;

        public static void Install()
        {
            try
            {
                Type scoring = AccessTools.TypeByName("DigitalStorage.AI.FoodScoring");
                MethodInfo score = (scoring == null)
                    ? null
                    : AccessTools.Method(scoring, "Score", new[] { typeof(Pawn), typeof(ThingDef) });
                MethodInfo curve = (scoring == null)
                    ? null
                    : AccessTools.DeclaredMethod(scoring, "EvalMoodCurve", new[] { typeof(float) });

                if (score == null || curve == null)
                {
                    Log.Warning("[数字存储补丁] 取食排序修正：定位失败"
                                + "（FoodScoring=" + (scoring != null ? "找到" : "缺失")
                                + "，Score=" + (score != null ? "找到" : "缺失")
                                + "，EvalMoodCurve=" + (curve != null ? "找到" : "缺失")
                                + "），本项跳过（原 mod 可能已改结构）。");
                    return;
                }

                moodCurve = (Func<float, float>)curve.CreateDelegate(typeof(Func<float, float>));

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(score, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_CoreFoodScoring), nameof(Postfix))));

                Log.Message("[数字存储补丁] 已挂载：从核心取食时按心情后果排序"
                            + "（不再给成年人挑婴儿食物；设置 →「核心库存可用性」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 取食排序修正安装失败（行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s == null || s.coreFoodScoringMood;   // 纯修复：默认开
        }

        private static void Postfix(Pawn eater, ThingDef def, ref float __result)
        {
            if (!Enabled() || moodCurve == null || eater == null || def?.ingestible == null)
            {
                return;
            }
            if (__result <= Rejected)
            {
                return;   // 原 mod 已判为不可食用，别改它的结论
            }
            if (eater.needs?.mood == null)
            {
                return;   // 没有心情需求（多数动物）⇒ 原版也不算心情，保持原样
            }

            ThoughtDef thought = def.ingestible.tasteThought;
            if (thought == null || thought.stages == null || thought.stages.Count == 0)
            {
                return;
            }
            // 苦行者不吃"口味"这套（与原版 ThoughtsFromIngesting 同口径）
            if (eater.story?.traits != null && eater.story.traits.HasTrait(TraitDefOf.Ascetic))
            {
                return;
            }

            try
            {
                __result += moodCurve(thought.stages[0].baseMoodEffect);   // 负值 ⇒ 扣分
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 取食排序修正计算失败（本次不改评分）："
                              + ex.GetType().Name + ": " + ex.Message, 66510402);
            }
        }
    }
}
