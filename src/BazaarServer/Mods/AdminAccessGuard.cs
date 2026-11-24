using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BazaarServer.Mods;

/// <summary>
/// 管理员权限评估结果（供 /api/workshop/*、/api/mods/*、/api/community/mods/* 统一消费）。
/// </summary>
public readonly record struct AdminAccessDecision(
    bool Allowed,
    string ModeLabel,
    string ReasonCode,
    bool TokenConfigured,
    bool RequireTokenOnly);

/// <summary>
/// 全服唯一管理员门控服务（P0-36）：
/// 1. 统一收口 WorkshopEndpoints、CommunityModEndpoints 与 ModEndpoints（/api/mods/reload、/toggle）的全部写/管理权限判定；
/// 2. 彻底消灭 X-Forwarded-For 首 IP 伪造绕过：只要请求携带任何代理转发头（无论其值是否为 127.0.0.1），一律拒绝视为本机回环；
/// 3. 严禁从 URL Query <c>?token=</c> 读取管理员口令（仅认请求头 <c>X-Workshop-Token</c>），并经 SHA-256 常量时间比对消除长度侧信道；
/// 4. 支持 <c>readonly</c>（最高优先禁写）、<c>open</c>、<c>token-only</c>（或 <c>BAZAAR_REQUIRE_ADMIN_TOKEN=1|true</c>，强制公网反代禁止回环免密）与 <c>auto</c> 模式；
/// 5. 支持注入环境变量委托，便于并发单测零环境变量污染验证。
/// </summary>
public sealed class AdminAccessGuard
{
    public static readonly string[] ProxyHeaderNames =
    [
        "X-Forwarded-For",
        "X-Real-IP",
        "Forwarded",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "CF-Connecting-IP",
        "True-Client-IP",
    ];

    /// <summary>进程默认单例（供未显式注册 DI 的轻量测试宿主安全回退）。</summary>
    public static AdminAccessGuard Default { get; } = new();

    private readonly Func<string, string?> _getEnv;

    public AdminAccessGuard(Func<string, string?>? envProvider = null)
    {
        _getEnv = envProvider ?? Environment.GetEnvironmentVariable;
    }

    public bool IsTokenConfigured => !string.IsNullOrWhiteSpace(_getEnv("BAZAAR_WORKSHOP_TOKEN"));

    public bool IsRequireTokenOnlyConfigured()
    {
        string modeEnv = (_getEnv("BAZAAR_WORKSHOP_MODE") ?? "auto").Trim().ToLowerInvariant();
        if (modeEnv == "token-only")
            return true;
        string? reqEnv = _getEnv("BAZAAR_REQUIRE_ADMIN_TOKEN");
        return string.Equals(reqEnv, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reqEnv, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>评估当前 HTTP 请求是否具备服务器管理员/工坊写权限。</summary>
    public AdminAccessDecision Evaluate(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        string modeEnv = (_getEnv("BAZAAR_WORKSHOP_MODE") ?? "auto").Trim().ToLowerInvariant();
        string? configuredToken = _getEnv("BAZAAR_WORKSHOP_TOKEN")?.Trim();
        bool tokenConfigured = !string.IsNullOrEmpty(configuredToken);
        bool requireTokenOnly = IsRequireTokenOnlyConfigured();

        // 1. readonly 拥有最高优先级，覆写一切回环与口令放行
        if (modeEnv == "readonly")
            return new AdminAccessDecision(false, "readonly", "MODE_READONLY", tokenConfigured, requireTokenOnly);

        // 2. 显式 open 模式
        if (modeEnv == "open")
            return new AdminAccessDecision(true, "open-rw", "MODE_OPEN", tokenConfigured, requireTokenOnly);

        // 3. 仅校验请求头 X-Workshop-Token（严禁接受 URL Query ?token=），SHA-256 恒定时间比对防长度侧信道
        if (tokenConfigured && ctx.Request.Headers.TryGetValue("X-Workshop-Token", out var providedVals))
        {
            string provided = (providedVals.FirstOrDefault() ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(provided) && ConstantTimeEquals(provided, configuredToken!))
                return new AdminAccessDecision(true, "token-rw", "VALID_TOKEN", tokenConfigured, requireTokenOnly);
        }

        // 4. token-only / BAZAAR_REQUIRE_ADMIN_TOKEN：公网反代双保险，强制禁止本机回环免密
        if (requireTokenOnly)
            return new AdminAccessDecision(false, "readonly", "TOKEN_REQUIRED", tokenConfigured, requireTokenOnly);

        // 5. 凡存在任意代理转发头（无论内容是否为 127.0.0.1 或空串），一律拒绝本机回环直连判定
        if (HasAnyProxyHeader(ctx.Request.Headers))
            return new AdminAccessDecision(false, "readonly", "PROXY_HEADER_PRESENT", tokenConfigured, requireTokenOnly);

        // 6. 仅无代理头的真实回环（或内存 TestServer 无远端 socket IP）允许本机单机管理
        if (IsDirectLoopbackConnection(ctx))
            return new AdminAccessDecision(true, "local-rw", "LOCAL_LOOPBACK", tokenConfigured, requireTokenOnly);

        return new AdminAccessDecision(false, "readonly", "REMOTE_READONLY", tokenConfigured, requireTokenOnly);
    }

    public bool IsAllowed(HttpContext ctx) => Evaluate(ctx).Allowed;

    public bool TryReject(
        HttpContext ctx,
        JsonSerializerOptions jsonOpts,
        out IResult? deniedResult,
        string errorCode = "WORKSHOP_READONLY_MODE",
        string? customMessageZh = null)
    {
        var decision = Evaluate(ctx);
        if (decision.Allowed)
        {
            deniedResult = null;
            return false;
        }

        deniedResult = Results.Json(new
        {
            ok = false,
            errorCode,
            reason = decision.ReasonCode,
            error = customMessageZh
                ?? "当前为公网只读安全沙盒模式，禁止直接向服务器磁盘写入或热重载。你可以在本机运行单机版 /workshop 创作，或在右上角填入管理员口令（X-Workshop-Token）解锁写入权限。"
        }, jsonOpts, statusCode: StatusCodes.Status403Forbidden);
        return true;
    }

    public static bool HasAnyProxyHeader(IHeaderDictionary headers)
    {
        for (int i = 0; i < ProxyHeaderNames.Length; i++)
        {
            if (headers.ContainsKey(ProxyHeaderNames[i]))
                return true;
        }
        return false;
    }

    public static bool IsDirectLoopbackConnection(HttpContext ctx)
    {
        var remoteIp = ctx.Connection.RemoteIpAddress;
        if (remoteIp == null)
            return true; // TestServer / 内存测试环境无真实 socket IP 视作本机
        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();
        return IPAddress.IsLoopback(remoteIp);
    }

    /// <summary>基于 SHA-256 摘要执行固定长度恒定时间比对，避免暴露口令长度信息。</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    public static AdminAccessGuard ResolveFrom(IServiceProvider? services)
        => services?.GetService<AdminAccessGuard>() ?? Default;
}
