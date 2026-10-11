using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 建造取料修复 **H4（方案 A）**：修掉「从核心搬料」时的一处**记账 bug**（纯修复，默认开）。
    ///
    /// <para><b>根因（报告 FIX-10 §11，代码 + 探针实证）</b>：原 mod 的
    /// <c>DigitalTask_Construct.TransferFromCores(Map, ThingDef, int count, ThingOwner dest)</c> 这样记账：</para>
    /// <code>
    /// int num3 = directlyHeldThings.TryTransferToContainer(thing, dest, Mathf.Min(num, thing.stackCount));
    /// if (num3 &gt; 0) { num -= num3; }
    /// </code>
    /// <para>而原版 <c>ThingOwner.TryTransferToContainer</c> 在"**合堆吸收**"这条路径上的返回值是
    /// <c>thing.stackCount</c> —— 当目标容器里已有同类堆、且把这一份**整个吸收**进去时，该值就是 <b>0</b>。
    /// ⇒ <c>num</c> 永远减不到 0 ⇒ <c>while (num2 &gt;= 0 &amp;&amp; num &gt; 0)</c> 变成**无界循环**
    /// ⇒ 把**核心里所有该材料的堆**（乃至其它核心里的）逐一搬进目标在建框架容器
    /// （探针现场：一次取料调用产生 15 次写入；"需要 20 木"的框架被塞到 <c>WoodLog×75 + ×75 + ×10</c>）。
    /// 建成时 <c>Frame.CompleteConstruction → ClearAndDestroyContents()</c> **全量销毁** ⇒ 材料凭空少 + 工程断料。</para>
    ///
    /// <para><b>修法</b>：Prefix **替换**该方法，逻辑与原来完全一致（遍历核心 → 每堆搬
    /// <c>min(剩余, 堆量)</c> ⇒ 直到搬够 <c>count</c>），**只把记账改成"按目标容器增量"**：
    /// 调 <c>TryTransferToContainer</c> 前后各取一次 <c>dest.TotalStackCountOfDef(def)</c>，用**差值**扣剩余量；
    /// 若差值 ≤ 0（真的没搬动）就 <c>break</c>，**从根本上杜绝自旋**。</para>
    ///
    /// <para><b>边界</b>：任何异常都交回原实现（绝不因为本补丁让建造取料失败）；只用 vanilla API
    /// 与原 mod 的 <b>public</b> 工具 <c>HaulSourceContents.CoreSources</c>。</para>
    /// </summary>
    internal static class Patch_TransferFromCoresFix
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.transferfromcores";

        public static void Install()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(DigitalTask_Construct), "TransferFromCores");
                if (target == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 DigitalTask_Construct.TransferFromCores"
                                + "（原 mod 可能已改结构），「建造取料记账修复」不可用（其余功能不受影响）。");
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    target,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_TransferFromCoresFix), nameof(Prefix))));

                Log.Message("[数字存储补丁] 已挂载：建造取料的「搬运记账」修复"
                            + "（不再把核心里同种材料成堆搬进在建工地；默认开启，设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 建造取料记账修复挂载失败（该问题保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 返回 <c>false</c> = 跳过原实现（用我们这版记账）。
        /// 关开关 / 参数为空 / 出错时返回 <c>true</c>（完全交回原 mod）。
        /// </summary>
        private static bool Prefix(Map map, ThingDef def, int count, ThingOwner dest)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.constructPickupAmountFix)
            {
                return true;
            }

            try
            {
                // 与原实现等价的空操作（原实现此时也不会做任何事）
                if (map == null || def == null || dest == null || count <= 0)
                {
                    return false;
                }

                int remaining = count;
                List<IHaulSource> cores = HaulSourceContents.CoreSources(map);
                if (cores == null)
                {
                    return false;
                }

                for (int i = 0; i < cores.Count && remaining > 0; i++)
                {
                    ThingOwner src = cores[i]?.GetDirectlyHeldThings();
                    if (src == null)
                    {
                        continue;
                    }

                    // 与原实现一样：从后往前扫（搬运会改动 src，倒序扫描才安全）
                    for (int j = src.Count - 1; j >= 0 && remaining > 0; j--)
                    {
                        Thing t = src[j];
                        if (t == null || t.Destroyed || t.def != def)
                        {
                            continue;
                        }

                        int before = dest.TotalStackCountOfDef(def);
                        int moved = src.TryTransferToContainer(t, dest, Math.Min(remaining, t.stackCount));
                        int delta = dest.TotalStackCountOfDef(def) - before;

                        // ★ 关键：以"目标容器实际多了多少"为准（返回值在合堆时会是 0，不可用）
                        int accounted = delta > 0 ? delta : moved;
                        if (accounted > 0)
                        {
                            remaining -= accounted;
                        }
                        else
                        {
                            break;   // 真的一件也没搬动 ⇒ 别空转
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 建造取料记账修复异常（本条交回原实现）："
                              + ex.GetType().Name + ": " + ex.Message, 771601);
                return true;
            }
        }
    }
}
