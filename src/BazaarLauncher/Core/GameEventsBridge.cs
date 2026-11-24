using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace BazaarLauncher.Core;

/// <summary>
/// D4-P1-1：统一安全的官方 Events.*.Trigger 反射桥接器。
/// 完整兼容 public / non-public 字段/属性以及 0 参 / 多参 Trigger 重载，带缓存与限流告警日志，
/// 彻底替换 Unlocker 与 GameDriver 中全部死反射调用。
/// </summary>
internal static class GameEventsBridge
{
    private const BindingFlags AllMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly ConcurrentDictionary<string, byte> LoggedWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(Type EventType, int ArgCount), MethodInfo> TriggerMethodCache = new();
    private static Type _cachedEventsType;

    /// <summary>
    /// 触发游戏静态 Events 容器上的指定事件名（支持 0 参或多参）。
    /// </summary>
    internal static bool TryTrigger(string eventName, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;

        try
        {
            _cachedEventsType ??= GameInspector.FindTypeSafe("Events", "TheBazaar.Events");
            if (_cachedEventsType == null)
            {
                LogWarningOnce("missing-events-type", "[GameEventsBridge] 未找到游戏 Events 类型，无法触发事件：" + eventName);
                return false;
            }

            object evObj = ReadMemberValue(_cachedEventsType, null, eventName);
            if (evObj == null)
            {
                LogWarningOnce("missing-static-" + eventName, $"[GameEventsBridge] 未在 {_cachedEventsType.FullName} 找到事件成员 '{eventName}'");
                return false;
            }

            return TryInvokeTrigger(evObj, eventName, args ?? Array.Empty<object>());
        }
        catch (Exception ex)
        {
            LogWarningOnce("err-static-" + eventName, $"[GameEventsBridge] 触发 Events.{eventName} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 在任意事件容器对象上触发指定成员的 Trigger 方法（同时供单元测试零 Unity 直测）。
    /// </summary>
    internal static bool TryTriggerOnContainer(object container, string eventName, params object[] args)
    {
        if (container == null || string.IsNullOrWhiteSpace(eventName)) return false;

        try
        {
            object evObj = ReadMemberValue(container.GetType(), container, eventName);
            if (evObj == null)
            {
                LogWarningOnce("missing-inst-" + container.GetType().Name + "." + eventName,
                    $"[GameEventsBridge] 未在 {container.GetType().Name} 找到事件成员 '{eventName}'");
                return false;
            }

            return TryInvokeTrigger(evObj, eventName, args ?? Array.Empty<object>());
        }
        catch (Exception ex)
        {
            LogWarningOnce("err-inst-" + eventName, $"[GameEventsBridge] 触发容器事件 {eventName} 失败：{ex.Message}");
            return false;
        }
    }

    private static object ReadMemberValue(Type type, object instance, string memberName)
    {
        FieldInfo fi = type.GetField(memberName, AllMemberFlags);
        if (fi != null) return fi.GetValue(fi.IsStatic ? null : instance);

        PropertyInfo pi = type.GetProperty(memberName, AllMemberFlags);
        if (pi != null && pi.CanRead) return pi.GetValue(pi.GetMethod?.IsStatic == true ? null : instance);

        return null;
    }

    private static bool TryInvokeTrigger(object evObj, string eventName, object[] args)
    {
        Type evType = evObj.GetType();
        MethodInfo method = TriggerMethodCache.GetOrAdd((evType, args.Length), static key => FindBestTriggerMethod(key.EventType, key.ArgCount));
        if (method == null)
        {
            LogWarningOnce("no-trigger-" + evType.FullName + "#" + args.Length,
                $"[GameEventsBridge] 事件 '{eventName}' ({evType.Name}) 未找到匹配 {args.Length} 参数的 Trigger 方法");
            return false;
        }

        ParameterInfo[] ps = method.GetParameters();
        object[] invokeArgs;
        if (ps.Length == args.Length)
        {
            invokeArgs = args;
        }
        else if (ps.Length > args.Length)
        {
            invokeArgs = new object[ps.Length];
            Array.Copy(args, invokeArgs, args.Length);
            for (int i = args.Length; i < ps.Length; i++)
            {
                invokeArgs[i] = ps[i].HasDefaultValue
                    ? ps[i].DefaultValue
                    : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
            }
        }
        else
        {
            return false;
        }

        method.Invoke(evObj, invokeArgs);
        return true;
    }

    private static MethodInfo FindBestTriggerMethod(Type evType, int argCount)
    {
        MethodInfo fallback = null;
        MethodInfo[] methods = evType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo m = methods[i];
            if (!string.Equals(m.Name, "Trigger", StringComparison.Ordinal)) continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == argCount)
            {
                return m;
            }
            if (ps.Length > argCount && ps[argCount].IsOptional && fallback == null)
            {
                fallback = m;
            }
        }
        return fallback;
    }

    private static void LogWarningOnce(string key, string message)
    {
        if (LoggedWarnings.TryAdd(key, 1))
        {
            BazaarLauncherPlugin.Log?.LogWarning(message);
        }
    }
}
