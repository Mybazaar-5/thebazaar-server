using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Planner;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace BazaarServer.Endpoints;

/// <summary>
/// M4 编排台静态单页托管与全部 `/api/planner/*` REST 端点：
/// - `/planner`、`/planner/`、`/planner/index.html` 注入 `{{ASSET_PREFIX}}`、严格支持 HEAD、设置 CSP 与 `X-Content-Type-Options: nosniff`；
/// - 鉴权严格走 `PlannerSessionStore`（`X-Planner-Session: pln_...` 或有效游戏 `Bearer`），绝不读取 `X-Account-Id` / legacy username；
/// - 全量响应统一 camelCase JSON；全部业务 404 一律携带非空 JSON `code`（防止游戏内插件误判 `ENDPOINT_MISSING`）。
/// </summary>
public static class PlannerEndpoints
{
    public const int MaxBlueprintBodyBytes = 300 * 1024;
    public const int MaxDefaultBodyBytes = 1024 * 1024;
    public const int MaxAuthBodyBytes = 64 * 1024;

    private const string PlannerCspHeader =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'none'; frame-ancestors 'none'";

    public static readonly JsonSerializerOptions PlannerJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>注册编排台静态前端与全部 `/api/planner/*` 端点（兼容 `PlannerEndpoints.Map(app)` 调用风格）。</summary>
    public static void Map(WebApplication app) => app.MapPlannerEndpoints();

    /// <summary>注册编排台静态前端与全部 `/api/planner/*` 端点。</summary>
    public static void MapPlannerEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var accounts = app.Services.GetRequiredService<AccountStore>();
        var ruleStore = app.Services.GetRequiredService<PlayerRuleStore>();
        var gameSessions = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Persistence>();
        var modHost = app.Services.GetRequiredService<ServerModHost>();
        var communityMods = app.Services.GetRequiredService<CommunityModStore>();
        var cardPool = app.Services.GetRequiredService<CardPool>();
        var monsterPoolStore = app.Services.GetRequiredService<MonsterPoolStore>();
        var runtimeSnapshots = app.Services.GetRequiredService<ServerRuntimeSnapshotProvider>();
        var ghostPool = app.Services.GetRequiredService<PvpGhostPool>();
        var plannerSessions = app.Services.GetRequiredService<PlannerSessionStore>();
        var lineupStore = app.Services.GetRequiredService<GhostLineupStore>();
        var blueprintService = app.Services.GetRequiredService<RunBlueprintService>();
        var seedFinder = app.Services.GetRequiredService<EventSeedFinderService>();

        // ── 0. 静态页面与前端资产托管（仿 WorkshopEndpoints，强制注入 {{ASSET_PREFIX}} + CSP + HEAD 支持）──
        string? plannerStaticDir = ResolvePlannerStaticDirectory();

