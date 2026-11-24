using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Modules;
using BazaarModSdk;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher.Core;

/// <summary>
/// 社区 Mod 列表拉取、锦标赛按房 Mod 资源同步、下载到本地以及本地打包上传扩展。
/// </summary>
internal sealed partial class ModContentSyncClient
{
    internal sealed class CommunityModEntry
    {
        public string ModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string UploaderName { get; set; } = string.Empty;
        public long ZipBytes { get; set; }
        public int CustomCardCount { get; set; }
        public int CustomEventCount { get; set; }
        public int AssetCount { get; set; }
        public int DownloadCount { get; set; }
        public bool IsServerDefault { get; set; }
        public bool IsInCommunityStore { get; set; }
        public bool CanDelete { get; set; }
        public bool CanToggleServerDefault { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;
    }

    internal sealed class LocalModSource
    {
        public string ModId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsZipFile { get; set; }
    }

    private List<CommunityModEntry> _communityMods = new();
    private string _lastSyncedRoomModKey = string.Empty;

    internal IReadOnlyList<CommunityModEntry> GetCommunityModsSnapshot()
    {
        lock (_sync)
        {
            return _communityMods.ToArray();
        }
    }

    internal static string LocalModsDownloadRoot
    {
        get
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath ?? ".", "mods");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    internal static string LocalZipArchiveRoot
    {
        get
        {
            string dir = Path.Combine(ModuleHost.CacheDirectory, "downloads");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>切换服务器后先取消旧任务、按旧服 URL 删除旧磁盘缓存、递增同步代数，再以新服 URL 立即重新拉取清单（P0-29）。</summary>
    internal void OnServerChanged(string oldServerBase = null)
    {
        // 1. 先递增代数并取消旧在途同步任务，防止旧服异步回调反写污染新服
        CancellationTokenSource oldCts;
        lock (_sync)
        {
            Interlocked.Increment(ref _syncGeneration);
            Interlocked.Increment(ref _roomBundleSyncEpoch);
            oldCts = _serverSyncCts;
            _serverSyncCts = null;
        }
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            try { oldCts.Dispose(); } catch { }
        }

        ClearSyncedServerMods();
        try { LocalizationOverlayModule.Instance?.ClearServerOverlayLayer(); } catch { }

        // 2. 精确按旧服务器 URL 删旧服 overlay 缓存（修复先改 URL 后误删新服缓存的 Bug）
        try
        {
            if (!string.IsNullOrWhiteSpace(oldServerBase))
            {
                string oldOverlayPath = GetOverlayCachePathForUrl(oldServerBase);
                if (File.Exists(oldOverlayPath)) File.Delete(oldOverlayPath);
            }
        }
        catch { }

        lock (_sync)
        {
            _communityMods = new List<CommunityModEntry>();
            _lastModSetHash = string.Empty;
            _lastOverlaySha256 = string.Empty;
            _lastVisualBindingsRev = string.Empty;
            _lastSyncedRoomModKey = string.Empty;
        }
        TriggerFullSyncInBackground(forceDownload: false);
    }

    private static void NotifyVisualAndLocaleChanged(string reason)
    {
        try
        {
            var visMod = CustomContentVisualModule.Instance;
            visMod?.InvalidateVisualCache(null);
            if (visMod != null)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    try { visMod.ReapplyActiveVisualsOnMainThread(); } catch { }
                });
            }
        }
        catch { }

