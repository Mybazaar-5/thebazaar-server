param(
    [int]$Port = 8080,
    [string]$GameDir = "",
    [string]$DataRoot = "",
    [string]$StateDir = "",
    [string]$PlayerName = "",
    [switch]$KeepServerRunning,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Resolve-RepoRoot {
    param([string]$StartDir)
    $candidates = @()
    $cur = Resolve-Path $StartDir
    for ($i = 0; $i -lt 5 -and $cur; $i++) {
        $candidates += $cur.Path
        $parent = Split-Path $cur.Path -Parent
        if (-not $parent -or $parent -eq $cur.Path) { break }
        $cur = Resolve-Path $parent
    }
    foreach ($c in $candidates) {
        if ((Test-Path (Join-Path $c "src\BazaarServer\BazaarServer.csproj")) -and
            (Test-Path (Join-Path $c "dist\data\GameData.db"))) {
            return $c
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "src\BazaarServer\BazaarServer.csproj")) {
            return $c
        }
    }
    return (Split-Path $PSScriptRoot -Parent)
}

function Resolve-DataRoot {
    param([string]$Explicit, [string]$RepoRoot)
    if ($Explicit -and (Test-Path (Join-Path $Explicit "GameData.db"))) {
        return (Resolve-Path $Explicit).Path
    }
    $localData = Join-Path $RepoRoot "dist\data"
    if (Test-Path (Join-Path $localData "GameData.db")) {
        return $localData
    }
    # 若在 .worktrees/<name> 下运行，尝试回溯主仓库的 dist\data
    $parent1 = Split-Path $RepoRoot -Parent
    $parent2 = if ($parent1) { Split-Path $parent1 -Parent } else { $null }
    if ($parent2) {
        $mainData = Join-Path $parent2 "dist\data"
        if (Test-Path (Join-Path $mainData "GameData.db")) {
            return $mainData
        }
    }
    $siblingData = Join-Path $PSScriptRoot "..\data"
    if (Test-Path (Join-Path $siblingData "GameData.db")) {
        return (Resolve-Path $siblingData).Path
    }
    throw "Cannot locate GameData.db. Please pass -DataRoot explicitly."
}

function Resolve-GameDir {
    param([string]$Explicit)
    if ($Explicit) {
        if (Test-Path (Join-Path $Explicit "TheBazaar.exe")) {
            return (Resolve-Path $Explicit).Path
        }
        throw "TheBazaar.exe not found in specified GameDir: $Explicit"
    }

    $candidates = @(
        "<THE_BAZAAR_GAME_DIR>",
        "C:\Program Files (x86)\Steam\steamapps\common\The Bazaar",
        "D:\Steam\steamapps\common\The Bazaar",
        "E:\Steam\steamapps\common\The Bazaar",
        "<THE_BAZAAR_GAME_DIR>",
        "<THE_BAZAAR_GAME_DIR>"
    )
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir "TheBazaar.exe")) {
            return $dir
        }
    }
    throw "Cannot locate TheBazaar.exe automatically. Please pass -GameDir."
}

function Ensure-ClientPluginInstalled {
    param([string]$TargetGameDir, [string]$RepoRoot)
    $pluginPath = Join-Path $TargetGameDir "BepInEx\plugins\BazaarLauncher.dll"
    $sdkPath = Join-Path $TargetGameDir "BepInEx\plugins\BazaarModSdk.dll"
    $winhttpPath = Join-Path $TargetGameDir "winhttp.dll"
    $doorstopPath = Join-Path $TargetGameDir "doorstop_config.ini"
    $payloadDir = Join-Path $RepoRoot "dist\client\payload"

    if (-not (Test-Path $pluginPath) -or -not (Test-Path $sdkPath) -or -not (Test-Path $winhttpPath) -or -not (Test-Path $doorstopPath)) {
        if (-not (Test-Path $payloadDir)) {
            throw "BazaarLauncher/BazaarModSdk payload is missing in '$TargetGameDir' and payload not found at '$payloadDir'."
        }
        Write-Host "[Standalone] Installing BepInEx, BazaarLauncher & BazaarModSdk payload into game directory..." -ForegroundColor Yellow
        $winhttpSrc = Join-Path $payloadDir "winhttp.dll"
        if (Test-Path $winhttpSrc) { Copy-Item -Path $winhttpSrc -Destination $TargetGameDir -Force }

        $doorstopSrc = Join-Path $payloadDir "doorstop_config.ini"
        if ((Test-Path $doorstopSrc) -and -not (Test-Path $doorstopPath)) {
            Copy-Item -Path $doorstopSrc -Destination $TargetGameDir -Force
        }

        $dsVer = Join-Path $payloadDir ".doorstop_version"
        if (Test-Path $dsVer) {
            Copy-Item -Path $dsVer -Destination $TargetGameDir -Force
        }

        # D7-P1-06：逐文件部署 BepInEx 目录，严格跳过目标已存在的 BepInEx\config\*.cfg 防覆写玩家配置
        $bepInExSrc = Join-Path $payloadDir "BepInEx"
        $bepInExDst = Join-Path $TargetGameDir "BepInEx"
        if (Test-Path $bepInExSrc) {
            Get-ChildItem -Path $bepInExSrc -Recurse -File | ForEach-Object {
                $rel = [System.IO.Path]::GetRelativePath($bepInExSrc, $_.FullName)
                $dstFile = Join-Path $bepInExDst $rel
                $isExistingUserCfg = ($rel -match '(?i)^config[\\/].+\.cfg$') -and (Test-Path -LiteralPath $dstFile)
                if ($isExistingUserCfg) {
                    return
                }
                $dstParent = Split-Path $dstFile -Parent
                if (-not (Test-Path $dstParent)) {
                    New-Item -ItemType Directory -Force -Path $dstParent | Out-Null
                }
                Copy-Item -LiteralPath $_.FullName -Destination $dstFile -Force
            }
        }

        $pluginsDir = Join-Path $bepInExDst "plugins"
        New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null

        $launcherSrc = Join-Path $payloadDir "BazaarLauncher.dll"
        if (Test-Path $launcherSrc) {
            Copy-Item -LiteralPath $launcherSrc -Destination (Join-Path $pluginsDir "BazaarLauncher.dll") -Force
        }

        $sdkSrc = Join-Path $payloadDir "BazaarModSdk.dll"
        if (-not (Test-Path $sdkSrc)) {
            $sdkSrc = Join-Path $payloadDir "BepInEx\plugins\BazaarModSdk.dll"
        }
        if (Test-Path $sdkSrc) {
            Copy-Item -LiteralPath $sdkSrc -Destination (Join-Path $pluginsDir "BazaarModSdk.dll") -Force
        }
    }
}

