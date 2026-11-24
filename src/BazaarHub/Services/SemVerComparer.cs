namespace BazaarHub.Services;

/// <summary>
/// 轻量零外部依赖 SemVer / Windows 四段 FileVersion 比较器（D7-P1-05）。
/// 支持可选前导 'v'/'V'、3 段 SemVer（15.4.0）、4 段 Windows FileVersion（15.4.0.0，与 15.4.0 等价）、
/// 预发布标识（15.4.0-beta.1 &lt; 15.4.0）以及构建元数据忽略（+gitsha）。
/// </summary>
public readonly record struct SemVerVersion(
    int Major,
    int Minor,
    int Patch,
    int Revision,
    string PreRelease) : IComparable<SemVerVersion>
{
    public int CompareTo(SemVerVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;

        bool thisHasPre = !string.IsNullOrEmpty(PreRelease);
        bool otherHasPre = !string.IsNullOrEmpty(other.PreRelease);
        if (!thisHasPre && !otherHasPre) return 0;
        if (!thisHasPre) return 1;   // 正式版 > 预发布版
        if (!otherHasPre) return -1;

        string[] a = PreRelease.Split('.');
        string[] b = other.PreRelease.Split('.');
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            bool aNum = int.TryParse(a[i], out int ai);
            bool bNum = int.TryParse(b[i], out int bi);
            if (aNum && bNum)
            {
                int nc = ai.CompareTo(bi);
                if (nc != 0) return nc;
            }
            else if (aNum != bNum)
            {
                return aNum ? -1 : 1;
            }
            else
            {
                int sc = StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
                if (sc != 0) return sc;
            }
        }
        return 0;
    }
}

public static class SemVerComparer
{
    public static bool TryParse(string? raw, out SemVerVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..].Trim();
        }

        // 忽略 +metadata（如 +abc123）
        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
        {
            s = s[..plusIdx].Trim();
        }

        string preRelease = string.Empty;
        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            preRelease = s[(dashIdx + 1)..].Trim();
            s = s[..dashIdx].Trim();
        }

        string[] parts = s.Split('.');
        if (parts.Length < 2 || parts.Length > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out int major) || major < 0) return false;
        if (!int.TryParse(parts[1], out int minor) || minor < 0) return false;
        int patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;
        int rev = 0;
        if (parts.Length >= 4 && (!int.TryParse(parts[3], out rev) || rev < 0)) return false;

        version = new SemVerVersion(major, minor, patch, rev, preRelease);
        return true;
    }

    /// <summary>
    /// 比较两个版本字符串；任意一方无法解析时返回 0。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        if (!TryParse(left, out var lv) || !TryParse(right, out var rv))
        {
            return 0;
        }
        return lv.CompareTo(rv);
    }

    public static bool IsLessThan(string? left, string? right)
        => TryParse(left, out var lv) && TryParse(right, out var rv) && lv.CompareTo(rv) < 0;

    public static bool IsGreaterThan(string? left, string? right)
        => TryParse(left, out var lv) && TryParse(right, out var rv) && lv.CompareTo(rv) > 0;
}
