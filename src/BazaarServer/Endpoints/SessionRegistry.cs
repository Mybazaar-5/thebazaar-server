using System.Collections.Concurrent;
using BazaarServer.Game;

namespace BazaarServer.Endpoints;

/// <summary>sid → (RunState, RunEngine) 的绑定表。</summary>
public static class SessionRegistry
{
    public sealed class Handle
    {
        public RunState Run { get; init; }
        public RunEngine Engine { get; init; }
    }

    private static readonly ConcurrentDictionary<string, Handle> _map = new(StringComparer.Ordinal);

    public static int ActiveCount => _map.Count;

    public static Handle? TryFindByRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        foreach (var kv in _map)
        {
            if (string.Equals(kv.Value.Run?.RunId, runId, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    public static void Attach(string sid, RunState run, RunEngine engine)
        => _map[sid] = new Handle { Run = run, Engine = engine };

    public static Handle? Get(string sid) => _map.TryGetValue(sid, out var h) ? h : null;

    /// <summary>本机回环单人模式兜底：优先返回唯一活跃会话，若存在多个则返回最新活动的对局句柄。</summary>
    public static Handle? TryGetSingleOrLatest()
    {
        Handle? best = null;
        foreach (var kv in _map)
        {
            var h = kv.Value;
            if (h?.Run == null) continue;
            if (best == null || h.Run.VisualBindingsRevision >= best.Run.VisualBindingsRevision)
                best = h;
        }
        return best;
    }

    public static void Detach(string sid) => _map.TryRemove(sid, out _);

    /// <summary>清理已不在 SessionStore 中的孤儿运行态句柄，配合空闲会话驱逐释放内存。</summary>
    public static int DetachWhereMissing(Func<string, bool> exists)
    {
        int removed = 0;
        foreach (string sid in _map.Keys)
        {
            if (!exists(sid) && _map.TryRemove(sid, out _))
                removed++;
        }
        return removed;
    }
}