function Update-LauncherConfigToLocal {
    param([string]$TargetGameDir, [string]$LocalUrl, [string]$RepoRoot, [string]$CustomPlayerName)
    $cfgDir = Join-Path $TargetGameDir "BepInEx\config"
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $cfgPath = Join-Path $cfgDir "local.thebazaar.bazaarlauncher.cfg"

    if (-not (Test-Path $cfgPath)) {
        $templateCfg = Join-Path $RepoRoot "dist\client\payload\BepInEx\config\local.thebazaar.bazaarlauncher.cfg"
        if (Test-Path $templateCfg) {
            Copy-Item -Path $templateCfg -Destination $cfgPath -Force
        }
        else {
            $defaultCfg = @"
[General]
Enabled = true
ServerBase = $LocalUrl
PlayerName =
BypassSteam = true
UnlockEverything = true
"@
            [System.IO.File]::WriteAllText($cfgPath, $defaultCfg, [System.Text.UTF8Encoding]::new($false))
        }
    }

    $content = [System.IO.File]::ReadAllText($cfgPath, [System.Text.Encoding]::UTF8)
    if ($content -match '(?m)^Enabled\s*=') {
        $content = [regex]::Replace($content, '(?m)^Enabled\s*=.*$', 'Enabled = true')
    } else {
        $content += "`nEnabled = true"
    }
    if ($content -match '(?m)^ServerBase\s*=') {
        $content = [regex]::Replace($content, '(?m)^ServerBase\s*=.*$', "ServerBase = $LocalUrl")
    } else {
        $content += "`nServerBase = $LocalUrl"
    }
    if ($CustomPlayerName) {
        if ($content -match '(?m)^PlayerName\s*=') {
            $content = [regex]::Replace($content, '(?m)^PlayerName\s*=.*$', "PlayerName = $CustomPlayerName")
        } else {
            $content += "`nPlayerName = $CustomPlayerName"
        }
    }
    [System.IO.File]::WriteAllText($cfgPath, $content, [System.Text.UTF8Encoding]::new($false))
    Write-Host "[Standalone] Updated config -> ServerBase = $LocalUrl" -ForegroundColor Green
}

function Test-ServerHealth {
    param([string]$HealthUrl)
    try {
        $resp = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 2
        return ($null -ne $resp -and $resp.ok -eq $true)
    }
    catch {
        return $false
    }
}

$repoRoot = Resolve-RepoRoot -StartDir $PSScriptRoot
$resolvedData = Resolve-DataRoot -Explicit $DataRoot -RepoRoot $repoRoot
$resolvedGame = Resolve-GameDir -Explicit $GameDir
if (-not $StateDir) {
    $StateDir = Join-Path $repoRoot "logs\standalone-state"
}
$logsDir = Join-Path $repoRoot "logs"
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

$localUrl = "http://127.0.0.1:$Port"
$healthUrl = "$localUrl/healthz"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host " The Bazaar - Standalone Offline Mode" -ForegroundColor Cyan
Write-Host " RepoRoot : $repoRoot"
Write-Host " GameDir  : $resolvedGame"
Write-Host " DataRoot : $resolvedData"
Write-Host " StateDir : $StateDir"
Write-Host " Server   : $localUrl"
Write-Host "======================================================" -ForegroundColor Cyan

