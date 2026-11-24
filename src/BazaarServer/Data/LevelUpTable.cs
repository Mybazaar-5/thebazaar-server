using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Data;

/// <summary>
/// 升级奖励（GameData.db 的 level_ups 表，支持完整 30 级 141 组 TSapwnContextQuery 语义）。
///
/// 官方结构：
///   { "$type": "TLevelUp", "Level": 1, "HealthIncrease": 5, "Version": "5.0.0",
///     "Rewards": {
///        "$type": "TSpawnContextQuery",
///        "SelectionMethod": "Sequential" | "Random",
///        "Limit": { "$type": "TFixedValue", "Value": 4.0 },
///        "Behaviors": [...],
///        "Groups": [
///          { "$type": "TSpawnGroup",
///            "Filters": [ { "$type": "TSpawnFilterIdList" | "TSpawnFilterQuery", ... } ],
///            "SelectionMethod": "Random" | "Sequential",
///            "Limit": { "$type": "TFixedValue", "Value": 1.0 },
///            "RandomWeight": 0 | 22 | 99,
///            "Behaviors": [...],
///            "Prerequisites": [...] } ] } }
/// </summary>
public sealed class LevelUpTable
{
    public sealed class LevelUpEntry
    {
        public int Level { get; init; }
        public int HealthIncrease { get; init; }
        public List<RewardGroup> Groups { get; init; } = new();
        public RuleNode? RewardsNode { get; init; }
        public string SelectionMethod { get; init; } = "Sequential";
        public int? TotalLimit { get; init; }

        /// <summary>已预编译的新手教程升级奖励节点（GameData.db 中 Lv1~Lv3 非空，其余等级为 null）。</summary>
        public RuleNode? TutorialRewardsNode { get; init; }
        public List<RewardGroup> TutorialGroups { get; init; } = new();
        public string TutorialSelectionMethod { get; init; } = "Sequential";
        public int? TutorialTotalLimit { get; init; }

        /// <summary>当前等级是否配置了独立的新手教程奖励池。</summary>
        public bool HasTutorialRewards => TutorialRewardsNode != null;

        /// <summary>
        /// 按运行模式返回应消费的升级奖励规则节点：教程模式优先返回 <see cref="TutorialRewardsNode"/>，常规模式或缺失时回退 <see cref="RewardsNode"/>。
        /// </summary>
        public RuleNode? GetRewardsNode(bool isTutorial)
            => isTutorial && TutorialRewardsNode != null ? TutorialRewardsNode : RewardsNode;
    }

    public sealed class RewardGroup
    {
        public List<string> CardIds { get; init; } = new();
        public int Limit { get; init; } = 1;
        public int? ExplicitLimit { get; init; }
        public int RandomWeight { get; init; }
        public string SelectionMethod { get; init; } = "Random";
        public RuleNode? GroupNode { get; init; }
    }

    private static readonly CardTier[] AllCardTiers =
    [
        CardTier.Bronze, CardTier.Silver, CardTier.Gold, CardTier.Diamond, CardTier.Legendary
    ];

    private readonly Dictionary<int, LevelUpEntry> _byLevel = new();

    public int Count => _byLevel.Count;
    public int MaxLevel => _byLevel.Count > 0 ? _byLevel.Keys.Max() : 0;

    /// <summary>
    /// 直接从已单次加载的 <see cref="GameDataRepository"/> 构建升级奖励表，消灭启动期重复解析 GameData.db。
    /// </summary>
    public static LevelUpTable FromRepository(GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var table = new LevelUpTable();
        foreach (var (fallbackLevel, tpl) in repo.LevelUps)
        {
            if (tpl == null) continue;
            var entry = FromTemplate(fallbackLevel, tpl);
            if (entry != null)
                table._byLevel[entry.Level] = entry;
        }
        Console.WriteLine($"[LevelUpTable] (shared repo) mapped {table._byLevel.Count} level-ups");
        return table;
    }

    private static LevelUpEntry? FromTemplate(int fallbackLevel, LevelUpTemplate tpl)
    {
        int level = tpl.Level > 0 ? tpl.Level : fallbackLevel;
        var rewardsNode = tpl.Rewards;
        var tutorialRewardsNode = tpl.TutorialRewards;

        var (groups, selectionMethod, totalLimit) = ExtractRewardsFromNode(rewardsNode, applyKarnokFix: true);
        var (tutGroups, tutMethod, tutTotalLimit) = ExtractRewardsFromNode(tutorialRewardsNode, applyKarnokFix: false);

        return new LevelUpEntry
        {
            Level = level,
            HealthIncrease = tpl.HealthIncrease,
            Groups = groups,
            RewardsNode = rewardsNode,
            SelectionMethod = selectionMethod,
            TotalLimit = totalLimit,
            TutorialRewardsNode = tutorialRewardsNode,
            TutorialGroups = tutGroups,
            TutorialSelectionMethod = tutMethod,
            TutorialTotalLimit = tutTotalLimit,
        };
    }

