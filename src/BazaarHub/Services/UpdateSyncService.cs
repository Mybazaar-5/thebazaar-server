using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 启动前同步结果纯静态门禁决策器（P0-47）。
/// </summary>
public static class LaunchSyncGate
{
    public static LaunchGateAction Evaluate(UpdateSyncReport? report)
    {
        if (report == null || report.DegradedOffline)
        {
            return LaunchGateAction.AllowLaunch;
        }

        if (report.BlockedByRunningGame)
        {
            return LaunchGateAction.BlockRunningGame;
        }

        if (report.HasCorePluginFailure || report.BlockedByMinVersion || report.RolledBackAtomicGroups.Count > 0)
        {
            return LaunchGateAction.BlockCoreFailure;
        }

        if (!report.Success || report.FailedCount > 0)
        {
            return LaunchGateAction.WarnNonCoreFailure;
        }

        return LaunchGateAction.AllowLaunch;
    }
}

/// <summary>
/// 客户端增量热更新与 Mod / 汉化同步服务。
/// 对接服务端：
/// - GET /api/client-update/manifest
/// - GET /api/mods/manifest
/// - GET /api/mods/assets/{sha256}
/// - GET /api/localization/overlay（自动回退兼容 /api/localization/zh-overlay?locale=zh-CN）
/// 强化保障：
/// - P0-34：全路径经 SafePathAndZipGuard 严控，Mod 资产按 64 位 hex + 白名单扩展名安全落盘，流式字节上限拦截。
/// - P0-45：权威 targetPath / updatePolicy 解析，本地用户配置 (.cfg / player-name.txt 等) 客户端硬保护不可覆写，兼容旧服务端防错写。
/// - P0-47：核心二进制写入前进程锁 + 只读属性/排他文件锁预检；同原子组两阶段 Staging → Verify → Commit / 自动逆序 Rollback。
/// </summary>
public sealed class UpdateSyncService : IDisposable
{
    public const long MaxClientBinaryBytes = 64L * 1024 * 1024;
    public const long MaxModAssetBytes = 16L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> RootLoaderFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "winhttp.dll",
        "doorstop_config.ini",
        ".doorstop_version"
    };

    private static readonly HashSet<string> CorePluginFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BazaarLauncher.dll",
        "BazaarModSdk.dll"
    };

    private static readonly HashSet<string> IgnoredDebugExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml",
        ".pdb",
        ".mdb"
    };

    // P0-28 方案 A 对齐：Unity 运行期仅支持 PNG/JPEG，拒绝同步不可解码的 .webp
    private static readonly HashSet<string> AllowedModAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg"
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateSyncService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BazaarHub/{LauncherConstants.HubVersion}");
            _ownsClient = true;
        }
    }

    /// <summary>
    /// 计算本地文件的 SHA-256（小写十六进制）；文件不存在返回空串。
    /// </summary>
    public static string ComputeFileSha256(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 计算字节数组 SHA-256（小写十六进制）。
    /// </summary>
    public static string ComputeBytesSha256(ReadOnlySpan<byte> data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 规范化哈希字符串（移除可选 "sha256:" 前缀并转为小写）。
    /// </summary>
    public static string NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            s = s["sha256:".Length..];
        }
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 检测当前是否存在正在运行的 TheBazaar 游戏进程（自动释放全部 Process 句柄防泄漏）。
    /// </summary>
    public static bool IsGameProcessRunning()
    {
        Process[] procs = Process.GetProcessesByName("TheBazaar");
        try
        {
            foreach (Process p in procs)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 无法确认退出状态时（如权限边界）保守视为运行中，防止热写损坏二进制
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (Process p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// 检查更新或执行全量增量同步。
    /// </summary>
    public Task<UpdateSyncReport> SyncAsync(
        string gameDir,
        string serverBase,
        bool applyDownloads = true,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
        => SyncAsync(
            gameDir,
            serverBase,
            applyDownloads,
            progress,
            log,
            isGameRunningProbe: null,
            beforeCommitMoveHook: null,
            cancellationToken);

    /// <summary>
    /// 支持测试注入进程探针与提交钩子的内部核心同步入口。
    /// </summary>
    internal async Task<UpdateSyncReport> SyncAsync(
        string gameDir,
        string serverBase,
        bool applyDownloads,
        IProgress<SyncProgress>? progress,
        Action<string>? log,
        Func<bool>? isGameRunningProbe,
        Action<string>? beforeCommitMoveHook,
        CancellationToken cancellationToken = default)
    {
        string normalizedBase = LauncherConfigService.NormalizeServerUrl(serverBase);
        var report = new UpdateSyncReport
        {
            ServerBase = normalizedBase
        };

        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            report.Success = true;
            report.SummaryMessage = "当前处于纯净官方原版模式，无需连接自建服同步。";
            return report;
        }

        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            report.Success = false;
            report.Errors.Add("游戏目录不存在，请先定位有效的《The Bazaar》目录。");
            report.SummaryMessage = "同步中止：未指定有效游戏目录。";
            return report;
        }

        int unreachableEndpoints = 0;

        progress?.Report(new SyncProgress { Percent = 5, Stage = "检查清单", Message = "正在连接服务端获取客户端更新清单..." });
        log?.Invoke($"[同步] 开始连接 {normalizedBase} 检查更新清单（模式：{(applyDownloads ? "增量同步" : "仅检查")}）...");

        // 1. 拉取 /api/client-update/manifest
        (ClientUpdateManifest? clientManifest, bool clientNetErr, string? clientWarn) =
            await FetchClientUpdateManifestAsync(normalizedBase, cancellationToken);
        report.ClientManifest = clientManifest;
        if (clientNetErr) unreachableEndpoints++;
        if (!string.IsNullOrWhiteSpace(clientWarn))
        {
            report.Warnings.Add(clientWarn);
            log?.Invoke($"[警告] {clientWarn}");
        }

        // 2. 拉取 /api/mods/manifest
        progress?.Report(new SyncProgress { Percent = 20, Stage = "检查 Mod", Message = "正在拉取服务端启用 Mod 与资产清单..." });
        (RemoteModsManifest? modsManifest, bool modsNetErr, string? modsWarn) =
            await FetchModsManifestAsync(normalizedBase, cancellationToken);
        report.ModsManifest = modsManifest;
        if (modsNetErr) unreachableEndpoints++;
        if (!string.IsNullOrWhiteSpace(modsWarn))
        {
            report.Warnings.Add(modsWarn);
            log?.Invoke($"[警告] {modsWarn}");
        }

        // 3. 拉取 /api/localization/overlay
        progress?.Report(new SyncProgress { Percent = 35, Stage = "检查汉化", Message = "正在拉取服务端外挂中文词典..." });
        (LocalizationOverlayInfo? overlayInfo, bool l10nNetErr, string? l10nWarn) =
            await FetchLocalizationOverlayAsync(normalizedBase, cancellationToken);
        if (l10nNetErr) unreachableEndpoints++;
        if (!string.IsNullOrWhiteSpace(l10nWarn))
        {
            report.Warnings.Add(l10nWarn);
            log?.Invoke($"[警告] {l10nWarn}");
        }

        // 若全部端点均因网络不可达失败，优雅降级（不阻断玩家离线/缓存启动）
        if (unreachableEndpoints >= 3 && clientManifest == null && modsManifest == null && overlayInfo == null)
        {
            report.Success = true;
            report.DegradedOffline = true;
            report.SummaryMessage = "无法连接更新服务器，已自动降级为本地缓存模式（可直接启动游戏）。";
            progress?.Report(new SyncProgress { Percent = 100, Stage = "已降级", Message = report.SummaryMessage });
            log?.Invoke($"[警告] {report.SummaryMessage}");
            return report;
        }

        // 构建待同步任务列表
        var tasks = new List<PendingSyncTask>();
        var seenClientTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 收集客户端更新文件（插件 DLL / Loader 等）
        if (clientManifest != null)
        {
            // D7-P1-05 门控 B：比对服务端声明的 MinHubVersion / HubVersion，提前提示玩家升级 BazaarHub
            EvaluateHubUpgradeAdvisory(clientManifest, report, log);

            foreach (RemoteClientFileEntry file in clientManifest.Files)
            {
                string relTarget = ResolveClientFileRelativePath(file);
                if (string.IsNullOrWhiteSpace(relTarget)) continue;

                // 客户端双重防线：过滤调试垃圾文件（.xml/.pdb/.mdb）
                if (IsIgnoredDebugArtifact(relTarget))
                {
                    log?.Invoke($"[同步] 忽略非必需调试文件：{relTarget}");
                    continue;
                }

                if (!seenClientTargets.Add(relTarget))
                {
                    continue;
                }

                string fullTarget;
                try
                {
                    fullTarget = ResolveSafePath(gameDir, relTarget);
                }
                catch (Exception ex)
                {
                    report.FailedCount++;
                    report.HasCorePluginFailure = true;
                    report.Errors.Add($"非法更新目标路径 {relTarget}：{ex.Message}");
                    report.Items.Add(new SyncItemStatus
                    {
                        Category = "安全拦截",
                        DisplayName = string.IsNullOrWhiteSpace(file.Name) ? relTarget : file.Name,
                        TargetRelativePath = relTarget,
                        RemoteSha256 = NormalizeSha256(file.Sha256),
                        SizeBytes = file.SizeBytes,
                        Action = "已拦截",
                        Detail = ex.Message
                    });
                    continue;
                }

                string localSha = ComputeFileSha256(fullTarget);
                string remoteSha = NormalizeSha256(file.Sha256);
                string resolvedUrl = ResolveAbsoluteUrl(normalizedBase, file.DownloadUrl);
                string atomicGroup = ResolveAtomicGroup(file, relTarget);
                bool isCoreCritical = IsCoreCriticalTask(file, relTarget, atomicGroup);

                string categoryLabel = file.Category.Equals("localization", StringComparison.OrdinalIgnoreCase)
                    ? "汉化词典"
                    : (file.Category.Equals("loader", StringComparison.OrdinalIgnoreCase) || atomicGroup == "bepinex-loader"
                        ? "引导核心"
                        : "核心插件");

                tasks.Add(new PendingSyncTask
                {
                    Category = categoryLabel,
                    DisplayName = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(relTarget) : file.Name,
                    TargetRelativePath = relTarget,
                    FullTargetPath = fullTarget,
                    LocalSha256 = localSha,
                    RemoteSha256 = remoteSha,
                    RemoteFileVersion = !string.IsNullOrWhiteSpace(file.FileVersion) ? file.FileVersion : file.Version,
                    DownloadUrl = resolvedUrl,
                    SizeBytes = file.SizeBytes,
                    MaxDownloadBytes = MaxClientBinaryBytes,
                    UpdatePolicy = NormalizeUpdatePolicy(file.UpdatePolicy),
                    AtomicGroup = atomicGroup,
                    IsCoreCritical = isCoreCritical,
                    IsProtectedUserFile = IsProtectedClientUserFile(relTarget),
                    BackupOldPlugin = relTarget.StartsWith(LauncherConstants.PluginsRelativeDir + @"\", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        // 收集外挂汉化词典（若 /api/localization/overlay 返回了实体内容）
        if (overlayInfo != null && overlayInfo.Available && overlayInfo.RawBytes.Length > 0)
        {
            report.LocalizationRevision = string.IsNullOrWhiteSpace(overlayInfo.Revision)
                ? (clientManifest?.LocalizationVersion ?? "latest")
                : overlayInfo.Revision;

            string relOverlay = LauncherConstants.ZhOverlayRelativePath;
            string fullOverlay = ResolveSafePath(gameDir, relOverlay);
            string localSha = ComputeFileSha256(fullOverlay);
            string remoteSha = NormalizeSha256(overlayInfo.Sha256);
            if (string.IsNullOrEmpty(remoteSha))
            {
                remoteSha = ComputeBytesSha256(overlayInfo.RawBytes);
            }

            // 若 clientManifest 里已经列过同一路径则去重，以直连 overlay 为准
            tasks.RemoveAll(t => string.Equals(t.TargetRelativePath, relOverlay, StringComparison.OrdinalIgnoreCase));
            tasks.Add(new PendingSyncTask
            {
                Category = "汉化词典",
                DisplayName = $"外挂简体中文词典 ({report.LocalizationRevision})",
                TargetRelativePath = relOverlay,
                FullTargetPath = fullOverlay,
                LocalSha256 = localSha,
                RemoteSha256 = remoteSha,
                InlineBytes = overlayInfo.RawBytes,
                SizeBytes = overlayInfo.RawBytes.Length,
                MaxDownloadBytes = MaxModAssetBytes,
                UpdatePolicy = "always",
                AtomicGroup = string.Empty,
                IsCoreCritical = false
            });
        }
        else if (!string.IsNullOrWhiteSpace(clientManifest?.LocalizationVersion))
        {
            report.LocalizationRevision = clientManifest.LocalizationVersion;
        }

        // 收集 Mod 资产（严格校检 SHA-256 与白名单图片扩展名防穿越）
        if (modsManifest != null)
        {
            var allAssets = new List<RemoteModAssetEntry>();
            allAssets.AddRange(modsManifest.Assets);
            foreach (RemoteModEntry mod in modsManifest.Mods)
            {
                foreach (RemoteModAssetEntry asset in mod.Assets)
                {
                    if (string.IsNullOrWhiteSpace(asset.ModId))
                    {
                        allAssets.Add(new RemoteModAssetEntry
                        {
                            ModId = mod.Id,
                            LogicalPath = asset.LogicalPath,
                            Sha256 = asset.Sha256,
                            DownloadUrl = asset.DownloadUrl,
                            SizeBytes = asset.SizeBytes,
                            Width = asset.Width,
                            Height = asset.Height
                        });
                    }
                    else
                    {
                        allAssets.Add(asset);
                    }
                }
            }

            var seenAssetTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RemoteModAssetEntry asset in allAssets)
            {
                // P0-28 方案 A：明确提示并跳过 Unity 客户端无法解码的 WebP 资产
                string rawExt = Path.GetExtension(asset.LogicalPath ?? string.Empty) ?? string.Empty;
                if (string.Equals(rawExt, ".webp", StringComparison.OrdinalIgnoreCase))
                {
                    string warnMsg = $"已跳过不支持的 WebP 资产（{asset.ModId}/{asset.LogicalPath}），Unity 客户端仅支持 PNG/JPEG。";
                    report.Warnings.Add(warnMsg);
                    report.SkippedUpToDateCount++;
                    report.Items.Add(new SyncItemStatus
                    {
                        Category = "Mod 资产",
                        DisplayName = $"{asset.ModId}/{asset.LogicalPath}",
                        TargetRelativePath = asset.LogicalPath ?? string.Empty,
                        RemoteSha256 = NormalizeSha256(asset.Sha256),
                        SizeBytes = asset.SizeBytes,
                        Action = "已跳过",
                        Detail = "不支持的 WebP 格式（仅支持 PNG/JPEG）"
                    });
                    log?.Invoke($"[警告] {warnMsg}");
                    continue;
                }

                string sha = NormalizeSha256(asset.Sha256);
                string relAsset;
                string fullTarget;
                try
                {
                    relAsset = BuildModAssetCacheRelativePath(asset, sha);
                    if (!seenAssetTargets.Add(relAsset)) continue;
                    fullTarget = ResolveSafePath(gameDir, relAsset);
                }
                catch (Exception ex)
                {
                    report.FailedCount++;
                    report.Errors.Add($"非法 Mod 资产路径（{asset.ModId}/{asset.LogicalPath}）：{ex.Message}");
                    report.Items.Add(new SyncItemStatus
                    {
                        Category = "Mod 资产",
                        DisplayName = $"{asset.ModId}/{asset.LogicalPath}",
                        TargetRelativePath = asset.LogicalPath ?? string.Empty,
                        RemoteSha256 = sha,
                        SizeBytes = asset.SizeBytes,
                        Action = "已拦截",
                        Detail = ex.Message
                    });
                    continue;
                }

                string localSha = ComputeFileSha256(fullTarget);
                string url = !string.IsNullOrWhiteSpace(asset.DownloadUrl)
                    ? ResolveAbsoluteUrl(normalizedBase, asset.DownloadUrl)
                    : (SafePathAndZipGuard.TryNormalizeSha256Hex(sha, out string cleanSha)
                        ? $"{normalizedBase}/api/mods/assets/{cleanSha}"
                        : string.Empty);

                tasks.Add(new PendingSyncTask
                {
                    Category = "Mod 资产",
                    DisplayName = string.IsNullOrWhiteSpace(asset.LogicalPath)
                        ? $"{asset.ModId}/{(sha.Length >= 8 ? sha[..8] : "asset")}"
                        : $"{(string.IsNullOrWhiteSpace(asset.ModId) ? "mod" : asset.ModId)}/{asset.LogicalPath}",
                    TargetRelativePath = relAsset,
                    FullTargetPath = fullTarget,
                    LocalSha256 = localSha,
                    RemoteSha256 = sha,
                    DownloadUrl = url,
                    SizeBytes = asset.SizeBytes,
                    MaxDownloadBytes = MaxModAssetBytes,
                    UpdatePolicy = "always",
                    AtomicGroup = string.Empty,
                    IsCoreCritical = false
                });
            }
        }

        // 阶段 A：策略裁决与差异比对，筛选出实际需要写入磁盘的任务
        var writableItems = new List<(PendingSyncTask Task, SyncItemStatus Status)>();
        foreach (PendingSyncTask item in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = new SyncItemStatus
            {
                Category = item.Category,
                DisplayName = item.DisplayName,
                TargetRelativePath = item.TargetRelativePath,
                LocalSha256 = item.LocalSha256,
                RemoteSha256 = item.RemoteSha256,
                SizeBytes = item.SizeBytes
            };
            report.Items.Add(status);

            bool fileExists = File.Exists(item.FullTargetPath);

            // 硬保护用户配置文件：已存在无条件禁止覆写；不存在时仅显式 ifMissing 才允许初始化一次
            if (item.IsProtectedUserFile)
            {
                if (fileExists || !string.Equals(item.UpdatePolicy, "ifMissing", StringComparison.OrdinalIgnoreCase))
                {
                    status.Action = "已保护";
                    status.Detail = fileExists
                        ? "受保护本地用户配置，已拦截服务端覆写"
                        : "受保护配置文件不接受服务端覆盖";
                    report.SkippedUpToDateCount++;
                    log?.Invoke($"[保护] 已跳过受保护配置：{item.TargetRelativePath}");
                    continue;
                }
            }

            if (string.Equals(item.UpdatePolicy, "never", StringComparison.OrdinalIgnoreCase))
            {
                status.Action = "已跳过";
                status.Detail = "更新策略为 never，跳过写入";
                report.SkippedUpToDateCount++;
                continue;
            }

            if (string.Equals(item.UpdatePolicy, "ifMissing", StringComparison.OrdinalIgnoreCase) && fileExists)
            {
                status.Action = "已保留";
                status.Detail = "本地文件已存在，按 ifMissing 策略保留";
                report.SkippedUpToDateCount++;
                continue;
            }

            bool upToDate = !string.IsNullOrEmpty(item.RemoteSha256) &&
                            string.Equals(item.LocalSha256, item.RemoteSha256, StringComparison.OrdinalIgnoreCase) &&
                            fileExists;

            if (upToDate)
            {
                status.Action = "已是最新";
                status.Detail = "SHA-256 一致，跳过下载";
                report.SkippedUpToDateCount++;
                continue;
            }

            // D7-P1-05 门控 C：防止旧版服务端降级覆写玩家本地更高版本的核心插件 DLL（双方均为合法 SemVer 才触发，兼容非 PE 测试桩）
            if (fileExists &&
                item.IsCoreCritical &&
                item.FullTargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.RemoteFileVersion))
            {
                string? localFileVer = TryReadLocalPeFileVersion(item.FullTargetPath);
                if (!string.IsNullOrWhiteSpace(localFileVer) &&
                    SemVerComparer.IsGreaterThan(localFileVer, item.RemoteFileVersion))
                {
                    status.Action = "已保留（本地更新）";
                    status.Detail = $"本地版本 v{localFileVer} 高于服务端 v{item.RemoteFileVersion}，已阻止降级覆盖";
                    report.DowngradeProtectedCount++;
                    report.SkippedUpToDateCount++;
                    log?.Invoke($"[版本保护] {item.TargetRelativePath} 本地 v{localFileVer} > 服务端 v{item.RemoteFileVersion}，已保留本地新版。");
                    continue;
                }
            }

            if (!applyDownloads)
            {
                status.Action = fileExists ? "待更新" : "待下载";
                status.Detail = string.IsNullOrEmpty(item.LocalSha256) ? "本地缺失" : "本地哈希与服务端不同";
                continue;
            }

            writableItems.Add((item, status));
        }

        // 阶段 B：仅在确实需要落盘写入核心二进制时，执行进程锁 + 只读属性/排他文件锁预检（P0-47）
        if (applyDownloads && writableItems.Count > 0)
        {
            var coreWritable = writableItems.Where(x => x.Task.IsCoreCritical).ToList();
            if (coreWritable.Count > 0)
            {
                if (!PreflightCoreWriteLocks(coreWritable, writableItems, report, isGameRunningProbe, log))
                {
                    FinalizeReportSummary(report, applyDownloads);
                    progress?.Report(new SyncProgress { Percent = 100, Stage = "已拦截", Message = report.SummaryMessage });
                    log?.Invoke($"[拦截] {report.SummaryMessage}");
                    return report;
                }
            }

            // 阶段 C：先按原子组两阶段提交（Stage → Verify → Commit / 逆序 Rollback），再处理独立非原子资产
            var atomicGroupOrder = new List<string>();
            var atomicBuckets = new Dictionary<string, List<(PendingSyncTask Task, SyncItemStatus Status)>>(StringComparer.OrdinalIgnoreCase);
            var nonAtomicItems = new List<(PendingSyncTask Task, SyncItemStatus Status)>();

            foreach (var pair in writableItems)
            {
                if (!string.IsNullOrEmpty(pair.Task.AtomicGroup))
                {
                    if (!atomicBuckets.TryGetValue(pair.Task.AtomicGroup, out var bucket))
                    {
                        bucket = new List<(PendingSyncTask, SyncItemStatus)>();
                        atomicBuckets[pair.Task.AtomicGroup] = bucket;
                        atomicGroupOrder.Add(pair.Task.AtomicGroup);
                    }
                    bucket.Add(pair);
                }
                else
                {
                    nonAtomicItems.Add(pair);
                }
            }

            int completedSteps = 0;
            int totalSteps = writableItems.Count;

            foreach (string groupName in atomicGroupOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupItems = atomicBuckets[groupName];
                await ExecuteAtomicGroupAsync(
                    gameDir,
                    groupName,
                    groupItems,
                    report,
                    progress,
                    log,
                    beforeCommitMoveHook,
                    completedSteps,
                    totalSteps,
                    cancellationToken);
                completedSteps += groupItems.Count;
            }

            foreach (var (item, status) in nonAtomicItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completedSteps++;
                int pct = 40 + (int)(completedSteps * 55.0 / Math.Max(1, totalSteps));
                progress?.Report(new SyncProgress
                {
                    Percent = Math.Clamp(pct, 40, 96),
                    Stage = "增量同步",
                    Message = $"正在同步 ({completedSteps}/{totalSteps})：{item.DisplayName}"
                });

                try
                {
                    long writtenBytes;
                    if (item.InlineBytes != null)
                    {
                        writtenBytes = WriteAndVerifyAtomic(item.FullTargetPath, item.InlineBytes, item.RemoteSha256);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
                        {
                            throw new InvalidOperationException("缺少有效下载地址。");
                        }

                        if (item.BackupOldPlugin && File.Exists(item.FullTargetPath))
                        {
                            BackupPreviousPlugin(gameDir, item.FullTargetPath);
                        }

                        writtenBytes = await DownloadAndVerifyAtomicAsync(
                            item.DownloadUrl,
                            item.FullTargetPath,
                            item.RemoteSha256,
                            item.MaxDownloadBytes,
                            cancellationToken);
                    }

                    report.DownloadedCount++;
                    report.TotalBytesDownloaded += writtenBytes;
                    status.Action = "已更新";
                    status.Detail = $"校验通过 ({FormatBytes(writtenBytes)})";
                    log?.Invoke($"[同步] 已更新：{item.TargetRelativePath} ({FormatBytes(writtenBytes)})");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.FailedCount++;
                    if (item.IsCoreCritical)
                    {
                        report.HasCorePluginFailure = true;
                    }
                    status.Action = "失败";
                    status.Detail = ex.Message;
                    report.Errors.Add($"{item.DisplayName}: {ex.Message}");
                    log?.Invoke($"[错误] 同步失败 {item.DisplayName}：{ex.Message}");
                }
            }
        }

        // 若存在 Mod 清单且允许落盘，写入本地 Mod 缓存索引供客户端插件与离线视图复用
        if (applyDownloads && modsManifest != null)
        {
            try
            {
                string cacheIndexPath = ResolveSafePath(gameDir, Path.Combine(LauncherConstants.ModCacheRelativeDir, "manifest.local.json"));
                string json = JsonSerializer.Serialize(modsManifest, PrettyJsonOptions);
                WriteAndVerifyAtomic(cacheIndexPath, Utf8NoBom.GetBytes(json), string.Empty);
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"写入本地 Mod 索引缓存失败：{ex.Message}");
            }
        }

        // D7-P1-05 门控 A：核验客户端有效插件版本是否满足服务端 MinRequiredVersion（兼容 Wave1D 非 PE 测试桩）
        if (clientManifest != null)
        {
            EvaluateMinRequiredVersionGate(gameDir, clientManifest, report, log);
        }

        FinalizeReportSummary(report, applyDownloads);
        progress?.Report(new SyncProgress { Percent = 100, Stage = "完成", Message = report.SummaryMessage });
        log?.Invoke($"[同步] {report.SummaryMessage}");
        return report;
    }

    private static void EvaluateHubUpgradeAdvisory(ClientUpdateManifest manifest, UpdateSyncReport report, Action<string>? log)
    {
        string currentHub = LauncherConstants.HubVersion;
        if (!string.IsNullOrWhiteSpace(manifest.MinHubVersion) &&
            SemVerComparer.IsLessThan(currentHub, manifest.MinHubVersion))
        {
            report.HubUpgradeRequired = true;
            report.HubUpgradeRecommended = true;
            report.RecommendedHubVersion = manifest.MinHubVersion;
            string msg = $"当前启动器 BazaarHub v{currentHub} 低于服务端建议最低版本 v{manifest.MinHubVersion}，请升级新版 BazaarHub。";
            report.Warnings.Add(msg);
            log?.Invoke($"[版本警告] {msg}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.HubVersion) &&
            SemVerComparer.IsLessThan(currentHub, manifest.HubVersion))
        {
            report.HubUpgradeRecommended = true;
            report.RecommendedHubVersion = manifest.HubVersion;
            string msg = $"发现新版启动器 BazaarHub v{manifest.HubVersion}（当前 v{currentHub}），建议前往发布页升级。";
            report.Warnings.Add(msg);
            log?.Invoke($"[版本提示] {msg}");
        }
    }

    private static void EvaluateMinRequiredVersionGate(
        string gameDir,
        ClientUpdateManifest manifest,
        UpdateSyncReport report,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(manifest.MinRequiredVersion) ||
            !SemVerComparer.TryParse(manifest.MinRequiredVersion, out _))
        {
            return;
        }

        // 解析有效插件版本：优先本地 PE FileVersion -> 回落 manifest.LauncherVersion -> 回落清单 BazaarLauncher.dll 版本（兼容非 PE 测试桩）
        string launcherPath = Path.Combine(gameDir, LauncherConstants.PluginsRelativeDir, "BazaarLauncher.dll");
        string? effectiveVer = TryReadLocalPeFileVersion(launcherPath);
        if (string.IsNullOrWhiteSpace(effectiveVer) || !SemVerComparer.TryParse(effectiveVer, out _))
        {
            if (SemVerComparer.TryParse(manifest.LauncherVersion, out _))
            {
                effectiveVer = manifest.LauncherVersion;
            }
            else
            {
                RemoteClientFileEntry? launcherFile = manifest.Files.FirstOrDefault(f =>
                    f.Name.Equals("BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativeTargetPath.EndsWith("BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase));
                string? candidate = !string.IsNullOrWhiteSpace(launcherFile?.FileVersion)
                    ? launcherFile.FileVersion
                    : launcherFile?.Version;
                if (SemVerComparer.TryParse(candidate, out _))
                {
                    effectiveVer = candidate;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveVer))
        {
            return;
        }

        if (SemVerComparer.IsLessThan(effectiveVer, manifest.MinRequiredVersion))
        {
            report.BlockedByMinVersion = true;
            report.HasCorePluginFailure = true;
            report.FailedCount++;
            string err = $"客户端核心插件版本 v{effectiveVer} 低于服务端最低兼容要求 v{manifest.MinRequiredVersion}，已阻断启动，请更新客户端或联系服主升级热更载荷。";
            report.Errors.Add(err);
            log?.Invoke($"[版本门控] {err}");
        }
    }

    internal static string? TryReadLocalPeFileVersion(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(filePath);
            string? raw = !string.IsNullOrWhiteSpace(fvi.FileVersion)
                ? fvi.FileVersion
                : fvi.ProductVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string clean = raw.Trim();
            return SemVerComparer.TryParse(clean, out _) ? clean : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool PreflightCoreWriteLocks(
        List<(PendingSyncTask Task, SyncItemStatus Status)> coreWritable,
        List<(PendingSyncTask Task, SyncItemStatus Status)> allWritable,
        UpdateSyncReport report,
        Func<bool>? isGameRunningProbe,
        Action<string>? log)
    {
        bool running = isGameRunningProbe != null ? isGameRunningProbe() : IsGameProcessRunning();
        if (running)
        {
            const string runningMsg = "检测到游戏进程 TheBazaar.exe 正在运行，请先完全关闭游戏后重试。";
            report.BlockedByRunningGame = true;
            report.HasCorePluginFailure = true;
            report.Errors.Add(runningMsg);
            foreach (var (task, status) in allWritable)
            {
                status.Action = "已拦截";
                status.Detail = task.IsCoreCritical
                    ? "游戏进程 TheBazaar.exe 运行中，已拦截核心文件覆盖"
                    : "因游戏运行中中止本次同步";
                if (task.IsCoreCritical)
                {
                    report.FailedCount++;
                }
            }
            log?.Invoke($"[拦截] {runningMsg}");
            return false;
        }

        var lockErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (task, _) in coreWritable)
        {
            if (!File.Exists(task.FullTargetPath))
            {
                continue;
            }

            try
            {
                FileAttributes attrs = File.GetAttributes(task.FullTargetPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    lockErrors[task.TargetRelativePath] =
                        $"目标文件被标记为只读属性：{task.TargetRelativePath}，请取消只读属性后重试";
                    continue;
                }

                using var fs = new FileStream(task.FullTargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                lockErrors[task.TargetRelativePath] =
                    $"目标文件被其他进程或杀毒软件占用：{task.TargetRelativePath}（{ex.Message}）";
            }
            catch (UnauthorizedAccessException ex)
            {
                lockErrors[task.TargetRelativePath] =
                    $"目标文件缺少写入权限或被占用：{task.TargetRelativePath}（{ex.Message}）";
            }
        }

        if (lockErrors.Count == 0)
        {
            return true;
        }

        report.HasCorePluginFailure = true;
        foreach (var kv in lockErrors)
        {
            report.Errors.Add(kv.Value);
            log?.Invoke($"[拦截] {kv.Value}");
        }

        foreach (var (task, status) in allWritable)
        {
            status.Action = "已拦截";
            if (lockErrors.TryGetValue(task.TargetRelativePath, out string? reason))
            {
                status.Detail = reason;
                report.FailedCount++;
            }
            else if (task.IsCoreCritical)
            {
                status.Detail = "前置核心文件可写性检查未通过，已整批中止";
                report.FailedCount++;
            }
            else
            {
                status.Detail = "核心更新预检未通过，暂缓写入";
            }
        }

        return false;
    }

    private async Task ExecuteAtomicGroupAsync(
        string gameDir,
        string groupName,
        List<(PendingSyncTask Task, SyncItemStatus Status)> groupItems,
        UpdateSyncReport report,
        IProgress<SyncProgress>? progress,
        Action<string>? log,
        Action<string>? beforeCommitMoveHook,
        int baseStepOffset,
        int totalSteps,
        CancellationToken cancellationToken)
    {
        string stagingRoot = Path.Combine(gameDir, LauncherConstants.StagingRootFolderName);
        string txDir = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        string newDir = Path.Combine(txDir, "new");
        string bakDir = Path.Combine(txDir, "bak");
        Directory.CreateDirectory(newDir);
        Directory.CreateDirectory(bakDir);

        try
        {
            // ── 阶段 1：Staging（全量下载至隔离暂存目录并流式校验 SHA-256）────
            var staged = new List<(PendingSyncTask Task, SyncItemStatus Status, string StagedPath, long Bytes)>();
            int failedStageIndex = -1;
            string stageFailMessage = string.Empty;

            for (int i = 0; i < groupItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (task, status) = groupItems[i];
                int pct = 40 + (int)((baseStepOffset + i + 1) * 50.0 / Math.Max(1, totalSteps));
                progress?.Report(new SyncProgress
                {
                    Percent = Math.Clamp(pct, 40, 90),
                    Stage = $"暂存校验 [{groupName}]",
                    Message = $"正在下载并校验 ({baseStepOffset + i + 1}/{totalSteps})：{task.DisplayName}"
                });

                string stagedPath = Path.Combine(newDir, $"{i:D3}_{SanitizePathSegment(Path.GetFileName(task.TargetRelativePath))}.staged");
                try
                {
                    long bytes;
                    if (task.InlineBytes != null)
                    {
                        bytes = WriteAndVerifyToFile(stagedPath, task.InlineBytes, task.RemoteSha256);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(task.DownloadUrl))
                        {
                            throw new InvalidOperationException("缺少有效下载地址。");
                        }
                        bytes = await DownloadAndVerifyToFileAsync(
                            task.DownloadUrl,
                            stagedPath,
                            task.RemoteSha256,
                            task.MaxDownloadBytes,
                            cancellationToken);
                    }
                    staged.Add((task, status, stagedPath, bytes));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedStageIndex = i;
                    stageFailMessage = ex.Message;
                    status.Action = "失败";
                    status.Detail = ex.Message;
                    report.FailedCount++;
                    report.HasCorePluginFailure = true;
                    report.Errors.Add($"[{groupName}] {task.DisplayName}: {ex.Message}");
                    log?.Invoke($"[错误] 原子组 [{groupName}] 暂存失败 {task.DisplayName}：{ex.Message}");
                    break;
                }
            }

            if (failedStageIndex >= 0)
            {
                if (!report.RolledBackAtomicGroups.Contains(groupName))
                {
                    report.RolledBackAtomicGroups.Add(groupName);
                }

                for (int i = 0; i < groupItems.Count; i++)
                {
                    if (i == failedStageIndex) continue;
                    groupItems[i].Status.Action = "已回滚";
                    groupItems[i].Status.Detail = $"同原子组 [{groupName}] 存在下载/校验失败，已整组放弃提交（工作目录未触碰）";
                }
                log?.Invoke($"[回滚] 原子组 [{groupName}] 阶段 1 失败（{stageFailMessage}），已放弃整组提交。");
                return;
            }

            // ── 阶段 2：Commit（自动建父目录 → 移旧至同分区 bak → 移新到位；遇错逆序 Rollback）────
            foreach (var entry in staged)
            {
                if (entry.Task.BackupOldPlugin && File.Exists(entry.Task.FullTargetPath))
                {
                    BackupPreviousPlugin(gameDir, entry.Task.FullTargetPath);
                }
            }

            var committed = new List<(PendingSyncTask Task, SyncItemStatus Status, string? BakPath, bool ExistedBefore)>();
            (PendingSyncTask Task, string BakPath)? inProgressBackup = null;
            int failedCommitIndex = -1;

            try
            {
                for (int i = 0; i < staged.Count; i++)
                {
                    var entry = staged[i];
                    failedCommitIndex = i;

                    // 首次安装场景：自动确保目标父目录（如 BepInEx\plugins 或 BepInEx\core）存在
                    string? parentDir = Path.GetDirectoryName(entry.Task.FullTargetPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    beforeCommitMoveHook?.Invoke(entry.Task.TargetRelativePath);

                    bool existedBefore = File.Exists(entry.Task.FullTargetPath);
                    string? bakPath = null;
                    if (existedBefore)
                    {
                        bakPath = Path.Combine(bakDir, $"{i:D3}.bak");
                        File.Move(entry.Task.FullTargetPath, bakPath, overwrite: true);
                        inProgressBackup = (entry.Task, bakPath);
                    }

                    File.Move(entry.StagedPath, entry.Task.FullTargetPath, overwrite: true);
                    inProgressBackup = null;
                    committed.Add((entry.Task, entry.Status, bakPath, existedBefore));
                }

                // 整组提交成功，统一记账
                foreach (var entry in staged)
                {
                    report.DownloadedCount++;
                    report.TotalBytesDownloaded += entry.Bytes;
                    entry.Status.Action = "已更新";
                    entry.Status.Detail = $"原子组 [{groupName}] 校验提交成功 ({FormatBytes(entry.Bytes)})";
                    log?.Invoke($"[同步] 已原子提交 [{groupName}]：{entry.Task.TargetRelativePath} ({FormatBytes(entry.Bytes)})");
                }
            }
            catch (Exception commitEx)
            {
                report.HasCorePluginFailure = true;
                report.FailedCount++;
                if (!report.RolledBackAtomicGroups.Contains(groupName))
                {
                    report.RolledBackAtomicGroups.Add(groupName);
                }

                report.Errors.Add($"原子组 [{groupName}] 提交中断并触发自动回滚：{commitEx.Message}");
                log?.Invoke($"[回滚] 原子组 [{groupName}] 提交阶段失败：{commitEx.Message}，正在逆序回滚 {committed.Count} 项已提交文件...");

                // 1. 恢复正在迁移中的单个旧文件
                if (inProgressBackup != null)
                {
                    try
                    {
                        if (File.Exists(inProgressBackup.Value.BakPath))
                        {
                            File.Move(inProgressBackup.Value.BakPath, inProgressBackup.Value.Task.FullTargetPath, overwrite: true);
                        }
                    }
                    catch (Exception rbEx)
                    {
                        string severe = $"[严重] 回滚恢复 {inProgressBackup.Value.Task.TargetRelativePath} 失败：{rbEx.Message}";
                        report.Errors.Add(severe);
                        log?.Invoke(severe);
                    }
                }

                // 2. 逆序回滚所有已提交项
                for (int r = committed.Count - 1; r >= 0; r--)
                {
                    var item = committed[r];
                    try
                    {
                        if (item.ExistedBefore && !string.IsNullOrEmpty(item.BakPath) && File.Exists(item.BakPath))
                        {
                            File.Move(item.BakPath, item.Task.FullTargetPath, overwrite: true);
                        }
                        else if (!item.ExistedBefore && File.Exists(item.Task.FullTargetPath))
                        {
                            File.Delete(item.Task.FullTargetPath);
                        }
                    }
                    catch (Exception rbEx)
                    {
                        string severe = $"[严重] 逆序回滚 {item.Task.TargetRelativePath} 失败：{rbEx.Message}";
                        report.Errors.Add(severe);
                        log?.Invoke(severe);
                    }
                }

                for (int i = 0; i < staged.Count; i++)
                {
                    if (i == failedCommitIndex)
                    {
                        staged[i].Status.Action = "失败";
                        staged[i].Status.Detail = commitEx.Message;
                    }
                    else
                    {
                        staged[i].Status.Action = "已回滚";
                        staged[i].Status.Detail = $"原子组 [{groupName}] 提交失败，已自动回滚至原版本";
                    }
                }
            }
        }
        finally
        {
            TryDeleteDirectorySilently(txDir);
            TryDeleteEmptyDirectorySilently(stagingRoot);
        }
    }

    private static void FinalizeReportSummary(UpdateSyncReport report, bool applyDownloads)
    {
        report.Success = report.FailedCount == 0 && !report.BlockedByRunningGame && !report.BlockedByMinVersion && !report.HasCorePluginFailure;
        if (report.BlockedByRunningGame)
        {
            report.SummaryMessage = "同步已拦截：检测到游戏进程 TheBazaar.exe 正在运行，请先关闭游戏后重试。";
        }
        else if (report.BlockedByMinVersion)
        {
            report.SummaryMessage = $"版本不兼容：客户端插件低于服务端最低要求 v{report.ClientManifest?.MinRequiredVersion}，已阻断启动。";
        }
        else if (!applyDownloads)
        {
            int pending = report.Items.Count(x => x.Action is "待更新" or "待下载");
            report.SummaryMessage = pending == 0
                ? $"检查完成：全部 {report.Items.Count} 项已是最新或受保护。"
                : $"检查完成：共 {report.Items.Count} 项，发现 {pending} 项可增量更新。";
        }
        else if (report.Success)
        {
            report.SummaryMessage = report.DownloadedCount == 0
                ? $"同步完成：本地 {report.SkippedUpToDateCount} 项均为最新或受保护，无需下载。"
                : $"同步完成：增量更新 {report.DownloadedCount} 项（{FormatBytes(report.TotalBytesDownloaded)}），跳过最新/受保护 {report.SkippedUpToDateCount} 项。";
        }
        else if (report.RolledBackAtomicGroups.Count > 0)
        {
            report.SummaryMessage = $"核心更新失败并已自动回滚（{string.Join(", ", report.RolledBackAtomicGroups)}）：成功 {report.DownloadedCount} 项，失败 {report.FailedCount} 项。";
        }
        else
        {
            report.SummaryMessage = $"同步部分失败：成功 {report.DownloadedCount} 项，跳过 {report.SkippedUpToDateCount} 项，失败 {report.FailedCount} 项。";
        }
    }

    /// <summary>
    /// 流式下载至随机 .tmp 文件并边写边算 SHA-256，校验通过后再原子替换目标文件。
    /// </summary>
    public Task<long> DownloadAndVerifyAtomicAsync(
        string url,
        string targetPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
        => DownloadAndVerifyAtomicAsync(url, targetPath, expectedSha256, MaxClientBinaryBytes, cancellationToken);

    public async Task<long> DownloadAndVerifyAtomicAsync(
        string url,
        string targetPath,
        string expectedSha256,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = $"{targetPath}.tmp_{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            long totalBytes = await DownloadAndVerifyToFileAsync(url, tmpPath, expectedSha256, maxBytes, cancellationToken);
            File.Move(tmpPath, targetPath, overwrite: true);
            return totalBytes;
        }
        catch
        {
            TryDeleteSilently(tmpPath);
            throw;
        }
    }

    private async Task<long> DownloadAndVerifyToFileAsync(
        string url,
        string destinationFilePath,
        string expectedSha256,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"下载失败 HTTP {(int)resp.StatusCode} ({url})");
            }

            if (maxBytes > 0 && resp.Content.Headers.ContentLength is long contentLen && contentLen > maxBytes)
            {
                throw new InvalidDataException($"下载内容体积超限（{FormatBytes(contentLen)} > {FormatBytes(maxBytes)}），已拒绝写入。");
            }

            long totalBytes = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += read;
                    if (maxBytes > 0 && totalBytes > maxBytes)
                    {
                        throw new InvalidDataException($"下载流超出最大允许体积 {FormatBytes(maxBytes)}，已中止写入。");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                }
                await fileStream.FlushAsync(cancellationToken);
            }

            string actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            string normalizedExpected = NormalizeSha256(expectedSha256);
            if (!string.IsNullOrEmpty(normalizedExpected) &&
                !string.Equals(actualSha, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 校验失败（期望 {normalizedExpected[..Math.Min(12, normalizedExpected.Length)]}，实际 {actualSha[..12]}），已拒绝写入。");
            }

            return totalBytes;
        }
        catch
        {
            TryDeleteSilently(destinationFilePath);
            throw;
        }
    }

    private static long WriteAndVerifyToFile(string destinationFilePath, byte[] bytes, string expectedSha256)
    {
        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string normalizedExpected = NormalizeSha256(expectedSha256);
        string actualSha = ComputeBytesSha256(bytes);
        if (!string.IsNullOrEmpty(normalizedExpected) &&
            !string.Equals(actualSha, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"内存负载 SHA-256 校验失败（期望 {normalizedExpected}，实际 {actualSha}）。");
        }

        File.WriteAllBytes(destinationFilePath, bytes);
        return bytes.Length;
    }

    private static long WriteAndVerifyAtomic(string targetPath, byte[] bytes, string expectedSha256)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = $"{targetPath}.tmp_{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            long written = WriteAndVerifyToFile(tmpPath, bytes, expectedSha256);
            File.Move(tmpPath, targetPath, overwrite: true);
            return written;
        }
        catch
        {
            TryDeleteSilently(tmpPath);
            throw;
        }
    }

    private async Task<(ClientUpdateManifest? Manifest, bool NetworkError, string? Warning)> FetchClientUpdateManifestAsync(
        string serverBase,
        CancellationToken cancellationToken)
    {
        string url = $"{serverBase}/api/client-update/manifest?channel=stable&hubVer={LauncherConstants.HubVersion}";
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, false, "当前服务端尚未开启 /api/client-update/manifest 端点，已跳过插件更新检查。");
            }

            if (!resp.IsSuccessStatusCode)
            {
                return (null, false, $"获取客户端更新清单失败（HTTP {(int)resp.StatusCode}）。");
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken);
            return (ParseClientUpdateManifest(json), false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, true, $"连接更新清单失败（网络不可达或超时）：{ex.Message}");
        }
    }

    private async Task<(RemoteModsManifest? Manifest, bool NetworkError, string? Warning)> FetchModsManifestAsync(
        string serverBase,
        CancellationToken cancellationToken)
    {
        string url = $"{serverBase}/api/mods/manifest";
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, false, "当前服务端尚未开启 /api/mods/manifest 端点，已跳过 Mod 清单检查。");
            }

            if (!resp.IsSuccessStatusCode)
            {
                return (null, false, $"获取 Mod 清单失败（HTTP {(int)resp.StatusCode}）。");
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken);
            return (ParseModsManifest(json), false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, true, $"连接 Mod 清单失败（网络不可达或超时）：{ex.Message}");
        }
    }

    private async Task<(LocalizationOverlayInfo? Overlay, bool NetworkError, string? Warning)> FetchLocalizationOverlayAsync(
        string serverBase,
        CancellationToken cancellationToken)
    {
        string[] endpoints =
        {
            $"{serverBase}/api/localization/overlay",
            $"{serverBase}/api/localization/zh-overlay?locale=zh-CN"
        };

        bool anyNetworkError = false;
        string? lastWarning = null;

        foreach (string url in endpoints)
        {
            try
            {
                using var resp = await _http.GetAsync(url, cancellationToken);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    lastWarning = $"拉取汉化外挂词典失败（HTTP {(int)resp.StatusCode}）。";
                    continue;
                }

                byte[] rawBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                if (rawBytes.Length == 0)
                {
                    continue;
                }

                // 校验为有效 JSON 并提取修订号与条目数（兼容 int 形态与 { total: 1416 } 对象形态及响应头）
                using JsonDocument doc = JsonDocument.Parse(rawBytes);
                JsonElement root = doc.RootElement;
                string rev = GetStringProp(root, "revision", "version", "generatedAt") ?? "latest";
                int entryCount = GetIntProp(root, "entryCount");
                if (entryCount <= 0 && TryGetObjectProp(root, out JsonElement ecObj, "entryCount"))
                {
                    entryCount = GetIntProp(ecObj, "total", "count");
                }
                if (entryCount <= 0 && resp.Headers.TryGetValues("X-Localization-Entries", out var hdrVals))
                {
                    int.TryParse(hdrVals.FirstOrDefault(), out entryCount);
                }
                string sha = ComputeBytesSha256(rawBytes);

                return (new LocalizationOverlayInfo
                {
                    Available = true,
                    Revision = rev,
                    Sha256 = sha,
                    EntryCount = entryCount,
                    RawBytes = rawBytes,
                    SourceEndpoint = url
                }, false, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                anyNetworkError = true;
                lastWarning = $"拉取汉化词典失败：{ex.Message}";
            }
        }

        if (anyNetworkError)
        {
            return (null, true, lastWarning);
        }

        return (null, false, lastWarning ?? "当前服务端尚未开启外挂汉化词典端点，已跳过词典同步。");
    }

    private static ClientUpdateManifest ParseClientUpdateManifest(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var files = new List<RemoteClientFileEntry>();
        if (TryGetArrayProp(root, out JsonElement filesArray, "files", "components", "artifacts", "plugins"))
        {
            foreach (JsonElement item in filesArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string? explicitTarget = GetStringProp(item, "targetPath", "relativeTargetPath");
                bool hasExplicitTarget = !string.IsNullOrWhiteSpace(explicitTarget);
                string relTarget = hasExplicitTarget
                    ? explicitTarget!
                    : (GetStringProp(item, "relativePath", "path", "fileName") ?? string.Empty);

                files.Add(new RemoteClientFileEntry
                {
                    Id = GetStringProp(item, "id", "name") ?? string.Empty,
                    Name = GetStringProp(item, "name", "displayName", "fileName", "id") ?? string.Empty,
                    Category = GetStringProp(item, "category", "type", "kind") ?? "plugin",
                    Version = GetStringProp(item, "version") ?? string.Empty,
                    FileVersion = GetStringProp(item, "fileVersion") ?? string.Empty,
                    RelativeTargetPath = relTarget,
                    HasExplicitTargetPath = hasExplicitTarget,
                    UpdatePolicy = NormalizeUpdatePolicy(GetStringProp(item, "updatePolicy", "policy")),
                    AtomicGroup = (GetStringProp(item, "atomicGroup", "group") ?? string.Empty).Trim(),
                    DownloadUrl = GetStringProp(item, "downloadUrl", "url", "href") ?? string.Empty,
                    Sha256 = GetStringProp(item, "sha256", "hash") ?? string.Empty,
                    SizeBytes = GetLongProp(item, "sizeBytes", "size", "bytes"),
                    Required = GetBoolProp(item, "required", defaultValue: true)
                });
            }
        }

        // 兼容扁平字段风格（如 launcher / bazaarLauncher / localizationOverlay）
        TryExtractFlatComponent(root, "launcher", "BazaarLauncher.dll", "plugin", @"BepInEx\plugins\BazaarLauncher.dll", files);
        TryExtractFlatComponent(root, "bazaarLauncher", "BazaarLauncher.dll", "plugin", @"BepInEx\plugins\BazaarLauncher.dll", files);
        TryExtractFlatComponent(root, "localizationOverlay", "custom_zh_overlay.json", "localization", LauncherConstants.ZhOverlayRelativePath, files);

        string launcherVer = GetStringProp(root, "launcherVersion", "pluginVersion", "version") ?? string.Empty;
        if (string.IsNullOrEmpty(launcherVer))
        {
            launcherVer = files.FirstOrDefault(f =>
                f.Name.Contains("BazaarLauncher", StringComparison.OrdinalIgnoreCase) ||
                f.RelativeTargetPath.Contains("BazaarLauncher", StringComparison.OrdinalIgnoreCase))?.Version ?? string.Empty;
        }

        return new ClientUpdateManifest
        {
            Channel = GetStringProp(root, "channel") ?? "stable",
            ServerVersion = GetStringProp(root, "serverVersion") ?? string.Empty,
            LauncherVersion = launcherVer,
            HubVersion = GetStringProp(root, "hubVersion") ?? string.Empty,
            LocalizationVersion = GetStringProp(root, "localizationVersion", "overlayRevision") ?? string.Empty,
            MinRequiredVersion = GetStringProp(root, "minRequiredVersion") ?? string.Empty,
            MinHubVersion = GetStringProp(root, "minHubVersion") ?? string.Empty,
            ReleaseNotes = GetStringProp(root, "releaseNotes", "notes") ?? string.Empty,
            Files = files
        };
    }

    private static void TryExtractFlatComponent(
        JsonElement root,
        string propName,
        string defaultName,
        string category,
        string defaultTarget,
        List<RemoteClientFileEntry> files)
    {
        if (!TryGetObjectProp(root, out JsonElement obj, propName)) return;
        string url = GetStringProp(obj, "downloadUrl", "url") ?? string.Empty;
        string sha = GetStringProp(obj, "sha256", "hash") ?? string.Empty;
        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(sha)) return;

        string? explicitTarget = GetStringProp(obj, "targetPath", "relativeTargetPath");
        string target = !string.IsNullOrWhiteSpace(explicitTarget)
            ? explicitTarget!
            : (GetStringProp(obj, "path") ?? defaultTarget);
        if (files.Any(f => string.Equals(f.RelativeTargetPath, target, StringComparison.OrdinalIgnoreCase))) return;

        files.Add(new RemoteClientFileEntry
        {
            Id = propName,
            Name = GetStringProp(obj, "name") ?? defaultName,
            Category = category,
            Version = GetStringProp(obj, "version") ?? string.Empty,
            RelativeTargetPath = target,
            HasExplicitTargetPath = true,
            UpdatePolicy = NormalizeUpdatePolicy(GetStringProp(obj, "updatePolicy", "policy")),
            AtomicGroup = (GetStringProp(obj, "atomicGroup", "group") ?? string.Empty).Trim(),
            DownloadUrl = url,
            Sha256 = sha,
            SizeBytes = GetLongProp(obj, "sizeBytes", "size", "bytes"),
            Required = true
        });
    }

    private static RemoteModsManifest ParseModsManifest(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var mods = new List<RemoteModEntry>();
        if (TryGetArrayProp(root, out JsonElement modsArr, "mods", "enabledMods", "packages"))
        {
            foreach (JsonElement m in modsArr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string modId = GetStringProp(m, "id", "modId") ?? "unknown";
                var modAssets = new List<RemoteModAssetEntry>();
                if (TryGetArrayProp(m, out JsonElement nestedAssets, "assets", "files"))
                {
                    foreach (JsonElement a in nestedAssets.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.Object)
                        {
                            modAssets.Add(ParseAssetElement(a, modId));
                        }
                    }
                }

                mods.Add(new RemoteModEntry
                {
                    Id = modId,
                    Name = GetStringProp(m, "displayNameZh", "name", "displayName") ?? modId,
                    Version = GetStringProp(m, "version") ?? "1.0.0",
                    Scope = GetStringProp(m, "scope") ?? "multiplayer-safe",
                    Author = GetStringProp(m, "author") ?? string.Empty,
                    Description = GetStringProp(m, "description", "descriptionZh") ?? string.Empty,
                    LocalizationHash = GetStringProp(m, "localizationHash") ?? string.Empty,
                    VisualCatalogHash = GetStringProp(m, "visualCatalogHash") ?? string.Empty,
                    Assets = modAssets
                });
            }
        }

        var topAssets = new List<RemoteModAssetEntry>();
        if (TryGetArrayProp(root, out JsonElement assetsArr, "assets"))
        {
            foreach (JsonElement a in assetsArr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object)
                {
                    topAssets.Add(ParseAssetElement(a, string.Empty));
                }
            }
        }

        return new RemoteModsManifest
        {
            ManifestHash = GetStringProp(root, "manifestHash", "hash", "etag") ?? string.Empty,
            Mods = mods,
            Assets = topAssets
        };
    }

    private static RemoteModAssetEntry ParseAssetElement(JsonElement el, string fallbackModId)
    {
        return new RemoteModAssetEntry
        {
            ModId = GetStringProp(el, "modId") ?? fallbackModId,
            LogicalPath = GetStringProp(el, "logicalPath", "path", "fileName", "name") ?? string.Empty,
            Sha256 = GetStringProp(el, "sha256", "hash") ?? string.Empty,
            DownloadUrl = GetStringProp(el, "downloadUrl", "url") ?? string.Empty,
            SizeBytes = GetLongProp(el, "bytes", "sizeBytes", "size"),
            Width = GetIntProp(el, "width"),
            Height = GetIntProp(el, "height")
        };
    }

    internal static string ResolveClientFileRelativePath(RemoteClientFileEntry entry)
    {
        string raw = entry.RelativeTargetPath.Trim().Replace('/', '\\').TrimStart('\\');

        // 若服务端明确下发了权威 targetPath / relativeTargetPath，直接采用相对游戏根目录路径
        if (entry.HasExplicitTargetPath && !string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        if (string.IsNullOrEmpty(raw))
        {
            string name = entry.Name.Trim();
            if (string.IsNullOrEmpty(name)) return string.Empty;

            if (RootLoaderFileNames.Contains(name) ||
                entry.Category.Equals("loader", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            if (entry.Category.Equals("localization", StringComparison.OrdinalIgnoreCase))
            {
                return LauncherConstants.ZhOverlayRelativePath;
            }

            return Path.Combine(LauncherConstants.PluginsRelativeDir, name);
        }

        // 兼容旧版未升级服务端：扁平文件名精确区分引导层（根目录）与插件层（BepInEx\plugins）
        if (!raw.Contains('\\'))
        {
            if (RootLoaderFileNames.Contains(raw) ||
                entry.Category.Equals("loader", StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }

            if (CorePluginFileNames.Contains(raw) ||
                entry.Category.Equals("plugin", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(LauncherConstants.PluginsRelativeDir, raw);
            }

            if (entry.Category.Equals("localization", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(@"BepInEx\config", raw);
            }

            return Path.Combine(LauncherConstants.PluginsRelativeDir, raw);
        }

        return raw;
    }

    internal static bool IsProtectedClientUserFile(string relTarget)
    {
        if (string.IsNullOrWhiteSpace(relTarget)) return false;
        string norm = relTarget.Trim().Replace('/', '\\').TrimStart('\\');

        if (norm.StartsWith(@"BepInEx\config\", StringComparison.OrdinalIgnoreCase) &&
            norm.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = Path.GetFileName(norm);
        if (string.Equals(fileName, "player-name.txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "game-dir.txt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "hub-settings.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "hub-presets.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (norm.StartsWith(LauncherConstants.BackupRootFolderName + @"\", StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith(LauncherConstants.StagingRootFolderName + @"\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsIgnoredDebugArtifact(string relTarget)
    {
        string ext = Path.GetExtension(relTarget);
        return !string.IsNullOrEmpty(ext) && IgnoredDebugExtensions.Contains(ext);
    }

    private static string NormalizeUpdatePolicy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "always";
        string s = raw.Trim();
        if (string.Equals(s, "ifMissing", StringComparison.OrdinalIgnoreCase)) return "ifMissing";
        if (string.Equals(s, "never", StringComparison.OrdinalIgnoreCase)) return "never";
        return "always";
    }

    private static string ResolveAtomicGroup(RemoteClientFileEntry entry, string relTarget)
    {
        if (!string.IsNullOrWhiteSpace(entry.AtomicGroup))
        {
            return entry.AtomicGroup.Trim();
        }

        string norm = relTarget.Replace('/', '\\').TrimStart('\\');
        if (norm.StartsWith(@"BepInEx\plugins\", StringComparison.OrdinalIgnoreCase) &&
            norm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return "core-plugins";
        }

        if (RootLoaderFileNames.Contains(norm) ||
            (norm.StartsWith(@"BepInEx\core\", StringComparison.OrdinalIgnoreCase) &&
             norm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            return "bepinex-loader";
        }

        return string.Empty;
    }

    private static bool IsCoreCriticalTask(RemoteClientFileEntry entry, string relTarget, string atomicGroup)
    {
        if (!string.IsNullOrEmpty(atomicGroup))
        {
            return true;
        }

        if (entry.Category.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
            entry.Category.Equals("loader", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relTarget.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               relTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildModAssetCacheRelativePath(RemoteModAssetEntry asset, string normalizedSha)
    {
        string ext = Path.GetExtension(asset.LogicalPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedModAssetExtensions.Contains(ext))
        {
            ext = ".png";
        }
        else
        {
            ext = ext.ToLowerInvariant();
        }

        // 若服务端提供了 SHA-256，必须严格为 64 位纯十六进制，彻底阻断路径穿越注入
        if (!string.IsNullOrWhiteSpace(normalizedSha))
        {
            if (!SafePathAndZipGuard.TryNormalizeSha256Hex(normalizedSha, out string cleanSha))
            {
                throw new InvalidOperationException($"Mod 资产 SHA-256 格式非法（已拦截）：{normalizedSha}");
            }

            return Path.Combine(LauncherConstants.ModCacheRelativeDir, "assets", cleanSha + ext);
        }

        string rawMod = string.IsNullOrWhiteSpace(asset.ModId) ? "common" : asset.ModId.Trim();
        if (!SafePathAndZipGuard.IsValidModId(rawMod))
        {
            throw new InvalidOperationException($"Mod 资产所属 modId 非法（已拦截）：{rawMod}");
        }
        string safeMod = rawMod;

        string safeFile = SanitizePathSegment(Path.GetFileName(asset.LogicalPath ?? string.Empty));
        if (string.IsNullOrEmpty(safeFile) || !AllowedModAssetExtensions.Contains(Path.GetExtension(safeFile)))
        {
            safeFile = "asset" + ext;
        }

        return Path.Combine(LauncherConstants.ModCacheRelativeDir, safeMod, safeFile);
    }

    /// <summary>
    /// 严格校验目标路径必须位于 rootDir 之内，彻底杜绝路径穿越与前缀兄弟目录伪造。
    /// </summary>
    public static string ResolveSafePath(string rootDir, string relativePath)
        => SafePathAndZipGuard.ResolveSafeChildPath(rootDir, relativePath);

    private static string ResolveAbsoluteUrl(string serverBase, string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        string trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{serverBase.TrimEnd('/')}/{trimmed.TrimStart('/')}";
    }

    private static void BackupPreviousPlugin(string gameDir, string existingPluginPath)
    {
        try
        {
            string prevDir = Path.Combine(gameDir, LauncherConstants.BackupRootFolderName, "plugins_prev");
            Directory.CreateDirectory(prevDir);
            string fileName = Path.GetFileName(existingPluginPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupFile = Path.Combine(prevDir, $"{fileName}.{stamp}.bak");
            File.Copy(existingPluginPath, backupFile, overwrite: true);
        }
        catch
        {
            // 备份旧版插件失败不应阻断更新
        }
    }

    private static string SanitizePathSegment(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (Array.IndexOf(invalid, c) >= 0 || (c == '.' && sb.Length == 0)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略临时文件清理异常
        }
    }

    private static void TryDeleteDirectorySilently(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath)) Directory.Delete(dirPath, recursive: true);
        }
        catch
        {
            // 忽略暂存目录清理异常
        }
    }

    private static void TryDeleteEmptyDirectorySilently(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
            {
                Directory.Delete(dirPath, recursive: false);
            }
        }
        catch
        {
            // 忽略空目录清理异常
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private static bool TryGetArrayProp(JsonElement root, out JsonElement array, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in root.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    {
                        array = p.Value;
                        return true;
                    }
                }
            }
        }
        array = default;
        return false;
    }

    private static bool TryGetObjectProp(JsonElement root, out JsonElement obj, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in root.EnumerateObject())
            {
                foreach (string n in names)
                {
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        obj = p.Value;
                        return true;
                    }
                }
            }
        }
        obj = default;
        return false;
    }

    private static string? GetStringProp(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            foreach (string n in names)
            {
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                    if (p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetRawText();
                }
            }
        }
        return null;
    }

    private static int GetIntProp(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            foreach (string n in names)
            {
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out int v))
                {
                    return v;
                }
            }
        }
        return 0;
    }

    private static long GetLongProp(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            foreach (string n in names)
            {
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt64(out long v))
                {
                    return v;
                }
            }
        }
        return 0;
    }

    private static bool GetBoolProp(JsonElement root, string name, bool defaultValue)
    {
        if (root.ValueKind != JsonValueKind.Object) return defaultValue;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Value.ValueKind == JsonValueKind.True) return true;
                if (p.Value.ValueKind == JsonValueKind.False) return false;
            }
        }
        return defaultValue;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private sealed class PendingSyncTask
    {
        public string Category { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string TargetRelativePath { get; init; } = string.Empty;
        public string FullTargetPath { get; init; } = string.Empty;
        public string LocalSha256 { get; init; } = string.Empty;
        public string RemoteSha256 { get; init; } = string.Empty;
        public string RemoteFileVersion { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public byte[]? InlineBytes { get; init; }
        public long SizeBytes { get; init; }
        public long MaxDownloadBytes { get; init; } = MaxClientBinaryBytes;
        public string UpdatePolicy { get; init; } = "always";
        public string AtomicGroup { get; init; } = string.Empty;
        public bool IsCoreCritical { get; init; }
        public bool IsProtectedUserFile { get; init; }
        public bool BackupOldPlugin { get; init; }
    }
}
