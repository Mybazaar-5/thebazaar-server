using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Mods;

namespace BazaarServer.Mods;

/// <summary>
/// 社区 Mod 持久化元数据记录（存储于 state/community_mods/index.json）。
/// </summary>
public sealed class CommunityModRecord
{
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Scope { get; set; } = "multiplayer-safe";
    public string UploaderAccountId { get; set; } = string.Empty;
    public string UploaderName { get; set; } = string.Empty;
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PackageSha256 { get; set; } = string.Empty;
    public long ZipBytes { get; set; }
    public int CustomCardCount { get; set; }
    public int CustomEventCount { get; set; }
    public int CustomMonsterCount { get; set; }
    public int CardPatchCount { get; set; }
    public int AssetCount { get; set; }
    public int DownloadCount { get; set; }
}

/// <summary>
/// 面向前端/客户端展示的统一 Mod 列表条目（合并服务器已装 Mod 与社区上传 Mod）。
/// </summary>
public sealed record CommunityModCatalogItem(
    string ModId,
    string Name,
    string Version,
    string Author,
    string Description,
    string Scope,
    string UploaderName,
    DateTimeOffset UpdatedAtUtc,
    string PackageSha256,
    long ZipBytes,
    int CustomCardCount,
    int CustomEventCount,
    int CustomMonsterCount,
    int CardPatchCount,
    int AssetCount,
    int DownloadCount,
    bool IsServerDefault,
    bool IsInCommunityStore,
    bool CanDelete,
    bool CanToggleServerDefault,
    string DownloadUrl);

/// <summary>
/// 锦标赛房间 / 联机对局所需 Mod 的简要描述（供房间成员展示与一键同步下载）。
/// </summary>
public sealed record RequiredModSummary(
    string ModId,
    string Name,
    string Version,
    string Author,
    string PackageSha256,
    int CustomCardCount,
    int CustomEventCount,
    int AssetCount,
    bool AvailableOnServer,
    string DownloadUrl);

/// <summary>
/// 服务端社区 Mod 仓库：
/// - 任何已登录玩家均可上传本地 `.bazaarmod.zip`，经安全解压、10 项静态校验与 25 场实战试跑通过后入库；
/// - 与服务器默认全局 `mods/` 目录严格隔离，避免普通上传污染默认标准卡池；
/// - 支持所有人下载到本地单机玩、在锦标赛/单局中按房选用，以及服主一键「设为全服启用」。
/// </summary>
public sealed class CommunityModStore
{
    public const long MaxCommunityZipBytes = 10L * 1024 * 1024;          // 单包 10 MB
    public const long MaxCommunityUncompressedBytes = 32L * 1024 * 1024; // 解压 32 MB
    public const int MaxCommunityZipEntries = 256;
    public const int MaxUploadsPerWindow = 6;
    public static readonly TimeSpan UploadRateWindow = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".vbs", ".msi", ".so", ".dylib", ".com", ".scr"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly ServerModHost _modHost;
    private readonly string _rootDir;
    private readonly string _indexFilePath;

    private readonly Dictionary<string, CommunityModRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModAssetEntry> _assetsBySha256 = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _uploadHistory = new(StringComparer.OrdinalIgnoreCase);

    public CommunityModStore(ServerModHost modHost, string stateDir)
    {
        ArgumentNullException.ThrowIfNull(modHost);
        _modHost = modHost;
        _rootDir = Path.Combine(stateDir, "community_mods");
        _indexFilePath = Path.Combine(_rootDir, "index.json");
        Directory.CreateDirectory(_rootDir);

        LoadFromDisk();
        _modHost.AttachCommunityStore(this);
    }

    public string RootDirectory => _rootDir;