    private static (List<RewardGroup> Groups, string SelectionMethod, int? TotalLimit) ExtractRewardsFromNode(
        RuleNode? rewardsNode,
        bool applyKarnokFix)
    {
        string selectionMethod = string.IsNullOrWhiteSpace(rewardsNode?.SelectionMethod)
            ? "Sequential"
            : rewardsNode.SelectionMethod!;
        int? totalLimit = null;
        if (rewardsNode?.Limit?.GetFixedNumericValue() is double rd && rd > 0)
            totalLimit = Math.Max(1, (int)Math.Round(rd));

        var groups = new List<RewardGroup>();
        if (rewardsNode?.Groups is { Count: > 0 } rawGroups)
        {
            const string inspiredByKarnokId = "0c71aef1-c049-483a-b9a0-19c5005b4fc7";
            const string wrongKarnoksRageId = "1ac7830c-8506-4e7f-a1b6-8ba302f6a8fe";
            const string correctInspiredRageId = "0beccea0-bb9b-444b-b063-bf6aeadaddc8";

            foreach (var g in rawGroups)
            {
                if (g == null) continue;
                var ids = new List<string>();
                if (g.Filters != null)
                {
                    foreach (var f in g.Filters)
                    {
                        if (f?.Ids == null) continue;
                        foreach (var id in f.Ids)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                                ids.Add(id);
                        }
                    }
                }

                int? explicitLimit = g.Limit?.GetFixedNumericValue() is double gd && gd > 0
                    ? Math.Max(1, (int)Math.Round(gd))
                    : null;
                int limit = explicitLimit ?? 1;
                string grpMethod = string.IsNullOrWhiteSpace(g.SelectionMethod) ? "Random" : g.SelectionMethod!;

                var grpNode = g;
                // 保持与 Load(dbPath) 一致的官服 DB 原生 Bug 超越修复：Inspired by Karnok 互斥检查改为 Inspired Rage
                if (applyKarnokFix && ids.Contains(inspiredByKarnokId, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        string rawJson = JsonSerializer.Serialize(g, GameDatabaseLoader.JsonOptions);
                        if (rawJson.Contains(wrongKarnoksRageId, StringComparison.OrdinalIgnoreCase))
                        {
                            string patchedJson = rawJson.Replace(
                                wrongKarnoksRageId,
                                correctInspiredRageId,
                                StringComparison.OrdinalIgnoreCase);
                            var patched = JsonSerializer.Deserialize<RuleNode>(patchedJson, GameDatabaseLoader.JsonOptions);
                            if (patched != null) grpNode = patched;
                        }
                    }
                    catch { /* 保留原始 grpNode */ }
                }

                groups.Add(new RewardGroup
                {
                    CardIds = ids,
                    Limit = limit,
                    ExplicitLimit = explicitLimit,
                    RandomWeight = (int)Math.Round((double)(g.RandomWeight ?? 0)),
                    SelectionMethod = grpMethod,
                    GroupNode = grpNode,
                });
            }
        }

