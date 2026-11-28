# 《The Bazaar》自建服务端与客户端增强插件（V15.5 · 种子模式、每日镜像编排台 `/planner`、跨服分享码与搜种器）

> [!IMPORTANT]
> **客户端版本要求（必看）**：本项目**必须使用 Steam 官方测试服（PTR 分支）客户端才可以正常使用，暂不支持官方正式服最新版！**
> 请先在 Steam 库右键《The Bazaar》→ **属性** → **测试版（Betas）**，将参与的测试切换至官方测试服（PTR，如 `1.0.13122-ptr`）更新完成后，再安装本补丁与运行服务端。

一个面向《The Bazaar》（Steam 官方 PTR 客户端兼容）的开源独立自建服务器、客户端插件生态（`BazaarModKit`）、零代码 Web 创意工坊（`/workshop`）、对手与种子编排台（`/planner`）与图形化启动器（`BazaarHub`），支持**单机离线开箱即玩**、**自建多人联机服务器**、**自定义种子开局与每日 PvP 对手编排/分享**、**零代码/AI Skill 创作自定义卡牌、开放沙盒机制与奇遇**、**5 大创意新玩法模式**与**8/10 人自定义锦标赛**，并严格对照官方 Wiki、反编译逻辑与 `GameData.db` 还原战斗、商店、事件、野怪与升级流程。

> 本开源仓库不含作者个人服务器地址、SSH 配置及原版受版权保护的二进制资源；客户端原版资产由你本地已安装的 Steam 游戏本体提供。

---

## 核心特性（V15.5 里程碑升级）

- **V15.5 种子模式、每日镜像编排台（`/planner`）、跨服分享码与搜种器**：
  - **全链路确定性种子系统（`RunRandom` 统一随机流）**：每局自动生成并记录可读 8 位种子码（`S` + 7 位规范字符，如 `S3KZ9QW1`）；支持手输或随机生成种子开局，确保同种子 + 同英雄 + 同规则/Mod 下每日事件、野怪、商店货架、升级与战利品完全一致，且断线重连/服务重启后不走偏；全随机对手模式依然保持每晚真随机；
  - **独立 Web 对手与种子编排台（`/planner`）**：支持从游戏内 `F8 → 常规规则 → 种子与对手` 一键免密跳转（或口令登录），提供三大工作区——**安排对手**（从 29,000+ 镜像池筛选、10 格盘面手搓或克隆微调、支持修改伤害/护盾/治疗/灼烧/剧毒/回复/暴击/冷却并实时查看官方中文效果说明）、**找种子**（后台按期望事件条件搜索匹配种子并预览 Day 1–N 事件日历）、**分享码**（支持方案与对局双向导出/导入）；
  - **对手方案与跨服分享闭环**：方案统一绑定「配套种子 + 每日对手」，支持将进行中或刚结束的一局一键存成方案（打过的天保留真实对手、未打到的天自动从镜像池补齐）；支持同服短码（`BZ-XXXX-XXXX`）与跨服离线长码（`BZR1:...` / 分享文件），在游戏主菜单、局内规则胶囊与规则页卡片均可一键导入/分享，并自动提示缺失 Mod 差异；
  - **零侵入与全面兼容**：未设置种子/对手方案的普通对局体验与此前 100% 一致；种子局与对手方案局不附加额外限制，正常参与排行榜、镜像池与元进度；SQLite 持久化自动平滑迁移，旧存档与旧客户端规则保存完全向后兼容。
