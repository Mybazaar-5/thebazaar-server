using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 客户端配置读写服务。
/// 负责：
/// 1. 无损读写 BepInEx\config\local.thebazaar.bazaarlauncher.cfg（保留全部注释与未知段落）；
/// 2. 以无 BOM UTF-8 读写游戏根目录 player-name.txt；
/// 3. 生成「机器名_用户名」防撞号专属昵称；
/// 4. 切换外置预设远程服（可选）/ 本地单机离线版 / 自定义服务器 / 纯净官方原版；
/// 5. 读写启动器自身偏好设置（hub-settings.json）。
/// </summary>
public sealed class LauncherConfigService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> GenericCollisionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "player",
        "admin",
        "administrator",
        "user",
        "guest",
        "default",
        "test",
        "bazaar"
    };

    public static string GetHubSettingsPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "BazaarHub", "hub-settings.json");
    }

    public static string GetLauncherConfigPath(string gameDir) =>
        Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath);

    public static string GetPlayerNameFilePath(string gameDir) =>
        Path.Combine(gameDir, LauncherConstants.PlayerNameFileName);

    /// <summary>
    /// 加载启动器自身偏好设置；若旧偏好指向已不存在的外置预设模式则自动归一化至本地单机模式。
    /// </summary>
    public HubPreferences LoadHubPreferences(string? customSettingsPath = null, HubPresetConfig? preset = null)
    {
        HubPresetConfig activePreset = preset ?? HubPresetRegistry.Current;
        string path = customSettingsPath ?? GetHubSettingsPath();
        HubPreferences prefs;
        if (!File.Exists(path))
        {
            prefs = new HubPreferences { SelectedMode = activePreset.DefaultMode };
            return prefs;
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            prefs = JsonSerializer.Deserialize<HubPreferences>(json, JsonOptions)
                ?? new HubPreferences { SelectedMode = activePreset.DefaultMode };
        }
        catch
        {
            prefs = new HubPreferences { SelectedMode = activePreset.DefaultMode };
        }

        if (prefs.SelectedMode == ServerMode.PresetRemote && !activePreset.HasPresetRemote)
        {
            prefs.SelectedMode = ServerMode.LocalStandalone;
        }

        return prefs;
    }

    /// <summary>
    /// 原子保存启动器自身偏好设置。
    /// </summary>
    public void SaveHubPreferences(HubPreferences preferences, string? customSettingsPath = null)
    {
        string path = customSettingsPath ?? GetHubSettingsPath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(preferences, JsonOptions);
        try
        {
            File.WriteAllText(tmpPath, json, Utf8NoBom);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 读取指定游戏目录下的客户端自建服配置快照。
    /// </summary>
    public LauncherConfigSnapshot ReadSnapshot(string gameDir, HubPresetConfig? preset = null)
    {
        HubPresetConfig activePreset = preset ?? HubPresetRegistry.Current;
        string defaultBase = activePreset.HasPresetRemote
            ? activePreset.PresetRemoteUrl
            : LauncherConstants.LocalStandaloneUrl;

        string cfgPath = GetLauncherConfigPath(gameDir);
        IniDocument doc = IniDocument.LoadOrEmpty(cfgPath);

        string nameFilePath = GetPlayerNameFilePath(gameDir);
        string nameFromFile = string.Empty;
        if (File.Exists(nameFilePath))
        {
            try
            {
                nameFromFile = File.ReadAllText(nameFilePath, Encoding.UTF8)
                    .Trim('\uFEFF', ' ', '\t', '\r', '\n');
            }
            catch
            {
                nameFromFile = string.Empty;
            }
        }

        return new LauncherConfigSnapshot
        {
            Enabled = doc.GetBool("General", "Enabled", true),
            ServerBase = doc.GetValue("General", "ServerBase") ?? defaultBase,
            AcceptServerMods = doc.GetBool("General", "AcceptServerMods", true),
            ConfigPlayerName = doc.GetValue("General", "PlayerName") ?? string.Empty,
            PlayerNameFileValue = nameFromFile,
            BypassSteam = doc.GetBool("General", "BypassSteam", true),
            UnlockEverything = doc.GetBool("General", "UnlockEverything", true),
            FastForwardFirstFight = doc.GetBool("Combat", "FastForwardFirstFight", true),
            MaxForegroundFps = doc.GetInt("Performance", "MaxForegroundFps", 60),
            BackgroundFps = doc.GetInt("Performance", "BackgroundFps", 20),
            LazyLoadCosmetics = doc.GetBool("Performance", "LazyLoadCosmetics", true),
            ReduceHighConcurrencyVfx = doc.GetBool("Performance", "ReduceHighConcurrencyVfx", false)
        };
    }

    /// <summary>
    /// 无损应用服务器模式、目标地址、玩家昵称及核心开关到配置文件与 player-name.txt。
    /// </summary>
    public LauncherConfigSnapshot ApplyServerAndPlayerSettings(
        string gameDir,
        ServerMode mode,
        string? customServerUrl,
        string? playerName,
        bool? bypassSteam = null,
        bool? unlockEverything = null,
        bool? acceptServerMods = null,
        HubPresetConfig? preset = null)
    {
        HubPresetConfig activePreset = preset ?? HubPresetRegistry.Current;
        string cfgPath = GetLauncherConfigPath(gameDir);
        IniDocument doc = File.Exists(cfgPath)
            ? IniDocument.LoadOrEmpty(cfgPath)
            : CreateDefaultLauncherIni();

        if (acceptServerMods.HasValue)
        {
            doc.SetValue("General", "AcceptServerMods", acceptServerMods.Value ? "true" : "false");
        }

        switch (mode)
        {
            case ServerMode.PresetRemote:
                doc.SetValue("General", "Enabled", "true");
                doc.SetValue(
                    "General",
                    "ServerBase",
                    activePreset.HasPresetRemote ? activePreset.PresetRemoteUrl : LauncherConstants.LocalStandaloneUrl);
                break;

            case ServerMode.LocalStandalone:
                doc.SetValue("General", "Enabled", "true");
                doc.SetValue("General", "ServerBase", LauncherConstants.LocalStandaloneUrl);
                break;

            case ServerMode.CustomServer:
                string normalizedCustom = NormalizeServerUrl(customServerUrl);
                if (string.IsNullOrWhiteSpace(normalizedCustom))
                {
                    throw new ArgumentException("自定义服务器地址不能为空，请填写有效的 http:// 或 https:// 地址。");
                }
                doc.SetValue("General", "Enabled", "true");
                doc.SetValue("General", "ServerBase", normalizedCustom);
                break;

            case ServerMode.PureOfficial:
                doc.SetValue("General", "Enabled", "false");
                break;
        }

        if (bypassSteam.HasValue)
        {
            doc.SetValue("General", "BypassSteam", bypassSteam.Value ? "true" : "false");
        }

        if (unlockEverything.HasValue)
        {
            doc.SetValue("General", "UnlockEverything", unlockEverything.Value ? "true" : "false");
        }

        // 昵称统一以无 BOM UTF-8 写入 player-name.txt，同时清空 cfg 的 PlayerName 避免旧高优先级值遮蔽 player-name.txt
        string finalName = ResolveOrGeneratePlayerName(playerName);
        if (mode != ServerMode.PureOfficial)
        {
            WritePlayerNameFile(gameDir, finalName);
            doc.SetValue("General", "PlayerName", string.Empty);
        }

        doc.SaveAtomic(cfgPath);
        return ReadSnapshot(gameDir, activePreset);
    }

    /// <summary>
    /// 仅以无 BOM UTF-8 原子写入 player-name.txt。
    /// </summary>
    public void WritePlayerNameFile(string gameDir, string playerName)
    {
        Directory.CreateDirectory(gameDir);
        string sanitized = SanitizePlayerName(playerName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = GenerateCollisionSafePlayerName();
        }

        string path = GetPlayerNameFilePath(gameDir);
        string tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmpPath, sanitized, Utf8NoBom);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 若给定昵称有效且非泛用撞号名则保留，否则自动生成「机器名_用户名」防撞号昵称。
    /// </summary>
    public static string ResolveOrGeneratePlayerName(string? candidate)
    {
        string sanitized = SanitizePlayerName(candidate ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized) || GenericCollisionNames.Contains(sanitized))
        {
            return GenerateCollisionSafePlayerName();
        }

        return sanitized;
    }

    /// <summary>
    /// 按「机器名_用户名」规则自动生成防撞号专属昵称。
    /// </summary>
    public static string GenerateCollisionSafePlayerName(string? machineOverride = null, string? userOverride = null)
    {
        string machine = CleanIdentityPart(machineOverride ?? Environment.MachineName);
        string user = CleanIdentityPart(userOverride ?? Environment.UserName);

        if (string.IsNullOrEmpty(machine)) machine = "PC";
        if (string.IsNullOrEmpty(user)) user = "Player";

        string combined = string.Equals(machine, user, StringComparison.OrdinalIgnoreCase)
            ? $"{machine}_Bazaar"
            : $"{machine}_{user}";

        if (combined.Length > 32)
        {
            combined = combined[..32].TrimEnd('_', '-');
        }

        return combined;
    }

    /// <summary>
    /// 规范化服务器 URL（自动补全 http:// 并去除末尾斜杠）。
    /// </summary>
    public static string NormalizeServerUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        string trimmed = rawUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }

    /// <summary>
    /// 根据所选模式解析有效服务器基址（纯净官方原版返回空字符串；未配置外置预设时回落单机地址）。
    /// </summary>
    public static string ResolveServerBase(ServerMode mode, string? customServerUrl, HubPresetConfig? preset = null)
    {
        HubPresetConfig activePreset = preset ?? HubPresetRegistry.Current;
        return mode switch
        {
            ServerMode.PresetRemote => activePreset.HasPresetRemote
                ? activePreset.PresetRemoteUrl
                : LauncherConstants.LocalStandaloneUrl,
            ServerMode.LocalStandalone => LauncherConstants.LocalStandaloneUrl,
            ServerMode.CustomServer => NormalizeServerUrl(customServerUrl),
            ServerMode.PureOfficial => string.Empty,
            _ => LauncherConstants.LocalStandaloneUrl
        };
    }

    private static string SanitizePlayerName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || char.IsControl(ch)) continue;
            sb.Append(ch);
        }

        string cleaned = sb.ToString().Trim();
        return cleaned.Length > 32 ? cleaned[..32].Trim() : cleaned;
    }

    private static string CleanIdentityPart(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch >= 0x4E00 && ch <= 0x9FFF)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim('_', '-');
    }

    /// <summary>
    /// 创建结构完整且带中文注释的默认 BazaarLauncher 配置文件（仅当游戏目录尚无 cfg 时使用，默认单机地址）。
    /// </summary>
    public static IniDocument CreateDefaultLauncherIni()
    {
        string template = $"""
## Settings file was created by plugin BazaarLauncher v{LauncherConstants.HubVersion}
## Plugin GUID: local.thebazaar.bazaarlauncher

[Combat]

## 首场战斗（Day 1）默认开启 3x 加速；在游戏选项中手动关闭后将同步记忆。
# Setting type: Boolean
# Default value: true
FastForwardFirstFight = true

## 内部标记：玩家是否已在游戏选项中显式修改过首战加速开关。
# Setting type: Boolean
# Default value: false
FastForwardUserExplicitlySet = false

[Debug]

## 把 /sessions、/commands 的原始字节落盘（差分测试用）。
# Setting type: Boolean
# Default value: false
DumpTraffic = false

## 抓包输出目录。
# Setting type: String
# Default value: 
DumpDir = 

[General]

## 总开关。false = 完全按原版行为运行（连官方服）。
# Setting type: Boolean
# Default value: true
Enabled = true

## 自建服基址。单机默认 http://127.0.0.1:8080；联机请填写远程服务器地址
# Setting type: String
# Default value: http://127.0.0.1:8080
ServerBase = http://127.0.0.1:8080

## 玩家名（账号名）。留空则读游戏目录 player-name.txt，再退回系统用户名。
# Setting type: String
# Default value: 
PlayerName = 

## 摘掉 Steam 依赖：不需要 Steam 客户端与正版票据。
# Setting type: Boolean
# Default value: true
BypassSteam = true

## 解锁全部英雄 / 皮肤 / 藏品 / 赛季奖励。
# Setting type: Boolean
# Default value: true
UnlockEverything = true

[Performance]

## 前台游玩帧率上限（建议 60 省电稳帧；支持 60/90/120，0 = 不额外限制跟随游戏原版）。
# Setting type: Int32
# Default value: 60
MaxForegroundFps = 60

## 游戏窗口失去焦点切到后台时的降频帧率（默认 20 FPS 释放 GPU，0 = 不降频）。
# Setting type: Int32
# Default value: 20
BackgroundFps = 20

## 按需懒加载藏品：启动时仅加载当前穿戴装扮，跳过 396 套未穿戴 3D 藏品全量预加载，打开藏品柜时按需加载。
# Setting type: Boolean
# Default value: true
LazyLoadCosmetics = true

## 高并发战斗特效降载：高倍速或多触发流派战斗时限制并发粒子数量，提升 1% Low 帧率。
# Setting type: Boolean
# Default value: false
ReduceHighConcurrencyVfx = false

""";
        return IniDocument.Parse(template);
    }
}
