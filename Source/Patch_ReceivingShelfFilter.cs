using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让数字收货架的「可存放物品」跟着数字核心走。
    ///
    /// <c>Building_Storage.GetStoreSettings()/Accepts()</c> 都是**非 virtual**（已核实），
    /// 无法 override，所以只能在核心的筛选**被改动**时把新筛选推给收货架。
    ///
    /// 挂点选 <c>Building_StorageCore.Notify_SettingsChanged()</c> 的原因：
    /// <c>StorageSettings</c> 构造时把自己的 filter 回调接成 <c>owner.Notify_SettingsChanged()</c>
    /// （反编译 <c>StorageSettings.TryNotifyChanged</c> 已核实），
    /// 所以玩家在核心上改筛选**必然**经过这里，是天然的"筛选已变"事件。
    ///
    /// 代价：本方法每次调用会遍历本图的殖民者建筑列表找收货架。
    /// 但它只在**筛选/优先级真的被改**时触发（不是每 tick），而 <c>listerBuildings.allBuildingsColonist</c>
    /// 本身就是一个常驻 List，遍历只是读一遍引用，代价可忽略。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_ReceivingShelfFilter
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.shelffilter";

        static Patch_ReceivingShelfFilter()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Building_StorageCore), "Notify_SettingsChanged");
                if (target == null)
                {
                    Log.Error("[数字存储补丁] 找不到 Building_StorageCore.Notify_SettingsChanged，"
                              + "数字收货架的「可存放物品」不会跟随核心（建成时仍会同步一次）。");
                    return;
                }
                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_ReceivingShelfFilter), nameof(Postfix))));
                Log.Message("[数字存储补丁] 已挂载：数字收货架「可存放物品」跟随核心筛选。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 收货架筛选同步补丁安装失败："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Postfix(Building_StorageCore __instance)
        {
            if (__instance == null)
            {
                return;
            }
            Map map = __instance.Map;
            if (map == null)
            {
                return;
            }
            List<Building> buildings = map.listerBuildings != null
                ? map.listerBuildings.allBuildingsColonist
                : null;
            if (buildings == null)
            {
                return;
            }

            try
            {
                for (int i = 0; i < buildings.Count; i++)
                {
                    if (!(buildings[i] is Building_ReceivingShelf shelf))
                    {
                        continue;
                    }
                    // 手动绑定的：只跟自己的核心走；自动模式的：跟任何核心的变化走。
                    if (shelf.boundCore != null && shelf.boundCore != __instance)
                    {
                        continue;
                    }
                    shelf.SyncFilterFromCore();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 同步收货架筛选时出错（核心筛选本身不受影响）："
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
