using System.Text.Json;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Storage;

namespace BazaarServer.Endpoints;

/// <summary>
/// 多模式排行榜与每日同种子挑战端点：
/// - GET /api/leaderboards/{mode}（无尽爬塔、每日挑战、Boss 车轮战、肉鸽词条、跨英雄混池、锦标赛、赛季排位）
///   支持 hero 筛选、分页、每日首通榜筛选及当前玩家个人最佳记录/排名；
/// - GET /api/daily-challenge/today（按东八区 UTC+8 确定性生成当日种子、指定英雄、2~3 条非冲突词条与 Top 50 榜单）。
/// </summary>
public static class LeaderboardEndpoints
{
    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/leaderboards/{mode}", (string mode, HttpContext ctx, AccountStore accounts, Persistence db) =>
        {
            string normMode = (mode ?? "").Trim().ToLowerInvariant();
            string hero = ctx.Request.Query["hero"].ToString().Trim();
            string date = ctx.Request.Query["date"].ToString().Trim();
            string season = ctx.Request.Query["season"].ToString().Trim();
            bool firstAttemptOnly = string.Equals(ctx.Request.Query["firstAttemptOnly"], "true", StringComparison.OrdinalIgnoreCase);
            int page = int.TryParse(ctx.Request.Query["page"], out int p) ? Math.Max(1, p) : 1;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 100) : 50;

            string? boardKey = ResolveBoardKey(normMode, hero, date, season, out string? queryHeroFilter, out string displayNameZh);
            if (boardKey == null)
            {
                return Results.Json(new
                {
                    error = $"不支持的排行榜模式 '{mode}'。可选模式：endless、daily、boss_rush、mutator、cross_hero、tournament、ranked。",
                    code = "UNSUPPORTED_LEADERBOARD_MODE",
                }, CamelJson, statusCode: 400);
            }

            var (items, total) = db.QueryLeaderboard(boardKey, queryHeroFilter, firstAttemptOnly, page, limit);
            int baseRank = (page - 1) * limit;

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            object? myBestDto = null;
            int myRank = 0;
            if (auth.Account != null)
            {
                var (best, rank) = db.QueryAccountLeaderboardBest(boardKey, auth.Account.AccountId.ToString("N"), queryHeroFilter, firstAttemptOnly);
                if (best != null)
                {
                    myRank = rank;
                    myBestDto = ToEntryDto(best, rank);
                }
            }

