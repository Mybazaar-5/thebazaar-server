using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// V15.6 模块 B（quest-core）单元测试：
/// 1. 全库非调试任务卡（未完成态 + 全完成态，中英文）TooltipFormatter 渲染零 '?' 与零副作用；
/// 2. ResolveAbilityAction（Reward.Abilities + CompletionEffects）与 ResolveAuraAction（Reward.Auras）第 4 级回退；
/// 3. 公开辅助方法 FormatQuestGoalText / FormatQuestRewardText / FormatEnchantmentSummary / TryFindNonRepeatableQuestTarget；
/// 4. 不可重复已完成任务在 PvpGhostPool.ToPlayerState 与 RunState.SyncEntryFromCoreCardAndDiff 中自动补满 Quest_X >= Target；
/// 5. 可重复任务（EffectiveRepeatable == true）绝不被强行补满到 Target。
/// </summary>
public sealed class V15_6_QuestCoreTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);
    private static readonly Lazy<CardPool> Pool = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Pool);
    private readonly ITestOutputHelper _output;

    public V15_6_QuestCoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool IsDebugTemplate(CardTemplate tpl)
    {
        string name = tpl.InternalName ?? string.Empty;
        string title = tpl.DisplayTitle ?? string.Empty;
        return name.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)
            || title.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase);
    }

    private static List<CardTier> GetSupportedTiers(CardTemplate tpl)
    {
        var tiers = new List<CardTier>();
        if (tpl.Tiers != null)
        {
            foreach (var t in Enum.GetValues<CardTier>())
            {
                if (tpl.Tiers.ContainsKey(t.ToString()))
                    tiers.Add(t);
            }
        }
        if (tiers.Count == 0)
            tiers.Add(tpl.DefaultTier);
        return tiers;
    }

    private static RuleNode ParseNode(string json)
        => JsonSerializer.Deserialize<RuleNode>(json, GameDatabaseLoader.JsonOptions)!;

    // =========================================================================
    // 1. 全库非调试任务卡：未完成态 + 全完成态，任意 Tooltip 行 0 个 '?'
    // =========================================================================

    [Fact]
    public void AllNonDebugQuestCards_IncompleteAndCompleted_HaveZeroQuestionMarks_AndNoSideEffects()
    {
        var repo = Repo.Value;
        var allQuestCards = repo.CardsById.Values
            .Where(c => c.Quests is { Count: > 0 })
            .OrderBy(c => c.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nonDebugQuestCards = allQuestCards.Where(c => !IsDebugTemplate(c)).ToList();

        Assert.True(nonDebugQuestCards.Count >= 33, $"期望至少 33 张非调试任务卡，实际 {nonDebugQuestCards.Count}");

        var questionMarkFailures = new List<string>();
        int totalLinesChecked = 0;

        foreach (var tpl in nonDebugQuestCards)
        {
            var tiers = GetSupportedTiers(tpl);
            foreach (var tier in tiers)
            {
                // A. 未完成态：校验零 '?' 且原卡绝不被预览副本修改
                var incompleteCard = new CardInstance(tpl, tier);
                int origCompletedCount = incompleteCard.CompletedQuests.Count;
                int origActiveAbilitiesCount = incompleteCard.ActiveAbilities.Count;
                int origActiveAurasCount = incompleteCard.ActiveAuras.Count;
                var origBaseSnapshot = new Dictionary<string, double>(incompleteCard.BaseAttributes, StringComparer.OrdinalIgnoreCase);

                var zhIncompleteLines = TooltipFormatter.FormatTooltips(incompleteCard, null, repo.Localization);
                var enIncompleteLines = TooltipFormatter.FormatTooltips(incompleteCard, null, null);

                Assert.Equal(origCompletedCount, incompleteCard.CompletedQuests.Count);
                Assert.Equal(origActiveAbilitiesCount, incompleteCard.ActiveAbilities.Count);
                Assert.Equal(origActiveAurasCount, incompleteCard.ActiveAuras.Count);
                Assert.Equal(origBaseSnapshot.Count, incompleteCard.BaseAttributes.Count);
                foreach (var (bk, bv) in origBaseSnapshot)
                    Assert.Equal(bv, incompleteCard.BaseAttributes[bk], precision: 6);

                CheckLines(tpl, tier, "incomplete-zh", zhIncompleteLines, questionMarkFailures, ref totalLinesChecked);
                CheckLines(tpl, tier, "incomplete-en", enIncompleteLines, questionMarkFailures, ref totalLinesChecked);

                // B. 全完成态：将全部不可重复任务标记完成并重建，校验零 '?'
                var completedCard = new CardInstance(tpl, tier);
                foreach (var group in tpl.Quests!)
                {
                    if (group.EffectiveRepeatable) continue;
                    foreach (var entry in group.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.AttributeType)) continue;
                        completedCard.CompletedQuests.Add(entry.AttributeType);
                        completedCard.QuestProgress[entry.AttributeType] = Math.Max(1, entry.Target);
                    }
                }
                completedCard.RebuildTierAndEnchantmentData();

                var zhCompletedLines = TooltipFormatter.FormatTooltips(completedCard, null, repo.Localization);
                var enCompletedLines = TooltipFormatter.FormatTooltips(completedCard, null, null);

                CheckLines(tpl, tier, "completed-zh", zhCompletedLines, questionMarkFailures, ref totalLinesChecked);
                CheckLines(tpl, tier, "completed-en", enCompletedLines, questionMarkFailures, ref totalLinesChecked);
            }
        }

        _output.WriteLine($"GameData.db 任务卡总数={allQuestCards.Count}，非调试任务卡数={nonDebugQuestCards.Count}，累计校验 Tooltip 行数={totalLinesChecked}，含 '?' 行数={questionMarkFailures.Count}");
        Assert.True(questionMarkFailures.Count == 0,
            $"存在 {questionMarkFailures.Count} 条含 '?' 的任务卡 Tooltip：\n" + string.Join("\n", questionMarkFailures.Take(30)));
    }

    private static void CheckLines(
        CardTemplate tpl,
        CardTier tier,
        string mode,
        List<string> lines,
        List<string> failures,
        ref int totalLinesChecked)
    {
        foreach (string line in lines)
        {
            totalLinesChecked++;
            if (line.Contains('?', StringComparison.Ordinal))
            {
                failures.Add($"[{tpl.InternalName} ({tpl.Id}) | {tier} | {mode}] {line}");
            }
        }
    }

    // =========================================================================
    // 2. B1 公开辅助方法：FormatQuestGoalText / FormatQuestRewardText / FormatEnchantmentSummary / TryFindNonRepeatableQuestTarget
    // =========================================================================

    [Fact]
    public void PublicFormatHelpers_GoalRewardAndEnchantmentSummary_RenderAccuratelyWithoutQuestionMarks()
    {
        var repo = Repo.Value;

        // 2a. 抽检重点任务卡（Blank Slate / Soulstone / Sands of Time / Waystones）的目标与奖励文本
        string[] keyCardNames = ["Blank Slate", "Soulstone", "Sands of Time", "Waystones", "Tour Bus"];
        int testedQuestEntries = 0;
        foreach (string name in keyCardNames)
        {
            var tpl = repo.CardsById.Values.FirstOrDefault(c =>
                string.Equals(c.InternalName, name, StringComparison.OrdinalIgnoreCase) && c.Quests is { Count: > 0 });
            Assert.NotNull(tpl);

            var card = new CardInstance(tpl!, tpl!.DefaultTier);
            foreach (var group in tpl.Quests!)
            {
                foreach (var entry in group.Entries)
                {
                    string goalZh = TooltipFormatter.FormatQuestGoalText(card, entry, repo.Localization);
                    string rewardZh = TooltipFormatter.FormatQuestRewardText(card, entry, repo.Localization);

                    Assert.False(string.IsNullOrWhiteSpace(goalZh), $"{name} {entry.AttributeType} goalZh 不应为空");
                    Assert.DoesNotContain("?", goalZh);
                    if (entry.Reward?.Localization != null)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(rewardZh), $"{name} {entry.AttributeType} rewardZh 不应为空");
                        Assert.DoesNotContain("?", rewardZh);
                    }
                    testedQuestEntries++;
                }
            }
            // 未完成预览绝不污染原卡
            Assert.Empty(card.CompletedQuests);
        }
        Assert.True(testedQuestEntries >= 15);

        // 2b. FormatEnchantmentSummary：验证真实卡牌附魔中文摘要、大小写不敏感、不带 "[附魔名]" 前缀、无效附魔返回空
        var enchCardTpl = repo.CardsById.Values.FirstOrDefault(c =>
            c.Enchantments != null
            && c.Enchantments.ContainsKey("Fiery")
            && c.Enchantments["Fiery"].Localization?.Tooltips is { Count: > 0 });
        Assert.NotNull(enchCardTpl);

        string fieryZh = TooltipFormatter.FormatEnchantmentSummary(enchCardTpl!, "Fiery", enchCardTpl!.DefaultTier, repo.Localization);
        string fieryLowerZh = TooltipFormatter.FormatEnchantmentSummary(enchCardTpl!, "fiery", enchCardTpl!.DefaultTier, repo.Localization);
        Assert.False(string.IsNullOrWhiteSpace(fieryZh));
        Assert.Equal(fieryZh, fieryLowerZh);
        Assert.DoesNotContain("[烈焰]", fieryZh);
        Assert.DoesNotContain("?", fieryZh);
        Assert.Equal(string.Empty, TooltipFormatter.FormatEnchantmentSummary(enchCardTpl!, "NonExistentEnchant", enchCardTpl!.DefaultTier, repo.Localization));
    }

    [Fact]
    public void FourthLevelFallback_ResolvesCompletionEffectsAndQuestRewardAurasAndAbilities()
    {
        var customTpl = new CardTemplate
        {
            Id = "00000000-0000-0000-0000-00000000b101",
            InternalName = "CustomQuestFallbackProbe",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData>
            {
                ["Bronze"] = new TierData()
            },
            Quests =
            [
                new QuestDefinition
                {
                    IsRepeatable = false,
                    Entries =
                    [
                        new QuestEntry
                        {
                            AttributeType = "Quest_1",
                            Target = 3,
                            Localization = new LocalizationBlock
                            {
                                Tooltips =
                                [
                                    new TooltipEntry
                                    {
                                        Content = new LocalizedText { Text = "完成时造成 {ability.comp_1} 伤害" }
                                    }
                                ]
                            },
                            CompletionEffects = new Dictionary<string, AbilityDefinition>
                            {
                                ["comp_1"] = new AbilityDefinition
                                {
                                    Id = "comp_1",
                                    Action = ParseNode("""{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":42}}""")
                                }
                            },
                            Reward = new QuestReward
                            {
                                Attributes = new Dictionary<string, double>
                                {
                                    ["BurnApplyAmount"] = 7
                                },
                                Abilities = new Dictionary<string, AbilityDefinition>
                                {
                                    ["q_ab1"] = new AbilityDefinition
                                    {
                                        Id = "q_ab1",
                                        Action = ParseNode("""{"$type":"TActionPlayerBurnApply"}""")
                                    }
                                },
                                Auras = new Dictionary<string, AuraDefinition>
                                {
                                    ["q_au1"] = new AuraDefinition
                                    {
                                        Id = "q_au1",
                                        Action = ParseNode("""{"$type":"TAuraActionCardModifyAttribute","AttributeType":"DamageAmount","Operation":"Add","Value":{"$type":"TFixedValue","Value":15}}""")
                                    }
                                },
                                Localization = new LocalizationBlock
                                {
                                    Tooltips =
                                    [
                                        new TooltipEntry
                                        {
                                            Content = new LocalizedText { Text = "造成 {ability.q_ab1} 灼烧并附加 +{aura.q_au1} 伤害" }
                                        }
                                    ]
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var card = new CardInstance(customTpl, CardTier.Bronze);
        var entry = customTpl.Quests![0].Entries[0];

        string goalText = TooltipFormatter.FormatQuestGoalText(card, entry);
        string rewardText = TooltipFormatter.FormatQuestRewardText(card, entry);

        Assert.Equal("完成时造成 42 伤害", goalText);
        Assert.Equal("造成 7 灼烧并附加 +15 伤害", rewardText);
        // 断言未完成预览绝不修改原卡状态
        Assert.Empty(card.CompletedQuests);
        Assert.False(card.BaseAttributes.ContainsKey("BurnApplyAmount"));
    }

    // =========================================================================
    // 3. B2：不可重复已完成任务同步（RunState + PvpGhostPool）与可重复任务豁免
    // =========================================================================

    [Fact]
    public void NonRepeatableCompletedQuest_SyncsQuestAttrToTarget_AndFillsQuestProgressInToPlayerState()
    {
        var repo = Repo.Value;
        var pool = Pool.Value;

        // 选取真实不可重复任务卡（Blank Slate，Quest_1 为不可重复任务）
        var tpl = repo.CardsById.Values.First(c =>
            string.Equals(c.InternalName, "Blank Slate", StringComparison.OrdinalIgnoreCase));
        Assert.True(TooltipFormatter.TryFindNonRepeatableQuestTarget(tpl, "Quest_1", out double expectedTarget));
        Assert.True(TooltipFormatter.TryFindNonRepeatableQuestTarget(tpl, "quest_1", out double caseInsensitiveTarget));
        Assert.Equal(expectedTarget, caseInsensitiveTarget);
        Assert.True(expectedTarget >= 1);

        // 3a. RunState.SyncEntryFromCoreCardAndDiff：仅设 CompletedQuests，不写 QuestProgress
        var run = new RunState { Hero = EHero.Vanessa };
        var core = new CardInstance(tpl, tpl.DefaultTier, null, "itm_quest_sync_1")
        {
            StartSlot = 0
        };
        core.CompletedQuests.Add("Quest_1");
        core.RebuildTierAndEnchantmentData();
        Assert.Empty(core.QuestProgress);

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tpl.StartingTier),
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
        };
        entry.BindAlignedCoreCard(core);
        RunState.PopulateMetaFromTemplate(entry, pool.Get(tpl.Id), tpl);

        var events = run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        Assert.True(entry.Attributes.TryGetValue(ECardAttributeType.Quest_1, out int syncedQuest1),
            "仅设置 CompletedQuests 时，SyncEntryFromCoreCardAndDiff 必须下发 Attributes[Quest_1]");
        Assert.True(syncedQuest1 >= (int)Math.Round(expectedTarget),
            $"期望 Attributes[Quest_1] >= {expectedTarget}，实际 {syncedQuest1}");
        Assert.Contains(events.OfType<GameSimEventCardQuestUpdated>(), e => e.InstanceId == entry.InstanceId);
        Assert.Contains(events.OfType<GameSimEventCardQuestCompleted>(), e => e.InstanceId == entry.InstanceId);

        // 3b. PvpGhostPool.ToPlayerState：仅带 CompletedQuests 时自动补满 QuestProgress
        var ghost = new PvpGhostPool.GhostBoard
        {
            BoardId = "v15_6_quest_ghost",
            Day = 5,
            Hero = HeroNames.Mak,
            Level = 5,
            HealthMax = 1000,
            Prestige = 20,
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    TemplateId = tpl.Id,
                    Tier = tpl.DefaultTier.ToString(),
                    Slot = 0,
                    Width = tpl.CardSize.SlotCount(),
                    CompletedQuests = ["Quest_1"],
                    QuestProgress = null
                }
            ]
        };

        var ps = PvpGhostPool.ToPlayerState(ghost, repo);
        var restored = Assert.Single(ps.Hand.Cards);
        Assert.Contains("Quest_1", restored.CompletedQuests);
        Assert.True(restored.QuestProgress.TryGetValue("Quest_1", out double restoredProgress));
        Assert.Equal(expectedTarget, restoredProgress, precision: 3);

        // 若原本已有高于 Target 的 QuestProgress，不应被向下覆盖
        var ghostHigher = new PvpGhostPool.GhostBoard
        {
            BoardId = "v15_6_quest_ghost_higher",
            Day = 5,
            Hero = HeroNames.Mak,
            Level = 5,
            HealthMax = 1000,
            Prestige = 20,
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    TemplateId = tpl.Id,
                    Tier = tpl.DefaultTier.ToString(),
                    Slot = 0,
                    Width = tpl.CardSize.SlotCount(),
                    CompletedQuests = ["Quest_1"],
                    QuestProgress = new Dictionary<string, double> { ["Quest_1"] = expectedTarget + 4 }
                }
            ]
        };
        var psHigher = PvpGhostPool.ToPlayerState(ghostHigher, repo);
        var restoredHigher = Assert.Single(psHigher.Hand.Cards);
        Assert.Equal(expectedTarget + 4, restoredHigher.QuestProgress["Quest_1"], precision: 3);
    }

    [Fact]
    public void RepeatableQuest_IsNeverForcedToTarget_InHelperSyncOrToPlayerState()
    {
        var repo = Repo.Value;

        // 覆盖两种可重复标记：IsRepeatable = true 与旧对象型 Repeatable != null
        var repeatableTpl = new CardTemplate
        {
            Id = "11111111-1111-1111-1111-11111111b201",
            InternalName = "SyntheticRepeatableQuestCard",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Tiers = new Dictionary<string, TierData> { ["Bronze"] = new TierData() },
            Quests =
            [
                new QuestDefinition
                {
                    IsRepeatable = true,
                    Entries =
                    [
                        new QuestEntry { AttributeType = "Quest_1", Target = 5 }
                    ]
                },
                new QuestDefinition
                {
                    Repeatable = new QuestRepeatableData(),
                    Entries =
                    [
                        new QuestEntry { AttributeType = "Quest_2", Target = 7 }
                    ]
                },
                new QuestDefinition
                {
                    IsRepeatable = false,
                    Entries =
                    [
                        new QuestEntry { AttributeType = "Quest_3", Target = 0 } // Target < 1 需钳制为 1
                    ]
                }
            ]
        };

        // TryFindNonRepeatableQuestTarget：严格跳过可重复组，仅命中不可重复组并下限钳制为 1
        Assert.False(TooltipFormatter.TryFindNonRepeatableQuestTarget(repeatableTpl, "Quest_1", out _));
        Assert.False(TooltipFormatter.TryFindNonRepeatableQuestTarget(repeatableTpl, "Quest_2", out _));
        Assert.True(TooltipFormatter.TryFindNonRepeatableQuestTarget(repeatableTpl, "Quest_3", out double clampedTarget));
        Assert.Equal(1.0, clampedTarget, precision: 3);

        // SyncEntryFromCoreCardAndDiff：可重复任务即使出现在 CompletedQuests 中，也不得强行抬高至 Target
        var run = new RunState { Hero = EHero.Vanessa };
        var core = new CardInstance(repeatableTpl, CardTier.Bronze, null, "itm_rep_1") { StartSlot = 0 };
        core.CompletedQuests.Add("Quest_1");
        core.CompletedQuests.Add("Quest_2");
        core.QuestProgress["Quest_1"] = 2; // 低于 Target=5

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(repeatableTpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
        };
        entry.BindAlignedCoreCard(core);

        run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);

        Assert.Equal(2, entry.Attributes[ECardAttributeType.Quest_1]);
        Assert.False(entry.Attributes.TryGetValue(ECardAttributeType.Quest_2, out int q2Val) && q2Val > 0,
            $"可重复任务 Quest_2 不应被补满到 Target=7，实际 {q2Val}");

        // 使用真实 GameData.db 中的可重复任务卡（若存在，如 [DEBUG] Repeatable Quest）验证 PvpGhostPool.ToPlayerState 不补满
        var dbRepeatableTpl = repo.CardsById.Values.FirstOrDefault(c =>
            c.Quests != null && c.Quests.Any(q => q.EffectiveRepeatable && q.Entries.Count > 0));
        Assert.NotNull(dbRepeatableTpl);
        var repEntry = dbRepeatableTpl!.Quests!.First(q => q.EffectiveRepeatable && q.Entries.Count > 0).Entries[0];
        Assert.False(TooltipFormatter.TryFindNonRepeatableQuestTarget(dbRepeatableTpl, repEntry.AttributeType, out _));

        var repGhost = new PvpGhostPool.GhostBoard
        {
            BoardId = "v15_6_rep_ghost",
            Day = 3,
            Hero = HeroNames.Vanessa,
            Level = 3,
            HealthMax = 500,
            Prestige = 20,
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    TemplateId = dbRepeatableTpl.Id,
                    Tier = dbRepeatableTpl.DefaultTier.ToString(),
                    Slot = 0,
                    Width = dbRepeatableTpl.CardSize.SlotCount(),
                    CompletedQuests = [repEntry.AttributeType],
                    QuestProgress = null
                }
            ]
        };

        var repPs = PvpGhostPool.ToPlayerState(repGhost, repo);
        var repCard = Assert.Single(repPs.Hand.Cards);
        Assert.Equal(0.0, repCard.QuestProgress.GetValueOrDefault(repEntry.AttributeType, 0), precision: 3);
    }
}
