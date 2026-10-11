using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// D. 存储容量自定义 —— 允许玩家按科技阶段自由改动数字核心的容量（单位：堆）。
    ///
    /// ── 原 mod 的容量链路（反编译核实） ──
    /// <code>
    /// // DigitalStorage.Core.CoreTier            （internal static = 原程序集内部类型）
    /// private static readonly int[] CapByLevel = { 500, 1000, 1500, 3000 };
    /// public static int Cap => { Ensure(); return CapByLevel[cachedLevel - 1]; }
    ///
    /// // DigitalStorage.Components.Building_StorageCore
    /// private int maxStacksField = 500;                                   // 存档字段，默认 500
    /// public  int maxStacks => Math.Max(maxStacksField, CoreTier.Cap);    // ★ 取较大值
    /// </code>
    ///
    /// ── 两条挂钩 ──
    /// ① **覆盖容量表**（无 Harmony）：反射拿到 <c>CapByLevel</c> 数组，直接改元素。
    ///    <c>Cap</c> 每次访问都现读数组 ⇒ 无需任何缓存失效；
    ///    <c>readonly</c> 只锁引用、不锁元素。
    /// ② **压低地板值**（Postfix <c>SpawnSetup</c>）：<c>maxStacks</c> 里那道
    ///    <c>Math.Max(maxStacksField(默认 500), Cap)</c> 会把低于 500 的容量顶回去。
    ///    由于 <c>maxStacksField</c> 只被这一处用到（已全库核实），把它设成
    ///    **当前容量表的最小值**即可让 <c>Math.Max</c> 恰好等于目标值：
    ///    <code>
    ///    maxStacksField = min(Caps)  ⇒  maxStacks = Max(min(Caps), Caps[Lv]) = Caps[Lv]   ← 恒等
    ///    </code>
    ///    关闭开关时 <c>Caps</c> 就是原值，<c>min = 500</c>，行为与不打补丁完全一致。
    ///
    /// ── 为什么不用 "Postfix get_maxStacks" ──
    /// 那是属性 getter，方法体极小。RimWorld 跑在 Mono 上、有 JIT 内联，
    /// 一旦被内联进调用点（<c>Accepts</c>），Harmony 在原生入口上打的补丁对**那些内联副本无效**，
    /// 结果就是"设置改了但容量没变"，而且不报任何错。改用「压地板」这条路径后，
    /// 我们只动数据、不改判定，原 mod 自己的 <c>Math.Max</c> 自动得出正确结果，没有内联风险。
    ///
    /// ── 生效时机 ──
    /// · 启动时 / 设置界面里每帧（<see cref="ApplyFromSettings"/>，内部有"值没变就跳过"判断）；
    /// · 新核心生成时（<c>SpawnSetup</c> Postfix）；
    /// · 已存在的核心：容量表一变就地更新一遍（只遍历本图搬运源，核心数量很少，且只在改设置时发生）。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_CustomCapacity
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.customcapacity";

        /// <summary>原 mod 的类型全名（internal，只能按名字反射）。</summary>
        private const string CoreTierTypeName = "DigitalStorage.Core.CoreTier";

        /// <summary>容量表的私有字段名。</summary>
        private const string CapByLevelName = "CapByLevel";

        /// <summary>核心"地板值"的私有字段名。</summary>
        private const string MaxStacksFieldName = "maxStacksField";

        /// <summary>当前生效的四档容量（下标 0 == Lv1）。关掉开关时 == 原值。</summary>
        private static readonly int[] Caps = new int[4] { 500, 1000, 1500, 3000 };

        /// <summary>启动时快照的原始容量表，用于"关闭开关即还原"。</summary>
        private static readonly int[] originals = new int[4] { 500, 1000, 1500, 3000 };

        private static FieldInfo capByLevelField;

        private static FieldInfo maxStacksFieldField;

        /// <summary>容量表已就绪（没就绪时整个功能不可用）。</summary>
        private static bool tableReady;

        /// <summary>地板值通道已就绪（没就绪时低于 500 的容量会被原 mod 顶回 500）。</summary>
        private static bool floorReady;

        /// <summary>当前应该写在 <c>maxStacksField</c> 上的值 = 当前容量表的最小值。</summary>
        private static int currentFloor = 500;

        static Patch_CustomCapacity()
        {
            try
            {
                Type coreTier = AccessTools.TypeByName(CoreTierTypeName);
                if (coreTier == null)
                {
                    Log.Error("[数字存储补丁] 找不到 " + CoreTierTypeName
                              + "，「存储容量自定义」不可用（原 mod 可能已改结构）。");
                    return;
                }

                capByLevelField = AccessTools.Field(coreTier, CapByLevelName);
                if (capByLevelField == null)
                {
                    Log.Error("[数字存储补丁] 找不到 CoreTier." + CapByLevelName
                              + "，「存储容量自定义」不可用（原 mod 可能已改结构）。");
                    return;
                }
                tableReady = true;

                // 快照原值：优先读真实的数组，读不到就用硬编码出厂值。
                int[] live = capByLevelField.GetValue(null) as int[];
                if (live != null && live.Length >= Caps.Length)
                {
                    for (int i = 0; i < Caps.Length; i++)
                    {
                        originals[i] = live[i];
                        Caps[i] = live[i];
                    }
                }
                currentFloor = MinOf(Caps);
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 存储容量自定义初始化失败（容量表部分）："
                          + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            try
            {
                maxStacksFieldField = AccessTools.Field(typeof(Building_StorageCore), MaxStacksFieldName);
                if (maxStacksFieldField == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 Building_StorageCore." + MaxStacksFieldName
                                + "：容量设为 500 以上仍可用，低于 500 会被原 mod 顶回 500。");
                }
                else
                {
                    MethodInfo spawn = AccessTools.Method(
                        typeof(Building_StorageCore), "SpawnSetup",
                        new Type[] { typeof(Map), typeof(bool) });
                    if (spawn == null)
                    {
                        Log.Warning("[数字存储补丁] 找不到 Building_StorageCore.SpawnSetup，"
                                    + "「地板值」只能对已存在的核心生效。");
                    }
                    else
                    {
                        new Harmony(HarmonyId).Patch(spawn, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Patch_CustomCapacity), nameof(Postfix_SpawnSetup))));
                        floorReady = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 存储容量自定义初始化失败（地板值部分）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            Log.Message("[数字存储补丁] 已挂载：数字核心存储容量可自定义"
                        + "（默认关闭；开启后按科技等级覆盖 " + string.Join("/", originals) + "）。");
        }

        /// <summary>
        /// 把设置同步到原 mod 的容量表与地板值。
        /// 启动时调用一次；设置界面里每帧调用一次（值没变时几乎零开销）。
        /// </summary>
        internal static void ApplyFromSettings()
        {
            if (!tableReady)
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null)
            {
                return;
            }

            bool on = s.customCapacityEnabled;
            for (int i = 0; i < Caps.Length; i++)
            {
                Caps[i] = on ? s.CapForLevel(i + 1) : originals[i];
            }

            try
            {
                int[] live = capByLevelField.GetValue(null) as int[];
                if (live != null)
                {
                    int n = Math.Min(Caps.Length, live.Length);
                    for (int i = 0; i < n; i++)
                    {
                        live[i] = Caps[i];
                    }
                    // Cap 每次现读数组元素 ⇒ 不需要通知原 mod 做任何缓存失效。
                }
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 写入 CoreTier." + CapByLevelName + " 失败："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            int floor = MinOf(Caps);
            if (floor == currentFloor)
            {
                return;
            }
            currentFloor = floor;
            // 地板值变了：已存在的核心就地更新（新核心由 SpawnSetup 的 Postfix 负责）。
            ApplyFloorToAllCores();
        }

        /// <summary>新核心生成：把地板值压到当前值。</summary>
        private static void Postfix_SpawnSetup(Building_StorageCore __instance)
        {
            if (!floorReady || __instance == null)
            {
                return;
            }
            SetFloor(__instance);
        }

        private static void SetFloor(Building_StorageCore core)
        {
            try
            {
                if ((int)maxStacksFieldField.GetValue(core) != currentFloor)
                {
                    maxStacksFieldField.SetValue(core, currentFloor);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 设置核心地板值失败（该核心容量可能偏大）："
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 更新所有已存在核心的地板值。
        /// 遍历各图的搬运源列表（原 mod 的核心由 <c>Thing.SpawnSetup</c> 自动注册进去），
        /// 只在"地板值真的变了"时执行 —— 即玩家改容量设置或开关时，不是每 tick。
        /// </summary>
        private static void ApplyFloorToAllCores()
        {
            if (!floorReady)
            {
                return;
            }
            try
            {
                List<Map> maps = Find.Maps;
                if (maps == null)
                {
                    return;
                }
                for (int i = 0; i < maps.Count; i++)
                {
                    List<IHaulSource> sources =
                        maps[i]?.haulDestinationManager?.AllHaulSourcesListForReading;
                    if (sources == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < sources.Count; j++)
                    {
                        if (sources[j] is Building_StorageCore c)
                        {
                            SetFloor(c);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 批量更新核心地板值失败（新核心仍会生效）："
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int MinOf(int[] arr)
        {
            int min = int.MaxValue;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] < min)
                {
                    min = arr[i];
                }
            }
            return min == int.MaxValue ? 500 : min;
        }
    }
}
