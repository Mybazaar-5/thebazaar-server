# 《The Bazaar》自建服客户端使用指南

> [!IMPORTANT]
> **客户端版本要求（必看）**：本项目**必须使用官方测试服（PTR 分支）客户端，暂不支持官方正式服最新版！**
> 请先在 Steam 库中右键《The Bazaar》→ **属性** → **测试版（Betas）**，切换为官方测试服分支（PTR，例如 `1.0.13122-ptr`）完成游戏更新后再进行安装游玩。

## 一、本地单机离线游玩（开箱即玩）

1. 解压发布包 `TheBazaar-SelfHosted-Full-v12.1.zip` 到任意文件夹（建议路径不含特殊符号）。
2. 确保本机已安装 **.NET 8 Desktop / ASP.NET Core Runtime**（Linux 或 Windows）。
3. 双击 **`启动本地单机版.bat`**：
   - 自动全盘扫描或引导选择《The Bazaar》Steam 游戏目录；
   - 自动部署 `BepInEx + BazaarLauncher.dll` 并生成防撞号专属玩家名；
   - 自动在后台拉起本机 `server\BazaarServer.exe`（`http://127.0.0.1:8080`），存档保存在同级 `state\bazaar.db`；
   - 游戏退出后自动关闭服务端并保存全部进度。

## 二、连接你自己部署的远程服务器（多人联机 / 锦标赛）

1. 按 [DEPLOY.md](./DEPLOY.md) 将包内 `server-linux/`（或 `server/`）与 `data/` 部署到你自己的云服务器或局域网主机。
2. 你和同服好友各自双击运行 **`install.bat`**：
   - 选 **`[2] 连接自建远程服`**，填入你的服务器地址（如 `http://<你的服务器IP>:8080` 或反代路径）；
   - 输入各自的专属昵称（名字即账号与独立存档，请勿重名）。
3. 以后直接双击游戏目录的 **`TheBazaar.exe`** 即可连接你的服务器联机与打锦标赛（无需打开 Steam）。

## 三、恢复原版官方服

重新运行 `install.bat` 选择 **`[3] 恢复原版官方服`**（或删除游戏目录下的 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\`、`player-name.txt`）即可 100% 还原原版游戏文件。