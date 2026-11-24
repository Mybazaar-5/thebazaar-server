using System.Runtime.CompilerServices;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>局外触发器执行引发的玩家属性变化明细。</summary>
public sealed class OutOfCombatPlayerAttributeChange
{
    public string Attribute { get; init; } = string.Empty;
    public double PreviousValue { get; init; }
    public double CurrentValue { get; init; }
    public double Delta => CurrentValue - PreviousValue;
}

/// <summary>局外触发器或光环重算引发的卡牌属性变化明细。</summary>
public sealed class OutOfCombatCardAttributeChange
{
    public CardInstance Card { get; init; } = null!;
    public string Attribute { get; init; } = string.Empty;
    public double PreviousValue { get; init; }
    public double CurrentValue { get; init; }
    public double Delta => CurrentValue - PreviousValue;
}

/// <summary>局外规则衍生生成的新卡牌及其落点位置。</summary>
public sealed class OutOfCombatSpawnedCard
{
    public CardInstance Card { get; init; } = null!;
    public string Section { get; init; } = "Hand";
    public int StartSlot { get; init; } = -1;
}

/// <summary>局外卡牌变形明细。</summary>
public sealed class OutOfCombatTransformedCard
{
    public CardInstance OldCard { get; init; } = null!;
    public CardInstance NewCard { get; init; } = null!;
    public string Section { get; init; } = "Hand";
    public int StartSlot { get; init; } = -1;
}

/// <summary>局外卡牌任务进度或完成日志。</summary>
public sealed class OutOfCombatQuestLogEntry
{
    public CardInstance Card { get; init; } = null!;
    public string QuestAttribute { get; init; } = string.Empty;
    public double PreviousProgress { get; init; }
    public double CurrentProgress { get; init; }
    public double Target { get; init; }
    public bool Completed { get; init; }
}

/// <summary>
/// 局外统一规则调度批量结果：汇总玩家属性变动、卡牌属性/光环变动、衍生卡、移除卡、升级/附魔/变形/任务日志。
/// </summary>
public sealed class OutOfCombatBatchResult
{
    public List<OutOfCombatPlayerAttributeChange> PlayerAttributeChanges { get; } = new();
    public Dictionary<string, double> PlayerAttributeDeltas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OutOfCombatCardAttributeChange> CardAttributeChanges { get; } = new();
    public HashSet<CardInstance> AffectedCards { get; } = new();
    public List<OutOfCombatSpawnedCard> SpawnedCards { get; } = new();
    public List<CardInstance> RemovedCards { get; } = new();
    public List<CardInstance> UpgradedCards { get; } = new();
    public List<CardInstance> EnchantedCards { get; } = new();
    public List<OutOfCombatTransformedCard> TransformedCards { get; } = new();
    public List<OutOfCombatQuestLogEntry> QuestLogs { get; } = new();
    public List<CardInstance> QuestCompletedCards { get; } = new();
    public List<string> TriggeredAbilityLogs { get; } = new();
    public List<(List<CardInstance> Cards, string SpawnMode)> DealtSelectionRequests { get; } = new();
    /// <summary>P0-01：强类型发牌请求列表（完整透传 SelectionContextRules，与 DealtSelectionRequests 同步追加保持兼容）。</summary>
    public List<DealtCardsPayload> DealtSelectionPayloads { get; } = new();
    public List<string> RerollRequests { get; } = new();
    /// <summary>带可选 SpawnCount 覆盖的完整重掷请求明细（P1-17；与 RerollRequests 同步追加以保持向后兼容）。</summary>
    public List<(string RerollType, int? SpawnCount)> RerollEntries { get; } = new();
    public bool ExitSelectionRequested { get; set; }
}

/// <summary>
/// 局外统一规则调度器：为上层 RunEngine / EncounterStepEngine 屏蔽递归触发器派发、光环重算、衍生卡落点与任务推进细节。
/// </summary>
public static class OutOfCombatController
{
    private const int MaxDispatchDepth = 8;

    private sealed record OutOfCombatSpawnPreferences(
        Func<string, ulong>? ArchetypeResolver,
        CustomRunRuleSet? CustomRules);

    private static readonly ConditionalWeakTable<PlayerState, List<CardInstance>> AttachedPlayerEffects = new();
    private static readonly ConditionalWeakTable<PlayerState, OutOfCombatSpawnPreferences> AttachedSpawnPreferences = new();
    private static readonly ConditionalWeakTable<PlayerState, IReadOnlyCollection<string>> AttachedVisitedEncounters = new();

    /// <summary>
    /// D1-P1-06：向指定玩家实例绑定已访问遭遇 ID 集合，供局外触发器、任务与光环评估 TRunConditionalHasVisitedEncounter。
    /// </summary>
    public static void AttachVisitedEncounters(PlayerState player, IReadOnlyCollection<string>? visitedEncounters)
    {
        if (player == null) return;
        if (visitedEncounters == null)
        {
            AttachedVisitedEncounters.Remove(player);
            return;
        }
        AttachedVisitedEncounters.AddOrUpdate(player, visitedEncounters);
    }

    public static IReadOnlyCollection<string>? GetAttachedVisitedEncounters(PlayerState player)
        => player != null && AttachedVisitedEncounters.TryGetValue(player, out var set) ? set : null;

    /// <summary>
    /// 为指定玩家实例挂载局外生成偏好（流派位掩码解析器与自定义规则集），
    /// 令该玩家触发的所有局外发牌/衍生/变形能力（如铲子、钓竿、曼陀罗）自动继承流派倾向与高阶掉率加成。
    /// </summary>
    public static void AttachSpawnPreferences(
        PlayerState player,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? customRules)
    {
        if (player == null) return;
        if (archetypeResolver == null && customRules == null)
        {
            AttachedSpawnPreferences.Remove(player);
            return;
        }
        AttachedSpawnPreferences.AddOrUpdate(player, new OutOfCombatSpawnPreferences(archetypeResolver, customRules));
    }

    private static readonly string[] CorePlayerAttributes =
    [
        "Gold", "Income", "HealthMax", "Health", "Experience", "Level", "Prestige", "HealthRegen",
        "Custom_3", "Custom_4", "Custom_5", "Custom_6", "Custom_7", "Custom_8", "Custom_9", "Custom_10", "Custom_11"
    ];