        try
        {
            LocalizationOverlayModule.Instance?.NotifyLocaleReloaded(reason);
        }
        catch { }
    }

    /// <summary>卸载当前已注册的所有服务器/房间/回放同步 Mod 视觉与文案覆盖并触发缓存刷新。</summary>
    internal void ClearSyncedServerMods()
    {
        lock (_sync)
        {
            DisposeHandlesLocked(_syncedVisualHandles);
            DisposeHandlesLocked(_syncedActiveBindingHandles);
            DisposeHandlesLocked(_syncedReplayBindingHandles);
            DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
            DisposeHandlesLocked(_syncedRoomBundleTextHandles);
            DisposeHandlesLocked(_syncedCardTextHandles);
            _serverMods = new List<ServerModEntry>();
            _customContents = new List<ServerCustomContentItem>();
            _globalDonorTemplateIds.Clear();
            _roomDonorTemplateIds.Clear();
            _activeDonorTemplateIds.Clear();
            RebuildDonorTemplateIdsLocked();
            _lastSyncedRoomModKey = string.Empty;
            _lastVisualBindingsRev = string.Empty;
        }
        NotifyVisualAndLocaleChanged("clear-server-mods");
    }

    /// <summary>离房或房主清空 Mod 时递增 syncEpoch、卸载房间专属 Mod 句柄、重建 _donorTemplateIds 并刷新视觉/文本缓存（D6-P1-08）。</summary>
    internal void ClearSyncedRoomBundleMods()
    {
        bool hadAny;
        lock (_sync)
        {
            Interlocked.Increment(ref _roomBundleSyncEpoch);
            hadAny = _syncedRoomBundleVisualHandles.Count > 0
                || _syncedRoomBundleTextHandles.Count > 0
                || _roomDonorTemplateIds.Count > 0
                || !string.IsNullOrEmpty(_lastSyncedRoomModKey);
            DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
            DisposeHandlesLocked(_syncedRoomBundleTextHandles);
            _roomDonorTemplateIds.Clear();
            RebuildDonorTemplateIdsLocked();
            _lastSyncedRoomModKey = string.Empty;
        }
        if (hadAny)
        {
            NotifyVisualAndLocaleChanged("clear-room-mods");
        }
    }

    /// <summary>拉取当前服务器的全部社区 + 服务器 Mod 列表。</summary>
    internal async Task<(bool Ok, List<CommunityModEntry> Mods, bool IsAdmin, string Error)> FetchCommunityModsAsync(
        string search = null,
        CancellationToken ct = default)
    {
        string path = string.IsNullOrWhiteSpace(search)
            ? "/api/community/mods"
            : "/api/community/mods?search=" + Uri.EscapeDataString(search.Trim());

        var resp = await NameAuth.GetJsonAsync(path, ct).ConfigureAwait(false);
        if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
        {
            return (false, new List<CommunityModEntry>(), false, resp.Error ?? "无法连接服务器");
        }

        try
        {
            var root = JObject.Parse(resp.Body);
            bool isAdmin = (bool?)root["isAdmin"] ?? false;
            var list = new List<CommunityModEntry>();
            if (root["mods"] is JArray arr)
            {
                foreach (var token in arr)
                {
                    if (token is not JObject mo) continue;
                    string id = (string)(mo["modId"] ?? mo["id"]) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    list.Add(new CommunityModEntry
                    {
                        ModId = id,
                        Name = (string)(mo["name"] ?? id) ?? id,
                        Version = (string)mo["version"] ?? "1.0.0",
                        Author = (string)mo["author"] ?? string.Empty,
                        Description = (string)mo["description"] ?? string.Empty,
                        UploaderName = (string)mo["uploaderName"] ?? string.Empty,
                        ZipBytes = (long?)mo["zipBytes"] ?? 0L,
                        CustomCardCount = (int?)mo["customCardCount"] ?? 0,
                        CustomEventCount = (int?)mo["customEventCount"] ?? 0,
                        AssetCount = (int?)mo["assetCount"] ?? 0,
                        DownloadCount = (int?)mo["downloadCount"] ?? 0,
                        IsServerDefault = (bool?)mo["isServerDefault"] ?? false,
                        IsInCommunityStore = (bool?)mo["isInCommunityStore"] ?? false,
                        CanDelete = (bool?)mo["canDelete"] ?? false,
                        CanToggleServerDefault = (bool?)mo["canToggleServerDefault"] ?? false,
                        DownloadUrl = (string)mo["downloadUrl"] ?? string.Empty,
                    });
                }
            }

            lock (_sync)
            {
                _communityMods = list;
            }
            return (true, list, isAdmin, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, new List<CommunityModEntry>(), false, "解析社区 Mod 列表失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 为锦标赛房间或本局选中的 Mod 列表同步全部卡图 PNG、视觉绑定与中文提示（无需全服启用也能完整显示）。
    /// </summary>
    internal async Task<(bool Ok, string Summary)> SyncSelectedModBundleAsync(
        IReadOnlyList<string> modIds,
        bool force = false,
        CancellationToken ct = default)
    {
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
        {
            return (false, "你已关闭「允许接收服务器下发的 Mod」，请先在服务器设置中开启。");
        }

        if (modIds == null || modIds.Count == 0)
        {
            ClearSyncedRoomBundleMods();
            return (true, "本房未使用额外 Mod");
        }

        var cleanIds = modIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string key = NameAuth.GetServerBaseUrl() + "|" + string.Join(",", cleanIds);
        if (!force && string.Equals(_lastSyncedRoomModKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"本房 {cleanIds.Count} 个 Mod 已同步");
        }

        // D6-P1-08：单调 syncEpoch 防止退房或切房后旧异步请求乱序反写房间 Mod
        long myEpoch = Interlocked.Increment(ref _roomBundleSyncEpoch);
        long myGen = Volatile.Read(ref _syncGeneration);

        string path = "/api/community/mods/bundle?modIds=" + Uri.EscapeDataString(string.Join(",", cleanIds));
        var resp = await NameAuth.GetJsonAsync(path, ct).ConfigureAwait(false);
        if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
        {
            return (false, "拉取房间 Mod 资源失败：" + (resp.Error ?? "网络错误"));
        }
        if (!IsRoomBundleEpochCurrent(myEpoch) || !IsSyncGenerationCurrent(myGen))
        {
            return (true, "房间状态已变更，已忽略过期 Mod 同步结果");
        }

        try
        {
            var root = JObject.Parse(resp.Body);
            var shaToLocalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int pngCount = 0;
            if (root["assets"] is JArray assetsArr)
            {
                foreach (var token in assetsArr)
                {
                    if (!IsRoomBundleEpochCurrent(myEpoch) || !IsSyncGenerationCurrent(myGen))
                        return (true, "房间状态已变更，已中止过期 Mod 资产下载");
                    if (token is not JObject ao) continue;
                    string sha = NormalizeSha((string)(ao["sha256"] ?? ao["hash"]));
                    if (string.IsNullOrEmpty(sha)) continue;
                    string localPath = await EnsureAssetCachedAsync(sha, force, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        shaToLocalPath[sha] = localPath;
                        pngCount++;
                    }
                }
            }

            var dummyContents = new List<ServerCustomContentItem>();
            var visualBindings = new List<CardVisualBinding>();
            var textOverrides = new List<CardTextOverride>();

            if (root["visualBindings"] is JObject bindingsMap)
            {
                foreach (var prop in bindingsMap.Properties())
                {
                    if (prop.Value is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: false, shaToLocalPath, dummyContents, visualBindings, textOverrides, force, ct).ConfigureAwait(false);
                }
            }

            // P0-18：纯文本 cardOverrides 优先级设为 90（低于 visualBindings 100），并由字段级合并保障绝不反杀元数据
            if (root["cardOverrides"] is JObject overridesMap)
            {
                foreach (var prop in overridesMap.Properties())
                {
                    if (prop.Value is not JObject co) continue;
                    string cid = ((string)(co["customId"] ?? co["customCardId"] ?? co["id"]))?.Trim();
                    if (string.IsNullOrWhiteSpace(cid)) cid = prop.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(cid)) continue;

                    string titleZh = (string)(co["titleZh"] ?? co["title"] ?? co["nameZh"]);
                    string titleEn = (string)(co["titleEn"] ?? co["nameEn"]);
                    string descZh = (string)(co["descriptionZh"] ?? co["description"] ?? co["descZh"]);
                    string descEn = (string)(co["descriptionEn"] ?? co["descEn"]);
                    string[] activeTips = ReadStringArray(co["activeTooltipsZh"] ?? co["activeTooltips"]);
                    string[] activeTipsEn = ReadStringArray(co["activeTooltipsEn"]);
                    string[] passiveTips = ReadStringArray(co["passiveTooltipsZh"] ?? co["passiveTooltips"]);
                    string[] passiveTipsEn = ReadStringArray(co["passiveTooltipsEn"]);
                    string[] extraTags = ReadStringArray(co["tags"] ?? co["extraDisplayTags"] ?? co["displayTags"]);
                    Dictionary<string, string[]> enchTips = ParseEnchantTooltipsMap((co["enchantTooltipsZh"] ?? co["enchantmentTooltips"]) as JObject);
                    Dictionary<string, string[]> enchTipsEn = ParseEnchantTooltipsMap(co["enchantTooltipsEn"] as JObject);

                    if (!string.IsNullOrWhiteSpace(titleZh) || !string.IsNullOrWhiteSpace(titleEn)
                        || !string.IsNullOrWhiteSpace(descZh) || !string.IsNullOrWhiteSpace(descEn)
                        || (activeTips != null && activeTips.Length > 0)
                        || (activeTipsEn != null && activeTipsEn.Length > 0)
                        || (passiveTips != null && passiveTips.Length > 0)
                        || (passiveTipsEn != null && passiveTipsEn.Length > 0)
                        || (enchTips != null && enchTips.Count > 0)
                        || (enchTipsEn != null && enchTipsEn.Count > 0)
                        || (extraTags != null && extraTags.Length > 0))
                    {
                        textOverrides.Add(new CardTextOverride
                        {
                            ModId = (string)(co["modId"] ?? co["ModId"]) ?? "room-mod",
                            CustomCardId = cid,
                            TemplateId = Guid.TryParse(cid, out _) ? cid : null,
                            Locale = "zh-CN",
                            Title = string.IsNullOrWhiteSpace(titleZh) ? null : titleZh,
                            TitleEn = string.IsNullOrWhiteSpace(titleEn) ? null : titleEn,
                            Description = string.IsNullOrWhiteSpace(descZh) ? null : descZh,
                            DescriptionEn = string.IsNullOrWhiteSpace(descEn) ? null : descEn,
                            ActiveTooltips = activeTips,
                            ActiveTooltipsEn = activeTipsEn,
                            PassiveTooltips = passiveTips,
                            PassiveTooltipsEn = passiveTipsEn,
                            EnchantmentTooltips = enchTips,
                            EnchantmentTooltipsEn = enchTipsEn,
                            ExtraDisplayTags = extraTags,
                            Priority = 90,
                        });
                    }
                }
            }

            if (!IsRoomBundleEpochCurrent(myEpoch) || !IsSyncGenerationCurrent(myGen))
            {
                return (true, "房间状态已变更，已放弃提交过期房间 Mod 句柄");
            }

            // 合并写入独立句柄组，不覆盖全局 Mod 句柄
            var visMod = CustomContentVisualModule.Instance;
            var l10nMod = LocalizationOverlayModule.Instance;
            var newVisHandles = new List<IDisposable>();
            var newTextHandles = new List<IDisposable>();
            if (visMod != null)
            {
                foreach (var vb in visualBindings)
                {
                    var h = visMod.RegisterVisualBinding(vb);
                    if (h != null) newVisHandles.Add(h);
                }
            }
            if (l10nMod != null)
            {
                foreach (var to in textOverrides)
                {
                    var h = l10nMod.RegisterCardTextOverride(to);
                    if (h != null) newTextHandles.Add(h);
                }
            }

            lock (_sync)
            {
                if (!IsRoomBundleEpochCurrent(myEpoch) || !IsSyncGenerationCurrent(myGen))
                {
                    DisposeHandlesLocked(newVisHandles);
                    DisposeHandlesLocked(newTextHandles);
                    return (true, "房间状态已变更，已回收过期 Mod 句柄");
                }

                DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
                DisposeHandlesLocked(_syncedRoomBundleTextHandles);
                _syncedRoomBundleVisualHandles.AddRange(newVisHandles);
                _syncedRoomBundleTextHandles.AddRange(newTextHandles);
                var roomDonors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in dummyContents)
                {
                    if (!string.IsNullOrWhiteSpace(item.BaseVisualTemplateId))
                        roomDonors.Add(item.BaseVisualTemplateId.Trim());
                }
                _roomDonorTemplateIds = roomDonors;
                RebuildDonorTemplateIdsLocked();
                _lastSyncedRoomModKey = key;
            }

            // 立即失效旧视觉缓存并在主线程重绘，同时刷新本地化缓存
            NotifyVisualAndLocaleChanged("room-mod-bundle");

            int missing = (root["missingModIds"] as JArray)?.Count ?? 0;
            string note = missing > 0 ? $"（其中 {missing} 个在服务器未找到）" : string.Empty;
            return (true, $"已同步本房 {cleanIds.Count} 个 Mod（贴图 {pngCount} 张，视觉 {visualBindings.Count} 条，文案 {textOverrides.Count} 条）{note}");
        }
        catch (Exception ex)
        {
            return (false, "解析房间 Mod 包失败：" + ex.Message);
        }
    }

    /// <summary>从当前服务器下载指定 Mod 的 .bazaarmod.zip 并解压到本地 BepInEx/mods/<modId> 供离线/单机使用。</summary>
    internal async Task<(bool Ok, string Message, string SavedZipPath, string ExtractedDir)> DownloadModToLocalAsync(
        string modId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return (false, "Mod ID 不能为空", string.Empty, string.Empty);

        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "尚未设置服务器地址", string.Empty, string.Empty);

        string cleanId = modId.Trim();
        if (!SafePathAndZipGuard.IsValidModId(cleanId))
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 拦截非法社区 Mod ID 下载请求：{cleanId}");
            return (false, $"非法的 Mod ID（包含不允许的路径字符或保留名）：{cleanId}", string.Empty, string.Empty);
        }

        try
        {
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/community/mods/{Uri.EscapeDataString(cleanId)}/download");
            NameAuth.ApplyAuthHeaders(req);
            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"下载失败（HTTP {(int)resp.StatusCode}）", string.Empty, string.Empty);
            }

            byte[] zipBytes = await SafePathAndZipGuard.ReadHttpContentWithLimitAsync(
                resp.Content,
                SafePathAndZipGuard.MaxCommunityZipBytes,
                linked.Token).ConfigureAwait(false);
            if (zipBytes == null || zipBytes.Length == 0)
                return (false, "下载到的压缩包为空", string.Empty, string.Empty);

            // 先安全校验并解压（Fail-Closed：若含路径穿越、可执行文件或 Zip Bomb 则绝不落盘归档）
            string extractTarget = ExtractModZipSafely(zipBytes, LocalModsDownloadRoot, cleanId);

            string zipPath = SafePathAndZipGuard.ResolveSafeChildPath(LocalZipArchiveRoot, $"{cleanId}.bazaarmod.zip");
            string zipTmp = zipPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllBytes(zipTmp, zipBytes);
                if (File.Exists(zipPath))
                {
                    File.Copy(zipTmp, zipPath, overwrite: true);
                    try { File.Delete(zipTmp); } catch { }
                }
                else
                {
                    File.Move(zipTmp, zipPath);
                }
            }
            catch
            {
                try { if (File.Exists(zipTmp)) File.Delete(zipTmp); } catch { }
                throw;
            }

            return (true, $"已下载并保存到本地：{extractTarget}", zipPath, extractTarget);
        }
        catch (Exception ex)
        {
            return (false, "下载 Mod 出错：" + ex.Message, string.Empty, string.Empty);
        }
    }

    private static string ResolveLocalModManifestPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        string modJson = Path.Combine(dir, "mod.json");
        if (File.Exists(modJson)) return modJson;
        string manifestJson = Path.Combine(dir, "manifest.json");
        if (File.Exists(manifestJson)) return manifestJson;
        return null;
    }

    /// <summary>扫描本地可用 Mod（包括 BepInEx/mods、游戏目录 mods/ 以及下载缓存目录里的 .bazaarmod.zip）。</summary>
    internal static List<LocalModSource> DiscoverLocalMods()
    {
        var results = new List<LocalModSource>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidateRoots = new List<string>
        {
            LocalModsDownloadRoot,
            Path.Combine(BepInEx.Paths.GameRootPath ?? ".", "mods"),
            LocalZipArchiveRoot,
        };

        foreach (string root in candidateRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                // 1. 子目录形式的 Mod（兼容 mod.json 与 manifest.json，与 ModuleHost 保持一致）
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string manifestPath = ResolveLocalModManifestPath(dir);
                    if (string.IsNullOrEmpty(manifestPath)) continue;
                    string full = Path.GetFullPath(dir);
                    if (!seenPaths.Add(full)) continue;
                    var info = ReadModSummaryFromManifest(manifestPath, Path.GetFileName(dir));
                    results.Add(new LocalModSource
                    {
                        ModId = info.Id,
                        DisplayName = info.Name,
                        Version = info.Version,
                        FullPath = full,
                        IsZipFile = false,
                    });
                }

                // 2. .bazaarmod.zip 压缩包
                foreach (string zip in Directory.GetFiles(root, "*.bazaarmod.zip"))
                {
                    string full = Path.GetFullPath(zip);
                    if (!seenPaths.Add(full)) continue;
                    string baseName = Path.GetFileName(zip);
                    string modId = baseName.EndsWith(".bazaarmod.zip", StringComparison.OrdinalIgnoreCase)
                        ? baseName.Substring(0, baseName.Length - ".bazaarmod.zip".Length)
                        : Path.GetFileNameWithoutExtension(zip);
                    results.Add(new LocalModSource
                    {
                        ModId = modId,
                        DisplayName = baseName,
                        Version = "zip",
                        FullPath = full,
                        IsZipFile = true,
                    });
                }
            }
            catch { }
        }

        return results.OrderBy(x => x.ModId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>将本地 Mod 目录或 .bazaarmod.zip 打包并上传到当前服务器的社区 Mod 库。</summary>
    internal async Task<(bool Ok, string Message)> UploadLocalModAsync(string pathOrDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrDir))
            return (false, "请选择要上传的本地 Mod 路径");

        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "尚未设置服务器地址");

        try
        {
            byte[] zipBytes;
            string uploadName;
            if (Directory.Exists(pathOrDir))
            {
                string manifestPath = ResolveLocalModManifestPath(pathOrDir);
                if (string.IsNullOrEmpty(manifestPath))
                    return (false, "该目录缺少 mod.json / manifest.json，无法作为 Mod 上传");
                var meta = ReadModSummaryFromManifest(manifestPath, Path.GetFileName(pathOrDir));
                if (!SafePathAndZipGuard.IsValidModId(meta.Id))
                    return (false, $"Mod ID 不合法（仅允许字母、数字、点、下划线、连字符，且不得含 .. 或系统保留名）：{meta.Id}");
                zipBytes = PackLocalModFolderToZip(pathOrDir, meta.Id);
                uploadName = $"{meta.Id}.bazaarmod.zip";
            }
            else if (File.Exists(pathOrDir))
            {
                zipBytes = File.ReadAllBytes(pathOrDir);
                uploadName = Path.GetFileName(pathOrDir);
            }
            else
            {
                return (false, "找不到指定的本地 Mod 文件或目录");
            }

            if (zipBytes.Length == 0)
                return (false, "打包结果为空");
            if (zipBytes.Length > 10 * 1024 * 1024)
                return (false, $"文件大小 {zipBytes.Length / 1024.0 / 1024.0:F1} MB 超过 10 MB 限制");

            await NameAuth.EnsureAccountIdentityAsync(ct).ConfigureAwait(false);
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(zipBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(fileContent, "file", uploadName);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/community/mods/upload")
            {
                Content = content
            };
            NameAuth.ApplyAuthHeaders(req);
            string playerName = PlayerIdentity.ResolveName();
            if (!string.IsNullOrWhiteSpace(playerName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(playerName));

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, linked.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var jo = JObject.Parse(body);
                bool ok = resp.IsSuccessStatusCode && ((bool?)jo["ok"] ?? true);
                string msg = (string)(jo["messageZh"] ?? jo["error"]) ?? (ok ? "上传成功" : $"上传失败（HTTP {(int)resp.StatusCode}）");
                return (ok, msg);
            }
            catch
            {
                return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? "上传成功" : $"上传失败（HTTP {(int)resp.StatusCode}）");
            }
        }
        catch (Exception ex)
        {
            return (false, "上传异常：" + ex.Message);
        }
    }

    internal async Task<(bool Ok, string Message)> DeleteCommunityModAsync(string modId, CancellationToken ct = default)
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modId))
            return (false, "参数无效");

        string cleanId = modId.Trim();
        if (!SafePathAndZipGuard.IsValidModId(cleanId))
            return (false, $"非法的 Mod ID：{cleanId}");

        try
        {
            await NameAuth.EnsureAccountIdentityAsync(ct).ConfigureAwait(false);
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/community/mods/{Uri.EscapeDataString(cleanId)}");
            NameAuth.ApplyAuthHeaders(req);
            string playerName = PlayerIdentity.ResolveName();
            if (!string.IsNullOrWhiteSpace(playerName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(playerName));

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, linked.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var jo = JObject.Parse(body);
                bool ok = resp.IsSuccessStatusCode && ((bool?)jo["ok"] ?? true);
                return (ok, (string)(jo["messageZh"] ?? jo["error"]) ?? (ok ? "已删除" : "删除失败"));
            }
            catch
            {
                return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? "已删除" : $"删除失败（HTTP {(int)resp.StatusCode}）");
            }
        }
        catch (Exception ex)
        {
            return (false, "删除失败：" + ex.Message);
        }
    }

    private static (string Id, string Name, string Version) ReadModSummaryFromManifest(string manifestPath, string fallbackId)
    {
        try
        {
            var jo = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            string id = (string)jo["id"] ?? fallbackId;
            string name = (string)jo["name"] ?? id;
            string ver = (string)jo["version"] ?? "1.0.0";
            return (id.Trim(), name.Trim(), ver.Trim());
        }
        catch
        {
            return (fallbackId, fallbackId, "1.0.0");
        }
    }

    private static byte[] PackLocalModFolderToZip(string sourceDir, string modId)
    {
        if (!SafePathAndZipGuard.IsValidModId(modId))
            throw new InvalidOperationException($"非法的 Mod ID：{modId}");

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            string rootFull = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = full.Substring(rootFull.Length).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)
                    || rel.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0
                    || rel.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
                    || rel.IndexOf("/__pycache__/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (SafePathAndZipGuard.IsForbiddenExtension(rel))
                    continue;

                var entry = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    private static string ExtractModZipSafely(byte[] zipBytes, string allowedModsRoot, string modId)
    {
        return SafePathAndZipGuard.ExtractModZipSafely(zipBytes, allowedModsRoot, modId);
    }
}
