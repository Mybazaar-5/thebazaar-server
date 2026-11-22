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
internal sealed class ModContentSyncClient
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
    private readonly List<IDisposable> _syncedCardTextHandles = new();
    private List<ServerModEntry> _serverMods = new();
    private List<ServerCustomContentItem> _customContents = new();
    private HashSet<string> _donorTemplateIds = new(StringComparer.OrdinalIgnoreCase);
    private int _syncInFlight;
    private int _activeBindingsInFlight;
    private int _activeBindingsResyncQueued;
    private long _lastAutoBindingsTickMs;
    private string _lastModSetHash = string.Empty;
    private string _lastOverlaySha256 = string.Empty;
    private string _lastSyncSummary = "尚未同步";
    private DateTime _lastSyncUtc;

    internal static ModContentSyncClient Instance { get; } = new();

    internal string LastModSetHash => _lastModSetHash;
    internal string LastOverlaySha256 => _lastOverlaySha256;
    internal string LastSyncSummary => _lastSyncSummary;
    internal DateTime LastSyncUtc => _lastSyncUtc;

    internal string AssetsCacheDir
    {
        get
        {
            string dir = Path.Combine(ModuleHost.CacheDirectory, "assets");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    internal string OverlayCachePath => Path.Combine(ModuleHost.CacheDirectory, "custom_zh_overlay.json");

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

    /// <summary>触发后台全量异步同步（/api/mods/manifest + /api/localization/overlay + /api/mods/active-bindings）。</summary>
    internal void TriggerFullSyncInBackground(bool forceDownload = false)
    {
        if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAllAsync(forceDownload, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[ModSync] 后台同步异常: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _syncInFlight, 0);
            }
        });
    }

    /// <summary>同步当前活跃对局的自定义实例视觉与文案绑定（/api/mods/active-bindings）。</summary>
    internal void TriggerActiveBindingsSyncInBackground(string runId = null)
    {
        if (Interlocked.CompareExchange(ref _activeBindingsInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _activeBindingsResyncQueued, 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncActiveBindingsAsync(runId, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[ModSync] 同步活跃对局绑定失败: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _activeBindingsInFlight, 0);
                if (Interlocked.Exchange(ref _activeBindingsResyncQueued, 0) != 0)
                {
                    TriggerActiveBindingsSyncInBackground(runId);
                }
            }
        });
    }

    /// <summary>当 3D 棋盘/商店新刷出的卡牌命中任意自定义卡底板 GUID 且尚未绑定时，按需触发防抖拉取 /api/mods/active-bindings。</summary>
    internal void NotifyUnboundInstanceSeen(string instanceId, string templateId)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(templateId)) return;
        bool isDonor;
        lock (_sync)
        {
            isDonor = _donorTemplateIds.Contains(templateId);
        }
        if (!isDonor) return;

        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long prevMs = Interlocked.Read(ref _lastAutoBindingsTickMs);
        if (nowMs - prevMs < 250)
        {
            Interlocked.Exchange(ref _activeBindingsResyncQueued, 1);
            return;
        }
        Interlocked.Exchange(ref _lastAutoBindingsTickMs, nowMs);
        TriggerActiveBindingsSyncInBackground();
    }

    /// <summary>执行一次完整的 Mod 清单、PNG 资产、四层中文词典和活跃绑定同步。</summary>
    internal async Task<bool> SyncAllAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        int downloadedPngs = 0;
        int bindingCount = 0;
        int enabledMods = 0;

        // 1. 拉取外挂四层中文词典 /api/localization/overlay（回退 /api/localization/zh-overlay）
        await SyncLocalizationOverlayAsync(forceRefresh, ct).ConfigureAwait(false);

        // 2. 拉取服务端 Mod 清单 /api/mods/manifest
        var manifestResp = await NameAuth.GetJsonAsync("/api/mods/manifest", ct).ConfigureAwait(false);
        if (manifestResp.Ok && !string.IsNullOrWhiteSpace(manifestResp.Body))
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

            // 按 SHA-256 增量下载自定义 PNG 资产
            var shaToLocalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root["assets"] is JArray assetsArr)
            {
                foreach (var token in assetsArr)
                {
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

            // 额外解析 customEvents / customSkills 摘要目录用于工坊展示
            AppendCatalogSummary(root["customSkills"] as JArray, "Skill", parsedContents);
            AppendCatalogSummary(root["customEvents"] as JArray, "EventEncounter", parsedContents);

            ApplySyncedVisualAndTextHandles(newVisualBindings, newTextOverrides, isActiveBinding: false);
            bindingCount = newVisualBindings.Count;

            lock (_sync)
            {
                _serverMods = parsedMods;
                _customContents = parsedContents;
                var donors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in parsedContents)
                {
                    if (!string.IsNullOrWhiteSpace(item.BaseVisualTemplateId))
                        donors.Add(item.BaseVisualTemplateId.Trim());
                }
                _donorTemplateIds = donors;
            }
            _lastModSetHash = modSetHash;
        }

        // 3. 拉取当前活跃对局实例绑定
        await SyncActiveBindingsAsync(null, ct).ConfigureAwait(false);

        _lastSyncUtc = DateTime.UtcNow;
        _lastSyncSummary = $"已同步 {enabledMods} 个 Mod / {bindingCount} 条视觉绑定 / 缓存 {downloadedPngs} 张贴图";

        BazaarLauncherPlugin.Host?.EventBus.Publish(new ModAssetsSyncedModEvent(
            enabledMods,
            bindingCount,
            downloadedPngs,
            _lastModSetHash));

        return true;
    }

    /// <summary>同步当前对局实例级绑定：优先 /api/mods/active-bindings，兼容 /api/runs/{runId}/visual-bindings。</summary>
    internal async Task SyncActiveBindingsAsync(string runId = null, CancellationToken ct = default)
    {
        string path = string.IsNullOrWhiteSpace(runId)
            ? "/api/mods/active-bindings"
            : $"/api/mods/active-bindings?runId={Uri.EscapeDataString(runId)}";

        var resp = await NameAuth.GetJsonAsync(path, ct).ConfigureAwait(false);
        if (!resp.Ok && !string.IsNullOrWhiteSpace(runId))
        {
            resp = await NameAuth.GetJsonAsync($"/api/runs/{Uri.EscapeDataString(runId)}/visual-bindings", ct).ConfigureAwait(false);
        }
        if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body)) return;

        var root = JObject.Parse(resp.Body);
        var shaToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visuals = new List<CardVisualBinding>();
        var texts = new List<CardTextOverride>();
        var dummyContents = new List<ServerCustomContentItem>();

        JToken mapToken = root["bindings"] ?? root["activeBindings"] ?? root;
        if (mapToken is JObject mapObj)
        {
            foreach (var prop in mapObj.Properties())
            {
                if (prop.Value is not JObject bo) continue;
                await ExtractBindingObjectAsync(bo, prop.Name, isActiveBinding: true, shaToPath, dummyContents, visuals, texts, false, ct).ConfigureAwait(false);
            }
        }
        else if (mapToken is JArray arr)
        {
            foreach (var token in arr)
            {
                if (token is not JObject bo) continue;
                await ExtractBindingObjectAsync(bo, null, isActiveBinding: true, shaToPath, dummyContents, visuals, texts, false, ct).ConfigureAwait(false);
            }
        }

        ApplySyncedVisualAndTextHandles(visuals, texts, isActiveBinding: true);
    }

    private async Task SyncLocalizationOverlayAsync(bool forceRefresh, CancellationToken ct)
    {
        var resp = await NameAuth.GetJsonAsync("/api/localization/overlay", ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            resp = await NameAuth.GetJsonAsync("/api/localization/zh-overlay?locale=zh-CN", ct).ConfigureAwait(false);
        }

        if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
        {
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
        string descZh = (string)(bo["descZh"] ?? bo["descriptionZh"] ?? bo["description"]) ?? string.Empty;
        // 对齐服务端 CustomCardVisualBinding.CustomArtSha256 (JSON 字段名 customArtSha256)
        string artSha = NormalizeSha((string)(bo["customArtSha256"] ?? bo["customArtHash"] ?? bo["artSha256"] ?? bo["sha256"]));

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

        // 若 customId 借用原版载体模板（customId != baseTemplateId），模板级绑定严禁把 TemplateId 设为 baseTemplateId，
        // 否则会劫持正常对局里原版载体卡的卡图与文案；仅在显式覆盖原版模板或实例级绑定时匹配。
        bool isDirectTemplateOverride = string.IsNullOrWhiteSpace(instanceId)
            && !string.IsNullOrWhiteSpace(baseTemplateId)
            && string.Equals(customId, baseTemplateId, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(localArtPath))
        {
            visuals.Add(new CardVisualBinding
            {
                ModId = modId,
                InstanceId = instanceId,
                CustomCardId = customId,
                TemplateId = isDirectTemplateOverride ? baseTemplateId : null,
                Target = ParseVisualTarget(cardType, (string)bo["visualTarget"]),
                LocalImagePath = localArtPath,
                ContentSha256 = artSha,
                DisableParallaxPremium = (bool?)bo["disableParallaxPremium"] ?? true,
                Priority = string.IsNullOrWhiteSpace(instanceId) ? 100 : 500,
            });
        }

        string[] activeTips = ReadStringArray(bo["activeTooltipsZh"] ?? bo["activeTooltips"]);
        string[] passiveTips = ReadStringArray(bo["passiveTooltipsZh"] ?? bo["passiveTooltips"]);
        string[] extraTags = ReadStringArray(bo["extraDisplayTags"] ?? bo["displayTags"]);
        Dictionary<string, string[]> enchantTips = null;
        if (bo["enchantTooltipsZh"] is JObject enchObj)
        {
            enchantTips = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in enchObj.Properties())
            {
                var arr = ReadStringArray(ep.Value);
                if (arr != null && arr.Length > 0) enchantTips[ep.Name] = arr;
            }
        }

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
                    if (ap.Value != null && double.TryParse(ap.Value.ToString(), out double dv))
                        inner[ap.Name] = dv;
                }
                if (inner.Count > 0) tierAttrs[tp.Name] = inner;
            }
        }

        if (!string.IsNullOrWhiteSpace(nameZh) || !string.IsNullOrWhiteSpace(descZh)
            || (activeTips != null && activeTips.Length > 0)
            || (passiveTips != null && passiveTips.Length > 0)
            || (enchantTips != null && enchantTips.Count > 0)
            || (tierAttrs != null && tierAttrs.Count > 0))
        {
            texts.Add(new CardTextOverride
            {
                ModId = modId,
                InstanceId = instanceId,
                CustomCardId = customId,
                TemplateId = isDirectTemplateOverride ? baseTemplateId : null,
                Locale = "zh-CN",
                Title = string.IsNullOrWhiteSpace(nameZh) ? null : nameZh,
                Description = string.IsNullOrWhiteSpace(descZh) ? null : descZh,
                ActiveTooltips = activeTips,
                PassiveTooltips = passiveTips,
                EnchantmentTooltips = enchantTips,
                ExtraDisplayTags = extraTags,
                TierAttributes = tierAttrs,
                Priority = string.IsNullOrWhiteSpace(instanceId) ? 100 : 500,
            });
        }
    }

    private void ApplySyncedVisualAndTextHandles(
        List<CardVisualBinding> visuals,
        List<CardTextOverride> texts,
        bool isActiveBinding)
    {
        var visualReg = BazaarLauncherPlugin.Host?.VisualRegistry;
        var l10nReg = BazaarLauncherPlugin.Host?.LocalizationRegistry;

        lock (_sync)
        {
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
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length < 16) return string.Empty;

        string targetPath = Path.Combine(AssetsCacheDir, sha256 + ".png");
        if (!forceDownload && File.Exists(targetPath))
        {
            try
            {
                byte[] existing = File.ReadAllBytes(targetPath);
                if (existing.Length > 16 && string.Equals(ComputeSha256Hex(existing), sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return targetPath;
                }
            }
            catch { }
        }

        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

        try
        {
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/mods/assets/{Uri.EscapeDataString(sha256)}");
            NameAuth.ApplyAuthHeaders(req);

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes == null || bytes.Length < 16 || bytes.Length > 8 * 1024 * 1024)
            {
                return string.Empty;
            }

            string actualSha = ComputeSha256Hex(bytes);
            if (!string.Equals(actualSha, sha256, StringComparison.OrdinalIgnoreCase))
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] PNG 资产 SHA-256 校验失败: expected={sha256}, actual={actualSha}");
                return string.Empty;
            }

            string tmp = targetPath + ".tmp";
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
            BazaarLauncherPlugin.Log?.LogWarning($"[ModSync] 下载资产 {sha256} 失败: {ex.Message}");
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

    private static CardVisualTarget ParseVisualTarget(string cardType, string explicitTarget)
    {
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            if (Enum.TryParse<CardVisualTarget>(explicitTarget, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        if (string.Equals(cardType, "Skill", StringComparison.OrdinalIgnoreCase))
        {
            return CardVisualTarget.SkillIcon;
        }
        if (cardType != null && cardType.IndexOf("Encounter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return CardVisualTarget.EncounterPortrait;
        }
        return CardVisualTarget.ItemArt;
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
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(7);
        }
        return s.Trim().ToLowerInvariant();
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
