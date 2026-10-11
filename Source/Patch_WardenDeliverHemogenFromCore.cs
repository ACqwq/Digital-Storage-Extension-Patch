using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「狱警给囚犯送血原质包」能从数字核心取包（v0.47，C9）。
    ///
    /// ── 现象 ──
    /// 狱警要给「血原质依赖的囚犯」送血原质包时，若血包**全存数字核心**，狱警不会去送。
    /// （与 C7/C8 的"医生给殖民者输注血原质"是**不同工作、不同来源**，故单独一项。）
    ///
    /// ── 根因（反编译取证，<c>RimWorld/WorkGiver_Warden_DeliverHemogen.cs</c>）──
    /// <code>
    /// public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    /// {
    ///     ...门禁（监狱内 / 需要喂 / 有 Hemogenic 基因 / 允许血包 / 现在该补 / 囚犯身边还没有包）...
    ///     Thing thing = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
    ///         ThingRequest.ForDef(ThingDefOf.HemogenPack), PathEndMode.OnCell, TraverseParms.For(pawn), 9999f,
    ///         pack =&gt; !pack.IsForbidden(pawn) &amp;&amp; pawn.CanReserve(pack) &amp;&amp; pack.GetRoom() != prisoner.GetRoom());
    ///     if (thing == null) return null;
    ///     Job job = JobMaker.MakeJob(JobDefOf.DeliverFood, thing, prisoner);
    ///     job.count = 1;
    ///     job.targetC = RCellFinder.SpotToChewStandingNear(prisoner, thing);
    ///     return job;
    /// }
    /// </code>
    ///
    /// <para>⚠️ **本项独有：这个方法没有 <c>HasJobOnThing</c> 重写**。而
    /// <c>WorkGiver_Scanner.HasJobOnThing</c> 的默认实现就是 <c>JobOnThing(...) != null</c>
    /// ⇒ **每个候选囚犯会被连着调用两次 <c>JobOnThing</c>**（一次来自 HasJobOnThing、一次来自派活方）。
    /// 若不做处理，一次尝试会从核心取**两份**包（一份用、一份留在地上）——实测经验里这类"超发"很容易被误当 bug。
    /// ⇒ 因此这里加了**短时去重缓存**：同一小人短时间内重复调用时复用刚取出的那一份。</para>
    ///
    /// ── 做法 ──
    /// <c>JobOnThing</c> 挂 Postfix：原版返回 null 时复刻其全部门禁；若地图上确实没有**原版用得上的**血包、
    /// 而核心里有，则取 1 个到狱警脚下，构造**与原版同款**的 <c>DeliverFood</c> 作业（含 <c>targetC</c> 站位）。
    ///
    /// 关闭 = 完全回到原版行为。
    /// </summary>
    internal static class Patch_WardenDeliverHemogenFromCore
    {
        internal static int FixCount;

        /// <summary>短时去重窗口（tick）：覆盖"HJOT + 派活方"这一对连续调用。</summary>
        private const int DedupeTicks = 120;

        private static Pawn cachedPawn;
        private static Thing cachedPack;
        private static int cachedTick;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.wardenHemogenFromCore;
        }

        /// <summary>复刻原版 <c>HemogenPackAlreadyAvailableFor(prisoner)</c>（只读）。</summary>
        private static bool PackAlreadyAvailable(Pawn prisoner)
        {
            if (prisoner.carryTracker != null && prisoner.carryTracker.CarriedCount(ThingDefOf.HemogenPack) > 0)
            {
                return true;
            }
            if (prisoner.inventory != null && prisoner.inventory.Count(ThingDefOf.HemogenPack) > 0)
            {
                return true;
            }
            Room room = prisoner.GetRoom();
            if (room != null)
            {
                var regions = room.Regions;
                for (int i = 0; i < regions.Count; i++)
                {
                    if (regions[i].ListerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>复刻原版那次查找（只读）：返回 true 就说明原版自己找得到。</summary>
        private static bool HasUsablePackOnMap(Pawn pawn, Map map, Pawn prisoner)
        {
            return GenClosest.ClosestThingReachable(
                       pawn.Position, map, ThingRequest.ForDef(ThingDefOf.HemogenPack),
                       PathEndMode.OnCell, TraverseParms.For(pawn), 9999f,
                       pack => pack != null && !pack.IsForbidden(pawn) && pawn.CanReserve(pack)
                               && pack.GetRoom() != prisoner.GetRoom()) != null;
        }

        /// <summary>取一份血包到狱警脚下（带短时去重，见类注释）。</summary>
        private static Thing TakePack(Pawn pawn, Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cachedPawn == pawn && cachedPack != null && !cachedPack.Destroyed && cachedPack.Spawned
                && now - cachedTick < DedupeTicks)
            {
                return cachedPack;   // 复用同一对调用里刚取出的那份，避免双倍取货
            }

            Thing taken = CoreContentAccess.ExtractOne(pawn, ThingDefOf.HemogenPack, 1, map);
            if (taken != null)
            {
                cachedPawn = pawn;
                cachedPack = taken;
                cachedTick = now;
            }
            return taken;
        }

        [HarmonyPatch(typeof(WorkGiver_Warden_DeliverHemogen), "JobOnThing")]
        internal static class Patch_JobOnThing
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
            {
                if (__result != null || pawn == null || !Enabled())
                {
                    return;
                }

                if (!ModsConfig.BiotechActive)
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null || !(t is Pawn prisoner))
                {
                    return;
                }

                // ── 复刻原版全部前置（顺序同原版；任一不满足即等同原版结果）──
                // ShouldTakeCareOfPrisoner（基类 protected，这里按源码同款复刻）
                if (!prisoner.IsPrisonerOfColony
                    || prisoner.guest == null || !prisoner.guest.PrisonerIsSecure
                    || !prisoner.Spawned || prisoner.InAggroMentalState
                    || t.IsForbidden(pawn) || prisoner.IsFormingCaravan()
                    || !pawn.CanReserveAndReach(prisoner, PathEndMode.OnCell, pawn.NormalMaxDanger(), 1, -1, null, forced))
                {
                    return;
                }
                if (!prisoner.guest.CanBeBroughtFood || !prisoner.Position.IsInPrisonCell(map))
                {
                    return;
                }
                if (WardenFeedUtility.ShouldBeFed(prisoner))
                {
                    return;   // 该喂饭而不是补血原质
                }
                if (!(prisoner.genes?.GetGene(GeneDefOf.Hemogenic) is Gene_Hemogen gene))
                {
                    return;
                }
                if (!gene.hemogenPacksAllowed || !gene.ShouldConsumeHemogenNow())
                {
                    return;
                }
                if (PackAlreadyAvailable(prisoner))
                {
                    return;   // 囚犯那边本来就有包
                }

                // 地图上有原版用得上的包 ⇒ 原版会成功，null 是别因 ⇒ 不介入。
                if (HasUsablePackOnMap(pawn, map, prisoner))
                {
                    return;
                }

                try
                {
                    Thing pack = TakePack(pawn, map);
                    if (pack == null)
                    {
                        return;   // 核心也没有 ⇒ 放行原版
                    }

                    Job job = JobMaker.MakeJob(JobDefOf.DeliverFood, pack, prisoner);
                    job.count = 1;
                    job.targetC = RCellFinder.SpotToChewStandingNear(prisoner, pack);
                    __result = job;
                    FixCount++;

                    DsPatchSettings s = DsPatchMod.Settings;
                    if (s != null && s.diagnosticsEnabled)
                    {
                        Log.Message("[数字存储补丁][狱警送血包] 地图上没有血包，已从数字核心取出 1 个到脚下 @ "
                                    + pack.Position);
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取血包（狱警送包）时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510354);
                }
            }
        }
    }
}
