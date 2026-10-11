using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 代理工人（数字工人 / 制作代理）的**替身不再抽到「禁工背景」**（纯修复，默认开）。
    ///
    /// <para><b>现象</b>：约 6.8% 的替身会「随机变笨」—— 制作代理报
    /// <c>DS_BA_Block_Skill</c>（"资质不够"）、数字工人整类活干不了；读档重建即恢复。
    /// 因为不报错，玩家只会以为"这个建筑坏了"。</para>
    ///
    /// <para><b>根因（反编译逐行核实，见报告 FIX-09 §2）</b>：
    /// <c>DigitalWorkerFactory.Create</c> 用
    /// <c>PawnGenerator.GeneratePawn(Colonist, Faction.OfPlayer)</c> <b>完整随机生成</b>一个殖民者，
    /// 之后清了特质、清了关系、把所有技能写成 <c>skillLevel</c>，
    /// <b>唯独没有换掉随机滚出来的童年/成年背景</b>。而背景正是 <c>workDisables</c> /
    /// <c>DisabledWorkTypes</c> 的来源（另一来源 traits 已被它清掉）：</para>
    /// <list type="bullet">
    /// <item><c>Pawn.GetDisabledWorkTypes</c> 走 <c>story.AllBackstories</c>
    /// ⇒ 命中 <c>BackstoryDef.DisabledWorkTypes</c>；</item>
    /// <item><c>Pawn.CombinedDisabledWorkTags</c> 走
    /// <c>story.DisabledWorkTagsBackstoryAndTraits</c> ⇒ 命中 <c>BackstoryDef.workDisables</c>。</item>
    /// </list>
    /// <para>两者喂给 <c>SkillRecord.CalculateTotallyDisabled</c> ⇒
    /// <c>TotallyDisabled = true</c> ⇒ <c>GetLevel()</c> <b>直接返回 0</b>
    /// ⇒ 工厂里那句 <c>skills[i].Level = skillLevel</c> <b>白写</b>。</para>
    ///
    /// <para><b>修法（方案 A）</b>：Postfix 挂 <c>DigitalStorage.AI.DigitalWorkerFactory.Create</c>
    /// （原 mod 的 <c>internal static</c> 成员，用 <c>AccessTools</c> 按名定位，不需要编译期引用），
    /// 在它把替身交出来之后：**只把"带禁用"的那个背景槽换成"零禁用"的原版背景**，
    /// 再重算一次「禁用工作」与技能读数。刻意<b>不换好的那一半</b>（尽量少干预）、
    /// 也<b>不复制原 mod 的清理逻辑</b>（铁律 ⑪）。</para>
    ///
    /// <para><b>为什么这样换是安全的（均为反编译核实）</b>：</para>
    /// <list type="bullet">
    /// <item><c>Pawn_StoryTracker.Childhood</c> / <c>Adulthood</c> 都是**公有可写属性**，
    /// 且两个 setter 自己会 <c>backstoriesCache = null</c> ⇒ 不必手工失效背景缓存；</item>
    /// <item>候选判据 = <c>workDisables == WorkTags.None</c> <b>且</b>
    /// <c>DisabledWorkTypes.Count == 0</c> <b>且</b> 不属于 "Child" 类
    /// （<c>IsPlayerColonyChildBackstory</c> —— 原版 setter 会对"成人 + Child 类背景"打警告，
    /// 排除掉就不会刷黄字）；</item>
    /// <item><c>SkillRecord.Aptitude</c> 只来自**基因**，与背景无关 ⇒ 换背景不会让技能读数偏高偏低；</item>
    /// <item>背景的其它消费者（角色卡 UI、冥想、复制器、生命周期填背景）对"不进地图、不 tick、
    /// 不进存档"的替身**均不生效**；</item>
    /// <item><c>Pawn.CombinedDisabledWorkTags</c> 与 <c>BackstoryDef.DisabledWorkTypes</c>
    /// 都是**现算**（无缓存），<c>GetDisabledWorkTypes</c> / 技能读数 / "禁用原因"三处缓存
    /// 由 <c>Pawn.Notify_DisabledWorkTypesChanged()</c> 统一清掉。</item>
    /// </list>
    ///
    /// <para><b>为什么挂得上（内联风险已排除）</b>：<c>Create</c> 体内含 <c>try/catch</c>，
    /// Mono 的 JIT **不对带异常处理器的方法做内联** ⇒ 不存在"挂点被内联导致补丁静默失效"（坑 26/27）。</para>
    ///
    /// <para><b>已知不碰的东西</b>：原 mod 那句 <c>story.traits.allTraits.Clear()</c> 是"直接清列表"
    /// 而非走 <c>RemoveTrait</c> ⇒ 理论上 <c>TraitSet</c> 的两个**需求**缓存
    /// （<c>cachedEnabledNeeds</c> / <c>cachedDisabledNeeds</c>）可能留下陈旧项。
    /// 影响面 = 一个**不 tick 需求、不显示**的隐藏替身 ⇒ **不修**（属原 mod 自身行为，本补丁不伸手）。</para>
    /// </summary>
    internal static class Patch_WorkerBackstory
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.workerbackstory";

        private static bool childResolved;
        private static BackstoryDef safeChildhood;
        private static bool adultResolved;
        private static BackstoryDef safeAdulthood;

        public static void Install()
        {
            try
            {
                Type factory = AccessTools.TypeByName("DigitalStorage.AI.DigitalWorkerFactory");
                MethodInfo create = factory == null ? null : AccessTools.Method(factory, "Create");
                if (create == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 DigitalStorage.AI.DigitalWorkerFactory.Create"
                                + "（原 mod 可能已改结构），「替身不再抽到禁工背景」不可用（其余功能不受影响）。");
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    create,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(Patch_WorkerBackstory), nameof(Postfix))));

                Log.Message("[数字存储补丁] 已挂载：代理工人的替身不再抽到「禁工背景」"
                            + "（修「资质不够」/整类活干不了；默认开启，设置 →「制作代理」可关）。");
            }
            catch (Exception ex)
            {
                Log.Error("[数字存储补丁] 替身背景清理挂载失败（该问题保持原样）："
                          + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary><c>Create</c> 的返回值就是替身小人；生成失败（null）时什么都不做。</summary>
        private static void Postfix(Pawn __result)
        {
            if (__result == null)
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.workerCleanBackstory)
            {
                return;   // 开关关闭 ⇒ 原 mod 行为
            }

            try
            {
                Sanitize(__result);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 代理工人背景清理失败（这个替身保持原样）："
                              + ex.GetType().Name + ": " + ex.Message, 771233);
            }
        }

        /// <summary>只替换"带禁用"的背景槽，然后重算禁用工作与技能读数。</summary>
        private static void Sanitize(Pawn pawn)
        {
            Pawn_StoryTracker story = pawn.story;
            if (story == null)
            {
                return;
            }

            BackstoryDef oldChild = story.Childhood;
            BackstoryDef oldAdult = story.Adulthood;
            bool changed = false;

            if (IsBadBackstory(oldChild))
            {
                BackstoryDef rep = SafeBackstory(BackstorySlot.Childhood);
                if (rep != null && !ReferenceEquals(rep, oldChild))
                {
                    // setter 内部会清掉 story 自己的背景缓存
                    story.Childhood = rep;
                    changed = true;
                }
            }

            if (IsBadBackstory(oldAdult))
            {
                BackstoryDef rep = SafeBackstory(BackstorySlot.Adulthood);
                if (rep != null && !ReferenceEquals(rep, oldAdult))
                {
                    story.Adulthood = rep;
                    changed = true;
                }
            }

            if (!changed)
            {
                // 抽到的是"零禁用"背景 ⇒ 不动它（少干预）
                LogWorkerState(pawn, swapped: false, oldChild, oldAdult);
                return;
            }

            // 换完必须重算，否则替身仍然"笨"：
            //   · Notify 清掉 Pawn 侧的三处缓存（禁用工作表 / 技能的 TotallyDisabled / 禁用原因）；
            //   · 工厂此前已跑过一次 EnableAndInitialize ⇒ 旧背景禁掉的工作类型被置成了 0，
            //     只有再跑一次才会把它们**重新启用**。
            pawn.Notify_DisabledWorkTypesChanged();
            if (pawn.workSettings != null)
            {
                pawn.workSettings.EnableAndInitialize();
            }

            LogWorkerState(pawn, swapped: true, oldChild, oldAdult);
        }

        /// <summary>
        /// 开「诊断日志」时，**每次创建替身**都写一行（不只是换过才写）——
        /// 这样验收时可以直接核对"禁用工作 = 0 项"，不必等那 6.8% 的坏签。
        /// 若这里出现了非 0，说明还有**背景以外**的来源（最可能是基因：原版
        /// <c>GeneDefs_Misc.xml</c> 里有一条基因会禁用 <c>Violent</c> 标签），需要另开一刀。
        /// </summary>
        private static void LogWorkerState(Pawn pawn, bool swapped, BackstoryDef oldChild, BackstoryDef oldAdult)
        {
            if (DsPatchMod.Settings == null || !DsPatchMod.Settings.diagnosticsEnabled)
            {
                return;
            }
            try
            {
                Pawn_StoryTracker story = pawn.story;
                int disabled = pawn.GetDisabledWorkTypes()?.Count ?? 0;
                Log.Message("[数字存储补丁][替身] " + pawn.LabelShort
                            + "：童年=" + DefName(story?.Childhood)
                            + " 成年=" + DefName(story?.Adulthood)
                            + "；本次" + (swapped
                                ? "已换掉禁工背景（" + DefName(oldChild) + " / " + DefName(oldAdult) + "）"
                                : "未换（背景本就可用）")
                            + "；禁用工作 " + disabled + " 项");
            }
            catch
            {
            }
        }

        private static string DefName(BackstoryDef b)
        {
            return b == null ? "（无）" : b.defName;
        }

        /// <summary>这个背景会不会让替身"干不了活"。</summary>
        private static bool IsBadBackstory(BackstoryDef b)
        {
            if (b == null)
            {
                return false;
            }
            if (b.workDisables != WorkTags.None)
            {
                return true;   // 命中 Pawn.CombinedDisabledWorkTags
            }
            List<WorkTypeDef> disabled = b.DisabledWorkTypes;
            return disabled != null && disabled.Count > 0;   // 命中 Pawn.GetDisabledWorkTypes
        }

        /// <summary>
        /// 取一个"零禁用"的原版背景（按槽位缓存，只算一次）。
        /// 判据与 <see cref="IsBadBackstory"/> 严格互补；再排除 "Child" 类，避免原版 setter
        /// 对"成人 + Child 类背景"打警告。按 <c>defName</c> 排序取第一个 ⇒ 结果确定、可复现。
        /// </summary>
        private static BackstoryDef SafeBackstory(BackstorySlot slot)
        {
            if (slot == BackstorySlot.Childhood && childResolved)
            {
                return safeChildhood;
            }
            if (slot == BackstorySlot.Adulthood && adultResolved)
            {
                return safeAdulthood;
            }

            List<BackstoryDef> all = DefDatabase<BackstoryDef>.AllDefsListForReading;
            List<BackstoryDef> candidates = new List<BackstoryDef>();
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    BackstoryDef b = all[i];
                    if (b == null || b.slot != slot)
                    {
                        continue;
                    }
                    if (b.IsPlayerColonyChildBackstory)
                    {
                        continue;   // 见方法注释：排除它就不会触发原版黄字
                    }
                    if (IsBadBackstory(b))
                    {
                        continue;
                    }
                    candidates.Add(b);
                }
            }

            BackstoryDef picked = null;
            if (candidates.Count > 0)
            {
                candidates.Sort((x, y) => string.CompareOrdinal(x.defName, y.defName));
                picked = candidates[0];
            }
            else
            {
                Log.Warning("[数字存储补丁] 找不到任何「零禁用」的 " + slot
                            + " 背景（可能是被某个 mod 全改了），这项清理对" + slot + "槽位跳过。");
            }

            if (slot == BackstorySlot.Childhood)
            {
                childResolved = true;
                safeChildhood = picked;
            }
            else
            {
                adultResolved = true;
                safeAdulthood = picked;
            }
            return picked;
        }
    }
}
