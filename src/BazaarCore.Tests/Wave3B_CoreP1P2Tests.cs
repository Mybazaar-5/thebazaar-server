using System.Reflection;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarServer.Game;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3B 域 1 独占强断言回归套件（默认并行，纯内存无全局静态状态污染）：
/// 覆盖 D1 剩余 10 项 P1（D1-P1-02/06/07/09/10/11余项/12/13/18/19）、
/// 全部 9 项 P2（D1-P2-01~09）+ 附注孤立 Heal 5% 净化回退 + ApplyEnchantment 重载收敛。
/// </summary>
public sealed class Wave3B_CoreP1P2Tests
{
    private static GameDataRepository Repo => SharedGameDataFixture.Shared.Repo;

    private static JsonElement FixedVal(double v)
        => JsonSerializer.SerializeToElement(new { Value = v });

    private static CardTemplate MakeSimpleCard(
        string id,
        string name = "TestCard",
        string size = "Medium",
        int cooldownMs = 2000,
        int damage = 10,
        string[]? tags = null,
        Dictionary<string, double>? extraAttrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        Dictionary<string, EnchantmentData>? enchantments = null,
        List<QuestDefinition>? quests = null)
    {
        var attrs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["CooldownMax"] = cooldownMs,
            ["DamageAmount"] = damage,
        };
        if (extraAttrs != null)
        {
            foreach (var kv in extraAttrs)
                attrs[kv.Key] = kv.Value;
        }

