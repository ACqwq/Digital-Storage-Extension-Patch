using System;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 「用电豁免」：**不改变**原 mod 对原版的耗电/供电要求，只让数字存储建筑
    /// 始终认为自己「已通电且可工作」。太阳耀斑、电网断电、雨天短路都不再让它们停机。
    ///
    /// 挂点选择（沿用 3.0 已验证的低频做法）：
    ///   - <see cref="Building_StorageCore"/>.get_Powered  —— 核心可用性的总闸门
    ///   - <see cref="Building_StorageCore"/>.get_IsUsableNow —— 防止 get_Powered 被内联后漏掉
    ///   - <see cref="CompDigitalWorker"/>.get_Powered     —— 数字工人代理
    ///   - <see cref="CompBillAutomation"/>.get_Powered    —— 制作代理
    ///
    /// ⚠️ **绝不去 patch 原版 `CompPowerTrader.get_PowerOn`**：
    /// 那是全局超高频属性，会给每一次通电判定加 Harmony 开销（3.0 曾因此踩性能坑）。
    ///
    /// 每个目标**单独挂载**并各自 try/catch：任一目标消失（原 mod 改结构）只记一条日志，
    /// 不会让其它挂点或整个补丁链失效。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Patch_PowerExempt
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.powerexempt";

        static Patch_PowerExempt()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 用电豁免安装失败（功能不可用，其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static bool ExemptActive
        {
            get
            {
                DsPatchSettings s = DsPatchMod.Settings;
                return s != null && s.powerExempt;
            }
        }

        private static void Install()
        {
            Harmony harmony = new Harmony(HarmonyId);
            int ok = 0;

            ok += TryPatch(harmony, typeof(Building_StorageCore), "get_Powered", nameof(Postfix_Powered)) ? 1 : 0;
            ok += TryPatch(harmony, typeof(Building_StorageCore), "get_IsUsableNow", nameof(Postfix_IsUsableNow)) ? 1 : 0;
            ok += TryPatch(harmony, typeof(CompDigitalWorker), "get_Powered", nameof(Postfix_Powered)) ? 1 : 0;
            ok += TryPatch(harmony, typeof(CompBillAutomation), "get_Powered", nameof(Postfix_Powered)) ? 1 : 0;

            if (ok < 4)
            {
                Log.Warning("[数字存储补丁] 用电豁免只挂上 " + ok + "/4 个挂点，"
                            + "原 mod 结构可能已变化；请检查上方的错误日志。");
            }
        }

        private static bool TryPatch(Harmony harmony, Type type, string methodName, string postfixName)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    Log.Error("[数字存储补丁] 用电豁免：找不到挂点 "
                              + type.FullName + "." + methodName + "（原 mod 结构可能已变，跳过该挂点）。");
                    return false;
                }
                MethodInfo postfix = AccessTools.Method(typeof(Patch_PowerExempt), postfixName);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 用电豁免：挂载 " + type.FullName + "." + methodName + " 失败："
                          + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>任何 "是否已通电" 属性：为假且豁免开启时置真。</summary>
        private static void Postfix_Powered(ref bool __result)
        {
            if (__result || !ExemptActive)
            {
                return;
            }
            __result = true;
        }

        /// <summary>核心可用性总闸门：只把「因为没电」而变假的补成真，不改其它条件。</summary>
        private static void Postfix_IsUsableNow(Building_StorageCore __instance, ref bool __result)
        {
            if (__result || !ExemptActive)
            {
                return;
            }
            if (__instance != null && __instance.Spawned && !__instance.Destroyed)
            {
                __result = true;
            }
        }
    }
}
