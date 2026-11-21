using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BazaarCore.Data;

/// <summary>
/// 只读探测本地热更新缓存、dist 目录与 Steam 安装目录中的 GameData.db / GameData.db.zip 及官方中文词库，绝不修改任何原游戏文件。
/// </summary>
public static partial class SteamPathResolver
{
    public const string RelativeGameDataZip = @"The Bazaar\TheBazaar_Data\StreamingAssets\GameData.db.zip";
    private const string DistDataDbPath = @"e:\thebazaar\dist\data\GameData.db";
    private const string DistZhCnPath = @"e:\thebazaar\dist\data\translations\zh-CN.bytes";

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    /// <summary>
    /// 按优先级定位游戏数据库（热更新直出 .db 优先于旧版 .db.zip）：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_GAME_DATA_DB；
    /// 3) %USERPROFILE%\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\GameData.db；
    /// 4) e:\thebazaar\dist\data\GameData.db；5) ResolveGameDataZipPath(customPath)。
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

        var cwdDistDb = Path.Combine(Environment.CurrentDirectory, "dist", "data", "GameData.db");
        if (File.Exists(cwdDistDb)) return Path.GetFullPath(cwdDistDb);
        if (File.Exists(DistDataDbPath)) return Path.GetFullPath(DistDataDbPath);

        return ResolveGameDataZipPath(customPath);
    }

    /// <summary>
    /// 按优先级定位官方简体中文词库 zh-CN.bytes：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_ZH_CN_PATH；
    /// 3) %USERPROFILE%\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\translations\zh-CN.bytes；
    /// 4) e:\thebazaar\dist\data\translations\zh-CN.bytes。
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

        var cwdDistZh = Path.Combine(Environment.CurrentDirectory, "dist", "data", "translations", "zh-CN.bytes");
        if (File.Exists(cwdDistZh)) return Path.GetFullPath(cwdDistZh);
        if (File.Exists(DistZhCnPath)) return Path.GetFullPath(DistZhCnPath);

        return null;
    }

    private static IEnumerable<string> DiscoverProdCacheDirs()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var special = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(special)) profiles.Add(special);

        var envProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(envProfile)) profiles.Add(envProfile);

        foreach (var profile in profiles)
        {
            yield return Path.Combine(profile, "AppData", "LocalLow", "Tempo Storm", "The Bazaar", "prod", "cache");
        }
    }

    /// <summary>
    /// 按优先级定位 GameData.db.zip：
    /// 1) 显式传入路径；2) 环境变量 BAZAAR_GAME_DATA_ZIP；3) 注册表 SteamPath + libraryfolders.vdf；4) 常见盘符候选目录。
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
                    var lib = match.Groups[1].Value.Replace(@"\\", @"\");
                    var candidate = Path.Combine(lib, "steamapps", "common", RelativeGameDataZip);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // 忽略单库 VDF 读取错误，继续检查其他候选路径
            }
        }

        // 兜底检查常见盘符上的 SteamLibrary
        foreach (var drive in new[] { "C:", "D:", "E:", "F:", "G:" })
        {
            foreach (var folder in new[] { "Steam", "SteamLibrary", @"Program Files (x86)\Steam", @"Program Files\Steam" })
            {
                var candidate = Path.Combine(drive + Path.DirectorySeparatorChar, folder, "steamapps", "common", RelativeGameDataZip);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> DiscoverSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
        }

        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        return roots;
    }
}
