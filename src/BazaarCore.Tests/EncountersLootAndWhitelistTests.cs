using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V4 核心测试套件（三）：验证 Gate 1 资源白名单（valid_card_ids.json + CardPool）、
/// ECardType 保留、去兜底纯奇遇、多英雄开局、EncounterStepEngine 多步链式跳转与野怪战利品/战败折算。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class EncountersLootAndWhitelistTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private static readonly Lazy<List<BazaarServer.Data.MonsterTemplate>> Monsters = new(() => TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy());

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static GameDataRepository CreateRepoWithExtraCards(params CardTemplate[] extras)
    {
        var baseRepo = Repo.Value;
        var cards = new Dictionary<string, CardTemplate>(baseRepo.CardsById, StringComparer.OrdinalIgnoreCase);
        foreach (var ex in extras) cards[ex.Id] = ex;
        var monsters = new Dictionary<string, BazaarCore.Models.MonsterTemplate>(baseRepo.MonstersById, StringComparer.OrdinalIgnoreCase);
        var levelUps = new Dictionary<int, LevelUpTemplate>(baseRepo.LevelUps);
        var tooltips = new Dictionary<string, string>(baseRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(baseRepo.ObservedRuleTypes, StringComparer.Ordinal);
        var unknown = new HashSet<string>(baseRepo.UnknownRuleTypes, StringComparer.Ordinal);
        return new GameDataRepository(
            baseRepo.SourceZipPath,
            baseRepo.GameMode,
            cards,
            monsters,
            levelUps,
            tooltips,
            observed,
            unknown,
            baseRepo.Localization,
            baseRepo.SourceDataPath);
    }

    private static RunState NewTestRun(EHero hero = EHero.Vanessa, int gold = 20, uint day = 2, uint hour = 1)
    {
        var run = new RunState
        {
            RunId = "test_enc_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 300;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 300;
        return run;
    }

    private static JsonElement JsonFixed(double val)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{val.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement JsonNumber(double val)
        => JsonDocument.Parse(val.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    [Fact]
    public void Gate1_ValidCardIdsJson_ShouldExistAndFilterAllInvalidCardsInCardPool()
    {
        string wlPath = ResolveDataPath("valid_card_ids.json");
        Assert.True(File.Exists(wlPath), $"找不到白名单文件: {wlPath}");

        var pool = Pool.Value;
        Assert.True(pool.WhitelistEnabled, "CardPool 应自动启用 valid_card_ids.json 白名单");
        Assert.True(pool.TotalCards >= 3000, $"白名单有效卡牌数应 >= 3000，实际: {pool.TotalCards}");
        Assert.True(pool.TotalRawCards > pool.TotalCards, "白名单应过滤掉缺失资产的 PTR/Debug 卡");

        using var doc = JsonDocument.Parse(File.ReadAllText(wlPath));
        var invalidArr = doc.RootElement.GetProperty("invalid_cards");
        Assert.True(invalidArr.GetArrayLength() > 0);
        foreach (var item in invalidArr.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Assert.False(pool.IsAssetValid(id), $"缺失资产卡牌 {id} 不应通过 IsAssetValid");
            Assert.Null(pool.GetIfAssetValid(id));
        }

        // 所有按类型导出的可生成对象必须 100% 通过白名单
        foreach (string t in new[] { "Item", "Skill", "EventEncounter", "EncounterStep", "PedestalEncounter", "CombatEncounter" })
        {
            foreach (var c in pool.OfType(t))
            {
                Assert.True(pool.IsAssetValid(c.Id), $"{t} {c.Id} ({c.InternalName}) 未在白名单中");
            }
        }
    }

    [Fact]
    public void ShopEngine_CreateEntryFromCoreInstance_ShouldPreserveExactECardType()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var run = NewTestRun();
        var shop = new ShopEngine(run, pool, repo);

        var samples = new (string TypeStr, ECardType Expected)[]
        {
            ("Item", ECardType.Item),
            ("Skill", ECardType.Skill),
            ("EventEncounter", ECardType.EventEncounter),
            ("EncounterStep", ECardType.EncounterStep),
            ("PedestalEncounter", ECardType.PedestalEncounter),
            ("CombatEncounter", ECardType.CombatEncounter),
        };

        foreach (var (typeStr, expected) in samples)
        {
            var tpl = pool.OfType(typeStr).FirstOrDefault();
            Assert.NotNull(tpl);
            var coreTpl = repo.GetCard(tpl!.Id);
            Assert.NotNull(coreTpl);

            var inst = new CardInstance(coreTpl!);
            var entry = shop.CreateEntryFromCoreInstance(inst, isFree: false);
            Assert.Equal(expected, entry.Type);
        }
    }

    [Fact]
    public void ShopEngine_NonMerchantEmptyEvent_ShouldNeverFallbackToGenericShopItems()
    {
        const string emptyEventId = "99999999-1111-2222-3333-444444444444";
        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = emptyEventId,
            InternalName = "Mysterious Shrine",
            Type = "EventEncounter",
            Tags = [],
            SelectionContext = new SelectionContextData
            {
                Rules = new SelectionRulesData
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                },
                SpawnContext = null,
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun();
        var shop = new ShopEngine(run, pool, repo);

        var evs = shop.DealEncounterShelf(emptyEventId, isReroll: false);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(emptyEventId, run.CurrentEncounterId);
        // 严禁把纯随机奇遇兜底塞入 3 件普通商店物品
        Assert.Empty(run.SelectionSet);
        Assert.DoesNotContain(evs, e => e is GameSimEventCardDealt);
        Assert.False(run.SelectionRules.CanSelectMultiple);
        Assert.True(run.SelectionRules.SelectionIsFree);
        Assert.True(run.SelectionRules.CanExit);
    }

    [Fact]
    public void ShopEngine_RealNonMerchantEventEncounter_ShouldDealEncounterStepNotItem()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa);
        var shop = new ShopEngine(run, pool, repo);

        // 002d8f6d-0e36-4921-89ed-3f0f28682d7b: The Travel Agent (非商人 EventEncounter)
        const string travelAgentId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";
        shop.DealEncounterShelf(travelAgentId, isReroll: false);

        Assert.NotEmpty(run.SelectionSet);
        foreach (string id in run.SelectionSet)
        {
            var entry = run.Cards[id];
            Assert.Equal(ECardType.EncounterStep, entry.Type);
            Assert.True(pool.IsAssetValid(entry.TemplateId.ToString()));
        }
    }

    [Fact]
    public void ShopEngine_DealHeroStarterEncounter_ShouldCoverAllPlayableHeroesWithValidAssets()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak, EHero.Stelle, EHero.Jules })
        {
            // 官方 1.0.13122 开局流程：Day 1 Hour 1 直接进入 Choice（3 个青铜白昼遭遇，无强制 0 数值起手技能）
            var run = NewTestRun(hero, gold: 8, day: 1, hour: 1);
            var shop = new ShopEngine(run, pool, repo);
            var evs = shop.DealHeroStarterEncounter(allowEventPackages: true, count: 3);

            Assert.Equal(ERunState.Choice, run.State);
            Assert.Null(run.CurrentEncounterId);
            Assert.False(run.SelectionRules.CanExit);
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.Contains(evs, e => e is GameSimEventCardDealt);
            foreach (string cid in run.SelectionSet)
            {
                var entry = run.Cards[cid];
                Assert.Equal(ECardType.EventEncounter, entry.Type);
                var encTpl = pool.Get(entry.TemplateId.ToString());
                bool isOfficialDay1Exempt = encTpl != null
                    && OfficialEncounterSchedule.TryGetRule(encTpl, out var day1Rule)
                    && day1Rule != null
                    && day1Rule.ExemptFromMaxDayTier
                    && OfficialEncounterSchedule.IsWithinWindow(encTpl, 1, 1, hero.ToString());
                // Patch 20.2 官方 Day 1 开放 Bronze/Silver 及实测合法 Day 1 豁免事件（如 Wishing Fountain / Finn's Big Bite / Piano），严禁其他 Gold/Diamond/Legendary 越阶泄露
                Assert.True(
                    entry.Tier is ETier.Bronze or ETier.Silver || isOfficialDay1Exempt,
                    $"英雄 {hero} Day 1 Hour 1 遭遇 {entry.TemplateId} ({encTpl?.InternalName}) 品阶={entry.Tier} 违规越阶");
                Assert.True(pool.IsAssetValid(entry.TemplateId.ToString()), $"英雄 {hero} 开局遭遇 {entry.TemplateId} 未通过白名单");
            }

            // 单独验证保留的 DealStarterSkills：白名单过滤 + 高起步阶技能夹紧后 Custom_0 等核心属性有效
            var skillRun = NewTestRun(hero, gold: 8, day: 1, hour: 1);
            var skillShop = new ShopEngine(skillRun, pool, repo);
            var skillEvs = skillShop.DealStarterSkills(count: 3);

            Assert.Equal(ERunState.Encounter, skillRun.State);
            Assert.True(pool.IsAssetValid(skillRun.CurrentEncounterId), $"英雄 {hero} 开局房间 {skillRun.CurrentEncounterId} 未通过白名单");
            Assert.NotEmpty(skillRun.SelectionSet);
            Assert.Contains(skillEvs, e => e is GameSimEventCardDealt);
            foreach (string cid in skillRun.SelectionSet)
            {
                var skEntry = skillRun.Cards[cid];
                Assert.Equal(ECardType.Skill, skEntry.Type);
                Assert.True(pool.IsAssetValid(skEntry.TemplateId.ToString()));
                var coreTpl = repo.GetCard(skEntry.TemplateId.ToString());
                if (coreTpl != null)
                {
                    var minValidTier = BazaarCore.Engine.SpawnQueryEngine.ClampToSupportedTier(coreTpl, CardTier.Bronze);
                    Assert.True(ShopEngine.ToCoreTier(skEntry.Tier) >= minValidTier,
                        $"技能 {skEntry.TemplateId} tier={skEntry.Tier} 低于最低有效阶级 {minValidTier}");
                    if (coreTpl.Tiers != null &&
                        coreTpl.Tiers.TryGetValue(ShopEngine.ToCoreTier(skEntry.Tier).ToString(), out var td) &&
                        td.Attributes.TryGetValue("Custom_0", out double expectedC0) &&
                        expectedC0 > 0)
                    {
                        Assert.True(skEntry.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 0) > 0,
                            $"技能 {skEntry.TemplateId} Custom_0 不应为 0");
                    }
                }
            }
        }
    }

    [Fact]
    public void EncounterStepEngine_MultiBranchAttributeModify_ShouldUpdatePlayerAndEmitLogs()
    {
        const string stepTplId = "88888888-1111-1111-1111-111111111101";
        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = stepTplId,
            InternalName = "Gain HP and Income Step",
            Type = "EncounterStep",
            ShouldProgressHour = true,
            ExperienceAwardUponSelection = 2,
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab1"] = new AbilityDefinition
                {
                    Id = "ab1",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode
                            {
                                Type = "TActionPlayerModifyAttribute",
                                AttributeType = "HealthMax",
                                Operation = "Add",
                                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                                RawValueNode = JsonFixed(50),
                            },
                            new RuleNode
                            {
                                Type = "TActionPlayerModifyAttribute",
                                AttributeType = "Income",
                                Operation = "Add",
                                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                                RawValueNode = JsonFixed(2),
                            },
                        ],
                    },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Pygmalien, gold: 15);
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(1));

        var stepEntry = new RunState.CardEntry
        {
            InstanceId = "stp_hp_inc",
            TemplateId = Guid.Parse(stepTplId),
            Type = ECardType.EncounterStep,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
        };
        run.Cards[stepEntry.InstanceId] = stepEntry;
        run.SelectionSet.Add(stepEntry.InstanceId);

        var res = stepEngine.ExecuteStep(stepEntry);
        Assert.True(res.Success);
        Assert.Equal(350, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(7, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(2, res.ExperienceAwarded);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.True(res.ShouldProgressHour);
        Assert.Contains(res.Events, e => e is GameSimEventPlayerIncomeGained);
    }

    [Fact]
    public void EncounterStepEngine_PaidStep_ShouldRejectInsufficientGold_AndDeductWhenAffordable()
    {
        const string paidStepId = "88888888-1111-1111-1111-111111111102";
        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = paidStepId,
            InternalName = "Paid Training",
            Type = "EncounterStep",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab1"] = new AbilityDefinition
                {
                    Id = "ab1",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "HealthMax",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(40),
                    },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa, gold: 4);
        run.SelectionRules = new TSelectionContextRules { SelectionIsFree = false, CanExit = true };
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(2));

        var entry = new RunState.CardEntry
        {
            InstanceId = "stp_paid",
            TemplateId = Guid.Parse(paidStepId),
            Type = ECardType.EncounterStep,
        };
        entry.Attributes[ECardAttributeType.BuyPrice] = 6;
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);

        var fail = stepEngine.ExecuteStep(entry);
        Assert.False(fail.Success);
        Assert.Equal(4, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(300, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);

        run.PlayerAttributes[EPlayerAttributeType.Gold] = 10;
        var ok = stepEngine.ExecuteStep(entry);
        Assert.True(ok.Success);
        Assert.Equal(6, ok.GoldSpent);
        Assert.Equal(4, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(340, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
    }

    [Fact]
    public void EncounterStepEngine_SpawnCardAndSelectionRequirements_ShouldWork()
    {
        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        const string reqStepId = "88888888-1111-1111-1111-111111111103";

        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = reqStepId,
            InternalName = "Gold Gate Spawn",
            Type = "EncounterStep",
            SelectionRequirements = new RuleNode
            {
                Type = "TPrerequisitePlayer",
                Subject = new RuleNode
                {
                    Type = "TTargetPlayerRelative",
                    TargetMode = "Self",
                    RawConditionsElement = JsonDocument.Parse("{\"$type\":\"TPlayerConditionalAttribute\",\"Attribute\":\"Gold\",\"ComparisonOperator\":\"GreaterThanOrEqual\",\"ComparisonValue\":{\"$type\":\"TFixedValue\",\"Value\":15}}").RootElement.Clone(),
                },
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab1"] = new AbilityDefinition
                {
                    Id = "ab1",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionGameSpawnCards",
                        SpawnContext = new RuleNode
                        {
                            Type = "TSpawnContextQuery",
                            SelectionMethod = "Sequential",
                            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                            Groups =
                            [
                                new RuleNode
                                {
                                    Type = "TSpawnGroup",
                                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [gumballId] }],
                                    SelectionMethod = "Sequential",
                                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                                },
                            ],
                        },
                    },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa, gold: 10);
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(3));

        var entry = new RunState.CardEntry
        {
            InstanceId = "stp_req",
            TemplateId = Guid.Parse(reqStepId),
            Type = ECardType.EncounterStep,
        };
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);

        // Gold=10 < 15 应被前置条件拦截
        Assert.False(stepEngine.ExecuteStep(entry).Success);

        run.PlayerAttributes[EPlayerAttributeType.Gold] = 20;
        var res = stepEngine.ExecuteStep(entry);
        Assert.True(res.Success);
        Assert.Single(res.SpawnedCards);
        Assert.Equal(Guid.Parse(gumballId), res.SpawnedCards[0].TemplateId);
        Assert.Contains(res.Events, e => e is GameSimEventCardSpawned);
    }

    [Fact]
    public void EncounterStepEngine_ChainedNextEncounter_ShouldSupportSubEventPedestalAndCombat()
    {
        var pool = Pool.Value;
        string validSubEvent = pool.OfType("EventEncounter").First().Id;
        string validPedestal = pool.OfType("PedestalEncounter").First().Id;
        string validCombat = pool.OfType("CombatEncounter").First().Id;

        const string step1Id = "88888888-1111-1111-1111-111111111104";
        const string step2Id = "88888888-1111-1111-1111-111111111105";
        const string step3Id = "88888888-1111-1111-1111-111111111106";

        var repo = CreateRepoWithExtraCards(
            new CardTemplate
            {
                Id = step1Id,
                InternalName = "Chain -> SubEvent",
                Type = "EncounterStep",
                NextEncounterOnSelection = [new NextEncounterOption { Id = validSubEvent, Weight = 10 }],
            },
            new CardTemplate
            {
                Id = step2Id,
                InternalName = "Chain -> Pedestal",
                Type = "EncounterStep",
                NextEncounterOnSelection = [new NextEncounterOption { Id = validPedestal, Weight = 10 }],
            },
            new CardTemplate
            {
                Id = step3Id,
                InternalName = "Chain -> Combat",
                Type = "EncounterStep",
                NextEncounterOnSelection = [new NextEncounterOption { Id = validCombat, Weight = 10 }],
            });

        var run = NewTestRun(EHero.Dooley, gold: 20);
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(4));

        var e1 = new RunState.CardEntry { InstanceId = "stp_1", TemplateId = Guid.Parse(step1Id), Type = ECardType.EncounterStep };
        run.Cards[e1.InstanceId] = e1;
        var r1 = stepEngine.ExecuteStep(e1);
        Assert.True(r1.Success);
        Assert.True(r1.TransitionsToSubEncounter);
        Assert.Equal(validSubEvent, r1.NextEncounterId);
        Assert.Equal(ECardType.EventEncounter, r1.NextEncounterType);
        Assert.False(r1.ShouldProgressHour);

        var e2 = new RunState.CardEntry { InstanceId = "stp_2", TemplateId = Guid.Parse(step2Id), Type = ECardType.EncounterStep };
        run.Cards[e2.InstanceId] = e2;
        var r2 = stepEngine.ExecuteStep(e2);
        Assert.True(r2.Success);
        Assert.True(r2.TransitionsToPedestal);
        Assert.Equal(validPedestal, r2.NextEncounterId);
        Assert.Equal(ECardType.PedestalEncounter, r2.NextEncounterType);

        var e3 = new RunState.CardEntry { InstanceId = "stp_3", TemplateId = Guid.Parse(step3Id), Type = ECardType.EncounterStep };
        run.Cards[e3.InstanceId] = e3;
        var r3 = stepEngine.ExecuteStep(e3);
        Assert.True(r3.Success);
        Assert.True(r3.TriggersCombat);
        Assert.Equal(validCombat, r3.NextEncounterId);
        Assert.Equal(ECardType.CombatEncounter, r3.NextEncounterType);
    }

    [Fact]
    public void EncounterStepEngine_DealCardsAction_ShouldReplaceShelfWithValidCards()
    {
        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        const string chocoId = "1a3d4cde-333c-4936-b147-263d73f6d200";
        const string dealStepId = "88888888-1111-1111-1111-111111111107";

        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = dealStepId,
            InternalName = "Deal New Options",
            Type = "EncounterStep",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_deal"] = new AbilityDefinition
                {
                    Id = "ab_deal",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionGameDealCards",
                        SpawnMode = "Replace",
                        SpawnContext = new RuleNode
                        {
                            Type = "TSpawnContextQuery",
                            Groups =
                            [
                                new RuleNode
                                {
                                    Type = "TSpawnGroup",
                                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [gumballId, chocoId] }],
                                    SelectionMethod = "Sequential",
                                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(2) },
                                },
                            ],
                        },
                    },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa, gold: 20);
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(5));

        var e = new RunState.CardEntry { InstanceId = "stp_deal", TemplateId = Guid.Parse(dealStepId), Type = ECardType.EncounterStep };
        run.Cards[e.InstanceId] = e;
        run.SelectionSet.Add(e.InstanceId);

        var res = stepEngine.ExecuteStep(e);
        Assert.True(res.Success);
        Assert.True(res.DealtNewCards);
        Assert.False(res.ShouldProgressHour);
        Assert.Equal(2, res.DealtCards.Count);
        Assert.Equal(2, run.SelectionSet.Count);
    }

    [Fact]
    public void MonsterVictoryLoot_ShouldSupportMonsterExclusiveSkillsAndItems()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var monsters = Monsters.Value;

        // 035ace94-61e5-4343-8383-83044ea4e08e: Haunted Kimono (有专属 Skill)
        var combatTpl = pool.Get("035ace94-61e5-4343-8383-83044ea4e08e");
        Assert.NotNull(combatTpl);
        var monster = monsters.FirstOrDefault(m => m.Id == combatTpl!.MonsterTemplateId);
        Assert.NotNull(monster);
        Assert.NotEmpty(monster!.Skills);

        // 也验证只带技能的怪物可稳定爆出 ECardType.Skill 圆形技能卡
        var skillOnlyMonster = new BazaarServer.Data.MonsterTemplate
        {
            Id = "skill_only_m",
            InternalName = "Skill Master",
            HealthMax = 300,
            Level = 2,
            Skills = monster.Skills.Where(s => pool.IsAssetValid(s.TemplateId)).ToList(),
        };
        Assert.NotEmpty(skillOnlyMonster.Skills);

        var run = NewTestRun(EHero.Vanessa, day: 2, hour: 3);
        var shop = new ShopEngine(run, pool, repo);
        var evs = shop.DealMonsterVictoryLoot(combatTpl, skillOnlyMonster);

        Assert.Equal(ERunState.Loot, run.State);
        Assert.NotEmpty(run.SelectionSet);
        Assert.Contains(run.SelectionSet, id => run.Cards[id].Type == ECardType.Skill);
        Assert.Contains(evs, e => e is GameSimEventCardDealt d && d.Type == ECardType.Skill);

        // 也验证真实带物品+技能混合掉落均通过白名单
        var run2 = NewTestRun(EHero.Vanessa, day: 2, hour: 3);
        var shop2 = new ShopEngine(run2, pool, repo);
        shop2.DealMonsterVictoryLoot(combatTpl, monster);
        Assert.NotEmpty(run2.SelectionSet);
        foreach (string id in run2.SelectionSet)
        {
            Assert.True(pool.IsAssetValid(run2.Cards[id].TemplateId.ToString()));
        }
    }

    [Theory]
    [InlineData(1000, 1000, 10, 4, 1, 1, 0.0, 1, 1)]   // 0% 伤血：仅得基础战败保底奖励
    [InlineData(1000, 500, 10, 4, 1, 1, 0.5, 6, 3)]    // 50% 伤血：5 + 1 = 6 金币，2 + 1 = 3 经验
    [InlineData(1000, 200, 10, 4, 1, 1, 0.8, 9, 4)]    // 80% 伤血：8 + 1 = 9 金币，Floor(3.2) + 1 = 4 经验
    public void CalculateMonsterDefeatRewards_ShouldScaleGoldAndXpByDamageRatio(
        int maxHp,
        int remHp,
        int combatGold,
        int combatXp,
        int defeatGold,
        int defeatXp,
        double expectedRatio,
        int expectedGold,
        int expectedXp)
    {
        var res = ShopEngine.CalculateMonsterDefeatRewards(
            maxHp, remHp, combatGold, combatXp, defeatGold, defeatXp);

        Assert.Equal(expectedRatio, res.DamageRatio, precision: 3);
        Assert.Equal(expectedGold, res.GoldAwarded);
        Assert.Equal(expectedXp, res.XpAwarded);
    }

    [Fact]
    public void MonsterPool_ShouldLoadSkillsAndStashItemsFromGameData()
    {
        var monsters = Monsters.Value;
        Assert.True(monsters.Count >= 170);
        Assert.True(monsters.Count(m => m.Skills.Count > 0) >= 150, "应解析出野怪专属圆形技能");
        Assert.Contains(monsters, m => m.AllLootCandidates().Any());
    }

    [Fact]
    public void EncounterStepEngine_TransformAction_ShouldEmitCardTransformedAndReplaceOwnedCard()
    {
        const string srcTplId = "6d6199b4-82a4-441d-9329-f4164737ac6b"; // Red Gumball
        const string dstTplId = "1a3d4cde-333c-4936-b147-263d73f6d200"; // Chocolate Bar
        const string transStepId = "88888888-1111-1111-1111-111111111108";

        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = transStepId,
            InternalName = "Transform Owned Step",
            Type = "EncounterStep",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_tr"] = new AbilityDefinition
                {
                    Id = "ab_tr",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardTransform",
                        Target = new RuleNode { Type = "TTargetCardXMost", TargetSection = "SelfHand", TargetMode = "LeftMost" },
                        SpawnContext = new RuleNode
                        {
                            Type = "TSpawnContextQuery",
                            Groups =
                            [
                                new RuleNode
                                {
                                    Type = "TSpawnGroup",
                                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [dstTplId] }],
                                    SelectionMethod = "Sequential",
                                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                                },
                            ],
                        },
                    },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa, gold: 20);
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(6));

        var srcCore = new CardInstance(repo.GetCard(srcTplId)!, CardTier.Bronze, null, "owned_gumball");
        Assert.True(run.HandGrid.TryPlaceOrShift(srcCore, 3));
        run.Cards[srcCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = srcCore.InstanceId,
            TemplateId = Guid.Parse(srcTplId),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = srcCore,
        };

        var stepEntry = new RunState.CardEntry { InstanceId = "stp_tr", TemplateId = Guid.Parse(transStepId), Type = ECardType.EncounterStep };
        run.Cards[stepEntry.InstanceId] = stepEntry;
        run.SelectionSet.Add(stepEntry.InstanceId);

        var res = stepEngine.ExecuteStep(stepEntry);
        Assert.True(res.Success);
        Assert.Single(res.TransformedCards);
        Assert.Equal(Guid.Parse(dstTplId), res.TransformedCards[0].TemplateId);
        Assert.Contains(res.Events, e => e is GameSimEventCardTransformed);
        Assert.False(run.Cards.ContainsKey("owned_gumball"));
    }

    [Fact]
    public void EncounterStepEngine_RerollAndExitActions_ShouldSetFlagsAndExit()
    {
        const string exitStepId = "88888888-1111-1111-1111-111111111109";
        var repo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = exitStepId,
            InternalName = "Exit Step",
            Type = "EncounterStep",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_exit"] = new AbilityDefinition
                {
                    Id = "ab_exit",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSelected" },
                    Action = new RuleNode { Type = "TActionExitReplacementSet" },
                },
            },
        });
        var pool = Pool.Value;
        var run = NewTestRun(EHero.Vanessa, gold: 20);
        run.CurrentEncounterId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";
        var shop = new ShopEngine(run, pool, repo);
        var stepEngine = new EncounterStepEngine(run, pool, repo, shop, new Random(7));

        var e = new RunState.CardEntry { InstanceId = "stp_exit", TemplateId = Guid.Parse(exitStepId), Type = ECardType.EncounterStep };
        run.Cards[e.InstanceId] = e;
        run.SelectionSet.Add(e.InstanceId);

        var res = stepEngine.ExecuteStep(e);
        Assert.True(res.Success);
        Assert.True(res.ExitedEncounter);
        Assert.Empty(run.SelectionSet);
    }

    [Fact]
    public void ShopEngine_DealHourEncounters_ShouldCoverDaytimeMonstersAndPvp()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        var runH1 = NewTestRun(EHero.Vanessa, day: 2, hour: 1);
        var shopH1 = new ShopEngine(runH1, pool, repo);
        shopH1.DealHourEncounters();
        Assert.Equal(ERunState.Choice, runH1.State);
        Assert.Equal(3, runH1.SelectionSet.Count);
        foreach (string id in runH1.SelectionSet)
            Assert.True(pool.IsAssetValid(runH1.Cards[id].TemplateId.ToString()));

        var runH3 = NewTestRun(EHero.Vanessa, day: 2, hour: 3);
        var shopH3 = new ShopEngine(runH3, pool, repo);
        shopH3.DealHourEncounters();
        Assert.Equal(3, runH3.SelectionSet.Count);
        Assert.All(runH3.SelectionSet, id => Assert.Equal(ECardType.CombatEncounter, runH3.Cards[id].Type));

        var runH6 = NewTestRun(EHero.Vanessa, day: 2, hour: 6);
        var shopH6 = new ShopEngine(runH6, pool, repo);
        shopH6.DealHourEncounters();
        Assert.Single(runH6.SelectionSet);
        Assert.Equal(ECardType.PvpEncounter, runH6.Cards[runH6.SelectionSet[0]].Type);
    }
}
