using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 光环引擎：支持全部 4 种 TAuraAction*，每当棋盘位置或属性变化时动态重算光环效果；
/// 内置线程局部缓冲复用与迭代收敛早停机制，并支持 SelectionSet 货架光环与飞行跃迁事件派发。
/// </summary>
public static class AuraEngine
{
    private static readonly List<AuraFlyingTransition> EmptyFlyingTransitions = new(0);

    public readonly record struct AuraFlyingTransition(
        PlayerState Owner,
        CardInstance? SourceCard,
        CardInstance TargetCard,
        bool StartedFlying);

    private sealed class ThreadScratch
    {
        public bool InUse;
        public readonly AuraStagingBuffer StagingA = new();
        public readonly AuraStagingBuffer StagingB = new();
        public readonly Dictionary<CardInstance, bool> WasFlying = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<CardInstance, CardInstance> FlyingGranters = new(ReferenceEqualityComparer.Instance);
    }

    [ThreadStatic]
    private static ThreadScratch? t_scratch;

    private static ThreadScratch AcquireScratch(out bool pooled)
    {
        var s = t_scratch ??= new ThreadScratch();
        if (!s.InUse)
        {
            s.InUse = true;
            pooled = true;
            return s;
        }
        pooled = false;
        return new ThreadScratch();
    }

    private static void ReleaseScratch(ThreadScratch s, bool pooled)
    {
        s.StagingA.Clear();
        s.StagingB.Clear();
        s.WasFlying.Clear();
        s.FlyingGranters.Clear();
        if (pooled) s.InUse = false;
    }

    public static void RecalculateAuras(PlayerState p1, PlayerState? p2, RuleScope scope, int day, int hour)
        => RecalculateAuras(p1, p2, scope, day, hour, repo: null, selectionCards: null, onFlyingTransition: null);

    /// <summary>
    /// 重算双方光环属性与附加标签（支持传入 <paramref name="repo"/> 以完整支持条件求值）。
    /// 当光环首次为卡牌赋予 Flying（0 → &gt;0）时，直接推进对应侧的飞行 Quest；
    /// 在非战斗（OutOfCombat）作用域下同步补满所有持有卡牌弹药并联动刷新弹药依赖光环。
    /// </summary>
    public static void RecalculateAuras(PlayerState p1, PlayerState? p2, RuleScope scope, int day, int hour, GameDataRepository? repo)
        => RecalculateAuras(p1, p2, scope, day, hour, repo, selectionCards: null, onFlyingTransition: null);

    /// <summary>
    /// 重算双方及可选货架（<paramref name="selectionCards"/>）的光环属性与附加标签；
    /// 当提供 <paramref name="onFlyingTransition"/> 回调时，将全部飞行状态跃迁（起飞与降落）派发给调用方（如 CombatSimulator）。
    /// </summary>
    public static void RecalculateAuras(
        PlayerState p1,
        PlayerState? p2,
        RuleScope scope,
        int day,
        int hour,
        GameDataRepository? repo,
        IReadOnlyList<CardInstance>? selectionCards,
        Action<AuraFlyingTransition>? onFlyingTransition = null)
    {
        var transitions = RecalculateAurasInternal(p1, p2, scope, day, hour, repo, selectionCards);

        if (scope == RuleScope.OutOfCombat)
        {
            bool anyAmmoChanged = RefillSideAmmoIfOutOfCombat(p1)
                                | (p2 != null && RefillSideAmmoIfOutOfCombat(p2));
            if (anyAmmoChanged)
            {
                var secondTransitions = RecalculateAurasInternal(p1, p2, scope, day, hour, repo, selectionCards);
                if (secondTransitions.Count > 0)
                {
                    if (transitions.Count == 0)
                        transitions = secondTransitions;
                    else
                        transitions.AddRange(secondTransitions);
                }
            }
        }

        if (transitions.Count == 0) return;

        if (onFlyingTransition != null)
        {
            for (int i = 0; i < transitions.Count; i++)
                onFlyingTransition(transitions[i]);
            return;
        }

        if (repo == null) return;

        for (int i = 0; i < transitions.Count; i++)
        {
            var tr = transitions[i];
            if (!tr.StartedFlying) continue;
            var opp = ReferenceEquals(tr.Owner, p1) ? p2 : p1;
            var qCtx = new RuleExecutionContext(repo, scope, tr.Owner, opp, new Random(0))
            {
                LocalPlayerId = p1.PlayerId,
                CurrentDay = day,
                CurrentHour = hour,
                SelfCard = null,
                TriggerSourceCard = tr.SourceCard,
                TriggerTargetCard = tr.TargetCard,
                SelectionCards = selectionCards,
                LastAttributeChangeDelta = 1
            };
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartedFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartsFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
            AbilityExecutor.TryAdvanceQuests("TTriggerOnCardAttributeChanged", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1, "PVP", "", qCtx);
        }
    }

