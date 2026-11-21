using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V4 核心测试套件（二）：验证 OutOfCombatController 全部局外钩子、
/// 买卡触发、卖卡“先触发再移除”相邻位置命中、消耗品效果、过小时/过天衍生进背包及摆盘光环即时变值。
/// </summary>
public sealed class OutOfCombatControllerTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static JsonElement JsonFixed(double val)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{val.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static JsonElement JsonNumber(double val)
        => JsonDocument.Parse(val.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    private static CardTemplate MakeSimpleWeapon(string id, double dmg, string size = "Small") => new()
    {
        Id = id,
        InternalName = id,
        Type = "Item",
        Size = size,
        StartingTier = "Bronze",
        Tags = ["Weapon"],
        Tiers = new Dictionary<string, TierData>
        {
            ["Bronze"] = new TierData
            {
                Attributes = new Dictionary<string, double>
                {
                    ["DamageAmount"] = dmg,
                    ["CooldownMax"] = 2000,
                },
            },
            ["Silver"] = new TierData
            {
                Attributes = new Dictionary<string, double>
                {
                    ["DamageAmount"] = dmg * 2,
                    ["CooldownMax"] = 2000,
                },
            },
        },
    };

    [Fact]
    public void OnItemPurchased_RealArmsDealerAndWrench_ShouldTriggerGoldHpAndAttributeGrowth()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Pygmalien, 250, 20, 10, 6);

        var armsDealerTpl = repo.GetCard("560a532c-73f4-4edd-b197-0127e2ca4cc9"); // Arms Dealer (Skill)
        var wrenchTpl = repo.GetCard("27e2ea0b-4e39-4826-be29-9f5505d5938a");     // Wrench (Item)
        Assert.NotNull(armsDealerTpl);
        Assert.NotNull(wrenchTpl);

        var armsDealer = new CardInstance(armsDealerTpl!);
        var wrench = new CardInstance(wrenchTpl!);
        var targetWeapon = new CardInstance(MakeSimpleWeapon("purchased_sword", 15));
        player.Skills.Add(armsDealer);
        player.Hand.TryAddAuto(wrench);
        player.Hand.TryAddAuto(targetWeapon);

        int hpBefore = player.HealthMax;
        var batch = OutOfCombatController.OnItemPurchased(player, targetWeapon, repo, new Random(1));

