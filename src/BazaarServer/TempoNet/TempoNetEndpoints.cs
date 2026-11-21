using System.Collections.Concurrent;
using System.Text.Json;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.TempoNet;

public static class TempoNetEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly ConcurrentDictionary<Guid, Queue<long>> ChestGrantRateMap = new();

    private static readonly string[] ReservedExtendedPrefixes =
    [
        "social/", "replays/", "leaderboards/", "sandbox/", "spectate/",
        "lobbies/", "daily-challenge/", "ping"
    ];

    public static void Map(WebApplication app)
    {
        // ── 时间 ─────────────────────────────────────────────────────────────
        // 客户端把响应体当裸字符串喂给 DateTime.Parse()，这里必须返回纯文本 ISO-8601。
        app.MapGet("/api/time", () =>
            Results.Text(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")));

        // ── 登录（无 Steam 版：支持 Steam 票据、用户名 + 可选口令/设备密钥与多端并存）──
        app.MapPost("/api/auth/login/silent", async (HttpContext ctx, AccountStore accounts, ILogger<Program> log) =>
        {
            string raw = await ReadBoundedBodyAsync(ctx, 16 * 1024);
            string ticket = ExtractTicket(raw);
            string clientKind = ExtractStringProperty(raw, "clientKind", "client_kind")
                ?? ctx.Request.Headers["X-Client-Kind"].ToString();
            string deviceId = ExtractStringProperty(raw, "deviceId", "device_id")
                ?? ctx.Request.Headers["X-Device-Id"].ToString();
            string? secret = ExtractStringProperty(raw, "password", "passphrase", "deviceSecret", "secret");

            AccountStore.Account acc;
            AccountStore.IssuedTokenPair? issued = null;
            if (!string.IsNullOrEmpty(ticket))
            {
                acc = accounts.GetOrCreateBySteamTicket(ticket, clientKind, deviceId);
                log.LogInformation("login/silent: steam ticket -> {user}", acc.Username);
            }
            else
            {
                string name = ctx.Request.Headers["uid"].ToString();
                if (string.IsNullOrWhiteSpace(name)) name = ExtractName(raw);
                var login = accounts.LoginByNameWithSecret(name, secret, clientKind, deviceId);
                if (!login.Ok || login.Account == null)
                    return Results.Json(new { error = login.ErrorZh ?? "登录失败，请检查用户名或口令。", code = "LOGIN_FAILED" }, Json, statusCode: 401);
                acc = login.Account;
                issued = login.Tokens;
                log.LogInformation("login/silent: name '{name}' -> {user}", name, acc.Username);
            }

            return Results.Json(new LoginResponseDto
            {
                AccessToken = issued?.AccessToken ?? acc.AccessToken,
                RefreshToken = issued?.RefreshToken ?? acc.RefreshToken,
                AlternateRefreshToken = issued?.RefreshToken ?? acc.RefreshToken,
                AccessTokenExpiresAt = issued?.AccessExpiresAt ?? acc.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        // ── 直接按名字登录（插件与启动器首选，多端并发不互踢）─────────────────
        app.MapPost("/api/auth/login/user", async (HttpContext ctx, AccountStore accounts, ILogger<Program> log) =>
        {
            string raw = await ReadBoundedBodyAsync(ctx, 16 * 1024);
            string name = ExtractName(raw);
            if (string.IsNullOrWhiteSpace(name)) name = ctx.Request.Headers["uid"].ToString();
            string clientKind = ExtractStringProperty(raw, "clientKind", "client_kind")
                ?? ctx.Request.Headers["X-Client-Kind"].ToString();
            string deviceId = ExtractStringProperty(raw, "deviceId", "device_id")
                ?? ctx.Request.Headers["X-Device-Id"].ToString();
            string? secret = ExtractStringProperty(raw, "password", "passphrase", "deviceSecret", "secret");

            var login = accounts.LoginByNameWithSecret(name, secret, clientKind, deviceId);
            if (!login.Ok || login.Account == null || login.Tokens == null)
                return Results.Json(new { error = login.ErrorZh ?? "账号登录失败。", code = "LOGIN_FAILED" }, Json, statusCode: 401);

            log.LogInformation("login/user: '{name}' -> {user} ({kind})", name, login.Account.Username, login.Tokens.ClientKind);
            return Results.Json(new LoginResponseDto
            {
                AccessToken = login.Tokens.AccessToken,
                RefreshToken = login.Tokens.RefreshToken,
                AlternateRefreshToken = login.Tokens.RefreshToken,
                AccessTokenExpiresAt = login.Tokens.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        // ── 注册（名字可用性检查 + 注册即登录，可选绑定口令保护）───────────────
        app.MapPost("/api/auth/register", async (HttpContext ctx, AccountStore accounts) =>
        {
            string raw = await ReadBoundedBodyAsync(ctx, 16 * 1024);
            string name = ExtractName(raw);
            string clientKind = ExtractStringProperty(raw, "clientKind", "client_kind")
                ?? ctx.Request.Headers["X-Client-Kind"].ToString();
            string deviceId = ExtractStringProperty(raw, "deviceId", "device_id")
                ?? ctx.Request.Headers["X-Device-Id"].ToString();
            string? secret = ExtractStringProperty(raw, "password", "passphrase", "deviceSecret", "secret");

            var login = accounts.LoginByNameWithSecret(name, secret, clientKind, deviceId);
            if (!login.Ok || login.Account == null || login.Tokens == null)
                return Results.Json(new { error = login.ErrorZh ?? "注册失败。", code = "REGISTER_FAILED" }, Json, statusCode: 401);

            return Results.Json(new LoginResponseDto
            {
                AccessToken = login.Tokens.AccessToken,
                RefreshToken = login.Tokens.RefreshToken,
                AlternateRefreshToken = login.Tokens.RefreshToken,
                AccessTokenExpiresAt = login.Tokens.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        // ── 设置/修改账号登录口令（需已认证身份）─────────────────────────────
        app.MapPost("/api/auth/password", async (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string raw = await ReadBoundedBodyAsync(ctx, 8 * 1024);
            string? currentSecret = ExtractStringProperty(raw, "currentPassword", "oldPassword", "currentSecret");
            string? newSecret = ExtractStringProperty(raw, "newPassword", "password", "passphrase", "newSecret");
            if (string.IsNullOrWhiteSpace(newSecret))
                return Results.Json(new { error = "请提供新的账号口令。", code = "INVALID_PASSWORD" }, Json, statusCode: 400);

            var (ok, err) = accounts.SetOrUpdateSecret(auth.Account!, currentSecret, newSecret);
            if (!ok)
                return Results.Json(new { error = err ?? "口令设置失败。", code = "PASSWORD_UPDATE_FAILED" }, Json, statusCode: 400);

            return Results.Json(new { ok = true, message = "账号口令保护已更新。" }, Json);
        });

        // ── 登出并吊销当前令牌 ───────────────────────────────────────────────
        app.MapPost("/api/auth/logout", (HttpContext ctx, AccountStore accounts) =>
        {
            string token = ctx.Request.Headers["Authorization"].ToString();
            accounts.RevokeToken(token);
            return Results.Json(new { ok = true, message = "当前会话已退出登录。" }, Json);
        });

        app.MapGet("/api/accounts/check-username", (HttpContext ctx, AccountStore accounts) =>
        {
            string name = ctx.Request.Query["username"].ToString();
            var existing = accounts.FindByUsername(name);
            return Results.Json(new
            {
                available = existing == null,
                username = name,
                hasPassword = existing?.HasSecretProtection ?? false,
            }, Json);
        });

        // ── 令牌刷新（支持 Body 或 Authorization Header，真正轮换新令牌并落盘）──
        app.MapPost("/api/auth/refreshtokens", async (HttpContext ctx, AccountStore accounts) =>
        {
            string raw = await ReadBoundedBodyAsync(ctx, 8 * 1024);
            string? tokenFromBody = ExtractStringProperty(raw, "refreshToken", "RefreshToken", "token", "Token");
            string token = !string.IsNullOrWhiteSpace(tokenFromBody)
                ? tokenFromBody
                : ctx.Request.Headers["Authorization"].ToString();
            string clientKind = ExtractStringProperty(raw, "clientKind", "client_kind")
                ?? ctx.Request.Headers["X-Client-Kind"].ToString();
            string deviceId = ExtractStringProperty(raw, "deviceId", "device_id")
                ?? ctx.Request.Headers["X-Device-Id"].ToString();

            var refreshed = accounts.TryRefreshTokens(token, clientKind, deviceId);
            if (!refreshed.Ok || refreshed.Tokens == null)
            {
                return Results.Json(new { error = refreshed.ErrorZh ?? "刷新令牌无效或已过期。", code = "INVALID_TOKEN" }, Json, statusCode: 401);
            }

            return Results.Json(new RefreshTokenResponseDto
            {
                AccessToken = refreshed.Tokens.AccessToken,
                RefreshToken = refreshed.Tokens.RefreshToken,
                AccessTokenExpiresAt = refreshed.Tokens.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        app.MapPost("/api/auth/playerprofile", () => Results.Json(new { ok = true }, Json));

        // ── Bootstrap ────────────────────────────────────────────────────────
        app.MapGet("/api/Bootstrap/app", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db, ILogger<Program> log) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            log.LogInformation("Bootstrap/app (GET) for {user}", acc!.Username);
            return Results.Json(BuildBootstrap(acc, sessions, db, accounts), Json);
        });
        app.MapPost("/api/Bootstrap/app", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db, ILogger<Program> log) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            log.LogInformation("Bootstrap/app (POST) for {user}", acc!.Username);
            return Results.Json(BuildBootstrap(acc, sessions, db, accounts), Json);
        });

        app.MapGet("/api/Bootstrap/post-run", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildPostRunBootstrap(acc!, sessions, db, accounts), Json);
        });
        app.MapPost("/api/Bootstrap/post-run", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildPostRunBootstrap(acc!, sessions, db, accounts), Json);
        });

        // ── 账号 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/Accounts/me/summary", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(new AccountSummaryDto
            {
                UserName = acc!.Username,
                Username = acc.Username,
                Email = acc.Username + "@local",
                IsEmailVerified = true,
                AccountId = acc.AccountId,
                CountryCode = "CN",
            }, Json);
        });

        app.MapGet("/api/Accounts/feature-access", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(AllAccess(acc!), Json);
        });
        app.MapGet("/api/accounts/me/email-verification-status", () => Results.Json(true, Json));

        // ── 档案 / 排名 / 钱包 ───────────────────────────────────────────────
        app.MapGet("/api/PlayerProfiles/me", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(new PlayerProfileDto
            {
                AccountId = acc!.AccountId,
                Username = acc.Username,
                TitlePrefix = "",
                TitleSuffix = "",
                SeasonRank = new RankDto(),
                ActiveRun = BuildActiveRunDto(acc, sessions, db),
            }, Json);
        });

        app.MapGet("/api/PlayerProfiles/me/career", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(new PlayerProfileCareerDto
            {
                AccountId = acc!.AccountId,
                HasCompletedTutorial = true,
                Heroes = BuildCareerHeroArray(acc, accounts),
            }, Json);
        });

        app.MapGet("/api/PlayerProfiles/{accountId:guid}/career", (Guid accountId, AccountStore accounts) =>
        {
            var acc = accounts.FindByAccountId(accountId.ToString("N"));
            if (acc == null)
            {
                return Results.Json(new
                {
                    accountId = accountId.ToString("D"),
                    heroes = Array.Empty<object>(),
                    seasonRanks = new Dictionary<string, int>(),
                }, Json);
            }
            return Results.Json(new
            {
                accountId = acc.AccountId.ToString("D"),
                heroes = BuildCareerHeroArray(acc, accounts),
                seasonRanks = new Dictionary<string, int>(),
            }, Json);
        });

        app.MapGet("/api/PlayerProfiles/career", (HttpContext ctx, AccountStore accounts) =>
        {
            string queryName = ctx.Request.Query["userName"].ToString();
            AccountStore.Account? acc;
            if (!string.IsNullOrWhiteSpace(queryName))
            {
                acc = accounts.FindByUsername(queryName);
            }
            else
            {
                var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
                if (auth.HasExplicitFailure) return AccountAuthResolver.ErrorResult(auth, Json);
                acc = auth.Account;
            }

            if (acc == null)
                return Results.Json(new { accountId = Guid.Empty.ToString("D"), heroes = Array.Empty<object>(), seasonRanks = new Dictionary<string, int>() }, Json);

            return Results.Json(new
            {
                accountId = acc.AccountId.ToString("D"),
                userName = acc.Username,
                runs = acc.RunsCompleted,
                victories = acc.Victories,
                defeats = acc.Defeats,
                bestDay = acc.BestDay,
                heroes = BuildCareerHeroArray(acc, accounts),
                seasonRanks = new Dictionary<string, int>(),
            }, Json);
        });

        app.MapGet("/api/PlayerRanks/me", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(new PlayerRankDto { AccountId = acc!.AccountId }, Json);
        });

        app.MapGet("/api/Wallets/me", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildWallet(acc!, accounts), Json);
        });

        // ── 赛季 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/Seasons", () => Results.Json(new[] { Season(1), Season(2) }, Json));
        app.MapGet("/api/Seasons/current", () => Results.Json(Season(2), Json));
        app.MapGet("/api/Seasons/{id:int}", (int id) => Results.Json(Season(id), Json));
        app.MapGet("/api/SeasonTracks/{id:int}", (int id) => Results.Json(BuildSeasonTrack(id), Json));
        app.MapGet("/api/SeasonTrackProgressions/me", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            int seasonId = int.TryParse(ctx.Request.Query["seasonId"], out int qid) ? qid : 2;
            return Results.Json(new SeasonTrackProgressionDto
            {
                accountId = acc!.AccountId,
                seasonId = seasonId,
                isPaid = true,
                experience = 1000,
                claimedFreeRewardTiers = Array.Empty<int>(),
                claimedPaidRewardTiers = Array.Empty<int>(),
                claimedFreeInfiniteTierCount = 0,
                claimedPaidInfiniteTierCount = 0,
            }, Json);
        });
        app.MapGet("/api/SeasonRankRewards", () => Results.Json(Array.Empty<object>(), Json));

        // ── 挑战 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/Challenges/me", () => Results.Json(new ChallengesDto(), Json));
        app.MapPost("/api/Challenges/acknowledge/{id}", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/Challenges/daily/reroll", () => Results.Json(new ChallengesDto(), Json));
        app.MapPost("/api/Challenges/weekly/reroll", () => Results.Json(new ChallengesDto(), Json));

        // ── 英雄（全部拥有）─────────────────────────────────────────────────
        app.MapGet("/api/Heroes/owned", () => Results.Json(AllHeroes(), Json));
        app.MapGet("/api/Heroes/listings", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildHeroListings(acc!), Json);
        });
        app.MapGet("/api/Heroes/purchase", () => Results.Json(new PurchaseHeroListingsDto(), Json));
        app.MapGet("/api/purchase/listings/Hero", () => Results.Json(new PurchaseHeroListingsDto(), Json));

        // ── 藏品 / 装备（写操作强制鉴权，防止伪造 X-Account-Id 篡改他人配装）──
        app.MapGet("/api/CollectionItems", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(GetOwnedCollectionGuids(acc!, accounts), Json);
        });
        app.MapGet("/api/collectionitems/duplicate-rates", () => Results.Json(new Dictionary<string, int>
        {
            ["Bronze"] = 50,
            ["Silver"] = 50,
            ["Gold"] = 50,
            ["Diamond"] = 50,
            ["Legendary"] = 50,
        }, Json));

        app.MapGet("/api/Loadouts/me", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildHeroLoadoutsResponse(acc!, accounts), Json);
        });
        app.MapGet("/api/Loadouts/me/{heroId}", (string heroId, HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            var state = accounts.GetHeroLoadout(acc!, heroId);
            return Results.Json(ToHeroLoadoutDto(acc!, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/equip/{collectibleId}", (string heroId, string collectibleId, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            var entry = FindCollectible(collectibleId);
            if (entry == null)
            {
                return Results.Json(new { error = "藏品不在有效资产白名单中。", collectibleId }, Json, statusCode: 404);
            }
            var state = accounts.EquipCollectible(auth.Account!, heroId, entry.Id, entry.Type);
            return Results.Json(ToHeroLoadoutDto(auth.Account!, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/unequip/{collectibleId}", (string heroId, string collectibleId, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            var state = accounts.UnequipCollectible(auth.Account!, heroId, collectibleId);
            return Results.Json(ToHeroLoadoutDto(auth.Account!, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/equip-loadout", async (string heroId, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            string raw = await ReadBoundedBodyAsync(ctx, 16 * 1024);
            EquipLoadoutRequestDto? req = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { req = JsonSerializer.Deserialize<EquipLoadoutRequestDto>(raw, CaseInsensitiveJson); }
                catch { }
            }
            req ??= new EquipLoadoutRequestDto();
            var state = accounts.ApplyEquipLoadout(
                auth.Account!, heroId,
                req.boardId, req.carpetId, req.cardBackId, req.toyId,
                req.stashId, req.bankId, req.albumId, req.heroSkinId);
            return Results.Json(ToHeroLoadoutDto(auth.Account!, state), Json);
        });

        // ── 箱子 / 商店 / 待领奖励（加固 MP-P1-10：鉴权 + 频率与开关限制）────────
        app.MapGet("/api/Chests/unopened", (HttpContext ctx, AccountStore accounts) =>
        {
            if (!TryResolveForRead(ctx, accounts, out var acc, out var err)) return err!;
            return Results.Json(BuildUnopenedChests(acc!, accounts), Json);
        });
        app.MapGet("/api/Chests/drop-rates/{seasonId:int}", (int seasonId) =>
            Results.Json(BuildDropRates(seasonId), Json));
        app.MapGet("/api/Chests/store-prices", () => Results.Json(Array.Empty<object>(), Json));
        app.MapPost("/api/Chests/grant", async (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string? allowGrant = Environment.GetEnvironmentVariable("BAZAAR_ALLOW_FREE_CHEST_GRANT");
            if (string.Equals(allowGrant, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(allowGrant, "false", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "服务器已关闭自由发放宝箱功能。", code = "CHEST_GRANT_DISABLED" }, Json, statusCode: 403);
            }

            if (!CheckChestGrantRateLimit(auth.Account!.AccountId))
            {
                return Results.Json(new { error = "领取宝箱过于频繁，请稍后再试。", code = "RATE_LIMITED" }, Json, statusCode: 429);
            }

            string raw = await ReadBoundedBodyAsync(ctx, 4 * 1024);
            int count = ExtractIntProperty(raw, 10, "count", "Count", "quantity", "Quantity");
            accounts.AddUnopenedChests(auth.Account, Math.Clamp(count, 1, 50));
            return Results.Json(Array.Empty<ChestResponseDto>(), Json);
        });
        app.MapPost("/api/app/chests/store-purchase", async (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string raw = await ReadBoundedBodyAsync(ctx, 4 * 1024);
            int qty = Math.Clamp(ExtractIntProperty(raw, 1, "quantity", "Quantity", "count", "Count"), 1, 50);
            accounts.AddUnopenedChests(auth.Account!, qty);
            return Results.Json(new AppStorePurchaseChestsResponseDto
            {
                accountId = auth.Account!.AccountId,
                cost = new ClientChestCurrencyDto { CurrencyType = "Gem", Amount = 0 },
                State = new StateDeltaResponseDto
                {
                    Wallet = BuildWallet(auth.Account, accounts),
                    BundledChestsGranted = new BundledChestsGrantDto { count = qty, seasonId = 2 },
                },
            }, Json);
        });
        app.MapPost("/api/app/chests/open", async (HttpContext ctx, AccountStore accounts) =>
            await HandleOpenChestsAsync(ctx, accounts));
        app.MapPost("/api/app/chests/open-bundled", async (HttpContext ctx, AccountStore accounts) =>
            await HandleOpenChestsAsync(ctx, accounts));
        app.MapGet("/api/PendingRewards/me", () => Results.Json(Array.Empty<object>(), Json));
        app.MapGet("/api/RunRewardLevels", () => Results.Json(BuildRunRewardLevels(), Json));
        app.MapGet("/api/MarketplaceListings/specials/daily/me", () => Results.Json(new DailySpecialsDto(), Json));
        app.MapGet("/api/CurrencyListings/{type}", () => Results.Json(new CurrencyListingsDto(), Json));
        app.MapGet("/api/Leaderboards/position/me", () => Results.Json(new LeaderboardPositionDto(), Json));

        // ── 好友 / 玩家列表（修复 MP-P1-10：分页 + 关键字搜索 + 真实在线态）──
        app.MapGet("/api/Players", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            int page = int.TryParse(ctx.Request.Query["page"], out int p) ? Math.Max(1, p) : 1;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 100) : 50;
            string q = ctx.Request.Query["q"].ToString().Trim();
            bool onlineOnly = string.Equals(ctx.Request.Query["onlineOnly"], "true", StringComparison.OrdinalIgnoreCase);

            var query = accounts.All.Select(a =>
            {
                bool hasSession = sessions.GetByAccount(a.AccountId.ToString("N")) != null;
                bool isOnline = hasSession || accounts.IsRecentlyOnline(a);
                return new
                {
                    accountId = a.AccountId,
                    username = a.Username,
                    online = isOnline,
                    inRun = hasSession,
                    victories = a.Victories,
                    defeats = a.Defeats,
                    bestDay = a.BestDay,
                    lastPingMs = a.LastPingMs,
                };
            });

            if (!string.IsNullOrEmpty(q))
                query = query.Where(x => x.username.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (onlineOnly)
                query = query.Where(x => x.online);

            var list = query
                .OrderByDescending(x => x.online)
                .ThenByDescending(x => x.victories)
                .Skip((page - 1) * limit)
                .Take(limit)
                .ToArray();

            return Results.Json(list, Json);
        });

        // ── 遥测 / 存档兼容 / 受控兜底路由 ──────────────────────────────────
        app.MapPost("/api/telemetry", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/game/feedback", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/app/challenges/progress", () => Results.Json(new { ok = true }, Json));
        app.MapGet("/api/game/loadgame", () => Results.Json(new { ok = true, save = (object?)null }, Json));
        app.MapPost("/api/game/savegame", () => Results.Json(new { ok = true }, Json));
        app.MapDelete("/api/game/deleteactivesave", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            var acc = auth.Account!;
            string key = acc.AccountId.ToString("N");
            var mem = sessions.GetByAccount(key);
            if (mem != null)
            {
                sessions.Remove(mem.Sid);
                SessionRegistry.Detach(mem.Sid);
            }
            db.ConcedeActiveRuns(acc.AccountId);
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPost("/api/game/addghost", () => Results.Json(new { ok = true }, Json));
        app.MapGet("/api/game/getghost", () => Results.Json(new { ok = true, ghost = (object?)null }, Json));

        app.Map("/api/{**unmatched}", (HttpContext ctx, string unmatched, ILogger<Program> log) =>
        {
            string normalized = (unmatched ?? "").TrimStart('/');
            foreach (string prefix in ReservedExtendedPrefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        new { error = $"接口 /api/{normalized} 不存在或请求方法不受支持。", code = "NOT_FOUND" },
                        Json,
                        statusCode: StatusCodes.Status404NotFound);
                }
            }

            log.LogDebug("Fallback /api/{path} ({method})", unmatched, ctx.Request.Method);
            return Results.Json(new { ok = true }, Json);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static bool TryResolveForRead(
        HttpContext ctx,
        AccountStore accounts,
        out AccountStore.Account? account,
        out IResult? errorResult)
    {
        var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: AccountAuthResolver.IsStrictAuthEnabled());
        if (auth.HasExplicitFailure)
        {
            account = null;
            errorResult = AccountAuthResolver.ErrorResult(auth, Json);
            return false;
        }
        if (auth.Account != null)
        {
            account = auth.Account;
            errorResult = null;
            return true;
        }

        // 仅在非严格模式的兼容读取接口中允许本地无头探针退回到只读默认游客档案，写操作绝不调用此路径
        account = accounts.GetOrCreateByName("guest");
        errorResult = null;
        return true;
    }

    private static bool CheckChestGrantRateLimit(Guid accountId)
    {
        long nowMs = Environment.TickCount64;
        var queue = ChestGrantRateMap.GetOrAdd(accountId, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && nowMs - queue.Peek() > 60_000)
                queue.Dequeue();
            if (queue.Count >= 5)
                return false;
            queue.Enqueue(nowMs);
            return true;
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.Body == null) return string.Empty;
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes - total + 1));
            if (read <= 0) break;
            total += read;
            if (total > maxBytes)
                return string.Empty;
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static object? BuildActiveRunDto(AccountStore.Account acc, SessionStore sessions, Persistence db)
    {
        var memSession = sessions.GetByAccount(acc.AccountId.ToString("N"));
        if (memSession != null)
        {
            var handle = SessionRegistry.Get(memSession.Sid);
            if (handle != null)
            {
                if (Persistence.IsTerminalState(handle.Run.State))
                    return null;

                var r = handle.Run;
                return new
                {
                    AccountId = acc.AccountId,
                    PlayMode = r.PlayMode,
                    TournamentSessionId = memSession.TournamentSessionId,
                    HeroID = (r.Hero == EHero.Common ? EHero.Vanessa : r.Hero).ToString(),
                    PlayerVictories = (int)r.Victories,
                    RunId = r.RunId,
                    Day = r.Day,
                    Hour = r.Hour,
                    CurrentPrestige = r.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20),
                    Victories = r.Victories,
                    Defeats = r.Defeats,
                };
            }
        }

        var row = db.LoadLatestActiveRun(acc.AccountId);
        if (row != null)
        {
            Guid? tsid = Guid.TryParse(row.TournamentSessionId, out var g) && g != Guid.Empty ? g : null;
            return new
            {
                AccountId = acc.AccountId,
                PlayMode = row.PlayMode,
                TournamentSessionId = tsid,
                HeroID = (row.Hero == EHero.Common ? EHero.Vanessa : row.Hero).ToString(),
                PlayerVictories = (int)row.Victories,
                RunId = row.RunId,
                Day = row.Day,
                Hour = row.Hour,
                CurrentPrestige = row.Prestige,
                Victories = row.Victories,
                Defeats = row.Defeats,
            };
        }

        return null;
    }

    private static object[] BuildCareerHeroArray(AccountStore.Account acc, AccountStore accounts)
    {
        var list = accounts.GetAllHeroCareerStats(acc);
        return list.Select(s => (object)new
        {
            heroId = s.Hero,
            gamesPlayed = s.GamesPlayed,
            rankedGamesPlayed = s.RankedGamesPlayed,
            chestsEarned = s.ChestsEarned,
            playerVictories = s.PlayerVictories,
            monsterVictories = s.MonsterVictories,
            daysPlayed = s.DaysPlayed,
            bestDay = s.BestDay,
            runsWon = s.RunsWon,
            runsLost = s.RunsLost,
            tenWinTrophies = s.TenWinTrophies,
            eventsCompleted = s.EventsCompleted,
            merchantsVisited = s.MerchantsVisited,
            coinsSpent = s.CoinsSpent,
            livesLost = s.LivesLost,
            secondsSpentPlaying = s.SecondsSpentPlaying,
            itemsSold = s.ItemsSold,
            damageDealt = s.DamageDealt,
            legendaryItemsAcquired = s.LegendaryItemsAcquired,
            ghostBattles = s.GhostBattles,
            ghostVictories = s.GhostVictories,
            ghostRunsEnded = s.GhostRunsEnded,
            trophies = new Dictionary<string, int>
            {
                ["bronze"] = s.BronzeTrophies,
                ["silver"] = s.SilverTrophies,
                ["gold"] = s.GoldTrophies,
                ["diamond"] = s.DiamondTrophies,
            },
        }).ToArray();
    }

    private static string? ExtractTicket(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("SteamTicket", out var t) &&
                    t.ValueKind == JsonValueKind.Object &&
                    t.TryGetProperty("TicketAsString", out var s))
                {
                    return s.GetString();
                }
                if (doc.RootElement.TryGetProperty("steamTicket", out var t2) &&
                    t2.ValueKind == JsonValueKind.Object &&
                    t2.TryGetProperty("ticketAsString", out var s2))
                {
                    return s2.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractName(string raw)
        => ExtractStringProperty(raw, "username", "Username", "userName", "UserName", "name", "Name");

    private static string? ExtractStringProperty(string raw, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static FeatureAccessDto AllAccess(AccountStore.Account acc) => new()
    {
        accountId = acc.AccountId,
        chestPurchases = true,
        prizePasses = true,
        subscriptions = true,
    };

    private static SeasonDto Season(int id)
    {
        string name = id switch
        {
            1 => "Beta Season",
            2 => "Season 1",
            _ => "Season " + Math.Max(1, id - 1),
        };
        return new SeasonDto
        {
            id = id,
            name = name,
            title = name,
            start = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            allowBundleChestAfterSeasonId = 0,
        };
    }

    private static object EmptyReward() => new
    {
        collectionItemIds = Array.Empty<Guid>(),
        currencies = Array.Empty<object>(),
        titlePrefixIds = Array.Empty<int>(),
        titleSuffixIds = Array.Empty<int>(),
        chests = 0,
    };

    private static SeasonTrackDto BuildSeasonTrack(int id) => new()
    {
        seasonId = id,
        tiers = Enumerable.Range(1, 50).Select(i => (object)new
        {
            rarity = i % 10 == 0 ? "Legendary" : i % 5 == 0 ? "Diamond" : i % 3 == 0 ? "Gold" : "Bronze",
            requiredXp = i * 100,
            freeRewards = EmptyReward(),
            paidRewards = EmptyReward(),
        }).ToArray(),
        infiniteTier = new
        {
            rarity = "Legendary",
            requiredXp = 1000,
            freeRewards = EmptyReward(),
            paidRewards = EmptyReward(),
        },
        subscriptionXpMultiplier = 1,
        expansionPrice = new CurrencyDto(),
        tierSkipPrice = new CurrencyDto(),
    };

    private static readonly (string Id, DateTime ReleaseDate)[] PlayableHeroes =
    [
        ("Pygmalien", new DateTime(2024, 10, 30, 0, 0, 0, DateTimeKind.Utc)),
        ("Vanessa",   new DateTime(2024, 10, 30, 0, 0, 0, DateTimeKind.Utc)),
        ("Dooley",    new DateTime(2024, 10, 30, 0, 0, 0, DateTimeKind.Utc)),
        ("Mak",       new DateTime(2025, 4,  2,  0, 0, 0, DateTimeKind.Utc)),
        ("Stelle",    new DateTime(2025, 8,  13, 0, 0, 0, DateTimeKind.Utc)),
        ("Jules",     new DateTime(2025, 11, 5,  0, 0, 0, DateTimeKind.Utc)),
        ("Karnok",    new DateTime(2026, 2,  1,  0, 0, 0, DateTimeKind.Utc)),
        ("Hero8",     new DateTime(2026, 4,  15, 0, 0, 0, DateTimeKind.Utc)),
    ];

    private static OwnedHeroDto[] AllHeroes() => PlayableHeroes.Select(h => new OwnedHeroDto
    {
        heroId = h.Id,
        releaseDate = h.ReleaseDate,
        owned = true,
        unlockedAt = h.ReleaseDate.ToString("O"),
    }).ToArray();

    private static HeroListingsDto BuildHeroListings(AccountStore.Account acc) => new()
    {
        accountId = acc.AccountId.ToString("D"),
        purchaseCount = 0,
        discountRates = Array.Empty<float>(),
        heroes = PlayableHeroes.Select(h => new ClientHeroListingDto
        {
            heroId = h.Id,
            releaseDate = h.ReleaseDate,
            price = 0,
            isPurchasable = false,
        }).ToArray(),
    };

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed class ValidCollectibleEntry
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string InternalName { get; set; } = "";
        public string Name { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string SeasonId { get; set; } = "";
    }

    private sealed class CollectibleCatalogSnapshot
    {
        public List<ValidCollectibleEntry> All { get; init; } = new();
        public Dictionary<string, ValidCollectibleEntry> ByNormalizedId { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Guid[] NonCardSkinGuids { get; init; } = Array.Empty<Guid>();
        public List<(ValidCollectibleEntry Entry, Guid ParsedGuid, string NormId)> CardSkinEntries { get; init; } = new();
        public List<(ValidCollectibleEntry Entry, Guid ParsedGuid, string NormId)> AllGuidEntries { get; init; } = new();
    }

    private static readonly Lazy<CollectibleCatalogSnapshot> CatalogLazy = new(LoadCollectibleCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? FindWhitelistPath()
    {
        var candidates = new List<string>();
        string? envData = Environment.GetEnvironmentVariable("BAZAAR_DATA");
        if (!string.IsNullOrWhiteSpace(envData))
        {
            candidates.Add(Path.Combine(envData, "valid_collectible_ids.json"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data", "valid_collectible_ids.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "data", "valid_collectible_ids.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "data", "valid_collectible_ids.json"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "data", "valid_collectible_ids.json"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "dist", "data", "valid_collectible_ids.json"));
        candidates.Add("/home/ubuntu/bazaar/data/valid_collectible_ids.json");

        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; depth < 7 && dir != null; depth++)
            {
                candidates.Add(Path.Combine(dir.FullName, "dist", "data", "valid_collectible_ids.json"));
                candidates.Add(Path.Combine(dir.FullName, "data", "valid_collectible_ids.json"));
                dir = dir.Parent;
            }
        }

        foreach (string path in candidates)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static CollectibleCatalogSnapshot LoadCollectibleCatalog()
    {
        string? path = FindWhitelistPath();
        if (string.IsNullOrEmpty(path)) return new CollectibleCatalogSnapshot();

        try
        {
            string raw = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<ValidCollectibleEntry>>(raw, CaseInsensitiveJson) ?? new();
            var byNorm = new Dictionary<string, ValidCollectibleEntry>(StringComparer.OrdinalIgnoreCase);
            var nonCardGuids = new List<Guid>();
            var cardSkins = new List<(ValidCollectibleEntry, Guid, string)>();
            var allGuids = new List<(ValidCollectibleEntry, Guid, string)>();
            var seenNonCard = new HashSet<Guid>();

            foreach (var e in list)
            {
                if (e == null || !Guid.TryParse(e.Id, out var g)) continue;
                string norm = g.ToString("N");
                byNorm[norm] = e;
                allGuids.Add((e, g, norm));
                if (string.Equals(e.Type, "card_skin", StringComparison.OrdinalIgnoreCase))
                {
                    cardSkins.Add((e, g, norm));
                }
                else if (seenNonCard.Add(g))
                {
                    nonCardGuids.Add(g);
                }
            }

            Console.WriteLine($"[CollectibleCatalog] 已加载 {list.Count} 条藏品白名单（非皮肤 {nonCardGuids.Count}，卡牌皮肤 {cardSkins.Count}）");
            return new CollectibleCatalogSnapshot
            {
                All = list,
                ByNormalizedId = byNorm,
                NonCardSkinGuids = nonCardGuids.ToArray(),
                CardSkinEntries = cardSkins,
                AllGuidEntries = allGuids,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("[CollectibleCatalog] 加载藏品白名单失败: " + ex.Message);
            return new CollectibleCatalogSnapshot();
        }
    }

    private static ValidCollectibleEntry? FindCollectible(string? collectibleId)
    {
        string norm = AccountStore.NormalizeCollectibleId(collectibleId);
        if (string.IsNullOrEmpty(norm)) return null;
        return CatalogLazy.Value.ByNormalizedId.TryGetValue(norm, out var entry) ? entry : null;
    }

    private static Guid[] GetOwnedCollectionGuids(AccountStore.Account acc, AccountStore accounts)
    {
        var snap = CatalogLazy.Value;
        var opened = accounts.GetOpenedCardSkinIds(acc);
        if (opened.Count == 0) return snap.NonCardSkinGuids;

        var set = new HashSet<Guid>(snap.NonCardSkinGuids);
        foreach (string sid in opened)
        {
            if (snap.ByNormalizedId.ContainsKey(sid) && Guid.TryParseExact(sid, "N", out var g))
            {
                set.Add(g);
            }
        }
        return set.ToArray();
    }

    private static HeroLoadoutDto ToHeroLoadoutDto(AccountStore.Account acc, AccountStore.HeroLoadoutState s) => new()
    {
        accountId = acc.AccountId.ToString("N"),
        heroId = s.HeroId,
        boardId = s.BoardId ?? "",
        carpetId = s.CarpetId ?? "",
        cardBackId = s.CardBackId ?? "",
        toyId = s.ToyId ?? "",
        stashId = s.StashId ?? "",
        bankId = s.BankId ?? "",
        albumId = s.AlbumId ?? "",
        heroSkinId = s.HeroSkinId ?? "",
        cardSkinIds = s.CardSkinIds?.ToArray() ?? Array.Empty<string>(),
        randomizeLoadout = s.RandomizeLoadout,
    };

    private static HeroLoadoutsResponseDto BuildHeroLoadoutsResponse(AccountStore.Account acc, AccountStore accounts)
    {
        var states = accounts.GetAllHeroLoadouts(acc, PlayableHeroes.Select(h => h.Id));
        var primary = states.FirstOrDefault(s => string.Equals(s.HeroId, "Vanessa", StringComparison.OrdinalIgnoreCase))
                      ?? states.FirstOrDefault()
                      ?? new AccountStore.HeroLoadoutState { HeroId = "Vanessa" };
        return new HeroLoadoutsResponseDto
        {
            loadouts = states.Select(s => ToHeroLoadoutDto(acc, s)).ToList(),
            collection = new CollectionLoadoutDto
            {
                accountId = acc.AccountId.ToString("N"),
                boardId = primary.BoardId ?? "",
                carpetId = primary.CarpetId ?? "",
                cardBackId = primary.CardBackId ?? "",
                toyId = primary.ToyId ?? "",
                stashId = primary.StashId ?? "",
                bankId = primary.BankId ?? "",
                albumId = primary.AlbumId ?? "",
                heroSkinIds = states.Select(s => s.HeroSkinId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray(),
                cardSkinIds = accounts.GetOpenedCardSkinIds(acc).ToArray(),
            },
        };
    }

    private static WalletDto BuildWallet(AccountStore.Account acc, AccountStore accounts) => new()
    {
        AccountId = acc.AccountId,
        Gems = accounts.GetGems(acc),
        RankedVouchers = 999999,
        DailyRankedVouchers = 999999,
    };

    private static UnopenedChestsDto BuildUnopenedChests(AccountStore.Account acc, AccountStore accounts) => new()
    {
        bundledUnopenedCount = accounts.GetUnopenedChestCount(acc),
        legacyChests = Array.Empty<ChestResponseDto>(),
    };

    private static ChestDropRatesResponseDto BuildDropRates(int seasonId) => new()
    {
        seasonId = seasonId,
        itemRarities = new object[]
        {
            new { rarity = "Bronze", rate = 55 },
            new { rarity = "Silver", rate = 25 },
            new { rarity = "Gold", rate = 13 },
            new { rarity = "Diamond", rate = 5 },
            new { rarity = "Legendary", rate = 2 },
        },
        currencies = new object[]
        {
            new { currencyType = "Gem", amount = 50, rate = 100 },
        },
        bonuses = Array.Empty<object>(),
        lootTables = new Dictionary<string, object[]>(),
    };

    private static async Task<IResult> HandleOpenChestsAsync(HttpContext ctx, AccountStore accounts)
    {
        var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
        if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
        var acc = auth.Account!;

        string raw = await ReadBoundedBodyAsync(ctx, 8 * 1024);
        int requested = ExtractOpenChestCount(raw);

        var snap = CatalogLazy.Value;
        var pool = snap.CardSkinEntries.Count > 0 ? snap.CardSkinEntries : snap.AllGuidEntries;
        if (pool.Count == 0)
        {
            return Results.Json(new { error = "藏品白名单暂不可用，请稍后重试。" }, Json, statusCode: 400);
        }

        var (ok, draws, _, _) = accounts.TryOpenChests(acc, requested, ownedCardSkins =>
        {
            var unowned = new List<(ValidCollectibleEntry Entry, Guid ParsedGuid, string NormId)>();
            foreach (var item in pool)
            {
                bool alreadyOwned = string.Equals(item.Entry.Type, "card_skin", StringComparison.OrdinalIgnoreCase)
                    ? ownedCardSkins.Contains(item.NormId)
                    : true;
                if (!alreadyOwned) unowned.Add(item);
            }

            if (unowned.Count > 0)
            {
                var pick = unowned[Random.Shared.Next(unowned.Count)];
                return new AccountStore.ChestOpenDraw(pick.ParsedGuid, pick.NormId, IsCardSkin: true, IsDuplicate: false, GemsAwarded: 0);
            }

            var dup = pool[Random.Shared.Next(pool.Count)];
            return new AccountStore.ChestOpenDraw(dup.ParsedGuid, dup.NormId, IsCardSkin: false, IsDuplicate: true, GemsAwarded: 50);
        });

        if (!ok || draws.Count == 0)
        {
            return Results.Json(new { error = "当前没有可开启的宝箱。" }, Json, statusCode: 400);
        }

        var now = DateTime.UtcNow;
        var chests = draws.Select(d => new ClientChestOpenResponseDto
        {
            id = Guid.NewGuid(),
            ownerId = acc.AccountId,
            seasonId = 2,
            collectionItemId = d.CollectibleGuid,
            openedAt = now,
            isDuplicate = d.IsDuplicate,
            gemsAwarded = d.GemsAwarded,
            currencies = d.IsDuplicate
                ? new[] { new ClientChestCurrencyDto { CurrencyType = "Gem", Amount = d.GemsAwarded } }
                : Array.Empty<ClientChestCurrencyDto>(),
            children = Array.Empty<Guid>(),
            awardedChildChest = false,
        }).ToArray();

        var addedGuids = draws
            .Where(d => !d.IsDuplicate)
            .Select(d => d.CollectibleGuid)
            .Distinct()
            .ToArray();

        return Results.Json(new AppOpenChestsResponseDto
        {
            Chests = chests,
            State = new StateDeltaResponseDto
            {
                Wallet = BuildWallet(acc, accounts),
                CollectionItemsAdded = addedGuids,
            },
        }, Json);
    }

    private static int ExtractOpenChestCount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "count", "Count", "quantity", "Quantity" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) && v > 0)
                        return Math.Clamp(v, 1, 50);
                }
                if (doc.RootElement.TryGetProperty("IncludedChestIDs", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    return Math.Clamp(arr.GetArrayLength(), 1, 50);
                if (doc.RootElement.TryGetProperty("includedChestIDs", out var arr2) && arr2.ValueKind == JsonValueKind.Array && arr2.GetArrayLength() > 0)
                    return Math.Clamp(arr2.GetArrayLength(), 1, 50);
            }
        }
        catch { }
        return 1;
    }

    private static int ExtractIntProperty(string raw, int defaultVal, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultVal;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string n in names)
                {
                    if (doc.RootElement.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) && v > 0)
                        return v;
                }
            }
        }
        catch { }
        return defaultVal;
    }

    private static RunRewardLevelDto[] BuildRunRewardLevels() => new[]
    {
        new RunRewardLevelDto
        {
            WinThreshold = 4,
            UnrankedChestCount = 1,
            RankedChestCount = 1,
            SubscriptionMultiplier = 2,
            UnrankedCurrencyRewards = Array.Empty<CurrencyDto>(),
            RankedCurrencyRewards = Array.Empty<CurrencyDto>(),
        },
        new RunRewardLevelDto
        {
            WinThreshold = 7,
            UnrankedChestCount = 1,
            RankedChestCount = 2,
            SubscriptionMultiplier = 2,
            UnrankedCurrencyRewards = Array.Empty<CurrencyDto>(),
            RankedCurrencyRewards = Array.Empty<CurrencyDto>(),
        },
        new RunRewardLevelDto
        {
            WinThreshold = 10,
            UnrankedChestCount = 1,
            RankedChestCount = 3,
            SubscriptionMultiplier = 2,
            UnrankedCurrencyRewards = Array.Empty<CurrencyDto>(),
            RankedCurrencyRewards = Array.Empty<CurrencyDto>(),
        },
    };

    private static PostRunBootstrapResponseDto BuildPostRunBootstrap(
        AccountStore.Account acc, SessionStore sessions, Persistence db, AccountStore accounts)
    {
        return new PostRunBootstrapResponseDto
        {
            wallet = BuildWallet(acc, accounts),
            challenges = new ChallengesDto(),
            profile = new PlayerProfileDto
            {
                AccountId = acc.AccountId,
                Username = acc.Username,
                TitlePrefix = "",
                TitleSuffix = "",
                SeasonRank = new RankDto { SeasonId = "2" },
                ActiveRun = BuildActiveRunDto(acc, sessions, db),
            },
            featureAccess = AllAccess(acc),
            accountSummary = new AccountSummaryDto
            {
                UserName = acc.Username,
                Username = acc.Username,
                Email = acc.Username + "@local",
                IsEmailVerified = true,
                AccountId = acc.AccountId,
                CountryCode = "CN",
            },
            seasonTrack = BuildSeasonTrack(2),
            seasonTrackProgression = new SeasonTrackProgressionDto
            {
                accountId = acc.AccountId,
                seasonId = 2,
                isPaid = true,
                experience = 1000,
                claimedFreeRewardTiers = Array.Empty<int>(),
                claimedPaidRewardTiers = Array.Empty<int>(),
                claimedFreeInfiniteTierCount = 0,
                claimedPaidInfiniteTierCount = 0,
            },
            chests = BuildUnopenedChests(acc, accounts),
            errors = Array.Empty<object>(),
            hasErrors = false,
        };
    }

    private static BootstrapResponseDto BuildBootstrap(AccountStore.Account acc, SessionStore sessions, Persistence db, AccountStore accounts)
    {
        var season1 = Season(1);
        var season2 = Season(2);
        return new BootstrapResponseDto
        {
            serverTime = DateTime.UtcNow,
            account = new BootstrapAccountDto
            {
                summary = new AccountSummaryDto
                {
                    UserName = acc.Username,
                    Username = acc.Username,
                    Email = acc.Username + "@local",
                    IsEmailVerified = true,
                    AccountId = acc.AccountId,
                    CountryCode = "CN",
                },
                featureAccess = AllAccess(acc),
            },
            profile = new BootstrapProfileDto
            {
                playerProfile = new PlayerProfileDto
                {
                    AccountId = acc.AccountId,
                    Username = acc.Username,
                    TitlePrefix = "",
                    TitleSuffix = "",
                    SeasonRank = new RankDto { SeasonId = "2" },
                    ActiveRun = BuildActiveRunDto(acc, sessions, db),
                },
            },
            wallet = BuildWallet(acc, accounts),
            playerRank = new PlayerRankDto { AccountId = acc.AccountId, SeasonID = 2 },
            season = new BootstrapSeasonDto
            {
                current = season2,
                all = new[] { season1, season2 },
                track = BuildSeasonTrack(2),
                trackProgression = new SeasonTrackProgressionDto
                {
                    accountId = acc.AccountId,
                    seasonId = 2,
                    isPaid = true,
                    experience = 1000,
                    claimedFreeRewardTiers = Array.Empty<int>(),
                    claimedPaidRewardTiers = Array.Empty<int>(),
                    claimedFreeInfiniteTierCount = 0,
                    claimedPaidInfiniteTierCount = 0,
                },
            },
            challenges = new ChallengesDto(),
            heroes = new BootstrapHeroesDto
            {
                owned = AllHeroes(),
                listings = BuildHeroListings(acc),
                purchaseListings = new PurchaseHeroListingsDto(),
            },
            collection = new BootstrapCollectionDto
            {
                items = GetOwnedCollectionGuids(acc, accounts),
                loadouts = BuildHeroLoadoutsResponse(acc, accounts),
            },
            chests = BuildUnopenedChests(acc, accounts),
            marketplace = new BootstrapMarketplaceDto(),
            leaderboard = new LeaderboardPositionDto(),
            pendingRewards = Array.Empty<object>(),
            runRewardLevels = BuildRunRewardLevels().Cast<object>().ToArray(),
            errors = Array.Empty<object>(),
            hasErrors = false,
        };
    }
}
