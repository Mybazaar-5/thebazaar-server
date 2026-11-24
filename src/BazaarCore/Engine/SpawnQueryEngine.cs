using System.Numerics;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 生成查询引擎：完整实现 TSpawnContextQuery / TSpawnGroup / 全 Filter / 全 Behavior 与每日品质概率表。
/// </summary>
public static class SpawnQueryEngine
{
    private static readonly CardTier[] AllCardTiers = Enum.GetValues<CardTier>();
    private static readonly HashSet<string> PlayableHeroSet = new(HeroNames.PlayableHeroes, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 经由 GameData.db + valid_card_ids.json + zh-CN.bytes 核验的重点 GuidOnly 模板显式白名单：
    /// 涵盖 Fates (Futura) 及其 Crossroads 基座、The Travel Agent、Surprise Me、Expedition 房间/门票与衍生设备。
    /// </summary>
    public static IReadOnlySet<string> ExplicitGuidOnlyAllowlist { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Fates (Futura) & Fate's Crossroads 11 个 GuidOnly 基座
        "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10",
        "4df2e5f8-1a7f-4046-b374-8f1642b24751",
        "5f65bbe1-bf39-4fe7-8eeb-57b669df1ee6",
        "9c1f2654-3077-4c4e-b38e-946aed17cbfb",
        "c1e69f94-fa5a-4d96-bb3e-1a4a0c8b4b04",
        "47c256d3-6f3e-495b-b119-12739adda129",
        "320e7f80-334b-440c-8cc1-0b54e3141335",
        "c3925fd2-d174-40ff-8d15-3d9c41b3615b",
        "81110f4a-ab1e-4a9e-af1f-a0a4ce44b629",
        "41e6d3fc-2795-4107-ba0a-29ecad00f37b",
        "8dead654-6cd3-4819-a0f1-b9d5a70b59f7",
        "f1878b7e-cad6-4301-a5c1-bccf5d35da7b",
        // The Travel Agent & Surprise Me
        "002d8f6d-0e36-4921-89ed-3f0f28682d7b",
        "32dd98de-811c-45bc-98ab-9e416ccefba7",
        // Expedition Tickets
        "77c8ef2d-62c8-4214-993d-648915c93e83",
        "5bd0a69d-420c-4cd0-93ec-07582c688a5b",
        "24dc340b-bb3d-4c9b-8715-a807c0c06a8c",
        // Expedition Rooms & Tiny World Depleted Device
        "91287d25-7f63-4826-8c42-06ed1bf83213",
        "ead0a1ed-703e-445e-ba43-9a7df5d64f54",
        "42db3205-58d5-4cfe-b537-14091b518ff6",
        "021639c0-4a9c-49d8-8254-6376994823e2",
    };

    /// <summary>判断模板 ID 是否处于显式 GuidOnly 白名单中。</summary>
    public static bool IsExplicitlyAllowedGuidOnlyId(string? templateId)
        => !string.IsNullOrWhiteSpace(templateId) && ExplicitGuidOnlyAllowlist.Contains(templateId);

    internal static bool HasBehavior(RuleNode? spawnContext, string behaviorType)
    {
        if (spawnContext == null) return false;
        if (spawnContext.Behaviors != null && spawnContext.Behaviors.Any(b => string.Equals(b.Type, behaviorType, StringComparison.Ordinal)))
            return true;
        if (spawnContext.Groups != null)
        {
            foreach (var grp in spawnContext.Groups)
            {
                if (grp.Behaviors != null && grp.Behaviors.Any(b => string.Equals(b.Type, behaviorType, StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 按匹配的流派比特数计算非凭空加权倍率：未匹配返回 1.0，命中 1 个比特返回 <paramref name="weight"/>，
    /// 命中 ≥2 个比特按 <c>Math.Min(cap, weight + (bits - 1))</c> 递增并封顶。
    /// </summary>
    public static double WeightMultiplierForMask(
        ulong cardMask,
        ulong selectedMask,
        double weight = 2.0,
        double cap = 3.0)
    {
        if (selectedMask == 0UL || cardMask == 0UL)
            return 1.0;

        int bits = BitOperations.PopCount(cardMask & selectedMask);
        if (bits <= 0)
            return 1.0;

        double safeWeight = double.IsFinite(weight) ? Math.Max(0.0, weight) : 2.0;
        double safeCap = double.IsFinite(cap) ? Math.Max(0.0, cap) : 3.0;
        return Math.Min(safeCap, safeWeight + (bits - 1));
    }

    public static List<CardInstance> SpawnCards(
        RuleNode? spawnContext,
        RuleExecutionContext ctx,
        int? defaultCountOverride = null,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers = null,
        IReadOnlySet<string>? validAssetIds = null)
        => SpawnCards(
            spawnContext,
            ctx,
            defaultCountOverride,
            ownedMaxTiers,
            validAssetIds,
            archetypeResolver: null,
            rules: null);

    public static List<CardInstance> SpawnCards(
        RuleNode? spawnContext,
        RuleExecutionContext ctx,
        int? defaultCountOverride,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers,
        IReadOnlySet<string>? validAssetIds,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules = null)
        => SpawnCards(
            spawnContext,
            ctx,
            defaultCountOverride,
            ownedMaxTiers,
            validAssetIds,
            archetypeResolver,
            rules,
            maxItemSlotWidthProvider: null,
            spawnAppliesTiny: false,
            tryAcceptCard: null);

    /// <summary>计算物品模板在考虑 Tiny 属性（自身或生成附带 Tiny &gt; 0）后占据的棋盘物理格数（1..3）。</summary>
    public static int GetEffectiveSpawnItemSlotWidth(CardTemplate tpl, bool hasTinyAttribute = false)
    {
        if (tpl == null) return 1;
        bool isTiny = hasTinyAttribute
                   || tpl.Tags.Contains("Tiny", StringComparer.OrdinalIgnoreCase)
                   || (tpl.Attributes != null && tpl.Attributes.GetValueOrDefault("Tiny", 0) > 0);
        if (!isTiny) return tpl.CardSize.SlotCount();
        return tpl.CardSize == CardSize.Large ? 2 : 1;
    }

    public static List<CardInstance> SpawnCards(
        RuleNode? spawnContext,
        RuleExecutionContext ctx,
        int? defaultCountOverride,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers,
        IReadOnlySet<string>? validAssetIds,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules,
        Func<int?>? maxItemSlotWidthProvider,
        bool spawnAppliesTiny = false,
        Func<CardInstance, bool>? tryAcceptCard = null)
    {
        var results = new List<CardInstance>();
        if (spawnContext == null) return results;

        // 统一从上下文回退继承流派解析器与自定义规则，覆盖 AbilityExecutor / EncounterStepEngine / 战利品等全部生成链路
        archetypeResolver ??= ctx.ArchetypeResolver;
        rules ??= ctx.CustomRules;

        var repo = ctx.Repo;
        var player = ctx.SelfPlayer;
        player.EnsureExclusionSetInitialized(repo?.GameMode);

        Dictionary<string, CardTier>? ownedLookup = null;
        if (ownedMaxTiers is { Count: > 0 })
        {
            ownedLookup = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in ownedMaxTiers)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (!ownedLookup.TryGetValue(k, out var existing) || v > existing)
                    ownedLookup[k] = v;
            }
        }

        IReadOnlySet<string>? validAssetLookup = null;
        if (validAssetIds != null)
        {
            validAssetLookup = validAssetIds is HashSet<string> hs && hs.Comparer == StringComparer.OrdinalIgnoreCase
                ? hs
                : new HashSet<string>(validAssetIds, StringComparer.OrdinalIgnoreCase);
        }

        int totalLimit = defaultCountOverride ?? (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(spawnContext.Limit, ctx, 3));
        if (totalLimit <= 0) totalLimit = 3;

        if (string.Equals(spawnContext.Type, "TSpawnContextMirror", StringComparison.Ordinal))
        {
            var items = player.Hand.Cards.Where(c => !c.IsDestroyed && c.Template.Type == "Item").ToList();
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = ctx.Rng.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
            for (int i = 0; i < items.Count && results.Count < totalLimit; i++)
            {
                var src = items[i];
                int? maxW = maxItemSlotWidthProvider?.Invoke();
                if (maxW.HasValue && GetEffectiveSpawnItemSlotWidth(src.Template, spawnAppliesTiny || src.IsTiny) > maxW.Value)
                    continue;

                var mirrored = new CardInstance(src.Template, src.Tier, src.Enchantment)
                {
                    AttachedStandardPrices = repo?.GameMode.StandardPrices
                };
                if (tryAcceptCard != null && !tryAcceptCard(mirrored))
                    continue;
                results.Add(mirrored);
            }
            return results;
        }

        var topBehaviors = spawnContext.Behaviors ?? [];

        var groups = spawnContext.Groups ?? [];
        if (groups.Count == 0) return results;

        bool sequential = string.Equals(spawnContext.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase);
        HashSet<string>? rejectedTemplateIds = null;

        if (sequential)
        {
            var eligibleSeqGroups = groups
                .Where(g => ValueAndConditionEvaluator.EvaluatePrerequisites(g.Prerequisites, ctx))
                .ToList();
            foreach (var grp in eligibleSeqGroups)
            {
                if (results.Count >= totalLimit) break;
                // 当 Sequential 仅单个有效组且组自身 Limit 为 null（如 Mystery Bundle / Candy Stash）时，直接采用外层 totalLimit
                int defaultGroupLimit = eligibleSeqGroups.Count == 1 ? totalLimit : 1;
                int gLimit = Math.Max(1, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(grp.Limit, ctx, defaultGroupLimit)));
                int groupPicked = 0;
                int consecutiveRejects = 0;
                int maxAttempts = Math.Max(gLimit + 6, gLimit * 3);
                for (int attempt = 0; attempt < maxAttempts && groupPicked < gLimit && results.Count < totalLimit; attempt++)
                {
                    int? curMaxWidth = maxItemSlotWidthProvider?.Invoke();
                    var picked = PickFromGroup(grp, topBehaviors, ctx, results, ownedLookup, validAssetLookup, archetypeResolver, rules, curMaxWidth, spawnAppliesTiny, gLimit, rejectedTemplateIds);
                    if (picked == null) break;
                    if (tryAcceptCard != null && !tryAcceptCard(picked))
                    {
                        bool isItemNoSpace = string.Equals(picked.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
                            && maxItemSlotWidthProvider != null
                            && (maxItemSlotWidthProvider() ?? 1) <= 0;
                        rejectedTemplateIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (isItemNoSpace || !rejectedTemplateIds.Add(picked.Template.Id) || ++consecutiveRejects >= 6)
                            break;
                        continue;
                    }
                    consecutiveRejects = 0;
                    results.Add(picked);
                    groupPicked++;
                }
            }

            // 若多组 Sequential 仍有未填满的配额，仅对具备多选潜力（含 Query / 多 ID 列表 / Target）且无显式 Limit 的有效组补足至 totalLimit，
            // 彻底消除对单 ID 固定选项组（如 Tiny Furry Creature / BazaarCON / The Docks 等 11 个事件）的 40 次重复空转。
            var refillableSeqGroups = eligibleSeqGroups
                .Where(g => g.Limit == null && HasMultiPickPotential(g))
                .ToList();
            int seqSafety = 0;
            int seqConsecutiveRejects = 0;
            while (results.Count < totalLimit && refillableSeqGroups.Count > 0 && seqSafety++ < 24)
            {
                bool anyAdded = false;
                bool anyRejected = false;
                foreach (var grp in refillableSeqGroups)
                {
                    if (results.Count >= totalLimit) break;
                    int? curMaxWidth = maxItemSlotWidthProvider?.Invoke();
                    var picked = PickFromGroup(grp, topBehaviors, ctx, results, ownedLookup, validAssetLookup, archetypeResolver, rules, curMaxWidth, spawnAppliesTiny, totalLimit, rejectedTemplateIds);
                    if (picked == null) continue;
                    if (tryAcceptCard == null || tryAcceptCard(picked))
                    {
                        results.Add(picked);
                        anyAdded = true;
                        seqConsecutiveRejects = 0;
                    }
                    else
                    {
                        bool isItemNoSpace = string.Equals(picked.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
                            && maxItemSlotWidthProvider != null
                            && (maxItemSlotWidthProvider() ?? 1) <= 0;
                        rejectedTemplateIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (isItemNoSpace || !rejectedTemplateIds.Add(picked.Template.Id) || ++seqConsecutiveRejects >= 6)
                        {
                            anyRejected = false;
                            break;
                        }
                        anyRejected = true;
                    }
                }
                if (!anyAdded && !anyRejected) break;
            }
        }
        else
        {
            var eligibleGroups = groups
                .Where(g => ValueAndConditionEvaluator.EvaluatePrerequisites(g.Prerequisites, ctx))
                .ToList();
            if (eligibleGroups.Count == 0) return results;

            var groupRemaining = new Dictionary<RuleNode, int>();
            foreach (var g in eligibleGroups)
            {
                if (g.Limit != null)
                    groupRemaining[g] = Math.Max(0, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(g.Limit, ctx, 1)));
            }

            int safety = 0;
            int rndConsecutiveRejects = 0;
            while (results.Count < totalLimit && safety++ < 40)
            {
                var activeGroups = eligibleGroups
                    .Where(g => !groupRemaining.TryGetValue(g, out int rem) || rem > 0)
                    .ToList();
                if (activeGroups.Count == 0) break;

                var grp = PickWeightedGroup(activeGroups, ctx.Rng);
                if (grp == null) break;
                int? curMaxWidth = maxItemSlotWidthProvider?.Invoke();
                int grpTarget = groupRemaining.TryGetValue(grp, out int gRem) ? gRem : totalLimit;
                var picked = PickFromGroup(grp, topBehaviors, ctx, results, ownedLookup, validAssetLookup, archetypeResolver, rules, curMaxWidth, spawnAppliesTiny, grpTarget, rejectedTemplateIds);
                if (picked == null)
                {
                    // 该组候选池已耗尽时移除，避免空转消耗次数导致少发
                    eligibleGroups.Remove(grp);
                    continue;
                }
                if (tryAcceptCard == null || tryAcceptCard(picked))
                {
                    results.Add(picked);
                    rndConsecutiveRejects = 0;
                    if (groupRemaining.TryGetValue(grp, out int rem))
                        groupRemaining[grp] = rem - 1;
                }
                else
                {
                    bool isItemNoSpace = string.Equals(picked.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
                        && maxItemSlotWidthProvider != null
                        && (maxItemSlotWidthProvider() ?? 1) <= 0;
                    if (isItemNoSpace)
                        break;
                    rejectedTemplateIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!rejectedTemplateIds.Add(picked.Template.Id) || ++rndConsecutiveRejects >= 8)
                    {
                        eligibleGroups.Remove(grp);
                        rndConsecutiveRejects = 0;
                    }
                }
            }
        }

        return results;
    }

    /// <summary>从候选项中按非负权重函数执行随机抽样（总权重 <= 0 时退化为等概率抽样）。</summary>
    public static T? PickWeighted<T>(IReadOnlyList<T> items, Func<T, double> weightFn, Random rng)
    {
        if (items.Count == 0) return default;
        double sum = 0;
        foreach (var item in items) sum += Math.Max(0, weightFn(item));
        if (sum <= 0) return items[rng.Next(items.Count)];
        double roll = rng.NextDouble() * sum;
        double acc = 0;
        foreach (var item in items)
        {
            acc += Math.Max(0, weightFn(item));
            if (roll <= acc) return item;
        }
        return items[^1];
    }

    /// <summary>按流派倾向规则从已过滤的合法候选池执行非凭空加权抽样。</summary>
    public static T? PickWeightedByArchetype<T>(
        IReadOnlyList<T> items,
        Func<T, string> idSelector,
        Random rng,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules)
    {
        if (items.Count == 0) return default;
        ulong selectedMask = rules?.SelectedArchetypeMask ?? 0UL;
        if (items.Count == 1 || selectedMask == 0UL || archetypeResolver == null)
            return items[rng.Next(items.Count)];

        double weight = rules!.ArchetypeWeightMultiplier;
        double cap = rules.ArchetypeMultiMatchCap;
        return PickWeighted(
            items,
            item => WeightMultiplierForMask(archetypeResolver(idSelector(item)), selectedMask, weight, cap),
            rng);
    }

    private static CardTemplate PickCandidateWithArchetypeWeight(
        IReadOnlyList<CardTemplate> candidates,
        Random rng,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules,
        string? playerHero = null)
    {
        if (candidates.Count <= 1)
            return candidates[0];

        // 跨英雄混池模式（CrossHeroMayhem）：按 OwnHeroWeightBias（默认 40%）保护本英雄核心组件抽中率，其余 60% 分布于跨英雄池
        IReadOnlyList<CardTemplate> effectivePool = candidates;
        if (rules?.SpecialModeId == ESpecialModeId.CrossHeroMayhem && !string.IsNullOrEmpty(playerHero))
        {
            var ownHeroCards = new List<CardTemplate>();
            var otherHeroCards = new List<CardTemplate>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Heroes.Contains(playerHero, StringComparer.OrdinalIgnoreCase))
                    ownHeroCards.Add(c);
                else
                    otherHeroCards.Add(c);
            }
            if (ownHeroCards.Count > 0 && otherHeroCards.Count > 0)
            {
                double bias = Math.Clamp(rules.CrossHeroOwnHeroBias, 0.0, 1.0);
                effectivePool = rng.NextDouble() < bias ? ownHeroCards : otherHeroCards;
            }
        }

        ulong selectedMask = rules?.SelectedArchetypeMask ?? 0UL;
        if (effectivePool.Count <= 1 || selectedMask == 0UL || archetypeResolver == null)
            return effectivePool[rng.Next(effectivePool.Count)];

        double weight = rules!.ArchetypeWeightMultiplier;
        double cap = rules.ArchetypeMultiMatchCap;
        return PickWeighted(
            effectivePool,
            tpl => tpl.Type is "Item" or "Skill"
                ? WeightMultiplierForMask(archetypeResolver(tpl.Id), selectedMask, weight, cap)
                : 1.0,
            rng)!;
    }

    private static RuleNode? PickWeightedGroup(List<RuleNode> groups, Random rng)
        => PickWeighted(groups, g => g.RandomWeight ?? 100, rng);

    /// <summary>
    /// 判断 Sequential 分组是否具备跨多次抽取产出不同卡牌的潜力（含 Query / Target / 多 ID 列表），
    /// 避免在第二轮补抽时对单 ID 固定选项组（如 Tiny Furry Creature / BazaarCON / The Docks）无意义空转。
    /// </summary>
    private static bool HasMultiPickPotential(RuleNode grp)
    {
        if (grp.Filters is not { Count: > 0 } filters)
            return false;

        foreach (var f in filters)
        {
            if (f.Type is "TSpawnFilterQuery" or "TSpawnFilterTarget" or "TSpawnFilterUpgrade")
                return true;
            if (f.Type == "TSpawnFilterIdList" && f.Ids is { Count: > 1 })
                return true;
        }
        return false;
    }

    private const string FaraiEncounterId = "412fb3ec-88de-40b4-a475-10e583189d8a";

    private static CardInstance? PickFromGroup(
        RuleNode grp,
        List<RuleNode> topBehaviors,
        RuleExecutionContext ctx,
        List<CardInstance> alreadyPicked,
        Dictionary<string, CardTier>? ownedLookup,
        IReadOnlySet<string>? validAssetLookup,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules,
        int? maxItemSlotWidth = null,
        bool spawnAppliesTiny = false,
        int targetPickCount = 1,
        HashSet<string>? rejectedTemplateIds = null)
    {
        var behaviors = new List<RuleNode>(topBehaviors);
        if (grp.Behaviors != null) behaviors.AddRange(grp.Behaviors);

        bool ignoreHero = behaviors.Any(b => b.Type == "TSpawnBehaviorIgnoreHero" && (b.IgnoreHero ?? true));
        bool includeCommonAndHero = behaviors.Any(b => b.Type == "TSpawnBehaviorIncludeCommonAndHero");
        bool excludePlayerHero = behaviors.Any(b => b.Type == "TSpawnBehaviorExcludePlayerHero");
        bool ignoreTierTable = behaviors.Any(b => b.Type == "TSpawnBehaviorIgnoreTierTable" && (b.IgnoreTierTable ?? true));
        bool allowDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowDuplicates" && (b.AllowDuplicates ?? true));
        bool explicitForbidDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowDuplicates" && b.AllowDuplicates == false);
        bool allowPlayerDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowPlayerDuplicates" && (b.AllowPlayerDuplicates ?? true));
        bool explicitForbidPlayerDup = behaviors.Any(b => b.Type == "TSpawnBehaviorAllowPlayerDuplicates" && b.AllowPlayerDuplicates == false);
        bool inheritTier = behaviors.Any(b => b.Type == "TSpawnBehaviorInheritTier" && (b.Inherits ?? true));
        bool inheritEnch = behaviors.Any(b => b.Type == "TSpawnBehaviorInheritEnchantment" && (b.Inherits ?? true));
        bool inheritSize = behaviors.Any(b => b.Type == "TSpawnBehaviorInheritSize" && (b.Inherits ?? true));
        bool downShiftTier = behaviors.Any(b => b.Type == "TSpawnBehaviorDownShiftTier");
        bool forceNative = behaviors.Any(b => b.Type == "TSpawnBehaviorForceNative" && (b.ForceNative ?? true));

        var tierConstraint = behaviors.FirstOrDefault(b => b.Type == "TSpawnBehaviorTier");

        var filters = grp.Filters ?? [];
        var idListFilter = filters.FirstOrDefault(f => f.Type == "TSpawnFilterIdList");
        // 当显式 IdList 目标抽取数大于去重后的模板数（如 Collect Spare Change (4) 单 ID 刷 4 张）且未显式禁止重复时，自动允许重复抽取
        if (!allowDup && !explicitForbidDup && idListFilter?.Ids is { Count: > 0 } && targetPickCount > 1)
        {
            int distinctIds = idListFilter.Ids.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (targetPickCount > distinctIds)
                allowDup = true;
        }
        var targetFilters = filters.Where(f => f.Type == "TSpawnFilterTarget" && f.Target != null).ToList();
        var upgradeFilter = filters.FirstOrDefault(f => f.Type == "TSpawnFilterUpgrade");

        // 预解析查询过滤器树并提取显式英雄/Loot声明，避免在模板遍历中重复反序列化
        var queryConstraints = new List<RuleNode>();
        foreach (var f in filters)
        {
            if (f.Type != "TSpawnFilterQuery") continue;
            var cons = f.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
            if (cons != null) queryConstraints.Add(cons);
        }
        bool allowsLootTag = HasPositiveConstraint(queryConstraints,
            n => n.Type == "ConstraintTag" && n.Tags != null && n.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase));
        bool hasPositiveHeroConstraint = HasPositiveConstraint(queryConstraints,
            n => n.Type is "ConstraintHero" or "ConstraintIsOnlyHero" && n.Heroes is { Count: > 0 });

        HashSet<string>? targetTemplateIds = null;
        if (targetFilters.Count > 0)
        {
            foreach (var tf in targetFilters)
            {
                var resolvedTargets = TargetResolver.ResolveCards(tf.Target, ctx);
                var ids = new HashSet<string>(resolvedTargets.Select(c => c.Template.Id), StringComparer.OrdinalIgnoreCase);
                if (targetTemplateIds == null)
                    targetTemplateIds = ids;
                else
                    targetTemplateIds.IntersectWith(ids);
            }
            if (targetTemplateIds == null || targetTemplateIds.Count == 0)
                return null;
        }

        bool PassesGroupTemplateRules(CardTemplate tpl, bool isOwnedTarget = false)
        {
            if (ctx.SelfPlayer.ExclusionSet.Contains(tpl.Id))
                return false;

            if (targetTemplateIds != null && !targetTemplateIds.Contains(tpl.Id))
                return false;

            if (inheritSize && ctx.SelfCard != null &&
                !string.Equals(tpl.Size, ctx.SelfCard.Template.Size, StringComparison.OrdinalIgnoreCase))
                return false;

            if (maxItemSlotWidth.HasValue &&
                string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                GetEffectiveSpawnItemSlotWidth(tpl, spawnAppliesTiny) > maxItemSlotWidth.Value)
                return false;

            if (!isOwnedTarget)
            {
                bool isSynthetic = GameDataRepository.IsSyntheticTestTemplate(tpl);
                bool isExplicitSynthetic = (idListFilter != null || targetTemplateIds != null) && isSynthetic;
                // V13.1 问题 #6：Farai（法莱，412fb3ec）的 29 张商人送货包裹在 DB 中标为 Never，但 100% 在资产白名单且具简中译名；
                // 仅在 Farai 自身显式 IdList 上下文中对其 Package 子卡优雅放行，防止 Farai 选项残缺或空架。
                bool isFaraiExplicitPackage = idListFilter != null
                    && string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase)
                    && (tpl.InternalName?.Contains("Package", StringComparison.OrdinalIgnoreCase) == true)
                    && (string.Equals(ctx.CurrentEncounterId, FaraiEncounterId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ctx.SelfCard?.Template.Id, FaraiEncounterId, StringComparison.OrdinalIgnoreCase));

                if (string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase)
                    && !isExplicitSynthetic
                    && !isFaraiExplicitPackage)
                    return false;

                bool isAlways = string.Equals(tpl.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase);
                bool isGuidOnly = string.Equals(tpl.SpawningEligibility, "GuidOnly", StringComparison.OrdinalIgnoreCase);

                if (idListFilter != null)
                {
                    // TSpawnFilterIdList 显式放行有效且具简中译名的 Always / GuidOnly（如 Fates/Crossroads/Expedition 房间与门票），Never 严格禁用（Farai 官方包裹除外）
                    if (!isAlways && !isGuidOnly && !isExplicitSynthetic && !isFaraiExplicitPackage)
                        return false;
                }
                else if (targetTemplateIds == null && !isAlways && !isSynthetic)
                {
                    return false;
                }

                if (validAssetLookup != null && !isSynthetic && !validAssetLookup.Contains(tpl.Id))
                    return false;

                if (ctx.Repo != null &&
                    ctx.Repo.HasLocalizationLoaded &&
                    !isSynthetic &&
                    tpl.Type is "Item" or "Skill" or "EventEncounter" or "EncounterStep" or "PedestalEncounter" &&
                    !ctx.Repo.HasZhTranslation(tpl))
                    return false;

                if (tpl.SelectionRequirements != null && !EvaluateTemplateSelectionRequirements(tpl.SelectionRequirements, ctx))
                    return false;

                // TSpawnBehaviorAllowPlayerDuplicates 仅豁免 Item 的 ownedLookup 限制；
                // 已持有满阶 Diamond Skill 永不允许重复刷出（即便显式带 AllowPlayerDuplicates）。
                bool checkOwnedForTpl = ownedLookup != null && (
                    string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase) ||
                    (!allowPlayerDup && string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase)));
                if (checkOwnedForTpl &&
                    ownedLookup!.TryGetValue(tpl.Id, out var ownedMaxTier) &&
                    ownedMaxTier >= CardTier.Diamond)
                    return false;
            }

