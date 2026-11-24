using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
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
/// V7 Official Parity 测试套件：
/// 1. [2.2] DealHourEncounters 跨小时每日去重与池耗尽回退
/// 2. [3.4] 商店拒绝低于玩家已持有最高品级或已达 Diamond 的重复物品
/// 3. [3.5 & 3.6] 商人 RerollRules 与 DB 对齐、单品 25% 绿色折扣标签及 Reroll 重刷
/// 4. [2.5, 2.6 & GuidOnly] MaxSelections 映射、空池免费发牌金币兜底与显式 GuidOnly 白名单（同时保持 Never 禁止）
/// 5. [2.8] Travel Agent / 远征门票检测、房间映射与日初远征触发/消耗/选房辅助 API
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V7ShopExpeditionTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static GameDataRepository CreateRepoWithExtraCards(params CardTemplate[] extras)
    {
        var baseRepo = Repo.Value;
        var cards = new Dictionary<string, CardTemplate>(baseRepo.CardsById, StringComparer.OrdinalIgnoreCase);
        foreach (var ex in extras) cards[ex.Id] = ex;
        return new GameDataRepository(
            baseRepo.SourceZipPath,
            baseRepo.GameMode,
            cards,
            new Dictionary<string, BazaarCore.Models.MonsterTemplate>(baseRepo.MonstersById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, LevelUpTemplate>(baseRepo.LevelUps),
            new Dictionary<string, string>(baseRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(baseRepo.ObservedRuleTypes, StringComparer.Ordinal),
            new HashSet<string>(baseRepo.UnknownRuleTypes, StringComparer.Ordinal),
            baseRepo.Localization,
            baseRepo.SourceDataPath);
    }

    private static RunState NewTestRun(EHero hero = EHero.Vanessa, int gold = 100, uint day = 2, uint hour = 1)
    {
        var run = new RunState
        {
            RunId = "v7_test_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = gold;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 400;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 400;
        run.UnlockedSlots = 0b1111111111;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 0b1111111111;
        return run;
    }

    private static RunState.CardEntry AddItemToRun(
        RunState run,
        GameDataRepository repo,
        string templateId,
        CardTier tier,
        EInventorySection section = EInventorySection.Hand,
        int slot = 0,
        string? instanceId = null)
    {
        var tpl = repo.GetCard(templateId) ?? throw new InvalidOperationException($"Missing template: {templateId}");
        var id = instanceId ?? $"itm_{Guid.NewGuid():N}"[..12];
        var core = new CardInstance(tpl, tier, null, id);
        bool placed = section == EInventorySection.Stash
            ? run.StashGrid.TryPlaceOrShift(core, slot)
            : run.HandGrid.TryPlaceOrShift(core, slot);
        Assert.True(placed, $"无法将 {templateId} ({tier}) 放入 {section} 槽位 {slot}");

        var eSize = tpl.Size?.ToLowerInvariant() switch
        {
            "large" => ECardSize.Large,
            "medium" => ECardSize.Medium,
            _ => ECardSize.Small,
        };
        var eTier = tier switch
        {
            CardTier.Silver => ETier.Silver,
            CardTier.Gold => ETier.Gold,
            CardTier.Diamond => ETier.Diamond,
            CardTier.Legendary => ETier.Legendary,
            _ => ETier.Bronze,
        };
        var entry = new RunState.CardEntry
        {
            InstanceId = id,
            TemplateId = Guid.Parse(templateId),
            Type = ShopEngine.ParseCardType(tpl.Type),
            Size = eSize,
            Tier = eTier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)slot,
            CoreCard = core,
        };
        run.Cards[id] = entry;
        return entry;
    }

    private static JsonElement JsonNumber(double val)
        => JsonDocument.Parse(val.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    private static HashSet<string> BuildValidAssetSet(CardPool pool, GameDataRepository repo)
        => repo.CardsById.Keys.Where(pool.IsAssetValid).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ── Task 1 [2.2]: DealHourEncounters 跨小时每日去重 ────────────────────

    [Fact]
    public void Task1_DealHourEncounters_ShouldDeduplicateWithinSameDay_AndFallbackWhenExhausted()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;
        var visitedToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 在同一 Day 连续 3 个非怪时段 (Day 3: Hour 1 -> Hour 2 -> Hour 4) 抽取，应保持 3 槽且不重复
        uint[] encounterHours = [1, 2, 4];
        foreach (uint hr in encounterHours)
        {
            var run = NewTestRun(EHero.Vanessa, day: 3, hour: hr);
            var shop = new ShopEngine(run, pool, repo);
            var player = new PlayerState("p1", "Vanessa", "Vanessa", 400, 30, 8, 5)
            {
                Day = 3,
                Hour = (int)hr,
            };

            shop.DealHourEncounters(player, visitedToday);
            Assert.Equal(3, run.SelectionSet.Count);

            var dealtIds = run.SelectionSet
                .Select(instId => run.Cards[instId].TemplateId.ToString())
                .ToList();

            foreach (string tplId in dealtIds)
            {
                Assert.DoesNotContain(tplId, visitedToday);
                visitedToday.Add(tplId);
            }
        }

        // 将所有 MerchantEncounter / EventEncounter 加入 visitedToday 模拟当日候选耗尽，仍应优雅回退并维持 3 槽
        var allDailyIds = pool.OfType("MerchantEncounter")
            .Concat(pool.OfType("EventEncounter"))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exhaustedRun = NewTestRun(EHero.Vanessa, day: 3, hour: 1);
        var exhaustedShop = new ShopEngine(exhaustedRun, pool, repo);
        exhaustedShop.DealHourEncounters(new PlayerState("p1", "Vanessa", "Vanessa", 400, 30, 8, 5) { Day = 3, Hour = 1 }, allDailyIds);
        Assert.Equal(3, exhaustedRun.SelectionSet.Count);

        // 验证无参原有重载仍保持完全兼容
        var legacyRun = NewTestRun(EHero.Vanessa, day: 2, hour: 1);
        var legacyShop = new ShopEngine(legacyRun, pool, repo);
        legacyShop.DealHourEncounters();
        Assert.Equal(3, legacyRun.SelectionSet.Count);
    }

    // ── Task 2 [3.4]: 商店不发 Diamond 或低于已持有品级的重复物品 ────────────

    [Fact]
    public void Task2_ShopAndSpawn_ShouldExcludeDiamondOwnedAndLowerTierDuplicates()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b"; // Red Gumball (Bronze起)
        const string cutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1"; // Cutlass (Vanessa Bronze起)
        const string chocoId = "1a3d4cde-333c-4936-b147-263d73f6d200";   // Chocolate Bar

        // 2a. SpawnQueryEngine 直接约束测试：Gold 持有仅可出 >= Gold，Diamond 持有完全屏蔽
        var queryAllThree = new RuleNode
        {
            Type = "TSpawnContextQuery",
            SelectionMethod = "Random",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(3) },
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [cutlassId, gumballId, chocoId] }],
                    SelectionMethod = "Random",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(3) },
                },
            ],
        };

        var player = new PlayerState("p1", "Vanessa", "Vanessa", 400, 50, 8, 5) { Day = 1, Hour = 1 };
        var ctxDay1 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(17))
        {
            CurrentDay = 1,
            CurrentHour = 1,
        };
        var ownedConstraint = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase)
        {
            [cutlassId] = CardTier.Diamond, // 已满级：绝不应出现
            [gumballId] = CardTier.Gold,    // Day 1 只滚 Bronze/Silver，不得出低品级
        };

        var validAssets = BuildValidAssetSet(pool, repo);
        for (int i = 0; i < 15; i++)
        {
            var spawned = SpawnQueryEngine.SpawnCards(queryAllThree, ctxDay1, ownedMaxTiers: ownedConstraint, validAssetIds: validAssets);
            Assert.NotEmpty(spawned);
            Assert.All(spawned, c => Assert.Equal(chocoId, c.Template.Id));
        }

        // Day 10 时允许滚到 Gold / Diamond：只屏蔽 Diamond 的 cutlassId，gumball 若被选中其品级必须 >= Gold
        var ctxDay10 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(29))
        {
            CurrentDay = 10,
            CurrentHour = 1,
        };
        bool sawGoldOrHigherGumball = false;
        for (int i = 0; i < 30; i++)
        {
            var spawned = SpawnQueryEngine.SpawnCards(queryAllThree, ctxDay10, ownedMaxTiers: ownedConstraint, validAssetIds: validAssets);
            Assert.DoesNotContain(spawned, c => c.Template.Id == cutlassId);
            foreach (var c in spawned.Where(c => c.Template.Id == gumballId))
            {
                Assert.True(c.Tier >= CardTier.Gold, $"已有 Gold 持有时不应生成 {c.Tier}");
                sawGoldOrHigherGumball = true;
            }
        }
        Assert.True(sawGoldOrHigherGumball, "Day 10 高品级概率下应允许出现 Gold/Diamond 同级或升级品以供合成");

        // 2b. CardPool.RollShopItems + ShopEngine.BuildOwnedMaxTiers + DealEncounterShelf 端到端约束
        var vanessaShopItems = pool.RollShopItems("Vanessa", 1, 10, new TierChances { CurrentDay = 1 });
        Assert.True(vanessaShopItems.Count >= 4);
        string blockedShopId = vanessaShopItems[0].Card.Id;
        string goldShopId = vanessaShopItems[1].Card.Id;

        var shopOwnedMap = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase)
        {
            [blockedShopId] = CardTier.Diamond,
            [goldShopId] = CardTier.Gold,
        };

        for (int i = 0; i < 12; i++)
        {
            var rolled = pool.RollShopItems("Vanessa", 1, 8, new TierChances { CurrentDay = 1 }, shopOwnedMap);
            Assert.DoesNotContain(rolled, r => r.Card.Id == blockedShopId);
            Assert.All(
                rolled.Where(r => r.Card.Id == goldShopId),
                r => Assert.True(Enum.TryParse<CardTier>(r.Tier, true, out var rolledTier) && rolledTier >= CardTier.Gold));
        }

        // BuildOwnedMaxTiers 自动取手牌+背包同名卡最高品级并接入自定义商店货架
        const string testMerchantId = "99990001-1111-1111-1111-111111111101";
        var customRepo = CreateRepoWithExtraCards(new CardTemplate
        {
            Id = testMerchantId,
            InternalName = "V7 Owned Tier Filter Merchant",
            Type = "MerchantEncounter",
            StartingTier = "Bronze",
            SelectionContext = new SelectionContextData
            {
                SpawnContext = new RuleNode
                {
                    Type = "TSpawnContextQuery",
                    SelectionMethod = "Random",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(3) },
                    Groups =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnGroup",
                            Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [cutlassId, gumballId, chocoId] }],
                            SelectionMethod = "Random",
                            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(3) },
                        },
                    ],
                },
                Rules = new SelectionRulesData { SelectionIsFree = false, CanExit = true },
            },
        });

        var run = NewTestRun(EHero.Vanessa, gold: 100, day: 1, hour: 1);
        AddItemToRun(run, customRepo, cutlassId, CardTier.Bronze, EInventorySection.Hand, slot: 0);
        AddItemToRun(run, customRepo, cutlassId, CardTier.Diamond, EInventorySection.Stash, slot: 0);
        AddItemToRun(run, customRepo, gumballId, CardTier.Gold, EInventorySection.Hand, slot: 3);

        var derivedOwned = ShopEngine.BuildOwnedMaxTiers(run);
        Assert.Equal(CardTier.Diamond, derivedOwned[cutlassId]);
        Assert.Equal(CardTier.Gold, derivedOwned[gumballId]);

        var shop = new ShopEngine(run, pool, customRepo);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            shop.DealEncounterShelf(testMerchantId, isReroll: attempt > 0);
            var shelfTplIds = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
            Assert.DoesNotContain(cutlassId, shelfTplIds);
            Assert.DoesNotContain(gumballId, shelfTplIds); // Day 1 无法发出 >= Gold 的 gumball
            Assert.Contains(chocoId, shelfTplIds);
        }
    }

    // ── Task 3 [3.5 & 3.6]: DB RerollRules 对齐 & 单品 25% 折扣绿标 ─────────

    [Theory]
    [InlineData("065f6793-8607-4f02-abfe-db6afb221ab2", 3u, 1u)]   // Pol (Silver): 3g / 1 reroll
    [InlineData("0ff275f7-60ef-4428-acb6-1a138d03152d", 3u, 1u)]   // Eli (Silver): 3g / 1 reroll
    [InlineData("1a21931f-6183-4251-914b-c8f83dc58a30", 3u, 1u)]   // Nautica (Silver): 3g / 1 reroll
    [InlineData("1f72700a-fe11-4792-8a47-9e52b9387f29", 4u, 1u)]   // Goldie (Gold): 4g / 1 reroll
    [InlineData("210003fa-a20d-4ad1-9ada-d5fad5bf9f89", 6u, 1u)]   // Nufu (Gold Skill): 6g / 1 reroll
    [InlineData("054e937d-553e-41a5-951b-62550f870f93", 8u, 1u)]   // Zosima (Diamond): 8g / 1 reroll
    [InlineData("dbdf69f8-9bea-40e7-9098-209c8c475971", 1u, 999u)] // Pearl's Dig Site: null TotalAllowedRerolls -> 999 (unlimited)
    [InlineData("f1302d1d-24e4-4d17-a434-3550af908ba3", 1u, 50u)]  // Aerodrome: 1g / 50 rerolls
    public void Task3_MerchantRerollRules_ShouldMatchGameDataDb(
        string encounterId,
        uint expectedStartCost,
        uint expectedAllowedRerolls)
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        var dbRules = pool.GetEncounterRerollRules(encounterId);
        Assert.NotNull(dbRules);
        var resolved = ShopEngine.ResolveRerollSettings(dbRules);
        Assert.Equal(expectedStartCost, resolved.StartingCost);
        Assert.Equal(expectedAllowedRerolls, resolved.RerollsRemaining);

        var run = NewTestRun(EHero.Vanessa, gold: 100, day: 5, hour: 1);
        var shop = new ShopEngine(run, pool, repo);
        shop.DealEncounterShelf(encounterId, isReroll: false);

        Assert.Equal(expectedStartCost, run.RerollCost);
        Assert.Equal(expectedAllowedRerolls, run.RerollsRemaining);
        Assert.NotNull(run.SelectionRules.RerollRules);
        Assert.Equal((int)expectedStartCost, run.SelectionRules.RerollRules!.StartingCost);
        if (encounterId == "dbdf69f8-9bea-40e7-9098-209c8c475971")
            Assert.Null(run.SelectionRules.RerollRules.TotalAllowedRerolls);
        else
            Assert.Equal((int)expectedAllowedRerolls, run.SelectionRules.RerollRules.TotalAllowedRerolls);
    }

    [Fact]
    public void Task3_MerchantGreenTagDiscount_ShouldBe25PercentOffAround12PercentChance_AndRefreshOnReroll()
    {
        Assert.Equal(0.12f, ShopEngine.MerchantItemDiscountChance);
        Assert.Equal(25, ShopEngine.MerchantItemDiscountPercent);
        Assert.Equal(1, ShopEngine.ComputeDiscountedBuyPrice(1));
        Assert.Equal(2, ShopEngine.ComputeDiscountedBuyPrice(2)); // round(1.5) = 2
        Assert.Equal(2, ShopEngine.ComputeDiscountedBuyPrice(3)); // round(2.25) = 2
        Assert.Equal(3, ShopEngine.ComputeDiscountedBuyPrice(4)); // round(3.0) = 3
        Assert.Equal(6, ShopEngine.ComputeDiscountedBuyPrice(8)); // round(6.0) = 6

        var pool = Pool.Value;
        var repo = Repo.Value;
        const string polId = "065f6793-8607-4f02-abfe-db6afb221ab2"; // Pol: 非免费 Large Item 商人

        var run = NewTestRun(EHero.Vanessa, gold: 500, day: 5, hour: 1);
        var shop = new ShopEngine(run, pool, repo);

        int totalDealtItems = 0;
        int discountedItems = 0;
        HashSet<string>? prevDiscountIds = null;

        for (int round = 0; round < 160; round++)
        {
            bool isReroll = round > 0;
            shop.DealEncounterShelf(polId, isReroll: isReroll);

            // Reroll 必须清空上一轮的货架折扣记录
            if (prevDiscountIds != null)
            {
                foreach (string oldId in prevDiscountIds)
                {
                    Assert.False(shop.ActiveShelfDiscounts.ContainsKey(oldId), "旧折扣实例未随 Reroll 清除");
                }
            }

            foreach (string instId in run.SelectionSet)
            {
                totalDealtItems++;
                var entry = run.Cards[instId];
                if (shop.TryGetShelfDiscount(instId, out var info) && info != null)
                {
                    discountedItems++;
                    Assert.Equal(25, info.DiscountPercent);
                    Assert.Equal(0.75, info.PriceMultiplier);
                    Assert.Equal(
                        Math.Max(1, (int)Math.Round(info.BasePrice * 0.75)),
                        info.DiscountedPrice);
                    Assert.Equal(info.DiscountedPrice, entry.Attributes[ECardAttributeType.BuyPrice]);
                    var coreCard = entry.CoreCard;
                    Assert.NotNull(coreCard);
                    Assert.Equal(0.75, coreCard.HourMult.GetValueOrDefault("BuyPrice", 1.0));
                }
            }

            prevDiscountIds = shop.ActiveShelfDiscounts.Keys.ToHashSet(StringComparer.Ordinal);
        }

        Assert.True(totalDealtItems >= 300, $"样本数量过少: {totalDealtItems}");
        double ratio = (double)discountedItems / totalDealtItems;
        Assert.InRange(ratio, 0.05, 0.22); // 期待约 12%，留足随机容差
        Assert.True(discountedItems > 0);
    }

    // ── Task 4 [2.5, 2.6 & GuidOnly]: MaxSelections / 空池金币兜底 / GuidOnly ──

    [Fact]
    public void Task4_MaxSelections_EmptyFreeDealFallback_AndGuidOnlyAllowlist()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        // 4a. MaxSelections 映射（SelectionRulesData & EncounterStep RuleNode）
        var selRulesData = new SelectionRulesData { MaxSelections = 2 };
        Assert.Equal(2, selRulesData.GetEffectiveMaxSelections());
        Assert.Equal(1, new SelectionRulesData { MaxSelections = 0 }.GetEffectiveMaxSelections());

        const string maxSelMerchantId = "99990001-2222-2222-2222-222222222201";
        const string emptyFreeStepId = "99990001-2222-2222-2222-222222222202";
        const string maxSelStepId = "99990001-2222-2222-2222-222222222203";
        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        const string chocoId = "1a3d4cde-333c-4936-b147-263d73f6d200";
        const string nonExistentId = "00000000-0000-0000-0000-000000000999";

        var customRepo = CreateRepoWithExtraCards(
            new CardTemplate
            {
                Id = maxSelMerchantId,
                InternalName = "Multi Pick Event",
                Type = "EventEncounter",
                SelectionContext = new SelectionContextData
                {
                    SpawnContext = new RuleNode
                    {
                        Type = "TSpawnContextQuery",
                        SelectionMethod = "Sequential",
                        Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(2) },
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
                    Rules = new SelectionRulesData { SelectionIsFree = true, CanExit = true, MaxSelections = 2 },
                },
            },
            new CardTemplate
            {
                Id = maxSelStepId,
                InternalName = "Step With MaxSelections=2",
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
                            SelectionContextRules = new RuleNode
                            {
                                ExtensionData = new Dictionary<string, JsonElement>
                                {
                                    ["MaxSelections"] = JsonDocument.Parse("2").RootElement.Clone(),
                                    ["SelectionIsFree"] = JsonDocument.Parse("true").RootElement.Clone(),
                                    ["CanExit"] = JsonDocument.Parse("true").RootElement.Clone(),
                                },
                            },
                        },
                    },
                },
            },
            new CardTemplate
            {
                Id = emptyFreeStepId,
                InternalName = "Empty Free Deal Step",
                Type = "EncounterStep",
                Abilities = new Dictionary<string, AbilityDefinition>
                {
                    ["ab_empty"] = new AbilityDefinition
                    {
                        Id = "ab_empty",
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
                                        Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [nonExistentId] }],
                                        SelectionMethod = "Sequential",
                                        Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(1) },
                                    },
                                ],
                            },
                            SelectionContextRules = new RuleNode
                            {
                                ExtensionData = new Dictionary<string, JsonElement>
                                {
                                    ["SelectionIsFree"] = JsonDocument.Parse("true").RootElement.Clone(),
                                    ["CanExit"] = JsonDocument.Parse("false").RootElement.Clone(),
                                    ["CanSelectMultiple"] = JsonDocument.Parse("true").RootElement.Clone(),
                                },
                            },
                        },
                    },
                },
            });

        var runMax = NewTestRun(EHero.Vanessa, gold: 10);
        var shopMax = new ShopEngine(runMax, pool, customRepo);
        shopMax.DealEncounterShelf(maxSelMerchantId, isReroll: false);
        Assert.Equal(2, shopMax.CurrentMaxSelections);
        Assert.Equal(2, shopMax.EffectiveMaxSelections);
        Assert.True(runMax.SelectionRules.CanSelectMultiple);
        Assert.False(shopMax.HasReachedMaxSelections(1));
        Assert.True(shopMax.HasReachedMaxSelections(2));

        // EncounterStep 动作内 SelectionRules.MaxSelections=2 也应正确传回 EncounterStepResult
        var stepRun = NewTestRun(EHero.Vanessa, gold: 10);
        var stepShop = new ShopEngine(stepRun, pool, customRepo);
        var stepEngine = new EncounterStepEngine(stepRun, pool, customRepo, stepShop, new Random(1));
        var maxEntry = new RunState.CardEntry { InstanceId = "stp_max", TemplateId = Guid.Parse(maxSelStepId), Type = ECardType.EncounterStep };
        stepRun.Cards[maxEntry.InstanceId] = maxEntry;
        stepRun.SelectionSet.Add(maxEntry.InstanceId);

        var maxRes = stepEngine.ExecuteStep(maxEntry);
        Assert.True(maxRes.Success);
        Assert.Equal(2, maxRes.MaxSelections);
        Assert.Equal(2, stepShop.CurrentMaxSelections);
        Assert.True(stepRun.SelectionRules.CanSelectMultiple);

        // 4b. 免费发牌奇遇步骤若 SpawnContext 抽空 -> 补偿 +3 Gold 且强制开放 CanExit = true
        var emptyStepRun = NewTestRun(EHero.Vanessa, gold: 10);
        var emptyStepShop = new ShopEngine(emptyStepRun, pool, customRepo);
        var emptyStepEngine = new EncounterStepEngine(emptyStepRun, pool, customRepo, emptyStepShop, new Random(2));
        var emptyEntry = new RunState.CardEntry { InstanceId = "stp_empty", TemplateId = Guid.Parse(emptyFreeStepId), Type = ECardType.EncounterStep };
        emptyStepRun.Cards[emptyEntry.InstanceId] = emptyEntry;
        emptyStepRun.SelectionSet.Add(emptyEntry.InstanceId);

        var emptyRes = emptyStepEngine.ExecuteStep(emptyEntry);
        Assert.True(emptyRes.Success);
        Assert.False(emptyRes.DealtNewCards);
        Assert.Equal(10 + ShopEngine.EmptyFreeDealFallbackGold, emptyStepRun.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.True(emptyStepRun.SelectionRules.CanExit);

        // 4c. GuidOnly 白名单：允许有效+zh-CN 的 Fates 选择与 Travel Agent / Expedition，严禁 Never 与无 zh-CN/无效资产卡
        const string futuraId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";
        const string surpriseMeId = "32dd98de-811c-45bc-98ab-9e416ccefba7";
        const string browseTicketsId = "cb0fd755-c9d9-4e2b-9096-de13c3773d30"; // GuidOnly 且缺失 zh-CN
        const string swoleNeverId = "c08396cd-ffc6-4b3f-86f8-18c053bee76f";    // Spawnable=Never

        Assert.True(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(futuraId));
        Assert.True(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(surpriseMeId));
        Assert.True(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(CardPool.TravelAgentEncounterId));
        Assert.True(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(CardPool.JungleExpeditionTicketId));
        Assert.True(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(CardPool.JungleExpeditionRoomId));
        Assert.False(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(browseTicketsId));
        Assert.False(SpawnQueryEngine.IsExplicitlyAllowedGuidOnlyId(swoleNeverId));

        // Futura (Fates) 的真实 SelectionContext 应成功发出其全部 3 个子 EncounterStep
        var fatesRun = NewTestRun(EHero.Vanessa, gold: 20, day: 4, hour: 1);
        var fatesShop = new ShopEngine(fatesRun, pool, repo);
        fatesShop.DealEncounterShelf(futuraId, isReroll: false);
        Assert.Equal(3, fatesRun.SelectionSet.Count);

        // 直接使用 SpawnQueryEngine 验证：TSpawnFilterIdList 中放行的 GuidOnly 可生成，Never 与未放行/缺 zh-CN 均禁止
        var validAssets = BuildValidAssetSet(pool, repo);
        var queryPlayer = new PlayerState("p1", "Vanessa", "Vanessa", 400, 30, 8, 5);
        var qCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, queryPlayer, null, new Random(7));

        var mixedQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            SelectionMethod = "Sequential",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(4) },
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnFilterIdList",
                            Ids = [surpriseMeId, CardPool.JungleExpeditionTicketId, browseTicketsId, swoleNeverId],
                        },
                    ],
                    SelectionMethod = "Sequential",
                    Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonNumber(4) },
                },
            ],
        };

        var spawnedGuidOnly = SpawnQueryEngine.SpawnCards(mixedQuery, qCtx, validAssetIds: validAssets);
        var spawnedIds = spawnedGuidOnly.Select(c => c.Template.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(surpriseMeId, spawnedIds);
        Assert.Contains(CardPool.JungleExpeditionTicketId, spawnedIds);
        Assert.DoesNotContain(browseTicketsId, spawnedIds);
        Assert.DoesNotContain(swoleNeverId, spawnedIds);
    }

    // ── Task 5 [2.8]: Expedition 系统辅助 API ───────────────────────────────

    [Fact]
    public void Task5_ExpeditionHelpers_ShouldDetectTicketsMapRoomsAndExecuteTransitions()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        // 5a. CardPool & EncounterStepEngine 门票 -> 远征房映射
        Assert.True(CardPool.IsExpeditionTicketTemplate(CardPool.JungleExpeditionTicketId));
        Assert.True(CardPool.IsExpeditionTicketTemplate(CardPool.CrashSiteExpeditionTicketId));
        Assert.True(CardPool.IsExpeditionTicketTemplate(CardPool.TinyWorldExpeditionTicketId));
        Assert.False(CardPool.IsExpeditionTicketTemplate("6d6199b4-82a4-441d-9329-f4164737ac6b"));

        Assert.True(CardPool.IsExpeditionRoomTemplate(CardPool.JungleExpeditionRoomId));
        Assert.True(CardPool.IsExpeditionRoomTemplate(CardPool.CrashSiteExpeditionRoomId));
        Assert.True(CardPool.IsExpeditionRoomTemplate(CardPool.TinyWorldExpeditionRoomId));

        Assert.True(EncounterStepEngine.TryMapTicketToExpeditionRoom(CardPool.JungleExpeditionTicketId, out string? jungleRoom));
        Assert.Equal(CardPool.JungleExpeditionRoomId, jungleRoom);
        Assert.True(EncounterStepEngine.TryMapTicketToExpeditionRoom(CardPool.CrashSiteExpeditionTicketId, out string? crashRoom));
        Assert.Equal(CardPool.CrashSiteExpeditionRoomId, crashRoom);
        Assert.True(EncounterStepEngine.TryMapTicketToExpeditionRoom(CardPool.TinyWorldExpeditionTicketId, out string? tinyRoom));
        Assert.Equal(CardPool.TinyWorldExpeditionRoomId, tinyRoom);

        var ticketTemplates = pool.GetExpeditionTicketTemplates();
        var roomTemplates = pool.GetExpeditionRoomTemplates();
        Assert.Equal(3, ticketTemplates.Count);
        Assert.Equal(3, roomTemplates.Count);
        Assert.All(ticketTemplates, t => Assert.True(pool.IsAssetValid(t.Id)));
        Assert.All(roomTemplates, r => Assert.True(pool.IsAssetValid(r.Id)));

        // 5b. 0 张门票：不触发远征
        var noTicketRun = NewTestRun(EHero.Vanessa, day: 5, hour: 1);
        var noTicketShop = new ShopEngine(noTicketRun, pool, repo);
        var noTicketEngine = new EncounterStepEngine(noTicketRun, pool, repo, noTicketShop, new Random(1));
        Assert.False(noTicketEngine.HasHeldExpeditionTicket());
        var emptyPlan = noTicketEngine.BuildDayStartExpeditionPlan();
        Assert.False(emptyPlan.ShouldTrigger);
        var noTicketResult = noTicketEngine.TryEnterDayStartExpedition();
        Assert.NotNull(noTicketResult);
        Assert.False(noTicketResult!.Success);

        // 5c. 非对应日（Day 5）不触发；Day 9 Hour 1 持有 Jungle 门票（Stash 中）精准匹配 Jungle Expedition Room
        var wrongDayRun = NewTestRun(EHero.Vanessa, day: 5, hour: 1);
        AddItemToRun(wrongDayRun, repo, CardPool.JungleExpeditionTicketId, CardTier.Bronze, EInventorySection.Stash, slot: 2);
        var wrongDayEngine = new EncounterStepEngine(wrongDayRun, pool, repo, new ShopEngine(wrongDayRun, pool, repo), new Random(2));
        Assert.True(wrongDayEngine.HasHeldExpeditionTicket());
        Assert.False(wrongDayEngine.BuildDayStartExpeditionPlan().ShouldTrigger);
        Assert.False(wrongDayEngine.TryEnterDayStartExpedition().Success);

        var oneTicketRun = NewTestRun(EHero.Vanessa, day: 9, hour: 1);
        var heldTicket = AddItemToRun(oneTicketRun, repo, CardPool.JungleExpeditionTicketId, CardTier.Bronze, EInventorySection.Stash, slot: 2);
        var oneTicketShop = new ShopEngine(oneTicketRun, pool, repo);
        var oneTicketEngine = new EncounterStepEngine(oneTicketRun, pool, repo, oneTicketShop, new Random(2));

        Assert.True(oneTicketEngine.HasHeldExpeditionTicket());
        var onePlan = oneTicketEngine.BuildDayStartExpeditionPlan();
        Assert.True(onePlan.ShouldTrigger);
        Assert.False(onePlan.RequiresChoice);
        Assert.Equal(CardPool.JungleExpeditionRoomId, onePlan.DirectRoomEncounterId);
        Assert.Single(onePlan.HeldTickets);
        Assert.Equal(EInventorySection.Stash, onePlan.HeldTickets[0].Section);

        var enterResult = oneTicketEngine.TryEnterDayStartExpedition(true);
        Assert.NotNull(enterResult);
        Assert.True(enterResult!.Success);
        Assert.Equal(CardPool.JungleExpeditionRoomId, enterResult.NextEncounterId);
        Assert.False(oneTicketRun.Cards.ContainsKey(heldTicket.InstanceId));
        Assert.Contains(enterResult.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == heldTicket.InstanceId);
        Assert.NotEmpty(oneTicketRun.SelectionSet);
        Assert.Equal(CardPool.JungleExpeditionRoomId, oneTicketRun.CurrentEncounterId);

        // 5d. Day 6 Hour 1 同时持有 Jungle + CrashSite 两张门票：
        //     仅激活当日匹配的 CrashSite（在 Choice 额外下发第 4 张免费入口卡），绝不越日提前触发 Jungle 门票
        var multiTicketRun = NewTestRun(EHero.Vanessa, day: 6, hour: 1);
        var tJungle = AddItemToRun(multiTicketRun, repo, CardPool.JungleExpeditionTicketId, CardTier.Bronze, EInventorySection.Hand, slot: 0);
        var tCrash = AddItemToRun(multiTicketRun, repo, CardPool.CrashSiteExpeditionTicketId, CardTier.Bronze, EInventorySection.Stash, slot: 0);
        var multiShop = new ShopEngine(multiTicketRun, pool, repo);
        var multiEngine = new EncounterStepEngine(multiTicketRun, pool, repo, multiShop, new Random(3));

        var multiPlan = multiEngine.BuildDayStartExpeditionPlan();
        Assert.True(multiPlan.ShouldTrigger);
        Assert.False(multiPlan.RequiresChoice);
        Assert.Equal(CardPool.CrashSiteExpeditionRoomId, multiPlan.DirectRoomEncounterId);
        Assert.Single(multiPlan.CandidateRoomEncounterIds);
        Assert.Contains(CardPool.CrashSiteExpeditionRoomId, multiPlan.CandidateRoomEncounterIds);

        var choiceEvents = multiShop.DealHourEncounters();
        Assert.NotEmpty(choiceEvents);
        Assert.Equal(ERunState.Choice, multiTicketRun.State);
        Assert.True(multiTicketRun.SelectionRules.SelectionIsFree);
        Assert.Equal(4, multiTicketRun.SelectionSet.Count);
        var offeredRoomIds = multiTicketRun.SelectionSet
            .Select(id => multiTicketRun.Cards[id].TemplateId.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CardPool.CrashSiteExpeditionRoomId, offeredRoomIds);
        Assert.DoesNotContain(CardPool.JungleExpeditionRoomId, offeredRoomIds);
        var crashChoiceCard = multiTicketRun.SelectionSet
            .Select(id => multiTicketRun.Cards[id])
            .Single(c => string.Equals(c.TemplateId.ToString(), CardPool.CrashSiteExpeditionRoomId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, crashChoiceCard.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, -1));

        // 模拟玩家选择进入 Crash Site，并消耗对应的 Crash Site 门票（保留 Jungle 门票）
        var enterCrashRes = multiEngine.EnterExpeditionRoom(CardPool.CrashSiteExpeditionRoomId, consumeMatchingTicket: true);
        Assert.True(enterCrashRes.Success);
        Assert.Equal(CardPool.CrashSiteExpeditionRoomId, enterCrashRes.NextEncounterId);
        Assert.False(multiTicketRun.Cards.ContainsKey(tCrash.InstanceId));
        Assert.True(multiTicketRun.Cards.ContainsKey(tJungle.InstanceId));
    }
}
