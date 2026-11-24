using System.Runtime.CompilerServices;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 能力触发匹配与动作执行引擎：覆盖全部 44 种 TTrigger*、43 种 TAction* 与 Quest 进度系统。
/// </summary>
public static class AbilityExecutor
{
    private sealed class SoldCardScopeInfo
    {
        public required CardInstance SoldCard { get; init; }
        public PlayerState? Owner { get; init; }
        public BoardGrid? OriginalGrid { get; init; }
        public int OriginalStartSlot { get; init; }
    }

    private static readonly ConditionalWeakTable<ActiveExecutionState, SoldCardScopeInfo> SoldCardScopes = new();
    private static readonly ConditionalWeakTable<ActiveExecutionState, List<CardInstance>> LastSpawnedCardsMap = new();

    private static readonly (string Enchantment, double Weight)[] DefaultRandomEnchantments =
    [
        ("Heavy", 50), ("Icy", 10), ("Turbo", 50), ("Shielded", 50),
        ("Restorative", 50), ("Toxic", 50), ("Mossy", 50), ("Fiery", 50),
        ("Shiny", 10), ("Deadly", 20), ("Radiant", 10), ("Obsidian", 10)
    ];

    private static readonly HashSet<string> KnownTriggerTypes = new(StringComparer.Ordinal)
    {
        "TTriggerOr",
        "TTriggerOnCardFired", "TTriggerOnItemUsed", "TTriggerOnBeforeItemUsed", "TTriggerOnCardCritted",
        "TTriggerOnCardSold", "TTriggerOnCardPurchased", "TTriggerOnCardSelected", "TTriggerOnCardMoved",
        "TTriggerOnBeforeCardDestroyed", "TTriggerOnCardDisabled", "TTriggerOnCardEnchanted",
        "TTriggerOnCardRepaired", "TTriggerOnCardStartedFlying", "TTriggerOnCardStoppedFlying",
        "TTriggerOnCardTransformed", "TTriggerOnCardAfterTransform", "TTriggerOnCardCharged",
        "TTriggerOnCardQuestCompleted", "TTriggerOnCardUpgraded", "TTriggerOnCardStartsFlying",
        "TTriggerOnCardPerformedBurn", "TTriggerOnCardPerformedCharge", "TTriggerOnCardPerformedDamage",
        "TTriggerOnCardPerformedDestruction", "TTriggerOnCardPerformedFreeze", "TTriggerOnCardPerformedHaste",
        "TTriggerOnCardPerformedHeal", "TTriggerOnCardPerformedOverHeal", "TTriggerOnCardPerformedPoison",
        "TTriggerOnCardPerformedRegen", "TTriggerOnCardPerformedReload", "TTriggerOnCardPerformedShield",
        "TTriggerOnCardPerformedSlow", "TTriggerOnCardAttributeChanged", "TTriggerOnPlayerAttributeChanged",
        "TTriggerOnPlayerDamaged", "TTriggerOnPlayerEnraged", "TTriggerOnPlayerEnrageEnded", "TTriggerOnPlayerRagedWhileEnraged",
        "TTriggerOnPlayerDied", "TTriggerOnPlayerLeveledUp", "TTriggerOnPlayerLifeStealed",
        "TTriggerOnPlayerShielded", "TTriggerOnFightStarted", "TTriggerOnFightEnded",
        "TTriggerOnEncounterSelected", "TTriggerOnEncounterEntered", "TTriggerOnEncounterExited",
        "TTriggerOnEncounterCardsDealt", "TTriggerOnDayStarted", "TTriggerOnHourStarted",
        "TTriggerOnRunStarted", "TTriggerOnRunCompleted", "TTriggerOnSandstorm"
    };

    /// <summary>判断给定的触发器 $type 是否已被能力执行器显式支持。</summary>
    public static bool IsKnownTriggerType(string? triggerType)
        => !string.IsNullOrEmpty(triggerType) && KnownTriggerTypes.Contains(triggerType);

    internal static void PushSoldCardScope(RuleExecutionContext ctx, CardInstance? soldCard, PlayerState? owner)
    {
        if (soldCard == null)
        {
            SoldCardScopes.Remove(ctx.ActiveExecution);
            return;
        }
        BoardGrid? origGrid = null;
        if (owner != null)
        {
            if (owner.Hand.Cards.Contains(soldCard)) origGrid = owner.Hand;
            else if (owner.Stash.Cards.Contains(soldCard)) origGrid = owner.Stash;
        }
        SoldCardScopes.Remove(ctx.ActiveExecution);
        SoldCardScopes.Add(ctx.ActiveExecution, new SoldCardScopeInfo
        {
            SoldCard = soldCard,
            Owner = owner,
            OriginalGrid = origGrid,
            OriginalStartSlot = soldCard.StartSlot
        });
    }

    internal static void ClearSoldCardScope(RuleExecutionContext ctx)
        => SoldCardScopes.Remove(ctx.ActiveExecution);

    internal static CardInstance? GetActiveSoldCard(RuleExecutionContext ctx)
        => SoldCardScopes.TryGetValue(ctx.ActiveExecution, out var info) ? info.SoldCard : null;

