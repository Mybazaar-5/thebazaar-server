using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BazaarLauncher.Core;

/// <summary>
/// 客户端纯 BCL（零 Unity / BepInEx 依赖）路径穿越、SHA-256、HTTP 流式下载限长与跨平台 Zip 安全守卫。
/// 覆盖 P0-34 全部防线：
/// 1. modId 严格字符白名单 + Windows 保留设备名 + 尾点拦截；
/// 2. sha256 严格 64 位小写十六进制校验；
/// 3. OS 感知根目录前缀断言（Windows OrdinalIgnoreCase / Linux Ordinal）；
/// 4. 先归一化反斜杠再判目录条目，彻底消灭 Linux 下 Windows Zip 目录崩溃；
/// 5. 社区 ZIP 危险扩展名 Fail-Closed 整包拒收 + 跨条目真实解压流 Zip Bomb 配额 + 同分区原子替换/自动回滚。
/// </summary>
internal static class SafePathAndZipGuard
{
    internal const long MaxCommunityZipBytes = 16L * 1024 * 1024;
    internal const long MaxAssetImageBytes = 8L * 1024 * 1024;
    internal const int DefaultMaxZipEntries = 500;
    internal const long DefaultMaxSingleFileBytes = 16L * 1024 * 1024;
    internal const long DefaultMaxTotalExtractedBytes = 64L * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly Regex ModIdRegex = new(
        "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> ForbiddenZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".vbs",
        ".msi", ".so", ".dylib", ".com", ".scr", ".pif", ".hta", ".jar"
    };

    /// <summary>校验来自网络、清单或本地 mod.json 的 modId 是否严格满足安全文件名白名单。</summary>
    internal static bool IsValidModId(string modId)
    {
        if (string.IsNullOrEmpty(modId) || modId.Length > 64)
            return false;
        if (!ModIdRegex.IsMatch(modId))
            return false;
        if (modId.IndexOf("..", StringComparison.Ordinal) >= 0)
            return false;
        if (modId.EndsWith(".", StringComparison.Ordinal) || modId.EndsWith(" ", StringComparison.Ordinal))
            return false;
        if (IsReservedDeviceName(modId))
            return false;
        return true;
    }

    /// <summary>严格校验并规范化 64 位小写十六进制 SHA-256（允许可选 sha256: 前缀）。</summary>
    internal static bool TryNormalizeSha256Hex(string raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string s = raw.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(7).Trim();
        }

        if (s.Length != 64)
            return false;

        char[] chars = new char[64];
        for (int i = 0; i < 64; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
            {
                chars[i] = c;
            }
            else if (c >= 'a' && c <= 'f')
            {
                chars[i] = c;
            }
            else if (c >= 'A' && c <= 'F')
            {
                chars[i] = (char)(c + ('a' - 'A'));
            }
            else
            {
                return false;
            }
        }

