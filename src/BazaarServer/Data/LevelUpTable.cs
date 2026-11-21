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

    private readonly Dictionary<int, LevelUpEntry> _byLevel = new();
    private readonly Random _rng = new();

    public int Count => _byLevel.Count;
    public int MaxLevel => _byLevel.Count > 0 ? _byLevel.Keys.Max() : 0;

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

            return new LevelUpEntry
            {
                Level = level,
                HealthIncrease = health,
                Groups = groups,
                RewardsNode = rewardsNode,
                SelectionMethod = selectionMethod,
                TotalLimit = totalLimit,
            };
        }
        catch { return null; }
    }

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    /// <summary>
    /// 简化版兼容重载（无玩家上下文时保留原行为）：先抽权重 > 0 组或全 0 组，再从 Ids 随机取。
    /// </summary>
    public List<string> RollRewards(int level, int pickCount)
    {
        var result = new List<string>();
        var entry = ResolveLevelEntry(level);
        if (entry == null || entry.Groups.Count == 0) return result;

        int totalWeight = entry.Groups.Sum(g => Math.Max(0, g.RandomWeight));
        for (int i = 0; i < pickCount; i++)
        {
            RewardGroup group;
            if (totalWeight > 0)
            {
                int roll = _rng.Next(totalWeight);
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
                group = entry.Groups[_rng.Next(entry.Groups.Count)];
            }

            if (group.CardIds.Count == 0) continue;
            result.Add(group.CardIds[_rng.Next(group.CardIds.Count)]);
        }
        return result;
    }

    /// <summary>
    /// 官方完整规则升级奖励抽取：
    ///   1. 依据真实 PlayerState 评估每组 Prerequisites（TPrerequisiteRun / TPrerequisiteCardCount 等）。
    ///   2. 解析 TSpawnFilterIdList 与 TSpawnFilterQuery，结合 Heroes / IgnoreHero / 资产白名单 / 去重语义过滤候选池。
    ///   3. 若 Rewards.SelectionMethod == Sequential：先依次执行 RandomWeight == 0 的必出条件组（直至上限），
    ///      再用 RandomWeight > 0 的权重组按权重不重复填满上限。
    ///   4. 若 Rewards.SelectionMethod == Random（如 9 级/18 级附魔台奖励）：按权重在可用组中加权抽满上限。
    /// </summary>
    public List<string> RollRewards(
        int level,
        RuleExecutionContext ctx,
        Func<string, bool>? isAssetValid = null,
        int? pickCountOverride = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var result = new List<string>();
        var entry = ResolveLevelEntry(level);
        if (entry == null || entry.Groups.Count == 0) return result;

        var rng = ctx.Rng ?? _rng;
        var topBehaviors = entry.RewardsNode?.Behaviors ?? [];
        int totalLimit = pickCountOverride ?? entry.TotalLimit ?? 0;
        if (totalLimit <= 0 && entry.RewardsNode?.Limit != null)
            totalLimit = (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(entry.RewardsNode.Limit, ctx, 3));
        if (totalLimit <= 0)
            totalLimit = 3;

        var groupStates = new List<GroupDrawState>();
        foreach (var grp in entry.Groups)
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

        bool isRandomTop = string.Equals(entry.SelectionMethod, "Random", StringComparison.OrdinalIgnoreCase);

        if (!isRandomTop)
        {
            // Pass 1：Sequential 模式下先按顺序处理 RandomWeight == 0 的条件必出组（如 Core Initialization / Gourmet Kitchen / Chef Brolin / MusicNote / Core Upgrade）
            foreach (var gs in groupStates.Where(s => s.Group.RandomWeight <= 0))
            {
                while (result.Count < totalLimit && gs.RemainingPicks > 0)
                {
                    string? picked = gs.PickOne(result, rng);
                    if (picked == null) break;
                    result.Add(picked);
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
        List<string> result,
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

            string? picked = chosen.PickOne(result, rng);
            if (picked == null)
            {
                groups.Remove(chosen);
                continue;
            }
            result.Add(picked);
        }
    }

    private static (List<string> Candidates, bool AllowDuplicates) ResolveGroupCandidates(
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

        var heroFilteredIds = new List<string>();
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
            rawIds = ctx.Repo.CardsById.Values
                .Where(c => string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                            (c.Type == "Item" || c.Type == "Skill"))
                .Select(c => c.Id);
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

        var dedupedBase = new List<string>();
        var baseSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in rawIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !baseSeen.Add(id)) continue;
            if (ctx.SelfPlayer.ExclusionSet.Contains(id) || (ctx.Repo?.IsExcluded(id) ?? false)) continue;
            if (isAssetValid != null && !isAssetValid(id)) continue;

            var tpl = ctx.Repo?.GetCard(id);
            if (tpl != null)
            {
                bool isExplicitSynthetic = (idListFilters.Count > 0 || grp.CardIds.Count > 0)
                                        && GameDataRepository.IsSyntheticTestTemplate(tpl);
                if (string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase) && !isExplicitSynthetic)
                    continue;

                if (idListFilters.Count == 0 && grp.CardIds.Count == 0 &&
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

                if (tpl.Type is "Item" or "Skill" && tpl.Tiers is { Count: > 0 })
                {
                    var supportedTier = SpawnQueryEngine.ClampToSupportedTier(tpl, parsedStart);
                    if (!tpl.Tiers.ContainsKey(supportedTier.ToString()))
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

                // 关键修复：level_ups 里即使使用 TSpawnFilterIdList（例如 L2 G3 29 张卡与 L10 G2..G9 专属升级师），
                // 除非显式声明 TSpawnBehaviorIgnoreHero，仍必须按玩家英雄或 Common 过滤。
                if (!ignoreHero && tpl.Heroes.Count > 0)
                {
                    if (excludePlayerHero)
                    {
                        if (tpl.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase))
                            continue;
                    }
                    else
                    {
                        bool matchesHero = tpl.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase)
                                        || tpl.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase);
                        if (!matchesHero)
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

                if (tierConstraint?.Tiers != null && tierConstraint.Tiers.Count > 0)
                {
                    bool inTiers = tierConstraint.Tiers.Contains(tpl.StartingTier, StringComparer.OrdinalIgnoreCase);
                    if (tierConstraint.IsNot == true ? inTiers : !inTiers)
                        continue;
                }

                // Q38：已持有 Diamond+ 或高于升级选项默认阶级的技能不可再融合/升级，永不进入 LevelUp 候选池（包括去重放宽兜底池）
                if (string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                    ownedSkillMaxTiers.TryGetValue(id, out var ownedSkillTier))
                {
                    var offeredSkillTier = SpawnQueryEngine.ClampToSupportedTier(tpl, tpl.DefaultTier);
                    if (ownedSkillTier >= CardTier.Diamond || ownedSkillTier > offeredSkillTier)
                        continue;
                }
            }

            dedupedBase.Add(id);
            if (ownedTemplateIds == null || !ownedTemplateIds.Contains(id))
                heroFilteredIds.Add(id);
        }

        // 若因去重导致池空，放宽已拥有去重限制（仍严格守住英雄归属与白名单边界）
        if (heroFilteredIds.Count == 0 && dedupedBase.Count > 0)
            return (dedupedBase, allowDup);

        return (heroFilteredIds, allowDup);
    }

    private sealed class GroupDrawState
    {
        public RewardGroup Group { get; }
        public List<string> Candidates { get; }
        public int RemainingPicks { get; private set; }
        public bool AllowDuplicates { get; }
        private int _seqIdx;

        public GroupDrawState(RewardGroup group, List<string> candidates, int limit, bool allowDuplicates)
        {
            Group = group;
            Candidates = candidates;
            RemainingPicks = limit;
            AllowDuplicates = allowDuplicates;
        }

        public bool HasRemaining(IReadOnlyCollection<string> alreadyPicked)
            => RemainingPicks > 0 && AvailableCandidates(alreadyPicked).Count > 0;

        public string? PickOne(IReadOnlyCollection<string> alreadyPicked, Random rng)
        {
            if (RemainingPicks <= 0) return null;
            var avail = AvailableCandidates(alreadyPicked);
            if (avail.Count == 0) return null;

            string chosen;
            if (string.Equals(Group.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase))
            {
                chosen = avail[Math.Min(_seqIdx, avail.Count - 1)];
                _seqIdx++;
            }
            else
            {
                chosen = avail[rng.Next(avail.Count)];
            }

            RemainingPicks--;
            return chosen;
        }

        private List<string> AvailableCandidates(IReadOnlyCollection<string> alreadyPicked)
        {
            if (AllowDuplicates) return Candidates;
            return Candidates
                .Where(id => !alreadyPicked.Contains(id, StringComparer.OrdinalIgnoreCase))
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
    public bool IsStandardMultiChoiceLevel(int targetNewLevel, RuleExecutionContext ctx)
    {
        var entry = ResolveLevelEntry(targetNewLevel);
        if (entry == null) return true;
        if (string.Equals(entry.SelectionMethod, "Random", StringComparison.OrdinalIgnoreCase))
            return false;

        int limit = entry.TotalLimit ?? (entry.RewardsNode?.Limit != null
            ? (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(entry.RewardsNode.Limit, ctx, 3))
            : 3);
        if (limit is > 0 and < 3) return false;

        // 按官方前置条件与英雄归属过滤后统计真实最大可发选项数，准确区分 1~2 选层与标准 3+ 选层
        var topBehaviors = entry.RewardsNode?.Behaviors ?? [];
        int maxPossiblePicks = 0;
        foreach (var grp in entry.Groups)
        {
            if (grp.GroupNode?.Prerequisites is { Count: > 0 } &&
                !ValueAndConditionEvaluator.EvaluatePrerequisites(grp.GroupNode.Prerequisites, ctx))
            {
                continue;
            }

            var (candidates, _) = ResolveGroupCandidates(grp, topBehaviors, ctx, isAssetValid: null);
            if (candidates.Count == 0) continue;

            int grpCap = grp.GroupNode?.Limit != null
                ? Math.Max(1, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(grp.GroupNode.Limit, ctx, grp.Limit)))
                : (grp.ExplicitLimit ?? candidates.Count);
            maxPossiblePicks += Math.Min(Math.Max(1, grpCap), candidates.Count);
        }

        return Math.Min(limit, maxPossiblePicks) >= 3;
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
