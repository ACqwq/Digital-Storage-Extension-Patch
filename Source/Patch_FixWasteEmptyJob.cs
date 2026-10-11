using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复「清空废料容器」在数字存储殖民地失效（Vanilla Biotech × 非槽组存储 的交叉 bug）。
    ///
    /// <para><b>两代问题（同一 WorkGiver 的两半）</b>：</para>
    /// <para>① <b>job 起不来</b>（v0.41 已修）：核心能被找到时，
    /// <c>HasJobOnThing</c> 说"有活"，<c>JobOnThing</c> 却用 <c>foundCell=Invalid</c> 建 job
    /// ⇒ 预约失败 ⇒ 刷 <c>yielded no actual job</c> 报错。</para>
    /// <para>② <b>静默罢工</b>（v0.42 修，本轮）：原版 <c>HasJobOnThing</c> 的<b>最后一道门</b>是
    /// <c>StoreUtility.TryFindBestBetterStorageFor(废料, …)</c> —— 找不到任何去处时直接
    /// <c>return false</c> + <c>JobFailReason</c>，<b>不写任何错误日志</b>。
    /// 「只有数字核心」的殖民地里唯一去处就是核心，而核心会被以下任一情况挡住：
    /// 容器满（<c>Accepts</c> 的 <c>maxStacks</c> 上限）、
    /// 正被别人预留（<c>CanReserveNew</c>：取物 / 搬运入库任务进行中）、
    /// 交互格被占用、不可达 ⇒ 这道门关闭 ⇒ 无 job 派发、右键无选项、无报错，
    /// 充电桩 / 培育器里的有毒垃圾永远取不出来（充电桩满后机械体连电都充不了）。</para>
    ///
    /// <para><b>修法（与 v0.41 同一哲学：找不到正规去处就就近落地）</b>：</para>
    /// <list type="number">
    /// <item><c>HasJobOnThing</c> Postfix：原版返回 false 时，若其余门槛都满足
    /// （有废料、可清空、可预留该建筑），且原版自带的三级兜底
    /// <c>TryFindStoreCellNearColonyDesperate</c> 找得到落点 ⇒ 放行为 true。</item>
    /// <item><c>JobOnThing</c> Postfix：原版因"找不到去处"返回 null 时，用同一兜底格
    /// 照原版的样子补造 job（A=建筑、B=废料、C=落点、count=废料堆数）；
    /// 原版建出的 job C 无效时照旧填就近格（v0.41 逻辑保留）。</item>
    /// </list>
    ///
    /// <para>废料落在核心附近地上，之后由原 mod 的自动收纳（核心腾出空间后）或小人搬走。
    /// 与用户确认过的口径一致："就算不带去核心直接丢地上也是没问题的（而且还有自带收纳）"。</para>
    ///
    /// <para>开关：Mod 设置 → 数字存储 · 补丁设置 → 性能 →「修复清空废料容器」（默认开）。
    /// 关闭即完全回退原版行为。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_FixWasteEmptyJob
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.wasteemptyjob";

        private static bool loggedFix;
        private static bool loggedUnlock;

        static Patch_FixWasteEmptyJob()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                var jobOnThing = AccessTools.Method(typeof(WorkGiver_EmptyWasteContainer), "JobOnThing");
                var hasJobOnThing = AccessTools.Method(typeof(WorkGiver_EmptyWasteContainer), "HasJobOnThing");
                bool okJob = false, okHas = false;
                if (jobOnThing != null)
                {
                    harmony.Patch(jobOnThing, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_FixWasteEmptyJob), nameof(JobOnThingPostfix))));
                    okJob = true;
                }
                if (hasJobOnThing != null)
                {
                    harmony.Patch(hasJobOnThing, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_FixWasteEmptyJob), nameof(HasJobOnThingPostfix))));
                    okHas = true;
                }
                if (okJob && okHas)
                {
                    Log.Message("[数字存储补丁] 已挂载：清空废料容器（充电桩 / 机械培育器 / 废料容器）兜底 —— "
                                + "落点无效改为就近落地；找不到去处（核心满 / 正被占用等）时不再静默罢工。");
                }
                else
                {
                    Log.Warning("[数字存储补丁] 清空废料容器兜底：挂点定位失败"
                                + "（JobOnThing=" + okJob + "，HasJobOnThing=" + okHas + "），原版结构可能已变。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 清空废料容器兜底挂载失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.fixWasteEmptyJob;
        }

        /// <summary>复刻原版 HasJobOnThing 里"存储判据之前"的所有门槛（不含存储判据本身）。</summary>
        private static bool PassesGatesBeforeStorage(Pawn pawn, Thing t, bool forced)
        {
            if (!ModsConfig.BiotechActive || t == null || pawn == null || pawn.Map == null)
            {
                return false;
            }
            CompWasteProducer comp = t.TryGetComp<CompWasteProducer>();
            if (comp == null || !comp.CanEmptyNow)
            {
                return false;
            }
            Thing waste = comp.Waste;
            if (waste == null || waste.stackCount <= 0)
            {
                return false;
            }
            // 与原版同参：层 = Empty；forced 时无视他人预留（原版语义）
            return pawn.CanReserve(t, 1, -1, ReservationLayerDefOf.Empty, forced);
        }

        private static Thing WasteOf(Thing t)
        {
            return t.TryGetComp<CompWasteProducer>()?.Waste;
        }

        /// <summary>
        /// HasJobOnThing Postfix：原版返回 false 时，若唯一被否决的原因是"找不到去处"
        /// （其余门槛都满足），且就近落地可行 ⇒ 放行为 true。
        /// </summary>
        private static void HasJobOnThingPostfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result || !Enabled())
            {
                return;
            }
            if (!PassesGatesBeforeStorage(pawn, t, forced))
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            Thing waste = WasteOf(t);
            if (StoreUtility.TryFindStoreCellNearColonyDesperate(waste, pawn, out _))
            {
                __result = true;
                if (s.diagnosticsEnabled && !loggedUnlock)
                {
                    loggedUnlock = true;
                    Log.Message("[数字存储补丁] 清空废料容器：原版因「找不到废料去处」不派工"
                                + "（核心满 / 正被占用 / 无普通储存区），已改为允许就近落地（本条只记一次）。");
                }
            }
        }

        /// <summary>
        /// JobOnThing Postfix：① 原版建出的 job C 无效时填就近格（v0.41 逻辑）；
        /// ② 原版因"找不到去处"返回 null 时，用同一兜底格补造 job（与 HasJobOnThing 放行配套）。
        /// </summary>
        private static void JobOnThingPostfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.fixWasteEmptyJob || pawn == null || t == null)
            {
                return;
            }

            if (__result != null)
            {
                FixInvalidTargetCell(pawn, s, __result);
                return;
            }

            // ── 原版返回 null：若其余门槛满足，说明败在"找不到去处" ⇒ 就近落地补造 ──
            if (!PassesGatesBeforeStorage(pawn, t, forced))
            {
                return;
            }
            Thing waste = WasteOf(t);
            // 兜底搜索含随机步骤；重试 3 次以对齐 HasJobOnThing 的放行判定
            for (int i = 0; i < 3; i++)
            {
                if (StoreUtility.TryFindStoreCellNearColonyDesperate(waste, pawn, out IntVec3 cell))
                {
                    Job job = JobMaker.MakeJob(JobDefOf.EmptyWasteContainer, t, waste, cell);
                    job.count = waste.stackCount;
                    __result = job;
                    if (s.diagnosticsEnabled && !loggedFix)
                    {
                        loggedFix = true;
                        Log.Message("[数字存储补丁] 清空废料 job 的储存格原为无效/缺失"
                                    + "（最优目标是核心/投影核心这类非槽组存储），已改为就近可落格 "
                                    + cell + "（本条只记一次）。");
                    }
                    return;
                }
            }
        }

        /// <summary>v0.41 逻辑：原版建出的 job 目标格（C）无效时，填一个就近可落格。</summary>
        private static void FixInvalidTargetCell(Pawn pawn, DsPatchSettings s, Job job)
        {
            if (job.GetTarget(TargetIndex.C).IsValid)
            {
                return;
            }
            Thing waste = job.GetTarget(TargetIndex.B).Thing;
            Map map = pawn.Map;
            if (waste == null || map == null)
            {
                return;
            }
            if (StoreUtility.TryFindStoreCellNearColonyDesperate(waste, pawn, out IntVec3 cell))
            {
                job.SetTarget(TargetIndex.C, cell);
                if (s.diagnosticsEnabled && !loggedFix)
                {
                    loggedFix = true;
                    Log.Message("[数字存储补丁] 清空废料 job 的储存格原为无效"
                                + "（最优目标是核心/投影核心这类非槽组存储），已改为就近可落格 "
                                + cell + "（本条只记一次）。");
                }
            }
        }
    }
}
