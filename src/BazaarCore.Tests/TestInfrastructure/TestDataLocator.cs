using System.Text;

namespace BazaarCore.Tests.TestInfrastructure;

/// <summary>
/// 跨机器、跨平台且完整支持 git worktree 的测试数据定位器（单一事实源）。
/// 解析优先级：
/// 1) 显式环境变量（BAZAAR_GAME_DATA_DB / BAZAAR_DATA_DIR / BAZAAR_ZH_CN_PATH 等）；
/// 2) 当前目录 / 测试输出目录向上探测到的当前仓库根 dist/data；
/// 3) 若当前仓库根 .git 是 worktree 指针文件（gitdir: ...），自动按 commondir（以 resolvedGitDir 为基准）
///    或父目录解析主仓根 dist/data。
/// 严禁任何情况下回退读取开发者本机 Steam 安装目录或 AppData/LocalLow/Tempo Storm 缓存。
/// </summary>
public static class TestDataLocator
{
    private const int MaxWalkDepth = 12;

    private static IReadOnlyList<string> DefaultSearchStartDirs =>
        [AppContext.BaseDirectory, Environment.CurrentDirectory];

    /// <summary>定位包含有效 GameData.db 的数据目录（找不到直接抛出异常，绝不回退 Steam）。</summary>
    public static string ResolveDataDir()
        => ResolveDataDirCore(
            Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR"),
            Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB"),
            DefaultSearchStartDirs);

    /// <summary>定位有效的 GameData.db 文件绝对路径（绝不回退 Steam）。</summary>
    public static string ResolveGameDataDbPath()
        => ResolveGameDataDbPathCore(
            Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR"),
            Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB"),
            DefaultSearchStartDirs);

    /// <summary>
    /// 定位有效的官方简体中文词库 translations/zh-CN.bytes 绝对路径（强制非空 fail-fast，绝不返回 null 触发 Steam 回退）。
    /// </summary>
    public static string ResolveZhCnPath()
        => ResolveZhCnPathCore(
            Environment.GetEnvironmentVariable("BAZAAR_ZH_CN_PATH"),
            Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR"),
            Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB"),
            DefaultSearchStartDirs);

    /// <summary>定位包含 ghost_pool_balanced.jsonl 的目录（优先数据目录，其次当前仓/主仓 dist/data）。</summary>
    public static string ResolveGhostPoolDir()
        => ResolveGhostPoolDirCore(
            Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR"),
            Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB"),
            DefaultSearchStartDirs);

    /// <summary>定位 valid_card_ids.json 绝对路径。</summary>
    public static string ResolveValidCardIdsPath()
        => ResolveDataFile("valid_card_ids.json");

    /// <summary>按文件名在权威数据目录（优先 BAZAAR_DATA_DIR / 含 GameData.db 目录，再回退当前仓/主仓 dist/data）中定位文件。</summary>
    public static string ResolveDataFile(string fileName)
        => ResolveDataFileCore(
            fileName,
            Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR"),
            Environment.GetEnvironmentVariable("BAZAAR_GAME_DATA_DB"),
            DefaultSearchStartDirs);

    /// <summary>定位当前工作仓库根目录（worktree 中返回当前 worktree 根）。</summary>
    public static string ResolveRepoRoot()
        => ResolveRepoRootCore(DefaultSearchStartDirs);

    /// <summary>定位主仓根目录（非 worktree 时与当前仓库根相同；worktree 中返回 .git 指针指向的主仓根）。</summary>
    public static string ResolveMainRepoRoot()
        => ResolveMainRepoRootCore(DefaultSearchStartDirs);

    /// <summary>定位包含示例 Mod 的 mods/ 目录（优先当前仓根，缺失时回退主仓根）。</summary>
    public static string ResolveModsRoot()
        => ResolveModsRootCore(DefaultSearchStartDirs);

    /// <summary>定位天梯蒸馏数据 compass_distilled.json（优先 BAZAAR_COMPASS_PATH，其次当前仓/主仓 dist/dataset-worker-win-x64/data）。</summary>
    public static string ResolveCompassDistilledPath()
        => ResolveCompassDistilledPathCore(
            Environment.GetEnvironmentVariable("BAZAAR_COMPASS_PATH"),
            DefaultSearchStartDirs);