        normalized = new string(chars);
        return true;
    }

    /// <summary>校验并解析相对于 rootDir 的安全子路径，严格阻断路径穿越、盘符/ADS、控制字符与 Windows 保留名。</summary>
    internal static string ResolveSafeChildPath(string rootDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            throw new InvalidOperationException("根目录不能为空。");
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("相对路径不能为空。");
        if (relativePath.IndexOf('\0') >= 0)
            throw new InvalidOperationException("相对路径包含非法空字符。");
        if (Path.IsPathRooted(relativePath) || relativePath.IndexOf(':') >= 0)
            throw new InvalidOperationException($"检测到绝对路径或非法冒号字符，已拦截：{relativePath}");

        string normalized = relativePath.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (segments.Length == 0)
            throw new InvalidOperationException($"无效的相对路径：{relativePath}");

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (string.IsNullOrEmpty(seg) || seg == "." || seg == "..")
                throw new InvalidOperationException($"检测到路径穿越或空路径段，已拦截：{relativePath}");
            if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal))
                throw new InvalidOperationException($"路径段包含非法尾随点或空格，已拦截：{relativePath}");
            if (IsReservedDeviceName(seg))
                throw new InvalidOperationException($"路径段命中系统保留设备名，已拦截：{relativePath}");

            for (int c = 0; c < seg.Length; c++)
            {
                if (char.IsControl(seg[c]))
                    throw new InvalidOperationException($"路径段包含控制字符，已拦截：{relativePath}");
            }
        }

        string fullRoot = Path.GetFullPath(rootDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string combined = Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar.ToString(), segments));
        string candidate = Path.GetFullPath(combined);

        if (!candidate.StartsWith(fullRoot, PathComparison) ||
            string.Equals(candidate, fullRoot, PathComparison))
        {
            throw new InvalidOperationException($"检测到越界路径访问，已拦截：{relativePath}");
        }

        return candidate;
    }

    /// <summary>带真实流式读取上限的 HTTP 响应体读取（不轻信可伪造的 Content-Length，防 OOM）。</summary>
    internal static async Task<byte[]> ReadHttpContentWithLimitAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken ct)
    {
        if (content == null)
            return Array.Empty<byte>();
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maxBytes)
        {
            throw new InvalidDataException($"响应体大小 ({declaredLength.Value} B) 超过安全上限 ({maxBytes} B)。");
        }

        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var ms = new MemoryStream(declaredLength.HasValue && declaredLength.Value > 0
            ? (int)Math.Min(declaredLength.Value, maxBytes)
            : 16384);
        byte[] buffer = new byte[81920];
        long totalRead = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (read <= 0) break;
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidDataException($"下载响应流超过安全上限 ({maxBytes} B)，已中止读取。");
            }
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    /// <summary>检查扩展名是否属于禁止出现在社区 Mod 包中的可执行/脚本后缀。</summary>
    internal static bool IsForbiddenExtension(string pathOrName)
    {
        string ext = Path.GetExtension(pathOrName ?? string.Empty);
        return !string.IsNullOrEmpty(ext) && ForbiddenZipExtensions.Contains(ext);
    }

    /// <summary>
    /// 安全解压社区 Mod ZIP 到 allowedModsRoot/&lt;modId&gt;：
    /// 强制校验 modId 与目标边界、跨平台归一化反斜杠目录、对禁止可执行扩展名 Fail-Closed 整包拒收、
    /// 执行跨条目真实读取字节配额防 Zip Bomb，并通过同分区备份目录实现原子替换与失败自动回滚。
    /// </summary>
    internal static string ExtractModZipSafely(
        byte[] zipBytes,
        string allowedModsRoot,
        string modId,
        int maxEntries = DefaultMaxZipEntries,
        long maxSingleFileBytes = DefaultMaxSingleFileBytes,
        long maxTotalExtractedBytes = DefaultMaxTotalExtractedBytes)
    {
        if (zipBytes == null || zipBytes.Length == 0)
            throw new InvalidDataException("压缩包内容为空。");
        if (string.IsNullOrWhiteSpace(allowedModsRoot))
            throw new InvalidOperationException("Mod 根目录不能为空。");
        if (!IsValidModId(modId))
            throw new InvalidOperationException($"非法的 Mod ID（已拦截路径穿越风险）：{modId}");

        Directory.CreateDirectory(allowedModsRoot);
        string targetDir = ResolveSafeChildPath(allowedModsRoot, modId);
        string nonce = Guid.NewGuid().ToString("N").Substring(0, 12);
        string tempDir = Path.Combine(allowedModsRoot, $".tmp_{modId}_{nonce}");
        string backupDir = Path.Combine(allowedModsRoot, $".bak_{modId}_{nonce}");
        bool movedExistingToBackup = false;

        try
        {
            Directory.CreateDirectory(tempDir);
            int extractedFileCount = 0;
            long totalExtractedBytes = 0;
            byte[] buffer = new byte[81920];
            string modPrefix = modId + "/";

            using (var ms = new MemoryStream(zipBytes, writable: false))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > maxEntries)
                {
                    throw new InvalidDataException($"ZIP 条目数 ({archive.Entries.Count}) 超过安全上限 ({maxEntries})，已拒绝解压。");
                }

                foreach (var entry in archive.Entries)
                {
                    string rawName = entry.FullName ?? string.Empty;
                    string normalized = rawName.Replace('\\', '/');
                    // 先归一化反斜杠再判目录条目，彻底避免 Linux 下 Windows `dir\` 条目触发 File.Create 崩溃
                    if (normalized.EndsWith("/", StringComparison.Ordinal))
                        continue;

                    string rel = normalized.TrimStart('/');
                    if (string.IsNullOrWhiteSpace(rel))
                        continue;

                    if (rel.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = rel.Substring(modPrefix.Length);
                    }
                    if (string.IsNullOrWhiteSpace(rel))
                        continue;

                    if (IsForbiddenExtension(rel))
                    {
                        throw new InvalidDataException($"Mod 压缩包包含禁止的可执行或脚本文件（已整包拒收）：{rel}");
                    }

                    string dst = ResolveSafeChildPath(tempDir, rel);
                    string dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir))
                    {
                        Directory.CreateDirectory(dstDir);
                    }

                    long entryBytes = 0;
                    using (var src = entry.Open())
                    using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            entryBytes += read;
                            totalExtractedBytes += read;
                            if (entryBytes > maxSingleFileBytes)
                            {
                                throw new InvalidDataException($"压缩包内单文件 {rel} 解压大小超过上限 ({maxSingleFileBytes} B)。");
                            }
                            if (totalExtractedBytes > maxTotalExtractedBytes)
                            {
                                throw new InvalidDataException($"压缩包累计解压总大小超过上限 ({maxTotalExtractedBytes} B)，疑似 Zip Bomb。");
                            }
                            fs.Write(buffer, 0, read);
                        }
                    }

                    extractedFileCount++;
                }
            }

            if (extractedFileCount == 0)
            {
                throw new InvalidDataException("压缩包内不包含任何有效 Mod 文件。");
            }

            if (Directory.Exists(targetDir))
            {
                Directory.Move(targetDir, backupDir);
                movedExistingToBackup = true;
            }

            try
            {
                Directory.Move(tempDir, targetDir);
            }
            catch
            {
                if (movedExistingToBackup && Directory.Exists(backupDir) && !Directory.Exists(targetDir))
                {
                    try { Directory.Move(backupDir, targetDir); } catch { }
                }
                throw;
            }

            if (movedExistingToBackup && Directory.Exists(backupDir))
            {
                try { Directory.Delete(backupDir, recursive: true); } catch { }
            }

            return targetDir;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return false;
        int dotIdx = segment.IndexOf('.');
        string baseName = dotIdx >= 0 ? segment.Substring(0, dotIdx) : segment;
        return WindowsReservedDeviceNames.Contains(baseName);
    }
}
