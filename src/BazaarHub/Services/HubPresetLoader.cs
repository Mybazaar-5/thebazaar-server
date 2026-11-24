using System.Net;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 外置可选节点预设（hub-presets.json）的不可变配置快照。
/// 开源默认态（无 hub-presets.json）恒为 Empty：不硬编码任何私人远程服务器地址，默认首选本地单机模式。
/// </summary>
public sealed class HubPresetConfig
{
    public static HubPresetConfig Empty { get; } = new(
        hasPresetRemote: false,
        presetRemoteUrl: string.Empty,
        presetRemoteName: "预设远程服",
        presetRemoteShortName: "远程服",
        presetRemoteSubtitle: "未配置外置预设节点");

    public bool HasPresetRemote { get; }
    public string PresetRemoteUrl { get; }
    public string PresetRemoteName { get; }
    public string PresetRemoteShortName { get; }
    public string PresetRemoteSubtitle { get; }

    public ServerMode DefaultMode => HasPresetRemote ? ServerMode.PresetRemote : ServerMode.LocalStandalone;

    public HubPresetConfig(
        bool hasPresetRemote,
        string presetRemoteUrl,
        string presetRemoteName,
        string presetRemoteShortName,
        string presetRemoteSubtitle)
    {
        HasPresetRemote = hasPresetRemote;
        PresetRemoteUrl = presetRemoteUrl;
        PresetRemoteName = presetRemoteName;
        PresetRemoteShortName = presetRemoteShortName;
        PresetRemoteSubtitle = presetRemoteSubtitle;
    }
}

/// <summary>
/// 外置可选 hub-presets.json 加载器（P0-46 根治核心）：
/// 1. 彻底废除发版脚本对 .cs 源码的硬编码字符串替换；
/// 2. 防碰撞硬不变量：凡指向回环地址（127.0.0.1 / localhost / ::1 / 0.0.0.0）的远程预设一律拒收，
///    从数学上根除 PresetRemoteUrl == LocalStandaloneUrl 碰撞。
/// </summary>
public static class HubPresetLoader
{
    public const string PresetFileName = "hub-presets.json";

    public static HubPresetConfig Load(string? explicitPath = null)
    {
        foreach (string candidate in EnumerateCandidatePaths(explicitPath))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;

            try
            {
                string json = File.ReadAllText(candidate, Encoding.UTF8);
                return Parse(json);
            }
            catch
            {
                // 损坏的外置预设文件优雅降级至开源默认态，绝不阻断启动器启动
                return HubPresetConfig.Empty;
            }
        }

        return HubPresetConfig.Empty;
    }

    public static HubPresetConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HubPresetConfig.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return HubPresetConfig.Empty;

            if (!TryGetPropertyIgnoreCase(root, "defaultRemote", out JsonElement remoteEl) ||
                remoteEl.ValueKind != JsonValueKind.Object)
            {
                return HubPresetConfig.Empty;
            }

            if (TryGetPropertyIgnoreCase(remoteEl, "enabled", out JsonElement enabledEl) &&
                enabledEl.ValueKind == JsonValueKind.False)
            {
                return HubPresetConfig.Empty;
            }

            string rawUrl = GetString(remoteEl, "url");
            string normalizedUrl = LauncherConfigService.NormalizeServerUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
                return HubPresetConfig.Empty;

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return HubPresetConfig.Empty;
            }

            // 防碰撞硬不变量：绝不允许远程预设指向本机回环或 0.0.0.0
            if (IsLoopbackOrAnyHost(uri.Host))
            {
                return HubPresetConfig.Empty;
            }

            string name = GetString(remoteEl, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "预设远程服";

            string shortName = GetString(remoteEl, "shortName");
            if (string.IsNullOrWhiteSpace(shortName)) shortName = "远程服";

            string subtitle = GetString(remoteEl, "subtitle");
            if (string.IsNullOrWhiteSpace(subtitle))
            {
                subtitle = normalizedUrl.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                                        .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return new HubPresetConfig(
                hasPresetRemote: true,
                presetRemoteUrl: normalizedUrl,
                presetRemoteName: name.Trim(),
                presetRemoteShortName: shortName.Trim(),
                presetRemoteSubtitle: subtitle.Trim());
        }
        catch
        {
            return HubPresetConfig.Empty;
        }
    }

    public static bool IsLoopbackOrAnyHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        string clean = host.Trim().Trim('[', ']');
        if (string.Equals(clean, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "::", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(clean, out IPAddress? ip))
        {
            return IPAddress.IsLoopback(ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip);
        }

        return false;
    }

    /// <summary>
    /// 精确判定地址是否为默认本机单机托管端点（回环主机 127.0.0.1 / localhost / ::1 且端口为 8080 且路径为空或 /）。
    /// 注意：不误抢占 127.0.0.1:9090 等玩家自定义本机端口。
    /// </summary>
    public static bool IsLocalStandaloneEndpoint(string? normalizedUrl)
    {
        if (string.IsNullOrWhiteSpace(normalizedUrl))
            return false;

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string cleanHost = uri.Host.Trim().Trim('[', ']');
        bool isLoopback = string.Equals(cleanHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                          (IPAddress.TryParse(cleanHost, out IPAddress? ip) && IPAddress.IsLoopback(ip));
        if (!isLoopback)
            return false;

        if (uri.Port != 8080)
            return false;

        string path = uri.AbsolutePath.TrimEnd('/');
        return string.IsNullOrEmpty(path);
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
            yield break;
        }

        string? envPath = Environment.GetEnvironmentVariable("BAZAAR_HUB_PRESETS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            yield return envPath;
        }

        yield return Path.Combine(AppContext.BaseDirectory, PresetFileName);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "BazaarHub", PresetFileName);
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement obj, string name)
    {
        return TryGetPropertyIgnoreCase(obj, name, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? (val.GetString() ?? string.Empty)
            : string.Empty;
    }
}

/// <summary>进程级当前外置预设只读入口。</summary>
public static class HubPresetRegistry
{
    private static readonly Lazy<HubPresetConfig> LazyCurrent = new(() => HubPresetLoader.Load());

    public static HubPresetConfig Current => LazyCurrent.Value;
}
