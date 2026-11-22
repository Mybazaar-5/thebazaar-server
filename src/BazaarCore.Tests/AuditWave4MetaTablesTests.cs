using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4 Batch 4A（元进度三整表数据驱动与 DB Schema P2）回归测试套件：
/// 覆盖 P1-02（challenges 80 条 + 持久化 + TempoNet 5 接口）、P1-03（collectibles 1,644 条 + IsFoundInChests 过滤 + 掉率分桶）、
/// P1-06（seasons 21 赛季 + 配置切换）、P2-03（tooltips 136 条 + TooltipGlossary 兼容视图）、
/// P2-04（GameModeConfig 单一信源代理）、P2-05（level_ups TutorialRewards Lv1~3 透传）与 P2-06（MonsterPool 死方法清理）。
/// </summary>
public sealed class AuditWave4MetaTablesTests
{
    private static readonly Lazy<string> DbPathLazy = new(ResolveWorktreeGameDataDb);
    private static readonly Lazy<GameDataRepository> RepoLazy = new(() => GameDatabaseLoader.Load(DbPathLazy.Value));

    private static string ResolveWorktreeGameDataDb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "dist", "data", "GameData.db");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("未找到 worktree 下的 dist/data/GameData.db");
    }

    private static async Task<(WebApplication App, HttpClient Http, string StateDbPath, ChallengeService Challenges)> StartHostAsync(
        GameDataRepository repo,
        IDictionary<string, string?>? configOverrides = null)
    {
        string stateDbPath = Path.Combine(Path.GetTempPath(), $"bazaar_w4a_{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (configOverrides is { Count: > 0 })
            builder.Configuration.AddInMemoryCollection(configOverrides);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var persistence = new Persistence(stateDbPath);
        builder.Services.AddSingleton(persistence);
        builder.Services.AddSingleton(repo);
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton(sp => new ChallengeService(
            sp.GetRequiredService<Persistence>(),
            sp.GetRequiredService<GameDataRepository>(),
            sp.GetRequiredService<AccountStore>()));

        var app = builder.Build();
        TempoNetEndpoints.Map(app);
        await app.StartAsync();

        string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var challengeService = app.Services.GetRequiredService<ChallengeService>();
        return (app, new HttpClient { BaseAddress = new Uri(baseUrl) }, stateDbPath, challengeService);
    }

    private static async Task CleanupHostAsync(WebApplication app, HttpClient http, string stateDbPath)
    {
        try { await app.StopAsync(); } catch { }
        try { await app.DisposeAsync(); } catch { }
        http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(stateDbPath)) File.Delete(stateDbPath); } catch { }
        try { if (File.Exists(stateDbPath + "-wal")) File.Delete(stateDbPath + "-wal"); } catch { }
        try { if (File.Exists(stateDbPath + "-shm")) File.Delete(stateDbPath + "-shm"); } catch { }
    }

    // ── P1-02: challenges 整表 80 条解析与预编译 ──────────────────────────────

    [Fact]
    public void P1_02_ChallengesTable_LoadsAll80Rows_PrecompilesTriggers_AndPreservesOverlay()
    {
        var repo = RepoLazy.Value;

        Assert.Equal(80, repo.ChallengesById.Count);
        Assert.Equal(55, repo.ChallengesById.Values.Count(c => c.IsDaily));
        Assert.Equal(25, repo.ChallengesById.Values.Count(c => c.IsWeekly));
        Assert.Equal(33, repo.ActiveDailyChallenges.Count);
        Assert.Equal(17, repo.ActiveWeeklyChallenges.Count);
        Assert.Equal(68, repo.ChallengesById.Values.Count(c => c.Trigger != null));
        Assert.Empty(repo.UnknownRuleTypes);

        foreach (var ch in repo.ChallengesById.Values)
        {
            Assert.True(Guid.TryParse(ch.Id, out _));
            Assert.True(ch.CompletionRequirement is >= 1 and <= 25000, $"异常 CompletionRequirement: {ch.Id}={ch.CompletionRequirement}");
            Assert.True(ch.XpReward is >= 200 and <= 500, $"异常 XpReward: {ch.Id}={ch.XpReward}");
            Assert.False(string.IsNullOrWhiteSpace(ch.Localization?.Title?.Text));
            Assert.False(string.IsNullOrWhiteSpace(ch.Localization?.Description?.Text));
            Assert.Equal(ch.Duration, ch.Type);
            if (ch.IsActive)
            {
                Assert.NotNull(ch.Trigger);
                Assert.False(string.IsNullOrWhiteSpace(ch.Trigger!.Type));
            }
        }

        // 校验 CreateWithOverlay 完整保留 4 张元进度/词条新表
        var overlay = repo.CreateWithOverlay();
        Assert.Equal(80, overlay.ChallengesById.Count);
        Assert.Equal(1644, overlay.CollectiblesById.Count);
        Assert.Equal(21, overlay.SeasonsById.Count);
        Assert.Equal(136, overlay.TooltipSymbolsById.Count);
    }

    // ── P1-02: ChallengeService 分配、重随、规则推进、领奖、跨周期与持久化 ────

    [Fact]
    public void P1_02_ChallengeService_Lifecycle_TriggerMatching_AndPersistence()
    {
        var repo = RepoLazy.Value;
        string stateDbPath = Path.Combine(Path.GetTempPath(), $"bazaar_w4a_ch_{Guid.NewGuid():N}.db");
        Guid accountId = Guid.NewGuid();
        DateTime day1 = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc); // 周三

        try
        {
            Guid rerolledNewDailyId;
            Guid completedDailyId;
            int expectedXp;

            using (var db = new Persistence(stateDbPath))
            {
                var accounts = new AccountStore(db);
                var service = new ChallengeService(db, repo, accounts, () => new Random(20260610));

                // 1. 首次分配每日 3 条 + 每周 3 条活跃且不重复挑战
                var initial = service.GetChallenges(accountId, day1);
                var dailyItems = initial.dailyChallenges.Cast<ChallengeProgressItemDto>().ToList();
                var weeklyItems = initial.weeklyChallenges.Cast<ChallengeProgressItemDto>().ToList();
                Assert.Equal(3, dailyItems.Count);
                Assert.Equal(3, weeklyItems.Count);
                Assert.Equal(6, initial.challenges.Length);
                Assert.Equal(3, dailyItems.Select(x => x.Id).Distinct().Count());
                Assert.Equal(3, weeklyItems.Select(x => x.Id).Distinct().Count());
                Assert.Equal(1, initial.dailyResets);
                Assert.Equal(1, initial.weeklyResets);
                Assert.All(dailyItems, x => Assert.True(repo.GetChallenge(x.Id.ToString("D"))!.IsDaily && repo.GetChallenge(x.Id.ToString("D"))!.IsActive));
                Assert.All(weeklyItems, x => Assert.True(repo.GetChallenge(x.Id.ToString("D"))!.IsWeekly && repo.GetChallenge(x.Id.ToString("D"))!.IsActive));

                // 2. 重随每日挑战与每周挑战（次数消耗与用尽拦截）
                Guid targetDaily = dailyItems[0].Id;
                var rerollDaily = service.RerollDaily(accountId, targetDaily, day1);
                Assert.True(rerollDaily.Ok);
                Assert.NotEqual(targetDaily, rerollDaily.NewChallengeId);
                Assert.Equal(0, rerollDaily.Snapshot.dailyResets);
                rerolledNewDailyId = rerollDaily.NewChallengeId!.Value;

                var rerollDailyAgain = service.RerollDaily(accountId, rerolledNewDailyId, day1);
                Assert.False(rerollDailyAgain.Ok);

                Guid targetWeekly = weeklyItems[0].Id;
                var rerollWeekly = service.RerollWeekly(accountId, targetWeekly, day1);
                Assert.True(rerollWeekly.Ok);
                Assert.NotEqual(targetWeekly, rerollWeekly.NewChallengeId);
                Assert.Equal(0, rerollWeekly.Snapshot.weeklyResets);

                // 3. 验证核心规则触发器精确匹配（涵盖多类真实 DB 挑战）
                var richWeekly = repo.ChallengesById.Values.First(c => c.DisplayTitle == "I'm Rich!");
                Assert.True(ChallengeService.MatchesChallengeTrigger(richWeekly, "TTriggerOnPlayerAttributeChanged", 50,
                    new ChallengeProgressContext { AttributeType = "Gold", ChangeType = "Gain" }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(richWeekly, "TTriggerOnPlayerAttributeChanged", 50,
                    new ChallengeProgressContext { AttributeType = "Gold", ChangeType = "Loss" }));

                var hunterDaily = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Hunter");
                Assert.True(ChallengeService.MatchesChallengeTrigger(hunterDaily, "TTriggerOnFightEnded", 1,
                    new ChallengeProgressContext { CombatType = "PVE", CombatOutcome = "Win" }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(hunterDaily, "TTriggerOnFightEnded", 1,
                    new ChallengeProgressContext { CombatType = "PVP", CombatOutcome = "Win" }));

                var materialistic = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Materialistic");
                Assert.True(ChallengeService.MatchesChallengeTrigger(materialistic, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardAttributes = new Dictionary<string, double> { ["BuyPrice"] = 12 } }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(materialistic, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardAttributes = new Dictionary<string, double> { ["BuyPrice"] = 6 } }));

                var armory = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Armory");
                Assert.True(ChallengeService.MatchesChallengeTrigger(armory, "TTriggerOnItemUsed", 1,
                    new ChallengeProgressContext { CardType = "Item", Tags = ["Weapon"] }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(armory, "TTriggerOnItemUsed", 1,
                    new ChallengeProgressContext { CardType = "Item", Tags = ["Food"] }));

                var levelUpLow = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Rolling out the Carpet");
                Assert.True(ChallengeService.MatchesChallengeTrigger(levelUpLow, "TTriggerOnPlayerLeveledUp", 1,
                    new ChallengeProgressContext { NewLevel = 3 }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(levelUpLow, "TTriggerOnPlayerLeveledUp", 1,
                    new ChallengeProgressContext { NewLevel = 5 }));

                var emptyClip = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Empty the Clip");
                Assert.True(ChallengeService.MatchesChallengeTrigger(emptyClip, "TTriggerOnItemUsed", 1,
                    new ChallengeProgressContext { CardType = "Item", HiddenTags = ["Ammo"] }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(emptyClip, "TTriggerOnItemUsed", 1,
                    new ChallengeProgressContext { CardType = "Item", HiddenTags = ["Burn"] }));

                var seeingDouble = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Seeing Double");
                Assert.True(ChallengeService.MatchesChallengeTrigger(seeingDouble, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardAttributes = new Dictionary<string, double> { ["Multicast"] = 2 } }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(seeingDouble, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardAttributes = new Dictionary<string, double> { ["Multicast"] = 1 } }));

                var midasTouch = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Midas Touch");
                Assert.True(ChallengeService.MatchesChallengeTrigger(midasTouch, "TTriggerOnCardUpgraded", 1,
                    new ChallengeProgressContext { CardType = "Item", CardTier = "Gold" }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(midasTouch, "TTriggerOnCardUpgraded", 1,
                    new ChallengeProgressContext { CardType = "Item", CardTier = "Silver" }));

                var bigHaul = repo.ChallengesById.Values.First(c => c.DisplayTitle == "Big Haul");
                Assert.True(ChallengeService.MatchesChallengeTrigger(bigHaul, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardSize = "Large" }));
                Assert.False(ChallengeService.MatchesChallengeTrigger(bigHaul, "TTriggerOnCardPurchased", 1,
                    new ChallengeProgressContext { CardType = "Item", CardSize = "Small" }));

                // 4. 推进某条活跃每日挑战进度 -> 未满不可领 -> 达标领奖加 XP -> 重复领取幂等
                completedDailyId = rerolledNewDailyId;
                var tpl = repo.GetChallenge(completedDailyId.ToString("D"))!;
                expectedXp = tpl.XpReward;

                var prematureAck = service.AcknowledgeChallenge(accountId, completedDailyId, currentSeasonId: 2, day1);
                Assert.False(prematureAck.Ok);

                var progRes = service.RecordChallengeProgressById(accountId, completedDailyId, tpl.CompletionRequirement + 99, day1);
                Assert.Contains(completedDailyId, progRes.UpdatedChallengeIds);
                Assert.Contains(completedDailyId, progRes.NewlyCompletedChallengeIds);
                var updatedItem = progRes.Snapshot.dailyChallenges.Cast<ChallengeProgressItemDto>().First(x => x.Id == completedDailyId);
                Assert.Equal(tpl.CompletionRequirement, updatedItem.Progress);

                var ackRes = service.AcknowledgeChallenge(accountId, completedDailyId, currentSeasonId: 2, day1);
                Assert.True(ackRes.Ok);
                Assert.Equal(expectedXp, ackRes.XpGranted);
                Assert.Equal(expectedXp, ackRes.NewSeasonExperience);

                // 重复领奖幂等，不重复加经验
                var ackDup = service.AcknowledgeChallenge(accountId, completedDailyId, currentSeasonId: 2, day1);
                Assert.True(ackDup.Ok);
                Assert.Equal(0, ackDup.XpGranted);
                Assert.Equal(expectedXp, ackDup.NewSeasonExperience);
            }

            // 5. 重建 Persistence 与 ChallengeService 校验 SQLite 持久化存活 + 跨天/跨周刷新
            using (var db2 = new Persistence(stateDbPath))
            {
                var service2 = new ChallengeService(db2, repo, rngFactory: () => new Random(99));
                var reloaded = service2.GetChallenges(accountId, day1);
                var savedItem = reloaded.dailyChallenges.Cast<ChallengeProgressItemDto>().First(x => x.Id == completedDailyId);
                Assert.True(savedItem.Acknowledged);
                Assert.Equal(0, reloaded.dailyResets);
                Assert.Equal(expectedXp, db2.LoadSeasonTrackProgress(accountId, 2).Experience);

                // 跨自然日：每日刷新并恢复 dailyResets=1，每周保留原状态
                var nextDay = day1.AddDays(1);
                var afterDayReset = service2.GetChallenges(accountId, nextDay);
                Assert.Equal(1, afterDayReset.dailyResets);
                Assert.Equal(0, afterDayReset.weeklyResets);

                // 跨自然周：每周也刷新并恢复 weeklyResets=1
                var nextWeek = day1.AddDays(7);
                var afterWeekReset = service2.GetChallenges(accountId, nextWeek);
                Assert.Equal(1, afterWeekReset.weeklyResets);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(stateDbPath)) File.Delete(stateDbPath); } catch { }
            try { if (File.Exists(stateDbPath + "-wal")) File.Delete(stateDbPath + "-wal"); } catch { }
            try { if (File.Exists(stateDbPath + "-shm")) File.Delete(stateDbPath + "-shm"); } catch { }
        }
    }

    // ── P1-02: TempoNet 全部 5 个挑战 HTTP 接口端到端校验 ─────────────────────

    [Fact]
    public async Task P1_02_TempoNet_FiveChallengeEndpoints_ReturnPersistedDataAndGrantSeasonXp()
    {
        var repo = RepoLazy.Value;
        var (app, http, stateDbPath, _) = await StartHostAsync(repo);
        try
        {
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "ChallengeTester" });
            loginResp.EnsureSuccessStatusCode();
            var token = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // 1. GET /api/Challenges/me
            var meJson = JsonNode.Parse(await http.GetStringAsync("/api/Challenges/me"))!;
            var dailyArr = meJson["dailyChallenges"]!.AsArray();
            var weeklyArr = meJson["weeklyChallenges"]!.AsArray();
            Assert.Equal(3, dailyArr.Count);
            Assert.Equal(3, weeklyArr.Count);
            Assert.Equal(1, meJson["dailyResets"]!.GetValue<int>());
            Assert.Equal(1, meJson["weeklyResets"]!.GetValue<int>());

            string firstDailyId = dailyArr[0]!["Id"]!.GetValue<string>();
            string firstWeeklyId = weeklyArr[0]!["Id"]!.GetValue<string>();

            // 2. POST /api/Challenges/daily/reroll
            var rerollDailyResp = await http.PostAsJsonAsync("/api/Challenges/daily/reroll", new { dailyChallengeId = firstDailyId });
            rerollDailyResp.EnsureSuccessStatusCode();
            var afterDailyReroll = JsonNode.Parse(await rerollDailyResp.Content.ReadAsStringAsync())!;
            Assert.Equal(0, afterDailyReroll["dailyResets"]!.GetValue<int>());
            string newDailyId = afterDailyReroll["dailyChallenges"]![0]!["Id"]!.GetValue<string>();
            Assert.NotEqual(firstDailyId, newDailyId, StringComparer.OrdinalIgnoreCase);

            // 3. POST /api/Challenges/weekly/reroll
            var rerollWeeklyResp = await http.PostAsJsonAsync("/api/Challenges/weekly/reroll", new { weeklyChallengeId = firstWeeklyId });
            rerollWeeklyResp.EnsureSuccessStatusCode();
            var afterWeeklyReroll = JsonNode.Parse(await rerollWeeklyResp.Content.ReadAsStringAsync())!;
            Assert.Equal(0, afterWeeklyReroll["weeklyResets"]!.GetValue<int>());
            string newWeeklyId = afterWeeklyReroll["weeklyChallenges"]![0]!["Id"]!.GetValue<string>();
            Assert.NotEqual(firstWeeklyId, newWeeklyId, StringComparer.OrdinalIgnoreCase);

            // 4. POST /api/app/challenges/progress（带参推进至完成 + 无 body 同步返回 StateDeltaResponse）
            var tpl = repo.GetChallenge(newDailyId)!;
            var progResp = await http.PostAsJsonAsync("/api/app/challenges/progress", new
            {
                challengeId = newDailyId,
                delta = tpl.CompletionRequirement,
            });
            progResp.EnsureSuccessStatusCode();
            var progDelta = JsonNode.Parse(await progResp.Content.ReadAsStringAsync())!;
            var progressedDaily = progDelta["Challenges"]!["dailyChallenges"]!.AsArray()
                .First(x => string.Equals(x!["Id"]!.GetValue<string>(), newDailyId, StringComparison.OrdinalIgnoreCase))!;
            Assert.Equal(tpl.CompletionRequirement, progressedDaily["Progress"]!.GetValue<int>());

            // 官方客户端无 body 同步调用
            var emptySyncResp = await http.PostAsync("/api/app/challenges/progress", null);
            emptySyncResp.EnsureSuccessStatusCode();
            var emptySyncJson = JsonNode.Parse(await emptySyncResp.Content.ReadAsStringAsync())!;
            Assert.NotNull(emptySyncJson["Challenges"]);
            Assert.NotNull(emptySyncJson["SeasonTrackProgression"]);

            // 5. POST /api/Challenges/acknowledge/{id} 确认领奖并增加赛季通行证 XP
            var ackResp = await http.PostAsync($"/api/Challenges/acknowledge/{newDailyId}", null);
            ackResp.EnsureSuccessStatusCode();
            var ackJson = JsonNode.Parse(await ackResp.Content.ReadAsStringAsync())!;
            Assert.True(ackJson["ok"]!.GetValue<bool>());
            Assert.Equal(tpl.XpReward, ackJson["xpGranted"]!.GetValue<int>());
            Assert.Equal(tpl.XpReward, ackJson["seasonExperience"]!.GetValue<int>());

            // 验证 SeasonTrackProgressions/me 与 Bootstrap/app 均反映持久化 XP 与 Acknowledged 状态
            var trackProg = JsonNode.Parse(await http.GetStringAsync("/api/SeasonTrackProgressions/me"))!;
            Assert.Equal(tpl.XpReward, trackProg["experience"]!.GetValue<int>());

            var boot = JsonNode.Parse(await http.GetStringAsync("/api/Bootstrap/app"))!;
            Assert.Equal(3, boot["challenges"]!["dailyChallenges"]!.AsArray().Count);
            Assert.Equal(tpl.XpReward, boot["season"]!["trackProgression"]!["experience"]!.GetValue<int>());

            var postRun = JsonNode.Parse(await http.GetStringAsync("/api/Bootstrap/post-run"))!;
            Assert.Equal(3, postRun["challenges"]!["weeklyChallenges"]!.AsArray().Count);
            Assert.Equal(tpl.XpReward, postRun["seasonTrackProgression"]!["experience"]!.GetValue<int>());
        }
        finally
        {
            await CleanupHostAsync(app, http, stateDbPath);
        }
    }

    // ── P1-03: collectibles 整表 1,644 条 + IsFoundInChests 过滤 + 55/25/13/5/2 分桶 ─

    [Fact]
    public void P1_03_CollectiblesTable_Loads1644Entries_AndChestRollMatchesDropRatesExcludingNonChest()
    {
        var repo = RepoLazy.Value;

        Assert.Equal(1644, repo.CollectiblesById.Count);
        Assert.Equal(868, repo.CollectiblesById.Values.Count(c => c.IsFoundInChests));
        Assert.Equal(776, repo.CollectiblesById.Values.Count(c => !c.IsFoundInChests));
        Assert.Equal(680, repo.CollectiblesById.Values.Count(c => c.IsCardSkin && c.IsFoundInChests));
        Assert.Equal(561, repo.CollectiblesById.Values.Count(c => c.IsCardSkin && !c.IsFoundInChests));

        // 确认外置白名单缺失的 7 条藏品已完整存在于 GameDataRepository.CollectiblesById
        Assert.NotNull(repo.GetCollectible("3809c54a-2d55-af74-0943-4dfa8b0c1631"));
        Assert.False(repo.GetCollectible("3809c54a-2d55-af74-0943-4dfa8b0c1631")!.IsFoundInChests);

        // 稀有度全量分布核验
        Assert.Equal(1118, repo.CollectiblesById.Values.Count(c => c.Rarity == "Bronze"));
        Assert.Equal(224, repo.CollectiblesById.Values.Count(c => c.Rarity == "Silver"));
        Assert.Equal(118, repo.CollectiblesById.Values.Count(c => c.Rarity == "Gold"));
        Assert.Equal(100, repo.CollectiblesById.Values.Count(c => c.Rarity == "Diamond"));
        Assert.Equal(84, repo.CollectiblesById.Values.Count(c => c.Rarity == "Legendary"));

        // 故意传入全量 1,644 条（含 776 条 IsFoundInChests=false），验证 20,000 次蒙特卡洛抽取：
        // 1) 绝对 0 次抽到非宝箱藏品；2) 5 档稀有度严格收敛至 55%/25%/13%/5%/2%
        var allEntries = repo.CollectiblesById.Values
            .Select(c => (
                Entry: new TempoNetEndpoints.ValidCollectibleEntry
                {
                    Id = c.Id,
                    Type = c.GetCanonicalCategoryKey(),
                    InternalName = c.InternalName,
                    Name = c.Name,
                    HeroId = c.HeroId,
                    SeasonId = c.SeasonId,
                    Rarity = c.Rarity,
                    IsFoundInChests = c.IsFoundInChests,
                    InStore = c.InStore,
                },
                ParsedGuid: Guid.Parse(c.Id),
                NormId: c.NormalizedId))
            .ToList();

        var rng = new Random(42001);
        var rarityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int trials = 20_000;
        for (int i = 0; i < trials; i++)
        {
            var draw = TempoNetEndpoints.RollChestCollectible(allEntries, ownedNormalizedIds: null, rng, preferUnowned: false);
            Assert.NotNull(draw);
            Assert.True(draw!.Value.Entry.IsFoundInChests, $"严禁抽中非宝箱藏品: {draw.Value.Entry.Id}");
            string r = draw.Value.Entry.Rarity;
            rarityCounts[r] = rarityCounts.GetValueOrDefault(r) + 1;
        }

        Assert.InRange(rarityCounts["Bronze"] / (double)trials, 0.52, 0.58);
        Assert.InRange(rarityCounts["Silver"] / (double)trials, 0.22, 0.28);
        Assert.InRange(rarityCounts["Gold"] / (double)trials, 0.11, 0.15);
        Assert.InRange(rarityCounts["Diamond"] / (double)trials, 0.035, 0.065);
        Assert.InRange(rarityCounts["Legendary"] / (double)trials, 0.01, 0.03);
    }

    [Fact]
    public async Task P1_03_TempoNet_HandleOpenChests_ExcludesAllNonChestSkins()
    {
        var repo = RepoLazy.Value;
        var (app, http, stateDbPath, _) = await StartHostAsync(repo);
        try
        {
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "ChestPoolTester" });
            loginResp.EnsureSuccessStatusCode();
            var token = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // 额外发放宝箱并连续开启 50 个，校验每件掉落均满足 IsFoundInChests == true
            var grant = await http.PostAsJsonAsync("/api/Chests/grant", new { count = 30 });
            grant.EnsureSuccessStatusCode();
            var openResp = await http.PostAsJsonAsync("/api/app/chests/open-bundled", new { count = 50 });
            openResp.EnsureSuccessStatusCode();

            var openJson = JsonNode.Parse(await openResp.Content.ReadAsStringAsync())!;
            var chests = openJson["Chests"]!.AsArray();
            Assert.Equal(50, chests.Count);

            foreach (var c in chests)
            {
                string itemId = c!["collectionItemId"]!.GetValue<string>();
                var tpl = repo.GetCollectible(itemId);
                Assert.NotNull(tpl);
                Assert.True(tpl!.IsFoundInChests, $"开箱命中了非宝箱限定藏品 {tpl.Id} ({tpl.Name})");
            }
        }
        finally
        {
            await CleanupHostAsync(app, http, stateDbPath);
        }
    }

    // ── P1-06: seasons 整表 21 个赛季与 TempoNet 驱动 ────────────────────────

    [Fact]
    public async Task P1_06_SeasonsTable_LoadsAll21Seasons_AndSupportsConfiguredCurrentSeason()
    {
        var repo = RepoLazy.Value;

        Assert.Equal(21, repo.SeasonsById.Count);
        Assert.Equal("Beta Season", repo.GetSeason(1)!.InternalName);
        Assert.Equal("Season 1", repo.GetSeason(2)!.InternalName);
        Assert.Equal("Season 20", repo.GetSeason(21)!.InternalName);
        Assert.Equal(5, repo.SeasonsById.Values.Select(s => s.ChestSeasonalAssetId).Distinct().Count());
        Assert.All(repo.SeasonsById.Values, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.TextSeasonalAssetId));
            Assert.False(string.IsNullOrWhiteSpace(s.SpriteSeasonalAssetId));
            Assert.False(string.IsNullOrWhiteSpace(s.ChestSeasonalAssetId));
        });

        var (app, http, stateDbPath, _) = await StartHostAsync(repo);
        try
        {
            // 1. /api/Seasons 返回全部 21 个赛季与真实赛季宝箱资产 ID
            var allSeasons = JsonNode.Parse(await http.GetStringAsync("/api/Seasons"))!.AsArray();
            Assert.Equal(21, allSeasons.Count);
            Assert.Equal(1, allSeasons[0]!["id"]!.GetValue<int>());
            Assert.Equal(21, allSeasons[20]!["id"]!.GetValue<int>());
            Assert.Equal("Season 20", allSeasons[20]!["name"]!.GetValue<string>());
            Assert.Equal(repo.GetSeason(2)!.ChestSeasonalAssetId, allSeasons[1]!["chestSeasonalAssetId"]!.GetValue<string>());

            // 2. 默认 /api/Seasons/current 兼容返回 Season 2 ("Season 1")
            var curDefault = JsonNode.Parse(await http.GetStringAsync("/api/Seasons/current"))!;
            Assert.Equal(2, curDefault["id"]!.GetValue<int>());
            Assert.Equal("Season 1", curDefault["name"]!.GetValue<string>());
            Assert.Equal(repo.GetSeason(2)!.ChestSeasonalAssetId, curDefault["chestSeasonalAssetId"]!.GetValue<string>());

            // 3. /api/Seasons/{id} 查询指定赛季与 404 越界保护
            var s5 = JsonNode.Parse(await http.GetStringAsync("/api/Seasons/5"))!;
            Assert.Equal("Season 4", s5["name"]!.GetValue<string>());
            Assert.Equal(repo.GetSeason(5)!.ChestSeasonalAssetId, s5["chestSeasonalAssetId"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/Seasons/999")).StatusCode);
        }
        finally
        {
            await CleanupHostAsync(app, http, stateDbPath);
        }

        // 4. 通过配置覆盖切换当前活跃赛季 ID（隔离内存配置，不污染全局进程环境变量，零并发竞态）
        var (appS5, httpS5, stateDbPathS5, _) = await StartHostAsync(
            repo,
            new Dictionary<string, string?> { ["BAZAAR_CURRENT_SEASON_ID"] = "5" });
        try
        {
            Assert.Equal(5, TempoNetEndpoints.ResolveCurrentSeasonId(repo, overrideSeasonId: 5));
            var curOverridden = JsonNode.Parse(await httpS5.GetStringAsync("/api/Seasons/current"))!;
            Assert.Equal(5, curOverridden["id"]!.GetValue<int>());
            Assert.Equal("Season 4", curOverridden["name"]!.GetValue<string>());
            Assert.Equal(repo.GetSeason(5)!.ChestSeasonalAssetId, curOverridden["chestSeasonalAssetId"]!.GetValue<string>());

            // Bootstrap/app 同步返回 21 个赛季与当前配置赛季 5
            var loginResp = await httpS5.PostAsJsonAsync("/api/auth/login/user", new { username = "Season5Tester" });
            loginResp.EnsureSuccessStatusCode();
            var token = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            httpS5.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var bootS5 = JsonNode.Parse(await httpS5.GetStringAsync("/api/Bootstrap/app"))!;
            Assert.Equal(5, bootS5["season"]!["current"]!["id"]!.GetValue<int>());
            Assert.Equal(21, bootS5["season"]!["all"]!.AsArray().Count);
        }
        finally
        {
            await CleanupHostAsync(appS5, httpS5, stateDbPathS5);
        }
    }

    // ── P2-03: tooltips 整表 136 条 TTooltipSymbol + TooltipGlossary 兼容 ────

    [Fact]
    public void P2_03_TooltipsTable_LoadsAll136Symbols_AndPreservesGlossaryCompatibility()
    {
        var repo = RepoLazy.Value;

        Assert.Equal(136, repo.TooltipSymbolsById.Count);
        Assert.Equal(62, repo.TooltipGlossary.Count);
        Assert.Equal(62, repo.TooltipSymbolsById.Values.Count(s => !string.IsNullOrEmpty(s.Keyword)));
        Assert.Equal(74, repo.TooltipSymbolsById.Values.Count(s => string.IsNullOrEmpty(s.Keyword)));
        Assert.Equal(32, repo.TooltipSymbolsById.Values.Count(s => !string.Equals(s.Icon, "Invalid", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(97, repo.TooltipSymbolsById.Values.Count(s => s.Singleton));
        Assert.Equal(13, repo.TooltipSymbolsById.Values.Count(s => s.PostSymbol));
        Assert.Equal(20, repo.TooltipSymbolsById.Values.Count(s => s.PostWord));

        var charge = repo.GetTooltipSymbol("Charge");
        Assert.NotNull(charge);
        Assert.Equal("AdvanceIcon", charge!.Icon);
        Assert.True(charge.PostSymbol);
        Assert.False(charge.PostWord);
        Assert.Equal(charge.Keyword, repo.TooltipGlossary["Charge"]);

        var whenSold = repo.GetTooltipSymbol("When Sold:");
        Assert.NotNull(whenSold);
        Assert.True(whenSold!.Singleton);
        Assert.Equal("", whenSold.Keyword);
        Assert.False(repo.TooltipGlossary.ContainsKey("When Sold:"));
    }

    // ── P2-04: GameModeConfig 单一信源代理与字段透传 ─────────────────────────

    [Fact]
    public void P2_04_GameModeConfig_ProxiesSingleSourceGameModeData_AndPassesThroughCustomFields()
    {
        var repo = RepoLazy.Value;
        var fromRepo = GameModeConfig.FromRepository(repo);
        var fromDb = GameModeConfig.Load(DbPathLazy.Value);

        Assert.Same(repo.GameMode, fromRepo.Source);
        Assert.Equal(repo.GameMode.CombatSimEffectDelay, fromRepo.CombatSimEffectDelay);
        Assert.Equal(repo.GameMode.CritPercentMultiplier, fromRepo.CritPercentMultiplier);
        Assert.Equal(repo.GameMode.BurnDecrementByAsPercentage, fromRepo.BurnDecrementByAsPercentage);
        Assert.Equal(repo.GameMode.Sandstorm.BaseTickRate, fromRepo.SandstormBaseTickRate);
        Assert.Equal(repo.GameMode.Sandstorm.CountdownStartTime, fromRepo.SandstormCountdownStartTime);
        Assert.Equal(repo.GameMode.Sandstorm.CountdownDuration, fromRepo.SandstormCountdownDuration);
        Assert.Equal(fromRepo.NumCarpetSocketsUnlockPerLevel, fromDb.NumCarpetSocketsUnlockPerLevel);
        Assert.Equal(fromRepo.PrestigeLossFromMonsters, fromDb.PrestigeLossFromMonsters);

        // 自定义 Mod 数值透传（NumCarpetSocketsUnlockPerLevel=1 与 PrestigeLossFromMonsters=3 不被下限吞噬）
        var customData = new GameModeData
        {
            Id = repo.GameMode.Id,
            InternalName = "Hardcore Mod Mode",
            NumCarpetSocketsUnlockPerLevel = 1,
            CombatSimEffectDelay = 120,
            CritPercentMultiplier = 250,
            BurnDecrementByAsPercentage = 5,
            Prestige = new PrestigeSettings { PrestigeInitial = 15, PrestigeMax = 15, PrestigeLossFromMonsters = 3 },
            Sandstorm = new SandstormSettings { BaseTickRate = 200, BaseDamage = 4, CountdownStartTime = 15000, CountdownDuration = 5000 },
        };
        var customCfg = GameModeConfig.FromGameModeData(customData);
        Assert.Equal(1, customCfg.NumCarpetSocketsUnlockPerLevel);
        Assert.Equal(3, customCfg.PrestigeLossFromMonsters);
        Assert.Equal(120, customCfg.CombatSimEffectDelay);
        Assert.Equal(250, customCfg.CritPercentMultiplier);
        Assert.Equal(5, customCfg.BurnDecrementByAsPercentage);
        Assert.Equal(200, customCfg.SandstormBaseTickRate);
        Assert.Equal(4, customCfg.SandstormBaseDamage);
        Assert.Equal(15000, customCfg.SandstormCountdownStartTime);
        Assert.Equal(5000, customCfg.SandstormCountdownDuration);
    }

    // ── P2-05: level_ups.TutorialRewards Lv1~Lv3 透传与 GetRewardsNode ───────

    [Fact]
    public void P2_05_LevelUpTable_ExposesTutorialRewardsForLv1To3_AndSupportsTutorialRolling()
    {
        var repo = RepoLazy.Value;
        var tableFromRepo = LevelUpTable.FromRepository(repo);
        var tableFromDb = LevelUpTable.Load(DbPathLazy.Value);

        string[] expectedTutorialRewardCards =
        [
            "bc75676c-f338-451d-9ea6-be1a2f158e12", // Lv1 TutorialRewards
            "8d5c8a08-9e12-49d0-a7d5-18df5c30aa76", // Lv2 TutorialRewards
            "8b811b0f-8b3d-4361-99db-b2d1b2d5a423", // Lv3 TutorialRewards
        ];

        foreach (var table in new[] { tableFromRepo, tableFromDb })
        {
            for (int dbLv = 1; dbLv <= 3; dbLv++)
            {
                var entry = table.GetEntryByDbId(dbLv);
                Assert.NotNull(entry);
                Assert.True(entry!.HasTutorialRewards);
                Assert.NotNull(entry.TutorialRewardsNode);
                Assert.Same(entry.TutorialRewardsNode, entry.GetRewardsNode(isTutorial: true));
                Assert.Same(entry.RewardsNode, entry.GetRewardsNode(isTutorial: false));
                Assert.NotSame(entry.RewardsNode, entry.TutorialRewardsNode);
                Assert.NotEmpty(entry.TutorialGroups);
                string prefix = expectedTutorialRewardCards[dbLv - 1][..8];
                Assert.Contains(entry.TutorialGroups.SelectMany(g => g.CardIds),
                    id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            // Lv4..30 无 TutorialRewards，教程模式调用 GetRewardsNode(true) 安全回退常规 RewardsNode
            for (int dbLv = 4; dbLv <= 30; dbLv++)
            {
                var entry = table.GetEntryByDbId(dbLv);
                Assert.NotNull(entry);
                Assert.False(entry!.HasTutorialRewards);
                Assert.Null(entry.TutorialRewardsNode);
                Assert.Same(entry.RewardsNode, entry.GetRewardsNode(isTutorial: true));
            }
        }

        // 教程模式调用 RollRewardsWithTier(targetNewLevel: 2 => dbId=1) 优先消费 TutorialRewards
        var player = new PlayerState("tut_p1", "TutorialTester", HeroNames.Vanessa);
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, rng: new Random(7));
        var tutRolled = tableFromRepo.RollRewardsWithTier(level: 2, ctx, isTutorial: true);
        Assert.NotEmpty(tutRolled);
        Assert.Contains(tutRolled, r => r.TemplateId.StartsWith("bc75676c", StringComparison.OrdinalIgnoreCase));
    }

    // ── P2-06: MonsterPool 零调用死方法清理校验 ──────────────────────────────

    [Fact]
    public void P2_06_MonsterPool_DeadScalingMethods_AreRemoved()
    {
        var type = typeof(BazaarServer.Data.MonsterTemplate);
        const BindingFlags allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        Assert.Null(type.GetMethod("ScaledHealth", allFlags));
        Assert.Null(type.GetMethod("ScaledPower", allFlags));
        Assert.Null(type.GetMethod("EstimatePower", allFlags));
    }
}
