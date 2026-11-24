using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1B（规则节点、三层同步、临时变形栈、原子棋盘与附魔）纯 BazaarCore 零静态写回归测试。
/// 不引用 BazaarServer、不修改全局静态态，完全对齐 Wave 0 集合并行规范。
/// </summary>
public sealed class Wave1B_StateAndRulesP0P1Tests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private const string SpicesId = "c05828f1-39e6-47aa-9041-216dfa60d83d";
    private const string ClockworkBladesId = "af24dea0-6cc9-49dc-acec-d90313b46b72";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string SatchelId = "3ce7312c-aa95-4711-9a2b-827609a380b4";
    private const string MysteryFlavorId = "78eb19fd-4da4-45d5-b4cb-d669b50b6ed6";
    private const string AtlasId = "03842226-6605-466f-9ea3-a8a2dbea67e1";
    private const string MakeAWishId = "3aaaaccc-98a6-4d3b-8585-469d8abef2df";
    private const string GetAGumballId = "07a62f0e-1e52-4b1a-8b6d-cc8013aa5e57";

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static PlayerState CreatePlayer(string id, string hero, int hp)
        => new(id, id, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 10);

    private static CardInstance CreateItem(
        string id,
        Dictionary<string, double>? attrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        CardSize size = CardSize.Small,
        CardTier tier = CardTier.Bronze,
        Dictionary<string, EnchantmentData>? enchantments = null,
        List<QuestDefinition>? quests = null)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = size.ToString(),
            StartingTier = tier.ToString(),
            Tiers = new Dictionary<string, TierData>
            {
                [tier.ToString()] = new TierData
                {
                    Attributes = attrs != null ? new Dictionary<string, double>(attrs) : new(),
                    AbilityIds = abilities != null ? abilities.Keys.ToList() : []
                },
                ["Silver"] = new TierData
                {
                    Attributes = attrs != null ? new Dictionary<string, double>(attrs) : new(),
                    AbilityIds = abilities != null ? abilities.Keys.ToList() : []
                }
            },
            Abilities = abilities ?? new(),
            Enchantments = enchantments,
            Quests = quests
        };
        return new CardInstance(tpl, tier, null, id);
    }

    [Fact]
    public void P0_02_AttributeLowest_SkipsMissingAttribute_AndHighestStillRejectsNonPositive()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var spices = new CardInstance(repo.GetCard(SpicesId)!, CardTier.Gold, null, "spices_inst");
        var weakWpn = CreateItem("wpn_15", attrs: new() { ["DamageAmount"] = 15, ["CooldownMax"] = 3000 });
        var strongWpn = CreateItem("wpn_40", attrs: new() { ["DamageAmount"] = 40, ["CooldownMax"] = 3000 });
        p1.Hand.TryPlaceOrShift(spices, 0);
        p1.Hand.TryPlaceOrShift(weakWpn, 1);
        p1.Hand.TryPlaceOrShift(strongWpn, 2);

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1)) { SelfCard = spices };
        var lowestTarget = ParseNode("""
        {
          "$type": "TTargetCardSection",
          "TargetSection": "SelfHand",
          "ExcludeSelf": false,
          "Conditions": { "$type": "TCardConditionalAttributeLowest", "AttributeType": "DamageAmount" }
        }
        """);
        var picked = TargetResolver.ResolveCards(lowestTarget, ctx);
        Assert.Single(picked);
        Assert.Same(weakWpn, picked[0]);

        // 红线 2 守护：TCardConditionalAttributeHighest 对全员 <= 0（含负值）仍严格返回空集
        var pNeg = CreatePlayer("pn", HeroNames.Vanessa, 1000);
        var neg1 = CreateItem("neg1", attrs: new() { ["Custom_0"] = -5 });
        var neg2 = CreateItem("neg2", attrs: new() { ["Custom_0"] = 0 });
        pNeg.Hand.TryAddAuto(neg1);
        pNeg.Hand.TryAddAuto(neg2);
        var ctxNeg = new RuleExecutionContext(repo, RuleScope.InCombat, pNeg, null, new Random(1)) { SelfCard = neg1 };
        var highestTarget = ParseNode("""
        {
          "$type": "TTargetCardSection",
          "TargetSection": "SelfHand",
          "Conditions": { "$type": "TCardConditionalAttributeHighest", "AttributeType": "Custom_0" }
        }
        """);
        Assert.Empty(TargetResolver.ResolveCards(highestTarget, ctxNeg));
    }

    [Fact]
    public void P0_03_HealthMultiplierP1_DoesNotCompoundAcrossCombats_NorBakeDayHp()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 250);
        p1.ModifyAttribute("HealthMax", "Add", 100, DurationTarget.Day); // 模拟 Relax 当日血量
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 50);
        p1.Hand.TryAddAuto(CreateItem("fast_hit", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }));

        var titanMod = new CombatModifiers { HealthMultiplierP1 = 2.5 };
        for (int round = 0; round < 3; round++)
        {
            var res = CombatSimulator.Simulate(repo, p1, p2, seed: 300 + round, combatType: "PVP", recordTimeline: false, recordEvents: false, syncPermanentChanges: true, modifiers: titanMod);
            Assert.Equal("p1", res.WinnerPlayerId);
            Assert.Equal(250, p1.Attributes["HealthMax"]);
            Assert.Equal(100, p1.DayAdd["HealthMax"]);
            Assert.Equal(350, p1.HealthMax);
        }
    }

    [Fact]
    public void P0_04_PostCombatSync_PreservesClockworkBladesDayCdr()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 200);
        var blades = new CardInstance(repo.GetCard(ClockworkBladesId)!, CardTier.Gold, null, "clockwork_blades");
        p1.Hand.TryAddAuto(blades);
        p2.Hand.TryAddAuto(CreateItem("p2_poke", attrs: new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 5 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 401, syncPermanentChanges: true, recordEvents: true);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == blades.InstanceId);
        Assert.True(blades.DayAdd.GetValueOrDefault("PercentCooldownReduction", 0) > 0, "Clockwork Blades 战中累积的 DayAdd 减 CD 战后必须回写保留");
        p1.ClearEndOfDayModifiers();
        Assert.Equal(0, blades.DayAdd.GetValueOrDefault("PercentCooldownReduction", 0));

        // 玩家层 Day/Hour 非瞬态属性 + 卡牌 DayEnchantment/HourEnchantment 同步 + CloneNonCombatState 无损深拷
        var tp1 = CreatePlayer("tp1", HeroNames.Vanessa, 500);
        var tp2 = CreatePlayer("tp2", HeroNames.Vanessa, 50);
        var enchTarget = CreateItem("ench_day_target", enchantments: new() { ["Fiery"] = new(), ["Turbo"] = new() });
        tp1.Hand.TryAddAuto(enchTarget);
        tp1.Hand.TryAddAuto(CreateItem(
            "day_hour_buffer",
            attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 100 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        {"$type":"TActionPlayerModifyAttribute","AttributeType":"HealthMax","Operation":"Add","Value":{"$type":"TFixedValue","Value":75},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfDay"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionPlayerModifyAttribute","AttributeType":"CritChance","Operation":"Add","Value":{"$type":"TFixedValue","Value":15},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfHour"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionCardEnchant","Enchantment":"Fiery","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfDay"},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}
                      ]
                    }
                    """)
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));

        CombatSimulator.Simulate(repo, tp1, tp2, seed: 402, combatType: "PVP", syncPermanentChanges: true);
        Assert.Equal(75, tp1.DayAdd.GetValueOrDefault("HealthMax", 0));
        Assert.Equal(575, tp1.HealthMax);
        Assert.Equal(15, tp1.HourAdd.GetValueOrDefault("CritChance", 0));
        Assert.Equal("Fiery", enchTarget.DayEnchantment);
        Assert.Equal("Fiery", enchTarget.Enchantment);

        // CloneNonCombatState 完整保留 Day/Hour 附魔、价格上下文与 6 组非战斗属性字典
        enchTarget.ApplyEnchantment("Turbo", DurationTarget.Hour, preventOverride: false);
        enchTarget.DayAdd["DamageAmount"] = 12;
        enchTarget.HourAdd["CritChance"] = 7;
        enchTarget.PermanentAdd["SellPrice"] = 3;
        var cloned = enchTarget.CloneNonCombatState("cloned_id_check");
        Assert.Equal("cloned_id_check", cloned.InstanceId);
        Assert.Equal("Fiery", cloned.DayEnchantment);
        Assert.Equal("Turbo", cloned.HourEnchantment);
        Assert.Equal("Turbo", cloned.Enchantment);
        Assert.Equal(12, cloned.DayAdd["DamageAmount"]);
        Assert.Equal(7, cloned.HourAdd["CritChance"]);
        Assert.Equal(3, cloned.PermanentAdd["SellPrice"]);
    }

    [Fact]
    public void P0_01_TActionGameDealCards_PreservesSelectionContextRulesEndToEnd()
    {
        var repo = Repo.Value;
        static RuleNode? FindDealAction(CardTemplate? tpl)
        {
            if (tpl == null) return null;
            foreach (var ab in tpl.Abilities.Values)
            {
                if (ab.Action == null) continue;
                if (ab.Action.Type == "TActionGameDealCards") return ab.Action;
                if (ab.Action.Actions != null)
                {
                    var found = ab.Action.Actions.FirstOrDefault(a => a.Type == "TActionGameDealCards");
                    if (found != null) return found;
                }
            }
            return null;
        }

        // 1. 真实库 Make a Wish（Free + Multi）与 Get a Gumball（SelectionIsFree = false）
        var wishAction = FindDealAction(repo.GetCard(MakeAWishId));
        var gumballAction = FindDealAction(repo.GetCard(GetAGumballId));
        Assert.NotNull(wishAction);
        Assert.NotNull(gumballAction);

        var wishRules1 = wishAction!.GetSelectionContextRulesData();
        var wishRules2 = wishAction.GetSelectionContextRulesData();
        Assert.NotNull(wishRules1);
        Assert.NotNull(wishRules2);
        Assert.NotSame(wishRules1, wishRules2); // 独立深拷贝防跨局污染
        wishRules1!.CanSelectMultiple = !wishRules1.CanSelectMultiple;
        Assert.NotEqual(wishRules1.CanSelectMultiple, wishRules2!.CanSelectMultiple);

        var gumballRules = gumballAction!.GetSelectionContextRulesData();
        Assert.NotNull(gumballRules);
        Assert.False(gumballRules!.SelectionIsFree);

        // 2. FromSelectionRulesData + RerollRules + CanExit=false + OutOfCombatController 全链路透传
        var customRules = new SelectionRulesData
        {
            CanSelectMultiple = true,
            SelectionIsFree = false,
            CanExit = false,
            WillAutoSellOnExit = true,
            MaxSelections = 2,
            RerollRules = new RerollRulesData { StartingCost = 3, CostIncrease = 1, TotalAllowedRerolls = 2 }
        };
        var dealNode = new RuleNode
        {
            Type = "TActionGameDealCards",
            SpawnMode = "Replace",
            SelectionContextRules = RuleNode.FromSelectionRulesData(customRules),
            SpawnContext = ParseNode($$"""
            {
              "$type": "TSpawnContextQuery",
              "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
            }
            """)
        };
        var p1 = CreatePlayer("p_deal", HeroNames.Vanessa, 500);
        var encCard = CreateItem("enc_deal", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardSelected"}"""),
                Action = dealNode
            }
        });
        var batch = OutOfCombatController.OnCardSelected(p1, encCard, repo, rng: new Random(101));
        Assert.Single(batch.DealtSelectionPayloads);
        Assert.Single(batch.DealtSelectionRequests);
        var payload = batch.DealtSelectionPayloads[0];
        Assert.Single(payload.Cards);
        Assert.NotNull(payload.SelectionContextRules);
        Assert.True(payload.SelectionContextRules!.CanSelectMultiple);
        Assert.False(payload.SelectionContextRules.SelectionIsFree);
        Assert.False(payload.SelectionContextRules.CanExit);
        Assert.True(payload.SelectionContextRules.WillAutoSellOnExit);
        Assert.Equal(2, payload.SelectionContextRules.MaxSelections);
        Assert.Equal(3, payload.SelectionContextRules.RerollRules?.StartingCost);
        Assert.Equal(2, payload.SelectionContextRules.RerollRules?.TotalAllowedRerolls);
    }

    [Fact]
    public void P0_05_TemporaryTransform_PreservesPreTransformPermanentGrowth()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
        var atlas = new CardInstance(repo.GetCard(AtlasId)!, CardTier.Silver, null, "atlas_grow");
        double beforeSell = atlas.PermanentAdd.GetValueOrDefault("SellPrice", 0);
        p1.Hand.TryPlaceOrShift(atlas, 0);

        // 对手在 1000ms 将玩家左卡临时变形（UntilEndOfCombat）为 Cutlass
        p2.Hand.TryAddAuto(CreateItem(
            "temp_transformer",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "OpponentHand" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }));
        // p1 在 2000ms 斩杀对手
        p1.Hand.TryPlaceOrShift(CreateItem("finisher", attrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 600 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }), 3);

        CombatSimulator.Simulate(repo, p1, p2, seed: 501, combatType: "PVE", syncPermanentChanges: true);
        Assert.True(atlas.PermanentAdd.GetValueOrDefault("SellPrice", 0) > beforeSell, "战中被临时变形前获得的永久 SellPrice 成长战后不得丢失");
    }

    [Fact]
    public void P0_05_PermanentPlusTemporaryChainTransform_DedupsToSingleReplacement_AndBlocksLaundering()
    {
        var repo = Repo.Value;

        // 场景 A（#29-2）：原卡 A 战中先永久变形为 B（Cutlass），随后 B 被临时变形为 C（Spices）
        // 战后 SyncPermanentChangesBack 必须精确产出 1 张 B（严禁因 Hand + RestoredTemporaryOriginals 双扫克隆出 2 张）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
        var cardA = CreateItem("chain_a", size: CardSize.Medium);
        p1.Hand.TryPlaceOrShift(cardA, 0);

        // t=0: A 永久变形为 Cutlass（B）；t=1000: B 临时变形为 Spices（C）；t=2000: 斩杀
        p1.Skills.Add(new CardInstance(new CardTemplate
        {
            Id = "perm_tf_skill",
            InternalName = "perm_tf_skill",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Target": { "$type": "TTargetCardXMost", "TargetSection": "SelfHand", "TargetMode": "LeftMost" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }
        }, CardTier.Bronze, null, "perm_tf_skill"));
        p2.Hand.TryAddAuto(CreateItem(
            "temp_after_perm",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "OpponentHand" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{SpicesId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }));
        p1.Hand.TryPlaceOrShift(CreateItem("finisher_chain", attrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 600 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }), 4);

        var resA = CombatSimulator.Simulate(repo, p1, p2, seed: 502, combatType: "PVP", syncPermanentChanges: true);
        Assert.True(resA.PostCombatPermanentSync!.PermanentReplacements.TryGetValue(cardA.InstanceId, out var repsA));
        Assert.Single(repsA);
        Assert.Equal(CutlassId, repsA[0].Template.Id);
        Assert.Single(p1.Hand.Cards, c => c.Template.Id == CutlassId);

        // 场景 B（防洗白）：原卡 A 先被临时变形为 B（Cutlass），随后 B 遭遇永久变形动作（Duration==null）变为 C（Spices）
        // 必须强制 C.IsCombatTemporary == true，战后局外原卡 A 完好无损不被 C 覆写！
        var lp1 = CreatePlayer("lp1", HeroNames.Vanessa, 2000);
        var lp2 = CreatePlayer("lp2", HeroNames.Vanessa, 500);
        var origCard = CreateItem("orig_safe", size: CardSize.Medium);
        origCard.PermanentAdd["SellPrice"] = 5;
        lp1.Hand.TryPlaceOrShift(origCard, 0);
        lp2.Skills.Add(new CardInstance(new CardTemplate
        {
            Id = "temp_first",
            InternalName = "temp_first",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "OpponentHand" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }
        }, CardTier.Bronze, null, "temp_first"));
        lp2.Hand.TryAddAuto(CreateItem(
            "perm_second_launder",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "OpponentHand" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{SpicesId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }));
        lp1.Hand.TryPlaceOrShift(CreateItem("finisher_launder", attrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 600 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }), 4);

        var resB = CombatSimulator.Simulate(repo, lp1, lp2, seed: 503, combatType: "PVP", syncPermanentChanges: true);
        Assert.False(resB.PostCombatPermanentSync!.PermanentReplacements.ContainsKey(origCard.InstanceId));
        Assert.Contains(origCard, lp1.Hand.Cards);
        Assert.DoesNotContain(lp1.Hand.Cards, c => c.Template.Id == SpicesId);
    }

    [Fact]
    public void P0_05_StaggeredTimedChainedTransform_ShortOuterBeforeLongInner_MustNotResurrectStaleTempCard()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);

        // 原卡 A 位于 slot 0；finisher 位于 slot 5，t=7500ms 终结战斗（晚于内层 6500ms 到期点）
        var cardA = CreateItem("stagger_orig_a", size: CardSize.Medium);
        p1.Hand.TryPlaceOrShift(cardA, 0);
        var finisher = CreateItem(
            "stagger_finisher",
            size: CardSize.Small,
            attrs: new() { ["CooldownMax"] = 7500, ["DamageAmount"] = 1000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1.Hand.TryPlaceOrShift(finisher, 5);

        // t=1000ms 外层限时变形：A -> B (Cutlass)，持续 2000ms（t=3000ms 先到期，连带撤销内层栈帧并还原 A）
        p2.Hand.TryAddAuto(CreateItem(
            "outer_short_tf",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Duration": { "$type": "TCombatDuration", "DurationInMs": 2000 },
                      "Target": { "$type": "TTargetCardXMost", "TargetSection": "OpponentHand", "TargetMode": "LeftMost" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }));

        // t=1500ms 内层限时变形：B (Cutlass) -> C (Spices)，持续 5000ms（原定 t=6500ms 到期；外层已提前撤销后严禁复活 B）
        p2.Hand.TryAddAuto(CreateItem(
            "inner_long_tf",
            attrs: new() { ["CooldownMax"] = 1500, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionCardTransform",
                      "Duration": { "$type": "TCombatDuration", "DurationInMs": 5000 },
                      "Target": { "$type": "TTargetCardXMost", "TargetSection": "OpponentHand", "TargetMode": "LeftMost" },
                      "SpawnContext": {
                        "$type": "TSpawnContextQuery",
                        "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{SpicesId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                      }
                    }
                    """)
                }
            }));

        var res = CombatSimulator.Simulate(
            repo, p1, p2, seed: 504, combatType: "PVP",
            recordTimeline: true, recordEvents: true, syncPermanentChanges: true,
            modifiers: null, sandstormEnabled: false);

        Assert.True(res.DurationMs >= 7000, $"战斗应持续超过 6500ms 内层到期点，实际 {res.DurationMs}ms");
        var tfEvents = res.Events.Where(e => e.Type == CombatEventType.CardTransformed).ToList();
        Assert.Equal(2, tfEvents.Count);
        string tempBId = tfEvents[0].TargetCardInstanceId!;
        string tempCId = tfEvents[1].TargetCardInstanceId!;

        var reverts = res.Events.Where(e => e.Type == CombatEventType.CardTransformReverted).ToList();
        Assert.Single(reverts);
        Assert.InRange(reverts[0].TimeMs, 2900, 3200);
        Assert.Equal(cardA.InstanceId, reverts[0].TargetCardInstanceId);
        Assert.DoesNotContain(reverts, e => e.TimeMs >= 6000);

        // t >= 6600ms（超过 6500ms 内层原定到期点后）盘面中仍保留原卡 A 与 finisher，已废弃临时卡 B/C 绝不复活共存
        var afterInnerFrame = res.Timeline.Last(f => f.TimeMs >= 6600);
        Assert.Contains(afterInnerFrame.Cards, c => c.InstanceId == cardA.InstanceId && !c.IsDestroyed);
        Assert.Contains(afterInnerFrame.Cards, c => c.InstanceId == finisher.InstanceId && !c.IsDestroyed);
        Assert.DoesNotContain(afterInnerFrame.Cards, c => c.InstanceId == tempBId || c.InstanceId == tempCId);
        Assert.DoesNotContain(res.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == tempBId && e.TimeMs > 3000);
    }

    [Fact]
    public void P0_06_FullBoardGrowingTransform_DoesNotSwallowCard()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        // 填满全部 10 格（10 张 Small）
        var smallToGrow = CreateItem("small_0", size: CardSize.Small);
        p1.Hand.TryPlaceOrShift(smallToGrow, 0);
        for (int s = 1; s < 10; s++)
            p1.Hand.TryPlaceOrShift(CreateItem($"filler_{s}", size: CardSize.Small), s);

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1)) { SelfCard = smallToGrow };
        // 尝试将 1 格卡变形为 2 格 Medium 卡（Cutlass）
        var growAction = ParseNode($$"""
        {
          "$type": "TActionCardTransform",
          "Target": { "$type": "TTargetCardSelf" },
          "SpawnContext": {
            "$type": "TSpawnContextQuery",
            "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{CutlassId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
          }
        }
        """);
        int transformedCount = 0;
        ctx.OnCardTransformed = (_, _, _) => transformedCount++;
        AbilityExecutor.ExecuteAction(growAction, smallToGrow, ctx);

        Assert.Contains(smallToGrow, p1.Hand.Cards);
        Assert.False(smallToGrow.IsDestroyed);
        Assert.Equal(0, smallToGrow.StartSlot);
        Assert.Equal(0, transformedCount);
        Assert.True(p1.Hand.ValidateInvariants(out var err), err);

        // 2. Stash 1:2 变形：全部产物优先落入 Stash，绝不错塞进 Hand
        var sp = CreatePlayer("p_stash", HeroNames.Vanessa, 1000);
        var stashMedium = CreateItem("stash_src", size: CardSize.Medium);
        sp.Stash.TryPlaceOrShift(stashMedium, 0);
        var stashCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, sp, null, new Random(2)) { SelfCard = stashMedium };
        var stashMultiAction = ParseNode($$"""
        {
          "$type": "TActionCardTransform",
          "Target": { "$type": "TTargetCardSelf" },
          "SpawnContext": {
            "$type": "TSpawnContextQuery",
            "Limit": { "$type": "TFixedValue", "Value": 2 },
            "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{SpicesId}}", "{{MysteryFlavorId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 2 } }]
          }
        }
        """);
        AbilityExecutor.ExecuteAction(stashMultiAction, stashMedium, stashCtx);
        Assert.Empty(sp.Hand.Cards);
        Assert.Equal(2, sp.Stash.Cards.Count);
        Assert.True(sp.Stash.ValidateInvariants(out var stashErr), stashErr);

        // 3. TActionCardTransformDestroyed 定时恢复到期后保持原卡 IsDestroyed == true（绝不免费复活）
        var dp1 = CreatePlayer("dp1", HeroNames.Vanessa, 2000);
        var dp2 = CreatePlayer("dp2", HeroNames.Vanessa, 2000);
        var brokenItem = CreateItem("broken_item", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 });
        dp1.Hand.TryPlaceOrShift(brokenItem, 0);
        dp1.Skills.Add(new CardInstance(new CardTemplate
        {
            Id = "destroy_then_timed_tf",
            InternalName = "destroy_then_timed_tf",
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode($$"""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        { "$type": "TActionCardDestroy", "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" } },
                        {
                          "$type": "TActionCardTransformDestroyed",
                          "Duration": { "$type": "TCombatDuration", "DurationInMs": 500 },
                          "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" },
                          "SpawnContext": {
                            "$type": "TSpawnContextQuery",
                            "Groups": [{ "$type": "TSpawnGroup", "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{SpicesId}}"] }], "Limit": { "$type": "TFixedValue", "Value": 1 } }]
                          }
                        }
                      ]
                    }
                    """)
                }
            }
        }, CardTier.Bronze, null, "destroy_then_timed_tf"));
        var dRes = CombatSimulator.Simulate(repo, dp1, dp2, seed: 602, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        Assert.Contains(dRes.Events, e => e.Type == CombatEventType.CardTransformReverted && e.TargetCardInstanceId == brokenItem.InstanceId);
        var revertedFrame = dRes.Timeline.First(t => t.TimeMs >= 600).Cards.First(c => c.InstanceId == brokenItem.InstanceId);
        Assert.True(revertedFrame.IsDestroyed, "TransformDestroyed 到期还原后必须保留变形前的 IsDestroyed 快照态，绝不可免费复活");

        // 4. TryReplaceCards 双满原子回滚（邻居 StartSlot 完好无损）+ Hand 满溢出至 Stash
        var fullP = CreatePlayer("full_rb", HeroNames.Vanessa, 1000);
        var targetSmall = CreateItem("rb_small", size: CardSize.Small);
        var neighbor = CreateItem("rb_neighbor", size: CardSize.Large);
        fullP.Hand.TryPlaceOrShift(targetSmall, 0);
        fullP.Hand.TryPlaceOrShift(neighbor, 1);
        for (int s = 4; s < 10; s++)
            fullP.Hand.TryPlaceOrShift(CreateItem($"rb_pad_{s}", size: CardSize.Small), s);
        for (int s = 0; s < 10; s++)
            fullP.Stash.TryPlaceOrShift(CreateItem($"rb_st_{s}", size: CardSize.Small), s);

        var bigRep = CreateItem("rb_big_rep", size: CardSize.Medium);
        Assert.False(fullP.Hand.TryReplaceCards(targetSmall, [bigRep], fullP.Stash, out var placedP, out var placedO));
        Assert.Empty(placedP);
        Assert.Empty(placedO);
        Assert.Equal(-1, bigRep.StartSlot);
        Assert.Equal(0, targetSmall.StartSlot);
        Assert.Equal(1, neighbor.StartSlot);
        Assert.Contains(targetSmall, fullP.Hand.Cards);
        Assert.True(fullP.Hand.ValidateInvariants(out var rbErr), rbErr);

        // 腾出 Stash 空位后再次替换，大产物成功溢出至 Stash
        fullP.Stash.Remove(fullP.Stash.Cards[0]);
        fullP.Stash.Remove(fullP.Stash.Cards[0]);
        Assert.True(fullP.Hand.TryReplaceCards(targetSmall, [bigRep], fullP.Stash, out var placedP2, out var placedO2));
        Assert.Empty(placedP2);
        Assert.Single(placedO2, bigRep);
        Assert.Contains(bigRep, fullP.Stash.Cards);
    }

    [Fact]
    public void P0_07_TemporaryUpgradeInheritEnchantment_DoesNotLeakToPermanent()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 50);
        var target = new CardInstance(repo.GetCard(CutlassId)!, CardTier.Bronze, null, "cutlass_plain");
        var tempUpgrader = CreateItem(
            "temp_upgrader",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                    {
                      "$type": "TActionCardUpgrade",
                      "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                      "InheritEnchantment": true,
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
                    }
                    """)
                }
            },
            enchantments: new Dictionary<string, EnchantmentData> { ["Fiery"] = new() });
        tempUpgrader.ApplyEnchantment("Fiery");
        p1.Hand.TryAddAuto(tempUpgrader);
        p1.Hand.TryAddAuto(target);
        p1.Hand.TryAddAuto(CreateItem("win_hit", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }));

        CombatSimulator.Simulate(repo, p1, p2, seed: 701, syncPermanentChanges: true);
        Assert.Null(target.PermanentEnchantment);
        Assert.Null(target.Enchantment);

        // 2. 携带 DayEnchantment（Mystery Flavor 语义）的源卡触发永久升级与永久变形 -> 目标严格继承为 DayEnchantment，换日清零绝不洗白为永久
        var dp = CreatePlayer("day_inh_p", HeroNames.Vanessa, 1000);
        var dayHammer = CreateItem(
            "day_hammer",
            enchantments: new() { ["Fiery"] = new() });
        dayHammer.ApplyEnchantment("Fiery", DurationTarget.Day, preventOverride: false);
        var upgradeRecv = CreateItem("day_upg_recv", tier: CardTier.Bronze, enchantments: new() { ["Fiery"] = new() });
        dp.Hand.TryAddAuto(dayHammer);
        dp.Hand.TryAddAuto(upgradeRecv);
        var dayCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, dp, null, new Random(7)) { SelfCard = dayHammer };
        var permUpgradeAction = ParseNode("""{"$type":"TActionCardUpgrade","InheritEnchantment":true,"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""");
        AbilityExecutor.ExecuteAction(permUpgradeAction, dayHammer, dayCtx);
        Assert.Null(upgradeRecv.PermanentEnchantment);
        Assert.Equal("Fiery", upgradeRecv.DayEnchantment);
        Assert.Equal("Fiery", upgradeRecv.Enchantment);
        dp.ClearEndOfDayModifiers();
        Assert.Null(upgradeRecv.Enchantment);

        // 3. 对已有未到期 DayEnchantment 的卡牌写入新 PermanentEnchantment，不清空未到期 DayEnchantment，换日后平滑回落到新永久附魔
        var layeredCard = CreateItem("layered_ench", enchantments: new() { ["Fiery"] = new(), ["Heavy"] = new() });
        layeredCard.ApplyEnchantment("Fiery", DurationTarget.Day, preventOverride: false);
        layeredCard.ApplyEnchantment("Heavy", DurationTarget.Permanent, preventOverride: false);
        Assert.Equal("Heavy", layeredCard.PermanentEnchantment);
        Assert.Equal("Fiery", layeredCard.DayEnchantment);
        Assert.Equal("Fiery", layeredCard.Enchantment);
        layeredCard.ClearEndOfDayModifiers();
        Assert.Equal("Heavy", layeredCard.Enchantment);
    }

    [Fact]
    public void P0_07_BazaarCoreInternal_ZeroBoolApplyEnchantmentCalls()
    {
        // 纯只读源码静态防线：确认 BazaarCore/{Combat,Engine,Runtime,Models} 内零残留旧布尔重载 ApplyEnchantment 调用
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? coreDir = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BazaarCore");
            if (File.Exists(Path.Combine(candidate, "BazaarCore.csproj")))
            {
                coreDir = candidate;
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(coreDir);

        string[] scopedSubdirs = ["Combat", "Engine", "Runtime", "Models"];
        var violations = new List<string>();
        var callRegex = new System.Text.RegularExpressions.Regex(@"\.ApplyEnchantment\s*\(([^;)]*)\)");

        foreach (var sub in scopedSubdirs)
        {
            var fullSub = Path.Combine(coreDir!, sub);
            if (!Directory.Exists(fullSub)) continue;
            foreach (var file in Directory.GetFiles(fullSub, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (System.Text.RegularExpressions.Match m in callRegex.Matches(lines[i]))
                    {
                        var args = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
                        if (args.Length >= 2)
                        {
                            string secondArg = args[1];
                            bool isBoolStyle = secondArg.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || secondArg.Equals("false", StringComparison.OrdinalIgnoreCase)
                                || secondArg.StartsWith("preventOverride", StringComparison.OrdinalIgnoreCase)
                                || secondArg.StartsWith("isTemporary", StringComparison.OrdinalIgnoreCase);
                            if (isBoolStyle)
                                violations.Add($"{Path.GetRelativePath(coreDir!, file)}:{i + 1}: {m.Value}");
                        }
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "发现 BazaarCore 内部残留旧布尔重载 ApplyEnchantment 调用：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void P1_01_CostCardAttribute_EnforcesDeduction_AndRejectsUnknownCost()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var card = CreateItem("cost_card", attrs: new() { ["AmmoMax"] = 2, ["Custom_0"] = 1 });
        card.CurrentAmmo = 0;
        p1.Hand.TryAddAuto(card);
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1)) { SelfCard = card };

        var costAmmoAction = ParseNode("""
        {
          "$type": "TActionPlayerShieldApply",
          "Cost": { "$type": "TActionCostCardAttribute", "CardAttributeType": "Ammo", "Value": { "$type": "TFixedValue", "Value": 1 } },
          "ReferenceValue": { "$type": "TFixedValue", "Value": 50 },
          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
        }
        """);
        // Ammo == 0，必须拒绝执行！
        Assert.False(AbilityExecutor.ExecuteAction(costAmmoAction, card, ctx));
        Assert.Equal(0, p1.Shield);

        // 余额充足时精确扣减 Ammo 并成功施加护盾
        card.CurrentAmmo = 2;
        Assert.True(AbilityExecutor.ExecuteAction(costAmmoAction, card, ctx));
        Assert.Equal(1, card.CurrentAmmo);
        Assert.Equal(50, p1.Shield);

        // 未知 Cost 类型绝不得免费放行，且触发未知节点遥测
        var unknownCaptured = new List<(string Cat, string Type)>();
        ctx.OnUnknownRuleType = (cat, t) => unknownCaptured.Add((cat, t));
        var unknownCostAction = ParseNode("""
        {
          "$type": "TActionPlayerShieldApply",
          "Cost": { "$type": "TActionCostUnknownFutureType", "Value": { "$type": "TFixedValue", "Value": 1 } },
          "ReferenceValue": { "$type": "TFixedValue", "Value": 50 },
          "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }
        }
        """);
        Assert.False(AbilityExecutor.ExecuteAction(unknownCostAction, card, ctx));
        Assert.Equal(50, p1.Shield);
        Assert.Contains(unknownCaptured, x => x.Cat == "TActionCost" && x.Type == "TActionCostUnknownFutureType");

        // 5 类缺失动作：ShieldRemove / RegenRemove / RageRemove / Heat / Chill
        p1.ModifyAttribute("HealthRegen", "Add", 20, DurationTarget.Combat);
        p1.Rage = 40;
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionPlayerShieldRemove","Value":{"$type":"TFixedValue","Value":30},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""), card, ctx);
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionPlayerRegenRemove","Value":{"$type":"TFixedValue","Value":8},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""), card, ctx);
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionPlayerRageRemove","Value":{"$type":"TFixedValue","Value":15},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""), card, ctx);
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionCardHeat","Target":{"$type":"TTargetCardSelf"}}"""), card, ctx);
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionCardChill","Target":{"$type":"TTargetCardSelf"}}"""), card, ctx);
        Assert.Equal(20, p1.Shield);
        Assert.Equal(12, p1.GetAttribute("HealthRegen"));
        Assert.Equal(25, p1.Rage);
        Assert.True(card.Heated);
        Assert.True(card.Chilled);
    }

    [Fact]
    public void P1_03_P1_04_ZeroReloadNoTrigger_AndZeroTargetCountNoOp()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var fullAmmo = CreateItem("full_ammo", attrs: new() { ["AmmoMax"] = 2 });
        fullAmmo.CurrentAmmo = 2;
        var customZero = CreateItem("zero_cnt", attrs: new() { ["Custom_0"] = 0 });
        p1.Hand.TryAddAuto(fullAmmo);
        p1.Hand.TryAddAuto(customZero);

        int reloadEvents = 0;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1))
        {
            SelfCard = customZero,
            OnDetailedTriggeredEvent = (ev, _, _, _, _, _, _, _) => { if (ev == "TTriggerOnCardPerformedReload") reloadEvents++; }
        };
        var reloadFull = ParseNode("""{"$type":"TActionCardReload","Value":{"$type":"TFixedValue","Value":2},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}""");
        AbilityExecutor.ExecuteAction(reloadFull, customZero, ctx);
        Assert.Equal(0, reloadEvents);

        // 动态 TargetCount == 0 时命中 0 张目标卡
        var zeroTargetUpgrade = ParseNode("""
        {
          "$type": "TActionCardModifyAttribute",
          "AttributeType": "DamageAmount",
          "Operation": "Add",
          "Value": { "$type": "TFixedValue", "Value": 10 },
          "TargetCount": { "$type": "TReferenceValueCardAttribute", "AttributeType": "Custom_0", "Target": { "$type": "TTargetCardSelf" } },
          "Target": { "$type": "TTargetCardRandom", "TargetSection": "SelfHand" }
        }
        """);
        AbilityExecutor.ExecuteAction(zeroTargetUpgrade, customZero, ctx);
        Assert.Equal(0, fullAmmo.GetAttribute("DamageAmount"));
        Assert.Equal(0, customZero.GetAttribute("DamageAmount"));

        // TActionCardAddTagsRandom 计数为 0 时添加 0 个标签（不再强抬为 1）
        var zeroAddTag = ParseNode("""
        {
          "$type": "TActionCardAddTagsRandom",
          "Tags": ["Weapon", "Tool"],
          "Value": { "$type": "TFixedValue", "Value": 0 },
          "Target": { "$type": "TTargetCardSelf" }
        }
        """);
        AbilityExecutor.ExecuteAction(zeroAddTag, customZero, ctx);
        Assert.False(customZero.HasTag("Weapon"));
        Assert.False(customZero.HasTag("Tool"));
    }

    [Fact]
    public void P1_05_ForceUse_AllFrozenFallsBack_WithoutBreakingActivePriority()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var frozen1 = CreateItem("f1", attrs: new() { ["CooldownMax"] = 3000 });
        var frozen2 = CreateItem("f2", attrs: new() { ["CooldownMax"] = 3000 });
        frozen1.FreezeMs = 5000;
        frozen2.FreezeMs = 5000;
        var driver = CreateItem("drv");
        p1.Hand.TryAddAuto(frozen1);
        p1.Hand.TryAddAuto(frozen2);
        p1.Hand.TryAddAuto(driver);

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(1)) { SelfCard = driver };
        ctx.ActiveExecution.ActionType = "TActionCardForceUse";
        var randTarget = ParseNode("""{"$type":"TTargetCardRandom","TargetSection":"SelfHand","ExcludeSelf":true}""");
        var picked = TargetResolver.ResolveCards(randTarget, ctx, maxCount: 1);
        Assert.Single(picked);
        Assert.True(ReferenceEquals(picked[0], frozen1) || ReferenceEquals(picked[0], frozen2));
    }

    [Fact]
    public void P1_08_MultiplyZero_DoesNotBakeTransientAura_AndMarksAttributeDirty()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var card = CreateItem("m0_card", attrs: new() { ["DamageAmount"] = 20 });
        p1.Hand.TryAddAuto(card);
        card.ApplyAuraAttribute("DamageAmount", "Add", 30);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(1)) { SelfCard = card };
        var mulZero = ParseNode("""
        {
          "$type": "TActionCardModifyAttribute",
          "AttributeType": "DamageAmount",
          "Operation": "Multiply",
          "Value": { "$type": "TFixedValue", "Value": 0 },
          "Target": { "$type": "TTargetCardSelf" }
        }
        """);
        AbilityExecutor.ExecuteAction(mulZero, card, ctx);

        card.ClearAuraModifiers();
        Assert.Equal(0, card.GetAttribute("DamageAmount"));
        Assert.Equal(-20, card.PermanentAdd.GetValueOrDefault("DamageAmount", 0));

        // 战中 Multiply 0 必须递增 AttributeVersion，使 Timeline 快照立刻反映归零而非命中过期缓存
        var cp1 = CreatePlayer("cp1", HeroNames.Vanessa, 2000);
        var cp2 = CreatePlayer("cp2", HeroNames.Vanessa, 2000);
        var victim = CreateItem(
            "m0_snap_victim",
            attrs: new() { ["DamageAmount"] = 45, ["CooldownMax"] = 5000 });
        var zeroer = CreateItem(
            "m0_zeroer",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                      "$type": "TActionCardModifyAttribute",
                      "AttributeType": "DamageAmount",
                      "Operation": "Multiply",
                      "Value": { "$type": "TFixedValue", "Value": 0 },
                      "Duration": { "$type": "TDeterminantDuration", "DurationType": "UntilEndOfCombat" },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
                    }
                    """)
                }
            });
        cp1.Hand.TryAddAuto(victim);
        cp1.Hand.TryAddAuto(zeroer);
        var snapRes = CombatSimulator.Simulate(repo, cp1, cp2, seed: 808, combatType: "PVP", recordTimeline: true, recordEvents: true);
        var snapBefore = snapRes.Timeline.First(t => t.TimeMs == 0).Cards.First(c => c.InstanceId == victim.InstanceId);
        var snapAfter = snapRes.Timeline.First(t => t.TimeMs == 1000).Cards.First(c => c.InstanceId == victim.InstanceId);
        Assert.Equal(45, snapBefore.DamageAmount);
        Assert.Equal(0, snapAfter.DamageAmount);

        // Set 在光环生效时亦不烘焙 AuraAdd / AuraMult
        var setCard = CreateItem("set_aura_card", attrs: new() { ["DamageAmount"] = 20 });
        setCard.ApplyAuraAttribute("DamageAmount", "Add", 30);
        setCard.ApplyAuraAttribute("DamageAmount", "Multiply", 2.0);
        setCard.ModifyAttribute("DamageAmount", "Set", 50, DurationTarget.Permanent);
        setCard.ClearAuraModifiers();
        Assert.Equal(50, setCard.GetAttribute("DamageAmount"));

        // 跨项防线（P0-02 × D1-P1-08 × D1-P1-17）：对原本无 DamageAmount 的真实 Spices 执行 ResetAttributeToZero 与非限时 Subtract，
        // 绝不得向 *Add 写入 0 幽灵键污染 HasAttribute("DamageAmount")
        var spices = new CardInstance(repo.GetCard(SpicesId)!, CardTier.Gold, null, "spices_ghost_guard");
        Assert.False(spices.HasAttribute("DamageAmount"));
        spices.ResetAttributeToZero("DamageAmount", DurationTarget.Combat);
        spices.ResetAttributeToZero("DamageAmount", DurationTarget.Permanent);
        spices.ModifyAttribute("DamageAmount", "Subtract", 20, DurationTarget.Combat);
        spices.ModifyAttribute("DamageAmount", "Subtract", 20, DurationTarget.Permanent);
        Assert.False(spices.HasAttribute("DamageAmount"));
        Assert.False(spices.HasDeclaredAttributeKey("DamageAmount"));
    }
}
