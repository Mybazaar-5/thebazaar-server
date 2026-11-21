using System.Text.Json;

namespace BazaarServer.Accounts;

/// <summary>
/// 统一身份鉴权与防冒充解析器：
/// 1. Authorization: Bearer Token 永远为最高权威；携带无效/过期 Bearer 时立即拒绝，严禁降级；
/// 2. 彻底封堵仅凭 X-Account-Id / aid 请求头任意冒充他人账号的 P0 安全漏洞；
/// 3. 支持环境变量 BAZAAR_STRICT_AUTH=1（公网严格模式，全站仅接受有效 Bearer Token）；
/// 4. 默认兼容模式（BAZAAR_STRICT_AUTH=0）下，仅在调用方显式携带 X-Username/uid、未伪造不匹配 X-Account-Id、
///    且目标账号未开启口令保护时受控降级，完全匿名请求绝不伪装成已登录用户。
/// </summary>
public static class AccountAuthResolver
{
    public enum AuthStrength
    {
        None = 0,
        LegacyUsername = 1,
        BearerToken = 2,
    }

    public sealed record AuthResolution(
        AccountStore.Account? Account,
        AuthStrength Strength,
        int FailureStatusCode,
        string? ErrorCode,
        string? ErrorMessageZh)
    {
        public bool IsAuthenticated => Account != null && FailureStatusCode == 0;
        public bool HasExplicitFailure => FailureStatusCode != 0;
    }

    /// <summary>是否开启公网严格鉴权模式（环境变量 BAZAAR_STRICT_AUTH=1|true）。</summary>
    public static bool IsStrictAuthEnabled()
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_STRICT_AUTH");
        return string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析当前请求身份。
    /// - requireAuth=true 时：匿名或认证失败均返回非空 FailureStatusCode（默认 401）；
    /// - allowLegacyUsernameFallback=false 时：强制要求 Bearer Token（不受环境变量宽松开关影响）。
    /// </summary>
    public static AuthResolution Resolve(
        HttpContext ctx,
        AccountStore accounts,
        bool requireAuth = true,
        bool allowLegacyUsernameFallback = true)
    {
        string authHeader = ctx.Request.Headers["Authorization"].ToString().Trim();
        if (!string.IsNullOrEmpty(authHeader))
        {
            var bearerAcc = accounts.FindByAccessToken(authHeader);
            if (bearerAcc != null)
            {
                return new AuthResolution(bearerAcc, AuthStrength.BearerToken, 0, null, null);
            }
            return new AuthResolution(
                null,
                AuthStrength.None,
                StatusCodes.Status401Unauthorized,
                "INVALID_TOKEN",
                "登录令牌无效或已过期，请重新登录。");
        }

        // 无 Bearer 令牌时，检测是否试图仅凭 X-Account-Id / aid 冒充他人账号
        string claimedAid = ctx.Request.Headers["X-Account-Id"].ToString().Trim();
        if (string.IsNullOrEmpty(claimedAid))
            claimedAid = ctx.Request.Headers["aid"].ToString().Trim();

        string legacyUser = ctx.Request.Headers["X-Username"].ToString().Trim();
        if (string.IsNullOrEmpty(legacyUser))
            legacyUser = ctx.Request.Headers["uid"].ToString().Trim();

        if (!string.IsNullOrEmpty(claimedAid) && string.IsNullOrEmpty(legacyUser))
        {
            return new AuthResolution(
                null,
                AuthStrength.None,
                StatusCodes.Status401Unauthorized,
                "ACCOUNT_ID_SPOOF_BLOCKED",
                "出于账号安全保护，禁止仅凭账号 ID 请求头访问，请先登录获取令牌。");
        }

        bool strict = IsStrictAuthEnabled() || !allowLegacyUsernameFallback;
        if (!strict && !string.IsNullOrEmpty(legacyUser))
        {
            string normalizedName = AccountStore.NormalizeName(legacyUser);
            var existing = accounts.FindByUsername(normalizedName);
            if (existing != null && existing.HasSecretProtection)
            {
                return new AuthResolution(
                    null,
                    AuthStrength.None,
                    StatusCodes.Status401Unauthorized,
                    "PASSWORD_AUTH_REQUIRED",
                    "该账号已开启口令保护，请使用登录令牌访问。");
            }

            var acc = existing ?? accounts.GetOrCreateByName(normalizedName);
            if (!string.IsNullOrEmpty(claimedAid))
            {
                string normClaimed = claimedAid.Replace("-", "").ToLowerInvariant();
                if (!string.Equals(normClaimed, acc.AccountId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                {
                    return new AuthResolution(
                        null,
                        AuthStrength.None,
                        StatusCodes.Status401Unauthorized,
                        "IDENTITY_MISMATCH",
                        "账号标识与用户名不匹配，已拒绝访问，请使用有效登录令牌。");
                }
            }

            accounts.TouchActivity(acc);
            return new AuthResolution(acc, AuthStrength.LegacyUsername, 0, null, null);
        }

        if (requireAuth)
        {
            return new AuthResolution(
                null,
                AuthStrength.None,
                StatusCodes.Status401Unauthorized,
                "AUTH_REQUIRED",
                "请先登录后再执行此操作。");
        }

        return new AuthResolution(null, AuthStrength.None, 0, null, null);
    }

    /// <summary>快捷构造统一中文错误响应。</summary>
    public static IResult ErrorResult(AuthResolution res, JsonSerializerOptions? jsonOpts = null)
    {
        int code = res.FailureStatusCode > 0 ? res.FailureStatusCode : StatusCodes.Status401Unauthorized;
        var payload = new
        {
            error = res.ErrorMessageZh ?? "身份认证失败，请重新登录。",
            code = res.ErrorCode ?? "AUTH_REQUIRED",
        };
        return jsonOpts != null
            ? Results.Json(payload, jsonOpts, statusCode: code)
            : Results.Json(payload, statusCode: code);
    }
}
