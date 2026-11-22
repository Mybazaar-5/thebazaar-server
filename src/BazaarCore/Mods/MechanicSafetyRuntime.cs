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
    void ModifyCardAttribute(CardInstance targetCard, string attribute, string operation, double value, bool permanent = false);
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
    void QueuePostCombatModifyAttr(CardInstance card, string attribute, string operation, double value, string mechanicId);

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
    public const int MaxDelayedCallbacksPerCombat = 64;
    public const int MaxRepeatCount = 20;
    public const int CircuitBreakerBudgetFaultLimit = 3;

    private const double MinCooldownMs = 1000.0;
    private const double MaxCooldownMs = 60000.0;
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
        return SideHasCustomMechanics(p1) || SideHasCustomMechanics(p2);
    }

    private static bool SideHasCustomMechanics(PlayerState side)
    {
        foreach (var c in side.EnumerateHandAndSkills())
        {
            if (c.Template.CustomMechanics is { Count: > 0 })
                return true;
        }
        return false;
    }

    private void BindSideCards(PlayerState owner, PlayerState opponent)
    {
        foreach (var card in owner.EnumerateHandAndSkills())
        {
            var attachments = card.Template.CustomMechanics;
            if (attachments == null || attachments.Count == 0) continue;
            for (int i = 0; i < attachments.Count; i++)
            {
                var att = attachments[i];
                if (att == null || !att.Enabled || string.IsNullOrWhiteSpace(att.MechanicId)) continue;
                if (_definitions.TryGetValue(att.MechanicId.Trim(), out var def))
                {
                    _bindings.Add(new RegisteredCardMechanic
                    {
                        OwnerPlayer = owner,
                        OpponentPlayer = opponent,
                        Card = card,
                        Attachment = att,
                        Definition = def
                    });
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
                        0));
                }
            }
        }
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
            Diagnostics.Add(new MechanicDiagnostic(
                "Warning",
                pipeEvent.CustomEventName ?? pipeEvent.HookName,
                pipeEvent.SourceCard?.InstanceId,
                pipeEvent.SourceCard?.Template.Id,
                pipeEvent.HookName,
                "MAX_CHAIN_DEPTH",
                $"管线事件 '{pipeEvent.HookName}' 达到最大递归深度 {MaxChainDepth}，已安全截断防死循环。",
                TimeMs));
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
                    _currentHookStepsUsed = 0;
                    _abortCurrentHook = false;
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

        string scope = string.IsNullOrWhiteSpace(hookDef.Scope) ? "SelfCard" : hookDef.Scope;
        bool scopeMatched = scope.ToLowerInvariant() switch
        {
            "selfcard" => ev.HookName is "OnFightStart" or "PostCombatCommit"
                || ev.SourceCard == binding.Card
                || ev.TargetCard == binding.Card
                || (ev.HookName == "BeforeDamage" && ev.TargetPlayer == binding.OwnerPlayer)
                || (ev.HookName == "BeforeStatusApply" && (ev.TargetCard == binding.Card || ev.TargetPlayer == binding.OwnerPlayer)),
            "ownerplayer" => ev.TargetPlayer == binding.OwnerPlayer || ev.SourcePlayer == binding.OwnerPlayer,
            "enemyplayer" => ev.TargetPlayer == binding.OpponentPlayer || ev.SourcePlayer == binding.OpponentPlayer,
            "ownerboard" => ev.SourcePlayer == binding.OwnerPlayer || ev.TargetPlayer == binding.OwnerPlayer,
            "enemyboard" => ev.SourcePlayer == binding.OpponentPlayer || ev.TargetPlayer == binding.OpponentPlayer,
            "any" => true,
            _ => true
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
    {
        if (p.Shield < 0) p.Shield = 0;
        if (p.Burn < 0) p.Burn = 0;
        if (p.Poison < 0) p.Poison = 0;
        if (p.Attributes.TryGetValue("HealthRegen", out double reg) && reg < 0) p.Attributes["HealthRegen"] = 0;
        if (p.HealthMax < 1) p.Attributes["HealthMax"] = 1;

        foreach (var c in p.EnumerateHandAndSkills())
        {
            if (c.HasteMs < 0) c.HasteMs = 0;
            if (c.SlowMs < 0) c.SlowMs = 0;
            if (c.FreezeMs < 0) c.FreezeMs = 0;
            if (c.CooldownProgressMs < 0) c.CooldownProgressMs = 0;
            // 保护 CooldownMax 与 PercentCooldownReduction 不被自定义机制改出 0 秒死循环或 NaN
            SanitizeDict(c.CombatAdd);
            SanitizeDict(c.CombatMult);
            if (c.CombatAdd.TryGetValue("PercentCooldownReduction", out double pcr) && pcr > 90)
                c.CombatAdd["PercentCooldownReduction"] = 90;
        }
    }

    private static void SanitizeDict(Dictionary<string, double> dict)
    {
        if (dict.Count == 0) return;
        List<string>? bad = null;
        foreach (var (k, v) in dict)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                bad ??= new List<string>();
                bad.Add(k);
            }
        }
        if (bad != null)
        {
            foreach (var k in bad) dict[k] = 0;
        }
    }

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

    public void ModifyCardAttribute(CardInstance targetCard, string attribute, string operation, double value, bool permanent = false)
    {
        if (targetCard == null || string.IsNullOrWhiteSpace(attribute)) return;
        double safeVal = SanitizeNumber(value);
        string op = string.IsNullOrWhiteSpace(operation) ? "Add" : operation;

        // 冷却属性强钳制：确保有效冷却绝不跌破 1000ms（杜绝 0 秒无限开火）
        if (attribute.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase))
        {
            double curCd = targetCard.GetAttribute("CooldownMax");
            double nextCd = ApplyMathOp(curCd, op, safeVal);
            nextCd = Math.Clamp(nextCd, MinCooldownMs, MaxCooldownMs);
            double delta = nextCd - curCd;
            targetCard.ModifyAttribute("CooldownMax", "Add", delta, permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat);
        }
        else if (attribute.Equals("PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
        {
            double curPcr = targetCard.GetAttribute("PercentCooldownReduction");
            double nextPcr = Math.Clamp(ApplyMathOp(curPcr, op, safeVal), -200, 90);
            double delta = nextPcr - curPcr;
            targetCard.ModifyAttribute("PercentCooldownReduction", "Add", delta, permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat);
        }
        else if (attribute.Equals("Multicast", StringComparison.OrdinalIgnoreCase))
        {
            double curMc = targetCard.GetAttribute("Multicast");
            double nextMc = Math.Clamp(ApplyMathOp(curMc, op, safeVal), 1, 10);
            double delta = nextMc - curMc;
            targetCard.ModifyAttribute("Multicast", "Add", delta, permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat);
        }
        else
        {
            if (op.Equals("Set", StringComparison.OrdinalIgnoreCase))
            {
                double cur = targetCard.GetAttribute(attribute);
                targetCard.ModifyAttribute(attribute, "Add", safeVal - cur, permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat);
            }
            else
            {
                targetCard.ModifyAttribute(attribute, op, safeVal, permanent ? Engine.DurationTarget.Permanent : Engine.DurationTarget.Combat);
            }
        }
        _host.RecalculateAuras();
    }

    public void ModifyPlayerAttribute(PlayerState targetPlayer, string attribute, string operation, double value)
    {
        if (targetPlayer == null || string.IsNullOrWhiteSpace(attribute)) return;
        double cur = targetPlayer.GetAttribute(attribute);
        double next = ApplyMathOp(cur, operation, value);
        if (attribute.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            next = Math.Clamp(next, 1, MaxSafeScalar);
        else if (attribute.Equals("Shield", StringComparison.OrdinalIgnoreCase)
                 || attribute.Equals("Burn", StringComparison.OrdinalIgnoreCase)
                 || attribute.Equals("Poison", StringComparison.OrdinalIgnoreCase)
                 || attribute.Equals("Regen", StringComparison.OrdinalIgnoreCase))
            next = Math.Clamp(next, 0, MaxSafeScalar);
        else if (attribute.Equals("PercentDamageReduction", StringComparison.OrdinalIgnoreCase))
            next = Math.Clamp(next, 0, 100);

        targetPlayer.Attributes[attribute] = next;
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
            card.ModifyAttribute(syncAttribute, "Add", next - curAttr, Engine.DurationTarget.Combat);
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

    public void QueuePostCombatRemoveCard(CardInstance card, string? reason, string mechanicId)
    {
        if (card == null) return;
        // 仅允许操作玩家1侧（真实玩家）的持久化卡牌，禁止重复入队
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

    public void QueuePostCombatModifyAttr(CardInstance card, string attribute, string operation, double value, string mechanicId)
    {
        if (card == null || string.IsNullOrWhiteSpace(attribute)) return;
        PostCombatOps.Add(new PostCombatMutationOp
        {
            OpKind = "ModifyCardPermanentAttribute",
            CardInstanceId = card.InstanceId,
            Attribute = attribute.Trim(),
            Operation = string.IsNullOrWhiteSpace(operation) ? "Add" : operation.Trim(),
            Value = SanitizeNumber(value),
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
