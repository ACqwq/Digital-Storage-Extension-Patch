using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 跨图共享数字库（`GameComponent`，RimWorld 在 `Game` 构造时自动创建，随存档持久化）。
    ///
    /// 所有数字电池共用这一份 Wd 记账：A 图电池的盈余存入，B 图电池的缺口从这里回填
    /// —— 这就是「跨图传输电力」，也是 3.0 的原始设计（原 mod 4.0 没有电池，更谈不上这一层）。
    ///
    /// ── 生命周期 ──
    /// 平时什么都不做，只在电池 **Spawn / Destroy** 时增删一个静态注册表（O(1)，无轮询）。
    /// 最后一块数字电池被**摧毁**时清空账本（避免存档里永久残留一份"凭空的电"，
    /// 否则拆光电池再造一块就能白拿电）。
    /// 电池被**缩小搬运 / 换地图**（`PostDeSpawn`）时**不**注销 —— 电池本体仍存活。
    /// </summary>
    public class DsPowerLedgerComponent : GameComponent
    {
        private float sharedEnergy;

        public DsPowerLedgerComponent(Game game)
        {
            // ★ 每次新建/读取一个 Game 都会走到这里（Game.FillComponents），
            //   而静态注册表是进程级的：不清就会把**上一个存档**的电池引用带过来。
            DsPowerLedger.ResetRegistry();
        }

        /// <summary>共享数字库当前余量（Wd）。</summary>
        public float SharedEnergy => sharedEnergy;

        public void Deposit(float amount)
        {
            if (amount > 0f)
            {
                sharedEnergy += amount;
            }
        }

        /// <summary>取电（不超过库存），返回实际取出量。</summary>
        public float Withdraw(float amount)
        {
            if (amount <= 0f)
            {
                return 0f;
            }
            float take = amount < sharedEnergy ? amount : sharedEnergy;
            sharedEnergy -= take;
            return take;
        }

        /// <summary>清空账本（最后一块数字电池被摧毁时调用）。</summary>
        public void Clear()
        {
            sharedEnergy = 0f;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref sharedEnergy, "sharedEnergy", 0f);
            if (sharedEnergy < 0f)
            {
                sharedEnergy = 0f;
            }
        }
    }

    /// <summary>
    /// 数字电池注册表 + 共享数字库访问入口。
    /// 只在电池 Spawn / 销毁时增删；**没有任何轮询检测**。
    /// </summary>
    public static class DsPowerLedger
    {
        private static readonly System.Collections.Generic.List<CompDigitalBattery> alive =
            new System.Collections.Generic.List<CompDigitalBattery>();

        internal static void ResetRegistry()
        {
            alive.Clear();
        }

        public static DsPowerLedgerComponent Component
        {
            get
            {
                Game game = Current.Game;
                return game != null ? game.GetComponent<DsPowerLedgerComponent>() : null;
            }
        }

        public static void Register(CompDigitalBattery battery)
        {
            if (battery != null && !alive.Contains(battery))
            {
                alive.Add(battery);
            }
        }

        public static void Unregister(CompDigitalBattery battery)
        {
            if (battery != null)
            {
                alive.Remove(battery);
            }
            if (CountAlive() == 0)
            {
                // 最后一块数字电池被摧毁 ⇒ 账本清零，存档不再保留余额。
                Component?.Clear();
            }
        }

        /// <summary>
        /// 数一遍仍存活的电池，顺手清掉已被摧毁/为空的残留引用
        /// （整张地图被卸载时 `PostDestroy` 不一定触发，靠这里兜住）。
        /// </summary>
        private static int CountAlive()
        {
            int n = 0;
            for (int i = alive.Count - 1; i >= 0; i--)
            {
                CompDigitalBattery b = alive[i];
                if (b == null || b.parent == null || b.parent.Destroyed)
                {
                    alive.RemoveAt(i);
                    continue;
                }
                n++;
            }
            return n;
        }

        /// <summary>存入共享数字库（组件不可用时静默丢弃 —— 仅理论边界）。</summary>
        public static void Deposit(float amount)
        {
            Component?.Deposit(amount);
        }

        /// <summary>从共享数字库取出，返回实际取出量（组件不可用时返回 0）。</summary>
        public static float Withdraw(float amount)
        {
            DsPowerLedgerComponent ledger = Component;
            return ledger != null ? ledger.Withdraw(amount) : 0f;
        }
    }
}
