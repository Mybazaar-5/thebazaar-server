using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wiki / BazaarDB 52 问（第 1–41 题：战斗底层时序 / 状态公式 / 卡牌生命周期与附魔）回归测试套件。
/// 全面覆盖本次四分类裁决与根因修复项，并尽量直接加载 GameData.db 真实卡牌模板验证。
/// </summary>
public sealed class V12WikiCombatMechanicsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static CardTemplate GetTemplate(string templateId)
        => Repo.Value.GetCard(templateId)
           ?? throw new InvalidOperationException($"未能在 GameData.db 中找到模板 {templateId}");

    private static CardInstance CreateRealCard(string templateId, CardTier? tier = null, string? enchantment = null, string? instanceId = null)
        => new(GetTemplate(templateId), tier, enchantment, instanceId);

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, JsonOpts)
           ?? throw new InvalidOperationException($"RuleNode 反序列化失败: {json}");

    private static CardInstance CreateCustomItem(
        string id,
        string name,
        CardSize size = CardSize.Small,
        CardTier tier = CardTier.Silver,
        Dictionary<string, double>? attrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null,
        List<string>? hiddenTags = null,
        string? enchantment = null)
    {
        var tierKey = tier.ToString();
        var abDict = abilities ?? new Dictionary<string, AbilityDefinition>();
        var auDict = auras ?? new Dictionary<string, AuraDefinition>();
        var tpl = new CardTemplate
        {
            TypeDiscriminator = "TCardItem",
            Id = id,
            Type = "Item",
            InternalName = name,
            StartingTier = tierKey,
            Size = size.ToString(),
            Tags = tags ?? new List<string>(),
            HiddenTags = hiddenTags ?? new List<string>(),
            Abilities = abDict,
            Auras = auDict,
            Tiers = new Dictionary<string, TierData>
            {
                [tierKey] = new TierData
                {
                    Attributes = attrs ?? new Dictionary<string, double>(),
                    AbilityIds = abDict.Keys.ToList(),
                    AuraIds = auDict.Keys.ToList()
                }
            }
        };
        return new CardInstance(tpl, tier, enchantment, $"{id}_inst");
    }

    private static CardInstance CreateCustomSkill(
        string id,
        string name,
        Dictionary<string, AbilityDefinition> abilities,
        Dictionary<string, double>? attrs = null)
    {
        var tpl = new CardTemplate
        {
            TypeDiscriminator = "TCardSkill",
            Id = id,
            Type = "Skill",
            InternalName = name,
            StartingTier = "Silver",
            Size = "Small",
            Abilities = abilities,
            Tiers = new Dictionary<string, TierData>
            {
                ["Silver"] = new TierData
                {
                    Attributes = attrs ?? new Dictionary<string, double>(),
                    AbilityIds = abilities.Keys.ToList()
                }
            }
        };
        return new CardInstance(tpl, CardTier.Silver, null, $"{id}_inst");
    }

    // =========================================================================
    // 模块一：Tick 粒度、ICD 队列与数值成长豁免、CD 下限、Use/Multicast/Charge/ForceUse/Trigger
    // =========================================================================

    [Fact]
    public void Q03_StatGains_Bypass250msIcdAndStackImmediatelyInSameTick()
    {
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 3 个同帧（1000ms）触发的快武器
        for (int i = 0; i < 3; i++)
        {
            var fastWeapon = CreateCustomItem(
                $"fast_w_{i}", $"FastWeapon_{i}",
                attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 1 },
                tags: new() { "Weapon" },
                abilities: new()
                {
                    ["0"] = new AbilityDefinition
                    {
                        Id = "0",
                        ActiveIn = "HandOnly",
                        Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                        Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                    }
                });
            Assert.True(p1.Hand.TryAddAuto(fastWeapon));
        }

        // 数值成长被动卡（Stat Gain）：当使用任意物品时自身 DamageAmount +10 —— 应豁免 250ms ICD 同毫秒立即叠加 +30
        var statScaler = CreateCustomItem(
            "stat_scaler", "StatScaler",
            attrs: new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 10 },
            tags: new() { "Weapon" },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                    Action = ParseNode("""{"$type":"TActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":10},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetCardSelf"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        Assert.True(p1.Hand.TryAddAuto(statScaler));

        // 对比对照组：非数值成长的输出型被动（When you use another item -> Deal 20 Damage）应走 250ms ICD FIFO 顺延
        var outputWatcher = CreateCustomItem(
            "output_watcher", "OutputWatcher",
            attrs: new() { ["DamageAmount"] = 20 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        Assert.True(p1.Hand.TryAddAuto(outputWatcher));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        // t=1000ms 时 3 把快武器同帧触发 -> statScaler 应在 t=1000ms 即时叠加到 Damage=40 (10 + 3*10)
        var frame980 = res.Timeline.First(t => t.TimeMs == 980);
        var scalerFrame = frame980.Cards.First(c => c.InstanceId == statScaler.InstanceId);
        Assert.Equal(40, scalerFrame.DamageAmount);

        // 输出型被动 outputWatcher 的 3 次 20 伤害应在 980ms, ~1240ms, ~1480ms 分步排队结算，而非单帧全打出
        var watcherDmgEvents = res.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == outputWatcher.InstanceId && e.TimeMs < 1800)
            .ToList();
        Assert.Equal(3, watcherDmgEvents.Count);
        Assert.Equal(980, watcherDmgEvents[0].TimeMs);
        Assert.True(watcherDmgEvents[1].TimeMs >= 1220, $"第二次输出应受 250ms ICD 队列顺延，实际 TimeMs={watcherDmgEvents[1].TimeMs}");
        Assert.True(watcherDmgEvents[2].TimeMs >= 1480, $"第三次输出应受 250ms ICD 队列顺延，实际 TimeMs={watcherDmgEvents[2].TimeMs}");
    }

    [Fact]
    public void Q04_AtomicClock_1sCooldownUnderHasteStillTakes1000ms()
    {
        // 对照组（无 Haste）
        var p1Base = new PlayerState("p1b", "P1B", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2Base = new PlayerState("p2b", "P2B", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var clockBase = CreateRealCard("86769097-271e-46ee-8d42-317c6d8a7a09", CardTier.Silver);
        Assert.True(p1Base.Hand.TryAddAuto(clockBase));
        Assert.True(p2Base.Hand.TryAddAuto(CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze)));
        var baseFires = CombatSimulator.Simulate(Repo.Value, p1Base, p2Base, seed: 42, recordTimeline: false, recordEvents: true)
            .Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == clockBase.InstanceId)
            .Select(e => e.TimeMs).ToList();

        var p1 = new PlayerState("p1", "P1", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 真实 Silver Atomic Clock: CooldownMax = 1000ms, AmmoMax = 2
        var clock = CreateRealCard("86769097-271e-46ee-8d42-317c6d8a7a09", CardTier.Silver);
        var haster = CreateCustomItem(
            "start_haste", "StartHaste",
            attrs: new() { ["HasteAmount"] = 5000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
                }
            });
        var dummy = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze); // Cutlass
        Assert.True(p1.Hand.TryAddAuto(clock));
        Assert.True(p1.Hand.TryAddAuto(haster));
        Assert.True(p2.Hand.TryAddAuto(dummy));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: false, recordEvents: true);
        var clockFires = res.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == clock.InstanceId)
            .Select(e => e.TimeMs)
            .ToList();

        Assert.Equal(2, clockFires.Count);
        Assert.Equal(baseFires[0], clockFires[0]); // 满 Haste 下 1.0s CD 物品不加速到 0.5s(480ms)，与无 Haste 一致维持首击 980ms
        Assert.Equal(980, clockFires[0]);
        Assert.Equal(1980, clockFires[1]);
    }

    [Fact]
    public void Q05_Q06_UseStrictDefinition_ExcludesSkillsAndPassives_AndOneActivationEqualsOneUse()
    {
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 真实 Bottled Lightning (5000ms CD, 同卡具备 Damage 与 Burn 两条 TTriggerOnCardFired 能力，AmmoMax=1)
        var bottled = CreateRealCard("df87dc00-f0fe-4e47-b864-ad6b4bfea81a", CardTier.Bronze);
        // 无 CD 被动开场给盾，与被动 Skill，均不应计作 Use
        var passiveShield = CreateCustomItem(
            "passive_start", "PassiveStart",
            attrs: new() { ["ShieldApplyAmount"] = 50 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        var passiveSkill = CreateCustomSkill(
            "skill_start", "SkillStart",
            attrs: new() { ["ShieldApplyAmount"] = 25 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });

        // Use 计数器卡：每次自身手牌触发 TTriggerOnItemUsed 就给己方加 +10 Shield（借 ShieldApplied 事件计数）
        var useCounter = CreateCustomItem(
            "use_counter", "UseCounter",
            attrs: new() { ["ShieldApplyAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });

        Assert.True(p1.Hand.TryAddAuto(bottled));
        Assert.True(p1.Hand.TryAddAuto(passiveShield));
        Assert.True(p1.Hand.TryAddAuto(useCounter));
        p1.Skills.Add(passiveSkill);

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: false, recordEvents: true);
        var counterTriggers = res.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == useCounter.InstanceId)
            .ToList();

        // Bottled Lightning 仅 1 发弹药于第 250 帧(4980ms)激活 1 次（同时造成伤害与灼烧），仅产生 1 次 Use；开场被动与技能均不计 Use
        Assert.Single(counterTriggers);
        Assert.Equal(4980, counterTriggers[0].TimeMs);
    }

    [Fact]
    public void Q07_Q08_Q09_Multicast_Interval250ms_IndependentUse_SingleAmmo_MidCastConditionAndCrit()
    {
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 3x Multicast、AmmoMax=2、CD=2000ms；第 1 能力挂 5 Burn，第 2 能力仅在敌人 Burn >= 5 时额外打 50 Damage（验证中途每发重估前置条件）
        var caster = CreateCustomItem(
            "multi_caster", "MultiCaster",
            attrs: new() { ["CooldownMax"] = 2000, ["Multicast"] = 3, ["AmmoMax"] = 2, ["DamageAmount"] = 10, ["BurnApplyAmount"] = 5, ["CritChance"] = 100 },
            tags: new() { "Weapon" },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Prerequisites = new()
                    {
                        ParseNode("""{"$type":"TPrerequisitePlayer","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Burn","ComparisonOperator":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":5}}}}""")
                    },
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });

        var useCounter = CreateCustomItem(
            "use_cnt", "UseCounter",
            attrs: new() { ["ShieldApplyAmount"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });

        Assert.True(p1.Hand.TryAddAuto(caster));
        Assert.True(p1.Hand.TryAddAuto(useCounter));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);
        var fires = res.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == caster.InstanceId)
            .Select(e => e.TimeMs)
            .ToList();

        // AmmoMax=2 -> 共激活 2 轮，每轮 3 发 Multicast = 共 6 次发射；第 3 轮因 0 弹药被阻断
        Assert.Equal(6, fires.Count);
        Assert.Equal(1980, fires[0]);
        Assert.InRange(fires[1], 2220, 2260); // scheduled 1980+250=2230 -> 20ms tick = 2240
        Assert.Equal(2500, fires[2]);         // scheduled 2240+250=2490 -> 20ms tick = 2500
        Assert.Equal(3980, fires[3]);

        // 首轮中：第 1 发执行前敌人 Burn==0，故 Damage 未触发；第 1 发挂上 Burn>=5 后，第 2 发(t~2260)、第 3 发(t=2500) 重估条件成立 -> 均打出暴击伤害!
        var firstRoundDmg = res.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == caster.InstanceId && e.TimeMs < 3000)
            .ToList();
        Assert.Equal(2, firstRoundDmg.Count);
        Assert.True(firstRoundDmg.All(d => d.IsCrit && d.Value == 20)); // Crit 2x

        // 每发 Multicast 独立算 1 次 Use -> 6 发共触发 6 次
        var uses = res.Events.Count(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == useCounter.InstanceId);
        Assert.Equal(6, uses);

        // 战后不污染原卡弹药数
        Assert.Equal(2, caster.CurrentAmmo);
    }

    [Fact]
    public void Q10_Q11_Q12_ChargeAdvancesFrozenAndIgnoresHaste_ForceUseRespectsFreezeNoCdZeroAmmoAndFullMulticast()
    {
        var p1 = new PlayerState("p1", "P1", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 1) Charge 靶卡：4000ms CD，t=0 受 Freeze 3000 + Haste 4000，t=200 受 Charge 2000 -> 应仅充能 2000（不被 Haste 乘 2），t=3000 解冻后剩余 2000 在 Haste 2x 下于 t=4000 发射
        var chargeTarget = CreateCustomItem(
            "charge_tgt", "ChargeTarget",
            attrs: new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 10 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        // 给 chargeTarget 开局冻结+加速并在 t=1000 充能 2000ms
        var setupAndCharger = CreateCustomItem(
            "charger", "Charger",
            attrs: new() { ["CooldownMax"] = 1000, ["ChargeAmount"] = 2000, ["FreezeAmount"] = 3000, ["HasteAmount"] = 5000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardPositional","Origin":"Self","TargetMode":"LeftCard"}},{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardPositional","Origin":"Self","TargetMode":"LeftCard"}}]}""")
                },
                ["1"] = new AbilityDefinition
                {
                    Id = "1",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardCharge","Target":{"$type":"TTargetCardPositional","Origin":"Self","TargetMode":"LeftCard"}}""")
                }
            });

        // 2) ForceUse 测试目标：合法 2x Multicast (Ammo=1), 0 弹药卡, 无 CD 卡, 冻结卡
        var legalMulti = CreateCustomItem(
            "force_ok", "ForceOk",
            attrs: new() { ["CooldownMax"] = 9000, ["Multicast"] = 2, ["AmmoMax"] = 1, ["DamageAmount"] = 5 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });
        var noCdCard = CreateCustomItem(
            "force_nocd", "ForceNoCd",
            attrs: new() { ["DamageAmount"] = 5 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });
        var zeroAmmoCard = CreateCustomItem(
            "force_noammo", "ForceNoAmmo",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["DamageAmount"] = 5 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });
        var frozenCard = CreateCustomItem(
            "force_frozen", "ForceFrozen",
            attrs: new() { ["CooldownMax"] = 9000, ["DamageAmount"] = 5, ["FreezeAmount"] = 5000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardSelf"}}""") },
                ["1"] = new AbilityDefinition { Id = "1", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });

        // ForceUse 触发器：于 t=1500ms 分别 ForceUse 己方手牌 (先用完 zeroAmmoCard 在 t=1000 自燃用光 1 弹药)
        var forceDriver = CreateCustomItem(
            "force_driver", "ForceDriver",
            attrs: new() { ["CooldownMax"] = 1500, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardForceUse","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
                }
            });

        Assert.True(p1.Hand.TryPlaceOrShift(chargeTarget, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(setupAndCharger, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(legalMulti, 2));
        Assert.True(p1.Hand.TryPlaceOrShift(noCdCard, 3));
        Assert.True(p1.Hand.TryPlaceOrShift(zeroAmmoCard, 4));
        Assert.True(p1.Hand.TryPlaceOrShift(frozenCard, 5));
        Assert.True(p1.Hand.TryPlaceOrShift(forceDriver, 6));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: false, recordEvents: true);

        // Charge 验证：t=1000 充能 2000 后不会在 Frozen 期间立即触发，而在 t=3000 解冻后凭 Haste(2x) 跑完剩下 2000ms，即 t=4000 首次开火
        var chargeTargetFires = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == chargeTarget.InstanceId).Select(e => e.TimeMs).ToList();
        Assert.True(chargeTargetFires.Count >= 1);
        Assert.Equal(3980, chargeTargetFires[0]);

        // ForceUse 于 t=1480ms 触发：
        // - noCdCard: 0 次
        // - frozenCard: 0 次
        // - zeroAmmoCard: 仅 t=980 自然触发 1 次（耗光弹药），t=1480 ForceUse 被 0 弹药阻断！
        // - legalMulti: ForceUse 时打出完整 2 发 Multicast（t=1480 与 ~1740）且消耗完唯一 1 发弹药（9000ms 后不再开火）
        Assert.Empty(res.Events.Where(e => e.SourceCardInstanceId == noCdCard.InstanceId && e.Type == CombatEventType.CardFired));
        Assert.Empty(res.Events.Where(e => e.SourceCardInstanceId == frozenCard.InstanceId && e.Type == CombatEventType.CardFired && e.TimeMs < 5000));
        var zeroAmmoFires = res.Events.Where(e => e.SourceCardInstanceId == zeroAmmoCard.InstanceId && e.Type == CombatEventType.CardFired).Select(e => e.TimeMs).ToList();
        Assert.Equal(new[] { 980 }, zeroAmmoFires);
        var legalFires = res.Events.Where(e => e.SourceCardInstanceId == legalMulti.InstanceId && e.Type == CombatEventType.CardFired).Select(e => e.TimeMs).ToList();
        Assert.Equal(2, legalFires.Count);
        Assert.Equal(1480, legalFires[0]);
        Assert.InRange(legalFires[1], 1720, 1760);
    }

    [Fact]
    public void Q13_Q14_WhenYouSlowFiresAgainstImmune_And_WhenYouBurnShieldIncludesSkillAndPassive()
    {
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 对手只有一张 Radiant（全免疫 Slow）的有 CD 卡
        var enemyRadiant = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze, enchantment: "Radiant");
        Assert.True(p2.Hand.TryAddAuto(enemyRadiant));

        // 1) 我方在 t=1000ms 对免疫目标施加 Slow -> 尽管 SlowMs==0，When you Slow 仍必须触发！
        var slower = CreateCustomItem(
            "slower", "Slower",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["SlowAmount"] = 2000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand"}}""") }
            });
        var slowWatcher = CreateCustomItem(
            "slow_watch", "SlowWatcher",
            attrs: new() { ["DamageAmount"] = 33 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedSlow","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });

        // 2) 技能挂 Burn + 无 CD 被动卡加 Shield -> 均需触发 When you Burn / When you Shield
        var skillBurner = CreateCustomSkill(
            "skill_burn", "SkillBurn",
            attrs: new() { ["BurnApplyAmount"] = 4 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });
        var passiveShielder = CreateCustomItem(
            "passive_shld", "PassiveShielder",
            attrs: new() { ["ShieldApplyAmount"] = 15 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
            });
        var burnShieldWatcher = CreateCustomItem(
            "bs_watch", "BurnShieldWatcher",
            attrs: new() { ["DamageAmount"] = 11, ["ShieldApplyAmount"] = 7 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedBurn","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") },
                ["1"] = new AbilityDefinition { Id = "1", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedShield","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
            });

        Assert.True(p1.Hand.TryAddAuto(slower));
        Assert.True(p1.Hand.TryAddAuto(slowWatcher));
        Assert.True(p1.Hand.TryAddAuto(passiveShielder));
        Assert.True(p1.Hand.TryAddAuto(burnShieldWatcher));
        p1.Skills.Add(skillBurner);

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        // Radiant 免疫目标在 t=1000ms 时 SlowMs 保持 0，但 slowWatcher 成功打出 33 伤害
        var frame1000 = res.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(0, frame1000.Cards.First(c => c.InstanceId == enemyRadiant.InstanceId).SlowMs);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == slowWatcher.InstanceId && e.Value == 33);

        // 技能 Burn 与无 CD 被动 Shield 均在 t=0 触发了 When you Burn / When you Shield 监控器
        Assert.Contains(res.Events, e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == burnShieldWatcher.InstanceId && e.Value == 11);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == burnShieldWatcher.InstanceId && e.Value == 7);
    }

    // =========================================================================
    // 模块二：Burn/Poison/Heal/MaxHP/Haste-Slow-Freeze/Flying/Crit/Rage/Sandstorm
    // =========================================================================

    [Fact]
    public void Q15_Q16_Q17_BurnShieldHalfAbsorbOddOverflow_Burn3PercentDecay_PoisonPenetratesAndNoDecay()
    {
        // Case A: 5 Burn 打 2 Shield -> 耗尽 2 盾并穿 1 血伤
        var p1a = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 2000, unlockedHandSlots: 10);
        var p2a = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(p1a.Hand.TryAddAuto(CreateCustomItem("b5", "Burn5", attrs: new() { ["BurnApplyAmount"] = 5 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));
        Assert.True(p2a.Hand.TryAddAuto(CreateCustomItem("s2", "Shield2", attrs: new() { ["ShieldApplyAmount"] = 2 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));
        var resA = CombatSimulator.Simulate(Repo.Value, p1a, p2a, seed: 1, recordTimeline: true, recordEvents: true);
        var tick500A = resA.Timeline.First(t => t.TimeMs == 500);
        Assert.Equal(0, tick500A.P2Shield);
        Assert.Equal(1999, tick500A.P2Hp); // 5 Burn - 2*2 Shield = 1 穿透伤害

        // Case B: Burn 3% 精确衰减边界: 33->32, 34->33, 67->65
        foreach (var (initBurn, expectedAfterOneTick) in new[] { (33, 32), (34, 33), (67, 65) })
        {
            var p1b = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 2000, unlockedHandSlots: 10);
            var p2b = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 2000, unlockedHandSlots: 10);
            Assert.True(p1b.Hand.TryAddAuto(CreateCustomItem($"b{initBurn}", $"Burn{initBurn}", attrs: new() { ["BurnApplyAmount"] = initBurn }, abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            })));
            var resB = CombatSimulator.Simulate(Repo.Value, p1b, p2b, seed: 1, recordTimeline: true, recordEvents: false);
            var tick500B = resB.Timeline.First(t => t.TimeMs == 500);
            Assert.Equal(expectedAfterOneTick, tick500B.P2Burn);
        }

        // Case C: Poison 1000ms 跳动、完全无视 Shield、且层数永不衰减
        var p1c = new PlayerState("p1", "P1", "Mak", initialHealthMax: 2000, unlockedHandSlots: 10);
        var p2c = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(p1c.Hand.TryAddAuto(CreateCustomItem("poi15", "Poison15", attrs: new() { ["PoisonApplyAmount"] = 15 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));
        Assert.True(p2c.Hand.TryAddAuto(CreateCustomItem("sh500", "Shield500", attrs: new() { ["ShieldApplyAmount"] = 500 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));
        var resC = CombatSimulator.Simulate(Repo.Value, p1c, p2c, seed: 1, recordTimeline: true, recordEvents: true);
        var tick1000C = resC.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(500, tick1000C.P2Shield);
        Assert.Equal(1985, tick1000C.P2Hp);
        Assert.Equal(15, tick1000C.P2Poison);
        var tick2000C = resC.Timeline.First(t => t.TimeMs == 2000);
        Assert.Equal(1970, tick2000C.P2Hp);
        Assert.Equal(15, tick2000C.P2Poison);
    }

    [Fact]
    public void Q18_Q19_Q20_HealCleanses5PercentAtFullHp_RegenAndLifestealExcluded_MaxHpBoostsCurrentHpWithoutHeal()
    {
        // Case A: 满血状态 Heal 100 -> 净化 Burn 与 Poison 各 max(1, floor(100*0.05)) = 5，且触发 When you Heal
        var p1a = new PlayerState("p1", "P1", "Pygmalien", initialHealthMax: 2000, unlockedHandSlots: 10);
        var p2a = new PlayerState("p2", "P2", "Mak", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(p2a.Hand.TryAddAuto(CreateCustomItem("dot", "DotInit", attrs: new() { ["BurnApplyAmount"] = 10, ["PoisonApplyAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}]}""") }
        })));
        Assert.True(p1a.Hand.TryAddAuto(CreateCustomItem("full_healer", "FullHealer", attrs: new() { ["CooldownMax"] = 200, ["AmmoMax"] = 1, ["HealAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));
        var healWatchA = CreateCustomItem("hw_a", "HealWatcherA", attrs: new() { ["ShieldApplyAmount"] = 20 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedHeal","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });
        Assert.True(p1a.Hand.TryAddAuto(healWatchA));
        // 注意 CooldownMax=200 会被 Engine 按 >= 1000 钳制；我们在 t=1000 检查状态（此前 t=500 Burn 从 10 跳 1 次衰减成 9，随后 t=1000 Heal 净化 5 -> Burn 4, Poison 5）
        var resA = CombatSimulator.Simulate(Repo.Value, p1a, p2a, seed: 1, recordTimeline: true, recordEvents: true);
        var f1000A = resA.Timeline.First(t => t.TimeMs == 1000);
        // 满血 2000 执行 Heal 100 仍净化 5 层（t=500 Burn 10->9，t=1000 Burn 9-1(tick)=8 或 9-5=4；按先 card fire 再 dot 或相反：9-5=4, 4->3）
        Assert.True(f1000A.P1Poison <= 5, $"满血 Heal 100 应净化至少 5 层 Poison，实际={f1000A.P1Poison}");
        Assert.Contains(resA.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == healWatchA.InstanceId && e.Value == 20);

        // Case B: Regen 与 Lifesteal 不触发 5% 净化也不触发 When you Heal
        var p1b = new PlayerState("p1", "P1", "Mak", initialHealthMax: 2000, unlockedHandSlots: 10);
        var p2b = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(p2b.Hand.TryAddAuto(CreateCustomItem("p_apply", "PoisonInit", attrs: new() { ["PoisonApplyAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));
        Assert.True(p1b.Hand.TryAddAuto(CreateCustomItem("regen_ls", "RegenAndLifesteal", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["DamageAmount"] = 50, ["Lifesteal"] = 100, ["RegenApplyAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerRegenApply","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") },
            ["1"] = new AbilityDefinition { Id = "1", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));
        var healWatchB = CreateCustomItem("hw_b", "HealWatcherB", attrs: new() { ["ShieldApplyAmount"] = 99 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedHeal","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });
        Assert.True(p1b.Hand.TryAddAuto(healWatchB));
        var resB = CombatSimulator.Simulate(Repo.Value, p1b, p2b, seed: 1, recordTimeline: true, recordEvents: true);
        var f1500B = resB.Timeline.First(t => t.TimeMs == 1500);
        Assert.Equal(10, f1500B.P1Poison); // Regen 与 Lifesteal 均不得净化 Poison
        Assert.DoesNotContain(resB.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == healWatchB.InstanceId);

        // Case C: 战中加 HealthMax 同步提高 CurrentHealth 且不算 Heal
        var p1c = new PlayerState("p1", "P1", "Pygmalien", initialHealthMax: 1000, unlockedHandSlots: 10);
        var p2c = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(p2c.Hand.TryAddAuto(CreateCustomItem("p_c", "PoisonC", attrs: new() { ["PoisonApplyAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));
        Assert.True(p1c.Hand.TryAddAuto(CreateCustomItem("maxhp_up", "MaxHpUp", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerModifyAttribute","AttributeType":"HealthMax","Operation":"Add","Value":{"$type":"TFixedValue","Value":200},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));
        var healWatchC = CreateCustomItem("hw_c", "HealWatcherC", attrs: new() { ["ShieldApplyAmount"] = 99 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardPerformedHeal","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfBoard"}}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });
        Assert.True(p1c.Hand.TryAddAuto(healWatchC));
        var resC = CombatSimulator.Simulate(Repo.Value, p1c, p2c, seed: 1, recordTimeline: true, recordEvents: true);
        var f1000C = resC.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(1200, f1000C.P1MaxHp);
        Assert.Equal(1190, f1000C.P1Hp); // 1000 + 200 MaxHp同步提升 - 10 PoisonTick
        Assert.Equal(10, f1000C.P1Poison);
        Assert.DoesNotContain(resC.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == healWatchC.InstanceId);
    }

    [Fact]
    public void Q21_Q22_Q23_Q24_HasteSlowCancel_FreezeTicksSlowHaste_NoCdExcludedFromHasteSlow_FlyingHalvesControl()
    {
        var p1 = new PlayerState("p1", "P1", "Stelle", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);

        // P1 卡 1: Haste(2000) + Slow(2000) 同时施加，共存期间应互相抵消以 1.0x 推进 -> 4000ms CD 在 3980ms 发射
        var cancelCard = CreateCustomItem("cancel_card", "CancelCard", attrs: new() { ["CooldownMax"] = 4000, ["AmmoMax"] = 1, ["DamageAmount"] = 5, ["HasteAmount"] = 2000, ["SlowAmount"] = 2000 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") },
            ["1"] = new AbilityDefinition { Id = "1", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardSelf"}},{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSelf"}}]}""") }
        });

        // P1 卡 2: Freeze(1000) + Slow(1000) 同时施加 -> 冻结期间 Slow 必须照常每帧流逝，t=980..1000ms 解冻时 SlowMs == 0！
        var frozenSlowCard = CreateCustomItem("frozen_slow", "FrozenSlowCard", attrs: new() { ["CooldownMax"] = 4000, ["AmmoMax"] = 1, ["DamageAmount"] = 5, ["FreezeAmount"] = 1000, ["SlowAmount"] = 1000 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") },
            ["1"] = new AbilityDefinition { Id = "1", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardSelf"}},{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSelf"}}]}""") }
        });

        // P1 卡 3: 无 CD 被动卡 -> 按官方 BazaarGameShared Conditionals.Priorities [CooldownMax>0, CooldownMax==0]，
        // 混池随机 Haste/Slow 优先命中带有 CooldownMax>0 的卡（跳过 noCdCard）；仅当整池无任何有 CD 卡且模板未限制 CooldownMax>0 时才回落。
        var noCdCard = CreateCustomItem("no_cd_tgt", "NoCdTarget", attrs: new() { ["DamageAmount"] = 1 }, tags: ["Tool"], abilities: new());

        // P1 卡 4: Flying 卡（且已飞，带 Tool 标签与 noCdCard 组成己方混池），测试：1) 承受 Slow/Freeze 时长减半 2) Haste 时长不减半 3) 混池随机 Haste 优先命中此有 CD 卡
        var flyingCard = CreateCustomItem("fly_card", "FlyingCard", attrs: new() { ["CooldownMax"] = 4000, ["AmmoMax"] = 1, ["SlowAmount"] = 2000, ["FreezeAmount"] = 2000, ["HasteAmount"] = 2000 }, tags: ["Tool"], abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardFlyingStart","Target":{"$type":"TTargetCardSelf"}},{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSelf"}},{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardSelf"}},{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardSelf"}}]}""")
            }
        });

        // 对手棋盘：1 个无 CD 卡 + 1 个有 CD 卡；己方加一个随机 Slow(敌方混池) 与 Haste(仅针对 Tool 混池即 noCdCard+flyingCard，不干扰 cancelCard)
        var p2NoCd = CreateCustomItem("p2_nocd", "P2NoCd", attrs: new() { ["DamageAmount"] = 1 });
        var p2WithCd = CreateCustomItem("p2_cd", "P2WithCd", attrs: new() { ["CooldownMax"] = 6000 });
        var p1RandomSlowAndHaste = CreateCustomItem("rand_sh", "RandSH", attrs: new() { ["SlowAmount"] = 2000, ["HasteAmount"] = 2000, ["SlowTargets"] = 1, ["HasteTargets"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand"}},{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardRandom","TargetSection":"SelfHand","ExcludeSelf":true,"Conditions":{"$type":"TCardConditionalTag","Tags":["Tool"],"Operator":"Any"}}}]}""")
            }
        });

        Assert.True(p1.Hand.TryPlaceOrShift(cancelCard, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(frozenSlowCard, 1));
        Assert.True(p1.Hand.TryPlaceOrShift(noCdCard, 2));
        Assert.True(p1.Hand.TryPlaceOrShift(flyingCard, 3));
        Assert.True(p1.Hand.TryPlaceOrShift(p1RandomSlowAndHaste, 4));
        Assert.True(p2.Hand.TryPlaceOrShift(p2NoCd, 0));
        Assert.True(p2.Hand.TryPlaceOrShift(p2WithCd, 1));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        // #21 共存抵消：cancelCard 精确在 3980ms 发射
        Assert.Equal(3980, res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == cancelCard.InstanceId).TimeMs);

        // #22 Freeze 期间 Slow 同步流逝：t=1000 时 frozenSlowCard 解冻且 SlowMs == 0，故能在 4980ms（1000冻结 + 4000推进）准时开火
        var f1000 = res.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(0, f1000.Cards.First(c => c.InstanceId == frozenSlowCard.InstanceId).FreezeMs);
        Assert.Equal(0, f1000.Cards.First(c => c.InstanceId == frozenSlowCard.InstanceId).SlowMs);
        Assert.Equal(4980, res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == frozenSlowCard.InstanceId).TimeMs);

        // #23 混池优先级（Conditionals.Priorities）：随机 Slow/Haste 优先命中 p2WithCd 与 flyingCard，跳过无 CD 卡 p2NoCd 与 noCdCard；
        // flyingCard 共收到自身 2000 + 随机 2000 = 4000ms Haste（t=0 首跳扣 20ms 后为 3980ms）
        var f20 = res.Timeline.First(t => t.TimeMs == 20);
        Assert.Equal(0, f20.Cards.First(c => c.InstanceId == p2NoCd.InstanceId).SlowMs);
        Assert.True(f20.Cards.First(c => c.InstanceId == p2WithCd.InstanceId).SlowMs > 0);
        Assert.Equal(0, f20.Cards.First(c => c.InstanceId == noCdCard.InstanceId).HasteMs);

        // #24 Flying 减半受控且不减半 Haste（t=0 快照已扣首跳 20ms：1000->980, 4000->3980）
        var f0 = res.Timeline.First(t => t.TimeMs == 0);
        var flyFrame0 = f0.Cards.First(c => c.InstanceId == flyingCard.InstanceId);
        Assert.True(flyFrame0.IsFlying);
        Assert.Equal(980, flyFrame0.SlowMs);   // 2000 * 0.5 - 20
        Assert.Equal(980, flyFrame0.FreezeMs); // 2000 * 0.5 - 20
        Assert.Equal(3980, flyFrame0.HasteMs); // 自身 2000 + 随机优先命中 2000，不减半 (4000 - 20)

        // #23 补充验证官方 BazaarGameShared 回落桶：当随机 Haste/Slow 仅剩无 CD 合法目标且模板未限定 CooldownMax>0 时，按 Priorities 第二优先级允许回落命中
        var fbP1 = new PlayerState("fb_p1", "FB1", "Stelle", initialHealthMax: 5000, unlockedHandSlots: 10);
        var fbP2 = new PlayerState("fb_p2", "FB2", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var fbNoCdSelf = CreateCustomItem("fb_nocd_self", "FbNoCdSelf", attrs: new() { ["DamageAmount"] = 1 });
        var fbNoCdOpp = CreateCustomItem("fb_nocd_opp", "FbNoCdOpp", attrs: new() { ["DamageAmount"] = 1 });
        var fbApplier = CreateCustomItem("fb_app", "FbApp", attrs: new() { ["HasteAmount"] = 1000, ["SlowAmount"] = 1000, ["HasteTargets"] = 1, ["SlowTargets"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardHaste","Target":{"$type":"TTargetCardRandom","TargetSection":"SelfHand","ExcludeSelf":true}},{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand"}}]}""")
            }
        });
        Assert.True(fbP1.Hand.TryPlaceOrShift(fbNoCdSelf, 0));
        Assert.True(fbP1.Hand.TryPlaceOrShift(fbApplier, 1));
        Assert.True(fbP2.Hand.TryPlaceOrShift(fbNoCdOpp, 0));
        var fbRes = CombatSimulator.Simulate(Repo.Value, fbP1, fbP2, seed: 42, recordTimeline: true, recordEvents: true);
        var fbF0 = fbRes.Timeline.First(t => t.TimeMs == 0);
        // 无 CD 卡本身不参与 AdvanceSideCards 冷却推进，因此 t=0 时 HasteMs/SlowMs 保持全额 1000ms
        Assert.Equal(1000, fbF0.Cards.First(c => c.InstanceId == fbNoCdSelf.InstanceId).HasteMs);
        Assert.Equal(1000, fbF0.Cards.First(c => c.InstanceId == fbNoCdOpp.InstanceId).SlowMs);
    }

    [Fact]
    public void Q25_Crit2xBase_And_DoubleCritDamage3xOnCutlassAndDeadlyBottledLightning()
    {
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 10000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 10000, unlockedHandSlots: 10);

        // 1) 真实 Cutlass (Damage=10, Multicast=2, 内置 Aura DamageCrit+100 -> 3x 暴伤=30/击)
        var cutlass = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);
        cutlass.ModifyAttribute("CritChance", "Add", 100, DurationTarget.Permanent);

        // 2) 真实 Deadly Bottled Lightning (Damage=15, Burn=1, CritChance=100, Deadly Aura DamageCrit+100 -> 伤害3x=45, Burn2x=2)
        var bottledDeadly = CreateRealCard("df87dc00-f0fe-4e47-b864-ad6b4bfea81a", CardTier.Bronze, enchantment: "Deadly");

        // 3) 常规 100% Crit Shield/Heal/Poison/Regen 卡 -> 全部 2x
        var standardCrit = CreateCustomItem("std_crit", "StdCrit", attrs: new() { ["CooldownMax"] = 2000, ["AmmoMax"] = 1, ["CritChance"] = 100, ["ShieldApplyAmount"] = 10, ["HealAmount"] = 10, ["PoisonApplyAmount"] = 3, ["RegenApplyAmount"] = 4 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},{"$type":"TActionPlayerRegenApply","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}]}""")
            }
        });

        Assert.True(p1.Hand.TryAddAuto(cutlass));
        Assert.True(p1.Hand.TryAddAuto(bottledDeadly));
        Assert.True(p1.Hand.TryAddAuto(standardCrit));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        var cutlassDmgs = res.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == cutlass.InstanceId && e.TimeMs <= 4500).ToList();
        Assert.True(cutlassDmgs.Count >= 2);
        Assert.All(cutlassDmgs, d => { Assert.True(d.IsCrit); Assert.Equal(30, d.Value); });

        var bottledDmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == bottledDeadly.InstanceId);
        var bottledBurn = res.Events.First(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == bottledDeadly.InstanceId);
        Assert.True(bottledDmg.IsCrit);
        Assert.Equal(45, bottledDmg.Value);  // 15 * 3x
        Assert.Equal(2, bottledBurn.Value);  // 1 * 2x

        Assert.Equal(20, res.Events.First(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == standardCrit.InstanceId).Value);
        Assert.Equal(20, res.Events.First(e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == standardCrit.InstanceId && e.Detail != "RegenGain").Value);
        Assert.Equal(6, res.Events.First(e => e.Type == CombatEventType.PoisonApplied && e.SourceCardInstanceId == standardCrit.InstanceId).Value);
        Assert.Equal(8, res.Events.First(e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == standardCrit.InstanceId && e.Detail == "RegenGain").Value);
    }

    [Fact]
    public void Q26_Rage100Enraged_FallbackAutoInject_CleansesSlowFreezeAndReducesAllCooldowns10Percent()
    {
        // 不手动调用 InjectOfficialPlayerEffects，直接创建新 PlayerState 模拟开战以验证引擎兜底挂接 Base Rage Effect
        var p1 = new PlayerState("p1", "P1", "Karnok", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);

        var weapon = CreateCustomItem("enrage_w", "EnrageWeapon", attrs: new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        // 开场先让敌人精准对 weapon（不冻结 rageBooster）挂 Slow+Freeze，t=980ms 己方获得 +100 Rage 触发狂暴
        var enemyCtrl = CreateCustomItem("en_ctrl", "EnemyCtrl", attrs: new() { ["SlowAmount"] = 4000, ["FreezeAmount"] = 4000 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalId","Id":"enrage_w","IsNot":false}}},{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalId","Id":"enrage_w","IsNot":false}}}]}""")
            }
        });
        var rageBooster = CreateCustomItem("rage_up", "RageUp", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["RageApplyAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerRageApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });

        Assert.True(p1.Hand.TryAddAuto(weapon));
        Assert.True(p1.Hand.TryAddAuto(rageBooster));
        Assert.True(p2.Hand.TryAddAuto(enemyCtrl));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        Assert.Contains(res.Events, e => e.Type == CombatEventType.PlayerEnraged && e.TargetPlayerId == p1.PlayerId && e.TimeMs == 980);
        var f980 = res.Timeline.First(t => t.TimeMs == 980);
        Assert.True(f980.P1Enraged);
        var wFrame = f980.Cards.First(c => c.InstanceId == weapon.InstanceId);
        // Enraged 清零己方全场 Slow/Freeze 且全场武器 CD -10% (4000 -> 3600)
        Assert.Equal(0, wFrame.SlowMs);
        Assert.Equal(0, wFrame.FreezeMs);
        Assert.Equal(3600, wFrame.CooldownMaxMs);
    }

    [Fact]
    public void Q27_Sandstorm_20sWarning30sStart_And_LeatherJacketProtection()
    {
        var p1 = new PlayerState("p1", "P1", "Karnok", initialHealthMax: 1500, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Pygmalien", initialHealthMax: 1500, unlockedHandSlots: 10);

        // 真实 Gold Leather Jacket (开战提供 30% PercentDamageReduction；30s 沙暴首跳触发自毁并 Haste+100% Crit 友军)
        var jacket = CreateRealCard("8619fc8b-ceec-4be6-9289-8c7b6f05a631", CardTier.Gold);
        var ally = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);
        // 把 Cutlass 设为只放 0 伤害或给个非常慢的有 CD 卡观察 CritChance 飙升至 100% 且沙暴自毁时机正确
        var slowClock = CreateCustomItem("slow_obs", "SlowObs", attrs: new() { ["CooldownMax"] = 60000 });
        Assert.True(p1.Hand.TryAddAuto(jacket));
        Assert.True(p1.Hand.TryAddAuto(slowClock));
        Assert.NotNull(ally); // 仅确保 Cutlass 正常加载

        // 同时让对手于 t=10000 对我方造成 100 伤害以验证开暴前 Leather Jacket 30% 减伤 -> 实扣 70
        Assert.True(p2.Hand.TryAddAuto(CreateCustomItem("test_hit", "TestHit", attrs: new() { ["CooldownMax"] = 10000, ["AmmoMax"] = 1, ["DamageAmount"] = 100 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        })));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        // 开暴前 t=9980 的 100 伤被 Gold Leather Jacket 30% 减伤至 70
        var hit10s = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.TimeMs == 9980);
        Assert.Equal(70, hit10s.Value);

        // Sandstorm DB 真值：20000ms 预警，30000ms 启动首跳 1 伤，随后每跳 +2 递增 (1, 3, 5, 7...)
        var warn = res.Events.First(e => e.Type == CombatEventType.SandstormWarning);
        Assert.InRange(warn.TimeMs, 19980, 20000);
        var ticks = res.Events.Where(e => e.Type == CombatEventType.SandstormTick).Take(4).ToList();
        Assert.Equal(4, ticks.Count);
        Assert.InRange(ticks[0].TimeMs, 29980, 30000);
        Assert.Equal(1, ticks[0].Value);
        Assert.Equal(3, ticks[1].Value);
        Assert.Equal(5, ticks[2].Value);
        Assert.Equal(7, ticks[3].Value);
        Assert.True(ticks[1].TimeMs - ticks[0].TimeMs <= 260 && ticks[2].TimeMs - ticks[1].TimeMs <= ticks[1].TimeMs - ticks[0].TimeMs);

        // Leather Jacket 收到沙暴自毁并给友军施加 Haste + +100% CritChance
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == jacket.InstanceId && e.TimeMs >= 29980 && e.TimeMs <= 30000);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.HasteApplied && e.TargetCardInstanceId == slowClock.InstanceId && e.TimeMs >= 29980 && e.TimeMs <= 30000);
    }

    // =========================================================================
    // 模块三：AllowWhenDestroyed、Absorb、Antimatter Chamber、Enchant/Transform、Sizeinator、Notes、Quest、Obsidian、Value
    // =========================================================================

    [Fact]
    public void Q28_Q29_Q30_AbsorbOnlyEnemyNotSelfDestroy_RadiantAntimatterChamberStillSelfDestroys_LightningRodAbsorbsControl()
    {
        // Case A: Chaff 拦截敌方 Destroy，Lightning Rod 拦截敌方 Freeze/Slow
        var p1a = new PlayerState("p1", "P1", "Stelle", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2a = new PlayerState("p2", "P2", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);

        var chaff = CreateRealCard("a5e0c248-624f-41dc-a33d-df8761f65642", CardTier.Bronze);
        var rod = CreateRealCard("0930fbea-e3ae-45d4-a1c7-a26c5abb63aa", CardTier.Gold); // 无 CD, AbsorbSlow + AbsorbFreeze
        var allyCutlass = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);
        Assert.True(p1a.Hand.TryPlaceOrShift(chaff, 0));
        Assert.True(p1a.Hand.TryPlaceOrShift(rod, 1));
        Assert.True(p1a.Hand.TryPlaceOrShift(allyCutlass, 4));

        // 敌方依次对 P1 打出单目标 Destroy（指定 Cutlass，排除 Property 标签的 Lightning Rod）、Slow（指定 Cutlass）、Freeze（指定 Cutlass）
        var enemyDisruptor = CreateCustomItem("disruptor", "Disruptor", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["SlowAmount"] = 2000, ["FreezeAmount"] = 2000 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardDestroy","TargetCount":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalTag","Tags":["Property"],"Operator":"None"}}},{"$type":"TActionCardSlow","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalSize","Sizes":["Medium"]}}},{"$type":"TActionCardFreeze","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalSize","Sizes":["Medium"]}}}]}""")
            }
        });
        Assert.True(p2a.Hand.TryAddAuto(enemyDisruptor));

        var resA = CombatSimulator.Simulate(Repo.Value, p1a, p2a, seed: 42, recordTimeline: true, recordEvents: true);
        var f1000A = resA.Timeline.First(t => t.TimeMs == 1000);
        // 敌方 Destroy 试图毁 Weapon（这里选到 allyCutlass / rod 均为 Weapon）：必须被 Chaff 吸收牺牲，allyCutlass 完好存活！
        Assert.Contains(resA.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == chaff.InstanceId);
        Assert.False(f1000A.Cards.First(c => c.InstanceId == allyCutlass.InstanceId).IsDestroyed);
        // Slow 与 Freeze 必须被 Lightning Rod 吸收，allyCutlass 全程保持 SlowMs==0 & FreezeMs==0，同时 Lightning Rod 自身承接敌方控场时长
        var cutlassF1000 = f1000A.Cards.First(c => c.InstanceId == allyCutlass.InstanceId);
        var rodF1000 = f1000A.Cards.First(c => c.InstanceId == rod.InstanceId);
        Assert.Equal(0, cutlassF1000.SlowMs);
        Assert.Equal(0, cutlassF1000.FreezeMs);
        Assert.True(rodF1000.SlowMs > 0);
        Assert.True(rodF1000.FreezeMs > 0);

        // Case B: Radiant Antimatter Chamber + 己方 Chaff 同场：自毁绝不被己方 Chaff 吸收，且 Radiant 不免疫自毁（因 DB Radiant 无 DestroyImmunity）
        var p1b = new PlayerState("p1", "P1", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2b = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var antimatter = CreateRealCard("405245d8-b81d-4015-8ed9-70e80f5eb980", CardTier.Gold, enchantment: "Radiant");
        var myChaff = CreateRealCard("a5e0c248-624f-41dc-a33d-df8761f65642", CardTier.Bronze);
        // 加速 Antimatter Chamber：配一个 1s CD 的 Tech 卡不停充能 (Charge 2s/次 -> ~7s 自毁爆敌)
        var fastTech = CreateCustomItem("fast_tech", "FastTech", attrs: new() { ["CooldownMax"] = 1000 }, tags: new() { "Tech" });
        var enemyVictim = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);
        Assert.True(p1b.Hand.TryPlaceOrShift(antimatter, 0));
        Assert.True(p1b.Hand.TryPlaceOrShift(myChaff, 3));
        Assert.True(p1b.Hand.TryPlaceOrShift(fastTech, 4));
        Assert.True(p2b.Hand.TryPlaceOrShift(enemyVictim, 0));

        var resB = CombatSimulator.Simulate(Repo.Value, p1b, p2b, seed: 42, recordTimeline: true, recordEvents: true);
        // Antimatter Chamber 必须成功自毁且遗言先毁敌方物品，同时 myChaff 不得被自毁误吸收（IsDestroyed==false）
        Assert.Contains(resB.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == antimatter.InstanceId);
        Assert.Contains(resB.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == enemyVictim.InstanceId);
        Assert.DoesNotContain(resB.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == myChaff.InstanceId);
    }

    [Fact]
    public void Q31_Q32_Q33_SizeinatorUnenchantable_DragonsBreathSelfFiery_3DPrinterAndHacksawTempTransformRestore()
    {
        // #31 Sizeinator 没有附魔表，TActionCardEnchant 不可对其强加附魔；Bottled Lightning 允许附魔
        var p = new PlayerState("p1", "P1", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var sizeinator = CreateRealCard("7b30db1a-80c8-4889-ae2f-12b0ad047796", CardTier.Legendary);
        var bottled = CreateRealCard("df87dc00-f0fe-4e47-b864-ad6b4bfea81a", CardTier.Bronze);
        Assert.True(p.Hand.TryAddAuto(sizeinator));
        Assert.True(p.Hand.TryAddAuto(bottled));

        var ctx = new RuleExecutionContext(Repo.Value, RuleScope.OutOfCombat, p, null, new Random(42));
        AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionCardEnchant","Enchantment":"Heavy","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}"""), sizeinator, ctx);
        Assert.Null(sizeinator.Enchantment);
        Assert.Equal("Heavy", bottled.Enchantment);

        // #32 Dragon's Breath 携带 Transform.Abilities.t1 (自附魔 Fiery)
        var breath = CreateRealCard("5a94a820-1b13-4164-bae9-f6abbd59eb2d", CardTier.Bronze);
        var pBreath = new PlayerState("pb", "PB", "Mak", initialHealthMax: 2000, unlockedHandSlots: 10);
        Assert.True(pBreath.Hand.TryAddAuto(breath));
        var ctxBreath = new RuleExecutionContext(Repo.Value, RuleScope.OutOfCombat, pBreath, null, new Random(42)) { SelfCard = breath };
        Assert.NotNull(breath.Template.Transform?.Abilities);
        foreach (var ab in breath.Template.Transform!.Abilities!.Values)
        {
            AbilityExecutor.ExecuteAction(ab.Action, breath, ctxBreath);
        }
        Assert.Equal("Fiery", breath.Enchantment);

        // #33 3D Printer (1 Large -> 3 Small) 原地邻接填充 3 槽 + Hacksaw (1 Medium -> 2 Small) + Virus，战后全部无损还原
        var p1c = new PlayerState("p1", "P1", "Dooley", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2c = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);

        var leftSmall = CreateRealCard("d66bcc8c-a817-4b0c-bda1-eb13b28acb2b", CardTier.Bronze); // Claws (Small, slot 0)
        var printer = CreateRealCard("723880db-8a97-4833-a9a1-80c75cde4c17", CardTier.Diamond);  // 3D Printer (Large, slots 1..3, CD=5000)
        var hacksaw = CreateRealCard("d50bb808-14f8-4554-950d-d0630899ba85", CardTier.Gold);     // Hacksaw (Medium, slots 4..5, CD=8000)
        Assert.True(p1c.Hand.TryPlaceOrShift(leftSmall, 0));
        Assert.True(p1c.Hand.TryPlaceOrShift(printer, 1));
        Assert.True(p1c.Hand.TryPlaceOrShift(hacksaw, 4));

        var enemyMed = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze); // Cutlass (Medium, slots 2..3)
        var enemyLeg = CreateRealCard("021639c0-4a9c-49d8-8254-6376994823e2", CardTier.Legendary); // Depleted Device (Legendary, slots 4..5, 禁止被变形)
        Assert.True(p2c.Hand.TryPlaceOrShift(enemyMed, 2));
        Assert.True(p2c.Hand.TryPlaceOrShift(enemyLeg, 4));

        var resC = CombatSimulator.Simulate(Repo.Value, p1c, p2c, seed: 42, recordTimeline: true, recordEvents: true);

        // 验证 3D Printer 在 t=4980 变形为 3 张 Claws，且占据原地槽位 [1, 2, 3]
        var p1Trans = resC.Events.Where(e => e.Type == CombatEventType.CardTransformed && e.SourcePlayerId == p1c.PlayerId && e.TimeMs == 4980).ToList();
        Assert.Equal(3, p1Trans.Count);
        var f5000 = resC.Timeline.First(t => t.TimeMs == 5000);
        // 原始 printer 已不在战中序列，而多了 3 张临时小卡（与 leftSmall 合计 P1 场上 5 张：1 Claws + 3 临时 Claws + 1 Hacksaw = 5）
        Assert.DoesNotContain(f5000.Cards, c => c.InstanceId == printer.InstanceId);

        // 验证 Hacksaw 于 t=7980 将 enemyMed (2格) 变成 2 张 Small 卡（enemyLeg 为 Legendary 豁免，被变形卡属 p2c 故查 TargetPlayerId）
        var p2Trans = resC.Events.Where(e => e.Type == CombatEventType.CardTransformed && e.TargetPlayerId == p2c.PlayerId && e.TimeMs == 7980).ToList();
        Assert.Equal(2, p2Trans.Count);

        // 战后双方原始手牌完整无损还原！
        Assert.Contains(p1c.Hand.Cards, c => c.InstanceId == printer.InstanceId && c.Template.Id == printer.Template.Id);
        Assert.Equal(3, p1c.Hand.Cards.Count);
        Assert.Contains(p2c.Hand.Cards, c => c.InstanceId == enemyMed.InstanceId && c.Template.Id == enemyMed.Template.Id);
        Assert.Contains(p2c.Hand.Cards, c => c.InstanceId == enemyLeg.InstanceId);
    }

    [Fact]
    public void Q34_Q35_SizeinatorSellShrinksLeftmostNonTinyMedOrLarge_And_DepletedDeviceUnsellable()
    {
        var p = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 500, initialGold: 10, unlockedHandSlots: 10);
        var smallCard = CreateRealCard("d66bcc8c-a817-4b0c-bda1-eb13b28acb2b", CardTier.Bronze); // Small, slot 0
        var medAlreadyTiny = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);
        medAlreadyTiny.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);           // Tiny=1, width 1, slot 1
        var medValidLeft = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Silver); // Medium, slots 2..3 (应被选中)
        var largeRight = CreateRealCard("fc01f3c3-5562-4616-aa12-005834d6647f", CardTier.Gold);     // Skyscraper Large, slots 4..6
        var sizeinator = CreateRealCard("7b30db1a-80c8-4889-ae2f-12b0ad047796", CardTier.Legendary); // Medium, slots 7..8

        Assert.True(p.Hand.TryPlaceOrShift(smallCard, 0));
        Assert.True(p.Hand.TryPlaceOrShift(medAlreadyTiny, 1));
        Assert.True(p.Hand.TryPlaceOrShift(medValidLeft, 2));
        Assert.True(p.Hand.TryPlaceOrShift(largeRight, 4));
        Assert.True(p.Hand.TryPlaceOrShift(sizeinator, 7));

        OutOfCombatController.OnCardSoldOrConsumed(p, sizeinator, Repo.Value, new Random(42));
        Assert.False(smallCard.IsTiny);
        Assert.True(medValidLeft.IsTiny);
        Assert.Equal(1, medValidLeft.SlotWidth);
        Assert.False(largeRight.IsTiny);
        Assert.DoesNotContain(sizeinator, p.Hand.Cards);

        // Depleted Device 严禁出售/销毁
        var depleted = CreateRealCard("021639c0-4a9c-49d8-8254-6376994823e2", CardTier.Legendary);
        Assert.True(p.Hand.TryAddAuto(depleted));
        int goldBefore = p.Gold;
        var sellRes = OutOfCombatController.OnCardSoldOrConsumed(p, depleted, Repo.Value, new Random(42));
        Assert.Equal(goldBefore, p.Gold);
        Assert.Contains(depleted, p.Hand.Cards);
        Assert.Empty(sellRes.RemovedCards);
    }

    [Fact]
    public void Q36_Q37_Tempo2000msPeriod_And_MultiSlotCardOccupyingMultipleNotes()
    {
        var p1 = new PlayerState("p1", "P1", "Hero8", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);

        // 装备两枚 Gold [G Note] Socket Effect (Slot 0 与 Slot 1，各 -10% CDR 且带 G Minor Diamond +3 Tempo)
        var gSocket0 = CreateRealCard("681de13d-5388-4638-b2e7-20bc566fdda1", CardTier.Gold);
        gSocket0.StartSlot = 0;
        var gSocket1 = CreateRealCard("681de13d-5388-4638-b2e7-20bc566fdda1", CardTier.Gold);
        gSocket1.StartSlot = 1;
        p1.SocketEffects.Add(gSocket0);
        p1.SocketEffects.Add(gSocket1);

        var gMinor = CreateRealCard("d47bc1fe-8c4b-4a87-86d8-fbbfdcf27df4", CardTier.Diamond);
        p1.Skills.Add(gMinor);

        // 一张横跨 Slot 0..2 的 3 格 Large Tempo 武器 (CooldownMax=10000 -> 双插座各 -10% CDR = 8000ms；激活一次从两个插座各获 +3 Tempo = +6 Tempo)
        var multiSlotTempoCard = CreateCustomItem(
            "large_tempo", "LargeTempoItem",
            size: CardSize.Large,
            attrs: new() { ["CooldownMax"] = 10000, ["DamageAmount"] = 10 },
            tags: new() { "Weapon" },
            hiddenTags: new() { "Tempo" },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
            });
        Assert.True(p1.Hand.TryPlaceOrShift(multiSlotTempoCard, 0));

        // 同时附带开局给出 Tempo=4，验证 DB Base Tempo Gain Cooldown Effect 的 2000ms 自然周期
        Assert.True(p1.Hand.TryAddAuto(CreateCustomItem("tempo_init", "TempoInit", abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerModifyAttribute","AttributeType":"Tempo","Operation":"Add","Value":{"$type":"TFixedValue","Value":4},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));

        var res = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, recordTimeline: true, recordEvents: true);

        // DB Base Tempo 周期 = 2000ms (每满 100 帧即 t=1980ms / t=3980ms 各 +1 Tempo，非旧资料误传 1.0s/1.05s)
        var t1960 = res.Timeline.First(t => t.TimeMs == 1960).P1Tempo;
        var t1980 = res.Timeline.First(t => t.TimeMs == 1980).P1Tempo;
        var t3960 = res.Timeline.First(t => t.TimeMs == 3960).P1Tempo;
        var t3980 = res.Timeline.First(t => t.TimeMs == 3980).P1Tempo;
        Assert.Equal(4, t1960);
        Assert.Equal(5, t1980);
        Assert.Equal(5, t3960);
        Assert.Equal(6, t3980);

        // 双 [G Note] 光环累加 -20% CDR：10000ms -> 8000ms (第 400 帧 7980ms) 发射，且单次使用从占据的 2 个插座同时各触发 +3 Tempo（共 +6）
        var itemFires = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == multiSlotTempoCard.InstanceId).Select(e => e.TimeMs).ToList();
        Assert.Contains(7980, itemFires);
        var beforeFire = res.Timeline.First(t => t.TimeMs == 7960).P1Tempo;
        var atFire = res.Timeline.First(t => t.TimeMs == 7980).P1Tempo;
        Assert.True(atFire - beforeFire >= 6, $"多格卡跨两个 G Note 插座应叠加触发 2 次 G Minor (+6 Tempo)，实际增量={atFire - beforeFire}");

        // 卖出 Obsidian G Note 应生成 G Note 插座与 Obsidian 小物品
        var pNote = new PlayerState("pn", "PN", "Hero8", initialHealthMax: 500, unlockedHandSlots: 10);
        var gNote = CreateRealCard("16c2083c-27a5-44eb-968b-e81a18dfbefb", CardTier.Gold, enchantment: "Obsidian");
        Assert.True(pNote.Hand.TryAddAuto(gNote));
        var sellNoteRes = OutOfCombatController.OnCardSoldOrConsumed(pNote, gNote, Repo.Value, new Random(42));
        Assert.Contains(sellNoteRes.SpawnedCards, s => s.Card.Template.Id == "681de13d-5388-4638-b2e7-20bc566fdda1");
        Assert.Contains(sellNoteRes.SpawnedCards, s => s.Card.Template.Type == "Item" && s.Card.Template.CardSize == CardSize.Small && s.Card.Enchantment == "Obsidian");
    }

    [Fact]
    public void Q38_Q39_Q40_QuestsConcurrencyAndOrExclusion_ObsidianDynamicWeaponTag_CombatValueAndGoldenTriple()
    {
        // 1) #38 任务并发 (Blank Slate)、互斥 OR (Cauterizing Blade)、双方 AllHands (Darkstone Engine)
        var p1 = new PlayerState("p1", "P1", "Mak", initialHealthMax: 5000, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        var blankSlate = CreateRealCard("4a5b6c7d-8e9f-0a1b-2c3d-4e5f6a7b8c9d", CardTier.Bronze);
        var cautBlade = CreateRealCard("61981752-7071-48fe-b7be-96a3e1c4653b", CardTier.Silver);
        var darkstone = CreateRealCard("a8972bcd-6002-416e-8af2-944383898c30", CardTier.Diamond);
        var dummyWeaponP2 = CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze);

        Assert.True(p1.Hand.TryAddAuto(blankSlate));
        Assert.True(p1.Hand.TryAddAuto(cautBlade));
        Assert.True(p1.Hand.TryAddAuto(darkstone));
        Assert.True(p2.Hand.TryAddAuto(dummyWeaponP2));

        var qCtx = new RuleExecutionContext(Repo.Value, RuleScope.InCombat, p1, p2, new Random(42));
        // 并发推进 Blank Slate 的 Poison (Quest_1) 与 Burn (Quest_2? 等): 每次各推进一次，均独立涨进度
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardPerformedPoison", p1, p1.Hand.Cards[0], null, p1, null, 0, 0, "PVP", "", qCtx);
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardPerformedBurn", p1, p1.Hand.Cards[0], null, p1, null, 0, 0, "PVP", "", qCtx);
        Assert.True(blankSlate.QuestProgress.GetValueOrDefault("Quest_1") >= 1);
        Assert.True(blankSlate.QuestProgress.Count >= 2);

        // Cauterizing Blade: 触发 10 次 Slow 完成 Quest_1 后，后续 10 次 Haste 不得解锁互斥分支 Quest_2
        for (int i = 0; i < 10; i++)
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardPerformedSlow", p1, p1.Hand.Cards[0], null, p1, null, 0, 0, "PVP", "", qCtx);
        Assert.Contains("Quest_1", cautBlade.CompletedQuests);
        for (int i = 0; i < 10; i++)
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardPerformedHaste", p1, p1.Hand.Cards[0], null, p1, null, 0, 0, "PVP", "", qCtx);
        Assert.DoesNotContain("Quest_2", cautBlade.CompletedQuests);

        // Darkstone Engine: 对手武器触发 TTriggerOnItemUsed 也应推进 Quest_1 (AllHands)
        AbilityExecutor.TryAdvanceQuests("TTriggerOnItemUsed", p1, dummyWeaponP2, null, p2, null, 0, 0, "PVP", "", qCtx);
        Assert.Equal(1, darkstone.QuestProgress.GetValueOrDefault("Quest_1"));

        // 2) #39 Obsidian 动态加/卸 Weapon 标签 (Dragon Statue 基础 Tags=[Dragon, Property]，无 Weapon)
        var statue = CreateRealCard("0152f1f7-1efd-40de-8de3-fdf936d793ed", CardTier.Bronze);
        Assert.False(statue.HasTag("Weapon"));
        statue.ApplyEnchantment("Obsidian");
        Assert.True(statue.HasTag("Weapon"));
        statue.ApplyEnchantment("Heavy"); // 附魔覆盖
        Assert.False(statue.HasTag("Weapon"));

        // 3) #40 战内价值翻倍 + Golden 改为 3x instead，战后无污染 (Spacescraper Gold: Base Custom_2=2 -> 2x; Golden Custom_2+=1 -> 3x; 战内 Shield=Value*2)
        var spNormal = CreateRealCard("40d8b44a-8e2f-4081-8163-043d76ddbafa", CardTier.Gold);
        var spGolden = CreateRealCard("40d8b44a-8e2f-4081-8163-043d76ddbafa", CardTier.Gold, enchantment: "Golden");
        double outValueNormal = spNormal.GetAttribute("SellPrice");
        double outValueGolden = spGolden.GetAttribute("SellPrice");
        Assert.True(outValueNormal > 0);
        // Golden Spacescraper 不再直接改永久 SellPrice（其 Aura 修改的是 Custom_2 战时倍率）
        Assert.Equal(outValueNormal, outValueGolden);

        var pGold = new PlayerState("pg", "PG", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);
        var pDummy = new PlayerState("pd", "PD", "Vanessa", initialHealthMax: 5000, unlockedHandSlots: 10);
        Assert.True(pGold.Hand.TryAddAuto(spGolden));
        var resGold = CombatSimulator.Simulate(Repo.Value, pGold, pDummy, seed: 42, recordTimeline: false, recordEvents: true);
        // 战内 Golden Spacescraper 价值变为 3 * outValueGolden，首次 t=5000ms 给盾 = 2 * (3 * outValueGolden)
        var shieldEvt = resGold.Events.First(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == spGolden.InstanceId);
        Assert.Equal(outValueGolden * 3 * 2, shieldEvt.Value);
        // 战后不污染局外 SellPrice
        Assert.Equal(outValueGolden, spGolden.GetAttribute("SellPrice"));
    }

    [Fact]
    public void Q42_SelfChargingFlyingHydraulicSqueezerTerminatesFast_And_ExtendedSandstormAwardsDualWinAtCap()
    {
        // 1) 防自充能/同帧死循环回归：真实 Hydraulic Squeezer (Flying=1, FlatCDR=3000 -> 1000ms CD, Charge=1000ms ExcludeSelf=false)
        // 搭配 Tools of the Trade + Give it a Whack + Recycler Bot + Parts Picker + Flycycle 必须在极短耗时内收敛完毕
        var p1 = new PlayerState("p1", "P1", "Vanessa", initialHealthMax: 2500, unlockedHandSlots: 10);
        var p2 = new PlayerState("p2", "P2", "Stelle", initialHealthMax: 1825, unlockedHandSlots: 10);
        p1.Hand.TryAddAuto(CreateRealCard("1b955b8d-0ea6-4437-935c-d588be5898c1", CardTier.Bronze)); // Cutlass

        var squeezer = CreateRealCard("3866978f-da19-45a8-9f28-d03b6d3aafc4", CardTier.Gold);
        squeezer.PermanentAdd["FlatCooldownReduction"] = 3000; // 压到 1000ms CD 下限
        squeezer.CombatAdd["Flying"] = 1;
        var flycycle = CreateRealCard("fa5a2ad2-48f2-4e55-b3ef-e3e708483946", CardTier.Bronze);
        var partsPicker = CreateRealCard("c47744c9-d3bd-42ba-ae49-5fbff2f5d785", CardTier.Diamond);
        var recyclerBot = CreateRealCard("12a26afa-0ff6-4aa2-a964-62a4e164ad22", CardTier.Bronze);
        var toolsOfTrade = CreateRealCard("6c3d41e4-1ca7-4be5-b0e2-6cadbbd2bd27", CardTier.Gold);
        var giveItAWhack = CreateRealCard("32573ba4-0fd4-49fd-b217-d772efbd0745", CardTier.Diamond);

        Assert.True(p2.Hand.TryAddAuto(partsPicker));
        Assert.True(p2.Hand.TryAddAuto(flycycle));
        Assert.True(p2.Hand.TryAddAuto(squeezer));
        Assert.True(p2.Hand.TryAddAuto(recyclerBot));
        p2.Skills.Add(toolsOfTrade);
        p2.Skills.Add(giveItAWhack);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sqRes = CombatSimulator.Simulate(Repo.Value, p1, p2, seed: 42, combatType: "PVP", recordTimeline: true, recordEvents: true);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Hydraulic Squeezer 自充能组合耗时异常偏高: {sw.ElapsedMilliseconds}ms");
        Assert.InRange(sqRes.DurationMs, 1, CombatSimulator.MaxDurationMs);

        // 2) 超 2 分钟（>120_000ms）抗沙尘暴不断流自然打完
        var sP1 = new PlayerState("player", "Player", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);
        var sP2 = new PlayerState("opp", "Opp", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);
        var megaShield1 = CreateCustomItem(
            "mega_sh_1", "MegaShield1",
            attrs: new() { ["CooldownMax"] = 1000, ["ShieldApplyAmount"] = 50000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
            });
        // P2 配 600000 血上限，稳稳扛过 120s 沙尘暴后在 ~145s 附近才被狂暴沙尘暴自然击穿
        var sP2Tank = new PlayerState("opp", "Opp", "Pygmalien", initialHealthMax: 600000, unlockedHandSlots: 10);
        sP1.Hand.TryAddAuto(megaShield1);
        var longRes = CombatSimulator.Simulate(Repo.Value, sP1, sP2Tank, seed: 101, combatType: "PVP", recordTimeline: false, recordEvents: false);
        Assert.True(longRes.DurationMs > 120_000, $"双方抗沙尘暴战斗应允许超过 120s 自然决胜，实际 DurationMs={longRes.DurationMs}");
        Assert.True(longRes.DurationMs < CombatSimulator.MaxDurationMs, $"一方倒地应早于上限结束，实际 DurationMs={longRes.DurationMs}");
        Assert.Equal("player", longRes.WinnerPlayerId);
        Assert.False(longRes.IsDraw);

        // 3) 若双方均扛到 MaxDurationMs (300_000ms) 上限仍存活，互换视角双方均判胜（两边同时胜利）
        var tankA = new PlayerState("player", "PlayerA", "Pygmalien", initialHealthMax: 5000, unlockedHandSlots: 10);
        var tankB = new PlayerState("opp", "PlayerB", "Mak", initialHealthMax: 5000, unlockedHandSlots: 10);
        tankA.Hand.TryAddAuto(CreateCustomItem(
            "immortal_a", "ImmortalA",
            attrs: new() { ["CooldownMax"] = 1000, ["ShieldApplyAmount"] = 50000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
            }));
        tankB.Hand.TryAddAuto(CreateCustomItem(
            "immortal_b", "ImmortalB",
            attrs: new() { ["CooldownMax"] = 1000, ["ShieldApplyAmount"] = 50000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
            }));
        // 刻意让 A 拥有更多血量验证从 B 视角（低血方）同样在到达上限时判胜
        var capB = new PlayerState("opp", "PlayerB", "Mak", initialHealthMax: 1000, unlockedHandSlots: 10);
        capB.Hand.TryAddAuto(tankB.Hand.Cards[0].CloneForCombat());
        var capResA = CombatSimulator.Simulate(Repo.Value, tankA, capB, seed: 77, combatType: "PVP", recordTimeline: false, recordEvents: false);
        var capResB = CombatSimulator.Simulate(Repo.Value, capB, tankA, seed: 77, combatType: "PVP", recordTimeline: false, recordEvents: false);
        Assert.Equal(CombatSimulator.MaxDurationMs, capResA.DurationMs);
        Assert.Equal("player", capResA.WinnerPlayerId);
        Assert.False(capResA.IsDraw);
        Assert.Equal(CombatSimulator.MaxDurationMs, capResB.DurationMs);
        Assert.Equal("opp", capResB.WinnerPlayerId);
        Assert.False(capResB.IsDraw);
    }
}
