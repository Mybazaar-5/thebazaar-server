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
    private static volatile bool _ignoreGameCachedAuthUntilRelogin;
    private static int _accountIdFetchInFlight;
    private static int _authGeneration;

    /// <summary>P1-15：与服务端 CustomRuleEndpoints.UsernameUtf8Header 严格对齐的 UTF-8 URL 编码用户名主请求头。</summary>
    internal const string UsernameUtf8Header = "X-Bazaar-Username-Utf8";

    /// <summary>P1-15：自建服登录/鉴权成功后通过 EventBus 广播的账号就绪事件（主线程安全封送）。</summary>
    internal readonly struct AuthSucceededEvent
    {
        public readonly string AccountId;
        public readonly string Username;
        public readonly int Generation;

        public AuthSucceededEvent(string accountId, string username, int generation)
        {
            AccountId = accountId ?? string.Empty;
            Username = username ?? string.Empty;
            Generation = generation;
        }
    }

    internal static int AuthGeneration => Volatile.Read(ref _authGeneration);

    /// <summary>P1-15：供无 EventBus 场景或显式订阅使用的静态鉴权成功回调。</summary>
    internal static event Action<AuthSucceededEvent> AuthSucceeded;

    private static Type _cachedAuthResultType;
    private static Type _cachedLoginResponseType;
    private static MethodInfo _cachedTypedDispatchMethod;

    internal sealed class HttpJsonResponse
    {
        public bool Ok { get; init; }
        public int StatusCode { get; init; }
        public string Body { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        /// <summary>P2-02：服务端响应的 ETag 头原值（用于后续 If-None-Match 协商缓存）。</summary>
        public string ETag { get; init; } = string.Empty;
        /// <summary>P2-02：是否命中 HTTP 304 Not Modified（命中时可直接复用本地缓存、零反序列化）。</summary>
        public bool NotModified { get; init; }
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
            if (_ignoreGameCachedAuthUntilRelogin)
            {
                return string.Empty;
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
            if (_ignoreGameCachedAuthUntilRelogin)
            {
                return string.Empty;
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

    /// <summary>D4-P1-6：切换服务器后清空旧服账号 ID 与原生 AccessTokenProvider 缓存 JWT，并在后台向新服静默重签。</summary>
    internal static void OnServerChanged()
    {
        _currentAccountId = null;
        _currentAccessToken = null;
        _ignoreGameCachedAuthUntilRelogin = true;
        TrySyncNativeAccessTokenProvider(string.Empty, string.Empty, default);
        _ = Task.Run(() => EnsureAccountIdentityAsync(HttpClientHub.ShutdownToken));
    }

    /// <summary>D4-P1-6：将 JWT 同步写入原生 AccessTokenProvider.SetCachedTokens(string, string, DateTime)，消除切服原生通道 401。</summary>
    private static void TrySyncNativeAccessTokenProvider(string accessToken, string refreshToken, DateTime expiresAtUtc)
    {
        try
        {
            var giType = AccessTools.TypeByName("TheBazaar.GameInstance");
            var gi = giType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (gi == null) return;
            var provider = giType.GetProperty("AccessTokenProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gi);
            if (provider == null) return;

            var setCached = provider.GetType().GetMethod(
                "SetCachedTokens",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(string), typeof(DateTime) },
                null);
            if (setCached != null)
            {
                setCached.Invoke(provider, new object[] { accessToken ?? string.Empty, refreshToken ?? string.Empty, expiresAtUtc });
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NameAuth] 同步原生 AccessTokenProvider 失败: " + ex.Message);
        }
    }

    private static bool IsPureAscii(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] > 0x7F) return false;
        }
        return true;
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
            string encodedUser = Uri.EscapeDataString(user);
            string headerVal = IsPureAscii(user) ? user : encodedUser;
            try { req.Headers.TryAddWithoutValidation("X-Username", headerVal); } catch { }
            // P1-15：同步附带纯 ASCII URL 编码的 UTF-8 用户名主头，确保非 ASCII 中文账号名（如「破风」）
            // 在冷启动未拿到 Bearer Token 前也能被服务端 CustomRuleEndpoints.ResolveAccountId 精确解析而不回退 guest
            try { req.Headers.TryAddWithoutValidation(UsernameUtf8Header, encodedUser); } catch { }
        }

        string token = CurrentAccessToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static void NotifyAuthSucceeded(string accountId, string username)
    {
        int gen = Interlocked.Increment(ref _authGeneration);
        var evt = new AuthSucceededEvent(
            string.IsNullOrWhiteSpace(accountId) ? CurrentAccountId : accountId,
            string.IsNullOrWhiteSpace(username) ? CurrentUsername : username,
            gen);
        try
        {
            BazaarLauncherPlugin.Host?.EventBus.Publish(evt);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NameAuth] 发布 AuthSucceededEvent 失败: " + ex.Message);
        }
        try
        {
            AuthSucceeded?.Invoke(evt);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NameAuth] 调用 AuthSucceeded 静态事件失败: " + ex.Message);
        }
    }

    private static void TryExtractIdentityFromLoginJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var obj = JObject.Parse(json);
            string aid = NormalizeAccountId((string)(obj["accountId"] ?? obj["AccountId"] ?? obj["id"] ?? obj["Id"]));
            if (!string.IsNullOrWhiteSpace(aid) && aid != "00000000000000000000000000000000")
            {
                _currentAccountId = aid;
            }
            string uname = (string)(obj["username"] ?? obj["Username"] ?? obj["userName"] ?? obj["UserName"]);
            if (!string.IsNullOrWhiteSpace(uname))
            {
                _currentUsername = uname;
            }
        }
        catch { }
    }

    internal static async Task EnsureAccountIdentityAsync(CancellationToken ct = default)
    {
        if (!_ignoreGameCachedAuthUntilRelogin && !string.IsNullOrWhiteSpace(CurrentAccountId)) return;
        if (Interlocked.CompareExchange(ref _accountIdFetchInFlight, 1, 0) != 0) return;
        bool reloginSucceeded = false;
        try
        {
            string baseUrl = GetServerBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            using var linked = HttpClientHub.CreateLinkedCts(ct);

            // 热切服后旧 JWT 失效：先向新服 /api/auth/login/user 签发新 JWT 与 AccountId，避免后续请求遭遇 401 INVALID_TOKEN
            if (_ignoreGameCachedAuthUntilRelogin)
            {
                string loginName = PlayerIdentity.ResolveName();
                using var loginReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/auth/login/user")
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new { username = loginName }), Encoding.UTF8, "application/json"),
                };
                using var loginResp = await HttpClientHub.SharedClient.SendAsync(loginReq, linked.Token).ConfigureAwait(false);
                if (loginResp.IsSuccessStatusCode)
                {
                    string loginRaw = await loginResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(loginRaw))
                    {
                        var lObj = JObject.Parse(loginRaw);
                        string newTok = (string)(lObj["accessToken"] ?? lObj["AccessToken"] ?? lObj["token"]);
                        string newAid = NormalizeAccountId((string)(lObj["accountId"] ?? lObj["AccountId"] ?? lObj["id"] ?? lObj["Id"]));
                        string newUser = (string)(lObj["username"] ?? lObj["Username"]);
                        if (!string.IsNullOrWhiteSpace(newTok))
                        {
                            _currentAccessToken = newTok;
                            _ignoreGameCachedAuthUntilRelogin = false;
                            reloginSucceeded = true;
                            TrySyncNativeAccessTokenProvider(newTok, string.Empty, DateTime.UtcNow.AddHours(12));
                        }
                        if (!string.IsNullOrWhiteSpace(newAid) && newAid != "00000000000000000000000000000000") _currentAccountId = newAid;
                        if (!string.IsNullOrWhiteSpace(newUser)) _currentUsername = newUser;
                    }
                }
                if (!string.IsNullOrWhiteSpace(_currentAccountId)) return;
            }

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

        if (reloginSucceeded)
        {
            NotifyAuthSucceeded(_currentAccountId, _currentUsername);
        }
    }

    internal static Task<HttpJsonResponse> GetJsonAsync(
        string relativePath,
        CancellationToken ct = default,
        string ifNoneMatch = null)
        => SendJsonAsync(HttpMethod.Get, relativePath, null, ct, ifNoneMatch);

    internal static Task<HttpJsonResponse> PostJsonAsync(string relativePath, object payload = null, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Post, relativePath, payload, ct);

    internal static Task<HttpJsonResponse> PutJsonAsync(string relativePath, object payload = null, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Put, relativePath, payload, ct);

    internal static Task<HttpJsonResponse> DeleteJsonAsync(string relativePath, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, relativePath, null, ct);

    internal static async Task<HttpJsonResponse> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object payload = null,
        CancellationToken ct = default,
        string ifNoneMatch = null)
    {
        string baseUrl = GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new HttpJsonResponse { Ok = false, StatusCode = 0, Error = "未配置自建服地址（ServerBase）" };
        }

        if (_ignoreGameCachedAuthUntilRelogin
            || (string.IsNullOrWhiteSpace(CurrentAccountId) && !string.IsNullOrWhiteSpace(CurrentAccessToken)))
        {
            await EnsureAccountIdentityAsync(ct).ConfigureAwait(false);
        }

        string url = baseUrl + "/" + (relativePath ?? string.Empty).TrimStart('/');
        try
        {
            using var linked = HttpClientHub.CreateLinkedCts(ct);
            using var req = new HttpRequestMessage(method, url);
            ApplyAuthHeaders(req);

            // P2-02：按需附加 If-None-Match 条件请求头，使服务端 ETag 协商缓存命中 304 Not Modified
            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            {
                try { req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch.Trim()); } catch { }
            }

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
            int statusCode = (int)resp.StatusCode;
            bool notModified = resp.StatusCode == HttpStatusCode.NotModified;
            string responseEtag = ExtractETagHeader(resp);
            string body = notModified
                ? string.Empty
                : (resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : string.Empty);

            if (!resp.IsSuccessStatusCode && !notModified)
            {
                var actualMethod = resp.RequestMessage?.Method ?? method;
                var actualUri = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                if (actualMethod != method)
                {
                    BazaarLauncherPlugin.Log?.LogWarning(
                        $"[NameAuth] 检测到 HTTP 重定向降级：{method} -> {actualMethod} ({actualUri})，状态码={statusCode}");
                }
                else if (statusCode == 405)
                {
                    BazaarLauncherPlugin.Log?.LogWarning(
                        $"[NameAuth] HTTP 405 Method Not Allowed：{actualMethod} {actualUri}");
                }
            }
            return new HttpJsonResponse
            {
                Ok = resp.IsSuccessStatusCode,
                NotModified = notModified,
                StatusCode = statusCode,
                ETag = responseEtag,
                Body = body ?? string.Empty,
                Error = (resp.IsSuccessStatusCode || notModified) ? string.Empty : FormatFriendlyHttpError(statusCode, body),
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

    private static string ExtractETagHeader(HttpResponseMessage resp)
    {
        if (resp?.Headers == null) return string.Empty;
        try
        {
            if (resp.Headers.ETag != null)
            {
                return resp.Headers.ETag.ToString();
            }
            if (resp.Headers.TryGetValues("ETag", out var values))
            {
                foreach (string val in values)
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }
            }
        }
        catch { }
        return string.Empty;
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

        bool dispatchedPostAuthSync = false;
        try
        {
            TryExtractIdentityFromLoginJson(json);
            string tok = AccessTools.Property(loginResponseType, "AccessToken")?.GetValue(loginResponse)?.ToString();
            string refreshTok = AccessTools.Property(loginResponseType, "RefreshToken")?.GetValue(loginResponse)?.ToString();
            if (!string.IsNullOrWhiteSpace(tok))
            {
                _currentAccessToken = tok;
                _ignoreGameCachedAuthUntilRelogin = false;
                TrySyncNativeAccessTokenProvider(tok, refreshTok ?? string.Empty, DateTime.UtcNow.AddHours(12));
                dispatchedPostAuthSync = true;
                _ = Task.Run(async () =>
                {
                    await EnsureAccountIdentityAsync(HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    // P1-15：账号身份与 Token 均已落定，立刻通知 CustomGameplayUI 等订阅者重拉个人与活跃对局规则
                    NotifyAuthSucceeded(_currentAccountId, _currentUsername);
                    ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: false);
                });
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NameAuth] 登录响应 Token 同步异常: " + ex.Message);
        }

        if (!dispatchedPostAuthSync)
        {
            NotifyAuthSucceeded(_currentAccountId, _currentUsername);
        }

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
