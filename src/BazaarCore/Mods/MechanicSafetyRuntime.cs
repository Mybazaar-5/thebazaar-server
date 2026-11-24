using System.Collections.Frozen;
using BazaarCore.Combat;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 通用战斗管线事件上下文（贯穿 6 大拦截点：BeforeDamage、BeforeStatusApply、BeforeCardStateChange、
/// BeforeCardFire/AfterCardFire、DelayedCallback、PostCombatCommit，以及 OnFightStart/OnCardFired/OnCustomEvent/OnTriggerEvent）。
/// </summary>
public sealed class CombatPipelineEvent
{
    public string HookName { get; init; } = string.Empty;
    public int TimeMs { get; init; }
    public PlayerState? SourcePlayer { get; init; }
    public PlayerState? TargetPlayer { get; init; }
    public CardInstance? SourceCard { get; init; }
    public CardInstance? TargetCard { get; init; }
    /// <summary>Direct | Burn | Poison | Sandstorm（用于 BeforeDamage）。</summary>
    public string? DamageType { get; init; }
    /// <summary>Shield | Burn | Poison | Heal | Regen | Haste | Slow | Freeze | Charge（用于 BeforeStatusApply）。</summary>
    public string? StatusType { get; init; }
    /// <summary>Disable | Destroy | Repair | Transform（用于 BeforeCardStateChange）。</summary>
    public string? ChangeKind { get; init; }
    /// <summary>官方触发器类型名（用于 OnTriggerEvent）。</summary>
    public string? TriggerEventType { get; init; }
    /// <summary>状态变更原因标签（如 "self_destruct" / "enemy_destroy" / "repair"）。</summary>
    public string? Reason { get; init; }
    /// <summary>自定义事件广播名（用于 OnCustomEvent）。</summary>
    public string? CustomEventName { get; init; }
    public double Amount { get; set; }
    public double PayloadValue { get; set; }
    public bool Cancelled { get; set; }
    public bool AmountModified { get; set; }
}

/// <summary>
/// 唯一受控战斗变更门面（Shield #1）：
/// 所有自定义声明式机制与 C# 管线拦截器严禁裸改内部列表或无界属性，必须经由本门面执行带强钳制、防 NaN、自动派发合法协议事件的受控操作。
/// </summary>
public interface ICombatMutationApi
{
    int TimeMs { get; }
    PlayerState Player1 { get; }
    PlayerState Player2 { get; }
    Random Rng { get; }

    void DealDamage(PlayerState targetPlayer, CardInstance? sourceCard, double amount, bool bypassShield = false, string? vfxKey = null);
    void ApplyStatus(string status, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard, PlayerState? targetPlayer, double amount);
    void ModifyCardAttribute(CardInstance targetCard, string attribute, string operation, double value, bool permanent = false, bool grantIfMissing = false);
    void ModifyPlayerAttribute(PlayerState targetPlayer, string attribute, string operation, double value);
    void ChangeCardState(CardInstance targetCard, CardInstance? sourceCard, string changeKind, string? reason = null, bool suppressNormalDestroyTriggers = false, string? vfxKey = null);
    void ForceUseCard(CardInstance targetCard, CardInstance? sourceCard);
    void EmitCustomEvent(string eventName, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard = null, double payloadValue = 0);
    bool ScheduleDelayed(int delayMs, string mechanicId, CardInstance? ownerCard, Action<ICombatMutationApi> callback);

    void SetCardFlag(CardInstance card, string flag);
    void ClearCardFlag(CardInstance card, string flag);
    bool HasCardFlag(CardInstance card, string flag);
    double ModifyCardCounter(CardInstance card, string key, string operation, double value, string? syncAttribute = null);
    double GetCardCounter(CardInstance card, string key);

    void SetCombatFlag(string flag);
    void ClearCombatFlag(string flag);
    bool HasCombatFlag(string flag);
    double ModifyCombatCounter(string key, string operation, double value);
    double GetCombatCounter(string key);

    void QueuePostCombatRemoveCard(CardInstance card, string? reason, string mechanicId);
    void QueuePostCombatModifyAttr(CardInstance card, string attribute, string operation, double value, string mechanicId, bool grantIfMissing = false);

    IReadOnlyList<CardInstance> ResolveTargets(
        MechanicTargetSpec spec,
        CardInstance selfCard,
        PlayerState ownerPlayer,
        PlayerState opponentPlayer,
        CombatPipelineEvent pipeEvent,
        Func<string, string?>? paramStringResolver = null,
        Func<string, double?>? paramNumberResolver = null);
}

/// <summary>
/// 供受信任私服服主本地高级开发使用的 C# 通用战斗管线拦截器（与声明式沙盒机制共享完全相同的 5 道运行时防线与熔断器）。
/// </summary>
public interface ICombatPipelineInterceptor
{
    string InterceptorId { get; }
    void OnPipelineEvent(CombatPipelineEvent pipeEvent, ICombatMutationApi api);
}

/// <summary>
/// 战斗模拟器回调桥接委托集（仅由 CombatSimulator 内部构造，不向第三方暴露底层内部结构）。
/// </summary>
public sealed class CombatMechanicHostCallbacks
{
    public required Func<int> GetTimeMs { get; init; }
    public required Action<PlayerState, CardInstance?, int, bool, string?> DealDamage { get; init; }
    public required Action<string, PlayerState, CardInstance?, CardInstance?, PlayerState?, int> ApplyStatus { get; init; }
    public required Action<CardInstance, CardInstance?, string, string?, bool, string?> ChangeCardState { get; init; }
    public required Action<CardInstance, CardInstance?> ForceUseCard { get; init; }
    public required Action RecalculateAuras { get; init; }
    public required Func<string?, bool> IsValidVfxKey { get; init; }
    /// <summary>可选：当机制计数器同步写入 Custom_* 属性槽发生变更时通知模拟器发射轻脉冲事件。</summary>
    public Action<CardInstance, string, double>? OnCardCounterSynced { get; init; }
}

