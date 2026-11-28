using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BazaarServer.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarServer.Planner;

/// <summary>编排台请求鉴权主体（同时支持网页 X-Planner-Session 与游戏内 Bearer 令牌）。</summary>
public sealed record PlannerAuthPrincipal(
    string AccountId,
    string Username,
    string AuthSource,
    AccountStore.Account? Account = null);

/// <summary>编排台统一鉴权结果（失败时携带 HTTP 状态码、错误码与简体中文提示）。</summary>
public sealed record PlannerAuthResolution(
    bool IsAuthenticated,
    PlannerAuthPrincipal? Principal,
    int FailureStatusCode,
    string? ErrorCode,
    string? ErrorMessageZh);

/// <summary>游戏内换取的一次性网页打开票据结果（有效期 120 秒）。</summary>
public sealed record WebTicketIssueResult(
    string Ticket,
    string Path,
    int ExpiresInSeconds,
    DateTime ExpiresAtUtc);

/// <summary>签发成功的编排台网页会话令牌（有效期 4 小时，前缀 pln_）。</summary>
public sealed record PlannerIssuedSession(
    string Token,
    DateTime ExpiresAtUtc,
    string Username,
    string AccountId);

/// <summary>票据兑换或浏览器口令直登结果。</summary>
public sealed record PlannerSessionExchangeResult(
    bool Ok,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessageZh,
    PlannerIssuedSession? Session);

/// <summary>
/// M4 编排台鉴权与纯内存会话存储：
/// - 一次性 Web 票据（256-bit base64url，120 秒过期，内存仅存 SHA-256 哈希，TryRemove 原子消费，上限 10,000 超出淘汰最旧）；
/// - 编排台会话令牌（pln_ + 256-bit base64url，4 小时过期，内存仅存 SHA-256 哈希，上限 20,000 超出淘汰过期/最旧）；
/// - 浏览器口令直登（不自动建号、不自动绑口令，未设口令返回 403 PASSWORD_REQUIRED_FOR_WEB_LOGIN，同 IP+账号 10 次/60s 防爆破）；
/// - 统一鉴权仅接受 X-Planner-Session 与有效 Bearer（严禁读取 X-Account-Id 与 legacy username fallback）。
/// </summary>
public sealed class PlannerSessionStore
{
    public const int MaxTickets = 10_000;
    public const int MaxSessions = 20_000;
    public const int MaxRateLimitBuckets = 50_000;
    public const int DefaultTicketLifetimeSeconds = 120;
    public const int DefaultSessionLifetimeHours = 4;
    public const int MaxLoginFailuresPerWindow = 10;
    public const string SessionTokenPrefix = "pln_";
    public const string SessionHeaderName = "X-Planner-Session";

    public static readonly TimeSpan DefaultTicketLifetime = TimeSpan.FromSeconds(DefaultTicketLifetimeSeconds);
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(DefaultSessionLifetimeHours);
    public static readonly TimeSpan DefaultLoginFailureWindow = TimeSpan.FromSeconds(60);

    private sealed record TicketEntry(
        string AccountId,
        string Username,
        DateTime ExpiresAtUtc,
        long CreatedSeq);

    private sealed record SessionEntry(
        string AccountId,
        string Username,
        DateTime ExpiresAtUtc,
        long CreatedSeq);

    private sealed class LoginFailureBucket
    {
        public readonly object Sync = new();
        public readonly Queue<DateTime> FailuresUtc = new();
    }

    private readonly Func<DateTime> _utcNow;
    private readonly int _maxTickets;
    private readonly int _maxSessions;
    private readonly TimeSpan _ticketLifetime;
    private readonly TimeSpan _sessionLifetime;
    private readonly int _maxLoginFailures;
    private readonly TimeSpan _loginFailureWindow;

