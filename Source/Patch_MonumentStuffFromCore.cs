using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让「纪念碑蓝图」的材料菜单也认数字核心里的材料（v0.46，N4）。
    ///
    /// ── 现象（用户实测）──
    /// 放置纪念碑蓝图时，材料**全存数字核心**（地图上一点都没有）⇒
    /// 菜单**把所有允许的材料全部列出**（而不是只列"实际有的"）；
    /// 只有当材料真的出现在地图上之后，菜单才恢复成"只列已有的"。
    ///
    /// ── 根因（反编译取证，<c>RimWorld/MonumentMarker.cs</c>）──
    /// <code>
    /// private void ListFloatMenuOptions(IEnumerable&lt;ThingDef&gt; allowedStuff, Action&lt;ThingDef&gt; action)
    /// {
    ///     bool flag = false;
    ///     foreach (ThingDef item in allowedStuff)
    ///         if (base.Map.listerThings.ThingsOfDef(item).Count &gt; 0) { flag = true; break; }
    ///     foreach (ThingDef item2 in allowedStuff)
    ///         if (!flag || base.Map.listerThings.ThingsOfDef(item2).Count != 0)   // ★ 只数地图上已生成的
    ///             list.Add(new FloatMenuOption(...));
    ///     Find.WindowStack.Add(new FloatMenu(list));
    /// }
    /// </code>
    /// "有没有这种材料"只看 <c>listerThings</c>（只索引已生成的 Thing）⇒ 核心里的不算。
    /// 于是 <c>flag</c> 恒为 false ⇒ 落入"全列"分支（玩家的观感：菜单不对）。
    ///
    /// ── 为什么必须 Transpiler ──
    /// 判定与菜单构建**都在同一个方法体内**，Postfix 拿不到那个 <c>list</c>，
    /// 也无法改变已经做出的分支 ⇒ 只能改 IL。
    /// 这里把 <c>ListerThings.ThingsOfDef(ThingDef)</c> 整体替换成本文件的
    /// <see cref="ThingsOfDefWithCore"/>（同签名、返回 <c>List&lt;Thing&gt;</c>），
    /// 让"核心里有"也能让 <c>.Count &gt; 0</c> 成立 —— **原版的两处判断逻辑一字不改**。
    ///
    /// ── 只读安全 ──
    /// 替换方法**只把核心里的那件物品放进返回的列表里用于计数**，
    /// **不从核心取出、不落到地上、不产生任何副作用**（见 <c>CoreContentAccess.FindOne</c>）。
    /// 玩家真正选中材料后的建造/取料由原版与既有补丁负责，本项只修菜单。
    ///
    /// 关闭 = 完全回到原版行为（全列）。
    /// </summary>
    internal static class Patch_MonumentStuffFromCore
    {
        internal static int FixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.monumentStuffFromCore;
        }

        /// <summary>空列表：原版 <c>ThingsOfDef</c> 找不到时会返回共享的空表，这里保持一致。</summary>
        private static readonly List<Thing> EmptyList = new List<Thing>(0);

        /// <summary>
        /// Transpiler 的替换目标：签名与原版 <c>ListerThings.ThingsOfDef(ThingDef)</c> 一致
        /// （实例 ⇒ 静态扩展形态：接收 <c>ListerThings</c> 自身），返回 <c>List&lt;Thing&gt;</c>。
        /// </summary>
        private static List<Thing> ThingsOfDefWithCore(ListerThings lister, ThingDef def)
        {
            List<Thing> mapThings = lister?.ThingsOfDef(def);
            if (!Enabled())
            {
                return mapThings ?? EmptyList;
            }

            Map map = MapOf(lister);
            if (map == null || def == null)
            {
                return mapThings ?? EmptyList;
            }

            // 地图上已经有 ⇒ 原版结论本来就是"有"，直接返回原表（零改动）。
            if (mapThings != null && mapThings.Count > 0)
            {
                return mapThings;
            }

            try
            {
                // 只读：只取一个"代表"放进列表，让 .Count 成立；不取出、不落地。
                Thing representative = CoreContentAccess.FindOne(map, t => t.def == def);
                if (representative != null)
                {
                    FixCount++;
                    return new List<Thing>(1) { representative };
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 检查数字核心材料（纪念碑菜单）时出错（本次按未找到处理）："
                              + ex.GetType().Name + ": " + ex.Message, 66510331);
            }

            return mapThings ?? EmptyList;
        }

        /// <summary>
        /// 由 <c>ListerThings</c> 反查所属地图（<c>ListerThings</c> 本身不暴露 Map）。
        /// 先看当前图（菜单就在当前图打开），再遍历 <c>Find.Maps</c> 兜底。
        /// </summary>
        private static Map MapOf(ListerThings lister)
        {
            if (lister == null)
            {
                return null;
            }

            Map current = Find.CurrentMap;
            if (current?.listerThings == lister)
            {
                return current;
            }

            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i]?.listerThings == lister)
                    {
                        return maps[i];
                    }
                }
            }

            return null;
        }

        [HarmonyPatch(typeof(MonumentMarker), "ListFloatMenuOptions")]
        internal static class Patch_ListFloatMenuOptions
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo target = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsOfDef));
                MethodInfo replacement = AccessTools.Method(
                    typeof(Patch_MonumentStuffFromCore), nameof(ThingsOfDefWithCore));

                int replaced = 0;
                foreach (CodeInstruction ci in instructions)
                {
                    if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call)
                        && ci.operand is MethodInfo m
                        && m.DeclaringType == typeof(ListerThings)
                        && m.Name == nameof(ListerThings.ThingsOfDef))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = replacement;
                        replaced++;
                    }
                    yield return ci;
                }

                if (replaced == 0)
                {
                    // 本项目铁律：挂点没打到要能查得到 —— 反编译核对时会看这行。
                    Log.Warning("[数字存储补丁] 纪念碑材料菜单：未找到 ListerThings.ThingsOfDef 调用点"
                                + "（原版可能已改结构），本项不会生效。");
                }
            }
        }
    }
}
