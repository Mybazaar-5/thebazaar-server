using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 客户端本体底层可逆性能调优服务。
/// 完整落地 wave1_client_perf_modkit.md 第 2.3 节全部 4 项本地配置与启动参数优化 + BazaarLauncher 性能配置：
///   1. TheBazaar_Data\boot.config（gc-max-time-slice、Native Gfx Jobs、job-worker-count、内存分配器块大小）
///   2. Unity 启动参数组合（-nolog / -gc-incremental / -window-mode borderless / -force-d3d11 / -high）
///   3. TheBazaar_Data\StreamingAssets\aa\settings.json（关闭启动期远程 Catalog 更新与异常刷盘）
///   4. BepInEx\config\BepInEx.cfg（关闭高开销控制台、精简磁盘日志级别与 UnityLogListening）
///   5. BepInEx\config\local.thebazaar.bazaarlauncher.cfg（[Performance] 前台帧率、后台降频、藏品懒加载、高并发特效降载）
///
/// 安全铁律：任何修改前自动将原始文件备份至 &lt;游戏目录&gt;\_BazaarHubBackups\original\（仅首次备份原版，绝不覆盖原始备份），
/// 并提供 RestoreOriginalFilesOnlyAsync / RestorePureOfficialAsync 一键逐字节还原！
/// </summary>
public sealed class PerformanceTunerService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetBackupRootDir(string gameDir) =>
        Path.Combine(gameDir, LauncherConstants.BackupRootFolderName);

    public static string GetOriginalBackupDir(string gameDir) =>
        Path.Combine(GetBackupRootDir(gameDir), LauncherConstants.OriginalBackupSubFolder);

    public static string GetBackupManifestPath(string gameDir) =>
        Path.Combine(GetBackupRootDir(gameDir), "backup-manifest.json");

    /// <summary>
    /// 读取当前游戏目录的原始备份清单。
    /// </summary>
    public BackupManifest LoadBackupManifest(string gameDir)
    {
        string path = GetBackupManifestPath(gameDir);
        if (!File.Exists(path))
        {
            return new BackupManifest();
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<BackupManifest>(json, ManifestJsonOptions) ?? new BackupManifest();
        }
        catch
        {
            return new BackupManifest();
        }
    }

    /// <summary>
    /// 一键应用性能调优选项（修改任何文件前均先确保首次原始备份已安全落地）。
    /// </summary>
    public Task<PerformanceOperationResult> ApplyAsync(
        string gameDir,
        PerformanceTuningOptions options,
        Action<string>? log = null)
    {
        var result = new PerformanceOperationResult
        {
            LaunchArgumentsPreview = BuildFullLaunchArgsPreview(options)
        };

        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            result.Success = false;
            result.Summary = "未定位有效的游戏目录，无法应用性能调优。";
            return Task.FromResult(result);
        }

        BackupManifest manifest = LoadBackupManifest(gameDir);
        bool manifestChanged = false;

        // 1. 优化 TheBazaar_Data\boot.config
        if (options.OptimizeBootConfig)
        {
            string bootPath = Path.Combine(gameDir, LauncherConstants.BootConfigRelativePath);
            if (File.Exists(bootPath))
            {
                if (EnsureOriginalBackup(gameDir, LauncherConstants.BootConfigRelativePath, manifest, log))
                {
                    manifestChanged = true;
                    result.BackedUpFiles.Add(LauncherConstants.BootConfigRelativePath);
                }

                ApplyBootConfigOptimization(bootPath, options);
                result.AppliedItems.Add("boot.config（增量 GC 切片 / Job 线程 / 原生内存与渲染管线）");
                log?.Invoke("[性能调优] 已更新 TheBazaar_Data\\boot.config。");
            }
            else
            {
                result.Warnings.Add("未找到 TheBazaar_Data\\boot.config，已跳过此项。");
            }
        }

        // 2. Unity 启动参数组合（记录在启动器偏好中，启动 TheBazaar.exe 时自动传入）
        if (!string.IsNullOrWhiteSpace(result.LaunchArgumentsPreview))
        {
            result.AppliedItems.Add($"Unity 启动参数（{result.LaunchArgumentsPreview}）");
            log?.Invoke($"[性能调优] Unity 启动参数已生效：{result.LaunchArgumentsPreview}");
        }

        // 3. 优化 TheBazaar_Data\StreamingAssets\aa\settings.json
        if (options.OptimizeAddressablesSettings)
        {
            string aaPath = Path.Combine(gameDir, LauncherConstants.AddressablesSettingsRelativePath);
            if (File.Exists(aaPath))
            {
                if (EnsureOriginalBackup(gameDir, LauncherConstants.AddressablesSettingsRelativePath, manifest, log))
                {
                    manifestChanged = true;
                    result.BackedUpFiles.Add(LauncherConstants.AddressablesSettingsRelativePath);
                }

                ApplyAddressablesOptimization(aaPath);
                result.AppliedItems.Add("Addressables settings.json（关闭启动期远程 Catalog 探测与异常刷盘）");
                log?.Invoke("[性能调优] 已更新 TheBazaar_Data\\StreamingAssets\\aa\\settings.json。");
            }
            else
            {
                result.Warnings.Add("未找到 TheBazaar_Data\\StreamingAssets\\aa\\settings.json，已跳过此项。");
            }
        }

        // 4. 优化 BepInEx\config\BepInEx.cfg
        if (options.OptimizeBepInExLogging)
        {
            string bepCfgPath = Path.Combine(gameDir, LauncherConstants.BepInExCfgRelativePath);
            if (EnsureOriginalBackup(gameDir, LauncherConstants.BepInExCfgRelativePath, manifest, log))
            {
                manifestChanged = true;
                result.BackedUpFiles.Add(LauncherConstants.BepInExCfgRelativePath);
            }

            ApplyBepInExLoggingOptimization(bepCfgPath);
            result.AppliedItems.Add("BepInEx.cfg（关闭高开销控制台、精简磁盘日志为 Fatal/Error、关闭 UnityLogListening）");
            log?.Invoke("[性能调优] 已更新 BepInEx\\config\\BepInEx.cfg。");
        }

        // 5. 优化 BazaarLauncher [Performance] 配置项
        if (options.ApplyLauncherPerfSettings)
        {
            string launcherCfgPath = Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath);
            if (EnsureOriginalBackup(gameDir, LauncherConstants.LauncherCfgRelativePath, manifest, log))
            {
                manifestChanged = true;
                result.BackedUpFiles.Add(LauncherConstants.LauncherCfgRelativePath);
            }

            ApplyLauncherPerformanceConfig(launcherCfgPath, options);
            string fpsDesc = options.MaxForegroundFps > 0 ? $"{options.MaxForegroundFps} FPS" : "不限帧";
            string bgDesc = options.BackgroundFps > 0 ? $"{options.BackgroundFps} FPS" : "不降频";
            result.AppliedItems.Add(
                $"BazaarLauncher 性能配置（前台 {fpsDesc} / 后台 {bgDesc} / 藏品懒加载={(options.LazyLoadCosmetics ? "开" : "关")} / 高并发特效降载={(options.ReduceHighConcurrencyVfx ? "开" : "关")}）");
            log?.Invoke("[性能调优] 已更新 local.thebazaar.bazaarlauncher.cfg 的 [Performance] 段落。");
        }

        if (manifestChanged)
        {
            SaveBackupManifest(gameDir, manifest);
        }

        result.Success = true;
        result.Summary = result.AppliedItems.Count > 0
            ? $"已成功应用 {result.AppliedItems.Count} 项性能调优，原始备份保护完好（共 {manifest.Files.Count} 份基线文件）。"
            : "当前未勾选任何性能调优项，未改动配置文件。";

        return Task.FromResult(result);
    }

    /// <summary>
    /// 仅逐字节还原 _BazaarHubBackups\original\ 中备份的全部原始配置文件（保证 round-trip 逐字节 100% 一致）。
    /// </summary>
    public Task<PerformanceOperationResult> RestoreOriginalFilesOnlyAsync(
        string gameDir,
        Action<string>? log = null)
    {
        var result = new PerformanceOperationResult();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            result.Success = false;
            result.Summary = "未定位有效的游戏目录，无法执行还原。";
            return Task.FromResult(result);
        }

        BackupManifest manifest = LoadBackupManifest(gameDir);
        string originalRoot = GetOriginalBackupDir(gameDir);

        foreach (OriginalFileBackupRecord record in manifest.Files.Values)
        {
            string relPath = NormalizeRelativePath(record.RelativePath);
            string targetPath = UpdateSyncService.ResolveSafePath(gameDir, relPath);

            if (record.ExistedOriginally)
            {
                string backupPath = UpdateSyncService.ResolveSafePath(originalRoot, relPath);
                if (!File.Exists(backupPath))
                {
                    result.Warnings.Add($"原始备份文件缺失：{relPath}");
                    continue;
                }

                string? targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                AtomicFileWriter.CopyFileAtomic(backupPath, targetPath);
                result.RestoredFiles.Add(relPath);
                log?.Invoke($"[还原] 已逐字节恢复原版文件：{relPath}");
            }
            else
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    result.RestoredFiles.Add($"{relPath}（原版不存在，已移除）");
                    log?.Invoke($"[还原] 已移除调优新建文件：{relPath}");
                }
            }
        }

        result.Success = true;
        result.Summary = result.RestoredFiles.Count > 0
            ? $"已从原始基线备份逐字节恢复 {result.RestoredFiles.Count} 份配置文件。"
            : "当前尚未检测到被修改的配置文件备份，所有文件均保持原状。";

        return Task.FromResult(result);
    }

    /// <summary>
    /// 一键还原纯净官方原版：
    /// 1. 从 _BazaarHubBackups\original\ 逐字节恢复全部被调优过的原始文件（boot.config、aa/settings.json、BepInEx.cfg 等）；
    /// 2. 可选停用自建服插件（将 local.thebazaar.bazaarlauncher.cfg 的 [General] Enabled 设为 false）。
    /// </summary>
    public async Task<PerformanceOperationResult> RestorePureOfficialAsync(
        string gameDir,
        bool disableBazaarLauncher = true,
        Action<string>? log = null)
    {
        PerformanceOperationResult result = await RestoreOriginalFilesOnlyAsync(gameDir, log);
        if (!result.Success)
        {
            return result;
        }

        if (disableBazaarLauncher)
        {
            string launcherCfgPath = Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath);
            if (File.Exists(launcherCfgPath))
            {
                IniDocument doc = IniDocument.LoadOrEmpty(launcherCfgPath);
                doc.SetValue("General", "Enabled", "false");
                doc.SaveAtomic(launcherCfgPath);
                result.AppliedItems.Add("已将 BazaarLauncher [General] Enabled 设为 false（停用自建服补丁）");
                log?.Invoke("[还原] 已停用 BazaarLauncher（Enabled = false），恢复纯净官方原版行为。");
            }
        }

        result.Summary = disableBazaarLauncher
            ? $"已一键还原纯净官方原版：恢复 {result.RestoredFiles.Count} 份原始配置并停用自建服补丁（Enabled=false）。"
            : result.Summary;

        return result;
    }

    /// <summary>
    /// 组装包含高优先级语义的启动参数预览文本。
    /// </summary>
    public static string BuildFullLaunchArgsPreview(PerformanceTuningOptions options)
    {
        string unityArgs = options.BuildUnityLaunchArguments();
        if (options.ArgHighProcessPriority)
        {
            return string.IsNullOrWhiteSpace(unityArgs) ? "-high" : $"-high {unityArgs}";
        }

        return unityArgs;
    }

    /// <summary>
    /// 仅首次备份原版文件（若已在原始备份清单中登记过则绝不覆盖，保护最纯净原版基线）。
    /// </summary>
    private static bool EnsureOriginalBackup(
        string gameDir,
        string relativePath,
        BackupManifest manifest,
        Action<string>? log)
    {
        string normalizedRel = NormalizeRelativePath(relativePath);
        if (manifest.Files.TryGetValue(normalizedRel, out OriginalFileBackupRecord? existing))
        {
            if (!existing.ExistedOriginally)
            {
                return false;
            }

            string existingBackupPath = UpdateSyncService.ResolveSafePath(GetOriginalBackupDir(gameDir), normalizedRel);
            if (File.Exists(existingBackupPath))
            {
                // 已存在首次原始备份，严禁覆写！
                return false;
            }
        }

        string sourcePath = UpdateSyncService.ResolveSafePath(gameDir, normalizedRel);
        if (!File.Exists(sourcePath))
        {
            manifest.Files[normalizedRel] = new OriginalFileBackupRecord
            {
                RelativePath = normalizedRel,
                ExistedOriginally = false,
                OriginalSha256 = string.Empty,
                OriginalBytes = 0,
                BackedUpAtUtc = DateTimeOffset.UtcNow
            };
            log?.Invoke($"[备份] 记录原版基线：{normalizedRel}（原版不存在，还原时将自动清理）");
            return true;
        }

        string backupPath = UpdateSyncService.ResolveSafePath(GetOriginalBackupDir(gameDir), normalizedRel);
        string? backupParent = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(backupParent))
        {
            Directory.CreateDirectory(backupParent);
        }

        File.Copy(sourcePath, backupPath, overwrite: false);
        var info = new FileInfo(backupPath);
        string sha = UpdateSyncService.ComputeFileSha256(backupPath);

        manifest.Files[normalizedRel] = new OriginalFileBackupRecord
        {
            RelativePath = normalizedRel,
            ExistedOriginally = true,
            OriginalSha256 = sha,
            OriginalBytes = info.Length,
            BackedUpAtUtc = DateTimeOffset.UtcNow
        };

        log?.Invoke($"[备份] 首次原版基线已保护：{normalizedRel} (SHA-256: {sha[..12]}...)");
        return true;
    }

    private static void SaveBackupManifest(string gameDir, BackupManifest manifest)
    {
        string manifestPath = GetBackupManifestPath(gameDir);
        string? dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        AtomicFileWriter.WriteAllTextAtomic(manifestPath, json, Utf8NoBom);
    }

    private static void ApplyBootConfigOptimization(string bootConfigPath, PerformanceTuningOptions options)
    {
        string raw = File.ReadAllText(bootConfigPath, Encoding.UTF8);
        string lineEnding = raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool trailingNewLine = raw.EndsWith('\n');

        var lines = new List<string>();
        if (raw.Length > 0)
        {
            string[] split = raw.Replace("\r\n", "\n").Split('\n');
            int count = trailingNewLine ? split.Length - 1 : split.Length;
            for (int i = 0; i < count; i++)
            {
                lines.Add(split[i]);
            }
        }

        var updates = new List<(string Key, string Value)>
        {
            ("gc-max-time-slice", Math.Clamp(options.GcMaxTimeSliceMs, 1, 10).ToString()),
            ("job-worker-count", Math.Clamp(options.JobWorkerCount, 2, 16).ToString())
        };

        if (options.EnableNativeGfxJobs)
        {
            updates.Add(("gfx-enable-native-gfx-jobs", "1"));
        }

        if (options.TuneNativeMemoryAllocators)
        {
            updates.Add(("memorysetup-main-allocator-block-size", "16777216"));
            updates.Add(("memorysetup-thread-allocator-block-size", "8388608"));
            updates.Add(("memorysetup-temp-allocator-size-main", "8388608"));
        }

        foreach ((string key, string value) in updates)
        {
            int idx = lines.FindIndex(l =>
            {
                int eq = l.IndexOf('=');
                return eq > 0 && string.Equals(l[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase);
            });

            string formatted = $"{key}={value}";
            if (idx >= 0)
            {
                lines[idx] = formatted;
            }
            else
            {
                lines.Add(formatted);
            }
        }

        string serialized = string.Join(lineEnding, lines) + (trailingNewLine ? lineEnding : string.Empty);
        AtomicFileWriter.WriteAllTextAtomic(bootConfigPath, serialized, Utf8NoBom);
    }

    private static void ApplyAddressablesOptimization(string settingsJsonPath)
    {
        string raw = File.ReadAllText(settingsJsonPath, Encoding.UTF8);
        JsonNode? root = JsonNode.Parse(raw);
        if (root is not JsonObject obj)
        {
            throw new InvalidDataException("Addressables settings.json 根节点不是合法 JSON 对象。");
        }

        obj["m_DisableCatalogUpdateOnStart"] = true;
        obj["m_LogResourceManagerExceptions"] = false;

        string updated = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        AtomicFileWriter.WriteAllTextAtomic(settingsJsonPath, updated, Utf8NoBom);
    }

    private static void ApplyBepInExLoggingOptimization(string bepInExCfgPath)
    {
        IniDocument doc = IniDocument.LoadOrEmpty(bepInExCfgPath);
        doc.SetValue("Logging.Console", "Enabled", "false", new[]
        {
            "## 关闭高开销控制台窗口，减少同步终端渲染对 Unity 主线程的阻塞。"
        });
        doc.SetValue("Logging.Disk", "LogLevels", "Fatal, Error", new[]
        {
            "## 生产游玩模式仅记录严重错误，降低同步磁盘 I/O。"
        });
        doc.SetValue("Logging", "UnityLogListening", "false", new[]
        {
            "## 停止拦截全部 Unity Debug.Log 转发给 BepInEx，显著降低主线程字符串拼接开销。"
        });
        doc.SaveAtomic(bepInExCfgPath);
    }

    private static void ApplyLauncherPerformanceConfig(string launcherCfgPath, PerformanceTuningOptions options)
    {
        IniDocument doc = File.Exists(launcherCfgPath)
            ? IniDocument.LoadOrEmpty(launcherCfgPath)
            : LauncherConfigService.CreateDefaultLauncherIni();

        int fgFps = options.MaxForegroundFps is 0 or 60 or 90 or 120
            ? options.MaxForegroundFps
            : Math.Clamp(options.MaxForegroundFps, 0, 240);
        int bgFps = Math.Clamp(options.BackgroundFps, 0, 120);

        doc.SetValue("Performance", "MaxForegroundFps", fgFps.ToString(), new[]
        {
            "## 前台游玩帧率上限（建议 60 省电稳帧；支持 60/90/120，0 = 不额外限制跟随游戏原版）。",
            "# Setting type: Int32",
            "# Default value: 60"
        });
        doc.SetValue("Performance", "BackgroundFps", bgFps.ToString(), new[]
        {
            "## 游戏窗口失去焦点切到后台时的降频帧率（默认 20 FPS 释放 GPU，0 = 不降频）。",
            "# Setting type: Int32",
            "# Default value: 20"
        });
        doc.SetValue("Performance", "LazyLoadCosmetics", options.LazyLoadCosmetics ? "true" : "false", new[]
        {
            "## 按需懒加载藏品：启动时仅加载当前穿戴装扮，跳过未穿戴 3D 藏品全量预加载。",
            "# Setting type: Boolean",
            "# Default value: true"
        });
        doc.SetValue("Performance", "ReduceHighConcurrencyVfx", options.ReduceHighConcurrencyVfx ? "true" : "false", new[]
        {
            "## 高并发战斗特效降载：高倍速或多触发流派战斗时限制并发粒子数量，提升 1% Low 帧率。",
            "# Setting type: Boolean",
            "# Default value: false"
        });

        doc.SaveAtomic(launcherCfgPath);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('/', '\\').Trim().TrimStart('\\');
}
