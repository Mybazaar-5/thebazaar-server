using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Planner;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 6（测试组 B）：覆盖计划第十三节第 10、11、12、13、14 条及核心 API 契约端到端验证（第 17 条）。
/// 全部测试均使用独立临时目录、临时 SQLite DB、隔离 ModHost/CardPool 与随机回环端口，零全局副作用。
/// </summary>
public sealed class Wave6_LineupBlueprintFinderAndApiTests : IClassFixture<SharedGameDataFixture>
{
    private readonly SharedGameDataFixture _fx;

    public Wave6_LineupBlueprintFinderAndApiTests(SharedGameDataFixture fx)
    {
        _fx = fx;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #10-A：固定剧本全场景（跨天、跨分区、1 血、Day 16+、预览=开战、断线恢复）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Test10_FixedLineup_CrossDay_CrossPartition_OneHp_Day16_PreviewMatchesCombat_AndResume()
    {
        using var temp = new TempTestDir("w6_t10a");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();
        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");

        string accountId = Guid.NewGuid().ToString("D");

        // Day 1：跨天指派（原 Day 10）、跨分区克隆（mod:deadbeef1234+custom）、1 血手搓、自定义名牌
        var day1Board = MakeValidBoard(
            hero: "Pygmalien",
            day: 10,
            healthMax: 1,
            displayName: "一日一血魔王",
            itemTpl: itemTpl,
            poolPartition: "mod:deadbeef1234+custom",
            boardId: "orig_day10_cross_part");

        // Day 16：超过常规 15 天上限的固定剧本
        var day16Board = MakeValidBoard(
            hero: "Dooley",
            day: 16,
            healthMax: 1234,
            displayName: "十六天终极战神",
            itemTpl: itemTpl,
            poolPartition: "custom",
            boardId: "orig_day16");

        var (saveOk, saveErr, savedLineup) = db.SaveGhostLineup(new CustomGhostLineup
        {
            AccountId = accountId,
            Name = "全场景剧本",
            Days = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [1] = day1Board,
                [16] = day16Board,
            },
        });
        Assert.True(saveOk, $"保存测试剧本失败：{saveErr}");
        Assert.NotNull(savedLineup);

        var rules = new CustomRunRuleSet
        {
            CustomSeedText = "S0A1B2C3",
            GhostOpponentMode = EGhostOpponentMode.FixedLineup,
            SelectedGhostLineupId = savedLineup!.LineupId,
        };

        var run = new RunState
        {
            RunId = "run_t10a_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Username = "LineupTester",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = rules,
        };

        var engine = new RunEngine(
            run, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        engine.StartRun();

        Assert.Equal("全场景剧本", run.PinnedLineupName);
        var pinnedInDb = db.LoadRunGhosts(run.RunId, "pinned");
        Assert.True(pinnedInDb.ContainsKey(1));
        Assert.True(pinnedInDb.ContainsKey(16));
        Assert.Equal($"lineup_{savedLineup.LineupId}_d1", pinnedInDb[1].BoardId);
        Assert.Equal(1, pinnedInDb[1].Day);
        Assert.Equal(1, pinnedInDb[1].SourceDay);
        Assert.Equal(1, pinnedInDb[1].TargetDay);
        Assert.Equal(16, pinnedInDb[16].Day);
        Assert.Equal(16, pinnedInDb[16].SourceDay);
        Assert.Equal(16, pinnedInDb[16].TargetDay);

        // 推进至 Day 1 Hour 6 并执行预览
        run.Day = 1;
        run.Hour = 6;
        run.State = ERunState.Choice;
        run.PendingPvpGhost = null;
        InvokePrivate(engine, "EnsurePendingHour6PvpOpponentLocked", new object?[] { null });

        Assert.NotNull(run.PendingPvpGhost);
        Assert.NotNull(run.CurrentPvpOpponent);
        string previewBoardId = run.PendingPvpGhost!.BoardId;
        string previewAccountId = run.CurrentPvpOpponent!.PlayerLoadout?.accountId ?? "";
        string previewName = run.CurrentPvpOpponent.Name ?? "";

        Assert.Equal($"lineup_{savedLineup.LineupId}_d1", previewBoardId);
        Assert.Equal("一日一血魔王", previewName);
        Assert.Equal(1, run.OpponentAttributes[EPlayerAttributeType.HealthMax]);

        // 模拟断线重连：经 RunSnapshotSerializer 往返后重建新 RunEngine（PinnedGhostCache 为非持久化字段，须重新从 DB 读）
        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: true, pendingLevelUp: false);
        var resumedRun = RunSnapshotSerializer.Deserialize(snapJson, repo);
        Assert.NotNull(resumedRun);
        Assert.Equal("全场景剧本", resumedRun!.PinnedLineupName);
        resumedRun.PendingPvpGhost = null; // 强制验证即使内存 PendingPvpGhost 丢失，开战仍从 run_ghosts(pinned) 命中同一剧本

        var resumedEngine = new RunEngine(
            resumedRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);

        var pvpTpl = pool.Get(ShopEngine.Hour6PvpCardId);
        var combatOutcome = new RunEngine.Outcome();
        InvokePrivate(resumedEngine, "RunRealCombatSequence", new object?[] { combatOutcome, pvpTpl, true });

        Assert.NotNull(combatOutcome.CombatSpawnSim);
        Assert.NotNull(combatOutcome.CombatSpawnSim!.CurrentState?.PvpOpponent);
        Assert.Equal("一日一血魔王", combatOutcome.CombatSpawnSim.CurrentState!.PvpOpponent!.Name);
        Assert.Equal(previewAccountId, combatOutcome.CombatSpawnSim.CurrentState.PvpOpponent.PlayerLoadout?.accountId);
        Assert.Equal(1, combatOutcome.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax]);

        // 战后已记录 Day 1 faced 交手镜像
        var facedAfterDay1 = db.LoadRunGhosts(resumedRun.RunId, "faced");
        Assert.True(facedAfterDay1.ContainsKey(1));
        Assert.Equal($"lineup_{savedLineup.LineupId}_d1", facedAfterDay1[1].BoardId);

        // Day 16+ 仍严格按剧本出战（不钳回 Day 15）
        resumedRun.Day = 16;
        resumedRun.Hour = 6;
        resumedRun.State = ERunState.Choice;
        resumedRun.PendingPvpGhost = null;
        resumedRun.CurrentPvpOpponent = null!;
        InvokePrivate(resumedEngine, "EnsurePendingHour6PvpOpponentLocked", new object?[] { null });

        Assert.NotNull(resumedRun.PendingPvpGhost);
        Assert.Equal($"lineup_{savedLineup.LineupId}_d16", resumedRun.PendingPvpGhost!.BoardId);
        Assert.Equal("十六天终极战神", resumedRun.CurrentPvpOpponent?.Name);
        Assert.Equal(1234, resumedRun.OpponentAttributes[EPlayerAttributeType.HealthMax]);

