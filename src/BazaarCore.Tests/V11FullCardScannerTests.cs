using System.Collections.Concurrent;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V11 全量卡牌执行、战斗模拟、随机附魔、棋盘挤位与 3,442 卡自动化扫描测试套件：
/// 1. 补齐的 5 个 Trigger（TTriggerOnCardPerformedCharge / TTriggerOnPlayerLeveledUp / TTriggerOnPlayerLifeStealed / TTriggerOnPlayerShielded / TTriggerOnRunCompleted）；
/// 2. 零冷却被动件（HANDOFF_FINDINGS #21）在局内全部被动触发/任务/光环正常运转；
/// 3. TActionCardEnchantRandom 精确权重、白名单、PreventOverride、TargetCount 与 UntilEndOfCombat / UntilEndOfDay 战后清理；
/// 4. BoardGrid 放置位移全量追踪与 SpawnQueryEngine ExclusionSet 过滤；
/// 5. 未知 $type 遥测与全部 3,442 张模板 + 12 种附魔 + 1,201 个遭遇 + 30 级×8 英雄升级全量无异常扫描。
/// </summary>
public sealed class V11FullCardScannerTests
{
    private const string AuthoritativeDbPath = @"e:\thebazaar\dist\data\GameData.db";

    private static readonly Lazy<GameDataRepository> Repo = new(() =>
    {
        if (File.Exists(AuthoritativeDbPath))
            return GameDatabaseLoader.Load(AuthoritativeDbPath);
        return GameDatabaseLoader.Load();
    });

    private static readonly string[] AllTwelveEnchantments =
    [
        "Heavy", "Icy", "Turbo", "Shielded", "Restorative", "Toxic",
        "Mossy", "Fiery", "Shiny", "Deadly", "Radiant", "Obsidian"
    ];

    private static readonly string[] AllEightHeroes =
    [
        HeroNames.Vanessa, HeroNames.Pygmalien, HeroNames.Dooley, HeroNames.Mak,
        HeroNames.Jules, HeroNames.Stelle, HeroNames.Karnok, HeroNames.Hero8
    ];

    private static readonly string[] AllAnchorTags =
    [
        "Weapon", "Shield", "Burn", "Poison", "Ammo", "Property",
        "Friend", "Tech", "Potion", "Food", "Relic", "Loot",
        "Tool", "Apparel", "Core", "Aquatic", "Toy", "Dinosaur", "Dragon"
    ];

