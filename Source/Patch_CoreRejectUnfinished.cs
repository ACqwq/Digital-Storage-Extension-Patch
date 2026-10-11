using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 从根源上避免「未完成产物被数字核心吞掉」这一类 bug（群友反馈）。
    ///
    /// <para><b>两道防线（同一个开关，均为事件驱动、不含每 tick 扫描）</b>：</para>
    /// <list type="number">
    /// <item><b>筛选层</b>：核心的存储筛选里把「未完成（Unfinished）」类别取消掉。
    /// 挂 <c>SpawnSetup</c> 的 Postfix —— 新建建筑与**读档**（respawningAfterLoad）都会经过它，
    /// 因此每次读档都会重新保证该类别未被勾选，玩家改回来也会在下次读档被纠正。</item>
    /// <item><b>保险层</b>：<c>Building_StorageCore.Accepts</c> 是原 mod 对外的**唯一准入询问**
    /// （搬运、自动收纳、收货架、产物投递都先问它），在这里再拒一次"未完成"物品 ——
    /// 即使某个路径绕过了筛选、或玩家手动勾上了该类别，物品也进不了核心。
    /// 只在<b>物品从外部进来</b>时拒绝（已在核心内的不拦），避免影响既有行为。</item>
    /// </list>
    ///
    /// <para><b>为什么不用"每 tick 检查核心内容物"</b>：那样既费性能又治标。
    /// 这里两道防线都挂在"事件点"上（建成/读档、以及每次准入询问），代价是一次
    /// <c>is</c> 判断 + 一次类别比对，本身就是 3.0 时代「单点判定」的做法
    /// （对应旧项目的 <c>ItemRouter.ShouldCoreTakeItem</c>：政策 → 筛选 → 容量 → 让位）。</para>
    ///
    /// <para><c>UnfinishedThing</c> 是原版类型（可用 <c>is</c> 判断）；
    /// 另配一次类别比对以覆盖"挂在 Unfinished 类别下但不是 UnfinishedThing"的 mod 物品。
    /// 该类别没有 <c>ThingCategoryDefOf</c> 常量，故用 <c>DefDatabase</c> 惰性取一次并缓存
    /// （Def 在加载后不变，缓存安全）。</para>
    /// </summary>
    internal static class Patch_CoreRejectUnfinished
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.rejectunfinished";

        private static bool categoryResolved;
        private static ThingCategoryDef unfinishedCategory;

        public static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                // ── 防线 1：筛选层（建成 / 读档时禁用 Unfinished 类别）──
                var spawnSetup = AccessTools.Method(typeof(Building_StorageCore), "SpawnSetup");
                var shield = AccessTools.Method(
                    typeof(Patch_CoreRejectUnfinished), nameof(ShieldFilterOnSpawnSetup));
                bool ok1 = false;
                if (spawnSetup != null && shield != null)
                {
                    harmony.Patch(spawnSetup, postfix: new HarmonyMethod(shield));
                    ok1 = true;
                }

                // ── 防线 2：保险层（准入询问时拒绝）──
                var accepts = AccessTools.Method(typeof(Building_StorageCore), "Accepts");
                var reject = AccessTools.Method(
                    typeof(Patch_CoreRejectUnfinished), nameof(RejectUnfinishedInAccepts));
                bool ok2 = false;
                if (accepts != null && reject != null)
                {
                    harmony.Patch(accepts, postfix: new HarmonyMethod(reject));
                    ok2 = true;
                }

                if (ok1 && ok2)
                {
                    Log.Message("[数字存储补丁] 已挂载：核心不再接收「未完成」物品"
                                + "（筛选层 + 准入层双重；设置 →「存储核心」可关）。");
                }
                else
                {
                    Log.Warning("[数字存储补丁] 核心拒收「未完成」物品：部分挂点定位失败"
                                + "（SpawnSetup=" + ok1 + "，Accepts=" + ok2 + "），原 mod 可能已改结构。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 核心拒收「未完成」物品安装失败（行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s == null || s.coreRejectUnfinished;
        }

        /// <summary>防线 1：新建成 / 读档后把「未完成」类别从核心筛选中取消。</summary>
        private static void ShieldFilterOnSpawnSetup(Building_StorageCore __instance)
        {
            if (__instance == null || !Enabled())
            {
                return;
            }

            try
            {
                ThingCategoryDef cat = UnfinishedCategory();
                if (cat == null)
                {
                    return;
                }

                StorageSettings settings = __instance.GetStoreSettings();
                if (settings?.filter != null)
                {
                    // ThingFilter 没有"按类别查询"的重载（只有 Thing/ThingDef），
                    // 故直接无条件设一次；SetAllow 幂等，重复调用无副作用。
                    settings.filter.SetAllow(cat, false);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 禁用核心筛选里的「未完成」类别失败："
                              + ex.GetType().Name + ": " + ex.Message, 8830161);
            }
        }

        /// <summary>
        /// 防线 2：准入询问时拒绝未完成物品。
        /// 只在"物品来自核心之外"时拒绝 —— 已经躺在核心里的不拦，避免影响既有逻辑。
        /// </summary>
        private static void RejectUnfinishedInAccepts(
            Building_StorageCore __instance, Thing t, ref bool __result)
        {
            if (!__result || __instance == null || t == null || !Enabled())
            {
                return;
            }

            if (t.ParentHolder == __instance)
            {
                return;   // 已在核心内：不拦
            }

            if (IsUnfinishedThing(t))
            {
                __result = false;
            }
        }

        private static bool IsUnfinishedThing(Thing t)
        {
            if (t is UnfinishedThing)
            {
                return true;   // 原版类型（含子类）
            }

            ThingCategoryDef cat = UnfinishedCategory();
            List<ThingCategoryDef> cats = t.def?.thingCategories;
            return cat != null && cats != null && cats.Contains(cat);
        }

        private static ThingCategoryDef UnfinishedCategory()
        {
            if (!categoryResolved)
            {
                categoryResolved = true;
                try
                {
                    unfinishedCategory = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Unfinished");
                }
                catch
                {
                    unfinishedCategory = null;
                }
            }

            return unfinishedCategory;
        }
    }
}