    /// <summary>快速检查双方是否持有任意未禁用/未销毁的光环源（供战斗热路径 O(1)/O(N) 跳过空光环重算）。</summary>
    public static bool HasAnyAuraSource(PlayerState p1, PlayerState? p2 = null)
        => HasAnyActiveAura(p1) || (p2 != null && HasAnyActiveAura(p2));

    /// <summary>检查双方或货架是否存在已挂载的残留光环修饰（用于刚摧毁/卖掉最后一张光环卡时的清理判定）。</summary>
    public static bool HasAnyAppliedAuraModifiers(PlayerState p1, PlayerState? p2 = null, IReadOnlyList<CardInstance>? selectionCards = null)
    {
        if (SideHasAppliedAuraModifiers(p1)) return true;
        if (p2 != null && SideHasAppliedAuraModifiers(p2)) return true;
        if (selectionCards != null)
        {
            for (int i = 0; i < selectionCards.Count; i++)
            {
                var c = selectionCards[i];
                if (c.AuraAdd.Count > 0 || c.AuraMult.Count > 0 || c.AuraTags.Count > 0)
                    return true;
            }
        }
        return false;
    }

    private static bool SideHasAppliedAuraModifiers(PlayerState player)
    {
        if (player.AuraAdd.Count > 0 || player.AuraMult.Count > 0) return true;
        return ListHasAppliedAura(player.Hand.Cards)
            || ListHasAppliedAura(player.Stash.Cards)
            || ListHasAppliedAura(player.Skills)
            || ListHasAppliedAura(player.SocketEffects)
            || ListHasAppliedAura(player.PlayerEffects);
    }

