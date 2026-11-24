using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarServer.Data;
using BazaarServer.Game;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4A（#2 RuleNode 字段兼容与引用语义 / #5 首帧虚假差分全字段根治 / #6 货架附魔衍生属性权威同步）强断言单元测试。
/// </summary>
[Xunit.Collection("ShopEngineStaticState")]
public sealed class Wave4A_RuleEvalCombatAndAttrsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static CardInstance MakeCard(
        string id,
        string name,
        Dictionary<string, double> baseAttrs,
        string? enchantment = null)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = new Dictionary<string, double>(baseAttrs, StringComparer.OrdinalIgnoreCase),
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment, id + "_inst");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #2：RuleNode 全量字段别名兼容（Attribute vs AttributeType / Comparison vs ComparisonOperator）
    //     + TReferenceValue* 在 target == null 时的官方语义对齐
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task2_PlayerAndCardConditionals_AcceptBothAttributeAndAttributeType_AndComparisonAliases()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, initialGold: 25, unlockedHandSlots: 10);
        p1.Burn = 0; // 验证不会静默回退到 Burn！
        var p2 = new PlayerState("opponent", "P2", HeroNames.Pygmalien, initialHealthMax: 900, initialGold: 5, unlockedHandSlots: 10);
        var card = MakeCard("w4a_c1", "GoldSword", new() { ["DamageAmount"] = 40, ["CooldownMax"] = 3000 });
        p1.Hand.TryPlaceOrShift(card, 0);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, p2)
        {
            SelfCard = card,
            CurrentDay = 3,
            CurrentHour = 5,
        };

        // 1. TPlayerConditionalAttribute：同时兼容官方 Attribute 与常见误写 AttributeType / PlayerAttributeType 以及 Comparison
        var condStandard = ParseNode("""
            {"$type":"TPlayerConditionalAttribute","Attribute":"Gold","ComparisonOperator":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":20}}
            """);
        var condAliasAttrType = ParseNode("""
            {"$type":"TPlayerConditionalAttribute","AttributeType":"Gold","Comparison":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":20}}
            """);
        var condAliasFail = ParseNode("""
            {"$type":"TPlayerConditionalAttribute","AttributeType":"Gold","Comparison":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":30}}
            """);
        Assert.True(ValueAndConditionEvaluator.EvaluatePlayerCondition(condStandard, p1, ctx));
        Assert.True(ValueAndConditionEvaluator.EvaluatePlayerCondition(condAliasAttrType, p1, ctx));
        Assert.False(ValueAndConditionEvaluator.EvaluatePlayerCondition(condAliasFail, p1, ctx));

        // 2. TPlayerConditionalAttributeHighest / Lowest：同时兼容 AttributeType 与 Attribute
        var condPlayerHighestAlias = ParseNode("""{"$type":"TPlayerConditionalAttributeHighest","Attribute":"Gold"}""");
        var condPlayerLowestAlias = ParseNode("""{"$type":"TPlayerConditionalAttributeLowest","Attribute":"Gold"}""");
        Assert.True(ValueAndConditionEvaluator.EvaluatePlayerCondition(condPlayerHighestAlias, p1, ctx));
        Assert.False(ValueAndConditionEvaluator.EvaluatePlayerCondition(condPlayerHighestAlias, p2, ctx));
        Assert.True(ValueAndConditionEvaluator.EvaluatePlayerCondition(condPlayerLowestAlias, p2, ctx));

        // 3. TCardConditionalAttribute：同时兼容 Attribute 与 AttributeType / CardAttributeType 以及 Comparison
        var cardCondAlias = ParseNode("""
            {"$type":"TCardConditionalAttribute","AttributeType":"DamageAmount","Comparison":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":40}}
            """);
        var cardCondAliasFail = ParseNode("""
            {"$type":"TCardConditionalAttribute","CardAttributeType":"DamageAmount","Comparison":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":50}}
            """);
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(cardCondAlias, card, p1, ctx));
        Assert.False(ValueAndConditionEvaluator.EvaluateCardCondition(cardCondAliasFail, card, p1, ctx));

        // 4. TCardConditionalAttributeHighest / Lowest：兼容 Attribute 别名
        var card2 = MakeCard("w4a_c2", "LightDagger", new() { ["DamageAmount"] = 10 });
        p1.Hand.TryPlaceOrShift(card2, 1);
        var cardHighestAlias = ParseNode("""{"$type":"TCardConditionalAttributeHighest","Attribute":"DamageAmount"}""");
        var cardLowestAlias = ParseNode("""{"$type":"TCardConditionalAttributeLowest","Attribute":"DamageAmount"}""");
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(cardHighestAlias, card, p1, ctx));
        Assert.False(ValueAndConditionEvaluator.EvaluateCardCondition(cardHighestAlias, card2, p1, ctx));
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(cardLowestAlias, card2, p1, ctx));

        // 5. TRunConditionalCurrentDay / CurrentHour & TPrerequisiteCardCount：兼容 Comparison 别名
        var dayCond = ParseNode("""{"$type":"TRunConditionalCurrentDay","Comparison":"GreaterThanOrEqual","CurrentDay":3}""");
        var hourCond = ParseNode("""{"$type":"TRunConditionalCurrentHour","Comparison":"Equal","CurrentHour":5}""");
        var prereqCount = ParseNode("""
            {"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"ComparisonOperator":"GreaterThanOrEqual","Amount":2}
            """);
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(dayCond, ctx));
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(hourCond, ctx));
        Assert.True(ValueAndConditionEvaluator.EvaluatePrerequisite(prereqCount, ctx));
    }

    [Fact]
    public void Task2_ReferenceValue_TargetNull_AlignsWithOfficialSemantics_And_PreservesAuraCurrentTargetCard()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, initialGold: 42, unlockedHandSlots: 10);
        var sourceCard = MakeCard("w4a_src", "SourceCard", new() { ["DamageAmount"] = 99 });
        var targetCard = MakeCard("w4a_tgt", "TargetCard", new() { ["DamageAmount"] = 15 });
        p1.Hand.TryPlaceOrShift(sourceCard, 0);
        p1.Hand.TryPlaceOrShift(targetCard, 1);

        var ctxNoCurrentTarget = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1)
        {
            SelfCard = sourceCard,
            CurrentTargetCard = null,
        };

        // 1. target == null 且无 CurrentTargetCard：严禁偷偷回退到 SourceCard (99)，必须回退 DefaultValue (7) 并套用 Modifier (+3 -> 10)
        var cardRefNullTarget = ParseNode("""
            {
                "$type": "TReferenceValueCardAttribute",
                "Attribute": "DamageAmount",
                "DefaultValue": 7,
                "Modifier": { "ModifyMode": "Add", "Value": { "$type": "TFixedValue", "Value": 3 } }
            }
            """);
        Assert.Equal(10, ValueAndConditionEvaluator.EvaluateValue(cardRefNullTarget, ctxNoCurrentTarget));

        // 2. target == null 但处于 Aura 逐目标上下文（CurrentTargetCard != null）：读取 CurrentTargetCard (15) + 3 = 18
        var ctxAuraTarget = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1)
        {
            SelfCard = sourceCard,
            CurrentTargetCard = targetCard,
        };
        Assert.Equal(18, ValueAndConditionEvaluator.EvaluateValue(cardRefNullTarget, ctxAuraTarget));

        // 3. 显式 Target = TTargetCardSelf + 别名字段 Attribute：读取 SourceCard (99)
        var cardRefSelf = ParseNode("""
            {"$type":"TReferenceValueCardAttribute","Attribute":"DamageAmount","Target":{"$type":"TTargetCardSelf"}}
            """);
        Assert.Equal(99, ValueAndConditionEvaluator.EvaluateValue(cardRefSelf, ctxNoCurrentTarget));

        // 4. TReferenceValuePlayerAttribute：target == null 严格返回 DefaultValue + Modifier，显式 Target 读 Gold (42)（且兼容 Attribute 别名）
        var playerRefNull = ParseNode("""
            {"$type":"TReferenceValuePlayerAttribute","Attribute":"Gold","DefaultValue":5}
            """);
        var playerRefSelf = ParseNode("""
            {"$type":"TReferenceValuePlayerAttribute","Attribute":"Gold","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
            """);
        Assert.Equal(5, ValueAndConditionEvaluator.EvaluateValue(playerRefNull, ctxNoCurrentTarget));
        Assert.Equal(42, ValueAndConditionEvaluator.EvaluateValue(playerRefSelf, ctxNoCurrentTarget));
    }

    [Fact]
    public void Task2_ActionAndAuraModifyAttribute_AcceptAttributeAlias()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, initialGold: 10, unlockedHandSlots: 10);
        var card = MakeCard("w4a_act", "AliasModCard", new() { ["DamageAmount"] = 10 });
        p1.Hand.TryPlaceOrShift(card, 0);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1)
        {
            SelfCard = card,
        };

        var addGoldAction = ParseNode("""
            {
                "$type": "TActionPlayerModifyAttribute",
                "Attribute": "Gold",
                "Operation": "Add",
                "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
                "Value": { "$type": "TFixedValue", "Value": 15 }
            }
            """);
        var addDmgAction = ParseNode("""
            {
                "$type": "TActionCardModifyAttribute",
                "Attribute": "DamageAmount",
                "Operation": "Add",
                "Target": { "$type": "TTargetCardSelf" },
                "Value": { "$type": "TFixedValue", "Value": 8 }
            }
            """);

        Assert.True(AbilityExecutor.ExecuteAction(addGoldAction, card, ctx));
        Assert.True(AbilityExecutor.ExecuteAction(addDmgAction, card, ctx));
        Assert.Equal(25, p1.Gold);
        Assert.Equal(18, card.GetAttribute("DamageAmount"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #5：首帧虚假差分全字段根治（消灭 RageMax -50 / EnragedDurationMax / TempoGainCooldownMax / 先天 Regen）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task5_InitialPlayerBaseline_AlignsAllFieldsWithFirstSnap_EliminatesPhantomFrame0Diffs()
    {
        var firstSnap = new CombatTickState
        {
            TimeMs = 0,
            P1Hp = 800,
            P1MaxHp = 800,
            P1Shield = 0,
            P1Burn = 0,
            P1Poison = 0,
            P1Regen = 6,
            P1Rage = 0,
            P1RageMax = 100,
            P1Enraged = false,
            P1EnragedDuration = 0,
            P1EnragedDurationMax = 5000,
            P1Tempo = 0,
            P1TempoGainCooldownMax = 2000,
            P1Joy = 0,
            P1PortraitIndex = 0,
            P2Hp = 350,
            P2MaxHp = 350,
            P2Shield = 20,
            P2Burn = 0,
            P2Poison = 0,
            P2Regen = 12,
            P2Rage = 0,
            P2RageMax = 50, // 经典复现：PvE 野怪 RageMax=50，旧代码硬编码 100 导致首帧漂浮“最大怒气 -50”
            P2Enraged = false,
            P2EnragedDuration = 0,
            P2EnragedDurationMax = 5000,
            P2Tempo = 0,
            P2TempoGainCooldownMax = 0,
            P2Joy = 0,
            P2PortraitIndex = 0,
            SandstormActive = false,
            SandstormNextDamage = 1,
        };

        // 1. 无 TimeMs==0 瞬发事件：基线必须与 firstSnap 全字段严格一致
        var baselineNoEvents = CombatSimConverter.CreateInitialPlayerBaseline(firstSnap, events: Array.Empty<CombatEvent>());
        Assert.Equal(firstSnap.P1MaxHp, baselineNoEvents.P1MaxHp);
        Assert.Equal(firstSnap.P1RageMax, baselineNoEvents.P1RageMax);
        Assert.Equal(firstSnap.P1EnragedDurationMax, baselineNoEvents.P1EnragedDurationMax);
        Assert.Equal(firstSnap.P1TempoGainCooldownMax, baselineNoEvents.P1TempoGainCooldownMax);
        Assert.Equal(firstSnap.P1Regen, baselineNoEvents.P1Regen);
        Assert.Equal(firstSnap.P2RageMax, baselineNoEvents.P2RageMax);
        Assert.Equal(firstSnap.P2Regen, baselineNoEvents.P2Regen);
        Assert.Equal(firstSnap.P2Shield, baselineNoEvents.P2Shield);
        Assert.Equal(firstSnap.P2EnragedDurationMax, baselineNoEvents.P2EnragedDurationMax);

        // 2. 存在 TimeMs==0 开战瞬发事件（如 TTriggerOnFightStarted 施加护盾+30、再生+5、Tempo+3、Joy+15）：精确回推开战前基线
        var snapWithStartEffects = new CombatTickState
        {
            TimeMs = 0,
            P1Hp = 800,
            P1MaxHp = 800,
            P1Shield = 30,
            P1Regen = 11, // 先天 6 + 开战施加 5
            P1Rage = 25,
            P1RageMax = 100,
            P1EnragedDurationMax = 5000,
            P1Tempo = 3,
            P1TempoGainCooldownMax = 2000,
            P1Joy = 15,
            P2Hp = 350,
            P2MaxHp = 350,
            P2RageMax = 50,
            P2EnragedDurationMax = 5000,
        };
        var startEvents = new List<CombatEvent>
        {
            new() { TimeMs = 0, Type = CombatEventType.ShieldApplied, TargetPlayerId = "player", Value = 30 },
            new() { TimeMs = 0, Type = CombatEventType.RegenApplied, TargetPlayerId = "player", Value = 5 },
            new() { TimeMs = 0, Type = CombatEventType.PlayerRageApplied, TargetPlayerId = "player", Value = 25 },
            new() { TimeMs = 0, Type = CombatEventType.PlayerTempoApplied, TargetPlayerId = "player", Value = 3 },
            new() { TimeMs = 0, Type = CombatEventType.PlayerJoyApplied, TargetPlayerId = "player", Value = 15 },
        };
        var baselineWithEvents = CombatSimConverter.CreateInitialPlayerBaseline(snapWithStartEffects, startEvents);
        Assert.Equal(0, baselineWithEvents.P1Shield);
        Assert.Equal(6, baselineWithEvents.P1Regen);
        Assert.Equal(0, baselineWithEvents.P1Rage);
        Assert.Equal(0, baselineWithEvents.P1Tempo);
        Assert.Equal(0, baselineWithEvents.P1Joy);
        // 静态上限字段保持零虚假差分
        Assert.Equal(100, baselineWithEvents.P1RageMax);
        Assert.Equal(5000, baselineWithEvents.P1EnragedDurationMax);
        Assert.Equal(2000, baselineWithEvents.P1TempoGainCooldownMax);
        Assert.Equal(50, baselineWithEvents.P2RageMax);
    }

    [Fact]
    public void Task5_PveMonsterWithRageMax50AndInnateRegen_HasZeroPhantomDiffOnFrame0()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("player", "P1", HeroNames.Vanessa, initialHealthMax: 500, unlockedHandSlots: 10);
        var p2 = new PlayerState("opponent", "Rage50Monster", HeroNames.Common, initialHealthMax: 300, unlockedHandSlots: 10)
        {
            IsMonster = true,
        };
        p2.Attributes["RageMax"] = 50;
        p2.Attributes["HealthRegen"] = 8;

        var sword = MakeCard("w4a_pve_sword", "SimpleSword", new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 100 });
        sword.Template.Abilities["0"] = new AbilityDefinition
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
            Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}"""),
        };
        sword.RebuildTierAndEnchantmentData();
        p1.Hand.TryPlaceOrShift(sword, 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 40501, combatType: "PVE", recordTimeline: true, recordEvents: true);
        var p1Entry = new RunState.CardEntry
        {
            InstanceId = sword.InstanceId,
            TemplateId = Guid.NewGuid(),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = sword,
        };
        var sim = CombatSimConverter.ToCombatSim(
            res,
            repo,
            new Dictionary<string, CardInstance> { [p1Entry.InstanceId] = sword },
            new Dictionary<string, CardInstance>(),
            [p1Entry],
            Array.Empty<RunState.CardEntry>());

        Assert.NotEmpty(sim.Frames);
        var f0 = sim.Frames[0];
        Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.RageMax) == true,
            "PvE 首帧绝不允许下发 RageMax 虚假差分（根治“最大怒气 -50”漂字）");
        Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.HealthRegen) == true,
            "PvE 野怪先天 HealthRegen 首帧绝不允许下发虚假差分");
        Assert.False(f0.OpponentUpdates?.Attributes.ContainsKey(EPlayerAttributeType.EnragedDurationMax) == true,
            "PvE 首帧绝不允许下发 EnragedDurationMax 虚假差分");
        Assert.False(f0.PlayerUpdates?.Attributes.ContainsKey(EPlayerAttributeType.EnragedDurationMax) == true,
            "玩家首帧绝不允许下发 EnragedDurationMax 虚假差分");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #6：CardInstance.EnumeratePresentAttributeKeys() 覆盖全层 + 货架附魔衍生属性与手牌一致
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task6_EnumeratePresentAttributeKeys_CoversAllIsolatedAndLayeredDictionaries()
    {
        var card = MakeCard("w4a_layers", "LayerProbe", new() { ["DamageAmount"] = 10 });
        card.PermanentAdd["CritChance"] = 5;
        card.PermanentMult["Lifesteal"] = 2.0;
        card.PermanentAdditiveMult["AmmoMax"] = 0.5;
        card.HourAdd["HasteAmount"] = 1000;
        card.HourMult["SlowAmount"] = 1.5;
        card.HourAdditiveMult["FreezeAmount"] = 0.2;
        card.DayAdd["HealAmount"] = 15;
        card.DayMult["RegenApplyAmount"] = 2.0;
        card.DayAdditiveMult["Custom_0"] = 0.1;
        card.CombatAdd["Custom_1"] = 3;
        card.CombatMult["Custom_2"] = 1.2;
        card.CombatAdditiveMult["Custom_3"] = 0.3;
        card.AuraAdd["Custom_4"] = 4;
        card.AuraMult["Custom_5"] = 1.5;
        card.AuraAdditiveMult["Custom_6"] = 0.25;
        card.EnchantmentBaseAttributes["BurnApplyAmount"] = 6;
        card.EnchantmentAuraAdd["PoisonApplyAmount"] = 4;
        card.EnchantmentAuraMult["ShieldApplyAmount"] = 1.5;
        card.EnchantmentAuraAdditiveMult["JoyApplyAmount"] = 0.5;

        var keys = card.EnumeratePresentAttributeKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] expected =
        [
            "DamageAmount", "CritChance", "Lifesteal", "AmmoMax",
            "HasteAmount", "SlowAmount", "FreezeAmount",
            "HealAmount", "RegenApplyAmount", "Custom_0",
            "Custom_1", "Custom_2", "Custom_3",
            "Custom_4", "Custom_5", "Custom_6",
            "BurnApplyAmount", "PoisonApplyAmount", "ShieldApplyAmount", "JoyApplyAmount",
        ];
        foreach (string k in expected)
            Assert.Contains(k, keys);
    }

    [Fact]
    public void Task6_ShopShelfEnchantedItems_DisplaySameDerivedAttributesAsInHand()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;
        var run = new RunState
        {
            RunId = "w4a_ench_shelf",
            GameModeId = pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 4,
            Hour = 2,
            State = ERunState.Encounter,
            UnlockedSlots = 0b11_1111_1111,
        };
        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 100;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 1000;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 1000;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 5;

        var shop = new ShopEngine(run, pool, repo, run.CustomRules);

        // 从真实 GameData.db 挑选带附魔衍生属性（Fiery/Toxic/Shielded/Restorative/Obsidian）且通过附魔光环或附魔属性产生数值的真实物品
        var candidates = new List<(CardTemplate Template, string EnchName, string DerivedAttr)>();
        var targetPairs = new (string Ench, string Attr)[]
        {
            ("Fiery", "BurnApplyAmount"),
            ("Toxic", "PoisonApplyAmount"),
            ("Shielded", "ShieldApplyAmount"),
            ("Restorative", "HealAmount"),
            ("Obsidian", "DamageAmount"),
        };

        foreach (var (enchName, attrName) in targetPairs)
        {
            var match = repo.CardsById.Values.FirstOrDefault(tpl =>
                tpl.Type == "Item"
                && (tpl.Attributes == null || !tpl.Attributes.ContainsKey(attrName))
                && tpl.Enchantments != null
                && tpl.Enchantments.TryGetValue(enchName, out var enchDef)
                && enchDef != null
                && ((enchDef.Attributes != null && enchDef.Attributes.ContainsKey(attrName))
                    || (enchDef.Auras != null && enchDef.Auras.Values.Any(a => string.Equals(a.Action?.AttributeType, attrName, StringComparison.OrdinalIgnoreCase)))));
            if (match != null)
                candidates.Add((match, enchName, attrName));
        }

        Assert.NotEmpty(candidates);

        var syncMethod = typeof(ShopEngine).GetMethod(
            "ApplyLocalShelfAuraAndSync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(syncMethod);

        foreach (var (tpl, enchName, attrName) in candidates)
        {
            var tier = SpawnQueryEngine.ClampToSupportedTier(tpl, CardTier.Silver);
            var shelfCore = new CardInstance(tpl, tier, enchName, "shelf_" + tpl.Id[..6]);
            var shelfEntry = new RunState.CardEntry
            {
                InstanceId = shelfCore.InstanceId,
                TemplateId = Guid.Parse(tpl.Id),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(tpl.Size),
                Tier = ShopEngine.ParseTier(tier.ToString()),
                Enchantment = Enum.Parse<EEnchantmentType>(enchName, ignoreCase: true),
                Owner = ECombatantId.Player,
                CoreCard = shelfCore,
            };
            RunState.PopulateMetaFromTemplate(shelfEntry, pool.Get(tpl.Id), tpl);
            syncMethod!.Invoke(shop, [shelfEntry, shelfCore, false]);

            // 对照买进手牌后在真实 PlayerState 跑全量 AuraEngine + SyncCardAttributesFromCore 的属性值
            var handCore = new CardInstance(tpl, tier, enchName, "hand_" + tpl.Id[..6]);
            var handPlayer = new PlayerState("player", "P1", run.Hero.ToString(), 1000, 20, 100, 10)
            {
                Day = (int)run.Day,
                Hour = (int)run.Hour,
                Level = 5,
            };
            handPlayer.Hand.TryAddAuto(handCore);
            AuraEngine.RecalculateAuras(handPlayer, null, RuleScope.OutOfCombat, (int)run.Day, (int)run.Hour, repo);

            var handEntry = new RunState.CardEntry
            {
                InstanceId = handCore.InstanceId,
                TemplateId = Guid.Parse(tpl.Id),
                Type = ECardType.Item,
                Size = ShopEngine.ParseSize(tpl.Size),
                Tier = ShopEngine.ParseTier(tier.ToString()),
                Enchantment = Enum.Parse<EEnchantmentType>(enchName, ignoreCase: true),
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_0,
                CoreCard = handCore,
            };
            run.SyncEntryFromCoreCardAndDiff(handEntry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

            var attrEnum = Enum.Parse<ECardAttributeType>(attrName, ignoreCase: true);
            int expectedHandVal = handEntry.Attributes.GetValueOrDefault(attrEnum, 0);
            int actualShelfVal = shelfEntry.Attributes.GetValueOrDefault(attrEnum, 0);

            Assert.True(expectedHandVal > 0, $"参考卡 {tpl.InternalName}+{enchName} 进手牌后 {attrName} 应 > 0");
            Assert.Equal(expectedHandVal, actualShelfVal);
        }
    }
}
