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
    private static PropertyInfo _cachedUwrDisposeMethod;

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
            var crType = AccessTools.TypeByName("UnityEngine.CrashReportHandler.CrashReportHandler");
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
        patcher.TryPatch(
            "AnalyticsManager",
            "Initialize",
            prefix: AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid)),
            optional: true);
    }

    private static void DisableSentryIfPresent(SafeHarmonyPatcher patcher)
    {
        foreach (var typeName in new[] { "Sentry.SentrySdk", "Sentry.Unity.SentryUnity" })
        {
            patcher.TryPatch(
                typeName,
                "Init",
                prefix: AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid)),
                optional: true);
        }
    }

    private static void PatchHttpClient(SafeHarmonyPatcher patcher)
    {
        var prefix = AccessTools.Method(typeof(CloudGuard), nameof(HttpClientPrefix));

        // 2 参重载
        patcher.TryPatch(
            typeof(HttpClient),
            "SendAsync",
            new[] { typeof(HttpRequestMessage), typeof(CancellationToken) },
            prefix: prefix,
            prefixPriority: Priority.First,
            label: "HttpClient.SendAsync(2)");

        // 3 参底层核心重载（GetAsync/PostAsync 最终均走此重载，彻底杜绝 P0-5 绕过缺口）
        patcher.TryPatch(
            typeof(HttpClient),
            "SendAsync",
            new[] { typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken) },
            prefix: prefix,
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
        var uwrType = AccessTools.TypeByName("UnityEngine.Networking.UnityWebRequest");
        if (uwrType == null) return;

        _cachedUwrUrlProp = uwrType.GetProperty("url", BindingFlags.Public | BindingFlags.Instance);
        _cachedUwrAbortMethod = uwrType.GetMethod("Abort", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        _cachedUwrDisposeMethod = uwrType.GetProperty("isDone", BindingFlags.Public | BindingFlags.Instance);

        patcher.TryPatch(
            uwrType,
            "SendWebRequest",
            Type.EmptyTypes,
            prefix: AccessTools.Method(typeof(CloudGuard), nameof(UnityWebRequestPrefix)),
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

    private static bool UnityWebRequestPrefix(object __instance, ref object __result)
    {
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
            // 直接短路外部遥测，不向自建服转发垃圾请求，避免污染自建服日志与连接池
            _cachedUwrUrlProp.SetValue(__instance, "http://127.0.0.1:1/blocked-telemetry");
            try { _cachedUwrAbortMethod?.Invoke(__instance, null); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CloudGuard UnityWebRequestPrefix: " + ex.Message);
            return true;
        }
    }

    internal static bool IsAllowedHost(Uri uri)
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

        string serverBase = BazaarLauncherPlugin.ServerBase?.Value;
        if (!string.IsNullOrWhiteSpace(serverBase) &&
            Uri.TryCreate(serverBase, UriKind.Absolute, out var allowedUri))
        {
            return string.Equals(host, allowedUri.Host, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

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
