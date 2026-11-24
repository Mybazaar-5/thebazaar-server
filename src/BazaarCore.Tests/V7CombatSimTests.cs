using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.TempoNet.Enums;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Official Parity V7 战斗模拟器（CombatSimulator &amp; CombatSimConverter）专项回归测试：
/// 覆盖 8.4 Heal/Lifesteal、8.5 Burn 护盾减免、8.9 Multicast 250ms 间隔、
/// 7.5 Passive Charge 溢出、9.4/7.6 单卡共享 ICD 与确定性排序、Flying 减半控场、
/// 9.1 零时长 Perform 触发及 7.3 冻结暂停 Haste/Slow。
/// </summary>
public sealed class V7CombatSimTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    [Fact]
    public void Task1_NormalAndSmallHeal_Cleanse5PercentMin1_AndTriggerPerformedHeal()
    {
        var repo = Repo.Value;

        // 1. 小额治疗（10 HP）：净化 max(1, floor(10 * 0.05)) = 1 点 Burn 与 Poison（而非旧版 10%）
        var sp1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var sp2 = CreatePlayer("p2", HeroNames.Pygmalien, 500);
        sp1.Hand.TryAddAuto(CreateStartFightItem("applier_small", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionPlayerBurnApply", Target = TargetOpponent(), ReferenceValue = Fixed(100) },
                new RuleNode { Type = "TActionPlayerPoisonApply", Target = TargetOpponent(), ReferenceValue = Fixed(100) },
                new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(100) }
            ]
        }));
        sp2.Hand.TryAddAuto(CreatePassiveItem("t0_small_heal", "TTriggerOnPlayerDamaged", new RuleNode
        {
            Type = "TActionPlayerHeal",
            Target = TargetSelf(),
            ReferenceValue = Fixed(10)
        }));
        // 监听 TTriggerOnCardPerformedHeal 获得护盾
        sp2.Hand.TryAddAuto(CreatePassiveItem("heal_watcher", "TTriggerOnCardPerformedHeal", new RuleNode
        {
            Type = "TActionPlayerShieldApply",
            Target = TargetSelf(),
            ReferenceValue = Fixed(15)
        }));

        var smallRes = CombatSimulator.Simulate(repo, sp1, sp2, seed: 101);
        var snap0Small = smallRes.Timeline.First(t => t.TimeMs == 0);
        Assert.Equal(99, snap0Small.P2Burn);
        Assert.Equal(99, snap0Small.P2Poison);
        Assert.Equal(410, snap0Small.P2Hp);
        Assert.True(smallRes.Player2TotalShieldGained >= 15, "普通治疗应触发 TTriggerOnCardPerformedHeal");

        // 2. 大额治疗（100 HP）：净化 max(1, floor(100 * 0.05)) = 5 点 Burn 与 Poison
        var lp1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var lp2 = CreatePlayer("p2", HeroNames.Pygmalien, 500);
        lp1.Hand.TryAddAuto(CreateStartFightItem("applier_large", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionPlayerBurnApply", Target = TargetOpponent(), ReferenceValue = Fixed(100) },
                new RuleNode { Type = "TActionPlayerPoisonApply", Target = TargetOpponent(), ReferenceValue = Fixed(100) },
                new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(200) }
            ]
        }));
        lp2.Hand.TryAddAuto(CreatePassiveItem("t0_large_heal", "TTriggerOnPlayerDamaged", new RuleNode
        {
            Type = "TActionPlayerHeal",
            Target = TargetSelf(),
            ReferenceValue = Fixed(100)
        }));

        var largeRes = CombatSimulator.Simulate(repo, lp1, lp2, seed: 103);
        var snap0Large = largeRes.Timeline.First(t => t.TimeMs == 0);
        Assert.Equal(95, snap0Large.P2Burn);
        Assert.Equal(95, snap0Large.P2Poison);
        Assert.Equal(400, snap0Large.P2Hp);
    }

    [Fact]
    public void Task1_Lifesteal_HealsWithoutCleanse_WithoutPerformedHeal_AndSeparateFromHealAdded()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("player", HeroNames.Vanessa, 1000);
        var p2 = CreatePlayer("opponent", HeroNames.Pygmalien, 1000);

        // 1. t=0 Lifesteal：p2 先给 p1 造成 300 伤害 + 40 Burn + 40 Poison，p1 受伤后立即触发 100 吸血反击
        p2.Hand.TryAddAuto(CreateStartFightItem("setup_debuffs", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionPlayerBurnApply", Target = TargetOpponent(), ReferenceValue = Fixed(40) },
                new RuleNode { Type = "TActionPlayerPoisonApply", Target = TargetOpponent(), ReferenceValue = Fixed(40) },
                new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(300) }
            ]
        }));
        var lsT0 = CreatePassiveItem(
            "ls_t0",
            "TTriggerOnPlayerDamaged",
            new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(100)
            },
            extraAttrs: new() { ["Lifesteal"] = 1 });
        var watcherT0 = CreatePassiveItem(
            "heal_watcher_t0",
            "TTriggerOnCardPerformedHeal",
            new RuleNode
            {
                Type = "TActionPlayerShieldApply",
                Target = TargetSelf(),
                ReferenceValue = Fixed(25)
            });
        p1.Hand.TryAddAuto(lsT0);
        p1.Hand.TryAddAuto(watcherT0);

        var t0Res = CombatSimulator.Simulate(repo, p1, p2, seed: 201);
        var snap0 = t0Res.Timeline.First(t => t.TimeMs == 0);
        Assert.Equal(800, snap0.P1Hp); // 1000 - 300 + 100
        Assert.Equal(40, snap0.P1Burn);
        Assert.Equal(40, snap0.P1Poison);
        Assert.Equal(0, t0Res.Player1TotalShieldGained); // Lifesteal 不触发 TTriggerOnCardPerformedHeal
        var lsEv = Assert.Single(t0Res.Events, e => e.TimeMs == 0 && e.Type == CombatEventType.HealApplied && e.SourceCardInstanceId == lsT0.InstanceId);
        Assert.Equal("Lifesteal", lsEv.Detail);

        // 2. CombatSimConverter：Lifesteal 不计入 HealAdded，也不生成 PlayerHeal EffectExecuted；普通治疗才计入 HealAdded
        var p1Conv = CreatePlayer("player", HeroNames.Vanessa, 1000);
        var p2Conv = CreatePlayer("opponent", HeroNames.Pygmalien, 1000);
        p2Conv.Hand.TryAddAuto(CreateStartFightItem("init_dmg", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(500)
        }));
        var lifestealSword = CreateSimpleActiveItem(
            "ls_sword",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(100)
            },
            extraAttrs: new() { ["Lifesteal"] = 1 });
        lifestealSword.StartSlot = 0;

        var healCard = CreateSimpleActiveItem(
            "pure_heal",
            cooldownMs: 2000,
            new RuleNode
            {
                Type = "TActionPlayerHeal",
                Target = TargetSelf(),
                ReferenceValue = Fixed(30)
            });
        healCard.StartSlot = 1;

        p1Conv.Hand.TryAddAuto(lifestealSword);
        p1Conv.Hand.TryAddAuto(healCard);
        var convRes = CombatSimulator.Simulate(repo, p1Conv, p2Conv, seed: 202);

        var lsEntry = MakeEntryFromCard("ent_ls", lifestealSword, ECombatantId.Player, EContainerSocketId.Socket_0);
        var healEntry = MakeEntryFromCard("ent_heal", healCard, ECombatantId.Player, EContainerSocketId.Socket_1);
        var sim = CombatSimConverter.ToCombatSim(
            convRes,
            repo,
            new Dictionary<string, CardInstance>
            {
                [lsEntry.InstanceId] = lifestealSword,
                [healEntry.InstanceId] = healCard,
            },
            new Dictionary<string, CardInstance>(),
            [lsEntry, healEntry],
            []);

        Assert.True(sim.CardStats.TryGetValue(lsEntry.InstanceId, out var lsStats));
        Assert.True(lsStats!.GetValueOrDefault(ECardStats.DamageDone, 0) >= 100);
        Assert.Equal(0, lsStats.GetValueOrDefault(ECardStats.HealAdded, 0));
        Assert.DoesNotContain(
            sim.Frames.SelectMany(f => f.Events).OfType<BazaarGameShared.Infra.Messages.CombatSimEvents.CombatSimEventEffectExecuted>(),
            e => e.Source?.Value == lsEntry.InstanceId && e.ActionType == BazaarGameShared.Domain.Effect.EActionCommandType.PlayerHeal);

        Assert.True(sim.CardStats.TryGetValue(healEntry.InstanceId, out var healStats));
        Assert.True(healStats!.GetValueOrDefault(ECardStats.HealAdded, 0) >= 30);
    }

    [Fact]
    public void Task2_BurnShieldMitigation_CapsByShieldPreventing1ShieldHalving100Burn()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 500);

        // t=0 施加 100 Burn 且目标只有 1 点 Shield -> mitigation=min(1, 50)=1 -> dmg=99（1 盾吸收 1 伤，扣血 98）
        p1.Hand.TryAddAuto(CreateStartFightItem("burn_100", new RuleNode
        {
            Type = "TActionPlayerBurnApply",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(100)
        }));
        p2.Hand.TryAddAuto(CreateStartFightItem("shield_1", new RuleNode
        {
            Type = "TActionPlayerShieldApply",
            Target = TargetSelf(),
            ReferenceValue = Fixed(1)
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 301);
        var firstBurnTick = res.Events.First(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "p2");
        Assert.Equal(500, firstBurnTick.TimeMs);
        Assert.Equal(99, (int)firstBurnTick.Value);
        Assert.Equal(0, firstBurnTick.Player2Shield);
        Assert.Equal(402, firstBurnTick.Player2Health);

        // 满护盾下奇数 Burn（97 Burn, 150 Shield）-> mitigation = min(150, (97+1)/2) = 49 -> dmg = 48
        var fp1 = CreatePlayer("fp1", HeroNames.Vanessa, 500);
        var fp2 = CreatePlayer("fp2", HeroNames.Pygmalien, 500);
        fp1.Hand.TryAddAuto(CreateStartFightItem("burn_97", new RuleNode
        {
            Type = "TActionPlayerBurnApply",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(97)
        }));
        fp2.Hand.TryAddAuto(CreateStartFightItem("shield_150", new RuleNode
        {
            Type = "TActionPlayerShieldApply",
            Target = TargetSelf(),
            ReferenceValue = Fixed(150)
        }));
        var fullShieldRes = CombatSimulator.Simulate(repo, fp1, fp2, seed: 302);
        var fullTick = fullShieldRes.Events.First(e => e.Type == CombatEventType.BurnTick && e.TargetPlayerId == "fp2");
        Assert.Equal(48, (int)fullTick.Value);
        Assert.Equal(102, fullTick.Player2Shield);
        Assert.Equal(500, fullTick.Player2Health);
    }

    [Fact]
    public void Task3_Multicast_Consumes1Ammo_Spaces250ms_RespectsFreezeDestroyAndShuriken()
    {
        var repo = Repo.Value;

        // 1. 普通 Multicast=3 武器：每轮只消耗 1 发弹药，且 3 次施放间隔 >= 250ms
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 5000);
        var multiWeapon = CreateSimpleActiveItem(
            "multi_gun",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionPlayerDamage",
                Target = TargetOpponent(),
                ReferenceValue = Fixed(10)
            },
            extraAttrs: new() { ["Multicast"] = 3, ["AmmoMax"] = 2 });
        p1.Hand.TryAddAuto(multiWeapon);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 401);
        var casts = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == multiWeapon.InstanceId).ToList();
        Assert.Equal(6, casts.Count); // 2 发弹药 * 3 连击 = 6 次施放（每次 CD 激活消耗 1 弹药）
        Assert.Equal(950, casts[0].TimeMs);
        Assert.True(casts[1].TimeMs - casts[0].TimeMs >= 250, $"第2发应间隔 >=250ms，实际 {casts[1].TimeMs - casts[0].TimeMs}ms");
        Assert.True(casts[2].TimeMs - casts[1].TimeMs >= 250, $"第3发应间隔 >=250ms，实际 {casts[2].TimeMs - casts[1].TimeMs}ms");
        Assert.Equal(1950, casts[3].TimeMs);
        Assert.True(casts[4].TimeMs - casts[3].TimeMs >= 250);
        Assert.True(casts[5].TimeMs - casts[4].TimeMs >= 250);

        // 2. 真实 Shuriken (3af3c836)：自身 Ability 消耗全部弹药 + Aura 提供 Multicast = 当前弹药数（3发）
        var shurikenTpl = repo.GetCard("3af3c836-7224-47bc-bc27-dade6f9d210c")!;
        var sp1 = CreatePlayer("sp1", HeroNames.Vanessa, 5000);
        var sp2 = CreatePlayer("sp2", HeroNames.Pygmalien, 5000);
        var shuriken = new CardInstance(shurikenTpl, CardTier.Bronze);
        sp1.Hand.TryAddAuto(shuriken);

        var shurikenRes = CombatSimulator.Simulate(repo, sp1, sp2, seed: 402);
        var shurikenCasts = shurikenRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == shuriken.InstanceId).ToList();
        Assert.Equal(3, shurikenCasts.Count); // 首轮耗尽全部 3 发弹药并完整抛出 3 次连发
        Assert.True(shurikenCasts[1].TimeMs - shurikenCasts[0].TimeMs >= 250);
        Assert.True(shurikenCasts[2].TimeMs - shurikenCasts[1].TimeMs >= 250);
        Assert.Equal(45, (int)shurikenRes.Player1TotalDamageDealt);

        // 3. 连发期间被冻结：后续连发推迟至解冻后
        var fp1 = CreatePlayer("fp1", HeroNames.Vanessa, 5000);
        var fp2 = CreatePlayer("fp2", HeroNames.Dooley, 5000);
        var fGun = CreateSimpleActiveItem(
            "frozen_multi",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) },
            extraAttrs: new() { ["Multicast"] = 3 });
        fp1.Hand.TryAddAuto(fGun);
        // 980ms 时对 fp1 卡牌施加单次 1000ms 冻结（恰好在 fGun 980ms 第 1 发之后、第 2 发之前；AmmoMax=1 避免永冻死锁）
        fp2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "freezer_1000",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionCardFreeze",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                ReferenceValue = Fixed(1000)
            },
            extraAttrs: new() { ["AmmoMax"] = 1 }));
        var frozenRes = CombatSimulator.Simulate(repo, fp1, fp2, seed: 403);
        var frozenCasts = frozenRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == fGun.InstanceId).ToList();
        Assert.True(frozenCasts.Count >= 3);
        Assert.Equal(950, frozenCasts[0].TimeMs);
        // 950ms 触发第 1 发并在同帧被冻结 1000ms -> 第 2 发必须推迟到解冻后 (>= 2000ms)
        Assert.True(frozenCasts[1].TimeMs >= 2000, $"冻结期间应推迟后续连发，实际第2发时间 {frozenCasts[1].TimeMs}ms");
        Assert.True(frozenCasts[2].TimeMs - frozenCasts[1].TimeMs >= 250);

        // 4. 连发期间被摧毁：后续未施放的连发不再执行
        var dp1 = CreatePlayer("dp1", HeroNames.Vanessa, 5000);
        var dp2 = CreatePlayer("dp2", HeroNames.Dooley, 5000);
        var doomedGun = CreateSimpleActiveItem(
            "doomed_multi",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) },
            extraAttrs: new() { ["Multicast"] = 3 });
        dp1.Hand.TryAddAuto(doomedGun);
        dp2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "destroyer_1000",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionCardDestroy",
                Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
            }));
        var destroyedRes = CombatSimulator.Simulate(repo, dp1, dp2, seed: 404);
        var doomedCasts = destroyedRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == doomedGun.InstanceId).ToList();
        Assert.Single(doomedCasts); // 980ms 第1发后在 1000ms 被摧毁，第2/3发不得再触发

        // 5. 战斗在连发尚未结束时已分胜负，未施放的后续连发不得继续执行
        var lp1 = CreatePlayer("lp1", HeroNames.Vanessa, 5000);
        var lp2 = CreatePlayer("lp2", HeroNames.Pygmalien, 100);
        var lethalGun = CreateSimpleActiveItem(
            "lethal_multi",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(60) },
            extraAttrs: new() { ["Multicast"] = 3 });
        lp1.Hand.TryAddAuto(lethalGun);
        var lethalRes = CombatSimulator.Simulate(repo, lp1, lp2, seed: 405);
        var lethalCasts = lethalRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == lethalGun.InstanceId).ToList();
        Assert.Equal(2, lethalCasts.Count); // 第1发打60(980ms)，第2发打60击杀100HP敌方(1240ms)，第3发不得在战后触发
    }

    [Fact]
    public void Task4_PassiveCharge_PreservesOverflowAndFiresImmediatelySameTick()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 5000);

        // receiver 冷却 2000ms；charger 每 1000ms 给 receiver 充能 1500ms
        // 在 980ms 时 receiver 自身已走 1000ms，被充能 1500ms 后达 2500ms >= 2000ms -> 同一 980ms tick 立即开火，保留 500ms 溢出！
        // 第 2 次冷却只需 1500ms：到 1980ms 时已有 500 + 1000 = 1500ms，charger 再充 1500ms 达 3000ms -> 保留 1000ms 溢出并开火
        var charger = CreateSimpleActiveItem("charger", cooldownMs: 1000, new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(1500)
        });
        charger.StartSlot = 0;

        var receiver = CreateSimpleActiveItem("receiver", cooldownMs: 2000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(10)
        });
        receiver.StartSlot = 1;

        p1.Hand.TryAddAuto(charger);
        p1.Hand.TryAddAuto(receiver);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 501);
        var firstCharge = res.Events.First(e => e.Type == CombatEventType.ChargeApplied && e.TargetCardInstanceId == receiver.InstanceId);
        var receiverFires = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == receiver.InstanceId).ToList();

        Assert.Equal(950, firstCharge.TimeMs);
        Assert.Equal(950, receiverFires[0].TimeMs); // 同 tick 立即开火
        // P1-54 新契约：950ms 超额充能钳制至 2000ms 并触发后归零，不结转 500ms 溢出；
        // 到 1950ms 自然走满 1000ms 后 charger 再充 1500ms -> 钳制至 2000ms 并在 1950ms 当帧再次开火
        Assert.True(receiverFires[1].TimeMs == 1950, $"第 2 次充能应在 1950ms 立即触发，实际 {receiverFires[1].TimeMs}ms");

        // 冻结时 Charge 保留进度但不立即触发；解冻后完成并立即触发
        // P1 槽 0 先施加 1000ms 冻结到 P2，再由 P2 槽 1 触发 Charge 2500ms 给 P2 槽 0 的 fcTarget（保证先冻后充）
        var fcp1 = CreatePlayer("fcp1", HeroNames.Vanessa, 5000);
        var fcp2 = CreatePlayer("fcp2", HeroNames.Dooley, 5000);
        var fcFreezer = CreateStartFightItem("fc_freezer", new RuleNode
        {
            Type = "TActionCardFreeze",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
            ReferenceValue = Fixed(1000)
        });
        fcFreezer.StartSlot = 0;
        fcp1.Hand.TryAddAuto(fcFreezer);

        var fcTarget = CreateSimpleActiveItem("fc_target", 2000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) });
        fcTarget.StartSlot = 0;
        var fcCharger = CreateStartFightItem("fc_charger", new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(2500)
        });
        fcCharger.StartSlot = 1;
        fcp2.Hand.TryAddAuto(fcTarget);
        fcp2.Hand.TryAddAuto(fcCharger);

        var fcRes = CombatSimulator.Simulate(repo, fcp1, fcp2, seed: 503);
        var fcFirstFire = fcRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == fcTarget.InstanceId);
        // 0ms 时被冻结 1000ms，虽然 Charged 2500ms 钳制至 2000ms，但冻结期间不得开火；1000ms 解冻后首跳立刻开火
        Assert.True(fcFirstFire.TimeMs >= 1000 && fcFirstFire.TimeMs <= 1050, $"解冻后应保留 Charge 并立刻开火，实际 {fcFirstFire.TimeMs}ms");

        // 缺弹时 Charge 不得提前触发开火
        var acp1 = CreatePlayer("acp1", HeroNames.Dooley, 5000);
        var acp2 = CreatePlayer("acp2", HeroNames.Vanessa, 5000);
        var emptyAmmoItem = CreateSimpleActiveItem(
            "empty_ammo_item",
            cooldownMs: 1000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) },
            extraAttrs: new() { ["AmmoMax"] = 1 });
        // 980ms 用尽 1 弹药后，1500ms 充能 2000ms 仍不应开火
        acp1.Hand.TryAddAuto(emptyAmmoItem);
        acp1.Hand.TryAddAuto(CreateSimpleActiveItem("late_charger", 1500, new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(2000)
        }));
        var acRes = CombatSimulator.Simulate(repo, acp1, acp2, seed: 504);
        Assert.Single(acRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == emptyAmmoItem.InstanceId));

        // 冻结与缺弹时 Charge 保留进度但不提前开火，且互充能受 MaxForceUseDepth 保护
        var lp1 = CreatePlayer("lp1", HeroNames.Dooley, 5000);
        var lp2 = CreatePlayer("lp2", HeroNames.Vanessa, 5000);
        var cA = CreateSimpleActiveItem("loop_a", 1000, new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(2000)
        });
        var cB = CreateSimpleActiveItem("loop_b", 1000, new RuleNode
        {
            Type = "TActionCardCharge",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(2000)
        });
        lp1.Hand.TryAddAuto(cA);
        lp1.Hand.TryAddAuto(cB);
        var loopRes = CombatSimulator.Simulate(repo, lp1, lp2, seed: 502);
        Assert.True(loopRes.DurationMs <= CombatSimulator.MaxDurationMs);
    }

    [Fact]
    public void Task5_SingleCardSharedIcd_AndDeterministicOrdering()
    {
        var repo = Repo.Value;

        // 1. 单卡拥有两个被动能力（"a" 与 "b"），均监听己方物品使用 -> 必须共享同一张卡的 250ms ICD
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 5000);
        var triggerItem = CreateSimpleActiveItem("trigger_stick", 1000, new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(1)
        });
        triggerItem.StartSlot = 0;

        var dualPassiveTpl = new CardTemplate
        {
            Id = "dual_passive_card",
            InternalName = "Dual Passive Card",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["a"] = new()
                {
                    Id = "a",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnItemUsed",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerBurnApply",
                        Target = TargetOpponent(),
                        ReferenceValue = Fixed(5)
                    }
                },
                ["b"] = new()
                {
                    Id = "b",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnItemUsed",
                        Subject = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerPoisonApply",
                        Target = TargetOpponent(),
                        ReferenceValue = Fixed(3)
                    }
                }
            }
        };
        var dualCard = new CardInstance(dualPassiveTpl) { StartSlot = 1 };
        p1.Hand.TryAddAuto(triggerItem);
        p1.Hand.TryAddAuto(dualCard);

        var sharedRes = CombatSimulator.Simulate(repo, p1, p2, seed: 601);
        var firstBurn = sharedRes.Events.First(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == dualCard.InstanceId);
        var firstPoison = sharedRes.Events.First(e => e.Type == CombatEventType.PoisonApplied && e.SourceCardInstanceId == dualCard.InstanceId);
        // P1-21 新契约：对齐官方单能力粒度 ICD，同卡双被动能力 "a"/"b" 拥有独立 ICD 槽位，首触发均于 950ms 同帧结算不互相挤占
        Assert.Equal(950, firstBurn.TimeMs);
        Assert.Equal(950, firstPoison.TimeMs);

        // 2. 确定性排序：Player < Opponent, StartSlot 升序, InstanceId 字典序
        var op1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var op2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);

        // 故意先插入 slot 5，再插入 slot 1，最后插入 P2 slot 0，均在 TTriggerOnFightStarted 触发伤害
        var p1Slot5 = CreateStartFightPassiveItem("z_inst_slot5", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(11)
        });
        p1Slot5.StartSlot = 5;
        var p1Slot1 = CreateStartFightPassiveItem("a_inst_slot1", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(22)
        });
        p1Slot1.StartSlot = 1;
        var p2Slot0 = CreateStartFightPassiveItem("b_opp_slot0", new RuleNode
        {
            Type = "TActionPlayerDamage",
            Target = TargetOpponent(),
            ReferenceValue = Fixed(33)
        });
        p2Slot0.StartSlot = 0;

        op1.Hand.Cards.Add(p1Slot5);
        op1.Hand.Cards.Add(p1Slot1);
        op2.Hand.Cards.Add(p2Slot0);

        var detRes = CombatSimulator.Simulate(repo, op1, op2, seed: 602);
        var t0Dmgs = detRes.Events.Where(e => e.TimeMs == 0 && e.Type == CombatEventType.DamageApplied).ToList();
        Assert.Equal(3, t0Dmgs.Count);
        Assert.Equal(p1Slot1.InstanceId, t0Dmgs[0].SourceCardInstanceId); // P1 StartSlot=1 最先
        Assert.Equal(p1Slot5.InstanceId, t0Dmgs[1].SourceCardInstanceId); // P1 StartSlot=5 其次
        Assert.Equal(p2Slot0.InstanceId, t0Dmgs[2].SourceCardInstanceId); // P2 StartSlot=0 最后
    }

    [Fact]
    public void Task6_Flying_HalvesFreezeAndSlowDurations_WithoutHalvingHaste()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Stelle, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);

        var flyingCard = CreateSimpleActiveItem(
            "flying_item",
            cooldownMs: 4000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(5) },
            extraAttrs: new() { ["Flying"] = 1 });
        p1.Hand.TryAddAuto(flyingCard);

        // 对手开战施加 1000ms Freeze + 1000ms Slow；己方开战施加 1000ms Haste
        p2.Hand.TryAddAuto(CreateStartFightItem("cc_1000", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode
                {
                    Type = "TActionCardFreeze",
                    Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                    ReferenceValue = Fixed(1000)
                },
                new RuleNode
                {
                    Type = "TActionCardSlow",
                    Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
                    ReferenceValue = Fixed(1000)
                }
            ]
        }));
        p1.Hand.TryAddAuto(CreateStartFightItem("haste_1000", new RuleNode
        {
            Type = "TActionCardHaste",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(1000)
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 701);
        var frzEv = res.Events.First(e => e.Type == CombatEventType.FreezeApplied && e.TargetCardInstanceId == flyingCard.InstanceId);
        var slwEv = res.Events.First(e => e.Type == CombatEventType.SlowApplied && e.TargetCardInstanceId == flyingCard.InstanceId);
        var hstEv = res.Events.First(e => e.Type == CombatEventType.HasteApplied && e.TargetCardInstanceId == flyingCard.InstanceId);

        Assert.Equal(500, (int)frzEv.Value);
        Assert.Equal(500, (int)slwEv.Value);
        Assert.Equal(1000, (int)hstEv.Value);
    }

    [Fact]
    public void Task7_ZeroDurationPerform_EmitsPerformedTriggersWhenReducedToZero()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 2000);

        // 目标卡拥有 100% 冰冻/减速/加速抗性
        var immuneTarget = CreateSimpleActiveItem(
            "immune_target",
            cooldownMs: 4000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(5) },
            extraAttrs: new()
            {
                ["PercentFreezeReduction"] = 100,
                ["PercentSlowReduction"] = 100,
                ["PercentHasteReduction"] = 100
            });
        p2.Hand.TryAddAuto(immuneTarget);

        // p1 施加非零 Freeze(1000), Slow(1000), Haste(1000)（以及一个 0ms 对照）
        p1.Hand.TryAddAuto(CreateStartFightItem("status_applier", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionCardFreeze", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(1000) },
                new RuleNode { Type = "TActionCardSlow", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(1000) },
                new RuleNode { Type = "TActionCardHaste", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(1000) }
            ]
        }));

        // p1 三个监听卡分别响应 PerformedFreeze / PerformedSlow / PerformedHaste
        p1.Hand.TryAddAuto(CreatePassiveItem("w_frz", "TTriggerOnCardPerformedFreeze", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(10) }));
        p1.Hand.TryAddAuto(CreatePassiveItem("w_slw", "TTriggerOnCardPerformedSlow", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(20) }));
        p1.Hand.TryAddAuto(CreatePassiveItem("w_hst", "TTriggerOnCardPerformedHaste", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(40) }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 801);
        Assert.DoesNotContain(res.Events, e => e.Type is CombatEventType.FreezeApplied or CombatEventType.SlowApplied or CombatEventType.HasteApplied);
        Assert.Equal(70, (int)res.Player1TotalShieldGained); // 10 + 20 + 40 全部触发

        // 原值本就是 0ms 时，不应触发 TTriggerOnCardPerformed*
        var zp1 = CreatePlayer("zp1", HeroNames.Vanessa, 2000);
        var zp2 = CreatePlayer("zp2", HeroNames.Dooley, 2000);
        zp2.Hand.TryAddAuto(CreateSimpleActiveItem("z_tgt", 4000, new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(5) }));
        zp1.Hand.TryAddAuto(CreateStartFightItem("z_applier", new RuleNode
        {
            Type = "TActionCardFreeze",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
            ReferenceValue = Fixed(0)
        }));
        zp1.Hand.TryAddAuto(CreatePassiveItem("zw_frz", "TTriggerOnCardPerformedFreeze", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(10) }));
        var zRes = CombatSimulator.Simulate(repo, zp1, zp2, seed: 802);
        Assert.Equal(0, (int)zRes.Player1TotalShieldGained);

        // Radiant 免疫将非零 Freeze/Slow 降至 0ms 时同样仍触发 TTriggerOnCardPerformedFreeze / Slow
        var rp1 = CreatePlayer("rp1", HeroNames.Vanessa, 2000);
        var rp2 = CreatePlayer("rp2", HeroNames.Dooley, 2000);
        rp2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "rad_tgt",
            cooldownMs: 4000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(5) },
            enchantment: "Radiant"));
        rp1.Hand.TryAddAuto(CreateStartFightItem("r_applier", new RuleNode
        {
            Type = "TActionAnd",
            Actions =
            [
                new RuleNode { Type = "TActionCardFreeze", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(1000) },
                new RuleNode { Type = "TActionCardSlow", Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }, ReferenceValue = Fixed(1000) }
            ]
        }));
        rp1.Hand.TryAddAuto(CreatePassiveItem("rw_frz", "TTriggerOnCardPerformedFreeze", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(12) }));
        rp1.Hand.TryAddAuto(CreatePassiveItem("rw_slw", "TTriggerOnCardPerformedSlow", new RuleNode { Type = "TActionPlayerShieldApply", Target = TargetSelf(), ReferenceValue = Fixed(24) }));
        var rRes = CombatSimulator.Simulate(repo, rp1, rp2, seed: 803);
        Assert.DoesNotContain(rRes.Events, e => e.Type is CombatEventType.FreezeApplied or CombatEventType.SlowApplied);
        Assert.Equal(36, (int)rRes.Player1TotalShieldGained);
    }

    [Fact]
    public void Task8_Freeze_PausesHasteAndSlowTimersUntilUnfrozen()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 5000);

        var targetItem = CreateSimpleActiveItem(
            "timer_item",
            cooldownMs: 4000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponent(), ReferenceValue = Fixed(10) });
        p1.Hand.TryAddAuto(targetItem);

        // t=0 同时赋予 1000ms Haste 与 1000ms Freeze
        p1.Hand.TryAddAuto(CreateStartFightItem("haste_starter", new RuleNode
        {
            Type = "TActionCardHaste",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true },
            ReferenceValue = Fixed(1000)
        }));
        p2.Hand.TryAddAuto(CreateStartFightItem("freeze_starter", new RuleNode
        {
            Type = "TActionCardFreeze",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" },
            ReferenceValue = Fixed(1000)
        }));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 901);
        var snap500 = res.Timeline.First(t => t.TimeMs == 500).Cards.First(c => c.InstanceId == targetItem.InstanceId);
        var snap1000 = res.Timeline.First(t => t.TimeMs == 1000).Cards.First(c => c.InstanceId == targetItem.InstanceId);
        var snap1500 = res.Timeline.First(t => t.TimeMs == 1500).Cards.First(c => c.InstanceId == targetItem.InstanceId);

        // 对齐官服 BazaarCardDealer.cs:4110-4151：冻结期间冷却进度完全暂停，但 Haste / Slow / Freeze 计时器均独立照常逐帧流逝（50ms*11=550ms -> 450ms）
        Assert.Equal(0f, snap500.Progress01);
        Assert.Equal(450, snap500.HasteMs);
        Assert.True(snap500.FreezeMs > 0);
        Assert.Equal(0, snap1000.HasteMs);
        Assert.Equal(0, snap1000.FreezeMs);
        Assert.Equal(0, snap1500.HasteMs);
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

    private static CardInstance CreateStartFightPassiveItem(string id, RuleNode action)
        => CreateStartFightItem(id, action);

    private static CardInstance CreatePassiveItem(
        string id,
        string triggerType,
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
                    Trigger = new RuleNode { Type = triggerType },
                    Action = action
                }
            }
        };
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
    }

    private static RunState.CardEntry MakeEntryFromCard(
        string entryId,
        CardInstance card,
        ECombatantId owner,
        EContainerSocketId socket)
    {
        var entry = new RunState.CardEntry
        {
            InstanceId = entryId,
            TemplateId = Guid.NewGuid(),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = owner,
            Section = EInventorySection.Hand,
            Socket = socket,
            CoreCard = card,
        };
        entry.Attributes[ECardAttributeType.CooldownMax] = (int)Math.Round(card.GetAttribute("CooldownMax"));
        return entry;
    }
}
