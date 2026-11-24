using System.Runtime.CompilerServices;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 统一求值器：对齐官方 BazaarGameShared.dll 实现全部 Value / Modifier / Conditional / Prerequisite 与 Spawn Constraint 求值。
/// </summary>
public static class ValueAndConditionEvaluator
{
    // 官方 ECardTag 枚举全量集合（30 个可见标签），用于在 TReferenceValueCardTagCount 中排除内部状态衍生标签（如 Tiny / 8 种效果隐藏标签）
    private static readonly HashSet<string> OfficialVisibleCardTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weapon", "Property", "Food", "Potion", "Tool", "Vehicle", "Aquatic", "Friend",
        "Core", "Unsellable", "Unstashable", "Merchant", "Event", "Combat", "Loot", "Ray",
        "Dinosaur", "Apparel", "Toy", "Tech", "Dragon", "Ingredient", "Relic", "Reagent",
        "Map", "Key", "Drone", "Sigil", "Trap", "Instrument"
    };

    // 为测试与扩展运行时提供轻量化遭遇访问记录与沙暴状态覆盖（不改动 ExecutionContext 公共签名）
    private static readonly ConditionalWeakTable<RuleExecutionContext, HashSet<string>> VisitedEncounterOverrides = new();
    private static readonly ConditionalWeakTable<RuleExecutionContext, StrongBox<bool>> SandstormOverrides = new();

    internal static void SetVisitedEncounters(RuleExecutionContext ctx, IEnumerable<string> encounterIds)
    {
        var set = new HashSet<string>(encounterIds, StringComparer.OrdinalIgnoreCase);
        VisitedEncounterOverrides.AddOrUpdate(ctx, set);
    }

    internal static void SetSandstormActive(RuleExecutionContext ctx, bool isActive)
    {
        SandstormOverrides.AddOrUpdate(ctx, new StrongBox<bool>(isActive));
    }

    public static double EvaluateValue(RuleNode? node, RuleExecutionContext ctx, double fallback = 0)
    {
        if (node == null) return fallback;

        if (string.Equals(node.Type, "TReferenceValueAttributeChange", StringComparison.Ordinal))
        {
            // 对齐官方 TReferenceValueAttributeChange：若无事件增量则对 DefaultValue 应用 Modifier，有增量则先应用 Modifier 再取绝对值
            if (HasDeltaEventFlag(ctx))
                return Math.Abs(ApplyModifier(node.Modifier, ctx, ctx.LastAttributeChangeDelta));
            return ApplyModifier(node.Modifier, ctx, node.DefaultValue ?? fallback);
        }

        double val = node.Type switch
        {
            "TFixedValue" => node.GetFixedNumericValue() is double fv ? (float)fv : fallback,
            "TRangeValue" => EvaluateRangeValue(node, ctx),
            "TReferenceValueCardAttribute" => EvaluateCardAttr(node, ctx, unscaled: false, aggregate: false),
            "TReferenceValueCardAttributeUnscaled" => EvaluateCardAttr(node, ctx, unscaled: true, aggregate: false),
            "TReferenceValueCardAttributeAggregate" => EvaluateCardAttr(node, ctx, unscaled: false, aggregate: true),
            "TReferenceValuePlayerAttribute" => EvaluatePlayerAttr(node, ctx, unscaled: false),
            "TReferenceValuePlayerAttributeUnscaled" => EvaluatePlayerAttr(node, ctx, unscaled: true),
            "TReferenceValueCardCount" => EvaluateCardCount(node, ctx),
            "TReferenceValueUniqueCardCount" => EvaluateUniqueCardCount(node, ctx),
            "TReferenceValueCardTagCount" => EvaluateTagCount(node, ctx),
            "TReferenceValueCardHeroCount" => EvaluateHeroCount(node, ctx),
            _ => node.GetFixedNumericValue() ?? node.DefaultValue ?? fallback
        };

        // TFixedValue 在官方语义中不挂载 Modifier；其余 TRangeValue / TReferenceValue* 统一应用 Modifier
        if (!string.Equals(node.Type, "TFixedValue", StringComparison.Ordinal))
            val = ApplyModifier(node.Modifier, ctx, val);

        return val;
    }

    // 说明：供 HasDeltaEvent 判定的无侵入辅助属性扩展，默认以 LastAttributeChangeDelta != 0 为准
    private static readonly ConditionalWeakTable<RuleExecutionContext, StrongBox<bool>> DeltaEventOverrides = new();

    internal static void SetHasDeltaEvent(RuleExecutionContext ctx, bool hasDeltaEvent)
    {
        DeltaEventOverrides.AddOrUpdate(ctx, new StrongBox<bool>(hasDeltaEvent));
    }

    private static bool HasDeltaEventFlag(RuleExecutionContext ctx)
        => (DeltaEventOverrides.TryGetValue(ctx, out var box) && box.Value)
           || Math.Abs(ctx.LastAttributeChangeDelta) > 0;

    private static double EvaluateValueWithExplicitTargets(RuleNode node, IReadOnlyList<CardInstance> targets, RuleExecutionContext ctx)
    {
        double val = targets.Count == 0
            ? (node.DefaultValue ?? 0)
            : node.Type switch
            {
                "TReferenceValueCardAttribute" => targets[0].HasAttribute(node.ResolveCardAttributeName("Ammo"))
                    ? GetCardAttr(targets[0], node.ResolveCardAttributeName("Ammo"), ctx, unscaled: false)
                    : (node.DefaultValue ?? 0),
                "TReferenceValueCardAttributeUnscaled" => targets[0].HasAttribute(node.ResolveCardAttributeName("Ammo"))
                    ? GetCardAttr(targets[0], node.ResolveCardAttributeName("Ammo"), ctx, unscaled: true)
                    : (node.DefaultValue ?? 0),
                "TReferenceValueCardAttributeAggregate" => targets.Sum(c => c.HasAttribute(node.ResolveCardAttributeName("Ammo")) ? GetCardAttr(c, node.ResolveCardAttributeName("Ammo"), ctx, unscaled: false) : 0),
                "TReferenceValueCardCount" => targets.Count,
                "TReferenceValueUniqueCardCount" => targets.Select(c => c.Template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "TReferenceValueCardTagCount" => CountTags(targets, node.Distinct ?? true),
                "TReferenceValueCardHeroCount" => CountHeroes(targets, node.IncludeCommon ?? true),
                _ => node.DefaultValue ?? 0
            };

        return ApplyModifier(node.Modifier, ctx, val);
    }

    private static double ApplyModifier(ValueModifierNode? modifier, RuleExecutionContext ctx, double originalValue)
    {
        if (modifier?.Value == null) return originalValue;
        // 对齐官方 TValueModifier.GetModifiedValue(float, ...) 单精度浮点运算与取整口径
        float orig = (float)originalValue;
        float modVal = (float)EvaluateValue(modifier.Value, ctx, 0);
        bool shouldRound = modifier.ShouldRound ?? true;
        return modifier.ModifyMode switch
        {
            "Subtract" => orig - modVal,
            "Multiply" => shouldRound ? OfficialRound(orig * modVal) : (orig * modVal),
            "Divide" => modVal == 0f
                ? orig
                : (shouldRound ? OfficialRound(orig / modVal) : (orig / modVal)),
            _ => orig + modVal
        };
    }

    private static double OfficialRound(float value)
    {
        if (float.IsNaN(value)) return 0;
        if (value > 0f && value < 1f) return 1;
        return SafeMath.ToSafeInt(value, -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, MidpointRounding.AwayFromZero);
    }

    private static double EvaluateRangeValue(RuleNode node, RuleExecutionContext ctx)
    {
        double min = node.MinValue ?? 0;
        double max = node.MaxValue ?? 1;
        double maxExclusive = max + 1.0;
        if (maxExclusive <= min) return min;
        double raw = Math.Floor(min + ctx.Rng.NextDouble() * (maxExclusive - min));
        double cap = Math.Floor(maxExclusive - 1.0);
        if (cap >= min && raw > cap) raw = cap;
        return raw;
    }

    private static double GetCardAttr(CardInstance card, string attr, RuleExecutionContext ctx, bool unscaled = false)
    {
        var prices = ctx.Repo?.GameMode.StandardPrices;
        double raw = unscaled ? card.GetAttributeUnscaled(attr, prices) : card.GetAttribute(attr, prices);
        return Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 递归检查数值表达式节点及其 Modifier 子树是否包含 Target == null 的卡牌引用节点（修复 P0-06：
    /// 覆盖 Eagle Sigil、Sunderer、Streaming Setup、Clean Storefront 与 [Shrouded Figure] Absorb Memories）。
    /// </summary>
    internal static bool ContainsNullTargetCardRef(RuleNode? node, int depth = 0)
    {
        if (node == null || depth > 8) return false;
        if (node.Target == null
            && node.Type is "TReferenceValueCardAttribute"
                or "TReferenceValueCardAttributeUnscaled"
                or "TReferenceValueCardAttributeAggregate"
                or "TReferenceValueCardTagCount"
                or "TReferenceValueCardHeroCount"
                or "TReferenceValueCardCount"
                or "TReferenceValueUniqueCardCount")
        {
            return true;
        }
        if (node.Modifier?.Value != null && ContainsNullTargetCardRef(node.Modifier.Value, depth + 1))
            return true;
        if (node.ReferenceValue != null && ContainsNullTargetCardRef(node.ReferenceValue, depth + 1))
            return true;
        var innerVal = node.GetValueNode(GameDatabaseLoader.JsonOptions);
        if (innerVal != null && ContainsNullTargetCardRef(innerVal, depth + 1))
            return true;
        return false;
    }

    private static List<CardInstance> ResolveValueTargets(RuleNode? target, RuleExecutionContext ctx)
    {
        if (target == null)
            return ctx.CurrentTargetCard != null ? [ctx.CurrentTargetCard] : [];
        return AbilityExecutor.ResolveActionTargets(target, ctx, int.MaxValue);
    }

    private static double EvaluateCardAttr(RuleNode node, RuleExecutionContext ctx, bool unscaled, bool aggregate)
    {
        var attr = node.ResolveCardAttributeName("Ammo");
        var cards = ResolveValueTargets(node.Target, ctx);
        if (cards.Count == 0) return node.DefaultValue ?? 0;

        if (aggregate)
        {
            double sum = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].HasAttribute(attr))
                    sum += GetCardAttr(cards[i], attr, ctx, unscaled);
            }
            return sum;
        }

        return cards[0].HasAttribute(attr)
            ? GetCardAttr(cards[0], attr, ctx, unscaled)
            : (node.DefaultValue ?? 0);
    }

    private static double EvaluatePlayerAttr(RuleNode node, RuleExecutionContext ctx, bool unscaled)
    {
        if (node.Target == null) return node.DefaultValue ?? 0;
        var attr = node.ResolvePlayerAttributeName("Burn");
        var players = TargetResolver.ResolvePlayers(node.Target, ctx);
        if (players.Count == 0) return node.DefaultValue ?? 0;
        double raw = unscaled ? players[0].GetAttributeUnscaled(attr) : players[0].GetAttribute(attr);
        return Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    private static double EvaluateCardCount(RuleNode node, RuleExecutionContext ctx)
    {
        var cards = ResolveValueTargets(node.Target, ctx);
        return cards.Count == 0 ? (node.DefaultValue ?? 0) : cards.Count;
    }

    private static double EvaluateUniqueCardCount(RuleNode node, RuleExecutionContext ctx)
    {
        var cards = ResolveValueTargets(node.Target, ctx);
        if (cards.Count == 0) return node.DefaultValue ?? 0;
        return cards.Select(c => c.Template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private static double EvaluateTagCount(RuleNode node, RuleExecutionContext ctx)
    {
        var cards = ResolveValueTargets(node.Target, ctx);
        if (cards.Count == 0) return node.DefaultValue ?? 0;
        return CountTags(cards, node.Distinct ?? true);
    }

    private static double EvaluateHeroCount(RuleNode node, RuleExecutionContext ctx)
    {
        var cards = ResolveValueTargets(node.Target, ctx);
        if (cards.Count == 0) return node.DefaultValue ?? 0;
        return CountHeroes(cards, node.IncludeCommon ?? true);
    }

    private static int CountTags(IReadOnlyList<CardInstance> cards, bool distinct)
    {
        if (distinct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cards)
            {
                foreach (var t in EnumerateOfficialCardTags(c))
                    set.Add(t);
            }
            return set.Count;
        }

        int total = 0;
        foreach (var c in cards)
        {
            foreach (var _ in EnumerateOfficialCardTags(c))
                total++;
        }
        return total;
    }

    private static IEnumerable<string> EnumerateOfficialCardTags(CardInstance c)
    {
        // D1-P1-02 (#124-5)：复用 CardInstance.GetAllTags() 逐层正/负标签叠算结果，再过滤为官方可见标签集合
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in c.GetAllTags())
        {
            if (OfficialVisibleCardTags.Contains(t))
                seen.Add(t);
        }
        return seen;
    }

    private static int CountHeroes(IReadOnlyList<CardInstance> cards, bool includeCommon)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cards)
        {
            foreach (var h in c.Template.Heroes)
            {
                if (!includeCommon && string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase))
                    continue;
                set.Add(h);
            }
        }
        return set.Count;
    }

    public static bool Compare(double left, string? op, double right) => op switch
    {
        "Equal" => Math.Abs(left - right) < 1e-6,
        "NotEqual" => Math.Abs(left - right) >= 1e-6,
        "GreaterThan" => left > right,
        "GreaterThanOrEqual" => left >= right,
        "LessThan" => left < right,
        "LessThanOrEqual" => left <= right,
        _ => Math.Abs(left - right) < 1e-6
    };

    public static bool EvaluatePrerequisites(List<RuleNode>? prereqs, RuleExecutionContext ctx)
    {
        if (prereqs == null || prereqs.Count == 0) return true;
        foreach (var p in prereqs)
        {
            if (!EvaluatePrerequisite(p, ctx)) return false;
        }
        return true;
    }

    private static readonly RuleNode DefaultSelfCardTarget = new() { Type = "TTargetCardSelf" };
    private static readonly RuleNode DefaultAbsPlayerTarget = new() { Type = "TTargetPlayerAbsolute", TargetMode = "Player" };

    public static bool EvaluatePrerequisite(RuleNode p, RuleExecutionContext ctx)
    {
        switch (p.Type)
        {
            case "TPrerequisiteCardCount":
            {
                int count = AbilityExecutor.ResolveActionTargets(p.Subject ?? DefaultSelfCardTarget, ctx, int.MaxValue).Count;
                return Compare(count, p.ResolveComparisonOperator(preferComparisonField: true), p.Amount ?? 0);
            }
            case "TPrerequisitePlayer":
            {
                var players = TargetResolver.ResolvePlayers(p.Subject ?? DefaultAbsPlayerTarget, ctx);
                return players.Count > 0;
            }
            case "TPrerequisiteRun":
            {
                var cond = p.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                return cond == null || EvaluateRunOrPlayerCondition(cond, ctx);
            }
            case "TPrerequisiteCardAttributeComparator":
            {
                var op = p.ResolveComparisonOperator("Equal", preferComparisonField: true);
                if (p.SubjectOther == null && string.IsNullOrEmpty(p.AttributeOther))
                {
                    return op is "Equal" or "GreaterThanOrEqual" or "LessThanOrEqual";
                }

                var leftCards = AbilityExecutor.ResolveActionTargets(p.Subject ?? DefaultSelfCardTarget, ctx, int.MaxValue);
                var rightCards = p.SubjectOther == null
                    ? leftCards
                    : AbilityExecutor.ResolveActionTargets(p.SubjectOther, ctx, int.MaxValue);

                var leftAttr = p.ResolveCardAttributeName("Ammo", preferAttributeField: true);
                var rightAttr = string.IsNullOrWhiteSpace(p.AttributeOther) ? leftAttr : p.AttributeOther;
                var leftVals = leftCards.Select(c => GetCardAttr(c, leftAttr, ctx)).ToList();
                var rightVals = rightCards.Select(c => GetCardAttr(c, rightAttr, ctx)).ToList();
                return leftVals.All(a => rightVals.All(o => Compare(a, op, o)));
            }
            default:
                return true;
        }
    }

    internal static bool ContainsExtremeCondition(RuleNode? cond)
    {
        if (cond == null) return false;
        if (cond.Type is "TCardConditionalAttributeHighest" or "TCardConditionalAttributeLowest"
            or "TCardConditionalSizeLargest" or "TCardConditionalSizeSmallest")
            return true;
        if (cond.Type is "TCardConditionalAnd" or "TCardConditionalOr")
        {
            foreach (var sub in cond.GetConditionList(GameDatabaseLoader.JsonOptions))
            {
                if (ContainsExtremeCondition(sub)) return true;
            }
        }
        return false;
    }

    internal static bool EvaluateCardConditionNonExtreme(RuleNode? cond, CardInstance card, PlayerState owner, RuleExecutionContext ctx)
    {
        if (cond == null) return true;
        if (cond.Type is "TCardConditionalAttributeHighest" or "TCardConditionalAttributeLowest"
            or "TCardConditionalSizeLargest" or "TCardConditionalSizeSmallest")
            return true;

        if (cond.Type == "TCardConditionalAnd")
        {
            return cond.GetConditionList(GameDatabaseLoader.JsonOptions)
                .All(c => EvaluateCardConditionNonExtreme(c, card, owner, ctx));
        }
        if (cond.Type == "TCardConditionalOr")
        {
            var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
            if (subs.Any(ContainsExtremeCondition))
                return true;
            return subs.Any(c => EvaluateCardConditionNonExtreme(c, card, owner, ctx));
        }
        return EvaluateCardCondition(cond, card, owner, ctx);
    }

    /// <summary>
    /// 完全复刻官方 ITCardConditional.Filter 与 SortedCardConditionalSet 分层集合过滤语义：
    /// TCardConditionalAnd 先执行 Normal（含 SizeLargest/Smallest）再 Composite 再 Aggregate（AttributeHighest/Lowest）；
    /// TCardConditionalOr 在同层原始输入集上求并集；其余叶子节点按条件过滤，其中极值条件在当前过滤池内求胜者。
    /// </summary>
    internal static List<(CardInstance Card, PlayerState Owner)> FilterCardCandidatePairs(
        RuleNode cond,
        List<(CardInstance Card, PlayerState Owner)> pairs,
        RuleExecutionContext ctx)
    {
        if (pairs.Count == 0) return [];

        switch (cond.Type)
        {
            case "TCardConditionalAnd":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                var normal = new List<RuleNode>();
                var composite = new List<RuleNode>();
                var aggregate = new List<RuleNode>();
                foreach (var sub in subs)
                {
                    if (sub.Type is "TCardConditionalAnd" or "TCardConditionalOr")
                        composite.Add(sub);
                    else if (sub.Type is "TCardConditionalAttributeHighest" or "TCardConditionalAttributeLowest")
                        aggregate.Add(sub);
                    else
                        normal.Add(sub);
                }

                var current = new List<(CardInstance Card, PlayerState Owner)>(pairs);
                foreach (var n in normal) current = FilterCardCandidatePairs(n, current, ctx);
                foreach (var c in composite) current = FilterCardCandidatePairs(c, current, ctx);
                foreach (var a in aggregate) current = FilterCardCandidatePairs(a, current, ctx);
                return current;
            }

            case "TCardConditionalOr":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                var normal = new List<RuleNode>();
                var composite = new List<RuleNode>();
                var aggregate = new List<RuleNode>();
                foreach (var sub in subs)
                {
                    if (sub.Type is "TCardConditionalAnd" or "TCardConditionalOr")
                        composite.Add(sub);
                    else if (sub.Type is "TCardConditionalAttributeHighest" or "TCardConditionalAttributeLowest")
                        aggregate.Add(sub);
                    else
                        normal.Add(sub);
                }

                var matched = new HashSet<CardInstance>(ReferenceEqualityComparer.Instance);
                void UnionSubList(List<RuleNode> list)
                {
                    foreach (var sub in list)
                    {
                        foreach (var p in FilterCardCandidatePairs(sub, pairs, ctx))
                            matched.Add(p.Card);
                    }
                }
                UnionSubList(normal);
                UnionSubList(composite);
                UnionSubList(aggregate);

                var res = new List<(CardInstance, PlayerState)>(matched.Count);
                foreach (var p in pairs)
                {
                    if (matched.Contains(p.Card)) res.Add(p);
                }
                return res;
            }

            case "TCardConditionalAttributeHighest":
            {
                // 严格对齐官方反编译 TCardConditionalAttributeHighest.cs:20-29（int num = 0; val > num；全员 <= 0 返回空集）
                string attr = cond.ResolveCardAttributeName("Ammo");
                int bestIdx = -1;
                double bestVal = 0;
                for (int i = 0; i < pairs.Count; i++)
                {
                    double v = GetCardAttr(pairs[i].Card, attr, ctx);
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestIdx = i;
                    }
                }
                return bestIdx < 0 ? [] : [pairs[bestIdx]];
            }

            case "TCardConditionalAttributeLowest":
            {
                string attr = cond.ResolveCardAttributeName("Ammo");
                int bestIdx = -1;
                double bestVal = 0;
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (!pairs[i].Card.HasAttribute(attr)) continue;
                    double v = GetCardAttr(pairs[i].Card, attr, ctx);
                    if (bestIdx < 0 || v < bestVal)
                    {
                        bestVal = v;
                        bestIdx = i;
                    }
                }
                return bestIdx < 0 ? [] : [pairs[bestIdx]];
            }

            case "TCardConditionalSizeLargest":
            {
                int maxSize = (int)CardSize.Small;
                for (int i = 0; i < pairs.Count; i++)
                {
                    int sz = (int)pairs[i].Card.EffectiveSize;
                    if (sz > maxSize) maxSize = sz;
                }
                var res = new List<(CardInstance, PlayerState)>(pairs.Count);
                for (int i = 0; i < pairs.Count; i++)
                {
                    if ((int)pairs[i].Card.EffectiveSize == maxSize) res.Add(pairs[i]);
                }
                return res;
            }

            case "TCardConditionalSizeSmallest":
            {
                int minSize = (int)CardSize.Large;
                for (int i = 0; i < pairs.Count; i++)
                {
                    int sz = (int)pairs[i].Card.EffectiveSize;
                    if (sz < minSize) minSize = sz;
                }
                var res = new List<(CardInstance, PlayerState)>(pairs.Count);
                for (int i = 0; i < pairs.Count; i++)
                {
                    if ((int)pairs[i].Card.EffectiveSize == minSize) res.Add(pairs[i]);
                }
                return res;
            }

            default:
            {
                var res = new List<(CardInstance, PlayerState)>(pairs.Count);
                foreach (var (c, owner) in pairs)
                {
                    if (EvaluateCardCondition(cond, c, owner, ctx))
                        res.Add((c, owner));
                }
                return res;
            }
        }
    }

    public static bool EvaluateCardCondition(RuleNode? cond, CardInstance card, PlayerState owner, RuleExecutionContext ctx)
    {
        if (cond == null) return true;
        return cond.Type switch
        {
            "TCardConditionalAnd" => EvaluateCardConditionList(cond.GetConditionList(GameDatabaseLoader.JsonOptions), card, owner, ctx, requireAll: true),
            "TCardConditionalOr" => EvaluateCardConditionList(cond.GetConditionList(GameDatabaseLoader.JsonOptions), card, owner, ctx, requireAll: false),
            "TCardConditionalTag" => MatchListOperator(cond.Tags, cond.Operator, t => OfficialVisibleCardTags.Contains(t) && card.HasTag(t)),
            "TCardConditionalHiddenTag" => MatchListOperator(cond.Tags ?? cond.HiddenTags, cond.Operator, t => card.HasHiddenTag(t)),
            "TCardConditionalId" => EvaluateCardIdCondition(cond, card),
            "TCardConditionalAttribute" => EvaluateCardAttributeCondition(cond, card, ctx),
            "TCardConditionalTier" => ApplyNegation(
                cond.Tiers != null && cond.Tiers.Contains(card.Tier.ToString(), StringComparer.OrdinalIgnoreCase),
                cond.IsNot),
            "TCardConditionalSize" => ApplyNegation(
                cond.Sizes != null && cond.Sizes.Contains(card.EffectiveSize.ToString(), StringComparer.OrdinalIgnoreCase),
                cond.IsNot),
            "TCardConditionalSizeComparison" => EvaluateSizeComparison(cond, card, ctx),
            "TCardConditionalType" => ApplyNegation(
                string.Equals(card.Template.Type, cond.CardType, StringComparison.OrdinalIgnoreCase),
                cond.IsNot),
            "TCardConditionalCanCrit" => ApplyNegation(EvaluateCanCrit(card), cond.IsNot),
            // 对齐原版 TCardConditionalHasEnchantment.cs：Skill/SocketEffect 非 IEnchantable 时 Enchantment 视为 null
            "TCardConditionalHasEnchantment" => ApplyNegation(
                EvaluateHasEnchantment(cond, card),
                cond.IsNot),
            "TCardConditionalEnchantmentEligible" => !string.IsNullOrEmpty(cond.Enchantment)
                && card.Template.Enchantments != null
                && card.Template.Enchantments.ContainsKey(cond.Enchantment),
            "TCardConditionalHero" => MatchListOperator(
                cond.Heroes,
                cond.Operator,
                h => card.Template.Heroes.Contains(h, StringComparer.OrdinalIgnoreCase)),
            "TCardConditionalPlayerHero" => EvaluateCardPlayerHero(cond, card, ctx),
            "TCardConditionalTriggerSource" => ApplyNegation(ReferenceEquals(card, ctx.TriggerSourceCard), cond.IsNot),
            "TCardConditionalTriggerEffectOwner" => EvaluateTriggerEffectOwner(cond, ctx),
            "TCardConditionalState" => MatchListOperator(cond.States, cond.Operator, st => MatchCardState(card, st)),
            "TCardConditionalAttributeHighest" => IsExtremeAttr(card, owner, cond.ResolveCardAttributeName("Ammo"), highest: true, ctx),
            "TCardConditionalAttributeLowest" => IsExtremeAttr(card, owner, cond.ResolveCardAttributeName("Ammo"), highest: false, ctx),
            "TCardConditionalSizeLargest" => IsExtremeSize(card, owner, largest: true, ctx),
            "TCardConditionalSizeSmallest" => IsExtremeSize(card, owner, largest: false, ctx),
            _ => true
        };
    }

    private static bool EvaluateCardAttributeCondition(RuleNode cond, CardInstance card, RuleExecutionContext ctx)
    {
        var attr = cond.ResolveCardAttributeName("Ammo", preferAttributeField: true);
        var op = cond.ResolveComparisonOperator();
        if (!card.HasAttribute(attr))
        {
            // 被动无冷却卡（未声明 CooldownMax 或 CooldownMax<=0）在显式判等 CooldownMax == 0 时按 0 匹配，
            // 同时 <= N 与 TCardConditionalAttributeLowest(CooldownMax) 仍严格排除无冷却被动卡
            if (attr.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase)
                && string.Equals(op, "Equal", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Abs(EvaluateCardConditionalComparisonValue(cond, card, ctx, 0)) < 1e-9;
            }
            return false;
        }
        return Compare(
            GetCardAttr(card, attr, ctx),
            op,
            EvaluateCardConditionalComparisonValue(cond, card, ctx, 0));
    }

    private static bool EvaluateCardConditionList(List<RuleNode> list, CardInstance card, PlayerState owner, RuleExecutionContext ctx, bool requireAll)
    {
        if (requireAll)
        {
            for (int i = 0; i < list.Count; i++)
                if (!EvaluateCardCondition(list[i], card, owner, ctx)) return false;
            return true;
        }
        for (int i = 0; i < list.Count; i++)
            if (EvaluateCardCondition(list[i], card, owner, ctx)) return true;
        return false;
    }

    private static bool ApplyNegation(bool raw, bool? isNot) => isNot == true ? !raw : raw;

    private static bool EvaluateCardIdCondition(RuleNode cond, CardInstance card)
    {
        bool raw;
        if (cond.Ids != null && cond.Ids.Count > 0)
        {
            raw = MatchListOperator(cond.Ids, cond.Operator, id => IdMatches(card.Template.Id, id));
        }
        else
        {
            raw = IdMatches(card.Template.Id, cond.Id);
        }
        return ApplyNegation(raw, cond.IsNot);
    }

    private static bool IdMatches(string? templateId, string? targetId)
    {
        if (string.IsNullOrEmpty(templateId) || string.IsNullOrEmpty(targetId))
            return false;
        if (Guid.TryParse(templateId, out var g1) && Guid.TryParse(targetId, out var g2))
            return g1 == g2;
        return string.Equals(templateId, targetId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateHasEnchantment(RuleNode cond, CardInstance card)
    {
        string? cardEnch = card.Enchantment;
        return string.IsNullOrEmpty(cond.Enchantment)
            ? string.IsNullOrEmpty(cardEnch)
            : string.Equals(cardEnch, cond.Enchantment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateCanCrit(CardInstance card)
    {
        if (card.HasHiddenTag("CanCrit")) return true;
        if (!string.Equals(card.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var ab in card.ActiveAbilities)
        {
            if (ab.Trigger?.Type == "TTriggerOnCardFired" && ActionContainsCritCapableEffect(ab.Action))
            {
                return true;
            }
        }

        // 向后兼容极简合成测试卡（MakeSimpleWeapon：无任何 Ability/Aura，但模板 BaseAttributes 自带 CooldownMax 与攻防基础值；真实 GameData.db 中此类卡数为 0）
        if (card.ActiveAbilities.Count == 0
            && (card.Template.Auras == null || card.Template.Auras.Count == 0)
            && card.BaseAttributes.GetValueOrDefault("CooldownMax", 0) > 0)
        {
            return card.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0
                || card.BaseAttributes.GetValueOrDefault("ShieldApplyAmount", 0) > 0
                || card.BaseAttributes.GetValueOrDefault("HealAmount", 0) > 0
                || card.BaseAttributes.GetValueOrDefault("BurnApplyAmount", 0) > 0
                || card.BaseAttributes.GetValueOrDefault("PoisonApplyAmount", 0) > 0;
        }

        return false;
    }

    /// <summary>递归检测动作节点（含 TActionAnd 等复合动作容器）是否包含可暴击的攻防/状态数值动作。</summary>
    private static bool ActionContainsCritCapableEffect(RuleNode? action, int depth = 0)
    {
        if (action == null || depth > 8) return false;
        if (action.Type is "TActionPlayerDamage" or "TActionPlayerShieldApply" or "TActionPlayerHeal"
            or "TActionPlayerReviveHeal" or "TActionPlayerBurnApply" or "TActionPlayerPoisonApply"
            or "TActionPlayerRegenApply")
        {
            return true;
        }
        if (action.Actions is { Count: > 0 })
        {
            for (int i = 0; i < action.Actions.Count; i++)
            {
                if (ActionContainsCritCapableEffect(action.Actions[i], depth + 1))
                    return true;
            }
        }
        return false;
    }

    private static bool EvaluateCardPlayerHero(RuleNode cond, CardInstance card, RuleExecutionContext ctx)
    {
        if (ctx.SelfCard == null) return false;
        var targetingOwner = TargetResolver.FindOwner(ctx.SelfCard, ctx);
        bool sameAsHero = cond.IsSameAsPlayerHero ?? false;
        bool containsHero = card.Template.Heroes.Contains(targetingOwner.Hero, StringComparer.OrdinalIgnoreCase);
        return sameAsHero ? containsHero : !containsHero;
    }

    private static bool EvaluateTriggerEffectOwner(RuleNode cond, RuleExecutionContext ctx)
    {
        PlayerState? srcOwner = ctx.TriggerSourceCard != null ? TargetResolver.FindOwner(ctx.TriggerSourceCard, ctx) : null;
        PlayerState? selfOwner = ctx.SelfCard != null ? TargetResolver.FindOwner(ctx.SelfCard, ctx) : null;
        string mode = cond.OwnerComparison ?? cond.TriggerEffectOwner ?? "SameOwner";
        return mode switch
        {
            "DifferentOwner" => !ReferenceEquals(srcOwner, selfOwner),
            _ => ReferenceEquals(srcOwner, selfOwner)
        };
    }

    private static bool EvaluateSizeComparison(RuleNode cond, CardInstance card, RuleExecutionContext ctx)
    {
        if (cond.Target == null) return true;
        // 对齐官方 TCardConditionalSizeComparison.cs：构造 new TargetingContext(Run, TriggerSource)，EventContext 为 null
        var targetingCtx = ctx.WithCards(ctx.TriggerSourceCard);
        targetingCtx.TriggerSourceCard = null;
        targetingCtx.TriggerTargetCard = null;
        var compareTargets = TargetResolver.ResolveCards(cond.Target, targetingCtx, int.MaxValue);
        string op = cond.ResolveComparisonOperator("LessThan", preferComparisonField: true)!;
        int mySize = (int)card.EffectiveSize;
        foreach (var t in compareTargets)
        {
            if (!Compare(mySize, op, (int)t.EffectiveSize))
                return false;
        }
        return true;
    }

    private static double EvaluateCardConditionalComparisonValue(RuleNode cond, CardInstance candidateCard, RuleExecutionContext ctx, double fallback)
    {
        var node = cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
        if (node != null)
        {
            if (node.Target == null && node.Type is "TReferenceValueCardAttribute" or "TReferenceValueCardAttributeUnscaled"
                or "TReferenceValueCardAttributeAggregate" or "TReferenceValueCardCount"
                or "TReferenceValueUniqueCardCount" or "TReferenceValueCardTagCount" or "TReferenceValueCardHeroCount")
            {
                return EvaluateValueWithExplicitTargets(node, [candidateCard], ctx);
            }
            return EvaluateValue(node, ctx, fallback);
        }
        return cond.GetFixedComparisonValue() ?? fallback;
    }

    internal static List<PlayerState> FilterPlayerCandidates(
        RuleNode cond,
        List<PlayerState> players,
        RuleExecutionContext ctx)
    {
        if (players.Count == 0) return [];

        switch (cond.Type)
        {
            case "TPlayerConditionalAnd":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                var normal = new List<RuleNode>();
                var composite = new List<RuleNode>();
                var aggregate = new List<RuleNode>();
                foreach (var sub in subs)
                {
                    if (sub.Type is "TPlayerConditionalAnd" or "TPlayerConditionalOr")
                        composite.Add(sub);
                    else if (sub.Type is "TPlayerConditionalAttributeHighest" or "TPlayerConditionalAttributeLowest")
                        aggregate.Add(sub);
                    else
                        normal.Add(sub);
                }

                var current = new List<PlayerState>(players);
                foreach (var n in normal) current = FilterPlayerCandidates(n, current, ctx);
                foreach (var c in composite) current = FilterPlayerCandidates(c, current, ctx);
                foreach (var a in aggregate) current = FilterPlayerCandidates(a, current, ctx);
                return current;
            }

            case "TPlayerConditionalOr":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                var normal = new List<RuleNode>();
                var composite = new List<RuleNode>();
                var aggregate = new List<RuleNode>();
                foreach (var sub in subs)
                {
                    if (sub.Type is "TPlayerConditionalAnd" or "TPlayerConditionalOr")
                        composite.Add(sub);
                    else if (sub.Type is "TPlayerConditionalAttributeHighest" or "TPlayerConditionalAttributeLowest")
                        aggregate.Add(sub);
                    else
                        normal.Add(sub);
                }

                var matched = new HashSet<PlayerState>(ReferenceEqualityComparer.Instance);
                void UnionSubList(List<RuleNode> list)
                {
                    foreach (var sub in list)
                    {
                        foreach (var p in FilterPlayerCandidates(sub, players, ctx))
                            matched.Add(p);
                    }
                }
                UnionSubList(normal);
                UnionSubList(composite);
                UnionSubList(aggregate);

                var res = new List<PlayerState>(matched.Count);
                foreach (var p in players)
                {
                    if (matched.Contains(p)) res.Add(p);
                }
                return res;
            }

            case "TPlayerConditionalAttributeHighest":
            {
                // 严格对齐官方反编译 TPlayerConditionalAttributeHighest.cs:51-60（int num = 0; val > num）
                string attr = cond.ResolvePlayerAttributeName("HealthMax");
                PlayerState? best = null;
                double bestVal = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    double v = Math.Round(p.GetAttribute(attr), MidpointRounding.AwayFromZero);
                    if (v > bestVal)
                    {
                        bestVal = v;
                        best = p;
                    }
                }
                return best == null ? [] : [best];
            }

            case "TPlayerConditionalAttributeLowest":
            {
                string attr = cond.ResolvePlayerAttributeName("HealthMax");
                PlayerState? best = null;
                double bestVal = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    double v = Math.Round(p.GetAttribute(attr), MidpointRounding.AwayFromZero);
                    if (best == null || v < bestVal)
                    {
                        bestVal = v;
                        best = p;
                    }
                }
                return best == null ? [] : [best];
            }

            default:
            {
                var res = new List<PlayerState>(players.Count);
                for (int i = 0; i < players.Count; i++)
                {
                    if (EvaluatePlayerCondition(cond, players[i], ctx))
                        res.Add(players[i]);
                }
                return res;
            }
        }
    }

    public static bool EvaluatePlayerCondition(RuleNode? cond, PlayerState player, RuleExecutionContext ctx, IReadOnlyList<PlayerState>? poolOverride = null)
    {
        if (cond == null) return true;
        return cond.Type switch
        {
            "TPlayerConditionalAnd" => cond.GetConditionList(GameDatabaseLoader.JsonOptions).All(c => EvaluatePlayerCondition(c, player, ctx, poolOverride)),
            "TPlayerConditionalOr" => cond.GetConditionList(GameDatabaseLoader.JsonOptions).Any(c => EvaluatePlayerCondition(c, player, ctx, poolOverride)),
            "TPlayerConditionalAttribute" => Compare(
                Math.Round(player.GetAttribute(cond.ResolvePlayerAttributeName("Burn", preferAttributeField: true)), MidpointRounding.AwayFromZero),
                cond.ResolveComparisonOperator(),
                EvaluateComparisonValue(cond, ctx, 0)),
            "TPlayerConditionalAttributeHighest" => IsExtremePlayerAttr(player, cond.ResolvePlayerAttributeName("HealthMax"), highest: true, ctx, poolOverride),
            "TPlayerConditionalAttributeLowest" => IsExtremePlayerAttr(player, cond.ResolvePlayerAttributeName("HealthMax"), highest: false, ctx, poolOverride),
            _ => EvaluateRunOrPlayerCondition(cond, ctx)
        };
    }

    private static bool IsExtremePlayerAttr(PlayerState player, string attr, bool highest, RuleExecutionContext ctx, IReadOnlyList<PlayerState>? poolOverride)
    {
        var pool = poolOverride ?? BuildDefaultPlayerPool(ctx);
        if (pool.Count == 0) return false;
        if (highest)
        {
            // 严格对齐官方反编译 TPlayerConditionalAttributeHighest.cs:30-38（int num = 0; val > num）
            PlayerState? best = null;
            double bestVal = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var p = pool[i];
                double v = Math.Round(p.GetAttribute(attr), MidpointRounding.AwayFromZero);
                if (v > bestVal)
                {
                    bestVal = v;
                    best = p;
                }
            }
            return ReferenceEquals(player, best);
        }
        else
        {
            PlayerState? best = null;
            double bestVal = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var p = pool[i];
                double v = Math.Round(p.GetAttribute(attr), MidpointRounding.AwayFromZero);
                if (best == null || v < bestVal)
                {
                    bestVal = v;
                    best = p;
                }
            }
            return ReferenceEquals(player, best);
        }
    }

    private static List<PlayerState> BuildDefaultPlayerPool(RuleExecutionContext ctx)
    {
        var local = TargetResolver.GetLocalPlayer(ctx);
        var opp = ReferenceEquals(local, ctx.SelfPlayer) ? ctx.OpponentPlayer : ctx.SelfPlayer;
        return opp != null ? [local, opp] : [local];
    }

    public static bool EvaluateRunOrPlayerCondition(RuleNode? cond, RuleExecutionContext ctx)
    {
        if (cond == null) return true;
        return cond.Type switch
        {
            "TRunConditionalAnd" => cond.GetConditionList(GameDatabaseLoader.JsonOptions).All(c => EvaluateRunOrPlayerCondition(c, ctx)),
            "TRunConditionalOr" => cond.GetConditionList(GameDatabaseLoader.JsonOptions).Any(c => EvaluateRunOrPlayerCondition(c, ctx)),
            "TRunConditionalCurrentDay" => Compare(ctx.CurrentDay, cond.ResolveComparisonOperator(), cond.CurrentDay ?? 0),
            "TRunConditionalCurrentHour" => Compare(ctx.CurrentHour, cond.ResolveComparisonOperator(), cond.CurrentHour ?? 0),
            "TRunConditionalPlayerHero" => MatchListOperator(
                cond.Heroes,
                cond.Operator,
                h => string.Equals(TargetResolver.GetLocalPlayer(ctx).Hero, h, StringComparison.OrdinalIgnoreCase)),
            "TRunConditionalPlayMode" => MatchListOperator(
                cond.PlayModes,
                cond.Operator,
                m => string.Equals(ctx.PlayMode, m, StringComparison.OrdinalIgnoreCase)),
            "TRunConditionalCurrentEncounter" => EvaluateCurrentEncounter(cond, ctx),
            "TRunConditionalMusicNoteHasValidSocket" => EvaluateMusicNoteHasValidSocket(cond, ctx),
            "TRunConditionalGameMode" => EvaluateRunGameMode(cond, ctx),
            "TRunConditionalHasVisitedEncounter" => EvaluateHasVisitedEncounter(cond, ctx),
            "TRunConditionalSandstorm" => EvaluateSandstormCondition(cond, ctx),
            "TPlayerConditionalAnd" or "TPlayerConditionalOr" or "TPlayerConditionalAttribute"
                or "TPlayerConditionalAttributeHighest" or "TPlayerConditionalAttributeLowest"
                => EvaluatePlayerCondition(cond, ctx.SelfPlayer, ctx),
            _ => true
        };
    }

    private static bool EvaluateMusicNoteHasValidSocket(RuleNode cond, RuleExecutionContext ctx)
    {
        int noteIdx = SocketPlacementHelper.TryParseMusicNote(cond.MusicNote, out int parsed) ? parsed : 0;
        bool valid = SocketPlacementHelper.CanPlaceMusicNote(TargetResolver.GetLocalPlayer(ctx), noteIdx);
        return ApplyNegation(valid, cond.IsNot);
    }

    private static bool EvaluateRunGameMode(RuleNode cond, RuleExecutionContext ctx)
    {
        string? gmId = ctx.Repo?.GameMode.Id;
        var list = cond.GameModeIds ?? (string.IsNullOrEmpty(cond.GameMode) ? null : new List<string> { cond.GameMode });
        return MatchListOperator(list, cond.Operator, id => IdMatches(gmId, id));
    }

    private static bool EvaluateHasVisitedEncounter(RuleNode cond, RuleExecutionContext ctx)
    {
        // D1-P1-06：优先读取 RuleExecutionContext.VisitedEncounterIds，再回退旧 ConditionalWeakTable
        IReadOnlyCollection<string>? visited = ctx.VisitedEncounterIds;
        if (visited == null && VisitedEncounterOverrides.TryGetValue(ctx, out var overrideSet))
            visited = overrideSet;
        var list = cond.EncounterIds ?? (string.IsNullOrEmpty(cond.EncounterId) ? null : new List<string> { cond.EncounterId });
        return MatchListOperator(list, cond.Operator, id =>
        {
            if (visited == null || visited.Count == 0) return false;
            if (visited is HashSet<string> hs && hs.Contains(id)) return true;
            foreach (var v in visited)
            {
                if (string.Equals(v, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (Guid.TryParse(id, out var g))
                return visited.Any(v => Guid.TryParse(v, out var vg) && vg == g);
            return false;
        });
    }

    private static bool EvaluateSandstormCondition(RuleNode cond, RuleExecutionContext ctx)
    {
        bool active;
        if (SandstormOverrides.TryGetValue(ctx, out var box))
        {
            active = box.Value;
        }
        else
        {
            int startMs = (ctx.Repo?.GameMode.Sandstorm.CountdownStartTime ?? 20000)
                        + (ctx.Repo?.GameMode.Sandstorm.CountdownDuration ?? 10000);
            active = ctx.Scope == RuleScope.InCombat && ctx.TimeMs >= startMs;
        }
        return active == (cond.IsActive ?? false);
    }

    private static double EvaluateComparisonValue(RuleNode cond, RuleExecutionContext ctx, double fallback = 0)
    {
        var node = cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
        if (node != null) return EvaluateValue(node, ctx, fallback);
        return cond.GetFixedComparisonValue() ?? fallback;
    }

    private static bool MatchCardState(CardInstance card, string state) => state switch
    {
        "Alive" => !card.IsDestroyed && !card.IsDisabled,
        "Destroyed" => card.IsDestroyed,
        "Disabled" => !card.IsDestroyed && card.IsDisabled,
        "Transformed" => card.WasTransformed || !string.IsNullOrEmpty(card.TransformedFromInstanceId),
        "Casting" => card.IsCasting,
        _ => false
    };

    private static bool EvaluateCurrentEncounter(RuleNode cond, RuleExecutionContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.CurrentEncounterId) || ctx.Repo == null) return false;
        var enc = ctx.Repo.GetCard(ctx.CurrentEncounterId);
        if (enc == null) return false;
        var inner = cond.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (inner == null) return false;
        var tempInst = new CardInstance(enc);
        return EvaluateCardCondition(inner, tempInst, TargetResolver.GetLocalPlayer(ctx), ctx);
    }

    public static bool EvaluateTemplateConstraint(RuleNode? constraint, CardTemplate template, string playerHero)
    {
        if (constraint == null) return true;
        bool negate = constraint.IsNot == true;
        bool raw = constraint.Type switch
        {
            "ConstraintAnd" => constraint.GetConstraintList(GameDatabaseLoader.JsonOptions).All(c => EvaluateTemplateConstraint(c, template, playerHero)),
            "ConstraintOr" => constraint.GetConstraintList(GameDatabaseLoader.JsonOptions).Any(c => EvaluateTemplateConstraint(c, template, playerHero)),
            "ConstraintCardType" => constraint.Types == null || constraint.Types.Contains(template.Type, StringComparer.OrdinalIgnoreCase),
            "ConstraintTag" => constraint.Tags != null && constraint.Tags.Any(t => template.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)),
            "ConstraintHiddenTag" => constraint.HiddenTags != null && constraint.HiddenTags.Any(t => template.HiddenTags.Contains(t, StringComparer.OrdinalIgnoreCase)),
            "ConstraintSize" => constraint.Sizes != null && constraint.Sizes.Contains(template.Size, StringComparer.OrdinalIgnoreCase),
            "ConstraintTier" => constraint.Tiers != null && constraint.Tiers.Contains(template.StartingTier, StringComparer.OrdinalIgnoreCase),
            "ConstraintHero" => constraint.Heroes != null && constraint.Heroes.Any(h => template.Heroes.Contains(h, StringComparer.OrdinalIgnoreCase)),
            "ConstraintIsOnlyHero" => constraint.Heroes != null && template.Heroes.Count == 1 && constraint.Heroes.Contains(template.Heroes[0], StringComparer.OrdinalIgnoreCase),
            "ConstraintId" => constraint.Ids != null && constraint.Ids.Contains(template.Id, StringComparer.OrdinalIgnoreCase),
            "ConstraintEnchantmentEligible" => constraint.GetWeightedEnchantments().Any(e => template.Enchantments != null && template.Enchantments.ContainsKey(e.Enchantment)),
            _ => true
        };
        return negate ? !raw : raw;
    }

    private static bool MatchListOperator(List<string>? items, string? op, Func<string, bool> predicate)
    {
        if (items == null || items.Count == 0)
        {
            return op is "All" or "None";
        }
        if (op == "All")
        {
            for (int i = 0; i < items.Count; i++)
                if (!predicate(items[i])) return false;
            return true;
        }
        if (op == "None")
        {
            for (int i = 0; i < items.Count; i++)
                if (predicate(items[i])) return false;
            return true;
        }
        for (int i = 0; i < items.Count; i++)
            if (predicate(items[i])) return true;
        return false;
    }

    private static IReadOnlyList<CardInstance> BuildDefaultExtremePool(CardInstance card, PlayerState owner, RuleExecutionContext ctx)
    {
        var soldCard = AbilityExecutor.GetActiveSoldCard(ctx);
        var pool = new List<CardInstance>(owner.Hand.Cards.Count + owner.Stash.Cards.Count + owner.Skills.Count + owner.SocketEffects.Count);
        void AddList(List<CardInstance> cards)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (!c.IsDestroyed && !ReferenceEquals(c, soldCard))
                    pool.Add(c);
            }
        }
        AddList(owner.Hand.Cards);
        AddList(owner.Stash.Cards);
        AddList(owner.Skills);
        AddList(owner.SocketEffects);
        if (!card.IsDestroyed && !pool.Contains(card))
            pool.Add(card);
        return pool;
    }

    private static bool IsExtremeAttr(CardInstance card, PlayerState owner, string attr, bool highest, RuleExecutionContext ctx)
    {
        IReadOnlyList<CardInstance> pool = ctx.ExtremeCandidatePool ?? BuildDefaultExtremePool(card, owner, ctx);
        if (pool.Count == 0) return false;

        if (highest)
        {
            // 严格对齐官方反编译 TCardConditionalAttributeHighest.cs:54-65（int num = 0; val > num）
            CardInstance? best = null;
            double bestVal = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var c = pool[i];
                double v = GetCardAttr(c, attr, ctx);
                if (v > bestVal)
                {
                    bestVal = v;
                    best = c;
                }
            }
            return ReferenceEquals(card, best);
        }
        else
        {
            CardInstance? best = null;
            double bestVal = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var c = pool[i];
                if (!c.HasAttribute(attr)) continue;
                double v = GetCardAttr(c, attr, ctx);
                if (best == null || v < bestVal)
                {
                    bestVal = v;
                    best = c;
                }
            }
            return best != null && ReferenceEquals(card, best);
        }
    }

    private static bool IsExtremeSize(CardInstance card, PlayerState owner, bool largest, RuleExecutionContext ctx)
    {
        IReadOnlyList<CardInstance> pool = ctx.ExtremeCandidatePool ?? BuildDefaultExtremePool(card, owner, ctx);
        if (pool.Count == 0) return false;
        int mySize = (int)card.EffectiveSize;
        if (largest)
        {
            int maxSize = (int)CardSize.Small;
            foreach (var c in pool)
            {
                int sz = (int)c.EffectiveSize;
                if (sz > maxSize) maxSize = sz;
            }
            return mySize == maxSize;
        }
        else
        {
            int minSize = (int)CardSize.Large;
            foreach (var c in pool)
            {
                int sz = (int)c.EffectiveSize;
                if (sz < minSize) minSize = sz;
            }
            return mySize == minSize;
        }
    }
}
