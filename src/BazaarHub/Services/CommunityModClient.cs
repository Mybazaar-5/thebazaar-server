using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BazaarHub.Services;

public sealed record HubCommunityModItem(
    string ModId,
    string Name,
    string Version,
    string Author,
    string UploaderName,
    string Description,
    int CustomCardCount,
    int CustomEventCount,
    int AssetCount,
    int DownloadCount,
    bool IsServerDefault,
    bool IsInCommunityStore,
    string DownloadUrl);

/// <summary>
/// BazaarHub 社区 Mod 客户端：拉取服务器社区 Mod 列表、安全下载解压到本地 BepInEx\mods\、以及打包上传本地 Mod。
/// 全量接入 SafePathAndZipGuard 防路径穿越、Linux 反斜杠目录崩溃、危险扩展名 Fail-Closed 与 Zip Bomb。
/// </summary>
public sealed class CommunityModClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public CommunityModClient(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            _ownsClient = true;
        }
    }

    public async Task<(bool Ok, List<HubCommunityModItem> Mods, string Message)> FetchListAsync(
        string serverBase,
        string? playerName = null,
        CancellationToken ct = default)
    {
        string normalized = NormalizeBase(serverBase);
        if (string.IsNullOrEmpty(normalized))
            return (false, new List<HubCommunityModItem>(), "请先选择有效的服务器地址");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{normalized}/api/community/mods");
            string encodedName = EncodeHeaderPlayerName(playerName);
            if (!string.IsNullOrEmpty(encodedName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", encodedName);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, new List<HubCommunityModItem>(), $"获取失败（HTTP {(int)resp.StatusCode}）");

            var list = new List<HubCommunityModItem>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("mods", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string modId = GetStr(el, "modId");
                    if (string.IsNullOrWhiteSpace(modId)) continue;
                    list.Add(new HubCommunityModItem(
                        ModId: modId,
                        Name: GetStr(el, "name", modId),
                        Version: GetStr(el, "version", "1.0.0"),
                        Author: GetStr(el, "author"),
                        UploaderName: GetStr(el, "uploaderName"),
                        Description: GetStr(el, "description"),
                        CustomCardCount: GetInt(el, "customCardCount"),
                        CustomEventCount: GetInt(el, "customEventCount"),
                        AssetCount: GetInt(el, "assetCount"),
                        DownloadCount: GetInt(el, "downloadCount"),
                        IsServerDefault: GetBool(el, "isServerDefault"),
                        IsInCommunityStore: GetBool(el, "isInCommunityStore"),
                        DownloadUrl: GetStr(el, "downloadUrl")));
                }
            }
            return (true, list, $"已获取到 {list.Count} 个可用 Mod");
        }
        catch (Exception ex)
        {
            return (false, new List<HubCommunityModItem>(), $"连接服务器失败：{ex.Message}");
        }
    }

    public async Task<(bool Ok, string Message, string? SavedDir)> DownloadToLocalAsync(
        string serverBase,
        string modId,
        string gameDir,
        CancellationToken ct = default)
    {
        string normalized = NormalizeBase(serverBase);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(gameDir))
            return (false, "参数不完整", null);

        string cleanId = modId.Trim();
        if (!SafePathAndZipGuard.IsValidModId(cleanId))
        {
            return (false, $"非法的 Mod ID（已拦截路径穿越风险）：{cleanId}", null);
        }

        try
        {
            using var resp = await _http.GetAsync(
                $"{normalized}/api/community/mods/{Uri.EscapeDataString(cleanId)}/download",
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"下载失败（HTTP {(int)resp.StatusCode}）", null);

            byte[] zipBytes = await SafePathAndZipGuard.ReadHttpContentWithLimitAsync(
                resp.Content,
                SafePathAndZipGuard.MaxCommunityZipBytes,
                ct).ConfigureAwait(false);
            if (zipBytes.Length == 0)
                return (false, "下载到的文件为空", null);

            string modsDir = Path.Combine(gameDir, "BepInEx", "mods");
            Directory.CreateDirectory(modsDir);

            // 先安全校验并原子解压（Fail-Closed：非法 ZIP / 危险扩展名 / Zip Bomb 绝不写入归档目录）
            string targetDir = SafePathAndZipGuard.ExtractModZipSafely(zipBytes, modsDir, cleanId);

            string archiveDir = Path.Combine(gameDir, "BepInEx", "config", "BazaarModCache", "downloads");
            Directory.CreateDirectory(archiveDir);
            string archivePath = SafePathAndZipGuard.ResolveSafeChildPath(archiveDir, $"{cleanId}.bazaarmod.zip");
            string tmpArchive = $"{archivePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tmpArchive, zipBytes, ct).ConfigureAwait(false);
                File.Move(tmpArchive, archivePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmpArchive)) File.Delete(tmpArchive); } catch { }
                throw;
            }

            return (true, $"已下载并保存到 {targetDir}", targetDir);
        }
        catch (Exception ex)
        {
            return (false, $"下载失败：{ex.Message}", null);
        }
    }

    public async Task<(bool Ok, string Message)> UploadModAsync(
        string serverBase,
        string localPathOrDir,
        string playerName,
        CancellationToken ct = default)
    {
        string normalized = NormalizeBase(serverBase);
        if (string.IsNullOrEmpty(normalized))
            return (false, "请先选择有效的服务器地址");

        try
        {
            byte[] zipBytes;
            string fileName;
            if (Directory.Exists(localPathOrDir))
            {
                string manifestPath = Path.Combine(localPathOrDir, "mod.json");
                if (!File.Exists(manifestPath))
                    return (false, "所选文件夹内缺少 mod.json");
                string modId = ReadModId(manifestPath, Path.GetFileName(localPathOrDir));
                if (!SafePathAndZipGuard.IsValidModId(modId))
                    return (false, $"Mod ID 不合法（仅允许字母、数字、点、下划线、连字符，且不得含 .. 或系统保留名）：{modId}");
                zipBytes = PackFolderToZip(localPathOrDir, modId);
                fileName = $"{modId}.bazaarmod.zip";
            }
            else if (File.Exists(localPathOrDir))
            {
                zipBytes = await File.ReadAllBytesAsync(localPathOrDir, ct).ConfigureAwait(false);
                fileName = Path.GetFileName(localPathOrDir);
            }
            else
            {
                return (false, "找不到指定的文件或文件夹");
            }

            if (zipBytes.Length > 10 * 1024 * 1024)
                return (false, "文件超过 10 MB 上传限制");

            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(zipBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "file", fileName);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{normalized}/api/community/mods/upload")
            {
                Content = form
            };
            string encodedUploader = EncodeHeaderPlayerName(playerName, Environment.UserName);
            if (!string.IsNullOrEmpty(encodedUploader))
                req.Headers.TryAddWithoutValidation("X-Player-Name", encodedUploader);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                bool ok = resp.IsSuccessStatusCode && (!doc.RootElement.TryGetProperty("ok", out var okEl) || okEl.GetBoolean());
                string msg = GetStr(doc.RootElement, "messageZh");
                if (string.IsNullOrWhiteSpace(msg)) msg = GetStr(doc.RootElement, "error");
                if (string.IsNullOrWhiteSpace(msg)) msg = ok ? "上传成功" : $"上传失败（HTTP {(int)resp.StatusCode}）";
                return (ok, msg);
            }
            catch
            {
                return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? "上传成功" : $"上传失败（HTTP {(int)resp.StatusCode}）");
            }
        }
        catch (Exception ex)
        {
            return (false, $"上传出错：{ex.Message}");
        }
    }

    private static string NormalizeBase(string url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private static string EncodeHeaderPlayerName(string? playerName, string? fallback = null)
    {
        string raw = string.IsNullOrWhiteSpace(playerName)
            ? (fallback ?? string.Empty).Trim()
            : playerName.Trim();
        return string.IsNullOrEmpty(raw) ? string.Empty : Uri.EscapeDataString(raw);
    }

    private static string GetStr(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetInt32(out int n) ? n : 0;

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

    private static string ReadModId(string manifestPath, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                string? id = idEl.GetString();
                if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            }
        }
        catch { }
        return fallback;
    }

    private static byte[] PackFolderToZip(string sourceDir, string modId)
    {
        if (!SafePathAndZipGuard.IsValidModId(modId))
            throw new InvalidOperationException($"非法的 Mod ID：{modId}");

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

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
