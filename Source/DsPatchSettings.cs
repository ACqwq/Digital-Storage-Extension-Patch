using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 「谁优先」策略（`功能要求.md` #3）。
    /// </summary>
    public enum DsPriorityMode
    {
        /// <summary>原 mod 逻辑（**默认**）：完全不动 priorityInType。</summary>
        ModDefault = 0,

        /// <summary>工作优先级置底：把原 mod 的 giver 排到原版同类工作之下。</summary>
        VanillaFirst = 1,
    }

    /// <summary>
    /// 补丁自有设置。刻意不往原作者的 DigitalStorageSettings 里塞字段
    /// （那需要改动原程序集，且无法安全扩展它的私有序列化布局）。
    /// </summary>
    public class DsPatchSettings : ModSettings
    {
        /// <summary>诊断日志开关。默认关闭。</summary>
        public bool diagnosticsEnabled = false;

        /// <summary>
        /// 收货架入库后**不再**立刻通知书系统做全量体检（默认**开** = 采用省性能的那一档）。
        ///
        /// ★ 为什么（v0.36，探针实测见报告 31 §16/§17）：
        ///   原 mod 的 `Building_StorageCore.Notify_SettingsChanged()` 会调
        ///   `ListerHaulables.Notify_HaulSourceChanged(core)`，其实现是
        ///   **遍历核心全部内容物、逐件跑一次 `Check`（= 完整的"是否需搬运"判定）**。
        ///   实测：核心物品多的存档里，**单次通知要 ~61 ms**（分段计时实测：
        ///   判定 0.0 / 摘除 0.9 / 加入 0.4 / **通知 61.4** ms），
        ///   而同一窗口内 `收货架.Tick` 的峰值 131 ms 就是它。
        ///
        ///   关键在于：**原版每 tick 本来就在做同一件事** ——
        ///   `ListerHaulables.HaulSourcesCheckTick` → `RecalculateAllInHaulSource(source)`
        ///   是**与 `Notify_HaulSourceChanged` 逐字相同**的实现（都遍历内容物逐件 `Check`）。
        ///   ⇒ 入库后立刻再体检一遍是纯重复劳动，省掉它最多只让"新入库物品"
        ///     晚一轮（通常 ≤1 tick）被体检，对"核心让位/自动补货"的影响可忽略。
        ///
        /// 关闭 = 回到"入库后立刻通知"的原行为。
        /// </summary>
        public bool shelfSkipIngestNotify = true;

        /// <summary>
        /// Better Architect Menu 兼容（默认**开**）：让建筑菜单的**地板页**与**默认选材**
        /// 也认数字核心里的材料（v0.38，报告 33）。
        ///
        /// BAM 把"殖民地有没有这个材料"实现成"**地图上有没有已生成**的这个物品"
        /// （`listerThings.ThingsOfDef(x).Any()`），而核心内容物是**未生成**的
        /// ⇒ 材料只在核心时，该材料的地板分类根本不出现 ⇒ 建造不了地板。
        ///
        /// 开启后只**追加**"地图上没有、但核心里有"的材料，不改 BAM 的任何原有判断。
        /// 关闭 = 原版行为（BAM 自身的不一致保持原样）。
        /// </summary>
        public bool bamMaterialsFromCore = true;

        /// <summary>用电豁免（默认关）：开启后数字存储建筑始终认为自己已通电。</summary>
        public bool powerExempt = false;

        /// <summary>
        /// 搬运兼容（默认关）：有可用的**数字收货架**时，不再让数字核心作为直接搬运目的地。
        ///
        /// ★ 为什么要这个开关（v0.35，实测见报告 31 §11 / §12）：
        ///   数字核心是**单个容器**，一次只放行一个小人互动（底层限制，改不了）。
        ///   而 Pick Up And Haul / Hauler's Dream 这类搬运 mod 的批量搬运
        ///   只认 `HaulToCell`（格子），不接管 `HaulToContainer`（容器）
        ///   ⇒ 只要小人还在往核心直接搬，就退化成"一次一堆、一人独享"。
        ///   开启后：核心会在"本图有可用收货架"时让位，小人们改投收货架
        ///   （每格一个独立投料位、可并行、搬运 mod 的批量搬运能生效），
        ///   再由收货架转进核心。
        ///
        /// ★ 让位是有条件的：只在**确实有收货架愿意收这件物品**时才让，
        ///   否则照旧走核心，避免"没人搬这件东西"。
        /// </summary>
        public bool avoidCoreDirectHaul = false;

        /// <summary>
        /// 性能缓解（v0.20 起默认**关**）：跳过原版对"核心搬运源"的每 tick 全量重算。
        /// 实测占 77.5% 帧时间。
        ///
        /// ★ 为什么改成默认关：它跳过的 <c>ListerHaulables.RecalculateAllInHaulSource</c>
        ///   同时负责**把不该再搬的东西从待搬表里移除**。核心是物理容器，吸进核心的物品
        ///   正是靠这条路径退出待搬表；跳过之后它们会留在表里，
        ///   而原 mod 自动收纳的候选池**就是**这张待搬表（<c>ThingsPotentiallyNeedingHauling</c>）
        ///   ⇒ 候选池被核心内容物占满、轮转窗口（rate×3）扫不到真正的散落物品
        ///   ⇒ 表现为"装了补丁后数字核心的自动收纳就不生效，卸载就正常"。
        ///   4.0.1 且原 mod 的「性能优化」开着时本项本来就自动让位（行为无差别）；
        ///   现在默认关 ⇒ 不打补丁与打补丁在这一点上完全一致。
        ///   确实有 77.5% 帧时间问题的旧版用户可自行开启。
        /// </summary>
        public bool skipCoreHaulRecalc = false;

        /// <summary>
        /// 修复「清空废料容器」job 刷屏（默认**开**）。
        /// 原版会把非槽组存储（数字核心/投影核心）当最优目标却丢掉它，导致 job 目标格无效、
        /// 预约必失败 ⇒ 日志刷屏且废料清不出去。
        /// </summary>
        public bool fixWasteEmptyJob = true;

        /// <summary>
        /// 自动补货间隔（tick，10–600；默认 250）。
        /// 每趟搬 <see cref="autoRestockBatch"/> 堆（默认 1），所以间隔越小铺满越快、开销越大。
        /// </summary>
        public int autoRestockInterval = 250;

        /// <summary>投影核心自动收取间隔（tick，10–600；默认 60）。</summary>
        public int autoCollectInterval = 60;

        /// <summary>
        /// 自动收取每趟最多收几堆（0 = 跟随核心研究等级 1/5/10，**默认**；1–50 = 固定批量）。
        /// v0.17：扫描窗口是批量的 3 倍（<c>window = budget * 3</c>），批量越大单趟越贵，摊到 tick 仍受间隔控制。
        /// </summary>
        public int autoCollectBatch = 0;

        /// <summary>
        /// 自动补货每趟最多搬几堆（1–20，**默认 1** = 3.0 原版语义「每趟只搬一堆，避免大搬家」）。
        /// 调大 = 铺满更快，但单趟 `TryFindRestockCell` × N 次的格子扫描也更贵。
        /// </summary>
        public int autoRestockBatch = 1;

        /// <summary>自动收取批量上限（0 = 跟随研究等级，不算在内）。</summary>
        public const int MaxCollectBatch = 50;

        /// <summary>自动补货批量上限。</summary>
        public const int MaxRestockBatch = 20;

        public static int ClampCollectBatch(int v)
        {
            if (v < 0)
            {
                return 0;
            }
            return v > MaxCollectBatch ? MaxCollectBatch : v;
        }

        public static int ClampRestockBatch(int v)
        {
            if (v < 1)
            {
                return 1;
            }
            return v > MaxRestockBatch ? MaxRestockBatch : v;
        }

        /// <summary>数字收货架扫描间隔（tick，10–600；默认 30）。每趟尽量把架上物品全转走。</summary>
        public int receivingScanInterval = 30;

        /// <summary>
        /// 自动补货（默认关）：投影核心把被绑定核心里的物品，送进**本图优先级高于核心**的储存区。
        /// 这是 3.0 的 ItemRouter.RouteCoreToStorage，原 mod 4.0 已删除该机制。
        /// </summary>
        public bool autoRestock = false;

        /// <summary>
        /// 同图投影也执行自动补货（默认**关**）。
        ///
        /// 背景：投影核心与它绑定的核心在同一张图时会被**静音**
        /// （避免统计翻倍与重复干活，见 Building_CoreProjector 的「三重守卫」），
        /// 于是「核心 → 本图更高优先级储存区」这条补货链在同图场景完全拿不到。
        ///
        /// 但静音的理由只对**收取**成立（同图时原 mod 的自动收纳已经在收了）；
        /// 原 mod 4.0 已删除整套补货机制，同图不存在"重复干活" ⇒ 放开补货是安全的。
        /// </summary>
        public bool restockSameMap = false;

        /// <summary>
        /// 同图投影也执行自动收取（v0.20 起，默认**开**）。
        ///
        /// 投影核心与它绑定的核心在同一张图时，v0.19 及以前会把投影**整段静音**
        /// （连收取一起停）。用户要求：同图时投影也要照样自动收纳。
        ///
        /// 重复干活的顾虑：同图时原 mod 核心自己的自动收纳也在收 —— 但两者只是
        /// **同一件物品被两个轮转窗口先后看到**，先到先得（收走后另一边会跳过），
        /// 不会重复入库，代价只是多一份有预算的扫描开销。想要省回来就把本项关掉。
        /// </summary>
        public bool collectSameMap = true;

        /// <summary>
        /// 制作代理跨图取料的范围（默认**关** = 只认投影核心）。
        /// 关闭：只有「本图有投影核心绑定了该核心」时，制作代理才会跨图取料 —— 由玩家显式授权。
        /// 开启：跟随原 mod 自己的跨图口径，任何可用核心都能供料（原 mod 的
        /// <c>RemoteIngredientProvider</c> 本来就是这么找料的）。
        /// </summary>
        public bool billCrossMapAnyCore = false;

        // ───────────────────────── D. 存储容量自定义 ─────────────────────────

        /// <summary>
        /// 存储容量自定义（默认**关**）。关闭时**完全不碰**原 mod 的容量表。
        /// 开启后按下方四个数值覆盖对应科技等级的核心容量（单位：堆）。
        /// </summary>
        public bool customCapacityEnabled = false;

        /// <summary>Lv1（未研究任何核心科技）容量，单位：堆。默认 500 = 原 mod 原值。</summary>
        public int capLevel1 = 500;

        /// <summary>Lv2（研究 DigitalStorage_CoreLevel2）容量。</summary>
        public int capLevel2 = 1000;

        /// <summary>Lv3 容量。</summary>
        public int capLevel3 = 1500;

        /// <summary>Lv4 容量。</summary>
        public int capLevel4 = 3000;

        /// <summary>
        /// 容量数值上限。刻意**不设下界**（用户要求"自己设的值自己承担"），
        /// 但负数会被夹到 0；上限只是为了防止手滑输入把 ITab 撑爆。
        /// </summary>
        public const int MaxCapacity = 1000000;

        public static int ClampCapacity(int v)
        {
            if (v < 0)
            {
                return 0;
            }
            return v > MaxCapacity ? MaxCapacity : v;
        }

        /// <summary>取某个科技等级（1–4）的容量。越界时退回 Lv1。</summary>
        public int CapForLevel(int level)
        {
            switch (level)
            {
                case 2: return capLevel2;
                case 3: return capLevel3;
                case 4: return capLevel4;
                default: return capLevel1;
            }
        }

        // ───────────────────────── E. 工作优先级 ─────────────────────────

        /// <summary>
        /// 「谁优先」策略。默认 <see cref="DsPriorityMode.ModDefault"/> = 完全不改原 mod 行为。
        /// </summary>
        public DsPriorityMode priorityMode = DsPriorityMode.ModDefault;

        // ───────────────────────── F. 数字电池 ─────────────────────────

        /// <summary>数字电池容量（Wd）。写回 def 的共享 <c>CompProperties_Battery.storedEnergyMax</c>。</summary>
        public int digitalBatteryCapacity = 5000;

        /// <summary>平衡线（容量百分比，20–80）。高于它 → 盈余存入数字库；低于它 → 从数字库回填。</summary>
        public int digitalBatteryTargetPct = 50;

        /// <summary>平衡周期（tick，30–10000；默认 250）。相位按电池 ID 散列。</summary>
        public int digitalBatteryBalanceInterval = 250;

        public const int BatteryCapacityMin = 100;
        public const int BatteryCapacityMax = 1000000;
        public const int TargetPctMin = 20;
        public const int TargetPctMax = 80;
        public const int BalanceIntervalMin = 30;
        public const int BalanceIntervalMax = 10000;

        public static int ClampBatteryCapacity(int v)
        {
            if (v < BatteryCapacityMin)
            {
                return BatteryCapacityMin;
            }
            return v > BatteryCapacityMax ? BatteryCapacityMax : v;
        }

        public static int ClampTargetPct(int v)
        {
            if (v < TargetPctMin)
            {
                return TargetPctMin;
            }
            return v > TargetPctMax ? TargetPctMax : v;
        }

        public static int ClampBalanceInterval(int v)
        {
            if (v < BalanceIntervalMin)
            {
                return BalanceIntervalMin;
            }
            return v > BalanceIntervalMax ? BalanceIntervalMax : v;
        }

        // ───────────────────────── G. 交易 ─────────────────────────

        /// <summary>
        /// 远行队 / 据点交易使用数字核心库存（默认**关** = 原 mod 行为）。
        /// 3.0 靠"终端芯片植入物"做门槛，4.0 没有对应物，
        /// 而且这类交易发生在世界地图上、没有"当前地图"可用，所以用开关而不是投影核心判据。
        /// </summary>
        public bool tradeFromCore = false;

        /// <summary>
        /// 交易付款可以从数字核心扣银（默认**开**，v0.37）。
        ///
        /// ★ 修的是「读」与「扣」两条链路的不一致（见 Patch_TradeLaunchFromCore 的类注释）：
        ///   「读」(<c>AllLaunchableThingsForTrade</c>) 已被原 mod 补成"含核心内容物"，
        ///   而「扣」(<c>LaunchThingsOfType</c>) 是**另一套独立实现**，只扫信标范围内的地面。
        ///   ⇒ 界面显示银够、一付款却扣不到核心里的银（并刷
        ///     <c>Could not find any Silver to transfer to trader.</c> 红字）。
        ///   受影响的不止 Universal Trade Hub：原版轨道贸易同样走这两条链。
        ///
        /// 开启后：仅在"信标地面凑不够"时接管，且**信标地面优先**、核心只作兜底。
        /// 关闭 = 原版行为。
        /// </summary>
        public bool tradePayFromCore = true;

        /// <summary>
        /// 「给某某送物资」也可以从数字核心取货（默认**开**，v0.40）。
        ///
        /// ★ 难民 / 访客索要物资（例如 700 银）时，原版
        ///   <c>GiveItemsToPawnUtility.FindItemToGive</c> 用 <c>ThingRequest.ForDef</c>
        ///   （= <c>ListerThings</c>）找货 —— **只看地图上已生成的 Thing，看不到容器内容物**。
        ///   东西全在数字核心里 ⇒ 找不到 ⇒ 右键菜单只有一条灰色的
        ///   「不能将 Nx 某某赠与某某: 未找到物品」，任务卡死。
        ///
        /// 开启后：**只在原版找不到时**兜底，从可用数字核心取一份到小人脚下再交给原版流程。
        /// 原版找得到时完全不介入。
        /// 关闭 = 原版行为。
        /// </summary>
        public bool giveItemsFromCore = true;

        // ─────────────────── 物品可见性兜底（v0.44） ───────────────────

        /// <summary>
        /// 「携带」栏设定的随身库存目标可用数字核心补货（<b>默认开</b>）。
        ///
        /// <para>背景：玩家在「携带」栏给小人设定「随身带 N 个药 / N 份食物」时，
        /// 原版 <c>JobGiver_TakeForInventoryStock.FindThingFor</c> 调
        /// <c>GenClosest.ClosestThingReachable(...)</c> 时**没传 <c>lookInHaulSources</c>**
        /// （该参数默认 false）⇒ 只搜地图上已生成的东西，看不到数字核心里的库存。
        /// 东西全在核心里 ⇒ 目标**永远填不满**，且每隔 6000–9000 tick 又去试一次。</para>
        ///
        /// <para>开启后：**只在原版拿不出作业时**，从可用数字核心取一份到小人脚下，
        /// 再构造与原版同款的 <c>TakeCountToInventory</c> 作业，后续全归原版。
        /// 原版拿得出作业时完全不介入。关闭 = 原版行为。</para>
        /// </summary>
        public bool inventoryStockFromCore = true;

        /// <summary>
        /// 「输血」手术把数字核心里的血原质包算在内（<b>默认开</b>）。
        ///
        /// <para>背景：<c>Recipe_BloodTransfusion.AvailableOnNow</c> 用
        /// <c>listerThings.ThingsOfDef(HemogenPack).Count == 0</c> 判断"有没有血原质"，
        /// <c>GetIngredientCount</c> 也用 <c>listerThings</c> 求和 ⇒ 都看不到核心。
        /// 血包全存核心 ⇒ 手术**直接不可用**（"判定为无"型硬缺口）。</para>
        ///
        /// <para>开启后：仅当"地图上确实没血包、而核心里有"时，才把可用性判为 true，
        /// 并把核心的包数计入用量。关闭 = 原版行为。</para>
        /// </summary>
        public bool bloodTransfusionFromCore = true;

        // ─────────────────── 物品可见性兜底（二）（v0.45） ───────────────────

        /// <summary>
        /// 医生「输注血原质」可用数字核心里的血原质包（<b>默认开</b>）。
        ///
        /// <para>背景：<c>Workgiver_AdministerHemogen.HasJobOnThing / JobOnThing</c> 用
        /// <c>ClosestThingReachable(HemogenPack)</c>（不带 <c>lookInHaulSources</c>）找血包 ⇒ 看不到核心。
        /// 血包全存核心 ⇒ 判定"没有血原质" ⇒ 该工作直接不给（硬缺口）。</para>
        ///
        /// <para>开启后：仅当"地图上确实没血包、而核心里有"时，让工作可用并从核心取出 1 个到医生脚下，
        /// 构造与原版同款的喂食作业。关闭 = 原版行为。</para>
        /// </summary>
        public bool hemogenFeedFromCore = true;

        /// <summary>
        /// 血族 / 血原质依赖者能从数字核心取血原质包回补血原质（<b>默认开</b>）。
        ///
        /// <para>背景：<c>JobGiver_GetHemogen.GetHemogenPack</c> 用
        /// <c>listerThings.ThingsOfDef(HemogenPack)</c> 找血包 ⇒ 看不到核心（硬缺口）。</para>
        ///
        /// <para>开启后：血原质需要回补、而地图上没有血包时，从核心按需取到脚下并照原版吃。
        /// 关闭 = 原版行为。</para>
        /// </summary>
        public bool hemogenPackFromCore = true;

        /// <summary>
        /// 药物暴食（Binging）能取数字核心里的成瘾品（<b>默认开</b>）。
        ///
        /// <para>背景：两处独立链路都看不到核心 —— 触发门 <c>AddictionUtility.CanBingeOnNow</c>
        /// （<c>listerThings.ThingsInGroup(Drug)</c>，返回 bool）与取药 <c>JobGiver_BingeDrug.BestIngestTarget</c>
        /// （<c>ThingRequestGroup.Drug</c>，不带钩子）。成瘾品全存核心 ⇒ 既进不了暴食、进去也找不到药。</para>
        ///
        /// <para>开启后：核心里有匹配的药时允许进入暴食，并从中按需取到脚下（镜像原版的类别/过量判据）。
        /// 关闭 = 原版行为。</para>
        /// </summary>
        public bool bingeDrugFromCore = true;

        /// <summary>
        /// 按药品政策把药备到背包时可用数字核心库存（<b>默认开</b>）。
        ///
        /// <para>背景：<c>JobGiver_MoveDrugsToInventory.FindDrugFor</c> 用
        /// <c>ClosestThingReachable(ForDef(drug))</c>（不带钩子）找药 ⇒ 看不到核心（软缺口）。
        /// （该方法为一行的私有方法，会被内联 ⇒ 本补丁挂在公共入口 <c>TryGiveJob</c>。）</para>
        ///
        /// <para>开启后：地图上找不到时从核心按缺口数量取到脚下，构造与原版同款的
        /// <c>TakeInventory</c> 作业。关闭 = 原版行为。</para>
        /// </summary>
        public bool moveDrugsToInventoryFromCore = true;

        /// <summary>
        /// 炮塔能从数字核心补弹 / 补燃料（<b>默认开</b>）。
        ///
        /// <para>背景：<c>JobDriver_ManTurret.FindAmmoForTurret / FindFuelForTurret</c> 用
        /// <c>ClosestThingReachable</c>（不带钩子、且 40 格限制）找弹/燃料 ⇒ 看不到核心。
        /// 炮弹全存核心 ⇒ 迫击炮装不上弹（"附近没有炮弹"）。</para>
        ///
        /// <para>开启后：地图上找不到时从核心按（弹药：允许弹种 + 阵营过滤；燃料：fuelFilter）取 1 份
        /// 到操作者脚下（其就在炮塔旁，天然满足 40 格）。关闭 = 原版行为。</para>
        /// </summary>
        public bool turretSuppliesFromCore = true;

        /// <summary>
        /// 麦芽汁能从数字核心倒进发酵桶（<b>默认开</b>）。
        ///
        /// <para>背景：<c>WorkGiver_FillFermentingBarrel.FindWort</c> 用 <c>ClosestThingReachable(ForDef(Wort))</c>
        /// （不带钩子）找麦汁 ⇒ 看不到核心（软缺口）。麦汁全存核心 ⇒ 倒不进桶，酿酒断链。</para>
        ///
        /// <para>开启后：地图上找不到时从核心按"桶还需的量"取到小人脚下，构造与原版同款作业。
        /// 关闭 = 原版行为。</para>
        /// </summary>
        public bool fillBarrelFromCore = true;

        /// <summary>
        /// 染发能用数字核心里的染料（<b>默认开</b>）。
        ///
        /// <para>★ 半通陷阱（见报告 ARCH-12）：染料计数/UI 端走 <c>map.resourceCounter.GetCount(Dye)</c>，
        /// 已被 4.0 补过 ⇒ **弹窗不报缺染料**；而取用端 <c>JobGiver_DyeHair.TryGiveJob</c> 走
        /// <c>listerThings.ThingsOfDef(Dye)</c> ⇒ **看不到核心** ⇒ 拿不出来、染不了。</para>
        ///
        /// <para>开启后：取用端也认核心（取 1 份到小人脚下）。关闭 = 原版行为（UI 仍说够，但染不了）。</para>
        /// </summary>
        /// <summary>
        /// 染发 / 改衣物颜色 / 涂装建筑与地板 都能用数字核心里的染料（<b>默认开</b>）。
        ///
        /// <para>★ **半通陷阱**（见报告 ARCH-12）：这几处的**计数/UI 端**走
        /// <c>map.resourceCounter.GetCount(Dye)</c>，已被 4.0 补过（<c>Patch_ResourceCounter_HaulSources</c>）
        /// ⇒ **界面常常不提示缺染料**；而**取用端**分别走 <c>JobGiver_DyeHair</c>（染发）、
        /// <c>JobGiver_OptimizeApparel</c>（改衣物颜色）、<c>WorkGiver_PaintBuilding/PaintFloor</c>（涂装），
        /// 都是 <c>listerThings.ThingsOfDef(Dye)</c> ⇒ **看不到核心** ⇒ 拿不出来、做不成。</para>
        ///
        /// <para>开启后：三处的取用端也都认核心（把染料取到小人脚下，后续全归原版）。
        /// 关闭 = 原版行为（界面仍说够，实际做不成）。</para>
        /// </summary>
        public bool dyeFromCore = true;

        /// <summary>
        /// 纪念碑蓝图的材料菜单也认数字核心里的材料（<b>默认开</b>）。
        ///
        /// <para>背景：<c>MonumentMarker.ListFloatMenuOptions</c> 用
        /// <c>listerThings.ThingsOfDef(def).Count</c> 判断"有没有这种材料" ⇒ 看不到核心。
        /// 材料全在核心时该判定恒为"没有" ⇒ 菜单退化成**把所有允许的材料全部列出**
        /// （直到材料真出现在地图上才恢复"只列已有的"）。</para>
        ///
        /// <para>开启后：该判定把核心里的也算上（**只读**，不从核心取出、不落地，只修菜单显示）。
        /// 建造与取料仍由原版与既有补丁负责。关闭 = 原版行为（全列）。</para>
        /// </summary>
        public bool monumentStuffFromCore = true;

        /// <summary>
        /// 「异种胚芽全链」都能用数字核心里的**超凡胶囊**与**异种胚芽**（<b>默认开</b>）。
        ///
        /// <para>覆盖三个环节：① **组装界面**能不能开始（<c>Dialog_CreateXenogerm.ColonyHasEnoughArchites</c>
        /// 数超凡胶囊）；② **搬运**超凡胶囊进基因装配器（<c>WorkGiver_CreateXenogerm</c>）；
        /// ③ 组装产出的**异种胚芽**放进核心后，**植入手术**能不能看到/选到
        /// （<c>Recipe_ImplantXenogerm.AvailableOnNow</c> + <c>Dialog_SelectXenogerm</c>）。</para>
        ///
        /// <para>⚠️ ② 的 <c>JobOnThing</c> 找不到胶囊时**仍返回 CreateXenogerm 作业**（坑 28）；
        /// ③ 用**预填**（先把胚芽取到目标小人脚下）而**不复制** <c>Recipe_Surgery.AvailableOnNow</c>
        /// 那一长串判据，让原版自己过判。</para>
        /// </summary>
        public bool xenogermFromCore = true;

        /// <summary>
        /// 「给生化塑体舱补营养」能从数字核心取食物（<b>默认开</b>）。
        ///
        /// <para>背景：<c>WorkGiver_HaulToBiosculpterPod.FindNutrition</c> 用
        /// <c>ClosestThingReachable(FoodSourceNotPlantOrTree)</c> 找食物 ⇒ 看不到核心（软缺口）。</para>
        /// </summary>
        public bool biosculpterFromCore = true;

        /// <summary>
        /// 「狱警给囚犯送血原质包」能从数字核心取包（<b>默认开</b>）。
        ///
        /// <para>背景：<c>WorkGiver_Warden_DeliverHemogen.JobOnThing</c> 用
        /// <c>ClosestThingReachable(HemogenPack)</c> 找包 ⇒ 看不到核心。
        /// 与「医生输注血原质」（<c>hemogenFeedFromCore</c>）是**不同工作**。</para>
        ///
        /// <para>⚠️ 该工作在原版**没有 <c>HasJobOnThing</c> 重写**，而默认实现是
        /// <c>JobOnThing(...) != null</c> ⇒ 同一候选人会被**连着调用两次 <c>JobOnThing</c>**；
        /// 本项内部带**短时去重**，避免一次尝试取两份。</para>
        /// </summary>
        public bool wardenHemogenFromCore = true;

        /// <summary>
        /// 「在聚集区进食」能从数字核心取食物（<b>默认开</b>）。
        ///
        /// <para>背景：<c>JobGiver_EatInGatheringArea.FindFood</c> 用
        /// <c>ClosestThingReachable(FoodSourceNotPlantOrTree, 14f, ...)</c> 找食物，
        /// 且校验含"必须在聚集区内" ⇒ 看不到核心。</para>
        ///
        /// <para>★ 本项的预填**落在「聚集点」而不是小人脚下**：否则小人若站在聚集区外，
        /// 原版的"在聚集区内"校验依然不过。</para>
        /// </summary>
        public bool gatheringEatFromCore = true;

        /// <summary>
        /// 「随机进食」能从数字核心取食物（<b>默认关 · 实验性</b>）。
        ///
        /// <para>背景：<c>JobGiver_EatRandom.TryGiveJob</c> 用
        /// <c>ClosestThingReachable(HaulableAlways, 10f, ...)</c> 找食物 ⇒ 看不到核心。
        /// ★ 该行为**只挂在动物 / 昆虫的 think tree 上**（不是殖民者行为）。</para>
        ///
        /// <para>⚠️ **实验性 + 默认关的原因**：它按"这个种族能不能吃"来挑，判据较宽
        /// ⇒ 可能让动物吃到**你本不想让它吃**的东西（例如把储存的珍贵食材当饲料）。
        /// 需要时再开。</para>
        /// </summary>
        public bool eatRandomFromCore = false;

        /// <summary>
        /// 「装填」可装填的武器 / 装备时能用数字核心的弹药（<b>默认开</b>）。
        ///
        /// <para>背景：<c>ReloadableUtility.FindEnoughAmmo</c> 下游调
        /// <c>RefuelWorkGiverUtility.FindEnoughReservableThings</c> —— 那是**区域遍历型**
        /// （<c>RegionTraverser</c> + <c>region.ListerThings</c>）⇒ 核心里的东西不在任何区域、凑不够。</para>
        ///
        /// <para>★ **只作用于「装填」这一条链**（原版 Anomaly 的几件可装填装备/武器）。
        /// 当年曾挂在共用的 <c>FindEnoughReservableThings</c> 上，但那个函数同时被**补燃料**使用
        /// （4.0 的补丁早已覆盖）⇒ 表现为"开关无区别"，已收窄到装填入口。</para>
        ///
        /// <para>实现：原版返回 null（凑不够）时，把缺量从核心取到 <c>rootCell</c>（区域遍历起点，
        /// 且取量**按小人负重上限收紧**），再复跑一次原方法；内部有**重入标志**防递归。</para>
        /// </summary>
        public bool ammoFromCore = true;

        /// <summary>
        /// 「碎片（Shard）」相关的判定与取用能认数字核心（<b>默认开</b>）。
        ///
        /// <para>覆盖：**黄金立方体停用**（`CompGoldenCube`，需 1 个碎片）与**方尖碑停用**
        /// （`CompObeliskDeactivationInteractor`，需 N 个碎片）的**可用性检查**（本项）与**黄金立方体的执行**（本项）。</para>
        ///
        /// <para>★ 实现刻意**按"检查 / 执行"分开**：
        /// 纯检查的 <c>ExistsUnreservedAmountOfDef</c> / <c>CanReserveAndReachableOfDef</c> **只翻结果、不取货**；
        /// 只有执行入口 <c>CompGoldenCube.OrderDeactivation</c> 才把碎片取到脚下。
        /// （不挂两用的 <c>TryFindReserveAndReachableOfDef</c>，避开"在只读检查里取货"的坑 29。）</para>
        ///
        /// <para>⚠️ 方尖碑的**执行**走 `FindFixedIngredientCount`（见 <c>fixedIngredientFromCore</c>）
        /// ⇒ **方尖碑横跨两个开关**，建议两个都开。</para>
        /// </summary>
        public bool shardFromCore = true;

        /// <summary>
        /// 「按固定材料清单找料」能认数字核心（<b>默认开</b>）。
        ///
        /// <para>目标：<c>HaulAIUtility.FindFixedIngredientCount</c>（**区域遍历型**）。
        /// 覆盖：**方尖碑停用取碎片**、通用 <c>InteractionUtility.OrderInteraction</c>、
        /// **机械搬运体补固定材料**（<c>WorkGiver_HaulResourcesToCarrier</c>）。</para>
        ///
        /// <para>做法与 N2 同款：原版返回空表时，把缺量从核心取到 <c>pawn.Position</c>（区域遍历起点），
        /// **再复跑一次原方法**；内部有**重入标志**防递归。</para>
        /// </summary>
        public bool fixedIngredientFromCore = true;

        // ───────────────────────── 界面 ─────────────────────────

        /// <summary>
        /// 底部菜单栏的「制作自动化」按钮是否显示。**默认隐藏**：
        /// 用户明确说它占位且不需要（同一个面板在制作代理建筑上也能打开，功能不丢）。
        /// 参考实现 Dubs Mint Menus 的 Def 里也是 `<buttonVisible>false</buttonVisible>` 默认隐藏。
        /// </summary>
        public bool craftAutomationButton = false;

        /// <summary>
        /// 「从数字存储取出…」右键菜单是否改用增强取料面板（图标/分组/搜索/来源标注/取满，**默认开**）。
        /// 关闭时完全回退原 mod 的两级扁平菜单；两条路径共用同一条 Job 下单链路，互不影响。
        /// </summary>
        public bool withdrawPickerEnabled = true;

        // ───────────────────────── H. 制作代理 ─────────────────────────

        /// <summary>
        /// 放宽工作台白名单（默认**开**）：原 mod 取活时按 <c>WorkGiverDef.fixedBillGiverDefs</c>
        /// 精确 defName 匹配台子，而原版 DoBill 系 giver 几乎全部带显式白名单（19 个里只有 3 个
        /// "通用"且都是手术）⇒ 任何 mod 添加的工作台（含厨房/炉灶）都被跳过、永远拿不到活。
        /// 开启后不再因白名单跳过，改由 <c>JobOnThing</c> 自身的各项判定把关。
        /// 关闭 = 完全回到原 mod 行为。
        /// </summary>
        public bool craftAnyBench = true;

        /// <summary>
        /// 回收闲置制作线（默认**开**）：维持模式（"维持 xxx"）达标后原 mod 只停"新建线"，
        /// 已建立的线被永久保留 —— 它占着工作台（<c>usedBenches</c> 与
        /// <c>BillBenchClaims</c>）与槽位（<c>maxSlots</c>），导致后续配方显示"待处理/堵塞"
        /// 且永不执行。开启后在每次 <c>SyncLines</c> 前回收"已不需要工作且无在制工作"的线
        /// （正在制作的那件会做完）。关闭 = 完全回到原 mod 行为。
        /// </summary>
        public bool craftReclaimIdle = true;

        /// <summary>
        /// 「制作自动化」界面打开时可正常调整游戏速度（默认**开**）。
        /// 原 mod 的该窗口设了 <c>absorbInputAroundWindow = true</c> 但没配套
        /// <c>forcePause</c>，导致游戏在跑、鼠标与键盘输入却被全局吞掉 ——
        /// 底部时间控制与 1/2/3/4 快捷键都用不了。开启 = 该窗口变为非模态。
        /// 关闭 = 完全回到原 mod 的模态行为（点击不会穿透到地图）。
        /// </summary>
        public bool craftWindowNonModal = true;

        /// <summary>
        /// 允许制作代理处理含「未完成品（UnfinishedThing）」的配方（**默认关**，实验性）。
        /// 原 mod 直接排除了这类配方（UsesUnfinishedThing），而它们正是服装/武器/装备等主流锻造缝纫配方。
        /// 本项放行后代理会走"跳过中间产物、直接出成品"的路径（主料算法已核实与原版等价）。
        /// 已知限制：地图上存在同配方半成品时，原版取活会返回"完成它"的 job，代理无法反解原料 ⇒ 该配方显示堵塞。
        /// </summary>
        public bool craftUftRecipes = false;

        /// <summary>
        /// 代理工人的隐藏替身不再被原版 AI 派活（**默认开** = 修复）。
        ///
        /// <para>数字工人与制作代理共用同一个**隐藏替身小人**（不进地图、从不 tick）。
        /// 但原版偶尔会把它当成真正的殖民者派活 —— 最典型的是「优化装备」让它去穿装备，
        /// 其次是被建造链当成施工者。因为替身的地图是**临时借来的**，这些作业必然失败并
        /// **在同一 tick 反复重来**：日志里刷出 <c>started 10 jobs in one tick</c> 与
        /// <c>Tried to FindPath for pawn which is spawned in another map</c> 两类红字。</para>
        ///
        /// <para>开启后，这类未唤醒的隐藏单位不再参与原版思考（判据用的是原版自己的
        /// "未唤醒单位不思考"标记，与真小人里"地图迷雾中尚未唤醒的单位"同一套规则）
        /// ⇒ 代理建筑照常干活，红字消失。</para>
        ///
        /// <para>关闭 = 原版行为（替身仍可能被派活、红字回来）。</para>
        /// </summary>
        public bool proxyWorkerNoThink = true;

        /// <summary>
        /// 代理工人的替身不再抽到「禁工背景」（**默认开** = 修复）。
        ///
        /// <para>替身是**随机生成**出来的殖民者，原 mod 清了它的特质与关系、也把所有技能写成了
        /// 预设等级，**但没换掉随机滚出来的童年/成年背景**。而背景会禁用某些工作类型
        /// （并让相关技能读数**恒为 0**）⇒ 约 **6.8%** 的替身会"随机变笨"：制作代理报
        /// 「资质不够」、数字工人整类活干不了。因为它**不报错**，玩家只会以为建筑坏了；
        /// 读档重建（替身不进存档）即随机恢复。</para>
        ///
        /// <para>开启后：只把**带禁用**的那个背景槽换成"零禁用"的原版背景，并重算一次禁用工作与
        /// 技能读数；抽到好背景时**不动它**。不影响真小人。</para>
        ///
        /// <para>关闭 = 原版行为（仍可能抽到禁工背景）。</para>
        /// </summary>
        public bool workerCleanBackstory = true;

        /// <summary>
        /// 建造取料**遵守预留**（**默认开** = 修复）。
        ///
        /// <para>原 mod「从核心取料去建造」的作业
        /// （<c>JobDriver_DS_Withdraw.TryMakePreToilReservations</c>）**忽略传入的 <c>errorOnFailed</c>、
        /// 且恒 <c>return true</c>** ⇒ 预留失败也照常开工。后果：**同一格能被多个小人同时派去取料**
        /// （用户实测：5 格 20 木的地板工程被取走 661 木，约 6.6 倍）。</para>
        ///
        /// <para>开启后按原版同形做预留，失败即作废作业 ⇒ 同一目标同一时刻只有一个在送料。
        /// 关闭 = 原 mod 行为。</para>
        /// </summary>
        public bool withdrawRespectReservations = true;

        /// <summary>
        /// 建造投料**按剩余量截断**（**默认开** = 修复）。
        ///
        /// <para>原 mod 往在建物里放料时是 <c>thingOwner.TryAdd(手上全部)</c> —— **盲加**，
        /// 而 <c>ThingOwner</c> 无容量上限 ⇒ 一个 20 木的地板框架能被塞到 150~400 木，
        /// 建成时又被 <c>ClearAndDestroyContents()</c> 全部销毁。</para>
        ///
        /// <para>开启后：放料前若手上多于"这一格还缺的量"，多出来的当场退回最近的数字核心
        /// （送不进才落地，且不标记禁止）。关闭 = 原 mod 行为。</para>
        /// </summary>
        public bool withdrawCapToNeed = true;

        /// <summary>
        /// 建造取料**不会把核心里的材料成堆搬空**（**默认开** = 修复）。
        ///
        /// <para>原 mod 从核心往在建工地搬料时，用"搬运 API 的返回值是否大于 0"来记账，
        /// 而该 API 在"并入已有堆"时会返回 0 ⇒ 记账失效 ⇒ 把核心里**同种材料的每一堆**都搬进
        /// 同一个在建框架（实测：需要 20 木的地板框架被塞到 160 木），建成时这些多出来的材料被全量销毁。</para>
        ///
        /// <para>开启后改为"按目标容器实际增加的件数"记账 ⇒ 一次只搬该格真正需要的量。
        /// 关闭 = 原 mod 行为。</para>
        /// </summary>
        public bool constructPickupAmountFix = true;

        /// <summary>
        /// 制作代理允许**同一配方重复添加任务**（**默认关，实验性**，FIX-11）。
        ///
        /// <para>原 mod 硬编码「一个配方 = 一个计划」：<c>AddPlan</c> 有 <c>HasPlan</c> 守卫、
        /// 挑配方面板对已添加的配方置灰且点击无效 ⇒ 像「切石砖」这种单一配方的活，
        /// 一个代理里永远建不了第二个。</para>
        ///
        /// <para>开启后：已添加的配方仍可点击追加，同一配方可有多个独立计划
        /// （各自的模式/数量/暂停互不影响）。已知限制：重复计划共享同一配方，无法指定不同原料
        /// （如"这个计划切花岗岩、那个切大理石" —— 计划数据模型没有原料过滤）。
        /// 关闭 = 原 mod 行为。</para>
        /// </summary>
        public bool craftAddDuplicate = false;

        /// <summary>
        /// 制作代理工作台**按需分配、计划间公平共享**（**默认关，实验性**，FIX-12）。
        ///
        /// <para>原 mod 的工作台分配是贪心的：每次扫描，每个「要开工」的计划把范围内**所有**
        /// 空闲且能做该配方的台子全部占线（与实际需求量无关）——「做 1 件」也占 5 个台子；
        /// 永久模式则直接占满，任务队列里其他需要同类台子的计划永远等不到
        /// （显示「范围内没有能做工的工作台」）。</para>
        ///
        /// <para>开启后改为：按需封顶（计数模式最多占 remaining 条线、维持模式最多占
        /// "还差的件数"条线，永久模式不封顶）+ 线少者优先轮转 + 超编让台
        /// （分不到台子的任务会从占用较多的任务那里等来空位，在制线不打断）。
        /// 无竞争时行为不变（仍可拿满并行）。关闭 = 原 mod 行为（先到先得，占满为止）。</para>
        /// </summary>
        public bool craftFairBenchAlloc = false;

        /// <summary>
        /// 制作代理取料**按线认领**（**默认关，实验性**，FIX-13）。
        ///
        /// <para>原 mod 的多线并行有一个隐性缺陷：取活时不做原料预留，几条线会对**同一堆料**
        /// 重复记账（料要到完成时才真正扣）；而每 tick 的在制校验盯的是"这一个堆" ——
        /// 一条线完成把堆扣掉一块，其余线的账面需求立刻超出堆的剩余量 ⇒ 校验失败 ⇒
        /// 进度清零重新取活。表现：同配方多线并行，一条完成、其余全部从 0% 重新读条，
        /// 并行塌缩成串行。</para>
        ///
        /// <para>开启后：取活成功即按线登记原料认领，探针选料时自动避开已被认领的堆
        /// （改选其它堆）—— 各线用各的料，互不再踩。已知边界：某种原料只剩一个堆时，
        /// 后续线会等前一条完成（串行但不清零）；多堆则真并行。
        /// 关闭 = 原 mod 行为。存量认领在关闭后仍会随制作线结束正常释放。</para>
        /// </summary>
        public bool craftIngredientClaims = false;

        /// <summary>
        /// 搬运判定**不对幽灵物品崩溃**（**默认开** = 修复，INVEST-14 方案 A，v0.61）。
        ///
        /// <para>原 mod 对搬运清单里的"幽灵物品"（已不在任何地图/容器上的无主物品）有两道防护前缀，
        /// 但都挂在 1.6 之前的旧方法名上；1.6 的"顺路搬运"调用点已改走
        /// <c>PawnCanAutomaticallyHaulFast_NewTemp</c>，防护未覆盖 ⇒ 清单里一旦出现幽灵物品，
        /// 附近小人每次开工都抛 NRE、作业系统软锁、日志每 tick 刷屏。</para>
        ///
        /// <para>开启后：给该新调用点挂上与原 mod 同语义的防护（幽灵物品判为"不可自动搬运"），
        /// NRE 与软锁消失；每次拦下按物品 ID 记一条日志（用于定位幽灵的产生路径）。
        /// 关闭 = 原 mod 行为（幽灵物品存在时报错复现）。</para>
        /// </summary>
        public bool haulGhostGuard = true;

        // ───────────────────────── I. 存储核心 ─────────────────────────

        /// <summary>
        /// 读档时保留核心的存储筛选（默认**开** = 修复）。
        /// 原 mod 在 <c>ExposeData</c> 的 PostLoadInit 分支里每次都按一个**没有 UI 入口**的
        /// 持久化字段 <c>allowWeaponsInStorage</c> 强制重设「武器」类别：字段为 false 时
        /// 武器被全部取消勾选（老存档常见，因为该字段的 Scribe 默认值与字段声明默认值不一致），
        /// 为 true 时又会被强行打开 —— 两种相反的现象同源：读档覆盖了玩家在筛选界面的选择。
        /// 开启后读档不再重置（新建筑的默认值仍由 GetStoreSettings 照常设置）。
        /// </summary>
        public bool keepCoreFilterOnLoad = true;

        /// <summary>
        /// 允许 Anomaly 的心灵仪式从数字核心取祭品（默认**开**）。
        /// 需要祭品的仪式由 <c>JobGiver_GatherOfferingsForPsychicRitual</c> 派活，
        /// 而它只在地图上找已生成的物品 ⇒ 核心里的祭品（未生成）永远找不到，仪式报
        /// "祭品不可用"后取消。开启后，原版找不到时本补丁从核心取出到收集者脚下，
        /// 再由原版同一个"捡进背包"job 捡起（保持真实消耗语义）。
        /// </summary>
        public bool ritualOfferingFromCore = true;

        /// <summary>
        /// 允许 Ideology「天灯节」（Sky Lanterns）从数字核心取原木（默认**开**）。
        /// 启动检查（GetBlockingIssues）与取木职责（JobGiver_TakeCountToInventory）
        /// 共用本开关：开则"能启动 + 能取到"，关则一并回到原版行为。
        /// </summary>
        public bool skyLanternFromCore = true;

        /// <summary>
        /// 数字核心不再接收「未完成（Unfinished）」物品（默认**开**）。
        /// 两道防线：① 建成/读档时把该类别从核心筛选里取消；② 准入询问
        /// （<c>Accepts</c>）时再拒绝一次，即使某路径绕过筛选也进不去。
        /// 目的是从根源避免"未完成产物被核心吞掉"一类问题。均为事件驱动，不含每 tick 扫描。
        /// </summary>
        public bool coreRejectUnfinished = true;

        /// <summary>
        /// 从核心取食时补上"饮食方案之外"的食物规则（**默认关**，实验性）。
        ///
        /// <para>原 mod 的四条取食入口都检查了<b>饮食方案（食物管制方案）</b>本身 —— 方案不是漏洞，
        /// 但"方案之外的规则"有缺口：喂食路径缺<b>头衔食物规则</b>与<b>动物崇拜戒律</b>，
        /// 自取/娱乐路径缺<b>原料级戒律</b>（用崇拜动物的肉做的饭菜），喂食路径还缺
        /// mutant 食物类型覆盖与婴儿规则。</para>
        ///
        /// <para>开启后，本补丁在原 mod 得出结果<b>之后</b>再用原版同款的
        /// <c>FoodUtility.WillEat(Thing)</c> 复核一次（它本身就含 def 级政策、头衔、
        /// 戒律的 def 级与<b>原料级</b>、物品级政策筛选、baby/mutant 判定）。
        /// 判据与原版逐字相同 ⇒ <b>只会更严、不会更松</b>：能吃的东西照旧能吃，
        /// 只有原版也会拒绝的东西会被拦下。</para>
        ///
        /// <para>已知副作用：喂食路径若被判不通过，原 mod 已经把食物取到了小人脚下 ⇒
        /// 地上会短暂留一堆（带"刚取出"标记，若干 tick 后被自动收纳吸回，不会丢）。</para>
        /// </summary>
        public bool foodRestrictionAlign = false;

        /// <summary>
        /// 修复「病人喂食永远预约失败」的死循环：原 mod 把食物取到小人脚下后，
        /// 那堆食物常被搬运工以 <c>maxPawns=1</c> 预留，而原版喂食驱动是按
        /// <c>maxPawns=10</c> 预留食物 ⇒ <c>CanReserveStack</c> 里"MaxPawns 不一致直接返回 0"
        /// ⇒ 作业永远起不来（实测 2026-10-08）。
        /// 开启后：若当前食物目标本来就预约不上，就改喂一份刚从核心取出的干净食物。
        /// **默认开**（纯修复：只在"本来就会失败"时才动手，不可能让能成的喂食变坏）。
        /// </summary>
        public bool feedPatientReservable = true;

        /// <summary>
        /// 修复「从数字核心取食时给成年人塞婴儿食物」：原 mod 的取食评分
        /// <c>FoodScoring.Score</c> 不看 <c>tasteThought</c> 的心情后果，
        /// 而 <c>BabyFood</c> 又是 <c>MealTerrible</c>（原 mod 没给这一档扣分）⇒
        /// 婴儿食物得 300 分，包装生存食品只有 295 分 ⇒ 每次都挑婴儿食物（成年人吃了掉心情）。
        /// 开启后：给评分补上心情这一项（婴儿食物 → 230 分）⇒ 改选更合适的食物。
        /// **默认开**（纯修复：只是把不合适的排到后面，不会让人没东西吃）。
        /// </summary>
        public bool coreFoodScoringMood = true;

        // ───────────────────────── 其它 ─────────────────────────

        /// <summary>首次运行标记，用于"默认行为 == 不打补丁"的兼容性初始化。</summary>
        public bool defaultsApplied = false;

        /// <summary>间隔下限（tick）。低于此值会让每 tick 取模几乎恒真、开销骤增。</summary>
        public const int MinInterval = 10;

        /// <summary>间隔上限（tick）。</summary>
        public const int MaxInterval = 600;

        public static int Clamp(int v)
        {
            if (v < MinInterval)
            {
                return MinInterval;
            }
            return v > MaxInterval ? MaxInterval : v;
        }

        public void EnsureDefaults()
        {
            // ★ v0.20 一次性复位：老存档里 skipCoreHaulRecalc 可能是 true，
            //   而它已被确认会破坏原 mod 自动收纳的候选池 ⇒ 首次升级到 v0.20 时强制关掉。
            //   之后再改就尊重玩家的手动选择（defaultsApplied 会写进存档）。
            if (!defaultsApplied)
            {
                skipCoreHaulRecalc = false;
                defaultsApplied = true;
            }

            autoRestockInterval = Clamp(autoRestockInterval);
            autoCollectInterval = Clamp(autoCollectInterval);
            autoCollectBatch = ClampCollectBatch(autoCollectBatch);
            autoRestockBatch = ClampRestockBatch(autoRestockBatch);
            receivingScanInterval = Clamp(receivingScanInterval);
            capLevel1 = ClampCapacity(capLevel1);
            capLevel2 = ClampCapacity(capLevel2);
            capLevel3 = ClampCapacity(capLevel3);
            capLevel4 = ClampCapacity(capLevel4);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref diagnosticsEnabled, "diagnosticsEnabled", false);
            Scribe_Values.Look(ref powerExempt, "powerExempt", false);
            Scribe_Values.Look(ref bamMaterialsFromCore, "bamMaterialsFromCore", true);
            Scribe_Values.Look(ref skipCoreHaulRecalc, "skipCoreHaulRecalc", false);
            Scribe_Values.Look(ref fixWasteEmptyJob, "fixWasteEmptyJob", true);
            Scribe_Values.Look(ref autoRestockInterval, "autoRestockInterval", 250);
            Scribe_Values.Look(ref autoCollectInterval, "autoCollectInterval", 60);
            Scribe_Values.Look(ref autoCollectBatch, "autoCollectBatch", 0);
            Scribe_Values.Look(ref autoRestockBatch, "autoRestockBatch", 1);
            Scribe_Values.Look(ref receivingScanInterval, "receivingScanInterval", 30);
            Scribe_Values.Look(ref avoidCoreDirectHaul, "avoidCoreDirectHaul", false);
            Scribe_Values.Look(ref shelfSkipIngestNotify, "shelfSkipIngestNotify", true);
            // 区间兜底：手改 XML 也不会出现 0 或负数（0 会导致取模除零）
            autoRestockInterval = Clamp(autoRestockInterval);
            autoCollectInterval = Clamp(autoCollectInterval);
            autoCollectBatch = ClampCollectBatch(autoCollectBatch);
            autoRestockBatch = ClampRestockBatch(autoRestockBatch);
            receivingScanInterval = Clamp(receivingScanInterval);
            Scribe_Values.Look(ref autoRestock, "autoRestock", false);
            Scribe_Values.Look(ref restockSameMap, "restockSameMap", false);
            Scribe_Values.Look(ref collectSameMap, "collectSameMap", true);
            Scribe_Values.Look(ref billCrossMapAnyCore, "billCrossMapAnyCore", false);
            Scribe_Values.Look(ref customCapacityEnabled, "customCapacityEnabled", false);
            Scribe_Values.Look(ref capLevel1, "capLevel1", 500);
            Scribe_Values.Look(ref capLevel2, "capLevel2", 1000);
            Scribe_Values.Look(ref capLevel3, "capLevel3", 1500);
            Scribe_Values.Look(ref capLevel4, "capLevel4", 3000);
            Scribe_Values.Look(ref digitalBatteryCapacity, "digitalBatteryCapacity", 5000);
            Scribe_Values.Look(ref digitalBatteryTargetPct, "digitalBatteryTargetPct", 50);
            Scribe_Values.Look(ref digitalBatteryBalanceInterval, "digitalBatteryBalanceInterval", 250);
            capLevel1 = ClampCapacity(capLevel1);
            capLevel2 = ClampCapacity(capLevel2);
            capLevel3 = ClampCapacity(capLevel3);
            capLevel4 = ClampCapacity(capLevel4);
            digitalBatteryCapacity = ClampBatteryCapacity(digitalBatteryCapacity);
            digitalBatteryTargetPct = ClampTargetPct(digitalBatteryTargetPct);
            digitalBatteryBalanceInterval = ClampBalanceInterval(digitalBatteryBalanceInterval);
            digitalBatteryCapacity = ClampBatteryCapacity(digitalBatteryCapacity);
            digitalBatteryTargetPct = ClampTargetPct(digitalBatteryTargetPct);
            digitalBatteryBalanceInterval = ClampBalanceInterval(digitalBatteryBalanceInterval);
            // 用 int 存枚举：枚举名写进 XML 后一旦改名/删项就会读不回来，整数更稳。
            int mode = (int)priorityMode;
            Scribe_Values.Look(ref mode, "priorityMode", (int)DsPriorityMode.ModDefault);
            priorityMode = (DsPriorityMode)mode;
            if ((int)priorityMode < (int)DsPriorityMode.ModDefault
                || (int)priorityMode > (int)DsPriorityMode.VanillaFirst)
            {
                priorityMode = DsPriorityMode.ModDefault;
            }
            Scribe_Values.Look(ref tradeFromCore, "tradeFromCore", false);
            Scribe_Values.Look(ref tradePayFromCore, "tradePayFromCore", true);
            Scribe_Values.Look(ref giveItemsFromCore, "giveItemsFromCore", true);
            Scribe_Values.Look(ref inventoryStockFromCore, "inventoryStockFromCore", true);
            Scribe_Values.Look(ref bloodTransfusionFromCore, "bloodTransfusionFromCore", true);
            Scribe_Values.Look(ref hemogenFeedFromCore, "hemogenFeedFromCore", true);
            Scribe_Values.Look(ref hemogenPackFromCore, "hemogenPackFromCore", true);
            Scribe_Values.Look(ref bingeDrugFromCore, "bingeDrugFromCore", true);
            Scribe_Values.Look(ref moveDrugsToInventoryFromCore, "moveDrugsToInventoryFromCore", true);
            Scribe_Values.Look(ref turretSuppliesFromCore, "turretSuppliesFromCore", true);
            Scribe_Values.Look(ref fillBarrelFromCore, "fillBarrelFromCore", true);
            Scribe_Values.Look(ref dyeFromCore, "dyeFromCore", true);
            Scribe_Values.Look(ref monumentStuffFromCore, "monumentStuffFromCore", true);
            Scribe_Values.Look(ref xenogermFromCore, "xenogermFromCore", true);
            Scribe_Values.Look(ref biosculpterFromCore, "biosculpterFromCore", true);
            Scribe_Values.Look(ref wardenHemogenFromCore, "wardenHemogenFromCore", true);
            Scribe_Values.Look(ref gatheringEatFromCore, "gatheringEatFromCore", true);
            Scribe_Values.Look(ref eatRandomFromCore, "eatRandomFromCore", false);
            Scribe_Values.Look(ref ammoFromCore, "ammoFromCore", true);
            Scribe_Values.Look(ref shardFromCore, "shardFromCore", true);
            Scribe_Values.Look(ref fixedIngredientFromCore, "fixedIngredientFromCore", true);
            Scribe_Values.Look(ref craftAnyBench, "craftAnyBench", true);
            Scribe_Values.Look(ref craftReclaimIdle, "craftReclaimIdle", true);
            Scribe_Values.Look(ref craftWindowNonModal, "craftWindowNonModal", true);
            Scribe_Values.Look(ref craftUftRecipes, "craftUftRecipes", false);
            Scribe_Values.Look(ref keepCoreFilterOnLoad, "keepCoreFilterOnLoad", true);
            Scribe_Values.Look(ref ritualOfferingFromCore, "ritualOfferingFromCore", true);
            Scribe_Values.Look(ref skyLanternFromCore, "skyLanternFromCore", true);
            Scribe_Values.Look(ref coreRejectUnfinished, "coreRejectUnfinished", true);
            Scribe_Values.Look(ref foodRestrictionAlign, "foodRestrictionAlign", false);
            Scribe_Values.Look(ref feedPatientReservable, "feedPatientReservable", true);
            Scribe_Values.Look(ref coreFoodScoringMood, "coreFoodScoringMood", true);
            Scribe_Values.Look(ref craftAutomationButton, "craftAutomationButton", false);
            Scribe_Values.Look(ref withdrawPickerEnabled, "withdrawPickerEnabled", true);
            // v0.56–v0.59 的五个修复开关：此前漏了 ExposeData，重启后会被重置回默认值
            // （默认恰好是"开"所以长期未暴露）。补上持久化。
            Scribe_Values.Look(ref proxyWorkerNoThink, "proxyWorkerNoThink", true);
            Scribe_Values.Look(ref workerCleanBackstory, "workerCleanBackstory", true);
            Scribe_Values.Look(ref withdrawRespectReservations, "withdrawRespectReservations", true);
            Scribe_Values.Look(ref withdrawCapToNeed, "withdrawCapToNeed", true);
            Scribe_Values.Look(ref constructPickupAmountFix, "constructPickupAmountFix", true);
            // v0.60：FIX-11（同配方重复任务）+ FIX-12（工作台按需分配、公平共享）+ FIX-13（取料按线认领）
            // 三个均为实验性，默认关
            Scribe_Values.Look(ref craftAddDuplicate, "craftAddDuplicate", false);
            Scribe_Values.Look(ref craftFairBenchAlloc, "craftFairBenchAlloc", false);
            Scribe_Values.Look(ref craftIngredientClaims, "craftIngredientClaims", false);
            // v0.61：INVEST-14 方案 A（搬运判定不对幽灵物品崩溃）
            Scribe_Values.Look(ref haulGhostGuard, "haulGhostGuard", true);
            Scribe_Values.Look(ref defaultsApplied, "defaultsApplied", false);
        }
    }
}
