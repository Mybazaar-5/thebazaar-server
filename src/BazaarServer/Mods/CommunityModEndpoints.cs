using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

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
            var (accountId, _, _) = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            string? search = ctx.Request.Query["search"].FirstOrDefault();
            var items = communityStore.ListCatalog(accountId, isAdmin, search);
            return Results.Json(new
            {
                ok = true,
                isAdmin,
                total = items.Count,
                mods = items
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

            var (accountId, _, _) = ResolveCallerIdentity(ctx, accounts);
            var catalogItem = communityStore.ListCatalog(accountId, IsServerAdmin(ctx))
                .FirstOrDefault(x => string.Equals(x.ModId, pkg.Manifest.Id, StringComparison.OrdinalIgnoreCase));

            return Results.Json(new
            {
                ok = true,
                mod = catalogItem,
                manifest = pkg.Manifest,
                cards = pkg.Cards.Select(c => new { c.CustomId, c.TitleZh, c.DescZh, c.Type, c.Size, c.StartingTier }).ToList(),
                events = pkg.Events.Select(e => new { customId = e.EventId, e.TitleZh, e.DescZh }).ToList(),
                assets = pkg.AssetsByLogicalPath.Values.Select(a => new
                {
                    a.LogicalPath,
                    a.Sha256,
                    a.Bytes,
                    url = $"/api/mods/assets/{a.Sha256}"
                }).ToList()
            }, JsonOpts);
        });

        // 4. 下载 .bazaarmod.zip（同时支持社区 Mod 和服务器自带 Mod）
        app.MapGet("/api/community/mods/{modId}/download", (
            HttpContext ctx,
            string modId,
            CommunityModStore communityStore) =>
        {
            if (!communityStore.TryGetDownloadZip(modId, out var zipBytes, out var fileName, out var err))
                return Results.Json(new { ok = false, error = err ?? "下载失败" }, JsonOpts, statusCode: 404);

            ctx.Response.Headers[HeaderNames.ContentDisposition] = $"attachment; filename=\"{fileName}\"";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            return Results.File(zipBytes, "application/zip", fileName);
        });

        // 5. 玩家上传本地 .bazaarmod.zip 到社区库（支持 multipart/form-data 或直接二进制上传）
        app.MapPost("/api/community/mods/upload", async (
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var (accountId, username, hasIdentity) = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            if (!hasIdentity && !isAdmin)
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
                        return Results.Json(new { ok = false, error = "压缩包体积超过 10 MB 上限。" }, JsonOpts, statusCode: 400);

                    using var ms = new MemoryStream((int)file.Length);
                    await file.CopyToAsync(ms, ctx.RequestAborted);
                    zipBytes = ms.ToArray();
                }
                else
                {
                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                    zipBytes = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "读取上传文件失败：" + ex.Message }, JsonOpts, statusCode: 400);
            }

            var (ok, statusCode, err, rec, report) = await communityStore.UploadZipAsync(
                zipBytes,
                accountId,
                username,
                isAdmin,
                ctx.RequestAborted);

            if (!ok || rec == null)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = err ?? "上传校验未通过。",
                    report
                }, JsonOpts, statusCode: statusCode);
            }

            return Results.Json(new
            {
                ok = true,
                messageZh = $"已上传 Mod「{rec.Name}」v{rec.Version}，其他玩家现在可以直接下载或建房选用。",
                mod = rec,
                report
            }, JsonOpts);
        });

        // 6. 删除社区 Mod（仅作者本人或服主）
        app.MapDelete("/api/community/mods/{modId}", (
            string modId,
            HttpContext ctx,
            AccountStore accounts,
            CommunityModStore communityStore) =>
        {
            var (accountId, _, hasIdentity) = ResolveCallerIdentity(ctx, accounts);
            bool isAdmin = IsServerAdmin(ctx);
            if (!hasIdentity && !isAdmin)
                return Results.Json(new { ok = false, error = "缺少身份信息，无法删除。" }, JsonOpts, statusCode: 401);

            var (ok, statusCode, err) = communityStore.DeleteCommunityMod(modId, accountId, isAdmin);
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

    private static (string AccountId, string Username, bool HasIdentity) ResolveCallerIdentity(HttpContext ctx, AccountStore accounts)
    {
        var resolved = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
        if (resolved.IsAuthenticated && resolved.Account != null)
        {
            return (resolved.Account.AccountId.ToString("N"), resolved.Account.Username, true);
        }

        // 兼容 BazaarHub / 轻量客户端通过 X-Player-Name 请求头声明上传者昵称
        string? headerName = ctx.Request.Headers["X-Player-Name"].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            if (headerName.Length > 32) headerName = headerName[..32];
            string hashKey = "name_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(headerName.ToLowerInvariant())))[..16].ToLowerInvariant();
            return (hashKey, headerName, true);
        }

        return (string.Empty, string.Empty, false);
    }

    internal static bool IsServerAdmin(HttpContext ctx)
    {
        string mode = (Environment.GetEnvironmentVariable("BAZAAR_WORKSHOP_MODE") ?? string.Empty).Trim();
        if (string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase))
            return true;

        string? configuredToken = Environment.GetEnvironmentVariable("BAZAAR_WORKSHOP_TOKEN");
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            string? provided = ctx.Request.Headers["X-Workshop-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(provided))
                provided = ctx.Request.Query["token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(provided))
            {
                byte[] a = Encoding.UTF8.GetBytes(provided.Trim());
                byte[] b = Encoding.UTF8.GetBytes(configuredToken.Trim());
                if (a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b))
                    return true;
            }
        }

        bool hasForwardedFor = ctx.Request.Headers.ContainsKey("X-Forwarded-For")
            || ctx.Request.Headers.ContainsKey("X-Real-IP");
        if (hasForwardedFor)
            return false;

        var remoteIp = ctx.Connection.RemoteIpAddress;
        return remoteIp != null && IPAddress.IsLoopback(remoteIp);
    }
}
