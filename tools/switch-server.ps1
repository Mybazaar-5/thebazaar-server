param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("local", "remote", "official")]
    [string]$Target,
    [string]$GameDir = "",
    [string]$CustomUrl = ""
)

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    foreach ($d in @("C:\Program Files (x86)\Steam\steamapps\common\The Bazaar","D:\SteamLibrary\steamapps\common\The Bazaar","E:\SteamLibrary\steamapps\common\The Bazaar")) {
        if (Test-Path (Join-Path $d "TheBazaar.exe")) { $GameDir = $d; break }
    }
}
if ([string]::IsNullOrWhiteSpace($GameDir)) { throw "请通过 -GameDir 指定《The Bazaar》游戏安装目录" }
$cfgPath = Join-Path $GameDir "BepInEx\config\local.thebazaar.bazaarlauncher.cfg"
if (-not (Test-Path $cfgPath)) {
    throw "未找到配置文件: $cfgPath"
}

$content = Get-Content $cfgPath -Raw -Encoding UTF8

switch ($Target) {
    "local" {
        $url = if ($CustomUrl) { $CustomUrl } else { "http://127.0.0.1:8080" }
        $content = [regex]::Replace($content, '(?m)^Enabled\s*=.*$', 'Enabled = true')
        $content = [regex]::Replace($content, '(?m)^ServerBase\s*=.*$', "ServerBase = $url")
        Write-Host "已切换至本机自有服: $url" -ForegroundColor Green
    }
    "remote" {
        $url = if ($CustomUrl) { $CustomUrl } else { throw "请通过 -CustomUrl 指定你部署的远程服务器地址，例如: -CustomUrl http://1.2.3.4:8080" }
        $content = [regex]::Replace($content, '(?m)^Enabled\s*=.*$', 'Enabled = true')
        $content = [regex]::Replace($content, '(?m)^ServerBase\s*=.*$', "ServerBase = $url")
        Write-Host "已切换至云端自有服: $url" -ForegroundColor Green
    }
    "official" {
        $content = [regex]::Replace($content, '(?m)^Enabled\s*=.*$', 'Enabled = false')
        Write-Host "已关闭自建服补丁，恢复原版行为" -ForegroundColor Yellow
    }
}

Set-Content -Path $cfgPath -Value $content -Encoding UTF8 -NoNewline
Write-Host "配置文件已更新: $cfgPath"
