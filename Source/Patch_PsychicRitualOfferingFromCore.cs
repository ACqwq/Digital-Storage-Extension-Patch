using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 修复：Anomaly 的心灵仪式「收集祭品」拿不到数字核心里的物品。
    ///
    /// <para><b>机制（原版 + Anomaly 反编译实锤）</b>：
    /// 需要祭品的仪式（<c>PsychicRitualDef_InvocationCircle.RequiredOffering</c>）
    /// 由 <c>JobGiver_GatherOfferingsForPsychicRitual</c> 派活，它只做两件事：</para>
    /// <code>
    /// // ① 数参与者**随身携带**（pawn.inventory）里已有的祭品
    /// needed = ceil(RequiredOffering.GetBaseCount() - PawnsOfferingCount(参与者们, RequiredOffering))
    /// // ② 在地图上找祭品
    /// GenClosest.ClosestThingReachable(pos, map,
    ///     ThingRequest.ForGroup(ThingRequestGroup.HaulableAlways), ...)   // ★ 只找地图上已生成的物品
    /// if (thing == null) return null;                                     // ★ 找不到 → 不派活
    /// return JobMaker.MakeJob(JobDefOf.TakeCountToInventory, thing);      // 捡进背包
    /// </code>
    /// <para>数字核心里的物品是**未生成（未 Spawned）**的容器内容物，既不在地图物品列表里，
    /// 也不在小人身上 ⇒ ② 永远找不到 ⇒ 收集者一直不派活 ⇒ 仪式走到
    /// <c>PsychicRitualToil_GatherOfferings</c> 的
    /// <c>PsychicRitualToil_GatherOfferings_OfferingUnavailable</c>（"祭品不可用"）
    /// 后取消。</para>
    ///
    /// <para><b>修法</b>：Postfix 挂 <c>TryGiveJob</c> —— **仅在原方法返回空**
    /// （地图上确实没有祭品）且仪式确实还缺祭品时接手：从数字核心的内容物里挑一件符合
    /// <c>RequiredOffering.filter</c> 的，取出到收集者脚下，再返回**原版同一个**
    /// <c>TakeCountToInventory</c> job 让它捡起来。</para>
    ///
    /// <para><b>为什么不是"直接从计数里算上核心物品"</b>：那样仪式会以为祭品已备齐，
    /// 但后续消耗阶段是从小人身上取的 ⇒ 会变成"白嫖祭品"或直接出错。
    /// 这里保持"小人真的把祭品拿到身上"的原版语义，只是把取货地点从地图换成核心。</para>
    ///
    /// <para><b>为什么不会重复取货</b>：我们取出后物品就落在地图上了，
    /// 下一次<b>原方法自己</b>就能看到它（它会返回 job，我们便不再干预）；
    /// 且我们只在原方法返回空时才动手，天然幂等。</para>
    ///
    /// <para><b>范围</b>：本项只覆盖"需要祭品（RequiredOffering）"的仪式。
    /// 若还有其它类型的仪式报同样问题（例如需要特定目标物），请指出具体仪式名再排查。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_GatherOfferingsForPsychicRitual), "TryGiveJob")]
    internal static class Patch_PsychicRitualOfferingFromCore
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.ritualOfferingFromCore || pawn == null)
            {
                return;
            }

            // 原方法已经找到了祭品（地图上有）⇒ 完全不干预。
            if (__result != null)
            {
                return;
            }

            try
            {
                if (!TryBuildCoreOfferingJob(pawn, out Job job))
                {
                    return;
                }

                __result = job;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 仪式从核心取祭品失败（本次不干预）："
                              + ex.GetType().Name + ": " + ex.Message, 8830141);
            }
        }

        private static bool TryBuildCoreOfferingJob(Pawn pawn, out Job job)
        {
            job = null;

            // ── 复刻原方法的上下文解析（全部为 public 成员，故可类型安全地访问）──
            Lord lord = pawn.GetLord();
            if (!(lord?.CurLordToil is LordToil_PsychicRitual toil))
            {
                return false;
            }

            PsychicRitual ritual = toil.RitualData?.psychicRitual;
            if (ritual == null || !(ritual.def is PsychicRitualDef_InvocationCircle def))
            {
                return false;
            }

            IngredientCount offering = def.RequiredOffering;
            if (offering == null || offering.filter == null)
            {
                return false;
            }

            PsychicRitualRoleAssignments assignments = ritual.assignments;
            PsychicRitualRoleDef role = assignments?.RoleForPawn(pawn);
            if (role == null)
            {
                return false;
            }

            float have = PsychicRitualToil_GatherOfferings.PawnsOfferingCount(
                assignments.AssignedPawns(role), offering);
            int needed = Mathf.CeilToInt(offering.GetBaseCount() - have);
            if (needed <= 0)
            {
                return false;   // 已经不缺，交给原逻辑
            }

            Map map = pawn.MapHeld;
            if (map == null)
            {
                return false;
            }

            // ── 从数字核心里找一件合格的祭品 ──
            Thing pick = FindInCores(map, pawn, offering, needed);
            if (pick == null)
            {
                return false;
            }

            // ── 取出到收集者脚下（用原版 API：SplitOff + GenPlace）──
            int take = Mathf.Min(needed, pick.stackCount);
            Thing extracted = ExtractToFeet(pick, take, pawn);
            if (extracted == null)
            {
                return false;
            }

            // ── 返回原版同一个「捡进背包」job ──
            job = JobMaker.MakeJob(JobDefOf.TakeCountToInventory, extracted);
            job.count = Mathf.Min(take, extracted.stackCount);
            return true;
        }

        private static Thing FindInCores(Map map, Pawn pawn, IngredientCount offering, int needed)
        {
            return CoreContentAccess.FindInCores(
                map, offering.filter, new List<Pawn> { pawn }, needed);
        }

        /// <summary>
        /// 把容器里的物品取出到 pawn 脚下（对应原 mod 的内部工具
        /// <c>HaulSourceContents.ExtractToFeet</c>，因它是 internal 而在此用原版 API 重写）。
        /// 落地失败则放回原容器。
        /// </summary>
        private static Thing ExtractToFeet(Thing t, int count, Pawn pawn)
        {
            int n = Mathf.Min(count, t.stackCount);
            if (n <= 0)
            {
                return null;
            }

            IThingHolder holder = t.ParentHolder;
            ThingOwner owner = holder?.GetDirectlyHeldThings();
            if (owner == null || !owner.Contains(t))
            {
                return null;
            }

            Thing extracted;
            if (n >= t.stackCount)
            {
                owner.Remove(t);
                extracted = t;
            }
            else
            {
                extracted = t.SplitOff(n);
            }

            if (extracted == null || extracted.Destroyed)
            {
                return null;
            }

            if (GenPlace.TryPlaceThing(extracted, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near))
            {
                return extracted;
            }

            // 落不了地：放回原容器，避免凭空消失。
            try
            {
                if (!extracted.Destroyed && !owner.TryAdd(extracted))
                {
                    Log.Error("[数字存储补丁] 仪式取祭品：既放不回核心也落不了地：" + extracted);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 仪式取祭品回滚失败：" + ex.Message, 8830143);
            }

            return null;
        }
    }
}