        return (groups, selectionMethod, totalLimit);
    }

    public static LevelUpTable Load(string dbPath)
    {
        var table = new LevelUpTable();
        if (!File.Exists(dbPath)) return table;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Data FROM level_ups ORDER BY Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int level = r.GetInt32(0);
            byte[] blob = (byte[])r.GetValue(1);
            var entry = Parse(level, System.Text.Encoding.UTF8.GetString(blob));
            if (entry != null) table._byLevel[entry.Level] = entry;
        }
        Console.WriteLine($"[LevelUpTable] loaded {table._byLevel.Count} level-ups");
        return table;
    }

    private static LevelUpEntry? Parse(int fallbackLevel, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int level = GetInt(root, "Level", fallbackLevel);
            int health = GetInt(root, "HealthIncrease", 0);

            RuleNode? rewardsNode = null;
            string selectionMethod = "Sequential";
            int? totalLimit = null;
            var groups = new List<RewardGroup>();

            if (root.TryGetProperty("Rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    rewardsNode = rewards.Deserialize<RuleNode>(GameDatabaseLoader.JsonOptions);
                }
                catch
                {
                    rewardsNode = null;
                }

                if (rewards.TryGetProperty("SelectionMethod", out var rm) && rm.ValueKind == JsonValueKind.String)
                    selectionMethod = rm.GetString() ?? "Sequential";

                if (rewards.TryGetProperty("Limit", out var rlim) &&
                    rlim.ValueKind == JsonValueKind.Object &&
                    rlim.TryGetProperty("Value", out var rlv) &&
                    rlv.ValueKind == JsonValueKind.Number &&
                    rlv.TryGetDouble(out double rd))
                {
                    totalLimit = Math.Max(1, (int)rd);
                }

                if (rewards.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                {
                    int groupIdx = 0;
                    foreach (var g in gs.EnumerateArray())
                    {
                        var ids = new List<string>();
                        if (g.TryGetProperty("Filters", out var filters) &&
                            filters.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in filters.EnumerateArray())
                            {
                                if (f.TryGetProperty("Ids", out var idArr) &&
                                    idArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var idEl in idArr.EnumerateArray())
                                    {
                                        if (idEl.ValueKind == JsonValueKind.String &&
                                            !string.IsNullOrWhiteSpace(idEl.GetString()))
                                        {
                                            ids.Add(idEl.GetString()!);
                                        }
                                    }
                                }
                            }
                        }

                        // 官方 JSON 中未显式设置 Group.Limit（如 Id=9/18 附魔祭坛组）时保持 ExplicitLimit 为 null，
                        // 避免误限制主权重组只能抽取 1 次。
                        int? explicitLimit = null;
                        if (g.TryGetProperty("Limit", out var lim) &&
                            lim.ValueKind == JsonValueKind.Object &&
                            lim.TryGetProperty("Value", out var lv) &&
                            lv.ValueKind == JsonValueKind.Number &&
                            lv.TryGetDouble(out double d))
                        {
                            explicitLimit = Math.Max(1, (int)d);
                        }
                        int limit = explicitLimit ?? 1;

                        string grpMethod = "Random";
                        if (g.TryGetProperty("SelectionMethod", out var gm) && gm.ValueKind == JsonValueKind.String)
                            grpMethod = gm.GetString() ?? "Random";

                        RuleNode? grpNode = rewardsNode?.Groups != null && groupIdx < rewardsNode.Groups.Count
                            ? rewardsNode.Groups[groupIdx]
                            : null;
                        if (grpNode == null)
                        {
                            try { grpNode = g.Deserialize<RuleNode>(GameDatabaseLoader.JsonOptions); }
                            catch { grpNode = null; }
                        }

                        // 官服 DB 原生 Bug 超越修复：level_ups Id=1 (TargetNewLevel=2) 的 Inspired by Karnok (0c71aef1)
                        // 错把互斥检查写成了 Karnok's Rage (1ac7830c) 而非实际授予技能 Inspired Rage (0beccea0)（Id=2..19 均为 0beccea0）
                        const string inspiredByKarnokId = "0c71aef1-c049-483a-b9a0-19c5005b4fc7";
                        const string wrongKarnoksRageId = "1ac7830c-8506-4e7f-a1b6-8ba302f6a8fe";
                        const string correctInspiredRageId = "0beccea0-bb9b-444b-b063-bf6aeadaddc8";
                        if (ids.Contains(inspiredByKarnokId, StringComparer.OrdinalIgnoreCase))
                        {
                            string rawGroupJson = g.GetRawText();
                            if (rawGroupJson.Contains(wrongKarnoksRageId, StringComparison.OrdinalIgnoreCase))
                            {
                                string patchedGroupJson = rawGroupJson.Replace(
                                    wrongKarnoksRageId,
                                    correctInspiredRageId,
                                    StringComparison.OrdinalIgnoreCase);
                                try
                                {
                                    var patchedNode = JsonSerializer.Deserialize<RuleNode>(patchedGroupJson, GameDatabaseLoader.JsonOptions);
                                    if (patchedNode != null) grpNode = patchedNode;
                                }
                                catch { /* 保留原始 grpNode */ }
                            }
                        }

                        groups.Add(new RewardGroup
                        {
                            CardIds = ids,
                            Limit = limit,
                            ExplicitLimit = explicitLimit,
                            RandomWeight = GetInt(g, "RandomWeight", 0),
                            SelectionMethod = grpMethod,
                            GroupNode = grpNode,
                        });
                        groupIdx++;
                    }
                }
            }

            RuleNode? tutorialRewardsNode = null;
            if (root.TryGetProperty("TutorialRewards", out var tutRewards) && tutRewards.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    tutorialRewardsNode = tutRewards.Deserialize<RuleNode>(GameDatabaseLoader.JsonOptions);
                }
                catch
                {
                    tutorialRewardsNode = null;
                }
            }
            var (tutGroups, tutSelectionMethod, tutTotalLimit) = ExtractRewardsFromNode(tutorialRewardsNode, applyKarnokFix: false);

            return new LevelUpEntry
            {
                Level = level,
                HealthIncrease = health,
                Groups = groups,
                RewardsNode = rewardsNode,
                SelectionMethod = selectionMethod,
                TotalLimit = totalLimit,
                TutorialRewardsNode = tutorialRewardsNode,
                TutorialGroups = tutGroups,
                TutorialSelectionMethod = tutSelectionMethod,
                TutorialTotalLimit = tutTotalLimit,
            };
        }
        catch { return null; }
    }

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    /// <summary>
    /// 简化版兼容重载（无玩家上下文时保留原行为）：先抽权重 > 0 组或全 0 组，再从 Ids 随机取（线程安全使用 Random.Shared）。
    /// </summary>
    public List<string> RollRewards(int level, int pickCount)
    {
        var result = new List<string>();
        var entry = ResolveLevelEntry(level);
        if (entry == null || entry.Groups.Count == 0) return result;

        var rng = Random.Shared;
        int totalWeight = entry.Groups.Sum(g => Math.Max(0, g.RandomWeight));
        for (int i = 0; i < pickCount; i++)
        {
            RewardGroup group;
            if (totalWeight > 0)
            {
                int roll = rng.Next(totalWeight);
                int acc = 0;
                group = entry.Groups[0];
                foreach (var g in entry.Groups)
                {
                    int w = Math.Max(0, g.RandomWeight);
                    if (w <= 0) continue;
                    acc += w;
                    if (roll < acc) { group = g; break; }
                }
            }
            else
            {
                group = entry.Groups[rng.Next(entry.Groups.Count)];
            }

            if (group.CardIds.Count == 0) continue;
            result.Add(group.CardIds[rng.Next(group.CardIds.Count)]);
        }
        return result;
    }

    /// <summary>按 DB 主键等级（1..30）读取升级表条目。</summary>
    public LevelUpEntry? GetEntryByDbId(int levelId)
        => _byLevel.TryGetValue(levelId, out var e) ? e : null;

    /// <summary>按玩家升级目标新等级（如 Lv1->Lv2 传 2）解析对应升级表条目。</summary>
    public LevelUpEntry? GetEntryForTargetLevel(int targetNewLevel)
        => ResolveLevelEntry(targetNewLevel);

    /// <summary>
    /// 官方完整规则升级奖励抽取（兼容返回纯模板 ID 列表，内部委托给 <see cref="RollRewardsWithTier"/>）。
    /// </summary>
    public List<string> RollRewards(
        int level,
        RuleExecutionContext ctx,
        Func<string, bool>? isAssetValid = null,
        int? pickCountOverride = null)
        => RollRewards(level, ctx, isTutorial: false, isAssetValid, pickCountOverride);

    /// <summary>
    /// 官方完整规则升级奖励抽取（支持教程模式优先消费 TutorialRewards，否则回退常规 Rewards）。
    /// </summary>
    public List<string> RollRewards(
        int level,
        RuleExecutionContext ctx,
        bool isTutorial,
        Func<string, bool>? isAssetValid = null,
        int? pickCountOverride = null)
    {
        var withTiers = RollRewardsWithTier(level, ctx, isTutorial, isAssetValid, pickCountOverride);
        var ids = new List<string>(withTiers.Count);
        for (int i = 0; i < withTiers.Count; i++)
            ids.Add(withTiers[i].TemplateId);
        return ids;
    }

    /// <summary>
    /// 官方完整规则升级奖励抽取（返回真实掷出的 (TemplateId, CardTier) 二元组，彻底修复 P1-12 品阶丢失与 StartingTier 误滤）：
    ///   1. 依据真实 PlayerState 评估每组 Prerequisites（TPrerequisiteRun / TPrerequisiteCardCount 等）。
    ///   2. 解析 TSpawnFilterIdList 与 TSpawnFilterQuery，结合 Heroes / IgnoreHero / IncludeCommonAndHero / 资产白名单 / 去重语义过滤候选池。
    ///   3. 正确按模板支持品阶（Tiers）匹配 TSpawnBehaviorTier 约束并回传应发放品阶。
    /// </summary>
    public List<(string TemplateId, CardTier Tier)> RollRewardsWithTier(
        int level,
        RuleExecutionContext ctx,
        Func<string, bool>? isAssetValid = null,
        int? pickCountOverride = null)
        => RollRewardsWithTier(level, ctx, isTutorial: false, isAssetValid, pickCountOverride);

    /// <summary>
    /// 官方完整规则升级奖励抽取（支持新手教程模式优先消费 <see cref="LevelUpEntry.TutorialRewardsNode"/>，常规模式或缺失时自动回退 <see cref="LevelUpEntry.RewardsNode"/>）。
    /// </summary>
    public List<(string TemplateId, CardTier Tier)> RollRewardsWithTier(
        int level,
        RuleExecutionContext ctx,
        bool isTutorial,
        Func<string, bool>? isAssetValid = null,
        int? pickCountOverride = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var result = new List<(string TemplateId, CardTier Tier)>();
        var entry = ResolveLevelEntry(level);
        if (entry == null) return result;

        bool useTutorial = isTutorial && entry.TutorialRewardsNode != null && entry.TutorialGroups.Count > 0;
        var activeNode = entry.GetRewardsNode(useTutorial);
        var activeGroups = useTutorial ? entry.TutorialGroups : entry.Groups;
        var activeSelectionMethod = useTutorial ? entry.TutorialSelectionMethod : entry.SelectionMethod;
        var activeTotalLimit = useTutorial ? entry.TutorialTotalLimit : entry.TotalLimit;
        if (activeGroups.Count == 0) return result;

        var rng = ctx.Rng ?? Random.Shared;
        var topBehaviors = activeNode?.Behaviors ?? [];
        int totalLimit = pickCountOverride ?? activeTotalLimit ?? 0;
        if (totalLimit <= 0 && activeNode?.Limit != null)
            totalLimit = (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(activeNode.Limit, ctx, 3));
        if (totalLimit <= 0)
            totalLimit = 3;

        var groupStates = new List<GroupDrawState>();
        foreach (var grp in activeGroups)
        {
            if (grp.GroupNode?.Prerequisites is { Count: > 0 } &&
                !ValueAndConditionEvaluator.EvaluatePrerequisites(grp.GroupNode.Prerequisites, ctx))
            {
                continue;
            }

            var (candidates, allowDuplicates) = ResolveGroupCandidates(grp, topBehaviors, ctx, isAssetValid);
            if (candidates.Count == 0) continue;

            int grpLimit;
            if (grp.GroupNode?.Limit != null)
            {
                grpLimit = Math.Max(1, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(grp.GroupNode.Limit, ctx, grp.Limit)));
            }
            else if (grp.ExplicitLimit.HasValue)
            {
                grpLimit = Math.Max(1, grp.ExplicitLimit.Value);
            }
            else
            {
                // 分组未声明 Limit（例如 Id=9 / Id=18 附魔台组）时不设单组次数上限，由顶层 totalLimit 与候选去重收口
                grpLimit = int.MaxValue;
            }

            groupStates.Add(new GroupDrawState(grp, candidates, grpLimit, allowDuplicates));
        }

        if (groupStates.Count == 0) return result;

        bool isRandomTop = string.Equals(activeSelectionMethod, "Random", StringComparison.OrdinalIgnoreCase);

        if (!isRandomTop)
        {
            // Pass 1：Sequential 模式下先按顺序处理 RandomWeight == 0 的条件必出组
            foreach (var gs in groupStates.Where(s => s.Group.RandomWeight <= 0))
            {
                while (result.Count < totalLimit && gs.RemainingPicks > 0)
                {
                    var picked = gs.PickOne(result, rng);
                    if (picked == null) break;
                    result.Add(picked.Value);
                }
                if (result.Count >= totalLimit) break;
            }

            // Pass 2：剩余名额按 RandomWeight > 0 的权重组加权补齐（各组不超出自身 Limit 与候选数）
            var weighted = groupStates
                .Where(s => s.Group.RandomWeight > 0 && s.HasRemaining(result))
                .ToList();
            FillWeightedFromGroups(weighted, totalLimit, result, rng);
            return result;
        }

        // Top-level Random 模式（如 Lv9 / Lv18 附魔台与稀有事件抽取）：所有可用组按权重加权抽取
        var randomEligible = groupStates.Where(s => s.HasRemaining(result)).ToList();
        FillWeightedFromGroups(randomEligible, totalLimit, result, rng);
        return result;
    }

    private void FillWeightedFromGroups(
        List<GroupDrawState> groups,
        int totalLimit,
        List<(string TemplateId, CardTier Tier)> result,
        Random rng)
    {
        while (result.Count < totalLimit)
        {
            var available = groups.Where(s => s.HasRemaining(result)).ToList();
            if (available.Count == 0) break;

            double sumWeight = available.Sum(s => s.Group.RandomWeight > 0 ? s.Group.RandomWeight : 1.0);
            GroupDrawState chosen;
            if (sumWeight <= 0)
            {
                chosen = available[rng.Next(available.Count)];
            }
            else
            {
                double roll = rng.NextDouble() * sumWeight;
                double acc = 0;
                chosen = available[^1];
                foreach (var g in available)
                {
                    acc += g.Group.RandomWeight > 0 ? g.Group.RandomWeight : 1.0;
                    if (roll <= acc) { chosen = g; break; }
                }
            }

            var picked = chosen.PickOne(result, rng);
            if (picked == null)
            {
                groups.Remove(chosen);
                continue;
            }
            result.Add(picked.Value);
        }
    }

    private static (List<(string TemplateId, CardTier Tier)> Candidates, bool AllowDuplicates) ResolveGroupCandidates(
        RewardGroup grp,
        List<RuleNode> topBehaviors,
        RuleExecutionContext ctx,
        Func<string, bool>? isAssetValid)
    {
        var behaviors = new List<RuleNode>(topBehaviors);
        if (grp.GroupNode?.Behaviors != null)
            behaviors.AddRange(grp.GroupNode.Behaviors);

        bool ignoreHero = behaviors.Any(b => b.Type == "TSpawnBehaviorIgnoreHero" && (b.IgnoreHero ?? true));
        bool includeCommonAndHero = behaviors.Any(b => b.Type == "TSpawnBehaviorIncludeCommonAndHero");
        bool excludePlayerHero = behaviors.Any(b => b.Type == "TSpawnBehaviorExcludePlayerHero");
        bool allowDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowDuplicates" && (b.AllowDuplicates ?? true));
        bool allowPlayerDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowPlayerDuplicates" && (b.AllowPlayerDuplicates ?? true));
        var tierConstraint = behaviors.FirstOrDefault(b => b.Type == "TSpawnBehaviorTier");

        var filters = grp.GroupNode?.Filters ?? [];
        var idListFilters = filters.Where(f => f.Type == "TSpawnFilterIdList" && f.Ids != null).ToList();
        var queryFilters = filters.Where(f => f.Type == "TSpawnFilterQuery").ToList();
        bool hasExplicitIdList = idListFilters.Count > 0 || grp.CardIds.Count > 0;

        var heroFilteredIds = new List<(string TemplateId, CardTier Tier)>();
        IEnumerable<string> rawIds;
        if (idListFilters.Count > 0)
        {
            rawIds = idListFilters.SelectMany(f => f.Ids!);
        }
        else if (grp.CardIds.Count > 0)
        {
            rawIds = grp.CardIds;
        }
        else if (queryFilters.Count > 0 && ctx.Repo != null)
        {
            rawIds = ctx.Repo.AlwaysSpawnableItemsAndSkills.Select(c => c.Id);
        }
        else
        {
            return (heroFilteredIds, allowDup);
        }

        string playerHero = ctx.SelfPlayer.Hero;
        var ownedTemplateIds = allowPlayerDup
            ? null
            : ctx.SelfPlayer.EnumerateAllOwnedCards()
                .Select(c => c.Template.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ownedSkillMaxTiers = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var sk in ctx.SelfPlayer.Skills)
        {
            if (sk.IsDestroyed) continue;
            if (!ownedSkillMaxTiers.TryGetValue(sk.Template.Id, out var prevTier) || sk.Tier > prevTier)
                ownedSkillMaxTiers[sk.Template.Id] = sk.Tier;
        }

        var ownedItemMaxTiers = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in ctx.SelfPlayer.Hand.Cards.Concat(ctx.SelfPlayer.Stash.Cards))
        {
            if (it.IsDestroyed) continue;
            if (!ownedItemMaxTiers.TryGetValue(it.Template.Id, out var prevTier) || it.Tier > prevTier)
                ownedItemMaxTiers[it.Template.Id] = it.Tier;
        }

        HashSet<CardTier>? constrainedTiers = null;
        if (tierConstraint?.Tiers is { Count: > 0 })
        {
            constrainedTiers = new HashSet<CardTier>();
            foreach (var rawTier in tierConstraint.Tiers)
            {
                if (Enum.TryParse<CardTier>(rawTier, true, out var parsed))
                    constrainedTiers.Add(parsed);
            }
        }

        var dedupedBase = new List<(string TemplateId, CardTier Tier)>();
        var baseSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in rawIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !baseSeen.Add(id)) continue;
            if (ctx.SelfPlayer.ExclusionSet.Contains(id) || (ctx.Repo?.IsExcluded(id) ?? false)) continue;
            if (isAssetValid != null && !isAssetValid(id)) continue;

            var tpl = ctx.Repo?.GetCard(id);
            CardTier resolvedTier = CardTier.Bronze;
            if (tpl != null)
            {
                bool isExplicitSynthetic = hasExplicitIdList
                                        && GameDataRepository.IsSyntheticTestTemplate(tpl);
                if (string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase) && !isExplicitSynthetic)
                    continue;

                if (!hasExplicitIdList &&
                    !string.Equals(tpl.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                    !GameDataRepository.IsSyntheticTestTemplate(tpl))
                    continue;

                if (ctx.Repo != null &&
                    ctx.Repo.HasLocalizationLoaded &&
                    !GameDataRepository.IsSyntheticTestTemplate(tpl) &&
                    tpl.Type is "Item" or "Skill" or "EventEncounter" or "EncounterStep" or "PedestalEncounter" &&
                    !ctx.Repo.HasZhTranslation(tpl))
                    continue;

                if (!Enum.TryParse<CardTier>(tpl.StartingTier, true, out var parsedStart))
                    continue;

                resolvedTier = SpawnQueryEngine.ClampToSupportedTier(tpl, parsedStart);
                if (tpl.Type is "Item" or "Skill" && tpl.Tiers is { Count: > 0 })
                {
                    if (!tpl.Tiers.ContainsKey(resolvedTier.ToString()))
                        continue;
                }

                if (tpl.SelectionRequirements != null)
                {
                    bool reqOk = tpl.SelectionRequirements.Type == "TSelectionRequirement"
                        ? ValueAndConditionEvaluator.EvaluatePrerequisites(tpl.SelectionRequirements.Prerequisites, ctx)
                        : (tpl.SelectionRequirements.Type.StartsWith("TPrerequisite", StringComparison.Ordinal)
                            ? ValueAndConditionEvaluator.EvaluatePrerequisite(tpl.SelectionRequirements, ctx)
                            : ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(tpl.SelectionRequirements, ctx));
                    if (!reqOk) continue;
                }

                // 关键修复（V13.1 问题 #5）：GameData.db 中 Heroes==["Common"] 的 124 张活跃技能有 123 张是野怪专属技能，
                // 跨职业通用技能本身已在 Heroes 中显式列全全部 8 英雄；因此对 Item 与 Skill 均禁止无条件放行 Common，
                // 仅在显式声明 includeCommonAndHero、IdList 或正向 ConstraintHero 时才放行。
                bool hasPositiveHeroConstraint = false;
                foreach (var qf in queryFilters)
                {
                    var cons = qf.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
                    if (cons != null && HasPositiveHeroConstraint(cons))
                    {
                        hasPositiveHeroConstraint = true;
                        break;
                    }
                }

                if (!ignoreHero && !hasPositiveHeroConstraint && tpl.Heroes.Count > 0)
                {
                    if (excludePlayerHero)
                    {
                        if (tpl.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase))
                            continue;
                    }
                    else
                    {
                        bool matchesPlayerHero = tpl.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase);
                        bool isItemOrSkill = string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
                        bool allowCommon = !isItemOrSkill
                                        || hasExplicitIdList
                                        || includeCommonAndHero;
                        bool matchesCommon = allowCommon && tpl.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase);
                        if (!matchesPlayerHero && !matchesCommon)
                            continue;
                    }
                }

                bool queryOk = true;
                foreach (var qf in queryFilters)
                {
                    var cons = qf.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
                    if (cons != null && !ValueAndConditionEvaluator.EvaluateTemplateConstraint(cons, tpl, playerHero))
                    {
                        queryOk = false;
                        break;
                    }
                }
                if (!queryOk) continue;

                // 修复 P1-12：TSpawnBehaviorTier 对 Item/Skill 校验模板是否支持目标品阶（DefaultTier <= targetTier && Tiers.ContainsKey），
                // 并把匹配到的最高合法约束品阶记录到 resolvedTier，彻底修复按 StartingTier 错滤及丢品阶问题！
                if (constrainedTiers is { Count: > 0 } && tpl.Type is "Item" or "Skill")
                {
                    bool isNot = tierConstraint!.IsNot == true;
                    CardTier? matchedTier = null;
                    for (int ti = 0; ti < AllCardTiers.Length; ti++)
                    {
                        var candidateTier = AllCardTiers[ti];
                        bool matchesConstraint = isNot
                            ? !constrainedTiers.Contains(candidateTier)
                            : constrainedTiers.Contains(candidateTier);
                        bool supportsTier = tpl.Tiers == null || tpl.Tiers.Count == 0 || tpl.Tiers.ContainsKey(candidateTier.ToString());
                        if (matchesConstraint && candidateTier >= tpl.DefaultTier && supportsTier)
                        {
                            matchedTier = candidateTier;
                            if (!isNot) break; // 取满足正向约束的最低合法品阶（如 Silver/Gold），不越阶膨胀
                        }
                    }
                    if (!matchedTier.HasValue)
                        continue;
                    resolvedTier = matchedTier.Value;
                }

                // Q38：已持有 Diamond+ 或高于升级选项发放品阶的技能/物品不可再融合升级，永不进入 LevelUp 候选池
                if (string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                    ownedSkillMaxTiers.TryGetValue(id, out var ownedSkillTier))
                {
                    if (ownedSkillTier >= CardTier.Diamond || ownedSkillTier > resolvedTier)
                        continue;
                }
                else if (string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                         ownedItemMaxTiers.TryGetValue(id, out var ownedItemTier))
                {
                    if (ownedItemTier >= CardTier.Diamond || ownedItemTier > resolvedTier)
                        continue;
                }
            }

            dedupedBase.Add((id, resolvedTier));
            if (ownedTemplateIds == null || !ownedTemplateIds.Contains(id))
                heroFilteredIds.Add((id, resolvedTier));
        }

        // 若因去重导致池空，放宽已拥有去重限制（仍严格守住英雄归属、高阶上限与白名单边界）
        if (heroFilteredIds.Count == 0 && dedupedBase.Count > 0)
            return (dedupedBase, allowDup);

        return (heroFilteredIds, allowDup);
    }

    private sealed class GroupDrawState
    {
        public RewardGroup Group { get; }
        public List<(string TemplateId, CardTier Tier)> Candidates { get; }
        public int RemainingPicks { get; private set; }
        public bool AllowDuplicates { get; }
        private int _seqIdx;

        public GroupDrawState(RewardGroup group, List<(string TemplateId, CardTier Tier)> candidates, int limit, bool allowDuplicates)
        {
            Group = group;
            Candidates = candidates;
            RemainingPicks = limit;
            AllowDuplicates = allowDuplicates;
        }

        public bool HasRemaining(IReadOnlyCollection<(string TemplateId, CardTier Tier)> alreadyPicked)
            => RemainingPicks > 0 && AvailableCandidates(alreadyPicked).Count > 0;

        public (string TemplateId, CardTier Tier)? PickOne(IReadOnlyCollection<(string TemplateId, CardTier Tier)> alreadyPicked, Random rng)
        {
            if (RemainingPicks <= 0) return null;
            var avail = AvailableCandidates(alreadyPicked);
            if (avail.Count == 0) return null;

            (string TemplateId, CardTier Tier) chosen;
            if (string.Equals(Group.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase))
            {
                // D2-P2-03：当不允许重复（!AllowDuplicates）时，avail 已移除已抽项，直接取 avail[0] 杜绝跳项；允许重复时按 _seqIdx 模数顺序推进
                if (!AllowDuplicates)
                {
                    chosen = avail[0];
                }
                else
                {
                    chosen = avail[_seqIdx % avail.Count];
                    _seqIdx++;
                }
            }
            else
            {
                chosen = avail[rng.Next(avail.Count)];
            }

            RemainingPicks--;
            return chosen;
        }

        private List<(string TemplateId, CardTier Tier)> AvailableCandidates(IReadOnlyCollection<(string TemplateId, CardTier Tier)> alreadyPicked)
        {
            if (AllowDuplicates) return Candidates;
            return Candidates
                .Where(c => !alreadyPicked.Any(p => string.Equals(p.TemplateId, c.TemplateId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>
    /// 将玩家升到的目标新等级（如 Lv1->Lv2 传 2，Lv9->Lv10 传 10）映射为 GameData.db level_ups 表主键 Id：
    /// 官方 BazaarCardDealer.HandleOnLevelUpEvent 在等级自增前调用 GetLevelUpById(oldLevel)，
    /// 故 targetNewLevel 对应 dbId = Math.Clamp(targetNewLevel - 1, 1, MaxLevel)。
    /// </summary>
    public int MapTargetLevelToDbId(int targetNewLevel)
    {
        if (targetNewLevel <= 0) return 0;
        int rawDbId = Math.Max(1, targetNewLevel - 1);
        return _byLevel.Count > 0 ? Math.Min(rawDbId, MaxLevel) : rawDbId;
    }

    /// <summary>
    /// 判断指定目标新等级对当前英雄是否属于标准多选一升级层（实际至少 3 项，区别于 Dooley/Jules/Hero8 Lv3 必出或 Lv10/19 附魔台 2 选层）。
    /// </summary>
    public bool IsStandardMultiChoiceLevel(int targetNewLevel, RuleExecutionContext ctx, Func<string, bool>? isAssetValid = null)
    {
        var entry = ResolveLevelEntry(targetNewLevel);
        if (entry == null) return true;
        if (string.Equals(entry.SelectionMethod, "Random", StringComparison.OrdinalIgnoreCase))
            return false;

        int limit = entry.TotalLimit ?? (entry.RewardsNode?.Limit != null
            ? (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(entry.RewardsNode.Limit, ctx, 3))
            : 3);
        if (limit is > 0 and < 3) return false;

        // 按官方前置条件、英雄归属与资产白名单过滤后统计真实最大可发选项数，准确区分 1~2 选层与标准 3+ 选层（D2-P2-03）
        var topBehaviors = entry.RewardsNode?.Behaviors ?? [];
        int maxPossiblePicks = 0;
        foreach (var grp in entry.Groups)
        {
            if (grp.GroupNode?.Prerequisites is { Count: > 0 } &&
                !ValueAndConditionEvaluator.EvaluatePrerequisites(grp.GroupNode.Prerequisites, ctx))
            {
                continue;
            }

            var (candidates, _) = ResolveGroupCandidates(grp, topBehaviors, ctx, isAssetValid: isAssetValid);
            if (candidates.Count == 0) continue;

            int grpCap = grp.GroupNode?.Limit != null
                ? Math.Max(1, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(grp.GroupNode.Limit, ctx, grp.Limit)))
                : (grp.ExplicitLimit ?? candidates.Count);
            maxPossiblePicks += Math.Min(Math.Max(1, grpCap), candidates.Count);
        }

        return Math.Min(limit, maxPossiblePicks) >= 3;
    }

    private static bool HasPositiveHeroConstraint(RuleNode? node, bool inverted = false)
    {
        if (node == null) return false;
        bool nextInverted = inverted ^ (node.IsNot == true);
        if (!nextInverted && node.Type is "ConstraintHero" or "ConstraintIsOnlyHero" && node.Heroes is { Count: > 0 })
            return true;
        if (node.Type is "ConstraintAnd" or "ConstraintOr")
        {
            foreach (var sub in node.GetConstraintList(GameDatabaseLoader.JsonOptions))
            {
                if (HasPositiveHeroConstraint(sub, nextInverted))
                    return true;
            }
        }
        return false;
    }

    private LevelUpEntry? ResolveLevelEntry(int targetNewLevel)
    {
        if (targetNewLevel <= 0) return null;
        int dbId = MapTargetLevelToDbId(targetNewLevel);
        if (_byLevel.TryGetValue(dbId, out var e)) return e;
        if (_byLevel.Count > 0)
        {
            int fallbackKey = _byLevel.Keys.Where(k => k <= dbId).DefaultIfEmpty(0).Max();
            if (fallbackKey > 0 && _byLevel.TryGetValue(fallbackKey, out var fb))
                return fb;
        }
        return null;
    }

    public int HealthIncreaseFor(int targetNewLevel)
    {
        if (targetNewLevel <= 0) return 0;
        int dbId = MapTargetLevelToDbId(targetNewLevel);
        if (_byLevel.TryGetValue(dbId, out var e) && e.HealthIncrease > 0)
            return e.HealthIncrease;
        if (_byLevel.Count > 0)
        {
            // P2-3：targetNewLevel - 1 超过 MaxLevel（30）时沿用最高配置级（DB Id=30 +800），不再断崖跌落
            int highest = _byLevel.Keys.Where(k => k <= dbId).DefaultIfEmpty(0).Max();
            if (highest > 0 && _byLevel.TryGetValue(highest, out var capped) && capped.HealthIncrease > 0)
                return capped.HealthIncrease;
        }
        return GetOfficialFallbackHealthIncrease(dbId);
    }

    /// <summary>
    /// 官方 GameData.db level_ups 权威血量增量回退表（Lv1..10 阶梯增长，Lv11+ 恒定 +800 HP；
    /// 避免在缺失 DB 或误读倒挂 levelups.json 时产生错误增量）。
    /// </summary>
    public static int GetOfficialFallbackHealthIncrease(int level) => level switch
    {
        <= 0 => 0,
        1 => 100,
        2 => 150,
        3 => 200,
        4 => 250,
        5 => 300,
        6 => 350,
        7 => 450,
        8 => 500,
        9 => 600,
        10 => 700,
        _ => 800,
    };
}
