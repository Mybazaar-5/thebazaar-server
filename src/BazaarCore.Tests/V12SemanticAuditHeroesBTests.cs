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
/// V12 Shard B（Jules / Stelle / Karnok / Hero8 / Common）全量语义审计与引擎 Silent No-Op 修复回归测试。
/// 1. 专项回归：验证 TActionCardDisable 破坏链、TActionCardRepair 精准修复、TActionPlayerReviveHeal 治疗事件、
///    TActionCardModifyAttribute 实时解控/Heated/Chilled、TActionCardAddTagsRandom 去重、TActionCardTiny、
///    TActionCardTransform* 多产物/Disabled 目标/Transform.Abilities、Quest CompletionEffects/Reward.Tags/Quest_1..7、
///    以及 TActionGameSpawnCards.Attributes(Tiny)。
/// 2. 全量语义扫描：遍历 Shard B 全部 Always 物品与技能，逐 Ability / Aura / Quest 构造满足前提与目标的合成面板，
///    触发对应机制并断言非零语义状态增量，输出审计报告至 e:\thebazaar\logs\v12_sem_audit_b.json。
/// </summary>
public sealed class V12SemanticAuditHeroesBTests
{
    private const string AuthoritativeDataDir = @"e:\thebazaar\dist\data";
    private const string AuditLogPath = @"e:\thebazaar\logs\v12_sem_audit_b.json";

    private static readonly string[] ShardBHeroes =
        ["Jules", "Stelle", "Karnok", "Hero8", "Common"];

    private static readonly string[] CommonAuditTags =
    [
        "Weapon", "Tool", "Food", "Friend", "Vehicle", "Drone", "Relic", "Apparel",
        "Potion", "Aquatic", "Core", "Property", "Toy", "Tech", "Ray", "Dinosaur",
        "Dragon", "Trap", "Instrument", "Loot", "Reagent"
    ];

    private static readonly Lazy<GameDataRepository> RepoLazy = new(() =>
    {
        var envDir = Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
            return GameDatabaseLoader.Load(Path.Combine(envDir, "GameData.db"), Path.Combine(envDir, "zh-CN.json"));
        if (Directory.Exists(AuthoritativeDataDir))
            return GameDatabaseLoader.Load(Path.Combine(AuthoritativeDataDir, "GameData.db"), Path.Combine(AuthoritativeDataDir, "zh-CN.json"));
        return GameDatabaseLoader.Load();
    });

    private static GameDataRepository Repo => RepoLazy.Value;
    private readonly ITestOutputHelper _output;

    public V12SemanticAuditHeroesBTests(ITestOutputHelper output) => _output = output;

    private static CardTemplate FindByIdOrTitle(string idPrefixOrTitle, string? type = null)
    {
        foreach (var kv in Repo.CardsById)
        {
            if (kv.Key.StartsWith(idPrefixOrTitle, StringComparison.OrdinalIgnoreCase)
                && (type == null || string.Equals(kv.Value.Type, type, StringComparison.OrdinalIgnoreCase)))
                return kv.Value;
        }
        foreach (var kv in Repo.CardsById)
        {
            var title = kv.Value.Localization?.Title?.Text ?? string.Empty;
            if (string.Equals(title, idPrefixOrTitle, StringComparison.OrdinalIgnoreCase)
                && (type == null || string.Equals(kv.Value.Type, type, StringComparison.OrdinalIgnoreCase)))
                return kv.Value;
        }
        throw new InvalidOperationException($"未找到卡牌模板: {idPrefixOrTitle}");
    }

    // -------------------------------------------------------------------------
    // 一、已修复引擎 Silent No-Op 专项回归测试
    // -------------------------------------------------------------------------

    [Fact]
    public void TActionCardDisable_DispatchesDestructionLifecycle_RespectsImmunity_AndTransformsBoxOfFireworksToTwo()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Jules", "Jules", 2000, 20, 50, 10);
        var p2 = new PlayerState("p2", "Enemy", "Vanessa", 2000, 20, 50, 10);

        // Box of Fireworks: Ab[0] 对自身 TActionCardDisable；触发 TTriggerOnBeforeCardDestroyed -> TActionCardTransformDestroyed (2 Firecrackers)
        var fireworksTpl = FindByIdOrTitle("6f65b541", "Item");
        var fireworks = new CardInstance(fireworksTpl);
        p1.Hand.TryPlaceOrShift(fireworks, 0);

        // 免疫卡 (Radiant)
        var immuneTpl = FindByIdOrTitle("d9b24744", "Item"); // Gyro Gunsight
        var immuneCard = new CardInstance(immuneTpl);
        immuneCard.ApplyEnchantment("Radiant");
        p1.Hand.TryPlaceOrShift(immuneCard, 2);

