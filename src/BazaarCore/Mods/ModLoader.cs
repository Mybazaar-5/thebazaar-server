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
        ".png", ".webp"
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

        // 1. 扫描客户端资产目录（仅接收 PNG/WebP，并读取魔数与宽高供 ModValidator 统一校验）
        ScanAssets(pkg, issues);

        // 2. 加载自定义卡牌、事件、可选独立野怪与自定义模式（支持目录、通配符或精确单文件配置，并自动解包顶层数组包裹对象）
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Cards, "cards"), pkg.Cards, "CARD_JSON_ERROR", "cards", issues);
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Events, "events"), pkg.Events, "EVENT_JSON_ERROR", "events", issues);
        LoadJsonEntries(pkg, Path.Combine(fullDir, "monsters"), pkg.Monsters, "MONSTER_JSON_ERROR", "monsters", issues);
        LoadJsonEntries(pkg, ResolveContentTarget(fullDir, manifest.ContentRoots.Modes, "modes"), pkg.Modes, "MODE_JSON_ERROR", "modes", issues, skipMutatorOnlyFiles: true);

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

        void RegisterBinding(CustomCardDefinition card)
        {
            if (string.IsNullOrWhiteSpace(card.CustomId)) return;
            string cid = card.CustomId.Trim();
            string? artSha256 = null;
            string? normalizedArt = null;

            if (!string.IsNullOrWhiteSpace(card.CustomArtFile))
            {
                normalizedArt = ModValidator.NormalizeLogicalPath(card.CustomArtFile);
                if (pkg.AssetsByLogicalPath.TryGetValue(normalizedArt, out var asset))
                    artSha256 = asset.Sha256;
            }

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
                CustomArtFile = normalizedArt,
                CustomArtSha256 = artSha256,
                ArtRect = card.ArtRect != null
                    ? new CustomCardArtRect { Width = card.ArtRect.Width, Height = card.ArtRect.Height, CropMode = card.ArtRect.CropMode }
                    : null,
                Tags = new List<string>(card.Tags),
                AccentColor = card.AccentColor
            };
        }

        for (int i = 0; i < pkg.Cards.Count; i++)
            RegisterBinding(pkg.Cards[i]);

        for (int i = 0; i < pkg.Events.Count; i++)
        {
            var ev = pkg.Events[i];
            RegisterBinding(ev.ToCardDefinition());
            for (int j = 0; j < ev.Steps.Count; j++)
                RegisterBinding(ev.Steps[j]);
        }
    }

    /// <summary>
    /// 计算单个 ModPackage 的确定性 SHA-256 指纹（用于热重载变化检测与客户端资产同步校验）。
    /// </summary>
    public static string ComputePackageHash(ModPackage pkg)
    {
        var sb = new StringBuilder(1024);
        sb.Append(pkg.Manifest.Id).Append('|')
          .Append(pkg.Manifest.Version).Append('|')
          .Append(pkg.Manifest.Scope).Append('|')
          .Append(pkg.Manifest.AffectsGhostPool).Append('\n');

        foreach (var card in pkg.Cards.OrderBy(c => c.CustomId, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("C:").Append(card.CustomId).Append('|')
              .Append(card.BaseVisualTemplateId).Append('|')
              .Append(card.Type).Append('|')
              .Append(card.Size).Append('|')
              .Append(card.TitleZh).Append('|')
              .Append(card.DescZh).Append('|')
              .Append(card.CustomArtFile ?? string.Empty).Append('\n');
        }

        foreach (var ev in pkg.Events.OrderBy(e => e.EventId, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("E:").Append(ev.EventId).Append('|')
              .Append(ev.BaseVisualTemplateId).Append('|')
              .Append(ev.TitleZh).Append('|')
              .Append(ev.Steps.Count).Append('\n');
        }

        foreach (var mon in pkg.Monsters.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            double hp = mon.Player?.Attributes.GetValueOrDefault("HealthMax", 0) ?? 0;
            sb.Append("M:").Append(mon.Id).Append('|').Append(mon.InternalName).Append('|').Append(hp).Append('\n');
        }

        foreach (var mode in pkg.Modes.OrderBy(m => m.ModeId, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("G:").Append(mode.ModeId).Append('|')
              .Append(mode.BasePreset).Append('|')
              .Append(mode.BaseSpecialMode).Append('\n');
        }

        foreach (var kv in pkg.AssetsByLogicalPath.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("A:").Append(kv.Key).Append('=').Append(kv.Value.Sha256).Append('\n');
        }

        if (pkg.LocalizationOverlay != null)
        {
            string locJson = JsonSerializer.Serialize(pkg.LocalizationOverlay, ModJsonOptions);
            sb.Append("L:").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(locJson))).ToLowerInvariant()).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
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
            if (!SupportedImageExtensions.Contains(ext))
                continue;

            string relPath = ModValidator.NormalizeLogicalPath(Path.GetRelativePath(fullModRoot, fullFile));
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
                        ContentType = ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/png"
                    };
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(fullFile);
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                bool headerOk = ModValidator.TryValidateImageHeader(bytes, out int w, out int h, out string contentType);
                if (!headerOk)
                {
                    issues.Add(new ModValidationIssue(
                        ModValidationSeverity.Error,
                        pkg.Manifest.Id,
                        "INVALID_ASSET_IMAGE_HEADER",
                        $"资产文件 '{relPath}' 不是合法的 PNG 或 WebP 图像（魔数头校验失败）。",
                        relPath));
                }

                pkg.AssetsByLogicalPath[relPath] = new ModAssetEntry
                {
                    ModId = pkg.Manifest.Id,
                    LogicalPath = relPath,
                    FullFilePath = fullFile,
                    Sha256 = sha,
                    Bytes = bytes.LongLength,
                    Width = headerOk ? w : 0,
                    Height = headerOk ? h : 0,
                    ContentType = headerOk ? contentType : "application/octet-stream"
                };
            }
            catch (Exception ex)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    pkg.Manifest.Id,
                    "ASSET_READ_ERROR",
                    $"读取资产文件 '{relPath}' 失败：{ex.Message}",
                    relPath));
            }
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
