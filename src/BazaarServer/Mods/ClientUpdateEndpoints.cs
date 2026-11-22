using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

/// <summary>
/// 客户端增量热更新分发端点（供 BazaarHub / 启动器一键拉取最新 BazaarLauncher.dll、BazaarModSdk.dll 等载荷）：
/// - GET /api/client-update/manifest      ：扫描 client-payload/ 或 dist/client/payload/，输出全部文件 SHA-256 清单与整体校验和
/// - GET /api/client-update/files/{*name} ：流式下发指定载荷文件（严格路径穿越拦截 + ETag 协商缓存）
/// </summary>
public static class ClientUpdateEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record CachedHashEntry(long Length, long LastWriteTicksUtc, string Sha256);

    private static readonly ConcurrentDictionary<string, CachedHashEntry> HashCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Map(WebApplication app, string dataRoot)
    {
        string payloadDir = ResolveClientPayloadDirectory(dataRoot);

        app.MapGet("/api/client-update/manifest", (HttpContext ctx) =>
        {
            string activeDir = Directory.Exists(payloadDir) ? payloadDir : ResolveClientPayloadDirectory(dataRoot);
            var files = new List<object>();
            var hashInput = new StringBuilder();

            if (Directory.Exists(activeDir))
            {
                foreach (string fullPath in Directory.GetFiles(activeDir, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrEmpty(fileName) || fileName.StartsWith('.'))
                        continue;

                    var info = new FileInfo(fullPath);
                    string relPath = Path.GetRelativePath(activeDir, fullPath).Replace('\\', '/');
                    string sha256 = GetOrComputeSha256(fullPath, info.Length, info.LastWriteTimeUtc.Ticks);

                    hashInput.Append(relPath).Append(':').Append(sha256).Append('\n');
                    files.Add(new
                    {
                        name = fileName,
                        relativePath = relPath,
                        sha256,
                        bytes = info.Length,
                        lastModifiedUtc = info.LastWriteTimeUtc,
                        downloadUrl = $"/api/client-update/files/{Uri.EscapeDataString(relPath).Replace("%2F", "/")}",
                    });
                }
            }

            string manifestSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()))).ToLowerInvariant();
            string etag = $"\"{manifestSha256}\"";

            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";

            return Results.Json(new
            {
                schemaVersion = 1,
                serverVersion = "15.3.0",
                launcherVersion = "15.3.0",
                hubVersion = "15.3.0",
                generatedAtUtc = DateTimeOffset.UtcNow,
                payloadDirAvailable = Directory.Exists(activeDir),
                manifestSha256,
                fileCount = files.Count,
                files,
            }, JsonOpts);
        });

        app.MapGet("/api/client-update/files/{*name}", (HttpContext ctx, string name) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.Json(new { ok = false, error = "文件名不能为空" }, JsonOpts, statusCode: StatusCodes.Status400BadRequest);
            }

            string activeDir = Directory.Exists(payloadDir) ? payloadDir : ResolveClientPayloadDirectory(dataRoot);
            if (!Directory.Exists(activeDir))
            {
                return Results.Json(new { ok = false, error = "服务端尚未部署客户端热更新载荷目录" }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            // 严格防范路径穿越（禁止 ..、盘符或绝对根路径）
            string normalizedRel = name.Replace('\\', '/').TrimStart('/');
            if (normalizedRel.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(normalizedRel) ||
                normalizedRel.Contains(':', StringComparison.Ordinal))
            {
                return Results.Json(new { ok = false, error = "非法的文件路径请求" }, JsonOpts, statusCode: StatusCodes.Status400BadRequest);
            }

            string rootFull = Path.GetFullPath(activeDir);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
                rootFull += Path.DirectorySeparatorChar;
            string candidateFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRel));

            if (!candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { ok = false, error = "非法越界的文件路径请求" }, JsonOpts, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!File.Exists(candidateFull))
            {
                return Results.Json(new { ok = false, error = $"未找到客户端更新文件：{normalizedRel}" }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            var info = new FileInfo(candidateFull);
            string sha256 = GetOrComputeSha256(candidateFull, info.Length, info.LastWriteTimeUtc.Ticks);
            string etag = $"\"{sha256}\"";

            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "public, max-age=300";
            ctx.Response.Headers["X-File-Sha256"] = sha256;

            string contentType = Path.GetExtension(candidateFull).ToLowerInvariant() switch
            {
                ".json" => "application/json; charset=utf-8",
                ".txt" or ".md" => "text/plain; charset=utf-8",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };

            return Results.File(candidateFull, contentType, fileDownloadName: Path.GetFileName(candidateFull), enableRangeProcessing: true);
        });
    }

    private static string GetOrComputeSha256(string fullPath, long length, long lastWriteTicksUtc)
    {
        if (HashCache.TryGetValue(fullPath, out var cached) &&
            cached.Length == length &&
            cached.LastWriteTicksUtc == lastWriteTicksUtc)
        {
            return cached.Sha256;
        }

        using var fs = File.OpenRead(fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        HashCache[fullPath] = new CachedHashEntry(length, lastWriteTicksUtc, sha256);
        return sha256;
    }

    private static string ResolveClientPayloadDirectory(string dataRoot)
    {
        var candidates = new List<string>();
        string? envDir = Environment.GetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD");
        if (!string.IsNullOrWhiteSpace(envDir))
            candidates.Add(envDir);

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "client-payload"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "client-payload"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "dist", "client", "payload"));

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            candidates.Add(Path.Combine(dataRoot, "client-payload"));
            string? p1 = Directory.GetParent(dataRoot)?.FullName;
            if (!string.IsNullOrEmpty(p1))
            {
                candidates.Add(Path.Combine(p1, "client-payload"));
                candidates.Add(Path.Combine(p1, "client", "payload"));
                string? p2 = Directory.GetParent(p1)?.FullName;
                if (!string.IsNullOrEmpty(p2))
                {
                    candidates.Add(Path.Combine(p2, "client-payload"));
                    candidates.Add(Path.Combine(p2, "dist", "client", "payload"));
                }
            }
        }

        foreach (string dir in candidates)
        {
            if (Directory.Exists(dir))
                return Path.GetFullPath(dir);
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "client-payload"));
    }

    private static bool IsNotModified(HttpRequest request, string etag)
    {
        if (!request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var values))
            return false;
        foreach (string? raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string trimmed = raw.Trim();
            if (trimmed == "*" ||
                string.Equals(trimmed, etag, StringComparison.Ordinal) ||
                string.Equals(trimmed.TrimStart('W', '/'), etag, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
