using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 让原版「给某某送物资」也能用数字核心里的东西（v0.40）。
    ///
    /// ── 现象（用户实测截图） ──
    /// 难民 / 访客向玩家索要物资（如 700 银）时，右键那个小人，
    /// 菜单里只有一条**灰色不可点**的：
    /// <c>不能将 700x 白银赠与波伊拉: 未找到物品</c>
    /// —— 明明核心里银多得是，却送不出去，任务也就卡死在这里。
    ///
    /// ── 根因（反编译取证，文件：<c>RimWorld/GiveItemsToPawnUtility.cs</c>） ──
    /// <code>
    /// public static Thing FindItemToGive(Pawn hauler, ThingDef thingDef)
    /// {
    ///     return GenClosest.ClosestThingReachable(
    ///         hauler.Position, hauler.Map,
    ///         ThingRequest.ForDef(thingDef),        // ★ 只搜"已生成在地图上"的 Thing
    ///         PathEndMode.Touch, TraverseParms.For(hauler), 9999f,
    ///         x => !x.IsForbidden(hauler) &amp;&amp; hauler.CanReserve(x));
    /// }
    /// </code>
    /// <c>ThingRequest.ForDef</c> 走的是 <c>ListerThings</c> —— **容器里的东西不在其中**
    /// （数字核心的内容物是未生成的 Thing）。于是：
    /// <list type="number">
    ///   <item>菜单构造 <c>GetFloatMenuOptionsForPawn</c>：<c>FindItemToGive</c> 返回 null
    ///         ⇒ 产出一条 <c>CannotGiveItemsTo / NoItemFound</c> 的**禁用**菜单项（就是截图那条）；</item>
    ///   <item>就算绕过菜单，任务链 <c>JobDriver_GiveToPawn</c> 的 <c>SetItemTarget</c>
    ///         也是同一个函数 ⇒ 拿到 null 就 <c>EndCurrentJob(Incompletable)</c>。</item>
    /// </list>
    ///
    /// 本轮**撤回**了一个跑偏的判断：最初以为这是 <c>VisitorGiftForPlayerUtility.GiveGift</c>
    /// （访客**送**礼给玩家）的落点问题。经反编译核实，截图那条文案的唯一来源是
    /// <c>GiveItemsToPawnUtility</c>，即**玩家给 NPC 送物资**这条路 —— 方向相反，已改正。
    ///
    /// ── 做法 ──
    /// 在 <c>FindItemToGive</c> 上挂 **Postfix**：原版找不到（<c>__result == null</c>）时，
    /// 从**可用数字核心**里取一份到小人脚下，并把取出来的 Thing 返回给原版。
    ///
    /// 取货复用原 mod 自己的 <see cref="HaulSourceContents.ExtractTo"/>：
    /// 它会正确地从容器移除 / <c>SplitOff</c>、落到指定格，并
    /// <c>CompAutoIngest.MarkWithdrawn</c> 打标记**防止被自动收纳立刻吸回核心**
    /// （本补丁的增强取料面板 <c>ITab_ProjectedCore</c> 用的也是这套，行为一致）。
    ///
    /// 取出之后，原版整条任务链**一字不改**地照常跑：
    /// <c>Toils_Reserve</c> → <c>GotoThing</c> → <c>StartCarryThing</c> →
    /// <c>CarryHauledThingToContainer</c> → <c>DepositHauledThingInContainer</c>。
    /// 我们只负责"让那件东西出现在地上"，搬运与交付全归原版。
    ///
    /// ── 为什么不返回"容器里的 Thing" ──
    /// 任务链要 <c>Reserve</c> 它、<c>GotoThing</c> 走到它旁边、<c>StartCarryThing</c> 搬起它
    /// —— 都需要它**在地图上**。返回容器内的引用会让 <c>GotoThing</c> 无法寻路、
    /// <c>StartCarryThing</c> 也搬不动（它只认 <c>carryTracker</c> 与格子）。
    /// 所以必须真切到地面，而不是把引用透出去。
    ///
    /// ── 失败时仍然照旧 ──
    /// 没有可用核心 / 核心没这种货 / 取不出来 ⇒ 原样返回 null，
    /// 原版照常给那条禁用菜单项（不掩盖真实缺货）。
    /// </summary>
    [HarmonyPatch(typeof(GiveItemsToPawnUtility), nameof(GiveItemsToPawnUtility.FindItemToGive))]
    internal static class Patch_FindItemToGiveFromCore
    {
        /// <summary>兜底取出次数（只读诊断用）。</summary>
        internal static int ExtractCount;

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn hauler, ThingDef thingDef, ref Thing __result)
        {
            // 原版找到了 ⇒ 一律不动（优先用地图上的现成货，与修复前完全一致）。
            if (__result != null || hauler == null || thingDef == null)
            {
                return;
            }

            Map map = hauler.Map;
            if (map == null || hauler.PositionHeld == default(IntVec3))
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.giveItemsFromCore)
            {
                return;
            }

            try
            {
                Thing taken = ExtractFromCores(hauler, thingDef, map);
                if (taken == null)
                {
                    return;   // 没有 ⇒ 放行，原版照常显示"未找到物品"
                }

                __result = taken;
                ExtractCount++;

                if (s.diagnosticsEnabled)
                {
                    Log.Message("[数字存储补丁][送物资兜底] 地图上没有，已从数字核心取出："
                                + taken.LabelShort + " x" + taken.stackCount
                                + " @ " + taken.Position);
                }
            }
            catch (Exception ex)
            {
                // 兜底失败不该把"物品没找到"升级成红字异常，也不该阻断原版流程。
                __result = null;
                Log.ErrorOnce("[数字存储补丁] 从数字核心取出赠礼物资时出错（本次放行原版行为）："
                              + ex.GetType().Name + ": " + ex.Message, 66510288);
            }
        }

        /// <summary>
        /// 在可用核心（含本图投影核心绑定的核心）里找一件 <paramref name="thingDef"/>，
        /// 取到 <paramref name="hauler"/> 脚下并返回取出来的那份。
        /// </summary>
        private static Thing ExtractFromCores(Pawn hauler, ThingDef thingDef, Map map)
        {
            List<Building_StorageCore> cores = tmpCores;
            cores.Clear();
            List<Building_StorageCore> mapCores = CoreContentAccess.UsableCores(map);
            if (mapCores != null)
            {
                cores.AddRange(mapCores);
            }

            // 本图投影核心绑定的核心也认（与补丁其它跨图逻辑同一套口径，自动去重）。
            try
            {
                ProjectionHelper.AppendProjectedCores(map, cores);
            }
            catch (Exception)
            {
                // 投影读取失败不影响本图核心这条主路径。
            }

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

                // ★ 先抓快照再取：ExtractTo 会把物品从 owner 里移除，边遍历边改会抛
                //   "Collection was modified"（本项目踩坑目录第 9 条的同类陷阱）。
                Thing found = null;
                for (int i = 0; i < owner.Count; i++)
                {
                    Thing t = owner[i];
                    if (t != null && !t.Destroyed && t.stackCount > 0 && t.def == thingDef)
                    {
                        found = t;
                        break;
                    }
                }

                if (found == null)
                {
                    continue;
                }

                // ★ 这里**只能整堆取**，不是疏漏：
                //   原版协议 GiveItemsToPawnUtility.FindItemToGive(hauler, thingDef) 只给了 def、
                //   **没有数量参数**，所以拿不到"需要几个"。取出来的这堆落到小人脚下后，
                //   原版 JobDriver_GiveToPawn 只会拿它需要的数量，**多余的留在原地、
                //   稍后被原 mod 自动收纳收回核心**，不会丢东西，也不会被消耗。
                //   （对比：Patch_InventoryStockFromCore 那边因为拿得到缺口数量，已改为按需取。）
                int want = Math.Max(1, found.stackCount);
                Thing taken = HaulSourceContents.ExtractTo(found, want, hauler.Position, map);
                if (taken != null)
                {
                    cores.Clear();
                    return taken;
                }
                // ExtractTo 返回 null 有两种可能：脚下放不下（它已把货放回核心），
                // 或者货在取的过程中失效。两者都继续找下一个核心。
            }

            cores.Clear();
            return null;
        }

        private static readonly List<Building_StorageCore> tmpCores = new List<Building_StorageCore>();
    }
}
