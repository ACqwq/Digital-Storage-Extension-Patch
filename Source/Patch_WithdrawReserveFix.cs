using System;
using System.Reflection;
using DigitalStorage.AI;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 建造取料修复 **H1**：让「从核心取料去建造」的作业**遵守预留**（纯修复，默认开）。
    ///
    /// <para><b>缺陷（见报告 FIX-10 §9）</b>：原 mod 的
    /// <c>JobDriver_DS_Withdraw.TryMakePreToilReservations(bool errorOnFailed)</c>
    /// **忽略传入的 <c>errorOnFailed</c>**（硬写 <c>false</c>）并且**恒 <c>return true</c>**
    /// ⇒ 预留失败也照常开工。对照原版 <c>JobDriver_HaulToContainer.TryMakePreToilReservations</c>：
    /// <c>if (!pawn.Reserve(...)) return false;</c> —— **预留失败就作废作业**。</para>
    ///
    /// <para><b>后果</b>：同一格可以被**多个真小人**同时派去取料（预留形同虚设），
    /// 加上该 driver 放货时是「盲加」（见 <see cref="Patch_WithdrawPlaceCap"/>），
    /// 于是每格木料 = 20 × (1 + N)，一路投到核心见底（用户实测 6.6 倍）。</para>
    ///
    /// <para><b>修法</b>：Prefix 自己按**原版同形**做两次预留（建造目标 + 核心里的那堆料），
    /// 任一失败就 <c>__result = false</c> 并跳过原实现 ⇒ 让"同一目标同时只有一个在送料"重新成立。</para>
    ///
    /// <para><b>为什么不复制原 mod 逻辑</b>：这里要改的正是"该方法的判据"本身，
    /// 且只用到两个**原版** API（<c>Pawn.Reserve</c> + <c>Job.GetTarget</c>），
    /// 没有复制任何原 mod / 原版的业务判定。目标清单与取料量口径仍完全按原 mod 自己的写法
    /// （<c>stackCount = job.count &gt; 0 ? job.count : -1</c>）。</para>
    ///
    /// <para><b>日志</b>：为了不刷 <c>Could not reserve</c>，内部 <c>Reserve</c> 用
    /// <c>errorOnFailed: false</c> 静默失败，失败由我们**自己判定并作废作业**；
    /// 开「诊断日志」时会记一行说明拦下了哪一次重复派工。</para>
    /// </summary>
    internal static class Patch_WithdrawReserveFix
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.withdrawreserve";

        public static void Install()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(JobDriver_DS_Withdraw), "TryMakePreToilReservations");
                if (target == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 JobDriver_DS_Withdraw.TryMakePreToilReservations"
                                + "（原 mod 可能已改结构），「建造取料遵守预留」不可用（其余功能不受影响）。");
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    target,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_WithdrawReserveFix), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：建造取料遵守预留"
                            + "（防止多个小人同时给同一格重复送料；默认开启，设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 建造取料遵守预留挂载失败（该问题保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 返回 <c>false</c> = 跳过原 mod 实现（用我们算出来的 <c>__result</c>）。
        /// 开关关闭 / 结构异常时返回 <c>true</c>（完全交回原 mod）。
        /// </summary>
        private static bool Prefix(JobDriver_DS_Withdraw __instance, ref bool __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.withdrawRespectReservations || __instance == null)
            {
                return true;
            }

            Pawn pawn = __instance.pawn;
            Job job = __instance.job;
            if (pawn == null || job == null || pawn.Map == null)
            {
                return true;
            }

            try
            {
                // ① 建造目标（蓝图 / 框架）：同一目标同一时刻只允许一个在送料
                Thing targetA = job.GetTarget(TargetIndex.A).Thing;
                if (targetA != null && !targetA.Destroyed)
                {
                    if (!pawn.Reserve(targetA, job, 1, -1, null, errorOnFailed: false))
                    {
                        Blocked("建造目标", targetA, pawn);
                        __result = false;
                        return false;
                    }
                }

                // ② 核心里的那堆料（取料量口径与原 mod 一致）
                Thing source = job.GetTarget(TargetIndex.B).Thing;
                if (source != null && !source.Destroyed && source.MapHeld == pawn.Map)
                {
                    int stackCount = job.count > 0 ? job.count : -1;
                    if (!pawn.Reserve(source, job, 1, stackCount, null, errorOnFailed: false))
                    {
                        Blocked("核心料堆", source, pawn);
                        __result = false;
                        return false;
                    }
                }

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                // 任何意外都退回原 mod 行为，绝不因为本补丁让作业起不来
                Log.ErrorOnce("[数字存储补丁] 建造取料预留校验异常（本条交回原 mod 处理）："
                              + ex.GetType().Name + ": " + ex.Message, 771501);
                return true;
            }
        }

        private static void Blocked(string what, Thing t, Pawn pawn)
        {
            if (DsPatchMod.Settings == null || !DsPatchMod.Settings.diagnosticsEnabled)
            {
                return;
            }
            Log.Message("[数字存储补丁][建造取料] " + pawn.LabelShort + " 的取料作业被拦下："
                        + what + "（" + t.LabelShort + " @ " + t.PositionHeld
                        + "）已被别人预留 —— 避免同一格被重复送料。");
        }
    }
}