- **V15.4 自定义 Mod 全链路通用兼容、原生 3D 融合机制徽章、ESC 菜单暗金重构与 23,093 套真玩家幽灵镜像**：
  - **自定义 Mod 贴图与全 4 条渲染通路彻底隔离**：根治异步材质加载期间贴图回退至原版借壳卡的问题，支持原版材质快照还原防串图；打通 3D 物品、3D 技能、2D 详情预览与 3D 奇遇遭遇的按需同步与主线程热重刷，并支持 PvP 幽灵/锦标赛对手的自定义物品与技能（含战后复盘持久呈现）；
  - **原生 3D World-Space 通用机制徽章体系**：彻底废除 `OnGUI()` 穿模黑框，直接挂接卡牌原生 3D 层级（随卡移动/翻面、自动被暂停菜单与悬浮窗遮挡），支持任意自定义机制与卡牌直配 `counterDisplays`（`pips`/`bar`/`gem`/`pill` 样式、进度/百分比/倒计时/倍率/自定义单位与位置）；
  - **通用自定义卡/补丁卡详情与升阶/附魔预览全栈对齐**：自动合成五阶（`Bronze..Legendary`）属性表与全 13 种官方附魔结构化预览运算表，右键详情与悬停下一阶升级对比在中文/英文界面下 100% 准确呈现，不再混入借壳原版卡数值；
  - **输出属性增益防误污染守卫**：战中 `ModifyCardAttribute` 与战后 `ApplyPostCombatMechanicOps` 共享同源能力判定，默认严禁给非产出物品凭空写入伤害/护盾宝石（同时兼容 0 初始成长型武器与显式 `grantIfMissing: true`）；
  - **ESC 暂停菜单与设置入口重排 & PvP 对手头像恢复**：将局内自建服快捷入口重排为贴合原版暗金框体的单行三列 Dock（杜绝溢框与裁切）；修复 PvP 进战漏发 `CardPurchased(Socket=null)` 导致的对手英雄头像丢失；
  - **事件/遭遇规则与 PvP 真盘镜像全面升级**：清理 6 张废弃/系统事件并按全 8 英雄精确划分（恢复 `Pol`）、修复同日遭遇刷出即去重与免费右槽 `Workshop` 误判、恢复官方默认开局参数（`15 金币 / 5 收入 / 3 经验 / 20 声望 / 300 生命`），并融合社区真实对局数据构建 **23,093 套 Day 1–15 高质量 PvP 幽灵镜像池**（硬剔演菜 AI，优先本服真人与高质量真盘）。
- **V15.3 全栈官方对齐修复、插件深度治理、安卓兼容与 BazaarHub 暗金界面重构**：
  - 服务端核心机制深度对齐与修复：补齐卖卡提前腾格与原位回滚、PvP 幽灵镜像去光环与防双重冷却缩减、断线重连 `SocketsUnlocked`/`BoardOverridden`/货架折扣与遭遇光环还原、遭遇引擎防幽灵卡与非 GUID Mod 兼容，以及安卓/外部登录兼容（`HEAD /maintenance.json`、SHA-256 ETag/304、受保护外部鉴权与中文昵称全链路编码对齐）；
  - 游戏插件全面实效治理：逐一对照官方客户端运行时签名对齐 `GameSimHandler`、`BoardManager`、`CardTooltipData`、`LruCache` 与音频/场景阻塞事件，补齐 9 项自定义规则字段对齐、SSE 退避重连与热切服事务保护；
  - `BazaarHub` 图形启动器全新暗黑黑曜石 × 暖鎏金视觉重构：纯自绘暗黑控件零露白、矢量图标、高 DPI `PerMonitorV2` 适配与 1060×680 紧凑布局防裁切，内置一键更新、自建服切换与社区工坊直连。
- **127 项全库深度一致性对齐 + Patch 20.2 官方事件调度**：
  - 完整对齐官方 `50ms` 战斗时钟与沙暴节拍、同跳 DoT 顺序（`Poison → Regen → Burn`）、单次施放原子暴击与护盾/治疗暴击标识、灼烧护盾拆分扣减、再生与治疗解耦、附魔同名动作独立域隔离、Karnok 完整战斗属性与头像包、以及 4 张元进度全表（`challenges` 80 条、`collectibles` 1,644 条、`seasons` 21 赛季、`tooltips` 136 条）；
  - 接入官方 Patch 20.2 全量 170 项白昼遭遇天数窗、小时禁令、英雄解锁覆盖与三槽（左商 / 中奇遇+基座 / 右免费与保底）Plackett-Luce Hazard 实测权重曲线，对齐 Day 10+ 商店品阶稳态；
  - 全面修复局内权重体系：`ShopHighTierBonusPct` 等比折算与 Day 6+ 平滑转阶、流派倾向权重覆盖商店/开局/事件分支/局外衍生（铲子/钓竿等）/变形祭坛/野怪战利品/升级奖励全链路，以及 Mod 自定义奇遇 `weight` / `minDay` / `maxDay` 调度落地；
  - 客户端全面稳健性治理：彻底消除对象池复用串图、祭坛拒单/切板卡死、Harmony 重复挂钩与战后临时属性残留，拖拽移牌与二合一融合严格 1:1 对齐官方协议。
