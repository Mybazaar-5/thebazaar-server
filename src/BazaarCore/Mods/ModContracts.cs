using System.Collections.Frozen;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 内容提供器契约：注入自定义卡牌、技能、野怪、事件与翻译覆盖（既可由纯 JSON 数据包实现，也可由扩展程序集实现）。
/// </summary>
public interface ICustomCardProvider
{
    IEnumerable<CardTemplate> GetCustomCards(GameDataRepository baseRepo);
    IEnumerable<MonsterTemplate> GetCustomMonsters(GameDataRepository baseRepo);
    IReadOnlyDictionary<string, string> GetLocalizationOverrides(string locale);
}

/// <summary>
/// 自定义规则动作扩展点：注册新的 RuleNode.$type（必须以 "TActionMod_" 开头，严禁劫持官方核心动作名）。
/// </summary>
public interface ICustomActionHandler
{
    string ActionType { get; }
    bool Execute(RuleNode action, CardInstance source, RuleExecutionContext ctx);
}

/// <summary>
/// 自定义触发器扩展点：支持新增战斗或局外事件钩子（必须以 "TTriggerMod_" 开头）。
/// </summary>
public interface ICustomTriggerHandler
{
    string TriggerType { get; }
    bool Matches(RuleNode trigger, CardInstance listener, RuleExecutionContext ctx, string eventType);
}

/// <summary>
/// P0-22：自定义游戏模式开局与每日阶段通用调整载体（供 RunEngine / ShopEngine 统一消费）。
/// </summary>
public sealed class GameModeRunSetupAdjustment
{
    public int StartingGoldDelta { get; set; }
    public int StartingIncomeDelta { get; set; }
    public int StartingHealthDelta { get; set; }
    public int? MaxDaysOverride { get; set; }
    public double ShopPriceMultiplier { get; set; } = 1.0;
    public bool FreeFirstShopReroll { get; set; }
    public List<string> ForcedMutatorIds { get; } = new();
}

/// <summary>
/// 自定义游戏模式生命周期钩子扩展点（保留既有 3 回调 100% 向后兼容，补充带默认实现的 P0-22 钩子）。
/// </summary>
public interface IGameModeHook
{
    string ModeId { get; }
    CustomModeDefinition? Definition => null;
    void OnRunInitializing(CustomRunRuleSet rules, GameModeData modeClone) { }
    void OnDayHourScheduleResolving(int day, int hour, IList<string> candidateEncounterIds) { }
    void OnCombatStarting(PlayerState player, PlayerState opponent, ref bool sandstormEnabled) { }

    void OnRunSetup(GameModeRunSetupAdjustment adjustment, CustomRunRuleSet? rules) { }
    void OnDayStart(int day, GameModeRunSetupAdjustment dayAdjustment) { }
    void OnShopGenerated(int day, int hour, IList<string> offeredCardIds) { }
    void OnCombatModifiersBuilt(CombatModifiers modifiers, int day, bool isPvp) { }
}

/// <summary>
/// P0-22：针对 JSON 声明式 CustomModeDefinition 的通用默认模式钩子实现（零单 Mod 硬编码）。
/// </summary>
public sealed class DeclarativeGameModeHook : IGameModeHook
{
    private readonly CustomModeDefinition _def;

