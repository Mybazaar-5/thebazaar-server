using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;

namespace BazaarServer.Mods;

/// <summary>
/// Web 创意工坊工作区完整模型（供 GET/POST /api/workshop/mods 与内存沙盒校验统一使用）。
/// </summary>
public sealed class WorkshopModWorkspace
{
    [JsonPropertyName("manifest")]
    public ModManifest Manifest { get; set; } = new();

    [JsonPropertyName("cards")]
    public List<CustomCardDefinition> Cards { get; set; } = new();

    [JsonPropertyName("patches")]
    public List<CardPatchDefinition> Patches { get; set; } = new();

    [JsonPropertyName("events")]
    public List<CustomEventDefinition> Events { get; set; } = new();

    [JsonPropertyName("monsters")]
    public List<MonsterTemplate> Monsters { get; set; } = new();

    [JsonPropertyName("modes")]
    public List<CustomModeDefinition> Modes { get; set; } = new();

    [JsonPropertyName("mechanics")]
    public List<MechanicDefinition> Mechanics { get; set; } = new();

    [JsonPropertyName("mutators")]
    public List<JsonElement> Mutators { get; set; } = new();

    [JsonPropertyName("localization")]
    public LocalizationOverlayDocument? Localization { get; set; }
}

/// <summary>
/// V13.1 Web 创意工坊核心业务与沙盒体检服务：
/// - 预建 3,442 原版卡 / 3,368 白名单卡高速中文搜索索引与克隆/原位魔改蓝图导出；
/// - 原子读写 mods/&lt;mod-id&gt;/ 草稿、标准化 PNG 资产校验落盘与 art_manifest.json 同步；
/// - 基于并发限流信号量执行完整 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 隔离压测；
/// - 提供安全 `.bazaarmod.zip` 流式导出与防 Zip Slip / Zip Bomb / 可执行文件检疫导入。
/// </summary>
public sealed class WorkshopValidatorService
{
    public const long MaxRawUploadBytes = 8L * 1024 * 1024; // 8 MB 原始上传限额
    public const long MaxZipBytes = 25L * 1024 * 1024;      // 25 MB 压缩包上限
    public const long MaxZipUncompressedBytes = 64L * 1024 * 1024; // 64 MB 解压总上限
    public const int MaxZipEntries = 200;

    private static readonly HashSet<string> ForbiddenZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".js", ".mjs", ".cjs", ".cs", ".so", ".dylib", ".msi", ".com", ".scr"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record BaseCardIndexEntry(
        string TemplateId,
        string TitleZh,
        string TitleEn,
        string Type,
        string Size,
        string StartingTier,
        List<string> Heroes,
        List<string> Tags,
        List<string> HiddenTags,
        bool IsWhitelisted,
        Dictionary<string, double> SummaryAttrs,
        List<string> TooltipsZh,
        string SearchHaystack);

    private sealed record CachedDiskValidationEntry(
        string ContentFingerprint,
        string ModSetHash,
        int Matches,
        int Seed,
        ModTenGateValidationReport Report,
        DateTimeOffset CachedAtUtc);

    private readonly ServerModHost _modHost;
    private readonly SemaphoreSlim _validationThrottle = new(2, 2);
    // 按 modId 分片的磁盘目录写/切/读互斥锁；锁序 DAG 硬承诺：所有 _modHost.*Reload() 必须在释放本锁后执行
    private readonly ConcurrentDictionary<string, object> _modDirLocks = new(StringComparer.OrdinalIgnoreCase);
    // P1-18：按 Mod 源码目录内容 SHA-256 + modSetHash 复用磁盘校验报告，避免每次 export 重复硬跑 20 场战斗模拟
    private readonly ConcurrentDictionary<string, CachedDiskValidationEntry> _diskValidationCache = new(StringComparer.OrdinalIgnoreCase);
    private long _exportValidationCacheHits;
    private long _exportValidationCacheMisses;
    private readonly Lazy<List<BaseCardIndexEntry>> _baseCardIndex;
    private readonly Lazy<List<string>> _knownVfxKeys;
    private readonly Lazy<string> _cachedRuleCatalogJson;
    private readonly Lazy<string> _cachedRuleCatalogEtag;

    /// <summary>P1-18：导出校验缓存命中次数（供诊断与自动化测试核验）。</summary>
    public long ExportValidationCacheHits => Interlocked.Read(ref _exportValidationCacheHits);

    /// <summary>P1-18：导出校验缓存未命中并执行实测校验的次数（供诊断与自动化测试核验）。</summary>
    public long ExportValidationCacheMisses => Interlocked.Read(ref _exportValidationCacheMisses);

    /// <summary>P1-18：主动失效指定 Mod（或全部）的磁盘校验缓存。</summary>
    public void InvalidateValidationCache(string? modId = null)
    {
        if (string.IsNullOrWhiteSpace(modId))
            _diskValidationCache.Clear();
        else
            _diskValidationCache.TryRemove(modId.Trim(), out _);
    }

    private object GetModDirLock(string modId) => _modDirLocks.GetOrAdd(modId.Trim(), static _ => new object());

