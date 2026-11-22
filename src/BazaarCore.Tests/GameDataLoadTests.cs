using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// 阶段 1 自动化测试：验证本地热更新 GameData.db / GameData.db.zip 只读加载、官方中文词库、Tooltip 渲染与品质概率表。
/// </summary>
public sealed class GameDataLoadTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    [Fact]
    public void ShouldLoadAllDatabaseTablesCompletely()
    {
        var repo = Repo.Value;

        Assert.True(repo.CardsById.Count >= 3386, $"卡牌总数应 >= 3386，实际: {repo.CardsById.Count}");
        Assert.True(repo.MonstersById.Count >= 177, $"野怪总数应 >= 177，实际: {repo.MonstersById.Count}");
        Assert.Equal(30, repo.LevelUps.Count);
        Assert.True(repo.TooltipGlossary.Count > 0);
        Assert.Equal(250, repo.GameMode.CombatSimEffectDelay);
        Assert.Equal(200, repo.GameMode.CritPercentMultiplier);
        Assert.Equal(3, repo.GameMode.BurnDecrementByAsPercentage);
        Assert.Equal(20000, repo.GameMode.Sandstorm.CountdownStartTime);
        Assert.Equal(10000, repo.GameMode.Sandstorm.CountdownDuration);

        var prodCacheDb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Tempo Storm", "The Bazaar", "prod", "cache", "GameData.db");
        if (File.Exists(prodCacheDb))
        {
            Assert.NotNull(repo.SourceDataPath);
            Assert.EndsWith(".db", repo.SourceDataPath!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(prodCacheDb), Path.GetFullPath(repo.SourceDataPath!));
        }
    }

    [Fact]
    public void ShouldHaveZeroUnknownRuleTypes()
    {
        var repo = Repo.Value;

        Assert.Empty(repo.UnknownRuleTypes);
        Assert.True(repo.ObservedRuleTypes.Count >= 196, $"已观测 $type 数量应 >= 196，实际: {repo.ObservedRuleTypes.Count}");
    }

    [Fact]
    public void ShouldIndexAllEightHeroesAndAllThirteenEnchantments()
    {
        var repo = Repo.Value;

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var items = repo.GetSpawnableItemsForHero(hero, includeCommon: false);
            Assert.True(items.Count > 20, $"英雄 {hero} 应包含足够数量的可生成物品，实际: {items.Count}");
        }

        var enchantCoverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in repo.AllItems)
        {
            if (item.Enchantments != null)
            {
                foreach (var k in item.Enchantments.Keys)
                    enchantCoverage.Add(k);
            }
        }

        foreach (var ench in EnchantmentNames.All)
        {
            Assert.Contains(ench, enchantCoverage);
        }
    }

    [Fact]
    public void ShouldLoadOfficialZhLocalizationAndCoverAllAlwaysSpawnableCards()
    {
        var repo = Repo.Value;

        Assert.NotNull(repo.Localization);
        Assert.True(repo.Localization!.EntryCount >= 9000, $"官方中文词条应 >= 9000，实际: {repo.Localization.EntryCount}");

        foreach (var item in repo.AlwaysSpawnableItems)
        {
            Assert.True(repo.HasOfficialZhTitle(item), $"AlwaysSpawnable 物品缺少官方中文名: {item.Id} ({item.InternalName})");
            var zhTitle = repo.GetLocalizedTitle(item);
            Assert.False(string.IsNullOrWhiteSpace(zhTitle));
        }

        foreach (var skill in repo.AlwaysSpawnableSkills)
        {
            Assert.True(repo.HasOfficialZhTitle(skill), $"AlwaysSpawnable 技能缺少官方中文名: {skill.Id} ({skill.InternalName})");
            var zhTitle = repo.GetLocalizedTitle(skill);
            Assert.False(string.IsNullOrWhiteSpace(zhTitle));
        }

        // 空词库哨兵安全检查
        Assert.Equal(0, LocalizationLoader.Empty.EntryCount);
        Assert.False(LocalizationLoader.Empty.TryGetText("missing", out _));
    }

    [Fact]
    public void ShouldRenderChineseTooltipsAndNormalizeDayTierWeights()
    {
        var repo = Repo.Value;
        var player = new PlayerState("test_p1", "Test", HeroNames.Vanessa);
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player);

        // 选取带官方中文 Tooltip 的物品校验中文渲染与占位符替换
        int verifiedZhTooltips = 0;
        foreach (var tpl in repo.AlwaysSpawnableItems)
        {
            if (tpl.Localization?.Tooltips == null || tpl.Localization.Tooltips.Count == 0) continue;
            var firstKey = tpl.Localization.Tooltips[0].Content?.Key;
            if (!repo.Localization!.TryGetText(firstKey, out var zhTpl) || !ContainsCjk(zhTpl)) continue;

            var inst = new CardInstance(tpl);
            var lines = TooltipFormatter.FormatTooltips(inst, ctx);
            Assert.NotEmpty(lines);
            Assert.Contains(lines, ContainsCjk);
            Assert.DoesNotContain(lines, l => l.Contains("{ability.", StringComparison.OrdinalIgnoreCase) || l.Contains("{aura.", StringComparison.OrdinalIgnoreCase));

            // 直接传 localization 参数的重载同样可输出中文
            var directLines = TooltipFormatter.FormatTooltips(inst, null, repo.Localization);
            Assert.Contains(directLines, ContainsCjk);

            verifiedZhTooltips++;
            if (verifiedZhTooltips >= 20) break;
        }
        Assert.True(verifiedZhTooltips >= 10, $"应至少验证 10 件物品的中文 Tooltip 渲染，实际: {verifiedZhTooltips}");

        // 校验 Patch 20.2 Day 10+ 商店品阶稳态（按 Base GameMode.NumberOfDays=10 钳制为 0 / 0.20 / 0.70 / 0.10，与 BazaarDB /shop-odds 实测对齐）
        var bronzeTpl = repo.AlwaysSpawnableItems.First(c => c.DefaultTier == CardTier.Bronze && c.Tiers != null && c.Tiers.ContainsKey("Diamond"));
        var rng = new Random(12345);
        int silver = 0, gold = 0, diamond = 0;
        for (int i = 0; i < 2000; i++)
        {
            var t = SpawnQueryEngine.RollTierForDay(bronzeTpl, day: 12, ignoreTierTable: false, inheritSource: null, downShift: false, rng, repo.GameMode);
            switch (t)
            {
                case CardTier.Silver: silver++; break;
                case CardTier.Gold: gold++; break;
                case CardTier.Diamond: diamond++; break;
            }
        }
        Assert.Equal(2000, silver + gold + diamond);
        Assert.InRange(silver / 2000.0, 0.15, 0.26);   // 理论 0.20 (BazaarDB Day 12 实测 20%)
        Assert.InRange(gold / 2000.0, 0.64, 0.76);     // 理论 0.70 (BazaarDB Day 12 实测 70%)
        Assert.InRange(diamond / 2000.0, 0.06, 0.15);  // 理论 0.10 (BazaarDB Day 12 实测 10%)

        // 超出表最大天数键（例如 Day 30 > 20）应取最大键配置而非退化为全 Bronze
        var lateTier = SpawnQueryEngine.RollTierForDay(bronzeTpl, day: 30, ignoreTierTable: false, inheritSource: null, downShift: false, rng, repo.GameMode);
        Assert.True(lateTier >= CardTier.Silver);
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF') return true;
        }
        return false;
    }
}
