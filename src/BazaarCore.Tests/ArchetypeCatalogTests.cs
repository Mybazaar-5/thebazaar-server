using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// 工作线 B 自动化测试：验证全 8 英雄 96 套流派蓝图与 32 套社区 10 胜参考蓝本的数据真值、官方中文映射及实战运转。
/// </summary>
public sealed class ArchetypeCatalogTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private readonly ITestOutputHelper _output;

    public ArchetypeCatalogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ShouldContainNinetySixUniqueArchetypesAcrossAllEightHeroes()
    {
        var catalog = new ArchetypeCatalog();

        Assert.InRange(catalog.All.Count, 80, 120);
        Assert.Equal(96, catalog.All.Count);

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var byHero = catalog.GetByHero(hero);
            Assert.InRange(byHero.Count, 10, 15);
            Assert.Equal(12, byHero.Count);

            foreach (var arch in byHero)
            {
                Assert.Equal(hero, arch.Hero);
                Assert.True(uniqueIds.Add(arch.Id), $"存在重复的流派 ID: {arch.Id}");
                Assert.Same(arch, catalog.GetById(arch.Id));
                Assert.False(string.IsNullOrWhiteSpace(arch.NameZh));
                Assert.False(string.IsNullOrWhiteSpace(arch.DescriptionZh));
                Assert.False(string.IsNullOrWhiteSpace(arch.EndgamePowerTag));
                Assert.NotEmpty(arch.EarlyFocusTags);
                Assert.NotEmpty(arch.PositioningRules);
                Assert.NotEmpty(arch.PreferredTags);
                Assert.NotEmpty(arch.PreferredHiddenTags);
                Assert.NotEmpty(arch.PreferredTriggers);
                Assert.NotEmpty(arch.PreferredEnchantments);

                Assert.True(arch.CoreItemNames.Count >= 2, $"{arch.Id} 核心物品不足 2 个");
                Assert.True(arch.SupportItemNames.Count >= 3, $"{arch.Id} 辅助物品不足 3 个");
                Assert.True(arch.CoreSkillNames.Count >= 2, $"{arch.Id} 核心技能不足 2 个");

                Assert.Equal(arch.CoreItemNames.Count, arch.CoreItemNames.Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(arch.SupportItemNames.Count, arch.SupportItemNames.Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(arch.CoreSkillNames.Count, arch.CoreSkillNames.Distinct(StringComparer.Ordinal).Count());

                var overlap = arch.CoreItemNames.Intersect(arch.SupportItemNames, StringComparer.Ordinal).ToList();
                Assert.Empty(overlap);
            }
        }

        Assert.Equal(96, uniqueIds.Count);
        Assert.Empty(catalog.GetByHero("UnknownHero"));
        Assert.Null(catalog.GetById("NonExistent_Archetype"));
    }

    [Fact]
    public void ShouldMatchRealAlwaysItemsSkillsAndEnchantmentsInGameData()
    {
        var repo = Repo.Value;
        var catalog = new ArchetypeCatalog(repo);
        var errors = catalog.ValidateAgainst(repo);
        Assert.Empty(errors);

        var validEnchants = new HashSet<string>(EnchantmentNames.All, StringComparer.Ordinal);
        var alwaysItemsByName = repo.AlwaysSpawnableItems
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var alwaysSkillsByName = repo.AlwaysSpawnableSkills
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var arch in catalog.All)
        {
            foreach (var item in arch.CoreItemNames.Concat(arch.SupportItemNames))
            {
                Assert.True(alwaysItemsByName.TryGetValue(item, out var templates),
                    $"{arch.Id}: 物品 '{item}' 不存在于 AlwaysSpawnableItems");
                Assert.Contains(templates, t =>
                    t.Heroes.Contains(arch.Hero, StringComparer.OrdinalIgnoreCase) ||
                    t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var skill in arch.CoreSkillNames)
            {
                Assert.True(alwaysSkillsByName.TryGetValue(skill, out var templates),
                    $"{arch.Id}: 技能 '{skill}' 不存在于 AlwaysSpawnableSkills");
                Assert.Contains(templates, t =>
                    t.Heroes.Contains(arch.Hero, StringComparer.OrdinalIgnoreCase) ||
                    t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var ench in arch.PreferredEnchantments.Keys)
            {
                Assert.Contains(ench, validEnchants);
            }
        }
    }

    [Fact]
    public void ShouldMatchLatestProdCacheGameDataAndOfficialZhCnTranslations()
    {
        var prodDbPath = TestInfrastructure.TestDataLocator.ResolveGameDataDbPath();
        var zhDbPath = TestInfrastructure.TestDataLocator.ResolveZhCnPath();

        Assert.True(File.Exists(prodDbPath), $"未找到权威 GameData.db: {prodDbPath}");
        Assert.True(File.Exists(zhDbPath), $"未找到权威 zh-CN.bytes: {zhDbPath}");
        Assert.False(TestInfrastructure.TestDataLocator.IsUnderSteamProdCache(prodDbPath));
        Assert.False(TestInfrastructure.TestDataLocator.IsUnderSteamProdCache(zhDbPath));

        var zhMap = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var zhConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = zhDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            zhConn.Open();
            using var cmd = zhConn.CreateCommand();
            cmd.CommandText = "SELECT hash, text FROM translation";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                zhMap[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var prodAlwaysItems = new Dictionary<string, List<CardTemplate>>(StringComparer.Ordinal);
        var prodAlwaysSkills = new Dictionary<string, List<CardTemplate>>(StringComparer.Ordinal);
        using (var gdConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = prodDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            gdConn.Open();
            using var cmd = gdConn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM cards";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetFieldValue<byte[]>(0);
                var card = JsonSerializer.Deserialize<CardTemplate>(raw, GameDatabaseLoader.JsonOptions);
                if (card == null || card.SpawningEligibility != "Always")
                    continue;

                var targetDict = card.Type switch
                {
                    "Item" => prodAlwaysItems,
                    "Skill" => prodAlwaysSkills,
                    _ => null
                };
                if (targetDict == null) continue;

                if (!targetDict.TryGetValue(card.InternalName, out var list))
                {
                    list = new List<CardTemplate>();
                    targetDict[card.InternalName] = list;
                }
                list.Add(card);
            }
        }

        bool HasZh(CardTemplate card)
        {
            var titleKey = card.Localization?.Title?.Key;
            if (!string.IsNullOrEmpty(titleKey) && zhMap.TryGetValue(titleKey, out var title) && !string.IsNullOrWhiteSpace(title))
                return true;
            if (!string.IsNullOrEmpty(card.TranslationKey) && zhMap.TryGetValue(card.TranslationKey, out var tkTitle) && !string.IsNullOrWhiteSpace(tkTitle))
                return true;
            return false;
        }

        var catalog = new ArchetypeCatalog();
        foreach (var arch in catalog.All)
        {
            foreach (var itemName in arch.CoreItemNames.Concat(arch.SupportItemNames))
            {
                Assert.True(prodAlwaysItems.TryGetValue(itemName, out var templates),
                    $"{arch.Id}: prod 缓存未找到 Always 物品 '{itemName}'");
                Assert.Contains(templates, t =>
                    (t.Heroes.Contains(arch.Hero, StringComparer.OrdinalIgnoreCase) ||
                     t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase)) && HasZh(t));
            }

            foreach (var skillName in arch.CoreSkillNames)
            {
                Assert.True(prodAlwaysSkills.TryGetValue(skillName, out var templates),
                    $"{arch.Id}: prod 缓存未找到 Always 技能 '{skillName}'");
                Assert.Contains(templates, t =>
                    (t.Heroes.Contains(arch.Hero, StringComparer.OrdinalIgnoreCase) ||
                     t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase)) && HasZh(t));
            }
        }
    }

    [Fact]
    public void ShouldDifferentiatePersonasAndCoverAllArchetypesInPickForHero()
    {
        var catalog = new ArchetypeCatalog();
        var allPersonas = Enum.GetValues<PersonaType>();
        Assert.Equal(8, allPersonas.Length);
        Assert.Contains(PersonaType.CluelessNewbie, allPersonas);
        Assert.Contains(PersonaType.MessyHybrid, allPersonas);
        Assert.Contains(PersonaType.GreedyBust, allPersonas);

        Assert.Throws<ArgumentNullException>(() => catalog.PickForHero(HeroNames.Vanessa, PersonaType.Casual, null!));
        Assert.Throws<ArgumentException>(() => catalog.PickForHero("InvalidHero", PersonaType.Casual, new Random(1)));

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var distByPersona = new Dictionary<PersonaType, Dictionary<string, int>>();

            foreach (var persona in allPersonas)
            {
                var rng = new Random(1000 + (int)persona * 97 + hero.Length);
                var counts = catalog.GetByHero(hero).ToDictionary(a => a.Id, _ => 0, StringComparer.Ordinal);

                const int samples = 2400;
                for (int i = 0; i < samples; i++)
                {
                    var picked = catalog.PickForHero(hero, persona, rng);
                    Assert.Equal(hero, picked.Hero);
                    counts[picked.Id]++;
                }

                // 每套流派都保留正向可达概率，避免某些流派被彻底饿死
                foreach (var kv in counts)
                {
                    Assert.True(kv.Value > 0, $"{hero} 在画像 {persona} 下未能抽中流派 {kv.Key}");
                }

                distByPersona[persona] = counts;
            }

            // 验证快攻画像（TempoAggro）与贪经济/贪崩盘画像（GreedyEco / GreedyBust）的分布存在显著差异
            double l1TempoVsGreedy = 0;
            double l1TempoVsGreedyBust = 0;
            double l1NewbieVsUniform = 0;
            double l1MessyVsUniform = 0;
            const double uniformProb = 1.0 / 12.0;

            foreach (var arch in catalog.GetByHero(hero))
            {
                double pTempo = distByPersona[PersonaType.TempoAggro][arch.Id] / 2400.0;
                double pGreedy = distByPersona[PersonaType.GreedyEco][arch.Id] / 2400.0;
                double pGreedyBust = distByPersona[PersonaType.GreedyBust][arch.Id] / 2400.0;
                double pNewbie = distByPersona[PersonaType.CluelessNewbie][arch.Id] / 2400.0;
                double pMessy = distByPersona[PersonaType.MessyHybrid][arch.Id] / 2400.0;

                l1TempoVsGreedy += Math.Abs(pTempo - pGreedy);
                l1TempoVsGreedyBust += Math.Abs(pTempo - pGreedyBust);
                l1NewbieVsUniform += Math.Abs(pNewbie - uniformProb);
                l1MessyVsUniform += Math.Abs(pMessy - uniformProb);
            }

            Assert.True(l1TempoVsGreedy > 0.30,
                $"{hero} 的 TempoAggro 与 GreedyEco 抽样分布差异过小 (L1={l1TempoVsGreedy:F3})");
            Assert.True(l1TempoVsGreedyBust > 0.30,
                $"{hero} 的 TempoAggro 与 GreedyBust 抽样分布差异过小 (L1={l1TempoVsGreedyBust:F3})");
            Assert.True(l1NewbieVsUniform < 0.15,
                $"{hero} 的 CluelessNewbie 应近似均匀抽选 (L1={l1NewbieVsUniform:F3})");
            Assert.True(l1MessyVsUniform < 0.18,
                $"{hero} 的 MessyHybrid 应近似均匀抽选 (L1={l1MessyVsUniform:F3})");
        }
    }

    [Fact]
    public void ShouldValidateAllCommunityTenWinBlueprintsAndPassBoardValidator()
    {
        var repo = Repo.Value;
        var catalog = new ArchetypeCatalog();
        var validator = new BoardValidator();
        var validEnchants = new HashSet<string>(EnchantmentNames.All, StringComparer.Ordinal);

        var allSpecs = CommunityTenWinBlueprints.GetAll();
        Assert.InRange(allSpecs.Count, 24, 32);
        Assert.Equal(32, allSpecs.Count);

        var bpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsByName = repo.AllItems
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var skillsByName = repo.AllSkills
            .GroupBy(c => c.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var heroSpecs = CommunityTenWinBlueprints.GetByHero(hero);
            Assert.InRange(heroSpecs.Count, 3, 4);
            Assert.Equal(4, heroSpecs.Count);

            foreach (var spec in heroSpecs)
            {
                Assert.True(bpIds.Add(spec.BlueprintId), $"存在重复的社区蓝本 ID: {spec.BlueprintId}");
                Assert.Equal(hero, spec.Hero);
                Assert.NotNull(catalog.GetById(spec.ArchetypeId));
                Assert.False(string.IsNullOrWhiteSpace(spec.TitleZh));
                Assert.False(string.IsNullOrWhiteSpace(spec.SourceNote));
                Assert.False(string.IsNullOrWhiteSpace(spec.ScalingNote));
                Assert.InRange(spec.EnchantedItems.Count, 1, 2);

                int totalWidth = 0;
                foreach (var itemName in spec.ItemInternalNames)
                {
                    Assert.True(itemsByName.TryGetValue(itemName, out var templates),
                        $"{spec.BlueprintId}: 5.0.0 库中未找到物品 '{itemName}'");
                    var tpl = templates.FirstOrDefault(t =>
                        t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) ||
                        t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
                    Assert.NotNull(tpl);
                    Assert.True(repo.HasOfficialZhTitle(tpl),
                        $"{spec.BlueprintId}: 物品 '{itemName}' 缺少官方简体中文标题");
                    totalWidth += tpl.CardSize.SlotCount();
                }

                Assert.InRange(totalWidth, 1, BoardGrid.Capacity);

                foreach (var (enchItem, enchName) in spec.EnchantedItems)
                {
                    Assert.Contains(enchItem, spec.ItemInternalNames);
                    Assert.Contains(enchName, validEnchants);
                }

                foreach (var skillName in spec.SkillInternalNames)
                {
                    Assert.True(skillsByName.TryGetValue(skillName, out var templates),
                        $"{spec.BlueprintId}: 5.0.0 库中未找到技能 '{skillName}'");
                    var tpl = templates.FirstOrDefault(t =>
                        t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) ||
                        t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
                    Assert.NotNull(tpl);
                    Assert.True(repo.HasOfficialZhTitle(tpl),
                        $"{spec.BlueprintId}: 技能 '{skillName}' 缺少官方简体中文标题");
                }
            }
        }

        // 构建 Day 12 参考盘面并逐一通过 BoardValidator 校验
        var boards = CommunityTenWinBlueprints.BuildReferenceBoards(repo, day: 12, seed: 1001);
        Assert.Equal(allSpecs.Count, boards.Count);

        for (int i = 0; i < boards.Count; i++)
        {
            var spec = allSpecs[i];
            var ps = boards[i];
            var arch = catalog.GetById(spec.ArchetypeId)!;
            var participant = new RunParticipantState
            {
                RunId = $"ref_{spec.BlueprintId}",
                Hero = spec.Hero,
                Persona = PersonaType.HardForcer,
                Archetype = arch,
                PlayerState = ps,
                Rng = new Random(1001 + i)
            };

            var snapshot = BoardSnapshotMapper.ToSnapshot(participant, repo, validator);
            var validation = validator.Validate(snapshot, repo, requireOfficialZh: true);
            Assert.True(validation.IsValid,
                $"{spec.BlueprintId} 未通过 BoardValidator 校验: {string.Join("; ", validation.Errors)}");
        }
    }

    [Fact]
    public void ShouldExecuteCombatAcrossAllCommunityTenWinReferenceBoardsWithoutMisfires()
    {
        var repo = Repo.Value;
        var specs = CommunityTenWinBlueprints.GetAll();
        var boards = CommunityTenWinBlueprints.BuildReferenceBoards(repo, day: 12, seed: 1001);
        Assert.Equal(32, boards.Count);

        int fightCount = 0;
        var dealtByBoard = new double[boards.Count];
        var shieldByBoard = new double[boards.Count];

        // 32 场跨英雄两两对抗（每套蓝本分别作为 P1 与 P2 各出战 1 场，总计 32 >= 24 场）
        for (int i = 0; i < boards.Count; i++)
        {
            int j = (i + 4) % boards.Count;
            var p1 = boards[i];
            var p2 = boards[j];
            Assert.NotEqual(p1.Hero, p2.Hero);

            var result = CombatSimulator.Simulate(
                repo,
                p1,
                p2,
                seed: 2026 + i,
                combatType: "PVP",
                recordTimeline: false,
                recordEvents: false);

            fightCount++;
            Assert.True(result.DurationMs > 0 && result.DurationMs <= CombatSimulator.MaxDurationMs,
                $"战斗 #{i} ({specs[i].BlueprintId} vs {specs[j].BlueprintId}) 时长异常: {result.DurationMs}ms");

            dealtByBoard[i] = Math.Max(dealtByBoard[i], result.Player1TotalDamageDealt);
            shieldByBoard[i] = Math.Max(shieldByBoard[i], result.Player1TotalShieldGained);
            dealtByBoard[j] = Math.Max(dealtByBoard[j], result.Player2TotalDamageDealt);
            shieldByBoard[j] = Math.Max(shieldByBoard[j], result.Player2TotalShieldGained);

            _output.WriteLine(
                $"Fight {i + 1:00}: {specs[i].BlueprintId} vs {specs[j].BlueprintId} -> Winner={result.WinnerPlayerId}, Dur={result.DurationMs}ms, P1Dmg={result.Player1TotalDamageDealt:F0}/Shd={result.Player1TotalShieldGained:F0}, P2Dmg={result.Player2TotalDamageDealt:F0}/Shd={result.Player2TotalShieldGained:F0}");
        }

        Assert.True(fightCount >= 24);
        for (int i = 0; i < boards.Count; i++)
        {
            Assert.True(dealtByBoard[i] > 0 || shieldByBoard[i] > 0,
                $"社区蓝本 {specs[i].BlueprintId} 在实战对抗中出现哑火（Damage={dealtByBoard[i]}, Shield={shieldByBoard[i]}）");
        }
    }
}
