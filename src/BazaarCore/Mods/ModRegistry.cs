using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Mods;

/// <summary>
/// 不可变的单局/单注册表 Mod 运行时上下文快照（供 CombatSimulator、SandboxEngineService 与 W1-C RunEngine 透传，P0-17）：
/// 所有字典与列表均不可变且永不为 null，支持按房/按局完全隔离，不再依赖全局静态 <see cref="ModRuntimeHooks"/>。
/// </summary>
public sealed record ModRuntimeContext(
    FrozenDictionary<string, MechanicDefinition> MechanicsById,
    FrozenDictionary<string, ICustomActionHandler> ActionHandlers,
    FrozenDictionary<string, ICustomTriggerHandler> TriggerHandlers,
    FrozenDictionary<string, IGameModeHook> ModeHooks,
    IReadOnlyList<ICombatPipelineInterceptor> PipelineInterceptors,
    FrozenDictionary<string, MutatorMetadata>? CustomMutatorsById = null,
    FrozenDictionary<string, CustomModeDefinition>? CustomModesById = null)
{
    public FrozenDictionary<string, MutatorMetadata> EffectiveCustomMutatorsById =>
        CustomMutatorsById ?? FrozenDictionary<string, MutatorMetadata>.Empty;

    public FrozenDictionary<string, CustomModeDefinition> EffectiveCustomModesById =>
        CustomModesById ?? FrozenDictionary<string, CustomModeDefinition>.Empty;

    public static ModRuntimeContext Empty { get; } = new(
        FrozenDictionary<string, MechanicDefinition>.Empty,
        FrozenDictionary<string, ICustomActionHandler>.Empty,
        FrozenDictionary<string, ICustomTriggerHandler>.Empty,
        FrozenDictionary<string, IGameModeHook>.Empty,
        Array.Empty<ICombatPipelineInterceptor>(),
        FrozenDictionary<string, MutatorMetadata>.Empty,
        FrozenDictionary<string, CustomModeDefinition>.Empty);
}

