using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// C. 数字收货架 —— 一个只收货、不发货的**原版货架**。
    ///
    /// ── 为什么用 <see cref="Building_Storage"/> ──
    /// 它是真正的槽组（SlotGroup）建筑 ⇒ 殖民者与各类搬运 mod 会像对待普通货架一样
    /// 把物品放上来，不需要我们写任何"让小人来放东西"的 AI。
    /// 本类只负责一件事：**把架上的物品按间隔转进数字核心，从不反向取出。**
    ///
    /// ── 准入与数字核心一致（`功能要求.md` #5） ──
    /// <c>Building_Storage.GetStoreSettings()/Accepts()</c> 都是**非 virtual**（已核实 1.6 反编译），
    /// 不能 override。所以走两条：
    ///   1. 建成 / 读档时把核心的 <c>ThingFilter</c> 抄到货架上
    ///      （<c>ThingFilter.CopyAllowancesFrom</c> 是原版的完整 1:1 拷贝）；
    ///   2. 核心的筛选被改动时**事件驱动**地重新抄一次 ——
    ///      挂 <c>Building_StorageCore.Notify_SettingsChanged()</c> 的 Postfix
    ///      （<c>StorageSettings</c> 构造时把自己的 filter 回调接在这里，改筛选必然触发）。
    ///      见 <see cref="Patch_ReceivingShelfFilter"/>。
    /// 未绑定核心时用"物品类、排除尸体"兜底 —— 与原 mod <c>Building_StorageCore.GetParentFilterPublic()</c>
    /// 的规则一致。
    ///
    /// ── 入库复用原 mod 自己的判定 ──
    /// 每个候选物品先过 <c>Building_StorageCore.Accepts(t)</c>（筛选 + 容量 + 是否已在本核心里，
    /// 一次判完），通过才 <c>DeSpawn()</c> 后 <c>GetDirectlyHeldThings().TryAdd()</c>。
    /// 这是原 mod <c>DigitalDropRedirect.TryIngestUnspawned</c> 的做法（该类是 internal，
    /// 无法直接引用，但用的全是 public API，逻辑等价）。
    ///
    /// ── 性能 ──
    /// 每 <see cref="DsPatchSettings.receivingScanInterval"/> tick 扫一次，相位按 thingIDNumber 散列。
    /// <c>SlotGroup.HeldThings</c> 是 **yield 迭代器**（边枚举边改动地图会抛 Collection was modified），
    /// 所以先把候选抓进本类的复用缓冲再搬 —— 缓冲只在本建筑 tick 时使用，无并发问题。
    /// </summary>
    /// <summary>
    /// 收货架"转移一件"的内建分段计时（只在设置里打开「诊断日志」时才走）。
    ///
    /// ★ 为什么用内建计时而不是探针：探针是给**方法**挂 Harmony 包装器，
    ///   而这里要拆分的是**一个方法体内**的四段（极小的方法体会被 Mono 内联，
    ///   探针会静默失效 —— 本项目踩过这个坑）。内建计时代码就在调用点上，不受内联影响。
    ///
    /// 背景：旧存档报告里 `收货架.转移单件` 6 次调用吃掉 391 ms（均 65 ms、峰值 65.5 ms），
    /// 六次一样慢 ⇒ 是"每次必然发生"的开销，必须定位到具体哪一段。
    /// </summary>
    internal static class DsShelfTiming
    {
        internal enum Stage
        {
            Accepts = 0,   // core.Accepts(t)：筛选 + 容量（满仓时会遍历核心全部条目）
            Detach = 1,    // t.DeSpawn()：从地图/货架上摘下来
            Attach = 2,    // innerContainer.TryAdd(t, true)：并入核心容器
            Notify = 3,    // core.Notify_SettingsChanged()：通知搬运源/目的地变化
        }

        internal static readonly long[] Ms = new long[4];
        internal static int Items;

        internal static bool On
        {
            get
            {
                DsPatchSettings s = DsPatchMod.Settings;
                return s != null && s.diagnosticsEnabled;
            }
        }

        internal static long Stamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>记一段并返回新的时间戳（stamp 为 0 表示未开启计时，此时原样返回）。</summary>
        internal static long Mark(Stage stage, long stamp)
        {
            if (stamp == 0L)
            {
                return 0L;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Ms[(int)stage] += now - stamp;
            return now;
        }

        internal static void Clear()
        {
            for (int i = 0; i < Ms.Length; i++)
            {
                Ms[i] = 0L;
            }
            Items = 0;
        }

        internal static string Describe()
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return "判定 " + (Ms[0] * f).ToString("N1")
                   + " / 摘除 " + (Ms[1] * f).ToString("N1")
                   + " / 加入 " + (Ms[2] * f).ToString("N1")
                   + " / 通知 " + (Ms[3] * f).ToString("N1")
                   + " ms（" + Items + " 件）";
        }
    }

    public class Building_ReceivingShelf : Building_Storage
    {
        /// <summary>
        /// 该槽组是否属于"数字存储自身的内部存储"（目前只有收货架）。
        ///
        /// ★ 存在的意义：**必须把内部存储排除在补货目标之外**。
        ///   收货架只进不出（架上 → 核心），如果它的存储优先级又被设得比核心高，
        ///   核心的自动补货就会把货送到货架、货架再收回核心 —— 两者互相搬运，形成死循环。
        ///   3.0 的 `DsCorePriority.IsDigitalStorageInternal` 就是干这个的。
        ///   以后再加同类"只进不出"的内部建筑时，只要在这里加一条就行。
        /// </summary>
        public static bool IsInternalStorage(SlotGroup group)
        {
            return group != null && group.parent is Building_ReceivingShelf;
        }

        /// <summary>扫描间隔默认值（设置项 missing 时的兜底）。</summary>
        private const int DefaultScanInterval = 30;

        /// <summary>被手动指定的核心；null = 自动（本图 / 可见的可用核心里轮转）。</summary>
        internal Building_StorageCore boundCore;

        /// <summary>被绑定核心的 thingIDNumber：跨图引用万一解析失败时的回退键（同投影核心）。</summary>
        private int savedCoreThingId = -1;

        /// <summary>回退解析的重试节流（只在解析失败时生效）。</summary>
        private int nextResolveTick = -1;

        private const int ResolveRetryInterval = 600;

        /// <summary>轮转游标：多个候选核心时摊平负载。</summary>
        private int rrIndex;

        /// <summary>候选快照缓冲（复用，避免每次扫描都新建 List）。</summary>
        private readonly List<Thing> heldBuffer = new List<Thing>();

        /// <summary>候选核心缓冲（复用）。</summary>
        private readonly List<Building_StorageCore> coreBuffer = new List<Building_StorageCore>();

        /// <summary>
        /// 本轮扫描里"因核心装不下而被拒"的物品 def。
        ///
        /// ★ 为什么安全（v0.34，配合 maxItemsInCell 提到 30）：
        ///   `Building_StorageCore.Accepts` 只有在 `innerContainer.Count >= maxStacks`（核心满仓）时
        ///   才会跑 `HasRoomInExistingStack(t)` —— 那是**遍历核心全部条目**（最高 3000 项）。
        ///   上限提到 30 后，最坏情况是 30 × 3000 次迭代/轮/货架，必须兜住。
        ///
        ///   而对同一个 def，只要本轮没有成功转入过该 def 的物品（转入成功就不会被记进来），
        ///   `HasRoomInExistingStack` 的结果就不会变：
        ///     · 被拒 ⇒ 核心里没有可堆叠的同类堆；
        ///     · 转入别的 def 不会凭空造出一个"可堆叠的同类堆"；
        ///     · 核心满仓这个前提在本轮内也不会改变（转入是往已有堆里加，不新增堆数）。
        ///   ⇒ 同一个 def 记一次就够，后面直接跳过。语义完全不变，只是少跑重复的 O(n) 扫描。
        /// </summary>
        private readonly HashSet<ThingDef> spaceBlockedDefs = new HashSet<ThingDef>();

        /// <summary>供只读诊断累加的统计（见 <see cref="Patch_HaulCompatDiag"/>）。</summary>
        internal static int DiagMoved;
        internal static int DiagBlockedBySpace;
        internal static int DiagBlockedByFilter;

        /// <summary>本轮扫描里收到过物品的核心（用来**每轮只发一次** Notify_SettingsChanged）。</summary>
        private readonly List<Building_StorageCore> notifiedCores = new List<Building_StorageCore>();

        /// <summary>
        /// 全场收货架登记表（SpawnSetup / DeSpawn 维护，数量通常只有几个到十几个）。
        /// 供 <see cref="Patch_AvoidCoreDirectHaul"/> 判断"本图有没有收货架能收这件东西"。
        /// </summary>
        private static readonly List<Building_ReceivingShelf> registry = new List<Building_ReceivingShelf>();

        /// <summary>
        /// 本图有没有收货架愿意收这件物品（给"核心让位"做前置判断）。
        /// 列表很短，线性扫描即可；不做任何缓存，避免与建筑增删不同步。
        /// </summary>
        internal static bool AnyShelfCanAccept(Map map, Thing t)
        {
            if (map == null || t == null)
            {
                return false;
            }
            for (int i = 0; i < registry.Count; i++)
            {
                Building_ReceivingShelf shelf = registry[i];
                if (shelf == null || shelf.Destroyed || shelf.Map != map || !shelf.Spawned)
                {
                    continue;
                }
                if (shelf.Accepts(t))
                {
                    return true;
                }
            }
            return false;
        }

        // 仅用于检查面板（选中才读，零开销）。
        private int lastMoved;
        private int lastLeft;
        private int lastBlockedByFilter;
        private int lastBlockedBySpace;

        /// <summary>手动绑定的核心（解析兜底后）。null = 自动。</summary>
        internal Building_StorageCore BoundCore
        {
            get
            {
                EnsureResolved();
                return boundCore;
            }
        }

        /// <summary>无核心可用时的兜底筛选：物品类、排除尸体（与原 mod 的核心父筛选同规则）。</summary>
        private static ThingFilter fallbackFilter;

        private static ThingFilter FallbackFilter
        {
            get
            {
                if (fallbackFilter == null)
                {
                    ThingFilter f = new ThingFilter();
                    List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
                    for (int i = 0; i < all.Count; i++)
                    {
                        ThingDef d = all[i];
                        if (d != null && d.category == ThingCategory.Item && !d.IsCorpse)
                        {
                            f.SetAllow(d, true);
                        }
                    }
                    fallbackFilter = f;
                }
                return fallbackFilter;
            }
        }

        // ---------------------------------------------------------------- 生命周期

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!registry.Contains(this))
            {
                registry.Add(this);
            }
            EnsureResolved();
            SyncFilterFromCore();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "dsShelfCore");
            Scribe_Values.Look(ref savedCoreThingId, "dsShelfCoreThingId", -1);
            Scribe_Values.Look(ref rrIndex, "dsShelfRrIndex", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && boundCore != null)
            {
                savedCoreThingId = boundCore.thingIDNumber;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // ★ 无论哪种模式都要出表：销毁/拆除/换图都会走到这里，
            //   留在表里会让"核心让位"误判为"还有收货架可用"（物品就没人搬了）。
            registry.Remove(this);
            // 只断引用，不销毁/修改核心本体（与投影核心一致）。
            if (mode == DestroyMode.Vanish || mode == DestroyMode.WillReplace)
            {
                boundCore = null;
                savedCoreThingId = -1;
            }
            base.DeSpawn(mode);
        }

        // ---------------------------------------------------------------- 每 tick（有节流）

        protected override void Tick()
        {
            base.Tick();

            DsPatchSettings s = DsPatchMod.Settings;
            int interval = s != null
                ? DsPatchSettings.Clamp(s.receivingScanInterval)
                : DefaultScanInterval;
            if ((Find.TickManager.TicksGame + thingIDNumber) % interval != 0)
            {
                return;
            }

            // ★ 搬运诊断的"心跳"在这里给（v0.35a）。
            //   原来它只在"架上有东西"的分支里调用 ⇒ 架子空着 + 没人找仓库时**完全不输出**，
            //   表现为"有时静默、有时一直弹"，看起来毫无规律 —— 其实只是"当前没有搬运活动"。
            //   放在节流之后、所有 early-return 之前 ⇒ 只要有**任意一个收货架存在**，
            //   每 600 tick 必定有一行（活动为 0 时那一行全是 0），"静默"就只能意味着"没建收货架"。
            Patch_HaulCompatDiag.MaybeFlush();

            Map map = Map;
            if (map == null)
            {
                return;
            }
            SlotGroup group = GetSlotGroup();
            if (group == null)
            {
                return;
            }

            // ★ HeldThings 是 yield 迭代器：先抓快照，再边搬边改地图，否则抛 Collection was modified。
            heldBuffer.Clear();
            foreach (Thing t in group.HeldThings)
            {
                heldBuffer.Add(t);
            }
            if (heldBuffer.Count == 0)
            {
                lastMoved = 0;
                lastLeft = 0;
                lastBlockedByFilter = 0;
                lastBlockedBySpace = 0;
                return;
            }

            CollectCores(map);

            // ★ 每次扫描都把筛选重新抄一遍（见 SyncFilterFromCore 的说明）：
            //   不依赖"谁改了筛选"的事件，保证"货架能放什么"永远等于"核心愿意收什么"。
            //   代价 = 一次 HashSet→List 拷贝 + 一次单格重算，几十个货架也可以忽略。
            SyncFilterFromCore();

            int moved = 0;
            int blockedByFilter = 0;
            int blockedBySpace = 0;
            spaceBlockedDefs.Clear();
            notifiedCores.Clear();
            for (int i = 0; i < heldBuffer.Count; i++)
            {
                Thing t = heldBuffer[i];
                if (t == null || t.Destroyed || t.stackCount <= 0)
                {
                    continue;
                }
                // ★ 本轮已确认"核心装不下这个 def" ⇒ 直接跳过，不再跑昂贵的 Accepts（见字段注释）。
                if (spaceBlockedDefs.Contains(t.def))
                {
                    blockedBySpace++;
                    continue;
                }

                Building_StorageCore core = PickCore(t);
                if (core == null)
                {
                    // 区分两种"搬不走"，玩家一眼就能看出该改什么：
                    //   筛选不收  ⇒ 去核心上改筛选（正常不该出现，因为货架筛选跟着核心走）
                    //   核心装不下 ⇒ 等核心腾地方 / 加容量
                    if (AnyCoreAllowsFilter(t))
                    {
                        blockedBySpace++;
                        spaceBlockedDefs.Add(t.def);
                    }
                    else
                    {
                        blockedByFilter++;
                    }
                    continue;
                }
                if (Transfer(core, t))
                {
                    moved++;
                }
            }
            // ★ 本轮收过货的核心，**每个只通知一次**（原来是在 Transfer 里逐件通知，见那里的注释）。
            //
            // ★★ v0.36：默认**跳过**这次通知。
            //   探针实测单次通知要 ~61 ms，因为原 mod 的 Notify_SettingsChanged →
            //   ListerHaulables.Notify_HaulSourceChanged 会**遍历核心全部内容物、逐件跑 Check**。
            //   而原版每 tick 的 HaulSourcesCheckTick → RecalculateAllInHaulSource
            //   是**逐字相同**的实现 ⇒ 立刻再体检一遍是纯重复。
            //   省掉它最多让新入库物品晚一轮（通常 ≤1 tick）被体检，影响可忽略。
            //   详见 DsPatchSettings.shelfSkipIngestNotify。
            DsPatchSettings settings = DsPatchMod.Settings;
            bool skipNotify = settings == null || settings.shelfSkipIngestNotify;
            if (!skipNotify)
            {
                for (int i = 0; i < notifiedCores.Count; i++)
                {
                    Building_StorageCore c = notifiedCores[i];
                    if (c == null || c.Destroyed)
                    {
                        continue;
                    }
                    bool timed = DsShelfTiming.On;
                    long stamp = timed ? DsShelfTiming.Stamp() : 0L;
                    c.Notify_SettingsChanged();
                    if (timed)
                    {
                        DsShelfTiming.Mark(DsShelfTiming.Stage.Notify, stamp);
                    }
                }
            }
            notifiedCores.Clear();

            lastMoved = moved;
            lastLeft = heldBuffer.Count - moved;
            lastBlockedByFilter = blockedByFilter;
            lastBlockedBySpace = blockedBySpace;

            // 只读诊断用（默认关闭时这两个字段没人读，累加本身是一次加法，可忽略）。
            DiagMoved += moved;
            DiagBlockedBySpace += blockedBySpace;
            DiagBlockedByFilter += blockedByFilter;

            if (moved > 0 && DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 数字收货架入库 " + moved + " 堆 @" + Position
                            + " ｜ 耗时 " + DsShelfTiming.Describe());
            }
        }

        /// <summary>
        /// 把一件物品送进核心。失败必须放回地面（否则物品凭空消失）。
        ///
        /// ★ 本方法**不发** <c>Notify_SettingsChanged</c>（v0.35c 改动）。
        ///   原实现是"每送进一件就通知一次"，而旧存档探针显示单次转移要 ~65 ms
        ///   （6 次共 391 ms）—— 六次一样慢 ⇒ 是每次必然发生的开销，头号嫌疑就是这个通知
        ///   （它会扇出到 `ListerHaulables.Notify_HaulSourceChanged` 与
        ///   `HaulDestinationManager.Notify_HaulDestinationChangedPriority` 两处全局列表）。
        ///   一轮扫描里送进 N 件就付 N 次，毫无必要 ⇒ 改为**每轮扫描对每个收到货的核心只发一次**。
        /// </summary>
        private bool Transfer(Building_StorageCore core, Thing t)
        {
            bool timed = DsShelfTiming.On;
            long stamp = timed ? DsShelfTiming.Stamp() : 0L;

            // 先判：核心愿意收（筛选 + 容量 + 是否已在本核心里），不通过就不动它。
            if (!core.Accepts(t))
            {
                if (timed)
                {
                    DsShelfTiming.Mark(DsShelfTiming.Stage.Accepts, stamp);
                }
                return false;
            }
            if (timed)
            {
                stamp = DsShelfTiming.Mark(DsShelfTiming.Stage.Accepts, stamp);
            }

            IntVec3 pos = t.PositionHeld;
            t.DeSpawn();
            if (timed)
            {
                stamp = DsShelfTiming.Mark(DsShelfTiming.Stage.Detach, stamp);
            }

            if (core.GetDirectlyHeldThings().TryAdd(t, true))
            {
                if (timed)
                {
                    DsShelfTiming.Mark(DsShelfTiming.Stage.Attach, stamp);
                    DsShelfTiming.Items++;
                }
                if (!notifiedCores.Contains(core))
                {
                    notifiedCores.Add(core);
                }
                return true;
            }

            if (timed)
            {
                DsShelfTiming.Mark(DsShelfTiming.Stage.Attach, stamp);
            }
            // 理论到不了这里（Accepts 已放行），但绝不吞物品。
            GenPlace.TryPlaceThing(t, pos, Map, ThingPlaceMode.Near);
            Log.Warning("[数字存储补丁] 数字收货架入库失败，物品已放回原地：" + t.LabelShort);
            return false;
        }

        // ---------------------------------------------------------------- 选核

        /// <summary>收集本图当前可用核心（已包含"本图投影核心授权的跨图核心"，见可见性补丁）。</summary>
        private void CollectCores(Map map)
        {
            coreBuffer.Clear();
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores == null)
            {
                return;
            }
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore c = cores[i];
                if (c != null && CoreFinder.IsUsable(c))
                {
                    coreBuffer.Add(c);
                }
            }
        }

        /// <summary>手动绑定的核心优先；不可用或装不下时退回自动轮转。</summary>
        private Building_StorageCore PickCore(Thing t)
        {
            Building_StorageCore manual = BoundCore;
            if (manual != null && manual.Spawned && !manual.Destroyed && manual.Accepts(t))
            {
                return manual;
            }

            int n = coreBuffer.Count;
            for (int k = 0; k < n; k++)
            {
                Building_StorageCore c = coreBuffer[(rrIndex + k) % n];
                if (c == null || c == manual || !c.Accepts(t))
                {
                    continue;
                }
                rrIndex = (rrIndex + k + 1) % n;
                return c;
            }
            return null;
        }

        /// <summary>
        /// 有没有哪个候选核心的**筛选**是允许收它的（不考虑容量）。
        /// 用来把"筛选不收"和"核心装不下"分开 —— <c>Accepts()</c> 把这两件事混在一个 bool 里。
        /// </summary>
        private bool AnyCoreAllowsFilter(Thing t)
        {
            Building_StorageCore manual = BoundCore;
            if (manual != null && manual.Spawned && !manual.Destroyed && AllowsFilter(manual, t))
            {
                return true;
            }
            for (int i = 0; i < coreBuffer.Count; i++)
            {
                if (AllowsFilter(coreBuffer[i], t))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AllowsFilter(Building_StorageCore core, Thing t)
        {
            if (core == null)
            {
                return false;
            }
            StorageSettings ss = core.GetStoreSettings();
            return ss != null && ss.AllowedToAccept(t);
        }

        /// <summary>
        /// 引用解析兜底：Scribe_References 一般能恢复跨图引用，
        /// 万一失败则按 thingIDNumber 在全部已加载地图里找回。节流 600 tick，只在失败时走到。
        /// </summary>
        private void EnsureResolved()
        {
            if (boundCore != null || savedCoreThingId < 0)
            {
                return;
            }
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (now < nextResolveTick)
            {
                return;
            }
            nextResolveTick = now + ResolveRetryInterval;

            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                List<IHaulSource> sources = maps[i]?.haulDestinationManager?.AllHaulSourcesListForReading;
                if (sources == null)
                {
                    continue;
                }
                for (int j = 0; j < sources.Count; j++)
                {
                    if (sources[j] is Building_StorageCore c
                        && c.thingIDNumber == savedCoreThingId
                        && c.Spawned && !c.Destroyed)
                    {
                        boundCore = c;
                        return;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 准入同步

        /// <summary>
        /// 把核心的筛选抄到本货架（"可存放物品与数字核心一致"）。
        ///
        /// ★ 两个必须做对的地方（v0.12 第一次实测就是栽在这两点上）：
        ///   1. **自动模式下要用"当前实际会收货的那个核心"的筛选**，而不是内置兜底筛选 ——
        ///      否则核心排除某类物品时货架照样收，物品永远躺在架上进不去；
        ///   2. **每次扫描都重抄**，不依赖"谁改了筛选"的事件。
        ///      代价很小（`CopyAllowancesFrom` 是一次 `HashSet→List` 拷贝 + 一个很小的列表分配），
        ///      换来的保证是：玩家在**核心**上改筛选，最长 <see cref="DsPatchSettings.receivingScanInterval"/>
        ///      tick 后货架就跟上，而且玩家在**货架**上瞎勾也不会破坏一致性。
        ///      （事件驱动的那条路仍然保留，见 <see cref="Patch_ReceivingShelfFilter"/>，用于即时响应。）
        /// </summary>
        internal void SyncFilterFromCore()
        {
            StorageSettings mine = GetStoreSettings();
            if (mine == null || mine.filter == null)
            {
                return;
            }

            Building_StorageCore core = ResolveFilterSource();

            ThingFilter src = null;
            StoragePriority? wantPriority = null;
            if (core != null)
            {
                StorageSettings theirs = core.GetStoreSettings();
                if (theirs != null)
                {
                    src = theirs.filter;
                    wantPriority = theirs.Priority;
                }
            }
            if (src == null)
            {
                src = FallbackFilter;
            }

            // ★★ 优先级必须与核心**相同**（v0.35，用户四轮实测定案，见报告 31 §11 / §12）。
            //   原版规则：物品已在某储存区里 ⇒ 它的 currentPriority = 那个区的优先级，
            //   只有**严格更高**的目标才算"更好的存储"（StoreUtility 与报告 19 均已核实）。
            //   · 货架 < 核心（原状，Normal 2 < 优先 3）⇒ 架上的货永远处于"还能进更好的地方"
            //     ⇒ 一直留在原版待搬表里 ⇒ HD / 原版反复询问、反复尝试搬去核心
            //     ⇒ 实测 `核心容器` 命中暴涨到 492/610/443，HD 则报 net-zero（搬了又回原地）。
            //   · 货架 > 核心 ⇒ 反过来：小人把核心里的东西搬到货架上，形成循环（用户实测）。
            //   · 货架 == 核心 ⇒ 架上的货被判为"已在最好的存储里" ⇒ **退出待搬表** ⇒ 轮询消失。
            //     用户实测这一档"没有搬运诊断弹出，只有收货架的日志"，且他自己的存档长期如此。
            //   只在真的不同时才写（set 会触发 Notify_SettingsChanged 重算槽组）。
            if (wantPriority.HasValue && mine.Priority != wantPriority.Value)
            {
                mine.Priority = wantPriority.Value;
            }

            if (src == null || ReferenceEquals(src, mine.filter))
            {
                return;
            }
            // 原版的完整 1:1 拷贝（含特殊筛选/血量/品质范围），比 SetAllowAll 更彻底。
            mine.filter.CopyAllowancesFrom(src);
        }

        /// <summary>
        /// 筛选要跟随的核心：手动绑定的优先；否则本图第一个可用核心（= 自动收货会用的那个）。
        /// 一个都没有时返回 null（调用方回落到内置兜底筛选）。
        /// </summary>
        private Building_StorageCore ResolveFilterSource()
        {
            Building_StorageCore manual = BoundCore;
            if (manual != null && manual.Spawned && !manual.Destroyed)
            {
                return manual;
            }
            Map map = Map;
            if (map == null)
            {
                return null;
            }
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores == null)
            {
                return null;
            }
            for (int i = 0; i < cores.Count; i++)
            {
                if (cores[i] != null && CoreFinder.IsUsable(cores[i]))
                {
                    return cores[i];
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- UI

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string mine;

            Building_StorageCore manual = BoundCore;
            if (manual != null)
            {
                mine = "DigitalStoragePatch_Shelf_BoundTo".Translate(Describe(manual)).ToString();
                if (!manual.Spawned || manual.Destroyed)
                {
                    mine += "\n" + "DigitalStoragePatch_Shelf_CoreLost".Translate().ToString();
                }
                else if (!manual.IsUsableNow)
                {
                    mine += "\n" + "DigitalStoragePatch_Shelf_CoreUnusable".Translate().ToString();
                }
                else if (!DsSafe.SameMap(manual, this))
                {
                    // ★ 不能读 manual.Map：外图核心可能是"僵尸"（地图下标已失效）。
                    Map mm = DsSafe.MapOf(manual);
                    mine += "\n" + "DigitalStoragePatch_Shelf_CrossMap".Translate(
                        mm?.Parent?.Label ?? ("#" + (mm?.uniqueID ?? 0))).ToString();
                }
            }
            else
            {
                mine = "DigitalStoragePatch_Shelf_Auto".Translate().ToString();
            }

            mine += "\n" + "DigitalStoragePatch_Shelf_Stats".Translate(
                lastMoved, lastLeft, lastBlockedByFilter, lastBlockedBySpace).ToString();
            if (lastBlockedByFilter > 0)
            {
                mine += "\n" + "DigitalStoragePatch_Shelf_FilterHint".Translate().ToString();
            }
            return baseStr.NullOrEmpty() ? mine : (baseStr + "\n" + mine);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }

            Map map = Map;
            if (map == null || Faction != Faction.OfPlayer)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "DigitalStoragePatch_Shelf_Bind".Translate(),
                defaultDesc = "DigitalStoragePatch_Shelf_BindDesc".Translate(),
                icon = DsIcons.Bind,
                action = OpenBindMenu
            };

            if (BoundCore != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DigitalStoragePatch_Shelf_Unbind".Translate(),
                    defaultDesc = "DigitalStoragePatch_Shelf_UnbindDesc".Translate(),
                    icon = DsIcons.Unbind,
                    action = delegate { SetBoundCore(null); }
                };
            }
        }

        private void SetBoundCore(Building_StorageCore core)
        {
            boundCore = core;
            savedCoreThingId = core != null ? core.thingIDNumber : -1;
            SyncFilterFromCore();
        }

        private void OpenBindMenu()
        {
            Map map = Map;
            if (map == null)
            {
                return;
            }

            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption(
                "DigitalStoragePatch_Shelf_AutoOption".Translate(), delegate { SetBoundCore(null); }));

            List<Building_StorageCore> seen = new List<Building_StorageCore>();
            Building_StorageCore cur = BoundCore;

            void AddOption(Building_StorageCore c)
            {
                if (c == null || seen.Contains(c))
                {
                    return;
                }
                seen.Add(c);
                Map cm = DsSafe.MapOf(c);   // ★ 不能读 c.Map：外图核心可能是"僵尸"
                string where = cm == map
                    ? "DigitalStoragePatch_Projector_ThisMap".Translate()
                    : "DigitalStoragePatch_Projector_OtherMap".Translate(
                        cm?.Parent?.Label ?? ("#" + (cm?.uniqueID ?? 0)));
                string label = "DigitalStoragePatch_Projector_Option".Translate(Describe(c), where).ToString();
                if (c == cur)
                {
                    label += " " + "DigitalStoragePatch_Projector_Current".Translate().ToString();
                }
                opts.Add(new FloatMenuOption(label, delegate { SetBoundCore(c); }));
            }

            List<Building_StorageCore> local = CoreFinder.AllUsableCores(map);
            if (local != null)
            {
                for (int i = 0; i < local.Count; i++)
                {
                    AddOption(local[i]);
                }
            }
            List<Building_StorageCore> all = CoreFinder.AllUsableCoresGlobal();
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    AddOption(all[i]);
                }
            }

            if (seen.Count == 0)
            {
                opts.Add(new FloatMenuOption("DigitalStoragePatch_Projector_NoCores".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static string Describe(Building_StorageCore core)
        {
            // 有名字显示名字（补丁恢复的「核心命名」），没名字就用核心自己的短标签。
            return CompCoreName.DescribeCore(core);
        }
    }
}
