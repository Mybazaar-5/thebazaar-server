using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BazaarServer.Tournament;

/// <summary>
/// 分层隔离 SSE 推送中枢（根治全局单版本广播风暴 MP-P1-02）：
/// - 按频道键分桶投递（lobby:{id} / public_lobbies / board:{accountId} / social:global / social:lobby:{id}）；
/// - 限制单账号并发 SSE 上限（默认 4）与全局并发上限（默认 2000）；
/// - 使用有界 Channel（DropOldest）防慢客户端积压导致内存暴涨；
/// - 跟踪各锦标赛房实时观战人数。
/// </summary>
public sealed class SseEventHub
{
    public const int DefaultMaxConnectionsPerAccount = 4;
    public const int DefaultMaxGlobalConnections = 2000;
    private const int ChannelCapacity = 64;

    public sealed record SseFrame(string? EventName, string Data, string? EventId = null);

    public sealed class Subscription : IDisposable
    {
        private readonly SseEventHub _hub;
        private readonly string _topicKey;
        private readonly string _ownerKey;
        private readonly string? _spectateLobbyId;
        private readonly Guid _subId;
        private int _disposed;

        internal Subscription(
            SseEventHub hub,
            string topicKey,
            string ownerKey,
            string? spectateLobbyId,
            Guid subId,
            Channel<SseFrame> channel)
        {
            _hub = hub;
            _topicKey = topicKey;
            _ownerKey = ownerKey;
            _spectateLobbyId = spectateLobbyId;
            _subId = subId;
            Channel = channel;
        }

