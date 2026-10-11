using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「碎片（Shard）」相关的判定与取用都能认数字核心（v0.51，G1' + N3）。
    ///
    /// ── 涉及的两个功能 ──
    /// <list type="bullet">
    ///   <item>**黄金立方体停用**（`CompGoldenCube`）—— 需要 1 个碎片；</item>
    ///   <item>**方尖碑停用**（`CompObeliskDeactivationInteractor`）—— 需要 N 个碎片（检查走本文件的 ①）。</item>
    /// </list>
    ///
    /// ── 三个挂点（★ 按"检查 / 执行"分开）──
    /// <list type="number">
    ///   <item>**① 纯检查** `<c>ReservationUtility.ExistsUnreservedAmountOfDef</c>`（**只读计数**，无 out、无副作用）
    ///         ⇒ Postfix **只翻结果、绝不取货**。一处覆盖**黄金立方体 + 方尖碑**的可用性检查；</item>
    ///   <item>**②a 纯检查** `<c>ReservationUtility.CanReserveAndReachableOfDef</c>`（**全库只有 `CompGoldenCube.CanInteract` 一个调用方**）
    ///         ⇒ 同上，只翻 bool；</item>
    ///   <item>**②b 执行** `<c>CompGoldenCube.OrderDeactivation</c>`（玩家**确认停用之后**才走）
    ///         ⇒ Prefix 里把 1 个碎片从核心取到小人脚下，随后原版自己的查找自然命中。</item>
    /// </list>
    ///
    /// <para>★ **为什么不挂 <c>TryFindReserveAndReachableOfDef</c>**：它被"检查"（经上面 ②a 的包装）
    /// 与"执行"（②b 内部）**两用** ⇒ 在只读检查阶段取货 = **踩坑 29**（改衣物颜色那个坑）。
    /// 所以这里**分类处理**：检查侧只翻结果，执行侧才取货。</para>
    ///
    /// <para>⚠️ **核心侧不跑 <c>validator</c>**：原版对地图上的物品还会跑一层可达性校验
    /// （如 `activateBy.CanReserveAndReach(...)`），对"还在核心里"的物品无法校验 ⇒ **口径放宽**。
    /// 后果是可能"选项可见、执行时才发现拿不到"（**安全失败**，不会出错物）。</para>
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_ShardFromCore
    {
        internal static int ExistsFixCount;     // ①
        internal static int CanReserveFixCount; // ②a
        internal static int ExtractCount;       // ②b

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.shardFromCore;
        }

        /// <summary>核心里有没有该 def（只读）。</summary>
        private static bool CoreHas(Map map, ThingDef def, int amount)
        {
            return map != null && def != null
                   && CoreContentAccess.CountMatching(map, t => t.def == def, amount) >= amount;
        }

        /// <summary>复刻 `<c>TryFindReserveAndReachableOfDef</c>` 的那次查找（只读）：原版找得到就不干预。</summary>
        private static bool HasUsableShardOnMap(Pawn pawn)
        {
            Map map = pawn.MapHeld;
            if (map == null)
            {
                return false;
            }
            return GenClosest.ClosestThingReachable(
                       pawn.PositionHeld, map, ThingRequest.ForDef(ThingDefOf.Shard),
                       PathEndMode.OnCell, TraverseParms.For(pawn), 9999f,
                       t => t != null && !t.IsForbidden(pawn)
                            && pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.None)) != null;
        }

        // ══════════════════════ ① 纯检查：够不够（黄金立方体 + 方尖碑） ══════════════════════

        [HarmonyPatch(typeof(ReservationUtility), "ExistsUnreservedAmountOfDef")]
        internal static class Patch_ExistsUnreservedAmountOfDef
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Map map, ThingDef thingDef, Faction faction, int amount, ref bool __result)
            {
                if (__result || !Enabled() || amount <= 0)
                {
                    return;
                }

                try
                {
                    // 与地图侧同口径：只要"未被该阵营禁止"的就算（validator 是对地图物品的可达性校验，核心侧跑不了）。
                    int found = CoreContentAccess.CountMatching(
                        map, t => t.def == thingDef && !t.IsForbidden(faction), amount);

                    if (found >= amount)
                    {
                        __result = true;
                        ExistsFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][碎片] 地图上不够，核心里有 ⇒ 判定为「可用」（" + thingDef.label + "）。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心碎片（ExistsUnreservedAmountOfDef）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510390);
                }
            }
        }

        // ══════════════════════ ②a 纯检查：黄金立方体「能否停用」 ══════════════════════

        [HarmonyPatch(typeof(ReservationUtility), "CanReserveAndReachableOfDef")]
        internal static class Patch_CanReserveAndReachableOfDef
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn p, ThingDef thingDef, ref bool __result)
            {
                if (__result || !Enabled() || p == null || thingDef == null)
                {
                    return;
                }

                try
                {
                    if (CoreHas(p.MapHeld, thingDef, 1))
                    {
                        __result = true;
                        CanReserveFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][碎片] 地图上没有可用的，核心里有 ⇒ 判定为「可停用」。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 检查数字核心碎片（CanReserveAndReachableOfDef）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510391);
                }
            }
        }

        // ══════════════════════ ②b 执行：黄金立方体停用下单前，把碎片取到脚下 ══════════════════════

        [HarmonyPatch(typeof(CompGoldenCube), "OrderDeactivation")]
        internal static class Patch_OrderDeactivation
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Pawn pawn)
            {
                // 走到这里说明玩家**已经确认**要停用（不是只读检查）⇒ 取货安全。
                if (pawn == null || !Enabled() || HasUsableShardOnMap(pawn))
                {
                    return;
                }

                Map map = pawn.MapHeld;
                if (map == null)
                {
                    return;
                }

                try
                {
                    Thing taken = CoreContentAccess.ExtractOne(pawn, ThingDefOf.Shard, 1, map);
                    if (taken == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版（它会拿不到碎片、什么也不做）
                    }

                    ExtractCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][黄金立方体] 地图上没有碎片，已从数字核心取出 1 个到脚下 @ "
                                    + taken.Position);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取碎片（黄金立方体）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510392);
                }
            }
        }
    }
}
