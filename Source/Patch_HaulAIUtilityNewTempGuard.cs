using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// INVEST-14 方案 A（v0.61）：「幽灵物品」NRE 止血 —— 1.6 顺路搬运调用点的防护缺口。
    ///
    /// <para><b>现象（2026-10-11 玩家反馈）</b>：两名小人每 tick 抛
    /// <c>NullReferenceException</c>（约 260 行），作业系统软锁——每次开工都在"顺路搬运"判定上崩溃。</para>
    ///
    /// <para><b>根因（反编译实锤，1.6.4871 与玩家同版本）</b>：</para>
    /// <list type="number">
    /// <item>1.6 的 <c>Pawn_JobTracker.TryOpportunisticJob</c> 遍历
    /// <c>pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()</c>（搬运清单），
    /// 并对每项**直接调用** <c>HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp(pawn, item, forced: false)</c>；</item>
    /// <item><c>_NewTemp</c> 第一句即 <c>t.Fogged()</c> ⇒ <c>GridsUtility.Fogged = t.MapHeld.fogGrid.…</c>
    /// ⇒ <b><c>t.MapHeld == null</c>（无主游离物：不在地图、也不在任何容器）当场 NRE</b>；</item>
    /// <item>原 mod 明知搬运清单可能出现这类幽灵（其自动收纳/核心收纳链路的已知边界），为此在**两个旧方法名**上
    /// 挂了同款防护前缀——<c>Patch_HaulAIUtility_PawnCanAutomaticallyHaul</c>（查 <c>t.Map == null</c>）与
    /// <c>Patch_HaulAIUtility_PawnCanAutomaticallyHaulFast</c>（查 <c>t.MapHeld == null</c>）；
    /// 但 1.6 的调用点已改走 **<c>_NewTemp</c>**，两道防护都没盖住（玩家日志栈内 `_NewTemp` 帧无任何补丁标注，
    /// 而同栈其它被补丁的帧均有标注 ⇒ 实锤）。</item>
    /// </list>
    ///
    /// <para><b>修法</b>：给 <c>PawnCanAutomaticallyHaulFast_NewTemp</c> 挂**同语义**防护前缀——
    /// <c>p/t 为 null 或 t.MapHeld == null ⇒ __result = false</c>（该物品"不可自动搬运"）。
    /// 仅补覆盖缺口，不改变任何正常物品的判定；幽灵条目被跳过后继续留在清单里（与原 mod 两道防护的处理方式一致），
    /// 不做清除（根因另查，见 INVEST-14 §3/§5-C）。</para>
    ///
    /// <para><b>遥测</b>：每次拦下按物品 ID 记一条 Warning（每件只记一次，上限 64 件防刷屏），
    /// 内容含 ThingID / defName / 标签 / 残留坐标 / 是否已销毁 —— 用于根因定位（INVEST-14 §5-C）。</para>
    ///
    /// <para><b>降级</b>：找不到目标方法（原版改结构）→ 安装时跳过 + 告警；开关关闭 = 原行为（幽灵存在时报错复现）。
    /// 本前缀无状态、每帧零分配（除首次拦截日志），热路径开销可忽略。</para>
    /// </summary>
    internal static class Patch_HaulAIUtilityNewTempGuard
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.haulghostguard";

        /// <summary>已记录过拦截日志的物品（防刷屏；上限 64 件后不再新增记录）。</summary>
        private static readonly HashSet<int> LoggedThings = new HashSet<int>();

        public static void Install()
        {
            try
            {
                MethodInfo newTemp = AccessTools.Method(typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast_NewTemp");
                if (newTemp == null)
                {
                    Log.Warning("[数字存储补丁] 幽灵物品防护：找不到 HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp"
                                + "（原版可能已改结构），本项跳过。");
                    return;
                }

                new Harmony(HarmonyId).Patch(newTemp, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_HaulAIUtilityNewTempGuard), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：搬运判定不对幽灵物品崩溃"
                            + "（补 1.6 顺路搬运调用点的防护缺口；设置 →「性能」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 幽灵物品防护挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 与原 mod 的 <c>Patch_HaulAIUtility_PawnCanAutomaticallyHaulFast</c> 同语义：
        /// 目标/参数为空或目标无地图（<c>MapHeld == null</c>，含已销毁与无主游离物）⇒ 判定"不可自动搬运"。
        /// </summary>
        private static bool Prefix(Pawn p, Thing t, ref bool __result)
        {
            if (p == null || t == null || t.MapHeld != null)
            {
                return true;    // 正常物品：走原逻辑
            }

            __result = false;   // 幽灵：不可自动搬运（原 mod 旧入口的同款语义）
            LogOnce(t);
            return false;
        }

        private static void LogOnce(Thing t)
        {
            try
            {
                if (t == null || LoggedThings.Count >= 64 || !LoggedThings.Add(t.thingIDNumber))
                {
                    return;
                }

                Log.Warning("[数字存储补丁] 拦下一次对幽灵物品的搬运判定（该物品已不在任何地图/容器上，"
                            + "MapHeld=null；不做清除，仅跳过）：ID=" + t.ThingID
                            + ", def=" + (t.def != null ? t.def.defName : "?")
                            + ", label=" + (t.def != null ? t.LabelCap : "?")
                            + ", stack=" + t.stackCount
                            + ", destroyed=" + t.Destroyed
                            + ", lastPos=" + t.Position
                            + "。若反复出现，请连同存档反馈（用于定位幽灵的产生路径）。");
            }
            catch (Exception)
            {
                // 遥测失败不影响防护本身
            }
        }
    }
}
