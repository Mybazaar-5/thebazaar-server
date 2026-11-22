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

    /// <summary>切换服务器后清空旧服内存绑定并立即重新拉取新服清单。</summary>
    internal void OnServerChanged()
    {
        ClearSyncedServerMods();
        lock (_sync)
        {
            _communityMods = new List<CommunityModEntry>();
            _lastModSetHash = string.Empty;
            _lastOverlaySha256 = string.Empty;
            _lastSyncedRoomModKey = string.Empty;
        }
        TriggerFullSyncInBackground(forceDownload: false);
    }

    /// <summary>卸载当前已注册的所有服务器/房间同步 Mod 视觉与文案覆盖。</summary>
    internal void ClearSyncedServerMods()
    {
        lock (_sync)
        {
            DisposeHandlesLocked(_syncedVisualHandles);
            DisposeHandlesLocked(_syncedActiveBindingHandles);
            DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
            DisposeHandlesLocked(_syncedRoomBundleTextHandles);
            DisposeHandlesLocked(_syncedCardTextHandles);
            _serverMods = new List<ServerModEntry>();
            _customContents = new List<ServerCustomContentItem>();
            _donorTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSyncedRoomModKey = string.Empty;
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
            lock (_sync)
            {
                DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
                DisposeHandlesLocked(_syncedRoomBundleTextHandles);
                _lastSyncedRoomModKey = string.Empty;
            }
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

        string path = "/api/community/mods/bundle?modIds=" + Uri.EscapeDataString(string.Join(",", cleanIds));
        var resp = await NameAuth.GetJsonAsync(path, ct).ConfigureAwait(false);
        if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
        {
            return (false, "拉取房间 Mod 资源失败：" + (resp.Error ?? "网络错误"));
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
                DisposeHandlesLocked(_syncedRoomBundleVisualHandles);
                DisposeHandlesLocked(_syncedRoomBundleTextHandles);
                _syncedRoomBundleVisualHandles.AddRange(newVisHandles);
                _syncedRoomBundleTextHandles.AddRange(newTextHandles);
                foreach (var item in dummyContents)
                {
                    if (!string.IsNullOrWhiteSpace(item.BaseVisualTemplateId))
                        _donorTemplateIds.Add(item.BaseVisualTemplateId.Trim());
                }
                _lastSyncedRoomModKey = key;
            }

            int missing = (root["missingModIds"] as JArray)?.Count ?? 0;
            string note = missing > 0 ? $"（其中 {missing} 个在服务器未找到）" : string.Empty;
            return (true, $"已同步本房 {cleanIds.Count} 个 Mod（贴图 {pngCount} 张，绑定 {visualBindings.Count} 条）{note}");
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
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/community/mods/{Uri.EscapeDataString(cleanId)}/download");
            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"下载失败（HTTP {(int)resp.StatusCode}）", string.Empty, string.Empty);
            }

            byte[] zipBytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (zipBytes == null || zipBytes.Length == 0)
                return (false, "下载到的压缩包为空", string.Empty, string.Empty);

            string zipPath = Path.Combine(LocalZipArchiveRoot, $"{cleanId}.bazaarmod.zip");
            File.WriteAllBytes(zipPath, zipBytes);

            string extractTarget = Path.Combine(LocalModsDownloadRoot, cleanId);
            ExtractModZipSafely(zipBytes, extractTarget, cleanId);

            return (true, $"已下载并保存到本地：{extractTarget}", zipPath, extractTarget);
        }
        catch (Exception ex)
        {
            return (false, "下载 Mod 出错：" + ex.Message, string.Empty, string.Empty);
        }
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
                // 1. 子目录形式的 Mod（含 mod.json）
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string manifestPath = Path.Combine(dir, "mod.json");
                    if (!File.Exists(manifestPath)) continue;
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
                string manifestPath = Path.Combine(pathOrDir, "mod.json");
                if (!File.Exists(manifestPath))
                    return (false, "该目录缺少 mod.json，无法作为 Mod 上传");
                var meta = ReadModSummaryFromManifest(manifestPath, Path.GetFileName(pathOrDir));
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

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(zipBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(fileContent, "file", uploadName);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/community/mods/upload")
            {
                Content = content
            };
            string token = NameAuth.CurrentAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            string playerName = PlayerIdentity.ResolveName();
            if (!string.IsNullOrWhiteSpace(playerName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", playerName);

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, ct).ConfigureAwait(false);
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

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/community/mods/{Uri.EscapeDataString(modId.Trim())}");
            string token = NameAuth.CurrentAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            string playerName = PlayerIdentity.ResolveName();
            if (!string.IsNullOrWhiteSpace(playerName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", playerName);

            using var resp = await HttpClientHub.DownloadClient.SendAsync(req, ct).ConfigureAwait(false);
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
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".vbs", ".msi", ".so", ".dylib"
        };

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
                if (forbidden.Contains(Path.GetExtension(rel)))
                    continue;

                var entry = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    private static void ExtractModZipSafely(byte[] zipBytes, string targetDir, string modId)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".vbs", ".msi", ".so", ".dylib"
        };

        string tempDir = targetDir + ".tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            Directory.CreateDirectory(tempDir);
            string guard = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        continue;
                    string rel = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..")) continue;
                    if (forbidden.Contains(Path.GetExtension(rel))) continue;

                    // 剥离顶层 <modId>/ 前缀避免嵌套双层目录
                    string prefix = modId + "/";
                    if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    string dst = Path.GetFullPath(Path.Combine(tempDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dst.StartsWith(guard, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using var src = entry.Open();
                    using var fs = File.Create(dst);
                    src.CopyTo(fs);
                }
            }

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            Directory.Move(tempDir, targetDir);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
