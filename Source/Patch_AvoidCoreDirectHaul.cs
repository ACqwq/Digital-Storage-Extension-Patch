using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 搬运兼容（v0.35，报告 31 §12）：有可用的数字收货架时，让**数字核心不再作为直接搬运目的地**。
    ///
    /// ── 为什么需要 ──
    /// 数字核心是**单个容器**：一次只放行一个小人互动（底层限制，改不了，见报告 31 §9.2）。
    /// 而 Pick Up And Haul / Hauler's Dream 这类搬运 mod 的批量搬运只认 `HaulToCell`（格子），
    /// 不接管 `HaulToContainer`（容器）—— 实测 HD 在 `BulkHaul.AcceptsHaulDestination` 里
    /// 对 `HaulToContainer` 直接 return false。
    /// ⇒ 只要还有小人在往核心直接搬，就退化成"一次一堆、一人独享"，
    ///   其他人只能等核心空出来（用户实测：其他人是在"有人占用核心"之后才用上 HD 批量搬运）。
    ///
    /// ── 做法 ──
    /// 在原版 `StoreUtility.TryFindBestBetterNonSlotGroupStorageFor`（找"非槽位容器"目的地）上挂 Postfix：
    /// 结果若是数字核心，**且**本图确实有收货架愿意收这件物品 ⇒ 把结果置空。
    /// 这样 `TryFindBestBetterStorageFor` 会自然落到格子分支（收货架），
    /// 小人们改投收货架（每格一个独立投料位、可并行、搬运 mod 批量生效），再由收货架转进核心。
    ///
    /// ── 让位是有条件的 ──
    /// **只有确实有收货架能收时才让**，否则照旧走核心 ⇒ 不会出现"这件东西没人搬"。
    ///
    /// ── 开关 ──
    /// 默认**关**（`DsPatchSettings.avoidCoreDirectHaul`）。关掉时本 Postfix 第一行就返回，
    /// 只多一次设置读取（与既有补丁同一套写法）。
    /// </summary>
    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterNonSlotGroupStorageFor")]
    internal static class Patch_AvoidCoreDirectHaul
    {
        /// <summary>被让位的次数（只读诊断用，见 <see cref="Patch_HaulCompatDiag"/>）。</summary>
        internal static int VetoCount;

        private static void Postfix(Thing t, Map map, ref IHaulDestination haulDestination, ref bool __result)
        {
            if (!__result || haulDestination == null)
            {
                return;
            }
            if (!(haulDestination is Building_StorageCore))
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.avoidCoreDirectHaul)
            {
                return;
            }

            // ★ 关键前置条件：确实有收货架接得住，才让核心让位。
            if (!Building_ReceivingShelf.AnyShelfCanAccept(map, t))
            {
                return;
            }

            haulDestination = null;
            __result = false;
            VetoCount++;
        }
    }
}
