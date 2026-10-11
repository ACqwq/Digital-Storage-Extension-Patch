using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 建造取料修复 **H3**：给"往在建物里放料"这一步**加一道按剩余量截断的兜底**（纯修复，默认开）。
    ///
    /// <para><b>缺陷（见报告 FIX-10 §9）</b>：原 mod 的
    /// <c>JobDriver_DS_Withdraw.PlaceHauled()</c> 里是
    /// <c>thingOwner.TryAdd(carriedThing)</c> —— **盲加**：既不问"这个在建物还差多少"，
    /// 也不看小人在路上时别人已经送了多少。而 <c>ThingOwner</c> **没有容量上限**
    /// ⇒ 一个 20 木的地板框架可以被塞到 151 / 284 / 420 木。
    /// 对照原版 <c>Toils_Haul.PlaceHauledThingInContainer</c>：
    /// <c>num = Mathf.Min(haulEnroute.GetSpaceRemainingWithEnroute(def, actor), num);</c> —— **按剩余量截断**。</para>
    ///
    /// <para><b>后果链</b>：多塞进去的那部分，框架建成时被
    /// <c>Frame.CompleteConstruction → resourceContainer.ClearAndDestroyContents()</c> **全量销毁**；
    /// 而核心被提前掏空后，剩下的在建物判"核心不够"直接放弃 ⇒ **材料凭空少、工程做不完**。</para>
    ///
    /// <para><b>修法</b>：Postfix 挂 <c>JobDriver_DS_Withdraw.MakeNewToils()</c>，
    /// 给每个 toil 的 <c>initAction</c> **前面**插一道 <c>CapCarriedToNeed</c>：
    /// 若小人手上那份**超过了目标当前还缺的量**，就把多出来的部分**拆分出来并送回最近的数字核心**
    /// （送不进去才落地，且**不标记禁止**）⇒ 后续原逻辑照常跑，只是手里只剩下"刚好够"的那份。</para>
    ///
    /// <para><b>为什么这样挂</b>：<c>MakeNewToils</c> 是<b>迭代器</b>，Postfix 收到的是未执行的
    /// 枚举对象 ⇒ 先 <c>ToList()</c> 把它实体化，再逐个包装 <c>initAction</c>，最后把列表交回去。
    /// 这样**不需要**去反射调用原方法（避免"在自身补丁里调自身"导致的递归风险），
    /// 也**不复制**任何原 mod 的放料逻辑（只在其前面加一次截断）。</para>
    ///
    /// <para><b>兜底属性</b>：H1（<see cref="Patch_WithdrawReserveFix"/>）让预留重新生效，
    /// 本项则保证"即使还有并发也不可能超投" —— 两者互补。</para>
    /// </summary>
    internal static class Patch_WithdrawPlaceCap
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.withdrawplacecap";

        private static int cappedCount;

        public static void Install()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(JobDriver_DS_Withdraw), "MakeNewToils");
                if (target == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 JobDriver_DS_Withdraw.MakeNewToils"
                                + "（原 mod 可能已改结构），「投料按剩余量截断」不可用（其余功能不受影响）。");
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    target,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_WithdrawPlaceCap), nameof(Postfix))));

                Log.Message("[数字存储补丁] 已挂载：建造投料按剩余量截断"
                            + "（放料前把多出来的那份退回核心；默认开启，设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 建造投料截断挂载失败（该问题保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>把 driver 的每个 toil 的 <c>initAction</c> 包一层"先截断、再跑原逻辑"。</summary>
        private static void Postfix(JobDriver_DS_Withdraw __instance, ref IEnumerable<Toil> __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.withdrawCapToNeed || __instance == null || __result == null)
            {
                return;
            }

            try
            {
                List<Toil> toils = __result.ToList();
                if (toils.Count == 0)
                {
                    __result = toils;
                    return;
                }

                JobDriver driver = __instance;
                for (int i = 0; i < toils.Count; i++)
                {
                    Toil toil = toils[i];
                    if (toil == null)
                    {
                        continue;
                    }
                    Action original = toil.initAction;
                    toil.initAction = delegate
                    {
                        CapCarriedToNeed(driver);
                        original?.Invoke();
                    };
                }

                __result = toils;
            }
            catch (Exception ex)
            {
                // 包装失败就交回原 mod 的 toil（不拦作业）
                Log.ErrorOnce("[数字存储补丁] 建造投料截断包装失败（本条交回原 mod 处理）："
                              + ex.GetType().Name + ": " + ex.Message, 771502);
            }
        }

        /// <summary>
        /// 若手上那份超过了目标当前还缺的量 ⇒ 把多出来的拆出来退回核心。
        /// 其余情况一律不动（幂等、无副作用）。
        /// </summary>
        private static void CapCarriedToNeed(JobDriver driver)
        {
            try
            {
                Pawn actor = driver?.pawn;
                if (actor == null || actor.Map == null)
                {
                    return;
                }

                Thing carried = actor.carryTracker?.CarriedThing;
                if (carried == null || carried.Destroyed)
                {
                    return;
                }

                // 目标就是 driver 的 TargetIndex.A（蓝图或框架）
                if (!(driver.job?.GetTarget(TargetIndex.A).Thing is IConstructible target))
                {
                    return;
                }

                int need = target.ThingCountNeeded(carried.def);
                if (need >= carried.stackCount)
                {
                    return;   // 没超（含"还差得更多"）⇒ 不动
                }

                int excess = carried.stackCount - Mathf.Max(need, 0);
                if (excess <= 0)
                {
                    return;
                }

                Thing extra = actor.carryTracker.innerContainer.Take(carried, excess);
                if (extra == null || extra.stackCount <= 0)
                {
                    return;
                }

                ReturnExcess(actor, extra);
                cappedCount++;

                if (DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][建造取料] " + actor.LabelShort
                                + "：目标只需再 " + Mathf.Max(need, 0) + " × " + carried.def.label
                                + "，已从手上退回 " + extra.stackCount + " 份（累计拦截 " + cappedCount
                                + " 次），避免塞进在建物后被销毁。");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 建造投料截断异常（本条跳过，不影响其他作业）："
                              + ex.GetType().Name + ": " + ex.Message, 771503);
            }
        }

        /// <summary>把多出来的那份送回最近的数字核心；实在送不进去才落地（不标记禁止，便于自动收纳吸回）。</summary>
        private static void ReturnExcess(Pawn actor, Thing extra)
        {
            ThingOwner carryContainer = actor.carryTracker?.innerContainer;
            Map map = actor.Map;

            List<IHaulSource> cores = (map != null) ? HaulSourceContents.CoreSources(map) : null;
            if (cores != null && carryContainer != null)
            {
                for (int i = 0; i < cores.Count; i++)
                {
                    ThingOwner owner = cores[i]?.GetDirectlyHeldThings();
                    if (owner == null || !owner.CanAcceptAnyOf(extra))
                    {
                        continue;
                    }
                    carryContainer.TryTransferToContainer(extra, owner, extra.stackCount);
                    if (owner.Contains(extra) || extra.Destroyed || extra.stackCount <= 0)
                    {
                        return;   // 已进核心
                    }
                }
            }

            if (map != null)
            {
                // 送不进核心（核心满 / 被占用）才落地；不 SetForbidden，交给原版搬运或自动收纳
                GenPlace.TryPlaceThing(extra, actor.PositionHeld, map, ThingPlaceMode.Near);
            }
        }
    }
}
