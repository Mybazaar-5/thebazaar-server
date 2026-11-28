using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

/// <summary>
/// 客户端增量热更新文件的权威元数据策略（Single Source of Truth）。
/// </summary>
public sealed record ClientUpdateFilePolicy(
    string TargetPath,
    string Category,
    string UpdatePolicy,
    string AtomicGroup,
    bool Required);

/// <summary>
/// 客户端增量热更新分发端点（供 BazaarHub / 启动器一键拉取最新 BazaarLauncher.dll、BazaarModSdk.dll 等白名单载荷）：
/// - GET /api/client-update/manifest      ：按白名单扫描载荷目录，输出权威 targetPath/updatePolicy/atomicGroup 与整体 SHA-256
/// - GET /api/client-update/files/{*name} ：流式下发白名单载荷文件（严格路径穿越拦截 + 同源白名单门禁 + ETag 协商缓存）
/// </summary>
public static class ClientUpdateEndpoints
{
    private const string DefaultFallbackVersion = "15.5.0";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record CachedHashEntry(long Length, long LastWriteTicksUtc, string Sha256);

    private sealed record ManifestCandidate(
        string FullPath,
        string FileName,
        string RelativePath,
        ClientUpdateFilePolicy Policy,
        int Priority);

    private static readonly ConcurrentDictionary<string, CachedHashEntry> HashCache = new(PathComparer);