    private static readonly HashSet<string> HeroConditionalRunStartEffectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15d06ad8-c064-40e4-be83-ffc3be8fc5d9", // [PlayerAttributes] Add random Stove or Cooler (Jules)
        "1979db9f-4729-413d-b313-9018cf299578"  // [PlayerAttributes] Add random music note (Hero8)
    };

    private const string TournamentModePlayerEffectId = "01dfd497-ceee-4c9b-a53d-3ff2eafe6a75";
    private const string StoveOrCoolerPlayerEffectId = "15d06ad8-c064-40e4-be83-ffc3be8fc5d9";
    private const string MusicNotePlayerEffectId = "1979db9f-4729-413d-b313-9018cf299578";

    /// <summary>
    /// 向玩家附加指定的系统级 PlayerEffect 卡牌实例（独立于 Hand/Stash/Skills 存储，避免污染技能数量统计）。
    /// </summary>
    public static void AttachPlayerEffects(PlayerState player, IEnumerable<CardInstance> effects)
    {
        var list = AttachedPlayerEffects.GetOrCreateValue(player);
        foreach (var eff in effects)
        {
            if (eff == null) continue;
            if (!list.Any(existing => ReferenceEquals(existing, eff)
                    || string.Equals(existing.Template.Id, eff.Template.Id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(eff);
            }
            if (!player.PlayerEffects.Any(existing => ReferenceEquals(existing, eff)
                    || string.Equals(existing.Template.Id, eff.Template.Id, StringComparison.OrdinalIgnoreCase)))
            {
                player.PlayerEffects.Add(eff);
            }
        }
    }

    /// <summary>清空玩家附加的系统级 PlayerEffect 卡牌。</summary>
    public static void ClearAttachedPlayerEffects(PlayerState player)
    {
        AttachedPlayerEffects.Remove(player);
        player.PlayerEffects.Clear();
    }

    /// <summary>获取玩家当前附加的系统级 PlayerEffect 卡牌快照。</summary>
    public static IReadOnlyList<CardInstance> GetAttachedPlayerEffects(PlayerState player)
        => AttachedPlayerEffects.TryGetValue(player, out var list) ? list.ToList() : [];

    /// <summary>
    /// 从游戏数据库加载并注入官方 PlayerEffect 系统卡（默认包含 8 张 [PlayerAttributes] 统计追踪器与基础系统卡；
    /// 锦标赛标记与英雄开局衍生效果按参数与当前英雄条件选择性注入）。
    /// </summary>
    public static IReadOnlyList<CardInstance> InjectOfficialPlayerEffects(
        PlayerState player,
        GameDataRepository repo,
        bool includeTournamentMode = false,
        bool includeHeroRunStartSpawners = true)
    {
        var existingTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in player.EnumerateAllOwnedCards())
            existingTemplateIds.Add(c.Template.Id);
        if (AttachedPlayerEffects.TryGetValue(player, out var alreadyAttached))
        {
            foreach (var c in alreadyAttached)
                existingTemplateIds.Add(c.Template.Id);
        }

        var newlyAdded = new List<CardInstance>();
        foreach (var tpl in repo.CardsById.Values
                     .Where(c => string.Equals(c.Type, "PlayerEffect", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(tpl.Id, TournamentModePlayerEffectId, StringComparison.OrdinalIgnoreCase) && !includeTournamentMode)
                continue;
            if (HeroConditionalRunStartEffectIds.Contains(tpl.Id))
            {
                if (!includeHeroRunStartSpawners) continue;
                if (string.Equals(tpl.Id, StoveOrCoolerPlayerEffectId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(player.Hero, HeroNames.Jules, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(tpl.Id, MusicNotePlayerEffectId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(player.Hero, HeroNames.Hero8, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (!existingTemplateIds.Add(tpl.Id))
                continue;
            newlyAdded.Add(new CardInstance(tpl));
        }

        if (newlyAdded.Count > 0)
            AttachPlayerEffects(player, newlyAdded);
        return GetAttachedPlayerEffects(player);
    }

    private static readonly string[] CoreCardAttributes =
    [
        "DamageAmount", "ShieldApplyAmount", "BurnApplyAmount", "PoisonApplyAmount",
        "HealAmount", "RegenApplyAmount", "CritChance", "Multicast", "Lifesteal",
        "CooldownMax", "FlatCooldownReduction", "PercentCooldownReduction",
        "AmmoMax", "BuyPrice", "SellPrice", "Custom_0", "Custom_1", "Custom_2",
        "Custom_3", "Custom_4", "Custom_5", "Heated", "Chilled", "Tiny", "Flying"
    ];

    /// <summary>
    /// 仅重算局外光环并返回所有受影响卡牌的属性差分结果（支持传入当前活跃事件/遭遇卡的光环及当前货架 selectionCards）。
    /// 当局外光环新赋予卡牌 Flying 时同步推进飞行 Quest 并记录日志。
    /// </summary>
    public static OutOfCombatBatchResult RecalculateOutOfCombatAuras(
        PlayerState player,
        GameDataRepository repo,
        CardInstance? activeEncounterCard = null,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        var result = new OutOfCombatBatchResult();
        player.EnsureExclusionSetInitialized(repo.GameMode);
        var playerSnap = SnapshotPlayerAttributes(player);
        var cardsSnap = SnapshotOwnedCards(player);

        var transitions = RecalculateAllOutOfCombatAuras(player, repo, activeEncounterCard, selectionCards);
        if (transitions.Count > 0)
        {
            var visitedEnc = GetAttachedVisitedEncounters(player);
            var qCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, null, new Random(0))
            {
                LocalPlayerId = player.PlayerId,
                CurrentDay = player.Day,
                CurrentHour = player.Hour,
                CurrentEncounterId = activeEncounterCard?.Template.Id,
                VisitedEncounterIds = visitedEnc,
                SelectionCards = selectionCards,
                OnQuestUpdated = (card, questAttr, qPrev, qCur, qTarget) =>
                {
                    result.QuestLogs.Add(new OutOfCombatQuestLogEntry
                    {
                        Card = card,
                        QuestAttribute = questAttr,
                        PreviousProgress = qPrev,
                        CurrentProgress = qCur,
                        Target = qTarget,
                        Completed = qCur >= qTarget
                    });
                    result.AffectedCards.Add(card);
                },
                OnQuestCompleted = card =>
                {
                    if (!result.QuestCompletedCards.Contains(card))
                        result.QuestCompletedCards.Add(card);
                    result.AffectedCards.Add(card);
                }
            };
            foreach (var tr in transitions)
            {
                if (!tr.StartedFlying) continue;
                var ctx = qCtx.WithCards(null, tr.SourceCard, tr.TargetCard);
                ctx.LastAttributeChangeDelta = 1;
                AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartedFlying", player, tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1, "PVP", "", ctx);
                AbilityExecutor.TryAdvanceQuests("TTriggerOnCardStartsFlying", player, tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1, "PVP", "", ctx);
                AbilityExecutor.TryAdvanceQuests("TTriggerOnCardAttributeChanged", player, tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1, "PVP", "", ctx);
            }
        }

        CollectDiffs(player, playerSnap, cardsSnap, result);
        return result;
    }

    /// <summary>
    /// 局外开局触发（TTriggerOnRunStarted）。
    /// </summary>
    public static OutOfCombatBatchResult OnRunStarted(PlayerState player, GameDataRepository repo, Random? rng = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnRunStarted",
            sourceCard: null, targetCard: null, subjectPlayer: player);
    }

    /// <summary>
    /// 玩家升级时调用：同时派发 TTriggerOnPlayerLeveledUp 与 TTriggerOnPlayerAttributeChanged(Level) 并刷新局外光环。
    /// </summary>
    public static OutOfCombatBatchResult OnPlayerLeveledUp(
        PlayerState player,
        GameDataRepository repo,
        Random? rng = null,
        int? newLevel = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        int targetLevel = newLevel ?? player.Level;
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnPlayerLeveledUp",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            changedAttr: "Level",
            prevVal: Math.Max(0, targetLevel - 1),
            curVal: targetLevel,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard,
            secondaryEventTypes: ["TTriggerOnPlayerAttributeChanged"]);
    }

    /// <summary>
    /// 整局结束结算触发（TTriggerOnRunCompleted）。
    /// </summary>
    public static OutOfCombatBatchResult OnRunCompleted(
        PlayerState player,
        GameDataRepository repo,
        string combatOutcome = "Victory",
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        string normalizedOutcome = combatOutcome switch
        {
            var s when s.Equals("Win", StringComparison.OrdinalIgnoreCase) => "Victory",
            var s when s.Equals("Lose", StringComparison.OrdinalIgnoreCase) => "Defeat",
            _ => combatOutcome
        };
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnRunCompleted",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            combatType: "RunCompleted",
            combatOutcome: normalizedOutcome,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard);
    }

    /// <summary>
    /// 购买物品或技能后调用（支持 buy-fusion 计为购买；<paramref name="isFree"/> 为 true 的免费发牌不计为购买）：
    /// 先触发 TTriggerOnCardPurchased 并推进 Quest，再重算局外光环。
    /// </summary>
    public static OutOfCombatBatchResult OnItemPurchased(
        PlayerState player,
        CardInstance boughtCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null,
        bool isFree = false,
        bool isFusion = false,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        if (isFree)
            return RecalculateOutOfCombatAuras(player, repo, activeEncounterCard, selectionCards);

        bool isOwned = player.Hand.Cards.Contains(boughtCard)
                    || player.Stash.Cards.Contains(boughtCard)
                    || player.Skills.Contains(boughtCard)
                    || player.SocketEffects.Contains(boughtCard);
        IReadOnlyList<CardInstance>? extra = isOwned ? null : [boughtCard];

        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardPurchased",
            sourceCard: boughtCard, targetCard: boughtCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            isFusionUpgrade: isFusion,
            selectionCards: selectionCards,
            extraCardsForTrigger: extra,
            activeEncounterCard: activeEncounterCard);
    }

    /// <summary>
    /// 出售或使用消耗品时调用。
    /// 关键顺序：
    ///   1. 先通过 SoldCardScope 记录 <paramref name="card"/> 的原始网格与 StartSlot，并在原位刷新一次局外光环（确保 Custom_0 / Value 等属性处于最新态）；
    ///   2. 若 removeAfterTrigger 为 true，则在派发 TTriggerOnCardSold 之前提前将 <paramref name="card"/> 从 Hand/Stash 摘除腾出槽位，
    ///      确保满手牌/满仓库卖出 Junkyard Club / Junkyard Repairbot / Pinata / Shipment 等产卡道具时能全额落入腾出的空位；
    ///   3. 触发期间由 AbilityExecutor.SoldCardScope 为 SelfNeighbors / Positional / TriggerSource 等定位与主体条件临时保留原始站位。
    /// </summary>
    public static OutOfCombatBatchResult OnCardSoldOrConsumed(
        PlayerState player,
        CardInstance card,
        GameDataRepository repo,
        Random? rng = null,
        bool removeAfterTrigger = true,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        if (card.HasHiddenTag("Unsellable") || card.HasTag("Unsellable") || card.GetAttribute("Unsellable") > 0)
            return new OutOfCombatBatchResult();

        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardSold",
            sourceCard: card, targetCard: card, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            selectionCards: selectionCards,
            extraCardsForTrigger: [card],
            activeEncounterCard: activeEncounterCard,
            afterPreAuraBeforeDispatchAction: result =>
            {
                if (!removeAfterTrigger) return;
                bool removed = player.Hand.Remove(card)
                            || player.Stash.Remove(card)
                            || player.Skills.Remove(card)
                            || player.SocketEffects.Remove(card);
                if (removed && !result.RemovedCards.Contains(card))
                    result.RemovedCards.Add(card);
            });
    }

    /// <summary>
    /// 商店拖拽换位后调用：重算 OutOfCombat 光环并收集发生数值/标签变化的卡牌。
    /// </summary>
    /// <summary>
    /// 局外卡牌换位/跨区移动后调用：
    /// - 传入 <paramref name="movedCard"/> 时派发 <c>TTriggerOnCardMoved</c> 生命周期事件（支持触发局外移动变形/衍生等被动）并重算光环；
    /// - 未传 <paramref name="movedCard"/> 时保持向后兼容，仅重算局外光环。
    /// </summary>
    public static OutOfCombatBatchResult OnCardMoved(
        PlayerState player,
        GameDataRepository repo,
        CardInstance? activeEncounterCard = null,
        IReadOnlyList<CardInstance>? selectionCards = null,
        CardInstance? movedCard = null,
        Random? rng = null,
        string? currentEncounterId = null)
    {
        if (movedCard == null)
            return RecalculateOutOfCombatAuras(player, repo, activeEncounterCard, selectionCards);

        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardMoved",
            sourceCard: movedCard,
            targetCard: movedCard,
            subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard,
            selectionCards: selectionCards);
    }

    /// <summary>
    /// 卡牌升阶（含二合一融合或外部升阶）后调用：触发 TTriggerOnCardUpgraded 并重算光环。
    /// </summary>
    public static OutOfCombatBatchResult OnCardUpgraded(
        PlayerState player,
        CardInstance upgradedCard,
        GameDataRepository repo,
        Random? rng = null,
        bool isFusion = false,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardUpgraded",
            sourceCard: upgradedCard, targetCard: upgradedCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            isFusionUpgrade: isFusion,
            selectionCards: selectionCards,
            activeEncounterCard: activeEncounterCard);
    }

    /// <summary>
    /// 卡牌附魔后调用：触发 TTriggerOnCardEnchanted 并重算光环。
    /// </summary>
    public static OutOfCombatBatchResult OnCardEnchanted(
        PlayerState player,
        CardInstance enchantedCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null,
        IReadOnlyList<CardInstance>? selectionCards = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardEnchanted",
            sourceCard: enchantedCard, targetCard: enchantedCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            selectionCards: selectionCards,
            activeEncounterCard: activeEncounterCard);
    }

    /// <summary>
    /// 新小时开始时调用：清理 UntilEndOfHour 临时属性，派发 TTriggerOnHourStarted，并刷新光环。
    /// </summary>
    public static OutOfCombatBatchResult OnHourStarted(
        PlayerState player,
        GameDataRepository repo,
        Random? rng = null,
        bool clearEndOfHourModifiers = true,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnHourStarted",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard,
            preDispatchAction: () =>
            {
                if (clearEndOfHourModifiers)
                    player.ClearEndOfHourModifiers();
            });
    }

    /// <summary>
    /// 新的一天开始时调用：清理 UntilEndOfDay 临时属性，派发 TTriggerOnDayStarted，并刷新光环。
    /// </summary>
    public static OutOfCombatBatchResult OnDayStarted(
        PlayerState player,
        GameDataRepository repo,
        Random? rng = null,
        bool clearEndOfDayModifiers = true,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnDayStarted",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard,
            preDispatchAction: () =>
            {
                if (clearEndOfDayModifiers)
                    player.ClearEndOfDayModifiers();
            });
    }

    /// <summary>
    /// 选择遭遇时调用（TTriggerOnEncounterSelected）。
    /// </summary>
    public static OutOfCombatBatchResult OnEncounterSelected(
        PlayerState player,
        CardInstance encounterCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnEncounterSelected",
            sourceCard: encounterCard, targetCard: encounterCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId ?? encounterCard.Template.Id,
            activeEncounterCard: activeEncounterCard ?? encounterCard);
    }

    /// <summary>
    /// 进入遭遇时调用（TTriggerOnEncounterEntered）。
    /// </summary>
    public static OutOfCombatBatchResult OnEncounterEntered(
        PlayerState player,
        CardInstance encounterCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnEncounterEntered",
            sourceCard: encounterCard, targetCard: encounterCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId ?? encounterCard.Template.Id,
            activeEncounterCard: activeEncounterCard ?? encounterCard);
    }

    /// <summary>
    /// 离开遭遇时调用（TTriggerOnEncounterExited）。
    /// </summary>
    public static OutOfCombatBatchResult OnEncounterExited(
        PlayerState player,
        CardInstance encounterCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnEncounterExited",
            sourceCard: encounterCard, targetCard: encounterCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId ?? encounterCard.Template.Id,
            activeEncounterCard: activeEncounterCard ?? encounterCard);
    }

    /// <summary>
    /// 遭遇/商店发牌后调用（TTriggerOnEncounterCardsDealt），传入已发出的货架卡牌以便 Lucky Clover 等修改 SelectionSet。
    /// </summary>
    public static OutOfCombatBatchResult OnEncounterCardsDealt(
        PlayerState player,
        CardInstance? encounterCard,
        IReadOnlyList<CardInstance>? dealtCards,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnEncounterCardsDealt",
            sourceCard: encounterCard, targetCard: encounterCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId ?? encounterCard?.Template.Id,
            selectionCards: dealtCards,
            activeEncounterCard: activeEncounterCard ?? encounterCard);
    }

    /// <summary>
    /// 选定事件步/选项卡时调用（TTriggerOnCardSelected）。
    /// </summary>
    public static OutOfCombatBatchResult OnCardSelected(
        PlayerState player,
        CardInstance selectedCard,
        GameDataRepository repo,
        Random? rng = null,
        IReadOnlyList<CardInstance>? selectionCards = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnCardSelected",
            sourceCard: selectedCard, targetCard: selectedCard, subjectPlayer: player,
            currentEncounterId: currentEncounterId,
            selectionCards: selectionCards,
            extraCardsForTrigger: [selectedCard],
            activeEncounterCard: activeEncounterCard);
    }

    /// <summary>
    /// 战斗结束后局外结算触发（TTriggerOnFightEnded）：驱动如 Hunting Knife、Wanted Poster、Crystal Bonsai、Candy Mail 等战后效果。
    /// </summary>
    public static OutOfCombatBatchResult OnFightEnded(
        PlayerState player,
        string combatType,
        string combatOutcome,
        GameDataRepository repo,
        Random? rng = null,
        PlayerState? opponent = null)
    {
        string normalizedOutcome = combatOutcome switch
        {
            var s when s.Equals("Victory", StringComparison.OrdinalIgnoreCase) => "Win",
            var s when s.Equals("Defeat", StringComparison.OrdinalIgnoreCase) => "Lose",
            _ => combatOutcome
        };

        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnFightEnded",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            combatType: combatType,
            combatOutcome: normalizedOutcome,
            opponent: opponent);
    }

    /// <summary>
    /// 卡牌变形（Transform）时调用：先在旧卡仍保留原标签/原分区可见状态下派发 TTriggerOnCardTransformed
    /// （驱动 [PlayerAttributes] Reagents Transformed 累加 Custom_9 等），再按需执行置换并派发 TTriggerOnCardAfterTransform 与光环重算。
    /// </summary>
    public static OutOfCombatBatchResult OnCardTransformed(
        PlayerState player,
        CardInstance oldCard,
        CardInstance? newCard,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null,
        bool swapInPlaceIfOldStillPresent = false)
    {
        bool oldInHand = player.Hand.Cards.Contains(oldCard);
        bool oldInStash = player.Stash.Cards.Contains(oldCard);
        bool oldInSkills = player.Skills.Contains(oldCard);
        bool oldInSockets = player.SocketEffects.Contains(oldCard);
        bool oldPresent = oldInHand || oldInStash || oldInSkills || oldInSockets;

        bool swappedTemporarily = false;
        bool addedTemporarily = false;
        bool prevOldDestroyed = oldCard.IsDestroyed;
        int tempNewHandIdx = -1;
        int tempNewStashIdx = -1;
        int tempNewSkillIdx = -1;
        int tempNewSocketIdx = -1;

        void RestoreTempExposure()
        {
            if (swappedTemporarily && newCard != null)
            {
                if (tempNewHandIdx >= 0)
                {
                    int idx = player.Hand.Cards.IndexOf(oldCard);
                    if (idx >= 0) player.Hand.Cards[idx] = newCard;
                }
                else if (tempNewStashIdx >= 0)
                {
                    int idx = player.Stash.Cards.IndexOf(oldCard);
                    if (idx >= 0) player.Stash.Cards[idx] = newCard;
                }
                else if (tempNewSkillIdx >= 0)
                {
                    int idx = player.Skills.IndexOf(oldCard);
                    if (idx >= 0) player.Skills[idx] = newCard;
                }
                else if (tempNewSocketIdx >= 0)
                {
                    int idx = player.SocketEffects.IndexOf(oldCard);
                    if (idx >= 0) player.SocketEffects[idx] = newCard;
                }
                swappedTemporarily = false;
            }
            if (addedTemporarily)
            {
                player.Stash.Cards.Remove(oldCard);
                addedTemporarily = false;
            }
            oldCard.IsDestroyed = prevOldDestroyed;
        }

        try
        {
            return ExecuteLifecycleEvent(
                player, repo, rng,
                eventType: "TTriggerOnCardTransformed",
                sourceCard: oldCard, targetCard: oldCard, subjectPlayer: player,
                currentEncounterId: currentEncounterId,
                extraCardsForTrigger: newCard != null ? [oldCard, newCard] : [oldCard],
                activeEncounterCard: activeEncounterCard,
                preDispatchAction: () =>
                {
                    if (oldPresent) return;
                    oldCard.IsDestroyed = false;
                    if (newCard != null)
                    {
                        tempNewHandIdx = player.Hand.Cards.IndexOf(newCard);
                        if (tempNewHandIdx >= 0)
                        {
                            player.Hand.Cards[tempNewHandIdx] = oldCard;
                            oldCard.StartSlot = newCard.StartSlot;
                            swappedTemporarily = true;
                            return;
                        }
                        tempNewStashIdx = player.Stash.Cards.IndexOf(newCard);
                        if (tempNewStashIdx >= 0)
                        {
                            player.Stash.Cards[tempNewStashIdx] = oldCard;
                            oldCard.StartSlot = newCard.StartSlot;
                            swappedTemporarily = true;
                            return;
                        }
                        tempNewSkillIdx = player.Skills.IndexOf(newCard);
                        if (tempNewSkillIdx >= 0)
                        {
                            player.Skills[tempNewSkillIdx] = oldCard;
                            swappedTemporarily = true;
                            return;
                        }
                        tempNewSocketIdx = player.SocketEffects.IndexOf(newCard);
                        if (tempNewSocketIdx >= 0)
                        {
                            player.SocketEffects[tempNewSocketIdx] = oldCard;
                            oldCard.StartSlot = newCard.StartSlot;
                            swappedTemporarily = true;
                            return;
                        }
                    }

                    // 独立直调时保证旧卡对 SelfHandAndStash 目标解析可见
                    player.Stash.Cards.Add(oldCard);
                    addedTemporarily = true;
                },
                postDispatchAction: result =>
                {
                    RestoreTempExposure();
                    if (newCard != null && oldPresent && swapInPlaceIfOldStillPresent)
                    {
                        int slot = oldCard.StartSlot;
                        if (oldInHand)
                        {
                            player.Hand.Remove(oldCard);
                            oldCard.IsDestroyed = true;
                            if (slot < 0 || !player.Hand.TryPlaceWithDisplaceOrSwap(newCard, slot))
                            {
                                if (!player.Hand.TryAddAuto(newCard))
                                    player.Stash.TryAddAuto(newCard);
                            }
                        }
                        else if (oldInStash)
                        {
                            player.Stash.Remove(oldCard);
                            oldCard.IsDestroyed = true;
                            if (slot < 0 || !player.Stash.TryPlaceWithDisplaceOrSwap(newCard, slot))
                                player.Stash.TryAddAuto(newCard);
                        }
                        else if (oldInSkills)
                        {
                            int sIdx = player.Skills.IndexOf(oldCard);
                            oldCard.IsDestroyed = true;
                            if (sIdx >= 0) player.Skills[sIdx] = newCard;
                        }
                        else if (oldInSockets)
                        {
                            int seIdx = player.SocketEffects.IndexOf(oldCard);
                            oldCard.IsDestroyed = true;
                            newCard.StartSlot = slot;
                            if (seIdx >= 0) player.SocketEffects[seIdx] = newCard;
                        }
                        var (sec, placedSlot) = LocateCardPlacement(player, newCard);
                        result.TransformedCards.Add(new OutOfCombatTransformedCard
                        {
                            OldCard = oldCard,
                            NewCard = newCard,
                            Section = sec,
                            StartSlot = placedSlot
                        });
                        result.AffectedCards.Add(newCard);
                        if (!result.RemovedCards.Contains(oldCard))
                            result.RemovedCards.Add(oldCard);
                    }
                },
                secondaryEventTypes: newCard != null ? ["TTriggerOnCardAfterTransform"] : null,
                secondarySourceCard: oldCard,
                secondaryTargetCard: newCard);
        }
        finally
        {
            RestoreTempExposure();
        }
    }

    /// <summary>
    /// 外部直接修改玩家属性（如升级 Level 提升、商店花费 Gold 等）时派发 TTriggerOnPlayerAttributeChanged。
    /// </summary>
    public static OutOfCombatBatchResult OnPlayerAttributeChanged(
        PlayerState player,
        string attr,
        double prevVal,
        double curVal,
        GameDataRepository repo,
        Random? rng = null,
        string? currentEncounterId = null,
        CardInstance? activeEncounterCard = null)
    {
        return ExecuteLifecycleEvent(
            player, repo, rng,
            eventType: "TTriggerOnPlayerAttributeChanged",
            sourceCard: null, targetCard: null, subjectPlayer: player,
            changedAttr: attr, prevVal: prevVal, curVal: curVal,
            currentEncounterId: currentEncounterId,
            activeEncounterCard: activeEncounterCard);
    }

    private static OutOfCombatBatchResult ExecuteLifecycleEvent(
        PlayerState player,
        GameDataRepository repo,
        Random? rng,
        string eventType,
        CardInstance? sourceCard,
        CardInstance? targetCard,
        PlayerState? subjectPlayer,
        string? changedAttr = null,
        double prevVal = 0,
        double curVal = 0,
        string combatType = "PVP",
        string combatOutcome = "",
        string? currentEncounterId = null,
        bool isFusionUpgrade = false,
        IReadOnlyList<CardInstance>? selectionCards = null,
        IReadOnlyList<CardInstance>? extraCardsForTrigger = null,
        PlayerState? opponent = null,
        CardInstance? activeEncounterCard = null,
        Action? preDispatchAction = null,
        Action<OutOfCombatBatchResult>? afterPreAuraBeforeDispatchAction = null,
        Action<OutOfCombatBatchResult>? postDispatchAction = null,
        IReadOnlyList<string>? secondaryEventTypes = null,
        CardInstance? secondarySourceCard = null,
        CardInstance? secondaryTargetCard = null)
    {
        var result = new OutOfCombatBatchResult();
        player.EnsureExclusionSetInitialized(repo.GameMode);
        var playerSnap = SnapshotPlayerAttributes(player);
        var cardsSnap = SnapshotOwnedCards(player);

        preDispatchAction?.Invoke();

        var effectiveRng = rng ?? Random.Shared;
        int dispatchDepth = 0;

        RuleExecutionContext baseCtx = null!;

        void DispatchOutOfCombat(
            string evName,
            CardInstance? srcCard,
            CardInstance? tgtCard,
            PlayerState? subjPlayer,
            string? attr,
            double pVal,
            double cVal)
        {
            if (dispatchDepth >= MaxDispatchDepth) return;
            dispatchDepth++;
            try
            {
                // 收集当前有资格触发的拥有卡牌（快照遍历防止 Spawn/Transform/Destroy 修改集合）
                var candidates = CollectTriggerCandidates(player, extraCardsForTrigger, activeEncounterCard);
                double delta = Math.Abs(cVal - pVal);

                // 修复 P1-21：先跨全盘收集全部候选 (card, ab)，再按官方 Ability.Priority 全局稳定排序执行
                var pendingAbilities = new List<(CardInstance Card, AbilityDefinition Ability, bool AllowMismatch, int CardOrder, int AbilityOrder)>(candidates.Count * 2);
                for (int i = 0; i < candidates.Count; i++)
                {
                    var (card, activeInMismatchAllowed) = candidates[i];
                    if (card.IsDisabled) continue;
                    if (card.IsDestroyed && evName != "TTriggerOnBeforeCardDestroyed") continue;

                    for (int a = 0; a < card.ActiveAbilities.Count; a++)
                    {
                        var ab = card.ActiveAbilities[a];
                        if (ab.Trigger == null) continue;
                        if (string.Equals(ab.WorksIn, "CombatOnly", StringComparison.OrdinalIgnoreCase)) continue;
                        bool allowDirectCardMismatch = activeInMismatchAllowed && evName == eventType && card == srcCard;
                        bool isEncounter = activeEncounterCard != null && card == activeEncounterCard;
                        bool inHand = player.Hand.Cards.Contains(card) || isEncounter;
                        bool inStash = player.Stash.Cards.Contains(card);
                        if (!allowDirectCardMismatch && !IsAbilityActiveInOutOfCombat(ab.ActiveIn, inHand, inStash))
                            continue;

                        pendingAbilities.Add((card, ab, allowDirectCardMismatch, i, a));
                    }
                }

                if (pendingAbilities.Count > 1)
                {
                    pendingAbilities.Sort(static (x, y) =>
                    {
                        int prioCmp = GetAbilityPriorityRank(x.Ability.Priority).CompareTo(GetAbilityPriorityRank(y.Ability.Priority));
                        if (prioCmp != 0) return prioCmp;
                        int cardCmp = x.CardOrder.CompareTo(y.CardOrder);
                        if (cardCmp != 0) return cardCmp;
                        return x.AbilityOrder.CompareTo(y.AbilityOrder);
                    });
                }

                for (int pIdx = 0; pIdx < pendingAbilities.Count; pIdx++)
                {
                    var (card, ab, _, _, _) = pendingAbilities[pIdx];
                    if (card.IsDisabled) continue;
                    if (card.IsDestroyed && evName != "TTriggerOnBeforeCardDestroyed") continue;

                    var abCtx = baseCtx.WithCards(card, srcCard, tgtCard);
                    abCtx.LastAttributeChangeDelta = delta;
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                        continue;

                    if (!AbilityExecutor.MatchesTrigger(
                            ab.Trigger, evName, card, player,
                            srcCard, tgtCard, subjPlayer,
                            attr, pVal, cVal,
                            combatType, combatOutcome, abCtx))
                        continue;

                    var prevAbId = abCtx.ActiveExecution.AbilityId;
                    var prevAbObj = abCtx.ActiveExecution.Ability;
                    abCtx.ActiveExecution.AbilityId = ab.Id;
                    abCtx.ActiveExecution.Ability = ab;
                    try
                    {
                        if (AbilityExecutor.ExecuteAction(ab.Action, card, abCtx))
                            result.TriggeredAbilityLogs.Add($"{card.Template.InternalName}:{ab.Id}:{evName}");
                    }
                    finally
                    {
                        abCtx.ActiveExecution.AbilityId = prevAbId;
                        abCtx.ActiveExecution.Ability = prevAbObj;
                    }
                }

                // 推进任务进度（对全局拥有卡）
                var qCtx = baseCtx.WithCards(null, srcCard, tgtCard);
                qCtx.LastAttributeChangeDelta = delta;
                AbilityExecutor.TryAdvanceQuests(
                    evName, player,
                    srcCard, tgtCard, subjPlayer,
                    attr, pVal, cVal,
                    combatType, combatOutcome, qCtx);
            }
            finally
            {
                dispatchDepth--;
            }
        }

        AttachedSpawnPreferences.TryGetValue(player, out var spawnPrefs);
        var visitedEncounters = GetAttachedVisitedEncounters(player);
        baseCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, player, opponent, effectiveRng)
        {
            LocalPlayerId = player.PlayerId,
            CurrentDay = player.Day,
            CurrentHour = player.Hour,
            CombatType = combatType,
            CurrentEncounterId = currentEncounterId ?? activeEncounterCard?.Template.Id,
            VisitedEncounterIds = visitedEncounters,
            IsFusionUpgrade = isFusionUpgrade,
            SelectionCards = selectionCards,
            ArchetypeResolver = spawnPrefs?.ArchetypeResolver,
            CustomRules = spawnPrefs?.CustomRules,
            OnDealCardsToSelection = (cards, mode) =>
            {
                result.DealtSelectionRequests.Add((cards, mode));
                result.DealtSelectionPayloads.Add(new DealtCardsPayload(cards, mode, null, null));
            },
            OnDealCardsToSelectionWithRules = payload =>
            {
                result.DealtSelectionRequests.Add((payload.Cards, payload.SpawnMode));
                result.DealtSelectionPayloads.Add(payload);
            },
            OnRerollEncounter = rerollType =>
            {
                result.RerollRequests.Add(rerollType);
                result.RerollEntries.Add((rerollType, null));
            },
            OnRerollEncounterWithCount = (rerollType, spawnCount) =>
            {
                result.RerollRequests.Add(rerollType);
                result.RerollEntries.Add((rerollType, spawnCount));
            },
            OnExitSelection = () => result.ExitSelectionRequested = true,
            OnCardSpawned = (owner, spawned) =>
            {
                if (owner != player) return;
                var (sec, slot) = LocateCardPlacement(player, spawned);
                result.SpawnedCards.Add(new OutOfCombatSpawnedCard
                {
                    Card = spawned,
                    Section = sec,
                    StartSlot = slot
                });
                result.AffectedCards.Add(spawned);
            },
            OnCardTransformed = (owner, oldCard, newCard) =>
            {
                if (owner != player) return;
                var (sec, slot) = LocateCardPlacement(player, newCard);
                result.TransformedCards.Add(new OutOfCombatTransformedCard
                {
                    OldCard = oldCard,
                    NewCard = newCard,
                    Section = sec,
                    StartSlot = slot
                });
                result.AffectedCards.Add(newCard);
                if (!result.RemovedCards.Contains(oldCard))
                    result.RemovedCards.Add(oldCard);
            },
            OnCardDestroyed = (owner, destroyed) =>
            {
                if (owner != player) return;
                if (!result.RemovedCards.Contains(destroyed))
                    result.RemovedCards.Add(destroyed);
            },
            OnCardUpgraded = (owner, card) =>
            {
                if (owner != player) return;
                if (!result.UpgradedCards.Contains(card))
                    result.UpgradedCards.Add(card);
                result.AffectedCards.Add(card);
            },
            OnCardEnchanted = (owner, card) =>
            {
                if (owner != player) return;
                if (!result.EnchantedCards.Contains(card))
                    result.EnchantedCards.Add(card);
                result.AffectedCards.Add(card);
            },
            OnQuestUpdated = (card, questAttr, qPrev, qCur, qTarget) =>
            {
                result.QuestLogs.Add(new OutOfCombatQuestLogEntry
                {
                    Card = card,
                    QuestAttribute = questAttr,
                    PreviousProgress = qPrev,
                    CurrentProgress = qCur,
                    Target = qTarget,
                    Completed = qCur >= qTarget
                });
                result.AffectedCards.Add(card);
            },
            OnQuestCompleted = card =>
            {
                if (!result.QuestCompletedCards.Contains(card))
                    result.QuestCompletedCards.Add(card);
                result.AffectedCards.Add(card);
                if (dispatchDepth < MaxDispatchDepth)
                    DispatchOutOfCombat("TTriggerOnCardQuestCompleted", card, card, player, null, 0, 0);
            },
            OnDetailedTriggeredEvent = (evName, actor, src, tgt, subj, attr, prev, cur) =>
            {
                if (dispatchDepth >= MaxDispatchDepth) return;
                DispatchOutOfCombat(evName, src, tgt, subj ?? actor, attr, prev, cur);
            }
        };

        void DispatchAuraFlyingTransitions(List<AuraEngine.AuraFlyingTransition> transitions)
        {
            for (int i = 0; i < transitions.Count; i++)
            {
                var tr = transitions[i];
                if (tr.StartedFlying)
                {
                    DispatchOutOfCombat("TTriggerOnCardStartedFlying", tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1);
                    DispatchOutOfCombat("TTriggerOnCardStartsFlying", tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1);
                    DispatchOutOfCombat("TTriggerOnCardAttributeChanged", tr.SourceCard, tr.TargetCard, player, "Flying", 0, 1);
                }
                else
                {
                    DispatchOutOfCombat("TTriggerOnCardStoppedFlying", tr.SourceCard, tr.TargetCard, player, "Flying", 1, 0);
                    DispatchOutOfCombat("TTriggerOnCardAttributeChanged", tr.SourceCard, tr.TargetCard, player, "Flying", 1, 0);
                }
            }
        }

        bool trackSoldScope = string.Equals(eventType, "TTriggerOnCardSold", StringComparison.Ordinal) && sourceCard != null;
        if (trackSoldScope)
            AbilityExecutor.PushSoldCardScope(baseCtx, sourceCard, player);

        try
        {
            // 在派发前先确保光环已处于最新状态（被卖卡仍位于原槽位以锁定最新光环属性）
            var preTransitions = RecalculateAllOutOfCombatAuras(player, repo, activeEncounterCard, selectionCards);
            if (preTransitions.Count > 0)
                DispatchAuraFlyingTransitions(preTransitions);

            // 若需在派发前腾出被卖卡槽位（已由 SoldCardScope 保留原网格与原槽位），在此先摘除再触发
            afterPreAuraBeforeDispatchAction?.Invoke(result);

            DispatchOutOfCombat(eventType, sourceCard, targetCard, subjectPlayer, changedAttr, prevVal, curVal);
            postDispatchAction?.Invoke(result);

            if (secondaryEventTypes != null)
            {
                var secSrc = secondarySourceCard ?? sourceCard;
                var secTgt = secondaryTargetCard ?? targetCard;
                for (int i = 0; i < secondaryEventTypes.Count; i++)
                {
                    DispatchOutOfCombat(secondaryEventTypes[i], secSrc, secTgt, subjectPlayer, changedAttr, prevVal, curVal);
                }
            }

            // 事件全量执行及摘除完成后再重算局外光环
            var postTransitions = RecalculateAllOutOfCombatAuras(player, repo, activeEncounterCard, selectionCards);
            if (postTransitions.Count > 0)
                DispatchAuraFlyingTransitions(postTransitions);

            CollectDiffs(player, playerSnap, cardsSnap, result);
            return result;
        }
        finally
        {
            if (trackSoldScope)
                AbilityExecutor.ClearSoldCardScope(baseCtx);
        }
    }

    private static List<AuraEngine.AuraFlyingTransition> RecalculateAllOutOfCombatAuras(
        PlayerState player,
        GameDataRepository repo,
        CardInstance? activeEncounterCard,
        IReadOnlyList<CardInstance>? selectionCards)
    {
        // 修复 P2-25：删除重复简化副本 ApplyEncounterAuraAction，直接将外部效果与活跃遭遇卡纳入 AuraEngine 统一管线
        List<CardInstance>? externalAuras = null;
        if (AttachedPlayerEffects.TryGetValue(player, out var attachedEffects) && attachedEffects.Count > 0)
        {
            externalAuras = new List<CardInstance>(attachedEffects.Count + (activeEncounterCard != null ? 1 : 0));
            for (int e = 0; e < attachedEffects.Count; e++)
                externalAuras.Add(attachedEffects[e]);
        }
        if (activeEncounterCard != null)
        {
            externalAuras ??= new List<CardInstance>(1);
            externalAuras.Add(activeEncounterCard);
        }

        string? encId = activeEncounterCard?.Template.Id;
        var visitedEnc = GetAttachedVisitedEncounters(player);
        var transitions = AuraEngine.RecalculateAurasInternal(
            player, null, RuleScope.OutOfCombat, player.Day, player.Hour, repo,
            selectionCards: selectionCards,
            snapshotSink: null,
            externalAuraCards: externalAuras,
            currentEncounterId: encId,
            visitedEncounters: visitedEnc);

        // 非战斗光环重算后，将拥有弹药上限的卡牌当前弹药同步补满；若触发光环增弹引发 CurrentAmmo 变化，再刷新一次关联光环（修复 P1-4）
        bool anyAmmoChanged = false;
        foreach (var c in player.EnumerateAllOwnedCards())
        {
            int prevAmmo = c.CurrentAmmo;
            c.RefillAmmoToMax();
            if (c.CurrentAmmo != prevAmmo) anyAmmoChanged = true;
        }
        if (anyAmmoChanged)
        {
            var secondTransitions = AuraEngine.RecalculateAurasInternal(
                player, null, RuleScope.OutOfCombat, player.Day, player.Hour, repo,
                selectionCards: selectionCards,
                snapshotSink: null,
                externalAuraCards: externalAuras,
                currentEncounterId: encId,
                visitedEncounters: visitedEnc);
            if (secondTransitions.Count > 0)
            {
                if (transitions.Count == 0)
                    transitions = secondTransitions;
                else
                    transitions.AddRange(secondTransitions);
            }
        }

        return transitions;
    }

    private static List<(CardInstance Card, bool AllowAnyActiveIn)> CollectTriggerCandidates(
        PlayerState player,
        IReadOnlyList<CardInstance>? extraCardsForTrigger,
        CardInstance? activeEncounterCard = null)
    {
        var list = new List<(CardInstance, bool)>();
        var seen = new HashSet<CardInstance>();

        void Add(CardInstance c, bool allowAnyActiveIn)
        {
            if (seen.Add(c)) list.Add((c, allowAnyActiveIn));
        }

        // 先加入正在被交互的额外卡（如刚选中的 EncounterStep / 正在出售但仍在网格上的消耗品）
        if (extraCardsForTrigger != null)
        {
            for (int i = 0; i < extraCardsForTrigger.Count; i++)
                Add(extraCardsForTrigger[i], allowAnyActiveIn: true);
        }

        // 活跃事件/遭遇卡（如 Salvage Yard 拆卸场）纳入候选以响应售卡/购卡等局外触发
        if (activeEncounterCard != null)
            Add(activeEncounterCard, allowAnyActiveIn: false);

        for (int i = 0; i < player.Hand.Cards.Count; i++) Add(player.Hand.Cards[i], false);
        for (int i = 0; i < player.Skills.Count; i++) Add(player.Skills[i], false);
        for (int i = 0; i < player.SocketEffects.Count; i++) Add(player.SocketEffects[i], false);
        for (int i = 0; i < player.Stash.Cards.Count; i++) Add(player.Stash.Cards[i], false);
        for (int i = 0; i < player.PlayerEffects.Count; i++) Add(player.PlayerEffects[i], false);
        if (AttachedPlayerEffects.TryGetValue(player, out var attachedEffects))
        {
            for (int i = 0; i < attachedEffects.Count; i++)
                Add(attachedEffects[i], false);
        }

        return list;
    }

    private static IReadOnlyList<AbilityDefinition> OrderAbilitiesByPriority(IReadOnlyList<AbilityDefinition> abilities)
    {
        if (abilities.Count <= 1) return abilities;
        bool alreadySorted = true;
        int prevRank = GetAbilityPriorityRank(abilities[0].Priority);
        for (int i = 1; i < abilities.Count; i++)
        {
            int curRank = GetAbilityPriorityRank(abilities[i].Priority);
            if (curRank < prevRank)
            {
                alreadySorted = false;
                break;
            }
            prevRank = curRank;
        }
        if (alreadySorted) return abilities;

        var copy = new List<AbilityDefinition>(abilities.Count);
        for (int i = 0; i < abilities.Count; i++) copy.Add(abilities[i]);
        // 稳定插入排序，避免 LINQ 闭包与元组分配
        for (int i = 1; i < copy.Count; i++)
        {
            var key = copy[i];
            int keyRank = GetAbilityPriorityRank(key.Priority);
            int j = i - 1;
            while (j >= 0 && GetAbilityPriorityRank(copy[j].Priority) > keyRank)
            {
                copy[j + 1] = copy[j];
                j--;
            }
            copy[j + 1] = key;
        }
        return copy;
    }

    private static int GetAbilityPriorityRank(string? priority) => priority switch
    {
        "Immediate" => 0,
        "Highest" => 1,
        "High" => 2,
        "Medium" => 3,
        "Low" => 4,
        "Lowest" => 5,
        _ => 3
    };

    private static bool IsAbilityActiveInOutOfCombat(string? activeIn, bool inHand, bool inStash)
    {
        if (string.IsNullOrEmpty(activeIn) || activeIn == "HandAndStash")
            return true;
        if (activeIn == "HandOnly")
            return !inStash; // Hand、Skills、SocketEffects 均视为在场生效
        if (activeIn == "StashOnly")
            return inStash && !inHand;
        return true;
    }

    private static (string Section, int StartSlot) LocateCardPlacement(PlayerState player, CardInstance card)
    {
        if (player.Hand.Cards.Contains(card)) return ("Hand", card.StartSlot);
        if (player.Stash.Cards.Contains(card)) return ("Stash", card.StartSlot);
        if (player.Skills.Contains(card)) return ("Skills", -1);
        if (player.SocketEffects.Contains(card)) return ("SocketEffects", card.StartSlot);
        return ("Hand", card.StartSlot);
    }

    private static Dictionary<string, double> SnapshotPlayerAttributes(PlayerState player)
    {
        var keys = new HashSet<string>(CorePlayerAttributes, StringComparer.OrdinalIgnoreCase);
        foreach (var k in player.Attributes.Keys) keys.Add(k);
        foreach (var k in player.HourAdd.Keys) keys.Add(k);
        foreach (var k in player.DayAdd.Keys) keys.Add(k);
        foreach (var k in player.AuraAdd.Keys) keys.Add(k);

        var snap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            snap[k] = player.GetAttribute(k);
        return snap;
    }

    private static Dictionary<CardInstance, Dictionary<string, double>> SnapshotOwnedCards(PlayerState player)
    {
        var result = new Dictionary<CardInstance, Dictionary<string, double>>();
        foreach (var card in player.EnumerateAllOwnedCards())
        {
            result[card] = SnapshotSingleCardAttributes(card);
        }
        return result;
    }

    private static Dictionary<string, double> SnapshotSingleCardAttributes(CardInstance card)
    {
        var keys = new HashSet<string>(CoreCardAttributes, StringComparer.OrdinalIgnoreCase);
        foreach (var k in card.BaseAttributes.Keys) keys.Add(k);
        foreach (var k in card.PermanentAdd.Keys) keys.Add(k);
        foreach (var k in card.HourAdd.Keys) keys.Add(k);
        foreach (var k in card.DayAdd.Keys) keys.Add(k);
        foreach (var k in card.AuraAdd.Keys) keys.Add(k);
        foreach (var k in card.PermanentMult.Keys) keys.Add(k);
        foreach (var k in card.HourMult.Keys) keys.Add(k);
        foreach (var k in card.DayMult.Keys) keys.Add(k);
        foreach (var k in card.AuraMult.Keys) keys.Add(k);

        var snap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            snap[k] = card.GetAttribute(k);
        return snap;
    }

    private static void CollectDiffs(
        PlayerState player,
        Dictionary<string, double> playerBefore,
        Dictionary<CardInstance, Dictionary<string, double>> cardsBefore,
        OutOfCombatBatchResult result)
    {
        var playerKeys = new HashSet<string>(playerBefore.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in player.Attributes.Keys) playerKeys.Add(k);
        foreach (var k in player.HourAdd.Keys) playerKeys.Add(k);
        foreach (var k in player.DayAdd.Keys) playerKeys.Add(k);
        foreach (var k in player.AuraAdd.Keys) playerKeys.Add(k);

        foreach (var k in playerKeys)
        {
            double prev = playerBefore.GetValueOrDefault(k, 0);
            double cur = player.GetAttribute(k);
            if (Math.Abs(cur - prev) > 1e-6)
            {
                result.PlayerAttributeChanges.Add(new OutOfCombatPlayerAttributeChange
                {
                    Attribute = k,
                    PreviousValue = prev,
                    CurrentValue = cur
                });
                result.PlayerAttributeDeltas[k] = cur - prev;
            }
        }

        foreach (var card in player.EnumerateAllOwnedCards())
        {
            if (!cardsBefore.TryGetValue(card, out var beforeMap))
            {
                result.AffectedCards.Add(card);
                continue;
            }

            var afterKeys = new HashSet<string>(beforeMap.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in card.BaseAttributes.Keys) afterKeys.Add(k);
            foreach (var k in card.PermanentAdd.Keys) afterKeys.Add(k);
            foreach (var k in card.HourAdd.Keys) afterKeys.Add(k);
            foreach (var k in card.DayAdd.Keys) afterKeys.Add(k);
            foreach (var k in card.AuraAdd.Keys) afterKeys.Add(k);

            foreach (var k in afterKeys)
            {
                double prev = beforeMap.GetValueOrDefault(k, 0);
                double cur = card.GetAttribute(k);
                if (Math.Abs(cur - prev) > 1e-6)
                {
                    result.CardAttributeChanges.Add(new OutOfCombatCardAttributeChange
                    {
                        Card = card,
                        Attribute = k,
                        PreviousValue = prev,
                        CurrentValue = cur
                    });
                    result.AffectedCards.Add(card);
                }
            }
        }
    }
}
