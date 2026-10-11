using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 数字电池的建筑本体：除了 Def 里的基座贴图，再叠一层与数字核心同款的
    /// 「一束光 + 悬浮球体」（见 <see cref="DigitalOrb"/>）。
    ///
    /// 行为逻辑一行都不在这里 —— 充放电全走原版 <c>CompPowerBattery</c> / <c>PowerNet</c>，
    /// 数字库平衡在 <see cref="CompDigitalBattery"/>。
    /// </summary>
    public class Building_DigitalBattery : Building
    {
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            DigitalOrb.DrawAt(drawLoc);
        }
    }
}