    private static JsonElement JsonFixed(double val)
        => JsonDocument.Parse($"{{\"$type\":\"TFixedValue\",\"Value\":{val.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone();

    private static CardTemplate MakeSyntheticTemplate(
        string id,
        string size = "Small",
        string cardType = "Item",
        IEnumerable<string>? tags = null,
        IEnumerable<string>? hiddenTags = null,
        Dictionary<string, double>? baseAttrs = null,
        Dictionary<string, AbilityDefinition>? abilities = null,
        Dictionary<string, AuraDefinition>? auras = null,
        Dictionary<string, EnchantmentData>? enchantments = null,
        List<QuestDefinition>? quests = null)
    {
        return new CardTemplate
        {
            Id = id,
            InternalName = id,
            Type = cardType,
            Size = size,
            StartingTier = "Bronze",
            Tags = tags?.ToList() ?? [],
            HiddenTags = hiddenTags?.ToList() ?? [],
            Attributes = baseAttrs ?? new Dictionary<string, double>(),
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>(),
            Auras = auras ?? new Dictionary<string, AuraDefinition>(),
            Enchantments = enchantments ?? new Dictionary<string, EnchantmentData>(),
            Quests = quests ?? []
        };
    }

    private static GameDataRepository CreateRepoWithExtraCards(params CardTemplate[] extra)
    {
        var baseRepo = Repo.Value;
        var cards = new Dictionary<string, CardTemplate>(baseRepo.CardsById, StringComparer.OrdinalIgnoreCase);
        foreach (var c in extra) cards[c.Id] = c;
        return new GameDataRepository(
            baseRepo.SourceZipPath,
            baseRepo.GameMode,
            cards,
            new Dictionary<string, MonsterTemplate>(baseRepo.MonstersById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, LevelUpTemplate>(baseRepo.LevelUps),
            new Dictionary<string, string>(baseRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(baseRepo.ObservedRuleTypes, StringComparer.Ordinal),
            new HashSet<string>(baseRepo.UnknownRuleTypes, StringComparer.Ordinal),
            baseRepo.Localization,
            baseRepo.SourceDataPath);
    }

    // =========================================================================
    // 1. 补齐 5 个缺失 Trigger 验证
    // =========================================================================

    [Fact]
    public void Missing5Triggers_ChargeShieldLifeStealLevelUpRunCompleted_ShouldMatchAndDispatchAccurately()
    {
        // A. TTriggerOnCardPerformedCharge + TTriggerOnPlayerShielded + TTriggerOnPlayerLifeStealed
        var chargerTpl = MakeSyntheticTemplate(
            "v11_charger",
            baseAttrs: new() { ["CooldownMax"] = 1000, ["ChargeAmount"] = 500 },
            abilities: new()
            {
                ["ab_charge"] = new()
                {
                    Id = "ab_charge",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionCardCharge",
                        Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand", ExcludeSelf = true }
                    }
                }
            });

        var chargeReceiverTpl = MakeSyntheticTemplate(
            "v11_charge_receiver",
            baseAttrs: new() { ["CooldownMax"] = 4000, ["DamageAmount"] = 10 });

        // 监听 TTriggerOnCardPerformedCharge -> 给自己加盾 50
        var chargeWatcherTpl = MakeSyntheticTemplate(
            "v11_charge_watcher",
            baseAttrs: new() { ["ShieldApplyAmount"] = 50 },
            abilities: new()
            {
                ["ab_watch_charge"] = new()
                {
                    Id = "ab_watch_charge",
                    Trigger = new RuleNode { Type = "TTriggerOnCardPerformedCharge" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerShieldApply",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }
                    }
                }
            });

        // 监听 TTriggerOnPlayerShielded（> 20）-> 造成 100 点带吸血伤害
        var shieldWatcherTpl = MakeSyntheticTemplate(
            "v11_shield_watcher",
            baseAttrs: new() { ["DamageAmount"] = 100, ["Lifesteal"] = 1 },
            abilities: new()
            {
                ["ab_watch_shield"] = new()
                {
                    Id = "ab_watch_shield",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnPlayerShielded",
                        ComparisonOperator = "GreaterThan",
                        RawComparisonValueElement = JsonFixed(20)
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            });

        // 监听 TTriggerOnPlayerLifeStealed -> 给予对方 Burn 15
        var lifestealWatcherTpl = MakeSyntheticTemplate(
            "v11_lifesteal_watcher",
            baseAttrs: new() { ["BurnApplyAmount"] = 15 },
            abilities: new()
            {
                ["ab_watch_ls"] = new()
                {
                    Id = "ab_watch_ls",
                    Trigger = new RuleNode
                    {
                        Type = "TTriggerOnPlayerLifeStealed",
                        ComparisonAmount = 0,
                        ComparisonOperator = "GreaterThan"
                    },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerBurnApply",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            });

        var synthRepo = CreateRepoWithExtraCards(chargerTpl, chargeReceiverTpl, chargeWatcherTpl, shieldWatcherTpl, lifestealWatcherTpl);
        var p1 = new PlayerState("p1", "P1", HeroNames.Vanessa, 1000, 20, 10, 10);
        var p2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 1000, 20, 10, 10);

        p1.Hand.TryAddAuto(new CardInstance(chargerTpl));
        p1.Hand.TryAddAuto(new CardInstance(chargeReceiverTpl));
        p1.Hand.TryAddAuto(new CardInstance(chargeWatcherTpl));
        p1.Hand.TryAddAuto(new CardInstance(shieldWatcherTpl));
        p1.Hand.TryAddAuto(new CardInstance(lifestealWatcherTpl));
        // 先压低血量使 Lifesteal 产生真实回血 (>0)
        p1.CurrentHealth = 500;
        // CloneForCombat 会重置血量为 HealthMax，因此安排 p2 在开局瞬间先打掉 p1 300 HP
        var oppBurstTpl = MakeSyntheticTemplate(
            "v11_opp_burst",
            baseAttrs: new() { ["DamageAmount"] = 300 },
            abilities: new()
            {
                ["ab_burst"] = new()
                {
                    Id = "ab_burst",
                    Trigger = new RuleNode { Type = "TTriggerOnFightStarted" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerDamage",
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            });
        p2.Hand.TryAddAuto(new CardInstance(oppBurstTpl));

        var simRes = CombatSimulator.Simulate(synthRepo, p1, p2, seed: 7, combatType: "PVP", recordTimeline: false, recordEvents: true);
        Assert.True(simRes.Player1TotalShieldGained >= 50, "TTriggerOnCardPerformedCharge 应触发加盾 50");
        Assert.Contains(simRes.Events, e => e.Type == CombatEventType.HealApplied && e.Detail == "Lifesteal");
        Assert.Contains(simRes.Events, e => e.Type == CombatEventType.BurnApplied && e.Value >= 15);

        // 同时验证 AbilityExecutor 直接调用 TActionPlayerShieldApply & TActionCardCharge 触发对应事件（当无外部 CombatSimulator 钩子时）
        var directEvents = new List<string>();
        var directPlayer = new PlayerState("dp", "DP", HeroNames.Vanessa, 500, 20, 10, 10);
        var directCard = new CardInstance(chargeReceiverTpl);
        directPlayer.Hand.TryAddAuto(directCard);
        var directCtx = new RuleExecutionContext(synthRepo, RuleScope.InCombat, directPlayer, null, new Random(1))
        {
            SelfCard = directCard,
            OnDetailedTriggeredEvent = (ev, _, _, _, _, _, _, _) => directEvents.Add(ev)
        };
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionPlayerShieldApply",
            RawValueNode = JsonFixed(30),
            Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }
        }, directCard, directCtx);
        AbilityExecutor.ExecuteAction(new RuleNode
        {
            Type = "TActionCardCharge",
            RawValueNode = JsonFixed(400),
            Target = new RuleNode { Type = "TTargetCardSelf" }
        }, directCard, directCtx);
        Assert.Contains("TTriggerOnPlayerShielded", directEvents);
        Assert.Contains("TTriggerOnCardPerformedCharge", directEvents);

        // B. TTriggerOnPlayerLeveledUp + OutOfCombatController.OnPlayerLeveledUp
        var levelSkillTpl = MakeSyntheticTemplate(
            "v11_level_skill",
            cardType: "Skill",
            abilities: new()
            {
                ["ab_lvl"] = new()
                {
                    Id = "ab_lvl",
                    Trigger = new RuleNode { Type = "TTriggerOnPlayerLeveledUp", Level = 5, ComparisonOperator = "LessThanOrEqual" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        RawValueNode = JsonFixed(10),
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" }
                    }
                }
            });
        var lvlPlayer = new PlayerState("lp", "LP", HeroNames.Vanessa, 250, 20, 10, 6) { Level = 3 };
        lvlPlayer.Skills.Add(new CardInstance(levelSkillTpl));
        var lvlBatch = OutOfCombatController.OnPlayerLeveledUp(lvlPlayer, synthRepo, new Random(1));
        Assert.Equal(20, lvlPlayer.Gold);
        Assert.Equal(10, lvlBatch.PlayerAttributeDeltas.GetValueOrDefault("Gold", 0));

        // 超出 Level <= 5 的条件时不应触发
        lvlPlayer.Level = 6;
        OutOfCombatController.OnPlayerLeveledUp(lvlPlayer, synthRepo, new Random(1));
        Assert.Equal(20, lvlPlayer.Gold);

        // C. TTriggerOnRunCompleted（非 RunCompleted 事件及不匹配 Outcome 时必须返回 false）
        var runCompTrigger = new RuleNode { Type = "TTriggerOnRunCompleted", CombatOutcome = "Victory" };
        var dummyCard = new CardInstance(chargeReceiverTpl);
        var oocCtx = new RuleExecutionContext(synthRepo, RuleScope.OutOfCombat, lvlPlayer, null, new Random(1));
        Assert.False(AbilityExecutor.MatchesTrigger(runCompTrigger, "TTriggerOnFightEnded", dummyCard, lvlPlayer, null, null, lvlPlayer, null, 0, 0, "PVP", "Victory", oocCtx));
        Assert.False(AbilityExecutor.MatchesTrigger(runCompTrigger, "TTriggerOnRunCompleted", dummyCard, lvlPlayer, null, null, lvlPlayer, null, 0, 0, "RunCompleted", "Defeat", oocCtx));
        Assert.True(AbilityExecutor.MatchesTrigger(runCompTrigger, "TTriggerOnRunCompleted", dummyCard, lvlPlayer, null, null, lvlPlayer, null, 0, 0, "RunCompleted", "Victory", oocCtx));
    }

    // =========================================================================
    // 2. 零冷却被动件全量验证（HANDOFF_FINDINGS #21）
    // =========================================================================

    [Fact]
    public void ZeroCooldownPassiveItems_AllAlwaysItemsWithoutCooldownMax_ShouldRunPassivesAurasAndQuestsInCombat()
    {
        var repo = Repo.Value;
        var passiveAlwaysItems = repo.AlwaysSpawnableItems
            .Where(t =>
            {
                var inst = new CardInstance(t);
                return inst.EffectiveCooldownMs <= 0 && (inst.ActiveAbilities.Count > 0 || inst.ActiveAuras.Count > 0 || (t.Quests?.Count ?? 0) > 0);
            })
            .ToList();

        Assert.True(passiveAlwaysItems.Count >= 100, $"预期存在大量无冷却 Always 被动件，实际={passiveAlwaysItems.Count}");

        // 附带一个带全标签的主动伴侣件及敌方武器，保证被动触发器与光环均有合法目标与触发时机
        var partnerTpl = MakeSyntheticTemplate(
            "v11_passive_partner",
            tags: AllAnchorTags,
            hiddenTags: ["Damage", "Shield", "Burn", "Poison", "Heal", "Regen", "Haste", "Slow", "Freeze"],
            baseAttrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 20, ["ShieldApplyAmount"] = 10, ["BurnApplyAmount"] = 5, ["AmmoMax"] = 3 },
            abilities: new()
            {
                ["ab_p"] = new()
                {
                    Id = "ab_p",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } },
                            new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" } }
                        ]
                    }
                }
            });
        var passiveTestRepo = CreateRepoWithExtraCards(partnerTpl);

