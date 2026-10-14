# 数字存储·扩展补丁 / Digital Storage — Extension Patch

为《**【CA】数字存储-Digital Storage 4.0**》打造的**社区扩展补丁**（RimWorld 1.7）：
补上原 mod 缺少的功能，修复已知问题。侧载实现 —— **不改动原 mod、不改动游戏本体**；
绝大多数功能都能单独关闭，关闭即回到原 mod 行为。

> ⚠️ 本补丁为**非官方补丁**，与原 mod 作者无隶属关系，也不包含原 mod 的任何代码或资产。
> 原 mod：**【CA】数字存储-Digital Storage 4.0**（工坊 `3623684313`，作者 **Cagier.阳 / CagierAsh123**）
> —— 开源仓库：**https://github.com/CagierAsh123/Digital-Storage**。
> 本补丁遵循原 mod 的许可协议（**非商用 · 署名 · 衍生同协议开源**），完整条款见 [LICENSE](LICENSE)。

> 🛠 **维护状态**：本仓库用于公开本补丁的**完整源代码**（协议要求）。作者精力有限、GitHub 侧不常维护 ——
> **问题反馈与版本更新请以创意工坊页为准**；欢迎 Fork 自取（请遵守 LICENSE）。

---

## 功能一览

### 新增三个建筑
- **数字投影核心**：把任意一张地图上的数字核心「投影」到当前地图。物品搬进去就直接进入那张图的核心，本图的资源统计与交易也会算上它；也能反过来把核心里的物品铺到本图优先级更高的储存区。
- **数字收货架**：只进不出的收货口。放上去的物品自动转入数字核心（收什么跟随核心筛选、自动同步）；每格独立投料位，多人可同时投料。
- **数字电池**：超出平衡线的电存入所有电池共用的数字库，缺电时自动回填 —— 跨地图用电自动完成。容量、平衡线、平衡周期可调。

### 核心库存「用得上」（每项可单独开关）
存进核心后，原本因「看不到核心库存」而失败或不出现的操作现在都能用：
医疗与药物、炮塔补弹与装填、麦汁入桶、染发/改色/涂装、异种胚芽全链、生化舱补营养、狱警送血原包、
方尖碑与黄金立方体停用、战争女王补钢、聚会进食、难民与访客索要物资、异象仪式祭品、按心情挑食物等。

### 制作代理修复（bug 修复）
- 识别其它 mod 的灶台 / 厨房 / 工作台（不再「配方选得到却永远缺料」）
- 「维持数量」达标后让出工作台；打开制作面板时可正常调整游戏速度
- 更多实验性修复（同配方重复任务 / 工作台公平分配 / 并行取料不互相重置），默认关、可在设置启用

### 便利性
- **核心命名**：给核心起名字，多核心一眼分清
- **投影核心面板**：查看被绑定核心的库存、取出到脚下
- **增强取料面板**：右键取出 = 带图标 / 分组 / 搜索 / 「取满」的面板
- **跨图交易**（默认关）、**自动补货**（默认关）

### 修复与兼容
- 修复：核心筛选读档被重置、半成品被吞、废料容器清不掉、送物资卡死、有毒垃圾取不出、读档幽灵物品报错刷屏、
  建造材料被成堆搬进工地、数字工人替身被原版 AI 派活、天灯节（Ideology）认不到核心原木、通讯台「索要位置」从核心付款不扣银 等
- 兼容：Hauler's Dream 等搬运类 mod、Universal Trade Hub、Better Architect Menu 等（注意：原 mod 自带自动收纳与 Pick Up And Haul 互斥，10 月版起会主动提示）

> 完整设置项与逐条说明都在游戏内：**选项 → Mod 设置 →「数字存储 · 补丁设置」**。

---

## 安装

1. **前置**：RimWorld 1.7 · 原 mod《【CA】数字存储-Digital Storage 4.0》· Harmony；
2. **工坊订阅（推荐）**：订阅本补丁的工坊条目（ID `3815069573`），并在 Mod 列表里把本补丁排在**原 mod 与 Harmony 之后**；
3. **手动安装**：把本目录整体放进 `<RimWorld>/Mods/`（文件夹名随意），同样确保排序在原 mod 之后。

## 从源码构建

需要：Windows + 一份装好**原 mod 与 Harmony** 的 RimWorld 1.7 + Python 3.8+，以及一个 Roslyn 编译器
（推荐安装 .NET SDK；已装 Visual Studio / Build Tools 也可）。

把本仓库（或 Release 源码包）放进 `<RimWorld>/Mods/` 后，一条命令即可：

```bash
python build.py
```

脚本会自动探测：游戏目录、Harmony（工坊 2009463077）、原 mod DLL（工坊 3623684313）、Roslyn 编译器。
也可以手动指定：

```bash
python build.py --game-dir "<你的Steam库>/steamapps/common/RimWorld" \
                --harmony "<你的Steam库>/workshop/content/294100/2009463077/Current/Assemblies/0Harmony.dll" \
                --orig-dll "<你的Steam库>/workshop/content/294100/3623684313/Current/Assemblies/DigitalStorage40.dll"
```

产物：`Assemblies/DigitalStoragePatch.dll`。注意：不同机器/不同编译器版本构建出的字节可能不同，属正常现象；
官方发布版由维护者用同一套源码构建（见工坊 / Releases）。

## 仓库结构

```
About/          工坊元数据（名称、描述、依赖声明）
Defs/           新增建筑与研究的定义
Languages/      中英文文本（Keyed）
Patches/        XML 兼容补丁
Source/         全部 C# 源码（Harmony 补丁为主）
Textures/       贴图
build.py        开源版构建脚本（见上）
```

## 反馈与贡献

- **Bug 反馈**：请附 `Player.log`（`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`）与复现步骤；
  崩溃/红字类问题请注明**原 mod、本补丁、Harmony 的版本**与是否安装了其它搬运类 mod。
- **代码贡献**：欢迎 Issue / PR。请注意本项目风格：所有功能开关化、默认行为尽量与"关闭"时保持一致。

## 许可

本补丁以**「非商用 + 署名 + 相同方式共享」**协议发布（等效于原模组 DS License）—— 见 [LICENSE](LICENSE)：

- 允许非商用使用 / 修改 / 分发 / 参考（含衍生创作）；
- 任何分发、衍生或引用都必须同时署名**原模组作者 Cagier.阳**（含主仓库链接）与**本补丁作者 AC酱qwq**（含本仓库链接）；
- 衍生作品必须以等效协议发布，并**公开完整源代码**（不得只发 dll）。

本协议仅覆盖本补丁自身代码与文本；不包含、也不授权原 mod（Digital Storage 4.0）与 RimWorld 游戏本体的任何内容。

## 致谢

- **Cagier.阳 (CagierAsh123)** —— 《【CA】数字存储-Digital Storage 4.0》原作者，
  开源仓库：https://github.com/CagierAsh123/Digital-Storage —— 没有原 mod 就没有这个补丁；
- **Harmony** 作者与 RimWorld 社区；
- 所有提交过反馈（日志 / 存档 / 复现步骤）的朋友。
