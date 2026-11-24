using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
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
    int CardPatchCount = 0,
    int CustomMechanicCount = 0,
    int CustomMutatorCount = 0);

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
    private MonsterPoolStore? _boundMonsterStore;
    private CommunityModStore? _communityStore;
    private readonly Dictionary<string, RunModContext> _runModContextCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModRegistry> _isolatedRegistryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单局或锦标赛房间专用的独立 Mod 运行上下文（不影响服务器全局默认池；P0-18/P0-22/P0-30 契约完整导出）。</summary>
    public sealed record RunModContext(
        string ContextKey,
        GameDataRepository Repository,
        CardPool Pool,
        List<Data.MonsterTemplate> Monsters,
        CardArchetypeIndex ArchetypeIndex,
        IReadOnlyDictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId,
        IReadOnlyList<string> ActiveModIds,
        ModRegistry? IsolatedRegistry = null,
        ModRuntimeContext? RuntimeContext = null,
        string EffectiveModSetHash = "")
    {
        private readonly ConcurrentDictionary<string, CustomCardVisualBinding> _transientBindings = new(StringComparer.OrdinalIgnoreCase);
        private int _visualBindingsRevision;

        public IReadOnlyDictionary<string, MutatorMetadata> CustomMutatorsById =>
            RuntimeContext?.EffectiveCustomMutatorsById ?? FrozenDictionary<string, MutatorMetadata>.Empty;

        public IReadOnlyDictionary<string, CustomModeDefinition> CustomModesById =>
            RuntimeContext?.EffectiveCustomModesById ?? FrozenDictionary<string, CustomModeDefinition>.Empty;

        public int VisualBindingsRevision => Volatile.Read(ref _visualBindingsRevision);

        public bool TryGetGameModeHook(string? modeId, out IGameModeHook? hook)
        {
            if (!string.IsNullOrWhiteSpace(modeId) && RuntimeContext?.ModeHooks != null
                && RuntimeContext.ModeHooks.TryGetValue(modeId.Trim(), out var found))
            {
                hook = found;
                return true;
            }
            hook = null;
            return false;
        }

        public CustomMutatorRuleSummary ResolveMutatorRuleSummary(CustomRunRuleSet? rules)
            => MutatorCatalog.ResolveRuleSummary(rules?.MutatorIds, CustomMutatorsById);

        /// <summary>P0-19：供战中变形自定义卡回写临时视觉绑定并递增修订版本号。</summary>
        public void RegisterTransientVisualBinding(CustomCardVisualBinding binding)
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.CustomTemplateId)) return;
            _transientBindings[binding.CustomTemplateId.Trim()] = binding;
            Interlocked.Increment(ref _visualBindingsRevision);
        }

        public bool TryResolveVisualBinding(string? customId, out CustomCardVisualBinding? binding, string? instanceId = null)
        {
            if (!string.IsNullOrWhiteSpace(customId))
            {
                string key = customId.Trim();
                if (_transientBindings.TryGetValue(key, out var tb)
                    || VisualBindingsByCustomId.TryGetValue(key, out tb))
                {
                    binding = string.IsNullOrWhiteSpace(instanceId) ? tb : tb.BindToInstance(instanceId);
                    return true;
                }
            }
            binding = null;
            return false;
        }

        public IReadOnlyDictionary<string, CustomCardVisualBinding> GetMergedVisualCatalog()
        {
            if (_transientBindings.IsEmpty)
                return VisualBindingsByCustomId;
            var merged = new Dictionary<string, CustomCardVisualBinding>(VisualBindingsByCustomId, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in _transientBindings)
                merged[k] = v;
            return merged;
        }
    }

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
        // 仅主服务器全局注册表发布至静态 ModRuntimeHooks；按房隔离/校验沙盒一律使用 ModRegistry.CreateIsolated
        _registry = new ModRegistry(_baseRepoWithOverlay, _validVisualWhitelist, publishGlobalRuntimeHooks: true);

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
    public CommunityModStore? CommunityStore => _communityStore;

    /// <summary>绑定社区 Mod 仓库，使按房间加载、实例绑定与 PNG 资产下载自动涵盖社区上传的 Mod。</summary>
    public void AttachCommunityStore(CommunityModStore communityStore)
    {
        lock (_gate)
        {
            _communityStore = communityStore;
            _runModContextCache.Clear();
            _isolatedRegistryCache.Clear();
        }
    }

    /// <summary>清空按房/按局隔离 Mod 上下文缓存（在全局重载或社区 Mod 变动时调用）。</summary>
    public void InvalidateRunModContextCache()
    {
        lock (_gate)
        {
            _runModContextCache.Clear();
            _isolatedRegistryCache.Clear();
        }
    }

    /// <summary>绑定运行期活动卡池与不可变野怪池快照存储，每次 Mod 热重载/启停后原子切换快照。</summary>
    public void BindPools(CardPool cardPool, MonsterPoolStore? monsterStore)
    {
        lock (_gate)
        {
            _boundCardPool = cardPool;
            _boundMonsterStore = monsterStore;
            SyncBoundPoolsLocked();
        }
    }

    /// <summary>向后兼容重载：内部包装为不可变 <see cref="MonsterPoolStore"/>，绝不再对调用方传入的 List 执行就地 Clear/AddRange。</summary>
    public void BindPools(CardPool cardPool, List<Data.MonsterTemplate>? monsters = null)
    {
        lock (_gate)
        {
            _boundCardPool = cardPool;
            _boundMonsterStore = monsters != null ? new MonsterPoolStore(monsters) : null;
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

            // 5. 同步刷新运行期 CardPool 与野怪池，并清空按房隔离缓存
            _runModContextCache.Clear();
            _isolatedRegistryCache.Clear();
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
                    CardPatchCount: pkg.Patches.Count,
                    CustomMechanicCount: pkg.Mechanics.Count,
                    CustomMutatorCount: pkg.CustomMutators.Count));
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

    /// <summary>
    /// 按单局或锦标赛房间选中的 Mod ID 列表解析独立运行上下文（P0-18）：
    /// 递归展开所有选中 Mod 的依赖闭包（兼容全局已启用 + 社区仓库 + 本地目录），
    /// 统一交由隔离 <see cref="ModRegistry"/> 导出合并仓库、合并视觉绑定、合成词典、自定义词条/模式与运行时钩子，绝不污染全局。
    /// </summary>
    public RunModContext ResolveRunModContext(
        IReadOnlyList<string>? selectedModIds,
        CardPool defaultPool,
        List<Data.MonsterTemplate> defaultMonsters,
        CardArchetypeIndex defaultArchetypeIndex)
    {
        lock (_gate)
        {
            var isoRegistry = ResolveEffectiveRegistryLocked(selectedModIds, out string cacheKey, out bool isDefaultGlobal);
            string effHash = ComputeEffectiveModSetHashFromPackages(isoRegistry.LoadedPackages);

            if (isDefaultGlobal)
            {
                return new RunModContext(
                    ContextKey: "default",
                    Repository: ActiveRepository,
                    Pool: defaultPool,
                    Monsters: _boundMonsterStore?.SnapshotList() ?? defaultMonsters,
                    ArchetypeIndex: defaultArchetypeIndex,
                    VisualBindingsByCustomId: VisualBindingsByCustomId,
                    ActiveModIds: _registry.LoadedPackages.Select(p => p.Manifest.Id).ToList(),
                    IsolatedRegistry: null,
                    RuntimeContext: _registry.RuntimeContext,
                    EffectiveModSetHash: effHash);
            }

            if (_runModContextCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var isoRepo = isoRegistry.ActiveRepository;
            var isoPool = defaultPool.CreateIsolatedOverlay(isoRepo, isoRegistry.VisualBindingsByCustomId);
            var isoMonsters = MonsterPool.FromRepository(isoRepo);
            var isoArchetypes = CardArchetypeIndex.Build(isoPool, isoRepo);

            var ctx = new RunModContext(
                ContextKey: cacheKey,
                Repository: isoRepo,
                Pool: isoPool,
                Monsters: isoMonsters,
                ArchetypeIndex: isoArchetypes,
                VisualBindingsByCustomId: isoRegistry.VisualBindingsByCustomId,
                ActiveModIds: isoRegistry.LoadedPackages.Select(p => p.Manifest.Id).ToList(),
                IsolatedRegistry: isoRegistry,
                RuntimeContext: isoRegistry.RuntimeContext,
                EffectiveModSetHash: effHash);

            if (_runModContextCache.Count >= 16)
                _runModContextCache.Clear();
            _runModContextCache[cacheKey] = ctx;
            return ctx;
        }
    }

    /// <summary>
    /// P0-18：双作用域同构解析有效 ModRegistry（全局已启用 + 递归展开 selectedModIds 依赖闭包，按包哈希缓存隔离实例）。
    /// </summary>
    public ModRegistry ResolveEffectiveRegistryForMods(IReadOnlyList<string>? selectedModIds)
    {
        lock (_gate)
        {
            return ResolveEffectiveRegistryLocked(selectedModIds, out _, out _);
        }
    }

    /// <summary>
    /// P0-26：供观战端点与 /api/mods/active-bindings 使用的轻量只读 Mod 视图解析（零重建 CardPool/MonsterPool 开销，
    /// 优先复用已缓存 <see cref="RunModContext"/> 的瞬态绑定与修订版本号）。
    /// </summary>
    public (ModRegistry Registry, GameDataRepository Repository, RunModContext? CachedContext, int TransientRevision, string EffectiveModSetHash) ResolveReadOnlyModView(
        IReadOnlyList<string>? selectedModIds)
    {
        lock (_gate)
        {
            var isoRegistry = ResolveEffectiveRegistryLocked(selectedModIds, out string cacheKey, out bool isDefaultGlobal);
            string effHash = ComputeEffectiveModSetHashFromPackages(isoRegistry.LoadedPackages);
            RunModContext? cachedCtx = null;
            if (!isDefaultGlobal)
                _runModContextCache.TryGetValue(cacheKey, out cachedCtx);
            int transientRev = cachedCtx?.VisualBindingsRevision ?? 0;
            var repo = isDefaultGlobal ? ActiveRepository : isoRegistry.ActiveRepository;
            return (isoRegistry, repo, cachedCtx, transientRev, effHash);
        }
    }

    private ModRegistry ResolveEffectiveRegistryLocked(
        IReadOnlyList<string>? selectedModIds,
        out string cacheKey,
        out bool isDefaultGlobal)
    {
        if (selectedModIds == null || selectedModIds.Count == 0)
        {
            cacheKey = "default";
            isDefaultGlobal = true;
            return _registry;
        }

        var closure = ResolveDependencyClosurePackagesLocked(selectedModIds);
        var globalIds = new HashSet<string>(_registry.LoadedPackages.Select(p => p.Manifest.Id), StringComparer.OrdinalIgnoreCase);
        bool hasAnyExtra = closure.Any(p => !globalIds.Contains(p.Manifest.Id));
        if (!hasAnyExtra)
        {
            cacheKey = "default";
            isDefaultGlobal = true;
            return _registry;
        }

        isDefaultGlobal = false;
        cacheKey = ModSetHash + "|" + string.Join(",",
            closure.OrderBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                   .Select(p => $"{p.Manifest.Id}:{p.PackageHash}"));

        if (_isolatedRegistryCache.TryGetValue(cacheKey, out var cachedIso))
            return cachedIso;

        // P0-17/P0-18：房间隔离注册表严禁触碰全局静态 ModRuntimeHooks，统一从隔离注册表导出绑定/词典/机制
        var isoRegistry = ModRegistry.CreateIsolated(_baseRepoWithOverlay, _validVisualWhitelist);
        isoRegistry.LoadPackages(
            closure,
            sourceLabel: "room-isolated",
            baseRepositoryOverride: _baseRepoWithOverlay,
            validVisualWhitelistOverride: _validVisualWhitelist,
            keepPreviousOnError: false);

        if (_isolatedRegistryCache.Count >= 16)
            _isolatedRegistryCache.Clear();
        _isolatedRegistryCache[cacheKey] = isoRegistry;
        return isoRegistry;
    }

    private List<ModPackage> ResolveDependencyClosurePackagesLocked(IReadOnlyCollection<string>? selectedModIds)
    {
        var ordered = new List<ModPackage>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ModPackage? ResolveSingle(string id)
            => _communityStore?.ResolvePackage(id)
               ?? FindDiscoveredPackage(id)
               ?? _registry.LoadedPackages.FirstOrDefault(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

        void Dfs(ModPackage pkg)
        {
            string pid = pkg.Manifest.Id.Trim();
            if (visited.Contains(pid) || !visiting.Add(pid)) return;
            if (pkg.Manifest.Dependencies is { Count: > 0 })
            {
                foreach (var depRaw in pkg.Manifest.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(depRaw)) continue;
                    var depPkg = ResolveSingle(depRaw.Trim());
                    if (depPkg != null) Dfs(depPkg);
                }
            }
            visiting.Remove(pid);
            if (visited.Add(pid))
                ordered.Add(pkg);
        }

        // 先载入全局已启用基线
        foreach (var gp in _registry.LoadedPackages)
            Dfs(gp);

        // 再递归展开本局/本房额外选中的 Mod 及其全部传递依赖
        if (selectedModIds != null)
        {
            foreach (var raw in selectedModIds)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var pkg = ResolveSingle(raw.Trim());
                if (pkg != null) Dfs(pkg);
            }
        }

        return ordered;
    }

    /// <summary>
    /// P0-18：统一从目标对局的有效隔离 ModRegistry（含递归依赖与两阶段 Patch 合并）收集实例级视觉绑定，
    /// 并对每一条绑定（含社区 Mod 与全局 Mod）一律调用 BindToInstance(instanceId)！
    /// </summary>
    public Dictionary<string, CustomCardVisualBinding> CollectActiveVisualBindings(RunState? run)
    {
        var result = new Dictionary<string, CustomCardVisualBinding>(StringComparer.Ordinal);
        if (run == null) return result;

        var effRegistry = ResolveEffectiveRegistryForMods(run.CustomRules?.SelectedModIds);
        void TryAddInstanceBinding(string? instanceId, string? customId)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(customId))
                return;
            if (effRegistry.TryResolveVisualBinding(customId, out var binding, instanceId) && binding != null)
            {
                result[instanceId] = binding;
            }
        }

        foreach (var entry in run.Cards.Values)
        {
            // 对 Patch 原版卡（CustomCardId 为空但 TemplateId 被 Patch）同样支持解析视觉绑定
            string lookupId = !string.IsNullOrWhiteSpace(entry.CustomCardId)
                ? entry.CustomCardId!
                : entry.TemplateId.ToString();
            TryAddInstanceBinding(entry.InstanceId, lookupId);
        }

        if (run.ActiveCombatOpponentCustomCardIds.Count > 0)
        {
            foreach (var (oppInstId, oppCustomId) in run.ActiveCombatOpponentCustomCardIds)
            {
                if (!result.ContainsKey(oppInstId))
                    TryAddInstanceBinding(oppInstId, oppCustomId);
            }
        }

        return result;
    }

    /// <summary>P0-18：返回指定对局有效的全量模板级视觉目录（统一从递归依赖后的隔离 ModRegistry 导出）。</summary>
    public IReadOnlyDictionary<string, CustomCardVisualBinding> GetVisualCatalogForRun(RunState? run)
        => ResolveEffectiveRegistryForMods(run?.CustomRules?.SelectedModIds).VisualBindingsByCustomId;

    /// <summary>P0-18：按指定选中 Mod 集导出同构合并后的四层本地化词典（递归展开依赖）。</summary>
    public MergedLocalizationExport ExportMergedLocalizationForMods(IReadOnlyList<string>? selectedModIds)
        => ResolveEffectiveRegistryForMods(selectedModIds).ExportMergedLocalization();

    public MergedLocalizationExport ExportMergedLocalization() => _registry.ExportMergedLocalization();

    /// <summary>P0-22：返回全局 + 指定自选 Mod 的全部可用自定义突变词条。</summary>
    public IReadOnlyList<MutatorMetadata> GetAllAvailableMutators(IReadOnlyList<string>? selectedModIds = null)
    {
        var reg = ResolveEffectiveRegistryForMods(selectedModIds);
        return MutatorCatalog.GetAll(reg.CustomMutatorsById);
    }

    /// <summary>P0-22：根据规则或显式模式 ID 解析已注册的自定义游戏模式定义。</summary>
    public CustomModeDefinition? ResolveActiveCustomMode(string? modeId, RunModContext? ctx = null)
    {
        if (string.IsNullOrWhiteSpace(modeId)) return null;
        string clean = modeId.Trim();
        if (ctx != null && ctx.CustomModesById.TryGetValue(clean, out var fromCtx))
            return fromCtx;
        return _registry.GetCustomMode(clean);
    }

    /// <summary>
    /// P0-30 单一权威纯函数（便捷重载）：计算对局有效幽灵池分区键。
    /// 标准无玩法 Mod 局返回 "standard"；纯玩法 Mod 返回 "mod:&lt;hash12&gt;"；自定义规则+玩法 Mod 返回 "mod:&lt;hash12&gt;+custom"。
    /// </summary>
    public static string ResolveEffectiveGhostPoolPartition(
        IReadOnlyList<string>? selectedModIds,
        bool isCustomRule,
        ServerModHost? modHost)
    {
        string rulePart = isCustomRule ? "custom" : "standard";
        string modHash12 = ComputeEffectiveModSetHash(selectedModIds, modHost);
        if (string.IsNullOrEmpty(modHash12))
            return rulePart;
        return rulePart == "standard" ? $"mod:{modHash12}" : $"mod:{modHash12}+{rulePart}";
    }

    /// <summary>
    /// P0-30 单一权威纯函数（完整重载）：统一结合 CustomRunRuleSet.ResolveGhostPoolPartition()、有效玩法 Mod 集哈希与局内自建卡标记。
    /// </summary>
    public static string ResolveEffectiveGhostPoolPartition(
        CustomRunRuleSet? rules,
        IReadOnlyList<string>? selectedModIds,
        bool hasCustomCardInRun,
        ServerModHost? modHost)
    {
        var effIds = selectedModIds ?? rules?.SelectedModIds;
        string rulePart;
        if (rules == null)
        {
            rulePart = "standard";
        }
        else if (rules.SelectedModIds is { Count: > 0 })
        {
            // P0-30 × P0-22：剥离 SelectedModIds 判定纯规则分区时必须保留单局隔离自定义突变词条，
            // 避免「玩法 Mod + 该 Mod 自定义词条」被无参 CloneNormalized() 洗空词条而误判为 "standard" 混入 "mod:<hash12>"
            IReadOnlyDictionary<string, MutatorMetadata>? effMutators = modHost != null
                ? modHost.ResolveEffectiveRegistryForMods(effIds).CustomMutatorsById
                : null;
            var rulesWithoutMods = rules.CloneNormalized(effMutators, preserveUnregisteredMutators: true);
            rulesWithoutMods.SelectedModIds.Clear();
            rulePart = rulesWithoutMods.ResolveGhostPoolPartition();
        }
        else
        {
            rulePart = rules.ResolveGhostPoolPartition();
        }
        if (string.Equals(rulePart, "none", StringComparison.OrdinalIgnoreCase))
            return "none";

        string modHash12 = ComputeEffectiveModSetHash(effIds, modHost);
        if (hasCustomCardInRun && string.IsNullOrEmpty(modHash12))
            return "none";

        if (string.IsNullOrEmpty(modHash12))
            return rulePart;

        return string.Equals(rulePart, "standard", StringComparison.OrdinalIgnoreCase)
            ? $"mod:{modHash12}"
            : $"mod:{modHash12}+{rulePart}";
    }

    /// <summary>
    /// D3-P1-12 / P0-30：展开递归依赖闭包后，过滤出所有非 cosmetic-only 玩法 Mod，
    /// 按 Mod ID 升序拼装 {id}@{version}#{packageHash} 求 SHA-256 取前 12 位小写 hex；纯原版或仅 cosmetic 返回空串 ""。
    /// </summary>
    public static string ComputeEffectiveModSetHash(
        IReadOnlyList<string>? selectedModIds,
        ServerModHost? modHost)
    {
        if (modHost == null)
        {
            if (selectedModIds == null || selectedModIds.Count == 0)
                return string.Empty;
            var fallbackIds = selectedModIds
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (fallbackIds.Length == 0) return string.Empty;
            byte[] fbHash = SHA256.HashData(Encoding.UTF8.GetBytes("bazaar-modset-ids:" + string.Join("|", fallbackIds)));
            return Convert.ToHexString(fbHash).ToLowerInvariant()[..12];
        }

        IReadOnlyList<ModPackage> effectivePackages;
        lock (modHost._gate)
        {
            var isoReg = modHost.ResolveEffectiveRegistryLocked(selectedModIds, out _, out _);
            effectivePackages = isoReg.LoadedPackages;
        }
        return ComputeEffectiveModSetHashFromPackages(effectivePackages);
    }

    private static bool IsGameplayAffectingPackage(ModPackage pkg)
    {
        if (pkg == null) return false;
        bool hasGameplayContent = pkg.Cards.Count > 0 || pkg.Patches.Count > 0 || pkg.Events.Count > 0
            || pkg.Monsters.Count > 0 || pkg.Modes.Count > 0 || pkg.CustomMutators.Count > 0 || pkg.Mechanics.Count > 0;
        if (hasGameplayContent) return true;
        return pkg.Manifest.ForcesGhostPoolIsolation;
    }

    private static string ComputeEffectiveModSetHashFromPackages(IReadOnlyList<ModPackage> packages)
    {
        if (packages == null || packages.Count == 0) return string.Empty;
        var gameplayPkgs = packages
            .Where(IsGameplayAffectingPackage)
            .OrderBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (gameplayPkgs.Count == 0) return string.Empty;

        var sb = new StringBuilder(128);
        for (int i = 0; i < gameplayPkgs.Count; i++)
        {
            var p = gameplayPkgs[i];
            if (i > 0) sb.Append(';');
            sb.Append(p.Manifest.Id.Trim().ToLowerInvariant())
              .Append('@')
              .Append(p.Manifest.Version.Trim())
              .Append('#')
              .Append(p.PackageHash ?? string.Empty);
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }

    public bool TryResolveAssetBySha256(string? sha256, out ModAssetEntry? asset)
    {
        if (_registry.TryResolveAssetBySha256(sha256, out asset))
            return true;
        if (!string.IsNullOrWhiteSpace(sha256) && _communityStore != null && _communityStore.TryResolveAssetBySha256(sha256, out asset))
            return true;
        asset = null;
        return false;
    }

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
        if (_boundMonsterStore != null)
        {
            var refreshed = MonsterPool.FromRepository(_registry.ActiveRepository);
            _boundMonsterStore.Replace(refreshed);
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
    /// 汇总已加载 Mod 包中的全部自定义突变词条（P0-22 优先直接消费 ModLoader 解析进 ModPackage.CustomMutators 的完整定义，保留磁盘回退兼容）。
    /// </summary>
    private static IReadOnlyList<MutatorMetadata> ExtractModCustomMutators(IReadOnlyList<ModPackage> loadedPackages)
    {
        var list = new List<MutatorMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in loadedPackages)
        {
            if (pkg.CustomMutators.Count > 0)
            {
                foreach (var m in pkg.CustomMutators)
                {
                    if (m != null && !string.IsNullOrWhiteSpace(m.Id) && seen.Add(m.Id.Trim()))
                        list.Add(m);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(pkg.RootDirectory)) continue;
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
