using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// H · 投影核心的「数字存储」页签。
    ///
    /// ── 为什么不复用原 mod 的 <c>ITab_DigitalStorage</c> ──
    /// 它的取值是 <c>SelThing as Building_StorageCore</c>（已核实），投影核心不是那个类型 ⇒ 直接
    /// 把它的 InspectTabDef 挂到投影核心上只会得到一个永远不可见的页签。
    /// 所以自己写一个，但**能引用的全都引用**：
    /// <list type="bullet">
    /// <item>分组与分组名：<see cref="ItemGrouping.GroupOf"/> / <see cref="ItemGrouping.LabelKeyOf"/></item>
    /// <item>页签名与文案：直接用原 mod 的翻译键（<c>DS_TabDigitalStorage</c> / <c>DS_CapacityBarLv</c> /
    ///      <c>DS_Kinds</c> / <c>DS_WithdrawBtn</c> / <c>DS_NoSpaceNearCore</c>），不另造一套词</item>
    /// <item>取物：<see cref="HaulSourceContents.ExtractTo"/> —— 它接受"任意容器里的 Thing + 目标地图"，
    ///      内部用 <c>holdingOwner.Remove</c> / <c>SplitOff</c> 正确移除，并 <c>CompAutoIngest.MarkWithdrawn</c>
    ///      防止刚取出的东西被立刻吸回去。跨图投放也由它处理（<c>GenPlace.TryPlaceThing</c> 传的是目标地图）</item>
    /// <item>数量输入：<c>Dialog_WithdrawAmount</c>（原 mod 的取件数量弹窗）</item>
    /// </list>
    ///
    /// ── 与核心面板的差异 ──
    /// **没有**存储优先级下拉框 —— 那是核心本体的属性，投影核心不该替它改；
    /// 玩家要改就到核心本体（或它的面板）上去改。
    /// </summary>
    public class ITab_ProjectedCore : ITab
    {
        private struct Row
        {
            public ThingDef def;
            public ThingDef stuff;
            public int qualityOrdinal;
            public string label;
            public int count;
            public ItemGroup group;
        }

        private static readonly Vector2 WinSize = new Vector2(460f, 540f);

        private readonly bool[] expanded = new bool[6];
        private Vector2 scroll;
        private string search = "";
        private readonly List<Row> rows = new List<Row>();
        private int cachedTick = -1;
        private int cachedCount = -1;
        /// <summary>
        /// ITab 实例是**跨建筑共享**的（InspectTabManager.GetSharedInstance），
        /// 只按 tick+数量判缓存的话，从核心 A 切到核心 B（数量恰好相同）会显示 A 的库存
        /// ⇒ 把核心引用也纳入缓存键。原 mod 的面板没做这一层，这里补上。
        /// </summary>
        private Building_StorageCore cachedCore;

        private Building_CoreProjector Projector => SelThing as Building_CoreProjector;

        /// <summary>只要选中的是投影核心就显示；未绑定时页签里给指引。</summary>
        public override bool IsVisible => Projector != null;

        public ITab_ProjectedCore()
        {
            size = WinSize;
            // 复用原 mod 的页签名（"数字存储"），和核心本体的页签长得一样。
            labelKey = "DS_TabDigitalStorage";
        }

        /// <summary>被绑定（且解析成功）的核心。未绑定为 null。</summary>
        private Building_StorageCore Core => Projector != null ? Projector.BoundCore : null;

        // ---------------------------------------------------------------- 行数据

        private void RebuildRowsIfNeeded(Building_StorageCore core, bool force = false)
        {
            int ticksGame = Find.TickManager.TicksGame;
            ThingOwner held = core.GetDirectlyHeldThings();
            int count = held != null ? held.Count : 0;
            if (!force && ticksGame == cachedTick && count == cachedCount
                && ReferenceEquals(core, cachedCore))
            {
                return;
            }
            cachedTick = ticksGame;
            cachedCount = count;
            cachedCore = core;

            var order = new List<ThingDef>();
            var counts = new Dictionary<ThingDef, int>();
            var labels = new Dictionary<ThingDef, string>();
            var stuffs = new Dictionary<ThingDef, ThingDef>();
            var qualities = new Dictionary<ThingDef, int>();

            for (int i = 0; i < count; i++)
            {
                Thing t = held[i];
                if (t == null || t.def == null || t.Destroyed)
                {
                    continue;
                }
                // ★ 按 def 聚合（不含材质/品质）—— 投影面板是"看库存"，分太细反而难读。
                if (counts.TryGetValue(t.def, out int prev))
                {
                    counts[t.def] = prev + t.stackCount;
                    continue;
                }
                counts[t.def] = t.stackCount;
                labels[t.def] = BuildLabel(t);
                stuffs[t.def] = t.Stuff;
                qualities[t.def] = QualityOrdinalOf(t);
                order.Add(t.def);
            }

            rows.Clear();
            for (int j = 0; j < order.Count; j++)
            {
                ThingDef def = order[j];
                rows.Add(new Row
                {
                    def = def,
                    stuff = stuffs[def],
                    qualityOrdinal = qualities[def],
                    label = labels[def],
                    count = counts[def],
                    group = ItemGrouping.GroupOf(def)
                });
            }
            rows.Sort((Row a, Row b) => string.Compare(a.label, b.label, StringComparison.Ordinal));
        }

        private static int QualityOrdinalOf(Thing t)
        {
            CompQuality q = t.TryGetComp<CompQuality>();
            return q == null ? -1 : (int)q.Quality;
        }

        private static string BuildLabel(Thing t)
        {
            string text;
            try
            {
                text = t.LabelNoCount;
            }
            catch
            {
                text = t.def.LabelCap;
            }
            if (t.def.useHitPoints && t.MaxHitPoints > 0)
            {
                text = text + "  " + GenText.ToStringPercent((float)t.HitPoints / t.MaxHitPoints);
            }
            return text;
        }

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
            for (int i = 0; i < 6; i++)
            {
                int n = 0;
                for (int j = 0; j < rows.Count; j++)
                {
                    if (rows[j].group == (ItemGroup)i && RowMatchesSearch(rows[j]))
                    {
                        n++;
                    }
                }
                if (n == 0)
                {
                    continue;
                }
                num += 28f;
                if (expanded[i])
                {
                    num += n * 26f;
                }
            }
            return num;
        }

        // ---------------------------------------------------------------- 绘制

        protected override void FillTab()
        {
            Building_CoreProjector projector = Projector;
            if (projector == null)
            {
                return;
            }
            Building_StorageCore core = projector.BoundCore;

            Rect rect = GenUI.ContractedBy(new Rect(0f, 0f, WinSize.x, WinSize.y), 10f);
            // ★ 显式设定文字状态：ITab 的绘制环境是共享的，
            //   不设定 WordWrap/Anchor/Font 的话，长文本会被当一行画（溢出后留下零碎的绘制残留）。
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            float y = 0f;

            // ---- 未绑定：给指引，然后结束 ----
            if (core == null)
            {
                TaggedString msg = "DigitalStoragePatch_ProjITab_Unbound".Translate();
                // ★ 高度按实际需要的行数算 —— 之前写死 24f，长文本会溢出/被裁剪，
                //   在文字下面留下一排零碎的白点（v0.16 的实测反馈）。
                float h = Text.CalcHeight(msg, rect.width);
                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, h), msg);
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            // ★★ 必须在画容量条之前重建行：容量条要显示"几种"，而列表高度全部来自 rows。
            //    v0.16 第一版忘了调这一句 ⇒ rows 恒为空 ⇒ CalcListHeight()=0 ⇒ 列表区高度为 0，
            //    表现就是"绑定后只看得到库存条和搜索框"。
            RebuildRowsIfNeeded(core);

            ThingOwner held = core.GetDirectlyHeldThings();
            int count = held != null ? held.Count : 0;
            int maxStacks = core.maxStacks;

            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                "DigitalStoragePatch_ProjITab_BoundTo".Translate(
                    CompCoreName.DescribeCore(core, DsSafe.MapOf(projector))));
            y += 24f;

            Rect bar = new Rect(rect.x, rect.y + y, rect.width, 22f);
            Widgets.FillableBar(bar, maxStacks > 0 ? Mathf.Clamp01((float)count / maxStacks) : 0f);
            Text.Anchor = TextAnchor.MiddleCenter;
            // ★ 别用原 mod 的 DS_CapacityBarLv：它要传 CoreTier.Level，而 CoreTier 是 internal
            //   （Patch_CustomCapacity 里只能反射）。这里自己说"几堆/几种"就够投影面板用了。
            Widgets.Label(bar, "DigitalStoragePatch_ProjITab_Capacity".Translate(
                count, maxStacks, rows.Count));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 26f;

            if (!core.IsUsableNow)
            {
                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                    "DigitalStoragePatch_ProjITab_CoreUnusable".Translate());
            }
            y += 4f;

            // ---- 搜索框 ----
            search = Widgets.TextField(new Rect(rect.x, rect.y + y, rect.width, 24f), search);
            y += 30f;

            // ---- 分组列表 ----
            Rect outRect = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, CalcListHeight());
            Widgets.BeginScrollView(outRect, ref scroll, viewRect, true);

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
                    Rect rowRect = new Rect(12f, cy, viewRect.width - 12f, 24f);
                    if (Mouse.IsOver(rowRect))
                    {
                        Widgets.DrawHighlight(rowRect);
                    }
                    Widgets.ThingIcon(new Rect(rowRect.x, rowRect.y, 22f, 22f),
                        row.def, row.stuff, null, 1f, null, null, 1f);
                    Widgets.Label(new Rect(rowRect.x + 26f, rowRect.y, rowRect.width - 130f, 24f),
                        row.label + "   x" + row.count);
                    if (Widgets.ButtonText(new Rect(rowRect.xMax - 100f, rowRect.y, 100f, 22f),
                        "DS_WithdrawBtn".Translate()))
                    {
                        Row captured = row;
                        Building_CoreProjector proj = projector;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(
                            captured.label, captured.count, 0,
                            delegate (int amount)
                            {
                                ExtractRow(proj, captured, amount);
                            }));
                    }
                    cy += 26f;
                }
            }
            Widgets.EndScrollView();

            // 恢复全局文字状态（Font / Anchor / WordWrap 都是静态字段，检查面板后面的 UI 还要接着画）
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
        }

        // ---------------------------------------------------------------- 取出

        /// <summary>
        /// 把匹配的物品从**被绑定核心**里取出来，放到投影核心脚下并禁用
        /// （禁用是核心面板同款语义：避免刚取出来的东西被自动收纳立刻吸回去）。
        ///
        /// ★ 必须先抓快照再遍历：<see cref="HaulSourceContents.ExtractTo"/> 会把物品从容器移除，
        ///   边遍历边移除会抛 Collection was modified。
        /// ★ 不用 <see cref="HaulSourceContents.ExtractMatchingTo"/>：它走
        ///   <c>FindBest(map)</c> → <c>EnabledSources(map)</c>，而那条路**不含**投影核心
        ///   （投影核心在 v0.5 就不再实现 IHaulSource 了）⇒ 跨图投影会一个都找不到。
        ///   这里直接对被绑定核心的容器逐件 <see cref="HaulSourceContents.ExtractTo"/>，目标明确且跨图可用。
        /// </summary>
        private static void ExtractRow(Building_CoreProjector projector, Row row, int amount)
        {
            if (projector == null || amount <= 0)
            {
                return;
            }
            Building_StorageCore core = projector.BoundCore;
            Map map = DsSafe.MapOf(projector);
            if (core == null || map == null)
            {
                return;
            }
            ThingOwner held = core.GetDirectlyHeldThings();
            if (held == null || held.Count == 0)
            {
                Messages.Message("DS_NoSpaceNearCore".Translate(), projector,
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Thing> snapshot = new List<Thing>();
            for (int i = 0; i < held.Count; i++)
            {
                snapshot.Add(held[i]);
            }

            int taken = 0;
            for (int i = 0; i < snapshot.Count && taken < amount; i++)
            {
                Thing t = snapshot[i];
                if (t == null || t.Destroyed)
                {
                    continue;
                }
                if (t.def != row.def || t.Stuff != row.stuff
                    || QualityOrdinalOf(t) != row.qualityOrdinal)
                {
                    continue;
                }
                int n = Mathf.Min(amount - taken, t.stackCount);
                Thing placed = CoreContentAccess.ExtractFromCoreCrossVersion(t, n, projector.Position, map);
                if (placed == null)
                {
                    break; // 没地方放（ExtractTo 已经把它放回核心）
                }
                if (!placed.Destroyed)
                {
                    ForbidUtility.SetForbidden(placed, true, false);
                }
                taken += n;
            }

            if (taken == 0)
            {
                Messages.Message("DS_NoSpaceNearCore".Translate(), projector,
                    MessageTypeDefOf.RejectInput, false);
            }
            else if (DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 投影核心取出 " + taken + " x " + row.label);
            }
        }
    }
}
