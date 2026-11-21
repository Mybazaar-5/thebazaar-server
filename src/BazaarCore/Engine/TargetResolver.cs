using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 统一目标解析器：完整对齐官方 BazaarGameShared.dll 全部 11 种 TTarget*（卡片自身/分区/邻接/最边缘/随机/触发源/玩家相对或绝对目标）。
/// </summary>
public static class TargetResolver
{
    public static PlayerState GetLocalPlayer(RuleExecutionContext ctx)
    {
        // 单人局外上下文下，SelfPlayer 始终代表当前正在结算的本地玩家，避免 PlayerId 为 GUID 时因默认值 "player" 失配
        if (ctx.Scope == RuleScope.OutOfCombat && !ctx.SelfPlayer.IsMonster)
            return ctx.SelfPlayer;
        if (string.Equals(ctx.SelfPlayer.PlayerId, ctx.LocalPlayerId, StringComparison.OrdinalIgnoreCase))
            return ctx.SelfPlayer;
        if (ctx.OpponentPlayer != null && string.Equals(ctx.OpponentPlayer.PlayerId, ctx.LocalPlayerId, StringComparison.OrdinalIgnoreCase))
            return ctx.OpponentPlayer;
        if (!ctx.SelfPlayer.IsMonster)
            return ctx.SelfPlayer;
        if (ctx.OpponentPlayer != null && !ctx.OpponentPlayer.IsMonster)
            return ctx.OpponentPlayer;
        return ctx.SelfPlayer;
    }

    private static PlayerState? GetEncounterPlayer(RuleExecutionContext ctx)
    {
        var local = GetLocalPlayer(ctx);
        return ReferenceEquals(local, ctx.SelfPlayer) ? ctx.OpponentPlayer : ctx.SelfPlayer;
    }

    private static PlayerState? GetRelativeOpponent(RuleExecutionContext ctx, PlayerState? targetingOwner)
    {
        var local = GetLocalPlayer(ctx);
        if (targetingOwner != null && !ReferenceEquals(targetingOwner, local))
            return local;
        return GetEncounterPlayer(ctx);
    }

