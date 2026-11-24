using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3 Batch 3A（Core 战斗与属性核心对齐）全量回归单测：
/// 严格覆盖 P1-19 ~ P1-21、P1-50 ~ P1-56 共 10 项官方反编译与实库对标修复。
/// </summary>
public sealed class AuditWave3CoreParityTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    // =========================================================================
    // P1-19：TargetingPriorities（Upgrade / ForceUse）下沉 Random/XMost + Maker's Mark 升至 Legendary
    // =========================================================================

    [Fact]
    public void P1_19_TargetingPriorities_UpgradeAndForceUse_AndMakersMarkDiamondToLegendary()
    {
        var repo = Repo.Value;

        // 1. Maker's Mark (3a939e5a) 是全库唯一 5 阶特例卡：Diamond -> Legendary 必须放行；普通 4 阶卡 Diamond 不得升阶
        var makersMarkTpl = repo.GetCard("3a939e5a-4601-40ba-9814-5e2645a4a74a")!;
        var makersMark = new CardInstance(makersMarkTpl, CardTier.Diamond);
        Assert.True(makersMark.CanUpgrade());
        Assert.True(makersMark.TryUpgrade());
        Assert.Equal(CardTier.Legendary, makersMark.Tier);
        Assert.False(makersMark.CanUpgrade());

        var cutlassTpl = repo.GetCard("1b955b8d-0ea6-4437-935c-d588be5898c1")!;
        var diamondCutlass = new CardInstance(cutlassTpl, CardTier.Diamond);
        Assert.False(diamondCutlass.CanUpgrade());
        Assert.False(diamondCutlass.TryUpgrade());
        Assert.Equal(CardTier.Diamond, diamondCutlass.Tier);

        // 2. TActionCardUpgrade + TTargetCardXMost(LeftMost) 与 TTargetCardRandom：跳过最左已满阶 Diamond 卡，优先升阶右侧可升阶卡
        var pUp = CreatePlayer("p_up", HeroNames.Vanessa, 2000);
        var maxedLeft = new CardInstance(cutlassTpl, CardTier.Diamond, null, "maxed_left");
        var upgradableRight = new CardInstance(cutlassTpl, CardTier.Bronze, null, "up_right");
        var driver = CreateCustomItem("up_driver", "UpDriver");
        Assert.True(pUp.Hand.TryPlaceOrShift(maxedLeft, 0));
        Assert.True(pUp.Hand.TryPlaceOrShift(upgradableRight, 2));
        Assert.True(pUp.Hand.TryPlaceOrShift(driver, 4));

        var ctxUp = new RuleExecutionContext(repo, RuleScope.OutOfCombat, pUp, null, new Random(19))
            .WithCards(driver, driver);
        var upgradeLeftMost = ParseNode("""
            {
              "$type": "TActionCardUpgrade",
              "Target": {
                "$type": "TTargetCardXMost",
                "TargetSection": "SelfHand",
                "TargetMode": "LeftMost",
                "ExcludeSelf": true
              }
            }
            """);
        AbilityExecutor.ExecuteAction(upgradeLeftMost, driver, ctxUp);
        Assert.Equal(CardTier.Diamond, maxedLeft.Tier);
        Assert.Equal(CardTier.Silver, upgradableRight.Tier);

        // 再用 TTargetCardRandom 重复 10 次验证永远跳过满阶卡命中 upgradableRight（直到满阶）
        var upgradeRandom = ParseNode("""
            {
              "$type": "TActionCardUpgrade",
              "Target": {
                "$type": "TTargetCardRandom",
                "TargetSection": "SelfHand",
                "ExcludeSelf": true
              }
            }
            """);
        AbilityExecutor.ExecuteAction(upgradeRandom, driver, ctxUp);
        Assert.Equal(CardTier.Gold, upgradableRight.Tier);
        AbilityExecutor.ExecuteAction(upgradeRandom, driver, ctxUp);
        Assert.Equal(CardTier.Diamond, upgradableRight.Tier);

        // 3. TActionCardForceUse + TTargetCardXMost(LeftMost) 与 TTargetCardRandom：跳过冻结卡与无冷却卡，优先强制触发未冻结有冷却卡
        var pFu1 = CreatePlayer("pfu1", HeroNames.Vanessa, 5000);
        var pFu2 = CreatePlayer("pfu2", HeroNames.Vanessa, 5000);
        var passiveLeft = CreateCustomItem("passive_left", "PassiveLeft"); // 无主动冷却
        var frozenMid = CreateCustomItem(
            "frozen_mid", "FrozenMid",
            attrs: new() { ["CooldownMax"] = 9000, ["DamageAmount"] = 10 },
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
        var readyRight = CreateCustomItem(
            "ready_right", "ReadyRight",
            attrs: new() { ["CooldownMax"] = 9000, ["DamageAmount"] = 77 },
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
        var forceDriver = CreateCustomItem(
            "force_driver", "ForceDriver",
            attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 },
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardForceUse","Target":{"$type":"TTargetCardXMost","TargetSection":"SelfHand","TargetMode":"LeftMost","ExcludeSelf":true}}""")
                }
            });
        // 开战冻结 frozenMid 5000ms
        var freezer = CreateCustomItem(
            "freeze_mid_starter", "FreezeMidStarter",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionCardFreeze","ReferenceValue":{"$type":"TFixedValue","Value":5000},"Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","Conditions":{"$type":"TCardConditionalId","Id":"frozen_mid","IsNot":false}}}""")
                }
            });
        Assert.True(pFu1.Hand.TryPlaceOrShift(passiveLeft, 0));
        Assert.True(pFu1.Hand.TryPlaceOrShift(frozenMid, 1));
        Assert.True(pFu1.Hand.TryPlaceOrShift(readyRight, 2));
        Assert.True(pFu1.Hand.TryPlaceOrShift(forceDriver, 3));
        Assert.True(pFu2.Hand.TryPlaceOrShift(freezer, 0));

        var fuRes = CombatSimulator.Simulate(repo, pFu1, pFu2, seed: 1901, recordEvents: true);
        var forcedReadyFires = fuRes.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == readyRight.InstanceId && e.TimeMs == 950)
            .ToList();
        Assert.Single(forcedReadyFires);
        Assert.DoesNotContain(fuRes.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == frozenMid.InstanceId && e.TimeMs == 950);
    }

    // =========================================================================
    // P1-20：Stash 战中被动触发与战后回写 + 背包位置光环严格隔离（红线 2）
    // =========================================================================

    [Fact]
    public void P1_20_StashCombatTriggers_WorkAndSyncBack_WhileStashAurasRemainIsolated()
    {
        var repo = Repo.Value;

        // 1. 真实 Atlas (03842226) 置入 Stash，在 PVE 战斗中触发 TTriggerOnFightStarted 永久 +SellPrice 并回写原 Stash 卡
        var atlasTpl = repo.GetCard("03842226-6605-466f-9ea3-a8a2dbea67e1")!;
        var tomeTpl = repo.GetCard("c35bd10f-8ada-44e5-b32e-427787e6f0b6")!; // The Tome of Yyahan: Stash 监听 Transform -> 永久 +HealthRegen
        var starChartTpl = repo.GetCard("5abf3441-11cb-4dbd-8d69-d4829f8df687")!; // Star Chart: 邻接位置光环（绝不可从 Stash 泄漏到场上）
        var cutlassTpl = repo.GetCard("1b955b8d-0ea6-4437-935c-d588be5898c1")!;

        var p1 = CreatePlayer("p1", HeroNames.Stelle, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Common, 300);

        var handWeapon = CreateCustomItem(
            "hand_killer", "HandKiller",
            attrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 300 },
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
        // 开战触发一次己方场上 Reagent 卡临时变形，用于触发 Stash 中 The Tome of Yyahan 的 TTriggerOnCardTransformed
        var transformFodder = CreateCustomItem("trans_fodder", "TransFodder", attrs: new() { ["CooldownMax"] = 5000 }, tags: ["Reagent"]);
        var transformer = CreateCustomItem(
            "trans_driver", "TransDriver",
            abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                    Action = ParseNode("""{"$type":"TActionCardTransform","SpawnContext":{"$type":"TSpawnContextQuery","Limit":{"$type":"TFixedValue","Value":1},"Groups":[{"Filters":[{"$type":"TSpawnFilterIdList","Ids":["1b955b8d-0ea6-4437-935c-d588be5898c1"]}]}]},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalId","Id":"trans_fodder","IsNot":false}}}""")
                }
            });

        // 场上放 slot 0 武器，验证 Stash slot 1 的 Star Chart 绝不会对其施加邻接 CDR / Crit 光环
        Assert.True(p1.Hand.TryPlaceOrShift(handWeapon, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(transformFodder, 2));
        Assert.True(p1.Hand.TryPlaceOrShift(transformer, 3));

        var stashAtlas = new CardInstance(atlasTpl, CardTier.Silver);
        var stashTome = new CardInstance(tomeTpl, CardTier.Silver);
        var stashStarChart = new CardInstance(starChartTpl, CardTier.Gold);
        var stashActiveGun = new CardInstance(cutlassTpl, CardTier.Bronze); // 含 TTriggerOnCardFired，在 Stash 中绝不得开火

        double atlasValueBefore = stashAtlas.GetAttribute("SellPrice");
        double regenBefore = p1.GetAttribute("HealthRegen");

        Assert.True(p1.Stash.TryPlaceOrShift(stashStarChart, 1));
        Assert.True(p1.Stash.TryAddAuto(stashAtlas));
        Assert.True(p1.Stash.TryAddAuto(stashTome));
        Assert.True(p1.Stash.TryAddAuto(stashActiveGun));

        var res = CombatSimulator.Simulate(
            repo, p1, p2,
            seed: 2001,
            combatType: "PVE",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: true);

        // 1. Atlas 在 Stash 中响应 PVE 开战并永久增加 SellPrice 且已回写原 Stash 卡
        double atlasValueAfter = stashAtlas.GetAttribute("SellPrice");
        Assert.True(atlasValueAfter > atlasValueBefore, $"Stash 内 Atlas 应在 PVE 战中触发并回写 SellPrice，before={atlasValueBefore}, after={atlasValueAfter}");

        // 2. The Tome of Yyahan (Silver Custom_0=3) 在 Stash 中响应战中 Reagent 变形并永久提升玩家 HealthRegen (+3)
        double regenAfter = p1.GetAttribute("HealthRegen");
        Assert.Equal(regenBefore + 3, regenAfter);

        // 3. Stash 内的主动武器绝不会开火
        Assert.DoesNotContain(res.Events, e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == stashActiveGun.InstanceId);

        // 4. Stash 内的 Star Chart 绝不可向场上 slot 0 的 handWeapon 泄漏 CDR/Crit 光环（CooldownMax 保持 2000ms，CritChance 保持 0）
        var f0HandWeapon = res.Timeline[0].Cards.First(c => c.InstanceId == handWeapon.InstanceId);
        Assert.Equal(2000, f0HandWeapon.CooldownMaxMs);
        Assert.Equal(0, f0HandWeapon.CritChance);
    }

    // =========================================================================
    // P1-21：单能力粒度 ICD + Priority==Immediate 同帧结算 + 出队重检 Prerequisites + 局外全局 Priority 排序
    // =========================================================================

    [Fact]
    public void P1_21_PerAbilityIcd_ImmediateExecution_QueuedPrerequisiteRecheck_AndOutOfCombatGlobalPriority()
    {
        var repo = Repo.Value;

        // 1. 真实 Neon Sign (41f1bb20)：Ab[0] & Ab[2] 均为 Priority=Immediate 的 TTriggerOnFightStarted 双随机附魔，必须在 t=0ms 同步完成零互挤
        var neonTpl = repo.GetCard("41f1bb20-6e95-4943-b401-1f3c41ffce1f")!;
        var cutlassTpl = repo.GetCard("1b955b8d-0ea6-4437-935c-d588be5898c1")!;
        var np1 = CreatePlayer("np1", HeroNames.Vanessa, 2000);
        var np2 = CreatePlayer("np2", HeroNames.Vanessa, 2000);
        var leftAdj = new CardInstance(cutlassTpl, CardTier.Bronze, null, "adj_left");
        var neon = new CardInstance(neonTpl, CardTier.Bronze, null, "neon_sign");
        var rightAdj = new CardInstance(cutlassTpl, CardTier.Bronze, null, "adj_right");
        Assert.True(np1.Hand.TryPlaceOrShift(leftAdj, 0));
        Assert.True(np1.Hand.TryPlaceOrShift(neon, 2));
        Assert.True(np1.Hand.TryPlaceOrShift(rightAdj, 4));

        var neonRes = CombatSimulator.Simulate(repo, np1, np2, seed: 2101, recordEvents: true);
        var t0Enchants = neonRes.Events
            .Where(e => e.Type == CombatEventType.CardEnchanted && e.TimeMs == 0)
            .ToList();
        Assert.Equal(2, t0Enchants.Count); // 左右两卡均在 t=0ms 零延迟获随机附魔，绝无 250ms 互挤

        // 2. 同卡双 Medium 被动能力（独立 ICD 槽位）：同帧并行触发且同一能力自身仍严格受 250ms ICD 节流
        var dp1 = CreatePlayer("dp1", HeroNames.Vanessa, 5000);
        var dp2 = CreatePlayer("dp2", HeroNames.Vanessa, 5000);
        var trigger1 = CreateCustomItem("trig1", "Trig1", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        var trigger2 = CreateCustomItem("trig2", "Trig2", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        var dualCard = CreateCustomItem("dual_ab", "DualAb", abilities: new()
        {
            ["a"] = new AbilityDefinition
            {
                Id = "a",
                ActiveIn = "HandOnly",
                Priority = "Medium",
                Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                Action = ParseNode("""{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":2},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            },
            ["b"] = new AbilityDefinition
            {
                Id = "b",
                ActiveIn = "HandOnly",
                Priority = "Medium",
                Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":2},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        Assert.True(dp1.Hand.TryPlaceOrShift(trigger1, 0));
        Assert.True(dp1.Hand.TryPlaceOrShift(trigger2, 1));
        Assert.True(dp1.Hand.TryPlaceOrShift(dualCard, 2));

        var dualRes = CombatSimulator.Simulate(repo, dp1, dp2, seed: 2102, recordEvents: true);
        var burns = dualRes.Events.Where(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == dualCard.InstanceId).ToList();
        var poisons = dualRes.Events.Where(e => e.Type == CombatEventType.PoisonApplied && e.SourceCardInstanceId == dualCard.InstanceId).ToList();
        Assert.True(burns.Count >= 2 && poisons.Count >= 2);
        // 能力 "a" 与 "b" 各自独立槽位 -> 首次均在 950ms 同帧结算
        Assert.Equal(950, burns[0].TimeMs);
        Assert.Equal(950, poisons[0].TimeMs);
        // 同一能力连续两次入队仍严格遵守 250ms 自身 ICD（950ms + 250ms = 1200ms）
        Assert.Equal(1200, burns[1].TimeMs);
        Assert.Equal(1200, poisons[1].TimeMs);

        // 3. ProcessIcdQueues 出队实时重检 Prerequisites：
        // 950ms 时我方两把武器同时触发 watcher（前提 Self.Shield > 0），第 1 次在 950ms 执行，第 2 次排入 1200ms；
        // 但敌方 1050ms 破盾武器打空我方 10 护盾 -> 1200ms 第 2 次出队重检 Self.Shield > 0 失败，必须拦截！
        var qp1 = CreatePlayer("qp1", HeroNames.Vanessa, 5000);
        var qp2 = CreatePlayer("qp2", HeroNames.Vanessa, 5000);
        var shieldInit = CreateCustomItem("sh_init", "ShInit", abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""), Action = ParseNode("""{"$type":"TActionPlayerShieldApply","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });
        var tA = CreateCustomItem("ta", "TA", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["DamageAmount"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        var tB = CreateCustomItem("tb", "TB", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1, ["DamageAmount"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        var shieldGatedWatcher = CreateCustomItem("sh_watcher", "ShWatcher", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Priority = "Medium",
                Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                Prerequisites =
                [
                    ParseNode("""{"$type":"TPrerequisitePlayer","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Shield","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}}}""")
                ],
                Action = ParseNode("""{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":9},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        var shieldBreaker = CreateCustomItem("sh_break", "ShBreak", attrs: new() { ["CooldownMax"] = 1100, ["AmmoMax"] = 1, ["DamageAmount"] = 50 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        Assert.True(qp1.Hand.TryPlaceOrShift(shieldInit, 0));
        Assert.True(qp1.Hand.TryPlaceOrShift(tA, 1));
        Assert.True(qp1.Hand.TryPlaceOrShift(tB, 2));
        Assert.True(qp1.Hand.TryPlaceOrShift(shieldGatedWatcher, 3));
        Assert.True(qp2.Hand.TryPlaceOrShift(shieldBreaker, 0));

        var qRes = CombatSimulator.Simulate(repo, qp1, qp2, seed: 2103, recordEvents: true);
        var gatedBurns = qRes.Events
            .Where(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == shieldGatedWatcher.InstanceId)
            .ToList();
        Assert.Single(gatedBurns);
        Assert.Equal(950, gatedBurns[0].TimeMs); // 仅第 1 次 950ms 有盾生效，第 2 次 1200ms 时护盾已被 1050ms 击穿而被出队重检拦截

        // 4. OutOfCombatController 跨卡全局 Priority 稳定排序：slot 0 (Low) 必须晚于 slot 1 (Highest) 执行
        // 验证方法：slot 1 (Highest) 给 Gold +10（0->10），slot 0 (Low) 要求 Gold >= 10 时给 HealthMax +50；
        // 若按旧逻辑先跑 slot 0 再跑 slot 1，slot 0 将因 Gold==0 无法触发；全局 Priority 排序后 slot 1 先加金，slot 0 必成功触发！
        var oocPlayer = CreatePlayer("ooc_p", HeroNames.Vanessa, 500, initialGold: 0);
        var lowSlot0 = CreateCustomItem("ooc_low", "OocLow", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Priority = "Low",
                Trigger = ParseNode("""{"$type":"TTriggerOnDayStarted"}"""),
                Prerequisites =
                [
                    ParseNode("""{"$type":"TPrerequisitePlayer","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":{"$type":"TPlayerConditionalAttribute","Attribute":"Gold","ComparisonOperator":"GreaterThanOrEqual","ComparisonValue":{"$type":"TFixedValue","Value":10}}}}""")
                ],
                Action = ParseNode("""{"$type":"TActionPlayerModifyAttribute","AttributeType":"HealthMax","Operation":"Add","Value":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
            }
        });
        var highestSlot1 = CreateCustomItem("ooc_high", "OocHigh", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Priority = "Highest",
                Trigger = ParseNode("""{"$type":"TTriggerOnDayStarted"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerModifyAttribute","AttributeType":"Gold","Operation":"Add","Value":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
            }
        });
        Assert.True(oocPlayer.Hand.TryPlaceOrShift(lowSlot0, 0));
        Assert.True(oocPlayer.Hand.TryPlaceOrShift(highestSlot1, 1));

        var oocRes = OutOfCombatController.OnDayStarted(oocPlayer, repo, new Random(2104));
        Assert.Equal(10, oocPlayer.Gold);
        Assert.Equal(550, oocPlayer.HealthMax);
        Assert.Equal(2, oocRes.TriggeredAbilityLogs.Count);
        Assert.StartsWith("OocHigh:", oocRes.TriggeredAbilityLogs[0]);
        Assert.StartsWith("OocLow:", oocRes.TriggeredAbilityLogs[1]);
    }

    // =========================================================================
    // P1-50：TickMs = 50ms 零漂移 + 沙暴 t <= 35,000ms 精确 30 跳 / 累计 450 伤（红线 1）
    // =========================================================================

    [Fact]
    public void P1_50_TickMs50_ZeroMulticastDrift_AndExact30Ticks450DamageSandstorm()
    {
        var repo = Repo.Value;
        Assert.Equal(50, CombatSimulator.TickMs);

        // 1. 250ms Multicast 零漂移（950 / 1200 / 1450ms）
        var mp1 = CreatePlayer("mp1", HeroNames.Vanessa, 5000);
        var mp2 = CreatePlayer("mp2", HeroNames.Vanessa, 5000);
        var multi = CreateCustomItem("multi_50", "Multi50", attrs: new() { ["CooldownMax"] = 1000, ["Multicast"] = 3, ["AmmoMax"] = 1, ["DamageAmount"] = 5 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        Assert.True(mp1.Hand.TryAddAuto(multi));
        var mRes = CombatSimulator.Simulate(repo, mp1, mp2, seed: 5001, recordEvents: true);
        var casts = mRes.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == multi.InstanceId).Select(e => e.TimeMs).ToList();
        Assert.Equal(new[] { 950, 1200, 1450 }, casts);

        // 2. 沙暴 t <= 35,000ms 精确逐跳核对：30 跳 / 累计 450 伤（对齐官方 BazaarCardDealer.cs:4235-4274 顺序双 if）
        var sp1 = CreatePlayer("sp1", HeroNames.Pygmalien, 5000);
        var sp2 = CreatePlayer("sp2", HeroNames.Pygmalien, 5000);
        var sandRes = CombatSimulator.Simulate(repo, sp1, sp2, seed: 5002, recordTimeline: true, recordEvents: true);
        var allTicks = sandRes.Events.Where(e => e.Type == CombatEventType.SandstormTick).ToList();
        var ticksBy35s = allTicks.Where(t => t.TimeMs <= 35_000).ToList();

        Assert.Equal(30, ticksBy35s.Count);
        Assert.Equal(450, ticksBy35s.Sum(t => (int)t.Value));

        // 逐关键跳校验（含第 1/2/3/10/11/20/30/31 跳时间与伤害）
        Assert.Equal(30_000, ticksBy35s[0].TimeMs);
        Assert.Equal(1, (int)ticksBy35s[0].Value);

        Assert.Equal(30_200, ticksBy35s[1].TimeMs);
        Assert.Equal(1, (int)ticksBy35s[1].Value);

        Assert.Equal(30_450, ticksBy35s[2].TimeMs);
        Assert.Equal(1, (int)ticksBy35s[2].Value);

        Assert.Equal(31_950, ticksBy35s[9].TimeMs);
        Assert.Equal(1, (int)ticksBy35s[9].Value);

        // 第 11 跳：第 10 跳刚由 160->150 触底并同跳 dmg 1->3，故 31950+150=32100ms 伤害 3
        Assert.Equal(32_100, ticksBy35s[10].TimeMs);
        Assert.Equal(3, (int)ticksBy35s[10].Value);

        // 第 20 跳：33450ms 伤害 21
        Assert.Equal(33_450, ticksBy35s[19].TimeMs);
        Assert.Equal(21, (int)ticksBy35s[19].Value);

        // 第 30 跳：34950ms 伤害 41
        Assert.Equal(34_950, ticksBy35s[29].TimeMs);
        Assert.Equal(41, (int)ticksBy35s[29].Value);

        // 第 31 跳落在 35,100ms（> 35,000ms），伤害 43
        Assert.True(allTicks.Count >= 31);
        Assert.Equal(35_100, allTicks[30].TimeMs);
        Assert.Equal(43, (int)allTicks[30].Value);

        // 核对 35,000ms 时间轴快照血量精准减少 450（5000 - 450 = 4550）
        var snap35s = sandRes.Timeline.First(t => t.TimeMs == 35_000);
        Assert.Equal(4550, snap35s.P1Hp);
        Assert.Equal(4550, snap35s.P2Hp);
    }

    // =========================================================================
    // P1-51：附魔同名动作参数隔离（全部 15 张可达污染卡 + 2 张无害叠加例外）
    // =========================================================================

    [Fact]
    public void P1_51_EnchantmentAttributeIsolation_All15ReachableCards_AndHarmlessExceptions()
    {
        var repo = Repo.Value;

        // 1. 全量 15 张可达附魔污染卡逐一验证基础能力 vs 附魔 e1 能力隔离值（0 污染）
        var all15Cases = new (string CardId, CardTier Tier, string Enchant, string Attr, double ExpectedBase, double ExpectedEnch)[]
        {
            ("4bb254dc-4b02-4c4b-8198-a310bdb122e4", CardTier.Silver, "Icy", "FreezeAmount", 100000, 500),       // Icebreaker + Icy
            ("4bb254dc-4b02-4c4b-8198-a310bdb122e4", CardTier.Silver, "Icy", "FreezeTargets", 1, 1),            // Icebreaker + Icy (targets)
            ("2e77aab4-4c38-4f0a-8953-696c55140d8e", CardTier.Legendary, "Obsidian", "DamageAmount", 100, 20),  // Tantius Carrier + Obsidian
            ("20d85338-b625-4b9a-95a7-8a60805e061f", CardTier.Gold, "Turbo", "HasteAmount", 2000, 3000),        // Anchor + Turbo
            ("20d85338-b625-4b9a-95a7-8a60805e061f", CardTier.Gold, "Turbo", "HasteTargets", 1, 2),             // Anchor + Turbo (targets)
            ("aa2ffa22-58de-48ea-aef4-b79bacedfb03", CardTier.Diamond, "Heavy", "SlowAmount", 5000, 2000),      // Monstrous Marble + Heavy
            ("aa2ffa22-58de-48ea-aef4-b79bacedfb03", CardTier.Diamond, "Heavy", "SlowTargets", 1, 2),           // Monstrous Marble + Heavy (targets)
            ("aaf1216d-69b6-406e-98af-6a9bec12c8b4", CardTier.Bronze, "Fiery", "BurnApplyAmount", 4, 0),        // Magnifying Glass + Fiery (未算 e2 光环前 e1 基值为 0)
            ("2601a813-7cab-4fa5-aa13-d16ddd647198", CardTier.Silver, "Shielded", "ShieldApplyAmount", 25, 0),  // Attachment Set + Shielded (未算 e2 光环前 e1 基值为 0)
            ("0e3d2e2c-0f2d-4150-b6d6-d1c688d7427c", CardTier.Silver, "Turbo", "HasteAmount", 1000, 1000),      // Propeller Hat + Turbo
            ("1179ed38-2357-4718-a0c7-c30d56493d08", CardTier.Silver, "Icy", "FreezeAmount", 1000, 1000),       // Cryosleeve + Icy
            ("4caeadbe-7637-4e40-805d-39d4bfc08816", CardTier.Bronze, "Turbo", "HasteAmount", 1000, 2000),       // Flying Fish + Turbo
            ("7d718c5d-e50e-46dd-ada7-f3eb0f8a16b2", CardTier.Bronze, "Heavy", "SlowAmount", 1000, 1000),       // Chains + Heavy
            ("991efa4e-b4d6-4a20-9de5-81ce00c3e2ae", CardTier.Bronze, "Heavy", "SlowAmount", 1000, 1000),       // River Otter + Heavy
            ("2834ede9-01e9-4b68-8f1f-70f83187c78f", CardTier.Gold, "Turbo", "HasteTargets", 2, 2),             // Sharkray + Turbo (Gold)
            ("490cc4cf-fd8b-4517-b3a9-f7acf604b97c", CardTier.Diamond, "Turbo", "HasteTargets", 3, 1),          // Tugboat + Turbo (Diamond)
            ("cae6a187-7e4e-40fb-989b-4b84d59bda24", CardTier.Silver, "Heavy", "SlowAmount", 1000, 2000),       // Kusarigama + Heavy
            ("e01e1043-3417-40f6-953e-d6a7357f2168", CardTier.Bronze, "Turbo", "HasteAmount", 1000, 2000),      // Ulu Knife + Turbo
        };

        foreach (var (cardId, tier, ench, attr, expBase, expEnch) in all15Cases)
        {
            var card = new CardInstance(repo.GetCard(cardId)!, tier, ench);
            Assert.Equal(expBase, card.GetAttribute(attr));
            Assert.Equal(expBase, card.GetAttributeForAbility(attr, "0"));
            Assert.Equal(expEnch, card.GetAttributeForAbility(attr, "e1"));
        }

        // 2. 实战模拟核验 Magnifying Glass + Fiery（Bronze：Damage=20, 基础 Burn=4, e2 附魔光环仅给 e1 赋 0.1*20=2 -> 单次开火共 4+2=6 Burn，绝非 12）
        var mp1 = CreatePlayer("mg1", HeroNames.Mak, 3000);
        var mp2 = CreatePlayer("mg2", HeroNames.Vanessa, 3000);
        var magGlass = new CardInstance(repo.GetCard("aaf1216d-69b6-406e-98af-6a9bec12c8b4")!, CardTier.Bronze, "Fiery");
        Assert.True(mp1.Hand.TryAddAuto(magGlass));
        var mgRes = CombatSimulator.Simulate(repo, mp1, mp2, seed: 5101, recordEvents: true);
        var firstFireTime = mgRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == magGlass.InstanceId).TimeMs;
        var firstFireBurns = mgRes.Events
            .Where(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == magGlass.InstanceId && e.TimeMs == firstFireTime)
            .Select(e => (int)e.Value)
            .OrderBy(v => v)
            .ToList();
        Assert.Equal(new[] { 2, 4 }, firstFireBurns);

        // 3. 实战模拟核验 Attachment Set + Shielded（Silver：Damage=25, 基础 Shield=25, e2 附魔光环仅给 e1 赋 1.0*25=25 -> 单次护盾事件 25 与 25，绝非 50+50）
        var ap1 = CreatePlayer("as1", HeroNames.Stelle, 3000);
        var ap2 = CreatePlayer("as2", HeroNames.Vanessa, 3000);
        var attachSet = new CardInstance(repo.GetCard("2601a813-7cab-4fa5-aa13-d16ddd647198")!, CardTier.Silver, "Shielded");
        Assert.True(ap1.Hand.TryAddAuto(attachSet));
        AuraEngine.RecalculateAuras(ap1, ap2, RuleScope.InCombat, 1, 1, repo);
        Assert.Equal(25, attachSet.GetAttributeForAbility("ShieldApplyAmount", "0"));
        Assert.Equal(25, attachSet.GetAttributeForAbility("ShieldApplyAmount", "e1"));

        // 4. 实战模拟核验 Tantius Carrier + Obsidian（Legendary：基础 100 伤，e1 20 伤，绝非 120 + 120）
        var tp1 = CreatePlayer("tc1", HeroNames.Dooley, 5000);
        var tp2 = CreatePlayer("tc2", HeroNames.Vanessa, 5000);
        var tantius = new CardInstance(repo.GetCard("2e77aab4-4c38-4f0a-8953-696c55140d8e")!, CardTier.Legendary, "Obsidian");
        Assert.True(tp1.Hand.TryAddAuto(tantius));
        var tcRes = CombatSimulator.Simulate(repo, tp1, tp2, seed: 5102, recordEvents: true);
        var tcFirstFire = tcRes.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == tantius.InstanceId).TimeMs;
        var tcDmgs = tcRes.Events
            .Where(e => e.Type == CombatEventType.DamageApplied && e.SourceCardInstanceId == tantius.InstanceId && e.TimeMs == tcFirstFire)
            .Select(e => (int)e.Value)
            .ToList();
        Assert.Contains(100, tcDmgs);
        Assert.DoesNotContain(120, tcDmgs);

        // 5. 无害叠加例外不受误伤：Mecha Dragon + Shiny (Multicast=2+1=3)、Elixir of Immortality + Restorative (HealAmount=0+30=30)
        var mechaShiny = new CardInstance(repo.GetCard("894657f8-ab1e-4acf-b52e-538559f00d37")!, CardTier.Silver, "Shiny");
        Assert.Equal(3, mechaShiny.GetAttribute("Multicast"));

        var elixirRest = new CardInstance(repo.GetCard("fdb8f063-d430-4391-9dc4-79b9f9bda25b")!, CardTier.Legendary, "Restorative");
        Assert.Equal(30, elixirRest.GetAttribute("HealAmount"));
        Assert.Equal(30, elixirRest.GetAttributeForAbility("HealAmount", "e1"));
    }

    // =========================================================================
    // P1-52：1.0s 同跳 DoT 顺序严格为 Poison -> Regen -> Burn
    // =========================================================================

    [Fact]
    public void P1_52_DotOrderAt1000ms_IsStrictlyPoisonThenRegenThenBurn()
    {
        var repo = Repo.Value;

        // 场景 A：满血 1000 HP，在 950ms 同时获得 Regen=50 与 Burn=20 -> 1000ms 先 Regen（满血回 0）后 Burn（扣 20 至 980）
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);
        var lateRegen = CreateCustomItem("late_regen", "LateRegen", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerRegenApply","ReferenceValue":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        });
        var lateBurn = CreateCustomItem("late_burn", "LateBurn", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        });
        Assert.True(p1.Hand.TryAddAuto(lateRegen));
        Assert.True(p2.Hand.TryAddAuto(lateBurn));

        var resA = CombatSimulator.Simulate(repo, p1, p2, seed: 5201, recordTimeline: true, recordEvents: true);
        var dotAt1000A = resA.Events.Where(e => e.TimeMs == 1000 && e.TargetPlayerId == "p1" && e.Type is CombatEventType.RegenTick or CombatEventType.BurnTick).ToList();
        Assert.Equal(2, dotAt1000A.Count);
        Assert.Equal(CombatEventType.RegenTick, dotAt1000A[0].Type);
        Assert.Equal(CombatEventType.BurnTick, dotAt1000A[1].Type);
        Assert.Equal(0, dotAt1000A[0].EffectiveHpRestored);
        Assert.Equal(980, resA.Timeline.First(t => t.TimeMs == 1000).P1Hp);

        // 场景 B：950ms 同时挂 Poison=30、Regen=20、Burn=10，验证 1000ms 严格按 Poison -> Regen -> Burn 顺序结算
        var bp1 = CreatePlayer("bp1", HeroNames.Vanessa, 1000);
        var bp2 = CreatePlayer("bp2", HeroNames.Vanessa, 5000);
        Assert.True(bp1.Hand.TryAddAuto(CreateCustomItem("b_reg", "BReg", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerRegenApply","ReferenceValue":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""") }
        })));
        Assert.True(bp2.Hand.TryAddAuto(CreateCustomItem("b_pb", "BPB", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionPlayerPoisonApply","ReferenceValue":{"$type":"TFixedValue","Value":30},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}},{"$type":"TActionPlayerBurnApply","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}]}""") }
        })));

        var resB = CombatSimulator.Simulate(repo, bp1, bp2, seed: 5202, recordTimeline: true, recordEvents: true);
        var dotAt1000B = resB.Events
            .Where(e => e.TimeMs == 1000 && e.TargetPlayerId == "bp1" && e.Type is CombatEventType.PoisonTick or CombatEventType.RegenTick or CombatEventType.BurnTick)
            .Select(e => e.Type)
            .ToList();
        Assert.Equal(new[] { CombatEventType.PoisonTick, CombatEventType.RegenTick, CombatEventType.BurnTick }, dotAt1000B);
        Assert.Equal(980, resB.Timeline.First(t => t.TimeMs == 1000).P1Hp); // 1000 - 30 + 20 - 10 = 980
    }

    // =========================================================================
    // P1-53：无主动冷却卡与 CooldownDisabled 卡的控场计时（Freeze/Slow/Haste）正常递减并标脏光环
    // =========================================================================

    [Fact]
    public void P1_53_PassiveAndCooldownDisabledCards_DecayFreezeSlowHasteAndRefreshAuras()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);

        // 无 CD 被动卡（自带依赖 Freeze 的动态光环：冻结时给队友 +50 伤，解冻后立刻撤回）
        var passiveCard = CreateCustomItem("passive_ctrl", "PassiveCtrl", auras: new()
        {
            ["a0"] = new AuraDefinition
            {
                Id = "a0",
                ActiveIn = "HandOnly",
                Prerequisites =
                [
                    ParseNode("""{"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalAttribute","Attribute":"Freeze","ComparisonOperator":"GreaterThan","ComparisonValue":{"$type":"TFixedValue","Value":0}}},"Comparison":"GreaterThanOrEqual","Amount":1}""")
                ],
                Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":50},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
            }
        });
        var cdDisabledCard = CreateCustomItem("cd_dis_ctrl", "CdDisCtrl", attrs: new() { ["CooldownMax"] = 4000, ["CooldownDisabled"] = 1 });
        var allyProbe = CreateCustomItem("ally_probe", "AllyProbe", attrs: new() { ["CooldownMax"] = 10000, ["DamageAmount"] = 10 });

        var starterCtrl = CreateCustomItem("ctrl_apply", "CtrlApply", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionAnd","Actions":[{"$type":"TActionCardFreeze","ReferenceValue":{"$type":"TFixedValue","Value":1000},"Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand"}},{"$type":"TActionCardSlow","ReferenceValue":{"$type":"TFixedValue","Value":1000},"Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand"}}]}""")
            }
        });
        var selfHaster = CreateCustomItem("haste_apply", "HasteApply", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnFightStarted"}"""),
                Action = ParseNode("""{"$type":"TActionCardHaste","ReferenceValue":{"$type":"TFixedValue","Value":1000},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
            }
        });

        Assert.True(p1.Hand.TryAddAuto(passiveCard));
        Assert.True(p1.Hand.TryAddAuto(cdDisabledCard));
        Assert.True(p1.Hand.TryAddAuto(allyProbe));
        Assert.True(p1.Hand.TryAddAuto(selfHaster));
        Assert.True(p2.Hand.TryAddAuto(starterCtrl));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 5301, recordTimeline: true, recordEvents: true);
        var snap500 = res.Timeline.First(t => t.TimeMs == 500);
        var snap1000 = res.Timeline.First(t => t.TimeMs == 1000);

        var p500 = snap500.Cards.First(c => c.InstanceId == passiveCard.InstanceId);
        var d500 = snap500.Cards.First(c => c.InstanceId == cdDisabledCard.InstanceId);
        Assert.Equal(450, p500.FreezeMs);
        Assert.Equal(450, p500.SlowMs);
        Assert.Equal(450, p500.HasteMs);
        Assert.Equal(450, d500.FreezeMs);
        Assert.Equal(450, d500.SlowMs);
        Assert.Equal(450, d500.HasteMs);
        // t=500 仍在冻结中，动态光环生效 -> allyProbe Damage = 60
        Assert.Equal(60, snap500.Cards.First(c => c.InstanceId == allyProbe.InstanceId).DamageAmount);

        var p1000 = snap1000.Cards.First(c => c.InstanceId == passiveCard.InstanceId);
        var d1000 = snap1000.Cards.First(c => c.InstanceId == cdDisabledCard.InstanceId);
        Assert.Equal(0, p1000.FreezeMs);
        Assert.Equal(0, p1000.SlowMs);
        Assert.Equal(0, p1000.HasteMs);
        Assert.Equal(0, d1000.FreezeMs);
        Assert.Equal(0, d1000.SlowMs);
        Assert.Equal(0, d1000.HasteMs);
        // t=1000 被动卡解冻归零触发标脏并即时重算动态光环 -> allyProbe Damage 恢复 10
        Assert.Equal(10, snap1000.Cards.First(c => c.InstanceId == allyProbe.InstanceId).DamageAmount);
    }

    // =========================================================================
    // P1-54：单次超额 Charge 上限钳制 CooldownMax 且激活后清零不结转溢出
    // =========================================================================

    [Fact]
    public void P1_54_ExcessiveCharge_ClampedToCooldownMaxAndResetsToZeroAfterFire()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Dooley, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);

        // charger 仅 1 发弹药，于 950ms 给 receiver(CD=4000ms) 单次超额充能 5000ms
        var charger = CreateCustomItem("mega_charger", "MegaCharger", attrs: new() { ["CooldownMax"] = 1000, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionCardCharge","ReferenceValue":{"$type":"TFixedValue","Value":5000},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}""")
            }
        });
        var receiver = CreateCustomItem("recv_4s", "Recv4s", attrs: new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        // receiver 置 slot 0、charger 置 slot 1：t=950ms 先自然推进 receiver 至 1000ms，再由 charger 超额充能 5000ms -> 钳制 4000ms 触发并归零
        Assert.True(p1.Hand.TryPlaceOrShift(receiver, 0));
        Assert.True(p1.Hand.TryPlaceOrShift(charger, 1));

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 5401, recordTimeline: true, recordEvents: true);
        var fires = res.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == receiver.InstanceId)
            .Select(e => e.TimeMs)
            .ToList();

        Assert.True(fires.Count >= 2);
        Assert.Equal(950, fires[0]); // 950ms 超额充能当帧激活
        // 触发后进度必须归零（不结转 2000ms 溢出），第 2 次自然走满完整 4000ms -> 950 + 4000 = 4950ms
        Assert.Equal(4950, fires[1]);
        var snap950 = res.Timeline.First(t => t.TimeMs == 950).Cards.First(c => c.InstanceId == receiver.InstanceId);
        Assert.Equal(0f, snap950.Progress01);
    }

    // =========================================================================
    // P1-55：CombatResult.IsSimultaneousDeath / IsTimeout 标志 + CombatSimConverter IsDraw 尾帧兼容
    // =========================================================================

    [Fact]
    public void P1_55_SimultaneousDeathAndTimeoutFlags_AndCombatSimConverterIsDrawHandling()
    {
        var repo = Repo.Value;

        // 1. 同帧双杀：IsSimultaneousDeath == true, IsTimeout == false, 异步规则维持 IsDraw == false & Winner == player
        var sp1 = CreatePlayer("player", HeroNames.Vanessa, 100);
        var sp2 = CreatePlayer("opponent", HeroNames.Vanessa, 100);
        sp1.Hand.TryAddAuto(CreateCustomItem("k1", "K1", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 200 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }));
        sp2.Hand.TryAddAuto(CreateCustomItem("k2", "K2", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 200 }, abilities: new()
        {
            ["0"] = new AbilityDefinition { Id = "0", ActiveIn = "HandOnly", Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""), Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""") }
        }));
        var simDeathRes = CombatSimulator.Simulate(repo, sp1, sp2, seed: 5501, recordTimeline: true, recordEvents: true);
        Assert.True(simDeathRes.IsSimultaneousDeath);
        Assert.False(simDeathRes.IsTimeout);
        Assert.False(simDeathRes.IsDraw);
        Assert.Equal("player", simDeathRes.WinnerPlayerId);

        // 2. 超时双存（关闭沙暴且无输出）：IsTimeout == true, IsSimultaneousDeath == false
        var tp1 = CreatePlayer("player", HeroNames.Vanessa, 500);
        var tp2 = CreatePlayer("opponent", HeroNames.Vanessa, 500);
        var timeoutRes = CombatSimulator.Simulate(
            repo, tp1, tp2,
            seed: 5502,
            combatType: "PVP",
            recordTimeline: false,
            recordEvents: false,
            syncPermanentChanges: false,
            modifiers: null,
            sandstormEnabled: false);
        Assert.True(timeoutRes.IsTimeout);
        Assert.False(timeoutRes.IsSimultaneousDeath);
        Assert.False(timeoutRes.IsDraw);
        Assert.Equal("player", timeoutRes.WinnerPlayerId);

        // 3. CombatSimConverter.ToCombatSim 兼容外部 IsDraw=true：
        //    (a) IsDraw=true + IsSimultaneousDeath=true -> 尾帧双方均 IsPlayerDead=true 且发双 CombatantDied
        var drawDualDeath = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            IsDraw = true,
            IsSimultaneousDeath = true,
            IsTimeout = false,
            DurationMs = 1000,
            Player1RemainingHp = 0,
            Player2RemainingHp = 0,
            Timeline = simDeathRes.Timeline,
            Events = simDeathRes.Events
        };
        var simDual = CombatSimConverter.ToCombatSim(
            drawDualDeath, repo,
            new Dictionary<string, CardInstance>(),
            new Dictionary<string, CardInstance>(),
            Array.Empty<RunState.CardEntry>(),
            Array.Empty<RunState.CardEntry>());
        var lastDual = simDual.Frames[^1];
        Assert.True(simDual.IsDraw);
        Assert.True(lastDual.PlayerUpdates?.IsPlayerDead);
        Assert.True(lastDual.OpponentUpdates?.IsPlayerDead);
        Assert.Contains(lastDual.Events, e => e is CombatSimEventCombatantDied { CombatantId: ECombatantId.Player });
        Assert.Contains(lastDual.Events, e => e is CombatSimEventCombatantDied { CombatantId: ECombatantId.Opponent });

        //    (b) IsDraw=true + IsTimeout=true -> 尾帧双方均存活，不置 IsPlayerDead 且不发 CombatantDied
        var drawTimeout = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            IsDraw = true,
            IsSimultaneousDeath = false,
            IsTimeout = true,
            DurationMs = 1000,
            Player1RemainingHp = 500,
            Player2RemainingHp = 500,
            Timeline = new List<CombatTickState>
            {
                new() { TimeMs = 0, P1Hp = 500, P1MaxHp = 500, P2Hp = 500, P2MaxHp = 500 },
                new() { TimeMs = 1000, P1Hp = 500, P1MaxHp = 500, P2Hp = 500, P2MaxHp = 500 }
            }
        };
        var simTimeout = CombatSimConverter.ToCombatSim(
            drawTimeout, repo,
            new Dictionary<string, CardInstance>(),
            new Dictionary<string, CardInstance>(),
            Array.Empty<RunState.CardEntry>(),
            Array.Empty<RunState.CardEntry>());
        var lastTimeout = simTimeout.Frames[^1];
        Assert.True(simTimeout.IsDraw);
        Assert.False(lastTimeout.PlayerUpdates?.IsPlayerDead ?? false);
        Assert.False(lastTimeout.OpponentUpdates?.IsPlayerDead ?? false);
        Assert.DoesNotContain(lastTimeout.Events, e => e is CombatSimEventCombatantDied);
    }

    // =========================================================================
    // P1-56：TurboFrenzy（MinCooldownFloorMs = 500）单帧推进步长上限动态对齐，不被硬编码 1000ms 腰斩
    // =========================================================================

    [Fact]
    public void P1_56_TurboFrenzy_MinCooldownFloor500ms_FiresEvery500msWithout1000msCapThrottle()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 5000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 5000);

        // 1000ms 物品在 TurboFrenzy (CooldownReductionPct=0.5, MinCooldownFloorMs=500) 下有效冷却应为 500ms
        var fastKnife = CreateCustomItem("turbo_knife", "TurboKnife", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 5 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        Assert.True(p1.Hand.TryAddAuto(fastKnife));

        var turboMod = new CombatModifiers
        {
            CooldownReductionPct = 0.5,
            MinCooldownFloorMs = 500
        };
        var res = CombatSimulator.Simulate(
            repo, p1, p2,
            seed: 5601,
            combatType: "PVP",
            recordTimeline: true,
            recordEvents: true,
            syncPermanentChanges: false,
            modifiers: turboMod);

        var first4Fires = res.Events
            .Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == fastKnife.InstanceId)
            .Take(4)
            .Select(e => e.TimeMs)
            .ToList();

        Assert.Equal(new[] { 450, 950, 1450, 1950 }, first4Fires);
    }

    // =========================================================================
    // 测试构造辅助方法
    // =========================================================================

    private static PlayerState CreatePlayer(string id, string hero, int hp, int initialGold = 10)
        => new(id, id, hero, initialHealthMax: hp, initialPrestige: 20, initialGold: initialGold, unlockedHandSlots: 10);

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    private static CardInstance CreateCustomItem(
        string id,
        string name,
        Dictionary<string, double>? attrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        List<string>? tags = null)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Attributes = attrs ?? new Dictionary<string, double>(),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Tags = tags ?? new List<string>()
        };
        return new CardInstance(tpl, CardTier.Bronze, null, id + "_inst");
    }
}
