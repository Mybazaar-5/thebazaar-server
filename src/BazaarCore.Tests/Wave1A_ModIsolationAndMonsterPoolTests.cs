using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1A 回归测试（文件 1）：覆盖 P0-17（ModRegistry/ModValidator/ServerModHost/SandboxEngineService
/// 全局运行态钩子零污染隔离与局级机制透传）与 D8-P1-01（MonsterPoolStore 不可变野怪池并发安全）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_ModIsolationAndMonsterPoolTests
{
    private sealed class DummyCustomActionHandler : ICustomActionHandler
    {
        public string ActionType => "TActionMod_Wave1AProbe";
        public bool Execute(RuleNode action, CardInstance source, RuleExecutionContext ctx) => true;
    }

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    private static string PickSmallWhitelistedItemId(GameDataRepository repo, HashSet<string> whitelist)
        => repo.CardsById.Values
            .First(c => whitelist.Contains(c.Id)
                        && string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase))
            .Id;

    private static void WriteMechanicModToDir(
        string modDir,
        string modId,
        string mechanicKey,
        string cardKey,
        string carrierId,
        int shieldAmount = 24)
    {
        Directory.CreateDirectory(Path.Combine(modDir, "mechanics"));
        Directory.CreateDirectory(Path.Combine(modDir, "cards"));

        File.WriteAllText(Path.Combine(modDir, "mod.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = modId,
            name = $"机制隔离测试包 {modId}",
            version = "1.0.0",
            author = "wave1a",
            scope = "multiplayer-safe",
            affectsGhostPool = false
        }), Encoding.UTF8);

        string fullMechId = $"{modId}:{mechanicKey}";
        File.WriteAllText(Path.Combine(modDir, "mechanics", "custom_mechanics.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mechanics = new[]
            {
                new MechanicDefinition
                {
                    Id = fullMechId,
                    NameZh = "开火护盾脉冲",
                    DescZh = "开火时获得固定护盾",
                    Parameters = [new MechanicParameterDef { Key = "shieldAmt", LabelZh = "护盾", Type = "int", DefaultValue = J(shieldAmount), Min = 1, Max = 300 }],
                    Hooks =
                    [
                        new MechanicHookDefinition
                        {
                            Id = "pulse",
                            Hook = "OnCardFired",
                            Scope = "SelfCard",
                            Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                            Steps =
                            [
                                new MechanicStep
                                {
                                    Op = "ApplyStatus",
                                    Status = "Shield",
                                    PlayerTarget = "Self",
                                    Amount = new MechanicValueExpr { Param = "shieldAmt" }
                                }
                            ]
                        }
                    ]
                }
            }
        }), Encoding.UTF8);

        File.WriteAllText(Path.Combine(modDir, "cards", "custom_cards.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            cards = new[]
            {
                new CustomCardDefinition
                {
                    CustomId = $"{modId}:{cardKey}",
                    BaseVisualTemplateId = carrierId,
                    Type = "Item",
                    Size = "Small",
                    StartingTier = "Bronze",
                    Heroes = [HeroNames.Common],
                    TitleZh = "脉冲短刃",
                    DescZh = "附带自定义机制的测试短刃",
                    ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害"],
                    Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bronze"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 2, ["SellPrice"] = 1, ["CooldownMax"] = 2500, ["DamageAmount"] = 12 }, AbilityIds = ["0"] },
                        ["Silver"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 4, ["SellPrice"] = 2, ["CooldownMax"] = 2500, ["DamageAmount"] = 24 }, AbilityIds = ["0"] },
                        ["Gold"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 8, ["SellPrice"] = 4, ["CooldownMax"] = 2000, ["DamageAmount"] = 48 }, AbilityIds = ["0"] },
                        ["Diamond"] = new TierData { Attributes = new(StringComparer.OrdinalIgnoreCase) { ["BuyPrice"] = 16, ["SellPrice"] = 8, ["CooldownMax"] = 2000, ["DamageAmount"] = 96 }, AbilityIds = ["0"] }
                    },
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["0"] = new AbilityDefinition
                        {
                            Id = "0",
                            ActiveIn = "HandOnly",
                            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                            Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                        }
                    },
                    CustomMechanics = [new CardMechanicAttachment { MechanicId = fullMechId, Params = new() { ["shieldAmt"] = J(shieldAmount) } }]
                }
            }
        }), Encoding.UTF8);
    }

    private static byte[] ZipDir(string modDir, string modId)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(modDir, file).Replace('\\', '/');
                var e = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = e.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Gate10_Validate_And_Rollback_MustNotClearGlobalModRuntimeHooks()
    {
        var fx = SharedGameDataFixture.Shared;
        var whitelist = fx.CreateValidCardIdsCopy();
        string carrierId = PickSmallWhitelistedItemId(fx.Repo, whitelist);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_g10_{Guid.NewGuid():N}");
        string globalModsDir = Path.Combine(tempRoot, "global_mods");
        string candidateDir = Path.Combine(tempRoot, "candidate_mod");
        Directory.CreateDirectory(globalModsDir);
        Directory.CreateDirectory(candidateDir);

        try
        {
            ModRuntimeHooks.ClearAll();
            WriteMechanicModToDir(Path.Combine(globalModsDir, "global-mech-a"), "global-mech-a", "mech_a", "blade_a", carrierId);
            WriteMechanicModToDir(candidateDir, "candidate-mech-b", "mech_b", "blade_b", carrierId);

            // 主注册表发布到全局静态 ModRuntimeHooks，并注册自定义 C# Action Handler
            var primary = new ModRegistry(fx.Repo, whitelist, publishGlobalRuntimeHooks: true);
            var loadReport = primary.LoadFromDirectory(globalModsDir);
            Assert.True(loadReport.Succeeded);
            ModRuntimeHooks.SetHandlers(actions: [new DummyCustomActionHandler()]);
            Assert.True(ModRuntimeHooks.HasAnyCustomMechanics);
            Assert.True(ModRuntimeHooks.HasAnyCustomActions);

            var scanIssues = new List<ModValidationIssue>();
            var candidatePkg = ModLoader.LoadSinglePackage(candidateDir, "candidate", scanIssues);
            Assert.NotNull(candidatePkg);

            // 使用同步屏障同时释放 20 线程并发执行 Gate #10 沙盒体检 + 回滚，严禁破坏共享 ModPackage 或清空外部线上 ModRuntimeHooks
            using var startBarrier = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            {
                startBarrier.Wait();
                var rep = ModValidator.RunTenGateValidation(
                    candidatePkg!,
                    null,
                    fx.Repo,
                    whitelist,
                    combatMatches: 3,
                    combatSeed: 202617,
                    runDynamicGates: true);
                string issueDetail = string.Join(" | ", rep.Gates.SelectMany(g => g.Issues).Select(i => $"[{i.Code}] {i.Message}"));
                Assert.True(rep.Passed, $"Gate #10 校验应通过：{rep.SummaryZh}；详情：{issueDetail}");
            })).ToArray();
            startBarrier.Set();
            await Task.WhenAll(tasks);

            Assert.True(ModRuntimeHooks.HasAnyCustomMechanics, "Gate #10 回滚绝不能清空全局机制注册表");
            Assert.True(ModRuntimeHooks.HasAnyCustomActions, "Gate #10 回滚绝不能清空全局自定义 C# Action 处理器");
            var globalMechs = ModRuntimeHooks.GetRegisteredMechanics();
            Assert.NotNull(globalMechs);
            Assert.True(globalMechs!.ContainsKey("global-mech-a:mech_a"));
            Assert.False(globalMechs.ContainsKey("candidate-mech-b:mech_b"));
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task IsolatedRoomModRegistry_MustNotOverwriteOrPolluteGlobalRuntimeHooks()
    {
        var fx = SharedGameDataFixture.Shared;
        var whitelist = fx.CreateValidCardIdsCopy();
        string carrierId = PickSmallWhitelistedItemId(fx.Repo, whitelist);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_iso_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        string commSrc = Path.Combine(tempRoot, "comm_src");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(commSrc);

        try
        {
            ModRuntimeHooks.ClearAll();
            WriteMechanicModToDir(Path.Combine(modsDir, "global-alpha"), "global-alpha", "alpha_shield", "alpha_dagger", carrierId);
            WriteMechanicModToDir(commSrc, "room-beta", "beta_shield", "beta_dagger", carrierId);

            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, whitelist, modsRootOverride: modsDir);
            var commStore = new CommunityModStore(host, stateDir);
            var up = await commStore.UploadZipAsync(ZipDir(commSrc, "room-beta"), "acc_owner", "Owner", isAdmin: false);
            Assert.True(up.Ok, up.Error);

            // 全局应仅包含 alpha
            var beforeMechs = ModRuntimeHooks.GetRegisteredMechanics();
            Assert.NotNull(beforeMechs);
            Assert.True(beforeMechs!.ContainsKey("global-alpha:alpha_shield"));
            Assert.False(beforeMechs.ContainsKey("room-beta:beta_shield"));

            // 按房解析隔离上下文：只选 room-beta（应与全局启用的 global-alpha 合并为局级快照，绝不反向污染全局！）
            var runCtx = host.ResolveRunModContext(
                ["room-beta"],
                fx.CreateIsolatedCardPool(),
                fx.CreateMonstersCopy(),
                fx.ArchetypeIndex);

            Assert.NotNull(runCtx.RuntimeContext);
            Assert.True(runCtx.RuntimeContext!.MechanicsById.ContainsKey("global-alpha:alpha_shield"));
            Assert.True(runCtx.RuntimeContext!.MechanicsById.ContainsKey("room-beta:beta_shield"));

            // 再次确认全局静态钩子仍只有 alpha，没有被 room-beta 污染
            var afterMechs = ModRuntimeHooks.GetRegisteredMechanics();
            Assert.NotNull(afterMechs);
            Assert.True(afterMechs!.ContainsKey("global-alpha:alpha_shield"));
            Assert.False(afterMechs.ContainsKey("room-beta:beta_shield"));
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SandboxEngineService_Simulate_MustPassIsolatedMechanicDefinitionsOverride()
    {
        var fx = SharedGameDataFixture.Shared;
        var whitelist = fx.CreateValidCardIdsCopy();
        string carrierId = PickSmallWhitelistedItemId(fx.Repo, whitelist);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_sbx_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        string commSrc = Path.Combine(tempRoot, "comm_sbx");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(commSrc);

        Persistence? db = null;
        string? attachedSid = null;
        try
        {
            ModRuntimeHooks.ClearAll();
            WriteMechanicModToDir(commSrc, "sbx-shield-mod", "pulse_sh", "pulse_knife", carrierId, shieldAmount: 40);

            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, whitelist, modsRootOverride: modsDir);
            var commStore = new CommunityModStore(host, stateDir);
            var up = await commStore.UploadZipAsync(ZipDir(commSrc, "sbx-shield-mod"), "acc_sbx", "SbxUser", isAdmin: false);
            Assert.True(up.Ok, up.Error);

            // 显式清空全局静态钩子，验证沙盒模拟严格依赖局级 mechanicDefinitionsOverride 而不是全局单例
            ModRuntimeHooks.ClearAll();
            Assert.False(ModRuntimeHooks.HasAnyCustomMechanics);

            db = new Persistence(Path.Combine(stateDir, "state.db"));
            var sessions = new SessionStore();
            var sseHub = new SseEventHub();
            var socialSink = new SocialAndReplayService(db, sseHub);
            var ghosts = new PvpGhostPool();
            var defaultPool = fx.CreateIsolatedCardPool();
            var defaultMonsters = fx.CreateMonstersCopy();

            var runCtx = host.ResolveRunModContext(["sbx-shield-mod"], defaultPool, defaultMonsters, fx.ArchetypeIndex);
            string accountId = Guid.NewGuid().ToString("N");
            var sbRules = PresetCatalog.Create(EGameModePreset.PureOfficial, ESpecialModeId.SandboxLab);
            sbRules.SelectedModIds = ["sbx-shield-mod"];
            var run = new RunState
            {
                RunId = "run_sbx_" + Guid.NewGuid().ToString("N")[..8],
                GameModeId = defaultPool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                AccountId = accountId,
                Username = "SandboxTester",
                CustomRules = sbRules
            };
            var engine = new RunEngine(
                run,
                runCtx.Pool,
                runCtx.Monsters,
                fx.LevelUps,
                fx.Mode,
                runCtx.Repository,
                ghosts,
                runCtx.ArchetypeIndex);
            engine.StartRun();

            var session = sessions.Create(accountId, "SandboxTester", new InitializeRunCommand { PlayMode = EPlayMode.Unranked, SelectedHero = EHero.Vanessa });
            attachedSid = session.Sid;
            SessionRegistry.Attach(session.Sid, run, engine);

            var sandbox = new SandboxEngineService(sessions, defaultPool, ghosts, db, socialSink, host);
            var grant = await sandbox.GrantCardAsync(
                accountId,
                new SandboxGrantCardRequest("sbx-shield-mod:pulse_knife", Section: "Hand", Tier: "Bronze", Enchantment: null, PreferredSlot: 0),
                CancellationToken.None);
            Assert.True(grant.Ok, grant.MessageZh);

            var combatRes = await sandbox.RunTestCombatAsync(
                accountId,
                new SandboxTestCombatRequest(OpponentSource: "self_mirror", Seed: 20261701),
                CancellationToken.None);
            Assert.True(combatRes.Ok, combatRes.MessageZh);

            // 解析 Summary 中的 playerTotalShield：脉冲短刃本身只有伤害，没有护盾，所有护盾必来自透传的隔离机制 sbx-shield-mod:pulse_sh！
            string sumJson = JsonSerializer.Serialize(combatRes.Summary);
            using var sumDoc = JsonDocument.Parse(sumJson);
            double totalShield = sumDoc.RootElement.GetProperty("playerTotalShield").GetDouble();
            Assert.True(totalShield >= 40.0, $"沙盒战斗应通过透传隔离机制触发 >= 40 点护盾，实际：{totalShield}");
            Assert.False(ModRuntimeHooks.HasAnyCustomMechanics, "沙盒模拟绝不应污染全局 ModRuntimeHooks");
        }
        finally
        {
            if (attachedSid != null) SessionRegistry.Detach(attachedSid);
            db?.Dispose();
            SqliteConnection.ClearAllPools();
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MonsterPoolStore_ConcurrentReloadAndEnumeration_ZeroInvalidOperationException()
    {
        var fx = SharedGameDataFixture.Shared;
        var whitelist = fx.CreateValidCardIdsCopy();
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_mpool_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        try
        {
            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, whitelist, modsRootOverride: modsDir);
            var cardPool = fx.CreateIsolatedCardPool();
            var monsterStore = new MonsterPoolStore(MonsterPool.FromRepository(host.ActiveRepository));
            host.BindPools(cardPool, monsterStore);

            int baselineCount = monsterStore.Count;
            Assert.True(baselineCount > 0);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            int totalEnumerations = 0;

            // 8 读线程连续枚举 SnapshotList() 与 Current（累计 >= 20,000 次）
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 2500; i++)
                {
                    var listSnap = monsterStore.SnapshotList();
                    int c1 = 0;
                    foreach (var m in listSnap)
                    {
                        if (m != null) c1++;
                    }
                    Assert.Equal(baselineCount, c1);

                    var immSnap = monsterStore.Current;
                    Assert.Equal(baselineCount, immSnap.Length);
                    Interlocked.Increment(ref totalEnumerations);
                }
            }, cts.Token)).ToArray();

            // 2 写线程并发高频触发 ServerModHost.Reload()（合计 100 次）
            var writers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    host.Reload();
                }
            }, cts.Token)).ToArray();

            await Task.WhenAll(readers.Concat(writers));
            Assert.Equal(20_000, totalEnumerations);
        }
        finally
        {
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
