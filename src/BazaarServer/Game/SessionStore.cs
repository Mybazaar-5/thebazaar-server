using System.Collections;
using System.Collections.Concurrent;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;

namespace BazaarServer.Game;

/// <summary>
/// 固定容量环形命令日志缓冲区，避免长时间对局下 List 无限扩容或大范围搬移开销。
/// 保留 Add / Count / RemoveRange / Clear 等接口以完全兼容现有调用方。
/// </summary>
public sealed class BoundedCommandLog : IReadOnlyList<string>, ICollection<string>
{
    private readonly string[] _ring;
    private readonly object _sync = new();
    private int _head;
    private int _count;

    public int Capacity => _ring.Length;

    public BoundedCommandLog(int capacity = Session.MaxCommandLogEntries)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new string[capacity];
    }

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public bool IsReadOnly => false;

    public string this[int index]
    {
        get
        {
            lock (_sync)
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _ring[(_head + index) % _ring.Length];
            }
        }
    }

    public void Add(string entry)
    {
        if (entry == null) return;
        lock (_sync)
        {
            if (_count == _ring.Length)
            {
                // 环满时覆盖最旧槽位并推进头指针
                _ring[_head] = entry;
                _head = (_head + 1) % _ring.Length;
            }
            else
            {
                int tail = (_head + _count) % _ring.Length;
                _ring[tail] = entry;
                _count++;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_ring, 0, _ring.Length);
            _head = 0;
            _count = 0;
        }
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        lock (_sync)
        {
            if (index < 0 || index >= _count)
                return;

            int removeCount = Math.Min(count, _count - index);
            if (index == 0)
            {
                for (int i = 0; i < removeCount; i++)
                    _ring[(_head + i) % _ring.Length] = null!;
                _head = (_head + removeCount) % _ring.Length;
                _count -= removeCount;
                return;
            }

            // 非头部裁剪：保留前后元素并紧缩环内容
            int kept = _count - removeCount;
            var temp = new string[kept];
            int write = 0;
            for (int i = 0; i < _count; i++)
            {
                if (i >= index && i < index + removeCount)
                    continue;
                temp[write++] = _ring[(_head + i) % _ring.Length];
            }
            Array.Clear(_ring, 0, _ring.Length);
            Array.Copy(temp, _ring, kept);
            _head = 0;
            _count = kept;
        }
    }

    public bool Contains(string item)
    {
        lock (_sync)
        {
            for (int i = 0; i < _count; i++)
            {
                if (string.Equals(_ring[(_head + i) % _ring.Length], item, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    public void CopyTo(string[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        lock (_sync)
        {
            for (int i = 0; i < _count; i++)
                array[arrayIndex + i] = _ring[(_head + i) % _ring.Length];
        }
    }

    public bool Remove(string item)
    {
        lock (_sync)
        {
            for (int i = 0; i < _count; i++)
            {
                if (string.Equals(_ring[(_head + i) % _ring.Length], item, StringComparison.Ordinal))
                {
                    RemoveRange(i, 1);
                    return true;
                }
            }
            return false;
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        string[] snapshot;
        lock (_sync)
        {
            snapshot = new string[_count];
            for (int i = 0; i < _count; i++)
                snapshot[i] = _ring[(_head + i) % _ring.Length];
        }
        return ((IEnumerable<string>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 一次对局会话（对应客户端的 HttpGameClient._sessionId）。
/// 客户端每次操作发一条 INetCommand 到 /commands，服务端回一条 INetMessage。
/// </summary>
public sealed class Session
{
    public const int MaxCommandLogEntries = 64;

    public string Sid { get; init; }
    public string AccountId { get; init; }
    public string Username { get; init; }
    public string RunId { get; init; }
    public EPlayMode PlayMode { get; init; }
    public EHero Hero { get; init; }
    public Guid? TournamentSessionId { get; init; }
    public Guid GameModeId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastCommandAt { get; set; } = DateTime.UtcNow;

    /// <summary>rid 单调递增，响应头回带，客户端用它对齐。</summary>
    public long RequestId;

    /// <summary>串行化同一会话的命令处理，避免事件顺序错乱。</summary>
    public readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>有界命令日志（上限 64 条环形缓冲），用于回放与差分测试。</summary>
    public BoundedCommandLog CommandLog { get; } = new(MaxCommandLogEntries);

    /// <summary>追加一条命令日志摘要（自动受限于 64 条环形容量）。</summary>
    public void AddCommandLog(string entry)
    {
        if (!string.IsNullOrEmpty(entry))
            CommandLog.Add(entry);
    }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>accountId → 当前活跃 sid。同一账号只保留一个会话，避免重复开局导致状态分裂。</summary>
    private readonly ConcurrentDictionary<string, string> _activeByAccount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>会话被驱逐或替换时触发（用于同步清理 RidCaches、SessionRegistry、观战录制器，杜绝内存泄漏）。</summary>
    public event Action<Session>? SessionEvicted;

    public Session Create(string accountId, string username, InitializeRunCommand init)
    {
        // 同一账号重复开局：摘除旧会话（严禁直接 Dispose(Gate)，防止正在执行 /commands 的线程触发 P1-1 ObjectDisposedException！）
        if (!string.IsNullOrWhiteSpace(accountId) && _activeByAccount.TryGetValue(accountId, out var oldSid))
        {
            if (_sessions.TryRemove(oldSid, out var old))
            {
                NotifySessionEvictedSafe(old);
            }
            _activeByAccount.TryRemove(new KeyValuePair<string, string>(accountId, oldSid));
        }

        var session = new Session
        {
            Sid = "sid_" + Guid.NewGuid().ToString("N"),
            AccountId = accountId,
            Username = username,
            RunId = "run_" + Guid.NewGuid().ToString("N")[..12],
            PlayMode = init.PlayMode,
            Hero = init.SelectedHero,
            TournamentSessionId = init.TournamentSessionId,
            GameModeId = init.GameModeId ?? Guid.Empty,
        };
        _sessions[session.Sid] = session;
        if (!string.IsNullOrWhiteSpace(accountId))
            _activeByAccount[accountId] = session.Sid;
        return session;
    }

    public Session Get(string sid)
        => !string.IsNullOrWhiteSpace(sid) && _sessions.TryGetValue(sid, out var s) ? s : null;

    /// <summary>按账号取活跃会话（客户端重连时用）。</summary>
    public Session GetByAccount(string accountId)
        => !string.IsNullOrWhiteSpace(accountId)
           && _activeByAccount.TryGetValue(accountId, out var sid)
           && _sessions.TryGetValue(sid, out var s) ? s : null;

    public void Remove(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return;
        if (_sessions.TryRemove(sid, out var s))
        {
            if (!string.IsNullOrWhiteSpace(s.AccountId))
                _activeByAccount.TryRemove(new KeyValuePair<string, string>(s.AccountId, s.Sid));
            // 不再直接调用 s.Gate.Dispose()：未持有 WaitHandle 的 SemaphoreSlim 由 GC 自动回收，100% 避免并发等待者崩 ObjectDisposedException
            NotifySessionEvictedSafe(s);
        }
    }

    /// <summary>驱逐超过 maxIdle 未活动的空闲会话并触发外围缓存清理，防止 100-CCU 长跑内存泄漏。</summary>
    public int EvictIdleSessions(TimeSpan maxIdle)
    {
        DateTime now = DateTime.UtcNow;
        int evicted = 0;

        foreach (var kv in _sessions.ToArray())
        {
            var session = kv.Value;
            if (now - session.LastCommandAt <= maxIdle)
                continue;

            if (_sessions.TryRemove(new KeyValuePair<string, Session>(kv.Key, session)))
            {
                if (!string.IsNullOrWhiteSpace(session.AccountId))
                    _activeByAccount.TryRemove(new KeyValuePair<string, string>(session.AccountId, session.Sid));
                NotifySessionEvictedSafe(session);
                evicted++;
            }
        }

        return evicted;
    }

    private void NotifySessionEvictedSafe(Session session)
    {
        try { SessionEvicted?.Invoke(session); }
        catch { }
    }

    public int Count => _sessions.Count;
    public IEnumerable<Session> All => _sessions.Values;
}
