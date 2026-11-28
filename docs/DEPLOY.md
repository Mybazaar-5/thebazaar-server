# 自建服务器部署与联机指南

本项目支持 **本地单机离线运行** 与 **自建 Linux / Windows 联机服务器** 两种部署方式。发布包默认以本地单机（`http://127.0.0.1:8080`）开箱即玩；如需与朋友跨网段或云端联机，可按本指南将服务端部署至你自己的服务器。

> [!IMPORTANT]
> **自建服务端前置要求（必看）**：开源发布包严格不包含受版权保护的原版商业 DLL（`BazaarGameShared.dll`、`BazaarBattleService.dll`、`BazaarGameClient.dll`、`BazaarUnityTools.dll`）、`GameData.db` 与 `translations/*.bytes`。
> - **Windows 本地单机玩家**：直接双击 `BazaarHub\BazaarHub.exe` 或 `启动本地单机版.bat`，程序会自动从你本机的 Steam 官方测试服（PTR）客户端目录提取补齐上述文件（并顺便补齐同级 `server-linux\`），无需手动操作。
> - **Linux 云服务器 / 独立服务器部署**：上传前请先在安装了 Steam PTR 客户端的 Windows 电脑上运行一次 `BazaarHub.exe`（或按包内 `LOCAL_ASSET_EXTRACTION.md` 一步脚本提取），确认 `server-linux/`（或 `server/`）已含上述 4 个 DLL 且 `data/` 已含 `GameData.db` 与 `translations/*.bytes` 后再上传部署。

---

## 一、快速选择：你想怎么玩？

| 模式 | 适用场景 | 服务端地址 | 操作方式 |
|---|---|---|---|
| **1. 本地单机离线版** | 个人游玩、断网游玩 | `http://127.0.0.1:8080` | 下载 Release 整包解压 -> 双击 `install.bat` 选 `1` -> 以后直接双击 `启动本地单机版.bat` |
| **2. 自建联机服务器** | 朋友开黑 / 8 人或 10 人锦标赛 | `http://<你的服务器IP>:8080` 或域名反代路径 | 先在自己的 Linux/Windows 服务器启动服务端 -> 大家各自跑 `install.bat` 选 `2` 并填入你的服务器地址 |

---

## 二、在 Linux 云服务器一键部署（推荐 Ubuntu 22.04 / 24.04）

### 环境要求
- 安装 [.NET 8 ASP.NET Core Runtime](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu)：
  ```bash
  sudo apt-get update && sudo apt-get install -y aspnetcore-runtime-8.0
  ```
- 防火墙/云安全组放行端口：TCP `8080`（如用 Nginx 域名反代，放行 `80`/`443` 或你自定义的代理端口即可）。

### 一键部署步骤
1. 从 GitHub Release 下载 `TheBazaar-SelfHosted-Full-<Tag>.zip` 并解压，将里面的 **`server-linux/`** 和 **`data/`** 两个文件夹上传到服务器同级目录（例如 `~/bazaar-pkg/server-linux` 和 `~/bazaar-pkg/data`）：
   ```bash
   cd ~/bazaar-pkg/server-linux
   bash deploy-linux.sh
   ```
   `deploy-linux.sh` 会自动安装到 `/opt/bazaar/`，创建 `systemd` 守护服务 `bazaar.service`（开机自启 + 崩溃自动拉起 + `LimitNOFILE=65535`）并启动服务。

2. 验证服务已启动：
   ```bash
   curl http://127.0.0.1:8080/healthz
   ```
   返回 `{"ok":true,...}` 即代表服务端就绪。

3. **客户端接入**：让你和朋友在各自电脑上运行 `install.bat` -> 选择 `[2] 连接自建远程服` -> 输入 `http://<你的服务器公网IP>:8080`（或你的 Nginx 域名地址），随后直接运行原版 `TheBazaar.exe` 即可！

### 可选：使用 Nginx 路径反代（如挂在二级路径 `/bz/`）
如果服务器通过 Nginx 统一暴露端口，需要特别关闭缓冲以保证 **SSE 事件流实时推送与 MessagePack 二进制传输**：
```nginx
location /bz/ {
    proxy_pass http://127.0.0.1:8080/;
    proxy_http_version 1.1;
    proxy_buffering off;
    proxy_cache off;
    proxy_request_buffering off;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
    client_max_body_size 64m;
}
```
配置后客户端在 `install.bat` 填写 `https://<你的域名>/bz`（或 `http://<IP>:<端口>/bz`）即可。

---

## 三、在 Windows 独立主机部署服务端

如果你手头有 Windows 电脑或 Windows 云主机做局域网/公网联机服：
1. 将发布包中的 `server\` 与 `data\` 放在同一父目录；
2. 在 PowerShell 设置环境变量并运行：
   ```powershell
   $env:BAZAAR_PORT = "8080"
   $env:BAZAAR_DATA = (Resolve-Path ".\data").Path
   $env:BAZAAR_STATE = (Resolve-Path ".\state").Path
   .\server\BazaarServer.exe
   ```
3. Windows 防火墙放行入站 TCP `8080` 端口；客户端安装器填 `http://<主机局域网或公网IP>:8080` 即可连接。

---

## 四、常用环境变量与运行数据说明

| 环境变量 | 默认值 | 说明 |
|---|---|---|
| `BAZAAR_PORT` | `8080` | HTTP + WebSocket/SSE 监听端口 |
| `BAZAAR_DATA` | `./data` | 运行期数据目录（含 `GameData.db`、`ghost_pool_balanced.jsonl`、`translations/` 与各类 `.json` 白名单） |
| `BAZAAR_STATE` | `./state` | SQLite 持久化数据库存放目录（自动生成 `bazaar.db`，保存账号、断线重连存档、个人玩法规则与 PvP 幽灵镜像） |

---

## 五、常用运维命令（Linux）

```bash
# 查看运行状态与实时日志
sudo systemctl status bazaar
tail -f /opt/bazaar/logs/server.log

# 备份存档数据库
cp /opt/bazaar/state/bazaar.db /opt/bazaar/bazaar.db.bak

# 重启服务
sudo systemctl restart bazaar
```