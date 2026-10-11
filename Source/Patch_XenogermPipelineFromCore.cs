using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// ★ **F1（整条异种胚芽链）**：让「组装异种胚芽 → 运进装配器 → 植入」全链都能用数字核心里的
    /// **超凡胶囊（ArchiteCapsule）** 与 **异种胚芽（Xenogerm）**（v0.47b）。
    ///
    /// ── 范围（★ 用户澄清后的完整范围）──
    /// 游戏里与"胶囊/胚芽"有关的其实是**两样东西、三个环节**：
    /// <list type="number">
    ///   <item>**超凡胶囊**（组装超凡基因用）—— ① **组装界面**能不能开始（缺它不能确认）；</item>
    ///   <item>**异种胚芽**（组装完成后产出的成品）—— ② **运进装配器**；③ 放进核心后
    ///         **植入异种胚芽手术**能不能看到、能不能选。</item>
    /// </list>
    ///
    /// ── 各环节的根因（反编译取证）──
    /// <list type="table">
    ///   <item>① <c>Dialog_CreateXenogerm.ColonyHasEnoughArchites()</c>：
    ///         <c>geneAssembler.MapHeld.listerThings.ThingsOfDef(ArchiteCapsule)</c> 累加 stackCount
    ///         ⇒ 胶囊只在核心 ⇒ 判"不够" ⇒ 点确认时弹 <c>NotEnoughArchites</c>、**不能开始组装**；</item>
    ///   <item>② <c>WorkGiver_CreateXenogerm.FindArchiteCapsule</c>（一行体）⇒ 装配器需要胶囊时
    ///         **没人去取**。且其 <c>JobOnThing</c> 找不到胶囊时**仍返回 CreateXenogerm 作业**（坑 28）；</item>
    ///   <item>③ <c>Recipe_ImplantXenogerm.AvailableOnNow</c>：<c>listerThings.ThingsOfDef(Xenogerm)</c>
    ///         ⇒ 手术**不可用**；<c>Dialog_SelectXenogerm</c> 构造函数同一查询 ⇒ 选择器里**列不出**胚芽。</item>
    /// </list>
    ///
    /// ── 做法（按环节分别选补法）──
    /// <list type="bullet">
    ///   <item>① **Postfix 翻结果**（纯 UI 判据，翻它最轻）：原版 false 时，若核心里的超凡胶囊数量
    ///         达到该次组装所需（<c>arc</c>）就翻 true（<c>arc</c> / <c>geneAssembler</c> 用字段反射读取）；</item>
    ///   <item>② **模板甲 + 乙**（同既有做法）：HasJobOnThing 翻可用性、JobOnThing 取胶囊造搬运作业；</item>
    ///   <item>③ **预填（补法 1）**：不去复制 <c>Recipe_Surgery.AvailableOnNow</c> 那一长串判据
    ///         （性别/生育/年龄/发育阶段/人形/mutant 黑名单…），而是
    ///         **先把一件异种胚芽从核心取到目标小人脚下** ⇒ 原版自己的判定与选择器**自然全部通过**，
    ///         而且 <c>bill.xenogerm</c> 指向的是**真实在地图上的**物品，后续搬运/植入才走得通。</item>
    /// </list>
    ///
    /// <para>⚠️ ③ 的预填放在 **`AvailableOnNow` 的 Prefix** 与 **`Dialog_SelectXenogerm` 构造函数的 Prefix**
    /// 两处，并用"**地图上已有可用胚芽就不动手**"做闸门 ⇒ 每次"需要胚芽"只会落一件，不会成堆。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_XenogermPipelineFromCore
    {
        internal static int ArchiteDialogFixCount;   // ①
        internal static int HasJobFixCount;          // ②
        internal static int JobFixCount;             // ②
        internal static int ImplantPrefillCount;     // ③

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.xenogermFromCore;
        }

        // ══════════════════════ ① 组装界面：超凡胶囊够不够 ① ══════════════════════

        private static FieldInfo assemblerField;
        private static FieldInfo arcField;

        [HarmonyPatch(typeof(Dialog_CreateXenogerm), "ColonyHasEnoughArchites")]
        internal static class Patch_ColonyHasEnoughArchites
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Dialog_CreateXenogerm __instance, ref bool __result)
            {
                if (__result || __instance == null || !Enabled())
                {
                    return;
                }

                try
                {
                    if (arcField == null)
                    {
                        // AccessTools.Field 会沿基类查找（arc 声明在 GeneCreationDialogBase）。
                        arcField = AccessTools.Field(typeof(Dialog_CreateXenogerm), "arc");
                        assemblerField = AccessTools.Field(typeof(Dialog_CreateXenogerm), "geneAssembler");
                    }

                    int arc = arcField != null ? (int)arcField.GetValue(__instance) : 0;
                    Building_GeneAssembler assembler = assemblerField != null
                        ? assemblerField.GetValue(__instance) as Building_GeneAssembler
                        : null;

                    // arc <= 0 时原版本来就返回 true，不会走到这里。
                    if (arc <= 0 || assembler == null)
                    {
                        return;
                    }

                    Map map = assembler.MapHeld;
                    if (map == null)
                    {
                        return;
                    }

                    if (CoreContentAccess.CountMatching(map, t => t.def == ThingDefOf.ArchiteCapsule, arc) >= arc)
                    {
                        __result = true;
                        ArchiteDialogFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][异种胚芽] 超凡胶囊在数字核心里 ⇒ 组装界面判定「胶囊足够」。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心超凡胶囊（组装界面）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510360);
                }
            }
        }

        // ══════════════════════ ② 搬运：把超凡胶囊运进装配器 ══════════════════════

        /// <summary>复刻原版 FindArchiteCapsule 的那次查找（只读）：返回 null 才该我们顶上。</summary>
        private static bool HasUsableCapsuleOnMap(Pawn pawn, Map map)
        {
            return GenClosest.ClosestThingReachable(
                       pawn.Position, map, ThingRequest.ForDef(ThingDefOf.ArchiteCapsule),
                       PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
                       x => x != null && !x.IsForbidden(pawn) && pawn.CanReserve(x)) != null;
        }

        [HarmonyPatch(typeof(WorkGiver_CreateXenogerm), "HasJobOnThing")]
        internal static class Patch_HasJobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
            {
                if (__result || pawn == null || !Enabled() || !(t is Building_GeneAssembler ga))
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || ga.ArchitesRequiredNow <= 0)
                {
                    return;   // 不需要胶囊 ⇒ 原版拒的是别的原因，不翻
                }

                if (!pawn.CanReserve(t, 1, -1, null, forced) || !pawn.CanReserveSittableOrSpot(t.InteractionCell, forced))
                {
                    return;
                }

                if (HasUsableCapsuleOnMap(pawn, map))
                {
                    return;
                }

                try
                {
                    if (CoreContentAccess.CountMatching(map, x => x.def == ThingDefOf.ArchiteCapsule, 1) > 0)
                    {
                        __result = true;
                        HasJobFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][异种胚芽] 地图上没有超凡胶囊，核心里有 ⇒ 该工作可用。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心超凡胶囊时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510350);
                }
            }
        }

        [HarmonyPatch(typeof(WorkGiver_CreateXenogerm), "JobOnThing")]
        internal static class Patch_JobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
            {
                if (pawn == null || !Enabled() || !(t is Building_GeneAssembler ga))
                {
                    return;
                }

                if (ga.ArchitesRequiredNow <= 0)
                {
                    return;
                }

                // ★ 原版找到胶囊时给的就是 HaulToContainer；否则是 CreateXenogerm（坑 28）。
                if (__result != null && __result.def == JobDefOf.HaulToContainer)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                try
                {
                    Thing capsule = CoreContentAccess.ExtractOne(
                        pawn, ThingDefOf.ArchiteCapsule, ga.ArchitesRequiredNow, map);
                    if (capsule == null)
                    {
                        return;
                    }

                    Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer, capsule, t);
                    job.count = Mathf.Min(ga.ArchitesRequiredNow, capsule.stackCount);
                    __result = job;
                    JobFixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][异种胚芽] 地图上没有超凡胶囊，已从数字核心取出 "
                                    + job.count + " 个到脚下 @ " + capsule.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取超凡胶囊时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510351);
                }
            }
        }

        // ══════════════════════ ③ 植入：先让胚芽出现在地上 ══════════════════════

        /// <summary>地图上有没有"原版用得上"的异种胚芽（可见、未雾）。</summary>
        private static bool HasUsableXenogermOnMap(Map map)
        {
            List<Thing> onMap = map?.listerThings?.ThingsOfDef(ThingDefOf.Xenogerm);
            if (onMap == null)
            {
                return false;
            }
            for (int i = 0; i < onMap.Count; i++)
            {
                Thing x = onMap[i];
                if (x != null && !x.Destroyed && !x.PositionHeld.Fogged(map))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 必要时把核心里的异种胚芽取一件到 <paramref name="pawn"/> 脚下。
        /// 这样原版的手术可用性判定与选择器都能靠自己的逻辑通过（见类注释 ③）。
        /// </summary>
        private static void EnsureXenogermOnGround(Pawn pawn, Map map)
        {
            if (pawn == null || map == null || !Enabled() || !ModsConfig.BiotechActive)
            {
                return;
            }
            if (HasUsableXenogermOnMap(map))
            {
                return;   // 地图上已有 ⇒ 原版自己能过
            }

            try
            {
                Thing taken = CoreContentAccess.ExtractOne(pawn, ThingDefOf.Xenogerm, 1, map);
                if (taken == null)
                {
                    return;
                }

                ImplantPrefillCount++;

                DsPatchSettings s = DsPatchMod.Settings;
                if (s != null && s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][异种胚芽] 地图上没有异种胚芽，已从数字核心取出 1 件到脚下 @ "
                                + taken.Position);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 从数字核心取异种胚芽时出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510361);
            }
        }

        [HarmonyPatch(typeof(Recipe_ImplantXenogerm), "AvailableOnNow")]
        internal static class Patch_ImplantAvailableOnNow
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Thing thing, BodyPartRecord part)
            {
                if (!(thing is Pawn pawn) || !pawn.Spawned)
                {
                    return;
                }
                EnsureXenogermOnGround(pawn, pawn.Map);
            }
        }

        // ⚠️ 曾考虑同时挂「选择器」<c>Dialog_SelectXenogerm</c> 的构造函数，**已放弃**：
        //   植入手术的可用性检查（本文件上面的 <c>AvailableOnNow</c>）会在玩家**打开手术列表时**先跑，
        //   那时胚芽已经落地 ⇒ 选择器靠自己的 <c>listerThings</c> 就能列出它，**无需再挂**。
        //   而构造函数挂点一旦签名不匹配，会让 Harmony 的 **PatchAll 整体失败（全部补丁失效）**，
        //   风险远大于收益。若日后实测发现选择器确实列不出，再用
        //   `PatchHarmonyInit` 里那种"手动 Patch + try/catch"的方式单独挂上。
    }
}