    private static bool ListHasAppliedAura(List<CardInstance> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.AuraAdd.Count > 0 || c.AuraMult.Count > 0 || c.AuraTags.Count > 0)
                return true;
        }
        return false;
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
        GameDataRepository? repo,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        bool hasP1Aura = HasAnyActiveAura(p1);
        bool hasP2Aura = p2 != null && HasAnyActiveAura(p2);

        // 零光环且零残留修饰时直接早停，零分配
        if (!hasP1Aura && !hasP2Aura && !HasAnyAppliedAuraModifiers(p1, p2, selectionCards))
            return EmptyFlyingTransitions;

        var scratch = AcquireScratch(out bool pooled);
        try
        {
            bool trackFlying = NeedsFlyingAuraTracking(p1, scope) || (p2 != null && NeedsFlyingAuraTracking(p2, scope));
            Dictionary<CardInstance, bool>? wasFlying = null;
            Dictionary<CardInstance, CardInstance>? flyingGranters = null;
            if (trackFlying)
            {
                wasFlying = scratch.WasFlying;
                wasFlying.Clear();
                CaptureFlyingState(p1, wasFlying);
                if (p2 != null) CaptureFlyingState(p2, wasFlying);
                flyingGranters = scratch.FlyingGranters;
                flyingGranters.Clear();
            }

            ClearSide(p1);
            if (p2 != null) ClearSide(p2);
            ClearSelectionCards(selectionCards);

            if (hasP1Aura || hasP2Aura)
            {
                var stagingA = scratch.StagingA;
                var stagingB = scratch.StagingB;
                stagingA.Clear();
                stagingB.Clear();

                // Pass 1：收集 1 跳光环输出并提交
                if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingA);
                if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingA);
                stagingA.Commit();

                // 若 Pass 1 产生有效光环输出，尝试 Pass 2 并做迭代收敛早停检测
                if (!stagingA.IsEmpty)
                {
                    flyingGranters?.Clear();
                    if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingB);
                    if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingB);

                    // 早停条件：Pass 2 与 Pass 1 输出完全一致说明已 1 跳收敛，无需重复 Clear/Commit 与 Pass 3
                    if (!stagingB.EqualsBuffer(stagingA))
                    {
                        ClearSide(p1);
                        if (p2 != null) ClearSide(p2);
                        ClearSelectionCards(selectionCards);
                        stagingB.Commit();

                        // Pass 3：收敛 3 跳聚合依赖（如 Shield/Damage 聚合 SellPrice）并采集最终飞行授予者
                        stagingA.Clear();
                        flyingGranters?.Clear();
                        if (hasP1Aura) ApplySideAuras(p1, p2, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingA);
                        if (hasP2Aura && p2 != null) ApplySideAuras(p2, p1, scope, day, hour, repo, p1.PlayerId, selectionCards, flyingGranters, stagingA);

                        if (!stagingA.EqualsBuffer(stagingB))
                        {
                            ClearSide(p1);
                            if (p2 != null) ClearSide(p2);
                            ClearSelectionCards(selectionCards);
                            stagingA.Commit();
                        }
                    }
                }
            }

            if (!trackFlying || wasFlying == null || flyingGranters == null)
                return EmptyFlyingTransitions;

            var transitions = new List<AuraFlyingTransition>();
            CollectFlyingTransitions(p1, wasFlying, flyingGranters, transitions);
            if (p2 != null) CollectFlyingTransitions(p2, wasFlying, flyingGranters, transitions);
            return transitions.Count == 0 ? EmptyFlyingTransitions : transitions;
        }
        finally
        {
            ReleaseScratch(scratch, pooled);
        }
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
        CaptureListFlyingState(player.Hand.Cards, state);
        CaptureListFlyingState(player.Stash.Cards, state);
        CaptureListFlyingState(player.Skills, state);
        CaptureListFlyingState(player.SocketEffects, state);
    }

    private static void CaptureListFlyingState(List<CardInstance> cards, Dictionary<CardInstance, bool> state)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            state[c] = c.GetAttribute("Flying") > 0;
        }
    }

    private static void CollectFlyingTransitions(
        PlayerState owner,
        Dictionary<CardInstance, bool> beforeState,
        Dictionary<CardInstance, CardInstance> flyingGranters,
        List<AuraFlyingTransition> transitions)
    {
        CollectListFlyingTransitions(owner, owner.Hand.Cards, beforeState, flyingGranters, transitions);
        CollectListFlyingTransitions(owner, owner.Stash.Cards, beforeState, flyingGranters, transitions);
        CollectListFlyingTransitions(owner, owner.Skills, beforeState, flyingGranters, transitions);
        CollectListFlyingTransitions(owner, owner.SocketEffects, beforeState, flyingGranters, transitions);
    }

    private static void CollectListFlyingTransitions(
        PlayerState owner,
        List<CardInstance> cards,
        Dictionary<CardInstance, bool> beforeState,
        Dictionary<CardInstance, CardInstance> flyingGranters,
        List<AuraFlyingTransition> transitions)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
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

    private static void ClearSelectionCards(IReadOnlyList<CardInstance>? selectionCards)
    {
        if (selectionCards == null) return;
        for (int i = 0; i < selectionCards.Count; i++)
        {
            var c = selectionCards[i];
            if (c.AuraAdd.Count > 0 || c.AuraMult.Count > 0 || c.AuraTags.Count > 0)
                c.ClearAuraModifiers();
        }
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
        return ListHasActiveAura(owner.Hand.Cards)
            || ListHasActiveAura(owner.Skills)
            || ListHasActiveAura(owner.SocketEffects)
            || ListHasActiveAura(owner.PlayerEffects)
            || ListHasActiveAura(owner.Stash.Cards);
    }

    private static bool ListHasActiveAura(List<CardInstance> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (!c.IsDestroyed && !c.IsDisabled && c.ActiveAuras.Count > 0)
                return true;
        }
        return false;
    }

    private sealed class AuraStagingBuffer
    {
        public readonly List<(CardInstance Target, string Attr, string Op, double Value)> CardAttrs = new(32);
        public readonly List<(PlayerState Target, string Attr, string Op, double Value)> PlayerAttrs = new(8);
        public readonly List<(CardInstance Target, string Tag)> CardTags = new(16);

        public bool IsEmpty => CardAttrs.Count == 0 && PlayerAttrs.Count == 0 && CardTags.Count == 0;

        public void Clear()
        {
            CardAttrs.Clear();
            PlayerAttrs.Clear();
            CardTags.Clear();
        }

        public bool EqualsBuffer(AuraStagingBuffer other)
        {
            if (CardAttrs.Count != other.CardAttrs.Count
                || PlayerAttrs.Count != other.PlayerAttrs.Count
                || CardTags.Count != other.CardTags.Count)
                return false;

            for (int i = 0; i < CardAttrs.Count; i++)
            {
                var a = CardAttrs[i];
                var b = other.CardAttrs[i];
                if (!ReferenceEquals(a.Target, b.Target)
                    || !string.Equals(a.Attr, b.Attr, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(a.Op, b.Op, StringComparison.Ordinal)
                    || Math.Abs(a.Value - b.Value) > 1e-9)
                    return false;
            }
            for (int i = 0; i < PlayerAttrs.Count; i++)
            {
                var a = PlayerAttrs[i];
                var b = other.PlayerAttrs[i];
                if (!ReferenceEquals(a.Target, b.Target)
                    || !string.Equals(a.Attr, b.Attr, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(a.Op, b.Op, StringComparison.Ordinal)
                    || Math.Abs(a.Value - b.Value) > 1e-9)
                    return false;
            }
            for (int i = 0; i < CardTags.Count; i++)
            {
                var a = CardTags[i];
                var b = other.CardTags[i];
                if (!ReferenceEquals(a.Target, b.Target)
                    || !string.Equals(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
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
                if (t.AuraTags.Add(tag))
                    t.MarkAttributeDirty();
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

        var prevSelf = sourceCtx.SelfCard;
        var prevSrc = sourceCtx.TriggerSourceCard;
        var prevTgt = sourceCtx.TriggerTargetCard;
        double raw;
        try
        {
            sourceCtx.SelfCard = target;
            sourceCtx.TriggerSourceCard = sourceCard;
            sourceCtx.TriggerTargetCard = target;
            raw = ValueAndConditionEvaluator.EvaluateValue(baseNode, sourceCtx, valNode.DefaultValue ?? 0);
        }
        finally
        {
            sourceCtx.SelfCard = prevSelf;
            sourceCtx.TriggerSourceCard = prevSrc;
            sourceCtx.TriggerTargetCard = prevTgt;
        }

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
        IReadOnlyList<CardInstance>? selectionCards,
        Dictionary<CardInstance, CardInstance>? flyingGranters,
        AuraStagingBuffer staging)
    {
        if (!HasAnyActiveAura(owner)) return;

        var rng = new Random(0);
        var baseCtx = new RuleExecutionContext(repo!, scope, owner, opponent, rng)
        {
            LocalPlayerId = localPlayerId,
            CurrentDay = day,
            CurrentHour = hour,
            SelectionCards = selectionCards
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
        RuleExecutionContext ctx,
        Dictionary<CardInstance, CardInstance>? flyingGranters,
        AuraStagingBuffer staging)
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

                // 复用单实例上下文，消除每张卡每条光环的 RuleExecutionContext 堆分配
                ctx.SelfCard = card;
                ctx.TriggerSourceCard = card;
                ctx.TriggerTargetCard = null;
                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(aura.Prerequisites, ctx))
                    continue;

                var action = aura.Action;
                if (action == null) continue;

                ApplyAuraAction(action, card, ctx, flyingGranters, staging);
            }
        }
        ctx.SelfCard = null;
        ctx.TriggerSourceCard = null;
        ctx.TriggerTargetCard = null;
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
        AuraStagingBuffer staging)
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
                var targets = TargetResolver.ResolveCards(action.Target, ctx);
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    double val = perTargetEval
                        ? EvaluateAuraCardValueForTarget(valNode!, target, sourceCard, ctx)
                        : sharedVal;
                    if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                    staging.CardAttrs.Add((target, attr, op, val));

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
                var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                for (int i = 0; i < targets.Count; i++)
                    staging.PlayerAttrs.Add((targets[i], attr, op, val));
                break;
            }
            case "TAuraActionCardAddTagsList":
            {
                if (action.Tags == null || action.Tags.Count == 0) break;
                var targets = TargetResolver.ResolveCards(action.Target, ctx);
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    for (int t = 0; t < action.Tags.Count; t++)
                        staging.CardTags.Add((target, action.Tags[t]));
                }
                break;
            }
            case "TAuraActionCardAddTagsBySource":
            {
                var sources = action.Source != null ? TargetResolver.ResolveCards(action.Source, ctx) : [sourceCard];
                var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tags = new List<string>(8);
                for (int s = 0; s < sources.Count; s++)
                {
                    foreach (var tag in sources[s].GetAllTags())
                    {
                        if (seenTags.Add(tag))
                            tags.Add(tag);
                    }
                }
                if (tags.Count == 0) break;
                var targets = TargetResolver.ResolveCards(action.Target, ctx);
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    for (int t = 0; t < tags.Count; t++)
                        staging.CardTags.Add((target, tags[t]));
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
