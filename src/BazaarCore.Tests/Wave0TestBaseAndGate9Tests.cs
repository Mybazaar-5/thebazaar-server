using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 0 专项回归测试：
/// 覆盖 TestDataLocator（worktree/commondir 主仓解析与严禁 Steam 回退）、
/// SharedGameDataFixture（只读共享 + 零拷贝内存 Overlay 保 Localization 防污染）、
/// 以及 D8-P1-05 Gate #9（确定性指标硬阻断、单 Tick 防重复计次、光环广播不误杀、200ms 降级 Warning、5000ms 极端防死循环熔断）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave0TestBaseAndGate9Tests : IClassFixture<SharedGameDataFixture>
{
    private const string SmallItemCarrierId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";
    private readonly SharedGameDataFixture _fx;
    private readonly ITestOutputHelper _output;

    public Wave0TestBaseAndGate9Tests(SharedGameDataFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    // ── 1. TestDataLocator：worktree（含相对 commondir 与无 commondir 回退）+ 严禁回退 Steam 缓存 ──

    [Fact]
    public void TestDataLocator_ResolvesWorktreePointerWithRelativeCommondir_AndNeverFallsBackToSteamCache()
    {
        // 1a. 真实环境定位的 DB 与 zh-CN.bytes 必须真实存在，且绝不落在 Steam prod/cache 目录
        string realDb = TestDataLocator.ResolveGameDataDbPath();
        string realZh = TestDataLocator.ResolveZhCnPath();
        Assert.True(File.Exists(realDb), $"权威 GameData.db 应存在: {realDb}");
        Assert.True(File.Exists(realZh), $"权威 zh-CN.bytes 应存在: {realZh}");
        Assert.False(TestDataLocator.IsUnderSteamProdCache(realDb), $"GameData.db 不得回退至 Steam 缓存: {realDb}");
        Assert.False(TestDataLocator.IsUnderSteamProdCache(realZh), $"zh-CN.bytes 不得回退至 Steam 缓存: {realZh}");

        string sandbox = Path.Combine(Path.GetTempPath(), "bazaar_w0_locator_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1b. 模拟带 commondir 相对路径（../..）的标准 git worktree
            string mainRepo = Path.Combine(sandbox, "main-repo");
            string mainGit = Path.Combine(mainRepo, ".git");
            string wtGitDir = Path.Combine(mainGit, "worktrees", "wt-a");
            string mainDistData = Path.Combine(mainRepo, "dist", "data");
            string mainTrans = Path.Combine(mainDistData, "translations");
            Directory.CreateDirectory(wtGitDir);
            Directory.CreateDirectory(mainTrans);
            File.WriteAllText(Path.Combine(mainDistData, "GameData.db"), "mock-db-bytes");
            File.WriteAllText(Path.Combine(mainTrans, "zh-CN.bytes"), "mock-zh-bytes");
            File.WriteAllText(Path.Combine(wtGitDir, "commondir"), "../..\n");

            string wtRoot = Path.Combine(sandbox, "worktrees", "wt-a");
            string wtSubDir = Path.Combine(wtRoot, "src", "BazaarCore.Tests", "bin", "Debug");
            Directory.CreateDirectory(wtSubDir);
            // 故意制造缺失 GameData.db 的 worktree/dist/data 目录，确保不会被不完整目录误截胡
            Directory.CreateDirectory(Path.Combine(wtRoot, "dist", "data"));
            File.WriteAllText(Path.Combine(wtRoot, ".git"), $"gitdir: {wtGitDir.Replace('\\', '/')}\r\n");

            Assert.True(TestDataLocator.TryResolveMainRepoRootFromWorktree(wtRoot, out string? resolvedMain));
            Assert.Equal(Path.GetFullPath(mainRepo), resolvedMain);

            string resolvedDataDir = TestDataLocator.ResolveDataDirCore(null, null, [wtSubDir]);
            Assert.Equal(Path.GetFullPath(mainDistData), resolvedDataDir);

            string resolvedZh = TestDataLocator.ResolveZhCnPathCore(null, null, null, [wtSubDir]);
            Assert.Equal(Path.GetFullPath(Path.Combine(mainTrans, "zh-CN.bytes")), resolvedZh);

            // 1c. 模拟无 commondir 文件时的经典 <main>/.git/worktrees/<name> 目录回退
            string wtGitDirNoCommon = Path.Combine(mainGit, "worktrees", "wt-b");
            Directory.CreateDirectory(wtGitDirNoCommon);
            string wtRootB = Path.Combine(sandbox, "worktrees", "wt-b");
            Directory.CreateDirectory(wtRootB);
            File.WriteAllText(Path.Combine(wtRootB, ".git"), $"gitdir: \"{wtGitDirNoCommon}\"");

            Assert.True(TestDataLocator.TryResolveMainRepoRootFromWorktree(wtRootB, out string? resolvedMainB));
            Assert.Equal(Path.GetFullPath(mainRepo), resolvedMainB);

            // 1d. 指向完全空目录且无任何候选仓库根时必须 fail-fast 抛 FileNotFoundException，绝不静默回退本机 Steam 缓存
            string emptyDir = Path.Combine(sandbox, "isolated-empty");
            Directory.CreateDirectory(emptyDir);
            Assert.Throws<FileNotFoundException>(() => TestDataLocator.ResolveDataDirCore(emptyDir, null, [emptyDir]));
            Assert.Throws<FileNotFoundException>(() => TestDataLocator.ResolveZhCnPathCore(null, emptyDir, null, [emptyDir]));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    // ── 2. SharedGameDataFixture：共享加载 + 内存 Overlay（含自定义 LevelUps）保 Localization 与隔离 ──

    [Fact]
    public void SharedGameDataFixture_LoadsOnce_MemoryOverlayAndLevelUpsPreserveLocalizationAndIsolation()
    {
        Assert.Same(SharedGameDataFixture.Shared.Repo, _fx.Repo);
        Assert.Same(SharedGameDataFixture.Shared.Pool, _fx.Pool);
        Assert.True(_fx.Repo.HasLocalizationLoaded, "共享 Repo 必须已加载仓库 zh-CN.bytes");
        Assert.True(_fx.Pool.ZhTranslationLoaded, "共享 CardPool 必须已复用仓库 zh-CN.bytes");
        Assert.False(TestDataLocator.IsUnderSteamProdCache(_fx.Pool.ZhTranslationLoadedFrom),
            $"共享 CardPool 词库路径不得来自 Steam 缓存: {_fx.Pool.ZhTranslationLoadedFrom}");

        const string syntheticCardId = "f0000001-0001-4001-8001-000000000099";
        string cardJson = JsonSerializer.Serialize(new
        {
            Id = syntheticCardId,
            InternalName = "Wave0 Synthetic Probe",
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = new[] { "Damage" },
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["CooldownMax"] = 4000,
                        ["DamageAmount"] = 10,
                        ["Multicast"] = 1
                    },
                    AbilityIds = Array.Empty<string>(),
                    AuraIds = Array.Empty<string>()
                }
            },
            Abilities = new { },
            Auras = new { }
        });

        string customLevelUpJson = JsonSerializer.Serialize(new
        {
            Level = 29,
            HealthIncrease = 777,
            Rewards = new
            {
                SelectionMethod = "Sequential",
                Groups = Array.Empty<object>()
            }
        });

        using var ov = _fx.CreateMemoryOverlay(
            customCards: [(syntheticCardId, cardJson)],
            customLevelUps: [(29, customLevelUpJson)]);

        // 严格校验 Overlay 完整保留 Localization（含自定义 LevelUps 走 14 参构造后绝不丢 Localization 导致 Steam 回退）
        Assert.True(ov.Repo.HasLocalizationLoaded, "带 customLevelUps 的 Overlay Repo 必须保留 Localization");
        Assert.Equal(_fx.Repo.Localization!.EntryCount, ov.Repo.Localization!.EntryCount);
        Assert.Equal(_fx.Repo.SourceDataPath, ov.Repo.SourceDataPath);
        Assert.True(ov.Pool.ZhTranslationLoaded);
        Assert.False(TestDataLocator.IsUnderSteamProdCache(ov.Pool.ZhTranslationLoadedFrom),
            $"Overlay CardPool 不得回退至 Steam 缓存: {ov.Pool.ZhTranslationLoadedFrom}");

        // 仅 Overlay 可见新增卡与自定义升级表，基础共享底盘零污染
        Assert.NotNull(ov.Repo.GetCard(syntheticCardId));
        Assert.NotNull(ov.Pool.Get(syntheticCardId));
        Assert.Equal(777, ov.Repo.LevelUps[29].HealthIncrease);
        Assert.Null(_fx.Repo.GetCard(syntheticCardId));
        Assert.Null(_fx.Pool.Get(syntheticCardId));
        Assert.NotEqual(777, _fx.Repo.LevelUps[29].HealthIncrease);

        // 隔离副本修改不影响共享实例
        var isolatedPool = _fx.CreateIsolatedCardPool();
        isolatedPool.ApplyRepositoryOverlay(ov.Repo);
        Assert.NotNull(isolatedPool.Get(syntheticCardId));
        Assert.Null(_fx.Pool.Get(syntheticCardId));

        var monstersCopy = _fx.CreateMonstersCopy();
        int origCount = _fx.Monsters.Count;
        monstersCopy.Clear();
        Assert.Equal(origCount, _fx.Monsters.Count);
    }

    // ── 3. Gate #9：墙钟 > 200ms 但确定性指标健康 -> 仅 Warning，0 Error，绝不误杀 ──

    [Fact]
    public void Gate9_WallClockOver200msWithoutDeterministicViolation_ReturnsWarningAndPasses()
    {
        var issues = new List<ModValidationIssue>();
        var input = new StressGateEvaluationInput(
            ModId: "ws-test-forge",
            SampleCardId: "ws-test-forge:forge_blade",
            MatchesExecuted: 50,
            TestedCardCount: 1,
            Exceptions: 0,
            NumericCorruptions: 0,
            MechanicBreakerTrips: 0,
            MechanicBudgetViolations: 0,
            SampleMechanicDiag: null,
            MaxDurationHits: 0,
            DeterministicStormMatches: 0,
            StormOrTimeoutUnionMatches: 0,
            PeakMatchEvents: 800,
            PeakMatchCardFires: 120,
            PeakMatchActionEvents: 450,
            PeakActionChainEvents: 18,
            PeakCascadeCapTicks: 0,
            SlowMatchesOver200ms: 3,
            AvgMatchMs: 65.4,
            MaxMatchMs: 1248.2,
            ModSideWinRatePct: 52.0,
            ExtremeHangDetected: false,
            ExtremeHangDetail: null);

        var (cascadeTrips, _) = ModCombatStressTester.EvaluateStressIssues(input, issues);

        Assert.Equal(0, cascadeTrips);
        Assert.DoesNotContain(issues, i => i.Severity == ModValidationSeverity.Error);
        var perfWarn = Assert.Single(issues, i => i.Code == "SIM_PERF_DEGRADATION_OVER_200MS");
        Assert.Equal(ModValidationSeverity.Warning, perfWarn.Severity);
        Assert.Equal(9, perfWarn.GateId);
    }

    // ── 4. Gate #9 反例：单 Tick 35 次 CardFired 仅计 CascadeCapTicks == 1，不误杀 ──

    [Fact]
    public void Gate9_SingleBusyTickWith35CardFires_CountsCascadeCapTickOnceAndDoesNotFalseBlock()
    {
        var events = new List<CombatEvent>(40);
        for (int i = 0; i < 35; i++)
        {
            events.Add(new CombatEvent
            {
                TimeMs = 1500,
                Type = CombatEventType.CardFired,
                ExecutionContextId = $"chain_1500_{i}"
            });
        }

        var result = new CombatResult
        {
            DurationMs = 12_000,
            IsTimeout = false,
            Events = events
        };

        var metrics = ModCombatStressTester.AnalyzeDeterministicMetrics(result);

        Assert.Equal(35, metrics.CardFires);
        Assert.Equal(1, metrics.CascadeCapTicks);
        Assert.False(metrics.ExceedsDeterministicBudget);

        // 即使输入非单调乱序 TimeMs，同一 TimeMs 桶仍严格最多计 1 次
        var outOfOrderEvents = new List<CombatEvent>(events);
        outOfOrderEvents.Add(new CombatEvent { TimeMs = 1000, Type = CombatEventType.CardFired });
        outOfOrderEvents.Add(new CombatEvent { TimeMs = 1500, Type = CombatEventType.CardFired });
        var metricsOutOfOrder = ModCombatStressTester.AnalyzeDeterministicMetrics(new CombatResult
        {
            DurationMs = 12_000,
            Events = outOfOrderEvents
        });
        Assert.Equal(1, metricsOutOfOrder.CascadeCapTicks);
    }

    // ── 5. Gate #9 反例：光环广播不计入动作链，而真实动作链超 256 在快机器（5ms）仍必阻断 ──

    [Fact]
    public void Gate9_AuraBroadcastBurstDoesNotTripActionChain_WhileRealActionChainStormBlocksEvenAt5ms()
    {
        // 5a. 同一 ExecutionContextId 下 400 条全盘光环/属性修改广播 + 10 条真实动作 -> MaxActionChainEvents == 10，绝不误杀
        var auraBurstEvents = new List<CombatEvent>(410);
        for (int i = 0; i < 200; i++)
        {
            auraBurstEvents.Add(new CombatEvent
            {
                TimeMs = 1000,
                Type = CombatEventType.AuraExecuted,
                ExecutionContextId = "chain_aura_1"
            });
            auraBurstEvents.Add(new CombatEvent
            {
                TimeMs = 1000,
                Type = CombatEventType.CardAttributeModified,
                ExecutionContextId = "chain_aura_1"
            });
        }
        for (int i = 0; i < 10; i++)
        {
            auraBurstEvents.Add(new CombatEvent
            {
                TimeMs = 1000,
                Type = CombatEventType.DamageApplied,
                ExecutionContextId = "chain_aura_1"
            });
        }

        var auraMetrics = ModCombatStressTester.AnalyzeDeterministicMetrics(new CombatResult
        {
            DurationMs = 5_000,
            Events = auraBurstEvents
        });
        Assert.Equal(10, auraMetrics.MaxActionChainEvents);
        Assert.Equal(10, auraMetrics.ActionEvents);
        Assert.False(auraMetrics.ExceedsDeterministicBudget);

        // 5b. 同一 ExecutionContextId 下 300 条真实动作事件 -> 突破 256 上限，即便墙钟仅 5ms 仍确定性 Error 阻断
        var stormEvents = new List<CombatEvent>(300);
        for (int i = 0; i < 300; i++)
        {
            stormEvents.Add(new CombatEvent
            {
                TimeMs = 2000,
                Type = CombatEventType.DamageApplied,
                ExecutionContextId = "chain_storm_1"
            });
        }
        var stormMetrics = ModCombatStressTester.AnalyzeDeterministicMetrics(new CombatResult
        {
            DurationMs = 5_000,
            Events = stormEvents
        });
        Assert.Equal(300, stormMetrics.MaxActionChainEvents);
        Assert.True(stormMetrics.ExceedsDeterministicBudget);

        var issues = new List<ModValidationIssue>();
        var (trips, _) = ModCombatStressTester.EvaluateStressIssues(new StressGateEvaluationInput(
            ModId: "storm-mod",
            SampleCardId: "storm-mod:loop",
            MatchesExecuted: 10,
            TestedCardCount: 1,
            Exceptions: 0,
            NumericCorruptions: 0,
            MechanicBreakerTrips: 0,
            MechanicBudgetViolations: 0,
            SampleMechanicDiag: null,
            MaxDurationHits: 0,
            DeterministicStormMatches: 1,
            StormOrTimeoutUnionMatches: 1,
            PeakMatchEvents: stormMetrics.TotalEvents,
            PeakMatchCardFires: stormMetrics.CardFires,
            PeakMatchActionEvents: stormMetrics.ActionEvents,
            PeakActionChainEvents: stormMetrics.MaxActionChainEvents,
            PeakCascadeCapTicks: stormMetrics.CascadeCapTicks,
            SlowMatchesOver200ms: 0,
            AvgMatchMs: 4.5,
            MaxMatchMs: 5.0,
            ModSideWinRatePct: 50.0,
            ExtremeHangDetected: false,
            ExtremeHangDetail: null), issues);

        Assert.Equal(1, trips);
        var stormErr = Assert.Single(issues, i => i.Code == "SIM_CASCADE_STORM_DETECTED");
        Assert.Equal(ModValidationSeverity.Error, stormErr.Severity);
    }

    // ── 6. Gate #9 极端墙钟 > 5000ms 防死循环熔断：采用 matchesExecuted 分母、防零除且不重复报 200ms Warning ──

    [Fact]
    public void Gate9_ExtremeWallClockHangFuse_BlocksWithError_SuppressesDuplicate200msWarning_AndGuardsZeroMatches()
    {
        var issues = new List<ModValidationIssue>();
        var (trips, _) = ModCombatStressTester.EvaluateStressIssues(new StressGateEvaluationInput(
            ModId: "hang-mod",
            SampleCardId: "hang-mod:bad",
            MatchesExecuted: 3,
            TestedCardCount: 1,
            Exceptions: 0,
            NumericCorruptions: 0,
            MechanicBreakerTrips: 0,
            MechanicBudgetViolations: 0,
            SampleMechanicDiag: null,
            MaxDurationHits: 1, // 1/3 = 33.3% > 20%（基于 matchesExecuted=3 而非原定 50 场）
            DeterministicStormMatches: 1,
            StormOrTimeoutUnionMatches: 1, // 同一场去重后应计 1 而非 2
            PeakMatchEvents: 35_000,
            PeakMatchCardFires: 1_000,
            PeakMatchActionEvents: 25_000,
            PeakActionChainEvents: 50,
            PeakCascadeCapTicks: 0,
            SlowMatchesOver200ms: 2,
            AvgMatchMs: 1900.0,
            MaxMatchMs: 5400.0,
            ModSideWinRatePct: 33.3,
            ExtremeHangDetected: true,
            ExtremeHangDetail: "第 3 场超过 5000ms"), issues);

        Assert.Equal(1, trips);
        Assert.Contains(issues, i => i.Code == "SIM_EXTREME_HANG_DETECTED" && i.Severity == ModValidationSeverity.Error);
        Assert.Contains(issues, i => i.Code == "SIM_CASCADE_STORM_DETECTED" && i.Severity == ModValidationSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Code == "SIM_PERF_DEGRADATION_OVER_200MS");
        Assert.DoesNotContain(issues, i => i.Code == "SIM_BALANCE_WARNING");

        // 零场边界保护（不抛除零异常）
        var zeroIssues = new List<ModValidationIssue>();
        var (zeroTrips, _) = ModCombatStressTester.EvaluateStressIssues(new StressGateEvaluationInput(
            ModId: "zero-mod",
            SampleCardId: null,
            MatchesExecuted: 0,
            TestedCardCount: 0,
            Exceptions: 0,
            NumericCorruptions: 0,
            MechanicBreakerTrips: 0,
            MechanicBudgetViolations: 0,
            SampleMechanicDiag: null,
            MaxDurationHits: 0,
            DeterministicStormMatches: 0,
            StormOrTimeoutUnionMatches: 0,
            PeakMatchEvents: 0,
            PeakMatchCardFires: 0,
            PeakMatchActionEvents: 0,
            PeakActionChainEvents: 0,
            PeakCascadeCapTicks: 0,
            SlowMatchesOver200ms: 0,
            AvgMatchMs: 0,
            MaxMatchMs: 0,
            ModSideWinRatePct: 0,
            ExtremeHangDetected: false,
            ExtremeHangDetail: null), zeroIssues);
        Assert.Equal(0, zeroTrips);
        Assert.Empty(zeroIssues);
    }

    // ── 7. 真实 50 场端到端校准：bazaar-chronicles-sample + 健康自定义武器卡确定性通过 ──

    [Fact]
    public void Gate9_Real50Matches_SampleModAndHealthyCustomWeapon_PassDeterministicallyWithHeadroom()
    {
        // 7a. 官方示例包 bazaar-chronicles-sample（50 场）+ 确定性峰值采集
        var scanIssues = new List<ModValidationIssue>();
        var samplePkg = ModLoader.LoadSinglePackage(
            Path.Combine(_fx.ModsRoot, "bazaar-chronicles-sample"),
            "bazaar-chronicles-sample",
            scanIssues);
        Assert.NotNull(samplePkg);
        Assert.DoesNotContain(scanIssues, i => i.Severity == ModValidationSeverity.Error);

        var sampleReport = ModValidator.RunTenGateValidation(
            samplePkg!,
            Array.Empty<ModPackage>(),
            _fx.Repo,
            _fx.CreateValidCardIdsCopy(),
            combatMatches: 50,
            combatSeed: 2026131,
            runDynamicGates: true);

        Assert.True(sampleReport.Passed, sampleReport.SummaryZh);
        Assert.Equal(0, sampleReport.ErrorCount);
        Assert.NotNull(sampleReport.CombatStressReport);
        Assert.Equal(50, sampleReport.CombatStressReport!.MatchesRun);
        Assert.Equal(0, sampleReport.CombatStressReport.Exceptions);
        Assert.Equal(0, sampleReport.CombatStressReport.NumericCorruptions);
        Assert.Equal(0, sampleReport.CombatStressReport.CascadeGuardTrips);
        Assert.Equal(0, sampleReport.CombatStressReport.MaxDurationHits);

        var sampleReg = new ModRegistry(_fx.Repo, _fx.CreateValidCardIdsCopy());
        try
        {
            Assert.True(sampleReg.LoadPackages([samplePkg!]).Succeeded);
            var (_, sampleStressIssues, sampleTel) = ModCombatStressTester.RunStressTestWithTelemetry(
                samplePkg!, sampleReg.ActiveRepository, matches: 50, seedBase: 2026131);
            Assert.DoesNotContain(sampleStressIssues, i => i.Severity == ModValidationSeverity.Error);
            Assert.Equal(0, sampleTel.DeterministicStormMatches);
            Console.WriteLine(
                $"[Gate9 Peak Sample 50场] PeakEvents={sampleTel.PeakMatchEvents}/{ModCombatStressTester.MaxDeterministicEventsPerMatch} " +
                $"PeakCardFires={sampleTel.PeakMatchCardFires}/{ModCombatStressTester.MaxDeterministicCardFiresPerMatch} " +
                $"PeakActionEvents={sampleTel.PeakMatchActionEvents}/{ModCombatStressTester.MaxDeterministicActionEventsPerMatch} " +
                $"PeakActionChain={sampleTel.PeakActionChainEvents}/{ModCombatStressTester.MaxDeterministicActionChainEventsPerContext} " +
                $"PeakCascadeCapTicks={sampleTel.PeakCascadeCapTicks}/{ModCombatStressTester.MaxDeterministicCascadeCapTicksPerMatch} " +
                $"avg={sampleTel.AvgMatchMs:0.0}ms max={sampleTel.MaxMatchMs:0.0}ms >200ms={sampleTel.SlowMatchesOver200ms}");
        }
        finally
        {
            sampleReg.RollbackToBase();
        }

        // 7b. 健康自定义青铜武器（模拟 ws-test-forge:forge_blade 50 场）+ 确定性峰值采集
        var healthyPkg = new ModPackage
        {
            RootDirectory = _fx.ModsRoot,
            Manifest = new ModManifest { Id = "w0-healthy-forge", Name = "健康武器压测", Version = "1.0.0", Scope = "multiplayer-safe" },
            Cards =
            {
                new CustomCardDefinition
                {
                    CustomId = "w0-healthy-forge:blade",
                    Type = "Item",
                    Size = "Small",
                    StartingTier = "Bronze",
                    BaseVisualTemplateId = SmallItemCarrierId,
                    Heroes = ["Vanessa"],
                    Tags = ["Weapon"],
                    HiddenTags = ["Damage"],
                    TitleZh = "熔炉短刃",
                    DescZh = "用于测试的标准短刃。",
                    ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害（冷却 {attr.CooldownMax} 秒）"],
                    Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bronze"] = new() { Attributes = new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 18, ["BuyPrice"] = 2, ["SellPrice"] = 1 }, AbilityIds = ["0"] },
                        ["Silver"] = new() { Attributes = new() { ["CooldownMax"] = 3800, ["DamageAmount"] = 36, ["BuyPrice"] = 4, ["SellPrice"] = 2 }, AbilityIds = ["0"] },
                        ["Gold"] = new() { Attributes = new() { ["CooldownMax"] = 3500, ["DamageAmount"] = 72, ["BuyPrice"] = 8, ["SellPrice"] = 4 }, AbilityIds = ["0"] },
                        ["Diamond"] = new() { Attributes = new() { ["CooldownMax"] = 3200, ["DamageAmount"] = 120, ["BuyPrice"] = 16, ["SellPrice"] = 8 }, AbilityIds = ["0"] },
                    },
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["0"] = new()
                        {
                            Id = "0",
                            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                            ActiveIn = "HandOnly",
                            Action = new RuleNode
                            {
                                Type = "TActionPlayerDamage",
                                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                                ReferenceValue = new RuleNode
                                {
                                    Type = "TReferenceValueCardAttribute",
                                    AttributeType = "DamageAmount",
                                    Target = new RuleNode { Type = "TTargetCardSelf" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var weaponReport = ModValidator.RunTenGateValidation(
            healthyPkg,
            Array.Empty<ModPackage>(),
            _fx.Repo,
            _fx.CreateValidCardIdsCopy(),
            combatMatches: 50,
            combatSeed: 2026131,
            runDynamicGates: true);

        Assert.True(weaponReport.Passed, weaponReport.SummaryZh);
        Assert.Equal(0, weaponReport.ErrorCount);
        Assert.NotNull(weaponReport.CombatStressReport);
        Assert.Equal(50, weaponReport.CombatStressReport!.MatchesRun);
        Assert.Equal(0, weaponReport.CombatStressReport.CascadeGuardTrips);
        Assert.Equal(0, weaponReport.CombatStressReport.Exceptions);
        Assert.Equal(0, weaponReport.CombatStressReport.NumericCorruptions);

        var weaponReg = new ModRegistry(_fx.Repo, _fx.CreateValidCardIdsCopy());
        try
        {
            Assert.True(weaponReg.LoadPackages([healthyPkg]).Succeeded);
            var (_, weaponStressIssues, weaponTel) = ModCombatStressTester.RunStressTestWithTelemetry(
                healthyPkg, weaponReg.ActiveRepository, matches: 50, seedBase: 2026131);
            Assert.DoesNotContain(weaponStressIssues, i => i.Severity == ModValidationSeverity.Error);
            Assert.Equal(0, weaponTel.DeterministicStormMatches);
            Console.WriteLine(
                $"[Gate9 Peak HealthyWeapon 50场] PeakEvents={weaponTel.PeakMatchEvents}/{ModCombatStressTester.MaxDeterministicEventsPerMatch} " +
                $"PeakCardFires={weaponTel.PeakMatchCardFires}/{ModCombatStressTester.MaxDeterministicCardFiresPerMatch} " +
                $"PeakActionEvents={weaponTel.PeakMatchActionEvents}/{ModCombatStressTester.MaxDeterministicActionEventsPerMatch} " +
                $"PeakActionChain={weaponTel.PeakActionChainEvents}/{ModCombatStressTester.MaxDeterministicActionChainEventsPerContext} " +
                $"PeakCascadeCapTicks={weaponTel.PeakCascadeCapTicks}/{ModCombatStressTester.MaxDeterministicCascadeCapTicksPerMatch} " +
                $"avg={weaponTel.AvgMatchMs:0.0}ms max={weaponTel.MaxMatchMs:0.0}ms >200ms={weaponTel.SlowMatchesOver200ms}");
        }
        finally
        {
            weaponReg.RollbackToBase();
        }

        // 7c. V14 风格开放自定义机制对抗卡（50 场，含前 5 场摧毁/修复/充能/DOT 对抗对手）确定性峰值采集
        var openMech = new MechanicDefinition
        {
            Id = "w0-open-mech:shield_pulse",
            NameZh = "开火护盾脉冲",
            DescZh = "开火时额外获得固定护盾",
            Parameters = [new MechanicParameterDef { Key = "shieldAmt", LabelZh = "护盾值", Type = "int", DefaultValue = JsonSerializer.SerializeToElement(18), Min = 1, Max = 500 }],
            Hooks =
            [
                new MechanicHookDefinition
                {
                    Id = "pulse",
                    Hook = "OnCardFired",
                    Scope = "SelfCard",
                    Filter = new MechanicHookFilter { OnlyWhenSelfSource = true },
                    Steps = [new MechanicStep { Op = "ApplyStatus", Status = "Shield", PlayerTarget = "Self", Amount = new MechanicValueExpr { Param = "shieldAmt" } }]
                }
            ]
        };
        var openMechPkg = new ModPackage
        {
            RootDirectory = _fx.ModsRoot,
            Manifest = new ModManifest { Id = "w0-open-mech", Name = "开放机制对抗压测", Version = "1.0.0", Scope = "multiplayer-safe" },
            Mechanics = { openMech },
            Cards =
            {
                new CustomCardDefinition
                {
                    CustomId = "w0-open-mech:pulse_dagger",
                    Type = "Item",
                    Size = "Small",
                    StartingTier = "Bronze",
                    BaseVisualTemplateId = SmallItemCarrierId,
                    Heroes = [HeroNames.Common],
                    Tags = ["Weapon"],
                    HiddenTags = ["Damage"],
                    TitleZh = "脉冲短刃",
                    DescZh = "附带开放自定义机制的演示短刃。",
                    ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害并获得护盾"],
                    Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Bronze"] = new() { Attributes = new() { ["CooldownMax"] = 3500, ["DamageAmount"] = 15, ["BuyPrice"] = 2, ["SellPrice"] = 1 }, AbilityIds = ["0"] },
                        ["Silver"] = new() { Attributes = new() { ["CooldownMax"] = 3500, ["DamageAmount"] = 30, ["BuyPrice"] = 4, ["SellPrice"] = 2 }, AbilityIds = ["0"] },
                        ["Gold"] = new() { Attributes = new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 60, ["BuyPrice"] = 8, ["SellPrice"] = 4 }, AbilityIds = ["0"] },
                        ["Diamond"] = new() { Attributes = new() { ["CooldownMax"] = 3000, ["DamageAmount"] = 120, ["BuyPrice"] = 16, ["SellPrice"] = 8 }, AbilityIds = ["0"] },
                    },
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["0"] = new()
                        {
                            Id = "0",
                            Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                            ActiveIn = "HandOnly",
                            Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                        }
                    },
                    CustomMechanics = [new CardMechanicAttachment { MechanicId = openMech.Id, Params = new() { ["shieldAmt"] = JsonSerializer.SerializeToElement(18) } }]
                }
            }
        };

        var mechReg = new ModRegistry(_fx.Repo, _fx.CreateValidCardIdsCopy());
        try
        {
            Assert.True(mechReg.LoadPackages([openMechPkg]).Succeeded);
            var (mechStressReport, mechStressIssues, mechTel) = ModCombatStressTester.RunStressTestWithTelemetry(
                openMechPkg, mechReg.ActiveRepository, matches: 50, seedBase: 2026131);
            Assert.DoesNotContain(mechStressIssues, i => i.Severity == ModValidationSeverity.Error);
            Assert.Equal(50, mechStressReport.MatchesRun);
            Assert.Equal(0, mechStressReport.CascadeGuardTrips);
            Assert.Equal(0, mechStressReport.MechanicCircuitBreakerTrips);
            Assert.Equal(0, mechStressReport.MechanicBudgetViolations);
            Console.WriteLine(
                $"[Gate9 Peak V14OpenMech 50场] PeakEvents={mechTel.PeakMatchEvents}/{ModCombatStressTester.MaxDeterministicEventsPerMatch} " +
                $"PeakCardFires={mechTel.PeakMatchCardFires}/{ModCombatStressTester.MaxDeterministicCardFiresPerMatch} " +
                $"PeakActionEvents={mechTel.PeakMatchActionEvents}/{ModCombatStressTester.MaxDeterministicActionEventsPerMatch} " +
                $"PeakActionChain={mechTel.PeakActionChainEvents}/{ModCombatStressTester.MaxDeterministicActionChainEventsPerContext} " +
                $"PeakCascadeCapTicks={mechTel.PeakCascadeCapTicks}/{ModCombatStressTester.MaxDeterministicCascadeCapTicksPerMatch} " +
                $"avg={mechTel.AvgMatchMs:0.0}ms max={mechTel.MaxMatchMs:0.0}ms >200ms={mechTel.SlowMatchesOver200ms}");
        }
        finally
        {
            mechReg.RollbackToBase();
            ModRuntimeHooks.ClearAll();
        }
        _output.WriteLine("Gate9 3-set deterministic peak calibration passed.");
    }
}
