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

    public static void Map(WebApplication app)
    {
        // ── 时间 ─────────────────────────────────────────────────────────────
        // 客户端把响应体当裸字符串喂给 DateTime.Parse()，所以这里必须返回
        // 不带引号、不带 JSON 包装的纯文本 ISO-8601。
        app.MapGet("/api/time", () =>
        {
            return Results.Text(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        });

        // ── 登录（无 Steam 版：直接给名字）─────────────────────────────────
        app.MapPost("/api/auth/login/silent", async (HttpContext ctx, AccountStore accounts, ILogger<Program> log) =>
        {
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            string ticket = ExtractTicket(raw);

            AccountStore.Account acc;
            if (!string.IsNullOrEmpty(ticket))
            {
                acc = accounts.GetOrCreateBySteamTicket(ticket);
                log.LogInformation("login/silent: steam ticket -> {user}", acc.Username);
            }
            else
            {
                string name = ctx.Request.Headers["uid"].ToString();
                if (string.IsNullOrWhiteSpace(name)) name = ExtractName(raw);
                acc = accounts.GetOrCreateByName(name);
                log.LogInformation("login/silent: name '{name}' -> {user}", name, acc.Username);
            }

            return Results.Json(new LoginResponseDto
            {
                AccessToken = acc.AccessToken,
                RefreshToken = acc.RefreshToken,
                AlternateRefreshToken = acc.RefreshToken,
                AccessTokenExpiresAt = acc.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        // ── 直接按名字登录（插件首选用这个）────────────────────────────────
        app.MapPost("/api/auth/login/user", async (HttpContext ctx, AccountStore accounts, ILogger<Program> log) =>
        {
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            string name = ExtractName(raw);
            if (string.IsNullOrWhiteSpace(name)) name = ctx.Request.Headers["uid"].ToString();
            var acc = accounts.GetOrCreateByName(name);
            log.LogInformation("login/user: '{name}' -> {user}", name, acc.Username);

            return Results.Json(new LoginResponseDto
            {
                AccessToken = acc.AccessToken,
                RefreshToken = acc.RefreshToken,
                AlternateRefreshToken = acc.RefreshToken,
                AccessTokenExpiresAt = acc.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        // ── 注册（名字可用性检查 + 注册即登录）─────────────────────────────
        app.MapPost("/api/auth/register", async (HttpContext ctx, AccountStore accounts) =>
        {
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            string name = ExtractName(raw);
            var acc = accounts.GetOrCreateByName(name);
            return Results.Json(new LoginResponseDto
            {
                AccessToken = acc.AccessToken,
                RefreshToken = acc.RefreshToken,
                AlternateRefreshToken = acc.RefreshToken,
                AccessTokenExpiresAt = acc.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        app.MapGet("/api/accounts/check-username", (HttpContext ctx, AccountStore accounts) =>
        {
            string name = ctx.Request.Query["username"].ToString();
            bool taken = accounts.FindByUsername(name) != null;
            return Results.Json(new { available = !taken, username = name }, Json);
        });

        app.MapPost("/api/auth/refreshtokens", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = accounts.FindByAccessToken(ctx.Request.Headers["Authorization"].ToString());
            if (acc == null) return Results.Json(new { error = "invalid token" }, Json, statusCode: 401);
            return Results.Json(new RefreshTokenResponseDto
            {
                AccessToken = acc.AccessToken,
                RefreshToken = acc.RefreshToken,
                AccessTokenExpiresAt = acc.AccessExpiresAt,
                CurrentTime = DateTime.UtcNow,
            }, Json);
        });

        app.MapPost("/api/auth/playerprofile", () => Results.Json(new { ok = true }, Json));

        // ── Bootstrap ────────────────────────────────────────────────────────
        app.MapGet("/api/Bootstrap/app", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db, ILogger<Program> log) =>
        {
            var acc = Resolve(ctx, accounts);
            log.LogInformation("Bootstrap/app (GET) for {user}", acc.Username);
            return Results.Json(BuildBootstrap(acc, sessions, db, accounts), Json);
        });
        app.MapPost("/api/Bootstrap/app", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db, ILogger<Program> log) =>
        {
            var acc = Resolve(ctx, accounts);
            log.LogInformation("Bootstrap/app (POST) for {user}", acc.Username);
            return Results.Json(BuildBootstrap(acc, sessions, db, accounts), Json);
        });

        app.MapGet("/api/Bootstrap/post-run", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(BuildPostRunBootstrap(acc, sessions, db, accounts), Json);
        });
        app.MapPost("/api/Bootstrap/post-run", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(BuildPostRunBootstrap(acc, sessions, db, accounts), Json);
        });

        // ── 账号 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/Accounts/me/summary", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(new AccountSummaryDto
            {
                UserName = acc.Username,
                Username = acc.Username,
                Email = acc.Username + "@local",
                IsEmailVerified = true,
                AccountId = acc.AccountId,
                CountryCode = "CN",
            }, Json);
        });

        app.MapGet("/api/Accounts/feature-access", (HttpContext ctx, AccountStore accounts) =>
            Results.Json(AllAccess(Resolve(ctx, accounts)), Json));
        app.MapGet("/api/accounts/me/email-verification-status", () => Results.Json(true, Json));

        // ── 档案 / 排名 / 钱包 ───────────────────────────────────────────────
        app.MapGet("/api/PlayerProfiles/me", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(new PlayerProfileDto
            {
                AccountId = acc.AccountId,
                Username = acc.Username,
                TitlePrefix = "",
                TitleSuffix = "",
                SeasonRank = new RankDto(),
                ActiveRun = BuildActiveRunDto(acc, sessions, db),
            }, Json);
        });

        app.MapGet("/api/PlayerProfiles/me/career", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(new PlayerProfileCareerDto
            {
                AccountId = acc.AccountId,
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
            var acc = !string.IsNullOrWhiteSpace(queryName)
                ? accounts.FindByUsername(queryName)
                : Resolve(ctx, accounts);

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
            var acc = Resolve(ctx, accounts);
            return Results.Json(new PlayerRankDto { AccountId = acc.AccountId }, Json);
        });

        app.MapGet("/api/Wallets/me", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(BuildWallet(acc, accounts), Json);
        });

        // ── 赛季 ─────────────────────────────────────────────────────────────
        app.MapGet("/api/Seasons", () => Results.Json(new[] { Season(1), Season(2) }, Json));
        app.MapGet("/api/Seasons/current", () => Results.Json(Season(2), Json));
        app.MapGet("/api/Seasons/{id:int}", (int id) => Results.Json(Season(id), Json));
        app.MapGet("/api/SeasonTracks/{id:int}", (int id) => Results.Json(BuildSeasonTrack(id), Json));
        app.MapGet("/api/SeasonTrackProgressions/me", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            int seasonId = int.TryParse(ctx.Request.Query["seasonId"], out int qid) ? qid : 2;
            return Results.Json(new SeasonTrackProgressionDto
            {
                accountId = acc.AccountId,
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

        // ── 挑战（全开：直接给已完成的）─────────────────────────────────────
        app.MapGet("/api/Challenges/me", () => Results.Json(new ChallengesDto(), Json));
        app.MapPost("/api/Challenges/acknowledge/{id}", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/Challenges/daily/reroll", () => Results.Json(new ChallengesDto(), Json));
        app.MapPost("/api/Challenges/weekly/reroll", () => Results.Json(new ChallengesDto(), Json));

        // ── 英雄（全部拥有）─────────────────────────────────────────────────
        app.MapGet("/api/Heroes/owned", () => Results.Json(AllHeroes(), Json));
        app.MapGet("/api/Heroes/listings", (HttpContext ctx, AccountStore accounts) =>
            Results.Json(BuildHeroListings(Resolve(ctx, accounts)), Json));
        app.MapGet("/api/Heroes/purchase", () => Results.Json(new PurchaseHeroListingsDto(), Json));
        app.MapGet("/api/purchase/listings/Hero", () => Results.Json(new PurchaseHeroListingsDto(), Json));

        // ── 藏品 / 装备（catalog.bin 白名单 + 持久化配装）──────────────────
        app.MapGet("/api/CollectionItems", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(GetOwnedCollectionGuids(acc, accounts), Json);
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
            var acc = Resolve(ctx, accounts);
            return Results.Json(BuildHeroLoadoutsResponse(acc, accounts), Json);
        });
        app.MapGet("/api/Loadouts/me/{heroId}", (string heroId, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var state = accounts.GetHeroLoadout(acc, heroId);
            return Results.Json(ToHeroLoadoutDto(acc, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/equip/{collectibleId}", (string heroId, string collectibleId, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var entry = FindCollectible(collectibleId);
            if (entry == null)
            {
                return Results.NotFound(new { error = "collectible not found in catalog whitelist", collectibleId });
            }
            var state = accounts.EquipCollectible(acc, heroId, entry.Id, entry.Type);
            return Results.Json(ToHeroLoadoutDto(acc, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/unequip/{collectibleId}", (string heroId, string collectibleId, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var state = accounts.UnequipCollectible(acc, heroId, collectibleId);
            return Results.Json(ToHeroLoadoutDto(acc, state), Json);
        });
        app.MapPost("/api/Loadouts/hero/{heroId}/equip-loadout", async (string heroId, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            EquipLoadoutRequestDto req = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { req = JsonSerializer.Deserialize<EquipLoadoutRequestDto>(raw, CaseInsensitiveJson); }
                catch { }
            }
            req ??= new EquipLoadoutRequestDto();
            var state = accounts.ApplyEquipLoadout(
                acc, heroId,
                req.boardId, req.carpetId, req.cardBackId, req.toyId,
                req.stashId, req.bankId, req.albumId, req.heroSkinId);
            return Results.Json(ToHeroLoadoutDto(acc, state), Json);
        });

        // ── 箱子 / 商店 / 待领奖励 ───────────────────────────────────────────
        app.MapGet("/api/Chests/unopened", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(BuildUnopenedChests(acc, accounts), Json);
        });
        app.MapGet("/api/Chests/drop-rates/{seasonId:int}", (int seasonId) =>
            Results.Json(BuildDropRates(seasonId), Json));
        app.MapGet("/api/Chests/store-prices", () => Results.Json(Array.Empty<object>(), Json));
        app.MapPost("/api/Chests/grant", async (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            int count = ExtractIntProperty(raw, 30, "count", "Count", "quantity", "Quantity");
            int total = accounts.AddUnopenedChests(acc, Math.Clamp(count, 1, 200));
            return Results.Json(Array.Empty<ChestResponseDto>(), Json);
        });
        app.MapPost("/api/app/chests/store-purchase", async (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            int qty = Math.Clamp(ExtractIntProperty(raw, 1, "quantity", "Quantity", "count", "Count"), 1, 200);
            accounts.AddUnopenedChests(acc, qty);
            return Results.Json(new AppStorePurchaseChestsResponseDto
            {
                accountId = acc.AccountId,
                cost = new ClientChestCurrencyDto { CurrencyType = "Gem", Amount = 0 },
                State = new StateDeltaResponseDto
                {
                    Wallet = BuildWallet(acc, accounts),
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

        // ── 好友 / 联机用的简易玩家列表（自定义 PvP 的基础）──────────────────
        app.MapGet("/api/Players", (AccountStore accounts) =>
            Results.Json(accounts.All.Select(a => new
            {
                accountId = a.AccountId,
                username = a.Username,
                online = false,
                victories = a.Victories,
                defeats = a.Defeats,
            }).ToArray(), Json));

        // ── 遥测 / 存档兼容 / 兜底路由（防止任何外部接口 404）────────────────
        app.MapPost("/api/telemetry", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/game/feedback", () => Results.Json(new { ok = true }, Json));
        app.MapPost("/api/app/challenges/progress", () => Results.Json(new { ok = true }, Json));
        app.MapGet("/api/game/loadgame", () => Results.Json(new { ok = true, save = (object)null }, Json));
        app.MapPost("/api/game/savegame", () => Results.Json(new { ok = true }, Json));
        app.MapDelete("/api/game/deleteactivesave", (HttpContext ctx, AccountStore accounts, SessionStore sessions, Persistence db) =>
        {
            var acc = Resolve(ctx, accounts);
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
        app.MapGet("/api/game/getghost", () => Results.Json(new { ok = true, ghost = (object)null }, Json));

        app.Map("/api/{**unmatched}", (HttpContext ctx, string unmatched, ILogger<Program> log) =>
        {
            log.LogDebug("Fallback /api/{path} ({method})", unmatched, ctx.Request.Method);
            return Results.Json(new { ok = true }, Json);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static object BuildActiveRunDto(AccountStore.Account acc, SessionStore sessions, Persistence db)
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
            return new
            {
                AccountId = acc.AccountId,
                PlayMode = row.PlayMode,
                TournamentSessionId = (Guid?)null,
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

    private static AccountStore.Account Resolve(HttpContext ctx, AccountStore accounts)
    {
        var acc = accounts.FindByAccessToken(ctx.Request.Headers["Authorization"].ToString());
        if (acc != null) return acc;

        string aid = ctx.Request.Headers["aid"].ToString();
        if (string.IsNullOrWhiteSpace(aid)) aid = ctx.Request.Headers["X-Account-Id"].ToString();
        acc = accounts.FindByAccountId(aid);
        if (acc != null) return acc;

        string uid = ctx.Request.Headers["uid"].ToString();
        if (string.IsNullOrWhiteSpace(uid)) uid = ctx.Request.Headers["X-Username"].ToString();
        return accounts.GetOrCreateByName(string.IsNullOrWhiteSpace(uid) ? "guest" : uid);
    }

    private static string ExtractTicket(string raw)
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

    private static string ExtractName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "username", "Username", "userName", "UserName", "name", "Name" })
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

    // 真实可用英雄（对齐客户端 8 位可玩英雄，包含第 8 英雄双龙 Hero8）
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

    // ── catalog.bin 校验藏品白名单懒加载与映射 ───────────────────────────────

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

            Console.WriteLine($"[CollectibleCatalog] loaded {list.Count} entries ({nonCardGuids.Count} non-card, {cardSkins.Count} card_skin) from {path}");
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
            Console.WriteLine("[CollectibleCatalog] failed to load whitelist: " + ex.Message);
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
        var acc = Resolve(ctx, accounts);
        string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        int requested = ExtractOpenChestCount(raw);

        var snap = CatalogLazy.Value;
        var pool = snap.CardSkinEntries.Count > 0 ? snap.CardSkinEntries : snap.AllGuidEntries;
        if (pool.Count == 0)
        {
            return Results.BadRequest(new { error = "collectible whitelist unavailable" });
        }

        var (ok, draws, _, _) = accounts.TryOpenChests(acc, requested, ownedCardSkins =>
        {
            // 优先从尚未开启的动态卡牌皮肤中抽取，全部集齐后触发重复宝石返还（+50 宝石）
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
            return Results.BadRequest(new { error = "no unopened chests remaining" });
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
