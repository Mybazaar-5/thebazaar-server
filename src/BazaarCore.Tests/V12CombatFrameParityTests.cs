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
/// V12 战斗逐帧对齐与 200 场真实天梯对局交叉验证测试套件：
/// 对照 BazaarCardDealer.cs 与 GameData.db (game_modes) 核验帧内执行顺序、状态衰减、
/// OverHeal、Sandstorm（含提前开启与单次启动触发）、Enrage、ReviveHeal、亡语保留与确定性 ICD 队列。
/// </summary>
public sealed class V12CombatFrameParityTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private readonly ITestOutputHelper _output;

    private const string HealthyJoltId = "721fa7ca-3451-4bbd-ac17-ffcff4a31ac6";
    private const string BottledTornadoId = "c8a322c7-a8f9-49e3-90e5-3a348d0fe5fc";
    private const string WindmillId = "ff06b63a-15ee-49be-80b1-5fb52b4e1616";
    private const string HunkerDownId = "369a6704-2727-4cea-be27-01b76c82df45";
    private const string FieryRebirthId = "09c59ebf-d2f9-4862-9c30-396b9ddfdd89";
    private const string MementoMoriId = "34618f7b-f262-4d49-b395-de92bef0508c";
    private const string ForestCloakId = "f718a14b-e8bd-4ee6-b9a9-63f15de5f242";
    private const string FrogHollowId = "0df9aa59-6b34-4968-94f0-98af5e22aa7a";
    private const string BandoleerId = "37c65de1-3027-43b1-923b-e9b675222123";
    private const string SittingDuckId = "b18ca39c-8aa4-40c1-a687-524380b64370";
    private const string GrabinatorId = "bd46ceff-a181-49f2-ac60-a769a062cb6d";
    private const string BoxOfFireworksId = "6f65b541-ec4e-4475-8ca1-fa8546aa2477";

    public V12CombatFrameParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FrameParity_HasteSlowFreezeAndAmmoZeroReset_MatchesDealerOrder()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 5000);

        // 1. 单发弹药枪（CD=1000ms, AmmoMax=1）：980ms 开火并耗尽弹药
        var gun = CreateSimpleActiveItem(
            "ammo_zero_gun",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) },
            extraAttrs: new() { ["AmmoMax"] = 1, ["DamageAmount"] = 10 });
        gun.StartSlot = 0;

        // 2. 1200ms 时仅对最左侧（已空弹的 gun）施加 1000ms Haste 与 2000ms Charge（均应在 Ammo==0 期间归零进度且 Haste 计时正常消耗，同时不误充能 reloader）
        var helper = CreateSimpleActiveItem(
            "ammo_helper",
            cooldownMs: 1200,
            new RuleNode
            {
                Type = "TActionAnd",
                Actions =
                [
                    new RuleNode
                    {
                        Type = "TActionCardHaste",
                        Target = new RuleNode
                        {
                            Type = "TTargetCardXMost",
                            TargetSection = "SelfHand",
                            TargetMode = "LeftMostCard",
                            RawConditionsElement = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$type"] = "TCardConditionalId", ["Id"] = "ammo_zero_gun", ["IsNot"] = false })
                        },
                        ReferenceValue = Fixed(1000)
                    },
                    new RuleNode
                    {
                        Type = "TActionCardCharge",
                        Target = new RuleNode
                        {
                            Type = "TTargetCardXMost",
                            TargetSection = "SelfHand",
                            TargetMode = "LeftMostCard",
                            RawConditionsElement = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$type"] = "TCardConditionalId", ["Id"] = "ammo_zero_gun", ["IsNot"] = false })
                        },
                        ReferenceValue = Fixed(2000)
                    }
                ]
            },
            extraAttrs: new() { ["AmmoMax"] = 1 });
        helper.StartSlot = 1;

        // 3. 2500ms 时为 gun 补弹 1 发：因为缺弹期间已重置冷却进度为 0 且 Haste 已消耗完毕，gun 需从 0 读条完整 1000ms 才能开第 2 枪
        var reloader = CreateSimpleActiveItem(
            "late_reloader",
            cooldownMs: 2500,
            new RuleNode
            {
                Type = "TActionCardReload",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
                ReferenceValue = Fixed(1)
            },
            extraAttrs: new() { ["AmmoMax"] = 1 });
        reloader.StartSlot = 2;

        p1.Hand.TryAddAuto(gun);
        p1.Hand.TryAddAuto(helper);
        p1.Hand.TryAddAuto(reloader);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1201, recordTimeline: true, recordEvents: true);
        var gunFires = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == gun.InstanceId).ToList();

        Assert.Equal(2, gunFires.Count);
        Assert.Equal(950, gunFires[0].TimeMs);

        // 1300ms 时（缺弹期）冷却进度必须已被清零，且 Haste 正在正常倒计时（< 1000ms）
        var snap1300 = res.Timeline.First(t => t.TimeMs == 1300).Cards.First(c => c.InstanceId == gun.InstanceId);
        Assert.Equal(0, snap1300.Ammo);
        Assert.Equal(0f, snap1300.Progress01);
        Assert.True(snap1300.HasteMs < 1000 && snap1300.HasteMs > 0, $"缺弹期间 Haste 应正常扣减，实际 {snap1300.HasteMs}ms");

        // 2480ms 补弹后，gun 不能瞬间利用滞留 Charge 开火，必须在 >= 3400ms 完成完整 1000ms 冷却后才开第 2 枪
        Assert.True(gunFires[1].TimeMs >= 3400, $"缺弹重置冷却后补弹不应残留旧充能，实际第 2 枪时间 {gunFires[1].TimeMs}ms");
    }

    [Fact]
    public void FrameParity_PoisonAndBurn_ExactPeriodsShieldMitigationAndDecay()
    {
        var repo = Repo.Value;
        Assert.Equal(3, repo.GameMode.BurnDecrementByAsPercentage);

        // 1. Burn 500ms 周期、护盾减免 min(Shield, (Burn+1)/2)、3% 衰减（向下取整至少 1 点）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 2000);
        p1.Hand.TryAddAuto(CreateStartFightItem("burn_setup", new RuleNode
        {
            Type = "TActionPlayerBurnApply",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(100)
        }));
        p2.Hand.TryAddAuto(CreateStartFightItem("shield_setup", new RuleNode
        {
            Type = "TActionPlayerShieldApply",
            Target = TargetSelf(),
            ReferenceValue = Fixed(300)
        }));

        var burnRes = CombatSimulator.Simulate(repo, p1, p2, seed: 1202);
        var burnTicks = burnRes.Events.Where(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "p2").Take(4).ToList();
        Assert.Equal(4, burnTicks.Count);
        Assert.Equal(500, burnTicks[0].TimeMs);
        Assert.Equal(1000, burnTicks[1].TimeMs);
        Assert.Equal(1500, burnTicks[2].TimeMs);
        Assert.Equal(2000, burnTicks[3].TimeMs);

        // 100 -> dmg 50 (Shield 250), decay 3 -> 97 -> dmg 48 (Shield 202), decay 2 -> 95 -> dmg 47, decay 2 -> 93 -> dmg 46
        // BurnTick 事件快照记录当前跳结算前瞬间的 Burn 层数，下一跳即可观测到衰减结果（100 -> 97 -> 95 -> 93）
        Assert.Equal(50, (int)burnTicks[0].Value);
        Assert.Equal(250, burnTicks[0].Player2Shield);
        Assert.Equal(100, burnTicks[0].Player2Burn);

        Assert.Equal(48, (int)burnTicks[1].Value);
        Assert.Equal(202, burnTicks[1].Player2Shield);
        Assert.Equal(97, burnTicks[1].Player2Burn);

        Assert.Equal(47, (int)burnTicks[2].Value);
        Assert.Equal(95, burnTicks[2].Player2Burn);
        Assert.Equal(46, (int)burnTicks[3].Value);
        Assert.Equal(93, burnTicks[3].Player2Burn);

        // 2. Poison 1000ms 周期、完全穿透护盾、同帧与 Regen 抵消净值
        var pp1 = CreatePlayer("pp1", HeroNames.Mak, 2000);
        var pp2 = CreatePlayer("pp2", HeroNames.Pygmalien, 500);
        pp1.Hand.TryAddAuto(CreateStartFightItem("poison_setup", new RuleNode
        {
            Type = "TActionPlayerPoisonApply",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(30)
        }));
        pp2.Hand.TryAddAuto(CreateStartFightItem("shield_regen_setup", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(500) },
                new RuleNode { Type = "TActionPlayerRegenApply", Target = TargetSelf(), ReferenceValue = Fixed(12) }
            ]
        }));

        var poisonRes = CombatSimulator.Simulate(repo, pp1, pp2, seed: 1203, recordTimeline: true, recordEvents: true);
        var pTick1 = poisonRes.Events.First(e => e.Type == CombatEventType.PoisonTick && e.TargetPlayerId == "pp2");
        var rTick1 = poisonRes.Events.First(e => e.Type == CombatEventType.RegenTick && e.TargetPlayerId == "pp2");
        Assert.Equal(1000, pTick1.TimeMs);
        Assert.Equal(1000, rTick1.TimeMs);
        Assert.Equal(30, (int)pTick1.Value);
        // 1000ms 末净伤 = 30 Poison - 12 Regen = 18，护盾维持 500 不变
        var snap1000 = poisonRes.Timeline.First(t => t.TimeMs == 1000);
        Assert.Equal(482, snap1000.P2Hp);
        Assert.Equal(500, snap1000.P2Shield);
    }

    [Fact]
    public void FrameParity_HealthRegen_AndOverHealTrigger_AtOrReachingHealthMax()
    {
        var repo = Repo.Value;
        var healthyJoltTpl = repo.GetCard(HealthyJoltId)!;

        // 1. 满血状态下治疗（at HealthMax）触发真实 Healthy Jolt（且仅触发 1 次，不被后续连续 OverHeal 重入）
        var p1 = CreatePlayer("p1", HeroNames.Pygmalien, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        p1.Skills.Add(new CardInstance(healthyJoltTpl, CardTier.Silver)); // 首次 OverHeal 加速全物品 1000ms
        var targetWeapon = CreateSimpleActiveItem("w_target", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) });
        p1.Hand.TryAddAuto(targetWeapon);
        p1.Hand.TryAddAuto(CreateSimpleActiveItem("full_healer", 1000, new RuleNode
        {
            Type = "TActionPlayerHeal",
            Target = TargetSelf(),
            ReferenceValue = Fixed(50)
        }));

        var fullRes = CombatSimulator.Simulate(repo, p1, p2, seed: 1204);
        var hasteEvents = fullRes.Events.Where(e => e.Type == CombatEventType.HasteApplied && e.TargetCardInstanceId == targetWeapon.InstanceId).ToList();
        Assert.Single(hasteEvents);
        Assert.Equal(950, hasteEvents[0].TimeMs);
        Assert.Equal(1000, (int)hasteEvents[0].Value);

        // 2. 未满血时治疗恰好补满 HealthMax（reaching HealthMax: 1000 - 100 + 100 = 1000）应触发 OverHeal
        var rp1 = CreatePlayer("rp1", HeroNames.Pygmalien, 1000);
        var rp2 = CreatePlayer("rp2", HeroNames.Vanessa, 2000);
        rp1.Skills.Add(new CardInstance(healthyJoltTpl, CardTier.Gold));
        var reachWeapon = CreateSimpleActiveItem("w_reach", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) });
        rp1.Hand.TryAddAuto(reachWeapon);
        rp2.Hand.TryAddAuto(CreateStartFightItem("chip_100", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(100)
        }));
        rp1.Hand.TryAddAuto(CreateSimpleActiveItem("exact_healer", 1000, new RuleNode
        {
            Type = "TActionPlayerHeal",
            Target = TargetSelf(),
            ReferenceValue = Fixed(100)
        }));

        var reachRes = CombatSimulator.Simulate(repo, rp1, rp2, seed: 1205);
        var reachHaste = reachRes.Events.Where(e => e.Type == CombatEventType.HasteApplied && e.TargetCardInstanceId == reachWeapon.InstanceId).ToList();
        Assert.Single(reachHaste);

        // 3. 治疗后仍未达到 HealthMax（1000 - 200 + 50 = 850 < 1000）不得触发 OverHeal
        var up1 = CreatePlayer("up1", HeroNames.Pygmalien, 1000);
        var up2 = CreatePlayer("up2", HeroNames.Vanessa, 2000);
        up1.Skills.Add(new CardInstance(healthyJoltTpl, CardTier.Gold));
        up1.Hand.TryAddAuto(CreateSimpleActiveItem("w_under", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) }));
        up2.Hand.TryAddAuto(CreateStartFightItem("chip_300", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(300)
        }));
        up1.Hand.TryAddAuto(CreateSimpleActiveItem("small_healer", 1000, new RuleNode
        {
            Type = "TActionPlayerHeal",
            Target = TargetSelf(),
            ReferenceValue = Fixed(50)
        }, extraAttrs: new() { ["AmmoMax"] = 1 }));

        var underRes = CombatSimulator.Simulate(repo, up1, up2, seed: 1206);
        Assert.DoesNotContain(underRes.Events, e => e.Type == CombatEventType.HasteApplied);
    }

    [Fact]
    public void FrameParity_Sandstorm_RampUpSingleStartTriggerAndBeginSandstorm()
    {
        var repo = Repo.Value;
        var windmillTpl = repo.GetCard(WindmillId)!;
        var hunkerDownTpl = repo.GetCard(HunkerDownId)!;
        var bottledTornadoTpl = repo.GetCard(BottledTornadoId)!;

        // 1. 自然沙暴（30,000ms 启动，P1-11 对齐官方 BazaarCardDealer.cs:4243-4274）：
        // 验证 20s 预警、30s 启动、间隔 250->150ms (-10ms/跳，降频期间伤害维持 1)、触底 150ms 后每跳 +2 伤害，以及 Windmill/Hunker Down 仅触发一次
        var p1 = CreatePlayer("p1", HeroNames.Pygmalien, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 1000);
        p1.Hand.TryAddAuto(new CardInstance(windmillTpl, CardTier.Gold)); // CD 35s，故在 30s 先被自然沙暴触发 TTriggerOnSandstorm（MaxHealth x2）
        p1.Skills.Add(new CardInstance(hunkerDownTpl, CardTier.Gold));    // TTriggerOnSandstorm -> PercentDamageReduction +50%

        var natRes = CombatSimulator.Simulate(repo, p1, p2, seed: 1207, recordTimeline: true, recordEvents: true);
        var warning = natRes.Events.First(e => e.Type == CombatEventType.SandstormWarning);
        var ticks = natRes.Events.Where(e => e.Type == CombatEventType.SandstormTick).ToList();

        Assert.Equal(20000, warning.TimeMs);
        Assert.Equal(30000, ticks[0].TimeMs);
        Assert.True(ticks.Count >= 13);
        for (int i = 0; i <= 9; i++)
            Assert.Equal(1, (int)ticks[i].Value);
        Assert.Equal(3, (int)ticks[10].Value);
        Assert.Equal(5, (int)ticks[11].Value);
        Assert.Equal(7, (int)ticks[12].Value);

        // 验证官方 50ms 累加器间隔（P1-50：首跳同帧预加 50ms 故第 2 跳在 30200ms 间距 200ms，第 11 跳以后收敛到 150ms）
        Assert.Equal(200, ticks[1].TimeMs - ticks[0].TimeMs);
        int lateGap = ticks[11].TimeMs - ticks[10].TimeMs;
        Assert.Equal(150, lateGap);

        // Windmill 仅应在沙暴启动时将 1000 MaxHP 翻倍一次到 2000（绝非每跳翻倍指数爆炸）
        var snap30s = natRes.Timeline.First(t => t.TimeMs == 30000);
        var snap32s = natRes.Timeline.First(t => t.TimeMs == 32000);
        Assert.Equal(2000, snap30s.P1MaxHp);
        Assert.Equal(2000, snap32s.P1MaxHp);

        // Hunker Down 仅叠加 50% PercentDamageReduction（在首跳后持续生效，使 p1 受到的沙暴伤害显著低于 p2）
        int p1Lost = 2000 - snap32s.P1Hp;
        int p2Lost = 1000 - snap32s.P2Hp;
        Assert.True(p1Lost < p2Lost, $"Hunker Down 50% 减伤应使 P1 掉血({p1Lost})明显少于 P2({p2Lost})");

        // 2. 提前沙暴（Bottled Tornado TActionCardBeginSandstorm @ ~5000ms）：
        // 验证提前触发沙暴、仅触发一次 TTriggerOnSandstorm、且 TRunConditionalSandstorm 即刻变为 true
        var ep1 = CreatePlayer("ep1", HeroNames.Vanessa, 1000);
        var ep2 = CreatePlayer("ep2", HeroNames.Vanessa, 1000);
        ep1.Hand.TryAddAuto(new CardInstance(bottledTornadoTpl, CardTier.Silver)); // CD 5000ms
        ep1.Hand.TryAddAuto(new CardInstance(windmillTpl, CardTier.Gold));
        var condCardTpl = new CardTemplate
        {
            Id = "sandstorm_cond_probe",
            InternalName = "Sandstorm Cond Probe",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = new Dictionary<string, double> { ["CooldownMax"] = 6000 },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["0"] = new()
                {
                    Id = "0",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Prerequisites =
                    [
                        new RuleNode { Type = "TRunConditionalSandstorm", IsActive = true }
                    ],
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(77)
                    }
                }
            }
        };
        ep1.Hand.TryAddAuto(new CardInstance(condCardTpl));

        var earlyRes = CombatSimulator.Simulate(repo, ep1, ep2, seed: 1208, recordTimeline: true, recordEvents: true);
        var firstEarlyTick = earlyRes.Events.First(e => e.Type == CombatEventType.SandstormTick);
        Assert.InRange(firstEarlyTick.TimeMs, 4950, 5020);
        Assert.Equal(1, (int)firstEarlyTick.Value);

        var snapEarly = earlyRes.Timeline.First(t => t.TimeMs >= firstEarlyTick.TimeMs);
        Assert.Equal(2000, snapEarly.P1MaxHp); // Windmill 只翻倍一次
        Assert.True(earlyRes.Player1TotalShieldGained >= 77, "提前沙暴后 TRunConditionalSandstorm 应在 30s 前立即判定为 true");
    }

    [Fact]
    public void FrameParity_Enraged_LifecycleAttributesAurasAndDuration()
    {
        var repo = Repo.Value;
        var bandoleerTpl = repo.GetCard(BandoleerId)!;
        var forestCloakTpl = repo.GetCard(ForestCloakId)!;
        var frogHollowTpl = repo.GetCard(FrogHollowId)!;

        // 构造快速叠加 Rage 的测试：每 1000ms 施加 100 点 Rage 触发 Enrage，持续 5000ms 后触发 EnrageEnded
        var p1 = CreatePlayer("p1", HeroNames.Karnok, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);

        p1.Hand.TryAddAuto(CreateStartFightItem("rage_starter", new RuleNode
        {
            Type = "TActionPlayerRageApply",
            Target = TargetSelf(),
            ReferenceValue = Fixed(100)
        }));
        p1.Hand.TryAddAuto(new CardInstance(forestCloakTpl, CardTier.Silver)); // While Enraged -> 50% DR
        p1.Hand.TryAddAuto(new CardInstance(frogHollowTpl, CardTier.Silver));  // EnrageEnded -> Haste friends 3s
        p1.Hand.TryAddAuto(new CardInstance(bandoleerTpl, CardTier.Silver));   // Enraged & EnrageEnded -> Reload
        var hasteReceiver = CreateSimpleActiveItem("receiver_ammo", 4000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(10)
        }, extraAttrs: new() { ["AmmoMax"] = 1 });
        p1.Hand.TryAddAuto(hasteReceiver);

        // 对手每 2000ms 造成 100 伤害：2000ms(狂暴期间应减半为 50), 6000ms(狂暴已结束应受满额 100)
        p2.Hand.TryAddAuto(CreateSimpleActiveItem("opp_hitter", 2000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(100)
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 1209, recordTimeline: true, recordEvents: true);
        var enrageStart = res.Events.First(e => e.Type == CombatEventType.PlayerEnraged && e.TargetPlayerId == "p1");
        var enrageEnd = res.Events.First(e => e.Type == CombatEventType.PlayerEnrageEnded && e.TargetPlayerId == "p1");

        Assert.Equal(0, enrageStart.TimeMs);
        Assert.Equal(4950, enrageEnd.TimeMs); // 100 ticks * 50ms = 5000ms 完整持续窗口 (0..4950ms)

        var snap1000 = res.Timeline.First(t => t.TimeMs == 1000);
        var snap5500 = res.Timeline.First(t => t.TimeMs == 5500);
        Assert.True(snap1000.P1Enraged);
        Assert.False(snap5500.P1Enraged);

        var oppHits = res.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p2").ToList();
        Assert.Equal(50, (int)oppHits[0].Value);  // 1950ms 狂暴期间受 Forest Cloak 50% 减伤
        Assert.Equal(100, (int)oppHits[2].Value); // 5950ms 狂暴结束后恢复 100 全额伤害

        // Frog Hollow 在 TTriggerOnPlayerEnrageEnded (4950ms) 后应对全队施加 Haste
        Assert.Contains(res.Events, e => e.Type == CombatEventType.HasteApplied && e.TimeMs >= 4950);
    }

    [Fact]
    public void FrameParity_ReviveHealAndLethalPrevention_RealCardsAndInvulnerability()
    {
        var repo = Repo.Value;
        var mementoTpl = repo.GetCard(MementoMoriId)!;
        var fieryRebirthTpl = repo.GetCard(FieryRebirthId)!;

        // 1. 真实 Memento Mori（Gold）：濒死复活至 1 HP 并获得 1000ms 的 100% PercentDamageReduction（0 穿伤）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 100);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        p1.Hand.TryAddAuto(new CardInstance(mementoTpl, CardTier.Gold));
        // P2 每 400ms (用 1000ms CD + 250ms 3连击) 在致死后继续追击，验证 1000ms 无敌窗口内 1 HP 绝不被 90% 上限漏伤击穿
        p2.Hand.TryAddAuto(CreateSimpleActiveItem("multi_killer", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(150)
        }, extraAttrs: new() { ["Multicast"] = 3 }));

        var memRes = CombatSimulator.Simulate(repo, p1, p2, seed: 1210, recordTimeline: true, recordEvents: true);
        // 950ms 第一发致死触发 Memento Mori 复活至 1 HP，950ms..1950ms 内后续多发全部受 100% DR 变为 0 伤害，直到 1950ms 无敌到期才被后续击杀
        var zeroHitsAfterRevive = memRes.Events.Where(e => e.Type == CombatEventType.DamageApplied && e.SourcePlayerId == "p2" && e.TimeMs > 950 && e.TimeMs < 1950).ToList();
        Assert.NotEmpty(zeroHitsAfterRevive);
        Assert.All(zeroHitsAfterRevive, h => Assert.Equal(0, (int)h.Value));
        Assert.True(memRes.DurationMs >= 1950, $"Memento Mori 1000ms 无敌应保命至少到 1950ms，实际 {memRes.DurationMs}ms");

        // 2. 真实 Fiery Rebirth（Diamond）：首次濒死回满全部 HealthMax 并反杀，整场仅触发一次
        var fp1 = CreatePlayer("fp1", HeroNames.Vanessa, 100);
        var fp2 = CreatePlayer("fp2", HeroNames.Vanessa, 150);
        fp1.Skills.Add(new CardInstance(fieryRebirthTpl, CardTier.Diamond));
        fp1.Hand.TryAddAuto(CreateSimpleActiveItem("slow_finisher", 1500, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(200)
        }));
        fp2.Hand.TryAddAuto(CreateSimpleActiveItem("fast_burst", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(120)
        }));

        var rebirthRes = CombatSimulator.Simulate(repo, fp1, fp2, seed: 1211);
        Assert.Equal("fp1", rebirthRes.WinnerPlayerId);
        Assert.Equal(100, rebirthRes.Player1RemainingHp);
    }

    [Fact]
    public void FrameParity_BeforeDestroyed_ImmediateChainsAndSittingDuckExpiry()
    {
        var repo = Repo.Value;

        // 1. Grabinator 同触发器 Priority=Immediate 状态递增 + Dependent Prerequisite(Custom_0 > 0) 联动测试
        // 保留原生 GrabinatorId（使 SpawnQueryEngine 在 TriggerTargetCard=Self 或 TargetCard 时均可命中真卡池），合并至 Gold 属性且不设 Tiers.AbilityIds 过滤器，使附加的 destroy_med 一同生效
        var grabinatorTpl = repo.GetCard(GrabinatorId)!;
        var mergedGrabAttrs = new Dictionary<string, double>(grabinatorTpl.Attributes ?? new Dictionary<string, double>());
        if (grabinatorTpl.Tiers != null)
        {
            foreach (CardTier t in Enum.GetValues<CardTier>())
            {
                if (t > CardTier.Gold) break;
                if (grabinatorTpl.Tiers.TryGetValue(t.ToString(), out var td))
                {
                    foreach (var (k, v) in td.Attributes)
                        mergedGrabAttrs[k] = v;
                }
            }
        }
        mergedGrabAttrs["Custom_0"] = 0;

        var mergedGrabAbilities = new Dictionary<string, AbilityDefinition>(grabinatorTpl.Abilities)
        {
            ["destroy_med"] = new()
            {
                Id = "destroy_med",
                Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                Action = new RuleNode
                {
                    Type = "TActionCardDestroy",
                    Target = new RuleNode
                    {
                        Type = "TTargetCardSection",
                        TargetSection = "OpponentHand",
                        RawConditionsElement = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                        {
                            ["$type"] = "TCardConditionalSize",
                            ["Sizes"] = new[] { "Medium" }
                        })
                    }
                }
            }
        };
        var syntheticGrabDriver = new CardTemplate
        {
            Id = grabinatorTpl.Id,
            InternalName = grabinatorTpl.InternalName,
            Type = grabinatorTpl.Type,
            Size = grabinatorTpl.Size,
            StartingTier = grabinatorTpl.StartingTier,
            Heroes = grabinatorTpl.Heroes,
            Tags = grabinatorTpl.Tags,
            HiddenTags = grabinatorTpl.HiddenTags,
            Tiers = null,
            Enchantments = grabinatorTpl.Enchantments,
            Auras = grabinatorTpl.Auras,
            Attributes = mergedGrabAttrs,
            Abilities = mergedGrabAbilities
        };

        var pA = CreatePlayer("pa", HeroNames.Stelle, 2000);
        var pB = CreatePlayer("pb", HeroNames.Vanessa, 2000);
        var driver = new CardInstance(syntheticGrabDriver, CardTier.Gold);
        pA.Hand.TryAddAuto(driver);

        // 使用真实 GameData.db Medium 卡（Bass）作为被销毁目标
        var enemyMedTpl = repo.GetCard("04312f35-0ac9-4fd0-9118-6367be0d4690")!;
        pB.Hand.TryAddAuto(new CardInstance(enemyMedTpl, CardTier.Gold));

        var grabRes = CombatSimulator.Simulate(repo, pA, pB, seed: 1212);
        // Priority=Immediate ("0","1") 必须先将 Custom_0 递增为 >0，使后置 Immediate ("2") 的 Custom_0 > 0 前置实时通过并触发变形
        Assert.Contains(grabRes.Events, e => e.Type == CombatEventType.CardTransformed && e.SourceCardInstanceId == driver.InstanceId);

        // 2. Box of Fireworks 亡语排序：Priority=Medium 的 Burn 与 Priority=Highest 的 TActionCardTransformDestroyed 同在 TTriggerOnBeforeCardDestroyed 上均须成功执行
        var bofTpl = repo.GetCard(BoxOfFireworksId)!;
        var bp1 = CreatePlayer("bp1", HeroNames.Vanessa, 2000);
        var bp2 = CreatePlayer("bp2", HeroNames.Dooley, 2000);
        var bof = new CardInstance(bofTpl, CardTier.Silver); // Silver BurnApplyAmount = 12
        bp1.Hand.TryAddAuto(bof);
        bp2.Hand.TryAddAuto(CreateStartFightItem("bof_destroyer", new RuleNode
        {
            Type = "TActionCardDestroy",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        }));

        var bofRes = CombatSimulator.Simulate(repo, bp1, bp2, seed: 1213);
        Assert.Contains(bofRes.Events, e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == bof.InstanceId && (int)e.Value == 12);
        Assert.Contains(bofRes.Events, e => e.Type == CombatEventType.CardTransformed && e.SourceCardInstanceId == bof.InstanceId);

        // 3. 真实 Sitting Duck：TTriggerOnFightStarted 挂 5000ms TCombatDuration Custom_8，5000ms 到期失活触发 TActionCardDisable 自毁
        var duckTpl = repo.GetCard(SittingDuckId)!;
        var dp1 = CreatePlayer("dp1", HeroNames.Vanessa, 3000);
        var dp2 = CreatePlayer("dp2", HeroNames.Vanessa, 3000);
        var duck = new CardInstance(duckTpl, CardTier.Diamond);
        dp1.Hand.TryAddAuto(duck);
        // 附带一张监听 TTriggerOnCardDisabled 的测试卡核实 5000ms 时 Sitting Duck 确实进入 Disabled 状态
        var disableWatcher = new CardTemplate
        {
            Id = "disable_watcher",
            InternalName = "Disable Watcher",
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
                        Type = "TTriggerOnCardDisabled",
                        Subject = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                            RawConditionsElement = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                            {
                                ["$type"] = "TCardConditionalOr",
                                ["Conditions"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["$type"] = "TCardConditionalState",
                                        ["States"] = new[] { "Disabled" }
                                    },
                                    new Dictionary<string, object?>
                                    {
                                        ["$type"] = "TCardConditionalState",
                                        ["States"] = new[] { "Destroyed" }
                                    }
                                }
                            })
                        }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = TargetSelf(),
                        ReferenceValue = Fixed(55)
                    }
                }
            }
        };
        dp1.Hand.TryAddAuto(new CardInstance(disableWatcher));

        var duckRes = CombatSimulator.Simulate(repo, dp1, dp2, seed: 1214);
        var duckExpired = duckRes.Events.FirstOrDefault(e =>
            e.Type == CombatEventType.CardAttributeModified &&
            e.TargetCardInstanceId == duck.InstanceId &&
            e.Detail == "Custom_8" &&
            e.Value < 0);
        Assert.NotNull(duckExpired);
        Assert.True(duckExpired!.TimeMs >= 5000, $"Sitting Duck Custom_8 应在 >= 5000ms 到期，实际 {duckExpired.TimeMs}ms");

        var disableShield = duckRes.Events.FirstOrDefault(e => e.Type == CombatEventType.ShieldApplied && (int)e.Value == 55);
        Assert.NotNull(disableShield);
        Assert.True(disableShield!.TimeMs >= 5000, $"Sitting Duck 应在 5000ms 时限到期后才触发 Disable，实际 {disableShield.TimeMs}ms");
    }

    [Fact]
    public void FrameParity_DeterministicIcdOrdering_AcrossRepeatedRuns()
    {
        var repo = Repo.Value;

        PlayerState BuildSide(string id)
        {
            var s = CreatePlayer(id, HeroNames.Dooley, 3000);
            var c1 = CreateStartFightItem(id + "_slot4", new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(15) });
            c1.StartSlot = 4;
            var c2 = CreateStartFightItem(id + "_slot1", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(20) });
            c2.StartSlot = 1;
            // 故意乱序添加
            s.Hand.Cards.Add(c1);
            s.Hand.Cards.Add(c2);
            return s;
        }

        var run1 = CombatSimulator.Simulate(repo, BuildSide("p1"), BuildSide("p2"), seed: 777, recordTimeline: true, recordEvents: true);
        var run2 = CombatSimulator.Simulate(repo, BuildSide("p1"), BuildSide("p2"), seed: 777, recordTimeline: true, recordEvents: true);

        Assert.Equal(run1.Events.Count, run2.Events.Count);
        for (int i = 0; i < run1.Events.Count; i++)
        {
            Assert.Equal(run1.Events[i].TimeMs, run2.Events[i].TimeMs);
            Assert.Equal(run1.Events[i].Type, run2.Events[i].Type);
            Assert.Equal(run1.Events[i].SourceCardInstanceId, run2.Events[i].SourceCardInstanceId);
            Assert.Equal(run1.Events[i].Value, run2.Events[i].Value);
        }

        // t=0 的触发顺序严格遵循：P1 StartSlot=1 (Shield) -> P1 StartSlot=4 (Damage) -> P2 StartSlot=1 -> P2 StartSlot=4
        var t0Actions = run1.Events.Where(e => e.TimeMs == 0 && e.Type is CombatEventType.ShieldApplied or CombatEventType.DamageApplied).ToList();
        Assert.Equal(4, t0Actions.Count);
        Assert.Equal("p1_slot1", t0Actions[0].SourceCardInstanceId);
        Assert.Equal("p1_slot4", t0Actions[1].SourceCardInstanceId);
        Assert.Equal("p2_slot1", t0Actions[2].SourceCardInstanceId);
        Assert.Equal("p2_slot4", t0Actions[3].SourceCardInstanceId);
    }

    [Fact]
    public void CrossValidation_200RealLadderMatchups_BitExactDeterministicAndCalibrated()
    {
        var repo = Repo.Value;
        string compassPath = ResolveCompassDistilledPath();
        Assert.True(File.Exists(compassPath), $"未找到只读天梯数据文件: {compassPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(compassPath));
        var root = doc.RootElement;

        var calibrationMedians = new Dictionary<int, (int MedianHp, double MinSec, double MaxSec, double MedianSec)>();
        var calEl = root.GetProperty("calibration");
        foreach (int day in new[] { 4, 7, 10 })
        {
            var entries = calEl.GetProperty(day.ToString()).EnumerateArray().ToList();
            var hps = entries.Select(e => e.GetProperty("healthMax").GetInt32()).OrderBy(x => x).ToList();
            var secs = entries.Select(e => e.GetProperty("secMedian").GetDouble()).OrderBy(x => x).ToList();
            calibrationMedians[day] = (hps[hps.Count / 2], secs.First(), secs.Last(), secs[secs.Count / 2]);
        }

        // 读取真实天梯 anchors（Day 10 完整配置含附魔、属性覆盖与技能）
        var ladderAnchors = root.GetProperty("anchors").EnumerateArray()
            .Where(a => string.Equals(a.GetProperty("kind").GetString(), "ladder", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(ladderAnchors.Count >= 160, $"天梯 anchors 数量不足: {ladderAnchors.Count}");

        // 读取真实天梯 boards（按 d4 / d7 / d10 分类）
        var boardsByPeriod = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal)
        {
            ["d4"] = new(),
            ["d7"] = new(),
            ["d10"] = new()
        };
        foreach (var b in root.GetProperty("boards").EnumerateArray())
        {
            string? period = b.GetProperty("period").GetString();
            if (period != null && boardsByPeriod.TryGetValue(period, out var list))
                list.Add(b);
        }

        var matchups = new List<(string Label, int Day, PlayerState P1, PlayerState P2, int Seed)>(200);

        // 80 场 Day 10 完整天梯 Anchors 对局（覆盖多英雄交叉对抗、属性覆盖、附魔与技能）
        for (int i = 0; i < 80; i++)
        {
            var a1 = ladderAnchors[(i * 3) % ladderAnchors.Count];
            var a2 = ladderAnchors[(i * 3 + 47) % ladderAnchors.Count];
            var p1 = BuildPlayerFromAnchor(a1, repo, $"anchor_p1_{i}", day: 10);
            var p2 = BuildPlayerFromAnchor(a2, repo, $"anchor_p2_{i}", day: 10);
            matchups.Add(($"Anchor_D10_{i}", 10, p1, p2, 12_000 + i));
        }

        // 各 40 场 Day 4 / Day 7 / Day 10 天梯 Boards 对局（使用 calibration 对应天数的健康上限）
        foreach (var (day, periodKey, count, offset) in new[]
        {
            (4, "d4", 40, 100),
            (7, "d7", 40, 200),
            (10, "d10", 40, 300)
        })
        {
            var pool = boardsByPeriod[periodKey];
            int dayHp = calibrationMedians[day].MedianHp;
            for (int i = 0; i < count; i++)
            {
                var b1 = pool[(i * 7) % pool.Count];
                var b2 = pool[(i * 7 + 193) % pool.Count];
                var p1 = BuildPlayerFromBoard(b1, repo, $"board_d{day}_p1_{i}", day, dayHp);
                var p2 = BuildPlayerFromBoard(b2, repo, $"board_d{day}_p2_{i}", day, dayHp);
                matchups.Add(($"Board_D{day}_{i}", day, p1, p2, 12_000 + offset + i));
            }
        }

        Assert.Equal(200, matchups.Count);

        var sw = Stopwatch.StartNew();
        var durationsByDay = new Dictionary<int, List<double>>
        {
            [4] = new(),
            [7] = new(),
            [10] = new()
        };
        var allDurationsSec = new List<double>(200);
        int p1Wins = 0;

        // 200 场独立对局（每场各自持有全新 p1/p2 实例与确定性种子），并发执行后按原下标汇总保证结果逐位等价
        var matchOutcomes = new (int Day, double Sec, bool P1Won)[matchups.Count];
        Parallel.For(
            0,
            matchups.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
            i =>
            {
                var (label, day, p1, p2, seed) = matchups[i];
                var runA = CombatSimulator.Simulate(repo, p1, p2, seed: seed, combatType: "PVP", recordTimeline: false, recordEvents: false);
                var runB = CombatSimulator.Simulate(repo, p1, p2, seed: seed, combatType: "PVP", recordTimeline: false, recordEvents: false);

                // 1. 100% 比特级确定性断言
                Assert.Equal(runA.WinnerPlayerId, runB.WinnerPlayerId);
                Assert.Equal(runA.LoserPlayerId, runB.LoserPlayerId);
                Assert.Equal(runA.IsDraw, runB.IsDraw);
                Assert.Equal(runA.DurationMs, runB.DurationMs);
                Assert.Equal(runA.Player1RemainingHp, runB.Player1RemainingHp);
                Assert.Equal(runA.Player2RemainingHp, runB.Player2RemainingHp);
                Assert.Equal(runA.Player1TotalDamageDealt, runB.Player1TotalDamageDealt);
                Assert.Equal(runA.Player2TotalDamageDealt, runB.Player2TotalDamageDealt);
                Assert.Equal(runA.Player1TotalShieldGained, runB.Player1TotalShieldGained);
                Assert.Equal(runA.Player2TotalShieldGained, runB.Player2TotalShieldGained);

                // 2. 零 NaN / Infinity / 负血量 / 死循环断言
                Assert.False(string.IsNullOrEmpty(runA.WinnerPlayerId), $"{label} 缺少胜者");
                Assert.False(runA.IsDraw, $"{label} 不应返回 IsDraw=true");
                Assert.InRange(runA.DurationMs, 0, CombatSimulator.MaxDurationMs);
                Assert.True(runA.Player1RemainingHp >= 0, $"{label} P1 负血量: {runA.Player1RemainingHp}");
                Assert.True(runA.Player2RemainingHp >= 0, $"{label} P2 负血量: {runA.Player2RemainingHp}");
                Assert.False(double.IsNaN(runA.Player1TotalDamageDealt) || double.IsInfinity(runA.Player1TotalDamageDealt));
                Assert.False(double.IsNaN(runA.Player2TotalDamageDealt) || double.IsInfinity(runA.Player2TotalDamageDealt));
                Assert.False(double.IsNaN(runA.Player1TotalShieldGained) || double.IsInfinity(runA.Player1TotalShieldGained));
                Assert.False(double.IsNaN(runA.Player2TotalShieldGained) || double.IsInfinity(runA.Player2TotalShieldGained));

                // 抽样验证开启 Events/Timeline 与无头模式完全一致
                if (i < 24)
                {
                    var runFull = CombatSimulator.Simulate(repo, p1, p2, seed: seed, combatType: "PVP", recordTimeline: true, recordEvents: true);
                    Assert.Equal(runA.WinnerPlayerId, runFull.WinnerPlayerId);
                    Assert.Equal(runA.DurationMs, runFull.DurationMs);
                    Assert.Equal(runA.Player1RemainingHp, runFull.Player1RemainingHp);
                    Assert.Equal(runA.Player2RemainingHp, runFull.Player2RemainingHp);
                    Assert.NotEmpty(runFull.Timeline);
                    Assert.NotEmpty(runFull.Events);
                }

                matchOutcomes[i] = (day, runA.DurationMs / 1000.0, runA.WinnerPlayerId == p1.PlayerId);
            });

        for (int i = 0; i < matchOutcomes.Length; i++)
        {
            var (day, sec, p1Won) = matchOutcomes[i];
            if (p1Won) p1Wins++;
            durationsByDay[day].Add(sec);
            allDurationsSec.Add(sec);
        }

        sw.Stop();

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(x => x).ToList();
            return sorted[sorted.Count / 2];
        }

        double overallMedianSec = Median(allDurationsSec);
        double d4Median = Median(durationsByDay[4]);
        double d7Median = Median(durationsByDay[7]);
        double d10Median = Median(durationsByDay[10]);

        _output.WriteLine(
            $"200 场真实天梯交叉验证完成（耗时 {sw.ElapsedMilliseconds}ms, P1胜率={p1Wins}/200）：" +
            $"总中位时长={overallMedianSec:F2}s, D4={d4Median:F2}s (校准 {calibrationMedians[4].MedianSec:F2}s), " +
            $"D7={d7Median:F2}s (校准 {calibrationMedians[7].MedianSec:F2}s), " +
            $"D10={d10Median:F2}s (校准 {calibrationMedians[10].MedianSec:F2}s)");

        // 3. 对齐天梯校准曲线合理区间（不卡死在极短瞬秒或沙暴死锁 35s+）
        Assert.InRange(overallMedianSec, 4.0, 35.0);
        Assert.InRange(d4Median, 4.0, 35.0);
        Assert.InRange(d7Median, 4.0, 35.0);
        Assert.InRange(d10Median, 4.0, 35.0);
    }

    private static PlayerState BuildPlayerFromAnchor(JsonElement anchor, GameDataRepository repo, string playerId, int day)
    {
        string hero = anchor.GetProperty("hero").GetString() ?? HeroNames.Vanessa;
        int hp = Math.Max(100, anchor.GetProperty("healthMax").GetInt32());
        int level = anchor.TryGetProperty("level", out var lvEl) ? Math.Max(1, lvEl.GetInt32()) : day;

        var ps = new PlayerState(playerId, playerId, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 10)
        {
            Day = day,
            Hour = 6,
            Level = level
        };

        if (anchor.TryGetProperty("items", out var itemsEl))
        {
            foreach (var it in itemsEl.EnumerateArray())
            {
                string id = it.GetProperty("id").GetString()!;
                var tpl = repo.GetCard(id);
                if (tpl == null) continue;
                var tier = TierHelper.ParseTier(it.TryGetProperty("tier", out var tEl) ? tEl.GetString() : null);
                string? ench = it.TryGetProperty("enchantment", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
                var card = new CardInstance(tpl, tier, ench);

                if (it.TryGetProperty("attributeOverrides", out var ovEl) && ovEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in ovEl.EnumerateObject())
                    {
                        double targetVal = prop.Value.GetDouble();
                        if (string.Equals(prop.Name, "Multicast", StringComparison.OrdinalIgnoreCase) && targetVal <= 1)
                            continue; // 避免覆盖附魔自带 +1 Multicast（如 Shiny）
                        double baseVal = card.BaseAttributes.GetValueOrDefault(prop.Name, 0);
                        card.PermanentAdd[prop.Name] = targetVal - baseVal;
                    }
                }
                ps.Hand.TryAddAuto(card);
            }
        }

        if (anchor.TryGetProperty("skills", out var skillsEl))
        {
            foreach (var sk in skillsEl.EnumerateArray())
            {
                string id = sk.GetProperty("id").GetString()!;
                var tpl = repo.GetCard(id);
                if (tpl == null) continue;
                var tier = TierHelper.ParseTier(sk.TryGetProperty("tier", out var tEl) ? tEl.GetString() : null);
                ps.Skills.Add(new CardInstance(tpl, tier));
            }
        }

        ps.PrepareForCombat();
        return ps;
    }

    private static PlayerState BuildPlayerFromBoard(JsonElement board, GameDataRepository repo, string playerId, int day, int healthMax)
    {
        string hero = board.GetProperty("hero").GetString() ?? HeroNames.Vanessa;
        int unlockedSlots = day >= 5 ? 10 : Math.Min(10, 4 + (day - 1) * 2);
        var ps = new PlayerState(playerId, playerId, hero, initialHealthMax: healthMax, initialPrestige: 20, initialGold: 10, unlockedHandSlots: unlockedSlots)
        {
            Day = day,
            Hour = 6,
            Level = day
        };
        if (day >= 5) ps.Hand.UnlockedMask = 1023;

        if (board.TryGetProperty("items", out var itemsEl))
        {
            foreach (var it in itemsEl.EnumerateArray())
            {
                string id = it.GetProperty("id").GetString()!;
                var tpl = repo.GetCard(id);
                if (tpl == null) continue;
                var tier = TierHelper.ParseTier(it.TryGetProperty("tier", out var tEl) ? tEl.GetString() : null);
                var card = new CardInstance(tpl, tier);
                if (!ps.Hand.TryAddAuto(card))
                {
                    ps.Hand.UnlockedMask = 1023;
                    ps.Hand.TryAddAuto(card);
                }
            }
        }

        ps.PrepareForCombat();
        return ps;
    }

    private static string ResolveCompassDistilledPath()
        => TestInfrastructure.TestDataLocator.ResolveCompassDistilledPath();

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
        return new CardInstance(tpl, CardTier.Bronze, enchantment, instanceId: id);
    }

    private static CardInstance CreateStartFightItem(
        string id,
        RuleNode action,
        Dictionary<string, double>? extraAttrs = null)
    {
        var attrs = extraAttrs != null ? new Dictionary<string, double>(extraAttrs) : new Dictionary<string, double>();
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
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = action
                }
            }
        };
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
    }
}
