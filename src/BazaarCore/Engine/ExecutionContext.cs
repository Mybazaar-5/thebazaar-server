using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

public enum RuleScope
{
    OutOfCombat,
    InCombat
}

public enum DurationTarget
{
    Permanent,
    Combat,
    Hour,
    Day
}

/// <summary>
/// 共享于同一次规则执行链路的当前能力/动作标识（跨 WithCards 复制引用）。
/// </summary>
public sealed class ActiveExecutionState
{
    public string? AbilityId { get; set; }
    public string? ActionType { get; set; }
    /// <summary>引起当前反应/能力执行的真实触发源卡牌实例 ID（P1-08，用于 CombatEvent.TriggerSourceCardInstanceId 精确透传）。</summary>
    public string? TriggerSourceCardInstanceId { get; set; }
    /// <summary>
    /// 仅在匹配 Disabled/BeforeDestroyed/Repaired 触发器及销毁变形目标求值期间临时放行已禁用卡，避免篡改 CardInstance.IsDisabled 状态。
    /// </summary>
    public bool IncludeDisabledTargets { get; set; }
    /// <summary>当前正在执行 TTriggerOnCardFired 施放的源卡牌（供 P0-05 单次施放原子暴击判定）。</summary>
    public CardInstance? CurrentCastCard { get; set; }
    /// <summary>标识当前是否正处于 CurrentCastCard 的 TTriggerOnCardFired 动作执行帧内（避免被动响应串用施放暴击态）。</summary>
    public bool InCardFiredExecution { get; set; }
    /// <summary>当前单次施放已掷出的共享暴击结果（同次施放全部可暴击动作共享 1 次 Roll）。</summary>
    public bool? CurrentCastCritRoll { get; set; }
    /// <summary>当前单次施放是否至少有 1 个可暴击动作实际暴击生效（整次施放循环末尾仅派发 1 次 TTriggerOnCardCritted）。</summary>
    public bool CurrentCastDidCrit { get; set; }
    /// <summary>同一顶层触发/施放链共享的执行上下文 ID（P2-10，用于 CombatSimEventEffectExecuted/AuraExecuted 因果链聚合）。</summary>
    public string? ExecutionContextId { get; set; }
}

/// <summary>
/// 统一规则执行与求值上下文，同时兼容局内事件（商店/事件/卖牌/升级）和战斗即时结算。
/// </summary>
public sealed class RuleExecutionContext
{
    public GameDataRepository Repo { get; }
    public RuleScope Scope { get; }
    public PlayerState SelfPlayer { get; }
    public PlayerState? OpponentPlayer { get; }
    public Random Rng { get; }

    /// <summary>标识本地玩家 ID，用于区分 TTargetPlayerAbsolute（始终指向本地玩家）。</summary>
    public string LocalPlayerId { get; init; } = "player";

    public int CurrentDay { get; set; } = 1;
    public int CurrentHour { get; set; } = 1;
    public int TimeMs { get; set; }
    public string CombatType { get; set; } = "PVP";
    public string PlayMode { get; set; } = "Unranked";
    public string? CurrentEncounterId { get; set; }

    public CardInstance? SelfCard { get; set; }
    public CardInstance? TriggerSourceCard { get; set; }
    public CardInstance? TriggerTargetCard { get; set; }
    /// <summary>逐目标动作/光环求值时的当前目标卡（供 P0-06 Target == null 节点及嵌套 Modifier.Value 自动解析）。</summary>
    public CardInstance? CurrentTargetCard { get; set; }
    public double LastAttributeChangeDelta { get; set; }
    public bool IsFusionUpgrade { get; set; }
    public IReadOnlyList<CardInstance>? SelectionCards { get; set; }
    internal IReadOnlyList<CardInstance>? ExtremeCandidatePool { get; set; }
    /// <summary>可选战斗突变全局暴击率加成（如 crit_overload +50%）。</summary>
    public int CombatBonusCritChance { get; set; }
    /// <summary>可选战斗突变暴击倍率覆盖百分比（如 crit_overload 275%）。</summary>
    public int? CombatCritPercentMultiplierOverride { get; set; }
    /// <summary>可选流派位掩码解析器（未显式传参时由 SpawnQueryEngine 自动继承流派倾向加权）。</summary>
    public Func<string, ulong>? ArchetypeResolver { get; set; }
    /// <summary>可选自定义规则集（未显式传参时由 SpawnQueryEngine 自动继承高阶概率与流派倍率）。</summary>
    public CustomRunRuleSet? CustomRules { get; set; }
    public ActiveExecutionState ActiveExecution { get; set; } = new();

