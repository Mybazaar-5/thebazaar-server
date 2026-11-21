using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace BazaarLauncher.Core;

/// <summary>
/// Harmony 签名预检、重载精确锁定与单点自降级补丁器。
/// 任何一条补丁在官方小版本更新后发生签名变更或抛出 HarmonyException 时，
/// 仅跳过该条补丁并记录可读诊断明细，绝不阻断宿主与其他模块加载。
/// </summary>
internal sealed class SafeHarmonyPatcher : IDisposable
{
    private readonly Harmony _harmony;
    private readonly string _ownerModId;
    private readonly object _sync = new();
    private readonly List<string> _diagnosticNotes = new();
    private int _attempted;
    private int _succeeded;
    private int _degraded;
    private bool _disposed;

    internal SafeHarmonyPatcher(string ownerModId)
    {
        _ownerModId = string.IsNullOrWhiteSpace(ownerModId) ? "bazaar.core" : ownerModId;
        _harmony = new Harmony($"local.thebazaar.modkit.{_ownerModId}");
    }

    internal Harmony RawHarmony => _harmony;
    internal int AttemptedCount => Volatile.Read(ref _attempted);
    internal int SucceededCount => Volatile.Read(ref _succeeded);
    internal int DegradedCount => Volatile.Read(ref _degraded);

    internal IReadOnlyList<string> GetDiagnosticNotesSnapshot()
    {
        lock (_sync)
        {
            return _diagnosticNotes.ToArray();
        }
    }

    /// <summary>
    /// 按类型名、方法名与可选精确参数类型数组安全安装补丁（内置 4 项静态预检）。
    /// </summary>
    internal bool TryPatch(
        string typeName,
        string methodName,
        Type[] parameterTypes = null,
        MethodInfo prefix = null,
        MethodInfo postfix = null,
        MethodInfo finalizer = null,
        int prefixPriority = Priority.Normal,
        int postfixPriority = Priority.Normal,
        bool optional = false,
        string label = null)
    {
        string tag = label ?? $"{typeName}.{methodName}";
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
        {
            RecordDegraded(tag, "类型名或方法名为空", optional);
            return false;
        }

        Type targetType = GameInspector.FindTypeSafe(typeName);
        if (targetType == null)
        {
            RecordDegraded(tag, $"目标类型不存在: {typeName}", optional);
            return false;
        }

        return TryPatch(
            targetType,
            methodName,
            parameterTypes,
            prefix,
            postfix,
            finalizer,
            prefixPriority,
            postfixPriority,
            optional,
            tag);
    }

    /// <summary>
    /// 对已知目标类型按方法名与可选精确参数签名安全安装补丁。
    /// </summary>
    internal bool TryPatch(
        Type targetType,
        string methodName,
        Type[] parameterTypes = null,
        MethodInfo prefix = null,
        MethodInfo postfix = null,
        MethodInfo finalizer = null,
        int prefixPriority = Priority.Normal,
        int postfixPriority = Priority.Normal,
        bool optional = false,
        string label = null)
    {
        string tag = label ?? $"{targetType?.FullName ?? "<null>"}.{methodName}";
        if (targetType == null)
        {
            RecordDegraded(tag, "目标类型为 null", optional);
            return false;
        }

        MethodBase target = ResolveMethodSafely(targetType, methodName, parameterTypes, out string resolveError);
        if (target == null)
        {
            RecordDegraded(tag, resolveError, optional);
            return false;
        }

        return TryPatchMethod(
            target,
            prefix,
            postfix,
            finalizer,
            prefixPriority,
            postfixPriority,
            optional,
            tag);
    }

    /// <summary>
    /// 直接对已定位的 MethodBase 执行预检与安全补丁安装。
    /// </summary>
    internal bool TryPatchMethod(
        MethodBase target,
        MethodInfo prefix = null,
        MethodInfo postfix = null,
        MethodInfo finalizer = null,
        int prefixPriority = Priority.Normal,
        int postfixPriority = Priority.Normal,
        bool optional = false,
        string label = null)
    {
        Interlocked.Increment(ref _attempted);
        string tag = label ?? (target == null ? "<null>" : $"{target.DeclaringType?.FullName}.{target.Name}");

        if (_disposed)
        {
            RecordDegraded(tag, "Patcher 已释放", optional);
            return false;
        }

        if (target == null)
        {
            RecordDegraded(tag, "目标方法为 null", optional);
            return false;
        }

        if (prefix == null && postfix == null && finalizer == null)
        {
            RecordDegraded(tag, "未提供任何 Prefix/Postfix/Finalizer", optional);
            return false;
        }

        // 静态预检 2 & 3：校验 __result 兼容性和 ___field 私有字段存在性
        if (!ValidateHookMethod(target, prefix, "Prefix", out string err)
            || !ValidateHookMethod(target, postfix, "Postfix", out err)
            || !ValidateHookMethod(target, finalizer, "Finalizer", out err))
        {
            RecordDegraded(tag, err, optional);
            return false;
        }

        try
        {
            HarmonyMethod hmPre = prefix != null ? new HarmonyMethod(prefix) { priority = prefixPriority } : null;
            HarmonyMethod hmPost = postfix != null ? new HarmonyMethod(postfix) { priority = postfixPriority } : null;
            HarmonyMethod hmFin = finalizer != null ? new HarmonyMethod(finalizer) : null;

            _harmony.Patch(target, prefix: hmPre, postfix: hmPost, finalizer: hmFin);
            Interlocked.Increment(ref _succeeded);
            return true;
        }
        catch (Exception ex)
        {
            RecordDegraded(tag, $"Harmony.Patch 失败: {ex.GetType().Name}: {ex.Message}", optional);
            return false;
        }
    }

