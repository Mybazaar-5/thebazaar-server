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
/// Wave 4 Batch 4B-Core（全部剩余 Core 与 CombatSimConverter P2 精修）回归测试套件：
/// 逐项覆盖 P1-17(Core)、P2-01、P2-02、P2-06、P2-09、P2-10、P2-13、P2-17、P2-19、P2-20、
/// P2-21、P2-22、P2-23、P2-24、P2-25、P2-30、P2-37、P2-43、P2-44、P2-45、P2-46、P2-47 共 22 项。
/// </summary>
public sealed class AuditWave4CoreP2Tests
{
    private static string ResolveDbPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "dist", "data", "GameData.db");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        const string fallback = @"e:\thebazaar\dist\data\GameData.db";
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException("未找到测试用 GameData.db");
    }

    private static readonly Lazy<GameDataRepository> Repo = new(() => GameDatabaseLoader.Load(ResolveDbPath()));

    // =========================================================================
    // P1-17 (Core)：TActionGameReroll 透传 SpawnCount 至 OutOfCombatBatchResult.RerollEntries
    // =========================================================================

    [Fact]
    public void P1_17_TActionGameReroll_PropagatesSpawnCountToRerollEntriesAndLegacyCallback()
    {
        var repo = Repo.Value;
        var player = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var rerollCard = CreateCustomItem("reroll_step", "RerollStep", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandAndStash",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardSelected"}"""),
                Action = ParseNode("""{"$type":"TActionGameReroll","RerollType":"All","SpawnCount":4}""")
            }
        });

        var batch = OutOfCombatController.OnCardSelected(player, rerollCard, repo, new Random(17));
        Assert.Contains("All", batch.RerollRequests);
        var entry = Assert.Single(batch.RerollEntries);
        Assert.Equal("All", entry.RerollType);
        Assert.Equal(4, entry.SpawnCount);

        // 回退兼容：未绑定 OnRerollEncounterWithCount 时仍调用旧 OnRerollEncounter
        string? legacyCalled = null;
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(17))
        {
            OnRerollEncounter = t => legacyCalled = t
        }.WithCards(rerollCard, rerollCard);
        Assert.True(AbilityExecutor.ExecuteAction(rerollCard.Template.Abilities["0"].Action, rerollCard, ctx));
        Assert.Equal("All", legacyCalled);
    }

    // =========================================================================
    // P2-01：AuraDefinition.VFXConfig 反序列化 + CombatSimConverter 登记光环 VFXOverrideKey
    // =========================================================================

    [Fact]
    public void P2_01_AuraDefinition_ParsesVfxConfig_AndRegistersAuraVfxKeyInCombatSim()
    {
        var repo = Repo.Value;

        // 1. 验证真实 GameData.db 中 4 条带非空 VFXOverrideKey 的光环（2 条 Card.Auras + 2 条 Enchantments.*.Auras）
        int baseAuraVfxCount = 0;
        int enchAuraVfxCount = 0;
        CardTemplate? sampleBaseAuraCard = null;
        string? sampleBaseAuraId = null;
        string? sampleBaseVfxKey = null;

        foreach (var card in repo.CardsById.Values)
        {
            foreach (var (auraId, aura) in card.Auras)
            {
                if (!string.IsNullOrWhiteSpace(aura.VFXConfig?.VFXOverrideKey))
                {
                    baseAuraVfxCount++;
                    if (sampleBaseAuraCard == null)
                    {
                        sampleBaseAuraCard = card;
                        sampleBaseAuraId = auraId;
                        sampleBaseVfxKey = aura.VFXConfig!.VFXOverrideKey!.Trim();
                    }
                }
            }
            if (card.Enchantments != null)
            {
                foreach (var ench in card.Enchantments.Values)
                {
                    foreach (var aura in ench.Auras.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(aura.VFXConfig?.VFXOverrideKey))
                            enchAuraVfxCount++;
                    }
                }
            }
        }

        Assert.True(baseAuraVfxCount >= 2, $"期望 Card.Auras 含 >= 2 条自定义 VFXOverrideKey，实际 {baseAuraVfxCount}");
        Assert.True(enchAuraVfxCount >= 2, $"期望 Enchantments.*.Auras 含 >= 2 条自定义 VFXOverrideKey，实际 {enchAuraVfxCount}");
        Assert.NotNull(sampleBaseAuraCard);

        // 2. CombatSimConverter 处理 AuraExecuted 时自动将对应 Aura.VFXConfig.VFXOverrideKey 注册入 CombatSim.VfxKeys
        var p1Core = new CardInstance(sampleBaseAuraCard!, CardTier.Bronze, null, "core_aura_src");
        var p1TgtCore = new CardInstance(repo.AlwaysSpawnableItems.First(), CardTier.Bronze, null, "core_aura_tgt");
        var p1Entry = MakeBoardEntry("ent_aura_src", p1Core, ECombatantId.Player, EContainerSocketId.Socket_0);
        var p1TgtEntry = MakeBoardEntry("ent_aura_tgt", p1TgtCore, ECombatantId.Player, EContainerSocketId.Socket_2);
        var p2Core = new CardInstance(repo.AlwaysSpawnableItems.Skip(1).First(), CardTier.Bronze, null, "core_opp");
        var p2Entry = MakeBoardEntry("ent_opp", p2Core, ECombatantId.Opponent, EContainerSocketId.Socket_0);

        var simResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opponent",
            DurationMs = 100,
            Player1RemainingHp = 500,
            Player2RemainingHp = 0,
            Events =
            {
                new CombatEvent
                {
                    TimeMs = 0,
                    Type = CombatEventType.AuraExecuted,
                    SourcePlayerId = "player",
                    SourceCardInstanceId = "core_aura_src",
                    AbilityId = sampleBaseAuraId,
                    AuraAppliedCardIds = new[] { "core_aura_tgt" }
                }
            },
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 500, P1MaxHp = 500, P2Hp = 500, P2MaxHp = 500 },
                new CombatTickState { TimeMs = 100, P1Hp = 500, P1MaxHp = 500, P2Hp = 0, P2MaxHp = 500 }
            }
        };

        var sim = CombatSimConverter.ToCombatSim(
            simResult,
            repo,
            new Dictionary<string, CardInstance> { [p1Entry.InstanceId] = p1Core, [p1TgtEntry.InstanceId] = p1TgtCore },
            new Dictionary<string, CardInstance> { [p2Entry.InstanceId] = p2Core },
            [p1Entry, p1TgtEntry],
            [p2Entry]);

        Assert.Contains(sampleBaseVfxKey!, sim.VfxKeys);
        Assert.Contains(sim.Frames.SelectMany(f => f.Events).OfType<CombatSimEventEffectAuraExecuted>(),
            e => e.EffectId == sampleBaseAuraId);
    }

    // =========================================================================
    // P2-02：QuestEntry.Localization / IconKeyOverride 解析 + TooltipFormatter 本地化渲染
    // =========================================================================

    [Fact]
    public void P2_02_QuestEntry_ParsesLocalizationAndIconOverride_AndFormatsTooltip()
    {
        var repo = Repo.Value;
        var questCards = repo.CardsById.Values.Where(c => c.Quests != null && c.Quests.Count > 0).ToList();
        Assert.NotEmpty(questCards);

        int locCount = 0;
        int iconCount = 0;
        CardTemplate? cardWithQuestLoc = null;
        foreach (var c in questCards)
        {
            foreach (var q in c.Quests!)
            {
                foreach (var entry in q.Entries)
                {
                    if (entry.Localization?.Tooltips?.Count > 0)
                    {
                        locCount++;
                        cardWithQuestLoc ??= c;
                    }
                    if (!string.IsNullOrWhiteSpace(entry.IconKeyOverride))
                        iconCount++;
                }
            }
        }

        Assert.True(locCount >= 80, $"期望解析 >= 80 条 QuestEntry.Localization，实际 {locCount}");
        Assert.True(iconCount >= 70, $"期望解析 >= 70 条 QuestEntry.IconKeyOverride，实际 {iconCount}");
        Assert.NotNull(cardWithQuestLoc);

        var inst = new CardInstance(cardWithQuestLoc!, CardTier.Bronze);
        var formatted = TooltipFormatter.FormatTooltips(inst);
        var questLines = formatted.Where(l => l.Contains("[任务进度]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(questLines);
        // 确认优先输出任务本地化说明而非原始裸 AttributeType（如 "Quest_1:"）
        Assert.DoesNotContain(questLines, l => l.Contains("Quest_1:", StringComparison.Ordinal));
    }

    // =========================================================================
    // P2-06：CardTemplate.SelectionRequirements 兼容 JSON 数组与单对象双格式
    // =========================================================================

    [Fact]
    public void P2_06_SelectionRequirements_DeserializesBothArrayAndSingleObjectSafely()
    {
        const string arrayJson = """
            {
              "Id": "00000000-0000-0000-0000-00000000p206",
              "Type": "EventEncounter",
              "InternalName": "ArrayReqEvent",
              "SelectionRequirements": [
                {
                  "$type": "TSelectionRequirement",
                  "Prerequisites": [
                    { "$type": "TPrerequisitePlayerAttribute", "AttributeType": "Gold", "Comparison": "GreaterThanOrEqual", "Amount": 5 }
                  ]
                },
                {
                  "$type": "TSelectionRequirement",
                  "Prerequisites": [
                    { "$type": "TPrerequisitePlayerAttribute", "AttributeType": "Level", "Comparison": "GreaterThanOrEqual", "Amount": 2 }
                  ]
                }
              ]
            }
            """;

        const string singleJson = """
            {
              "Id": "00000000-0000-0000-0000-00000000p207",
              "Type": "EventEncounter",
              "InternalName": "SingleReqEvent",
              "SelectionRequirements": {
                "$type": "TSelectionRequirement",
                "Prerequisites": [
                  { "$type": "TPrerequisitePlayerAttribute", "AttributeType": "Gold", "Comparison": "GreaterThanOrEqual", "Amount": 3 }
                ]
              }
            }
            """;

        var arrCard = JsonSerializer.Deserialize<CardTemplate>(arrayJson, GameDatabaseLoader.JsonOptions)!;
        Assert.NotNull(arrCard.SelectionRequirements);
        Assert.NotNull(arrCard.SelectionRequirements!.CombinedSelectionRequirements);
        Assert.Equal(2, arrCard.SelectionRequirements.CombinedSelectionRequirements!.Count);
        Assert.NotNull(arrCard.SelectionRequirements.Prerequisites);
        Assert.Equal(2, arrCard.SelectionRequirements.Prerequisites!.Count);

        var singleCard = JsonSerializer.Deserialize<CardTemplate>(singleJson, GameDatabaseLoader.JsonOptions)!;
        Assert.NotNull(singleCard.SelectionRequirements);
        Assert.Single(singleCard.SelectionRequirements!.Prerequisites!);
    }

    // =========================================================================
    // P2-09 + P2-10 + P2-13：移除冗余 Triggered、平局双死 / IsReverted / ExecutionContextId 同链共享、被动 UseCount
    // =========================================================================

    [Fact]
    public void P2_09_P2_10_P2_13_ProtocolCleanup_RevertedEnchant_SharedChainId_AndPassiveUseCount()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("player", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("opponent", HeroNames.Pygmalien, 2000);

        // 1. 主动武器（1000ms 冷却打 10 伤），触发被动技能（无冷却，通过 TActionAnd 同链执行护盾 + 灼烧双动作）
        var striker = CreateCustomItem("p213_striker", "Striker", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        var passiveSkill = CreateCustomSkill("p213_skill", "PassiveMultiActionSkill", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandAndStash",
                Priority = "Immediate",
                Trigger = ParseNode("""{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand"}}"""),
                Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        { "$type": "TActionPlayerShieldApply", "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" }, "ReferenceValue": { "$type": "TFixedValue", "Value": 15 } },
                        { "$type": "TActionPlayerBurnApply", "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" }, "ReferenceValue": { "$type": "TFixedValue", "Value": 3 } }
                      ]
                    }
                    """)
            }
        });
        // 纯无冷却被动物品（在自己获得护盾时触发给对手 1 点毒）
        var passiveItem = CreateCustomItem("p213_passive_item", "PassiveToxicTrinket", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Priority = "Immediate",
                Trigger = ParseNode("""{"$type":"TTriggerOnPlayerShielded","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""),
                Action = ParseNode("""{"$type":"TActionPlayerPoisonApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"},"ReferenceValue":{"$type":"TFixedValue","Value":1}}""")
            }
        });

        // 2. 3000ms 时对 striker 施加再撤回附魔（通过合成事件测试 IsReverted=true 协议透传）
        p1.Hand.TryPlaceOrShift(striker, 0);
        p1.Hand.TryPlaceOrShift(passiveItem, 1);
        p1.Skills.Add(passiveSkill);

        // 2500ms 时让对手被一击必杀，确保 striker 恰好开火 2 次（950ms / 1950ms）
        var finisher = CreateCustomItem("p213_fin", "Finisher", attrs: new() { ["CooldownMax"] = 2500, ["DamageAmount"] = 5000 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
            }
        });
        p1.Hand.TryPlaceOrShift(finisher, 2);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2130, recordTimeline: true, recordEvents: true);
        // 核验 P2-10 触发链共享 ExecutionContextId：t=950ms striker 开火与同链引发的被动技能护盾/灼烧共享同一个非空 ExecutionContextId
        var firstFire = res.Events.First(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == striker.InstanceId);
        var secondFire = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.SourceCardInstanceId == striker.InstanceId).Skip(1).First();
        Assert.False(string.IsNullOrEmpty(firstFire.ExecutionContextId));
        Assert.NotEqual(firstFire.ExecutionContextId, secondFire.ExecutionContextId);

        var firstChainSkillShield = res.Events.First(e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == passiveSkill.InstanceId && e.TimeMs == firstFire.TimeMs);
        var firstChainSkillBurn = res.Events.First(e => e.Type == CombatEventType.BurnApplied && e.SourceCardInstanceId == passiveSkill.InstanceId && e.TimeMs == firstFire.TimeMs);
        Assert.Equal(firstFire.ExecutionContextId, firstChainSkillShield.ExecutionContextId);
        Assert.Equal(firstFire.ExecutionContextId, firstChainSkillBurn.ExecutionContextId);

        // 追加一条结构化 IsReverted=true 的附魔撤回事件供转换器验证 P2-10
        res.Events.Add(new CombatEvent
        {
            TimeMs = firstFire.TimeMs,
            Type = CombatEventType.CardEnchanted,
            SourcePlayerId = "player",
            TargetPlayerId = "player",
            SourceCardInstanceId = striker.InstanceId,
            TargetCardInstanceId = striker.InstanceId,
            Detail = "Fiery",
            IsReverted = true,
            ExecutionContextId = firstFire.ExecutionContextId
        });

        var p1Entries = new List<RunState.CardEntry>
        {
            MakeBoardEntry(striker.InstanceId, striker, ECombatantId.Player, EContainerSocketId.Socket_0),
            MakeBoardEntry(passiveItem.InstanceId, passiveItem, ECombatantId.Player, EContainerSocketId.Socket_1),
            MakeBoardEntry(finisher.InstanceId, finisher, ECombatantId.Player, EContainerSocketId.Socket_2),
            MakeSkillEntry(passiveSkill.InstanceId, passiveSkill, ECombatantId.Player)
        };
        var p1Map = p1Entries.ToDictionary(e => e.InstanceId, e => e.CoreCard!);
        var sim = CombatSimConverter.ToCombatSim(res, repo, p1Map, new Dictionary<string, CardInstance>(), p1Entries, new List<RunState.CardEntry>());

        var allProtoEvents = sim.Frames.SelectMany(f => f.Events).ToList();
        // P2-09：全战 0 条冗余 CombatSimEventEffectTriggered
        Assert.DoesNotContain(allProtoEvents, e => e is CombatSimEventEffectTriggered);

        // P2-10：同一触发链下的 CombatSimEventEffectExecuted 共享 ExecutionContextId
        var chainExecs = allProtoEvents.OfType<CombatSimEventEffectExecuted>()
            .Where(e => e.ExecutionContextId == firstFire.ExecutionContextId)
            .ToList();
        Assert.True(chainExecs.Count >= 3, $"同一触发链至少含开火+护盾+灼烧 3 条执行事件，实际 {chainExecs.Count}");

        // P2-10：CombatSimEventCardEnchanted.IsReverted = true 精确透传
        var revertedEnch = Assert.Single(allProtoEvents.OfType<CombatSimEventCardEnchanted>());
        Assert.True(revertedEnch.IsReverted);
        Assert.Equal(EEnchantmentType.Fiery, revertedEnch.EnchantmentType);

        // P2-13：被动技能（TActionAnd 双子动作）触发 3 次（striker×2 + finisher×1）严格计 UseCount=3（非 6）；纯被动物品计 UseCount=3
        Assert.Equal(3, GetStatValue(sim, passiveSkill.InstanceId, ECardStats.UseCount));
        Assert.Equal(3, GetStatValue(sim, passiveItem.InstanceId, ECardStats.UseCount));
        Assert.Equal(2, GetStatValue(sim, striker.InstanceId, ECardStats.UseCount));

        // P2-10：双杀平局（即使未显式置 IsSimultaneousDeath，只要 IsDraw=true 且双方残血 <= 0）尾帧双方均置 IsPlayerDead=true 且发双 CombatantDied
        var drawResult = new CombatResult
        {
            IsDraw = true,
            IsSimultaneousDeath = false,
            Player1RemainingHp = 0,
            Player2RemainingHp = 0,
            DurationMs = 100,
            Timeline =
            {
                new CombatTickState { TimeMs = 0, P1Hp = 100, P1MaxHp = 100, P2Hp = 100, P2MaxHp = 100 },
                new CombatTickState { TimeMs = 100, P1Hp = 0, P1MaxHp = 100, P2Hp = 0, P2MaxHp = 100 }
            }
        };
        var drawSim = CombatSimConverter.ToCombatSim(
            drawResult,
            repo,
            new Dictionary<string, CardInstance>(),
            new Dictionary<string, CardInstance>(),
            new List<RunState.CardEntry>(),
            new List<RunState.CardEntry>());
        var lastDrawFrame = drawSim.Frames.Last();
        Assert.True(lastDrawFrame.PlayerUpdates?.IsPlayerDead);
        Assert.True(lastDrawFrame.OpponentUpdates?.IsPlayerDead);
        Assert.Contains(lastDrawFrame.Events, e => e is CombatSimEventCombatantDied { CombatantId: ECombatantId.Player });
        Assert.Contains(lastDrawFrame.Events, e => e is CombatSimEventCombatantDied { CombatantId: ECombatantId.Opponent });
    }

    // =========================================================================
    // P2-17：TActionPlayerDamage 无回调分支致命一击正确传伤值触发 TTriggerOnPlayerDamaged
    // =========================================================================

    [Fact]
    public void P2_17_TActionPlayerDamage_WithoutCallback_PassesDamageAmountOnLethalHit()
    {
        var repo = Repo.Value;
        var attacker = CreatePlayer("att", HeroNames.Vanessa, 500);
        var victim = CreatePlayer("vic", HeroNames.Pygmalien, 50); // 仅 50 血，遭受 80 点致命一击后 CurrentHealth == 0

        var sword = CreateCustomItem("lethal_sword", "LethalSword", attrs: new() { ["DamageAmount"] = 80 });
        var watcher = CreateCustomItem("flamberge_like", "FlambergeLike", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""
                    {
                      "$type": "TTriggerOnPlayerDamaged",
                      "Subject": { "$type": "TTargetPlayerRelative", "TargetMode": "Self" },
                      "ComparisonOperator": "GreaterThan",
                      "ComparisonAmount": 0
                    }
                    """)
            }
        });
        attacker.Hand.TryPlaceOrShift(sword, 0);
        victim.Hand.TryPlaceOrShift(watcher, 0);

        double? reportedCurVal = null;
        bool matchedTrigger = false;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, attacker, victim, new Random(217))
        {
            OnDetailedTriggeredEvent = (evType, _, src, tgt, subj, attr, prev, cur) =>
            {
                if (evType == "TTriggerOnPlayerDamaged")
                {
                    reportedCurVal = cur;
                    var victimCtx = new RuleExecutionContext(repo, RuleScope.InCombat, victim, attacker, new Random(217));
                    matchedTrigger = AbilityExecutor.MatchesTrigger(
                        watcher.Template.Abilities["0"].Trigger,
                        evType, watcher, victim, src, tgt, subj, attr, prev, cur, "PVP", "", victimCtx);
                }
            }
        }.WithCards(sword, sword);

        var dmgAction = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""");
        Assert.True(AbilityExecutor.ExecuteAction(dmgAction, sword, ctx));
        Assert.Equal(0, victim.CurrentHealth);
        Assert.Equal(80.0, reportedCurVal);
        Assert.True(matchedTrigger, "致命一击造成 80 点伤害后，TTriggerOnPlayerDamaged(>0) 必须命中");
    }

    // =========================================================================
    // P2-19：Quest 边界（PreviousValue/CurrentValue、Quest_X GetAttribute、Source 校验、Repeatable 重置）
    // =========================================================================

    [Fact]
    public void P2_19_QuestSystem_AttributeFallback_SourceCheck_ValueNode_AndRepeatableReset()
    {
        var repo = Repo.Value;

        // 1. RuleNode PreviousValue / CurrentValue 兼容 RuleNode 与纯数字双反序列化
        var trigWithNode = ParseNode("""
            {
              "$type": "TTriggerOnCardAttributeChanged",
              "AttributeChanged": "Custom_0",
              "PreviousValue": { "$type": "TFixedValue", "Value": 2 },
              "CurrentValue": { "$type": "TFixedValue", "Value": 5 }
            }
            """);
        Assert.NotNull(trigWithNode.GetPreviousValueNode(GameDatabaseLoader.JsonOptions));
        Assert.NotNull(trigWithNode.GetCurrentValueNode(GameDatabaseLoader.JsonOptions));
        Assert.Equal(2.0, trigWithNode.PreviousValue);
        Assert.Equal(5.0, trigWithNode.CurrentValue);

        // 2. CardInstance.GetAttribute("Quest_1") 回退读取 QuestProgress
        var basketTpl = repo.GetCard("30da8c8f-5342-4867-b1c2-5b5f8e1a5881")
            ?? repo.CardsById.Values.First(c => string.Equals(c.InternalName, "Basket", StringComparison.OrdinalIgnoreCase));
        var basket = new CardInstance(basketTpl, CardTier.Bronze, null, "basket_inst");
        basket.QuestProgress["Quest_1"] = 3;
        Assert.Equal(3.0, basket.GetAttribute("Quest_1"));

        // 3. TTriggerOnCardAttributeChanged.Source 校验（ExcludeSelf 拦截自身、放行他源卡）
        var owner = CreatePlayer("p1", HeroNames.Pygmalien, 500);
        var helper = CreateCustomItem("helper_food", "HelperFood");
        owner.Hand.TryPlaceOrShift(basket, 0);
        owner.Hand.TryPlaceOrShift(helper, 2);

        var sourceTrigger = ParseNode("""
            {
              "$type": "TTriggerOnCardAttributeChanged",
              "AttributeChanged": "Quest_1",
              "Subject": { "$type": "TTargetCardSelf" },
              "Source": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
            }
            """);
        var checkCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, owner, null, new Random(219));
        Assert.False(AbilityExecutor.MatchesTrigger(sourceTrigger, "TTriggerOnCardAttributeChanged", basket, owner, basket, basket, owner, "Quest_1", 1, 2, "PVP", "", checkCtx));
        Assert.True(AbilityExecutor.MatchesTrigger(sourceTrigger, "TTriggerOnCardAttributeChanged", basket, owner, helper, basket, owner, "Quest_1", 1, 2, "PVP", "", checkCtx));

        // 4. Repeatable 任务：满足 Repeatable.Prerequisites 时归零进度不锁死 CompletedQuests，可连续完成两次
        var repeatableTpl = new CardTemplate
        {
            Id = "repeatable_quest_test",
            InternalName = "RepeatableQuestTest",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Quests = new List<QuestDefinition>
            {
                new QuestDefinition
                {
                    Repeatable = new QuestRepeatableData
                    {
                        Prerequisites = new List<RuleNode>
                        {
                            ParseNode("""{"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand"},"Comparison":"GreaterThanOrEqual","Amount":3}""")
                        }
                    },
                    Entries =
                    {
                        new QuestEntry
                        {
                            AttributeType = "Quest_1",
                            Target = 2,
                            Trigger = ParseNode("""{"$type":"TTriggerOnCardSold","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true}}"""),
                            CompletionEffects = new Dictionary<string, AbilityDefinition>
                            {
                                ["0"] = new AbilityDefinition
                                {
                                    Id = "0",
                                    Action = ParseNode("""{"$type":"TActionPlayerModifyAttribute","AttributeType":"Gold","Operation":"Add","Value":{"$type":"TFixedValue","Value":3},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
                                }
                            }
                        }
                    }
                }
            }
        };

        var rqOwner = CreatePlayer("rq_owner", HeroNames.Vanessa, 500, initialGold: 10);
        var rqCard = new CardInstance(repeatableTpl, CardTier.Bronze, null, "rq_card");
        var soldDummy = CreateCustomItem("sold_dummy", "SoldDummy");
        var extraHandCard = CreateCustomItem("extra_hand", "ExtraHand");
        rqOwner.Hand.TryPlaceOrShift(rqCard, 0);
        rqOwner.Hand.TryPlaceOrShift(soldDummy, 2);
        rqOwner.Hand.TryPlaceOrShift(extraHandCard, 4);
        int completions = 0;
        var rqCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, rqOwner, null, new Random(219))
        {
            OnQuestCompleted = _ => completions++
        };

        // 第 1 轮（手牌共 3 张满足 Repeatable.Prerequisites >= 3 -> 完成 + 奖励 + 清零，不锁入 CompletedQuests）
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardSold", rqOwner, soldDummy, soldDummy, rqOwner, null, 0, 0, "PVP", "", rqCtx);
        Assert.Equal(1.0, rqCard.GetAttribute("Quest_1"));
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardSold", rqOwner, soldDummy, soldDummy, rqOwner, null, 0, 0, "PVP", "", rqCtx);
        Assert.Equal(1, completions);
        Assert.Equal(13, rqOwner.Gold);
        Assert.Equal(0.0, rqCard.GetAttribute("Quest_1"));
        Assert.DoesNotContain("Quest_1", rqCard.CompletedQuests);

        // 第 2 轮（移走 extraHandCard 使手牌仅剩 2 张不满足 >= 3 -> 第 2 次完成后应封顶锁入 CompletedQuests）
        rqOwner.Hand.Remove(extraHandCard);
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardSold", rqOwner, soldDummy, soldDummy, rqOwner, null, 0, 0, "PVP", "", rqCtx);
        AbilityExecutor.TryAdvanceQuests("TTriggerOnCardSold", rqOwner, soldDummy, soldDummy, rqOwner, null, 0, 0, "PVP", "", rqCtx);
        Assert.Equal(2, completions);
        Assert.Contains("Quest_1", rqCard.CompletedQuests);
    }

    // =========================================================================
    // P2-20：BurnRemove / PoisonRemove 与 HealCleanse 发战报事件、PlayerAttributeChanged 并刷新动态光环
    // =========================================================================

    [Fact]
    public void P2_20_BurnPoisonRemoveAndHealCleanse_EmitEventsAttributeChangesAndRefreshAuras()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 1000);
        var p2 = CreatePlayer("p2", HeroNames.Pygmalien, 1000);

        // 对手 500ms 给 p1 上 1 点 Burn & 1 点 Poison；p1 1000ms 用治疗卡净化全部 Burn/Poison
        // p1 同时有一张动态光环卡：当自己 Burn == 0 时给另一张武器 +50 DamageAmount
        var p2Applier = CreateCustomItem("p220_applier", "Applier", attrs: new() { ["CooldownMax"] = 500, ["BurnApplyAmount"] = 1, ["PoisonApplyAmount"] = 1, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""
                    {
                      "$type": "TActionAnd",
                      "Actions": [
                        { "$type": "TActionPlayerBurnApply", "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" } },
                        { "$type": "TActionPlayerPoisonApply", "Target": { "$type": "TTargetPlayerRelative", "TargetMode": "Opponent" } }
                      ]
                    }
                    """)
            }
        });
        var p1Healer = CreateCustomItem("p220_healer", "Healer", attrs: new() { ["CooldownMax"] = 1000, ["HealAmount"] = 20, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerHeal","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}""")
            }
        });
        var p1Remover = CreateCustomItem("p220_remover", "BurnPoisonRemover", attrs: new() { ["BurnRemoveAmount"] = 5, ["PoisonRemoveAmount"] = 4 });
        p1.Hand.TryPlaceOrShift(p1Healer, 0);
        p2.Hand.TryPlaceOrShift(p2Applier, 0);

        var simRes = CombatSimulator.Simulate(repo, p1, p2, seed: 2200, recordTimeline: true, recordEvents: true);
        Assert.Contains(simRes.Events, e => e.Type == CombatEventType.BurnRemoved && e.Detail == "HealCleanse" && e.Value >= 1);
        Assert.Contains(simRes.Events, e => e.Type == CombatEventType.PoisonRemoved && e.Detail == "HealCleanse" && e.Value >= 1);

        // 直接测试 TActionPlayerBurnRemove / TActionPlayerPoisonRemove 发射战报事件与 Loss 属性变更
        var directEvents = new List<CombatEvent>();
        var directAttrEvents = new List<(string Attr, double Prev, double Cur)>();
        p1.Burn = 8;
        p1.Poison = 6;
        var ctx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, p2, new Random(220))
        {
            EmitCombatEvent = ev => directEvents.Add(ev),
            OnDetailedTriggeredEvent = (evType, _, _, _, _, attr, prev, cur) =>
            {
                if (evType == "TTriggerOnPlayerAttributeChanged" && attr != null)
                    directAttrEvents.Add((attr, prev, cur));
            }
        }.WithCards(p1Remover, p1Remover);

        Assert.True(AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionPlayerBurnRemove","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""), p1Remover, ctx));
        Assert.True(AbilityExecutor.ExecuteAction(ParseNode("""{"$type":"TActionPlayerPoisonRemove","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""), p1Remover, ctx));
        Assert.Contains(directEvents, e => e.Type == CombatEventType.BurnRemoved && Math.Abs(e.Value - 5) < 1e-6);
        Assert.Contains(directEvents, e => e.Type == CombatEventType.PoisonRemoved && Math.Abs(e.Value - 4) < 1e-6);
        Assert.Contains(directAttrEvents, t => t.Attr == "Burn" && t.Prev == 8 && t.Cur == 3);
        Assert.Contains(directAttrEvents, t => t.Attr == "Poison" && t.Prev == 6 && t.Cur == 2);
    }

    // =========================================================================
    // P2-21：TSpawnGroup.SelectionMethod == "Sequential" 按声明序确定性出卡
    // =========================================================================

    [Fact]
    public void P2_21_SpawnGroup_SequentialSelectionMethod_PicksInDeclaredIdOrderAcrossSeeds()
    {
        var repo = Repo.Value;
        var sampleIds = repo.AlwaysSpawnableItems
            .Where(c => c.DefaultTier == CardTier.Bronze
                && c.Heroes.Contains(HeroNames.Vanessa, StringComparer.OrdinalIgnoreCase))
            .Take(4)
            .Select(c => c.Id)
            .ToList();
        Assert.Equal(4, sampleIds.Count);

        string queryJson = $$"""
            {
              "$type": "TSpawnContextQuery",
              "Limit": { "$type": "TFixedValue", "Value": 3 },
              "Groups": [
                {
                  "$type": "TSpawnGroup",
                  "SelectionMethod": "Sequential",
                  "Limit": { "$type": "TFixedValue", "Value": 3 },
                  "Filters": [
                    {
                      "$type": "TSpawnFilterIdList",
                      "Ids": ["{{sampleIds[2]}}", "{{sampleIds[0]}}", "{{sampleIds[3]}}", "{{sampleIds[1]}}"]
                    }
                  ]
                }
              ]
            }
            """;

        var spawnNode = ParseNode(queryJson);
        var player = CreatePlayer("p1", HeroNames.Vanessa, 500);
        string[] expectedOrder = [sampleIds[2], sampleIds[0], sampleIds[3]];

        foreach (int seed in new[] { 1, 42, 999, 2026 })
        {
            var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(seed));
            var spawned = SpawnQueryEngine.SpawnCards(spawnNode, ctx);
            Assert.Equal(3, spawned.Count);
            Assert.Equal(expectedOrder, spawned.Select(c => c.Template.Id).ToArray());
        }
    }

    // =========================================================================
    // P2-22：TTriggerOnPlayerLeveledUp.NewLevel 与 TTriggerOnRunCompleted.Outcome 生效
    // =========================================================================

    [Fact]
    public void P2_22_StronglyTypedNewLevelAndOutcome_AreHonoredByTriggerMatching()
    {
        var repo = Repo.Value;
        var player = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var dummyCard = CreateCustomItem("p222_card", "ChallengeDummy");
        var ctx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(222));

        var levelUpTrig = ParseNode("""
            {
              "$type": "TTriggerOnPlayerLeveledUp",
              "NewLevel": 5,
              "ComparisonOperator": "Equal"
            }
            """);
        Assert.Equal(5, levelUpTrig.NewLevel);
        Assert.False(AbilityExecutor.MatchesTrigger(levelUpTrig, "TTriggerOnPlayerLeveledUp", dummyCard, player, null, null, player, "Level", 3, 4, "PVP", "", ctx));
        Assert.True(AbilityExecutor.MatchesTrigger(levelUpTrig, "TTriggerOnPlayerLeveledUp", dummyCard, player, null, null, player, "Level", 4, 5, "PVP", "", ctx));

        var runCompletedTrig = ParseNode("""
            {
              "$type": "TTriggerOnRunCompleted",
              "Outcome": "Victory"
            }
            """);
        Assert.Equal("Victory", runCompletedTrig.Outcome);
        Assert.False(AbilityExecutor.MatchesTrigger(runCompletedTrig, "TTriggerOnRunCompleted", dummyCard, player, null, null, player, null, 0, 0, "PVP", "Defeat", ctx));
        Assert.True(AbilityExecutor.MatchesTrigger(runCompletedTrig, "TTriggerOnRunCompleted", dummyCard, player, null, null, player, null, 0, 0, "PVP", "Victory", ctx));
    }

    // =========================================================================
    // P2-23：TActionCardUpgrade.InheritEnchantment (true 继承附魔 / false 不继承)
    // =========================================================================

    [Fact]
    public void P2_23_TActionCardUpgrade_RespectsInheritEnchantmentFlag()
    {
        var repo = Repo.Value;
        var fieryCapableTpl = repo.AlwaysSpawnableItems.First(c =>
            c.DefaultTier == CardTier.Bronze
            && c.Enchantments != null
            && c.Enchantments.ContainsKey("Fiery"));

        // 1. InheritEnchantment = true（或省略默认 true）：带 Fiery 附魔的升级源卡应将 Fiery 透传给未附魔合法目标
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var sourceCard = new CardInstance(fieryCapableTpl, CardTier.Bronze, "Fiery", "src_en");
        var targetA = new CardInstance(fieryCapableTpl, CardTier.Bronze, null, "tgt_inherit_true");
        p1.Hand.TryPlaceOrShift(sourceCard, 0);
        p1.Hand.TryPlaceOrShift(targetA, 2);

        var ctx1 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p1, null, new Random(223)).WithCards(sourceCard, sourceCard);
        var upTrue = ParseNode("""
            {
              "$type": "TActionCardUpgrade",
              "InheritEnchantment": true,
              "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
            }
            """);
        Assert.True(AbilityExecutor.ExecuteAction(upTrue, sourceCard, ctx1));
        Assert.Equal(CardTier.Silver, targetA.Tier);
        Assert.Equal("Fiery", targetA.Enchantment);

        // 2. InheritEnchantment = false：仅升阶，绝不继承源卡附魔
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
        var sourceCard2 = new CardInstance(fieryCapableTpl, CardTier.Bronze, "Fiery", "src_en2");
        var targetB = new CardInstance(fieryCapableTpl, CardTier.Bronze, null, "tgt_inherit_false");
        p2.Hand.TryPlaceOrShift(sourceCard2, 0);
        p2.Hand.TryPlaceOrShift(targetB, 2);

        var ctx2 = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p2, null, new Random(223)).WithCards(sourceCard2, sourceCard2);
        var upFalse = ParseNode("""
            {
              "$type": "TActionCardUpgrade",
              "InheritEnchantment": false,
              "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand", "ExcludeSelf": true }
            }
            """);
        Assert.True(AbilityExecutor.ExecuteAction(upFalse, sourceCard2, ctx2));
        Assert.Equal(CardTier.Silver, targetB.Tier);
        Assert.Null(targetB.Enchantment);
    }

    // =========================================================================
    // P2-24：PlayerState.GetAttribute 对 Shield/Burn/Poison/Rage/Tempo 叠加光环与临时层，Health 直读
    // =========================================================================

    [Fact]
    public void P2_24_PlayerState_LivePoolsComposeAuraAndCombatLayersWithoutPollutingBasePool()
    {
        var p = CreatePlayer("p1", HeroNames.Vanessa, 500);
        p.CurrentHealth = 320;
        p.AuraAdd["Health"] = 100; // Health 必须保持直读 CurrentHealth，不受 AuraAdd 污染
        Assert.Equal(320.0, p.GetAttribute("Health"));

        p.Shield = 20;
        p.AuraAdd["Shield"] = 10;
        p.CombatAdd["Shield"] = 5;
        Assert.Equal(35.0, p.GetAttribute("Shield"));

        // ModifyAttribute("Shield", "Add", 10) 仅增加裸池 Shield (20 -> 30)，不把 AuraAdd 重复烘入实时池也不污染永久 Attributes
        p.ModifyAttribute("Shield", "Add", 10);
        Assert.Equal(30, p.Shield);
        Assert.Equal(45.0, p.GetAttribute("Shield"));
        Assert.False(p.Attributes.ContainsKey("Shield"));

        // Tempo 叠加光环加算与乘算
        p.Tempo = 2;
        p.AuraAdd["Tempo"] = 3;
        p.AuraMult["Tempo"] = 2.0;
        Assert.Equal(10.0, p.GetAttribute("Tempo"));
    }

    // =========================================================================
    // P2-25：OutOfCombatController 将外部遭遇卡统一纳入 AuraEngine（支持逐目标求值与标签脏标）
    // =========================================================================

    [Fact]
    public void P2_25_OutOfCombatController_DelegatesExternalEncounterAuraToAuraEngine()
    {
        var repo = Repo.Value;
        var player = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var itemA = CreateCustomItem("p225_a", "ItemA", attrs: new() { ["DamageAmount"] = 10, ["Custom_0"] = 10 });
        var itemB = CreateCustomItem("p225_b", "ItemB", attrs: new() { ["DamageAmount"] = 10, ["Custom_0"] = 30 });
        player.Hand.TryPlaceOrShift(itemA, 0);
        player.Hand.TryPlaceOrShift(itemB, 1);

        // 先访问一次把属性缓存预热
        Assert.Equal(10.0, itemA.GetAttribute("DamageAmount"));
        Assert.Equal(10.0, itemB.GetAttribute("DamageAmount"));
        Assert.False(itemA.HasTag("Relic"));

        // 外部遭遇卡携带 2 条光环：1) 逐目标读取自身 Custom_0（Target==null）叠加至 DamageAmount；2) 赋予 Relic 标签
        var encounterCard = CreateCustomItem("p225_enc", "EncounterWithAura", auras: new()
        {
            ["0"] = new AuraDefinition
            {
                Id = "0",
                ActiveIn = "HandAndStash",
                Action = ParseNode("""
                    {
                      "$type": "TAuraActionCardModifyAttribute",
                      "AttributeType": "DamageAmount",
                      "Operation": "Add",
                      "Value": { "$type": "TReferenceValueCardAttribute", "AttributeType": "Custom_0" },
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" }
                    }
                    """)
            },
            ["1"] = new AuraDefinition
            {
                Id = "1",
                ActiveIn = "HandAndStash",
                Action = ParseNode("""
                    {
                      "$type": "TAuraActionCardAddTagsList",
                      "Tags": ["Relic"],
                      "Target": { "$type": "TTargetCardSection", "TargetSection": "SelfHand" }
                    }
                    """)
            }
        });

        // 同时放一张依赖 Relic 标签才生效的己方光环卡，验证标签脏标与多轮收敛联动生效
        var relicWatcher = CreateCustomItem("p225_watcher", "RelicWatcher", auras: new()
        {
            ["0"] = new AuraDefinition
            {
                Id = "0",
                ActiveIn = "HandAndStash",
                Action = ParseNode("""
                    {
                      "$type": "TAuraActionCardModifyAttribute",
                      "AttributeType": "ShieldApplyAmount",
                      "Operation": "Add",
                      "Value": { "$type": "TFixedValue", "Value": 7 },
                      "Target": {
                        "$type": "TTargetCardSection",
                        "TargetSection": "SelfHand",
                        "Conditions": { "$type": "TCardConditionalTag", "Tags": ["Relic"], "Operator": "Any" }
                      }
                    }
                    """)
            }
        });
        player.Hand.TryPlaceOrShift(relicWatcher, 2);

        OutOfCombatController.RecalculateOutOfCombatAuras(player, repo, activeEncounterCard: encounterCard);

        Assert.Equal(20.0, itemA.GetAttribute("DamageAmount"));
        Assert.Equal(40.0, itemB.GetAttribute("DamageAmount"));
        Assert.True(itemA.HasTag("Relic"));
        Assert.Equal(7.0, itemA.GetAttribute("ShieldApplyAmount"));
    }

    // =========================================================================
    // P2-30 + Patch 20.2：标准局按 NumberOfDays=10 钳制 Day 10+ 稳态 (0/0.20/0.70/0.10)；
    // 自定义 Mode 设 NumberOfDays>=12 时仍保留旧 DB Day 12 总和 0.80 -> Diamond=0.25 防呆修正
    // =========================================================================

    [Fact]
    public void P2_30_Day12TierWeights_NormalizesDiamondFrom005To025()
    {
        var repo = Repo.Value;
        // 1. 标准 Base GameMode（NumberOfDays=10）：Day 10–16 统一对齐 BazaarDB Patch 20.2 稳态 0 / 0.20 / 0.70 / 0.10
        foreach (int lateDay in new[] { 10, 11, 12, 13, 15, 16 })
        {
            var w = SpawnQueryEngine.GetDayTierWeights(repo.GameMode, lateDay);
            Assert.Equal(0.0, w.Bronze, 6);
            Assert.Equal(0.20, w.Silver, 6);
            Assert.Equal(0.70, w.Gold, 6);
            Assert.Equal(0.10, w.Diamond, 6);
            Assert.Equal(1.00, w.Bronze + w.Silver + w.Gold + w.Diamond, 6);
        }

        // 2. 自定义 Mode 将 NumberOfDays 扩至 20 且沿用旧表时：仍可读取高天行并对 Day 12 笔误（0.80）做 P2-30 平滑修正
        var customExtendedMode = new GameModeData
        {
            Id = repo.GameMode.Id,
            NumberOfDays = 20,
            ItemSkillSpawnTierPercantagesByDay = repo.GameMode.ItemSkillSpawnTierPercantagesByDay,
        };
        var w12Custom = SpawnQueryEngine.GetDayTierWeights(customExtendedMode, 12);
        Assert.Equal(0.0, w12Custom.Bronze, 6);
        Assert.Equal(0.20, w12Custom.Silver, 6);
        Assert.Equal(0.55, w12Custom.Gold, 6);
        Assert.Equal(0.25, w12Custom.Diamond, 6);
        Assert.Equal(1.00, w12Custom.Bronze + w12Custom.Silver + w12Custom.Gold + w12Custom.Diamond, 6);
    }

    // =========================================================================
    // P2-37：SocketPlacementHelper.TryPlaceSocketEffect strictIsolated 严格孤立 vs 默认回退
    // =========================================================================

    [Fact]
    public void P2_37_SocketPlacementHelper_StrictIsolatedRejectsAdjacentFallback()
    {
        // 在 10 槽棋盘的偶数槽位 0, 2, 4, 6, 8 均放入插槽效果后，剩余所有奇数槽 1, 3, 5, 7, 9 均与已有插槽紧邻 -> 无任何孤立空槽
        static CardInstance MakeSocket(string id, int slot)
        {
            var tpl = new CardTemplate
            {
                Id = id,
                InternalName = id,
                Type = "SocketEffect",
                Size = "Small",
                StartingTier = "Bronze"
            };
            var card = new CardInstance(tpl, CardTier.Bronze, null, id + "_inst");
            card.StartSlot = slot;
            return card;
        }

        var player = CreatePlayer("p_sock", HeroNames.Jules, 500);
        foreach (int occupied in new[] { 0, 2, 4, 6, 8 })
            player.SocketEffects.Add(MakeSocket($"occ_{occupied}", occupied));

        var strictCard = MakeSocket("new_strict", -1);
        bool strictPlaced = SocketPlacementHelper.TryPlaceSocketEffect(
            player, strictCard, avoidAdjacent: true, new Random(237), out _, strictIsolated: true);
        Assert.False(strictPlaced);
        Assert.DoesNotContain(strictCard, player.SocketEffects);

        var fallbackCard = MakeSocket("new_fallback", -1);
        bool fallbackPlaced = SocketPlacementHelper.TryPlaceSocketEffect(
            player, fallbackCard, avoidAdjacent: true, new Random(237), out _, strictIsolated: false);
        Assert.True(fallbackPlaced);
        Assert.Contains(fallbackCard, player.SocketEffects);
        Assert.True(fallbackCard.StartSlot is 1 or 3 or 5 or 7 or 9);
    }

    // =========================================================================
    // P2-43：High Impact & Escalation Protocol 异常百分比 Multiply 防御性兼容
    // =========================================================================

    [Fact]
    public void P2_43_LegacyPercentMultiplyCards_HighImpactAndEscalationProtocol_HandledSafely()
    {
        var repo = Repo.Value;

        // 1. High Impact (5b393f5e)：DamageAmount Multiply 25.0 -> +25% (100 -> 125，而非 2500)
        var highImpactTpl = repo.GetCard("5b393f5e-2c86-40c6-b210-1a215c1ccd51");
        Assert.NotNull(highImpactTpl);
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var weapon = CreateCustomItem("hi_wpn", "Weapon100", attrs: new() { ["DamageAmount"] = 100 }, tags: ["Weapon"]);
        var hiSkill = new CardInstance(highImpactTpl!, CardTier.Bronze, null, "hi_skill");
        p1.Hand.TryPlaceOrShift(weapon, 0);
        p1.Skills.Add(hiSkill);

        var hiCtx = new RuleExecutionContext(repo, RuleScope.InCombat, p1, null, new Random(243)).WithCards(hiSkill, hiSkill);
        var hiAb = hiSkill.ActiveAbilities.First(a => a.Action != null);
        Assert.True(AbilityExecutor.ExecuteAction(hiAb.Action, hiSkill, hiCtx));
        Assert.Equal(125.0, weapon.GetAttribute("DamageAmount"), 6);

        // 2. Escalation Protocol (f87817fd)：PercentCooldownReduction Multiply 5.0 -> Add 5.0（5000ms -> 4750ms）
        var escTpl = repo.GetCard("f87817fd-7a77-4bd6-8248-37e90a72c81d");
        Assert.NotNull(escTpl);
        var p2 = CreatePlayer("p2", HeroNames.Dooley, 500);
        var coreItem = CreateCustomItem("esc_core", "TestCore", attrs: new() { ["CooldownMax"] = 5000 }, tags: ["Core"]);
        var escCard = new CardInstance(escTpl!, CardTier.Bronze, null, "esc_card");
        p2.Hand.TryPlaceOrShift(coreItem, 0);

        var escCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, p2, null, new Random(243)).WithCards(escCard, escCard);
        var escAb = escCard.ActiveAbilities.First(a => a.Action != null);
        Assert.True(AbilityExecutor.ExecuteAction(escAb.Action, escCard, escCtx));
        Assert.Equal(5.0, coreItem.GetAttribute("PercentCooldownReduction"), 6);
        Assert.Equal(4750.0, coreItem.EffectiveCooldownMs, 6);
    }

    // =========================================================================
    // P2-44：TTriggerOnSandstorm subjectPlayer == null 且沙暴跳伤不触发 Flamberge OnPlayerDamaged
    // =========================================================================

    [Fact]
    public void P2_44_SandstormTrigger_NormalizesSubjectPlayerNull_AndDoesNotTriggerPlayerDamaged()
    {
        var repo = Repo.Value;
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);

        // 双方都放监听 TTriggerOnSandstorm 的卡（验证双方都能正常响应）+ 监听 TTriggerOnPlayerDamaged 的 Flamberge
        var sandListenerP1 = CreateCustomItem("sand_p1", "SandListenerP1", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnSandstorm"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"},"ReferenceValue":{"$type":"TFixedValue","Value":33}}""")
            }
        });
        var sandListenerP2 = CreateCustomItem("sand_p2", "SandListenerP2", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnSandstorm"}"""),
                Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"},"ReferenceValue":{"$type":"TFixedValue","Value":44}}""")
            }
        });
        // 若 subjectPlayer 仍错误传为 p1，则带 Subject=Self 的 P1 卡会被误放行而 P2 不会；规范为 null 后带 Subject 的都不应因硬编码 p1 偏置触发
        var subjectFilteredP1 = CreateCustomItem("sand_subj_p1", "SandSubjP1", abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnSandstorm","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self"}}"""),
                Action = ParseNode("""{"$type":"TActionPlayerShieldApply","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self"},"ReferenceValue":{"$type":"TFixedValue","Value":999}}""")
            }
        });
        var flambergeTpl = repo.GetCard("8938035e-be96-4827-bd34-7258253bd942")!;
        var flamberge = new CardInstance(flambergeTpl, CardTier.Bronze, null, "flam_p1");

        // 提前触发沙暴卡（500ms 时开启沙暴）
        var earlySand = CreateCustomItem("early_sand", "EarlySand", attrs: new() { ["CooldownMax"] = 500, ["AmmoMax"] = 1 }, abilities: new()
        {
            ["0"] = new AbilityDefinition
            {
                Id = "0",
                ActiveIn = "HandOnly",
                Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                Action = ParseNode("""{"$type":"TActionCardBeginSandstorm"}""")
            }
        });

        p1.Hand.TryPlaceOrShift(sandListenerP1, 0);
        p1.Hand.TryPlaceOrShift(subjectFilteredP1, 1);
        p1.Hand.TryPlaceOrShift(flamberge, 2);
        p1.Hand.TryPlaceOrShift(earlySand, 5);
        p2.Hand.TryPlaceOrShift(sandListenerP2, 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2440, recordTimeline: true, recordEvents: true);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == sandListenerP1.InstanceId && (int)e.Value == 33);
        Assert.Contains(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == sandListenerP2.InstanceId && (int)e.Value == 44);
        Assert.DoesNotContain(res.Events, e => e.Type == CombatEventType.ShieldApplied && e.SourceCardInstanceId == subjectFilteredP1.InstanceId);
        // 沙暴仅走 ApplyDirectDamage，不触发 Flamberge 的 OnPlayerDamaged 暴击叠加
        Assert.DoesNotContain(res.Events, e => e.Type == CombatEventType.CardAttributeModified && e.SourceCardInstanceId == flamberge.InstanceId && e.Detail == "CritChance");
    }

    // =========================================================================
    // P2-45：AuraEngine 5-Pass 收敛覆盖 4 跳级联依赖光环链
    // =========================================================================

    [Fact]
    public void P2_45_AuraEngine_5PassConvergence_Resolves4HopCascadingAuraChain()
    {
        var repo = Repo.Value;
        var player = CreatePlayer("p1", HeroNames.Vanessa, 500);

        // 构造 4 跳级联链（故意逆序摆放在 Hand 以确保需要完整 4 轮传播，3-Pass 无法收敛）：
        // E(Slot0) 读 D.Damage -> D(Slot1) 读 C.Damage -> C(Slot2) 读 B.Damage -> B(Slot3) 读 A.Damage(10)
        static AuraDefinition MakeReadRightDamageAura() => new()
        {
            Id = "0",
            ActiveIn = "HandOnly",
            Action = ParseNode("""
                {
                  "$type": "TAuraActionCardModifyAttribute",
                  "AttributeType": "DamageAmount",
                  "Operation": "Add",
                  "Value": {
                    "$type": "TReferenceValueCardAttribute",
                    "AttributeType": "DamageAmount",
                    "Target": { "$type": "TTargetCardPositional", "Origin": "Self", "TargetMode": "RightCard" }
                  },
                  "Target": { "$type": "TTargetCardSelf" }
                }
                """)
        };

        var cardE = CreateCustomItem("hop_e", "HopE", attrs: new() { ["DamageAmount"] = 1 }, auras: new() { ["0"] = MakeReadRightDamageAura() });
        var cardD = CreateCustomItem("hop_d", "HopD", attrs: new() { ["DamageAmount"] = 1 }, auras: new() { ["0"] = MakeReadRightDamageAura() });
        var cardC = CreateCustomItem("hop_c", "HopC", attrs: new() { ["DamageAmount"] = 1 }, auras: new() { ["0"] = MakeReadRightDamageAura() });
        var cardB = CreateCustomItem("hop_b", "HopB", attrs: new() { ["DamageAmount"] = 1 }, auras: new() { ["0"] = MakeReadRightDamageAura() });
        var cardA = CreateCustomItem("hop_a", "HopA", attrs: new() { ["DamageAmount"] = 10 });

        player.Hand.TryPlaceOrShift(cardE, 0);
        player.Hand.TryPlaceOrShift(cardD, 1);
        player.Hand.TryPlaceOrShift(cardC, 2);
        player.Hand.TryPlaceOrShift(cardB, 3);
        player.Hand.TryPlaceOrShift(cardA, 4);

        AuraEngine.RecalculateAuras(player, null, RuleScope.InCombat, player.Day, player.Hour, repo);

        // 收敛终值：A=10, B=11, C=12, D=13, E=14（3-Pass 时 E 仅能收敛到 4，5-Pass 必精准收敛至 14）
        Assert.Equal(10.0, cardA.GetAttribute("DamageAmount"));
        Assert.Equal(11.0, cardB.GetAttribute("DamageAmount"));
        Assert.Equal(12.0, cardC.GetAttribute("DamageAmount"));
        Assert.Equal(13.0, cardD.GetAttribute("DamageAmount"));
        Assert.Equal(14.0, cardE.GetAttribute("DamageAmount"));
    }

    // =========================================================================
    // P2-46：EnsureCombatBasePlayerEffect 自动注入 Base Tempo Gain Cooldown Effect 数据驱动抑制
    // =========================================================================

    [Fact]
    public void P2_46_BaseTempoGainCooldownEffect_InjectedAndDataDrivesPassiveTempoSuppression()
    {
        var repo = Repo.Value;
        const string baseTempoId = "e8478722-1851-4b05-9749-1480352ff710";
        Assert.NotNull(repo.GetCard(baseTempoId));

        // P1 无 Tempo 卡，P2 有带 Tempo HiddenTag 卡
        var p1 = CreatePlayer("p1", HeroNames.Vanessa, 2000);
        var p2 = CreatePlayer("p2", HeroNames.Vanessa, 2000);
        var plainItem = CreateCustomItem("plain_item", "PlainItem", attrs: new() { ["CooldownMax"] = 10000 });
        var tempoTpl = new CardTemplate
        {
            Id = "tempo_holder",
            InternalName = "TempoHolder",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            HiddenTags = ["Tempo"],
            Attributes = new() { ["CooldownMax"] = 10000 }
        };
        p1.Hand.TryPlaceOrShift(plainItem, 0);
        p2.Hand.TryPlaceOrShift(new CardInstance(tempoTpl, CardTier.Bronze, null, "tempo_inst"), 0);

        var res = CombatSimulator.Simulate(repo, p1, p2, seed: 2460, combatType: "PVP", recordTimeline: true, recordEvents: true, syncPermanentChanges: false, modifiers: null, sandstormEnabled: false);

        // 验证注入的 Base Tempo Gain Cooldown Effect 拥有确定性 InstanceId，并对无 Tempo 卡的 P1 施加了抑制光环，而未抑制持 Tempo 卡的 P2
        Assert.Contains(res.Events, e =>
            e.Type == CombatEventType.AuraExecuted
            && e.SourceCardInstanceId == $"sys_pe_p1_{baseTempoId}"
            && e.AuraAppliedPlayerIds != null
            && e.AuraAppliedPlayerIds.Contains("p1"));
        Assert.DoesNotContain(res.Events, e =>
            e.Type == CombatEventType.AuraExecuted
            && e.SourceCardInstanceId == $"sys_pe_p2_{baseTempoId}"
            && e.AuraAppliedPlayerIds != null
            && e.AuraAppliedPlayerIds.Contains("p2"));

        // P1 被该卡光环抑制：全程 Tempo == 0 且 TempoGainCooldownMax == 0
        var t5000 = res.Timeline.First(t => t.TimeMs >= 5000);
        Assert.Equal(0, t5000.P1Tempo);
        Assert.Equal(0, t5000.P1TempoGainCooldownMax);

        // P2 持 Tempo 卡，抑制光环自动失效：5000ms 时已自然获得 >= 2 点 Tempo，TempoGainCooldownMax == 2000
        Assert.True(t5000.P2Tempo >= 2, $"期望持 Tempo 卡方 5s 时被动获能 >= 2，实际 {t5000.P2Tempo}");
        Assert.Equal(2000, t5000.P2TempoGainCooldownMax);
    }

    // =========================================================================
    // P2-47：同帧双方就绪统一队列（先按 Ability.Priority 排序，同优先级通过 seed+帧号确定性破平）
    // =========================================================================

    [Fact]
    public void P2_47_SameFrameReadyQueue_RespectsAbilityPriority_AndFairlyBreaksTiesDeterministically()
    {
        var repo = Repo.Value;

        // 1. 优先级优先：P1 为 Low 优先级，P2 为 Highest 优先级，均在 1000ms 同帧就绪 -> 无论任何 seed，P2 必定先于 P1 结算
        foreach (int seed in new[] { 1, 2, 3, 42, 100 })
        {
            var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
            var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
            var p1Low = CreateCustomItem("p1_low", "P1Low", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 }, abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Priority = "Low",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
            var p2High = CreateCustomItem("p2_high", "P2High", attrs: new() { ["CooldownMax"] = 1000, ["DamageAmount"] = 10 }, abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Priority = "Highest",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionPlayerDamage","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent"}}""")
                }
            });
            p1.Hand.TryPlaceOrShift(p1Low, 0);
            p2.Hand.TryPlaceOrShift(p2High, 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: seed, recordTimeline: false, recordEvents: true);
            var firstTwoFires = res.Events.Where(e => e.Type == CombatEventType.CardFired && e.TimeMs == 950).ToList();
            Assert.Equal(2, firstTwoFires.Count);
            Assert.Equal(p2High.InstanceId, firstTwoFires[0].SourceCardInstanceId);
            Assert.Equal(p1Low.InstanceId, firstTwoFires[1].SourceCardInstanceId);
        }

        // 2. 同优先级公平破平：遍历 64 个不同 seed，双方各执一张 1000ms 同优先级摧毁卡，P1 与 P2 均有接近 50/50 的先手率，且同 seed 100% 确定复现
        int p1FirstCount = 0;
        int p2FirstCount = 0;
        for (int seed = 1; seed <= 64; seed++)
        {
            var p1 = CreatePlayer("p1", HeroNames.Vanessa, 500);
            var p2 = CreatePlayer("p2", HeroNames.Vanessa, 500);
            var d1 = CreateCustomItem("d1", "Destroyer1", attrs: new() { ["CooldownMax"] = 1000 }, abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Priority = "Medium",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardDestroy","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand"}}""")
                }
            });
            var d2 = CreateCustomItem("d2", "Destroyer2", attrs: new() { ["CooldownMax"] = 1000 }, abilities: new()
            {
                ["0"] = new AbilityDefinition
                {
                    Id = "0",
                    ActiveIn = "HandOnly",
                    Priority = "Medium",
                    Trigger = ParseNode("""{"$type":"TTriggerOnCardFired"}"""),
                    Action = ParseNode("""{"$type":"TActionCardDestroy","Target":{"$type":"TTargetCardSection","TargetSection":"OpponentHand"}}""")
                }
            });
            p1.Hand.TryPlaceOrShift(d1, 0);
            p2.Hand.TryPlaceOrShift(d2, 0);

            var res = CombatSimulator.Simulate(repo, p1, p2, seed: seed, recordTimeline: false, recordEvents: true);
            var firstFire = res.Events.First(e => e.Type == CombatEventType.CardFired);
            if (firstFire.SourcePlayerId == "p1") p1FirstCount++;
            else p2FirstCount++;

            if (seed == 7)
            {
                // 确定性复现检查
                var p1b = CreatePlayer("p1", HeroNames.Vanessa, 500);
                var p2b = CreatePlayer("p2", HeroNames.Vanessa, 500);
                p1b.Hand.TryPlaceOrShift(d1.CloneForCombat(), 0);
                p2b.Hand.TryPlaceOrShift(d2.CloneForCombat(), 0);
                var res2 = CombatSimulator.Simulate(repo, p1b, p2b, seed: 7, recordTimeline: false, recordEvents: true);
                Assert.Equal(firstFire.SourcePlayerId, res2.Events.First(e => e.Type == CombatEventType.CardFired).SourcePlayerId);
            }
        }

        Assert.True(p1FirstCount >= 18 && p2FirstCount >= 18,
            $"64 组不同种子下双方同帧破平应无单边固定偏置，实际 P1先手={p1FirstCount}, P2先手={p2FirstCount}");
    }

    // =========================================================================
    // 构造辅助方法
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

    private static CardInstance CreateCustomSkill(
        string id,
        string name,
        Dictionary<string, AbilityDefinition>? abilities = null)
    {
        var tpl = new CardTemplate
        {
            Id = id,
            InternalName = name,
            Type = "Skill",
            Size = "Small",
            StartingTier = "Bronze",
            Abilities = abilities ?? new Dictionary<string, AbilityDefinition>()
        };
        return new CardInstance(tpl, CardTier.Bronze, null, id + "_inst");
    }

    private static RunState.CardEntry MakeBoardEntry(string instanceId, CardInstance core, ECombatantId owner, EContainerSocketId socket)
    {
        var entry = new RunState.CardEntry
        {
            InstanceId = instanceId,
            TemplateId = Guid.TryParse(core.Template.Id, out var g) ? g : Guid.NewGuid(),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = owner,
            Section = EInventorySection.Hand,
            Socket = socket,
            CoreCard = core
        };
        if (core.EffectiveCooldownMs > 0)
            entry.Attributes[ECardAttributeType.CooldownMax] = (int)Math.Round(core.EffectiveCooldownMs);
        return entry;
    }

    private static RunState.CardEntry MakeSkillEntry(string instanceId, CardInstance core, ECombatantId owner)
        => new()
        {
            InstanceId = instanceId,
            TemplateId = Guid.TryParse(core.Template.Id, out var g) ? g : Guid.NewGuid(),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = owner,
            Section = EInventorySection.Hand,
            CoreCard = core
        };

    private static int GetStatValue(CombatSim sim, string instanceId, ECardStats stat)
    {
        if (sim.CardStats != null
            && sim.CardStats.TryGetValue(instanceId, out var dict)
            && dict.TryGetValue(stat, out int val))
        {
            return val;
        }
        return 0;
    }
}