        // 购买带有 Weapon 标签的卡时，Arms Dealer 触发增加 HealthMax
        Assert.True(player.HealthMax > hpBefore, $"Arms Dealer 购买武器应增加 HealthMax: before={hpBefore}, after={player.HealthMax}");
        Assert.True(batch.PlayerAttributeDeltas.GetValueOrDefault("HealthMax", 0) > 0);
        Assert.NotEmpty(batch.TriggeredAbilityLogs);
    }

    [Fact]
    public void OnCardSoldOrConsumed_AdjacentWeaponBuff_MustTriggerBeforeRemovalUsingOriginalSlot()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 10);

        var leftWeapon = new CardInstance(MakeSimpleWeapon("left_wpn", 10));
        var rightWeapon = new CardInstance(MakeSimpleWeapon("right_wpn", 10));
        var farWeapon = new CardInstance(MakeSimpleWeapon("far_wpn", 10));

        var adjBuffConsumableTpl = new CardTemplate
        {
            Id = "adj_whetstone",
            InternalName = "Adjacent Whetstone",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["sell_adj"] = new AbilityDefinition
                {
                    Id = "sell_adj",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSold" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonFixed(12),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardPositional",
                            TargetMode = "Neighbor",
                            Origin = "Self",
                            IncludeOrigin = false,
                        },
                    },
                },
            },
        };
        var consumable = new CardInstance(adjBuffConsumableTpl);

        // 布局：slot 2 = leftWeapon, slot 3 = consumable, slot 4 = rightWeapon, slot 8 = farWeapon
        Assert.True(player.Hand.TryPlaceOrShift(leftWeapon, 2));
        Assert.True(player.Hand.TryPlaceOrShift(consumable, 3));
        Assert.True(player.Hand.TryPlaceOrShift(rightWeapon, 4));
        Assert.True(player.Hand.TryPlaceOrShift(farWeapon, 8));

        var batch = OutOfCombatController.OnCardSoldOrConsumed(player, consumable, repo, new Random(2));

        // 核心断言：在摘除前携带 StartSlot=3 触发，精准命中左右相邻 2 与 4，不命中远端 8，随后再把 consumable 移除
        Assert.Equal(22, leftWeapon.GetAttribute("DamageAmount"));
        Assert.Equal(22, rightWeapon.GetAttribute("DamageAmount"));
        Assert.Equal(10, farWeapon.GetAttribute("DamageAmount"));
        Assert.DoesNotContain(consumable, player.Hand.Cards);
        Assert.Contains(consumable, batch.RemovedCards);
        Assert.Contains(leftWeapon, batch.AffectedCards);
        Assert.Contains(rightWeapon, batch.AffectedCards);
    }

    [Fact]
    public void Consumable_ChocolateBar_And_RedGumball_ShouldBoostHealthMaxAndWeaponDamage()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 6);

        var chocoTpl = repo.GetCard("1a3d4cde-333c-4936-b147-263d73f6d200")!; // Chocolate Bar
        var gumballTpl = repo.GetCard("6d6199b4-82a4-441d-9329-f4164737ac6b")!; // Red Gumball
        var sword = new CardInstance(MakeSimpleWeapon("choco_sword", 10));
        var choco = new CardInstance(chocoTpl);
        var gumball = new CardInstance(gumballTpl);

        player.Hand.TryAddAuto(sword);
        player.Hand.TryAddAuto(choco);
        player.Hand.TryAddAuto(gumball);

        int hpBefore = player.HealthMax;
        OutOfCombatController.OnCardSoldOrConsumed(player, choco, repo, new Random(3));
        Assert.True(player.HealthMax > hpBefore, "吃掉 Chocolate Bar 应提升玩家 HealthMax");
        Assert.DoesNotContain(choco, player.Hand.Cards);

        double dmgBefore = sword.GetAttribute("DamageAmount");
        OutOfCombatController.OnCardSoldOrConsumed(player, gumball, repo, new Random(3));
        Assert.True(sword.GetAttribute("DamageAmount") > dmgBefore, "吃掉 Red Gumball 应提升手牌武器 DamageAmount");
        Assert.DoesNotContain(gumball, player.Hand.Cards);
    }

    [Fact]
    public void Consumable_EagleTalisman_UpgradeHammer_Transform_And_GoldSpawn_ShouldWork()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 6);

        var eagleTpl = repo.GetCard("0022c409-c839-41e8-8022-65a407457dfe")!; // Eagle Talisman -> CritChance
        var hammerTpl = repo.GetCard("19379b88-4fe5-474c-a254-d266c0435bf4")!; // Upgrade Hammer -> Upgrade XMost

        var sword = new CardInstance(MakeSimpleWeapon("upg_sword", 15));
        var eagle = new CardInstance(eagleTpl);
        var hammer = new CardInstance(hammerTpl);
        player.Hand.TryPlaceOrShift(sword, 3);
        player.Hand.TryPlaceOrShift(eagle, 4);
        player.Hand.TryPlaceOrShift(hammer, 5);

        OutOfCombatController.OnCardSoldOrConsumed(player, eagle, repo, new Random(4));
        Assert.True(sword.GetAttribute("CritChance") > 0, "Eagle Talisman 应提升暴击率");

        var hammerBatch = OutOfCombatController.OnCardSoldOrConsumed(player, hammer, repo, new Random(4));
        Assert.Equal(CardTier.Silver, sword.Tier);
        Assert.Contains(sword, hammerBatch.UpgradedCards);

        // 变形 + 产币消耗品（合成精确触发链验证）
        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        var coinMorphTpl = new CardTemplate
        {
            Id = "coin_morph_consumable",
            InternalName = "Coin & Morph",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["sell_coin"] = new AbilityDefinition
                {
                    Id = "sell_coin",
                    Trigger = new RuleNode { Type = "TTriggerOnCardSold" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode
                            {
                                Type = "TActionPlayerModifyAttribute",
                                AttributeType = "Gold",
                                Operation = "Add",
                                Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                                RawValueNode = JsonFixed(5),
                            },
                            new RuleNode
                            {
                                Type = "TActionCardTransform",
                                Target = new RuleNode { Type = "TTargetCardXMost", TargetSection = "SelfHand", TargetMode = "LeftMost", ExcludeSelf = true },
                                SpawnContext = new RuleNode
                                {
                                    Type = "TSpawnContextQuery",
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
                        ],
                    },
                },
            },
        };
        var coinMorph = new CardInstance(coinMorphTpl);
        player.Hand.TryPlaceOrShift(coinMorph, 4);
        int goldBefore = player.Gold;

        var morphBatch = OutOfCombatController.OnCardSoldOrConsumed(player, coinMorph, repo, new Random(5));
        Assert.Equal(goldBefore + 5, player.Gold);
        Assert.Single(morphBatch.TransformedCards);
        Assert.Equal(sword, morphBatch.TransformedCards[0].OldCard);
        Assert.Equal(gumballId, morphBatch.TransformedCards[0].NewCard.Template.Id);
    }

    [Fact]
    public void OnHourStarted_And_OnDayStarted_SpawnCards_HandFullShouldFallbackToStash()
    {
        var repo = Repo.Value;
        // 仅解锁 4 格手牌
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, unlockedHandSlots: 4);

        const string gumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
        var hourSpawnerTpl = new CardTemplate
        {
            Id = "hour_producer",
            InternalName = "Hour Producer",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["hr"] = new AbilityDefinition
                {
                    Id = "hr",
                    Trigger = new RuleNode { Type = "TTriggerOnHourStarted" },
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
        };

        // 手牌放满 4 个 Small 占满全部 4 格
        player.Hand.TryAddAuto(new CardInstance(hourSpawnerTpl));
        player.Hand.TryAddAuto(new CardInstance(MakeSimpleWeapon("filler1", 5)));
        player.Hand.TryAddAuto(new CardInstance(MakeSimpleWeapon("filler2", 5)));
        var filler3 = new CardInstance(MakeSimpleWeapon("filler3", 5));
        player.Hand.TryAddAuto(filler3);
        Assert.Equal(-1, player.Hand.FindFirstAvailableSlot(new CardInstance(MakeSimpleWeapon("probe", 5))));

        var hourBatch = OutOfCombatController.OnHourStarted(player, repo, new Random(7));
        Assert.Single(hourBatch.SpawnedCards);
        Assert.Equal("Stash", hourBatch.SpawnedCards[0].Section);
        Assert.Single(player.Stash.Cards);

        // 清理出 1 个手牌空位再测 OnDayStarted + Fishing Rod 优先进入 Hand
        player.Hand.Remove(filler3);
        var fishingRodTpl = repo.GetCard("bb1b65d5-ead6-4828-adb9-688c7e2aabb6")!; // Fishing Rod (Medium, TTriggerOnDayStarted)
        var rod = new CardInstance(fishingRodTpl);
        player.Stash.TryAddAuto(rod); // 哪怕在背包，HandAndStash 也可于过天衍生

        var dayBatch = OutOfCombatController.OnDayStarted(player, repo, new Random(8));
        Assert.NotEmpty(dayBatch.SpawnedCards);
        Assert.Contains(dayBatch.SpawnedCards, s => s.Section == "Hand");
    }

    [Fact]
    public void OnCardMoved_RealAuraCard_ShouldImmediatelyRecalculateOutOfCombatAttribute()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, unlockedHandSlots: 6);

        var swashBuckleTpl = repo.GetCard("69688b74-fd2b-42a2-a018-2b720a13f95a")!; // Swash Buckle: RightCard +DamageAmount
        var auraCard = new CardInstance(swashBuckleTpl);
        var weapon = new CardInstance(MakeSimpleWeapon("right_target_weapon", 10));

        // 先放远处：weapon 在 slot 2，auraCard 在 slot 6（不相邻）
        Assert.True(player.Hand.TryPlaceOrShift(weapon, 2));
        Assert.True(player.Hand.TryPlaceOrShift(auraCard, 6));
        OutOfCombatController.RecalculateOutOfCombatAuras(player, repo);
        double baseDmg = weapon.GetAttribute("DamageAmount");
        Assert.Equal(10, baseDmg);

        // 将 auraCard 拖到 slot 2，把 weapon 挤到 slot 3（成为 RightCard）
        Assert.True(player.Hand.TryPlaceOrShift(auraCard, 2));
        var moveBatch = OutOfCombatController.OnCardMoved(player, repo);
        double buffedDmg = weapon.GetAttribute("DamageAmount");

        Assert.True(buffedDmg > baseDmg, $"拖拽 Swash Buckle 至武器左侧后伤害应立即上升: before={baseDmg}, after={buffedDmg}");
        Assert.Contains(weapon, moveBatch.AffectedCards);

        // 再把 auraCard 移走
        Assert.True(player.Hand.TryPlaceOrShift(auraCard, 6));
        OutOfCombatController.OnCardMoved(player, repo);
        Assert.Equal(10, weapon.GetAttribute("DamageAmount"));
    }

    [Fact]
    public void OnCardUpgraded_FusionVsNormal_And_OnCardEnchanted_ShouldTriggerAndRefreshAuras()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 6);

        var listenerTpl = new CardTemplate
        {
            Id = "upg_ench_listener",
            InternalName = "Upgrade & Enchant Listener",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["on_upg"] = new AbilityDefinition
                {
                    Id = "on_upg",
                    Trigger = new RuleNode { Type = "TTriggerOnCardUpgraded" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(3),
                    },
                },
                ["on_ench"] = new AbilityDefinition
                {
                    Id = "on_ench",
                    Trigger = new RuleNode { Type = "TTriggerOnCardEnchanted" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "HealthMax",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(25),
                    },
                },
            },
        };
        var card = new CardInstance(listenerTpl);
        player.Hand.TryAddAuto(card);

        var upgBatch = OutOfCombatController.OnCardUpgraded(player, card, repo, new Random(1), isFusion: true);
        Assert.Equal(13, player.Gold);
        Assert.Equal(3, upgBatch.PlayerAttributeDeltas.GetValueOrDefault("Gold", 0));

        var enchBatch = OutOfCombatController.OnCardEnchanted(player, card, repo, new Random(1));
        Assert.Equal(275, player.HealthMax);
        Assert.Equal(25, enchBatch.PlayerAttributeDeltas.GetValueOrDefault("HealthMax", 0));
    }

    [Fact]
    public void OnEncounterHooks_SelectedEnteredExitedCardsDealt_ShouldDispatchCorrectly()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Vanessa, 250, 20, 10, 6);

        var encListenerTpl = new CardTemplate
        {
            Id = "enc_listener",
            InternalName = "Encounter Listener",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["on_sel"] = new AbilityDefinition
                {
                    Id = "on_sel",
                    Trigger = new RuleNode { Type = "TTriggerOnEncounterSelected" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(2),
                    },
                },
                ["on_ent"] = new AbilityDefinition
                {
                    Id = "on_ent",
                    Trigger = new RuleNode { Type = "TTriggerOnEncounterEntered" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(4),
                    },
                },
                ["on_exit"] = new AbilityDefinition
                {
                    Id = "on_exit",
                    Trigger = new RuleNode { Type = "TTriggerOnEncounterExited" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(1),
                    },
                },
            },
        };
        var item = new CardInstance(encListenerTpl);
        player.Hand.TryAddAuto(item);

        var encCard = new CardInstance(new CardTemplate { Id = "dummy_enc", InternalName = "Dummy Enc", Type = "EventEncounter" });

        OutOfCombatController.OnEncounterSelected(player, encCard, repo, new Random(1));
        Assert.Equal(12, player.Gold);

        OutOfCombatController.OnEncounterEntered(player, encCard, repo, new Random(1));
        Assert.Equal(16, player.Gold);

        OutOfCombatController.OnEncounterExited(player, encCard, repo, new Random(1));
        Assert.Equal(17, player.Gold);

        var dealt = new List<CardInstance> { new(MakeSimpleWeapon("dealt1", 5)) };
        var dealtBatch = OutOfCombatController.OnEncounterCardsDealt(player, encCard, dealt, repo, new Random(1));
        Assert.NotNull(dealtBatch);
    }

    [Fact]
    public void StashOnlyVsHandOnly_ActiveIn_ShouldBeRespectedOutOfCombat()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Mak, 250, 20, 10, 4);

        var stashOnlyTpl = new CardTemplate
        {
            Id = "stash_only_card",
            InternalName = "Stash Only",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["ab_stash"] = new AbilityDefinition
                {
                    Id = "ab_stash",
                    ActiveIn = "StashOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnHourStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(5),
                    },
                },
            },
        };

        var card = new CardInstance(stashOnlyTpl);
        player.Hand.TryAddAuto(card);
        OutOfCombatController.OnHourStarted(player, repo, new Random(1));
        Assert.Equal(10, player.Gold); // 在 Hand 不触发 StashOnly

        player.Hand.Remove(card);
        player.Stash.TryAddAuto(card);
        OutOfCombatController.OnHourStarted(player, repo, new Random(1));
        Assert.Equal(15, player.Gold); // 移入 Stash 后触发
    }

    [Fact]
    public void OnItemPurchased_RealLightbulb_ShouldSpawnCardIntoHandOrStash()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Dooley, 250, 20, 10, 6);
        var lightbulbTpl = repo.GetCard("2e4c2528-f3e8-4743-a1c9-637012d9eb76")!; // Lightbulb
        var bulb = new CardInstance(lightbulbTpl);
        player.Hand.TryAddAuto(bulb);

        var batch = OutOfCombatController.OnItemPurchased(player, bulb, repo, new Random(11));
        Assert.NotEmpty(batch.SpawnedCards);
    }

    [Fact]
    public void OnCardSoldOrConsumed_RealScrapMetal_ShouldReduceLeftmostWeaponCooldown()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Dooley, 250, 20, 10, 6);
        var scrapTpl = repo.GetCard("a167bf66-e29e-43d2-b30e-a480600c992d")!; // Scrap Metal

        var leftmostWeapon = new CardInstance(MakeSimpleWeapon("left_cd_wpn", 20));
        var scrap = new CardInstance(scrapTpl);
        player.Hand.TryPlaceOrShift(leftmostWeapon, 2);
        player.Hand.TryPlaceOrShift(scrap, 3);

        double cdBefore = leftmostWeapon.GetAttribute("CooldownMax");
        OutOfCombatController.OnCardSoldOrConsumed(player, scrap, repo, new Random(12));
        double cdAfter = leftmostWeapon.GetAttribute("CooldownMax");

        Assert.True(cdAfter < cdBefore, $"Scrap Metal 应降低最左侧物品冷却时间: before={cdBefore}, after={cdAfter}");
        Assert.DoesNotContain(scrap, player.Hand.Cards);
    }

    [Fact]
    public void OutOfCombat_QuestProgressAndCompletion_ShouldLogAndDispatchCompletionTrigger()
    {
        var repo = Repo.Value;
        var player = new PlayerState("p1", "P1", HeroNames.Mak, 250, 20, 10, 6);

        var questTpl = new CardTemplate
        {
            Id = "ooc_quest_card",
            InternalName = "OOC Quest Card",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["on_complete"] = new AbilityDefinition
                {
                    Id = "on_complete",
                    Trigger = new RuleNode { Type = "TTriggerOnCardQuestCompleted" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
                        RawValueNode = JsonFixed(9),
                    },
                },
            },
            Quests =
            [
                new QuestDefinition
                {
                    Entries =
                    [
                        new QuestEntry
                        {
                            AttributeType = "Quest_1",
                            Target = 1,
                            Trigger = new RuleNode { Type = "TTriggerOnCardPurchased" },
                            Reward = new QuestReward
                            {
                                Attributes = new Dictionary<string, double> { ["DamageAmount"] = 40 },
                            },
                        },
                    ],
                },
            ],
        };

        var questCard = new CardInstance(questTpl);
        player.Hand.TryAddAuto(questCard);

        var batch = OutOfCombatController.OnItemPurchased(player, questCard, repo, new Random(15));

        Assert.Contains("Quest_1", questCard.CompletedQuests);
        Assert.Equal(40, questCard.GetAttribute("DamageAmount"));
        Assert.Equal(19, player.Gold);
        Assert.Contains(questCard, batch.QuestCompletedCards);
        Assert.Single(batch.QuestLogs);
        Assert.True(batch.QuestLogs[0].Completed);
    }
}