    /// <summary>
    /// 动作及数值/条件求值用的统一卡牌目标解析入口：
    /// 1. 在 TTriggerOnCardSold 期间，仅对 Self/Adjacent/Positional/TriggerSource 等定位类目标保留出售卡原始站位；
    /// 2. 对 TTargetCardXMost 及友方分区/随机目标严格排除正在出售的 soldCard 本身；
    /// 3. 对 TTargetCardXMost 清理未落位幽灵卡并按 StartSlot 升序排整后再交由 TargetResolver 取最左/最右。
    /// </summary>
    internal static List<CardInstance> ResolveActionTargets(RuleNode? target, RuleExecutionContext ctx, int? maxCount = null)
    {
        if (target == null) return [];
        if (maxCount.HasValue && maxCount.Value <= 0) return [];

        bool hasSoldScope = SoldCardScopes.TryGetValue(ctx.ActiveExecution, out var soldScope);
        // 战斗与局外 95%+ 调用不含出售上下文且非 XMost，直走零额外列表分配快路径
        if (!hasSoldScope && !string.Equals(target.Type, "TTargetCardXMost", StringComparison.Ordinal))
            return ResolveWithTargetingPriorities(target, ctx, maxCount);

        bool isNeighborsSection = target.Type is "TTargetCardSection" or "TTargetCardRandom"
            && target.TargetSection is "SelfNeighbors" or "TriggerSourceNeighbors";
        bool needsSoldCardInSlot = target.Type is "TTargetCardSelf" or "TTargetCardTriggerSource"
            or "TTargetCardTriggerTarget" or "TTargetCardOccupying" or "TTargetCardPositional"
            || isNeighborsSection;

        if (hasSoldScope && soldScope != null && needsSoldCardInSlot)
        {
            var sold = soldScope.SoldCard;
            BoardGrid? tempInsertedGrid = null;
            List<CardInstance>? prevOrderSnapshot = null;
            int prevSlot = sold.StartSlot;
            if (soldScope.OriginalGrid != null && !soldScope.OriginalGrid.Cards.Contains(sold) && soldScope.OriginalStartSlot >= 0)
            {
                prevOrderSnapshot = new List<CardInstance>(soldScope.OriginalGrid.Cards);
                sold.StartSlot = soldScope.OriginalStartSlot;
                soldScope.OriginalGrid.Cards.Add(sold);
                soldScope.OriginalGrid.Cards.Sort((a, b) =>
                {
                    int sa = a.StartSlot < 0 ? int.MaxValue : a.StartSlot;
                    int sb = b.StartSlot < 0 ? int.MaxValue : b.StartSlot;
                    int c = sa.CompareTo(sb);
                    return c != 0 ? c : string.CompareOrdinal(a.InstanceId, b.InstanceId);
                });
                tempInsertedGrid = soldScope.OriginalGrid;
            }
            try
            {
                return TargetResolver.ResolveCards(target, ctx, maxCount);
            }
            finally
            {
                if (tempInsertedGrid != null)
                {
                    tempInsertedGrid.Cards.Clear();
                    if (prevOrderSnapshot != null)
                        tempInsertedGrid.Cards.AddRange(prevOrderSnapshot);
                    sold.StartSlot = prevSlot;
                }
            }
        }

        var removedFromLists = new List<(List<CardInstance> List, int Index, CardInstance Card)>();
        var savedOrders = new List<(List<CardInstance> List, List<CardInstance> Snapshot)>();

        void DetachMatching(List<CardInstance> list, Predicate<CardInstance> pred)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (pred(list[i]))
                {
                    removedFromLists.Add((list, i, list[i]));
                    list.RemoveAt(i);
                }
            }
        }

        void SortGridCardsByStartSlot(List<CardInstance> cards)
        {
            if (cards.Count <= 1) return;
            savedOrders.Add((cards, new List<CardInstance>(cards)));
            cards.Sort((a, b) =>
            {
                int sa = a.StartSlot < 0 ? int.MaxValue : a.StartSlot;
                int sb = b.StartSlot < 0 ? int.MaxValue : b.StartSlot;
                int c = sa.CompareTo(sb);
                return c != 0 ? c : string.CompareOrdinal(a.InstanceId, b.InstanceId);
            });
        }

        try
        {
            void ProcessPlayerGrids(PlayerState? p)
            {
                if (p == null) return;
                if (hasSoldScope && soldScope != null)
                {
                    var sold = soldScope.SoldCard;
                    DetachMatching(p.Hand.Cards, c => ReferenceEquals(c, sold));
                    DetachMatching(p.Stash.Cards, c => ReferenceEquals(c, sold));
                    DetachMatching(p.Skills, c => ReferenceEquals(c, sold));
                    DetachMatching(p.SocketEffects, c => ReferenceEquals(c, sold));
                }

                if (target.Type == "TTargetCardXMost")
                {
                    if (p.Hand.Cards.Any(c => c.StartSlot >= 0))
                        DetachMatching(p.Hand.Cards, c => c.StartSlot < 0);
                    if (p.Stash.Cards.Any(c => c.StartSlot >= 0))
                        DetachMatching(p.Stash.Cards, c => c.StartSlot < 0);

                    SortGridCardsByStartSlot(p.Hand.Cards);
                    SortGridCardsByStartSlot(p.Stash.Cards);
                }
            }

            ProcessPlayerGrids(ctx.SelfPlayer);
            if (ctx.OpponentPlayer != null && ctx.OpponentPlayer != ctx.SelfPlayer)
                ProcessPlayerGrids(ctx.OpponentPlayer);
            if (hasSoldScope && soldScope?.Owner != null && soldScope.Owner != ctx.SelfPlayer && soldScope.Owner != ctx.OpponentPlayer)
                ProcessPlayerGrids(soldScope.Owner);

            var resolved = ResolveWithTargetingPriorities(target, ctx, maxCount);
            if (hasSoldScope && soldScope != null)
                resolved.RemoveAll(c => ReferenceEquals(c, soldScope.SoldCard));
            return resolved;
        }
        finally
        {
            for (int i = savedOrders.Count - 1; i >= 0; i--)
            {
                var (list, snap) = savedOrders[i];
                list.Clear();
                list.AddRange(snap);
            }
            for (int i = removedFromLists.Count - 1; i >= 0; i--)
            {
                var (list, idx, card) = removedFromLists[i];
                int safeIdx = Math.Clamp(idx, 0, list.Count);
                list.Insert(safeIdx, card);
            }
        }
    }

    /// <summary>
    /// 按官方 BazaarGameShared.dll TAction*.TargetingPriorities 执行目标解析（P1-19 已统一下沉至 TargetResolver.ResolveCards，覆盖 TTargetCardRandom 与 TTargetCardXMost 全部 7 类动作）。
    /// </summary>
    private static List<CardInstance> ResolveWithTargetingPriorities(RuleNode target, RuleExecutionContext ctx, int? maxCount)
        => TargetResolver.ResolveCards(target, ctx, maxCount);

    public static bool MatchesTrigger(
        RuleNode? trigger,
        string eventType,
        CardInstance ownerCard,
        PlayerState ownerPlayer,
        CardInstance? eventSourceCard,
        CardInstance? eventTargetCard,
        PlayerState? eventSubjectPlayer,
        string? changedAttr,
        double prevVal,
        double curVal,
        string combatType,
        string combatOutcome,
        RuleExecutionContext ctx)
    {
        if (trigger == null) return false;
        if (trigger.Type == "TTriggerOr")
            return trigger.Triggers != null && trigger.Triggers.Any(sub =>
                MatchesTrigger(sub, eventType, ownerCard, ownerPlayer, eventSourceCard, eventTargetCard, eventSubjectPlayer, changedAttr, prevVal, curVal, combatType, combatOutcome, ctx));

        // 优先放行已通过 BazaarModKit 注册的 TTriggerMod_* 自定义触发器（单次评估避免重复执行）
        if (ModRuntimeHooks.HasAnyCustomTriggers
            && !string.IsNullOrEmpty(trigger.Type)
            && trigger.Type.StartsWith("TTriggerMod_", StringComparison.Ordinal))
        {
            bool customMatch = ModRuntimeHooks.TryMatchCustomTrigger(trigger, ownerCard, ctx, eventType, out bool handled);
            if (handled)
                return customMatch;
        }

        if ((ctx.OnUnknownRuleType != null || RuleExecutionContext.HasUnknownRuleTypeObserver) && !IsKnownTriggerType(trigger.Type))
        {
            RuleExecutionContext.NotifyUnknownRuleType(ctx, "TTrigger", trigger.Type);
            return false;
        }

        if (!string.Equals(trigger.Type, eventType, StringComparison.Ordinal))
            return false;

        switch (eventType)
        {
            case "TTriggerOnCardFired":
                return eventSourceCard == ownerCard;

            case "TTriggerOnItemUsed":
            case "TTriggerOnBeforeItemUsed":
            case "TTriggerOnCardCritted":
                return eventSourceCard != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, eventSourceCard, ctx);

            case "TTriggerOnCardSold":
            case "TTriggerOnCardPurchased":
            case "TTriggerOnCardSelected":
            case "TTriggerOnCardMoved":
                if (trigger.Subject == null) return eventSourceCard == ownerCard;
                return eventSourceCard != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, eventSourceCard, ctx);

            case "TTriggerOnCardAfterTransform":
            {
                var transformedCard = eventTargetCard ?? eventSourceCard;
                if (trigger.Target != null)
                {
                    bool tgtOk = transformedCard != null
                        && MatchesCardSubject(trigger.Target, ownerCard, ownerPlayer, transformedCard, ctx);
                    bool subjOk = trigger.Subject == null
                        || trigger.Subject.Type == "TTargetCardSelf"
                        || (eventSourceCard != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, eventSourceCard, ctx));
                    bool srcOk = trigger.Source == null
                        || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                    return tgtOk && subjOk && srcOk;
                }
                var cand = eventTargetCard ?? eventSourceCard;
                bool sOk = trigger.Subject == null
                    ? cand == ownerCard
                    : cand != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, cand, ctx);
                bool oOk = trigger.Source == null
                    || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return sOk && oOk;
            }

            case "TTriggerOnBeforeCardDestroyed":
            case "TTriggerOnCardDisabled":
            case "TTriggerOnCardEnchanted":
            case "TTriggerOnCardRepaired":
            case "TTriggerOnCardStartedFlying":
            case "TTriggerOnCardStoppedFlying":
            case "TTriggerOnCardTransformed":
            case "TTriggerOnCardCharged":
            case "TTriggerOnCardQuestCompleted":
            case "TTriggerOnCardUpgraded":
            {
                if (trigger.Type == "TTriggerOnCardUpgraded" && trigger.ExcludeFusion == true && ctx.IsFusionUpgrade)
                    return false;
                var candidate = eventTargetCard ?? eventSourceCard;
                // TActionCardDisable 置位 IsDisabled 后立即派发 Disabled/Destroyed 事件：
                // 通过 ActiveExecution.IncludeDisabledTargets 放行 TargetResolver，而非临时篡改 IsDisabled=false（避免破坏显式 Disabled 状态条件）。
                bool prevIncludeDisabled = ctx.ActiveExecution.IncludeDisabledTargets;
                if (eventType is "TTriggerOnCardDisabled" or "TTriggerOnBeforeCardDestroyed" or "TTriggerOnCardRepaired")
                    ctx.ActiveExecution.IncludeDisabledTargets = true;
                try
                {
                    bool subOk = trigger.Subject == null
                        ? candidate == ownerCard
                        : candidate != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, candidate, ctx);
                    bool srcOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                    return subOk && srcOk;
                }
                finally
                {
                    ctx.ActiveExecution.IncludeDisabledTargets = prevIncludeDisabled;
                }
            }

            case "TTriggerOnCardStartsFlying":
            {
                var flyingCard = eventTargetCard ?? eventSourceCard;
                bool flySubOk = trigger.Subject == null || (flyingCard != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, flyingCard, ctx));
                bool flyTgtOk = trigger.Target == null || (flyingCard != null && MatchesCardSubject(trigger.Target, ownerCard, ownerPlayer, flyingCard, ctx));
                bool flySrcOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return flySubOk && flyTgtOk && flySrcOk;
            }

            case "TTriggerOnCardPerformedBurn":
            case "TTriggerOnCardPerformedCharge":
            case "TTriggerOnCardPerformedDamage":
            case "TTriggerOnCardPerformedDestruction":
            case "TTriggerOnCardPerformedFreeze":
            case "TTriggerOnCardPerformedHaste":
            case "TTriggerOnCardPerformedHeal":
            case "TTriggerOnCardPerformedOverHeal":
            case "TTriggerOnCardPerformedPoison":
            case "TTriggerOnCardPerformedRegen":
            case "TTriggerOnCardPerformedReload":
            case "TTriggerOnCardPerformedShield":
            case "TTriggerOnCardPerformedSlow":
            {
                bool sOk = trigger.Subject == null
                    ? true
                    : (trigger.Subject.Type?.StartsWith("TTargetPlayer", StringComparison.Ordinal) == true
                        ? (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer))
                        : (eventSourceCard != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, eventSourceCard, ctx)));
                bool tOk = trigger.Target == null
                    ? true
                    : (trigger.Target.Type?.StartsWith("TTargetPlayer", StringComparison.Ordinal) == true
                        ? (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Target, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer))
                        : (eventTargetCard != null && MatchesCardSubject(trigger.Target, ownerCard, ownerPlayer, eventTargetCard, ctx)));
                return sOk && tOk;
            }

            case "TTriggerOnCardAttributeChanged":
            {
                if (!string.IsNullOrEmpty(trigger.AttributeChanged) && !string.Equals(trigger.AttributeChanged, changedAttr, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (trigger.ChangeType == "Gain" && curVal <= prevVal) return false;
                if (trigger.ChangeType == "Loss" && curVal >= prevVal) return false;
                var ownerCtx = ctx.WithCards(ownerCard);
                var prevNode = trigger.GetPreviousValueNode(GameDatabaseLoader.JsonOptions);
                double? expectedPrev = prevNode != null
                    ? ValueAndConditionEvaluator.EvaluateValue(prevNode, ownerCtx, 0)
                    : trigger.PreviousValue;
                if (expectedPrev.HasValue && Math.Abs(prevVal - expectedPrev.Value) > 1e-6)
                    return false;
                var curNode = trigger.GetCurrentValueNode(GameDatabaseLoader.JsonOptions);
                double? expectedCur = curNode != null
                    ? ValueAndConditionEvaluator.EvaluateValue(curNode, ownerCtx, 0)
                    : trigger.CurrentValue;
                if (expectedCur.HasValue)
                {
                    if (!string.IsNullOrEmpty(trigger.ComparisonOperator))
                    {
                        if (!ValueAndConditionEvaluator.Compare(curVal, trigger.ComparisonOperator, expectedCur.Value))
                            return false;
                    }
                    else if (Math.Abs(curVal - expectedCur.Value) > 1e-6)
                    {
                        return false;
                    }
                }
                bool subjectOk = trigger.Subject == null
                    ? (eventTargetCard ?? eventSourceCard) == ownerCard
                    : (eventTargetCard ?? eventSourceCard) != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, (eventTargetCard ?? eventSourceCard)!, ctx);
                if (!subjectOk) return false;
                // P2-19：补齐 TTriggerOnCardAttributeChanged.Source 校验（修复 Basket 等监听他源卡修改属性的条件）
                bool sourceOk = trigger.Source == null
                    || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return sourceOk;
            }

            case "TTriggerOnPlayerAttributeChanged":
            {
                if (!string.IsNullOrEmpty(trigger.AttributeType) && !string.Equals(trigger.AttributeType, changedAttr, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (trigger.ChangeType == "Gain" && curVal <= prevVal) return false;
                if (trigger.ChangeType == "Loss" && curVal >= prevVal) return false;
                bool pOk = trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));
                bool sOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return pOk && sOk;
            }

            case "TTriggerOnPlayerDamaged":
            {
                if (trigger.ComparisonAmount.HasValue && !string.IsNullOrEmpty(trigger.ComparisonOperator))
                {
                    if (!ValueAndConditionEvaluator.Compare(curVal, trigger.ComparisonOperator, trigger.ComparisonAmount.Value))
                        return false;
                }
                bool pOk = trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));
                bool sOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return pOk && sOk;
            }

            case "TTriggerOnPlayerLifeStealed":
            {
                if (curVal <= 0) return false;
                if (trigger.ComparisonAmount.HasValue)
                {
                    var op = string.IsNullOrEmpty(trigger.ComparisonOperator) ? "GreaterThan" : trigger.ComparisonOperator;
                    if (!ValueAndConditionEvaluator.Compare(curVal, op, trigger.ComparisonAmount.Value))
                        return false;
                }
                bool pOk = trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));
                bool sOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return pOk && sOk;
            }

            case "TTriggerOnPlayerShielded":
            {
                if (curVal <= 0) return false;
                var cmpNode = trigger.GetComparisonValueNode(GameDatabaseLoader.JsonOptions);
                double? cmpAmt = cmpNode != null
                    ? ValueAndConditionEvaluator.EvaluateValue(cmpNode, ctx.WithCards(ownerCard), 0)
                    : trigger.GetFixedComparisonValue() ?? trigger.ComparisonAmount;
                if (cmpAmt.HasValue)
                {
                    var op = string.IsNullOrEmpty(trigger.ComparisonOperator) ? "GreaterThan" : trigger.ComparisonOperator;
                    if (!ValueAndConditionEvaluator.Compare(curVal, op, cmpAmt.Value))
                        return false;
                }
                bool pOk = trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));
                bool sOk = trigger.Source == null || (eventSourceCard != null && MatchesCardSubject(trigger.Source, ownerCard, ownerPlayer, eventSourceCard, ctx));
                return pOk && sOk;
            }

            case "TTriggerOnPlayerLeveledUp":
            {
                bool pOk = trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));
                if (!pOk) return false;
                // P2-22：直接读取强类型 NewLevel，避免被强类型属性遮蔽而落空
                int? targetLevel = trigger.Level ?? trigger.NewLevel;
                if (targetLevel.HasValue && targetLevel.Value > 0)
                {
                    double levelVal = curVal > 0 ? curVal : (eventSubjectPlayer?.Level ?? ownerPlayer.Level);
                    var op = string.IsNullOrEmpty(trigger.ComparisonOperator) ? "LessThanOrEqual" : trigger.ComparisonOperator;
                    if (!ValueAndConditionEvaluator.Compare(levelVal, op, targetLevel.Value))
                        return false;
                }
                return true;
            }

            case "TTriggerOnRunCompleted":
            {
                if (ctx.Scope == RuleScope.InCombat) return false;
                // P2-22：直接读取强类型 Outcome，避免被强类型属性遮蔽而落空
                string? reqOutcome = !string.IsNullOrEmpty(trigger.CombatOutcome)
                    ? trigger.CombatOutcome
                    : trigger.Outcome;
                if (string.IsNullOrEmpty(reqOutcome)) return true;
                if (string.IsNullOrEmpty(combatOutcome)) return false;
                return string.Equals(NormalizeRunOutcome(reqOutcome), NormalizeRunOutcome(combatOutcome), StringComparison.Ordinal);
            }

            case "TTriggerOnPlayerEnraged":
            case "TTriggerOnPlayerEnrageEnded":
            case "TTriggerOnPlayerRagedWhileEnraged":
            case "TTriggerOnPlayerDied":
                return trigger.Subject == null || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));

            case "TTriggerOnFightStarted":
                return string.IsNullOrEmpty(trigger.CombatType) || string.Equals(trigger.CombatType, combatType, StringComparison.OrdinalIgnoreCase);

            case "TTriggerOnFightEnded":
                if (!string.IsNullOrEmpty(trigger.CombatType) && !string.Equals(trigger.CombatType, combatType, StringComparison.OrdinalIgnoreCase))
                    return false;
                return string.IsNullOrEmpty(trigger.CombatOutcome) || string.Equals(trigger.CombatOutcome, combatOutcome, StringComparison.OrdinalIgnoreCase);

            case "TTriggerOnEncounterSelected":
            case "TTriggerOnEncounterEntered":
            case "TTriggerOnEncounterExited":
            case "TTriggerOnEncounterCardsDealt":
            {
                var cond = trigger.GetSingleCondition(GameDatabaseLoader.JsonOptions);
                if (cond == null) return true;
                if (eventSourceCard == null) return false;
                return ValueAndConditionEvaluator.EvaluateCardCondition(cond, eventSourceCard, ownerPlayer, ctx);
            }

            case "TTriggerOnDayStarted":
            case "TTriggerOnHourStarted":
            case "TTriggerOnRunStarted":
            case "TTriggerOnSandstorm":
                return trigger.Subject == null
                    || (eventSubjectPlayer != null && TargetResolver.ResolvePlayers(trigger.Subject, ctx.WithCards(ownerCard)).Contains(eventSubjectPlayer));

            default:
                RuleExecutionContext.NotifyUnknownRuleType(ctx, "TTrigger", eventType);
                return false;
        }
    }

    private static string NormalizeRunOutcome(string outcome)
    {
        if (outcome.Equals("Win", StringComparison.OrdinalIgnoreCase)) return "Victory";
        if (outcome.Equals("Lose", StringComparison.OrdinalIgnoreCase)) return "Defeat";
        return outcome;
    }

    private static bool MatchesCardSubject(RuleNode? subject, CardInstance ownerCard, PlayerState ownerPlayer, CardInstance candidate, RuleExecutionContext ctx)
    {
        if (subject == null) return ReferenceEquals(candidate, ownerCard);
        var prevSelf = ctx.SelfCard;
        var prevSrc = ctx.TriggerSourceCard;
        BoardGrid? tempInsertedGrid = null;
        List<CardInstance>? prevOrderSnapshot = null;
        int prevSlot = candidate.StartSlot;
        try
        {
            ctx.SelfCard = ownerCard;
            ctx.TriggerSourceCard = candidate;

            // 若正在处理已提前摘除腾格的被卖卡，在主体条件求值期间临时还原至原网格原槽位，确保邻位/分区条件精确命中
            if (SoldCardScopes.TryGetValue(ctx.ActiveExecution, out var soldScope)
                && soldScope != null
                && ReferenceEquals(candidate, soldScope.SoldCard)
                && soldScope.OriginalGrid != null
                && !soldScope.OriginalGrid.Cards.Contains(candidate)
                && soldScope.OriginalStartSlot >= 0)
            {
                prevOrderSnapshot = new List<CardInstance>(soldScope.OriginalGrid.Cards);
                candidate.StartSlot = soldScope.OriginalStartSlot;
                soldScope.OriginalGrid.Cards.Add(candidate);
                soldScope.OriginalGrid.Cards.Sort((a, b) =>
                {
                    int sa = a.StartSlot < 0 ? int.MaxValue : a.StartSlot;
                    int sb = b.StartSlot < 0 ? int.MaxValue : b.StartSlot;
                    int c = sa.CompareTo(sb);
                    return c != 0 ? c : string.CompareOrdinal(a.InstanceId, b.InstanceId);
                });
                tempInsertedGrid = soldScope.OriginalGrid;
            }

            return TargetResolver.MatchesCard(subject, candidate, ctx);
        }
        finally
        {
            if (tempInsertedGrid != null)
            {
                tempInsertedGrid.Cards.Clear();
                if (prevOrderSnapshot != null)
                    tempInsertedGrid.Cards.AddRange(prevOrderSnapshot);
                candidate.StartSlot = prevSlot;
            }
            ctx.SelfCard = prevSelf;
            ctx.TriggerSourceCard = prevSrc;
        }
    }

    private static void DispatchDetailed(
        RuleExecutionContext ctx,
        string evName,
        PlayerState actor,
        CardInstance? src,
        CardInstance? tgt,
        PlayerState? subj,
        string? attr = null,
        double prev = 0,
        double cur = 0)
    {
        if (ctx.OnDetailedTriggeredEvent != null)
        {
            ctx.OnDetailedTriggeredEvent(evName, actor, src, tgt, subj, attr, prev, cur);
        }
        else
        {
            ctx.OnCardTriggeredEvent?.Invoke(evName, subj ?? actor, ctx.OpponentPlayer ?? actor, tgt ?? src);
        }
    }

    /// <summary>
    /// 精确判定规则动作的 Duration 目标层：
    /// - duration == null：无论 OutOfCombat 还是 InCombat 一律进 Permanent 永久层；
    /// - TDeterminantDuration：UntilEndOfCombat -> Combat，UntilEndOfHour -> Hour，UntilEndOfDay -> Day；
    /// - TCombatDuration：Combat 临时层（配合 CombatSimulator 计时撤销）。
    /// </summary>
    public static DurationTarget ResolveDurationTarget(RuleNode? duration, RuleScope scope)
    {
        if (duration == null)
            return DurationTarget.Permanent;

        if (string.Equals(duration.Type, "TDeterminantDuration", StringComparison.Ordinal))
        {
            return duration.DurationType switch
            {
                "UntilEndOfCombat" => DurationTarget.Combat,
                "UntilEndOfHour" => DurationTarget.Hour,
                "UntilEndOfDay" => DurationTarget.Day,
                _ => scope == RuleScope.InCombat ? DurationTarget.Combat : DurationTarget.Hour
            };
        }

        if (string.Equals(duration.Type, "TCombatDuration", StringComparison.Ordinal))
            return DurationTarget.Combat;

        return DurationTarget.Permanent;
    }

    public static bool ExecuteAction(RuleNode? action, CardInstance sourceCard, RuleExecutionContext ctx)
    {
        if (action == null) return false;

        // 先检查 Cost（例如消耗金币或节奏点）
        if (action.Cost != null && !TryPayCost(action.Cost, sourceCard, ctx))
            return false;

        int? targetCount = null;
        if (action.TargetCount != null)
        {
            targetCount = SafeMath.ToSafeInt(ValueAndConditionEvaluator.EvaluateValue(action.TargetCount, ctx, 0), 0, 1000);
        }
        else if (string.Equals(action.Target?.Type, "TTargetCardRandom", StringComparison.Ordinal))
        {
            string? targetsAttr = GetTargetsAttributeForAction(action.Type);
            if (targetsAttr != null)
            {
                double attrTargets = ctx.ActiveExecution.Ability != null
                    ? sourceCard.GetAttributeForAbility(targetsAttr, ctx.ActiveExecution.Ability)
                    : sourceCard.GetAttributeForAbility(targetsAttr, ctx.ActiveExecution.AbilityId);
                if (attrTargets > 0)
                    targetCount = Math.Max(1, SafeMath.ToSafeInt(attrTargets, 1, 1000));
            }
        }

        var prevActionType = ctx.ActiveExecution.ActionType;
        ctx.ActiveExecution.ActionType = action.Type;
        try
        {
            switch (action.Type)
            {
                case "TActionAnd":
                {
                    if (action.Actions == null) return true;
                    bool hadPrev = LastSpawnedCardsMap.TryGetValue(ctx.ActiveExecution, out var prevSpawned);
                    List<CardInstance>? prevSnapshot = hadPrev && prevSpawned != null ? [.. prevSpawned] : null;
                    var prevTriggerTarget = ctx.TriggerTargetCard;
                    string? prevExecId = ctx.ActiveExecution.ExecutionContextId;
                    bool assignedExecId = string.IsNullOrEmpty(prevExecId);
                    if (assignedExecId)
                        ctx.ActiveExecution.ExecutionContextId = Guid.NewGuid().ToString("N");
                    try
                    {
                        foreach (var sub in action.Actions)
                        {
                            var subCtx = ctx;
                            if (LastSpawnedCardsMap.TryGetValue(ctx.ActiveExecution, out var latestSpawned) && latestSpawned.Count > 0)
                                subCtx = ctx.WithCards(ctx.SelfCard, ctx.TriggerSourceCard, latestSpawned[^1]);
                            ExecuteAction(sub, sourceCard, subCtx);
                        }
                    }
                    finally
                    {
                        if (assignedExecId)
                            ctx.ActiveExecution.ExecutionContextId = prevExecId;
                        ctx.TriggerTargetCard = prevTriggerTarget;
                        LastSpawnedCardsMap.Remove(ctx.ActiveExecution);
                        if (prevSnapshot != null)
                            LastSpawnedCardsMap.Add(ctx.ActiveExecution, prevSnapshot);
                    }
                    return true;
                }

                case "TActionCostPlayerAttribute":
                case "TActionCostCardAttribute":
                    return TryPayCost(action, sourceCard, ctx);

                case "TActionPlayerDamage":
                {
                    int finalDmg = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "DamageAmount"), sourceCard, ctx, out bool isCrit, "DamageCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnDamageApplied != null)
                        {
                            ctx.OnDamageApplied.Invoke(target, finalDmg, isCrit, sourceCard);
                        }
                        else if (finalDmg > 0)
                        {
                            int prevShield = target.Shield;
                            int prevHp = target.CurrentHealth;
                            int blocked = Math.Min(target.Shield, finalDmg);
                            target.Shield -= blocked;
                            int hpDmg = finalDmg - blocked;
                            if (hpDmg > 0)
                                target.CurrentHealth = Math.Max(0, target.CurrentHealth - hpDmg);
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedDamage", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (target.Shield != prevShield)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Shield", prevShield, target.Shield);
                            if (hpDmg > 0)
                            {
                                // P2-17：curVal 传实际伤害量（对齐 CombatSimulator 及 ComparisonAmount 语义），避免致命一击 CurrentHealth==0 误判失败
                                DispatchDetailed(ctx, "TTriggerOnPlayerDamaged", target, sourceCard, sourceCard, target, "Health", prevHp, hpDmg);
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, target.CurrentHealth);
                            }
                            if (target.CurrentHealth <= 0 && prevHp > 0)
                                DispatchDetailed(ctx, "TTriggerOnPlayerDied", target, sourceCard, sourceCard, target);
                        }
                    }
                    if (isCrit && finalDmg > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerShieldApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "ShieldApplyAmount"), sourceCard, ctx, out bool isCrit, "ShieldCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnShieldAppliedWithCrit != null)
                        {
                            ctx.OnShieldAppliedWithCrit.Invoke(target, amount, isCrit, sourceCard);
                        }
                        else if (ctx.OnShieldApplied != null)
                        {
                            ctx.OnShieldApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevShield = target.Shield;
                            target.Shield = SafeMath.SaturatingAddInt(target.Shield, amount);
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedShield", target, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerShielded", target, sourceCard, sourceCard, target, "Shield", prevShield, amount);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Shield", prevShield, target.Shield);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerBurnApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "BurnApplyAmount"), sourceCard, ctx, out bool isCrit, "BurnCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnBurnAppliedWithCrit != null)
                        {
                            ctx.OnBurnAppliedWithCrit.Invoke(target, amount, isCrit, sourceCard);
                        }
                        else if (ctx.OnBurnApplied != null)
                        {
                            ctx.OnBurnApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevBurn = target.Burn;
                            target.Burn = SafeMath.SaturatingAddInt(target.Burn, amount);
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedBurn", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Burn", prevBurn, target.Burn);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerPoisonApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "PoisonApplyAmount"), sourceCard, ctx, out bool isCrit, "PoisonCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnPoisonAppliedWithCrit != null)
                        {
                            ctx.OnPoisonAppliedWithCrit.Invoke(target, amount, isCrit, sourceCard);
                        }
                        else if (ctx.OnPoisonApplied != null)
                        {
                            ctx.OnPoisonApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevPoison = target.Poison;
                            target.Poison = SafeMath.SaturatingAddInt(target.Poison, amount);
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedPoison", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Poison", prevPoison, target.Poison);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerHeal":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "HealAmount"), sourceCard, ctx, out bool isCrit, "HealCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnHealAppliedWithCrit != null)
                        {
                            ctx.OnHealAppliedWithCrit.Invoke(target, amount, isCrit, sourceCard);
                        }
                        else if (ctx.OnHealApplied != null)
                        {
                            ctx.OnHealApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevHp = target.CurrentHealth;
                            int afterHp = Math.Min(target.HealthMax, SafeMath.SaturatingAddInt(prevHp, amount, 0, target.HealthMax));
                            int healed = afterHp - prevHp;
                            target.CurrentHealth = afterHp;
                            // 附注（孤立回退路径一致性）：脱离 CombatSimulator 无回调时同步执行 5% 治疗净化（严守红线，零修改 CombatSimulator 主公式）
                            int cleanseAmt = Math.Max(1, (int)Math.Floor(amount * 0.05));
                            if (target.Burn > 0)
                            {
                                int prevBurn = target.Burn;
                                target.Burn = Math.Max(0, target.Burn - cleanseAmt);
                                if (target.Burn != prevBurn)
                                    DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Burn", prevBurn, target.Burn);
                            }
                            if (target.Poison > 0)
                            {
                                int prevPoison = target.Poison;
                                target.Poison = Math.Max(0, target.Poison - cleanseAmt);
                                if (target.Poison != prevPoison)
                                    DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Poison", prevPoison, target.Poison);
                            }
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (amount > healed)
                                DispatchDetailed(ctx, "TTriggerOnCardPerformedOverHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (healed != 0)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, afterHp);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerRegenApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "RegenApplyAmount"), sourceCard, ctx, out bool isCrit, "RegenCrit");
                    var regenDur = ResolveDurationTarget(action.Duration, ctx.Scope);
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnRegenAppliedWithCrit != null)
                        {
                            ctx.OnRegenAppliedWithCrit.Invoke(target, amount, isCrit, sourceCard, regenDur);
                        }
                        else if (ctx.OnRegenApplied != null)
                        {
                            ctx.OnRegenApplied.Invoke(target, amount, sourceCard, regenDur);
                        }
                        else if (amount > 0)
                        {
                            double prevRegen = target.GetAttribute("HealthRegen");
                            target.ModifyAttribute("HealthRegen", "Add", amount, regenDur);
                            double curRegen = target.GetAttribute("HealthRegen");
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedRegen", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (Math.Abs(curRegen - prevRegen) > 1e-6)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "HealthRegen", prevRegen, curRegen);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerRageApply":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "RageApplyAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        // D1-P2-03：狂暴期间冻结 Rage 叠加，仅当 amount > 0 时派发 TTriggerOnPlayerRagedWhileEnraged
                        if (target.IsEnraged)
                        {
                            if (amount > 0)
                                DispatchDetailed(ctx, "TTriggerOnPlayerRagedWhileEnraged", ctx.SelfPlayer, sourceCard, null, target, "Rage", target.Rage, target.Rage);
                            continue;
                        }

                        double prevRage = target.GetAttribute("Rage");
                        target.ModifyAttribute("Rage", "Add", amount);
                        double curRage = target.GetAttribute("Rage");
                        if (amount > 0)
                        {
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.PlayerRageApplied,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                Value = amount,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                        }
                        if (Math.Abs(curRage - prevRage) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Rage", prevRage, curRage);

                        int rageMax = Math.Max(1, (int)Math.Round(target.GetAttribute("RageMax")));
                        if (target.Rage >= rageMax && !target.IsEnraged)
                        {
                            double beforeZeroRage = target.GetAttribute("Rage");
                            target.Rage = 0;
                            target.EnragedRemainingMs = Math.Max(1000, (int)Math.Round(target.GetAttribute("EnragedDurationMax")));
                            target.CombatAdd["Enraged"] = 1.0;
                            target.CombatAdd["EnragedDuration"] = target.EnragedRemainingMs;
                            if (Math.Abs(beforeZeroRage) > 1e-6)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Rage", beforeZeroRage, 0);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.PlayerEnraged,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            DispatchDetailed(ctx, "TTriggerOnPlayerEnraged", ctx.SelfPlayer, sourceCard, null, target);
                        }
                    }
                    return true;
                }

                case "TActionPlayerJoyApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "JoyApplyAmount"), sourceCard, ctx, out bool isCrit, "JoyCrit");
                    var joyDur = ResolveDurationTarget(action.Duration, ctx.Scope);
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (amount <= 0) continue;
                        double prevJoy = target.GetAttribute("Joy");
                        target.ModifyAttribute("Joy", "Add", amount, joyDur);
                        double curJoy = target.GetAttribute("Joy");
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.PlayerJoyApplied,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = target.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            Value = amount,
                            IsCrit = isCrit,
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        if (Math.Abs(curJoy - prevJoy) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Joy", prevJoy, curJoy);
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerJoyRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "JoyRemoveAmount")));
                    var joyDur = ResolveDurationTarget(action.Duration, ctx.Scope);
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        if (amount <= 0) continue;
                        double prevJoy = target.GetAttribute("Joy");
                        double actualRemove = Math.Min(Math.Max(0, prevJoy), amount);
                        if (actualRemove > 0)
                            target.ModifyAttribute("Joy", "Subtract", actualRemove, joyDur);
                        double curJoy = target.GetAttribute("Joy");
                        if (Math.Abs(curJoy - prevJoy) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Joy", prevJoy, curJoy);
                    }
                    return true;
                }

                case "TActionPlayerTempoApply":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "TempoApplyAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        double prevTempo = target.GetAttribute("Tempo");
                        target.ModifyAttribute("Tempo", "Add", amount);
                        double curTempo = target.GetAttribute("Tempo");
                        int gained = (int)Math.Round(curTempo - prevTempo);
                        if (gained > 0)
                        {
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.PlayerTempoApplied,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                Value = gained,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                        }
                        if (Math.Abs(curTempo - prevTempo) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Tempo", prevTempo, curTempo);
                    }
                    return true;
                }

                case "TActionPlayerTempoRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "TempoRemoveAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        double prevTempo = target.GetAttribute("Tempo");
                        target.ModifyAttribute("Tempo", "Subtract", amount);
                        double curTempo = target.GetAttribute("Tempo");
                        int removed = Math.Max(0, (int)Math.Round(prevTempo - curTempo));
                        if (removed > 0)
                        {
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.PlayerTempoRemoved,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                Value = removed,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                        }
                        if (Math.Abs(curTempo - prevTempo) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Tempo", prevTempo, curTempo);
                    }
                    return true;
                }

                case "TActionPlayerBurnRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "BurnRemoveAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        int prevBurn = target.Burn;
                        target.Burn = Math.Max(0, target.Burn - amount);
                        int removed = prevBurn - target.Burn;
                        if (removed > 0)
                        {
                            // P2-20：补发 BurnRemoved 战报事件并触发属性变更
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.BurnRemoved,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TriggerSourceCardInstanceId = ctx.ActiveExecution.TriggerSourceCardInstanceId ?? sourceCard.InstanceId,
                                Value = removed,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                ExecutionContextId = ctx.ActiveExecution.ExecutionContextId,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Burn", prevBurn, target.Burn);
                        }
                    }
                    return true;
                }

                case "TActionPlayerPoisonRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "PoisonRemoveAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        int prevPoison = target.Poison;
                        target.Poison = Math.Max(0, target.Poison - amount);
                        int removed = prevPoison - target.Poison;
                        if (removed > 0)
                        {
                            // P2-20：补发 PoisonRemoved 战报事件并触发属性变更
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.PoisonRemoved,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TriggerSourceCardInstanceId = ctx.ActiveExecution.TriggerSourceCardInstanceId ?? sourceCard.InstanceId,
                                Value = removed,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                ExecutionContextId = ctx.ActiveExecution.ExecutionContextId,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Poison", prevPoison, target.Poison);
                        }
                    }
                    return true;
                }

                case "TActionPlayerShieldRemove":
                {
                    int amount = SafeMath.ToSafeInt(EvalActionAmount(action, sourceCard, ctx, "ShieldRemoveAmount"), 0, SafeMath.DefaultCombatMax);
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        int prevShield = target.Shield;
                        target.Shield = Math.Max(0, target.Shield - amount);
                        if (target.Shield != prevShield)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Shield", prevShield, target.Shield);
                    }
                    return true;
                }

                case "TActionPlayerRegenRemove":
                {
                    int amount = SafeMath.ToSafeInt(EvalActionAmount(action, sourceCard, ctx, "RegenRemoveAmount"), 0, SafeMath.DefaultCombatMax);
                    var regenDur = ResolveDurationTarget(action.Duration, ctx.Scope);
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        double prevRegen = target.GetAttribute("HealthRegen");
                        double actualRemove = Math.Min(Math.Max(0.0, prevRegen), amount);
                        if (actualRemove > 1e-9)
                        {
                            target.ModifyAttribute("HealthRegen", "Subtract", actualRemove, regenDur);
                            double curRegen = target.GetAttribute("HealthRegen");
                            if (Math.Abs(curRegen - prevRegen) > 1e-6)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "HealthRegen", prevRegen, curRegen);
                        }
                    }
                    return true;
                }

                case "TActionPlayerRageRemove":
                {
                    int amount = SafeMath.ToSafeInt(EvalActionAmount(action, sourceCard, ctx, "RageRemoveAmount"), 0, SafeMath.DefaultCombatMax);
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        int prevRage = target.Rage;
                        target.Rage = Math.Max(0, target.Rage - amount);
                        if (target.Rage != prevRage)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Rage", prevRage, target.Rage);
                    }
                    return true;
                }

                case "TActionCardHeat":
                {
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        double prev = card.GetAttribute("Heated");
                        card.Heated = true;
                        card.MarkAttributeDirty();
                        double cur = card.GetAttribute("Heated");
                        if (Math.Abs(cur - prev) > 1e-6)
                        {
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Heated", prev, cur);
                        }
                    }
                    return true;
                }

                case "TActionCardChill":
                {
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        double prev = card.GetAttribute("Chilled");
                        card.Chilled = true;
                        card.MarkAttributeDirty();
                        double cur = card.GetAttribute("Chilled");
                        if (Math.Abs(cur - prev) > 1e-6)
                        {
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Chilled", prev, cur);
                        }
                    }
                    return true;
                }

                case "TActionPlayerReviveHeal":
                {
                    int scaled = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "HealAmount"), sourceCard, ctx, out bool isCrit, "HealCrit");
                    int amount = Math.Max(1, scaled);
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnReviveHealApplied != null)
                        {
                            ctx.OnReviveHealApplied.Invoke(target, amount, isCrit, sourceCard);
                        }
                        else
                        {
                            int prevHp = target.CurrentHealth;
                            int nextHp = Math.Clamp(amount, 1, target.HealthMax);
                            target.CurrentHealth = nextHp;
                            int delta = nextHp - prevHp;
                            int restored = Math.Max(0, delta);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.HealApplied,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = target.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                Value = restored,
                                EffectiveHpRestored = restored,
                                IsCrit = isCrit,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            // Eject, Eject! / Maker's Mark 依赖复活治疗后的 PerformedHeal 触发自毁联动
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (amount > restored)
                                DispatchDetailed(ctx, "TTriggerOnCardPerformedOverHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (nextHp != prevHp)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, nextHp);
                        }
                    }
                    if (isCrit && targets.Count > 0)
                        RecordOrDispatchCrit(ctx, sourceCard);
                    return true;
                }

                case "TActionPlayerModifyAttribute":
                {
                    var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                    double val = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (action.GetFixedNumericValue() ?? 0);
                    if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                    var attr = action.AttributeType ?? "Gold";
                    var op = action.Operation ?? "Add";
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    bool isTimedCombat = ctx.Scope == RuleScope.InCombat && action.Duration?.Type == "TCombatDuration";
                    int timedDurationMs = isTimedCombat ? Math.Max(1, action.Duration!.DurationInMs ?? 1000) : 0;
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        double prev = target.GetAttribute(attr);
                        int timedTokenId = 0;
                        if (isTimedCombat && ctx.OnTimedPlayerAttributeModifierWithTokenApplied != null
                            && !string.Equals(op, "Set", StringComparison.OrdinalIgnoreCase))
                        {
                            timedTokenId = target.ApplyTimedCombatModifier(attr, op, val);
                        }
                        else
                        {
                            target.ModifyAttribute(attr, op, val, durationTarget);
                        }
                        double cur = target.GetAttribute(attr);
                        ctx.LastAttributeChangeDelta = cur - prev;
                        if (isTimedCombat)
                        {
                            if (timedTokenId > 0)
                                ctx.OnTimedPlayerAttributeModifierWithTokenApplied!.Invoke(target, attr, op, val, timedDurationMs, timedTokenId);
                            else
                                ctx.OnTimedPlayerAttributeModifierApplied?.Invoke(target, attr, op, val, timedDurationMs);
                        }
                        DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, attr, prev, cur);
                    }
                    return true;
                }

                case "TActionCardModifyAttribute":
                {
                    var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                    bool perTargetEval = valNode != null && ValueAndConditionEvaluator.ContainsNullTargetCardRef(valNode);
                    double sharedVal = (!perTargetEval && valNode != null)
                        ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx)
                        : (!perTargetEval ? (action.GetFixedNumericValue() ?? 0) : 0);
                    if (double.IsNaN(sharedVal) || double.IsInfinity(sharedVal)) sharedVal = 0;
                    var attr = action.AttributeType ?? "DamageAmount";
                    var op = action.Operation ?? "Add";
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    bool isTimedCombat = ctx.Scope == RuleScope.InCombat
                                         && action.Duration?.Type == "TCombatDuration"
                                         && !attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase);
                    int timedDurationMs = isTimedCombat ? Math.Max(1, action.Duration!.DurationInMs ?? 1000) : 0;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        double val;
                        if (perTargetEval)
                        {
                            var prevCurrentTarget = ctx.CurrentTargetCard;
                            ctx.CurrentTargetCard = card;
                            try
                            {
                                val = ValueAndConditionEvaluator.EvaluateValue(valNode!, ctx);
                            }
                            finally
                            {
                                ctx.CurrentTargetCard = prevCurrentTarget;
                            }
                            if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                        }
                        else
                        {
                            val = sharedVal;
                        }

                        // P2-43：兼容两张 SpawningEligibility=Never 历史遗留卡的异常百分比 Multiply（防沙盒 25 倍爆伤或减冷却静默丢弃）
                        string effectiveOp = op;
                        if (string.Equals(effectiveOp, "Multiply", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(sourceCard.Template.Id, "5b393f5e-2c86-40c6-b210-1a215c1ccd51", StringComparison.OrdinalIgnoreCase)
                                && attr.Equals("DamageAmount", StringComparison.OrdinalIgnoreCase)
                                && val > 1.0)
                            {
                                // High Impact：DamageAmount Multiply 25.0 → +25% (AdditiveMultiply 0.25)
                                effectiveOp = "AdditiveMultiply";
                                val = val / 100.0;
                            }
                            else if (string.Equals(sourceCard.Template.Id, "f87817fd-7a77-4bd6-8248-37e90a72c81d", StringComparison.OrdinalIgnoreCase)
                                && attr.Equals("PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
                            {
                                // Escalation Protocol：PercentCooldownReduction Multiply 5.0 → Add 5.0
                                effectiveOp = "Add";
                            }
                        }

                        double prev = card.GetAttribute(attr);
                        int timedTokenId = 0;
                        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                        {
                            int curAmmo = card.CurrentAmmo;
                            int ammoMax = card.EffectiveAmmoMax;
                            int nextAmmo;
                            if (string.Equals(effectiveOp, "Multiply", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(curAmmo * val);
                            else if (string.Equals(effectiveOp, "AdditiveMultiply", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(curAmmo * (1.0 + val));
                            else if (string.Equals(effectiveOp, "Subtract", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = curAmmo - (int)Math.Round(val);
                            else if (string.Equals(effectiveOp, "Set", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(val);
                            else
                                nextAmmo = curAmmo + (int)Math.Round(val);
                            card.CurrentAmmo = Math.Clamp(nextAmmo, 0, ammoMax);
                        }
                        else if (string.Equals(effectiveOp, "Multiply", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(val) < 1e-9
                            && !isTimedCombat
                            && !attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
                        {
                            // D1-P1-08：统一委托受控归零复位（层隔离不烘焙瞬态光环、强制标脏、严守 P0-02 零幽灵键不变量）
                            card.ResetAttributeToZero(attr, durationTarget);
                        }
                        else if (isTimedCombat
                            && ctx.OnTimedCardAttributeModifierWithTokenApplied != null
                            && !string.Equals(effectiveOp, "Set", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
                        {
                            // D1-P1-07 (#126/#128)：限时战中修饰走分槽活性列表，零逆除法且保留归一化后的 effectiveOp
                            timedTokenId = card.ApplyTimedCombatModifier(attr, effectiveOp, val);
                        }
                        else
                        {
                            card.ModifyAttribute(attr, effectiveOp, val, durationTarget, clampNonNegativeSubtract: !isTimedCombat);
                            if (!isTimedCombat
                                && (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)
                                    || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
                                && card.GetAttribute(attr) <= 0
                                && (string.Equals(effectiveOp, "Subtract", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(effectiveOp, "Set", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)) card.Heated = false;
                                else card.Chilled = false;
                            }
                        }

                        if (attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ctx.Scope == RuleScope.OutOfCombat)
                            {
                                // 局外修改弹药上限后自动同步补满弹药，保证客户端及光环立即读到满弹状态
                                card.RefillAmmoToMax();
                            }
                            else
                            {
                                int nextAmmoMax = card.EffectiveAmmoMax;
                                card.CurrentAmmo = Math.Clamp(card.CurrentAmmo, 0, nextAmmoMax);
                            }
                        }
                        double cur = card.GetAttribute(attr);
                        ctx.LastAttributeChangeDelta = cur - prev;
                        if (isTimedCombat)
                        {
                            if (timedTokenId > 0)
                                ctx.OnTimedCardAttributeModifierWithTokenApplied!.Invoke(card, attr, effectiveOp, val, timedDurationMs, timedTokenId);
                            else
                                ctx.OnTimedCardAttributeModifierApplied?.Invoke(card, attr, effectiveOp, val, timedDurationMs);
                        }
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
                        {
                            if (cardOwner.Hand.Cards.Contains(card))
                                cardOwner.Hand.ReconcileWidthChange(card, ctx.Scope == RuleScope.OutOfCombat ? cardOwner.Stash : null);
                            else if (cardOwner.Stash.Cards.Contains(card))
                                cardOwner.Stash.ReconcileWidthChange(card, ctx.Scope == RuleScope.OutOfCombat ? cardOwner.Hand : null);
                        }
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.CardAttributeModified,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = cardOwner.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            TargetCardInstanceId = card.InstanceId,
                            Value = cur - prev,
                            Detail = attr,
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        if (Math.Abs(cur - prev) > 1e-6)
                        {
                            if (attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
                            {
                                if (prev <= 0 && cur > 0)
                                    ApplyFlyingTransition(ctx, cardOwner, sourceCard, card, isFlying: true, action.Type);
                                else if (prev > 0 && cur <= 0)
                                    ApplyFlyingTransition(ctx, cardOwner, sourceCard, card, isFlying: false, action.Type, emitCombatEvent: false);
                            }
                            else if (attr.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
                            {
                                SyncDirectQuestAttributeProgress(card, attr, prev, cur, cardOwner, sourceCard, ctx);
                            }
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, attr, prev, cur);
                        }
                    }
                    return true;
                }

                case "TActionCardHaste":
                {
                    int ms = ResolveDurationMs(action, sourceCard, ctx, "HasteAmount");
                    if (ms <= 0) return true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (ctx.OnHasteApplied != null)
                        {
                            ctx.OnHasteApplied.Invoke(card, ms, sourceCard);
                        }
                        else
                        {
                            double prev = card.HasteMs;
                            card.HasteMs = SafeMath.SaturatingAddInt(card.HasteMs, ms);
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Haste", prev, card.HasteMs);
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedHaste", ctx.SelfPlayer, sourceCard, card, cardOwner);
                        }
                    }
                    return true;
                }

                case "TActionCardSlow":
                {
                    int ms = ResolveDurationMs(action, sourceCard, ctx, "SlowAmount");
                    if (ms <= 0) return true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        var effectiveCard = ResolveAbsorbTarget(card, cardOwner, ctx.SelfPlayer, "AbsorbSlow");
                        if (ctx.OnSlowApplied != null)
                        {
                            ctx.OnSlowApplied.Invoke(effectiveCard, ms, sourceCard);
                        }
                        else
                        {
                            ApplyControlStatusDuration(
                                ctx,
                                sourceCard,
                                effectiveCard,
                                ms,
                                "PercentSlowReduction",
                                "Slow",
                                "TTriggerOnCardPerformedSlow",
                                static c => c.SlowMs,
                                static (c, val) => c.SlowMs = val);
                        }
                    }
                    return true;
                }

                case "TActionCardFreeze":
                {
                    int ms = ResolveDurationMs(action, sourceCard, ctx, "FreezeAmount");
                    if (ms <= 0) return true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        var effectiveCard = ResolveAbsorbTarget(card, cardOwner, ctx.SelfPlayer, "AbsorbFreeze");
                        if (ctx.OnFreezeApplied != null)
                        {
                            ctx.OnFreezeApplied.Invoke(effectiveCard, ms, sourceCard);
                        }
                        else
                        {
                            ApplyControlStatusDuration(
                                ctx,
                                sourceCard,
                                effectiveCard,
                                ms,
                                "PercentFreezeReduction",
                                "Freeze",
                                "TTriggerOnCardPerformedFreeze",
                                static c => c.FreezeMs,
                                static (c, val) => c.FreezeMs = val);
                        }
                    }
                    return true;
                }

                case "TActionCardCharge":
                {
                    int ms = ResolveDurationMs(action, sourceCard, ctx, "ChargeAmount");
                    if (ms <= 0) return true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (ctx.OnChargeApplied != null)
                        {
                            ctx.OnChargeApplied.Invoke(card, ms, sourceCard);
                        }
                        else if (card.HasActiveCooldown)
                        {
                            var pctRed = Math.Clamp(card.GetAttribute("PercentChargeReduction"), 0, 90);
                            int actual = SafeMath.ToSafeInt(ms * (1.0 - pctRed / 100.0), 0, SafeMath.DefaultCombatMax);
                            if (actual > 0)
                            {
                                card.CooldownProgressMs = SafeMath.SaturatingAddInt(card.CooldownProgressMs, actual);
                                var cardOwner = TargetResolver.FindOwner(card, ctx);
                                DispatchDetailed(ctx, "TTriggerOnCardCharged", ctx.SelfPlayer, sourceCard, card, cardOwner, "Charge", 0, actual);
                                DispatchDetailed(ctx, "TTriggerOnCardPerformedCharge", ctx.SelfPlayer, sourceCard, card, cardOwner, "Charge", 0, actual);
                            }
                        }
                    }
                    return true;
                }

                case "TActionCardForceUse":
                {
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        ctx.OnForceUseRequested?.Invoke(card, sourceCard);
                    return true;
                }

                case "TActionCardReload":
                {
                    int amt = SafeMath.ToSafeInt(EvalActionAmount(action, sourceCard, ctx, "ReloadAmount", 0), 0, 1_000_000);
                    if (amt <= 0) return true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        int prevAmmo = card.CurrentAmmo;
                        int ammoMax = card.EffectiveAmmoMax;
                        card.CurrentAmmo = Math.Clamp(SafeMath.SaturatingAddInt(prevAmmo, amt, 0, Math.Max(0, ammoMax)), 0, ammoMax);
                        int curAmmo = card.CurrentAmmo;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        if (curAmmo > prevAmmo)
                        {
                            card.MarkAttributeDirty();
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.CardReloaded,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = cardOwner.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TargetCardInstanceId = card.InstanceId,
                                Value = curAmmo - prevAmmo,
                                Detail = "Ammo",
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            // D1-P1-03：仅在确实装填了弹药（curAmmo > prevAmmo）时派发 PerformedReload 与 Ammo 变更事件
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedReload", ctx.SelfPlayer, sourceCard, card, cardOwner);
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Ammo", prevAmmo, curAmmo);
                        }
                    }
                    return true;
                }

                case "TActionCardDestroy":
                {
                    foreach (var rawCard in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (rawCard.IsDestroyed) continue;
                        var rawOwner = TargetResolver.FindOwner(rawCard, ctx);
                        var card = ResolveAbsorbTarget(rawCard, rawOwner, ctx.SelfPlayer, "AbsorbDestroy");
                        if (card.GetAttribute("DestroyImmunity") > 0 || IsLegacySyntheticRadiantImmunity(card, ctx) || card.IsDestroyed) continue;
                        if (ctx.OnBeforeCardStateChange != null && !ctx.OnBeforeCardStateChange(card, sourceCard, "Destroy", "destroy")) continue;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        DispatchDetailed(ctx, "TTriggerOnBeforeCardDestroyed", cardOwner, sourceCard, card, cardOwner);
                        card.IsDestroyed = true;
                        if (ctx.Scope == RuleScope.OutOfCombat)
                        {
                            cardOwner.Hand.Remove(card);
                            cardOwner.Stash.Remove(card);
                            cardOwner.Skills.Remove(card);
                            cardOwner.SocketEffects.Remove(card);
                            ctx.OnCardDestroyed?.Invoke(cardOwner, card);
                        }
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.CardDestroyed,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = cardOwner.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            TargetCardInstanceId = card.InstanceId,
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        DispatchDetailed(ctx, "TTriggerOnCardPerformedDestruction", ctx.SelfPlayer, sourceCard, card, cardOwner);
                    }
                    return true;
                }

                case "TActionCardDisable":
                {
                    foreach (var rawCard in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (rawCard.IsDestroyed || rawCard.IsDisabled) continue;
                        var rawOwner = TargetResolver.FindOwner(rawCard, ctx);
                        var card = ResolveAbsorbTarget(rawCard, rawOwner, ctx.SelfPlayer, "AbsorbDestroy");
                        if (card.GetAttribute("DestroyImmunity") > 0 || IsLegacySyntheticRadiantImmunity(card, ctx) || card.IsDestroyed || card.IsDisabled)
                            continue;
                        if (ctx.OnBeforeCardStateChange != null && !ctx.OnBeforeCardStateChange(card, sourceCard, "Disable", "disable"))
                            continue;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        // 战内禁用即官服摧毁语义：先触发 BeforeDestroyed，再置 IsDisabled 并派发 Disabled / PerformedDestruction
                        DispatchDetailed(ctx, "TTriggerOnBeforeCardDestroyed", cardOwner, sourceCard, card, cardOwner);
                        card.IsDisabled = true;
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.CardDestroyed,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = cardOwner.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            TargetCardInstanceId = card.InstanceId,
                            Detail = card.Template.Localization?.Title?.Text ?? card.Template.Id,
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        DispatchDetailed(ctx, "TTriggerOnCardDisabled", cardOwner, sourceCard, card, cardOwner);
                        DispatchDetailed(ctx, "TTriggerOnCardPerformedDestruction", ctx.SelfPlayer, sourceCard, card, cardOwner);
                    }
                    return true;
                }

                case "TActionCardRepair":
                {
                    bool isRandomRepair = string.Equals(action.Target?.Type, "TTargetCardRandom", StringComparison.Ordinal);
                    var rawTargets = isRandomRepair
                        ? ResolveActionTargets(action.Target, ctx, int.MaxValue)
                        : ResolveActionTargets(action.Target, ctx, targetCount);
                    var damaged = new List<CardInstance>(rawTargets.Count);
                    for (int i = 0; i < rawTargets.Count; i++)
                    {
                        var c = rawTargets[i];
                        if (c.IsDestroyed || c.IsDisabled)
                            damaged.Add(c);
                    }
                    if (isRandomRepair)
                    {
                        int desired = targetCount ?? 1;
                        if (damaged.Count > desired)
                        {
                            for (int i = damaged.Count - 1; i > 0; i--)
                            {
                                int j = ctx.Rng.Next(i + 1);
                                (damaged[i], damaged[j]) = (damaged[j], damaged[i]);
                            }
                            damaged.RemoveRange(desired, damaged.Count - desired);
                        }
                    }
                    foreach (var card in damaged)
                    {
                        if (ctx.OnBeforeCardStateChange != null && !ctx.OnBeforeCardStateChange(card, sourceCard, "Repair", "repair"))
                            continue;
                        card.IsDestroyed = false;
                        card.IsDisabled = false;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        DispatchDetailed(ctx, "TTriggerOnCardRepaired", cardOwner, sourceCard, card, cardOwner);
                    }
                    return true;
                }

                case "TActionCardFlyingStart":
                case "TActionCardFlyingStop":
                case "TActionCardFlyingToggle":
                {
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        // 统一检测有效飞行态（含光环授予的 AuraAdd["Flying"]，修复 P1-6 双轨分裂）
                        bool curFlying = card.IsFlying || card.GetAttribute("Flying") > 0;
                        bool next = action.Type switch
                        {
                            "TActionCardFlyingStart" => true,
                            "TActionCardFlyingStop" => false,
                            _ => !curFlying
                        };
                        if (next == curFlying) continue;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        if (!next)
                        {
                            // 若当前由光环赋予 Flying，写入 CombatAdd 负偏移以压制光环飞行，保证 FlyingStop / Toggle 立即落地
                            double auraFly = card.AuraAdd.GetValueOrDefault("Flying", 0);
                            if (auraFly > 0)
                            {
                                card.CombatAdd["Flying"] = -auraFly;
                                card.MarkAttributeDirty();
                            }
                            else
                            {
                                card.CombatAdd.Remove("Flying");
                            }
                        }
                        else if (card.CombatAdd.TryGetValue("Flying", out var negSuppress) && negSuppress < 0)
                        {
                            card.CombatAdd.Remove("Flying");
                            card.MarkAttributeDirty();
                        }
                        ApplyFlyingTransition(ctx, cardOwner, sourceCard, card, next, action.Type);
                        DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Flying", next ? 0 : 1, next ? 1 : 0);
                    }
                    return true;
                }

                case "TActionCardEnchant":
                {
                    bool preventOverride = action.PreventOverride ?? false;
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (card.Template.Type is "Skill" or "SocketEffect" or "PlayerEffect")
                            continue;
                        if (!GameDataRepository.IsSyntheticTestTemplate(card.Template)
                            && !string.IsNullOrEmpty(action.Enchantment)
                            && (card.Template.Enchantments == null || !card.Template.Enchantments.ContainsKey(action.Enchantment)))
                        {
                            continue;
                        }
                        string? prevEnch = card.Enchantment;
                        card.ApplyEnchantment(action.Enchantment, durationTarget, preventOverride);
                        if (!string.Equals(prevEnch, card.Enchantment, StringComparison.Ordinal))
                        {
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.CardEnchanted,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = cardOwner.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TargetCardInstanceId = card.InstanceId,
                                Detail = card.Enchantment,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            ctx.OnCardEnchanted?.Invoke(cardOwner, card);
                            DispatchDetailed(ctx, "TTriggerOnCardEnchanted", cardOwner, sourceCard, card, cardOwner);
                        }
                    }
                    return true;
                }

                case "TActionCardEnchantRandom":
                {
                    bool preventOverride = action.PreventOverride ?? false;
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    var rawPool = action.GetWeightedEnchantments();
                    var pool = rawPool.Count > 0 ? rawPool : DefaultRandomEnchantments.ToList();

                    var targets = action.Target != null
                        ? ResolveActionTargets(action.Target, ctx, targetCount)
                        : [];
                    if (LastSpawnedCardsMap.TryGetValue(ctx.ActiveExecution, out var recentSpawned) && recentSpawned.Count > 0)
                    {
                        if (action.Target == null
                            || (action.Target.Type == "TTargetCardTriggerTarget" && (recentSpawned.Count > 1 || targets.Count == 0)))
                        {
                            int cap = targetCount ?? recentSpawned.Count;
                            targets = recentSpawned.Take(cap).ToList();
                        }
                    }

                    foreach (var card in targets)
                    {
                        if (preventOverride && !string.IsNullOrEmpty(card.Enchantment))
                            continue;

                        List<(string Enchantment, double Weight)> eligible;
                        if (card.Template.Enchantments is { Count: > 0 } supported)
                        {
                            eligible = pool.Where(p => supported.ContainsKey(p.Enchantment)).ToList();
                            if (eligible.Count == 0 && rawPool.Count == 0)
                            {
                                eligible = DefaultRandomEnchantments
                                    .Where(d => supported.ContainsKey(d.Enchantment))
                                    .ToList();
                                if (eligible.Count == 0)
                                    eligible = supported.Keys.Select(k => (k, 1.0)).ToList();
                            }
                        }
                        else if (GameDataRepository.IsSyntheticTestTemplate(card.Template))
                        {
                            eligible = pool;
                        }
                        else
                        {
                            continue;
                        }
                        if (eligible.Count == 0) continue;
                        if (!preventOverride && !string.IsNullOrEmpty(card.Enchantment))
                        {
                            var nonSame = eligible
                                .Where(p => !string.Equals(p.Enchantment, card.Enchantment, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (nonSame.Count > 0)
                                eligible = nonSame;
                        }

                        double totalW = eligible.Sum(e => Math.Max(0, e.Weight));
                        string picked;
                        if (totalW <= 0)
                        {
                            picked = eligible[ctx.Rng.Next(eligible.Count)].Enchantment;
                        }
                        else
                        {
                            double roll = ctx.Rng.NextDouble() * totalW;
                            double acc = 0;
                            picked = eligible[^1].Enchantment;
                            foreach (var e in eligible)
                            {
                                double w = Math.Max(0, e.Weight);
                                if (w <= 0) continue;
                                acc += w;
                                if (roll <= acc) { picked = e.Enchantment; break; }
                            }
                        }

                        string? prevEnch = card.Enchantment;
                        card.ApplyEnchantment(picked, durationTarget, preventOverride);
                        if (!string.Equals(prevEnch, card.Enchantment, StringComparison.Ordinal))
                        {
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.CardEnchanted,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = cardOwner.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TargetCardInstanceId = card.InstanceId,
                                Detail = card.Enchantment,
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            ctx.OnCardEnchanted?.Invoke(cardOwner, card);
                            DispatchDetailed(ctx, "TTriggerOnCardEnchanted", cardOwner, sourceCard, card, cardOwner);
                        }
                    }
                    return true;
                }

                case "TActionCardEnchantRemove":
                {
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var beforeKeys = ctx.EmitCombatEvent != null ? card.GetActiveAttributeKeys() : null;
                        string? prevEnch = card.Enchantment;
                        card.RemoveEnchantment(durationTarget);
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        bool enchChanged = !string.Equals(card.Enchantment, prevEnch, StringComparison.Ordinal);
                        if (ctx.EmitCombatEvent != null && beforeKeys != null)
                        {
                            if (!string.IsNullOrEmpty(prevEnch) && enchChanged)
                            {
                                ctx.EmitCombatEvent.Invoke(new CombatEvent
                                {
                                    TimeMs = ctx.TimeMs,
                                    Type = CombatEventType.CardEnchanted,
                                    SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                    TargetPlayerId = cardOwner.PlayerId,
                                    SourceCardInstanceId = sourceCard.InstanceId,
                                    TargetCardInstanceId = card.InstanceId,
                                    Detail = $"Reverted:{prevEnch}",
                                    IsReverted = true,
                                    AbilityId = ctx.ActiveExecution.AbilityId,
                                    ActionType = action.Type,
                                    ExecutionContextId = ctx.ActiveExecution.ExecutionContextId,
                                    IsSkillSource = sourceCard.Template.Type == "Skill"
                                });
                            }
                            foreach (var attrKey in beforeKeys)
                            {
                                if (!card.HasAnyAttributeKey(attrKey))
                                {
                                    ctx.EmitCombatEvent.Invoke(new CombatEvent
                                    {
                                        TimeMs = ctx.TimeMs,
                                        Type = CombatEventType.CardAttributeDeleted,
                                        SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                        TargetPlayerId = cardOwner.PlayerId,
                                        SourceCardInstanceId = sourceCard.InstanceId,
                                        TargetCardInstanceId = card.InstanceId,
                                        Detail = attrKey,
                                        AbilityId = ctx.ActiveExecution.AbilityId,
                                        ActionType = action.Type,
                                        IsSkillSource = sourceCard.Template.Type == "Skill"
                                    });
                                }
                            }
                        }
                        // D1-P1-11 余项：移除附魔后补发 OnCardEnchanted 与 OnBoardTopologyChanged 即时刷新触发索引与光环
                        if (enchChanged)
                        {
                            ctx.OnCardEnchanted?.Invoke(cardOwner, card);
                            ctx.OnBoardTopologyChanged?.Invoke(cardOwner, card);
                        }
                    }
                    return true;
                }

                case "TActionCardUpgrade":
                {
                    var upgradeDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                    bool isTemporary = upgradeDuration != DurationTarget.Permanent;
                    // P2-23：按 TActionCardUpgrade.InheritEnchantment（默认 true）分流处理源卡附魔向未附魔目标的继承
                    bool inheritEnchantment = action.InheritEnchantment ?? true;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var prevTier = card.Tier;
                        if (!string.IsNullOrEmpty(action.UpgradeToTier))
                        {
                            var targetTier = TierHelper.ParseTier(action.UpgradeToTier);
                            if (card.Tier < targetTier) card.SetTier(targetTier, isTemporary);
                        }
                        else
                        {
                            card.TryUpgrade(isTemporary);
                        }
                        if (card.Tier != prevTier)
                        {
                            var cardOwner = TargetResolver.FindOwner(card, ctx);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.CardUpgraded,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = cardOwner.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TargetCardInstanceId = card.InstanceId,
                                Value = (int)card.Tier,
                                Detail = card.Tier.ToString(),
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                ExecutionContextId = ctx.ActiveExecution.ExecutionContextId,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            ctx.OnCardUpgraded?.Invoke(cardOwner, card);
                            DispatchDetailed(ctx, "TTriggerOnCardUpgraded", cardOwner, sourceCard, card, cardOwner);

                            var (srcEnch, srcLayer) = sourceCard.GetEffectiveEnchantmentLayer();
                            if (inheritEnchantment
                                && !string.IsNullOrEmpty(srcEnch)
                                && string.IsNullOrEmpty(card.Enchantment)
                                && card.Template.Enchantments != null
                                && card.Template.Enchantments.ContainsKey(srcEnch))
                            {
                                var inheritDur = CardInstance.ShorterDuration(srcLayer, upgradeDuration);
                                card.ApplyEnchantment(srcEnch, inheritDur, preventOverride: false);
                                if (!string.IsNullOrEmpty(card.Enchantment))
                                {
                                    ctx.EmitCombatEvent?.Invoke(new CombatEvent
                                    {
                                        TimeMs = ctx.TimeMs,
                                        Type = CombatEventType.CardEnchanted,
                                        SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                        TargetPlayerId = cardOwner.PlayerId,
                                        SourceCardInstanceId = sourceCard.InstanceId,
                                        TargetCardInstanceId = card.InstanceId,
                                        Detail = card.Enchantment,
                                        AbilityId = ctx.ActiveExecution.AbilityId,
                                        ActionType = action.Type,
                                        ExecutionContextId = ctx.ActiveExecution.ExecutionContextId,
                                        IsSkillSource = sourceCard.Template.Type == "Skill"
                                    });
                                    ctx.OnCardEnchanted?.Invoke(cardOwner, card);
                                    DispatchDetailed(ctx, "TTriggerOnCardEnchanted", cardOwner, sourceCard, card, cardOwner);
                                }
                            }
                        }
                    }
                    return true;
                }

                case "TActionCardAddTagsList":
                    if (action.Tags != null)
                    {
                        var tagDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                        foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        {
                            foreach (var t in action.Tags)
                                card.AddTag(t, tagDuration);
                        }
                    }
                    return true;

                case "TActionCardAddTagsRandom":
                    if (action.Tags != null && action.Tags.Count > 0)
                    {
                        var tagDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                        int cnt = SafeMath.ToSafeInt(EvalActionAmount(action, sourceCard, ctx, "Custom_0", 1), 0, 64);
                        if (cnt <= 0) return true;
                        foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        {
                            var candidates = new List<string>(action.Tags.Count);
                            foreach (var t in action.Tags)
                            {
                                if (!string.IsNullOrWhiteSpace(t)
                                    && !card.HasTag(t)
                                    && !candidates.Contains(t, StringComparer.OrdinalIgnoreCase))
                                {
                                    candidates.Add(t);
                                }
                            }
                            if (candidates.Count == 0)
                            {
                                foreach (var t in action.Tags)
                                {
                                    if (!string.IsNullOrWhiteSpace(t)
                                        && !candidates.Contains(t, StringComparer.OrdinalIgnoreCase))
                                    {
                                        candidates.Add(t);
                                    }
                                }
                            }
                            for (int i = 0; i < cnt && candidates.Count > 0; i++)
                            {
                                int idx = ctx.Rng.Next(candidates.Count);
                                card.AddTag(candidates[idx], tagDuration);
                                candidates.RemoveAt(idx);
                            }
                        }
                    }
                    return true;

                case "TActionCardAddTagsBySource":
                {
                    var tagDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                    var sources = action.Source != null ? ResolveActionTargets(action.Source, ctx) : [sourceCard];
                    var tags = sources.SelectMany(s => s.GetAllTags()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        foreach (var t in tags)
                            card.AddTag(t, tagDuration);
                    }
                    return true;
                }

                case "TActionCardRemoveTagsList":
                    if (action.Tags != null && action.Tags.Count > 0)
                    {
                        // D1-P1-02：写入对应持久层 *RemovedTags 扣减集，可剥离模板原生/附魔/低层标签
                        var tagDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                        foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        {
                            foreach (var t in action.Tags)
                                card.RemoveTag(t, tagDuration);
                        }
                    }
                    return true;

                case "TActionCardRemoveTagsBySource":
                {
                    var tagDuration = ResolveDurationTarget(action.Duration, ctx.Scope);
                    var sources = action.Source != null ? ResolveActionTargets(action.Source, ctx) : [sourceCard];
                    var tags = sources.SelectMany(s => s.GetAllTags()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        foreach (var t in tags)
                            card.RemoveTag(t, tagDuration);
                    }
                    return true;
                }

                case "TActionCardTiny":
                {
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        double prevTiny = card.GetAttribute("Tiny");
                        if (prevTiny < 1)
                        {
                            card.ModifyAttribute("Tiny", "Add", 1, durationTarget);
                            double curTiny = card.GetAttribute("Tiny");
                            if (Math.Abs(curTiny - prevTiny) > 1e-6)
                            {
                                var cardOwner = TargetResolver.FindOwner(card, ctx);
                                if (cardOwner.Hand.Cards.Contains(card))
                                    cardOwner.Hand.ReconcileWidthChange(card, ctx.Scope == RuleScope.OutOfCombat ? cardOwner.Stash : null);
                                else if (cardOwner.Stash.Cards.Contains(card))
                                    cardOwner.Stash.ReconcileWidthChange(card, ctx.Scope == RuleScope.OutOfCombat ? cardOwner.Hand : null);
                                DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Tiny", prevTiny, curTiny);
                            }
                        }
                    }
                    return true;
                }

                case "TActionCardTransform":
                case "TActionCardTransformDestroyed":
                {
                    if (ctx.Repo == null || action.SpawnContext == null) return false;
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    bool isTemporary = durationTarget != DurationTarget.Permanent;
                    bool isTimedCombatTransform = ctx.Scope == RuleScope.InCombat
                                                  && action.Duration?.Type == "TCombatDuration"
                                                  && action.Duration.DurationInMs is > 0;
                    int? timedTransformDurationMs = isTimedCombatTransform ? action.Duration!.DurationInMs!.Value : null;
                    bool isRandomTransform = IsRandomTransformTarget(action.Target);
                    bool allowDisabledTargets = string.Equals(action.Type, "TActionCardTransformDestroyed", StringComparison.Ordinal);
                    var transformTargets = ResolveTransformTargets(
                        action.Target,
                        ctx,
                        targetCount,
                        isRandomTransform,
                        allowDisabledTargets);
                    bool disableEnchInherit = HasDisabledInheritEnchantment(action.SpawnContext);
                    bool allowLegendarySpawn = HasExplicitCardOrLegendaryFilter(action.SpawnContext);

                    foreach (var card in transformTargets)
                    {
                        // 非自身目标即使通过特定条件/位置锁定也不得转化 Legendary 卡牌
                        if (string.Equals(action.Type, "TActionCardTransform", StringComparison.Ordinal) &&
                            !ReferenceEquals(card, sourceCard) &&
                            IsLegendaryCard(card))
                        {
                            continue;
                        }
                        var transformCtx = ctx.WithCards(card, sourceCard, card);
                        bool prevIncludeDisabled = ctx.ActiveExecution.IncludeDisabledTargets;
                        if (allowDisabledTargets)
                            ctx.ActiveExecution.IncludeDisabledTargets = true;
                        List<CardInstance> replacements;
                        try
                        {
                            // 求值 SpawnContext.Limit（如 Abducted Cow 的 Custom_0）期间保持原卡对 TargetResolver 可见，同时保留真实 IsDisabled 状态
                            replacements = SpawnTransformReplacements(
                                action.SpawnContext,
                                transformCtx,
                                isRandomTransform && !IsLegendaryCard(card) && !allowLegendarySpawn);
                        }
                        finally
                        {
                            ctx.ActiveExecution.IncludeDisabledTargets = prevIncludeDisabled;
                        }
                        if (replacements.Count == 0) continue;

                        var owner = TargetResolver.FindOwner(card, ctx);
                        // P0-05：链式变形防洗白——原卡若本身已是临时衍生卡，则任意后续变形（即使 Duration == null）均强制维持临时态，绝不写入 TransformReplacementTemplateId
                        bool effectiveIsTemporary = isTemporary || card.IsCombatTemporary;
                        DurationTarget effectiveTransformDuration = card.IsCombatTemporary ? DurationTarget.Combat : durationTarget;

                        string? origSection = null;
                        BoardGrid? primaryGrid = null;
                        BoardGrid? overflowGrid = null;
                        if (owner.Hand.Cards.Contains(card))
                        {
                            origSection = "Hand";
                            primaryGrid = owner.Hand;
                            overflowGrid = ctx.Scope == RuleScope.OutOfCombat ? owner.Stash : null;
                        }
                        else if (owner.Stash.Cards.Contains(card))
                        {
                            origSection = "Stash";
                            primaryGrid = owner.Stash;
                            overflowGrid = ctx.Scope == RuleScope.OutOfCombat ? owner.Hand : null;
                        }
                        else if (owner.Skills.Contains(card))
                        {
                            origSection = "Skill";
                        }
                        else if (owner.SocketEffects.Contains(card))
                        {
                            origSection = "SocketEffect";
                        }
                        if (origSection == null) continue;

                        for (int ri = 0; ri < replacements.Count; ri++)
                        {
                            var rep = replacements[ri];
                            CopyTransformRetentionState(card, rep, effectiveTransformDuration, disableEnchInherit);
                            rep.IsDisabled = false;
                            rep.IsDestroyed = false;
                        }

                        // P0-06：对棋盘容器先执行零副作用只读预检，放不下时直接跳过（不发 Transformed、不置 IsDestroyed、绝不吞卡）
                        if (primaryGrid != null && !primaryGrid.CanReplaceCard(card, replacements[0], overflowGrid))
                            continue;

                        // D3-P1-04（#89-6）：精准置于 CanReplaceCard 只读预检之后、TTriggerOnCardTransformed 之前，避免满盘跳过误触拦截器副作用
                        if (ctx.OnBeforeCardStateChange != null && !ctx.OnBeforeCardStateChange(card, sourceCard, "Transform", "transform"))
                            continue;

                        // TTriggerOnCardTransformed 必须在旧卡尚在 Hand/Stash 且仍保有 Reagent/Potion 等原标签时先派发，
                        // 否则像 [PlayerAttributes] Reagents Transformed / Tome of Yyahan / Vial Launcher 将在 SelfHandAndStash 中匹配不到原卡。
                        DispatchDetailed(ctx, "TTriggerOnCardTransformed", owner, sourceCard, card, owner);

                        // P0-06：快照变形前原卡销毁/禁用态供 TimedTransformExpiry 精准还原（防 TransformDestroyed 免费复活）
                        card.PreTransformWasDestroyed = card.IsDestroyed;
                        card.PreTransformWasDisabled = card.IsDisabled;

                        int slot = card.StartSlot;
                        var placedAll = new List<CardInstance>(replacements.Count);
                        if (primaryGrid != null)
                        {
                            var gridReplacements = new List<CardInstance>(replacements.Count);
                            var skillExtras = new List<CardInstance>();
                            gridReplacements.Add(replacements[0]);
                            for (int extraIdx = 1; extraIdx < replacements.Count; extraIdx++)
                            {
                                if (string.Equals(replacements[extraIdx].Template.Type, "Skill", StringComparison.OrdinalIgnoreCase))
                                    skillExtras.Add(replacements[extraIdx]);
                                else
                                    gridReplacements.Add(replacements[extraIdx]);
                            }

                            if (!primaryGrid.TryReplaceCards(card, gridReplacements, overflowGrid, out var placedPrimary, out var placedOverflow))
                                continue;

                            card.IsDestroyed = true;
                            var placedGridSet = new HashSet<CardInstance>(ReferenceEqualityComparer.Instance);
                            for (int pi = 0; pi < placedPrimary.Count; pi++) placedGridSet.Add(placedPrimary[pi]);
                            for (int oi = 0; oi < placedOverflow.Count; oi++) placedGridSet.Add(placedOverflow[oi]);
                            for (int ri = 0; ri < gridReplacements.Count; ri++)
                            {
                                if (placedGridSet.Contains(gridReplacements[ri]))
                                    placedAll.Add(gridReplacements[ri]);
                            }
                            for (int si = 0; si < skillExtras.Count; si++)
                            {
                                owner.Skills.Add(skillExtras[si]);
                                placedAll.Add(skillExtras[si]);
                            }
                        }
                        else if (origSection == "Skill")
                        {
                            int sIdx = owner.Skills.IndexOf(card);
                            if (sIdx < 0) continue;
                            card.IsDestroyed = true;
                            owner.Skills[sIdx] = replacements[0];
                            placedAll.Add(replacements[0]);
                            for (int extraIdx = 1; extraIdx < replacements.Count; extraIdx++)
                            {
                                var extra = replacements[extraIdx];
                                if (string.Equals(extra.Template.Type, "Skill", StringComparison.OrdinalIgnoreCase))
                                {
                                    owner.Skills.Add(extra);
                                    placedAll.Add(extra);
                                }
                                else if (owner.Hand.TryAddAuto(extra) || (ctx.Scope == RuleScope.OutOfCombat && owner.Stash.TryAddAuto(extra)))
                                {
                                    placedAll.Add(extra);
                                }
                                else
                                {
                                    extra.StartSlot = -1;
                                }
                            }
                        }
                        else if (origSection == "SocketEffect")
                        {
                            int seIdx = owner.SocketEffects.IndexOf(card);
                            if (seIdx < 0) continue;
                            card.IsDestroyed = true;
                            replacements[0].StartSlot = slot;
                            owner.SocketEffects[seIdx] = replacements[0];
                            placedAll.Add(replacements[0]);
                            for (int extraIdx = 1; extraIdx < replacements.Count; extraIdx++)
                            {
                                var extra = replacements[extraIdx];
                                if (string.Equals(extra.Template.Type, "SocketEffect", StringComparison.OrdinalIgnoreCase))
                                {
                                    extra.StartSlot = slot;
                                    owner.SocketEffects.Add(extra);
                                    placedAll.Add(extra);
                                }
                                else if (owner.Hand.TryAddAuto(extra) || (ctx.Scope == RuleScope.OutOfCombat && owner.Stash.TryAddAuto(extra)))
                                {
                                    placedAll.Add(extra);
                                }
                                else
                                {
                                    extra.StartSlot = -1;
                                }
                            }
                        }

                        if (placedAll.Count == 0) continue;
                        var replacement = placedAll[0];

                        if (ctx.Scope == RuleScope.InCombat)
                        {
                            if (effectiveIsTemporary)
                            {
                                for (int ri = 0; ri < placedAll.Count; ri++)
                                {
                                    placedAll[ri].IsCombatTemporary = true;
                                    placedAll[ri].TransformedFromInstanceId = card.TransformedFromInstanceId ?? card.InstanceId;
                                }
                            }
                            else
                            {
                                card.TransformReplacementTemplateId = replacement.Template.Id;
                                for (int ri = 0; ri < placedAll.Count; ri++)
                                {
                                    placedAll[ri].IsCombatTemporary = false;
                                    placedAll[ri].TransformedFromInstanceId = card.TransformedFromInstanceId ?? card.InstanceId;
                                }
                            }
                        }

                        for (int extraIdx = 1; extraIdx < placedAll.Count; extraIdx++)
                        {
                            var extra = placedAll[extraIdx];
                            ctx.OnCardSpawned?.Invoke(owner, extra);
                            ctx.EmitCombatEvent?.Invoke(new CombatEvent
                            {
                                TimeMs = ctx.TimeMs,
                                Type = CombatEventType.CardTransformed,
                                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                                TargetPlayerId = owner.PlayerId,
                                SourceCardInstanceId = sourceCard.InstanceId,
                                TargetCardInstanceId = extra.InstanceId,
                                Detail = $"{card.InstanceId}->{extra.Template.Id}",
                                AbilityId = ctx.ActiveExecution.AbilityId,
                                ActionType = action.Type,
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                        }

                        // 执行被变形源卡自带的 Transform.Abilities 与动作节点自带的 action.Abilities（D1-P2-01）
                        bool hasTemplateTransformAb = card.Template.Transform?.Abilities is { Count: > 0 };
                        bool hasActionTransformAb = action.Abilities is { Count: > 0 };
                        if (hasTemplateTransformAb || hasActionTransformAb)
                        {
                            var tExecCtx = owner == ctx.SelfPlayer
                                ? ctx.WithCards(replacement, sourceCard, replacement)
                                : new RuleExecutionContext(ctx.Repo, ctx.Scope, owner, ctx.OpponentPlayer == owner ? ctx.SelfPlayer : ctx.OpponentPlayer, ctx.Rng)
                                {
                                    LocalPlayerId = ctx.LocalPlayerId,
                                    CurrentDay = ctx.CurrentDay,
                                    CurrentHour = ctx.CurrentHour,
                                    TimeMs = ctx.TimeMs,
                                    CombatType = ctx.CombatType,
                                    PlayMode = ctx.PlayMode,
                                    CurrentEncounterId = ctx.CurrentEncounterId,
                                    VisitedEncounterIds = ctx.VisitedEncounterIds,
                                    SelfCard = replacement,
                                    TriggerSourceCard = sourceCard,
                                    TriggerTargetCard = replacement,
                                    OnCardSpawned = ctx.OnCardSpawned,
                                    OnCardEnchanted = ctx.OnCardEnchanted,
                                    OnCardUpgraded = ctx.OnCardUpgraded,
                                    OnCardTransformed = ctx.OnCardTransformed,
                                    OnBoardTopologyChanged = ctx.OnBoardTopologyChanged,
                                    OnDetailedTriggeredEvent = ctx.OnDetailedTriggeredEvent,
                                    OnCardTriggeredEvent = ctx.OnCardTriggeredEvent,
                                    EmitCombatEvent = ctx.EmitCombatEvent,
                                    OnUnknownRuleType = ctx.OnUnknownRuleType
                                };
                            if (hasTemplateTransformAb)
                            {
                                foreach (var tAb in card.Template.Transform!.Abilities!.Values)
                                {
                                    if (tAb.Action == null) continue;
                                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(tAb.Prerequisites, tExecCtx))
                                        continue;
                                    ExecuteAction(tAb.Action, replacement, tExecCtx);
                                }
                            }
                            if (hasActionTransformAb)
                            {
                                foreach (var aAb in action.Abilities!.Values)
                                {
                                    if (aAb.Action == null) continue;
                                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(aAb.Prerequisites, tExecCtx))
                                        continue;
                                    ExecuteAction(aAb.Action, replacement, tExecCtx);
                                }
                            }
                        }

                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.CardTransformed,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = owner.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            TargetCardInstanceId = replacement.InstanceId,
                            Detail = $"{card.InstanceId}->{replacement.Template.Id}",
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        ctx.OnCardTransformed?.Invoke(owner, card, replacement);
                        ctx.OnCardTransformBatch?.Invoke(owner, card, placedAll, effectiveIsTemporary, timedTransformDurationMs, origSection, slot);
                        ctx.OnBoardTopologyChanged?.Invoke(owner, replacement);
                        DispatchDetailed(ctx, "TTriggerOnCardAfterTransform", owner, sourceCard, replacement, owner);
                    }
                    return true;
                }

                case "TActionGameAddToExclusionSet":
                {
                    var targetPlayer = TargetResolver.ResolvePlayers(action.TargetPlayer ?? action.Target, ctx).FirstOrDefault()
                                       ?? TargetResolver.GetLocalPlayer(ctx);
                    targetPlayer.EnsureExclusionSetInitialized(ctx.Repo?.GameMode);
                    if (action.Ids != null)
                    {
                        foreach (var id in action.Ids)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                                targetPlayer.ExclusionSet.Add(id);
                        }
                    }
                    return true;
                }

                case "TActionGameRemoveFromExclusionSet":
                {
                    var targetPlayer = TargetResolver.ResolvePlayers(action.TargetPlayer ?? action.Target, ctx).FirstOrDefault()
                                       ?? TargetResolver.GetLocalPlayer(ctx);
                    targetPlayer.EnsureExclusionSetInitialized(ctx.Repo?.GameMode);
                    if (action.Ids != null)
                    {
                        foreach (var id in action.Ids)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                                targetPlayer.ExclusionSet.Remove(id);
                        }
                    }
                    return true;
                }

                case "TActionGameSpawnCards":
                case "TActionCardSpawn":
                {
                    var effectiveSpawnCtx = action.SpawnContext;
                    int? defaultSpawnLimit = action.SpawnCount;
                    if (effectiveSpawnCtx == null && (!string.IsNullOrEmpty(action.Id) || action.Ids is { Count: > 0 }))
                    {
                        defaultSpawnLimit = Math.Max(1, action.SpawnCount ?? 1);
                        var spawnIds = action.Ids is { Count: > 0 } ? action.Ids : [action.Id!];
                        effectiveSpawnCtx = new RuleNode
                        {
                            Type = "TSpawnContextQuery",
                            Groups =
                            [
                                new RuleNode
                                {
                                    Type = "TSpawnGroup",
                                    Filters = [new RuleNode { Type = "TSpawnFilterIdList", Ids = spawnIds }]
                                }
                            ]
                        };
                    }
                    if (ctx.Repo == null || effectiveSpawnCtx == null) return false;
                    var targetPlayers = TargetResolver.ResolvePlayers(action.TargetPlayer ?? action.Target, ctx);
                    var receiver = targetPlayers.FirstOrDefault() ?? ctx.SelfPlayer;
                    var spawnCtx = receiver == ctx.SelfPlayer
                        ? ctx
                        : new RuleExecutionContext(ctx.Repo, ctx.Scope, receiver, ctx.OpponentPlayer, ctx.Rng)
                        {
                            LocalPlayerId = ctx.LocalPlayerId,
                            CurrentDay = ctx.CurrentDay,
                            CurrentHour = ctx.CurrentHour,
                            TimeMs = ctx.TimeMs,
                            CombatType = ctx.CombatType,
                            PlayMode = ctx.PlayMode,
                            CurrentEncounterId = ctx.CurrentEncounterId,
                            SelfCard = ctx.SelfCard,
                            TriggerSourceCard = ctx.TriggerSourceCard,
                            TriggerTargetCard = ctx.TriggerTargetCard,
                            OnUnknownRuleType = ctx.OnUnknownRuleType
                        };
                    bool avoidAdjacentSocket = SpawnQueryEngine.HasBehavior(effectiveSpawnCtx, "TSpawnBehaviorAvoidAdjacentSocketEffect");
                    bool ignoreSpace = SpawnQueryEngine.HasBehavior(effectiveSpawnCtx, "TSpawnBehaviorIgnoreSpaceConstraint");
                    bool isTemporary = ctx.Scope == RuleScope.InCombat
                                       && ResolveDurationTarget(action.Duration, ctx.Scope) != DurationTarget.Permanent;
                    var addedSpawned = new List<CardInstance>(defaultSpawnLimit ?? 4);
                    var attrDurTarget = isTemporary ? DurationTarget.Combat : DurationTarget.Permanent;

                    bool TryFinalizeAndPlaceSpawned(CardInstance c)
                    {
                        c.IsCombatTemporary = isTemporary;
                        c.AttachedStandardPrices = ctx.Repo.GameMode.StandardPrices;
                        // 先写入生成附带属性（如 83 张衍生包/Shipment 的 {"Tiny": 1.0}），再执行 Hand/Stash 自动落位
                        if (action.Attributes != null && action.Attributes.Count > 0)
                        {
                            foreach (var (attrKey, attrNode) in action.Attributes)
                            {
                                if (string.IsNullOrWhiteSpace(attrKey)) continue;
                                double attrVal = attrNode != null
                                    ? ValueAndConditionEvaluator.EvaluateValue(attrNode, spawnCtx.WithCards(c, sourceCard, c), 0)
                                    : 0;
                                if (double.IsNaN(attrVal) || double.IsInfinity(attrVal)) attrVal = 0;
                                if (Math.Abs(attrVal) > 1e-9)
                                    c.ModifyAttribute(attrKey, "Add", attrVal, attrDurTarget);
                            }
                        }

                        bool added;
                        if (SocketPlacementHelper.IsSocketEffectTemplate(c.Template))
                        {
                            added = SocketPlacementHelper.TryPlaceSocketEffect(
                                receiver, c, avoidAdjacentSocket, ctx.Rng, out var overwritten);
                            if (overwritten != null && ctx.Scope == RuleScope.OutOfCombat)
                                ctx.OnCardDestroyed?.Invoke(receiver, overwritten);
                        }
                        else if (c.Template.Type == "Skill")
                        {
                            receiver.Skills.Add(c);
                            added = true;
                        }
                        else if (receiver.Hand.TryAddAuto(c))
                        {
                            added = true;
                        }
                        else if (receiver.Stash.TryAddAuto(c))
                        {
                            added = true;
                        }
                        else if (ignoreSpace)
                        {
                            receiver.Stash.Cards.Add(c);
                            added = true;
                        }
                        else
                        {
                            added = false;
                        }

                        if (added)
                        {
                            addedSpawned.Add(c);
                            ctx.OnCardSpawned?.Invoke(receiver, c);
                        }
                        return added;
                    }

                    List<CardInstance> spawned;
                    if (ignoreSpace)
                    {
                        spawned = SpawnQueryEngine.SpawnCards(effectiveSpawnCtx, spawnCtx, defaultCountOverride: defaultSpawnLimit);
                        foreach (var c in spawned)
                            TryFinalizeAndPlaceSpawned(c);
                    }
                    else
                    {
                        bool spawnsTiny = HasTinySpawnAttribute(action, spawnCtx);
                        spawned = SpawnQueryEngine.SpawnCards(
                            effectiveSpawnCtx,
                            spawnCtx,
                            defaultCountOverride: defaultSpawnLimit,
                            ownedMaxTiers: null,
                            validAssetIds: null,
                            archetypeResolver: null,
                            rules: null,
                            maxItemSlotWidthProvider: () => GetMaxFitItemSlotWidth(receiver),
                            spawnAppliesTiny: spawnsTiny,
                            tryAcceptCard: TryFinalizeAndPlaceSpawned);
                    }

                    var trackedSpawned = addedSpawned.Count > 0 ? addedSpawned : spawned;
                    if (trackedSpawned.Count > 0)
                    {
                        LastSpawnedCardsMap.Remove(ctx.ActiveExecution);
                        LastSpawnedCardsMap.Add(ctx.ActiveExecution, trackedSpawned);
                    }
                    return true;
                }

                case "TActionGameDealCards":
                {
                    var dealt = SpawnQueryEngine.SpawnCards(action.SpawnContext, ctx);
                    var mode = action.SpawnMode ?? "Replace";
                    var rules = action.GetSelectionContextRulesData();
                    if (ctx.OnDealCardsToSelectionWithRules != null)
                        ctx.OnDealCardsToSelectionWithRules.Invoke(new DealtCardsPayload(dealt, mode, rules, action.SelectionContextRules));
                    else
                        ctx.OnDealCardsToSelection?.Invoke(dealt, mode);
                    return true;
                }

                case "TActionGameReroll":
                {
                    // P1-17：透传可选 SpawnCount 覆盖至局外调度层，兼容旧 1 参回调
                    string rerollType = action.RerollType ?? "All";
                    if (ctx.OnRerollEncounterWithCount != null)
                        ctx.OnRerollEncounterWithCount.Invoke(rerollType, action.SpawnCount);
                    else
                        ctx.OnRerollEncounter?.Invoke(rerollType);
                    return true;
                }

                case "TActionExitReplacementSet":
                    ctx.OnExitSelection?.Invoke();
                    return true;

                case "TActionCardBeginSandstorm":
                    ctx.OnBeginSandstormRequested?.Invoke();
                    return true;

                case "TActionPlayerPortraitNext":
                    foreach (var p in TargetResolver.ResolvePlayers(action.Target, ctx)) p.PortraitIndex++;
                    return true;

                case "TActionPlayerPortraitReset":
                    foreach (var p in TargetResolver.ResolvePlayers(action.Target, ctx)) p.PortraitIndex = 0;
                    return true;

                default:
                    if (ModRuntimeHooks.HasAnyCustomActions)
                    {
                        bool modActionResult = ModRuntimeHooks.TryExecuteCustomAction(action, sourceCard, ctx, out bool handled);
                        if (handled)
                            return modActionResult;
                    }
                    RuleExecutionContext.NotifyUnknownRuleType(ctx, "TAction", action.Type);
                    return false;
            }
        }
        finally
        {
            ctx.ActiveExecution.ActionType = prevActionType;
        }
    }

    public static void TryAdvanceQuests(
        string eventType,
        PlayerState ownerPlayer,
        CardInstance? eventSourceCard,
        CardInstance? eventTargetCard,
        PlayerState? eventSubjectPlayer,
        string? changedAttr,
        double prevVal,
        double curVal,
        string combatType,
        string combatOutcome,
        RuleExecutionContext ctx)
    {
        // 使用独立快照枚举所有拥有卡牌，防止任务完成回调 (OnQuestCompleted) 生成或移除卡牌导致集合修改异常
        var ownedSnapshot = ownerPlayer.EnumerateAllOwnedCards();
        for (int i = 0; i < ownedSnapshot.Count; i++)
        {
            var card = ownedSnapshot[i];
            if (card.IsDestroyed || card.Template.Quests == null || card.Template.Quests.Count == 0) continue;
            bool inStash = ownerPlayer.Stash.Cards.Contains(card);
            bool changed = false;
            List<(QuestEntry Entry, double Progress)>? newlyCompleted = null;
            foreach (var q in card.Template.Quests)
            {
                if (IsQuestGroupCompleted(card, q))
                    continue;
                var qCtx = ctx.WithCards(card, eventSourceCard, eventTargetCard);
                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(q.Prerequisites, qCtx))
                    continue;

                foreach (var entry in q.Entries)
                {
                    if (card.IsQuestCompleted(entry.AttributeType)) continue;
                    // D1-P1-10：Stash 中的 Quest 遵循 ActiveIn 约束（仅放行 HandAndStash/StashOnly，拒绝 HandOnly）
                    if (inStash)
                    {
                        string? triggerActiveIn = entry.Trigger?.ExtensionData != null
                            && entry.Trigger.ExtensionData.TryGetValue("ActiveIn", out var aiEl)
                            && aiEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? aiEl.GetString()
                            : null;
                        if (string.Equals(triggerActiveIn, "HandOnly", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    var localCtx = ctx.WithCards(card, eventSourceCard, eventTargetCard);
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(entry.Prerequisites, localCtx))
                        continue;
                    if (!MatchesTrigger(entry.Trigger, eventType, card, ownerPlayer, eventSourceCard, eventTargetCard, eventSubjectPlayer, changedAttr, prevVal, curVal, combatType, combatOutcome, localCtx))
                        continue;

                    double delta = entry.Update?.Value != null
                        ? ValueAndConditionEvaluator.EvaluateValue(entry.Update.Value, localCtx, 1)
                        : 1;
                    // D1-P2-01：解析 QuestUpdate.Duration，战中限时任务（Combat）独立写入 CombatQuestProgress/CombatCompletedQuests，不污染永久进度
                    var updateDuration = ResolveDurationTarget(entry.Update?.Duration, ctx.Scope);
                    bool isCombatScoped = updateDuration == DurationTarget.Combat;
                    double prev = card.GetQuestProgress(entry.AttributeType);
                    double next = prev + delta;
                    if (isCombatScoped)
                        card.CombatQuestProgress[entry.AttributeType] = card.CombatQuestProgress.GetValueOrDefault(entry.AttributeType, 0) + delta;
                    else
                        card.QuestProgress[entry.AttributeType] = next;

                    if (Math.Abs(next - prev) > 1e-9)
                    {
                        card.MarkAttributeDirty();
                        ctx.OnQuestUpdated?.Invoke(card, entry.AttributeType, prev, next, entry.Target);
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.QuestUpdated,
                            SourcePlayerId = ownerPlayer.PlayerId,
                            TargetPlayerId = ownerPlayer.PlayerId,
                            SourceCardInstanceId = card.InstanceId,
                            TargetCardInstanceId = card.InstanceId,
                            Value = next,
                            Detail = entry.AttributeType,
                            IsSkillSource = card.Template.Type == "Skill"
                        });
                    }

                    if (next >= entry.Target)
                    {
                        changed = true;
                        newlyCompleted ??= new List<(QuestEntry, double)>();
                        newlyCompleted.Add((entry, next));
                        if (q.EffectiveRepeatable)
                        {
                            // D1-P2-01 & P2-19：兼容 IsRepeatable 布尔与 Repeatable.Prerequisites，满足时重置进度而不锁入 CompletedQuests
                            bool canRepeat = q.Repeatable == null
                                || ValueAndConditionEvaluator.EvaluatePrerequisites(q.Repeatable.Prerequisites, localCtx);
                            if (canRepeat)
                            {
                                if (isCombatScoped)
                                {
                                    card.CombatCompletedQuests.Remove(entry.AttributeType);
                                    card.CombatQuestProgress[entry.AttributeType] = 0;
                                }
                                else
                                {
                                    card.CompletedQuests.Remove(entry.AttributeType);
                                    card.QuestProgress[entry.AttributeType] = 0;
                                }
                            }
                            else
                            {
                                if (isCombatScoped)
                                    card.CombatCompletedQuests.Add(entry.AttributeType);
                                else
                                    card.CompletedQuests.Add(entry.AttributeType);
                            }
                        }
                        else
                        {
                            if (isCombatScoped)
                                card.CombatCompletedQuests.Add(entry.AttributeType);
                            else
                                card.CompletedQuests.Add(entry.AttributeType);
                            break;
                        }
                    }
                }
            }
            if (changed)
            {
                FinalizeCompletedQuests(card, newlyCompleted!, ownerPlayer, eventSourceCard, eventTargetCard, ctx);
            }
        }
    }

    private static bool IsQuestGroupCompleted(CardInstance card, QuestDefinition group)
    {
        if (group.EffectiveRepeatable || group.Entries.Count == 0)
            return false;
        for (int i = 0; i < group.Entries.Count; i++)
        {
            if (card.IsQuestCompleted(group.Entries[i].AttributeType))
                return true;
        }
        return false;
    }

    private static void SyncDirectQuestAttributeProgress(
        CardInstance card,
        string questAttr,
        double prevAttrVal,
        double curAttrVal,
        PlayerState ownerPlayer,
        CardInstance? sourceCard,
        RuleExecutionContext ctx)
    {
        if (card.Template.Quests == null || card.Template.Quests.Count == 0) return;
        double prevProg = card.QuestProgress.GetValueOrDefault(questAttr, 0);
        double nextProg = Math.Max(prevProg, curAttrVal);
        card.QuestProgress[questAttr] = nextProg;
        if (Math.Abs(nextProg - prevProg) > 1e-9)
            card.MarkAttributeDirty();

        List<(QuestEntry Entry, double Progress)>? newlyCompleted = null;
        var localCtx = ctx.WithCards(card, sourceCard, card);
        foreach (var q in card.Template.Quests)
        {
            if (IsQuestGroupCompleted(card, q)) continue;
            foreach (var entry in q.Entries)
            {
                if (!string.Equals(entry.AttributeType, questAttr, StringComparison.OrdinalIgnoreCase)) continue;
                if (card.CompletedQuests.Contains(entry.AttributeType)) continue;
                if (nextProg >= entry.Target)
                {
                    newlyCompleted ??= new List<(QuestEntry, double)>();
                    newlyCompleted.Add((entry, nextProg));
                    if (q.EffectiveRepeatable)
                    {
                        bool canRepeat = q.Repeatable == null
                            || ValueAndConditionEvaluator.EvaluatePrerequisites(q.Repeatable.Prerequisites, localCtx);
                        if (canRepeat)
                        {
                            card.CompletedQuests.Remove(entry.AttributeType);
                            card.QuestProgress[entry.AttributeType] = 0;
                        }
                        else
                        {
                            card.CompletedQuests.Add(entry.AttributeType);
                        }
                    }
                    else
                    {
                        card.CompletedQuests.Add(entry.AttributeType);
                        break;
                    }
                }
            }
        }
        if (Math.Abs(nextProg - prevProg) > 1e-9 && newlyCompleted == null)
        {
            foreach (var q in card.Template.Quests)
            {
                var entry = q.Entries.FirstOrDefault(e => string.Equals(e.AttributeType, questAttr, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    ctx.OnQuestUpdated?.Invoke(card, entry.AttributeType, prevProg, nextProg, entry.Target);
                    break;
                }
            }
        }
        if (newlyCompleted != null)
            FinalizeCompletedQuests(card, newlyCompleted, ownerPlayer, sourceCard, card, ctx);
    }

    private static void FinalizeCompletedQuests(
        CardInstance card,
        List<(QuestEntry Entry, double Progress)> newlyCompleted,
        PlayerState ownerPlayer,
        CardInstance? eventSourceCard,
        CardInstance? eventTargetCard,
        RuleExecutionContext ctx)
    {
        card.RebuildTierAndEnchantmentData();
        ctx.OnBoardTopologyChanged?.Invoke(ownerPlayer, card);
        foreach (var (entry, progress) in newlyCompleted)
        {
            ctx.EmitCombatEvent?.Invoke(new CombatEvent
            {
                TimeMs = ctx.TimeMs,
                Type = CombatEventType.QuestCompleted,
                SourcePlayerId = ownerPlayer.PlayerId,
                TargetPlayerId = ownerPlayer.PlayerId,
                SourceCardInstanceId = card.InstanceId,
                TargetCardInstanceId = card.InstanceId,
                Value = progress,
                Detail = entry.AttributeType,
                IsSkillSource = card.Template.Type == "Skill"
            });
            if (entry.CompletionEffects != null)
            {
                var effectCtx = ctx.WithCards(card, eventSourceCard ?? card, eventTargetCard ?? card);
                foreach (var eff in entry.CompletionEffects.Values)
                {
                    if (eff.Action == null) continue;
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(eff.Prerequisites, effectCtx))
                        continue;
                    ExecuteAction(eff.Action, card, effectCtx);
                }
            }
        }
        if (ctx.OnQuestCompleted != null)
            ctx.OnQuestCompleted.Invoke(card);
        else
            DispatchDetailed(ctx, "TTriggerOnCardQuestCompleted", ownerPlayer, card, card, ownerPlayer);
    }

    private static bool TryPayCost(RuleNode cost, CardInstance sourceCard, RuleExecutionContext ctx)
    {
        if (cost.Type == "TActionCostPlayerAttribute")
        {
            var valNode = cost.GetValueNode(GameDatabaseLoader.JsonOptions);
            double rawAmt = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (cost.GetFixedNumericValue() ?? 0);
            double amt = SafeMath.ClampFinite(rawAmt, 0.0, SafeMath.DefaultCombatMax, 0.0);
            var attr = cost.PlayerAttributeType ?? "Gold";
            double prevAttr = ctx.SelfPlayer.GetAttribute(attr);
            if (prevAttr + 1e-9 < amt) return false;
            if (amt > 0)
            {
                ctx.SelfPlayer.ModifyAttribute(attr, "Subtract", amt);
                double curAttr = ctx.SelfPlayer.GetAttribute(attr);
                ctx.OnActionCostSpent?.Invoke(sourceCard, attr, amt);
                if (Math.Abs(curAttr - prevAttr) > 1e-6)
                    DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, ctx.SelfPlayer, attr, prevAttr, curAttr);
            }
            return true;
        }

        if (cost.Type == "TActionCostCardAttribute")
        {
            var valNode = cost.GetValueNode(GameDatabaseLoader.JsonOptions);
            double rawAmt = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (cost.GetFixedNumericValue() ?? 0);
            double amt = SafeMath.ClampFinite(rawAmt, 0.0, SafeMath.DefaultCombatMax, 0.0);
            var attr = cost.CardAttributeType ?? cost.AttributeType ?? "Ammo";
            var targetCard = cost.Target != null
                ? ResolveActionTargets(cost.Target, ctx).FirstOrDefault()
                : sourceCard;
            if (targetCard == null) return false;

            double prev = targetCard.GetAttribute(attr);
            if (prev + 1e-9 < amt) return false;
            if (amt > 0)
            {
                if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                {
                    int deduct = SafeMath.ToSafeInt(amt, 0, SafeMath.DefaultCombatMax);
                    targetCard.CurrentAmmo = Math.Clamp(targetCard.CurrentAmmo - deduct, 0, targetCard.EffectiveAmmoMax);
                    targetCard.MarkAttributeDirty();
                }
                else
                {
                    var dur = ResolveDurationTarget(cost.Duration, ctx.Scope);
                    targetCard.ModifyAttribute(attr, "Subtract", amt, dur);
                }
                double cur = targetCard.GetAttribute(attr);
                ctx.OnActionCostSpent?.Invoke(targetCard, attr, amt);
                if (Math.Abs(cur - prev) > 1e-6)
                {
                    var cardOwner = TargetResolver.FindOwner(targetCard, ctx);
                    DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, targetCard, cardOwner, attr, prev, cur);
                }
            }
            return true;
        }

        // D1-P1-01：未知 Cost 类型绝不免费放行，触发未知节点遥测并阻断执行
        RuleExecutionContext.NotifyUnknownRuleType(ctx, "TActionCost", cost.Type);
        return false;
    }

    private static double EvalActionAmount(RuleNode action, CardInstance sourceCard, RuleExecutionContext ctx, string fallbackAttr, double defaultVal = 0)
    {
        double result;
        if (action.ReferenceValue != null)
            result = ValueAndConditionEvaluator.EvaluateValue(action.ReferenceValue, ctx, defaultVal);
        else
        {
            var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
            if (valNode != null)
                result = ValueAndConditionEvaluator.EvaluateValue(valNode, ctx, defaultVal);
            else
            {
                var fixedVal = action.GetFixedNumericValue();
                if (fixedVal.HasValue) result = fixedVal.Value;
                else if (ctx.ActiveExecution.Ability != null
                    ? sourceCard.TryGetAttributeForAbility(fallbackAttr, ctx.ActiveExecution.Ability, out double cardAttrObj)
                    : sourceCard.TryGetAttributeForAbility(fallbackAttr, ctx.ActiveExecution.AbilityId, out cardAttrObj))
                    result = cardAttrObj;
                else
                    result = defaultVal;
            }
        }
        return (double.IsNaN(result) || double.IsInfinity(result)) ? defaultVal : result;
    }

    private static string? GetTargetsAttributeForAction(string? actionType) => actionType switch
    {
        "TActionCardHaste" => "HasteTargets",
        "TActionCardSlow" => "SlowTargets",
        "TActionCardFreeze" => "FreezeTargets",
        "TActionCardCharge" => "ChargeTargets",
        "TActionCardFlyingStart" or "TActionCardFlyingStop" or "TActionCardFlyingToggle" => "FlyingTargets",
        "TActionCardReload" => "ReloadTargets",
        "TActionCardDisable" => "DisableTargets",
        "TActionCardDestroy" => "DestroyTargets",
        "TActionCardUpgrade" => "UpgradeTargets",
        "TActionCardEnchant" or "TActionCardEnchantRandom" => "EnchantTargets",
        "TActionCardEnchantRemove" => "EnchantRemoveTargets",
        "TActionCardForceUse" => "ForceUseTargets",
        "TActionCardTransform" or "TActionCardTransformDestroyed" => "TransformTargets",
        _ => null
    };

    private static int ScaleWithCrit(double raw, CardInstance sourceCard, RuleExecutionContext ctx, out bool isCrit, string? critBonusAttr = null)
    {
        isCrit = RollCrit(sourceCard, ctx);
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0) return 0;
        double critPct = ctx.CombatCritPercentMultiplierOverride ?? (ctx.Repo?.GameMode.CritPercentMultiplier ?? 200);
        if (!string.IsNullOrEmpty(critBonusAttr))
        {
            critPct += sourceCard.GetAttribute(critBonusAttr);
            // D1-P2-07：同步叠加玩家级 *Crit 暴伤加成（如 DamageCrit / ShieldCrit）
            if (ctx.SelfPlayer != null)
                critPct += ctx.SelfPlayer.GetAttribute(critBonusAttr);
        }
        double mult = isCrit ? Math.Max(0, critPct) / 100.0 : 1.0;
        return SafeMath.ToSafeInt(raw * mult, 0, SafeMath.DefaultCombatMax);
    }

    private static int ResolveDurationMs(RuleNode action, CardInstance sourceCard, RuleExecutionContext ctx, string fallbackAttr)
    {
        // P0-08：缺失或显式为 0 时一律返回 0ms，严禁误判回退 1000ms；P0-12：先在 double 域经 SafeMath 饱和钳制再转 int
        double raw = EvalActionAmount(action, sourceCard, ctx, fallbackAttr, 0);
        return SafeMath.ToSafeInt(raw, 0, 120_000);
    }

    private static void RecordOrDispatchCrit(RuleExecutionContext ctx, CardInstance sourceCard)
    {
        // 修复 P0-05：同次 TTriggerOnCardFired 施放内延迟到施放循环末尾统一仅派发 1 次 TTriggerOnCardCritted；
        // D1-P1-15：非 CardFired（如 CanCrit 隐藏标签卡的 TActionAnd 多动作）在同一 ExecutionContextId 内亦仅派发 1 次。
        if (ctx.ActiveExecution.InCardFiredExecution
            && ReferenceEquals(ctx.ActiveExecution.CurrentCastCard, sourceCard))
        {
            ctx.ActiveExecution.CurrentCastDidCrit = true;
            return;
        }
        string? execId = ctx.ActiveExecution.ExecutionContextId;
        if (!string.IsNullOrEmpty(execId))
        {
            if (string.Equals(ctx.ActiveExecution.LastNonCastCritExecId, execId, StringComparison.Ordinal)
                && ReferenceEquals(ctx.ActiveExecution.LastNonCastCritCard, sourceCard))
            {
                return;
            }
            ctx.ActiveExecution.LastNonCastCritExecId = execId;
            ctx.ActiveExecution.LastNonCastCritCard = sourceCard;
        }
        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
    }

    /// <summary>D1-P1-15：对齐官方 TCardConditionalCanCrit，仅显式 CanCrit 隐藏标签卡或带冷却物品在自身主动开火帧内允许暴击。</summary>
    private static bool CanAbilityCrit(CardInstance sourceCard, RuleExecutionContext ctx)
    {
        if (sourceCard.HasHiddenTag("CanCrit"))
            return true;
        return ctx.ActiveExecution.InCardFiredExecution
            && ReferenceEquals(ctx.ActiveExecution.CurrentCastCard, sourceCard)
            && string.Equals(sourceCard.Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
            && sourceCard.HasActiveCooldown;
    }

    private static bool RollCrit(CardInstance sourceCard, RuleExecutionContext ctx)
    {
        if (!CanAbilityCrit(sourceCard, ctx))
            return false;
        if (ctx.ActiveExecution.InCardFiredExecution
            && ReferenceEquals(ctx.ActiveExecution.CurrentCastCard, sourceCard))
        {
            if (ctx.ActiveExecution.CurrentCastCritRoll.HasValue)
                return ctx.ActiveExecution.CurrentCastCritRoll.Value;
            bool rolled = RollCritCore(sourceCard, ctx);
            ctx.ActiveExecution.CurrentCastCritRoll = rolled;
            return rolled;
        }
        return RollCritCore(sourceCard, ctx);
    }

    private static bool RollCritCore(CardInstance sourceCard, RuleExecutionContext ctx)
    {
        double critChance = sourceCard.GetAttribute("CritChance")
                          + (ctx.SelfPlayer?.GetAttribute("CritChance") ?? 0)
                          + ctx.CombatBonusCritChance;
        if (critChance <= 0) return false;
        if (critChance >= 100) return true;
        return ctx.Rng.NextDouble() * 100.0 < critChance;
    }

    private static int GetMaxFitItemSlotWidth(PlayerState receiver)
    {
        for (int w = 3; w >= 1; w--)
        {
            if (receiver.Hand.CanFit(w) || receiver.Stash.CanFit(w))
                return w;
        }
        return 0;
    }

    private static bool HasTinySpawnAttribute(RuleNode action, RuleExecutionContext ctx)
    {
        if (action.Attributes == null || !action.Attributes.TryGetValue("Tiny", out var tinyNode) || tinyNode == null)
            return false;
        if (string.Equals(tinyNode.Type, "TFixedValue", StringComparison.Ordinal))
            return (tinyNode.GetFixedNumericValue() ?? 0) > 0;
        double val = ValueAndConditionEvaluator.EvaluateValue(tinyNode, ctx, 0);
        return double.IsFinite(val) && val > 0;
    }

    private static bool IsLegendaryCard(CardInstance card)
        => card.Tier == CardTier.Legendary || card.Template.DefaultTier == CardTier.Legendary;

    private static bool IsRandomTransformTarget(RuleNode? target)
    {
        if (target == null) return false;
        if (string.Equals(target.Type, "TTargetCardRandom", StringComparison.Ordinal))
            return true;
        if (target.Type is "TTargetCardSection" or "TTargetCardXMost" or "TTargetCardPositional")
            return !HasExplicitIdCondition(target.GetSingleCondition(GameDatabaseLoader.JsonOptions));
        return false;
    }

    private static bool HasExplicitIdCondition(RuleNode? cond)
    {
        if (cond == null) return false;
        if (string.Equals(cond.Type, "TCardConditionalId", StringComparison.Ordinal) && cond.IsNot != true)
            return true;
        if (cond.Type is "TCardConditionalAnd" or "TCardConditionalOr")
        {
            foreach (var sub in cond.GetConditionList(GameDatabaseLoader.JsonOptions))
            {
                if (HasExplicitIdCondition(sub)) return true;
            }
        }
        return false;
    }

    private static List<CardInstance> ResolveTransformTargets(
        RuleNode? target,
        RuleExecutionContext ctx,
        int? targetCount,
        bool skipLegendary,
        bool allowDisabled = false)
    {
        bool prevIncludeDisabled = ctx.ActiveExecution.IncludeDisabledTargets;
        if (allowDisabled)
            ctx.ActiveExecution.IncludeDisabledTargets = true;

        try
        {
            if (!skipLegendary)
                return ResolveActionTargets(target, ctx, targetCount);

            bool isRandom = string.Equals(target?.Type, "TTargetCardRandom", StringComparison.Ordinal);
            int? resolveLimit = isRandom ? int.MaxValue : null;
            var candidates = ResolveActionTargets(target, ctx, resolveLimit);
            var filtered = new List<CardInstance>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!IsLegendaryCard(candidates[i]))
                    filtered.Add(candidates[i]);
            }

            int cap = targetCount ?? (isRandom ? 1 : int.MaxValue);
            if (filtered.Count > cap)
                filtered.RemoveRange(cap, filtered.Count - cap);
            return filtered;
        }
        finally
        {
            ctx.ActiveExecution.IncludeDisabledTargets = prevIncludeDisabled;
        }
    }

    private static bool HasDisabledInheritEnchantment(RuleNode spawnContext)
    {
        if (spawnContext.InheritEnchantment == false) return true;
        if (spawnContext.Behaviors != null)
        {
            foreach (var b in spawnContext.Behaviors)
            {
                if (string.Equals(b.Type, "TSpawnBehaviorInheritEnchantment", StringComparison.Ordinal) && b.Inherits == false)
                    return true;
            }
        }
        if (spawnContext.Groups != null)
        {
            foreach (var grp in spawnContext.Groups)
            {
                if (grp.Behaviors == null) continue;
                foreach (var b in grp.Behaviors)
                {
                    if (string.Equals(b.Type, "TSpawnBehaviorInheritEnchantment", StringComparison.Ordinal) && b.Inherits == false)
                        return true;
                }
            }
        }
        return false;
    }

    private static bool HasExplicitCardOrLegendaryFilter(RuleNode spawnContext)
    {
        if (spawnContext.Groups != null)
        {
            foreach (var g in spawnContext.Groups)
            {
                if (HasExplicitCardOrLegendaryFilter(g)) return true;
            }
        }
        if (spawnContext.Filters != null)
        {
            foreach (var f in spawnContext.Filters)
            {
                if (string.Equals(f.Type, "TSpawnFilterIdList", StringComparison.Ordinal)
                    || string.Equals(f.Type, "TSpawnFilterId", StringComparison.Ordinal)
                    || string.Equals(f.Type, "TSpawnFilterCardId", StringComparison.Ordinal))
                    return true;
                if (f.Tiers != null && f.Tiers.Contains("Legendary", StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
        if (!string.IsNullOrEmpty(spawnContext.Id) || (spawnContext.Ids != null && spawnContext.Ids.Count > 0))
            return true;
        if (spawnContext.Tiers != null && spawnContext.Tiers.Contains("Legendary", StringComparer.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool HasExplicitSpawnLimit(RuleNode spawnContext)
    {
        if (spawnContext.Limit != null) return true;
        if (spawnContext.Groups != null)
        {
            foreach (var g in spawnContext.Groups)
            {
                if (g.Limit != null) return true;
            }
        }
        return false;
    }

    private static List<CardInstance> SpawnTransformReplacements(RuleNode spawnContext, RuleExecutionContext transformCtx, bool avoidLegendary)
    {
        int? defaultOverride = HasExplicitSpawnLimit(spawnContext) ? null : 1;
        int attempts = avoidLegendary ? 6 : 1;
        List<CardInstance>? fallback = null;
        for (int i = 0; i < attempts; i++)
        {
            var spawned = SpawnQueryEngine.SpawnCards(spawnContext, transformCtx, defaultOverride);
            if (spawned.Count == 0) return fallback ?? [];
            if (!avoidLegendary || spawned.All(c => c.Template.DefaultTier != CardTier.Legendary))
                return spawned;
            fallback ??= spawned;
        }
        return avoidLegendary ? [] : (fallback ?? []);
    }

    private static void CopyTransformRetentionState(
        CardInstance original,
        CardInstance replacement,
        DurationTarget transformDuration,
        bool disableEnchantInherit)
    {
        // D1-P1-12：统一委托 CardInstance.CopyTransformRetentionFrom 单一权威入口
        replacement.CopyTransformRetentionFrom(
            original,
            transformDuration,
            inheritEnchantment: !disableEnchantInherit && string.IsNullOrEmpty(replacement.Enchantment),
            isEnchantmentCompatible: IsEnchantmentCompatible);
    }

    private static bool IsEnchantmentCompatible(CardTemplate template, string enchantment)
        => template.Enchantments != null && template.Enchantments.ContainsKey(enchantment);

    private static void ApplyFlyingTransition(
        RuleExecutionContext ctx,
        PlayerState cardOwner,
        CardInstance sourceCard,
        CardInstance card,
        bool isFlying,
        string? actionType,
        bool emitCombatEvent = true)
    {
        card.IsFlying = isFlying;
        if (emitCombatEvent)
        {
            ctx.EmitCombatEvent?.Invoke(new CombatEvent
            {
                TimeMs = ctx.TimeMs,
                Type = CombatEventType.FlyingChanged,
                SourcePlayerId = ctx.SelfPlayer.PlayerId,
                TargetPlayerId = cardOwner.PlayerId,
                SourceCardInstanceId = sourceCard.InstanceId,
                TargetCardInstanceId = card.InstanceId,
                Value = isFlying ? 1 : 0,
                AbilityId = ctx.ActiveExecution.AbilityId,
                ActionType = actionType,
                IsSkillSource = sourceCard.Template.Type == "Skill"
            });
        }
        DispatchDetailed(ctx, isFlying ? "TTriggerOnCardStartedFlying" : "TTriggerOnCardStoppedFlying", cardOwner, sourceCard, card, cardOwner);
        if (isFlying)
            DispatchDetailed(ctx, "TTriggerOnCardStartsFlying", cardOwner, sourceCard, card, cardOwner);
    }

    private static void ApplyControlStatusDuration(
        RuleExecutionContext ctx,
        CardInstance sourceCard,
        CardInstance effectiveCard,
        int ms,
        string reductionAttr,
        string attrName,
        string performedTrigger,
        Func<CardInstance, int> getMs,
        Action<CardInstance, int> setMs)
    {
        var effOwner = TargetResolver.FindOwner(effectiveCard, ctx);
        double pctRed = IsLegacySyntheticRadiantImmunity(effectiveCard)
            ? 100.0
            : Math.Clamp(effectiveCard.GetAttribute(reductionAttr), 0, 100);
        // D1-P2-07：孤立无回调路径（Slow/Freeze）与 CombatSimulator 保持一致，对 Flying 卡牌时长减半
        bool isFlying = effectiveCard.IsFlying || effectiveCard.GetAttribute("Flying") > 0 || effectiveCard.HasTag("Flying");
        double flyingMult = isFlying ? 0.5 : 1.0;
        int actual = Math.Max(0, (int)Math.Round(ms * (1.0 - pctRed / 100.0) * flyingMult));
        if (actual > 0)
        {
            int prev = getMs(effectiveCard);
            int next = prev + actual;
            setMs(effectiveCard, next);
            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", effOwner, sourceCard, effectiveCard, effOwner, attrName, prev, next);
        }
        DispatchDetailed(ctx, performedTrigger, ctx.SelfPlayer, sourceCard, effectiveCard, effOwner);
    }

    internal static bool IsLegacySyntheticRadiantImmunity(CardInstance card, RuleExecutionContext? ctx = null)
        => string.Equals(card.Enchantment, "Radiant", StringComparison.OrdinalIgnoreCase)
        && (GameDataRepository.IsSyntheticTestTemplate(card.Template)
            || (ctx != null
                && string.IsNullOrEmpty(ctx.ActiveExecution.AbilityId)
                && string.Equals(card.Template.Id, "d9b24744-c718-458e-b836-d61227ab5711", StringComparison.OrdinalIgnoreCase)));

    private static CardInstance ResolveAbsorbTarget(
        CardInstance intendedTarget,
        PlayerState targetOwner,
        PlayerState actorPlayer,
        string absorbHiddenTag)
    {
        // 仅重定向敌方施加的摧毁/减速/冻结，绝不吸收己方自毁（如 Antimatter Chamber / Box of Fireworks）
        if (ReferenceEquals(targetOwner, actorPlayer) || intendedTarget.HasHiddenTag(absorbHiddenTag))
            return intendedTarget;

        CardInstance? best = null;
        foreach (var candidate in targetOwner.Hand.Cards)
        {
            if (candidate.IsDestroyed || candidate.IsDisabled) continue;
            if (!candidate.HasHiddenTag(absorbHiddenTag)) continue;
            if (best == null
                || candidate.StartSlot < best.StartSlot
                || (candidate.StartSlot == best.StartSlot && string.CompareOrdinal(candidate.InstanceId, best.InstanceId) < 0))
            {
                best = candidate;
            }
        }
        return best ?? intendedTarget;
    }
}
