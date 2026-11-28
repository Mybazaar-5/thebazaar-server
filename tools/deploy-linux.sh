#!/usr/bin/env bash
set -euo pipefail

# One-click Ubuntu/Debian systemd deployment script for The Bazaar self-hosted server (D7-P1-01)
INSTALL_ROOT="${INSTALL_ROOT:-/opt/bazaar}"
PORT="${BAZAAR_PORT:-8080}"
BIND_HOST="${BAZAAR_BIND:-0.0.0.0}"
SERVICE_USER="${BAZAAR_USER:-$USER}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Installing BazaarServer into ${INSTALL_ROOT} (bind=${BIND_HOST}, port=${PORT}, user=${SERVICE_USER}) ==="

if [[ "${BIND_HOST}" != "127.0.0.1" && "${BIND_HOST}" != "localhost" && "${BIND_HOST}" != "::1" ]]; then
    if [[ -z "${BAZAAR_WORKSHOP_TOKEN:-}" ]]; then
        echo "[Security][WARN] 服务将监听非回环地址 (${BIND_HOST}:${PORT}) 且未设置 BAZAAR_WORKSHOP_TOKEN。"
        echo "[Security][WARN] 公网部署请务必在反向代理透传 X-Forwarded-For/X-Real-IP，并配置 BAZAAR_WORKSHOP_MODE=token-only 与高熵 BAZAAR_WORKSHOP_TOKEN。"
    fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[Hint] .NET 8 Runtime is required (or self-contained binary). Checking ASP.NET Core 8 runtime..."
fi

sudo mkdir -p "${INSTALL_ROOT}/server" "${INSTALL_ROOT}/data" "${INSTALL_ROOT}/state" "${INSTALL_ROOT}/logs" "${INSTALL_ROOT}/payload" "${INSTALL_ROOT}/mods"
sudo chown -R "${SERVICE_USER}:${SERVICE_USER}" "${INSTALL_ROOT}"

# 部署前预检：确认 4 个原版共享 DLL 已按 LOCAL_ASSET_EXTRACTION.md 提取到当前目录或既有 server 目录
MISSING_DLLS=()
for dll in BazaarGameShared.dll BazaarBattleService.dll BazaarGameClient.dll BazaarUnityTools.dll; do
    if [ ! -s "${SCRIPT_DIR}/${dll}" ] && [ ! -s "${INSTALL_ROOT}/server/${dll}" ]; then
        MISSING_DLLS+=("${dll}")
    fi
done
if [ ${#MISSING_DLLS[@]} -gt 0 ]; then
    echo "[ERROR] 缺少原版游戏共享库: ${MISSING_DLLS[*]}"
    echo "[ERROR] 开源发布包依版权规范不含上述原版商业 DLL，请先在您的 Windows 电脑上："
    echo "        1) 运行一次 BazaarHub.exe 或 启动本地单机版.bat（会自动补齐到 server-linux/ 与 data/），或按 LOCAL_ASSET_EXTRACTION.md 手动提取；"
    echo "        2) 再将补齐后的 server-linux/ 与 data/ 上传到本机后重新执行 deploy-linux.sh。"
    exit 1
fi

cp -rf "${SCRIPT_DIR}/." "${INSTALL_ROOT}/server/"
if [ -f "${INSTALL_ROOT}/server/BazaarServer" ]; then
    chmod +x "${INSTALL_ROOT}/server/BazaarServer"
fi

# 同步运行态 data 目录
if [ -d "${SCRIPT_DIR}/../data" ] && [ -f "${SCRIPT_DIR}/../data/GameData.db" ]; then
    echo "Copying runtime data directory into ${INSTALL_ROOT}/data ..."
    cp -rf "${SCRIPT_DIR}/../data/." "${INSTALL_ROOT}/data/"
elif [ -d "${SCRIPT_DIR}/../dist/data" ] && [ -f "${SCRIPT_DIR}/../dist/data/GameData.db" ]; then
    echo "Copying repo dist/data directory into ${INSTALL_ROOT}/data ..."
    cp -rf "${SCRIPT_DIR}/../dist/data/." "${INSTALL_ROOT}/data/"
elif [ ! -f "${INSTALL_ROOT}/data/GameData.db" ]; then
    echo "[ERROR] GameData.db not found in ${SCRIPT_DIR}/../data, ${SCRIPT_DIR}/../dist/data or ${INSTALL_ROOT}/data !"
    echo "Please copy dist/data/* (including GameData.db & translations/) into ${INSTALL_ROOT}/data before starting."
    exit 1
fi

# 统一同步客户端热更新载荷目录 payload/（对齐 sync-github-public.ps1 Release 整包与 ClientUpdateEndpoints.cs）
PAYLOAD_SRC=""
if [ -d "${SCRIPT_DIR}/../payload" ]; then
    PAYLOAD_SRC="${SCRIPT_DIR}/../payload"
elif [ -d "${SCRIPT_DIR}/../dist/client/payload" ]; then
    PAYLOAD_SRC="${SCRIPT_DIR}/../dist/client/payload"
fi
if [ -n "${PAYLOAD_SRC}" ]; then
    echo "Copying client update payload from ${PAYLOAD_SRC} into ${INSTALL_ROOT}/payload ..."
    cp -rf "${PAYLOAD_SRC}/." "${INSTALL_ROOT}/payload/"
fi

# 可选同步官方示例/预置 mods/
if [ -d "${SCRIPT_DIR}/../mods" ]; then
    echo "Copying mods from ${SCRIPT_DIR}/../mods into ${INSTALL_ROOT}/mods ..."
    cp -rf "${SCRIPT_DIR}/../mods/." "${INSTALL_ROOT}/mods/"
fi

SERVICE_FILE="/etc/systemd/system/bazaar.service"
echo "Writing ${SERVICE_FILE} ..."
sudo tee "${SERVICE_FILE}" >/dev/null <<EOF
[Unit]
Description=Bazaar Self-Hosted Server
After=network.target

[Service]
Type=simple
User=${SERVICE_USER}
WorkingDirectory=${INSTALL_ROOT}/server
Environment=BAZAAR_BIND=${BIND_HOST}
Environment=BAZAAR_PORT=${PORT}
Environment=BAZAAR_DATA=${INSTALL_ROOT}/data
Environment=BAZAAR_STATE=${INSTALL_ROOT}/state
Environment=BAZAAR_CLIENT_PAYLOAD=${INSTALL_ROOT}/payload
Environment=BAZAAR_WORKSHOP_MODE=${BAZAAR_WORKSHOP_MODE:-auto}
Environment=DOTNET_NOLOGO=1
ExecStart=${INSTALL_ROOT}/server/BazaarServer
Restart=always
RestartSec=3
StandardOutput=append:${INSTALL_ROOT}/logs/server.log
StandardError=append:${INSTALL_ROOT}/logs/server.err.log
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable bazaar
sudo systemctl restart bazaar
sleep 3
sudo systemctl status bazaar --no-pager -n 12 || true

echo ""
echo "Deployment complete!"
echo "Local check : curl http://127.0.0.1:${PORT}/healthz"
echo "Payload dir : ${INSTALL_ROOT}/payload"
echo "Client URL  : http://<YOUR_SERVER_IP>:${PORT} (open TCP ${PORT} in firewall or reverse-proxy via Nginx)"
