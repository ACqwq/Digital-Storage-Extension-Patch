using System;
using DigitalStorage.Components;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 性能缓解：跳过「核心搬运源重算」（实测占 77.5% 帧时间）。
    ///
    /// 背景：
    ///   原版 <c>ListerHaulables.HaulSourcesCheckTick</c> 每 tick 轮转 4 个搬运源，
    ///   对"有内容的源"调用 <c>RecalculateAllInHaulSource</c> —— 而该方法会对
    ///   该源的**每一件物品**执行 <c>ShouldBeHaulable</c> → <c>IsInValidBestStorage</c>
    ///   → <c>StoreUtility.TryFindBestBetterStorageFor</c>（一次完整储存搜索，含两段遍历）。
    ///
    ///   4.0 把核心改成了**物理容器**并让它实现 <c>IHaulSource</c> ⇒ 核心里的
    ///   约 880 堆物品**每 tick** 被全量重算一次 ⇒ 实测 643 万 + 644 万次储存搜索 / 15 秒，
    ///   吃掉 **77.5%** 的帧时间。3.0 是抽象账本（容器为空）所以从不触发。
    ///
    /// 为什么跳过是安全的：核心默认存储优先级「偏好」高于普通储存区「普通」，
    /// 这条链的判定结果恒为"不该搬运"，实际只执行了 <c>haulables.Remove(item)</c>
    /// （而该物品本来就不在 <c>haulables</c> 里）—— 纯浪费。
    ///
    /// 代价（唯一的语义差别）：若把核心优先级调到**低于**某储存区，
    /// 原本"小人把核心里的东西搬去更高优先级储存区"这条路径不再触发；
    /// 该场景由本补丁的「自动补货」覆盖（更可靠、每趟 1 堆、可开关）。
    ///
    /// 开关：Mod 设置 → 数字存储 · 补丁设置 → 性能 → 跳过核心搬运源重算（默认开）。
    ///
    /// ★ 4.0.1 起让位：原作者加了 <c>Performance.Patch_ListerHaulables_CoreSweep</c>——
    ///   同一个方法的更优 prefix（无更优储存区时用轻量扫描代替全量重算；有更优储存区时
    ///   250 tick 节流放行原版，保住"核心 → 更高优先级储存区"的搬运路径，比本补丁的
    ///   一刀切跳过更好）。检测到它且其设置 <c>perfOptimizationsEnabled</c> 为开时，
    ///   本补丁**完全让位**（直接 return true），否则两个 prefix 会互相短路：
    ///   谁先返回 false 谁就把对方和原版一起跳掉，效果要么退化成本补丁的旧语义、
    ///   要么把作者的优化整个吞掉。作者优化被用户关掉时，本补丁重新接管（兜底）。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_SkipCoreHaulRecalc
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.skipcorerecalc";

        /// <summary>原 mod 是否自带核心重算优化（4.0.1+）。只在静态构造时解析一次。</summary>
        private static readonly bool authorCoreSweepPresent =
            AccessTools.TypeByName("DigitalStorage.Performance.Patch_ListerHaulables_CoreSweep") != null;

        static Patch_SkipCoreHaulRecalc()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                // 目标是 private 方法，按名字取；取不到只记日志，不影响其它功能。
                var target = AccessTools.Method(typeof(ListerHaulables), "RecalculateAllInHaulSource");
                if (target == null)
                {
                    Log.Error("[数字存储补丁] 找不到 ListerHaulables.RecalculateAllInHaulSource，"
                              + "性能缓解未生效（原版可能改过结构）。");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_SkipCoreHaulRecalc), nameof(Prefix))));
                Log.Message("[数字存储补丁] 已挂载性能缓解：跳过核心搬运源重算"
                            + "（可在 Mod 设置里关闭）"
                            + (authorCoreSweepPresent
                                ? "。检测到原 mod 4.0.1+ 自带核心重算优化，本补丁已自动让位。"
                                : "") + ".");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 性能缓解挂载失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 一次性诊断：把见过的"搬运源类型"打进日志（每种最多一条），
        // 用于确认到底是谁被每 tick 全量重算。
        private static readonly System.Collections.Generic.HashSet<string> seenTypes =
            new System.Collections.Generic.HashSet<string>();

        private static bool Prefix(IHaulSource source)
        {
            // ★ 4.0.1+ 作者的 CoreSweep prefix 更优且与本补丁打在同一方法上：
            //   它开启时本补丁直接放行（不返回 false），把行为完全交给作者实现；
            //   它被关闭时本补丁照常接管（下方原有逻辑）。
            if (authorCoreSweepPresent && DigitalStorageSettings.perfOptimizationsEnabled)
            {
                return true;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            bool skip = s != null && s.skipCoreHaulRecalc && source is Building_StorageCore;

            try
            {
                string key = source == null ? "(null)" : source.GetType().FullName;
                if (seenTypes.Count < 6 && seenTypes.Add(key))
                {
                    Log.Message("[数字存储补丁] 搬运源重算命中类型：" + key
                                + (skip ? "（已跳过）" : "（未跳过）"));
                }
            }
            catch
            {
            }

            if (s == null || !s.skipCoreHaulRecalc)
            {
                return true; // 关掉开关 ⇒ 完全恢复原 mod 行为
            }
            return !skip;
        }
    }
}
