using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// **只读**诊断：把原 mod「自动收纳」每道门的状态打进日志。
    ///
    /// ★ 它**什么都不改**：只有一个返回 void 的 prefix（不干预返回值、不跳过原方法），
    ///   且只有在设置里打开「诊断日志」时才真正干活，否则第一行就 return（零开销）。
    ///   存在的唯一目的：出现"自动收纳不生效"时，一次就把卡住的那道门说出来。
    ///
    /// 五道门（对应日志里的 ①~⑤）：
    ///   ① 节流   —— <c>(TicksGame + core.thingIDNumber) % 15 != 0</c> 时被跳过（作者硬编码 15）
    ///   ② 开关   —— 核心自己的 <c>CompAutoIngest.enabled</c>、以及原 mod 总开关
    ///               <c>DigitalStorageSettings.autoIngestEnabled</c>
    ///   ③ 通电   —— <c>core.Powered</c>
    ///   ④ 研究   —— <c>DigitalStorage_AutoIngest1</c>（2/3 只决定速率 1/5/10）
    ///   ⑤ 候选   —— 原版待搬表 <c>listerHaulables.ThingsPotentiallyNeedingHauling()</c>
    ///               里有多少件通过"过滤层 + 目的地层"
    ///   ⑥ 队列   —— 每轮只收 rate 堆、窗口 rate×3，待搬表很大时排在后面的要等很多轮
    /// </summary>
    internal static class Patch_AutoIngestDiag
    {
        private const string HarmonyId = "cagier.digitalstorage.patch.autoingestdiag";

        /// <summary>全局节流：诊断开着时最多每这么多 tick 打一次，避免刷屏。</summary>
        private const int LogEveryTicks = 600;

        /// <summary>统计"可吸几件"时最多扫多少件待搬物品（这些判定不便宜）。</summary>
        private const int ScanLimit = 200;

        private static int lastTick = -1000000;

        private static MethodInfo wouldHaul;
        private static MethodInfo rejectReason;

        internal static void Install()
        {
            MethodInfo target = AccessTools.Method(typeof(CompAutoIngest), "CompTick");
            if (target == null)
            {
                Log.Warning("[数字存储补丁] 找不到 CompAutoIngest.CompTick，自动收纳诊断不可用。");
                return;
            }

            wouldHaul = AccessTools.Method(typeof(CompAutoIngest), "WouldVanillaHaulIntoCore");
            rejectReason = AccessTools.Method(typeof(CompAutoIngest), "RejectReason");

            new Harmony(HarmonyId).Patch(
                target,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Patch_AutoIngestDiag), nameof(Prefix))));
        }

        private static void Prefix(CompAutoIngest __instance)
        {
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.diagnosticsEnabled)
            {
                return; // 没开诊断 ⇒ 立刻返回，平时零开销
            }
            if (Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now - lastTick < LogEveryTicks)
            {
                return;
            }
            lastTick = now;

            try
            {
                Dump(__instance, now);
            }
            catch (Exception ex)
            {
                Log.Warning("[数字存储补丁] 自动收纳诊断输出失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Dump(CompAutoIngest comp, int now)
        {
            Traverse tv = Traverse.Create(comp);
            Building_StorageCore core = tv.Field("core").GetValue<Building_StorageCore>();
            if (core == null)
            {
                Log.Message("[数字存储补丁][自动收纳诊断] 组件的 core 为 null（不是数字核心？）。");
                return;
            }

            bool enabled = tv.Field("enabled").GetValue<bool>();
            bool researched = tv.Field("researchedCache").GetValue<bool>();
            int rate = tv.Field("ingestRateCache").GetValue<int>();
            if (rate <= 0)
            {
                rate = 1;
            }

            bool globalOn = DigitalStorageSettings.autoIngestEnabled;
            bool powered = core.Powered;
            int rem = (now + core.thingIDNumber) % 15;

            Map map = DsSafe.MapOf(core);
            int pool = 0;
            int ingestable = 0;
            int rejected = 0;
            int noDest = 0;

            if (map != null && wouldHaul != null)
            {
                ICollection<Thing> list = map.listerHaulables?.ThingsPotentiallyNeedingHauling();
                if (list != null)
                {
                    pool = list.Count;
                    int scanned = 0;
                    foreach (Thing t in list)
                    {
                        if (scanned++ >= ScanLimit)
                        {
                            break;
                        }
                        if (t == null || t.Destroyed)
                        {
                            continue;
                        }
                        if (rejectReason != null)
                        {
                            object rr = rejectReason.Invoke(null, new object[] { t, map });
                            if (rr != null && rr.ToString() != "None")
                            {
                                rejected++;
                                continue;
                            }
                        }
                        object dest = wouldHaul.Invoke(null, new object[] { map, t });
                        if (dest != null)
                        {
                            ingestable++;
                        }
                        else
                        {
                            noDest++;
                        }
                    }
                }
            }

            string verdict;
            if (!globalOn)
            {
                verdict = "★ 原 mod 总开关 autoIngestEnabled = false（去原 mod 的设置里打开）";
            }
            else if (!enabled)
            {
                verdict = "★ 本核心的自动收纳开关是关的（核心 / 投影核心上的开关）";
            }
            else if (!powered)
            {
                verdict = "★ 核心没通电";
            }
            else if (!researched)
            {
                verdict = "★ 研究 DigitalStorage_AutoIngest1 未完成";
            }
            else if (map == null)
            {
                verdict = "★ 核心不在有效地图上";
            }
            else if (pool == 0)
            {
                verdict = "正常（原版待搬表是空的 —— 现在没有需要搬的东西）";
            }
            else if (ingestable == 0)
            {
                verdict = "★ 待搬表里有 " + pool + " 件，但前 " + ScanLimit + " 件里没有一件通过判定"
                          + "（过滤层挡下 " + rejected + "、目的地层挡下 " + noDest + "）";
            }
            else
            {
                verdict = "正常：可吸 " + ingestable + " 件；每轮收 " + rate + " 堆、窗口 " + (rate * 3)
                          + " ⇒ 待搬表 " + pool + " 件时，排在后面的要等约 "
                          + (int)Math.Ceiling(pool / (double)rate) + " 轮（每轮 15 tick）";
            }

            Log.Message("[数字存储补丁][自动收纳诊断] 核心#" + core.thingIDNumber
                        + "  ①节流余数=" + rem + "(0 才干活)"
                        + "  ②本核开关=" + enabled + " 总开关=" + globalOn
                        + "  ③通电=" + powered
                        + "  ④研究=" + researched + " 速率=" + rate
                        + "  ⑤待搬表=" + pool + " 可吸=" + ingestable
                        + " 过滤挡=" + rejected + " 目的地挡=" + noDest
                        + "\n    ⑥结论：" + verdict);
        }
    }
}
