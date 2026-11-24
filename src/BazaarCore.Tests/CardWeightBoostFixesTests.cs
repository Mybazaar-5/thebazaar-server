using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5 卡牌/事件权重提升修复与 Mod 避让延伸专项回归测试：
/// - 缺陷 A：SpawnQueryEngine.ShopHighTierBonusPct 100 倍单位错配与 Day 6+ 平滑衔接；
/// - 缺陷 B：流派倾向（SelectedArchetypeMask / ArchetypeWeightMultiplier）全链路透传
///   （ExecutionContext → SpawnQueryEngine / AbilityExecutor / OutOfCombatController / EncounterStepEngine / ShopEngine 战利品与升级奖励）；
/// - 缺陷 C：Mod 创意工坊自定义事件 Weight / MinDay / MaxDay 落地（weight<=0 转 GuidOnly、正权重作用于白昼抽签、天数窗门控、Gate 1 校验）；
/// - 避让延伸 #1（P1-04）：ModModels（CustomCard / CustomEvent / CardPatch）透传 BoardOverride 与 Presentation；
/// - 避让延伸 #2（P2-03）：ModValidator Gate 7 接入官方 136 条 tooltips 词条表校验 &lt;sprite&gt; / &lt;link&gt; 引用。
/// </summary>
[Xunit.Collection("GlobalStaticState")]
public sealed class CardWeightBoostFixesTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static RunState NewRun(EHero hero = EHero.Vanessa, uint day = 1, uint hour = 1, CustomRunRuleSet? rules = null, int seed = 42) => new()
    {
        RunId = "w5_" + Guid.NewGuid().ToString("N")[..8],
        GameModeId = Pool.Value.DefaultGameModeId,
        Hero = hero,
        PlayMode = EPlayMode.Unranked,
        Day = day,
        Hour = hour,
        RngSeed = seed,
        CustomRules = rules,
        PlayerAttributes =
        {
            [EPlayerAttributeType.Gold] = 40,
            [EPlayerAttributeType.HealthMax] = 400,
            [EPlayerAttributeType.Health] = 400
        }
    };

    private static GameDataRepository CloneRepoWithExtras(GameDataRepository baseRepo, IEnumerable<CardTemplate> extras)
        => baseRepo.CreateWithOverlay(additionalCards: extras);

    private static RuleNode BuildVanessaItemSpawnQuery(int limit = 1) => new()
    {
        Type = "TSpawnContextQuery",
        SelectionMethod = "Random",
        Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement((double)limit) },
        Groups =
        [
            new RuleNode
            {
                Type = "TSpawnGroup",
                Filters =
                [
                    new RuleNode { Type = "TSpawnFilterCardType", CardType = "Item" },
                    new RuleNode { Type = "TSpawnFilterHero", Heroes = ["Vanessa"] },
                ],
            }
        ],
    };

    // ── 缺陷 A：ShopHighTierBonusPct 100 倍单位错配修复 ─────────────────────────

    [Fact]
    public void BugA_ShopHighTierBonusPct_ScalesProportionallyForDbAndFallbackWeights_AndWorksOnDay6Plus()
    {
        var repo = Repo.Value;
        var gm = repo.GameMode;

        var enhanced = CustomRunRuleSet.CreatePreset(EGameModePreset.EnhancedFun); // +15%
        var extreme = CustomRunRuleSet.CreatePreset(EGameModePreset.ExtremeRoguelike); // +20%

        // 1. 真实 GameData.db Day 1（0..1 小数表，原始 Bronze=1.0）
        var (b1, s1, g1, d1) = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(gm, 1, enhanced);
        Assert.Equal(0.85, b1, precision: 6);
        Assert.Equal(0.12, s1, precision: 6);
        Assert.Equal(0.03, g1, precision: 6);
        Assert.Equal(0.00, d1, precision: 6);

        var (eb1, es1, eg1, ed1) = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(gm, 1, extreme);
        Assert.Equal(0.80, eb1, precision: 6);
        Assert.Equal(0.00, es1, precision: 6);
        Assert.Equal(0.15, eg1, precision: 6);
        Assert.Equal(0.05, ed1, precision: 6);

        // 2. 真实 GameData.db Day 6（Bronze=0 时，EnhancedFun 的 15% 应平滑从白银转入金/钻而非彻底归零失效）
        var (baseB6, baseS6, baseG6, baseD6) = SpawnQueryEngine.GetDayTierWeights(gm, 6);
        var (b6, s6, g6, d6) = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(gm, 6, enhanced);
        Assert.Equal(baseB6 + baseS6 + baseG6 + baseD6, b6 + s6 + g6 + d6, precision: 6);
        Assert.True(g6 + d6 > baseG6 + baseD6 + 0.10,
            $"Day 6 EnhancedFun 应有效提升金/钻占比：base={baseG6 + baseD6:P1}, enhanced={g6 + d6:P1}");

        // 3. null-gameMode 兜底 0..100 百分表与 0..1 小数表保持完全一致的百分比偏移
        var (fb1, fs1, fg1, fd1) = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(null, 1, enhanced);
        Assert.Equal(85.0, fb1, precision: 6);
        Assert.Equal(12.0, fs1, precision: 6);
        Assert.Equal(3.0, fg1, precision: 6);
        Assert.Equal(0.0, fd1, precision: 6);

        // 4. 真实 SpawnCards 蒙特卡洛抽样（Day 1 EnhancedFun 应以约 85% 青铜 + 15% 银/金分布，绝不会出现 0% 青铜）
        var qp = new PlayerState("p", "P", "Vanessa", 400, 20, 30, 10) { Day = 1, Hour = 1 };
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, new Random(2026))
        {
            CurrentDay = 1,
            CustomRules = enhanced,
        };
        var query = BuildVanessaItemSpawnQuery(limit: 1);
        int bronzeCount = 0, silverPlusCount = 0;
        const int draws = 600;
        for (int i = 0; i < draws; i++)
        {
            var spawned = SpawnQueryEngine.SpawnCards(query, ctx, defaultCountOverride: 1, validAssetIds: Pool.Value.ValidCardIds);
            Assert.Single(spawned);
            if (spawned[0].Tier == CardTier.Bronze) bronzeCount++;
            else silverPlusCount++;
        }

        double bronzeRate = bronzeCount / (double)draws;
        double silverPlusRate = silverPlusCount / (double)draws;
        Assert.InRange(bronzeRate, 0.75, 0.93);
        Assert.InRange(silverPlusRate, 0.07, 0.25);
    }

    // ── 缺陷 B：流派倾向全链路透传（ExecutionContext / OutOfCombat / EncounterStep / ShopEngine）──

    [Fact]
    public void BugB_SpawnCards_InheritsArchetypeResolverAndCustomRulesFromExecutionContext()
    {
        var (pool, repo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, repo);
        var burnRules = new CustomRunRuleSet
        {
            SelectedArchetypeMask = (ulong)ECardArchetype.Burn,
            ArchetypeWeightMultiplier = 2.5,
        };
        var query = BuildVanessaItemSpawnQuery(limit: 1);
        var qp = new PlayerState("p", "P", "Vanessa", 400, 20, 30, 10) { Day = 3, Hour = 1 };

        // 仅通过 RuleExecutionContext 注入（显式参数为 null，模拟 AbilityExecutor 内部调用方式）
        var ctxWeighted = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, new Random(777))
        {
            CurrentDay = 3,
            ArchetypeResolver = index.GetArchetypeMask,
            CustomRules = burnRules,
        };
        var ctxBase = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, new Random(777))
        {
            CurrentDay = 3,
        };

        int burnWeighted = 0, burnBase = 0;
        for (int i = 0; i < 400; i++)
        {
            var w = SpawnQueryEngine.SpawnCards(query, ctxWeighted, defaultCountOverride: 1, validAssetIds: pool.ValidCardIds);
            if (w.Count > 0 && (index.GetArchetypeMask(w[0].Template.Id) & (ulong)ECardArchetype.Burn) != 0)
                burnWeighted++;

            var b = SpawnQueryEngine.SpawnCards(query, ctxBase, defaultCountOverride: 1, validAssetIds: pool.ValidCardIds);
            if (b.Count > 0 && (index.GetArchetypeMask(b[0].Template.Id) & (ulong)ECardArchetype.Burn) != 0)
                burnBase++;
        }

        Assert.True(burnWeighted > burnBase + 25,
            $"ExecutionContext 继承流派倾向后灼烧命中数应显著提升：weighted={burnWeighted}, baseline={burnBase}");
    }

    [Fact]
    public void BugB_OutOfCombatController_AttachSpawnPreferences_PropagatesArchetypeToDayStartSpawn()
    {
        var (pool, repo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, repo);
        var burnRules = new CustomRunRuleSet
        {
            SelectedArchetypeMask = (ulong)ECardArchetype.Burn,
            ArchetypeWeightMultiplier = 3.0,
        };

        // 构造模拟“铲子/钓竿”的开天衍生卡（TTriggerOnDayStarted -> TActionGameSpawnCards）
        var shovelTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = "test_daystart_spawner",
            Type = "Item",
            InternalName = "Test Shovel",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = ["Vanessa"],
            SpawningEligibility = "GuidOnly",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandAndStash",
                    WorksIn = "OutOfCombatOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnDayStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionGameSpawnCards",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        SpawnContext = BuildVanessaItemSpawnQuery(limit: 1),
                    }
                }
            }
        };

        int RunTrials(bool attachBurnPreference)
        {
            var rng = new Random(13579);
            int burnHits = 0;
            for (int i = 0; i < 240; i++)
            {
                var p = new PlayerState("p", "P", "Vanessa", 400, 20, 30, 10) { Day = 3, Hour = 1 };
                p.Hand.TryPlaceOrShift(new CardInstance(shovelTpl, CardTier.Bronze), 0);
                if (attachBurnPreference)
                    OutOfCombatController.AttachSpawnPreferences(p, index.GetArchetypeMask, burnRules);

                var batch = OutOfCombatController.OnDayStarted(p, repo, rng);
                foreach (var sp in batch.SpawnedCards)
                {
                    if ((index.GetArchetypeMask(sp.Card.Template.Id) & (ulong)ECardArchetype.Burn) != 0)
                        burnHits++;
                }
            }
            return burnHits;
        }

        int weightedHits = RunTrials(attachBurnPreference: true);
        int baseHits = RunTrials(attachBurnPreference: false);
        Assert.True(weightedHits > baseHits + 20,
            $"OutOfCombatController 开天衍生应继承流派倾向：weighted={weightedHits}, baseline={baseHits}");
    }

    [Fact]
    public void BugB_EncounterStepEngine_DealCards_PropagatesArchetypeAndCustomRules()
    {
        var (pool, baseRepo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, baseRepo);

        var stepTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardEncounterStep",
            Id = "test_step_deal_archetype",
            Type = "EncounterStep",
            InternalName = "Test Archetype Deal Step",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "GuidOnly",
            ShouldProgressHour = false,
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { AbilityIds = ["0"] }
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionGameDealCards",
                        SpawnMode = "Replace",
                        SpawnContext = BuildVanessaItemSpawnQuery(limit: 3),
                    }
                }
            }
        };
        var overlayRepo = CloneRepoWithExtras(baseRepo, [stepTpl]);

        int RunStepTrials(CustomRunRuleSet? rules)
        {
            int burnHits = 0;
            for (int trial = 0; trial < 120; trial++)
            {
                var run = NewRun(EHero.Vanessa, day: 3, hour: 1, rules: rules, seed: 500 + trial);
                var shop = new ShopEngine(run, pool, overlayRepo, rules, index);
                var stepEngine = new EncounterStepEngine(run, pool, overlayRepo, shop, new Random(900 + trial));

                var stepEntry = new RunState.CardEntry
                {
                    InstanceId = $"stp_{trial}",
                    TemplateId = Guid.NewGuid(),
                    Type = ECardType.EncounterStep,
                    Tier = ETier.Bronze,
                    Size = ECardSize.Medium,
                    CoreCard = new CardInstance(stepTpl, CardTier.Bronze, instanceId: $"stp_{trial}"),
                };
                run.Cards[stepEntry.InstanceId] = stepEntry;
                run.SelectionSet.Add(stepEntry.InstanceId);

                var res = stepEngine.ExecuteStep(stepEntry);
                Assert.True(res.Success);
                foreach (var dealt in res.DealtCards)
                {
                    string tid = RunState.ResolveCoreTemplateId(dealt);
                    if ((index.GetArchetypeMask(tid) & (ulong)ECardArchetype.Burn) != 0)
                        burnHits++;
                }
            }
            return burnHits;
        }

        var burnRules = new CustomRunRuleSet
        {
            SelectedArchetypeMask = (ulong)ECardArchetype.Burn,
            ArchetypeWeightMultiplier = 3.0,
        };
        int weightedHits = RunStepTrials(burnRules);
        int baseHits = RunStepTrials(null);
        Assert.True(weightedHits > baseHits + 25,
            $"EncounterStepEngine.TActionGameDealCards 应继承流派倾向：weighted={weightedHits}, baseline={baseHits}");
    }

    [Fact]
    public void BugB_ShopEngine_MonsterVictoryLoot_PropagatesArchetypeAndCustomRules()
    {
        var (pool, baseRepo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, baseRepo);

        var combatTpl = new CardTemplate
        {
            TypeDiscriminator = "TCardEncounterCombat",
            Id = "test_combat_archetype_loot",
            Type = "CombatEncounter",
            InternalName = "Test Archetype Loot Combat",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = ["Common"],
            SpawningEligibility = "GuidOnly",
            RewardVictory = new CombatRewardData
            {
                GoldReward = 4,
                ExperienceReward = 2,
                SpawnContext = BuildVanessaItemSpawnQuery(limit: 3),
                SelectionContextRules = new SelectionRulesData
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                }
            }
        };
        var overlayRepo = CloneRepoWithExtras(baseRepo, [combatTpl]);
        var poolCombatTpl = new CardPool.CardTemplate
        {
            Id = combatTpl.Id,
            InternalName = combatTpl.InternalName,
            Type = "CombatEncounter",
            Heroes = ["Common"],
            StartingTier = "Bronze",
            RewardVictory = combatTpl.RewardVictory,
        };

        int RunLootTrials(CustomRunRuleSet? rules)
        {
            int burnHits = 0;
            for (int trial = 0; trial < 120; trial++)
            {
                var run = NewRun(EHero.Vanessa, day: 3, hour: 3, rules: rules, seed: 1200 + trial);
                // 刻意通过静态入口触发，顺便回归静态 new ShopEngine(run, pool, repo, run.CustomRules) 透传修复
                ShopEngine.DealMonsterVictoryLoot(run, pool, overlayRepo, poolCombatTpl, monster: null, p1Core: null, chooseOne: false);
                foreach (string sid in run.SelectionSet)
                {
                    if (run.Cards.TryGetValue(sid, out var entry))
                    {
                        string tid = RunState.ResolveCoreTemplateId(entry);
                        if ((index.GetArchetypeMask(tid) & (ulong)ECardArchetype.Burn) != 0)
                            burnHits++;
                    }
                }
            }
            return burnHits;
        }

        var burnRules = new CustomRunRuleSet
        {
            SelectedArchetypeMask = (ulong)ECardArchetype.Burn,
            ArchetypeWeightMultiplier = 3.0,
        };
        int weightedHits = RunLootTrials(burnRules);
        int baseHits = RunLootTrials(null);
        Assert.True(weightedHits > baseHits + 25,
            $"ShopEngine.DealMonsterVictoryLoot (SpawnContext + 静态工厂) 应继承流派倾向：weighted={weightedHits}, baseline={baseHits}");
    }

    // ── 缺陷 C：Mod 创意工坊自定义事件 Weight / MinDay / MaxDay 落地 ────────────

    [Fact]
    public void BugC_ModCustomEvent_ZeroWeightBecomesGuidOnly_AndDayWindowEnforced()
    {
        var baseRepo = Repo.Value;
        var baseVisualEvent = baseRepo.CardsById.Values.First(c =>
            string.Equals(c.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase) &&
            Pool.Value.IsAssetValid(c.Id));

        // 1. weight = 0 自动转为 GuidOnly 且 ModSpawnWeight = 0
        var chainOnlyEv = new CustomEventDefinition
        {
            EventId = "w5test:chain_only_event",
            BaseVisualTemplateId = baseVisualEvent.Id,
            TitleZh = "仅链式跳转奇遇",
            DescZh = "只能从上一奇遇跳转进入。",
            Heroes = ["Vanessa"],
            MinDay = 4,
            MaxDay = 6,
            Weight = 0.0,
            NextEncounterIds = [baseVisualEvent.Id],
        };

        var cardDef = chainOnlyEv.ToCardDefinition();
        Assert.Equal("GuidOnly", cardDef.SpawningEligibility);
        Assert.Equal(0.0, cardDef.ModSpawnWeight);
        Assert.Equal(4, cardDef.ModMinDay);
        Assert.Equal(6, cardDef.ModMaxDay);

        var tpl = cardDef.ToCardTemplate(baseVisualEvent, "w5test");
        Assert.Equal("GuidOnly", tpl.SpawningEligibility);
        Assert.Equal(0.0, tpl.ModSpawnWeight);
        Assert.Equal(4, tpl.ModMinDay);
        Assert.Equal(6, tpl.ModMaxDay);

        // 注入叠加 CardPool 后，确认不会进入随机白昼池 HourlyEncounters，且天数窗门控生效
        var overlayRepo = CloneRepoWithExtras(baseRepo, [tpl]);
        var overlayPool = TestInfrastructure.SharedGameDataFixture.Shared.CreateIsolatedCardPool();
        var bindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [tpl.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = tpl.Id,
                ModId = "w5test",
                BaseVisualTemplateId = baseVisualEvent.Id,
                CardType = "EventEncounter",
                Size = "Medium",
            }
        };
        overlayPool.ApplyRepositoryOverlay(overlayRepo, bindings);

        Assert.DoesNotContain(overlayPool.HourlyEncounters("Vanessa", "EventEncounter"), e => e.Id == tpl.Id);
        var mapped = overlayPool.Get(tpl.Id);
        Assert.NotNull(mapped);
        Assert.Equal(4, mapped!.ModMinDay);
        Assert.Equal(6, mapped.ModMaxDay);
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(mapped, day: 3, hour: 1, hero: "Vanessa"));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(mapped, day: 4, hour: 1, hero: "Vanessa"));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(mapped, day: 6, hour: 1, hero: "Vanessa"));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(mapped, day: 7, hour: 1, hero: "Vanessa"));

        // 2. ModValidator Gate 1 拦截非法负权重与倒挂天数窗
        var badPkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "w5-bad-ev", Name = "Bad Event", Scope = "singleplayer-only" },
            Events =
            {
                new CustomEventDefinition
                {
                    EventId = "w5-bad-ev:broken",
                    BaseVisualTemplateId = baseVisualEvent.Id,
                    TitleZh = "非法调度事件",
                    DescZh = "测试非法权重和天数窗。",
                    MinDay = 8,
                    MaxDay = 3,
                    Weight = -2.0,
                }
            }
        };
        var (_, issues, rejected) = ModValidator.ValidateAndSort([badPkg], baseRepo, Pool.Value.ValidCardIds);
        Assert.Contains("w5-bad-ev", rejected);
        Assert.Contains(issues, i => i.Code == "INVALID_EVENT_WEIGHT");
        Assert.Contains(issues, i => i.Code == "INVALID_EVENT_DAY_WINDOW");
    }

    [Fact]
    public void BugC_ModCustomEvent_PositiveWeightMultipliesHourlyEncounterDrawProbability()
    {
        var baseRepo = Repo.Value;
        // 选择一个带有效 SelectionContext 且白名单内合法的原版事件作为视觉载体
        var baseVisualEvent = baseRepo.CardsById.Values.First(c =>
            string.Equals(c.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase) &&
            c.SelectionContext?.SpawnContext != null &&
            Pool.Value.IsAssetValid(c.Id));

        var highWeightEv = new CustomEventDefinition
        {
            EventId = "w5weight:high_weight_event",
            BaseVisualTemplateId = baseVisualEvent.Id,
            TitleZh = "高频测试奇遇",
            DescZh = "高权重白昼测试事件。",
            Heroes = ["Vanessa"],
            MinDay = 1,
            MaxDay = 10,
            Weight = 40.0,
            NextEncounterIds = [baseVisualEvent.Id],
        };
        var lowWeightEv = new CustomEventDefinition
        {
            EventId = "w5weight:low_weight_event",
            BaseVisualTemplateId = baseVisualEvent.Id,
            TitleZh = "低频测试奇遇",
            DescZh = "低权重白昼测试事件。",
            Heroes = ["Vanessa"],
            MinDay = 1,
            MaxDay = 10,
            Weight = 1.0,
            NextEncounterIds = [baseVisualEvent.Id],
        };

        var highTpl = highWeightEv.ToCardDefinition().ToCardTemplate(baseVisualEvent, "w5weight");
        var lowTpl = lowWeightEv.ToCardDefinition().ToCardTemplate(baseVisualEvent, "w5weight");
        // 确保两个自定义事件带有效可玩载荷（HasPlayableEncounterPayload == true）
        highTpl.SelectionContext = baseVisualEvent.SelectionContext;
        lowTpl.SelectionContext = baseVisualEvent.SelectionContext;

        var overlayRepo = CloneRepoWithExtras(baseRepo, [highTpl, lowTpl]);
        var overlayPool = TestInfrastructure.SharedGameDataFixture.Shared.CreateIsolatedCardPool();
        var bindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [highTpl.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = highTpl.Id,
                ModId = "w5weight",
                BaseVisualTemplateId = baseVisualEvent.Id,
                CardType = "EventEncounter",
                Size = "Medium",
            },
            [lowTpl.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = lowTpl.Id,
                ModId = "w5weight",
                BaseVisualTemplateId = baseVisualEvent.Id,
                CardType = "EventEncounter",
                Size = "Medium",
            }
        };
        overlayPool.ApplyRepositoryOverlay(overlayRepo, bindings);

        int highHits = 0, lowHits = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            var run = NewRun(EHero.Vanessa, day: 2, hour: 1, rules: null, seed: 3000 + trial);
            var shop = new ShopEngine(run, overlayPool, overlayRepo);
            shop.DealHourEncounters(playerState: null, visitedTodayIds: null, rules: null);
            foreach (string sid in run.SelectionSet)
            {
                if (!run.Cards.TryGetValue(sid, out var entry)) continue;
                string coreId = RunState.ResolveCoreTemplateId(entry);
                if (string.Equals(coreId, highTpl.Id, StringComparison.OrdinalIgnoreCase)) highHits++;
                else if (string.Equals(coreId, lowTpl.Id, StringComparison.OrdinalIgnoreCase)) lowHits++;
            }
        }

        Assert.True(highHits >= 40, $"高权重 Mod 奇遇 (weight=40) 在 300 次白昼抽签中应高频出现，实际仅 {highHits} 次");
        Assert.True(highHits > lowHits * 4,
            $"高权重 Mod 奇遇应显著高于低权重奇遇：highHits(w=40)={highHits}, lowHits(w=1)={lowHits}");
    }

    // ── 避让延伸 #1（P1-04）：ModModels 透传 BoardOverride / Presentation ────────

    [Fact]
    public void ExtensionP1_04_ModModels_PropagateBoardOverrideAndPresentationToTemplateAndCardPool()
    {
        var baseRepo = Repo.Value;
        var baseVisualEvent = baseRepo.CardsById.Values.First(c =>
            string.Equals(c.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase) &&
            Pool.Value.IsAssetValid(c.Id));

        // 1. CustomEventDefinition JSON 反序列化与透传
        string evJson = $$"""
        {
          "eventId": "w5board:nebula_bay",
          "baseVisualTemplateId": "{{baseVisualEvent.Id}}",
          "titleZh": "星云海湾",
          "descZh": "切换至专属星云棋盘。",
          "boardOverride": "Board_NebulaBay",
          "presentation": "Presentation_NebulaArrival",
          "weight": 2.0
        }
        """;
        var evDef = JsonSerializer.Deserialize<CustomEventDefinition>(evJson)!;
        Assert.Null(evDef.ExtensionData);
        var evTpl = evDef.ToCardDefinition().ToCardTemplate(baseVisualEvent, "w5board");
        Assert.Equal("Board_NebulaBay", evTpl.BoardOverride);
        Assert.Equal("Presentation_NebulaArrival", evTpl.Presentation);

        // 2. CardPatchDefinition 原位魔改覆写
        string patchJson = $$"""
        {
          "targetTemplateId": "{{baseVisualEvent.Id}}",
          "boardOverride": "Board_PatchedArena",
          "presentation": "Presentation_PatchedIntro"
        }
        """;
        var patchDef = JsonSerializer.Deserialize<CardPatchDefinition>(patchJson)!;
        Assert.Null(patchDef.ExtensionData);
        var patchedTpl = patchDef.ApplyToTemplate(baseVisualEvent, "w5board");
        Assert.Equal("Board_PatchedArena", patchedTpl.BoardOverride);
        Assert.Equal("Presentation_PatchedIntro", patchedTpl.Presentation);

        // 3. 注入 CardPool 后保持原值
        var overlayRepo = CloneRepoWithExtras(baseRepo, [evTpl]);
        var overlayPool = TestInfrastructure.SharedGameDataFixture.Shared.CreateIsolatedCardPool();
        overlayPool.ApplyRepositoryOverlay(overlayRepo);
        var mapped = overlayPool.Get(evTpl.Id);
        Assert.NotNull(mapped);
        Assert.Equal("Board_NebulaBay", mapped!.BoardOverride);
        Assert.Equal("Presentation_NebulaArrival", mapped.Presentation);
    }

    // ── 避让延伸 #2（P2-03）：ModValidator Gate 7 官方 136 词条校验 ──────────────

    [Fact]
    public void ExtensionP2_03_ModValidatorGate7_ValidatesOfficial136TooltipSymbols()
    {
        var baseRepo = Repo.Value;
        Assert.True(baseRepo.TooltipSymbolsById.Count >= 130,
            $"GameData.db 应加载 >=130 条官方 tooltips 词条，实际 {baseRepo.TooltipSymbolsById.Count}");

        // 1. 合法官方图标与词条链接、普通中文文案均 0 告警
        var okIssues = ModValidator.ValidateTooltipSymbolReferences(
            "w5tip", "w5tip:ok",
            "施加 <sprite name=\"BurnIcon\"> <link=\"Burn\">灼烧</link> 并提升 <link=\"Crit Chance\">暴击率</link>。",
            baseRepo.TooltipSymbolsById);
        Assert.Empty(okIssues);

        var plainZhIssues = ModValidator.ValidateTooltipSymbolReferences(
            "w5tip", "w5tip:zh",
            "造成 {attr.DamageAmount} 点伤害，触发时充能相邻物品 1 秒 [0/3]。",
            baseRepo.TooltipSymbolsById);
        Assert.Empty(plainZhIssues);

        // 2. 拼写错误的图标与未知词条链接触发精准 Warning
        var badIssues = ModValidator.ValidateTooltipSymbolReferences(
            "w5tip", "w5tip:bad",
            "施加 <sprite name=\"BurnIocn\"> <link=\"TotallyFakeKeyword\">虚假词条</link>。",
            baseRepo.TooltipSymbolsById);
        Assert.Contains(badIssues, i => i.Code == "UNKNOWN_TOOLTIP_SPRITE_ICON" && i.Severity == ModValidationSeverity.Warning);
        Assert.Contains(badIssues, i => i.Code == "UNKNOWN_TOOLTIP_SYMBOL_LINK" && i.Severity == ModValidationSeverity.Warning);

        // 3. 端到端走 ModValidator.ValidateAndSort 门禁 7 链路验证
        var baseWeapon = baseRepo.CardsById.Values.First(c =>
            string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase) &&
            Pool.Value.IsAssetValid(c.Id));
        var pkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest { Id = "w5-tip-mod", Name = "Tooltip Check Mod", Scope = "singleplayer-only" },
            Cards =
            {
                new CustomCardDefinition
                {
                    CustomId = "w5-tip-mod:typo_card",
                    BaseVisualTemplateId = baseWeapon.Id,
                    Type = "Item",
                    Size = "Small",
                    StartingTier = "Bronze",
                    Heroes = ["Vanessa"],
                    TitleZh = "拼写检测卡",
                    DescZh = "用于测试 136 条官方词条校验。",
                    ActiveTooltipsZh = ["造成 <sprite name=\"DamgeIcon\"> {attr.DamageAmount} 伤害并触发 <link=\"NonExistentSym\">未知</link>。"],
                    Tiers = new Dictionary<string, TierData>
                    {
                        ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["DamageAmount"] = 10, ["CooldownMax"] = 4000 } }
                    }
                }
            }
        };

        var (_, issues, rejected) = ModValidator.ValidateAndSort([pkg], baseRepo, Pool.Value.ValidCardIds);
        Assert.DoesNotContain("w5-tip-mod", rejected); // Warning 不应硬阻断加载
        Assert.Contains(issues, i => i.Code == "UNKNOWN_TOOLTIP_SPRITE_ICON" && i.GateId == 7);
        Assert.Contains(issues, i => i.Code == "UNKNOWN_TOOLTIP_SYMBOL_LINK" && i.GateId == 7);
    }
}
