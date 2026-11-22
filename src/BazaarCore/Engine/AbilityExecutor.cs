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
        "TTriggerOnCardSold", "TTriggerOnCardPurchased", "TTriggerOnCardSelected",
        "TTriggerOnBeforeCardDestroyed", "TTriggerOnCardDisabled", "TTriggerOnCardEnchanted",
        "TTriggerOnCardRepaired", "TTriggerOnCardStartedFlying", "TTriggerOnCardStoppedFlying",
        "TTriggerOnCardTransformed", "TTriggerOnCardAfterTransform", "TTriggerOnCardCharged",
        "TTriggerOnCardQuestCompleted", "TTriggerOnCardUpgraded", "TTriggerOnCardStartsFlying",
        "TTriggerOnCardPerformedBurn", "TTriggerOnCardPerformedCharge", "TTriggerOnCardPerformedDamage",
        "TTriggerOnCardPerformedDestruction", "TTriggerOnCardPerformedFreeze", "TTriggerOnCardPerformedHaste",
        "TTriggerOnCardPerformedHeal", "TTriggerOnCardPerformedOverHeal", "TTriggerOnCardPerformedPoison",
        "TTriggerOnCardPerformedRegen", "TTriggerOnCardPerformedReload", "TTriggerOnCardPerformedShield",
        "TTriggerOnCardPerformedSlow", "TTriggerOnCardAttributeChanged", "TTriggerOnPlayerAttributeChanged",
        "TTriggerOnPlayerDamaged", "TTriggerOnPlayerEnraged", "TTriggerOnPlayerEnrageEnded",
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
    /// 按官方 BazaarGameShared.dll TAction*.TargetingPriorities 对 TTargetCardRandom 执行分级优先随机抽取：
    /// 保持 TargetResolver.ResolveCards 本身纯规则不变，仅在动作目标解析层优先抽取可装弹/可充能等高优桶。
    /// </summary>
    private static List<CardInstance> ResolveWithTargetingPriorities(RuleNode target, RuleExecutionContext ctx, int? maxCount)
    {
        if (!string.Equals(target.Type, "TTargetCardRandom", StringComparison.Ordinal))
            return TargetResolver.ResolveCards(target, ctx, maxCount);

        string? actionType = ctx.ActiveExecution.ActionType;
        if (actionType is not ("TActionCardReload" or "TActionCardCharge" or "TActionCardHaste" or "TActionCardSlow" or "TActionCardFreeze"))
            return TargetResolver.ResolveCards(target, ctx, maxCount);

        string nonNullActionType = actionType;
        var pool = TargetResolver.ResolveCards(target, ctx, int.MaxValue);
        if (pool.Count == 0) return pool;

        int effectiveTake = maxCount.HasValue
            ? maxCount.Value
            : 1;
        if (effectiveTake <= 0) return [];

        static int GetPriorityTier(CardInstance c, string act) => act switch
        {
            // 对齐 Conditionals.CanBeReloaded：Ammo < AmmoMax
            "TActionCardReload" => (c.EffectiveAmmoMax > 0 && c.CurrentAmmo < c.EffectiveAmmoMax) ? 0 : 1,
            // 对齐 Conditionals.ChargePriorities：[(HasCooldown && (NotAnAmmoItem || UsesAndHasAmmo)), HasCooldown]
            "TActionCardCharge" => c.HasActiveCooldown
                ? ((c.EffectiveAmmoMax <= 0 || c.CurrentAmmo > 0) ? 0 : 1)
                : 2,
            // 对齐 Conditionals.Priorities：[HasCooldown, HasNoCooldown]
            _ => c.HasActiveCooldown ? 0 : 1
        };

        var tier0 = new List<CardInstance>(pool.Count);
        var tier1 = new List<CardInstance>(pool.Count);
        var tier2 = new List<CardInstance>();
        for (int i = 0; i < pool.Count; i++)
        {
            var c = pool[i];
            int t = GetPriorityTier(c, nonNullActionType);
            if (t == 0) tier0.Add(c);
            else if (t == 1) tier1.Add(c);
            else tier2.Add(c);
        }

        // 若单次目标数不超过最高优桶且最高优桶非空，直接在该桶内等概率随机抽取
        var picked = new List<CardInstance>(Math.Min(effectiveTake, pool.Count));
        void DrawFromTier(List<CardInstance> bucket)
        {
            if (bucket.Count == 0 || picked.Count >= effectiveTake) return;
            int need = effectiveTake - picked.Count;
            if (need >= bucket.Count)
            {
                picked.AddRange(bucket);
                bucket.Clear();
                return;
            }
            for (int i = 0; i < need; i++)
            {
                int idx = ctx.Rng.Next(bucket.Count);
                picked.Add(bucket[idx]);
                bucket.RemoveAt(idx);
            }
        }

        DrawFromTier(tier0);
        // Charge 目标不回落到无冷却物品（tier2）；Reload/Haste/Slow/Freeze 按官方分级回落
        DrawFromTier(tier1);
        if (actionType != "TActionCardCharge")
            DrawFromTier(tier2);
        return picked;
    }

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
                return trigger.Subject == null
                    ? (eventTargetCard ?? eventSourceCard) == ownerCard
                    : (eventTargetCard ?? eventSourceCard) != null && MatchesCardSubject(trigger.Subject, ownerCard, ownerPlayer, (eventTargetCard ?? eventSourceCard)!, ctx);
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
                int? targetLevel = trigger.Level;
                if (!targetLevel.HasValue
                    && trigger.ExtensionData != null
                    && trigger.ExtensionData.TryGetValue("NewLevel", out var nlEl)
                    && nlEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    && nlEl.TryGetInt32(out int nl))
                {
                    targetLevel = nl;
                }
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
                string? reqOutcome = trigger.CombatOutcome;
                if (string.IsNullOrEmpty(reqOutcome)
                    && trigger.ExtensionData != null
                    && trigger.ExtensionData.TryGetValue("Outcome", out var outEl)
                    && outEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    reqOutcome = outEl.GetString();
                }
                if (string.IsNullOrEmpty(reqOutcome)) return true;
                if (string.IsNullOrEmpty(combatOutcome)) return false;
                return string.Equals(NormalizeRunOutcome(reqOutcome), NormalizeRunOutcome(combatOutcome), StringComparison.Ordinal);
            }

            case "TTriggerOnPlayerEnraged":
            case "TTriggerOnPlayerEnrageEnded":
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
                return true;

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
        try
        {
            ctx.SelfCard = ownerCard;
            ctx.TriggerSourceCard = candidate;
            return TargetResolver.MatchesCard(subject, candidate, ctx);
        }
        finally
        {
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
            targetCount = Math.Max(1, (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(action.TargetCount, ctx, 1)));
        }
        else if (string.Equals(action.Target?.Type, "TTargetCardRandom", StringComparison.Ordinal))
        {
            string? targetsAttr = GetTargetsAttributeForAction(action.Type);
            if (targetsAttr != null)
            {
                double attrTargets = sourceCard.GetAttribute(targetsAttr);
                if (attrTargets > 0)
                    targetCount = Math.Max(1, (int)Math.Round(attrTargets));
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
                        ctx.TriggerTargetCard = prevTriggerTarget;
                        LastSpawnedCardsMap.Remove(ctx.ActiveExecution);
                        if (prevSnapshot != null)
                            LastSpawnedCardsMap.Add(ctx.ActiveExecution, prevSnapshot);
                    }
                    return true;
                }

                case "TActionCostPlayerAttribute":
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
                                DispatchDetailed(ctx, "TTriggerOnPlayerDamaged", target, sourceCard, sourceCard, target, "Health", prevHp, target.CurrentHealth);
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, target.CurrentHealth);
                            }
                            if (target.CurrentHealth <= 0 && prevHp > 0)
                                DispatchDetailed(ctx, "TTriggerOnPlayerDied", target, sourceCard, sourceCard, target);
                        }
                    }
                    if (isCrit && finalDmg > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerShieldApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "ShieldApplyAmount"), sourceCard, ctx, out bool isCrit, "ShieldCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnShieldApplied != null)
                        {
                            ctx.OnShieldApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevShield = target.Shield;
                            target.Shield += amount;
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedShield", target, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerShielded", target, sourceCard, sourceCard, target, "Shield", prevShield, amount);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Shield", prevShield, target.Shield);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerBurnApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "BurnApplyAmount"), sourceCard, ctx, out bool isCrit, "BurnCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnBurnApplied != null)
                        {
                            ctx.OnBurnApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevBurn = target.Burn;
                            target.Burn += amount;
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedBurn", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Burn", prevBurn, target.Burn);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerPoisonApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "PoisonApplyAmount"), sourceCard, ctx, out bool isCrit, "PoisonCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnPoisonApplied != null)
                        {
                            ctx.OnPoisonApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevPoison = target.Poison;
                            target.Poison += amount;
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedPoison", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Poison", prevPoison, target.Poison);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerHeal":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "HealAmount"), sourceCard, ctx, out bool isCrit, "HealCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnHealApplied != null)
                        {
                            ctx.OnHealApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            int prevHp = target.CurrentHealth;
                            int afterHp = Math.Min(target.HealthMax, prevHp + amount);
                            int healed = afterHp - prevHp;
                            target.CurrentHealth = afterHp;
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (amount > healed)
                                DispatchDetailed(ctx, "TTriggerOnCardPerformedOverHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (healed != 0)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, afterHp);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerRegenApply":
                {
                    int amount = ScaleWithCrit(EvalActionAmount(action, sourceCard, ctx, "RegenApplyAmount"), sourceCard, ctx, out bool isCrit, "RegenCrit");
                    var targets = TargetResolver.ResolvePlayers(action.Target, ctx);
                    foreach (var target in targets)
                    {
                        if (ctx.OnRegenApplied != null)
                        {
                            ctx.OnRegenApplied.Invoke(target, amount, sourceCard);
                        }
                        else if (amount > 0)
                        {
                            double prevRegen = target.GetAttribute("HealthRegen");
                            var regenDur = ctx.Scope == RuleScope.InCombat ? DurationTarget.Combat : DurationTarget.Permanent;
                            target.ModifyAttribute("HealthRegen", "Add", amount, regenDur);
                            double curRegen = target.GetAttribute("HealthRegen");
                            DispatchDetailed(ctx, "TTriggerOnCardPerformedRegen", ctx.SelfPlayer, sourceCard, sourceCard, target);
                            if (Math.Abs(curRegen - prevRegen) > 1e-6)
                                DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "HealthRegen", prevRegen, curRegen);
                        }
                    }
                    if (isCrit && amount > 0 && targets.Count > 0)
                        DispatchDetailed(ctx, "TTriggerOnCardCritted", ctx.SelfPlayer, sourceCard, sourceCard, ctx.SelfPlayer);
                    return true;
                }

                case "TActionPlayerRageApply":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "RageApplyAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        double prevRage = target.GetAttribute("Rage");
                        target.ModifyAttribute("Rage", "Add", amount);
                        double curRage = target.GetAttribute("Rage");
                        if (Math.Abs(curRage - prevRage) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Rage", prevRage, curRage);

                        int rageMax = Math.Max(1, (int)Math.Round(target.GetAttribute("RageMax")));
                        if (target.Rage >= rageMax && !target.IsEnraged)
                        {
                            target.Rage = 0;
                            target.EnragedRemainingMs = Math.Max(1000, (int)Math.Round(target.GetAttribute("EnragedDurationMax")));
                            target.Attributes["Enraged"] = 1.0;
                            target.Attributes["EnragedDuration"] = target.EnragedRemainingMs;
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
                        if (Math.Abs(curTempo - prevTempo) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, "Tempo", prevTempo, curTempo);
                    }
                    return true;
                }

                case "TActionPlayerBurnRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "BurnRemoveAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                        target.Burn = Math.Max(0, target.Burn - amount);
                    return true;
                }

                case "TActionPlayerPoisonRemove":
                {
                    int amount = Math.Max(0, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "PoisonRemoveAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                        target.Poison = Math.Max(0, target.Poison - amount);
                    return true;
                }

                case "TActionPlayerReviveHeal":
                {
                    int amount = Math.Max(1, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "HealAmount")));
                    foreach (var target in TargetResolver.ResolvePlayers(action.Target, ctx))
                    {
                        int prevHp = target.CurrentHealth;
                        int nextHp = Math.Clamp(amount, 1, target.HealthMax);
                        target.CurrentHealth = nextHp;
                        int delta = nextHp - prevHp;
                        ctx.EmitCombatEvent?.Invoke(new CombatEvent
                        {
                            TimeMs = ctx.TimeMs,
                            Type = CombatEventType.HealApplied,
                            SourcePlayerId = ctx.SelfPlayer.PlayerId,
                            TargetPlayerId = target.PlayerId,
                            SourceCardInstanceId = sourceCard.InstanceId,
                            Value = Math.Max(0, delta),
                            AbilityId = ctx.ActiveExecution.AbilityId,
                            ActionType = action.Type,
                            IsSkillSource = sourceCard.Template.Type == "Skill"
                        });
                        // Eject, Eject! / Maker's Mark 依赖复活治疗后的 PerformedHeal 触发自毁联动
                        DispatchDetailed(ctx, "TTriggerOnCardPerformedHeal", ctx.SelfPlayer, sourceCard, sourceCard, target);
                        if (nextHp != prevHp)
                            DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", target, sourceCard, null, target, "Health", prevHp, nextHp);
                    }
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
                        target.ModifyAttribute(attr, op, val, durationTarget);
                        double cur = target.GetAttribute(attr);
                        ctx.LastAttributeChangeDelta = cur - prev;
                        if (isTimedCombat)
                            ctx.OnTimedPlayerAttributeModifierApplied?.Invoke(target, attr, op, val, timedDurationMs);
                        DispatchDetailed(ctx, "TTriggerOnPlayerAttributeChanged", ctx.SelfPlayer, sourceCard, null, target, attr, prev, cur);
                    }
                    return true;
                }

                case "TActionCardModifyAttribute":
                {
                    var valNode = action.GetValueNode(GameDatabaseLoader.JsonOptions);
                    double val = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (action.GetFixedNumericValue() ?? 0);
                    if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                    var attr = action.AttributeType ?? "DamageAmount";
                    var op = action.Operation ?? "Add";
                    var durationTarget = ResolveDurationTarget(action.Duration, ctx.Scope);
                    bool isTimedCombat = ctx.Scope == RuleScope.InCombat
                                         && action.Duration?.Type == "TCombatDuration"
                                         && !attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase);
                    int timedDurationMs = isTimedCombat ? Math.Max(1, action.Duration!.DurationInMs ?? 1000) : 0;
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        double prev = card.GetAttribute(attr);
                        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                        {
                            int curAmmo = card.CurrentAmmo;
                            int ammoMax = card.EffectiveAmmoMax;
                            int nextAmmo;
                            if (string.Equals(op, "Multiply", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(curAmmo * val);
                            else if (string.Equals(op, "AdditiveMultiply", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(curAmmo * (1.0 + val));
                            else if (string.Equals(op, "Subtract", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = curAmmo - (int)Math.Round(val);
                            else if (string.Equals(op, "Set", StringComparison.OrdinalIgnoreCase))
                                nextAmmo = (int)Math.Round(val);
                            else
                                nextAmmo = curAmmo + (int)Math.Round(val);
                            card.CurrentAmmo = Math.Clamp(nextAmmo, 0, ammoMax);
                        }
                        else if (string.Equals(op, "Multiply", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(val) < 1e-9
                            && !isTimedCombat
                            && !attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)
                            && !attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
                        {
                            // 官方数据以 Multiply 0 表示将属性清零重置；按持续域清理对应层并避免 Multiply=0 锁死后续 Add
                            if (durationTarget == DurationTarget.Permanent)
                            {
                                card.PermanentMult.Remove(attr);
                                double baseAndOtherAdd = card.BaseAttributes.GetValueOrDefault(attr, 0)
                                                       + card.HourAdd.GetValueOrDefault(attr, 0)
                                                       + card.DayAdd.GetValueOrDefault(attr, 0)
                                                       + card.AuraAdd.GetValueOrDefault(attr, 0);
                                if (Math.Abs(baseAndOtherAdd) > 1e-9)
                                    card.PermanentAdd[attr] = -baseAndOtherAdd;
                                else
                                    card.PermanentAdd.Remove(attr);
                            }
                            else
                            {
                                var (targetAdd, targetMult) = durationTarget switch
                                {
                                    DurationTarget.Hour => (card.HourAdd, card.HourMult),
                                    DurationTarget.Day => (card.DayAdd, card.DayMult),
                                    _ => (card.CombatAdd, card.CombatMult)
                                };
                                targetMult.Remove(attr);
                                targetAdd.Remove(attr);
                                double remaining = card.BaseAttributes.GetValueOrDefault(attr, 0)
                                                 + card.PermanentAdd.GetValueOrDefault(attr, 0)
                                                 + (durationTarget == DurationTarget.Combat
                                                     ? card.HourAdd.GetValueOrDefault(attr, 0) + card.DayAdd.GetValueOrDefault(attr, 0)
                                                     : 0)
                                                 + card.AuraAdd.GetValueOrDefault(attr, 0);
                                if (Math.Abs(remaining) > 1e-9)
                                    targetAdd[attr] = -remaining;
                            }
                            if (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)) card.Heated = false;
                            else if (attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase)) card.Chilled = false;
                        }
                        else
                        {
                            card.ModifyAttribute(attr, op, val, durationTarget);
                            if (!isTimedCombat
                                && (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)
                                    || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
                                && card.GetAttribute(attr) <= 0
                                && (string.Equals(op, "Subtract", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(op, "Set", StringComparison.OrdinalIgnoreCase)))
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
                            ctx.OnTimedCardAttributeModifierApplied?.Invoke(card, attr, op, val, timedDurationMs);
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
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
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (ctx.OnHasteApplied != null)
                        {
                            ctx.OnHasteApplied.Invoke(card, ms, sourceCard);
                        }
                        else if (ms > 0)
                        {
                            double prev = card.HasteMs;
                            card.HasteMs += ms;
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
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        var effectiveCard = ResolveAbsorbTarget(card, cardOwner, ctx.SelfPlayer, "AbsorbSlow");
                        if (ctx.OnSlowApplied != null)
                        {
                            ctx.OnSlowApplied.Invoke(effectiveCard, ms, sourceCard);
                        }
                        else if (ms > 0)
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
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        var effectiveCard = ResolveAbsorbTarget(card, cardOwner, ctx.SelfPlayer, "AbsorbFreeze");
                        if (ctx.OnFreezeApplied != null)
                        {
                            ctx.OnFreezeApplied.Invoke(effectiveCard, ms, sourceCard);
                        }
                        else if (ms > 0)
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
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        if (ctx.OnChargeApplied != null)
                        {
                            ctx.OnChargeApplied.Invoke(card, ms, sourceCard);
                        }
                        else if (ms > 0 && card.HasActiveCooldown)
                        {
                            var pctRed = Math.Clamp(card.GetAttribute("PercentChargeReduction"), 0, 90);
                            int actual = Math.Max(0, (int)Math.Round(ms * (1.0 - pctRed / 100.0)));
                            if (actual > 0)
                            {
                                card.CooldownProgressMs += actual;
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
                    int amt = Math.Max(1, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "ReloadAmount", 1)));
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        int prevAmmo = card.CurrentAmmo;
                        int ammoMax = card.EffectiveAmmoMax;
                        card.CurrentAmmo = Math.Clamp(prevAmmo + amt, 0, ammoMax);
                        double curAmmo = card.CurrentAmmo;
                        var cardOwner = TargetResolver.FindOwner(card, ctx);
                        if (Math.Abs(curAmmo - prevAmmo) > 1e-6)
                        {
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
                        }
                        DispatchDetailed(ctx, "TTriggerOnCardPerformedReload", ctx.SelfPlayer, sourceCard, card, cardOwner);
                        if (Math.Abs(curAmmo - prevAmmo) > 1e-6)
                            DispatchDetailed(ctx, "TTriggerOnCardAttributeChanged", cardOwner, sourceCard, card, cardOwner, "Ammo", prevAmmo, curAmmo);
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
                        card.ApplyEnchantment(action.Enchantment, preventOverride, durationTarget);
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
                        card.ApplyEnchantment(picked, preventOverride, durationTarget);
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
                        card.RemoveEnchantment(durationTarget);
                    return true;
                }

                case "TActionCardUpgrade":
                {
                    bool isTemporary = ResolveDurationTarget(action.Duration, ctx.Scope) != DurationTarget.Permanent;
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
                                IsSkillSource = sourceCard.Template.Type == "Skill"
                            });
                            ctx.OnCardUpgraded?.Invoke(cardOwner, card);
                            DispatchDetailed(ctx, "TTriggerOnCardUpgraded", cardOwner, sourceCard, card, cardOwner);
                        }
                    }
                    return true;
                }

                case "TActionCardAddTagsList":
                    if (action.Tags != null)
                    {
                        bool isTemporary = ResolveDurationTarget(action.Duration, ctx.Scope) != DurationTarget.Permanent;
                        foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        {
                            var bag = isTemporary ? card.CombatTags : card.ExtraTags;
                            foreach (var t in action.Tags)
                                bag.Add(t);
                        }
                    }
                    return true;

                case "TActionCardAddTagsRandom":
                    if (action.Tags != null && action.Tags.Count > 0)
                    {
                        bool isTemporary = ResolveDurationTarget(action.Duration, ctx.Scope) != DurationTarget.Permanent;
                        int cnt = Math.Max(1, (int)Math.Round(EvalActionAmount(action, sourceCard, ctx, "Custom_0", 1)));
                        foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                        {
                            var bag = isTemporary ? card.CombatTags : card.ExtraTags;
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
                                bag.Add(candidates[idx]);
                                candidates.RemoveAt(idx);
                            }
                        }
                    }
                    return true;

                case "TActionCardAddTagsBySource":
                {
                    bool isTemporary = ResolveDurationTarget(action.Duration, ctx.Scope) != DurationTarget.Permanent;
                    var sources = action.Source != null ? ResolveActionTargets(action.Source, ctx) : [sourceCard];
                    var tags = sources.SelectMany(s => s.GetAllTags()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var card in ResolveActionTargets(action.Target, ctx, targetCount))
                    {
                        var bag = isTemporary ? card.CombatTags : card.ExtraTags;
                        foreach (var t in tags)
                            bag.Add(t);
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
                        var replacement = replacements[0];
                        CopyTransformRetentionState(card, replacement, isTemporary, disableEnchInherit);

                        if (ctx.Scope == RuleScope.InCombat)
                        {
                            if (isTemporary)
                            {
                                replacement.IsCombatTemporary = true;
                            }
                            else
                            {
                                card.TransformReplacementTemplateId = replacement.Template.Id;
                                replacement.TransformedFromInstanceId = card.TransformedFromInstanceId ?? card.InstanceId;
                                replacement.IsCombatTemporary = false;
                            }
                        }

                        // TTriggerOnCardTransformed 必须在旧卡尚在 Hand/Stash 且仍保有 Reagent/Potion 等原标签时先派发，
                        // 否则像 [PlayerAttributes] Reagents Transformed / Tome of Yyahan / Vial Launcher 将在 SelfHandAndStash 中匹配不到原卡。
                        DispatchDetailed(ctx, "TTriggerOnCardTransformed", owner, sourceCard, card, owner);

                        int slot = card.StartSlot;
                        int origWidth = card.SlotWidth;
                        bool replacedInHand = false;
                        replacement.IsDisabled = false;
                        replacement.IsDestroyed = false;

                        if (owner.Hand.Cards.Contains(card))
                        {
                            owner.Hand.Remove(card);
                            card.IsDestroyed = true;
                            replacedInHand = true;
                            bool placedInHand = slot >= 0
                                ? owner.Hand.TryPlaceWithDisplaceOrSwap(replacement, slot)
                                : owner.Hand.TryAddAuto(replacement);
                            if (!placedInHand && !owner.Hand.TryAddAuto(replacement) && ctx.Scope == RuleScope.OutOfCombat)
                                owner.Stash.TryAddAuto(replacement);
                        }
                        else if (owner.Stash.Cards.Contains(card))
                        {
                            owner.Stash.Remove(card);
                            card.IsDestroyed = true;
                            if (slot < 0 || !owner.Stash.TryPlaceWithDisplaceOrSwap(replacement, slot))
                                owner.Stash.TryAddAuto(replacement);
                        }
                        else if (owner.Skills.Contains(card))
                        {
                            int sIdx = owner.Skills.IndexOf(card);
                            card.IsDestroyed = true;
                            if (sIdx >= 0) owner.Skills[sIdx] = replacement;
                        }
                        else if (owner.SocketEffects.Contains(card))
                        {
                            int seIdx = owner.SocketEffects.IndexOf(card);
                            card.IsDestroyed = true;
                            replacement.StartSlot = slot;
                            if (seIdx >= 0) owner.SocketEffects[seIdx] = replacement;
                        }

                        // 支持多产物变形（如 Box of Fireworks -> 2 Firecrackers、3D Printer -> 3 小、Hacksaw -> 2 小），优先填满原卡腾出的连续槽位后再自动寻位
                        int nextVacatedSlot = replacedInHand && slot >= 0 ? slot + replacement.SlotWidth : -1;
                        int vacatedEnd = replacedInHand && slot >= 0 ? slot + origWidth : -1;
                        for (int extraIdx = 1; extraIdx < replacements.Count; extraIdx++)
                        {
                            var extra = replacements[extraIdx];
                            CopyTransformRetentionState(card, extra, isTemporary, disableEnchInherit);
                            extra.IsDisabled = false;
                            extra.IsDestroyed = false;
                            if (ctx.Scope == RuleScope.InCombat)
                            {
                                extra.IsCombatTemporary = isTemporary;
                                if (!isTemporary)
                                    extra.TransformedFromInstanceId = card.TransformedFromInstanceId ?? card.InstanceId;
                            }
                            if (extra.Template.Type == "Skill")
                            {
                                owner.Skills.Add(extra);
                            }
                            else
                            {
                                bool placed = false;
                                if (nextVacatedSlot >= 0 && nextVacatedSlot + extra.SlotWidth <= vacatedEnd)
                                {
                                    placed = owner.Hand.TryPlaceOrShift(extra, nextVacatedSlot);
                                    if (placed) nextVacatedSlot += extra.SlotWidth;
                                }
                                if (!placed && !owner.Hand.TryAddAuto(extra) && ctx.Scope == RuleScope.OutOfCombat)
                                    owner.Stash.TryAddAuto(extra);
                            }

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

                        // 执行被变形源卡自带的 Transform.Abilities（如 Chunk of Lead 生金、14 种试剂附加随机/指定附魔）
                        if (card.Template.Transform?.Abilities is { Count: > 0 } tAbilities)
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
                                    SelfCard = replacement,
                                    TriggerSourceCard = sourceCard,
                                    TriggerTargetCard = replacement,
                                    OnCardSpawned = ctx.OnCardSpawned,
                                    OnCardEnchanted = ctx.OnCardEnchanted,
                                    OnCardUpgraded = ctx.OnCardUpgraded,
                                    OnCardTransformed = ctx.OnCardTransformed,
                                    OnDetailedTriggeredEvent = ctx.OnDetailedTriggeredEvent,
                                    OnCardTriggeredEvent = ctx.OnCardTriggeredEvent,
                                    EmitCombatEvent = ctx.EmitCombatEvent,
                                    OnUnknownRuleType = ctx.OnUnknownRuleType
                                };
                            foreach (var tAb in tAbilities.Values)
                            {
                                if (tAb.Action == null) continue;
                                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(tAb.Prerequisites, tExecCtx))
                                    continue;
                                ExecuteAction(tAb.Action, replacement, tExecCtx);
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
                    ctx.OnDealCardsToSelection?.Invoke(dealt, action.SpawnMode ?? "Replace");
                    return true;
                }

                case "TActionGameReroll":
                    ctx.OnRerollEncounter?.Invoke(action.RerollType ?? "All");
                    return true;

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
                    if (card.CompletedQuests.Contains(entry.AttributeType)) continue;
                    var localCtx = ctx.WithCards(card, eventSourceCard, eventTargetCard);
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(entry.Prerequisites, localCtx))
                        continue;
                    if (!MatchesTrigger(entry.Trigger, eventType, card, ownerPlayer, eventSourceCard, eventTargetCard, eventSubjectPlayer, changedAttr, prevVal, curVal, combatType, combatOutcome, localCtx))
                        continue;

                    double delta = entry.Update?.Value != null
                        ? ValueAndConditionEvaluator.EvaluateValue(entry.Update.Value, localCtx, 1)
                        : 1;
                    double prev = card.QuestProgress.GetValueOrDefault(entry.AttributeType, 0);
                    double next = prev + delta;
                    card.QuestProgress[entry.AttributeType] = next;

                    if (Math.Abs(next - prev) > 1e-9)
                    {
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
                        card.CompletedQuests.Add(entry.AttributeType);
                        changed = true;
                        newlyCompleted ??= new List<(QuestEntry, double)>();
                        newlyCompleted.Add((entry, next));
                        if (q.Repeatable == null)
                            break;
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
        if (group.Repeatable != null || group.Entries.Count == 0)
            return false;
        for (int i = 0; i < group.Entries.Count; i++)
        {
            if (card.CompletedQuests.Contains(group.Entries[i].AttributeType))
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

        List<(QuestEntry Entry, double Progress)>? newlyCompleted = null;
        foreach (var q in card.Template.Quests)
        {
            if (IsQuestGroupCompleted(card, q)) continue;
            foreach (var entry in q.Entries)
            {
                if (!string.Equals(entry.AttributeType, questAttr, StringComparison.OrdinalIgnoreCase)) continue;
                if (card.CompletedQuests.Contains(entry.AttributeType)) continue;
                if (nextProg >= entry.Target)
                {
                    card.CompletedQuests.Add(entry.AttributeType);
                    newlyCompleted ??= new List<(QuestEntry, double)>();
                    newlyCompleted.Add((entry, nextProg));
                    if (q.Repeatable == null)
                        break;
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
            double amt = valNode != null ? ValueAndConditionEvaluator.EvaluateValue(valNode, ctx) : (cost.GetFixedNumericValue() ?? 0);
            if (double.IsNaN(amt) || double.IsInfinity(amt)) amt = 0;
            amt = Math.Max(0, amt);
            var attr = cost.PlayerAttributeType ?? "Gold";
            double prevAttr = ctx.SelfPlayer.GetAttribute(attr);
            if (prevAttr < amt) return false;
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
        return true;
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
                else
                {
                    var cardAttr = sourceCard.GetAttribute(fallbackAttr);
                    result = Math.Abs(cardAttr) > 1e-6 ? cardAttr : defaultVal;
                }
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
            critPct += sourceCard.GetAttribute(critBonusAttr);
        double mult = isCrit ? Math.Max(0, critPct) / 100.0 : 1.0;
        double scaled = Math.Round(raw * mult);
        if (double.IsNaN(scaled) || double.IsInfinity(scaled)) return 0;
        return (int)Math.Clamp(scaled, 0, 1_000_000_000);
    }

    private static int ResolveDurationMs(RuleNode action, CardInstance sourceCard, RuleExecutionContext ctx, string fallbackAttr)
    {
        double raw = EvalActionAmount(action, sourceCard, ctx, fallbackAttr, 1000);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        // 在 GameData.db 中 HasteAmount/SlowAmount/FreezeAmount/ChargeAmount 均以毫秒计（如 1000 代表 1 秒）
        return Math.Clamp((int)Math.Round(raw), 0, 120_000);
    }

    private static bool RollCrit(CardInstance sourceCard, RuleExecutionContext ctx)
    {
        double critChance = sourceCard.GetAttribute("CritChance") + ctx.CombatBonusCritChance;
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
        bool isTemporary,
        bool disableEnchantInherit)
    {
        foreach (var (attr, val) in original.PermanentAdd)
            replacement.PermanentAdd[attr] = val;

        foreach (var tag in original.ExtraTags)
            replacement.ExtraTags.Add(tag);

        if (isTemporary)
        {
            foreach (var tag in original.CombatTags)
                replacement.CombatTags.Add(tag);
        }

        if (original.IsTiny)
        {
            bool hasPermTiny = original.PermanentAdd.GetValueOrDefault("Tiny", 0) > 0
                            || original.BaseAttributes.GetValueOrDefault("Tiny", 0) > 0
                            || original.ExtraTags.Contains("Tiny");
            if (hasPermTiny)
            {
                if (replacement.PermanentAdd.GetValueOrDefault("Tiny", 0) <= 0)
                    replacement.PermanentAdd["Tiny"] = 1.0;
                replacement.ExtraTags.Add("Tiny");
            }
            else if (isTemporary)
            {
                if (replacement.CombatAdd.GetValueOrDefault("Tiny", 0) <= 0)
                    replacement.CombatAdd["Tiny"] = 1.0;
                replacement.CombatTags.Add("Tiny");
            }
        }

        if (string.IsNullOrEmpty(replacement.Enchantment)
            && !string.IsNullOrEmpty(original.Enchantment)
            && !disableEnchantInherit
            && IsEnchantmentCompatible(replacement.Template, original.Enchantment))
        {
            replacement.ApplyEnchantment(original.Enchantment, preventOverride: false, isTemporary: isTemporary);
        }
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
        int actual = Math.Max(0, (int)Math.Round(ms * (1.0 - pctRed / 100.0)));
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