    public WorkshopValidatorService(ServerModHost modHost)
    {
        _modHost = modHost;
        _baseCardIndex = new Lazy<List<BaseCardIndexEntry>>(BuildBaseCardIndex);
        _knownVfxKeys = new Lazy<List<string>>(BuildKnownVfxKeys);
        _cachedRuleCatalogJson = new Lazy<string>(() =>
        {
            var baseDto = ModRuleCatalog.BuildCatalogDto();
            // 叠加全库 GameData.db 提取出的全部 133 种官方白名单 VFX 键（含 Abilities/Auras/Enchantments），供工坊积木与自定义机制下拉选取
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(baseDto, JsonOpts));
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
                merged[prop.Name] = prop.Value.Clone();
            merged["vfxKeys"] = _knownVfxKeys.Value;
            return JsonSerializer.Serialize(merged, JsonOpts);
        });
        _cachedRuleCatalogEtag = new Lazy<string>(() =>
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_cachedRuleCatalogJson.Value));
            return $"\"{Convert.ToHexString(hash).ToLowerInvariant()[..16]}\"";
        });
    }

    public string RuleCatalogJson => _cachedRuleCatalogJson.Value;
    public string RuleCatalogEtag => _cachedRuleCatalogEtag.Value;
    public IReadOnlyList<string> KnownVfxKeys => _knownVfxKeys.Value;

    private List<string> BuildKnownVfxKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _modHost.BaseRepository.CardsById.Values)
        {
            foreach (var ab in card.Abilities.Values)
                if (!string.IsNullOrWhiteSpace(ab.VFXConfig?.VFXOverrideKey))
                    set.Add(ab.VFXConfig!.VFXOverrideKey!.Trim());
            foreach (var aura in card.Auras.Values)
                if (!string.IsNullOrWhiteSpace(aura.VFXConfig?.VFXOverrideKey))
                    set.Add(aura.VFXConfig!.VFXOverrideKey!.Trim());
            if (card.Enchantments != null)
            {
                foreach (var ed in card.Enchantments.Values)
                {
                    foreach (var ab in ed.Abilities.Values)
                        if (!string.IsNullOrWhiteSpace(ab.VFXConfig?.VFXOverrideKey))
                            set.Add(ab.VFXConfig!.VFXOverrideKey!.Trim());
                    foreach (var aura in ed.Auras.Values)
                        if (!string.IsNullOrWhiteSpace(aura.VFXConfig?.VFXOverrideKey))
                            set.Add(aura.VFXConfig!.VFXOverrideKey!.Trim());
                }
            }
        }
        return set.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>列出服务端当前所有已加载及已发现 Mod 注册的开放自定义沙盒机制（供工坊动态生成参数表单）。</summary>
    public object ListAllRegisteredMechanics()
    {
        var items = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in _modHost.Registry.LoadedPackages)
        {
            foreach (var m in pkg.Mechanics)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || !seen.Add(m.Id.Trim())) continue;
                items.Add(new { modId = pkg.Manifest.Id, loaded = true, mechanic = m });
            }
        }
        foreach (var pkg in _modHost.DiscoveredPackages)
        {
            foreach (var m in pkg.Mechanics)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || !seen.Add(m.Id.Trim())) continue;
                items.Add(new { modId = pkg.Manifest.Id, loaded = false, mechanic = m });
            }
        }

        return new
        {
            ok = true,
            count = items.Count,
            mechanics = items,
            schema = ModRuleCatalog.BuildMechanicSchemaDto()
        };
    }

    /// <summary>针对单条草稿自定义机制执行独立隔离对抗压测（验证静态合规 + 20 场极限对抗战斗 0 异常/0 熔断/0 超预算）。</summary>
    public async Task<object> StressTestMechanicAsync(
        MechanicDefinition mechanic,
        Dictionary<string, JsonElement>? sampleParams = null,
        int matches = 20,
        CancellationToken ct = default)
    {
        if (mechanic == null)
            return new { ok = false, passed = false, error = "请求体缺少 mechanic 定义。" };

        var staticIssues = new List<ModValidationIssue>();
        MechanicSandboxInterpreter.ValidateStatic(mechanic, "mechanic-lab", staticIssues);
        // 统一归一化静态规则审计闸门编号为 Gate #4（避免旧版 11 等离散编号破坏前端定位）
        staticIssues = staticIssues.Select(i => i.GateId == 4 ? i : i with { GateId = 4 }).ToList();
        if (staticIssues.Any(i => i.Severity == ModValidationSeverity.Error))
        {
            return new
            {
                ok = true,
                passed = false,
                stage = "static",
                summaryZh = $"静态沙盒审计未通过：发现 {staticIssues.Count(i => i.Severity == ModValidationSeverity.Error)} 处结构错误，请按提示修复后再压测。",
                staticIssues
            };
        }

        bool acquired = await _validationThrottle.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!acquired)
            return new { ok = false, passed = false, error = "校验沙盒繁忙，请稍候 3 秒再试。" };

        try
        {
            return await Task.Run<object>(() =>
            {
                var baseRepo = _modHost.BaseRepository;
                var carrier = baseRepo.AlwaysSpawnableItems.FirstOrDefault(c => c.Type == "Item" && c.Size == "Medium")
                    ?? baseRepo.AllItems.First();

                var boundParams = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in mechanic.Parameters)
                    if (!string.IsNullOrWhiteSpace(p.Key) && p.DefaultValue.HasValue)
                        boundParams[p.Key] = p.DefaultValue.Value.Clone();
                if (sampleParams != null)
                    foreach (var (k, v) in sampleParams)
                        boundParams[k] = v.Clone();

                var testCardDef = new CustomCardDefinition
                {
                    CustomId = $"mechanic-lab:test_{MakeSlug(mechanic.Id)}",
                    BaseVisualTemplateId = carrier.Id,
                    Type = "Item",
                    Size = carrier.Size,
                    StartingTier = "Bronze",
                    Heroes = [HeroNames.Common],
                    Tags = ["Weapon", "Tool"],
                    HiddenTags = ["Damage"],
                    TitleZh = $"沙盒测试卡（{mechanic.NameZh}）",
                    DescZh = mechanic.DescZh,
                    ActiveTooltipsZh = [mechanic.RenderTooltip(new CardMechanicAttachment { MechanicId = mechanic.Id, Params = boundParams })],
                    Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 3000, ["DamageAmount"] = 20, ["Custom_0"] = 3 }, AbilityIds = ["0"] },
                        ["Silver"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 3000, ["DamageAmount"] = 40, ["Custom_0"] = 3 }, AbilityIds = ["0"] },
                        ["Gold"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 2500, ["DamageAmount"] = 80, ["Custom_0"] = 4 }, AbilityIds = ["0"] },
                        ["Diamond"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 32, ["SellPrice"] = 16, ["CooldownMax"] = 2500, ["DamageAmount"] = 140, ["Custom_0"] = 5 }, AbilityIds = ["0"] }
                    },
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["0"] = new AbilityDefinition
                        {
                            Id = "0",
                            ActiveIn = "HandOnly",
                            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                            Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                        }
                    },
                    CustomMechanics = [new CardMechanicAttachment { MechanicId = mechanic.Id, Params = boundParams, Enabled = true }]
                };

                var tmpPkg = new ModPackage
                {
                    Manifest = new ModManifest { Id = "mechanic-lab", Name = "机制工坊沙盒测试", Version = "1.0.0" },
                    RootDirectory = Path.Combine(_modHost.ModsRootDirectory, "_mechanic_lab")
                };
                tmpPkg.Cards.Add(testCardDef);
                tmpPkg.Mechanics.Add(mechanic);

                var tpl = testCardDef.ToCardTemplate(carrier, "mechanic-lab");
                var overlayRepo = baseRepo.CreateWithOverlay([tpl], [], null);
                var candMechs = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    [mechanic.Id.Trim()] = mechanic
                };
                var (stressReport, simIssues) = ModCombatStressTester.RunStressTest(tmpPkg, overlayRepo, Math.Clamp(matches, 5, 50), 2026140, candMechs);
                bool passed = !simIssues.Any(i => i.Severity == ModValidationSeverity.Error)
                    && stressReport.MechanicCircuitBreakerTrips == 0
                    && stressReport.MechanicBudgetViolations == 0;

                return new
                {
                    ok = true,
                    passed,
                    stage = "adversarial-combat",
                    summaryZh = passed
                        ? $"安全通过！已完成 {stressReport.MatchesRun} 场对抗压测（含高速伤害/DOT/摧毁/修复对手），0 异常、0 熔断、0 超预算，平均单场 {stressReport.AvgMatchMs:0.0}ms。"
                        : $"对抗压测拦截：检测到 {stressReport.Exceptions} 异常、{stressReport.MechanicCircuitBreakerTrips} 次熔断、{stressReport.MechanicBudgetViolations} 次步数超预算，请修复后重试。",
                    staticIssues,
                    simIssues,
                    combatStressReport = stressReport
                };
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _validationThrottle.Release();
        }
    }

    // ── 1. 原版 3,368 白名单卡高速检索与单卡蓝图导出 ──────────────────────────

    public object SearchBaseCards(
        string? query,
        string? type,
        string? hero,
        string? size,
        string? tier,
        bool whitelistOnly = true,
        int limit = 40,
        int offset = 0)
    {
        int safeLimit = Math.Clamp(limit, 1, 200);
        int safeOffset = Math.Max(0, offset);
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        var index = _baseCardIndex.Value;

        var filtered = new List<BaseCardIndexEntry>(Math.Min(256, index.Count));
        for (int i = 0; i < index.Count; i++)
        {
            var e = index[i];
            if (whitelistOnly && !e.IsWhitelisted) continue;
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(e.Type, type.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(size) && !string.Equals(e.Size, size.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(tier) && !string.Equals(e.StartingTier, tier.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(hero) && !e.Heroes.Any(h => string.Equals(h, hero.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            if (q.Length > 0 && !e.SearchHaystack.Contains(q, StringComparison.Ordinal)) continue;
            filtered.Add(e);
        }

        var page = filtered
            .Skip(safeOffset)
            .Take(safeLimit)
            .Select(e => new
            {
                templateId = e.TemplateId,
                titleZh = e.TitleZh,
                titleEn = e.TitleEn,
                type = e.Type,
                size = e.Size,
                startingTier = e.StartingTier,
                heroes = e.Heroes,
                tags = e.Tags,
                hiddenTags = e.HiddenTags,
                isWhitelisted = e.IsWhitelisted,
                summaryAttrs = e.SummaryAttrs,
                tooltipsZh = e.TooltipsZh
            })
            .ToList();

        return new
        {
            ok = true,
            total = filtered.Count,
            offset = safeOffset,
            limit = safeLimit,
            items = page
        };
    }

    public object? ExportBaseCardBlueprint(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return null;
        var baseRepo = _modHost.BaseRepository;
        var card = baseRepo.GetCard(templateId.Trim());
        if (card == null) return null;

        var loc = baseRepo.Localization;
        string titleZh = loc?.GetLocalizedTitle(card) ?? card.DisplayTitle;
        string descZh = loc?.GetLocalizedText(card.Localization?.Description) ?? card.Localization?.Description?.Text ?? card.InternalDescription ?? string.Empty;
        var activeTips = new List<string>();
        var passiveTips = new List<string>();
        if (card.Localization?.Tooltips != null)
        {
            foreach (var tip in card.Localization.Tooltips)
            {
                string txt = loc?.GetLocalizedText(tip.Content) ?? tip.Content?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(txt)) continue;
                if (string.Equals(tip.TooltipType, "Passive", StringComparison.OrdinalIgnoreCase))
                    passiveTips.Add(txt);
                else
                    activeTips.Add(txt);
            }
        }

        // 预生成标准克隆草稿（自动绑定合法 baseVisualTemplateId 与标准化 tiers.*.abilityIds）
        string slug = MakeSlug(card.InternalName);
        var cloneDraft = new CustomCardDefinition
        {
            CustomId = $"my-mod:{slug}_custom",
            BaseVisualTemplateId = card.Id,
            Type = card.Type,
            Size = card.Size,
            StartingTier = card.StartingTier,
            SpawningEligibility = string.IsNullOrWhiteSpace(card.SpawningEligibility) ? "Always" : card.SpawningEligibility!,
            Heroes = card.Heroes.Count > 0 ? new List<string>(card.Heroes) : [HeroNames.Common],
            Tags = new List<string>(card.Tags),
            HiddenTags = new List<string>(card.HiddenTags),
            TitleZh = $"{titleZh}（改）",
            TitleEn = $"{card.InternalName} Custom",
            DescZh = string.IsNullOrWhiteSpace(descZh) ? $"基于原版「{titleZh}」打造的自定义衍生卡。" : descZh,
            ActiveTooltipsZh = activeTips,
            PassiveTooltipsZh = passiveTips,
            Attributes = card.Attributes != null ? new Dictionary<string, double>(card.Attributes, StringComparer.OrdinalIgnoreCase) : null,
            Tiers = card.Tiers != null
                ? card.Tiers.ToDictionary(
                    kv => kv.Key,
                    kv => new TierData
                    {
                        Attributes = new Dictionary<string, double>(kv.Value.Attributes, StringComparer.OrdinalIgnoreCase),
                        AbilityIds = new List<string>(kv.Value.AbilityIds),
                        AuraIds = new List<string>(kv.Value.AuraIds),
                        TooltipIds = new List<int>(kv.Value.TooltipIds)
                    },
                    StringComparer.OrdinalIgnoreCase)
                : null,
            Enchantments = card.Enchantments,
            Abilities = new Dictionary<string, AbilityDefinition>(card.Abilities),
            Auras = new Dictionary<string, AuraDefinition>(card.Auras),
            Quests = card.Quests
        };
        cloneDraft.NormalizeTierBindings();

        // 预生成原位魔改补丁草稿
        var tierOverrides = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        if (card.Tiers != null)
        {
            foreach (var (tk, td) in card.Tiers)
            {
                var subset = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (ak, av) in td.Attributes)
                    subset[ak] = av;
                tierOverrides[tk] = subset;
            }
        }
        var patchDraft = new CardPatchDefinition
        {
            TargetTemplateId = card.Id,
            Note = $"原位平衡调整原版卡：{titleZh} ({card.InternalName})",
            StartingTier = card.StartingTier,
            TierAttributeOverrides = tierOverrides
        };

        return new
        {
            ok = true,
            templateId = card.Id,
            isWhitelisted = _modHost.ValidVisualWhitelist.Contains(card.Id),
            titleZh,
            titleEn = card.InternalName,
            descZh,
            card,
            cloneDraftTemplate = cloneDraft,
            patchDraftTemplate = patchDraft
        };
    }

    // ── 2. 读取与原子保存 Mod 工作区 ──────────────────────────────────────────

    public (bool Found, object? Payload) LoadModWorkspace(string modId)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, null);

        string modDir = ResolveSafeModDir(modId);
        if (!Directory.Exists(modDir) || !File.Exists(Path.Combine(modDir, "mod.json")))
            return (false, null);

        var scanIssues = new List<ModValidationIssue>();
        var pkg = ModLoader.LoadSinglePackage(modDir, modId, scanIssues);
        if (pkg == null)
            return (false, null);

        // 读取 modes/custom_mutators.json（如果存在）
        var mutators = new List<JsonElement>();
        string mutPath = Path.Combine(modDir, "modes", "custom_mutators.json");
        if (File.Exists(mutPath))
        {
            try
            {
                using var mdoc = JsonDocument.Parse(File.ReadAllText(mutPath, Encoding.UTF8));
                if (mdoc.RootElement.TryGetProperty("mutators", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                        mutators.Add(el.Clone());
                }
            }
            catch { }
        }

        var assets = pkg.AssetsByLogicalPath.Values
            .OrderBy(a => a.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .Select(a => new
            {
                logicalPath = a.LogicalPath,
                sha256 = a.Sha256,
                bytes = a.Bytes,
                width = a.Width,
                height = a.Height,
                contentType = a.ContentType,
                previewUrl = $"/api/workshop/mods/{pkg.Manifest.Id}/assets/{a.LogicalPath}"
            })
            .ToList();

        bool enabled = _modHost.IsModEnabled(pkg.Manifest.Id);
        bool loaded = _modHost.Registry.LoadedPackages.Any(p => string.Equals(p.Manifest.Id, pkg.Manifest.Id, StringComparison.OrdinalIgnoreCase));

        return (true, new
        {
            ok = true,
            modId = pkg.Manifest.Id,
            enabled,
            loaded,
            packageHash = pkg.PackageHash,
            scanIssues,
            workspace = new WorkshopModWorkspace
            {
                Manifest = pkg.Manifest,
                Cards = pkg.Cards,
                Patches = pkg.Patches,
                Events = pkg.Events,
                Monsters = pkg.Monsters,
                Modes = pkg.Modes,
                Mechanics = pkg.Mechanics,
                Mutators = mutators,
                Localization = pkg.LocalizationOverlay
            },
            assets
        });
    }

    public (bool Ok, string? Error, string? ModId, string? PackageHash) SaveWorkspaceAtomic(WorkshopModWorkspace workspace)
    {
        if (workspace?.Manifest == null)
            return (false, "请求体缺少 manifest 字段。", null, null);

        string modId = (workspace.Manifest.Id ?? string.Empty).Trim();
        if (!ModValidator.IsValidModId(modId))
            return (false, $"Mod ID '{modId}' 非法，必须为 2~64 位小写字母、数字或连字符。", null, null);

        workspace.Manifest.Id = modId;
        workspace.Manifest.SchemaVersion = 1;
        workspace.Manifest.ContentRoots ??= new ModContentRoots();
        if (string.IsNullOrWhiteSpace(workspace.Manifest.Scope))
            workspace.Manifest.Scope = "multiplayer-safe";
        if (string.IsNullOrWhiteSpace(workspace.Manifest.Version))
            workspace.Manifest.Version = "1.0.0";

        // 标准化全部自定义卡的 tiers.*.abilityIds / auraIds，彻底消除 0 触发隐患
        foreach (var card in workspace.Cards)
            card.NormalizeTierBindings();

        string modsRoot = _modHost.ModsRootDirectory;
        Directory.CreateDirectory(modsRoot);
        string targetDir = ResolveSafeModDir(modId);

        lock (GetModDirLock(modId))
        {
            bool isNewMod = !Directory.Exists(targetDir) || !File.Exists(Path.Combine(targetDir, "mod.json"));
            if (isNewMod)
                workspace.Manifest.EnabledByDefault = false;

            string stageDir = Path.Combine(modsRoot, $".tmp_{modId}_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stageDir);

                // 保留已有贴图资产与非受控附加文件（如 README.md、docs/），避免保存草稿丢失资产与文档
                string existingAssets = Path.Combine(targetDir, "assets");
                string stagedAssets = Path.Combine(stageDir, "assets");
                Directory.CreateDirectory(stagedAssets);
                if (Directory.Exists(existingAssets))
                {
                    foreach (var f in Directory.GetFiles(existingAssets, "*.*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(existingAssets, f);
                        string dest = Path.Combine(stagedAssets, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(f, dest, overwrite: true);
                    }
                }

                var managedTopDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "cards", "patches", "events", "monsters", "modes", "mechanics", "localization", "assets"
                };
                if (Directory.Exists(targetDir))
                {
                    foreach (var f in Directory.GetFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(f);
                        if (string.Equals(name, "mod.json", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.'))
                            continue;
                        File.Copy(f, Path.Combine(stageDir, name), overwrite: true);
                    }
                    foreach (var d in Directory.GetDirectories(targetDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(d);
                        if (managedTopDirs.Contains(name) || name.StartsWith('.'))
                            continue;
                        CopyDirectoryRecursive(d, Path.Combine(stageDir, name));
                    }
                }

                // 仅当自定义 contentRoots 不安全或无法覆盖本次写出文件时才回退默认值，绝不硬覆写作者合法配置
                var writtenCards = workspace.Cards.Count > 0 ? new[] { "cards/custom_cards.json" } : Array.Empty<string>();
                var writtenPatches = workspace.Patches.Count > 0 ? new[] { "patches/vanilla_patches.json" } : Array.Empty<string>();
                var writtenEvents = workspace.Events.Count > 0 ? new[] { "events/custom_events.json" } : Array.Empty<string>();
                var writtenModes = new List<string>();
                if (workspace.Modes.Count > 0) writtenModes.Add("modes/custom_modes.json");
                if (workspace.Mutators.Count > 0) writtenModes.Add("modes/custom_mutators.json");
                var writtenMechanics = workspace.Mechanics.Count > 0 ? new[] { "mechanics/custom_mechanics.json" } : Array.Empty<string>();
                var writtenLoc = workspace.Localization != null ? new[] { "localization/zh-CN.json" } : Array.Empty<string>();

                var stagedAssetRelPaths = Directory.Exists(stagedAssets)
                    ? Directory.GetFiles(stagedAssets, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsSupportedAssetExtension(Path.GetExtension(f)))
                        .Select(f => ModValidator.NormalizeLogicalPath(Path.GetRelativePath(stageDir, f)))
                        .ToList()
                    : new List<string>();

                var cr = workspace.Manifest.ContentRoots;
                cr.Cards = EnsureSafeMatchingContentRoot(cr.Cards, writtenCards, "cards/custom_cards.json");
                cr.Patches = EnsureSafeMatchingContentRoot(cr.Patches, writtenPatches, "patches/vanilla_patches.json");
                cr.Events = EnsureSafeMatchingContentRoot(cr.Events, writtenEvents, "events/custom_events.json");
                cr.Modes = EnsureSafeMatchingContentRoot(cr.Modes, writtenModes, "modes/*.json");
                cr.Mechanics = EnsureSafeMatchingContentRoot(cr.Mechanics, writtenMechanics, "mechanics/custom_mechanics.json");
                cr.Localization = EnsureSafeMatchingContentRoot(cr.Localization, writtenLoc, "localization/zh-CN.json");
                cr.Assets = EnsureSafeMatchingContentRoot(cr.Assets, stagedAssetRelPaths, "assets/*");
                cr.ArtManifest = EnsureSafeMatchingContentRoot(cr.ArtManifest, ["assets/art_manifest.json"], "assets/art_manifest.json");

                WriteJson(Path.Combine(stageDir, "mod.json"), workspace.Manifest);

                if (workspace.Cards.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "cards", "custom_cards.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        cards = workspace.Cards
                    });
                }

                if (workspace.Patches.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "patches", "vanilla_patches.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        patches = workspace.Patches
                    });
                }

                if (workspace.Events.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "events", "custom_events.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        events = workspace.Events
                    });
                }

                if (workspace.Monsters.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "monsters", "custom_monsters.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        monsters = workspace.Monsters
                    });
                }

                if (workspace.Modes.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "modes", "custom_modes.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        modes = workspace.Modes
                    });
                }

                if (workspace.Mechanics.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "mechanics", "custom_mechanics.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        mechanics = workspace.Mechanics
                    });
                }

                if (workspace.Mutators.Count > 0)
                {
                    WriteJson(Path.Combine(stageDir, "modes", "custom_mutators.json"), new
                    {
                        schemaVersion = 1,
                        modId,
                        mutators = workspace.Mutators
                    });
                }

                if (workspace.Localization != null)
                {
                    WriteJson(Path.Combine(stageDir, "localization", "zh-CN.json"), workspace.Localization);
                }

                RebuildArtManifest(stageDir, modId);

                // 同卷隐藏目录原子切换事务（.tmp_ -> targetDir，旧目录临时移入 .bak_ 失败自动回滚）
                AtomicSwapDirectory(stageDir, targetDir, modsRoot, modId);
                InvalidateValidationCache(modId);

                var issues = new List<ModValidationIssue>();
                var loaded = ModLoader.LoadSinglePackage(targetDir, modId, issues);
                return (true, null, modId, loaded?.PackageHash ?? string.Empty);
            }
            finally
            {
                TryDeleteDirQuietly(stageDir);
            }
        }
    }

    // ── 3. PNG/JPEG 资产上传校验与 SHA-256 登记（方案 A：支持 PNG & JPEG，严拒 WebP）──

    public (bool Ok, string? Error, object? AssetInfo) SavePngAsset(string modId, string logicalFileName, byte[] rawBytes)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, $"非法 Mod ID：{modId}", null);
        if (rawBytes == null || rawBytes.Length == 0)
            return (false, "上传的图片内容为空。", null);
        if (rawBytes.Length > MaxRawUploadBytes)
            return (false, $"上传图片体积 {rawBytes.Length} 字节超出上限 8 MB。", null);

        if (!ModValidator.TryValidateImageHeader(rawBytes, out int width, out int height, out string contentType))
            return (false, "仅支持合法的标准 PNG 或 JPEG 图片（图片魔数头校验失败；如为 WebP 请先在网页工坊裁切器转换为 PNG）。", null);

        if (!string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "原版 Unity 客户端不支持 WebP 解码，请上传标准 PNG 或 JPEG 图片（网页工坊裁切器会自动转 PNG）。", null);
        }

        if (rawBytes.Length > ModValidator.MaxAssetBytes)
            return (false, $"图片体积 {rawBytes.Length} 字节超出单图上限 2 MB，请适当缩小分辨率或压缩。", null);

        if (width < ModValidator.MinAssetDimension || height < ModValidator.MinAssetDimension
            || width > ModValidator.MaxAssetWidth || height > ModValidator.MaxAssetHeight)
        {
            return (false, $"图片分辨率 {width}×{height} 超出安全范围 [{ModValidator.MinAssetDimension}, {ModValidator.MaxAssetWidth}]。", null);
        }

        string normalizedContentType = string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";
        string safeName = SanitizeImageFileName(logicalFileName, normalizedContentType);
        string modDir = ResolveSafeModDir(modId);
        string sha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        string logicalPath = $"assets/{safeName}";

        lock (GetModDirLock(modId))
        {
            string assetsDir = Path.Combine(modDir, "assets");
            Directory.CreateDirectory(assetsDir);

            string destPath = Path.Combine(assetsDir, safeName);
            string tmpPath = destPath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmpPath, rawBytes);
            File.Move(tmpPath, destPath, overwrite: true);

            RebuildArtManifest(modDir, modId);
            InvalidateValidationCache(modId);
        }

        return (true, null, new
        {
            ok = true,
            modId,
            logicalPath,
            sha256 = sha,
            width,
            height,
            bytes = rawBytes.LongLength,
            contentType = normalizedContentType,
            previewUrl = $"/api/workshop/mods/{modId}/assets/{logicalPath}"
        });
    }

    public bool TryResolveDraftAssetFile(string modId, string logicalPath, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "image/png";
        if (!ModValidator.IsValidModId(modId)) return false;
        string normalized = ModValidator.NormalizeLogicalPath(logicalPath);
        if (!SafePathGuard.IsSafeRelativePath(normalized)) return false;
        if (!normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return false;

        string ext = Path.GetExtension(normalized);
        if (!IsSupportedAssetExtension(ext))
            return false;

        string modDir = ResolveSafeModDir(modId);
        if (!SafePathGuard.TryResolveUnderRoot(modDir, normalized, out string candidate) || !File.Exists(candidate))
            return false;

        // 核验前 12 字节真实魔数，严防伪造扩展名（如文本/JSON/WebP 改名 .png/.jpg）绕过读取
        byte[] header = new byte[12];
        int read;
        try
        {
            using var fs = File.OpenRead(candidate);
            read = fs.Read(header, 0, header.Length);
        }
        catch
        {
            return false;
        }

        if (read < 3)
            return false;

        bool isPng = read >= 4
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && (read < 8 || (header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A));
        bool isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        if (!isPng && !isJpeg)
            return false;
        if (isPng && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return false;
        if (isJpeg && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        contentType = isPng ? "image/png" : "image/jpeg";
        return true;
    }

    // ── 4. 沙盒 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 压测 ─────────────

    public async Task<ModTenGateValidationReport> ValidateAsync(
        string modId,
        WorkshopModWorkspace? inMemoryDraft,
        int matches = 50,
        int seed = 2026131,
        CancellationToken ct = default)
    {
        bool acquired = await _validationThrottle.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!acquired)
        {
            return new ModTenGateValidationReport
            {
                Ok = false,
                Passed = false,
                ModId = modId,
                ErrorCount = 1,
                SummaryZh = "服务端校验沙盒繁忙（并发压测已达上限），请稍候 3 秒再试。"
            };
        }

        try
        {
            string? diskFingerprintBefore = null;
            string? modSetHashBefore = null;
            string? diskModDir = null;
            if (inMemoryDraft == null && ModValidator.IsValidModId(modId))
            {
                diskModDir = ResolveSafeModDir(modId);
                if (Directory.Exists(diskModDir))
                {
                    (diskFingerprintBefore, modSetHashBefore) = ComputeDiskValidationFingerprint(modId, diskModDir);
                }
            }

            var report = await Task.Run(() => RunValidationCore(modId, inMemoryDraft, matches, seed), ct).ConfigureAwait(false);

            // P1-18：磁盘校验完成后复核目录指纹未在校验期间漂移，再写入指纹缓存供后续 export 零模拟复用
            if (inMemoryDraft == null
                && report.Ok
                && diskFingerprintBefore != null
                && modSetHashBefore != null
                && diskModDir != null
                && Directory.Exists(diskModDir))
            {
                var (diskFingerprintAfter, modSetHashAfter) = ComputeDiskValidationFingerprint(modId, diskModDir);
                if (string.Equals(diskFingerprintBefore, diskFingerprintAfter, StringComparison.Ordinal)
                    && string.Equals(modSetHashBefore, modSetHashAfter, StringComparison.Ordinal))
                {
                    int effectiveMatches = Math.Clamp(matches, 1, 100);
                    _diskValidationCache[modId.Trim()] = new CachedDiskValidationEntry(
                        diskFingerprintAfter,
                        modSetHashAfter,
                        effectiveMatches,
                        seed,
                        report,
                        DateTimeOffset.UtcNow);
                }
            }

            return report;
        }
        finally
        {
            _validationThrottle.Release();
        }
    }

    private ModTenGateValidationReport RunValidationCore(
        string modId,
        WorkshopModWorkspace? draft,
        int matches,
        int seed)
    {
        ModPackage? targetPkg;
        var scanIssues = new List<ModValidationIssue>();

        if (draft != null)
        {
            string effectiveId = !string.IsNullOrWhiteSpace(draft.Manifest?.Id) ? draft.Manifest.Id.Trim() : modId.Trim();
            targetPkg = BuildInMemoryPackageFromWorkspace(draft, effectiveId, scanIssues);
        }
        else
        {
            if (!ModValidator.IsValidModId(modId))
            {
                return new ModTenGateValidationReport
                {
                    Ok = true,
                    Passed = false,
                    ModId = modId,
                    ErrorCount = 1,
                    SummaryZh = $"非法 Mod ID：{modId}"
                };
            }

            string modDir = ResolveSafeModDir(modId);
            targetPkg = ModLoader.LoadSinglePackage(modDir, modId, scanIssues);
            if (targetPkg == null)
            {
                return new ModTenGateValidationReport
                {
                    Ok = true,
                    Passed = false,
                    ModId = modId,
                    ErrorCount = Math.Max(1, scanIssues.Count(i => i.Severity == ModValidationSeverity.Error)),
                    SummaryZh = "读取 Mod 磁盘文件失败，请检查 mod.json 语法。",
                    Gates =
                    [
                        new ModGateResult
                        {
                            GateId = 1,
                            Key = "schema_structure",
                            NameZh = "闸门 1：Schema 结构、废字段与四阶绑定完整性",
                            Status = "FAIL",
                            Issues = scanIssues
                        }
                    ]
                };
            }
        }

        var report = ModValidator.RunTenGateValidation(
            targetPkg,
            _modHost.DiscoveredPackages,
            _modHost.BaseRepository,
            _modHost.ValidVisualWhitelist,
            combatMatches: Math.Clamp(matches, 1, 100),
            combatSeed: seed,
            runDynamicGates: true);

        if (scanIssues.Count > 0 && report.Gates.Count > 0)
        {
            report.Gates[0].Issues.InsertRange(0, scanIssues);
            if (scanIssues.Any(i => i.Severity == ModValidationSeverity.Error))
            {
                report.Gates[0].Status = "FAIL";
                report.Passed = false;
                report.ErrorCount = report.Gates.Sum(g => g.Issues.Count(i => i.Severity == ModValidationSeverity.Error));
            }
        }

        return report;
    }

    private ModPackage BuildInMemoryPackageFromWorkspace(
        WorkshopModWorkspace draft,
        string modId,
        List<ModValidationIssue> scanIssues)
    {
        string modDir = ModValidator.IsValidModId(modId) ? ResolveSafeModDir(modId) : Path.Combine(_modHost.ModsRootDirectory, "_memory");
        var pkg = new ModPackage
        {
            Manifest = draft.Manifest ?? new ModManifest { Id = modId },
            RootDirectory = modDir
        };
        pkg.Manifest.Id = modId;
        pkg.Cards.AddRange(draft.Cards ?? []);
        pkg.Patches.AddRange(draft.Patches ?? []);
        pkg.Events.AddRange(draft.Events ?? []);
        pkg.Monsters.AddRange(draft.Monsters ?? []);
        pkg.Modes.AddRange(draft.Modes ?? []);
        pkg.Mechanics.AddRange(draft.Mechanics ?? []);
        pkg.LocalizationOverlay = draft.Localization;

        // 将工作区 draft.Mutators 安全转换为 CustomMutators（捕获格式错误转为 Gate #1 Error，绝不 500）
        if (draft.Mutators is { Count: > 0 })
        {
            for (int i = 0; i < draft.Mutators.Count; i++)
            {
                var el = draft.Mutators[i];
                try
                {
                    var def = JsonSerializer.Deserialize<CustomMutatorDefinition>(el.GetRawText(), JsonOpts);
                    if (def == null || string.IsNullOrWhiteSpace(def.Id))
                    {
                        scanIssues.Add(new ModValidationIssue(
                            ModValidationSeverity.Error,
                            modId,
                            "MUTATOR_JSON_INVALID",
                            $"草稿突变词条 #{i + 1} 缺少必填 id 字段。",
                            TargetId: $"mutators[{i}]",
                            GateId: 1,
                            FixHintZh: "请为每条自定义突变词条填写非空唯一 id（如 my-mod:fast_storm）。"));
                        continue;
                    }
                    pkg.CustomMutators.Add(def.ToMetadata(modId));
                }
                catch (JsonException ex)
                {
                    scanIssues.Add(new ModValidationIssue(
                        ModValidationSeverity.Error,
                        modId,
                        "MUTATOR_JSON_INVALID",
                        $"草稿突变词条 #{i + 1} JSON 字段类型非法：{ex.Message}",
                        TargetId: $"mutators[{i}]",
                        GateId: 1,
                        FixHintZh: "请核对 mutator 字段类型（difficultyDelta/scoreMultiplierDelta 为数字，isPositive/enabledByDefault 为布尔值，rules 为字符串键值表）。"));
                }
            }
        }

        // 继承磁盘该 Mod 目录下已上传的资产，以便草稿态直接过 Gate #8
        if (Directory.Exists(modDir))
        {
            var tmpIssues = new List<ModValidationIssue>();
            var diskPkg = ModLoader.LoadSinglePackage(modDir, modId, tmpIssues);
            if (diskPkg != null)
            {
                foreach (var (k, v) in diskPkg.AssetsByLogicalPath) pkg.AssetsByLogicalPath[k] = v;
                foreach (var (k, v) in diskPkg.ExpectedAssetSha256ByPath) pkg.ExpectedAssetSha256ByPath[k] = v;
            }
        }

        ModLoader.BuildVisualBindings(pkg);
        pkg.PackageHash = ModLoader.ComputePackageHash(pkg);
        return pkg;
    }

    // ── 5. .bazaarmod.zip 导出与严苛检疫导入 ──────────────────────────────────

    /// <summary>向后兼容的 4 元组导出包装（默认阻断含 Error 病包，需显式 allowDraft 才允许导出草稿备份）。</summary>
    public async Task<(bool Ok, string? Error, byte[]? ZipBytes, string? FileName)> ExportZipAsync(string modId, CancellationToken ct = default)
    {
        var (ok, _, err, bytes, fileName, _) = await ExportZipWithReportAsync(modId, allowDraft: false, ct).ConfigureAwait(false);
        return (ok, err, bytes, fileName);
    }

    public async Task<(bool Ok, int StatusCode, string? Error, byte[]? ZipBytes, string? FileName, ModTenGateValidationReport? Validation)> ExportZipWithReportAsync(
        string modId,
        bool allowDraft = false,
        CancellationToken ct = default)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, 400, $"非法 Mod ID：{modId}", null, null, null);

        string modDir = ResolveSafeModDir(modId);
        if (!Directory.Exists(modDir) || !File.Exists(Path.Combine(modDir, "mod.json")))
            return (false, 404, $"未找到 Mod '{modId}'。", null, null, null);

        // P1-18：优先复用同源码目录 SHA-256 指纹与同 modSetHash 的最近一次校验报告，避免每次导出重复硬跑 20 场战斗模拟；
        // 任何文件内容或上下文 Mod 集合变更都会立即产生新指纹导致缓存失效。
        const int ExportMinMatches = 20;
        const int ExportDefaultSeed = 2026131;
        string cleanModId = modId.Trim();
        var (currentFingerprint, currentModSetHash) = ComputeDiskValidationFingerprint(cleanModId, modDir);
        ModTenGateValidationReport validation;
        if (_diskValidationCache.TryGetValue(cleanModId, out var cachedValidation)
            && string.Equals(cachedValidation.ContentFingerprint, currentFingerprint, StringComparison.Ordinal)
            && string.Equals(cachedValidation.ModSetHash, currentModSetHash, StringComparison.Ordinal)
            && cachedValidation.Matches >= ExportMinMatches
            && cachedValidation.Seed == ExportDefaultSeed)
        {
            Interlocked.Increment(ref _exportValidationCacheHits);
            validation = cachedValidation.Report;
        }
        else
        {
            Interlocked.Increment(ref _exportValidationCacheMisses);
            validation = await ValidateAsync(cleanModId, null, matches: ExportMinMatches, seed: ExportDefaultSeed, ct: ct).ConfigureAwait(false);
        }

        if (!validation.Passed && !allowDraft)
        {
            return (false, 422, $"导出已拦截：存在 {validation.ErrorCount} 个错误未修复（如需导出未完成草稿请显式传 ?allowDraft=true）。", null, null, validation);
        }

        string version = "1.0.0";
        byte[] zipBytes;
        lock (GetModDirLock(modId))
        {
            if (!Directory.Exists(modDir) || !File.Exists(Path.Combine(modDir, "mod.json")))
                return (false, 404, $"未找到 Mod '{modId}'。", null, null, validation);

            try
            {
                var m = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(Path.Combine(modDir, "mod.json"), Encoding.UTF8), JsonOpts);
                if (!string.IsNullOrWhiteSpace(m?.Version)) version = m!.Version.Trim();
            }
            catch { }

            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var files = Directory.GetFiles(modDir, "*.*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(modDir, f));
                    if (rel.StartsWith('.') || rel.Contains("/.") || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string ext = Path.GetExtension(f);
                    if (ForbiddenZipExtensions.Contains(ext))
                        continue;

                    var entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(f);
                    fs.CopyTo(es);
                }

                var stampEntry = archive.CreateEntry("validation_stamp.json", CompressionLevel.Optimal);
                using (var sw = new StreamWriter(stampEntry.Open(), new UTF8Encoding(false)))
                {
                    sw.Write(JsonSerializer.Serialize(new
                    {
                        modId,
                        version,
                        exportedAtUtc = DateTimeOffset.UtcNow,
                        validationPassed = validation.Passed,
                        isDraftExport = !validation.Passed,
                        errorCount = validation.ErrorCount,
                        warningCount = validation.WarningCount,
                        summaryZh = validation.SummaryZh
                    }, JsonOpts));
                }
            }
            zipBytes = ms.ToArray();
        }

        string safeVersion = SanitizeFileSegment(version, "1.0.0");
        return (true, 200, null, zipBytes, $"{modId}-{safeVersion}.bazaarmod.zip", validation);
    }

    public async Task<(bool Ok, int StatusCode, string? Error, string? ModId, ModTenGateValidationReport? Report)> ImportZipAsync(
        byte[] zipBytes,
        bool allowDraftWithWarnings,
        CancellationToken ct = default)
    {
        if (zipBytes == null || zipBytes.Length == 0)
            return (false, 400, "上传的压缩包为空。", null, null);
        if (zipBytes.Length > MaxZipBytes)
            return (false, 400, $"压缩包体积 {zipBytes.Length} 字节超出上限 25 MB。", null, null);

        string modsRoot = _modHost.ModsRootDirectory;
        Directory.CreateDirectory(modsRoot);
        string tempExtract = Path.Combine(modsRoot, $".import_{Guid.NewGuid():N}");
        string? sameVolStage = null;
        try
        {
            Directory.CreateDirectory(tempExtract);
            var extractResult = SafeZipExtractor.ExtractToDirectory(
                zipBytes,
                tempExtract,
                new SafeZipExtractOptions(
                    MaxEntries: MaxZipEntries,
                    MaxTotalUncompressedBytes: MaxZipUncompressedBytes,
                    MaxSingleFileBytes: MaxZipUncompressedBytes,
                    ForbiddenExtensions: ForbiddenZipExtensions,
                    RejectSymlinks: true));
            if (!extractResult.Ok)
                return (false, 400, extractResult.ErrorZh ?? "压缩包安全解压失败。", null, null);

            // 兼容顶层嵌套了一层 <mod-id>/ 目录的压缩包结构
            string actualModDir = tempExtract;
            if (!File.Exists(Path.Combine(actualModDir, "mod.json")))
            {
                var subDirs = Directory.GetDirectories(tempExtract);
                if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "mod.json")))
                    actualModDir = subDirs[0];
                else
                    return (false, 400, "压缩包根目录缺少必需的 mod.json 清单文件。", null, null);
            }

            var scanIssues = new List<ModValidationIssue>();
            var pkg = ModLoader.LoadSinglePackage(actualModDir, "imported", scanIssues);
            if (pkg == null || !ModValidator.IsValidModId(pkg.Manifest.Id))
                return (false, 422, $"导入的 mod.json 无效或 Mod ID 非法：{string.Join("; ", scanIssues.Select(i => i.Message))}", null, null);

            string modId = pkg.Manifest.Id.Trim();

            // 纳入并发压测限流，隔离跑 10 道防 Bug 铁闸（导入时跑 50 场真实战斗压测）
            bool acquired = await _validationThrottle.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (!acquired)
                return (false, 429, "服务端校验沙盒繁忙（并发压测已达上限），请稍候再试。", modId, null);

            ModTenGateValidationReport report;
            try
            {
                report = await Task.Run(() =>
                    ModValidator.RunTenGateValidation(
                        pkg,
                        _modHost.DiscoveredPackages,
                        _modHost.BaseRepository,
                        _modHost.ValidVisualWhitelist,
                        combatMatches: 50,
                        combatSeed: 2026131,
                        runDynamicGates: true), ct).ConfigureAwait(false);
            }
            finally
            {
                _validationThrottle.Release();
            }

            if (scanIssues.Count > 0 && report.Gates.Count > 0)
            {
                report.Gates[0].Issues.InsertRange(0, scanIssues);
                if (scanIssues.Any(i => i.Severity == ModValidationSeverity.Error))
                {
                    report.Gates[0].Status = "FAIL";
                    report.Passed = false;
                    report.ErrorCount = report.Gates.Sum(g => g.Issues.Count(i => i.Severity == ModValidationSeverity.Error));
                }
            }

            // 严禁任何 Error 病包入库（allowDraftWithWarnings 仅语义允许 0 Error + 存在 Warning 的草稿透传）
            _ = allowDraftWithWarnings;
            if (!report.Passed)
            {
                return (false, 422, $"导入体检未通过（{report.ErrorCount} 个错误），已严格拦截落盘防带病污染服务器。", modId, report);
            }

            // 同卷暂存 + 按 modId 加锁原子切换目录（绝不在锁内调用 _modHost.Reload）
            string targetDir = ResolveSafeModDir(modId);
            sameVolStage = Path.Combine(modsRoot, $".tmp_{modId}_{Guid.NewGuid():N}");
            CopyDirectoryRecursive(actualModDir, sameVolStage);

            lock (GetModDirLock(modId))
            {
                AtomicSwapDirectory(sameVolStage, targetDir, modsRoot, modId);
            }
            InvalidateValidationCache(modId);

            // 释放目录锁后再通知 ServerModHost 重新扫描发现列表
            _modHost.Reload();
            return (true, 200, null, modId, report);
        }
        catch (InvalidDataException)
        {
            return (false, 400, "上传的文件不是有效的 ZIP / .bazaarmod.zip 压缩包。", null, null);
        }
        finally
        {
            TryDeleteDirQuietly(tempExtract);
            if (!string.IsNullOrEmpty(sameVolStage))
                TryDeleteDirQuietly(sameVolStage);
        }
    }

    public (bool Ok, int StatusCode, string? Error) DeleteDraftMod(string modId, bool allowSampleDelete = false)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, 400, $"非法 Mod ID：{modId}");

        string clean = modId.Trim();
        if (!allowSampleDelete && string.Equals(clean, "bazaar-chronicles-sample", StringComparison.OrdinalIgnoreCase))
            return (false, 403, "系统官方示例包 bazaar-chronicles-sample 受保护禁止直接删除（可先禁用或克隆为新 Mod）。");

        string modDir = ResolveSafeModDir(clean);
        lock (GetModDirLock(clean))
        {
            if (!Directory.Exists(modDir))
                return (false, 404, $"未找到 Mod 目录：{clean}");

            Directory.Delete(modDir, recursive: true);
        }
        InvalidateValidationCache(clean);

        // 释放目录锁后再通知 ServerModHost 移除状态并重载
        _modHost.RemoveModStateAndReload(clean);
        return (true, 200, null);
    }

    /// <summary>
    /// P1-18：计算指定 Mod 磁盘目录的确定性内容指纹（覆盖全部有效源码/资产文件字节 SHA-256 及其他已发现 Mod 上下文哈希）与当前服务端 ModSetHash，
    /// 保证任意文件内容或依赖集合变动都会在毫秒级立即触发导出校验缓存失效。
    /// </summary>
    internal (string ContentFingerprint, string ModSetHash) ComputeDiskValidationFingerprint(string modId, string modDir)
    {
        string cleanId = modId.Trim();
        string modSetHash = _modHost.Registry.ComputeModSetHash();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void AppendUtf8(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            hasher.AppendData(bytes);
        }

        AppendUtf8("workshop-disk-validation:v1:");
        AppendUtf8(cleanId);
        AppendUtf8("|");

        lock (GetModDirLock(cleanId))
        {
            if (Directory.Exists(modDir))
            {
                var files = Directory.GetFiles(modDir, "*.*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                byte[] buf = new byte[16 * 1024];
                foreach (var f in files)
                {
                    string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(modDir, f));
                    if (rel.StartsWith('.') || rel.Contains("/.") || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string ext = Path.GetExtension(f);
                    if (ForbiddenZipExtensions.Contains(ext))
                        continue;

                    AppendUtf8(rel);
                    AppendUtf8("\0");
                    using var fs = File.OpenRead(f);
                    AppendUtf8(fs.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    AppendUtf8("\0");
                    int read;
                    while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        hasher.AppendData(buf, 0, read);
                    }
                    AppendUtf8("\n");
                }
            }
        }

        // 同步纳入其他已发现 Mod 包的哈希（避免跨 Mod ID 碰撞或依赖包变动后沿用过期缓存）
        AppendUtf8("|ctx:");
        var otherCtx = _modHost.DiscoveredPackages
            .Where(p => !string.Equals(p.Manifest.Id, cleanId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var p in otherCtx)
        {
            AppendUtf8(p.Manifest.Id);
            AppendUtf8("@");
            AppendUtf8(p.Manifest.Version);
            AppendUtf8("=");
            AppendUtf8(p.PackageHash ?? string.Empty);
            AppendUtf8(";");
        }

        string contentFp = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return (contentFp, modSetHash);
    }

    // ── 内部工具 ──────────────────────────────────────────────────────────────

    private List<BaseCardIndexEntry> BuildBaseCardIndex()
    {
        var baseRepo = _modHost.BaseRepository;
        var whitelist = _modHost.ValidVisualWhitelist;
        var loc = baseRepo.Localization;
        var list = new List<BaseCardIndexEntry>(baseRepo.CardsById.Count);

        foreach (var card in baseRepo.CardsById.Values)
        {
            string titleZh = loc?.GetLocalizedTitle(card) ?? card.DisplayTitle ?? card.InternalName;
            string titleEn = card.InternalName ?? string.Empty;
            bool isWl = whitelist.Contains(card.Id);

            var summaryAttrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (card.Attributes != null)
            {
                foreach (var (k, v) in card.Attributes) summaryAttrs[k] = v;
            }
            if (card.Tiers != null && card.Tiers.TryGetValue(card.StartingTier, out var startTd))
            {
                foreach (var (k, v) in startTd.Attributes) summaryAttrs[k] = v;
            }
            else if (card.Tiers != null && card.Tiers.Count > 0)
            {
                foreach (var (k, v) in card.Tiers.Values.First().Attributes) summaryAttrs[k] = v;
            }

            var tipsZh = new List<string>();
            if (card.Localization?.Tooltips != null)
            {
                foreach (var t in card.Localization.Tooltips)
                {
                    string txt = loc?.GetLocalizedText(t.Content) ?? t.Content?.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(txt)) tipsZh.Add(txt);
                }
            }

            string hay = $"{card.Id} {titleZh} {titleEn} {card.Type} {card.Size} {string.Join(' ', card.Heroes)} {string.Join(' ', card.Tags)} {string.Join(' ', card.HiddenTags)}".ToLowerInvariant();

            list.Add(new BaseCardIndexEntry(
                TemplateId: card.Id,
                TitleZh: titleZh,
                TitleEn: titleEn,
                Type: card.Type,
                Size: card.Size,
                StartingTier: card.StartingTier,
                Heroes: card.Heroes.Count > 0 ? new List<string>(card.Heroes) : [HeroNames.Common],
                Tags: new List<string>(card.Tags),
                HiddenTags: new List<string>(card.HiddenTags),
                IsWhitelisted: isWl,
                SummaryAttrs: summaryAttrs,
                TooltipsZh: tipsZh,
                SearchHaystack: hay));
        }

        list.Sort((a, b) =>
        {
            int wlCmp = b.IsWhitelisted.CompareTo(a.IsWhitelisted);
            if (wlCmp != 0) return wlCmp;
            int typeCmp = StringComparer.Ordinal.Compare(a.Type, b.Type);
            if (typeCmp != 0) return typeCmp;
            return StringComparer.OrdinalIgnoreCase.Compare(a.TitleZh, b.TitleZh);
        });
        return list;
    }

    private string ResolveSafeModDir(string modId)
    {
        if (!SafePathGuard.TryResolveUnderRoot(_modHost.ModsRootDirectory, modId.Trim(), out string candidate))
            throw new InvalidOperationException($"越界 Mod 目录路径：{modId}");
        return candidate;
    }

    private static void WriteJson(string path, object obj)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts), new UTF8Encoding(false));
    }

    private static void RebuildArtManifest(string modDir, string modId)
    {
        string assetsDir = Path.Combine(modDir, "assets");
        if (!Directory.Exists(assetsDir)) return;

        var entries = new List<object>();
        var files = Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories)
            .Where(f => IsSupportedAssetExtension(Path.GetExtension(f)))
            .OrderBy(f => ModValidator.NormalizeLogicalPath(Path.GetRelativePath(modDir, f)), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            bool valid = ModValidator.TryValidateImageHeader(bytes, out int w, out int h, out string ct);
            string contentType = valid && !string.IsNullOrWhiteSpace(ct)
                ? ct
                : (string.Equals(Path.GetExtension(f), ".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(modDir, f));
            entries.Add(new
            {
                filename = Path.GetFileName(f),
                logicalPath = rel,
                contentType,
                width = w,
                height = h,
                bytes = bytes.LongLength,
                sha256 = sha,
                customArtHash = $"sha256:{sha}"
            });
        }

        WriteJson(Path.Combine(assetsDir, "art_manifest.json"), new
        {
            schemaVersion = 1,
            modId,
            updatedAtUtc = DateTimeOffset.UtcNow,
            assets = entries
        });
    }

    private static bool IsSupportedAssetExtension(string? ext) =>
        string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeImageFileName(string rawName, string contentType)
    {
        string name = Path.GetFileNameWithoutExtension(rawName ?? string.Empty).Trim().ToLowerInvariant();
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                sb.Append(ch);
            else if (ch == ' ')
                sb.Append('_');
        }
        string clean = sb.ToString().Trim('_', '-');
        if (string.IsNullOrEmpty(clean))
            clean = $"art_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        if (clean.Length > 48) clean = clean[..48];
        string ext = string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
        return clean + ext;
    }

    private static string SanitizeFileSegment(string raw, string fallback)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch is '.' or '_' or '-')
                sb.Append(ch);
        }
        string cleaned = sb.ToString().Trim('.', '-', '_');
        if (cleaned.Length > 40) cleaned = cleaned[..40];
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }

    private static string EnsureSafeMatchingContentRoot(
        string? existing,
        IReadOnlyList<string> writtenRelPaths,
        string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return defaultValue;

        string normalized = ModValidator.NormalizeLogicalPath(existing.Trim());
        string probe = normalized.Replace("*", "x", StringComparison.Ordinal);
        if (!SafePathGuard.IsSafeRelativePath(probe))
            return defaultValue;

        if (writtenRelPaths.Count == 0)
            return normalized;

        foreach (var rawTarget in writtenRelPaths)
        {
            string target = ModValidator.NormalizeLogicalPath(rawTarget);
            if (!ContentRootMatches(normalized, target))
                return defaultValue;
        }
        return normalized;
    }

    private static bool ContentRootMatches(string pattern, string targetRelPath)
    {
        if (string.Equals(pattern, targetRelPath, StringComparison.OrdinalIgnoreCase))
            return true;

        int starIdx = pattern.IndexOf('*');
        if (starIdx < 0)
            return false;

        string dirPrefix = pattern[..starIdx];
        string suffix = pattern[(starIdx + 1)..];
        if (!targetRelPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(suffix) && !targetRelPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        return targetRelPath.Length >= dirPrefix.Length + suffix.Length;
    }

    private static void AtomicSwapDirectory(string sameVolStageDir, string targetDir, string modsRoot, string modId)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.Move(sameVolStageDir, targetDir);
            return;
        }

        string backupDir = Path.Combine(modsRoot, $".bak_{modId}_{Guid.NewGuid():N}");
        Directory.Move(targetDir, backupDir);
        try
        {
            Directory.Move(sameVolStageDir, targetDir);
        }
        catch
        {
            try
            {
                if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                    Directory.Move(backupDir, targetDir);
            }
            catch { }
            throw;
        }
        TryDeleteDirQuietly(backupDir);
    }

    private static string MakeSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "custom_card";
        var sb = new StringBuilder();
        foreach (char ch in input.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        string slug = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(slug) ? "custom_card" : slug;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDeleteDirQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
