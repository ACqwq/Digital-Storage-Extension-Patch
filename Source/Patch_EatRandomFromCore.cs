using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「随机进食（EatRandom）」能从数字核心取食物（v0.48，D3）。
    ///
    /// ── 现象 ──
    /// 该行为会让小人随手找点东西吃；若食物**全存数字核心**，它找不到就不吃。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/JobGiver_EatRandom.cs</c>）──
    /// <code>
    /// protected override Job TryGiveJob(Pawn pawn)
    /// {
    ///     if (pawn.Downed) return null;
    ///     Predicate&lt;Thing&gt; validator = t =&gt; t.def.category == ThingCategory.Item &amp;&amp; t.IngestibleNow
    ///                                          &amp;&amp; pawn.RaceProps.CanEverEat(t) &amp;&amp; pawn.CanReserve(t);
    ///     Thing thing = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForGroup(ThingRequestGroup.HaulableAlways),
    ///         PathEndMode.OnCell, TraverseParms.For(pawn), 10f, validator);   // ★ 只查已生成 + 10 格
    ///     if (thing == null) return null;
    ///     return JobMaker.MakeJob(JobDefOf.Ingest, thing);
    /// }
    /// </code>
    ///
    /// ── 做法（预填 · 补法 1）──
    /// Prefix <c>TryGiveJob</c>：复刻上面那次查找（原版自己找得到就不干预）；
    /// 找不到而**核心里有**符合"是物品 / 现在能吃 / 这个种族能吃"的东西时，
    /// 取 1 份到小人脚下 —— 原版随后的 10 格查找自然命中，进食链一字不改。
    ///
    /// <para>核心里那一侧**不做 <c>CanReserve</c> 判断**：它此刻还没生成，
    /// 取出来落到地上后由原版自己去 <c>CanReserve</c>（那才是准的）。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_EatRandomFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.eatRandomFromCore;
        }

        /// <summary>原版那一套校验（原样复刻），用于判断"原版自己找不找得到"。</summary>
        private static bool VanillaValidator(Pawn pawn, Thing t)
        {
            if (t.def.category != ThingCategory.Item) return false;
            if (!t.IngestibleNow) return false;
            if (!pawn.RaceProps.CanEverEat(t)) return false;
            return pawn.CanReserve(t);
        }

        /// <summary>核心物品用的校验：同款，但**去掉 <c>CanReserve</c>**（见类注释）。</summary>
        private static bool CoreValidator(Pawn pawn, Thing x)
        {
            if (x?.def == null) return false;
            if (x.def.category != ThingCategory.Item) return false;
            if (!x.IngestibleNow) return false;
            return pawn.RaceProps.CanEverEat(x);
        }

        private static bool HasUsableFood(Pawn pawn, Map map)
        {
            return GenClosest.ClosestThingReachable(
                       pawn.Position, map, ThingRequest.ForGroup(ThingRequestGroup.HaulableAlways),
                       PathEndMode.OnCell, TraverseParms.For(pawn), 10f,
                       t => VanillaValidator(pawn, t)) != null;
        }

        [HarmonyPatch(typeof(JobGiver_EatRandom), "TryGiveJob")]
        internal static class Patch_TryGiveJob
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn)
            {
                if (pawn == null || !Enabled() || pawn.Downed)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                if (HasUsableFood(pawn, map))
                {
                    return;   // 原版自己找得到 ⇒ 不干预
                }

                try
                {
                    Thing taken = CoreContentAccess.ExtractOne(pawn, x => CoreValidator(pawn, x), 1, map);
                    if (taken == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版
                    }

                    FixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][随机进食] 附近没有食物，已从数字核心取出 "
                                    + taken.LabelShort + " 1 份到脚下 @ " + taken.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取食物（随机进食）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510371);
                }
            }
        }
    }
}