/// <summary>
/// 单场战斗的自定义机制隔离运行时：
/// 承载 5 道安全铁闸（受控变更门面、递归/步数硬预算、单机制异常熔断器、不变量守卫、协议投影守卫）。
/// 纯原版对局不实例化本对象，保持零内存/零性能损耗。
/// </summary>
public sealed class CombatMechanicRuntime : ICombatMutationApi
{
    public const int MaxChainDepth = 16;
    public const int MaxStepsPerHook = 128;
    public const int MaxStepsPerTick = 500;
    /// <summary>D3-P2-02：延迟回调配额拆分单卡(32)/单阵营(64)/全局(128)三级分账，杜绝一方刷满饿死对手。</summary>
    public const int MaxDelayedCallbacksPerCard = 32;
    public const int MaxDelayedCallbacksPerSide = 64;
    public const int MaxDelayedCallbacksPerCombat = 128;
    public const int MaxRepeatCount = 20;
    public const int CircuitBreakerBudgetFaultLimit = 3;

    private const double MaxSafeScalar = 1_000_000.0;

    private sealed class ScheduledMechanicCallback
    {
        public required int ExecuteAtMs { get; init; }
        public required string MechanicId { get; init; }
        public CardInstance? OwnerCard { get; init; }
        public required Action<ICombatMutationApi> Callback { get; init; }
    }

    private sealed class RegisteredCardMechanic
    {
        public required PlayerState OwnerPlayer { get; init; }
        public required PlayerState OpponentPlayer { get; init; }
        public required CardInstance Card { get; init; }
        public required CardMechanicAttachment Attachment { get; init; }
        public required MechanicDefinition Definition { get; init; }
        public Dictionary<string, int> HookTriggerCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly PlayerState _p1;
    private readonly PlayerState _p2;
    private readonly Random _rng;
    private readonly CombatMechanicHostCallbacks _host;
    private readonly FrozenDictionary<string, MechanicDefinition> _definitions;
    private readonly IReadOnlyList<ICombatPipelineInterceptor> _csharpInterceptors;
    private readonly List<RegisteredCardMechanic> _bindings = new();
    private readonly List<ScheduledMechanicCallback> _delayedQueue = new();
    private readonly HashSet<string> _combatFlags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trippedBreakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _faultCounts = new(StringComparer.OrdinalIgnoreCase);

    private int _currentChainDepth;
    private int _tickStepsUsed;
    private int _currentHookStepsUsed;
    private int _delayedScheduledTotal;
    private int _delayedScheduledP1;
    private int _delayedScheduledP2;
    private readonly Dictionary<CardInstance, int> _delayedScheduledByCard = new(ReferenceEqualityComparer.Instance);
    private string? _activeChainMechanicId;
    private CardInstance? _activeChainCard;
    private bool _abortCurrentHook;

    public List<MechanicDiagnostic> Diagnostics { get; } = new();
    public List<PostCombatMutationOp> PostCombatOps { get; } = new();
    public int TrippedCircuitBreakersCount => _trippedBreakers.Count;
    public int BudgetViolationsCount { get; private set; }
    public int TotalStepsExecuted { get; private set; }

    public int TimeMs => _host.GetTimeMs();
    public PlayerState Player1 => _p1;
    public PlayerState Player2 => _p2;
    public Random Rng => _rng;

    public bool HasAnyActiveBindings => _bindings.Count > 0 || _csharpInterceptors.Count > 0;

    public CombatMechanicRuntime(
        PlayerState p1,
        PlayerState p2,
        Random rng,
        CombatMechanicHostCallbacks host,
        FrozenDictionary<string, MechanicDefinition>? definitions,
        IReadOnlyList<ICombatPipelineInterceptor>? csharpInterceptors)
    {
        _p1 = p1;
        _p2 = p2;
        _rng = rng;
        _host = host;
        _definitions = definitions ?? FrozenDictionary<string, MechanicDefinition>.Empty;
        _csharpInterceptors = csharpInterceptors ?? Array.Empty<ICombatPipelineInterceptor>();

        BindSideCards(p1, p2);
        BindSideCards(p2, p1);
    }

    /// <summary>快速检测一场战斗是否涉及任何自定义声明式机制或 C# 管线拦截器（无则零分配跳过）。</summary>
    public static bool ShouldActivate(
        PlayerState p1,
        PlayerState p2,
        IReadOnlyDictionary<string, MechanicDefinition>? definitions,
        bool hasCsharpInterceptors)
    {
        if (hasCsharpInterceptors) return true;
        if (definitions == null || definitions.Count == 0) return false;
        return SideHasCustomMechanics(p1, definitions) || SideHasCustomMechanics(p2, definitions);
    }