    // 供局外商店/发牌动作回调钩子
    public Action<List<CardInstance>, string>? OnDealCardsToSelection { get; set; }
    public Action<string>? OnRerollEncounter { get; set; }
    /// <summary>带可选 SpawnCount 覆盖的重掷回调（P1-17，优先于 OnRerollEncounter 触发以保留刷新槽位数语义）。</summary>
    public Action<string, int?>? OnRerollEncounterWithCount { get; set; }
    public Action? OnExitSelection { get; set; }

    // 战斗日志与规则变更回调
    public Action<CombatEvent>? EmitCombatEvent { get; set; }
    public Action<string, PlayerState, PlayerState, CardInstance?>? OnCardTriggeredEvent { get; set; }
    /// <summary>
    /// 精确战斗/局外触发事件回调（依次为 eventType, actorPlayer, sourceCard, targetCard, subjectPlayer, attrChanged, prevVal, curVal）。
    /// </summary>
    public Action<string, PlayerState, CardInstance?, CardInstance?, PlayerState?, string?, double, double>? OnDetailedTriggeredEvent { get; set; }
    public Action<PlayerState, int, bool, CardInstance?>? OnDamageApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnShieldApplied { get; set; }
    /// <summary>带暴击标记的护盾施加回调（P1-07；优先于 3 参 OnShieldApplied 调用，向后兼容既有绑定）。</summary>
    public Action<PlayerState, int, bool, CardInstance?>? OnShieldAppliedWithCrit { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnBurnApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnPoisonApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnHealApplied { get; set; }
    /// <summary>带暴击标记的治疗施加回调（P1-07；优先于 3 参 OnHealApplied 调用，向后兼容既有绑定）。</summary>
    public Action<PlayerState, int, bool, CardInstance?>? OnHealAppliedWithCrit { get; set; }
    public Action<PlayerState, int, CardInstance?, DurationTarget>? OnRegenApplied { get; set; }
    public Action<CardInstance, int, CardInstance?>? OnHasteApplied { get; set; }
    public Action<CardInstance, int, CardInstance?>? OnSlowApplied { get; set; }
    public Action<CardInstance, int, CardInstance?>? OnFreezeApplied { get; set; }
    public Action<CardInstance, int, CardInstance?>? OnChargeApplied { get; set; }
    public Action<CardInstance, string, double>? OnActionCostSpent { get; set; }
    public Action<CardInstance, CardInstance?>? OnForceUseRequested { get; set; }
    public Action? OnBeginSandstormRequested { get; set; }
    public Action<CardInstance>? OnQuestCompleted { get; set; }
    public Action<CardInstance, string, double, double, double>? OnQuestUpdated { get; set; }
    public Action<CardInstance, string, string, double, int>? OnTimedCardAttributeModifierApplied { get; set; }
    public Action<PlayerState, string, string, double, int>? OnTimedPlayerAttributeModifierApplied { get; set; }
    public Action<PlayerState, CardInstance>? OnCardSpawned { get; set; }
    public Action<PlayerState, CardInstance, CardInstance>? OnCardTransformed { get; set; }
    /// <summary>
    /// 批量变形完成回调（P1-10，参数：owner, originalCard, replacements, isTemporary, timedDurationMs, originalSection, originalSlot）。
    /// 用于登记 UntilEndOfCombat 临时变形供战后还原，以及登记战内限时变形到期还原。
    /// </summary>
    public Action<PlayerState, CardInstance, List<CardInstance>, bool, int?, string, int>? OnCardTransformBatch { get; set; }
    public Action<PlayerState, CardInstance>? OnCardDestroyed { get; set; }
    public Action<PlayerState, CardInstance>? OnCardUpgraded { get; set; }
    public Action<PlayerState, CardInstance>? OnCardEnchanted { get; set; }
    /// <summary>
    /// V14 通用卡牌状态变更事前拦截回调（参数：targetCard, sourceCard, changeKind[Disable/Destroy/Repair/Transform], reason）。
    /// 返回 false 表示拦截并取消该次状态变更（例如拦截对不可复原卡牌的 Repair）。
    /// </summary>
    public Func<CardInstance, CardInstance?, string, string?, bool>? OnBeforeCardStateChange { get; set; }

    /// <summary>
    /// 可选未知规则节点遥测回调（依次传入分类 "TAction"/"TAuraAction"/"TTrigger" 与具体 $type 名称）；未设置时零开销。
    /// </summary>
    public Action<string, string>? OnUnknownRuleType { get; set; }

    [ThreadStatic]
    private static Action<string, string>? t_unknownRuleTypeObserver;

    /// <summary>判断当前线程是否启用了未知规则类型遥测观察器（用于热路径零开销门控）。</summary>
    public static bool HasUnknownRuleTypeObserver => t_unknownRuleTypeObserver != null;

    /// <summary>在当前线程作用域内启用未知 $type 遥测回调，Dispose 时自动恢复先前观察器。</summary>
    public static IDisposable BeginUnknownRuleTypeScope(Action<string, string> observer)
    {
        var previous = t_unknownRuleTypeObserver;
        t_unknownRuleTypeObserver = observer;
        return new UnknownRuleTypeScopeGuard(previous);
    }

    internal static void NotifyUnknownRuleType(RuleExecutionContext? ctx, string category, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return;
        ctx?.OnUnknownRuleType?.Invoke(category, typeName);
        t_unknownRuleTypeObserver?.Invoke(category, typeName);
    }

    private sealed class UnknownRuleTypeScopeGuard(Action<string, string>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            t_unknownRuleTypeObserver = previous;
        }
    }

