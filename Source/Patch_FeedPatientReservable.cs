using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复「病人 / 婴儿喂食」反复报
    /// <c>TryMakePreToilReservations() returned false for a non-queued job right after StartJob()</c>
    /// 且**永远喂不成**的问题（实测 2026-10-08，日志见 `tmp/婴儿喂食相关-Player.log`）。
    ///
    /// <para>── 现象（日志实证）──</para>
    /// <para>三个小人轮流对**同一个**病人反复起 <c>FeedPatient</c> 作业，
    /// 每一次的食物目标（targetA）都是**新的一堆**（<c>Thing_BabyFood55727 → 55729 → 55734 → …</c>），
    /// 而病人目标（targetB）始终不变；每次都在 <c>StartJob</c> 之后立刻预约失败、作业作废，**无限循环**。</para>
    ///
    /// <para>── 根因（反编译逐行核实）──</para>
    /// <list type="number">
    /// <item>原 mod 的取食兜底(<c>Patch_FeedFromStorage</c>)挂在**查询**方法
    /// <c>FoodUtility.TryFindBestFoodSourceFor</c> 上，命中时会把食物
    /// <c>ExtractToFeet</c> 到小人脚下 ⇒ **每次查询都新生成一堆食物**
    /// （这就是 Thing 编号一路增长的原因）。</item>
    /// <item>原版 <c>JobDriver_FoodFeedPatient.TryMakePreToilReservations</c> 预留食物时用的是
    /// **<c>maxPawns = 10</c>**（<c>pawn.Reserve(Food, job, 10, maxAmountToPickup, …)</c>）。</item>
    /// <item>而 <c>ReservationManager.CanReserveStack(claimant, target, maxPawns)</c> 里写着：
    /// <code>if (reservation.MaxPawns != maxPawns) return 0;   // 已有一条 MaxPawns 不同的预留 ⇒ 直接 0</code>
    /// 普通物品预留的 <c>maxPawns</c> 是 **1**（搬运、其它作业都是）⇒ 那堆刚落到地上的食物
    /// **立刻被搬运工/别的作业以 maxPawns=1 预留**。</item>
    /// <item>于是：<c>GetMaxAmountToPickup</c> 拿到 0 ⇒ <c>pawn.Reserve(Food, job, <b>10</b>, 0, …)</c>
    /// 撞上 <c>CanReserve</c> 里同一句 <c>MaxPawns</c> 检查 ⇒ **返回 false** ⇒ 整个作业作废。</item>
    /// </list>
    /// <para>⇒ 取出的食物越积越多、作业永远起不来，形成死循环。</para>
    ///
    /// <para>── 修法：只修"输入"，不碰原版判定（铁律 ⑪）──</para>
    /// <para>Prefix 挂在 <c>JobDriver_FoodFeedPatient.TryMakePreToilReservations</c> 上：
    /// 如果当前食物目标**本来就预约不上**（<c>!CanReserve(food, 10, -1, …)</c>），
    /// 就从数字核心**重新取一份干净的食物**放到小人脚下（走 <see cref="CoreContentAccess.ExtractOne"/>：
    /// 按需取 + 负重上限 + 防自动吸回），并**按驱动将要使用的 maxPawns=10 先替它预留**，
    /// 然后把 <c>targetA</c> 换成这一份。之后原版自己的预约逻辑照跑
    /// （它命中"已被本人预留"⇒ 成功），**一行原版判定都没有被复制或改写**。</para>
    /// <list type="bullet">
    /// <item>只在"这次作业本来就会失败"时才动手 ⇒ **不可能让原本能成的喂食变坏**；</item>
    /// <item>核心/地图上再取不到同样的食物 ⇒ 返回 null ⇒ 什么都不做，行为与今天完全一致；</item>
    /// <item>关掉开关 = 完全回到原版（含原 bug）行为。</item>
    /// </list>
    ///
    /// <para>── 已知代价 ──</para>
    /// <para>被换掉的那一堆食物仍留在原地（带"刚取出"标记，之后会被自动收纳吸回核心，不会丢）。
    /// 与 <c>foodRestrictionAlign</c> 的副作用同源。</para>
    /// </summary>
    internal static class Patch_FeedPatientReservable
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.feedpatientreservable";

        /// <summary>原版驱动预留食物时用的 maxPawns（必须与它一致，否则我们替它预留也没用）。</summary>
        private const int DriverFoodMaxPawns = 10;

        public static void Install()
        {
            try
            {
                // TryMakePreToilReservations 是 public override 虚方法（方法体不小、无 JIT 内联风险）。
                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    AccessTools.Method(typeof(JobDriver_FoodFeedPatient), "TryMakePreToilReservations"),
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_FeedPatientReservable), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：病人喂食预约修复（原 mod 取出的食物被别人"
                            + "预留时，改喂一份干净的；设置 →「核心库存可用性」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 病人喂食预约修复安装失败（行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s == null || s.feedPatientReservable;   // 纯修复：默认开
        }

        private static void Prefix(JobDriver_FoodFeedPatient __instance)
        {
            if (!Enabled() || __instance == null)
            {
                return;
            }

            try
            {
                Pawn pawn = __instance.pawn;
                Job job = __instance.job;
                if (pawn == null || job == null || pawn.Map == null)
                {
                    return;
                }

                Thing food = job.targetA.Thing;
                if (food == null || food.def == null)
                {
                    return;   // 原版本来就会失败，不插手
                }
                if (food is Building_NutrientPasteDispenser)
                {
                    return;   // 原版对喂食器走另一条分支，不预留食物
                }
                if (pawn.inventory != null && pawn.inventory.Contains(food))
                {
                    return;   // 食物在小人身上 ⇒ 原版会跳过食物预留
                }
                if (pawn.CanReserve(food, DriverFoodMaxPawns, -1, null, false))
                {
                    return;   // 能预约 ⇒ 一切照原版
                }

                // ── 到这里：这次喂食必然预约失败。改喂一份刚取出的干净食物。──
                int want = Math.Max(1, job.count);
                Thing fresh = CoreContentAccess.ExtractOne(pawn, food.def, want, pawn.Map);
                if (fresh == null || fresh == food || fresh.Destroyed || !fresh.Spawned)
                {
                    return;   // 取不到 ⇒ 什么都不改，行为与今天一致
                }

                job.targetA = fresh;
                job.count = Math.Max(1, Math.Min(job.count, fresh.stackCount));
                // ★ 立刻按驱动将要用的 maxPawns 替它预留：这样原版那句
                //   pawn.Reserve(Food, job, 10, maxAmountToPickup, …) 会命中"已被本人预留"而通过。
                pawn.Reserve(fresh, job, DriverFoodMaxPawns, -1, null, false);

                DsPatchSettings s = DsPatchMod.Settings;
                if (s != null && s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][喂食] 原食物目标预约不上，已改用核心取出的 "
                                + fresh.def.defName + " ×" + fresh.stackCount
                                + "（病人=" + (job.targetB.Pawn?.LabelShort ?? "?") + "）。");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 病人喂食预约修复出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510401);
            }
        }
    }
}
