using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 光环引擎：支持全部 4 种 TAuraAction*，每当棋盘位置或属性变化时动态重算光环效果。
/// </summary>
public static class AuraEngine
{
    private static readonly List<AuraFlyingTransition> EmptyFlyingTransitions = new(0);

    public readonly record struct AuraFlyingTransition(
        PlayerState Owner,
        CardInstance? SourceCard,
        CardInstance TargetCard,
        bool StartedFlying);

    public static void RecalculateAuras(PlayerState p1, PlayerState? p2, RuleScope scope, int day, int hour)
        => RecalculateAuras(p1, p2, scope, day, hour, null);

    /// <summary>
    /// 重算双方光环属性与附加标签（支持传入 <paramref name="repo"/> 以完整支持条件求值）。
    /// 当光环首次为卡牌赋予 Flying（0 → &gt;0）时，直接推进对应侧的飞行 Quest；
    /// 在非战斗（OutOfCombat）作用域下同步补满所有持有卡牌弹药并联动刷新弹药依赖光环。
    /// </summary>
    public static void RecalculateAuras(PlayerState p1, PlayerState? p2, RuleScope scope, int day, int hour, GameDataRepository? repo)
    {
        var transitions = RecalculateAurasInternal(p1, p2, scope, day, hour, repo);

        if (scope == RuleScope.OutOfCombat)
        {
            bool anyAmmoChanged = RefillSideAmmoIfOutOfCombat(p1)
                                | (p2 != null && RefillSideAmmoIfOutOfCombat(p2));
            if (anyAmmoChanged)
                RecalculateAurasInternal(p1, p2, scope, day, hour, repo);
        }

        if (transitions.Count == 0) return;

        foreach (var tr in transitions)
        {
            if (!tr.StartedFlying) continue;
            var opp = ReferenceEquals(tr.Owner, p1) ? p2 : p1;
            var qCtx = new RuleExecutionContext(repo!, scope, tr.Owner, opp, new Random(0))
            {
                LocalPlayerId = p1.PlayerId,
                CurrentDay = day,
                CurrentHour = hour,
                SelfCard = null,
                TriggerSourceCard = tr.SourceCard,
                TriggerTargetCard = tr.TargetCard,
                LastAttributeChangeDelta = 1
            };
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartedFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartsFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardAttributeChanged", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
        }
    }

    private static bool RefillSideAmmoIfOutOfCombat(PlayerState side)
    {
        bool anyChanged = false;
        foreach (var c in side.EnumerateAllOwnedCards())
        {
            int prev = c.CurrentAmmo;
            c.RefillAmmoToMax();
            if (c.CurrentAmmo != prev) anyChanged = true;
        }
        return anyChanged;
    }

    internal static List<AuraFlyingTransition> RecalculateAurasInternal(
        PlayerState p1,
        PlayerState? p2,
        RuleScope scope,
        int day,
        int hour,
        GameDataRepository? repo)
    {
        bool trackFlying = NeedsFlyingAuraTracking(p1, scope) || (p2 != null && NeedsFlyingAuraTracking(p2, scope));
        Dictionary<CardInstance, bool>? wasFlying = null;
        Dictionary<CardInstance, CardInstance>? flyingGranters = null;
        if (trackFlying)
        {
            wasFlying = new Dictionary<CardInstance, bool>();
            CaptureFlyingState(p1, wasFlying);
            if (p2 != null) CaptureFlyingState(p2, wasFlying);
            flyingGranters = new Dictionary<CardInstance, CardInstance>();
        }

        ClearSide(p1);
        if (p2 != null) ClearSide(p2);

        bool hasP1Aura = HasAnyActiveAura(p1);
        bool hasP2Aura = p2 != null && HasAnyActiveAura(p2);

        if (hasP1Aura || hasP2Aura)
        {
            // Pass 1：直接写入 1 跳活态属性与标签
            if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, flyingGranters: null, staging: null);
            if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, flyingGranters: null, staging: null);

