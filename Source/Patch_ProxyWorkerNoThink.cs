using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 代理工人（数字工人 / 制作代理）的**隐藏替身不再被原版 AI 派活**（纯修复，默认开）。
    ///
    /// <para><b>现象</b>：日志反复出现两类红字，且数量互相呼应 ——</para>
    /// <list type="bullet">
    /// <item><c>Tried to FindPath for pawn which is spawned in another map. … pawn.Map= map=Map-0-PlayerHome</c></item>
    /// <item><c>数字工人 started 10 jobs in one tick. newJob=Wear … jobGiver=RimWorld.JobGiver_OptimizeApparel</c></item>
    /// </list>
    ///
    /// <para><b>根因（反编译逐行核实，见报告 FIX-09）</b>：原 mod 把替身小人当"施工者/工人句柄"
    /// 喂进原版链路（如 <c>DigitalTask_Construct.TryReplace</c> → <c>Blueprint.TryReplaceWithSolidThing</c>），
    /// 而原版该方法在建造被挡时会对 worker 调
    /// <c>workerPawn.jobs.EndCurrentJob(JobCondition.Incompletable)</c>
    /// （<c>RimWorld/Blueprint.cs:51</c>）。</para>
    ///
    /// <para><c>Pawn_JobTracker.EndCurrentJob</c> 在 <c>curJob == null</c> 时一路不拦，
    /// 末尾<b>无条件</b>调 <c>TryFindAndStartJob()</c>（<c>Pawn_JobTracker.cs:472</c>）；
    /// 而 <c>TryFindAndStartJob</c> 全文只有"thinker 不为 null"一道门
    /// （<c>Pawn_JobTracker.cs:602-624</c>）—— <b>既不检查 <c>mindState.Active</c>，
    /// 也不要求该 pawn 被 tick</b>。</para>
    ///
    /// <para>⇒ 原 mod 那两处 <c>mindState.Active = false</c>（<c>DigitalWorkerFactory.Create</c>、
    /// <c>DigitalWorkerScope.Enter</c>）在这条链上**等于没设**，"替身不进 TickList"也**不构成保护**。
    /// 于是人形主思考树照跑；替身恰好满足 <c>JobGiver_OptimizeApparel</c> 的全部前置
    /// （<c>AddComponentsForSpawn</c> 因它是 <c>Faction.OfPlayer</c> 而给了 <c>outfits</c>）⇒ 派 <c>Wear</c>。
    /// <c>StartJob</c> 会**同步**跑第一个 toil（<c>:370</c>）⇒ 用"此刻的 <c>pawn.Map</c>"建路径请求；
    /// 随后 mod 退出 <c>DigitalWorkerScope</c>（<c>mapIndexOrState ← -1</c>）⇒ <c>pawn.Map == null</c>
    /// ⇒ 路径校验失败刷红字（<c>Verse/PathRequest.cs:127</c>）；作业必然失败 ⇒ 又回到
    /// <c>EndCurrentJob</c> ⇒ 同一 tick 反复 ⇒ 撞上 <c>started 10 jobs in one tick</c>（<c>:267</c>）。</para>
    ///
    /// <para><b>修法</b>：给原版 <c>Pawn_JobTracker.TryFindAndStartJob</c> 挂 Prefix ——
    /// <b>当 <c>mindState.Active</c> 为 false 时跳过本次思考</b>。这不是新造判据：
    /// 原版自己在 tick 路径上就是这么做的（<c>JobTrackerTickInterval</c> 的
    /// <c>if (curJob == null &amp;&amp; !pawn.Dead &amp;&amp; pawn.mindState.Active)</c>，<c>:197</c>），
    /// 本补丁只是把**同一道门**补到 <c>EndCurrentJob</c> 这条侧门上。</para>
    ///
    /// <para><b>为什么不会伤到真小人</b>：<c>mindState.Active</c> 的唯一读点就是上面那处，
    /// 而它只在 <c>Pawn_MindState</c> 构造时默认为 false，之后由
    /// <c>Pawn.Notify_Unfogged</c> / <c>FloodFillerFog</c> / <c>Pawn_HealthTracker.PreApplyDamage</c>
    /// 置为 true。⇒ 它标记的是"**地图迷雾里还没唤醒的单位**"，这类 pawn **本来就不该思考**
    /// （原版 tick 路径已经拦着它们）。正常游玩中的小人恒为 true，行为完全不变。</para>
    ///
    /// <para><b>附带收益</b>：FEAT-01 §8 记录的 <c>newJob=HaulToContainer</c> 红字同源，一并消失。</para>
    ///
    /// <para><b>反射说明</b>：<c>TryFindAndStartJob</c> 与 <c>Pawn_JobTracker.pawn</c> 都是
    /// 原版 private/protected 成员，用 <c>AccessTools</c> 定位；字段读取用
    /// <c>FieldRefAccess</c> 做成委托缓存 —— 该方法是"每次起作业"都会走的热路径，
    /// 不能用 <c>FieldInfo.GetValue</c>。</para>
    /// </summary>
    internal static class Patch_ProxyWorkerNoThink
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.proxyworkernothink";

        /// <summary><c>Pawn_JobTracker.pawn</c>（protected）的快速读取委托。</summary>
        private static AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnField;

        public static void Install()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(Pawn_JobTracker), "TryFindAndStartJob");
                if (target == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 Pawn_JobTracker.TryFindAndStartJob"
                                + "（原版可能已改结构），「代理工人不再被原版派活」不可用（其余功能不受影响）。");
                    return;
                }

                PawnField = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");
                if (PawnField == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 Pawn_JobTracker.pawn 字段"
                                + "（原版可能已改结构），「代理工人不再被原版派活」不可用（其余功能不受影响）。");
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    target,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_ProxyWorkerNoThink), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：代理工人的隐藏替身不再被原版 AI 派活"
                            + "（修「被派去穿装备」一类的反复报错与红字刷屏；默认开启，"
                            + "设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 代理工人替身思考拦截挂载失败（该问题保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 返回 <c>false</c> = 跳过原方法（本次不思考）。
        /// 其余情况一律 <c>true</c>（完全交给原版）。
        /// </summary>
        private static bool Prefix(Pawn_JobTracker __instance)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.proxyWorkerNoThink || __instance == null)
            {
                return true;   // 开关关闭 ⇒ 原版行为
            }

            Pawn p = PawnField != null ? PawnField(__instance) : null;
            if (p == null)
            {
                return true;
            }

            Pawn_MindState mind = p.mindState;
            if (mind == null || mind.Active)
            {
                return true;   // 正常小人（含迷雾唤醒后的）照常思考
            }

            // 隐藏的、未被唤醒的单位（代理工人的替身就在此列）：与 JobTrackerTickInterval
            // 的门保持一致 —— 不思考。原 mod 的 mindState.Active = false 正是这个意图。
            return false;
        }
    }
}
