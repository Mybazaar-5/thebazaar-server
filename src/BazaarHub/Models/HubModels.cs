using System.Reflection;
using BazaarHub.Services;

namespace BazaarHub.Models;

/// <summary>
/// 服务器连接模式枚举（保留 0..3 整数值向后兼容既有 hub-settings.json）。
/// </summary>
public enum ServerMode
{
    /// <summary>可选外置预设远程服（仅当同级或用户目录存在有效 hub-presets.json 时启用）。</summary>
    PresetRemote = 0,

    /// <summary>本地单机离线版（http://127.0.0.1:8080，开源默认首选）。</summary>
    LocalStandalone = 1,

    /// <summary>玩家自定义自建服地址。</summary>
    CustomServer = 2,

    /// <summary>纯净官方原版（设置 Enabled=false，不走自建服）。</summary>
    PureOfficial = 3
}

/// <summary>
/// 游戏安装目录体检结果。
/// </summary>
public sealed class GameInstallInfo
{
    public string GameDirectory { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool HasGameExecutable { get; init; }
    public bool HasManagedDirectory { get; init; }
    public bool HasWinhttpProxy { get; init; }
    public bool HasDoorstopConfig { get; init; }
    public bool HasBepInExCore { get; init; }
    public bool HasBazaarLauncherPlugin { get; init; }
    public bool HasBazaarModSdk { get; init; }
    public bool HasBootConfig { get; init; }
    public bool HasAddressablesSettings { get; init; }
    public string? LauncherPluginVersion { get; init; }
    public string SourceHint { get; init; } = string.Empty;

    /// <summary>核心本体文件是否完整。</summary>
    public bool IsValidGameInstall => Exists && HasGameExecutable && HasManagedDirectory;

    /// <summary>BepInEx 引导与核心自建服插件是否完整就绪（D7-P1-07：补检 doorstop_config.ini 与 BazaarModSdk.dll）。</summary>
    public bool IsBepInExReady =>
        HasWinhttpProxy &&
        HasDoorstopConfig &&
        HasBepInExCore &&
        HasBazaarLauncherPlugin &&
        HasBazaarModSdk;

    public string BuildSummaryText()
    {
        if (!IsValidGameInstall)
        {
            return "未检测到有效的《The Bazaar》游戏目录（缺少 TheBazaar.exe 或 TheBazaar_Data\\Managed）";
        }

        string pluginVer = string.IsNullOrWhiteSpace(LauncherPluginVersion)
            ? (HasBazaarLauncherPlugin ? "已安装" : "未安装")
            : $"v{LauncherPluginVersion}";

        if (IsBepInExReady)
        {
            return $"已定位游戏本体 ｜ BepInEx 与插件就绪（BazaarLauncher {pluginVer}）";
        }

        var missing = new List<string>();
        if (!HasWinhttpProxy) missing.Add("winhttp.dll");
        if (!HasDoorstopConfig) missing.Add("doorstop_config.ini");
        if (!HasBepInExCore) missing.Add("BepInEx.dll");
        if (!HasBazaarLauncherPlugin) missing.Add("BazaarLauncher.dll");
        if (!HasBazaarModSdk) missing.Add("BazaarModSdk.dll");
        return $"已定位游戏本体 ｜ 缺少引导或核心插件（{string.Join(" / ", missing)}）";
    }
}

/// <summary>
/// 客户端自建服配置快照（对应 local.thebazaar.bazaarlauncher.cfg 与 player-name.txt）。
/// </summary>
public sealed class LauncherConfigSnapshot
{
    public bool Enabled { get; set; } = true;
    public string ServerBase { get; set; } = LauncherConstants.LocalStandaloneUrl;
    public bool AcceptServerMods { get; set; } = true;
    public string ConfigPlayerName { get; set; } = string.Empty;
    public string PlayerNameFileValue { get; set; } = string.Empty;
    public bool BypassSteam { get; set; } = true;
    public bool UnlockEverything { get; set; } = true;
    public bool FastForwardFirstFight { get; set; } = true;
    public int MaxForegroundFps { get; set; } = 60;
    public int BackgroundFps { get; set; } = 20;
    public bool LazyLoadCosmetics { get; set; } = true;
    public bool ReduceHighConcurrencyVfx { get; set; } = false;