    /// <summary>返回当前社区 Mod 总数。</summary>
    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>查询全部可用 Mod（合并社区上传与服务器已装 Mod，支持按关键字筛选）。</summary>
    public List<CommunityModCatalogItem> ListCatalog(
        string? requesterAccountId,
        bool isAdmin,
        string? search = null)
    {
        string q = (search ?? string.Empty).Trim();
        var serverStatuses = _modHost.GetAllModStatuses()
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, CommunityModCatalogItem>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            // 1. 先载入社区库记录
            foreach (var rec in _records.Values)
            {
                bool isSrvDefault = serverStatuses.TryGetValue(rec.ModId, out var st) && st.Enabled && st.Loaded;
                bool isOwner = !string.IsNullOrWhiteSpace(requesterAccountId)
                    && string.Equals(rec.UploaderAccountId, requesterAccountId, StringComparison.OrdinalIgnoreCase);
                merged[rec.ModId] = new CommunityModCatalogItem(
                    ModId: rec.ModId,
                    Name: rec.Name,
                    Version: rec.Version,
                    Author: rec.Author,
                    Description: rec.Description,
                    Scope: rec.Scope,
                    UploaderName: string.IsNullOrWhiteSpace(rec.UploaderName) ? rec.Author : rec.UploaderName,
                    UpdatedAtUtc: rec.UpdatedAtUtc,
                    PackageSha256: rec.PackageSha256,
                    ZipBytes: rec.ZipBytes,
                    CustomCardCount: rec.CustomCardCount,
                    CustomEventCount: rec.CustomEventCount,
                    CustomMonsterCount: rec.CustomMonsterCount,
                    CardPatchCount: rec.CardPatchCount,
                    AssetCount: rec.AssetCount,
                    DownloadCount: rec.DownloadCount,
                    IsServerDefault: isSrvDefault,
                    IsInCommunityStore: true,
                    CanDelete: isAdmin || isOwner,
                    CanToggleServerDefault: isAdmin,
                    DownloadUrl: $"/api/community/mods/{Uri.EscapeDataString(rec.ModId)}/download");
            }
        }

        // 2. 合并服务器 mods/ 目录中已存在但未进社区库的 Mod（如官方示例包/服主自建包）
        foreach (var st in serverStatuses.Values)
        {
            if (merged.ContainsKey(st.Id)) continue;
            merged[st.Id] = new CommunityModCatalogItem(
                ModId: st.Id,
                Name: st.Name,
                Version: st.Version,
                Author: st.Author,
                Description: st.Description,
                Scope: st.Scope,
                UploaderName: "服务器自带",
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                PackageSha256: st.PackageHash,
                ZipBytes: 0,
                CustomCardCount: st.CustomCardCount,
                CustomEventCount: st.CustomEventCount,
                CustomMonsterCount: st.CustomMonsterCount,
                CardPatchCount: st.CardPatchCount,
                AssetCount: st.AssetCount,
                DownloadCount: 0,
                IsServerDefault: st.Enabled && st.Loaded,
                IsInCommunityStore: false,
                CanDelete: false,
                CanToggleServerDefault: isAdmin,
                DownloadUrl: $"/api/community/mods/{Uri.EscapeDataString(st.Id)}/download");
        }

        IEnumerable<CommunityModCatalogItem> query = merged.Values;
        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(item =>
                item.ModId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.UploaderName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(i => i.IsServerDefault)
            .ThenByDescending(i => i.UpdatedAtUtc)
            .ThenBy(i => i.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按 Mod ID 查找包（优先服务器已发现包，其次社区库包）。</summary>
    public ModPackage? ResolvePackage(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        string clean = modId.Trim();
        var srvPkg = _modHost.FindDiscoveredPackage(clean);
        if (srvPkg != null) return srvPkg;
        lock (_gate)
        {
            return _packages.TryGetValue(clean, out var pkg) ? pkg : null;
        }
    }

    /// <summary>仅从社区库查找指定 ModPackage。</summary>
    public ModPackage? FindCommunityPackage(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        lock (_gate)
        {
            return _packages.TryGetValue(modId.Trim(), out var pkg) ? pkg : null;
        }
    }

    /// <summary>按 SHA-256 查找社区 Mod 中的 PNG 资产文件（供 /api/mods/assets/{sha256} 复用）。</summary>
    public bool TryResolveAssetBySha256(string sha256, out ModAssetEntry? asset)
    {
        asset = null;
        if (string.IsNullOrWhiteSpace(sha256)) return false;
        lock (_gate)
        {
            return _assetsBySha256.TryGetValue(sha256.Trim(), out asset);
        }
    }

    /// <summary>将房间/对局选中的 Mod ID 列表解析为给成员展示的摘要列表。</summary>
    public List<RequiredModSummary> DescribeRequiredMods(IReadOnlyList<string>? modIds)
    {
        var list = new List<RequiredModSummary>();
        if (modIds == null || modIds.Count == 0) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawId in modIds)
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            string id = rawId.Trim();
            if (!seen.Add(id)) continue;

            var pkg = ResolvePackage(id);
            if (pkg != null)
            {
                list.Add(new RequiredModSummary(
                    ModId: pkg.Manifest.Id,
                    Name: string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? pkg.Manifest.Id : pkg.Manifest.Name,
                    Version: pkg.Manifest.Version,
                    Author: pkg.Manifest.Author,
                    PackageSha256: pkg.PackageHash,
                    CustomCardCount: pkg.Cards.Count,
                    CustomEventCount: pkg.Events.Count,
                    AssetCount: pkg.AssetsByLogicalPath.Count,
                    AvailableOnServer: true,
                    DownloadUrl: $"/api/community/mods/{Uri.EscapeDataString(pkg.Manifest.Id)}/download"));
            }
            else
            {
                list.Add(new RequiredModSummary(
                    ModId: id,
                    Name: id,
                    Version: "-",
                    Author: "-",
                    PackageSha256: string.Empty,
                    CustomCardCount: 0,
                    CustomEventCount: 0,
                    AssetCount: 0,
                    AvailableOnServer: false,
                    DownloadUrl: string.Empty));
            }
        }
        return list;
    }

