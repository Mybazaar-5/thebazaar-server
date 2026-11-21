# 《The Bazaar》自建服务端与客户端增强插件（V13 · BazaarModKit 生态版）

> [!IMPORTANT]
> **客户端版本要求（必看）**：本项目**必须使用 Steam 官方测试服（PTR 分支）客户端才可以正常使用，暂不支持官方正式服最新版！**
> 请在 Steam 库右键《The Bazaar》→ **属性** → **测试版（Betas）**，将参与的测试切换至官方测试服（PTR，如 `1.0.13122-ptr`）更新完成后，再安装本补丁与运行服务端。

一个面向《The Bazaar》（Steam 官方 PTR 客户端兼容）的开源独立自建服务器、客户端插件生态（`BazaarModKit`）与图形化启动器（`BazaarHub`），支持**单机离线开箱即玩**、**自建多人联机服务器**、**Mod 热加载与自定义卡牌/贴图**、**5 大创意新玩法模式**与**8/10 人自定义锦标赛**，并严格按官方协议与 `GameData.db` 机制还原战斗、商店、事件与升级流程。

> 本开源仓库不含作者个人服务器地址、SSH 配置及原版受版权保护的二进制资源；客户端原版资产由你本地已安装的 Steam 游戏本体提供。

---

## 核心特性（V13 全新升级）

- **100% 官方协议兼容 & 引擎全量对齐**：完整实现登录/Bootstrap/Store/Chests/Loadouts/Sessions/Commands（MessagePack）与多人锦标赛 SSE 推送，不修改《The Bazaar》原版二进制文件；单次共享 `GameData.db` 加载、3-Pass 光环收敛、ICD 队列与 12 项核心不变量零违例。
- **BazaarModKit 模组生态 & 零白块自定义内容**：
  - 全新公共契约库 `src/BazaarModSdk`（`BazaarModSdk.dll`）与双端热加载主机 `ServerModHost` / `ModContentSyncClient`；
  - 随仓附带完整示例扩展包 `mods/bazaar-chronicles-sample/`（6 张自定义卡牌/技能、2 个多步奇遇事件、6 张 512×512 PNG 贴图与 2 个自定义词条），通过 `baseVisualTemplateId` + 实例级视觉覆盖实现 0 白块、0 反序列化崩溃与按 SHA-256 增量同步；
  - 离线兼容体检器 `tools/compat_doctor.py`（Steam 更新后秒级核验 Harmony 钩子、资产白名单、DB 与汉化增量）。
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

# 运行全量自动化测试（406+ 用例）
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
| `docs/` | 部署运维指南、战斗/架构文档与全链路深度审计报告 |

---

## 声明

本项目仅用于技术研究与个人学习娱乐，请支持《The Bazaar》官方正版；开源分发中已剔除所有原版受版权保护的美术/音频/商业资产文件，请在合规前提下搭配你合法拥有的客户端使用。
