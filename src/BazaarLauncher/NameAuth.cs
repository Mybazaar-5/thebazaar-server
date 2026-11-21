using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher;

/// <summary>
/// 用户名即登录与自建服统一鉴权 HTTP 客户端（修复 P0-4 &amp; P2-5）：
///   1. 严格通过泛型闭合方法构造精确的 Task&lt;AuthResult&gt; 返回值，彻底消除 Task&lt;object&gt; 错配与 InvalidCastException；
///   2. 改用 Newtonsoft.Json 序列化登录请求体，彻底解决用户名反斜杠/特殊字符转义缺陷；
///   3. 统一复用 HttpClientHub.SharedClient，杜绝重复登录 Socket 泄漏；
///   4. 所有面向玩家的失败提示均采用简体中文，登录成功后自动触发 Mod/PNG/汉化词典增量同步。
/// </summary>
internal static class NameAuth
{
    private static bool _applied;
    private static volatile string _currentUsername;
    private static volatile string _currentAccountId;
    private static volatile string _currentAccessToken;
    private static int _accountIdFetchInFlight;

    private static Type _cachedAuthResultType;
    private static Type _cachedLoginResponseType;
    private static MethodInfo _cachedTypedDispatchMethod;

    internal sealed class HttpJsonResponse
    {
        public bool Ok { get; init; }
        public int StatusCode { get; init; }
        public string Body { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }

