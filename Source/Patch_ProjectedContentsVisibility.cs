using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「被投影的核心」在本图的**资源计数**与**交易可售列表**里出现。
    ///
    /// 为什么不用现成的：原 mod 的两处（<c>Patch_ResourceCounter_HaulSources</c>、
    /// <c>Patch_AllLaunchableThingsForTrade</c>）都是按"本图搬运源"收集内容的，
    /// 而投影核心**刻意不实现 IHaulSource**（否则原版 <c>HaulSourcesCheckTick</c>
    /// 会定期把被绑定核心的全部内容物在本图再算一遍，同一核心 N 个投影就算 N 遍，
    /// 纯浪费性能 —— 见 Building_CoreProjector 顶部注释）。
    ///
    /// 所以这里用**自己的**两个补丁点，直接按"每图每核心一次"补齐（已按核心去重）：
    ///   - <c>ResourceCounter.UpdateResourceCounts</c> —— 资源计数
    ///   - <c>TradeUtility.AllLaunchableThingsForTrade</c> —— 交易可售列表
    ///
    /// 两者都只在原版/原 mod 本来就会跑的时机执行（计数按原版节流、交易只在打开对话框时），
    /// 不新增任何周期扫描。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_ProjectedContentsVisibility
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.projectedcontents";

        static Patch_ProjectedContentsVisibility()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 投影库存可见性补丁安装失败（投影仍可存取，"
                          + "但本图统计/交易可能看不到被投影核心的内容）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Install()
        {
            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;

            MethodInfo counter = AccessTools.Method(typeof(ResourceCounter), "UpdateResourceCounts");
            if (counter == null)
            {
                Log.Error("[数字存储补丁] 找不到 ResourceCounter.UpdateResourceCounts，"
                          + "被投影核心的内容不会计入本图资源统计。");
            }
            else
            {
                harmony.Patch(counter, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_ProjectedContentsVisibility), nameof(Postfix_Counter))));
                ok++;
            }

            MethodInfo trade = AccessTools.Method(typeof(TradeUtility), "AllLaunchableThingsForTrade");
            if (trade == null)
            {
                Log.Error("[数字存储补丁] 找不到 TradeUtility.AllLaunchableThingsForTrade，"
                          + "被投影核心的内容不会出现在本图交易列表。");
            }
            else
            {
                harmony.Patch(trade, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_ProjectedContentsVisibility), nameof(Postfix_Trade))));
                ok++;
            }

            if (ok == 2)
            {
                Log.Message("[数字存储补丁] 投影库存可见性已挂载：资源计数、交易可售列表。");
            }
            else
            {
                Log.Warning("[数字存储补丁] 投影库存可见性只挂上 " + ok + "/2。");
            }
        }

        private static readonly List<Building_StorageCore> tmpCores = new List<Building_StorageCore>();

        /// <summary>与原 mod 的 Patch_ResourceCounter_HaulSources 同款签名（字段名取自该补丁）。</summary>
        private static void Postfix_Counter(Map ___map, Dictionary<ThingDef, int> ___countedAmounts)
        {
            if (___map == null || ___countedAmounts == null)
            {
                return;
            }
            tmpCores.Clear();
            ProjectionHelper.AppendProjectedCores(___map, tmpCores);
            for (int i = 0; i < tmpCores.Count; i++)
            {
                ThingOwner held = tmpCores[i]?.GetDirectlyHeldThings();
                if (held == null)
                {
                    continue;
                }
                for (int j = 0; j < held.Count; j++)
                {
                    Thing thing = held[j];
                    if (thing?.def == null)
                    {
                        continue;
                    }
                    Thing inner = thing.GetInnerIfMinified();
                    if (inner?.def == null || !inner.def.CountAsResource || !ShouldCount(inner))
                    {
                        continue;
                    }
                    ___countedAmounts.TryGetValue(inner.def, out int cur);
                    ___countedAmounts[inner.def] = cur + inner.stackCount;
                }
            }
            tmpCores.Clear();
        }

        private static bool ShouldCount(Thing t)
        {
            if (t.IsNotFresh())
            {
                return false;
            }
            if (t.SpawnedOrAnyParentSpawned && t.PositionHeld.Fogged(t.MapHeld))
            {
                return false;
            }
            return true;
        }

        /// <summary>把被投影核心里的可售物品追加进交易列表（原版内容物原样保留）。</summary>
        private static IEnumerable<Thing> Postfix_Trade(IEnumerable<Thing> __result, Map map, ITrader trader)
        {
            if (__result != null)
            {
                foreach (Thing t in __result)
                {
                    yield return t;
                }
            }
            if (map == null)
            {
                yield break;
            }

            List<Building_StorageCore> cores = new List<Building_StorageCore>();
            try
            {
                ProjectionHelper.AppendProjectedCores(map, cores);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 追加投影库存到交易列表时异常（已降级为只显示原版内容）："
                              + ex, 382233);
                yield break;
            }

            for (int i = 0; i < cores.Count; i++)
            {
                ThingOwner held = cores[i]?.GetDirectlyHeldThings();
                if (held == null)
                {
                    continue;
                }
                for (int j = 0; j < held.Count; j++)
                {
                    Thing thing = held[j];
                    if (thing == null || thing.def == null || thing.Destroyed || thing is Pawn)
                    {
                        continue;
                    }
                    if (TradeUtility.PlayerSellableNow(thing, trader))
                    {
                        yield return thing;
                    }
                }
            }
        }
    }
}
