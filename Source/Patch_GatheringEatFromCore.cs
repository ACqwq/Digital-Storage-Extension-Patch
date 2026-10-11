using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「在聚集区进食」能从数字核心取食物（v0.48，D2）。
    ///
    /// ── 现象 ──
    /// 小人参加聚会/仪式时按职责要在**聚集区**里吃东西；若食物**全存数字核心**，
    /// 它找不到食物就不吃（聚会的进食环节空转）。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/JobGiver_EatInGatheringArea.cs</c>）──
    /// <code>
    /// private Thing FindFood(Pawn pawn, IntVec3 gatheringSpot)
    /// {
    ///     Predicate&lt;Thing&gt; validator = x =&gt; ... &amp;&amp; GatheringsUtility.InGatheringArea(x.Position, gatheringSpot, pawn.Map) &amp;&amp; ...;
    ///     return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree),
    ///         PathEndMode.ClosestTouch, TraverseParms.For(TraverseMode.NoPassClosedDoors),
    ///         14f, validator, null, 0, 12);          // ★ 只查已生成 + 14 格 + 必须在聚集区内
    /// }
    /// </code>
    ///
    /// ── 做法（预填 · 补法 1；★ 落点必须是**聚集点**而不是小人脚下）──
    /// 这是"预填"里少见的**必须指定落点**的一例：原版校验含
    /// <c>GatheringsUtility.InGatheringArea(x.Position, gatheringSpot, map)</c>，
    /// 若把食物取到小人脚下，而小人恰好站在聚集区**外**，原版仍会判它不合格。
    /// ⇒ 所以用 <c>CoreContentAccess.ExtractOneAt(..., gatheringSpot, map)</c> **直接落在聚集点上**，
    /// 之后原版自己的查找与校验全部命中，进食链一字不改。
    ///
    /// <para>判据仍**复刻原版那次查找**（同 14 格、同 PathEndMode、同一整套校验），
    /// 不干预"原版自己找得到"的情形。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_GatheringEatFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.gatheringEatFromCore;
        }

        /// <summary>原版那一整套校验（原样复刻），用于判断"原版自己找不找得到"。</summary>
        private static bool VanillaValidator(Pawn pawn, IntVec3 gatheringSpot, Thing x)
        {
            if (!x.IngestibleNow) return false;
            if (!x.def.IsNutritionGivingIngestible) return false;
            if (!GatheringsUtility.InGatheringArea(x.Position, gatheringSpot, pawn.Map)) return false;
            if (x.def.IsDrug) return false;
            if ((int)x.def.ingestible.preferability <= 4) return false;
            if (!pawn.WillEat(x)) return false;
            if (x.IsForbidden(pawn)) return false;
            if (!x.IsSociallyProper(pawn)) return false;
            return pawn.CanReserve(x);
        }

        /// <summary>
        /// 核心物品用的校验：与上面同款，但**去掉"位置在聚集区内"那一条** ——
        /// 它此刻还在核心里，取出来会**落在聚集点**，那一条自然成立。
        /// </summary>
        private static bool CoreValidator(Pawn pawn, Thing x)
        {
            if (x?.def?.ingestible == null) return false;
            if (!x.def.IsNutritionGivingIngestible) return false;
            if (x.def.IsDrug) return false;
            if ((int)x.def.ingestible.preferability <= 4) return false;
            if (!pawn.WillEat(x)) return false;
            if (x.IsForbidden(pawn)) return false;
            return true;
        }

        /// <summary>复刻原版那次查找（只读）：返回 true 就说明原版自己找得到。</summary>
        private static bool HasUsableFood(Pawn pawn, Map map, IntVec3 gatheringSpot)
        {
            return GenClosest.ClosestThingReachable(
                       pawn.Position, map,
                       ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree),
                       PathEndMode.ClosestTouch,
                       TraverseParms.For(TraverseMode.NoPassClosedDoors),
                       14f,
                       x => VanillaValidator(pawn, gatheringSpot, x),
                       null, 0, 12) != null;
        }

        [HarmonyPatch(typeof(JobGiver_EatInGatheringArea), "FindFood")]
        internal static class Patch_FindFood
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn, IntVec3 gatheringSpot)
            {
                if (pawn == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !gatheringSpot.IsValid)
                {
                    return;
                }

                // 原版自己找得到 ⇒ 不干预。
                if (HasUsableFood(pawn, map, gatheringSpot))
                {
                    return;
                }

                try
                {
                    // ★ 落在**聚集点**（不是小人脚下）—— 见类注释。
                    Thing taken = CoreContentAccess.ExtractOneAt(
                        x => CoreValidator(pawn, x), 1, gatheringSpot, map);

                    if (taken == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版
                    }

                    FixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][聚集区进食] 聚集区里没有食物，已从数字核心取出 "
                                    + taken.LabelShort + " 1 份到聚集点 @ " + taken.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取食物（聚集区进食）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510370);
                }
            }
        }
    }
}
