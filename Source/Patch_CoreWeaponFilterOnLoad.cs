using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复：读档时数字核心的「存储筛选」被强制重置（尤其是武器类）。
    ///
    /// <para><b>现象（用户与群友各反馈一例，方向相反却是同一个原因）</b>：</para>
    /// <list type="bullet">
    /// <item>旧存档读档后「武器」类别被**全部取消勾选**（手动勾回、再读档又被取消）；</item>
    /// <item>另一人不想让核心存武器，手动取消后**读档又被自动打开**。</item>
    /// </list>
    ///
    /// <para><b>根因（原 mod 实现缺陷，反编译实锤）</b>：
    /// <c>Building_StorageCore</c> 有一个**没有任何 UI 入口**的持久化字段
    /// <c>allowWeaponsInStorage</c>，并在两处调 <c>ApplyWeaponFilter()</c>：</para>
    /// <code>
    /// private bool allowWeaponsInStorage = true;                       // ① 字段声明默认 true
    /// Scribe_Values.Look(ref allowWeaponsInStorage, "...", false);     // ② 读档默认 false ← 不一致！
    ///
    /// private void ApplyWeaponFilter()
    ///     => storeSettings.filter.SetAllow(ThingCategoryDefOf.Weapons, allowWeaponsInStorage);
    ///
    /// // 调用点 A：GetStoreSettings() 内首次创建 storeSettings 时  —— 合理（新建筑默认值）
    /// // 调用点 B：ExposeData() 的 PostLoadInit 分支里            —— ★ 每次读档都覆盖玩家设置
    /// </code>
    /// <para>⇒ 因为该字段没有任何玩家可操作入口，它的取值只能是
    /// 「新建筑 = true（声明默认）」或「从存档读出 / 无此字段时 = false（Scribe 默认）」，
    /// 而调用点 B 又**每次读档**把武器类别按它强制设置一遍：</para>
    /// <list type="bullet">
    /// <item>字段读到 false（老存档没有该字段）⇒ 读档把武器**全部取消勾选** ⇒ 第一种反馈；</item>
    /// <item>字段读到 true ⇒ 读档把武器**强行打开** ⇒ 第二种反馈。</item>
    /// </list>
    /// <para>两种现象同源：**读档路径用这个死字段覆盖了玩家在筛选界面里的选择**。</para>
    ///
    /// <para><b>修法</b>：只掐掉读档路径的这一次调用（调用点 B），保留调用点 A
    /// （新建筑的默认值仍然照常设置）。用 **Transpiler** 改写 <c>ExposeData</c> 内的
    /// <c>call ApplyWeaponFilter</c> 为目标方法 —— Transpiler 直接改调用点 IL，
    /// **不受 JIT 内联影响**（若改挂 <c>ApplyWeaponFilter</c> 本身，那是个方法体很小的方法，
    /// 有补丁被内联绕过而静默失效的风险，见项目踩坑）。</para>
    ///
    /// <para>目标方法在开关开启时**什么都不做**（保留存档/玩家状态）；关闭时反射调用原方法，
    /// 逐位还原原 mod 行为。默认开。</para>
    /// </summary>
    internal static class Patch_CoreWeaponFilterOnLoad
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.corefilter";

        private static MethodInfo applyWeaponFilter;

        public static void Install()
        {
            try
            {
                Type coreType = typeof(Building_StorageCore);
                MethodInfo exposeData = AccessTools.Method(coreType, "ExposeData");
                applyWeaponFilter = AccessTools.Method(coreType, "ApplyWeaponFilter");
                MethodInfo replacer = AccessTools.Method(
                    typeof(Patch_CoreWeaponFilterOnLoad), nameof(PreserveOrApply));

                if (exposeData == null || applyWeaponFilter == null || replacer == null)
                {
                    Log.Warning("[数字存储补丁] 核心筛选保留：定位不到 ExposeData/ApplyWeaponFilter"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                new Harmony(HarmonyId).Patch(exposeData, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_CoreWeaponFilterOnLoad), nameof(Transpile))));

                Log.Message("[数字存储补丁] 已挂载：读档时保留核心的存储筛选"
                            + "（不再被「允许存武器」字段覆盖；设置 →「存储核心」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 核心筛选保留挂载失败（该行为保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            MethodInfo replacer = AccessTools.Method(
                typeof(Patch_CoreWeaponFilterOnLoad), nameof(PreserveOrApply));
            int replaced = 0;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Call && Equals(list[i].operand, applyWeaponFilter))
                {
                    // 原调用栈形态为 [this] + call ApplyWeaponFilter()（void、无参）
                    // ⇒ 换成同栈形态的静态方法 [this] + call PreserveOrApply(this)。
                    list[i] = new CodeInstruction(OpCodes.Call, replacer);
                    replaced++;
                }
            }

            if (replaced == 0)
            {
                Log.Warning("[数字存储补丁] 核心筛选保留：ExposeData 里没有找到 ApplyWeaponFilter 调用"
                            + "（原 mod 可能已改结构），本次未生效。");
            }
            else
            {
                Log.Message("[数字存储补丁] 核心筛选保留：已改写 ExposeData 内 " + replaced
                            + " 处武器筛选调用（读档不再覆盖玩家设置）。");
            }

            return list;
        }

        /// <summary>
        /// 替换进 <c>ExposeData</c> 的调用点。开关开启 = 读档路径不重置筛选；
        /// 关闭 = 反射调用原 <c>ApplyWeaponFilter</c>，完全还原原 mod 行为。
        /// </summary>
        public static void PreserveOrApply(Building_StorageCore self)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s != null && s.keepCoreFilterOnLoad)
            {
                return;      // 保留存档里玩家设置的筛选（含武器类别）
            }

            try
            {
                applyWeaponFilter?.Invoke(self, null);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 还原原武器筛选行为失败："
                              + ex.GetType().Name + ": " + ex.Message, 8830131);
            }
        }
    }
}
