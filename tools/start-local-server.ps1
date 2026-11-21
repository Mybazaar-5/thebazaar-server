param(
    [int]$Port = 8080,
    [string]$DataRoot = "e:\thebazaar\dist\data",
    [string]$StateDir = "e:\thebazaar\logs\local-state"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null

$env:BAZAAR_PORT = "$Port"
$env:BAZAAR_DATA = $DataRoot
$env:BAZAAR_STATE = $StateDir
$env:BAZAAR_GHOSTS = Join-Path $DataRoot "ghost_pool_balanced.jsonl"

Write-Host "启动本机自有服务器 BazaarServer -> http://127.0.0.1:$Port" -ForegroundColor Cyan
Write-Host "  数据目录 : $DataRoot"
Write-Host "  存档目录 : $StateDir"
Write-Host "  镜像池   : $env:BAZAAR_GHOSTS"

dotnet run --project "e:\thebazaar\src\BazaarServer\BazaarServer.csproj" -c Release --no-build