    internal static string CurrentUsername
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_currentUsername))
            {
                return _currentUsername;
            }
            string fromCache = TryReadProfileMember("Username");
            if (!string.IsNullOrWhiteSpace(fromCache))
            {
                _currentUsername = fromCache;
                return fromCache;
            }
            return PlayerIdentity.ResolveName();
        }
    }

    internal static string CurrentAccountId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_currentAccountId))
            {
                return _currentAccountId;
            }
            string fromCache = NormalizeAccountId(TryReadProfileMember("AccountId"));
            if (!string.IsNullOrWhiteSpace(fromCache) && fromCache != "00000000000000000000000000000000")
            {
                _currentAccountId = fromCache;
                return fromCache;
            }
            return string.Empty;
        }
    }

    internal static string CurrentAccessToken
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_currentAccessToken))
            {
                return _currentAccessToken;
            }
            string fromGame = TryReadAccessTokenFromGame();
            if (!string.IsNullOrWhiteSpace(fromGame))
            {
                _currentAccessToken = fromGame;
                return fromGame;
            }
            return string.Empty;
        }
    }

    internal static string NormalizeAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        return accountId.Replace("-", "").Trim().ToLowerInvariant();
    }

    internal static string GetServerBaseUrl()
        => (BazaarLauncherPlugin.ServerBase?.Value ?? string.Empty).TrimEnd('/');

    internal static void ApplyAuthHeaders(HttpRequestMessage req)
    {
        if (req == null) return;

        string aid = CurrentAccountId;
        if (!string.IsNullOrWhiteSpace(aid))
        {
            req.Headers.TryAddWithoutValidation("X-Account-Id", aid);
        }

        string user = CurrentUsername;
        if (!string.IsNullOrWhiteSpace(user))
        {
            bool added = false;
            try { added = req.Headers.TryAddWithoutValidation("X-Username", user); } catch { }
            if (!added)
            {
                try { req.Headers.TryAddWithoutValidation("X-Username", Uri.EscapeDataString(user)); } catch { }
            }
        }

        string token = CurrentAccessToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    internal static async Task EnsureAccountIdentityAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(CurrentAccountId)) return;
        if (Interlocked.CompareExchange(ref _accountIdFetchInFlight, 1, 0) != 0) return;
        try
        {
            string baseUrl = GetServerBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/Accounts/me/summary");
            ApplyAuthHeaders(req);
            using var resp = await HttpClientHub.SharedClient.SendAsync(req, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var obj = JObject.Parse(raw);
            string aid = NormalizeAccountId((string)(obj["AccountId"] ?? obj["accountId"]));
            if (!string.IsNullOrWhiteSpace(aid) && aid != "00000000000000000000000000000000")
            {
                _currentAccountId = aid;
            }
            string uname = (string)(obj["Username"] ?? obj["username"] ?? obj["UserName"] ?? obj["userName"]);
            if (!string.IsNullOrWhiteSpace(uname))
            {
                _currentUsername = uname;
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _accountIdFetchInFlight, 0);
        }
    }

    internal static Task<HttpJsonResponse> GetJsonAsync(string relativePath, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Get, relativePath, null, ct);

    internal static Task<HttpJsonResponse> PostJsonAsync(string relativePath, object payload = null, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, relativePath, payload, ct);

    internal static Task<HttpJsonResponse> PutJsonAsync(string relativePath, object payload = null, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Put, relativePath, payload, ct);

    internal static Task<HttpJsonResponse> DeleteJsonAsync(string relativePath, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, relativePath, null, ct);

    internal static async Task<HttpJsonResponse> SendJsonAsync(
        HttpMethod method, string relativePath, object payload = null, CancellationToken ct = default)
    {
        string baseUrl = GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new HttpJsonResponse { Ok = false, StatusCode = 0, Error = "未配置自建服地址（ServerBase）" };
        }

        if (string.IsNullOrWhiteSpace(CurrentAccountId) && !string.IsNullOrWhiteSpace(CurrentAccessToken))
        {
            await EnsureAccountIdentityAsync(ct).ConfigureAwait(false);
        }

        string url = baseUrl + "/" + (relativePath ?? string.Empty).TrimStart('/');
        try
        {
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(method, url);
            ApplyAuthHeaders(req);

            if (payload != null)
            {
                string jsonBody = payload as string ?? JsonConvert.SerializeObject(payload);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            else if (method == HttpMethod.Post || method == HttpMethod.Put)
            {
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var resp = await HttpClientHub.SharedClient.SendAsync(req, linked.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpJsonResponse
            {
                Ok = resp.IsSuccessStatusCode,
                StatusCode = (int)resp.StatusCode,
                Body = body ?? string.Empty,
                Error = resp.IsSuccessStatusCode ? string.Empty : FormatFriendlyHttpError((int)resp.StatusCode, body),
            };
        }
        catch (OperationCanceledException)
        {
            return new HttpJsonResponse
            {
                Ok = false,
                StatusCode = 0,
                Error = "请求已取消或超时，请稍后重试",
            };
        }
        catch (Exception ex)
        {
            return new HttpJsonResponse
            {
                Ok = false,
                StatusCode = 0,
                Error = "网络连接失败：" + ex.Message,
            };
        }
    }

    private static string FormatFriendlyHttpError(int statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var jo = JObject.Parse(body);
                string serverMsg = (string)(jo["error"] ?? jo["message"] ?? jo["Message"]);
                if (!string.IsNullOrWhiteSpace(serverMsg))
                {
                    if (Modules.LocalizationOverlayModule.Instance != null &&
                        Modules.LocalizationOverlayModule.Instance.TryResolveText("zh-CN", null, serverMsg, "NetMessageError", out string zh))
                    {
                        return $"请求失败 ({statusCode})：{zh}";
                    }
                    return $"请求失败 ({statusCode})：{serverMsg}";
                }
            }
            catch { }
        }
        return $"服务端返回异常状态 ({statusCode})";
    }

    private static string TryReadProfileMember(string memberName)
    {
        try
        {
            var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var profileObs = cacheType?.GetField("Profile", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var profile = profileObs?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(profileObs);
            if (profile != null)
            {
                var prop = profile.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                string val = prop?.GetValue(profile)?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            var dataProfile = dataType?.GetProperty("Profile", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (dataProfile != null)
            {
                var prop = dataProfile.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                string val = prop?.GetValue(dataProfile)?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { }
        return null;
    }

    private static string TryReadAccessTokenFromGame()
    {
        try
        {
            var giType = AccessTools.TypeByName("TheBazaar.GameInstance");
            var gi = giType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var provider = giType?.GetProperty("AccessTokenProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gi);
            var tokenProp = provider?.GetType().GetProperty("AccessToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return tokenProp?.GetValue(provider)?.ToString();
        }
        catch { return null; }
    }

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        var steamAuth = AccessTools.TypeByName("TheBazaar.Auth.SteamAuthService");
        if (steamAuth == null)
        {
            BazaarLauncherPlugin.Log?.LogError("NameAuth: 未找到 SteamAuthService");
            return;
        }

        _cachedAuthResultType = AccessTools.TypeByName("TheBazaar.Auth.AuthResult");
        _cachedLoginResponseType = AccessTools.TypeByName("BazaarGameShared.TempoNet.Responses.LoginResponse");

        var authMethod = AccessTools.Method(steamAuth, "AuthenticateAsync");
        if (authMethod != null)
        {
            // 提前解析目标精确返回类型 Task<T> 的泛型参数 T，构建严格闭合的泛型派发方法（根治 P0-4）
            Type taskPayloadType = _cachedAuthResultType;
            if (authMethod.ReturnType.IsGenericType && authMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                taskPayloadType = authMethod.ReturnType.GetGenericArguments()[0];
            }

            if (taskPayloadType != null)
            {
                var open = AccessTools.Method(typeof(NameAuth), nameof(AuthenticateTypedAsync));
                _cachedTypedDispatchMethod = open?.MakeGenericMethod(taskPayloadType);
            }

            patcher.TryPatchMethod(
                authMethod,
                prefix: AccessTools.Method(typeof(NameAuth), nameof(AuthenticatePrefix)),
                label: "SteamAuthService.AuthenticateAsync");
        }

        patcher.TryPatchPropertyGetter(
            "TheBazaar.Auth.SteamAuthService",
            "RequiresEmailVerification",
            prefix: AccessTools.Method(typeof(NameAuth), nameof(ReturnFalse)),
            optional: true);

        patcher.TryPatchPropertyGetter(
            "TheBazaar.Auth.SteamAuthService",
            "IsAuthenticated",
            prefix: AccessTools.Method(typeof(NameAuth), nameof(ReturnTrue)),
            optional: true);
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        Apply(new SafeHarmonyPatcher("nameauth.legacy"));
    }

    private static bool ReturnFalse(ref bool __result) { __result = false; return false; }
    private static bool ReturnTrue(ref bool __result) { __result = true; return false; }

    private static bool AuthenticatePrefix(ref Task __result)
    {
        try
        {
            if (_cachedTypedDispatchMethod != null)
            {
                __result = (Task)_cachedTypedDispatchMethod.Invoke(null, null);
                return false;
            }
            __result = AuthenticateFallbackObjectAsync();
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogError("NameAuth 登录派发异常: " + ex);
            __result = CreateTypedErrorTask("TN-DISPATCH", "登录初始化异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 严格闭合返回类型 Task&lt;TAuthResult&gt; 的核心登录方法，彻底杜绝 Task&lt;object&gt; 泛型转换崩溃（P0-4）。
    /// </summary>
    private static async Task<TAuthResult> AuthenticateTypedAsync<TAuthResult>() where TAuthResult : class
    {
        object raw = await AuthenticateCoreAsync().ConfigureAwait(false);
        if (raw is TAuthResult typed)
        {
            return typed;
        }

        object err = BuildError("TN-TYPE", "服务端响应与客户端登录数据结构不匹配");
        return err as TAuthResult;
    }

    private static async Task<object> AuthenticateFallbackObjectAsync()
        => await AuthenticateCoreAsync().ConfigureAwait(false);

    private static Task CreateTypedErrorTask(string code, string message)
    {
        Type resultType = _cachedAuthResultType ?? typeof(object);
        try
        {
            object errObj = BuildError(code, message);
            MethodInfo fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))?.MakeGenericMethod(resultType);
            if (fromResult != null)
            {
                return (Task)fromResult.Invoke(null, new[] { errObj });
            }
        }
        catch { }
        return Task.FromResult<object>(null);
    }

    private static async Task<object> AuthenticateCoreAsync()
    {
        string name = PlayerIdentity.ResolveName();
        _currentUsername = name;
        BazaarLauncherPlugin.Log?.LogInfo($"NameAuth: 正在以用户名「{name}」登录自建服");

        string baseUrl = GetServerBaseUrl();
        string url = baseUrl + "/api/auth/login/user";
        string body = JsonConvert.SerializeObject(new { username = name });

        string json;
        try
        {
            using var linked = HttpClientHub.CreateLinkedCts();
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var resp = await HttpClientHub.SharedClient.SendAsync(req, linked.Token).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                BazaarLauncherPlugin.Log?.LogError($"NameAuth 登录失败: HTTP {(int)resp.StatusCode} {json}");
                return BuildError("TN-LOGIN", $"登录自建服失败（状态码 {(int)resp.StatusCode}）");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogError("NameAuth 网络连接异常: " + ex.Message);
            return BuildError("TN-NET", "无法连接自建服：" + ex.Message);
        }

        var loginResponseType = _cachedLoginResponseType ?? AccessTools.TypeByName("BazaarGameShared.TempoNet.Responses.LoginResponse");
        if (loginResponseType == null)
        {
            BazaarLauncherPlugin.Log?.LogError("NameAuth: 未找到 LoginResponse 类型");
            return BuildError("TN-SCHEMA", "客户端版本不匹配（缺少 LoginResponse 类型）");
        }

        object loginResponse = JsonConvert.DeserializeObject(json, loginResponseType);
        if (loginResponse == null)
        {
            return BuildError("TN-PARSE", "解析自建服登录响应失败");
        }

        try
        {
            string tok = AccessTools.Property(loginResponseType, "AccessToken")?.GetValue(loginResponse)?.ToString();
            if (!string.IsNullOrWhiteSpace(tok))
            {
                _currentAccessToken = tok;
                _ = Task.Run(async () =>
                {
                    await EnsureAccountIdentityAsync(HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: false);
                });
            }
        }
        catch { }

        BazaarLauncherPlugin.Log?.LogInfo("NameAuth: 自建服登录成功，已启动 Mod 与词典缓存同步");

        var authResultType = _cachedAuthResultType ?? AccessTools.TypeByName("TheBazaar.Auth.AuthResult");
        if (authResultType == null)
        {
            return BuildError("TN-AUTHRESULT", "客户端缺少 AuthResult 类型");
        }

        var okMethod = AccessTools.Method(authResultType, "Ok", new[] { loginResponseType })
            ?? AccessTools.Method(authResultType, "Ok");
        if (okMethod != null)
        {
            return okMethod.Invoke(null, new[] { loginResponse });
        }

        object result = Activator.CreateInstance(authResultType);
        SetMember(result, "Success", true);
        SetMember(result, "Data", loginResponse);
        return result;
    }

    private static object BuildError(string code, string message)
    {
        var authResultType = _cachedAuthResultType ?? AccessTools.TypeByName("TheBazaar.Auth.AuthResult");
        if (authResultType == null) return null;

        var errMethod = AccessTools.Method(authResultType, "Error");
        if (errMethod != null)
        {
            var ps = errMethod.GetParameters();
            var args = new object[ps.Length];
            args[0] = code;
            if (ps.Length > 1) args[1] = message;
            if (ps.Length > 2) args[2] = (HttpStatusCode?)null;
            return errMethod.Invoke(null, args);
        }

        object result = Activator.CreateInstance(authResultType);
        SetMember(result, "Success", false);
        SetMember(result, "ErrorCode", code);
        SetMember(result, "ErrorMessage", message);
        return result;
    }

    private static void SetMember(object obj, string name, object value)
    {
        var p = AccessTools.Property(obj.GetType(), name);
        if (p != null && p.CanWrite) { p.SetValue(obj, value); return; }
        AccessTools.Field(obj.GetType(), name)?.SetValue(obj, value);
    }
}
