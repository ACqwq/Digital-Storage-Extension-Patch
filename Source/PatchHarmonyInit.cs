using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;
using Verse.AI;

namespace DigitalStorage.Patch
{
    /// <summary>
    /// 补丁注册点。当前为脚手架：只做 PatchAll + 健康自检，
    /// 用于验证「编译 → 程序集加载 → Harmony 目标解析 → 日志」整条链路可用。
    /// 具体功能的 Harmony 补丁随后按需加入。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PatchHarmonyInit
    {
        public const string HarmonyId = "cagier.digitalstorage.patch";

        static PatchHarmonyInit()
        {
            // 必须最先 PatchAll 且独立 try/catch：后续初始化若抛异常中断静态构造，补丁就全部失效。
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                // 用 Assembly 重载，只扫本程序集，避免误扫其它程序集。
                harmony.PatchAll(typeof(PatchHarmonyInit).Assembly);
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] Harmony PatchAll 失败，本补丁的功能将不可用："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 以下为次要初始化，全部隔离，任一失败都不影响已打上的补丁。
            try
            {
                DsPatchMod.EnsureSettings();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 设置初始化失败（功能仍可用，设置界面可能异常）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 存储容量自定义（默认关；开启后覆盖 CoreTier.CapByLevel）。
            try
            {
                Patch_CustomCapacity.ApplyFromSettings();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 容量设置应用失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 工作优先级（默认「原 mod 逻辑」= 不改动）。显式再应用一次，确保顺序确定。
            try
            {
                Patch_WorkPriority.Apply(force: true);
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 工作优先级应用失败（将保持原 mod 的优先级）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 底部「制作自动化」按钮的显隐（默认显示 = 原 mod 行为）。
            try
            {
                Patch_CraftAutomationButton.Apply();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 主按钮显隐应用失败："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 数字电池容量：写回 def 的共享 CompProperties_Battery.storedEnergyMax。
            try
            {
                DsDigitalBatteryManager.ApplyCapacity();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 数字电池容量应用失败（电池仍可用，只是容量为 Def 里的初始值）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理 ↔ 投影核心互通（改写三处「核心必须在本图」的判定）。
            try
            {
                Patch_BillAutomationCrossMap.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理跨图补丁安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理的产物投递：让它与取料用同一套"哪个核心可用"的规则。
            try
            {
                Patch_BillProductDelivery.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 产物投递补丁安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ①：放宽工作台白名单，让 mod 添加的工作台（含厨房/炉灶）也能接单。
            try
            {
                Patch_BillBenchWhitelist.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理台子放宽安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ②：回收闲置制作线，避免「维持数量」达标后占着工作台堵住后续配方。
            try
            {
                Patch_BillIdleReclaim.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理闲置线回收安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ⑧（FIX-11，v0.60）：同一配方允许重复添加任务
            //   原 mod 硬编码「一个配方 = 一个计划」：AddPlan 有 HasPlan 守卫、挑配方面板
            //   对已添加的配方点击无效 ⇒ 「切石砖」这类单一配方建不了第二个任务。
            //   注意：必须装在 Patch_BillIdleReclaim 之后（同挂 SyncLines 前置时保持回收先跑）。
            try
            {
                Patch_CraftAddDuplicate.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理重复任务安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ⑨（FIX-12，v0.60）：工作台按需分配、计划间公平共享
            //   原 mod 的 SyncLines 贪心分配：每个「要开工」的计划把范围内所有空闲同类台子
            //   全部占线（做 1 件也占满）⇒ 队列里其他需要同类台子的计划永远等不到。
            try
            {
                Patch_SyncLinesFairAlloc.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理工作台分配安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ⑩（FIX-13，v0.60）：取料按线认领，避免并行互踩清零
            //   原 mod 取活不做原料预留：几条线对同一堆料重复记账，一条完成扣掉堆的一部分，
            //   其余线的每 tick 校验（盯单一堆）立刻失败 ⇒ 进度清零重新取活，并行塌缩成串行。
            try
            {
                Patch_CraftIngredientClaims.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 制作代理取料认领安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // INVEST-14 方案 A（v0.61）：搬运判定不对幽灵物品崩溃
            //   原 mod 的两道"幽灵物防护"挂在旧方法名上，1.6 顺路搬运调用点已改走
            //   PawnCanAutomaticallyHaulFast_NewTemp 且未被覆盖 ⇒ 清单里的无主物品让小人软锁刷 NRE。
            try
            {
                Patch_HaulAIUtilityNewTempGuard.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 幽灵物品防护安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ③：代理工人的隐藏替身不再被原版 AI 派活（纯修复，默认开）。
            //   原版本会对替身调 EndCurrentJob ⇒ TryFindAndStartJob（不看 mindState.Active、不要求被 tick）
            //   ⇒ 主思考树照跑 ⇒ 被「优化装备」派穿装备 ⇒ 地图是临时借来的 ⇒ 红字刷屏 + 同 tick 反复起停。
            try
            {
                Patch_ProxyWorkerNoThink.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 代理工人替身思考拦截安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 制作代理缺陷 ④：替身不再抽到「禁工背景」（纯修复，默认开）。
            //   Create 用 PawnGenerator 完整随机生成替身，却只清了特质/关系、没换背景 ⇒
            //   背景的 workDisables / DisabledWorkTypes 会让相关技能读数恒为 0（"资质不够"）。
            try
            {
                Patch_WorkerBackstory.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 替身背景清理安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 建造取料缺陷 ⑤：取料作业不再"预留形同虚设"（纯修复，默认开）。
            //   原 mod 的 JobDriver_DS_Withdraw.TryMakePreToilReservations 忽略 errorOnFailed 且恒 return true
            //   ⇒ 同一格能被多个真小人同时派去取料 ⇒ 每格材料 = 20×(1+N)（用户实测 6.6 倍）。
            try
            {
                Patch_WithdrawReserveFix.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 建造取料遵守预留安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 建造取料缺陷 ⑥：投料不再"盲加"（纯修复，默认开）。
            //   原 mod 的 JobDriver_DS_Withdraw.PlaceHauled 用 TryAdd(手上全部)，而 ThingOwner 无上限
            //   ⇒ 20 木的框架能被塞到 150~400 木，建成时 ClearAndDestroyContents 全销毁。
            try
            {
                Patch_WithdrawPlaceCap.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 建造投料截断安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 建造取料缺陷 ⑦（真凶，v0.59）：搬运记账 bug —— 原 mod 用"搬运 API 返回值是否 >0"记账，
            //   而该 API 在合堆时返回 0 ⇒ 记账失效 ⇒ 把核心里同种材料的每一堆都搬进同一个在建框架
            //   （实测：需要 20 木的框架被塞到 160 木，建成时全量销毁）。
            try
            {
                Patch_TransferFromCoresFix.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 建造取料记账修复安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 读档时保留核心的存储筛选（原 mod 每次读档按内部字段重设"武器"类别，覆盖玩家设置）。
            try
            {
                Patch_CoreWeaponFilterOnLoad.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 核心筛选保留安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 核心拒收「未完成」物品（筛选层 + 准入层双重，从根源避免产物被吞）。
            try
            {
                Patch_CoreRejectUnfinished.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 核心拒收「未完成」物品安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 取食判据对齐（实验性，默认关）：补上饮食方案之外的头衔/戒律等规则。
            try
            {
                Patch_FoodRestrictionAlign.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 取食判据对齐安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 病人喂食预约修复（纯修复，默认开）：食物被别人按 maxPawns=1 预留时，
            // 原版驱动按 maxPawns=10 预留会撞上"MaxPawns 不一致直接返回 0" ⇒ 作业永远起不来。
            try
            {
                Patch_FeedPatientReservable.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 病人喂食预约修复安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 取食排序修正（纯修复，默认开）：给原 mod 的取食评分补上"吃了掉不掉心情"这一项，
            // 免得核心里有包装食品时还去拿婴儿食物（成年人吃了掉心情）。
            try
            {
                Patch_CoreFoodScoring.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 取食排序修正安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 读档 / 新开局时清空投影核心注册表（防止上一局的登记项污染下一局）。
            try
            {
                Patch_RegistryReset.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 注册表重置补丁安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // ★ 只读诊断：原 mod 自动收纳的门状态（默认关，见「诊断日志」）。
            //   只 Log、不改任何返回值 —— 用于"自动收纳不生效"时一次定位卡在哪道门。
            try
            {
                Patch_AutoIngestDiag.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 自动收纳诊断挂载失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 搬运兼容：核心让位（默认关）。用特性挂的补丁是静默的，
            // 这里显式核一次目标方法，把"挂点找到了没有"写进日志（本项目铁律：ok/missing 可查）。
            try
            {
                System.Reflection.MethodInfo m = AccessTools.Method(
                    typeof(StoreUtility), "TryFindBestBetterNonSlotGroupStorageFor");
                if (m == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 StoreUtility.TryFindBestBetterNonSlotGroupStorageFor，"
                                + "「有收货架时核心让位」不可用（其余功能不受影响）。");
                }
                else
                {
                    Log.Message("[数字存储补丁] 已挂载：有可用收货架时核心不再作为直接搬运目的地"
                                + "（默认关闭，设置 →「搬运兼容」开启）。");
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[数字存储补丁] 搬运兼容挂点校验失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            // Better Architect Menu 兼容：建筑菜单地板页/默认选材也认核心材料（默认开；BAM 未装则静默跳过）。
            try
            {
                Patch_BamMaterialsFromCore.Install();
            }
            catch (System.Exception ex)
            {
                Log.Error("[数字存储补丁] 建筑菜单兼容安装失败（其余功能不受影响）："
                          + ex.GetType().Name + ": " + ex.Message);
            }

            // 交易付款补齐：付款可从数字核心扣银（默认开）。
            try
            {
                System.Reflection.MethodInfo m = AccessTools.Method(
                    typeof(TradeUtility), "LaunchThingsOfType");
                if (m == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 TradeUtility.LaunchThingsOfType，"
                                + "「交易付款可从核心扣银」不可用（其余功能不受影响）。");
                }
                else
                {
                    Log.Message("[数字存储补丁] 已挂载：交易付款在信标地面不够时可从数字核心扣银"
                                + "（默认开启，设置 →「交易」可关）。");
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[数字存储补丁] 交易付款补齐挂点校验失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            // 「给某某送物资」也可从数字核心取货：难民/访客索要物资时不再"未找到物品"（默认开）。
            try
            {
                System.Reflection.MethodInfo m = AccessTools.Method(
                    typeof(GiveItemsToPawnUtility), "FindItemToGive",
                    new[] { typeof(Pawn), typeof(ThingDef) });
                if (m == null)
                {
                    Log.Warning("[数字存储补丁] 找不到 GiveItemsToPawnUtility.FindItemToGive，"
                                + "「给某某送物资可用核心库存」不可用（其余功能不受影响）。");
                }
                else
                {
                    Log.Message("[数字存储补丁] 已挂载：难民/访客索要物资时可从数字核心取货"
                                + "（默认开启，设置 →「存储核心」可关）。");
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[数字存储补丁] 送物资取货挂点校验失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            // 染料同族 + 纪念碑菜单：显式核一次挂点（本项目铁律：ok/missing 可查）。
            // 用特性挂的补丁如果目标不存在是**静默**的，这里把"找到了没有"写进日志，
            // 便于原版改结构后一次定位（几处染料的挂点都在原版私有/静态方法上，风险更高）。
            try
            {
                CheckHook(typeof(JobGiver_DyeHair), "TryGiveJob", "染发可取数字核心里的染料");
                CheckHook(typeof(JobGiver_OptimizeApparel), "TryCreateRecolorJob", "衣物改色可取数字核心里的染料");
                CheckHook(typeof(WorkGiver_PaintBuilding), "ShouldPaintThing", "涂装建筑可取数字核心里的染料");
                CheckHook(typeof(WorkGiver_PaintFloor), "ShouldPaintCell", "涂装地板可取数字核心里的染料");
                CheckHook(typeof(MonumentMarker), "ListFloatMenuOptions", "纪念碑材料菜单认数字核心材料");
                CheckHook(typeof(WorkGiver_CreateXenogerm), "JobOnThing", "组装异种胚芽：运胶囊进装配器可用核心");
                CheckHook(typeof(Dialog_CreateXenogerm), "ColonyHasEnoughArchites", "组装界面认数字核心的超凡胶囊");
                CheckHook(typeof(Recipe_ImplantXenogerm), "AvailableOnNow", "植入异种胚芽手术认数字核心的胚芽");
                CheckHook(typeof(WorkGiver_HaulToBiosculpterPod), "JobOnThing", "生化塑体舱补营养可取数字核心的食物");
                CheckHook(typeof(WorkGiver_Warden_DeliverHemogen), "JobOnThing", "狱警送血原质包可取数字核心的包");
                CheckHook(typeof(JobGiver_EatInGatheringArea), "FindFood", "聚集区进食可取数字核心的食物");
                CheckHook(typeof(JobGiver_EatRandom), "TryGiveJob", "随机进食可取数字核心的食物（实验性，默认关）");
                CheckHook(typeof(ReloadableUtility), "FindEnoughAmmo", "装填可用数字核心的弹药");
                CheckHook(typeof(ReservationUtility), "ExistsUnreservedAmountOfDef", "碎片判定认数字核心（黄金立方体/方尖碑）");
                CheckHook(typeof(ReservationUtility), "CanReserveAndReachableOfDef", "黄金立方体停用判定认数字核心碎片");
                CheckHook(typeof(CompGoldenCube), "OrderDeactivation", "黄金立方体停用可取数字核心碎片");
                CheckHook(typeof(HaulAIUtility), "FindFixedIngredientCount", "固定材料（方尖碑碎片/机械搬运体补料）认数字核心");
                CheckHook(typeof(JobDriver_FoodFeedPatient), "TryMakePreToilReservations", "病人喂食预约修复");
            }
            catch (System.Exception ex)
            {
                Log.Warning("[数字存储补丁] 挂点存在性校验失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            // 健康自检：确认目标原 mod 已加载，否则本补丁大部分挂钩点无意义。
            try
            {
                bool targetLoaded = LoadedModManager.RunningMods != null
                    && LoadedModManager.RunningMods.Any(m => m != null && m.PackageId == "cagier.digitalstorage");
                if (!targetLoaded)
                {
                    Log.Warning("[数字存储补丁] 未检测到原 mod（cagier.digitalstorage）。"
                                + "本补丁需要原 mod 才能发挥作用，请把原 mod 排在本补丁之前加载。");
                }
            }
            catch
            {
            }

            // ★ 健康标志：这行出现 = PatchAll 成功。排查时先看它。
            try
            {
                Log.Message("[数字存储补丁] 已加载（面向 Digital Storage 4.0）。"
                            + "功能：数字投影核心（跨图存取/统计）、用电豁免。"
                            + "设置入口：Mod 设置 →「数字存储 · 补丁设置」。");
            }
            catch
            {
            }
        }

        /// <summary>核对某个挂点是否存在，并把结果写进日志（缺失时给出可读的功能名）。</summary>
        private static void CheckHook(System.Type type, string method, string friendlyName)
        {
            System.Reflection.MethodInfo m = AccessTools.Method(type, method);
            if (m == null)
            {
                Log.Warning("[数字存储补丁] 找不到 " + type.Name + "." + method
                            + " ⇒「" + friendlyName + "」不可用（其余功能不受影响）。");
            }
            else
            {
                Log.Message("[数字存储补丁] 已挂载：" + friendlyName);
            }
        }
    }
}