    /// <summary>
    /// 按官方客户端约定解析当前生效昵称（cfg PlayerName > player-name.txt）。
    /// </summary>
    public string EffectivePlayerName =>
        !string.IsNullOrWhiteSpace(ConfigPlayerName)
            ? ConfigPlayerName.Trim()
            : PlayerNameFileValue.Trim();

    /// <summary>
    /// 推断当前对应的服务器模式（优先匹配本机 8080 单机回环，彻底杜绝远程预设碰撞）。
    /// </summary>
    public ServerMode InferServerMode(HubPresetConfig? preset = null)
    {
        if (!Enabled)
        {
            return ServerMode.PureOfficial;
        }

        string normalized = LauncherConfigService.NormalizeServerUrl(ServerBase);
        if (string.IsNullOrEmpty(normalized) || HubPresetLoader.IsLocalStandaloneEndpoint(normalized))
        {
            return ServerMode.LocalStandalone;
        }

        HubPresetConfig activePreset = preset ?? HubPresetRegistry.Current;
        if (activePreset.HasPresetRemote &&
            string.Equals(normalized, activePreset.PresetRemoteUrl, StringComparison.OrdinalIgnoreCase))
        {
            return ServerMode.PresetRemote;
        }

        return ServerMode.CustomServer;
    }
}

/// <summary>
/// 启动器自身用户偏好设置（保存在 %LocalAppData%\BazaarHub\hub-settings.json）。
/// </summary>
public sealed class HubPreferences
{
    public string LastGameDirectory { get; set; } = string.Empty;
    public ServerMode SelectedMode { get; set; } = ServerMode.LocalStandalone;
    public string CustomServerUrl { get; set; } = "http://127.0.0.1:8080";
    public List<string> SavedCustomServers { get; set; } = new();
    public bool AutoSyncBeforeLaunch { get; set; } = true;
    public PerformanceTuningOptions PerformanceOptions { get; set; } = PerformanceTuningOptions.CreateRecommended();
}

/// <summary>
/// 服务器健康与延迟探测结果。
/// </summary>
public sealed class ServerProbeResult
{
    public string ServerUrl { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public bool HealthzOk { get; init; }
    public bool PingEndpointSupported { get; init; }
    public long LatencyMs { get; init; } = -1;
    public int? OnlinePlayers { get; init; }
    public int? ActiveRuns { get; init; }
    public int? ActiveLobbies { get; init; }
    public long? UptimeSeconds { get; init; }
    public string? ServerVersion { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 全局常量与默认路径约定（零私人硬编码 IP）。
/// </summary>
public static class LauncherConstants
{
    private const string FallbackHubVersion = "15.5.0";

    /// <summary>
    /// 启动器版本号（D7-P1-01：动态读取根级 Directory.Build.props 注入的 AssemblyInformationalVersion）。
    /// </summary>
    public static string HubVersion { get; } = ResolveHubVersion();

    private static string ResolveHubVersion()
    {
        try
        {
            string? info = typeof(LauncherConstants).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plusIdx = info.IndexOf('+');
                string clean = (plusIdx > 0 ? info[..plusIdx] : info).Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    return clean;
                }
            }
        }
        catch
        {
            // 忽略反射异常回退默认版本号
        }
        return FallbackHubVersion;
    }

    public const string LocalStandaloneUrl = "http://127.0.0.1:8080";
    public const string LauncherCfgRelativePath = @"BepInEx\config\local.thebazaar.bazaarlauncher.cfg";
    public const string BepInExCfgRelativePath = @"BepInEx\config\BepInEx.cfg";
    public const string BootConfigRelativePath = @"TheBazaar_Data\boot.config";
    public const string AddressablesSettingsRelativePath = @"TheBazaar_Data\StreamingAssets\aa\settings.json";
    public const string PluginsRelativeDir = @"BepInEx\plugins";
    public const string ModCacheRelativeDir = @"BepInEx\config\BazaarModCache";
    public const string ZhOverlayRelativePath = @"BepInEx\config\custom_zh_overlay.json";
    public const string BackupRootFolderName = "_BazaarHubBackups";
    public const string StagingRootFolderName = "_BazaarHubStaging";
    public const string OriginalBackupSubFolder = "original";
    public const string PlayerNameFileName = "player-name.txt";
}
