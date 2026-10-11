using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// Better Architect Menu 兼容：让建筑菜单的**地板页 / 默认选材**也认数字核心里的材料（v0.38）。
    ///
    /// ── 根因（报告 33，已用源码 + DLL 双份核实） ──
    /// BAM 把"殖民地有没有这个材料"实现成了"**地图上有没有已生成**的这个物品"：
    ///
    ///   `PopulateMaterials`（DLL `:1507`，地板按材料分组）
    ///       Find.CurrentMap.listerThings.ThingsOfDef(cost.thingDef).Any()
    ///   `GetStuffFrom`（DLL `:1294`，需材料建筑的默认选材）
    ///       Find.CurrentMap.listerThings.ThingsOfDef(item).Count > 0
    ///
    /// `listerThings` **只含有已生成的物品** ⇒ 材料只在数字核心（未生成、在容器里）时判据恒为假：
    ///   · 地板页：该材料的地板分类**根本不出现** ⇒ 玩家看不到、选不了 ⇒ 建造不了地板（用户反馈的现象）；
    ///   · 建筑默认选材：候选被筛空 ⇒ 返回 null。
    ///
    /// 原 mod 的 `Patch_DesignatorBuild_ProcessInput_Transpiler` 本来就在原版
    /// `Designator_Build.ProcessInput` 里补过同类问题（`get_Count` 前插 `ExtendList`），
    /// **但 BAM 把同一段逻辑自己重写了一遍**，锚点覆盖不到它的副本。
    /// （与报告 32 的 `MapWideTradeBeacon` 同类：第三方 mod 重写原版逻辑，原 mod 的补丁管不到。）
    ///
    /// ── 做法：两个**纯追加**的 Postfix ──
    /// 只把"地图上没有、但核心里有"的材料**补上**，不改 BAM 的任何原有判断。
    /// 用 Postfix 而不是 Transpiler：`ProcessInput` 上已经有原 mod 与 BAM **两个** transpiler，
    /// 再加一个就是三方抢同一方法体、锚点互相影响；Postfix 不碰 IL，只依赖方法名+签名。
    ///
    /// ── 关于参数注入 ──
    /// 挂点是 BAM 的**私有**静态方法，参数含 BAM 自有类型（`MaterialInfo`）。
    /// 我们**没有** BAM 的编译引用（它是可选 mod），所以：
    ///   · 方法只声明我们关心的参数（Harmony 按**名字**匹配注入，不做类型检查，已反编译确认）；
    ///   · 那个 `Dictionary&lt;MaterialInfo, List&lt;Designator&gt;&gt;` 用 `IDictionary` 接住
    ///     （它实现了非泛型 `IDictionary` ⇒ 运行时 cast 必定成功）；
    ///   · 唯一需要反射的是 `MaterialInfo` 的**构造器**（缓存一次）。
    ///
    /// ── 性能 ──
    /// `PopulateMaterials` 每个地板设计器都会调一次、且建筑菜单打开时每帧重绘都会跑到，
    /// 所以**刻意不去反射调用 BAM 的 `GetFloorCosts`**（它内部会扫全部 ThingDef，翻倍就贵了）。
    /// 改为：只用一份**缓存 60 tick 的核心材料集合**去匹配 `stuffProps.categories` / `costList`，
    /// 失败即静默跳过。
    /// </summary>
    internal static class Patch_BamMaterialsFromCore
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.bammaterials";
        private const string BamPatchTypeName = "BetterArchitect.ArchitectCategoryTab_DesignationTabOnGUI_Patch";
        private const string BamMaterialInfoTypeName = "BetterArchitect.MaterialInfo";

        /// <summary>核心材料集合的缓存有效期（tick）。UI 期间够用，且把开销压到秒级一次。</summary>
        private const int CacheTicks = 60;

        private static ConstructorInfo materialInfoCtor;

        private static Map cachedMap;
        private static int cachedTick = -1000000;
        private static HashSet<ThingDef> cachedCoreStuff;

        internal static void Install()
        {
            Type patchType = AccessTools.TypeByName(BamPatchTypeName);
            if (patchType == null)
            {
                Log.Message("[数字存储补丁] 未检测到 Better Architect Menu，跳过其材料列表兼容（其余功能不受影响）。");
                return;
            }

            Type materialInfoType = AccessTools.TypeByName(BamMaterialInfoTypeName);
            materialInfoCtor = materialInfoType == null
                ? null
                : AccessTools.Constructor(materialInfoType,
                    new[] { typeof(string), typeof(Texture2D), typeof(Color), typeof(ThingDef) });

            MethodInfo populate = AccessTools.Method(patchType, "PopulateMaterials");
            MethodInfo getStuff = AccessTools.Method(patchType, "GetStuffFrom");

            if (populate == null || getStuff == null)
            {
                Log.Warning("[数字存储补丁] 找到 Better Architect Menu，但"
                            + " PopulateMaterials=" + (populate != null)
                            + " GetStuffFrom=" + (getStuff != null)
                            + "（BAM 可能改过结构）⇒ 「建筑菜单认核心材料」不可用，其余功能不受影响。");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(populate, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(Patch_BamMaterialsFromCore), nameof(Postfix_PopulateMaterials))));
            harmony.Patch(getStuff, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(Patch_BamMaterialsFromCore), nameof(Postfix_GetStuffFrom))));

            Log.Message("[数字存储补丁] 已挂载：建筑菜单（Better Architect Menu）的地板页与默认选材"
                        + "也会认数字核心里的材料"
                        + (materialInfoCtor == null ? "（但 MaterialInfo 构造器未解析到，可能不生效）" : "")
                        + "。默认开启，设置 →「Mod 兼容性」可关。");
        }

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.bamMaterialsFromCore;
        }

        // ---------------------------------------------------------------- 挂点

        /// <summary>
        /// 地板页：BAM 原方法只把"地图上有"的材料建成分类；这里补上"只在核心里有"的那些。
        /// 参数按**名字**注入；字典用 <see cref="IDictionary"/> 接住（BAM 的类型写不出来）。
        /// </summary>
        private static void Postfix_PopulateMaterials(IDictionary floorsByMaterial, Designator element)
        {
            if (floorsByMaterial == null || element == null || !Enabled() || materialInfoCtor == null)
            {
                return;
            }

            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    return;
                }

                BuildableDef bd = BuildableOf(element);
                if (bd == null)
                {
                    return;
                }

                HashSet<ThingDef> coreStuff = CoreStuffDefs(map);
                if (coreStuff.Count == 0)
                {
                    return;
                }

                // ① 固定材料清单（复刻 BAM GetFloorCosts 的第一支）
                if (bd.costList != null)
                {
                    for (int i = 0; i < bd.costList.Count; i++)
                    {
                        TryAddMaterial(floorsByMaterial, map, element, bd.costList[i].thingDef, coreStuff);
                    }
                }

                // ② "同类别的任意材料"（复刻 BAM GetFloorCosts 的第二支：
                //    条件是 stuffProps.categories 与 floor.stuffCategories 有交集，BAM 没查 IsStuff，这里保持一致）
                if (bd.costStuffCount > 0 && bd.stuffCategories != null)
                {
                    foreach (ThingDef def in coreStuff)
                    {
                        if (def == null || def.stuffProps?.categories == null)
                        {
                            continue;
                        }

                        bool match = false;
                        List<StuffCategoryDef> cats = def.stuffProps.categories;
                        for (int c = 0; c < cats.Count; c++)
                        {
                            if (cats[c] != null && bd.stuffCategories.Contains(cats[c]))
                            {
                                match = true;
                                break;
                            }
                        }

                        if (match)
                        {
                            TryAddMaterial(floorsByMaterial, map, element, def, coreStuff);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 地板页补核心材料时抛异常（已忽略，建筑菜单照常）：" + ex.Message, 5570331);
            }
        }

        /// <summary>
        /// 需材料建筑的默认选材：原方法返回 null（地图上找不到材料）时，
        /// 按与 BAM 相同的排序（commonality 降序 → BaseMarketValue 升序）从核心里挑一个。
        /// </summary>
        private static void Postfix_GetStuffFrom(BuildableDef buildable, ref ThingDef __result)
        {
            if (__result != null || !Enabled())
            {
                return;
            }

            try
            {
                if (!(buildable is ThingDef thingDef) || !thingDef.MadeFromStuff)
                {
                    return;
                }

                Map map = Find.CurrentMap;
                if (map == null)
                {
                    return;
                }

                HashSet<ThingDef> coreStuff = CoreStuffDefs(map);
                if (coreStuff.Count == 0)
                {
                    return;
                }

                ThingDef best = null;
                float bestCommonality = float.NegativeInfinity;

                foreach (ThingDef def in coreStuff)
                {
                    if (def == null || def.stuffProps == null || !def.stuffProps.CanMake(thingDef))
                    {
                        continue;
                    }

                    // 地图上已经有的，原方法早就挑过了（它返回 null 说明这些都不满足）
                    if (map.listerThings.ThingsOfDef(def).Count > 0)
                    {
                        continue;
                    }

                    float commonality = def.stuffProps.commonality;
                    if (best == null
                        || commonality > bestCommonality
                        || (commonality == bestCommonality && def.BaseMarketValue < best.BaseMarketValue))
                    {
                        best = def;
                        bestCommonality = commonality;
                    }
                }

                if (best != null)
                {
                    __result = best;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 默认选材补核心材料时抛异常（已忽略）：" + ex.Message, 5570332);
            }
        }

        // ---------------------------------------------------------------- 辅助

        private static void TryAddMaterial(IDictionary floorsByMaterial, Map map, Designator element,
                                           ThingDef def, HashSet<ThingDef> coreStuff)
        {
            if (def == null || !coreStuff.Contains(def))
            {
                return;
            }

            // 地图上已经有的，BAM 原方法已经加过了 —— 不重复加（MaterialInfo 按 def 去重，重复加也无害，但省一次反射）
            if (map.listerThings.ThingsOfDef(def).Count > 0)
            {
                return;
            }

            object key = MakeMaterialInfo(def);
            if (key == null)
            {
                return;
            }

            // 复刻 BAM 的 AddFloor：按 key 去重后把该设计器加进列表
            if (!floorsByMaterial.Contains(key))
            {
                floorsByMaterial[key] = new List<Designator>();
            }

            if (floorsByMaterial[key] is List<Designator> list && !list.Contains(element))
            {
                list.Add(element);
            }
        }

        /// <summary>反射构造 BAM 的 MaterialInfo（我们没有 BAM 的编译引用）。失败返回 null ⇒ 调用方静默跳过。</summary>
        private static object MakeMaterialInfo(ThingDef def)
        {
            try
            {
                return materialInfoCtor.Invoke(new object[]
                {
                    (string)def.LabelCap, def.uiIcon, def.uiIconColor, def
                });
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 构造 MaterialInfo 失败（地板页补材料将不生效）：" + ex.Message, 5570333);
                materialInfoCtor = null;
                return null;
            }
        }

        /// <summary>复刻 BAM 的 GetBuildableDefFrom：地板设计器可能是单项或下拉。</summary>
        private static BuildableDef BuildableOf(Designator d)
        {
            if (d is Designator_Build build)
            {
                return build.PlacingDef;
            }

            if (d is Designator_Dropdown dropdown && dropdown.Elements != null)
            {
                for (int i = 0; i < dropdown.Elements.Count; i++)
                {
                    if (dropdown.Elements[i] is Designator_Build inner)
                    {
                        return inner.PlacingDef;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 本图所有可用核心内容物里出现的**材料类** def 集合（缓存 <see cref="CacheTicks"/> tick）。
        ///
        /// ★ 为什么不用 `map.resourceCounter.AllCountedAmounts`：那套口径依赖 `def.CountAsResource`，
        ///   不能保证覆盖所有材料（例如各类石块）—— 而地板最常见的材料恰恰是石块。
        ///   直接扫核心内容物最稳，且缓存后开销可忽略（每 60 tick 一次 O(核心物品数)）。
        /// </summary>
        private static HashSet<ThingDef> CoreStuffDefs(Map map)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cachedCoreStuff != null && cachedMap == map && now - cachedTick < CacheTicks)
            {
                return cachedCoreStuff;
            }

            cachedMap = map;
            cachedTick = now;

            HashSet<ThingDef> set = cachedCoreStuff ?? new HashSet<ThingDef>();
            set.Clear();

            try
            {
                List<Building_StorageCore> cores = CoreContentAccess.UsableCores(map);
                if (cores != null)
                {
                    for (int c = 0; c < cores.Count; c++)
                    {
                        Building_StorageCore core = cores[c];
                        if (core == null || !core.Spawned)
                        {
                            continue;
                        }

                        ThingOwner owner = core.GetDirectlyHeldThings();
                        if (owner == null)
                        {
                            continue;
                        }

                        for (int i = 0; i < owner.Count; i++)
                        {
                            Thing t = owner[i];
                            if (t != null && !t.Destroyed && t.def != null && t.def.IsStuff && t.stackCount > 0)
                            {
                                set.Add(t.def);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 收集核心材料集合失败（本次按空处理）：" + ex.Message, 5570334);
                set.Clear();
            }

            cachedCoreStuff = set;
            return set;
        }
    }
}