            return Results.Json(new
            {
                mode = normMode,
                boardKey,
                displayNameZh,
                page,
                limit,
                total,
                totalPages = (int)Math.Ceiling(total / (double)limit),
                myRank,
                myBest = myBestDto,
                items = items.Select((entry, idx) => ToEntryDto(entry, baseRank + idx + 1)).ToArray(),
            }, CamelJson);
        });

        app.MapGet("/api/daily-challenge/today", (HttpContext ctx, AccountStore accounts, Persistence db) =>
        {
            // P0-41：全栈统一复用 BazaarCore.Engine.DailyChallengeSeedGenerator 单一信源，彻底消除双份种子/词条漂移
            var spec = BazaarCore.Engine.DailyChallengeSeedGenerator.GenerateSpec();
            string dateKey = spec.DateKey;
            int masterSeed = spec.MasterSeed;
            string featuredHero = spec.FeaturedHero;
            var mutators = spec.MutatorIds
                .Select(id => MutatorCatalog.ById.TryGetValue(id, out var m) ? m : null)
                .Where(m => m != null)
                .Cast<MutatorMetadata>()
                .ToList();
            double scoreMultiplier = MutatorCatalog.CalculateTotalScoreMultiplier(EGameModePreset.PureOfficial, spec.MutatorIds);
            int remainingSec = Math.Max(0, (int)Math.Ceiling((spec.NextResetUtc - DateTimeOffset.UtcNow).TotalSeconds));

            string boardKey = $"daily:{dateKey}";
            var (topEntries, totalParticipants) = db.QueryLeaderboard(boardKey, heroFilter: null, firstAttemptOnly: true, page: 1, limit: 50);

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            object? myBest = null;
            int myRank = 0;
            if (auth.Account != null)
            {
                var (best, rank) = db.QueryAccountLeaderboardBest(boardKey, auth.Account.AccountId.ToString("N"), heroFilter: null, firstAttemptOnly: true);
                if (best != null)
                {
                    myRank = rank;
                    myBest = ToEntryDto(best, rank);
                }
            }

            return Results.Json(new
            {
                dateKey,
                timezone = "UTC+8",
                masterSeed,
                featuredHero,
                scoreMultiplier,
                resetInSeconds = remainingSec,
                resetAtUtc = spec.NextResetUtc.UtcDateTime,
                mutators = mutators.Select(m => new
                {
                    id = m.Id,
                    nameZh = m.NameZh,
                    descZh = m.DescZh,
                    isPositive = m.IsPositive,
                    scoreMultiplierDelta = m.ScoreMultiplierDelta,
                }).ToArray(),
                totalParticipants,
                myRank,
                myBest,
                topLeaderboard = topEntries.Select((e, idx) => ToEntryDto(e, idx + 1)).ToArray(),
            }, CamelJson);
        });
    }

    /// <summary>获取东八区（UTC+8）当日日期键 yyyy-MM-dd（委托 DailyChallengeSeedGenerator 单一信源）。</summary>
    public static string GetChinaTodayDateKey()
        => BazaarCore.Engine.DailyChallengeSeedGenerator.GetTodayDateKeyUtc8();

    private static string? ResolveBoardKey(
        string normMode,
        string hero,
        string date,
        string season,
        out string? queryHeroFilter,
        out string displayNameZh)
    {
        queryHeroFilter = string.IsNullOrWhiteSpace(hero) ? null : hero;
        switch (normMode)
        {
            case "endless":
            case "endless_tower":
            case "endless:all":
                displayNameZh = string.IsNullOrWhiteSpace(hero) ? "无尽爬塔总榜" : $"无尽爬塔榜（{hero}）";
                if (!string.IsNullOrWhiteSpace(hero))
                {
                    queryHeroFilter = null; // 已分键存储
                    return $"endless:hero:{hero}";
                }
                return "endless:all";

            case "daily":
            case "daily_challenge":
            case "daily:today":
                string dKey = string.IsNullOrWhiteSpace(date) ? GetChinaTodayDateKey() : date;
                displayNameZh = $"每日同种子挑战榜（{dKey}）";
                return $"daily:{dKey}";

            case "boss_rush":
            case "boss_rush:all":
                displayNameZh = "Boss 车轮战排行榜";
                return "boss_rush:all";

            case "mutator":
            case "mutator_challenge":
            case "mutator:all":
                displayNameZh = "肉鸽突变词条挑战榜";
                return "mutator:all";

            case "cross_hero":
            case "cross_hero:all":
                displayNameZh = "全英雄跨职业混池榜";
                return "cross_hero:all";

            case "tournament":
            case "tournament:s2":
                string tSeason = string.IsNullOrWhiteSpace(season) ? "s2" : season;
                displayNameZh = "锦标赛积分排行榜";
                return $"tournament:{tSeason}";

            case "ranked":
            case "standard":
            case "season:2:ranked":
                string sId = string.IsNullOrWhiteSpace(season) ? "2" : season;
                displayNameZh = $"赛季 {sId} 排位排行榜";
                return $"season:{sId}:ranked";

            default:
                displayNameZh = "";
                return null;
        }
    }

    private static object ToEntryDto(Persistence.LeaderboardEntryRow e, int rank) => new
    {
        rank,
        accountId = e.AccountId,
        username = e.Username,
        hero = e.Hero,
        runId = e.RunId,
        modeId = e.ModeId,
        score = e.Score,
        primaryMetric = e.PrimaryMetric,
        secondaryMetric = e.SecondaryMetric,
        dayReached = e.DayReached,
        victories = e.Victories,
        defeats = e.Defeats,
        prestige = e.Prestige,
        elapsedSeconds = e.ElapsedSeconds,
        isFirstAttempt = e.IsFirstAttempt,
        mutators = ParseJsonArray(e.MutatorsJson),
        summaryBoard = ParseJsonObject(e.SummaryBoardJson),
        achievedAt = e.AchievedAt,
    };

    private static string[] ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static object ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new { };
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return new { }; }
    }
}