            // 非精确 ID 列表且非己方卡牌目标时，未显式声明允许 "Loot" 标签的查询严格排除全部战利品卡牌
            if (!isOwnedTarget && idListFilter == null && targetTemplateIds == null &&
                !allowsLootTag && tpl.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            // 遭遇步骤/事件/基座，或在遭遇上下文中通过 TSpawnFilterIdList 发出的候选卡，除非显式声明 TSpawnBehaviorIgnoreHero，否则必须严格校验英雄归属；
            // 仅物品/技能自身的显式 ID 衍生或变形（如 Toolbox->Loupe、Transform->Dog）在非遭遇上下文中放行跨英雄指定 ID。
            bool isEncounterContext = ctx.SelfCard == null
                || ctx.SelfCard.Template.Type is "EventEncounter" or "EncounterStep" or "PedestalEncounter";
            bool enforceHero = !isOwnedTarget && targetTemplateIds == null && (
                idListFilter == null
                || tpl.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter"
                || isEncounterContext);

            if (enforceHero)
            {
                // TSpawnBehaviorExcludePlayerHero 独立生效（即使同时携带 IgnoreHero，如 Learning Crystal）
                if (excludePlayerHero && tpl.Heroes.Contains(ctx.SelfPlayer.Hero, StringComparer.OrdinalIgnoreCase))
                    return false;

                // 未启用 IgnoreHero 且未显式声明正向 ConstraintHero/ConstraintIsOnlyHero 时：
                // - Item / Skill：仅匹配当前玩家英雄（或携带 TSpawnBehaviorExcludePlayerHero 时的其他可玩英雄，以及无归属测试模板）；
                //   V13.1 问题 #5 根治：GameData.db 中 Heroes==["Common"] 的 124 张活跃技能有 123 张为野怪专属技能，
                //   跨职业通用技能本身已在 Heroes 中显式列全全部 8 位英雄，故仅在显式 ConstraintHero:["Common"]（如 Zurphin's Safari / Tiny Furry 5%）、
                //   TSpawnBehaviorIncludeCommonAndHero、IdList 或显式允许 Loot 时才放行 Common；
                // - 遭遇卡（EventEncounter / EncounterStep / PedestalEncounter）：继续允许 Common 归属。
                if (!ignoreHero && !hasPositiveHeroConstraint)
                {
                    bool isCrossHeroMayhem = rules?.SpecialModeId == ESpecialModeId.CrossHeroMayhem;
                    bool matchesAllowedHero;
                    if (excludePlayerHero)
                    {
                        matchesAllowedHero = tpl.Heroes.Count == 0
                            || tpl.Heroes.Any(h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)
                                                && !string.Equals(h, ctx.SelfPlayer.Hero, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (isCrossHeroMayhem && tpl.Type is "Item" or "Skill")
                    {
                        matchesAllowedHero = tpl.Heroes.Count == 0
                            || tpl.Heroes.Any(h => PlayableHeroSet.Contains(h));
                    }
                    else
                    {
                        matchesAllowedHero = tpl.Heroes.Count == 0
                            || tpl.Heroes.Contains(ctx.SelfPlayer.Hero, StringComparer.OrdinalIgnoreCase);
                    }
                    bool isItemOrSkill = string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
                    bool allowCommon = !isItemOrSkill
                                    || idListFilter != null
                                    || includeCommonAndHero
                                    || (allowsLootTag && tpl.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase));
                    bool matchesCommon = allowCommon && tpl.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase);
                    if (!matchesAllowedHero && !matchesCommon)
                        return false;
                }
            }

            foreach (var cons in queryConstraints)
            {
                if (!ValueAndConditionEvaluator.EvaluateTemplateConstraint(cons, tpl, ctx.SelfPlayer.Hero))
                    return false;
            }

            // TSpawnBehaviorTier（例如 Arms Locker (Silver) / Gunpowder Cache (Gold)）：
            // 对 Item/Skill 校验是否能以该目标品阶（DefaultTier <= targetTier && SupportsTier）生成，而非死板要求 StartingTier == targetTier；
            // 对 EncounterStep/EventEncounter（如 The Travel Agent 32dd98de）不以 StartingTier 误杀。
            if (tierConstraint?.Tiers != null && tierConstraint.Tiers.Count > 0 && tpl.Type is "Item" or "Skill")
            {
                var constrained = ParseConstrainedTiers(tierConstraint);
                if (constrained.Count > 0)
                {
                    bool isNot = tierConstraint.IsNot == true;
                    bool hasValidTier = false;
                    for (int ti = 0; ti < AllCardTiers.Length; ti++)
                    {
                        var t = AllCardTiers[ti];
                        bool matchesConstraint = isNot ? !constrained.Contains(t) : constrained.Contains(t);
                        if (matchesConstraint && t >= tpl.DefaultTier && SupportsTier(tpl, t))
                        {
                            hasValidTier = true;
                            break;
                        }
                    }
                    if (!hasValidTier)
                        return false;
                }
            }

            return true;
        }

