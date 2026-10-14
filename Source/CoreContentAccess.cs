using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 共享工具：访问数字核心的内容物（供"仪式祭品"相关补丁使用）。
    ///
    /// <para>原 mod 的 <c>DigitalStorage.AI.CoreFinder</c> 是 <c>internal</c>，
    /// 补丁不能编译期引用 ⇒ 这里用反射调用它的 <c>AllUsableCores(Map)</c> 并缓存 MethodInfo，
    /// 以保持与原 mod 一致的"可用核心"过滤（通电/已研究等），避免从断电核心取料。
    /// 反射失败时退回遍历本图玩家核心建筑。</para>
    /// </summary>
    internal static class CoreContentAccess
    {
        private static bool coresResolved;
        private static MethodInfo allUsableCores;

        /// <summary>本图可用核心列表（语义同原 mod 的 CoreFinder.AllUsableCores）。</summary>
        internal static List<Building_StorageCore> UsableCores(Map map)
        {
            if (map == null)
            {
                return null;
            }

            if (!coresResolved)
            {
                coresResolved = true;
                try
                {
                    Type finder = AccessTools.TypeByName("DigitalStorage.AI.CoreFinder");
                    allUsableCores = finder == null
                        ? null
                        : AccessTools.Method(finder, "AllUsableCores", new[] { typeof(Map) });
                }
                catch
                {
                    allUsableCores = null;
                }

                if (allUsableCores == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 CoreFinder.AllUsableCores"
                                + "（原 mod 可能已改结构），核心内容物访问将退回遍历本图核心建筑。");
                }
            }

            if (allUsableCores != null)
            {
                try
                {
                    if (allUsableCores.Invoke(null, new object[] { map }) is List<Building_StorageCore> list)
                    {
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[数字存储补丁] 调用 AllUsableCores 失败，改用本图遍历：" + ex.Message, 8830151);
                }
            }

            return FallbackCores(map);
        }

        // ────────────────────────────────────────────────────────────────
        // 跨版本取物桥（v0.62，上游 2026-10 对齐）：
        // 原 mod 把 HaulSourceContents.ExtractTo 的签名从
        //   (Thing, int, IntVec3, Map)                                        ← 10 月前
        //   (Thing, int, IntVec3, Map, out int extracted, bool forbid = false) ← 10 月起
        // 改成了带 out 的新签名（旧 4 参重载**不存在**）⇒ 编译期直调的一方遇到另一版本
        // 会在 JIT 时报 MissingMethodException。这里统一走反射桥：按签名探测、优先新版，
        // **一份构建同时兼容新旧上游**（调用频率低，反射开销可忽略）。
        // ────────────────────────────────────────────────────────────────
        private static readonly Type[] ExtractToNewSig =
        {
            typeof(Thing), typeof(int), typeof(IntVec3), typeof(Map),
            typeof(int).MakeByRefType(), typeof(bool),
        };

        private static readonly Type[] ExtractToOldSig =
        {
            typeof(Thing), typeof(int), typeof(IntVec3), typeof(Map),
        };

        private static MethodInfo extractToNew;
        private static MethodInfo extractToOld;
        private static bool extractToResolved;

        /// <summary>经跨版本桥调 <c>HaulSourceContents.ExtractTo</c>（新旧签名通吃）。</summary>
        internal static Thing ExtractFromCoreCrossVersion(Thing t, int count, IntVec3 pos, Map map)
        {
            if (!extractToResolved)
            {
                extractToResolved = true;
                try
                {
                    extractToNew = AccessTools.Method(typeof(HaulSourceContents), "ExtractTo", ExtractToNewSig);
                    extractToOld = AccessTools.Method(typeof(HaulSourceContents), "ExtractTo", ExtractToOldSig);
                }
                catch
                {
                    extractToNew = null;
                    extractToOld = null;
                }
                if (extractToNew == null && extractToOld == null)
                {
                    Log.ErrorOnce("[数字存储补丁] 找不到 HaulSourceContents.ExtractTo"
                                  + "（新旧签名都不匹配），从核心取货不可用。", 779001);
                }
            }

            try
            {
                if (extractToNew != null)
                {
                    // out 放在 args[4]（值忽略）；forbid 默认 false，与旧行为一致。
                    object[] args = { t, count, pos, map, 0, false };
                    return (Thing)extractToNew.Invoke(null, args);
                }
                if (extractToOld != null)
                {
                    return (Thing)extractToOld.Invoke(null, new object[] { t, count, pos, map });
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 跨版本取货失败：" + ex.GetType().Name + ": " + ex.Message, 779002);
            }
            return null;
        }

        private static List<Building_StorageCore> FallbackCores(Map map)
        {
            List<Building_StorageCore> result = new List<Building_StorageCore>();
            List<Building> buildings = map.listerBuildings?.allBuildingsColonist;
            if (buildings != null)
            {
                for (int i = 0; i < buildings.Count; i++)
                {
                    if (buildings[i] is Building_StorageCore core && core.Spawned)
                    {
                        result.Add(core);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 统计核心内容物里符合 <paramref name="filter"/> 的数量（按 stackCount 累加，
        /// 上限 <paramref name="limit"/>；limit &lt;= 0 表示不限）。
        /// 若给出了 <paramref name="reserveCheckers"/>，则要求至少其中一个能预约该物品
        /// （与地图扫描的谓词口径一致，避免抢别人正在搬的东西）。
        /// </summary>
        internal static int CountMatching(Map map, ThingFilter filter, List<Pawn> reserveCheckers, float limit)
        {
            if (map == null || filter == null)
            {
                return 0;
            }

            List<Building_StorageCore> cores = UsableCores(map);
            if (cores == null)
            {
                return 0;
            }

            int total = 0;
            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.Spawned)
                {
                    continue;
                }

                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null)
                {
                    continue;
                }

                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t == null || t.Destroyed || t.stackCount <= 0 || !filter.Allows(t))
                    {
                        continue;
                    }

                    if (!CanAnyReserve(reserveCheckers, t, t.stackCount))
                    {
                        continue;
                    }

                    total += t.stackCount;
                    if (limit > 0f && total >= limit)
                    {
                        return (int)limit;
                    }
                }
            }

            return total;
        }

        /// <summary>在核心内容物里找第一件符合条件且可预约的物品（供实际取货用）。</summary>
        internal static Thing FindInCores(Map map, ThingFilter filter, List<Pawn> reserveCheckers, int needed)
        {
            if (map == null || filter == null)
            {
                return null;
            }

            List<Building_StorageCore> cores = UsableCores(map);
            if (cores == null)
            {
                return null;
            }

            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.Spawned)
                {
                    continue;
                }

                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null)
                {
                    continue;
                }

                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t == null || t.Destroyed || t.stackCount <= 0 || !filter.Allows(t))
                    {
                        continue;
                    }

                    int stack = Mathf.Min(Mathf.Max(1, needed), t.stackCount);
                    if (!CanAnyReserve(reserveCheckers, t, stack))
                    {
                        continue;
                    }

                    return t;
                }
            }

            return null;
        }

        /// <summary>
        /// 在核心内容物里找第一件满足 <paramref name="predicate"/>、可被检查者预约、
        /// 且 **stackCount ≥ <paramref name="minCount"/>** 的物品
        /// （供需要"镜像原版单堆足够语义"的取货方，如天灯节取木）。
        /// </summary>
        internal static Thing FindInCoresWhere(
            Map map, Func<Thing, bool> predicate, List<Pawn> reserveCheckers, int minCount)
        {
            if (map == null || predicate == null)
            {
                return null;
            }

            List<Building_StorageCore> cores = UsableCores(map);
            if (cores == null)
            {
                return null;
            }

            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.Spawned)
                {
                    continue;
                }

                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null)
                {
                    continue;
                }

                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (!IsUsable(t) || t.stackCount < minCount || !predicate(t))
                    {
                        continue;
                    }

                    if (!CanAnyReserve(reserveCheckers, t, minCount))
                    {
                        continue;
                    }

                    return t;
                }
            }

            return null;
        }

        private static bool CanAnyReserve(List<Pawn> checkers, Thing t, int count)
        {
            if (checkers == null || checkers.Count == 0)
            {
                return true;   // 无检查者时不做预约限制
            }

            for (int i = 0; i < checkers.Count; i++)
            {
                Pawn p = checkers[i];
                if (p == null || p.MapHeld == null)
                {
                    continue;
                }

                if (t.IsForbidden(p))
                {
                    continue;
                }

                if (p.CanReserve(t, 10, Mathf.Max(1, count)))
                {
                    return true;
                }
            }

            return false;
        }

        // ═══════════════ v0.45 · 通用取物 / 计数（供「物品可见性兜底（二）」复用） ═══════════════
        //
        // 下面几个方法把历史上被复制了三份的样板（遍历可用核心 + 叠加本图投影核心 +
        // 快照找 + 按需 ExtractTo）收敛到一处，供各"可见性兜底"补丁调用。
        // 口径与 Patch_InventoryStockFromCore / Patch_FindItemToGiveFromCore 完全一致：
        //   · 只认"可用核心"（CoreContentAccess.UsableCores）与本图投影核心绑定的核心；
        //   · **按需取** min(count, 这一堆)（绝不整堆 —— v0.44 的坑 27）；
        //   · 失败方向安全：找不到/取不出返回 null / 0，由调用方放行原版。

        private static readonly List<Building_StorageCore> tmpCores = new List<Building_StorageCore>();

        /// <summary>可用核心 + 本图投影核心绑定的核心（结果写入共享缓冲 tmpCores）。</summary>
        private static List<Building_StorageCore> GatherCores(Map map)
        {
            tmpCores.Clear();
            List<Building_StorageCore> mapCores = UsableCores(map);
            if (mapCores != null)
            {
                tmpCores.AddRange(mapCores);
            }
            try
            {
                ProjectionHelper.AppendProjectedCores(map, tmpCores);
            }
            catch (Exception)
            {
                // 投影读取失败不影响本图核心这条主路径。
            }
            return tmpCores;
        }

        private static ThingOwner HeldOwner(Building_StorageCore core)
        {
            if (core == null || !core.Spawned)
            {
                return null;
            }
            return core.GetDirectlyHeldThings();
        }

        private static bool IsUsable(Thing t)
        {
            return t != null && !t.Destroyed && t.stackCount > 0;
        }

        /// <summary>
        /// 统计核心内容物里满足 <paramref name="predicate"/> 的 **stackCount 合计**。
        /// <paramref name="limit"/> &gt; 0 时累计到达上限即提前返回（用于"够不够 N 个"）。
        /// 找不到核心或出错返回 0。
        /// </summary>
        internal static int CountMatching(Map map, Func<Thing, bool> predicate, int limit = 0)
        {
            if (map == null || predicate == null)
            {
                return 0;
            }

            int total = 0;
            try
            {
                List<Building_StorageCore> cores = GatherCores(map);
                for (int c = 0; c < cores.Count; c++)
                {
                    ThingOwner owner = HeldOwner(cores[c]);
                    if (owner == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < owner.Count; i++)
                    {
                        Thing t = owner[i];
                        if (!IsUsable(t) || !predicate(t))
                        {
                            continue;
                        }
                        total += t.stackCount;
                        if (limit > 0 && total >= limit)
                        {
                            return limit;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 统计数字核心内容物时出错（本次按 0 处理）："
                              + ex.GetType().Name + ": " + ex.Message, 66510301);
                return 0;
            }
            finally
            {
                tmpCores.Clear();
            }

            return total;
        }

        /// <summary>
        /// 「按负重上限」收紧取量：绝不一次取出超过 <paramref name="carrier"/> 能拿动的数量
        /// （否则多出来的会掉在地上、等自动收纳吸回，既浪费又难看）。
        /// 找不到 / 出错时按原量放行；已超重也至少给 1，保证功能不会因超重而彻底失效。
        /// </summary>
        private static int CapByCarry(Pawn carrier, Thing found, int want)
        {
            if (carrier == null || want <= 1)
            {
                return want;
            }
            try
            {
                int canHold = MassUtility.CountToPickUpUntilOverEncumbered(carrier, found);
                if (canHold < 1)
                {
                    canHold = 1;
                }
                if (canHold < want)
                {
                    return canHold;
                }
            }
            catch
            {
                // 负重计算异常 ⇒ 不收紧，按原量走。
            }
            return want;
        }

        /// <summary>
        /// 在可用核心（含本图投影核心绑定的）里找第一件满足 <paramref name="predicate"/> 的物品，
        /// **按 min(count, 这一堆) 取到 <paramref name="pawn"/> 脚下**并返回取出的那份；
        /// 找不到 / 取不出返回 null。取物复用原 mod 的 <c>HaulSourceContents.ExtractTo</c>
        /// （含 MarkWithdrawn，防被自动收纳立刻吸回）。取量会**按小人的负重上限收紧**。
        /// </summary>
        internal static Thing ExtractOne(Pawn pawn, Func<Thing, bool> predicate, int count, Map map)
        {
            if (pawn == null)
            {
                return null;
            }
            return ExtractOneAt(pawn, predicate, count, pawn.Position, map ?? pawn.MapHeld);
        }

        /// <summary>
        /// 与 <see cref="ExtractOneAt(Pawn, Func{Thing, bool}, int, IntVec3, Map)"/> 相同，但**不按负重收紧**
        /// —— 用于"东西落到指定格、不靠小人背"的场景（如「聚集区进食」把食物放到聚集点）。
        /// </summary>
        internal static Thing ExtractOneAt(Func<Thing, bool> predicate, int count, IntVec3 pos, Map map)
        {
            return ExtractCore(null, predicate, count, null, pos, map);
        }

        /// <summary>
        /// 与 <see cref="ExtractOne(Pawn, Func{Thing, bool}, int, Map)"/> 相同，但**落到指定格**
        /// （用于"必须落在某个范围内"的场景）；且按 <paramref name="carrier"/> 的负重上限收紧取量。
        /// </summary>
        internal static Thing ExtractOneAt(Pawn carrier, Func<Thing, bool> predicate, int count, IntVec3 pos, Map map)
        {
            return ExtractCore(carrier, predicate, count, null, pos, map);
        }

        /// <summary>取物内核：<paramref name="countFor"/> 非空时按"找到的那件"决定数量，否则用 <paramref name="count"/>。</summary>
        private static Thing ExtractCore(Pawn carrier, Func<Thing, bool> predicate, int count,
            Func<Thing, int> countFor, IntVec3 pos, Map map)
        {
            if (predicate == null || map == null || !pos.IsValid || (countFor == null && count <= 0))
            {
                return null;
            }

            try
            {
                List<Building_StorageCore> cores = GatherCores(map);
                for (int c = 0; c < cores.Count; c++)
                {
                    ThingOwner owner = HeldOwner(cores[c]);
                    if (owner == null)
                    {
                        continue;
                    }

                    // ★ 先抓快照再取：ExtractTo 会把物品从 owner 里移除，边遍历边改会抛
                    //   "Collection was modified"（踩坑目录同类陷阱）。
                    Thing found = null;
                    for (int i = 0; i < owner.Count; i++)
                    {
                        Thing t = owner[i];
                        if (IsUsable(t) && predicate(t))
                        {
                            found = t;
                            break;
                        }
                    }

                    if (found == null)
                    {
                        continue;
                    }

                    int need;
                    if (countFor != null)
                    {
                        try
                        {
                            need = Math.Max(1, countFor(found));
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    else
                    {
                        need = count;
                    }

                    int want = CapByCarry(carrier, found, Math.Min(need, found.stackCount));
                    if (want <= 0)
                    {
                        continue;
                    }

                    Thing taken = ExtractFromCoreCrossVersion(found, want, pos, map);
                    if (taken != null)
                    {
                        return taken;
                    }
                    // 取不出（落点放不下等）：ExtractTo 已把货放回核心，继续找下一个核心。
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 从数字核心取物时出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510303);
            }
            finally
            {
                tmpCores.Clear();
            }

            return null;
        }

        /// <summary>按 def 取物（便捷重载）。</summary>
        internal static Thing ExtractOne(Pawn pawn, ThingDef def, int count, Map map)
        {
            return def == null ? null : ExtractOne(pawn, t => t.def == def, count, map);
        }

        /// <summary>
        /// 取物（数量取决于找到的那件物品本身）——用于"要几个"由物品属性决定的场景
        /// （如生化舱按<b>营养值</b>算需要几份）。<paramref name="countFor"/> 返回想要的件数，
        /// 内部仍取 <c>min(countFor(找到的), 这一堆)</c>，且至少 1。
        /// </summary>
        internal static Thing ExtractOne(Pawn pawn, Func<Thing, bool> predicate, Func<Thing, int> countFor, Map map)
        {
            if (pawn == null)
            {
                return null;
            }
            return ExtractCore(pawn, predicate, 0, countFor, pawn.Position, map ?? pawn.MapHeld);
        }

        /// <summary>
        /// **只读**：在核心内容物里找第一件满足 <paramref name="predicate"/> 的物品并返回，
        /// **不从核心取出、不产生任何副作用**。用于"只需要知道有没有/拿个代表"的场景
        /// （如纪念碑材料菜单的可用性判断）。
        /// </summary>
        internal static Thing FindOne(Map map, Func<Thing, bool> predicate)
        {
            if (map == null || predicate == null)
            {
                return null;
            }

            try
            {
                List<Building_StorageCore> cores = GatherCores(map);
                for (int c = 0; c < cores.Count; c++)
                {
                    ThingOwner owner = HeldOwner(cores[c]);
                    if (owner == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < owner.Count; i++)
                    {
                        Thing t = owner[i];
                        if (IsUsable(t) && predicate(t))
                        {
                            return t;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 在数字核心里查找物品时出错（本次按未找到处理）："
                              + ex.GetType().Name + ": " + ex.Message, 66510304);
            }
            finally
            {
                tmpCores.Clear();
            }

            return null;
        }
    }
}