    public DeclarativeGameModeHook(CustomModeDefinition definition)
    {
        _def = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public string ModeId => _def.ModeId;
    public CustomModeDefinition? Definition => _def;

    public void OnRunInitializing(CustomRunRuleSet rules, GameModeData modeClone)
    {
        if (rules == null) return;
        if (_def.RuleOverrides != null)
        {
            var ov = _def.RuleOverrides;
            if (ov.StartingGold != 15) rules.StartingGold = ov.StartingGold;
            if (ov.StartingIncome != 5) rules.StartingIncome = ov.StartingIncome;
            if (ov.StartingHealth != 300) rules.StartingHealth = ov.StartingHealth;
            if (ov.StartingPrestige != 20) rules.StartingPrestige = ov.StartingPrestige;
            if (Math.Abs(ov.ShopPriceMultiplier - 1.0) > 1e-6) rules.ShopPriceMultiplier = ov.ShopPriceMultiplier;
            if (Math.Abs(ov.EnemyStatMultiplier - 1.0) > 1e-6) rules.EnemyStatMultiplier = ov.EnemyStatMultiplier;
            if (ov.DisableFatesRevive) rules.DisableFatesRevive = true;
        }

        if (_def.MutatorIds is { Count: > 0 })
        {
            foreach (var mid in _def.MutatorIds)
            {
                if (!string.IsNullOrWhiteSpace(mid)
                    && !rules.MutatorIds.Contains(mid.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    rules.MutatorIds.Add(mid.Trim());
                }
            }
        }
    }

    public void OnRunSetup(GameModeRunSetupAdjustment adjustment, CustomRunRuleSet? rules)
    {
        if (adjustment == null) return;
        if (_def.RuleOverrides != null)
        {
            var ov = _def.RuleOverrides;
            if (ov.StartingGold != 15) adjustment.StartingGoldDelta += ov.StartingGold - 15;
            if (ov.StartingIncome != 5) adjustment.StartingIncomeDelta += ov.StartingIncome - 5;
            if (ov.StartingHealth != 300) adjustment.StartingHealthDelta += ov.StartingHealth - 300;
            if (Math.Abs(ov.ShopPriceMultiplier - 1.0) > 1e-6)
                adjustment.ShopPriceMultiplier = Math.Clamp(adjustment.ShopPriceMultiplier * ov.ShopPriceMultiplier, 0.2, 5.0);
            if (ov.EndlessMaxDay > 11) adjustment.MaxDaysOverride = ov.EndlessMaxDay;
        }
        if (_def.MutatorIds is { Count: > 0 })
        {
            foreach (var mid in _def.MutatorIds)
            {
                if (!string.IsNullOrWhiteSpace(mid)
                    && !adjustment.ForcedMutatorIds.Contains(mid.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    adjustment.ForcedMutatorIds.Add(mid.Trim());
                }
            }
        }
    }
}

/// <summary>
/// 统一扩展注册表接口：供服务端 Mod 加载器收集全部内容提供器与规则钩子并冻结至运行时。
/// </summary>
public interface IModContentRegistry
{
    void RegisterCardProvider(ICustomCardProvider provider);
    void RegisterActionHandler(ICustomActionHandler handler);
    void RegisterTriggerHandler(ICustomTriggerHandler handler);
    void RegisterGameModeHook(IGameModeHook hook);
    void RegisterPipelineInterceptor(ICombatPipelineInterceptor interceptor);
}

/// <summary>
/// 线程安全、无 Mod 时零性能损耗的运行时自定义规则钩子分发器（供 AbilityExecutor 与 CombatSimulator 按需查询）。
/// </summary>
public static class ModRuntimeHooks
{
    private static readonly object LockObj = new();
    private static volatile FrozenDictionary<string, ICustomActionHandler>? _actions;
    private static volatile FrozenDictionary<string, ICustomTriggerHandler>? _triggers;
    private static volatile FrozenDictionary<string, IGameModeHook>? _modeHooks;
    private static volatile FrozenDictionary<string, MechanicDefinition>? _mechanics;
    private static volatile IReadOnlyList<ICombatPipelineInterceptor>? _pipelineInterceptors;

    public static bool HasAnyCustomActions => _actions != null;
    public static bool HasAnyCustomTriggers => _triggers != null;
    public static bool HasAnyCustomMechanics => _mechanics != null && _mechanics.Count > 0;
    public static bool HasAnyPipelineInterceptors => _pipelineInterceptors != null && _pipelineInterceptors.Count > 0;

    public static FrozenDictionary<string, MechanicDefinition>? GetRegisteredMechanics() => _mechanics;
    public static IReadOnlyList<ICombatPipelineInterceptor>? GetPipelineInterceptors() => _pipelineInterceptors;

    /// <summary>原子替换全部自定义动作/触发器/模式钩子/管线拦截器（强校验 TActionMod_ / TTriggerMod_ 安全前缀）。</summary>
    public static void SetHandlers(
        IEnumerable<ICustomActionHandler>? actions = null,
        IEnumerable<ICustomTriggerHandler>? triggers = null,
        IEnumerable<IGameModeHook>? modeHooks = null,
        IEnumerable<ICombatPipelineInterceptor>? pipelineInterceptors = null)
    {
        var actDict = new Dictionary<string, ICustomActionHandler>(StringComparer.Ordinal);
        if (actions != null)
        {
            foreach (var h in actions)
            {
                if (h == null || string.IsNullOrWhiteSpace(h.ActionType)) continue;
                if (!h.ActionType.StartsWith("TActionMod_", StringComparison.Ordinal))
                    throw new ArgumentException($"自定义动作类型 '{h.ActionType}' 必须以 'TActionMod_' 开头，禁止劫持官方核心动作名。");
                actDict[h.ActionType] = h;
            }
        }

        var trgDict = new Dictionary<string, ICustomTriggerHandler>(StringComparer.Ordinal);
        if (triggers != null)
        {
            foreach (var h in triggers)
            {
                if (h == null || string.IsNullOrWhiteSpace(h.TriggerType)) continue;
                if (!h.TriggerType.StartsWith("TTriggerMod_", StringComparison.Ordinal))
                    throw new ArgumentException($"自定义触发器类型 '{h.TriggerType}' 必须以 'TTriggerMod_' 开头，禁止劫持官方核心触发器名。");
                trgDict[h.TriggerType] = h;
            }
        }

        var modeDict = new Dictionary<string, IGameModeHook>(StringComparer.OrdinalIgnoreCase);
        if (modeHooks != null)
        {
            foreach (var m in modeHooks)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.ModeId)) continue;
                modeDict[m.ModeId.Trim()] = m;
            }
        }

        var interceptorList = new List<ICombatPipelineInterceptor>();
        if (pipelineInterceptors != null)
        {
            foreach (var p in pipelineInterceptors)
            {
                if (p != null && !string.IsNullOrWhiteSpace(p.InterceptorId))
                    interceptorList.Add(p);
            }
        }

        lock (LockObj)
        {
            _actions = actDict.Count > 0 ? actDict.ToFrozenDictionary(StringComparer.Ordinal) : null;
            _triggers = trgDict.Count > 0 ? trgDict.ToFrozenDictionary(StringComparer.Ordinal) : null;
            _modeHooks = modeDict.Count > 0 ? modeDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : null;
            _pipelineInterceptors = interceptorList.Count > 0 ? interceptorList.ToArray() : null;
        }
    }

