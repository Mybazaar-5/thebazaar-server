using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarModSdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher.Core;

/// <summary>
/// 服务端 Mod 清单、自定义 PNG 卡图资产（SHA-256 增量下载）、四层中文词典与活跃对局视觉绑定的联机自动同步客户端。
/// 本地缓存路径：BepInEx/config/BazaarModCache/
/// </summary>
internal sealed partial class ModContentSyncClient
{
    internal sealed class ServerModEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Scope { get; set; } = "multiplayer-safe";
        public bool Enabled { get; set; } = true;
        public int CustomCardCount { get; set; }
        public int CustomEventCount { get; set; }
        public int AssetCount { get; set; }
    }

    internal sealed class ServerCustomContentItem
    {
        public string ModId { get; set; } = string.Empty;
        public string CustomId { get; set; } = string.Empty;
        public string BaseVisualTemplateId { get; set; } = string.Empty;
        public string CardType { get; set; } = "Item";
        public string Size { get; set; } = "Medium";
        public string NameZh { get; set; } = string.Empty;
        public string DescZh { get; set; } = string.Empty;
        public string ArtSha256 { get; set; } = string.Empty;
        public string LocalArtPath { get; set; } = string.Empty;
    }

    private readonly object _sync = new();
    private readonly List<IDisposable> _syncedVisualHandles = new();
    private readonly List<IDisposable> _syncedActiveBindingHandles = new();
    private readonly List<IDisposable> _syncedRoomBundleVisualHandles = new();
    private readonly List<IDisposable> _syncedRoomBundleTextHandles = new();
    private readonly List<IDisposable> _syncedCardTextHandles = new();
    private List<ServerModEntry> _serverMods = new();
    private readonly List<IDisposable> _syncedReplayBindingHandles = new();
    private List<ServerCustomContentItem> _customContents = new();
    private HashSet<string> _globalDonorTemplateIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _roomDonorTemplateIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _activeDonorTemplateIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _donorTemplateIds = new(StringComparer.OrdinalIgnoreCase);
    private long _syncGeneration;
    private long _roomBundleSyncEpoch;
    private CancellationTokenSource _serverSyncCts;
    private int _activeBindingsInFlight;
    private int _activeBindingsResyncQueued;
    private long _lastAutoBindingsTickMs;
    private long _lastRevSyncTickMs;
    private string _lastVisualBindingsRev = string.Empty;
    private string _lastModSetHash = string.Empty;
    private string _lastOverlaySha256 = string.Empty;
    private string _lastSyncSummary = "尚未同步";
    private DateTime _lastSyncUtc;

    // P2-02：按端点路径缓存最近一次服务端 ETag（按服务器基址自动隔离），供 If-None-Match 命中 304 早退
    private readonly Dictionary<string, string> _endpointEtags = new(StringComparer.OrdinalIgnoreCase);
    private string _etagServerScope = string.Empty;
    private int _lastManifestEnabledMods;
    private int _lastManifestTotalModCount;
    private int _lastManifestCustomContentCount;
    private int _lastManifestVisualBindingCount;
    private int _lastManifestDownloadedPngs;

    // P2-02：按当前 visual bindings revision 记忆已确认的“原版壳卡”实例 ID，彻底消灭每 250ms 重复全量轮询
    private readonly HashSet<string> _checkedVanillaInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingProbeInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlightProbeInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private string _checkedVanillaRev = string.Empty;

    internal static ModContentSyncClient Instance { get; } = new();

    internal string LastModSetHash => _lastModSetHash;
    internal string LastOverlaySha256 => _lastOverlaySha256;
    internal string LastSyncSummary => _lastSyncSummary;
    internal DateTime LastSyncUtc => _lastSyncUtc;
    internal string LastVisualBindingsRev => _lastVisualBindingsRev;

    internal static string GetServerCacheDirForUrl(string baseUrl)
    {
        string key = string.IsNullOrWhiteSpace(baseUrl)
            ? "default"
            : ComputeSha256Hex(Encoding.UTF8.GetBytes(baseUrl.Trim().TrimEnd('/').ToLowerInvariant())).Substring(0, 12);
        string dir = Path.Combine(ModuleHost.CacheDirectory, "srv_" + key);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    internal static string GetOverlayCachePathForUrl(string baseUrl)
        => Path.Combine(GetServerCacheDirForUrl(baseUrl), "custom_zh_overlay.json");

    internal string CurrentServerCacheDir => GetServerCacheDirForUrl(NameAuth.GetServerBaseUrl());

    /// <summary>D7-P1-02：与 BazaarHub 共享统一的按 SHA-256 内容寻址 CAS 资产池（BepInEx\config\BazaarModCache\assets\）。</summary>
    internal string SharedAssetsCacheDir
    {
        get
        {
            string dir = Path.Combine(ModuleHost.CacheDirectory, "assets");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    internal string AssetsCacheDir => SharedAssetsCacheDir;

    internal string LegacyServerAssetsCacheDir => Path.Combine(CurrentServerCacheDir, "assets");

    internal string OverlayCachePath => Path.Combine(CurrentServerCacheDir, "custom_zh_overlay.json");

    internal long AdvanceSyncGenerationForTest() => Interlocked.Increment(ref _syncGeneration);
    internal bool IsSyncGenerationCurrent(long generation) => Volatile.Read(ref _syncGeneration) == generation;
    internal long AdvanceRoomBundleEpochForTest() => Interlocked.Increment(ref _roomBundleSyncEpoch);
    internal bool IsRoomBundleEpochCurrent(long epoch) => Volatile.Read(ref _roomBundleSyncEpoch) == epoch;

    private void RebuildDonorTemplateIdsLocked()
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in _globalDonorTemplateIds) merged.Add(id);
        foreach (string id in _roomDonorTemplateIds) merged.Add(id);
        foreach (string id in _activeDonorTemplateIds) merged.Add(id);
        _donorTemplateIds = merged;
    }

    private (long Generation, CancellationToken Token) BeginNewServerSyncGeneration()
    {
        CancellationTokenSource oldCts;
        CancellationTokenSource nextCts = CancellationTokenSource.CreateLinkedTokenSource(HttpClientHub.ShutdownToken);
        long gen;
        lock (_sync)
        {
            gen = Interlocked.Increment(ref _syncGeneration);
            oldCts = _serverSyncCts;
            _serverSyncCts = nextCts;
        }
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            try { oldCts.Dispose(); } catch { }
        }
        return (gen, nextCts.Token);
    }

    private CancellationToken GetCurrentServerSyncToken()
    {
        lock (_sync)
        {
            return _serverSyncCts != null ? _serverSyncCts.Token : HttpClientHub.ShutdownToken;
        }
    }

    internal IReadOnlyList<ServerModEntry> GetServerModsSnapshot()
    {
        lock (_sync)
        {
            return _serverMods.ToArray();
        }
    }

    internal IReadOnlyList<ServerCustomContentItem> GetCustomContentsSnapshot()
    {
        lock (_sync)
        {
            return _customContents.ToArray();
        }
    }

    /// <summary>触发后台全量异步同步（P0-29：单调 SyncGeneration + 独立 CTS，切服先取消旧任务绝不静默丢弃新服）。</summary>
    internal void TriggerFullSyncInBackground(bool forceDownload = false)
    {
        var (gen, syncCt) = BeginNewServerSyncGeneration();

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAllInternalAsync(forceDownload, gen, syncCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[ModSync] 后台同步异常: " + ex.Message);
            }
        });
    }

    private void EnsureEtagScopeLocked()
    {
        string curBase = NameAuth.GetServerBaseUrl() ?? string.Empty;
        if (!string.Equals(_etagServerScope, curBase, StringComparison.OrdinalIgnoreCase))
        {
            _etagServerScope = curBase;
            _endpointEtags.Clear();
            _checkedVanillaInstances.Clear();
            _pendingProbeInstanceIds.Clear();
            _inFlightProbeInstanceIds.Clear();
            _checkedVanillaRev = string.Empty;
        }
    }

    private string TryGetCachedEtag(string endpointKey)
    {
        if (string.IsNullOrWhiteSpace(endpointKey)) return null;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            return _endpointEtags.TryGetValue(endpointKey, out string etag) && !string.IsNullOrWhiteSpace(etag)
                ? etag
                : null;
        }
    }

    private void StoreCachedEtag(string endpointKey, string etag)
    {
        if (string.IsNullOrWhiteSpace(endpointKey) || string.IsNullOrWhiteSpace(etag)) return;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            _endpointEtags[endpointKey] = etag.Trim();
        }
    }

    private void ClearActiveBindingsEtagsLocked()
    {
        if (_endpointEtags.Count == 0) return;
        var toRemove = new List<string>();
        foreach (string key in _endpointEtags.Keys)
        {
            if (key.StartsWith("/api/mods/active-bindings", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("/api/runs/", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(key);
            }
        }
        for (int i = 0; i < toRemove.Count; i++)
        {
            _endpointEtags.Remove(toRemove[i]);
        }
    }

    private void ConfirmProbedVanillaInstances(
        List<string> probedSnapshot,
        HashSet<string> boundInstanceIds,
        string confirmedRev,
        long expectedGeneration)
    {
        lock (_sync)
        {
            if (!IsSyncGenerationCurrent(expectedGeneration)) return;
            EnsureEtagScopeLocked();

            string effectiveRev = !string.IsNullOrWhiteSpace(confirmedRev) ? confirmedRev : _lastVisualBindingsRev;
            if (string.IsNullOrWhiteSpace(effectiveRev))
            {
                return;
            }

            if (!string.Equals(_checkedVanillaRev, effectiveRev, StringComparison.Ordinal))
            {
                _checkedVanillaInstances.Clear();
                _checkedVanillaRev = effectiveRev;
            }

            if (probedSnapshot == null || probedSnapshot.Count == 0) return;

            if (_checkedVanillaInstances.Count > 2048)
            {
                _checkedVanillaInstances.Clear();
            }

            for (int i = 0; i < probedSnapshot.Count; i++)
            {
                string instId = probedSnapshot[i];
                if (string.IsNullOrWhiteSpace(instId)) continue;
                if (boundInstanceIds != null && boundInstanceIds.Contains(instId)) continue;
                _checkedVanillaInstances.Add(instId);
            }
        }
    }

    /// <summary>D3-P1-13：由常驻 CloudGuard 响应头感知 X-Visual-Bindings-Rev 变更，节流触发活跃绑定同步。</summary>
    internal void NotifyVisualBindingsRevObserved(string observedRev)
    {
        if (string.IsNullOrWhiteSpace(observedRev)) return;
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value) return;
        if (string.Equals(_lastVisualBindingsRev, observedRev, StringComparison.Ordinal)) return;

        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long prevMs = Interlocked.Read(ref _lastRevSyncTickMs);
        if (nowMs - prevMs < 1500)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRevSyncTickMs, nowMs);
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            _lastVisualBindingsRev = observedRev;
            // revision 已推进，立即失效旧 revision 的原版壳卡记忆，避免遮蔽新产生的 Mod 实例绑定
            _checkedVanillaInstances.Clear();
            _checkedVanillaRev = string.Empty;
        }
        TriggerActiveBindingsSyncInBackground();
    }

    /// <summary>P0-29：退出对局返回主大厅时清理局内活跃绑定与回放绑定快照，防止跨局残留。</summary>
    internal void OnReturnedToMainMenu()
    {
        bool hadAny;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            hadAny = _syncedActiveBindingHandles.Count > 0
                || _syncedReplayBindingHandles.Count > 0
                || _activeDonorTemplateIds.Count > 0;
            DisposeHandlesLocked(_syncedActiveBindingHandles);
            DisposeHandlesLocked(_syncedReplayBindingHandles);
            _activeDonorTemplateIds.Clear();
            _lastVisualBindingsRev = string.Empty;
            _checkedVanillaRev = string.Empty;
            _checkedVanillaInstances.Clear();
            _pendingProbeInstanceIds.Clear();
            _inFlightProbeInstanceIds.Clear();
            ClearActiveBindingsEtagsLocked();
            RebuildDonorTemplateIdsLocked();
        }
        if (hadAny)
        {
            NotifyVisualAndLocaleChanged("return-main-menu");
        }
    }

    /// <summary>同步当前活跃对局或观战目标的自定义实例视觉与文案绑定（/api/mods/active-bindings）。</summary>
    internal void TriggerActiveBindingsSyncInBackground(string runId = null, string accountId = null, string watchCode = null)
    {
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
        {
            ClearSyncedServerMods();
            return;
        }

        if (Interlocked.CompareExchange(ref _activeBindingsInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _activeBindingsResyncQueued, 1);
            return;
        }

        CancellationToken syncCt = GetCurrentServerSyncToken();
        long capturedGen = Volatile.Read(ref _syncGeneration);

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncActiveBindingsInternalAsync(runId, accountId, watchCode, capturedGen, syncCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[ModSync] 同步活跃对局绑定失败: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _activeBindingsInFlight, 0);
                if (Interlocked.Exchange(ref _activeBindingsResyncQueued, 0) != 0 && IsSyncGenerationCurrent(capturedGen))
                {
                    TriggerActiveBindingsSyncInBackground(runId, accountId, watchCode);
                }
            }
        });
    }

    /// <summary>当 3D 棋盘/商店新刷出的卡牌命中任意自定义卡底板 GUID 且尚未绑定时，按需触发防抖拉取 /api/mods/active-bindings。</summary>
    internal void NotifyUnboundInstanceSeen(string instanceId, string templateId)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(templateId)) return;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            if (!_donorTemplateIds.Contains(templateId)) return;

            // P2-02：同一 revision 下已经服务端确认的“原版壳卡”实例直接 O(1) 早退，避免每 250ms 反复轮询 active-bindings
            if (!string.IsNullOrEmpty(_checkedVanillaRev)
                && string.Equals(_checkedVanillaRev, _lastVisualBindingsRev, StringComparison.Ordinal)
                && _checkedVanillaInstances.Contains(instanceId))
            {
                return;
            }

            // 同一实例若已在等待/在途探测队列中，无需重复入队触发多余轮询
            if (_inFlightProbeInstanceIds.Contains(instanceId) || _pendingProbeInstanceIds.Contains(instanceId))
            {
                return;
            }

            if (_pendingProbeInstanceIds.Count >= 512)
            {
                _pendingProbeInstanceIds.Clear();
            }
            _pendingProbeInstanceIds.Add(instanceId);
        }

        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long prevMs = Interlocked.Read(ref _lastAutoBindingsTickMs);
        if (nowMs - prevMs < 250)
        {
            if (Volatile.Read(ref _activeBindingsInFlight) != 0)
            {
                Interlocked.Exchange(ref _activeBindingsResyncQueued, 1);
                return;
            }
            // 上一次请求已结束但仍在防抖窗口内：延迟 260ms 补拉一次，避免快速连刷商店/战中生卡时漏同步
            if (Interlocked.CompareExchange(ref _activeBindingsResyncQueued, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(260, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                        if (Interlocked.Exchange(ref _activeBindingsResyncQueued, 0) != 0)
                        {
                            Interlocked.Exchange(ref _lastAutoBindingsTickMs, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
                            TriggerActiveBindingsSyncInBackground();
                        }
                    }
                    catch { }
                });
            }
            return;
        }
        Interlocked.Exchange(ref _lastAutoBindingsTickMs, nowMs);
        TriggerActiveBindingsSyncInBackground();
    }

    /// <summary>执行一次完整的 Mod 清单、PNG 资产、四层中文词典和活跃绑定同步。</summary>
    internal Task<bool> SyncAllAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        long gen = Volatile.Read(ref _syncGeneration);
        return SyncAllInternalAsync(forceRefresh, gen, ct);
    }

    private async Task<bool> SyncAllInternalAsync(bool forceRefresh, long capturedGen, CancellationToken ct)
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl) || ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen))
        {
            return false;
        }

        int downloadedPngs = 0;
        int bindingCount = 0;
        int enabledMods = 0;

        // 1. 拉取外挂四层中文词典 /api/localization/overlay（回退 /api/localization/zh-overlay）
        await SyncLocalizationOverlayAsync(forceRefresh, capturedGen, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return false;

        // 若用户关闭「允许接收服务器下发的 Mod」，清掉已注册的服务端 Mod 贴图与文案并直接结束
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
        {
            ClearSyncedServerMods();
            _lastSyncSummary = "已关闭接收服务器 Mod（仅同步中文词典）";
            _lastSyncUtc = DateTime.UtcNow;
            return true;
        }

        // 2. 拉取服务端 Mod 清单 /api/mods/manifest（P2-02：携带 If-None-Match 命中 304 直接早退复用内存缓存）
        const string manifestPath = "/api/mods/manifest";
        bool hasCachedManifest;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            // 若曾因关闭 AcceptServerMods 触发 ClearSyncedServerMods() 清空内存句柄，则跳过旧 ETag 强制全量重建
            hasCachedManifest = !string.IsNullOrEmpty(_lastModSetHash)
                && _serverMods.Count == _lastManifestTotalModCount
                && _customContents.Count == _lastManifestCustomContentCount
                && (_lastManifestVisualBindingCount == 0 || _syncedVisualHandles.Count > 0 || _syncedCardTextHandles.Count > 0);
        }
        string manifestIfNoneMatch = (!forceRefresh && hasCachedManifest) ? TryGetCachedEtag(manifestPath) : null;
        var manifestResp = await NameAuth.GetJsonAsync(manifestPath, ct, manifestIfNoneMatch).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return false;

        if (manifestResp.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(manifestResp.ETag))
            {
                StoreCachedEtag(manifestPath, manifestResp.ETag);
            }
            lock (_sync)
            {
                enabledMods = _lastManifestEnabledMods;
                bindingCount = _lastManifestVisualBindingCount;
                downloadedPngs = _lastManifestDownloadedPngs;
            }
        }
        else if (manifestResp.Ok && !string.IsNullOrWhiteSpace(manifestResp.Body))
        {
            var root = JObject.Parse(manifestResp.Body);
            string modSetHash = (string)(root["modSetHash"] ?? root["manifestHash"]) ?? string.Empty;

            var parsedMods = new List<ServerModEntry>();
            if (root["mods"] is JArray modsArr)
            {
                foreach (var token in modsArr)
                {
                    if (token is not JObject mo) continue;
                    var entry = new ServerModEntry
                    {
                        Id = (string)(mo["id"] ?? mo["modId"]) ?? string.Empty,
                        Name = (string)(mo["name"] ?? mo["nameZh"] ?? mo["id"]) ?? string.Empty,
                        Version = (string)mo["version"] ?? "1.0.0",
                        Author = (string)mo["author"] ?? string.Empty,
                        Description = (string)(mo["description"] ?? mo["descZh"]) ?? string.Empty,
                        Scope = (string)mo["scope"] ?? "multiplayer-safe",
                        Enabled = (bool?)mo["enabled"] ?? true,
                        CustomCardCount = (int?)(mo["cardCount"] ?? mo["customCardCount"]) ?? 0,
                        CustomEventCount = (int?)(mo["eventCount"] ?? mo["customEventCount"]) ?? 0,
                        AssetCount = (int?)mo["assetCount"] ?? 0,
                    };
                    if (!string.IsNullOrWhiteSpace(entry.Id))
                    {
                        parsedMods.Add(entry);
                        if (entry.Enabled) enabledMods++;
                    }
                }
            }

            // 按 SHA-256 增量下载自定义 PNG/JPEG 资产到统一共享 CAS 池
            var shaToLocalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root["assets"] is JArray assetsArr)
            {
                foreach (var token in assetsArr)
                {
                    if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return false;
                    if (token is not JObject ao) continue;
                    string sha = NormalizeSha((string)(ao["sha256"] ?? ao["hash"]));
                    if (string.IsNullOrEmpty(sha)) continue;

                    string localPath = await EnsureAssetCachedAsync(sha, forceRefresh, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        shaToLocalPath[sha] = localPath;
                        downloadedPngs++;
                    }
                }
            }

            // 解析并注册视觉与文案绑定
            var parsedContents = new List<ServerCustomContentItem>();
            var newVisualBindings = new List<CardVisualBinding>();
            var newTextOverrides = new List<CardTextOverride>();

            JToken bindingsToken = root["visualBindings"] ?? root["customCards"] ?? root["bindings"];
            if (bindingsToken is JArray bindingsArr)
            {
                foreach (var token in bindingsArr)
                {
                    if (token is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, null, isActiveBinding: false, shaToLocalPath, parsedContents, newVisualBindings, newTextOverrides, forceRefresh, ct).ConfigureAwait(false);
                }
            }
            else if (bindingsToken is JObject bindingsMap)
            {
                foreach (var prop in bindingsMap.Properties())
                {
                    if (prop.Value is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: false, shaToLocalPath, parsedContents, newVisualBindings, newTextOverrides, forceRefresh, ct).ConfigureAwait(false);
                }
            }

            // D3-P2-06：服务端已在 customCards 中按 cardType 统一承载 Item/Skill，移除废弃的 customSkills 死分支
            AppendCatalogSummary(root["customEvents"] as JArray, "EventEncounter", parsedContents);

            if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return false;

            ApplySyncedVisualAndTextHandles(newVisualBindings, newTextOverrides, isActiveBinding: false, capturedGen);
            bindingCount = newVisualBindings.Count;

            if (!string.IsNullOrWhiteSpace(manifestResp.ETag))
            {
                StoreCachedEtag(manifestPath, manifestResp.ETag);
            }

            lock (_sync)
            {
                if (!IsSyncGenerationCurrent(capturedGen)) return false;
                _serverMods = parsedMods;
                _customContents = parsedContents;
                var donors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in parsedContents)
                {
                    if (!string.IsNullOrWhiteSpace(item.BaseVisualTemplateId))
                        donors.Add(item.BaseVisualTemplateId.Trim());
                }
                _globalDonorTemplateIds = donors;
                RebuildDonorTemplateIdsLocked();
                _lastModSetHash = modSetHash;
                _lastManifestEnabledMods = enabledMods;
                _lastManifestTotalModCount = parsedMods.Count;
                _lastManifestCustomContentCount = parsedContents.Count;
                _lastManifestVisualBindingCount = bindingCount;
                _lastManifestDownloadedPngs = downloadedPngs;
            }
        }

        // 3. 拉取当前活跃对局实例绑定
        await SyncActiveBindingsInternalAsync(null, null, null, capturedGen, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return false;

        _lastSyncUtc = DateTime.UtcNow;
        _lastSyncSummary = $"已同步 {enabledMods} 个 Mod / {bindingCount} 条视觉绑定 / 缓存 {downloadedPngs} 张贴图";

        BazaarLauncherPlugin.Host?.EventBus.Publish(new ModAssetsSyncedModEvent(
            enabledMods,
            bindingCount,
            downloadedPngs,
            _lastModSetHash));

        return true;
    }

    /// <summary>同步当前对局或观战目标的实例级绑定（P0-26 支援 ?watchCode=&amp;runId=&amp;accountId=；D3-P1-13 尊重 AcceptServerMods）。</summary>
    internal Task SyncActiveBindingsAsync(
        string runId = null,
        CancellationToken ct = default,
        string accountId = null,
        string watchCode = null)
    {
        long gen = Volatile.Read(ref _syncGeneration);
        return SyncActiveBindingsInternalAsync(runId, accountId, watchCode, gen, ct);
    }

    private async Task SyncActiveBindingsInternalAsync(
        string runId,
        string accountId,
        string watchCode,
        long capturedGen,
        CancellationToken ct)
    {
        // D3-P1-13：严格尊重 AcceptServerMods = false
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
        {
            ClearSyncedServerMods();
            return;
        }

        if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return;

        var qp = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(watchCode)) qp.Add("watchCode=" + Uri.EscapeDataString(watchCode.Trim()));
        if (!string.IsNullOrWhiteSpace(runId)) qp.Add("runId=" + Uri.EscapeDataString(runId.Trim()));
        if (!string.IsNullOrWhiteSpace(accountId)) qp.Add("accountId=" + Uri.EscapeDataString(accountId.Trim()));
        string path = qp.Count > 0 ? "/api/mods/active-bindings?" + string.Join("&", qp) : "/api/mods/active-bindings";

        bool isDefaultLocalQuery = qp.Count == 0;
        List<string> probedSnapshot = null;
        string revBeforeRequest;
        bool hasActiveCacheState;
        lock (_sync)
        {
            EnsureEtagScopeLocked();
            revBeforeRequest = _lastVisualBindingsRev;
            // 若 _lastVisualBindingsRev 为空（如刚回主菜单或 ClearSyncedServerMods 清空句柄），先清除旧活跃绑定 ETag 强制拉 200
            hasActiveCacheState = !string.IsNullOrEmpty(revBeforeRequest);
            if (!hasActiveCacheState)
            {
                ClearActiveBindingsEtagsLocked();
            }

            if (isDefaultLocalQuery && _pendingProbeInstanceIds.Count > 0)
            {
                probedSnapshot = new List<string>(_pendingProbeInstanceIds);
                _pendingProbeInstanceIds.Clear();
                for (int i = 0; i < probedSnapshot.Count; i++)
                {
                    _inFlightProbeInstanceIds.Add(probedSnapshot[i]);
                }
            }
        }

        try
        {
            string primaryIfNoneMatch = hasActiveCacheState ? TryGetCachedEtag(path) : null;
            var resp = await NameAuth.GetJsonAsync(path, ct, primaryIfNoneMatch).ConfigureAwait(false);
            string usedPath = path;

            if (resp.NotModified)
            {
                if (!string.IsNullOrWhiteSpace(resp.ETag))
                {
                    StoreCachedEtag(usedPath, resp.ETag);
                }
                if (isDefaultLocalQuery)
                {
                    ConfirmProbedVanillaInstances(probedSnapshot, null, revBeforeRequest, capturedGen);
                }
                // P2-02：HTTP 304 Not Modified 直接复用内存绑定缓存，零反序列化、零句柄重建、零主线程重绘
                return;
            }

            if (!resp.Ok && !string.IsNullOrWhiteSpace(runId))
            {
                string fallbackPath = $"/api/runs/{Uri.EscapeDataString(runId)}/visual-bindings";
                string fallbackIfNoneMatch = hasActiveCacheState ? TryGetCachedEtag(fallbackPath) : null;
                resp = await NameAuth.GetJsonAsync(fallbackPath, ct, fallbackIfNoneMatch).ConfigureAwait(false);
                usedPath = fallbackPath;
                if (resp.NotModified)
                {
                    if (!string.IsNullOrWhiteSpace(resp.ETag))
                    {
                        StoreCachedEtag(usedPath, resp.ETag);
                    }
                    return;
                }
            }

            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body)) return;
            if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return;

            var root = JObject.Parse(resp.Body);
            string rev = (string)(root["revision"] ?? root["visualBindingsRev"]);
            if (!string.IsNullOrWhiteSpace(rev)) _lastVisualBindingsRev = rev;

            var shaToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visuals = new List<CardVisualBinding>();
            var texts = new List<CardTextOverride>();
            var activeContents = new List<ServerCustomContentItem>();
            var activeDonors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // P0-18：同步解析 templateCatalog 更新 _activeDonorTemplateIds 并补充模板级绑定
            JToken catalogToken = root["templateCatalog"] ?? root["templates"];
            if (catalogToken is JObject catObj)
            {
                foreach (var prop in catObj.Properties())
                {
                    if (prop.Value is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: false, shaToPath, activeContents, visuals, texts, false, ct).ConfigureAwait(false);
                }
            }
            else if (catalogToken is JArray catArr)
            {
                foreach (var token in catArr)
                {
                    if (token is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, null, isActiveBinding: false, shaToPath, activeContents, visuals, texts, false, ct).ConfigureAwait(false);
                }
            }

            JToken mapToken = root["bindings"] ?? root["activeBindings"] ?? root;
            if (mapToken is JObject mapObj)
            {
                foreach (var prop in mapObj.Properties())
                {
                    if (prop.Value is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: true, shaToPath, activeContents, visuals, texts, false, ct).ConfigureAwait(false);
                }
            }
            else if (mapToken is JArray arr)
            {
                foreach (var token in arr)
                {
                    if (token is not JObject bo) continue;
                    await ExtractBindingObjectAsync(bo, null, isActiveBinding: true, shaToPath, activeContents, visuals, texts, false, ct).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return;

            foreach (var item in activeContents)
            {
                if (!string.IsNullOrWhiteSpace(item.BaseVisualTemplateId))
                {
                    activeDonors.Add(item.BaseVisualTemplateId.Trim());
                }
            }

            lock (_sync)
            {
                if (!IsSyncGenerationCurrent(capturedGen)) return;
                _activeDonorTemplateIds = activeDonors;
                RebuildDonorTemplateIdsLocked();
            }

            ApplySyncedVisualAndTextHandles(visuals, texts, isActiveBinding: true, capturedGen);

            if (!string.IsNullOrWhiteSpace(resp.ETag))
            {
                StoreCachedEtag(usedPath, resp.ETag);
            }

            if (isDefaultLocalQuery)
            {
                var boundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < visuals.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(visuals[i].InstanceId))
                    {
                        boundIds.Add(visuals[i].InstanceId);
                    }
                }
                for (int i = 0; i < texts.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(texts[i].InstanceId))
                    {
                        boundIds.Add(texts[i].InstanceId);
                    }
                }
                ConfirmProbedVanillaInstances(probedSnapshot, boundIds, rev, capturedGen);
            }
        }
        finally
        {
            if (probedSnapshot != null && probedSnapshot.Count > 0)
            {
                lock (_sync)
                {
                    for (int i = 0; i < probedSnapshot.Count; i++)
                    {
                        _inFlightProbeInstanceIds.Remove(probedSnapshot[i]);
                    }
                }
            }
        }
    }

    /// <summary>P0-26：消费战报回放记录内嵌的 visualBindings / visualBindingsJson 快照，以独立句柄组还原历史卡图与双语文案。</summary>
    internal async Task ApplyReplayVisualBindingsSnapshotAsync(JToken visualBindingsToken, CancellationToken ct = default)
    {
        if (visualBindingsToken == null || visualBindingsToken.Type == JTokenType.Null)
        {
            ClearReplayVisualBindings();
            return;
        }
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
        {
            return;
        }

        JToken parsed = visualBindingsToken;
        if (visualBindingsToken.Type == JTokenType.String)
        {
            string raw = visualBindingsToken.Value<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ClearReplayVisualBindings();
                return;
            }
            try { parsed = JToken.Parse(raw); }
            catch { return; }
        }

        var shaToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visuals = new List<CardVisualBinding>();
        var texts = new List<CardTextOverride>();
        var dummy = new List<ServerCustomContentItem>();

        JToken inner = parsed is JObject jo && (jo["bindings"] != null || jo["visualBindings"] != null)
            ? (jo["bindings"] ?? jo["visualBindings"])
            : parsed;

        if (inner is JObject mapObj)
        {
            foreach (var prop in mapObj.Properties())
            {
                if (prop.Value is not JObject bo) continue;
                await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: true, shaToPath, dummy, visuals, texts, false, ct).ConfigureAwait(false);
            }
        }
        else if (inner is JArray arr)
        {
            foreach (var token in arr)
            {
                if (token is not JObject bo) continue;
                await ExtractBindingObjectAsync(bo, null, isActiveBinding: true, shaToPath, dummy, visuals, texts, false, ct).ConfigureAwait(false);
            }
        }

        var visualReg = BazaarLauncherPlugin.Host?.VisualRegistry;
        var l10nReg = BazaarLauncherPlugin.Host?.LocalizationRegistry;
        lock (_sync)
        {
            DisposeHandlesLocked(_syncedReplayBindingHandles);
            if (visualReg != null)
            {
                foreach (var v in visuals)
                {
                    v.Priority = Math.Max(v.Priority, 600);
                    _syncedReplayBindingHandles.Add(visualReg.RegisterVisualBinding(v));
                }
            }
            if (l10nReg != null)
            {
                foreach (var t in texts)
                {
                    t.Priority = Math.Max(t.Priority, 600);
                    _syncedReplayBindingHandles.Add(l10nReg.RegisterCardTextOverride(t));
                }
            }
        }
        NotifyVisualAndLocaleChanged("replay-bindings");
    }

    internal void ClearReplayVisualBindings()
    {
        bool hadAny;
        lock (_sync)
        {
            hadAny = _syncedReplayBindingHandles.Count > 0;
            DisposeHandlesLocked(_syncedReplayBindingHandles);
        }
        if (hadAny)
        {
            NotifyVisualAndLocaleChanged("clear-replay-bindings");
        }
    }

    private async Task SyncLocalizationOverlayAsync(bool forceRefresh, long capturedGen, CancellationToken ct)
    {
        const string primaryPath = "/api/localization/overlay";
        const string fallbackPath = "/api/localization/zh-overlay?locale=zh-CN";

        // P2-02：非强制刷新且本地已同步过词典时携带 If-None-Match；收到 304 直接早退零写盘零重载
        bool canUseEtag = !forceRefresh && !string.IsNullOrEmpty(_lastOverlaySha256);
        string primaryIfNoneMatch = canUseEtag ? TryGetCachedEtag(primaryPath) : null;
        var resp = await NameAuth.GetJsonAsync(primaryPath, ct, primaryIfNoneMatch).ConfigureAwait(false);
        string usedPath = primaryPath;

        if (resp.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(resp.ETag))
            {
                StoreCachedEtag(usedPath, resp.ETag);
            }
            return;
        }

        if (!resp.Ok)
        {
            string fallbackIfNoneMatch = canUseEtag ? TryGetCachedEtag(fallbackPath) : null;
            resp = await NameAuth.GetJsonAsync(fallbackPath, ct, fallbackIfNoneMatch).ConfigureAwait(false);
            usedPath = fallbackPath;
            if (resp.NotModified)
            {
                if (!string.IsNullOrWhiteSpace(resp.ETag))
                {
                    StoreCachedEtag(usedPath, resp.ETag);
                }
                return;
            }
        }

        if (ct.IsCancellationRequested || !IsSyncGenerationCurrent(capturedGen)) return;

        if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
        {
            if (!string.IsNullOrWhiteSpace(resp.ETag))
            {
                StoreCachedEtag(usedPath, resp.ETag);
            }
            try
            {
                string sha = ComputeSha256Hex(Encoding.UTF8.GetBytes(resp.Body));
                if (forceRefresh || !string.Equals(sha, _lastOverlaySha256, StringComparison.OrdinalIgnoreCase))
                {
                    _lastOverlaySha256 = sha;
                    AtomicWriteText(OverlayCachePath, resp.Body);
                    Modules.LocalizationOverlayModule.Instance?.LoadOverlayJson(resp.Body, "server-sync");
                }
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[ModSync] 解析外挂汉化词典失败: " + ex.Message);
            }
        }
        else if (File.Exists(OverlayCachePath))
        {
            try
            {
                string cached = File.ReadAllText(OverlayCachePath, Encoding.UTF8);
                Modules.LocalizationOverlayModule.Instance?.LoadOverlayJson(cached, "disk-cache");
            }
            catch { }
        }
    }

    private async Task ExtractBindingObjectAsync(
        JObject bo,
        string keyHint,
        bool isActiveBinding,
        Dictionary<string, string> shaCache,
        List<ServerCustomContentItem> contents,
        List<CardVisualBinding> visuals,
        List<CardTextOverride> texts,
        bool forceRefresh,
        CancellationToken ct)
    {
        string instanceId = (string)(bo["instanceId"] ?? bo["InstanceId"]);
        // 仅在解析 /api/mods/active-bindings 实例级映射表时允许用字典键作为 instanceId，避免把含下划线的 customId 误判为 instanceId
        if (isActiveBinding && string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(keyHint))
        {
            instanceId = keyHint;
        }

        string customId = (string)(bo["customId"] ?? bo["customCardId"] ?? bo["id"]) ?? (!isActiveBinding ? keyHint : null) ?? string.Empty;
        string modId = (string)(bo["modId"] ?? bo["ModId"]) ?? "server-mod";
        string baseTemplateId = (string)(bo["baseVisualTemplateId"] ?? bo["templateId"] ?? bo["TemplateId"]) ?? string.Empty;
        string cardType = (string)(bo["cardType"] ?? bo["type"]) ?? "Item";
        string size = (string)(bo["size"] ?? bo["Size"]) ?? "Medium";
        string nameZh = (string)(bo["nameZh"] ?? bo["titleZh"] ?? bo["title"]) ?? string.Empty;
        string titleEn = (string)(bo["titleEn"] ?? bo["nameEn"]) ?? string.Empty;
        string descZh = (string)(bo["descZh"] ?? bo["descriptionZh"] ?? bo["description"]) ?? string.Empty;
        string descEn = (string)(bo["descEn"] ?? bo["descriptionEn"]) ?? string.Empty;
        // 对齐服务端 CustomCardVisualBinding.CustomArtSha256 与 CustomBgArtSha256（D3-P1-11）
        string artSha = NormalizeSha((string)(bo["customArtSha256"] ?? bo["customArtHash"] ?? bo["artSha256"] ?? bo["sha256"]));
        string bgArtSha = NormalizeSha((string)(bo["customBgArtSha256"] ?? bo["bgArtSha256"]));

        string localArtPath = string.Empty;
        if (!string.IsNullOrEmpty(artSha))
        {
            if (!shaCache.TryGetValue(artSha, out localArtPath))
            {
                localArtPath = await EnsureAssetCachedAsync(artSha, forceRefresh, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(localArtPath))
                {
                    shaCache[artSha] = localArtPath;
                }
            }
        }

        string localBgArtPath = string.Empty;
        if (!string.IsNullOrEmpty(bgArtSha))
        {
            if (!shaCache.TryGetValue(bgArtSha, out localBgArtPath))
            {
                localBgArtPath = await EnsureAssetCachedAsync(bgArtSha, forceRefresh, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(localBgArtPath))
                {
                    shaCache[bgArtSha] = localBgArtPath;
                }
            }
        }

        contents.Add(new ServerCustomContentItem
        {
            ModId = modId,
            CustomId = customId,
            BaseVisualTemplateId = baseTemplateId,
            CardType = cardType,
            Size = size,
            NameZh = nameZh,
            DescZh = descZh,
            ArtSha256 = artSha,
            LocalArtPath = localArtPath,
        });

        bool? explicitPatch = (bool?)bo["isPatchOverride"];
        bool? explicitBorrow = (bool?)bo["isCustomBorrowShell"];
        bool isPatchOverride = explicitPatch
            ?? (!string.IsNullOrWhiteSpace(baseTemplateId)
                && string.Equals(customId, baseTemplateId, StringComparison.OrdinalIgnoreCase));
        bool isCustomBorrowShell = explicitBorrow
            ?? (!isPatchOverride && !string.IsNullOrWhiteSpace(customId));

        // 仅原位 Patch（isPatchOverride == true 或 customId == baseTemplateId）允许模板级覆盖
        bool isDirectTemplateOverride = string.IsNullOrWhiteSpace(instanceId)
            && !string.IsNullOrWhiteSpace(baseTemplateId)
            && (isPatchOverride || string.Equals(customId, baseTemplateId, StringComparison.OrdinalIgnoreCase));

        CardVisualTarget resolvedTarget = MapVisualTarget((string)bo["visualTarget"], cardType);
        CardMonsterPreviewInfo monsterPreview = ParseMonsterPreview(bo["monsterPreview"] as JObject);

        if (!string.IsNullOrEmpty(localArtPath) || monsterPreview != null)
        {
            visuals.Add(new CardVisualBinding
            {
                ModId = modId,
                InstanceId = instanceId,
                CustomCardId = customId,
                TemplateId = isDirectTemplateOverride ? baseTemplateId : null,
                Target = resolvedTarget,
                LocalImagePath = localArtPath,
                ContentSha256 = artSha,
                BgArtPath = string.IsNullOrEmpty(localBgArtPath) ? null : localBgArtPath,
                BgArtSha256 = string.IsNullOrEmpty(bgArtSha) ? null : bgArtSha,
                IsPatchOverride = isPatchOverride,
                IsCustomBorrowShell = isCustomBorrowShell,
                MonsterPreview = monsterPreview,
                DisableParallaxPremium = (bool?)bo["disableParallaxPremium"] ?? true,
                Priority = string.IsNullOrWhiteSpace(instanceId) ? 100 : 500,
            });
        }

        // D3-P1-11：独立奇遇/野怪背景图绑定
        if (!string.IsNullOrEmpty(localBgArtPath))
        {
            visuals.Add(new CardVisualBinding
            {
                ModId = modId,
                InstanceId = instanceId,
                CustomCardId = customId,
                TemplateId = isDirectTemplateOverride ? baseTemplateId : null,
                Target = CardVisualTarget.EncounterBackground,
                LocalImagePath = localBgArtPath,
                ContentSha256 = bgArtSha,
                IsPatchOverride = isPatchOverride,
                IsCustomBorrowShell = isCustomBorrowShell,
                DisableParallaxPremium = true,
                Priority = string.IsNullOrWhiteSpace(instanceId) ? 100 : 500,
            });
        }

        string[] activeTips = ReadStringArray(bo["activeTooltipsZh"] ?? bo["activeTooltips"]);
        string[] activeTipsEn = ReadStringArray(bo["activeTooltipsEn"]);
        string[] passiveTips = ReadStringArray(bo["passiveTooltipsZh"] ?? bo["passiveTooltips"]);
        string[] passiveTipsEn = ReadStringArray(bo["passiveTooltipsEn"]);
        string[] extraTags = ReadStringArray(bo["tags"] ?? bo["extraDisplayTags"] ?? bo["displayTags"]);
        Dictionary<string, string[]> enchantTips = ParseEnchantTooltipsMap(bo["enchantTooltipsZh"] as JObject);
        Dictionary<string, string[]> enchantTipsEn = ParseEnchantTooltipsMap(bo["enchantTooltipsEn"] as JObject);

        Dictionary<string, Dictionary<string, double>> tierAttrs = null;
        if (bo["tierAttributes"] is JObject taObj)
        {
            tierAttrs = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tp in taObj.Properties())
            {
                if (tp.Value is not JObject attrMap) continue;
                var inner = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var ap in attrMap.Properties())
                {
                    if (ap.Value != null
                        && double.TryParse(ap.Value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dv))
                        inner[ap.Name] = dv;
                }
                if (inner.Count > 0) tierAttrs[tp.Name] = inner;
            }
        }

        Dictionary<string, CardPreviewAttrOp[]> enchPreviewOps = null;
        if (bo["enchantmentPreviewOps"] is JObject epoObj)
        {
            enchPreviewOps = new Dictionary<string, CardPreviewAttrOp[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in epoObj.Properties())
            {
                if (ep.Value is not JArray opArr) continue;
                var opList = new List<CardPreviewAttrOp>(opArr.Count);
                foreach (var item in opArr)
                {
                    if (item is not JObject opObj) continue;
                    string attrName = ((string)opObj["attribute"])?.Trim();
                    if (string.IsNullOrEmpty(attrName)) continue;
                    double opVal = 0;
                    if (opObj["value"] != null)
                    {
                        double.TryParse(opObj["value"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out opVal);
                    }
                    double? srcMult = null;
                    if (opObj["sourceMultiplier"] != null &&
                        double.TryParse(opObj["sourceMultiplier"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sm))
                    {
                        srcMult = sm;
                    }
                    double? srcOffset = null;
                    if (opObj["sourceAddOffset"] != null &&
                        double.TryParse(opObj["sourceAddOffset"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double so))
                    {
                        srcOffset = so;
                    }
                    opList.Add(new CardPreviewAttrOp
                    {
                        Attribute = attrName,
                        Operation = ((string)opObj["operation"])?.Trim() ?? "Set",
                        Value = opVal,
                        SourceAttribute = ((string)opObj["sourceAttribute"])?.Trim(),
                        SourceMultiplier = srcMult,
                        SourceAddOffset = srcOffset,
                    });
                }
                enchPreviewOps[ep.Name] = opList.ToArray();
            }
        }

        CardCounterDisplayInfo[] counterDisplays = null;
        if (bo["counterDisplays"] is JArray cdArr && cdArr.Count > 0)
        {
            var cdList = new List<CardCounterDisplayInfo>(cdArr.Count);
            foreach (var item in cdArr)
            {
                if (item is not JObject cdo) continue;
                string syncAttr = ((string)(cdo["syncAttribute"] ?? cdo["attr"]))?.Trim();
                if (string.IsNullOrEmpty(syncAttr)) continue;
                cdList.Add(new CardCounterDisplayInfo
                {
                    LabelZh = ((string)(cdo["labelZh"] ?? cdo["label"]))?.Trim() ?? string.Empty,
                    SyncAttribute = syncAttr,
                    MaxValue = (int?)cdo["maxValue"],
                    MaxAttr = ((string)cdo["maxAttr"])?.Trim(),
                    ColorHex = ((string)(cdo["colorHex"] ?? cdo["accentColor"]))?.Trim(),
                    HideWhenZero = (bool?)cdo["hideWhenZero"] ?? false,
                    Style = ((string)cdo["style"])?.Trim(),
                    ValueType = ((string)(cdo["valueType"] ?? cdo["valueFormat"]))?.Trim(),
                    Format = ((string)cdo["format"])?.Trim(),
                    Unit = ((string)cdo["unit"])?.Trim(),
                    Position = ((string)cdo["position"])?.Trim(),
                });
            }
            if (cdList.Count > 0) counterDisplays = cdList.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(nameZh) || !string.IsNullOrWhiteSpace(titleEn)
            || !string.IsNullOrWhiteSpace(descZh) || !string.IsNullOrWhiteSpace(descEn)
            || (activeTips != null && activeTips.Length > 0)
            || (activeTipsEn != null && activeTipsEn.Length > 0)
            || (passiveTips != null && passiveTips.Length > 0)
            || (passiveTipsEn != null && passiveTipsEn.Length > 0)
            || (enchantTips != null && enchantTips.Count > 0)
            || (enchantTipsEn != null && enchantTipsEn.Count > 0)
            || (extraTags != null && extraTags.Length > 0)
            || (tierAttrs != null && tierAttrs.Count > 0)
            || (enchPreviewOps != null && enchPreviewOps.Count > 0)
            || (counterDisplays != null && counterDisplays.Length > 0)
            || monsterPreview != null
            || isCustomBorrowShell)
        {
            texts.Add(new CardTextOverride
            {
                ModId = modId,
                InstanceId = instanceId,
                CustomCardId = customId,
                TemplateId = isDirectTemplateOverride ? baseTemplateId : null,
                Locale = "zh-CN",
                Title = string.IsNullOrWhiteSpace(nameZh) ? null : nameZh,
                TitleEn = string.IsNullOrWhiteSpace(titleEn) ? null : titleEn,
                Description = string.IsNullOrWhiteSpace(descZh) ? null : descZh,
                DescriptionEn = string.IsNullOrWhiteSpace(descEn) ? null : descEn,
                ActiveTooltips = activeTips,
                ActiveTooltipsEn = activeTipsEn,
                PassiveTooltips = passiveTips,
                PassiveTooltipsEn = passiveTipsEn,
                EnchantmentTooltips = enchantTips,
                EnchantmentTooltipsEn = enchantTipsEn,
                ExtraDisplayTags = extraTags,
                TierAttributes = tierAttrs,
                EnchantmentPreviewOps = enchPreviewOps,
                CounterDisplays = counterDisplays,
                IsPatchOverride = isPatchOverride,
                IsCustomBorrowShell = isCustomBorrowShell,
                MonsterPreview = monsterPreview,
                Priority = string.IsNullOrWhiteSpace(instanceId) ? 100 : 500,
            });
        }
    }

    private void ApplySyncedVisualAndTextHandles(
        List<CardVisualBinding> visuals,
        List<CardTextOverride> texts,
        bool isActiveBinding,
        long expectedGeneration)
    {
        var visualReg = BazaarLauncherPlugin.Host?.VisualRegistry;
        var l10nReg = BazaarLauncherPlugin.Host?.LocalizationRegistry;

        lock (_sync)
        {
            if (!IsSyncGenerationCurrent(expectedGeneration)) return;

            var targetVisualList = isActiveBinding ? _syncedActiveBindingHandles : _syncedVisualHandles;
            DisposeHandlesLocked(targetVisualList);
            if (!isActiveBinding) DisposeHandlesLocked(_syncedCardTextHandles);

            if (visualReg != null)
            {
                foreach (var v in visuals)
                {
                    targetVisualList.Add(visualReg.RegisterVisualBinding(v));
                }
            }

            if (l10nReg != null)
            {
                var targetTextList = isActiveBinding ? _syncedActiveBindingHandles : _syncedCardTextHandles;
                foreach (var t in texts)
                {
                    targetTextList.Add(l10nReg.RegisterCardTextOverride(t));
                }
            }
        }

        visualReg?.InvalidateVisualCache(null);
        if (isActiveBinding)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                BazaarLauncher.Modules.CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
            });
        }
    }

    private async Task<string> EnsureAssetCachedAsync(string sha256, bool forceDownload, CancellationToken ct)
    {
        if (!SafePathAndZipGuard.TryNormalizeSha256Hex(sha256, out string validSha))
        {
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 拦截非法资产 SHA-256 哈希：{sha256}");
            }
            return string.Empty;
        }

        // D7-P1-02：优先命中 Hub 与 Launcher 共享 CAS 资产池（.png / .jpg / .jpeg），平滑兼容旧按服私有目录
        if (!forceDownload)
        {
            string[] probeDirs = { SharedAssetsCacheDir, LegacyServerAssetsCacheDir };
            string[] probeExts = { ".png", ".jpg", ".jpeg" };
            foreach (string dir in probeDirs)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                foreach (string ext in probeExts)
                {
                    string candidate;
                    try { candidate = SafePathAndZipGuard.ResolveSafeChildPath(dir, validSha + ext); }
                    catch { continue; }
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        var fi = new FileInfo(candidate);
                        if (fi.Length > 16 && fi.Length <= SafePathAndZipGuard.MaxAssetImageBytes)
                        {
                            byte[] existing = File.ReadAllBytes(candidate);
                            if (existing.Length > 16 && string.Equals(ComputeSha256Hex(existing), validSha, StringComparison.OrdinalIgnoreCase))
                            {
                                return candidate;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

        string tmp = string.Empty;
        try
        {
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/mods/assets/{Uri.EscapeDataString(validSha)}");
            NameAuth.ApplyAuthHeaders(req);

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            byte[] bytes = await SafePathAndZipGuard.ReadHttpContentWithLimitAsync(
                resp.Content,
                SafePathAndZipGuard.MaxAssetImageBytes,
                linked.Token).ConfigureAwait(false);
            if (bytes == null || bytes.Length < 16 || bytes.Length > SafePathAndZipGuard.MaxAssetImageBytes)
            {
                return string.Empty;
            }

            // P0-28 方案 A：拦截 WebP 并按魔数选择 .png 或 .jpg 存入统一共享 CAS 池
            if (!Modules.CardTextureCache.IsSupportedImageMagic(bytes))
            {
                if (Modules.CardTextureCache.IsWebpMagic(bytes))
                {
                    BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 跳过不支持的 WebP 资产 {validSha}（Unity 运行期仅支持 PNG/JPEG）");
                }
                else
                {
                    BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 跳过非合法 PNG/JPEG 资产 {validSha}");
                }
                return string.Empty;
            }

            string actualSha = ComputeSha256Hex(bytes);
            if (!string.Equals(actualSha, validSha, StringComparison.OrdinalIgnoreCase))
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 图片资产 SHA-256 校验失败: expected={validSha}, actual={actualSha}");
                return string.Empty;
            }

            string finalExt = (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ? ".jpg" : ".png";
            string targetPath = SafePathAndZipGuard.ResolveSafeChildPath(SharedAssetsCacheDir, validSha + finalExt);
            tmp = targetPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(targetPath))
            {
                File.Copy(tmp, targetPath, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
            else
            {
                File.Move(tmp, targetPath);
            }

            return targetPath;
        }
        catch (Exception ex)
        {
            try { if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
            BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 下载资产 {validSha} 失败: {ex.Message}");
            return string.Empty;
        }
    }

    private static void AppendCatalogSummary(JArray arr, string defaultType, List<ServerCustomContentItem> list)
    {
        if (arr == null) return;
        foreach (var token in arr)
        {
            if (token is not JObject jo) continue;
            string cid = (string)(jo["customId"] ?? jo["id"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cid)) continue;
            list.Add(new ServerCustomContentItem
            {
                ModId = (string)jo["modId"] ?? "server-mod",
                CustomId = cid,
                BaseVisualTemplateId = (string)(jo["baseVisualTemplateId"] ?? jo["templateId"]) ?? string.Empty,
                CardType = (string)(jo["cardType"] ?? jo["type"]) ?? defaultType,
                Size = (string)jo["size"] ?? "Medium",
                NameZh = (string)(jo["nameZh"] ?? jo["titleZh"] ?? jo["title"]) ?? cid,
                DescZh = (string)(jo["descZh"] ?? jo["descriptionZh"]) ?? string.Empty,
                ArtSha256 = NormalizeSha((string)(jo["customArtHash"] ?? jo["artSha256"])),
            });
        }
    }

    /// <summary>
    /// 统一映射服务端 VisualTarget（支持 plan_w2a Rev 2 枚举值 ItemArt/SkillIcon/EncounterPortrait/EncounterBackground
    /// 以及兼容别名 "card" / "event" / "monster"）。
    /// </summary>
    internal static CardVisualTarget MapVisualTarget(string explicitTarget, string cardType)
    {
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            string norm = explicitTarget.Trim();
            if (Enum.TryParse<CardVisualTarget>(norm, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
            if (string.Equals(norm, "card", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(cardType, "Skill", StringComparison.OrdinalIgnoreCase)
                    ? CardVisualTarget.SkillIcon
                    : CardVisualTarget.ItemArt;
            }
            if (string.Equals(norm, "event", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(norm, "monster", StringComparison.OrdinalIgnoreCase))
            {
                return CardVisualTarget.EncounterPortrait;
            }
        }

        if (string.Equals(cardType, "Skill", StringComparison.OrdinalIgnoreCase))
        {
            return CardVisualTarget.SkillIcon;
        }
        if (cardType != null &&
            (cardType.IndexOf("Encounter", StringComparison.OrdinalIgnoreCase) >= 0 ||
             string.Equals(cardType, "Event", StringComparison.OrdinalIgnoreCase)))
        {
            return CardVisualTarget.EncounterPortrait;
        }
        return CardVisualTarget.ItemArt;
    }

    internal static Dictionary<string, string[]> ParseEnchantTooltipsMap(JObject enchObj)
    {
        if (enchObj == null) return null;
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in enchObj.Properties())
        {
            if (ep.Value == null) continue;
            if (ep.Value.Type == JTokenType.String)
            {
                string single = ep.Value.Value<string>();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    map[ep.Name] = new[] { single.Trim() };
                }
            }
            else
            {
                var arr = ReadStringArray(ep.Value);
                if (arr != null && arr.Length > 0) map[ep.Name] = arr;
            }
        }
        return map.Count > 0 ? map : null;
    }

    internal static CardMonsterPreviewInfo ParseMonsterPreview(JObject mpObj)
    {
        if (mpObj == null) return null;
        var info = new CardMonsterPreviewInfo
        {
            MonsterId = ((string)mpObj["monsterId"])?.Trim() ?? string.Empty,
            NameZh = ((string)mpObj["nameZh"])?.Trim() ?? string.Empty,
            NameEn = ((string)mpObj["nameEn"])?.Trim(),
            Day = (int?)mpObj["day"] ?? 0,
            Level = (int?)mpObj["level"] ?? 0,
            HealthMax = (int?)mpObj["healthMax"] ?? 0,
            StartingTier = ((string)mpObj["startingTier"])?.Trim() ?? "Bronze",
            RewardGold = (int?)(mpObj["rewardGold"] ?? mpObj["goldReward"]) ?? 0,
            RewardXp = (int?)(mpObj["rewardXp"] ?? mpObj["xpReward"]) ?? 0,
            Items = ParseMonsterLoadoutArray(mpObj["items"] as JArray ?? mpObj["boardItems"] as JArray),
            Skills = ParseMonsterLoadoutArray(mpObj["skills"] as JArray ?? mpObj["boardSkills"] as JArray),
        };
        if (info.HealthMax <= 0 && info.Items.Length == 0 && info.Skills.Length == 0 && info.Level <= 0)
        {
            return null;
        }
        return info;
    }

    private static CardMonsterLoadoutItemInfo[] ParseMonsterLoadoutArray(JArray arr)
    {
        if (arr == null || arr.Count == 0) return Array.Empty<CardMonsterLoadoutItemInfo>();
        var list = new List<CardMonsterLoadoutItemInfo>(arr.Count);
        foreach (var token in arr)
        {
            if (token is not JObject jo) continue;
            string cardId = ((string)(jo["cardId"] ?? jo["customCardId"] ?? jo["templateId"]))?.Trim() ?? string.Empty;
            string wireId = ((string)(jo["wireTemplateId"] ?? jo["templateId"]))?.Trim();
            if (string.IsNullOrEmpty(cardId) && string.IsNullOrEmpty(wireId)) continue;
            list.Add(new CardMonsterLoadoutItemInfo
            {
                CardId = cardId,
                WireTemplateId = wireId,
                Tier = ((string)jo["tier"])?.Trim() ?? "Bronze",
                Size = ((string)jo["size"])?.Trim(),
                Enchantment = ((string)jo["enchantment"])?.Trim(),
                TitleZh = ((string)jo["titleZh"])?.Trim(),
                TitleEn = ((string)jo["titleEn"])?.Trim(),
            });
        }
        return list.ToArray();
    }

    private static string[] ReadStringArray(JToken token)
    {
        if (token is not JArray arr || arr.Count == 0) return null;
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            string s = item?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string NormalizeSha(string raw)
    {
        return SafePathAndZipGuard.TryNormalizeSha256Hex(raw, out string validSha)
            ? validSha
            : string.Empty;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private static void AtomicWriteText(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void DisposeHandlesLocked(List<IDisposable> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            try { handles[i]?.Dispose(); } catch { }
        }
        handles.Clear();
    }
}
