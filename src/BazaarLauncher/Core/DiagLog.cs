using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace BazaarLauncher.Core;

/// <summary>
/// D4-P2-6：零 Unity 依赖的日志节流纯 .NET 内核（按 tag 与时间窗口限制重复异常日志刷屏，同时精确累计吞吐次数）。
/// 可由纯 .NET 8 单元测试通过反射精准加载直测。
/// </summary>
public sealed class DiagLogThrottleCore
{
    private readonly long _windowMs;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _lastEmitByTag = new(StringComparer.Ordinal);
    private long _totalSwallowedCount;

    public DiagLogThrottleCore(long throttleWindowMs = 60_000L)
    {
        _windowMs = throttleWindowMs > 0 ? throttleWindowMs : 60_000L;
    }

    public long TotalSwallowedCount => Interlocked.Read(ref _totalSwallowedCount);

    public bool ShouldEmit(string tag, long nowMs)
    {
        Interlocked.Increment(ref _totalSwallowedCount);
        string key = string.IsNullOrEmpty(tag) ? "<default>" : tag;
        lock (_sync)
        {
            if (!_lastEmitByTag.TryGetValue(key, out long lastMs) || nowMs - lastMs >= _windowMs)
            {
                if (_lastEmitByTag.Count >= 1024 && !_lastEmitByTag.ContainsKey(key))
                    _lastEmitByTag.Clear();
                _lastEmitByTag[key] = nowMs;
                return true;
            }
            return false;
        }
    }
}

/// <summary>
/// D4-P2-6：客户端关键路径节流告警器，替代空 catch 静默吞异常（同一 tag 60s 仅输出一次 Warning）。
/// </summary>
internal static class DiagLog
{
    private static readonly DiagLogThrottleCore Throttle = new(60_000L);

    internal static long TotalSwallowedCount => Throttle.TotalSwallowedCount;

    internal static void SwallowOnce(string tag, Exception ex)
    {
        if (ex == null) return;
        long freq = Stopwatch.Frequency;
        long nowMs = freq > 0 ? (Stopwatch.GetTimestamp() * 1000L / freq) : Environment.TickCount;
        if (Throttle.ShouldEmit(tag, nowMs))
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[Diag:{tag}] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
