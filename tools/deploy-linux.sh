#!/usr/bin/env bash
set -euo pipefail

# One-click Ubuntu/Debian systemd deployment script for The Bazaar self-hosted server
INSTALL_ROOT="${INSTALL_ROOT:-/opt/bazaar}"
PORT="${BAZAAR_PORT:-8080}"
SERVICE_USER="${BAZAAR_USER:-$USER}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Installing BazaarServer into ${INSTALL_ROOT} (port=${PORT}, user=${SERVICE_USER}) ==="

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[Hint] .NET 8 Runtime is required (or self-contained binary). Checking ASP.NET Core 8 runtime..."
fi

sudo mkdir -p "${INSTALL_ROOT}/server" "${INSTALL_ROOT}/data" "${INSTALL_ROOT}/state" "${INSTALL_ROOT}/logs"
sudo chown -R "${SERVICE_USER}:${SERVICE_USER}" "${INSTALL_ROOT}"

cp -rf "${SCRIPT_DIR}/." "${INSTALL_ROOT}/server/"
chmod +x "${INSTALL_ROOT}/server/BazaarServer"

if [ -d "${SCRIPT_DIR}/../data" ] && [ -f "${SCRIPT_DIR}/../data/GameData.db" ]; then
    echo "Copying runtime data directory into ${INSTALL_ROOT}/data ..."
    cp -rf "${SCRIPT_DIR}/../data/." "${INSTALL_ROOT}/data/"
elif [ ! -f "${INSTALL_ROOT}/data/GameData.db" ]; then
    echo "[ERROR] GameData.db not found in ${SCRIPT_DIR}/../data or ${INSTALL_ROOT}/data !"
    echo "Please copy dist/data/* (including GameData.db & translations/) into ${INSTALL_ROOT}/data before starting."
    exit 1
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
Environment=BAZAAR_PORT=${PORT}
Environment=BAZAAR_DATA=${INSTALL_ROOT}/data
Environment=BAZAAR_STATE=${INSTALL_ROOT}/state
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
echo "Client URL  : http://<YOUR_SERVER_IP>:${PORT} (open TCP ${PORT} in firewall or reverse-proxy via Nginx)"