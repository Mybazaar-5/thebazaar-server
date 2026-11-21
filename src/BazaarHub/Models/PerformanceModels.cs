namespace BazaarHub.Models;

/// <summary>
/// 客户端本体底层可逆性能调优选项集合。
/// 完整覆盖 wave1_client_perf_modkit.md 第 2.3 节 4 项配置与启动参数 + BazaarLauncher 运行时性能项。
/// </summary>
public sealed class PerformanceTuningOptions
{
    // 1. TheBazaar_Data\boot.config 底层引擎参数
    public bool OptimizeBootConfig { get; set; } = true;
    public bool EnableNativeGfxJobs { get; set; } = true;
    public int GcMaxTimeSliceMs { get; set; } = 2;
    public int JobWorkerCount { get; set; } = DefaultRecommendedJobWorkerCount();
    public bool TuneNativeMemoryAllocators { get; set; } = true;

    // 2. Unity 启动参数组合
    public bool ArgNoLog { get; set; } = true;
    public bool ArgGcIncremental { get; set; } = true;
    public bool ArgWindowModeBorderless { get; set; } = false;
    public bool ArgForceD3D11 { get; set; } = false;
    public bool ArgHighProcessPriority { get; set; } = true;
    public string ExtraLaunchArguments { get; set; } = string.Empty;

    // 3. TheBazaar_Data\StreamingAssets\aa\settings.json（Addressables 冷启动优化）
    public bool OptimizeAddressablesSettings { get; set; } = true;

    // 4. BepInEx\config\BepInEx.cfg 日志链路瘦身
    public bool OptimizeBepInExLogging { get; set; } = true;

    // 5. BazaarLauncher 性能配置项（[Performance] 段）
    public bool ApplyLauncherPerfSettings { get; set; } = true;
    public int MaxForegroundFps { get; set; } = 60;
    public int BackgroundFps { get; set; } = 20;
    public bool LazyLoadCosmetics { get; set; } = true;
    public bool ReduceHighConcurrencyVfx { get; set; } = false;

    /// <summary>
    /// 按审计报告推荐计算 Job 工作线程数：Clamp(逻辑/物理核心数 - 1, 4, 8)。
    /// </summary>
    public static int DefaultRecommendedJobWorkerCount()
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        int suggested = Math.Max(1, cores - 1);
        return Math.Clamp(suggested, 4, 8);
    }

    /// <summary>
    /// 均衡流畅推荐预设（兼顾稳定性、帧率平滑与启动速度）。
    /// </summary>
    public static PerformanceTuningOptions CreateRecommended() => new()
    {
        OptimizeBootConfig = true,
        EnableNativeGfxJobs = true,
        GcMaxTimeSliceMs = 2,
        JobWorkerCount = DefaultRecommendedJobWorkerCount(),
        TuneNativeMemoryAllocators = true,
        ArgNoLog = true,
        ArgGcIncremental = true,
        ArgWindowModeBorderless = false,
        ArgForceD3D11 = false,
        ArgHighProcessPriority = true,
        ExtraLaunchArguments = string.Empty,
        OptimizeAddressablesSettings = true,
        OptimizeBepInExLogging = true,
        ApplyLauncherPerfSettings = true,
        MaxForegroundFps = 60,
        BackgroundFps = 20,
        LazyLoadCosmetics = true,
        ReduceHighConcurrencyVfx = false
    };

    /// <summary>
    /// 极致低配/核显减负预设（最大程度压降 GPU/CPU 负担）。
    /// </summary>
    public static PerformanceTuningOptions CreateExtremeLowSpec() => new()
    {
        OptimizeBootConfig = true,
        EnableNativeGfxJobs = true,
        GcMaxTimeSliceMs = 2,
        JobWorkerCount = DefaultRecommendedJobWorkerCount(),
        TuneNativeMemoryAllocators = true,
        ArgNoLog = true,
        ArgGcIncremental = true,
        ArgWindowModeBorderless = true,
        ArgForceD3D11 = true,
        ArgHighProcessPriority = true,
        ExtraLaunchArguments = string.Empty,
        OptimizeAddressablesSettings = true,
        OptimizeBepInExLogging = true,
        ApplyLauncherPerfSettings = true,
        MaxForegroundFps = 60,
        BackgroundFps = 15,
        LazyLoadCosmetics = true,
        ReduceHighConcurrencyVfx = true
    };

    /// <summary>
    /// 纯净官方默认（全部关闭）。
    /// </summary>
    public static PerformanceTuningOptions CreatePureOfficialDefault() => new()
    {
        OptimizeBootConfig = false,
        EnableNativeGfxJobs = false,
        GcMaxTimeSliceMs = 3,
        JobWorkerCount = DefaultRecommendedJobWorkerCount(),
        TuneNativeMemoryAllocators = false,
        ArgNoLog = false,
        ArgGcIncremental = false,
        ArgWindowModeBorderless = false,
        ArgForceD3D11 = false,
        ArgHighProcessPriority = false,
        ExtraLaunchArguments = string.Empty,
        OptimizeAddressablesSettings = false,
        OptimizeBepInExLogging = false,
        ApplyLauncherPerfSettings = false,
        MaxForegroundFps = 0,
        BackgroundFps = 0,
        LazyLoadCosmetics = false,
        ReduceHighConcurrencyVfx = false
    };

    /// <summary>
    /// 组装最终传递给 TheBazaar.exe 的 Unity 命令行参数。
    /// </summary>
    public string BuildUnityLaunchArguments()
    {
        var args = new List<string>();
        if (ArgNoLog) args.Add("-nolog");
        if (ArgGcIncremental) args.Add("-gc-incremental");
        if (ArgWindowModeBorderless) args.Add("-window-mode borderless");
        if (ArgForceD3D11) args.Add("-force-d3d11");
        if (!string.IsNullOrWhiteSpace(ExtraLaunchArguments))
        {
            args.Add(ExtraLaunchArguments.Trim());
        }

        return string.Join(" ", args);
    }
}

/// <summary>
/// 原始文件首次备份记录（存放在 _BazaarHubBackups\backup-manifest.json）。
/// </summary>
public sealed class OriginalFileBackupRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public bool ExistedOriginally { get; set; }
    public string OriginalSha256 { get; set; } = string.Empty;
    public long OriginalBytes { get; set; }
    public DateTimeOffset BackedUpAtUtc { get; set; }
}

/// <summary>
/// 备份清单根对象。
/// </summary>
public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, OriginalFileBackupRecord> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 性能调优应用或还原操作结果。
/// </summary>
public sealed class PerformanceOperationResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> AppliedItems { get; } = new();
    public List<string> BackedUpFiles { get; } = new();
    public List<string> RestoredFiles { get; } = new();
    public List<string> Warnings { get; } = new();
    public string LaunchArgumentsPreview { get; set; } = string.Empty;
}