    public static List<PlayerState> ResolvePlayers(RuleNode? target, RuleExecutionContext ctx)
    {
        if (target == null)
            return [ctx.SelfPlayer];

        string mode = target.TargetMode ?? string.Empty;
        var candidates = new List<PlayerState>(2);

        switch (target.Type)
        {
            case "TTargetPlayerAbsolute":
            {
                var local = GetLocalPlayer(ctx);
                var enc = GetEncounterPlayer(ctx);
                if (mode.Equals("Encounter", StringComparison.OrdinalIgnoreCase)
                    || mode.Equals("Opponent", StringComparison.OrdinalIgnoreCase))
                {
                    if (enc != null) candidates.Add(enc);
                }
                else
                {
                    candidates.Add(local);
                }
                break;
            }

            case "TTargetPlayer":
            {
                if (ctx.SelfCard == null)
                    return [];
                var owner = FindOwner(ctx.SelfCard, ctx);
                if (mode.Equals("Opponent", StringComparison.OrdinalIgnoreCase))
                {
                    var relOpp = GetRelativeOpponent(ctx, owner);
                    if (relOpp != null) candidates.Add(relOpp);
                }
                else if (mode.Equals("Both", StringComparison.OrdinalIgnoreCase))
                {
                    var local = GetLocalPlayer(ctx);
                    var enc = GetEncounterPlayer(ctx);
                    candidates.Add(local);
                    if (enc != null && !ReferenceEquals(enc, local))
                        candidates.Add(enc);
                }
                else if (mode.Equals("AbsolutePlayer", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(GetLocalPlayer(ctx));
                }
                else
                {
                    candidates.Add(owner);
                }
                break;
            }

            case "TTargetPlayerRelative":
            {
                if (ctx.SelfCard == null && ctx.ActiveExecution.ActionType == null)
                    return [];
                var owner = ctx.SelfCard != null ? FindOwner(ctx.SelfCard, ctx) : ctx.SelfPlayer;
                if (mode.Equals("Opponent", StringComparison.OrdinalIgnoreCase))
                {
                    var relOpp = ctx.SelfCard != null ? GetRelativeOpponent(ctx, owner) : ctx.OpponentPlayer;
                    if (relOpp != null) candidates.Add(relOpp);
                }
                else
                {
                    candidates.Add(owner);
                }
                break;
            }

            default:
            {
                if (mode.Equals("Opponent", StringComparison.OrdinalIgnoreCase))
                {
                    if (ctx.OpponentPlayer != null) candidates.Add(ctx.OpponentPlayer);
                }
                else if (mode.Equals("Both", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(ctx.SelfPlayer);
                    if (ctx.OpponentPlayer != null && !ReferenceEquals(ctx.OpponentPlayer, ctx.SelfPlayer))
                        candidates.Add(ctx.OpponentPlayer);
                }
                else
                {
                    candidates.Add(ctx.SelfPlayer);
                }
                break;
            }
        }

        var cond = target.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (cond == null)
            return candidates;

        return ValueAndConditionEvaluator.FilterPlayerCandidates(cond, candidates, ctx);
    }

    public static List<CardInstance> ResolveCards(RuleNode? target, RuleExecutionContext ctx, int? maxCount = null)
    {
        var result = new List<CardInstance>();
        if (target == null) return result;

        var cond = target.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        bool excludeSelf = target.ExcludeSelf ?? false;
        bool isRepairAction = string.Equals(ctx.ActiveExecution.ActionType, "TActionCardRepair", StringComparison.Ordinal);
        bool isTransformDestroyedAction = string.Equals(ctx.ActiveExecution.ActionType, "TActionCardTransformDestroyed", StringComparison.Ordinal);
        bool includeDestroyed = HasStateCondition(cond, "Destroyed") || HasStateCondition(cond, "Transformed")
                                || isTransformDestroyedAction || isRepairAction;
        bool includeDisabled = HasStateCondition(cond, "Disabled") || isRepairAction
                               || isTransformDestroyedAction || ctx.ActiveExecution.IncludeDisabledTargets;

        switch (target.Type)
        {
            case "TTargetCardSelf":
                if (ctx.SelfCard != null
                    && (includeDestroyed || !ctx.SelfCard.IsDestroyed)
                    && (includeDisabled || !ctx.SelfCard.IsDisabled))
                {
                    AddIfMatch(result, ctx.SelfCard, FindOwner(ctx.SelfCard, ctx), cond, ctx);
                }
                break;

            case "TTargetCardTriggerSource":
                if (ctx.TriggerSourceCard != null
                    && (!excludeSelf || !ReferenceEquals(ctx.TriggerSourceCard, ctx.SelfCard))
                    && (ctx.ActiveExecution.ActionType == null || ((includeDestroyed || !ctx.TriggerSourceCard.IsDestroyed) && (includeDisabled || !ctx.TriggerSourceCard.IsDisabled))))
                {
                    AddIfMatch(result, ctx.TriggerSourceCard, FindOwner(ctx.TriggerSourceCard, ctx), cond, ctx);
                }
                break;

            case "TTargetCardTriggerTarget":
                if (ctx.TriggerTargetCard != null
                    && (!excludeSelf || !ReferenceEquals(ctx.TriggerTargetCard, ctx.SelfCard))
                    && (ctx.ActiveExecution.ActionType == null || ((includeDestroyed || !ctx.TriggerTargetCard.IsDestroyed) && (includeDisabled || !ctx.TriggerTargetCard.IsDisabled))))
                {
                    AddIfMatch(result, ctx.TriggerTargetCard, FindOwner(ctx.TriggerTargetCard, ctx), cond, ctx);
                }
                break;

            case "TTargetCardOccupying":
                if (ctx.SelfCard != null
                    && ctx.SelfCard.StartSlot >= 0
                    && (includeDestroyed || !ctx.SelfCard.IsDestroyed)
                    && (includeDisabled || !ctx.SelfCard.IsDisabled))
                {
                    var owner = FindOwner(ctx.SelfCard, ctx);
                    int selfStart = ctx.SelfCard.StartSlot;
                    int selfEnd = selfStart + ctx.SelfCard.SlotWidth;
                    bool isSocketEffect = IsSocketEffectCard(ctx.SelfCard);
                    var occ = new List<(CardInstance Card, PlayerState Owner)>();

                    if (isSocketEffect)
                    {
                        // 对齐官方 TTargetCardOccupying：SocketEffect 查询其覆盖槽位上的 Hand 物品（不包含其他 SocketEffect）
                        foreach (var c in owner.Hand.Cards)
                        {
                            if (ReferenceEquals(c, ctx.SelfCard) || c.StartSlot < 0) continue;
                            if (c.IsDestroyed) continue;
                            if (c.StartSlot < selfEnd && selfStart < c.StartSlot + c.SlotWidth)
                                occ.Add((c, owner));
                        }
                    }
                    else
                    {
                        // 兼容已有单测：当由 Item 反查占据的 SocketEffect 时扫描同槽位 SocketEffects
                        foreach (var c in owner.SocketEffects)
                        {
                            if (ReferenceEquals(c, ctx.SelfCard) || c.StartSlot < 0) continue;
                            if (!includeDestroyed && c.IsDestroyed) continue;
                            if (c.StartSlot < selfEnd && selfStart < c.StartSlot + c.SlotWidth)
                                occ.Add((c, owner));
                        }
                    }

                    FilterCandidatePairs(occ, cond, ctx, result);
                }
                break;

            case "TTargetCardPositional":
            {
                if (ctx.SelfCard == null) break;
                var origin = target.Origin == "TriggerSource" ? ctx.TriggerSourceCard : ctx.SelfCard;
                var candidates = new List<CardInstance>(4);
                if (origin != null)
                {
                    var originOwner = FindOwner(origin, ctx);
                    candidates.AddRange(ResolvePositionalNeighbors(originOwner, origin, target.TargetMode, includeDestroyed, includeDisabled));
                }

                // 严格对齐官方反编译 TTargetCardPositional.cs:33-36：
                // if (IncludeOrigin && !list.Contains(ctx.TargetingCard)) list.Add(ctx.TargetingCard);
                if (target.IncludeOrigin == true && !candidates.Contains(ctx.SelfCard))
                {
                    candidates.Add(ctx.SelfCard);
                }

                var pairs = new List<(CardInstance Card, PlayerState Owner)>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                    pairs.Add((candidates[i], FindOwner(candidates[i], ctx)));

                FilterCandidatePairs(pairs, cond, ctx, result);
                break;
            }

            case "TTargetCardXMost":
            {
                var pairs = CollectSectionPairs(target.TargetSection, ctx, excludeSelf, includeDestroyed, includeDisabled);
                var activeSold = AbilityExecutor.GetActiveSoldCard(ctx);
                // 过滤未入格卡（StartSlot < 0）及正在出售但尚未从分区摘除的卡牌，防止 -1 或已售卡抢占最左/最右目标
                for (int i = pairs.Count - 1; i >= 0; i--)
                {
                    var c = pairs[i].Card;
                    if (c.StartSlot < 0 || (activeSold != null && ReferenceEquals(c, activeSold)))
                        pairs.RemoveAt(i);
                }
                // 先滤后取最左/最右（经官方反编译 TTargetCardXMost.cs:42-53 核验）
                FilterCandidatePairs(pairs, cond, ctx, result);
                if (result.Count > 1)
                {
                    bool leftMost = string.Equals(target.TargetMode, "LeftMostCard", StringComparison.Ordinal)
                        || string.Equals(target.TargetMode, "LeftMost", StringComparison.Ordinal);
                    CardInstance chosen = result[0];
                    int bestSlot = chosen.StartSlot;
                    for (int i = 1; i < result.Count; i++)
                    {
                        var cand = result[i];
                        // 稳定序：LeftMost 严格小于才替换（保留最先），RightMost 大于等于才替换（保留最后）
                        if (leftMost ? cand.StartSlot < bestSlot : cand.StartSlot >= bestSlot)
                        {
                            bestSlot = cand.StartSlot;
                            chosen = cand;
                        }
                    }
                    result.Clear();
                    result.Add(chosen);
                }
                break;
            }

            case "TTargetCardSection":
            {
                var pairs = CollectSectionPairs(target.TargetSection, ctx, excludeSelf, includeDestroyed, includeDisabled);
                FilterCandidatePairs(pairs, cond, ctx, result);
                break;
            }

            case "TTargetCardRandom":
            {
                if (ctx.SelfCard == null && ctx.ActiveExecution.ActionType == null)
                    break;

                var pairs = CollectSectionPairs(target.TargetSection, ctx, excludeSelf, includeDestroyed, includeDisabled);
                FilterCandidatePairs(pairs, cond, ctx, result);

                int? effectiveTake = maxCount.HasValue
                    ? maxCount.Value
                    : (ctx.ActiveExecution.ActionType != null ? 1 : null);

                if (effectiveTake.HasValue && effectiveTake.Value > 0 && effectiveTake.Value < result.Count)
                {
                    // 对齐官方 TTargetCardRandom.SelectRandomCards：按 SeedManager.GetNumber(cards.Count) 依次抽取并移除
                    var picked = new List<CardInstance>(effectiveTake.Value);
                    for (int i = 0; i < effectiveTake.Value; i++)
                    {
                        int idx = ctx.Rng.Next(result.Count);
                        picked.Add(result[idx]);
                        result.RemoveAt(idx);
                    }
                    return picked;
                }
                return result;
            }
        }

        if (maxCount.HasValue && maxCount.Value > 0 && result.Count > maxCount.Value)
            result.RemoveRange(maxCount.Value, result.Count - maxCount.Value);

        return result;
    }

    /// <summary>
    /// 零分配快速目标判定路径（供高频触发过滤 MatchesCardSubject 调用）：
    /// 对 Self / TriggerSource / TriggerTarget / 无极值条件的 Section / Random / Positional / Occupying 直接判定，避免每事件分配完整候选列表。
    /// </summary>
    public static bool MatchesCard(RuleNode? target, CardInstance candidate, RuleExecutionContext ctx)
    {
        if (target == null || candidate == null) return false;

        var cond = target.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (cond != null && ValueAndConditionEvaluator.ContainsExtremeCondition(cond))
            return ResolveCards(target, ctx).Contains(candidate);

        bool excludeSelf = target.ExcludeSelf ?? false;
        bool isRepairAction = string.Equals(ctx.ActiveExecution.ActionType, "TActionCardRepair", StringComparison.Ordinal);
        bool isTransformDestroyedAction = string.Equals(ctx.ActiveExecution.ActionType, "TActionCardTransformDestroyed", StringComparison.Ordinal);
        bool includeDestroyed = HasStateCondition(cond, "Destroyed") || HasStateCondition(cond, "Transformed")
                                || isTransformDestroyedAction || isRepairAction;
        bool includeDisabled = HasStateCondition(cond, "Disabled") || isRepairAction
                               || isTransformDestroyedAction || ctx.ActiveExecution.IncludeDisabledTargets;

        bool IsEligible(CardInstance c) => (includeDestroyed || !c.IsDestroyed) && (includeDisabled || !c.IsDisabled);

        switch (target.Type)
        {
            case "TTargetCardSelf":
                if (!ReferenceEquals(candidate, ctx.SelfCard) || !IsEligible(candidate))
                    return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, FindOwner(candidate, ctx), ctx);

            case "TTargetCardTriggerSource":
                if (!ReferenceEquals(candidate, ctx.TriggerSourceCard)) return false;
                if (excludeSelf && ReferenceEquals(candidate, ctx.SelfCard)) return false;
                if (ctx.ActiveExecution.ActionType != null && !IsEligible(candidate)) return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, FindOwner(candidate, ctx), ctx);

            case "TTargetCardTriggerTarget":
                if (!ReferenceEquals(candidate, ctx.TriggerTargetCard)) return false;
                if (excludeSelf && ReferenceEquals(candidate, ctx.SelfCard)) return false;
                if (ctx.ActiveExecution.ActionType != null && !IsEligible(candidate)) return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, FindOwner(candidate, ctx), ctx);

            case "TTargetCardSection":
            case "TTargetCardRandom":
            {
                if (target.Type == "TTargetCardRandom" && ctx.SelfCard == null && ctx.ActiveExecution.ActionType == null)
                    return false;
                if (excludeSelf && ReferenceEquals(candidate, ctx.SelfCard))
                    return false;
                if (!IsCardInSection(target.TargetSection, candidate, ctx, includeDestroyed, includeDisabled, out var matchedOwner))
                    return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, matchedOwner!, ctx);
            }