    /// <summary>原子发布全部已校验通过的声明式沙盒自定义机制表（由 ModRegistry 维护）。</summary>
    public static void SetMechanics(IEnumerable<MechanicDefinition>? mechanics)
    {
        var dict = new Dictionary<string, MechanicDefinition>(StringComparer.OrdinalIgnoreCase);
        if (mechanics != null)
        {
            foreach (var m in mechanics)
            {
                if (m != null && !string.IsNullOrWhiteSpace(m.Id))
                    dict[m.Id.Trim()] = m;
            }
        }
        lock (LockObj)
        {
            _mechanics = dict.Count > 0 ? dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : null;
        }
    }

    /// <summary>清空所有已注册的自定义钩子与声明式机制（回滚或测试清理用）。</summary>
    public static void ClearAll()
    {
        lock (LockObj)
        {
            _actions = null;
            _triggers = null;
            _modeHooks = null;
            _mechanics = null;
            _pipelineInterceptors = null;
        }
    }

    public static bool TryExecuteCustomAction(RuleNode action, CardInstance source, RuleExecutionContext ctx, out bool handled)
    {
        var map = _actions;
        if (map == null || string.IsNullOrEmpty(action.Type) || !map.TryGetValue(action.Type, out var handler))
        {
            handled = false;
            return false;
        }
        handled = true;
        try
        {
            return handler.Execute(action, source, ctx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ModRuntimeHooks] 自定义动作 '{action.Type}' 抛出异常已熔断隔离：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool TryMatchCustomTrigger(RuleNode trigger, CardInstance listener, RuleExecutionContext ctx, string eventType, out bool handled)
    {
        var map = _triggers;
        if (map == null || string.IsNullOrEmpty(trigger.Type) || !map.TryGetValue(trigger.Type, out var handler))
        {
            handled = false;
            return false;
        }
        handled = true;
        try
        {
            return handler.Matches(trigger, listener, ctx, eventType);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ModRuntimeHooks] 自定义触发器 '{trigger.Type}' 抛出异常已熔断隔离：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool TryGetGameModeHook(string? modeId, out IGameModeHook? hook)
    {
        var map = _modeHooks;
        if (map != null && !string.IsNullOrWhiteSpace(modeId) && map.TryGetValue(modeId, out var found))
        {
            hook = found;
            return true;
        }
        hook = null;
        return false;
    }
}
