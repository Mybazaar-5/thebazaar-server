using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// 沙盒模式发放原版/全局 Mod/社区与单局 Mod 卡牌、ID/短 ID/中文名解析、
/// sandbox_sync 即时同步协议与 /api/mods/active-bindings 映射回归测试。
/// </summary>
public sealed class SandboxModGrantAndSyncTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string SampleCompassBladeId = "bazaar-chronicles-sample:starport_compass_blade";
    private const string ClockworkOverloadId = "clockwork-forge:z_sword_custom_d83alo";

    private sealed record BaseFixture(
        string RepoRoot,
        string DataDir,
        string ModsDir,
        string? OverlayPath,
        GameDataRepository BaseRepo,
        HashSet<string> Whitelist);

    private static readonly Lazy<BaseFixture> Shared = new(LoadBaseFixture);

    private static BaseFixture LoadBaseFixture()
    {
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Bazaar.sln")))
            {
                repoRoot = cur.FullName;
                break;
            }
            cur = cur.Parent;
        }
        Assert.False(string.IsNullOrWhiteSpace(repoRoot), "未定位到 Bazaar.sln 根目录");

        string dataDir = Path.Combine(repoRoot!, "dist", "data");
        string modsDir = Path.Combine(repoRoot!, "mods");
        string dbPath = Path.Combine(dataDir, "GameData.db");
        string? zhPath = SteamPathResolver.ResolveZhCnTranslationPath(dataDir);
        string? overlayPath = Path.Combine(dataDir, "translations", "custom_zh_overlay.json");
        if (!File.Exists(overlayPath)) overlayPath = null;

        var baseRepo = GameDatabaseLoader.Load(dbPath, zhPath);
        var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "valid_card_ids.json"), Encoding.UTF8)))
        {
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.GetProperty("valid_ids");
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                    wl.Add(it.GetString()!);
        }

        return new BaseFixture(repoRoot!, dataDir, modsDir, overlayPath, baseRepo, wl);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        public required string TempDir { get; init; }
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required AccountStore Accounts { get; init; }
        public required SessionStore Sessions { get; init; }
        public required ServerModHost ModHost { get; init; }
        public required CommunityModStore CommunityStore { get; init; }
        public required CardPool CardPool { get; init; }
        public required List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; }
        public required LevelUpTable LevelUps { get; init; }
        public required GameModeConfig GameMode { get; init; }
        public required PvpGhostPool Ghosts { get; init; }
        public required CardArchetypeIndex Archetypes { get; init; }
        public required ISandboxEngineBridge Sandbox { get; init; }
        private readonly List<string> _registeredSids = new();

        public (AccountStore.Account Account, string Bearer, Session Session, SessionRegistry.Handle Handle)
            CreateActiveRun(string username, CustomRunRuleSet rules, EHero hero = EHero.Vanessa)
        {
            var acc = Accounts.GetOrCreateByName(username, "game");
            string aid = acc.AccountId.ToString("N");
            var normalizedRules = rules.CloneNormalized();

            var runCtx = ModHost.ResolveRunModContext(normalizedRules.SelectedModIds, CardPool, Monsters, Archetypes);
            var run = new RunState
            {
                RunId = "sbx_" + Guid.NewGuid().ToString("N")[..10],
                AccountId = aid,
                Username = acc.Username,
                GameModeId = runCtx.Pool.DefaultGameModeId,
                Hero = hero,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 20261122,
                CustomRules = normalizedRules,
            };
            var engine = new RunEngine(
                run,
                runCtx.Pool,
                runCtx.Monsters,
                LevelUps,
                GameMode,
                runCtx.Repository,
                Ghosts,
                archetypeIndex: runCtx.ArchetypeIndex);
            engine.StartRun();
            engine.Handle(new CheatCommand { Args = ["skipstart"] });

            var session = Sessions.Create(aid, acc.Username, new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = hero });
            SessionRegistry.Attach(session.Sid, run, engine);
            var handle = SessionRegistry.Get(session.Sid)!;
            _registeredSids.Add(session.Sid);
            return (acc, acc.AccessToken, session, handle);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (string sid in _registeredSids)
                SessionRegistry.Detach(sid);
            try { Http.Dispose(); } catch { }
            try { await App.StopAsync(); } catch { }
            try { await App.DisposeAsync(); } catch { }
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
        }
    }

    private static async Task<TestHarness> StartHarnessAsync()
    {
        var fx = Shared.Value;
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_sbx_mod_" + Guid.NewGuid().ToString("N")[..10]);
        string stateDir = Path.Combine(tempDir, "state");
        Directory.CreateDirectory(stateDir);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = tempDir });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var db = new Persistence(Path.Combine(stateDir, "bazaar.db"));
        var accounts = new AccountStore(db);
        var playerRules = new PlayerRuleStore(db);
        var sessions = new SessionStore();
        var lobbies = new LobbyStore(db);
        var modHost = new ServerModHost(
            fx.BaseRepo,
            fx.DataDir,
            stateDir,
            fx.Whitelist,
            modsRootOverride: fx.ModsDir,
            zhOverlayPathOverride: fx.OverlayPath);
        var cardPool = CardPool.FromRepository(
            modHost.ActiveRepository,
            fx.DataDir,
            validWhitelistOverride: fx.Whitelist,
            visualBindings: modHost.VisualBindingsByCustomId);
        var monsters = MonsterPool.FromRepository(modHost.ActiveRepository);
        var levelUps = LevelUpTable.FromRepository(modHost.ActiveRepository);
        var gameMode = GameModeConfig.FromRepository(modHost.ActiveRepository);
        var ghosts = PvpGhostPool.Load(fx.DataDir);
        var archetypes = CardArchetypeIndex.Build(cardPool, modHost.ActiveRepository);
        modHost.BindPools(cardPool, monsters);
        var communityStore = new CommunityModStore(modHost, stateDir);

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(playerRules);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(modHost);
        builder.Services.AddSingleton(communityStore);
        builder.Services.AddSingleton<OverlayLocalizationStore>(_ => modHost.ActiveLocalization);
        builder.Services.AddSingleton<GameDataRepository>(_ => modHost.BaseRepository);
        builder.Services.AddSingleton(cardPool);
        builder.Services.AddSingleton(monsters);
        builder.Services.AddSingleton(levelUps);
        builder.Services.AddSingleton(gameMode);
        builder.Services.AddSingleton(ghosts);
        builder.Services.AddSingleton<ISandboxEngineBridge, SandboxEngineService>();
        MultiplayerEndpointModule.RegisterServices(builder.Services);

        var app = builder.Build();
        ModEndpoints.Map(app);
        CommunityModEndpoints.Map(app);
        MultiplayerEndpointModule.MapEndpoints(app);
        await app.StartAsync();

        string baseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

        return new TestHarness
        {
            TempDir = tempDir,
            App = app,
            Http = http,
            Accounts = accounts,
            Sessions = sessions,
            ModHost = modHost,
            CommunityStore = communityStore,
            CardPool = cardPool,
            Monsters = monsters,
            LevelUps = levelUps,
            GameMode = gameMode,
            Ghosts = ghosts,
            Archetypes = archetypes,
            Sandbox = app.Services.GetRequiredService<ISandboxEngineBridge>(),
        };
    }

    private static HttpRequestMessage AuthedJson(HttpMethod method, string path, string bearer, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body != null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task SandboxGrant_SupportsVanillaGlobalModAndRunSelectedMod_ByFullIdShortIdAndChineseName()
    {
        await using var h = await StartHarnessAsync();
        var fx = Shared.Value;

        // 1. 普通沙盒局（未勾选 clockwork-forge）：全局启用的 bazaar-chronicles-sample 与原版卡可直接按完整 ID / 短 ID / 中文名发放
        var sandboxDefaultRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
        var (acc1, _, _, handle1) = h.CreateActiveRun("sbx_player_1", sandboxDefaultRules);
        string aid1 = acc1.AccountId.ToString("N");

        // 原版卡（完整 GUID）
        var rVanilla = await h.Sandbox.GrantCardAsync(aid1, new SandboxGrantCardRequest(CutlassId, Section: "Hand", Tier: "Gold"), CancellationToken.None);
        Assert.True(rVanilla.Ok, rVanilla.MessageZh);

        // 全局 Mod 卡（完整 ID / 短 ID / 中文名）
        var rFull = await h.Sandbox.GrantCardAsync(aid1, new SandboxGrantCardRequest(SampleCompassBladeId, Section: "Hand", Tier: "Gold"), CancellationToken.None);
        Assert.True(rFull.Ok, rFull.MessageZh);

        var rShort = await h.Sandbox.GrantCardAsync(aid1, new SandboxGrantCardRequest("starport_compass_blade", Section: "Hand", Tier: "Silver"), CancellationToken.None);
        Assert.True(rShort.Ok, rShort.MessageZh);

        var rZh = await h.Sandbox.GrantCardAsync(aid1, new SandboxGrantCardRequest("星港罗盘短刃", Section: "Stash", Tier: "Diamond"), CancellationToken.None);
        Assert.True(rZh.Ok, rZh.MessageZh);

        // 未启用且未勾选的 clockwork-forge 卡：应给出明确的中文启用指引提示
        var rUnenabled = await h.Sandbox.GrantCardAsync(aid1, new SandboxGrantCardRequest("过载蓄能炉", Section: "Hand", Tier: "Gold"), CancellationToken.None);
        Assert.False(rUnenabled.Ok);
        Assert.Contains("clockwork-forge", rUnenabled.MessageZh, StringComparison.OrdinalIgnoreCase);

        // 2. 单局勾选了未默认启用 Mod（clockwork-forge）的沙盒局：完整 ID / 短 ID / 中文名全部成功发卡
        var sandboxWithModRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
        sandboxWithModRules.SelectedModIds = ["clockwork-forge"];
        var (acc2, _, _, handle2) = h.CreateActiveRun("sbx_player_2", sandboxWithModRules);
        string aid2 = acc2.AccountId.ToString("N");

        var rModFull = await h.Sandbox.GrantCardAsync(aid2, new SandboxGrantCardRequest(ClockworkOverloadId, Section: "Hand", Tier: "Gold"), CancellationToken.None);
        Assert.True(rModFull.Ok, rModFull.MessageZh);

        var rModShort = await h.Sandbox.GrantCardAsync(aid2, new SandboxGrantCardRequest("z_sword_custom_d83alo", Section: "Hand", Tier: "Silver"), CancellationToken.None);
        Assert.True(rModShort.Ok, rModShort.MessageZh);

        var rModZh = await h.Sandbox.GrantCardAsync(aid2, new SandboxGrantCardRequest("过载蓄能炉", Section: "Stash", Tier: "Diamond"), CancellationToken.None);
        Assert.True(rModZh.Ok, rModZh.MessageZh);

        var grantedModCards = handle2.Run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && string.Equals(c.CustomCardId, ClockworkOverloadId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(3, grantedModCards.Count);
        foreach (var card in grantedModCards)
        {
            Assert.NotEqual(Guid.Empty, card.TemplateId);
            Assert.Contains(card.TemplateId.ToString(), fx.Whitelist);
        }
    }

    [Fact]
    public async Task SandboxSyncCommand_ReturnsSpawnedEventsUpdatesBindingsAndSupportsFallbackAndStats()
    {
        await using var h = await StartHarnessAsync();
        var fx = Shared.Value;

        var rules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
        rules.SelectedModIds = ["clockwork-forge"];
        var (acc, bearer, _, handle) = h.CreateActiveRun("sbx_sync_player", rules);
        string aid = acc.AccountId.ToString("N");
        int revBefore = handle.Run.VisualBindingsRevision;

        // 1. 发一张单局 Mod 物品卡并验证 VisualBindingsRevision 递增
        var grantResp = await h.Http.SendAsync(AuthedJson(
            HttpMethod.Post,
            "/api/sandbox/grant-card",
            bearer,
            new { templateId = "过载蓄能炉", tier = "Gold", section = "Hand" }));
        Assert.Equal(HttpStatusCode.OK, grantResp.StatusCode);
        using var grantDoc = JsonDocument.Parse(await grantResp.Content.ReadAsStringAsync());
        Assert.True(grantDoc.RootElement.GetProperty("forceSyncRequired").GetBoolean());
        string newInstanceId = grantDoc.RootElement.GetProperty("data").GetProperty("highlightInstanceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(newInstanceId));
        Assert.True(handle.Run.VisualBindingsRevision > revBefore);

        // 2. 通过 /api/mods/active-bindings 查询新实例映射（应包含刚生成的 InstanceId -> clockwork-forge:...）
        var bindResp = await h.Http.SendAsync(AuthedJson(HttpMethod.Get, "/api/mods/active-bindings", bearer));
        Assert.Equal(HttpStatusCode.OK, bindResp.StatusCode);
        using var bindDoc = JsonDocument.Parse(await bindResp.Content.ReadAsStringAsync());
        Assert.True(bindDoc.RootElement.GetProperty("revision").GetInt32() > revBefore);
        var bindingsEl = bindDoc.RootElement.GetProperty("bindings");
        Assert.True(bindingsEl.TryGetProperty(newInstanceId, out var bindingObj), "active-bindings.bindings 应包含新发 Mod 卡的实例映射");
        Assert.Equal(ClockworkOverloadId, bindingObj.GetProperty("customId").GetString());
        Assert.True(bindDoc.RootElement.GetProperty("templateCatalog").TryGetProperty(ClockworkOverloadId, out _));

        // 3. 执行 sandbox_sync 并走 BuildCommandReply，验证返回 GameSimEventCardSpawned（且载体在白名单中）
        var syncCmd = new CheatCommand { Args = ["sandbox_sync"] };
        var syncOutcome = handle.Engine.Handle(syncCmd);
        Assert.Null(syncOutcome.Error);

        var reply = GameServerEndpoints.BuildCommandReply(
            syncCmd,
            handle.Run,
            syncOutcome,
            handle.Run.State,
            isOwnedSelectMove: false,
            isFirstAfterReestablish: false,
            out bool isPureBoardMove,
            out _);
        Assert.False(isPureBoardMove);

        var agg = Assert.IsType<NetMessageAggregate>(reply);
        var gameSimMsg = Assert.IsType<NetMessageGameSim>(agg.Messages[0]);
        var spawned = Assert.Single(gameSimMsg.Data.Events.OfType<GameSimEventCardSpawned>(), e => e.InstanceId == newInstanceId);
        Assert.Contains(spawned.TemplateId, fx.Whitelist);
        Assert.Empty(handle.Run.PendingExternalSyncEvents);

        // 4. 发一张技能卡并验证同时产出 GameSimEventCardSpawned + GameSimEventPlayerSkillEquipped
        var anySkill = handle.Engine.ActiveRepository.CardsById.Values.First(c => string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase));
        var skillGrant = await h.Sandbox.GrantCardAsync(aid, new SandboxGrantCardRequest(anySkill.Id, Tier: "Gold"), CancellationToken.None);
        Assert.True(skillGrant.Ok, skillGrant.MessageZh);
        var skillSyncOutcome = handle.Engine.Handle(new CheatCommand { Args = ["sandbox_sync"] });
        Assert.Null(skillSyncOutcome.Error);
        Assert.Contains(skillSyncOutcome.Events, e => e is GameSimEventCardSpawned s && s.Type == ECardType.Skill);
        Assert.Contains(skillSyncOutcome.Events, e => e is GameSimEventPlayerSkillEquipped);

        // 5. 调数值后 sandbox_sync 必须附带 GameStateSync 且严格位于 GameSim 之后（遵守 P1-26）
        var statsRes = await h.Sandbox.SetStatsAsync(aid, new SandboxSetStatsRequest(Gold: 888, HealthMax: 2500, UnlockAllSlots: true), CancellationToken.None);
        Assert.True(statsRes.Ok, statsRes.MessageZh);
        var statsOutcome = handle.Engine.Handle(new CheatCommand { Args = ["sandbox_sync"] });
        Assert.True(statsOutcome.ForceStateSync);
        var statsReply = Assert.IsType<NetMessageAggregate>(GameServerEndpoints.BuildCommandReply(
            new CheatCommand { Args = ["sandbox_sync"] },
            handle.Run,
            statsOutcome,
            handle.Run.State,
            false,
            false,
            out _,
            out _));
        Assert.Equal(2, statsReply.Messages.Count);
        Assert.IsType<NetMessageGameSim>(statsReply.Messages[0]);
        Assert.IsType<NetMessageGameStateSync>(statsReply.Messages[1]);

        // 6. 移除卡牌后产生 GameSimEventCardDisposed
        var removeRes = await h.Sandbox.ModifyCardAsync(aid, new SandboxModifyCardRequest(newInstanceId, RemoveCard: true), CancellationToken.None);
        Assert.True(removeRes.Ok, removeRes.MessageZh);
        var removeOutcome = handle.Engine.Handle(new CheatCommand { Args = ["sandbox_sync"] });
        Assert.Contains(removeOutcome.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == newInstanceId);

        // 7. 兜底验证：未调 sandbox_sync 时，挂起的发卡事件随下一条 MoveItem 命令一起合并下发且不被 BuildDeltaGameSim 裁剪
        var g2 = await h.Sandbox.GrantCardAsync(aid, new SandboxGrantCardRequest("starport_compass_blade", Section: "Hand", Tier: "Gold"), CancellationToken.None);
        Assert.True(g2.Ok, g2.MessageZh);
        Assert.NotEmpty(handle.Run.PendingExternalSyncEvents);
        var fakeMoveOutcome = new RunEngine.Outcome();
        bool extForce = false;
        Assert.True(handle.Run.ConsumePendingExternalSync(fakeMoveOutcome.Events, ref extForce));
        var fallbackReply = GameServerEndpoints.BuildCommandReply(
            new MoveItemCommand(),
            handle.Run,
            fakeMoveOutcome,
            handle.Run.State,
            isOwnedSelectMove: false,
            isFirstAfterReestablish: false,
            out bool fallbackPureMove,
            out _);
        Assert.False(fallbackPureMove);
        var fallbackAgg = Assert.IsType<NetMessageAggregate>(fallbackReply);
        var fallbackSim = Assert.IsType<NetMessageGameSim>(fallbackAgg.Messages[0]);
        Assert.Contains(fallbackSim.Data.Events, e => e is GameSimEventCardSpawned);
    }

    [Fact]
    public async Task NonSandboxRun_RejectsSandboxEndpointsAndSandboxSync_AndPreservesIsolation()
    {
        await using var h = await StartHarnessAsync();

        // 1. 常规标准局调用 /api/sandbox/grant-card 必须返回 409 SANDBOX_ONLY，调用 sandbox_sync 必须拒绝
        var standardRules = new CustomRunRuleSet();
        var (_, stdBearer, _, stdHandle) = h.CreateActiveRun("std_player", standardRules);

        var blockedHttp = await h.Http.SendAsync(AuthedJson(
            HttpMethod.Post,
            "/api/sandbox/grant-card",
            stdBearer,
            new { templateId = CutlassId, tier = "Gold", section = "Hand" }));
        Assert.Equal(HttpStatusCode.Conflict, blockedHttp.StatusCode);

        var blockedSync = stdHandle.Engine.Handle(new CheatCommand { Args = ["sandbox_sync"] });
        Assert.NotNull(blockedSync.Error);

        // 2. 沙盒局三禁隔离不变量严丝合缝
        var sandboxRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
        sandboxRules.SelectedModIds = ["clockwork-forge"];
        var normalized = sandboxRules.CloneNormalized();
        Assert.True(normalized.ShouldSkipGhostPool);
        Assert.True(normalized.ShouldSkipCareerAndLeaderboard);
        Assert.Equal("none", normalized.ResolveGhostPoolPartition());
    }
}