        var day16Outcome = new RunEngine.Outcome();
        InvokePrivate(resumedEngine, "RunRealCombatSequence", new object?[] { day16Outcome, pvpTpl, true });
        Assert.NotNull(day16Outcome.CombatSpawnSim?.CurrentState?.PvpOpponent);
        Assert.Equal("十六天终极战神", day16Outcome.CombatSpawnSim!.CurrentState!.PvpOpponent!.Name);
        Assert.Equal(1234, day16Outcome.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #10-B：已删剧本一次性 Toast、整板缺卡回落、无尽 Day 25 不放大、锦标赛隔离、全随机不锁死
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test10_FixedLineup_DeletedToastOnce_EmptyCardFallback_EndlessDay25NoScale_TournamentIgnored_AndRandomUnpinned()
    {
        using var temp = new TempTestDir("w6_t10b");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();
        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");
        var pvpTpl = pool.Get(ShopEngine.Hour6PvpCardId);

        var accounts = new AccountStore(db);
        var ruleStore = new PlayerRuleStore(db);
        var sessions = new SessionStore();

        var acc = accounts.GetOrCreateByName("ToastTester");
        string accountId = acc.AccountId.ToString("D");

        // 1) 剧本已删 → 开局回落随机并设置一次性 PendingStartToastZh，经 active-run-tendency 读一次清空
        var deletedRun = new RunState
        {
            RunId = "run_t10_del_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Username = acc.Username,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet
            {
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = "ln_deleted_does_not_exist",
            },
        };
        var deletedEngine = new RunEngine(
            deletedRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        deletedEngine.StartRun();

        Assert.False(string.IsNullOrWhiteSpace(deletedRun.PendingStartToastZh));
        Assert.Null(deletedRun.PinnedLineupName);

        var session = sessions.Create(accountId, acc.Username, new InitializeRunCommand
        {
            PlayMode = EPlayMode.Unranked,
            SelectedHero = EHero.Vanessa,
            GameModeId = pool.DefaultGameModeId,
        });
        SessionRegistry.Attach(session.Sid, deletedRun, deletedEngine);

        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            app = builder.Build();
            CustomRuleEndpoints.Map(app, ruleStore, accounts, () => new { }, sessions, db);
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            using (var req1 = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me/active-run-tendency"))
            {
                req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);
                using var resp1 = await http.SendAsync(req1);
                Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
                using var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync());
                string? toast1 = doc1.RootElement.GetProperty("startToastZh").GetString();
                Assert.False(string.IsNullOrWhiteSpace(toast1), "首次读取 active-run-tendency 应返回一次性 startToastZh");
            }

            using (var req2 = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me/active-run-tendency"))
            {
                req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);
                using var resp2 = await http.SendAsync(req2);
                Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
                using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
                var toastEl2 = doc2.RootElement.GetProperty("startToastZh");
                Assert.True(toastEl2.ValueKind == JsonValueKind.Null, "二次读取 active-run-tendency 时 startToastZh 必须已清空为 null");
            }
        }
        finally
        {
            http?.Dispose();
            if (app != null) await app.DisposeAsync();
            SessionRegistry.Detach(session.Sid);
            sessions.Remove(session.Sid);
        }

        // 2) 整板缺卡（全部物品模板不存在）→ 当天回落随机、不崩
        var brokenRun = new RunState
        {
            RunId = "run_t10_broken_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet(),
        };
        var brokenEngine = new RunEngine(
            brokenRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        brokenEngine.StartRun();
        db.SaveRunGhost(brokenRun.RunId, 1, "pinned", new PvpGhostPool.GhostBoard
        {
            BoardId = "broken_missing_all_cards_d1",
            Day = 1,
            SourceDay = 1,
            TargetDay = 1,
            Hero = "Vanessa",
            HealthMax = 250,
            DisplayName = "坏板不应命中",
            Items = new List<PvpGhostPool.GhostItem>
            {
                new() { Slot = 0, Width = 1, TemplateId = "00000000-0000-0000-0000-deadbeefcafe", Tier = "Bronze" },
            },
        });
        brokenRun.PinnedGhostCache = null;
        brokenRun.Day = 1;
        brokenRun.Hour = 6;
        brokenRun.State = ERunState.Choice;
        brokenRun.PendingPvpGhost = null;
        InvokePrivate(brokenEngine, "EnsurePendingHour6PvpOpponentLocked", new object?[] { null });
        Assert.NotNull(brokenRun.PendingPvpGhost);
        Assert.NotEqual("broken_missing_all_cards_d1", brokenRun.PendingPvpGhost!.BoardId);

        var brokenCombat = new RunEngine.Outcome();
        InvokePrivate(brokenEngine, "RunRealCombatSequence", new object?[] { brokenCombat, pvpTpl, true });
        Assert.NotNull(brokenCombat.CombatSpawnSim);
        Assert.NotEqual("坏板不应命中", brokenCombat.CombatSpawnSim!.CurrentState?.PvpOpponent?.Name);

        // 3) 无尽模式 Day 25 固定剧本不被放大（Day=SourceDay=TargetDay=25，血量保持原值 777）
        var (eOk, _, endlessLineup) = db.SaveGhostLineup(new CustomGhostLineup
        {
            AccountId = accountId,
            Name = "无尽25测试剧本",
            Days = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [25] = MakeValidBoard("Mak", day: 25, healthMax: 777, displayName: "无尽25固定盘", itemTpl: itemTpl),
            },
        });
        Assert.True(eOk);

        var endlessRun = new RunState
        {
            RunId = "run_t10_endless_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet
            {
                SpecialModeId = ESpecialModeId.EndlessTower,
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = endlessLineup!.LineupId,
            },
        };
        var endlessEngine = new RunEngine(
            endlessRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        endlessEngine.StartRun();

        var endlessPinned = db.LoadRunGhosts(endlessRun.RunId, "pinned");
        Assert.True(endlessPinned.ContainsKey(25));
        Assert.Equal(25, endlessPinned[25].Day);
        Assert.Equal(25, endlessPinned[25].SourceDay);
        Assert.Equal(25, endlessPinned[25].TargetDay);

        endlessRun.Day = 25;
        endlessRun.Hour = 6;
        endlessRun.State = ERunState.Choice;
        endlessRun.PendingPvpGhost = null;
        var endlessCombat = new RunEngine.Outcome();
        InvokePrivate(endlessEngine, "RunRealCombatSequence", new object?[] { endlessCombat, pvpTpl, true });
        Assert.NotNull(endlessCombat.CombatSpawnSim?.Opponent);
        Assert.Equal("无尽25固定盘", endlessCombat.CombatSpawnSim!.CurrentState?.PvpOpponent?.Name);
        Assert.Equal(777, endlessCombat.CombatSpawnSim.Opponent!.Attributes[EPlayerAttributeType.HealthMax]);

        // 4) 锦标赛局（PlayMode = Tournament）不受个人固定剧本影响
        var (tOk, _, personalLineup) = db.SaveGhostLineup(new CustomGhostLineup
        {
            AccountId = accountId,
            Name = "不应进锦标赛",
            Days = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [1] = MakeValidBoard("Stelle", day: 1, healthMax: 333, displayName: "锦标赛禁止命中", itemTpl: itemTpl),
            },
        });
        Assert.True(tOk);

        var tourneyRun = new RunState
        {
            RunId = "run_t10_tourney_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Tournament,
            TournamentSessionId = Guid.NewGuid(),
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet
            {
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = personalLineup!.LineupId,
            },
        };
        var tourneyEngine = new RunEngine(
            tourneyRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        tourneyEngine.StartRun();

        Assert.Null(tourneyRun.PinnedLineupName);
        Assert.Empty(db.LoadRunGhosts(tourneyRun.RunId, "pinned"));
        tourneyRun.Day = 1;
        tourneyRun.Hour = 6;
        tourneyRun.State = ERunState.Choice;
        tourneyRun.PendingPvpGhost = null;
        InvokePrivate(tourneyEngine, "EnsurePendingHour6PvpOpponentLocked", new object?[] { null });
        Assert.NotEqual("锦标赛禁止命中", tourneyRun.CurrentPvpOpponent?.Name);

        // 5) 全随机模式（EGhostOpponentMode.Random）下同种子多次开局，PvP 对手走 Random.Shared 不被主种子锁死
        var observedBoardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int trial = 0; trial < 12; trial++)
        {
            var rState = new RunState
            {
                RunId = $"run_t10_rand_{trial}_{Guid.NewGuid():N}"[..20],
                AccountId = accountId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                GameModeId = pool.DefaultGameModeId,
                CustomRules = new CustomRunRuleSet
                {
                    CustomSeedText = "S1F1XED0",
                    GhostOpponentMode = EGhostOpponentMode.Random,
                },
            };
            var rEng = new RunEngine(
                rState, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
                archetypeIndex: _fx.ArchetypeIndex, db: db);
            rEng.StartRun();
            rState.Day = 3;
            rState.Hour = 6;
            rState.State = ERunState.Choice;
            rState.PendingPvpGhost = null;
            InvokePrivate(rEng, "EnsurePendingHour6PvpOpponentLocked", new object?[] { null });
            if (!string.IsNullOrWhiteSpace(rState.PendingPvpGhost?.BoardId))
                observedBoardIds.Add(rState.PendingPvpGhost!.BoardId);
        }
        Assert.True(observedBoardIds.Count >= 2,
            $"全随机镜像模式下同种子 12 次抽取不应被锁死为同一对手（实际仅见 {observedBoardIds.Count} 个唯一 BoardId）");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #11：进行中对局导出（只含已交手 + 当天 Hour 6 预览，不含未来天）与旧局 MasterSeed==0 拒导
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test11_BlueprintExport_ActiveRun_IncludesFacedAndHour6Preview_ExcludesFuture_AndRejectsLegacyZeroSeed()
    {
        using var temp = new TempTestDir("w6_t11");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();
        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");

        var modsDir = Path.Combine(temp.Root, "mods");
        var stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var sessions = new SessionStore();
        var blueprintService = new RunBlueprintService(
            db, ruleStore, sessions, modHost, communityMods, pool, monsters, _fx.ArchetypeIndex);

        string accountId = Guid.NewGuid().ToString("D");

        // 构造进行中对局（Day 2 Hour 6 预览中）：Day 1 已交手（faced）、Day 2 正在 Hour 6 预览、Day 5 只有未来 pinned（未打）
        var activeRun = new RunState
        {
            RunId = "run_t11_act_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accountId,
            Username = "Exporter",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet { CustomSeedText = "S2EXP011" },
        };
        var activeEngine = new RunEngine(
            activeRun, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
            archetypeIndex: _fx.ArchetypeIndex, db: db);
        activeEngine.StartRun();

        var day1Faced = MakeValidBoard("Vanessa", day: 1, healthMax: 280, displayName: "第一晚交手", itemTpl: itemTpl, boardId: "faced_d1");
        var day2Preview = MakeValidBoard("Dooley", day: 2, healthMax: 420, displayName: "第二晚预览", itemTpl: itemTpl, boardId: "preview_d2");
        var day5FuturePinned = MakeValidBoard("Mak", day: 5, healthMax: 900, displayName: "第五晚未来剧本", itemTpl: itemTpl, boardId: "pinned_d5");

        db.SaveRunGhost(activeRun.RunId, 1, "faced", day1Faced);
        db.SaveRunGhost(activeRun.RunId, 5, "pinned", day5FuturePinned);

        activeRun.Day = 2;
        activeRun.Hour = 6;
        activeRun.State = ERunState.Choice;
        activeRun.PendingPvpGhost = day2Preview;

        var sess = sessions.Create(accountId, "Exporter", new InitializeRunCommand
        {
            PlayMode = EPlayMode.Unranked,
            SelectedHero = EHero.Vanessa,
            GameModeId = pool.DefaultGameModeId,
        });
        SessionRegistry.Attach(sess.Sid, activeRun, activeEngine);
        try
        {
            // 1) Hour 6 预览中导出：应包含 [1, 2]，严格不包含未来 Day 5
            var expWithPreview = await blueprintService.ExportAsync(
                accountId,
                "Exporter",
                new BlueprintExportRequest
                {
                    Source = "active",
                    Scope = "events_and_ghosts",
                    IncludeRules = true,
                    Title = "进行中导出测试",
                });
            Assert.True(expWithPreview.Ok, $"导出失败：{expWithPreview.ErrorCode} {expWithPreview.ErrorMessage}");
            Assert.NotNull(expWithPreview.Data);
            Assert.StartsWith("BZ-", expWithPreview.Data!.ShortCode, StringComparison.Ordinal);
            Assert.StartsWith("BZR1:", expWithPreview.Data.OfflineCode, StringComparison.Ordinal);
            Assert.Equal(new[] { 1, 2 }, expWithPreview.Data.Summary.GhostDays);
            Assert.DoesNotContain(5, expWithPreview.Data.Summary.GhostDays);

            // 2) 非 Hour 6 预览（Hour 2，无 PendingPvpGhost）导出：仅包含已打过的 Day 1
            activeRun.Hour = 2;
            activeRun.PendingPvpGhost = null;
            var expFacedOnly = await blueprintService.ExportAsync(
                accountId,
                "Exporter",
                new BlueprintExportRequest
                {
                    Source = "active",
                    Scope = "events_and_ghosts",
                    IncludeRules = false,
                });
            Assert.True(expFacedOnly.Ok, $"仅已打天数导出失败：{expFacedOnly.ErrorMessage}");
            Assert.Equal(new[] { 1 }, expFacedOnly.Data!.Summary.GhostDays);

            // 3) 旧版本对局（MasterSeed == 0）导出：必须返回 409 SEED_UNAVAILABLE 中文错误
            activeRun.MasterSeed = 0;
            activeRun.SeedText = null;
            var expLegacy = await blueprintService.ExportAsync(
                accountId,
                "Exporter",
                new BlueprintExportRequest
                {
                    Source = "active",
                    Scope = "events_only",
                });
            Assert.False(expLegacy.Ok);
            Assert.Equal(409, expLegacy.HttpStatus);
            Assert.Equal("SEED_UNAVAILABLE", expLegacy.ErrorCode);
            Assert.Contains("没有种子", expLegacy.ErrorMessage ?? "");
        }
        finally
        {
            SessionRegistry.Detach(sess.Sid);
            sessions.Remove(sess.Sid);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #19：分享闭环——一局存成方案（自动补天）→ 改配套种子 → 分享方案 → 朋友用长码导入 → 朋友再分享 → 第三人导入
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Test19a_SeedCodeStrict_OnlyFixedFormat_AndCanonicalConversion()
    {
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText("s3kz 9qw1", out int m1, out string d1));
        Assert.Equal("S3KZ9QW1", d1);
        Assert.Equal(m1, DailyChallengeSeedGenerator.TryParseSeedText("S3KZ9QW1", out int m1b, out _) ? m1b : -1);
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText("SO0000L1", out _, out string d2));
        Assert.Equal("S0000011", d2); // O→0、L→1 笔误自动纠正
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("12345", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("我的幸运种子", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("S3KZ9QWU", out _, out _)); // U 不在字母表
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("S3KZ9QW", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("SZZZZZZZ", out _, out _)); // 超出 32 位
        Assert.Null(DailyChallengeSeedGenerator.NormalizeSeedCode("MY-SEED-10"));
        Assert.Equal("S3KZ9QW1", DailyChallengeSeedGenerator.NormalizeSeedCode(" s3kz9qw1 "));
    }

    [Fact]
    public async Task Test19b_ShareLoop_RunToLineup_AutoFill_EditSeed_ReShare_CrossServerImport()
    {
        using var temp = new TempTestDir("w6_t19");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();
        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");

        var modsDir = Path.Combine(temp.Root, "mods");
        var stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var sessions = new SessionStore();
        var svc = new RunBlueprintService(db, ruleStore, sessions, modHost, communityMods, pool, monsters, _fx.ArchetypeIndex);
        var lineupStore = new GhostLineupStore(db, ruleStore, repo, pool, monsters, _fx.ArchetypeIndex, modHost, communityMods);
        var webOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        string accA = Guid.NewGuid().ToString("D");
        string accB = Guid.NewGuid().ToString("D");
        string accC = Guid.NewGuid().ToString("D");

        // A 的进行中对局：第 1、2 天打过；这局本来用了方案，第 5 天已安排；现在第 3 天第 2 小时
        var run = new RunState
        {
            RunId = "run_t19_" + Guid.NewGuid().ToString("N")[..8],
            AccountId = accA,
            Username = "Sharer",
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            GameModeId = pool.DefaultGameModeId,
            CustomRules = new CustomRunRuleSet { CustomSeedText = "S1T19RXN" },
        };
        var engine = new RunEngine(run, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts, archetypeIndex: _fx.ArchetypeIndex, db: db);
        engine.StartRun();
        db.SaveRunGhost(run.RunId, 1, "faced", MakeValidBoard("Vanessa", 1, 300, "第一晚", itemTpl, boardId: "t19_d1"));
        db.SaveRunGhost(run.RunId, 2, "faced", MakeValidBoard("Dooley", 2, 420, "第二晚", itemTpl, boardId: "t19_d2"));
        db.SaveRunGhost(run.RunId, 5, "pinned", MakeValidBoard("Mak", 5, 900, "第五晚原方案", itemTpl, boardId: "t19_d5"));
        run.Day = 3;
        run.Hour = 2;
        run.State = ERunState.Choice;

        var sess = sessions.Create(accA, "Sharer", new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa, GameModeId = pool.DefaultGameModeId });
        SessionRegistry.Attach(sess.Sid, run, engine);
        try
        {
            string runSeed = DailyChallengeSeedGenerator.FormatSeedCode(run.MasterSeed);

            // 1) 存成方案，补到第 12 天
            var fr = await svc.CreateLineupFromRunAsync(accA, "active", 12, _fx.Ghosts);
            Assert.True(fr.Ok, $"{fr.ErrorCode} {fr.ErrorMessage}");
            var r1 = fr.Data!;
            Assert.Equal(new[] { 1, 2 }, r1.FacedDays);
            Assert.Equal(new[] { 5 }, r1.PinnedDays);
            Assert.NotEmpty(r1.AutoFilledDays);
            Assert.Equal(Enumerable.Range(1, 12), r1.FacedDays.Concat(r1.PinnedDays).Concat(r1.AutoFilledDays).Concat(r1.SkippedDays).OrderBy(d => d));
            Assert.Equal(runSeed, r1.Lineup.SeedText);
            Assert.Equal("Vanessa", r1.Lineup.SeedHero);
            Assert.Equal("第一晚", r1.Lineup.Days[1].DisplayName);
            Assert.Equal("第五晚原方案", r1.Lineup.Days[5].DisplayName);
            Assert.Equal(r1.FacedDays.Count + r1.PinnedDays.Count + r1.AutoFilledDays.Count, r1.Lineup.Days.Count);

            // 补满 30 天：镜像池没有那么晚的天时往前借，所以一天都不空；30 天的真实阵容也存得下
            var fr30 = await svc.CreateLineupFromRunAsync(accA, "active", 30, _fx.Ghosts);
            Assert.True(fr30.Ok, $"补满 30 天失败：{fr30.ErrorCode} {fr30.ErrorMessage}");
            Assert.Empty(fr30.Data!.SkippedDays);
            Assert.Equal(30, fr30.Data.Lineup.Days.Count);
            Assert.True(db.DeleteGhostLineup(accA, fr30.Data.Lineup.LineupId) || true);

            // 同一局再存一次：自动补的结果完全一样（不会每次乱变）
            var fr2 = await svc.CreateLineupFromRunAsync(accA, "active", 12, _fx.Ghosts);
            Assert.True(fr2.Ok);
            Assert.Equal(r1.AutoFilledDays, fr2.Data!.AutoFilledDays);
            int autoDay = r1.AutoFilledDays[0];
            Assert.Equal(r1.Lineup.Days[autoDay].HealthMax, fr2.Data.Lineup.Days[autoDay].HealthMax);
            Assert.Equal(
                string.Join(",", r1.Lineup.Days[autoDay].Items.Select(i => i.TemplateId)),
                string.Join(",", fr2.Data.Lineup.Days[autoDay].Items.Select(i => i.TemplateId)));

            // 2) 改配套种子：只认固定格式；不带 seedText 时保持原样；从游戏里往方案加一天也不能丢种子
            string lineupId = r1.Lineup.LineupId;
            var full = lineupStore.GetLineup(accA, lineupId)!;
            Assert.Equal(runSeed, full.SeedText);
            Dictionary<string, JsonElement> DaysJson(LineupFullDto l) => l.Days.ToDictionary(
                kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kv => JsonSerializer.SerializeToElement(kv.Value, webOpts));

            var bad = lineupStore.SaveLineup(accA, lineupId, full.Name, full.Description, null, DaysJson(full), seedProvided: true, seedText: "我的种子");
            Assert.False(bad.Ok);
            Assert.Equal("SEED_INVALID", bad.ErrorCode);

            var good = lineupStore.SaveLineup(accA, lineupId, full.Name, full.Description, null, DaysJson(full), seedProvided: true, seedText: "s2t19new");
            Assert.True(good.Ok, good.ErrorMessage);
            Assert.Equal("S2T19NEW", good.Data!.SeedText);
            Assert.Equal(full.DayNumbers, good.Data.DayNumbers);

            var keep = lineupStore.SaveLineup(accA, lineupId, full.Name + "改", full.Description, null, DaysJson(good.Data), seedProvided: false);
            Assert.True(keep.Ok, keep.ErrorMessage);
            Assert.Equal("S2T19NEW", keep.Data!.SeedText);
            Assert.Equal("S2T19NEW", db.LoadGhostLineup(accA, lineupId)!.SeedText);

            // 3) 分享这个方案：种子 = 方案的配套种子，对手 = 方案里所有天（包括第 10 天以后）
            var expA = await svc.ExportAsync(accA, "Sharer", new BlueprintExportRequest { Source = "lineup", LineupId = lineupId });
            Assert.True(expA.Ok, $"{expA.ErrorCode} {expA.ErrorMessage}");
            Assert.Equal("S2T19NEW", expA.Data!.Summary.SeedText);
            Assert.Equal(keep.Data.DayNumbers, expA.Data.Summary.GhostDays.ToArray());
            Assert.Contains(12, expA.Data.Summary.GhostDays.Concat(r1.SkippedDays));
            Assert.StartsWith("BZR1:", expA.Data.OfflineCode, StringComparison.Ordinal);

            // 4) 别的服务器的朋友 B：用长码导入 → 方案带种子、天数一致，下一局 = 这个种子 + 这个方案
            var impB = await svc.ImportAsync(accB, new BlueprintImportRequest { Code = expA.Data.OfflineCode, Opponents = "lineup", Apply = true });
            Assert.True(impB.Ok, $"{impB.ErrorCode} {impB.ErrorMessage}");
            Assert.NotNull(impB.Data!.Lineup);
            var rulesB = ruleStore.Get(accB);
            Assert.Equal("S2T19NEW", rulesB.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, rulesB.GhostOpponentMode);
            Assert.Equal(impB.Data.Lineup!.LineupId, rulesB.SelectedGhostLineupId);
            var lineupB = db.LoadGhostLineup(accB, impB.Data.Lineup.LineupId)!;
            Assert.Equal("S2T19NEW", lineupB.SeedText);
            Assert.Equal(keep.Data.DayNumbers, lineupB.Days.Keys.OrderBy(d => d).ToArray());

            // 5) B 再分享（第二代）→ C 用短码导入，天数和种子不变
            var expB = await svc.ExportAsync(accB, "Friend", new BlueprintExportRequest { Source = "lineup", LineupId = lineupB.LineupId });
            Assert.True(expB.Ok, $"{expB.ErrorCode} {expB.ErrorMessage}");
            var impC = await svc.ImportAsync(accC, new BlueprintImportRequest { Code = expB.Data!.ShortCode, Opponents = "lineup", Apply = true });
            Assert.True(impC.Ok, $"{impC.ErrorCode} {impC.ErrorMessage}");
            var lineupC = db.LoadGhostLineup(accC, impC.Data!.Lineup!.LineupId)!;
            Assert.Equal("S2T19NEW", lineupC.SeedText);
            Assert.Equal(keep.Data.DayNumbers, lineupC.Days.Keys.OrderBy(d => d).ToArray());

            // 6) 方案没配种子：退回用「下一局的种子」；两个都没有 → 409 中文提示
            var fullB = lineupStore.GetLineup(accB, lineupB.LineupId)!;
            var cleared = lineupStore.SaveLineup(accB, lineupB.LineupId, fullB.Name, fullB.Description, null, DaysJson(fullB), seedProvided: true, seedText: "");
            Assert.True(cleared.Ok, cleared.ErrorMessage);
            Assert.Null(cleared.Data!.SeedText);
            var expFallback = await svc.ExportAsync(accB, "Friend", new BlueprintExportRequest { Source = "lineup", LineupId = lineupB.LineupId });
            Assert.True(expFallback.Ok);
            Assert.Equal("S2T19NEW", expFallback.Data!.Summary.SeedText);

            var rulesNoSeed = ruleStore.Get(accB);
            rulesNoSeed.CustomSeedText = null;
            ruleStore.Set(accB, rulesNoSeed);
            var expNoSeed = await svc.ExportAsync(accB, "Friend", new BlueprintExportRequest { Source = "lineup", LineupId = lineupB.LineupId });
            Assert.False(expNoSeed.Ok);
            Assert.Equal(409, expNoSeed.HttpStatus);
            Assert.Equal("SEED_UNAVAILABLE", expNoSeed.ErrorCode);

            // 7) 不存在的方案 → 404
            var expMissing = await svc.ExportAsync(accB, "Friend", new BlueprintExportRequest { Source = "lineup", LineupId = "ln_not_exist" });
            Assert.Equal(404, expMissing.HttpStatus);
        }
        finally
        {
            SessionRegistry.Detach(sess.Sid);
            sessions.Remove(sess.Sid);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #12：BZR1 坏数据防护（超长/非gzip/解压炸弹/深层JSON/超基数/非法模板/频率属性篡改）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Test12_BZR1_MalformedAndMaliciousPayloads_NeverCrash_AndStripFrequencyTampering()
    {
        using var temp = new TempTestDir("w6_t12");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();

        var modsDir = Path.Combine(temp.Root, "mods");
        var stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var sessions = new SessionStore();
        var svc = new RunBlueprintService(
            db, ruleStore, sessions, modHost, communityMods, pool, monsters, _fx.ArchetypeIndex);

        // 1) 超长码串 (> 160 KB)
        string hugeCode = "BZR1:" + new string('A', RunBlueprintService.MaxOfflineCodeChars + 1024);
        Assert.False(svc.DecodeAndValidateCode(hugeCode, out _, out _, out _, out _, out int s1, out string c1, out _));
        Assert.Equal(400, s1);
        Assert.Equal("CODE_INVALID", c1);

        // 2) 非 gzip 魔数
        string nonGzip = "BZR1:" + Base64UrlEncode(Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"));
        Assert.False(svc.DecodeAndValidateCode(nonGzip, out _, out _, out _, out _, out int s2, out string c2, out _));
        Assert.Equal(400, s2);
        Assert.Equal("CODE_INVALID", c2);

        // 3) gzip 解压炸弹（全 0 字节 700 KB，压缩后仅数 KB，解压超 512 KB 上限）
        byte[] bombRaw = new byte[700 * 1024];
        string bombCode = "BZR1:" + Base64UrlEncode(CompressGzip(bombRaw));
        Assert.True(bombCode.Length < RunBlueprintService.MaxOfflineCodeChars, "炸弹码串本身应小于 160 KB 以验证流式解压守卫");
        Assert.False(svc.DecodeAndValidateCode(bombCode, out _, out _, out _, out _, out int s3, out string c3, out _));
        Assert.Equal(400, s3);
        Assert.Equal("CODE_INVALID", c3);

        // 4) 深层嵌套 JSON（深度 > 16）
        var deepSb = new StringBuilder();
        for (int i = 0; i < 25; i++) deepSb.Append("{\"a\":");
        deepSb.Append("1");
        for (int i = 0; i < 25; i++) deepSb.Append('}');
        string deepCode = EncodeBzr1RawJson(deepSb.ToString());
        Assert.False(svc.DecodeAndValidateCode(deepCode, out _, out _, out _, out _, out int s4, out string c4, out _));
        Assert.Equal(400, s4);
        Assert.Equal("CODE_INVALID", c4);

        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");

        // 5) 超基数 DayGhosts（31 天）与越界天号（Day 0 / Day 31）
        var tooManyDays = new Dictionary<int, PvpGhostPool.GhostBoard>();
        for (int d = 1; d <= 31; d++)
            tooManyDays[d] = MakeValidBoard("Vanessa", day: Math.Min(d, 30), healthMax: 300, displayName: null, itemTpl: itemTpl);
        string tooManyCode = EncodeBzr1Package(new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_and_ghosts",
            Hero = "Vanessa",
            MasterSeed = 123456,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(123456),
            DayGhosts = tooManyDays,
        });
        Assert.False(svc.DecodeAndValidateCode(tooManyCode, out _, out _, out _, out _, out int s5, out string c5, out _));
        Assert.Equal(400, s5);
        Assert.Equal("CODE_INVALID", c5);

        string outOfRangeDayCode = EncodeBzr1Package(new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_and_ghosts",
            Hero = "Vanessa",
            MasterSeed = 123456,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(123456),
            DayGhosts = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [0] = MakeValidBoard("Vanessa", day: 1, healthMax: 300, displayName: null, itemTpl: itemTpl),
            },
        });
        Assert.False(svc.DecodeAndValidateCode(outOfRangeDayCode, out _, out _, out _, out _, out int s5b, out string c5b, out _));
        Assert.Equal(400, s5b);
        Assert.Equal("CODE_INVALID", c5b);

        // 6) 非法模板 ID（未声明缺失 Mod 时必须被拦截为 400 CODE_INVALID）
        string illegalTplCode = EncodeBzr1Package(new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_and_ghosts",
            Hero = "Vanessa",
            MasterSeed = 246810,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(246810),
            DayGhosts = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [1] = new PvpGhostPool.GhostBoard
                {
                    Day = 1,
                    Hero = "Vanessa",
                    Level = 1,
                    HealthMax = 250,
                    Prestige = 20,
                    Items = new List<PvpGhostPool.GhostItem>
                    {
                        new() { Slot = 0, Width = 1, TemplateId = "illegal-template-id-999999", Tier = "Bronze" },
                    },
                },
            },
        });
        Assert.False(svc.DecodeAndValidateCode(illegalTplCode, out _, out _, out _, out _, out int s6, out string c6, out _));
        Assert.Equal(400, s6);
        Assert.Equal("CODE_INVALID", c6);

