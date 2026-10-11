using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// E. 工作优先级设置 —— 只改 <c>WorkGiverDef.priorityInType</c>，其余交给原版自己的机制。
    ///
    /// ── 原理（原版 <c>JobGiver_Work</c> 的工作方式） ──
    /// 原版按 <c>priorityInType</c> **降序**逐个评估 giver；换到更低一档时立即返回上一档已找到的目标。
    /// 所以数值越大越先被考虑，"把本 mod 的 giver 排在原版之下"就等于原版真的先跑。
    ///
    /// ── 4.0 现状（已核实 Defs/WorkGiverDefs_DS.xml） ──
    /// 整个 mod **只剩 1 个 WorkGiverDef**：
    /// <code>
    /// DigitalStorage_WithdrawForConstruction   workType = Hauling   priorityInType = 200
    /// </code>
    /// 原版 <c>Hauling</c> 里与它相关的 giver（实测全表）：
    /// <code>
    /// RearmTurrets 150 / Refuel 140 / … / HaulGeneral 15 / DeliverResourcesToFrames 10 /
    /// DeliverResourcesToBlueprints 9 / HaulMerge 5   ← 全类型最低
    /// </code>
    /// ⇒ 200 = 数字存储硬赢（作者本意，排在所有 Hauling giver 之前）；
    ///   1   = 排到 <c>HaulMerge(5)</c> 之下，即"工作优先级置底"。
    ///
    /// ── 两处必须手动处理（原版只在启动时排过一次） ──
    /// 1. 重排 <c>WorkTypeDef.workGiversByPriority</c>（原版 <c>ResolveReferences</c> 里按 priorityInType
    ///    降序一次性填好，之后再没人动过）；
    /// 2. 把每个小人的 <c>Pawn_WorkSettings</c> 顺序缓存置脏 —— 用它的**公开方法**
    ///    <c>Notify_UseWorkPrioritiesChanged()</c>（已核实 1.6 反编译存在），**不需要反射**。
    ///
    /// ★ 刻意**不**去 Clear <c>workGiversInOrderNormal</c>：重建只看脏标志，
    ///   清空那个 List 会让小人拿到空工作列表而永久闲逛（3.0 记录过的坑）。
    ///
    /// ★ 与 3.0 版的差异（3.0 源码有个还原 bug，这里不照抄）：
    ///   3.0 还原时对**所有** giver 都写 <c>OriginalPriorities["DigitalStorage_WithdrawForBill"]</c>（999），
    ///   于是它那 4 个 giver（200/50/45/999）还原后全成了 999。
    ///   这里改为**按 defName 各自还原**（启动时对该 mod 的全部 giver 做一次快照）。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_WorkPriority
    {
        /// <summary>「工作优先级置底」时使用的值。必须低于原版 Hauling 里最低的 HaulMerge(5)。</summary>
        private const int VanillaFirstPriority = 1;

        /// <summary>本 mod 的 giver defName 前缀。</summary>
        private const string GiverPrefix = "DigitalStorage_";

        /// <summary>defName → 启动时的原始 priorityInType（只对本 mod 的 giver 建表）。</summary>
        private static readonly Dictionary<string, int> originals = new Dictionary<string, int>();

        private static bool snapshotTaken;

        private static DsPriorityMode appliedMode = (DsPriorityMode)(-1);

        /// <summary>原 mod 程序集（用它的类型取，避免写死程序集名）。</summary>
        private static Assembly originalAssembly;

        static Patch_WorkPriority()
        {
            // 静态构造一旦抛出，引用它的类型也会失败 ⇒ 全包兜底。
            try
            {
                originalAssembly = typeof(Building_StorageCore).Assembly;
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 取原 mod 程序集失败，工作优先级功能将按 defName 前缀判定："
                            + ex.GetType().Name);
            }

            try
            {
                Snapshot();
                Apply(force: true);
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 工作优先级初始化失败（将保持原 mod 的优先级）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>按当前设置重排。设置界面改动后调用。</summary>
        internal static void Apply(bool force = false)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            DsPriorityMode mode = s != null ? s.priorityMode : DsPriorityMode.ModDefault;
            if (!force && mode == appliedMode)
            {
                return;
            }

            bool changed = ApplyToExistingDefs(mode, out HashSet<WorkTypeDef> touched);
            if (changed)
            {
                ResortWorkTypes(touched);
                InvalidatePawnCaches();
            }
            appliedMode = mode;

            DsPatchSettings cur = DsPatchMod.Settings;
            if (cur != null && cur.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 工作优先级已应用：" + Describe(mode)
                            + "（改动 " + (changed ? "是" : "否") + "）。");
            }
        }

        internal static string Describe(DsPriorityMode mode)
        {
            return mode == DsPriorityMode.VanillaFirst
                ? "工作优先级置底（原 mod 的取料 giver = " + VanillaFirstPriority + "，排在原版之后）"
                : "原 mod 逻辑（不改 priorityInType）";
        }

        /// <summary>启动时快照本 mod 全部 giver 的原始 priorityInType。</summary>
        private static void Snapshot()
        {
            if (snapshotTaken)
            {
                return;
            }
            snapshotTaken = true;
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (IsOurGiver(d))
                {
                    originals[d.defName] = d.priorityInType;
                }
            }
        }

        /// <summary>是否属于原 mod 的 giver。</summary>
        private static bool IsOurGiver(WorkGiverDef d)
        {
            if (d == null || d.defName == null || !d.defName.StartsWith(GiverPrefix))
            {
                return false;
            }
            // 程序集判定：防止别的 mod 恰好用了同名前缀。
            // giverClass 解析失败（null）时只认前缀，宁可多管也别漏掉原 mod 的 giver。
            if (originalAssembly != null && d.giverClass != null
                && d.giverClass.Assembly != originalAssembly)
            {
                return false;
            }
            return true;
        }

        /// <summary>改写 priorityInType，并回传被波及的 WorkTypeDef。</summary>
        private static bool ApplyToExistingDefs(DsPriorityMode mode, out HashSet<WorkTypeDef> touched)
        {
            touched = new HashSet<WorkTypeDef>();
            if (!snapshotTaken)
            {
                Snapshot();
            }

            bool vanillaFirst = mode == DsPriorityMode.VanillaFirst;
            bool changed = false;

            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (!IsOurGiver(d))
                {
                    continue;
                }

                int want;
                if (vanillaFirst)
                {
                    want = VanillaFirstPriority;
                }
                else if (!originals.TryGetValue(d.defName, out want))
                {
                    // 快照之后才出现的 giver：保持现状，不猜。
                    want = d.priorityInType;
                }

                if (d.priorityInType != want)
                {
                    d.priorityInType = want;
                    changed = true;
                }
                if (d.workType != null)
                {
                    touched.Add(d.workType);
                }
            }
            return changed;
        }

        /// <summary>
        /// 按 priorityInType 降序重建受影响的 WorkTypeDef 的 giver 列表。
        /// 用 OrderByDescending（稳定排序）而不是 List.Sort（不稳定），
        /// 保证同分 giver 的相对次序与启用补丁前一致。
        /// </summary>
        private static void ResortWorkTypes(HashSet<WorkTypeDef> touched)
        {
            if (touched == null || touched.Count == 0)
            {
                return;
            }
            foreach (WorkTypeDef wt in touched)
            {
                List<WorkGiverDef> list = wt.workGiversByPriority;
                if (list == null || list.Count < 2)
                {
                    continue;
                }
                List<WorkGiverDef> sorted = list.OrderByDescending(g => g.priorityInType).ToList();
                list.Clear();
                list.AddRange(sorted);
            }
        }

        /// <summary>
        /// 把每个小人的工作顺序缓存置脏（原版会在下次取用时自动重建）。
        /// 用公开方法 <c>Notify_UseWorkPrioritiesChanged()</c>，不碰私有字段。
        /// </summary>
        private static void InvalidatePawnCaches()
        {
            try
            {
                if (Find.Maps == null)
                {
                    return;
                }
                for (int m = 0; m < Find.Maps.Count; m++)
                {
                    Map map = Find.Maps[m];
                    if (map == null || map.mapPawns == null)
                    {
                        continue;
                    }
                    IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
                    for (int p = 0; p < all.Count; p++)
                    {
                        Pawn pawn = all[p];
                        if (pawn != null && pawn.workSettings != null)
                        {
                            pawn.workSettings.Notify_UseWorkPrioritiesChanged();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 重排工作顺序缓存失败（改动会在原版自行重算时生效）："
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