    /// <summary>
    /// 客户端热更新白名单分类器：
    /// 仅放行核心插件（BazaarLauncher.dll / BazaarModSdk.dll）、引导文件（winhttp.dll / doorstop_config.ini / .doorstop_version）
    /// 与 BepInEx/core/*.dll；彻底禁止下发玩家本地配置（*.cfg）、状态文件（game-dir.txt）及调试文档（*.xml / *.pdb / *.mdb）。
    /// </summary>
    public static bool TryClassifyPayloadFile(string normalizedRelPath, out ClientUpdateFilePolicy policy)
    {
        policy = default!;
        if (string.IsNullOrWhiteSpace(normalizedRelPath))
            return false;

        string rel = normalizedRelPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..", StringComparison.Ordinal) || rel.Contains(':'))
            return false;

        if (string.Equals(rel, "BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rel, "BepInEx/plugins/BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase))
        {
            policy = new ClientUpdateFilePolicy(
                TargetPath: "BepInEx/plugins/BazaarLauncher.dll",
                Category: "plugin",
                UpdatePolicy: "always",
                AtomicGroup: "core-plugins",
                Required: true);
            return true;
        }

        if (string.Equals(rel, "BazaarModSdk.dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rel, "BepInEx/plugins/BazaarModSdk.dll", StringComparison.OrdinalIgnoreCase))
        {
            policy = new ClientUpdateFilePolicy(
                TargetPath: "BepInEx/plugins/BazaarModSdk.dll",
                Category: "plugin",
                UpdatePolicy: "always",
                AtomicGroup: "core-plugins",
                Required: true);
            return true;
        }

        if (string.Equals(rel, "winhttp.dll", StringComparison.OrdinalIgnoreCase))
        {
            policy = new ClientUpdateFilePolicy(
                TargetPath: "winhttp.dll",
                Category: "loader",
                UpdatePolicy: "always",
                AtomicGroup: "bepinex-loader",
                Required: true);
            return true;
        }

        if (string.Equals(rel, "doorstop_config.ini", StringComparison.OrdinalIgnoreCase))
        {
            policy = new ClientUpdateFilePolicy(
                TargetPath: "doorstop_config.ini",
                Category: "loader",
                UpdatePolicy: "ifMissing",
                AtomicGroup: "bepinex-loader",
                Required: true);
            return true;
        }

        if (string.Equals(rel, ".doorstop_version", StringComparison.OrdinalIgnoreCase))
        {
            policy = new ClientUpdateFilePolicy(
                TargetPath: ".doorstop_version",
                Category: "loader",
                UpdatePolicy: "always",
                AtomicGroup: "bepinex-loader",
                Required: false);
            return true;
        }

        const string corePrefix = "BepInEx/core/";
        if (rel.StartsWith(corePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string sub = rel[corePrefix.Length..];
            if (!string.IsNullOrWhiteSpace(sub) &&
                !sub.Contains('/', StringComparison.Ordinal) &&
                !sub.StartsWith('.') &&
                sub.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                policy = new ClientUpdateFilePolicy(
                    TargetPath: $"BepInEx/core/{sub}",
                    Category: "loader",
                    UpdatePolicy: "always",
                    AtomicGroup: "bepinex-loader",
                    Required: true);
                return true;
            }
        }

        return false;
    }

    public static void Map(WebApplication app, string dataRoot) => Map(app, dataRoot, payloadDirOverride: null);

    public static void Map(WebApplication app, string dataRoot, string? payloadDirOverride)
    {
        string initialPayloadDir = ResolveClientPayloadDirectory(dataRoot, payloadDirOverride);

        app.MapGet("/api/client-update/manifest", (HttpContext ctx) =>
        {
            string activeDir = !string.IsNullOrWhiteSpace(payloadDirOverride)
                ? Path.GetFullPath(payloadDirOverride)
                : (Directory.Exists(initialPayloadDir) ? initialPayloadDir : ResolveClientPayloadDirectory(dataRoot, null));

            var byTarget = new Dictionary<string, ManifestCandidate>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(activeDir))
            {
                foreach (string fullPath in Directory.GetFiles(activeDir, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.Ordinal))
                {
                    string fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrEmpty(fileName))
                        continue;

                    string relPath = Path.GetRelativePath(activeDir, fullPath).Replace('\\', '/');
                    if (!TryClassifyPayloadFile(relPath, out var policy))
                        continue;

                    // 根层发布的插件产物优先级高于子目录副本，避免同 targetPath 重复条目
                    int priority = relPath.Contains('/', StringComparison.Ordinal) ? 1 : 0;
                    if (!byTarget.TryGetValue(policy.TargetPath, out var existing) || priority < existing.Priority)
                    {
                        byTarget[policy.TargetPath] = new ManifestCandidate(fullPath, fileName, relPath, policy, priority);
                    }
                }
            }

            var orderedCandidates = byTarget.Values
                .OrderBy(c => c.Policy.TargetPath, StringComparer.Ordinal)
                .ToList();

            var files = new List<object>(orderedCandidates.Count);
            var hashInput = new StringBuilder();
            string launcherVersion = ResolveServerVersion();

            foreach (var cand in orderedCandidates)
            {
                var info = new FileInfo(cand.FullPath);
                string sha256 = GetOrComputeSha256(cand.FullPath, info.Length, info.LastWriteTimeUtc.Ticks);

                string? detectedFileVer = (cand.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                           cand.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ? TryReadAssemblyFileVersion(cand.FullPath)
                    : null;

                if (string.Equals(cand.FileName, "BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(detectedFileVer))
                {
                    launcherVersion = detectedFileVer;
                }

                hashInput
                    .Append(cand.Policy.TargetPath).Append(':')
                    .Append(cand.Policy.UpdatePolicy).Append(':')
                    .Append(detectedFileVer ?? string.Empty).Append(':')
                    .Append(sha256).Append('\n');

                files.Add(new
                {
                    name = cand.FileName,
                    relativePath = cand.RelativePath,
                    targetPath = cand.Policy.TargetPath,
                    category = cand.Policy.Category,
                    updatePolicy = cand.Policy.UpdatePolicy,
                    atomicGroup = cand.Policy.AtomicGroup,
                    required = cand.Policy.Required,
                    fileVersion = detectedFileVer ?? string.Empty,
                    sha256,
                    bytes = info.Length,
                    lastModifiedUtc = info.LastWriteTimeUtc,
                    downloadUrl = $"/api/client-update/files/{Uri.EscapeDataString(cand.RelativePath).Replace("%2F", "/")}",
                });
            }

            string serverVer = ResolveServerVersion();
            string minRequiredVersion = ResolveMinRequiredVersion();
            string minHubVersion = ResolveMinHubVersion();
            hashInput
                .Append("meta:")
                .Append(serverVer).Append(':')
                .Append(minRequiredVersion).Append(':')
                .Append(minHubVersion).Append('\n');

            string manifestSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()))).ToLowerInvariant();
            string etag = $"\"{manifestSha256}\"";

            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";

            return Results.Json(new
            {
                schemaVersion = 2,
                serverVersion = serverVer,
                launcherVersion,
                hubVersion = serverVer,
                minRequiredVersion,
                minHubVersion,
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

            string activeDir = !string.IsNullOrWhiteSpace(payloadDirOverride)
                ? Path.GetFullPath(payloadDirOverride)
                : (Directory.Exists(initialPayloadDir) ? initialPayloadDir : ResolveClientPayloadDirectory(dataRoot, null));

            if (!Directory.Exists(activeDir))
            {
                return Results.Json(new { ok = false, error = "服务端尚未部署客户端热更新载荷目录" }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            // 严格防范路径穿越（禁止 ..、盘符/ADS 冒号、空字符或绝对根路径）
            string normalizedRel = name.Replace('\\', '/').TrimStart('/');
            if (normalizedRel.Contains("..", StringComparison.Ordinal) ||
                normalizedRel.Contains('\0') ||
                Path.IsPathRooted(normalizedRel) ||
                normalizedRel.Contains(':', StringComparison.Ordinal))
            {
                return Results.Json(new { ok = false, error = "非法的文件路径请求" }, JsonOpts, statusCode: StatusCodes.Status400BadRequest);
            }

            // 同源白名单门禁：禁止绕过 manifest 直接下载 .cfg / .xml / .pdb 等非白名单文件
            if (!TryClassifyPayloadFile(normalizedRel, out _))
            {
                return Results.Json(new { ok = false, error = $"未找到允许分发的客户端更新文件：{normalizedRel}" }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            string rootFull = Path.GetFullPath(activeDir);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
                rootFull += Path.DirectorySeparatorChar;
            string candidateFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRel.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidateFull.StartsWith(rootFull, PathComparison))
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
                ".txt" or ".md" or ".ini" => "text/plain; charset=utf-8",
                ".png" => "image/png",
                _ => "application/octet-stream",
            };

            return Results.File(candidateFull, contentType, fileDownloadName: Path.GetFileName(candidateFull), enableRangeProcessing: true);
        });
    }

    public static string ResolveClientPayloadDirectory(string dataRoot, string? payloadDirOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(payloadDirOverride))
        {
            return Path.GetFullPath(payloadDirOverride);
        }

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
                // 兼容 Release 整包根目录 payload/（供 install.bat 与服务端热更新共用）
                candidates.Add(Path.Combine(p1, "payload"));
            }
        }

        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "payload")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "payload"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "payload"));
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            candidates.Add(Path.Combine(dataRoot, "client-update"));
        }

        foreach (string dir in candidates)
        {
            if (Directory.Exists(dir))
                return Path.GetFullPath(dir);
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "client-payload"));
    }

    private static string ResolveMinRequiredVersion()
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_MIN_CLIENT_VERSION")?.Trim();
        return !string.IsNullOrWhiteSpace(env) ? env : "15.0.0";
    }

    private static string ResolveMinHubVersion()
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_MIN_HUB_VERSION")?.Trim();
        return !string.IsNullOrWhiteSpace(env) ? env : string.Empty;
    }

    private static string ResolveServerVersion()
    {
        try
        {
            string? infoVer = typeof(ClientUpdateEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                int plusIdx = infoVer.IndexOf('+');
                string clean = (plusIdx > 0 ? infoVer[..plusIdx] : infoVer).Trim();
                if (!string.IsNullOrWhiteSpace(clean) && !string.Equals(clean, "1.0.0", StringComparison.Ordinal))
                    return clean;
            }
        }
        catch
        {
            // 忽略程序集版本读取异常
        }
        return DefaultFallbackVersion;
    }

    private static string? TryReadAssemblyFileVersion(string dllPath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(dllPath);
            if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
            {
                string v = fvi.FileVersion.Trim();
                if (v.EndsWith(".0", StringComparison.Ordinal) && v.Count(c => c == '.') == 3)
                    v = v[..^2];
                return v;
            }
        }
        catch
        {
            // 测试占位二进制非 PE 文件时回退默认版本
        }
        return null;
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
