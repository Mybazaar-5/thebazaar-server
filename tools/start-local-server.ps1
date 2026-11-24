param(
    [int]$Port = 8080,
    [string]$BindHost = "127.0.0.1",
    [string]$DataRoot = "",
    [string]$StateDir = ""
)

$ErrorActionPreference = "Stop"

# D7-P1-08：基于脚本相对路径解析工作区根目录，彻底移除个人盘符硬编码
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $repoRoot "dist\data"
}
if ([string]::IsNullOrWhiteSpace($StateDir)) {
    $StateDir = Join-Path $repoRoot "logs\local-state"
}

$serverProj = Join-Path $repoRoot "src\BazaarServer\BazaarServer.csproj"
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null

$env:BAZAAR_BIND = $BindHost
$env:BAZAAR_PORT = "$Port"
$env:BAZAAR_DATA = $DataRoot
$env:BAZAAR_STATE = $StateDir
$env:BAZAAR_GHOSTS = Join-Path $DataRoot "ghost_pool_balanced.jsonl"

Write-Host "启动本机自有服务器 BazaarServer -> http://${BindHost}:$Port" -ForegroundColor Cyan
Write-Host "  工作目录 : $repoRoot"
Write-Host "  数据目录 : $DataRoot"
Write-Host "  存档目录 : $StateDir"
Write-Host "  镜像池   : $env:BAZAAR_GHOSTS"

dotnet run --project $serverProj -c Release --no-build