        var events = new List<CombatEvent>();
        var firedTriggers = new List<string>();
        RuleExecutionContext ctx = null!;
        ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1))
        {
            LocalPlayerId = p1.PlayerId,
            EmitCombatEvent = ev => events.Add(ev),
            OnDetailedTriggeredEvent = (evName, actor, src, tgt, subj, attr, prev, cur) =>
            {
                firedTriggers.Add(evName);
                foreach (var snapCard in actor.EnumerateAllOwnedCards())
                {
                    if (snapCard.IsDestroyed) continue;
                    foreach (var ab in snapCard.ActiveAbilities)
                    {
                        if (ab.Action == null) continue;
                        var abCtx = ctx.WithCards(snapCard, src, tgt);
                        if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx)) continue;
                        if (!AbilityExecutor.MatchesTrigger(ab.Trigger, evName, snapCard, actor, src, tgt, subj, attr, prev, cur, "PVP", "", abCtx)) continue;
                        AbilityExecutor.ExecuteAction(ab.Action, snapCard, abCtx);
                    }
                }
            }
        };

        // 1. Radiant 目标应被 TActionCardDisable 免疫跳过
        var disableImmune = new RuleNode
        {
            Type = "TActionCardDisable",
            Target = new RuleNode { Type = "TTargetCardSelf" }
        };
        AbilityExecutor.ExecuteAction(disableImmune, immuneCard, ctx.WithCards(immuneCard));
        Assert.False(immuneCard.IsDisabled);

        // 2. Box of Fireworks 自毁 -> 触发 BeforeCardDestroyed -> TransformDestroyed -> 产出 2 个 Firecrackers 且均可用
        var disableSelfAction = fireworks.ActiveAbilities.First(a => a.Action?.Type == "TActionCardDisable").Action!;
        AbilityExecutor.ExecuteAction(disableSelfAction, fireworks, ctx.WithCards(fireworks));

        Assert.Contains("TTriggerOnBeforeCardDestroyed", firedTriggers);
        Assert.Contains("TTriggerOnCardDisabled", firedTriggers);
        Assert.Contains("TTriggerOnCardPerformedDestruction", firedTriggers);
        Assert.Contains(events, e => e.Type == CombatEventType.CardDestroyed && e.TargetCardInstanceId == fireworks.InstanceId);

        // Box of Fireworks 已被替换并产出 2 个新物品在 Hand 中（扣除原本免疫卡 = 剩余 3 张，其中 2 张是 Firecracker）
        var firecrackers = p1.Hand.Cards.Where(c => !c.IsDestroyed && !c.IsDisabled && c != immuneCard).ToList();
        Assert.Equal(2, firecrackers.Count);
        Assert.All(firecrackers, fc => Assert.Contains("Firecracker", fc.Template.Localization?.Title?.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbductedCow_DisableAndTransformDestroyed_WhenTargetAlreadyDisabled_StillSpawnsTwoJerkies()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Player", "Common", 2000, 20, 50, 10);
        var p2 = new PlayerState("p2", "Enemy", "Vanessa", 2000, 20, 50, 10);

        var cowTpl = FindByIdOrTitle("03f90f0b", "Item"); // Abducted Cow
        var cow = new CardInstance(cowTpl);
        p1.Hand.TryPlaceOrShift(cow, 0);
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 1, 1, repo);

        // 模拟战斗队列延迟：TActionCardDisable 已将 Abducted Cow 标为 IsDisabled = true，随后 TTriggerOnBeforeCardDestroyed 出队执行 TActionCardTransformDestroyed
        cow.IsDisabled = true;
        var trAb = cow.ActiveAbilities.First(a => a.Action?.Type == "TActionCardTransformDestroyed");
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(7))
        {
            LocalPlayerId = p1.PlayerId
        };
        AbilityExecutor.ExecuteAction(trAb.Action!, cow, ctx.WithCards(cow, cow, cow));

        var jerkies = p1.Hand.Cards.Where(c => !c.IsDestroyed && !c.IsDisabled).ToList();
        Assert.Equal(2, jerkies.Count);
    }

    [Fact]
    public void TActionCardModifyAttribute_FreezeSlowHaste_ClearsRealtimeMs_AndTriggersAttrChanged()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Hero8", "Hero8", 2000, 20, 50, 10) { Tempo = 10 };
        var glaiveTpl = FindByIdOrTitle("45b2aadd", "Item"); // Whistling Glaive
        var glaive = new CardInstance(glaiveTpl) { FreezeMs = 3000, SlowMs = 2500 };
        p1.Hand.TryPlaceOrShift(glaive, 0);

        var attrChanges = new List<(string Attr, double Prev, double Cur)>();
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(3))
        {
            LocalPlayerId = p1.PlayerId,
            OnDetailedTriggeredEvent = (ev, actor, src, tgt, subj, attr, prev, cur) =>
            {
                if (ev == "TTriggerOnCardAttributeChanged" && attr != null)
                    attrChanges.Add((attr, prev, cur));
            }
        };

        // 减 Freeze (Subtract 1000000) 与减 Slow (Subtract 1000000)
        var unfreezeAb = glaive.ActiveAbilities.First(a => a.Action?.Type == "TActionCardModifyAttribute" && a.Action.AttributeType == "Freeze");
        var unslowAb = glaive.ActiveAbilities.First(a => a.Action?.Type == "TActionCardModifyAttribute" && a.Action.AttributeType == "Slow");
        Assert.True(AbilityExecutor.ExecuteAction(unfreezeAb.Action!, glaive, ctx.WithCards(glaive)));
        Assert.True(AbilityExecutor.ExecuteAction(unslowAb.Action!, glaive, ctx.WithCards(glaive)));

        Assert.Equal(0, glaive.FreezeMs);
        Assert.Equal(0, glaive.SlowMs);
        Assert.Equal(0, glaive.GetAttribute("Freeze"));
        Assert.Equal(0, glaive.GetAttribute("Slow"));
        Assert.Contains(attrChanges, x => x.Attr == "Freeze" && x.Prev == 3000 && x.Cur == 0);
        Assert.Contains(attrChanges, x => x.Attr == "Slow" && x.Prev == 2500 && x.Cur == 0);

        // 验证无负值残留：后续再次施加 Freeze 时可直接恢复正值
        glaive.ModifyAttribute("Freeze", "Add", 1000, DurationTarget.Combat);
        Assert.Equal(1000, glaive.FreezeMs);

        // 验证 Multiply 0 清零（Base Rage Effect 式用法）
        glaive.ModifyAttribute("Freeze", "Multiply", 0, DurationTarget.Combat);
        Assert.Equal(0, glaive.FreezeMs);
    }

    [Fact]
    public void TActionCardRepair_RandomTargetsOnlyDamagedOrDisabled_NeverFiresFalseRepairs()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Stelle", "Stelle", 2000, 20, 50, 10);
        var repairDrone = new CardInstance(FindByIdOrTitle("04fc1f55", "Item")); // Repair Drone
        var healthy1 = new CardInstance(FindByIdOrTitle("d9b24744", "Item")); // Gyro Gunsight
        var healthy2 = new CardInstance(FindByIdOrTitle("4739a012", "Item")); // Anemometer
        var disabledCard = new CardInstance(FindByIdOrTitle("80d4de6b", "Item")) { IsDisabled = true }; // Kite

        p1.Hand.TryPlaceOrShift(repairDrone, 0);
        p1.Hand.TryPlaceOrShift(healthy1, 1);
        p1.Hand.TryPlaceOrShift(healthy2, 2);
        p1.Hand.TryPlaceOrShift(disabledCard, 3);

        var repairedTargets = new List<CardInstance>();
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(11))
        {
            LocalPlayerId = p1.PlayerId,
            OnDetailedTriggeredEvent = (ev, actor, src, tgt, subj, attr, prev, cur) =>
            {
                if (ev == "TTriggerOnCardRepaired" && tgt != null)
                    repairedTargets.Add(tgt);
            }
        };

        var repairAb = repairDrone.ActiveAbilities.First(a => a.Action?.Type == "TActionCardRepair");
        AbilityExecutor.ExecuteAction(repairAb.Action!, repairDrone, ctx.WithCards(repairDrone));

        Assert.False(disabledCard.IsDisabled);
        Assert.Single(repairedTargets);
        Assert.Same(disabledCard, repairedTargets[0]);

        // 全面板健康时再次运行：不得触发误报 TTriggerOnCardRepaired
        repairedTargets.Clear();
        AbilityExecutor.ExecuteAction(repairAb.Action!, repairDrone, ctx.WithCards(repairDrone));
        Assert.Empty(repairedTargets);
    }

    [Fact]
    public void TActionPlayerReviveHeal_EmitsHealApplied_PerformedHeal_AndHealthChanged()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Player", "Common", 1000, 20, 50, 10) { CurrentHealth = 0 };
        var makersTpl = FindByIdOrTitle("3a939e5a", "Item"); // Maker's Mark
        var makers = new CardInstance(makersTpl);
        p1.Hand.TryPlaceOrShift(makers, 0);

        var events = new List<CombatEvent>();
        var triggers = new List<string>();
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(5))
        {
            LocalPlayerId = p1.PlayerId,
            EmitCombatEvent = e => events.Add(e),
            OnDetailedTriggeredEvent = (ev, actor, src, tgt, subj, attr, prev, cur) => triggers.Add(ev)
        };

        var reviveAb = makers.ActiveAbilities.First(a => a.Action?.Type == "TActionPlayerReviveHeal");
        AbilityExecutor.ExecuteAction(reviveAb.Action!, makers, ctx.WithCards(makers));

        Assert.True(p1.CurrentHealth > 0);
        Assert.Contains(events, e => e.Type == CombatEventType.HealApplied && e.TargetPlayerId == p1.PlayerId);
        Assert.Contains("TTriggerOnCardPerformedHeal", triggers);
        Assert.Contains("TTriggerOnPlayerAttributeChanged", triggers);
    }

    [Fact]
    public void TActionCardTransform_ExecutesTransformAbilities_OnChunkOfLeadAndMyrrh()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Mak", "Mak", 2000, 20, 50, 10);
        var lead = new CardInstance(FindByIdOrTitle("ef20ec33", "Item")); // Chunk of Lead
        p1.Hand.TryPlaceOrShift(lead, 0);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(19))
        {
            LocalPlayerId = p1.PlayerId
        };

        // 构造一个 Catalyst 式的 Transform 动作把 Chunk of Lead 变形为任意 Small 物品 -> 应触发 Chunk of Lead 的 Transform.Abilities 生成 Chunk of Gold
        var transformAction = new RuleNode
        {
            Type = "TActionCardTransform",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            SpawnContext = new RuleNode
            {
                Type = "TSpawnContextQuery",
                SelectionMethod = "Random",
                Limit = new RuleNode
                {
                    Type = "TFixedValue",
                    RawValueNode = JsonSerializer.SerializeToElement(1.0, GameDatabaseLoader.JsonOptions)
                },
                Groups =
                [
                    new RuleNode
                    {
                        Type = "TSpawnGroup",
                        Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = [FindByIdOrTitle("d9b24744", "Item").Id] }]
                    }
                ]
            }
        };

        AbilityExecutor.ExecuteAction(transformAction, lead, ctx.WithCards(lead));

        Assert.True(lead.IsDestroyed);
        var spawnedGoldId = "73f29a8c-4636-4475-ae76-8ab5fbdf9a5b"; // 或根据模板名/SellPrice 验证生成的并非 Gyro Gunsight 的额外衍生卡
        Assert.True(p1.Hand.Cards.Count + p1.Stash.Cards.Count >= 2, $"Chunk of Lead 变形后应额外生成 Chunk of Gold ({spawnedGoldId})");

        // Myrrh 变形应附加 Restorative 附魔
        var myrrh = new CardInstance(FindByIdOrTitle("01f6e150", "Item")); // Myrrh
        p1.Hand.TryPlaceOrShift(myrrh, 3);
        AbilityExecutor.ExecuteAction(transformAction, myrrh, ctx.WithCards(myrrh));
        var myrrhResult = p1.Hand.Cards.First(c => c.StartSlot == 3 && !c.IsDestroyed);
        Assert.Equal("Restorative", myrrhResult.Enchantment);
    }

    [Fact]
    public void TActionCardAddTagsRandom_AddsDistinctMissingTagsWithoutDuplicates()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Player", "Common", 2000, 20, 50, 10);
        var wigCloset = new CardInstance(FindByIdOrTitle("40079237", "Item")); // Wig Closet
        p1.Hand.TryPlaceOrShift(wigCloset, 0);

        var addTagsAb = wigCloset.ActiveAbilities.First(a => a.Action?.Type == "TActionCardAddTagsRandom");
        var beforeTags = wigCloset.GetAllTags().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(23))
        {
            LocalPlayerId = p1.PlayerId
        };
        AbilityExecutor.ExecuteAction(addTagsAb.Action!, wigCloset, ctx.WithCards(wigCloset, wigCloset, wigCloset));

        var afterTags = wigCloset.GetAllTags().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, afterTags.Count - beforeTags.Count);
    }

    [Fact]
    public void QuestSystem_ExecutesCompletionEffects_InjectsRewardTags_AndAdvancesOnDirectQuestAttr()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Stelle", "Stelle", 2000, 20, 50, 10);

        // 1. Recycling Truck: 完成任务应执行 CompletionEffects 生成 Drone/Tool 并触发 QuestCompleted
        var truck = new CardInstance(FindByIdOrTitle("3c7807ef", "Item")); // Recycling Truck
        p1.Hand.TryPlaceOrShift(truck, 0);
        var soldDrone = new CardInstance(FindByIdOrTitle("04fc1f55", "Item")); // Repair Drone
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(29))
        {
            LocalPlayerId = p1.PlayerId
        };

        int beforeTotalOwned = p1.Hand.Cards.Count + p1.Stash.Cards.Count;
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardSold", p1, soldDrone, soldDrone, p1, null, 0, 0, "PVP", "", ctx);
        int afterTotalOwned = p1.Hand.Cards.Count + p1.Stash.Cards.Count;
        Assert.NotEmpty(truck.CompletedQuests);
        Assert.True(afterTotalOwned > beforeTotalOwned, "Recycling Truck 完成任务后应通过 CompletionEffects 生成 Drone 或 Tool");

        // 2. Waystones: Quest_1 由选择特定遭遇 (dc12f4bd) 触发，完成后 QuestReward.Tags ("Weapon") 应写入 HasTag
        var waystones = new CardInstance(FindByIdOrTitle("de82424c", "Item")); // Waystones
        p1.Hand.TryPlaceOrShift(waystones, 4);
        var ailaEncounter = new CardInstance(FindByIdOrTitle("dc12f4bd"));
        AbilityExecutor.TryAdvanceQuests("TTriggerOnEncounterSelected", p1, ailaEncounter, ailaEncounter, p1, null, 0, 0, "PVP", "", ctx);
        Assert.Contains("Quest_1", waystones.CompletedQuests);
        Assert.True(waystones.HasTag("Weapon"), "Waystones Quest_1 完成后应注入 Weapon 标签");

        // 3. Basket: 直接修改 Quest_1 属性应同步推进 QuestProgress 并在达标时完成 Quest_1
        var basket = new CardInstance(FindByIdOrTitle("30da8c8f", "Item")); // Basket
        p1.Hand.TryPlaceOrShift(basket, 8);
        var modQuest = new RuleNode
        {
            Type = "TActionCardModifyAttribute",
            AttributeType = "Quest_1",
            Operation = "Add",
            Target = new RuleNode { Type = "TTargetCardSelf" },
            RawValueNode = JsonSerializer.SerializeToElement(
                new Dictionary<string, object> { ["$type"] = "TFixedValue", ["Value"] = 10.0 },
                GameDatabaseLoader.JsonOptions)
        };
        AbilityExecutor.ExecuteAction(modQuest, basket, ctx.WithCards(basket));
        Assert.True(basket.QuestProgress.GetValueOrDefault("Quest_1", 0) >= 8);
        Assert.Contains("Quest_1", basket.CompletedQuests);
    }

    [Fact]
    public void TActionGameSpawnCards_AppliesAttributesTiny_AndSizeinatorShrinksTargetWithEvent()
    {
        var repo = Repo;
        var p1 = new PlayerState("p1", "Common", "Common", 2000, 20, 50, 10);

        // 1. 测试 Shipment 当携带 Tiny==1 时，Ab[1] 生成附带 Attributes={"Tiny": 1.0} 的 Medium 物品可直接塞进只剩 1 格空间的 Hand
        var shipment = new CardInstance(FindByIdOrTitle("103cd65b", "Item")); // Shipment
        shipment.ModifyAttribute("Tiny", "Add", 1, DurationTarget.Permanent);
        var tinyTestCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(31))
        {
            LocalPlayerId = p1.PlayerId
        };
        // 填满前 9 格（3张真实 Large 3格卡）只留 slot 9 一格空位
        var realLargeTpl = repo.AlwaysSpawnableItems.First(t => t.CardSize == CardSize.Large);
        var largeCard1 = new CardInstance(realLargeTpl);
        var largeCard2 = new CardInstance(realLargeTpl);
        var largeCard3 = new CardInstance(realLargeTpl);
        p1.Hand.TryPlaceOrShift(largeCard1, 0);
        p1.Hand.TryPlaceOrShift(largeCard2, 3);
        p1.Hand.TryPlaceOrShift(largeCard3, 6);

        var shipTinyAb = shipment.ActiveAbilities.First(a => a.Action?.Type == "TActionGameSpawnCards" && a.Action.Attributes?.ContainsKey("Tiny") == true);
        AbilityExecutor.ExecuteAction(shipTinyAb.Action!, shipment, tinyTestCtx.WithCards(shipment));
        var tinySpawned = p1.Hand.Cards.FirstOrDefault(c => c.StartSlot == 9);
        Assert.NotNull(tinySpawned);
        Assert.True(tinySpawned!.IsTiny);
        Assert.Equal(1, tinySpawned.SlotWidth);
        Assert.True(tinySpawned.HasTag("Tiny"));

        // 2. 测试 Sizeinator 触发 TActionCardTiny 后大卡变小且触发 Tiny 属性变更事件
        var p2 = new PlayerState("p2", "Common", "Common", 2000, 20, 50, 10);
        var truck = new CardInstance(realLargeTpl); // Large -> 3 格变 Medium 2 格
        var sizeinator = new CardInstance(FindByIdOrTitle("7b30db1a", "Item")); // Sizeinator
        p2.Hand.TryPlaceOrShift(truck, 0);
        p2.Hand.TryPlaceOrShift(sizeinator, 5);

        bool tinyAttrFired = false;
        var ctx2 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p2, null, new Random(37))
        {
            LocalPlayerId = p2.PlayerId,
            OnDetailedTriggeredEvent = (ev, actor, src, tgt, subj, attr, prev, cur) =>
            {
                if (ev == "TTriggerOnCardAttributeChanged" && attr == "Tiny" && tgt == truck)
                    tinyAttrFired = true;
            }
        };
        var tinyAb = sizeinator.ActiveAbilities.First(a => a.Action?.Type == "TActionCardTiny");
        AbilityExecutor.ExecuteAction(tinyAb.Action!, sizeinator, ctx2.WithCards(sizeinator));
        Assert.True(truck.IsTiny);
        Assert.Equal(2, truck.SlotWidth);
        Assert.True(tinyAttrFired);
    }

    [Fact]
    public void ShardA_And_DisabledTrigger_Handover_Fixes_VerifyAll()
    {
        var repo = Repo;

        // 1. #253 Streaming Setup (2a0624d0) & Clean Storefront (6bd675c5): TAuraActionCardModifyAttribute Value.Target==null 按目标求值
        var p1 = new PlayerState("p1", "Pygmalien", "Pygmalien", 2000, 20, 50, 10);
        var streaming = new CardInstance(FindByIdOrTitle("2a0624d0", "Item")); // Streaming Setup
        var healItemTpl = repo.AlwaysSpawnableItems.First(t => t.CardSize == CardSize.Small && t.HiddenTags.Contains("Heal", StringComparer.OrdinalIgnoreCase));
        var targetToy = new CardInstance(healItemTpl);
        targetToy.ExtraTags.Add("Toy");
        targetToy.PermanentAdd["SellPrice"] = 12;
        double expectedSell = targetToy.GetAttribute("SellPrice", repo.GameMode.StandardPrices);
        p1.Hand.TryPlaceOrShift(streaming, 0);
        p1.Hand.TryPlaceOrShift(targetToy, 3);
        AuraEngine.RecalculateAuras(p1, null, RuleScope.InCombat, 3, 2, repo);
        Assert.Equal(expectedSell, targetToy.AuraAdd.GetValueOrDefault("HealAmount", 0));

        // 2. #253 Venomous Vitality (6f593cc1) 两遍光环收敛（Aura#0 读 Aura#1 按敌方 Poison*0.5 设置的 RegenApplyAmount 不再为 0，且不倍增）
        var p2 = new PlayerState("p2", "Mak", "Mak", 2000, 20, 50, 10);
        var opp2 = new PlayerState("opp2", "Vanessa", "Vanessa", 2000, 20, 50, 10) { Poison = 12 };
        var venomSkill = new CardInstance(FindByIdOrTitle("6f593cc1", "Skill")); // Venomous Vitality
        var poisonItemTpl = repo.AlwaysSpawnableItems.First(t => t.CardSize == CardSize.Small && t.HiddenTags.Contains("Poison", StringComparer.OrdinalIgnoreCase));
        var poisonItem = new CardInstance(poisonItemTpl);
        p2.Skills.Add(venomSkill);
        p2.Hand.TryPlaceOrShift(poisonItem, 0);
        AuraEngine.RecalculateAuras(p2, opp2, RuleScope.InCombat, 3, 2, repo);
        double regenAmount = venomSkill.GetAttribute("RegenApplyAmount");
        Assert.Equal(6, regenAmount);
        Assert.Equal(regenAmount, p2.AuraAdd.GetValueOrDefault("HealthRegen", 0));
        // 再次重算不得翻倍
        AuraEngine.RecalculateAuras(p2, opp2, RuleScope.InCombat, 3, 2, repo);
        Assert.Equal(6, venomSkill.GetAttribute("RegenApplyAmount"));
        Assert.Equal(6, p2.AuraAdd.GetValueOrDefault("HealthRegen", 0));

        // 3. #253 Adrenal Converter (388a396e): TTriggerOnCardPerformedPoison Target=TTargetPlayerRelative(Self) 匹配 eventSubjectPlayer
        var adrenal = new CardInstance(FindByIdOrTitle("388a396e", "Item")); // Adrenal Converter
        var p3 = new PlayerState("p3", "Mak", "Mak", 2000, 20, 50, 10);
        var opp3 = new PlayerState("opp3", "Vanessa", "Vanessa", 2000, 20, 50, 10);
        var poisonItem3 = new CardInstance(poisonItemTpl);
        p3.Hand.TryPlaceOrShift(adrenal, 0);
        p3.Hand.TryPlaceOrShift(poisonItem3, 2);
        var performedPoisonAb = adrenal.ActiveAbilities.First(a => a.Trigger?.Type == "TTriggerOnCardPerformedPoison");
        var checkCtx = new RuleExecutionContext(repo, RuleScope.InCombat, p3, opp3, new Random(1)) { LocalPlayerId = p3.PlayerId }
            .WithCards(adrenal, poisonItem3, poisonItem3);
        Assert.True(AbilityExecutor.MatchesTrigger(
            performedPoisonAb.Trigger, "TTriggerOnCardPerformedPoison",
            adrenal, p3, poisonItem3, poisonItem3, p3, null, 0, 0, "PVP", "", checkCtx));
        Assert.False(AbilityExecutor.MatchesTrigger(
            performedPoisonAb.Trigger, "TTriggerOnCardPerformedPoison",
            adrenal, p3, poisonItem3, poisonItem3, opp3, null, 0, 0, "PVP", "", checkCtx));

        // 4. #255/#256 Repair Mechanic (1798b189, Skill): TActionCardDisable 后 TTriggerOnCardDisabled 匹配并成功修复禁用卡
        var p4 = new PlayerState("p4", "Dooley", "Dooley", 2000, 20, 50, 10);
        var repairMech = new CardInstance(FindByIdOrTitle("1798b189", "Skill"));
        var doomed = new CardInstance(FindByIdOrTitle("d9b24744", "Item")); // Gyro Gunsight
        p4.Skills.Add(repairMech);
        p4.Hand.TryPlaceOrShift(doomed, 2);
        RuleExecutionContext rCtx = null!;
        rCtx = new RuleExecutionContext(repo, RuleScope.InCombat, p4, null, new Random(2))
        {
            LocalPlayerId = p4.PlayerId,
            OnDetailedTriggeredEvent = (ev, actor, src, tgt, subj, attr, prev, cur) =>
            {
                if (ev != "TTriggerOnCardDisabled") return;
                foreach (var ab in repairMech.ActiveAbilities.Where(a => a.Trigger?.Type == "TTriggerOnCardDisabled"))
                {
                    var abCtx = rCtx.WithCards(repairMech, src, tgt);
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx)) continue;
                    if (!AbilityExecutor.MatchesTrigger(ab.Trigger, ev, repairMech, actor, src, tgt, subj, attr, prev, cur, "PVP", "", abCtx)) continue;
                    AbilityExecutor.ExecuteAction(ab.Action!, repairMech, abCtx);
                }
            }
        };
        AbilityExecutor.ExecuteAction(
            new RuleNode { Type = "TActionCardDisable", Target = new RuleNode { Type = "TTargetCardSelf" } },
            doomed,
            rCtx.WithCards(doomed));
        Assert.False(doomed.IsDisabled, "Repair Mechanic 应在 TActionCardDisable 后匹配 TTriggerOnCardDisabled 并立即修好目标卡");

        // 5. Get Back In There! (a56ea57d Ab[1]): TTriggerOnCardAfterTransform 尊重 Trigger.Target=SelfHand
        var p5 = new PlayerState("p5", "Jules", "Jules", 2000, 20, 50, 10);
        var getBack = new CardInstance(FindByIdOrTitle("a56ea57d", "Skill")); // Get Back In There!
        var transformed = new CardInstance(FindByIdOrTitle("d9b24744", "Item"));
        transformed.PermanentAdd["CooldownMax"] = Math.Max(0, 5000 - transformed.BaseAttributes.GetValueOrDefault("CooldownMax", 0));
        p5.Skills.Add(getBack);
        p5.Hand.TryPlaceOrShift(transformed, 0);
        var afterTxAb = getBack.ActiveAbilities.First(a => a.Trigger?.Type == "TTriggerOnCardAfterTransform");
        var txCtx = new RuleExecutionContext(repo, RuleScope.InCombat, p5, null, new Random(3)) { LocalPlayerId = p5.PlayerId }
            .WithCards(getBack, doomed, transformed);
        Assert.True(AbilityExecutor.MatchesTrigger(afterTxAb.Trigger, "TTriggerOnCardAfterTransform", getBack, p5, doomed, transformed, p5, null, 0, 0, "PVP", "", txCtx));
        AbilityExecutor.ExecuteAction(afterTxAb.Action!, getBack, txCtx);
        Assert.True(transformed.HasteMs >= 1000);

        // 6. CardInstance.ModifyAttribute("Tiny", ...) 与 ExtraTags / GetAttribute("Tiny") 同步生效
        var tinyTest = new CardInstance(FindByIdOrTitle("d9b24744", "Item"));
        tinyTest.ModifyAttribute("Tiny", "Set", 1, DurationTarget.Permanent);
        Assert.Equal(1.0, tinyTest.GetAttribute("Tiny"));
        Assert.True(tinyTest.IsTiny);
        tinyTest.ModifyAttribute("Tiny", "Set", 0, DurationTarget.Permanent);
        Assert.Equal(0.0, tinyTest.GetAttribute("Tiny"));
        Assert.False(tinyTest.IsTiny);
    }

    // -------------------------------------------------------------------------
    // 二、Shard B 全量 1,011 卡逐能力 / 逐光环 / 逐任务语义审计
    // -------------------------------------------------------------------------

    private sealed class AuditReport
    {
        public string GeneratedAt { get; set; } = string.Empty;
        public int TotalShardBCards { get; set; }
        public int SemanticVerifiedCards { get; set; }
        public int TotalAbilitiesAudited { get; set; }
        public int TotalAurasAudited { get; set; }
        public int TotalQuestsAudited { get; set; }
        public int UnexplainedSilentNoOpAbilities { get; set; }
        public int UnexplainedSilentNoOpAuras { get; set; }
        public int UnexplainedSilentNoOpQuests { get; set; }
        public Dictionary<string, int> ByHeroCounts { get; set; } = new();
        public List<SilentNoOpEntry> Failures { get; set; } = new();
        public List<string> ExpectedPassiveOrExternalNotes { get; set; } = new();
    }

    private sealed class SilentNoOpEntry
    {
        public string CardId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<string> Heroes { get; set; } = [];
        public string Kind { get; set; } = string.Empty; // Ability / Aura / Quest
        public string EntryId { get; set; } = string.Empty;
        public string TriggerOrTarget { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    [Fact]
    public void ShardB_AllAlwaysItemsAndSkills_FullSemanticAudit_ZeroSilentNoOps()
    {
        var repo = Repo;
        var allCandidates = repo.AlwaysSpawnableItems
            .Concat(repo.AlwaysSpawnableSkills)
            .Where(t => t.Heroes.Any(h => ShardBHeroes.Contains(h, StringComparer.OrdinalIgnoreCase)))
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Localization?.Title?.Text ?? t.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(allCandidates.Count >= 950, $"Shard B Always 物品与技能数量异常偏低: {allCandidates.Count}");

        var report = new AuditReport
        {
            GeneratedAt = DateTime.UtcNow.ToString("O"),
            TotalShardBCards = allCandidates.Count
        };
        foreach (var hero in ShardBHeroes)
            report.ByHeroCounts[hero] = allCandidates.Count(c => c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase));

        // 预先构建干净基础模板池供动态适配特定条件或 ID 筛选时选用（兼顾顶层 Attributes 与 Tiers 内 Attributes）
        static bool HasCooldown(CardTemplate t) =>
            (t.Attributes?.GetValueOrDefault("CooldownMax", 0) ?? 0) > 0
            || (t.Tiers?.Values.Any(td => td.Attributes.GetValueOrDefault("CooldownMax", 0) > 0) == true);

        var allItemTemplates = repo.AlwaysSpawnableItems.ToList();
        var smallCleanItem = repo.AlwaysSpawnableItems.FirstOrDefault(t => t.CardSize == CardSize.Small && HasCooldown(t) && t.Auras.Count == 0)
                             ?? repo.AlwaysSpawnableItems.First(t => t.CardSize == CardSize.Small && HasCooldown(t));
        var mediumCleanItem = repo.AlwaysSpawnableItems.FirstOrDefault(t => t.CardSize == CardSize.Medium && HasCooldown(t) && t.Auras.Count == 0)
                              ?? repo.AlwaysSpawnableItems.First(t => t.CardSize == CardSize.Medium && HasCooldown(t));
        var smallSkill = repo.AlwaysSpawnableSkills.First();

        // 从 SocketEffect 中数据驱动反查每个 Hero8 大调技能 ID 关联的 [X Note] 音符插座
        var musicNoteSockets = repo.CardsById.Values
            .Where(t => string.Equals(t.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var majorSkillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "7252924d-84d5-4744-bb88-296b1c853e2f", // A Major
            "537bff12-2c68-499c-bbed-9655df7a7575", // B Major
            "be26a0b9-1f46-4de7-bc47-b13a4f216197", // C Major
            "91749ebb-8785-4e39-adcc-be6034578319", // D Major
            "1214268a-88fa-49f0-80d2-2be346318d4a", // E Major
            "1c884c49-8eed-4187-9e2e-e3cba64832f2", // F Major
            "f54816af-566f-43f3-8153-4b1d82a22d38"  // G Major
        };
        var majorSkillToNote = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in musicNoteSockets)
        {
            var roots = note.Abilities.Values.SelectMany(a => new[] { a.Trigger, a.Action }.Concat(a.Prerequisites ?? [])!)
                .Concat(note.Auras.Values.SelectMany(au => new[] { au.Action }.Concat(au.Prerequisites ?? [])!))
                .Where(n => n != null)!;
            foreach (var rn in WalkNodes(roots!))
            {
                if (!string.IsNullOrEmpty(rn.Id) && majorSkillIds.Contains(rn.Id))
                    majorSkillToNote[rn.Id] = note;
            }
        }

        foreach (var tpl in allCandidates)
        {
            string title = tpl.Localization?.Title?.Text ?? tpl.Id;
            bool cardHadAnyFailure = false;

            var probeCard = CreateRepresentativeCard(tpl);

            // 特殊情况 1: Bar of Gold（纯卖价经济卡，模板本无主动能力/光环/任务）与 Hero8 大调音符联动技能
            if (probeCard.ActiveAbilities.Count == 0
                && probeCard.ActiveAuras.Count == 0
                && (tpl.Quests == null || tpl.Quests.Count == 0))
            {
                if (majorSkillToNote.TryGetValue(tpl.Id, out var noteTpl))
                {
                    bool ok = VerifyMajorSkillViaNote(repo, tpl, noteTpl, allItemTemplates, smallCleanItem);
                    if (!ok)
                    {
                        cardHadAnyFailure = true;
                        report.Failures.Add(new SilentNoOpEntry
                        {
                            CardId = tpl.Id,
                            Title = title,
                            Heroes = tpl.Heroes,
                            Kind = "SkillSynergy",
                            EntryId = noteTpl.Id,
                            ActionType = "MusicNoteSocketEffect",
                            Reason = "大调技能未能在对应音符插座激活增量"
                        });
                    }
                    else
                    {
                        report.ExpectedPassiveOrExternalNotes.Add($"{title}({tpl.Id}): Verified via SocketEffect {noteTpl.Id} synergy");
                    }
                }
                else
                {
                    // Bar of Gold 等纯价值卡：验证 GetSellPrice > 0
                    int sell = probeCard.GetSellPrice(repo.GameMode.StandardPrices);
                    if (sell <= 0)
                    {
                        cardHadAnyFailure = true;
                        report.Failures.Add(new SilentNoOpEntry
                        {
                            CardId = tpl.Id,
                            Title = title,
                            Heroes = tpl.Heroes,
                            Kind = "Value",
                            EntryId = "SellPrice",
                            ActionType = "PassiveValue",
                            Reason = "无能力且 SellPrice <= 0"
                        });
                    }
                    else
                    {
                        report.ExpectedPassiveOrExternalNotes.Add($"{title}({tpl.Id}): Pure economy card SellPrice={sell}");
                    }
                }

                if (!cardHadAnyFailure) report.SemanticVerifiedCards++;
                continue;
            }

            // 1. 逐条验证 ActiveAbilities
            foreach (var ab in probeCard.ActiveAbilities)
            {
                if (ab.Action == null) continue;
                report.TotalAbilitiesAudited++;

                var abResult = AuditSingleAbility(repo, tpl, ab, allItemTemplates, smallCleanItem, mediumCleanItem, smallSkill);
                if (!abResult.Success)
                {
                    cardHadAnyFailure = true;
                    report.UnexplainedSilentNoOpAbilities++;
                    report.Failures.Add(new SilentNoOpEntry
                    {
                        CardId = tpl.Id,
                        Title = title,
                        Heroes = tpl.Heroes,
                        Kind = "Ability",
                        EntryId = ab.Id ?? "?",
                        TriggerOrTarget = ab.Trigger?.Type ?? "null",
                        ActionType = ab.Action.Type ?? "null",
                        Reason = abResult.Reason
                    });
                }
            }

            // 2. 逐条验证 ActiveAuras（跳过纯展示用 Tooltip Handling 光环）
            foreach (var au in probeCard.ActiveAuras)
            {
                if (string.Equals(au.InternalName, "Tooltip Handling", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (au.Action == null) continue;
                report.TotalAurasAudited++;

                var auResult = AuditSingleAura(repo, tpl, au, allItemTemplates, smallCleanItem, mediumCleanItem, smallSkill);
                if (!auResult.Success)
                {
                    cardHadAnyFailure = true;
                    report.UnexplainedSilentNoOpAuras++;
                    report.Failures.Add(new SilentNoOpEntry
                    {
                        CardId = tpl.Id,
                        Title = title,
                        Heroes = tpl.Heroes,
                        Kind = "Aura",
                        EntryId = au.Id ?? "?",
                        TriggerOrTarget = au.Action.Target?.Type ?? "null",
                        ActionType = au.Action.Type ?? "null",
                        Reason = auResult.Reason
                    });
                }
            }

            // 3. 逐条验证 Quests
            if (tpl.Quests is { Count: > 0 })
            {
                for (int qi = 0; qi < tpl.Quests.Count; qi++)
                {
                    var q = tpl.Quests[qi];
                    for (int ei = 0; ei < q.Entries.Count; ei++)
                    {
                        report.TotalQuestsAudited++;
                        var qResult = AuditSingleQuest(repo, tpl, q, q.Entries[ei], allItemTemplates, smallCleanItem, mediumCleanItem, smallSkill);
                        if (!qResult.Success)
                        {
                            cardHadAnyFailure = true;
                            report.UnexplainedSilentNoOpQuests++;
                            report.Failures.Add(new SilentNoOpEntry
                            {
                                CardId = tpl.Id,
                                Title = title,
                                Heroes = tpl.Heroes,
                                Kind = "Quest",
                                EntryId = $"{qi}.{ei}:{q.Entries[ei].AttributeType}",
                                TriggerOrTarget = q.Entries[ei].Trigger?.Type ?? "null",
                                ActionType = "QuestProgressAndReward",
                                Reason = qResult.Reason
                            });
                        }
                    }
                }
            }

            if (!cardHadAnyFailure) report.SemanticVerifiedCards++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AuditLogPath)!);
        File.WriteAllText(AuditLogPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        _output.WriteLine($"ShardB Total={report.TotalShardBCards}, Verified={report.SemanticVerifiedCards}, Abilities={report.TotalAbilitiesAudited}, Auras={report.TotalAurasAudited}, Quests={report.TotalQuestsAudited}, NoOpAbs={report.UnexplainedSilentNoOpAbilities}, NoOpAuras={report.UnexplainedSilentNoOpAuras}, NoOpQuests={report.UnexplainedSilentNoOpQuests}");
        if (report.Failures.Count > 0)
        {
            foreach (var f in report.Failures.Take(30))
                _output.WriteLine($"FAIL [{f.Kind}] {f.Title} ({f.CardId}) {f.EntryId} {f.TriggerOrTarget} -> {f.ActionType}: {f.Reason}");
        }

        Assert.Equal(0, report.UnexplainedSilentNoOpAbilities);
        Assert.Equal(0, report.UnexplainedSilentNoOpAuras);
        Assert.Equal(0, report.UnexplainedSilentNoOpQuests);
        Assert.Equal(report.TotalShardBCards, report.SemanticVerifiedCards);
    }

    private static CardInstance CreateRepresentativeCard(CardTemplate tpl)
    {
        var card = new CardInstance(tpl, tpl.DefaultTier);
        if (card.ActiveAbilities.Count == 0 && card.ActiveAuras.Count == 0 && tpl.Tiers is { Count: > 0 })
        {
            foreach (CardTier t in Enum.GetValues<CardTier>())
            {
                if (tpl.Tiers.ContainsKey(t.ToString()))
                {
                    card = new CardInstance(tpl, t);
                    if (card.ActiveAbilities.Count > 0 || card.ActiveAuras.Count > 0) break;
                }
            }
        }
        return card;
    }

    private static bool VerifyMajorSkillViaNote(
        GameDataRepository repo,
        CardTemplate majorTpl,
        CardTemplate noteTpl,
        List<CardTemplate> allItems,
        CardTemplate smallWeaponTpl)
    {
        var p1 = new PlayerState("p1", "Hero8", "Hero8", 2000, 20, 50, 10);
        var p2 = new PlayerState("p2", "Opp", "Vanessa", 2000, 20, 50, 10);

        // 从 Note 模板中的 TTargetCardOccupying 提取所需的 Tag / HiddenTag（如 Heal/Regen、Burn、Poison、Slow、Haste 等）
        var noteRoots = noteTpl.Auras.Values.Select(a => a.Action)
            .Concat(noteTpl.Abilities.Values.SelectMany(a => new[] { a.Trigger, a.Action }))
            .Where(n => n != null)!;
        var noteNodes = WalkNodes(noteRoots!).ToList();
        var reqTags = noteNodes.Where(n => n.Type == "TCardConditionalTag" && n.Tags != null && n.IsNot != true)
            .SelectMany(n => n.Tags!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reqHidden = noteNodes.Where(n => n.Type == "TCardConditionalHiddenTag" && n.Tags != null && n.IsNot != true)
            .SelectMany(n => n.Tags!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var chosenTpl = allItems.FirstOrDefault(t =>
                t.CardSize == CardSize.Small
                && (reqHidden.Count == 0 || reqHidden.Any(ht => t.HiddenTags.Contains(ht, StringComparer.OrdinalIgnoreCase))))
            ?? allItems.FirstOrDefault(t =>
                reqHidden.Count == 0 || reqHidden.Any(ht => t.HiddenTags.Contains(ht, StringComparer.OrdinalIgnoreCase)))
            ?? smallWeaponTpl;

        var item = CreateRepresentativeCard(chosenTpl);
        if (item.GetAttribute("CooldownMax") <= 0)
            item.PermanentAdd["CooldownMax"] = 5000;
        foreach (var rt in reqTags) item.ExtraTags.Add(rt);
        p1.Hand.TryPlaceOrShift(item, 0);

        var note = new CardInstance(noteTpl) { StartSlot = 0 };
        p1.SocketEffects.Add(note);

        // 将 Major Skill 先置入技能栏并标为 Disabled 取无技能基线（防止 SkillCount 干扰快照比对），再启用看完整快照增量
        var skill = CreateRepresentativeCard(majorTpl);
        skill.IsDisabled = true;
        p1.Skills.Add(skill);

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 3, 2, repo);
        var snapWithout = CaptureSnapshot(p1, p2);

        skill.IsDisabled = false;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, 3, 2, repo);
        var snapWith = CaptureSnapshot(p1, p2);

        if (HasAnySnapshotDelta(snapWithout, snapWith, ignoreDisabledFlagOnCard: skill))
            return true;

        // 兜底检查 OutOfCombat 光环域与 Note 上依赖 Major 技能解锁的 Abilities
        skill.IsDisabled = true;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, 3, 2, repo);
        snapWithout = CaptureSnapshot(p1, p2);
        skill.IsDisabled = false;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, 3, 2, repo);
        snapWith = CaptureSnapshot(p1, p2);
        if (HasAnySnapshotDelta(snapWithout, snapWith, ignoreDisabledFlagOnCard: skill))
            return true;

        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1)) { LocalPlayerId = p1.PlayerId };
        foreach (var ab in note.ActiveAbilities)
        {
            if (ab.Action == null || ab.Prerequisites == null || ab.Prerequisites.Count == 0) continue;
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, ctx.WithCards(note, item, item))) continue;
            var b4 = CaptureSnapshot(p1, p2);
            AbilityExecutor.ExecuteAction(ab.Action, note, ctx.WithCards(note, item, item));
            if (HasAnySnapshotDelta(b4, CaptureSnapshot(p1, p2)))
                return true;
        }
        return false;
    }

    private sealed record SingleAuditResult(bool Success, string Reason);

    private static SingleAuditResult AuditSingleAbility(
        GameDataRepository repo,
        CardTemplate tpl,
        AbilityDefinition ab,
        List<CardTemplate> allItems,
        CardTemplate smallClean,
        CardTemplate mediumClean,
        CardTemplate smallSkill)
    {
        var hero = tpl.Heroes.FirstOrDefault(h => h != "Common") ?? "Jules";
        var p1 = new PlayerState("p1", hero, hero, 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            Wins = 5,
            Losses = 1,
            CurrentHealth = 1000,
            Shield = 100,
            Burn = 15,
            Poison = 10,
            Rage = 25,
            Tempo = 35
        };
        p1.Attributes["Income"] = 20;
        p1.Attributes["Level"] = 8;
        p1.Attributes["Experience"] = 5;
        p1.Attributes["RageMax"] = 100;

        var p2 = new PlayerState("p2", "Enemy", "Vanessa", 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            CurrentHealth = 1500,
            Shield = 100,
            Burn = 25,
            Poison = 15,
            Rage = 20,
            Tempo = 20
        };

        var card = CreateRepresentativeCard(tpl);

        var collectedNodes = new List<RuleNode>();
        if (ab.Prerequisites != null) collectedNodes.AddRange(ab.Prerequisites);
        if (ab.Trigger != null) collectedNodes.Add(ab.Trigger);
        collectedNodes.Add(ab.Action!);

        // 先放待测卡本身（Hand 槽 3 留出 0..2 在左侧、3+w..9 在右侧）
        if (tpl.Type == "Skill")
            p1.Skills.Add(card);
        else
            p1.Hand.TryPlaceOrShift(card, 3);

        // 根据规则树智能装配搭档卡（左右邻位、LeftMost Medium、敌方卡、Stash 卡、SocketEffect、玩家参数）
        var (companions, enemyCards, sockets) = BuildRichSyntheticBoard(repo, p1, p2, card, collectedNodes, allItems, smallClean, mediumClean, smallSkill);

        // 先行触发一次前序累计能力（仅如目标动作需要自身非零 Custom_* 等衍生状态而当前为 0，且同卡前序能力能先给自身攒状态时；否则确保 Custom_* 若被 Value 引用且为 0 则预置正基数）
        PrepareSelfAndTargetDynamicAttributes(card, companions, enemyCards, ab);

        RuleScope scope = DetermineScopeForTrigger(ab.Trigger, ab.Action!, ab.WorksIn);
        if (scope == RuleScope.InCombat)
        {
            p1.Hand.Cards.ForEach(c => { if (c.CurrentAmmo == 0 && c.EffectiveAmmoMax > 0 && !companions.Contains(c)) c.CurrentAmmo = c.EffectiveAmmoMax; });
            if (card.EffectiveAmmoMax > 0 && card.CurrentAmmo == 0)
                card.CurrentAmmo = card.EffectiveAmmoMax;
        }

        // 若动作自身就是 Reload（含 Self 或任意目标），先清零目标卡当前弹药以便准确检测装弹收益
        if (WalkNodes([ab.Action!]).Any(n => n.Type == "TActionCardReload"))
        {
            if (card.EffectiveAmmoMax <= 0) card.PermanentAdd["AmmoMax"] = 3;
            card.CurrentAmmo = 0;
            foreach (var comp in companions)
            {
                if (comp.EffectiveAmmoMax <= 0) comp.PermanentAdd["AmmoMax"] = 3;
                comp.CurrentAmmo = 0;
            }
        }

        // 若动作成本消耗某属性（如 Gold / Tempo），保证满足并可清晰扣费
        EnsureActionCostSatisfied(p1, card, ab.Action!);

        // 触发前先算光环
        AuraEngine.RecalculateAuras(p1, p2, scope, p1.Day, p1.Hour, repo);

        // 光环重算可能同步提高动态费用（如 Whistling Glaive 的 Custom_1 随 Tempo 等值增长），需再保底覆盖实际费用引用
        if (ab.Action!.Cost?.Type == "TActionCostPlayerAttribute")
        {
            var costAttr = ab.Action.Cost.PlayerAttributeType ?? "Gold";
            var cValNode = ab.Action.Cost.GetValueNode(GameDatabaseLoader.JsonOptions);
            double dynCost = cValNode != null
                ? ValueAndConditionEvaluator.EvaluateValue(cValNode, new RuleExecutionContext(repo, scope, p1, p2, new Random(1)) { LocalPlayerId = p1.PlayerId }.WithCards(card, card, card), 0)
                : (ab.Action.Cost.GetFixedNumericValue() ?? 0);
            int needAmt = Math.Max(100, (int)Math.Ceiling(Math.Max(0, dynCost)) + 50);
            if (costAttr == "Tempo") p1.Tempo = Math.Max(p1.Tempo, needAmt);
            else if (costAttr == "Rage") p1.Rage = Math.Max(p1.Rage, needAmt);
            else if (costAttr == "Gold") p1.Gold = Math.Max(p1.Gold, needAmt);
            else p1.Attributes[costAttr] = Math.Max(p1.Attributes.GetValueOrDefault(costAttr, 0), needAmt);
        }

        string combatType = string.IsNullOrEmpty(ab.Trigger?.CombatType) ? "PVP" : ab.Trigger.CombatType;
        string combatOutcome = ab.Trigger?.CombatOutcome ?? "";
        if (string.IsNullOrEmpty(combatOutcome)
            && ab.Trigger?.ExtensionData != null
            && ab.Trigger.ExtensionData.TryGetValue("Outcome", out var ocEl)
            && ocEl.ValueKind == JsonValueKind.String)
        {
            combatOutcome = ocEl.GetString() ?? "";
        }
        if (string.IsNullOrEmpty(combatOutcome) && ab.Trigger?.Type is "TTriggerOnRunCompleted" or "TTriggerOnFightEnded")
            combatOutcome = "Victory";

        string playMode = "Unranked";
        var runPrereq = WalkNodes(ab.Prerequisites ?? []).FirstOrDefault(n => n.Type == "TPrerequisiteRun");
        if (runPrereq?.PlayModes is { Count: > 0 })
            playMode = runPrereq.PlayModes[0];

        string? currentEncounterId = null;
        var encPrereq = WalkNodes(ab.Prerequisites ?? []).FirstOrDefault(n => n.Type == "TRunConditionalCurrentEncounter");
        if (encPrereq != null)
        {
            var reqEncTags = WalkNodes([encPrereq]).Where(n => n.Type == "TCardConditionalTag" && n.Tags != null && n.IsNot != true)
                .SelectMany(n => n.Tags!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            currentEncounterId = repo.CardsById.Values
                .FirstOrDefault(c => reqEncTags.Count == 0 || reqEncTags.Any(tg => c.Tags.Contains(tg, StringComparer.OrdinalIgnoreCase)))?.Id;
        }

        var callbackHits = new List<string>();
        var events = new List<CombatEvent>();
        var rng = new Random(101);
        var execCtx = new RuleExecutionContext(repo, scope, p1, p2, rng)
        {
            LocalPlayerId = p1.PlayerId,
            CurrentDay = p1.Day,
            CurrentHour = p1.Hour,
            CurrentEncounterId = currentEncounterId,
            CombatType = combatType,
            PlayMode = playMode,
            SelectionCards = [companions[0], companions[1]],
            EmitCombatEvent = e => events.Add(e),
            OnDealCardsToSelection = (cards, mode) => callbackHits.Add($"DealSelection:{cards.Count}:{mode}"),
            OnRerollEncounter = mode => callbackHits.Add($"Reroll:{mode}"),
            OnExitSelection = () => callbackHits.Add("ExitSelection"),
            OnBeginSandstormRequested = () => callbackHits.Add("BeginSandstorm"),
            OnCardSpawned = (owner, c) => callbackHits.Add($"Spawned:{c.Template.Id}"),
            OnCardTransformed = (owner, o, n) => callbackHits.Add($"Transformed:{o.Template.Id}->{n.Template.Id}"),
            OnCardDestroyed = (owner, c) => callbackHits.Add($"Destroyed:{c.Template.Id}"),
            OnCardUpgraded = (owner, c) => callbackHits.Add($"Upgraded:{c.Template.Id}"),
            OnCardEnchanted = (owner, c) => callbackHits.Add($"Enchanted:{c.Template.Id}:{c.Enchantment}"),
            OnForceUseRequested = (t, s) => callbackHits.Add($"ForceUse:{t.Template.Id}"),
            OnActionCostSpent = (c, a, amt) => callbackHits.Add($"Cost:{a}:{amt}"),
            OnQuestUpdated = (c, qAttr, qPrev, qCur, qTgt) => callbackHits.Add($"Quest:{qAttr}:{qCur}"),
            OnQuestCompleted = c => callbackHits.Add($"QuestCompleted:{c.Template.Id}")
        };

        var (evName, srcCard, tgtCard, subjPlayer, attrChanged, prevVal, curVal) =
            ResolveTriggerInvocation(repo, ab.Trigger, card, p1, p2, companions, enemyCards, execCtx, allItems, ab.Prerequisites, ab.Action!);

        var checkCtx = execCtx.WithCards(card, srcCard, tgtCard);
        if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, checkCtx))
            return new SingleAuditResult(false, "无法满足 Prerequisites（智能布阵器未命中条件）");

        if (ab.Trigger != null
            && !AbilityExecutor.MatchesTrigger(ab.Trigger, evName, card, p1, srcCard, tgtCard, subjPlayer, attrChanged, prevVal, curVal, combatType, combatOutcome, checkCtx))
        {
            return new SingleAuditResult(false, $"MatchesTrigger 失败: event={evName}, attr={attrChanged}, prev={prevVal}, cur={curVal}");
        }

        // 若是自身具有成长/附魔解锁机制的纯战斗输出能力（初值 0，由其它技能/光环累加），预置测试用基础正值以验证输出链路
        SeedZeroBaseScalingPrimaryAttrIfNeeded(card, ab);

        // 触发动作并刷新光环
        var snapBefore = CaptureSnapshot(p1, p2);
        var runCtx = execCtx.WithCards(card, srcCard, tgtCard);
        bool execRet = AbilityExecutor.ExecuteAction(ab.Action!, card, runCtx);
        if (execRet)
            AuraEngine.RecalculateAuras(p1, p2, scope, p1.Day, p1.Hour, repo);

        if (!execRet)
            return new SingleAuditResult(false, $"ExecuteAction 返回 false (Cost={ab.Action?.Cost?.Type})");

        var snapAfter = CaptureSnapshot(p1, p2);
        if (HasAnySnapshotDelta(snapBefore, snapAfter) || events.Count > 0 || callbackHits.Count > 0)
            return new SingleAuditResult(true, "OK");

        // 特殊卡牌内在互斥机制校验（如 Nullfrost Altar Ab[0] 试图冻结自己手牌物品，而其自身 Aura[3] 为全手牌赋予 PercentFreezeReduction=+100% 免疫）
        if (ab.Action!.Type == "TActionCardFreeze"
            && srcCard != null
            && !ReferenceEquals(srcCard, card)
            && card.ActiveAuras.Any(au => string.Equals(au.Action?.AttributeType, "PercentFreezeReduction", StringComparison.OrdinalIgnoreCase))
            && TargetResolver.ResolveCards(ab.Action.Target, runCtx).Any(t => t.GetAttribute("PercentFreezeReduction") >= 100))
        {
            return new SingleAuditResult(true, "OK");
        }

        return new SingleAuditResult(false, $"Silent No-Op: 执行成功但面板属性/容器/状态/事件全零变化 (Target={ab.Action?.Target?.Type}/{ab.Action?.Target?.TargetSection})");
    }

    private static SingleAuditResult AuditSingleAura(
        GameDataRepository repo,
        CardTemplate tpl,
        AuraDefinition au,
        List<CardTemplate> allItems,
        CardTemplate smallClean,
        CardTemplate mediumClean,
        CardTemplate smallSkill)
    {
        var hero = tpl.Heroes.FirstOrDefault(h => h != "Common") ?? "Jules";
        var p1 = new PlayerState("p1", hero, hero, 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            Wins = 5,
            Losses = 1,
            CurrentHealth = 1000,
            Shield = 100,
            Rage = 25,
            Tempo = 35
        };
        p1.Attributes["Income"] = 20;
        p1.Attributes["Level"] = 8;
        p1.Attributes["HealthRegen"] = 10;

        var p2 = new PlayerState("p2", "Enemy", "Vanessa", 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            CurrentHealth = 1500,
            Shield = 100,
            Tempo = 20
        };

        var card = CreateRepresentativeCard(tpl);

        var nodes = new List<RuleNode>();
        if (au.Prerequisites != null) nodes.AddRange(au.Prerequisites);
        nodes.Add(au.Action!);

        if (tpl.Type == "Skill")
            p1.Skills.Add(card);
        else
            p1.Hand.TryPlaceOrShift(card, 3);

        var (companions, enemyCards, _) = BuildRichSyntheticBoard(repo, p1, p2, card, nodes, allItems, smallClean, mediumClean, smallSkill);
        PrepareSelfAndTargetDynamicAttributes(card, companions, enemyCards, null, au);

        // 先把卡标为 Disabled（不参与光环）做基准快照，再启用后算光环差值
        card.IsDisabled = true;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        var snapWithout = CaptureSnapshot(p1, p2);

        card.IsDisabled = false;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        var snapWith = CaptureSnapshot(p1, p2);

        if (HasAnySnapshotDelta(snapWithout, snapWith, ignoreDisabledFlagOnCard: card))
            return new SingleAuditResult(true, "OK");

        // 也尝试 OutOfCombat 光环域
        card.IsDisabled = true;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, p1.Day, p1.Hour, repo);
        snapWithout = CaptureSnapshot(p1, p2);

        card.IsDisabled = false;
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.OutOfCombat, p1.Day, p1.Hour, repo);
        snapWith = CaptureSnapshot(p1, p2);

        if (HasAnySnapshotDelta(snapWithout, snapWith, ignoreDisabledFlagOnCard: card))
            return new SingleAuditResult(true, "OK");

        return new SingleAuditResult(false, $"Silent No-Op Aura: {au.Action?.Type} attr={au.Action?.AttributeType} target={au.Action?.Target?.Type}/{au.Action?.Target?.TargetSection} 未造成属性/标签变化");
    }

    private static SingleAuditResult AuditSingleQuest(
        GameDataRepository repo,
        CardTemplate tpl,
        QuestDefinition q,
        QuestEntry entry,
        List<CardTemplate> allItems,
        CardTemplate smallClean,
        CardTemplate mediumClean,
        CardTemplate smallSkill)
    {
        var hero = tpl.Heroes.FirstOrDefault(h => h != "Common") ?? "Jules";
        var p1 = new PlayerState("p1", hero, hero, 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            CurrentHealth = 1000,
            Shield = 100,
            Rage = 25,
            Tempo = 35
        };
        p1.Attributes["Income"] = 20;
        p1.Attributes["Level"] = 8;

        var p2 = new PlayerState("p2", "Enemy", "Vanessa", 2000, 20, 100, 10)
        {
            Day = 5,
            Hour = 3,
            CurrentHealth = 1500
        };

        var card = CreateRepresentativeCard(tpl);
        if (tpl.Type == "Skill") p1.Skills.Add(card);
        else p1.Hand.TryPlaceOrShift(card, 3);

        var nodes = new List<RuleNode>();
        if (q.Prerequisites != null) nodes.AddRange(q.Prerequisites);
        if (entry.Prerequisites != null) nodes.AddRange(entry.Prerequisites);
        if (entry.Trigger != null) nodes.Add(entry.Trigger);
        if (q.Repeatable == null && q.Entries.Count > 1)
        {
            var ownTags = WalkNodes(nodes)
                .Where(n => n.Type == "TCardConditionalTag" && n.Tags != null && n.IsNot != true && !string.Equals(n.Operator, "None", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.Tags!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var siblingOnlyTags = q.Entries
                .Where(sib => !ReferenceEquals(sib, entry) && sib.Trigger != null)
                .SelectMany(sib => WalkNodes([sib.Trigger!, .. (sib.Prerequisites ?? [])]))
                .Where(n => n.Type == "TCardConditionalTag" && n.Tags != null && n.IsNot != true && !string.Equals(n.Operator, "None", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.Tags!)
                .Where(t => !ownTags.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (siblingOnlyTags.Count > 0)
                nodes.Add(new RuleNode { Type = "TCardConditionalTag", IsNot = true, Tags = siblingOnlyTags });
        }

        var (companions, enemyCards, _) = BuildRichSyntheticBoard(repo, p1, p2, card, nodes, allItems, smallClean, mediumClean, smallSkill);
        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);

        string qCombatType = string.IsNullOrEmpty(entry.Trigger?.CombatType) ? "PVP" : entry.Trigger.CombatType;
        string qCombatOutcome = entry.Trigger?.CombatOutcome ?? (entry.Trigger?.Type is "TTriggerOnRunCompleted" or "TTriggerOnFightEnded" ? "Victory" : "");

        var snapBefore = CaptureSnapshot(p1, p2);
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, p2, new Random(77))
        {
            LocalPlayerId = p1.PlayerId,
            CurrentDay = p1.Day,
            CurrentHour = p1.Hour,
            CombatType = qCombatType
        };

        var (evName, srcCard, tgtCard, subjPlayer, attrChanged, prevVal, curVal) =
            ResolveTriggerInvocation(repo, entry.Trigger, card, p1, p2, companions, enemyCards, ctx, allItems, entry.Prerequisites ?? q.Prerequisites, null);

        int maxSteps = Math.Clamp((int)Math.Ceiling(Math.Max(1, entry.Target)) + 5, 1, 300);
        for (int step = 0; step < maxSteps && !card.CompletedQuests.Contains(entry.AttributeType); step++)
        {
            double prevProg = card.QuestProgress.GetValueOrDefault(entry.AttributeType, 0);
            AbilityExecutor.TryAdvanceQuests(evName, p1, srcCard, tgtCard, subjPlayer, attrChanged, prevVal, curVal, qCombatType, qCombatOutcome, ctx);
            if (Math.Abs(card.QuestProgress.GetValueOrDefault(entry.AttributeType, 0) - prevProg) < 1e-9 && !card.CompletedQuests.Contains(entry.AttributeType))
                break;
        }

        if (!card.CompletedQuests.Contains(entry.AttributeType))
            return new SingleAuditResult(false, $"Quest {entry.AttributeType} 未能完成 (progress={card.QuestProgress.GetValueOrDefault(entry.AttributeType, 0)}/{entry.Target}, trigger={evName})");

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
        var snapAfter = CaptureSnapshot(p1, p2);
        if (HasAnySnapshotDelta(snapBefore, snapAfter)
            || card.CompletedQuests.Contains(entry.AttributeType))
            return new SingleAuditResult(true, "OK");

        return new SingleAuditResult(false, $"Quest {entry.AttributeType} 完成后无任何状态/属性/标签/效果变化");
    }

    // -------------------------------------------------------------------------
    // 三、智能规则 AST 需求提取与富面板构建辅助
    // -------------------------------------------------------------------------

    private static IEnumerable<RuleNode> WalkNodes(IEnumerable<RuleNode> roots)
    {
        var stack = new Stack<RuleNode>();
        foreach (var r in roots) if (r != null) stack.Push(r);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            if (cur.Target != null) stack.Push(cur.Target);
            if (cur.TargetPlayer != null) stack.Push(cur.TargetPlayer);
            if (cur.TargetCount != null) stack.Push(cur.TargetCount);
            if (cur.Source != null) stack.Push(cur.Source);
            if (cur.Subject != null) stack.Push(cur.Subject);
            if (cur.SubjectOther != null) stack.Push(cur.SubjectOther);
            if (cur.ReferenceValue != null) stack.Push(cur.ReferenceValue);
            if (cur.SpawnContext != null) stack.Push(cur.SpawnContext);
            if (cur.Cost != null) stack.Push(cur.Cost);
            if (cur.Limit != null) stack.Push(cur.Limit);
            var valNode = cur.GetValueNode(GameDatabaseLoader.JsonOptions);
            if (valNode != null) stack.Push(valNode);
            var singleCond = cur.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (singleCond != null) stack.Push(singleCond);
            var multiCond = cur.GetConditionList(GameDatabaseLoader.JsonOptions);
            if (multiCond != null) foreach (var mc in multiCond) stack.Push(mc);
            if (cur.Actions != null) foreach (var a in cur.Actions) stack.Push(a);
            if (cur.Triggers != null) foreach (var t in cur.Triggers) stack.Push(t);
            if (cur.Groups != null) foreach (var g in cur.Groups) stack.Push(g);
            if (cur.Filters != null) foreach (var f in cur.Filters) stack.Push(f);
            if (cur.Prerequisites != null) foreach (var p in cur.Prerequisites) stack.Push(p);
        }
    }

    private static (List<CardInstance> Companions, List<CardInstance> EnemyCards, List<CardInstance> Sockets)
        BuildRichSyntheticBoard(
            GameDataRepository repo,
            PlayerState p1,
            PlayerState p2,
            CardInstance targetCard,
            List<RuleNode> ruleRoots,
            List<CardTemplate> allItems,
            CardTemplate smallClean,
            CardTemplate mediumClean,
            CardTemplate smallSkill)
    {
        var allNodes = WalkNodes(ruleRoots).ToList();

        // 提取条件中指定 ID / Tag / HiddenTag / 玩家属性需求，严格区分正向包含与否定排除（Operator == None / IsNot）
        var requiredCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positiveTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positiveHiddenTags = new List<string>();
        var excludedHiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredEnchants = new List<string>();
        var excludedEnchants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool requiresCanCrit = false;
        bool needsTinyZero = false;
        bool needsDisabled = false;
        bool needsDisabledLeft = false;
        bool needsZeroAmmo = false;
        bool hasSpawnCards = false;
        bool needsLargeSelfHand = false;
        bool needsMediumOppHand = false;
        bool needsMediumLeftCard = false;
        bool needsEnchantableTemplates = false;
        bool wantsEnchantedSelfNeighbor = false;
        bool wantsEnchantedOppHand = false;
        int? exactOpponentHandCount = null;

        foreach (var n in allNodes)
        {
            if (n.Type == "TCardConditionalId" && !string.IsNullOrEmpty(n.Id))
            {
                if (n.IsNot == true) excludedCardIds.Add(n.Id);
                else requiredCardIds.Add(n.Id);
            }
            if (n.Type == "TCardConditionalTag" && n.Tags != null)
            {
                bool isNeg = n.IsNot == true || string.Equals(n.Operator, "None", StringComparison.OrdinalIgnoreCase);
                foreach (var t in n.Tags)
                {
                    if (isNeg) excludedTags.Add(t);
                    else positiveTags.Add(t);
                }
            }
            if (n.Type == "TCardConditionalHiddenTag" && n.Tags != null)
            {
                bool isNeg = n.IsNot == true || string.Equals(n.Operator, "None", StringComparison.OrdinalIgnoreCase);
                foreach (var ht in n.Tags)
                {
                    if (isNeg) excludedHiddenTags.Add(ht);
                    else if (!positiveHiddenTags.Contains(ht, StringComparer.OrdinalIgnoreCase)) positiveHiddenTags.Add(ht);
                }
            }
            if (n.Type == "TCardConditionalCanCrit" && n.IsNot != true)
                requiresCanCrit = true;
            if (n.Type == "TCardConditionalHasEnchantment")
            {
                if (!string.IsNullOrEmpty(n.Enchantment))
                {
                    if (n.IsNot == true) excludedEnchants.Add(n.Enchantment);
                    else requiredEnchants.Add(n.Enchantment);
                }
                else if (n.IsNot == true)
                {
                    // Enchantment == null && IsNot == true 表示“必须已附魔”
                    wantsEnchantedSelfNeighbor = true;
                    wantsEnchantedOppHand = true;
                }
            }
            if (n.Type is "TActionCardEnchant" or "TActionCardEnchantRandom")
            {
                needsEnchantableTemplates = true;
                if (n.Type == "TActionCardEnchant" && !string.IsNullOrEmpty(n.Enchantment))
                    excludedEnchants.Add(n.Enchantment);
            }
            if (n.Type == "TCardConditionalSize" && n.Sizes != null && n.IsNot != true)
            {
                if (n.Sizes.Contains("Large", StringComparer.OrdinalIgnoreCase)) needsLargeSelfHand = true;
                if (n.Sizes.Contains("Medium", StringComparer.OrdinalIgnoreCase)) needsMediumOppHand = true;
            }
            if (n.Type == "TTargetCardPositional"
                && n.TargetMode == "LeftCard"
                && WalkNodes([n]).Any(x => x.Type == "TCardConditionalSize" && x.Sizes != null && x.Sizes.Contains("Medium", StringComparer.OrdinalIgnoreCase)))
            {
                needsMediumLeftCard = true;
            }
            if (n.Type == "TCardConditionalAttribute" && n.Attribute != null)
            {
                if (n.Attribute.Equals("Tiny", StringComparison.OrdinalIgnoreCase)) needsTinyZero = true;
            }
            if (n.Type == "TPrerequisitePlayer")
            {
                bool defaultOpp = (n.Subject?.TargetMode ?? n.TargetPlayer?.TargetMode ?? string.Empty).Contains("Opponent", StringComparison.OrdinalIgnoreCase);
                var pConds = WalkNodes([n]).Where(x => x.Type == "TPlayerConditionalAttribute" && !string.IsNullOrEmpty(x.Attribute)).ToList();
                if (pConds.Count == 0 && !string.IsNullOrEmpty(n.AttributeType))
                    pConds.Add(new RuleNode { Attribute = n.AttributeType, ComparisonOperator = n.ComparisonOperator });

                foreach (var pc in pConds)
                {
                    var pAttr = pc.Attribute!;
                    var ps = defaultOpp ? p2 : p1;
                    bool wantLow = pc.ComparisonOperator is "LessThan" or "LessThanOrEqual" or "Equal";
                    if (pc.ComparisonOperator == "Equal")
                    {
                        var cmpNum = pc.GetFixedComparisonValue() ?? pc.ComparisonAmount ?? 0;
                        if (Math.Abs(cmpNum) < 1e-6)
                        {
                            if (pAttr == "Gold") ps.Gold = 0;
                            else if (pAttr == "Rage") ps.Rage = 0;
                            else if (pAttr == "Tempo") ps.Tempo = 0;
                            else ps.Attributes[pAttr] = 0;
                            continue;
                        }
                    }
                    if (pAttr == "Health") ps.CurrentHealth = wantLow ? 350 : 1800;
                    else if (pAttr == "Rage") ps.Rage = wantLow ? 5 : Math.Max(ps.Rage, 50);
                    else if (pAttr == "Tempo") ps.Tempo = wantLow ? 5 : Math.Max(ps.Tempo, 50);
                    else if (pAttr == "Gold") ps.Gold = wantLow ? 5 : Math.Max(ps.Gold, 200);
                    else if (pAttr == "Burn") ps.Burn = wantLow ? 2 : Math.Max(ps.Burn, 25);
                    else if (pAttr == "Poison") ps.Poison = wantLow ? 2 : Math.Max(ps.Poison, 15);
                    else if (pAttr == "Shield") ps.Shield = wantLow ? 10 : Math.Max(ps.Shield, 150);
                    else ps.Attributes[pAttr] = wantLow ? 1 : Math.Max(ps.Attributes.GetValueOrDefault(pAttr, 0), 50);
                }
            }
            if (n.Type == "TPrerequisiteCardCount")
            {
                if (n.Subject?.TargetSection == "OpponentHand"
                    && string.Equals(n.Comparison, "Equal", StringComparison.OrdinalIgnoreCase)
                    && n.Amount.HasValue)
                {
                    exactOpponentHandCount = Math.Clamp((int)n.Amount.Value, 0, 4);
                }
            }
            if (n.Type == "TActionGameSpawnCards") hasSpawnCards = true;
            if (n.Type == "TActionCardRepair")
            {
                needsDisabled = true;
                if (n.Target?.Type == "TTargetCardPositional" && n.Target.TargetMode is "LeftCard" or "Neighbor")
                    needsDisabledLeft = true;
            }
            if (n.Type == "TActionCardReload") needsZeroAmmo = true;
            if (n.Type == "TActionCardTiny") needsTinyZero = true;
        }

        // 确保所需通用标签剔除掉否定条件里点名排除的 Tag
        var companionDefaultTags = CommonAuditTags
            .Where(t => !excludedTags.Contains(t))
            .Concat(positiveTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 先看是否有音乐音符 SocketEffect 需求（或 ID 指向 SocketEffect）
        var sockets = new List<CardInstance>();
        bool needsMusicNoteSocket = allNodes.Any(n =>
            n.MusicNote.HasValue
            || (n.HiddenTags != null && n.HiddenTags.Any(t => t.Contains("Note", StringComparison.OrdinalIgnoreCase)))
            || (n.Id != null && repo.CardsById.TryGetValue(n.Id, out var st) && st.Type == "SocketEffect"));
        if (needsMusicNoteSocket || p1.Hero == "Hero8")
        {
            var noteTpl = repo.CardsById.Values.FirstOrDefault(t => string.Equals(t.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase))
                          ?? mediumClean;
            if (noteTpl.Type == "SocketEffect")
            {
                var sock = new CardInstance(noteTpl) { StartSlot = 3 };
                p1.SocketEffects.Add(sock);
                sockets.Add(sock);
            }
        }

        // 查找符合 HiddenTag / CanCrit / Size 且不触犯 Exclusion 的安全搭档模板
        static bool HasCardCooldown(CardTemplate t) =>
            (t.Attributes?.GetValueOrDefault("CooldownMax", 0) ?? 0) > 0
            || (t.Tiers?.Values.Any(td => td.Attributes.GetValueOrDefault("CooldownMax", 0) > 0) == true);

        CardTemplate PickTemplate(CardSize size, string? preferredHidden = null)
        {
            if (requiredCardIds.Count > 0)
            {
                foreach (var rid in requiredCardIds)
                {
                    if (repo.CardsById.TryGetValue(rid, out var rt) && rt.Type == "Item" && !excludedCardIds.Contains(rid))
                        return rt;
                }
            }
            if (!string.IsNullOrEmpty(preferredHidden))
            {
                var hiddenMatch = allItems.FirstOrDefault(t =>
                    t.CardSize == size
                    && !excludedCardIds.Contains(t.Id)
                    && t.HiddenTags.Contains(preferredHidden, StringComparer.OrdinalIgnoreCase)
                    && !excludedHiddenTags.Any(eh => t.HiddenTags.Contains(eh, StringComparer.OrdinalIgnoreCase)));
                if (hiddenMatch != null) return hiddenMatch;
            }
            if (requiresCanCrit)
            {
                var critMatch = allItems.FirstOrDefault(t =>
                    t.CardSize == size
                    && !excludedCardIds.Contains(t.Id)
                    && HasCardCooldown(t)
                    && (t.HiddenTags.Contains("CanCrit", StringComparer.OrdinalIgnoreCase) || t.Tags.Contains("Weapon", StringComparer.OrdinalIgnoreCase)));
                if (critMatch != null) return critMatch;
            }
            return allItems.FirstOrDefault(t =>
                       t.CardSize == size
                       && !excludedCardIds.Contains(t.Id)
                       && t.Auras.Count == 0
                       && HasCardCooldown(t)
                       && (!needsEnchantableTemplates || (t.Enchantments != null && t.Enchantments.Count > 0))
                       && !excludedTags.Any(et => t.Tags.Contains(et, StringComparer.OrdinalIgnoreCase))
                       && !excludedHiddenTags.Any(eh => t.HiddenTags.Contains(eh, StringComparer.OrdinalIgnoreCase)))
                   ?? allItems.FirstOrDefault(t =>
                       t.CardSize == size
                       && !excludedCardIds.Contains(t.Id)
                       && HasCardCooldown(t)
                       && (!needsEnchantableTemplates || (t.Enchantments != null && t.Enchantments.Count > 0))
                       && !excludedTags.Any(et => t.Tags.Contains(et, StringComparer.OrdinalIgnoreCase))
                       && !excludedHiddenTags.Any(eh => t.HiddenTags.Contains(eh, StringComparer.OrdinalIgnoreCase)))
                   ?? allItems.FirstOrDefault(t => t.CardSize == size && !excludedCardIds.Contains(t.Id))
                   ?? (size == CardSize.Medium ? mediumClean : smallClean);
        }

        var nonConflictingHidden = positiveHiddenTags.Where(h => !excludedHiddenTags.Contains(h)).ToList();
        var conflictingHidden = positiveHiddenTags.Where(h => excludedHiddenTags.Contains(h)).ToList();
        string? hLeftMost = nonConflictingHidden.Count > 0 ? nonConflictingHidden[0] : (conflictingHidden.Count > 0 ? conflictingHidden[0] : null);
        string? hLeft = nonConflictingHidden.Count > 1 ? nonConflictingHidden[1] : (nonConflictingHidden.Count > 0 ? nonConflictingHidden[0] : null);
        string? hRight = conflictingHidden.Count > 0 ? conflictingHidden[0] : (nonConflictingHidden.Count > 2 ? nonConflictingHidden[2] : hLeftMost);

        var leftAnchorTpl = PickTemplate(CardSize.Medium, hLeftMost);
        var leftNeighborTpl = PickTemplate(needsMediumLeftCard ? CardSize.Medium : CardSize.Small, hLeft);
        // 当某隐藏标签同时出现在正向动作目标和否定触发过滤中（如 Anything to Win 需要净空卡片触发 + 有 Poison/Burn 卡片被充能），让 rightNeighbor 专属匹配冲突隐藏标签
        var rightNeighborTpl = conflictingHidden.Count > 0
            ? (allItems.FirstOrDefault(t => t.CardSize == CardSize.Small && !excludedCardIds.Contains(t.Id) && conflictingHidden.Any(ch => t.HiddenTags.Contains(ch, StringComparer.OrdinalIgnoreCase)))
               ?? PickTemplate(CardSize.Small, hRight))
            : PickTemplate(CardSize.Small, hRight);

        bool wantsLifesteal = allNodes.Any(n => n.Type == "TCardConditionalAttribute" && string.Equals(n.Attribute, "Lifesteal", StringComparison.OrdinalIgnoreCase));
        bool wantsDiamond = allNodes.Any(n => n.Type == "TCardConditionalTier" && n.Tiers != null && n.Tiers.Contains("Diamond", StringComparer.OrdinalIgnoreCase) && n.IsNot != true);
        bool wantsGold = allNodes.Any(n => n.Type == "TCardConditionalTier" && n.Tiers != null && n.Tiers.Contains("Gold", StringComparer.OrdinalIgnoreCase) && n.IsNot != true);
        string? enchantToApply = requiredEnchants.FirstOrDefault(e => !excludedEnchants.Contains(e));

        void PopulateRichAttributes(CardInstance c, bool asHeatedChilled, bool asFlying, bool withFreezeSlow, bool asDisabled, bool zeroAmmo, bool allowEnchant, bool applyCommonTags)
        {
            c.PermanentAdd["CooldownMax"] = Math.Max(0, 5000 - c.BaseAttributes.GetValueOrDefault("CooldownMax", 0));
            c.PermanentAdd["DamageAmount"] = Math.Max(20, c.BaseAttributes.GetValueOrDefault("DamageAmount", 0));
            c.PermanentAdd["ShieldApplyAmount"] = Math.Max(20, c.BaseAttributes.GetValueOrDefault("ShieldApplyAmount", 0));
            c.PermanentAdd["BurnApplyAmount"] = Math.Max(10, c.BaseAttributes.GetValueOrDefault("BurnApplyAmount", 0));
            c.PermanentAdd["PoisonApplyAmount"] = Math.Max(10, c.BaseAttributes.GetValueOrDefault("PoisonApplyAmount", 0));
            c.PermanentAdd["HealAmount"] = Math.Max(20, c.BaseAttributes.GetValueOrDefault("HealAmount", 0));
            c.PermanentAdd["RegenApplyAmount"] = Math.Max(10, c.BaseAttributes.GetValueOrDefault("RegenApplyAmount", 0));
            c.PermanentAdd["HasteAmount"] = Math.Max(1000, c.BaseAttributes.GetValueOrDefault("HasteAmount", 0));
            c.PermanentAdd["SlowAmount"] = Math.Max(1000, c.BaseAttributes.GetValueOrDefault("SlowAmount", 0));
            c.PermanentAdd["FreezeAmount"] = Math.Max(1000, c.BaseAttributes.GetValueOrDefault("FreezeAmount", 0));
            c.PermanentAdd["ChargeAmount"] = Math.Max(1000, c.BaseAttributes.GetValueOrDefault("ChargeAmount", 0));
            c.PermanentAdd["CritChance"] = Math.Max(25, c.BaseAttributes.GetValueOrDefault("CritChance", 0));
            if (wantsLifesteal) c.PermanentAdd["Lifesteal"] = Math.Max(10, c.BaseAttributes.GetValueOrDefault("Lifesteal", 0));
            c.PermanentAdd["SellPrice"] = Math.Max(10, c.BaseAttributes.GetValueOrDefault("SellPrice", 0));
            c.PermanentAdd["AmmoMax"] = Math.Max(3, c.BaseAttributes.GetValueOrDefault("AmmoMax", 0));
            c.CurrentAmmo = zeroAmmo ? 0 : Math.Max(1, c.EffectiveAmmoMax);
            if (wantsDiamond) c.SetTier(CardTier.Diamond);
            else if (wantsGold) c.SetTier(CardTier.Gold);
            else if (c.Tier == CardTier.Diamond) c.SetTier(CardTier.Silver);

            if (applyCommonTags)
            {
                foreach (var rt in companionDefaultTags) c.ExtraTags.Add(rt);
            }
            else
            {
                foreach (var pt in positiveTags)
                {
                    if (!excludedTags.Contains(pt)) c.ExtraTags.Add(pt);
                }
            }
            if (asHeatedChilled)
            {
                c.Heated = true;
                c.Chilled = true;
            }
            if (asFlying) c.IsFlying = true;
            if (withFreezeSlow)
            {
                c.FreezeMs = 2000;
                c.SlowMs = 2000;
                c.HasteMs = 1000;
            }
            c.IsDisabled = asDisabled;
            if (allowEnchant && string.IsNullOrEmpty(c.Enchantment))
            {
                string? fallbackEnc = !excludedEnchants.Contains("Heavy") ? "Heavy" : (!excludedEnchants.Contains("Fiery") ? "Fiery" : null);
                var enc = enchantToApply ?? fallbackEnc;
                if (!string.IsNullOrEmpty(enc)) c.ApplyEnchantment(enc);
            }
        }

        var companions = new List<CardInstance>();

        // 槽 0..1 / 槽 1..2: 当规则要求紧邻 LeftCard 必须为 Medium（如 Green Screen / Corkscrew）时，槽 0 放 Small，槽 1..2 放 Medium 左邻，避免推挤被测卡
        CardInstance leftMost;
        CardInstance leftNeighbor;
        if (needsMediumLeftCard)
        {
            leftMost = new CardInstance(PickTemplate(CardSize.Small, hLeftMost));
            PopulateRichAttributes(leftMost, asHeatedChilled: true, asFlying: true, withFreezeSlow: true, asDisabled: false, zeroAmmo: needsZeroAmmo, allowEnchant: true, applyCommonTags: true);
            p1.Hand.TryPlaceOrShift(leftMost, 0);
            companions.Add(leftMost);

            leftNeighbor = new CardInstance(leftNeighborTpl);
            if (leftNeighbor.Template.CardSize != CardSize.Medium)
                leftNeighbor = new CardInstance(mediumClean);
            PopulateRichAttributes(leftNeighbor, asHeatedChilled: true, asFlying: true, withFreezeSlow: true, asDisabled: needsDisabledLeft, zeroAmmo: true, allowEnchant: enchantToApply != null || wantsEnchantedSelfNeighbor, applyCommonTags: true);
            if (needsTinyZero) leftNeighbor.ModifyAttribute("Tiny", "Set", 0, DurationTarget.Permanent);
            p1.Hand.TryPlaceOrShift(leftNeighbor, 1);
            companions.Add(leftNeighbor);
        }
        else
        {
            // 槽 0..1: Medium 最左锚定卡（默认附魔非目标附魔的 Heavy，满足附魔条件同时留空被 Fiery 等附魔覆盖）
            leftMost = new CardInstance(leftAnchorTpl);
            if (leftMost.Template.CardSize != CardSize.Medium && positiveHiddenTags.Count == 0 && !wantsLifesteal)
                leftMost = new CardInstance(mediumClean);
            PopulateRichAttributes(leftMost, asHeatedChilled: true, asFlying: true, withFreezeSlow: true, asDisabled: false, zeroAmmo: needsZeroAmmo, allowEnchant: true, applyCommonTags: true);
            p1.Hand.TryPlaceOrShift(leftMost, 0);
            companions.Add(leftMost);

            // 槽 2: 紧邻待测卡左侧的 Small 搭档
            leftNeighbor = new CardInstance(leftNeighborTpl);
            PopulateRichAttributes(leftNeighbor, asHeatedChilled: true, asFlying: true, withFreezeSlow: true, asDisabled: needsDisabledLeft, zeroAmmo: true, allowEnchant: enchantToApply != null || wantsEnchantedSelfNeighbor, applyCommonTags: true);
            if (needsTinyZero) leftNeighbor.ModifyAttribute("Tiny", "Set", 0, DurationTarget.Permanent);
            p1.Hand.TryPlaceOrShift(leftNeighbor, 2);
            companions.Add(leftNeighbor);
        }

        // 槽 3+w（被测物为 Skill 不占槽位时从 3 开始保证紧贴槽 2 的 leftNeighbor）: 右侧伴侣（净空排除标签与默认未附魔）
        int rightSlot = targetCard.Template.Type == "Item" && targetCard.StartSlot >= 0
            ? targetCard.StartSlot + targetCard.SlotWidth
            : 3;
        var rightNeighbor = new CardInstance(rightNeighborTpl);
        PopulateRichAttributes(rightNeighbor, asHeatedChilled: false, asFlying: false, withFreezeSlow: true, asDisabled: needsDisabled, zeroAmmo: needsZeroAmmo || true, allowEnchant: enchantToApply != null, applyCommonTags: false);
        if (!wantsDiamond && !wantsGold && (rightNeighbor.Template.TierMapFallback() || rightNeighbor.Tier >= CardTier.Diamond))
            rightNeighbor.SetTier(CardTier.Bronze);
        p1.Hand.TryPlaceOrShift(rightNeighbor, rightSlot);
        companions.Add(rightNeighbor);

        // 若规则需要场上存在 Large 物品（如 Juggler / Captain's Charge / Hypnotic Drain / Tractor Beam），在手牌放入一张 Large 物品
        if (needsLargeSelfHand && p1.Hand.Cards.All(x => x.EffectiveSize != CardSize.Large))
        {
            var largeCard = new CardInstance(PickTemplate(CardSize.Large, hRight));
            PopulateRichAttributes(largeCard, asHeatedChilled: false, asFlying: false, withFreezeSlow: false, asDisabled: false, zeroAmmo: false, allowEnchant: false, applyCommonTags: true);
            if (!p1.Hand.TryAddAuto(largeCard))
            {
                // 手牌太满时把 leftMost 撤回 Stash 腾出 2 格以便 Large 必定能落入 SelfHand
                p1.Hand.Remove(leftMost);
                p1.Stash.TryAddAuto(leftMost);
                p1.Hand.TryAddAuto(largeCard);
            }
            companions.Add(largeCard);
        }

        // 储物箱 Stash 里也放一张可修/可升级卡（同样只带正向标签，保持空槽给 Hand SpawnCards）
        var stashItem = new CardInstance(PickTemplate(CardSize.Small, hRight));
        PopulateRichAttributes(stashItem, asHeatedChilled: true, asFlying: false, withFreezeSlow: true, asDisabled: needsDisabled, zeroAmmo: true, allowEnchant: false, applyCommonTags: false);
        p1.Stash.TryPlaceOrShift(stashItem, 0);
        companions.Add(stashItem);

        // 若条件点名需要多个特定卡数量（跳过 Self 与 Equal <= 1），额外补齐进 Hand/Stash
        foreach (var prereq in allNodes.Where(n => n.Type == "TPrerequisiteCardCount"))
        {
            if (prereq.Subject?.Type == "TTargetCardSelf") continue;
            if (prereq.Subject?.TargetSection == "OpponentHand") continue;
            if (prereq.Comparison is "LessThan" or "LessThanOrEqual" or "Equal" && (prereq.Amount ?? 1) <= 1) continue;
            int need = Math.Clamp((int)(prereq.Amount ?? 1), 1, 5);
            for (int k = 1; k < need; k++)
            {
                var extra = new CardInstance(leftNeighborTpl);
                PopulateRichAttributes(extra, asHeatedChilled: true, asFlying: true, withFreezeSlow: true, asDisabled: false, zeroAmmo: false, allowEnchant: enchantToApply != null, applyCommonTags: true);
                if (hasSpawnCards || !p1.Hand.TryAddAuto(extra))
                    p1.Stash.TryAddAuto(extra);
                companions.Add(extra);
            }
        }

        // 精准裁剪 SelfHand Equal N 前置条件（如 Lone Charge Friend==1 / Secret Ingredient Food==1 / Machete CooldownMax>0 == 1）
        foreach (var prereq in allNodes.Where(n => n.Type == "TPrerequisiteCardCount" && string.Equals(n.Comparison, "Equal", StringComparison.OrdinalIgnoreCase) && n.Amount.HasValue))
        {
            if (prereq.Subject?.Type != "TTargetCardSection" || prereq.Subject.TargetSection != "SelfHand") continue;
            int exactWant = Math.Max(0, (int)Math.Round(prereq.Amount!.Value));
            var cond = prereq.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            var probe = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(1)) { LocalPlayerId = p1.PlayerId }.WithCards(targetCard);
            var matching = p1.Hand.Cards
                .Where(c => !(prereq.Subject.ExcludeSelf == true && ReferenceEquals(c, targetCard))
                         && (cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, c, p1, probe)))
                .ToList();
            if (matching.Count > exactWant)
            {
                var reqTagsInCond = WalkNodes([prereq.Subject]).Where(x => x.Type == "TCardConditionalTag" && x.Tags != null && x.IsNot != true).SelectMany(x => x.Tags!).ToList();
                for (int idx = matching.Count - 1; idx >= 0 && matching.Count > exactWant; idx--)
                {
                    var cand = matching[idx];
                    if (ReferenceEquals(cand, targetCard)) continue;
                    foreach (var tg in reqTagsInCond) cand.ExtraTags.Remove(tg);
                    if (cond != null && !ValueAndConditionEvaluator.EvaluateCardCondition(cond, cand, p1, probe))
                    {
                        matching.RemoveAt(idx);
                        continue;
                    }
                    p1.Hand.Remove(cand);
                    p1.Stash.TryAddAuto(cand);
                    matching.RemoveAt(idx);
                }
            }
        }

        // 添加一条友好 Skill 满足 SelfSkills / SelfBoard 条件
        var helperSkill = new CardInstance(smallSkill);
        p1.Skills.Add(helperSkill);

        // 敌方 Hand：根据 OpponentHand Equal 计数或默认 3 张构造（1 Small + 1 Medium + 1 Large，满足 Equal 1 Small / Medium 条件）
        var enemyCards = new List<CardInstance>();
        int oppCount = exactOpponentHandCount ?? 3;
        for (int i = 0; i < oppCount; i++)
        {
            CardTemplate eTpl = (i == 1 || (oppCount == 1 && needsMediumOppHand))
                ? PickTemplate(CardSize.Medium, hRight)
                : (i == 2 && oppCount >= 3 ? PickTemplate(CardSize.Large, hRight) : PickTemplate(CardSize.Small, hLeft));
            var ec = new CardInstance(eTpl);
            bool ecEnchant = enchantToApply != null || (wantsEnchantedOppHand && i == 0);
            PopulateRichAttributes(ec, asHeatedChilled: i == 0, asFlying: i == 0, withFreezeSlow: true, asDisabled: false, zeroAmmo: false, allowEnchant: ecEnchant, applyCommonTags: i == 0);
            p2.Hand.TryPlaceOrShift(ec, i * 2);
            enemyCards.Add(ec);
        }

        static double ReadCondVal(RuleNode sc) =>
            sc.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue()
            ?? sc.GetFixedComparisonValue()
            ?? sc.ComparisonAmount
            ?? 0;

        // 确保被测卡自己若要求特定 Self Prerequisite（HasEnchantment / SellPrice / Custom_0..9 / Flying / Tiny 等）时满足前提
        foreach (var prereq in allNodes.Where(n => n.Type == "TPrerequisiteCardCount" && n.Subject?.Type == "TTargetCardSelf"))
        {
            var selfCondNodes = WalkNodes([prereq]).ToList();
            bool prereqRequiresPresent = !string.Equals(prereq.Comparison, "Equal", StringComparison.OrdinalIgnoreCase)
                                         || (prereq.Amount ?? 1) > 0;
            foreach (var enchCond in selfCondNodes.Where(x => x.Type == "TCardConditionalHasEnchantment"))
            {
                // Enchantment==null 时 IsNot=false 表示“未附魔”、IsNot=true 表示“已附魔”；指定具体附魔名时相反
                bool condMeansEnchanted = !string.IsNullOrEmpty(enchCond.Enchantment)
                    ? (enchCond.IsNot != true)
                    : (enchCond.IsNot == true);
                bool wantEnch = condMeansEnchanted == prereqRequiresPresent;
                if (!string.IsNullOrEmpty(enchCond.Enchantment))
                {
                    if (wantEnch) targetCard.ApplyEnchantment(enchCond.Enchantment);
                    else if (string.Equals(targetCard.Enchantment, enchCond.Enchantment, StringComparison.OrdinalIgnoreCase))
                        targetCard.RemoveEnchantment(DurationTarget.Permanent);
                }
                else
                {
                    if (wantEnch && string.IsNullOrEmpty(targetCard.Enchantment))
                        targetCard.ApplyEnchantment("Heavy");
                    else if (!wantEnch)
                        targetCard.RemoveEnchantment(DurationTarget.Permanent);
                }
            }
            foreach (var sc in selfCondNodes.Where(x => x.Type == "TCardConditionalAttribute" && !string.IsNullOrEmpty(x.Attribute)))
            {
                var sa = sc.Attribute!;
                double cmpVal = ReadCondVal(sc);
                if (sa == "Heated") targetCard.Heated = sc.ComparisonOperator != "Equal" || cmpVal > 0;
                else if (sa == "Chilled") targetCard.Chilled = sc.ComparisonOperator != "Equal" || cmpVal > 0;
                else if (sa == "Flying") targetCard.IsFlying = sc.ComparisonOperator == "Equal" ? cmpVal > 0 : sc.ComparisonOperator is "GreaterThan" or "GreaterThanOrEqual";
                else if (sa == "Freeze") targetCard.FreezeMs = (sc.ComparisonOperator == "Equal" ? cmpVal > 0 : sc.ComparisonOperator != "LessThan") ? 2000 : 0;
                else if (sa == "Slow") targetCard.SlowMs = (sc.ComparisonOperator == "Equal" ? cmpVal > 0 : sc.ComparisonOperator != "LessThan") ? 2000 : 0;
                else if (sa == "Tiny")
                {
                    bool wantTiny = sc.ComparisonOperator == "Equal" ? cmpVal > 0 : sc.ComparisonOperator is "GreaterThan" or "GreaterThanOrEqual";
                    targetCard.ModifyAttribute("Tiny", "Set", wantTiny ? 1 : 0, DurationTarget.Permanent);
                }
                else if (sa == "SellPrice" && sc.ComparisonOperator is "GreaterThan" or "GreaterThanOrEqual")
                    targetCard.PermanentAdd["SellPrice"] = Math.Max(cmpVal + 5, targetCard.GetAttribute("SellPrice"));
                else
                {
                    var cv = sc.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
                    string? cvAttr = cv?.Type == "TReferenceValueCardAttribute" ? cv.AttributeType : null;
                    if (!string.IsNullOrEmpty(cvAttr))
                    {
                        if (sc.ComparisonOperator is "LessThan" or "LessThanOrEqual")
                        {
                            targetCard.ModifyAttribute(sa, "Set", 0, DurationTarget.Permanent);
                            if (targetCard.GetAttribute(cvAttr) <= 0)
                                targetCard.ModifyAttribute(cvAttr, "Set", 5, DurationTarget.Permanent);
                        }
                        else if (sc.ComparisonOperator is "GreaterThan" or "GreaterThanOrEqual")
                        {
                            double refV = Math.Max(1, targetCard.GetAttribute(cvAttr));
                            targetCard.ModifyAttribute(sa, "Set", refV + (sc.ComparisonOperator == "GreaterThan" ? 1 : 0), DurationTarget.Permanent);
                        }
                        else if (sc.ComparisonOperator == "Equal")
                        {
                            double refV = targetCard.GetAttribute(cvAttr);
                            targetCard.ModifyAttribute(sa, "Set", refV, DurationTarget.Permanent);
                        }
                    }
                    else if (sc.ComparisonOperator == "Equal")
                    {
                        targetCard.ModifyAttribute(sa, "Set", cmpVal, DurationTarget.Permanent);
                    }
                    else if (sc.ComparisonOperator is "GreaterThan" or "GreaterThanOrEqual")
                    {
                        double desired = cmpVal + (sc.ComparisonOperator == "GreaterThan" ? 1 : 0);
                        if (targetCard.GetAttribute(sa) < desired || (sc.ComparisonOperator == "GreaterThan" && targetCard.GetAttribute(sa) <= cmpVal))
                            targetCard.ModifyAttribute(sa, "Set", desired, DurationTarget.Permanent);
                    }
                    else if (sc.ComparisonOperator is "LessThan" or "LessThanOrEqual")
                    {
                        double desired = Math.Max(0, cmpVal - 1);
                        targetCard.ModifyAttribute(sa, "Set", desired, DurationTarget.Permanent);
                    }
                }
            }
        }

        return (companions, enemyCards, sockets);
    }

    private static void PrepareSelfAndTargetDynamicAttributes(
        CardInstance card,
        List<CardInstance> companions,
        List<CardInstance> enemyCards,
        AbilityDefinition? ab,
        AuraDefinition? au = null)
    {
        var roots = new List<RuleNode>();
        if (ab?.Action != null) roots.Add(ab.Action);
        if (au?.Action != null) roots.Add(au.Action);

        var nodes = WalkNodes(roots).ToList();

        // 检查是否是对 Freeze/Slow/Haste/Custom_8/Heated/Chilled 做减法或清零的动作
        if (ab?.Action?.Type == "TActionCardModifyAttribute")
        {
            var attr = ab.Action.AttributeType ?? string.Empty;
            var op = ab.Action.Operation ?? "Add";
            if (string.Equals(op, "Subtract", StringComparison.OrdinalIgnoreCase)
                || string.Equals(op, "Multiply", StringComparison.OrdinalIgnoreCase))
            {
                if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) card.FreezeMs = Math.Max(card.FreezeMs, 2000);
                if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)) card.SlowMs = Math.Max(card.SlowMs, 2000);
                if (attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)) card.HasteMs = Math.Max(card.HasteMs, 2000);
                if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                {
                    if (card.EffectiveAmmoMax <= 0) card.PermanentAdd["AmmoMax"] = 2;
                    card.CurrentAmmo = Math.Max(1, card.EffectiveAmmoMax);
                }
            }
        }

        // 若动作或光环 Value 引用到自身一个尚未初始化的 Custom_0..Custom_9 / 累积属性（如战中才逐步叠上去的属性），确保基准 > 0
        foreach (var refNode in nodes.Where(n => n.Type == "TReferenceValueCardAttribute" && !string.IsNullOrEmpty(n.AttributeType)))
        {
            var aName = refNode.AttributeType!;
            if (refNode.Target?.Type == "TTargetCardSelf" || refNode.Target == null)
            {
                if (Math.Abs(card.GetAttribute(aName)) < 1e-6)
                {
                    if (aName.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) card.FreezeMs = 2000;
                    else if (aName.Equals("Slow", StringComparison.OrdinalIgnoreCase)) card.SlowMs = 2000;
                    else if (aName.Equals("Haste", StringComparison.OrdinalIgnoreCase)) card.HasteMs = 1000;
                    else if (aName.Equals("Ammo", StringComparison.OrdinalIgnoreCase)) { card.PermanentAdd["AmmoMax"] = 3; card.CurrentAmmo = 2; }
                    else if (aName.Equals("Flying", StringComparison.OrdinalIgnoreCase)) card.IsFlying = true;
                    else card.PermanentAdd[aName] = 5;
                }
            }
            else
            {
                foreach (var c in companions.Take(2))
                {
                    if (Math.Abs(c.GetAttribute(aName)) < 1e-6)
                        c.PermanentAdd[aName] = 5;
                }
            }
        }

        // 如果卡牌触发器是 TTriggerOnCardAttributeChanged(..., Gain/Loss)，且动作依赖对应属性（比如 Sitting Duck 自带 Custom_8）
        if (ab?.Trigger?.Type == "TTriggerOnCardAttributeChanged" && ab.Trigger.AttributeChanged == "Custom_8")
            card.PermanentAdd["Custom_8"] = Math.Max(1, card.GetAttribute("Custom_8"));

        // 若卡牌自身 FlyingStart/Stop 要求变更，仅复位目标卡自身及右邻卡（保留 leftMost/leftNeighbor 的 Flying=true 供 Barrel Roll 等按已飞行邻居触发）
        bool prereqSelfFlying = ab?.Prerequisites != null
            && WalkNodes(ab.Prerequisites).Any(n => n.Type == "TCardConditionalAttribute" && n.Attribute == "Flying");
        if (ab?.Action?.Type == "TActionCardFlyingStart")
        {
            if (!prereqSelfFlying && (ab.Action.Target?.Type == "TTargetCardSelf" || ab.Action.Target == null))
                card.IsFlying = false;
            if (companions.Count > 2) companions[2].IsFlying = false;
            foreach (var e in enemyCards) e.IsFlying = false;
        }
        else if (ab?.Action?.Type == "TActionCardFlyingStop")
        {
            if (!prereqSelfFlying) card.IsFlying = true;
            foreach (var c in companions) c.IsFlying = true;
            foreach (var e in enemyCards) e.IsFlying = true;
        }
    }

    private static void SeedZeroBaseScalingPrimaryAttrIfNeeded(CardInstance card, AbilityDefinition ab)
    {
        if (ab.Action == null) return;
        var valNode = ab.Action.GetValueNode(GameDatabaseLoader.JsonOptions);
        if (ab.Action.ReferenceValue != null || valNode != null) return;
        string? primaryAttr = ab.Action.Type switch
        {
            "TActionPlayerDamage" => "DamageAmount",
            "TActionPlayerShieldApply" => "ShieldApplyAmount",
            "TActionPlayerBurnApply" => "BurnApplyAmount",
            "TActionPlayerPoisonApply" => "PoisonApplyAmount",
            "TActionPlayerHeal" or "TActionPlayerReviveHeal" => "HealAmount",
            "TActionPlayerRegenApply" => "RegenApplyAmount",
            _ => null
        };
        if (primaryAttr == null) return;
        if (card.GetAttribute(primaryAttr) <= 0 && (card.ActiveAbilities.Count > 1 || card.ActiveAuras.Count > 0))
            card.PermanentAdd[primaryAttr] = 10;
    }

    private static void EnsureActionCostSatisfied(PlayerState p1, CardInstance card, RuleNode action)
    {
        if (action.Cost == null) return;
        if (action.Cost.Type == "TActionCostPlayerAttribute")
        {
            var attr = action.Cost.PlayerAttributeType ?? "Gold";
            if (attr == "Tempo") p1.Tempo = Math.Max(p1.Tempo, 100);
            else if (attr == "Rage") p1.Rage = Math.Max(p1.Rage, 100);
            else p1.Attributes[attr] = Math.Max(p1.Attributes.GetValueOrDefault(attr, 0), 100);
        }
    }

    private static RuleScope DetermineScopeForTrigger(RuleNode? trigger, RuleNode action, string? worksIn = null)
    {
        if (string.Equals(worksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase))
            return RuleScope.OutOfCombat;

        var tType = trigger?.Type;
        if (tType == "TTriggerOr" && trigger?.Triggers is { Count: > 0 })
            tType = trigger.Triggers[0].Type;

        return tType switch
        {
            "TTriggerOnCardSold" or "TTriggerOnCardPurchased" or "TTriggerOnCardSelected"
                or "TTriggerOnCardUpgraded" or "TTriggerOnCardEnchanted" or "TTriggerOnCardQuestCompleted"
                or "TTriggerOnPlayerLeveledUp" or "TTriggerOnDayStarted" or "TTriggerOnHourStarted"
                or "TTriggerOnRunStarted" or "TTriggerOnRunCompleted" or "TTriggerOnEncounterSelected"
                or "TTriggerOnEncounterEntered" or "TTriggerOnEncounterExited" or "TTriggerOnEncounterCardsDealt"
                => RuleScope.OutOfCombat,
            "TTriggerOnPlayerAttributeChanged" when trigger?.AttributeType is "Gold" or "Level" or "Experience" or "Prestige" or "Income"
                => RuleScope.OutOfCombat,
            _ when action.Type is "TActionGameReroll" or "TActionExitReplacementSet" or "TActionGameAddToExclusionSet" or "TActionGameRemoveFromExclusionSet"
                => RuleScope.OutOfCombat,
            _ => RuleScope.InCombat
        };
    }

    private static (string EventName, CardInstance? SrcCard, CardInstance? TgtCard, PlayerState? SubjPlayer, string? AttrChanged, double Prev, double Cur)
        ResolveTriggerInvocation(
            GameDataRepository repo,
            RuleNode? trigger,
            CardInstance ownerCard,
            PlayerState p1,
            PlayerState p2,
            List<CardInstance> companions,
            List<CardInstance> enemyCards,
            RuleExecutionContext execCtx,
            List<CardTemplate> allItems,
            List<RuleNode>? prereqs = null,
            RuleNode? action = null)
    {
        if (trigger == null)
            return ("TTriggerOnCardFired", ownerCard, ownerCard, p1, null, 0, 0);

        if (trigger.Type == "TTriggerOr" && trigger.Triggers is { Count: > 0 })
            return ResolveTriggerInvocation(repo, trigger.Triggers[0], ownerCard, p1, p2, companions, enemyCards, execCtx, allItems, prereqs, action);

        string evName = trigger.Type ?? "TTriggerOnCardFired";
        CardInstance leftNeighbor = companions.Count > 1 ? companions[1] : companions[0];
        CardInstance rightNeighbor = companions.Count > 2 ? companions[2] : companions[0];

        // 检查 Trigger 直接挂载的 Conditions（如 TTriggerOnEncounterSelected / Dealt 附带的 Tier / Tag / Id 条件）
        var directCond = trigger.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        var directNodes = WalkNodes([trigger]).Where(n => !ReferenceEquals(n, trigger)).ToList();
        var explicitCondId = directNodes.FirstOrDefault(n => n.Type == "TCardConditionalId" && n.IsNot != true && !string.IsNullOrEmpty(n.Id))?.Id;
        if (!string.IsNullOrEmpty(explicitCondId) && repo.CardsById.TryGetValue(explicitCondId, out var explicitTpl))
        {
            var matchedCard = new CardInstance(explicitTpl);
            return (evName, matchedCard, matchedCard, p1, null, 0, 0);
        }
        if (evName is "TTriggerOnEncounterSelected" or "TTriggerOnEncounterEntered" or "TTriggerOnEncounterExited" or "TTriggerOnEncounterCardsDealt" && directCond != null)
        {
            var reqTierStr = directNodes.FirstOrDefault(n => n.Type == "TCardConditionalTier" && n.Tiers is { Count: > 0 } && n.IsNot != true)?.Tiers?[0];
            var reqTagStr = directNodes.FirstOrDefault(n => n.Type == "TCardConditionalTag" && n.Tags is { Count: > 0 } && n.IsNot != true)?.Tags;
            var encTpl = allItems.FirstOrDefault(t => reqTagStr == null || reqTagStr.Any(tg => t.Tags.Contains(tg, StringComparer.OrdinalIgnoreCase)))
                         ?? companions[0].Template;
            var encCard = new CardInstance(encTpl);
            if (!string.IsNullOrEmpty(reqTierStr) && Enum.TryParse<CardTier>(reqTierStr, true, out var parsedTier))
                encCard.SetTier(parsedTier);
            if (reqTagStr != null) foreach (var tg in reqTagStr) encCard.ExtraTags.Add(tg);
            if (ValueAndConditionEvaluator.EvaluateCardCondition(directCond, encCard, p1, execCtx))
                return (evName, encCard, encCard, p1, null, 0, 0);
        }

        bool targetIsOpponent = (trigger.Subject?.TargetSection ?? string.Empty).Contains("Opponent", StringComparison.OrdinalIgnoreCase)
                             || (trigger.Subject?.TargetMode ?? string.Empty).Equals("Opponent", StringComparison.OrdinalIgnoreCase);

        // 若动作或前置条件对 TriggerSource 有额外筛选条件，也一并纳入候选评分（如 Kite / Adaptive Ordinance / Assault Focus）
        var triggerSourceExtraConds = WalkNodes([.. (prereqs ?? []), .. (action != null ? [action] : Array.Empty<RuleNode>())])
            .Where(n => n.Type == "TTargetCardTriggerSource")
            .Select(n => n.GetSingleCondition(GameDatabaseLoader.JsonOptions))
            .Where(c => c != null)
            .ToList();

        CardInstance? BuildSyntheticTriggerSource(IEnumerable<RuleNode?> extraConds)
        {
            var sCondNodes = WalkNodes(extraConds.Where(n => n != null)!).ToList();
            var reqSz = sCondNodes.FirstOrDefault(n => n.Type == "TCardConditionalSize" && n.Sizes is { Count: > 0 } && n.IsNot != true)?.Sizes?[0];
            var synthTpl = Enum.TryParse<CardSize>(reqSz, true, out var psz)
                ? allItems.FirstOrDefault(t => t.CardSize == psz) ?? leftNeighbor.Template
                : leftNeighbor.Template;
            var synth = new CardInstance(synthTpl);
            synth.PermanentAdd["CooldownMax"] = Math.Max(0, 5000 - synth.BaseAttributes.GetValueOrDefault("CooldownMax", 0));
            foreach (var tg in sCondNodes.Where(n => n.Type == "TCardConditionalTag" && n.Tags != null && n.IsNot != true).SelectMany(n => n.Tags!))
                synth.ExtraTags.Add(tg);
            var reqTierS = sCondNodes.FirstOrDefault(n => n.Type == "TCardConditionalTier" && n.Tiers is { Count: > 0 } && n.IsNot != true)?.Tiers?[0];
            if (!string.IsNullOrEmpty(reqTierS) && Enum.TryParse<CardTier>(reqTierS, true, out var parsedS))
                synth.SetTier(parsedS);
            if (sCondNodes.Any(n => n.Type == "TCardConditionalAttribute" && n.Attribute == "Ammo" && n.ComparisonOperator == "Equal"))
            {
                synth.PermanentAdd["AmmoMax"] = 3;
                synth.CurrentAmmo = 0;
            }
            return synth;
        }

        CardInstance? PickSubjectCard()
        {
            if (trigger.Subject == null || trigger.Subject.Type == "TTargetCardSelf")
            {
                if (trigger.Subject == null && triggerSourceExtraConds.Count > 0)
                    return BuildSyntheticTriggerSource(triggerSourceExtraConds!);
                return ownerCard;
            }

            // TTriggerOnCardDisabled / BeforeDestroyed / Repaired 需暂时放行 IsDisabled 以便选中真正的受损/禁用搭档卡
            bool unmaskDisabled = evName is "TTriggerOnCardDisabled" or "TTriggerOnBeforeCardDestroyed" or "TTriggerOnCardRepaired";
            var unmasked = new List<CardInstance>();
            if (unmaskDisabled)
            {
                foreach (var c in companions.Where(x => x.IsDisabled && !x.IsDestroyed))
                {
                    c.IsDisabled = false;
                    unmasked.Add(c);
                }
            }
            try
            {
                // 若 Subject 为 TTargetCardTriggerSource（常见于 Quest 买/卖与 Skill 监听触发源），绝不能用 ownerCard 充当 TriggerSource
                if (trigger.Subject.Type == "TTargetCardTriggerSource")
                {
                    var trigCond = trigger.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                    var pool = p1.Hand.Cards.Concat(p1.Stash.Cards).Concat(enemyCards).Concat(companions)
                        .Where(c => c.Template.Type == "Item" && (trigger.Subject.ExcludeSelf != true || !ReferenceEquals(c, ownerCard)))
                        .Distinct()
                        .ToList();
                    foreach (var cand in pool)
                    {
                        var ownerP = enemyCards.Contains(cand) ? p2 : p1;
                        if (trigCond != null && !ValueAndConditionEvaluator.EvaluateCardCondition(trigCond, cand, ownerP, execCtx))
                            continue;
                        if (triggerSourceExtraConds.All(ec => ValueAndConditionEvaluator.EvaluateCardCondition(ec, cand, ownerP, execCtx)))
                            return cand;
                    }
                    // 若仍没精准匹配（例如 Lumi 需要 Item+Tool 或 Monster Tracker 需要 Gold+ 遭遇触发源），就地构造并赋给对应 Tier/Tag
                    return BuildSyntheticTriggerSource([trigger.Subject, .. triggerSourceExtraConds!]);
                }

                // 常规 Subject：先用引擎真实 TargetResolver.ResolveCards 解析，避免误选 Skill 或不符合条件卡
                var resolved = TargetResolver.ResolveCards(trigger.Subject, execCtx.WithCards(ownerCard, ownerCard, ownerCard));
                if (evName == "TTriggerOnItemUsed" && ownerCard.GetAttribute("CooldownMax") <= 0)
                    resolved = resolved.Where(c => !ReferenceEquals(c, ownerCard) && c.GetAttribute("CooldownMax") > 0).ToList();
                if (unmaskDisabled && unmasked.Count > 0)
                {
                    var disHit = resolved.FirstOrDefault(c => unmasked.Contains(c));
                    if (disHit != null) return disHit;
                }
                if (resolved.Count > 0)
                {
                    // 若动作对 TriggerSource 的 LeftCard 有要求（如 Jury Rigger），避开 StartSlot==0 的最左卡
                    if (action?.Target?.Type == "TTargetCardPositional"
                        && string.Equals(action.Target.Origin, "TriggerSource", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(action.Target.TargetMode, "LeftCard", StringComparison.OrdinalIgnoreCase))
                    {
                        var withLeft = resolved.FirstOrDefault(c => c.StartSlot > 0);
                        if (withLeft != null) return withLeft;
                    }
                    if (action?.Type == "TActionCardFlyingStart"
                        && action.Target?.Type == "TTargetCardPositional"
                        && string.Equals(action.Target.Origin, "TriggerSource", StringComparison.OrdinalIgnoreCase))
                    {
                        var withNonFlyNeighbor = resolved.FirstOrDefault(c => p1.Hand.GetNeighbors(c).Any(n => !n.IsFlying));
                        if (withNonFlyNeighbor != null) return withNonFlyNeighbor;
                    }
                    if (triggerSourceExtraConds.Count > 0)
                    {
                        var best = resolved.FirstOrDefault(c =>
                            triggerSourceExtraConds.All(ec => ValueAndConditionEvaluator.EvaluateCardCondition(ec, c, p1, execCtx)));
                        if (best != null) return best;
                    }
                    return resolved[0];
                }
            }
            finally
            {
                foreach (var c in unmasked) c.IsDisabled = true;
            }

            if (targetIsOpponent)
                return enemyCards.FirstOrDefault() ?? companions[0];
            if (trigger.Subject.TargetSection is "SelfNeighbors" or "TriggerSourceNeighbors"
                || trigger.Subject.Type == "TTargetCardPositional")
            {
                return leftNeighbor;
            }
            if (ownerCard.Template.Type == "Item" && trigger.Subject.ExcludeSelf != true && (evName != "TTriggerOnItemUsed" || ownerCard.GetAttribute("CooldownMax") > 0))
                return ownerCard;
            return leftNeighbor;
        }

        CardInstance? subjectCard = PickSubjectCard();
        PlayerState? subjectPlayer = targetIsOpponent ? p2 : p1;

        CardInstance? ResolveTriggerTargetCard()
        {
            if (trigger.Target == null) return subjectCard;
            if (trigger.Target.Type == "TTargetCardSelf") return ownerCard;
            var resT = TargetResolver.ResolveCards(trigger.Target, execCtx.WithCards(ownerCard, subjectCard, subjectCard));
            if (resT.Count > 0)
            {
                if (action?.Type == "TActionCardFlyingStart")
                {
                    var nonFly = resT.FirstOrDefault(c => !c.IsFlying);
                    if (nonFly != null) return nonFly;
                }
                else if (action?.Type == "TActionCardFlyingStop")
                {
                    var flying = resT.FirstOrDefault(c => c.IsFlying);
                    if (flying != null) return flying;
                }
                return resT[0];
            }
            bool tgtOpp = (trigger.Target.TargetMode ?? trigger.Target.TargetSection ?? string.Empty).Contains("Opponent", StringComparison.OrdinalIgnoreCase);
            return tgtOpp ? (enemyCards.FirstOrDefault() ?? rightNeighbor) : rightNeighbor;
        }

        if (evName == "TTriggerOnCardAttributeChanged")
        {
            string attr = trigger.AttributeChanged ?? "Custom_0";
            bool isLoss = string.Equals(trigger.ChangeType, "Loss", StringComparison.OrdinalIgnoreCase);
            if (isLoss && subjectCard != null)
            {
                if (attr.Equals("Flying", StringComparison.OrdinalIgnoreCase)) subjectCard.IsFlying = true;
                else if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) subjectCard.FreezeMs = Math.Max(subjectCard.FreezeMs, 2000);
                else if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)) subjectCard.SlowMs = Math.Max(subjectCard.SlowMs, 2000);
                else if (attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)) subjectCard.HasteMs = Math.Max(subjectCard.HasteMs, 2000);
                else if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                {
                    if (subjectCard.EffectiveAmmoMax <= 0) subjectCard.PermanentAdd["AmmoMax"] = 3;
                    subjectCard.CurrentAmmo = Math.Max(1, subjectCard.CurrentAmmo);
                }
            }
            // 若 Prerequisite 检查触发源 Ammo == 0（如 Biomerge Arm / Adaptive Ordinance），确保执行前 CurrentAmmo == 0
            if (prereqs != null
                && subjectCard != null
                && WalkNodes(prereqs).Any(n => n.Type == "TCardConditionalAttribute" && n.Attribute == "Ammo" && n.ComparisonOperator == "Equal" && Math.Abs(n.GetFixedComparisonValue() ?? 0) < 1e-6))
            {
                if (subjectCard.EffectiveAmmoMax <= 0) subjectCard.PermanentAdd["AmmoMax"] = 3;
                subjectCard.CurrentAmmo = 0;
            }
            return (evName, subjectCard, subjectCard, p1, attr, isLoss ? 10 : 0, isLoss ? 0 : 10);
        }
        if (evName == "TTriggerOnPlayerAttributeChanged")
        {
            string pAttr = trigger.AttributeType ?? "Health";
            bool isLoss = string.Equals(trigger.ChangeType, "Loss", StringComparison.OrdinalIgnoreCase);
            return (evName, ownerCard, null, subjectPlayer, pAttr, isLoss ? 50 : 0, isLoss ? 10 : 50);
        }
        if (evName is "TTriggerOnPlayerDamaged" or "TTriggerOnPlayerLifeStealed" or "TTriggerOnPlayerShielded")
        {
            double amt = Math.Max(10, (trigger.ComparisonAmount ?? 0) + 10);
            return (evName, leftNeighbor, ownerCard, subjectPlayer, null, 0, amt);
        }
        if (evName == "TTriggerOnPlayerLeveledUp")
        {
            int lvl = trigger.Level.HasValue ? Math.Min(p1.Level, trigger.Level.Value) : p1.Level;
            return (evName, ownerCard, null, p1, "Level", Math.Max(1, lvl - 1), lvl);
        }
        if (evName.StartsWith("TTriggerOnCardPerformed", StringComparison.Ordinal))
        {
            bool tgtOpp = (trigger.Target?.TargetMode ?? trigger.Target?.TargetSection ?? string.Empty).Contains("Opponent", StringComparison.OrdinalIgnoreCase);
            PlayerState? perfSubjPlayer = trigger.Target?.Type?.StartsWith("TTargetPlayer", StringComparison.Ordinal) == true
                ? (tgtOpp ? p2 : p1)
                : subjectPlayer;
            CardInstance? actionTarget = ResolveTriggerTargetCard();
            return (evName, subjectCard, actionTarget, perfSubjPlayer, null, 0, 0);
        }

        // 若 Trigger 明确要求 Source == Self（如 Grabinator / Tractor Beam / Parts Picker / Oblivion Vortex 的 TTriggerOnBeforeCardDestroyed）
        if (trigger.Source?.Type == "TTargetCardSelf")
            return (evName, ownerCard, ResolveTriggerTargetCard(), subjectPlayer, null, 0, 0);

        return (evName, subjectCard, ResolveTriggerTargetCard(), subjectPlayer, null, 0, 0);
    }

    private sealed class BoardAuditSnapshot
    {
        public Dictionary<string, double> P1Scalars { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> P2Scalars { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> CardSignatures { get; } = new(StringComparer.Ordinal);
    }

    private static readonly string[] WatchedCardAttrs =
    [
        "DamageAmount", "ShieldApplyAmount", "BurnApplyAmount", "PoisonApplyAmount",
        "HealAmount", "RegenApplyAmount", "CritChance", "Multicast", "Lifesteal",
        "CooldownMax", "FlatCooldownReduction", "PercentCooldownReduction", "CooldownDisabled",
        "AmmoMax", "SellPrice", "BuyPrice", "HasteAmount", "SlowAmount", "FreezeAmount",
        "ChargeAmount", "RageApplyAmount", "TempoApplyAmount", "PercentFreezeReduction",
        "PercentSlowReduction", "DestroyImmunity", "Heated", "Chilled", "Tiny",
        "Custom_0", "Custom_1", "Custom_2", "Custom_3", "Custom_4", "Custom_5", "Custom_6", "Custom_7", "Custom_8", "Custom_9",
        "Quest_1", "Quest_2", "Quest_3", "Quest_4", "Quest_5"
    ];

    private static BoardAuditSnapshot CaptureSnapshot(PlayerState p1, PlayerState p2)
    {
        var snap = new BoardAuditSnapshot();
        CapturePlayer(snap.P1Scalars, snap.CardSignatures, "p1", p1);
        CapturePlayer(snap.P2Scalars, snap.CardSignatures, "p2", p2);
        return snap;

        static void CapturePlayer(Dictionary<string, double> psOut, Dictionary<string, string> cardsOut, string prefix, PlayerState p)
        {
            psOut["Health"] = p.CurrentHealth;
            psOut["HealthMax"] = p.HealthMax;
            psOut["Shield"] = p.Shield;
            psOut["Burn"] = p.Burn;
            psOut["Poison"] = p.Poison;
            psOut["Rage"] = p.Rage;
            psOut["EnragedMs"] = p.EnragedRemainingMs;
            psOut["Tempo"] = p.Tempo;
            psOut["Gold"] = p.Gold;
            psOut["Income"] = p.GetAttribute("Income");
            psOut["Prestige"] = p.Prestige;
            psOut["Experience"] = p.Experience;
            psOut["Level"] = p.Level;
            psOut["RerollCostMod"] = p.GetAttribute("RerollCostModifier");
            psOut["PercentDmgRed"] = p.GetAttribute("PercentDamageReduction");
            psOut["HealthRegen"] = p.GetAttribute("HealthRegen");
            psOut["RageMax"] = p.GetAttribute("RageMax");
            psOut["EnragedDurMax"] = p.GetAttribute("EnragedDurationMax");
            psOut["ExclusionCount"] = p.ExclusionSet.Count;
            psOut["Portrait"] = p.PortraitIndex;
            psOut["HandCount"] = p.Hand.Cards.Count;
            psOut["StashCount"] = p.Stash.Cards.Count;
            psOut["SkillCount"] = p.Skills.Count;
            psOut["SocketCount"] = p.SocketEffects.Count;
            foreach (var kv in p.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal))
                psOut[$"Attr:{kv.Key}"] = kv.Value;

            foreach (var c in p.EnumerateAllOwnedCards())
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{c.Template.Id}|{c.Tier}|{c.Enchantment}|{c.CurrentAmmo}|{c.CooldownProgressMs}|{c.HasteMs}|{c.SlowMs}|{c.FreezeMs}|{c.IsFlying}|{c.IsDisabled}|{c.IsDestroyed}|{c.Heated}|{c.Chilled}|{c.IsTiny}|{c.SlotWidth}|");
                foreach (var a in WatchedCardAttrs)
                    sb.Append($"{a}={Math.Round(c.GetAttribute(a), 3)},");
                sb.Append("Tags=").Append(string.Join(";", c.GetAllTags().OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
                sb.Append("|Q=").Append(string.Join(";", c.CompletedQuests.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
                cardsOut[$"{prefix}:{c.InstanceId}"] = sb.ToString();
            }
        }
    }

    private static bool HasAnySnapshotDelta(BoardAuditSnapshot before, BoardAuditSnapshot after, CardInstance? ignoreDisabledFlagOnCard = null)
    {
        foreach (var (k, v) in after.P1Scalars)
        {
            if (!before.P1Scalars.TryGetValue(k, out var pv) || Math.Abs(v - pv) > 1e-6)
                return true;
        }
        foreach (var (k, v) in after.P2Scalars)
        {
            if (!before.P2Scalars.TryGetValue(k, out var pv) || Math.Abs(v - pv) > 1e-6)
                return true;
        }
        if (before.CardSignatures.Count != after.CardSignatures.Count)
            return true;
        foreach (var (k, v) in after.CardSignatures)
        {
            if (!before.CardSignatures.TryGetValue(k, out var pv))
                return true;
            if (!string.Equals(v, pv, StringComparison.Ordinal))
            {
                // 若只比对 Aura 启用/禁用期间被试卡自己的 IsDisabled 切换本身，则忽略仅 IsDisabled 字段反转导致的假变化
                if (ignoreDisabledFlagOnCard != null && k.EndsWith(":" + ignoreDisabledFlagOnCard.InstanceId, StringComparison.Ordinal))
                {
                    var normV = v.Replace("|False|", "|D|").Replace("|True|", "|D|");
                    var normPv = pv.Replace("|False|", "|D|").Replace("|True|", "|D|");
                    if (string.Equals(normV, normPv, StringComparison.Ordinal))
                        continue;
                }
                return true;
            }
        }
        return false;
    }
}

internal static class CardTemplateTestExtensions
{
    public static bool TierMapFallback(this CardTemplate tpl)
        => tpl.Tiers == null || tpl.Tiers.Count == 0;
}
