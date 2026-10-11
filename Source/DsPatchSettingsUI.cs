using UnityEngine;
using Verse;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 补丁设置界面。用原版 Listing_Standard 直接绘制（不写自绘滚动视图），
    /// 因此不存在"内容高度函数不同步"的坑。
    ///
    /// ★ v0.12 起必须自带滚动：原版 <c>Dialog_ModSettings</c> **不提供** ScrollView
    ///   （已核实 1.6 源码：内容高度 = 窗口高 700 - 顶栏 40 - 关闭按钮 ≈ 600px），
    ///   而本补丁的设置分区已经超过这个高度 —— 底部会直接被裁掉、够不到。
    ///   这里用 <c>Widgets.BeginScrollView</c> 包一层，高度取"上一帧实测值 + 余量"，
    ///   第一帧用保守估计，之后收敛到真实高度。
    /// </summary>
    public static class DsPatchSettingsUI
    {
        // 容量输入框的文本缓冲（Listing_Standard.TextFieldNumericLabeled 需要）。
        // 初次绘制时从设置里回填一次，避免手改 XML 后界面显示旧值。
        private static string bufCap1;
        private static string bufCap2;
        private static string bufCap3;
        private static string bufCap4;
        private static string bufBatteryCap;
        private static string bufBatteryInterval;

        /// <summary>滚动位置（跨帧保持）。</summary>
        private static Vector2 scrollPos;

        /// <summary>内容高度：上一帧实测 + 余量；首帧给个够大的保守值。</summary>
        private static float contentHeight = 1100f;

        /// <summary>滚动条宽度 + 一点余量。</summary>
        private const float ScrollbarWidth = 20f;

        /// <summary>分节标题：一条带左侧强调色竖条的浅色背景条。</summary>
        private static readonly Color SectionBg = new Color(1f, 1f, 1f, 0.07f);

        /// <summary>分节标题的强调色（左侧竖条）。</summary>
        private static readonly Color SectionAccent = new Color(0.788f, 0.663f, 0.38f);

        /// <summary>
        /// 画一条分节标题并用 <c>l.Gap</c> 推进 Listing 游标 ——
        /// 不依赖 Listing 的任何内部排版状态，因此不会破坏既有高度计算/滚动。
        /// 字体与锚点是共享静态环境，进出都显式设置并复原（项目踩坑）。
        /// </summary>
        private static void SectionHeader(Listing_Standard l, string key)
        {
            const float h = 28f;
            Rect r = new Rect(0f, l.CurHeight, l.ColumnWidth, h);
            Widgets.DrawBoxSolid(r, SectionBg);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 3f, r.height), SectionAccent);

            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(r.x + 10f, r.y, r.width - 14f, r.height), key.Translate());
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;

            l.Gap(h + 6f);
        }

        /// <summary>说明性小字（降亮度，与设置项本身区分开）。</summary>
        private static void Hint(Listing_Standard l, string text)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.62f);
            l.Label(text);
            GUI.color = old;
        }

        public static void Draw(Rect inRect, DsPatchSettings s)
        {
            if (s == null)
            {
                return;
            }

            float viewWidth = inRect.width - ScrollbarWidth;
            Rect viewRect = new Rect(0f, 0f, viewWidth, contentHeight);
            Widgets.BeginScrollView(inRect, ref scrollPos, viewRect);

            Listing_Standard l = new Listing_Standard();
            // ★★ 必须置 true，否则会出现"勾选某项后，它下面的控件全都看不见、也点不到"：
            //    Listing.NewColumnIfNeeded() 在 `curY + neededHeight > listingRect.height` 时
            //    会自动 NewColumn() —— 把后续控件画到 **curX += ColumnWidth + 17** 的右侧第二栏去。
            //    而我们的可见宽度只有 viewRect.width，第二栏整个落在可视区外。
            //    （这也解释了历史上"电力/调试两节够不到"的现象：它们被折到看不见的第二栏了。）
            l.maxOneColumn = true;
            l.Begin(new Rect(0f, 0f, viewWidth, contentHeight));

            SectionHeader(l, "DigitalStoragePatch_SectionPerf");
            l.CheckboxLabeled(
                "DigitalStoragePatch_SkipCoreHaulRecalc".Translate(),
                ref s.skipCoreHaulRecalc,
                "DigitalStoragePatch_SkipCoreHaulRecalcDesc".Translate());
            l.CheckboxLabeled(
                "DigitalStoragePatch_FixWasteEmptyJob".Translate(),
                ref s.fixWasteEmptyJob,
                "DigitalStoragePatch_FixWasteEmptyJobDesc".Translate());
            l.CheckboxLabeled(
                "DigitalStoragePatch_ShelfSkipIngestNotify".Translate(),
                ref s.shelfSkipIngestNotify,
                "DigitalStoragePatch_ShelfSkipIngestNotifyDesc".Translate());
            l.Gap(2f);
            // v0.61：INVEST-14 方案 A —— 搬运判定不对幽灵物品崩溃（纯修复，默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_HaulGhostGuard".Translate(),
                ref s.haulGhostGuard,
                "DigitalStoragePatch_HaulGhostGuardDesc".Translate());

            l.Gap(14f);
            SectionHeader(l, "DigitalStoragePatch_SectionIntervals");

            l.Label("DigitalStoragePatch_AutoRestockInterval".Translate(s.autoRestockInterval));
            s.autoRestockInterval = DsPatchSettings.Clamp(
                Mathf.RoundToInt(l.Slider(s.autoRestockInterval,
                    DsPatchSettings.MinInterval, DsPatchSettings.MaxInterval)));
            l.Gap(2f);
            l.Label("DigitalStoragePatch_AutoCollectInterval".Translate(s.autoCollectInterval));
            s.autoCollectInterval = DsPatchSettings.Clamp(
                Mathf.RoundToInt(l.Slider(s.autoCollectInterval,
                    DsPatchSettings.MinInterval, DsPatchSettings.MaxInterval)));
            l.Gap(2f);
            l.Label("DigitalStoragePatch_ReceivingScanInterval".Translate(s.receivingScanInterval));
            s.receivingScanInterval = DsPatchSettings.Clamp(
                Mathf.RoundToInt(l.Slider(s.receivingScanInterval,
                    DsPatchSettings.MinInterval, DsPatchSettings.MaxInterval)));
            l.Gap(4f);
            Hint(l, "DigitalStoragePatch_IntervalHint".Translate());

            l.Gap(24f);

            SectionHeader(l, "DigitalStoragePatch_SectionProjector");
            l.CheckboxLabeled(
                "DigitalStoragePatch_AutoRestock".Translate(),
                ref s.autoRestock,
                "DigitalStoragePatch_AutoRestockDesc".Translate());
            l.Gap(2f);
            // v0.20：同图投影也照样自动收取（默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_CollectSameMap".Translate(),
                ref s.collectSameMap,
                "DigitalStoragePatch_CollectSameMapDesc".Translate());
            l.Gap(2f);
            // v0.19：同图补货默认不放行（4.0 已删除补货机制，同图没有第二条补货路径）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_RestockSameMap".Translate(),
                ref s.restockSameMap,
                "DigitalStoragePatch_RestockSameMapDesc".Translate());
            l.Gap(2f);
            // v0.17 批量：收取 0 = 跟随研究等级；补货默认 1 = 3.0 语义
            l.Label("DigitalStoragePatch_AutoCollectBatch".Translate(s.autoCollectBatch));
            s.autoCollectBatch = DsPatchSettings.ClampCollectBatch(
                Mathf.RoundToInt(l.Slider(s.autoCollectBatch, 0f, DsPatchSettings.MaxCollectBatch)));
            l.Gap(2f);
            l.Label("DigitalStoragePatch_AutoRestockBatch".Translate(s.autoRestockBatch));
            s.autoRestockBatch = DsPatchSettings.ClampRestockBatch(
                Mathf.RoundToInt(l.Slider(s.autoRestockBatch, 1f, DsPatchSettings.MaxRestockBatch)));
            l.Gap(24f);

            // ───────────── D. 存储容量自定义 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionCapacity");
            l.CheckboxLabeled(
                "DigitalStoragePatch_CustomCapacity".Translate(),
                ref s.customCapacityEnabled,
                "DigitalStoragePatch_CustomCapacityDesc".Translate());
            if (s.customCapacityEnabled)
            {
                l.Gap(2f);
                if (bufCap1 == null) bufCap1 = s.capLevel1.ToString();
                if (bufCap2 == null) bufCap2 = s.capLevel2.ToString();
                if (bufCap3 == null) bufCap3 = s.capLevel3.ToString();
                if (bufCap4 == null) bufCap4 = s.capLevel4.ToString();
                l.TextFieldNumericLabeled(
                    "DigitalStoragePatch_CapLevel1".Translate(), ref s.capLevel1, ref bufCap1,
                    0f, DsPatchSettings.MaxCapacity);
                l.TextFieldNumericLabeled(
                    "DigitalStoragePatch_CapLevel2".Translate(), ref s.capLevel2, ref bufCap2,
                    0f, DsPatchSettings.MaxCapacity);
                l.TextFieldNumericLabeled(
                    "DigitalStoragePatch_CapLevel3".Translate(), ref s.capLevel3, ref bufCap3,
                    0f, DsPatchSettings.MaxCapacity);
                l.TextFieldNumericLabeled(
                    "DigitalStoragePatch_CapLevel4".Translate(), ref s.capLevel4, ref bufCap4,
                    0f, DsPatchSettings.MaxCapacity);
                l.Gap(2f);
                Hint(l, "DigitalStoragePatch_CapacityHint".Translate());
            }
            // 每帧同步到原 mod 的容量表（4 次 int 写入，代价可忽略；关掉开关会写回原值）
            Patch_CustomCapacity.ApplyFromSettings();
            l.Gap(24f);

            // ───────────── F. 数字电池 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionBattery");
            if (bufBatteryCap == null) bufBatteryCap = s.digitalBatteryCapacity.ToString();
            l.TextFieldNumericLabeled(
                "DigitalStoragePatch_Battery_Capacity".Translate(), ref s.digitalBatteryCapacity,
                ref bufBatteryCap, DsPatchSettings.BatteryCapacityMin, DsPatchSettings.BatteryCapacityMax);
            l.Gap(2f);
            l.Label("DigitalStoragePatch_Battery_TargetPct".Translate(s.digitalBatteryTargetPct));
            s.digitalBatteryTargetPct = DsPatchSettings.ClampTargetPct(
                Mathf.RoundToInt(l.Slider(s.digitalBatteryTargetPct,
                    DsPatchSettings.TargetPctMin, DsPatchSettings.TargetPctMax)));
            l.Gap(2f);
            if (bufBatteryInterval == null) bufBatteryInterval = s.digitalBatteryBalanceInterval.ToString();
            l.TextFieldNumericLabeled(
                "DigitalStoragePatch_Battery_Interval".Translate(), ref s.digitalBatteryBalanceInterval,
                ref bufBatteryInterval, DsPatchSettings.BalanceIntervalMin, DsPatchSettings.BalanceIntervalMax);
            l.Gap(2f);
            Hint(l, "DigitalStoragePatch_Battery_Hint".Translate());
            // 每帧把容量写回 def 的共享 CompProperties（改设置立即生效，已建成的电池也认）
            DsDigitalBatteryManager.ApplyCapacity();
            l.Gap(24f);

            // ───────────── G. 交易 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionTrade");
            l.CheckboxLabeled(
                "DigitalStoragePatch_TradeFromCore".Translate(),
                ref s.tradeFromCore,
                "DigitalStoragePatch_TradeFromCoreDesc".Translate());
            l.Gap(24f);

            // ───────────── H. 制作代理 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionCraftWorker");
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftAnyBench".Translate(),
                ref s.craftAnyBench,
                "DigitalStoragePatch_CraftAnyBenchDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftReclaimIdle".Translate(),
                ref s.craftReclaimIdle,
                "DigitalStoragePatch_CraftReclaimIdleDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftWindowNonModal".Translate(),
                ref s.craftWindowNonModal,
                "DigitalStoragePatch_CraftWindowNonModalDesc".Translate());
            l.Gap(2f);
            // 跨图取料的判据（原在「投影核心」节，v0.22 起归到本条目下 —— 它只影响制作代理）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_BillCrossMapAnyCore".Translate(),
                ref s.billCrossMapAnyCore,
                "DigitalStoragePatch_BillCrossMapAnyCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftUftRecipes".Translate(),
                ref s.craftUftRecipes,
                "DigitalStoragePatch_CraftUftRecipesDesc".Translate());
            l.Gap(2f);
            // v0.56：代理工人的隐藏替身不再被原版 AI 派活（纯修复，默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_ProxyWorkerNoThink".Translate(),
                ref s.proxyWorkerNoThink,
                "DigitalStoragePatch_ProxyWorkerNoThinkDesc".Translate());
            l.Gap(2f);
            // v0.57：替身不再抽到「禁工背景」（纯修复，默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_WorkerCleanBackstory".Translate(),
                ref s.workerCleanBackstory,
                "DigitalStoragePatch_WorkerCleanBackstoryDesc".Translate());
            l.Gap(2f);
            // v0.58：建造取料修复 H1（遵守预留）+ H3（按剩余量截断）（纯修复，默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_WithdrawRespectReservations".Translate(),
                ref s.withdrawRespectReservations,
                "DigitalStoragePatch_WithdrawRespectReservationsDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_WithdrawCapToNeed".Translate(),
                ref s.withdrawCapToNeed,
                "DigitalStoragePatch_WithdrawCapToNeedDesc".Translate());
            l.Gap(2f);
            // v0.59：建造取料的搬运记账修复（H4/方案A，纯修复，默认开）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_ConstructPickupAmountFix".Translate(),
                ref s.constructPickupAmountFix,
                "DigitalStoragePatch_ConstructPickupAmountFixDesc".Translate());
            l.Gap(2f);
            // v0.60：FIX-11 —— 同一配方允许重复添加任务（实验性，默认关）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftAddDuplicate".Translate(),
                ref s.craftAddDuplicate,
                "DigitalStoragePatch_CraftAddDuplicateDesc".Translate());
            l.Gap(2f);
            // v0.60：FIX-12 —— 工作台按需分配、计划间公平共享（实验性，默认关）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftFairBenchAlloc".Translate(),
                ref s.craftFairBenchAlloc,
                "DigitalStoragePatch_CraftFairBenchAllocDesc".Translate());
            l.Gap(2f);
            // v0.60：FIX-13 —— 取料按线认领，避免并行互踩清零（实验性，默认关）。
            l.CheckboxLabeled(
                "DigitalStoragePatch_CraftIngredientClaims".Translate(),
                ref s.craftIngredientClaims,
                "DigitalStoragePatch_CraftIngredientClaimsDesc".Translate());
            l.Gap(24f);

            // ───────────── I. 存储核心 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionCoreStorage");
            l.CheckboxLabeled(
                "DigitalStoragePatch_KeepCoreFilterOnLoad".Translate(),
                ref s.keepCoreFilterOnLoad,
                "DigitalStoragePatch_KeepCoreFilterOnLoadDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_CoreRejectUnfinished".Translate(),
                ref s.coreRejectUnfinished,
                "DigitalStoragePatch_CoreRejectUnfinishedDesc".Translate());
            l.Gap(2f);
            // v0.52：RitualOfferingFromCore 已迁入「核心库存可用性」分区
            //        （它同样是"让核心库存用得上"，与「存储核心」自身的存储行为不是一类）
            // v0.52：GiveItemsFromCore / InventoryStockFromCore / BloodTransfusionFromCore 三项
            //        已迁入「核心库存可用性」分区 —— 它们与那一批是同一类（让核心库存"用得上"），
            //        留在「存储核心」里会让玩家在两处找同一件事。
            l.CheckboxLabeled(
                "DigitalStoragePatch_FoodRestrictionAlign".Translate(),
                ref s.foodRestrictionAlign,
                "DigitalStoragePatch_FoodRestrictionAlignDesc".Translate());
            l.Gap(24f);

            // ───────────── 物品可见性兜底（二）（v0.45） ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionVisibilityB");
            l.CheckboxLabeled(
                "DigitalStoragePatch_HemogenFeedFromCore".Translate(),
                ref s.hemogenFeedFromCore,
                "DigitalStoragePatch_HemogenFeedFromCoreDesc".Translate());
            l.Gap(2f);
            // v0.52：由「存储核心」迁入（与上面一条同属"血原质"）
            l.CheckboxLabeled(
                "DigitalStoragePatch_BloodTransfusionFromCore".Translate(),
                ref s.bloodTransfusionFromCore,
                "DigitalStoragePatch_BloodTransfusionFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_HemogenPackFromCore".Translate(),
                ref s.hemogenPackFromCore,
                "DigitalStoragePatch_HemogenPackFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_BingeDrugFromCore".Translate(),
                ref s.bingeDrugFromCore,
                "DigitalStoragePatch_BingeDrugFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_MoveDrugsToInventoryFromCore".Translate(),
                ref s.moveDrugsToInventoryFromCore,
                "DigitalStoragePatch_MoveDrugsToInventoryFromCoreDesc".Translate());
            l.Gap(2f);
            // v0.52：两项由「存储核心」迁入（与上面一项同属"把东西备到小人身上"）
            l.CheckboxLabeled(
                "DigitalStoragePatch_InventoryStockFromCore".Translate(),
                ref s.inventoryStockFromCore,
                "DigitalStoragePatch_InventoryStockFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_GiveItemsFromCore".Translate(),
                ref s.giveItemsFromCore,
                "DigitalStoragePatch_GiveItemsFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_TurretSuppliesFromCore".Translate(),
                ref s.turretSuppliesFromCore,
                "DigitalStoragePatch_TurretSuppliesFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_FillBarrelFromCore".Translate(),
                ref s.fillBarrelFromCore,
                "DigitalStoragePatch_FillBarrelFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_DyeFromCore".Translate(),
                ref s.dyeFromCore,
                "DigitalStoragePatch_DyeFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_MonumentStuffFromCore".Translate(),
                ref s.monumentStuffFromCore,
                "DigitalStoragePatch_MonumentStuffFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_XenogermFromCore".Translate(),
                ref s.xenogermFromCore,
                "DigitalStoragePatch_XenogermFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_BiosculpterFromCore".Translate(),
                ref s.biosculpterFromCore,
                "DigitalStoragePatch_BiosculpterFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_WardenHemogenFromCore".Translate(),
                ref s.wardenHemogenFromCore,
                "DigitalStoragePatch_WardenHemogenFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_GatheringEatFromCore".Translate(),
                ref s.gatheringEatFromCore,
                "DigitalStoragePatch_GatheringEatFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_EatRandomFromCore".Translate(),
                ref s.eatRandomFromCore,
                "DigitalStoragePatch_EatRandomFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_AmmoFromCore".Translate(),
                ref s.ammoFromCore,
                "DigitalStoragePatch_AmmoFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_ShardFromCore".Translate(),
                ref s.shardFromCore,
                "DigitalStoragePatch_ShardFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_FixedIngredientFromCore".Translate(),
                ref s.fixedIngredientFromCore,
                "DigitalStoragePatch_FixedIngredientFromCoreDesc".Translate());
            l.Gap(2f);
            // v0.52：由「存储核心」迁入 —— 与上面两项同属异象（Anomaly）相关，放一起更好找
            l.CheckboxLabeled(
                "DigitalStoragePatch_RitualOfferingFromCore".Translate(),
                ref s.ritualOfferingFromCore,
                "DigitalStoragePatch_RitualOfferingFromCoreDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_FeedPatientReservable".Translate(),
                ref s.feedPatientReservable,
                "DigitalStoragePatch_FeedPatientReservableDesc".Translate());
            l.Gap(2f);
            l.CheckboxLabeled(
                "DigitalStoragePatch_CoreFoodScoringMood".Translate(),
                ref s.coreFoodScoringMood,
                "DigitalStoragePatch_CoreFoodScoringMoodDesc".Translate());
            l.Gap(24f);

            // ───────────── 界面 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionUI");
            l.CheckboxLabeled(
                "DigitalStoragePatch_UI_CraftButton".Translate(),
                ref s.craftAutomationButton,
                "DigitalStoragePatch_UI_CraftButtonDesc".Translate());
            // ★ 必须 here 每帧同步一次：MainButtonDef.buttonVisible 只有在被写入时才会变，
            //   而设置界面是唯一会改它的地方。忘了这行的话，开关要到**下次重启**才生效
            //   （v0.15 第一次实测就是这么翻车的）。
            Patch_CraftAutomationButton.Apply();
            l.CheckboxLabeled(
                "DigitalStoragePatch_UI_WithdrawPicker".Translate(),
                ref s.withdrawPickerEnabled,
                "DigitalStoragePatch_UI_WithdrawPickerDesc".Translate());
            l.Gap(24f);

            // ───────────── E. 工作优先级 ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionPriority");
            if (l.RadioButton(
                "DigitalStoragePatch_PriorityModDefault".Translate(),
                s.priorityMode == DsPriorityMode.ModDefault, 0f,
                "DigitalStoragePatch_PriorityModDefaultDesc".Translate()))
            {
                s.priorityMode = DsPriorityMode.ModDefault;
            }
            if (l.RadioButton(
                "DigitalStoragePatch_PriorityVanillaFirst".Translate(),
                s.priorityMode == DsPriorityMode.VanillaFirst, 0f,
                "DigitalStoragePatch_PriorityVanillaFirstDesc".Translate()))
            {
                s.priorityMode = DsPriorityMode.VanillaFirst;
            }
            l.Gap(2f);
            // 只在模式真的变了时才干活（内部有 early-out），所以每帧调用也没开销。
            Patch_WorkPriority.Apply();
            l.Gap(24f);

            // ───────────── Mod 兼容性（针对其它 mod 的"特殊识别逻辑"） ─────────────
            SectionHeader(l, "DigitalStoragePatch_SectionModCompat");
            l.CheckboxLabeled(
                "DigitalStoragePatch_AvoidCoreDirectHaul".Translate(),
                ref s.avoidCoreDirectHaul,
                "DigitalStoragePatch_AvoidCoreDirectHaulDesc".Translate());
            l.CheckboxLabeled(
                "DigitalStoragePatch_TradePayFromCore".Translate(),
                ref s.tradePayFromCore,
                "DigitalStoragePatch_TradePayFromCoreDesc".Translate());
            l.CheckboxLabeled(
                "DigitalStoragePatch_BamMaterialsFromCore".Translate(),
                ref s.bamMaterialsFromCore,
                "DigitalStoragePatch_BamMaterialsFromCoreDesc".Translate());
            l.Gap(24f);

            SectionHeader(l, "DigitalStoragePatch_SectionPower");
            l.CheckboxLabeled(
                "DigitalStoragePatch_PowerExempt".Translate(),
                ref s.powerExempt,
                "DigitalStoragePatch_PowerExemptDesc".Translate());
            l.Gap(24f);

            SectionHeader(l, "DigitalStoragePatch_SectionDebug");
            l.CheckboxLabeled(
                "DigitalStoragePatch_Diagnostics".Translate(),
                ref s.diagnosticsEnabled,
                "DigitalStoragePatch_DiagnosticsDesc".Translate());

            l.End();
            // 实测内容高（Listing_Standard 走完才知道），下一帧用它作为滚动范围。
            contentHeight = Mathf.Max(l.CurHeight + 16f, inRect.height);

            Widgets.EndScrollView();
        }
    }
}
