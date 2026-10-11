using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 补丁各处按钮用的**原版**贴图。
    ///
    /// ── 为什么是"引用"而不是"把 png 拷进 mod" ──
    /// 原版贴图**全部打包在 Unity 资源里**：`Data/Core` 下只有 `About/ Defs/ Languages/`，
    /// 没有任何散装 png（贴图在 `RimWorldWin64_Data/resources.assets` 里）。
    /// 所以 mod 想用原版图标，标准做法就是引用 —— 原版自己也是这么用的
    /// （例：所有改名按钮都走 `TexButton.Rename`，见 `RimWorld.RenameUIUtility`）。
    /// 引用还有个好处：原版以后换图，我们自动跟着换，不会留下一份抄来的旧图。
    ///
    /// 每个图标都是**一次性懒加载 + 取不到就给 null**（不设图标的 Command 会正常画成纯文字按钮），
    /// 绝不让按钮本身消失。
    /// </summary>
    internal static class DsIcons
    {
        private static Texture2D bind;
        private static bool bindTried;
        private static Texture2D unbind;
        private static bool unbindTried;

        /// <summary>
        /// 「绑定核心」。用原版「链接存储设置」的链环图标
        /// （`RimWorld.StorageGroupUtility` 的 LinkTex，同路径）。
        /// </summary>
        internal static Texture2D Bind
        {
            get
            {
                if (!bindTried)
                {
                    bindTried = true;
                    bind = ContentFinder<Texture2D>.Get("UI/Commands/LinkStorageSettings", false);
                    if (bind == null)
                    {
                        Log.Warning("[数字存储补丁] 找不到 UI/Commands/LinkStorageSettings，"
                                    + "「绑定核心」按钮将只显示文字。");
                    }
                }
                return bind;
            }
        }

        /// <summary>「改为自动 / 解除绑定」。用原版「解除链接存储设置」图标。</summary>
        internal static Texture2D Unbind
        {
            get
            {
                if (!unbindTried)
                {
                    unbindTried = true;
                    unbind = ContentFinder<Texture2D>.Get("UI/Commands/UnlinkStorageSettings", false);
                }
                return unbind;
            }
        }

        /// <summary>
        /// 「重命名核心」。**就是原版改名按钮用的那张**（`Verse.TexButton.Rename`
        /// = `UI/Buttons/Rename`，被 `RenameUIUtility` 的四个 DrawRenameButton 共用）。
        /// </summary>
        internal static Texture2D Rename => TexButton.Rename;

        /// <summary>「自动平衡」开关。复用原版「需要电力」图标。</summary>
        internal static Texture2D Power => TexCommand.DesirePower;
    }
}
