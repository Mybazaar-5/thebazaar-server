using System.Text.RegularExpressions;

namespace BazaarCore.Mods;

/// <summary>
/// 跨平台安全路径与标识符守卫（全仓统一单一事实源，P0-34）：
/// - 严格校验 Mod ID 与 SHA-256 十六进制字符串白名单，彻底阻断路径穿越与任意目录删改；
/// - 统一先归一化反斜杠再判断目录/相对路径，消除 Windows 打包 ZIP 在 Linux 崩溃；
/// - 按宿主机 OS 语义选择路径前缀比较器（Windows 忽略大小写、Linux/macOS 区分大小写）并强制目录尾分隔符守卫，杜绝兄弟目录前缀绕过；
/// - 拦截 Windows 保留设备名、驱动器盘符、UNC、ADS 冒号流与控制字符。
/// </summary>
public static partial class SafePathGuard
{
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$|^[a-z0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictModIdRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexRegex();

    /// <summary>当前操作系统的路径前缀比较规则（Linux/macOS 区分大小写防大小写混淆穿越）。</summary>
    public static StringComparison OsPathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>严格校验 Mod ID（2~64 位小写字母、数字或连字符，与 ModValidator 唯一源对齐）。</summary>
    public static bool IsValidModId(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return false;
        string trimmed = modId.Trim();
        return StrictModIdRegex().IsMatch(trimmed) && !IsWindowsReservedSegment(trimmed);
    }

    /// <summary>严格校验 64 位十六进制 SHA-256，并输出规范小写串。</summary>
    public static bool TryNormalizeSha256Hex(string? rawSha256, out string normalizedSha256)
    {
        normalizedSha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(rawSha256))
            return false;
        string trimmed = rawSha256.Trim();
        if (!Sha256HexRegex().IsMatch(trimmed))
            return false;
        normalizedSha256 = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>判断是否为合法 64 位十六进制 SHA-256。</summary>
    public static bool IsValidSha256Hex(string? rawSha256)
        => TryNormalizeSha256Hex(rawSha256, out _);

    /// <summary>
    /// 规范化 ZIP 条目路径：先将反斜杠 <c>\</c> 统一替换为正斜杠 <c>/</c>，再精确判定是否为目录条目，
    /// 彻底解决 Windows 打包的 <c>dir\</c> 目录条目在 Linux 上未命中 <c>EndsWith('/')</c> 而崩溃的问题。
    /// </summary>
    public static bool TryNormalizeZipEntryPath(string? rawEntryName, out string normalizedPath, out bool isDirectory)
    {
        normalizedPath = string.Empty;
        isDirectory = false;

        if (string.IsNullOrEmpty(rawEntryName))
            return false;

        // 严禁 NUL 字节与控制字符
        for (int i = 0; i < rawEntryName.Length; i++)
        {
            char c = rawEntryName[i];
            if (c == '\0' || char.IsControl(c))
                return false;
        }

        string unified = rawEntryName.Replace('\\', '/');
        isDirectory = unified.EndsWith('/');

        string candidate = unified;
        while (candidate.StartsWith("./", StringComparison.Ordinal))
            candidate = candidate[2..];

        if (isDirectory)
        {
            candidate = candidate.TrimEnd('/');
            if (candidate.Length == 0)
            {
                // 根占位目录条目（如 "./" 或 "/"）仅当原串不含绝对根穿越时跳过
                if (unified.StartsWith("/", StringComparison.Ordinal))
                    return false;
                normalizedPath = string.Empty;
                return true;
            }
        }

        if (!IsSafeRelativePath(candidate))
            return false;

        normalizedPath = candidate;
        return true;
    }

    /// <summary>将逻辑路径中的反斜杠统一转为正斜杠并剥离前导 <c>./</c>。</summary>
    public static string NormalizeLogicalPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        string unified = rawPath.Trim().Replace('\\', '/');
        while (unified.StartsWith("./", StringComparison.Ordinal))
            unified = unified[2..];
        return unified;
    }

    /// <summary>
    /// 严格校验相对逻辑路径安全性：
    /// 禁止空串、绝对路径、盘符/ADS 冒号、控制符、空段（双斜杠）、<c>.</c>/<c>..</c> 段、尾空格/尾点与 Windows 保留设备名。
    /// </summary>
    public static bool IsSafeRelativePath(string? normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
            return false;

        string path = normalizedRelativePath;
        if (path.StartsWith('/') || path.StartsWith('\\') || path.EndsWith('/') || path.EndsWith('\\'))
            return false;

        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '\0' || c == ':' || c == '\\' || char.IsControl(c))
                return false;
        }

        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (string.IsNullOrEmpty(seg))
                return false;
            if (seg == "." || seg == "..")
                return false;
            if (seg.EndsWith(' ') || seg.EndsWith('.'))
                return false;
            if (IsWindowsReservedSegment(seg))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 在指定根目录 <paramref name="rootDir"/> 下安全解析相对路径，防止路径穿越与兄弟目录前缀绕过。
    /// </summary>
    public static bool TryResolveUnderRoot(string rootDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootDir))
            return false;

        string normalized = NormalizeLogicalPath(relativePath);
        if (!IsSafeRelativePath(normalized))
            return false;

        string rootFull = EnsureTrailingSeparator(Path.GetFullPath(rootDir));
        string candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootFull, OsPathComparison))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>确保规范化根目录路径以平台目录分隔符结尾（防 <c>/tmp/root</c> 匹配 <c>/tmp/root_evil</c>）。</summary>
    public static string EnsureTrailingSeparator(string fullRootPath)
    {
        string trimmed = fullRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static bool IsWindowsReservedSegment(string segment)
    {
        int dotIdx = segment.IndexOf('.');
        string stem = dotIdx >= 0 ? segment[..dotIdx] : segment;
        return WindowsReservedDeviceNames.Contains(stem);
    }
}
