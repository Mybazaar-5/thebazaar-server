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

    public static void Attach(string sid, RunState run, RunEngine engine)
        => _map[sid] = new Handle { Run = run, Engine = engine };

    public static Handle Get(string sid) => _map.TryGetValue(sid, out var h) ? h : null;

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