- **V14 开放式声明式沙盒机制平台 + 零代码 Web 创意工坊 `/workshop` + 10 道防 Bug 铁闸**：
  - 支持声明式沙盒机制（6 大通用管线钩子 + 5 道安全铁闸 + 131 个官方 Unity VFX 别名解析 + 卡面底部实时计数胶囊角标）、四阶升级对比预览、13 种官方附魔编辑器、局内热刷自定义 PNG 卡图、事件 `boardOverride`/`presentation` 切板支持，以及门禁 7 接入 136 条官方词条图标/链接校验；
  - 服务端内置零依赖可视化 Web 工作室（`http://127.0.0.1:8080/workshop`，`BazaarHub` 一键直达；公网默认只读防篡改）：支持检索全库 `3,368` 张白名单原版卡一键「原位改数值（`patches/*.json`）」或「克隆做新卡」、中文效果积木编排、拖拽上传 PNG 自动裁剪预览、一键跑 **10 道防 Bug 铁闸 + 50 场真实 `CombatSimulator` 战斗试跑**（带病 HTTP 422 硬阻断）、热重载上线与 `.bazaarmod.zip` 导入导出；
  - 随仓附带标准化文档 [docs/MOD_AUTHORING_STANDARD.md](docs/MOD_AUTHORING_STANDARD.md)、JSON Schema [docs/schemas/bazaar-mod.schema.json](docs/schemas/bazaar-mod.schema.json)、流水线 CLI [tools/mod_sdk_cli.py](tools/mod_sdk_cli.py) 与专属 AI Skill [docs/skills/bazaar-mod-creator/SKILL.md](docs/skills/bazaar-mod-creator/SKILL.md)（`.agents/skills/bazaar-mod-creator/`）；
  - 公共契约库 `src/BazaarModSdk`（`BazaarModSdk.dll`）+ 黄金示例扩展包 `mods/bazaar-chronicles-sample/`（10/10 铁闸全绿）+ 离线兼容体检器 `tools/compat_doctor.py`（97/100 绿灯）。
- **100% 简体中文热更四层词典**：内置 `tools/data/custom_zh_overlay.json`（`1,474` 条覆盖项），服务端下发 `/api/localization/overlay`、客户端内存层实时拦截补全 2026-11-06 新卡、送货包裹与官方 UI 未汉化文案。
- **5 大新玩法模式 & 持久化多人社交生态**：
  - 支持 **无尽爬塔（Endless Tower）**、**跨英雄大乱斗（Cross-Hero Mayhem）**、**16 词条肉鸽突变 / 每日固定种子挑战**、**10 波 Boss 车轮战（Boss Rush）** 与 **构筑沙盒实验室（Sandbox Lab）**；
  - 新增公开/密码房大厅浏览、大厅与房间实时聊天、观战帧录制与历史战绩回放、无尽/每日持久化排行榜。
- **BazaarHub 独立图形启动器 & 10 项客户端性能优化**：
  - `src/BazaarHub` 深色鎏金 WinForms 启动器：自动寻路游戏、服务器延迟测速、插件/词典/Mod 增量哈希更新、4 项可逆底层性能调优与一键还原官方原版；
  - 客户端 `PerfOptimizerModule` 10 项运行时优化 + 4 组原生菜单深度锚点嵌入（主大厅 7 模式直达条、左侧导航抽屉、局内 ESC 菜单、设置页入口）+ `F8` 鎏金工坊控制中心。

