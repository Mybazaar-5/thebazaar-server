using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

/// <summary>
/// 服务端 Mod 增量分发、热重载管理与四层中文词典同步 API：
/// - GET  /api/mods/manifest           ：返回 Mod 列表、启停状态、modSetHash、视觉绑定表、自定义目录与 PNG 资产 SHA-256 列表（支持 ETag/304）
/// - GET  /api/mods/bundle/{modId}     ：返回单个 Mod 元数据、本地化覆盖与自定义视觉映射
/// - GET  /api/mods/assets/{sha256}    ：高效流式返回自定义 PNG/WebP 纹理（带强不可变缓存与 ETag）
/// - GET  /api/mods/active-bindings    ：返回当前活跃对局内各 InstanceId 对应的自定义卡视觉/文本绑定
/// - GET  /api/runs/{runId}/visual-bindings ：按 runId 查询实例级视觉绑定（客户端侧信道别名）
/// - POST /api/mods/reload             ：热重载 mods/ 目录与 custom_zh_overlay.json
/// - POST /api/mods/{modId}/toggle     ：运行时开关指定 Mod
/// - GET  /api/localization/overlay    ：下发合并后的四层 custom_zh_overlay.json（带 SHA-256 ETag/304；兼容 /api/localization/zh-overlay）
/// </summary>
public static class ModEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ToggleModBody
    {
        public bool? Enabled { get; set; }
    }

    public static void Map(WebApplication app)
    {
        // 1. 全量 Mod 清单与视觉绑定总表（支持 ETag 协商缓存）
        app.MapGet("/api/mods/manifest", (HttpContext ctx, ServerModHost modHost) =>
        {
            var clientManifest = modHost.Registry.BuildClientManifestDto();
            string etag = $"\"{clientManifest.ManifestHash}\"";
            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";

            var statuses = modHost.GetAllModStatuses();
            var customCards = new List<object>();
            var customEvents = new List<object>();
            foreach (var pkg in modHost.Registry.LoadedPackages)
            {
                foreach (var c in pkg.Cards)
                {
                    customCards.Add(new
                    {
                        modId = pkg.Manifest.Id,
                        customId = c.CustomId,
                        baseVisualTemplateId = c.BaseVisualTemplateId,
                        type = c.Type,
                        size = c.Size,
                        startingTier = c.StartingTier,
                        heroes = c.Heroes,
                        tags = c.Tags,
                        titleZh = c.TitleZh,
                        descZh = c.DescZh,
                        customArtFile = c.CustomArtFile,
                    });
                }
                foreach (var e in pkg.Events)
                {
                    customEvents.Add(new
                    {
                        modId = pkg.Manifest.Id,
                        customId = e.EventId,
                        baseVisualTemplateId = e.BaseVisualTemplateId,
                        titleZh = e.TitleZh,
                        descZh = e.DescZh,
                        heroes = e.Heroes,
                    });
                }
            }

            return Results.Json(new
            {
                schemaVersion = clientManifest.SchemaVersion,
                serverVersion = "2.0.0",
                generatedAtUtc = clientManifest.GeneratedAtUtc,
                modSetHash = modHost.ModSetHash,
                manifestHash = clientManifest.ManifestHash,
                localizationHash = clientManifest.LocalizationHash,
                visualCatalogHash = clientManifest.VisualCatalogHash,
                forcesGhostPoolIsolation = modHost.ForcesGhostPoolIsolation,
                mods = statuses,
                visualBindings = modHost.VisualBindingsByCustomId,
                customCards,
                customEvents,
                customMutators = modHost.ModCustomMutators,
                assets = clientManifest.Assets.Select(a => new
                {
                    a.ModId,
                    a.LogicalPath,
                    a.Sha256,
                    a.Bytes,
                    a.Width,
                    a.Height,
                    a.ContentType,
                    url = $"/api/mods/assets/{a.Sha256}",
                }).ToList(),
            }, JsonOpts);
        });

        // 2. 单个 Mod 详情包（元数据 + 本地化 + 自定义内容与视觉映射）
        app.MapGet("/api/mods/bundle/{modId}", (string modId, ServerModHost modHost) =>
        {
            var pkg = modHost.FindDiscoveredPackage(modId);
            if (pkg == null)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = $"未找到指定的 Mod：{modId}",
                }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            bool enabled = modHost.IsModEnabled(pkg.Manifest.Id);
            bool loaded = modHost.Registry.LoadedPackages.Any(p =>
                string.Equals(p.Manifest.Id, pkg.Manifest.Id, StringComparison.OrdinalIgnoreCase));

            return Results.Json(new
            {
                ok = true,
                enabled,
                loaded,
                packageHash = pkg.PackageHash,
                manifest = pkg.Manifest,
                localization = pkg.LocalizationOverlay,
                visualBindings = pkg.VisualBindingsByCustomId,
                cards = pkg.Cards.Select(c => new
                {
                    c.CustomId,
                    c.BaseVisualTemplateId,
                    c.Type,
                    c.Size,
                    c.StartingTier,
                    c.Heroes,
                    c.Tags,
                    c.TitleZh,
                    c.DescZh,
                    c.CustomArtFile,
                }).ToList(),
                events = pkg.Events.Select(e => new
                {
                    customId = e.EventId,
                    e.BaseVisualTemplateId,
                    e.TitleZh,
                    e.DescZh,
                    e.Heroes,
                }).ToList(),
                modes = pkg.Modes,
                assets = pkg.AssetsByLogicalPath.Values.Select(a => new
                {
                    a.LogicalPath,
                    a.Sha256,
                    a.Bytes,
                    a.Width,
                    a.Height,
                    a.ContentType,
                    url = $"/api/mods/assets/{a.Sha256}",
                }).ToList(),
            }, JsonOpts);
        });

        // 3. Mod 纹理资产内容寻址下载（SHA-256 不可变强缓存）
        app.MapGet("/api/mods/assets/{sha256}", (HttpContext ctx, string sha256, ServerModHost modHost) =>
        {
            if (!modHost.TryResolveAssetBySha256(sha256, out var asset) || asset == null || !File.Exists(asset.FullFilePath))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = $"未找到 SHA-256 为 {sha256} 的 Mod 资产文件",
                }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            string etag = $"\"{asset.Sha256}\"";
            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            return Results.File(asset.FullFilePath, asset.ContentType, enableRangeProcessing: true);
        });

        // 4. 当前活跃对局实例级视觉绑定查询（支持按账号自动定位或传 ?runId=...）
        app.MapGet("/api/mods/active-bindings", (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ServerModHost modHost) =>
        {
            string? runIdQuery = ctx.Request.Query["runId"].FirstOrDefault();
            return BuildActiveBindingsResponse(ctx, runIdQuery, accounts, sessions, modHost);
        });

        app.MapGet("/api/runs/{runId}/visual-bindings", (
            HttpContext ctx,
            string runId,
            AccountStore accounts,
            SessionStore sessions,
            ServerModHost modHost) =>
        {
            return BuildActiveBindingsResponse(ctx, runId, accounts, sessions, modHost);
        });

        // 5. 热重载全部 Mod 与外挂中文词典
        app.MapPost("/api/mods/reload", (ServerModHost modHost) =>
        {
            var report = modHost.Reload();
            return Results.Json(new
            {
                ok = report.Succeeded,
                messageZh = report.Succeeded
                    ? $"已热重载 {report.LoadedModIdsInOrder.Count} 个 Mod（自定义卡 {report.TotalCustomCards} 张，资产 {report.TotalAssets} 个）"
                    : $"热重载完成但存在校验告警（已加载 {report.LoadedModIdsInOrder.Count} 个，拒绝 {report.RejectedModIds.Count} 个）",
                modSetHash = report.ModSetHash,
                report,
                mods = modHost.GetAllModStatuses(),
            }, JsonOpts);
        });

        // 6. 运行时启停指定 Mod
        app.MapPost("/api/mods/{modId}/toggle", async (HttpContext ctx, string modId, ServerModHost modHost) =>
        {
            bool? desiredEnabled = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<ToggleModBody>(ctx.Request.Body, JsonOpts);
                    desiredEnabled = body?.Enabled;
                }
                catch
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "请求体 JSON 格式无效，期望 {\"enabled\": true|false} 或留空自动翻转",
                    }, JsonOpts, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var (found, enabled, report) = modHost.ToggleMod(modId, desiredEnabled);
            if (!found)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = $"未找到指定的 Mod：{modId}",
                }, JsonOpts, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new
            {
                ok = true,
                modId,
                enabled,
                messageZh = enabled ? $"已启用 Mod「{modId}」并完成热重载" : $"已禁用 Mod「{modId}」并完成热重载",
                modSetHash = report.ModSetHash,
                report,
            }, JsonOpts);
        });

        // 7. 合并后的四层中文外挂词典下发（支持 ETag/304 与双路由别名）
        IResult HandleLocalizationOverlay(HttpContext ctx, ServerModHost modHost)
        {
            var exported = modHost.ExportMergedLocalization();
            string etag = $"\"{exported.Sha256}\"";
            if (IsNotModified(ctx.Request, etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            ctx.Response.Headers["X-Localization-Sha256"] = exported.Sha256;
            ctx.Response.Headers["X-Localization-Entries"] = exported.TotalOverlayEntryCount.ToString();
            return Results.Content(exported.Json, "application/json; charset=utf-8", Encoding.UTF8);
        }

        app.MapGet("/api/localization/overlay", HandleLocalizationOverlay);
        app.MapGet("/api/localization/zh-overlay", HandleLocalizationOverlay);
    }

    private static IResult BuildActiveBindingsResponse(
        HttpContext ctx,
        string? runId,
        AccountStore accounts,
        SessionStore sessions,
        ServerModHost modHost)
    {
        SessionRegistry.Handle? handle = null;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            handle = SessionRegistry.TryFindByRunId(runId);
        }
        else
        {
            string? auth = ctx.Request.Headers.Authorization.FirstOrDefault();
            var acc = accounts.FindByAccessToken(auth);
            if (acc != null)
            {
                var mem = sessions.GetByAccount(acc.AccountId.ToString("N"))
                    ?? sessions.GetByAccount(acc.AccountId.ToString("D"));
                if (mem != null) handle = SessionRegistry.Get(mem.Sid);
            }
        }

        var bindings = modHost.CollectActiveVisualBindings(handle?.Run);
        return Results.Json(new
        {
            ok = true,
            runId = handle?.Run?.RunId,
            revision = handle?.Run?.VisualBindingsRevision ?? 0,
            modSetHash = modHost.ModSetHash,
            bindingCount = bindings.Count,
            bindings,
            templateCatalog = modHost.VisualBindingsByCustomId,
        }, JsonOpts);
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
