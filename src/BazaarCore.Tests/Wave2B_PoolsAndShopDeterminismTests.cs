using System.Reflection;
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
/// Wave 2B（D2-P1-11/12/13, D6-P1-10/13, P0-21/23/24/25, D3-P1-01/10）：
/// 卡池解封、纯 Common 隔离、ShopEngine 去重/线程安全/确定性子流与跨轨 Mod 池隔离强断言测试。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class Wave2B_PoolsAndShopDeterminismTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    [Fact]
    public void D2_P1_11_OfficialActiveCards_MustNotBeExcluded_AndScheduleRules100PctSpawnable()
    {
        var pool = Pool.Value;
        string[] unblockedActiveIds =
        [
            "071cf90c-57a4-40f2-8d79-4c87f0d100d4", // Test Subject Alpha
            "41cdf4ee-7790-4da1-96f8-a46d3391a0d9", // The Tester
            "47704642-ae7a-4775-a26a-e5e5a8cff7bf", // Eating Contest
            "fd862d85-66cd-431f-a85a-07742cbc2e1b", // Test Subject Alpha (Monster)
        ];

        foreach (string id in unblockedActiveIds)
        {
            var tpl = pool.Get(id);
            Assert.NotNull(tpl);
            Assert.False(CardPool.IsExcludedDevOrStoryTemplate(tpl), $"官方活跃卡 {tpl!.InternalName} ({id}) 不得被误封。");
            Assert.False(CardPool.IsExcludedFromDynamicEncounterPool(id, tpl.InternalName, tpl), $"官方活跃卡 {tpl.InternalName} 不得被动态遭遇池误封。");
            Assert.True(pool.IsPlayerSpawnableId(id, allowGuidOnly: false), $"官方活跃卡 {tpl.InternalName} 须通过 IsPlayerSpawnableId(allowGuidOnly: false)。");
        }

        // 真实开发/教程废卡仍被拦截
        Assert.False(pool.IsPlayerSpawnableId("4cdcc6d3-c0e6-4996-a0ea-c3457211f406", allowGuidOnly: true)); // [DEBUG] Transform
        Assert.False(pool.IsPlayerSpawnableId("1400ea5b-6492-4c41-9bf9-c7016973ee0a", allowGuidOnly: true)); // Greenheart Help

        // OfficialEncounterSchedule 中记录的常规遭遇必须可生成（The Tester 归属 Dooley/Stelle，Eating Contest 通用）
        Assert.Contains(pool.HourlyEncounters("Dooley", "EventEncounter"), e => e.Id.Equals("41cdf4ee-7790-4da1-96f8-a46d3391a0d9", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pool.HourlyEncounters("Vanessa", "EventEncounter"), e => e.Id.Equals("47704642-ae7a-4775-a26a-e5e5a8cff7bf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void D2_P1_12_ShopItems_DefaultExcludes136PureCommonNonShop_AndAllowsExplicitCommon()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        foreach (string hero in HeroNames.PlayableHeroes)
        {
            var defaultItems = pool.ShopItems(hero);
            Assert.NotEmpty(defaultItems);
            Assert.DoesNotContain(defaultItems, t =>
                !t.IsCustomModCard
                && !t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                && t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(defaultItems, t =>
                !t.IsCustomModCard
                && t.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase)
                && !t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase));
        }

        var withCommon = pool.ShopItems("Vanessa", includeCommon: true);
        var withoutCommon = pool.ShopItems("Vanessa", includeCommon: false);
        Assert.True(withCommon.Count - withoutCommon.Count >= 100,
            $"显式 includeCommon=true 应比默认池多出至少 100 件纯 Common 物品，实际差值={withCommon.Count - withoutCommon.Count}");

        // 通用自定义 Mod 物品在默认 ShopItems(hero) 中必须仍被放行
        var donorItem = pool.ShopItems("Vanessa").First();
        var modItemCore = new BazaarCore.Models.CardTemplate
        {
            Id = "mod_w2b_universal_item",
            InternalName = "W2B Universal Mod Item",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = ["Common"],
            Tags = ["Tool"],
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["BuyPrice"] = 4, ["SellPrice"] = 2 } },
            },
        };
        var overlayRepo = repo.CreateWithOverlay(additionalCards: [modItemCore]);
        var bindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [modItemCore.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = modItemCore.Id,
                ModId = "w2b-test-mod",
                BaseVisualTemplateId = donorItem.Id,
                CardType = "Item",
                Size = "Small",
                TitleZh = "通用测试自定义物品",
            }
        };
        var overlayPool = pool.CreateIsolatedOverlay(overlayRepo, bindings);
        Assert.Contains(overlayPool.ShopItems("Vanessa"), t => t.Id == modItemCore.Id);
    }

    [Fact]
    public void D2_P1_13_D6_P1_10_ShopEngine_SameHourDedup_ThreadSafeReplacementSet_AndDeterministicStreamRng()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        // 1) 同小时重抽（Hour == OfferedDayEncounterLastHour）不得清空当日已展示去重表
        var runDedup = new RunState
        {
            RunId = "w2b_dedup",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 2,
            Hour = 2,
            RngSeed = 4242,
        };
        var shopDedup = new ShopEngine(runDedup, pool, repo);
        shopDedup.DealHourEncounters();
        var firstOffered = new HashSet<string>(runDedup.OfferedDayEncounterIds, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(firstOffered);
        Assert.Equal(2u, runDedup.OfferedDayEncounterLastHour);

        // 同日同小时再次发牌（模拟不推进小时的奇遇重抽），不得清空首次记录的去重集
        shopDedup.DealHourEncounters();
        foreach (string id in firstOffered)
        {
            Assert.Contains(id, runDedup.OfferedDayEncounterIds);
        }

        // 小时真回退（Hour 1 < LastHour 2）才清空
        runDedup.Hour = 1;
        shopDedup.DealHourEncounters();
        Assert.Equal(1u, runDedup.OfferedDayEncounterLastHour);

        // 2) ReplacementSetRegistry 多线程并发读写零异常
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 16, tid =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    string setId = $"w2b_concurrent_set_{tid}_{i % 8}";
                    ShopEngine.RegisterReplacementSet(new ShopEngine.ReplacementSetDefinition(
                        setId,
                        CardPool.CrashSiteExpeditionRoomId,
                        Variant: null,
                        Hours: new Dictionary<int, IReadOnlyList<ShopEngine.ReplacementSetEntry>>()));
                    Assert.True(ShopEngine.TryGetReplacementSet(setId, out var def));
                    Assert.NotNull(def);
                    Assert.True(ShopEngine.TryGetReplacementSet(CardPool.ReplacementSetCrashSiteId, out _));
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        Assert.Empty(exceptions);

        // 3) 确定性子流：同一 DailyChallenge FixedRunSeed 下，局 B 额外消费 _rng 后，
        //    Hour 3 野怪列表、非分页战利品掉落与商人折扣落点仍 100% 逐项相等
        static (List<string> Monsters, List<string> Discounts, List<string> Loot) RunDeterministicProbe(
            CardPool p, GameDataRepository r, int extraRngNoise)
        {
            var rules = new CustomRunRuleSet
            {
                SpecialModeId = ESpecialModeId.DailyChallenge,
                FixedRunSeed = 987654321,
                MonsterLootChooseOne = false,
            };

            var run = new RunState
            {
                RunId = "w2b_det_" + extraRngNoise,
                GameModeId = p.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 3,
                Hour = 3,
                RngSeed = 987654321,
                CustomRules = rules,
            };
            var shop = new ShopEngine(run, p, r) { ActiveRules = rules };

            // 往共享主随机源（原私有 _rng 已按 M1 统一收口为 shop.Rng.Main）注入额外消费噪声，若抽签绕过确定性子流 streamRng 则结果必然偏移
            var rawRng = shop.Rng.Main;
            for (int n = 0; n < extraRngNoise; n++) _ = rawRng.Next();

            // Hour 3 野怪
            shop.DealHourEncounters();
            var monsters = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();

            // 噪声再加一次后抽 10 次商人货架（覆盖 12% 折扣掷骰）
            var discounts = new List<string>();
            var merchantTpl = p.HourlyEncounters("Vanessa", "EventEncounter")
                .First(e => CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, r.GetCard(e.Id)));
            for (int k = 0; k < 10; k++)
            {
                for (int n = 0; n < extraRngNoise; n++) _ = rawRng.Next();
                run.Day = (uint)(2 + (k % 5));
                run.Hour = (uint)(1 + (k % 2));
                run.DailyRerollSequence = k;
                shop.DealEncounterShelf(merchantTpl.Id);
                foreach (var kv in run.ActiveShelfDiscounts.OrderBy(x => x.Key, StringComparer.Ordinal))
                    discounts.Add($"{k}:{run.Cards[kv.Key].TemplateId}:{kv.Value}");
            }

            // 噪声再加一次后抽非分页战利品
            for (int n = 0; n < extraRngNoise; n++) _ = rawRng.Next();
            var firstMonster = TestInfrastructure.SharedGameDataFixture.Shared.CreateMonstersCopy()
                .First(m => m.AllLootCandidates().Count() >= 3);
            run.Day = 3;
            run.Hour = 3;
            shop.DealMonsterVictoryLoot(null, firstMonster, p1Core: null, chooseOne: false);
            var loot = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();

            return (monsters, discounts, loot);
        }

        var a = RunDeterministicProbe(pool, repo, extraRngNoise: 0);
        var b = RunDeterministicProbe(pool, repo, extraRngNoise: 37);
        Assert.Equal(a.Monsters, b.Monsters);
        Assert.NotEmpty(a.Discounts);
        Assert.Equal(a.Discounts, b.Discounts);
        Assert.Equal(a.Loot, b.Loot);
    }

    [Fact]
    public void D6_P1_13_ShopHighTierBonus100Pct_AndCrossHeroOwnHeroBiasExactSplit()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        // 1) ShopHighTierBonusPct = 90 不可被 SpawnQueryEngine 二次截断在 60
        var r60 = new CustomRunRuleSet { ShopHighTierBonusPct = 60.0 };
        var r90 = new CustomRunRuleSet { ShopHighTierBonusPct = 90.0 };

        var w60 = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(repo.GameMode, day: 1, r60);
        var w90 = SpawnQueryEngine.GetEffectiveDayTierWeightsWithRules(repo.GameMode, day: 1, r90);
        Assert.True(w90.Silver + w90.Gold + w90.Diamond > w60.Silver + w60.Gold + w60.Diamond + 1e-6,
            $"ShopHighTierBonusPct=90 权重不应被截断为 60：w60={w60}, w90={w90}");

        // 2) CrossHeroMayhem + CrossHeroOwnHeroBias = 0.0 -> 200 次抽店 0 张本英雄专属卡
        var crossRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.CrossHeroMayhem,
            CrossHeroOwnHeroBias = 0.0,
        };

        var chances = pool.CreateTierChances();
        var rng = new Random(1337);
        int ownHeroHits = 0;
        for (int i = 0; i < 200; i++)
        {
            var rolled = pool.RollShopItems("Vanessa", day: 5, count: 3, chances, ownedMaxTiers: null, archetypeResolver: null, rules: crossRules, rngOverride: rng);
            Assert.NotEmpty(rolled);
            foreach (var (tpl, _) in rolled)
            {
                if (tpl.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase))
                    ownHeroHits++;
            }
        }
        Assert.Equal(0, ownHeroHits);
    }

    [Fact]
    public void P0_23_P0_25_D3_P1_01_CardPool_UniversalCustomSkill_PatchNotCustom_AndTopLevelAttrsMerged()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;
        var donorSkill = pool.Skills("Vanessa").First();
        var donorItem = pool.ShopItems("Vanessa").First();

        // 1) 通用 Common 自定义技能（P0-23）
        var universalModSkill = new BazaarCore.Models.CardTemplate
        {
            Id = "mod_w2b_universal_skill",
            InternalName = "W2B Universal Skill",
            Type = "Skill",
            Size = "Medium",
            StartingTier = "Bronze",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["DamageAmount"] = 5 } },
            },
        };

        // 2) 仅配顶层 Attributes + 单阶 Bronze 的 Mod 物品（D3-P1-01）
        var topLevelOnlyItem = new BazaarCore.Models.CardTemplate
        {
            Id = "mod_w2b_top_level_attrs_item",
            InternalName = "W2B TopLevel Attrs Item",
            Type = "Item",
            Size = "Medium",
            StartingTier = "Bronze",
            Heroes = ["Vanessa"],
            SpawningEligibility = "Always",
            Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["CooldownMax"] = 4000,
                ["DamageAmount"] = 18,
                ["BuyPrice"] = 6,
                ["SellPrice"] = 3,
            },
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["DamageAmount"] = 20 } },
            },
        };

        var overlayRepo = repo.CreateWithOverlay(additionalCards: [universalModSkill, topLevelOnlyItem]);
        var bindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [universalModSkill.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = universalModSkill.Id,
                ModId = "w2b-test-mod",
                BaseVisualTemplateId = donorSkill.Id,
                CardType = "Skill",
                Size = "Medium",
                TitleZh = "通用自定义技能",
            },
            [topLevelOnlyItem.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = topLevelOnlyItem.Id,
                ModId = "w2b-test-mod",
                BaseVisualTemplateId = donorItem.Id,
                CardType = "Item",
                Size = "Medium",
                TitleZh = "顶层属性自定义武器",
            },
        };
        var overlayPool = pool.CreateIsolatedOverlay(overlayRepo, bindings);

        // P0-23：通用自定义技能进入 Vanessa 技能池，而 123 张原版 Common 野怪技能仍被隔离
        var vanessaSkills = overlayPool.Skills("Vanessa");
        Assert.Contains(vanessaSkills, s => s.Id == universalModSkill.Id);
        Assert.DoesNotContain(vanessaSkills, s =>
            !s.IsCustomModCard
            && !s.Heroes.Contains("Vanessa", StringComparer.OrdinalIgnoreCase)
            && s.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase));

        // P0-25：IsPatchOverride=true 的模板 IsCustomModCard 必须为 false
        var patchTpl = new CardPool.CardTemplate
        {
            Id = donorItem.Id,
            InternalName = donorItem.InternalName,
            Type = "Item",
            Size = donorItem.Size,
            StartingTier = donorItem.StartingTier,
            Heroes = donorItem.Heroes,
            BaseVisualTemplateId = donorItem.Id,
            IsPatchOverride = true,
        };
        Assert.False(patchTpl.IsCustomModCard);

        // D3-P1-01：合并顶层 core.Attributes 并向高阶自动补齐 Silver/Gold/Diamond，Day 8 可正常刷出
        var mappedTopItem = overlayPool.Get(topLevelOnlyItem.Id);
        Assert.NotNull(mappedTopItem);
        Assert.True(mappedTopItem!.TierAttributes.ContainsKey("Bronze"));
        Assert.Equal(4000, mappedTopItem.TierAttributes["Bronze"]["CooldownMax"]);
        Assert.Equal(20, mappedTopItem.TierAttributes["Bronze"]["DamageAmount"]);
        Assert.True(mappedTopItem.TierAttributes.ContainsKey("Silver"));
        Assert.True(mappedTopItem.TierAttributes.ContainsKey("Gold"));
        Assert.True(mappedTopItem.TierAttributes.ContainsKey("Diamond"));
        Assert.Equal(4000, mappedTopItem.TierAttributes["Diamond"]["CooldownMax"]);
    }

    [Fact]
    public void P0_21_P0_24_D3_P1_10_CustomMonsterRngStream_EventScheduleGate_AndHasOwnedItemTemplateIsolation()
    {
        var pool = Pool.Value;
        var repo = Repo.Value;

        // 1) P0-21：自定义野怪经 CreateEncounterCandidate 保留 CustomCardId 并递增 VisualBindingsRevision
        var donorMonster = pool.OfType("CombatEncounter").First(c => pool.IsAssetValid(c.Id));
        var customMonsterTpl = new CardPool.CardTemplate
        {
            Id = "mod_w2b_custom_boss",
            InternalName = "W2B Custom Boss",
            Type = "CombatEncounter",
            Size = "Medium",
            StartingTier = "Bronze",
            Heroes = ["Common"],
            BaseVisualTemplateId = donorMonster.Id,
            ModId = "w2b-test-mod",
        };
        var overlayRepo = repo.CreateWithOverlay(additionalCards:
        [
            new BazaarCore.Models.CardTemplate
            {
                Id = customMonsterTpl.Id,
                InternalName = customMonsterTpl.InternalName,
                Type = "CombatEncounter",
                Size = "Medium",
                StartingTier = "Bronze",
                Heroes = ["Common"],
                SpawningEligibility = "Always",
            }
        ]);
        var monsterBindings = new Dictionary<string, CustomCardVisualBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [customMonsterTpl.Id] = new CustomCardVisualBinding
            {
                CustomTemplateId = customMonsterTpl.Id,
                ModId = "w2b-test-mod",
                BaseVisualTemplateId = donorMonster.Id,
                CardType = "CombatEncounter",
                Size = "Medium",
                TitleZh = "测试自定义野怪",
            }
        };
        var overlayPool = pool.CreateIsolatedOverlay(overlayRepo, monsterBindings);
        var run = new RunState
        {
            RunId = "w2b_p024_iso",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 3,
            Hour = 2,
            RngSeed = 777,
        };
        var shop = new ShopEngine(run, overlayPool, overlayRepo);
        int revBefore = run.VisualBindingsRevision;
        var mappedBoss = overlayPool.Get(customMonsterTpl.Id)!;
        var bossEntry = shop.CreateEncounterCandidate(mappedBoss, ECardType.CombatEncounter, "cbt");
        Assert.Equal(customMonsterTpl.Id, bossEntry.CustomCardId);
        Assert.Equal(Guid.Parse(donorMonster.Id), bossEntry.TemplateId);
        Assert.True(run.VisualBindingsRevision > revBefore);

        // 2) P0-24：ModAllowedHours 门控（仅 Hour 2 允许，Hour 1/4 拒绝）
        var hourRestrictedEvent = new CardPool.CardTemplate
        {
            Id = "mod_w2b_hour2_event",
            InternalName = "W2B Hour 2 Event",
            Type = "EventEncounter",
            Size = "Medium",
            StartingTier = "Bronze",
            Heroes = ["Common"],
            SpawningEligibility = "Always",
            ModMinDay = 1,
            ModMaxDay = 10,
            ModAllowedHours = [2],
            IsSingleSpawn = true,
        };
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(hourRestrictedEvent, day: 3, hour: 2, hero: "Vanessa"));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(hourRestrictedEvent, day: 3, hour: 1, hero: "Vanessa"));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(hourRestrictedEvent, day: 3, hour: 4, hero: "Vanessa"));
        Assert.True(ShopEngine.IsEffectiveSingleSpawnEncounter(hourRestrictedEvent));

        // 3) D3-P1-10：手牌持有借壳原版 GUID 的自定义卡时，HasOwnedItemTemplate(vanillaGuid) 必须返回 false
        var vanillaItem = pool.ShopItems("Vanessa").First();
        var customShellEntry = new RunState.CardEntry
        {
            InstanceId = "itm_mod_shell_1",
            TemplateId = Guid.Parse(vanillaItem.Id),
            CustomCardId = "mod_w2b_borrowed_shell_sword",
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Socket = EContainerSocketId.Socket_3,
            Section = EInventorySection.Hand,
        };
        run.Cards[customShellEntry.InstanceId] = customShellEntry;

        var hasOwnedMethod = typeof(ShopEngine).GetMethod("HasOwnedItemTemplate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(hasOwnedMethod);
        bool ownsVanilla = (bool)hasOwnedMethod!.Invoke(shop, [vanillaItem.Id, null])!;
        Assert.False(ownsVanilla, "持有借壳自定义卡不得误判为已拥有原版底座物品。");
        bool ownsCustom = (bool)hasOwnedMethod.Invoke(shop, ["mod_w2b_borrowed_shell_sword", null])!;
        Assert.True(ownsCustom, "查询自定义卡自身逻辑 ID 须精确命中。");
    }
}