Ensure-ClientPluginInstalled -TargetGameDir $resolvedGame -RepoRoot $repoRoot
Update-LauncherConfigToLocal -TargetGameDir $resolvedGame -LocalUrl $localUrl -RepoRoot $repoRoot -CustomPlayerName $PlayerName

$serverProc = $null
$startedByUs = $false

try {
    if (Test-ServerHealth -HealthUrl $healthUrl) {
        Write-Host "[Standalone] Local BazaarServer is already healthy at $healthUrl" -ForegroundColor Yellow
    }
    else {
        $csproj = Join-Path $repoRoot "src\BazaarServer\BazaarServer.csproj"
        $prebuiltExe = Join-Path $repoRoot "dist\server\BazaarServer.exe"
        $binExe = Join-Path $repoRoot "src\BazaarServer\bin\Release\net8.0\BazaarServer.exe"
        $binDll = Join-Path $repoRoot "src\BazaarServer\bin\Release\net8.0\BazaarServer.dll"

        $serverFile = $null
        $serverArgs = ""

        if (Test-Path $prebuiltExe) {
            $serverFile = $prebuiltExe
        }
        elseif (Test-Path $csproj) {
            if (-not $NoBuild) {
                Write-Host "[Standalone] Building BazaarServer (Release)..." -ForegroundColor Cyan
                & dotnet build $csproj -c Release --nologo
                if ($LASTEXITCODE -ne 0) {
                    throw "BazaarServer build failed with exit code $LASTEXITCODE"
                }
            }
            if (Test-Path $binExe) {
                $serverFile = $binExe
            }
            elseif (Test-Path $binDll) {
                $serverFile = "dotnet"
                $serverArgs = "`"$binDll`""
            }
            else {
                $serverFile = "dotnet"
                $serverArgs = "run --project `"$csproj`" -c Release --no-build"
            }
        }
        else {
            throw "Cannot locate BazaarServer binary or project under $repoRoot"
        }

        $env:BAZAAR_BIND = "127.0.0.1"
        $env:BAZAAR_PORT = "$Port"
        $env:BAZAAR_DATA = $resolvedData
        $env:BAZAAR_STATE = $StateDir
        $env:BAZAAR_GHOSTS = Join-Path $resolvedData "ghost_pool_balanced.jsonl"

        $outLog = Join-Path $logsDir "standalone-server.out.log"
        $errLog = Join-Path $logsDir "standalone-server.err.log"

        Write-Host "[Standalone] Starting BazaarServer ($serverFile $serverArgs)..." -ForegroundColor Cyan
        $psiParams = @{
            FilePath = $serverFile
            WorkingDirectory = (Split-Path $resolvedData -Parent)
            RedirectStandardOutput = $outLog
            RedirectStandardError = $errLog
            WindowStyle = "Hidden"
            PassThru = $true
        }
        if ($serverArgs) { $psiParams["ArgumentList"] = $serverArgs }

        $serverProc = Start-Process @psiParams
        $startedByUs = $true

        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            if ($serverProc.HasExited) {
                $errTail = if (Test-Path $errLog) { Get-Content $errLog -Tail 20 | Out-String } else { "" }
                throw "BazaarServer exited prematurely (code=$($serverProc.ExitCode)). $errTail"
            }
            if (Test-ServerHealth -HealthUrl $healthUrl) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            throw "Timed out waiting for $healthUrl"
        }
        Write-Host "[Standalone] BazaarServer is ready -> $healthUrl" -ForegroundColor Green
    }

    $gameExe = Join-Path $resolvedGame "TheBazaar.exe"
    Write-Host "[Standalone] Launching $gameExe ..." -ForegroundColor Cyan
    $gameProc = Start-Process -FilePath $gameExe -WorkingDirectory $resolvedGame -PassThru
    Write-Host "[Standalone] Game started (PID=$($gameProc.Id)). Waiting for game exit to clean up..." -ForegroundColor Green
    $gameProc.WaitForExit()
    Write-Host "[Standalone] Game exited (code=$($gameProc.ExitCode))." -ForegroundColor Cyan
}
finally {
    if ($startedByUs -and $serverProc -and -not $serverProc.HasExited -and -not $KeepServerRunning) {
        Write-Host "[Standalone] Gracefully stopping local BazaarServer (PID=$($serverProc.Id))..." -ForegroundColor Yellow
        try {
            $null = $serverProc.CloseMainWindow()
            if (-not $serverProc.WaitForExit(3000)) {
                Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
                $serverProc.WaitForExit(3000) | Out-Null
            }
            Write-Host "[Standalone] BazaarServer stopped. Save database preserved at: $StateDir\bazaar.db" -ForegroundColor Green
        }
        catch {
            Write-Warning "Failed to stop BazaarServer cleanly: $($_.Exception.Message)"
        }
    }
}
