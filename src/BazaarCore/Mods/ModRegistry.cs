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
/// BazaarModKit 运行时注册表与热重载管理器：
/// - 从 mods/ 目录或内存 ModPackage 集合扫描、拓扑校验并增量叠加至只读 <see cref="GameDataRepository"/> 不可变副本；
/// - 原子切换与安全回滚（绝不破坏原版 BaseRepository）；
/// - 合并四层中文词典（自动汇总 Mod 卡牌中文元数据 + localization/zh-CN.json）；
/// - 同步自定义动作/触发器/模式钩子至 <see cref="ModRuntimeHooks"/>；
/// - 导出供客户端增量同步的 <see cref="ModManifestDto"/>、资产 SHA-256 索引与实例级视觉绑定查询接口。
/// </summary>
public sealed class ModRegistry : IModContentRegistry
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class RegistrySnapshot
    {
        public required GameDataRepository BaseRepository { get; init; }
        public required GameDataRepository ActiveRepository { get; init; }
        public required OverlayLocalizationStore ActiveLocalization { get; init; }
        public required IReadOnlyList<ModPackage> LoadedPackages { get; init; }
        public required ModLoadReport Report { get; init; }
        public required FrozenDictionary<string, CustomModeDefinition> CustomModesById { get; init; }
        public required FrozenDictionary<string, MechanicDefinition> MechanicsById { get; init; }
        public required FrozenDictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId { get; init; }
        public required FrozenDictionary<string, ModAssetEntry> AssetsBySha256 { get; init; }
        public required FrozenDictionary<string, ModAssetEntry> AssetsByModAndPath { get; init; }
        public required string VisualCatalogJson { get; init; }
        public required string VisualCatalogHash { get; init; }
        public required ModManifestDto ManifestDto { get; init; }
    }

    private readonly object _syncLock = new();
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
        IReadOnlySet<string>? validVisualWhitelist = null)
    {
        ArgumentNullException.ThrowIfNull(baseRepository);
        _validVisualWhitelist = validVisualWhitelist;
        _snapshot = BuildEmptySnapshot(baseRepository, string.Empty);
    }

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

    /// <summary>将本注册表内登记的全部自定义动作/触发器/模式钩子/管线拦截器同步发布到全局 <see cref="ModRuntimeHooks"/>。</summary>
    public void ApplyRuntimeHooks()
    {
        lock (_syncLock)
        {
            ModRuntimeHooks.SetHandlers(_actionHandlers, _triggerHandlers, _modeHooks, _pipelineInterceptors);
            ModRuntimeHooks.SetMechanics(_snapshot.MechanicsById.Values);
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
            ModLoader.BuildVisualBindings(candidatePackages[i]);
            if (string.IsNullOrEmpty(candidatePackages[i].PackageHash))
                candidatePackages[i].PackageHash = ModLoader.ComputePackageHash(candidatePackages[i]);
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
    /// 原子回滚到纯净基础数据仓库（移除所有 Mod 卡牌/野怪/模式/词典覆盖与全局自定义规则钩子）。
    /// </summary>
    public void RollbackToBase()
    {
        lock (_syncLock)
        {
            _snapshot = BuildEmptySnapshot(_snapshot.BaseRepository, _lastModsRoot ?? string.Empty);
            ModRuntimeHooks.ClearAll();
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

        // 汇总所有通过校验的 Mod 卡牌、原位补丁、野怪、模式、本地化与视觉绑定
        var mergedCardsById = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        var mergedMonsters = new List<MonsterTemplate>();
        var modLocDocs = new List<LocalizationOverlayDocument>();
        var modesMap = new Dictionary<string, CustomModeDefinition>(StringComparer.OrdinalIgnoreCase);
        var mechanicsMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
        var visualBindingsMap = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase);
        var assetsBySha = new Dictionary<string, ModAssetEntry>(StringComparer.OrdinalIgnoreCase);
        var assetsByModPath = new Dictionary<string, ModAssetEntry>(StringComparer.OrdinalIgnoreCase);

        bool forcesIsolation = false;
        int totalEvents = 0;
        int totalPatches = 0;

        foreach (var pkg in validOrdered)
        {
            string modId = pkg.Manifest.Id;
            report.LoadedModIdsInOrder.Add(modId);

            if (pkg.Manifest.ForcesGhostPoolIsolation || pkg.Cards.Count > 0 || pkg.Patches.Count > 0 || pkg.Events.Count > 0 || pkg.Monsters.Count > 0)
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

            // 自定义模式
            foreach (var mode in pkg.Modes)
            {
                modesMap[mode.ModeId] = mode;
                if (!mode.AffectsGhostPool)
                    forcesIsolation = true;
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

            // 视觉绑定与资产索引（原位 Patch 若有视觉覆盖也一并补齐 CardType/Size）
            foreach (var (cid, binding) in pkg.VisualBindingsByCustomId)
            {
                var resolved = binding;
                if (baseRepo.GetCard(cid) is { } vanillaCard)
                {
                    resolved = new CustomCardVisualBinding
                    {
                        CustomTemplateId = binding.CustomTemplateId,
                        ModId = binding.ModId,
                        BaseVisualTemplateId = vanillaCard.Id,
                        CardType = vanillaCard.Type,
                        Size = vanillaCard.Size,
                        TitleZh = binding.TitleZh,
                        DescZh = binding.DescZh,
                        ActiveTooltipsZh = binding.ActiveTooltipsZh,
                        PassiveTooltipsZh = binding.PassiveTooltipsZh,
                        EnchantTooltipsZh = binding.EnchantTooltipsZh,
                        CustomArtFile = binding.CustomArtFile,
                        CustomArtSha256 = binding.CustomArtSha256,
                        ArtRect = binding.ArtRect,
                        Tags = binding.Tags,
                        AccentColor = binding.AccentColor
                    };
                }
                visualBindingsMap[cid] = resolved;
            }

            foreach (var (logicalPath, asset) in pkg.AssetsByLogicalPath)
            {
                if (!string.IsNullOrEmpty(asset.Sha256))
                    assetsBySha[asset.Sha256] = asset;
                assetsByModPath[$"{modId}:{logicalPath}"] = asset;
            }
        }

        // 叠加通过 ICustomCardProvider 注册的扩展内容
        if (_cardProviders.Count > 0)
        {
            forcesIsolation = true;
            foreach (var provider in _cardProviders)
            {
                foreach (var c in provider.GetCustomCards(baseRepo))
                    if (c != null && !string.IsNullOrWhiteSpace(c.Id)) mergedCardsById[c.Id] = c;
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

        _snapshot = new RegistrySnapshot
        {
            BaseRepository = baseRepo,
            ActiveRepository = activeRepo,
            ActiveLocalization = activeLoc,
            LoadedPackages = validOrdered.AsReadOnly(),
            Report = report,
            CustomModesById = modesMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            MechanicsById = mechanicsMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            VisualBindingsByCustomId = visualBindingsMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            AssetsBySha256 = assetsBySha.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            AssetsByModAndPath = assetsByModPath.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            VisualCatalogJson = visualJson,
            VisualCatalogHash = visualHash,
            ManifestDto = manifestDto
        };

        ModRuntimeHooks.SetHandlers(_actionHandlers, _triggerHandlers, _modeHooks, _pipelineInterceptors);
        ModRuntimeHooks.SetMechanics(mechanicsMap.Values);
        return report;
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
            MechanicsById = FrozenDictionary<string, MechanicDefinition>.Empty,
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
}
