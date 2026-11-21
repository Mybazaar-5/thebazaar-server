using System.Diagnostics;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// 战斗引擎单元测试与全量卡池/技能/野怪压测套件。
/// </summary>
public sealed class CombatEngineTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load());
    private readonly ITestOutputHelper _output;

    public CombatEngineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BurnShouldTickEvery500msBeHalvedByShieldAndDecay()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 500);

        var burnItem = CreateSimpleActiveItem(
            "burn_tester",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerBurnApply",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(100)
            });
        p1.Hand.TryAddAuto(burnItem);

        var shieldItem = CreateSimpleActiveItem(
            "shield_tester",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerShieldApply",
                Target = TargetSelf(),
                ReferenceValue = Fixed(200)
            });
        p2.Hand.TryAddAuto(shieldItem);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1);
        var burnTicks = res.Events.Where(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "p2").ToList();

        Assert.NotEmpty(burnTicks);
        Assert.True(burnTicks[0].TimeMs >= 1000 && burnTicks[0].TimeMs % 500 == 0);
        // 200 护盾下 100 灼烧第一跳应减半为 50
        Assert.Equal(50, (int)burnTicks[0].Value);
        // 后续衰减 3%（100 -> 97），在有护盾时减半为 48
        Assert.True(burnTicks.Count >= 2);
        Assert.Equal(48, (int)burnTicks[1].Value);
    }

    [Fact]
    public void PoisonShouldTickEvery1000msAndBypassShield()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Mak, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 500);

        p1.Hand.TryAddAuto(CreateSimpleActiveItem(
            "poison_tester",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerPoisonApply",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(20)
            }));

        p2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "big_shield",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerShieldApply",
                Target = TargetSelf(),
                ReferenceValue = Fixed(500)
            }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2);
        var poisonTicks = res.Events.Where(e => e.Type == CombatEventType.PoisonTick && e.TargetPlayerId == "p2").ToList();

        Assert.NotEmpty(poisonTicks);
        Assert.True(poisonTicks[0].TimeMs >= 1000);
        // 剧毒应直接扣血且不消耗护盾
        Assert.Equal(20, (int)poisonTicks[0].Value);
        Assert.True(poisonTicks[0].Player2Shield >= 500);
        Assert.Equal(480, poisonTicks[0].Player2Health);
    }

    [Fact]
    public void RegenAndShieldShouldRestoreHpAndAbsorbDamage()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Mak, 500);

        // P1 每次造成 30 伤害
        p1.Hand.TryAddAuto(CreateSimpleActiveItem(
            "dmg_stick",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(30)
            }));

        // P2 同时获得 40 护盾与 15 再生
        p2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "def_kit",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionAnd",
                Actions =
                [
                    new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(40) },
                    new RuleNode { Type = "TActionPlayerRegenApply", Target = TargetSelf(), ReferenceValue = Fixed(15) }
                ]
            }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 3);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.TargetPlayerId == "p2");
        Assert.Contains(res.Events, e => e.Type == CombatEventType.RegenTick && e.TargetPlayerId == "p2" && e.Value >= 15);
        Assert.True(res.Player2TotalShieldGained > 0);
    }

    [Fact]
    public void HasteSlowFreezeAndRadiantShouldControlCooldownSpeed()
    {
        var repo = Repo.Value;

        // 基准：4000ms CD，12000ms 内正常约开火 3 次
        var baseP1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var baseP2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        var baseWeapon = CreateSimpleActiveItem("w_base", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) });
        baseP1.Hand.TryAddAuto(baseWeapon);
        var baseRes = CombatSimulator.Simulate(repo, baseP1, baseP2, seed: 10);
        int baseFirstFire = baseRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").TimeMs;
        Assert.Equal(3980, baseFirstFire); // 20ms * 200 ticks (0..3980ms)

        // Haste 4000ms：加速 2x，首触发应在 ~2000ms 左右
        var hasteP1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var hasteP2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        hasteP1.Hand.TryAddAuto(CreateSimpleActiveItem("w_hasted", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) }));
        hasteP1.Hand.TryAddAuto(CreateStartFightItem("haste_starter", new RuleNode
        {
            Type = "TActionCardHaste",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(4000)
        }));
        var hasteRes = CombatSimulator.Simulate(repo, hasteP1, hasteP2, seed: 10);
        int hasteFirstFire = hasteRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").TimeMs;
        Assert.True(hasteFirstFire < 2500, $"Haste 应显著提前开火时间，实际: {hasteFirstFire}ms");

        // Slow 4000ms：减速 0.5x，首触发应延后至 ~6000ms 左右
        var slowP1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var slowP2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        slowP1.Hand.TryAddAuto(CreateSimpleActiveItem("w_slowed", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) }));
        slowP2.Hand.TryAddAuto(CreateStartFightItem("slow_starter", new RuleNode
        {
            Type = "TActionCardSlow",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
            ReferenceValue = Fixed(4000)
        }));
        var slowRes = CombatSimulator.Simulate(repo, slowP1, slowP2, seed: 10);
        int slowFirstFire = slowRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").TimeMs;
        Assert.True(slowFirstFire > 5500, $"Slow 应延后开火时间，实际: {slowFirstFire}ms");

        // Freeze 2000ms：冻结期间完全停止读条，首触发延后至 ~6000ms
        var frzP1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var frzP2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        frzP1.Hand.TryAddAuto(CreateSimpleActiveItem("w_frozen", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) }));
        frzP2.Hand.TryAddAuto(CreateStartFightItem("frz_starter", new RuleNode
        {
            Type = "TActionCardFreeze",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
            ReferenceValue = Fixed(2000)
        }));
        var frzRes = CombatSimulator.Simulate(repo, frzP1, frzP2, seed: 10);
        int frzFirstFire = frzRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").TimeMs;
        Assert.True(frzFirstFire >= 5900, $"Freeze 2000ms 应使 4000ms 物品延后至 >= 5900ms，实际: {frzFirstFire}ms");

        // Radiant 免疫 Freeze 与 Slow
        var radP1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var radP2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        radP1.Hand.TryAddAuto(CreateSimpleActiveItem("w_radiant", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) }, enchantment: "Radiant"));
        radP2.Hand.TryAddAuto(CreateStartFightItem("cc_starter", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionCardFreeze", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(5000) },
                new RuleNode { Type = "TActionCardSlow", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(5000) }
            ]
        }));
        var radRes = CombatSimulator.Simulate(repo, radP1, radP2, seed: 10);
        int radFirstFire = radRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").TimeMs;
        Assert.Equal(baseFirstFire, radFirstFire);
    }

    [Fact]
    public void PassiveTriggersShouldRespect250msIcdAndHandleDynamicQueueMutation()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);

        // 物品1：1000ms 3连击（会在同一时刻连续发出 3 次 TTriggerOnItemUsed）
        var striker = CreateSimpleActiveItem("multi_striker", 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(5) },
            extraAttrs: new() { ["Multicast"] = 3 });
        p1.Hand.TryAddAuto(striker);

        // 物品2：监听任意己方物品使用 -> 造成 7 点伤害（将向 triggerQueues 插入自身键，并在首次执行时触发物品3插入新键）
        var reactorTpl = new CardTemplate
        {
            Id = "reactor_icd",
            InternalName = "Reactor ICD",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnItemUsed",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = TargetOpponent(),
                        ReferenceValue = Fixed(7)
                    }
                }
            }
        };
        p1.Hand.TryAddAuto(new CardInstance(reactorTpl));

        // 物品3：监听伤害事件 -> 加 3 点护盾（ProcessIcdQueues 执行物品2时才首次写入 triggerQueues，验证不会抛枚举修改异常）
        var chainShieldTpl = new CardTemplate
        {
            Id = "chain_shield",
            InternalName = "Chain Shield",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardPerformedDamage",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(3)
                    }
                }
            }
        };
        p1.Hand.TryAddAuto(new CardInstance(chainShieldTpl));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 22);
        var reactorHits = res.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.Value == 7)
            .OrderBy(e => e.TimeMs)
            .ToList();

        Assert.True(reactorHits.Count >= 3);
        // 连续 3 次入队后受 250ms ICD 限制，相邻两次执行时间差必须 >= 250ms（20ms 步长下为 >= 260ms）
        Assert.True(reactorHits[1].TimeMs - reactorHits[0].TimeMs >= 250,
            $"ICD 间距应 >= 250ms，实际: {reactorHits[1].TimeMs - reactorHits[0].TimeMs}ms");
        Assert.True(reactorHits[2].TimeMs - reactorHits[1].TimeMs >= 250);
        Assert.True(res.Player1TotalShieldGained > 0);
    }

    [Fact]
    public void CritMulticastAndAmmoShouldWorkAccurately()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 2000);

        // CritChance = 100%, Multicast = 3, AmmoMax = 2, Damage = 10 -> 每轮 3 次暴击 (20 伤害 * 3 = 60)，共 2 轮 = 6 发 = 120 伤害
        var pistol = CreateSimpleActiveItem(
            "crit_multi_pistol",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) },
            extraAttrs: new()
            {
                ["CritChance"] = 100,
                ["Multicast"] = 3,
                ["AmmoMax"] = 2
            });
        p1.Hand.TryAddAuto(pistol);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 33);
        var fired = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourcePlayerId == "p1").ToList();
        var hits = res.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1").ToList();

        Assert.Equal(6, fired.Count); // 2 弹药 * 3 连发
        Assert.Equal(6, hits.Count);
        Assert.All(hits, h =>
        {
            Assert.True(h.IsCrit);
            Assert.Equal(20, (int)h.Value);
        });
        Assert.Equal(120, (int)res.Player1TotalDamageDealt);
    }

    [Fact]
    public void SandstormAndForceUseGuardShouldTerminateCleanly()
    {
        var repo = Repo.Value;

        // 1. 沙暴测试：空手牌双方靠沙暴决胜
        var s1 = CreatePlayer("s1", HeroNames.Pygmalien, 500);
        var s2 = CreatePlayer("s2", HeroNames.Vanessa, 300);
        var sandRes = CombatSimulator.Simulate(repo, s1, s2, seed: 44);

        var warning = sandRes.Events.FirstOrDefault(e => e.Type == CombatEventType.SandstormWarning);
        var firstTick = sandRes.Events.FirstOrDefault(e => e.Type == CombatEventType.SandstormTick);
        Assert.NotNull(warning);
        Assert.NotNull(firstTick);
        Assert.Equal(20000, warning!.TimeMs);
        Assert.Equal(30000, firstTick!.TimeMs);
        Assert.Equal(1, (int)firstTick.Value);
        Assert.Equal("s1", sandRes.WinnerPlayerId);
        Assert.True(sandRes.DurationMs <= CombatSimulator.MaxDurationMs);

        // 2. 无限 ForceUse 互指死循环防护测试
        var f1 = CreatePlayer("f1", HeroNames.Vanessa, 500);
        var f2 = CreatePlayer("f2", HeroNames.Vanessa, 500);
        var loopA = CreateSimpleActiveItem("loop_a", 1000, new RuleNode
        {
            Type = "TActionCardForceUse",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
        });
        var loopB = CreateSimpleActiveItem("loop_b", 5000, new RuleNode
        {
            Type = "TActionCardForceUse",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
        });
        f1.Hand.TryAddAuto(loopA);
        f1.Hand.TryAddAuto(loopB);

        var loopRes = CombatSimulator.Simulate(repo, f1, f2, seed: 55);
        Assert.True(loopRes.DurationMs <= CombatSimulator.MaxDurationMs);
        Assert.False(string.IsNullOrEmpty(loopRes.WinnerPlayerId));
    }

    [Fact]
    public void SameSeedShouldBeDeterministicAndRecordEventsFalseMustMatchTrue()
    {
        var repo = Repo.Value;
        var items = repo.AlwaysSpawnableItems.Where(c => c.CardSize == CardSize.Small).Take(12).ToList();
        var skills = repo.AlwaysSpawnableSkills.Take(6).ToList();

        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1800);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 1800);
        for (int i = 0; i < 6; i++) p1.Hand.TryAddAuto(new CardInstance(items[i]));
        for (int i = 6; i < 12; i++) p2.Hand.TryAddAuto(new CardInstance(items[i]));
        for (int i = 0; i < 3; i++) p1.Skills.Add(new CardInstance(skills[i]));
        for (int i = 3; i < 6; i++) p2.Skills.Add(new CardInstance(skills[i]));

        var runA = CombatSimulator.Simulate(repo, p1, p2, seed: 2026, combatType: "PVP", recordTimeline: true, recordEvents: true);
        var runB = CombatSimulator.Simulate(repo, p1, p2, seed: 2026, combatType: "PVP", recordTimeline: true, recordEvents: true);
        var runHeadless = CombatSimulator.Simulate(repo, p1, p2, seed: 2026, combatType: "PVP", recordTimeline: false, recordEvents: false);

        Assert.Equal(runA.WinnerPlayerId, runB.WinnerPlayerId);
        Assert.Equal(runA.DurationMs, runB.DurationMs);
        Assert.Equal(runA.Player1RemainingHp, runB.Player1RemainingHp);
        Assert.Equal(runA.Player2RemainingHp, runB.Player2RemainingHp);
        Assert.Equal(runA.Events.Count, runB.Events.Count);
        Assert.Equal(runA.Timeline.Count, runB.Timeline.Count);

        Assert.NotEmpty(runA.Events);
        Assert.NotEmpty(runA.Timeline);
        Assert.Empty(runHeadless.Events);
        Assert.Empty(runHeadless.Timeline);

        Assert.Equal(runA.WinnerPlayerId, runHeadless.WinnerPlayerId);
        Assert.Equal(runA.DurationMs, runHeadless.DurationMs);
        Assert.Equal(runA.Player1RemainingHp, runHeadless.Player1RemainingHp);
        Assert.Equal(runA.Player2RemainingHp, runHeadless.Player2RemainingHp);
        Assert.Equal(runA.Player1TotalDamageDealt, runHeadless.Player1TotalDamageDealt);
        Assert.Equal(runA.Player2TotalDamageDealt, runHeadless.Player2TotalDamageDealt);
        Assert.Equal(runA.Player1TotalShieldGained, runHeadless.Player1TotalShieldGained);
        Assert.Equal(runA.Player2TotalShieldGained, runHeadless.Player2TotalShieldGained);
    }

    [Fact]
    public void NewRulePrimitivesAndNestedComparisonValueShouldWork()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 1000);

        // 1. 充能触发 TTriggerOnCardCharged + TActionCardCharge
        var battery = CreateSimpleActiveItem("battery", 1000, new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(1000)
        });
        var receiver = CreateSimpleActiveItem("receiver", 4000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(5)
        });
        var chargeWatcherTpl = new CardTemplate
        {
            Id = "charge_watcher",
            InternalName = "Charge Watcher",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardCharged",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(12)
                    }
                }
            }
        };
        p1.Hand.TryAddAuto(battery);
        p1.Hand.TryAddAuto(receiver);
        p1.Hand.TryAddAuto(new CardInstance(chargeWatcherTpl));

        // 2. TTriggerOnPlayerDamaged + TActionCardTiny
        var counterStrikeTpl = new CardTemplate
        {
            Id = "damaged_watcher",
            InternalName = "Damaged Watcher",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnPlayerDamaged",
                        Subject = TargetSelf(),
                        ComparisonAmount = 0,
                        ComparisonOperator = "GreaterThan"
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionCardTiny",
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    }
                }
            }
        };
        var tinyTarget = new CardInstance(counterStrikeTpl);
        p2.Hand.TryAddAuto(tinyTarget);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 88);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ChargeApplied);
        Assert.True(res.Player1TotalShieldGained >= 12);
        Assert.False(string.IsNullOrEmpty(res.WinnerPlayerId));

        // 3. 嵌套对象形式 ComparisonValue + TCardConditionalHero 求值校验
        var condJson = "{\"$type\":\"TCardConditionalAnd\",\"Conditions\":[{\"$type\":\"TCardConditionalAttribute\",\"Attribute\":\"CooldownMax\",\"ComparisonOperator\":\"GreaterThan\",\"ComparisonValue\":{\"$type\":\"TFixedValue\",\"Value\":500.0}},{\"$type\":\"TCardConditionalHero\",\"Heroes\":[\"Vanessa\"],\"Operator\":\"Any\"}]}";
        var condNode = JsonSerializer.Deserialize<RuleNode>(condJson, GameDatabaseLoader.JsonOptions)!;
        var vanessaInst = repo.AlwaysSpawnableItems
            .Where(c => c.Heroes.Contains(HeroNames.Vanessa))
            .Select(c => new CardInstance(c))
            .First(inst => inst.GetAttribute("CooldownMax") > 500);
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2);
        Assert.True(ValueAndConditionEvaluator.EvaluateCardCondition(condNode, vanessaInst, p1, ctx));
    }

    [Fact]
    public void CritTriggerShouldScaleRealRunicBladeAndNonDamageCrits()
    {
        var repo = Repo.Value;
        var runicTpl = repo.AllItems.First(c => string.Equals(c.InternalName, "Runic Blade", StringComparison.OrdinalIgnoreCase));

        // 1. 真实 Runic Blade：100% 暴击下每次开火触发 TTriggerOnCardCritted 翻倍/累加伤害
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 5000);
        var runic = new CardInstance(runicTpl, CardTier.Gold);
        runic.PermanentAdd["CritChance"] = 100;
        runic.PermanentAdd["FlatCooldownReduction"] = 9000; // 加速至 ~1000ms CD 以便多轮对比
        p1.Hand.TryAddAuto(runic);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 101);
        var hits = res.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p1").ToList();
        var attrMods = res.Events.Where(e => e.Type == CombatEventType.CardAttributeModified && e.Detail == "DamageAmount").ToList();

        Assert.True(hits.Count >= 3, $"Runic Blade 应至少打出 3 次暴击伤害，实际: {hits.Count}");
        Assert.All(hits, h => Assert.True(h.IsCrit));
        Assert.NotEmpty(attrMods);
        // 首次暴击基础 20 -> 暴击 40；触发 TTriggerOnCardCritted 后基础 +20 -> 第二次暴击 80 -> 第三次暴击 120
        Assert.Equal(40, (int)hits[0].Value);
        Assert.True(hits[1].Value > hits[0].Value, $"第二次暴击伤害应因 TTriggerOnCardCritted 成长，实际: {hits[0].Value} -> {hits[1].Value}");
        Assert.True(hits[2].Value > hits[1].Value, $"第三次暴击伤害应持续递增，实际: {hits[1].Value} -> {hits[2].Value}");

        // 2. 非伤害类暴击（护盾暴击）也应触发 TTriggerOnCardCritted
        var sp1 = CreatePlayer("sp1", HeroNames.Dooley, 2000);
        var sp2 = CreatePlayer("sp2", HeroNames.Vanessa, 2000);
        var critShield = CreateSimpleActiveItem(
            "crit_shield",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerShieldApply",
                Target = TargetSelf(),
                ReferenceValue = Fixed(10)
            },
            extraAttrs: new() { ["CritChance"] = 100 });
        var critWatcherTpl = new CardTemplate
        {
            Id = "crit_watcher",
            InternalName = "Crit Watcher",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardCritted",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = TargetOpponent(),
                        ReferenceValue = Fixed(15)
                    }
                }
            }
        };
        sp1.Hand.TryAddAuto(critShield);
        sp1.Hand.TryAddAuto(new CardInstance(critWatcherTpl));

        var shieldCritRes = CombatSimulator.Simulate(repo, sp1, sp2, seed: 102);
        Assert.True(shieldCritRes.Player1TotalShieldGained >= 20);
        Assert.Contains(shieldCritRes.Events, e => e.Type == CombatEventType.DamageApplied && e.Value == 15);
    }

    [Fact]
    public void HalfHealthTriggerShouldActivateRealHardShellAndHealthyHeart()
    {
        var repo = Repo.Value;
        var hardShellTpl = repo.AllSkills.First(c => string.Equals(c.InternalName, "Hard Shell", StringComparison.OrdinalIgnoreCase));
        var healthyHeartTpl = repo.AllSkills.First(c => string.Equals(c.InternalName, "Healthy Heart", StringComparison.OrdinalIgnoreCase));

        // 1. 真实 Hard Shell：首次跌破 50% 血量时获得 20% MaxHP 护盾（1000 HP -> 200 护盾），且仅触发一次
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 1000);
        p1.Hand.TryAddAuto(CreateSimpleActiveItem("heavy_hit", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(300)
        }));
        p2.Skills.Add(new CardInstance(hardShellTpl, CardTier.Bronze));

        var hardShellRes = CombatSimulator.Simulate(repo, p1, p2, seed: 201);
        var p2ShieldEvents = hardShellRes.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.TargetPlayerId == "p2")
            .ToList();

        Assert.Single(p2ShieldEvents);
        // 第1击 980ms (1000->700 >=500 未触发)；第2击 1980ms (700->400 <500 触发 200 护盾)
        Assert.True(p2ShieldEvents[0].TimeMs >= 1980);
        Assert.Equal(200, (int)p2ShieldEvents[0].Value);
        Assert.Equal(200, (int)hardShellRes.Player2TotalShieldGained);

        // 2. 真实 Healthy Heart：跌破半血时触发回血
        var hp1 = CreatePlayer("hp1", HeroNames.Vanessa, 2000);
        var hp2 = CreatePlayer("hp2", HeroNames.Pygmalien, 1000);
        hp1.Hand.TryAddAuto(CreateSimpleActiveItem("heavy_hit2", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(300)
        }));
        hp2.Skills.Add(new CardInstance(healthyHeartTpl, CardTier.Diamond));

        var healthyHeartRes = CombatSimulator.Simulate(repo, hp1, hp2, seed: 202);
        var p2HealEvents = healthyHeartRes.Events
            .Where(e => e.Type == CombatEventType.HealApplied && e.TargetPlayerId == "hp2")
            .ToList();
        Assert.NotEmpty(p2HealEvents);
        Assert.True(p2HealEvents[0].TimeMs >= 1980);
        Assert.True(p2HealEvents[0].Value > 0);
    }

    [Fact]
    public void CardAttributeChangedAndBeforeCardDestroyedShouldTriggerInCombat()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);

        // 1. TActionCardModifyAttribute 递增武器伤害，并触发 TTriggerOnCardAttributeChanged
        var sword = CreateSimpleActiveItem("growing_sword", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent()
        }, extraAttrs: new() { ["DamageAmount"] = 10 });

        var buffer = CreateSimpleActiveItem("dmg_buffer", 1000, new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "DamageAmount",
            Operation = "Add",
            Duration = new RuleNode { Type = "TDeterminantDuration", DurationType = "UntilEndOfCombat" },
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            RawValueNode = JsonSerializer.SerializeToElement(Fixed(15), GameDatabaseLoader.JsonOptions)
        });

        var attrWatcherTpl = new CardTemplate
        {
            Id = "attr_watcher",
            InternalName = "Attr Watcher",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardAttributeChanged",
                        AttributeChanged = "DamageAmount",
                        ChangeType = "Gain",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(25)
                    }
                }
            }
        };

        // 2. TTriggerOnBeforeCardDestroyed：对手开局摧毁该卡时，仍应先触发其遗言护盾
        var martyrTpl = new CardTemplate
        {
            Id = "martyr_item",
            InternalName = "Martyr Item",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnBeforeCardDestroyed",
                        Subject = new RuleNode { Type = "TTargetCardSelf" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(50)
                    }
                }
            }
        };

        p1.Hand.TryAddAuto(sword);
        p1.Hand.TryAddAuto(buffer);
        p1.Hand.TryAddAuto(new CardInstance(attrWatcherTpl));

        var martyr = new CardInstance(martyrTpl);
        p2.Hand.TryAddAuto(martyr);
        p1.Hand.TryAddAuto(CreateStartFightItem("destroyer", new RuleNode
        {
            Type = "TActionCardDestroy",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 303);
        var swordHits = res.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == sword.InstanceId).ToList();

        Assert.True(swordHits.Count >= 2);
        Assert.True(swordHits[1].Value > swordHits[0].Value, $"DamageAmount 应随局内增益上升，实际: {swordHits[0].Value} -> {swordHits[1].Value}");
        Assert.True(res.Player1TotalShieldGained >= 25, "TTriggerOnCardAttributeChanged 应触发己方护盾增益");
        Assert.Contains(res.Events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == martyr.InstanceId);
        Assert.True(res.Player2TotalShieldGained >= 50, "TTriggerOnBeforeCardDestroyed 应在卡牌被摧毁前成功结算遗言护盾");
    }

    [Fact]
    public void InCombatQuestShouldAdvanceCompleteAndUnlockAbilities()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Mak, 3000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 3000);

        // 构造带战斗内 Quest 的卡牌（施毒 2 次即完成 Quest，解锁新主动毒伤能力并在同场战斗触发 TTriggerOnCardQuestCompleted）
        var questTpl = new CardTemplate
        {
            Id = "combat_quest_relic",
            InternalName = "Combat Quest Relic",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = new Dictionary<string, double> { ["CooldownMax"] = 1000 },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerPoisonApply",
                        Target = TargetOpponent(),
                        ReferenceValue = Fixed(3)
                    }
                },
                ["on_q"] = new()
                {
                    Id = "on_q",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnCardQuestCompleted",
                        Subject = new RuleNode { Type = "TTargetCardSelf" }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(77)
                    }
                }
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
                            Target = 2,
                            Trigger = new RuleNode
                            {
                                Type = "TTriggerOnCardPerformedPoison",
                                Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
                            },
                            Reward = new QuestReward
                            {
                                Attributes = new Dictionary<string, double> { ["ShieldApplyAmount"] = 40 },
                                Abilities = new Dictionary<string, AbilityDefinition>
                                {
                                    ["q1"] = new()
                                    {
                                        Id = "q1",
                                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                                        Action = new RuleNode
                                        {
                                            Type = "TActionPlayerShieldApply",
                                            Target = TargetSelf()
                                        }
                                    }
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var questCard = new CardInstance(questTpl);
        p1.Hand.TryAddAuto(questCard);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 404);
        var p1Shields = res.Events
            .Where(e => e.Type == CombatEventType.ShieldApplied && e.TargetPlayerId == "p1")
            .ToList();

        // 第 2 秒完成 Quest -> on_q 触发 +77 护盾；第 3 秒起新解锁的 q1 每次开火触发 +40 护盾
        Assert.Contains(p1Shields, e => (int)e.Value == 77);
        Assert.Contains(p1Shields, e => (int)e.Value == 40);
        Assert.True(res.Player1TotalShieldGained >= 117);
    }

    [Fact]
    public void FullCatalogItemsSkillsAndMonstersSmokeStressTest()
    {
        var repo = Repo.Value;
        var sw = Stopwatch.StartNew();

        // 1. 将全部 Item 按 10 格棋盘打包编队（覆盖 100% repo.AllItems）
        var itemBoards = new List<PlayerState>();
        var coveredItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        PlayerState? currentBoard = null;
        int boardIdx = 0;
        foreach (var itemTpl in repo.AllItems)
        {
            var card = new CardInstance(itemTpl);
            if (currentBoard == null || currentBoard.Hand.Cards.Count >= 5 || !currentBoard.Hand.TryAddAuto(card))
            {
                currentBoard = CreatePlayer($"stress_p_{boardIdx++}", HeroNames.Common, 2500);
                itemBoards.Add(currentBoard);
                Assert.True(currentBoard.Hand.TryAddAuto(card), $"空棋盘应能放下物品: {itemTpl.Id} ({itemTpl.InternalName})");
            }
            coveredItems.Add(itemTpl.Id);
        }

        // 2. 将全部 Skill 均匀挂载到编队上（覆盖 100% repo.AllSkills）
        var coveredSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < repo.AllSkills.Count; i++)
        {
            var skillTpl = repo.AllSkills[i];
            itemBoards[i % itemBoards.Count].Skills.Add(new CardInstance(skillTpl));
            coveredSkills.Add(skillTpl.Id);
        }

        Assert.Equal(repo.AllItems.Count, coveredItems.Count);
        Assert.Equal(repo.AllSkills.Count, coveredSkills.Count);

        // 3. 全物品+全技能 PVP 对抗压测
        int pvpFights = 0;
        for (int i = 0; i < itemBoards.Count; i += 2)
        {
            var b1 = itemBoards[i];
            var b2 = itemBoards[(i + 1) % itemBoards.Count];
            var res = CombatSimulator.Simulate(repo, b1, b2, seed: 1000 + i, combatType: "PVP", recordTimeline: false, recordEvents: false);
            Assert.False(string.IsNullOrEmpty(res.WinnerPlayerId));
            Assert.InRange(res.DurationMs, 0, CombatSimulator.MaxDurationMs);
            pvpFights++;
        }

        // 4. 全野怪 PVE 对抗压测（覆盖 100% repo.MonstersById）
        int pveFights = 0;
        foreach (var monster in repo.MonstersById.Values)
        {
            var mState = CombatSimulator.BuildMonsterPlayerState(repo, monster);
            var playerBoard = itemBoards[pveFights % itemBoards.Count];
            var res = CombatSimulator.Simulate(repo, playerBoard, mState, seed: 5000 + pveFights, combatType: "PVE", recordTimeline: false, recordEvents: false);
            Assert.False(string.IsNullOrEmpty(res.WinnerPlayerId));
            Assert.InRange(res.DurationMs, 0, CombatSimulator.MaxDurationMs);
            pveFights++;
        }

        sw.Stop();
        Assert.Equal(repo.MonstersById.Count, pveFights);
        _output.WriteLine(
            $"全卡池压测完成: Items={coveredItems.Count}, Skills={coveredSkills.Count}, Boards={itemBoards.Count}, " +
            $"PVP={pvpFights}场, PVE(Monsters)={pveFights}场, 总耗时={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void SpawnQueryEngine_ShouldFilterUnsupportedInheritedEnchantments()
    {
        var repo = Repo.Value;
        var toolboxTpl = repo.AllItems.First(c => string.Equals(c.InternalName, "Toolbox", StringComparison.OrdinalIgnoreCase));
        var loupeTpl = repo.AllItems.First(c => string.Equals(c.InternalName, "Loupe", StringComparison.OrdinalIgnoreCase));
        var deadlyToolTpl = repo.AlwaysSpawnableItems.First(c =>
            !string.Equals(c.InternalName, "Loupe", StringComparison.OrdinalIgnoreCase) &&
            c.Tags.Contains("Tool", StringComparer.OrdinalIgnoreCase) &&
            c.Enchantments != null &&
            c.Enchantments.ContainsKey("Deadly"));

        Assert.True(loupeTpl.Enchantments == null || !loupeTpl.Enchantments.ContainsKey("Deadly"));

        var player = CreatePlayer("p_spawn", HeroNames.Stelle, 1000);
        var toolbox = new CardInstance(toolboxTpl, CardTier.Diamond, "Deadly");
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(42))
        {
            SelfCard = toolbox
        };

        // 1. 从 Deadly 附魔的工具箱继承附魔生成仅支持 Golden/Shiny 的 Loupe 时，必须自动剥离不支持的 Deadly
        var spawnLoupeQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Behaviors =
            [
                new RuleNode { Type = "TSpawnBehaviorInheritEnchantment", Inherits = true },
                new RuleNode { Type = "TSpawnBehaviorInheritTier", Inherits = true }
            ],
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [loupeTpl.Id] }]
                }
            ]
        };

        var spawnedLoupe = SpawnQueryEngine.SpawnCards(spawnLoupeQuery, ctx, 1);
        Assert.Single(spawnedLoupe);
        Assert.Equal(loupeTpl.Id, spawnedLoupe[0].Template.Id);
        Assert.Null(spawnedLoupe[0].Enchantment);

        // 2. 生成明确支持 Deadly 的工具时，正常保留 Deadly 附魔
        var spawnDeadlyToolQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Behaviors =
            [
                new RuleNode { Type = "TSpawnBehaviorInheritEnchantment", Inherits = true },
                new RuleNode { Type = "TSpawnBehaviorInheritTier", Inherits = true }
            ],
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [deadlyToolTpl.Id] }]
                }
            ]
        };

        var spawnedDeadly = SpawnQueryEngine.SpawnCards(spawnDeadlyToolQuery, ctx, 1);
        Assert.Single(spawnedDeadly);
        Assert.Equal(deadlyToolTpl.Id, spawnedDeadly[0].Template.Id);
        Assert.Equal("Deadly", spawnedDeadly[0].Enchantment);

        // 3. TSpawnBehaviorEnchantment 同样绝不把不支持的 Deadly 赋予 Loupe
        var behaviorQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Behaviors =
            [
                new RuleNode
                {
                    Type = "TSpawnBehaviorEnchantment",
                    RawEnchantmentsElement = JsonSerializer.SerializeToElement(new[] { "Deadly" })
                }
            ],
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [loupeTpl.Id] }]
                }
            ]
        };

        var spawnedBehaviorLoupe = SpawnQueryEngine.SpawnCards(behaviorQuery, ctx, 1);
        Assert.Single(spawnedBehaviorLoupe);
        Assert.Null(spawnedBehaviorLoupe[0].Enchantment);
    }

    private static PlayerState CreatePlayer(string id, string hero, int hp)
        => new(id, id, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 10);

    private static RuleNode Fixed(double value)
        => new() { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(value) };

    private static RuleNode TargetSelf()
        => new() { Type = "TTargetPlayerRelative", TargetMode = "Self" };

    private static RuleNode TargetOpponent()
        => new() { Type = "TTargetPlayerRelative", TargetMode = "Opponent" };

    private static CardInstance CreateSimpleActiveItem(
        string id,
        int cooldownMs,
        RuleNode action,
        string? enchantment = null,
        Dictionary<string, double>? extraAttrs = null)
    {
        var attrs = new Dictionary<string, double> { ["CooldownMax"] = cooldownMs };
        if (extraAttrs != null)
        {
            foreach (var (k, v) in extraAttrs) attrs[k] = v;
        }

        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = attrs,
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = action
                }
            }
        };
        return new CardInstance(tpl, CardTier.Bronze, enchantment);
    }

    private static CardInstance CreateStartFightItem(string id, RuleNode action)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = action
                }
            }
        };
        return new CardInstance(tpl);
    }
}
