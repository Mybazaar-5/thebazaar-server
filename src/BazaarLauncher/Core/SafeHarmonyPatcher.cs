using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace BazaarLauncher.Core;

/// <summary>D4-P2-1：补丁重要度分级。</summary>
public enum PatchSeverity
{
    Critical = 0,
    Optional = 1,
}

/// <summary>
/// D4-P2-1：零 Unity 依赖的确定性方法重载选择器纯 .NET 内核。
/// 当未指定 parameterTypes 命中多个重载时，按 (参数数, 参数类型全名序列, 返回类型全名) 稳定字典序选取唯一确定性重载，彻底消除反射枚举顺序漂移。
/// </summary>
public static class HarmonyOverloadSelector
{
    public static MethodInfo SelectDeterministic(IEnumerable<MethodInfo> candidates)
    {
        if (candidates == null) return null;
        MethodInfo best = null;
        string bestKey = null;
        int bestParamCount = int.MaxValue;

        foreach (MethodInfo m in candidates)
        {
            if (m == null) continue;
            var ps = m.GetParameters();
            int pc = ps.Length;
            string key = BuildSignatureKey(m, ps);
            if (best == null || pc < bestParamCount || (pc == bestParamCount && string.CompareOrdinal(key, bestKey) < 0))
            {
                best = m;
                bestParamCount = pc;
                bestKey = key;
            }
        }
        return best;
    }

