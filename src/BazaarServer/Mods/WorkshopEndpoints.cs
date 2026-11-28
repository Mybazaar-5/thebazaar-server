using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Mods;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Mods;

/// <summary>
/// V15.4 零代码 Web 创意工坊 API 路由与 `/workshop` 单页应用静态托管：
/// - 全部 11+ 个 `/api/workshop/*` 端点（原版 3,368 白名单卡搜索、规则积木中文目录、草稿保存、PNG/JPEG 资产上传校验、10 道铁闸 + 50 场战斗体检、全绿强门禁发布热重载、.bazaarmod.zip 导入导出与安全删除）；
/// - 安全门控：本机回环默认全功能读写；公网访问默认只读沙盒（浏览/检索/内存体检/导出可用），写操作需 BAZAAR_WORKSHOP_MODE=open 或 X-Workshop-Token 常量时间匹配 BAZAAR_WORKSHOP_TOKEN。
/// </summary>
public static class WorkshopEndpoints
{
    private static readonly Lazy<string> CachedServerVersion = new(ComputeServerVersion);

    /// <summary>从程序集 InformationalVersion 权威解析服务端版本号（剥离 +commit 元数据后缀）。</summary>
    internal static string ResolveServerVersion() => CachedServerVersion.Value;

    private static string ComputeServerVersion()
    {
        var asm = typeof(WorkshopEndpoints).Assembly;
        string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            string cleaned = (plusIdx >= 0 ? infoVer[..plusIdx] : infoVer).Trim();
            if (!string.IsNullOrEmpty(cleaned))
                return cleaned;
        }
        return asm.GetName().Version?.ToString(3) ?? "15.5.0";
    }
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ValidateRequestBody
    {
        public WorkshopModWorkspace? Draft { get; set; }
        public int? Matches { get; set; }
        public int? Seed { get; set; }
    }

    private sealed class StressTestMechanicRequestBody
    {
        public MechanicDefinition? Mechanic { get; set; }
        public Dictionary<string, JsonElement>? SampleParams { get; set; }
        public int? Matches { get; set; }
    }

    public static void Map(WebApplication app)
    {
        var modHost = app.Services.GetRequiredService<ServerModHost>();
        var service = app.Services.GetService<WorkshopValidatorService>() ?? new WorkshopValidatorService(modHost);

        // ── 静态页面与资产托管（前置中间件统一拦截 /workshop、/workshop/、/workshop/index.html，强制注入 {{ASSET_PREFIX}} 并严格支持 HEAD）──
        string? workshopStaticDir = ResolveWorkshopStaticDirectory();

        async Task WriteWorkshopIndexAsync(HttpContext ctx)
        {
            if (workshopStaticDir == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                if (!HttpMethods.IsHead(ctx.Request.Method))
                    await ctx.Response.WriteAsync("未找到 wwwroot/workshop 静态前端目录，请确认 BazaarServer 完整发布。", Encoding.UTF8, ctx.RequestAborted);
                return;
            }
            string indexPath = Path.Combine(workshopStaticDir, "index.html");
            if (!File.Exists(indexPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                if (!HttpMethods.IsHead(ctx.Request.Method))
                    await ctx.Response.WriteAsync("未找到 workshop/index.html", Encoding.UTF8, ctx.RequestAborted);
                return;
            }

            string rawPath = ctx.Request.Path.Value ?? string.Empty;
            bool inWorkshopDir = rawPath.EndsWith("/", StringComparison.Ordinal)
                || rawPath.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
            string prefix = inWorkshopDir ? "./" : "./workshop/";

            string html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, ctx.RequestAborted);
            html = html.Replace("{{ASSET_PREFIX}}", prefix, StringComparison.Ordinal);
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            ctx.Response.ContentLength = htmlBytes.Length;
            if (HttpMethods.IsHead(ctx.Request.Method))
                return;
            await ctx.Response.Body.WriteAsync(htmlBytes, ctx.RequestAborted);
        }

        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            {
                string path = ctx.Request.Path.Value ?? string.Empty;
                if (path.Equals("/workshop", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/workshop/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/workshop/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteWorkshopIndexAsync(ctx);
                    return;
                }
            }
            await next();
        });

        if (workshopStaticDir != null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(workshopStaticDir),
                RequestPath = "/workshop",
                ServeUnknownFileTypes = false
            });
        }

        // ── 1. 工坊运行态元信息（含权限模式判定） ───────────────────────────
        app.MapGet("/api/workshop/meta", (HttpContext ctx) =>
        {
            var access = AdminAccessGuard.ResolveFrom(ctx.RequestServices).Evaluate(ctx);
            return Results.Json(new
            {
                ok = true,
                mode = access.ModeLabel,
                canWrite = access.Allowed,
                tokenConfigured = access.TokenConfigured,
                requireTokenOnly = access.RequireTokenOnly,
                serverVersion = ResolveServerVersion(),
                baseCardCount = modHost.BaseRepository.CardsById.Count,
                whitelistedCardCount = modHost.ValidVisualWhitelist.Count,
                registeredMechanicCount = modHost.Registry.MechanicsById.Count,
                vfxKeyCount = service.KnownVfxKeys.Count,
                enabledMods = modHost.EnabledModCount,
                modSetHash = modHost.ModSetHash,
                forcesGhostPoolIsolation = modHost.ForcesGhostPoolIsolation,
                modsRoot = modHost.ModsRootDirectory,
                skillInstallPaths = new[]
                {
                    "docs/skills/bazaar-mod-creator/SKILL.md",
                    ".agents/skills/bazaar-mod-creator/SKILL.md"
                }
            }, JsonOpts);
        });

        // ── 2. 原版 3,368 张白名单卡高速检索与单卡完整蓝图导出 ───────────────
        app.MapGet("/api/workshop/base-cards", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            string? query = q["q"].FirstOrDefault();
            string? type = q["type"].FirstOrDefault();
            string? hero = q["hero"].FirstOrDefault();
            string? size = q["size"].FirstOrDefault();
            string? tier = q["tier"].FirstOrDefault();
            bool whitelistOnly = !string.Equals(q["whitelistOnly"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase);
            int limit = int.TryParse(q["limit"].FirstOrDefault(), out var l) ? l : 40;
            int offset = int.TryParse(q["offset"].FirstOrDefault(), out var o) ? o : 0;

            var res = service.SearchBaseCards(query, type, hero, size, tier, whitelistOnly, limit, offset);
            return Results.Json(res, JsonOpts);
        });

        app.MapGet("/api/workshop/base-cards/{templateId}", (string templateId) =>
        {
            var blueprint = service.ExportBaseCardBlueprint(templateId);
            if (blueprint == null)
                return Results.Json(new { ok = false, error = $"未找到原版模板：{templateId}" }, JsonOpts, statusCode: 404);
            return Results.Json(blueprint, JsonOpts);
        });

        // ── 3. 权威中文规则积木目录（带永久 ETag） ────────────────────────────
        app.MapGet("/api/workshop/rule-catalog", (HttpContext ctx) =>
        {
            string etag = service.RuleCatalogEtag;
            if (ctx.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var inm)
                && inm.Any(v => string.Equals(v?.Trim(), etag, StringComparison.Ordinal)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600";
            return Results.Content(service.RuleCatalogJson, "application/json; charset=utf-8", Encoding.UTF8);
        });

        // ── 3b. 开放自定义沙盒机制目录 & 单条草稿机制即时对抗压测 ─────────────
        app.MapGet("/api/workshop/mechanics", () =>
        {
            return Results.Json(service.ListAllRegisteredMechanics(), JsonOpts);
        });

        app.MapPost("/api/workshop/mechanics/stress-test", async (HttpContext ctx) =>
        {
            StressTestMechanicRequestBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<StressTestMechanicRequestBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, passed = false, error = $"机制 JSON 格式错误：{ex.Message}" }, JsonOpts, statusCode: 400);
            }
            if (body?.Mechanic == null)
                return Results.Json(new { ok = false, passed = false, error = "请求体缺少 mechanic 字段。" }, JsonOpts, statusCode: 400);

            var res = await service.StressTestMechanicAsync(body.Mechanic, body.SampleParams, body.Matches ?? 20, ctx.RequestAborted);
            return Results.Json(res, JsonOpts);
        });

        // ── 4. 全部已发现 Mod 列表 ────────────────────────────────────────────
        app.MapGet("/api/workshop/mods", () =>
        {
            var statuses = modHost.GetAllModStatuses();
            var items = statuses.Select(s =>
            {
                int mechCount = s.CustomMechanicCount > 0
                    ? s.CustomMechanicCount
                    : (modHost.FindDiscoveredPackage(s.Id)?.Mechanics.Count ?? 0);
                return new
                {
                    id = s.Id,
                    name = s.Name,
                    version = s.Version,
                    author = s.Author,
                    description = s.Description,
                    scope = s.Scope,
                    enabled = s.Enabled,
                    loaded = s.Loaded,
                    affectsGhostPool = s.AffectsGhostPool,
                    cardCount = s.CustomCardCount,
                    patchCount = s.CardPatchCount,
                    eventCount = s.CustomEventCount,
                    monsterCount = s.CustomMonsterCount,
                    modeCount = s.CustomModeCount,
                    mechanicCount = mechCount,
                    customMechanicCount = mechCount,
                    mutatorCount = s.CustomMutatorCount,
                    assetCount = s.AssetCount,
                    packageHash = s.PackageHash,
                    lastValidationPassed = !s.Issues.Any(i => i.Severity == ModValidationSeverity.Error),
                    issues = s.Issues
                };
            }).ToList();

            return Results.Json(new
            {
                ok = true,
                modSetHash = modHost.ModSetHash,
                enabledModCount = modHost.EnabledModCount,
                mods = items
            }, JsonOpts);
        });

        // ── 5. 单个 Mod 完整工作区模型 ────────────────────────────────────────
        app.MapGet("/api/workshop/mods/{modId}", (string modId) =>
        {
            var (found, payload) = service.LoadModWorkspace(modId);
            if (!found || payload == null)
                return Results.Json(new { ok = false, error = $"未找到指定的 Mod：{modId}" }, JsonOpts, statusCode: 404);
            return Results.Json(payload, JsonOpts);
        });

        // ── 6. 新建或保存 Mod 草稿（写权限保护） ─────────────────────────────
        app.MapPost("/api/workshop/mods", async (HttpContext ctx) =>
        {
            if (RejectIfReadOnly(ctx, out var denied)) return denied!;

            WorkshopModWorkspace? workspace;
            try
            {
                workspace = await JsonSerializer.DeserializeAsync<WorkshopModWorkspace>(ctx.Request.Body, JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = $"解析工作区 JSON 失败：{ex.Message}" }, JsonOpts, statusCode: 400);
            }

            if (workspace == null)
                return Results.Json(new { ok = false, error = "请求体为空。" }, JsonOpts, statusCode: 400);

            var (ok, err, savedId, hash) = service.SaveWorkspaceAtomic(workspace);
            if (!ok)
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: 400);

            // 刷新已发现列表（不改变禁用草稿的启用状态）
            modHost.Reload();
            return Results.Json(new
            {
                ok = true,
                modId = savedId,
                packageHash = hash,
                messageZh = $"草稿「{savedId}」已原子保存（请运行 10 道防 Bug 铁闸体检通过后再发布热重载）。"
            }, JsonOpts);
        });

        // ── 7. 上传并自动校验/保存 PNG/JPEG 资产 + 草稿图实时预览 ─────────────
        app.MapPost("/api/workshop/mods/{modId}/assets", async (HttpContext ctx, string modId) =>
        {
            if (RejectIfReadOnly(ctx, out var denied)) return denied!;
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { ok = false, error = "请使用 multipart/form-data 上传 PNG/JPEG 图片字段 'file'。" }, JsonOpts, statusCode: 400);

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.Json(new { ok = false, error = "未检测到上传的图片文件（字段名：file）。" }, JsonOpts, statusCode: 400);

            if (file.Length > WorkshopValidatorService.MaxRawUploadBytes)
                return Results.Json(new { ok = false, error = "上传图片体积超出 8 MB 上限。" }, JsonOpts, statusCode: 400);

            string logicalName = form["logicalName"].FirstOrDefault() ?? file.FileName ?? "card_art.png";
            using var ms = new MemoryStream((int)Math.Min(file.Length, WorkshopValidatorService.MaxRawUploadBytes));
            await file.CopyToAsync(ms, ctx.RequestAborted);

            var (ok, err, assetInfo) = service.SavePngAsset(modId, logicalName, ms.ToArray());
            if (!ok)
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: 400);

            return Results.Json(assetInfo, JsonOpts);
        });

        app.MapGet("/api/workshop/mods/{modId}/assets/{*logicalPath}", (string modId, string logicalPath) =>
        {
            if (!service.TryResolveDraftAssetFile(modId, logicalPath, out var fullPath, out var ct))
                return Results.NotFound();
            return Results.File(fullPath, ct, enableRangeProcessing: true);
        });

        // ── 8. 执行 10 道防 Bug 铁闸 + 50 场真实 CombatSimulator 体检 ─────────
        app.MapPost("/api/workshop/mods/{modId}/validate", async (HttpContext ctx, string modId) =>
        {
            ValidateRequestBody? body = null;
            if (ctx.Request.ContentLength != 0)
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                string rawBody = await reader.ReadToEndAsync(ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        body = JsonSerializer.Deserialize<ValidateRequestBody>(rawBody, JsonOpts);
                    }
                    catch (Exception ex)
                    {
                        return Results.Json(new { ok = false, error = $"校验请求 JSON 格式错误：{ex.Message}" }, JsonOpts, statusCode: 400);
                    }
                }
            }

            int matches = body?.Matches ?? 50;
            int seed = body?.Seed ?? 2026131;
            var report = await service.ValidateAsync(modId, body?.Draft, matches, seed, ctx.RequestAborted);
            return Results.Json(report, JsonOpts);
        });

        // ── 9. 全绿强门禁发布并热重载进服务器（带病返回 HTTP 422 阻断） ───────
        app.MapPost("/api/workshop/mods/{modId}/publish-reload", async (HttpContext ctx, string modId) =>
        {
            if (RejectIfReadOnly(ctx, out var denied)) return denied!;

            var validation = await service.ValidateAsync(modId, inMemoryDraft: null, matches: 50, seed: 2026131, ct: ctx.RequestAborted);
            if (!validation.Passed)
            {
                return Results.Json(new
                {
                    ok = false,
                    blocked = true,
                    modId,
                    messageZh = $"安全门禁已拦截发布：存在 {validation.ErrorCount} 处错误未修复，绝不允许带病热重载到游戏服务器！",
                    validation
                }, JsonOpts, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var loadReport = modHost.SetModEnabledAndReload(modId, enabled: true);
            var clientManifest = modHost.Registry.BuildClientManifestDto();
            return Results.Json(new
            {
                ok = loadReport.Succeeded,
                modId,
                modSetHash = loadReport.ModSetHash,
                manifestHash = clientManifest.ManifestHash,
                messageZh = $"Mod「{modId}」已通过全部 10 道防 Bug 铁闸与 50 场真战斗压测，成功发布并热重载进服务器！",
                report = loadReport,
                validation
            }, JsonOpts);
        });

        // ── 10. 导出 & 严苛检疫导入 .bazaarmod.zip ────────────────────────────
        app.MapGet("/api/workshop/mods/{modId}/export", async (HttpContext ctx, string modId) =>
        {
            bool allowDraft = string.Equals(ctx.Request.Query["allowDraft"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var (ok, statusCode, err, zipBytes, fileName, validation) = await service.ExportZipWithReportAsync(modId, allowDraft, ctx.RequestAborted);
            if (!ok || zipBytes == null)
            {
                if (statusCode == StatusCodes.Status422UnprocessableEntity)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        blocked = true,
                        modId,
                        error = err,
                        messageZh = err,
                        validation
                    }, JsonOpts, statusCode: statusCode);
                }
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: statusCode);
            }

            return Results.File(zipBytes, "application/zip", fileDownloadName: fileName ?? $"{modId}.bazaarmod.zip");
        });

        app.MapPost("/api/workshop/mods/import", async (HttpContext ctx) =>
        {
            if (RejectIfReadOnly(ctx, out var denied)) return denied!;
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { ok = false, error = "请使用 multipart/form-data 上传 .bazaarmod.zip 文件（字段名：file）。" }, JsonOpts, statusCode: 400);

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.Json(new { ok = false, error = "未检测到上传的压缩包文件。" }, JsonOpts, statusCode: 400);
            if (file.Length > WorkshopValidatorService.MaxZipBytes)
                return Results.Json(new { ok = false, error = "压缩包体积超出 25 MB 上限。" }, JsonOpts, statusCode: 400);

            using var ms = new MemoryStream((int)file.Length);
            await file.CopyToAsync(ms, ctx.RequestAborted);

            bool allowDraft = string.Equals(ctx.Request.Query["allowDraftWithWarnings"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var (ok, statusCode, err, importedId, report) = await service.ImportZipAsync(ms.ToArray(), allowDraft, ctx.RequestAborted);

            return Results.Json(new
            {
                ok,
                modId = importedId,
                error = err,
                messageZh = ok
                    ? $"已安全检疫并导入 Mod「{importedId}」（请点击「发布并热重载」使其在游戏中生效）。"
                    : err,
                validation = report
            }, JsonOpts, statusCode: statusCode);
        });

        // ── 11. 删除自定义草稿 Mod（需写权限且保护系统目录） ──────────────────
        app.MapDelete("/api/workshop/mods/{modId}", (HttpContext ctx, string modId) =>
        {
            if (RejectIfReadOnly(ctx, out var denied)) return denied!;

            bool force = string.Equals(ctx.Request.Query["force"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var (ok, statusCode, err) = service.DeleteDraftMod(modId, allowSampleDelete: force);
            if (!ok)
                return Results.Json(new { ok = false, error = err }, JsonOpts, statusCode: statusCode);

            return Results.Json(new
            {
                ok = true,
                modId,
                modSetHash = modHost.ModSetHash,
                messageZh = $"已安全删除自定义 Mod「{modId}」并同步刷新服务器。"
            }, JsonOpts);
        });
    }

    // ── 公网/本机写权限安全门控（统一委托全服唯一 AdminAccessGuard，P0-36）────

    private static bool RejectIfReadOnly(HttpContext ctx, out IResult? deniedResult)
        => AdminAccessGuard.ResolveFrom(ctx.RequestServices).TryReject(ctx, JsonOpts, out deniedResult);

    private static string? ResolveWorkshopStaticDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "workshop"),
            Path.Combine(Environment.CurrentDirectory, "wwwroot", "workshop"),
            Path.Combine(Environment.CurrentDirectory, "src", "BazaarServer", "wwwroot", "workshop")
        };

        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && cur != null; i++, cur = cur.Parent)
        {
            candidates.Add(Path.Combine(cur.FullName, "src", "BazaarServer", "wwwroot", "workshop"));
            candidates.Add(Path.Combine(cur.FullName, "wwwroot", "workshop"));
        }

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html")))
                return Path.GetFullPath(dir);
        }
        return null;
    }
}
