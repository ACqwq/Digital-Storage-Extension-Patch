using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让制作代理的**产物投递**与它的**取料**用同一套"哪个核心可用"的规则。
    /// </summary>
    /// <remarks>
    /// <para>问题：产物投递走 <c>BillCraftFunnel.StoreProducts</c> →
    /// <c>DigitalDropRedirect.NearestUsableCore(map, bench位置)</c> →
    /// <c>CoreFinder.AllUsableCores(map)</c> → <c>HaulSourceContents.EnabledSources(map)</c>，
    /// 也就是**只看本图搬运源**。于是出现"取料走通了、产物却回不去"的半通状态。</para>
    ///
    /// <para>更糟的是 <c>FindNearestUsableCore</c> 用
    /// <c>(core.Position - near).LengthHorizontalSquared</c> 排序 ——
    /// 一旦列表里混进**其它地图**的核心（我们的可见性补丁会追加被投影的核心），
    /// 这个跨图距离是**无意义的数字**，可能压过本图那个真正近的核心。</para>
    ///
    /// <para>所以这里改成与取料一致的优先级：<b>本图可用核心 → 本图投影核心所绑定的核心 →
    /// （可选）任意可用核心</b>。</para>
    ///
    /// <para><b>作用面很窄</b>：原 mod 里 <c>NearestUsableCore(Map, IntVec3)</c>
    /// <b>只有 <c>StoreProducts</c> 一处调用</b>（已全量检索确认）。
    /// 数字工人走的是 <c>DigitalDropRedirect.Begin()</c>、普通掉落走 <c>Patch_GenPlace_DropRedirect</c>，
    /// 都不经过本方法，所以改它不会波及它们。</para>
    /// </remarks>
    internal static class Patch_BillProductDelivery
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.billproductdelivery";

        private static readonly List<Building_StorageCore> tmpProjected = new List<Building_StorageCore>();
        private static readonly List<Building_StorageCore> tmpRemote = new List<Building_StorageCore>();

        public static void Install()
        {
            Type type = AccessTools.TypeByName("DigitalStorage.Core.DigitalDropRedirect");
            if (type == null)
            {
                Log.Warning("[数字存储补丁] 找不到 DigitalStorage.Core.DigitalDropRedirect，"
                            + "制作代理的产物投递不会被修正（产物仍可能落在工作台旁）。");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;

            MethodInfo nearest = AccessTools.Method(type, "NearestUsableCore",
                new[] { typeof(Map), typeof(IntVec3) });
            if (nearest == null)
            {
                Log.Warning("[数字存储补丁] 找不到 DigitalDropRedirect.NearestUsableCore(Map, IntVec3)，"
                            + "制作代理的产物投递不会被修正。");
            }
            else
            {
                harmony.Patch(nearest, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_BillProductDelivery), nameof(Postfix_Nearest))));
                ok++;
            }

            MethodInfo ingest = AccessTools.Method(type, "TryIngestUnspawned",
                new[] { typeof(Building_StorageCore), typeof(Thing) });
            if (ingest == null)
            {
                Log.Warning("[数字存储补丁] 找不到 DigitalDropRedirect.TryIngestUnspawned，"
                            + "产物投递失败时不会有诊断输出（不影响功能）。");
            }
            else
            {
                harmony.Patch(ingest, postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(Patch_BillProductDelivery), nameof(Postfix_Ingest))));
                ok++;
            }

            if (ok > 0)
            {
                Log.Message("[数字存储补丁] 制作代理产物投递已修正：" + ok + "/2 处。"
                            + "规则与取料一致：本图核心 → 本图投影核心绑定的核心 → （可选）任意可用核心。");
            }
        }

        /// <summary>重选产物要进的核心。</summary>
        private static void Postfix_Nearest(Map map, IntVec3 near, ref Building_StorageCore __result)
        {
            if (map == null)
            {
                return;
            }

            // ① 本图可用核心（原 mod 的本来口径；顺带修掉"跨图核心挤掉本图近核心"的排序问题）
            List<Building_StorageCore> allHere = CoreFinder.AllUsableCores(map);
            int localCount = 0;
            for (int i = 0; i < allHere.Count; i++)
            {
                Building_StorageCore c = allHere[i];
                if (c != null && ((Thing)c).Map == map && CoreFinder.IsUsable(c))
                {
                    localCount++;
                }
            }

            // 诊断（不依赖设置开关，按图节流）：这一行能直接区分
            // "本图没核心" / "本图有核心但没被选中" / "投影核心没被算进来"。
            tmpProjected.Clear();
            ProjectionHelper.AppendProjectedCores(map, tmpProjected);
            Diag(map, localCount, tmpProjected.Count);

            Building_StorageCore local = NearestCoreOn(allHere, map, near);
            if (local != null)
            {
                if (!ReferenceEquals(__result, local) && __result != null)
                {
                    Diagnose("[数字存储补丁] 制作代理产物投递：原选中的核心在另一张图，已改回本图核心。");
                }
                __result = local;
                return;
            }

            // ② 本图投影核心所绑定的核心 —— 与取料的默认口径一致
            for (int i = 0; i < tmpProjected.Count; i++)
            {
                Building_StorageCore c = tmpProjected[i];
                if (c != null && CoreFinder.IsUsable(c))
                {
                    if (!ReferenceEquals(__result, c))
                    {
                        Diagnose("[数字存储补丁] 制作代理产物投递：本图没有可用核心，"
                                 + "改用投影核心绑定的那个核心。");
                    }
                    __result = c;
                    tmpProjected.Clear();
                    return;
                }
            }
            tmpProjected.Clear();

            // ③ 可选：跟随原 mod 的跨图口径，任意可用核心都能收产物
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.billCrossMapAnyCore)
            {
                return;
            }
            tmpRemote.Clear();
            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    Map m = maps[i];
                    if (m == null || m == map)
                    {
                        continue;
                    }
                    List<Building_StorageCore> cores = CoreFinder.AllUsableCores(m);
                    for (int j = 0; j < cores.Count; j++)
                    {
                        Building_StorageCore c = cores[j];
                        if (c != null && c.Map == m && CoreFinder.IsUsable(c) && !tmpRemote.Contains(c))
                        {
                            tmpRemote.Add(c);
                        }
                    }
                }
            }
            if (tmpRemote.Count > 0)
            {
                __result = tmpRemote[0];
            }
            tmpRemote.Clear();
        }

        /// <summary>只在给定的候选列表里，按距离选最近的可用的核心；没有则返回 null。</summary>
        private static Building_StorageCore NearestCoreOn(
            List<Building_StorageCore> cores, Map want, IntVec3 near)
        {
            Building_StorageCore best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore c = cores[i];
                if (c == null || ((Thing)c).Map != want || !CoreFinder.IsUsable(c))
                {
                    continue;
                }
                IntVec3 d = c.Position - near;
                float dist = d.LengthHorizontalSquared;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }
            return best;
        }

        private static readonly Dictionary<int, int> lastDiagTick = new Dictionary<int, int>();

        /// <summary>
        /// 产物投递的选核诊断（**不看设置开关**，按图每 600 tick 最多一条）。
        /// 这一条同时给出本图核心数、本图"可用的投影核心"数、以及该图投影核心总数 ——
        /// 足以区分"本图没核心""有核心但不可用""投影核心没被算进来"三种情况。
        /// </summary>
        /// <remarks>
        /// 产物投递已实测通过（四种组合全部符合预期），所以这条**只在开启诊断时才打印** ——
        /// 默认开着会每 600 tick 带一段堆栈刷日志。
        /// 真正需要长期保留的是 <see cref="Postfix_Ingest"/> 的失败告警（有界，只在真失败时出现）。
        /// </remarks>
        private static void Diag(Map map, int localCount, int projectedCount)
        {
            if (map == null)
            {
                return;
            }
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.diagnosticsEnabled)
            {
                return;
            }

            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            lastDiagTick.TryGetValue(map.uniqueID, out int last);
            if (tick - last < 600)
            {
                return;
            }
            lastDiagTick[map.uniqueID] = tick;

            Log.Message("[数字存储补丁] 产物投递选核｜本图可用核心 " + localCount
                        + " ｜本图可用投影核心 " + projectedCount
                        + "（该图上投影核心总数 " + ProjectionRegistry.CountOn(map) + "）"
                        + " ｜设置=" + (s.billCrossMapAnyCore ? "任意可用核心" : "需投影核心"));
        }

        /// <summary>产物没进成核心时，把原因一次性写进日志（这是定位此类问题最关键的一条数据）。</summary>
        private static void Postfix_Ingest(Building_StorageCore target, Thing t, bool __result)
        {
            if (__result || target == null || t == null)
            {
                return;
            }

            string coreMap = ((Thing)target).Map != null ? ((Thing)target).Map.ToString() : "null";
            bool accepts;
            try
            {
                accepts = target.Accepts(t);
            }
            catch (Exception ex)
            {
                accepts = false;
                Log.WarningOnce("[数字存储补丁] 制作代理产物投递：判定 Accepts 时异常："
                                + ex.GetType().Name + ": " + ex.Message, 472003);
            }

            ThingOwner held = target.GetDirectlyHeldThings();
            int count = held != null ? held.Count : -1;
            int max = target.maxStacks;
            Map cur = Find.CurrentMap;
            bool crossMap = ((Thing)target).Map != null && ((Thing)target).Map != cur;

            Log.WarningOnce("[数字存储补丁] 制作代理产物未进核心：" + (t.def != null ? t.def.defName : "?")
                            + " → 核心@" + coreMap + (crossMap ? "（跨图）" : "（本图）")
                            + " ｜ Accepts=" + accepts
                            + " ｜ 容器 " + count + "/" + max
                            + " ｜ 核心可用=" + target.IsUsableNow, 472001);
        }

        private static bool diagLogged;

        private static void Diagnose(string text)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.diagnosticsEnabled || diagLogged)
            {
                return;
            }
            diagLogged = true;
            Log.Message(text);
        }
    }
}