    /// <summary>P0-33：O(1) 判定新生成/变形产出的卡牌是否携带任意已注册机制。</summary>
    public static bool CardNeedsMechanicRuntime(
        CardInstance? card,
        IReadOnlyDictionary<string, MechanicDefinition>? definitions)
    {
        if (card == null || definitions == null || definitions.Count == 0) return false;
        var attachments = card.Template.CustomMechanics;
        if (attachments == null || attachments.Count == 0) return false;
        for (int i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            if (att != null && att.Enabled && !string.IsNullOrWhiteSpace(att.MechanicId)
                && definitions.ContainsKey(att.MechanicId.Trim()))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<CardInstance> EnumerateAllMechanicContainers(PlayerState side)
    {
        for (int i = 0; i < side.Hand.Cards.Count; i++) yield return side.Hand.Cards[i];
        for (int i = 0; i < side.Skills.Count; i++) yield return side.Skills[i];
        for (int i = 0; i < side.SocketEffects.Count; i++) yield return side.SocketEffects[i];
        for (int i = 0; i < side.Stash.Cards.Count; i++) yield return side.Stash.Cards[i];
    }

    private static bool SideHasCustomMechanics(PlayerState side, IReadOnlyDictionary<string, MechanicDefinition> definitions)
    {
        foreach (var c in EnumerateAllMechanicContainers(side))
        {
            if (CardNeedsMechanicRuntime(c, definitions))
                return true;
        }
        return false;
    }

    private void BindSideCards(PlayerState owner, PlayerState opponent)
    {
        foreach (var card in EnumerateAllMechanicContainers(owner))
        {
            RegisterCardInternal(owner, opponent, card);
        }
    }

    /// <summary>P0-33：战中动态为新生成/变形/到期还原的卡牌幂等注册机制绑定。</summary>
    public bool RegisterCard(PlayerState owner, CardInstance? card)
    {
        if (owner == null || card == null) return false;
        var opponent = ReferenceEquals(owner, _p1) ? _p2 : _p1;
        return RegisterCardInternal(owner, opponent, card);
    }

    /// <summary>P0-33：战中动态注销离开棋盘/被变形替换的卡牌机制绑定，防止僵尸触发。</summary>
    public int UnregisterCard(CardInstance? card)
    {
        if (card == null || _bindings.Count == 0) return 0;
        return _bindings.RemoveAll(b => ReferenceEquals(b.Card, card)
            || string.Equals(b.Card.InstanceId, card.InstanceId, StringComparison.Ordinal));
    }

    /// <summary>P0-33：战中变形原子替换机制绑定（先注销旧卡再注册新卡）。</summary>
    public void ReplaceCard(PlayerState owner, CardInstance? oldCard, CardInstance? newCard)
    {
        UnregisterCard(oldCard);
        RegisterCard(owner, newCard);
    }

    private bool RegisterCardInternal(PlayerState owner, PlayerState opponent, CardInstance card)
    {
        var attachments = card.Template.CustomMechanics;
        if (attachments == null || attachments.Count == 0) return false;

        bool addedAny = false;
        for (int i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            if (att == null || !att.Enabled || string.IsNullOrWhiteSpace(att.MechanicId)) continue;
            string mechId = att.MechanicId.Trim();

            // 幂等去重：同实例同机制只绑定一次
            bool alreadyBound = false;
            for (int b = 0; b < _bindings.Count; b++)
            {
                if (ReferenceEquals(_bindings[b].Card, card)
                    && string.Equals(_bindings[b].Definition.Id, mechId, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyBound = true;
                    break;
                }
            }
            if (alreadyBound) continue;

            if (_definitions.TryGetValue(mechId, out var def))
            {
                _bindings.Add(new RegisteredCardMechanic
                {
                    OwnerPlayer = owner,
                    OpponentPlayer = opponent,
                    Card = card,
                    Attachment = att,
                    Definition = def
                });
                addedAny = true;
            }
            else
            {
                Diagnostics.Add(new MechanicDiagnostic(
                    "Warning",
                    att.MechanicId,
                    card.InstanceId,
                    card.Template.Id,
                    "Bind",
                    "MECHANIC_NOT_REGISTERED",
                    $"卡牌 '{card.Template.DisplayTitle}' 引用了未注册的机制 '{att.MechanicId}'，已安全跳过。",
                    TimeMs));
            }
        }
        return addedAny;
    }

    public void ResetTickBudget()
    {
        _tickStepsUsed = 0;
    }

    internal bool TryConsumeStep(string mechanicId, CardInstance? card, string hookName)
    {
        if (_abortCurrentHook) return false;
        _currentHookStepsUsed++;
        _tickStepsUsed++;
        TotalStepsExecuted++;
        if (_currentHookStepsUsed > MaxStepsPerHook || _tickStepsUsed > MaxStepsPerTick)
        {
            _abortCurrentHook = true;
            BudgetViolationsCount++;
            RecordFault(
                mechanicId,
                card,
                hookName,
                "BUDGET_EXCEEDED",
                $"机制 '{mechanicId}' 触发执行步数上限（单钩子={_currentHookStepsUsed}/{MaxStepsPerHook}，单帧={_tickStepsUsed}/{MaxStepsPerTick}），已强制中止本次链条防止死循环。",
                tripImmediately: false);
            return false;
        }
        return true;
    }

    private string BreakerKey(string mechanicId, CardInstance? card)
        => card != null ? $"{mechanicId}@{card.InstanceId}" : mechanicId;

    private bool IsBreakerTripped(string mechanicId, CardInstance? card)
        => _trippedBreakers.Contains(BreakerKey(mechanicId, card));

    private void RecordFault(string mechanicId, CardInstance? card, string hookName, string code, string messageZh, bool tripImmediately)
    {
        string key = BreakerKey(mechanicId, card);
        int nextCount = _faultCounts.GetValueOrDefault(key, 0) + 1;
        _faultCounts[key] = nextCount;
        bool shouldTrip = tripImmediately || nextCount >= CircuitBreakerBudgetFaultLimit;
        if (shouldTrip && _trippedBreakers.Add(key))
        {
            Diagnostics.Add(new MechanicDiagnostic(
                "Error",
                mechanicId,
                card?.InstanceId,
                card?.Template.Id,
                hookName,
                "CIRCUIT_BREAKER_TRIPPED",
                $"[熔断隔离] {messageZh} 本场战斗已熔断该机制，不影响其他卡牌与正常对局。",
                TimeMs));
        }
        else
        {
            Diagnostics.Add(new MechanicDiagnostic(
                "Warning",
                mechanicId,
                card?.InstanceId,
                card?.Template.Id,
                hookName,
                code,
                messageZh,
                TimeMs));
        }
    }

    /// <summary>向全部已挂载的声明式机制与 C# 管线拦截器安全派发管线事件。</summary>
    public void DispatchPipeline(CombatPipelineEvent pipeEvent)
    {
        if (!HasAnyActiveBindings) return;
        if (_currentChainDepth >= MaxChainDepth)
        {
            BudgetViolationsCount++;
            // D3-P2-02：MaxChainDepth 超限时触发真实故障登记与即时熔断，杜绝死循环反复刷屏却不熔断
            string faultMechId = !string.IsNullOrWhiteSpace(_activeChainMechanicId)
                ? _activeChainMechanicId!
                : (pipeEvent.CustomEventName ?? pipeEvent.HookName);
            var faultCard = _activeChainCard ?? pipeEvent.SourceCard;
            RecordFault(
                faultMechId,
                faultCard,
                pipeEvent.HookName,
                "MAX_CHAIN_DEPTH",
                $"管线事件 '{pipeEvent.HookName}' 达到最大递归深度 {MaxChainDepth}，已安全截断并触发熔断防死循环。",
                tripImmediately: true);
            return;
        }

        _currentChainDepth++;
        try
        {
            // 1. 声明式沙盒机制（按绑定顺序确定性执行）
            for (int i = 0; i < _bindings.Count; i++)
            {
                var binding = _bindings[i];
                string mechId = binding.Definition.Id;
                if (IsBreakerTripped(mechId, binding.Card)) continue;

                var hooks = binding.Definition.Hooks;
                for (int h = 0; h < hooks.Count; h++)
                {
                    var hookDef = hooks[h];
                    if (!string.Equals(hookDef.Hook, pipeEvent.HookName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!MatchesHookScopeAndFilter(binding, hookDef, pipeEvent))
                        continue;

                    string hookKey = hookDef.Id ?? $"{hookDef.Hook}#{h}";
                    int fired = binding.HookTriggerCounts.GetValueOrDefault(hookKey, 0);
                    if (hookDef.MaxTriggersPerCombat.HasValue && fired >= Math.Max(0, hookDef.MaxTriggersPerCombat.Value))
                        continue;

                    binding.HookTriggerCounts[hookKey] = fired + 1;
                    int prevSteps = _currentHookStepsUsed;
                    bool prevAbort = _abortCurrentHook;
                    string? prevChainMech = _activeChainMechanicId;
                    CardInstance? prevChainCard = _activeChainCard;
                    _currentHookStepsUsed = 0;
                    _abortCurrentHook = false;
                    _activeChainMechanicId = mechId;
                    _activeChainCard = binding.Card;
                    try
                    {
                        MechanicSandboxInterpreter.ExecuteSteps(
                            hookDef.Steps,
                            binding.Definition,
                            binding.Attachment,
                            binding.Card,
                            binding.OwnerPlayer,
                            binding.OpponentPlayer,
                            pipeEvent,
                            this,
                            depth: 0);
                        EnforceCombatInvariants();
                    }
                    catch (Exception ex)
                    {
                        RecordFault(
                            mechId,
                            binding.Card,
                            pipeEvent.HookName,
                            "SANDBOX_EXCEPTION",
                            $"声明式机制 '{mechId}' 在钩子 '{pipeEvent.HookName}' 抛出异常：{ex.GetType().Name}: {ex.Message}",
                            tripImmediately: true);
                        EnforceCombatInvariants();
                    }
                    finally
                    {
                        _currentHookStepsUsed = prevSteps;
                        _abortCurrentHook = prevAbort;
                        _activeChainMechanicId = prevChainMech;
                        _activeChainCard = prevChainCard;
                    }
                }
            }

            // 2. 受信任私服本地 C# 管线拦截器（共享完全相同的熔断与不变量守卫）
            for (int i = 0; i < _csharpInterceptors.Count; i++)
            {
                var interceptor = _csharpInterceptors[i];
                if (IsBreakerTripped(interceptor.InterceptorId, null)) continue;
                int prevSteps = _currentHookStepsUsed;
                bool prevAbort = _abortCurrentHook;
                _currentHookStepsUsed = 0;
                _abortCurrentHook = false;
                try
                {
                    interceptor.OnPipelineEvent(pipeEvent, this);
                    EnforceCombatInvariants();
                }
                catch (Exception ex)
                {
                    RecordFault(
                        interceptor.InterceptorId,
                        null,
                        pipeEvent.HookName,
                        "INTERCEPTOR_EXCEPTION",
                        $"C# 管线拦截器 '{interceptor.InterceptorId}' 在 '{pipeEvent.HookName}' 抛出异常：{ex.GetType().Name}: {ex.Message}",
                        tripImmediately: true);
                    EnforceCombatInvariants();
                }
                finally
                {
                    _currentHookStepsUsed = prevSteps;
                    _abortCurrentHook = prevAbort;
                }
            }
        }
        finally
        {
            _currentChainDepth--;
        }
    }

    /// <summary>每帧推进到期的延迟回调（支持多段带间隔伤害、定时延迟引爆等任意延迟机制）。</summary>
    public void TickDelayedCallbacks(int currentTimeMs)
    {
        if (_delayedQueue.Count == 0) return;

        // 取出本帧所有到期回调，防止回调里再次 ScheduleDelayed 导致同帧无限循环
        List<ScheduledMechanicCallback>? due = null;
        for (int i = 0; i < _delayedQueue.Count; i++)
        {
            if (_delayedQueue[i].ExecuteAtMs <= currentTimeMs)
            {
                due ??= new List<ScheduledMechanicCallback>();
                due.Add(_delayedQueue[i]);
                _delayedQueue.RemoveAt(i--);
            }
        }
        if (due == null) return;

        for (int i = 0; i < due.Count; i++)
        {
            var cb = due[i];
            if (IsBreakerTripped(cb.MechanicId, cb.OwnerCard)) continue;
            int prevSteps = _currentHookStepsUsed;
            bool prevAbort = _abortCurrentHook;
            _currentHookStepsUsed = 0;
            _abortCurrentHook = false;
            try
            {
                cb.Callback(this);
                EnforceCombatInvariants();
            }
            catch (Exception ex)
            {
                RecordFault(
                    cb.MechanicId,
                    cb.OwnerCard,
                    "DelayedCallback",
                    "DELAYED_CALLBACK_EXCEPTION",
                    $"延迟机制回调 '{cb.MechanicId}' 抛出异常：{ex.GetType().Name}: {ex.Message}",
                    tripImmediately: true);
                EnforceCombatInvariants();
            }
            finally
            {
                _currentHookStepsUsed = prevSteps;
                _abortCurrentHook = prevAbort;
            }
        }
    }

    public void RunPostCombatCommit(bool p1Won)
    {
        DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "PostCombatCommit",
            TimeMs = TimeMs,
            SourcePlayer = p1Won ? _p1 : _p2,
            TargetPlayer = p1Won ? _p2 : _p1,
            PayloadValue = p1Won ? 1 : 0
        });
    }

    private bool MatchesHookScopeAndFilter(RegisteredCardMechanic binding, MechanicHookDefinition hookDef, CombatPipelineEvent ev)
    {
        var filter = hookDef.Filter;
        bool includeDisabledSelf = filter?.IncludeDisabledSelf == true
            || string.Equals(hookDef.Hook, "BeforeCardStateChange", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hookDef.Hook, "PostCombatCommit", StringComparison.OrdinalIgnoreCase);

        if (!includeDisabledSelf && (binding.Card.IsDisabled || binding.Card.IsDestroyed))
            return false;

        string scope = string.IsNullOrWhiteSpace(hookDef.Scope) ? "SelfCard" : hookDef.Scope.Trim();
        bool isCardTargetEvent = ev.TargetCard != null
            && (ev.HookName.Equals("BeforeStatusApply", StringComparison.OrdinalIgnoreCase)
                || ev.HookName.Equals("BeforeCardStateChange", StringComparison.OrdinalIgnoreCase));
        bool isRoundGlobalEvent = ev.HookName.Equals("OnFightStart", StringComparison.OrdinalIgnoreCase)
            || ev.HookName.Equals("PostCombatCommit", StringComparison.OrdinalIgnoreCase);

        // D3-P1-03：精确单边 Scope 语义（未知 Scope 返回 false，绝不再默认放行）
        bool scopeMatched = scope.ToLowerInvariant() switch
        {
            "selfcard" => isRoundGlobalEvent
                || (isCardTargetEvent
                    ? ReferenceEquals(ev.TargetCard, binding.Card)
                    : ev.HookName.Equals("BeforeStatusApply", StringComparison.OrdinalIgnoreCase)
                        ? (ev.TargetCard == null && ReferenceEquals(ev.TargetPlayer, binding.OwnerPlayer))
                        : ev.HookName.Equals("BeforeDamage", StringComparison.OrdinalIgnoreCase)
                            ? (ReferenceEquals(ev.SourceCard, binding.Card) || (ev.TargetCard == null && ReferenceEquals(ev.TargetPlayer, binding.OwnerPlayer)))
                            : (ReferenceEquals(ev.SourceCard, binding.Card) || ReferenceEquals(ev.TargetCard, binding.Card))),
            "ownerplayer" => isRoundGlobalEvent
                ? (ev.SourcePlayer == null || ReferenceEquals(ev.SourcePlayer, binding.OwnerPlayer) || ReferenceEquals(ev.TargetPlayer, binding.OwnerPlayer))
                : ReferenceEquals(ev.TargetPlayer, binding.OwnerPlayer),
            "enemyplayer" => isRoundGlobalEvent
                ? (ev.SourcePlayer == null || ReferenceEquals(ev.SourcePlayer, binding.OpponentPlayer) || ReferenceEquals(ev.TargetPlayer, binding.OpponentPlayer))
                : ReferenceEquals(ev.TargetPlayer, binding.OpponentPlayer),
            "ownerboard" => isRoundGlobalEvent
                || (isCardTargetEvent
                    ? ReferenceEquals(ev.TargetPlayer, binding.OwnerPlayer)
                    : ReferenceEquals(ev.SourcePlayer, binding.OwnerPlayer)),
            "enemyboard" => isRoundGlobalEvent
                || (isCardTargetEvent
                    ? ReferenceEquals(ev.TargetPlayer, binding.OpponentPlayer)
                    : ReferenceEquals(ev.SourcePlayer, binding.OpponentPlayer)),
            "global" or "any" => true,
            _ => false
        };
        if (!scopeMatched) return false;

        if (filter == null) return true;

        if (filter.OnlyWhenOwnerTarget == true && ev.TargetPlayer != binding.OwnerPlayer) return false;
        if (filter.OnlyWhenSelfTarget == true && ev.TargetCard != binding.Card) return false;
        if (filter.OnlyWhenSelfSource == true && ev.SourceCard != binding.Card) return false;
        if (filter.OnlyWhenAdjacentSource == true)
        {
            if (ev.SourceCard == null || !binding.OwnerPlayer.Hand.GetNeighbors(binding.Card).Contains(ev.SourceCard))
                return false;
        }

        if (filter.DamageTypes is { Count: > 0 } &&
            (string.IsNullOrEmpty(ev.DamageType) || !filter.DamageTypes.Any(d => d.Equals(ev.DamageType, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (filter.StatusTypes is { Count: > 0 } &&
            (string.IsNullOrEmpty(ev.StatusType) || !filter.StatusTypes.Any(s => s.Equals(ev.StatusType, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (filter.ChangeKinds is { Count: > 0 } &&
            (string.IsNullOrEmpty(ev.ChangeKind) || !filter.ChangeKinds.Any(c => c.Equals(ev.ChangeKind, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (filter.TriggerEventTypes is { Count: > 0 } &&
            (string.IsNullOrEmpty(ev.TriggerEventType) || !filter.TriggerEventTypes.Any(t => t.Equals(ev.TriggerEventType, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (!string.IsNullOrEmpty(filter.ReasonEquals) &&
            !string.Equals(ev.Reason, filter.ReasonEquals, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(filter.ReasonNotEquals) &&
            string.Equals(ev.Reason, filter.ReasonNotEquals, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(filter.CustomEventName) &&
            !string.Equals(ev.CustomEventName, filter.CustomEventName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.RequireCardFlags is { Count: > 0 })
        {
            for (int i = 0; i < filter.RequireCardFlags.Count; i++)
                if (!HasCardFlag(binding.Card, filter.RequireCardFlags[i])) return false;
        }
        if (filter.ForbidCardFlags is { Count: > 0 })
        {
            for (int i = 0; i < filter.ForbidCardFlags.Count; i++)
                if (HasCardFlag(binding.Card, filter.ForbidCardFlags[i])) return false;
        }
        if (filter.RequireCombatFlags is { Count: > 0 })
        {
            for (int i = 0; i < filter.RequireCombatFlags.Count; i++)
                if (!HasCombatFlag(filter.RequireCombatFlags[i])) return false;
        }
        if (filter.ForbidCombatFlags is { Count: > 0 })
        {
            for (int i = 0; i < filter.ForbidCombatFlags.Count; i++)
                if (HasCombatFlag(filter.ForbidCombatFlags[i])) return false;
        }

        return true;
    }

    // ── Shield #3 / #4：状态不变量硬守卫 ────────────────────────────────────────
    private void EnforceCombatInvariants()
    {
        ClampPlayerInvariants(_p1);
        ClampPlayerInvariants(_p2);
    }

    private static void ClampPlayerInvariants(PlayerState p)
        => MechanicSafeAttributeWriter.ClampPlayerInvariants(p);

    // ── Shield #1：受控变更门面 ICombatMutationApi 实现 ──────────────────────────
    private static double SanitizeNumber(double v, double min = -MaxSafeScalar, double max = MaxSafeScalar)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        return Math.Clamp(v, min, max);
    }

    private static double ApplyMathOp(double current, string? op, double val)
    {
        val = SanitizeNumber(val);
        double res = (op ?? "Add").ToLowerInvariant() switch
        {
            "set" => val,
            "subtract" => current - val,
            "multiply" => current * SanitizeNumber(val, -100, 100),
            _ => current + val
        };
        return SanitizeNumber(res);
    }

    public void DealDamage(PlayerState targetPlayer, CardInstance? sourceCard, double amount, bool bypassShield = false, string? vfxKey = null)
    {
        int dmg = (int)Math.Round(SanitizeNumber(amount, 0, MaxSafeScalar));
        if (dmg <= 0) return;
        string? safeVfx = (!string.IsNullOrWhiteSpace(vfxKey) && _host.IsValidVfxKey(vfxKey)) ? vfxKey : null;
        _host.DealDamage(targetPlayer, sourceCard, dmg, bypassShield, safeVfx);
    }

    public void ApplyStatus(string status, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard, PlayerState? targetPlayer, double amount)
    {
        if (string.IsNullOrWhiteSpace(status) || !MechanicDefinition.AllowedStatuses.Contains(status)) return;
        int amt = (int)Math.Round(SanitizeNumber(amount, 0, MaxSafeScalar));
        if (amt <= 0) return;
        _host.ApplyStatus(status, actorPlayer, sourceCard, targetCard, targetPlayer, amt);
    }

    public static bool IsOutputActionAttribute(string attribute)
        => !string.IsNullOrWhiteSpace(attribute)
           && MechanicSafeAttributeWriter.ProtectedOutputAttributes.Contains(attribute.Trim());

    public static bool CardSupportsOutputAttribute(CardInstance card, string attribute)
        => MechanicSafeAttributeWriter.HasInherentProtectedAttribute(card, attribute);

    public void ModifyCardAttribute(CardInstance targetCard, string attribute, string operation, double value, bool permanent = false, bool grantIfMissing = false)
    {
        if (targetCard == null || string.IsNullOrWhiteSpace(attribute)) return;
        var res = MechanicSafeAttributeWriter.ApplyCardAttribute(
            targetCard,
            attribute,
            operation,
            value,
            permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat,
            grantIfMissing,
            _definitions);
        if (res.Applied)
            _host.RecalculateAuras();
    }

    public void ModifyPlayerAttribute(PlayerState targetPlayer, string attribute, string operation, double value)
    {
        if (targetPlayer == null || string.IsNullOrWhiteSpace(attribute)) return;
        string trimmed = attribute.Trim();
        if (!MechanicDefinition.AllowedPlayerAttributes.Contains(trimmed)) return;
        if (!double.IsFinite(value)) return;

        string normalizedAttr = PlayerState.NormalizeAttributeName(trimmed);
        string op = string.IsNullOrWhiteSpace(operation) ? "Add" : operation.Trim();

        double safeVal = op.Equals("Multiply", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(value, MechanicSafeAttributeWriter.MinMultiplyStep, MechanicSafeAttributeWriter.MaxMultiplyStep)
            : SanitizeNumber(value);

        // PercentDamageReduction 钳制最终值在 [0, 100]
        if (normalizedAttr.Equals("PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
        {
            double cur = targetPlayer.GetAttribute("PercentDamageReduction");
            double next = Math.Clamp(ApplyMathOp(cur, op, safeVal), 0, 100);
            targetPlayer.ModifyAttribute("PercentDamageReduction", "Set", next, Engine.DurationTarget.Combat);
        }
        else
        {
            targetPlayer.ModifyAttribute(normalizedAttr, op, safeVal, Engine.DurationTarget.Combat);
        }

        MechanicSafeAttributeWriter.ClampPlayerInvariants(targetPlayer);
        _host.RecalculateAuras();
    }

    public void ChangeCardState(CardInstance targetCard, CardInstance? sourceCard, string changeKind, string? reason = null, bool suppressNormalDestroyTriggers = false, string? vfxKey = null)
    {
        if (targetCard == null || string.IsNullOrWhiteSpace(changeKind)) return;
        string? safeVfx = (!string.IsNullOrWhiteSpace(vfxKey) && _host.IsValidVfxKey(vfxKey)) ? vfxKey : null;
        _host.ChangeCardState(targetCard, sourceCard, changeKind, reason, suppressNormalDestroyTriggers, safeVfx);
    }

    public void ForceUseCard(CardInstance targetCard, CardInstance? sourceCard)
    {
        if (targetCard == null || targetCard.IsDisabled || targetCard.IsDestroyed) return;
        _host.ForceUseCard(targetCard, sourceCard);
    }

    public void EmitCustomEvent(string eventName, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard = null, double payloadValue = 0)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;
        DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnCustomEvent",
            TimeMs = TimeMs,
            SourcePlayer = actorPlayer,
            TargetPlayer = actorPlayer == _p1 ? _p2 : _p1,
            SourceCard = sourceCard,
            TargetCard = targetCard ?? sourceCard,
            CustomEventName = eventName.Trim(),
            PayloadValue = SanitizeNumber(payloadValue)
        });
    }

    public bool ScheduleDelayed(int delayMs, string mechanicId, CardInstance? ownerCard, Action<ICombatMutationApi> callback)
    {
        if (callback == null) return false;
        // D3-P2-02：延迟回调按单卡(32) / 单阵营(64) / 全局(128)三级分账，避免一方耗尽配额波及对手
        if (ownerCard != null && _delayedScheduledByCard.GetValueOrDefault(ownerCard, 0) >= MaxDelayedCallbacksPerCard)
        {
            BudgetViolationsCount++;
            RecordFault(
                mechanicId,
                ownerCard,
                "ScheduleDelayed",
                "DELAYED_CARD_QUOTA_EXCEEDED",
                $"机制 '{mechanicId}' 单卡延迟回调注册次数超过上限 {MaxDelayedCallbacksPerCard}，已拒绝继续排程。",
                tripImmediately: false);
            return false;
        }

        int side = 0; // 1 = P1, 2 = P2, 0 = neutral
        if (ownerCard != null)
        {
            if (_p1.Hand.Cards.Contains(ownerCard) || _p1.Skills.Contains(ownerCard) || _p1.Stash.Cards.Contains(ownerCard) || _p1.SocketEffects.Contains(ownerCard))
                side = 1;
            else if (_p2.Hand.Cards.Contains(ownerCard) || _p2.Skills.Contains(ownerCard) || _p2.Stash.Cards.Contains(ownerCard) || _p2.SocketEffects.Contains(ownerCard))
                side = 2;
        }
        if (side == 1 && _delayedScheduledP1 >= MaxDelayedCallbacksPerSide)
        {
            BudgetViolationsCount++;
            RecordFault(
                mechanicId,
                ownerCard,
                "ScheduleDelayed",
                "DELAYED_SIDE_QUOTA_EXCEEDED",
                $"机制 '{mechanicId}' P1 阵营延迟回调注册次数超过上限 {MaxDelayedCallbacksPerSide}，已拒绝继续排程。",
                tripImmediately: false);
            return false;
        }
        if (side == 2 && _delayedScheduledP2 >= MaxDelayedCallbacksPerSide)
        {
            BudgetViolationsCount++;
            RecordFault(
                mechanicId,
                ownerCard,
                "ScheduleDelayed",
                "DELAYED_SIDE_QUOTA_EXCEEDED",
                $"机制 '{mechanicId}' P2 阵营延迟回调注册次数超过上限 {MaxDelayedCallbacksPerSide}，已拒绝继续排程。",
                tripImmediately: false);
            return false;
        }

        if (_delayedScheduledTotal >= MaxDelayedCallbacksPerCombat)
        {
            BudgetViolationsCount++;
            RecordFault(
                mechanicId,
                ownerCard,
                "ScheduleDelayed",
                "DELAYED_QUOTA_EXCEEDED",
                $"机制 '{mechanicId}' 延迟回调注册次数超过单战上限 {MaxDelayedCallbacksPerCombat}，已拒绝继续排程。",
                tripImmediately: false);
            return false;
        }
        int safeDelay = Math.Clamp(delayMs, 20, 60000);
        _delayedScheduledTotal++;
        if (side == 1) _delayedScheduledP1++;
        else if (side == 2) _delayedScheduledP2++;
        if (ownerCard != null)
            _delayedScheduledByCard[ownerCard] = _delayedScheduledByCard.GetValueOrDefault(ownerCard, 0) + 1;
        _delayedQueue.Add(new ScheduledMechanicCallback
        {
            ExecuteAtMs = TimeMs + safeDelay,
            MechanicId = mechanicId,
            OwnerCard = ownerCard,
            Callback = callback
        });
        return true;
    }

    public void SetCardFlag(CardInstance card, string flag)
    {
        if (card == null || string.IsNullOrWhiteSpace(flag)) return;
        card.CombatCustomFlags.Add(flag.Trim());
    }

    public void ClearCardFlag(CardInstance card, string flag)
    {
        if (card == null || string.IsNullOrWhiteSpace(flag)) return;
        card.CombatCustomFlags.Remove(flag.Trim());
    }

    public bool HasCardFlag(CardInstance card, string flag)
        => card != null && !string.IsNullOrWhiteSpace(flag) && card.CombatCustomFlags.Contains(flag.Trim());

    public double ModifyCardCounter(CardInstance card, string key, string operation, double value, string? syncAttribute = null)
    {
        if (card == null || string.IsNullOrWhiteSpace(key)) return 0;
        string trimmed = key.Trim();
        double cur = card.CombatCustomCounters.GetValueOrDefault(trimmed, 0);
        double next = ApplyMathOp(cur, operation, value);
        card.CombatCustomCounters[trimmed] = next;

        if (!string.IsNullOrWhiteSpace(syncAttribute) && MechanicDefinition.AllowedSyncAttributes.Contains(syncAttribute))
        {
            double curAttr = card.GetAttribute(syncAttribute);
            if (Math.Abs(next - curAttr) > 1e-6)
            {
                card.ModifyAttribute(syncAttribute, "Add", next - curAttr, Engine.DurationTarget.Combat);
                _host.OnCardCounterSynced?.Invoke(card, syncAttribute, next);
            }
        }
        return next;
    }

    public double GetCardCounter(CardInstance card, string key)
        => card != null && !string.IsNullOrWhiteSpace(key) ? card.CombatCustomCounters.GetValueOrDefault(key.Trim(), 0) : 0;

    public void SetCombatFlag(string flag)
    {
        if (!string.IsNullOrWhiteSpace(flag)) _combatFlags.Add(flag.Trim());
    }

    public void ClearCombatFlag(string flag)
    {
        if (!string.IsNullOrWhiteSpace(flag)) _combatFlags.Remove(flag.Trim());
    }

    public bool HasCombatFlag(string flag)
        => !string.IsNullOrWhiteSpace(flag) && _combatFlags.Contains(flag.Trim());

    public double ModifyCombatCounter(string key, string operation, double value)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        string trimmed = key.Trim();
        double cur = _combatCounters.GetValueOrDefault(trimmed, 0);
        double next = ApplyMathOp(cur, operation, value);
        _combatCounters[trimmed] = next;
        return next;
    }

    public double GetCombatCounter(string key)
        => !string.IsNullOrWhiteSpace(key) ? _combatCounters.GetValueOrDefault(key.Trim(), 0) : 0;

    private bool IsCardOwnedByPlayer1(CardInstance card)
    {
        if (_p1.Hand.Cards.Contains(card) || _p1.Stash.Cards.Contains(card)
            || _p1.Skills.Contains(card) || _p1.SocketEffects.Contains(card))
            return true;
        if (_p1.CombatSourceCardMap != null && _p1.CombatSourceCardMap.ContainsKey(card.InstanceId))
            return true;
        return false;
    }

    public void QueuePostCombatRemoveCard(CardInstance card, string? reason, string mechanicId)
    {
        if (card == null) return;
        // D3-P1-05：严格校验归属 _p1（真实玩家），杜绝敌方 P2 幽灵/野怪机制反向污染玩家局外存档
        if (!IsCardOwnedByPlayer1(card)) return;
        if (PostCombatOps.Any(o => o.OpKind == "RemoveCard" && string.Equals(o.CardInstanceId, card.InstanceId, StringComparison.Ordinal)))
            return;
        PostCombatOps.Add(new PostCombatMutationOp
        {
            OpKind = "RemoveCard",
            CardInstanceId = card.InstanceId,
            Reason = reason,
            SourceMechanicId = mechanicId
        });
    }

    public void QueuePostCombatModifyAttr(CardInstance card, string attribute, string operation, double value, string mechanicId, bool grantIfMissing = false)
    {
        if (card == null || string.IsNullOrWhiteSpace(attribute)) return;
        // D3-P1-05：严格校验归属 _p1，且按统一写手防 NaN
        if (!IsCardOwnedByPlayer1(card)) return;
        if (!double.IsFinite(value)) return;
        PostCombatOps.Add(new PostCombatMutationOp
        {
            OpKind = "ModifyCardPermanentAttribute",
            CardInstanceId = card.InstanceId,
            Attribute = attribute.Trim(),
            Operation = string.IsNullOrWhiteSpace(operation) ? "Add" : operation.Trim(),
            Value = SanitizeNumber(value),
            GrantIfMissing = grantIfMissing,
            SourceMechanicId = mechanicId
        });
    }

    public IReadOnlyList<CardInstance> ResolveTargets(
        MechanicTargetSpec spec,
        CardInstance selfCard,
        PlayerState ownerPlayer,
        PlayerState opponentPlayer,
        CombatPipelineEvent pipeEvent,
        Func<string, string?>? paramStringResolver = null,
        Func<string, double?>? paramNumberResolver = null)
    {
        string mode = (!string.IsNullOrWhiteSpace(spec.ModeParam) ? paramStringResolver?.Invoke(spec.ModeParam!) : null)
            ?? spec.Mode
            ?? "Self";

        var candidates = new List<CardInstance>();
        bool incDis = spec.IncludeDisabled;

        void AddFiltered(IEnumerable<CardInstance> src)
        {
            foreach (var c in src)
            {
                if (!incDis && (c.IsDisabled || c.IsDestroyed)) continue;
                candidates.Add(c);
            }
        }

        switch (mode.ToLowerInvariant())
        {
            case "self":
                if (incDis || (!selfCard.IsDisabled && !selfCard.IsDestroyed)) candidates.Add(selfCard);
                break;
            case "hooktarget":
                if (pipeEvent.TargetCard != null && (incDis || (!pipeEvent.TargetCard.IsDisabled && !pipeEvent.TargetCard.IsDestroyed)))
                    candidates.Add(pipeEvent.TargetCard);
                break;
            case "hooksource":
                if (pipeEvent.SourceCard != null && (incDis || (!pipeEvent.SourceCard.IsDisabled && !pipeEvent.SourceCard.IsDestroyed)))
                    candidates.Add(pipeEvent.SourceCard);
                break;
            case "allallies":
                AddFiltered(ownerPlayer.Hand.Cards);
                break;
            case "otherallies":
                AddFiltered(ownerPlayer.Hand.Cards.Where(c => c != selfCard));
                break;
            case "allenemies":
                AddFiltered(opponentPlayer.Hand.Cards);
                break;
            case "adjacent":
                AddFiltered(ownerPlayer.Hand.GetNeighbors(selfCard, incDis));
                break;
            case "left":
            {
                var left = ownerPlayer.Hand.GetLeftNeighbor(selfCard, incDis);
                if (left != null && (incDis || (!left.IsDisabled && !left.IsDestroyed))) candidates.Add(left);
                break;
            }
            case "right":
            {
                var right = ownerPlayer.Hand.GetRightNeighbor(selfCard, incDis);
                if (right != null && (incDis || (!right.IsDisabled && !right.IsDestroyed))) candidates.Add(right);
                break;
            }
            case "leftmostally":
            {
                var leftMost = ownerPlayer.Hand.Cards.Where(c => incDis || (!c.IsDisabled && !c.IsDestroyed)).OrderBy(c => c.StartSlot).FirstOrDefault();
                if (leftMost != null) candidates.Add(leftMost);
                break;
            }
            case "rightmostally":
            {
                var rightMost = ownerPlayer.Hand.Cards.Where(c => incDis || (!c.IsDisabled && !c.IsDestroyed)).OrderByDescending(c => c.StartSlot).FirstOrDefault();
                if (rightMost != null) candidates.Add(rightMost);
                break;
            }
            case "randomallies":
                AddFiltered(ownerPlayer.Hand.Cards);
                break;
            case "randomenemies":
                AddFiltered(opponentPlayer.Hand.Cards);
                break;
        }

        // 应用标签条件过滤
        var reqTags = new List<string>();
        if (spec.RequireTags is { Count: > 0 }) reqTags.AddRange(spec.RequireTags);
        if (!string.IsNullOrWhiteSpace(spec.RequireTagParam))
        {
            string? pTag = paramStringResolver?.Invoke(spec.RequireTagParam!);
            if (!string.IsNullOrWhiteSpace(pTag) && !pTag.Equals("Any", StringComparison.OrdinalIgnoreCase))
                reqTags.Add(pTag);
        }

        if (reqTags.Count > 0)
        {
            candidates.RemoveAll(c => !reqTags.Any(t => c.HasTag(t)));
        }
        if (spec.RequireHiddenTags is { Count: > 0 })
        {
            candidates.RemoveAll(c => !spec.RequireHiddenTags.Any(ht => c.HasHiddenTag(ht)));
        }

        // 随机抽样截断
        if (mode.Equals("RandomAllies", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("RandomEnemies", StringComparison.OrdinalIgnoreCase))
        {
            int want = spec.Count ?? 1;
            if (!string.IsNullOrWhiteSpace(spec.CountParam))
            {
                double? pCnt = paramNumberResolver?.Invoke(spec.CountParam!);
                if (pCnt.HasValue) want = (int)Math.Round(pCnt.Value);
            }
            want = Math.Clamp(want, 1, 10);
            if (candidates.Count > want)
            {
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                }
                candidates.RemoveRange(want, candidates.Count - want);
            }
        }

        return candidates;
    }
}
