using System.IO.Compression;

namespace BazaarCore.Mods;

/// <summary>
/// <see cref="SafeZipExtractor"/> 安全解压配额与策略选项。
/// </summary>
public sealed record SafeZipExtractOptions(
    int MaxEntries,
    long MaxTotalUncompressedBytes,
    long MaxSingleFileBytes,
    IReadOnlySet<string>? ForbiddenExtensions = null,
    bool RejectSymlinks = true);

/// <summary>
/// <see cref="SafeZipExtractor"/> 解压结果。
/// </summary>
public sealed record SafeZipExtractResult(
    bool Ok,
    string? ErrorZh,
    int FilesExtracted,
    long TotalBytesExtracted);

/// <summary>
/// 跨平台安全 ZIP 解压器（全仓统一防线，P0-34）：
/// 1. 先经 <see cref="SafePathGuard.TryNormalizeZipEntryPath"/> 统一将 <c>\</c> 替换为 <c>/</c> 再判定目录条目，根治 Windows ZIP 在 Linux 崩溃；
/// 2. 对目录与文件条目双向执行 Zip Slip、盘符、Windows 保留设备名与根目录尾分隔符前缀守卫；
/// 3. 拦截 Unix 符号链接条目与大小写碰撞重复覆盖条目；
/// 4. 绝不轻信可伪造的 <c>entry.Length</c>，跨所有条目累计真实解压流读取字节数，彻底根治跨条目 Zip Bomb。
/// </summary>
public static class SafeZipExtractor
{
    private const int CopyBufferSize = 16 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlinkType = 0xA000;

    public static SafeZipExtractResult ExtractToDirectory(
        byte[] zipBytes,
        string destRoot,
        SafeZipExtractOptions options)
    {
        if (zipBytes == null || zipBytes.Length == 0)
            return new SafeZipExtractResult(false, "压缩包为空，里面没有文件。", 0, 0);

        using var ms = new MemoryStream(zipBytes, writable: false);
        return ExtractToDirectory(ms, destRoot, options);
    }

    public static SafeZipExtractResult ExtractToDirectory(
        Stream zipStream,
        string destRoot,
        SafeZipExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(destRoot))
            return new SafeZipExtractResult(false, "目标解压目录不能为空。", 0, 0);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0)
            return new SafeZipExtractResult(false, "压缩包为空，里面没有文件。", 0, 0);

        if (archive.Entries.Count > options.MaxEntries)
            return new SafeZipExtractResult(false, $"压缩包条目数过多（{archive.Entries.Count} > {options.MaxEntries}）。", 0, 0);

        Directory.CreateDirectory(destRoot);

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buf = new byte[CopyBufferSize];
        long totalActualRead = 0;
        int filesExtracted = 0;

        foreach (var entry in archive.Entries)
        {
            // 1. 拦截 Unix 符号链接条目（防软链逃逸）
            if (options.RejectSymlinks && IsUnixSymlink(entry))
                return new SafeZipExtractResult(false, $"安全拦截：压缩包包含禁止的符号链接条目 '{entry.FullName}'。", filesExtracted, totalActualRead);

            // 2. 跨平台路径归一化：先 Replace('\\', '/') 再判目录，且目录条目自身也受安全相对路径校验
            if (!SafePathGuard.TryNormalizeZipEntryPath(entry.FullName, out string relPath, out bool isDirectory))
                return new SafeZipExtractResult(false, $"安全拦截：压缩包包含非法路径穿越条目 '{entry.FullName}'。", filesExtracted, totalActualRead);

            if (isDirectory)
                continue;

            // 3. 拦截大小写碰撞 / 重复路径恶意覆盖
            if (!seenPaths.Add(relPath))
                return new SafeZipExtractResult(false, $"安全拦截：压缩包包含重复或大小写冲突的文件条目 '{relPath}'。", filesExtracted, totalActualRead);

            // 4. 拦截可执行/脚本后缀黑名单
            if (options.ForbiddenExtensions != null)
            {
                string ext = Path.GetExtension(relPath);
                if (!string.IsNullOrEmpty(ext) && options.ForbiddenExtensions.Contains(ext))
                    return new SafeZipExtractResult(false, $"出于安全限制，不允许上传可执行或脚本文件：{relPath}", filesExtracted, totalActualRead);
            }

            // 5. 快速剪枝异常 Header 声明体积（但绝不信任其作为唯一上限，下方严格以真实流读取字节为准）
            if (entry.Length < 0 || entry.Length > options.MaxSingleFileBytes)
                return new SafeZipExtractResult(false, $"安全拦截：单文件声明体积超出上限（{FormatMb(options.MaxSingleFileBytes)}）。", filesExtracted, totalActualRead);

            // 6. 根目录前缀边界解析（按 OS 大小写语义 + 尾分隔符守卫）
            if (!SafePathGuard.TryResolveUnderRoot(destRoot, relPath, out string destFile))
                return new SafeZipExtractResult(false, $"安全拦截：目标解压路径越界 '{entry.FullName}'。", filesExtracted, totalActualRead);

            string? parentDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            // 7. 严格累计真实解压流读取字节（单文件 + 跨条目全局双重硬截断，根治伪造 Header Zip Bomb）
            using var src = entry.Open();
            using var dst = File.Create(destFile);
            long fileActualRead = 0;
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                fileActualRead += read;
                totalActualRead += read;

                if (fileActualRead > options.MaxSingleFileBytes)
                    return new SafeZipExtractResult(false, $"安全拦截：单文件解压流超出上限（{FormatMb(options.MaxSingleFileBytes)}，Zip Bomb 防护）。", filesExtracted, totalActualRead);

                if (totalActualRead > options.MaxTotalUncompressedBytes)
                    return new SafeZipExtractResult(false, $"安全拦截：压缩包累计解压总体积超出上限（{FormatMb(options.MaxTotalUncompressedBytes)}，Zip Bomb 防护）。", filesExtracted, totalActualRead);

                dst.Write(buf, 0, read);
            }

            filesExtracted++;
        }

        if (filesExtracted == 0)
            return new SafeZipExtractResult(false, "压缩包中未包含任何有效文件。", 0, 0);

        return new SafeZipExtractResult(true, null, filesExtracted, totalActualRead);
    }

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        int unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymlinkType;
    }

    private static string FormatMb(long bytes)
        => bytes >= 1024L * 1024L ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{bytes} B";
}
