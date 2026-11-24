using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace BazaarHub.Services;

/// <summary>
/// BazaarHub 路径穿越、SHA-256、HTTP 流式限长与跨平台 Zip 安全守卫（与 BazaarLauncher 同构）。
/// </summary>
public static partial class SafePathAndZipGuard
{
    public const long MaxCommunityZipBytes = 16L * 1024 * 1024;
    public const long MaxAssetImageBytes = 8L * 1024 * 1024;
    public const long MaxCorePayloadFileBytes = 64L * 1024 * 1024;
    public const int DefaultMaxZipEntries = 500;
    public const long DefaultMaxSingleFileBytes = 16L * 1024 * 1024;
    public const long DefaultMaxTotalExtractedBytes = 64L * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdRegex();

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

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    public static bool IsValidModId(string? modId)
    {
        if (string.IsNullOrEmpty(modId) || modId.Length > 64)
            return false;
        if (!ModIdRegex().IsMatch(modId))
            return false;
        if (modId.Contains("..", StringComparison.Ordinal))
            return false;
        if (modId.EndsWith('.') || modId.EndsWith(' '))
            return false;
        if (IsReservedDeviceName(modId))
            return false;
        return true;
    }

    public static bool TryNormalizeSha256Hex(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string s = raw.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            s = s["sha256:".Length..].Trim();
        }

        if (s.Length != 64)
            return false;

        Span<char> chars = stackalloc char[64];
        for (int i = 0; i < 64; i++)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
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

    public static string NormalizeAllowedImageExtension(string? pathOrExt)
    {
        string ext = Path.GetExtension(pathOrExt ?? string.Empty);
        return !string.IsNullOrEmpty(ext) && AllowedImageExtensions.Contains(ext)
            ? ext.ToLowerInvariant()
            : ".png";
    }

    public static bool IsForbiddenExtension(string? pathOrName)
    {
        string ext = Path.GetExtension(pathOrName ?? string.Empty);
        return !string.IsNullOrEmpty(ext) && ForbiddenZipExtensions.Contains(ext);
    }

    public static string ResolveSafeChildPath(string rootDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            throw new InvalidOperationException("根目录不能为空。");
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("相对路径不能为空。");
        if (relativePath.Contains('\0'))
            throw new InvalidOperationException("相对路径包含非法空字符。");
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
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
            if (seg.EndsWith('.') || seg.EndsWith(' '))
                throw new InvalidOperationException($"路径段包含非法尾随点或空格，已拦截：{relativePath}");
            if (IsReservedDeviceName(seg))
                throw new InvalidOperationException($"路径段命中系统保留设备名，已拦截：{relativePath}");

            foreach (char ch in seg)
            {
                if (char.IsControl(ch))
                    throw new InvalidOperationException($"路径段包含控制字符，已拦截：{relativePath}");
            }
        }

        string fullRoot = Path.GetFullPath(rootDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string combined = Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar, segments));
        string candidate = Path.GetFullPath(combined);

        if (!candidate.StartsWith(fullRoot, PathComparison) ||
            string.Equals(candidate, fullRoot, PathComparison))
        {
            throw new InvalidOperationException($"检测到路径穿越风险，已拦截：{relativePath}");
        }

        return candidate;
    }

    public static async Task<byte[]> ReadHttpContentWithLimitAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken ct)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maxBytes)
        {
            throw new InvalidDataException($"响应体大小 ({declaredLength.Value} B) 超过安全上限 ({maxBytes} B)。");
        }

        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(declaredLength.HasValue && declaredLength.Value > 0
            ? (int)Math.Min(declaredLength.Value, maxBytes)
            : 16384);
        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidDataException($"下载响应流超过安全上限 ({maxBytes} B)，已中止读取。");
            }
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    public static string ExtractModZipSafely(
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
        string nonce = Guid.NewGuid().ToString("N")[..12];
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

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string rawName = entry.FullName ?? string.Empty;
                    string normalized = rawName.Replace('\\', '/');
                    if (normalized.EndsWith("/", StringComparison.Ordinal))
                        continue;

                    string rel = normalized.TrimStart('/');
                    if (string.IsNullOrWhiteSpace(rel))
                        continue;

                    if (rel.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = rel[modPrefix.Length..];
                    }
                    if (string.IsNullOrWhiteSpace(rel))
                        continue;

                    if (IsForbiddenExtension(rel))
                    {
                        throw new InvalidDataException($"Mod 压缩包包含禁止的可执行或脚本文件（已整包拒收）：{rel}");
                    }

                    string dst = ResolveSafeChildPath(tempDir, rel);
                    string? dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir))
                    {
                        Directory.CreateDirectory(dstDir);
                    }

                    long entryBytes = 0;
                    using (Stream src = entry.Open())
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
        string baseName = dotIdx >= 0 ? segment[..dotIdx] : segment;
        return WindowsReservedDeviceNames.Contains(baseName);
    }
}
