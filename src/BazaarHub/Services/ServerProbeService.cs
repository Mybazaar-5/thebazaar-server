using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 服务器健康与延迟探测服务。
/// 优先请求 /api/ping?ts=... 获取精确延迟、在线人数与版本，同时联动 /healthz 确保兼容旧版服务端。
/// </summary>
public sealed class ServerProbeService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ServerProbeService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(4)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BazaarHub/{LauncherConstants.HubVersion}");
            _ownsClient = true;
        }
    }

    /// <summary>
    /// 探测目标服务器的 /api/ping 与 /healthz 状态。
    /// </summary>
    public async Task<ServerProbeResult> ProbeAsync(string serverBase, CancellationToken cancellationToken = default)
    {
        string normalized = LauncherConfigService.NormalizeServerUrl(serverBase);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ServerProbeResult
            {
                ServerUrl = string.Empty,
                IsOnline = false,
                StatusMessage = "未配置服务器地址"
            };
        }

        bool pingSupported = false;
        long latencyMs = -1;
        int? onlinePlayers = null;
        int? activeRuns = null;
        int? activeLobbies = null;
        long? uptimeSec = null;
        string? serverVersion = null;
        bool pingOk = false;

        // 1. 尝试 /api/ping?ts=...
        long clientTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync($"{normalized}/api/ping?ts={clientTs}", cancellationToken);
            sw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                pingSupported = true;
                latencyMs = Math.Max(1, sw.ElapsedMilliseconds);
                string body = await resp.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                pingOk = !root.TryGetProperty("ok", out JsonElement okProp) || okProp.ValueKind != JsonValueKind.False;
                onlinePlayers = TryGetInt(root, "onlinePlayers");
                activeRuns = TryGetInt(root, "activeRuns");
                activeLobbies = TryGetInt(root, "activeLobbies");
                uptimeSec = TryGetLong(root, "uptimeSec") ?? TryGetLong(root, "uptime");
                serverVersion = TryGetString(root, "serverVersion") ?? TryGetString(root, "version");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // /api/ping 不可用时回退 /healthz
        }

        // 2. 探测 /healthz（确认服务健康）
        bool healthzOk = false;
        string? lastError = null;
        var healthSw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync($"{normalized}/healthz", cancellationToken);
            healthSw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                if (latencyMs < 0)
                {
                    latencyMs = Math.Max(1, healthSw.ElapsedMilliseconds);
                }

                string body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using JsonDocument doc = JsonDocument.Parse(body);
                    JsonElement root = doc.RootElement;
                    healthzOk = !root.TryGetProperty("ok", out JsonElement okProp) || okProp.ValueKind != JsonValueKind.False;
                    uptimeSec ??= TryGetLong(root, "uptime") ?? TryGetLong(root, "uptimeSec");
                }
                else
                {
                    healthzOk = true;
                }
            }
            else
            {
                lastError = $"HTTP {(int)resp.StatusCode}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }

        bool isOnline = pingOk || healthzOk;
        string msg;
        if (isOnline)
        {
            string extra = onlinePlayers.HasValue ? $" · 在线 {onlinePlayers.Value} 人" : string.Empty;
            string ver = !string.IsNullOrWhiteSpace(serverVersion) ? $" · v{serverVersion}" : string.Empty;
            msg = $"在线 ({latencyMs} ms{extra}{ver})";
        }
        else
        {
            msg = string.IsNullOrWhiteSpace(lastError) ? "无法连接服务器" : $"离线 ({Truncate(lastError, 48)})";
        }

        return new ServerProbeResult
        {
            ServerUrl = normalized,
            IsOnline = isOnline,
            HealthzOk = healthzOk,
            PingEndpointSupported = pingSupported,
            LatencyMs = latencyMs,
            OnlinePlayers = onlinePlayers,
            ActiveRuns = activeRuns,
            ActiveLobbies = activeLobbies,
            UptimeSeconds = uptimeSec,
            ServerVersion = serverVersion,
            StatusMessage = msg,
            CheckedAt = DateTimeOffset.Now
        };
    }

    private static int? TryGetInt(JsonElement root, string name)
    {
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetInt32(out int v))
            {
                return v;
            }
        }
        return null;
    }

    private static long? TryGetLong(JsonElement root, string name)
    {
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetInt64(out long v))
            {
                return v;
            }
        }
        return null;
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