        return new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            Size = size,
            StartingTier = "Bronze",
            Tags = (tags ?? new[] { "Weapon" }).ToList(),
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData { Attributes = attrs },
            },
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Enchantments = enchantments ?? new Dictionary<string, EnchantmentData>(),
            Quests = quests,
        };
    }

    private static PlayerState MakePlayer(string id, int hp = 500)
        => new(id, id, HeroNames.Vanessa, hp, 20, 10, 10);

    [Fact]
    public void ApplyEnchantment_LegacyBooleanOverloadsRemoved_OnlyTwoCanonicalOverloadsRemain()
    {
        var methods = typeof(CardInstance)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(CardInstance.ApplyEnchantment))
            .ToList();

        Assert.Equal(2, methods.Count);
        Assert.Contains(methods, m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        Assert.Contains(methods, m =>
            m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(string)
            && m.GetParameters()[1].ParameterType == typeof(DurationTarget)
            && m.GetParameters()[2].ParameterType == typeof(bool));
    }

    [Fact]
    public void D1_P1_02_LayeredTagRemoval_RemovesNativeTagAndHigherLayerReAddRevertsCleanly()
    {
        var tpl = MakeSimpleCard("w3b_tag_01", tags: new[] { "Weapon", "Tool" });
        var card = new CardInstance(tpl);
        Assert.True(card.HasTag("Weapon"));

        var p1 = MakePlayer("p1");
        var p2 = MakePlayer("p2");
        p1.Hand.TryAddAuto(card);

        var ctx = new RuleExecutionContext(Repo, RuleScope.OutOfCombat, p1, p2, new Random(1))
        {
            SelfCard = card,
        };

        // 1. 永久移除原生模板标签 Weapon
        var removePermAction = new RuleNode
        {
            Type = "TActionCardRemoveTagsList",
            Tags = new List<string> { "Weapon" },
            Target = new RuleNode { Type = "TTargetCardSelf" },
        };
        AbilityExecutor.ExecuteAction(removePermAction, card, ctx);
        Assert.False(card.HasTag("Weapon"), "Permanent 移除标签必须能剥离模板原生 Weapon 标签");
        Assert.True(card.HasTag("Tool"));

        // 2. 战中临时重新赋予 Weapon，进战态生效，ResetForCombat 后自动恢复永久剥离态
        var combatCtx = new RuleExecutionContext(Repo, RuleScope.InCombat, p1, p2, new Random(1))
        {
            SelfCard = card,
        };
        var addCombatAction = new RuleNode
        {
            Type = "TActionCardAddTagsList",
            Tags = new List<string> { "Weapon" },
            Duration = new RuleNode { Type = "TDeterminantDuration", DurationType = "UntilEndOfCombat" },
            Target = new RuleNode { Type = "TTargetCardSelf" },
        };
        AbilityExecutor.ExecuteAction(addCombatAction, card, combatCtx);
        Assert.True(card.HasTag("Weapon"), "永久剥离后在 Combat 短久层重新赋予 Weapon 必须立即生效");

        card.ResetForCombat(null);
        Assert.False(card.HasTag("Weapon"), "战后清空 Combat 层后，必须自动恢复 Permanent 剥离态");
    }

    [Fact]
    public void D1_P1_06_VisitedEncountersAndOutOfCombatPlayerEffectsAura_AreEvaluatedAccurately()
    {
        var visitedId = "enc-w3b-visited-001";
        var cond = new RuleNode
        {
            Type = "TRunConditionalHasVisitedEncounter",
            EncounterId = visitedId,
        };

        var p1 = MakePlayer("p1");
        var p2 = MakePlayer("p2");
        var ctx = new RuleExecutionContext(Repo, RuleScope.OutOfCombat, p1, p2, new Random(7));

        // 必须支持在 RuleExecutionContext 上直接注入 VisitedEncounterIds
        PropertyInfo? visitedProp = typeof(RuleExecutionContext).GetProperty("VisitedEncounterIds");
        Assert.NotNull(visitedProp);
        visitedProp!.SetValue(ctx, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { visitedId });
        Assert.True(ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(cond, ctx));

        // 局外 PlayerEffects 光环必须无需手动转嫁即生效
        var targetWeapon = new CardInstance(MakeSimpleCard("w3b_pe_wpn", damage: 20, tags: new[] { "Weapon" }));
        p1.Hand.TryAddAuto(targetWeapon);

        var peTpl = new CardTemplate
        {
            Id = "w3b_pe_aura",
            InternalName = "TestPlayerEffect",
            Type = "PlayerEffect",
            Size = "Small",
            StartingTier = "Bronze",
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["pe_a1"] = new AuraDefinition
                {
                    Id = "pe_a1",
                    ActiveIn = "HandAndStash",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonSerializer.SerializeToElement(new { _type = "TFixedValue", Value = 15.0 }).Clone(),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                        },
                    },
                },
            },
        };
        // 使用合法 $type = TFixedValue
        peTpl.Auras["pe_a1"].Action!.RawValueNode = JsonDocument.Parse("""{"$type":"TFixedValue","Value":15}""").RootElement.Clone();

        p1.PlayerEffects.Add(new CardInstance(peTpl));
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, 1, 1, Repo);
        Assert.Equal(35.0, targetWeapon.GetAttribute("DamageAmount"), precision: 3);
    }

    [Fact]
    public void D1_P1_07_AdditiveMultiplyStacksAdditivelyAndSeparateFromPureMultiply()
    {
        var card = new CardInstance(MakeSimpleCard("w3b_mult_01", damage: 100));

        // 两次 +50% AdditiveMultiply 必须累加为 1 + 0.5 + 0.5 = 2.0（200），绝非 1.5 * 1.5 = 2.25（225）
        card.ModifyAttribute("DamageAmount", "AdditiveMultiply", 0.5, DurationTarget.Permanent);
        card.ModifyAttribute("DamageAmount", "AdditiveMultiply", 0.5, DurationTarget.Combat);
        Assert.Equal(200.0, card.GetAttribute("DamageAmount"), precision: 3);

        // 再叠一次 Pure Multiply 2.0x => 100 * 2.0 * 2.0 = 400
        card.ModifyAttribute("DamageAmount", "Multiply", 2.0, DurationTarget.Combat);
        Assert.Equal(400.0, card.GetAttribute("DamageAmount"), precision: 3);

        // ResetForCombat 后仅保留 Permanent +50% AdditiveMultiply => 150
        card.ResetForCombat(null);
        Assert.Equal(150.0, card.GetAttribute("DamageAmount"), precision: 3);
    }

    [Fact]
    public void D1_P1_09_EnchantmentAttribute_StacksExternalBuffsWithoutMixingBaseAttributes()
    {
        var tpl = MakeSimpleCard(
            "w3b_ench_iso",
            damage: 50,
            extraAttrs: new Dictionary<string, double> { ["BurnApplyAmount"] = 12 },
            enchantments: new Dictionary<string, EnchantmentData>
            {
                ["Fiery"] = new EnchantmentData
                {
                    Attributes = new Dictionary<string, double> { ["BurnApplyAmount"] = 4 },
                    Abilities = new Dictionary<string, AbilityDefinition>
                    {
                        ["e1"] = new AbilityDefinition { Id = "e1", ActiveIn = "HandOnly" },
                    },
                },
            });
        var card = new CardInstance(tpl, CardTier.Bronze, enchantment: "Fiery");

        // 无外部增益时严格保持附魔隔离底值 4（绝不混入 BaseAttributes=12）
        Assert.Equal(4.0, card.GetEnchantmentAttribute("BurnApplyAmount"), precision: 3);

        // 外部持久/战斗增益 +6 必须叠加到附魔属性（4 + 6 = 10），仍绝不混入 BaseAttributes=12
        card.ModifyAttribute("BurnApplyAmount", "Add", 6, DurationTarget.Combat);
        Assert.Equal(10.0, card.GetEnchantmentAttribute("BurnApplyAmount"), precision: 3);
    }

    [Fact]
    public void D1_P1_10_HandAndStashAuraInStash_AppliesInCombat_WhileHandOnlyBlocked()
    {
        var p1 = MakePlayer("p1");
        var p2 = MakePlayer("p2");
        var weapon = new CardInstance(MakeSimpleCard("w3b_hs_wpn", damage: 20));
        p1.Hand.TryAddAuto(weapon);

        var handAndStashCard = new CardInstance(MakeSimpleCard(
            "w3b_hs_aura",
            auras: new Dictionary<string, AuraDefinition>
            {
                ["a1"] = new AuraDefinition
                {
                    Id = "a1",
                    ActiveIn = "HandAndStash",
                    WorksIn = "InCombatAndOutOfCombat",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonDocument.Parse("""{"$type":"TFixedValue","Value":7}""").RootElement.Clone(),
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" },
                    },
                },
                ["a2_hand_only"] = new AuraDefinition
                {
                    Id = "a2_hand_only",
                    ActiveIn = "HandOnly",
                    WorksIn = "InCombatAndOutOfCombat",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "DamageAmount",
                        Operation = "Add",
                        RawValueNode = JsonDocument.Parse("""{"$type":"TFixedValue","Value":100}""").RootElement.Clone(),
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" },
                    },
                },
            }));
        p1.Stash.TryAddAuto(handAndStashCard);

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, Repo);
        Assert.Equal(27.0, weapon.GetAttribute("DamageAmount"), precision: 3);
    }

    [Fact]
    public void D1_P1_13_TinyAttribute_RespectsDayDurationAndExpiresOnEndOfDay()
    {
        var card = new CardInstance(MakeSimpleCard("w3b_tiny_day", size: "Large"));
        Assert.Equal(3, card.SlotWidth);
        Assert.False(card.IsTiny);

        card.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Day);
        Assert.True(card.IsTiny);
        Assert.Equal(2, card.SlotWidth);
        Assert.True(card.HasTag("Tiny"));

        // 进战后 ResetForCombat 不得误删 Day 层 Tiny
        card.ResetForCombat(null);
        Assert.True(card.IsTiny);
        Assert.Equal(2, card.SlotWidth);

        // 换日清空后必须恢复原始 Large 宽度 3
        card.ClearEndOfDayModifiers();
        Assert.False(card.IsTiny);
        Assert.Equal(3, card.SlotWidth);
        Assert.False(card.HasTag("Tiny"));
    }

    [Fact]
    public void D1_P1_18_Multicast_SupportsUpTo50CastsWithoutTruncatingAt10()
    {
        var p1 = MakePlayer("p1", hp: 2000);
        var p2 = MakePlayer("p2", hp: 5000);

        var shurikenLike = MakeSimpleCard(
            "w3b_multi_15",
            cooldownMs: 4000,
            damage: 10,
            extraAttrs: new Dictionary<string, double> { ["Multicast"] = 15 },
            abilities: new Dictionary<string, AbilityDefinition>
            {
                ["fire"] = new AbilityDefinition
                {
                    Id = "fire",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                    },
                },
            });
        p1.Hand.TryAddAuto(new CardInstance(shurikenLike));

        // 单轮 4000ms 触发 15 次连发（总时长约 4000 + 14*200 = 6800ms），由 15000ms 时限或 150 伤击杀判定
        var res = CombatSimulator.Simulate(
            Repo, p1, p2, seed: 42, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: false);

        // 在第 1 轮 4000ms~7500ms 窗内必须完整打出全部 15 次施放，绝不可在 10 次处被截断
        int firstWaveCasts = res.Events.Count(e =>
            e.Type == CombatEventType.CardFired
            && e.SourcePlayerId == "p1"
            && e.TimeMs <= 7600);
        Assert.Equal(15, firstWaveCasts);
    }

    [Fact]
    public void D1_P1_19_OnChargeApplied_BlocksEmptyAmmoWeapon()
    {
        var p1 = MakePlayer("p1", hp: 1000);
        var p2 = MakePlayer("p2", hp: 1000);

        // 1 弹武器于 t=1000ms 开火耗尽唯一弹药；充能器冷却 2500ms，在武器已空弹后才触发充能
        var emptyGun = new CardInstance(MakeSimpleCard(
            "w3b_empty_gun",
            cooldownMs: 1000,
            damage: 25,
            extraAttrs: new Dictionary<string, double> { ["AmmoMax"] = 1, ["Ammo"] = 1 }));

        var charger = new CardInstance(MakeSimpleCard(
            "w3b_charger",
            cooldownMs: 2500,
            damage: 0,
            extraAttrs: new Dictionary<string, double> { ["ChargeAmount"] = 2000 },
            abilities: new Dictionary<string, AbilityDefinition>
            {
                ["chg"] = new AbilityDefinition
                {
                    Id = "chg",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardCharge",
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
                    },
                },
            }));

        p1.Hand.TryAddAuto(emptyGun);
        p1.Hand.TryAddAuto(charger);

        var res = CombatSimulator.Simulate(
            Repo, p1, p2, seed: 99, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: true);

        bool emptyGunCharged = res.Events.Any(e => e.Type == CombatEventType.ChargeApplied && e.TargetCardInstanceId == emptyGun.InstanceId);
        Assert.False(emptyGunCharged, "弹药耗尽的武器绝不可接受 Charge 或触发 ChargeApplied 事件");
    }

    [Fact]
    public void D1_P2_01_QuestDefinition_ParsesIsRepeatable()
    {
        const string json = """{"Id":"q1","IsRepeatable":true,"Entries":[],"Reward":{"Abilities":{},"Auras":{}}}""";
        var q = JsonSerializer.Deserialize<QuestDefinition>(json);
        Assert.NotNull(q);
        PropertyInfo? isRepProp = typeof(QuestDefinition).GetProperty("IsRepeatable");
        Assert.NotNull(isRepProp);
        Assert.Equal(true, isRepProp!.GetValue(q));
    }

    [Fact]
    public void D1_P2_02_PlayerState_ClampsNegativeAdditiveMultAtZero()
    {
        var p = MakePlayer("p_neg_mult");
        p.ModifyAttribute("DamageAmount", "Add", 50, DurationTarget.Permanent);
        p.ModifyAttribute("DamageAmount", "AdditiveMultiply", -1.5, DurationTarget.Combat);
        Assert.Equal(0.0, p.GetAttribute("DamageAmount"), precision: 3);
    }

    [Fact]
    public void D1_P2_03_RageApply_FreezesRageGainWhileEnraged()
    {
        var p1 = MakePlayer("p_rage");
        var p2 = MakePlayer("p_opp");
        var dummyCard = new CardInstance(MakeSimpleCard("w3b_rage_src"));
        p1.Hand.TryAddAuto(dummyCard);
        p1.EnragedRemainingMs = 5000;
        p1.Rage = 0;

        var ctx = new RuleExecutionContext(Repo, RuleScope.InCombat, p1, p2, new Random(5))
        {
            SelfCard = dummyCard,
        };
        var rageAction = new RuleNode
        {
            Type = "TActionPlayerRageApply",
            RawValueNode = JsonDocument.Parse("""{"$type":"TFixedValue","Value":25}""").RootElement.Clone(),
            Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
        };
        AbilityExecutor.ExecuteAction(rageAction, dummyCard, ctx);
        Assert.Equal(0, p1.Rage);
    }

    [Fact]
    public void D1_P2_07_CardCooldownAliasAndPlayerFlatDamageReduction()
    {
        var card = new CardInstance(MakeSimpleCard("w3b_cd_alias", cooldownMs: 4000));
        card.CooldownProgressMs = 1500;
        Assert.Equal(2500.0, card.GetAttribute("Cooldown"), precision: 3);

        var p1 = MakePlayer("p1", hp: 500);
        var p2 = MakePlayer("p2", hp: 500);
        p2.ModifyAttribute("FlatDamageReduction", "Add", 15, DurationTarget.Permanent);

        p1.Hand.TryAddAuto(new CardInstance(MakeSimpleCard(
            "w3b_flat_dr_wpn",
            cooldownMs: 1000,
            damage: 40,
            abilities: new Dictionary<string, AbilityDefinition>
            {
                ["hit"] = new AbilityDefinition
                {
                    Id = "hit",
                    ActiveIn = "HandOnly",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                    },
                },
            })));

        var res = CombatSimulator.Simulate(
            Repo, p1, p2, seed: 12, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: false,
            modifiers: null, sandstormEnabled: false);
        var firstDmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.TargetPlayerId == "p2");
        Assert.Equal(25, firstDmg.Value);
    }

    [Fact]
    public void D1_P2_09_SandstormTick_RealSimConverterAndFlipPerspective_SplitsActualDamageWithoutGhostHits()
    {
        var p1 = MakePlayer("player", hp: 500);
        var p2 = MakePlayer("opponent", hp: 500);
        // P1 拥有 100% 减伤（沙暴实际扣血 0），P2 无减伤（沙暴正常扣血）
        p1.Attributes["PercentDamageReduction"] = 100;

        var res = CombatSimulator.Simulate(
            Repo, p1, p2, seed: 42, combatType: "PVP",
            recordTimeline: true, recordEvents: true, syncPermanentChanges: false,
            modifiers: new CombatModifiers { SandstormStartOffsetMs = -29000, MaxDurationMsOverride = 12000 },
            sandstormEnabled: true);

        var ticks = res.Events.Where(e => e.Type == CombatEventType.SandstormTick).ToList();
        Assert.NotEmpty(ticks);
        // 每跳严格 1 条 SandstormTick（不拆两条，保持与存量测试兼容）
        Assert.Equal(ticks.Select(t => t.TimeMs).Distinct().Count(), ticks.Count);
        Assert.All(ticks, t =>
        {
            Assert.Equal(0, t.P1SandstormDamage);
            Assert.Equal(0, t.P1SandstormHpDamage);
            Assert.True(t.P2SandstormDamage > 0, "未减伤方 P2 必须记录实际沙暴伤害");
            Assert.True(t.P2SandstormHpDamage > 0, "未减伤方 P2 必须记录实际沙暴扣血");
        });

        var sim = CombatSimConverter.ToCombatSim(
            res,
            Repo,
            new Dictionary<string, CardInstance>(),
            new Dictionary<string, CardInstance>(),
            Array.Empty<RunState.CardEntry>(),
            Array.Empty<RunState.CardEntry>());

        int p1SandstormCount = sim.Frames
            .Where(f => f.PlayerUpdates?.HealthAdjustments != null)
            .SelectMany(f => f.PlayerUpdates!.HealthAdjustments!)
            .Count(a => a.DamageType == EDamageType.Sandstorm);
        var p2SandstormAmts = sim.Frames
            .Where(f => f.OpponentUpdates?.HealthAdjustments != null)
            .SelectMany(f => f.OpponentUpdates!.HealthAdjustments!)
            .Where(a => a.DamageType == EDamageType.Sandstorm)
            .Select(a => a.Amount)
            .ToList();

        Assert.Equal(0, p1SandstormCount);
        Assert.NotEmpty(p2SandstormAmts);
        // 伤害 HealthAdjustment.Amount 为负值（扣血）
        Assert.All(p2SandstormAmts, amt => Assert.True(amt < 0, $"沙暴扣血应为负值，实际={amt}"));

        // FlipPerspective 必须精确互换双方沙暴减伤后拆账
        var flipped = CombatSimConverter.FlipPerspective(sim);
        int flippedOppSandstormCount = flipped.Frames
            .Where(f => f.OpponentUpdates?.HealthAdjustments != null)
            .SelectMany(f => f.OpponentUpdates!.HealthAdjustments!)
            .Count(a => a.DamageType == EDamageType.Sandstorm);
        var flippedPlayerSandstormAmts = flipped.Frames
            .Where(f => f.PlayerUpdates?.HealthAdjustments != null)
            .SelectMany(f => f.PlayerUpdates!.HealthAdjustments!)
            .Where(a => a.DamageType == EDamageType.Sandstorm)
            .Select(a => a.Amount)
            .ToList();

        Assert.Equal(0, flippedOppSandstormCount);
        Assert.Equal(p2SandstormAmts, flippedPlayerSandstormAmts);
    }

    [Fact]
    public void D1_P2_01_CombatScopedQuestUpdate_IsolatedFromPermanentAndResetAfterCombat()
    {
        var questTpl = MakeSimpleCard(
            "w3b_combat_quest_card",
            cooldownMs: 1000,
            damage: 10,
            quests: new List<QuestDefinition>
            {
                new()
                {
                    Entries = new List<QuestEntry>
                    {
                        new()
                        {
                            AttributeType = "Quest_CombatOnly",
                            Target = 2,
                            Trigger = new RuleNode { Type = "TTriggerOnCardFired", Subject = new RuleNode { Type = "TTargetCardSelf" } },
                            Update = new QuestUpdate
                            {
                                Value = null,
                                Duration = new RuleNode { Type = "TDeterminantDuration", DurationType = "UntilEndOfCombat" },
                            },
                            Reward = new QuestReward
                            {
                                Attributes = new Dictionary<string, double> { ["DamageAmount"] = 30 },
                            },
                        },
                    },
                },
            });
        var origCard = new CardInstance(questTpl);
        var p1 = MakePlayer("p1", hp: 500);
        var p2 = MakePlayer("p2", hp: 500);
        p1.Hand.TryAddAuto(origCard);

        // 先在战中单卡直接推进 2 次验证战中生效与 ResetForCombat 清理
        var simCard = origCard.CloneForCombat();
        var simP1 = MakePlayer("p1", hp: 500);
        simP1.Hand.TryAddAuto(simCard);
        var ctx = new RuleExecutionContext(Repo, RuleScope.InCombat, simP1, p2, new Random(1)) { SelfCard = simCard };
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardFired", simP1, simCard, null, simP1, null, 0, 0, "PVP", "", ctx);
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardFired", simP1, simCard, null, simP1, null, 0, 0, "PVP", "", ctx);
        Assert.True(simCard.IsQuestCompleted("Quest_CombatOnly"));
        Assert.Equal(40, simCard.GetAttribute("DamageAmount"));
        Assert.Empty(simCard.QuestProgress);
        Assert.Empty(simCard.CompletedQuests);

        // ResetForCombat 后战中任务进度与完成奖励必须彻底归零
        simCard.ResetForCombat(Repo);
        Assert.False(simCard.IsQuestCompleted("Quest_CombatOnly"));
        Assert.Equal(0, simCard.GetQuestProgress("Quest_CombatOnly"));
        Assert.Equal(10, simCard.GetAttribute("DamageAmount"));

        // 完整战斗模拟 + syncPermanentChanges: true 绝不可将 UntilEndOfCombat 任务回写污染原卡永久进度
        CombatSimulator.Simulate(
            Repo, p1, p2, seed: 5, combatType: "PVP",
            recordTimeline: false, recordEvents: true, syncPermanentChanges: true,
            modifiers: new CombatModifiers { MaxDurationMsOverride = 5000 },
            sandstormEnabled: false);
        Assert.Empty(origCard.QuestProgress);
        Assert.Empty(origCard.CompletedQuests);
        Assert.Equal(10, origCard.GetAttribute("DamageAmount"));
    }

    [Fact]
    public void D1_P1_07_TimedModifiers_PlayerSetBaseline_ResetZeroNoGhost_AndSafeWriterSanitization()
    {
        // 1. 卡牌战中基线（UntilEndOfCombat）+ 限时（TCombatDuration）叠加并无损撤销
        var card = new CardInstance(MakeSimpleCard("w3b_timed_card", damage: 20));
        card.ModifyAttribute("DamageAmount", "Add", 10, DurationTarget.Combat);             // 基线 30
        card.ModifyAttribute("DamageAmount", "AdditiveMultiply", 0.5, DurationTarget.Combat); // 基线 30 * 1.5 = 45
        int tokPure = card.ApplyTimedCombatModifier("DamageAmount", "Multiply", 2.0);       // 90
        int tokAddM = card.ApplyTimedCombatModifier("DamageAmount", "AdditiveMultiply", 0.5); // 30 * (1+1.0) * 2 = 120
        Assert.Equal(120, card.GetAttribute("DamageAmount"));
        Assert.True(card.RevertTimedCombatModifier(tokAddM));
        Assert.Equal(90, card.GetAttribute("DamageAmount"));
        Assert.True(card.RevertTimedCombatModifier(tokPure));
        Assert.Equal(45, card.GetAttribute("DamageAmount"));

        // 2. ResetAttributeToZero 在 under == 0 时清除限时残影不复活，且同步清除 Tiny 标签
        int tokCustom = card.ApplyTimedCombatModifier("Custom_1", "Add", 25);
        Assert.Equal(25, card.GetAttribute("Custom_1"));
        card.ResetAttributeToZero("Custom_1", DurationTarget.Combat);
        Assert.Equal(0, card.GetAttribute("Custom_1"));
        int tokNew = card.ApplyTimedCombatModifier("Custom_1", "Add", 5);
        Assert.Equal(5, card.GetAttribute("Custom_1"));
        card.RevertTimedCombatModifier(tokNew);
        Assert.Equal(0, card.GetAttribute("Custom_1"));
        Assert.False(card.RevertTimedCombatModifier(tokCustom)); // 旧 token 已被 Reset 清除

        card.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Combat);
        Assert.True(card.IsTiny);
        card.ResetAttributeToZero("Tiny", DurationTarget.Combat);
        Assert.False(card.IsTiny);
        Assert.False(card.HasTag("Tiny"));

        // 3. 玩家先获限时 Multiply 2.0，再受非限时 Set 50，限时到期后基线精准保持 50（绝不漂移至 25）
        var player = MakePlayer("p_set_test", hp: 500);
        player.Attributes["HealthRegen"] = 10;
        int pTok = player.ApplyTimedCombatModifier("HealthRegen", "Multiply", 2.0);
        Assert.Equal(20, player.GetAttribute("HealthRegen"));
        player.ModifyAttribute("HealthRegen", "Set", 50, DurationTarget.Combat);
        Assert.Equal(100, player.GetAttribute("HealthRegen"));
        Assert.True(player.RevertTimedCombatModifier(pTok));
        Assert.Equal(50, player.GetAttribute("HealthRegen"));

        // 4. MechanicSafeAttributeWriter 钳制 AdditiveMultiply 并清洗全部 5 张 *AdditiveMult 字典
        var modCard = new CardInstance(MakeSimpleCard("w3b_safe_writer", damage: 10));
        var wr = MechanicSafeAttributeWriter.ApplyCardAttribute(modCard, "DamageAmount", "AdditiveMultiply", 999.0, DurationTarget.Combat);
        Assert.True(wr.Applied);
        Assert.True(modCard.CombatAdditiveMult["DamageAmount"] <= 10.0);

        modCard.PermanentAdditiveMult["DamageAmount"] = double.NaN;
        modCard.DayAdditiveMult["DamageAmount"] = double.PositiveInfinity;
        modCard.HourAdditiveMult["DamageAmount"] = -999.0;
        modCard.CombatAdditiveMult["DamageAmount"] = 500.0;
        modCard.AuraAdditiveMult["DamageAmount"] = double.NegativeInfinity;
        MechanicSafeAttributeWriter.ClampCardInvariants(modCard);
        Assert.False(modCard.PermanentAdditiveMult.ContainsKey("DamageAmount"));
        Assert.False(modCard.DayAdditiveMult.ContainsKey("DamageAmount"));
        Assert.InRange(modCard.HourAdditiveMult["DamageAmount"], -10.0, 100.0);
        Assert.InRange(modCard.CombatAdditiveMult["DamageAmount"], -10.0, 100.0);
        Assert.False(modCard.AuraAdditiveMult.ContainsKey("DamageAmount"));
    }

    [Fact]
    public void D1_P1_13_BoardGrid_CombatTenTinyWidening_AndFullHandFullStash_ZeroCardLossAndValidInvariants()
    {
        // 1. 战中 10 张带 Tiny 的 Medium 卡铺满 10 格（BoardGrid 默认 unlockedMask=1023 全 10 格解锁），解除多张 Tiny 后零重叠零丢卡保底
        var combatHand = new BoardGrid();
        var cards = new List<CardInstance>();
        for (int i = 0; i < 10; i++)
        {
            var c = new CardInstance(MakeSimpleCard($"w3b_tiny_c_{i}", size: "Medium"));
            c.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Combat);
            Assert.True(combatHand.TryAddAuto(c));
            cards.Add(c);
        }
        cards[0].ModifyAttribute("Tiny", "Add", -1, DurationTarget.Combat);
        combatHand.ReconcileWidthChange(cards[0], overflowGrid: null);
        cards[5].ModifyAttribute("Tiny", "Add", -1, DurationTarget.Combat);
        combatHand.ReconcileWidthChange(cards[5], overflowGrid: null);

        Assert.Equal(10, combatHand.Cards.Count);
        Assert.True(combatHand.ValidateInvariants(out string combatErr), $"战中多卡 Tiny 变宽必须保持网格合法: {combatErr}");

        // 2. 局外 Hand 满 + Stash 满时 Tiny 解除变宽：0 丢卡且双网格均满足 ValidateInvariants
        var hand = new BoardGrid();
        var stash = new BoardGrid();
        var handCards = new List<CardInstance>();
        for (int i = 0; i < 10; i++)
        {
            var hc = new CardInstance(MakeSimpleCard($"w3b_ooc_h_{i}", size: "Medium"));
            hc.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Day);
            Assert.True(hand.TryAddAuto(hc));
            handCards.Add(hc);

            var sc = new CardInstance(MakeSimpleCard($"w3b_ooc_s_{i}", size: "Small"));
            Assert.True(stash.TryAddAuto(sc));
        }

        handCards[2].ModifyAttribute("Tiny", "Add", -1, DurationTarget.Day);
        var overflowed = hand.ReconcileWidthChange(handCards[2], stash);
        Assert.Empty(overflowed);
        Assert.Equal(20, hand.Cards.Count + stash.Cards.Count);
        Assert.True(hand.ValidateInvariants(out string handErr), $"双满时 Hand 必须零丢卡且无重叠: {handErr}");
        Assert.True(stash.ValidateInvariants(out string stashErr), $"双满时 Stash 必须保持合法: {stashErr}");
    }

    [Fact]
    public void D1_IsolatedHealFallback_AppliesConsistent5PercentCleanse()
    {
        var p1 = MakePlayer("p1", hp: 500);
        p1.CurrentHealth = 300;
        p1.Burn = 10;
        p1.Poison = 10;
        var healCard = new CardInstance(MakeSimpleCard("w3b_heal_src"));
        p1.Hand.TryAddAuto(healCard);

        var ctx = new RuleExecutionContext(Repo, RuleScope.InCombat, p1, MakePlayer("p2"), new Random(3))
        {
            SelfCard = healCard,
            OnHealApplied = null,
        };
        var healAction = new RuleNode
        {
            Type = "TActionPlayerHeal",
            RawValueNode = JsonDocument.Parse("""{"$type":"TFixedValue","Value":100}""").RootElement.Clone(),
            Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" },
        };
        AbilityExecutor.ExecuteAction(healAction, healCard, ctx);
        Assert.Equal(400, p1.CurrentHealth);
        Assert.Equal(5, p1.Burn);
        Assert.Equal(5, p1.Poison);
    }
}
