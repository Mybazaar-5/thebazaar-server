using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V15.6 模块 D3：Flying 停飞引擎语义修复单元测试。
/// 覆盖 PermanentAdd / DayAdd / HourAdd / AuraAdd / CombatAdd 各类飞行来源的
/// 停飞落地、再起飞、Toggle 连续切换、战斗模拟器控场减半与战后 ResetForCombat 恢复语义。
/// </summary>
public sealed class V15_6_FlyingStopSemanticsTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private static bool IsCardEffectivelyFlying(CardInstance card)
        => card.IsFlying || card.GetAttribute("Flying") > 0 || card.HasTag("Flying");

    [Fact]
    public void PermanentFlying_StartStopRestartAndPostCombatReset_PreservesPermanentLayer()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);

        var flyer = CreateDummyItem("perm_flyer", cooldownMs: 4000);
        flyer.PermanentAdd["Flying"] = 1;
        flyer.MarkAttributeDirty();
        p1.Hand.TryAddAuto(flyer);

        var actor = CreateDummyItem("fly_actor");
        p2.Hand.TryAddAuto(actor);

        // 模拟进战前复位（验证 ResetForCombat 不误设 IsFlying=true，开局 GetAttribute("Flying") 严格为 1 而非 2）
        flyer.ResetForCombat(repo);
        Assert.False(flyer.IsFlying);
        Assert.Equal(1.0, flyer.GetAttribute("Flying"));
        Assert.True(IsCardEffectivelyFlying(flyer));

        var combatEvents = new List<CombatEvent>();
        var triggered = new List<(string EventType, string? Attr, double Prev, double Cur)>();
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(1))
        {
            SelfCard = actor,
            EmitCombatEvent = combatEvents.Add,
            OnDetailedTriggeredEvent = (evType, _, _, _, _, attr, prev, cur) =>
                triggered.Add((evType, attr, prev, cur))
        };

        var stopAction = new RuleNode
        {
            Type = "TActionCardFlyingStop",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };
        var startAction = new RuleNode
        {
            Type = "TActionCardFlyingStart",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };

        // 1. 被 TActionCardFlyingStop 命中后立即落地，且不破坏 PermanentAdd
        AbilityExecutor.ExecuteAction(stopAction, actor, ctx);

        Assert.False(flyer.IsFlying);
        Assert.Equal(0.0, flyer.GetAttribute("Flying"));
        Assert.False(IsCardEffectivelyFlying(flyer));
        Assert.Equal(1.0, flyer.PermanentAdd.GetValueOrDefault("Flying", 0));
        Assert.Equal(-1.0, flyer.CombatAdd.GetValueOrDefault("Flying", 0));
        Assert.Contains(combatEvents, e => e.Type == CombatEventType.FlyingChanged && e.Value == 0 && e.TargetCardInstanceId == flyer.InstanceId);
        Assert.Contains(triggered, t => t.EventType == "TTriggerOnCardStoppedFlying");
        Assert.Contains(triggered, t => t.EventType == "TTriggerOnCardAttributeChanged" && t.Attr == "Flying" && t.Prev == 1 && t.Cur == 0);

        // 2. 已落地时再次执行 FlyingStop 应无副作用（不叠加负偏移、不重复发事件）
        int eventsBeforeNoop = combatEvents.Count;
        AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
        Assert.Equal(eventsBeforeNoop, combatEvents.Count);
        Assert.Equal(-1.0, flyer.CombatAdd.GetValueOrDefault("Flying", 0));

        // 3. 被 TActionCardFlyingStart 命中后可重新起飞，且清除 CombatAdd 负压制
        combatEvents.Clear();
        triggered.Clear();
        AbilityExecutor.ExecuteAction(startAction, actor, ctx);

        Assert.True(flyer.IsFlying);
        Assert.True(flyer.GetAttribute("Flying") > 0);
        Assert.True(IsCardEffectivelyFlying(flyer));
        Assert.False(flyer.CombatAdd.ContainsKey("Flying"));
        Assert.Equal(1.0, flyer.PermanentAdd.GetValueOrDefault("Flying", 0));
        Assert.Contains(combatEvents, e => e.Type == CombatEventType.FlyingChanged && e.Value == 1 && e.TargetCardInstanceId == flyer.InstanceId);
        Assert.Contains(triggered, t => t.EventType == "TTriggerOnCardStartedFlying");
        Assert.Contains(triggered, t => t.EventType == "TTriggerOnCardStartsFlying");

        // 4. 重新起飞后再次被 FlyingStop 命中，仍可干净落地
        AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
        Assert.False(flyer.IsFlying);
        Assert.Equal(0.0, flyer.GetAttribute("Flying"));
        Assert.False(IsCardEffectivelyFlying(flyer));
        Assert.Equal(-1.0, flyer.CombatAdd.GetValueOrDefault("Flying", 0));

        // 5. 战后 ResetForCombat 清理战斗层偏移，PermanentAdd["Flying"] 仍为 1 且 GetAttribute("Flying") == 1
        flyer.ResetForCombat(repo);
        Assert.False(flyer.IsFlying);
        Assert.False(flyer.CombatAdd.ContainsKey("Flying"));
        Assert.Equal(1.0, flyer.PermanentAdd.GetValueOrDefault("Flying", 0));
        Assert.Equal(1.0, flyer.GetAttribute("Flying"));
        Assert.True(IsCardEffectivelyFlying(flyer));
    }

    [Fact]
    public void PermanentFlying_InCombatSimulator_StopsFlyingRestoresFullSlowDuration_AndRestartsFlying()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 5000, 10, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 5000, 10, 10, 10);

        // P1 核心目标卡：开局自带 PermanentAdd["Flying"] = 1，带唯一标识标签 "Tool" 供精准定向
        var flyer = CreateSimpleActiveItem(
            "perm_flyer_sim",
            cooldownMs: 9000,
            new RuleNode { Type = "TActionPlayerDamage", Target = TargetOpponentPlayer(), ReferenceValue = Fixed(1) },
            tags: ["Tool"]);
        flyer.PermanentAdd["Flying"] = 1;
        flyer.MarkAttributeDirty();
        p1.Hand.TryAddAuto(flyer);

        // P1 辅助卡：4000ms 时令友方 Tool 重新起飞（AmmoMax=1 仅触发一次）
        var restarter = CreateSimpleActiveItem(
            "p1_fly_restarter",
            cooldownMs: 4000,
            new RuleNode
            {
                Type = "TActionCardFlyingStart",
                Target = new RuleNode
                {
                    Type = "TTargetCardSection",
                    TargetSection = "SelfHand",
                    RawConditionsElement = JsonTagCondition("Tool")
                }
            },
            ammoMax: 1);
        p1.Hand.TryAddAuto(restarter);

        // P2-1：开战 0ms 对 P1 Tool 施加 1000ms Slow（开局飞行态，应减半为 500ms）
        p2.Hand.TryAddAuto(CreateStartFightItem("p2_slow_t0", new RuleNode
        {
            Type = "TActionCardSlow",
            Target = new RuleNode
            {
                Type = "TTargetCardSection",
                TargetSection = "OpponentHand",
                RawConditionsElement = JsonTagCondition("Tool")
            },
            ReferenceValue = Fixed(1000)
        }));

        // P2-2：1000ms 对 P1 Tool 触发 TActionCardFlyingStop（AmmoMax=1 仅停飞一次）
        p2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "p2_stop_t1000",
            cooldownMs: 1000,
            new RuleNode
            {
                Type = "TActionCardFlyingStop",
                Target = new RuleNode
                {
                    Type = "TTargetCardSection",
                    TargetSection = "OpponentHand",
                    RawConditionsElement = JsonTagCondition("Tool")
                }
            },
            ammoMax: 1));

        // P2-3：2000ms 对 P1 Tool 施加 1000ms Slow（已落地，应吃满 1000ms，AmmoMax=1）
        p2.Hand.TryAddAuto(CreateSimpleActiveItem(
            "p2_slow_t2000",
            cooldownMs: 2000,
            new RuleNode
            {
                Type = "TActionCardSlow",
                Target = new RuleNode
                {
                    Type = "TTargetCardSection",
                    TargetSection = "OpponentHand",
                    RawConditionsElement = JsonTagCondition("Tool")
                },
                ReferenceValue = Fixed(1000)
            },
            ammoMax: 1));

        // P2-4：5000ms 对 P1 Tool 施加 1000ms Slow（4000ms 已重新起飞，应再次减半为 500ms，AmmoMax=1）
        var slowT5000 = CreateSimpleActiveItem(
            "p2_slow_t5000",
            cooldownMs: 5000,
            new RuleNode
            {
                Type = "TActionCardSlow",
                Target = new RuleNode
                {
                    Type = "TTargetCardSection",
                    TargetSection = "OpponentHand",
                    RawConditionsElement = JsonTagCondition("Tool")
                },
                ReferenceValue = Fixed(1000)
            },
            ammoMax: 1);
        p2.Hand.TryAddAuto(slowT5000);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2026, syncPermanentChanges: true);

        // 校验 0ms / 2000ms / 5000ms 三次 Slow 的实际控场时长：500 -> 1000 -> 500
        var slowEvents = res.Events
            .Where(e => e.Type == CombatEventType.SlowApplied && e.TargetCardInstanceId == flyer.InstanceId)
            .OrderBy(e => e.TimeMs)
            .ToList();
        Assert.True(slowEvents.Count >= 3, $"期望至少 3 次 SlowApplied 事件，实际为 {slowEvents.Count}");
        Assert.Equal(500, (int)slowEvents[0].Value);
        Assert.Equal(1000, (int)slowEvents[1].Value);
        Assert.Equal(500, (int)slowEvents[2].Value);

        // 校验战斗时间线快照中的 IsCardFlying 投影：开局 true -> 停飞后 false -> 再起飞后 true
        bool SnapshotFlyingAt(int timeMs)
        {
            var frame = res.Timeline.First(t => t.TimeMs >= timeMs);
            var cardFrame = frame.Cards.First(c => c.InstanceId == flyer.InstanceId);
            return cardFrame.IsFlying;
        }
        Assert.True(SnapshotFlyingAt(0), "开局带 PermanentAdd[Flying]=1 的卡快照应为飞行态");
        Assert.False(SnapshotFlyingAt(2000), "1000ms 被 FlyingStop 命中后，2000ms 快照应为落地态");
        Assert.True(SnapshotFlyingAt(4500), "4000ms 被 FlyingStart 命中后，4500ms 快照应重新为飞行态");

        // 校验 FlyingChanged 事件序列：1000ms 停飞(0) -> 4000ms 再起飞(1)
        var flyEvents = res.Events
            .Where(e => e.Type == CombatEventType.FlyingChanged && e.TargetCardInstanceId == flyer.InstanceId)
            .OrderBy(e => e.TimeMs)
            .ToList();
        Assert.True(flyEvents.Count >= 2);
        Assert.Equal(0, (int)flyEvents[0].Value);
        Assert.Equal(1, (int)flyEvents[1].Value);

        // 战后原始盘面卡 PermanentAdd["Flying"] 完整保留为 1，且 GetAttribute("Flying") 仍为 1（不是 2）
        Assert.Equal(1.0, flyer.PermanentAdd.GetValueOrDefault("Flying", 0));
        Assert.False(flyer.IsFlying);
        Assert.Equal(1.0, flyer.GetAttribute("Flying"));
    }

    [Fact]
    public void AuraOnlyFlying_StopThenAuraRecalcThenStart_MatchesPreFixBehavior()
    {
        var repo = Repo.Value;
        var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);

        var weapon = CreateDummyItem("aura_target_weapon", tags: ["Weapon"]);
        var auraSkill = CreateAuraFlyingSkill("aura_fly_skill", "Weapon");
        p1.Hand.TryAddAuto(weapon);
        p1.Skills.Add(auraSkill);

        var actor = CreateDummyItem("aura_actor");
        p2.Hand.TryAddAuto(actor);

        // 光环赋予飞行
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);
        Assert.False(weapon.IsFlying);
        Assert.Equal(1.0, weapon.AuraAdd.GetValueOrDefault("Flying", 0));
        Assert.Equal(1.0, weapon.GetAttribute("Flying"));
        Assert.True(IsCardEffectivelyFlying(weapon));

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(2))
        {
            SelfCard = actor
        };
        var stopAction = new RuleNode
        {
            Type = "TActionCardFlyingStop",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };
        var startAction = new RuleNode
        {
            Type = "TActionCardFlyingStart",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };

        // 1. 停飞立即落地
        AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
        Assert.False(weapon.IsFlying);
        Assert.Equal(0.0, weapon.GetAttribute("Flying"));
        Assert.False(IsCardEffectivelyFlying(weapon));
        Assert.Equal(-1.0, weapon.CombatAdd.GetValueOrDefault("Flying", 0));

        // 2. 光环反复重算后，不得误将已停飞的卡重新拉起飞
        var transitions = new List<AuraEngine.AuraFlyingTransition>();
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo, selectionCards: null, onFlyingTransition: transitions.Add);
        Assert.Empty(transitions);
        Assert.Equal(1.0, weapon.AuraAdd.GetValueOrDefault("Flying", 0));
        Assert.Equal(-1.0, weapon.CombatAdd.GetValueOrDefault("Flying", 0));
        Assert.Equal(0.0, weapon.GetAttribute("Flying"));
        Assert.False(IsCardEffectivelyFlying(weapon));

        // 3. 再起飞移除负压制并恢复飞行态
        AbilityExecutor.ExecuteAction(startAction, actor, ctx);
        Assert.True(weapon.IsFlying);
        Assert.False(weapon.CombatAdd.ContainsKey("Flying"));
        Assert.True(weapon.GetAttribute("Flying") >= 1.0);
        Assert.True(IsCardEffectivelyFlying(weapon));

        // 4. 再次停飞仍可落地
        AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
        Assert.False(weapon.IsFlying);
        Assert.Equal(0.0, weapon.GetAttribute("Flying"));
        Assert.False(IsCardEffectivelyFlying(weapon));
    }

    [Fact]
    public void FlyingToggle_TwiceReturnsToOriginalState_ForPermanentAuraAndNormalCards()
    {
        var repo = Repo.Value;

        var toggleAction = new RuleNode
        {
            Type = "TActionCardFlyingToggle",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };

        // (a) PermanentAdd["Flying"] = 1
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var permCard = CreateDummyItem("toggle_perm");
            permCard.PermanentAdd["Flying"] = 1;
            permCard.ResetForCombat(repo);
            p1.Hand.TryAddAuto(permCard);
            var actor = CreateDummyItem("actor_a");
            p2.Hand.TryAddAuto(actor);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(3)) { SelfCard = actor };

            Assert.True(IsCardEffectivelyFlying(permCard));
            Assert.Equal(1.0, permCard.GetAttribute("Flying"));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(permCard));
            Assert.Equal(0.0, permCard.GetAttribute("Flying"));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(permCard));
            Assert.True(permCard.IsFlying);
            Assert.False(permCard.CombatAdd.ContainsKey("Flying"));

            // 第三次 Toggle 确认周期可重复
            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(permCard));
            Assert.Equal(0.0, permCard.GetAttribute("Flying"));

            permCard.ResetForCombat(repo);
            Assert.Equal(1.0, permCard.PermanentAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(1.0, permCard.GetAttribute("Flying"));
        }

        // (b) 仅 AuraAdd["Flying"] = 1
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var auraCard = CreateDummyItem("toggle_aura", tags: ["Weapon"]);
            p1.Hand.TryAddAuto(auraCard);
            p1.Skills.Add(CreateAuraFlyingSkill("toggle_aura_skill", "Weapon"));
            var actor = CreateDummyItem("actor_b");
            p2.Hand.TryAddAuto(actor);
            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(4)) { SelfCard = actor };

            Assert.True(IsCardEffectivelyFlying(auraCard));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(auraCard));
            Assert.Equal(0.0, auraCard.GetAttribute("Flying"));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(auraCard));
            Assert.False(auraCard.CombatAdd.ContainsKey("Flying"));
        }

        // (c) 普通无飞行卡
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var normalCard = CreateDummyItem("toggle_normal");
            p1.Hand.TryAddAuto(normalCard);
            var actor = CreateDummyItem("actor_c");
            p2.Hand.TryAddAuto(actor);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(5)) { SelfCard = actor };

            Assert.False(IsCardEffectivelyFlying(normalCard));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(normalCard));
            Assert.Equal(1.0, normalCard.GetAttribute("Flying"));

            AbilityExecutor.ExecuteAction(toggleAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(normalCard));
            Assert.Equal(0.0, normalCard.GetAttribute("Flying"));
            Assert.False(normalCard.CombatAdd.ContainsKey("Flying"));
        }
    }

    [Fact]
    public void DayAddHourAddStackedAndCombatOnlyFlying_StopAndRestart_OnlyMutateCombatLayer()
    {
        var repo = Repo.Value;

        var stopAction = new RuleNode
        {
            Type = "TActionCardFlyingStop",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };
        var startAction = new RuleNode
        {
            Type = "TActionCardFlyingStart",
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "OpponentHand" }
        };

        // 1. DayAdd["Flying"] = 1
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var card = CreateDummyItem("day_flyer");
            card.DayAdd["Flying"] = 1;
            card.ResetForCombat(repo);
            p1.Hand.TryAddAuto(card);
            var actor = CreateDummyItem("actor_day");
            p2.Hand.TryAddAuto(actor);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(6)) { SelfCard = actor };

            Assert.Equal(1.0, card.GetAttribute("Flying"));
            AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(card));
            Assert.Equal(0.0, card.GetAttribute("Flying"));
            Assert.Equal(1.0, card.DayAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(-1.0, card.CombatAdd.GetValueOrDefault("Flying", 0));

            AbilityExecutor.ExecuteAction(startAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(card));
            Assert.False(card.CombatAdd.ContainsKey("Flying"));

            card.ResetForCombat(repo);
            Assert.Equal(1.0, card.DayAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(1.0, card.GetAttribute("Flying"));
        }

        // 2. HourAdd["Flying"] = 1
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var card = CreateDummyItem("hour_flyer");
            card.HourAdd["Flying"] = 1;
            card.ResetForCombat(repo);
            p1.Hand.TryAddAuto(card);
            var actor = CreateDummyItem("actor_hour");
            p2.Hand.TryAddAuto(actor);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(7)) { SelfCard = actor };

            Assert.Equal(1.0, card.GetAttribute("Flying"));
            AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(card));
            Assert.Equal(0.0, card.GetAttribute("Flying"));
            Assert.Equal(1.0, card.HourAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(-1.0, card.CombatAdd.GetValueOrDefault("Flying", 0));

            AbilityExecutor.ExecuteAction(startAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(card));
            Assert.False(card.CombatAdd.ContainsKey("Flying"));

            card.ResetForCombat(repo);
            Assert.Equal(1.0, card.HourAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(1.0, card.GetAttribute("Flying"));
        }

        // 3. PermanentAdd + AuraAdd 双层叠加（总和 2）同时停飞，应一次性抵消 -2 落地，战后保留 PermanentAdd
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var card = CreateDummyItem("stacked_flyer", tags: ["Weapon"]);
            card.PermanentAdd["Flying"] = 1;
            p1.Hand.TryAddAuto(card);
            p1.Skills.Add(CreateAuraFlyingSkill("stacked_aura_skill", "Weapon"));
            var actor = CreateDummyItem("actor_stacked");
            p2.Hand.TryAddAuto(actor);
            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(8)) { SelfCard = actor };

            Assert.Equal(2.0, card.GetAttribute("Flying"));

            AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(card));
            Assert.Equal(0.0, card.GetAttribute("Flying"));
            Assert.Equal(-2.0, card.CombatAdd.GetValueOrDefault("Flying", 0));
            Assert.Equal(1.0, card.PermanentAdd.GetValueOrDefault("Flying", 0));

            AbilityExecutor.ExecuteAction(startAction, actor, ctx);
            Assert.True(IsCardEffectivelyFlying(card));
            Assert.False(card.CombatAdd.ContainsKey("Flying"));

            card.ResetForCombat(repo);
            Assert.Equal(1.0, card.PermanentAdd.GetValueOrDefault("Flying", 0));
        }

        // 4. 仅 CombatAdd["Flying"] = 1 停飞时，应移除 CombatAdd["Flying"]（严守零幽灵键不变量，不写入 0）
        {
            var p1 = new PlayerState("p1", "P1", HeroNames.Stelle, 1000, 10, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Vanessa, 1000, 10, 10, 10);
            var card = CreateDummyItem("combat_only_flyer");
            card.CombatAdd["Flying"] = 1;
            card.MarkAttributeDirty();
            p1.Hand.TryAddAuto(card);
            var actor = CreateDummyItem("actor_combat_only");
            p2.Hand.TryAddAuto(actor);
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p2, p1, new Random(9)) { SelfCard = actor };

            Assert.Equal(1.0, card.GetAttribute("Flying"));
            AbilityExecutor.ExecuteAction(stopAction, actor, ctx);
            Assert.False(IsCardEffectivelyFlying(card));
            Assert.Equal(0.0, card.GetAttribute("Flying"));
            Assert.False(card.CombatAdd.ContainsKey("Flying"));
        }
    }

    private static RuleNode Fixed(double value) => new() { Type = "TFixedValue", RawValueNode = JsonSerializer.SerializeToElement(value) };

    private static RuleNode TargetOpponentPlayer() => new() { Type = "TTargetPlayerRelative", TargetMode = "Opponent" };

    private static JsonElement JsonTagCondition(string tag)
        => JsonDocument.Parse($$"""{"$type":"TCardConditionalTag","Tags":["{{tag}}"],"Operator":"Any"}""").RootElement.Clone();

    private static JsonElement JsonFixed(double value)
        => JsonDocument.Parse($$"""{"$type":"TFixedValue","Value":{{value}}}""").RootElement.Clone();

    private static CardInstance CreateDummyItem(string id, int cooldownMs = 0, List<string>? tags = null)
    {
        var attrs = new Dictionary<string, double>();
        if (cooldownMs > 0) attrs["CooldownMax"] = cooldownMs;
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = tags ?? new List<string>(),
            Attributes = attrs
        };
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
    }

    private static CardInstance CreateSimpleActiveItem(
        string id,
        int cooldownMs,
        RuleNode action,
        List<string>? tags = null,
        int? ammoMax = null)
    {
        var attrs = new Dictionary<string, double> { ["CooldownMax"] = cooldownMs };
        if (ammoMax.HasValue) attrs["AmmoMax"] = ammoMax.Value;
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tags = tags ?? new List<string>(),
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
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
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
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
    }

    private static CardInstance CreateAuraFlyingSkill(string id, string targetTag)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["aura_fly"] = new()
                {
                    Id = "aura_fly",
                    ActiveIn = "HandAndStash",
                    WorksIn = "Anywhere",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = "Flying",
                        Operation = "Add",
                        RawValueNode = JsonFixed(1),
                        Target = new RuleNode
                        {
                            Type = "TTargetCardSection",
                            TargetSection = "SelfHand",
                            RawConditionsElement = JsonTagCondition(targetTag)
                        }
                    }
                }
            }
        };
        return new CardInstance(tpl, CardTier.Bronze, null, instanceId: id);
    }
}
