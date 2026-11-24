using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BazaarHub.Models;
using Microsoft.Win32;

namespace BazaarHub.Services;

/// <summary>
/// 《The Bazaar》游戏安装目录自动定位与完整性体检服务。
/// 依次结合：用户记忆路径、Windows 注册表、Steam libraryfolders.vdf 以及全本地固定盘符扫描。
/// </summary>
public sealed partial class GameLocatorService
{
    private const string GameFolderName = "The Bazaar";

    private static readonly string[] RelativeSteamSubPaths =
    {
        @"Program Files (x86)\Steam\steamapps\common\The Bazaar",
        @"Program Files\Steam\steamapps\common\The Bazaar",
        @"Steam\steamapps\common\The Bazaar",
        @"SteamLibrary\steamapps\common\The Bazaar",
        @"Games\Steam\steamapps\common\The Bazaar",
        @"Games\SteamLibrary\steamapps\common\The Bazaar",
        @"Games\The Bazaar",
        @"The Bazaar"
    };

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("^\\s*\"\\d+\"\\s+\"([A-Za-z]:[^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex VdfLegacyPathRegex();

    /// <summary>
    /// 自动探测首个有效的游戏安装目录。
    /// </summary>
    public GameInstallInfo? AutoLocate(string? preferredDirectory = null)
    {
        foreach ((string candidate, string sourceHint) in EnumerateCandidates(preferredDirectory))
        {
            GameInstallInfo info = InspectGameDirectory(candidate, sourceHint);
            if (info.IsValidGameInstall)
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// 枚举所有探测到的有效游戏安装目录（去重）。
    /// </summary>
    public List<GameInstallInfo> DiscoverAll(string? preferredDirectory = null)
    {
        var found = new List<GameInstallInfo>();
        foreach ((string candidate, string sourceHint) in EnumerateCandidates(preferredDirectory))
        {
            GameInstallInfo info = InspectGameDirectory(candidate, sourceHint);
            if (info.IsValidGameInstall)
            {
                found.Add(info);
            }
        }

        return found;
    }

    /// <summary>
    /// 对指定目录执行游戏本体与 BepInEx 插件完整性校验。
    /// </summary>
    public GameInstallInfo InspectGameDirectory(string directoryPath, string sourceHint = "手动指定")
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new GameInstallInfo { SourceHint = sourceHint };
        }

        string normalized = NormalizeDirPath(directoryPath);
        bool exists = Directory.Exists(normalized);
        if (!exists)
        {
            return new GameInstallInfo
            {
                GameDirectory = normalized,
                Exists = false,
                SourceHint = sourceHint
            };
        }

        string exePath = Path.Combine(normalized, "TheBazaar.exe");
        string managedDir = Path.Combine(normalized, "TheBazaar_Data", "Managed");
        string winhttpPath = Path.Combine(normalized, "winhttp.dll");
        string doorstopPath = Path.Combine(normalized, "doorstop_config.ini");
        string bepCorePath = Path.Combine(normalized, "BepInEx", "core", "BepInEx.dll");
        string pluginPath = Path.Combine(normalized, LauncherConstants.PluginsRelativeDir, "BazaarLauncher.dll");
        string sdkPath = Path.Combine(normalized, LauncherConstants.PluginsRelativeDir, "BazaarModSdk.dll");
        string bootConfigPath = Path.Combine(normalized, LauncherConstants.BootConfigRelativePath);
        string aaSettingsPath = Path.Combine(normalized, LauncherConstants.AddressablesSettingsRelativePath);

        bool hasManaged = Directory.Exists(managedDir) &&
                          (File.Exists(Path.Combine(managedDir, "Assembly-CSharp.dll")) ||
                           Directory.EnumerateFiles(managedDir, "*.dll").Any());

        string? pluginVer = null;
        if (File.Exists(pluginPath))
        {
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(pluginPath);
                pluginVer = !string.IsNullOrWhiteSpace(fvi.FileVersion)
                    ? fvi.FileVersion
                    : fvi.ProductVersion;
            }
            catch
            {
                // 忽略读取版本号异常，文件存在即可识别
            }
        }

        return new GameInstallInfo
        {
            GameDirectory = normalized,
            Exists = true,
            HasGameExecutable = File.Exists(exePath),
            HasManagedDirectory = hasManaged,
            HasWinhttpProxy = File.Exists(winhttpPath),
            HasDoorstopConfig = File.Exists(doorstopPath),
            HasBepInExCore = File.Exists(bepCorePath),
            HasBazaarLauncherPlugin = File.Exists(pluginPath),
            HasBazaarModSdk = File.Exists(sdkPath),
            HasBootConfig = File.Exists(bootConfigPath),
            HasAddressablesSettings = File.Exists(aaSettingsPath),
            LauncherPluginVersion = pluginVer,
            SourceHint = sourceHint
        };
    }

