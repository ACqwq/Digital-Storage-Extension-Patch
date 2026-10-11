using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.UI;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 「从数字存储取出…」的增强选料面板。
    ///
    /// ── 背景 ──
    /// 原 mod 的选料是**二级 FloatMenu**（FloatMenuOptionProvider_DS_Withdraw 的点击闭包里现拼）：
    /// 按 def 标签排序的扁平列表，每行"标签（可用 N）"——没有图标、没有分组、没有搜索，
    /// 库存一多基本没法用。本窗口替换的就是这一层。
    ///
    /// ── 刻意保持与原版逐字一致的部分（不要"顺手优化"）──
    /// <list type="bullet">
    /// <item>库存快照：<see cref="HaulSourceContents.GatherAllIncludingRemote"/>，一次性快照，不做每帧扫描
    ///       （原二级菜单同为一次性快照；手动「刷新」重扫）。</item>
    /// <item>数量弹窗：直接复用原 mod 的 <see cref="Dialog_WithdrawAmount"/>
    ///       （上限 = min(可用, 可携带)，与原版同一语义）。</item>
    /// <item>下单：FindBestIncludingRemote（本图源优先、远图兜底、取最大堆）
    ///       → <c>Job(DigitalStorage_WithdrawToSpot)</c>，A=右键格、B=那堆货、C=其直接容器，
    ///       <c>count=数量</c> → <c>TryTakeOrderedJob(job, JobTag.MiscWork, false)</c>。
    ///       与原版回调逐行等价（原版传的就是 (JobTag)1 == JobTag.MiscWork，已核实枚举顺序）。</item>
    /// </list>
    ///
    /// ── 与原版不同的部分（即本面板的价值）──
    /// 搜索框；按原 mod 自己的 <see cref="ItemGrouping"/> 六组折叠；行带图标与数量；
    /// 标注物品在**哪个核心**（本图 / 远图 + 核心名@地图名，跨图时才显示）；
    /// 行内「取满」快捷下单；下单后面板保持打开，可连续取多种（原版每取一次要重走两层菜单）。
    /// </summary>
    public class Window_WithdrawPicker : Window
    {
        private struct Row
        {
            public ThingDef def;
            public string label;
            public ThingDef stuff;
            public int count;
            public int thisMapCount;
            public int remoteCount;
            public string remoteHint;
            public ItemGroup group;
        }

        private static readonly Vector2 WinSize = new Vector2(620f, 640f);

        private readonly Pawn pawn;
        private readonly IntVec3 cell;

        private string search = "";
        private Vector2 scroll;
        // 与 ITab_ProjectedCore 一致：ItemGroup 共 6 组（含 Other）。
        private readonly bool[] expanded = { true, true, true, true, true, true };
        private readonly List<Row> rows = new List<Row>();

        // 小屏 / 大 UI 缩放兜底：面板高不超过屏幕高 - 60（原版 Window 的常见做法）。
        // 注意 Verse.UI 要写全名 —— using DigitalStorage.UI 会把 "UI" 先解析成原 mod 的命名空间。
        public override Vector2 InitialSize =>
            new Vector2(WinSize.x, Mathf.Min(WinSize.y, Verse.UI.screenHeight - 60f));

        public Window_WithdrawPicker(Pawn pawn, IntVec3 cell)
        {
            this.pawn = pawn;
            this.cell = cell;
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            draggable = true;
            closeOnClickedOutside = false;
            Snapshot();
        }

        // ---------------------------------------------------------------- 快照

        /// <summary>
        /// 重建 def → 可用量表。与原版聚合同口径：按 <see cref="ThingDef"/> 聚合
        /// （不分材质/品质——下单走 FindBest，本来就允许"任意一堆该 def"）。
        /// </summary>
        private void Snapshot()
        {
            rows.Clear();
            if (pawn == null || pawn.Map == null)
            {
                return;
            }
            Map map = pawn.Map;

            List<Thing> buffer = new List<Thing>();
            HaulSourceContents.GatherAllIncludingRemote(map, buffer);

            var order = new List<ThingDef>();
            var counts = new Dictionary<ThingDef, int>();
            var localCounts = new Dictionary<ThingDef, int>();
            var remoteCounts = new Dictionary<ThingDef, int>();
            var stuffs = new Dictionary<ThingDef, ThingDef>();
            var hints = new Dictionary<ThingDef, List<string>>();

            for (int i = 0; i < buffer.Count; i++)
            {
                Thing t = buffer[i];
                if (t == null || t.Destroyed || t.def == null
                    || t.def.category != ThingCategory.Item)
                {
                    continue;
                }
                if (!counts.TryGetValue(t.def, out int _))
                {
                    counts[t.def] = 0;
                    localCounts[t.def] = 0;
                    remoteCounts[t.def] = 0;
                    stuffs[t.def] = t.Stuff;
                    hints[t.def] = new List<string>();
                    order.Add(t.def);
                }
                counts[t.def] += t.stackCount;

                Map holderMap = HolderMapOf(t);
                if (holderMap == map)
                {
                    localCounts[t.def] += t.stackCount;
                }
                else
                {
                    remoteCounts[t.def] += t.stackCount;
                    string hint = DescribeSourceCore(t, map);
                    List<string> list = hints[t.def];
                    if (!string.IsNullOrEmpty(hint) && !list.Contains(hint) && list.Count < 3)
                    {
                        list.Add(hint);
                    }
                }
            }

            for (int j = 0; j < order.Count; j++)
            {
                ThingDef def = order[j];
                List<string> list = hints[def];
                string hint;
                if (list.Count == 0)
                {
                    hint = "";
                }
                else if (list.Count <= 2)
                {
                    hint = string.Join("、", list.ToArray());
                }
                else
                {
                    hint = list[0] + "等";
                }
                rows.Add(new Row
                {
                    def = def,
                    label = def.LabelCap.Resolve(),
                    stuff = stuffs[def],
                    count = counts[def],
                    thisMapCount = localCounts[def],
                    remoteCount = remoteCounts[def],
                    remoteHint = hint,
                    group = ItemGrouping.GroupOf(def)
                });
            }
            rows.Sort((Row a, Row b) => string.Compare(a.label, b.label, StringComparison.Ordinal));
        }

        /// <summary>
        /// 沿容器链往上找第一件"在有效地图上"的 Thing，取其地图。
        /// ★ 核心内容物全部处于未 Spawn 状态，直接读 <c>t.Map</c> 恒为 null，必须走持有链；
        ///   用 <see cref="DsSafe.MapOf"/> 而不是裸 <c>.Map</c>，防僵尸地图下标（v0.18 的教训）。
        /// </summary>
        private static Map HolderMapOf(Thing t)
        {
            IThingHolder h = t.ParentHolder;
            while (h != null)
            {
                Thing th = h as Thing;
                if (th != null)
                {
                    Map m = DsSafe.MapOf(th);
                    if (m != null)
                    {
                        return m;
                    }
                }
                h = h.ParentHolder;
            }
            return null;
        }

        /// <summary>
        /// 找这堆货所在的核心，复用 <see cref="CompCoreName.DescribeCore"/> 拼出
        /// "核心名 (x, z) @ 地图"（同图核心自动不带 @ 地图后缀）。
        /// </summary>
        private static string DescribeSourceCore(Thing t, Map relativeTo)
        {
            IThingHolder h = t.ParentHolder;
            while (h != null)
            {
                if (h is Building_StorageCore core)
                {
                    return CompCoreName.DescribeCore(core, relativeTo);
                }
                h = h.ParentHolder;
            }
            return null;
        }

        // ---------------------------------------------------------------- 查询

        private bool RowMatchesSearch(Row row)
        {
            if (string.IsNullOrEmpty(search))
            {
                return true;
            }
            return row.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private float CalcListHeight()
        {
            float num = 0f;
            for (int g = 0; g < 6; g++)
            {
                int n = CountInGroup((ItemGroup)g);
                if (n == 0)
                {
                    continue;
                }
                num += 28f;
                if (expanded[g])
                {
                    num += n * 26f;
                }
            }
            return num;
        }

        private int CountInGroup(ItemGroup group)
        {
            int n = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].group == group && RowMatchesSearch(rows[i]))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>当前小人还能再拿多少个该 def（与原版传给数量弹窗的口径一致）。</summary>
        private int MaxCarryOf(ThingDef def)
        {
            return pawn.carryTracker != null ? pawn.carryTracker.AvailableStackSpace(def) : 0;
        }

        // ---------------------------------------------------------------- 绘制

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            if (pawn == null || pawn.Destroyed || pawn.Map == null)
            {
                Close(false);
                return;
            }

            Rect rect = inRect.ContractedBy(10f);
            float y = 0f;

            // ---- 标题 ----
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 30f),
                "DS_WithdrawToSpot".Translate());
            Text.Font = GameFont.Small;
            y += 32f;

            // ---- 操作说明（可能换行，高度现算）----
            TaggedString hint = "DigitalStoragePatch_WithdrawPicker_Hint".Translate();
            float hintH = Text.CalcHeight(hint, rect.width);
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, hintH), hint);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += hintH + 6f;

            // ---- 搜索 + 刷新 ----
            search = Widgets.TextField(new Rect(rect.x, rect.y + y, rect.width - 86f, 24f), search);
            if (Widgets.ButtonText(new Rect(rect.xMax - 78f, rect.y + y, 78f, 24f),
                "DigitalStoragePatch_WithdrawPicker_Refresh".Translate()))
            {
                Snapshot();
            }
            y += 32f;

            // ---- 分组列表 ----
            Rect outRect = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, CalcListHeight());
            Widgets.BeginScrollView(outRect, ref scroll, viewRect, true);

            if (rows.Count == 0)
            {
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 40f),
                    "DigitalStoragePatch_WithdrawPicker_Empty".Translate());
            }

            float cy = 0f;
            for (int g = 0; g < 6; g++)
            {
                ItemGroup group = (ItemGroup)g;
                int kinds = 0;
                long total = 0L;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].group == group && RowMatchesSearch(rows[i]))
                    {
                        kinds++;
                        total += rows[i].count;
                    }
                }
                if (kinds == 0)
                {
                    continue;
                }

                Rect groupRect = new Rect(0f, cy, viewRect.width, 26f);
                if (Mouse.IsOver(groupRect))
                {
                    Widgets.DrawHighlight(groupRect);
                }
                Widgets.Label(groupRect, (expanded[g] ? "▼ " : "▶ ")
                    + ItemGrouping.LabelKeyOf(group).Translate()
                    + "   x" + total + "   (" + kinds + " " + "DS_Kinds".Translate() + ")");
                if (Widgets.ButtonInvisible(groupRect, true))
                {
                    expanded[g] = !expanded[g];
                }
                cy += 28f;

                if (!expanded[g])
                {
                    continue;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    Row row = rows[i];
                    if (row.group != group || !RowMatchesSearch(row))
                    {
                        continue;
                    }
                    cy = DrawRow(row, viewRect.width, cy);
                }
            }
            Widgets.EndScrollView();

            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
        }

        private float DrawRow(Row row, float width, float cy)
        {
            Rect rowRect = new Rect(12f, cy, width - 12f, 24f);
            if (Mouse.IsOver(rowRect))
            {
                Widgets.DrawHighlight(rowRect);
            }
            Widgets.ThingIcon(new Rect(rowRect.x, rowRect.y, 22f, 22f),
                row.def, row.stuff, null, 1f, null, null, 1f);

            // 来源标注（跨图才有）：行尾只放短文案（数量），核心名详情进悬停提示。
            // v0.21 首版把"核心名 (x,z) @ 地图"整段塞进 160px 行内 —— Tiny 字开着自动换行，
            // 多行文字在 20px 高的矩形里叠成一团（实测翻车），改为 tooltip 承载详情。
            string sourceTag = "";
            string sourceTip = null;
            if (row.remoteCount > 0)
            {
                sourceTag = row.thisMapCount > 0
                    ? "DigitalStoragePatch_WithdrawPicker_SourceMixed".Translate(
                        row.thisMapCount, row.remoteCount).Resolve()
                    : "DigitalStoragePatch_WithdrawPicker_SourceRemote".Translate(
                        row.remoteCount).Resolve();
                if (!string.IsNullOrEmpty(row.remoteHint))
                {
                    sourceTip = "DigitalStoragePatch_WithdrawPicker_RemoteTip"
                        .Translate(row.remoteHint).Resolve();
                }
            }
            if (!string.IsNullOrEmpty(sourceTip))
            {
                TooltipHandler.TipRegion(rowRect, sourceTip);
            }

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            Text.WordWrap = false;
            if (!string.IsNullOrEmpty(sourceTag))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Widgets.Label(new Rect(rowRect.xMax - 300f, rowRect.y + 4f, 160f, 20f), sourceTag);
                GUI.color = Color.white;
            }
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Widgets.Label(new Rect(rowRect.x + 26f, rowRect.y, rowRect.width - 330f, 24f),
                row.label + "   x" + row.count);

            // 「取出」：复用原 mod 的数量弹窗（口径与原版一致）。
            if (Widgets.ButtonText(new Rect(rowRect.xMax - 64f, rowRect.y, 64f, 22f),
                "DS_WithdrawBtn".Translate()))
            {
                Row captured = row;
                Find.WindowStack.Add(new Dialog_WithdrawAmount(
                    captured.label, captured.count, MaxCarryOf(captured.def),
                    delegate (int amount) { Withdraw(captured, amount); }));
            }
            // 「取满」：直接按可携带上限下单，不弹窗。
            if (Widgets.ButtonText(new Rect(rowRect.xMax - 130f, rowRect.y, 62f, 22f),
                "DigitalStoragePatch_WithdrawPicker_TakeAll".Translate()))
            {
                int maxCarry = MaxCarryOf(row.def);
                int amount = maxCarry > 0 ? Math.Min(row.count, maxCarry) : row.count;
                if (amount <= 0)
                {
                    Messages.Message("DigitalStoragePatch_WithdrawPicker_CantCarry".Translate(),
                        pawn, MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    Withdraw(row, amount);
                }
            }
            return cy + 26f;
        }

        // ---------------------------------------------------------------- 下单

        /// <summary>
        /// 下单，逐行复用原 mod 的取料链路（见类注释）。找不到货时不做任何事
        /// （与原版回调一致：快照过期是常态，玩家重试即可）。
        /// </summary>
        private void Withdraw(Row row, int amount)
        {
            if (pawn == null || pawn.Destroyed || pawn.Map == null || amount <= 0)
            {
                return;
            }
            Map map = pawn.Map;
            Thing best = HaulSourceContents.FindBestIncludingRemote(
                map, (Thing t) => t.stackCount, (Thing t) => t.def == row.def);
            if (best == null)
            {
                Messages.Message(
                    "DigitalStoragePatch_WithdrawPicker_TookFail".Translate(row.label),
                    pawn, MessageTypeDefOf.RejectInput, false);
                Snapshot();
                return;
            }

            Job job = JobMaker.MakeJob(
                DigitalStorage_JobDefOf.DigitalStorage_WithdrawToSpot, cell);
            job.SetTarget(TargetIndex.B, best);
            job.SetTarget(TargetIndex.C, best.ParentHolder as Thing);
            job.count = amount;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork, false);

            Messages.Message(
                "DigitalStoragePatch_WithdrawPicker_Took".Translate(row.label, amount),
                pawn, MessageTypeDefOf.NeutralEvent, false);
            if (DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 取料面板下单：" + amount + " x " + row.label
                            + " → (" + cell.x + ", " + cell.z + ")，来源堆 "
                            + best.stackCount + "，持有者 " + (best.ParentHolder as Thing)?.LabelShort);
            }

            // 下单成功后刷新计数（窗口保持打开，方便连续取多种）。
            Snapshot();
        }
    }
}