/// <summary>
/// BazaarModKit 运行时注册表与热重载管理器：
/// - 从 mods/ 目录或内存 ModPackage 集合扫描、拓扑校验并增量叠加至只读 <see cref="GameDataRepository"/> 不可变副本；
/// - 原子切换与安全回滚（绝不破坏原版 BaseRepository）；
/// - 合并四层中文词典（自动汇总 Mod 卡牌中文元数据 + localization/zh-CN.json）；
/// - 仅当 <see cref="PublishesGlobalRuntimeHooks"/> 为 true 时同步发布至全局 <see cref="ModRuntimeHooks"/>，隔离/沙盒注册表严禁污染全局静态钩子；
/// - 导出不可变 <see cref="ModRuntimeContext"/>、客户端增量同步 <see cref="ModManifestDto"/>、资产 SHA-256 索引与实例级视觉绑定查询接口。
/// </summary>
public sealed class ModRegistry : IModContentRegistry
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RegistrySnapshot
    {
        public required GameDataRepository BaseRepository { get; init; }
        public required GameDataRepository ActiveRepository { get; init; }
        public required OverlayLocalizationStore ActiveLocalization { get; init; }
        public required IReadOnlyList<ModPackage> LoadedPackages { get; init; }
        public required ModLoadReport Report { get; init; }
        public required FrozenDictionary<string, CustomModeDefinition> CustomModesById { get; init; }
        public required FrozenDictionary<string, MutatorMetadata> CustomMutatorsById { get; init; }
        public required FrozenDictionary<string, MechanicDefinition> MechanicsById { get; init; }
        public required ModRuntimeContext RuntimeContext { get; init; }
        public required FrozenDictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId { get; init; }
        public required FrozenDictionary<string, ModAssetEntry> AssetsBySha256 { get; init; }
        public required FrozenDictionary<string, ModAssetEntry> AssetsByModAndPath { get; init; }
        public required string VisualCatalogJson { get; init; }
        public required string VisualCatalogHash { get; init; }
        public required ModManifestDto ManifestDto { get; init; }
    }

    private readonly object _syncLock = new();
    private readonly bool _publishGlobalRuntimeHooks;
    private readonly List<ICustomCardProvider> _cardProviders = new();
    private readonly List<ICustomActionHandler> _actionHandlers = new();
    private readonly List<ICustomTriggerHandler> _triggerHandlers = new();
    private readonly List<IGameModeHook> _modeHooks = new();
    private readonly List<ICombatPipelineInterceptor> _pipelineInterceptors = new();

    private IReadOnlySet<string>? _validVisualWhitelist;
    private string? _lastModsRoot;
    private volatile RegistrySnapshot _snapshot;

    public ModRegistry(
        GameDataRepository baseRepository,
        IReadOnlySet<string>? validVisualWhitelist = null,
        bool publishGlobalRuntimeHooks = true)
    {
        ArgumentNullException.ThrowIfNull(baseRepository);
        _validVisualWhitelist = validVisualWhitelist;
        _publishGlobalRuntimeHooks = publishGlobalRuntimeHooks;
        _snapshot = BuildEmptySnapshot(baseRepository, string.Empty);
    }

    /// <summary>创建隔离沙盒/房间级注册表（<c>publishGlobalRuntimeHooks: false</c>，绝不读写全局静态 <see cref="ModRuntimeHooks"/>）。</summary>
    public static ModRegistry CreateIsolated(
        GameDataRepository baseRepository,
        IReadOnlySet<string>? validVisualWhitelist = null)
        => new(baseRepository, validVisualWhitelist, publishGlobalRuntimeHooks: false);

    /// <summary>本注册表是否将机制与 C# 钩子发布到全局静态 <see cref="ModRuntimeHooks"/>（仅主服务器注册表为 true）。</summary>
    public bool PublishesGlobalRuntimeHooks => _publishGlobalRuntimeHooks;

    /// <summary>当前生效的不可变运行时钩子与声明式机制快照（推荐透传给 CombatSimulator，避免读取全局静态单例）。</summary>
    public ModRuntimeContext RuntimeContext => _snapshot.RuntimeContext;

    /// <summary>未经 Mod 叠加的纯净基础只读数据仓库。</summary>
    public GameDataRepository BaseRepository => _snapshot.BaseRepository;

    /// <summary>当前生效的数据仓库（已增量叠加全部通过校验的 Mod 卡牌/野怪/本地化）。</summary>
    public GameDataRepository ActiveRepository => _snapshot.ActiveRepository;

    /// <summary>当前生效的四层外挂本地化存储（Mod 覆盖 &gt; 外挂词典 &gt; 官方 zh-CN.bytes &gt; 英文回退）。</summary>
    public OverlayLocalizationStore ActiveLocalization => _snapshot.ActiveLocalization;

    /// <summary>按拓扑加载顺序排列的已生效 ModPackage 列表。</summary>
    public IReadOnlyList<ModPackage> LoadedPackages => _snapshot.LoadedPackages;

    /// <summary>最近一次扫描/加载/重载的详细诊断报告。</summary>
    public ModLoadReport LastReport => _snapshot.Report;

    /// <summary>已注册的自定义游戏模式表。</summary>
    public IReadOnlyDictionary<string, CustomModeDefinition> CustomModesById => _snapshot.CustomModesById;

    /// <summary>P0-22：已注册的自定义肉鸽突变词条表（来自全部已加载 Mod 的 modes/*mutator*.json）。</summary>
    public IReadOnlyDictionary<string, MutatorMetadata> CustomMutatorsById => _snapshot.CustomMutatorsById;

    /// <summary>已注册的开放声明式沙盒自定义机制表（来自全部已加载 Mod 的 mechanics/*.json）。</summary>
    public IReadOnlyDictionary<string, MechanicDefinition> MechanicsById => _snapshot.MechanicsById;

    /// <summary>全部自定义卡牌/事件的模板级视觉绑定字典。</summary>
    public IReadOnlyDictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId => _snapshot.VisualBindingsByCustomId;

    /// <summary>全部客户端资产的 SHA-256 寻址索引。</summary>
    public IReadOnlyDictionary<string, ModAssetEntry> AssetsBySha256 => _snapshot.AssetsBySha256;

    /// <summary>最近一次加载的 mods 目录路径。</summary>
    public string? ModsRootDirectory => _lastModsRoot;

    /// <summary>当前已加载 Mod 集合是否要求强制隔离标准幽灵池。</summary>
    public bool ForcesGhostPoolIsolation => _snapshot.Report.ForcesGhostPoolIsolation;

    /// <summary>计算当前已加载 Mod 集合的确定性 SHA-256 哈希。</summary>
    public string ComputeModSetHash() => _snapshot.Report.ModSetHash;

    /// <inheritdoc />
    public void RegisterCardProvider(ICustomCardProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_syncLock) _cardProviders.Add(provider);
    }

    /// <inheritdoc />
    public void RegisterActionHandler(ICustomActionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncLock) _actionHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterTriggerHandler(ICustomTriggerHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncLock) _triggerHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterGameModeHook(IGameModeHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_syncLock) _modeHooks.Add(hook);
    }

    /// <inheritdoc />
    public void RegisterPipelineInterceptor(ICombatPipelineInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_syncLock) _pipelineInterceptors.Add(interceptor);
    }

    /// <summary>
    /// 刷新本注册表不可变 <see cref="RuntimeContext"/>；
    /// 仅当 <see cref="PublishesGlobalRuntimeHooks"/> 为 true 时同步发布到全局 <see cref="ModRuntimeHooks"/>。
    /// </summary>
    public void ApplyRuntimeHooks()
    {
        lock (_syncLock)
        {
            var runtimeCtx = BuildRuntimeContextLocked(_snapshot.MechanicsById, _snapshot.CustomMutatorsById, _snapshot.CustomModesById);
            _snapshot = _snapshot with { RuntimeContext = runtimeCtx };
            if (_publishGlobalRuntimeHooks)
            {
                ModRuntimeHooks.SetHandlers(_actionHandlers, _triggerHandlers, BuildEffectiveModeHooksLocked(_snapshot.CustomModesById), _pipelineInterceptors);
                ModRuntimeHooks.SetMechanics(_snapshot.MechanicsById.Values);
                MutatorCatalog.ClearModMutators();
                MutatorCatalog.RegisterModMutators(_snapshot.CustomMutatorsById.Values);
            }
        }
    }

    /// <summary>
    /// 从磁盘 mods 目录扫描、强校验并原子加载全部合法 Mod 包至 <see cref="ActiveRepository"/>。
    /// </summary>
    public ModLoadReport LoadFromDirectory(
        string modsRoot,
        GameDataRepository? baseRepositoryOverride = null,
        IReadOnlySet<string>? validVisualWhitelistOverride = null,
        bool keepPreviousOnError = false)
    {
        var (scanned, scanIssues) = ModLoader.ScanDirectory(modsRoot);
        lock (_syncLock)
        {
            _lastModsRoot = modsRoot;
            return CommitCandidatePackagesLocked(
                scanned,
                scanIssues,
                modsRoot,
                baseRepositoryOverride ?? _snapshot.BaseRepository,
                validVisualWhitelistOverride ?? _validVisualWhitelist,
                keepPreviousOnError);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModPackage, object> InitializedPackages = new();

    /// <summary>
    /// 线程安全地对尚未初始化的内存构造 <see cref="ModPackage"/> 执行一次性视觉绑定与哈希补全；
    /// 对已初始化的共享包严禁再次调用破坏性的 <see cref="ModLoader.BuildVisualBindings"/>（其内部会就地 Clear() 字典）。
    /// </summary>
    private static void EnsurePackageVisualMetadataReady(ModPackage pkg)
    {
        if (pkg == null) return;
        if (InitializedPackages.TryGetValue(pkg, out _))
            return;

        // ModLoader.LoadSinglePackage 已在单线程内完成 BuildVisualBindings 与 PackageHash 计算
        if (!string.IsNullOrEmpty(pkg.PackageHash)
            && (pkg.VisualBindingsByCustomId.Count > 0 || (pkg.Cards.Count == 0 && pkg.Events.Count == 0)))
        {
            InitializedPackages.TryAdd(pkg, pkg);
            return;
        }

        lock (pkg)
        {
            if (InitializedPackages.TryGetValue(pkg, out _))
                return;

            if (pkg.VisualBindingsByCustomId.Count == 0
                && (pkg.Cards.Count > 0 || pkg.Events.Count > 0 || pkg.Patches.Count > 0))
            {
                ModLoader.BuildVisualBindings(pkg);
            }
            if (string.IsNullOrEmpty(pkg.PackageHash))
            {
                pkg.PackageHash = ModLoader.ComputePackageHash(pkg);
            }

            InitializedPackages.TryAdd(pkg, pkg);
        }
    }

    private static CustomCardVisualBinding CloneVisualBinding(CustomCardVisualBinding binding, CardTemplate? vanillaCard) => new()
    {
        InstanceId = binding.InstanceId,
        CustomTemplateId = binding.CustomTemplateId,
        ModId = binding.ModId,
        BaseVisualTemplateId = vanillaCard?.Id ?? binding.BaseVisualTemplateId,
        CardType = vanillaCard?.Type ?? binding.CardType,
        Size = vanillaCard?.Size ?? binding.Size,
        TitleZh = binding.TitleZh,
        DescZh = binding.DescZh,
        ActiveTooltipsZh = binding.ActiveTooltipsZh != null ? new List<string>(binding.ActiveTooltipsZh) : new List<string>(),
        PassiveTooltipsZh = binding.PassiveTooltipsZh != null ? new List<string>(binding.PassiveTooltipsZh) : new List<string>(),
        EnchantTooltipsZh = binding.EnchantTooltipsZh != null
            ? binding.EnchantTooltipsZh.ToDictionary(
                kv => kv.Key,
                kv => new List<string>(kv.Value),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
        CustomArtFile = binding.CustomArtFile,
        CustomArtSha256 = binding.CustomArtSha256,
        ArtRect = binding.ArtRect != null
            ? new CustomCardArtRect { Width = binding.ArtRect.Width, Height = binding.ArtRect.Height, CropMode = binding.ArtRect.CropMode }
            : null,
        Tags = binding.Tags != null ? new List<string>(binding.Tags) : new List<string>(),
        AccentColor = binding.AccentColor,
        // 双审备忘 #2：同步深拷贝全部 11 个新增契约属性及子对象新字段
        IsPatchOverride = binding.IsPatchOverride,
        IsCustomBorrowShell = binding.IsCustomBorrowShell,
        TitleEn = binding.TitleEn,
        DescEn = binding.DescEn,
        ActiveTooltipsEn = binding.ActiveTooltipsEn != null ? new List<string>(binding.ActiveTooltipsEn) : null,
        PassiveTooltipsEn = binding.PassiveTooltipsEn != null ? new List<string>(binding.PassiveTooltipsEn) : null,
        EnchantTooltipsEn = binding.EnchantTooltipsEn?.ToDictionary(
            kv => kv.Key,
            kv => new List<string>(kv.Value),
            StringComparer.OrdinalIgnoreCase),
        VisualTarget = string.IsNullOrWhiteSpace(binding.VisualTarget)
            ? CustomCardVisualBinding.ResolveDefaultVisualTarget(vanillaCard?.Type ?? binding.CardType)
            : binding.VisualTarget,
        CustomBgArtFile = binding.CustomBgArtFile,
        CustomBgArtSha256 = binding.CustomBgArtSha256,
        MonsterPreview = binding.MonsterPreview?.Clone(),
        TierAttributes = binding.TierAttributes?.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, double>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
        EnchantmentPreviewOps = binding.EnchantmentPreviewOps?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(op => new ResolvedPreviewAttrOp
            {
                Attribute = op.Attribute,
                Operation = op.Operation,
                Value = op.Value,
                SourceAttribute = op.SourceAttribute,
                SourceMultiplier = op.SourceMultiplier,
                SourceAddOffset = op.SourceAddOffset
            }).ToList(),
            StringComparer.OrdinalIgnoreCase),
        CounterDisplays = binding.CounterDisplays?.Select(cd => new ResolvedCounterDisplay
        {
            LabelZh = cd.LabelZh,
            SyncAttribute = cd.SyncAttribute,
            MaxValue = cd.MaxValue,
            MaxAttr = cd.MaxAttr,
            ColorHex = cd.ColorHex,
            HideWhenZero = cd.HideWhenZero,
            Style = cd.Style,
            ValueType = cd.ValueType,
            Format = cd.Format,
            Unit = cd.Unit,
            Position = cd.Position
        }).ToList()
    };

    /// <summary>
    /// 直接校验并原子加载一组内存构造的 <see cref="ModPackage"/>（适用于单元测试、热补丁或远程包注入）。
    /// </summary>
    public ModLoadReport LoadPackages(
        IReadOnlyList<ModPackage> candidatePackages,
        string sourceLabel = "<in-memory>",
        GameDataRepository? baseRepositoryOverride = null,
        IReadOnlySet<string>? validVisualWhitelistOverride = null,
        bool keepPreviousOnError = false)
    {
        ArgumentNullException.ThrowIfNull(candidatePackages);
        for (int i = 0; i < candidatePackages.Count; i++)
        {
            EnsurePackageVisualMetadataReady(candidatePackages[i]);
        }

        lock (_syncLock)
        {
            return CommitCandidatePackagesLocked(
                candidatePackages,
                Array.Empty<ModValidationIssue>(),
                sourceLabel,
                baseRepositoryOverride ?? _snapshot.BaseRepository,
                validVisualWhitelistOverride ?? _validVisualWhitelist,
                keepPreviousOnError);
        }
    }

    /// <summary>
    /// 热重载最近一次加载的 mods 目录；若设置 <paramref name="keepPreviousOnError"/> 为 true 且存在错误则自动保留旧的健康快照。
    /// </summary>
    public ModLoadReport Reload(bool keepPreviousOnError = true)
    {
        string? root;
        lock (_syncLock) root = _lastModsRoot;

        if (string.IsNullOrWhiteSpace(root))
            return _snapshot.Report;

        return LoadFromDirectory(root, keepPreviousOnError: keepPreviousOnError);
    }

    /// <summary>
    /// 原子回滚到纯净基础数据仓库（移除本注册表的 Mod 卡牌/野怪/模式/词典覆盖；仅主注册表同步清空全局自定义规则钩子）。
    /// </summary>
    public void RollbackToBase()
    {
        lock (_syncLock)
        {
            _snapshot = BuildEmptySnapshot(_snapshot.BaseRepository, _lastModsRoot ?? string.Empty);
            if (_publishGlobalRuntimeHooks)
            {
                ModRuntimeHooks.ClearAll();
            }
        }
    }

    /// <summary>导出供服务端 /api/mods/manifest 响应客户端的标准增量同步清单 DTO。</summary>
    public ModManifestDto BuildClientManifestDto() => _snapshot.ManifestDto;

    /// <summary>导出全部自定义卡视觉绑定字典 JSON（供客户端视觉映射同步）。</summary>
    public string ExportVisualCatalogJson() => _snapshot.VisualCatalogJson;

    /// <summary>导出合并后的四层中文外挂词典与确定性 SHA-256。</summary>
    public MergedLocalizationExport ExportMergedLocalization() => _snapshot.ActiveLocalization.ExportMergedOverlay();

    /// <summary>
    /// 按自定义模板 ID 解析客户端视觉绑定；传入 <paramref name="instanceId"/> 时返回锚定该实例 ID 的独立副本。
    /// </summary>
    public bool TryResolveVisualBinding(string? customTemplateId, out CustomCardVisualBinding? binding, string? instanceId = null)
    {
        if (!string.IsNullOrWhiteSpace(customTemplateId)
            && _snapshot.VisualBindingsByCustomId.TryGetValue(customTemplateId, out var tplBinding))
        {
            binding = string.IsNullOrWhiteSpace(instanceId)
                ? tplBinding
                : tplBinding.BindToInstance(instanceId);
            return true;
        }
        binding = null;
        return false;
    }

    /// <summary>按 SHA-256 哈希解析可下载的客户端 Mod 资产。</summary>
    public bool TryResolveAssetBySha256(string? sha256, out ModAssetEntry? asset)
    {
        if (!string.IsNullOrWhiteSpace(sha256)
            && _snapshot.AssetsBySha256.TryGetValue(sha256.Trim(), out var found))
        {
            asset = found;
            return true;
        }
        asset = null;
        return false;
    }

    /// <summary>按 ModId + 逻辑路径解析客户端 Mod 资产。</summary>
    public bool TryResolveAsset(string? modId, string? logicalPath, out ModAssetEntry? asset)
    {
        if (!string.IsNullOrWhiteSpace(modId) && !string.IsNullOrWhiteSpace(logicalPath))
        {
            string key = $"{modId.Trim()}:{ModValidator.NormalizeLogicalPath(logicalPath)}";
            if (_snapshot.AssetsByModAndPath.TryGetValue(key, out var found))
            {
                asset = found;
                return true;
            }
        }
        asset = null;
        return false;
    }

    /// <summary>按模式 ID 查找 Mod 注册的自定义玩法模式。</summary>
    public CustomModeDefinition? GetCustomMode(string? modeId)
    {
        if (string.IsNullOrWhiteSpace(modeId)) return null;
        return _snapshot.CustomModesById.TryGetValue(modeId.Trim(), out var mode) ? mode : null;
    }

    private ModLoadReport CommitCandidatePackagesLocked(
        IReadOnlyList<ModPackage> candidates,
        IReadOnlyList<ModValidationIssue> scanIssues,
        string sourceLabel,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? whitelist,
        bool keepPreviousOnError)
    {
        _validVisualWhitelist = whitelist;

        // 剔除在扫描阶段就发生不可恢复解析错误的 Mod，其余送入 ModValidator 做拓扑排序与跨包深校验
        var scanFatalMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in scanIssues)
        {
            if (issue.Severity == ModValidationSeverity.Error)
                scanFatalMods.Add(issue.ModId);
        }

        var cleanCandidates = new List<ModPackage>(candidates.Count);
        foreach (var pkg in candidates)
        {
            if (!scanFatalMods.Contains(pkg.Manifest.Id))
                cleanCandidates.Add(pkg);
        }

        var (validOrdered, validationIssues, rejectedIds) = ModValidator.ValidateAndSort(cleanCandidates, baseRepo, whitelist);

        var report = new ModLoadReport
        {
            LoadedAtUtc = DateTimeOffset.UtcNow,
            ModsRootDirectory = sourceLabel
        };
        report.Issues.AddRange(scanIssues);
        report.Issues.AddRange(validationIssues);
        foreach (var rid in scanFatalMods)
        {
            if (!rejectedIds.Contains(rid, StringComparer.OrdinalIgnoreCase))
                rejectedIds.Add(rid);
        }
        report.RejectedModIds.AddRange(rejectedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (keepPreviousOnError && report.HasErrors && _snapshot.LoadedPackages.Count > 0)
        {
            return report;
        }

        // 汇总所有通过校验的 Mod 卡牌、原位补丁、野怪、模式、突变词条、本地化与视觉绑定
        var mergedCardsById = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        var mergedMonsters = new List<MonsterTemplate>();
        var modLocDocs = new List<LocalizationOverlayDocument>();
        var modesMap = new Dictionary<string, CustomModeDefinition>(StringComparer.OrdinalIgnoreCase);
        var mutatorsMap = new Dictionary<string, MutatorMetadata>(StringComparer.OrdinalIgnoreCase);
        var mechanicsMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
        var visualBindingsMap = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase);
        var cardLevelDisplaysById = new Dictionary<string, List<MechanicCounterDisplay>>(StringComparer.OrdinalIgnoreCase);
        var assetsBySha = new Dictionary<string, ModAssetEntry>(StringComparer.OrdinalIgnoreCase);
        var assetsByModPath = new Dictionary<string, ModAssetEntry>(StringComparer.OrdinalIgnoreCase);

        bool forcesIsolation = false;
        int totalEvents = 0;
        int totalPatches = 0;

        // ── 阶段 1：按依赖拓扑序依次应用所有 Mod 的卡牌/字段级增量 Patch/事件/野怪/模式/词条/机制 ──
        foreach (var pkg in validOrdered)
        {
            string modId = pkg.Manifest.Id;
            report.LoadedModIdsInOrder.Add(modId);

            if (pkg.Manifest.ForcesGhostPoolIsolation
                || pkg.Cards.Count > 0 || pkg.Patches.Count > 0 || pkg.Events.Count > 0
                || pkg.Monsters.Count > 0 || pkg.CustomMutators.Count > 0 || pkg.Mechanics.Count > 0)
                forcesIsolation = true;

            var synthesizedLoc = BuildSynthesizedModLocalization(pkg);
            if (synthesizedLoc != null)
                modLocDocs.Add(synthesizedLoc);

            // 新建自定义卡牌转换
            foreach (var cardDef in pkg.Cards)
            {
                var baseVisual = baseRepo.GetCard(cardDef.BaseVisualTemplateId);
                if (baseVisual != null)
                {
                    var tpl = cardDef.ToCardTemplate(baseVisual, modId);
                    mergedCardsById[tpl.Id] = tpl;
                }
                if (!string.IsNullOrWhiteSpace(cardDef.CustomId) && cardDef.CounterDisplays is { Count: > 0 })
                    cardLevelDisplaysById[cardDef.CustomId.Trim()] = new List<MechanicCounterDisplay>(cardDef.CounterDisplays);
            }

            // 原位魔改原版卡补丁增量叠合（支持多 Mod 按拓扑顺序依次 Patch 同一原版卡）
            foreach (var patch in pkg.Patches)
            {
                if (string.IsNullOrWhiteSpace(patch.TargetTemplateId)) continue;
                string tid = patch.TargetTemplateId.Trim();
                var sourceCard = mergedCardsById.TryGetValue(tid, out var alreadyPatched)
                    ? alreadyPatched
                    : baseRepo.GetCard(tid);
                if (sourceCard != null)
                {
                    mergedCardsById[tid] = patch.ApplyToTemplate(sourceCard, modId);
                    totalPatches++;
                }
                if (patch.CounterDisplaysOverride is { Count: > 0 })
                    cardLevelDisplaysById[tid] = new List<MechanicCounterDisplay>(patch.CounterDisplaysOverride);
            }

            // 事件、子步骤与内联野怪转换
            foreach (var ev in pkg.Events)
            {
                totalEvents++;
                var evCardDef = ev.ToCardDefinition();
                var evBase = baseRepo.GetCard(evCardDef.BaseVisualTemplateId);
                if (evBase != null)
                {
                    var tpl = evCardDef.ToCardTemplate(evBase, modId);
                    mergedCardsById[tpl.Id] = tpl;
                }

                foreach (var step in ev.Steps)
                {
                    var stepBase = baseRepo.GetCard(step.BaseVisualTemplateId);
                    if (stepBase != null)
                    {
                        var tpl = step.ToCardTemplate(stepBase, modId);
                        mergedCardsById[tpl.Id] = tpl;
                    }
                }

                if (ev.CombatMonster != null)
                    mergedMonsters.Add(ev.CombatMonster);
            }

            // 独立自定义野怪
            foreach (var m in pkg.Monsters)
                mergedMonsters.Add(m);

            // 自定义模式（D3-P2-05：与 Gate #10 对齐，任何自定义模式一律强制幽灵池隔离，不因 AffectsGhostPool 放开）
            foreach (var mode in pkg.Modes)
            {
                modesMap[mode.ModeId] = mode;
                forcesIsolation = true;
            }

            // P0-22：自定义肉鸽突变词条
            foreach (var mut in pkg.CustomMutators)
            {
                if (mut != null && !string.IsNullOrWhiteSpace(mut.Id))
                {
                    mutatorsMap[mut.Id.Trim()] = mut;
                    forcesIsolation = true;
                }
            }

            // V14 开放声明式沙盒机制定义
            foreach (var mech in pkg.Mechanics)
            {
                if (!string.IsNullOrWhiteSpace(mech.Id))
                {
                    mechanicsMap[mech.Id.Trim()] = mech;
                    forcesIsolation = true;
                }
            }

            foreach (var (logicalPath, asset) in pkg.AssetsByLogicalPath)
            {
                if (!string.IsNullOrEmpty(asset.Sha256))
                    assetsBySha[asset.Sha256] = asset;
                assetsByModPath[$"{modId}:{logicalPath}"] = asset;
            }

            // P0-25：字段级增量合并视觉绑定（多 Mod 叠加 Patch 同一原版卡时保留前置 Mod 改写字段，绝不相互覆盖抹零）
            foreach (var (cid, binding) in pkg.VisualBindingsByCustomId)
            {
                var vanillaCard = baseRepo.GetCard(cid);
                if (binding.IsPatchOverride && visualBindingsMap.TryGetValue(cid, out var existingPatchBinding))
                {
                    MergePatchVisualBindingInPlace(existingPatchBinding, binding);
                }
                else
                {
                    visualBindingsMap[cid] = CloneVisualBinding(binding, vanillaCard);
                }
            }
        }

        // ── 阶段 2（P0-25）：在全部 Patch 与机制合并完毕后的最终态 CardTemplate 上统一重算五阶数值/附魔预览/计数器 ──
        foreach (var (cid, resolved) in visualBindingsMap)
        {
            if (mergedCardsById.TryGetValue(cid, out var mergedTpl))
            {
                resolved.CardType = mergedTpl.Type;
                resolved.Size = mergedTpl.Size;
                resolved.VisualTarget = CustomCardVisualBinding.ResolveDefaultVisualTarget(mergedTpl.Type);
                var mergedTiers = BuildTierAttributesFromTemplate(mergedTpl);
                if (mergedTiers is { Count: > 0 })
                    resolved.TierAttributes = mergedTiers;
                var mergedEnchOps = ExtractEnchantmentPreviewOps(mergedTpl.Enchantments);
                if (mergedEnchOps is { Count: > 0 })
                    resolved.EnchantmentPreviewOps = mergedEnchOps;
                if (mergedTpl.Tags is { Count: > 0 })
                    resolved.Tags = new List<string>(mergedTpl.Tags);

                cardLevelDisplaysById.TryGetValue(cid, out var explicitDisplays);
                var displays = ResolveCounterDisplaysForCard(explicitDisplays, mergedTpl.CustomMechanics, mechanicsMap);
                if (displays.Count > 0)
                    resolved.CounterDisplays = displays;
            }
        }

        // 叠加通过 ICustomCardProvider 注册的扩展内容（D3-P2-05：为自定义卡同步补齐视觉绑定，避免客户端断壳）
        if (_cardProviders.Count > 0)
        {
            forcesIsolation = true;
            foreach (var provider in _cardProviders)
            {
                foreach (var c in provider.GetCustomCards(baseRepo))
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Id)) continue;
                    mergedCardsById[c.Id] = c;
                    if (c.IsCustomModCard && !visualBindingsMap.ContainsKey(c.Id))
                    {
                        string baseVisualId = Guid.TryParse(c.Id, out _)
                            ? c.Id
                            : (baseRepo.CardsById.Values
                                .FirstOrDefault(b => !b.IsCustomModCard &&
                                                     string.Equals(b.Type, c.Type, StringComparison.OrdinalIgnoreCase) &&
                                                     Guid.TryParse(b.Id, out _) &&
                                                     (whitelist == null || whitelist.Contains(b.Id)))
                                ?.Id ?? "5e17cfc3-25c0-4a74-8a33-07ee972e6f6e");
                        visualBindingsMap[c.Id] = new CustomCardVisualBinding
                        {
                            CustomTemplateId = c.Id,
                            ModId = string.IsNullOrWhiteSpace(c.CustomModOrigin) ? "provider" : c.CustomModOrigin!,
                            CardType = string.IsNullOrWhiteSpace(c.Type) ? "Item" : c.Type,
                            Size = string.IsNullOrWhiteSpace(c.Size) ? "Medium" : c.Size,
                            BaseVisualTemplateId = baseVisualId,
                            TitleZh = c.Localization?.Title?.Text ?? c.InternalName ?? c.Id,
                            DescZh = c.InternalDescription ?? string.Empty,
                            IsPatchOverride = false,
                        };
                    }
                }
                foreach (var m in provider.GetCustomMonsters(baseRepo))
                    if (m != null) mergedMonsters.Add(m);

                var overrides = provider.GetLocalizationOverrides("zh-CN");
                if (overrides != null && overrides.Count > 0)
                {
                    var providerDoc = new LocalizationOverlayDocument();
                    foreach (var (k, v) in overrides)
                        providerDoc.HashOverrides[k] = v;
                    modLocDocs.Add(providerDoc);
                }
            }
        }

        var mergedCards = mergedCardsById.Values.ToList();

        // 构建或派生四层外挂本地化存储
        OverlayLocalizationStore activeLoc = CreateDerivedLocalizationStore(baseRepo.Localization, modLocDocs);

        // 基于纯净 BaseRepository 派生不可变 ActiveRepository 副本
        GameDataRepository activeRepo = (mergedCards.Count > 0 || mergedMonsters.Count > 0 || modLocDocs.Count > 0)
            ? baseRepo.CreateWithOverlay(mergedCards, mergedMonsters, activeLoc)
            : baseRepo;

        string modSetHash = ComputeCombinedModSetHash(validOrdered);
        var (visualJson, visualHash) = SerializeVisualCatalog(visualBindingsMap);

        report.TotalCustomCards = mergedCards.Count;
        report.TotalCardPatches = totalPatches;
        report.TotalCustomEvents = totalEvents;
        report.TotalCustomMonsters = mergedMonsters.Count;
        report.TotalCustomModes = modesMap.Count;
        report.TotalCustomMechanics = mechanicsMap.Count;
        report.TotalAssets = assetsBySha.Count;
        report.ModSetHash = modSetHash;
        report.ForcesGhostPoolIsolation = forcesIsolation;

        var manifestDto = BuildManifestDtoInternal(validOrdered, assetsBySha, report, activeLoc.MergedOverlaySha256, visualHash);

        var frozenMechanics = mechanicsMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var frozenMutators = mutatorsMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var frozenModes = modesMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var runtimeCtx = BuildRuntimeContextLocked(frozenMechanics, frozenMutators, frozenModes);

        _snapshot = new RegistrySnapshot
        {
            BaseRepository = baseRepo,
            ActiveRepository = activeRepo,
            ActiveLocalization = activeLoc,
            LoadedPackages = validOrdered.AsReadOnly(),
            Report = report,
            CustomModesById = frozenModes,
            CustomMutatorsById = frozenMutators,
            MechanicsById = frozenMechanics,
            RuntimeContext = runtimeCtx,
            VisualBindingsByCustomId = visualBindingsMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            AssetsBySha256 = assetsBySha.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            AssetsByModAndPath = assetsByModPath.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            VisualCatalogJson = visualJson,
            VisualCatalogHash = visualHash,
            ManifestDto = manifestDto
        };

        if (_publishGlobalRuntimeHooks)
        {
            ModRuntimeHooks.SetHandlers(_actionHandlers, _triggerHandlers, BuildEffectiveModeHooksLocked(frozenModes), _pipelineInterceptors);
            ModRuntimeHooks.SetMechanics(mechanicsMap.Values);
            MutatorCatalog.ClearModMutators();
            MutatorCatalog.RegisterModMutators(mutatorsMap.Values);
        }
        return report;
    }

    private static void MergePatchVisualBindingInPlace(CustomCardVisualBinding target, CustomCardVisualBinding delta)
    {
        target.IsPatchOverride = true;
        target.IsCustomBorrowShell = false;
        if (!string.IsNullOrWhiteSpace(delta.ModId)) target.ModId = delta.ModId;
        if (!string.IsNullOrWhiteSpace(delta.TitleZh)) target.TitleZh = delta.TitleZh;
        if (!string.IsNullOrWhiteSpace(delta.TitleEn)) target.TitleEn = delta.TitleEn;
        if (!string.IsNullOrWhiteSpace(delta.DescZh)) target.DescZh = delta.DescZh;
        if (!string.IsNullOrWhiteSpace(delta.DescEn)) target.DescEn = delta.DescEn;
        if (delta.ActiveTooltipsZh is { Count: > 0 }) target.ActiveTooltipsZh = new List<string>(delta.ActiveTooltipsZh);
        if (delta.PassiveTooltipsZh is { Count: > 0 }) target.PassiveTooltipsZh = new List<string>(delta.PassiveTooltipsZh);
        if (delta.EnchantTooltipsZh is { Count: > 0 })
        {
            foreach (var (k, v) in delta.EnchantTooltipsZh)
                target.EnchantTooltipsZh[k] = new List<string>(v);
        }
        if (delta.ActiveTooltipsEn is { Count: > 0 }) target.ActiveTooltipsEn = new List<string>(delta.ActiveTooltipsEn);
        if (delta.PassiveTooltipsEn is { Count: > 0 }) target.PassiveTooltipsEn = new List<string>(delta.PassiveTooltipsEn);
        if (delta.EnchantTooltipsEn is { Count: > 0 })
        {
            target.EnchantTooltipsEn ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in delta.EnchantTooltipsEn)
                target.EnchantTooltipsEn[k] = new List<string>(v);
        }
        if (!string.IsNullOrWhiteSpace(delta.CustomArtFile))
        {
            target.CustomArtFile = delta.CustomArtFile;
            target.CustomArtSha256 = delta.CustomArtSha256;
        }
        if (!string.IsNullOrWhiteSpace(delta.CustomBgArtFile))
        {
            target.CustomBgArtFile = delta.CustomBgArtFile;
            target.CustomBgArtSha256 = delta.CustomBgArtSha256;
        }
        if (delta.ArtRect != null)
            target.ArtRect = new CustomCardArtRect { Width = delta.ArtRect.Width, Height = delta.ArtRect.Height, CropMode = delta.ArtRect.CropMode };
        if (!string.IsNullOrWhiteSpace(delta.AccentColor)) target.AccentColor = delta.AccentColor;
        if (delta.Tags is { Count: > 0 })
        {
            foreach (var t in delta.Tags)
                if (!target.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) target.Tags.Add(t);
        }
        if (delta.CounterDisplays is { Count: > 0 })
            target.CounterDisplays = new List<ResolvedCounterDisplay>(delta.CounterDisplays);
    }

    private IReadOnlyList<IGameModeHook> BuildEffectiveModeHooksLocked(IReadOnlyDictionary<string, CustomModeDefinition> modes)
    {
        var map = new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase);
        // 先装配 JSON 声明式 CustomModeDefinition 默认钩子，再由显式注册的 C# IGameModeHook 覆盖优先
        foreach (var mode in modes.Values)
        {
            if (mode != null && !string.IsNullOrWhiteSpace(mode.ModeId))
                map[mode.ModeId.Trim()] = new DeclarativeGameModeHook(mode);
        }
        foreach (var m in _modeHooks)
        {
            if (m != null && !string.IsNullOrWhiteSpace(m.ModeId))
                map[m.ModeId.Trim()] = m;
        }
        return map.Values.ToArray();
    }

    private ModRuntimeContext BuildRuntimeContextLocked(
        FrozenDictionary<string, MechanicDefinition> mechanicsById,
        FrozenDictionary<string, MutatorMetadata> mutatorsById,
        FrozenDictionary<string, CustomModeDefinition> modesById)
    {
        var actDict = new Dictionary<string, ICustomActionHandler>(StringComparer.Ordinal);
        foreach (var h in _actionHandlers)
        {
            if (h != null && !string.IsNullOrWhiteSpace(h.ActionType) && h.ActionType.StartsWith("TActionMod_", StringComparison.Ordinal))
                actDict[h.ActionType] = h;
        }

        var trgDict = new Dictionary<string, ICustomTriggerHandler>(StringComparer.Ordinal);
        foreach (var h in _triggerHandlers)
        {
            if (h != null && !string.IsNullOrWhiteSpace(h.TriggerType) && h.TriggerType.StartsWith("TTriggerMod_", StringComparison.Ordinal))
                trgDict[h.TriggerType] = h;
        }

        var modeDict = new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in BuildEffectiveModeHooksLocked(modesById))
        {
            modeDict[m.ModeId.Trim()] = m;
        }

        var interceptors = _pipelineInterceptors
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.InterceptorId))
            .ToArray();

        return new ModRuntimeContext(
            MechanicsById: mechanicsById,
            ActionHandlers: actDict.Count > 0 ? actDict.ToFrozenDictionary(StringComparer.Ordinal) : FrozenDictionary<string, ICustomActionHandler>.Empty,
            TriggerHandlers: trgDict.Count > 0 ? trgDict.ToFrozenDictionary(StringComparer.Ordinal) : FrozenDictionary<string, ICustomTriggerHandler>.Empty,
            ModeHooks: modeDict.Count > 0 ? modeDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : FrozenDictionary<string, IGameModeHook>.Empty,
            PipelineInterceptors: interceptors,
            CustomMutatorsById: mutatorsById,
            CustomModesById: modesById);
    }

    private static RegistrySnapshot BuildEmptySnapshot(GameDataRepository baseRepo, string sourceLabel)
    {
        var activeLoc = CreateDerivedLocalizationStore(baseRepo.Localization, null);
        var report = new ModLoadReport
        {
            LoadedAtUtc = DateTimeOffset.UtcNow,
            ModsRootDirectory = sourceLabel,
            ModSetHash = ComputeCombinedModSetHash(Array.Empty<ModPackage>()),
            ForcesGhostPoolIsolation = false
        };

        var emptyVisualMap = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase);
        var (visualJson, visualHash) = SerializeVisualCatalog(emptyVisualMap);
        var dto = BuildManifestDtoInternal(
            Array.Empty<ModPackage>(),
            new Dictionary<string, ModAssetEntry>(StringComparer.OrdinalIgnoreCase),
            report,
            activeLoc.MergedOverlaySha256,
            visualHash);

        return new RegistrySnapshot
        {
            BaseRepository = baseRepo,
            ActiveRepository = baseRepo,
            ActiveLocalization = activeLoc,
            LoadedPackages = Array.Empty<ModPackage>(),
            Report = report,
            CustomModesById = FrozenDictionary<string, CustomModeDefinition>.Empty,
            CustomMutatorsById = FrozenDictionary<string, MutatorMetadata>.Empty,
            MechanicsById = FrozenDictionary<string, MechanicDefinition>.Empty,
            RuntimeContext = ModRuntimeContext.Empty,
            VisualBindingsByCustomId = FrozenDictionary<string, CustomCardVisualBinding>.Empty,
            AssetsBySha256 = FrozenDictionary<string, ModAssetEntry>.Empty,
            AssetsByModAndPath = FrozenDictionary<string, ModAssetEntry>.Empty,
            VisualCatalogJson = visualJson,
            VisualCatalogHash = visualHash,
            ManifestDto = dto
        };
    }

    private static OverlayLocalizationStore CreateDerivedLocalizationStore(
        ILocalizationStore? baseLocalization,
        IEnumerable<LocalizationOverlayDocument>? modOverlays)
    {
        if (baseLocalization is OverlayLocalizationStore existingOverlay)
        {
            // 派生全新实例避免污染纯净 BaseRepository 持有的旧实例
            var baseExport = existingOverlay.ExportMergedOverlay();
            var derived = new OverlayLocalizationStore(
                existingOverlay.OfficialBase,
                existingOverlay.OverlayFilePath,
                initialFileOverlay: null,
                initialModOverlays: modOverlays);
            // 若原基础实例是内存注入的外挂词典（无磁盘文件），沿用其导出文件层
            if (string.IsNullOrWhiteSpace(existingOverlay.OverlayFilePath) && baseExport.TotalOverlayEntryCount > 0)
            {
                derived.UpdateFileOverlay(baseExport.Document);
                derived.SetModOverlays(modOverlays);
            }
            return derived;
        }

        return new OverlayLocalizationStore(
            baseLocalization ?? LocalizationLoader.Empty,
            overlayFilePath: null,
            initialFileOverlay: null,
            initialModOverlays: modOverlays);
    }

    private static LocalizationOverlayDocument? BuildSynthesizedModLocalization(ModPackage pkg)
    {
        bool hasCards = pkg.Cards.Count > 0 || pkg.Events.Count > 0 || pkg.Patches.Any(p => p.HasVisualOrTextOverride);
        if (!hasCards && pkg.LocalizationOverlay == null)
            return null;

        var doc = new LocalizationOverlayDocument
        {
            Revision = $"{pkg.Manifest.Id}@{pkg.Manifest.Version}",
            Priority = "override"
        };

        void AppendCardLoc(CustomCardDefinition card)
        {
            if (string.IsNullOrWhiteSpace(card.CustomId)) return;
            if (string.IsNullOrWhiteSpace(card.TitleZh) && string.IsNullOrWhiteSpace(card.DescZh)
                && card.ActiveTooltipsZh.Count == 0 && card.PassiveTooltipsZh.Count == 0
                && card.EnchantTooltipsZh.Count == 0)
                return;

            var ov = new CardLocalizationOverride
            {
                Title = !string.IsNullOrWhiteSpace(card.TitleZh) ? card.TitleZh : null,
                Description = !string.IsNullOrWhiteSpace(card.DescZh) ? card.DescZh : null
            };

            if (card.ActiveTooltipsZh.Count > 0 || card.PassiveTooltipsZh.Count > 0)
            {
                ov.Tooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int idx = 0;
                foreach (var tip in card.ActiveTooltipsZh)
                    ov.Tooltips[$"active_{idx++}"] = tip;
                idx = 0;
                foreach (var tip in card.PassiveTooltipsZh)
                    ov.Tooltips[$"passive_{idx++}"] = tip;
            }

            if (card.EnchantTooltipsZh.Count > 0)
            {
                ov.Enchantments = card.EnchantTooltipsZh.ToDictionary(
                    kv => kv.Key,
                    kv => new List<string>(kv.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            doc.CardOverrides[card.CustomId] = ov;
        }

        foreach (var c in pkg.Cards) AppendCardLoc(c);
        foreach (var ev in pkg.Events)
        {
            AppendCardLoc(ev.ToCardDefinition());
            foreach (var st in ev.Steps) AppendCardLoc(st);
        }
        foreach (var p in pkg.Patches)
        {
            if (string.IsNullOrWhiteSpace(p.TargetTemplateId) || !p.HasVisualOrTextOverride)
                continue;
            var ov = new CardLocalizationOverride
            {
                Title = !string.IsNullOrWhiteSpace(p.TitleZh) ? p.TitleZh : null,
                Description = !string.IsNullOrWhiteSpace(p.DescZh) ? p.DescZh : null
            };
            if (p.ActiveTooltipsZh is { Count: > 0 } || p.PassiveTooltipsZh is { Count: > 0 })
            {
                ov.Tooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int idx = 0;
                if (p.ActiveTooltipsZh != null)
                    foreach (var tip in p.ActiveTooltipsZh) ov.Tooltips[$"active_{idx++}"] = tip;
                idx = 0;
                if (p.PassiveTooltipsZh != null)
                    foreach (var tip in p.PassiveTooltipsZh) ov.Tooltips[$"passive_{idx++}"] = tip;
            }
            if (p.EnchantTooltipsZh is { Count: > 0 })
            {
                ov.Enchantments = p.EnchantTooltipsZh.ToDictionary(
                    kv => kv.Key,
                    kv => new List<string>(kv.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
            doc.CardOverrides[p.TargetTemplateId.Trim()] = ov;
        }

        // 若 Mod 自带 localization/zh-CN.json，再叠加其显式条目（覆盖自动合成项）
        if (pkg.LocalizationOverlay != null)
        {
            var src = pkg.LocalizationOverlay;
            foreach (var (k, v) in src.HashOverrides)
                doc.HashOverrides[k] = v;

            foreach (var (cid, ov) in src.CardOverrides)
                doc.CardOverrides[cid] = ov;

            if (src.UiExact != null)
            {
                foreach (var (k, v) in src.UiExact.Global)
                    doc.UiExact.Global[k] = v;
                foreach (var (ctxKey, map) in src.UiExact.Contexts)
                {
                    doc.UiExact.Contexts[ctxKey] = new Dictionary<string, string>(map, StringComparer.Ordinal);
                }
            }

            if (src.UiRegex != null)
                doc.UiRegex.AddRange(src.UiRegex);

            if (src.Entries != null)
            {
                foreach (var (k, v) in src.Entries)
                {
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                        doc.UiExact.Global[k] = v;
                }
            }
        }

        return doc;
    }

    private static string ComputeCombinedModSetHash(IReadOnlyList<ModPackage> orderedPackages)
    {
        if (orderedPackages.Count == 0)
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bazaar-mods:empty"))).ToLowerInvariant();

        var sb = new StringBuilder(256);
        sb.Append("bazaar-mods:v1:");
        for (int i = 0; i < orderedPackages.Count; i++)
        {
            var p = orderedPackages[i];
            sb.Append(p.Manifest.Id).Append('@')
              .Append(p.Manifest.Version).Append('=')
              .Append(p.PackageHash).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static (string Json, string Sha256) SerializeVisualCatalog(Dictionary<string, CustomCardVisualBinding> map)
    {
        var ordered = new SortedDictionary<string, CustomCardVisualBinding>(map, StringComparer.OrdinalIgnoreCase);
        string json = JsonSerializer.Serialize(ordered, ExportJsonOptions);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return (json, sha);
    }

    private static ModManifestDto BuildManifestDtoInternal(
        IReadOnlyList<ModPackage> orderedPackages,
        Dictionary<string, ModAssetEntry> assetsBySha,
        ModLoadReport report,
        string localizationHash,
        string visualCatalogHash)
    {
        var dto = new ModManifestDto
        {
            SchemaVersion = 1,
            GeneratedAtUtc = report.LoadedAtUtc,
            ForcesGhostPoolIsolation = report.ForcesGhostPoolIsolation,
            LocalizationHash = localizationHash,
            VisualCatalogHash = visualCatalogHash
        };

        foreach (var pkg in orderedPackages)
        {
            dto.Mods.Add(new ModEntryDto
            {
                Id = pkg.Manifest.Id,
                Name = pkg.Manifest.Name,
                Version = pkg.Manifest.Version,
                Author = pkg.Manifest.Author,
                Scope = pkg.Manifest.Scope,
                PackageHash = pkg.PackageHash,
                CustomCardCount = pkg.VisualBindingsByCustomId.Count,
                CustomModeCount = pkg.Modes.Count,
                CustomMechanicCount = pkg.Mechanics.Count,
                AssetCount = pkg.AssetsByLogicalPath.Count
            });
        }

        foreach (var asset in assetsBySha.Values
                     .OrderBy(a => a.ModId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(a => a.LogicalPath, StringComparer.OrdinalIgnoreCase))
        {
            dto.Assets.Add(new ModAssetDto
            {
                ModId = asset.ModId,
                LogicalPath = asset.LogicalPath,
                Sha256 = asset.Sha256,
                Bytes = asset.Bytes,
                Width = asset.Width,
                Height = asset.Height,
                ContentType = asset.ContentType
            });
        }

        string composite = $"{report.ModSetHash}|{localizationHash}|{visualCatalogHash}";
        dto.ManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composite))).ToLowerInvariant();
        return dto;
    }

    /// <summary>D3-P1-01：从合并后的卡牌模板构造完整五阶基础属性表（先合入 core.Attributes 底表再从 StartingTier 起补齐至 Legendary）。</summary>
    internal static Dictionary<string, Dictionary<string, double>>? BuildTierAttributesFromTemplate(CardTemplate tpl)
    {
        if (tpl == null) return null;
        bool hasTiers = tpl.Tiers is { Count: > 0 };
        bool hasCoreAttrs = tpl.Attributes is { Count: > 0 };
        if (!hasTiers && !hasCoreAttrs) return null;

        var res = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (hasCoreAttrs)
        {
            foreach (var (k, v) in tpl.Attributes!)
                running[k] = v;
        }
        bool started = false;
        string startTierNorm = string.IsNullOrWhiteSpace(tpl.StartingTier) ? "Bronze" : tpl.StartingTier.Trim();
        foreach (var tierName in new[] { "Bronze", "Silver", "Gold", "Diamond", "Legendary" })
        {
            TierData? td = null;
            bool hasExplicit = hasTiers && tpl.Tiers!.TryGetValue(tierName, out td) && td != null;
            if (hasExplicit || string.Equals(tierName, startTierNorm, StringComparison.OrdinalIgnoreCase))
                started = true;
            if (hasExplicit && td!.Attributes != null)
            {
                foreach (var (k, v) in td.Attributes)
                    running[k] = v;
            }
            if (started && running.Count > 0)
                res[tierName] = new Dictionary<string, double>(running, StringComparer.OrdinalIgnoreCase);
        }
        return res.Count > 0 ? res : null;
    }

    /// <summary>
    /// 通用提取卡牌全部附魔的预览属性运算表：
    /// 1. 收集 enchantments[name].attributes 中的直接属性（对 Multicast=1 兼容官方增量约定为 Add 1）；
    /// 2. 收集 enchantments[name].auras 中作用于自身（TTargetCardSelf）的 TAuraActionCardModifyAttribute（覆盖黑曜石×2/黄金×2/致命+50/闪亮+1/任意自定义附魔）。
    /// </summary>
    internal static Dictionary<string, List<ResolvedPreviewAttrOp>>? ExtractEnchantmentPreviewOps(
        Dictionary<string, EnchantmentData>? enchantments)
    {
        if (enchantments == null || enchantments.Count == 0) return null;
        var result = new Dictionary<string, List<ResolvedPreviewAttrOp>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (enchName, ench) in enchantments)
        {
            if (string.IsNullOrWhiteSpace(enchName) || ench == null) continue;
            var ops = new List<ResolvedPreviewAttrOp>();

            if (ench.Attributes != null)
            {
                foreach (var (attrKey, attrVal) in ench.Attributes)
                {
                    if (string.IsNullOrWhiteSpace(attrKey)) continue;
                    // 与 CardInstance.RebuildTierAndEnchantmentData (L709) 严格同源：ench.Attributes 统一按 Add 语义叠加（客户端对 Multicast 自动按隐含基准 1 累加）
                    ops.Add(new ResolvedPreviewAttrOp
                    {
                        Attribute = attrKey,
                        Operation = "Add",
                        Value = attrVal
                    });
                }
            }

            if (ench.Auras != null)
            {
                foreach (var aura in ench.Auras.Values)
                {
                    var act = aura?.Action;
                    if (act == null || !string.Equals(act.Type, "TAuraActionCardModifyAttribute", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (act.Target != null && !string.Equals(act.Target.Type, "TTargetCardSelf", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrWhiteSpace(act.AttributeType))
                        continue;

                    string opName = string.IsNullOrWhiteSpace(act.Operation) ? "Add" : act.Operation!;
                    double? fixedNum = act.GetFixedNumericValue();
                    string? srcAttr = null;
                    double? srcMult = null;
                    double? srcAddOffset = null;
                    if (!fixedNum.HasValue)
                    {
                        var vn = act.GetValueNode(Data.GameDatabaseLoader.JsonOptions);
                        fixedNum = vn?.GetFixedNumericValue();
                        if (!fixedNum.HasValue && vn != null && !string.IsNullOrWhiteSpace(vn.AttributeType))
                        {
                            srcAttr = vn.AttributeType;
                            // D3-P1-02：提取 ValueModifierNode 中的比例乘数与加偏移（如伤害转 50% 护盾 + 2）
                            if (vn.Modifier != null)
                            {
                                double? modNum = vn.Modifier.Value?.GetFixedNumericValue();
                                if (modNum.HasValue && double.IsFinite(modNum.Value))
                                {
                                    string mode = (vn.Modifier.ModifyMode ?? "Add").Trim();
                                    if (mode.Equals("Multiply", StringComparison.OrdinalIgnoreCase))
                                        srcMult = modNum.Value;
                                    else if (mode.Equals("Divide", StringComparison.OrdinalIgnoreCase) && Math.Abs(modNum.Value) > 1e-9)
                                        srcMult = 1.0 / modNum.Value;
                                    else if (mode.Equals("Add", StringComparison.OrdinalIgnoreCase))
                                        srcAddOffset = modNum.Value;
                                    else if (mode.Equals("Subtract", StringComparison.OrdinalIgnoreCase))
                                        srcAddOffset = -modNum.Value;
                                }
                            }
                        }
                    }

                    if (fixedNum.HasValue || !string.IsNullOrEmpty(srcAttr))
                    {
                        ops.Add(new ResolvedPreviewAttrOp
                        {
                            Attribute = act.AttributeType!,
                            Operation = opName,
                            Value = fixedNum ?? 0,
                            SourceAttribute = srcAttr,
                            SourceMultiplier = srcMult,
                            SourceAddOffset = srcAddOffset
                        });
                    }
                }
            }

            result[enchName.Trim()] = ops;
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 通用解析某张卡牌的实时数值徽章配置（支持卡牌级直配 + 机制级显式声明 + ModifyCounter 自动推导）：
    /// 1. 优先消费卡牌自身声明的 counterDisplays（不依赖机制钩子即可展示任意 Custom_0..Custom_9 或原生属性）；
    /// 2. 其次消费机制显式声明的 counterDisplay（自动解析 maxParam 参数或 maxConst 常量）；
    /// 3. 若机制未手写 counterDisplay，但步骤流中使用了 ModifyCounter + syncAttribute，则自动推导默认徽章，
    ///    确保任意玩家/开发者自创的叠层、充能、进度、倒计时机制都能开箱即得原生 3D 实时卡面显示。
    /// </summary>
    internal static List<ResolvedCounterDisplay> ResolveCounterDisplaysForCard(
        IReadOnlyList<MechanicCounterDisplay>? cardLevelDisplays,
        IReadOnlyList<CardMechanicAttachment>? attachments,
        IReadOnlyDictionary<string, MechanicDefinition> mechanicsMap)
    {
        var list = new List<ResolvedCounterDisplay>();
        var usedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cardLevelDisplays is { Count: > 0 })
        {
            foreach (var cd in cardLevelDisplays)
            {
                if (list.Count >= 4) break;
                if (cd == null || string.IsNullOrWhiteSpace(cd.SyncAttribute)) continue;
                string slot = cd.SyncAttribute.Trim();
                if (!usedSlots.Add(slot)) continue;

                list.Add(new ResolvedCounterDisplay
                {
                    LabelZh = cd.LabelZh?.Trim() ?? string.Empty,
                    SyncAttribute = slot,
                    MaxValue = cd.MaxConst,
                    MaxAttr = string.IsNullOrWhiteSpace(cd.MaxAttr) ? null : cd.MaxAttr!.Trim(),
                    ColorHex = string.IsNullOrWhiteSpace(cd.ColorHex) ? "#F97316" : cd.ColorHex!.Trim(),
                    HideWhenZero = cd.HideWhenZero,
                    Style = string.IsNullOrWhiteSpace(cd.Style) ? null : cd.Style!.Trim(),
                    ValueType = string.IsNullOrWhiteSpace(cd.ValueType) ? null : cd.ValueType!.Trim(),
                    Format = string.IsNullOrWhiteSpace(cd.Format) ? null : cd.Format!.Trim(),
                    Unit = string.IsNullOrWhiteSpace(cd.Unit) ? null : cd.Unit!.Trim(),
                    Position = string.IsNullOrWhiteSpace(cd.Position) ? null : cd.Position!.Trim()
                });
            }
        }

        if (attachments is null || attachments.Count == 0 || list.Count >= 4)
            return list;

        foreach (var att in attachments)
        {
            if (list.Count >= 4) break;
            if (att == null || !att.Enabled || string.IsNullOrWhiteSpace(att.MechanicId)) continue;
            if (!mechanicsMap.TryGetValue(att.MechanicId.Trim(), out var mech) || mech == null) continue;

            // D3-P1-06：支持单机制多槽位 CounterDisplays（兼容旧单槽 CounterDisplay，最多保留 4 槽）
            var mechDisplays = new List<MechanicCounterDisplay>();
            if (mech.CounterDisplay != null) mechDisplays.Add(mech.CounterDisplay);
            if (mech.CounterDisplays is { Count: > 0 }) mechDisplays.AddRange(mech.CounterDisplays);

            if (mechDisplays.Count > 0)
            {
                foreach (var cd in mechDisplays)
                {
                    if (list.Count >= 4) break;
                    if (cd == null || string.IsNullOrWhiteSpace(cd.SyncAttribute)) continue;
                    string slot = cd.SyncAttribute.Trim();
                    if (!usedSlots.Add(slot)) continue;

                    int? maxVal = cd.MaxConst;
                    if (!string.IsNullOrWhiteSpace(cd.MaxParam))
                        maxVal = TryResolveMechanicIntParam(mech, att, cd.MaxParam!) ?? maxVal;

                    list.Add(new ResolvedCounterDisplay
                    {
                        LabelZh = !string.IsNullOrWhiteSpace(cd.LabelZh) ? cd.LabelZh.Trim() : (mech.NameZh ?? "层数"),
                        SyncAttribute = slot,
                        MaxValue = maxVal,
                        MaxAttr = string.IsNullOrWhiteSpace(cd.MaxAttr) ? null : cd.MaxAttr!.Trim(),
                        ColorHex = string.IsNullOrWhiteSpace(cd.ColorHex) ? "#F97316" : cd.ColorHex!.Trim(),
                        HideWhenZero = cd.HideWhenZero,
                        Style = string.IsNullOrWhiteSpace(cd.Style) ? null : cd.Style!.Trim(),
                        ValueType = string.IsNullOrWhiteSpace(cd.ValueType) ? null : cd.ValueType!.Trim(),
                        Format = string.IsNullOrWhiteSpace(cd.Format) ? null : cd.Format!.Trim(),
                        Unit = string.IsNullOrWhiteSpace(cd.Unit) ? null : cd.Unit!.Trim(),
                        Position = string.IsNullOrWhiteSpace(cd.Position) ? null : cd.Position!.Trim()
                    });
                }
                continue;
            }

            // 自动推导兜底：扫描该机制任意步骤里的 ModifyCounter.syncAttribute
            string? autoSlot = FindFirstSyncAttribute(mech.Hooks);
            if (!string.IsNullOrWhiteSpace(autoSlot) && usedSlots.Add(autoSlot!))
            {
                int? inferredMax = TryResolveMechanicIntParam(mech, att, "threshold")
                                ?? TryResolveMechanicIntParam(mech, att, "maxStacks")
                                ?? TryResolveMechanicIntParam(mech, att, "charges");
                list.Add(new ResolvedCounterDisplay
                {
                    LabelZh = !string.IsNullOrWhiteSpace(mech.NameZh) ? mech.NameZh.Trim() : "计数",
                    SyncAttribute = autoSlot!,
                    MaxValue = inferredMax,
                    ColorHex = "#38BDF8",
                    HideWhenZero = false
                });
            }
        }

        return list;
    }

    private static int? TryResolveMechanicIntParam(MechanicDefinition mech, CardMechanicAttachment att, string paramKey)
    {
        if (att.Params != null && att.Params.TryGetValue(paramKey, out var boundEl))
        {
            if (TryExtractInt(boundEl, out int bv)) return bv;
        }
        var pdef = mech.Parameters.FirstOrDefault(p => string.Equals(p.Key, paramKey, StringComparison.OrdinalIgnoreCase));
        if (pdef?.DefaultValue is { } defEl && TryExtractInt(defEl, out int dv))
            return dv;
        return null;
    }

    private static bool TryExtractInt(System.Text.Json.JsonElement el, out int value)
    {
        value = 0;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            if (el.TryGetInt32(out value)) return true;
            if (el.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (int)Math.Round(d);
                return true;
            }
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(el.GetString(), out value))
        {
            return true;
        }
        return false;
    }

    private static string? FindFirstSyncAttribute(IReadOnlyList<MechanicHookDefinition>? hooks)
    {
        if (hooks == null) return null;
        foreach (var h in hooks)
        {
            string? found = FindSyncAttrInSteps(h.Steps, 0);
            if (!string.IsNullOrEmpty(found)) return found;
        }
        return null;
    }

    private static string? FindSyncAttrInSteps(IReadOnlyList<MechanicStep>? steps, int depth)
    {
        if (steps == null || depth > 8) return null;
        foreach (var s in steps)
        {
            if (s == null) continue;
            if (string.Equals(s.Op, "ModifyCounter", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.SyncAttribute)
                && MechanicDefinition.AllowedSyncAttributes.Contains(s.SyncAttribute))
                return s.SyncAttribute.Trim();
            string? sub = FindSyncAttrInSteps(s.Then, depth + 1)
                       ?? FindSyncAttrInSteps(s.Else, depth + 1)
                       ?? FindSyncAttrInSteps(s.Steps, depth + 1);
            if (!string.IsNullOrEmpty(sub)) return sub;
        }
        return null;
    }
}