    /// <summary>
    /// 为一组选中的 Mod ID 构建合并后的客户端同步包（视觉绑定 + 中文文案覆盖 + 资产 URL 列表），
    /// 供锦标赛房间成员一键拉取本房 Mod 的全部贴图与文案。
    /// </summary>
    public object BuildSelectedBundleDto(IReadOnlyList<string> modIds)
    {
        var visualBindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase);
        var cardOverrides = new Dictionary<string, CardLocalizationOverride>(StringComparer.OrdinalIgnoreCase);
        var assets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var resolvedMods = new List<object>();
        var customCards = new List<object>();
        var customEvents = new List<object>();
        var missingModIds = new List<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawId in modIds)
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            string id = rawId.Trim();
            if (!seen.Add(id)) continue;

            var pkg = ResolvePackage(id);
            if (pkg == null)
            {
                missingModIds.Add(id);
                continue;
            }

            resolvedMods.Add(new
            {
                modId = pkg.Manifest.Id,
                name = pkg.Manifest.Name,
                version = pkg.Manifest.Version,
                author = pkg.Manifest.Author,
                packageSha256 = pkg.PackageHash,
                downloadUrl = $"/api/community/mods/{Uri.EscapeDataString(pkg.Manifest.Id)}/download"
            });

            foreach (var (cid, binding) in pkg.VisualBindingsByCustomId)
                visualBindings[cid] = binding;

            if (pkg.LocalizationOverlay?.CardOverrides != null)
            {
                foreach (var (cid, ov) in pkg.LocalizationOverlay.CardOverrides)
                    cardOverrides[cid] = ov;
            }

            foreach (var c in pkg.Cards)
            {
                customCards.Add(new
                {
                    modId = pkg.Manifest.Id,
                    customId = c.CustomId,
                    baseVisualTemplateId = c.BaseVisualTemplateId,
                    type = c.Type,
                    size = c.Size,
                    startingTier = c.StartingTier,
                    titleZh = c.TitleZh,
                    descZh = c.DescZh,
                    customArtFile = c.CustomArtFile,
                });
            }
            foreach (var e in pkg.Events)
            {
                customEvents.Add(new
                {
                    modId = pkg.Manifest.Id,
                    customId = e.EventId,
                    baseVisualTemplateId = e.BaseVisualTemplateId,
                    titleZh = e.TitleZh,
                    descZh = e.DescZh,
                });
            }

            foreach (var a in pkg.AssetsByLogicalPath.Values)
            {
                if (string.IsNullOrWhiteSpace(a.Sha256)) continue;
                assets[a.Sha256] = new
                {
                    modId = pkg.Manifest.Id,
                    logicalPath = a.LogicalPath,
                    sha256 = a.Sha256,
                    bytes = a.Bytes,
                    width = a.Width,
                    height = a.Height,
                    contentType = a.ContentType,
                    url = $"/api/mods/assets/{a.Sha256}",
                };
            }
        }

        return new
        {
            ok = true,
            mods = resolvedMods,
            missingModIds,
            visualBindings,
            cardOverrides,
            customCards,
            customEvents,
            assets = assets.Values.ToList(),
        };
    }

    /// <summary>
    /// 上传本地 `.bazaarmod.zip` 至社区 Mod 库：
    /// 限频 + 限体积 + 防路径穿越/可执行文件 + 10 项校验与 25 场模拟测试 + 归属防覆盖。
    /// </summary>
    public async Task<(bool Ok, int StatusCode, string? Error, CommunityModRecord? Record, ModTenGateValidationReport? Report)> UploadZipAsync(
        byte[] zipBytes,
        string uploaderAccountId,
        string uploaderName,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (zipBytes == null || zipBytes.Length == 0)
            return (false, 400, "上传的文件是空的。", null, null);
        if (zipBytes.Length > MaxCommunityZipBytes)
            return (false, 400, $"压缩包大小为 {zipBytes.Length / 1024.0 / 1024.0:F1} MB，超过 10 MB 上限。", null, null);

        string safeAccount = string.IsNullOrWhiteSpace(uploaderAccountId) ? "guest" : uploaderAccountId.Trim();
        string safeName = string.IsNullOrWhiteSpace(uploaderName) ? "玩家" : uploaderName.Trim();

        if (!isAdmin && !TryConsumeUploadQuota(safeAccount))
            return (false, 429, $"上传太频繁了，每 {UploadRateWindow.TotalMinutes:0} 分钟最多上传 {MaxUploadsPerWindow} 次，请稍后再试。", null, null);

        string tempDir = Path.Combine(_rootDir, $".upload_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var extractErr = SafeExtractZip(zipBytes, tempDir);
            if (extractErr != null)
                return (false, 400, extractErr, null, null);

            string actualModDir = tempDir;
            if (!File.Exists(Path.Combine(actualModDir, "mod.json")))
            {
                var subDirs = Directory.GetDirectories(tempDir);
                if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "mod.json")))
                    actualModDir = subDirs[0];
                else
                    return (false, 400, "压缩包里找不到 mod.json，请确认上传的是正确的 .bazaarmod.zip。", null, null);
            }

            var scanIssues = new List<ModValidationIssue>();
            var pkg = ModLoader.LoadSinglePackage(actualModDir, "community-upload", scanIssues);
            if (pkg == null || !ModValidator.IsValidModId(pkg.Manifest.Id))
            {
                string detail = scanIssues.Count > 0 ? string.Join("；", scanIssues.Take(3).Select(i => i.Message)) : "mod.json 格式不正确";
                return (false, 422, $"Mod 读取失败：{detail}", null, null);
            }

            string modId = pkg.Manifest.Id.Trim();
            if (!isAdmin && string.Equals(modId, "bazaar-chronicles-sample", StringComparison.OrdinalIgnoreCase))
                return (false, 403, "官方示例包名称已被保留，请给你的 Mod 换一个 ID。", null, null);

            // 检查归属权：已存在的社区 Mod 仅原上传者或服主可更新
            lock (_gate)
            {
                if (_records.TryGetValue(modId, out var existing)
                    && !isAdmin
                    && !string.IsNullOrWhiteSpace(existing.UploaderAccountId)
                    && !string.Equals(existing.UploaderAccountId, safeAccount, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, 403, $"Mod「{modId}」已由 {existing.UploaderName} 上传，你无法覆盖他人的作品，请更换 modId 后再上传。", null, null);
                }
            }

            // 执行完整 10 项校验 + 25 场实战模拟
            var peerPackages = CollectValidationPeers(modId);
            var report = await Task.Run(() =>
                ModValidator.RunTenGateValidation(
                    pkg,
                    peerPackages,
                    _modHost.BaseRepository,
                    _modHost.ValidVisualWhitelist,
                    combatMatches: 25,
                    combatSeed: 2026150,
                    runDynamicGates: true), ct).ConfigureAwait(false);

            if (!report.Passed)
            {
                var firstErr = report.Gates
                    .SelectMany(g => g.Issues)
                    .FirstOrDefault(i => i.Severity == ModValidationSeverity.Error)?.Message;
                string msg = string.IsNullOrWhiteSpace(firstErr)
                    ? $"Mod 校验未通过（共 {report.ErrorCount} 个错误），请修复后再上传。"
                    : $"Mod 校验未通过（{report.ErrorCount} 个错误）：{firstErr}";
                return (false, 422, msg, null, report);
            }

            // 安全落盘：extracted/ + 重新规范打包 package.bazaarmod.zip
            string modSlotDir = ResolveSafeSlotDir(modId);
            string stagingDir = Path.Combine(_rootDir, $".stage_{Guid.NewGuid():N}");
            string stagingExtracted = Path.Combine(stagingDir, "extracted");
            CopyDirectoryRecursive(actualModDir, stagingExtracted);

            byte[] cleanZipBytes = PackDirectoryToZipBytes(stagingExtracted, modId);
            string zipSha256 = Convert.ToHexString(SHA256.HashData(cleanZipBytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(stagingDir, "package.bazaarmod.zip"), cleanZipBytes);

            // 重新加载落盘后的包，确保资产路径指向最终目录
            lock (_gate)
            {
                if (Directory.Exists(modSlotDir))
                    Directory.Delete(modSlotDir, recursive: true);
                Directory.Move(stagingDir, modSlotDir);

                string finalExtracted = Path.Combine(modSlotDir, "extracted");
                var reloadIssues = new List<ModValidationIssue>();
                var finalPkg = ModLoader.LoadSinglePackage(finalExtracted, "community", reloadIssues);
                if (finalPkg == null)
                    return (false, 500, "保存 Mod 后重新加载失败。", null, report);

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool existed = _records.TryGetValue(modId, out var prev);
                var rec = new CommunityModRecord
                {
                    ModId = modId,
                    Name = string.IsNullOrWhiteSpace(finalPkg.Manifest.Name) ? modId : finalPkg.Manifest.Name.Trim(),
                    Version = string.IsNullOrWhiteSpace(finalPkg.Manifest.Version) ? "1.0.0" : finalPkg.Manifest.Version.Trim(),
                    Author = string.IsNullOrWhiteSpace(finalPkg.Manifest.Author) ? safeName : finalPkg.Manifest.Author.Trim(),
                    Description = finalPkg.Manifest.Description?.Trim() ?? string.Empty,
                    Scope = string.IsNullOrWhiteSpace(finalPkg.Manifest.Scope) ? "multiplayer-safe" : finalPkg.Manifest.Scope.Trim(),
                    UploaderAccountId = existed && !string.IsNullOrWhiteSpace(prev!.UploaderAccountId) ? prev.UploaderAccountId : safeAccount,
                    UploaderName = existed && !string.IsNullOrWhiteSpace(prev!.UploaderName) && isAdmin ? prev.UploaderName : safeName,
                    UploadedAtUtc = existed ? prev!.UploadedAtUtc : now,
                    UpdatedAtUtc = now,
                    PackageSha256 = zipSha256,
                    ZipBytes = cleanZipBytes.LongLength,
                    CustomCardCount = finalPkg.Cards.Count,
                    CustomEventCount = finalPkg.Events.Count,
                    CustomMonsterCount = finalPkg.Monsters.Count,
                    CardPatchCount = finalPkg.Patches.Count,
                    AssetCount = finalPkg.AssetsByLogicalPath.Count,
                    DownloadCount = existed ? prev!.DownloadCount : 0,
                };

                _records[modId] = rec;
                _packages[modId] = finalPkg;
                RebuildAssetIndexLocked();
                SaveIndexLocked();
                _modHost.InvalidateRunModContextCache();

                return (true, 200, null, rec, report);
            }
        }
        catch (InvalidDataException)
        {
            return (false, 400, "上传的文件不是有效的 ZIP 压缩包。", null, null);
        }
        finally
        {
            TryDeleteDirQuietly(tempDir);
        }
    }

    /// <summary>获取可供下载的 `.bazaarmod.zip`（同时支持社区 Mod 与服务器自带 Mod）。</summary>
    public bool TryGetDownloadZip(string modId, out byte[] zipBytes, out string fileName, out string? error)
    {
        zipBytes = Array.Empty<byte>();
        fileName = string.Empty;
        error = null;

        if (!ModValidator.IsValidModId(modId))
        {
            error = "Mod ID 格式不正确。";
            return false;
        }

        string clean = modId.Trim();
        lock (_gate)
        {
            if (_records.TryGetValue(clean, out var rec))
            {
                string zipPath = Path.Combine(ResolveSafeSlotDir(clean), "package.bazaarmod.zip");
                if (File.Exists(zipPath))
                {
                    zipBytes = File.ReadAllBytes(zipPath);
                    fileName = $"{clean}-{rec.Version}.bazaarmod.zip";
                    rec.DownloadCount++;
                    SaveIndexLocked();
                    return true;
                }
            }
        }

        // 回退打包服务器 mods/ 目录里的对应包
        var srvPkg = _modHost.FindDiscoveredPackage(clean);
        if (srvPkg != null && !string.IsNullOrWhiteSpace(srvPkg.RootDirectory) && Directory.Exists(srvPkg.RootDirectory))
        {
            zipBytes = PackDirectoryToZipBytes(srvPkg.RootDirectory, clean);
            fileName = $"{clean}-{srvPkg.Manifest.Version}.bazaarmod.zip";
            return true;
        }

        error = $"未找到可下载的 Mod：{clean}";
        return false;
    }

    /// <summary>删除社区 Mod（仅原上传者或服主可执行）。</summary>
    public (bool Ok, int StatusCode, string? Error) DeleteCommunityMod(string modId, string requesterAccountId, bool isAdmin)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, 400, "Mod ID 格式不正确。");

        string clean = modId.Trim();
        lock (_gate)
        {
            if (!_records.TryGetValue(clean, out var existing))
                return (false, 404, $"社区库中未找到 Mod：{clean}");

            bool isOwner = !string.IsNullOrWhiteSpace(requesterAccountId)
                && string.Equals(existing.UploaderAccountId, requesterAccountId, StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && !isOwner)
                return (false, 403, "只有上传者本人或服主可以删除这个 Mod。");

            _records.Remove(clean);
            _packages.Remove(clean);
            TryDeleteDirQuietly(ResolveSafeSlotDir(clean));
            RebuildAssetIndexLocked();
            SaveIndexLocked();
            _modHost.InvalidateRunModContextCache();
            return (true, 200, null);
        }
    }

    /// <summary>服主操作：将任意社区 Mod 设为全服默认启用（安装到 mods/ 并热重载）或取消全服默认。</summary>
    public (bool Ok, int StatusCode, string? Error, bool Enabled) SetServerDefault(string modId, bool enabled)
    {
        if (!ModValidator.IsValidModId(modId))
            return (false, 400, "Mod ID 格式不正确。", false);

        string clean = modId.Trim();
        if (enabled)
        {
            string? srcExtracted = null;
            lock (_gate)
            {
                if (_records.ContainsKey(clean))
                {
                    string candidate = Path.Combine(ResolveSafeSlotDir(clean), "extracted");
                    if (Directory.Exists(candidate))
                        srcExtracted = candidate;
                }
            }

            if (srcExtracted != null)
            {
                string serverModDir = Path.GetFullPath(Path.Combine(_modHost.ModsRootDirectory, clean));
                string guard = Path.GetFullPath(_modHost.ModsRootDirectory) + Path.DirectorySeparatorChar;
                if (!serverModDir.StartsWith(guard, StringComparison.OrdinalIgnoreCase))
                    return (false, 400, "目标路径非法。", false);

                if (Directory.Exists(serverModDir))
                    Directory.Delete(serverModDir, recursive: true);
                CopyDirectoryRecursive(srcExtracted, serverModDir);
                _modHost.Reload();
            }

            var (found, isEnabled, _) = _modHost.ToggleMod(clean, enabled: true);
            if (!found)
                return (false, 404, $"未找到可启用的 Mod：{clean}", false);
            return (true, 200, null, isEnabled);
        }
        else
        {
            var (found, isEnabled, _) = _modHost.ToggleMod(clean, enabled: false);
            if (!found)
                return (true, 200, null, false);
            return (true, 200, null, isEnabled);
        }
    }

    // ── 内部方法 ──────────────────────────────────────────────────────────────

    private IReadOnlyList<ModPackage> CollectValidationPeers(string excludeModId)
    {
        var peers = new List<ModPackage>();
        foreach (var p in _modHost.DiscoveredPackages)
        {
            if (!string.Equals(p.Manifest.Id, excludeModId, StringComparison.OrdinalIgnoreCase))
                peers.Add(p);
        }
        return peers;
    }

    private bool TryConsumeUploadQuota(string accountKey)
    {
        var now = DateTimeOffset.UtcNow;
        var list = _uploadHistory.GetOrAdd(accountKey, _ => new List<DateTimeOffset>());
        lock (list)
        {
            list.RemoveAll(t => now - t > UploadRateWindow);
            if (list.Count >= MaxUploadsPerWindow)
                return false;
            list.Add(now);
            return true;
        }
    }

    private string ResolveSafeSlotDir(string modId)
    {
        string full = Path.GetFullPath(Path.Combine(_rootDir, modId));
        string guard = Path.GetFullPath(_rootDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(guard, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"非法目录路径：{modId}");
        return full;
    }

    private void LoadFromDisk()
    {
        lock (_gate)
        {
            _records.Clear();
            _packages.Clear();
            _assetsBySha256.Clear();

            if (!File.Exists(_indexFilePath))
                return;

            try
            {
                string raw = File.ReadAllText(_indexFilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<CommunityModRecord>>(raw, JsonOpts);
                if (list == null) return;

                foreach (var rec in list)
                {
                    if (string.IsNullOrWhiteSpace(rec.ModId) || !ModValidator.IsValidModId(rec.ModId))
                        continue;
                    string extractedDir = Path.Combine(ResolveSafeSlotDir(rec.ModId), "extracted");
                    if (!Directory.Exists(extractedDir))
                        continue;

                    var issues = new List<ModValidationIssue>();
                    var pkg = ModLoader.LoadSinglePackage(extractedDir, "community", issues);
                    if (pkg == null)
                        continue;

                    _records[rec.ModId] = rec;
                    _packages[rec.ModId] = pkg;
                }

                RebuildAssetIndexLocked();
                Console.WriteLine($"[CommunityModStore] 已加载社区 Mod {_records.Count} 个，资产 {_assetsBySha256.Count} 个。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommunityModStore] 加载 index.json 失败：{ex.Message}");
            }
        }
    }

    private void RebuildAssetIndexLocked()
    {
        _assetsBySha256.Clear();
        foreach (var pkg in _packages.Values)
        {
            foreach (var asset in pkg.AssetsByLogicalPath.Values)
            {
                if (!string.IsNullOrWhiteSpace(asset.Sha256))
                    _assetsBySha256[asset.Sha256] = asset;
            }
        }
    }

    private void SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            string tmp = _indexFilePath + ".tmp";
            string json = JsonSerializer.Serialize(_records.Values.OrderBy(r => r.ModId, StringComparer.OrdinalIgnoreCase).ToList(), JsonOpts);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _indexFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CommunityModStore] 持久化 index.json 失败：{ex.Message}");
        }
    }

    private static string? SafeExtractZip(byte[] zipBytes, string destRoot)
    {
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0)
            return "压缩包为空，里面没有文件。";
        if (archive.Entries.Count > MaxCommunityZipEntries)
            return $"压缩包文件数量过多（{archive.Entries.Count} > {MaxCommunityZipEntries}）。";

        long totalUncompressed = 0;
        string rootGuard = Path.GetFullPath(destRoot) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;

            string rel = ModValidator.NormalizeLogicalPath(entry.FullName);
            if (!ModValidator.IsSafeRelativePath(rel))
                return $"压缩包包含非法路径条目：{entry.FullName}";

            string ext = Path.GetExtension(rel);
            if (ForbiddenExtensions.Contains(ext))
                return $"出于安全限制，不允许上传可执行或脚本文件：{rel}";

            totalUncompressed += entry.Length;
            if (totalUncompressed > MaxCommunityUncompressedBytes)
                return "压缩包解压后体积超过 32 MB 上限。";

            string destFile = Path.GetFullPath(Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!destFile.StartsWith(rootGuard, StringComparison.OrdinalIgnoreCase))
                return $"目标解压路径越界：{entry.FullName}";

            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            using var src = entry.Open();
            using var dst = File.Create(destFile);
            long copied = 0;
            var buf = new byte[16384];
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                copied += read;
                if (copied > MaxCommunityUncompressedBytes)
                    return "压缩包单文件解压体积超过 32 MB 上限。";
                dst.Write(buf, 0, read);
            }
        }
        return null;
    }

    public static byte[] PackDirectoryToZipBytes(string sourceDir, string modId)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains("/__pycache__/", StringComparison.OrdinalIgnoreCase))
                    continue;
                string ext = Path.GetExtension(rel);
                if (ForbiddenExtensions.Contains(ext))
                    continue;

                var entry = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, rel));
        }
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    private static void TryDeleteDirQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
