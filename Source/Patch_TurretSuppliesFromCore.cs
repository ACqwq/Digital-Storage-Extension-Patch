using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让炮塔（原版迫击炮 / 需要装弹的炮塔）能从数字核心补弹与补燃料（v0.45，「上·2」批，C1 + C2）。
    ///
    /// ── 现象（用户实测 C1）──
    /// 操作原版迫击炮需要装弹时，炮弹全存数字核心 ⇒ 读不到核心的炮弹，**装不上**（弹"附近没有炮弹"）。
    /// （C2 是同一处的"补燃料"分支，触发条件较窄：现代迫击炮模式 + 需要炮管燃料。）
    ///
    /// ── 根因（反编译取证，<c>RimWorld/JobDriver_ManTurret.cs</c>）──
    /// <code>
    /// public static Thing FindAmmoForTurret(Pawn pawn, Building_TurretGun gun)
    /// {
    ///     ... allowedShellsSettings ...
    ///     return GenClosest.ClosestThingReachable(gun.Position, gun.Map,
    ///         ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.OnCell,
    ///         TraverseParms.For(pawn), 40f, ShellValidator);          // ★ 没传 lookInHaulSources
    /// }
    ///
    /// public static Thing FindFuelForTurret(Pawn pawn, Building_TurretGun gun)
    /// {
    ///     ... refuelableComp ...
    ///     return GenClosest.ClosestThingReachable(gun.Position, gun.Map,
    ///         ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), ... 40f, FuelValidator);  // ★ 同上
    /// }
    /// </code>
    /// 两处均为 <c>lookInHaulSources</c> 默认 false ⇒ 只搜地图上已生成的物品，看不到核心。
    /// <para>⚠️ 原版有 <b>40 格距离限制</b>（以炮塔位置为根）。本补丁把货取到**操作者脚下** ——
    /// 操作者就站在炮塔旁 ⇒ 距离天然满足，无需复刻该限制。</para>
    ///
    /// ── 做法（两个 static 方法各挂 Postfix；体量足，不会被内联）──
    /// 原版返回 null 时，在可用数字核心（含本图投影核心绑定的）里找一件**通过与原版同样校验**的物品
    /// （弹药：外壳组 + 允许弹种 + 阵营/非致命过滤；燃料：<c>fuelFilter</c>），取 1 份到操作者脚下。
    /// 原版给得出物品时一字不动。后续装填/加燃料全归原版。
    ///
    /// 关闭 = 完全回到原版行为（读不到核心弹/燃料）。
    /// </summary>
    internal static class Patch_TurretSuppliesFromCore
    {
        internal static int AmmoFixCount;
        internal static int FuelFixCount;

        private static bool Enabled()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            return s != null && s.turretSuppliesFromCore;
        }

        // ════════════════════════════ C1 · 补弹 ════════════════════════════

        [HarmonyPatch(typeof(JobDriver_ManTurret), "FindAmmoForTurret")]
        internal static class Patch_FindAmmoForTurret
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Building_TurretGun gun, ref Thing __result)
            {
                if (__result != null || pawn == null || gun == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                try
                {
                    // 镜像原版：殖民者 / 殖民机械体才受"允许弹种"限制。
                    StorageSettings allowedShellsSettings = (pawn.IsColonist || pawn.IsColonyMech)
                        ? gun.gun?.TryGetComp<CompChangeableProjectile>()?.allowedShellsSettings
                        : null;
                    ThingRequest shellReq = ThingRequest.ForGroup(ThingRequestGroup.Shell);

                    Thing shell = CoreContentAccess.ExtractOne(pawn, t =>
                    {
                        if (t.def == null || !shellReq.Accepts(t))
                        {
                            return false;
                        }
                        if (allowedShellsSettings != null && !allowedShellsSettings.AllowedToAccept(t))
                        {
                            return false;
                        }
                        // 镜像原版：非玩家阵营时排除"不伤健康"的弹（如烟雾）。
                        if (pawn.Faction != Faction.OfPlayer
                            && t.def.projectileWhenLoaded?.projectile != null
                            && !t.def.projectileWhenLoaded.projectile.damageDef.harmsHealth)
                        {
                            return false;
                        }
                        return true;
                    }, 1, map);

                    if (shell != null)
                    {
                        __result = shell;
                        AmmoFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][炮塔补弹] 地图上没有炮弹，已从数字核心取出："
                                        + shell.LabelShort + " @ " + shell.Position);
                        }
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取炮弹时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510320);
                }
            }
        }

        // ════════════════════════════ C2 · 补燃料 ════════════════════════════

        [HarmonyPatch(typeof(JobDriver_ManTurret), "FindFuelForTurret")]
        internal static class Patch_FindFuelForTurret
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Pawn pawn, Building_TurretGun gun, ref Thing __result)
            {
                if (__result != null || pawn == null || gun == null || !Enabled())
                {
                    return;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    return;
                }

                CompRefuelable refuelable = gun.TryGetComp<CompRefuelable>();
                if (refuelable?.Props?.fuelFilter == null)
                {
                    return;   // 原版此分支也会返回 null
                }

                try
                {
                    ThingFilter filter = refuelable.Props.fuelFilter;
                    Thing fuel = CoreContentAccess.ExtractOne(pawn, t => t.def != null && filter.Allows(t), 1, map);
                    if (fuel != null)
                    {
                        __result = fuel;
                        FuelFixCount++;

                        DsPatchSettings s = DsPatchMod.Settings;
                        if (s != null && s.diagnosticsEnabled)
                        {
                            Log.Message("[数字存储补丁][炮塔补燃料] 地图上没有燃料，已从数字核心取出："
                                        + fuel.LabelShort + " @ " + fuel.Position);
                        }
                    }
                }
                catch (Exception ex)
                {
                    __result = null;
                    Log.ErrorOnce("[数字存储补丁] 从数字核心取炮塔燃料时出错（本次放行原版行为）："
                                  + ex.GetType().Name + ": " + ex.Message, 66510321);
                }
            }
        }
    }
}
