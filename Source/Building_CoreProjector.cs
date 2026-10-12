using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 数字投影核心 —— 把「本图或外图的一个数字存储核心」在本图投影出来。
    ///
    /// 它自己**不存任何东西**，只是一个「指针建筑」：
    /// 把自己的 <see cref="GetDirectlyHeldThings"/> 指向被绑定核心的 <c>ThingOwner</c>。
    ///
    /// 由此几件事一次性成立（大部分寄生在原版与原 mod 的既有路径上）：
    ///
    /// 1. **存（跨图入库）**：原版 <c>HaulAIUtility.HaulToStorageJob</c> 发现目标是
    ///    「有内部容器的 Thing」时走 <c>HaulToContainerJob</c>，落料用
    ///    <c>Container.TryGetInnerInteractableThingOwner()</c> —— 也就是被绑定核心的容器。
    /// 2. **资源统计 / 交易列表**：原 mod 按"本图搬运源"收集内容物；投影核心就是本图的搬运源。
    /// 3. **自动收取**（本类 Tick）：本图散落物品自动吸进被绑定核心
    ///    （原 mod 的自动收纳只会扫核心自己那张图，跨图需要这里补）。
    /// 4. **自动补货**（本类 Tick，可开关）：把核心里的物品补进本图**优先级更高**的储存区
    ///    —— 这是 3.0 的 <c>ItemRouter.RouteCoreToStorage</c>，4.0 被作者整块删掉了。
    ///
    /// 「取」不在本类职责内：4.0 的 <c>HaulSourceContents.FindBestIncludingRemote</c> 已让取料原生跨图。
    ///
    /// ★ 三重守卫（都只为"不让同一批库存被算两次 / 不重复干活"）：
    ///   1. 绑定本图核心 ⇒ 静音（该核心本就在本图工作）；
    ///   2. 本图多个投影绑同一核心 ⇒ 只有主源干活（见 <see cref="ProjectionRegistry"/>）；
    ///   3. 未绑定 / 绑定核心不可用 ⇒ 一切停止。
    ///
    /// ★ 性能：主源判定是**事件驱动**的（生成/销毁/换绑才重算），
    ///   平时只读一个 bool；Tick 里只有取模判断与低频、有预算的扫描，没有每 tick 全图检测。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_CoreProjector : Building, IThingHolder, IHaulDestination
    {
        // ★ 刻意**不实现 IHaulSource**：
        //   原版 ListerHaulables.HaulSourcesCheckTick 会定期对每个搬运源
        //   遍历其全部内容物做 Check()（含 IsInValidBestStorage 的储存搜索）。
        //   投影核心若当搬运源，等于把被绑定核心的全部内容物在**本图**再算一遍，
        //   而且同一核心有 N 个投影时会被算 N 遍 —— 纯浪费。
        //   统计与交易改由本补丁自己的两个补丁点（Patch_ProjectedContentsVisibility）
        //   直接按"每图每核心一次"补齐，行为等价而开销可控。
        // 外观（光效 + 悬浮球体）走 DigitalOrb：复用原 mod 的 2.0/一束光 与 2.0/一个球，
        // 与数字存储核心、数字电池同款，不自带美术资源。

        /// <summary>自动收取节流（tick）。相位再按 thingIDNumber 散列。</summary>
        private const int AutoCollectInterval = 60;

        /// <summary>自动补货节流（tick）。补货不着急，比收取慢一档。</summary>
        private const int AutoRestockInterval = 250;

        /// <summary>被绑定的数字存储核心。跨图引用靠 <c>Scribe_References</c> 持久化。</summary>
        internal Building_StorageCore boundCore;

        /// <summary>被绑定核心的 thingIDNumber：跨图引用万一解析失败时的回退键。</summary>
        private int savedCoreThingId = -1;

        /// <summary>未绑定时的占位 StorageSettings（原版按优先级排序时会读它，不能返回 null）。</summary>
        private StorageSettings mutedSettings;

        /// <summary>回退解析的重试节流（只在解析失败时生效）。</summary>
        private int nextResolveTick = -1;

        private const int ResolveRetryInterval = 600;

        /// <summary>本图绑同一核心的多个投影里，是否为"主源"。由 <see cref="ProjectionRegistry"/> 维护。</summary>
        internal bool isPrimarySource;

        /// <summary>自动收取的轮转游标（多投影/多次扫描错峰用）。</summary>
        private int collectScanOffset;

        /// <summary>自动收取的候选缓冲：必须先把候选收集完再入库，见 TryAutoCollect。</summary>
        private readonly List<Thing> collectBuffer = new List<Thing>();

        // 以下仅用于检查面板展示（零开销，选中时才读）。
        private int lastCollectFound;
        private int lastCollectMoved;
        private int lastRestockMoved;

        // ---------------------------------------------------------------- 绑定状态

        public Building_StorageCore BoundCore
        {
            get
            {
                EnsureResolved();
                return boundCore;
            }
        }

        /// <summary>绑定对象存在且已生成（不要求通电 —— 核心暂时停电不该丢绑定）。</summary>
        public bool IsLinked
        {
            get
            {
                Building_StorageCore c = BoundCore;
                return c != null && c.Spawned && !c.Destroyed;
            }
        }

        /// <summary>投影可用 = 绑定核心存在、已生成、且自身可用（通电等）。</summary>
        public bool IsUsable
        {
            get
            {
                Building_StorageCore c = BoundCore;
                if (c == null || !c.Spawned || c.Destroyed)
                {
                    return false;
                }
                // ★ 僵尸核心：地图集合变动后（重力飞船起飞/落地等），
                //   它的 mapIndex 可能残留一个不存在的下标 —— 读 .Map 会抛异常。
                //   它虽然"还 Spawned"，但已不在任何有效地图上 ⇒ 直接判为不可用。
                if (DsSafe.MapOf(c) == null)
                {
                    return false;
                }
                return c.IsUsableNow;
            }
        }

        /// <summary>同图：本图核心已经把自己暴露成搬运源/目标，投影再暴露一次就会重复计数。</summary>
        internal bool MutedBySameMap => DsSafe.SameMap(BoundCore, this);

        /// <summary>
        /// 自动收取是否可以执行（v0.20）。
        ///
        /// v0.19 及以前同图是**整段静音**的（连收取一起停），用户要求同图也要照样自动收纳，
        /// 所以现在同图是否放行交给设置项 <see cref="DsPatchSettings.collectSameMap"/>（默认**开**）。
        ///
        /// 会不会和核心自带的自动收纳重复干活：两者只是**同一件物品被两个轮转窗口先后看到**，
        /// 先到先得 —— 收走之后另一边的 <c>t.Spawned</c> 已变，会自然跳过，不会重复入库。
        /// 代价只有"多一份有预算的扫描"，想省回来就把那一项关掉。
        /// </summary>
        internal bool IsActiveForCollect
        {
            get
            {
                if (!IsUsable || !isPrimarySource)
                {
                    return false;
                }
                if (!MutedBySameMap)
                {
                    return true; // 跨图：与原来一样照常工作
                }
                DsPatchSettings s = DsPatchMod.Settings;
                // 设置读不到（极端情况）时按"开"处理，保持 v0.20 的新默认行为。
                return s == null || s.collectSameMap;
            }
        }

        /// <summary>本投影是否为"这张图上代表该核心的那个"：可用 + 非同图 + 主源。
        /// （v0.20 起收取不再直接用它，改走 <see cref="IsActiveForCollect"/>。）</summary>
        internal bool IsActiveForMap => IsUsable && !MutedBySameMap && isPrimarySource;

        /// <summary>
        /// 自动补货是否可以执行。与 <see cref="IsActiveForMap"/> 的唯一差别是**同图时是否放行**：
        /// 由设置项 <see cref="DsPatchSettings.restockSameMap"/> 决定（默认关）。
        ///
        /// 为什么补货可以放开到同图而收取不行：
        /// 原 mod 4.0 已删除整套补货机制（3.0 的 ItemRouter），
        /// 所以同图补货不会与任何既有机制重复干活；而收取会被核心自带的自动收纳重复。
        /// </summary>
        internal bool IsActiveForRestock
        {
            get
            {
                if (!IsUsable || !isPrimarySource)
                {
                    return false;
                }
                if (!MutedBySameMap)
                {
                    return true; // 跨图：与原来一样照常工作
                }
                DsPatchSettings s = DsPatchMod.Settings;
                return s != null && s.restockSameMap;
            }
        }

        // ------------------------------------------------- IHaulDestination（只做"投放点"）

        /// <summary>不提供存储页签：筛选与优先级归被绑定核心管（本类直接跟随核心，不另设筛选）。</summary>
        public bool StorageTabVisible => false;

        /// <summary>
        /// 作为本图的搬运目标：让小人能把东西「存」进那张图的核心。
        /// 目标不参与统计/交易，因此**不需要**主源限制——多个投影都能当投放点。
        /// </summary>
        public bool HaulDestinationEnabled => IsUsable && !MutedBySameMap;

        public bool Accepts(Thing t)
        {
            if (!HaulDestinationEnabled || t == null || t.def == null)
            {
                return false;
            }
            return boundCore.Accepts(t);
        }

        public StorageSettings GetStoreSettings()
        {
            Building_StorageCore c = BoundCore;
            if (c != null)
            {
                return c.GetStoreSettings();
            }
            // 原版 AddHaulSource/AddHaulDestination 会按 Priority 排序，这里不能返回 null。
            if (mutedSettings == null)
            {
                mutedSettings = new StorageSettings();
            }
            return mutedSettings;
        }

        /// <summary>筛选直接跟随被绑定核心（玩家在核心上改筛选即可，投影自动同步）。</summary>
        public StorageSettings GetParentStoreSettings()
        {
            Building_StorageCore c = BoundCore;
            return c?.GetParentStoreSettings();
        }

        public void Notify_SettingsChanged()
        {
        }

        // ---------------------------------------------------------------- IThingHolder

        /// <summary>未绑定时的空容器（见 <see cref="GetDirectlyHeldThings"/>）。</summary>
        private ThingOwner emptyOwner;

        /// <summary>
        /// 代理容器：返回被绑定核心的内容物容器；未绑定（或核心容器不可用）时返回**空容器**。
        ///
        /// ★ **绝不能返回 null**：原版 <c>ListerHaulables.Notify_HaulSourceChanged</c> 直接
        /// <c>foreach (holder.GetDirectlyHeldThings())</c>、<c>HaulSourcesCheckTick</c> 直接
        /// <c>.Count</c> —— 返回 null 会**每 tick 抛 NullReferenceException**，
        /// 而 <c>Map.MapPreTick</c> 里 <c>ListerHaulablesTick()</c> 一抛出，整个
        /// <c>DoSingleTick</c> 就中断（`MapPostTick` 不再执行 ⇒ **电源网停止更新**，
        /// 表现为"断电了但建筑照常工作"）。这是本项目踩过的最贵的一个坑。
        /// </summary>
        public ThingOwner GetDirectlyHeldThings()
        {
            Building_StorageCore c = BoundCore;
            if (c != null)
            {
                ThingOwner owned = c.GetDirectlyHeldThings();
                if (owned != null)
                {
                    return owned;
                }
            }
            if (emptyOwner == null)
            {
                emptyOwner = new ThingOwner<Thing>();
            }
            return emptyOwner;
        }

        // Thing 已有 ParentHolder（holdingOwner?.Owner），这里按 IThingHolder 语义改为所在地图。
        // ★ 走 DsSafe：地图下标失效时返回 null，而不是抛异常。
        public new IThingHolder ParentHolder => DsSafe.MapOf(this);

        /// <summary>
        /// 刻意返回空：内容物已经由 <see cref="GetDirectlyHeldThings"/> 暴露，
        /// 若在这里再把核心容器挂为子持有者，遍历会重复枚举（统计翻倍）。
        /// </summary>
        public void GetChildHolders(List<IThingHolder> outChildren)
        {
        }

        // ---------------------------------------------------------------- 绘制

        /// <summary>
        /// 与数字存储核心同款外观：主体贴图来自 Def（复用原 mod 的 2.0/基座），
        /// 这里再叠一层「一束光 + 悬浮球体」并做正弦起伏 —— 即核心那个会动的球。
        /// 仅在绑定有效时绘制（未绑定／核心不可用时不投影，外观上就没有球）。
        /// </summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (!IsLinked)
            {
                return;
            }
            // 与数字存储核心同款外观：光效 + 会起伏的悬浮球（与数字电池共用同一套绘制）。
            DigitalOrb.DrawAt(drawLoc);
        }

        // ---------------------------------------------------------------- 生命周期

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // ★ 每次生成都试一次兜底解析（不再限于读档）：
            //   搬运 / 被重力飞船带着落地后重新生成时，正是靠这里按 thingIDNumber 把绑定找回来。
            //   EnsureResolved 内部有提前返回与节流（失败时每 600 tick 才扫一次），正常情况零开销。
            EnsureResolved();
            // 事件驱动：只在这里登记一次，主源判定随之重算（不做每 tick 扫描）。
            ProjectionRegistry.Register(this);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "dsProjectorCore");
            Scribe_Values.Look(ref savedCoreThingId, "dsProjectorCoreThingId", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureResolved();
            }
        }

        /// <summary>
        /// ★ 这里**不清绑定**（v0.18 修复的"落地后失去绑定"）。
        ///
        /// 旧写法在 <c>Vanish</c> / <c>WillReplace</c> 时把 <c>boundCore</c> 与
        /// <c>savedCoreThingId</c> 一起清掉。可是"搬运 / 重新安装 / 被重力飞船带着走"
        /// 走的**正是**这两种 mode ⇒ 建筑一落地就变成"未绑定"；
        /// 更糟的是 savedCoreThingId 被清成 -1，连按 ID 兜底找回的机会都没了 ⇒ 绑定永久丢失。
        /// 真正该断引用的是**销毁**（见 <see cref="Destroy"/>）。
        /// </summary>
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ProjectionRegistry.Unregister(this);
            base.DeSpawn(mode);
        }

        /// <summary>
        /// 只有真正销毁才断引用 —— 此时本体都没了，留着引用没意义，也避免跨存档拖着旧对象。
        /// （原版 Destroy 内部若仍处生成状态会先调 DeSpawn，那里的 Unregister 是幂等的。）
        /// </summary>
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            ProjectionRegistry.Unregister(this);
            boundCore = null;
            savedCoreThingId = -1;
            base.Destroy(mode);
        }

        // ---------------------------------------------------------------- 每 tick（低频、有预算）

        protected override void Tick()
        {
            base.Tick();

            Building_StorageCore core = BoundCore;
            if (core == null)
            {
                return;
            }
            // ★ 不用 Map：地图集合变动后自身也可能读到失效下标。
            Map map = DsSafe.MapOf(this);
            if (map == null)
            {
                return;
            }
            int tick = Find.TickManager.TicksGame;
            DsPatchSettings s = DsPatchMod.Settings;

            // ---- 自动收取：本图散落物品 → 被绑定核心（跟随核心的自动收纳开关）----
            // v0.20：同图不再一律静音，改由设置项 collectSameMap 决定（默认**开**）
            // ⇒ 投影与核心同图时，投影也会照样自动收纳（用户要求）。
            if (IsActiveForCollect)
            {
                int collectInterval = s != null
                    ? DsPatchSettings.Clamp(s.autoCollectInterval)
                    : AutoCollectInterval;
                if ((tick + thingIDNumber) % collectInterval == 0)
                {
                    TryAutoCollect(core, map);
                }
            }

            // ---- 自动补货：核心 → 本图优先级更高的储存区（补丁设置开关，默认关）----
            // 用 IsActiveForRestock：同图是否放行由设置项 restockSameMap 决定（v0.18）。
            if (s != null && s.autoRestock && IsActiveForRestock)
            {
                int restockInterval = DsPatchSettings.Clamp(s.autoRestockInterval);
                if ((tick + thingIDNumber) % restockInterval == 0)
                {
                    TryAutoRestock(core, map);
                }
            }
        }

        /// <summary>
        /// 自动收取：把本图**归属优先级低于核心**的散落物品吸进被绑定核心。
        ///
        /// 与核心自带的自动收纳同语义（研究等级决定每次 1/5/10 堆、跳过牢房与工作台材料区、
        /// 跳过被预留/刚取出的物品），只是扫描的地图换成**投影核心所在的图**。
        /// 扫描是**轮转窗口 + 有预算**的，不做全图遍历。
        /// </summary>
        private void TryAutoCollect(Building_StorageCore core, Map map)
        {
            if (!core.Powered || !DigitalStorageSettings.autoIngestEnabled)
            {
                return;
            }
            CompAutoIngest autoIngest = core.GetComp<CompAutoIngest>();
            if (autoIngest == null || !autoIngest.Enabled || !autoIngest.IsResearched)
            {
                return;
            }

            ICollection<Thing> candidates = map.listerHaulables?.ThingsPotentiallyNeedingHauling();
            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            // 批量（v0.17）：0 = 跟随核心研究等级（1/5/10，原行为）；>0 = 固定每趟堆数。
            // 扫描窗口始终是批量的 3 倍：批量越大单趟越贵，总开销仍由间隔档位兜住。
            DsPatchSettings batchSettings = DsPatchMod.Settings;
            int batch = batchSettings != null
                ? DsPatchSettings.ClampCollectBatch(batchSettings.autoCollectBatch)
                : 0;
            int budget = batch > 0 ? batch : IngestRateFromResearch();
            int window = budget * 3;
            int count = candidates.Count;
            int start = collectScanOffset % count;
            if (start < 0)
            {
                start = 0;
            }
            collectScanOffset = (start + window) % count;

            int corePriority = (int)core.GetStoreSettings().Priority;

            // ---- 第一遍：**只读**枚举，把候选收进缓冲 ----
            // ★ 关键修复：candidates 是 map.listerHaulables 的 HashSet，
            //   而入库时 t.DeSpawn() 会把它从该集合移除 ⇒ 边遍历边入库会抛
            //   InvalidOperationException("Collection was modified")，
            //   每个扫描周期报一次错并中断整个 Tick（自动补货因此从不执行）。
            //   所以先收集、枚举结束后再入库。
            collectBuffer.Clear();
            int index = 0;
            foreach (Thing t in candidates)
            {
                if (index++ < start)
                {
                    continue;
                }
                if (collectBuffer.Count >= budget || index - start > window)
                {
                    break;
                }
                if (CanAutoCollect(t, map, core, corePriority))
                {
                    collectBuffer.Add(t);
                }
            }
            lastCollectFound = collectBuffer.Count;

            // ---- 第二遍：入库 ----
            int done = 0;
            for (int i = 0; i < collectBuffer.Count; i++)
            {
                Thing t = collectBuffer[i];
                if (t == null || t.Destroyed || !t.Spawned)
                {
                    continue; // 第一遍之后已被别处处理
                }
                if (IngestInto(core, t, map))
                {
                    done++;
                }
            }
            collectBuffer.Clear();
            lastCollectMoved = done;
        }

        private static int IngestRateFromResearch()
        {
            if (ResearchProjectDef.Named("DigitalStorage_AutoIngest3").IsFinished)
            {
                return 10;
            }
            if (ResearchProjectDef.Named("DigitalStorage_AutoIngest2").IsFinished)
            {
                return 5;
            }
            return 1;
        }

        private static bool CanAutoCollect(Thing t, Map map, Building_StorageCore core, int corePriority)
        {
            if (t == null || t.Destroyed || !t.Spawned || t.def == null)
            {
                return false;
            }
            // 只收"当前归属优先级低于核心"的：核心优先级更高才值得吸走。
            if (corePriority <= (int)StoreUtility.CurrentStoragePriorityOf(t))
            {
                return false;
            }
            if (!core.Accepts(t))
            {
                return false;
            }
            // 工作台材料区不收（与核心自带收纳一致）。
            if (t.PositionHeld.GetEdifice(map) is IBillGiver)
            {
                return false;
            }
            // v0.62 上游对齐：跟随原 mod 的「自动收纳避让作业」开关（10 月版新增，默认关；
            // 旧版上游无此字段 ⇒ 视为关）。开启时两条新判据生效（镜像 CompAutoIngest.RejectReason）：
            //   ① 作业目标不收：任何小人的当前 / 排队作业正拿它当目标（targetA/B/C + 两条队列）。
            if (YieldToJobs() && IsTargetOfAnyPawnJob(t, map))
            {
                return false;
            }
            // 牢房不收。
            Room room = t.PositionHeld.GetRoom(map);
            if (room != null && room.IsPrisonCell)
            {
                return false;
            }
            // 被预留 / 刚被取出的不收（复用原 mod 的"刚取出"记录，避免取出来又被吸回去）。
            if (map.reservationManager != null && map.reservationManager.IsReserved(t))
            {
                return false;
            }
            if (CompAutoIngest.IsRecentlyWithdrawn(t))
            {
                return false;
            }
            //   ② 工作台旁宽限：躺在"还有 bill"的工作台 8 邻内、自首次发现起 300 tick 未满
            //     （只放宽一次、不刷新；躺够 5 秒还没人要就照旧收走）。
            if (YieldToJobs() && IsInWaitingBillGiverRim(t, map))
            {
                return false;
            }
            return true;
        }

        // ── v0.62 上游对齐：两条新收纳判据 + 开关解析（镜像自上游 CompAutoIngest，语义逐条对应）──

        private static bool yieldToJobsResolved;
        private static FieldInfo yieldToJobsField;

        /// <summary>原 mod「自动收纳避让作业」开关（10 月版新增；旧版无此字段 ⇒ 视为关）。</summary>
        private static bool YieldToJobs()
        {
            if (!yieldToJobsResolved)
            {
                yieldToJobsResolved = true;
                try
                {
                    yieldToJobsField = AccessTools.Field(typeof(DigitalStorageSettings), "autoIngestYieldToJobs");
                }
                catch
                {
                    yieldToJobsField = null;
                }
            }
            try
            {
                return yieldToJobsField != null && (bool)yieldToJobsField.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>① 这件东西是任何已 spawn 小人"当前 / 排队中"作业的目标。</summary>
        private static bool IsTargetOfAnyPawnJob(Thing t, Map map)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns?.AllPawnsSpawned;
            if (pawns == null || t == null)
            {
                return false;
            }
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn?.jobs == null)
                {
                    continue;
                }
                if (JobTargets(t, pawn.jobs.curJob))
                {
                    return true;
                }
                JobQueue queue = pawn.jobs.jobQueue;
                if (queue == null)
                {
                    continue;
                }
                for (int j = 0; j < queue.Count; j++)
                {
                    if (JobTargets(t, queue[j].job))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool JobTargets(Thing t, Job job)
        {
            if (job == null)
            {
                return false;
            }
            if (job.targetA.Thing == t || job.targetB.Thing == t || job.targetC.Thing == t)
            {
                return true;
            }
            return TargetQueueContains(job.targetQueueA, t) || TargetQueueContains(job.targetQueueB, t);
        }

        private static bool TargetQueueContains(List<LocalTargetInfo> queue, Thing t)
        {
            if (queue == null)
            {
                return false;
            }
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].Thing == t)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly Dictionary<int, int> rimFirstSeen = new Dictionary<int, int>();

        /// <summary>② 躺在"还有 bill"的工作台旁 8 邻内，且宽限（300 tick，自首次发现起）未满。</summary>
        private static bool IsInWaitingBillGiverRim(Thing t, Map map)
        {
            if (!HasWaitingBillGiverAround(t.PositionHeld, map))
            {
                return false;
            }
            return !RimGraceElapsed(t);
        }

        private static bool HasWaitingBillGiverAround(IntVec3 pos, Map map)
        {
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 cell = pos + GenAdj.AdjacentCells[i];
                if (!cell.InBounds(map))
                {
                    continue;
                }
                Building edifice = cell.GetEdifice(map);
                if (edifice != null && !edifice.Destroyed
                    && edifice is IBillGiver billGiver
                    && billGiver.BillStack != null && billGiver.BillStack.Count > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool RimGraceElapsed(Thing t)
        {
            int now = Find.TickManager.TicksGame;
            if (!rimFirstSeen.TryGetValue(t.thingIDNumber, out int firstSeen))
            {
                rimFirstSeen[t.thingIDNumber] = now;
                return false;
            }
            return now - firstSeen >= 300;
        }

        /// <summary>把物品收进核心（语义同原 mod 的 CompAutoIngest.TryIngest）。</summary>
        private static bool IngestInto(Building_StorageCore core, Thing t, Map map)
        {
            IntVec3 oldPos = t.PositionHeld;
            t.DeSpawn();
            if (core.GetDirectlyHeldThings().TryAdd(t))
            {
                core.Notify_SettingsChanged();
                return true;
            }
            GenPlace.TryPlaceThing(t, oldPos, map, ThingPlaceMode.Near);
            return false;
        }

    /// <summary>
    /// 自动补货：把核心里的物品送进本图**存储优先级高于核心**的储存区。
    ///
    /// 这是 3.0 的 <c>ItemRouter.RouteCoreToStorage</c>（原 mod 4.0 把整套机制删掉了，
    /// 所以玩家感觉"3.0 有、4.0 没了"）。语义与原版一致：
    /// 储存区按优先级**降序**遍历，遇到"不高于核心"的就停；每趟默认只搬 1 堆（3.0 语义，
    /// 避免大搬家），v0.17 起批量可在设置里调（<see cref="DsPatchSettings.autoRestockBatch"/>）。
    /// </summary>
    private void TryAutoRestock(Building_StorageCore core, Map map)
    {
        if (!core.Powered)
        {
            return;
        }
        ThingOwner held = core.GetDirectlyHeldThings();
        if (held == null || held.Count == 0)
        {
            return;
        }
        List<SlotGroup> groups = map.haulDestinationManager?.AllGroupsListInPriorityOrder;
        if (groups == null || groups.Count == 0)
        {
            return;
        }
        int corePriority = (int)core.GetStoreSettings().Priority;

        DsPatchSettings st = DsPatchMod.Settings;
        int batch = st != null ? DsPatchSettings.ClampRestockBatch(st.autoRestockBatch) : 1;
        int moved = 0;
        for (int i = held.Count - 1; i >= 0 && moved < batch; i--)
        {
            Thing t = held[i];
            if (t == null || t.Destroyed || t.def == null)
            {
                continue;
            }
            // 3.0 的 flag = core.AllowsDef(def)：核心已经不再接受的物品，
            // 不受"优先级必须高于核心"的限制，会被清到任何接收它的储存区去。
            bool coreAllows = core.Accepts(t);
            if (!TryFindRestockCell(groups, map, t, corePriority, coreAllows, out IntVec3 cell))
            {
                continue;
            }
            held.Remove(t);
            // ★ 必须用 Direct：Near 在目标格放不下时会"就近找地方"，
            //   于是货被挤到储存区外面 —— 那正是玩家看到的现象。
            if (GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Direct))
            {
                core.Notify_SettingsChanged();
                lastRestockMoved++;
                moved++; // 每趟最多 batch 堆（默认 1 = 3.0 语义）
            }
            else
            {
                held.TryAdd(t); // 放不下去就退回核心
            }
        }
    }

        private static bool TryFindRestockCell(
            List<SlotGroup> groups, Map map, Thing t, int corePriority, bool coreAllows, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            for (int g = 0; g < groups.Count; g++)
            {
                SlotGroup sg = groups[g];
                if (sg == null || sg.parent == null)
                {
                    continue;
                }
                // ★ 数字存储自己的内部存储（收货架）绝不是补货目标：
                //   它只进不出（架上 → 核心），一旦它的优先级高于核心，
                //   补货「核心 → 货架」+ 收货「货架 → 核心」就会互相搬运成死循环。
                //   3.0 的 DsCorePriority.IsDigitalStorageInternal 就是干这个的。
                if (Building_ReceivingShelf.IsInternalStorage(sg))
                {
                    continue;
                }
                // 降序：一旦不高于核心，后面的只会更低，直接停（3.0 同款判据）。
                if (coreAllows && (int)sg.Settings.Priority <= corePriority)
                {
                    break;
                }
                if (!sg.Settings.AllowedToAccept(t))
                {
                    continue;
                }
                List<IntVec3> cells = sg.CellsList;
                if (cells == null)
                {
                    continue;
                }
                for (int c = 0; c < cells.Count; c++)
                {
                    IntVec3 candidate = cells[c];
                    if (!candidate.InBounds(map))
                    {
                        continue;
                    }
                    if (CanPlaceFullStack(candidate, map, t))
                    {
                        cell = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 该格能否**完整放下这一整堆**（3.0 `ItemRouter.CanPlaceFullStack` 的等价实现）。
        ///
        /// 语义（照抄 3.0，别凭直觉改）：
        ///   - 格子里若已有**可叠**的同类堆：必须"剩余空间 ≥ 本次要放的堆数"才算行；
        ///     有同类堆但装不下 ⇒ 直接 false（**不拆堆、不硬塞**）；
        ///   - 没有任何同类堆：看格子里的物品件数是否未达 `GetMaxItemsAllowedInCell`。
        ///
        /// 之前用的是 `StoreUtility.IsGoodStoreCell`，它只看"有没有阻挡物"，
        /// 于是储存区满了也会被判定为可用 —— 配合 `ThingPlaceMode.Near`
        /// 就把货挤到了储存区外面。
        /// </summary>
        private static bool CanPlaceFullStack(IntVec3 cell, Map map, Thing item)
        {
            if (map.reservationManager != null
                && map.reservationManager.IsReservedByAnyoneOf(cell, Faction.OfPlayer))
            {
                return false;
            }
            bool sawSameStack = false;
            int itemCount = 0;
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing other = things[i];
                if (other.def.category == ThingCategory.Item)
                {
                    itemCount++;
                }
                if (other.CanStackWith(item))
                {
                    sawSameStack = true;
                    if (other.def.stackLimit - other.stackCount >= item.stackCount)
                    {
                        return true;
                    }
                }
            }
            if (sawSameStack)
            {
                return false;
            }
            return itemCount < cell.GetMaxItemsAllowedInCell(map);
        }

        /// <summary>
        /// 本图存储优先级**严格高于该核心**的储存区数量。
        /// 自动补货只会往这些储存区送东西（3.0 的语义），
        /// 所以这个数字为 0 就是"补货没反应"的最常见原因 —— 面板里直接显示出来。
        /// </summary>
        private static int CountRestockTargets(Building_StorageCore core, Map map)
        {
            List<SlotGroup> groups = map?.haulDestinationManager?.AllGroupsListInPriorityOrder;
            if (groups == null || core == null)
            {
                return 0;
            }
            int corePriority = (int)core.GetStoreSettings().Priority;
            int n = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                SlotGroup sg = groups[i];
                if (sg == null || sg.parent == null)
                {
                    continue;
                }
                // 内部存储（收货架）不是补货目标，也不该算进这个数字（见 TryFindRestockCell）。
                if (Building_ReceivingShelf.IsInternalStorage(sg))
                {
                    continue;
                }
                if ((int)sg.Settings.Priority <= corePriority)
                {
                    break; // 降序，后面的只会更低
                }
                n++;
            }
            return n;
        }

        // ---------------------------------------------------------------- 绑定解析

        public void SetBoundCore(Building_StorageCore core)
        {
            boundCore = core;
            savedCoreThingId = core != null ? core.thingIDNumber : -1;
            nextResolveTick = -1;
            // 事件驱动：绑定变了才重算主源，不做每 tick 检测。
            ProjectionRegistry.NotifyBindingChanged(this);

            if (DsPatchMod.Settings != null && DsPatchMod.Settings.diagnosticsEnabled)
            {
                Log.Message("[数字存储补丁] 投影核心绑定 → "
                            + (core == null ? "(解除)" : Describe(core)));
            }
        }

        /// <summary>
        /// 引用解析兜底：Scribe_References 一般能直接恢复跨图引用，
        /// 万一失败则按 thingIDNumber 在全部已加载地图里找回。
        /// 节流到每 600 tick 最多试一次，且只在解析失败时才会走到。
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
                        ProjectionRegistry.NotifyBindingChanged(this);
                        return;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- UI

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string mine;
            // ★ 不用 Map：地图集合变动后自身也可能读到失效下标（表现为"一选中就报错"）。
            Map myMap = DsSafe.MapOf(this);

            Building_StorageCore c = BoundCore;
            if (c == null)
            {
                mine = (savedCoreThingId >= 0
                    ? "DigitalStoragePatch_Projector_CoreLost".Translate()
                    : "DigitalStoragePatch_Projector_Unbound".Translate()).ToString();
            }
            else if (c.Destroyed)
            {
                mine = "DigitalStoragePatch_Projector_CoreDestroyed".Translate().ToString();
            }
            else if (!c.Spawned)
            {
                mine = "DigitalStoragePatch_Projector_CoreAbsent".Translate().ToString();
            }
            else
            {
                // ★ 被绑定核心可能在"已失效的地图"上，读 c.Map 会抛 ⇒ 一律走 DsSafe。
                Map coreMap = DsSafe.MapOf(c);
                mine = "DigitalStoragePatch_Projector_BoundTo".Translate(Describe(c)).ToString();
                if (coreMap != myMap)
                {
                    mine += "\n" + "DigitalStoragePatch_Projector_CrossMap".Translate(
                        coreMap?.Parent?.Label ?? ("#" + (coreMap?.uniqueID ?? 0))).ToString();
                    if (!isPrimarySource)
                    {
                        mine += "\n" + "DigitalStoragePatch_Projector_NotPrimary".Translate().ToString();
                    }
                }
                else
                {
                    // v0.20：同图不再整段静音 —— 收取默认照常（collectSameMap），补货看设置。
                    DsPatchSettings sm = DsPatchMod.Settings;
                    bool collectOn = sm == null || sm.collectSameMap;
                    mine += "\n" + "DigitalStoragePatch_Projector_SameMap".Translate().ToString();

                    // 补货提示：直接说明"补货为什么没反应"，以及到哪儿开。
                    if (sm != null && sm.autoRestock && !sm.restockSameMap)
                    {
                        mine += "\n"
                            + "DigitalStoragePatch_Projector_RestockSameMapHint".Translate().ToString();
                    }
                    // 收取被手动关掉时也说明一下，避免"同图怎么不收"的困惑。
                    if (!collectOn)
                    {
                        mine += "\n"
                            + "DigitalStoragePatch_Projector_CollectSameMapOffHint".Translate().ToString();
                    }
                }
                if (!c.IsUsableNow)
                {
                    mine += "\n" + "DigitalStoragePatch_Projector_CoreUnusable".Translate().ToString();
                }

                // 运行状况自检（选中才读，零开销）：能直接看出"为什么没动"。
                if (coreMap != myMap)
                {
                    mine += "\n" + "DigitalStoragePatch_Projector_ScanStats".Translate(
                        lastCollectFound, lastCollectMoved).ToString();

                    DsPatchSettings st = DsPatchMod.Settings;
                    bool restockOn = st != null && st.autoRestock;
                    int targets = CountRestockTargets(c, myMap);
                    mine += "\n" + "DigitalStoragePatch_Projector_RestockStats".Translate(
                        (restockOn
                            ? "DigitalStoragePatch_On".Translate()
                            : "DigitalStoragePatch_Off".Translate()),
                        targets, lastRestockMoved).ToString();
                    if (restockOn && targets == 0)
                    {
                        mine += "\n" + "DigitalStoragePatch_Projector_NoRestockHint".Translate().ToString();
                    }
                }
            }

            return baseStr.NullOrEmpty() ? mine : (baseStr + "\n" + mine);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }

            // ★ 不用 Map：选中报错的场景正是这里读到失效下标。
            Map map = DsSafe.MapOf(this);
            if (map == null || Faction != Faction.OfPlayer)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "DigitalStoragePatch_Projector_Bind".Translate(),
                defaultDesc = "DigitalStoragePatch_Projector_BindDesc".Translate(),
                icon = DsIcons.Bind,
                action = OpenBindMenu
            };

            Building_StorageCore bound = BoundCore;
            if (bound == null)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "DigitalStoragePatch_Projector_Unbind".Translate(),
                defaultDesc = "DigitalStoragePatch_Projector_UnbindDesc".Translate(),
                icon = DsIcons.Unbind,
                action = delegate { SetBoundCore(null); }
            };

            // 给被绑定核心改名。★ 放在投影核心上是因为**跨图核心点不到** ——
            //   玩家唯一能稳定选中"那张图上的那个核心"的地方，就是这里的绑定菜单。
            CompCoreName nameComp = bound.GetComp<CompCoreName>();
            if (nameComp != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DigitalStoragePatch_Core_Rename".Translate(),
                    defaultDesc = "DigitalStoragePatch_Core_RenameBoundDesc".Translate(),
                    icon = DsIcons.Rename,
                    action = delegate { Find.WindowStack.Add(new Dialog_RenameCore(nameComp)); }
                };
            }

            // 自动收纳开关：切换**被绑定核心**的 CompAutoIngest.Enabled。
            // 投影核心的"自动收取"跟随该开关（并跟随核心的研究等级与电量）。
            CompAutoIngest autoIngest = bound.GetComp<CompAutoIngest>();
            if (autoIngest != null && autoIngest.IsResearched)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "DS_AutoIngest".Translate(),
                    defaultDesc = "DigitalStoragePatch_Projector_AutoIngestDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("收纳", false),
                    isActive = () => autoIngest.Enabled,
                    toggleAction = delegate { autoIngest.Enabled = !autoIngest.Enabled; }
                };
            }
        }

        private void OpenBindMenu()
        {
            Map map = DsSafe.MapOf(this);
            if (map == null)
            {
                return;
            }

            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            List<Building_StorageCore> seen = new List<Building_StorageCore>();
            Building_StorageCore cur = BoundCore;

            void AddOption(Building_StorageCore c)
            {
                if (c == null || seen.Contains(c))
                {
                    return; // 去重：本图/外图两张列表可能指向同一个核心
                }
                seen.Add(c);
                Map cm = DsSafe.MapOf(c);   // ★ 不能读 c.Map：外图核心可能是"僵尸"
                string where = cm == map
                    ? "DigitalStoragePatch_Projector_ThisMap".Translate()
                    : "DigitalStoragePatch_Projector_OtherMap".Translate(
                        cm?.Parent?.Label ?? ("#" + (cm?.uniqueID ?? 0)));
                opts.Add(MakeOption(c, cur, where));
            }

            // 本图 + 全图：两边都过一遍，靠 seen 去重（全图的列表里也可能含本图核心）
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

            if (opts.Count == 0)
            {
                opts.Add(new FloatMenuOption("DigitalStoragePatch_Projector_NoCores".Translate(), null));
            }
            if (cur != null)
            {
                opts.Add(new FloatMenuOption(
                    "DigitalStoragePatch_Projector_Unbind".Translate(),
                    delegate { SetBoundCore(null); }));
            }

            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private FloatMenuOption MakeOption(Building_StorageCore core, Building_StorageCore current, string where)
        {
            string label = "DigitalStoragePatch_Projector_Option".Translate(Describe(core), where).ToString();
            if (core == current)
            {
                label += " " + "DigitalStoragePatch_Projector_Current".Translate().ToString();
            }
            return new FloatMenuOption(label, delegate { SetBoundCore(core); });
        }

        private static string Describe(Building_StorageCore core)
        {
            // 有名字显示名字（补丁恢复的「核心命名」），没名字就用核心自己的短标签。
            return CompCoreName.DescribeCore(core);
        }
    }
}
