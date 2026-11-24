using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1B（BazaarCore 核心战斗数值、生存、无敌、复活与饱和安全）纯 BazaarCore 零静态写回归测试。
/// 不引用 BazaarServer、不修改全局静态态，完全对齐 Wave 0 集合并行规范。
/// </summary>
public sealed class Wave1B_CombatCoreP0Tests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private const string MechaDragonId = "894657f8-ab1e-4acf-b52e-538559f00d37";
    private const string OblivionVortexId = "4f2b2cd3-88db-43e6-ae79-53eb2554b887";
    private const string SparringPartnerSkillId = "fcb51ffd-6d25-4e74-871a-7dc0de2bae91";
    private const string ElixirOfImmortalityId = "fdb8f063-d430-4391-9dc4-79b9f9bda25b";
    private const string HealthyHeartId = "05ec8652-3c5f-4cba-acd1-c3eee8e49d44";
    private const string HardShellId = "050ff26a-59ec-4666-89b0-b0630a75149a";
    private const string SwashBuckleId = "69688b74-fd2b-42a2-a018-2b720a13f95a";

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static PlayerState CreatePlayer(string id, string hero, int hp)
        => new(id, id, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 10);

    private static CardInstance CreateItem(
        string id,
        Dictionary<string, double>? attrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null,
        List<string>? hiddenTags = null,
        CardSize size = CardSize.Small,
        CardTier tier = CardTier.Bronze)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = size.ToString(),
            StartingTier = tier.ToString(),
            Tags = tags ?? [],
            HiddenTags = hiddenTags ?? [],
            Tiers = new Dictionary<string, TierData>
            {
                [tier.ToString()] = new TierData
                {
                    Attributes = attrs != null ? new Dictionary<string, double>(attrs) : new(),
                    AbilityIds = abilities != null ? abilities.Keys.ToList() : [],
                    AuraIds = auras != null ? auras.Keys.ToList() : []
                }
            },
            Abilities = abilities ?? new(),
            Auras = auras ?? new()
        };
        return new CardInstance(tpl, tier, null, id);
    }

    private static CardInstance CreateSkill(
        string id,
        Dictionary<string, double>? attrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        List<string>? hiddenTags = null)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            HiddenTags = hiddenTags ?? [],
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData
                {
                    Attributes = attrs != null ? new Dictionary<string, double>(attrs) : new(),
                    AbilityIds = abilities != null ? abilities.Keys.ToList() : []
                }
            },
            Abilities = abilities ?? new()
        };
        return new CardInstance(tpl, CardTier.Bronze, null, id);
    }

    [Fact]
    public void P0_08_ZeroActionAmount_NeverFallsBackTo1000MsOr1()
    {
        var repo = Repo.Value;
        // 1. 白板 Mecha Dragon（无附魔）：SlowAmount/FreezeAmount/HasteAmount 缺失，不得回退 1000ms 白嫖控场
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var mecha = new CardInstance(repo.GetCard(MechaDragonId)!, CardTier.Silver, null, "mecha_plain");
        var dummyTarget = CreateItem("dummy_cd", attrs: new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 1 });
        var p1Ally = CreateItem("ally_cd", attrs: new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 1 });
        Assert.True(p1.Hand.TryAddAuto(mecha));
        Assert.True(p1.Hand.TryAddAuto(p1Ally));
        Assert.True(p2.Hand.TryAddAuto(dummyTarget));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 801, combatType: "PVP", recordTimeline: false, recordEvents: true);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == mecha.InstanceId);
        // Mecha Dragon 能力 0 会先给自己随机附魔（如 Turbo 合法获得 HasteAmount）；凡未获得对应附魔的 Slow/Freeze/Haste 一律 0ms 不得回退 1000ms
        var rolledEnch = res.Events
            .Where(e => e.Type == CombatEventType.CardEnchanted && e.TargetCardInstanceId == mecha.InstanceId)
            .Select(e => e.Detail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!rolledEnch.Contains("Heavy"))
            Assert.DoesNotContain(res.Events, e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.SlowApplied);
        if (!rolledEnch.Contains("Icy"))
            Assert.DoesNotContain(res.Events, e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.FreezeApplied);
        if (!rolledEnch.Contains("Turbo"))
            Assert.DoesNotContain(res.Events, e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.HasteApplied);
        int controlKindsFired = (res.Events.Any(e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.SlowApplied) ? 1 : 0)
                              + (res.Events.Any(e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.FreezeApplied) ? 1 : 0)
                              + (res.Events.Any(e => e.SourceCardInstanceId == mecha.InstanceId && e.Type == CombatEventType.HasteApplied) ? 1 : 0);
        Assert.True(controlKindsFired <= Math.Max(0, rolledEnch.Count), $"Mecha Dragon 未附魔对应属性却非法触发了 {controlKindsFired} 种控场");

        // 2. Oblivion Vortex (Gold ChargeAmount=0) 与 Jammer 削减 ChargeAmount 至 0：不得回退 1000ms
        var vortex = new CardInstance(repo.GetCard(OblivionVortexId)!, CardTier.Gold, null, "vortex_0");
        var chargerZero = CreateItem(
            "charger_zero",
            attrs: new() { ["CooldownMax"] = 2000, ["ChargeAmount"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardCharge","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
                }
            });
        var receiver = CreateItem("recv_cd", attrs: new() { ["CooldownMax"] = 5000 });
        var pCharge = CreatePlayer("pc", HeroNames.Vanessa, 1000);
        pCharge.Hand.TryAddAuto(chargerZero);
        pCharge.Hand.TryAddAuto(receiver);
        int chargeCalled = 0;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, pCharge, null, new Random(1))
        {
            SelfCard = chargerZero,
            OnChargeApplied = (_, _, _) => chargeCalled++
        };
        AbilityExecutor.ExecuteAction(chargerZero.ActiveAbilities[0].Action, chargerZero, ctx);
        Assert.Equal(0, chargeCalled);
        Assert.Equal(0, vortex.GetAttribute("ChargeAmount"));

        // 3. ReloadAmount == 0 不得强行装填 1 发
        var zeroReloader = CreateItem(
            "zero_reloader",
            attrs: new() { ["ReloadAmount"] = 0, ["AmmoMax"] = 3 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardReload","Target":{"$type":"TTargetCardSelf"}}""")
                }
            });
        zeroReloader.CurrentAmmo = 0;
        var pRel = CreatePlayer("pr", HeroNames.Vanessa, 1000);
        pRel.Hand.TryAddAuto(zeroReloader);
        int performedReload = 0;
        var relCtx = new RuleExecutionContext(repo, RuleScope.InCombat, pRel, null, new Random(1))
        {
            SelfCard = zeroReloader,
            OnDetailedTriggeredEvent = (ev, _, _, _, _, _, _, _) => { if (ev == "TTriggerOnCardPerformedReload") performedReload++; }
        };
        AbilityExecutor.ExecuteAction(zeroReloader.ActiveAbilities[0].Action, zeroReloader, relCtx);
        Assert.Equal(0, zeroReloader.CurrentAmmo);
        Assert.Equal(0, performedReload);
    }

    [Fact]
    public void P0_09_PercentDamageReduction_ProtectsAgainstBurnAndPoison()
    {
        var repo = Repo.Value;
        // 1. 100% 无敌期内 Burn 与 Poison 均造成 0 伤害、不掉盾、不死
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 10);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        p1.ModifyAttribute("PercentDamageReduction", "Add", 100, DurationTarget.Permanent);
        var shieldStarter = CreateItem(
            "p1_shield",
            attrs: new() { ["ShieldApplyAmount"] = 50 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            });
        var dotStarter = CreateItem(
            "p2_dot",
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
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":200},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":200},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                      ]
                    }
                    """)
                }
            });
        p1.Hand.TryAddAuto(shieldStarter);
        p2.Hand.TryAddAuto(dotStarter);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 901, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        var snap1500 = res.Timeline.First(t => t.TimeMs == 1500);
        Assert.Equal(10, snap1500.P1Hp);
        Assert.Equal(50, snap1500.P1Shield);

        // 2. 50% 减伤对 Burn 与 Poison 均生效
        var p1Half = CreatePlayer("p1h", HeroNames.Vanessa, 1000);
        var p2Half = CreatePlayer("p2h", HeroNames.Vanessa, 5000);
        p1Half.ModifyAttribute("PercentDamageReduction", "Add", 50, DurationTarget.Permanent);
        p2Half.Hand.TryAddAuto(CreateItem(
            "p2_poison_only",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":100},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        var resHalf = CombatSimulator.Simulate(repo, p1Half, p2Half, seed: 902, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        var snap1000 = resHalf.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(950, snap1000.P1Hp);
    }

    [Fact]
    public void P0_10_DotAndSandstorm_FlushIcdQueuesBeforeDeathCheck()
    {
        var repo = Repo.Value;
        // 玩家 1000 HP，开局挂 500 Poison -> t=1000ms 单跳降至 500 -> 下跳或同帧被 1000 Burn 打穿至 0 前，
        // 非 Immediate 优先级（High）的 Healthy Heart 必须在 DoT 后、CheckEndOfTickDeath 前出队救活玩家！
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var healthyHeart = new CardInstance(repo.GetCard(HealthyHeartId)!, CardTier.Diamond, null, "hh_save");
        p1.Skills.Add(healthyHeart);
        // 施加 1000 剧毒（单跳直接从满血跨过半血打到 0 HP -> 必须在判死前出队触发 Healthy Heart 救活至 300 HP！）
        p2.Hand.TryAddAuto(CreateItem(
            "p2_lethal_poison",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":1000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));

        // 同侧额外携带 Elixir of Immortality：若 DoT 后未先 ProcessIcdQueues()，旧代码会先误触发 OnPlayerDied 消耗并销毁 Elixir；
        // 修复 P0-10 后 Healthy Heart 必须在 CheckEndOfTickDeath 前出队救活，Elixir 完好无损不被触发！
        var elixir = new CardInstance(repo.GetCard(ElixirOfImmortalityId)!, CardTier.Gold, null, "p10_elixir");
        p1.Hand.TryAddAuto(elixir);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1001, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == healthyHeart.InstanceId && e.TimeMs == 1000);
        Assert.DoesNotContain(res.Events, e => e.SourceCardInstanceId == elixir.InstanceId && e.TimeMs == 1000);
        var snap1000 = res.Timeline.First(t => t.TimeMs == 1000);
        Assert.True(snap1000.P1Hp > 0, $"DoT 跨过半血致死帧 Healthy Heart 应在判死前出队救活玩家，实际 t=1000 HP={snap1000.P1Hp}");
        Assert.False(snap1000.Cards.First(c => c.InstanceId == elixir.InstanceId).IsDestroyed, "Healthy Heart 提前出队救活后绝不应触发销毁 Elixir of Immortality");
    }

    [Fact]
    public void P0_11_OnPlayerDied_MultiRevive_SingleSaveStop_AndSimultaneousDeathEarlyStop()
    {
        var repo = Repo.Value;

        // 1. 真实 Sparring Partner 连续多次血量归零均成功复活（绝不被全局单次布尔锁死）
        var spP1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var spP2 = CreatePlayer("sparring", HeroNames.Common, 100);
        spP2.Skills.Add(new CardInstance(repo.GetCard(SparringPartnerSkillId)!, CardTier.Bronze, null, "sp_skill"));
        spP1.Hand.TryAddAuto(CreateItem(
            "sp_nuker",
            attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 5000 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        var spRes = CombatSimulator.Simulate(repo, spP1, spP2, seed: 1101, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        int spReviveHeals = spRes.Events.Count(e => e.Type == CombatEventType.HealApplied && e.TargetPlayerId == "sparring");
        Assert.True(spReviveHeals >= 3, $"Sparring Partner 应支持多次复活，实际仅复活 {spReviveHeals} 次");

        // 2. 同帧双杀：p2 死后立即早停判 p1 胜，绝不触发并销毁 p1 的 Elixir of Immortality
        var dP1 = CreatePlayer("p1", HeroNames.Vanessa, 100);
        var dP2 = CreatePlayer("p2", HeroNames.Vanessa, 100);
        var elixir = new CardInstance(repo.GetCard(ElixirOfImmortalityId)!, CardTier.Gold, null, "p1_elixir");
        dP1.Hand.TryAddAuto(elixir);
        // 双方开场各挂 150 剧毒，t=1000ms 同帧归零
        dP1.Hand.TryAddAuto(CreateItem(
            "p1_mutual",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":150},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        dP2.Hand.TryAddAuto(CreateItem(
            "p2_mutual",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":150},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        var dualRes = CombatSimulator.Simulate(repo, dP1, dP2, seed: 1102, recordTimeline: false, recordEvents: true, syncPermanentChanges: true);
        Assert.Equal("p1", dualRes.WinnerPlayerId);
        Assert.DoesNotContain(dualRes.Events, e => e.SourceCardInstanceId == elixir.InstanceId && e.Type == CombatEventType.HealApplied);
        Assert.False(elixir.IsDestroyed, "同帧双杀 p1 已胜，不应触发并销毁 Elixir of Immortality");

        // 3. 单侧双复活卡：第 1 次濒死仅消费第 1 张，第 2 张保留至第 2 次濒死
        CardInstance MakeLimitedRevive(string id) => CreateSkill(
            id,
            attrs: new() { ["Custom_0"] = 0 },
            abilities: new()
            {
                ["heal"] = new AbilityDefinition
                {
                    Id = "heal",
                    Trigger = ParseNode("""{"$type":"TTriggerOnPlayerDied","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""),
                    Prerequisites = [ParseNode("""{"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalAttribute","Attribute":"Custom_0","ComparisonOperator":"Equal","ComparisonValue":{"$type":"TFixedValue","Value":0}}},"Comparison":"GreaterThanOrEqual","Amount":1}""")],
                    Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        {"$type":"TActionPlayerHeal","ReferenceValue":{"$type":"TFixedValue","Value":60},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionCardModifyAttribute","AttributeType":"Custom_0","Operation":"Add","Value":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSelf"}}
                      ]
                    }
                    """)
                }
            });

        var mP1 = CreatePlayer("p1", HeroNames.Vanessa, 100);
        var mP2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var revA = MakeLimitedRevive("rev_a");
        var revB = MakeLimitedRevive("rev_b");
        mP1.Skills.Add(revA);
        mP1.Skills.Add(revB);
        mP2.Hand.TryAddAuto(CreateItem(
            "p2_killer",
            attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 120 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            }));
        var mRes = CombatSimulator.Simulate(repo, mP1, mP2, seed: 1103, recordTimeline: false, recordEvents: true);
        var revHeals = mRes.Events.Where(e => e.Type == CombatEventType.HealApplied && e.TargetPlayerId == "p1").ToList();
        Assert.Equal(2, revHeals.Count);
        Assert.NotEqual(revHeals[0].TimeMs, revHeals[1].TimeMs); // 第 1 次与第 2 次濒死分别在不同帧消费两张复活卡
    }

    [Fact]
    public void P0_12_SaturatingAccumulation_AndNaNShield()
    {
        var repo = Repo.Value;
        // 3 次连续 1e9 Burn / Poison / Shield 累加饱和于 1e9，绝不 Int32 溢负成 -1,294,967,296
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        p1.Hand.TryAddAuto(CreateItem(
            "huge_stacker",
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
                        {"$type":"TActionPlayerShieldApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionPlayerShieldApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionPlayerShieldApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":1000000000},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                      ]
                    }
                    """)
                }
            }));
        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1201, recordTimeline: true, recordEvents: true);
        var f0 = res.Timeline.First(t => t.TimeMs == 0);
        Assert.Equal(1_000_000_000, f0.P1Shield);
        Assert.Equal(1_000_000_000, f0.P2Burn);

        // NaN 注入 PlayerState.HealthMax 不得得出 int.MinValue
        var pNan = CreatePlayer("pnan", HeroNames.Vanessa, 500);
        pNan.Attributes["HealthMax"] = double.NaN;
        Assert.True(pNan.HealthMax >= 1);

        // P0-12 兄弟路径：PlayerState.ModifyAttribute("HealthMax") 永久 + 临时大额增益严禁 int32 溢出负血暴毙
        var pHugePerm = CreatePlayer("phuge_perm", HeroNames.Vanessa, 1_000_000);
        pHugePerm.ModifyAttribute("HealthMax", "Multiply", 5000, DurationTarget.Permanent);
        Assert.True(pHugePerm.IsAlive, "大额永久 HealthMax 倍增后玩家必须存活");
        Assert.Equal(pHugePerm.HealthMax, pHugePerm.CurrentHealth);
        Assert.Equal(SafeMath.DefaultCombatMax, pHugePerm.CurrentHealth);

        var pHugeTemp = CreatePlayer("phuge_temp", HeroNames.Vanessa, 800_000_000);
        pHugeTemp.ModifyAttribute("HealthMax", "Add", 1_500_000_000, DurationTarget.Combat);
        Assert.True(pHugeTemp.IsAlive, "大额临时 HealthMax 加成后玩家必须存活且不溢出回绕为负");
        Assert.Equal(pHugeTemp.HealthMax, pHugeTemp.CurrentHealth);
        Assert.Equal(SafeMath.DefaultCombatMax, pHugeTemp.CurrentHealth);

        // CardInstance 弹药与价格在超大有限缩放下同样饱和封顶，不得溢出归 0
        var hugeCard = CreateItem("huge_card", attrs: new() { ["AmmoMax"] = 10, ["BuyPrice"] = 10, ["SellPrice"] = 10 });
        hugeCard.CombatMult["AmmoMax"] = 1e12;
        hugeCard.CombatMult["BuyPrice"] = 1e12;
        hugeCard.CombatMult["SellPrice"] = 1e12;
        Assert.Equal(SafeMath.DefaultCombatMax, hugeCard.EffectiveAmmoMax);
        Assert.Equal(SafeMath.DefaultCombatMax, hugeCard.GetBuyPrice(repo.GameMode.StandardPrices));
        Assert.Equal(SafeMath.DefaultCombatMax, hugeCard.GetSellPrice(repo.GameMode.StandardPrices));
    }

    [Fact]
    public void P1_15_CritScope_BlocksSkillsAndPassives_AndPropagatesDotIsCrit()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        p1.ModifyAttribute("CritChance", "Add", 100, DurationTarget.Permanent);

        // 1. 攻防 Skill（无 CanCrit）在 100% 玩家暴击率下绝不暴击
        var dmgSkill = CreateSkill(
            "skill_opener",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1.Skills.Add(dmgSkill);

        // 2. 物品被动能力（非 CardFired）在 100% 暴击率下绝不暴击
        var passiveItem = CreateItem(
            "passive_burn",
            attrs: new() { ["BurnApplyAmount"] = 20 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1.Hand.TryAddAuto(passiveItem);

        // 3. 主动冷却 Item（TTriggerOnCardFired）施加 Burn/Poison/Regen 正确暴击翻倍且 CombatEvent.IsCrit == true
        var activeDot = CreateItem(
            "active_dot",
            attrs: new() { ["CooldownMax"] = 1000, ["BurnApplyAmount"] = 10, ["PoisonApplyAmount"] = 10, ["RegenApplyAmount"] = 10, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        {"$type":"TActionPlayerBurnApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerRegenApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                      ]
                    }
                    """)
                }
            });
        p1.Hand.TryAddAuto(activeDot);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1501, recordTimeline: false, recordEvents: true);
        var skillDmg = res.Events.First(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == dmgSkill.InstanceId);
        Assert.Equal(50, skillDmg.Value);
        Assert.False(skillDmg.IsCrit);

        var passiveBurn = res.Events.First(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == passiveItem.InstanceId);
        Assert.Equal(20, passiveBurn.Value);
        Assert.False(passiveBurn.IsCrit);

        var actBurn = res.Events.First(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == activeDot.InstanceId);
        var actPoison = res.Events.First(e => e.Type == CombatEventType.PoisonApplied && e.SourceCardInstanceId == activeDot.InstanceId);
        var actRegen = res.Events.First(e => e.Type == CombatEventType.RegenApplied && e.SourceCardInstanceId == activeDot.InstanceId);
        Assert.True(actBurn.IsCrit && actBurn.Value == 20);
        Assert.True(actPoison.IsCrit && actPoison.Value == 20);
        Assert.True(actRegen.IsCrit && actRegen.Value == 20);

        // 4. 显式 CanCrit 隐藏标签卡在单次 TActionAnd 多动作中仅派发 1 次 TTriggerOnCardCritted
        var pDedup = CreatePlayer("pd", HeroNames.Vanessa, 1000);
        var pDedupOpp = CreatePlayer("pdo", HeroNames.Vanessa, 1000);
        pDedup.Attributes["CritChance"] = 100;
        var canCritCard = CreateItem("cancrit_multi", hiddenTags: ["CanCrit"]);
        pDedup.Hand.TryAddAuto(canCritCard);
        int critTriggerCount = 0;
        var dedupCtx = new RuleExecutionContext(repo, RuleScope.InCombat, pDedup, pDedupOpp, new Random(1))
        {
            SelfCard = canCritCard,
            OnDetailedTriggeredEvent = (ev, _, _, _, _, _, _, _) => { if (ev == "TTriggerOnCardCritted") critTriggerCount++; }
        };
        var multiAction = ParseNode("""
        {
          "$type": "TActionAnd",
          "Actions": [
            {"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
            {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
          ]
        }
        """);
        AbilityExecutor.ExecuteAction(multiAction, canCritCard, dedupCtx);
        Assert.Equal(1, critTriggerCount);
    }

    [Fact]
    public void P1_15_ReviveHeal_ClearsOverkill_AndMatchesEventEffectiveRestored()
    {
        var repo = Repo.Value;
        // 对带残余 200 溢伤 + 100 Burn 的 0 HP 玩家执行 TActionPlayerReviveHeal(50)，
        // 验证：1) overkill 先清零使 HP 精确恢复至 50；2) HealApplied.EffectiveHpRestored == 50；3) 红线 1 治疗净化 Floor(50*0.05)=2
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 100);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        // p2 开场先施加 100 Burn 再打 300 直伤（产生 200 溢伤）
        p2.Hand.TryAddAuto(CreateItem(
            "overkill_opener",
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
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":100},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":300},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                      ]
                    }
                    """)
                }
            }));
        // p1 在受伤瞬间直接以 Immediate 能力触发 TActionPlayerReviveHeal(50)（此时尚未进 CheckEndOfTickDeath，p1Overkill == 200！）
        var reviver = CreateSkill(
            "instant_revive_50",
            attrs: new() { ["Custom_0"] = 0 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Priority = "Immediate",
                    Trigger = ParseNode("""{"$type":"TTriggerOnPlayerDamaged","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""),
                    Prerequisites = [ParseNode("""{"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalAttribute","Attribute":"Custom_0","ComparisonOperator":"Equal","ComparisonValue":{"$type":"TFixedValue","Value":0}}},"Comparison":"Equal","Amount":1}""")],
                    Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        {"$type":"TActionPlayerReviveHeal","ReferenceValue":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}},
                        {"$type":"TActionCardModifyAttribute","AttributeType":"Custom_0","Operation":"Add","Value":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardSelf"}}
                      ]
                    }
                    """)
                }
            });
        p1.Skills.Add(reviver);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1502, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        var reviveEv = res.Events.First(e => e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == reviver.InstanceId);
        Assert.Equal(50, reviveEv.Value);
        Assert.Equal(50, reviveEv.EffectiveHpRestored);
        Assert.Equal(50, reviveEv.Player1Health);
        Assert.Equal(98, reviveEv.Player1Burn); // 红线 1：Floor(50 * 0.05) = 2，100 - 2 = 98
    }

    private sealed class NanPoisonInterceptor : ICombatPipelineInterceptor
    {
        public string InterceptorId => "wave1b_nan_test";
        public int Priority => 0;
        public void OnPipelineEvent(CombatPipelineEvent ev, ICombatMutationApi api)
        {
            if (ev.HookName is "BeforeDamage" or "BeforeStatusApply")
            {
                ev.Amount = double.NaN;
                ev.AmountModified = true;
            }
        }
    }

    [Fact]
    public void P0_12_SafeMath_OfficialRound_DurationMs_AndPipelineNaNShield()
    {
        var repo = Repo.Value;
        // 1. SafeMath 直接契约断言
        Assert.Equal(SafeMath.DefaultCombatMax, SafeMath.SaturatingAddInt(900_000_000, 900_000_000));
        Assert.Equal(0, SafeMath.ToSafeInt(double.NaN, 0, SafeMath.DefaultCombatMax));
        Assert.Equal(SafeMath.DefaultCombatMax, SafeMath.ToSafeInt(double.PositiveInfinity, 0, SafeMath.DefaultCombatMax));
        Assert.Equal(0, SafeMath.ToSafeInt(double.NegativeInfinity, 0, SafeMath.DefaultCombatMax));
        Assert.Equal(5.0, SafeMath.ClampFinite(double.NaN, 1.0, 10.0, 5.0));

        // 2. OfficialRound(3e9f) 饱和于 DefaultCombatMax（绝不溢负）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
        var bigNode = ParseNode("""
        {
          "$type": "TRangeValue",
          "MinValue": 3000000000.0,
          "MaxValue": 3000000000.0,
          "Modifier": { "ModifyMode": "Multiply", "Value": { "$type": "TFixedValue", "Value": 1.0 }, "ShouldRound": true }
        }
        """);
        var evalCtx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1));
        double roundedBig = ValueAndConditionEvaluator.EvaluateValue(bigNode, evalCtx);
        Assert.Equal(SafeMath.DefaultCombatMax, roundedBig);

        // 3. ResolveDurationMs(3e9) 饱和于 120_000ms（绝不溢负变 0）
        var hasteTarget = CreateItem("ht", attrs: new() { ["CooldownMax"] = 5000 });
        p1.Hand.TryAddAuto(hasteTarget);
        int capturedHasteMs = -1;
        evalCtx.SelfCard = hasteTarget;
        evalCtx.OnHasteApplied = (_, ms, _) => capturedHasteMs = ms;
        var hugeHaste = ParseNode("""
        {
          "$type": "TActionCardHaste",
          "ReferenceValue": { "$type": "TFixedValue", "Value": 3000000000.0 },
          "Target": { "$type": "TTargetCardSelf" }
        }
        """);
        AbilityExecutor.ExecuteAction(hugeHaste, hasteTarget, evalCtx);
        Assert.Equal(120_000, capturedHasteMs);

        // 4. NaN 注入 HealthMax / CooldownMax 均安全有限
        var nanPlayer = CreatePlayer("nan_p", HeroNames.Vanessa, 500);
        nanPlayer.Attributes["HealthMax"] = double.NaN;
        Assert.Equal(1, nanPlayer.HealthMax);
        var nanCard = CreateItem("nan_c", attrs: new() { ["CooldownMax"] = 2000 });
        nanCard.CombatAdd["CooldownMax"] = double.NaN;
        Assert.True(double.IsFinite(nanCard.GetAttribute("CooldownMax")) && nanCard.GetAttribute("CooldownMax") >= 0);

        // 5. RunBeforeDamageHook / RunBeforeStatusHook 收到自定义管线注入 NaN 时安全回退为 0（绝不产生 int.MinValue）
        var ip1 = CreatePlayer("ip1", HeroNames.Vanessa, 500);
        var ip2 = CreatePlayer("ip2", HeroNames.Vanessa, 500);
        ip1.Hand.TryAddAuto(CreateItem(
            "nan_pipe_src",
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
                        {"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerShieldApply","ReferenceValue":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}
                      ]
                    }
                    """)
                }
            }));
        var pipeRes = CombatSimulator.Simulate(
            repo,
            ip1,
            ip2,
            seed: 1202,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: null,
            sandstormEnabled: false,
            pipelineInterceptorsOverride: [new NanPoisonInterceptor()]);
        Assert.True(pipeRes.Player1RemainingHp >= 0 && pipeRes.Player2RemainingHp >= 0);
        Assert.Equal(0, pipeRes.Timeline.First().P1Shield);
    }

    [Fact]
    public void P1_16_CooldownFloorDecoupled_AndGetAttributeRawIncludesMult()
    {
        var repo = Repo.Value;
        // 2000ms 物品带自身 60% CDR（理论 800ms），在 TurboFrenzy (-35%, floor 500ms) 下有效冷却应为 520ms（而非被硬钳 1000ms 算出 650ms）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var card = CreateItem(
            "cdr_fast",
            attrs: new() { ["CooldownMax"] = 2000, ["PercentCooldownReduction"] = 60, ["DamageAmount"] = 5 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
        p1.Hand.TryAddAuto(card);

        // 验证 PercentCooldownReduction 叠加乘区生效（20% * 2 = 40% -> 2000 * 0.6 = 1200ms）
        var multCdrCard = CreateItem("mult_cdr", attrs: new() { ["CooldownMax"] = 2000, ["PercentCooldownReduction"] = 20 });
        multCdrCard.ModifyAttribute("PercentCooldownReduction", "Multiply", 2.0, DurationTarget.Combat);
        Assert.Equal(1200, multCdrCard.GetAttribute("CooldownMax"), precision: 3);

        var turboMod = new CombatModifiers { CooldownReductionPct = 0.35, MinCooldownFloorMs = 500 };
        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1601, combatType: "PVP", recordTimeline: false, recordEvents: true, syncPermanentChanges: false, modifiers: turboMod);
        var firstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == card.InstanceId);
        // 520ms -> 11 ticks = 550ms -> 首次开火于 500ms（而非 650ms -> 13 ticks -> 600ms）
        Assert.True(firstFire.TimeMs <= 550, $"2000ms*0.4*0.65=520ms 应于 <=550ms 首开火，实际 {firstFire.TimeMs}ms");
    }

    [Fact]
    public void P1_17_NonNegativeAttributes_ClampAtZero_AndNoNegativeDebt_WithoutAuraBaking()
    {
        // 1. 0% 暴击物品受 -90% CritChance 后输出钳制为 0
        var card = CreateItem("crit_victim", attrs: new() { ["DamageAmount"] = 20, ["CritChance"] = 0 });
        card.ModifyAttribute("CritChance", "Subtract", 90, DurationTarget.Combat);
        Assert.Equal(0, card.GetAttribute("CritChance"));

        // 2. 层隔离反例（#24-1）：Base 20 + Aura +30 执行 Permanent Subtract 50，撤光环再 Permanent Add +25 精确得 25
        var sword = CreateItem("layer_sword", attrs: new() { ["DamageAmount"] = 20 });
        sword.ApplyAuraAttribute("DamageAmount", "Add", 30);
        Assert.Equal(50, sword.GetAttribute("DamageAmount"));
        sword.ModifyAttribute("DamageAmount", "Subtract", 50, DurationTarget.Permanent);
        sword.ClearAuraModifiers();
        Assert.Equal(0, sword.GetAttribute("DamageAmount"));
        sword.ModifyAttribute("DamageAmount", "Add", 25, DurationTarget.Permanent);
        Assert.Equal(25, sword.GetAttribute("DamageAmount"));

        // 3. PercentCooldownReduction 与 Custom_0 仍合法支持负值
        var negOk = CreateItem("neg_ok", attrs: new() { ["CooldownMax"] = 2000, ["Custom_0"] = 0 });
        negOk.ModifyAttribute("PercentCooldownReduction", "Subtract", 25, DurationTarget.Combat);
        negOk.ModifyAttribute("Custom_0", "Subtract", 3, DurationTarget.Combat);
        Assert.Equal(-25, negOk.GetAttribute("PercentCooldownReduction"));
        Assert.Equal(-3, negOk.GetAttribute("Custom_0"));

        // 4. 限时 TCombatDuration Subtract 50 到期后精确还原原值 20（不多加属性）
        var repo = Repo.Value;
        var tp1 = CreatePlayer("tp1", HeroNames.Vanessa, 2000);
        var tp2 = CreatePlayer("tp2", HeroNames.Vanessa, 2000);
        var timedVictim = CreateItem("timed_sub_victim", attrs: new() { ["DamageAmount"] = 20, ["CooldownMax"] = 5000 });
        var timedDebuffer = CreateItem(
            "timed_debuffer",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""
                    {
                      "$type": "TActionCardModifyAttribute",
                      "AttributeType": "DamageAmount",
                      "Operation": "Subtract",
                      "Value": { "$type": "TFixedValue", "Value": 50 },
                      "Duration": { "$type": "TCombatDuration", "DurationInMs": 500 },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
                    }
                    """)
                }
            });
        tp1.Hand.TryAddAuto(timedVictim);
        tp1.Hand.TryAddAuto(timedDebuffer);
        var timedRes = CombatSimulator.Simulate(repo, tp1, tp2, seed: 1701, combatType: "PVP", recordTimeline: true, recordEvents: false, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);
        var at0 = timedRes.Timeline.First(t => t.TimeMs == 0).Cards.First(c => c.InstanceId == timedVictim.InstanceId);
        var at600 = timedRes.Timeline.First(t => t.TimeMs == 600).Cards.First(c => c.InstanceId == timedVictim.InstanceId);
        Assert.Equal(0, at0.DamageAmount);
        Assert.Equal(20, at600.DamageAmount);
    }

    [Fact]
    public void Redline_HealCleanse5PercentOfHealAmount_RemainsUntouched()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
        p1.Hand.TryAddAuto(CreateItem(
            "burn_200",
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
                        {"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":200},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},
                        {"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":200},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}
                      ]
                    }
                    """)
                }
            }));
        // 治疗 10 HP 对 200 Burn：按治疗量 5% 至少 1 => 仅净 1 层剩 199（绝非 200*5%=10 剩 190！）
        p2.Hand.TryAddAuto(CreateItem(
            "small_heal_10",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    Trigger = ParseNode("""{"$type":"TTriggerOnPlayerDamaged","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerHeal","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                }
            }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 7001, recordTimeline: true, recordEvents: false);
        var f0 = res.Timeline.First(t => t.TimeMs == 0);
        Assert.Equal(199, f0.P2Burn);
    }
}
