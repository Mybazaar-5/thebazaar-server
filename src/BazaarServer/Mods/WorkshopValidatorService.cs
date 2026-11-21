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

    private readonly ServerModHost _modHost;
    private readonly SemaphoreSlim _validationThrottle = new(2, 2);
    private readonly Lazy<List<BaseCardIndexEntry>> _baseCardIndex;
    private readonly Lazy<string> _cachedRuleCatalogJson;
    private readonly Lazy<string> _cachedRuleCatalogEtag;

    public WorkshopValidatorService(ServerModHost modHost)
    {
        _modHost = modHost;
        _baseCardIndex = new Lazy<List<BaseCardIndexEntry>>(BuildBaseCardIndex);
        _cachedRuleCatalogJson = new Lazy<string>(() => JsonSerializer.Serialize(ModRuleCatalog.BuildCatalogDto(), JsonOpts));
        _cachedRuleCatalogEtag = new Lazy<string>(() =>
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_cachedRuleCatalogJson.Value));
            return $"\"{Convert.ToHexString(hash).ToLowerInvariant()[..16]}\"";
        });
    }

    public string RuleCatalogJson => _cachedRuleCatalogJson.Value;
    public string RuleCatalogEtag => _cachedRuleCatalogEtag.Value;

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
        bool isNewMod = !Directory.Exists(targetDir) || !File.Exists(Path.Combine(targetDir, "mod.json"));
        if (isNewMod)
            workspace.Manifest.EnabledByDefault = false;

        string stageDir = Path.Combine(modsRoot, $".tmp_{modId}_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stageDir);

            // 保留已有贴图资产，避免保存草稿丢失已上传 PNG
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

            // 规范 contentRoots 默认路径
            workspace.Manifest.ContentRoots.Cards = "cards/custom_cards.json";
            workspace.Manifest.ContentRoots.Patches = "patches/vanilla_patches.json";
            workspace.Manifest.ContentRoots.Events = "events/custom_events.json";
            workspace.Manifest.ContentRoots.Modes = "modes/*.json";
            workspace.Manifest.ContentRoots.Localization = "localization/zh-CN.json";
            workspace.Manifest.ContentRoots.Assets = "assets/*.png";
            workspace.Manifest.ContentRoots.ArtManifest = "assets/art_manifest.json";

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

            // 原子同步落盘：移除旧 JSON 目录内容并用新 staged 目录覆盖（保留 assets）
            Directory.CreateDirectory(targetDir);
            string[] managedSubdirs = ["cards", "patches", "events", "monsters", "modes", "localization"];
            foreach (var sub in managedSubdirs)
            {
                string oldSub = Path.Combine(targetDir, sub);
                if (Directory.Exists(oldSub))
                    Directory.Delete(oldSub, recursive: true);
                string newSub = Path.Combine(stageDir, sub);
                if (Directory.Exists(newSub))
                    CopyDirectoryRecursive(newSub, oldSub);
            }
            File.Copy(Path.Combine(stageDir, "mod.json"), Path.Combine(targetDir, "mod.json"), overwrite: true);
            string stagedArtManifest = Path.Combine(stageDir, "assets", "art_manifest.json");
            if (File.Exists(stagedArtManifest))
            {
                Directory.CreateDirectory(Path.Combine(targetDir, "assets"));
                File.Copy(stagedArtManifest, Path.Combine(targetDir, "assets", "art_manifest.json"), overwrite: true);
            }

            var issues = new List<ModValidationIssue>();
            var loaded = ModLoader.LoadSinglePackage(targetDir, modId, issues);
            return (true, null, modId, loaded?.PackageHash ?? string.Empty);
        }
        finally
        {
            TryDeleteDirQuietly(stageDir);
        }
    }

    // ── 3. PNG 资产上传校验与 SHA-256 登记 ────────────────────────────────────

    public (bool Ok, string? Error, object? AssetInfo) SavePngAsset(string modId, string logicalFileName, byte[] rawBytes)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, $"非法 Mod ID：{modId}", null);
        if (rawBytes == null || rawBytes.Length == 0)
            return (false, "上传的图片内容为空。", null);
        if (rawBytes.Length > MaxRawUploadBytes)
            return (false, $"上传图片体积 {rawBytes.Length} 字节超出上限 8 MB。", null);

        if (!ModValidator.TryValidateImageHeader(rawBytes, out int width, out int height, out string contentType))
            return (false, "仅支持合法的标准 PNG 图片（PNG 8 字节魔数头校验失败；如为 JPG/WebP 请先在网页工坊裁切器转换为 PNG）。", null);

        if (!string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase))
            return (false, "原版 Unity 客户端不支持 WebP 解码，请上传标准 PNG 图片（网页工坊裁切器会自动转 PNG）。", null);

        if (rawBytes.Length > ModValidator.MaxAssetBytes)
            return (false, $"PNG 体积 {rawBytes.Length} 字节超出单图上限 2 MB，请适当缩小分辨率或压缩。", null);

        if (width < ModValidator.MinAssetDimension || height < ModValidator.MinAssetDimension
            || width > ModValidator.MaxAssetWidth || height > ModValidator.MaxAssetHeight)
        {
            return (false, $"PNG 分辨率 {width}×{height} 超出安全范围 [{ModValidator.MinAssetDimension}, {ModValidator.MaxAssetWidth}]。", null);
        }

        string safeName = SanitizePngFileName(logicalFileName);
        string modDir = ResolveSafeModDir(modId);
        string assetsDir = Path.Combine(modDir, "assets");
        Directory.CreateDirectory(assetsDir);

        string destPath = Path.Combine(assetsDir, safeName);
        string tmpPath = destPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmpPath, rawBytes);
        File.Move(tmpPath, destPath, overwrite: true);

        string sha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        string logicalPath = $"assets/{safeName}";
        RebuildArtManifest(modDir, modId);

        return (true, null, new
        {
            ok = true,
            modId,
            logicalPath,
            sha256 = sha,
            width,
            height,
            bytes = rawBytes.LongLength,
            contentType = "image/png",
            previewUrl = $"/api/workshop/mods/{modId}/assets/{logicalPath}"
        });
    }

    public bool TryResolveDraftAssetFile(string modId, string logicalPath, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "image/png";
        if (!ModValidator.IsValidModId(modId)) return false;
        string normalized = ModValidator.NormalizeLogicalPath(logicalPath);
        if (!ModValidator.IsSafeRelativePath(normalized)) return false;

        string modDir = ResolveSafeModDir(modId);
        string candidate = Path.GetFullPath(Path.Combine(modDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string fullRoot = Path.GetFullPath(modDir) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            return false;

        fullPath = candidate;
        contentType = candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "application/octet-stream";
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
            return await Task.Run(() => RunValidationCore(modId, inMemoryDraft, matches, seed), ct).ConfigureAwait(false);
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
            targetPkg = BuildInMemoryPackageFromWorkspace(draft, effectiveId);
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

    private ModPackage BuildInMemoryPackageFromWorkspace(WorkshopModWorkspace draft, string modId)
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
        pkg.LocalizationOverlay = draft.Localization;

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

    public async Task<(bool Ok, string? Error, byte[]? ZipBytes, string? FileName)> ExportZipAsync(string modId, CancellationToken ct = default)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, $"非法 Mod ID：{modId}", null, null);

        string modDir = ResolveSafeModDir(modId);
        if (!Directory.Exists(modDir) || !File.Exists(Path.Combine(modDir, "mod.json")))
            return (false, $"未找到 Mod '{modId}'。", null, null);

        var validation = await ValidateAsync(modId, null, matches: 20, seed: 2026131, ct: ct).ConfigureAwait(false);
        string version = "1.0.0";
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
                    errorCount = validation.ErrorCount,
                    warningCount = validation.WarningCount,
                    summaryZh = validation.SummaryZh
                }, JsonOpts));
            }
        }

        return (true, null, ms.ToArray(), $"{modId}-{version}.bazaarmod.zip");
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

        string tempExtract = Path.Combine(_modHost.ModsRootDirectory, $".import_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempExtract);
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count == 0)
                    return (false, 400, "压缩包为空，未包含 mod.json。", null, null);
                if (archive.Entries.Count > MaxZipEntries)
                    return (false, 400, $"压缩包条目数 {archive.Entries.Count} 超过安全上限 {MaxZipEntries}。", null, null);

                long totalUncompressed = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                        continue;

                    string rel = ModValidator.NormalizeLogicalPath(entry.FullName);
                    if (!ModValidator.IsSafeRelativePath(rel))
                        return (false, 400, $"安全拦截：压缩包包含非法路径穿越条目 '{entry.FullName}'（Zip Slip 防护）。", null, null);

                    string ext = Path.GetExtension(rel);
                    if (ForbiddenZipExtensions.Contains(ext))
                        return (false, 400, $"安全拦截：严禁导入包含可执行/脚本后缀的文件 '{rel}'。", null, null);

                    totalUncompressed += entry.Length;
                    if (totalUncompressed > MaxZipUncompressedBytes)
                        return (false, 400, "安全拦截：解压总体积超出 64 MB 限制（Zip Bomb 防护）。", null, null);

                    string destFile = Path.GetFullPath(Path.Combine(tempExtract, rel.Replace('/', Path.DirectorySeparatorChar)));
                    string rootGuard = Path.GetFullPath(tempExtract) + Path.DirectorySeparatorChar;
                    if (!destFile.StartsWith(rootGuard, StringComparison.OrdinalIgnoreCase))
                        return (false, 400, $"安全拦截：目标路径越界 '{entry.FullName}'。", null, null);

                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    using var src = entry.Open();
                    using var dst = File.Create(destFile);
                    long copied = 0;
                    var buf = new byte[16384];
                    int read;
                    while ((read = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        copied += read;
                        if (copied > MaxZipUncompressedBytes)
                            return (false, 400, "安全拦截：单文件解压流超出 64 MB 上限（Zip Bomb 防护）。", null, null);
                        dst.Write(buf, 0, read);
                    }
                }
            }

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

            // 隔离跑 10 道防 Bug 铁闸（导入时跑 50 场真实战斗压测）
            var report = await Task.Run(() =>
                ModValidator.RunTenGateValidation(
                    pkg,
                    _modHost.DiscoveredPackages,
                    _modHost.BaseRepository,
                    _modHost.ValidVisualWhitelist,
                    combatMatches: 50,
                    combatSeed: 2026131,
                    runDynamicGates: true), ct).ConfigureAwait(false);

            if (!report.Passed && !allowDraftWithWarnings)
            {
                return (false, 422, $"导入体检未通过（{report.ErrorCount} 个错误），已拦截落盘防带病污染服务器。", modId, report);
            }

            // 安全落盘到 mods/<mod-id>/（导入默认禁用草稿态，由用户确认发布热重载）
            string targetDir = ResolveSafeModDir(modId);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            CopyDirectoryRecursive(actualModDir, targetDir);

            // 保证新导入的 Mod 若未通过全绿则强制设为禁用草稿
            if (!report.Passed)
                _modHost.SetModEnabledAndReload(modId, enabled: false);
            else
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
        if (!Directory.Exists(modDir))
            return (false, 404, $"未找到 Mod 目录：{clean}");

        Directory.Delete(modDir, recursive: true);
        _modHost.RemoveModStateAndReload(clean);
        return (true, 200, null);
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
        string fullRoot = Path.GetFullPath(_modHost.ModsRootDirectory);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, modId.Trim()));
        string guard = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(guard, StringComparison.OrdinalIgnoreCase))
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
        var files = Directory.GetFiles(assetsDir, "*.png", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            ModValidator.TryValidateImageHeader(bytes, out int w, out int h, out _);
            string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(modDir, f));
            entries.Add(new
            {
                filename = Path.GetFileName(f),
                logicalPath = rel,
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

    private static string SanitizePngFileName(string rawName)
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
        return clean + ".png";
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
