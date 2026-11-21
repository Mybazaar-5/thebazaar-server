using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V9 Stage B1 核心对齐回归测试套件：
/// 1. #15 / #22：Day 3 / Day 5 主力商人（Mittel / Jay Jay / Kev's Armory）白银率统计与品阶多样性，以及 TSpawnBehaviorDownShiftTier 局部逐阶回退；
/// 2. #23：Lv10 英雄训练师 Old Zane（Day 6）一次发满 3 个不重复技能且 Reroll 可出新技能；
/// 3. #34：Fiery Eagle Talisman 附魔动态标签以模板 HiddenTags 为准（不误带 Burn），TargetResolver 过滤未入格卡并稳定排序；
/// 4. #12：CardInstance.ModifyAttribute("Ammo", ...) 严格受 [0, AmmoMax] 封顶；
/// 5. #21：无 CooldownMax（EffectiveCooldownMs == 0）被动战斗触发器物品在 CombatSimulator 正常触发且战后保留。
/// </summary>
public sealed class V9CoreParityStageBTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());
    private static readonly Lazy<CardPool> Pool = new(() => CardPool.Load(ResolveDataPath("GameData.db")));

    private const string MittelId = "912c8b09-3b76-4aa1-8e21-245e0dfb5046";
    private const string JayJayId = "85db60ee-3619-4e39-bfb2-2f4ac56f172b";
    private const string KevsArmoryVanessaId = "6f5c1ce2-19c1-4df8-bc5a-242a0f5b5189";
    private const string OldZaneId = "845d2051-cca0-4533-947a-aa290f364e0a";
    private const string EagleTalismanId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string CannonId = "c74239b9-563c-4672-b664-9c9f22329f19";
    private const string MusketId = "5e7abe19-0087-4bb1-a8bf-617706cc67c9";
    private const string FangId = "7cbc8056-482b-4ccf-887a-ddb2702c856c";
    private const string AtlasId = "03842226-6605-466f-9ea3-a8a2dbea67e1";
    private const string DragonStatueId = "0152f1f7-1efd-40de-8de3-fdf936d793ed";

    private static string ResolveDataPath(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "dist", "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(@"e:\thebazaar\dist\data", fileName);
    }

    private static JsonElement JsonNumber(double val)
        => JsonDocument.Parse(val.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    private static HashSet<string> BuildValidAssetSet(CardPool pool, GameDataRepository repo)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tpl in repo.CardsById.Values)
        {
            if (pool.IsAssetValid(tpl.Id))
                set.Add(tpl.Id);
        }
        return set;
    }

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

    private static RunState NewTestRun(EHero hero = EHero.Vanessa, uint day = 3, uint hour = 1, int level = 5)
    {
        var run = new RunState
        {
            RunId = "v9b1_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = Pool.Value.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            Day = day,
            Hour = hour,
        };
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 100;
        run.PlayerAttributes[EPlayerAttributeType.Level] = level;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 500;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 500;
        run.UnlockedSlots = 0b1111111111;
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.StashGrid.UnlockedMask = 0b1111111111;
        return run;
    }

    // =========================================================================
    // #15 / #22：Day 3 / Day 5 主力商人白银率统计与品种多样性 + DownShift 局部回退
    // =========================================================================

    [Fact]
    public void Issue15_22_MainMerchants_Day3AndDay5_SilverRatesAndVariety_MatchOfficialWeights()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var validAssets = BuildValidAssetSet(pool, repo);

        string[] merchantIds = [MittelId, JayJayId, KevsArmoryVanessaId];
        foreach (var merchantId in merchantIds)
        {
            var merchantTpl = repo.GetCard(merchantId)!;
            var spawnCtx = merchantTpl.SelectionContext!.SpawnContext!;

            static (double SilverRate, double BronzeRate, int DistinctCount, int TotalCount) SampleMerchant(
                GameDataRepository repo,
                CardTemplate merchantTpl,
                RuleNode spawnCtx,
                HashSet<string> validAssets,
                int day,
                int shelves)
            {
                int silver = 0, bronze = 0, total = 0;
                var distinctIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < shelves; i++)
                {
                    var player = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, 500, 30, 8, 10)
                    {
                        Day = day,
                        Hour = 1,
                        Level = day,
                    };
                    var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(10_000 + day * 1000 + i))
                    {
                        CurrentDay = day,
                        CurrentHour = 1,
                        CurrentEncounterId = merchantTpl.Id,
                        SelfCard = new CardInstance(merchantTpl),
                    };

                    var spawned = SpawnQueryEngine.SpawnCards(spawnCtx, ctx, validAssetIds: validAssets);
                    Assert.Equal(3, spawned.Count);
                    foreach (var card in spawned)
                    {
                        total++;
                        distinctIds.Add(card.Template.Id);
                        if (card.Tier == CardTier.Silver) silver++;
                        else if (card.Tier == CardTier.Bronze) bronze++;
                    }
                }

                return (silver / (double)total, bronze / (double)total, distinctIds.Count, total);
            }

            var d3 = SampleMerchant(repo, merchantTpl, spawnCtx, validAssets, day: 3, shelves: 160);
            var d5 = SampleMerchant(repo, merchantTpl, spawnCtx, validAssets, day: 5, shelves: 160);

            Assert.Equal(480, d3.TotalCount);
            Assert.Equal(480, d5.TotalCount);

            // Day 3 官方权重：70% Bronze / 30% Silver
            Assert.InRange(d3.SilverRate, 0.20, 0.42);
            Assert.InRange(d3.BronzeRate, 0.58, 0.80);

            // Day 5 官方权重：25% Bronze / 75% Silver
            Assert.InRange(d5.SilverRate, 0.63, 0.87);
            Assert.InRange(d5.BronzeRate, 0.13, 0.37);

            // Day 5 白银率必须显著高于 Day 3，且两日均保持足够品种多样性（绝不锁死单一青铜子池）
            Assert.True(d5.SilverRate > d3.SilverRate + 0.25,
                $"{merchantTpl.InternalName} Day5 白银率({d5.SilverRate:P1})应显著高于 Day3({d3.SilverRate:P1})");
            Assert.True(d3.DistinctCount >= 10, $"{merchantTpl.InternalName} Day3 品种过少: {d3.DistinctCount}");
            Assert.True(d5.DistinctCount >= 12, $"{merchantTpl.InternalName} Day5 品种过少: {d5.DistinctCount}");
        }
    }

    [Fact]
    public void Issue15_22_DownShiftTier_FallsBackDownwardLocallyAcrossZeroWeightBronze_WithoutShiftingProbabilities()
    {
        var bronzeOnlyTpl = new CardTemplate
        {
            Id = "99999999-1111-1111-1111-111111111101",
            InternalName = "Synthetic Bronze Only",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = [HeroNames.Common],
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["DamageAmount"] = 5 } },
            },
        };
        var goldOnlyTpl = new CardTemplate
        {
            Id = "99999999-1111-1111-1111-111111111102",
            InternalName = "Synthetic Gold Only",
            Type = "Item",
            Size = "Small",
            StartingTier = "Gold",
            Heroes = [HeroNames.Common],
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, TierData>
            {
                ["Gold"] = new TierData { Attributes = new Dictionary<string, double> { ["DamageAmount"] = 30 } },
            },
        };

        var testRepo = CreateRepoWithExtraCards(bronzeOnlyTpl, goldOnlyTpl);

        // 1. Day 6 (Bronze=0%, Silver=95%, Gold=5%) + TSpawnBehaviorDownShiftTier：
        // 若候选池仅含纯青铜卡，targetTier 掷出 Silver 后应向下越过 0 权重的 Bronze 回退命中，保证不空抽
        var downShiftQuery = JsonSerializer.Deserialize<RuleNode>(
            """
            {
              "$type": "TSpawnContextQuery",
              "Limit": { "$type": "TFixedValue", "Value": 1.0 },
              "Behaviors": [{ "$type": "TSpawnBehaviorDownShiftTier" }],
              "Groups": [{
                "$type": "TSpawnGroup",
                "Filters": [
                  { "$type": "TSpawnFilterQuery", "Constraints": { "$type": "ConstraintCardType", "Types": ["Item"] } },
                  { "$type": "TSpawnFilterQuery", "Constraints": { "$type": "ConstraintHero", "Heroes": ["Common"] } }
                ]
              }]
            }
            """,
            GameDatabaseLoader.JsonOptions)!;

        // 仅放行合成的纯青铜测试卡（排除仓库内原有 Common 卡，直接用 ownedMaxTiers 不易隔离，改为直接测带该合成卡的限定子池）
        var bronzeOnlyRepo = CreateRepoWithExtraCards(bronzeOnlyTpl);
        var pDay6 = new PlayerState("p6", "P6", HeroNames.Vanessa, 500, 20, 8, 10) { Day = 6, Hour = 1 };
        // 将除 bronzeOnlyTpl 外所有 Common 卡标记为已持有 Diamond，使有效候选仅剩 bronzeOnlyTpl
        var blockOtherCommon = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in bronzeOnlyRepo.CardsById.Values)
        {
            if (c.Id != bronzeOnlyTpl.Id && c.Type == "Item")
                blockOtherCommon[c.Id] = CardTier.Diamond;
        }
        var ctxDay6 = new RuleExecutionContext(bronzeOnlyRepo, RuleScope.OutOfCombat, pDay6, null, new Random(42))
        {
            CurrentDay = 6,
            CurrentHour = 1,
        };
        var pickedDay6 = SpawnQueryEngine.SpawnCards(downShiftQuery, ctxDay6, ownedMaxTiers: blockOtherCommon);
        Assert.Single(pickedDay6);
        Assert.Equal(bronzeOnlyTpl.Id, pickedDay6[0].Template.Id);
        Assert.Equal(CardTier.Bronze, pickedDay6[0].Tier);

        // 2. Day 1 (Bronze=100%, Silver=0%, Gold=0%, Diamond=0%)：无论 downShift true/false，绝不向 0 权重高阶泄露
        var pDay1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 500, 20, 8, 10) { Day = 1, Hour = 1 };
        var blockAllExceptGold = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in testRepo.CardsById.Values)
        {
            if (c.Id != goldOnlyTpl.Id && c.Type == "Item")
                blockAllExceptGold[c.Id] = CardTier.Diamond;
        }
        var ctxDay1 = new RuleExecutionContext(testRepo, RuleScope.OutOfCombat, pDay1, null, new Random(7))
        {
            CurrentDay = 1,
            CurrentHour = 1,
        };
        Assert.Empty(SpawnQueryEngine.SpawnCards(downShiftQuery, ctxDay1, ownedMaxTiers: blockAllExceptGold));
    }

    // =========================================================================
    // #23：Lv10 英雄训练师 Old Zane（Day 6）一次发满 3 个技能且 Reroll 可出新技能
    // =========================================================================

    [Fact]
    public void Issue23_OldZane_Day5AndDay6_DealsFullThreeDistinctSkills_AndRerollYieldsNewSkills()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var validAssets = BuildValidAssetSet(pool, repo);
        var zaneTpl = repo.GetCard(OldZaneId)!;
        var zaneSpawn = zaneTpl.SelectionContext!.SpawnContext!;

        // 1. Day 5 与 Day 6 下直接用 SpawnQueryEngine 连续抽取：每次都必须出满 3 个不重复 Vanessa 专属技能
        foreach (int day in new[] { 5, 6 })
        {
            var seenSkillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int seed = 1; seed <= 20; seed++)
            {
                var player = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, 800, 30, 10, 10)
                {
                    Day = day,
                    Hour = 1,
                    Level = 10,
                };
                var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(seed))
                {
                    CurrentDay = day,
                    CurrentHour = 1,
                    CurrentEncounterId = OldZaneId,
                    SelfCard = new CardInstance(zaneTpl),
                };

                var skills = SpawnQueryEngine.SpawnCards(zaneSpawn, ctx, validAssetIds: validAssets);
                Assert.Equal(3, skills.Count);
                Assert.Equal(3, skills.Select(s => s.Template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.All(skills, s =>
                {
                    Assert.Equal("Skill", s.Template.Type);
                    Assert.Equal([HeroNames.Vanessa], s.Template.Heroes);
                    seenSkillIds.Add(s.Template.Id);
                });
            }
            Assert.True(seenSkillIds.Count >= 6, $"Day {day} Old Zane 多次重抽应覆盖多个技能，实际仅 {seenSkillIds.Count} 种");
        }

        // 2. 通过 ShopEngine.DealEncounterShelf 完整验证 Day 6 Lv10 进入与 Reroll
        bool sawDifferentSkillAfterReroll = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var run = NewTestRun(EHero.Vanessa, day: 6, hour: 1, level: 10);
            var shop = new ShopEngine(run, pool, repo);

            shop.DealEncounterShelf(OldZaneId, isReroll: false);
            Assert.Equal(3, run.SelectionSet.Count);
            var firstShelf = run.SelectionSet
                .Select(id => run.Cards[id].TemplateId.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(3, firstShelf.Count);
            Assert.All(run.SelectionSet, id => Assert.Equal(ECardType.Skill, run.Cards[id].Type));

            shop.DealEncounterShelf(OldZaneId, isReroll: true);
            Assert.Equal(3, run.SelectionSet.Count);
            var rerollShelf = run.SelectionSet
                .Select(id => run.Cards[id].TemplateId.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(3, rerollShelf.Count);
            Assert.All(run.SelectionSet, id => Assert.Equal(ECardType.Skill, run.Cards[id].Type));

            if (!firstShelf.SetEquals(rerollShelf))
                sawDifferentSkillAfterReroll = true;
        }
        Assert.True(sawDifferentSkillAfterReroll, "Old Zane Reroll 后应当能够刷出新技能组合");
    }

    // =========================================================================
    // #34：Fiery Eagle Talisman 动态标签修正 + TargetResolver.TTargetCardXMost 复核
    // =========================================================================

    [Fact]
    public void Issue34_FieryEagleTalisman_DoesNotGainBurnTag_WhileFieryCannonRetainsBurn()
    {
        var repo = Repo.Value;
        var talismanTpl = repo.GetCard(EagleTalismanId)!;
        var cannonTpl = repo.GetCard(CannonId)!;

        // 1. Fiery Eagle Talisman 在 DB 中仅带 Enchantments.Fiery.HiddenTags=["BurnReference"]，绝不可误加 Burn 标签
        var fieryTalisman = new CardInstance(talismanTpl, CardTier.Diamond, "Fiery");
        Assert.False(fieryTalisman.HasTag("Burn"), "Fiery Eagle Talisman 不应携带 Burn 可见标签");
        Assert.False(fieryTalisman.HasHiddenTag("Burn"), "Fiery Eagle Talisman 不应携带 Burn 隐藏标签");
        Assert.True(fieryTalisman.HasHiddenTag("BurnReference"), "Fiery Eagle Talisman 应保留模板定义中的 BurnReference 隐藏标签");
        Assert.DoesNotContain("Burn", fieryTalisman.GetAllTags(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Burn", fieryTalisman.GetAllHiddenTags(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("BurnReference", fieryTalisman.GetAllHiddenTags(), StringComparer.OrdinalIgnoreCase);

        // 2. 真正的 Fiery Cannon（模板自身含 Burn 隐藏标签与 TActionPlayerBurnApply）：
        //    严格对齐官方规则（P0-5）：动态效果标签属于 HiddenTags，不污染可见 Tags（HasTag）
        var fieryCannon = new CardInstance(cannonTpl, CardTier.Bronze, "Fiery");
        Assert.False(fieryCannon.HasTag("Burn"), "P0-5：Burn 为隐藏效果标签，不应污染可见 HasTag");
        Assert.True(fieryCannon.HasHiddenTag("Burn"));

        // 3. 当某附魔未在 Template.Enchantments 中定义时，仍保留 MapEnchantmentToEffectBit 兜底至 HiddenTags
        var bareTpl = new CardTemplate
        {
            Id = "bare_fallback_ench_item",
            InternalName = "BareFallback",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
        };
        var fallbackFiery = new CardInstance(bareTpl, CardTier.Bronze, "Fiery");
        Assert.False(fallbackFiery.HasTag("Burn"), "P0-5：附魔兜底效果标签应仅归入 HiddenTags");
        Assert.True(fallbackFiery.HasHiddenTag("Burn"));
    }

    [Fact]
    public void Issue34_TargetResolver_XMost_FiltersUnplaced_SortsStably_AndRespectsSoldScope()
    {
        var repo = Repo.Value;
        var cannonTpl = repo.GetCard(CannonId)!;
        var fangTpl = repo.GetCard(FangId)!;
        var talismanTpl = repo.GetCard(EagleTalismanId)!;

        var player = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, 500, 30, 8, 10);

        // 手牌故意乱序放入：未入格鬼卡(-1)、slot 5、slot 2；储藏区 slot 2 也有一张卡以验证同 StartSlot 稳定保留 Hand 优先
        var ghostUnplaced = new CardInstance(fangTpl, CardTier.Bronze, null, "ghost_unplaced") { StartSlot = -1 };
        var handRight = new CardInstance(fangTpl, CardTier.Bronze, null, "hand_right_5") { StartSlot = 5 };
        var handLeft = new CardInstance(cannonTpl, CardTier.Bronze, null, "hand_left_2") { StartSlot = 2 };
        var stashSameSlot = new CardInstance(fangTpl, CardTier.Bronze, null, "stash_slot_2") { StartSlot = 2 };
        var soldTalisman = new CardInstance(talismanTpl, CardTier.Diamond, "Fiery", "sold_talisman_0") { StartSlot = 0 };

        player.Hand.Cards.Add(ghostUnplaced);
        player.Hand.Cards.Add(handRight);
        player.Hand.Cards.Add(handLeft);
        player.Hand.Cards.Add(soldTalisman);
        player.Stash.Cards.Add(stashSameSlot);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(1))
        {
            SelfCard = soldTalisman,
        };

        // 1. 直接调用 TargetResolver.ResolveCards：必须过滤掉 StartSlot < 0 的 ghostUnplaced，
        //    按 StartSlot 升序选最左（ExcludeSelf=true 排除 soldTalisman 时命中 handLeft），选最右命中 handRight
        var leftRes = TargetResolver.ResolveCards(new RuleNode
        {
            Type = "TTargetCardXMost",
            TargetSection = "SelfHandAndStash",
            TargetMode = "LeftMostCard",
            ExcludeSelf = true,
        }, ctx);
        Assert.Single(leftRes);
        Assert.Same(handLeft, leftRes[0]);

        var rightRes = TargetResolver.ResolveCards(new RuleNode
        {
            Type = "TTargetCardXMost",
            TargetSection = "SelfHandAndStash",
            TargetMode = "RightMostCard",
            ExcludeSelf = true,
        }, ctx);
        Assert.Single(rightRes);
        Assert.Same(handRight, rightRes[0]);

        // 2. 同一 StartSlot(2) 同时存在 Hand 与 Stash 卡时，LeftMostCard 取 Hand（First），RightMostCard 取 Stash（Last）
        player.Hand.Cards.Remove(handRight);
        var tieLeft = TargetResolver.ResolveCards(new RuleNode
        {
            Type = "TTargetCardXMost",
            TargetSection = "SelfHandAndStash",
            TargetMode = "LeftMostCard",
            ExcludeSelf = true,
        }, ctx);
        Assert.Same(handLeft, tieLeft[0]);

        var tieRight = TargetResolver.ResolveCards(new RuleNode
        {
            Type = "TTargetCardXMost",
            TargetSection = "SelfHandAndStash",
            TargetMode = "RightMostCard",
            ExcludeSelf = true,
        }, ctx);
        Assert.Same(stashSameSlot, tieRight[0]);

        // 3. 通过 OutOfCombatController.OnCardSoldOrConsumed 协同 SoldCardScopes：
        //    卖出 Fiery Eagle Talisman 时（含未入格幽灵卡 StartSlot=-1），精确增益最左火炮，不增益自身也不受 -1 幽灵卡干扰
        player.Hand.Cards.Remove(soldTalisman);
        Assert.True(player.Hand.TryPlaceOrShift(soldTalisman, 0));
        AuraEngine.RecalculateAuras(player, null, RuleScope.OutOfCombat, player.Day, player.Hour, repo);
        double baseBurn = handLeft.GetAttribute("BurnApplyAmount");
        OutOfCombatController.OnCardSoldOrConsumed(player, soldTalisman, repo, new Random(1));
        Assert.Equal(20, handLeft.GetAttribute("CritChance"));
        Assert.Equal(baseBurn + 1, handLeft.GetAttribute("BurnApplyAmount"));
        Assert.Equal(0, ghostUnplaced.GetAttribute("CritChance"));
        Assert.Equal(0, soldTalisman.GetAttribute("BurnApplyAmount"));
    }

    // =========================================================================
    // #12：CardInstance.ModifyAttribute("Ammo", ...) 严格受 [0, AmmoMax] 封顶
    // =========================================================================

    [Fact]
    public void Issue12_CardInstance_ModifyAttribute_Ammo_NeverExceedsAmmoMax()
    {
        var repo = Repo.Value;
        var musketTpl = repo.GetCard(MusketId)!; // Silver AmmoMax=1
        var cannonTpl = repo.GetCard(CannonId)!; // Bronze AmmoMax=2
        var fangTpl = repo.GetCard(FangId)!;     // 无 AmmoMax (0)

        var musket = new CardInstance(musketTpl, CardTier.Silver);
        Assert.Equal(1, (int)Math.Round(musket.GetAttribute("AmmoMax")));
        Assert.Equal(1, musket.CurrentAmmo);

        musket.ModifyAttribute("Ammo", "Add", 5, permanent: false);
        Assert.Equal(1, musket.CurrentAmmo);

        musket.ModifyAttribute("Ammo", "Set", 99, permanent: true);
        Assert.Equal(1, musket.CurrentAmmo);

        musket.ModifyAttribute("Ammo", "Subtract", 5, permanent: false);
        Assert.Equal(0, musket.CurrentAmmo);

        musket.ModifyAttribute("Ammo", "Add", 5, permanent: false);
        Assert.Equal(1, musket.CurrentAmmo);

        var cannon = new CardInstance(cannonTpl, CardTier.Bronze);
        Assert.Equal(2, (int)Math.Round(cannon.GetAttribute("AmmoMax")));
        cannon.CurrentAmmo = 1;
        cannon.ModifyAttribute("Ammo", "Add", 5, permanent: false);
        Assert.Equal(2, cannon.CurrentAmmo);

        // 无 AmmoMax 的卡也不得被充出大于 0 的弹药
        var fang = new CardInstance(fangTpl, CardTier.Bronze);
        Assert.Equal(0, (int)Math.Round(fang.GetAttribute("AmmoMax")));
        fang.ModifyAttribute("Ammo", "Add", 5, permanent: false);
        Assert.Equal(0, fang.CurrentAmmo);
    }

    // =========================================================================
    // #21：无 CooldownMax 被动战斗触发器物品在 CombatSimulator 正常触发且战后保留
    // =========================================================================

    [Fact]
    public void Issue21_PassiveItemsWithoutCooldownMax_TriggerInCombatAndSurviveSync()
    {
        var repo = Repo.Value;
        var atlasTpl = repo.GetCard(AtlasId)!;          // 无 CooldownMax: TTriggerOnFightStarted(PVE) 永久加 SellPrice
        var statueTpl = repo.GetCard(DragonStatueId)!;  // 无 CooldownMax: TTriggerOnItemUsed(Neighbor) 治疗己方
        var fangTpl = repo.GetCard(FangId)!;            // 主动武器 3000ms CD

        // 1. 真实 DB 被动件 Atlas + Dragon Statue 在 PVE 战斗中验证
        var p1 = new PlayerState("p1", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10) { Day = 3, Hour = 1 };
        var p2 = new PlayerState("p2", "Monster", HeroNames.Common, 300, 0, 1, 10) { IsMonster = true };

        var atlas = new CardInstance(atlasTpl, CardTier.Silver, null, "passive_atlas");
        var statue = new CardInstance(statueTpl, CardTier.Silver, null, "passive_statue");
        var fang = new CardInstance(fangTpl, CardTier.Bronze, null, "active_fang");
        var enemyWpn = new CardInstance(fangTpl, CardTier.Bronze, null, "enemy_fang");

        Assert.Equal(0, atlas.EffectiveCooldownMs);
        Assert.False(atlas.HasActiveCooldown);
        Assert.Equal(0, statue.EffectiveCooldownMs);
        Assert.False(statue.HasActiveCooldown);

        Assert.True(p1.Hand.TryPlaceOrShift(atlas, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(statue, 2));
        Assert.True(p1.Hand.TryPlaceOrShift(fang, 3)); // 紧邻 Dragon Statue
        Assert.True(p2.Hand.TryPlaceOrShift(enemyWpn, 0));

        // 预先扣掉一点血，使 Dragon Statue 的治疗产生可观的回血数值与 HealApplied 事件
        p1.CurrentHealth = 420;
        int atlasSellBefore = atlas.GetSellPrice(repo.GameMode.StandardPrices);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 42, combatType: "PVE", recordTimeline: false, recordEvents: true, syncPermanentChanges: true);

        Assert.Contains(atlas, p1.Hand.Cards);
        Assert.Contains(statue, p1.Hand.Cards);
        Assert.Equal(atlasSellBefore + 1, atlas.GetSellPrice(repo.GameMode.StandardPrices));
        Assert.Contains(res.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == statue.InstanceId);

        // 2. 合成被动件同时验证 TTriggerOnFightStarted / TTriggerOnItemUsed / TTriggerOnCardFired (通过 ForceUse)
        var passiveAllTpl = new CardTemplate
        {
            Id = "passive_all_triggers_test",
            InternalName = "PassiveAllTriggers",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double>
                    {
                        ["ShieldApplyAmount"] = 15,
                        ["DamageAmount"] = 25,
                        ["BurnApplyAmount"] = 3,
                    },
                    AbilityIds = ["ab_start", "ab_used", "ab_fired"],
                },
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_start"] = new AbilityDefinition
                {
                    Id = "ab_start",
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                    },
                },
                ["ab_used"] = new AbilityDefinition
                {
                    Id = "ab_used",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnItemUsed",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerBurnApply",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                    },
                },
                ["ab_fired"] = new AbilityDefinition
                {
                    Id = "ab_fired",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                    },
                },
            },
        };
        var driverTpl = new CardTemplate
        {
            Id = "passive_force_driver_test",
            InternalName = "ForceDriver",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = new Dictionary<string, double> { ["CooldownMax"] = 1000 },
                    AbilityIds = ["ab_force"],
                },
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_force"] = new AbilityDefinition
                {
                    Id = "ab_force",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardForceUse",
                        Target = new RuleNode { Type = "TTargetCardXMost", TargetSection = "SelfHand", TargetMode = "LeftMostCard" },
                    },
                },
            },
        };

        var synthRepo = CreateRepoWithExtraCards(passiveAllTpl, driverTpl);
        var sp1 = new PlayerState("sp1", "SP1", HeroNames.Vanessa, 500, 0, 10, 10);
        var sp2 = new PlayerState("sp2", "SP2", HeroNames.Pygmalien, 500, 0, 10, 10);
        var passiveCard = new CardInstance(passiveAllTpl, CardTier.Bronze, null, "passive_all_inst");
        var driverCard = new CardInstance(driverTpl, CardTier.Bronze, null, "force_driver_inst");

        Assert.Equal(0, passiveCard.EffectiveCooldownMs);
        Assert.False(passiveCard.HasActiveCooldown);
        Assert.True(sp1.Hand.TryPlaceOrShift(passiveCard, 0));
        Assert.True(sp1.Hand.TryPlaceOrShift(driverCard, 1));

        var synthRes = CombatSimulator.Simulate(synthRepo, sp1, sp2, seed: 11, combatType: "PVP", recordTimeline: false, recordEvents: true);

        Assert.Contains(synthRes.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == passiveCard.InstanceId);
        Assert.Contains(synthRes.Events, e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == passiveCard.InstanceId);
        // 对齐官服 BazaarCardDealer.cs:1099：无有效冷却（!HasActiveCooldown）的纯被动卡禁止被 TActionCardForceUse 强制激活
        Assert.DoesNotContain(synthRes.Events, e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == passiveCard.InstanceId);
    }
}