    // 内存中仅以 SHA-256 十六进制哈希为键，绝不留存任何明文票据或会话令牌
    private readonly ConcurrentDictionary<string, TicketEntry> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Hash, long Seq)> _ticketOrder = new();
    private readonly object _ticketEvictLock = new();

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Hash, long Seq)> _sessionOrder = new();
    private readonly object _sessionEvictLock = new();

    private readonly ConcurrentDictionary<string, LoginFailureBucket> _loginFailures = new(StringComparer.OrdinalIgnoreCase);

    private long _seqCounter;

    /// <summary>当前活跃（未消费）一次性票据数。</summary>
    public int ActiveTicketCount => _tickets.Count;

    /// <summary>当前活跃编排台网页会话数。</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>当前内存中的防爆破限流桶数量。</summary>
    public int ActiveRateLimitBucketCount => _loginFailures.Count;

    [ActivatorUtilitiesConstructor]
    public PlannerSessionStore()
        : this(utcNowProvider: null)
    {
    }

    /// <summary>支持注入时钟、容量上限与有效期参数，便于单元测试验证过期淘汰、容量上限与限流行为。</summary>
    public PlannerSessionStore(
        Func<DateTime>? utcNowProvider,
        int maxTickets = MaxTickets,
        int maxSessions = MaxSessions,
        TimeSpan? ticketLifetime = null,
        TimeSpan? sessionLifetime = null,
        int maxLoginFailures = MaxLoginFailuresPerWindow,
        TimeSpan? loginFailureWindow = null)
    {
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
        _maxTickets = Math.Max(1, maxTickets);
        _maxSessions = Math.Max(1, maxSessions);
        _ticketLifetime = ticketLifetime is { Ticks: > 0 } tl ? tl : DefaultTicketLifetime;
        _sessionLifetime = sessionLifetime is { Ticks: > 0 } sl ? sl : DefaultSessionLifetime;
        _maxLoginFailures = Math.Max(1, maxLoginFailures);
        _loginFailureWindow = loginFailureWindow is { Ticks: > 0 } fw ? fw : DefaultLoginFailureWindow;
    }

    /// <summary>为已通过游戏内 Bearer 认证的账号签发 2 分钟一次性网页票据。</summary>
    public WebTicketIssueResult IssueWebTicket(AccountStore.Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return IssueWebTicket(NormalizeAccountId(account.AccountId), account.Username ?? string.Empty);
    }

    /// <summary>按账号 ID 与用户名签发 2 分钟一次性网页票据（仅向内存写入 SHA-256 哈希）。</summary>
    public WebTicketIssueResult IssueWebTicket(string accountId, string username)
    {
        string normalizedAid = NormalizeAccountId(accountId);
        if (string.IsNullOrEmpty(normalizedAid))
            throw new ArgumentException("AccountId cannot be empty.", nameof(accountId));

        DateTime now = _utcNow();
        DateTime expiresAt = now.Add(_ticketLifetime);
        string rawTicket = GenerateBase64Url256Bit();
        string hash = ComputeSha256Hex(rawTicket);
        long seq = Interlocked.Increment(ref _seqCounter);

        var entry = new TicketEntry(normalizedAid, username ?? string.Empty, expiresAt, seq);
        _tickets[hash] = entry;
        _ticketOrder.Enqueue((hash, seq));

        if (_tickets.Count > _maxTickets)
            EnforceTicketCapacity(now);

        int expiresInSeconds = Math.Max(1, (int)Math.Ceiling(_ticketLifetime.TotalSeconds));
        return new WebTicketIssueResult(
            Ticket: rawTicket,
            Path: $"/planner/#ticket={rawTicket}",
            ExpiresInSeconds: expiresInSeconds,
            ExpiresAtUtc: expiresAt);
    }

    /// <summary>
    /// 原子消费一次性 Web 票据并换发 4 小时 pln_ 网页会话令牌：
    /// 使用 ConcurrentDictionary.TryRemove 保证并发重放时至多一次成功，无效/过期/已用统一返回 401 TICKET_INVALID。
    /// </summary>
    public PlannerSessionExchangeResult ExchangeWebTicket(string? rawTicket)
    {
        if (string.IsNullOrWhiteSpace(rawTicket))
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "TICKET_INVALID",
                ErrorMessageZh: "登录链接无效或已失效，请从游戏内重新点击「打开网页编排台」。",
                Session: null);
        }

        string hash = ComputeSha256Hex(rawTicket.Trim());
        if (!_tickets.TryRemove(hash, out var entry))
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "TICKET_INVALID",
                ErrorMessageZh: "登录链接无效或已使用，请从游戏内重新点击「打开网页编排台」。",
                Session: null);
        }

        DateTime now = _utcNow();
        if (entry.ExpiresAtUtc <= now)
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "TICKET_INVALID",
                ErrorMessageZh: "登录链接已过期，请从游戏内重新点击「打开网页编排台」。",
                Session: null);
        }

        var issued = IssueSession(entry.AccountId, entry.Username);
        return new PlannerSessionExchangeResult(
            Ok: true,
            StatusCode: StatusCodes.Status200OK,
            ErrorCode: null,
            ErrorMessageZh: null,
            Session: issued);
    }

    /// <summary>签发 4 小时编排台网页会话令牌（pln_ 前缀 + 256-bit base64url，仅存 SHA-256 哈希）。</summary>
    public PlannerIssuedSession IssueSession(string accountId, string username)
    {
        string normalizedAid = NormalizeAccountId(accountId);
        if (string.IsNullOrEmpty(normalizedAid))
            throw new ArgumentException("AccountId cannot be empty.", nameof(accountId));

        DateTime now = _utcNow();
        DateTime expiresAt = now.Add(_sessionLifetime);
        string rawToken = SessionTokenPrefix + GenerateBase64Url256Bit();
        string hash = ComputeSha256Hex(rawToken);
        long seq = Interlocked.Increment(ref _seqCounter);

        var entry = new SessionEntry(normalizedAid, username ?? string.Empty, expiresAt, seq);
        _sessions[hash] = entry;
        _sessionOrder.Enqueue((hash, seq));

        if (_sessions.Count > _maxSessions)
            EnforceSessionCapacity(now);

        return new PlannerIssuedSession(
            Token: rawToken,
            ExpiresAtUtc: expiresAt,
            Username: username ?? string.Empty,
            AccountId: normalizedAid);
    }

    /// <summary>校验编排台 pln_ 会话令牌是否有效且未过期（仅按 SHA-256 哈希比对）。</summary>
    public bool TryValidateSessionToken(string? rawToken, out PlannerAuthPrincipal? principal)
    {
        principal = null;
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        string trimmed = rawToken.Trim();
        if (!trimmed.StartsWith(SessionTokenPrefix, StringComparison.Ordinal))
            return false;

        string hash = ComputeSha256Hex(trimmed);
        if (!_sessions.TryGetValue(hash, out var entry))
            return false;

        DateTime now = _utcNow();
        if (entry.ExpiresAtUtc <= now)
        {
            _sessions.TryRemove(new KeyValuePair<string, SessionEntry>(hash, entry));
            return false;
        }

        principal = new PlannerAuthPrincipal(entry.AccountId, entry.Username, "planner_session");
        return true;
    }

    /// <summary>
    /// 浏览器口令直登：
    /// - 严禁自动建号，严禁自动绑定口令；
    /// - 账号不存在 → 401 BAD_CREDENTIALS；
    /// - 账号未设口令 → 403 PASSWORD_REQUIRED_FOR_WEB_LOGIN；
    /// - 限流命中（同 IP + 账号 10 次/60s）→ 429 RATE_LIMITED；
    /// - 口令错误 → 401 BAD_CREDENTIALS；
    /// - 验证通过 → 签发 4 小时 pln_ 会话令牌。
    /// </summary>
    public PlannerSessionExchangeResult LoginWithPassword(
        AccountStore accounts,
        string? username,
        string? password,
        string? clientIp = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "BAD_CREDENTIALS",
                ErrorMessageZh: "账号名或口令不对",
                Session: null);
        }

        DateTime now = _utcNow();
        string rateKey = BuildRateLimitKey(clientIp, username);
        if (IsRateLimited(rateKey, now))
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status429TooManyRequests,
                ErrorCode: "RATE_LIMITED",
                ErrorMessageZh: "登录尝试过于频繁，请稍后再试",
                Session: null);
        }

        var acc = accounts.FindByUsername(username);
        if (acc == null)
        {
            RecordLoginFailure(rateKey, now);
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "BAD_CREDENTIALS",
                ErrorMessageZh: "账号名或口令不对",
                Session: null);
        }

        if (!acc.HasSecretProtection)
        {
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status403Forbidden,
                ErrorCode: "PASSWORD_REQUIRED_FOR_WEB_LOGIN",
                ErrorMessageZh: "该账号尚未设置口令，请在游戏内点击「打开网页编排台」",
                Session: null);
        }

        if (!AccountStore.VerifySecretHash(password, acc.AuthSecretHash))
        {
            RecordLoginFailure(rateKey, now);
            return new PlannerSessionExchangeResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "BAD_CREDENTIALS",
                ErrorMessageZh: "账号名或口令不对",
                Session: null);
        }

        ClearLoginFailures(rateKey);
        accounts.TouchActivity(acc);

        var session = IssueSession(NormalizeAccountId(acc.AccountId), acc.Username);
        return new PlannerSessionExchangeResult(
            Ok: true,
            StatusCode: StatusCodes.Status200OK,
            ErrorCode: null,
            ErrorMessageZh: null,
            Session: session);
    }

    /// <summary>
    /// 编排台全量业务接口统一鉴权：
    /// 1) 优先校验 X-Planner-Session: pln_...；
    /// 2) 其次校验 Authorization: Bearer ...（若为 pln_ 前缀走网页会话校验，否则走游戏 Bearer 校验）；
    /// 3) 严禁读取 X-Account-Id / aid，严禁降级为 legacy username；
    /// 4) 均缺失或无效时返回 401 AUTH_REQUIRED。
    /// </summary>
    public PlannerAuthResolution ResolveAuth(HttpContext ctx, AccountStore accounts)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(accounts);

        string sessionHeader = ctx.Request.Headers[SessionHeaderName].ToString().Trim();
        if (!string.IsNullOrEmpty(sessionHeader) &&
            TryValidateSessionToken(sessionHeader, out var sessionPrincipal) &&
            sessionPrincipal != null)
        {
            var resolvedAcc = accounts.FindByAccountId(sessionPrincipal.AccountId);
            var enriched = resolvedAcc != null
                ? sessionPrincipal with { Account = resolvedAcc, Username = string.IsNullOrEmpty(sessionPrincipal.Username) ? resolvedAcc.Username : sessionPrincipal.Username }
                : sessionPrincipal;
            return new PlannerAuthResolution(true, enriched, 0, null, null);
        }

        string authHeader = ctx.Request.Headers["Authorization"].ToString().Trim();
        if (!string.IsNullOrEmpty(authHeader))
        {
            string rawToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader[7..].Trim()
                : authHeader;

            if (rawToken.StartsWith(SessionTokenPrefix, StringComparison.Ordinal))
            {
                if (TryValidateSessionToken(rawToken, out var bearerPlnPrincipal) && bearerPlnPrincipal != null)
                {
                    var resolvedAcc = accounts.FindByAccountId(bearerPlnPrincipal.AccountId);
                    var enriched = resolvedAcc != null
                        ? bearerPlnPrincipal with { Account = resolvedAcc, Username = string.IsNullOrEmpty(bearerPlnPrincipal.Username) ? resolvedAcc.Username : bearerPlnPrincipal.Username }
                        : bearerPlnPrincipal;
                    return new PlannerAuthResolution(true, enriched, 0, null, null);
                }

                return new PlannerAuthResolution(
                    IsAuthenticated: false,
                    Principal: null,
                    FailureStatusCode: StatusCodes.Status401Unauthorized,
                    ErrorCode: "AUTH_REQUIRED",
                    ErrorMessageZh: "编排台会话无效或已过期，请重新登录。");
            }

            var bearerRes = AccountAuthResolver.Resolve(
                ctx,
                accounts,
                requireAuth: true,
                allowLegacyUsernameFallback: false);
            if (bearerRes.IsAuthenticated && bearerRes.Account != null)
            {
                var principal = new PlannerAuthPrincipal(
                    AccountId: NormalizeAccountId(bearerRes.Account.AccountId),
                    Username: bearerRes.Account.Username,
                    AuthSource: "bearer",
                    Account: bearerRes.Account);
                return new PlannerAuthResolution(true, principal, 0, null, null);
            }

            return new PlannerAuthResolution(
                IsAuthenticated: false,
                Principal: null,
                FailureStatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "AUTH_REQUIRED",
                ErrorMessageZh: bearerRes.ErrorMessageZh ?? "登录令牌无效或已过期，请重新登录。");
        }

        return new PlannerAuthResolution(
            IsAuthenticated: false,
            Principal: null,
            FailureStatusCode: StatusCodes.Status401Unauthorized,
            ErrorCode: "AUTH_REQUIRED",
            ErrorMessageZh: "请先登录后再访问编排台。");
    }

    /// <summary>
    /// 游戏内换取一次性票据（POST /api/planner/web-ticket）专用鉴权：
    /// 仅认游戏内 Bearer 令牌，不接受网页会话令牌，严禁 legacy username fallback。
    /// </summary>
    public PlannerAuthResolution ResolveBearerOnlyForTicket(HttpContext ctx, AccountStore accounts)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(accounts);

        var res = AccountAuthResolver.Resolve(
            ctx,
            accounts,
            requireAuth: true,
            allowLegacyUsernameFallback: false);
        if (res.IsAuthenticated && res.Account != null)
        {
            var principal = new PlannerAuthPrincipal(
                AccountId: NormalizeAccountId(res.Account.AccountId),
                Username: res.Account.Username,
                AuthSource: "bearer",
                Account: res.Account);
            return new PlannerAuthResolution(true, principal, 0, null, null);
        }

        return new PlannerAuthResolution(
            IsAuthenticated: false,
            Principal: null,
            FailureStatusCode: res.FailureStatusCode > 0 ? res.FailureStatusCode : StatusCodes.Status401Unauthorized,
            ErrorCode: string.IsNullOrWhiteSpace(res.ErrorCode) ? "AUTH_REQUIRED" : res.ErrorCode,
            ErrorMessageZh: res.ErrorMessageZh ?? "请使用游戏内登录令牌换取编排台票据。");
    }

    /// <summary>提取请求客户端 IP（供登录防爆破限流使用）。</summary>
    public static string ResolveClientIp(HttpContext? ctx)
    {
        string? remote = ctx?.Connection?.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(remote) ? "unknown" : remote.Trim();
    }

    /// <summary>清理所有已过期的票据、会话与过期限流桶（供 Program.cs 15 分钟定时维护协程调用）。</summary>
    public int PruneExpired()
    {
        DateTime now = _utcNow();
        int removed = 0;

        foreach (var kv in _tickets)
        {
            if (kv.Value.ExpiresAtUtc <= now && _tickets.TryRemove(kv))
                removed++;
        }

        lock (_ticketEvictLock)
        {
            DrainDeadOrderQueueLocked(_ticketOrder, _tickets, entry => entry.CreatedSeq);
        }

        foreach (var kv in _sessions)
        {
            if (kv.Value.ExpiresAtUtc <= now && _sessions.TryRemove(kv))
                removed++;
        }

        lock (_sessionEvictLock)
        {
            DrainDeadOrderQueueLocked(_sessionOrder, _sessions, entry => entry.CreatedSeq);
        }

        DateTime cutoff = now - _loginFailureWindow;
        foreach (var kv in _loginFailures)
        {
            bool empty;
            lock (kv.Value.Sync)
            {
                while (kv.Value.FailuresUtc.Count > 0 && kv.Value.FailuresUtc.Peek() <= cutoff)
                    kv.Value.FailuresUtc.Dequeue();
                empty = kv.Value.FailuresUtc.Count == 0;
            }
            if (empty && _loginFailures.TryRemove(kv))
                removed++;
        }

        return removed;
    }

    /// <summary>将账号 ID 统一归一化为标准连字符小写 GUID（与 PlayerRuleStore 保持一致）。</summary>
    public static string NormalizeAccountId(Guid accountId) => accountId.ToString("D");

    /// <summary>将账号 ID 字符串统一归一化为标准 GUID（"D"）格式，非 GUID 字符串则去空白保留原串。</summary>
    public static string NormalizeAccountId(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        string trimmed = accountId.Trim();
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed;
    }

    private void EnforceTicketCapacity(DateTime now)
    {
        lock (_ticketEvictLock)
        {
            while (_tickets.Count > _maxTickets && _ticketOrder.TryDequeue(out var oldest))
            {
                if (_tickets.TryGetValue(oldest.Hash, out var cur) && cur.CreatedSeq == oldest.Seq)
                    _tickets.TryRemove(new KeyValuePair<string, TicketEntry>(oldest.Hash, cur));
            }

            if (_tickets.Count > _maxTickets)
            {
                foreach (var kv in _tickets)
                {
                    if (kv.Value.ExpiresAtUtc <= now)
                        _tickets.TryRemove(kv);
                }
            }

            while (_tickets.Count > _maxTickets)
            {
                KeyValuePair<string, TicketEntry>? victim = null;
                foreach (var kv in _tickets)
                {
                    if (victim == null || kv.Value.CreatedSeq < victim.Value.Value.CreatedSeq)
                        victim = kv;
                }
                if (victim == null) break;
                _tickets.TryRemove(victim.Value);
            }
        }
    }

    private void EnforceSessionCapacity(DateTime now)
    {
        lock (_sessionEvictLock)
        {
            while (_sessions.Count > _maxSessions && _sessionOrder.TryDequeue(out var oldest))
            {
                if (_sessions.TryGetValue(oldest.Hash, out var cur) && cur.CreatedSeq == oldest.Seq)
                    _sessions.TryRemove(new KeyValuePair<string, SessionEntry>(oldest.Hash, cur));
            }

            if (_sessions.Count > _maxSessions)
            {
                foreach (var kv in _sessions)
                {
                    if (kv.Value.ExpiresAtUtc <= now)
                        _sessions.TryRemove(kv);
                }
            }

            while (_sessions.Count > _maxSessions)
            {
                KeyValuePair<string, SessionEntry>? victim = null;
                foreach (var kv in _sessions)
                {
                    if (victim == null || kv.Value.CreatedSeq < victim.Value.Value.CreatedSeq)
                        victim = kv;
                }
                if (victim == null) break;
                _sessions.TryRemove(victim.Value);
            }
        }
    }

    private static void DrainDeadOrderQueueLocked<TEntry>(
        ConcurrentQueue<(string Hash, long Seq)> orderQueue,
        ConcurrentDictionary<string, TEntry> table,
        Func<TEntry, long> getSeq)
    {
        while (orderQueue.TryPeek(out var head))
        {
            if (table.TryGetValue(head.Hash, out var cur) && getSeq(cur) == head.Seq)
                break;
            orderQueue.TryDequeue(out _);
        }
    }

    private string BuildRateLimitKey(string? clientIp, string username)
    {
        string ip = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp.Trim();
        string normUser = AccountStore.NormalizeName(username).ToLowerInvariant();
        return $"{ip}|{normUser}";
    }

    private bool IsRateLimited(string rateKey, DateTime now)
    {
        if (!_loginFailures.TryGetValue(rateKey, out var bucket))
            return false;

        DateTime cutoff = now - _loginFailureWindow;
        lock (bucket.Sync)
        {
            while (bucket.FailuresUtc.Count > 0 && bucket.FailuresUtc.Peek() <= cutoff)
                bucket.FailuresUtc.Dequeue();
            return bucket.FailuresUtc.Count >= _maxLoginFailures;
        }
    }

    private void RecordLoginFailure(string rateKey, DateTime now)
    {
        if (_loginFailures.Count >= MaxRateLimitBuckets && !_loginFailures.ContainsKey(rateKey))
        {
            PruneStaleRateLimitBuckets(now);
            if (_loginFailures.Count >= MaxRateLimitBuckets)
            {
                // 极端爆破洪水下丢弃一批最先枚举到的桶以兜底内存安全
                int dropped = 0;
                foreach (var k in _loginFailures.Keys)
                {
                    if (_loginFailures.TryRemove(k, out _) && ++dropped >= 1024)
                        break;
                }
            }
        }

        var bucket = _loginFailures.GetOrAdd(rateKey, _ => new LoginFailureBucket());
        DateTime cutoff = now - _loginFailureWindow;
        lock (bucket.Sync)
        {
            while (bucket.FailuresUtc.Count > 0 && bucket.FailuresUtc.Peek() <= cutoff)
                bucket.FailuresUtc.Dequeue();
            bucket.FailuresUtc.Enqueue(now);
        }
    }

    private void ClearLoginFailures(string rateKey)
    {
        _loginFailures.TryRemove(rateKey, out _);
    }

    private void PruneStaleRateLimitBuckets(DateTime now)
    {
        DateTime cutoff = now - _loginFailureWindow;
        foreach (var kv in _loginFailures)
        {
            bool empty;
            lock (kv.Value.Sync)
            {
                while (kv.Value.FailuresUtc.Count > 0 && kv.Value.FailuresUtc.Peek() <= cutoff)
                    kv.Value.FailuresUtc.Dequeue();
                empty = kv.Value.FailuresUtc.Count == 0;
            }
            if (empty)
                _loginFailures.TryRemove(kv);
        }
    }

    private static string GenerateBase64Url256Bit()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
