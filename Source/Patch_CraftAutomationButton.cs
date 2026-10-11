using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 隐藏 / 显示底部菜单栏的「制作自动化」按钮（原 mod 的
    /// <c>MainButtonDef DigitalStorage_CraftAutomation</c>）。
    ///
    /// ── 为什么需要"每帧同步"（v0.15 翻车的教训） ──
    /// 参考实现 Dubs Mint Menus 是把
    /// `def.buttonVisible = Settings.ShowMintBillsButton;` 写在
    /// `MainButtonWorker.Disabled` 的 getter 里 —— 那是 `MainButtonsRoot.DoButtons()`
    /// **每帧**都会调的地方，所以开关当场生效。
    /// 我们不改原 mod 的 workerClass（那要动别人的 Def），
    /// 改成在设置界面里**每帧**调 <see cref="Apply"/>，效果等价；
    /// 外加启动时调一次，覆盖"没开过设置窗口"的情况。
    ///
    /// ── 为什么**不需要 Harmony** ──
    /// <c>MainButtonDef.buttonVisible</c> 是一个普通 public bool 字段，
    /// 而 <c>MainButtonWorker.Visible</c> 直接返回它；并且
    /// <c>MainButtonsRoot.DoButtons()</c> **每一帧**都会重新读一次 <c>Worker.Visible</c>
    /// 来决定画哪些按钮（反编译 1.6 核实）。
    /// ⇒ 改这一个字段就是全部工作，改完下一帧生效、无需重启。
    ///
    /// 窗口本身不受影响：<c>MainButtonWorker_CraftAutomation.Activate()</c> 只是
    /// <c>Window_CraftAutomation.OpenOrFocus(null)</c>，同一个面板在制作代理建筑上也能打开
    /// —— 用户正是冲着这一点才要求把底部按钮藏起来的。
    ///
    /// ── 关闭按钮时顺手关掉已打开的窗口 ──
    /// 该窗口不是 <c>MainTabWindow</c>（是普通 <c>Window</c>），
    /// 所以 <c>MainTabsRoot.OpenTab</c> 不会追踪它，<c>EscapeCurrentTab</c> 对它无效；
    /// 用 <c>WindowStack.TryRemove(Type)</c> 按类型移除。
    /// </summary>
    internal static class Patch_CraftAutomationButton
    {
        /// <summary>原 mod 的主按钮 defName（`Defs/MainButtons_Craft.xml`）。</summary>
        private const string DefName = "DigitalStorage_CraftAutomation";

        /// <summary>原 mod 的制作自动化窗口类型（直接引用，原 mod 是硬依赖）。</summary>
        private static readonly System.Type windowType = typeof(DigitalStorage.UI.Window_CraftAutomation);

        /// <summary>把设置同步到主按钮。启动时与设置界面每帧各调用一次。</summary>
        internal static void Apply()
        {
            DsPatchSettings s = DsPatchMod.Settings;
            bool want = s == null || s.craftAutomationButton;

            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamed(DefName, false);
            if (def == null)
            {
                return;
            }

            if (def.buttonVisible == want)
            {
                return;
            }

            def.buttonVisible = want;

            // 关掉按钮的那一刻，把它已经打开的窗口也收起来（否则窗口悬空、只剩它一个）。
            if (!want && windowType != null && Find.WindowStack != null)
            {
                try
                {
                    Find.WindowStack.TryRemove(windowType, false);
                }
                catch (System.Exception)
                {
                    // 窗口本来就没开 / 正在关闭：忽略。
                }
            }

            if (s != null && s.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 「制作自动化」底部按钮已" + (want ? "显示" : "隐藏") + "。");
            }
        }
    }
}
