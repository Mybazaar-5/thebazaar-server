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
/// BazaarHub 社区 Mod 客户端：拉取服务器社区 Mod 列表、下载解压到本地 BepInEx\mods\、以及打包上传本地 Mod。
/// </summary>
public sealed class CommunityModClient : IDisposable
{
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".js", ".vbs", ".msi", ".so", ".dylib"
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

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
            if (!string.IsNullOrWhiteSpace(playerName))
                req.Headers.TryAddWithoutValidation("X-Player-Name", playerName.Trim());

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
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(modId))
            return (false, "参数不完整", null);

        string cleanId = modId.Trim();
        try
        {
            using var resp = await _http.GetAsync($"{normalized}/api/community/mods/{Uri.EscapeDataString(cleanId)}/download", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"下载失败（HTTP {(int)resp.StatusCode}）", null);

            byte[] zipBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (zipBytes.Length == 0)
                return (false, "下载到的文件为空", null);

            string modsDir = Path.Combine(gameDir, "BepInEx", "mods");
            Directory.CreateDirectory(modsDir);
            string targetDir = Path.Combine(modsDir, cleanId);
            ExtractSafely(zipBytes, targetDir, cleanId);

            string archiveDir = Path.Combine(gameDir, "BepInEx", "config", "BazaarModCache", "downloads");
            Directory.CreateDirectory(archiveDir);
            await File.WriteAllBytesAsync(Path.Combine(archiveDir, $"{cleanId}.bazaarmod.zip"), zipBytes, ct).ConfigureAwait(false);

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
            req.Headers.TryAddWithoutValidation("X-Player-Name", string.IsNullOrWhiteSpace(playerName) ? Environment.UserName : playerName.Trim());

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
                if (ForbiddenExtensions.Contains(Path.GetExtension(rel)))
                    continue;
                var entry = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    private static void ExtractSafely(byte[] zipBytes, string targetDir, string modId)
    {
        string tempDir = targetDir + ".tmp_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(tempDir);
            string guard = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
                    string rel = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..")) continue;
                    if (ForbiddenExtensions.Contains(Path.GetExtension(rel))) continue;
                    if (rel.StartsWith(modId + "/", StringComparison.OrdinalIgnoreCase))
                        rel = rel[(modId.Length + 1)..];
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    string dst = Path.GetFullPath(Path.Combine(tempDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dst.StartsWith(guard, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    using var src = entry.Open();
                    using var fs = File.Create(dst);
                    src.CopyTo(fs);
                }
            }
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.Move(tempDir, targetDir);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    public void Dispose() => _http.Dispose();
}
