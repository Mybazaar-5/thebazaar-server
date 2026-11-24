using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BazaarCore.Data;

/// <summary>
/// 只读探测本地热更新缓存、dist 目录与 Steam 安装目录中的 GameData.db / GameData.db.zip 及官方中文词库，绝不修改任何原游戏文件。
/// </summary>
public static partial class SteamPathResolver
{
    public static readonly string RelativeGameDataZip =
        Path.Combine("The Bazaar", "TheBazaar_Data", "StreamingAssets", "GameData.db.zip");

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    /// <summary>
    /// 按优先级定位游戏数据库（热更新直出 .db 优先于旧版 .db.zip）：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_GAME_DATA_DB；
    /// 3) %USERPROFILE%\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\GameData.db；
    /// 4) 当前目录与程序基目录向上探测到的 dist/data/GameData.db；5) ResolveGameDataZipPath(customPath)。
    /// </summary>
    public static string? ResolveGameDataSourcePath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath)) return Path.GetFullPath(customPath);
            if (Directory.Exists(customPath))
            {
                var dbInDir = Path.Combine(customPath, "GameData.db");
                if (File.Exists(dbInDir)) return Path.GetFullPath(dbInDir);

                var zipInDir = Path.Combine(customPath, "TheBazaar_Data", "StreamingAssets", "GameData.db.zip");
                if (File.Exists(zipInDir)) return Path.GetFullPath(zipInDir);
            }
        }

        var envDb = Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB");
        if (!string.IsNullOrWhiteSpace(envDb) && File.Exists(envDb))
            return Path.GetFullPath(envDb);

        foreach (var cacheDir in DiscoverProdCacheDirs())
        {
            var prodDb = Path.Combine(cacheDir, "GameData.db");
            if (File.Exists(prodDb)) return Path.GetFullPath(prodDb);
        }

        foreach (var distDataDir in DiscoverRelativeDistDataDirs())
        {
            var distDb = Path.Combine(distDataDir, "GameData.db");
            if (File.Exists(distDb)) return Path.GetFullPath(distDb);
        }

        return ResolveGameDataZipPath(customPath);
    }

    /// <summary>
    /// 按优先级定位官方简体中文词库 zh-CN.bytes：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_ZH_CN_PATH；
    /// 3) %USERPROFILE%\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\translations\zh-CN.bytes；
    /// 4) 当前目录与程序基目录向上探测到的 dist/data/translations/zh-CN.bytes。
    /// </summary>
    public static string? ResolveZhCnTranslationPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath)) return Path.GetFullPath(customPath);
            if (Directory.Exists(customPath))
            {
                var direct = Path.Combine(customPath, "zh-CN.bytes");
                if (File.Exists(direct)) return Path.GetFullPath(direct);

                var nested = Path.Combine(customPath, "translations", "zh-CN.bytes");
                if (File.Exists(nested)) return Path.GetFullPath(nested);
            }
        }

        var envPath = Environment.GetEnvironmentVariable("BAZAAR_ZH_CN_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        foreach (var cacheDir in DiscoverProdCacheDirs())
        {
            var prodZh = Path.Combine(cacheDir, "translations", "zh-CN.bytes");
            if (File.Exists(prodZh)) return Path.GetFullPath(prodZh);
        }

        foreach (var distDataDir in DiscoverRelativeDistDataDirs())
        {
            var distZh = Path.Combine(distDataDir, "translations", "zh-CN.bytes");
            if (File.Exists(distZh)) return Path.GetFullPath(distZh);
        }

        return null;
    }

    /// <summary>
    /// 按优先级定位外挂中文词典 custom_zh_overlay.json。
    /// </summary>
    public static string? ResolveCustomZhOverlayPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath)) return Path.GetFullPath(customPath);
            if (Directory.Exists(customPath))
            {
                var direct = Path.Combine(customPath, "custom_zh_overlay.json");
                if (File.Exists(direct)) return Path.GetFullPath(direct);
                var nested = Path.Combine(customPath, "translations", "custom_zh_overlay.json");
                if (File.Exists(nested)) return Path.GetFullPath(nested);
            }
        }

        var envPath = Environment.GetEnvironmentVariable("BAZAAR_ZH_OVERLAY_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        foreach (var distDataDir in DiscoverRelativeDistDataDirs())
        {
            var nested = Path.Combine(distDataDir, "translations", "custom_zh_overlay.json");
            if (File.Exists(nested)) return Path.GetFullPath(nested);
            var direct = Path.Combine(distDataDir, "custom_zh_overlay.json");
            if (File.Exists(direct)) return Path.GetFullPath(direct);
        }

        return null;
    }

    private static IEnumerable<string> DiscoverRelativeDistDataDirs()
    {
        var yielded = new HashSet<string>(PathComparer);

        IEnumerable<string> WalkUp(string? startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir)) yield break;
            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(startDir); }
            catch { yield break; }

            for (int depth = 0; depth < 7 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "dist", "data");
                if (Directory.Exists(candidate) && yielded.Add(candidate))
                    yield return candidate;

                var dataOnly = Path.Combine(dir.FullName, "data");
                if (Directory.Exists(dataOnly) && yielded.Add(dataOnly))
                    yield return dataOnly;
            }
        }

        foreach (var d in WalkUp(Environment.CurrentDirectory)) yield return d;
        foreach (var d in WalkUp(AppContext.BaseDirectory)) yield return d;
    }

    private static IEnumerable<string> DiscoverProdCacheDirs()
    {
        var profiles = new HashSet<string>(PathComparer);
        var special = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(special)) profiles.Add(special);

        if (OperatingSystem.IsWindows())
        {
            var envProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(envProfile)) profiles.Add(envProfile);
            foreach (var profile in profiles)
                yield return Path.Combine(profile, "AppData", "LocalLow", "Tempo Storm", "The Bazaar", "prod", "cache");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) profiles.Add(home);
            foreach (var profile in profiles)
                yield return Path.Combine(profile, "Library", "Application Support", "Tempo Storm", "The Bazaar", "prod", "cache");
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) profiles.Add(home);
            foreach (var profile in profiles)
                yield return Path.Combine(profile, ".config", "unity3d", "Tempo Storm", "The Bazaar", "prod", "cache");
        }
    }

    /// <summary>
    /// 按优先级定位 GameData.db.zip：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_GAME_DATA_ZIP；3) 注册表/平台默认 Steam 根 + libraryfolders.vdf；4) Windows 常见盘符候选目录。
    /// </summary>
    public static string? ResolveGameDataZipPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath)) return Path.GetFullPath(customPath);
            var sub = Path.Combine(customPath, "TheBazaar_Data", "StreamingAssets", "GameData.db.zip");
            if (File.Exists(sub)) return sub;
        }

        var envPath = Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_ZIP");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        foreach (var steamRoot in DiscoverSteamRoots())
        {
            var direct = Path.Combine(steamRoot, "steamapps", "common", RelativeGameDataZip);
            if (File.Exists(direct)) return direct;

            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                var content = File.ReadAllText(vdf);
                foreach (Match match in LibraryPathRegex().Matches(content))
                {
                    var rawLib = match.Groups[1].Value;
                    var lib = OperatingSystem.IsWindows()
                        ? rawLib.Replace(@"\\", @"\")
                        : rawLib.Replace(@"\\", "/").Replace('\\', '/');
                    var candidate = Path.Combine(lib, "steamapps", "common", RelativeGameDataZip);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // 忽略单库 VDF 读取错误，继续检查其他候选路径
            }
        }

        // 兜底检查仅限 Windows 常见盘符上的 SteamLibrary
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in new[] { "C:", "D:", "E:", "F:", "G:" })
            {
                string driveRoot = drive + Path.DirectorySeparatorChar;
                string[] folders =
                [
                    "Steam",
                    "SteamLibrary",
                    Path.Combine("Program Files (x86)", "Steam"),
                    Path.Combine("Program Files", "Steam"),
                ];
                foreach (var folder in folders)
                {
                    var candidate = Path.Combine(driveRoot, folder, "steamapps", "common", RelativeGameDataZip);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> DiscoverSteamRoots()
    {
        var roots = new HashSet<string>(PathComparer);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                    roots.Add(steamPath.Replace('/', '\\'));
            }
            catch
            {
                // 忽略注册表访问异常
            }

            string driveRoot = "C:" + Path.DirectorySeparatorChar;
            roots.Add(Path.Combine(driveRoot, "Program Files (x86)", "Steam"));
            roots.Add(Path.Combine(driveRoot, "Program Files", "Steam"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (!string.IsNullOrWhiteSpace(home))
                roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (!string.IsNullOrWhiteSpace(home))
            {
                roots.Add(Path.Combine(home, ".local", "share", "Steam"));
                roots.Add(Path.Combine(home, ".steam", "steam"));
                roots.Add(Path.Combine(home, ".steam", "root"));
            }
        }

        return roots;
    }
}
