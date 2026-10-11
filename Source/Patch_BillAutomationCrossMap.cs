using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理（<c>CompBillAutomation</c>）取料时，判定"某个核心在这张图上能不能用"。
    /// 由 <see cref="Patch_BillAutomationCrossMap"/> 的 IL 改写调用。
    /// </summary>
    /// <remarks>
    /// <para>原 mod 的三处取料判定都写死了"核心必须在本图"：</para>
    /// <code>
    /// if (!(thing.ParentHolder is Building_StorageCore core) || core.Map != map) → 视为无效
    /// </code>
    /// <para>但原 mod 自己又有 <c>RemoteIngredientProvider</c>：小人做 bill 时会去
    /// <b>其它地图</b>的核心里找料，并且背包 <c>HediffComp_Backpack.TryAbsorb</c> 支持跨图吸收。
    /// 4.0 更新说明也写明「含跨图：别的图上的核心也能供货」。
    /// 也就是说：跨图取料是原 mod 的既有能力，而制作代理这条链被上面那三处判定卡住了。</para>
    ///
    /// <para>本补丁只把"核心必须在本图"放宽为"核心能被这张图用到"，其余逻辑一律不动。</para>
    /// </remarks>
    public static class BillCrossMapAccess
    {
        /// <summary>
        /// 替换 IL 里的 <c>core.Map == map</c>。
        /// 语义：同图完全保持原样；跨图时才按设置决定放不放行。
        /// </summary>
        public static bool IsCoreReachableForBill(Building_StorageCore core, Map map)
        {
            if (core == null || map == null)
            {
                return false;
            }

            // ★ 不能读 core.Map：核心可能停留在一张"已失效的地图"上（地图集合变动后），
            //   那时抛的是 ArgumentOutOfRangeException 而不是返回 null。
            Map coreMap = DsSafe.MapOf(core);
            if (coreMap == map)
            {
                return true; // 同图：与原来完全一致，一行行为都不改
            }
            if (coreMap == null || !core.Spawned || core.Destroyed)
            {
                return false;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s != null && s.billCrossMapAnyCore)
            {
                // 可选：跟随原 mod 自己的跨图口径（任意可用核心），不再要求投影核心。
                return core.IsUsableNow;
            }

            // 默认：只有"本图有投影核心绑定了这个核心"才算可达 —— 由玩家用投影核心显式授权。
            return ProjectionRegistry.IsProjectedTo(map, core);
        }
    }

    /// <summary>
    /// 把制作代理链路上的三处 <c>core.Map != map</c> 判定，改成
    /// <see cref="BillCrossMapAccess.IsCoreReachableForBill"/>。
    /// </summary>
    /// <remarks>
    /// <para>为什么用 IL 改写而不是重写方法：这三处判定都嵌在方法中间，
    /// 用 Prefix/Postfix 都够不到；而整段重写意味着复制原 mod 上百行逻辑，
    /// 原 mod 一更新就会失配。改 IL 只动那 3 条指令。</para>
    ///
    /// <para>三处目标（已用 ILSpy 核对 IL，形态完全一致）：</para>
    /// <list type="bullet">
    ///   <item><c>BillCraftFunnel.TryComplete(…, Map map, …)</c> —— 实际扣料做成品</item>
    ///   <item><c>BillProbe.ExtractIngredients(…, Map map, …)</c> —— 从找到的 job 里取出配料</item>
    ///   <item><c>CompBillAutomation.StillValid(…, Map map)</c> —— 每 tick 复核配料是否还有效</item>
    /// </list>
    ///
    /// <para>原文形态（IL）：</para>
    /// <code>
    /// ldloc.s  core
    /// callvirt instance class Verse.Map Verse.Thing::get_Map()
    /// ldarg    &lt;map&gt;
    /// beq.s    &lt;继续&gt;      // core.Map == map 才继续，否则视为无效
    /// </code>
    /// <para>改成：</para>
    /// <code>
    /// ldloc.s  core
    /// ldarg    &lt;map&gt;
    /// call     bool IsCoreReachableForBill(core, map)
    /// brtrue   &lt;继续&gt;
    /// </code>
    /// </remarks>
    internal static class Patch_BillAutomationCrossMap
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.billcrossmap";

        private static readonly MethodInfo Helper = AccessTools.Method(
            typeof(BillCrossMapAccess), nameof(BillCrossMapAccess.IsCoreReachableForBill));

        /// <summary>三个目标：类型名 / 方法名。全部运行时解析，缺一个都只降级、不报错。</summary>
        private static readonly string[][] Targets =
        {
            new[] { "DigitalStorage.AI.BillCraftFunnel", "TryComplete" },
            new[] { "DigitalStorage.AI.BillProbe", "ExtractIngredients" },
            new[] { "DigitalStorage.Components.CompBillAutomation", "StillValid" },
        };

        public static void Install()
        {
            if (Helper == null)
            {
                Log.Error("[数字存储补丁] 找不到 BillCrossMapAccess.IsCoreReachableForBill，"
                          + "制作代理跨图取料不可用。");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;
            int failed = 0;

            for (int i = 0; i < Targets.Length; i++)
            {
                string typeName = Targets[i][0];
                string methodName = Targets[i][1];
                try
                {
                    Type type = AccessTools.TypeByName(typeName);
                    if (type == null)
                    {
                        Log.Warning("[数字存储补丁] 制作代理跨图：找不到类型 " + typeName
                                    + "（原 mod 可能已改结构），跳过这一处。");
                        failed++;
                        continue;
                    }

                    MethodInfo method = AccessTools.Method(type, methodName);
                    if (method == null)
                    {
                        Log.Warning("[数字存储补丁] 制作代理跨图：找不到方法 " + typeName + "."
                                    + methodName + "（原 mod 可能已改结构），跳过这一处。");
                        failed++;
                        continue;
                    }

                    harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(
                        typeof(Patch_BillAutomationCrossMap), nameof(Transpile))));
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warning("[数字存储补丁] 制作代理跨图：改写 " + typeName + "." + methodName
                                + " 失败（该处保持原行为）：" + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (ok > 0)
            {
                Log.Message("[数字存储补丁] 制作代理 ↔ 投影核心互通已挂载：" + ok + "/" + Targets.Length
                            + " 处。默认只在「本图有投影核心绑定该核心」时跨图取料；"
                            + "可在设置里改成跟随原 mod 的任意跨图核心。");
            }
            else
            {
                Log.Warning("[数字存储补丁] 制作代理 ↔ 投影核心互通未挂载（" + failed
                            + " 处失败），制作代理仍只能使用本图核心。");
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            if (!original.IsStatic)
            {
                // 三个目标都是静态方法；不是的话参数索引对不上，宁可不动。
                Log.Warning("[数字存储补丁] 制作代理跨图：" + original.Name
                            + " 不是静态方法，跳过（保持原行为）。");
                return codes;
            }

            int mapArgIndex = -1;
            ParameterInfo[] ps = original.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(Map))
                {
                    mapArgIndex = i;
                    break;
                }
            }
            if (mapArgIndex < 0)
            {
                Log.Warning("[数字存储补丁] 制作代理跨图：" + original.Name
                            + " 没有 Map 参数，跳过（保持原行为）。");
                return codes;
            }

            int patched = 0;
            for (int i = 0; i + 2 < codes.Count; i++)
            {
                if (!IsThingGetMap(codes[i]))
                {
                    continue;
                }
                if (!IsLdargIndex(codes[i + 1], mapArgIndex))
                {
                    continue;
                }
                if (codes[i + 2].opcode != OpCodes.Beq && codes[i + 2].opcode != OpCodes.Beq_S)
                {
                    continue;
                }
                if (codes[i].labels.Count != 0 || codes[i + 1].labels.Count != 0
                    || codes[i + 2].labels.Count != 0)
                {
                    // 有别的跳转指向这几条指令 —— 不冒险改动。
                    continue;
                }

                object continueLabel = codes[i + 2].operand;
                List<CodeInstruction> replacement = new List<CodeInstruction>
                {
                    new CodeInstruction(codes[i + 1]),              // ldarg <map>
                    new CodeInstruction(OpCodes.Call, Helper),      // bool IsCoreReachableForBill(core, map)
                    new CodeInstruction(OpCodes.Brtrue, continueLabel)
                };

                codes.RemoveRange(i, 3);
                codes.InsertRange(i, replacement);
                patched++;
                i += replacement.Count - 1;
            }

            if (patched != 1)
            {
                // 形态对不上时**原样返回**：最多是功能不生效，绝不会把方法改坏。
                Log.Warning("[数字存储补丁] 制作代理跨图：" + original.Name + " 里匹配到 "
                            + patched + " 处目标指令（期望 1 处），已原样返回不改 IL。");
                return new List<CodeInstruction>(instructions);
            }

            return codes;
        }

        private static bool IsThingGetMap(CodeInstruction c)
        {
            return c.opcode == OpCodes.Callvirt
                   && c.operand is MethodInfo m
                   && m.Name == "get_Map"
                   && m.DeclaringType == typeof(Thing);
        }

        private static bool IsLdargIndex(CodeInstruction c, int index)
        {
            if (c.opcode == OpCodes.Ldarg_0)
            {
                return index == 0;
            }
            if (c.opcode == OpCodes.Ldarg_1)
            {
                return index == 1;
            }
            if (c.opcode == OpCodes.Ldarg_2)
            {
                return index == 2;
            }
            if (c.opcode == OpCodes.Ldarg_3)
            {
                return index == 3;
            }
            if (c.opcode == OpCodes.Ldarg || c.opcode == OpCodes.Ldarg_S)
            {
                return c.operand is int k && k == index;
            }
            return false;
        }
    }
}