---

## 快速上手（无需编译，下载 Release 整包即玩）

前往本仓库 **Releases** 下载完整发布包：

1. **推荐：使用 `BazaarHub.exe` 图形启动器**（依赖 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)）：自动定位游戏、增量同步双插件（`BazaarModSdk.dll` + `BazaarLauncher.dll`）与汉化/Mod 资产，一键启动单机或远程服。
2. **命令行单机离线直接玩**：解压后直接双击 `启动本地单机版.bat`，自动安装补丁并在本机 `127.0.0.1:8080` 拉起服务端后启动游戏。
3. **搭建自己的云端 / 局域网多人服**：参照 [docs/DEPLOY.md](docs/DEPLOY.md) 使用发布包内的 `server-linux/`（附一键脚本 `deploy-linux.sh`）或 `server/` 配合 `data/` 与 `mods/` 部署在自己的服务器上；客户端运行 `install.bat` 选 `[2]` 填入你的服务器地址即可联机。

---

## 从源码编译运行

### 前置准备
1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 从本地 Steam 游戏目录 `<GameDir>/TheBazaar_Data/Managed/` 复制托管 DLL 至 `ext/Managed/`，将 `BepInEx 5.x` 核心库放入 `ext/bepinex/extracted/BepInEx/core/`，并准备好 `dist/data/`。

### 编译命令
```powershell
# 编译服务端（Windows / Linux 跨平台发布同理）
dotnet build src/BazaarServer/BazaarServer.csproj -c Release

# 编译客户端 Mod SDK 与 BepInEx 插件（需将两个 DLL 同时部署到 BepInEx/plugins/）
dotnet build src/BazaarModSdk/BazaarModSdk.csproj -c Release
dotnet build src/BazaarLauncher/BazaarLauncher.csproj -c Release

# 编译 BazaarHub 图形启动器（Windows）
dotnet publish src/BazaarHub/BazaarHub.csproj -c Release -r win-x64 --self-contained false

# 运行全量自动化测试（636+ 用例）
$env:BAZAAR_DATA_DIR = "./dist/data"
dotnet test Bazaar.sln -c Release
```

---

## 仓库目录结构

| 目录 | 说明 |
|---|---|
| `src/BazaarCore` | 游戏核心引擎、SpawnQuery、光环与战斗模拟器、Mod 加载注册表与自定义规则契约 |
| `src/BazaarServer` | 自建服 HTTP/SSE/MessagePack 服务、RunEngine 编排、5 大新模式、Mod 热加载与持久化社交/排行/锦标赛 |
| `src/BazaarModSdk` | 客户端第三方 Mod 扩展公共契约 SDK（`BazaarModSdk.dll`） |
| `src/BazaarLauncher` | 客户端 BepInEx 注入插件（去 Steam、名字登录、原生菜单嵌入、四层汉化、10 项性能优化与工坊 UI） |
| `src/BazaarHub` | Windows 独立图形化启动器与增量热更新器 |
| `src/BazaarCore.Tests` | 全量核心机制、Mod 生态、新模式与端到端回归测试套件 |
| `src/BazaarProbe` / `src/EngineSmoke` | 协议高压压测器与引擎冒烟工具 |
| `mods/bazaar-chronicles-sample` | 官方端到端示例扩展包（自定义卡牌/技能/奇遇/词条/PNG 贴图） |
| `tools/` | 离线兼容体检器 `compat_doctor.py`、汉化热更词典 `tools/data/`、单机与部署脚本 |
| `dist/client/` | 防撞号安装器（双 DLL 部署）、单机引导脚本与客户端默认配置模板 |
| `docs/` | 部署运维指南、Mod 创作标准手册、JSON Schema 与 AI 创作 Skill |

---

## 声明

本项目仅用于技术研究与个人学习娱乐，请支持《The Bazaar》官方正版；开源分发中已剔除所有原版受版权保护的美术/音频/商业资产文件，请在合规前提下搭配你合法拥有的客户端使用。
