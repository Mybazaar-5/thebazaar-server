using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

/// <summary>社区 Mod 端点请求者解析结果（严格区分强认证账号与匿名展示名/高熵 Secret）。</summary>
public sealed record CommunityCallerIdentity(
    string? BearerAccountId,
    string DisplayName,
    string? ProvidedSecretHash,
    bool IsAuthenticatedAccount,
    bool HasAnyUploaderIdentity);

/// <summary>
/// 服务器社区 Mod 共享接口（面向普通玩家上传/下载、锦标赛按房同步与服主管理）：
/// - GET    /api/community/mods                       ：浏览服务器与社区全部可用 Mod 列表（支持 ?search=）
/// - GET    /api/community/mods/bundle?modIds=a,b     ：打包返回指定多个 Mod 的视觉绑定、中文词条与 PNG 下载地址（供锦标赛成员同步本房 Mod）
/// - GET    /api/community/mods/{modId}               ：查看单个 Mod 详情
/// - GET    /api/community/mods/{modId}/download      ：下载 `.bazaarmod.zip` 到本地
/// - POST   /api/community/mods/upload                ：玩家上传本地 `.bazaarmod.zip`（免管理员 Token，经 10 项校验 + 25 场实战试跑入社区库）
/// - DELETE /api/community/mods/{modId}               ：上传者本人或服主删除社区 Mod
/// - POST   /api/community/mods/{modId}/server-default：服主一键设为全服默认启用或取消全服默认
/// </summary>
public static class CommunityModEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ServerDefaultBody
    {
        public bool Enabled { get; set; }
    }

    public static void Map(WebApplication app)
    {
        // 1. 社区 + 服务器 Mod 总列表
        app.MapGet("/api/community/mods", (
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var identity = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            string? search = ctx.Request.Query["search"].FirstOrDefault();
            var items = communityStore.ListCatalog(identity.BearerAccountId, identity.ProvidedSecretHash, isAdmin, search);
            var enriched = items.Select(item => EnrichCatalogItemWithMechanics(item, communityStore)).ToList();
            return Results.Json(new
            {
                ok = true,
                isAdmin,
                total = enriched.Count,
                mods = enriched
            }, JsonOpts);
        });

        // 2. 按房间/对局选中的多个 modId 合并拉取视觉绑定、中文覆盖与 PNG 资产列表
        app.MapGet("/api/community/mods/bundle", (
            HttpContext ctx,
            CommunityModStore communityStore) =>
        {
            string raw = ctx.Request.Query["modIds"].FirstOrDefault() ?? string.Empty;
            var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Json(communityStore.BuildSelectedBundleDto(ids), JsonOpts);
        });

        // 3. 单个 Mod 详情
        app.MapGet("/api/community/mods/{modId}", (
            string modId,
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var pkg = communityStore.ResolvePackage(modId);
            if (pkg == null)
                return Results.Json(new { ok = false, error = $"未找到 Mod：{modId}" }, JsonOpts, statusCode: 404);

            var identity = ResolveCallerIdentity(ctx, accounts);
            var catalogItem = communityStore.ListCatalog(identity.BearerAccountId, identity.ProvidedSecretHash, IsServerAdmin(ctx))
                .FirstOrDefault(x => string.Equals(x.ModId, pkg.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            var enrichedItem = catalogItem != null
                ? EnrichCatalogItemWithMechanics(catalogItem, communityStore, pkg.Mechanics.Count)
                : null;

            return Results.Json(new
            {
                ok = true,
                mod = enrichedItem,
                manifest = pkg.Manifest,
                cards = pkg.Cards.Select(c => new { c.CustomId, c.TitleZh, c.DescZh, c.Type, c.Size, c.StartingTier }).ToList(),
                events = pkg.Events.Select(e => new { customId = e.EventId, e.TitleZh, e.DescZh }).ToList(),
                customMechanicCount = pkg.Mechanics.Count,
                mechanics = pkg.Mechanics.Select(m => new { m.Id, m.NameZh, m.DescZh, m.CategoryZh, m.Version }).ToList(),
                assets = pkg.AssetsByLogicalPath.Values.Select(a => new
                {
                    a.LogicalPath,
                    a.Sha256,
                    a.Bytes,
                    url = $"/api/mods/assets/{a.Sha256}"
                }).ToList()
            }, JsonOpts);
        });

        // 4. 下载 .bazaarmod.zip（同时支持社区 Mod 和服务器自带 Mod；先清洗文件名再由 Results.File 安全输出 RFC 5987 响应头）
        app.MapGet("/api/community/mods/{modId}/download", (
            HttpContext ctx,
            string modId,
            CommunityModStore communityStore) =>
        {
            if (!communityStore.TryGetDownloadZip(modId, out var zipBytes, out var fileName, out var err))
                return Results.Json(new { ok = false, error = err ?? "下载失败" }, JsonOpts, statusCode: 404);

            string safeFileName = SanitizeDownloadFileName(fileName, modId);
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            return Results.File(zipBytes, "application/zip", safeFileName);
        });

        // 5. 玩家上传本地 .bazaarmod.zip 到社区库（支持 multipart/form-data 或直接二进制上传，双分支强制 10 MB 流式截断）
        app.MapPost("/api/community/mods/upload", async (
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var identity = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            if (!identity.HasAnyUploaderIdentity && !isAdmin)
                return Results.Json(new { ok = false, error = "请先登录游戏或设置玩家昵称后再上传 Mod。" }, JsonOpts, statusCode: 401);

            byte[] zipBytes;
            try
            {
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                    if (file == null || file.Length == 0)
                        return Results.Json(new { ok = false, error = "请选择要上传的 .bazaarmod.zip 文件。" }, JsonOpts, statusCode: 400);
                    if (file.Length > CommunityModStore.MaxCommunityZipBytes)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            errorCode = "PAYLOAD_TOO_LARGE",
                            error = "压缩包体积超过 10 MB 上限。"
                        }, JsonOpts, statusCode: 413);
                    }

                    await using var fileStream = file.OpenReadStream();
                    var (tooLarge, bytes) = await ReadBoundedBytesAsync(fileStream, CommunityModStore.MaxCommunityZipBytes, ctx.RequestAborted);
                    if (tooLarge)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            errorCode = "PAYLOAD_TOO_LARGE",
                            error = "压缩包体积超过 10 MB 上限。"
                        }, JsonOpts, statusCode: 413);
                    }
                    zipBytes = bytes!;
                }
                else
                {
                    if (ctx.Request.ContentLength is > CommunityModStore.MaxCommunityZipBytes)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            errorCode = "PAYLOAD_TOO_LARGE",
                            error = "压缩包体积超过 10 MB 上限。"
                        }, JsonOpts, statusCode: 413);
                    }

                    var (tooLarge, bytes) = await ReadBoundedBytesAsync(ctx.Request.Body, CommunityModStore.MaxCommunityZipBytes, ctx.RequestAborted);
                    if (tooLarge)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            errorCode = "PAYLOAD_TOO_LARGE",
                            error = "压缩包体积超过 10 MB 上限。"
                        }, JsonOpts, statusCode: 413);
                    }
                    zipBytes = bytes!;
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "读取上传文件失败：" + ex.Message }, JsonOpts, statusCode: 400);
            }

            var (ok, statusCode, err, rec, report, issuedSecret) = await communityStore.UploadZipAsync(
                zipBytes,
                identity.BearerAccountId,
                identity.DisplayName,
                identity.ProvidedSecretHash,
                isAdmin,
                ctx.RequestAborted);

            if (!ok || rec == null)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = err ?? "上传校验未通过。",
                    report,
                    validation = report
                }, JsonOpts, statusCode: statusCode);
            }

            if (!string.IsNullOrEmpty(issuedSecret))
                ctx.Response.Headers["X-Uploader-Secret"] = issuedSecret;

            int uploadedMechCount = communityStore.ResolvePackage(rec.ModId)?.Mechanics.Count ?? 0;

            return Results.Json(new
            {
                ok = true,
                messageZh = $"已上传 Mod「{rec.Name}」v{rec.Version}，其他玩家现在可以直接下载或建房选用。",
                mod = ToPublicModDto(rec, uploadedMechCount),
                report,
                validation = report,
                uploaderSecret = issuedSecret
            }, JsonOpts);
        });

        // 6. 删除社区 Mod（仅作者本人凭 Bearer 账号或 X-Uploader-Secret，或服主）
        app.MapDelete("/api/community/mods/{modId}", (
            string modId,
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var identity = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            bool hasAnyCaller = identity.HasAnyUploaderIdentity || !string.IsNullOrWhiteSpace(identity.ProvidedSecretHash);
            if (!hasAnyCaller && !isAdmin)
                return Results.Json(new { ok = false, error = "缺少身份信息，无法删除。" }, JsonOpts, statusCode: 401);

            var (ok, statusCode, err) = communityStore.DeleteCommunityMod(
                modId,
                identity.BearerAccountId,
                identity.ProvidedSecretHash,
                isAdmin);
            if (!ok)
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: statusCode);

            return Results.Json(new { ok = true, modId, messageZh = $"已删除社区 Mod「{modId}」。" }, JsonOpts);
        });

        // 7. 服主一键设为全服默认启用 / 取消全服默认
        app.MapPost("/api/community/mods/{modId}/server-default", async (
            string modId,
            HttpContext ctx,
            CommunityModStore communityStore) =>
        {
            if (!IsServerAdmin(ctx))
                return Results.Json(new { ok = false, error = "只有服主可以修改全服默认启用的 Mod（需管理员口令或本机访问）。" }, JsonOpts, statusCode: 403);

            bool desired = true;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<ServerDefaultBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                    if (body != null) desired = body.Enabled;
                }
                catch
                {
                    return Results.Json(new { ok = false, error = "请求体 JSON 格式不正确。" }, JsonOpts, statusCode: 400);
                }
            }

            var (ok, statusCode, err, isEnabled) = communityStore.SetServerDefault(modId, desired);
            if (!ok)
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: statusCode);

            return Results.Json(new
            {
                ok = true,
                modId,
                enabled = isEnabled,
                messageZh = isEnabled
                    ? $"已将「{modId}」设为全服默认启用并热重载生效。"
                    : $"已取消「{modId}」的全服默认启用状态。"
            }, JsonOpts);
        });
    }

    private static JsonObject EnrichCatalogItemWithMechanics(
        CommunityModCatalogItem item,
        CommunityModStore communityStore,
        int? precomputedCount = null)
    {
        int mechCount = precomputedCount ?? (communityStore.ResolvePackage(item.ModId)?.Mechanics.Count ?? 0);
        var node = JsonSerializer.SerializeToNode(item, JsonOpts)?.AsObject() ?? new JsonObject();
        node["customMechanicCount"] = mechCount;
        node["mechanicCount"] = mechCount;
        return node;
    }

    internal static string SanitizeDownloadFileName(string? rawFileName, string fallbackModId)
    {
        string candidate = string.IsNullOrWhiteSpace(rawFileName) ? $"{fallbackModId}.bazaarmod.zip" : rawFileName.Trim();
        var sb = new StringBuilder(candidate.Length);
        foreach (char ch in candidate)
        {
            if (ch < 0x20 || ch == 0x7F || ch is '"' or '\'' or '/' or '\\' or ';' or ':' or '*' or '?' or '<' or '>' or '|')
                continue;
            sb.Append(ch);
        }
        string cleaned = sb.ToString().Trim().Trim('.');
        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "mod";
        if (!cleaned.EndsWith(".bazaarmod.zip", StringComparison.OrdinalIgnoreCase))
        {
            if (cleaned.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^4];
            cleaned = cleaned.TrimEnd('.') + ".bazaarmod.zip";
        }
        return cleaned;
    }

    private static object ToPublicModDto(CommunityModRecord rec, int mechanicCount = 0) => new
    {
        modId = rec.ModId,
        name = rec.Name,
        version = rec.Version,
        author = rec.Author,
        description = rec.Description,
        scope = rec.Scope,
        uploaderAccountId = rec.UploaderAccountId,
        uploaderName = rec.UploaderName,
        uploadedAtUtc = rec.UploadedAtUtc,
        updatedAtUtc = rec.UpdatedAtUtc,
        packageSha256 = rec.PackageSha256,
        zipBytes = rec.ZipBytes,
        customCardCount = rec.CustomCardCount,
        customEventCount = rec.CustomEventCount,
        customMonsterCount = rec.CustomMonsterCount,
        cardPatchCount = rec.CardPatchCount,
        customMechanicCount = mechanicCount,
        mechanicCount = mechanicCount,
        assetCount = rec.AssetCount,
        downloadCount = rec.DownloadCount
    };

    private static async Task<(bool TooLarge, byte[]? Bytes)> ReadBoundedBytesAsync(
        Stream stream,
        long maxBytes,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return (true, null);
            ms.Write(buffer, 0, read);
        }
        return (false, ms.ToArray());
    }

    private static CommunityCallerIdentity ResolveCallerIdentity(HttpContext ctx, AccountStore accounts)
    {
        string? rawSecret = ctx.Request.Headers["X-Uploader-Secret"].FirstOrDefault();
        string? providedSecretHash = string.IsNullOrWhiteSpace(rawSecret)
            ? null
            : CommunityModStore.ComputeSecretHash(rawSecret);

        // 严禁接受 X-Username 降级充当认证账号，防止任意伪造他人 AccountId
        var resolved = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false, allowLegacyUsernameFallback: false);
        if (resolved.IsAuthenticated && resolved.Account != null)
        {
            return new CommunityCallerIdentity(
                BearerAccountId: resolved.Account.AccountId.ToString("N"),
                DisplayName: resolved.Account.Username,
                ProvidedSecretHash: providedSecretHash,
                IsAuthenticatedAccount: true,
                HasAnyUploaderIdentity: true);
        }

        // 兼容 BazaarHub / 轻量客户端 / 网页工坊通过 X-Player-Name 请求头声明展示昵称（仅作展示名，绝不作为归属凭证）
        string? rawHeaderName = ctx.Request.Headers["X-Player-Name"].FirstOrDefault();
        string headerName = DecodeHeaderPlayerName(rawHeaderName);
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            return new CommunityCallerIdentity(
                BearerAccountId: null,
                DisplayName: headerName,
                ProvidedSecretHash: providedSecretHash,
                IsAuthenticatedAccount: false,
                HasAnyUploaderIdentity: true);
        }

        return new CommunityCallerIdentity(
            BearerAccountId: null,
            DisplayName: string.Empty,
            ProvidedSecretHash: providedSecretHash,
            IsAuthenticatedAccount: false,
            HasAnyUploaderIdentity: false);
    }

    /// <summary>
    /// 安全解码客户端经 <c>Uri.EscapeDataString</c> / <c>encodeURIComponent</c> 编码的 <c>X-Player-Name</c> 请求头：
    /// - 含百分号编码时还原为 UTF-8 中文昵称；
    /// - 遇到非法 <c>%</c> 序列时不抛异常，安全回退为原串；
    /// - 纯 ASCII 昵称原样保留，并经 <see cref="AccountStore.SanitizeDisplayText"/> 过滤控制符与超长截断（≤32 字符）。
    /// </summary>
    public static string DecodeHeaderPlayerName(string? rawHeader)
    {
        if (string.IsNullOrWhiteSpace(rawHeader))
            return string.Empty;

        string trimmed = rawHeader.Trim();
        string decoded = trimmed;
        if (trimmed.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                decoded = Uri.UnescapeDataString(trimmed);
            }
            catch
            {
                decoded = trimmed;
            }
        }

        return AccountStore.SanitizeDisplayText(decoded, maxLength: 32);
    }

    internal static bool IsServerAdmin(HttpContext ctx)
    {
        var guard = ctx.RequestServices?.GetService<AdminAccessGuard>() ?? AdminAccessGuard.Default;
        return guard.Evaluate(ctx).Allowed;
    }
}
