using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 客户端增量热更新与 Mod / 汉化同步服务。
/// 对接服务端：
/// - GET /api/client-update/manifest
/// - GET /api/mods/manifest
/// - GET /api/mods/assets/{sha256}
/// - GET /api/localization/overlay（自动回退兼容 /api/localization/zh-overlay?locale=zh-CN）
/// 所有落盘写入严格遵循「先写 .tmp → 流式验证 SHA-256 → 原子替换」并支持断网优雅降级。
/// </summary>
public sealed class UpdateSyncService : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
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
    /// 检查更新或执行全量增量同步。
    /// </summary>
    /// <param name="gameDir">游戏根目录。</param>
    /// <param name="serverBase">当前选定的服务器基址。</param>
    /// <param name="applyDownloads">true = 下载并替换差异文件；false = 仅对比检查差异不落盘。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="log">实时日志回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<UpdateSyncReport> SyncAsync(
        string gameDir,
        string serverBase,
        bool applyDownloads = true,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
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

        // 收集客户端更新文件（插件 DLL 等）
        if (clientManifest != null)
        {
            foreach (RemoteClientFileEntry file in clientManifest.Files)
            {
                string relTarget = ResolveClientFileRelativePath(file);
                if (string.IsNullOrWhiteSpace(relTarget)) continue;

                string fullTarget = ResolveSafePath(gameDir, relTarget);
                string localSha = ComputeFileSha256(fullTarget);
                string remoteSha = NormalizeSha256(file.Sha256);
                string resolvedUrl = ResolveAbsoluteUrl(normalizedBase, file.DownloadUrl);

                tasks.Add(new PendingSyncTask
                {
                    Category = file.Category.Equals("localization", StringComparison.OrdinalIgnoreCase) ? "汉化词典" : "核心插件",
                    DisplayName = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(relTarget) : file.Name,
                    TargetRelativePath = relTarget,
                    FullTargetPath = fullTarget,
                    LocalSha256 = localSha,
                    RemoteSha256 = remoteSha,
                    DownloadUrl = resolvedUrl,
                    SizeBytes = file.SizeBytes,
                    BackupOldPlugin = relTarget.StartsWith(LauncherConstants.PluginsRelativeDir, StringComparison.OrdinalIgnoreCase)
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
                SizeBytes = overlayInfo.RawBytes.Length
            });
        }
        else if (!string.IsNullOrWhiteSpace(clientManifest?.LocalizationVersion))
        {
            report.LocalizationRevision = clientManifest.LocalizationVersion;
        }

        // 收集 Mod 资产（去重按 SHA-256 或目标路径）
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
                string sha = NormalizeSha256(asset.Sha256);
                string relAsset = BuildModAssetCacheRelativePath(asset, sha);
                if (!seenAssetTargets.Add(relAsset)) continue;

                string fullTarget = ResolveSafePath(gameDir, relAsset);
                string localSha = ComputeFileSha256(fullTarget);
                string url = !string.IsNullOrWhiteSpace(asset.DownloadUrl)
                    ? ResolveAbsoluteUrl(normalizedBase, asset.DownloadUrl)
                    : (!string.IsNullOrEmpty(sha) ? $"{normalizedBase}/api/mods/assets/{sha}" : string.Empty);

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
                    SizeBytes = asset.SizeBytes
                });
            }
        }

        // 执行逐项比对与增量原子更新
        int total = tasks.Count;
        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingSyncTask item = tasks[i];
            int pct = 40 + (int)((i + 1) * 55.0 / Math.Max(1, total));

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

            bool upToDate = !string.IsNullOrEmpty(item.RemoteSha256) &&
                            string.Equals(item.LocalSha256, item.RemoteSha256, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(item.FullTargetPath);

            if (upToDate)
            {
                status.Action = "已是最新";
                status.Detail = "SHA-256 一致，跳过下载";
                report.SkippedUpToDateCount++;
                continue;
            }

            if (!applyDownloads)
            {
                status.Action = File.Exists(item.FullTargetPath) ? "待更新" : "待下载";
                status.Detail = string.IsNullOrEmpty(item.LocalSha256) ? "本地缺失" : "本地哈希与服务端不同";
                continue;
            }

            progress?.Report(new SyncProgress
            {
                Percent = Math.Clamp(pct, 40, 96),
                Stage = "增量同步",
                Message = $"正在同步 ({i + 1}/{total})：{item.DisplayName}"
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
                status.Action = "失败";
                status.Detail = ex.Message;
                report.Errors.Add($"{item.DisplayName}: {ex.Message}");
                log?.Invoke($"[错误] 同步失败 {item.DisplayName}：{ex.Message}");
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

        report.Success = report.FailedCount == 0;
        if (!applyDownloads)
        {
            int pending = report.Items.Count(x => x.Action is "待更新" or "待下载");
            report.SummaryMessage = pending == 0
                ? $"检查完成：全部 {report.Items.Count} 项已是最新版本。"
                : $"检查完成：共 {report.Items.Count} 项，发现 {pending} 项可增量更新。";
        }
        else if (report.FailedCount == 0)
        {
            report.SummaryMessage = report.DownloadedCount == 0
                ? $"同步完成：本地 {report.SkippedUpToDateCount} 项均为最新，无需下载。"
                : $"同步完成：增量更新 {report.DownloadedCount} 项（{FormatBytes(report.TotalBytesDownloaded)}），跳过最新 {report.SkippedUpToDateCount} 项。";
        }
        else
        {
            report.SummaryMessage = $"同步部分失败：成功 {report.DownloadedCount} 项，跳过 {report.SkippedUpToDateCount} 项，失败 {report.FailedCount} 项。";
        }

        progress?.Report(new SyncProgress { Percent = 100, Stage = "完成", Message = report.SummaryMessage });
        log?.Invoke($"[同步] {report.SummaryMessage}");
        return report;
    }

    /// <summary>
    /// 流式下载至 .tmp 文件并边写边算 SHA-256，校验通过后再原子替换目标文件。
    /// </summary>
    public async Task<long> DownloadAndVerifyAtomicAsync(
        string url,
        string targetPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = targetPath + ".tmp";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"下载失败 HTTP {(int)resp.StatusCode} ({url})");
            }

            long totalBytes = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    totalBytes += read;
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

            File.Move(tmpPath, targetPath, overwrite: true);
            return totalBytes;
        }
        catch
        {
            TryDeleteSilently(tmpPath);
            throw;
        }
    }

    private static long WriteAndVerifyAtomic(string targetPath, byte[] bytes, string expectedSha256)
    {
        string? dir = Path.GetDirectoryName(targetPath);
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

        string tmpPath = targetPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, bytes);
            File.Move(tmpPath, targetPath, overwrite: true);
            return bytes.Length;
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
                files.Add(new RemoteClientFileEntry
                {
                    Id = GetStringProp(item, "id", "name") ?? string.Empty,
                    Name = GetStringProp(item, "name", "displayName", "fileName", "id") ?? string.Empty,
                    Category = GetStringProp(item, "category", "type", "kind") ?? "plugin",
                    Version = GetStringProp(item, "version") ?? string.Empty,
                    RelativeTargetPath = GetStringProp(item, "relativeTargetPath", "targetPath", "relativePath", "path", "fileName") ?? string.Empty,
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
            LauncherVersion = launcherVer,
            HubVersion = GetStringProp(root, "hubVersion") ?? string.Empty,
            LocalizationVersion = GetStringProp(root, "localizationVersion", "overlayRevision") ?? string.Empty,
            MinRequiredVersion = GetStringProp(root, "minRequiredVersion") ?? string.Empty,
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

        string target = GetStringProp(obj, "relativeTargetPath", "targetPath", "path") ?? defaultTarget;
        if (files.Any(f => string.Equals(f.RelativeTargetPath, target, StringComparison.OrdinalIgnoreCase))) return;

        files.Add(new RemoteClientFileEntry
        {
            Id = propName,
            Name = GetStringProp(obj, "name") ?? defaultName,
            Category = category,
            Version = GetStringProp(obj, "version") ?? string.Empty,
            RelativeTargetPath = target,
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

    private static string ResolveClientFileRelativePath(RemoteClientFileEntry entry)
    {
        string raw = entry.RelativeTargetPath.Trim().Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrEmpty(raw))
        {
            string name = entry.Name.Trim();
            if (string.IsNullOrEmpty(name)) return string.Empty;
            raw = entry.Category.Equals("localization", StringComparison.OrdinalIgnoreCase)
                ? LauncherConstants.ZhOverlayRelativePath
                : Path.Combine(LauncherConstants.PluginsRelativeDir, name);
        }
        else if (!raw.Contains('\\'))
        {
            raw = entry.Category.Equals("localization", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(@"BepInEx\config", raw)
                : Path.Combine(LauncherConstants.PluginsRelativeDir, raw);
        }

        return raw;
    }

    private static string BuildModAssetCacheRelativePath(RemoteModAssetEntry asset, string normalizedSha)
    {
        string ext = Path.GetExtension(asset.LogicalPath);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 8)
        {
            ext = ".png";
        }

        if (!string.IsNullOrWhiteSpace(normalizedSha) && normalizedSha.Length >= 16)
        {
            // 按内容寻址落盘在 BepInEx\config\BazaarModCache\assets\<sha256><ext>，天然去重防冲突
            return Path.Combine(LauncherConstants.ModCacheRelativeDir, "assets", normalizedSha + ext.ToLowerInvariant());
        }

        string safeMod = SanitizePathSegment(string.IsNullOrWhiteSpace(asset.ModId) ? "common" : asset.ModId);
        string safeFile = SanitizePathSegment(Path.GetFileName(asset.LogicalPath));
        if (string.IsNullOrEmpty(safeFile)) safeFile = "asset" + ext;
        return Path.Combine(LauncherConstants.ModCacheRelativeDir, safeMod, safeFile);
    }

    /// <summary>
    /// 严格校验目标路径必须位于 rootDir 之内，彻底杜绝路径穿越。
    /// </summary>
    public static string ResolveSafePath(string rootDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"非法的相对目标路径：{relativePath}");
        }

        string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"检测到路径穿越风险，已拦截：{relativePath}");
        }

        return candidate;
    }

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
            if (Array.IndexOf(invalid, c) >= 0 || c == '.' && sb.Length == 0) continue;
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
        public string DownloadUrl { get; init; } = string.Empty;
        public byte[]? InlineBytes { get; init; }
        public long SizeBytes { get; init; }
        public bool BackupOldPlugin { get; init; }
    }
}