        // 处理 TSpawnFilterUpgrade：按 UpgradeChance 概率从玩家当前已拥有且可升阶的匹配卡牌中直接抽选融合升级项
        if (upgradeFilter != null && (upgradeFilter.UpgradeChance ?? 0) > 0 && ctx.Rng.NextDouble() < upgradeFilter.UpgradeChance!.Value)
        {
            var ownedPool = new List<CardInstance>();
            foreach (var owned in ctx.SelfPlayer.EnumerateAllOwnedCards())
            {
                if (owned.IsDestroyed || !owned.CanUpgrade()) continue;
                if (!string.IsNullOrEmpty(upgradeFilter.CardType) &&
                    !string.Equals(owned.Template.Type, upgradeFilter.CardType, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (idListFilter?.Ids != null && idListFilter.Ids.Count > 0 &&
                    !idListFilter.Ids.Contains(owned.Template.Id, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!PassesGroupTemplateRules(owned.Template, isOwnedTarget: true)) continue;
                if (!allowDup && alreadyPicked.Any(a => a.Template.Id == owned.Template.Id)) continue;
                ownedPool.Add(owned);
            }
            if (ownedPool.Count > 0)
            {
                var pickedOwned = ownedPool[ctx.Rng.Next(ownedPool.Count)];
                string? upgEnch = BuildSpawnEnchantment(pickedOwned.Template, behaviors, filters, inheritEnch ? ctx.SelfCard : null, ctx.Rng, rules);
                return new CardInstance(pickedOwned.Template, pickedOwned.Tier, upgEnch)
                {
                    AttachedStandardPrices = ctx.Repo?.GameMode.StandardPrices
                };
            }

            // V13.1 问题 #5：100% 纯升级组（如 Grandmaster，UpgradeChance >= 1.0）在无可升级的已拥有卡牌时直接返回 null，
            // 严禁跌落到下方全新卡池并在 IgnoreHero 作用下抽出跨职业或野怪技能。
            if (upgradeFilter.UpgradeChance!.Value >= 1.0 - 1e-9)
                return null;
        }

        IEnumerable<CardTemplate> pool;
        if (idListFilter?.Ids != null && idListFilter.Ids.Count > 0)
        {
            pool = idListFilter.Ids
                .Select(id => ctx.Repo.GetCard(id))
                .Where(c => c != null)!;
        }
        else if (targetTemplateIds != null)
        {
            pool = targetTemplateIds
                .Select(id => ctx.Repo.GetCard(id))
                .Where(c => c != null)!;
        }
        else if (HasExplicitCardTypeConstraint(filters))
        {
            // 若查询过滤器显式约束了 CardType（例如 Street Festival 抽 EventEncounter 商人），直接复用预建冻结索引
            pool = ctx.Repo.AlwaysSpawnableAll;
        }
        else
        {
            pool = ctx.Repo.AlwaysSpawnableItemsAndSkills;
        }

        bool isOwnedPool = targetTemplateIds != null;

        // 预先构建玩家已拥有模板 ID 集合，避免在大池过滤循环内重复分配快照与线性扫描
        HashSet<string>? selfOwnedTplIds = null;
        HashSet<string> GetSelfOwnedTemplateIds()
        {
            if (selfOwnedTplIds != null) return selfOwnedTplIds;
            selfOwnedTplIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ownedCard in ctx.SelfPlayer.EnumerateAllOwnedCards())
                selfOwnedTplIds.Add(ownedCard.Template.Id);
            return selfOwnedTplIds;
        }

        // 过滤条件
        var ruleCompliantPool = new List<CardTemplate>();
        var candidates = new List<CardTemplate>();
        foreach (var tpl in pool)
        {
            if (rejectedTemplateIds != null && rejectedTemplateIds.Contains(tpl.Id))
                continue;
            if (!PassesGroupTemplateRules(tpl, isOwnedTarget: isOwnedPool))
                continue;

            ruleCompliantPool.Add(tpl);

            if (!allowDup && alreadyPicked.Any(a => a.Template.Id == tpl.Id))
                continue;
            bool skipBlanketPlayerDupCheck = ownedLookup != null && string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase);
            if (!skipBlanketPlayerDupCheck && !allowPlayerDup && targetTemplateIds == null && GetSelfOwnedTemplateIds().Contains(tpl.Id))
                continue;

            candidates.Add(tpl);
        }

        // 若仅因去重导致池空，优先从 ruleCompliantPool 恢复（绝不绕过 PassesGroupTemplateRules 及显式 AllowPlayerDuplicates=false 的禁止玩家重复约束）；
        // V13.1 问题 #6：在 !allowDup 模式下绝不把已在 alreadyPicked 中的重复模板塞回结果，防止单 ID 组空转重复发卡。
        if (candidates.Count == 0 && ruleCompliantPool.Count > 0 && (idListFilter?.Ids != null || allowDup))
        {
            var fallbackPool = (explicitForbidPlayerDup && targetTemplateIds == null)
                ? ruleCompliantPool.Where(c => !GetSelfOwnedTemplateIds().Contains(c.Id)).ToList()
                : ruleCompliantPool;
            candidates = fallbackPool
                .Where(c => !alreadyPicked.Any(a => a.Template.Id == c.Id))
                .ToList();
            if (candidates.Count == 0 && allowDup)
                candidates = fallbackPool;
        }
        if (candidates.Count == 0) return null;

        // 当 AllowDuplicates == true 且池内仍有本批次尚未抽过的模板时，优先从非重复子池抽取，
        // 避免上层遭遇/商店按 Template.Id 去重时吞掉已生成的物品（仅在非重复子池耗尽时才允许批内重复）
        if (allowDup && alreadyPicked.Count > 0)
        {
            var distinctInBatch = candidates
                .Where(c => !alreadyPicked.Any(a => a.Template.Id == c.Id))
                .ToList();
            if (distinctInBatch.Count > 0)
                candidates = distinctInBatch;
        }

        bool isSequentialGroup = string.Equals(grp.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase);
        // P2-21：当 TSpawnGroup 显式声明 SelectionMethod == "Sequential" 时，按 IdList 声明序或候选池确定序依次取出
        if (isSequentialGroup && idListFilter?.Ids is { Count: > 0 })
        {
            var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < idListFilter.Ids.Count; idx++)
            {
                if (!orderMap.ContainsKey(idListFilter.Ids[idx]))
                    orderMap[idListFilter.Ids[idx]] = idx;
            }
            candidates.Sort((a, b) =>
            {
                int oa = orderMap.TryGetValue(a.Id, out var ia) ? ia : int.MaxValue;
                int ob = orderMap.TryGetValue(b.Id, out var ib) ? ib : int.MaxValue;
                int cmp = oa.CompareTo(ob);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
            });
        }