    /// <summary>判断给定路径是否落入本机 Steam 安装目录或 Tempo Storm prod/cache 缓存（用于零泄漏断言）。</summary>
    public static bool IsUnderSteamProdCache(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("Tempo Storm/The Bazaar/prod/cache", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("steamapps/common/The Bazaar", StringComparison.OrdinalIgnoreCase);
    }

    // ── 无副作用纯静态解析入口（支持单元测试零修改环境变量独立验证） ──

    public static string ResolveGameDataDbPathCore(
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs)
    {
        if (!string.IsNullOrWhiteSpace(envGameDataDb) && IsNonEmptyFile(envGameDataDb))
            return Path.GetFullPath(envGameDataDb);

        string dataDir = ResolveDataDirCore(envDataDir, envGameDataDb, searchStartDirs);
        string dbPath = Path.Combine(dataDir, "GameData.db");
        if (IsNonEmptyFile(dbPath))
            return Path.GetFullPath(dbPath);

        throw new FileNotFoundException($"已解析数据目录但未找到有效 GameData.db：{dbPath}");
    }

    public static string ResolveDataDirCore(
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs)
    {
        if (TryResolveDataDirCore(envDataDir, envGameDataDb, searchStartDirs, out string? resolved) && resolved != null)
            return resolved;

        throw new FileNotFoundException(
            "未在环境变量 BAZAAR_DATA_DIR / BAZAAR_GAME_DATA_DB、当前仓库 dist/data 或 git worktree 主仓 dist/data 中找到有效 GameData.db。" +
            "测试框架严禁回退读取本机 Steam 缓存，请设置 BAZAAR_DATA_DIR 指向包含 GameData.db 的目录。");
    }

    public static bool TryResolveDataDirCore(
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs,
        out string? resolvedDir)
    {
        if (!string.IsNullOrWhiteSpace(envGameDataDb) && IsNonEmptyFile(envGameDataDb))
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(envGameDataDb));
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                resolvedDir = parent;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(envDataDir))
        {
            string trimmed = envDataDir.Trim();
            if (IsNonEmptyFile(Path.Combine(trimmed, "GameData.db")))
            {
                resolvedDir = Path.GetFullPath(trimmed);
                return true;
            }
        }

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string distData = Path.Combine(repoRoot, "dist", "data");
            if (IsNonEmptyFile(Path.Combine(distData, "GameData.db")))
            {
                resolvedDir = Path.GetFullPath(distData);
                return true;
            }
        }

        resolvedDir = null;
        return false;
    }

    public static string ResolveZhCnPathCore(
        string? envZhCnPath,
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs)
    {
        if (!string.IsNullOrWhiteSpace(envZhCnPath) && IsNonEmptyFile(envZhCnPath))
            return Path.GetFullPath(envZhCnPath);

        if (TryResolveDataDirCore(envDataDir, envGameDataDb, searchStartDirs, out string? dataDir) && dataDir != null)
        {
            string nested = Path.Combine(dataDir, "translations", "zh-CN.bytes");
            if (IsNonEmptyFile(nested))
                return Path.GetFullPath(nested);
            string direct = Path.Combine(dataDir, "zh-CN.bytes");
            if (IsNonEmptyFile(direct))
                return Path.GetFullPath(direct);
        }

        if (!string.IsNullOrWhiteSpace(envDataDir))
        {
            string trimmed = envDataDir.Trim();
            string nested = Path.Combine(trimmed, "translations", "zh-CN.bytes");
            if (IsNonEmptyFile(nested))
                return Path.GetFullPath(nested);
            string direct = Path.Combine(trimmed, "zh-CN.bytes");
            if (IsNonEmptyFile(direct))
                return Path.GetFullPath(direct);
        }

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string candidate = Path.Combine(repoRoot, "dist", "data", "translations", "zh-CN.bytes");
            if (IsNonEmptyFile(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "未在 BAZAAR_ZH_CN_PATH、数据目录或仓库 dist/data/translations/ 中找到有效 zh-CN.bytes。" +
            "为彻底阻断 Steam 缓存污染，测试定位器拒绝返回 null。");
    }

    public static string ResolveGhostPoolDirCore(
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs)
    {
        if (TryResolveDataDirCore(envDataDir, envGameDataDb, searchStartDirs, out string? dataDir)
            && dataDir != null
            && IsNonEmptyFile(Path.Combine(dataDir, "ghost_pool_balanced.jsonl")))
        {
            return dataDir;
        }

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string distData = Path.Combine(repoRoot, "dist", "data");
            if (IsNonEmptyFile(Path.Combine(distData, "ghost_pool_balanced.jsonl")))
                return Path.GetFullPath(distData);
        }

        throw new FileNotFoundException("未在数据目录或仓库/主仓 dist/data 中找到 ghost_pool_balanced.jsonl。");
    }

    public static string ResolveDataFileCore(
        string fileName,
        string? envDataDir,
        string? envGameDataDb,
        IEnumerable<string> searchStartDirs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (string.Equals(fileName, "GameData.db", StringComparison.OrdinalIgnoreCase))
            return ResolveGameDataDbPathCore(envDataDir, envGameDataDb, searchStartDirs);

        if (TryResolveDataDirCore(envDataDir, envGameDataDb, searchStartDirs, out string? dataDir) && dataDir != null)
        {
            string direct = Path.Combine(dataDir, fileName);
            if (File.Exists(direct))
                return Path.GetFullPath(direct);
        }

        if (!string.IsNullOrWhiteSpace(envDataDir))
        {
            string envCandidate = Path.Combine(envDataDir.Trim(), fileName);
            if (File.Exists(envCandidate))
                return Path.GetFullPath(envCandidate);
        }

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string candidate = Path.Combine(repoRoot, "dist", "data", fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException($"未在数据目录或仓库/主仓 dist/data 中找到文件：{fileName}");
    }

    public static string ResolveRepoRootCore(IEnumerable<string> searchStartDirs)
    {
        foreach (string root in DiscoverCandidateRepoRoots(searchStartDirs))
            return root;

        throw new DirectoryNotFoundException("无法从测试工作目录或输出目录向上定位仓库根目录。");
    }

    public static string ResolveMainRepoRootCore(IEnumerable<string> searchStartDirs)
    {
        var roots = DiscoverCandidateRepoRoots(searchStartDirs);
        if (roots.Count == 0)
            throw new DirectoryNotFoundException("无法定位主仓库根目录。");

        // 若首个仓库根是 git worktree，优先返回解析到的主仓根
        if (TryResolveMainRepoRootFromWorktree(roots[0], out string? mainRoot) && mainRoot != null)
            return mainRoot;

        return roots[0];
    }

    public static string ResolveModsRootCore(IEnumerable<string> searchStartDirs)
    {
        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string modsDir = Path.Combine(repoRoot, "mods");
            if (Directory.Exists(modsDir) && Directory.Exists(Path.Combine(modsDir, "bazaar-chronicles-sample")))
                return Path.GetFullPath(modsDir);
        }

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string modsDir = Path.Combine(repoRoot, "mods");
            if (Directory.Exists(modsDir))
                return Path.GetFullPath(modsDir);
        }

        throw new DirectoryNotFoundException("未在当前仓库或主仓根目录找到 mods/ 目录。");
    }

    public static string ResolveCompassDistilledPathCore(
        string? envCompassPath,
        IEnumerable<string> searchStartDirs)
    {
        if (!string.IsNullOrWhiteSpace(envCompassPath) && File.Exists(envCompassPath))
            return Path.GetFullPath(envCompassPath);

        foreach (string repoRoot in DiscoverCandidateRepoRoots(searchStartDirs))
        {
            string workerData = Path.Combine(repoRoot, "dist", "dataset-worker-win-x64", "data", "compass_distilled.json");
            if (File.Exists(workerData))
                return Path.GetFullPath(workerData);

            string distData = Path.Combine(repoRoot, "dist", "data", "compass_distilled.json");
            if (File.Exists(distData))
                return Path.GetFullPath(distData);
        }

        throw new FileNotFoundException("未在 BAZAAR_COMPASS_PATH、当前仓库或主仓中找到 compass_distilled.json。");
    }

    /// <summary>
    /// 向上扫描候选仓库根目录；遇到 git worktree（.git 为指针文件）时自动紧随其后追加解析出的主仓根。
    /// </summary>
    public static IReadOnlyList<string> DiscoverCandidateRepoRoots(IEnumerable<string> searchStartDirs)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void TryAdd(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string full;
            try { full = TrimTrailingSeparators(Path.GetFullPath(dir)); }
            catch { return; }
            if (Directory.Exists(full) && seen.Add(full))
                result.Add(full);
        }

        foreach (string start in searchStartDirs)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? cur;
            try { cur = new DirectoryInfo(start); }
            catch { continue; }

            for (int depth = 0; depth < MaxWalkDepth && cur != null; depth++, cur = cur.Parent)
            {
                string dirPath = cur.FullName;
                string dotGit = Path.Combine(dirPath, ".git");
                bool isRepoMarker = File.Exists(dotGit)
                    || Directory.Exists(dotGit)
                    || File.Exists(Path.Combine(dirPath, "Bazaar.sln"))
                    || Directory.Exists(Path.Combine(dirPath, "dist", "data"));

                if (!isRepoMarker) continue;

                TryAdd(dirPath);

                if (File.Exists(dotGit) && TryResolveMainRepoRootFromWorktree(dirPath, out string? mainRoot))
                    TryAdd(mainRoot);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 git worktree 的 .git 指针文件，返回主仓根目录：
    /// 1) 读取 gitdir: 行并清洗空白/引号/换行，相对路径以 worktreeRoot 为基准解析得到 resolvedGitDir；
    /// 2) 若 resolvedGitDir/commondir 存在，严格以 resolvedGitDir 为基准解析 commonGitDir，其父目录即为主仓根；
    /// 3) 若无 commondir，沿 resolvedGitDir 向上定位 .git 目录，取其父目录作为主仓根。
    /// </summary>
    public static bool TryResolveMainRepoRootFromWorktree(string worktreeRoot, out string? mainRepoRoot)
    {
        mainRepoRoot = null;
        if (string.IsNullOrWhiteSpace(worktreeRoot)) return false;

        string dotGitFile = Path.Combine(worktreeRoot, ".git");
        if (!File.Exists(dotGitFile)) return false;

        string? rawGitDir = null;
        try
        {
            foreach (string line in File.ReadLines(dotGitFile, Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    rawGitDir = CleanGitPointerPath(trimmed["gitdir:".Length..]);
                    break;
                }
            }
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawGitDir)) return false;

        string resolvedGitDir;
        try
        {
            resolvedGitDir = Path.IsPathRooted(rawGitDir)
                ? Path.GetFullPath(rawGitDir)
                : Path.GetFullPath(Path.Combine(worktreeRoot, rawGitDir));
            resolvedGitDir = TrimTrailingSeparators(resolvedGitDir);
        }
        catch
        {
            return false;
        }

        // 优先遵循 git 标准 commondir 文件（相对路径严格以 resolvedGitDir 为基准）
        string commonDirFile = Path.Combine(resolvedGitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            try
            {
                string? commonRaw = File.ReadLines(commonDirFile, Encoding.UTF8)
                    .Select(CleanGitPointerPath)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(commonRaw))
                {
                    string commonGitDir = Path.IsPathRooted(commonRaw)
                        ? Path.GetFullPath(commonRaw)
                        : Path.GetFullPath(Path.Combine(resolvedGitDir, commonRaw));
                    commonGitDir = TrimTrailingSeparators(commonGitDir);

                    string? parent = Directory.GetParent(commonGitDir)?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    {
                        mainRepoRoot = TrimTrailingSeparators(Path.GetFullPath(parent));
                        return true;
                    }
                }
            }
            catch
            {
                // commondir 无法读取时回退至目录结构解析
            }
        }

        // 回退：沿 resolvedGitDir 向上定位名为 .git 的目录（典型 <main>/.git/worktrees/<name>）
        try
        {
            var probe = new DirectoryInfo(resolvedGitDir);
            for (int depth = 0; depth < 6 && probe != null; depth++, probe = probe.Parent)
            {
                if (string.Equals(probe.Name, ".git", StringComparison.OrdinalIgnoreCase) && probe.Parent != null)
                {
                    if (Directory.Exists(probe.Parent.FullName))
                    {
                        mainRepoRoot = TrimTrailingSeparators(Path.GetFullPath(probe.Parent.FullName));
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string CleanGitPointerPath(string raw)
    {
        string cleaned = raw.Trim().Trim('\r', '\n', ' ', '\t');
        if (cleaned.Length >= 2 &&
            ((cleaned[0] == '"' && cleaned[^1] == '"') || (cleaned[0] == '\'' && cleaned[^1] == '\'')))
        {
            cleaned = cleaned[1..^1].Trim();
        }
        return cleaned.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string TrimTrailingSeparators(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (path.Length <= root.Length) return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
