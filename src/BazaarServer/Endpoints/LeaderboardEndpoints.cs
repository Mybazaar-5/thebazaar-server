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

    private static readonly TimeSpan ChinaUtcOffset = TimeSpan.FromHours(8);

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
            DateTimeOffset chinaNow = DateTimeOffset.UtcNow.ToOffset(ChinaUtcOffset);
            string dateKey = chinaNow.ToString("yyyy-MM-dd");
            DateTimeOffset nextReset = new DateTimeOffset(chinaNow.Year, chinaNow.Month, chinaNow.Day, 0, 0, 0, ChinaUtcOffset).AddDays(1);
            int remainingSec = Math.Max(0, (int)Math.Ceiling((nextReset - chinaNow).TotalSeconds));

            int masterSeed = ComputeFnv1a32($"bazaar-daily-v1:{dateKey}");
            var (featuredHero, mutators) = PickDailyRecipe(masterSeed);
            double scoreMultiplier = MutatorCatalog.CalculateTotalScoreMultiplier(EGameModePreset.PureOfficial, mutators.Select(m => m.Id));

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
                resetAtUtc = nextReset.UtcDateTime,
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

    /// <summary>获取东八区（UTC+8）当日日期键 yyyy-MM-dd。</summary>
    public static string GetChinaTodayDateKey()
        => DateTimeOffset.UtcNow.ToOffset(ChinaUtcOffset).ToString("yyyy-MM-dd");

    /// <summary>确定性 FNV-1a 32 位哈希（与审计报告 4.3 每日挑战规范严格一致）。</summary>
    public static int ComputeFnv1a32(string text)
    {
        uint hash = 2166136261u;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        int signed = (int)(hash & 0x7FFFFFFFu);
        return signed == 0 ? 1 : signed;
    }

    private static (string FeaturedHero, List<MutatorMetadata> Mutators) PickDailyRecipe(int masterSeed)
    {
        var rng = new Random(masterSeed);
        string hero = AccountStore.KnownPlayableHeroes[rng.Next(AccountStore.KnownPlayableHeroes.Length)];

        var positives = MutatorCatalog.All.Where(m => m.IsPositive).ToList();
        var challenges = MutatorCatalog.All.Where(m => !m.IsPositive).ToList();

        var pickedIds = new List<string>();
        if (positives.Count > 0)
            pickedIds.Add(positives[rng.Next(positives.Count)].Id);
        if (challenges.Count > 0)
            pickedIds.Add(challenges[rng.Next(challenges.Count)].Id);

        // 50% 概率追加第 3 条不冲突词条，保证每日配方在 2~3 条之间
        if (rng.Next(2) == 0 && MutatorCatalog.All.Count > 2)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var extra = MutatorCatalog.All[rng.Next(MutatorCatalog.All.Count)];
                var candidate = new List<string>(pickedIds) { extra.Id };
                var resolved = MutatorCatalog.NormalizeAndResolveConflicts(candidate, maxCount: 3);
                if (resolved.Count == 3)
                {
                    pickedIds = resolved;
                    break;
                }
            }
        }

        var normalized = MutatorCatalog.NormalizeAndResolveConflicts(pickedIds, maxCount: 3);
        var metas = normalized
            .Select(id => MutatorCatalog.ById.TryGetValue(id, out var m) ? m : null)
            .Where(m => m != null)
            .Cast<MutatorMetadata>()
            .ToList();
        return (hero, metas);
    }

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