        CardTemplate PickCandidateForGroup(List<CardTemplate> sourcePool)
        {
            if (isSequentialGroup)
            {
                if (allowDup && alreadyPicked.Count > 0 && sourcePool.All(c => alreadyPicked.Any(a => a.Template.Id == c.Id)))
                    return sourcePool[alreadyPicked.Count % sourcePool.Count];
                return sourcePool[0];
            }
            return PickCandidateWithArchetypeWeight(sourcePool, ctx.Rng, archetypeResolver, rules, ctx.SelfPlayer.Hero);
        }

        CardTemplate chosen;
        CardTier tier;
        var forcedTiers = (tierConstraint != null && tierConstraint.IsNot != true)
            ? ParseConstrainedTiers(tierConstraint)
            : new HashSet<CardTier>();
        if (forcedTiers.Count == 0 && idListFilter == null && targetTemplateIds == null &&
            HasExplicitLegendaryOnlyConstraint(filters))
        {
            forcedTiers.Add(CardTier.Legendary);
        }

        bool useDayTierFirst = !forceNative
            && !ignoreTierTable
            && !(inheritTier && ctx.SelfCard != null)
            && forcedTiers.Count == 0
            && idListFilter == null
            && targetTemplateIds == null
            && candidates.Any(c => c.Type is "Item" or "Skill");

