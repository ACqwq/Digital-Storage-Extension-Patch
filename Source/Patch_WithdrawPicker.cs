using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Core;
using DigitalStorage.UI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 「从数字存储取出…」入口替换（v0.21）。
    ///
    /// ── 现状链路（反编译核实）──
    /// 原 mod 的 <c>FloatMenuOptionProvider_DS_Withdraw.GetSingleOption</c> 生成一级菜单项；
    /// 点击后在闭包里现拼一个**扁平二级 FloatMenu**（按 def 标签排序、无图标/分组/搜索），
    /// 再进 <c>Dialog_WithdrawAmount</c> 数量弹窗下单。
    ///
    /// ── 做法 ──
    /// 只挂**一个** Prefix：设置「增强取料面板」开启时，用 <c>ref __result</c> 把一级菜单项
    /// 替换成打开 <see cref="Window_WithdrawPicker"/> 的同文案项；关闭时不干预（返回 true 走原版）。
    /// 不碰二级菜单、数量弹窗与 Job 链路的任何代码 —— 下单在窗口里逐行复用原版调用。
    ///
    /// ── 为什么安全 ──
    /// 挂点是 protected override 方法，Harmony 可正常补；该方法只被原版
    /// <c>FloatMenuMakerMap.GetProviderOptions</c> 调用（全局唯一调用点），
    /// 方法体大（不会内联），且替换后玩家仍可用设置一键回退原版菜单。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_WithdrawPicker
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.withdrawpicker";

        static Patch_WithdrawPicker()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 增强取料面板安装失败（右键菜单保持原 mod 行为）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Install()
        {
            MethodInfo target = AccessTools.Method(
                typeof(FloatMenuOptionProvider_DS_Withdraw), "GetSingleOption");
            if (target == null)
            {
                Log.Error("[数字存储补丁] 找不到原 mod 的取料菜单入口"
                          + "（FloatMenuOptionProvider_DS_Withdraw.GetSingleOption）。"
                          + "增强面板不可用，原版菜单不受影响。");
                return;
            }
            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(target, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(Patch_WithdrawPicker), nameof(Prefix_GetSingleOption))));
            Log.Message("[数字存储补丁] 增强取料面板已挂载"
                        + "（设置 →「界面」可关闭并回退原版菜单）。");
        }

        /// <summary>
        /// 返回 false = 跳过原方法、用我们的菜单项；返回 true = 完全走原版。
        /// 门槛判定与原版逐条一致（选中小人、地图、全局可用核心、至少一种物品可取）。
        /// </summary>
        private static bool Prefix_GetSingleOption(FloatMenuContext context, ref FloatMenuOption __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.withdrawPickerEnabled)
            {
                return true;
            }
            __result = BuildOption(context);
            return false;
        }

        private static FloatMenuOption BuildOption(FloatMenuContext context)
        {
            if (context == null)
            {
                return null;
            }
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || pawn.Map == null)
            {
                return null;
            }
            if (!CoreFinder.AnyUsableCoreGlobal(pawn))
            {
                return null;
            }
            // 与原版同一门槛：聚合后没有任何可取物品就不显示菜单项。
            List<Thing> buffer = new List<Thing>();
            HaulSourceContents.GatherAllIncludingRemote(pawn.Map, buffer);
            bool any = false;
            for (int i = 0; i < buffer.Count; i++)
            {
                Thing t = buffer[i];
                if (t != null && !t.Destroyed && t.def != null
                    && t.def.category == ThingCategory.Item)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return null;
            }

            Pawn captured = pawn;
            IntVec3 cell = context.ClickedCell;
            // 与原版同一优先级：原 mod 传的是 (MenuOptionPriority)4，
            // 反编译枚举核实 4 == MenuOptionPriority.Default。
            return new FloatMenuOption(
                "DS_WithdrawToSpot".Translate(),
                delegate { Find.WindowStack.Add(new Window_WithdrawPicker(captured, cell)); },
                MenuOptionPriority.Default);
        }
    }
}
