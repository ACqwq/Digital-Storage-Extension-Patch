using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理缺陷修复 ⑧（FIX-11）：同一配方无法在一个代理里建立多个任务（如「切除石砖」）。
    ///
    /// <para><b>根因（反编译实锤）</b>：「一个配方 = 一个计划」
    /// 是原 mod 硬编码的唯一性约束，共两道锁：</para>
    /// <list type="number">
    /// <item><b>数据层</b>：<c>CompBillAutomation.AddPlan</c> 有
    /// <c>if (recipe != null &amp;&amp; !HasPlan(recipe))</c> 守卫 —— 同一 <c>RecipeDef</c>
    /// 在一个代理里只能存在一个 <c>CraftPlan</c>。</item>
    /// <item><b>UI 层</b>：挑配方面板 <c>Dialog_DS_AddCraft.DoWindowContents</c> 对已添加的
    /// 配方置灰并显示「已添加」，且点击处理是 <c>if (!flag &amp;&amp; Widgets.ButtonInvisible(rect4))</c>
    /// —— 已添加的行点击无效。<c>flag</c> 即 <c>HasPlan(recipe)</c> 的结果。</item>
    /// </list>
    /// <para>「切石砖」在原版是**单一 RecipeDef**（MakeStoneBlocks）⇒ 添加过一次后，
    /// 该代理永远无法再添加第二个切石砖任务。</para>
    ///
    /// <para><b>修法</b>（两部分，同一开关 <c>craftAddDuplicate</c> 控制）：</para>
    /// <list type="number">
    /// <item>Prefix 替换 <c>CompBillAutomation.AddPlan</c>：去掉 <c>HasPlan</c> 守卫，
    /// 直接 <c>plans.Add(new CraftPlan { recipe = recipe, mode = 0 })</c>。
    /// 计划列表 <c>List&lt;CraftPlan&gt;</c> 深存档天然支持重复项；主窗口每个计划独立一行、
    /// 独立控制（已核实 <c>Window_CraftAutomation</c> 按列表下标渲染，无"按配方去重"假设）。</item>
    /// <item>Transpiler 改 <c>Dialog_DS_AddCraft.DoWindowContents</c>：只动点击门那一处 ——
    /// 在 <c>ldloc flag; brtrue …</c>（flag 为真则跳过点击体）的 <c>ldloc</c> 之后插入一个
    /// 静态变换调用 <see cref="TransformGate"/>：开关开 → 恒返回 false（门恒可进）；
    /// 开关关 → 原值原样传回（逐位等价原行为）。「已添加」角标、置灰显示等其余 UI 逻辑不动。</item>
    /// </list>
    ///
    /// <para><b>匹配失败降级</b>：Transpiler 定位不到 <c>HasPlan</c> 存值 → 点击门 →
    /// <c>ButtonInvisible</c> 的三点链路，或匹配数 ≠ 1 时，**原样返回未改写的 IL** 并打告警日志
    /// （表现为原 mod 行为：不能重复添加），不会崩游戏。</para>
    ///
    /// <para><b>已知限制</b>：重复计划共享同一配方 —— 两个「切石砖」计划无法指定用不同石种
    /// （原 mod 的计划数据模型没有原料过滤；如需按石种拆分，另立项调查）。</para>
    /// </summary>
    internal static class Patch_CraftAddDuplicate
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.craftadddup";

        private static FieldInfo PlansField;            // CompBillAutomation.plans (private)
        private static Type CraftPlanType;              // DigitalStorage.AI.CraftPlan (internal sealed)
        private static FieldInfo PlanRecipeField;       // CraftPlan.recipe (public)
        private static FieldInfo PlanModeField;         // CraftPlan.mode (public)
        private static MethodInfo HasPlanMethod;        // CompBillAutomation.HasPlan(RecipeDef) (internal)
        private static MethodInfo ButtonInvisibleMethod; // Widgets.ButtonInvisible(Rect, bool)

        /// <summary>
        /// 被 Transpiler 插入点击门的替身变换：输入原 <c>flag</c>（是否已添加），
        /// 输出交给 <c>brtrue</c> 的值。
        ///
        /// <para>原 IL：<c>ldloc flag; brtrue SKIP</c> —— flag 为真（已添加）则跳过点击体。
        /// 本方法返回 false 时 <c>brtrue</c> 不跳 ⇒ 点击体可达。</para>
        ///
        /// <para>开关开 → 恒返回 false（已添加的行也可点击追加）；
        /// 开关关 → 原值传回（逐位等价原行为）。</para>
        /// </summary>
        public static bool TransformGate(bool alreadyAdded)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s != null && s.craftAddDuplicate)
            {
                return false;
            }

            return alreadyAdded;
        }

        public static void Install()
        {
            try
            {
                Type compType = typeof(CompBillAutomation);
                CraftPlanType = AccessTools.TypeByName("DigitalStorage.AI.CraftPlan");
                if (CraftPlanType == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理重复任务：找不到 CraftPlan"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                PlansField = AccessTools.Field(compType, "plans");
                PlanRecipeField = AccessTools.Field(CraftPlanType, "recipe");
                PlanModeField = AccessTools.Field(CraftPlanType, "mode");
                HasPlanMethod = AccessTools.Method(compType, "HasPlan");
                ButtonInvisibleMethod = AccessTools.Method(
                    typeof(Widgets), nameof(Widgets.ButtonInvisible));

                if (PlansField == null || PlanRecipeField == null || PlanModeField == null
                    || HasPlanMethod == null || ButtonInvisibleMethod == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理重复任务：成员定位不完整"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);

                // ① 数据层：AddPlan 去掉唯一性守卫
                MethodInfo addPlan = AccessTools.Method(compType, "AddPlan");
                if (addPlan == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理重复任务：找不到 CompBillAutomation.AddPlan，"
                                + "数据层跳过（仅剩 UI 放行，点击将无效）。");
                }
                else
                {
                    harmony.Patch(addPlan, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_CraftAddDuplicate), nameof(Prefix_AddPlan))));
                }

                // ② UI 层：挑配方面板放行已添加的行
                Type dialogType = AccessTools.TypeByName("DigitalStorage.UI.Dialog_DS_AddCraft");
                MethodInfo doWindow = dialogType == null
                    ? null
                    : AccessTools.Method(dialogType, "DoWindowContents");
                if (doWindow == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理重复任务：找不到 Dialog_DS_AddCraft.DoWindowContents，"
                                + "UI 层跳过（可改用主窗口等途径建重复计划 —— 若未来有的话）。");
                }
                else
                {
                    harmony.Patch(doWindow, transpiler: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_CraftAddDuplicate), nameof(Transpile))));
                }

                Log.Message("[数字存储补丁] 已挂载：制作代理允许同一配方重复添加任务"
                            + "（设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理重复任务挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Prefix 替换 <c>AddPlan</c>（开关开时 return false 跳过原方法）：
        /// 复刻原逻辑但去掉 <c>!HasPlan(recipe)</c> 守卫。
        /// 原方法体仅 <c>if (recipe != null &amp;&amp; !HasPlan(recipe)) { plans.Add(new CraftPlan{recipe, mode=0}); }</c>。
        /// </summary>
        private static bool Prefix_AddPlan(CompBillAutomation __instance, RecipeDef recipe)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftAddDuplicate)
            {
                return true;    // 开关关：原方法自带守卫
            }

            if (recipe == null)
            {
                return false;   // 与原方法等价：null 直接不做
            }

            IList plans = PlansField.GetValue(__instance) as IList;
            if (plans == null)
            {
                return true;    // 反射异常：退回原方法（有守卫，安全）
            }

            try
            {
                object plan = Activator.CreateInstance(CraftPlanType);
                PlanRecipeField.SetValue(plan, recipe);
                PlanModeField.SetValue(plan, 0);
                plans.Add(plan);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作代理重复任务：创建计划失败（本条跳过）："
                              + ex.GetType().Name + ": " + ex.Message, 8842117);
            }

            return false;
        }

        /// <summary>
        /// Transpiler：在 <c>HasPlan</c> 结果（局部变量 flag）进入「点击门」
        /// <c>brtrue</c> 之前插入 <see cref="TransformGate"/> 变换。
        ///
        /// <para>定位链路（全部满足才改写）：</para>
        /// <list type="number">
        /// <item><c>callvirt HasPlan</c> 后紧跟 <c>stloc L</c>（flag 存入 L）；</item>
        /// <item>在 flag 的使用点中找 <c>ldloc L; brtrue TGT</c>，
        /// 且 <c>[当前位置, TGT)</c> 区间内存在 <c>ButtonInvisible</c> 调用 ——
        /// 这是点击门独有的形态（「已添加」角标的三元/或运算分支里没有 ButtonInvisible）；</item>
        /// <item>整个方法恰好匹配一处。</item>
        /// </list>
        /// 不满足 ⇒ 原样返回 IL + 告警日志（降级为原 mod 行为）。
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();

            // ── 预备：label → 指令下标映射（解析 brtrue 的跳转目标）──
            Dictionary<Label, int> labelIndex = new Dictionary<Label, int>();
            for (int i = 0; i < list.Count; i++)
            {
                foreach (object labelObj in list[i].labels)
                {
                    if (labelObj is Label label)
                    {
                        labelIndex[label] = i;
                    }
                }
            }

            // ── 第 1 步：HasPlan → stloc L ──
            int flagLocal;
            int hasPlanIdx = FindHasPlanStore(list, out flagLocal);
            if (hasPlanIdx < 0)
            {
                Log.Warning("[数字存储补丁] 制作代理重复任务：IL 里没有定位到 HasPlan 的存值点"
                            + "（原 mod 可能已改结构），UI 放行未生效（原行为）。");
                return list;
            }

            // ── 第 2 步：ButtonInvisible 调用点集合 ──
            HashSet<int> buttonCalls = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Call && Equals(list[i].operand, ButtonInvisibleMethod))
                {
                    buttonCalls.Add(i);
                }
            }

            if (buttonCalls.Count == 0)
            {
                Log.Warning("[数字存储补丁] 制作代理重复任务：IL 里没有定位到 ButtonInvisible，"
                            + "UI 放行未生效（原行为）。");
                return list;
            }

            // ── 第 3 步：找 ldloc L; brtrue TGT 且 (当前位置, TGT) 间含 ButtonInvisible ──
            int gateIdx = -1;
            for (int k = hasPlanIdx + 2; k < list.Count - 1; k++)
            {
                if (!LoadsLocal(list[k], flagLocal)
                    || !(list[k + 1].opcode == OpCodes.Brtrue || list[k + 1].opcode == OpCodes.Brtrue_S))
                {
                    continue;
                }

                object labelObj2 = list[k + 1].operand;
                if (labelObj2 == null || !(labelObj2 is Label label)
                    || !labelIndex.TryGetValue(label, out int target))
                {
                    continue;
                }

                // brtrue 的跳转目标必须越过某个 ButtonInvisible（说明它是点击门）
                bool guardsButton = buttonCalls.Any(bi => bi > k + 1 && bi < target);
                if (guardsButton)
                {
                    gateIdx = k;
                    break;
                }
            }

            if (gateIdx < 0)
            {
                Log.Warning("[数字存储补丁] 制作代理重复任务：没有定位到点击门（ldloc+brtrue+ButtonInvisible 链路），"
                            + "UI 放行未生效（原行为）。");
                return list;
            }

            // ── 第 4 步：在 ldloc 之后插入变换调用 ──
            list.Insert(gateIdx + 1, new CodeInstruction(
                OpCodes.Call, AccessTools.Method(typeof(Patch_CraftAddDuplicate), nameof(TransformGate))));

            Log.Message("[数字存储补丁] 制作代理重复任务：已放行挑配方面板的「已添加」行（点击可追加）。");
            return list;
        }

        /// <summary>找 <c>callvirt HasPlan</c> 后紧跟的 <c>stloc L</c>，返回下标与局部变量编号。</summary>
        private static int FindHasPlanStore(List<CodeInstruction> list, out int flagLocal)
        {
            flagLocal = -1;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (!(list[i].operand is MethodInfo mi) || mi != HasPlanMethod)
                {
                    continue;
                }

                int? stored = StoreLocalIndex(list[i + 1]);
                if (stored.HasValue)
                {
                    flagLocal = stored.Value;
                    return i;
                }
            }

            return -1;
        }

        private static bool LoadsLocal(CodeInstruction inst, int local)
        {
            int? loaded = LoadLocalIndex(inst);
            return loaded.HasValue && loaded.Value == local;
        }

        // ── 局部变量索引解析（stloc/ldloc 家族；本 Harmony 版本无无参 LoadLocal/StoreLocal 扩展）──

        private static int? StoreLocalIndex(CodeInstruction inst)
        {
            OpCode op = inst.opcode;
            if (op.Equals(OpCodes.Stloc_0)) return 0;
            if (op.Equals(OpCodes.Stloc_1)) return 1;
            if (op.Equals(OpCodes.Stloc_2)) return 2;
            if (op.Equals(OpCodes.Stloc_3)) return 3;
            if (op.Equals(OpCodes.Stloc_S) || op.Equals(OpCodes.Stloc))
            {
                return LocalIndexFromOperand(inst.operand);
            }

            return null;
        }

        private static int? LoadLocalIndex(CodeInstruction inst)
        {
            OpCode op = inst.opcode;
            if (op.Equals(OpCodes.Ldloc_0)) return 0;
            if (op.Equals(OpCodes.Ldloc_1)) return 1;
            if (op.Equals(OpCodes.Ldloc_2)) return 2;
            if (op.Equals(OpCodes.Ldloc_3)) return 3;
            if (op.Equals(OpCodes.Ldloc_S) || op.Equals(OpCodes.Ldloc))
            {
                return LocalIndexFromOperand(inst.operand);
            }

            return null;
        }

        private static int? LocalIndexFromOperand(object operand)
        {
            if (operand is int i)
            {
                return i;
            }

            if (operand is LocalVariableInfo lvi)
            {
                return lvi.LocalIndex;
            }

            return null;
        }
    }
}