        int fightStartedPassiveCount = 0;
        foreach (var tpl in passiveAlwaysItems)
        {
            var p1 = new PlayerState("p1", "P1", tpl.Heroes.FirstOrDefault() ?? HeroNames.Vanessa, 1500, 20, 10, 10);
            var p2 = new PlayerState("p2", "P2", HeroNames.Pygmalien, 1500, 20, 10, 10);

            var passive = new CardInstance(tpl);
            Assert.False(passive.HasActiveCooldown);
            p1.Hand.TryAddAuto(passive);

            p1.Hand.TryAddAuto(new CardInstance(partnerTpl));
            p2.Hand.TryAddAuto(new CardInstance(MakeSyntheticTemplate(
                "v11_passive_opp",
                tags: ["Weapon"],
                baseAttrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 20 },
                abilities: new()
                {
                    ["ab_o"] = new()
                    {
                        Id = "ab_o",
                        Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                        Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                    }
                })));

            if (passive.ActiveAbilities.Any(a => a.Trigger?.Type == "TTriggerOnFightStarted"
                    || (a.Trigger?.Triggers?.Any(sub => sub.Type == "TTriggerOnFightStarted") ?? false)))
                fightStartedPassiveCount++;

            var res = CombatSimulator.Simulate(passiveTestRepo, p1, p2, seed: 42, combatType: "PVP", recordTimeline: false, recordEvents: false, syncPermanentChanges: true);
            Assert.NotNull(res);
            Assert.Contains(passive, p1.Hand.Cards);
        }

        Assert.True(fightStartedPassiveCount >= 18, $"应覆盖带 TTriggerOnFightStarted 的无冷却被动件，实际={fightStartedPassiveCount}");
    }

    // =========================================================================
    // 3. 随机附魔权重、白名单、PreventOverride、TargetCount 与战后清理审计
    // =========================================================================

    [Fact]
    public void EnchantRandomAndPostCombatCleanup_ShouldHonorWeightsEligibilityPreventOverrideTargetCountAndDurations()
    {
        var repo = Repo.Value;
        // 真实 DB 卡 Hallucination / Farai / Mystery Flavor 检查
        var hallucination = repo.CardsById.Values.FirstOrDefault(c => c.InternalName == "Hallucination");
        Assert.NotNull(hallucination);

        // 1. 权重严格遵循与 PreventOverride / Template.Enchantments 资格校验
        var targetItemTpl = MakeSyntheticTemplate(
            "v11_ench_target",
            enchantments: new()
            {
                ["Fiery"] = new EnchantmentData { HiddenTags = ["Burn"] },
                ["Icy"] = new EnchantmentData { HiddenTags = ["Freeze"] }
            });
        var onlyIcyWeightedAction = new RuleNode
        {
            Type = "TActionCardEnchantRandom",
            PreventOverride = true,
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" },
            RawEnchantmentsElement = JsonDocument.Parse("""
                [
                  {"Enchantment": "Heavy", "Weight": 500.0},
                  {"Enchantment": "Icy", "Weight": 100.0},
                  {"Enchantment": "Fiery", "Weight": 0.0}
                ]
                """).RootElement.Clone(),
            Duration = new RuleNode { Type = "TDeterminantDuration", DurationType = "UntilEndOfCombat" }
        };

        var player = new PlayerState("ep", "EP", HeroNames.Vanessa, 500, 20, 10, 10);
        var card1 = new CardInstance(targetItemTpl);
        var card2 = new CardInstance(targetItemTpl, enchantment: "Fiery"); // 已附魔，PreventOverride=true 不得覆盖
        player.Hand.TryAddAuto(card1);
        player.Hand.TryAddAuto(card2);

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, player, null, new Random(1)) { SelfCard = card1 };
        Assert.True(AbilityExecutor.ExecuteAction(onlyIcyWeightedAction, card1, ctx));
        // Heavy 不在 targetItemTpl.Enchantments 中，Fiery 权重为 0，仅 Icy 合法且权重 > 0
        Assert.Equal("Icy", card1.Enchantment);
        Assert.Null(card1.PermanentEnchantment);
        Assert.Equal("Fiery", card2.Enchantment); // PreventOverride 生效

        // TargetCount 限制应仅附魔前 N 张
        var c3 = new CardInstance(targetItemTpl);
        var c4 = new CardInstance(targetItemTpl);
        var pCount = new PlayerState("ec", "EC", HeroNames.Vanessa, 500, 20, 10, 10);
        pCount.Hand.TryAddAuto(c3);
        pCount.Hand.TryAddAuto(c4);
        var oneTargetAction = new RuleNode
        {
            Type = "TActionCardEnchantRandom",
            TargetCount = new RuleNode { Type = "TFixedValue", RawValueNode = JsonDocument.Parse("1.0").RootElement.Clone() },
            Target = new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfHand" }
        };
        var ctxCount = new RuleExecutionContext(repo, RuleScope.OutOfCombat, pCount, null, new Random(2));
        Assert.True(AbilityExecutor.ExecuteAction(oneTargetAction, c3, ctxCount));
        Assert.Equal(1, pCount.Hand.Cards.Count(c => !string.IsNullOrEmpty(c.Enchantment)));

        // 2. 战后 ResetForCombat 清理 Freeze/Slow/Haste/Flying/Heated/Chilled/临时标签/临时附魔并补满弹药
        var musketTpl = repo.GetCard("01841d2e-9409-4bb6-a886-b89f8ebfb0e3") ?? repo.AllItems.First(i => (i.Tiers?.Values.Any(td => td.Attributes.ContainsKey("AmmoMax")) ?? false));
        var dirty = new CardInstance(musketTpl);
        dirty.ApplyEnchantment("Icy", preventOverride: false, DurationTarget.Combat);
        dirty.CombatTags.Add("TemporarySweepTag");
        dirty.CombatAdd["DamageAmount"] = 99;
        dirty.FreezeMs = 1500;
        dirty.SlowMs = 1200;
        dirty.HasteMs = 800;
        dirty.IsFlying = true;
        dirty.Heated = true;
        dirty.Chilled = true;
        dirty.CurrentAmmo = 0;

        dirty.ResetForCombat(repo);
        Assert.Null(dirty.Enchantment);
        Assert.Equal(0, dirty.FreezeMs);
        Assert.Equal(0, dirty.SlowMs);
        Assert.Equal(0, dirty.HasteMs);
        Assert.False(dirty.IsFlying);
        Assert.False(dirty.Heated);
        Assert.False(dirty.Chilled);
        Assert.Empty(dirty.CombatTags);
        Assert.Empty(dirty.CombatAdd);
        Assert.Equal(dirty.EffectiveAmmoMax, dirty.CurrentAmmo);

        // 3. UntilEndOfDay 临时附魔在当日进战斗克隆中保留，过天后清理
        var dayCard = new CardInstance(targetItemTpl);
        dayCard.ApplyEnchantment("Fiery", preventOverride: false, DurationTarget.Day);
        var combatClone = dayCard.CloneForCombat();
        Assert.Equal("Fiery", combatClone.Enchantment);
        dayCard.ClearEndOfDayModifiers();
        Assert.Null(dayCard.Enchantment);
    }

    // =========================================================================
    // 4. BoardGrid 挤位零静默位移追踪 + SpawnQueryEngine ExclusionSet
    // =========================================================================

    [Fact]
    public void BoardGridDisplacementAndExclusionSet_ShouldReportEveryMovedCardAndFilterExcludedPools()
    {
        var grid = new BoardGrid(1023);
        var a = new CardInstance(MakeSyntheticTemplate("bg_a", "Small"));
        var b = new CardInstance(MakeSyntheticTemplate("bg_b", "Small"));
        var c = new CardInstance(MakeSyntheticTemplate("bg_c", "Medium"));
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(a, 0, out var d0));
        Assert.Empty(d0);
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(b, 1, out _));
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(c, 2, out _));

        // 将新卡 X 插入槽 1，迫使 b(1->2) 与 c(2->3..4) 全部向右位移：out 列表必须完整捕获 b 与 c
        var x = new CardInstance(MakeSyntheticTemplate("bg_x", "Small"));
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(x, 1, out var displaced));
        Assert.Equal(0, a.StartSlot);
        Assert.Equal(1, x.StartSlot);
        Assert.Equal(2, b.StartSlot);
        Assert.Equal(3, c.StartSlot);
        Assert.Contains(b, displaced);
        Assert.Contains(c, displaced);
        Assert.DoesNotContain(a, displaced);

        // 将 c(3..4) 拖拽到 0（占用 a(0) 与 x(1) 区间）：检查所有位移卡全部在 displaced2 内
        var snapBefore = grid.Cards.ToDictionary(card => card, card => card.StartSlot);
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(c, 0, out var displaced2));
        foreach (var (card, oldSlot) in snapBefore)
        {
            if (card.StartSlot != oldSlot)
                Assert.Contains(card, displaced2);
            else
                Assert.DoesNotContain(card, displaced2);
        }
        Assert.True(grid.ValidateInvariants(out var err), err);

        // SpawnQueryEngine 随机池生成必须严格尊重 GameMode.ExclusionSet
        var repo = Repo.Value;
        Assert.NotEmpty(repo.GameMode.ExclusionSet);
        var player = new PlayerState("ex", "EX", HeroNames.Vanessa, 250, 20, 10, 10) { Day = 5, Hour = 1 };
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(99));
        var randomAllPoolQuery = new RuleNode
        {
            Type = "TSpawnContextQuery",
            Limit = new RuleNode { Type = "TFixedValue", RawValueNode = JsonDocument.Parse("10.0").RootElement.Clone() },
            Behaviors =
            [
                new RuleNode { Type = "TSpawnBehaviorIgnoreHero", IgnoreHero = true },
                new RuleNode { Type = "TSpawnBehaviorIgnoreTierTable", IgnoreTierTable = true }
            ],
            Groups =
            [
                new RuleNode
                {
                    Type = "TSpawnGroup",
                    Filters =
                    [
                        new RuleNode
                        {
                            Type = "TSpawnFilterQuery",
                            RawConstraintsElement = JsonDocument.Parse("""{"$type":"ConstraintCardType","Types":["EventEncounter","PedestalEncounter","Item","Skill"]}""").RootElement.Clone()
                        }
                    ]
                }
            ]
        };
        for (int i = 0; i < 20; i++)
        {
            var spawned = SpawnQueryEngine.SpawnCards(randomAllPoolQuery, ctx);
            foreach (var s in spawned)
                Assert.DoesNotContain(s.Template.Id, repo.GameMode.ExclusionSet, StringComparer.OrdinalIgnoreCase);
        }
    }

    // =========================================================================
    // 5. 未知 $type 遥测自检 + 全量 3,442 卡 + 12 附魔 + 1,201 遭遇 + 30 级×8 英雄扫描
    // =========================================================================

    [Fact]
    public void UnknownTypeTelemetry_ShouldDetectUnrecognizedActionAuraAndTriggerTypes()
    {
        var repo = Repo.Value;
        var player = new PlayerState("ut", "UT", HeroNames.Vanessa, 250, 20, 10, 10);
        var unknownCard = new CardInstance(MakeSyntheticTemplate(
            "v11_unknown_probe",
            abilities: new()
            {
                ["ab_u"] = new()
                {
                    Id = "ab_u",
                    Trigger = new RuleNode { Type = "TTriggerSyntheticUnknown" },
                    Action = new RuleNode { Type = "TActionSyntheticUnknown" }
                }
            },
            auras: new()
            {
                ["au_u"] = new()
                {
                    Id = "au_u",
                    Action = new RuleNode { Type = "TAuraActionSyntheticUnknown" }
                }
            }));
        player.Hand.TryAddAuto(unknownCard);

        var hits = new List<(string Category, string Type)>();
        using (RuleExecutionContext.BeginUnknownRuleTypeScope((cat, t) => hits.Add((cat, t))))
        {
            AuraEngine.RecalculateAuras(player, null, RuleScope.OutOfCombat, 1, 1, repo);
            OutOfCombatController.OnItemPurchased(player, unknownCard, repo, new Random(1));
            var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, player, null, new Random(1)) { SelfCard = unknownCard };
            Assert.False(AbilityExecutor.ExecuteAction(unknownCard.ActiveAbilities[0].Action, unknownCard, ctx));
            Assert.False(AbilityExecutor.MatchesTrigger(unknownCard.ActiveAbilities[0].Trigger, "TTriggerSyntheticUnknown", unknownCard, player, unknownCard, unknownCard, player, null, 0, 0, "PVP", "", ctx));
        }

        Assert.Contains(("TAuraAction", "TAuraActionSyntheticUnknown"), hits);
        Assert.Contains(("TTrigger", "TTriggerSyntheticUnknown"), hits);
        Assert.Contains(("TAction", "TActionSyntheticUnknown"), hits);
    }

    [Fact]
    public void FullDatabaseSweep_All3442Cards_12Enchantments_EncountersAndLevelUps_ZeroExceptionsZeroUnknownTypesCleanPostCombat()
    {
        var repo = Repo.Value;
        Assert.True(repo.CardsById.Count >= 3442, $"应至少加载 3,442 张 DB 卡牌，实际={repo.CardsById.Count}");

        var unknownHits = new ConcurrentBag<string>();
        var boardMountableTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Item", "Skill", "SocketEffect", "MusicNoteSocketEffect", "PlayerEffect"
        };

        var mountableCards = repo.CardsById.Values
            .Where(c => boardMountableTypes.Contains(c.Type))
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 构造精简但标签全覆盖的 10 格棋盘模板（含全标签主动武器/护盾/灼烧/中毒/弹药/属性/朋友/科技/药水/食物/圣物/战利品）
        var richLeftPartnerTpl = MakeSyntheticTemplate(
            "v11_scan_left_partner",
            size: "Small",
            tags: AllAnchorTags,
            hiddenTags: ["Damage", "Shield", "Burn", "Poison", "Heal", "Regen", "Haste", "Slow", "Freeze", "Charge", "Crit", "Lifesteal", "Ammo", "HeatedReference", "ChilledReference"],
            baseAttrs: new()
            {
                ["CooldownMax"] = 1500,
                ["DamageAmount"] = 30,
                ["ShieldApplyAmount"] = 25,
                ["BurnApplyAmount"] = 4,
                ["PoisonApplyAmount"] = 3,
                ["HealAmount"] = 15,
                ["RegenApplyAmount"] = 2,
                ["AmmoMax"] = 3,
                ["CritChance"] = 25
            },
            abilities: new()
            {
                ["ab_left"] = new()
                {
                    Id = "ab_left",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } },
                            new RuleNode { Type = "TActionPlayerShieldApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Self" } },
                            new RuleNode { Type = "TActionPlayerBurnApply", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                        ]
                    }
                }
            });

        var richRightPartnerTpl = MakeSyntheticTemplate(
            "v11_scan_right_partner",
            size: "Small",
            tags: ["Weapon", "Friend", "Property", "Tech", "Relic", "Food", "Potion", "Loot"],
            hiddenTags: ["Damage", "Shield", "Heal", "Haste", "Slow", "Freeze"],
            baseAttrs: new() { ["CooldownMax"] = 2000, ["DamageAmount"] = 20, ["AmmoMax"] = 2 },
            abilities: new()
            {
                ["ab_right"] = new()
                {
                    Id = "ab_right",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                }
            });

        var oppFastStrikerTpl = MakeSyntheticTemplate(
            "v11_scan_opp_striker",
            size: "Small",
            tags: ["Weapon"],
            baseAttrs: new() { ["CooldownMax"] = 1200, ["DamageAmount"] = 40 },
            abilities: new()
            {
                ["ab_opp"] = new()
                {
                    Id = "ab_opp",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                },
                // 确保微型战斗在约 5,000ms 稳定结束，不因高防高回复卡牌拖满 120,000ms
                ["ab_cap5s"] = new()
                {
                    Id = "ab_cap5s",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionPlayerPoisonApply",
                        RawValueNode = JsonFixed(50),
                        Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" }
                    }
                }
            });

        var oppCap5sTpl = MakeSyntheticTemplate(
            "v11_scan_opp_cap5s",
            size: "Small",
            tags: ["Weapon"],
            baseAttrs: new() { ["CooldownMax"] = 5000, ["DamageAmount"] = 500_000 },
            abilities: new()
            {
                ["ab_cap"] = new()
                {
                    Id = "ab_cap",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = "TActionAnd",
                        Actions =
                        [
                            new RuleNode { Type = "TActionPlayerPoisonApply", RawValueNode = JsonFixed(500_000), Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } },
                            new RuleNode { Type = "TActionPlayerDamage", Target = new RuleNode { Type = "TTargetPlayerRelative", TargetMode = "Opponent" } }
                        ]
                    }
                }
            });

        var scanRepo = CreateRepoWithExtraCards(richLeftPartnerTpl, richRightPartnerTpl, oppFastStrikerTpl, oppCap5sTpl);

        int scannedBaseCount = 0;
        int scannedEnchantedCount = 0;

        using (RuleExecutionContext.BeginUnknownRuleTypeScope((cat, typeName) => unknownHits.Add($"{cat}:{typeName}")))
        {
            // 1. 扫描全部可上场模板（Item / Skill / SocketEffect / MusicNoteSocketEffect / PlayerEffect）
            //    覆盖 StartingTier、最高 Tier 与全 12 种附魔
            foreach (var tpl in mountableCards)
            {
                var startTier = tpl.DefaultTier;
                var maxTier = GetMaxSupportedTier(tpl);
                CardTier[] tiersToTest = maxTier != startTier ? [startTier, maxTier] : [startTier];

                foreach (var tier in tiersToTest)
                {
                    RunSingleCardLifecycleAndMicroCombat(scanRepo, tpl, tier, enchantment: null, richLeftPartnerTpl, richRightPartnerTpl, oppFastStrikerTpl, oppCap5sTpl, runFullOocLifecycle: true);
                    scannedBaseCount++;
                }

                if (tpl.Type == "Item")
                {
                    var enchantList = AllTwelveEnchantments.Where(e => tpl.Enchantments == null || tpl.Enchantments.Count == 0 || tpl.Enchantments.ContainsKey(e));
                    foreach (var ench in enchantList)
                    {
                        RunSingleCardLifecycleAndMicroCombat(scanRepo, tpl, maxTier, ench, richLeftPartnerTpl, richRightPartnerTpl, oppFastStrikerTpl, oppCap5sTpl, runFullOocLifecycle: false);
                        scannedEnchantedCount++;
                    }
                }
                else if (tpl.Enchantments is { Count: > 0 })
                {
                    foreach (var ench in tpl.Enchantments.Keys)
                    {
                        RunSingleCardLifecycleAndMicroCombat(scanRepo, tpl, maxTier, ench, richLeftPartnerTpl, richRightPartnerTpl, oppFastStrikerTpl, oppCap5sTpl, runFullOocLifecycle: false);
                        scannedEnchantedCount++;
                    }
                }
            }

            // 2. 扫描全部 EncounterStep (599) + EventEncounter (561) + PedestalEncounter (41) + CombatEncounter (184) + Monsters (179)
            var encounterTemplates = repo.CardsById.Values
                .Where(c => c.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter" or "CombatEncounter")
                .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int encounterStepCount = 0;
            int eventEncounterCount = 0;
            int pedestalEncounterCount = 0;
            int combatEncounterCount = 0;

            foreach (var encTpl in encounterTemplates)
            {
                if (encTpl.Type == "EncounterStep") encounterStepCount++;
                else if (encTpl.Type == "EventEncounter") eventEncounterCount++;
                else if (encTpl.Type == "PedestalEncounter") pedestalEncounterCount++;
                else if (encTpl.Type == "CombatEncounter") combatEncounterCount++;

                string hero = encTpl.Heroes.FirstOrDefault(h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)) ?? HeroNames.Vanessa;
                var encCard = new CardInstance(encTpl);

                // 在 Day 1 / Day 5 / Day 10 上下文中运行 SpawnContext 与生命周期钩子
                int[] probeDays = [1, 5, 10];
                foreach (int day in probeDays)
                {
                    var player = new PlayerState("enc_p", "EncP", hero, 1000, 20, 100, 10)
                    {
                        Day = day,
                        Hour = 3,
                        Level = Math.Max(1, day)
                    };
                    player.Hand.TryAddAuto(new CardInstance(richLeftPartnerTpl));
                    player.Hand.TryAddAuto(new CardInstance(richRightPartnerTpl));
                    player.Stash.TryAddAuto(new CardInstance(richRightPartnerTpl));
                    // 解锁排除集以确保特殊事件卡（Thieves Guild / Burning Library / Partner Portal）亦被扫描测试
                    player.InitializeExclusionSet(repo.GameMode);
                    player.ExclusionSet.Remove(encTpl.Id);

                    if (encTpl.SelectionContext?.SpawnContext != null)
                    {
                        var spCtx = new RuleExecutionContext(scanRepo, RuleScope.OutOfCombat, player, null, new Random(day * 31))
                        {
                            CurrentDay = day,
                            CurrentHour = 3,
                            CurrentEncounterId = encTpl.Id,
                            SelfCard = encCard
                        };
                        var dealt = SpawnQueryEngine.SpawnCards(encTpl.SelectionContext.SpawnContext, spCtx);
                        Assert.NotNull(dealt);
                        foreach (var d in dealt)
                        {
                            Assert.NotNull(d.Template);
                            Assert.True(Enum.IsDefined(typeof(CardTier), d.Tier));
                        }
                    }

                    foreach (var ab in encTpl.Abilities.Values)
                    {
                        if (ab.Action?.SpawnContext == null) continue;
                        var abSpCtx = new RuleExecutionContext(scanRepo, RuleScope.OutOfCombat, player, null, new Random(day * 37))
                        {
                            CurrentDay = day,
                            CurrentHour = 3,
                            CurrentEncounterId = encTpl.Id,
                            SelfCard = encCard
                        };
                        var abSpawned = SpawnQueryEngine.SpawnCards(ab.Action.SpawnContext, abSpCtx);
                        Assert.NotNull(abSpawned);
                        foreach (var s in abSpawned)
                        {
                            Assert.NotNull(s.Template);
                            Assert.True(Enum.IsDefined(typeof(CardTier), s.Tier));
                        }
                    }
                }

                // 执行遭遇自身触发能力与光环（Selected / Entered / CardSelected / Exited）
                var execPlayer = new PlayerState("enc_exec", "EncExec", hero, 1000, 20, 100, 10) { Day = 5, Hour = 3, Level = 5 };
                execPlayer.Hand.TryAddAuto(new CardInstance(richLeftPartnerTpl));
                execPlayer.Hand.TryAddAuto(new CardInstance(richRightPartnerTpl));
                OutOfCombatController.OnEncounterSelected(execPlayer, encCard, scanRepo, new Random(1), encTpl.Id, encCard);
                OutOfCombatController.OnEncounterEntered(execPlayer, encCard, scanRepo, new Random(2), encTpl.Id, encCard);
                OutOfCombatController.OnCardSelected(execPlayer, encCard, scanRepo, new Random(3), [encCard], encTpl.Id, encCard);
                OutOfCombatController.OnEncounterExited(execPlayer, encCard, scanRepo, new Random(4), encTpl.Id, encCard);
            }

            Assert.True(encounterStepCount >= 599, $"EncounterStep 数量应 >= 599，实际={encounterStepCount}");
            Assert.True(eventEncounterCount >= 561, $"EventEncounter 数量应 >= 561，实际={eventEncounterCount}");
            Assert.True(pedestalEncounterCount >= 41, $"PedestalEncounter 数量应 >= 41，实际={pedestalEncounterCount}");
            Assert.True(combatEncounterCount >= 184, $"CombatEncounter 数量应 >= 184，实际={combatEncounterCount}");
            Assert.Equal(repo.CardsById.Count, mountableCards.Count + encounterTemplates.Count);

            // 扫描全部 179 只 Monsters 构建战斗状态与 5,000ms 对战
            Assert.True(repo.MonstersById.Count >= 179, $"Monsters 数量应 >= 179，实际={repo.MonstersById.Count}");
            foreach (var monster in repo.MonstersById.Values)
            {
                var mp = CombatSimulator.BuildMonsterPlayerState(scanRepo, monster);
                var p1 = new PlayerState("vs_m", "Player", HeroNames.Vanessa, 2000, 20, 10, 10) { Day = 5, Hour = 6 };
                p1.Hand.TryAddAuto(new CardInstance(richLeftPartnerTpl));
                p1.Hand.TryAddAuto(new CardInstance(oppCap5sTpl));
                var mRes = CombatSimulator.Simulate(scanRepo, p1, mp, seed: 11, combatType: "PVE", recordTimeline: false, recordEvents: false);
                Assert.NotNull(mRes);
            }

            // 3. 扫描全部 30 个 level_ups × 8 英雄
            Assert.Equal(30, repo.LevelUps.Count);
            foreach (var (level, luTpl) in repo.LevelUps.OrderBy(kv => kv.Key))
            {
                foreach (var hero in AllEightHeroes)
                {
                    var player = new PlayerState($"lu_{level}_{hero}", hero, hero, 500 + level * 100, 20, 50, 10)
                    {
                        Day = Math.Clamp(level, 1, 15),
                        Hour = 3,
                        Level = level
                    };
                    player.Hand.TryAddAuto(new CardInstance(richLeftPartnerTpl));
                    player.Hand.TryAddAuto(new CardInstance(richRightPartnerTpl));

                    var ctx = new RuleExecutionContext(scanRepo, RuleScope.OutOfCombat, player, null, new Random(level * 13 + hero.Length))
                    {
                        CurrentDay = player.Day,
                        CurrentHour = player.Hour
                    };
                    if (luTpl.Rewards != null)
                    {
                        var spawned = SpawnQueryEngine.SpawnCards(luTpl.Rewards, ctx);
                        Assert.True(spawned.Count > 0, $"LevelUp Lv{level} 英雄 {hero} 未生成任何有效候选卡牌");
                    }
                    if (luTpl.TutorialRewards != null)
                    {
                        var tutSpawned = SpawnQueryEngine.SpawnCards(luTpl.TutorialRewards, ctx);
                        Assert.NotNull(tutSpawned);
                    }
                }
            }
        }

        Assert.True(mountableCards.Count >= 2057, $"可上场卡牌数应 >= 2057，实际={mountableCards.Count}");
        Assert.True(scannedBaseCount >= 2057);
        Assert.True(scannedEnchantedCount >= 12000);
        Assert.Empty(unknownHits);
    }

    private static CardTier GetMaxSupportedTier(CardTemplate tpl)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0)
            return tpl.DefaultTier;
        var max = tpl.DefaultTier;
        foreach (var key in tpl.Tiers.Keys)
        {
            var parsed = TierHelper.ParseTier(key);
            if (parsed > max) max = parsed;
        }
        return max;
    }

    private static void RunSingleCardLifecycleAndMicroCombat(
        GameDataRepository repo,
        CardTemplate tpl,
        CardTier tier,
        string? enchantment,
        CardTemplate leftPartnerTpl,
        CardTemplate rightPartnerTpl,
        CardTemplate oppFastTpl,
        CardTemplate oppCap5sTpl,
        bool runFullOocLifecycle)
    {
        string hero = tpl.Heroes.FirstOrDefault(h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)) ?? HeroNames.Vanessa;
        var p1 = new PlayerState("scan_p1", "ScanP1", hero, 2500, 20, 100, 10) { Day = 5, Hour = 3, Level = 5 };
        var p2 = new PlayerState("scan_p2", "ScanP2", HeroNames.Pygmalien, 2500, 20, 100, 10) { Day = 5, Hour = 3, Level = 5 };

        var leftPartner = new CardInstance(leftPartnerTpl);
        var rightPartner = new CardInstance(rightPartnerTpl);
        var targetCard = new CardInstance(tpl, tier, enchantment);

        p1.Hand.TryPlaceOrShift(leftPartner, 0);
        if (tpl.Type == "Item")
        {
            if (!p1.Hand.TryPlaceWithDisplaceOrSwap(targetCard, 1))
                p1.Hand.TryAddAuto(targetCard);
            if (!p1.Hand.TryAddAuto(rightPartner))
                p1.Stash.TryAddAuto(rightPartner);
        }
        else if (tpl.Type == "Skill")
        {
            p1.Skills.Add(targetCard);
            p1.Hand.TryPlaceOrShift(rightPartner, 1);
        }
        else
        {
            targetCard.StartSlot = 0;
            p1.SocketEffects.Add(targetCard);
            p1.Hand.TryPlaceOrShift(rightPartner, 1);
        }

        // 在 Stash 中也保留一张多标签卡，覆盖 Stash 目标查询
        p1.Stash.TryAddAuto(new CardInstance(rightPartnerTpl));

        p2.Hand.TryPlaceOrShift(new CardInstance(oppFastTpl), 0);
        p2.Hand.TryPlaceOrShift(new CardInstance(oppCap5sTpl, CardTier.Bronze, "Radiant"), 1);
        p2.Hand.TryPlaceOrShift(new CardInstance(leftPartnerTpl), 2);

        // 1. 局外光环刷新与全套生命周期钩子
        AuraEngine.RecalculateAuras(p1, null, RuleScope.OutOfCombat, p1.Day, p1.Hour, repo);
        if (runFullOocLifecycle)
        {
            var rng = new Random(17);
            OutOfCombatController.OnItemPurchased(p1, targetCard, repo, rng);
            OutOfCombatController.OnDayStarted(p1, repo, rng);
            OutOfCombatController.OnHourStarted(p1, repo, rng);
            OutOfCombatController.OnCardUpgraded(p1, targetCard, repo, rng);
            OutOfCombatController.OnCardEnchanted(p1, targetCard, repo, rng);
            OutOfCombatController.OnPlayerLeveledUp(p1, repo, rng);
            // 卖卡测试在临时副本上执行，防止把 targetCard 从 p1 移走导致后续微型战斗缺少该卡
            var sellClone = p1.CloneForCombat();
            var sellTarget = sellClone.EnumerateAllOwnedCards().FirstOrDefault(c => c.InstanceId == targetCard.InstanceId);
            if (sellTarget != null)
                OutOfCombatController.OnCardSoldOrConsumed(sellClone, sellTarget, repo, new Random(19));
        }

        // 2. 5,000ms 微型战斗模拟（开启 syncPermanentChanges 以同步验证战斗永久回写不带出战斗临时残留）
        var simRes = CombatSimulator.Simulate(repo, p1, p2, seed: 23, combatType: "PVP", recordTimeline: false, recordEvents: false, syncPermanentChanges: true);
        Assert.NotNull(simRes);

        // 3. 战后状态与 ResetForCombat 清理验证：保证 p1 全部持有卡及目标卡 0 状态残留
        string cardLabel = $"{tpl.InternalName}({tpl.Id}, {tier}, {enchantment ?? "None"})";
        foreach (var owned in p1.EnumerateAllOwnedCards())
        {
            Assert.True(owned.FreezeMs == 0, $"[{cardLabel}] {owned.Template.InternalName} FreezeMs={owned.FreezeMs}");
            Assert.True(owned.SlowMs == 0, $"[{cardLabel}] {owned.Template.InternalName} SlowMs={owned.SlowMs}");
            Assert.True(owned.HasteMs == 0, $"[{cardLabel}] {owned.Template.InternalName} HasteMs={owned.HasteMs}");
            Assert.False(owned.IsFlying, $"[{cardLabel}] {owned.Template.InternalName} IsFlying");
            Assert.False(owned.Heated, $"[{cardLabel}] {owned.Template.InternalName} Heated");
            Assert.False(owned.Chilled, $"[{cardLabel}] {owned.Template.InternalName} Chilled");
            Assert.True(owned.CombatAdd.Count == 0, $"[{cardLabel}] {owned.Template.InternalName} CombatAdd.Count={owned.CombatAdd.Count}");
            Assert.True(owned.CombatMult.Count == 0, $"[{cardLabel}] {owned.Template.InternalName} CombatMult.Count={owned.CombatMult.Count}");
            Assert.True(owned.CombatTags.Count == 0, $"[{cardLabel}] {owned.Template.InternalName} CombatTags.Count={owned.CombatTags.Count}");
            Assert.Equal(owned.HourEnchantment ?? owned.DayEnchantment ?? owned.PermanentEnchantment, owned.Enchantment);
            Assert.True(owned.EffectiveAmmoMax == owned.CurrentAmmo,
                $"[{cardLabel}] {owned.Template.InternalName} EffectiveAmmoMax={owned.EffectiveAmmoMax} != CurrentAmmo={owned.CurrentAmmo}");
        }

        targetCard.FreezeMs = Math.Max(targetCard.FreezeMs, 100);
        targetCard.SlowMs = Math.Max(targetCard.SlowMs, 100);
        targetCard.HasteMs = Math.Max(targetCard.HasteMs, 100);
        targetCard.IsFlying = true;
        targetCard.Heated = true;
        targetCard.Chilled = true;
        targetCard.CombatAdd["DamageAmount"] = 10;
        targetCard.CombatTags.Add("CombatScanDirty");
        if (targetCard.Template.Type == "Item")
            targetCard.ApplyEnchantment("Icy", preventOverride: false, DurationTarget.Combat);
        targetCard.ResetForCombat(repo);

        Assert.Equal(0, targetCard.FreezeMs);
        Assert.Equal(0, targetCard.SlowMs);
        Assert.Equal(0, targetCard.HasteMs);
        Assert.False(targetCard.IsFlying);
        Assert.False(targetCard.Heated);
        Assert.False(targetCard.Chilled);
        Assert.False(targetCard.IsDestroyed);
        Assert.False(targetCard.IsDisabled);
        Assert.Empty(targetCard.CombatAdd);
        Assert.Empty(targetCard.CombatMult);
        Assert.Empty(targetCard.CombatTags);
        Assert.Equal(targetCard.HourEnchantment ?? targetCard.DayEnchantment ?? targetCard.PermanentEnchantment, targetCard.Enchantment);
        Assert.Equal(targetCard.PermanentTier, targetCard.Tier);
        Assert.Equal(targetCard.EffectiveAmmoMax, targetCard.CurrentAmmo);
    }
}
