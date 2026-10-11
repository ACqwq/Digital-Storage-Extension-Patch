using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让投影核心被原 mod「找本图核心」的两条主路径看见。
    ///
    /// 背景：4.0 里这两处都按 <c>is Building_StorageCore</c> 过滤本图搬运源，
    /// 而投影核心是补丁自有的 <see cref="Building_CoreProjector"/> ⇒ 会被过滤掉，
    /// 导致"这张图只有投影核心"时数字工人取不到料、也交不了货。
    ///
    /// 挂点（都是 public static，且返回的是新集合或共享缓冲——见各自处理）：
    ///   - <c>CoreFinder.AllUsableCores(Map)</c>     —— 每次新建 List，可直接追加
    ///   - <c>HaulSourceContents.CoreSources(Map)</c> —— 返回 mod 的**共享静态缓冲**，
    ///     绝不能就地修改（会污染其它调用方）⇒ 复制一份再替换 __result
    ///
    /// 两个挂点各自独立 try/catch：任一目标消失只记日志，不影响另一个与其它功能。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_ProjectedCoreVisibility
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.projectedcore";

        static Patch_ProjectedCoreVisibility()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 投影核心可见性补丁安装失败（投影仍可存取，"
                          + "但数字工人等可能仍看不到它）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Install()
        {
            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;

            MethodInfo allCores = AccessTools.Method(
                typeof(CoreFinder), "AllUsableCores", new Type[] { typeof(Map) });
            if (allCores == null)
            {
                Log.Error("[数字存储补丁] 找不到 CoreFinder.AllUsableCores(Map)，投影核心对数字工人不可见。");
            }
            else
            {
                harmony.Patch(allCores, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_ProjectedCoreVisibility), nameof(Postfix_Cores))));
                ok++;
            }

            MethodInfo coreSources = AccessTools.Method(
                typeof(HaulSourceContents), "CoreSources", new Type[] { typeof(Map) });
            if (coreSources == null)
            {
                Log.Error("[数字存储补丁] 找不到 HaulSourceContents.CoreSources(Map)，"
                          + "数字工人将无法从投影核心取料。");
            }
            else
            {
                harmony.Patch(coreSources, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_ProjectedCoreVisibility), nameof(Postfix_Sources))));
                ok++;
            }

            // 明确把结果写进日志：这条是"数字工人能否看见投影核心"的第一诊断依据。
            if (ok == 2)
            {
                Log.Message("[数字存储补丁] 投影核心可见性已挂载：AllUsableCores(Map)、CoreSources(Map)。");
            }
            else
            {
                Log.Warning("[数字存储补丁] 投影核心可见性只挂上 " + ok + "/2 —— "
                            + "数字工人可能仍看不到投影核心。");
            }
        }

        /// <summary>AllUsableCores(Map) 返回新建 List，直接追加即可。</summary>
        private static void Postfix_Cores(Map map, ref List<Building_StorageCore> __result)
        {
            if (__result == null)
            {
                return;
            }
            ProjectionHelper.AppendProjectedCores(map, __result);
        }

        /// <summary>
        /// CoreSources(Map) 返回的是 mod 的共享静态缓冲 <c>tmpCoreSources</c>，
        /// 就地 Add 会污染其它调用方 ⇒ 复制成新列表后替换返回值。
        /// </summary>
        private static void Postfix_Sources(Map map, ref List<IHaulSource> __result)
        {
            if (__result == null)
            {
                return;
            }
            List<Building_StorageCore> extra = new List<Building_StorageCore>();
            ProjectionHelper.AppendProjectedCores(map, extra);
            if (extra.Count == 0)
            {
                return;
            }
            List<IHaulSource> combined = new List<IHaulSource>(__result.Count + extra.Count);
            combined.AddRange(__result);
            for (int i = 0; i < extra.Count; i++)
            {
                if (!combined.Contains(extra[i]))
                {
                    combined.Add(extra[i]);
                }
            }
            __result = combined;
        }
    }
}
