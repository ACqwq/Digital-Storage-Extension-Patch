using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// UFT 支持 —— 第 1 步：允许含「未完成品（UnfinishedThing）」的配方进入制作代理菜单。
    ///
    /// <para><b>原本的阻断</b>（`DigitalStorage.AI.CraftUnlocks.IsAutomationCandidate`）：
    /// <c>if (r.UsesUnfinishedThing) return false;</c> —— UFT 配方连菜单都进不了。
    /// 而这覆盖的正是**服装 / 武器 / 装备**这类主流锻造缝纫配方（<c>unfinishedThingDef != null</c>）。</para>
    ///
    /// <para><b>为什么挂 <c>CraftUnlocks.For</c> 而不是 <c>IsAutomationCandidate</c></b>：
    /// 后者方法体很小（几个 if 就 return），有 Mono JIT 内联导致补丁静默失效的风险
    /// （见项目踩坑：属性 getter / 小方法不要挂）。<c>For</c> 方法体大（循环 + 缓存写入），
    /// 且它的返回值就是**被缓存的那个 List 实例** ⇒ 原地增删即可让缓存与开关保持一致。</para>
    ///
    /// <para><b>与开关的双向同步</b>：每次都先剔除列表里所有 UFT 条目，再按开关补回合格的，
    /// 所以开关切换后**无需清缓存**，下次 <c>For</c> 调用（打开制作面板时）即生效。
    /// 剔除条件与 <c>IsAutomationCandidate</c> 的其余口径一致（非手术、有产物）。</para>
    ///
    /// <para><b>第 1 步的已知限制</b>（实测时请注意）：原版 <c>WorkGiver_DoBill.StartOrResumeBillJob</c>
    /// 对 UFT 账单会先找"地图上无主的同配方半成品"（<c>ClosestUnfinishedThingForBill</c>），
    /// 找到就返回"去把它做完"的 job（目标物是未完成品而非原料）⇒ 代理反解原料会失败，
    /// 该配方会显示"堵塞/缺料"。**地图上没有同配方半成品时不受影响**（走正常找料流程）。
    /// 这一条由后续"接管已有半成品"（第 3 步）彻底解决。</para>
    ///
    /// <para><b>默认关闭</b>：这是新增能力且尚待实测（产物四项需逐项与原版对照），
    /// 故默认关；开启路径：Mod 设置 →「制作代理」→ 允许处理含未完成品的配方。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_UftRecipes
    {
        private static Type craftUnlocksType;

        private static MethodBase TargetMethod()
        {
            craftUnlocksType = AccessTools.TypeByName("DigitalStorage.AI.CraftUnlocks");
            return craftUnlocksType == null ? null : AccessTools.Method(craftUnlocksType, "For");
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        [HarmonyPostfix]
        private static void Postfix(ThingDef benchDef, List<RecipeDef> __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || __result == null || benchDef == null)
            {
                return;
            }

            try
            {
                // 双向同步：先剔除本补丁可能加过的 UFT 条目（让"关开关"也生效），
                // 再按当前开关补回。For 的返回值就是缓存实例，原地改即等于改缓存。
                for (int i = __result.Count - 1; i >= 0; i--)
                {
                    if (IsUftRecipe(__result[i]))
                    {
                        __result.RemoveAt(i);
                    }
                }

                if (!s.craftUftRecipes)
                {
                    return;
                }

                List<RecipeDef> all = benchDef.AllRecipes;
                if (all == null)
                {
                    return;
                }

                for (int i = 0; i < all.Count; i++)
                {
                    RecipeDef r = all[i];
                    if (IsUftRecipe(r) && !__result.Contains(r))
                    {
                        __result.Add(r);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 未完成品配方放行失败（本次保持原样）："
                              + ex.GetType().Name + ": " + ex.Message, 8830125);
            }
        }

        /// <summary>与 CraftUnlocks.IsAutomationCandidate 的其余口径保持一致：非手术、有产物。</summary>
        private static bool IsUftRecipe(RecipeDef r)
        {
            if (r == null || !r.UsesUnfinishedThing || r.IsSurgery)
            {
                return false;
            }

            if (r.products != null && r.products.Count > 0)
            {
                return true;
            }

            return r.specialProducts != null && r.specialProducts.Count > 0;
        }
    }
}
