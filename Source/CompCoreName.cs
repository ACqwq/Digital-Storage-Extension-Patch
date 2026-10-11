using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// D 项之外的「核心命名」恢复 —— 4.0 把 3.0 的网络命名功能**整块删掉了**
    /// （全库已核实：`NetworkName` / `RenamableLabel` / `Dialog_RenameNetwork` / 重命名 gizmo 一个都不剩）。
    ///
    /// ── 为什么不往原程序集里加字段 ──
    /// 原 mod 的 `Building_StorageCore` 在别的程序集里，我们加不了字段。
    /// 而 RimWorld 给这类需求准备的标准答案就是 <see cref="ThingComp"/>：
    /// 用一条 XML Patch 把本组件挂到 `DigitalStorage_StorageCore` 的 `<comps>` 上
    /// （见 `Patches/CoreName.xml`），于是：
    ///   · 名字随 Thing 一起存档（`PostExposeData`）；
    ///   · 免费获得一个 gizmo 槽（<see cref="CompGetGizmosExtra"/>）；
    ///   · <see cref="TransformLabel"/> 会被 `ThingWithComps.LabelNoCount` 自动调用
    ///     ⇒ 命名后整张地图上这个核心的显示名都会变；
    ///   · <see cref="CompInspectStringExtra"/> 会在检查面板里多一行。
    ///
    /// ── 与原 mod 的耦合度 ──
    /// 只依赖一个 Def 名（`DigitalStorage_StorageCore`）与"它是 ThingWithComps"这个事实，
    /// 不碰它的任何私有成员。Def 改名的极端情况下 XML Patch 会失败并留一条日志，
    /// 游戏本身不受影响（补丁的其它功能照常）。
    /// </summary>
    public class CompProperties_CoreName : CompProperties
    {
        public CompProperties_CoreName()
        {
            compClass = typeof(CompCoreName);
        }
    }

    /// <summary>给数字存储核心起名的组件（4.0 缺失功能的恢复）。</summary>
    public class CompCoreName : ThingComp
    {
        /// <summary>玩家给核心起的名字。空 = 未命名。</summary>
        private string coreName;

        public bool HasName => !string.IsNullOrEmpty(coreName);

        public string CoreName => coreName;

        /// <summary>
        /// 本组件是否挂在**真正的核心**上。
        /// Def 的 `<comps>` 会被 RimWorld 派生到 `..._Blueprint` / `..._Frame` 等 Def 上，
        /// 那些不是核心 —— 不加这道判断的话，右键蓝图也会弹出「重命名核心」。
        /// </summary>
        private bool IsCore => parent is Building_StorageCore;

        // ---------------------------------------------------------------- 对外取值

        /// <summary>取某个 Thing 上的名字；没有名字（或没有本组件）返回 null。</summary>
        public static string NameOf(ThingWithComps t)
        {
            if (t == null)
            {
                return null;
            }
            CompCoreName c = t.GetComp<CompCoreName>();
            return c != null && c.HasName ? c.coreName : null;
        }

        /// <summary>
        /// UI 里描述一个核心：**有名字就先显示名字**，再带上坐标与（跨图时的）地图。
        /// 投影核心 / 收货架的绑定菜单与检查面板统一走这里，避免各处各写一套。
        /// </summary>
        public static string DescribeCore(Building_StorageCore core, Map relativeTo = null)
        {
            if (core == null)
            {
                return "?";
            }
            IntVec3 p = core.PositionHeld;
            string name = NameOf(core);
            string label = name ?? core.LabelShort;
            string where = "(" + p.x + ", " + p.z + ")";
            // ★ 不能读 core.Map：核心可能停留在一张"已失效的地图"上（地图集合变动后），
            //   那时读 .Map 抛的是 ArgumentOutOfRangeException，而不是返回 null。
            Map coreMap = DsSafe.MapOf(core);
            if (relativeTo != null && coreMap != null && coreMap != relativeTo)
            {
                where += " @ " + (coreMap.Parent?.Label ?? ("#" + coreMap.uniqueID));
            }
            return label + " " + where;
        }

        /// <summary>设置名字。空字符串等同于清除命名。</summary>
        public void SetName(string name)
        {
            coreName = string.IsNullOrEmpty(name) ? null : name.Trim();
        }

        // ---------------------------------------------------------------- 原版扩展点

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref coreName, "dsCoreName");
        }

        /// <summary>
        /// 命名后整个核心的显示名变成「名字 (原标签)」。
        /// 由 <c>ThingWithComps.LabelNoCount</c> / <c>GetCustomLabelNoCount</c> 自动调用。
        /// 未命名时原样返回（保证"不打补丁 == 原行为"）。
        /// </summary>
        public override string TransformLabel(string label)
        {
            return IsCore && HasName ? (coreName + " (" + label + ")") : label;
        }

        public override string CompInspectStringExtra()
        {
            if (!IsCore || !HasName)
            {
                return null;
            }
            return "DigitalStoragePatch_Core_NameIs".Translate(coreName).ToString();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!IsCore || parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            yield return new Command_Action
            {
                defaultLabel = "DigitalStoragePatch_Core_Rename".Translate(),
                defaultDesc = "DigitalStoragePatch_Core_RenameDesc".Translate(),
                // 原版所有改名按钮用的就是这张（RenameUIUtility → TexButton.Rename）。
                icon = DsIcons.Rename,
                action = OpenRenameDialog
            };
        }

        private void OpenRenameDialog()
        {
            Find.WindowStack.Add(new Dialog_RenameCore(this));
        }
    }

    /// <summary>
    /// 重命名对话框。照 3.0 的 <c>Dialog_RenameNetwork</c> 写（原版没有可直接复用的通用改名窗口：
    /// <c>Dialog_Rename</c> 是抽象基类，只给它自家的 Zone/建筑用）。
    /// </summary>
    public class Dialog_RenameCore : Window
    {
        private readonly CompCoreName comp;
        private string curName;

        public override Vector2 InitialSize => new Vector2(320f, 180f);

        public Dialog_RenameCore(CompCoreName comp)
        {
            this.comp = comp;
            curName = comp != null ? comp.CoreName : null;
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                "DigitalStoragePatch_Core_RenamePrompt".Translate());
            curName = Widgets.TextField(new Rect(0f, 35f, inRect.width, 35f), curName ?? "");
            Widgets.Label(new Rect(0f, 74f, inRect.width, 24f),
                "DigitalStoragePatch_Core_RenameHint".Translate());

            float y = inRect.height - 35f;
            float half = inRect.width / 2f - 5f;
            if (Widgets.ButtonText(new Rect(0f, y, half, 35f),
                    "DigitalStoragePatch_Confirm".Translate()))
            {
                comp?.SetName(curName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(inRect.width / 2f + 5f, y, half, 35f),
                    "DigitalStoragePatch_Cancel".Translate()))
            {
                Close();
            }
        }
    }
}