            // Pass 2：基于 Pass 1 活态收敛 2 跳依赖（如 Golden Custom_2 += 1 -> SellPrice *= Custom_2）
            var staging = new AuraStagingBuffer();
            if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, flyingGranters: null, staging);
            if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, flyingGranters: null, staging);

            ClearSide(p1);
            if (p2 != null) ClearSide(p2);
            staging.Commit();
            staging.Clear();

            // Pass 3：基于已收敛的 Pass 2 状态收尾 3 跳聚合光环（如 Shield/Damage 聚合 SellPrice）及飞行跃迁追踪
            if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, flyingGranters, staging);
            if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, flyingGranters, staging);

            ClearSide(p1);
            if (p2 != null) ClearSide(p2);
            staging.Commit();
        }

        if (!trackFlying || wasFlying == null || flyingGranters == null)
            return EmptyFlyingTransitions;

        var transitions = new List<AuraFlyingTransition>();
        CollectFlyingTransitions(p1, wasFlying, flyingGranters, transitions);
        if (p2 != null) CollectFlyingTransitions(p2, wasFlying, flyingGranters, transitions);
        return transitions;
    }

    private static bool NeedsFlyingAuraTracking(PlayerState owner, RuleScope scope)
        => ListHasFlyingAuraOrFlag(owner.Hand.Cards, scope)
        || ListHasFlyingAuraOrFlag(owner.Skills, scope)
        || ListHasFlyingAuraOrFlag(owner.SocketEffects, scope)
        || (scope == RuleScope.InCombat && ListHasFlyingAuraOrFlag(owner.PlayerEffects, scope))
        || ListHasFlyingAuraOrFlag(owner.Stash.Cards, scope);

    private static bool ListHasFlyingAuraOrFlag(List<CardInstance> cards, RuleScope scope)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.AuraAdd.Count > 0 && c.AuraAdd.ContainsKey("Flying"))
                return true;
            if (c.IsDestroyed || c.IsDisabled || c.ActiveAuras.Count == 0) continue;
            for (int j = 0; j < c.ActiveAuras.Count; j++)
            {
                var aura = c.ActiveAuras[j];
                if (!IsWorksInMatch(aura.WorksIn, scope)) continue;
                var act = aura.Action;
                if (act != null
                    && string.Equals(act.Type, "TAuraActionCardModifyAttribute", StringComparison.Ordinal)
                    && string.Equals(act.AttributeType, "Flying", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static void CaptureFlyingState(PlayerState player, Dictionary<CardInstance, bool> state)
    {
        foreach (var c in player.EnumerateAllOwnedCards())
            state[c] = c.GetAttribute("Flying") > 0;
    }

    private static void CollectFlyingTransitions(
        PlayerState owner,
        Dictionary<CardInstance, bool> beforeState,
        Dictionary<CardInstance, CardInstance> flyingGranters,
        List<AuraFlyingTransition> transitions)
    {
        foreach (var c in owner.EnumerateAllOwnedCards())
        {
            bool prev = beforeState.GetValueOrDefault(c, false);
            bool now = c.GetAttribute("Flying") > 0;
            if (prev == now) continue;
            flyingGranters.TryGetValue(c, out var granter);
            transitions.Add(new AuraFlyingTransition(owner, granter, c, StartedFlying: now));
        }
    }

    private static void ClearSide(PlayerState player)
    {
        if (player.AuraAdd.Count > 0 || player.AuraMult.Count > 0)
            player.ClearAuraModifiers();

        ClearCards(player.Hand.Cards);
        ClearCards(player.Stash.Cards);
        ClearCards(player.Skills);
        ClearCards(player.SocketEffects);
        ClearCards(player.PlayerEffects);
    }

    private static void ClearCards(List<CardInstance> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.AuraAdd.Count > 0 || c.AuraMult.Count > 0 || c.AuraTags.Count > 0)
                c.ClearAuraModifiers();
        }
    }

    private static bool HasAnyActiveAura(PlayerState owner)
    {
        foreach (var c in owner.Hand.Cards)
            if (!c.IsDestroyed && !c.IsDisabled && c.ActiveAuras.Count > 0) return true;
        foreach (var s in owner.Skills)
            if (!s.IsDestroyed && !s.IsDisabled && s.ActiveAuras.Count > 0) return true;
        foreach (var se in owner.SocketEffects)
            if (!se.IsDestroyed && !se.IsDisabled && se.ActiveAuras.Count > 0) return true;
        foreach (var pe in owner.PlayerEffects)
            if (!pe.IsDestroyed && !pe.IsDisabled && pe.ActiveAuras.Count > 0) return true;
        foreach (var c in owner.Stash.Cards)
            if (!c.IsDestroyed && !c.IsDisabled && c.ActiveAuras.Count > 0) return true;
        return false;
    }

    private sealed class AuraStagingBuffer
    {
        public readonly List<(CardInstance Target, string Attr, string Op, double Value)> CardAttrs = new();
        public readonly List<(PlayerState Target, string Attr, string Op, double Value)> PlayerAttrs = new();
        public readonly List<(CardInstance Target, string Tag)> CardTags = new();

        public void Clear()
        {
            CardAttrs.Clear();
            PlayerAttrs.Clear();
            CardTags.Clear();
        }

        public void Commit()
        {
            for (int i = 0; i < CardAttrs.Count; i++)
            {
                var (t, attr, op, val) = CardAttrs[i];
                t.ApplyAuraAttribute(attr, op, val);
            }
            for (int i = 0; i < PlayerAttrs.Count; i++)
            {
                var (t, attr, op, val) = PlayerAttrs[i];
                t.ApplyAuraAttribute(attr, op, val);
            }
            for (int i = 0; i < CardTags.Count; i++)
            {
                var (t, tag) = CardTags[i];
                t.AuraTags.Add(tag);
            }
        }
    }

    private static readonly RuleNode SelfTargetNode = new() { Type = "TTargetCardSelf" };

    private static bool IsPerTargetNullCardRef(RuleNode valNode)
        => valNode.Target == null
        && valNode.Type is "TReferenceValueCardAttribute"
            or "TReferenceValueCardAttributeUnscaled"
            or "TReferenceValueCardTagCount"
            or "TReferenceValueCardHeroCount";

    private static double EvaluateAuraCardValueForTarget(
        RuleNode valNode,
        CardInstance target,
        CardInstance sourceCard,
        RuleExecutionContext sourceCtx)
    {
        var baseNode = new RuleNode
        {
            Type = valNode.Type,
            AttributeType = valNode.AttributeType,
            DefaultValue = valNode.DefaultValue,
            Distinct = valNode.Distinct,
            IncludeCommon = valNode.IncludeCommon,
            Target = SelfTargetNode
        };
        double raw = ValueAndConditionEvaluator.EvaluateValue(baseNode, sourceCtx.WithCards(target, sourceCard, target), valNode.DefaultValue ?? 0);
        var modifier = valNode.Modifier;
        if (modifier?.Value == null) return raw;

        // 对齐官方 TValueModifier（Modifier 内引用如 Clean Storefront 的 Custom_0 仍以 sourceCard 为中心求值）
        float orig = (float)raw;
        float modVal = (float)ValueAndConditionEvaluator.EvaluateValue(modifier.Value, sourceCtx, 0);
        bool shouldRound = modifier.ShouldRound ?? true;
        static double RoundOff(double v) => Math.Round(v, MidpointRounding.AwayFromZero);
        return modifier.ModifyMode switch
        {
            "Subtract" => orig - modVal,
            "Multiply" => shouldRound ? RoundOff(orig * modVal) : (orig * modVal),
            "Divide" => Math.Abs(modVal) < 1e-6f ? orig : (shouldRound ? RoundOff(orig / modVal) : (orig / modVal)),
            _ => orig + modVal
        };
    }

    private static void ApplySideAuras(
        PlayerState owner,
        PlayerState? opponent,
        RuleScope scope,
        int day,
        int hour,
        GameDataRepository? repo,
        string localPlayerId,
        Dictionary<CardInstance, CardInstance>? flyingGranters,
        AuraStagingBuffer? staging)
    {
        if (!HasAnyActiveAura(owner)) return;

        var rng = new Random(0);
        var baseCtx = new RuleExecutionContext(repo!, scope, owner, opponent, rng)
        {
            LocalPlayerId = localPlayerId,
            CurrentDay = day,
            CurrentHour = hour
        };

        ApplyCardListAuras(owner.Hand.Cards, inHand: true, scope, baseCtx, flyingGranters, staging);
        ApplyCardListAuras(owner.Skills, inHand: true, scope, baseCtx, flyingGranters, staging);
        ApplyCardListAuras(owner.SocketEffects, inHand: true, scope, baseCtx, flyingGranters, staging);
        if (scope == RuleScope.InCombat && owner.PlayerEffects.Count > 0)
            ApplyCardListAuras(owner.PlayerEffects, inHand: true, scope, baseCtx, flyingGranters, staging);
        ApplyCardListAuras(owner.Stash.Cards, inHand: false, scope, baseCtx, flyingGranters, staging);
    }

    private static void ApplyCardListAuras(
        List<CardInstance> cards,
        bool inHand,
        RuleScope scope,
        RuleExecutionContext baseCtx,
        Dictionary<CardInstance, CardInstance>? flyingGranters,
        AuraStagingBuffer? staging)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (card.IsDestroyed || card.IsDisabled || card.ActiveAuras.Count == 0) continue;

            for (int j = 0; j < card.ActiveAuras.Count; j++)
            {
                var aura = card.ActiveAuras[j];
                if (!IsWorksInMatch(aura.WorksIn, scope)) continue;
                if (!IsActiveInMatch(aura.ActiveIn, inHand, scope)) continue;

                var ctx = baseCtx.WithCards(card, card);
                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(aura.Prerequisites, ctx))
                    continue;

                var action = aura.Action;
                if (action == null) continue;

                ApplyAuraAction(action, card, ctx, flyingGranters, staging);
            }
        }
    }

    /// <summary>判断给定的光环动作 $type 是否已被光环引擎显式支持。</summary>
    public static bool IsKnownAuraActionType(string? actionType) => actionType is
        "TAuraActionCardModifyAttribute" or
        "TAuraActionPlayerModifyAttribute" or
        "TAuraActionCardAddTagsList" or
        "TAuraActionCardAddTagsBySource";

    private static void ApplyAuraAction(
        RuleNode action,
        CardInstance sourceCard,
        RuleExecutionContext ctx,
        Dictionary<CardInstance, CardInstance>? flyingGranters,
        AuraStagingBuffer? staging)
    {
        switch (action.Type)
        {
            case "TAuraActionCardModifyAttribute":
            {
                var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                bool perTargetEval = valNode != null && IsPerTargetNullCardRef(valNode);
                double sharedVal = (!perTargetEval && valNode != null)
                    ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx)
                    : (!perTargetEval ? (action.GetFixedNumericValue() ?? 0) : 0);
                if (double.IsNaN(sharedVal) || double.IsInfinity(sharedVal)) sharedVal = 0;
                var attr = action.AttributeType ?? "Custom_0";
                var op = action.Operation ?? "Add";
                foreach (var target in TargetResolver.ResolveCards(action.Target, ctx))
                {
                    double val = perTargetEval
                        ? EvaluateAuraCardValueForTarget(valNode!, target, sourceCard, ctx)
                        : sharedVal;
                    if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                    if (staging != null)
                        staging.CardAttrs.Add((target, attr, op, val));
                    else
                        target.ApplyAuraAttribute(attr, op, val);

                    bool grantsFlying = flyingGranters != null
                        && attr.Equals("Flying", StringComparison.OrdinalIgnoreCase)
                        && val > 0
                        && !string.Equals(op, "Subtract", StringComparison.OrdinalIgnoreCase);
                    if (grantsFlying) flyingGranters![target] = sourceCard;
                }
                break;
            }
            case "TAuraActionPlayerModifyAttribute":
            {
                var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                double val = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (action.GetFixedNumericValue() ?? 0);
                if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                var attr = action.AttributeType ?? "HealthMax";
                var op = action.Operation ?? "Add";
                foreach (var targetPlayer in TargetResolver.ResolvePlayers(action.Target, ctx))
                {
                    if (staging != null)
                        staging.PlayerAttrs.Add((targetPlayer, attr, op, val));
                    else
                        targetPlayer.ApplyAuraAttribute(attr, op, val);
                }
                break;
            }
            case "TAuraActionCardAddTagsList":
            {
                if (action.Tags == null) break;
                foreach (var target in TargetResolver.ResolveCards(action.Target, ctx))
                {
                    foreach (var tag in action.Tags)
                    {
                        if (staging != null) staging.CardTags.Add((target, tag));
                        else target.AuraTags.Add(tag);
                    }
                }
                break;
            }
            case "TAuraActionCardAddTagsBySource":
            {
                var sources = action.Source != null ? TargetResolver.ResolveCards(action.Source, ctx) : [sourceCard];
                var tags = sources.SelectMany(s => s.GetAllTags()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var target in TargetResolver.ResolveCards(action.Target, ctx))
                {
                    foreach (var tag in tags)
                    {
                        if (staging != null) staging.CardTags.Add((target, tag));
                        else target.AuraTags.Add(tag);
                    }
                }
                break;
            }
            default:
                RuleExecutionContext.NotifyUnknownRuleType(ctx, "TAuraAction", action.Type);
                break;
        }
    }

    /// <summary>
    /// 校验光环在当前所在区域与规则作用域下是否生效：
    /// 战斗阶段（InCombat）中背包（Stash）里的常规 HandAndStash 光环一律不向场上生效，
    /// 仅保留显式标记为 StashOnly 的局内背包例外；局外（OutOfCombat）正常保留 HandAndStash 经济/商店光环。
    /// </summary>
    private static bool IsActiveInMatch(string? activeIn, bool inHand, RuleScope scope)
    {
        if (inHand)
            return activeIn != "StashOnly";

        if (scope == RuleScope.InCombat)
            return activeIn == "StashOnly";

        return activeIn != "HandOnly";
    }

    private static bool IsWorksInMatch(string? worksIn, RuleScope scope) => worksIn switch
    {
        "CombatOnly" => scope == RuleScope.InCombat,
        "OutOfCombatOnly" => scope == RuleScope.OutOfCombat,
        _ => true // Anywhere
    };
}
