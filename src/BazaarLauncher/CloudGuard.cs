using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// D4-P2-4：零 Unity 依赖的 URL 白名单/外网遥测过滤纯 .NET 内核，供纯 .NET 8 单元测试通过反射直测。
/// 放行本地回环（127.0.0.1/localhost/::1）、file:// 与当前自建服同主机 URL，拦截其余所有外网请求。
/// </summary>
public static class CloudGuardUrlFilterCore
{
    public static bool ShouldBlockUrl(string url, string serverBase)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return !IsAllowedUri(uri, serverBase);
    }

    public static bool IsAllowedUri(Uri uri, string serverBase)
    {
        if (uri == null || !uri.IsAbsoluteUri) return true;
        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "file") return true;
        if (scheme != "http" && scheme != "https" && scheme != "ws" && scheme != "wss") return false;

        string host = uri.Host;
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(serverBase) &&
            Uri.TryCreate(serverBase.Trim(), UriKind.Absolute, out var serverUri))
        {
            if (string.Equals(host, serverUri.Host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// 100% 云端隔离防火墙（修复 P0-5）：
/// 1. 禁用 Unity CrashReportHandler 崩溃上报、Sentry 与 AnalyticsManager 外部遥测；
/// 2. 同时拦截 HttpClient.SendAsync 的 2 参和底层核心 3 参重载 (HttpRequestMessage, HttpCompletionOption, CancellationToken)；
/// 3. 拦截 ClientWebSocket.ConnectAsync 防止外部 WS 外联泄漏；
/// 4. 缓存 UnityWebRequest.url 反射属性，并将拦截到的外部遥测直接短路（不向自建服转发无效垃圾流量）。
/// </summary>
internal static class CloudGuard
{
    private static bool _applied;
    private static long _allowedCount;
    private static long _blockedExternalCount;
    private static readonly object _lock = new();
    private static readonly List<string> _blockedUrls = new();
    private const int MaxRecordedBlockedUrls = 30;

    private static PropertyInfo _cachedUwrUrlProp;
    private static MethodInfo _cachedUwrAbortMethod;

    internal static long AllowedCount => Interlocked.Read(ref _allowedCount);
    internal static long BlockedExternalCount => Interlocked.Read(ref _blockedExternalCount);

    internal static List<string> GetBlockedUrlsSnapshot()
    {
        lock (_lock)
        {
            return new List<string>(_blockedUrls);
        }
    }

    internal static object GetStats() => new
    {
        allowed = AllowedCount,
        blockedExternal = BlockedExternalCount,
        blockedUrls = GetBlockedUrlsSnapshot(),
    };

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        DisableCrashReporting();
        DisableAnalytics(patcher);
        PatchHttpClient(patcher);
        PatchClientWebSocket(patcher);
        PatchUnityWebRequest(patcher);
        DisableSentryIfPresent(patcher);
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        var patcher = new SafeHarmonyPatcher("cloudguard.legacy");
        Apply(patcher);
    }

    private static void DisableCrashReporting()
    {
        try
        {
            var crType = GameInspector.FindTypeSafe("UnityEngine.CrashReportHandler.CrashReportHandler");
            var prop = crType?.GetProperty("enableCaptureExceptions", BindingFlags.Public | BindingFlags.Static);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(null, false);
                BazaarLauncherPlugin.Log?.LogInfo("CloudGuard: 已关闭 CrashReportHandler 崩溃上报");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CloudGuard CrashReportHandler: " + ex.Message);
        }
    }

    private static void DisableAnalytics(SafeHarmonyPatcher patcher)
    {
        var analyticsType = GameInspector.FindTypeSafe("AnalyticsManager", "TheBazaar.AnalyticsManager");
        if (analyticsType == null) return;
        patcher.TryPatch(
            analyticsType,
            "Initialize",
            prefix: AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid)),
            optional: true);
    }

    private static void DisableSentryIfPresent(SafeHarmonyPatcher patcher)
    {
        // 使用静默查找避免 Release 裁剪掉 Sentry 程序集时触发 HarmonyX 黄色反射告警
        foreach (var typeName in new[] { "Sentry.SentrySdk", "Sentry.Unity.SentryUnity" })
        {
            var sentryType = GameInspector.FindTypeSafe(typeName);
            if (sentryType == null) continue;
            patcher.TryPatch(
                sentryType,
                "Init",
                prefix: AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid)),
                optional: true);
        }
    }

    private static void PatchHttpClient(SafeHarmonyPatcher patcher)
    {
        var prefix = AccessTools.Method(typeof(CloudGuard), nameof(HttpClientPrefix));

        // D4-P2-4：仅挂 3 参底层核心重载（1 参/2 参 SendAsync、GetAsync/PostAsync 最终均转发至此重载），
        // 彻底杜绝 2 参 + 3 参双钩子造成的双计数，同时完整保留 D3-P1-13 X-Visual-Bindings-Rev 响应头 Postfix
        patcher.TryPatch(
            typeof(HttpClient),
            "SendAsync",
            new[] { typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken) },
            prefix: prefix,
            postfix: AccessTools.Method(typeof(CloudGuard), nameof(HttpClientSend3Postfix)),
            prefixPriority: Priority.First,
            label: "HttpClient.SendAsync(3)");
    }

    private static void PatchClientWebSocket(SafeHarmonyPatcher patcher)
    {
        patcher.TryPatch(
            typeof(ClientWebSocket),
            "ConnectAsync",
            new[] { typeof(Uri), typeof(CancellationToken) },
            prefix: AccessTools.Method(typeof(CloudGuard), nameof(WebSocketConnectPrefix)),
            prefixPriority: Priority.First,
            optional: true,
            label: "ClientWebSocket.ConnectAsync");
    }

    private static void PatchUnityWebRequest(SafeHarmonyPatcher patcher)
    {
        var uwrType = GameInspector.FindTypeSafe("UnityEngine.Networking.UnityWebRequest");
        if (uwrType == null) return;

        _cachedUwrUrlProp = uwrType.GetProperty("url", BindingFlags.Public | BindingFlags.Instance);
        _cachedUwrAbortMethod = uwrType.GetMethod("Abort", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

        patcher.TryPatch(
            uwrType,
            "SendWebRequest",
            Type.EmptyTypes,
            prefix: AccessTools.Method(typeof(CloudGuard), nameof(UnityWebRequestPrefix)),
            postfix: AccessTools.Method(typeof(CloudGuard), nameof(UnityWebRequestPostfix)),
            prefixPriority: Priority.First,
            label: "UnityWebRequest.SendWebRequest");
    }

    private static bool SkipVoid() => false;

    private static bool HttpClientPrefix(HttpRequestMessage __0, ref Task<HttpResponseMessage> __result)
    {
        try
        {
            var uri = __0?.RequestUri;
            if (uri == null) return true;

            if (IsAllowedHost(uri))
            {
                Interlocked.Increment(ref _allowedCount);
                return true;
            }

            RecordBlocked(uri.ToString());
            var dummy = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                RequestMessage = __0,
            };
            __result = Task.FromResult(dummy);
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CloudGuard HttpClientPrefix: " + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// D3-P1-13：常驻监听所有自建服响应头中的 X-Visual-Bindings-Rev（含 TempoNet /sessions 与 /commands）。
    /// 严格仅非阻塞只读 resp.Headers，绝不触碰 resp.Content、绝不修改 __result 任务流。
    /// </summary>
    private static void HttpClientSend3Postfix(HttpRequestMessage __0, Task<HttpResponseMessage> __result)
    {
        try
        {
            if (__result == null || __0?.RequestUri == null) return;
            string path = __0.RequestUri.AbsolutePath ?? string.Empty;
            if (path.IndexOf("/api/mods/active-bindings", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }
            if (!IsAllowedHost(__0.RequestUri))
            {
                return;
            }

            _ = __result.ContinueWith(static t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                try
                {
                    var resp = t.Result;
                    if (resp?.Headers == null) return;
                    if (resp.Headers.TryGetValues("X-Visual-Bindings-Rev", out var values))
                    {
                        foreach (string v in values)
                        {
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                ModContentSyncClient.Instance.NotifyVisualBindingsRevObserved(v.Trim());
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { Core.DiagLog.SwallowOnce("CloudGuard.VisualRevHeader", ex); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) { Core.DiagLog.SwallowOnce("CloudGuard.Send3Postfix", ex); }
    }

    private static bool WebSocketConnectPrefix(Uri __0, ref Task __result)
    {
        try
        {
            if (__0 == null || IsAllowedHost(__0))
            {
                Interlocked.Increment(ref _allowedCount);
                return true;
            }

            RecordBlocked(__0.ToString());
            __result = Task.FromException(new WebSocketException("已由 CloudGuard 拦截外部 WebSocket 连接"));
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool UnityWebRequestPrefix(object __instance, out bool __state)
    {
        __state = false;
        try
        {
            if (__instance == null || _cachedUwrUrlProp == null) return true;
            string url = _cachedUwrUrlProp.GetValue(__instance) as string;
            if (string.IsNullOrWhiteSpace(url)) return true;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
            if (IsAllowedHost(uri))
            {
                Interlocked.Increment(ref _allowedCount);
                return true;
            }

            RecordBlocked(url);
            // D4-P2-4：Prefix 仅改写 URL 至本地无效端口并标记 __state=true，由 Postfix 紧随发送启动后 Abort()，避免发送前 Abort 引发原生异常
            _cachedUwrUrlProp.SetValue(__instance, "http://127.0.0.1:1/blocked-telemetry");
            __state = true;
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CloudGuard UnityWebRequestPrefix: " + ex.Message);
            return true;
        }
    }

    private static void UnityWebRequestPostfix(object __instance, bool __state)
    {
        if (!__state || __instance == null || _cachedUwrAbortMethod == null) return;
        try { _cachedUwrAbortMethod.Invoke(__instance, null); }
        catch (Exception ex) { DiagLog.SwallowOnce("CloudGuard.UwrAbort", ex); }
    }

    internal static bool IsAllowedHost(Uri uri)
        => CloudGuardUrlFilterCore.IsAllowedUri(uri, BazaarLauncherPlugin.ServerBase?.Value);

    private static void RecordBlocked(string url)
    {
        Interlocked.Increment(ref _blockedExternalCount);
        lock (_lock)
        {
            if (_blockedUrls.Count < MaxRecordedBlockedUrls && !_blockedUrls.Contains(url))
            {
                _blockedUrls.Add(url);
            }
        }
        BazaarLauncherPlugin.Log?.LogDebug($"[CloudGuard] 已拦截外部出站请求 -> {url}");
    }
}
