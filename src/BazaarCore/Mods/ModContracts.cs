using System.Collections.Frozen;
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
/// 自定义游戏模式生命周期钩子扩展点。
/// </summary>
public interface IGameModeHook
{
    string ModeId { get; }
    void OnRunInitializing(CustomRunRuleSet rules, GameModeData modeClone);
    void OnDayHourScheduleResolving(int day, int hour, IList<string> candidateEncounterIds);
    void OnCombatStarting(PlayerState player, PlayerState opponent, ref bool sandstormEnabled);
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
}

/// <summary>
/// 线程安全、无 Mod 时零性能损耗的运行时自定义规则钩子分发器（供 AbilityExecutor 在 default 分支按需查询）。
/// </summary>
public static class ModRuntimeHooks
{
    private static readonly object LockObj = new();
    private static volatile FrozenDictionary<string, ICustomActionHandler>? _actions;
    private static volatile FrozenDictionary<string, ICustomTriggerHandler>? _triggers;
    private static volatile FrozenDictionary<string, IGameModeHook>? _modeHooks;

    public static bool HasAnyCustomActions => _actions != null;
    public static bool HasAnyCustomTriggers => _triggers != null;

    /// <summary>原子替换全部自定义动作/触发器/模式钩子（强校验 TActionMod_ / TTriggerMod_ 安全前缀）。</summary>
    public static void SetHandlers(
        IEnumerable<ICustomActionHandler>? actions = null,
        IEnumerable<ICustomTriggerHandler>? triggers = null,
        IEnumerable<IGameModeHook>? modeHooks = null)
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

        lock (LockObj)
        {
            _actions = actDict.Count > 0 ? actDict.ToFrozenDictionary(StringComparer.Ordinal) : null;
            _triggers = trgDict.Count > 0 ? trgDict.ToFrozenDictionary(StringComparer.Ordinal) : null;
            _modeHooks = modeDict.Count > 0 ? modeDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : null;
        }
    }

    /// <summary>清空所有已注册的自定义钩子（回滚或测试清理用）。</summary>
    public static void ClearAll()
    {
        lock (LockObj)
        {
            _actions = null;
            _triggers = null;
            _modeHooks = null;
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
        return handler.Execute(action, source, ctx);
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
        return handler.Matches(trigger, listener, ctx, eventType);
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