        if (useDayTierFirst)
        {
            var excludedTiers = (tierConstraint != null && tierConstraint.IsNot == true)
                ? ParseConstrainedTiers(tierConstraint)
                : null;
            if (!TryPickByDayTierFirst(candidates, ctx.CurrentDay, downShiftTier, ctx.Rng, ctx.Repo?.GameMode, excludedTiers, ownedLookup, archetypeResolver, rules, ctx.SelfPlayer.Hero, out chosen!, out tier, isSequential: isSequentialGroup, allowItemPlayerDup: allowPlayerDup))
                return null;
        }
        else if (forcedTiers.Count > 0 && idListFilter == null && targetTemplateIds == null)
        {
            var forcedPool = ownedLookup == null
                ? candidates
                : candidates.Where(c =>
                {
                    bool checkOwned = string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase)
                        || (!allowPlayerDup && string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase));
                    return !checkOwned || GetValidForcedTiersForCandidate(c, forcedTiers, ownedLookup, allowPlayerDup).Count > 0;
                }).ToList();
            if (forcedPool.Count == 0) return null;

            chosen = PickCandidateForGroup(forcedPool);
            if (chosen.Type is "Item" or "Skill")
            {
                var validForced = GetValidForcedTiersForCandidate(chosen, forcedTiers, ownedLookup, allowPlayerDup);
                tier = validForced.Count > 0
                    ? validForced[ctx.Rng.Next(validForced.Count)]
                    : ClampToSupportedTier(chosen, chosen.DefaultTier);
            }
            else
            {
                tier = ClampToSupportedTier(chosen, chosen.DefaultTier);
            }
        }
        else if (inheritTier && ctx.SelfCard != null && idListFilter == null && targetTemplateIds == null)
        {
            var baseTargetTier = ctx.SelfCard.Tier;
            if (downShiftTier && baseTargetTier > CardTier.Bronze)
                baseTargetTier = (CardTier)((int)baseTargetTier - 1);

            var excludedTiers = (tierConstraint != null && tierConstraint.IsNot == true)
                ? ParseConstrainedTiers(tierConstraint)
                : null;

            if (candidates.Any(c => c.Type is "Item" or "Skill"))
            {
                // 按继承品阶起从低到高逐阶向上查找（Bronze -> Silver -> Gold -> Diamond），确保目标阶空池时优先落入最低可用阶而非跨阶随机抽取
                var tryTiers = new List<CardTier>(4);
                if (baseTargetTier <= CardTier.Diamond)
                {
                    for (int t = (int)baseTargetTier; t <= (int)CardTier.Diamond; t++)
                        tryTiers.Add((CardTier)t);
                }
                else
                {
                    tryTiers.Add(CardTier.Legendary);
                }

                List<CardTemplate>? matchedTierPool = null;
                CardTier resolvedTier = baseTargetTier;
                foreach (var tryTier in tryTiers)
                {
                    if (forcedTiers.Count > 0 && !forcedTiers.Contains(tryTier))
                        continue;
                    if (excludedTiers != null && excludedTiers.Contains(tryTier))
                        continue;

                    var tierPool = candidates
                        .Where(c => c.Type is "Item" or "Skill"
                                 && c.DefaultTier <= tryTier
                                 && SupportsTier(c, tryTier)
                                 && IsItemAllowedByOwnedTier(c, tryTier, ownedLookup, allowPlayerDup))
                        .ToList();
                    if (tierPool.Count > 0)
                    {
                        matchedTierPool = tierPool;
                        resolvedTier = tryTier;
                        break;
                    }
                }

                if (matchedTierPool == null || matchedTierPool.Count == 0)
                    return null;

                chosen = PickCandidateForGroup(matchedTierPool);
                tier = ClampToSupportedTier(chosen, resolvedTier);
            }
            else
            {
                chosen = PickCandidateForGroup(candidates);
                tier = ClampToSupportedTier(chosen, chosen.DefaultTier);
            }
        }
        else
        {
            if (ownedLookup != null)
            {
                var eligibleForOwned = FilterCandidatesForOwnedMaxTiers(
                    candidates,
                    ctx.CurrentDay,
                    ignoreTierTable,
                    inheritTier ? ctx.SelfCard : null,
                    downShiftTier,
                    forceNative,
                    forcedTiers,
                    ctx.Repo?.GameMode,
                    ownedLookup,
                    allowPlayerDup);
                if (eligibleForOwned.Count == 0) return null;
                candidates = eligibleForOwned;
            }

            chosen = PickCandidateForGroup(candidates);
            if (forcedTiers.Count > 0 && chosen.Type is "Item" or "Skill")
            {
                var validForced = GetValidForcedTiersForCandidate(chosen, forcedTiers, ownedLookup, allowPlayerDup);
                tier = validForced.Count > 0
                    ? validForced[ctx.Rng.Next(validForced.Count)]
                    : ClampToSupportedTier(chosen, chosen.DefaultTier);
            }
            else
            {
                tier = forceNative
                    ? ClampToSupportedTier(chosen, chosen.DefaultTier)
                    : RollTierForDayWithOwnedConstraint(chosen, ctx.CurrentDay, ignoreTierTable, inheritTier ? ctx.SelfCard : null, downShiftTier, ctx.Rng, ctx.Repo?.GameMode, ownedLookup, allowPlayerDup);
            }
        }

        string? enchant = BuildSpawnEnchantment(chosen, behaviors, filters, inheritEnch ? ctx.SelfCard : null, ctx.Rng, rules);

        return new CardInstance(chosen, tier, enchant)
        {
            AttachedStandardPrices = ctx.Repo?.GameMode.StandardPrices
        };
    }

    private static string? BuildSpawnEnchantment(
        CardTemplate chosen,
        List<RuleNode> behaviors,
        List<RuleNode> filters,
        CardInstance? inheritSource,
        Random rng,
        CustomRunRuleSet? rules = null)
    {
        bool disableInherit = behaviors.Any(b => b.Type == "TSpawnBehaviorInheritEnchantment" && b.Inherits == false);
        string? rawInherited = disableInherit ? null : inheritSource?.Enchantment;
        string? enchant = (!string.IsNullOrEmpty(rawInherited)
                           && chosen.Enchantments != null
                           && chosen.Enchantments.ContainsKey(rawInherited))
            ? rawInherited
            : null;

        var enchBehavior = behaviors.FirstOrDefault(b => b.Type is "TSpawnBehaviorEnchantment" or "TSpawnBehaviorEnchantRandom");
        if (string.IsNullOrEmpty(enchant) && enchBehavior != null)
        {
            var listedWeighted = enchBehavior.GetWeightedEnchantments();
            if (listedWeighted.Count > 0)
            {
                var candidateWeighted = (enchBehavior.IsNot == true
                    ? EnchantmentNames.All
                        .Where(e => !listedWeighted.Any(w => string.Equals(w.Enchantment, e, StringComparison.OrdinalIgnoreCase)))
                        .Select(e => (Enchantment: e, Weight: 1.0))
                    : listedWeighted.Select(w => (w.Enchantment, Weight: Math.Max(0.1, w.Weight))))
                    .Where(w => chosen.Enchantments != null && chosen.Enchantments.ContainsKey(w.Enchantment))
                    .ToList();
                if (candidateWeighted.Count > 0)
                    enchant = PickWeightedEnchantmentName(candidateWeighted, rng);
            }
            else if (enchBehavior.Type == "TSpawnBehaviorEnchantRandom" && chosen.Enchantments is { Count: > 0 } supported)
            {
                var allEligible = EnchantmentNames.All
                    .Where(e => supported.ContainsKey(e))
                    .ToList();
                if (allEligible.Count > 0)
                    enchant = allEligible[rng.Next(allEligible.Count)];
            }
        }

        // 官方数据中大量出售触发/事件（如 Chimeric Egg、Raffle Ticket、Mysterious Gift）在 TSpawnFilterQuery 中以
        // 正向 ConstraintEnchantmentEligible 表达“生成一个带合法随机附魔的物品”，未额外附带 TSpawnBehaviorEnchantRandom
        if (string.IsNullOrEmpty(enchant) && chosen.Enchantments is { Count: > 0 } templateEnchants)
        {
            var constraintWeighted = new List<(string Enchantment, double Weight)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in filters)
            {
                if (f.Type != "TSpawnFilterQuery") continue;
                var cons = f.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
                CollectPositiveEligibleEnchantments(cons, constraintWeighted, seen);
            }

            if (constraintWeighted.Count > 0)
            {
                var valid = constraintWeighted
                    .Where(w => templateEnchants.ContainsKey(w.Enchantment))
                    .ToList();
                if (valid.Count > 0)
                    enchant = PickWeightedEnchantmentName(valid, rng);
            }
        }

        // 肉鸽突变词条：all_enchanted（万物附魔）保证生成的所有物品 100% 附带合法随机附魔
        if (string.IsNullOrEmpty(enchant)
            && rules?.HasMutator(MutatorCatalog.AllEnchanted) == true
            && string.Equals(chosen.Type, "Item", StringComparison.OrdinalIgnoreCase)
            && chosen.Enchantments is { Count: > 0 } allEnchSupported)
        {
            var allEligible = new List<string>(allEnchSupported.Count);
            for (int i = 0; i < EnchantmentNames.All.Length; i++)
            {
                var e = EnchantmentNames.All[i];
                if (allEnchSupported.ContainsKey(e)) allEligible.Add(e);
            }
            if (allEligible.Count > 0)
                enchant = allEligible[rng.Next(allEligible.Count)];
        }

        return enchant;
    }

    private static void CollectPositiveEligibleEnchantments(
        RuleNode? node,
        List<(string Enchantment, double Weight)> output,
        HashSet<string> seen)
    {
        if (node == null || node.IsNot == true) return;
        if (node.Type == "ConstraintEnchantmentEligible")
        {
            foreach (var (ench, weight) in node.GetWeightedEnchantments())
            {
                if (!string.IsNullOrWhiteSpace(ench) && seen.Add(ench))
                    output.Add((ench, Math.Max(0.1, weight)));
            }
            return;
        }
        if (node.Type is "ConstraintAnd" or "ConstraintOr")
        {
            foreach (var child in node.GetConstraintList(GameDatabaseLoader.JsonOptions))
                CollectPositiveEligibleEnchantments(child, output, seen);
        }
    }

    private static string PickWeightedEnchantmentName(List<(string Enchantment, double Weight)> entries, Random rng)
    {
        if (entries.Count == 1) return entries[0].Enchantment;
        double totalWeight = entries.Sum(e => e.Weight);
        if (totalWeight <= 0) return entries[rng.Next(entries.Count)].Enchantment;
        double roll = rng.NextDouble() * totalWeight;
        double acc = 0;
        foreach (var (ench, weight) in entries)
        {
            acc += weight;
            if (roll <= acc) return ench;
        }
        return entries[^1].Enchantment;
    }

    /// <summary>
    /// 按游戏天数掷骰物品/技能品质；若传入 <paramref name="gameMode"/> 则优先使用其真实每日概率表并按总权重归一化。
    /// </summary>
    public static CardTier RollTierForDay(
        CardTemplate tpl,
        int day,
        bool ignoreTierTable,
        CardInstance? inheritSource,
        bool downShift,
        Random rng,
        GameModeData? gameMode = null)
    {
        if (inheritSource != null)
        {
            var t = inheritSource.Tier;
            if (downShift && t > CardTier.Bronze) t = (CardTier)((int)t - 1);
            return ClampToSupportedTier(tpl, t);
        }

        if (ignoreTierTable || tpl.Type is not ("Item" or "Skill"))
            return ClampToSupportedTier(tpl, tpl.DefaultTier);

        var (bronze, silver, gold, diamond) = GetEffectiveDayTierWeights(gameMode, day);
        double totalWeight = bronze + silver + gold + diamond;
        if (totalWeight > 0)
        {
            double roll = rng.NextDouble() * totalWeight;
            CardTier picked = roll < bronze ? CardTier.Bronze
                            : roll < bronze + silver ? CardTier.Silver
                            : roll < bronze + silver + gold ? CardTier.Gold
                            : CardTier.Diamond;
            if (picked < tpl.DefaultTier) picked = tpl.DefaultTier;
            return ClampToSupportedTier(tpl, picked);
        }

        return ClampToSupportedTier(tpl, tpl.DefaultTier);
    }

    private static bool HasPositiveConstraint(List<RuleNode> roots, Func<RuleNode, bool> predicate)
    {
        foreach (var root in roots)
        {
            if (NodeContainsPositiveConstraint(root, predicate, inverted: false))
                return true;
        }
        return false;
    }

    private static bool NodeContainsPositiveConstraint(RuleNode? node, Func<RuleNode, bool> predicate, bool inverted)
    {
        if (node == null) return false;
        bool nextInverted = inverted ^ (node.IsNot == true);
        if (!nextInverted && predicate(node))
            return true;
        if (node.Type is "ConstraintAnd" or "ConstraintOr")
        {
            foreach (var child in node.GetConstraintList(GameDatabaseLoader.JsonOptions))
            {
                if (NodeContainsPositiveConstraint(child, predicate, nextInverted))
                    return true;
            }
        }
        return false;
    }

    private static bool HasExplicitCardTypeConstraint(List<RuleNode> filters)
    {
        foreach (var f in filters)
        {
            if (f.Type != "TSpawnFilterQuery") continue;
            var root = f.GetSingleConstraint(GameDatabaseLoader.JsonOptions);
            if (ConstraintMentionsCardType(root))
                return true;
        }
        return false;
    }

    private static bool ConstraintMentionsCardType(RuleNode? node)
    {
        if (node == null) return false;
        if (node.Type == "ConstraintCardType" && node.IsNot != true && node.Types is { Count: > 0 })
            return true;
        foreach (var child in node.GetConstraintList(GameDatabaseLoader.JsonOptions))
        {
            if (ConstraintMentionsCardType(child))
                return true;
        }
        return false;
    }

    private static HashSet<CardTier> ParseConstrainedTiers(RuleNode? tierConstraint)
    {
        var set = new HashSet<CardTier>();
        if (tierConstraint?.Tiers == null) return set;
        foreach (string s in tierConstraint.Tiers)
        {
            if (Enum.TryParse<CardTier>(s, true, out var ct))
                set.Add(ct);
        }
        return set;
    }

    /// <summary>
    /// 若 TSpawnFilterQuery 中显式声明了正向且仅限 Legendary 的 ConstraintTier（例如 Atlas Quest_3 / Book of Secrets），
    /// 因官方每日概率表仅含 Bronze..Diamond 四档，需将其识别为强制 Legendary 以免疫官服漏配 IgnoreTierTable 导致的零掉落。
    /// </summary>
    private static bool HasExplicitLegendaryOnlyConstraint(List<RuleNode> filters)
    {
        var positiveTiers = new HashSet<CardTier>();
        bool hasNonLegendary = false;
        void Walk(RuleNode? node, bool inverted)
        {
            if (node == null) return;
            bool nextInv = inverted ^ (node.IsNot == true);
            if (!nextInv && node.Type == "ConstraintTier" && node.Tiers is { Count: > 0 })
            {
                foreach (string ts in node.Tiers)
                {
                    if (Enum.TryParse<CardTier>(ts, true, out var ct))
                    {
                        positiveTiers.Add(ct);
                        if (ct != CardTier.Legendary) hasNonLegendary = true;
                    }
                }
                return;
            }
            if (node.Type is "ConstraintAnd" or "ConstraintOr")
            {
                foreach (var child in node.GetConstraintList(GameDatabaseLoader.JsonOptions))
                    Walk(child, nextInv);
            }
        }
        foreach (var f in filters)
        {
            if (f.Type == "TSpawnFilterQuery")
                Walk(f.GetSingleConstraint(GameDatabaseLoader.JsonOptions), false);
        }
        return !hasNonLegendary && positiveTiers.Contains(CardTier.Legendary);
    }

    private static bool IsItemAllowedByOwnedTier(
        CardTemplate tpl,
        CardTier candidateTier,
        Dictionary<string, CardTier>? ownedLookup,
        bool allowItemPlayerDup = false)
    {
        if (ownedLookup == null)
            return true;
        bool isSkill = string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase);
        bool isItem = string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase);
        // AllowPlayerDuplicates 仅豁免 Item；Skill 始终受 ownedLookup 约束（满阶 Diamond 禁重复，低阶不可降级刷新）
        if (!isSkill && (!isItem || allowItemPlayerDup))
            return true;
        if (!ownedLookup.TryGetValue(tpl.Id, out var ownedTier))
            return true;
        if (ownedTier >= CardTier.Diamond)
            return false;
        return ownedTier <= candidateTier;
    }

    private static List<CardTier> GetValidForcedTiersForCandidate(
        CardTemplate tpl,
        HashSet<CardTier> forcedTiers,
        Dictionary<string, CardTier>? ownedLookup,
        bool allowItemPlayerDup = false)
    {
        return forcedTiers
            .Where(t => t >= tpl.DefaultTier && SupportsTier(tpl, t) && IsItemAllowedByOwnedTier(tpl, t, ownedLookup, allowItemPlayerDup))
            .ToList();
    }

    private static List<CardTemplate> FilterCandidatesForOwnedMaxTiers(
        List<CardTemplate> candidates,
        int day,
        bool ignoreTierTable,
        CardInstance? inheritSource,
        bool downShift,
        bool forceNative,
        HashSet<CardTier> forcedTiers,
        GameModeData? gameMode,
        Dictionary<string, CardTier> ownedLookup,
        bool allowItemPlayerDup = false)
    {
        var result = new List<CardTemplate>(candidates.Count);
        foreach (var c in candidates)
        {
            bool isSkill = string.Equals(c.Type, "Skill", StringComparison.OrdinalIgnoreCase);
            bool isItem = string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase);
            if ((!isSkill && (!isItem || allowItemPlayerDup)) ||
                !ownedLookup.TryGetValue(c.Id, out var ownedTier))
            {
                result.Add(c);
                continue;
            }
            if (ownedTier >= CardTier.Diamond)
                continue;

            if (forcedTiers.Count > 0)
            {
                if (GetValidForcedTiersForCandidate(c, forcedTiers, ownedLookup, allowItemPlayerDup).Count > 0)
                    result.Add(c);
                continue;
            }

            if (inheritSource != null)
            {
                var t = inheritSource.Tier;
                if (downShift && t > CardTier.Bronze) t = (CardTier)((int)t - 1);
                var clamped = ClampToSupportedTier(c, t);
                if (ownedTier <= clamped)
                    result.Add(c);
                continue;
            }

            if (forceNative || ignoreTierTable)
            {
                var nativeClamped = ClampToSupportedTier(c, c.DefaultTier);
                if (ownedTier <= nativeClamped)
                    result.Add(c);
                continue;
            }

            var (bronze, silver, gold, diamond) = GetEffectiveDayTierWeights(gameMode, day);
            double[] weights = [bronze, silver, gold, diamond];
            bool anyDayTier = false;
            for (int i = 0; i < 4; i++)
            {
                if (weights[i] <= 0) continue;
                var candTier = (CardTier)i;
                var effective = ClampToSupportedTier(c, candTier < c.DefaultTier ? c.DefaultTier : candTier);
                if (effective >= ownedTier)
                {
                    anyDayTier = true;
                    break;
                }
            }
            if (anyDayTier)
                result.Add(c);
        }
        return result;
    }

    private static CardTier RollTierForDayWithOwnedConstraint(
        CardTemplate tpl,
        int day,
        bool ignoreTierTable,
        CardInstance? inheritSource,
        bool downShift,
        Random rng,
        GameModeData? gameMode,
        Dictionary<string, CardTier>? ownedLookup,
        bool allowItemPlayerDup = false)
    {
        bool checkOwned = ownedLookup != null && (
            string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase) ||
            (!allowItemPlayerDup && string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase)));
        if (!checkOwned ||
            !ownedLookup!.TryGetValue(tpl.Id, out var ownedTier) ||
            inheritSource != null ||
            ignoreTierTable)
        {
            return RollTierForDay(tpl, day, ignoreTierTable, inheritSource, downShift, rng, gameMode);
        }

        var (bronze, silver, gold, diamond) = GetEffectiveDayTierWeights(gameMode, day);
        double[] weights = [bronze, silver, gold, diamond];
        double validSum = 0;
        var validTiers = new List<(CardTier Tier, double Weight)>(4);
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] <= 0) continue;
            var candTier = (CardTier)i;
            var effective = ClampToSupportedTier(tpl, candTier < tpl.DefaultTier ? tpl.DefaultTier : candTier);
            if (effective >= ownedTier)
            {
                validSum += weights[i];
                validTiers.Add((effective, weights[i]));
            }
        }
        if (validTiers.Count == 0 || validSum <= 0)
            return RollTierForDay(tpl, day, ignoreTierTable, inheritSource, downShift, rng, gameMode);

        double roll = rng.NextDouble() * validSum;
        double acc = 0;
        foreach (var (t, w) in validTiers)
        {
            acc += w;
            if (roll <= acc) return t;
        }
        return validTiers[^1].Tier;
    }

    private static bool TryPickByDayTierFirst(
        List<CardTemplate> candidates,
        int day,
        bool downShift,
        Random rng,
        GameModeData? gameMode,
        HashSet<CardTier>? excludedTiers,
        Dictionary<string, CardTier>? ownedLookup,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules,
        string? playerHero,
        out CardTemplate? chosen,
        out CardTier chosenTier,
        bool isSequential = false,
        bool allowItemPlayerDup = false)
    {
        chosen = null;
        chosenTier = CardTier.Bronze;

        var (bronze, silver, gold, diamond) = GetEffectiveDayTierWeightsWithRules(gameMode, day, rules);

        double[] weights = [bronze, silver, gold, diamond];
        if (excludedTiers != null)
        {
            foreach (var ex in excludedTiers)
            {
                if ((int)ex is >= 0 and < 4)
                    weights[(int)ex] = 0;
            }
        }
        double totalWeight = weights[0] + weights[1] + weights[2] + weights[3];
        if (totalWeight <= 0)
            return false;

        double roll = rng.NextDouble() * totalWeight;
        CardTier targetTier = roll < weights[0] ? CardTier.Bronze
                            : roll < weights[0] + weights[1] ? CardTier.Silver
                            : roll < weights[0] + weights[1] + weights[2] ? CardTier.Gold
                            : CardTier.Diamond;

        var tryOrder = new List<CardTier>(4);
        if (weights[(int)targetTier] > 0)
            tryOrder.Add(targetTier);

        if (downShift)
        {
            for (int t = (int)targetTier - 1; t >= 0; t--)
            {
                var candTier = (CardTier)t;
                if (excludedTiers != null && excludedTiers.Contains(candTier)) continue;
                if (!tryOrder.Contains(candTier))
                    tryOrder.Add(candTier);
            }
        }

        foreach (var candTier in new[] { CardTier.Bronze, CardTier.Silver, CardTier.Gold, CardTier.Diamond }
                     .Where(t => !tryOrder.Contains(t) && weights[(int)t] > 0)
                     .OrderBy(t => Math.Abs((int)t - (int)targetTier))
                     .ThenBy(t => (int)t))
        {
            tryOrder.Add(candTier);
        }

        foreach (var tier in tryOrder)
        {
            var matching = candidates
                .Where(c => c.Type is not ("Item" or "Skill")
                         || (c.DefaultTier <= tier && SupportsTier(c, tier) && IsItemAllowedByOwnedTier(c, tier, ownedLookup, allowItemPlayerDup)))
                .ToList();
            if (matching.Count > 0)
            {
                chosen = isSequential
                    ? matching[0]
                    : PickCandidateWithArchetypeWeight(matching, rng, archetypeResolver, rules, playerHero);
                chosenTier = chosen.Type is "Item" or "Skill"
                    ? ClampToSupportedTier(chosen, tier)
                    : ClampToSupportedTier(chosen, chosen.DefaultTier);
                return true;
            }
        }

        // 若池中恰好全是非物品/技能卡（如 EncounterStep / EventEncounter）则正常选出
        var nonItemSkill = candidates.Where(c => c.Type is not ("Item" or "Skill")).ToList();
        if (nonItemSkill.Count > 0)
        {
            chosen = isSequential
                ? nonItemSkill[0]
                : PickCandidateWithArchetypeWeight(nonItemSkill, rng, archetypeResolver, rules, playerHero);
            chosenTier = ClampToSupportedTier(chosen, chosen.DefaultTier);
            return true;
        }

        return false;
    }

    /// <summary>返回指定天数的有效物品/技能品阶抽选权重（含 P2-30 Day 12 平滑修正）。</summary>
    public static (double Bronze, double Silver, double Gold, double Diamond) GetDayTierWeights(
        GameModeData? gameMode,
        int day)
        => GetEffectiveDayTierWeights(gameMode, day);

    /// <summary>
    /// 返回叠合 <see cref="CustomRunRuleSet.ShopHighTierBonusPct"/> 后的有效物品/技能品阶抽选权重：
    /// 严格按基础权重总和 <c>rawSum</c> 等比折算百分比（同时兼容 DB 的 0..1 小数表与兜底 0..100 百分表），
    /// 且在青铜权重已归零的中后期（如 Day 6+）将剩余高阶加成平滑从白银向金/钻转移。
    /// </summary>
    public static (double Bronze, double Silver, double Gold, double Diamond) GetEffectiveDayTierWeightsWithRules(
        GameModeData? gameMode,
        int day,
        CustomRunRuleSet? rules)
    {
        var (bronze, silver, gold, diamond) = GetEffectiveDayTierWeights(gameMode, day);
        if (rules == null || !double.IsFinite(rules.ShopHighTierBonusPct) || rules.ShopHighTierBonusPct <= 1e-6)
            return (bronze, silver, gold, diamond);

        double rawSum = bronze + silver + gold + diamond;
        if (rawSum <= 1e-9)
            return (bronze, silver, gold, diamond);

        double bonus = (Math.Clamp(rules.ShopHighTierBonusPct, 0.0, 100.0) / 100.0) * rawSum;
        if (rules.PresetId == EGameModePreset.ExtremeRoguelike)
        {
            double take = Math.Min(bronze + silver, bonus);
            double fromBronze = Math.Min(bronze, take);
            bronze -= fromBronze;
            silver -= (take - fromBronze);
            gold += take * 0.75;
            diamond += take * 0.25;
        }
        else
        {
            double fromBronze = Math.Min(bronze, bonus);
            bronze -= fromBronze;
            silver += fromBronze * 0.80;
            gold += fromBronze * 0.20;

            double remaining = bonus - fromBronze;
            if (remaining > 1e-9 && silver > 1e-9)
            {
                double fromSilver = Math.Min(silver, remaining);
                silver -= fromSilver;
                gold += fromSilver * 0.75;
                diamond += fromSilver * 0.25;
            }
        }

        return (bronze, silver, gold, diamond);
    }

    private static (double Bronze, double Silver, double Gold, double Diamond) GetEffectiveDayTierWeights(
        GameModeData? gameMode,
        int day)
    {
        if (!TryGetTierWeightsForDay(gameMode, day, out var bronze, out var silver, out var gold, out var diamond))
        {
            bronze = 100; silver = 0; gold = 0; diamond = 0;
            if (day >= 2) { bronze = 90; silver = 10; }
            if (day >= 3) { bronze = 70; silver = 30; }
            if (day >= 4) { bronze = 50; silver = 50; }
            if (day >= 5) { bronze = 25; silver = 75; }
            if (day >= 6) { bronze = 0; silver = 95; gold = 5; }
            if (day >= 7) { bronze = 0; silver = 80; gold = 20; }
            if (day >= 8) { bronze = 0; silver = 60; gold = 40; }
            if (day >= 9) { bronze = 0; silver = 35; gold = 60; diamond = 5; }
            if (day >= 10) { bronze = 0; silver = 20; gold = 70; diamond = 10; }
        }

        // TSpawnBehaviorDownShiftTier 不得全局压低当日品阶概率表，仅作用于候选池为空时的逐阶向下局部回退
        return (bronze, silver, gold, diamond);
    }

    private static bool SupportsTier(CardTemplate tpl, CardTier tier)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0) return true;
        return tpl.Tiers.ContainsKey(tier.ToString());
    }

    private static bool EvaluateTemplateSelectionRequirements(RuleNode req, RuleExecutionContext ctx)
    {
        if (req.Type == "TSelectionRequirement")
            return ValueAndConditionEvaluator.EvaluatePrerequisites(req.Prerequisites, ctx);
        if (req.Type.StartsWith("TPrerequisite", StringComparison.Ordinal))
            return ValueAndConditionEvaluator.EvaluatePrerequisite(req, ctx);
        return ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(req, ctx);
    }

    private static bool TryGetTierWeightsForDay(
        GameModeData? gameMode,
        int day,
        out double bronze,
        out double silver,
        out double gold,
        out double diamond)
    {
        bronze = silver = gold = diamond = 0;
        var table = gameMode?.ItemSkillSpawnTierPercantagesByDay;
        if (table == null || table.Count == 0) return false;

        // Patch 20.2 实测（BazaarDB /shop-odds Day 10–16 合计 38.7 万样本稳态 Silver 20% / Gold 70% / Diamond 10%）：
        // 官方按 GameMode.NumberOfDays（标准局为 10）钳制查表天数；若自定义 Mode 将 NumberOfDays 设为 >10，
        // 则自然放行读取对应高天自定义品阶行，同时继续兼容 P2-30 对旧 DB Day 12 总和 0.80 笔误的平滑防呆。
        int lookupDay = gameMode!.NumberOfDays > 0
            ? Math.Clamp(day, 1, gameMode.NumberOfDays)
            : Math.Max(1, day);

        Dictionary<string, double>? row = null;
        if (table.TryGetValue(lookupDay.ToString(), out var exact))
        {
            row = exact;
        }
        else
        {
            int bestLeDay = int.MinValue;
            int maxDay = int.MinValue;
            int minDay = int.MaxValue;
            Dictionary<string, double>? bestLeRow = null;
            Dictionary<string, double>? maxRow = null;
            Dictionary<string, double>? minRow = null;

            foreach (var (key, val) in table)
            {
                if (!int.TryParse(key, out int k)) continue;
                if (k > maxDay) { maxDay = k; maxRow = val; }
                if (k < minDay) { minDay = k; minRow = val; }
                if (k <= lookupDay && k > bestLeDay) { bestLeDay = k; bestLeRow = val; }
            }

            row = bestLeRow ?? (lookupDay > maxDay ? maxRow : minRow);
        }

        if (row == null) return false;
        bronze = Math.Max(0, row.GetValueOrDefault("Bronze", 0));
        silver = Math.Max(0, row.GetValueOrDefault("Silver", 0));
        gold = Math.Max(0, row.GetValueOrDefault("Gold", 0));
        diamond = Math.Max(0, row.GetValueOrDefault("Diamond", 0));
        // 防呆保留（P2-30）：若自定义 Mode 设 NumberOfDays>=12 并沿用旧 DB Day 12（0.20/0.55/0.05 总和 0.80），平滑补齐 Diamond=0.25
        double rawSum = bronze + silver + gold + diamond;
        if (lookupDay == 12
            && Math.Abs(rawSum - 0.80) < 1e-6
            && Math.Abs(diamond - 0.05) < 1e-6)
        {
            diamond = 0.25;
        }
        return (bronze + silver + gold + diamond) > 0;
    }

    /// <summary>
    /// 将过低目标品阶安全钳制到模板实际支持的最低档位（绝不在不存在的 Bronze 等档位实例化，如 Gold 起步的 1e8293e9）；
    /// 高于最高已定义档位的满阶 Legendary 予以保留，以维持官方 Legendary 标准定价与升级语义。
    /// </summary>
    public static CardTier ClampToSupportedTier(CardTemplate tpl, CardTier target)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0) return target;
        if (tpl.Tiers.ContainsKey(target.ToString())) return target;

        var supported = tpl.Tiers.Keys.Select(k => TierHelper.ParseTier(k)).ToList();
        var minTier = supported.Min();
        var maxTier = supported.Max();

        if (target < minTier)
            return tpl.Tiers.ContainsKey(tpl.DefaultTier.ToString()) ? tpl.DefaultTier : minTier;

        if (target > maxTier)
            return target;

        for (var cur = target; cur >= minTier; cur = (CardTier)((int)cur - 1))
        {
            if (tpl.Tiers.ContainsKey(cur.ToString()))
                return cur;
        }
        return minTier;
    }
}
