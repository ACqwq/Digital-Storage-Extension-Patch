using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复（配套 <see cref="Patch_SkyLanternTakeWoodFromCore"/>）：天灯节仪式**启动检查**
    /// 不认数字核心里的原木 ⇒ 界面报「缺少原木」、参与者被标灰、仪式无法开始。
    ///
    /// <para><b>机制（原版反编译实锤，1.7）</b>：<c>RitualObligationTargetWorker_SkyLanterns.GetBlockingIssues</c>
    /// 逐参与者算缺口 <c>woodPerParticipant - 随身原木</c>，然后**只扫地图上已生成的原木**
    /// （<c>map.listerThings.ThingsOfDef(WoodLog)</c> + <c>IsForbidden/CanReserveAndReach</c>，
    /// 一条共享扣减表避免多人重复认领同一堆）；填不上的参与者进 <c>blocked</c> 集合
    /// （<c>ShouldGrayOut</c> 用它标灰）并产出 <c>RitualTargetWoodInfo</c> 阻挡文案。</para>
    /// <para>核心里的原木不在 listerThings 里 ⇒ 计数恒为 0 ⇒ 存木全在核心的殖民地无法开仪式。</para>
    ///
    /// <para><b>修法</b>：Prefix **整体替换**——这是 yield 迭代器（无法用 Postfix 过滤单条文案），
    /// 且 <c>blocked</c> 集合必须与文案同步重算。实现与原版**逐行同构**，只在"地面层填不上"之后
    /// 加一层：从**核心木料池**（本图可用核心中的原木总数）里扣减该参与者的缺口；池子同样会耗尽，
    /// 因此"真不够"时报的文案与原版一致（数字也对得上）。</para>
    ///
    /// <para><b>与原版的唯一有意差异</b>：原版存在一个边角——参与者缺口为 0 时，
    /// 只要地图上**一根可及原木都没有**也会被标为 blocked（列表为空的循环空转）；
    /// 本实现中这类参与者由核心池（≥0 恒成立）兜住、不再被误标灰。这正是"全存核心"场景
    /// 能跑通所必需的行为。</para>
    ///
    /// <para><b>边界</b>：反射取不到 <c>def</c> / <c>blocked</c>（未来改结构）或任何异常时，
    /// 直接放行原实现（return true）。与取木侧共用开关 <c>skyLanternFromCore</c>，
    /// 避免"能启动却永远取不到木"的坏组合。</para>
    /// </summary>
    [HarmonyPatch(typeof(RitualObligationTargetWorker_SkyLanterns), "GetBlockingIssues")]
    internal static class Patch_SkyLanternStartCheck
    {
        private static FieldInfo defField;
        private static FieldInfo blockedField;

        [HarmonyPrefix]
        private static bool Prefix(
            RitualObligationTargetWorker_SkyLanterns __instance,
            TargetInfo target,
            RitualRoleAssignments assignments,
            ref IEnumerable<string> __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.skyLanternFromCore)
            {
                return true;
            }

            if (__instance == null || assignments == null || target.Map == null)
            {
                return true;
            }

            try
            {
                RitualObligationTargetFilterDef def =
                    ResolveDef(__instance) as RitualObligationTargetFilterDef;
                HashSet<Pawn> blocked = ResolveBlocked();
                if (def == null || blocked == null)
                {
                    return true;   // 结构变了 ⇒ 交回原实现
                }

                List<string> issues = new List<string>();
                Dictionary<Thing, int> claimed = new Dictionary<Thing, int>();
                List<Thing> groundWood = target.Map.listerThings.ThingsOfDef(ThingDefOf.WoodLog);
                List<Pawn> participants = assignments.Participants;

                // 核心木料池：本图可用核心里原木的 stackCount 合计（上限无所谓，取满即够用）。
                int corePool = CoreContentAccess.CountMatching(
                    target.Map, t => t.def == ThingDefOf.WoodLog);

                bool allOk = true;
                int blockedCount = 0;
                blocked.Clear();

                for (int p = 0; p < participants.Count; p++)
                {
                    Pawn pawn = participants[p];
                    if (pawn == null)
                    {
                        continue;
                    }

                    int need = Math.Max(def.woodPerParticipant - pawn.inventory.Count(ThingDefOf.WoodLog), 0);
                    bool covered = false;

                    // —— 第一层：地图地面（与原版逐行同构：共享扣减表 + 禁止/可预约可达）——
                    for (int i = 0; i < groundWood.Count; i++)
                    {
                        Thing thing = groundWood[i];
                        if (thing.IsForbidden(pawn)
                            || !pawn.CanReserveAndReach(thing, PathEndMode.Touch, pawn.NormalMaxDanger()))
                        {
                            continue;
                        }

                        int available = thing.stackCount;
                        if (claimed.TryGetValue(thing, out int used))
                        {
                            available = Math.Max(available - used, 0);
                        }
                        if (available >= need)
                        {
                            claimed[thing] = (claimed.TryGetValue(thing, out int old) ? old : 0) + need;
                            covered = true;
                            break;
                        }
                    }

                    // —— 第二层（本补丁新增）：核心木料池补缺口 ——
                    if (!covered && corePool >= need)
                    {
                        corePool -= need;
                        covered = true;
                    }

                    if (!covered)
                    {
                        blocked.Add(pawn);
                        allOk = false;
                        blockedCount++;
                    }
                }

                if (!allOk)
                {
                    TaggedString text = "RitualTargetWoodInfo".Translate(
                        def.woodPerParticipant, def.woodPerParticipant * participants.Count);
                    if (blockedCount == 1)
                    {
                        text += $" {"RitualTargetWoodInfoAppend".Translate(blockedCount)}";
                    }
                    else
                    {
                        text += $" {"RitualTargetWoodInfoAppendMultiple".Translate(blockedCount)}";
                    }
                    issues.Add(text);
                }

                __result = issues;
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 天灯节启动检查补核心木料失败（交回原实现）："
                              + ex.GetType().Name + ": " + ex.Message, 8830162);
                return true;
            }
        }

        private static object ResolveDef(RitualObligationTargetWorker_SkyLanterns instance)
        {
            if (defField == null)
            {
                defField = AccessTools.Field(typeof(RitualObligationTargetWorker_SkyLanterns), "def");
            }
            return defField?.GetValue(instance);
        }

        private static HashSet<Pawn> ResolveBlocked()
        {
            if (blockedField == null)
            {
                blockedField = AccessTools.Field(typeof(RitualObligationTargetWorker_SkyLanterns), "blocked");
            }
            return blockedField?.GetValue(null) as HashSet<Pawn>;
        }
    }
}