    public RuleExecutionContext(
        GameDataRepository repo,
        RuleScope scope,
        PlayerState selfPlayer,
        PlayerState? opponentPlayer = null,
        Random? rng = null)
    {
        Repo = repo;
        Scope = scope;
        SelfPlayer = selfPlayer;
        OpponentPlayer = opponentPlayer;
        Rng = rng ?? new Random(12345);
        CurrentDay = selfPlayer.Day;
        CurrentHour = selfPlayer.Hour;
    }

    public RuleExecutionContext WithCards(CardInstance? selfCard, CardInstance? sourceCard = null, CardInstance? targetCard = null)
    {
        return new RuleExecutionContext(Repo, Scope, SelfPlayer, OpponentPlayer, Rng)
        {
            LocalPlayerId = LocalPlayerId,
            CurrentDay = CurrentDay,
            CurrentHour = CurrentHour,
            TimeMs = TimeMs,
            CombatType = CombatType,
            PlayMode = PlayMode,
            CurrentEncounterId = CurrentEncounterId,
            SelfCard = selfCard,
            TriggerSourceCard = sourceCard ?? TriggerSourceCard,
            TriggerTargetCard = targetCard ?? TriggerTargetCard,
            CurrentTargetCard = CurrentTargetCard,
            LastAttributeChangeDelta = LastAttributeChangeDelta,
            IsFusionUpgrade = IsFusionUpgrade,
            SelectionCards = SelectionCards,
            ExtremeCandidatePool = ExtremeCandidatePool,
            CombatBonusCritChance = CombatBonusCritChance,
            CombatCritPercentMultiplierOverride = CombatCritPercentMultiplierOverride,
            ArchetypeResolver = ArchetypeResolver,
            CustomRules = CustomRules,
            ActiveExecution = ActiveExecution,
            OnDealCardsToSelection = OnDealCardsToSelection,
            OnRerollEncounter = OnRerollEncounter,
            OnRerollEncounterWithCount = OnRerollEncounterWithCount,
            OnExitSelection = OnExitSelection,
            EmitCombatEvent = EmitCombatEvent,
            OnCardTriggeredEvent = OnCardTriggeredEvent,
            OnDetailedTriggeredEvent = OnDetailedTriggeredEvent,
            OnDamageApplied = OnDamageApplied,
            OnShieldApplied = OnShieldApplied,
            OnShieldAppliedWithCrit = OnShieldAppliedWithCrit,
            OnBurnApplied = OnBurnApplied,
            OnPoisonApplied = OnPoisonApplied,
            OnHealApplied = OnHealApplied,
            OnHealAppliedWithCrit = OnHealAppliedWithCrit,
            OnRegenApplied = OnRegenApplied,
            OnHasteApplied = OnHasteApplied,
            OnSlowApplied = OnSlowApplied,
            OnFreezeApplied = OnFreezeApplied,
            OnChargeApplied = OnChargeApplied,
            OnActionCostSpent = OnActionCostSpent,
            OnForceUseRequested = OnForceUseRequested,
            OnBeginSandstormRequested = OnBeginSandstormRequested,
            OnQuestCompleted = OnQuestCompleted,
            OnQuestUpdated = OnQuestUpdated,
            OnTimedCardAttributeModifierApplied = OnTimedCardAttributeModifierApplied,
            OnTimedPlayerAttributeModifierApplied = OnTimedPlayerAttributeModifierApplied,
            OnCardSpawned = OnCardSpawned,
            OnCardTransformed = OnCardTransformed,
            OnCardTransformBatch = OnCardTransformBatch,
            OnCardDestroyed = OnCardDestroyed,
            OnCardUpgraded = OnCardUpgraded,
            OnCardEnchanted = OnCardEnchanted,
            OnBeforeCardStateChange = OnBeforeCardStateChange,
            OnUnknownRuleType = OnUnknownRuleType
        };
    }
}
