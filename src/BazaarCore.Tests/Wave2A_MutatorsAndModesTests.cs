using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2A P0-22 强断言端到端测试套件（触碰全局 MutatorCatalog 运行时表，归属 GlobalStaticState 串行集合）：
/// 直接加载仓库实样 mods/bazaar-chronicles-sample/modes/custom_mutators.json，验证 CustomMutators 加载、
/// MutatorCatalog 运行时注册/单局隔离、Gate #4 参数越界拦截、CustomMutatorRuleSummary 聚合、
/// CombatModifiers 叠合 W1-C 参数 + CombatSimulator 通用周期灼烧/充能驱动，以及 CustomModes + IGameModeHook。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2A_MutatorsAndModesTests
{
    private static RuleNode ParseRule(string json) => JsonSerializer.Deserialize<RuleNode>(json)!;

    [Fact]
    public void P022_SampleCustomMutatorsJson_EndToEnd_Catalog_Summary_AndCombatPeriodicEffects()
    {
        var shared = SharedGameDataFixture.Shared;
        string sampleModDir = Path.Combine(shared.ModsRoot, "bazaar-chronicles-sample");
        Assert.True(Directory.Exists(sampleModDir), $"未找到仓库实样 Mod 目录：{sampleModDir}");

        var loadIssues = new List<ModValidationIssue>();
        var pkg = ModLoader.LoadSinglePackage(sampleModDir, "bazaar-chronicles-sample", loadIssues);
        Assert.NotNull(pkg);
        Assert.DoesNotContain(loadIssues, i => i.Severity == ModValidationSeverity.Error);

        // 1. 精准解析仓库实样 custom_mutators.json 的两条自定义词条与混合类型 rules
        Assert.True(pkg!.CustomMutators.Count >= 2);
        var boom = pkg.CustomMutators.FirstOrDefault(m => m.Id == "bazaar-chronicles-sample:mutator_starport_boom");
        var tempest = pkg.CustomMutators.FirstOrDefault(m => m.Id == "bazaar-chronicles-sample:mutator_ember_tempest");
        Assert.NotNull(boom);
        Assert.NotNull(tempest);
        Assert.Equal("星港商潮", boom!.NameZh);
        Assert.Equal("Starport Trade Boom", boom.NameEn);
        Assert.Equal("Economy", boom.Category);
        Assert.Equal(1.0, boom.DifficultyDelta);
        Assert.Contains("星港进入贸易黄金季", boom.DescZh);
        Assert.NotNull(boom.Rules);
        Assert.True(MutatorCatalog.TryReadRuleDouble(boom, "startingGoldDelta", out double goldDelta) && goldDelta == 8.0);
        Assert.True(MutatorCatalog.TryReadRuleBool(boom, "freeFirstShopReroll", out bool freeReroll) && freeReroll);

        Assert.NotNull(tempest!.Rules);
        Assert.True(MutatorCatalog.TryReadRuleDouble(tempest, "combatPeriodicTriggerAfterMs", out double trigAfter) && trigAfter == 12000.0);
        Assert.True(MutatorCatalog.TryReadRuleDouble(tempest, "combatPeriodicIntervalMs", out double intervalMs) && intervalMs == 1000.0);
        Assert.True(MutatorCatalog.TryReadRuleDouble(tempest, "combatBothApplyBurn", out double bothBurn) && bothBurn == 4.0);
        Assert.True(MutatorCatalog.TryReadRuleDouble(tempest, "combatAllItemsChargeMsPerTick", out double chargeMs) && chargeMs == 250.0);
        Assert.True(MutatorCatalog.TryReadRuleDouble(tempest, "sandstormCountdownDeltaMs", out double sandDelta) && sandDelta == -5000.0);

        // 2. 验证 MutatorCatalog 单局隔离（extraMutators）与既有 maxCount = 4 / EGameModePreset 签名向后兼容
        var extraDict = pkg.CustomMutators.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        MutatorCatalog.ClearModMutators();
        try
        {
            Assert.Null(MutatorCatalog.TryGet(boom.Id));
            Assert.NotNull(MutatorCatalog.TryGet(boom.Id, extraDict));

            var rawRules = new CustomRunRuleSet
            {
                MutatorIds = new List<string> { boom.Id, tempest.Id }
            };
            var normalized = rawRules.CloneNormalized(extraDict);
            Assert.Contains(boom.Id, normalized.MutatorIds);
            Assert.Contains(tempest.Id, normalized.MutatorIds);

            MutatorCatalog.RegisterModMutators(pkg.CustomMutators);
            var globalNorm = MutatorCatalog.NormalizeAndResolveConflicts(new[] { boom.Id, tempest.Id });
            Assert.Equal(2, globalNorm.Count);
            double scoreMult = MutatorCatalog.CalculateTotalScoreMultiplier(EGameModePreset.PureOfficial, globalNorm);
            Assert.True(scoreMult > 0);

            // 3. 验证 CustomMutatorRuleSummary 通用规则聚合
            var summary = MutatorCatalog.ResolveRuleSummary(new[] { boom.Id, tempest.Id }, extraDict);
            Assert.Equal(8, summary.StartingGoldDelta);
            Assert.Equal(2, summary.StartingIncomeDelta);
            Assert.True(summary.FreeFirstShopReroll);
            Assert.Equal(1.15, summary.MonsterHealthMultiplier, precision: 3);
            Assert.Equal(1.5, summary.BoostModCardWeight, precision: 3);

            // 4. 验证 CombatModifiers.FromRulesAndDay 叠合 W1-C 参数并构建 PeriodicEffects + 提前沙暴
            var baselineMods = CombatModifiers.FromRulesAndDay(new CustomRunRuleSet(), day: 1, isBossRush: false, isPve: false);
            var tempestRules = new CustomRunRuleSet { MutatorIds = new List<string> { tempest.Id } };
            var tempestMods = CombatModifiers.FromRulesAndDay(
                tempestRules,
                day: 1,
                isBossRush: false,
                isPve: false,
                bossRushWave: 0,
                isSymmetricHumanPvp: false,
                endlessGhostSourceDay: null,
                customMutators: extraDict);

            Assert.Equal(baselineMods.SandstormStartOffsetMs - 5000, tempestMods.SandstormStartOffsetMs);
            Assert.Single(tempestMods.PeriodicEffects);
            var pe = tempestMods.PeriodicEffects[0];
            Assert.Equal(12000, pe.TriggerAfterMs);
            Assert.Equal(1000, pe.IntervalMs);
            Assert.Equal(4, pe.BothApplyBurn);
            Assert.Equal(250, pe.AllItemsChargeMsPerTick);

            // 5. CombatSimulator 真实战斗驱动：12000ms 后双方均收到周期灼烧且物品获得充能事件
            var slowCardTpl = new CardTemplate
            {
                Id = "w2a_slow_clock",
                Type = "Item",
                Size = "Small",
                StartingTier = "Bronze",
                InternalName = "SlowClock",
                Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bronze"] = new TierData
                    {
                        AbilityIds = new List<string> { "s0" },
                        Attributes = new Dictionary<string, double> { ["CooldownMax"] = 6000, ["ShieldApplyAmount"] = 20 }
                    }
                },
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["s0"] = new AbilityDefinition
                    {
                        Id = "s0",
                        ActiveIn = "HandOnly",
                        Trigger = ParseRule("""{"$type":"TTriggerOnCardFired"}"""),
                        Action = ParseRule("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                    }
                }
            };
            var simRepo = shared.Repo.CreateWithOverlay(additionalCards: new[] { slowCardTpl });

            var p1 = new PlayerState("player", "P1", "Vanessa", initialHealthMax: 2000);
            var p2 = new PlayerState("opponent", "P2", "Vanessa", initialHealthMax: 2000);
            p1.Hand.TryAddAuto(new CardInstance(slowCardTpl, CardTier.Bronze, enchantment: null, instanceId: "p1_clock"));
            p2.Hand.TryAddAuto(new CardInstance(slowCardTpl, CardTier.Bronze, enchantment: null, instanceId: "p2_clock"));

            var combatRes = CombatSimulator.Simulate(
                simRepo,
                p1,
                p2,
                seed: 77,
                combatType: "PVP",
                recordTimeline: false,
                recordEvents: true,
                syncPermanentChanges: false,
                modifiers: tempestMods);
            Assert.Contains(combatRes.Events, e => e.Type == CombatEventType.BurnApplied && e.TimeMs >= 12000 && e.TargetPlayerId == "player");
            Assert.Contains(combatRes.Events, e => e.Type == CombatEventType.BurnApplied && e.TimeMs >= 12000 && e.TargetPlayerId == "opponent");
            Assert.Contains(combatRes.Events, e => e.Type == CombatEventType.ChargeApplied && e.TimeMs >= 12000 && e.TargetCardInstanceId == "p1_clock");
        }
        finally
        {
            MutatorCatalog.ClearModMutators();
        }
    }

    [Fact]
    public void P022_ModValidatorGate4_BlocksBadMutatorRules_AndCustomModeHookRunsThroughModRegistry()
    {
        var shared = SharedGameDataFixture.Shared;

        // 1. 越界突变词条参数（interval < 250 / monsterHealthMultiplier > 10 / burn > 500）必须被 Gate #4 阻断
        var badPkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest
            {
                Id = "w2a-bad-mutator",
                Name = "坏词条测试包",
                Version = "1.0.0",
                Scope = "multiplayer-safe"
            },
            CustomMutators =
            {
                new MutatorMetadata(
                    Id: "w2a-bad-mutator:mut_illegal",
                    NameZh: "非法周期词条",
                    DescZh: "故意越界",
                    ConflictGroup: null,
                    ScoreMultiplierDelta: 0.0,
                    IsPositive: false,
                    SourceModId: "w2a-bad-mutator",
                    Rules: new Dictionary<string, object?>
                    {
                        ["combatPeriodicIntervalMs"] = 50,
                        ["monsterHealthMultiplier"] = 99.0,
                        ["combatBothApplyBurn"] = 999
                    })
            }
        };

        var (_, valIssues, _) = ModValidator.ValidateAndSort(new[] { badPkg }, shared.Repo, shared.ValidCardIds);
        var mutatorErrs = valIssues.Where(i => i.GateId == 4 && i.Severity == ModValidationSeverity.Error && i.Code == "MUTATOR_PARAM_OUT_OF_RANGE").ToList();
        Assert.Equal(3, mutatorErrs.Count);
        Assert.Contains(mutatorErrs, i => i.Message.Contains("combatPeriodicIntervalMs"));
        Assert.Contains(mutatorErrs, i => i.Message.Contains("monsterHealthMultiplier"));
        Assert.Contains(mutatorErrs, i => i.Message.Contains("combatBothApplyBurn"));

        // 2. 合法自定义模式 + 自定义词条通过 ModRegistry 隔离装配 DeclarativeGameModeHook 并由 RuntimeContext 导出
        var modePkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest
            {
                Id = "w2a-mode-pack",
                Name = "自定义模式测试包",
                Version = "1.0.0",
                Scope = "multiplayer-safe"
            },
            CustomMutators =
            {
                new MutatorMetadata(
                    Id: "w2a-mode-pack:mut_fast_shop",
                    NameZh: "极速商潮",
                    DescZh: "开局多给金币",
                    ConflictGroup: null,
                    ScoreMultiplierDelta: 0.1,
                    IsPositive: true,
                    SourceModId: "w2a-mode-pack",
                    Rules: new Dictionary<string, object?>
                    {
                        ["startingGoldDelta"] = 12,
                        ["shopPriceMultiplier"] = 0.85
                    })
            },
            Modes =
            {
                new CustomModeDefinition
                {
                    ModeId = "w2a-mode-pack:turbo_gold",
                    NameZh = "金币竞速模式",
                    RuleOverrides = new CustomRunRuleSet
                    {
                        StartingGold = 25,
                        StartingIncome = 8,
                        StartingHealth = 450,
                        ShopPriceMultiplier = 0.8,
                        EndlessMaxDay = 15
                    },
                    MutatorIds = new List<string> { "w2a-mode-pack:mut_fast_shop" }
                }
            }
        };

        var isoRegistry = ModRegistry.CreateIsolated(shared.Repo, shared.ValidCardIds);
        var report = isoRegistry.LoadPackages(new[] { modePkg }, sourceLabel: "w2a-mode-test");
        Assert.True(report.Succeeded, string.Join("; ", report.Issues.Select(i => $"{i.Code}:{i.Message}")));

        Assert.True(isoRegistry.RuntimeContext.EffectiveCustomModesById.ContainsKey("w2a-mode-pack:turbo_gold"));
        Assert.True(isoRegistry.RuntimeContext.ModeHooks.TryGetValue("w2a-mode-pack:turbo_gold", out var hook));
        Assert.NotNull(hook);

        var setupAdj = new GameModeRunSetupAdjustment();
        hook!.OnRunSetup(setupAdj, new CustomRunRuleSet());
        Assert.Equal(10, setupAdj.StartingGoldDelta);
        Assert.Equal(3, setupAdj.StartingIncomeDelta);
        Assert.Equal(150, setupAdj.StartingHealthDelta);
        Assert.Equal(15, setupAdj.MaxDaysOverride);
        Assert.Equal(0.8, setupAdj.ShopPriceMultiplier, precision: 3);
        Assert.Contains("w2a-mode-pack:mut_fast_shop", setupAdj.ForcedMutatorIds);

        var appliedRules = new CustomRunRuleSet();
        hook.OnRunInitializing(appliedRules, new GameModeData());
        Assert.Equal(25, appliedRules.StartingGold);
        Assert.Contains("w2a-mode-pack:mut_fast_shop", appliedRules.MutatorIds);
    }
}