    private static string BuildSignatureKey(MethodInfo m, ParameterInfo[] ps)
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(m.DeclaringType?.FullName ?? string.Empty);
        sb.Append('.');
        sb.Append(m.Name);
        sb.Append('(');
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ps[i].ParameterType?.FullName ?? ps[i].ParameterType?.Name ?? string.Empty);
        }
        sb.Append(")->");
        sb.Append(m.ReturnType?.FullName ?? string.Empty);
        return sb.ToString();
    }
}

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
    private readonly List<string> _failedLabels = new();
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
    internal int FailedCount => Math.Max(0, AttemptedCount - SucceededCount);

    internal IReadOnlyList<string> GetDiagnosticNotesSnapshot()
    {
        lock (_sync)
        {
            return _diagnosticNotes.ToArray();
        }
    }

    /// <summary>返回所有未安装成功（含可选跳过与降级）的补丁标签快照，杜绝纯数字黑盒。</summary>
    internal IReadOnlyList<string> GetFailedLabelsSnapshot()
    {
        lock (_sync)
        {
            return _failedLabels.ToArray();
        }
    }

    /// <summary>
    /// 格式化补丁安装汇总文本：全部成功时输出 "成功=N/N"，存在任何失败项时明文附带失败的 patch label 清单。
    /// </summary>
    internal string FormatSummary(bool includeReasons = false)
    {
        int succeeded = SucceededCount;
        int attempted = AttemptedCount;
        string[] failedLabels;
        string[] notes;
        lock (_sync)
        {
            failedLabels = _failedLabels.ToArray();
            notes = includeReasons ? _diagnosticNotes.ToArray() : Array.Empty<string>();
        }

        if (failedLabels.Length == 0 && succeeded >= attempted)
        {
            return $"成功={succeeded}/{attempted}";
        }

        string labelList = failedLabels.Length > 0 ? string.Join(", ", failedLabels) : "<unknown>";
        if (includeReasons && notes.Length > 0)
        {
            return $"成功={succeeded}/{attempted}, 失败=[{labelList}], 明细=[{string.Join("; ", notes)}]";
        }
        return $"成功={succeeded}/{attempted}, 失败=[{labelList}]";
    }

    /// <summary>
    /// 输出补丁汇总日志：全成功时记录 Info；只要存在任何失败项，即使用 Warning 明文打印失败的 patch label 清单与诊断明细。
    /// </summary>
    internal void LogSummary(string prefix)
    {
        string head = string.IsNullOrWhiteSpace(prefix) ? $"[SafeHarmony:{_ownerModId}] 补丁汇总" : prefix;
        if (FailedCount > 0 || GetFailedLabelsSnapshot().Count > 0)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"{head} ({FormatSummary(includeReasons: true)})");
        }
        else
        {
            BazaarLauncherPlugin.Log?.LogInfo($"{head} ({FormatSummary(includeReasons: false)})");
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
            Interlocked.Increment(ref _attempted);
            RecordDegraded(tag, "类型名或方法名为空", optional);
            return false;
        }

        Type targetType = GameInspector.FindTypeSafe(typeName);
        if (targetType == null)
        {
            Interlocked.Increment(ref _attempted);
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
            Interlocked.Increment(ref _attempted);
            RecordDegraded(tag, "目标类型为 null", optional);
            return false;
        }

        MethodBase target = ResolveMethodSafely(targetType, methodName, parameterTypes, out string resolveError);
        if (target == null)
        {
            Interlocked.Increment(ref _attempted);
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
        => TryPatchMethodCore(target, prefix, postfix, finalizer, prefixPriority, postfixPriority, optional, label, countAttempt: true);

    private bool TryPatchMethodCore(
        MethodBase target,
        MethodInfo prefix,
        MethodInfo postfix,
        MethodInfo finalizer,
        int prefixPriority,
        int postfixPriority,
        bool optional,
        string label,
        bool countAttempt)
    {
        if (countAttempt)
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
        // 一旦目标方法已存在但钩子签名/字段不兼容，无论 optional 是否为 true 都强制输出 Warning，避免死码静默潜伏
        if (!ValidateHookMethod(target, prefix, "Prefix", out string err)
            || !ValidateHookMethod(target, postfix, "Postfix", out err)
            || !ValidateHookMethod(target, finalizer, "Finalizer", out err))
        {
            RecordDegraded(tag, err, optional, forceWarning: true);
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
            RecordDegraded(tag, $"Harmony.Patch 失败: {ex.GetType().Name}: {ex.Message}", optional, forceWarning: true);
            return false;
        }
    }

    /// <summary>
    /// 安全安装属性 Getter/Setter 补丁（D4-P2-1：入口即计入 _attempted，确保缺失类型/Getter 时统计准确）。
    /// </summary>
    internal bool TryPatchPropertyGetter(
        string typeName,
        string propertyName,
        MethodInfo prefix = null,
        MethodInfo postfix = null,
        bool optional = false)
    {
        Interlocked.Increment(ref _attempted);
        Type t = GameInspector.FindTypeSafe(typeName);
        if (t == null)
        {
            RecordDegraded($"{typeName}.get_{propertyName}", $"目标类型不存在: {typeName}", optional);
            return false;
        }

        PropertyInfo prop = FindPropertyInHierarchy(t, propertyName);
        MethodInfo getter = prop?.GetGetMethod(nonPublic: true);
        if (getter == null)
        {
            RecordDegraded($"{typeName}.get_{propertyName}", $"属性 Getter 不存在: {propertyName}", optional);
            return false;
        }

        return TryPatchMethodCore(getter, prefix: prefix, postfix: postfix, finalizer: null, prefixPriority: Priority.Normal, postfixPriority: Priority.Normal, optional: optional, label: $"{typeName}.get_{propertyName}", countAttempt: false);
    }

    private const BindingFlags DeclaredAnyFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// 使用纯 BCL 反射沿继承链查找精确参数签名匹配的方法，避免在非 Unity 测试宿主中触发 Unity-Mono 版 AccessTools 静态构造异常。
    /// </summary>
    private static MethodInfo FindExactMethodInHierarchy(Type targetType, string methodName, Type[] parameterTypes)
    {
        if (targetType == null || string.IsNullOrEmpty(methodName) || parameterTypes == null) return null;

        Type cur = targetType;
        while (cur != null)
        {
            foreach (MethodInfo m in cur.GetMethods(DeclaredAnyFlags))
            {
                if (m.Name != methodName || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length != parameterTypes.Length) continue;

                bool allMatch = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType != parameterTypes[i])
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch) return m;
            }
            cur = cur.BaseType;
        }
        return null;
    }

    private static PropertyInfo FindPropertyInHierarchy(Type targetType, string propertyName)
    {
        if (targetType == null || string.IsNullOrEmpty(propertyName)) return null;
        Type cur = targetType;
        while (cur != null)
        {
            PropertyInfo prop = cur.GetProperty(propertyName, DeclaredAnyFlags);
            if (prop != null) return prop;
            cur = cur.BaseType;
        }
        return null;
    }

    private static FieldInfo FindFieldInHierarchy(Type targetType, string fieldName)
    {
        if (targetType == null || string.IsNullOrEmpty(fieldName)) return null;
        Type cur = targetType;
        while (cur != null)
        {
            FieldInfo field = cur.GetField(fieldName, DeclaredAnyFlags);
            if (field != null) return field;
            cur = cur.BaseType;
        }
        return null;
    }

    /// <summary>
    /// 预检解析目标方法：若存在多重载且未指定参数类型，记录告警避免盲匹配冲突。
    /// </summary>
    private static MethodBase ResolveMethodSafely(Type targetType, string methodName, Type[] parameterTypes, out string error)
    {
        error = string.Empty;
        if (parameterTypes != null)
        {
            MethodInfo exact = FindExactMethodInHierarchy(targetType, methodName, parameterTypes);
            if (exact == null)
            {
                error = $"精确签名不匹配: {targetType.FullName}.{methodName}({FormatTypeList(parameterTypes)})";
            }
            return exact;
        }

        var matched = new List<MethodInfo>(2);
        Type cur = targetType;
        while (cur != null && cur != typeof(object))
        {
            foreach (MethodInfo m in cur.GetMethods(DeclaredAnyFlags))
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
            foreach (MethodInfo m in typeof(object).GetMethods(DeclaredAnyFlags))
            {
                if (m.Name == methodName && !m.IsGenericMethodDefinition)
                {
                    matched.Add(m);
                }
            }
            if (matched.Count == 0)
            {
                error = $"方法不存在: {targetType.FullName}.{methodName}";
                return null;
            }
        }

        if (matched.Count > 1)
        {
            // D4-P2-1：通过确定性选择器稳定选取唯一重载，消除反射枚举序漂移
            var chosen = HarmonyOverloadSelector.SelectDeterministic(matched);
            BazaarLauncherPlugin.Log?.LogDebug(
                $"[SafeHarmonyPatcher] {targetType.FullName}.{methodName} 匹配到 {matched.Count} 个重载，未指定 parameterTypes，确定性选择 ({chosen.GetParameters().Length} 参)。");
            if (chosen != null && chosen.DeclaringType != targetType)
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"[SafeHarmonyPatcher] 基类方法漂移提醒：{targetType.FullName}.{methodName} 解析到基类 {chosen.DeclaringType?.FullName}.{chosen.Name}。");
            }
            return chosen;
        }

        var single = matched[0];
        if (single.DeclaringType != targetType)
        {
            BazaarLauncherPlugin.Log?.LogWarning(
                $"[SafeHarmonyPatcher] 基类方法漂移提醒：{targetType.FullName}.{methodName} 解析到基类 {single.DeclaringType?.FullName}.{single.Name}。");
        }
        return single;
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
                FieldInfo field = FindFieldInHierarchy(declaring, fieldName);
                if (field == null)
                {
                    error = $"{role} 引用的私有字段不存在: {declaring.FullName}.{fieldName}";
                    return false;
                }
            }
        }

        return true;
    }

    private void RecordDegraded(string tag, string reason, bool optional, bool forceWarning = false)
    {
        if (!optional)
        {
            Interlocked.Increment(ref _degraded);
        }

        string safeTag = string.IsNullOrWhiteSpace(tag) ? "<unnamed>" : tag;
        string entry = $"[{(optional ? "可选跳过" : "降级")}] {safeTag} -> {reason}";
        lock (_sync)
        {
            if (_diagnosticNotes.Count < 64)
            {
                _diagnosticNotes.Add(entry);
            }
            if (_failedLabels.Count < 64)
            {
                _failedLabels.Add(safeTag);
            }
        }

        if (optional && !forceWarning)
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
        if (SucceededCount <= 0) return;
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
