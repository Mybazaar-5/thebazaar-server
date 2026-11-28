using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Planner;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarCore.Tests;

/// <summary>
/// V15.6 模块 A2（校验器顺序对齐）、模块 C（编排台任务进度/完成态）、模块 D（Tiny 缩格、9 种 SocketEffect、状态开关、ExtraTags、附魔摘要）
/// 及编排台 REST API 端到端契约自动化测试。
/// </summary>
public sealed class V15_6_PlannerQuestsAndEffectsTests : IClassFixture<SharedGameDataFixture>
{
    private readonly SharedGameDataFixture _fx;

    public V15_6_PlannerQuestsAndEffectsTests(SharedGameDataFixture fx)
    {
        _fx = fx;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. 模块 A2：旧格式平铺 PermanentAttributes + CompletedQuests 连续校验两次 100% 幂等且与 ToPlayerState 一致
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleA2_LegacyFlatAttributesWithCompletedQuests_DoubleValidationIdempotentAndMatchesToPlayerState()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        // 在真实 GameData.db 中选取一张「已完成不可重复任务奖励会直接增加数量类输出属性」的物品卡
        CardTemplate? chosenTpl = null;
        string chosenTier = "Bronze";
        string chosenQuestAttr = "";
        double chosenQuestTarget = 1;
        string chosenRewardAttr = "";
        int baseWithoutQuest = 0;
        int baseWithQuest = 0;

        foreach (var tpl in repo.AllItems)
        {
            if (tpl.Quests is not { Count: > 0 }) continue;
            if (!pool.IsAssetValid(tpl.Id) || MonsterPool.IsBlockedDebugOrBlankArtSkill(tpl)) continue;
            if ((tpl.InternalName ?? "").Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)) continue;

            string tierName = GhostBoardValidator.GetSupportedTiers(tpl)[0];
            var coreTier = TierHelper.ParseTier(tierName);
            var cardBefore = new CardInstance(tpl, coreTier, null, "before");

            foreach (var group in tpl.Quests)
            {
                if (group == null || group.EffectiveRepeatable || group.Entries == null) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.AttributeType)) continue;
                    var cardAfter = new CardInstance(tpl, coreTier, null, "after");
                    cardAfter.CompletedQuests.Add(entry.AttributeType);
                    cardAfter.RebuildTierAndEnchantmentData();