        async Task WritePlannerIndexAsync(HttpContext ctx)
        {
            if (plannerStaticDir == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (!HttpMethods.IsHead(ctx.Request.Method))
                    await ctx.Response.WriteAsync("未找到 wwwroot/planner 静态前端目录，请确认 BazaarServer 完整发布。", Encoding.UTF8, ctx.RequestAborted);
                return;
            }

            string indexPath = Path.Combine(plannerStaticDir, "index.html");
            if (!File.Exists(indexPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (!HttpMethods.IsHead(ctx.Request.Method))
                    await ctx.Response.WriteAsync("未找到 planner/index.html", Encoding.UTF8, ctx.RequestAborted);
                return;
            }

            string rawPath = ctx.Request.Path.Value ?? string.Empty;
            bool inPlannerDir = rawPath.EndsWith("/", StringComparison.Ordinal)
                || rawPath.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
            string prefix = inPlannerDir ? "./" : "./planner/";

            string html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, ctx.RequestAborted);
            html = html.Replace("{{ASSET_PREFIX}}", prefix, StringComparison.Ordinal);
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Content-Security-Policy"] = PlannerCspHeader;
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-store";
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
                if (path.Equals("/planner", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/planner/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/planner/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    await WritePlannerIndexAsync(ctx);
                    return;
                }
            }
            await next();
        });

        if (plannerStaticDir != null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(plannerStaticDir),
                RequestPath = "/planner",
                ServeUnknownFileTypes = false,
                OnPrepareResponse = staticCtx =>
                {
                    staticCtx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    // 每次都向服务器确认（有 ETag，没变化只回 304）：服务端更新后不会出现「新页面 + 旧脚本」对不上
                    staticCtx.Context.Response.Headers["Cache-Control"] = "no-cache";
                },
            });
        }

        // ── 1. 鉴权端点 ───────────────────────────────────────────────────────

        // POST /api/planner/web-ticket（只认游戏 Bearer，禁 legacy fallback）
        app.MapPost("/api/planner/web-ticket", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveBearerOnlyForTicket(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return JsonError(auth.FailureStatusCode, auth.ErrorCode ?? "AUTH_REQUIRED", auth.ErrorMessageZh ?? "请使用游戏内登录令牌。");

            var issued = auth.Principal.Account != null
                ? plannerSessions.IssueWebTicket(auth.Principal.Account)
                : plannerSessions.IssueWebTicket(auth.Principal.AccountId, auth.Principal.Username);

            return Results.Json(new
            {
                ticket = issued.Ticket,
                path = issued.Path,
                expiresInSeconds = issued.ExpiresInSeconds,
            }, PlannerJsonOpts);
        });

        // POST /api/planner/web-ticket/exchange（无需鉴权，原子消费一次性票据）
        app.MapPost("/api/planner/web-ticket/exchange", async (HttpContext ctx) =>
        {
            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxAuthBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                string? ticket = doc != null && TryGetPropertyCaseInsensitive(doc.RootElement, "ticket", out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;

                var res = plannerSessions.ExchangeWebTicket(ticket);
                if (!res.Ok || res.Session == null)
                    return JsonError(res.StatusCode, res.ErrorCode ?? "TICKET_INVALID", res.ErrorMessageZh ?? "登录票据无效或已失效");

                return Results.Json(new
                {
                    token = res.Session.Token,
                    expiresAtUtc = res.Session.ExpiresAtUtc,
                    username = res.Session.Username,
                    accountId = res.Session.AccountId,
                }, PlannerJsonOpts);
            }
        });

        // POST /api/planner/auth/login（无需鉴权，已设口令账号浏览器直登）
        app.MapPost("/api/planner/auth/login", async (HttpContext ctx) =>
        {
            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxAuthBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                string? username = doc != null && TryGetPropertyCaseInsensitive(doc.RootElement, "username", out var uEl) && uEl.ValueKind == JsonValueKind.String
                    ? uEl.GetString()
                    : null;
                string? password = doc != null && TryGetPropertyCaseInsensitive(doc.RootElement, "password", out var pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString()
                    : null;

                var res = plannerSessions.LoginWithPassword(
                    accounts,
                    username,
                    password,
                    PlannerSessionStore.ResolveClientIp(ctx));

                if (!res.Ok || res.Session == null)
                    return JsonError(res.StatusCode, res.ErrorCode ?? "BAD_CREDENTIALS", res.ErrorMessageZh ?? "登录失败");

                return Results.Json(new
                {
                    token = res.Session.Token,
                    expiresAtUtc = res.Session.ExpiresAtUtc,
                    username = res.Session.Username,
                    accountId = res.Session.AccountId,
                }, PlannerJsonOpts);
            }
        });

        // ── 2. 上下文与目录端点 ───────────────────────────────────────────────

        // GET /api/planner/me/context
        app.MapGet("/api/planner/me/context", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            string accountId = auth.Principal.AccountId;
            string username = auth.Principal.Username;
            var rules = ruleStore.Get(accountId);

            // 种子一律是固定格式的码（规范写法）
            string? currentSeedText = DailyChallengeSeedGenerator.NormalizeSeedCode(rules.CustomSeedText);

            var heroes = PlannerCatalogConstants.SupportedHeroes
                .Select(h => new { id = h.Id, nameZh = h.NameZh })
                .ToList();

            var selectedModSet = new HashSet<string>(
                rules.SelectedModIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var catalogMods = communityMods.ListCatalog(accountId, isAdmin: false, search: null);
            var mods = new List<object>(catalogMods.Count);
            var seenModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in catalogMods)
            {
                if (!seenModIds.Add(m.ModId))
                    continue;
                var pkg = communityMods.ResolvePackage(m.ModId) ?? modHost.FindDiscoveredPackage(m.ModId);
                bool isGameplay = pkg != null
                    ? ServerModHost.IsGameplayAffectingPackage(pkg)
                    : (m.CustomCardCount > 0 || m.CustomEventCount > 0 || m.CustomMonsterCount > 0 || m.CardPatchCount > 0);
                mods.Add(new
                {
                    modId = m.ModId,
                    name = string.IsNullOrWhiteSpace(m.Name) ? m.ModId : m.Name,
                    version = m.Version ?? "",
                    author = m.Author ?? "",
                    isServerDefault = m.IsServerDefault,
                    isGameplay,
                    selected = m.IsServerDefault || selectedModSet.Contains(m.ModId),
                });
            }

            var lineups = lineupStore.ListLineups(accountId);
            var activeRun = await TryBuildActiveRunSummaryAsync(gameSessions, db, accountId, ctx.RequestAborted);
            var recentRun = TryBuildRecentRunSummary(db, modHost, accountId);

            var dayBaseSlots = new Dictionary<string, int>(PlannerCatalogConstants.DayBaseSlots.Count);
            foreach (var kv in PlannerCatalogConstants.DayBaseSlots.OrderBy(k => k.Key))
                dayBaseSlots[kv.Key.ToString()] = kv.Value;

            return Results.Json(new
            {
                username,
                accountId,
                rules,
                currentSeedText,
                heroes,
                mods,
                lineups,
                activeRun,
                recentRun,
                limits = new
                {
                    maxLineups = PlannerCatalogConstants.MaxLineups,
                    maxLineupDays = PlannerCatalogConstants.MaxLineupDays,
                    maxSeedTextLength = PlannerCatalogConstants.MaxSeedTextLength,
                    maxDisplayName = PlannerCatalogConstants.MaxDisplayName,
                    maxLineupName = PlannerCatalogConstants.MaxLineupName,
                    maxDescription = PlannerCatalogConstants.MaxDescription,
                },
                boardEditRules = new
                {
                    quantityAttributes = PlannerCatalogConstants.QuantityAttributes,
                    attributeLabelsZh = PlannerCatalogConstants.AttributeLabelsZh,
                    quantityMax = PlannerCatalogConstants.QuantityMax,
                    healthMin = PlannerCatalogConstants.HealthMin,
                    healthMax = PlannerCatalogConstants.HealthMax,
                    levelMax = PlannerCatalogConstants.LevelMax,
                    prestigeMax = PlannerCatalogConstants.PrestigeMax,
                    dayBaseSlots,
                },
            }, PlannerJsonOpts);
        });

        // GET /api/planner/catalog/cards
        app.MapGet("/api/planner/catalog/cards", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var query = ctx.Request.Query;
            var ids = ParseCsv(query["ids"]);
            var selectedModIds = ParseCsv(query["selectedModIds"]);
            string q = (query["q"].ToString() ?? string.Empty).Trim();
            string typeFilter = (query["type"].ToString() ?? string.Empty).Trim();
            string heroFilter = (query["hero"].ToString() ?? string.Empty).Trim();
            string sizeFilter = (query["size"].ToString() ?? string.Empty).Trim();
            int limit = int.TryParse(query["limit"].ToString(), out int parsedLimit) && parsedLimit > 0
                ? Math.Clamp(parsedLimit, 1, 100)
                : 40;

            var modCtx = modHost.ResolveRunModContext(
                selectedModIds,
                cardPool,
                monsterPoolStore.SnapshotList(),
                runtimeSnapshots.Current.ArchetypeIndex);

            if (ids.Count > 0)
            {
                var items = new List<object>(Math.Min(ids.Count, 100));
                GameDataRepository? allModsFallbackRepo = null;

                GameDataRepository GetAllModsFallbackRepo()
                {
                    if (allModsFallbackRepo != null)
                        return allModsFallbackRepo;
                    var allModIds = communityMods.ListCatalog(auth.Principal.AccountId, isAdmin: false, search: null)
                        .Select(m => m.ModId)
                        .ToList();
                    allModsFallbackRepo = modHost.ResolveEffectiveRegistryForMods(allModIds).ActiveRepository;
                    return allModsFallbackRepo;
                }

                int taken = 0;
                foreach (string id in ids)
                {
                    if (taken >= 100) break;
                    var tpl = modCtx.Repository.GetCard(id);
                    var sourceRepo = modCtx.Repository;
                    if (tpl == null)
                    {
                        var fbRepo = GetAllModsFallbackRepo();
                        tpl = fbRepo.GetCard(id);
                        sourceRepo = fbRepo;
                    }
                    if (tpl == null)
                        continue;

                    items.Add(BuildCatalogCardDto(tpl, sourceRepo));
                    taken++;
                }

                return Results.Json(new { total = items.Count, items }, PlannerJsonOpts);
            }

            var matched = new List<(BazaarCore.Models.CardTemplate Tpl, string NameZh, string NameEn)>();
            foreach (var tpl in modCtx.Repository.CardsById.Values)
            {
                if (tpl == null || string.IsNullOrWhiteSpace(tpl.Id))
                    continue;

                bool isItem = string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase);
                bool isSkill = string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
                if (!isItem && !isSkill)
                    continue;

                if (!modCtx.Pool.IsAssetValid(tpl.Id))
                    continue;
                if (MonsterPool.IsBlockedDebugOrBlankArtSkill(tpl))
                    continue;
                var poolTpl = modCtx.Pool.Get(tpl.Id);
                if (poolTpl != null && CardPool.IsExcludedDevOrStoryTemplate(poolTpl))
                    continue;

                if (!string.IsNullOrEmpty(typeFilter) &&
                    !string.Equals(tpl.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(sizeFilter))
                {
                    if (!isItem || !string.Equals(tpl.Size, sizeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!string.IsNullOrEmpty(heroFilter))
                {
                    bool heroMatch = tpl.Heroes == null
                        || tpl.Heroes.Count == 0
                        || tpl.Heroes.Any(h =>
                            string.Equals(h, heroFilter, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase));
                    if (!heroMatch)
                        continue;
                }

                string nameZh = ResolveCatalogCardNameZh(tpl, modCtx.Repository);
                string nameEn = !string.IsNullOrWhiteSpace(tpl.DisplayTitle)
                    ? tpl.DisplayTitle.Trim()
                    : (tpl.InternalName ?? tpl.Id);

                if (!string.IsNullOrEmpty(q))
                {
                    bool qMatch = nameZh.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || nameEn.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (tpl.InternalName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                        || tpl.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
                    if (!qMatch)
                        continue;
                }

                matched.Add((tpl, nameZh, nameEn));
            }

            matched.Sort((a, b) =>
            {
                if (!string.IsNullOrEmpty(q))
                {
                    bool aExact = a.NameZh.Equals(q, StringComparison.OrdinalIgnoreCase) || a.NameEn.Equals(q, StringComparison.OrdinalIgnoreCase);
                    bool bExact = b.NameZh.Equals(q, StringComparison.OrdinalIgnoreCase) || b.NameEn.Equals(q, StringComparison.OrdinalIgnoreCase);
                    if (aExact != bExact) return aExact ? -1 : 1;

                    bool aPrefix = a.NameZh.StartsWith(q, StringComparison.OrdinalIgnoreCase) || a.NameEn.StartsWith(q, StringComparison.OrdinalIgnoreCase);
                    bool bPrefix = b.NameZh.StartsWith(q, StringComparison.OrdinalIgnoreCase) || b.NameEn.StartsWith(q, StringComparison.OrdinalIgnoreCase);
                    if (aPrefix != bPrefix) return aPrefix ? -1 : 1;
                }

                int cmp = StringComparer.Ordinal.Compare(a.NameZh, b.NameZh);
                if (cmp != 0) return cmp;
                return StringComparer.OrdinalIgnoreCase.Compare(a.Tpl.Id, b.Tpl.Id);
            });

            int total = matched.Count;
            var pageItems = new List<object>(Math.Min(limit, total));
            for (int i = 0; i < total && i < limit; i++)
                pageItems.Add(BuildCatalogCardDto(matched[i].Tpl, modCtx.Repository));

            return Results.Json(new { total, items = pageItems }, PlannerJsonOpts);
        });

        // GET /api/planner/catalog/events
        app.MapGet("/api/planner/catalog/events", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            string hero = ctx.Request.Query["hero"].ToString();
            var selectedModIds = ParseCsv(ctx.Request.Query["selectedModIds"]);
            var items = seedFinder.ListCatalogEvents(hero, selectedModIds);
            return Results.Json(new { items }, PlannerJsonOpts);
        });

        // POST /api/planner/catalog/describe：按品阶 / 附魔渲染卡牌效果说明（官方中文 Tooltip，数值按品阶填好），挑对手和编辑物品时显示
        app.MapPost("/api/planner/catalog/describe", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                var root = doc?.RootElement ?? default;
                var selectedModIds = ReadOptionalStringList(root, "selectedModIds");
                var modCtx = modHost.ResolveRunModContext(
                    selectedModIds,
                    cardPool,
                    monsterPoolStore.SnapshotList(),
                    runtimeSnapshots.Current.ArchetypeIndex);
                var repo = modCtx.Repository;

                var items = new List<object>();
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("cards", out var cardsEl)
                    && cardsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in cardsEl.EnumerateArray())
                    {
                        if (items.Count >= 60) break;
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        string? templateId = ReadOptionalString(el, "templateId")?.Trim();
                        if (string.IsNullOrEmpty(templateId)) continue;

                        var tpl = repo.GetCard(templateId);
                        if (tpl == null) continue;

                        var tiers = GhostBoardValidator.GetSupportedTiers(tpl);
                        string? reqTier = ReadOptionalString(el, "tier");
                        string tier = tiers.FirstOrDefault(t => string.Equals(t, reqTier, StringComparison.OrdinalIgnoreCase)) ?? tiers[0];
                        string? reqEnch = ReadOptionalString(el, "enchantment")?.Trim();
                        string? ench = string.IsNullOrEmpty(reqEnch)
                            ? null
                            : GhostBoardValidator.GetSupportedEnchantments(tpl).FirstOrDefault(e => string.Equals(e, reqEnch, StringComparison.OrdinalIgnoreCase));

                        List<string> lines;
                        try
                        {
                            var inst = new BazaarCore.Runtime.CardInstance(tpl, TierHelper.ParseTier(tier), ench, "describe");
                            // 可选：按阵容里的当前数值（伤害 / 护盾 / 冷却等白名单键）渲染，让说明和编辑后的数字一致
                            if (el.TryGetProperty("attributes", out var attrsEl) && attrsEl.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in attrsEl.EnumerateObject())
                                {
                                    string key = prop.Name.Trim();
                                    if (!PlannerCatalogConstants.QuantityAttributeSet.Contains(key)) continue;
                                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double target)) continue;
                                    if (!double.IsFinite(target)) continue;
                                    target = Math.Clamp(Math.Round(target), 0, PlannerCatalogConstants.QuantityMax);
                                    var canonKey = PlannerCatalogConstants.QuantityAttributes.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                                    if (!inst.BaseAttributes.TryGetValue(canonKey, out double baseVal) || !double.IsFinite(baseVal)) continue;
                                    if (canonKey == "CooldownMax" && !(baseVal > 0)) continue;
                                    inst.PermanentAdd[canonKey] = target - baseVal;
                                }
                            }
                            lines = TooltipFormatter.FormatTooltips(inst, null, repo.Localization);
                        }
                        catch
                        {
                            lines = new List<string>();
                        }

                        items.Add(new
                        {
                            templateId = tpl.Id,
                            tier,
                            enchantment = ench,
                            nameZh = GhostBoardValidator.ResolveCardNameZh(tpl, repo),
                            lines,
                        });
                    }
                }
                return Results.Json(new { items }, PlannerJsonOpts);
            }
        });

        // ── 3. 镜像池端点 ─────────────────────────────────────────────────────

        // POST /api/planner/ghost-pool/search
        app.MapPost("/api/planner/ghost-pool/search", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                var root = doc?.RootElement ?? default;
                var searchReq = new GhostPoolSearchRequest
                {
                    Day = ReadOptionalInt(root, "day"),
                    Hero = ReadOptionalString(root, "hero"),
                    PowerBucket = ReadOptionalString(root, "powerBucket"),
                    Source = ReadOptionalString(root, "source"),
                    Partition = ReadOptionalString(root, "partition"),
                    HpMin = ReadOptionalInt(root, "hpMin"),
                    HpMax = ReadOptionalInt(root, "hpMax"),
                    LevelMin = ReadOptionalInt(root, "levelMin"),
                    LevelMax = ReadOptionalInt(root, "levelMax"),
                    Keyword = ReadOptionalString(root, "keyword"),
                    Page = ReadOptionalInt(root, "page") ?? 1,
                    PageSize = ReadOptionalInt(root, "pageSize") ?? 20,
                };
                var selectedModIds = ReadOptionalStringList(root, "selectedModIds");

                var modCtx = modHost.ResolveRunModContext(
                    selectedModIds,
                    cardPool,
                    monsterPoolStore.SnapshotList(),
                    runtimeSnapshots.Current.ArchetypeIndex);

                var allowedModIds = new HashSet<string>(modCtx.ActiveModIds, StringComparer.OrdinalIgnoreCase);

                // 延迟构建全服已安装 Mod 的自定义模板归属映射（仅在命中未知模板时触发一次）
                Dictionary<string, string>? installedCustomOrigins = null;
                string? ResolveTemplateModId(string templateId)
                {
                    var card = modCtx.Repository.GetCard(templateId);
                    if (card != null)
                        return card.IsCustomModCard ? card.CustomModOrigin : null;

                    if (modHost.BaseRepository.GetCard(templateId) != null)
                        return null;

                    if (installedCustomOrigins == null)
                    {
                        installedCustomOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in communityMods.ListCatalog(auth.Principal.AccountId, isAdmin: false, search: null))
                        {
                            var pkg = communityMods.ResolvePackage(m.ModId) ?? modHost.FindDiscoveredPackage(m.ModId);
                            if (pkg == null) continue;
                            foreach (var customCard in pkg.Cards)
                            {
                                if (!string.IsNullOrWhiteSpace(customCard.CustomId))
                                    installedCustomOrigins[customCard.CustomId] = pkg.Manifest.Id;
                            }
                        }
                    }

                    return installedCustomOrigins.TryGetValue(templateId, out var originModId)
                        ? originModId
                        : null;
                }

                var result = ghostPool.SearchGhosts(
                    searchReq,
                    effectiveRepo: modCtx.Repository,
                    allowedModIds: allowedModIds,
                    resolveTemplateModId: ResolveTemplateModId);

                return Results.Json(result, PlannerJsonOpts);
            }
        });

        // GET /api/planner/ghost-pool/{boardId}
        app.MapGet("/api/planner/ghost-pool/{boardId}", (HttpContext ctx, string boardId) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var board = ghostPool.GetGhostById(boardId, modHost.ActiveRepository);
            if (board == null)
                return JsonError(StatusCodes.Status404NotFound, "NOT_FOUND", "这个镜像阵容不存在");

            return Results.Json(board, PlannerJsonOpts);
        });

        // ── 4. 剧本端点 ───────────────────────────────────────────────────────

        // GET /api/planner/lineups
        app.MapGet("/api/planner/lineups", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var items = lineupStore.ListLineups(auth.Principal.AccountId);
            return Results.Json(new { items }, PlannerJsonOpts);
        });

        // GET /api/planner/lineups/{id}
        app.MapGet("/api/planner/lineups/{id}", (HttpContext ctx, string id) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var lineup = lineupStore.GetLineup(auth.Principal.AccountId, id);
            if (lineup == null)
                return JsonError(StatusCodes.Status404NotFound, "NOT_FOUND", "方案不存在（可能已经删掉了）");

            return Results.Json(new { lineup }, PlannerJsonOpts);
        });

        // POST /api/planner/lineups
        app.MapPost("/api/planner/lineups", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                    return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "请求体必须是 JSON 对象");

                var root = doc.RootElement;
                string? lineupId = ReadOptionalString(root, "lineupId");
                string? name = ReadOptionalString(root, "name");
                string? description = ReadOptionalString(root, "description");
                var selectedModIds = ReadOptionalStringList(root, "selectedModIds");

                Dictionary<string, JsonElement>? days = null;
                if (TryGetPropertyCaseInsensitive(root, "days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Object)
                {
                    days = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var prop in daysEl.EnumerateObject())
                        days[prop.Name] = prop.Value;
                }

                // 配套种子：只有请求里带了 seedText 字段才改（null / "" = 清掉）；不带就保持原样
                bool seedProvided = TryGetPropertyCaseInsensitive(root, "seedText", out var seedEl)
                    && (seedEl.ValueKind == JsonValueKind.String || seedEl.ValueKind == JsonValueKind.Null);
                string? seedText = seedProvided && seedEl.ValueKind == JsonValueKind.String ? seedEl.GetString() : null;
                string? seedHero = ReadOptionalString(root, "seedHero");

                var res = lineupStore.SaveLineup(
                    auth.Principal.AccountId,
                    lineupId,
                    name,
                    description,
                    selectedModIds,
                    days,
                    seedProvided: seedProvided,
                    seedText: seedText,
                    seedHero: seedHero);

                if (!res.Ok || res.Data == null)
                    return JsonError(res.HttpStatus, res.ErrorCode ?? "BOARD_INVALID", res.ErrorMessage ?? "保存方案失败", res.Details);

                return Results.Json(new { lineup = res.Data, warnings = res.Warnings }, PlannerJsonOpts);
            }
        });

        // POST /api/planner/lineups/from-run：把进行中 / 最近结束的一局存成方案（实际遇到的天 + 自动补到第 N 天），方便改完再分享
        app.MapPost("/api/planner/lineups/from-run", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                var root = doc?.RootElement ?? default;
                string? source = root.ValueKind == JsonValueKind.Object ? ReadOptionalString(root, "source") : null;
                int? fillToDay = root.ValueKind == JsonValueKind.Object ? ReadOptionalInt(root, "fillToDay") : null;

                var res = await blueprintService.CreateLineupFromRunAsync(
                    auth.Principal.AccountId,
                    source,
                    fillToDay,
                    ghostPool,
                    ctx.RequestAborted);
                if (!res.Ok || res.Data == null)
                    return JsonError(res.HttpStatus, res.ErrorCode ?? "BAD_REQUEST", res.ErrorMessage ?? "存成方案失败");

                var full = lineupStore.GetLineup(auth.Principal.AccountId, res.Data.Lineup.LineupId);
                return Results.Json(new
                {
                    lineup = full,
                    facedDays = res.Data.FacedDays,
                    pinnedDays = res.Data.PinnedDays,
                    autoFilledDays = res.Data.AutoFilledDays,
                    skippedDays = res.Data.SkippedDays,
                    messageZh = res.Data.MessageZh,
                }, PlannerJsonOpts);
            }
        });

        // DELETE /api/planner/lineups/{id}
        app.MapDelete("/api/planner/lineups/{id}", (HttpContext ctx, string id) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (found, rulesChanged) = lineupStore.DeleteLineup(auth.Principal.AccountId, id);
            if (!found)
                return JsonError(StatusCodes.Status404NotFound, "NOT_FOUND", "方案不存在");

            return Results.Json(new { ok = true, rulesChanged }, PlannerJsonOpts);
        });

        // POST /api/planner/lineups/validate-board
        app.MapPost("/api/planner/lineups/validate-board", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                    return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "请求体必须是 JSON 对象");

                var root = doc.RootElement;
                int day = ReadOptionalInt(root, "day") ?? 1;
                var selectedModIds = ReadOptionalStringList(root, "selectedModIds");
                if (!TryGetPropertyCaseInsensitive(root, "board", out var boardEl) || boardEl.ValueKind != JsonValueKind.Object)
                    return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "缺少盘面数据 board");

                var vr = lineupStore.ValidateBoard(day, boardEl, selectedModIds);
                return Results.Json(new
                {
                    ok = vr.Ok,
                    board = vr.Board,
                    errors = vr.Errors,
                    warnings = vr.Warnings,
                }, PlannerJsonOpts);
            }
        });

        // POST /api/planner/lineups/capture-sandbox（注意契约字段名为 day！）
        app.MapPost("/api/planner/lineups/capture-sandbox", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                var root = doc?.RootElement ?? default;
                string? lineupId = ReadOptionalString(root, "lineupId");
                string? newLineupName = ReadOptionalString(root, "newLineupName");
                int targetDay = ReadOptionalInt(root, "day") ?? ReadOptionalInt(root, "targetDay") ?? 1;

                var session = FindSessionForAccount(gameSessions, auth.Principal.AccountId);
                if (session == null)
                    return JsonError(StatusCodes.Status409Conflict, "NOT_IN_SANDBOX", "当前不在沙盒局中，请先进入沙盒构筑实验室");

                PvpGhostPool.GhostBoard? snapshot = null;
                List<string>? runModIds = null;

                try
                {
                    await session.Gate.WaitAsync(ctx.RequestAborted);
                }
                catch (ObjectDisposedException)
                {
                    return JsonError(StatusCodes.Status409Conflict, "NOT_IN_SANDBOX", "当前不在沙盒局中，请先进入沙盒构筑实验室");
                }

                try
                {
                    var handle = SessionRegistry.Get(session.Sid);
                    if (handle == null || Persistence.IsTerminalState(handle.Run.State) || !handle.Run.IsSandboxRun)
                        return JsonError(StatusCodes.Status409Conflict, "NOT_IN_SANDBOX", "当前不在沙盒局中，请先进入沙盒构筑实验室");

                    snapshot = PvpGhostPool.SnapshotBoardFromRun(
                        handle.Run,
                        handle.Engine.ActiveRepository,
                        targetDay,
                        displayName: null,
                        source: "sandbox");
                    runModIds = handle.Run.CustomRules?.SelectedModIds?.ToList();
                }
                finally
                {
                    try { session.Gate.Release(); } catch (ObjectDisposedException) { }
                }

                var capRes = lineupStore.CaptureDayBoard(
                    auth.Principal.AccountId,
                    lineupId,
                    newLineupName,
                    targetDay,
                    snapshot,
                    sourceType: "sandbox",
                    selectedModIds: runModIds);

                if (!capRes.Ok || capRes.Data == null)
                    return JsonError(capRes.HttpStatus, capRes.ErrorCode ?? "BOARD_INVALID", capRes.ErrorMessage ?? "保存沙盒盘面失败", capRes.Details);

                return Results.Json(new { lineup = capRes.Data, day = targetDay }, PlannerJsonOpts);
            }
        });

        // POST /api/planner/lineups/capture-faced
        app.MapPost("/api/planner/lineups/capture-faced", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                var root = doc?.RootElement ?? default;
                string accountId = auth.Principal.AccountId;
                string? requestedRunId = ReadOptionalString(root, "runId");
                int sourceDay = ReadOptionalInt(root, "sourceDay") ?? 0;
                string? lineupId = ReadOptionalString(root, "lineupId");
                string? newLineupName = ReadOptionalString(root, "newLineupName");
                int targetDay = ReadOptionalInt(root, "targetDay") ?? sourceDay;

                if (sourceDay < 1 || sourceDay > PlannerCatalogConstants.MaxLineupDays)
                    return JsonError(StatusCodes.Status404NotFound, "NO_FACED_GHOST", "这一天没有记录到对手");

                string? targetRunId = await ResolveOwnedRunIdForCaptureAsync(
                    gameSessions, db, accountId, requestedRunId, ctx.RequestAborted);
                if (string.IsNullOrWhiteSpace(targetRunId))
                    return JsonError(StatusCodes.Status404NotFound, "NO_FACED_GHOST", "没有找到可收藏的对局对手记录");

                var facedGhosts = db.LoadRunGhosts(targetRunId, "faced");
                if (!facedGhosts.TryGetValue(sourceDay, out var board) || board == null)
                    return JsonError(StatusCodes.Status404NotFound, "NO_FACED_GHOST", $"第 {sourceDay} 天没有记录到对手");

                var rules = ruleStore.Get(accountId);
                var capRes = lineupStore.CaptureDayBoard(
                    accountId,
                    lineupId,
                    newLineupName,
                    targetDay,
                    board,
                    sourceType: "faced",
                    selectedModIds: rules.SelectedModIds);

                if (!capRes.Ok || capRes.Data == null)
                    return JsonError(capRes.HttpStatus, capRes.ErrorCode ?? "BOARD_INVALID", capRes.ErrorMessage ?? "收藏对手失败", capRes.Details);

                return Results.Json(new { lineup = capRes.Data }, PlannerJsonOpts);
            }
        });

        // ── 5. 规则应用与随机种子 ─────────────────────────────────────────────

        // POST /api/planner/apply-rules（严格部分更新语义：仅修改请求 JSON 中出现的字段）
        app.MapPost("/api/planner/apply-rules", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, doc) = await ReadJsonDocumentAsync(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;
            using (doc)
            {
                if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                    return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "请求体必须是 JSON 对象");

                string accountId = auth.Principal.AccountId;
                var root = doc.RootElement;
                var rules = ruleStore.Get(accountId);

                // 1) customSeedText
                if (TryGetPropertyCaseInsensitive(root, "customSeedText", out var seedEl))
                {
                    if (seedEl.ValueKind == JsonValueKind.Null)
                    {
                        rules.CustomSeedText = null;
                        if (rules.SpecialModeId != ESpecialModeId.DailyChallenge)
                            rules.FixedRunSeed = null;
                    }
                    else if (seedEl.ValueKind == JsonValueKind.String)
                    {
                        string rawSeed = seedEl.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(rawSeed))
                        {
                            rules.CustomSeedText = null;
                            if (rules.SpecialModeId != ESpecialModeId.DailyChallenge)
                                rules.FixedRunSeed = null;
                        }
                        else
                        {
                            // 只认固定格式的种子码（S + 7 位）
                            if (!DailyChallengeSeedGenerator.TryParseSeedText(rawSeed, out _, out string strictCode))
                                return JsonError(StatusCodes.Status400BadRequest, "SEED_INVALID", "种子码格式不对：应该是 S 开头的 8 位码，比如 S3KZ9QW1。可以点「随机」生成，或用别人给你的码");

                            rules.CustomSeedText = strictCode;
                            if (rules.SpecialModeId != ESpecialModeId.DailyChallenge)
                                rules.FixedRunSeed = null;
                        }
                    }
                    else
                    {
                        return JsonError(StatusCodes.Status400BadRequest, "SEED_INVALID", "种子必须是字符串");
                    }
                }

                // 2) ghostOpponentMode（同时兼容字符串与 0/1）
                if (TryGetPropertyCaseInsensitive(root, "ghostOpponentMode", out var modeEl))
                {
                    if (!TryParseGhostOpponentMode(modeEl, out var parsedMode))
                        return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "对手模式无效，仅支持 Random 或 FixedLineup");
                    rules.GhostOpponentMode = parsedMode;
                }

                // 3) selectedGhostLineupId
                if (TryGetPropertyCaseInsensitive(root, "selectedGhostLineupId", out var lineupEl))
                {
                    if (lineupEl.ValueKind == JsonValueKind.Null)
                    {
                        rules.SelectedGhostLineupId = null;
                    }
                    else if (lineupEl.ValueKind == JsonValueKind.String)
                    {
                        string? lid = lineupEl.GetString()?.Trim();
                        if (string.IsNullOrEmpty(lid))
                        {
                            rules.SelectedGhostLineupId = null;
                        }
                        else
                        {
                            if (db.LoadGhostLineup(accountId, lid) == null)
                                return JsonError(StatusCodes.Status400BadRequest, "LINEUP_NOT_FOUND", "所选方案不存在");
                            rules.SelectedGhostLineupId = lid;
                        }
                    }
                    else
                    {
                        return JsonError(StatusCodes.Status400BadRequest, "BAD_REQUEST", "selectedGhostLineupId 格式无效");
                    }
                }

                if (rules.GhostOpponentMode == EGhostOpponentMode.Random)
                    rules.SelectedGhostLineupId = null;

                // 4) addModIds / removeModIds
                bool hasAddMods = TryGetPropertyCaseInsensitive(root, "addModIds", out var addEl) && addEl.ValueKind == JsonValueKind.Array;
                bool hasRemoveMods = TryGetPropertyCaseInsensitive(root, "removeModIds", out var remEl) && remEl.ValueKind == JsonValueKind.Array;
                if (hasAddMods || hasRemoveMods)
                {
                    var selectedList = new List<string>(rules.SelectedModIds ?? new List<string>());
                    var selectedSet = new HashSet<string>(selectedList, StringComparer.OrdinalIgnoreCase);

                    if (hasAddMods)
                    {
                        var serverDefaultIds = new HashSet<string>(
                            modHost.GetAllModStatuses()
                                .Where(s => s.Enabled && s.Loaded)
                                .Select(s => s.Id),
                            StringComparer.OrdinalIgnoreCase);

                        var requestedAdds = new List<string>();
                        foreach (var item in addEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                requestedAdds.Add(item.GetString()!.Trim());
                        }

                        if (requestedAdds.Count > 0)
                        {
                            // 递归拉齐本服可用依赖 Mod，并跳过全服默认与不存在的 Mod
                            var effReg = modHost.ResolveEffectiveRegistryForMods(requestedAdds);
                            var expandedIds = new List<string>();
                            foreach (var pkg in effReg.LoadedPackages)
                            {
                                string mid = pkg.Manifest.Id?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(mid) && !serverDefaultIds.Contains(mid))
                                    expandedIds.Add(mid);
                            }
                            // 保留直接匹配社区库的纯资产/非默认 Mod（若 ResolveEffectiveRegistry 未展开）
                            foreach (string rid in requestedAdds)
                            {
                                if (serverDefaultIds.Contains(rid)) continue;
                                var pkg = communityMods.ResolvePackage(rid) ?? modHost.FindDiscoveredPackage(rid);
                                if (pkg != null && !serverDefaultIds.Contains(pkg.Manifest.Id))
                                    expandedIds.Add(pkg.Manifest.Id);
                            }

                            foreach (string mid in expandedIds)
                            {
                                if (selectedSet.Add(mid))
                                    selectedList.Add(mid);
                            }
                        }
                    }

                    if (hasRemoveMods)
                    {
                        var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in remEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                toRemove.Add(item.GetString()!.Trim());
                        }
                        if (toRemove.Count > 0)
                            selectedList.RemoveAll(id => toRemove.Contains(id));
                    }

                    rules.SelectedModIds = selectedList;
                }

                var savedRules = ruleStore.Set(accountId, rules);
                string? currentSeedText = DailyChallengeSeedGenerator.NormalizeSeedCode(savedRules.CustomSeedText);
                bool hasActiveRun = await HasActiveNonEndedRunAsync(gameSessions, accountId, ctx.RequestAborted);

                return Results.Json(new
                {
                    rules = savedRules,
                    currentSeedText,
                    hasActiveRun,
                }, PlannerJsonOpts);
            }
        });

        // POST /api/planner/seed/random
        app.MapPost("/api/planner/seed/random", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            int seed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            string seedText = DailyChallengeSeedGenerator.FormatSeedCode(seed);
            return Results.Json(new { seedText }, PlannerJsonOpts);
        });

        // ── 6. 分享码与蓝图端点（请求体上限 300 KB） ─────────────────────────

        // POST /api/planner/blueprints/export
        app.MapPost("/api/planner/blueprints/export", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, req) = await ReadJsonModelAsync<BlueprintExportRequest>(ctx, MaxBlueprintBodyBytes);
            if (!ok) return errResult!;

            var res = await blueprintService.ExportAsync(
                auth.Principal.AccountId,
                auth.Principal.Username,
                req,
                ctx.RequestAborted);
            if (!res.Ok || res.Data == null)
                return JsonError(res.HttpStatus, res.ErrorCode ?? "BAD_REQUEST", res.ErrorMessage ?? "导出失败");

            return Results.Json(res.Data, PlannerJsonOpts);
        });

        // POST /api/planner/blueprints/preview
        app.MapPost("/api/planner/blueprints/preview", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, req) = await ReadJsonModelAsync<BlueprintPreviewRequest>(ctx, MaxBlueprintBodyBytes);
            if (!ok) return errResult!;

            var res = await blueprintService.PreviewAsync(
                auth.Principal.AccountId,
                req?.Code,
                ctx.RequestAborted);
            if (!res.Ok || res.Data == null)
                return JsonError(res.HttpStatus, res.ErrorCode ?? "CODE_INVALID", res.ErrorMessage ?? "预览失败");

            return Results.Json(res.Data, PlannerJsonOpts);
        });

        // POST /api/planner/blueprints/import
        app.MapPost("/api/planner/blueprints/import", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, req) = await ReadJsonModelAsync<BlueprintImportRequest>(ctx, MaxBlueprintBodyBytes);
            if (!ok) return errResult!;

            var res = await blueprintService.ImportAsync(
                auth.Principal.AccountId,
                req,
                ctx.RequestAborted);
            if (!res.Ok || res.Data == null)
                return JsonError(res.HttpStatus, res.ErrorCode ?? "CODE_INVALID", res.ErrorMessage ?? "导入失败");

            return Results.Json(res.Data, PlannerJsonOpts);
        });

        // GET /api/planner/blueprints/mine
        app.MapGet("/api/planner/blueprints/mine", (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var list = blueprintService.ListMine(auth.Principal.AccountId);
            return Results.Json(list, PlannerJsonOpts);
        });

        // DELETE /api/planner/blueprints/{shortCode}
        app.MapDelete("/api/planner/blueprints/{shortCode}", (HttpContext ctx, string shortCode) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var res = blueprintService.DeleteMine(auth.Principal.AccountId, shortCode);
            if (!res.Ok)
                return JsonError(res.HttpStatus, res.ErrorCode ?? "CODE_NOT_FOUND", res.ErrorMessage ?? "分享码不存在或不是你的");

            return Results.Json(new { ok = true }, PlannerJsonOpts);
        });

        // ── 7. 种子日历与搜种端点 ─────────────────────────────────────────────

        // POST /api/planner/seed-preview
        app.MapPost("/api/planner/seed-preview", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, req) = await ReadJsonModelAsync<SeedPreviewRequest>(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;

            // 手填的种子只认固定格式的码；转成规范写法再算（大小写、O/0 之类的笔误自动纠正）
            if (req != null && !string.IsNullOrWhiteSpace(req.SeedText))
            {
                if (!DailyChallengeSeedGenerator.TryParseSeedText(req.SeedText, out _, out string strictPreview))
                    return JsonError(StatusCodes.Status400BadRequest, "SEED_INVALID", "种子码格式不对：应该是 S 开头的 8 位码，比如 S3KZ9QW1");
                req.SeedText = strictPreview;
            }

            var playerRules = req is { UseMyRules: true }
                ? ruleStore.Get(auth.Principal.AccountId)
                : null;

            var res = seedFinder.PreviewSeed(req, playerRules);
            if (!res.Ok || res.Data == null)
                return JsonError(res.StatusCode, res.ErrorCode ?? "BAD_REQUEST", res.ErrorMessageZh ?? "推演失败");

            return Results.Json(res.Data, PlannerJsonOpts);
        });

        // POST /api/planner/seed-finder/jobs
        app.MapPost("/api/planner/seed-finder/jobs", async (HttpContext ctx) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var (ok, errResult, req) = await ReadJsonModelAsync<SeedFinderJobRequest>(ctx, MaxDefaultBodyBytes);
            if (!ok) return errResult!;

            var playerRules = req is { UseMyRules: true }
                ? ruleStore.Get(auth.Principal.AccountId)
                : null;

            var res = seedFinder.StartJob(auth.Principal.AccountId, req, playerRules);
            if (!res.Ok || string.IsNullOrEmpty(res.JobId))
                return JsonError(res.StatusCode, res.ErrorCode ?? "BAD_REQUEST", res.ErrorMessageZh ?? "启动搜种失败", res.Details);

            return Results.Json(new { jobId = res.JobId }, PlannerJsonOpts);
        });

        // GET /api/planner/seed-finder/jobs/{id}
        app.MapGet("/api/planner/seed-finder/jobs/{id}", (HttpContext ctx, string id) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            var status = seedFinder.GetJob(auth.Principal.AccountId, id);
            if (status == null)
                return JsonError(StatusCodes.Status404NotFound, "NOT_FOUND", "搜索任务不存在（可能已过期或服务器已重启）");

            return Results.Json(status, PlannerJsonOpts);
        });

        // DELETE /api/planner/seed-finder/jobs/{id}
        app.MapDelete("/api/planner/seed-finder/jobs/{id}", (HttpContext ctx, string id) =>
        {
            var auth = plannerSessions.ResolveAuth(ctx, accounts);
            if (!auth.IsAuthenticated || auth.Principal == null)
                return AuthError(auth);

            if (!seedFinder.CancelJob(auth.Principal.AccountId, id))
                return JsonError(StatusCodes.Status404NotFound, "NOT_FOUND", "搜索任务不存在");

            return Results.Json(new { ok = true }, PlannerJsonOpts);
        });
    }

    // ── 统一错误与 JSON 读取工具 ─────────────────────────────────────────────

    private static IResult AuthError(PlannerAuthResolution auth)
        => JsonError(
            auth.FailureStatusCode > 0 ? auth.FailureStatusCode : StatusCodes.Status401Unauthorized,
            string.IsNullOrWhiteSpace(auth.ErrorCode) ? "AUTH_REQUIRED" : auth.ErrorCode!,
            auth.ErrorMessageZh ?? "请先登录后再访问编排台。");

    private static IResult JsonError(int statusCode, string code, string error, object? details = null)
    {
        if (details != null)
            return Results.Json(new { error, code, details }, PlannerJsonOpts, statusCode: statusCode);
        return Results.Json(new { error, code }, PlannerJsonOpts, statusCode: statusCode);
    }

    private static async Task<(bool Ok, IResult? ErrorResult, JsonDocument? Document)> ReadJsonDocumentAsync(
        HttpContext ctx,
        int maxBytes)
    {
        var (bytesOk, err, bytes) = await ReadBodyBytesBoundedAsync(ctx, maxBytes);
        if (!bytesOk)
            return (false, err, null);

        if (bytes.Length == 0 || IsAllWhitespaceUtf8(bytes))
            return (true, null, JsonDocument.Parse("{}"));

        try
        {
            var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            return (true, null, doc);
        }
        catch (JsonException)
        {
            return (false, JsonError(StatusCodes.Status400BadRequest, "BAD_JSON", "请求不是合法的 JSON"), null);
        }
    }

    private static async Task<(bool Ok, IResult? ErrorResult, T? Model)> ReadJsonModelAsync<T>(
        HttpContext ctx,
        int maxBytes) where T : class, new()
    {
        var (bytesOk, err, bytes) = await ReadBodyBytesBoundedAsync(ctx, maxBytes);
        if (!bytesOk)
            return (false, err, null);

        if (bytes.Length == 0 || IsAllWhitespaceUtf8(bytes))
            return (true, null, new T());

        try
        {
            var model = JsonSerializer.Deserialize<T>(bytes, PlannerJsonOpts);
            return (true, null, model ?? new T());
        }
        catch (JsonException)
        {
            return (false, JsonError(StatusCodes.Status400BadRequest, "BAD_JSON", "请求不是合法的 JSON"), null);
        }
    }

    private static async Task<(bool Ok, IResult? ErrorResult, byte[] Bytes)> ReadBodyBytesBoundedAsync(
        HttpContext ctx,
        int maxBytes)
    {
        if (ctx.Request.ContentLength is long declaredLen && declaredLen > maxBytes)
        {
            int kb = maxBytes / 1024;
            return (false, JsonError(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", $"请求体超过 {kb} KB 限制"), Array.Empty<byte>());
        }

        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 16 * 1024));
        byte[] buffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
            {
                int kb = maxBytes / 1024;
                return (false, JsonError(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", $"请求体超过 {kb} KB 限制"), Array.Empty<byte>());
            }
            ms.Write(buffer, 0, read);
        }

        return (true, null, ms.ToArray());
    }

    private static bool IsAllWhitespaceUtf8(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                return false;
        }
        return true;
    }

    // ── 对局摘要与会话查找工具 ───────────────────────────────────────────────

    private static Session? FindSessionForAccount(SessionStore? sessions, string accountId)
    {
        if (sessions == null || string.IsNullOrWhiteSpace(accountId))
            return null;

        if (Guid.TryParse(accountId, out var parsed))
        {
            return sessions.GetByAccount(parsed.ToString("N"))
                ?? sessions.GetByAccount(parsed.ToString("D"));
        }
        return sessions.GetByAccount(accountId);
    }

    private static async Task<bool> HasActiveNonEndedRunAsync(
        SessionStore gameSessions,
        string accountId,
        CancellationToken ct)
    {
        var session = FindSessionForAccount(gameSessions, accountId);
        if (session == null)
            return false;

        try
        {
            await session.Gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            var handle = SessionRegistry.Get(session.Sid);
            return handle != null && !Persistence.IsTerminalState(handle.Run.State);
        }
        finally
        {
            try { session.Gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<object?> TryBuildActiveRunSummaryAsync(
        SessionStore gameSessions,
        Persistence db,
        string accountId,
        CancellationToken ct)
    {
        var session = FindSessionForAccount(gameSessions, accountId);
        if (session == null)
            return null;

        string runId;
        string hero;
        int day;
        int hour;
        string state;
        ESpecialModeId specialMode;
        int masterSeed;
        string? rawSeedText;
        bool isCustomSeeded;
        bool hasPendingGhost;
        bool isSandbox;

        try
        {
            await session.Gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        try
        {
            var handle = SessionRegistry.Get(session.Sid);
            if (handle == null || Persistence.IsTerminalState(handle.Run.State))
                return null;

            var run = handle.Run;
            runId = run.RunId;
            hero = PlannerCatalogConstants.NormalizeHeroId(run.Hero.ToString()) ?? run.Hero.ToString();
            day = (int)run.Day;
            hour = (int)run.Hour;
            state = run.State.ToString();
            specialMode = run.CustomRules?.SpecialModeId ?? ESpecialModeId.Standard;
            masterSeed = run.MasterSeed;
            rawSeedText = run.SeedText;
            isCustomSeeded = run.IsCustomSeededRun;
            hasPendingGhost = run.PendingPvpGhost != null;
            isSandbox = run.IsSandboxRun;
        }
        finally
        {
            try { session.Gate.Release(); } catch (ObjectDisposedException) { }
        }

        // DB 读取一律放在 Session.Gate 锁外
        var facedDays = db.LoadRunGhostDays(runId, "faced");
        int? pendingGhostDay = hasPendingGhost && !facedDays.Contains(day) ? day : null;
        string? effectiveSeedText = masterSeed == 0
            ? null
            : DailyChallengeSeedGenerator.FormatSeedCode(masterSeed);

        return new
        {
            runId,
            hero,
            heroZh = PlannerCatalogConstants.GetHeroNameZh(hero),
            day,
            hour,
            state,
            isFinished = false,
            specialModeId = specialMode.ToString(),
            modeZh = ResolveModeNameZh(specialMode),
            seedText = effectiveSeedText,
            isCustomSeededRun = isCustomSeeded,
            facedDays,
            pendingGhostDay,
            isSandbox,
        };
    }

    private static object? TryBuildRecentRunSummary(
        Persistence db,
        ServerModHost modHost,
        string accountId)
    {
        var latest = db.LoadLatestFinishedRun(accountId);
        if (latest == null)
            return null;

        RunSnapshotSerializer.TryReadHeader(latest.Value.SnapshotJson, null, out var header);
        var headerModIds = header?.CustomRules?.SelectedModIds;
        var effReg = modHost.ResolveEffectiveRegistryForMods(headerModIds);
        if (!RunSnapshotSerializer.TryDeserialize(
                latest.Value.SnapshotJson,
                effReg.ActiveRepository,
                out var run,
                out _,
                extraMutators: effReg.CustomMutatorsById) || run == null)
        {
            return null;
        }

        string hero = PlannerCatalogConstants.NormalizeHeroId(run.Hero.ToString()) ?? run.Hero.ToString();
        var specialMode = run.CustomRules?.SpecialModeId ?? ESpecialModeId.Standard;
        var facedDays = db.LoadRunGhostDays(run.RunId, "faced");
        string? effectiveSeedText = run.MasterSeed == 0
            ? null
            : DailyChallengeSeedGenerator.FormatSeedCode(run.MasterSeed);

        return new
        {
            runId = run.RunId,
            hero,
            heroZh = PlannerCatalogConstants.GetHeroNameZh(hero),
            day = (int)run.Day,
            hour = (int)run.Hour,
            state = run.State.ToString(),
            isFinished = true,
            specialModeId = specialMode.ToString(),
            modeZh = ResolveModeNameZh(specialMode),
            seedText = effectiveSeedText,
            isCustomSeededRun = run.IsCustomSeededRun,
            facedDays,
            pendingGhostDay = (int?)null,
            isSandbox = run.IsSandboxRun,
        };
    }

    private static async Task<string?> ResolveOwnedRunIdForCaptureAsync(
        SessionStore gameSessions,
        Persistence db,
        string accountId,
        string? requestedRunId,
        CancellationToken ct)
    {
        string? activeRunId = null;
        var session = FindSessionForAccount(gameSessions, accountId);
        if (session != null)
        {
            try
            {
                await session.Gate.WaitAsync(ct);
                try
                {
                    var handle = SessionRegistry.Get(session.Sid);
                    if (handle != null && !Persistence.IsTerminalState(handle.Run.State))
                        activeRunId = handle.Run.RunId;
                }
                finally
                {
                    try { session.Gate.Release(); } catch (ObjectDisposedException) { }
                }
            }
            catch (ObjectDisposedException) { }
        }

        if (!string.IsNullOrWhiteSpace(requestedRunId))
        {
            string trimmed = requestedRunId.Trim();
            if (!string.IsNullOrEmpty(activeRunId) && string.Equals(activeRunId, trimmed, StringComparison.OrdinalIgnoreCase))
                return activeRunId;

            var row = db.LoadRun(trimmed);
            if (row == null)
                return null;

            string normOwner = PlannerSessionStore.NormalizeAccountId(row.AccountId);
            string normCaller = PlannerSessionStore.NormalizeAccountId(accountId);
            return string.Equals(normOwner, normCaller, StringComparison.OrdinalIgnoreCase) ? row.RunId : null;
        }

        if (!string.IsNullOrEmpty(activeRunId))
            return activeRunId;

        return db.LoadLatestFinishedRun(accountId)?.RunId;
    }

    // ── 目录与解析小工具 ─────────────────────────────────────────────────────

    private static object BuildCatalogCardDto(BazaarCore.Models.CardTemplate tpl, GameDataRepository repo)
    {
        bool isSkill = string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
        string nameZh = ResolveCatalogCardNameZh(tpl, repo);
        string nameEn = !string.IsNullOrWhiteSpace(tpl.DisplayTitle)
            ? tpl.DisplayTitle.Trim()
            : (tpl.InternalName ?? tpl.Id);
        string? size = isSkill ? null : (string.IsNullOrWhiteSpace(tpl.Size) ? "Medium" : tpl.Size);
        var tiers = GhostBoardValidator.GetSupportedTiers(tpl);
        var enchantments = isSkill ? new List<string>() : GhostBoardValidator.GetSupportedEnchantments(tpl);
        var baseAttrs = GhostBoardValidator.BuildBaseAttributesByTier(tpl);
        bool isMod = tpl.IsCustomModCard;
        string? modId = isMod && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin) ? tpl.CustomModOrigin : null;

        var heroes = (tpl.Heroes ?? new List<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h) && !string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new
        {
            templateId = tpl.Id,
            nameZh,
            nameEn,
            type = tpl.Type,
            size,
            heroes,
            tags = tpl.Tags ?? new List<string>(),
            startingTier = string.IsNullOrWhiteSpace(tpl.StartingTier) ? "Bronze" : tpl.StartingTier,
            tiers,
            enchantments,
            baseAttributesByTier = baseAttrs,
            isMod,
            modId,
        };
    }

    private static string ResolveCatalogCardNameZh(BazaarCore.Models.CardTemplate tpl, GameDataRepository repo)
    {
        string? title = repo.Localization?.GetLocalizedTitle(tpl);
        if (!string.IsNullOrWhiteSpace(title))
            return title.Trim();
        return GhostBoardValidator.ResolveCardNameZh(tpl, repo);
    }

    private static string ResolveModeNameZh(ESpecialModeId mode) => mode switch
    {
        ESpecialModeId.EndlessTower => "无尽爬塔",
        ESpecialModeId.CrossHeroMayhem => "跨职业大乱斗",
        ESpecialModeId.MutatorChallenge => "肉鸽词条挑战",
        ESpecialModeId.DailyChallenge => "每日同种子挑战",
        ESpecialModeId.BossRush => "Boss 车轮战",
        ESpecialModeId.SandboxLab => "沙盒构筑实验室",
        _ => "经典官方",
    };

    private static bool TryParseGhostOpponentMode(JsonElement el, out EGhostOpponentMode mode)
    {
        mode = EGhostOpponentMode.Random;
        if (el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString()?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            if (string.Equals(s, "Random", StringComparison.OrdinalIgnoreCase) || s == "0")
            {
                mode = EGhostOpponentMode.Random;
                return true;
            }
            if (string.Equals(s, "FixedLineup", StringComparison.OrdinalIgnoreCase) || s == "1")
            {
                mode = EGhostOpponentMode.FixedLineup;
                return true;
            }
            return false;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n))
        {
            if (n == 0) { mode = EGhostOpponentMode.Random; return true; }
            if (n == 1) { mode = EGhostOpponentMode.FixedLineup; return true; }
        }
        return false;
    }

    private static List<string> ParseCsv(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                list.Add(trimmed);
        }
        return list;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value))
                return true;
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? ReadOptionalString(JsonElement obj, string name)
    {
        if (!TryGetPropertyCaseInsensitive(obj, name, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int? ReadOptionalInt(JsonElement obj, string name)
    {
        if (!TryGetPropertyCaseInsensitive(obj, name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
            return v;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int parsed))
            return parsed;
        return null;
    }

    private static List<string>? ReadOptionalStringList(JsonElement obj, string name)
    {
        if (!TryGetPropertyCaseInsensitive(obj, name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!.Trim());
        }
        return list;
    }

    private static string? ResolvePlannerStaticDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "planner"),
            Path.Combine(Environment.CurrentDirectory, "wwwroot", "planner"),
            Path.Combine(Environment.CurrentDirectory, "src", "BazaarServer", "wwwroot", "planner"),
        };

        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && cur != null; i++, cur = cur.Parent)
        {
            candidates.Add(Path.Combine(cur.FullName, "src", "BazaarServer", "wwwroot", "planner"));
            candidates.Add(Path.Combine(cur.FullName, "wwwroot", "planner"));
        }

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html")))
                return Path.GetFullPath(dir);
        }
        return null;
    }
}