    private IEnumerable<(string Path, string SourceHint)> EnumerateCandidates(string? preferredDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Clean(string path) => NormalizeDirPath(path);

        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            string p = Clean(preferredDirectory);
            if (p.Length > 0 && seen.Add(p))
            {
                yield return (p, "上次记住的路径");
            }
        }

        // 1. 通过注册表读取 Steam 根目录并解析 libraryfolders.vdf
        foreach (string steamRoot in GetSteamRootsFromRegistry())
        {
            string direct = Clean(Path.Combine(steamRoot, "steamapps", "common", GameFolderName));
            if (direct.Length > 0 && seen.Add(direct))
            {
                yield return (direct, "Steam 注册表默认库");
            }

            foreach (string libPath in ParseSteamLibraryFolders(steamRoot))
            {
                string fromVdf = Clean(Path.Combine(libPath, "steamapps", "common", GameFolderName));
                if (fromVdf.Length > 0 && seen.Add(fromVdf))
                {
                    yield return (fromVdf, "Steam libraryfolders.vdf");
                }
            }
        }

        // 2. 尝试读取 Steam 游戏卸载项注册表（AppId 或 DisplayName）
        foreach (string uninstallPath in GetInstallLocationsFromUninstallRegistry())
        {
            string clean = Clean(uninstallPath);
            if (clean.Length > 0 && seen.Add(clean))
            {
                yield return (clean, "Windows 安装注册表");
            }
        }

        // 3. 遍历本机所有就绪固定磁盘的常见路径
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            bool isReady;
            try
            {
                isReady = drive.IsReady;
            }
            catch
            {
                continue;
            }
            if (!isReady) continue;

            string root = drive.RootDirectory.FullName;
            foreach (string rel in RelativeSteamSubPaths)
            {
                string cand = Clean(Path.Combine(root, rel));
                if (cand.Length > 0 && seen.Add(cand))
                {
                    yield return (cand, $"全盘扫描 ({drive.Name.TrimEnd('\\')})");
                }
            }
        }
    }

    /// <summary>
    /// 从 Steam libraryfolders.vdf 中解析所有 Steam 库根目录（公开以便自检测试）。
    /// </summary>
    public static List<string> ParseLibraryFoldersVdfContent(string vdfContent)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in VdfPathRegex().Matches(vdfContent))
        {
            string raw = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\').Trim();
            if (raw.Length > 0 && seen.Add(raw))
            {
                results.Add(raw);
            }
        }

        foreach (Match m in VdfLegacyPathRegex().Matches(vdfContent))
        {
            string raw = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\').Trim();
            if (raw.Length > 0 && seen.Add(raw))
            {
                results.Add(raw);
            }
        }

        return results;
    }

    private static IEnumerable<string> ParseSteamLibraryFolders(string steamRoot)
    {
        string[] vdfCandidates =
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string vdf in vdfCandidates)
        {
            if (!File.Exists(vdf)) continue;
            string content;
            try
            {
                content = File.ReadAllText(vdf, Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            foreach (string path in ParseLibraryFoldersVdfContent(content))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> GetSteamRootsFromRegistry()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive Hive, string SubKey, string ValueName)[] probes =
        {
            (RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
        };

        foreach ((RegistryHive hive, string subKey, string valueName) in probes)
        {
            string? raw = ReadRegistryString(hive, subKey, valueName);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string cleaned = raw.Replace('/', '\\').Trim();
            if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = Path.GetDirectoryName(cleaned) ?? string.Empty;
            }

            cleaned = NormalizeDirPath(cleaned);
            if (cleaned.Length > 0 && seen.Add(cleaned))
            {
                yield return cleaned;
            }
        }
    }

    private static IEnumerable<string> GetInstallLocationsFromUninstallRegistry()
    {
        var results = new List<string>();
        (RegistryHive Hive, string SubKey)[] roots =
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach ((RegistryHive hive, string subKey) in roots)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using RegistryKey? uninstall = baseKey.OpenSubKey(subKey);
                if (uninstall == null) continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry == null) continue;

                    string? displayName = entry.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("Bazaar", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? loc = entry.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        results.Add(loc);
                    }
                }
            }
            catch
            {
                // 忽略无权限访问的注册表分支
            }
        }

        return results;
    }

    private static string? ReadRegistryString(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using RegistryKey? key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDirPath(string path)
    {
        return path.Replace('/', '\\').Trim().TrimEnd('\\');
    }
}