                    foreach (string qtyKey in PlannerCatalogConstants.QuantityAttributes)
                    {
                        if (string.Equals(qtyKey, "CooldownMax", StringComparison.OrdinalIgnoreCase)) continue;
                        int b0 = (int)Math.Round(cardBefore.BaseAttributes.GetValueOrDefault(qtyKey, 0));
                        int b1 = (int)Math.Round(cardAfter.BaseAttributes.GetValueOrDefault(qtyKey, 0));
                        if (b1 > b0)
                        {
                            chosenTpl = tpl;
                            chosenTier = tierName;
                            chosenQuestAttr = entry.AttributeType;
                            chosenQuestTarget = Math.Max(1.0, Math.Round(entry.Target));
                            chosenRewardAttr = qtyKey;
                            baseWithoutQuest = b0;
                            baseWithQuest = b1;
                            break;
                        }
                    }
                    if (chosenTpl != null) break;
                }
                if (chosenTpl != null) break;
            }
            if (chosenTpl != null) break;
        }

        Assert.NotNull(chosenTpl);
        Assert.True(baseWithQuest > baseWithoutQuest, "必须找到任务奖励直接叠加基础输出属性的真实任务卡");

        // 构造「旧格式平铺 PermanentAttributes（最终绝对值 = 任务后基础值 + 25）+ CompletedQuests，无 PermanentAdd/Mult」盘面
        int targetFinalValue = baseWithQuest + 25;
        var legacyBoard = new PvpGhostPool.GhostBoard
        {
            BoardId = "legacy_flat_quest_board",
            Day = 5,
            Hero = "Vanessa",
            Level = 6,
            HealthMax = 1000,
            Prestige = 20,
            Source = "custom",
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 0,
                    Width = chosenTpl!.CardSize.SlotCount(),
                    TemplateId = chosenTpl.Id,
                    Tier = chosenTier,
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        [chosenRewardAttr] = targetFinalValue,
                    },
                    PermanentAdd = null,
                    PermanentMult = null,
                    CompletedQuests = new List<string> { chosenQuestAttr },
                    QuestProgress = null,
                },
            },
        };

        // 第 1 次校验：先挂任务重建，再反解 delta = 25（绝不把任务奖励双倍叠算！）
        var res1 = GhostBoardValidator.ValidateAndNormalize(5, legacyBoard, repo, pool);
        Assert.True(res1.Ok, $"第 1 次校验失败：{string.Join("; ", res1.Errors)}");
        var item1 = Assert.Single(res1.Board.Items);
        Assert.Equal(targetFinalValue, item1.PermanentAttributes[chosenRewardAttr]);
        Assert.NotNull(item1.PermanentAdd);
        Assert.Equal(25.0, item1.PermanentAdd![chosenRewardAttr], 3);
        Assert.NotNull(item1.CompletedQuests);
        Assert.Contains(chosenQuestAttr, item1.CompletedQuests!);
        Assert.NotNull(item1.QuestProgress);
        Assert.Equal(chosenQuestTarget, item1.QuestProgress![chosenQuestAttr], 3);

        // 第 2 次校验（把第 1 次输出原样再提交）：100% 幂等、零漂移
        var res2 = GhostBoardValidator.ValidateAndNormalize(5, res1.Board, repo, pool);
        Assert.True(res2.Ok, $"第 2 次校验失败：{string.Join("; ", res2.Errors)}");
        var item2 = Assert.Single(res2.Board.Items);
        Assert.Equal(item1.PermanentAttributes[chosenRewardAttr], item2.PermanentAttributes[chosenRewardAttr]);
        Assert.Equal(item1.PermanentAdd![chosenRewardAttr], item2.PermanentAdd![chosenRewardAttr], 6);
        Assert.Equal(item1.CompletedQuests, item2.CompletedQuests);
        Assert.Equal(item1.QuestProgress![chosenQuestAttr], item2.QuestProgress![chosenQuestAttr], 6);

        // 与 PvpGhostPool.ToPlayerState 还原结果严格一致
        var ps = PvpGhostPool.ToPlayerState(res1.Board, repo);
        var coreCard = Assert.Single(ps.Hand.Cards);
        Assert.True(coreCard.IsQuestCompleted(chosenQuestAttr));
        Assert.True(coreCard.GetQuestProgress(chosenQuestAttr) >= chosenQuestTarget);
        Assert.Equal(targetFinalValue, (int)Math.Round(coreCard.GetAttribute(chosenRewardAttr)));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. 模块 C：任务清洗（无任务卡/白名单大小写/双向对齐/互斥防呆/可重复任务/editedAttributes 幂等）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleC_SanitizeItemQuests_AllRulesAndIdempotency()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        // 2a) 无任务卡携带任务字段 -> 忽略并给出中文 warning
        var plainItem = PickValidItem(repo, pool, c => (c.Quests == null || c.Quests.Count == 0) && c.CardSize == CardSize.Small);
        var plainBoard = MakeBoardInput(day: 4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = plainItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(plainItem)[0],
            CompletedQuests = new List<string> { "Quest_1" },
            QuestProgress = new Dictionary<string, double> { ["Quest_1"] = 3 },
        });
        var plainRes = GhostBoardValidator.ValidateAndNormalize(4, plainBoard, repo, pool);
        Assert.True(plainRes.Ok, string.Join("; ", plainRes.Errors));
        Assert.Null(plainRes.Board.Items[0].CompletedQuests);
        Assert.Null(plainRes.Board.Items[0].QuestProgress);
        Assert.Contains(plainRes.Warnings, w => w.Contains("没有任务机制", StringComparison.Ordinal));

        // 2b) 不可重复互斥多分支任务卡（如 Soulstone 等：!EffectiveRepeatable && Entries.Count > 1）
        var exclusiveItem = PickValidItem(repo, pool, c =>
            c.Quests != null &&
            c.Quests.Any(g => !g.EffectiveRepeatable && g.Entries != null && g.Entries.Count >= 2));
        var exGroup = exclusiveItem.Quests!.First(g => !g.EffectiveRepeatable && g.Entries.Count >= 2);
        string branchA = exGroup.Entries[0].AttributeType;
        string branchB = exGroup.Entries[1].AttributeType;
        double targetA = Math.Max(1.0, Math.Round(exGroup.Entries[0].Target));

        var exInput = MakeBoardInput(day: 5, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = exclusiveItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(exclusiveItem)[0],
            CompletedQuests = new List<string> { branchA.ToLowerInvariant(), branchB, "Quest_99" },
            QuestProgress = new Dictionary<string, double>
            {
                [branchB] = Math.Max(1.0, Math.Round(exGroup.Entries[1].Target)),
                ["Quest_88"] = 5,
            },
            EditedAttributes = new Dictionary<string, double> { ["DamageAmount"] = 77 },
        });

        var exRes1 = GhostBoardValidator.ValidateAndNormalize(5, exInput, repo, pool);
        Assert.True(exRes1.Ok, string.Join("; ", exRes1.Errors));
        var exOut1 = Assert.Single(exRes1.Board.Items);
        Assert.Equal(new[] { branchA }, exOut1.CompletedQuests); // 大小写归一且保留首个互斥分支
        Assert.NotNull(exOut1.QuestProgress);
        Assert.Equal(targetA, exOut1.QuestProgress![branchA], 3); // 双向补满 target
        Assert.False(exOut1.QuestProgress.ContainsKey(branchB));  // 清除冲突分支进度
        Assert.Contains(exRes1.Warnings, w => w.Contains("互斥任务分支", StringComparison.Ordinal));
        Assert.Contains(exRes1.Warnings, w => w.Contains("Quest_99", StringComparison.Ordinal));
        Assert.Equal(77, exOut1.PermanentAttributes["DamageAmount"]);

        // 二次保存幂等（零 warning、结果完全一致）
        var exRes2 = GhostBoardValidator.ValidateAndNormalize(5, exRes1.Board, repo, pool);
        Assert.True(exRes2.Ok, string.Join("; ", exRes2.Errors));
        var exOut2 = Assert.Single(exRes2.Board.Items);
        Assert.Equal(exOut1.CompletedQuests, exOut2.CompletedQuests);
        Assert.Equal(exOut1.QuestProgress![branchA], exOut2.QuestProgress![branchA], 6);
        Assert.Equal(exOut1.PermanentAttributes["DamageAmount"], exOut2.PermanentAttributes["DamageAmount"]);
        Assert.Equal(exOut1.PermanentAdd!["DamageAmount"], exOut2.PermanentAdd!["DamageAmount"], 6);

        // 2c) 仅填部分进度（< target）不会误标为已完成；进度 >= target 自动双向加入 CompletedQuests
        var multiTargetItem = PickValidItem(repo, pool, c =>
            c.Quests != null &&
            c.Quests.Any(g => !g.EffectiveRepeatable && g.Entries.Any(e => e.Target >= 3)));
        var mtEntry = multiTargetItem.Quests!
            .Where(g => !g.EffectiveRepeatable)
            .SelectMany(g => g.Entries)
            .First(e => e.Target >= 3);
        double mtTarget = Math.Round(mtEntry.Target);

        var partialRes = GhostBoardValidator.ValidateAndNormalize(4, MakeBoardInput(4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = multiTargetItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(multiTargetItem)[0],
            QuestProgress = new Dictionary<string, double> { [mtEntry.AttributeType] = 2 },
        }), repo, pool);
        Assert.True(partialRes.Ok);
        Assert.Null(partialRes.Board.Items[0].CompletedQuests);
        Assert.Equal(2.0, partialRes.Board.Items[0].QuestProgress![mtEntry.AttributeType], 3);

        var reachTargetRes = GhostBoardValidator.ValidateAndNormalize(4, MakeBoardInput(4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = multiTargetItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(multiTargetItem)[0],
            QuestProgress = new Dictionary<string, double> { [mtEntry.AttributeType] = mtTarget + 10 },
        }), repo, pool);
        Assert.True(reachTargetRes.Ok);
        Assert.Equal(new[] { mtEntry.AttributeType }, reachTargetRes.Board.Items[0].CompletedQuests);
        Assert.Equal(mtTarget, reachTargetRes.Board.Items[0].QuestProgress![mtEntry.AttributeType], 3);

        // 2d) 可重复任务（EffectiveRepeatable == true）：绝不锁入 CompletedQuests，进度钳制至 0..max(0, target - 1)
        var repTpl = repo.AllItems.FirstOrDefault(c =>
            c.Quests != null &&
            c.Quests.Any(g => g.EffectiveRepeatable && g.Entries.Any(e => e.Target >= 2)));
        // 若真库可重复任务卡属于调试卡或 target<2，用纯模板构造直测 SanitizeItemQuests 同样覆盖完整边界
        var testRepTpl = repTpl ?? new CardTemplate
        {
            Id = "test_rep_quest_card",
            Type = "Item",
            InternalName = "TestRepeatableQuestCard",
            Quests = new List<QuestDefinition>
            {
                new()
                {
                    IsRepeatable = true,
                    Entries = new List<QuestEntry>
                    {
                        new() { AttributeType = "Quest_1", Target = 5 },
                    },
                },
            },
        };
        var repEntry = testRepTpl.Quests!.First(g => g.EffectiveRepeatable).Entries[0];
        double repMax = Math.Max(0.0, Math.Round(repEntry.Target) - 1.0);
        var repErrors = new List<string>();
        var repWarnings = new List<string>();
        var (repComp, repProg) = GhostBoardValidator.SanitizeItemQuests(
            testRepTpl,
            "可重复任务卡",
            new List<string> { repEntry.AttributeType },
            new Dictionary<string, double> { [repEntry.AttributeType] = Math.Round(repEntry.Target) + 3 },
            repErrors,
            repWarnings);
        Assert.Empty(repErrors);
        Assert.Empty(repComp);
        Assert.Contains(repWarnings, w => w.Contains("可重复任务", StringComparison.Ordinal));
        if (repMax > 0)
            Assert.Equal(repMax, repProg[repEntry.AttributeType], 3);
        else
            Assert.Empty(repProg);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. 模块 D1：Tiny 缩格（Large→2、Medium→1、Small 拒缩、掩码联动、历史 ExtraTags 兼容与 RunState 同步）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleD1_Tiny_Validation_Width_Mask_LegacyExtraTag_AndRunStateSync()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        var largeItem = PickValidItem(repo, pool, c => c.CardSize == CardSize.Large);
        var mediumItem = PickValidItem(repo, pool, c => c.CardSize == CardSize.Medium);
        var smallItem = PickValidItem(repo, pool, c => c.CardSize == CardSize.Small);
        string smallZh = GhostBoardValidator.ResolveCardNameZh(smallItem, repo);

        // 3a) Large 放槽位 8 + 开启 Tiny → 有效宽 2 格（占 8..9 不越界！），写入 PermanentAdd["Tiny"]=1 & PermanentAttributes["Tiny"]=1，不塞 ExtraTags
        var tinyBoard = MakeBoardInput(day: 1,
            new PlannerItemInput
            {
                Slot = 8,
                TemplateId = largeItem.Id,
                Tier = GhostBoardValidator.GetSupportedTiers(largeItem)[0],
                EditedToggles = new Dictionary<string, bool> { ["Tiny"] = true },
            },
            new PlannerItemInput
            {
                Slot = 0,
                TemplateId = mediumItem.Id,
                Tier = GhostBoardValidator.GetSupportedTiers(mediumItem)[0],
                EditedToggles = new Dictionary<string, bool> { ["Tiny"] = true },
            },
            new PlannerItemInput
            {
                Slot = 2,
                TemplateId = smallItem.Id,
                Tier = GhostBoardValidator.GetSupportedTiers(smallItem)[0],
                EditedToggles = new Dictionary<string, bool> { ["Tiny"] = true },
            });

        var res = GhostBoardValidator.ValidateAndNormalize(1, tinyBoard, repo, pool);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(3, res.Board.Items.Count);

        var outLarge = res.Board.Items[0];
        Assert.Equal(2, outLarge.Width);
        Assert.Equal(1.0, outLarge.PermanentAdd!["Tiny"], 3);
        Assert.Equal(1, outLarge.PermanentAttributes["Tiny"]);
        Assert.True(outLarge.ExtraTags == null || !outLarge.ExtraTags.Contains("Tiny", StringComparer.OrdinalIgnoreCase));

        var outMed = res.Board.Items[1];
        Assert.Equal(1, outMed.Width);
        Assert.Equal(1.0, outMed.PermanentAdd!["Tiny"], 3);
        Assert.Equal(1, outMed.PermanentAttributes["Tiny"]);

        // Small 拒缩并给出精确中文 warning
        var outSmall = res.Board.Items[2];
        Assert.Equal(1, outSmall.Width);
        Assert.False(outSmall.PermanentAdd != null && outSmall.PermanentAdd.ContainsKey("Tiny"));
        Assert.False(outSmall.PermanentAttributes.ContainsKey("Tiny"));
        Assert.Contains($"「{smallZh}」本身就是小型物品（1 格），无需微型化", res.Warnings);

        // Day 1 掩码验证：默认 3..6 (120) ∪ slot 0 (bit 0，仅 1 格不含 bit 1!) ∪ slot 2 (bit 2) ∪ slots 8..9 (bits 8,9)
        int expectedMask = PvpGhostPool.DefaultUnlockedMaskForDay(1) | (1 << 0) | (1 << 2) | (1 << 8) | (1 << 9);
        Assert.Equal(expectedMask, res.Board.UnlockedSlotsMask);
        Assert.Equal(0, res.Board.UnlockedSlotsMask & (1 << 1)); // Medium 缩为 1 格，不误解锁 slot 1

        // ComputeEffectiveUnlockedSlotsMask 同样识别 Tiny 缩格（单测将 UnlockedSlotsMask 归零再算）
        var maskProbeBoard = new PvpGhostPool.GhostBoard
        {
            Day = 1,
            Hero = "Vanessa",
            UnlockedSlotsMask = 0,
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 0,
                    Width = 3, // 故意传未缩宽度，验证 ResolveGhostItemEffectiveWidth 仍按 Tiny 算 2 格
                    TemplateId = largeItem.Id,
                    Tier = outLarge.Tier,
                    PermanentAdd = new Dictionary<string, double> { ["Tiny"] = 1 },
                },
            },
        };
        int computedMask = PvpGhostPool.ComputeEffectiveUnlockedSlotsMask(maskProbeBoard, repo);
        Assert.NotEqual(0, computedMask & (1 << 0));
        Assert.NotEqual(0, computedMask & (1 << 1));
        Assert.Equal(0, computedMask & (1 << 2)); // Large 缩为 2 格，slot 2 绝不解锁

        // 关闭 Tiny：双清 PermanentAdd 与 PermanentAttributes，宽度恢复
        var turnOffRes = GhostBoardValidator.ValidateAndNormalize(4, MakeBoardInput(4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = mediumItem.Id,
            Tier = outMed.Tier,
            PermanentAdd = new Dictionary<string, double> { ["Tiny"] = 1 },
            PermanentAttributes = new Dictionary<string, int> { ["Tiny"] = 1 },
            EditedToggles = new Dictionary<string, bool> { ["Tiny"] = false },
        }), repo, pool);
        Assert.True(turnOffRes.Ok);
        var restoredMed = Assert.Single(turnOffRes.Board.Items);
        Assert.Equal(2, restoredMed.Width);
        Assert.False(restoredMed.PermanentAdd != null && restoredMed.PermanentAdd.ContainsKey("Tiny"));
        Assert.False(restoredMed.PermanentAttributes.ContainsKey("Tiny"));

        // 历史镜像仅在 ExtraTags 写 "Tiny" 未写属性时，ToPlayerState 自动补 PermanentAdd["Tiny"]=1 且 SyncEntryFromCoreCardAndDiff 同步尺寸与 Tiny==1
        var legacyTinyBoard = new PvpGhostPool.GhostBoard
        {
            BoardId = "legacy_tiny_tag",
            Day = 4,
            Hero = "Vanessa",
            HealthMax = 800,
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 0,
                    Width = 2,
                    TemplateId = largeItem.Id,
                    Tier = outLarge.Tier,
                    ExtraTags = new List<string> { "Tiny" },
                },
            },
        };
        var ps = PvpGhostPool.ToPlayerState(legacyTinyBoard, repo);
        var coreLarge = Assert.Single(ps.Hand.Cards);
        Assert.True(coreLarge.IsTiny);
        Assert.Equal(CardSize.Medium, coreLarge.EffectiveSize);
        Assert.Equal(1.0, coreLarge.PermanentAdd["Tiny"], 3);

        var runState = new RunState { RunId = "sync_tiny_test", Hero = EHero.Vanessa };
        var entry = new RunState.CardEntry
        {
            InstanceId = coreLarge.InstanceId,
            TemplateId = Guid.Parse(largeItem.Id),
            Type = ECardType.Item,
            Size = ECardSize.Large,
            Tier = ShopEngine.ParseTier(coreLarge.Tier.ToString()),
            Owner = ECombatantId.Opponent,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = coreLarge,
        };
        runState.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        Assert.Equal(ECardSize.Medium, entry.Size);
        Assert.Equal(1, entry.Attributes[ECardAttributeType.Tiny]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. 模块 D2：全库 9 种官方 SocketEffect 校验、非 SocketEffect 拦截、同槽重复拦截、掩码与升序排序
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleD2_SocketEffects_All9Official_NonSocketRejected_DuplicateSlotRejected_MaskAndSort()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        var allSocketTpls = repo.CardsById.Values
            .Where(GhostBoardValidator.IsValidSocketEffectTemplate)
            .OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(9, allSocketTpls.Count);

        // 将全部 9 种官方 SocketEffect 按乱序槽位放入 Day 1 盘面
        int[] unsortedSlots = { 9, 1, 7, 3, 5, 2, 8, 0, 4 };
        var boardIn = new PlannerBoardInput
        {
            Day = 1,
            Hero = "Jules",
            Level = 1,
            HealthMax = 250,
            Prestige = 20,
            Items = new List<PlannerItemInput>(),
            Skills = new List<PlannerSkillInput>(),
            SocketEffects = allSocketTpls
                .Select((tpl, idx) => new PlannerSocketEffectInput
                {
                    Slot = unsortedSlots[idx],
                    TemplateId = tpl.Id,
                    Tier = GhostBoardValidator.GetSupportedTiers(tpl)[0],
                })
                .ToList(),
        };

        var res = GhostBoardValidator.ValidateAndNormalize(1, boardIn, repo, pool);
        Assert.True(res.Ok, $"9 种官方 SocketEffect 校验失败：{string.Join("; ", res.Errors)}");
        Assert.NotNull(res.Board.SocketEffects);
        Assert.Equal(9, res.Board.SocketEffects!.Count);

        // 按 Slot 升序稳定输出，且全部 9 个槽位并入 UnlockedSlotsMask
        var expectedSortedSlots = unsortedSlots.OrderBy(s => s).ToList();
        Assert.Equal(expectedSortedSlots, res.Board.SocketEffects.Select(s => s.Slot).ToList());
        foreach (int s in expectedSortedSlots)
            Assert.NotEqual(0, res.Board.UnlockedSlotsMask & (1 << s));

        // 还原至 PlayerState 后完整保留 9 个 SocketEffects
        var ps = PvpGhostPool.ToPlayerState(res.Board, repo);
        Assert.Equal(9, ps.SocketEffects.Count);

        // 非 SocketEffect 模板（用普通物品模板冒充）必须报错拒绝
        var normalItem = PickValidItem(repo, pool, c => c.CardSize == CardSize.Small);
        var badTypeBoard = new PlannerBoardInput
        {
            Day = 4,
            Hero = "Vanessa",
            Level = 4,
            HealthMax = 500,
            Prestige = 20,
            SocketEffects = new List<PlannerSocketEffectInput>
            {
                new() { Slot = 2, TemplateId = normalItem.Id, Tier = "Bronze" },
            },
        };
        var badTypeRes = GhostBoardValidator.ValidateAndNormalize(4, badTypeBoard, repo, pool);
        Assert.False(badTypeRes.Ok);
        Assert.Contains(badTypeRes.Errors, e => e.Contains("不是合法的插座效果模板", StringComparison.Ordinal));

        // 同一槽位放置 2 个 SocketEffect 必须报错拒绝
        var dupSlotBoard = new PlannerBoardInput
        {
            Day = 4,
            Hero = "Jules",
            Level = 4,
            HealthMax = 500,
            Prestige = 20,
            SocketEffects = new List<PlannerSocketEffectInput>
            {
                new() { Slot = 3, TemplateId = allSocketTpls[0].Id, Tier = GhostBoardValidator.GetSupportedTiers(allSocketTpls[0])[0] },
                new() { Slot = 3, TemplateId = allSocketTpls[1].Id, Tier = GhostBoardValidator.GetSupportedTiers(allSocketTpls[1])[0] },
            },
        };
        var dupSlotRes = GhostBoardValidator.ValidateAndNormalize(4, dupSlotBoard, repo, pool);
        Assert.False(dupSlotRes.Ok);
        Assert.Contains(dupSlotRes.Errors, e => e.Contains("不能同时放置多个插座效果", StringComparison.Ordinal));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. 模块 D3：Lifesteal / Flying / DestroyImmunity 开关、原卡自带只读、ExtraTags 校验与频率黑名单严防
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleD3_Toggles_NativeReadOnly_ExtraTags_AndFrequencyBlacklist()
    {
        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();

        // 选一件不自带 Lifesteal / DestroyImmunity 且自带至少一个标签（如 Weapon）的普通物品
        var normalItem = PickValidItem(repo, pool, c =>
            c.CardSize == CardSize.Medium &&
            c.Tags != null && c.Tags.Count > 0 &&
            GhostBoardValidator.GetNativeToggles(c).Count == 0);
        string nativeTag = normalItem.Tags[0];

        // 5a) 开启 Lifesteal / Flying / DestroyImmunity + 尝试在 editedToggles 与 permanentAdd 中注入频率黑名单 + ExtraTags 清洗
        var nonNativeCandidates = PlannerCatalogConstants.EditableExtraTags
            .Select(t => t.Id)
            .Where(id => !normalItem.Tags.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Take(7)
            .ToList();
        Assert.Equal(7, nonNativeCandidates.Count);
        string firstCandidateLower = nonNativeCandidates[0].ToLowerInvariant();
        var rawExtraTags = new List<string>
        {
            nativeTag,              // 模板自带 -> 剔除
            "Tiny",                 // Tiny -> 剔除
            "InvalidBogus",         // 非法 ECardTag -> 丢弃并 warning
            firstCandidateLower,    // 大小写归一
            nonNativeCandidates[0], // 重复 -> 去重
        };
        rawExtraTags.AddRange(nonNativeCandidates.Skip(1)); // 再加 6 个 -> 共 7 个合法额外标签 -> 截断至 6 个并 warning

        var input = MakeBoardInput(4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = normalItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(normalItem)[0],
            PermanentAdd = new Dictionary<string, double>
            {
                ["Multicast"] = 5,
                ["AmmoMax"] = 10,
                ["HasteAmount"] = 3000,
                ["SlowAmount"] = 2000,
                ["FreezeAmount"] = 1000,
                ["ChargeAmount"] = 2000,
            },
            EditedToggles = new Dictionary<string, bool>
            {
                ["Lifesteal"] = true,
                ["Flying"] = true,
                ["DestroyImmunity"] = true,
                ["Multicast"] = true,
                ["AmmoMax"] = true,
            },
            ExtraTags = rawExtraTags,
        });

        var res1 = GhostBoardValidator.ValidateAndNormalize(4, input, repo, pool);
        Assert.True(res1.Ok, string.Join("; ", res1.Errors));
        var out1 = Assert.Single(res1.Board.Items);

        // 3 项合法状态开关写入 PermanentAdd 与 PermanentAttributes
        Assert.Equal(100.0, out1.PermanentAdd!["Lifesteal"], 3);
        Assert.Equal(1.0, out1.PermanentAdd["Flying"], 3);
        Assert.Equal(1.0, out1.PermanentAdd["DestroyImmunity"], 3);
        Assert.Equal(100, out1.PermanentAttributes["Lifesteal"]);
        Assert.Equal(1, out1.PermanentAttributes["Flying"]);
        Assert.Equal(1, out1.PermanentAttributes["DestroyImmunity"]);

        // 频率/控制黑名单全部拦截
        foreach (string blocked in new[] { "Multicast", "AmmoMax", "HasteAmount", "SlowAmount", "FreezeAmount", "ChargeAmount" })
            Assert.False(out1.PermanentAdd.ContainsKey(blocked), $"频率黑名单键 {blocked} 绝不可进入 PermanentAdd");
        Assert.Contains(res1.Warnings, w => w.Contains("Multicast", StringComparison.Ordinal));
        Assert.Contains(res1.Warnings, w => w.Contains("AmmoMax", StringComparison.Ordinal));

        // ExtraTags：剔除模板自带、剔除 Tiny、剔除非法标签、去重、最多 6 个
        Assert.NotNull(out1.ExtraTags);
        Assert.Equal(PlannerCatalogConstants.MaxExtraTags, out1.ExtraTags!.Count);
        Assert.DoesNotContain("Tiny", out1.ExtraTags, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(nativeTag, out1.ExtraTags, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidBogus", out1.ExtraTags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(nonNativeCandidates[0], out1.ExtraTags);
        Assert.DoesNotContain(nonNativeCandidates[6], out1.ExtraTags);
        Assert.Contains(res1.Warnings, w => w.Contains("InvalidBogus", StringComparison.Ordinal));
        Assert.Contains(res1.Warnings, w => w.Contains("最多 6 个", StringComparison.Ordinal));

        // 二次提交幂等
        var res2 = GhostBoardValidator.ValidateAndNormalize(4, res1.Board, repo, pool);
        Assert.True(res2.Ok);
        var out2 = Assert.Single(res2.Board.Items);
        Assert.Equal(out1.ExtraTags, out2.ExtraTags);
        Assert.Equal(out1.PermanentAttributes["Lifesteal"], out2.PermanentAttributes["Lifesteal"]);
        Assert.Equal(out1.PermanentAttributes["Flying"], out2.PermanentAttributes["Flying"]);
        Assert.Equal(out1.PermanentAttributes["DestroyImmunity"], out2.PermanentAttributes["DestroyImmunity"]);

        // 5b) 原卡自带 Lifesteal 的卡（如 Runic Blade）关闭 Lifesteal 开关时保留原生值并提示
        var nativeLifestealItem = PickValidItem(repo, pool, c =>
            GhostBoardValidator.GetNativeToggles(c).Contains("Lifesteal", StringComparer.OrdinalIgnoreCase));
        var lsRes = GhostBoardValidator.ValidateAndNormalize(4, MakeBoardInput(4, new PlannerItemInput
        {
            Slot = 0,
            TemplateId = nativeLifestealItem.Id,
            Tier = GhostBoardValidator.GetSupportedTiers(nativeLifestealItem)[0],
            EditedToggles = new Dictionary<string, bool> { ["Lifesteal"] = false },
        }), repo, pool);
        Assert.True(lsRes.Ok);
        Assert.Contains(lsRes.Warnings, w => w.Contains("本身自带吸血", StringComparison.Ordinal));
        var lsPs = PvpGhostPool.ToPlayerState(lsRes.Board, repo);
        Assert.True(lsPs.Hand.Cards[0].GetAttribute("Lifesteal") > 0, "原卡自带吸血不应被关闭倒扣为 0");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. 端到端 HTTP API 契约验证（me/context、catalog/cards、catalog/describe、validate-board）
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EndToEndApi_MeContext_CatalogCards_Describe_AndValidateBoard_MatchesContract()
    {
        using var temp = new TempDir("v15_6_planner_api");
        using var db = new Persistence(Path.Combine(temp.Root, "state.db"));

        var repo = _fx.Repo;
        var pool = _fx.CreateIsolatedCardPool();
        var monsters = _fx.CreateMonstersCopy();

        string modsDir = Path.Combine(temp.Root, "mods");
        string stateDir = Path.Combine(temp.Root, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var modHost = new ServerModHost(repo, _fx.DataDir, stateDir, _fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var monsterStore = new MonsterPoolStore(monsters, modHost.ActiveRepository);
        modHost.BindPools(pool, monsterStore);
        var runtimeSnapshots = new ServerRuntimeSnapshotProvider(modHost, pool);

        var accounts = new AccountStore(db);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var gameSessions = new SessionStore();
        var plannerSessions = new PlannerSessionStore();
        var lineupStore = new GhostLineupStore(db, ruleStore, modHost.BaseRepository, pool, monsterStore, runtimeSnapshots, modHost, communityMods);
        var blueprintService = new RunBlueprintService(db, ruleStore, gameSessions, modHost, communityMods, pool, monsterStore, runtimeSnapshots);
        using var seedFinder = new EventSeedFinderService(modHost, pool, monsterStore, runtimeSnapshots);

        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(ruleStore);
            builder.Services.AddSingleton(gameSessions);
            builder.Services.AddSingleton(modHost);
            builder.Services.AddSingleton(communityMods);
            builder.Services.AddSingleton(pool);
            builder.Services.AddSingleton(monsterStore);
            builder.Services.AddSingleton(runtimeSnapshots);
            builder.Services.AddSingleton(_fx.Ghosts);
            builder.Services.AddSingleton(plannerSessions);
            builder.Services.AddSingleton(lineupStore);
            builder.Services.AddSingleton(blueprintService);
            builder.Services.AddSingleton(seedFinder);

            app = builder.Build();
            app.MapPlannerEndpoints();
            await app.StartAsync();

            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var login = accounts.LoginByNameWithSecret("V156ApiUser", "V156#Pass99", allowAutoBindSecret: true);
            Assert.True(login.Ok && login.Account != null);

            string plnToken;
            using (var loginResp = await http.PostAsJsonAsync("/api/planner/auth/login", new { username = "V156ApiUser", password = "V156#Pass99" }))
            {
                Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
                using var doc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
                plnToken = doc.RootElement.GetProperty("token").GetString()!;
            }

            // 6.1 GET /api/planner/me/context：9 种 SocketEffect + 4 项 editableToggles + 17 项 editableTags + maxExtraTags=6
            string stoveId;
            using (var req = CreateAuthedGet("/api/planner/me/context", plnToken))
            using (var resp = await http.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var rules = doc.RootElement.GetProperty("boardEditRules");

                var sockets = rules.GetProperty("socketEffects");
                Assert.Equal(9, sockets.GetArrayLength());
                foreach (var se in sockets.EnumerateArray())
                {
                    Assert.False(string.IsNullOrWhiteSpace(se.GetProperty("templateId").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(se.GetProperty("nameZh").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(se.GetProperty("defaultTier").GetString()));
                    string desc = se.GetProperty("descriptionZh").GetString() ?? "";
                    Assert.False(string.IsNullOrWhiteSpace(desc));
                    Assert.DoesNotContain("?", desc, StringComparison.Ordinal);
                }
                stoveId = sockets[0].GetProperty("templateId").GetString()!;
                Assert.Equal("c017c0dd-af3c-47e0-b510-3661d9dcd7f2", stoveId);

                var toggles = rules.GetProperty("editableToggles");
                Assert.Equal(4, toggles.GetArrayLength());
                Assert.Equal("Tiny", toggles[0].GetProperty("key").GetString());
                Assert.Equal(1, toggles[0].GetProperty("onValue").GetInt32());
                Assert.Equal("nonSmallItem", toggles[0].GetProperty("scope").GetString());
                Assert.Equal("Lifesteal", toggles[1].GetProperty("key").GetString());
                Assert.Equal(100, toggles[1].GetProperty("onValue").GetInt32());

                var tags = rules.GetProperty("editableTags");
                Assert.Equal(17, tags.GetArrayLength());
                Assert.Equal(6, rules.GetProperty("maxExtraTags").GetInt32());
            }

            // 6.2 GET /api/planner/catalog/cards?ids=...：任务卡带 quests + enchantmentSummaries（0 '?'），无任务卡省略 quests，自带吸血卡带 nativeToggles
            var questItem = PickValidItem(repo, pool, c =>
                c.Quests != null &&
                c.Quests.Any(g => !g.EffectiveRepeatable && g.Entries.Count > 0) &&
                GhostBoardValidator.GetSupportedEnchantments(c).Count > 0);
            var plainItem = PickValidItem(repo, pool, c =>
                (c.Quests == null || c.Quests.Count == 0) &&
                GhostBoardValidator.GetNativeToggles(c).Count == 0 &&
                c.CardSize == CardSize.Large);
            var lifestealItem = PickValidItem(repo, pool, c =>
                GhostBoardValidator.GetNativeToggles(c).Contains("Lifesteal", StringComparer.OrdinalIgnoreCase));

            string idsCsv = $"{questItem.Id},{plainItem.Id},{lifestealItem.Id}";
            using (var req = CreateAuthedGet($"/api/planner/catalog/cards?ids={Uri.EscapeDataString(idsCsv)}", plnToken))
            using (var resp = await http.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var arr = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
                Assert.Equal(3, arr.Count);

                var qDto = arr.Single(x => x.GetProperty("templateId").GetString() == questItem.Id);
                Assert.True(qDto.TryGetProperty("quests", out var questsEl) && questsEl.GetArrayLength() > 0);
                foreach (var g in questsEl.EnumerateArray())
                {
                    foreach (var e in g.GetProperty("entries").EnumerateArray())
                    {
                        string goalZh = e.GetProperty("goalZh").GetString() ?? "";
                        string rewardZh = e.GetProperty("rewardZh").GetString() ?? "";
                        Assert.DoesNotContain("?", goalZh, StringComparison.Ordinal);
                        Assert.DoesNotContain("?", rewardZh, StringComparison.Ordinal);
                    }
                }
                Assert.True(qDto.TryGetProperty("enchantmentSummaries", out var enchSumEl) && enchSumEl.ValueKind == JsonValueKind.Object);
                foreach (var prop in enchSumEl.EnumerateObject())
                {
                    string summary = prop.Value.GetString() ?? "";
                    Assert.False(string.IsNullOrWhiteSpace(summary));
                    Assert.DoesNotContain("?", summary, StringComparison.Ordinal);
                }

                var plainDto = arr.Single(x => x.GetProperty("templateId").GetString() == plainItem.Id);
                Assert.False(plainDto.TryGetProperty("quests", out _), "无任务卡不应返回 quests 字段");
                Assert.False(plainDto.TryGetProperty("nativeToggles", out _), "无自带开关卡不应返回 nativeToggles 字段");

                var lsDto = arr.Single(x => x.GetProperty("templateId").GetString() == lifestealItem.Id);
                Assert.True(lsDto.TryGetProperty("nativeToggles", out var ntEl));
                Assert.Contains("Lifesteal", ntEl.EnumerateArray().Select(x => x.GetString()));
            }

            // 6.3 POST /api/planner/catalog/describe：传入 completedQuests / extraTags / toggles 返回已解锁奖励且 0 '?'
            var firstQuestAttr = questItem.Quests!
                .Where(g => !g.EffectiveRepeatable)
                .SelectMany(g => g.Entries)
                .First().AttributeType;

            using (var req = CreateAuthedJson(HttpMethod.Post, "/api/planner/catalog/describe", plnToken, new
            {
                cards = new[]
                {
                    new
                    {
                        templateId = questItem.Id,
                        tier = GhostBoardValidator.GetSupportedTiers(questItem)[0],
                        completedQuests = new[] { firstQuestAttr },
                        extraTags = new[] { "Aquatic" },
                        toggles = new Dictionary<string, bool> { ["Lifesteal"] = true, ["Flying"] = true },
                    },
                },
                selectedModIds = Array.Empty<string>(),
            }))
            using (var resp = await http.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var descItem = doc.RootElement.GetProperty("items")[0];
                var lines = descItem.GetProperty("lines").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                Assert.NotEmpty(lines);
                Assert.Contains(lines, l => l.Contains("[任务已完成]", StringComparison.Ordinal));
                Assert.All(lines, l => Assert.DoesNotContain("?", l, StringComparison.Ordinal));
            }

            // 6.4 POST /api/planner/lineups/validate-board：完整提交 editedToggles + completedQuests + extraTags + socketEffects，返回剥离 editedToggles 且二次提交完全一致
            string firstValBoardJson;
            using (var req = CreateAuthedJson(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                selectedModIds = Array.Empty<string>(),
                board = new
                {
                    day = 1,
                    hero = "Jules",
                    level = 1,
                    healthMax = 250,
                    prestige = 20,
                    items = new object[]
                    {
                        new
                        {
                            slot = 8,
                            templateId = plainItem.Id, // Large 缩为 2 格放 8..9
                            tier = GhostBoardValidator.GetSupportedTiers(plainItem)[0],
                            editedToggles = new Dictionary<string, bool> { ["Tiny"] = true, ["Lifesteal"] = true },
                            extraTags = new[] { "Aquatic", "Tool" },
                        },
                        new
                        {
                            slot = 3,
                            templateId = questItem.Id,
                            tier = GhostBoardValidator.GetSupportedTiers(questItem)[0],
                            completedQuests = new[] { firstQuestAttr },
                        },
                    },
                    skills = Array.Empty<object>(),
                    socketEffects = new[]
                    {
                        new { slot = 8, templateId = stoveId, tier = "Diamond" },
                    },
                },
            }))
            using (var resp = await http.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var boardEl = doc.RootElement.GetProperty("board");
                firstValBoardJson = boardEl.GetRawText();

                var item0 = boardEl.GetProperty("items")[0];
                Assert.Equal(2, item0.GetProperty("width").GetInt32());
                Assert.False(item0.TryGetProperty("editedToggles", out _), "响应必须剥离 editedToggles");
                Assert.Equal(1, item0.GetProperty("permanentAdd").GetProperty("Tiny").GetInt32());
                Assert.Equal(100, item0.GetProperty("permanentAdd").GetProperty("Lifesteal").GetInt32());
                Assert.Equal(1, item0.GetProperty("permanentAttributes").GetProperty("Tiny").GetInt32());
                Assert.Equal(100, item0.GetProperty("permanentAttributes").GetProperty("Lifesteal").GetInt32());

                var sockets = boardEl.GetProperty("socketEffects");
                Assert.Equal(1, sockets.GetArrayLength());
                Assert.Equal(8, sockets[0].GetProperty("slot").GetInt32());
            }

            // 二次提交 validate-board 必须 100% 幂等
            using (var parsedBoard = JsonDocument.Parse(firstValBoardJson))
            using (var req2 = CreateAuthedJson(HttpMethod.Post, "/api/planner/lineups/validate-board", plnToken, new
            {
                day = 1,
                selectedModIds = Array.Empty<string>(),
                board = parsedBoard.RootElement,
            }))
            using (var resp2 = await http.SendAsync(req2))
            {
                Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
                using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
                Assert.True(doc2.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(firstValBoardJson, doc2.RootElement.GetProperty("board").GetRawText());
            }
        }
        finally
        {
            http?.Dispose();
            if (app != null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }

    // ── 测试辅助 ──────────────────────────────────────────────────────────────

    private static CardTemplate PickValidItem(GameDataRepository repo, CardPool pool, Func<CardTemplate, bool> predicate)
    {
        var hit = repo.AllItems.FirstOrDefault(c =>
            pool.IsAssetValid(c.Id) &&
            !MonsterPool.IsBlockedDebugOrBlankArtSkill(c) &&
            !(c.InternalName ?? "").Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) &&
            predicate(c));
        Assert.NotNull(hit);
        return hit!;
    }

    private static PlannerBoardInput MakeBoardInput(int day, params PlannerItemInput[] items) => new()
    {
        Day = day,
        Hero = "Vanessa",
        Level = Math.Max(1, day),
        HealthMax = 500,
        Prestige = 20,
        Items = items.ToList(),
        Skills = new List<PlannerSkillInput>(),
        SocketEffects = new List<PlannerSocketEffectInput>(),
    };

    private static HttpRequestMessage CreateAuthedGet(string url, string plnToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Planner-Session", plnToken);
        return req;
    }

    private static HttpRequestMessage CreateAuthedJson(HttpMethod method, string url, string plnToken, object body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Planner-Session", plnToken);
        req.Content = new StringContent(JsonSerializer.Serialize(body, PlannerEndpoints.PlannerJsonOpts), Encoding.UTF8, "application/json");
        return req;
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir(string prefix)
        {
            Root = Path.Combine(Path.GetTempPath(), $"bazaar_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // 忽略 Windows 文件锁导致的临时目录清理竞态
            }
        }
    }
}
