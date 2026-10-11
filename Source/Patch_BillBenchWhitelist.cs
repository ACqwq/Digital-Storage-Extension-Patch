using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理缺陷修复 ①：只认原版工作台，mod 工作台（含厨房/炉灶）一律"识别不到"。
    ///
    /// <para><b>根因</b>：原 mod 取活时遍历所有 <c>WorkGiver_DoBill</c> 系 WorkGiverDef 逐个试
    /// <c>JobOnThing</c>，但循环里有一道**精确 defName 白名单**：</para>
    /// <code>
    /// // DigitalStorage.AI.BillProbe.TryAcquire
    /// if (workGiverDef.fixedBillGiverDefs != null &amp;&amp; !workGiverDef.fixedBillGiverDefs.Contains(bench.def))
    ///     continue;   // ← mod 台子在这里被全部跳过
    /// </code>
    /// <para>而原版 Core 的 DoBill 系 giver 几乎全部带显式白名单（19 个里只有 3 个"通用"，
    /// 且那 3 个都是手术 giver、又被 <c>CraftUnlocks.IsAutomationCandidate</c> 排除）：
    /// DoBillsCook 只认 ElectricStove/FueledStove、DoBillsBrew 只认 Brewery……
    /// ⇒ 任何 mod 添加的工作台 defName 都不在其中，**所有 giver 全被跳过** ⇒ 拿不到活
    /// ⇒ 显示缺料/堵塞且永不执行。而配方菜单是按 <c>benchDef.AllRecipes</c> 生成的、
    /// 不看 giver ⇒ 表现为"菜单里能选、选了不干活"。</para>
    ///
    /// <para><b>修法</b>：Transpiler 把该处 <c>List&lt;ThingDef&gt;.Contains</c> 替换为
    /// <see cref="ContainsOrRelaxed"/> —— 开关开启时恒真（原式 <c>!Contains</c> 恒假
    /// ⇒ 永不因白名单跳过）；关闭时逐位等价原语义。</para>
    ///
    /// <para><b>为什么不改 <c>WorkGiverDef.fixedBillGiverDefs</c> 字段本身</b>：
    /// 原版真小人的 <c>WorkGiver_DoBill.PotentialWorkThingsGlobal</c> 也读它，
    /// 改数据会连带破坏普通殖民者的做账单流程。只改原 mod 自己的取活逻辑才安全。</para>
    ///
    /// <para><b>放行后的安全性</b>：多试几个 giver 无副作用 —— <c>JobOnThing</c> 内部的
    /// <c>ThingIsUsableBillGiver</c> / <c>BillStack.AnyShouldDoNow</c> /
    /// <c>recipe.requiredGiverWorkType != def.workType</c> / 技能要求 / 找料 等判定会自行过滤，
    /// 不匹配只是返回 null。取活本身是低频操作（每 tick 最多 <c>maxProbesPerTick</c> 条），
    /// 多几次调用可忽略。</para>
    /// </summary>
    internal static class Patch_BillBenchWhitelist
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.billbench";

        private static readonly MethodInfo ReplaceContains = AccessTools.Method(
            typeof(Patch_BillBenchWhitelist), nameof(ContainsOrRelaxed));

        private static readonly FieldInfo FixedBillGiverDefsField =
            AccessTools.Field(typeof(WorkGiverDef), "fixedBillGiverDefs");

        /// <summary>
        /// 被 Transpiler 替换进原 IL 的替身方法。签名与原
        /// <c>List&lt;ThingDef&gt;.Contains(ThingDef)</c> 的**栈形态一致**
        /// （实例方法 [list, arg] ≡ 静态方法 [arg0, arg1]），因此可以直接换调用指令。
        ///
        /// 原逻辑是 <c>!list.Contains(x)</c> 时才 continue ⇒ 这里恒返回 true 即可让判定恒假、
        /// 永不由白名单跳过。<c>list == null</c> 的情况上游的 <c>!= null</c> 判定早已短路，
        /// 不会执行到这里（这里仍做判空只是防御）。
        /// </summary>
        public static bool ContainsOrRelaxed(List<ThingDef> list, ThingDef benchDef)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s != null && s.craftAnyBench)
            {
                return true;
            }

            return list != null && benchDef != null && list.Contains(benchDef);
        }

        public static void Install()
        {
            try
            {
                if (ReplaceContains == null)
                {
                    Log.Error("[数字存储补丁] 制作代理台子放宽：找不到替身方法，本项不可用。");
                    return;
                }

                Type probeType = AccessTools.TypeByName("DigitalStorage.AI.BillProbe");
                if (probeType == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理台子放宽：找不到 DigitalStorage.AI.BillProbe"
                                + "（原 mod 可能已改结构），本项跳过。");
                    return;
                }

                MethodInfo tryAcquire = AccessTools.Method(probeType, "TryAcquire");
                if (tryAcquire == null)
                {
                    Log.Warning("[数字存储补丁] 制作代理台子放宽：找不到 BillProbe.TryAcquire，本项跳过。");
                    return;
                }

                new Harmony(HarmonyId).Patch(tryAcquire, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_BillBenchWhitelist), nameof(Transpile))));

                Log.Message("[数字存储补丁] 已挂载：制作代理可识别 mod 工作台"
                            + "（放宽 WorkGiver 的台子白名单；设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理台子放宽挂载失败（该功能保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            int replaced = 0;

            for (int i = 0; i < list.Count; i++)
            {
                MethodInfo mi = list[i].operand as MethodInfo;
                if (list[i].opcode != OpCodes.Callvirt || !IsListOfThingDefContains(mi))
                {
                    continue;
                }

                // 上下文校验：往前找 fixedBillGiverDefs 的字段读取，
                // 确保改的是白名单判定而不是别处的 Contains（防御性，避免误伤）。
                if (!HasFixedBillGiverDefsNearby(list, i))
                {
                    continue;
                }

                list[i] = new CodeInstruction(OpCodes.Call, ReplaceContains);
                replaced++;
            }

            if (replaced == 0)
            {
                // Transpiler 里抛异常会让整个补丁失败，这里只告警并原样返回。
                Log.Warning("[数字存储补丁] 制作代理台子放宽：没有定位到白名单判定"
                            + "（原 mod 可能已改结构），本次未生效。");
            }
            else
            {
                Log.Message("[数字存储补丁] 制作代理台子放宽：已改写 " + replaced + " 处白名单判定。");
            }

            return list;
        }

        private static bool IsListOfThingDefContains(MethodInfo mi)
        {
            if (mi == null || mi.Name != "Contains" || mi.DeclaringType == null)
            {
                return false;
            }

            Type dt = mi.DeclaringType;
            if (!dt.IsGenericType || dt.GetGenericTypeDefinition() != typeof(List<>))
            {
                return false;
            }

            return dt.GetGenericArguments()[0] == typeof(ThingDef);
        }

        private static bool HasFixedBillGiverDefsNearby(List<CodeInstruction> list, int index)
        {
            if (FixedBillGiverDefsField == null)
            {
                // 拿不到字段引用时不做上下文校验，仍按 Contains 形态替换
                // （BillProbe.TryAcquire 里该形态只有白名单一处）。
                return true;
            }

            int from = Math.Max(0, index - 10);
            for (int i = from; i < index; i++)
            {
                if (list[i].opcode == OpCodes.Ldfld && Equals(list[i].operand, FixedBillGiverDefsField))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
