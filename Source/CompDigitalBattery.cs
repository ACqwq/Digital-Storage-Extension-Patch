using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// F · 数字电池 —— 移植 3.0 的实现（4.0 完全没有电池）。
    ///
    /// = 原版电池行为 + 免自放电 + 共享数字库（跨图输电）+ 周期双向平衡。
    ///
    /// ── 为什么几乎不用写电网代码（反编译 1.6 核实） ──
    /// · <c>PowerNet.RegisterAllComponentsOf()</c> 用 <c>GetComp&lt;CompPowerBattery&gt;()</c> 收集电池
    ///   ⇒ 本子类**自动**进入 <c>batteryComps</c> 池，充放电完全由原版结算；
    /// · 自放电 5W 硬编码在 <c>CompPowerBattery.CompTick()</c> 里，而 `CompPower` 基类
    ///   **没有** `CompTick` 覆写（已核实）⇒ 覆写 `CompTick` 不调 base 即可干净去掉自放电；
    /// · 容量通过改写 def 的共享 <c>CompProperties_Battery.storedEnergyMax</c> 实现
    ///   （per-def 共享实例，见 <see cref="DsDigitalBatteryManager"/>）。
    ///
    /// ── 共享数字库 ──
    /// 所有数字电池共用一份 Wd 记账（<see cref="DsPowerLedgerComponent"/>，Game 级组件）
    /// ⇒ A 图盈余存入、B 图缺口回填 = **跨图传输电力**。
    /// 每块电池有独立的「自动平衡」开关（Gizmo），关掉就完全不与数字库交互。
    ///
    /// ── 生命周期 ──
    /// Spawn 时注册进 <see cref="DsPowerLedger"/>；被**摧毁**时注销，
    /// 最后一块被摧毁时清空共享账本。缩小搬运 / 换图（`PostDeSpawn`）**不**注销。
    /// </summary>
    public class CompDigitalBattery : CompPowerBattery
    {
        /// <summary>是否已注册进全局注册表（防止 Spawn/Destroy 序列里重复增删）。</summary>
        private bool registered;

        /// <summary>自动平衡开关（每电池独立，Gizmo 切换，随建筑存档）。</summary>
        private bool balanceEnabled = true;

        /// <summary>Wd 的可读格式（≥1000 显示为 kWd）。</summary>
        public static string FormatWd(float wd)
        {
            return wd >= 1000f
                ? (wd / 1000f).ToString("0.##") + " kWd"
                : wd.ToString("0.#") + " Wd";
        }

        // ---------------------------------------------------------------- 存档 / 生命周期

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref balanceEnabled, "dsBatteryBalance", true);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!registered)
            {
                registered = true;
                DsPowerLedger.Register(this);
            }
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            if (registered)
            {
                registered = false;
                // 最后一块被摧毁时，DsPowerLedger.Unregister 内部会清空账本。
                DsPowerLedger.Unregister(this);
            }
            // 注意：PostDeSpawn（缩小搬运 / 换地图）**不**注销 —— 电池本体仍存活。
        }

        // ---------------------------------------------------------------- 每 tick

        public override void CompTick()
        {
            // ★ 不调 base：base 的 CompTick 只做自放电（5W 硬编码），数字电池免除。

            if (parent == null || parent.Destroyed || !parent.Spawned)
            {
                return;
            }
            DsPatchSettings s = DsPatchMod.Settings;
            if (s == null || !balanceEnabled)
            {
                return;
            }

            int interval = DsPatchSettings.ClampBalanceInterval(s.digitalBatteryBalanceInterval);
            if ((Find.TickManager.TicksGame + parent.thingIDNumber) % interval != 0)
            {
                return;
            }

            float max = DsDigitalBatteryManager.Capacity;
            float target = max * DsPatchSettings.ClampTargetPct(s.digitalBatteryTargetPct) / 100f;
            float stored = StoredEnergy;

            if (stored > target + 0.01f)
            {
                // 盈余 → 共享数字库。DrawPower 精确扣除，数额不会超过现有存量。
                float excess = stored - target;
                DrawPower(excess);
                DsPowerLedger.Deposit(excess);
            }
            else if (stored < target - 0.01f && !StunnedByEMP)
            {
                // 不足 → 从共享数字库回填。
                // ★ 下面这段是**能量守恒**的关键：先按"这一拍最多能装多少"夹一次，
                //   取回来之后用"实际涨了多少"对账，多取的立刻退回账本。
                //   否则在 efficiency != 1 或 AmountCanAccept 被夹住时，
                //   取出来但装不进去的部分会**凭空蒸发**。
                float need = target - stored;
                float canAccept = AmountCanAccept;
                if (canAccept <= 0f)
                {
                    return;
                }
                float fromLedger = DsPowerLedger.Withdraw(need < canAccept ? need : canAccept);
                if (fromLedger <= 0f)
                {
                    return;
                }
                float before = StoredEnergy;
                AddEnergy(fromLedger);
                float gained = StoredEnergy - before;
                if (fromLedger - gained > 0.0001f)
                {
                    DsPowerLedger.Deposit(fromLedger - gained);
                }
            }
        }

        // ---------------------------------------------------------------- UI

        public override string CompInspectStringExtra()
        {
            string text = base.CompInspectStringExtra();

            // ★ 原版 CompPowerBattery.CompInspectStringExtra() 在 storedEnergy > 0 时会追加
            //   一行「自放电: 5 W」。我们的 CompTick 刻意不调 base ⇒ 根本没有自放电，
            //   留着这行就是给玩家看**假信息**。
            //   这里按原版自己的拼接方式把那行精确摘掉（同一个翻译键 + 同样的 "F0" 格式，
            //   所以任何语言下都认得出来）。原版若改了那个常量，最坏结果只是这行又出现。
            string selfDischarge = "SelfDischarging".Translate() + ": " + 5f.ToString("F0") + " W";
            if (!text.NullOrEmpty())
            {
                text = text.Replace("\n" + selfDischarge, "");
            }

            DsPowerLedgerComponent ledger = DsPowerLedger.Component;
            if (ledger != null)
            {
                text = text.NullOrEmpty() ? "" : text + "\n";
                text += "DigitalStoragePatch_Battery_Shared"
                    .Translate(FormatWd(ledger.SharedEnergy)).ToString();
            }
            text = text.NullOrEmpty() ? "" : text + "\n";
            text += "DigitalStoragePatch_Battery_Balance".Translate(
                (balanceEnabled
                    ? "DigitalStoragePatch_On".Translate()
                    : "DigitalStoragePatch_Off".Translate())).ToString();
            return text;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (parent == null || parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            yield return new Command_Toggle
            {
                defaultLabel = "DigitalStoragePatch_Battery_BalanceToggle".Translate(),
                defaultDesc = "DigitalStoragePatch_Battery_BalanceToggleDesc".Translate(),
                icon = DsIcons.Power,
                isActive = () => balanceEnabled,
                toggleAction = delegate { balanceEnabled = !balanceEnabled; }
            };
        }
    }

    /// <summary>
    /// 数字电池的容量管理：把设置值写回 def 的**共享** `CompProperties_Battery` 实例。
    ///
    /// `CompProperties` 是每个 Def 一份、被该 Def 的所有实例共用的，所以写一次全场生效。
    /// `storedEnergy` 是**每实例**字段 ⇒ 改 max 不影响已存电量；
    /// 容量调低后超出的部分会在下一次平衡 tick 被推进数字库，不会丢。
    /// </summary>
    public static class DsDigitalBatteryManager
    {
        /// <summary>★ 必须带本补丁前缀：清理版就是按 `DigitalStoragePatch_` 前缀删除物体的。</summary>
        public const string DefName = "DigitalStoragePatch_DigitalBattery";

        /// <summary>当前容量（Wd）。设置缺失时用 5000。</summary>
        public static float Capacity
        {
            get
            {
                DsPatchSettings s = DsPatchMod.Settings;
                return s != null
                    ? DsPatchSettings.ClampBatteryCapacity(s.digitalBatteryCapacity)
                    : 5000f;
            }
        }

        /// <summary>把设置容量写回 def 的共享 `storedEnergyMax`。Defs 加载后调用。</summary>
        public static void ApplyCapacity()
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(DefName, false);
            if (def == null || def.comps == null)
            {
                return;
            }
            for (int i = 0; i < def.comps.Count; i++)
            {
                if (def.comps[i] is CompProperties_Battery props)
                {
                    props.storedEnergyMax = Capacity;
                    return;
                }
            }
        }
    }
}
