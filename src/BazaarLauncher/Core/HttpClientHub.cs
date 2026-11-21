using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BazaarLauncher.Core;

/// <summary>
/// 统一客户端 HTTP 连接池与退出级取消令牌中心（修复 P2-5 / P0-4 连接泄漏与退出悬挂请求）。
/// </summary>
internal static class HttpClientHub
{
    private static readonly object _sync = new();
    private static CancellationTokenSource _shutdownCts = new();

    private static readonly HttpClient _sharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly HttpClient _downloadClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly HttpClient _sseClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    internal static HttpClient SharedClient => _sharedClient;
    internal static HttpClient DownloadClient => _downloadClient;
    internal static HttpClient SseClient => _sseClient;

    internal static CancellationToken ShutdownToken
    {
        get
        {
            lock (_sync)
            {
                return _shutdownCts.Token;
            }
        }
    }

    /// <summary>重新初始化退出令牌（用于热重载重启周期）。</summary>
    internal static void ResetForStartup()
    {
        lock (_sync)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                try { _shutdownCts.Dispose(); } catch { }
                _shutdownCts = new CancellationTokenSource();
            }
        }
    }

    /// <summary>插件退出时统一取消所有悬挂中的后台 HTTP/SSE 请求。</summary>
    internal static void Shutdown()
    {
        lock (_sync)
        {
            try
            {
                if (!_shutdownCts.IsCancellationRequested)
                {
                    _shutdownCts.Cancel();
                }
            }
            catch { }
        }
    }

    /// <summary>将调用方令牌与全局退出令牌链接。</summary>
    internal static CancellationTokenSource CreateLinkedCts(CancellationToken callerToken = default)
    {
        CancellationToken shutdown = ShutdownToken;
        return callerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(shutdown, callerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }

    /// <summary>探测服务器 /api/ping 实时往返延迟（毫秒），失败返回 -1。</summary>
    internal static async Task<long> ProbePingMsAsync(CancellationToken ct = default)
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return -1;

        using var linked = CreateLinkedCts(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(4));

        long clientTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/ping?ts={clientTs}");
            using var resp = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            sw.Stop();
            return resp.IsSuccessStatusCode ? Math.Max(1L, sw.ElapsedMilliseconds) : -1;
        }
        catch
        {
            return -1;
        }
    }
}
