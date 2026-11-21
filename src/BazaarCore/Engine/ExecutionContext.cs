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
    /// <summary>
    /// 仅在匹配 Disabled/BeforeDestroyed/Repaired 触发器及销毁变形目标求值期间临时放行已禁用卡，避免篡改 CardInstance.IsDisabled 状态。
    /// </summary>
    public bool IncludeDisabledTargets { get; set; }
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
    public double LastAttributeChangeDelta { get; set; }
    public bool IsFusionUpgrade { get; set; }
    public IReadOnlyList<CardInstance>? SelectionCards { get; set; }
    internal IReadOnlyList<CardInstance>? ExtremeCandidatePool { get; set; }
    /// <summary>可选战斗突变全局暴击率加成（如 crit_overload +50%）。</summary>
    public int CombatBonusCritChance { get; set; }
    /// <summary>可选战斗突变暴击倍率覆盖百分比（如 crit_overload 275%）。</summary>
    public int? CombatCritPercentMultiplierOverride { get; set; }
    public ActiveExecutionState ActiveExecution { get; set; } = new();

    // 供局外商店/发牌动作回调钩子
    public Action<List<CardInstance>, string>? OnDealCardsToSelection { get; set; }
    public Action<string>? OnRerollEncounter { get; set; }
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
    public Action<PlayerState, int, CardInstance?>? OnBurnApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnPoisonApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnHealApplied { get; set; }
    public Action<PlayerState, int, CardInstance?>? OnRegenApplied { get; set; }
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
    public Action<PlayerState, CardInstance>? OnCardDestroyed { get; set; }
    public Action<PlayerState, CardInstance>? OnCardUpgraded { get; set; }
    public Action<PlayerState, CardInstance>? OnCardEnchanted { get; set; }

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
            LastAttributeChangeDelta = LastAttributeChangeDelta,
            IsFusionUpgrade = IsFusionUpgrade,
            SelectionCards = SelectionCards,
            ExtremeCandidatePool = ExtremeCandidatePool,
            CombatBonusCritChance = CombatBonusCritChance,
            CombatCritPercentMultiplierOverride = CombatCritPercentMultiplierOverride,
            ActiveExecution = ActiveExecution,
            OnDealCardsToSelection = OnDealCardsToSelection,
            OnRerollEncounter = OnRerollEncounter,
            OnExitSelection = OnExitSelection,
            EmitCombatEvent = EmitCombatEvent,
            OnCardTriggeredEvent = OnCardTriggeredEvent,
            OnDetailedTriggeredEvent = OnDetailedTriggeredEvent,
            OnDamageApplied = OnDamageApplied,
            OnShieldApplied = OnShieldApplied,
            OnBurnApplied = OnBurnApplied,
            OnPoisonApplied = OnPoisonApplied,
            OnHealApplied = OnHealApplied,
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
            OnCardDestroyed = OnCardDestroyed,
            OnCardUpgraded = OnCardUpgraded,
            OnCardEnchanted = OnCardEnchanted,
            OnUnknownRuleType = OnUnknownRuleType
        };
    }
}
