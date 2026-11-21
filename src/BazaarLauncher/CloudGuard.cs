using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 100% 云端隔离防火墙：
/// 1. 禁用 Unity CrashReportHandler 崩溃上报与 AnalyticsManager 外部遥测；
/// 2. 在 HttpClient.SendAsync 与 UnityWebRequest.SendWebRequest 底层校验目标 Host，
///    仅允许访问自有服务器（127.0.0.1 / localhost / 配置中的 ServerBase Host），
///    任何发往官方云/CDN/第三方遥测的出站请求全部拦截并记录统计。
/// </summary>
internal static class CloudGuard
{
    private static bool _applied;
    private static long _allowedCount;
    private static long _blockedExternalCount;
    private static readonly object _lock = new();
    private static readonly List<string> _blockedUrls = new();
    private const int MaxRecordedBlockedUrls = 20;

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

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        DisableCrashReporting();
        DisableAnalytics(harmony);
        PatchHttpClient(harmony);
        PatchUnityWebRequest(harmony);
        DisableSentryIfPresent(harmony);
    }

    private static void DisableCrashReporting()
    {
        try
        {
            var crType = AccessTools.TypeByName("UnityEngine.CrashReportHandler.CrashReportHandler");
            if (crType != null)
            {
                var prop = crType.GetProperty("enableCaptureExceptions",
                    BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(null, false);
                    BazaarLauncherPlugin.Log.LogInfo("CloudGuard: CrashReportHandler.enableCaptureExceptions = false");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("CloudGuard CrashReportHandler: " + ex.Message);
        }
    }

    private static void DisableAnalytics(Harmony harmony)
    {
        try
        {
            var amType = AccessTools.TypeByName("AnalyticsManager");
            if (amType == null) return;

            var init = AccessTools.Method(amType, "Initialize");
            if (init != null)
            {
                harmony.Patch(init, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid))));
                BazaarLauncherPlugin.Log.LogInfo("CloudGuard: AnalyticsManager.Initialize disabled");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("CloudGuard Analytics: " + ex.Message);
        }
    }

    private static void DisableSentryIfPresent(Harmony harmony)
    {
        try
        {
            foreach (var typeName in new[] { "Sentry.SentrySdk", "Sentry.Unity.SentryUnity" })
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;
                var init = AccessTools.Method(t, "Init");
                if (init != null)
                {
                    harmony.Patch(init, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CloudGuard), nameof(SkipVoid))));
                    BazaarLauncherPlugin.Log.LogInfo($"CloudGuard: {typeName}.Init disabled");
                }
            }
        }
        catch { }
    }

    private static void PatchHttpClient(Harmony harmony)
    {
        try
        {
            var sendAsync = AccessTools.Method(typeof(HttpClient), "SendAsync",
                new[] { typeof(HttpRequestMessage), typeof(CancellationToken) });
            if (sendAsync != null)
            {
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(CloudGuard), nameof(HttpClientPrefix)))
                {
                    priority = Priority.First,
                };
                harmony.Patch(sendAsync, prefix: prefix);
                BazaarLauncherPlugin.Log.LogInfo("CloudGuard: HttpClient.SendAsync whitelist active");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("CloudGuard HttpClient patch failed: " + ex.Message);
        }
    }

    private static void PatchUnityWebRequest(Harmony harmony)
    {
        try
        {
            var uwrType = AccessTools.TypeByName("UnityEngine.Networking.UnityWebRequest");
            if (uwrType == null)
            {
                BazaarLauncherPlugin.Log.LogWarning("CloudGuard: UnityWebRequest type not found");
                return;
            }

            var send = AccessTools.Method(uwrType, "SendWebRequest");
            if (send != null)
            {
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(CloudGuard), nameof(UnityWebRequestPrefix)))
                {
                    priority = Priority.First,
                };
                harmony.Patch(send, prefix: prefix);
                BazaarLauncherPlugin.Log.LogInfo("CloudGuard: UnityWebRequest.SendWebRequest whitelist active");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("CloudGuard UnityWebRequest patch failed: " + ex.Message);
        }
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

    private static void UnityWebRequestPrefix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            var urlProp = __instance.GetType().GetProperty("url", BindingFlags.Public | BindingFlags.Instance);
            string url = urlProp?.GetValue(__instance) as string;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (IsAllowedHost(uri))
            {
                Interlocked.Increment(ref _allowedCount);
                return;
            }

            RecordBlocked(url);
            string fallback = BazaarLauncherPlugin.ServerBase.Value.TrimEnd('/') + "/api/telemetry";
            urlProp?.SetValue(__instance, fallback);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CloudGuard UnityWebRequestPrefix: " + ex.Message);
        }
    }

    internal static bool IsAllowedHost(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return true;
        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "file") return true;
        if (scheme != "http" && scheme != "https") return false;

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
        BazaarLauncherPlugin.Log?.LogWarning($"[CloudGuard] BLOCKED external request -> {url}");
    }
}
