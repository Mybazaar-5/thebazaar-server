using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12.1 专项测试：
///   1) MonsterLootChooseOne 与 PromptTendencyBeforeRun 默认生效，且默认规则不脱离标准 PvP 镜像池；
///   2) 全模式（普通 / 排位 / 锦标赛）同一套进局 Day 1 Hour 1 唯一一次确认锁定流派倾向；
///   3) 一旦锁定或进入首个事件/后续小时，服务端严禁途中二次改倾向；
///   4) RunSnapshotSerializer 无损持久化 TendencyLocked，锦标赛房主不覆盖成员个人倾向；
///   5) HTTP 端到端验证 active-run-tendency / lock-opening-tendency (200 -> 409) / tendency。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class V12_1_UnifiedTendencyAndLootDefaultsTests
{
    private static readonly Lazy<TestFixture> Shared = new(() => new TestFixture());

    private sealed class TestFixture
    {
        public GameDataRepository Repo { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public PvpGhostPool Ghosts { get; }
        public CardArchetypeIndex ArchetypeIndex { get; }

        public TestFixture()
        {
            var shared = TestInfrastructure.SharedGameDataFixture.Shared;
            Repo = shared.Repo;
            Pool = shared.Pool;
            Monsters = shared.CreateMonstersCopy();
            LevelUps = shared.LevelUps;
            Mode = shared.Mode;
            Ghosts = new PvpGhostPool();
            ArchetypeIndex = shared.ArchetypeIndex;
        }
    }

    private static string ResolveDataPath(string name)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(name);

    private static string NewTempDb(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bz_v12_1_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    [Fact]
    public void Defaults_MonsterLootChooseOneAndPromptTendency_ShouldBeEnabled_WithoutCustomPowerIsolation()
    {
        var def = new CustomRunRuleSet();
        Assert.True(def.MonsterLootChooseOne);
        Assert.True(def.PromptTendencyBeforeRun);
        Assert.True(def.AllowTendencySelection);
        Assert.False(def.IsCustomPowerModified());

        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            var acc = new AccountStore(db).GetOrCreateByName("v12_1_def_user");
            var store = new PlayerRuleStore(db);
            var loaded = store.Get(acc.AccountId);
            Assert.True(loaded.MonsterLootChooseOne);
            Assert.True(loaded.PromptTendencyBeforeRun);
            Assert.False(loaded.IsCustomPowerModified());

            store.Set(acc.AccountId, new CustomRunRuleSet { MonsterLootChooseOne = false, PromptTendencyBeforeRun = false });
            Assert.False(store.Get(acc.AccountId).MonsterLootChooseOne);
            var reset = store.Reset(acc.AccountId);
            Assert.True(reset.MonsterLootChooseOne);
            Assert.True(reset.PromptTendencyBeforeRun);
        }
        finally { CleanupDir(dir); }
    }

    [Theory]
    [InlineData(EPlayMode.Unranked)]
    [InlineData(EPlayMode.Ranked)]
    [InlineData(EPlayMode.Tournament)]
    public void UnifiedTendencyLock_AllModes_AllowOnceAtOpeningAndForbidMidRunChanges(EPlayMode mode)
    {
        var fx = Shared.Value;
        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var host = accounts.GetOrCreateByName("host_" + mode);
            var guest = accounts.GetOrCreateByName("guest_" + mode);
            var ruleStore = new PlayerRuleStore(db);
            var lobbies = new LobbyStore();

            Guid? tid = null;
            if (mode == EPlayMode.Tournament)
            {
                // 房主预设 Burn，非房主预设 Poison；建房后房主个人倾向不写入房间公共规则
                ruleStore.Set(host.AccountId, new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Burn });
                ruleStore.Set(guest.AccountId, new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Poison });
                var lobby = lobbies.Create(host, "V121Room", "Tournament", 8, false, new CustomRunRuleSet
                {
                    SelectedArchetypeMask = (ulong)ECardArchetype.Weapon,
                    AllowTendencySelection = true,
                });
                Assert.Equal(0UL, lobby.Rules.SelectedArchetypeMask);
                lobbies.Join(lobby.Code, guest);
                lobbies.StartCountdown(lobby.LobbyId, Guid.NewGuid());
                tid = lobby.TournamentSessionId;
            }

            var run = new RunState
            {
                RunId = "run_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = mode,
                AccountId = guest.AccountId.ToString("N"),
                TournamentSessionId = tid,
            };

            var engine = new RunEngine(
                run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                fx.ArchetypeIndex, ruleStore, lobbies, sessions: null);
            engine.StartRun();

            // 开局 Day 1 Hour 1 初始态：所有模式一致允许锁定一次
            Assert.False(run.TendencyLocked);
            Assert.True(engine.CanLockOpeningTendency);

            ulong requestedMask = (ulong)(ECardArchetype.Burn | ECardArchetype.Shield | ECardArchetype.Control | ECardArchetype.Damage);
            Assert.True(engine.TryLockOpeningTendency(requestedMask, out ulong lockedMask, out string? err));
            Assert.Null(err);
            Assert.True(run.TendencyLocked);
            Assert.False(engine.CanLockOpeningTendency);
            Assert.Equal(3, CardArchetypeCatalog.CountBits(lockedMask));
            Assert.Equal(lockedMask, run.CustomRules.SelectedArchetypeMask);
            Assert.Equal(lockedMask, engine.Handle(new CheatCommand { Args = ["nop"] }).Error != null ? lockedMask : run.CustomRules.SelectedArchetypeMask);

            // 二次修改严格拒绝
            Assert.False(engine.TryLockOpeningTendency((ulong)ECardArchetype.Poison, out ulong rejectedMask, out string? rejectErr));
            Assert.Equal(lockedMask, rejectedMask);
            Assert.False(string.IsNullOrWhiteSpace(rejectErr));
            Assert.Equal(lockedMask, run.CustomRules.SelectedArchetypeMask);

            // 快照序列化/恢复后 TendencyLocked 严密保留
            string snapJson = RunSnapshotSerializer.Serialize(run, engine.StartSelectionDone, run.PendingLevelUp);
            Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, fx.Repo, out var restoredRun, out _));
            Assert.NotNull(restoredRun);
            Assert.True(restoredRun!.TendencyLocked);
            Assert.Equal(lockedMask, restoredRun.CustomRules.SelectedArchetypeMask);
            var resumedEngine = new RunEngine(
                restoredRun, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts,
                fx.ArchetypeIndex, ruleStore, lobbies, sessions: null);
            Assert.False(resumedEngine.CanLockOpeningTendency);
            Assert.False(resumedEngine.TryLockOpeningTendency((ulong)ECardArchetype.Tech, out _, out _));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void LeavingOpeningChoiceOrDisallowingTendency_AutoLocksAndBlocksMidRunModification()
    {
        var fx = Shared.Value;
        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var user = accounts.GetOrCreateByName("advance_lock_user");
            var ruleStore = new PlayerRuleStore(db);
            var lobbies = new LobbyStore();

            // Case 1: 进入首小时第一个遭遇后，自动永久锁死
            var run = new RunState
            {
                RunId = "run_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                AccountId = user.AccountId.ToString("N"),
            };
            var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.ArchetypeIndex, ruleStore, lobbies, null);
            engine.StartRun();
            Assert.True(engine.CanLockOpeningTendency);

            string firstEncId = run.SelectionSet.First();
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(firstEncId) }).Error);
            Assert.True(run.TendencyLocked);
            Assert.False(engine.CanLockOpeningTendency);
            Assert.False(engine.TryLockOpeningTendency((ulong)ECardArchetype.Poison, out _, out _));

            // Case 2: 锦标赛房主关闭自选倾向时，开局即自动锁死且归零
            var host = accounts.GetOrCreateByName("deny_host");
            ruleStore.Set(user.AccountId, new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Burn });
            var denyLobby = lobbies.Create(host, "DenyRoom", "Tournament", 8, false, new CustomRunRuleSet { AllowTendencySelection = false });
            lobbies.Join(denyLobby.Code, user);
            lobbies.StartCountdown(denyLobby.LobbyId, Guid.NewGuid());

            var denyRun = new RunState
            {
                RunId = "run_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Dooley,
                PlayMode = EPlayMode.Tournament,
                AccountId = user.AccountId.ToString("N"),
                TournamentSessionId = denyLobby.TournamentSessionId,
            };
            var denyEngine = new RunEngine(denyRun, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.ArchetypeIndex, ruleStore, lobbies, null);
            denyEngine.StartRun();
            Assert.True(denyRun.TendencyLocked);
            Assert.Equal(0UL, denyRun.CustomRules.SelectedArchetypeMask);
            Assert.False(denyEngine.CanLockOpeningTendency);
            Assert.False(denyEngine.TryLockOpeningTendency((ulong)ECardArchetype.Shield, out ulong applied, out _));
            Assert.Equal(0UL, applied);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public async Task CustomRuleEndpoints_ActiveRunLockOnce_Returns200Then409_AndDoesNotTouchMidRunOnPresetPut()
    {
        var fx = Shared.Value;
        string dbPath = NewTempDb(out string dir);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        string? attachedSid = null;
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var alice = accounts.GetOrCreateByName("v121_http_alice");
            var ruleStore = new PlayerRuleStore(db);
            var sessions = new SessionStore();

            CustomRuleEndpoints.Map(
                app,
                ruleStore,
                accounts,
                () => fx.ArchetypeIndex.ToCatalogDto(fx.Pool, fx.Repo),
                sessions,
                db);
            await app.StartAsync();

            var run = new RunState
            {
                RunId = "run_http_121",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                AccountId = alice.AccountId.ToString("N"),
            };
            var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.ArchetypeIndex, ruleStore, null, sessions);
            engine.StartRun();

            var session = sessions.Create(alice.AccountId.ToString("N"), alice.Username, new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
            });
            run = new RunState
            {
                RunId = session.RunId,
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Tournament,
                AccountId = alice.AccountId.ToString("N"),
            };
            engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, fx.ArchetypeIndex, ruleStore, null, sessions);
            engine.StartRun();
            attachedSid = session.Sid;
            SessionRegistry.Attach(session.Sid, run, engine);

            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + alice.AccessToken);

            // 1. 查询开场状态 -> 可锁定一次
            var activeJson = await http.GetFromJsonAsync<JsonElement>("/custom-rules/me/active-run-tendency");
            Assert.True(activeJson.GetProperty("hasActiveRun").GetBoolean());
            Assert.True(activeJson.GetProperty("canLockNow").GetBoolean());
            Assert.False(activeJson.GetProperty("tendencyLocked").GetBoolean());

            // 2. 锁定一次 -> 200 OK
            ulong chosen = (ulong)(ECardArchetype.Weapon | ECardArchetype.Burn);
            using var lockResp1 = await http.PostAsync(
                "/custom-rules/me/lock-opening-tendency",
                new StringContent(JsonSerializer.Serialize(new { selectedArchetypeMask = chosen, runId = run.RunId }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, lockResp1.StatusCode);
            var lockJson1 = await lockResp1.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(lockJson1.GetProperty("locked").GetBoolean());
            Assert.Equal(chosen, lockJson1.GetProperty("selectedArchetypeMask").GetUInt64());
            Assert.Equal(chosen, run.CustomRules.SelectedArchetypeMask);
            Assert.True(run.TendencyLocked);

            // 3. 再次修改 -> 409 Conflict 拒绝
            using var lockResp2 = await http.PostAsync(
                "/custom-rules/me/lock-opening-tendency",
                new StringContent(JsonSerializer.Serialize(new { selectedArchetypeMask = (ulong)ECardArchetype.Poison, runId = run.RunId }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, lockResp2.StatusCode);
            Assert.Equal(chosen, run.CustomRules.SelectedArchetypeMask);

            // 4. PUT /custom-rules/me/tendency 仅更新下局预设，绝不改变进行中对局的锁定倾向
            ulong nextPreset = (ulong)ECardArchetype.Control;
            using var tendResp = await http.PutAsync(
                "/custom-rules/me/tendency",
                new StringContent(JsonSerializer.Serialize(new { selectedArchetypeMask = nextPreset }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, tendResp.StatusCode);
            Assert.Equal(nextPreset, ruleStore.Get(alice.AccountId).SelectedArchetypeMask);
            Assert.Equal(chosen, run.CustomRules.SelectedArchetypeMask);
        }
        finally
        {
            if (attachedSid != null) SessionRegistry.Detach(attachedSid);
            await app.StopAsync();
            CleanupDir(dir);
        }
    }
}