        // 7) 频率类属性篡改（CooldownMax / Multicast / AmmoMax / HasteAmount）→ 自动剥除 Add/Mult 并钳回基础值
        string baseTier = GhostBoardValidator.GetSupportedTiers(itemTpl)[0];
        var baseCard = new CardInstance(itemTpl, TierHelper.ParseTier(baseTier), null, "check_base");
        int expectedBaseCd = baseCard.BaseAttributes.TryGetValue("CooldownMax", out double cdVal) ? (int)Math.Round(cdVal) : -1;

        var tamperedBoard = new PvpGhostPool.GhostBoard
        {
            Day = 1,
            Hero = "Vanessa",
            Level = 2,
            HealthMax = 350,
            Prestige = 20,
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 0,
                    Width = 1,
                    TemplateId = itemTpl.Id,
                    Tier = baseTier,
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        ["DamageAmount"] = 88,
                        ["CooldownMax"] = 10,
                        ["Multicast"] = 99,
                        ["AmmoMax"] = 999,
                    },
                    PermanentAdd = new Dictionary<string, double>
                    {
                        ["DamageAmount"] = 25,
                        ["CooldownMax"] = -9999,
                        ["Multicast"] = 50,
                        ["AmmoMax"] = 99,
                        ["HasteAmount"] = 5000,
                    },
                    PermanentMult = new Dictionary<string, double>
                    {
                        ["CooldownMax"] = 0.01,
                        ["Multicast"] = 10.0,
                    },
                },
            },
        };

        // 直接过校验器：确认频率类篡改被剥除/钳回且产出非空中文 warnings（覆盖 #43 P1 #4）
        var directVal = GhostBoardValidator.ValidateAndNormalize(1, tamperedBoard, repo, pool);
        Assert.True(directVal.Ok, $"篡改频率属性应清洗通过而非报错：{string.Join("；", directVal.Errors)}");
        Assert.NotEmpty(directVal.Warnings);
        Assert.All(directVal.Warnings, w => Assert.False(string.IsNullOrWhiteSpace(w)));

        string tamperedCode = EncodeBzr1Package(new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_and_ghosts",
            Hero = "Vanessa",
            MasterSeed = 777888,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(777888),
            DayGhosts = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [1] = tamperedBoard,
            },
        });

        Assert.True(svc.DecodeAndValidateCode(
            tamperedCode,
            out _, out _, out var decodedPkg, out _, out _, out _, out string err7),
            $"含篡改频率属性的盘面应被安全清洗而非崩错：{err7}");
        Assert.NotNull(decodedPkg?.DayGhosts);
        var sanitizedItem = Assert.Single(decodedPkg!.DayGhosts![1].Items);

        // 多重触发 / 弹药 / 加速等频率类键必须被剥除；冷却现在允许手改（最低 1 秒，被动物品不能加冷却）
        if (sanitizedItem.PermanentAdd != null)
        {
            Assert.DoesNotContain("Multicast", sanitizedItem.PermanentAdd.Keys);
            Assert.DoesNotContain("AmmoMax", sanitizedItem.PermanentAdd.Keys);
            Assert.DoesNotContain("HasteAmount", sanitizedItem.PermanentAdd.Keys);
            if (expectedBaseCd <= 0)
                Assert.DoesNotContain("CooldownMax", sanitizedItem.PermanentAdd.Keys);
        }
        Assert.True(sanitizedItem.PermanentMult == null || !sanitizedItem.PermanentMult.ContainsKey("Multicast"),
            "篡改的多重触发 PermanentMult 必须被剥除");

        // 冷却被篡改成极小值时钳到最低 1 秒（引擎默认下限），绝不留 10 毫秒；被动物品不输出冷却
        if (expectedBaseCd > 0)
        {
            Assert.True(sanitizedItem.PermanentAttributes.TryGetValue("CooldownMax", out int outCd));
            Assert.Equal((int)GhostBoardValidator.MinEditableCooldownMs, outCd);
        }
        else
        {
            Assert.False(sanitizedItem.PermanentAttributes.ContainsKey("CooldownMax"));
        }
        Assert.False(sanitizedItem.PermanentAttributes.TryGetValue("Multicast", out int outMc) && outMc == 99);
        Assert.False(sanitizedItem.PermanentAttributes.TryGetValue("AmmoMax", out int outAmmo) && outAmmo == 999);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #18：冷却可以在编排台手改（最低 1 秒、幂等、被动物品不能加冷却）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Test18_CooldownEditable_MinOneSecond_Idempotent_PassiveIgnored()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        double BaseCd(CardTemplate t)
        {
            string tier = GhostBoardValidator.GetSupportedTiers(t)[0];
            var inst = new CardInstance(t, TierHelper.ParseTier(tier), null, "t18");
            return inst.BaseAttributes.TryGetValue("CooldownMax", out double v) && double.IsFinite(v) ? v : 0.0;
        }

        bool IsSmallValidItem(CardTemplate t) => t != null
            && string.Equals(t.Type, "Item", StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Size, "Small", StringComparison.OrdinalIgnoreCase)
            && pool.IsAssetValid(t.Id);

        var active = repo.CardsById.Values.Where(IsSmallValidItem).OrderBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault(t => BaseCd(t) > 1500);
        var passive = repo.CardsById.Values.Where(IsSmallValidItem).OrderBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault(t => BaseCd(t) <= 0);
        Assert.NotNull(active);
        Assert.NotNull(passive);
        string tierA = GhostBoardValidator.GetSupportedTiers(active!)[0];
        string tierP = GhostBoardValidator.GetSupportedTiers(passive!)[0];

        string BoardJson(int cdMs) =>
            "{\"day\":4,\"hero\":\"Vanessa\",\"level\":5,\"healthMax\":800,\"prestige\":20,\"items\":["
            + "{\"slot\":0,\"width\":1,\"templateId\":\"" + active!.Id + "\",\"tier\":\"" + tierA + "\",\"editedAttributes\":{\"CooldownMax\":" + cdMs + "}},"
            + "{\"slot\":1,\"width\":1,\"templateId\":\"" + passive!.Id + "\",\"tier\":\"" + tierP + "\",\"editedAttributes\":{\"CooldownMax\":2000}}"
            + "],\"skills\":[]}";

        // 1) 主动物品改成 3.5 秒；被动物品改冷却被忽略并给出中文提示
        using var doc1 = JsonDocument.Parse(BoardJson(3500));
        var r1 = GhostBoardValidator.ValidateAndNormalize(4, doc1.RootElement, repo, pool);
        Assert.True(r1.Ok, string.Join("；", r1.Errors));
        var a1 = r1.Board.Items.Single(i => i.TemplateId == active!.Id);
        Assert.Equal(3500, a1.PermanentAttributes["CooldownMax"]);
        var p1 = r1.Board.Items.Single(i => i.TemplateId == passive!.Id);
        Assert.False(p1.PermanentAttributes != null && p1.PermanentAttributes.ContainsKey("CooldownMax"));
        Assert.False(p1.PermanentAdd != null && p1.PermanentAdd.ContainsKey("CooldownMax"));
        Assert.Contains(r1.Warnings, w => w.Contains("被动物品", StringComparison.Ordinal));

        // 2) 原样再提交一次：结果不变（幂等）
        var r2 = GhostBoardValidator.ValidateAndNormalize(4, r1.Board, repo, pool);
        Assert.True(r2.Ok, string.Join("；", r2.Errors));
        Assert.Equal(3500, r2.Board.Items.Single(i => i.TemplateId == active!.Id).PermanentAttributes["CooldownMax"]);

        // 3) 改成 0.2 秒 → 钳到引擎下限 1 秒
        using var doc3 = JsonDocument.Parse(BoardJson(200));
        var r3 = GhostBoardValidator.ValidateAndNormalize(4, doc3.RootElement, repo, pool);
        Assert.True(r3.Ok, string.Join("；", r3.Errors));
        Assert.Equal((int)GhostBoardValidator.MinEditableCooldownMs, r3.Board.Items.Single(i => i.TemplateId == active!.Id).PermanentAttributes["CooldownMax"]);

        // 4) 目录：主动物品各品阶带冷却基础值，被动物品不带（前端据此决定显不显示冷却输入框）
        Assert.True(GhostBoardValidator.BuildBaseAttributesByTier(active!)[tierA].ContainsKey("CooldownMax"));
        Assert.False(GhostBoardValidator.BuildBaseAttributesByTier(passive!)[tierP].ContainsKey("CooldownMax"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #13：导入 Mod 四种情况（autoEnable含依赖 / missing含affectedDays / versionMismatch / serverExtra）+ 导入清理
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test13_BlueprintImport_FourModDiffScenarios_AndSafeImportCleanup()
    {
        using var temp = new TempTestDir("w6_t13");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();
        var itemTpl = PickValidDamageSmallItem(repo, pool, "Vanessa");

        string modsDir = Path.Combine(temp.Root, "mods");
        string stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        // 1) 在隔离 mods/ 目录放置一个默认启用的全局玩法 Mod（模拟“本服全局多开的玩法 Mod”）
        WriteClonedModDirectory(
            _fx.RepoRoot,
            Path.Combine(modsDir, "srv-extra-mod"),
            newModId: "srv-extra-mod",
            nameZh: "本服多开扩展",
            version: "1.0.0",
            enabledByDefault: true,
            dependencies: Array.Empty<string>());

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        modHost.BindPools(pool, new MonsterPoolStore(monsters, modHost.ActiveRepository));

        // 2) 上传社区库 Mod：
        //    - dep-base-mod (v1.0.0)：被 comm-auto-mod 依赖
        //    - comm-auto-mod (v1.0.0, depends on dep-base-mod)：本服有但玩家未勾选 → autoEnable（含依赖）
        //    - ver-mismatch-mod (v2.0.0)：本服已安装且玩家已勾选，但版本与蓝图 v1.0.0 不同 → versionMismatch
        var upDep = await communityMods.UploadZipAsync(
            BuildClonedModZip(_fx.RepoRoot, "dep-base-mod", "基础依赖包", "1.0.0", Array.Empty<string>()),
            "acc_author", "Author", isAdmin: false);
        Assert.True(upDep.Ok, $"上传 dep-base-mod 失败：{upDep.Error}");

        var upAuto = await communityMods.UploadZipAsync(
            BuildClonedModZip(_fx.RepoRoot, "comm-auto-mod", "自动启用扩展", "1.0.0", new[] { "dep-base-mod" }),
            "acc_author", "Author", isAdmin: false);
        Assert.True(upAuto.Ok, $"上传 comm-auto-mod 失败：{upAuto.Error}");

        var upMismatch = await communityMods.UploadZipAsync(
            BuildClonedModZip(_fx.RepoRoot, "ver-mismatch-mod", "版本差异包", "2.0.0", Array.Empty<string>()),
            "acc_author", "Author", isAdmin: false);
        Assert.True(upMismatch.Ok, $"上传 ver-mismatch-mod 失败：{upMismatch.Error}");

        var ruleStore = new PlayerRuleStore(db, modHost);
        var sessions = new SessionStore();
        var svc = new RunBlueprintService(
            db, ruleStore, sessions, modHost, communityMods, pool, monsters, _fx.ArchetypeIndex);

        string accountId = Guid.NewGuid().ToString("D");
        // 玩家当前只勾选了 ver-mismatch-mod（未勾选 comm-auto-mod / dep-base-mod）
        var playerRules = ruleStore.Get(accountId);
        playerRules.SelectedModIds = new List<string> { "ver-mismatch-mod" };
        ruleStore.Set(accountId, playerRules);

        // 构造蓝图包：
        // - ModEnvironment 含 comm-auto-mod(1.0.0)、ver-mismatch-mod(1.0.0)、ghost-missing-mod(1.2.3，本服不存在)
        // - Day 1：纯原版卡（正常保留）
        // - Day 2：原版卡 + 缺失 Mod 卡（导入时剔除缺失卡、保留原版卡）
        // - Day 3：仅缺失 Mod 卡（导入时整板变空、该天自动丢弃不崩）
        var autoPkg = communityMods.ResolvePackage("comm-auto-mod")!;
        string localDataVersion = PlannerCatalogConstants.ResolveGameDataVersion(modHost.ActiveRepository);
        var pkg = new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_and_ghosts",
            Title = "四类Mod差异蓝图",
            CreatorUsername = "OtherPlayer",
            CreatedAtUtc = DateTime.UtcNow,
            Hero = "Vanessa",
            MasterSeed = 31415926,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(31415926),
            IncludeRules = false,
            ModEnvironment = new ModEnvironmentSpec
            {
                GameDataVersion = localDataVersion,
                EffectiveModSetHash = "001122334455",
                Mods = new List<ModRequirementEntry>
                {
                    new()
                    {
                        ModId = "comm-auto-mod",
                        Name = "自动启用扩展",
                        Version = "1.0.0",
                        Author = "星港工匠",
                        PackageHash = autoPkg.PackageHash,
                        IsGameplay = true,
                    },
                    new()
                    {
                        ModId = "ver-mismatch-mod",
                        Name = "版本差异包",
                        Version = "1.0.0",
                        Author = "星港工匠",
                        PackageHash = "old_hash_v1",
                        IsGameplay = true,
                    },
                    new()
                    {
                        ModId = "ghost-missing-mod",
                        Name = "不存在的远古扩展",
                        Version = "1.2.3",
                        Author = "神秘作者",
                        PackageHash = "missing_hash",
                        IsGameplay = true,
                    },
                },
            },
            DayGhosts = new Dictionary<int, PvpGhostPool.GhostBoard>
            {
                [1] = MakeValidBoard("Vanessa", day: 1, healthMax: 260, displayName: "第一天正常", itemTpl: itemTpl),
                [2] = new PvpGhostPool.GhostBoard
                {
                    Day = 2,
                    Hero = "Vanessa",
                    Level = 2,
                    HealthMax = 400,
                    Prestige = 20,
                    DisplayName = "第二天混合卡",
                    Items = new List<PvpGhostPool.GhostItem>
                    {
                        new() { Slot = 0, Width = 1, TemplateId = itemTpl.Id, Tier = GhostBoardValidator.GetSupportedTiers(itemTpl)[0] },
                        new() { Slot = 1, Width = 1, TemplateId = "ghost-missing-mod:phantom_relic", Tier = "Bronze" },
                    },
                },
                [3] = new PvpGhostPool.GhostBoard
                {
                    Day = 3,
                    Hero = "Vanessa",
                    Level = 3,
                    HealthMax = 500,
                    Prestige = 20,
                    DisplayName = "第三天全缺失卡",
                    Items = new List<PvpGhostPool.GhostItem>
                    {
                        new() { Slot = 0, Width = 1, TemplateId = "ghost-missing-mod:phantom_relic", Tier = "Bronze" },
                    },
                },
            },
        };

        string offlineCode = EncodeBzr1Package(pkg);

        // A. 预览：核查四类 ModDiff 全部命中且 warnings 不重复 modDiff
        var previewRes = await svc.PreviewAsync(accountId, offlineCode);
        Assert.True(previewRes.Ok, $"预览失败：{previewRes.ErrorCode} {previewRes.ErrorMessage}");
        var preview = previewRes.Data!;

        Assert.Contains(preview.ModDiff.AutoEnable, m => string.Equals(m.ModId, "comm-auto-mod", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.ModDiff.AutoEnable, m => string.Equals(m.ModId, "dep-base-mod", StringComparison.OrdinalIgnoreCase));

        var missingEntry = Assert.Single(preview.ModDiff.Missing, m => string.Equals(m.ModId, "ghost-missing-mod", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(2, missingEntry.AffectedDays);
        Assert.Contains(3, missingEntry.AffectedDays);

        var mismatchEntry = Assert.Single(preview.ModDiff.VersionMismatch, m => string.Equals(m.ModId, "ver-mismatch-mod", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2.0.0", mismatchEntry.Version);
        Assert.Equal("1.0.0", mismatchEntry.ExpectedVersion);

        Assert.Contains(preview.ModDiff.ServerExtra, m => string.Equals(m.ModId, "srv-extra-mod", StringComparison.OrdinalIgnoreCase));

        // warnings 不应重复输出 modDiff 的缺失/版本不同/本服多开提示
        Assert.Empty(preview.Warnings);

        // B. 导入：合并 SelectedModIds（含依赖）、剔除缺失卡、丢弃空板 Day 3 且不崩
        var importRes = await svc.ImportAsync(accountId, new BlueprintImportRequest
        {
            Code = offlineCode,
            Opponents = "lineup",
            ApplyRules = false,
            EnableModIds = new List<string> { "comm-auto-mod" },
            Apply = true,
        });
        Assert.True(importRes.Ok, $"导入失败：{importRes.ErrorCode} {importRes.ErrorMessage}");
        var imported = importRes.Data!;

        Assert.NotNull(imported.Rules);
        Assert.Contains("comm-auto-mod", imported.Rules!.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dep-base-mod", imported.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ver-mismatch-mod", imported.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(EGhostOpponentMode.FixedLineup, imported.Rules.GhostOpponentMode);

        Assert.NotNull(imported.Lineup);
        Assert.Equal(new List<int> { 1, 2 }, imported.Lineup!.DayNumbers);
        Assert.DoesNotContain(3, imported.Lineup.DayNumbers);

        var savedInDb = db.LoadGhostLineup(accountId, imported.Lineup.LineupId);
        Assert.NotNull(savedInDb);
        Assert.True(savedInDb!.Days.ContainsKey(1));
        Assert.True(savedInDb.Days.ContainsKey(2));
        Assert.False(savedInDb.Days.ContainsKey(3));
        var d2Item = Assert.Single(savedInDb.Days[2].Items);
        Assert.Equal(itemTpl.Id, d2Item.TemplateId);

        // C. P1 #1 回归：applyRules=true 且用户未勾选某可选 Mod 时，保存后的 rules.SelectedModIds 绝不混入原局缺失 Mod 或未勾选 Mod，且 rulesPatch.RulesSnapshot.SelectedModIds 为空
        string accountId2 = Guid.NewGuid().ToString("D");
        var p2InitRules = ruleStore.Get(accountId2);
        p2InitRules.SelectedModIds = new List<string> { "ver-mismatch-mod" };
        ruleStore.Set(accountId2, p2InitRules);

        var pkgWithRules = new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = "events_only",
            Title = "带规则蓝图P1回归",
            CreatorUsername = "OtherPlayer",
            CreatedAtUtc = DateTime.UtcNow,
            Hero = "Vanessa",
            MasterSeed = 27182818,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(27182818),
            IncludeRules = true,
            RuleSetSnapshot = new CustomRunRuleSet
            {
                StartingGold = 25,
                SelectedModIds = new List<string> { "comm-auto-mod", "ghost-missing-mod", "srv-extra-mod" },
            },
            ModEnvironment = pkg.ModEnvironment,
            DayGhosts = null,
        };
        string codeWithRules = EncodeBzr1Package(pkgWithRules);

        var importRulesRes = await svc.ImportAsync(accountId2, new BlueprintImportRequest
        {
            Code = codeWithRules,
            Opponents = "random",
            ApplyRules = true,
            EnableModIds = new List<string>(), // 用户明确取消勾选 comm-auto-mod
            Apply = true,
        });
        Assert.True(importRulesRes.Ok, $"带规则导入失败：{importRulesRes.ErrorCode} {importRulesRes.ErrorMessage}");
        var importedRulesData = importRulesRes.Data!;
        Assert.NotNull(importedRulesData.Rules);
        Assert.Equal(25, importedRulesData.Rules!.StartingGold);
        Assert.Contains("ver-mismatch-mod", importedRulesData.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghost-missing-mod", importedRulesData.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("comm-auto-mod", importedRulesData.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("dep-base-mod", importedRulesData.Rules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(importedRulesData.RulesPatch.RulesSnapshot);
        Assert.Empty(importedRulesData.RulesPatch.RulesSnapshot!.SelectedModIds);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #14：搜种器（与 RunEngine 100% 逐卡一致、不可能条件、并发排队、取消与超时）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test14_SeedFinder_MatchesRunEngineExactly_ImpossibleCondition_QueueCancelAndTimeout()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();

        using var finder = new EventSeedFinderService(
            modHost: null,
            basePool: pool,
            baseMonsters: monsters,
            baseArchetypes: _fx.ArchetypeIndex,
            baseRepo: repo);

        // 1) 严格一致性：EventSeedFinderService.PreviewSeed 与反射驱动 RunEngine 跑 Day 1~3 H1~H5 100% 逐卡一致
        int[] testSeeds = [100001, 20261128];
        (string HeroName, EHero HeroEnum)[] testHeroes =
        [
            ("Vanessa", EHero.Vanessa),
            ("Pygmalien", EHero.Pygmalien),
        ];

        foreach (int seed in testSeeds)
        {
            string seedCode = DailyChallengeSeedGenerator.FormatSeedCode(seed);
            foreach (var (heroName, heroEnum) in testHeroes)
            {
                var previewRes = finder.PreviewSeed(new SeedPreviewRequest
                {
                    SeedText = seedCode,
                    Hero = heroName,
                    MaxDay = 3,
                    UseMyRules = false,
                });
                Assert.True(previewRes.Ok, $"PreviewSeed 失败：{previewRes.ErrorMessageZh}");
                Assert.Equal(seed, previewRes.Data!.MasterSeed);
                Assert.Equal(3, previewRes.Data.Calendar.Count);

                // 用真实 RunEngine 跑同一 (seed, hero) Day 1..3 H1..H5（不选任何事件）
                var run = new RunState
                {
                    RunId = $"run_t14_{heroName}_{seed}",
                    Hero = heroEnum,
                    PlayMode = EPlayMode.Unranked,
                    GameModeId = pool.DefaultGameModeId,
                    CustomRules = new CustomRunRuleSet
                    {
                        CustomSeedText = seedCode,
                    },
                };
                var engine = new RunEngine(
                    run, pool, monsters, _fx.LevelUps, _fx.Mode, repo, _fx.Ghosts,
                    archetypeIndex: _fx.ArchetypeIndex);
                engine.StartRun();

                for (int day = 1; day <= 3; day++)
                {
                    var previewDay = previewRes.Data.Calendar[day - 1];
                    Assert.Equal(day, previewDay.Day);
                    Assert.Equal(5, previewDay.Hours.Count);

                    for (uint hour = 1; hour <= 5; hour++)
                    {
                        if (!(day == 1 && hour == 1))
                        {
                            run.Hour = hour;
                            run.State = ERunState.Choice;
                            run.SelectionsMadeInCurrentContext = 0;
                            InvokePrivate(engine, "DealHourEncountersAndClearActive", new object?[] { null });
                        }

                        var engineIds = run.SelectionSet
                            .Where(instId => run.Cards.ContainsKey(instId))
                            .Select(instId => RunState.ResolveCoreTemplateId(run.Cards[instId]))
                            .ToList();
                        var previewIds = previewDay.Hours[(int)hour - 1].Options
                            .Select(o => o.TemplateId)
                            .ToList();

                        Assert.Equal(engineIds, previewIds);
                    }

                    if (day < 3)
                    {
                        run.ApplyNewDayEncounterBookkeeping();
                        run.Day++;
                    }
                }
            }
        }

        // 2) 不可能条件：不存在模板 & 超出合法天数窗 → 400 CONDITION_IMPOSSIBLE + 非空中文 reasonZh
        var catalogEvents = finder.ListCatalogEvents("Vanessa", null);
        Assert.NotEmpty(catalogEvents);

        var impossibleUnknown = finder.StartJob("acc_imp", new SeedFinderJobRequest
        {
            Hero = "Vanessa",
            MaxDay = 5,
            Conditions = new List<SeedFinderConditionRequest>
            {
                new() { TemplateId = "nonexistent-encounter-id-xyz", MinDay = 1, MaxDay = 3 },
            },
        });
        Assert.False(impossibleUnknown.Ok);
        Assert.Equal(400, impossibleUnknown.StatusCode);
        Assert.Equal("CONDITION_IMPOSSIBLE", impossibleUnknown.ErrorCode);
        Assert.NotNull(impossibleUnknown.Details);
        Assert.NotEmpty(impossibleUnknown.Details!);
        Assert.False(string.IsNullOrWhiteSpace(impossibleUnknown.Details![0].ReasonZh));

        // 若目录中存在有天数窗限制的事件（如 MinDay >= 2），验证请求落在合法窗外同样返回 CONDITION_IMPOSSIBLE
        var lateEvent = catalogEvents.FirstOrDefault(e => e.MinDay >= 3);
        if (lateEvent != null)
        {
            var impossibleWindow = finder.StartJob("acc_imp2", new SeedFinderJobRequest
            {
                Hero = "Vanessa",
                MaxDay = 5,
                Conditions = new List<SeedFinderConditionRequest>
                {
                    new() { TemplateId = lateEvent.TemplateId, MinDay = 1, MaxDay = lateEvent.MinDay - 1 },
                },
            });
            Assert.False(impossibleWindow.Ok);
            Assert.Equal(400, impossibleWindow.StatusCode);
            Assert.Equal("CONDITION_IMPOSSIBLE", impossibleWindow.ErrorCode);
            Assert.NotEmpty(impossibleWindow.Details!);
            Assert.False(string.IsNullOrWhiteSpace(impossibleWindow.Details![0].ReasonZh));
        }

        // 挑选一组罕见组合条件（5 个不同事件且窄天窗）确保后台任务不会瞬间扫满 20 命中，从而稳定观测排队、取消与超时
        var hardConditions = catalogEvents
            .Where(e => e.MinDay <= 5 && e.MaxDay >= e.MinDay)
            .Take(5)
            .Select(e => new SeedFinderConditionRequest
            {
                TemplateId = e.TemplateId,
                MinDay = e.MinDay,
                MaxDay = e.MinDay,
            })
            .ToList();
        Assert.NotEmpty(hardConditions);
        int hardMaxDay = Math.Clamp(hardConditions.Max(c => c.MaxDay), 1, 10);

        // 3) 并发排队（maxConcurrentJobs = 1）与主动取消（cancelled）
        using (var queueFinder = new EventSeedFinderService(
            modHost: null,
            basePool: pool,
            baseMonsters: monsters,
            baseArchetypes: _fx.ArchetypeIndex,
            baseRepo: repo,
            maxConcurrentJobs: 1,
            maxJobDuration: TimeSpan.FromSeconds(15),
            maxHits: 20))
        {
            var start1 = queueFinder.StartJob("acc_q1", new SeedFinderJobRequest
            {
                Hero = "Vanessa",
                MaxDay = hardMaxDay,
                StartSeed = 10000,
                Conditions = hardConditions,
            });
            Assert.True(start1.Ok, start1.ErrorMessageZh);
            string job1 = start1.JobId!;

            // 等待 job1 进入 running（或极罕见情况下已 done）
            await WaitUntilAsync(() =>
            {
                var st = queueFinder.GetJob("acc_q1", job1);
                return st != null && st.Status != "queued";
            }, TimeSpan.FromSeconds(3));

            var start2 = queueFinder.StartJob("acc_q2", new SeedFinderJobRequest
            {
                Hero = "Vanessa",
                MaxDay = hardMaxDay,
                StartSeed = 20000,
                Conditions = hardConditions,
            });
            Assert.True(start2.Ok, start2.ErrorMessageZh);
            string job2 = start2.JobId!;

            var st1Now = queueFinder.GetJob("acc_q1", job1);
            var st2Now = queueFinder.GetJob("acc_q2", job2);
            Assert.NotNull(st2Now);
            if (st1Now?.Status == "running")
            {
                Assert.Equal("queued", st2Now!.Status);
            }

            // 主动取消 job2 与 job1 → 均进入 cancelled
            Assert.True(queueFinder.CancelJob("acc_q2", job2));
            Assert.True(queueFinder.CancelJob("acc_q1", job1));

            bool bothCancelled = await WaitUntilAsync(() =>
                queueFinder.GetJob("acc_q2", job2)?.Status == "cancelled" &&
                queueFinder.GetJob("acc_q1", job1)?.Status == "cancelled",
                TimeSpan.FromSeconds(5));
            Assert.True(bothCancelled, "CancelJob 后任务应在 5 秒内进入 cancelled 状态");
        }

        // 4) 超时结束（done + 合法 nextSeed）
        using (var timeoutFinder = new EventSeedFinderService(
            modHost: null,
            basePool: pool,
            baseMonsters: monsters,
            baseArchetypes: _fx.ArchetypeIndex,
            baseRepo: repo,
            maxConcurrentJobs: 1,
            maxJobDuration: TimeSpan.FromMilliseconds(150),
            maxHits: 20))
        {
            const int startSeed = 55000;
            var startT = timeoutFinder.StartJob("acc_timeout", new SeedFinderJobRequest
            {
                Hero = "Vanessa",
                MaxDay = hardMaxDay,
                StartSeed = startSeed,
                Conditions = hardConditions,
            });
            Assert.True(startT.Ok, startT.ErrorMessageZh);
            string jobId = startT.JobId!;

            bool finished = await WaitUntilAsync(() =>
                timeoutFinder.GetJob("acc_timeout", jobId)?.Status == "done",
                TimeSpan.FromSeconds(8));
            Assert.True(finished, "短超时搜种任务应在限定时间内结束并进入 done 状态");

            var finalSt = timeoutFinder.GetJob("acc_timeout", jobId)!;
            Assert.Equal("done", finalSt.Status);
            Assert.True(finalSt.Scanned > 0, "超时结束前应至少扫描过 1 个候选种子");
            Assert.NotEqual(0, finalSt.NextSeed);
            Assert.NotEqual(startSeed, finalSt.NextSeed);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test #17：核心 API 契约端到端验证（鉴权、静态页与 CSP、catalog/cards?ids=、validate-board/lineups 幂等、全部 404 code）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test17_PlannerEndpoints_EndToEnd_Auth_StaticCsp_Catalog_IdempotentValidation_AndAll404Codes()
    {
        using var temp = new TempTestDir("w6_t17");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();

        string modsDir = Path.Combine(temp.Root, "mods");
        string stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var monsterStore = new MonsterPoolStore(monsters, modHost.ActiveRepository);
        modHost.BindPools(pool, monsterStore);
        var runtimeSnapshots = new ServerRuntimeSnapshotProvider(modHost, pool);

        var accounts = new AccountStore(db);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var gameSessions = new SessionStore();
        var plannerSessions = new PlannerSessionStore();
        var lineupStore = new GhostLineupStore(db, ruleStore, modHost.BaseRepository, pool, monsterStore, runtimeSnapshots, modHost, communityMods);
        var blueprintService = new RunBlueprintService(db, ruleStore, gameSessions, modHost, communityMods, pool, monsterStore, runtimeSnapshots);
        using var seedFinder = new EventSeedFinderService(modHost, pool, monsterStore, runtimeSnapshots);

        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(ruleStore);
            builder.Services.AddSingleton(gameSessions);
            builder.Services.AddSingleton(modHost);
            builder.Services.AddSingleton(communityMods);
            builder.Services.AddSingleton(pool);
            builder.Services.AddSingleton(monsterStore);
            builder.Services.AddSingleton(runtimeSnapshots);
            builder.Services.AddSingleton(_fx.Ghosts);
            builder.Services.AddSingleton(plannerSessions);
            builder.Services.AddSingleton(lineupStore);
            builder.Services.AddSingleton(blueprintService);
            builder.Services.AddSingleton(seedFinder);

            app = builder.Build();
            CustomRuleEndpoints.Map(app, ruleStore, accounts, () => new { }, gameSessions, db);
            app.MapPlannerEndpoints();
            await app.StartAsync();

            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            // 准备两个账号：无口令账号 & 带口令账号
            var noPassAcc = accounts.GetOrCreateByName("NoPassPlannerUser");
            var passLogin = accounts.LoginByNameWithSecret(
                "PassPlannerUser",
                "Correct#Pass99",
                allowAutoBindSecret: true);
            Assert.True(passLogin.Ok && passLogin.Account != null && passLogin.Tokens != null);
            var passAcc = passLogin.Account!;
            string gameBearer = passLogin.Tokens!.AccessToken;

            // ── 1) POST /api/planner/web-ticket（只认 Bearer）与 web-ticket/exchange 一次性消费 ──
            using (var ticketNoAuth = new HttpRequestMessage(HttpMethod.Post, "/api/planner/web-ticket"))
            {
                ticketNoAuth.Headers.TryAddWithoutValidation("X-Account-Id", passAcc.AccountId.ToString("D"));
                ticketNoAuth.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(ticketNoAuth);
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            string ticket;
            using (var ticketReq = new HttpRequestMessage(HttpMethod.Post, "/api/planner/web-ticket"))
            {
                ticketReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gameBearer);
                ticketReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(ticketReq);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                ticket = doc.RootElement.GetProperty("ticket").GetString() ?? "";
                string path = doc.RootElement.GetProperty("path").GetString() ?? "";
                Assert.False(string.IsNullOrWhiteSpace(ticket));
                Assert.StartsWith("/planner/#ticket=", path, StringComparison.Ordinal);
                Assert.Equal(120, doc.RootElement.GetProperty("expiresInSeconds").GetInt32());
            }

            string plnToken;
            using (var exResp1 = await http.PostAsJsonAsync("/api/planner/web-ticket/exchange", new { ticket }))
            {
                Assert.Equal(HttpStatusCode.OK, exResp1.StatusCode);
                using var doc = JsonDocument.Parse(await exResp1.Content.ReadAsStringAsync());
                plnToken = doc.RootElement.GetProperty("token").GetString() ?? "";
                Assert.StartsWith("pln_", plnToken, StringComparison.Ordinal);
            }

            // 二次消费同一票据必须返回 401 TICKET_INVALID
            using (var exResp2 = await http.PostAsJsonAsync("/api/planner/web-ticket/exchange", new { ticket }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, exResp2.StatusCode);
                using var doc = JsonDocument.Parse(await exResp2.Content.ReadAsStringAsync());
                Assert.Equal("TICKET_INVALID", doc.RootElement.GetProperty("code").GetString());
            }

            // ── 2) POST /api/planner/auth/login：无口令 403 / 错口令 401 / 正确口令 200 ──
            using (var noPassResp = await http.PostAsJsonAsync("/api/planner/auth/login", new { username = noPassAcc.Username, password = "any" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noPassResp.StatusCode);
                using var doc = JsonDocument.Parse(await noPassResp.Content.ReadAsStringAsync());
                Assert.Equal("PASSWORD_REQUIRED_FOR_WEB_LOGIN", doc.RootElement.GetProperty("code").GetString());
            }

            using (var badPassResp = await http.PostAsJsonAsync("/api/planner/auth/login", new { username = passAcc.Username, password = "WrongPassword!" }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, badPassResp.StatusCode);
                using var doc = JsonDocument.Parse(await badPassResp.Content.ReadAsStringAsync());
                Assert.Equal("BAD_CREDENTIALS", doc.RootElement.GetProperty("code").GetString());
            }

            string loginPlnToken;
            using (var okPassResp = await http.PostAsJsonAsync("/api/planner/auth/login", new { username = passAcc.Username, password = "Correct#Pass99" }))
            {
                Assert.Equal(HttpStatusCode.OK, okPassResp.StatusCode);
                using var doc = JsonDocument.Parse(await okPassResp.Content.ReadAsStringAsync());
                loginPlnToken = doc.RootElement.GetProperty("token").GetString() ?? "";
                Assert.StartsWith("pln_", loginPlnToken, StringComparison.Ordinal);
            }

            // ── 3) 仅带 X-Account-Id 访问 /api/planner/me/context 必须返回 401 AUTH_REQUIRED ──
            using (var spoofingReq = new HttpRequestMessage(HttpMethod.Get, "/api/planner/me/context"))
            {
                spoofingReq.Headers.TryAddWithoutValidation("X-Account-Id", passAcc.AccountId.ToString("D"));
                using var resp = await http.SendAsync(spoofingReq);
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal("AUTH_REQUIRED", doc.RootElement.GetProperty("code").GetString());
            }

            // ── 4) GET /planner 与 GET /planner/：200 HTML、{{ASSET_PREFIX}} 精确替换与 CSP 响应头 ──
            using (var pNoSlash = await http.GetAsync("/planner"))
            {
                Assert.Equal(HttpStatusCode.OK, pNoSlash.StatusCode);
                string html = await pNoSlash.Content.ReadAsStringAsync();
                Assert.DoesNotContain("{{ASSET_PREFIX}}", html, StringComparison.Ordinal);
                Assert.Contains("./planner/planner.js", html, StringComparison.Ordinal);
                Assert.Contains("./planner/planner.css", html, StringComparison.Ordinal);
                Assert.True(pNoSlash.Headers.TryGetValues("Content-Security-Policy", out var cspVals));
                string csp = string.Join("; ", cspVals);
                Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
                Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
                Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            }

            using (var pWithSlash = await http.GetAsync("/planner/"))
            {
                Assert.Equal(HttpStatusCode.OK, pWithSlash.StatusCode);
                string html = await pWithSlash.Content.ReadAsStringAsync();
                Assert.DoesNotContain("{{ASSET_PREFIX}}", html, StringComparison.Ordinal);
                Assert.Contains("./planner.js", html, StringComparison.Ordinal);
                Assert.Contains("./planner.css", html, StringComparison.Ordinal);
                Assert.True(pWithSlash.Headers.Contains("Content-Security-Policy"));
            }

            // ── 5) GET /api/planner/catalog/cards?ids=... 批量查询并返回 baseAttributesByTier ──
            var item1 = PickValidDamageSmallItem(repo, pool, "Vanessa");
            var item2 = repo.AllItems.First(c =>
                !string.Equals(c.Id, item1.Id, StringComparison.OrdinalIgnoreCase) &&
                pool.IsAssetValid(c.Id) &&
                !MonsterPool.IsBlockedDebugOrBlankArtSkill(c) &&
                c.CardSize == CardSize.Small);

            using (var catReq = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/planner/catalog/cards?ids={Uri.EscapeDataString(item1.Id)},{Uri.EscapeDataString(item2.Id)}"))
            {
                catReq.Headers.TryAddWithoutValidation("X-Planner-Session", loginPlnToken);
                using var resp = await http.SendAsync(catReq);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var items = doc.RootElement.GetProperty("items");
                Assert.Equal(2, items.GetArrayLength());

                var matchedItem1 = items.EnumerateArray()
                    .Single(el => string.Equals(el.GetProperty("templateId").GetString(), item1.Id, StringComparison.OrdinalIgnoreCase));
                var byTier = matchedItem1.GetProperty("baseAttributesByTier");
                Assert.Equal(JsonValueKind.Object, byTier.ValueKind);
                string firstTier = GhostBoardValidator.GetSupportedTiers(item1)[0];
                Assert.True(byTier.TryGetProperty(firstTier, out var tierObj));
                Assert.True(tierObj.TryGetProperty("DamageAmount", out var dmgEl) && dmgEl.GetInt32() > 0);
            }

            // ── 6) POST lineups/validate-board 与 POST lineups：editedAttributes 折算、更新 unlockedSlotsMask、100% 幂等 ──
            string item1Tier = GhostBoardValidator.GetSupportedTiers(item1)[0];
            var initialBoardObj = new
            {
                boardId = "",
                day = 1,
                hero = "Vanessa",
                level = 1,
                healthMax = 250,
                prestige = 20,
                wins = 0,
                losses = 0,
                unlockedSlotsMask = 0,
                displayName = "幂等校验对手",
                items = new[]
                {
                    new
                    {
                        slot = 0, // Day 1 默认居中 4 格（mask 120，槽位 3..6）；放槽位 0 必须自动解锁 bit 0
                        width = 1,
                        templateId = item1.Id,
                        tier = item1Tier,
                        editedAttributes = new Dictionary<string, int> { ["DamageAmount"] = 123 },
                    },
                },
                skills = Array.Empty<object>(),
                socketEffects = Array.Empty<object>(),
            };

            string validatedBoard1Json;
            int expectedMask;
            double expectedPermAddDmg;
            using (var valReq1 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                board = initialBoardObj,
                selectedModIds = Array.Empty<string>(),
            }))
            using (var valResp1 = await http.SendAsync(valReq1))
            {
                Assert.Equal(HttpStatusCode.OK, valResp1.StatusCode);
                using var doc = JsonDocument.Parse(await valResp1.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var boardEl = doc.RootElement.GetProperty("board");
                validatedBoard1Json = boardEl.GetRawText();

                expectedMask = boardEl.GetProperty("unlockedSlotsMask").GetInt32();
                Assert.Equal(1, expectedMask & 1);       // 自动包含槽位 0
                Assert.Equal(120, expectedMask & 120);   // 保留 Day 1 默认 4 格

                var outItem = boardEl.GetProperty("items")[0];
                Assert.False(outItem.TryGetProperty("editedAttributes", out _), "折算后输出盘面必须删除 editedAttributes");
                Assert.Equal(123, outItem.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                expectedPermAddDmg = outItem.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble();
            }

            // 二次原样提交到 validate-board：100% 幂等
            using (var boardDoc = JsonDocument.Parse(validatedBoard1Json))
            using (var valReq2 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                board = boardDoc.RootElement,
                selectedModIds = Array.Empty<string>(),
            }))
            using (var valResp2 = await http.SendAsync(valReq2))
            {
                Assert.Equal(HttpStatusCode.OK, valResp2.StatusCode);
                using var doc = JsonDocument.Parse(await valResp2.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var boardEl2 = doc.RootElement.GetProperty("board");
                Assert.Equal(expectedMask, boardEl2.GetProperty("unlockedSlotsMask").GetInt32());
                var outItem2 = boardEl2.GetProperty("items")[0];
                Assert.Equal(123, outItem2.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                Assert.Equal(expectedPermAddDmg, outItem2.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble(), precision: 6);
            }

            // POST /api/planner/lineups 保存（带 editedAttributes）并二次原样提交验证幂等
            string savedLineupId;
            string savedDay1Json;
            using (var saveReq1 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups", plnToken, new
            {
                lineupId = (string?)null,
                name = "契约端到端剧本",
                description = "含手改属性折算",
                selectedModIds = Array.Empty<string>(),
                days = new Dictionary<string, object> { ["1"] = initialBoardObj },
            }))
            using (var saveResp1 = await http.SendAsync(saveReq1))
            {
                Assert.Equal(HttpStatusCode.OK, saveResp1.StatusCode);
                using var doc = JsonDocument.Parse(await saveResp1.Content.ReadAsStringAsync());
                var lineupEl = doc.RootElement.GetProperty("lineup");
                savedLineupId = lineupEl.GetProperty("lineupId").GetString()!;
                var day1El = lineupEl.GetProperty("days").GetProperty("1");
                savedDay1Json = day1El.GetRawText();

                Assert.Equal(expectedMask, day1El.GetProperty("unlockedSlotsMask").GetInt32());
                var outItem = day1El.GetProperty("items")[0];
                Assert.False(outItem.TryGetProperty("editedAttributes", out _));
                Assert.Equal(123, outItem.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                Assert.Equal(expectedPermAddDmg, outItem.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble(), precision: 6);
            }

            using (var savedDay1Doc = JsonDocument.Parse(savedDay1Json))
            using (var saveReq2 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups", plnToken, new
            {
                lineupId = savedLineupId,
                name = "契约端到端剧本",
                description = "含手改属性折算",
                selectedModIds = Array.Empty<string>(),
                days = new Dictionary<string, JsonElement> { ["1"] = savedDay1Doc.RootElement },
            }))
            using (var saveResp2 = await http.SendAsync(saveReq2))
            {
                Assert.Equal(HttpStatusCode.OK, saveResp2.StatusCode);
                using var doc = JsonDocument.Parse(await saveResp2.Content.ReadAsStringAsync());
                var day1El2 = doc.RootElement.GetProperty("lineup").GetProperty("days").GetProperty("1");
                var outItem2 = day1El2.GetProperty("items")[0];
                Assert.Equal(123, outItem2.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                Assert.Equal(expectedPermAddDmg, outItem2.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble(), precision: 6);
            }

            // 6b) P1 #2 + P2 #1 回归：负 permanentAdd + 非 1 permanentMult 折算精确命中目标值且二次提交 100% 幂等；未编辑克隆卡保留合法非频率成长（如 Lifesteal）
            int item1BaseDmg = GhostBoardValidator.BuildBaseAttributesByTier(item1)[item1Tier]["DamageAmount"];
            string item2Tier = GhostBoardValidator.GetSupportedTiers(item2)[0];
            var negAddAndCloneBoard = new
            {
                boardId = "",
                day = 1,
                hero = "Vanessa",
                level = 1,
                healthMax = 250,
                prestige = 20,
                wins = 0,
                losses = 0,
                unlockedSlotsMask = 0,
                items = new object[]
                {
                    new
                    {
                        slot = 0,
                        width = 1,
                        templateId = item1.Id,
                        tier = item1Tier,
                        permanentAdd = new Dictionary<string, double> { ["DamageAmount"] = -(item1BaseDmg + 20) },
                        permanentMult = new Dictionary<string, double> { ["DamageAmount"] = 2.0 },
                        editedAttributes = new Dictionary<string, int> { ["DamageAmount"] = 50 },
                    },
                    new
                    {
                        slot = 1,
                        width = 1,
                        templateId = item2.Id,
                        tier = item2Tier,
                        permanentAdd = new Dictionary<string, double> { ["Lifesteal"] = 1.0 },
                    },
                },
                skills = Array.Empty<object>(),
                socketEffects = Array.Empty<object>(),
            };

            string negValidatedJson;
            double negFoldedAdd;
            using (var negReq1 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                board = negAddAndCloneBoard,
                selectedModIds = Array.Empty<string>(),
            }))
            using (var negResp1 = await http.SendAsync(negReq1))
            {
                Assert.Equal(HttpStatusCode.OK, negResp1.StatusCode);
                using var doc = JsonDocument.Parse(await negResp1.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var boardEl = doc.RootElement.GetProperty("board");
                negValidatedJson = boardEl.GetRawText();

                var outItem0 = boardEl.GetProperty("items")[0];
                Assert.False(outItem0.TryGetProperty("editedAttributes", out _));
                Assert.Equal(50, outItem0.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                negFoldedAdd = outItem0.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble();
                Assert.Equal(2.0, outItem0.GetProperty("permanentMult").GetProperty("DamageAmount").GetDouble(), precision: 6);

                var outItem1 = boardEl.GetProperty("items")[1];
                Assert.True(
                    outItem1.TryGetProperty("permanentAdd", out var clonedAddEl) &&
                    clonedAddEl.TryGetProperty("Lifesteal", out var lsEl) &&
                    Math.Abs(lsEl.GetDouble() - 1.0) < 1e-6,
                    "未编辑克隆卡的合法非频率永久成长（Lifesteal）必须原样保留");
            }

            // 二次原样提交负 Add + 非 1 Mult 折算后的盘面：100% 幂等
            using (var negDoc = JsonDocument.Parse(negValidatedJson))
            using (var negReq2 = CreateAuthedJsonRequest(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                board = negDoc.RootElement,
                selectedModIds = Array.Empty<string>(),
            }))
            using (var negResp2 = await http.SendAsync(negReq2))
            {
                Assert.Equal(HttpStatusCode.OK, negResp2.StatusCode);
                using var doc = JsonDocument.Parse(await negResp2.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var outItem0Again = doc.RootElement.GetProperty("board").GetProperty("items")[0];
                Assert.Equal(50, outItem0Again.GetProperty("permanentAttributes").GetProperty("DamageAmount").GetInt32());
                Assert.Equal(negFoldedAdd, outItem0Again.GetProperty("permanentAdd").GetProperty("DamageAmount").GetDouble(), precision: 6);
                Assert.Equal(2.0, outItem0Again.GetProperty("permanentMult").GetProperty("DamageAmount").GetDouble(), precision: 6);
            }

            // ── 7) 所有真实业务 404 必须返回带非空 code 的 JSON ──
            await Assert404WithCodeAsync(http, HttpMethod.Get, "/api/planner/ghost-pool/not-exist", plnToken, "NOT_FOUND");
            await Assert404WithCodeAsync(http, HttpMethod.Get, "/api/planner/lineups/not-exist", plnToken, "NOT_FOUND");
            await Assert404WithCodeAsync(http, HttpMethod.Delete, "/api/planner/lineups/not-exist", plnToken, "NOT_FOUND");
            await Assert404WithCodeAsync(http, HttpMethod.Post, "/api/planner/blueprints/preview", plnToken, "CODE_NOT_FOUND", new { code = "BZ-ZZZZ-ZZZZ" });
            await Assert404WithCodeAsync(http, HttpMethod.Delete, "/api/planner/blueprints/BZ-ZZZZ-ZZZZ", plnToken, "CODE_NOT_FOUND");
            await Assert404WithCodeAsync(http, HttpMethod.Get, "/api/planner/seed-finder/jobs/not-exist", plnToken, "NOT_FOUND");
            await Assert404WithCodeAsync(http, HttpMethod.Post, "/api/planner/blueprints/export", plnToken, "NO_RUN", new
            {
                source = "active",
                scope = "events_only",
                includeRules = false,
            });
            await Assert404WithCodeAsync(http, HttpMethod.Post, "/api/planner/lineups/capture-faced", plnToken, "NO_FACED_GHOST", new
            {
                runId = (string?)null,
                sourceDay = 1,
                lineupId = (string?)null,
                newLineupName = "收藏测试",
                targetDay = 1,
            });
        }
        finally
        {
            http?.Dispose();
            if (app != null) await app.DisposeAsync();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 私有测试辅助方法
    // ──────────────────────────────────────────────────────────────────────────

    private static CardTemplate PickValidDamageSmallItem(GameDataRepository repo, CardPool pool, string hero)
    {
        var candidate = repo.AllItems.FirstOrDefault(c =>
            c.CardSize == CardSize.Small &&
            pool.IsAssetValid(c.Id) &&
            !MonsterPool.IsBlockedDebugOrBlankArtSkill(c) &&
            (c.Heroes.Count == 0 || c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || c.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)) &&
            HasPositiveBaseDamage(c));
        if (candidate != null) return candidate;

        return repo.AllItems.First(c =>
            c.CardSize == CardSize.Small &&
            pool.IsAssetValid(c.Id) &&
            !MonsterPool.IsBlockedDebugOrBlankArtSkill(c) &&
            HasPositiveBaseDamage(c));
    }

    private static bool HasPositiveBaseDamage(CardTemplate tpl)
    {
        var byTier = GhostBoardValidator.BuildBaseAttributesByTier(tpl);
        foreach (var attrs in byTier.Values)
        {
            if (attrs.TryGetValue("DamageAmount", out int dmg) && dmg > 0)
                return true;
        }
        return false;
    }

    private static PvpGhostPool.GhostBoard MakeValidBoard(
        string hero,
        int day,
        int healthMax,
        string? displayName,
        CardTemplate itemTpl,
        string poolPartition = "standard",
        string boardId = "")
    {
        string tier = GhostBoardValidator.GetSupportedTiers(itemTpl)[0];
        return new PvpGhostPool.GhostBoard
        {
            BoardId = boardId,
            Day = day,
            SourceDay = day,
            TargetDay = day,
            Hero = hero,
            Level = Math.Clamp(day, 1, 20),
            HealthMax = healthMax,
            Prestige = 20,
            Wins = Math.Max(0, day - 1),
            Losses = 0,
            UnlockedSlotsMask = 1023,
            PowerBucket = "Normal",
            PoolPartition = poolPartition,
            DisplayName = displayName,
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 0,
                    Width = Math.Clamp(itemTpl.CardSize.SlotCount(), 1, 3),
                    TemplateId = itemTpl.Id,
                    InternalName = itemTpl.InternalName,
                    Tier = tier,
                },
            },
            Skills = new List<PvpGhostPool.GhostSkill>(),
        };
    }

    private static object? InvokePrivate(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static byte[] CompressGzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string EncodeBzr1RawJson(string json)
        => "BZR1:" + Base64UrlEncode(CompressGzip(Encoding.UTF8.GetBytes(json)));

    private static string EncodeBzr1Package(RunBlueprintPackage package)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(package);
        return "BZR1:" + Base64UrlEncode(CompressGzip(jsonBytes));
    }

    private static void WriteClonedModDirectory(
        string repoRoot,
        string targetDir,
        string newModId,
        string nameZh,
        string version,
        bool enabledByDefault,
        string[] dependencies)
    {
        string srcDir = Path.Combine(repoRoot, "mods", "clockwork-forge");
        Assert.True(Directory.Exists(srcDir), "缺少示例 Mod 目录 mods/clockwork-forge");
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
            if (rel.StartsWith(".", StringComparison.Ordinal)) continue;

            string dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (rel.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
            {
                string rewritten = RewriteModManifestJson(File.ReadAllText(file, Encoding.UTF8), newModId, nameZh, version, enabledByDefault, dependencies);
                File.WriteAllText(dest, rewritten, Encoding.UTF8);
            }
            else if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string text = File.ReadAllText(file, Encoding.UTF8)
                    .Replace("clockwork-forge", newModId, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(dest, text, Encoding.UTF8);
            }
            else
            {
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    private static byte[] BuildClonedModZip(
        string repoRoot,
        string newModId,
        string nameZh,
        string version,
        string[] dependencies)
    {
        string srcDir = Path.Combine(repoRoot, "mods", "clockwork-forge");
        Assert.True(Directory.Exists(srcDir), "缺少示例 Mod 目录 mods/clockwork-forge");

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;

                var entry = archive.CreateEntry($"{newModId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                if (rel.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    string rewritten = RewriteModManifestJson(File.ReadAllText(file, Encoding.UTF8), newModId, nameZh, version, enabledByDefault: false, dependencies);
                    byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
                    es.Write(bytes, 0, bytes.Length);
                }
                else if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8)
                        .Replace("clockwork-forge", newModId, StringComparison.OrdinalIgnoreCase);
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    es.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                }
            }
        }
        return ms.ToArray();
    }

    private static string RewriteModManifestJson(
        string rawJson,
        string newModId,
        string nameZh,
        string version,
        bool enabledByDefault,
        string[] dependencies)
    {
        string replaced = rawJson.Replace("clockwork-forge", newModId, StringComparison.OrdinalIgnoreCase);
        var node = System.Text.Json.Nodes.JsonNode.Parse(replaced)!.AsObject();
        node["id"] = newModId;
        node["name"] = nameZh;
        node["displayNameZh"] = nameZh;
        node["version"] = version;
        node["enabledByDefault"] = enabledByDefault;
        var depsArr = new System.Text.Json.Nodes.JsonArray();
        foreach (string dep in dependencies)
            depsArr.Add(System.Text.Json.Nodes.JsonValue.Create(dep));
        node["dependencies"] = depsArr;
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });
    }

    private static HttpRequestMessage CreateAuthedJsonRequest(
        HttpMethod method,
        string path,
        string plnToken,
        object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("X-Planner-Session", plnToken);
        if (body != null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }
        return req;
    }

    private static async Task Assert404WithCodeAsync(
        HttpClient http,
        HttpMethod method,
        string path,
        string plnToken,
        string expectedCode,
        object? body = null)
    {
        using var req = CreateAuthedJsonRequest(method, path, plnToken, body);
        using var resp = await http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.NotFound,
            $"{method} {path} 应返回 404，实际 {(int)resp.StatusCode}：{text}");
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("code", out var codeEl),
            $"{method} {path} 的 404 响应必须带 JSON code 字段：{text}");
        Assert.Equal(expectedCode, codeEl.GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private sealed class TempTestDir : IDisposable
    {
        public string Root { get; }

        public TempTestDir(string prefix)
        {
            Root = Path.Combine(Path.GetTempPath(), $"bazaar_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { SqliteConnection.ClearAllPools(); } catch { }
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }
}
