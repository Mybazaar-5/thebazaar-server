using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarServer.Data;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// 模块 A（A1 / A2）C# 唯一口径审计单测：
/// 直接遍历新版 <c>ghost_pool_balanced.jsonl</c> 中全部 <c>real_compass_anchor</c> 盘面，
/// 复用 <see cref="CardInstance.RebuildTierAndEnchantmentData"/> 与 <see cref="PvpGhostPool.ToPlayerState"/>
/// 真实叠算口径统计清洗前后的精确计数，并断言清洗后零倒扣、零孤立输出属性、零冗余 Multicast、零非正 Lifesteal。
/// </summary>
public sealed class GhostPoolLegacyAnchorAuditTests
{
    private readonly ITestOutputHelper _output;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public GhostPoolLegacyAnchorAuditTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool IsGuardedOutputAttribute(string attr)
        => !string.Equals(attr, "CooldownMax", StringComparison.OrdinalIgnoreCase)
        && (CardInstance.IsOutputMagnitudeAttribute(attr)
            || string.Equals(attr, "AmmoMax", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void LegacyCompassAnchors_CSharpAudit_ZeroNerf_ZeroOrphanOutput_ZeroRedundantMulticast()
    {
        var repo = TestInfrastructure.SharedGameDataFixture.Shared.Repo;
        string dataDir = TestInfrastructure.TestDataLocator.ResolveDataDir();
        string jsonlPath = Path.Combine(dataDir, "ghost_pool_balanced.jsonl");
        Assert.True(File.Exists(jsonlPath), $"未找到镜像池文件：{jsonlPath}");

        var anchors = new List<PvpGhostPool.GhostBoard>();
        foreach (string line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.Contains("real_compass_anchor", StringComparison.OrdinalIgnoreCase)) continue;
            var board = JsonSerializer.Deserialize<PvpGhostPool.GhostBoard>(line, JsonOpts);
            if (board != null && string.Equals(board.Source, "real_compass_anchor", StringComparison.OrdinalIgnoreCase))
                anchors.Add(board);
        }

        Assert.Equal(346, anchors.Count);

        // 统计清洗前状态（按 C# 真实 RebuildTierAndEnchantmentData 口径）
        int totalItems = 0;
        int itemsWithPermanentAttrs = 0;
        int itemsWithCompletedQuests = 0;
        int rawNegativeOutputBoards = 0;
        int rawNegativeOutputAttrs = 0;
        int rawOrphanOutputBoards = 0;
        int rawOrphanOutputAttrs = 0;
        int rawRedundantMulticastItems = 0;
        int rawNonPositiveLifestealItems = 0;

        var negativeSamples = new List<string>();
        var orphanSamples = new List<string>();
        var waystoneBoards = new List<(string BoardId, string Hero, List<string> Quests, CardInstance Card)>();

        var collector = new PvpGhostPool.GhostSanitizationAuditCollector();

        foreach (var board in anchors)
        {
            bool boardHasRawNeg = false;
            bool boardHasRawOrphan = false;

            // 1) 统计清洗前原始锚点与 C# 真实基础值的偏差
            foreach (var it in board.Items)
            {
                totalItems++;
                if (it.PermanentAttributes is { Count: > 0 })
                    itemsWithPermanentAttrs++;
                if (it.CompletedQuests is { Count: > 0 })
                    itemsWithCompletedQuests++;

                var tpl = repo.GetCard(it.TemplateId);
                if (tpl == null || it.PermanentAttributes == null || it.PermanentAttributes.Count == 0)
                    continue;

                string? validEnch = (!string.IsNullOrWhiteSpace(it.Enchantment)
                    && tpl.Enchantments != null
                    && tpl.Enchantments.ContainsKey(it.Enchantment))
                    ? it.Enchantment
                    : null;
                var refCard = new CardInstance(tpl, TierHelper.ParseTier(it.Tier), validEnch, "audit_ref");
                if (it.CompletedQuests is { Count: > 0 })
                {
                    foreach (string qid in it.CompletedQuests)
                    {
                        if (!string.IsNullOrWhiteSpace(qid))
                            refCard.CompletedQuests.Add(qid);
                    }
                    refCard.RebuildTierAndEnchantmentData();
                }

                foreach (var (ak, av) in it.PermanentAttributes)
                {
                    if (string.Equals(ak, "Lifesteal", StringComparison.OrdinalIgnoreCase) && av <= 0)
                    {
                        rawNonPositiveLifestealItems++;
                    }
                    else if (string.Equals(ak, "Multicast", StringComparison.OrdinalIgnoreCase))
                    {
                        double baseMc = refCard.BaseAttributes.TryGetValue("Multicast", out double mcVal)
                            ? mcVal
                            : (string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);
                        if (av <= baseMc + 1e-6)
                            rawRedundantMulticastItems++;
                    }
                    else if (IsGuardedOutputAttribute(ak))
                    {
                        bool hasOutput = refCard.BaseAttributes.ContainsKey(ak)
                            || refCard.EnchantmentBaseAttributes.ContainsKey(ak);
                        if (!hasOutput)
                        {
                            boardHasRawOrphan = true;
                            rawOrphanOutputAttrs++;
                            orphanSamples.Add($"{board.BoardId}/{tpl.InternalName}({it.Tier},{it.Enchantment ?? "none"}): {ak}={av}");
                        }
                        else
                        {
                            double baseVal = refCard.BaseAttributes.TryGetValue(ak, out double bv)
                                ? bv
                                : refCard.EnchantmentBaseAttributes.GetValueOrDefault(ak, 0);
                            if (av < baseVal - 1e-6)
                            {
                                boardHasRawNeg = true;
                                rawNegativeOutputAttrs++;
                                negativeSamples.Add($"{board.BoardId}/{tpl.InternalName}({it.Tier},{it.Enchantment ?? "none"}): {ak} {av} < base {baseVal}");
                            }
                        }
                    }
                }
            }

            if (boardHasRawNeg) rawNegativeOutputBoards++;
            if (boardHasRawOrphan) rawOrphanOutputBoards++;

            // 2) 调用生产级 PvpGhostPool.ToPlayerState 执行读时清洗，并断言清洗后 100% 干净
            var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "", auditCollector: collector);
            Assert.NotEmpty(ps.Hand.Cards);

            foreach (var card in ps.Hand.Cards)
            {
                if (string.Equals(card.Template.InternalName, "Waystones", StringComparison.OrdinalIgnoreCase))
                    waystoneBoards.Add((board.BoardId, board.Hero, card.CompletedQuests.ToList(), card));

                // 2a. PermanentAdd 中严禁残留孤立输出属性、输出属性倒扣、冗余 Multicast、非正 Lifesteal
                foreach (var (ak, delta) in card.PermanentAdd)
                {
                    if (IsGuardedOutputAttribute(ak))
                    {
                        bool hasOutput = card.BaseAttributes.ContainsKey(ak)
                            || card.EnchantmentBaseAttributes.ContainsKey(ak);
                        Assert.True(hasOutput,
                            $"清洗后仍存在孤立输出属性：{board.BoardId} / {card.Template.InternalName} / {ak}={delta}");

                        double baseVal = card.BaseAttributes.TryGetValue(ak, out double bv)
                            ? bv
                            : card.EnchantmentBaseAttributes.GetValueOrDefault(ak, 0);
                        Assert.True(delta >= -1e-6,
                            $"清洗后仍存在输出属性负增量倒扣：{board.BoardId} / {card.Template.InternalName} / {ak} delta={delta}");
                        Assert.True(baseVal + delta >= baseVal - 1e-6,
                            $"清洗后输出属性终值低于基础值：{board.BoardId} / {card.Template.InternalName} / {ak}");
                    }

                    if (string.Equals(ak, "Multicast", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.True(delta > 1e-6,
                            $"清洗后 PermanentAdd 中仍存在冗余或负向 Multicast：{board.BoardId} / {card.Template.InternalName} / delta={delta}");
                    }

                    if (string.Equals(ak, "Lifesteal", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.True(card.BaseAttributes.GetValueOrDefault("Lifesteal", 0) + delta > 0,
                            $"清洗后仍存在非正 Lifesteal：{board.BoardId} / {card.Template.InternalName}");
                    }
                }

                // 2b. 对每个已存在的输出属性断言永久层后终值 >= BaseAttributes（零倒扣）
                foreach (var (ak, baseVal) in card.BaseAttributes)
                {
                    if (!IsGuardedOutputAttribute(ak)) continue;
                    double add = card.PermanentAdd.GetValueOrDefault(ak, 0);
                    double mult = card.PermanentMult.GetValueOrDefault(ak, 1.0);
                    double finalPerm = (baseVal + add) * mult;
                    Assert.True(finalPerm >= baseVal - 1e-6,
                        $"清洗后输出属性倒扣：{board.BoardId} / {card.Template.InternalName} / {ak}: {finalPerm} < {baseVal}");
                }
            }
        }

        var summary = collector.BuildSummary();

        // 核验关键结构计数
        Assert.Equal(62, itemsWithCompletedQuests);
        Assert.Equal(0, rawNonPositiveLifestealItems); // Python A1 阶段已过滤全部 Lifesteal <= 0
        Assert.Equal(2, waystoneBoards.Count);

        // 核验 2 条真实 Waystones 经反推后均具备补全任务且奖励能力已激活
        var dooleyWs = Assert.Single(waystoneBoards, w => w.Hero == "Dooley");
        Assert.Contains("Quest_1", dooleyWs.Quests);
        Assert.Contains("Quest_3", dooleyWs.Quests);
        Assert.True(dooleyWs.Card.QuestProgress.GetValueOrDefault("Quest_3", 0) >= 1);
        Assert.Equal(60.0, dooleyWs.Card.BaseAttributes["ShieldApplyAmount"] + dooleyWs.Card.PermanentAdd.GetValueOrDefault("ShieldApplyAmount", 0), precision: 3);

        var karnokWs = Assert.Single(waystoneBoards, w => w.Hero == "Karnok");
        Assert.Contains("Quest_1", karnokWs.Quests);
        Assert.Contains("Quest_2", karnokWs.Quests);
        Assert.True(karnokWs.Card.QuestProgress.GetValueOrDefault("Quest_2", 0) >= 1);
        Assert.Equal(66.0, karnokWs.Card.BaseAttributes["DamageAmount"] + karnokWs.Card.PermanentAdd.GetValueOrDefault("DamageAmount", 0), precision: 3);
        Assert.Equal(110.0, karnokWs.Card.BaseAttributes["HealAmount"] + karnokWs.Card.PermanentAdd.GetValueOrDefault("HealAmount", 0), precision: 3);

        // 核验审计汇总计数与 RunStartupSanitizationAudit 一致且零污染 LRU
        var freshPool = PvpGhostPool.Load(dataDir);
        Assert.Equal(0, freshPool.CachedMaterializedCount);
        var startupSummary = freshPool.RunStartupSanitizationAudit(repo, logSummary: true);
        Assert.Equal(0, freshPool.CachedMaterializedCount);
        Assert.Equal(anchors.Count, startupSummary.ScannedBoards);
        Assert.Equal(summary.AffectedBoards, startupSummary.AffectedBoards);
        Assert.Equal(summary.TotalCleanedEntries, startupSummary.TotalCleanedEntries);

        // 打印真实 C# 口径前后统计（供回填 docs/GHOST_QUEST_AND_PLANNER_EFFECTS_PLAN.md 1.1 节）
        string report = $"""
            === C# GhostPoolLegacyAnchorAudit Report ===
            AnchorsScanned={anchors.Count}, TotalItems={totalItems}, ItemsWithPermAttrs={itemsWithPermanentAttrs}, ItemsWithCompletedQuests={itemsWithCompletedQuests}
            [Before Sanitization (C# RebuildTierAndEnchantmentData)]
              NegativeOutputBoards={rawNegativeOutputBoards}, NegativeOutputAttrs={rawNegativeOutputAttrs}
              OrphanOutputBoards={rawOrphanOutputBoards}, OrphanOutputAttrs={rawOrphanOutputAttrs}
              RedundantMulticastItems={rawRedundantMulticastItems}
              NonPositiveLifestealItems={rawNonPositiveLifestealItems}
            [Audit Collector Summary]
              {PvpGhostPool.FormatSanitizationAuditSummary("csharp_audit_test", summary)}
            [Negative Samples ({negativeSamples.Count})]
              {string.Join("\n  ", negativeSamples)}
            [Orphan Samples ({orphanSamples.Count})]
              {string.Join("\n  ", orphanSamples)}
            [After Sanitization]
              NegativeOutputAttrs=0, OrphanOutputAttrs=0, RedundantMulticastItems=0, NonPositiveLifestealItems=0
            """;
        _output.WriteLine(report);
        Console.WriteLine(report);
    }
}