            case "TTargetCardPositional":
            {
                if (ctx.SelfCard == null) return false;
                var origin = target.Origin == "TriggerSource" ? ctx.TriggerSourceCard : ctx.SelfCard;
                if (origin == null) return false;
                bool hit = false;
                if (target.IncludeOrigin == true && ReferenceEquals(candidate, origin) && IsEligible(origin))
                {
                    hit = true;
                }
                else
                {
                    var originOwner = FindOwner(origin, ctx);
                    foreach (var n in ResolvePositionalNeighbors(originOwner, origin, target.TargetMode, includeDestroyed, includeDisabled))
                    {
                        if (ReferenceEquals(n, candidate))
                        {
                            hit = true;
                            break;
                        }
                    }
                }
                if (!hit) return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, FindOwner(candidate, ctx), ctx);
            }

            case "TTargetCardOccupying":
            {
                if (ctx.SelfCard == null || ctx.SelfCard.StartSlot < 0 || !IsEligible(ctx.SelfCard))
                    return false;
                if (ReferenceEquals(candidate, ctx.SelfCard) || candidate.StartSlot < 0)
                    return false;
                var owner = FindOwner(ctx.SelfCard, ctx);
                int selfStart = ctx.SelfCard.StartSlot;
                int selfEnd = selfStart + ctx.SelfCard.SlotWidth;
                if (!(candidate.StartSlot < selfEnd && selfStart < candidate.StartSlot + candidate.SlotWidth))
                    return false;
                bool inContainer = IsSocketEffectCard(ctx.SelfCard)
                    ? (!candidate.IsDestroyed && owner.Hand.Cards.Contains(candidate))
                    : ((includeDestroyed || !candidate.IsDestroyed) && owner.SocketEffects.Contains(candidate));
                if (!inContainer) return false;
                return cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, candidate, owner, ctx);
            }

            default:
                return ResolveCards(target, ctx).Contains(candidate);
        }
    }

    private static bool IsCardInSection(
        string? section,
        CardInstance candidate,
        RuleExecutionContext ctx,
        bool includeDestroyed,
        bool includeDisabled,
        out PlayerState? matchedOwner)
    {
        matchedOwner = null;
        bool eligible = (includeDestroyed || !candidate.IsDestroyed) && (includeDisabled || !candidate.IsDisabled);

        bool InHand(PlayerState? p) => p != null && eligible && p.Hand.Cards.Contains(candidate);
        bool InStash(PlayerState? p) => p != null && eligible && p.Stash.Cards.Contains(candidate);
        bool InSkills(PlayerState? p) => p != null && eligible && p.Skills.Contains(candidate);
        bool InSocketEffects(PlayerState? p) => p != null && eligible && p.SocketEffects.Contains(candidate);
        bool InBoard(PlayerState? p) => InHand(p) || InStash(p) || InSkills(p);

        var local = GetLocalPlayer(ctx);
        var oppOfLocal = GetEncounterPlayer(ctx);
        bool selfCardUnowned = ctx.SelfCard != null
            && (IsUnownedSystemCard(ctx.SelfCard) || (!OwnsCard(ctx.SelfPlayer, ctx.SelfCard) && !OwnsCard(ctx.OpponentPlayer, ctx.SelfCard)));
        var selfOwner = ctx.SelfCard != null
            ? (selfCardUnowned ? ctx.SelfPlayer : FindOwner(ctx.SelfCard, ctx))
            : (ctx.Scope == RuleScope.OutOfCombat ? ctx.SelfPlayer : local);
        var relOpponent = ctx.SelfCard != null ? GetRelativeOpponent(ctx, selfOwner) : local;

        switch (section)
        {
            case "SelfHand":
                if (InHand(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "SelfBoard":
                if (InBoard(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "SelfStash":
                if (InStash(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "SelfHandAndStash":
                if (InHand(selfOwner) || InStash(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "SelfSocketEffects":
                if (InSocketEffects(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "SelfSkills":
                if (InSkills(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
            case "AbsolutePlayerHand":
                if (InHand(local)) { matchedOwner = local; return true; }
                return false;
            case "AbsolutePlayerStash":
                if (InStash(local)) { matchedOwner = local; return true; }
                return false;
            case "AbsolutePlayerHandAndStash":
                if (InHand(local) || InStash(local)) { matchedOwner = local; return true; }
                return false;
            case "AbsolutePlayerSkills":
                if (InSkills(local)) { matchedOwner = local; return true; }
                return false;
            case "AbsolutePlayerSocketEffects":
                if (InSocketEffects(local)) { matchedOwner = local; return true; }
                return false;
            case "AbsoluteOpponentHand":
                if (InHand(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                return false;
            case "AbsoluteOpponentStash":
                if (InStash(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                return false;
            case "AbsoluteOpponentHandAndStash":
                if (InHand(oppOfLocal) || InStash(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                return false;
            case "SelfNeighbors":
                if (ctx.SelfCard != null)
                {
                    var owner = FindOwner(ctx.SelfCard, ctx);
                    foreach (var c in ResolvePositionalNeighbors(owner, ctx.SelfCard, "Neighbor", includeDestroyed, includeDisabled))
                    {
                        if (ReferenceEquals(c, candidate)) { matchedOwner = owner; return true; }
                    }
                }
                return false;
            case "TriggerSourceNeighbors":
                if (ctx.TriggerSourceCard != null)
                {
                    var owner = FindOwner(ctx.TriggerSourceCard, ctx);
                    foreach (var c in ResolvePositionalNeighbors(owner, ctx.TriggerSourceCard, "Neighbor", includeDestroyed, includeDisabled))
                    {
                        if (ReferenceEquals(c, candidate)) { matchedOwner = owner; return true; }
                    }
                }
                return false;
            case "OpponentHand":
                if (InHand(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "OpponentStash":
                if (InStash(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "OpponentHandAndStash":
                if (InHand(relOpponent) || InStash(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "OpponentSocketEffects":
                if (InSocketEffects(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "OpponentSkills":
                if (InSkills(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "OpponentBoard":
                if (InBoard(relOpponent)) { matchedOwner = relOpponent; return true; }
                return false;
            case "AllHands":
                if (InHand(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                if (InHand(local)) { matchedOwner = local; return true; }
                return false;
            case "AllStashes":
                if (InStash(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                if (InStash(local)) { matchedOwner = local; return true; }
                return false;
            case "AllHandsAndStashes":
                if (InHand(oppOfLocal) || InStash(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                if (InHand(local) || InStash(local)) { matchedOwner = local; return true; }
                return false;
            case "AllHandsAndSkills":
                if (InHand(local) || InSkills(local)) { matchedOwner = local; return true; }
                if (InHand(oppOfLocal) || InSkills(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                return false;
            case "AllSocketEffects":
                if (InSocketEffects(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                if (InSocketEffects(local)) { matchedOwner = local; return true; }
                return false;
            case "AllBoards":
                if (InBoard(local)) { matchedOwner = local; return true; }
                if (InBoard(oppOfLocal)) { matchedOwner = oppOfLocal; return true; }
                return false;
            case "SelectionSet":
                if (ctx.SelectionCards != null)
                {
                    for (int i = 0; i < ctx.SelectionCards.Count; i++)
                    {
                        if (ReferenceEquals(ctx.SelectionCards[i], candidate))
                        {
                            matchedOwner = FindOwner(candidate, ctx);
                            return true;
                        }
                    }
                }
                return false;
            default:
                if (InHand(selfOwner)) { matchedOwner = selfOwner; return true; }
                return false;
        }
    }

    private static bool IsSocketEffectCard(CardInstance card)
        => card.Template.Type is "SocketEffect" or "MusicNoteSocketEffect"
        || card.Template.TypeDiscriminator is "TCardSocketEffect" or "TCardMusicNoteSocketEffect";

    private static void AddIfMatch(List<CardInstance> list, CardInstance c, PlayerState owner, RuleNode? cond, RuleExecutionContext ctx)
    {
        if (cond == null || ValueAndConditionEvaluator.EvaluateCardCondition(cond, c, owner, ctx))
            list.Add(c);
    }

    private static IEnumerable<CardInstance> ResolvePositionalNeighbors(
        PlayerState owner,
        CardInstance origin,
        string? targetMode,
        bool includeDestroyed,
        bool includeDisabled)
    {
        bool IsEligible(CardInstance c) => (includeDestroyed || !c.IsDestroyed) && (includeDisabled || !c.IsDisabled);

        if (owner.SocketEffects.Contains(origin) || IsSocketEffectCard(origin))
        {
            if (origin.StartSlot < 0) yield break;
            var activeSockets = owner.SocketEffects
                .Where(se => !se.IsDestroyed && se.StartSlot >= 0)
                .OrderBy(se => se.StartSlot)
                .ToList();

            int leftIdx = origin.StartSlot - 1;
            int rightIdx = origin.StartSlot + origin.SlotWidth;

            CardInstance? FindSocketAt(int slot)
                => activeSockets.FirstOrDefault(se => !ReferenceEquals(se, origin) && slot >= se.StartSlot && slot < se.StartSlot + se.SlotWidth);

            switch (targetMode)
            {
                case "LeftCard":
                    if (leftIdx >= 0 && FindSocketAt(leftIdx) is { } ls && IsEligible(ls)) yield return ls;
                    break;
                case "RightCard":
                    if (FindSocketAt(rightIdx) is { } rs && IsEligible(rs)) yield return rs;
                    break;
                case "AllLeftCards":
                    foreach (var se in activeSockets)
                    {
                        if (!ReferenceEquals(se, origin) && se.StartSlot + se.SlotWidth <= origin.StartSlot && IsEligible(se))
                            yield return se;
                    }
                    break;
                case "AllRightCards":
                    foreach (var se in activeSockets)
                    {
                        if (!ReferenceEquals(se, origin) && se.StartSlot >= rightIdx && IsEligible(se))
                            yield return se;
                    }
                    break;
                default:
                    if (leftIdx >= 0 && FindSocketAt(leftIdx) is { } nls && IsEligible(nls)) yield return nls;
                    if (FindSocketAt(rightIdx) is { } nrs && IsEligible(nrs)) yield return nrs;
                    break;
            }
            yield break;
        }

        BoardGrid? grid = owner.Hand.Cards.Contains(origin)
            ? owner.Hand
            : (owner.Stash.Cards.Contains(origin) ? owner.Stash : null);
        if (grid == null) yield break;

        switch (targetMode)
        {
            case "LeftCard":
            {
                var l = grid.GetLeftNeighbor(origin, includeDestroyed: true);
                if (l != null && IsEligible(l)) yield return l;
                break;
            }
            case "RightCard":
            {
                var r = grid.GetRightNeighbor(origin, includeDestroyed: true);
                if (r != null && IsEligible(r)) yield return r;
                break;
            }
            case "AllLeftCards":
                foreach (var c in grid.GetAllLeftCards(origin, includeDestroyed: true))
                    if (IsEligible(c)) yield return c;
                break;
            case "AllRightCards":
                foreach (var c in grid.GetAllRightCards(origin, includeDestroyed: true))
                    if (IsEligible(c)) yield return c;
                break;
            default:
            {
                var nl = grid.GetLeftNeighbor(origin, includeDestroyed: true);
                if (nl != null && IsEligible(nl)) yield return nl;
                var nr = grid.GetRightNeighbor(origin, includeDestroyed: true);
                if (nr != null && IsEligible(nr)) yield return nr;
                break;
            }
        }
    }

    private static bool OwnsCard(PlayerState? player, CardInstance card)
        => player != null && (
            player.Hand.Cards.Contains(card) ||
            player.Stash.Cards.Contains(card) ||
            player.Skills.Contains(card) ||
            player.SocketEffects.Contains(card) ||
            player.PlayerEffects.Contains(card));

    private static bool IsUnownedSystemCard(CardInstance card)
        => card.Template.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter" or "CombatEncounter" or "PvpEncounter" or "PlayerEffect";

    public static PlayerState FindOwner(CardInstance card, RuleExecutionContext ctx)
    {
        // 遭遇/步骤/PlayerEffect 系统卡并非棋盘对抗方所有，在 EncounterStepEngine（OpponentPlayer=="shelf"）与常规规则中均应归属当前执行主体 SelfPlayer
        if (IsUnownedSystemCard(card)
            || (string.Equals(ctx.OpponentPlayer?.PlayerId, "shelf", StringComparison.Ordinal) && ReferenceEquals(card, ctx.SelfCard)))
        {
            // 若玩家/对手容器意外显式持有 PlayerEffect，仍尊重所在容器；否则回落到 SelfPlayer
            if (card.Template.Type == "PlayerEffect" && OwnsCard(ctx.OpponentPlayer, card))
                return ctx.OpponentPlayer!;
            return ctx.SelfPlayer;
        }
        if (OwnsCard(ctx.OpponentPlayer, card))
            return ctx.OpponentPlayer!;
        return ctx.SelfPlayer;
    }

    private static bool HasStateCondition(RuleNode? cond, string stateName)
    {
        if (cond == null) return false;
        if (string.Equals(cond.Type, "TCardConditionalState", StringComparison.Ordinal))
        {
            return cond.IsNot != true
                && !string.Equals(cond.Operator, "None", StringComparison.OrdinalIgnoreCase)
                && cond.States != null
                && cond.States.Contains(stateName, StringComparer.OrdinalIgnoreCase);
        }
        if (cond.Type is "TCardConditionalAnd" or "TCardConditionalOr")
        {
            foreach (var sub in cond.GetConditionList(GameDatabaseLoader.JsonOptions))
            {
                if (HasStateCondition(sub, stateName)) return true;
            }
        }
        return false;
    }

    private static List<(CardInstance Card, PlayerState Owner)> CollectSectionPairs(
        string? section,
        RuleExecutionContext ctx,
        bool excludeSelf,
        bool includeDestroyed,
        bool includeDisabled)
    {
        var list = new List<(CardInstance, PlayerState)>();
        foreach (var (c, owner) in EnumerateSection(section, ctx, includeDestroyed, includeDisabled))
        {
            if (excludeSelf && ReferenceEquals(c, ctx.SelfCard)) continue;
            list.Add((c, owner));
        }
        return list;
    }

    private static void FilterCandidatePairs(
        List<(CardInstance Card, PlayerState Owner)> pairs,
        RuleNode? cond,
        RuleExecutionContext ctx,
        List<CardInstance> result)
    {
        if (pairs.Count == 0) return;
        if (cond == null)
        {
            for (int i = 0; i < pairs.Count; i++) result.Add(pairs[i].Card);
            return;
        }

        var filtered = ValueAndConditionEvaluator.FilterCardCandidatePairs(cond, pairs, ctx);
        if (filtered.Count == 0) return;
        if (filtered.Count == 1)
        {
            result.Add(filtered[0].Card);
            return;
        }

        // 对齐官方 TTargetCardBase.FilterByConditions：经条件过滤后按 LeftSocketId（未入格/技能为 null 排最前）稳定升序输出（插入排序保稳定且零额外分配）
        for (int i = 1; i < filtered.Count; i++)
        {
            var key = filtered[i];
            int keySlot = key.Card.StartSlot >= 0 ? key.Card.StartSlot : -1;
            int j = i - 1;
            while (j >= 0)
            {
                int prevSlot = filtered[j].Card.StartSlot >= 0 ? filtered[j].Card.StartSlot : -1;
                if (prevSlot <= keySlot) break;
                filtered[j + 1] = filtered[j];
                j--;
            }
            filtered[j + 1] = key;
        }
        for (int i = 0; i < filtered.Count; i++)
            result.Add(filtered[i].Card);
    }

    private static IEnumerable<(CardInstance Card, PlayerState Owner)> EnumerateSection(
        string? section,
        RuleExecutionContext ctx,
        bool includeDestroyed = false,
        bool includeDisabled = false)
    {
        bool IsEligible(CardInstance c) => (includeDestroyed || !c.IsDestroyed) && (includeDisabled || !c.IsDisabled);

        IEnumerable<(CardInstance, PlayerState)> EnumerateHand(PlayerState? p)
        {
            if (p == null) yield break;
            foreach (var c in p.Hand.Cards) if (IsEligible(c)) yield return (c, p);
        }

        IEnumerable<(CardInstance, PlayerState)> EnumerateStash(PlayerState? p)
        {
            if (p == null) yield break;
            foreach (var c in p.Stash.Cards) if (IsEligible(c)) yield return (c, p);
        }

        IEnumerable<(CardInstance, PlayerState)> EnumerateSkills(PlayerState? p)
        {
            if (p == null) yield break;
            foreach (var s in p.Skills) if (IsEligible(s)) yield return (s, p);
        }

        IEnumerable<(CardInstance, PlayerState)> EnumerateSocketEffects(PlayerState? p)
        {
            if (p == null) yield break;
            foreach (var se in p.SocketEffects) if (IsEligible(se)) yield return (se, p);
        }

        IEnumerable<(CardInstance, PlayerState)> EnumeratePlayerBoard(PlayerState? p)
        {
            foreach (var x in EnumerateHand(p)) yield return x;
            foreach (var x in EnumerateStash(p)) yield return x;
            foreach (var x in EnumerateSkills(p)) yield return x;
        }

        var local = GetLocalPlayer(ctx);
        var oppOfLocal = GetEncounterPlayer(ctx);
        // 无主事件/步骤/临时卡（SelfCard 无棋盘所属）回退到 ctx.SelfPlayer；
        // 当 SelfCard == null 时：局外（OutOfCombat）安全回退到 ctx.SelfPlayer，局内（InCombat）对齐官方 TargetSystem.GetPlayer*(run, null) 使用 local。
        bool selfCardUnowned = ctx.SelfCard != null
            && (IsUnownedSystemCard(ctx.SelfCard) || (!OwnsCard(ctx.SelfPlayer, ctx.SelfCard) && !OwnsCard(ctx.OpponentPlayer, ctx.SelfCard)));
        var selfOwner = ctx.SelfCard != null
            ? (selfCardUnowned ? ctx.SelfPlayer : FindOwner(ctx.SelfCard, ctx))
            : (ctx.Scope == RuleScope.OutOfCombat ? ctx.SelfPlayer : local);
        // 对齐官方 TargetSystem.GetRelativeOpponent(run, ctx.TargetingCard?.Owner)：
        // 当 SelfCard == null 时 targetingPlayer 为 null，官方判定 null != run.GetPlayer()，故相对对手回落为绝对本地玩家 local
        var relOpponent = ctx.SelfCard != null ? GetRelativeOpponent(ctx, selfOwner) : local;

        switch (section)
        {
            case "SelfHand":
                foreach (var x in EnumerateHand(selfOwner)) yield return x;
                break;
            case "SelfBoard":
                foreach (var x in EnumeratePlayerBoard(selfOwner)) yield return x;
                break;
            case "SelfStash":
                foreach (var x in EnumerateStash(selfOwner)) yield return x;
                break;
            case "SelfHandAndStash":
                foreach (var x in EnumerateHand(selfOwner)) yield return x;
                foreach (var x in EnumerateStash(selfOwner)) yield return x;
                break;
            case "SelfSocketEffects":
                foreach (var x in EnumerateSocketEffects(selfOwner)) yield return x;
                break;
            case "SelfSkills":
                foreach (var x in EnumerateSkills(selfOwner)) yield return x;
                break;
            case "AbsolutePlayerHand":
                foreach (var x in EnumerateHand(local)) yield return x;
                break;
            case "AbsolutePlayerStash":
                foreach (var x in EnumerateStash(local)) yield return x;
                break;
            case "AbsolutePlayerHandAndStash":
                foreach (var x in EnumerateHand(local)) yield return x;
                foreach (var x in EnumerateStash(local)) yield return x;
                break;
            case "AbsolutePlayerSkills":
                foreach (var x in EnumerateSkills(local)) yield return x;
                break;
            case "AbsolutePlayerSocketEffects":
                foreach (var x in EnumerateSocketEffects(local)) yield return x;
                break;
            case "AbsoluteOpponentHand":
                foreach (var x in EnumerateHand(oppOfLocal)) yield return x;
                break;
            case "AbsoluteOpponentStash":
                foreach (var x in EnumerateStash(oppOfLocal)) yield return x;
                break;
            case "AbsoluteOpponentHandAndStash":
                foreach (var x in EnumerateHand(oppOfLocal)) yield return x;
                foreach (var x in EnumerateStash(oppOfLocal)) yield return x;
                break;
            case "SelfNeighbors":
                if (ctx.SelfCard != null)
                {
                    var owner = FindOwner(ctx.SelfCard, ctx);
                    foreach (var c in ResolvePositionalNeighbors(owner, ctx.SelfCard, "Neighbor", includeDestroyed, includeDisabled))
                        yield return (c, owner);
                }
                break;
            case "TriggerSourceNeighbors":
                if (ctx.TriggerSourceCard != null)
                {
                    var owner = FindOwner(ctx.TriggerSourceCard, ctx);
                    foreach (var c in ResolvePositionalNeighbors(owner, ctx.TriggerSourceCard, "Neighbor", includeDestroyed, includeDisabled))
                        yield return (c, owner);
                }
                break;
            case "OpponentHand":
                foreach (var x in EnumerateHand(relOpponent)) yield return x;
                break;
            case "OpponentStash":
                foreach (var x in EnumerateStash(relOpponent)) yield return x;
                break;
            case "OpponentHandAndStash":
                foreach (var x in EnumerateHand(relOpponent)) yield return x;
                foreach (var x in EnumerateStash(relOpponent)) yield return x;
                break;
            case "OpponentSocketEffects":
                foreach (var x in EnumerateSocketEffects(relOpponent)) yield return x;
                break;
            case "OpponentSkills":
                foreach (var x in EnumerateSkills(relOpponent)) yield return x;
                break;
            case "OpponentBoard":
                foreach (var x in EnumeratePlayerBoard(relOpponent)) yield return x;
                break;
            case "AllHands":
                // 对齐 TargetSystem.GetAllHands：OpponentHand 先于 PlayerHand
                foreach (var x in EnumerateHand(oppOfLocal)) yield return x;
                foreach (var x in EnumerateHand(local)) yield return x;
                break;
            case "AllStashes":
                // 对齐 TargetSystem.GetAllStashes：OpponentStash 先于 PlayerStash
                foreach (var x in EnumerateStash(oppOfLocal)) yield return x;
                foreach (var x in EnumerateStash(local)) yield return x;
                break;
            case "AllHandsAndStashes":
                // 对齐 TargetSystem.GetAllHandsAndStashes：GetAllHands().Concat(GetAllStashes())
                foreach (var x in EnumerateHand(oppOfLocal)) yield return x;
                foreach (var x in EnumerateHand(local)) yield return x;
                foreach (var x in EnumerateStash(oppOfLocal)) yield return x;
                foreach (var x in EnumerateStash(local)) yield return x;
                break;
            case "AllHandsAndSkills":
                foreach (var x in EnumerateHand(local)) yield return x;
                foreach (var x in EnumerateSkills(local)) yield return x;
                foreach (var x in EnumerateHand(oppOfLocal)) yield return x;
                foreach (var x in EnumerateSkills(oppOfLocal)) yield return x;
                break;
            case "AllSocketEffects":
                // 对齐 TargetSystem.GetAllSocketEffects：OpponentSocketEffects 先于 PlayerSocketEffects
                foreach (var x in EnumerateSocketEffects(oppOfLocal)) yield return x;
                foreach (var x in EnumerateSocketEffects(local)) yield return x;
                break;
            case "AllBoards":
                foreach (var x in EnumeratePlayerBoard(local)) yield return x;
                foreach (var x in EnumeratePlayerBoard(oppOfLocal)) yield return x;
                break;
            case "SelectionSet":
                if (ctx.SelectionCards != null)
                {
                    foreach (var c in ctx.SelectionCards)
                        yield return (c, FindOwner(c, ctx));
                }
                break;
            default:
                foreach (var x in EnumerateHand(selfOwner)) yield return x;
                break;
        }
    }
}