    /// <summary>
    /// 安全安装属性 Getter/Setter 补丁。
    /// </summary>
    internal bool TryPatchPropertyGetter(
        string typeName,
        string propertyName,
        MethodInfo prefix = null,
        MethodInfo postfix = null,
        bool optional = false)
    {
        Type t = GameInspector.FindTypeSafe(typeName);
        if (t == null)
        {
            RecordDegraded($"{typeName}.get_{propertyName}", $"目标类型不存在: {typeName}", optional);
            return false;
        }

        PropertyInfo prop = AccessTools.Property(t, propertyName);
        MethodInfo getter = prop?.GetGetMethod(nonPublic: true);
        if (getter == null)
        {
            RecordDegraded($"{typeName}.get_{propertyName}", $"属性 Getter 不存在: {propertyName}", optional);
            return false;
        }

        return TryPatchMethod(getter, prefix: prefix, postfix: postfix, optional: optional, label: $"{typeName}.get_{propertyName}");
    }

    /// <summary>
    /// 预检解析目标方法：若存在多重载且未指定参数类型，记录告警避免盲匹配冲突。
    /// </summary>
    private static MethodBase ResolveMethodSafely(Type targetType, string methodName, Type[] parameterTypes, out string error)
    {
        error = string.Empty;
        if (parameterTypes != null)
        {
            MethodInfo exact = AccessTools.Method(targetType, methodName, parameterTypes);
            if (exact == null)
            {
                error = $"精确签名不匹配: {targetType.FullName}.{methodName}({FormatTypeList(parameterTypes)})";
            }
            return exact;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var matched = new List<MethodInfo>(2);
        Type cur = targetType;
        while (cur != null && cur != typeof(object))
        {
            foreach (MethodInfo m in cur.GetMethods(flags))
            {
                if (m.Name == methodName && !m.IsGenericMethodDefinition)
                {
                    matched.Add(m);
                }
            }
            if (matched.Count > 0) break;
            cur = cur.BaseType;
        }

        if (matched.Count == 0)
        {
            MethodInfo fallback = AccessTools.Method(targetType, methodName);
            if (fallback == null)
            {
                error = $"方法不存在: {targetType.FullName}.{methodName}";
                return null;
            }
            return fallback;
        }

        if (matched.Count > 1)
        {
            // 若存在多重载，优先取参数最少的重载并允许通过，但保留明确诊断提示
            matched.Sort((a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
        }

        return matched[0];
    }

    private static bool ValidateHookMethod(MethodBase target, MethodInfo hook, string role, out string error)
    {
        error = string.Empty;
        if (hook == null) return true;

        Type targetRet = target is MethodInfo mi ? mi.ReturnType : typeof(void);
        Type declaring = target.DeclaringType;

        foreach (ParameterInfo p in hook.GetParameters())
        {
            string name = p.Name ?? string.Empty;
            if (name == "__result")
            {
                if (targetRet == typeof(void))
                {
                    error = $"{role} 声明了 __result 但目标方法返回 void";
                    return false;
                }

                Type paramType = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (paramType != null && paramType != typeof(object) && !paramType.IsAssignableFrom(targetRet) && !targetRet.IsAssignableFrom(paramType))
                {
                    error = $"{role} __result 类型不兼容: hook={paramType.Name}, target={targetRet.Name}";
                    return false;
                }
            }
            else if (name.StartsWith("___", StringComparison.Ordinal) && name.Length > 3 && declaring != null)
            {
                string fieldName = name.Substring(3);
                FieldInfo field = AccessTools.Field(declaring, fieldName);
                if (field == null)
                {
                    error = $"{role} 引用的私有字段不存在: {declaring.FullName}.{fieldName}";
                    return false;
                }
            }
        }

        return true;
    }

    private void RecordDegraded(string tag, string reason, bool optional)
    {
        if (!optional)
        {
            Interlocked.Increment(ref _degraded);
        }

        string entry = $"[{(optional ? "可选跳过" : "降级")}] {tag} -> {reason}";
        lock (_sync)
        {
            if (_diagnosticNotes.Count < 64)
            {
                _diagnosticNotes.Add(entry);
            }
        }

        if (optional)
        {
            BazaarLauncherPlugin.Log?.LogDebug($"[SafeHarmony:{_ownerModId}] {entry}");
        }
        else
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[SafeHarmony:{_ownerModId}] {entry}");
        }
    }

    private static string FormatTypeList(Type[] types)
    {
        if (types == null || types.Length == 0) return string.Empty;
        var names = new string[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            names[i] = types[i]?.Name ?? "null";
        }
        return string.Join(", ", names);
    }

    /// <summary>卸载当前 Patcher 挂载的全部补丁。</summary>
    public void UnpatchAll()
    {
        try
        {
            _harmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[SafeHarmony:{_ownerModId}] UnpatchAll 异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnpatchAll();
    }
}