        public Channel<SseFrame> Channel { get; }
        public ChannelReader<SseFrame> Reader => Channel.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _hub.Unsubscribe(_topicKey, _ownerKey, _spectateLobbyId, _subId, Channel);
        }
    }

    private readonly object _topicSync = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<SseFrame>>> _topics =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _ownerConnCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _spectatorCounts = new(StringComparer.OrdinalIgnoreCase);
    private int _totalConnections;

    public int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>O(1) 线程安全检测指定主题是否存在活跃订阅者。</summary>
    public bool HasTopicSubscribers(string topicKey)
        => !string.IsNullOrWhiteSpace(topicKey)
           && _topics.TryGetValue(topicKey, out var bucket)
           && !bucket.IsEmpty;

    /// <summary>O(1) 线程安全检测目标玩家棋盘观战频道是否存在活跃 SSE 订阅者。</summary>
    public bool HasBoardSubscribers(string targetAccountId)
        => !string.IsNullOrWhiteSpace(targetAccountId)
           && HasTopicSubscribers(BoardTopic(targetAccountId));

    public static string LobbyTopic(string lobbyId) => $"lobby:{lobbyId.Trim().ToLowerInvariant()}";
    public static string PublicLobbiesTopic => "lobby:__public__";
    public static string BoardTopic(string targetAccountId) => $"board:{NormalizeKey(targetAccountId)}";
    public static string SocialGlobalTopic => "social:global";
    public static string SocialLobbyTopic(string lobbyId) => $"social:lobby:{lobbyId.Trim().ToLowerInvariant()}";

    /// <summary>订阅指定频道，超过全局或单账号并发连接上限时返回 null。</summary>
    public Subscription? TrySubscribe(
        string topicKey,
        string? ownerKey = null,
        string? spectateLobbyId = null,
        int maxPerOwner = DefaultMaxConnectionsPerAccount)
    {
        int nextTotal = Interlocked.Increment(ref _totalConnections);
        if (nextTotal > DefaultMaxGlobalConnections)
        {
            Interlocked.Decrement(ref _totalConnections);
            return null;
        }

        string effectiveOwner = string.IsNullOrWhiteSpace(ownerKey) ? "anon" : ownerKey.Trim().ToLowerInvariant();
        int effectiveLimit = effectiveOwner.StartsWith("anon", StringComparison.Ordinal) ? Math.Max(8, maxPerOwner * 2) : Math.Max(1, maxPerOwner);

        var channel = Channel.CreateBounded<SseFrame>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        Guid subId = Guid.NewGuid();
        lock (_topicSync)
        {
            int ownerCount = _ownerConnCounts.AddOrUpdate(effectiveOwner, 1, (_, old) => old + 1);
            if (ownerCount > effectiveLimit)
            {
                int rolledBack = _ownerConnCounts.AddOrUpdate(effectiveOwner, 0, (_, old) => Math.Max(0, old - 1));
                if (rolledBack <= 0)
                    _ownerConnCounts.TryRemove(effectiveOwner, out _);
                Interlocked.Decrement(ref _totalConnections);
                return null;
            }

            var bucket = _topics.GetOrAdd(topicKey, _ => new ConcurrentDictionary<Guid, Channel<SseFrame>>());
            bucket[subId] = channel;

            if (!string.IsNullOrWhiteSpace(spectateLobbyId))
            {
                _spectatorCounts.AddOrUpdate(spectateLobbyId.Trim().ToLowerInvariant(), 1, (_, old) => old + 1);
            }
        }

        return new Subscription(this, topicKey, effectiveOwner, spectateLobbyId, subId, channel);
    }

    private void Unsubscribe(string topicKey, string ownerKey, string? spectateLobbyId, Guid subId, Channel<SseFrame> channel)
    {
        lock (_topicSync)
        {
            if (_topics.TryGetValue(topicKey, out var bucket))
            {
                bucket.TryRemove(subId, out _);
                if (bucket.IsEmpty)
                    _topics.TryRemove(topicKey, out _);
            }

            int nextOwner = _ownerConnCounts.AddOrUpdate(ownerKey, 0, (_, old) => Math.Max(0, old - 1));
            if (nextOwner <= 0)
                _ownerConnCounts.TryRemove(ownerKey, out _);

            if (!string.IsNullOrWhiteSpace(spectateLobbyId))
            {
                string lk = spectateLobbyId.Trim().ToLowerInvariant();
                int nextSpec = _spectatorCounts.AddOrUpdate(lk, 0, (_, old) => Math.Max(0, old - 1));
                if (nextSpec <= 0)
                    _spectatorCounts.TryRemove(lk, out _);
            }
        }

        channel.Writer.TryComplete();
        Interlocked.Decrement(ref _totalConnections);
    }

    public int GetSpectatorCount(string? lobbyId)
    {
        if (string.IsNullOrWhiteSpace(lobbyId)) return 0;
        return _spectatorCounts.TryGetValue(lobbyId.Trim().ToLowerInvariant(), out int count) ? Math.Max(0, count) : 0;
    }

    public void Publish(string topicKey, SseFrame frame)
    {
        if (!_topics.TryGetValue(topicKey, out var bucket) || bucket.IsEmpty)
            return;

        foreach (var ch in bucket.Values)
        {
            ch.Writer.TryWrite(frame);
        }
    }

    public void PublishLobbyUpdate(string lobbyId, bool alsoPublicList = false)
    {
        if (!string.IsNullOrWhiteSpace(lobbyId))
            Publish(LobbyTopic(lobbyId), new SseFrame(null, "update"));
        if (alsoPublicList)
            Publish(PublicLobbiesTopic, new SseFrame(null, "update"));
    }

    public void PublishBoardUpdate(string targetAccountId, string jsonFrame)
    {
        if (string.IsNullOrWhiteSpace(targetAccountId)) return;
        Publish(BoardTopic(targetAccountId), new SseFrame(null, jsonFrame));
    }

    public void PublishSocialEvent(string? lobbyId, string eventName, string jsonData, long? eventId = null)
    {
        string? idText = eventId.HasValue ? eventId.Value.ToString() : null;
        var frame = new SseFrame(eventName, jsonData, idText);
        if (string.IsNullOrWhiteSpace(lobbyId) || string.Equals(lobbyId, "global", StringComparison.OrdinalIgnoreCase))
        {
            Publish(SocialGlobalTopic, frame);
        }
        else
        {
            Publish(SocialLobbyTopic(lobbyId), frame);
        }
    }

    /// <summary>向 HTTP 响应写出标准 SSE 响应头（必带 X-Accel-Buffering: no 防止反向代理缓冲）。</summary>
    public static void PrepareSseResponseHeaders(HttpResponse response)
    {
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache, no-store";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>安全写出一条 SSE 帧，捕获客户端断连产生的 IOException/ObjectDisposedException/OperationCanceledException。</summary>
    public static async Task<bool> TryWriteRawAsync(HttpResponse response, string text, CancellationToken ct)
    {
        try
        {
            await response.WriteAsync(text, ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public static async Task<bool> TryWriteFrameAsync(HttpResponse response, SseFrame frame, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder(frame.Data.Length + 48);
        if (!string.IsNullOrEmpty(frame.EventId))
            sb.Append("id: ").Append(frame.EventId).Append('\n');
        if (!string.IsNullOrEmpty(frame.EventName))
            sb.Append("event: ").Append(frame.EventName).Append('\n');
        sb.Append("data: ").Append(frame.Data).Append("\n\n");
        return await TryWriteRawAsync(response, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static string NormalizeKey(string accountId)
        => Guid.TryParse(accountId, out var g) ? g.ToString("N") : (accountId ?? "").Trim().Replace("-", "").ToLowerInvariant();
}
