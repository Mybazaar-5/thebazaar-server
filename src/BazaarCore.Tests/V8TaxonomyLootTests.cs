using System.Numerics;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarCore.Tests;

[Xunit.Collection("ShopEngineStaticState")]
public sealed class V8TaxonomyLootTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    private static string ResolveDataPath(string fileName)
        => TestInfrastructure.TestDataLocator.ResolveDataFile(fileName);

    private static RunState NewRun(EHero hero = EHero.Vanessa, uint day = 1, uint hour = 1) => new()
    {
        RunId = "v8_" + Guid.NewGuid().ToString("N")[..8],
        GameModeId = Pool.Value.DefaultGameModeId,
        Hero = hero,
        PlayMode = EPlayMode.Unranked,
        Day = day,
        Hour = hour,
        PlayerAttributes = { [EPlayerAttributeType.Gold] = 30, [EPlayerAttributeType.HealthMax] = 400, [EPlayerAttributeType.Health] = 400 }
    };

    [Fact]
    public void ArchetypeIndex_Builds18Bits_HeroCounts_Encounters_AndCatalogDto()
    {
        var (pool, repo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, repo);
        var dto = CardArchetypeIndex.BuildCatalogDto(pool, repo);

        Assert.Equal(18, dto.Archetypes.Count);
        Assert.Equal(10, dto.EncounterCategories.Count);
        Assert.NotEmpty(dto.Items);
        Assert.NotEmpty(dto.Skills);
        Assert.Contains(dto.Items, i => BitOperations.PopCount(i.ArchetypeMask) > 1);
        Assert.Contains(dto.Items, i => i.NameZh.Any(c => c >= '\u4e00' && c <= '\u9fff'));

        var heroCounts = index.GetHeroCounts();
        foreach (string h in CardArchetypeIndex.StandardHeroCountKeys)
        {
            Assert.True(heroCounts.ContainsKey(h), $"Missing hero count key: {h}");
            Assert.True(heroCounts[h].Values.Sum() > 0, $"Hero {h} has zero categorized cards");
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        foreach (string prop in new[] { "archetypes", "encounterCategories", "items", "skills" })
            Assert.True(doc.RootElement.TryGetProperty(prop, out _));

        var allEncs = pool.OfType("PedestalEncounter").Concat(pool.OfType("EventEncounter")).Concat(pool.OfType("CombatEncounter")).ToList();
        var expectedCats = new (string Prefix, EEncounterWeightCategory Expected)[]
        {
            ("49717d0f", EEncounterWeightCategory.PedestalEnchantRandom), ("693af45b", EEncounterWeightCategory.PedestalUpgrade),
            ("e36bfb52", EEncounterWeightCategory.PedestalEnchant), ("210003fa", EEncounterWeightCategory.MerchantSkillsRare),
            ("22bb0004", EEncounterWeightCategory.MerchantSkillsRare), ("dc12f4bd", EEncounterWeightCategory.MerchantItems),
            ("818fcd1d", EEncounterWeightCategory.EventCultAndMystery), ("bf1594cc", EEncounterWeightCategory.EventCultAndMystery),
            ("002d8f6d", EEncounterWeightCategory.EventExpeditionAgent), ("9d0370fc", EEncounterWeightCategory.EventEconomyStats),
            ("dbdf69f8", EEncounterWeightCategory.EventFreeDealTransform),
            (pool.OfType("CombatEncounter").First(c => pool.IsPlayerSpawnable(c, true)).Id[..8], EEncounterWeightCategory.Monsters),
        };
        foreach (var (prefix, expected) in expectedCats)
        {
            var enc = allEncs.First(e => e.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Assert.True(index.TryGetEncounterCategory(enc.Id, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ArchetypeWeighting_Applies2xFormula_AndNeverConjuresIllegalCards()
    {
        Assert.Equal(1.0, SpawnQueryEngine.WeightMultiplierForMask((ulong)ECardArchetype.Shield, (ulong)ECardArchetype.Burn));
        Assert.Equal(2.0, SpawnQueryEngine.WeightMultiplierForMask((ulong)ECardArchetype.Burn, (ulong)ECardArchetype.Burn));
        Assert.Equal(3.0, SpawnQueryEngine.WeightMultiplierForMask((ulong)(ECardArchetype.Burn | ECardArchetype.Weapon), (ulong)(ECardArchetype.Burn | ECardArchetype.Weapon)));
        Assert.Equal(3.0, SpawnQueryEngine.WeightMultiplierForMask((ulong)(ECardArchetype.Burn | ECardArchetype.Weapon | ECardArchetype.Speed), (ulong)(ECardArchetype.Burn | ECardArchetype.Weapon | ECardArchetype.Speed)));

        var (pool, repo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, repo);
        var techRules = new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Tech };

        foreach (var (card, tier) in pool.RollShopItems("Vanessa", 1, 10, new TierChances(), null, index.GetArchetypeMask, techRules))
        {
            Assert.True(card.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase) || card.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase));
            Assert.Equal("Bronze", tier);
        }

        var qp = new PlayerState("p", "P", "Vanessa", 400, 20, 30, 10) { Day = 1, Hour = 1 };
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, new Random(42)) { CurrentDay = 1 };
        var shopQuery = repo.GetCard(pool.OfType("EventEncounter").First(e => e.Id.StartsWith("dc12f4bd")).Id)!.SelectionContext!.SpawnContext!;
        Assert.All(SpawnQueryEngine.SpawnCards(shopQuery, ctx, 3, null, pool.ValidCardIds, index.GetArchetypeMask, techRules),
            c => Assert.True(c.Template.Heroes.Contains("Vanessa") || c.Template.Heroes.Contains("Common")));

        var burnRules = new CustomRunRuleSet { SelectedArchetypeMask = (ulong)ECardArchetype.Burn, ArchetypeWeightMultiplier = 2.0 };
        var baseRules = new CustomRunRuleSet();
        int burnWeighted = 0, burnBase = 0;
        for (int i = 0; i < 180; i++)
        {
            foreach (var (c, _) in pool.RollShopItems("Vanessa", 1, 3, new TierChances(), null, index.GetArchetypeMask, burnRules))
                if ((index.GetArchetypeMask(c.Id) & (ulong)ECardArchetype.Burn) != 0) burnWeighted++;
            foreach (var (c, _) in pool.RollShopItems("Vanessa", 1, 3, new TierChances(), null, index.GetArchetypeMask, baseRules))
                if ((index.GetArchetypeMask(c.Id) & (ulong)ECardArchetype.Burn) != 0) burnBase++;
        }
        Assert.True(burnWeighted > burnBase, $"Expected Burn weighted ({burnWeighted}) > baseline ({burnBase})");
    }

    [Fact]
    public void DealHourEncounters_RespectsCategoryMultipliers_Bans_AndDayGating()
    {
        var (pool, repo) = (Pool.Value, Repo.Value);
        var index = CardArchetypeIndex.Build(pool, repo);
        var bannedAila = pool.OfType("EventEncounter").First(e => e.Id.StartsWith("dc12f4bd")).Id;
        var bannedMonster = pool.OfType("CombatEncounter").First(c => c.SpawningEligibility == "Always" && c.MonsterLevel <= 3 && !string.IsNullOrEmpty(c.MonsterTemplateId)).Id;
        var rules = new CustomRunRuleSet
        {
            BannedEncounterIds = [bannedAila, bannedMonster],
            EncounterCategoryMultipliers = new(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(EEncounterWeightCategory.EventEconomyStats)] = 0.0,
                [nameof(EEncounterWeightCategory.EventCultAndMystery)] = 6.0,
                [nameof(EEncounterWeightCategory.EventExpeditionAgent)] = 8.0,
            }
        };

        var runDay1 = NewRun(EHero.Vanessa, day: 1, hour: 1);
        var shopDay1 = new ShopEngine(runDay1, pool, repo, rules, index);
        for (int i = 0; i < 20; i++)
        {
            shopDay1.DealHourEncounters();
            var dealt = runDay1.SelectionSet.Select(id => runDay1.Cards[id].TemplateId.ToString()).ToList();
            Assert.Equal(3, dealt.Count);
            Assert.DoesNotContain(bannedAila, dealt, StringComparer.OrdinalIgnoreCase);
            foreach (string tid in dealt)
            {
                Assert.False(tid.StartsWith("818fcd1d", StringComparison.OrdinalIgnoreCase) || tid.StartsWith("002d8f6d", StringComparison.OrdinalIgnoreCase));
                if (index.TryGetEncounterCategory(tid, out var cat)) Assert.NotEqual(EEncounterWeightCategory.EventEconomyStats, cat);
            }
        }

        runDay1.Hour = 3;
        for (int i = 0; i < 10; i++)
        {
            shopDay1.DealHourEncounters();
            Assert.DoesNotContain(bannedMonster, runDay1.SelectionSet.Select(id => runDay1.Cards[id].TemplateId.ToString()), StringComparer.OrdinalIgnoreCase);
        }

        var day7Rules = new CustomRunRuleSet { EncounterCategoryMultipliers = new(StringComparer.OrdinalIgnoreCase) { [nameof(EEncounterWeightCategory.EventEconomyStats)] = 0.0, [nameof(EEncounterWeightCategory.EventFreeDealTransform)] = 0.0, [nameof(EEncounterWeightCategory.EventCultAndMystery)] = 0.0, [nameof(EEncounterWeightCategory.EventExpeditionAgent)] = 8.0 } };
        var runDay7 = NewRun(EHero.Vanessa, day: 7, hour: 1);
        new ShopEngine(runDay7, pool, repo, day7Rules, index).DealHourEncounters();
        Assert.Contains(runDay7.SelectionSet.Select(id => runDay7.Cards[id].TemplateId.ToString()), tid => tid.Equals(CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MonsterLoot_DefaultIsTrue_AndPaginatesWithin10SlotsWithFreeCycle()
    {
        Assert.True(new CustomRunRuleSet().MonsterLootChooseOne);
        var (pool, repo) = (Pool.Value, Repo.Value);
        List<CardPool.CardTemplate> PickItems(string size, int count) => pool.OfType("Item").Where(c => pool.IsPlayerSpawnable(c, true) && c.Size == size).Take(count).ToList();
        var large = PickItems("Large", 2);
        BazaarServer.Data.MonsterTemplate.MonsterItem Mi(string id, string tier) => new() { TemplateId = id, Tier = tier };
        var monster = new BazaarServer.Data.MonsterTemplate
        {
            Id = "test_mon_v8",
            InternalName = "Paginated Boss",
            Items = [.. large.Select(x => Mi(x.Id, "Gold")), .. PickItems("Medium", 3).Select(x => Mi(x.Id, "Silver"))],
            Skills = pool.OfType("Skill").Where(c => pool.IsPlayerSpawnable(c, true)).Take(2).Select(x => Mi(x.Id, "Silver")).ToList(),
            StashItems = [Mi(large[0].Id, "Gold"), .. PickItems("Small", 3).Select(x => Mi(x.Id, "Bronze"))],
        };

        var runSingle = NewRun();
        var shopSingle = new ShopEngine(runSingle, pool, repo, new CustomRunRuleSet { MonsterLootChooseOne = false });
        shopSingle.DealMonsterVictoryLoot(null, monster);
        Assert.Equal(ERunState.Loot, runSingle.State);
        Assert.Single(runSingle.SelectionSet);
        Assert.Empty(shopSingle.MonsterLootPages);

        var runPaged = NewRun();
        var shopPaged = new ShopEngine(runPaged, pool, repo, new CustomRunRuleSet());
        shopPaged.DealMonsterVictoryLoot(null, monster);
        Assert.Equal(ERunState.Loot, runPaged.State);
        Assert.Equal(1, shopPaged.CurrentMaxSelections);
        Assert.False(runPaged.SelectionRules.CanSelectMultiple);
        Assert.True(runPaged.SelectionRules.SelectionIsFree && runPaged.SelectionRules.CanExit);
        Assert.Equal((0u, 999u), (runPaged.RerollCost, runPaged.RerollsRemaining));
        Assert.True(shopPaged.MonsterLootPages.Count >= 2);
        Assert.All(shopPaged.MonsterLootPages, page => Assert.True(page.Sum(c => c.SlotCost) <= 10));
        Assert.Equal(10, shopPaged.MonsterLootPages.Sum(p => p.Count));
        Assert.Equal((0, shopPaged.MonsterLootPages[0].Count), (shopPaged.CurrentMonsterLootPageIndex, runPaged.SelectionSet.Count));

        for (int i = 1; i <= shopPaged.MonsterLootPages.Count; i++)
        {
            Assert.True(shopPaged.TryCycleMonsterLootPage(out var cycleEvents));
            Assert.NotEmpty(cycleEvents);
            int expectedIdx = i % shopPaged.MonsterLootPages.Count;
            Assert.Equal((expectedIdx, shopPaged.MonsterLootPages[expectedIdx].Count, 0u), (shopPaged.CurrentMonsterLootPageIndex, runPaged.SelectionSet.Count, runPaged.RerollCost));
        }
    }
}
