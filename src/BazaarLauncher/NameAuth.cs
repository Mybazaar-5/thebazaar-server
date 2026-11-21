using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarLauncher;

/// <summary>
/// 把 SteamAuthService 换成「用户名即登录」。
///
/// 原版：SteamAuthService.AuthenticateAsync()
///          → SteamModule.GetSteamTicket() → POST /api/auth/login/silent
/// 这里：POST /api/auth/login/user {"username": 名字}，直接拿 LoginResponse。
///
/// 只替换 AuthenticateAsync，不碰 TryAutoLoginAsync —— 后者是公开接口，
/// 内部会调用被 patch 的 AuthenticateAsync，所以替换点越靠内越安全。
/// </summary>
internal static class NameAuth
{
    private static bool _applied;
    private static volatile string _currentUsername;
    private static volatile string _currentAccountId;
    private static volatile string _currentAccessToken;
    private static int _accountIdFetchInFlight;

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    /// <summary>服务端 HTTP 响应包装。</summary>
    internal sealed class HttpJsonResponse
    {
        public bool Ok { get; init; }
        public int StatusCode { get; init; }
        public string Body { get; init; } = "";
        public string Error { get; init; } = "";
    }

    /// <summary>当前已解析的玩家用户名。</summary>
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

    /// <summary>当前已登录账号的标准化 AccountId（32 位小写无连字符 GUID）。</summary>
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
            return "";
        }
    }

    /// <summary>当前已持有的 AccessToken。</summary>
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
            return "";
        }
    }

    internal static string NormalizeAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return "";
        return accountId.Replace("-", "").Trim().ToLowerInvariant();
    }

    internal static string GetServerBaseUrl()
        => (BazaarLauncherPlugin.ServerBase?.Value ?? "").TrimEnd('/');

    /// <summary>为请求附带服务端鉴权头：X-Account-Id / X-Username / Authorization。</summary>
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

    /// <summary>确保已知 AccountId（若尚未从 ClientCache 取得，则回查 /api/Accounts/me/summary）。</summary>
    internal static async Task EnsureAccountIdentityAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(CurrentAccountId)) return;
        if (Interlocked.CompareExchange(ref _accountIdFetchInFlight, 1, 0) != 0) return;
        try
        {
            string baseUrl = GetServerBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/Accounts/me/summary");
            ApplyAuthHeaders(req);
            using var resp = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
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

    /// <summary>向 BazaarLauncherPlugin.ServerBase 发送带鉴权头的 JSON 请求。</summary>
    internal static async Task<HttpJsonResponse> SendJsonAsync(
        HttpMethod method, string relativePath, object payload = null, CancellationToken ct = default)
    {
        string baseUrl = GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new HttpJsonResponse { Ok = false, StatusCode = 0, Error = "ServerBase 未配置" };
        }

        if (string.IsNullOrWhiteSpace(CurrentAccountId) && !string.IsNullOrWhiteSpace(CurrentAccessToken))
        {
            await EnsureAccountIdentityAsync(ct).ConfigureAwait(false);
        }

        string url = baseUrl + "/" + (relativePath ?? "").TrimStart('/');
        try
        {
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

            using var resp = await SharedHttp.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new HttpJsonResponse
            {
                Ok = resp.IsSuccessStatusCode,
                StatusCode = (int)resp.StatusCode,
                Body = body ?? "",
                Error = resp.IsSuccessStatusCode ? "" : $"HTTP {(int)resp.StatusCode}: {body}",
            };
        }
        catch (Exception ex)
        {
            return new HttpJsonResponse
            {
                Ok = false,
                StatusCode = 0,
                Error = ex.Message,
            };
        }
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

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        var steamAuth = AccessTools.TypeByName("TheBazaar.Auth.SteamAuthService");
        if (steamAuth == null)
        {
            BazaarLauncherPlugin.Log.LogError("SteamAuthService not found");
            return;
        }

        var auth = AccessTools.Method(steamAuth, "AuthenticateAsync");
        if (auth == null)
        {
            BazaarLauncherPlugin.Log.LogError("SteamAuthService.AuthenticateAsync not found");
            return;
        }

        harmony.Patch(auth, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(NameAuth), nameof(AuthenticatePrefix))));
        BazaarLauncherPlugin.Log.LogInfo("Patched SteamAuthService.AuthenticateAsync -> name login");

        // RequiresEmailVerification 恒 false
        var req = AccessTools.Property(steamAuth, "RequiresEmailVerification");
        if (req != null && req.CanRead && req.GetMethod != null)
        {
            harmony.Patch(req.GetMethod, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NameAuth), nameof(ReturnFalse))));
        }

        // IsAuthenticated 恒 true
        var isAuth = AccessTools.Property(steamAuth, "IsAuthenticated");
        if (isAuth != null && isAuth.CanRead && isAuth.GetMethod != null)
        {
            harmony.Patch(isAuth.GetMethod, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NameAuth), nameof(ReturnTrue))));
        }
    }

    private static bool ReturnFalse(ref bool __result) { __result = false; return false; }
    private static bool ReturnTrue(ref bool __result) { __result = true; return false; }

    /// <summary>
    /// 替换 AuthenticateAsync() 的实现。
    /// 返回 Task&lt;AuthResult&gt;：用反射构造 AuthResult.Ok(LoginResponse)。
    /// </summary>
    private static bool AuthenticatePrefix(ref Task __result)
    {
        try
        {
            __result = AuthenticateAsync();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogError("NameAuth dispatch failed: " + ex);
            __result = Task.FromResult<object>(null);
        }
        return false;
    }

    private static async Task<object> AuthenticateAsync()
    {
        string name = PlayerIdentity.ResolveName();
        _currentUsername = name;
        BazaarLauncherPlugin.Log.LogInfo($"NameAuth: logging in as '{name}'");

        string baseUrl = BazaarLauncherPlugin.ServerBase.Value.TrimEnd('/');
        string url = baseUrl + "/api/auth/login/user";
        string body = "{\"username\":\"" + name.Replace("\"", "\\\"") + "\"}";

        string json;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(url, content);
            json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                BazaarLauncherPlugin.Log.LogError($"NameAuth: HTTP {(int)resp.StatusCode} {json}");
                return BuildError("TN-LOGIN", "login failed " + resp.StatusCode);
            }
        }

        var loginResponseType = AccessTools.TypeByName("BazaarGameShared.TempoNet.Responses.LoginResponse");
        if (loginResponseType == null)
        {
            BazaarLauncherPlugin.Log.LogError("LoginResponse type not found");
            return null;
        }

        object loginResponse = JsonConvert.DeserializeObject(json, loginResponseType);
        try
        {
            string tok = AccessTools.Property(loginResponseType, "AccessToken")?.GetValue(loginResponse)?.ToString();
            if (!string.IsNullOrWhiteSpace(tok))
            {
                _currentAccessToken = tok;
                _ = Task.Run(() => EnsureAccountIdentityAsync());
            }
        }
        catch { }
        BazaarLauncherPlugin.Log.LogInfo("NameAuth: got LoginResponse");

        var authResultType = AccessTools.TypeByName("TheBazaar.Auth.AuthResult");
        if (authResultType == null)
        {
            BazaarLauncherPlugin.Log.LogError("AuthResult type not found");
            return loginResponse;
        }

        var okMethod = AccessTools.Method(authResultType, "Ok");
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
        var authResultType = AccessTools.TypeByName("TheBazaar.Auth.AuthResult");
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
