using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 排错诊断包导出服务。
/// 一键打包：BepInEx\LogOutput.log、Unity Player.log、全部核心配置文件、系统硬件与安装体检信息、
/// 多服务器实时测速结果以及启动器本次会话日志为标准 ZIP 压缩包。
/// </summary>
public sealed class DiagnosticsExportService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 查找 Unity Player.log 所在目录。
    /// </summary>
    public static string? LocateUnityPlayerLogDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localLow = Path.Combine(userProfile, "AppData", "LocalLow");
        if (!Directory.Exists(localLow)) return null;

        string[] directCandidates =
        {
            Path.Combine(localLow, "Tempo Storm", "The Bazaar"),
            Path.Combine(localLow, "Tempo Games", "The Bazaar"),
            Path.Combine(localLow, "Tempo", "The Bazaar"),
            Path.Combine(localLow, "The Bazaar")
        };

        foreach (string dir in directCandidates)
        {
            if (Directory.Exists(dir) &&
                (File.Exists(Path.Combine(dir, "Player.log")) || File.Exists(Path.Combine(dir, "Player-prev.log"))))
            {
                return dir;
            }
        }

        // 宽松扫描 LocalLow 下一级厂商目录中的 The Bazaar 子目录
        try
        {
            foreach (string companyDir in Directory.EnumerateDirectories(localLow))
            {
                string sub = Path.Combine(companyDir, "The Bazaar");
                if (Directory.Exists(sub) && File.Exists(Path.Combine(sub, "Player.log")))
                {
                    return sub;
                }
            }
        }
        catch
        {
            // 忽略无权限目录
        }

        return null;
    }

    /// <summary>
    /// 导出完整排错诊断 ZIP 包。
    /// </summary>
    public async Task<string> ExportZipAsync(
        string outputZipPath,
        string? gameDir,
        IEnumerable<string> probeUrls,
        IEnumerable<string> sessionLogs,
        string? unityLogDirOverride = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string? outDir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        log?.Invoke("[诊断] 正在收集网络测速结果...");
        var probeResults = new List<ServerProbeResult>();
        using (var probeService = new ServerProbeService())
        {
            foreach (string rawUrl in probeUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                probeResults.Add(await probeService.ProbeAsync(rawUrl, cancellationToken));
            }
        }

        GameInstallInfo? installInfo = null;
        var installedPlugins = new List<object>();
        if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
        {
            var locator = new GameLocatorService();
            installInfo = locator.InspectGameDirectory(gameDir, "诊断导出");

            string pluginsDir = Path.Combine(gameDir, LauncherConstants.PluginsRelativeDir);
            if (Directory.Exists(pluginsDir))
            {
                foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(dll);
                    installedPlugins.Add(new
                    {
                        RelativePath = Path.GetRelativePath(pluginsDir, dll),
                        SizeBytes = fi.Length,
                        LastWriteTimeUtc = fi.LastWriteTimeUtc,
                        Sha256 = UpdateSyncService.ComputeFileSha256(dll)
                    });
                }
            }
        }

        string? unityLogDir = unityLogDirOverride ?? LocateUnityPlayerLogDirectory();

        var systemInfo = new
        {
            ExportedAt = DateTimeOffset.Now,
            HubVersion = LauncherConstants.HubVersion,
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            UnityPlayerLogDirectory = unityLogDir ?? "(未找到)",
            GameInstall = installInfo,
            InstalledPlugins = installedPlugins
        };

        string tmpZip = outputZipPath + ".tmp";
        try
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);

            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, "system-info.json", JsonSerializer.Serialize(systemInfo, JsonOptions));
                WriteTextEntry(zip, "network-probe.json", JsonSerializer.Serialize(probeResults, JsonOptions));
                WriteTextEntry(zip, "hub-session.log", string.Join(Environment.NewLine, sessionLogs));

                if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
                {
                    TryAddFileShared(zip, Path.Combine(gameDir, @"BepInEx\LogOutput.log"), "logs/BepInEx_LogOutput.log");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath), "config/local.thebazaar.bazaarlauncher.cfg");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.BepInExCfgRelativePath), "config/BepInEx.cfg");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.BootConfigRelativePath), "config/boot.config");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.AddressablesSettingsRelativePath), "config/aa_settings.json");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.PlayerNameFileName), "config/player-name.txt");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.BackupRootFolderName, "backup-manifest.json"), "config/backup-manifest.json");
                    TryAddFileShared(zip, Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "manifest.local.json"), "config/mods-manifest.local.json");
                }

                if (!string.IsNullOrWhiteSpace(unityLogDir) && Directory.Exists(unityLogDir))
                {
                    TryAddFileShared(zip, Path.Combine(unityLogDir, "Player.log"), "logs/Unity_Player.log");
                    TryAddFileShared(zip, Path.Combine(unityLogDir, "Player-prev.log"), "logs/Unity_Player-prev.log");
                }
            }

            File.Move(tmpZip, outputZipPath, overwrite: true);
            log?.Invoke($"[成功] 排错诊断包已生成：{outputZipPath}");
            return outputZipPath;
        }
        catch
        {
            try
            {
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
            }
            catch
            {
                // 忽略清理异常
            }
            throw;
        }
    }

    private static void WriteTextEntry(ZipArchive zip, string entryName, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        byte[] bytes = Utf8NoBom.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void TryAddFileShared(ZipArchive zip, string sourcePath, string entryName)
    {
        if (!File.Exists(sourcePath)) return;
        try
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream dst = entry.Open();
            src.CopyTo(dst);
        }
        catch
        {
            // 单个文件不可读时不中断整个诊断包导出
        }
    }
}
