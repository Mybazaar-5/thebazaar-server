using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarServer.Mods;

/// <summary>
/// 服务端已发现 Mod 的综合运行态摘要（供 /api/mods/manifest 与管理接口输出）。
/// </summary>
public sealed record DiscoveredModStatus(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Scope,
    bool Enabled,
    bool Loaded,
    bool AffectsGhostPool,
    string PackageHash,
    int CustomCardCount,
    int CustomEventCount,
    int CustomMonsterCount,
    int CustomModeCount,
    int AssetCount,
    IReadOnlyList<ModValidationIssue> Issues,
    int CardPatchCount = 0);

/// <summary>
/// 服务端 Mod 热加载主机：
/// - 自动定位 mods/ 目录与 custom_zh_overlay.json，构建四层 OverlayLocalizationStore；
/// - 驱动 BazaarCore.Mods.ModRegistry 完成拓扑校验、不可变仓库叠加、资产 SHA-256 索引与视觉载体映射；
/// - 支持运行时热重载（POST /api/mods/reload）与单 Mod 启停开关（POST /api/mods/{modId}/toggle），自动持久化启停状态并原子刷新活动 CardPool；
/// - 额外解析已启用 Mod 中的自定义突变词条（如 modes/custom_mutators.json），供规则目录统一暴露。
/// </summary>
public sealed class ServerModHost
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly GameDataRepository _rawBaseRepo;
    private readonly OverlayLocalizationStore _baseOverlayLocalization;
    private readonly GameDataRepository _baseRepoWithOverlay;
    private readonly IReadOnlySet<string> _validVisualWhitelist;
    private readonly ModRegistry _registry;
    private readonly string _modsRootDir;
    private readonly string _stateFilePath;

    private CardPool? _boundCardPool;
    private List<Data.MonsterTemplate>? _boundMonsters;

    private Dictionary<string, bool> _enabledOverrides = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<ModPackage> _discoveredPackages = Array.Empty<ModPackage>();
    private volatile IReadOnlyList<ModValidationIssue> _lastScanIssues = Array.Empty<ModValidationIssue>();
    private volatile IReadOnlyList<MutatorMetadata> _modCustomMutators = Array.Empty<MutatorMetadata>();

    public ServerModHost(
        GameDataRepository rawBaseRepo,
        string dataRoot,
        string stateDir,
        IReadOnlySet<string>? validVisualWhitelist = null,
        string? modsRootOverride = null,
        string? zhOverlayPathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(rawBaseRepo);
        _rawBaseRepo = rawBaseRepo;

        string? overlayPath = ResolveOverlayFilePath(dataRoot, zhOverlayPathOverride);
        _baseOverlayLocalization = new OverlayLocalizationStore(rawBaseRepo.Localization, overlayPath);
        _baseRepoWithOverlay = rawBaseRepo.CreateWithOverlay(localizationOverride: _baseOverlayLocalization);

        _validVisualWhitelist = validVisualWhitelist
            ?? new HashSet<string>(rawBaseRepo.CardsById.Keys, StringComparer.OrdinalIgnoreCase);
        _registry = new ModRegistry(_baseRepoWithOverlay, _validVisualWhitelist);

        _modsRootDir = ResolveModsDirectory(dataRoot, modsRootOverride);
        _stateFilePath = Path.Combine(stateDir, "mods_state.json");

        LoadEnabledStateFromDisk();
        Reload();
    }

    public string ModsRootDirectory => _modsRootDir;
    public string? OverlayFilePath => _baseOverlayLocalization.OverlayFilePath;
    public GameDataRepository BaseRepository => _baseRepoWithOverlay;
    public GameDataRepository ActiveRepository => _registry.ActiveRepository;
    public OverlayLocalizationStore ActiveLocalization => _registry.ActiveLocalization;
    public ModRegistry Registry => _registry;
    public ModLoadReport LastReport => _registry.LastReport;
    public string ModSetHash => _registry.ComputeModSetHash();
    public bool ForcesGhostPoolIsolation => _registry.ForcesGhostPoolIsolation;
    public IReadOnlyDictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId => _registry.VisualBindingsByCustomId;
    public IReadOnlyList<MutatorMetadata> ModCustomMutators => _modCustomMutators;
    public IReadOnlySet<string> ValidVisualWhitelist => _validVisualWhitelist;
    public IReadOnlyList<ModPackage> DiscoveredPackages => _discoveredPackages;
    public int EnabledModCount => _registry.LoadedPackages.Count;

    /// <summary>绑定运行期活动卡池与野怪池，每次 Mod 热重载/启停后自动无锁刷新。</summary>
    public void BindPools(CardPool cardPool, List<Data.MonsterTemplate>? monsters = null)
    {
        lock (_gate)
        {
            _boundCardPool = cardPool;
            _boundMonsters = monsters;
            SyncBoundPoolsLocked();
        }
    }

    /// <summary>热重载 mods/ 目录与外挂中文词典 custom_zh_overlay.json，返回完整诊断报告。</summary>
    public ModLoadReport Reload()
    {
        lock (_gate)
        {
            // 1. 先热刷新磁盘上的 custom_zh_overlay.json
            _baseOverlayLocalization.ReloadOverlay();

            // 2. 扫描 mods/ 目录发现全部 Mod 包（含当前禁用的）
            var (scanned, scanIssues) = Directory.Exists(_modsRootDir)
                ? ModLoader.ScanDirectory(_modsRootDir)
                : (new List<ModPackage>(), new List<ModValidationIssue>());

            _discoveredPackages = scanned.ToArray();
            _lastScanIssues = scanIssues.ToArray();

            // 3. 过滤出启用的 Mod 包交给 ModRegistry 拓扑校验并原子叠加
            var enabledPackages = new List<ModPackage>(scanned.Count);
            foreach (var pkg in scanned)
            {
                if (IsPackageEnabledLocked(pkg))
                    enabledPackages.Add(pkg);
            }

            var report = _registry.LoadPackages(
                enabledPackages,
                sourceLabel: _modsRootDir,
                baseRepositoryOverride: _baseRepoWithOverlay,
                validVisualWhitelistOverride: _validVisualWhitelist,
                keepPreviousOnError: false);

            // 将扫描阶段告警合并进报告（防止禁用/损坏包静默消失）
            foreach (var issue in scanIssues)
            {
                if (!report.Issues.Contains(issue))
                    report.Issues.Add(issue);
            }

            // 4. 解析已加载 Mod 中的扩展自定义突变词条（如 modes/custom_mutators.json）
            _modCustomMutators = ExtractModCustomMutators(_registry.LoadedPackages);

            // 5. 同步刷新运行期 CardPool 与野怪池
            SyncBoundPoolsLocked();

            Console.WriteLine(
                $"[ServerModHost] Mod 热加载完成: modsDir={_modsRootDir}, 发现={scanned.Count}, " +
                $"已启用加载={_registry.LoadedPackages.Count}, 自定义卡={report.TotalCustomCards}, " +
                $"自定义事件={report.TotalCustomEvents}, 资产={report.TotalAssets}, " +
                $"自定义词条={_modCustomMutators.Count}, modSetHash={report.ModSetHash[..Math.Min(12, report.ModSetHash.Length)]}");

            return report;
        }
    }

    /// <summary>运行时开启或关闭指定 Mod（enabled 为 null 时翻转当前状态），立即原子重载并持久化。</summary>
    public (bool Found, bool Enabled, ModLoadReport Report) ToggleMod(string modId, bool? enabled = null)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return (false, false, LastReport);

        lock (_gate)
        {
            var pkg = _discoveredPackages.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, modId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pkg == null)
                return (false, false, LastReport);

            bool current = IsPackageEnabledLocked(pkg);
            bool next = enabled ?? !current;
            _enabledOverrides[pkg.Manifest.Id] = next;
            SaveEnabledStateToDiskLocked();

            var report = Reload();
            return (true, next, report);
        }
    }

    /// <summary>设置指定 Mod 启用状态并立刻执行磁盘重载（适用于工坊刚落盘的新 Mod 发布或安全删除后的状态清理）。</summary>
    public ModLoadReport SetModEnabledAndReload(string modId, bool enabled)
    {
        lock (_gate)
        {
            string clean = (modId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                _enabledOverrides[clean] = enabled;
                SaveEnabledStateToDiskLocked();
            }
            return Reload();
        }
    }

    /// <summary>移除指定 Mod 的启停覆写记录并执行热重载（删除草稿 Mod 时调用）。</summary>
    public ModLoadReport RemoveModStateAndReload(string modId)
    {
        lock (_gate)
        {
            string clean = (modId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                _enabledOverrides.Remove(clean);
                SaveEnabledStateToDiskLocked();
            }
            return Reload();
        }
    }

    /// <summary>获取全部已发现 Mod 的综合状态列表（含启用状态与告警）。</summary>
    public IReadOnlyList<DiscoveredModStatus> GetAllModStatuses()
    {
        lock (_gate)
        {
            var loadedSet = new HashSet<string>(
                _registry.LoadedPackages.Select(p => p.Manifest.Id),
                StringComparer.OrdinalIgnoreCase);
            var reportIssues = _registry.LastReport.Issues;

            var list = new List<DiscoveredModStatus>(_discoveredPackages.Count);
            foreach (var pkg in _discoveredPackages.OrderBy(p => p.Manifest.LoadOrder).ThenBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                string id = pkg.Manifest.Id;
                bool enabled = IsPackageEnabledLocked(pkg);
                bool loaded = loadedSet.Contains(id);
                var modIssues = reportIssues
                    .Where(i => string.Equals(i.ModId, id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                list.Add(new DiscoveredModStatus(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? id : pkg.Manifest.Name,
                    Version: pkg.Manifest.Version,
                    Author: pkg.Manifest.Author,
                    Description: pkg.Manifest.Description,
                    Scope: pkg.Manifest.Scope,
                    Enabled: enabled,
                    Loaded: loaded,
                    AffectsGhostPool: pkg.Manifest.AffectsGhostPool,
                    PackageHash: pkg.PackageHash,
                    CustomCardCount: pkg.Cards.Count,
                    CustomEventCount: pkg.Events.Count,
                    CustomMonsterCount: pkg.Monsters.Count,
                    CustomModeCount: pkg.Modes.Count,
                    AssetCount: pkg.AssetsByLogicalPath.Count,
                    Issues: modIssues,
                    CardPatchCount: pkg.Patches.Count));
            }
            return list;
        }
    }

    public ModPackage? FindDiscoveredPackage(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        string clean = modId.Trim();
        return _discoveredPackages.FirstOrDefault(p =>
            string.Equals(p.Manifest.Id, clean, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsModEnabled(string modId)
    {
        lock (_gate)
        {
            var pkg = FindDiscoveredPackage(modId);
            return pkg != null && IsPackageEnabledLocked(pkg);
        }
    }

    /// <summary>收集指定对局内全部活跃实例对应的 Mod 自定义卡视觉与文本绑定。</summary>
    public Dictionary<string, CustomCardVisualBinding> CollectActiveVisualBindings(RunState? run)
    {
        var result = new Dictionary<string, CustomCardVisualBinding>(StringComparer.Ordinal);
        if (run == null) return result;

        foreach (var entry in run.Cards.Values)
        {
            string? customId = entry.CustomCardId;
            if (string.IsNullOrWhiteSpace(customId))
                continue;
            if (_registry.TryResolveVisualBinding(customId, out var binding, entry.InstanceId) && binding != null)
            {
                result[entry.InstanceId] = binding;
            }
        }
        return result;
    }

    public MergedLocalizationExport ExportMergedLocalization() => _registry.ExportMergedLocalization();

    public bool TryResolveAssetBySha256(string? sha256, out ModAssetEntry? asset)
        => _registry.TryResolveAssetBySha256(sha256, out asset);

    // ── 内部工具方法 ────────────────────────────────────────────────────────

    private bool IsPackageEnabledLocked(ModPackage pkg)
    {
        if (_enabledOverrides.TryGetValue(pkg.Manifest.Id, out bool explicitFlag))
            return explicitFlag;
        return pkg.Manifest.EnabledByDefault;
    }

    private void SyncBoundPoolsLocked()
    {
        if (_boundCardPool != null)
        {
            _boundCardPool.ApplyRepositoryOverlay(_registry.ActiveRepository, _registry.VisualBindingsByCustomId);
        }
        if (_boundMonsters != null)
        {
            var refreshed = MonsterPool.FromRepository(_registry.ActiveRepository);
            _boundMonsters.Clear();
            _boundMonsters.AddRange(refreshed);
        }
    }

    private void LoadEnabledStateFromDisk()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            string json = File.ReadAllText(_stateFilePath, Encoding.UTF8);
            var doc = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOpts);
            if (doc != null)
                _enabledOverrides = new Dictionary<string, bool>(doc, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerModHost] 读取 mods_state.json 失败（将使用默认启停状态）: {ex.Message}");
        }
    }

    private void SaveEnabledStateToDiskLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_enabledOverrides, JsonOpts);
            File.WriteAllText(_stateFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerModHost] 保存 mods_state.json 失败（已忽略）: {ex.Message}");
        }
    }

    /// <summary>
    /// 扫描已加载 Mod 包中 modes/*mutator*.json 形式的扩展词条文件（例如 bazaar-chronicles-sample/modes/custom_mutators.json）。
    /// </summary>
    private static IReadOnlyList<MutatorMetadata> ExtractModCustomMutators(IReadOnlyList<ModPackage> loadedPackages)
    {
        var list = new List<MutatorMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in loadedPackages)
        {
            string modesDir = Path.Combine(pkg.RootDirectory, "modes");
            if (!Directory.Exists(modesDir)) continue;

            foreach (string file in Directory.GetFiles(modesDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("mutators", out var mutsEl) ||
                        mutsEl.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var el in mutsEl.EnumerateArray())
                    {
                        string id = el.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "").Trim() : "";
                        if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;

                        string nameZh = el.TryGetProperty("nameZh", out var nEl) ? (nEl.GetString() ?? id) : id;
                        string descZh = el.TryGetProperty("descZh", out var dEl) && dEl.ValueKind == JsonValueKind.String
                            ? (dEl.GetString() ?? "")
                            : (el.TryGetProperty("descriptionZh", out var d2El) && d2El.ValueKind == JsonValueKind.String
                                ? (d2El.GetString() ?? "")
                                : "");
                        string? conflictGroup = el.TryGetProperty("conflictGroup", out var cgEl) && cgEl.ValueKind == JsonValueKind.String
                            ? cgEl.GetString()
                            : null;
                        double scoreDelta = el.TryGetProperty("scoreMultiplierDelta", out var sdEl) && sdEl.TryGetDouble(out var dVal)
                            ? dVal
                            : (el.TryGetProperty("difficultyDelta", out var ddEl) && ddEl.TryGetDouble(out var ddVal)
                                ? ddVal * 0.1
                                : 0.0);
                        bool isPositive = !el.TryGetProperty("isPositive", out var ipEl) || ipEl.ValueKind != JsonValueKind.False;
                        string hook = el.TryGetProperty("targetHook", out var hkEl) && hkEl.ValueKind == JsonValueKind.String
                            ? (hkEl.GetString() ?? "ModHook")
                            : (el.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.String
                                ? $"Mod:{pkg.Manifest.Id}:{catEl.GetString()}"
                                : $"Mod:{pkg.Manifest.Id}");

                        Dictionary<string, double>? paramMap = null;
                        JsonElement paramsContainer = default;
                        bool hasParams = (el.TryGetProperty("parameters", out paramsContainer) && paramsContainer.ValueKind == JsonValueKind.Object)
                            || (el.TryGetProperty("rules", out paramsContainer) && paramsContainer.ValueKind == JsonValueKind.Object);
                        if (hasParams)
                        {
                            paramMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in paramsContainer.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var pv))
                                    paramMap[prop.Name] = pv;
                                else if (prop.Value.ValueKind == JsonValueKind.True)
                                    paramMap[prop.Name] = 1.0;
                                else if (prop.Value.ValueKind == JsonValueKind.False)
                                    paramMap[prop.Name] = 0.0;
                            }
                        }

                        list.Add(new MutatorMetadata(
                            Id: id,
                            NameZh: nameZh,
                            DescZh: descZh,
                            ConflictGroup: conflictGroup,
                            ScoreMultiplierDelta: scoreDelta,
                            IsPositive: isPositive,
                            TargetHook: hook,
                            Parameters: paramMap));
                    }
                }
                catch
                {
                    // 非词条格式的 mode json 由 ModLoader 常规加载，此处静默跳过
                }
            }
        }

        return list;
    }

    private static string ResolveModsDirectory(string dataRoot, string? explicitOverride)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitOverride))
            candidates.Add(explicitOverride);

        string? envMods = Environment.GetEnvironmentVariable("BAZAAR_MODS");
        if (!string.IsNullOrWhiteSpace(envMods))
            candidates.Add(envMods);

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            candidates.Add(Path.Combine(dataRoot, "mods"));
            string? p1 = Directory.GetParent(dataRoot)?.FullName;
            if (!string.IsNullOrEmpty(p1))
            {
                candidates.Add(Path.Combine(p1, "mods"));
                string? p2 = Directory.GetParent(p1)?.FullName;
                if (!string.IsNullOrEmpty(p2))
                    candidates.Add(Path.Combine(p2, "mods"));
            }
        }

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "mods"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "mods"));

        foreach (string dir in candidates)
        {
            if (Directory.Exists(dir))
                return Path.GetFullPath(dir);
        }

        // 若尚未建立 mods/ 目录，默认指向工作目录同级 mods/
        string fallback = !string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Directory.GetParent(dataRoot)?.FullName ?? dataRoot, "mods")
            : Path.Combine(Environment.CurrentDirectory, "mods");
        return Path.GetFullPath(fallback);
    }

    private static string? ResolveOverlayFilePath(string dataRoot, string? explicitOverride)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitOverride))
            candidates.Add(explicitOverride);

        string? envOverlay = Environment.GetEnvironmentVariable("BAZAAR_ZH_OVERLAY");
        if (!string.IsNullOrWhiteSpace(envOverlay))
            candidates.Add(envOverlay);

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            // 优先匹配 dist/data/translations/custom_zh_overlay.json（服务端部署标准路径）
            candidates.Add(Path.Combine(dataRoot, "translations", "custom_zh_overlay.json"));
            candidates.Add(Path.Combine(dataRoot, "custom_zh_overlay.json"));
            string? p1 = Directory.GetParent(dataRoot)?.FullName;
            if (!string.IsNullOrEmpty(p1))
            {
                candidates.Add(Path.Combine(p1, "translations", "custom_zh_overlay.json"));
                candidates.Add(Path.Combine(p1, "custom_zh_overlay.json"));
                candidates.Add(Path.Combine(p1, "tools", "data", "custom_zh_overlay.json"));
                string? p2 = Directory.GetParent(p1)?.FullName;
                if (!string.IsNullOrEmpty(p2))
                    candidates.Add(Path.Combine(p2, "tools", "data", "custom_zh_overlay.json"));
            }
        }

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "tools", "data", "custom_zh_overlay.json"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "custom_zh_overlay.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "custom_zh_overlay.json"));

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }
}
