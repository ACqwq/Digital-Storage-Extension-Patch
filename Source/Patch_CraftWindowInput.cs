using System;
using HarmonyLib;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 制作代理界面缺陷修复 ③：「制作自动化」窗口打开时无法调整游戏时间倍速。
    ///
    /// <para><b>根因（原 mod 行为，反编译实锤）</b>：原 mod 的
    /// <c>Window_CraftAutomation</c> 构造函数里设了
    /// <c>absorbInputAroundWindow = true</c>，**却没有配套设 <c>forcePause</c>**
    /// （原版做同类事的 <c>Dialog_BillConfig</c> 是"吸收输入 + 强制暂停"两者一起设，
    /// 自洽：既然暂停了就不需要调速）。</para>
    ///
    /// <para>而原版 <c>WindowStack.HandleEventsHighPriority()</c>（在
    /// <c>UIRootOnGUI</c> 的**最开始**执行，早于地图与底部栏绘制）里有：</para>
    /// <code>
    /// if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.KeyDown)
    ///     &amp;&amp; !GetsInput(null))
    ///     Event.current.Use();        // ← 全局吞掉鼠标/键盘事件
    /// </code>
    /// <para><c>GetsInput(null)</c> 在**任一窗口** <c>absorbInputAroundWindow == true</c>
    /// 时返回 false ⇒ 所有鼠标点击与按键被 <c>Use()</c> 吃掉。结果：游戏**没有**暂停
    /// （时间照走），但底部的时间速度按钮与 1/2/3/4 快捷键**都收不到输入** ——
    /// 表现为"这个界面里没法调整游戏速度"。</para>
    ///
    /// <para><b>修法</b>：在该窗口 <c>PreOpen</c> 时把 <c>absorbInputAroundWindow</c>
    /// 置 false，让它成为**非模态**窗口：时间控制、底部按钮、快捷键恢复正常。
    /// <c>preventCameraMotion</c> 保持原样（仍防止相机被误拖）。</para>
    ///
    /// <para><b>代价（权衡）</b>：非模态后点击窗口外的地图会穿透到游戏世界
    /// （可能取消选择/移动镜头）——这正是原作者设 <c>absorbInputAroundWindow</c> 的本意。
    /// 因此做成开关：默认开（可正常调速），关闭即完全回到原 mod 的模态行为。</para>
    ///
    /// <para><b>挂点说明</b>：挂原版基类虚方法 <c>Window.PreOpen</c>（虚方法不会被内联，
    /// 且原 mod 未重写它），再用**类型名字符串**判断是否为目标窗口 ——
    /// 不新增对本补丁的编译期依赖（原 mod 缺席时只是不生效）。</para>
    /// </summary>
    [HarmonyPatch(typeof(Window), "PreOpen")]
    internal static class Patch_CraftWindowInput
    {
        private static Type craftWindowType;
        private static bool typeResolved;

        [HarmonyPostfix]
        private static void Postfix(Window __instance)
        {
            if (__instance == null)
            {
                return;
            }

            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !s.craftWindowNonModal)
            {
                return;
            }

            try
            {
                Type t = ResolveType();
                if (t == null || !t.IsInstanceOfType(__instance))
                {
                    return;
                }

                if (__instance.absorbInputAroundWindow)
                {
                    __instance.absorbInputAroundWindow = false;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[数字存储补丁] 制作界面非模态化失败（本次保持原行为）："
                              + ex.GetType().Name + ": " + ex.Message, 8830121);
            }
        }

        private static Type ResolveType()
        {
            if (!typeResolved)
            {
                craftWindowType = AccessTools.TypeByName("DigitalStorage.UI.Window_CraftAutomation");
                typeResolved = true;
                if (craftWindowType == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 DigitalStorage.UI.Window_CraftAutomation"
                                + "（原 mod 可能已改结构），制作界面非模态化不生效。");
                }
            }

            return craftWindowType;
        }
    }
}
