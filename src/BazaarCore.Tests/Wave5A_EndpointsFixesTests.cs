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
using BazaarGameShared.Infra.Messages;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5A（服务端端点专项修复）回归单元测试：
/// - P0-01（RC-PEDESTAL-STRICT-HANDSHAKE / RC-PEDESTAL-PRESENTATION-ORPHAN）：
///   活跃 ERunState.Pedestal 与 CommitToPedestalCommand 拒单绝不附发重型 NetMessageGameStateSync，
///   同时保证非祭坛交互态真正失步时的兜底同步机制不受破坏；
/// - P1-15（RC-HUD-COLD-RESTART-RULES-NEVER-FETCHED）：
///   GET /custom-rules/me/active-run-tendency（及只读 GET /lock-opening-tendency）向后兼容返回活跃局权威 customRules，
///   且 CustomRuleEndpoints.ResolveAccountId 完整支持 UTF-8 URL 编码与 Base64 编码非 ASCII 中文账号名（如「破风」）不误回退 guest。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave5A_EndpointsFixesTests
{
    private static readonly Lazy<TestFixture> Shared = new(() => new TestFixture());

    private sealed class TestFixture
    {
        public GameDataRepository Repo { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public CardArchetypeIndex ArchetypeIndex { get; }

        public TestFixture()
        {
            var shared = TestInfrastructure.SharedGameDataFixture.Shared;
            Repo = shared.Repo;
            Pool = shared.Pool;
            Monsters = shared.CreateMonstersCopy();
            LevelUps = shared.LevelUps;
            Mode = shared.Mode;
            ArchetypeIndex = shared.ArchetypeIndex;
        }
    }

    private static string NewTempDb(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bz_w5a_ep_" + Guid.NewGuid().ToString("N")[..10]);
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

    private static RunState CreateMinimalRun(ERunState state)
    {
        var fx = Shared.Value;
        var run = new RunState
        {
            RunId = "w5a_ep_" + Guid.NewGuid().ToString("N")[..10],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Unranked,
            Day = 3,
            Hour = 4,
            State = state,
            UnlockedSlots = 0b00_0111_1000,
            CustomRules = new CustomRunRuleSet().CloneNormalized(),
            RngSeed = 20261124,
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 20;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 400;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 400;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 2;
        return run;
    }

    // ── P0-01：活跃 Pedestal 态与 CommitToPedestalCommand 拒单禁附 GameStateSync ──

    [Fact]
    public void P0_01_PedestalState_AnyCommandRejection_NeverAttachesGameStateSync()
    {
        var run = CreateMinimalRun(ERunState.Pedestal);
        var errOutcome = new RunEngine.Outcome
        {
            Error = new NetMessageError("ped_reject", EResponseErrorType.NotAllowed, "该卡牌不满足当前祭坛的选择条件"),
        };

        INetCommand[] commands =
        [
            new CommitToPedestalCommand { InstanceId = new InstanceId("itm_001") },
            new SelectItemCommand { InstanceId = new InstanceId("itm_001") },
            new MoveItemCommand { InstanceId = new InstanceId("itm_001"), Section = EInventorySection.Hand },
            new SellCardCommand { InstanceId = new InstanceId("itm_001") },
            new SelectSkillCommand { InstanceId = new InstanceId("skl_001") },
            new SelectEncounterCommand { InstanceId = new InstanceId("enc_001") },
            new ExitCurrentStateCommand(),
            new RerollCommand(),
        ];

        foreach (bool isFirstAfterReestablish in new[] { false, true })
        {
            foreach (var cmd in commands)
            {
                Assert.False(GameServerEndpoints.ShouldAttachStateSyncOnCommandError(
                    cmd, ERunState.Pedestal, isFirstAfterReestablish));

                var reply = GameServerEndpoints.BuildCommandReply(
                    cmd, run, errOutcome, ERunState.Pedestal,
                    isOwnedSelectMove: false,
                    isFirstAfterReestablish: isFirstAfterReestablish,
                    out bool isPureMove,
                    out bool isCombat);

                Assert.False(isPureMove);
                Assert.False(isCombat);
                var err = Assert.IsType<NetMessageError>(reply);
                Assert.Same(errOutcome.Error, err);
            }
        }
    }

    [Fact]
    public void P0_01_CommitToPedestalCommand_RejectedInAnyInteractiveState_NeverAttachesGameStateSync()
    {
        var errOutcome = new RunEngine.Outcome
        {
            Error = new NetMessageError("ped_stale", EResponseErrorType.NotAllowed, "当前状态不允许提交祭坛"),
        };
        var cmd = new CommitToPedestalCommand { InstanceId = new InstanceId("itm_stale") };

        ERunState[] states =
        [
            ERunState.Pedestal,
            ERunState.Choice,
            ERunState.Encounter,
            ERunState.LevelUp,
            ERunState.Loot,
            ERunState.Combat,
            ERunState.PVPCombat,
            ERunState.EndRunVictory,
            ERunState.EndRunDefeat,
            ERunState.Shutdown,
        ];

        foreach (var state in states)
        {
            var run = CreateMinimalRun(state);
            foreach (bool isFirstAfterReestablish in new[] { false, true })
            {
                Assert.False(GameServerEndpoints.ShouldAttachStateSyncOnCommandError(
                    cmd, state, isFirstAfterReestablish));

                var reply = GameServerEndpoints.BuildCommandReply(
                    cmd, run, errOutcome, state,
                    isOwnedSelectMove: false,
                    isFirstAfterReestablish: isFirstAfterReestablish,
                    out _, out _);

                Assert.IsType<NetMessageError>(reply);
            }
        }
    }

    [Fact]
    public void P0_01_NonPedestalInteractiveStates_StillPreserveFallbackGameStateSync()
    {
        var errOutcome = new RunEngine.Outcome
        {
            Error = new NetMessageError("desync", EResponseErrorType.NotAllowed, "desync"),
        };

        INetCommand[] nonPedestalOptimisticCmds =
        [
            new SelectItemCommand { InstanceId = new InstanceId("x") },
            new MoveItemCommand { InstanceId = new InstanceId("x"), Section = EInventorySection.Hand },
            new SellCardCommand { InstanceId = new InstanceId("x") },
            new SelectSkillCommand { InstanceId = new InstanceId("x") },
            new SelectEncounterCommand { InstanceId = new InstanceId("x") },
            new ExitCurrentStateCommand(),
            new RerollCommand(),
        ];

        foreach (var state in new[] { ERunState.Encounter, ERunState.Choice, ERunState.LevelUp, ERunState.Loot })
        {
            var run = CreateMinimalRun(state);
            foreach (var cmd in nonPedestalOptimisticCmds)
            {
                Assert.True(GameServerEndpoints.ShouldAttachStateSyncOnCommandError(
                    cmd, state, isFirstAfterReestablish: false));

                var reply = GameServerEndpoints.BuildCommandReply(
                    cmd, run, errOutcome, state,
                    isOwnedSelectMove: false,
                    isFirstAfterReestablish: false,
                    out _, out _);

                var agg = Assert.IsType<NetMessageAggregate>(reply);
                Assert.Equal(2, agg.Messages.Count);
                Assert.IsType<NetMessageError>(agg.Messages[0]);
                Assert.IsType<NetMessageGameStateSync>(agg.Messages[1]);
            }

            // ReestablishSession 首包过期拒单在非祭坛交互态仍应附发兜底同步
            Assert.True(GameServerEndpoints.ShouldAttachStateSyncOnCommandError(
                new CheatCommand { Args = ["noop"] }, state, isFirstAfterReestablish: true));
        }
    }

    // ── P1-15：非 ASCII 中文账号名解析与 /active-run-tendency 权威 customRules ──

    [Fact]
    public void P1_15_ResolveAccountId_ResolvesNonAsciiChineseUsernames_WithoutGuestFallback()
    {
        string dbPath = NewTempDb(out string dir);
        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);

            var pofeng = accounts.GetOrCreateByName("破风");
            var pofengAlt = accounts.GetOrCreateByName("破风A");
            var alice = accounts.GetOrCreateByName("alice");
            var guest = accounts.GetOrCreateByName("guest");

            string expectedPofengId = pofeng.AccountId.ToString("D");
            string expectedAltId = pofengAlt.AccountId.ToString("D");
            string expectedAliceId = alice.AccountId.ToString("D");
            string guestId = guest.AccountId.ToString("D");
            Assert.NotEqual(guestId, expectedPofengId);

            string urlEncodedPofeng = Uri.EscapeDataString("破风"); // %E7%A0%B4%E9%A3%8E
            string b64Pofeng = Convert.ToBase64String(Encoding.UTF8.GetBytes("破风"));
            // 7 字节 UTF-8 -> Base64 带尾部 '=' 填充，测试省略 '=' 的 Base64Url 兼容解析
            string b64UrlAltNoPad = Convert.ToBase64String(Encoding.UTF8.GetBytes("破风A"))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            // 1. X-Bazaar-Username-Utf8（URL 编码 UTF-8）
            var ctx1 = new DefaultHttpContext();
            ctx1.Request.Headers[CustomRuleEndpoints.UsernameUtf8Header] = urlEncodedPofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx1, accounts));

            // 2. X-Username-B64（标准 Base64 UTF-8）
            var ctx2 = new DefaultHttpContext();
            ctx2.Request.Headers[CustomRuleEndpoints.UsernameBase64Header] = b64Pofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx2, accounts));

            // 3. X-Username-B64（Base64Url 无 '=' 填充）
            var ctx3 = new DefaultHttpContext();
            ctx3.Request.Headers[CustomRuleEndpoints.UsernameBase64Header] = b64UrlAltNoPad;
            Assert.Equal(expectedAltId, CustomRuleEndpoints.ResolveAccountId(ctx3, accounts));

            // 4. X-Username-Utf8 兼容别名
            var ctx4 = new DefaultHttpContext();
            ctx4.Request.Headers["X-Username-Utf8"] = urlEncodedPofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx4, accounts));

            // 5. X-Username（兼容 NameAuth.cs 既有 Uri.EscapeDataString 发送方式）
            var ctx5 = new DefaultHttpContext();
            ctx5.Request.Headers["X-Username"] = urlEncodedPofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx5, accounts));

            // 6. uid 遗留头 URL 编码兼容
            var ctx6 = new DefaultHttpContext();
            ctx6.Request.Headers["uid"] = urlEncodedPofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx6, accounts));

            // 7. 优先级校验：X-Bazaar-Username-Utf8 优先于 X-Username
            var ctx7 = new DefaultHttpContext();
            ctx7.Request.Headers[CustomRuleEndpoints.UsernameUtf8Header] = urlEncodedPofeng;
            ctx7.Request.Headers["X-Username"] = "guest";
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx7, accounts));

            // 8. 容错降级：损坏的 X-Username-B64、非法百分号 UTF-8（%FF%FE）与残缺 % 转义（bad%ZZ）均自动回落到下一有效用户名头
            var ctx8 = new DefaultHttpContext();
            ctx8.Request.Headers[CustomRuleEndpoints.UsernameUtf8Header] = "%FF%FE";
            ctx8.Request.Headers[CustomRuleEndpoints.UsernameBase64Header] = "!!!not-base64!!!";
            ctx8.Request.Headers["X-Username-Utf8"] = "bad%ZZ";
            ctx8.Request.Headers["X-Username"] = urlEncodedPofeng;
            Assert.Equal(expectedPofengId, CustomRuleEndpoints.ResolveAccountId(ctx8, accounts));
            Assert.Null(accounts.FindByUsername("FFFE"));
            Assert.Null(accounts.FindByUsername("badZZ"));

            // 9. 纯 ASCII 用户名与空头回退 guest 行为保持向后兼容
            var ctxAscii = new DefaultHttpContext();
            ctxAscii.Request.Headers["X-Username"] = "alice";
            Assert.Equal(expectedAliceId, CustomRuleEndpoints.ResolveAccountId(ctxAscii, accounts));

            var ctxEmpty = new DefaultHttpContext();
            Assert.Equal(guestId, CustomRuleEndpoints.ResolveAccountId(ctxEmpty, accounts));

            // 10. 严禁把百分号编码串去 '%' 后误建成脏账号 "E7A0B4E9A38E"
            Assert.Null(accounts.FindByUsername("E7A0B4E9A38E"));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task P1_15_HttpEndToEnd_ColdRestartChineseAccount_ActiveRunTendencyReturnsAuthoritativeCustomRules()
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
            var ruleStore = new PlayerRuleStore(db);
            var sessions = new SessionStore();

            // 1. 注册中文账号「破风」并配置 2+ 项自定义规则
            var pofeng = accounts.GetOrCreateByName("破风");
            var runCustomRules = new CustomRunRuleSet
            {
                PresetId = EGameModePreset.Custom,
                SpecialModeId = ESpecialModeId.EndlessTower,
                StartingGold = 50,
                StartingHealth = 500,
                MonsterLootBonusChoices = 2,
                SelectedArchetypeMask = (ulong)(ECardArchetype.Weapon | ECardArchetype.Burn),
                SelectedModIds = ["bazaar-chronicles-sample"],
            }.CloneNormalized();
            ruleStore.Set(pofeng.AccountId, runCustomRules);

            CustomRuleEndpoints.Map(
                app,
                ruleStore,
                accounts,
                () => fx.ArchetypeIndex.ToCatalogDto(fx.Pool, fx.Repo),
                sessions,
                db);
            await app.StartAsync();

            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            string encodedPofeng = Uri.EscapeDataString("破风");
            string b64Pofeng = Convert.ToBase64String(Encoding.UTF8.GetBytes("破风"));

            // 2. 模拟冷重启首帧（无 Bearer Token / 无 X-Account-Id）：仅凭编码后的中文用户名头拉取 /custom-rules/me 必须命中「破风」而非 guest
            using (var meReq1 = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me"))
            {
                meReq1.Headers.TryAddWithoutValidation("X-Username", encodedPofeng);
                using var meResp1 = await http.SendAsync(meReq1);
                Assert.Equal(HttpStatusCode.OK, meResp1.StatusCode);
                var meRules1 = await meResp1.Content.ReadFromJsonAsync<CustomRunRuleSet>();
                Assert.NotNull(meRules1);
                Assert.Equal(50, meRules1!.StartingGold);
                Assert.Equal(500, meRules1.StartingHealth);
                Assert.Equal(ESpecialModeId.EndlessTower, meRules1.SpecialModeId);
                Assert.True(meRules1.IsCustomPowerModified());
            }

            using (var meReq2 = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me"))
            {
                meReq2.Headers.TryAddWithoutValidation(CustomRuleEndpoints.UsernameUtf8Header, encodedPofeng);
                using var meResp2 = await http.SendAsync(meReq2);
                Assert.Equal(HttpStatusCode.OK, meResp2.StatusCode);
                var meRules2 = await meResp2.Content.ReadFromJsonAsync<CustomRunRuleSet>();
                Assert.NotNull(meRules2);
                Assert.Equal(50, meRules2!.StartingGold);
            }

            using (var meReq3 = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me"))
            {
                meReq3.Headers.TryAddWithoutValidation(CustomRuleEndpoints.UsernameBase64Header, b64Pofeng);
                using var meResp3 = await http.SendAsync(meReq3);
                Assert.Equal(HttpStatusCode.OK, meResp3.StatusCode);
                var meRules3 = await meResp3.Content.ReadFromJsonAsync<CustomRunRuleSet>();
                Assert.NotNull(meRules3);
                Assert.Equal(500, meRules3!.StartingHealth);
            }

            // 3. 模拟带自定义规则开局 -> 写入 SQLite 快照 -> 冷重启从 SQLite 反序列化恢复活跃对局
            var session = sessions.Create(pofeng.AccountId.ToString("N"), pofeng.Username, new InitializeRunCommand
            {
                PlayMode = EPlayMode.Unranked,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
            });

            var originalRun = new RunState
            {
                RunId = session.RunId,
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                AccountId = pofeng.AccountId.ToString("N"),
                Username = pofeng.Username,
                CustomRules = runCustomRules.CloneNormalized(),
            };
            var originalEngine = new RunEngine(
                originalRun, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo,
                new PvpGhostPool(), fx.ArchetypeIndex, ruleStore, null, sessions);
            originalEngine.StartRun();

            string snapJson = RunSnapshotSerializer.Serialize(
                originalRun,
                startSelectionDone: originalEngine.StartSelectionDone,
                pendingLevelUp: originalRun.PendingLevelUp);
            db.SaveRunSnapshot(originalRun, pofeng.AccountId.ToString("D"), snapJson, isCustomRule: originalRun.IsCustomRuleRun);

            var loadedRow = db.LoadLatestActiveRun(pofeng.AccountId);
            Assert.NotNull(loadedRow);
            Assert.True(RunSnapshotSerializer.TryDeserialize(loadedRow!.Snapshot, fx.Repo, out var resumedRun, out _));
            Assert.NotNull(resumedRun);

            var resumedEngine = new RunEngine(
                resumedRun!, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo,
                new PvpGhostPool(), fx.ArchetypeIndex, ruleStore, null, sessions);
            attachedSid = session.Sid;
            SessionRegistry.Attach(session.Sid, resumedRun!, resumedEngine);

            // 4. 故意把大厅规则重置回标准默认，验证 /active-run-tendency 仍以 SQLite 活跃对局的 customRules 为唯一权威事实源
            ruleStore.Reset(pofeng.AccountId);
            Assert.False(ruleStore.Get(pofeng.AccountId).IsCustomPowerModified());

            using (var activeReq = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me/active-run-tendency"))
            {
                activeReq.Headers.TryAddWithoutValidation(CustomRuleEndpoints.UsernameUtf8Header, encodedPofeng);
                using var activeResp = await http.SendAsync(activeReq);
                Assert.Equal(HttpStatusCode.OK, activeResp.StatusCode);

                var activeJson = await activeResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(activeJson.GetProperty("hasActiveRun").GetBoolean());
                Assert.Equal(session.RunId, activeJson.GetProperty("runId").GetString());
                Assert.Equal("EndlessTower", activeJson.GetProperty("specialModeId").GetString());
                Assert.True(activeJson.GetProperty("isCustomRuleRun").GetBoolean());

                Assert.True(activeJson.TryGetProperty("customRules", out var rulesEl));
                Assert.Equal(JsonValueKind.Object, rulesEl.ValueKind);
                Assert.Equal(50, rulesEl.GetProperty("startingGold").GetInt32());
                Assert.Equal(500, rulesEl.GetProperty("startingHealth").GetInt32());
                Assert.Equal(2, rulesEl.GetProperty("monsterLootBonusChoices").GetInt32());
                Assert.Equal("EndlessTower", rulesEl.GetProperty("specialModeId").GetString());
                Assert.Equal("Custom", rulesEl.GetProperty("presetId").GetString());
                Assert.True(rulesEl.GetProperty("isCustomPowerModified").GetBoolean());

                // 反序列化为强类型 CustomRunRuleSet 与 GET /custom-rules/me 字段 100% 一致
                var parsedActiveRules = JsonSerializer.Deserialize<CustomRunRuleSet>(rulesEl.GetRawText());
                Assert.NotNull(parsedActiveRules);
                Assert.Equal(50, parsedActiveRules!.StartingGold);
                Assert.Equal(500, parsedActiveRules.StartingHealth);
                Assert.Contains("bazaar-chronicles-sample", parsedActiveRules.SelectedModIds);
            }

            // 5. 校验只读 GET /custom-rules/me/lock-opening-tendency 同步透传 customRules
            using (var lockGetReq = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me/lock-opening-tendency"))
            {
                lockGetReq.Headers.Add("Authorization", "Bearer " + pofeng.AccessToken);
                using var lockGetResp = await http.SendAsync(lockGetReq);
                Assert.Equal(HttpStatusCode.OK, lockGetResp.StatusCode);
                var lockGetJson = await lockGetResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(lockGetJson.GetProperty("hasActiveRun").GetBoolean());
                Assert.True(lockGetJson.GetProperty("isCustomRuleRun").GetBoolean());
                Assert.Equal(50, lockGetJson.GetProperty("customRules").GetProperty("startingGold").GetInt32());
            }

            // 6. 无活跃局时向后兼容返回 hasActiveRun=false 且 customRules=null
            SessionRegistry.Detach(attachedSid);
            attachedSid = null;
            using (var inactiveReq = new HttpRequestMessage(HttpMethod.Get, "/custom-rules/me/active-run-tendency"))
            {
                inactiveReq.Headers.TryAddWithoutValidation(CustomRuleEndpoints.UsernameBase64Header, b64Pofeng);
                using var inactiveResp = await http.SendAsync(inactiveReq);
                Assert.Equal(HttpStatusCode.OK, inactiveResp.StatusCode);
                var inactiveJson = await inactiveResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(inactiveJson.GetProperty("hasActiveRun").GetBoolean());
                Assert.False(inactiveJson.GetProperty("isCustomRuleRun").GetBoolean());
                Assert.True(inactiveJson.TryGetProperty("customRules", out var nullRulesEl));
                Assert.Equal(JsonValueKind.Null, nullRulesEl.ValueKind);
            }
        }
        finally
        {
            if (attachedSid != null) SessionRegistry.Detach(attachedSid);
            await app.StopAsync();
            CleanupDir(dir);
        }
    }
}
