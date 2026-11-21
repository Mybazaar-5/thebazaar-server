# 《The Bazaar》自建服务端与客户端增强插件（V12.1）

> [!IMPORTANT]
> **客户端版本要求（必看）**：本项目**必须使用 Steam 官方测试服（PTR 分支）客户端才可以正常使用，暂不支持官方正式服最新版！**
> 请在 Steam 库右键《The Bazaar》→ **属性** → **测试版（Betas）**，将参与的测试切换至官方测试服（PTR，如 `1.0.13122-ptr`）更新完成后，再安装本补丁与运行服务端。

一个面向《The Bazaar》（Steam 官方 PTR 客户端兼容）的开源独立自建服务器与客户端 BepInEx 插件实现，支持**单机离线开箱即玩**、**自建多人联机服务器**与**8/10 人自定义锦标赛**，并严格按官方协议与 `GameData.db` 机制还原战斗、商店、事件与升级流程。

> 本开源仓库不含作者个人服务器地址、SSH 配置及原版受版权保护的二进制资源；客户端原版资产由你本地已安装的 Steam 游戏本体提供。

---

## 核心特性

- **100% 官方协议兼容**：完整实现登录/Bootstrap/Store/Chests/Loadouts/Sessions/Commands（MessagePack）与多人锦标赛 SSE 推送，不修改《The Bazaar》原版二进制文件。
- **全 8 英雄 & 机制高还原**：完整支持瓦内莎、皮格马利翁、杜利、马克、朱尔斯、斯黛拉、卡诺克及第 8 英雄**双龙（The Dragons）**；涵盖 3-Pass 光环收敛、ICD 队列、多触发顺序、官方分天日程（Hour 1–6）、动态 `ExclusionSet`、盗贼行会/远征第 4 节点及多项官服已知 Bug 修复。
- **游戏内原生 uGUI 中文自定义规则控制台（`F8`）**：
  - **18 大流派开局倾向**（`0-3` 项，开局 `Day 1 Hour 1` 确认锁定 1 次，途中禁改，合法卡池内加权绝不虚空造卡）；
  - **野怪战利品全池任选 1 件**（默认开启且共享标准 PvP 幽灵镜像池）；
  - **多维度微调**：初始属性、固定开局物品/技能（中文检索+品质+附魔）、10 大事件权重倍率与禁用列表；
  - **多人锦标赛大厅**：`8人标准 / 10人缓冲`、机器人自动补位、4 轮内必遇真人、决战吃鸡结算与房主预设记忆。
- **性能与中文显示专项优化（V12.1）**：
  - Unity 6 TextMeshPro 直读系统字体创建动态多图集字库，彻底消除菜单与面板 `□` 方块字；
  - **按需懒加载 396 套全解锁藏品**（告别启动全量驻留显存）、前台 `60 / 90 / 120 / 原版` 稳帧与后台失焦自动降频至 `20 FPS`、战斗 **`1x / 2x / 3x / 5x`** 控速缩放物理步长与隐身画布零消耗休眠。

---

## 快速上手（无需编译，下载 Release 整包即玩）

前往本仓库 **Releases** 下载 `TheBazaar-SelfHosted-Full-v12.1.zip`：

1. **单机离线直接玩**：解压后直接双击 `启动本地单机版.bat`（依赖 [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)），自动安装补丁并在本机 `127.0.0.1:8080` 拉起服务端后启动游戏。
2. **搭建自己的云端 / 局域网多人服**：参照 [docs/DEPLOY.md](docs/DEPLOY.md) 使用发布包内的 `server-linux/`（附一键脚本 `deploy-linux.sh`）或 `server/`（Windows `BazaarServer.exe`）配合 `data/` 运行数据部署在你自己的服务器上；客户端运行 `install.bat` 选 `[2]` 填入你的服务器地址即可与朋友联机。

---

## 从源码编译运行

### 前置准备
1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 本仓库 Git 代码树不包含原版客户端托管 DLL 与运行数据：
   - **懒人方式**：直接从 Release 下载 `TheBazaar-Source-And-Recon-v12.1.zip`，其中已整理好目录结构；
   - **手动方式**：从你本地 Steam 游戏目录 `<GameDir>/TheBazaar_Data/Managed/` 复制托管 DLL 至 `ext/Managed/`，将 `BepInEx 5.x` 核心库放入 `ext/bepinex/extracted/BepInEx/core/`，并准备好 `dist/data/`。

### 编译命令
```powershell
# 编译服务端（Windows / Linux 跨平台发布同理）
dotnet build src/BazaarServer/BazaarServer.csproj -c Release

# 编译客户端 BepInEx 插件
dotnet build src/BazaarLauncher/BazaarLauncher.csproj -c Release

# 运行自动化测试（需设置 BAZAAR_DATA_DIR 指向数据目录）
$env:BAZAAR_DATA_DIR = "./dist/data"
dotnet test Bazaar.sln -c Release
```

---

## 仓库目录结构

| 目录 | 说明 |
|---|---|
| `src/BazaarCore` | 游戏核心引擎、SpawnQuery、光环与战斗模拟器、自定义规则契约 |
| `src/BazaarServer` | 自建服 HTTP/SSE/MessagePack 服务、RunEngine 编排、锦标赛匹配与 SQLite 持久化 |
| `src/BazaarLauncher` | 客户端 BepInEx 注入插件（去 Steam、名字登录、原生 uGUI 中文面板与性能优化） |
| `src/BazaarCore.Tests` | 全量核心机制与端到端回归测试套件 |
| `src/BazaarProbe` / `src/EngineSmoke` | 协议压测并发跑批器与引擎冒烟工具 |
| `tools/` | 单机启动器、切服脚本、Linux `systemd` 一键部署脚本等 |
| `dist/client/` | 纯 7-bit ASCII 防撞号安装/单机引导脚本与客户端默认配置模板 |
| `docs/` | 部署运维指南、战斗与架构文档 |

---

## 声明

本项目仅用于技术研究与个人学习娱乐，请支持《The Bazaar》官方正版；开源分发中已剔除所有原版受版权保护的美术/音频/商业资产文件，请在合规前提下搭配你合法拥有的客户端使用。