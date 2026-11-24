using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Mods;

/// <summary>
/// BazaarModKit 磁盘扫描与加载器：
/// 从 mods/&lt;mod-id&gt;/ 目录解析 mod.json、cards/*.json、events/*.json、monsters/*.json、modes/*.json、
/// localization/zh-CN.json 及 assets/* 贴图资产（计算 SHA-256 与魔数头校验），并构建模板级视觉绑定与确定性包哈希。
/// </summary>
public static class ModLoader
{
    private static readonly JsonSerializerOptions ModJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    /// <summary>P0-28（方案 A）：显式登记以便送入 Gate #8 报 ASSET_FORMAT_UNSUPPORTED_FOR_UNITY，严禁静默跳过导致漏检。</summary>
    private static readonly HashSet<string> ExplicitlyRejectedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp"
    };

    /// <summary>
    /// 扫描指定 mods 根目录下的全部候选 Mod 目录，返回解析后的 <see cref="ModPackage"/> 列表与扫描阶段问题列表。
    /// 仅做文件读取、JSON 反序列化与资产魔数/哈希采集，跨 Mod 依赖排序与强白名单校验由 <see cref="ModValidator"/> 统一执行。
    /// </summary>
    public static (List<ModPackage> Packages, List<ModValidationIssue> ScanIssues) ScanDirectory(string modsRoot)
    {
        var packages = new List<ModPackage>();
        var issues = new List<ModValidationIssue>();

        if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
            return (packages, issues);

        string fullRoot = Path.GetFullPath(modsRoot);
        var subDirs = Directory.GetDirectories(fullRoot);
        Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < subDirs.Length; i++)
        {
            string dir = subDirs[i];
            string dirName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(dirName) || dirName.StartsWith('.') || dirName.StartsWith('_'))
                continue;

            string manifestPath = Path.Combine(dir, "mod.json");
            if (!File.Exists(manifestPath))
                continue;

            var pkg = LoadSinglePackage(dir, dirName, issues);
            if (pkg != null)
                packages.Add(pkg);
        }

        return (packages, issues);
    }

    /// <summary>
    /// 从单个 Mod 目录加载完整 <see cref="ModPackage"/>。
    /// </summary>
    public static ModPackage? LoadSinglePackage(string modDirectory, string fallbackModId, List<ModValidationIssue> issues)
    {
        string fullDir = Path.GetFullPath(modDirectory);
        string manifestPath = Path.Combine(fullDir, "mod.json");
        if (!File.Exists(manifestPath))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                fallbackModId,
                "MANIFEST_NOT_FOUND",
                $"Mod 目录 '{fullDir}' 缺少必需的 mod.json 清单文件。"));
            return null;
        }

        ModManifest? manifest;
        try
        {
            string manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            manifest = JsonSerializer.Deserialize<ModManifest>(manifestJson, ModJsonOptions);
        }
        catch (Exception ex)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                fallbackModId,
                "MANIFEST_PARSE_ERROR",
                $"解析 mod.json 失败：{ex.Message}"));
            return null;
        }

        if (manifest == null)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                fallbackModId,
                "MANIFEST_EMPTY",
                $"Mod 清单 '{manifestPath}' 为空。"));
            return null;
        }

        manifest.ContentRoots ??= new ModContentRoots();
        manifest.Dependencies ??= new List<string>();
        string modId = string.IsNullOrWhiteSpace(manifest.Id) ? fallbackModId : manifest.Id.Trim();
        manifest.Id = modId;

        var pkg = new ModPackage
        {
            Manifest = manifest,
            RootDirectory = fullDir
        };

        // 1. 扫描客户端资产目录（记录全部候选图像及 art_manifest.json 预期 SHA-256，交由 ModValidator Gate #8 统一判定）
        ScanAssets(pkg, issues);
        LoadArtManifestExpectations(pkg, issues);

        // 2. 加载自定义卡牌、原位补丁(patches/*.json)、事件、可选独立野怪、自定义模式与自定义突变词条(P0-22)
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Cards, "cards"), pkg.Cards, "CARD_JSON_ERROR", "cards", issues);
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Patches, "patches"), pkg.Patches, "PATCH_JSON_ERROR", "patches", issues);
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Events, "events"), pkg.Events, "EVENT_JSON_ERROR", "events", issues);
        LoadJsonEntries(pkg, Path.Combine(fullDir, "monsters"), pkg.Monsters, "MONSTER_JSON_ERROR", "monsters", issues);
        string modesTarget = ResolveContentTarget(fullDir, manifest.ContentRoots.Modes, "modes");
        LoadJsonEntries(pkg, modesTarget, pkg.Modes, "MODE_JSON_ERROR", "modes", issues, skipMutatorOnlyFiles: true);
        LoadCustomMutators(pkg, modesTarget, issues);
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Mechanics, "mechanics"), pkg.Mechanics, "MECHANIC_JSON_ERROR", "mechanics", issues);

        // 3. 加载 Mod 自定义中文词典（localization/zh-CN.json）
        LoadLocalizationOverlay(pkg, issues);

        // 4. 构建模板级 CustomCardVisualBinding 与确定性包哈希
        BuildVisualBindings(pkg);
        pkg.PackageHash = ComputePackageHash(pkg);

        return pkg;
    }

    /// <summary>
    /// 为内存或磁盘构建的 <see cref="ModPackage"/> 统一重建模板级视觉绑定表（含资产 SHA-256 关联）。
    /// </summary>
    public static void BuildVisualBindings(ModPackage pkg)
    {
        pkg.VisualBindingsByCustomId.Clear();
        string modId = pkg.Manifest.Id;
        var pkgMechMap = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Mechanics.Count; i++)
        {
            var m = pkg.Mechanics[i];
            if (m != null && !string.IsNullOrWhiteSpace(m.Id))
                pkgMechMap[m.Id.Trim()] = m;
        }

        var pkgCardMap = new Dictionary<string, CustomCardDefinition>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Cards.Count; i++)
        {
            var c = pkg.Cards[i];
            if (c != null && !string.IsNullOrWhiteSpace(c.CustomId))
                pkgCardMap[c.CustomId.Trim()] = c;
        }

        void ResolveArt(string? rawArt, out string? normalizedPath, out string? sha256)
        {
            normalizedPath = null;
            sha256 = null;
            if (string.IsNullOrWhiteSpace(rawArt)) return;
            normalizedPath = ModValidator.NormalizeLogicalPath(rawArt);
            if (pkg.AssetsByLogicalPath.TryGetValue(normalizedPath, out var asset))
                sha256 = asset.Sha256;
        }

        CustomMonsterPreviewBinding BuildMonsterPreview(MonsterTemplate mon, CustomEventDefinition? parentEvent)
        {
            var preview = new CustomMonsterPreviewBinding
            {
                MonsterId = mon.Id,
                NameZh = !string.IsNullOrWhiteSpace(parentEvent?.TitleZh)
                    ? parentEvent!.TitleZh
                    : (!string.IsNullOrWhiteSpace(mon.InternalName) ? mon.InternalName : mon.Id),
                NameEn = !string.IsNullOrWhiteSpace(parentEvent?.TitleEn)
                    ? parentEvent!.TitleEn
                    : mon.InternalName,
                MinDay = parentEvent != null && parentEvent.MinDay > 1 ? parentEvent.MinDay : null,
                MaxDay = parentEvent != null && parentEvent.MaxDay is >= 1 and < 99 ? parentEvent.MaxDay : null,
                DifficultySlot = parentEvent?.DifficultySlot,
                Level = mon.Player != null ? (int)Math.Round(mon.Player.Attributes.GetValueOrDefault("Level", 1)) : 1,
                HealthMax = mon.Player != null ? (int)Math.Round(mon.Player.Attributes.GetValueOrDefault("HealthMax", 300)) : 300,
                StartingTier = !string.IsNullOrWhiteSpace(parentEvent?.StartingTier) ? parentEvent!.StartingTier! : "Bronze",
                RewardGold = parentEvent?.RewardCombatGold ?? 0,
                RewardXp = parentEvent?.RewardCombatXp ?? 0
            };

            if (mon.Player != null)
            {
                if (mon.Player.Hand?.Items != null)
                {
                    foreach (var item in mon.Player.Hand.Items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.TemplateId)) continue;
                        string tid = item.TemplateId.Trim();
                        pkgCardMap.TryGetValue(tid, out var modCard);
                        preview.Items.Add(new CustomMonsterLoadoutItemBinding
                        {
                            CardId = tid,
                            WireTemplateId = modCard?.BaseVisualTemplateId ?? tid,
                            Size = !string.IsNullOrWhiteSpace(modCard?.Size) ? modCard!.Size : "Small",
                            Tier = !string.IsNullOrWhiteSpace(item.Tier) ? item.Tier : "Bronze",
                            Enchantment = item.EnchantmentType,
                            TitleZh = modCard?.TitleZh,
                            TitleEn = modCard?.TitleEn
                        });
                    }
                }
                if (mon.Player.Skills != null)
                {
                    foreach (var sk in mon.Player.Skills)
                    {
                        if (sk == null || string.IsNullOrWhiteSpace(sk.TemplateId)) continue;
                        string tid = sk.TemplateId.Trim();
                        pkgCardMap.TryGetValue(tid, out var modSkill);
                        preview.Skills.Add(new CustomMonsterLoadoutItemBinding
                        {
                            CardId = tid,
                            WireTemplateId = modSkill?.BaseVisualTemplateId ?? tid,
                            Size = "Small",
                            Tier = !string.IsNullOrWhiteSpace(sk.Tier) ? sk.Tier : "Bronze",
                            Enchantment = sk.EnchantmentType,
                            TitleZh = modSkill?.TitleZh,
                            TitleEn = modSkill?.TitleEn
                        });
                    }
                }
            }
            return preview;
        }

        void RegisterBinding(CustomCardDefinition card, CustomMonsterPreviewBinding? monsterPreview = null)
        {
            if (string.IsNullOrWhiteSpace(card.CustomId)) return;
            string cid = card.CustomId.Trim();
            ResolveArt(card.CustomArtFile, out string? normalizedArt, out string? artSha256);
            ResolveArt(card.CustomBgArtFile, out string? normalizedBgArt, out string? bgArtSha256);

            var resolvedDisplays = ModRegistry.ResolveCounterDisplaysForCard(
                card.CounterDisplays,
                card.CustomMechanics,
                pkgMechMap);

            pkg.VisualBindingsByCustomId[cid] = new CustomCardVisualBinding
            {
                CustomTemplateId = cid,
                ModId = modId,
                BaseVisualTemplateId = card.BaseVisualTemplateId ?? string.Empty,
                CardType = card.Type,
                Size = card.Size,
                TitleZh = !string.IsNullOrWhiteSpace(card.TitleZh) ? card.TitleZh : (card.TitleEn ?? cid),
                DescZh = !string.IsNullOrWhiteSpace(card.DescZh) ? card.DescZh : (card.DescEn ?? string.Empty),
                ActiveTooltipsZh = new List<string>(card.ActiveTooltipsZh),
                PassiveTooltipsZh = new List<string>(card.PassiveTooltipsZh),
                EnchantTooltipsZh = card.EnchantTooltipsZh.ToDictionary(
                    kv => kv.Key,
                    kv => new List<string>(kv.Value),
                    StringComparer.OrdinalIgnoreCase),
                TitleEn = !string.IsNullOrWhiteSpace(card.TitleEn) ? card.TitleEn : null,
                DescEn = !string.IsNullOrWhiteSpace(card.DescEn) ? card.DescEn : null,
                ActiveTooltipsEn = card.ActiveTooltipsEn is { Count: > 0 } ? new List<string>(card.ActiveTooltipsEn) : null,
                PassiveTooltipsEn = card.PassiveTooltipsEn is { Count: > 0 } ? new List<string>(card.PassiveTooltipsEn) : null,
                EnchantTooltipsEn = card.EnchantTooltipsEn is { Count: > 0 }
                    ? card.EnchantTooltipsEn.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase)
                    : null,
                CustomArtFile = normalizedArt,
                CustomArtSha256 = artSha256,
                CustomBgArtFile = normalizedBgArt,
                CustomBgArtSha256 = bgArtSha256,
                VisualTarget = CustomCardVisualBinding.ResolveDefaultVisualTarget(card.Type),
                IsPatchOverride = false,
                IsCustomBorrowShell = !string.IsNullOrWhiteSpace(card.BaseVisualTemplateId),
                MonsterPreview = monsterPreview,
                ArtRect = card.ArtRect != null
                    ? new CustomCardArtRect { Width = card.ArtRect.Width, Height = card.ArtRect.Height, CropMode = card.ArtRect.CropMode }
                    : null,
                Tags = new List<string>(card.Tags),
                AccentColor = card.AccentColor,
                TierAttributes = BuildTierAttributesSnapshot(card),
                EnchantmentPreviewOps = BuildEnchantmentPreviewOpsSnapshot(card.Enchantments),
                CounterDisplays = resolvedDisplays.Count > 0 ? resolvedDisplays : null
            };
        }

        static Dictionary<string, Dictionary<string, double>>? BuildTierAttributesSnapshot(CustomCardDefinition card)
        {
            if (card.Tiers == null || card.Tiers.Count == 0) return null;
            var res = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            var running = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (card.Attributes != null)
            {
                foreach (var (k, v) in card.Attributes)
                    running[k] = v;
            }
            bool started = false;
            string startTierNorm = string.IsNullOrWhiteSpace(card.StartingTier) ? "Bronze" : card.StartingTier.Trim();
            foreach (var tierName in new[] { "Bronze", "Silver", "Gold", "Diamond", "Legendary" })
            {
                bool hasExplicit = card.Tiers.TryGetValue(tierName, out var td) && td != null;
                if (hasExplicit || string.Equals(tierName, startTierNorm, StringComparison.OrdinalIgnoreCase))
                    started = true;
                if (hasExplicit && td!.Attributes != null)
                {
                    foreach (var (k, v) in td.Attributes)
                        running[k] = v;
                }
                // D3-P1-01：从 StartingTier 起完整补齐至 Legendary 五阶底表
                if (started && running.Count > 0)
                    res[tierName] = new Dictionary<string, double>(running, StringComparer.OrdinalIgnoreCase);
            }
            return res.Count > 0 ? res : null;
        }

        static Dictionary<string, List<ResolvedPreviewAttrOp>>? BuildEnchantmentPreviewOpsSnapshot(
            Dictionary<string, EnchantmentData>? enchantments)
        {
            return ModRegistry.ExtractEnchantmentPreviewOps(enchantments);
        }

        for (int i = 0; i < pkg.Cards.Count; i++)
            RegisterBinding(pkg.Cards[i]);

        var boundMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Events.Count; i++)
        {
            var ev = pkg.Events[i];
            CustomMonsterPreviewBinding? monPreview = null;
            if (ev.CombatMonster != null)
            {
                monPreview = BuildMonsterPreview(ev.CombatMonster, ev);
                if (!string.IsNullOrWhiteSpace(ev.CombatMonster.Id))
                    boundMonsterIds.Add(ev.CombatMonster.Id.Trim());
            }
            RegisterBinding(ev.ToCardDefinition(), monPreview);
            for (int j = 0; j < ev.Steps.Count; j++)
                RegisterBinding(ev.Steps[j]);
        }

        // P0-21：补齐独立 pkg.Monsters 视觉绑定（确保不遗漏独立野怪的 MonsterPreview）
        for (int i = 0; i < pkg.Monsters.Count; i++)
        {
            var mon = pkg.Monsters[i];
            if (mon == null || string.IsNullOrWhiteSpace(mon.Id)) continue;
            string mid = mon.Id.Trim();
            if (pkg.VisualBindingsByCustomId.ContainsKey(mid) || boundMonsterIds.Contains(mid))
                continue;

            var preview = BuildMonsterPreview(mon, null);
            pkg.VisualBindingsByCustomId[mid] = new CustomCardVisualBinding
            {
                CustomTemplateId = mid,
                ModId = modId,
                BaseVisualTemplateId = string.Empty,
                CardType = "CombatEncounter",
                Size = "Medium",
                TitleZh = preview.NameZh,
                TitleEn = preview.NameEn,
                DescZh = string.Empty,
                VisualTarget = "EncounterPortrait",
                IsPatchOverride = false,
                IsCustomBorrowShell = false,
                MonsterPreview = preview
            };
        }

        // P0-25：任意字段改动 Patch 均生成 IsPatchOverride=true 视觉绑定（不仅限于文本/贴图改动）
        for (int i = 0; i < pkg.Patches.Count; i++)
        {
            var patch = pkg.Patches[i];
            if (string.IsNullOrWhiteSpace(patch.TargetTemplateId) || !patch.HasAnyMutation)
                continue;

            string tid = patch.TargetTemplateId.Trim();
            ResolveArt(patch.CustomArtFile, out string? normalizedArt, out string? artSha256);
            ResolveArt(patch.CustomBgArtFile, out string? normalizedBgArt, out string? bgArtSha256);

            var patchDisplays = ModRegistry.ResolveCounterDisplaysForCard(
                patch.CounterDisplaysOverride,
                patch.CustomMechanicsOverride,
                pkgMechMap);

            pkg.VisualBindingsByCustomId[tid] = new CustomCardVisualBinding
            {
                CustomTemplateId = tid,
                ModId = modId,
                BaseVisualTemplateId = tid,
                CardType = "Item",
                Size = "Medium",
                TitleZh = patch.TitleZh ?? string.Empty,
                DescZh = patch.DescZh ?? string.Empty,
                ActiveTooltipsZh = patch.ActiveTooltipsZh != null ? new List<string>(patch.ActiveTooltipsZh) : new List<string>(),
                PassiveTooltipsZh = patch.PassiveTooltipsZh != null ? new List<string>(patch.PassiveTooltipsZh) : new List<string>(),
                EnchantTooltipsZh = patch.EnchantTooltipsZh != null
                    ? patch.EnchantTooltipsZh.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
                TitleEn = !string.IsNullOrWhiteSpace(patch.TitleEn) ? patch.TitleEn : null,
                DescEn = !string.IsNullOrWhiteSpace(patch.DescEn) ? patch.DescEn : null,
                ActiveTooltipsEn = patch.ActiveTooltipsEn is { Count: > 0 } ? new List<string>(patch.ActiveTooltipsEn) : null,
                PassiveTooltipsEn = patch.PassiveTooltipsEn is { Count: > 0 } ? new List<string>(patch.PassiveTooltipsEn) : null,
                EnchantTooltipsEn = patch.EnchantTooltipsEn is { Count: > 0 }
                    ? patch.EnchantTooltipsEn.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase)
                    : null,
                CustomArtFile = normalizedArt,
                CustomArtSha256 = artSha256,
                CustomBgArtFile = normalizedBgArt,
                CustomBgArtSha256 = bgArtSha256,
                VisualTarget = "ItemArt",
                IsPatchOverride = true,
                IsCustomBorrowShell = false,
                ArtRect = patch.ArtRect != null
                    ? new CustomCardArtRect { Width = patch.ArtRect.Width, Height = patch.ArtRect.Height, CropMode = patch.ArtRect.CropMode }
                    : null,
                Tags = new List<string>(patch.AddTags),
                AccentColor = patch.AccentColor,
                CounterDisplays = patchDisplays.Count > 0 ? patchDisplays : null
            };
        }
    }

    /// <summary>
    /// D3-P1-08：对整包规范化确定性 JSON（Manifest + 排序后的 Cards/Patches/Events/Monsters/Modes/CustomMutators/Mechanics/Assets/LocalizationOverlay）求 SHA-256。
    /// 任意数值、词条、机制、模式或贴图变更 100% 导致包哈希精确变化。
    /// </summary>
    public static string ComputePackageHash(ModPackage pkg)
    {
        var canonicalPayload = new
        {
            manifest = pkg.Manifest,
            cards = pkg.Cards.OrderBy(c => c.CustomId, StringComparer.OrdinalIgnoreCase).ToList(),
            patches = pkg.Patches.OrderBy(p => p.TargetTemplateId, StringComparer.OrdinalIgnoreCase).ToList(),
            events = pkg.Events.OrderBy(e => e.EventId, StringComparer.OrdinalIgnoreCase).ToList(),
            monsters = pkg.Monsters.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            modes = pkg.Modes.OrderBy(m => m.ModeId, StringComparer.OrdinalIgnoreCase).ToList(),
            customMutators = pkg.CustomMutators.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            mechanics = pkg.Mechanics.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            assets = pkg.AssetsByLogicalPath
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { path = kv.Key, sha256 = kv.Value.Sha256, bytes = kv.Value.Bytes, contentType = kv.Value.ContentType })
                .ToList(),
            localizationOverlay = pkg.LocalizationOverlay
        };

        string canonicalJson = JsonSerializer.Serialize(canonicalPayload, ModJsonOptions);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ScanAssets(ModPackage pkg, List<ModValidationIssue> issues)
    {
        string assetsDir = ResolveGlobDirectory(pkg.RootDirectory, pkg.Manifest.ContentRoots.Assets, "assets");
        if (!Directory.Exists(assetsDir))
            return;

        string fullModRoot = Path.GetFullPath(pkg.RootDirectory);
        string fullAssetsDir = Path.GetFullPath(assetsDir);
        if (!IsPathInsideRoot(fullAssetsDir, fullModRoot))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                pkg.Manifest.Id,
                "ASSET_ROOT_TRAVERSAL",
                $"Mod 资产目录配置 '{pkg.Manifest.ContentRoots.Assets}' 越界。"));
            return;
        }

        var files = Directory.GetFiles(fullAssetsDir, "*.*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Length; i++)
        {
            string fullFile = files[i];
            string ext = Path.GetExtension(fullFile);
            bool isSupportedExt = SupportedImageExtensions.Contains(ext);
            bool isRejectedExt = ExplicitlyRejectedImageExtensions.Contains(ext);
            if (!isSupportedExt && !isRejectedExt)
                continue;

            string relPath = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(fullModRoot, fullFile));
            string fallbackContentType = ext.ToLowerInvariant() switch
            {
                ".webp" => "image/webp",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "application/octet-stream"
            };
            try
            {
                var fi = new FileInfo(fullFile);
                if (fi.Length > ModValidator.MaxAssetBytes)
                {
                    pkg.AssetsByLogicalPath[relPath] = new ModAssetEntry
                    {
                        ModId = pkg.Manifest.Id,
                        LogicalPath = relPath,
                        FullFilePath = fullFile,
                        Sha256 = string.Empty,
                        Bytes = fi.Length,
                        Width = 0,
                        Height = 0,
                        ContentType = fallbackContentType
                    };
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(fullFile);
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                bool headerOk = ModValidator.TryValidateImageHeader(bytes, out int w, out int h, out string contentType);

                pkg.AssetsByLogicalPath[relPath] = new ModAssetEntry
                {
                    ModId = pkg.Manifest.Id,
                    LogicalPath = relPath,
                    FullFilePath = fullFile,
                    Sha256 = sha,
                    Bytes = bytes.LongLength,
                    Width = headerOk ? w : 0,
                    Height = headerOk ? h : 0,
                    ContentType = headerOk
                        ? contentType
                        : (string.Equals(contentType, "image/webp", StringComparison.OrdinalIgnoreCase) || isRejectedExt
                            ? "image/webp"
                            : "application/octet-stream")
                };
            }
            catch (Exception ex)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    pkg.Manifest.Id,
                    "ASSET_READ_ERROR",
                    $"读取资产文件 '{relPath}' 失败：{ex.Message}",
                    relPath,
                    GateId: 8));
            }
        }
    }

    private static void LoadArtManifestExpectations(ModPackage pkg, List<ModValidationIssue> issues)
    {
        string relManifest = string.IsNullOrWhiteSpace(pkg.Manifest.ContentRoots.ArtManifest)
            ? "assets/art_manifest.json"
            : pkg.Manifest.ContentRoots.ArtManifest!;
        if (!ModValidator.IsSafeRelativePath(relManifest))
            return;

        string fullPath = Path.Combine(pkg.RootDirectory, relManifest.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!TryGetPropertyIgnoreCase(doc.RootElement, "assets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? lp = null;
                if (TryGetPropertyIgnoreCase(item, "logicalPath", out var lpEl) && lpEl.ValueKind == JsonValueKind.String)
                    lp = lpEl.GetString();
                else if (TryGetPropertyIgnoreCase(item, "filename", out var fnEl) && fnEl.ValueKind == JsonValueKind.String)
                    lp = "assets/" + fnEl.GetString();

                if (string.IsNullOrWhiteSpace(lp)) continue;
                if (TryGetPropertyIgnoreCase(item, "sha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.String)
                {
                    string sha = (shaEl.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(sha))
                        pkg.ExpectedAssetSha256ByPath[ModValidator.NormalizeLogicalPath(lp)] = sha;
                }
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning,
                pkg.Manifest.Id,
                "ART_MANIFEST_PARSE_WARNING",
                $"解析 '{relManifest}' 失败（将在保存时重建）：{ex.Message}",
                relManifest,
                GateId: 8));
        }
    }

    private static void LoadJsonEntries<T>(
        ModPackage pkg,
        string targetPathOrDir,
        List<T> sink,
        string errorCode,
        string? wrapperArrayPropertyName,
        List<ModValidationIssue> issues,
        bool skipMutatorOnlyFiles = false)
    {
        bool isSingleFile = File.Exists(targetPathOrDir);
        bool isDir = !isSingleFile && Directory.Exists(targetPathOrDir);
        if (!isSingleFile && !isDir)
            return;

        string fullModRoot = Path.GetFullPath(pkg.RootDirectory);
        string fullTarget = Path.GetFullPath(targetPathOrDir);
        if (!IsPathInsideRoot(fullTarget, fullModRoot))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                pkg.Manifest.Id,
                errorCode,
                $"内容路径 '{targetPathOrDir}' 越出 Mod 根目录边界。"));
            return;
        }

        string[] files = isSingleFile
            ? new[] { fullTarget }
            : Directory.GetFiles(fullTarget, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(fullModRoot, file));
            try
            {
                string raw = File.ReadAllText(file, Encoding.UTF8);
                using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = JsonSerializer.Deserialize<List<T>>(raw, ModJsonOptions);
                    if (list != null)
                    {
                        for (int j = 0; j < list.Count; j++)
                        {
                            if (list[j] != null)
                                sink.Add(list[j]!);
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // 优先兼容顶层包裹数组风格（如 { "cards": [...] } / { "events": [...] } / { "modes": [...] }）
                    if (!string.IsNullOrEmpty(wrapperArrayPropertyName)
                        && TryGetPropertyIgnoreCase(doc.RootElement, wrapperArrayPropertyName, out var arrEl)
                        && arrEl.ValueKind == JsonValueKind.Array)
                    {
                        var list = arrEl.Deserialize<List<T>>(ModJsonOptions);
                        if (list != null)
                        {
                            for (int j = 0; j < list.Count; j++)
                            {
                                if (list[j] != null)
                                    sink.Add(list[j]!);
                            }
                        }
                        continue;
                    }

                    // modes/custom_mutators.json 仅承载扩展词条数组（由 ServerModHost.ExtractModCustomMutators 解析），不当作空 CustomModeDefinition
                    if (skipMutatorOnlyFiles
                        && TryGetPropertyIgnoreCase(doc.RootElement, "mutators", out var mutEl)
                        && mutEl.ValueKind == JsonValueKind.Array
                        && !TryGetPropertyIgnoreCase(doc.RootElement, "modeId", out _))
                    {
                        continue;
                    }

                    var single = JsonSerializer.Deserialize<T>(raw, ModJsonOptions);
                    if (single != null)
                        sink.Add(single);
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    pkg.Manifest.Id,
                    errorCode,
                    $"解析文件 '{rel}' 失败：{ex.Message}",
                    rel));
            }
        }
    }

    /// <summary>
    /// P0-22：从 modes 目录解析所有自定义肉鸽突变词条（兼容 { "mutators": [...] } 与数组/单对象形式，完整映射 custom_mutators.json）。
    /// </summary>
    private static void LoadCustomMutators(ModPackage pkg, string modesTargetPathOrDir, List<ModValidationIssue> issues)
    {
        bool isSingleFile = File.Exists(modesTargetPathOrDir);
        bool isDir = !isSingleFile && Directory.Exists(modesTargetPathOrDir);
        if (!isSingleFile && !isDir) return;

        string fullModRoot = Path.GetFullPath(pkg.RootDirectory);
        string fullTarget = Path.GetFullPath(modesTargetPathOrDir);
        if (!IsPathInsideRoot(fullTarget, fullModRoot)) return;

        string[] files = isSingleFile
            ? new[] { fullTarget }
            : Directory.GetFiles(fullTarget, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            string rel = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(fullModRoot, file));
            string fileName = Path.GetFileName(file);
            bool isMutatorNamed = fileName.Contains("mutator", StringComparison.OrdinalIgnoreCase);

            try
            {
                string raw = File.ReadAllText(file, Encoding.UTF8);
                using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                List<CustomMutatorDefinition>? defs = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(doc.RootElement, "mutators", out var mutArr)
                    && mutArr.ValueKind == JsonValueKind.Array)
                {
                    defs = mutArr.Deserialize<List<CustomMutatorDefinition>>(ModJsonOptions);
                }
                else if (isMutatorNamed && doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    defs = JsonSerializer.Deserialize<List<CustomMutatorDefinition>>(raw, ModJsonOptions);
                }
                else if (isMutatorNamed && doc.RootElement.ValueKind == JsonValueKind.Object
                    && !TryGetPropertyIgnoreCase(doc.RootElement, "modeId", out _)
                    && TryGetPropertyIgnoreCase(doc.RootElement, "id", out _))
                {
                    var single = JsonSerializer.Deserialize<CustomMutatorDefinition>(raw, ModJsonOptions);
                    if (single != null) defs = new List<CustomMutatorDefinition> { single };
                }

                if (defs == null) continue;
                for (int j = 0; j < defs.Count; j++)
                {
                    var d = defs[j];
                    if (d == null || string.IsNullOrWhiteSpace(d.Id)) continue;
                    string id = d.Id.Trim();
                    if (!seenIds.Add(id)) continue;
                    pkg.CustomMutators.Add(d.ToMetadata(pkg.Manifest.Id));
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    pkg.Manifest.Id,
                    "MUTATOR_JSON_ERROR",
                    $"解析自定义突变词条文件 '{rel}' 失败：{ex.Message}",
                    rel,
                    GateId: 1));
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void LoadLocalizationOverlay(ModPackage pkg, List<ModValidationIssue> issues)
    {
        string locRel = pkg.Manifest.ContentRoots.Localization;
        if (string.IsNullOrWhiteSpace(locRel))
            locRel = "localization/zh-CN.json";

        if (!ModValidator.IsSafeRelativePath(locRel))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                pkg.Manifest.Id,
                "LOCALIZATION_PATH_TRAVERSAL",
                $"本地化文件路径 '{locRel}' 非法。"));
            return;
        }

        string fullPath = Path.Combine(pkg.RootDirectory, locRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return;

        try
        {
            string raw = File.ReadAllText(fullPath, Encoding.UTF8);
            pkg.LocalizationOverlay = JsonSerializer.Deserialize<LocalizationOverlayDocument>(raw, ModJsonOptions);
        }
        catch (Exception ex)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error,
                pkg.Manifest.Id,
                "LOCALIZATION_PARSE_ERROR",
                $"解析 Mod 汉化词典 '{locRel}' 失败：{ex.Message}",
                locRel));
        }
    }

    private static string ResolveContentTarget(string modRoot, string? globOrRel, string fallbackSubdir)
    {
        string raw = string.IsNullOrWhiteSpace(globOrRel) ? fallbackSubdir : globOrRel;
        string normalized = ModValidator.NormalizeLogicalPath(raw);
        int starIdx = normalized.IndexOf('*');
        if (starIdx >= 0)
        {
            int slashIdx = normalized.LastIndexOf('/', starIdx);
            normalized = slashIdx > 0 ? normalized[..slashIdx] : fallbackSubdir;
        }
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = fallbackSubdir;

        return Path.Combine(modRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolveGlobDirectory(string modRoot, string? globOrRel, string fallbackSubdir)
        => ResolveContentTarget(modRoot, globOrRel, fallbackSubdir);

    private static bool IsPathInsideRoot(string candidateFullPath, string rootFullPath)
    {
        string rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        return string.Equals(candidateFullPath, rootFullPath, StringComparison.OrdinalIgnoreCase)
               || candidateFullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
