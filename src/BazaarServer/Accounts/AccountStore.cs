using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Accounts;

/// <summary>
/// 账号、多端持久化令牌与英雄生涯战绩存储：
/// - 令牌持久化至 SQLite account_tokens（仅存 SHA-256 哈希），支持同一账号最多 5 端并发登录不互踢、重启 100% 存活、严格过期与类型校验；
/// - 支持可选账号口令/设备密钥保护（PBKDF2-SHA256 盐值哈希）；
/// - 按 AccountId 提供 O(1) 哈希检索，并将高频局内进度写入改为 3 秒防抖合批落盘以消除 SQLite 锁争抢。
/// </summary>
public sealed class AccountStore : IDisposable
{
    public const int MaxActiveTokensPerType = 5;
    public const int AccountProgressSaveDebounceMs = 3000;
    private const int Pbkdf2Iterations = 60_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex TmpTagRegex = new(@"<[^>]{0,128}>", RegexOptions.Compiled);

    public static readonly string[] KnownPlayableHeroes =
    [
        "Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle", "Jules", "Karnok", "Hero8",
    ];

    public sealed record IssuedTokenPair(
        string AccessToken,
        string RefreshToken,
        DateTime AccessExpiresAt,
        DateTime RefreshExpiresAt,
        string ClientKind);

    private sealed record CachedToken(
        string TokenHash,
        Guid AccountId,
        string TokenType,
        string ClientKind,
        DateTime ExpiresAt,
        bool IsRevoked);

    private readonly Persistence _db;
    private readonly ConcurrentDictionary<string, Account> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Account> _byAccountId = new();
    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Account> _dirtyAccounts = new();
    private readonly object _lock = new();
    private readonly Timer _accountSaveTimer;
    private int _disposed;

    public AccountStore(Persistence db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accountSaveTimer = new Timer(OnAccountSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
        _db.RegisterFlushCallback(FlushPendingAccountSaves);
    }

    public sealed class HeroCareerStats
    {
        public string Hero { get; set; } = "";
        public int GamesPlayed { get; set; }
        public int RankedGamesPlayed { get; set; }
        public int PlayerVictories { get; set; }
        public int MonsterVictories { get; set; }
        public int MonsterDefeats { get; set; }
        public int RunsWon { get; set; }
        public int RunsLost { get; set; }
        public uint BestDay { get; set; }
        public int DaysPlayed { get; set; }
        public int EventsCompleted { get; set; }
        public int MerchantsVisited { get; set; }
        public int CoinsSpent { get; set; }
        public int LivesLost { get; set; }
        public int SecondsSpentPlaying { get; set; }
        public int ItemsSold { get; set; }
        public long DamageDealt { get; set; }
        public int ChestsEarned { get; set; }
        public int LegendaryItemsAcquired { get; set; }
        public int GhostBattles { get; set; }
        public int GhostVictories { get; set; }
        public int GhostRunsEnded { get; set; }
        public int BronzeTrophies { get; set; }
        public int SilverTrophies { get; set; }
        public int GoldTrophies { get; set; }
        public int DiamondTrophies { get; set; }
        public int TenWinTrophies { get; set; }
    }

    public sealed class HeroLoadoutState
    {
        public string HeroId { get; set; } = "";
        public string BoardId { get; set; } = "";
        public string CarpetId { get; set; } = "";
        public string CardBackId { get; set; } = "";
        public string ToyId { get; set; } = "";
        public string StashId { get; set; } = "";
        public string BankId { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string HeroSkinId { get; set; } = "";
        public List<string> CardSkinIds { get; set; } = new();
        public bool RandomizeLoadout { get; set; }

        public HeroLoadoutState Clone() => new()
        {
            HeroId = HeroId,
            BoardId = BoardId,
            CarpetId = CarpetId,
            CardBackId = CardBackId,
            ToyId = ToyId,
            StashId = StashId,
            BankId = BankId,
            AlbumId = AlbumId,
            HeroSkinId = HeroSkinId,
            CardSkinIds = CardSkinIds != null ? new List<string>(CardSkinIds) : new List<string>(),
            RandomizeLoadout = RandomizeLoadout,
        };
    }

    public sealed record ChestOpenDraw(
        Guid CollectibleGuid,
        string NormalizedId,
        bool IsCardSkin,
        bool IsDuplicate,
        int GemsAwarded);

    public sealed class CareerSnapshotDto
    {
        public Dictionary<string, HeroCareerStats> Heroes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RecordedRunIds { get; set; } = new();
        public Dictionary<string, HeroLoadoutState> HeroLoadouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int? UnopenedChestCount { get; set; }
        public List<string> OpenedCardSkinIds { get; set; } = new();
        public int? Gems { get; set; }
    }

    public sealed class Account
    {
        public Guid AccountId { get; init; }
        public string Username { get; init; } = "";
        public string IdentityKey { get; init; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime AccessExpiresAt { get; set; }
        public DateTime RefreshExpiresAt { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
        public string AuthSecretHash { get; set; } = "";
        public string Role { get; set; } = "player";
        public int LastPingMs { get; set; }
        public int RunsCompleted { get; set; }
        public int Victories { get; set; }
        public int Defeats { get; set; }
        public uint BestDay { get; set; }
        public int UnopenedChestCount { get; set; } = 30;
        public int Gems { get; set; } = 999999;
        public Dictionary<string, HeroCareerStats> HeroStats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RecordedRunIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HeroLoadoutState> HeroLoadouts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> OpenedCardSkinIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasSecretProtection => !string.IsNullOrWhiteSpace(AuthSecretHash);
    }

    /// <summary>按 Steam 票据建号/登录（旧路径，支持多端并发不互踢）。</summary>
    public Account GetOrCreateBySteamTicket(string ticketHex, string clientKind = "game", string deviceId = "")
        => GetOrCreate("steam:" + Fingerprint(ticketHex), DeriveNameFromFingerprint(ticketHex), clientKind, deviceId);

    /// <summary>按用户名建号/登录（新路径：输入名字即登录，支持多端并发不互踢）。</summary>
    public Account GetOrCreateByName(string username, string clientKind = "game", string deviceId = "")
    {
        string clean = NormalizeName(username);
        return GetOrCreate("name:" + clean.ToLowerInvariant(), clean, clientKind, deviceId);
    }

    /// <summary>
    /// 按用户名登录并校验/绑定可选口令或设备密钥：
    /// - 若账号尚未设置口令且客户端传入了 secret，自动首次绑定（平滑升级老账号）；
    /// - 若账号已设置口令，必须提供匹配口令方可通过，杜绝同名抢号。
    /// </summary>
    public (bool Ok, Account? Account, IssuedTokenPair? Tokens, string? ErrorZh) LoginByNameWithSecret(
        string username,
        string? secret,
        string clientKind = "game",
        string deviceId = "")
    {
        string clean = NormalizeName(username);
        string key = "name:" + clean.ToLowerInvariant();
        Account acc;
        bool needSave = false;

        lock (_lock)
        {
            acc = EnsureAccountLoadedLocked(key, clean, out bool isNew);
            if (isNew) needSave = true;

            if (acc.HasSecretProtection)
            {
                if (string.IsNullOrWhiteSpace(secret) || !VerifySecretHash(secret, acc.AuthSecretHash))
                {
                    return (false, null, null, "账号口令校验失败，请输入正确的登录口令。");
                }
            }
            else if (!string.IsNullOrWhiteSpace(secret))
            {
                string trimmed = secret.Trim();
                if (trimmed.Length < 4 || trimmed.Length > 128)
                    return (false, null, null, "账号口令长度需在 4 到 128 个字符之间。");

                acc.AuthSecretHash = HashSecret(trimmed);
                needSave = true;
            }
            acc.LastActiveUtc = DateTime.UtcNow;
        }

        if (needSave)
            SaveAccount(acc);

        var pair = IssuePersistentTokens(acc, clientKind, deviceId);
        return (true, acc, pair, null);
    }

    /// <summary>为已登录账号设置或修改登录口令保护。</summary>
    public (bool Ok, string? ErrorZh) SetOrUpdateSecret(Account acc, string? currentSecret, string newSecret)
    {
        if (acc == null) return (false, "账号不存在。");
        string trimmedNew = (newSecret ?? "").Trim();
        if (trimmedNew.Length < 4 || trimmedNew.Length > 128)
            return (false, "新口令长度必须在 4 到 128 个字符之间。");

        string newHash;
        lock (_lock)
        {
            if (acc.HasSecretProtection)
            {
                if (string.IsNullOrWhiteSpace(currentSecret) || !VerifySecretHash(currentSecret, acc.AuthSecretHash))
                    return (false, "原口令不正确，无法修改。");
            }
            newHash = HashSecret(trimmedNew);
            acc.AuthSecretHash = newHash;
        }

        _db.UpdateAccountSecretHash(acc.AccountId, newHash);
        return (true, null);
    }

    private Account GetOrCreate(string key, string username, string clientKind, string deviceId)
    {
        Account acc;
        bool isNew;
        lock (_lock)
        {
            acc = EnsureAccountLoadedLocked(key, username, out isNew);
            acc.LastActiveUtc = DateTime.UtcNow;
        }
        if (isNew)
            SaveAccount(acc);

        IssuePersistentTokens(acc, clientKind, deviceId);
        return acc;
    }

    private Account EnsureAccountLoadedLocked(string key, string username, out bool isNew)
    {
        isNew = false;
        if (_byKey.TryGetValue(key, out var existing))
        {
            _byAccountId[existing.AccountId] = existing;
            return existing;
        }

        var row = _db.LoadAccount(key);
        Account acc;
        if (row != null)
        {
            acc = AccountFromRow(row);
        }
        else
        {
            isNew = true;
            acc = new Account
            {
                AccountId = GuidFromHash("acc:" + key),
                Username = username,
                IdentityKey = key,
            };
        }
        _byKey[key] = acc;
        _byAccountId[acc.AccountId] = acc;
        return acc;
    }

    private static Account AccountFromRow(Persistence.AccountRow row)
    {
        var acc = new Account
        {
            AccountId = row.AccountId,
            Username = row.Username,
            IdentityKey = row.IdentityKey,
            CreatedAt = row.CreatedAt,
            LastActiveUtc = row.LastSeen,
            AuthSecretHash = row.AuthSecretHash ?? "",
            Role = string.IsNullOrWhiteSpace(row.Role) ? "player" : row.Role,
            LastPingMs = row.LastPingMs,
            RunsCompleted = row.RunsCompleted,
            Victories = row.Victories,
            Defeats = row.Defeats,
            BestDay = row.BestDay,
        };
        LoadCareerJsonIntoAccount(acc, row.CareerJson);
        return acc;
    }

    private static void LoadCareerJsonIntoAccount(Account acc, string? careerJson)
    {
        if (string.IsNullOrWhiteSpace(careerJson)) return;
        try
        {
            var snap = JsonSerializer.Deserialize<CareerSnapshotDto>(careerJson, JsonOpts);
            if (snap == null) return;
            if (snap.Heroes != null)
            {
                foreach (var kv in snap.Heroes)
                {
                    if (kv.Value == null) continue;
                    kv.Value.Hero = string.IsNullOrEmpty(kv.Value.Hero) ? kv.Key : kv.Value.Hero;
                    acc.HeroStats[kv.Key] = kv.Value;
                }
            }
            if (snap.RecordedRunIds != null)
            {
                foreach (string rid in snap.RecordedRunIds)
                {
                    if (!string.IsNullOrWhiteSpace(rid)) acc.RecordedRunIds.Add(rid);
                }
            }
            if (snap.HeroLoadouts != null)
            {
                foreach (var kv in snap.HeroLoadouts)
                {
                    if (kv.Value == null) continue;
                    var cloned = kv.Value.Clone();
                    cloned.HeroId = string.IsNullOrEmpty(cloned.HeroId) ? kv.Key : cloned.HeroId;
                    acc.HeroLoadouts[kv.Key] = cloned;
                }
            }
            if (snap.UnopenedChestCount.HasValue)
            {
                acc.UnopenedChestCount = Math.Max(0, snap.UnopenedChestCount.Value);
            }
            if (snap.OpenedCardSkinIds != null)
            {
                foreach (string sid in snap.OpenedCardSkinIds)
                {
                    string norm = NormalizeCollectibleId(sid);
                    if (!string.IsNullOrEmpty(norm)) acc.OpenedCardSkinIds.Add(norm);
                }
            }
            if (snap.Gems.HasValue)
            {
                acc.Gems = Math.Max(0, snap.Gems.Value);
            }
        }
        catch { }
    }

    private static string SerializeCareerJson(Account acc)
    {
        var dto = new CareerSnapshotDto
        {
            Heroes = acc.HeroStats,
            RecordedRunIds = acc.RecordedRunIds.TakeLast(500).ToList(),
            HeroLoadouts = acc.HeroLoadouts,
            UnopenedChestCount = acc.UnopenedChestCount,
            OpenedCardSkinIds = acc.OpenedCardSkinIds.ToList(),
            Gems = acc.Gems,
        };
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    // ── 多端持久化 Token 管理 ────────────────────────────────────────────────

    /// <summary>
    /// 签发新的持久化令牌对并落盘至 account_tokens。
    /// 同一账号允许最多 5 端并存活跃令牌不互踢，超出上限时仅淘汰最旧 1 枚。
    /// </summary>
    public IssuedTokenPair IssuePersistentTokens(Account acc, string clientKind = "game", string deviceId = "")
    {
        DateTime now = DateTime.UtcNow;
        string kind = NormalizeClientKind(clientKind);
        string dev = (deviceId ?? "").Trim();
        if (dev.Length > 64) dev = dev[..64];

        string access = "acc_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        string refresh = "ref_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        DateTime accessExp = now.AddHours(24);
        DateTime refreshExp = now.AddDays(30);

        string accessHash = Fingerprint(access);
        string refreshHash = Fingerprint(refresh);

        _db.UpsertAccountToken(new Persistence.AccountTokenRow(
            accessHash, acc.AccountId, "access", kind, dev, now, accessExp, null));
        _db.UpsertAccountToken(new Persistence.AccountTokenRow(
            refreshHash, acc.AccountId, "refresh", kind, dev, now, refreshExp, null));

        // 限制多端活跃令牌上限为 5，不互踢现有并存端
        var trimmedAccess = _db.TrimExcessAccountTokens(acc.AccountId, "access", MaxActiveTokensPerType, now);
        var trimmedRefresh = _db.TrimExcessAccountTokens(acc.AccountId, "refresh", MaxActiveTokensPerType, now);
        foreach (string oldHash in trimmedAccess) _tokenCache.TryRemove(oldHash, out _);
        foreach (string oldHash in trimmedRefresh) _tokenCache.TryRemove(oldHash, out _);

        _tokenCache[accessHash] = new CachedToken(accessHash, acc.AccountId, "access", kind, accessExp, false);
        _tokenCache[refreshHash] = new CachedToken(refreshHash, acc.AccountId, "refresh", kind, refreshExp, false);

        lock (_lock)
        {
            acc.AccessToken = access;
            acc.RefreshToken = refresh;
            acc.AccessExpiresAt = accessExp;
            acc.RefreshExpiresAt = refreshExp;
            acc.LastActiveUtc = now;
        }

        return new IssuedTokenPair(access, refresh, accessExp, refreshExp, kind);
    }

    /// <summary>
    /// 校验 Access Token（仅接受未吊销且未过期的 access 类型令牌；内存未命中时自动回查 SQLite 保证重启 100% 存活）。
    /// </summary>
    public Account? FindByAccessToken(string? token)
    {
        string raw = StripBearerPrefix(token);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var entry = ResolveTokenEntry(raw);
        if (entry == null || entry.IsRevoked) return null;
        if (!string.Equals(entry.TokenType, "access", StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.ExpiresAt <= DateTime.UtcNow)
        {
            _tokenCache.TryRemove(entry.TokenHash, out _);
            return null;
        }

        var acc = ResolveAccountByGuid(entry.AccountId);
        if (acc != null)
            acc.LastActiveUtc = DateTime.UtcNow;
        return acc;
    }

    /// <summary>
    /// 使用 Refresh Token（兼容旧客户端传有效 Access Token）轮换签发全新令牌对并落盘，同时吊销旧 Refresh Token。
    /// </summary>
    public (bool Ok, Account? Account, IssuedTokenPair? Tokens, string? ErrorZh) TryRefreshTokens(
        string? rawToken,
        string clientKind = "game",
        string deviceId = "")
    {
        string clean = StripBearerPrefix(rawToken);
        if (string.IsNullOrWhiteSpace(clean))
            return (false, null, null, "缺少刷新令牌，请重新登录。");

        var entry = ResolveTokenEntry(clean);
        if (entry == null || entry.IsRevoked || entry.ExpiresAt <= DateTime.UtcNow)
            return (false, null, null, "刷新令牌无效或已过期，请重新登录。");

        var acc = ResolveAccountByGuid(entry.AccountId);
        if (acc == null)
            return (false, null, null, "关联账号不存在，请重新登录。");

        string effectiveKind = string.IsNullOrWhiteSpace(clientKind) ? entry.ClientKind : clientKind;
        var nextPair = IssuePersistentTokens(acc, effectiveKind, deviceId);

        // 若使用 refresh 令牌刷新，则安全轮换吊销旧 refresh 令牌，防止重放
        if (string.Equals(entry.TokenType, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            RevokeTokenHash(entry.TokenHash);
        }

        return (true, acc, nextPair, null);
    }

    /// <summary>主动吊销指定明文令牌（用于登出）。</summary>
    public bool RevokeToken(string? rawToken)
    {
        string clean = StripBearerPrefix(rawToken);
        if (string.IsNullOrWhiteSpace(clean)) return false;
        string hash = Fingerprint(clean);
        RevokeTokenHash(hash);
        return true;
    }

    private void RevokeTokenHash(string hash)
    {
        _tokenCache.TryRemove(hash, out _);
        try { _db.RevokeAccountToken(hash); } catch { }
    }

    private CachedToken? ResolveTokenEntry(string rawToken)
    {
        string hash = Fingerprint(rawToken);
        if (_tokenCache.TryGetValue(hash, out var cached))
            return cached;

        var row = _db.LoadAccountTokenByHash(hash);
        if (row == null) return null;

        bool revoked = row.RevokedAt.HasValue;
        var entry = new CachedToken(row.TokenHash, row.AccountId, row.TokenType, row.ClientKind, row.ExpiresAt, revoked);
        if (!revoked && row.ExpiresAt > DateTime.UtcNow)
            _tokenCache[hash] = entry;
        return entry;
    }

    private Account? ResolveAccountByGuid(Guid accountId)
    {
        if (accountId == Guid.Empty) return null;
        if (_byAccountId.TryGetValue(accountId, out var acc))
            return acc;

        lock (_lock)
        {
            if (_byAccountId.TryGetValue(accountId, out acc))
                return acc;

            var row = _db.LoadAccountById(accountId);
            if (row == null) return null;
            acc = AccountFromRow(row);
            _byKey[acc.IdentityKey] = acc;
            _byAccountId[acc.AccountId] = acc;
            return acc;
        }
    }

    public Account? FindByAccountId(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        if (Guid.TryParse(accountId.Trim(), out var guid))
            return ResolveAccountByGuid(guid);

        string norm = accountId.Replace("-", "").Trim();
        if (norm.Length == 32 && Guid.TryParseExact(norm, "N", out guid))
            return ResolveAccountByGuid(guid);

        return null;
    }

    public Account? FindByUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        string key = "name:" + NormalizeName(username).ToLowerInvariant();
        if (_byKey.TryGetValue(key, out var acc)) return acc;

        var row = _db.LoadAccount(key);
        if (row == null) return null;
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out acc)) return acc;
            acc = AccountFromRow(row);
            _byKey[key] = acc;
            _byAccountId[acc.AccountId] = acc;
            return acc;
        }
    }

    public IEnumerable<Account> All => _byKey.Values;

    /// <summary>记录玩家在线心跳与最近测速 RTT。</summary>
    public void TouchActivity(Account acc, int? pingMs = null)
    {
        if (acc == null) return;
        acc.LastActiveUtc = DateTime.UtcNow;
        if (pingMs.HasValue && pingMs.Value >= 0)
        {
            acc.LastPingMs = Math.Clamp(pingMs.Value, 0, 60000);
        }
    }

    /// <summary>判断账号近期是否在线（默认 3 分钟内有请求/心跳即视为在线）。</summary>
    public bool IsRecentlyOnline(Account acc, TimeSpan? window = null)
    {
        if (acc == null) return false;
        return DateTime.UtcNow - acc.LastActiveUtc <= (window ?? TimeSpan.FromMinutes(3));
    }

    /// <summary>统计当前在线玩家总数。</summary>
    public int CountOnlinePlayers(TimeSpan? window = null)
    {
        DateTime cutoff = DateTime.UtcNow - (window ?? TimeSpan.FromMinutes(3));
        int count = 0;
        foreach (var acc in _byKey.Values)
        {
            if (acc.LastActiveUtc >= cutoff) count++;
        }
        return count;
    }

    /// <summary>从 SQLite 加载全部账号、生涯战绩与有效持久化令牌到内存（服务端启动时调用）。</summary>
    public void LoadAllFromDb()
    {
        try
        {
            var rows = _db.LoadAllAccounts();
            foreach (var row in rows)
            {
                if (_byKey.ContainsKey(row.IdentityKey)) continue;
                var acc = AccountFromRow(row);
                _byKey[row.IdentityKey] = acc;
                _byAccountId[acc.AccountId] = acc;
            }

            var tokens = _db.LoadActiveAccountTokens();
            foreach (var t in tokens)
            {
                _tokenCache[t.TokenHash] = new CachedToken(
                    t.TokenHash, t.AccountId, t.TokenType, t.ClientKind, t.ExpiresAt, false);
            }
            Console.WriteLine($"[AccountStore] 已加载 {_byKey.Count} 个账号与 {tokens.Count} 枚活跃持久化令牌");
        }
        catch (Exception ex) { Console.WriteLine("[AccountStore] LoadAllFromDb 失败: " + ex.Message); }
    }

    /// <summary>立即将账号与生涯战绩同步写回 SQLite（并移除挂起防抖项）。</summary>
    public void SaveAccount(Account acc)
    {
        if (acc == null) return;
        _dirtyAccounts.TryRemove(acc.AccountId, out _);
        SaveAccountCore(acc);
    }

    /// <summary>将所有挂起防抖的账号进度立即同步落盘（优雅停机调用）。</summary>
    public void FlushPendingAccountSaves()
    {
        var toFlush = _dirtyAccounts.Keys.ToList();
        foreach (Guid id in toFlush)
        {
            if (_dirtyAccounts.TryRemove(id, out var acc))
                SaveAccountCore(acc);
        }
    }

    private void ScheduleAccountSaveDebounced(Account acc)
    {
        if (acc == null) return;
        if (_disposed != 0)
        {
            SaveAccountCore(acc);
            return;
        }
        _dirtyAccounts[acc.AccountId] = acc;
        try { _accountSaveTimer.Change(AccountProgressSaveDebounceMs, Timeout.Infinite); } catch { }
    }

    private void OnAccountSaveTimer(object? _)
    {
        if (_disposed != 0) return;
        FlushPendingAccountSaves();
    }

    private void SaveAccountCore(Account acc)
    {
        try
        {
            string careerJson;
            string secretHash;
            string role;
            int pingMs;
            int runsCompleted, victories, defeats;
            uint bestDay;
            lock (_lock)
            {
                careerJson = SerializeCareerJson(acc);
                secretHash = acc.AuthSecretHash;
                role = acc.Role;
                pingMs = acc.LastPingMs;
                runsCompleted = acc.RunsCompleted;
                victories = acc.Victories;
                defeats = acc.Defeats;
                bestDay = acc.BestDay;
            }
            _db.SaveAccount(new Persistence.AccountRow(
                acc.AccountId, acc.Username, acc.IdentityKey,
                runsCompleted, victories, defeats, bestDay,
                acc.CreatedAt, DateTime.UtcNow, careerJson,
                secretHash, role, pingMs));
        }
        catch { /* 持久化失败不中断玩家当前操作 */ }
    }

    /// <summary>终局或投降结算生涯战绩（按 RunId 幂等，不会重复累计）。</summary>
    public bool RecordRunCompleted(Account acc, RunState run, bool isConceded = false, int elapsedSeconds = 0)
    {
        if (acc == null || run == null) return false;
        int prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20);
        return RecordRunCompleted(
            acc, run.RunId, run.Hero, run.PlayMode, run.Day,
            run.Victories, run.Defeats, prestige, run.State, isConceded, elapsedSeconds);
    }

    /// <summary>终局或投降结算生涯战绩（支持直接从持久化行数据结算）。</summary>
    public bool RecordRunCompleted(
        Account acc,
        string runId,
        EHero hero,
        EPlayMode playMode,
        uint day,
        uint victories,
        uint defeats,
        int prestige,
        ERunState endState,
        bool isConceded = false,
        int elapsedSeconds = 0)
    {
        if (acc == null || string.IsNullOrWhiteSpace(runId)) return false;

        var dbRun = _db.LoadRun(runId);
        if (dbRun != null && !_db.TryMarkCareerRecorded(runId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!acc.RecordedRunIds.Add(runId))
                return false;

            acc.RunsCompleted++;
            acc.Victories += (int)victories;
            acc.Defeats += (int)defeats;
            if (day > acc.BestDay) acc.BestDay = day;

            string heroKey = hero == EHero.Common ? "Vanessa" : hero.ToString();
            var stats = GetOrCreateHeroStatsLocked(acc, heroKey);
            stats.GamesPlayed++;
            if (playMode == EPlayMode.Ranked) stats.RankedGamesPlayed++;
            stats.PlayerVictories += (int)victories;
            stats.LivesLost += (int)defeats;
            stats.DaysPlayed += (int)Math.Max(1u, day);
            if (day > stats.BestDay) stats.BestDay = day;
            if (elapsedSeconds > 0) stats.SecondsSpentPlaying += elapsedSeconds;

            bool wonRun = !isConceded && (endState == ERunState.EndRunVictory || victories >= 10);
            int rewardChests = Game.RunEngine.ComputeRunChestCount(playMode, (int)victories, isConceded);
            if (rewardChests > 0)
            {
                stats.ChestsEarned += rewardChests;
            }
            if (wonRun)
            {
                stats.RunsWon++;
                stats.TenWinTrophies++;
                if (prestige >= 20 && defeats == 0)
                    stats.DiamondTrophies++;
                else
                    stats.GoldTrophies++;
            }
            else
            {
                stats.RunsLost++;
                if (victories >= 7)
                    stats.SilverTrophies++;
                else if (victories >= 4)
                    stats.BronzeTrophies++;
            }
        }

        SaveAccount(acc);
        return true;
    }

    /// <summary>局内增量统计（内存即时累加 + 3 秒防抖落盘，消除高频买卖卡写锁争抢）。</summary>
    public void RecordInRunProgress(
        Account acc,
        EHero hero,
        int monsterVictoriesDelta = 0,
        int monsterDefeatsDelta = 0,
        int itemsSoldDelta = 0,
        int coinsSpentDelta = 0,
        int eventsCompletedDelta = 0,
        int merchantsVisitedDelta = 0,
        long damageDealtDelta = 0,
        int legendaryAcquiredDelta = 0)
    {
        if (acc == null) return;
        string heroKey = hero == EHero.Common ? "Vanessa" : hero.ToString();

        lock (_lock)
        {
            var stats = GetOrCreateHeroStatsLocked(acc, heroKey);
            stats.MonsterVictories += monsterVictoriesDelta;
            stats.MonsterDefeats += monsterDefeatsDelta;
            stats.ItemsSold += itemsSoldDelta;
            stats.CoinsSpent += coinsSpentDelta;
            stats.EventsCompleted += eventsCompletedDelta;
            stats.MerchantsVisited += merchantsVisitedDelta;
            stats.DamageDealt += damageDealtDelta;
            stats.LegendaryItemsAcquired += legendaryAcquiredDelta;
            acc.LastActiveUtc = DateTime.UtcNow;
        }

        ScheduleAccountSaveDebounced(acc);
    }

    /// <summary>返回全量英雄的真实生涯统计快照（供 /api/PlayerProfiles/me/career 使用）。</summary>
    public List<HeroCareerStats> GetAllHeroCareerStats(Account acc)
    {
        lock (_lock)
        {
            var result = new List<HeroCareerStats>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string h in KnownPlayableHeroes)
            {
                seen.Add(h);
                result.Add(CloneStats(GetOrCreateHeroStatsLocked(acc, h)));
            }

            foreach (var kv in acc.HeroStats)
            {
                if (seen.Add(kv.Key))
                    result.Add(CloneStats(kv.Value));
            }
            return result;
        }
    }

    private static HeroCareerStats GetOrCreateHeroStatsLocked(Account acc, string heroKey)
    {
        if (!acc.HeroStats.TryGetValue(heroKey, out var stats))
        {
            stats = new HeroCareerStats { Hero = heroKey };
            acc.HeroStats[heroKey] = stats;
        }
        return stats;
    }

    private static HeroCareerStats CloneStats(HeroCareerStats s) => new()
    {
        Hero = s.Hero,
        GamesPlayed = s.GamesPlayed,
        RankedGamesPlayed = s.RankedGamesPlayed,
        PlayerVictories = s.PlayerVictories,
        MonsterVictories = s.MonsterVictories,
        MonsterDefeats = s.MonsterDefeats,
        RunsWon = s.RunsWon,
        RunsLost = s.RunsLost,
        BestDay = s.BestDay,
        DaysPlayed = s.DaysPlayed,
        EventsCompleted = s.EventsCompleted,
        MerchantsVisited = s.MerchantsVisited,
        CoinsSpent = s.CoinsSpent,
        LivesLost = s.LivesLost,
        SecondsSpentPlaying = s.SecondsSpentPlaying,
        ItemsSold = s.ItemsSold,
        DamageDealt = s.DamageDealt,
        ChestsEarned = s.ChestsEarned,
        LegendaryItemsAcquired = s.LegendaryItemsAcquired,
        GhostBattles = s.GhostBattles,
        GhostVictories = s.GhostVictories,
        GhostRunsEnded = s.GhostRunsEnded,
        BronzeTrophies = s.BronzeTrophies,
        SilverTrophies = s.SilverTrophies,
        GoldTrophies = s.GoldTrophies,
        DiamondTrophies = s.DiamondTrophies,
        TenWinTrophies = s.TenWinTrophies,
    };

    public int Count => _byKey.Count;

    // ── 英雄配装、卡牌皮肤与宝箱库存持久化 ────────────────────────────────

    public static string NormalizeCollectibleId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (Guid.TryParse(id.Trim(), out var g)) return g.ToString("N");
        return id.Trim().Replace("-", "").ToLowerInvariant();
    }

    public HeroLoadoutState GetHeroLoadout(Account acc, string heroId)
    {
        string key = string.IsNullOrWhiteSpace(heroId) ? "Vanessa" : heroId.Trim();
        lock (_lock)
        {
            return GetOrCreateHeroLoadoutLocked(acc, key).Clone();
        }
    }

    public List<HeroLoadoutState> GetAllHeroLoadouts(Account acc, IEnumerable<string> heroIds)
    {
        lock (_lock)
        {
            var list = new List<HeroLoadoutState>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string h in heroIds)
            {
                if (string.IsNullOrWhiteSpace(h) || !seen.Add(h)) continue;
                list.Add(GetOrCreateHeroLoadoutLocked(acc, h).Clone());
            }
            foreach (var kv in acc.HeroLoadouts)
            {
                if (seen.Add(kv.Key))
                {
                    list.Add(kv.Value.Clone());
                }
            }
            return list;
        }
    }

    public HeroLoadoutState EquipCollectible(Account acc, string heroId, string collectibleId, string? collectibleType)
    {
        string key = string.IsNullOrWhiteSpace(heroId) ? "Vanessa" : heroId.Trim();
        string normId = NormalizeCollectibleId(collectibleId);
        HeroLoadoutState result;
        lock (_lock)
        {
            var loadout = GetOrCreateHeroLoadoutLocked(acc, key);
            if (!string.IsNullOrEmpty(normId))
            {
                switch ((collectibleType ?? "").Trim().ToLowerInvariant())
                {
                    case "hero_skin":
                    case "heroskin":
                    case "heroskins":
                        loadout.HeroSkinId = normId;
                        break;
                    case "board":
                    case "boards":
                        loadout.BoardId = normId;
                        break;
                    case "carpet":
                    case "carpets":
                        loadout.CarpetId = normId;
                        break;
                    case "card_back":
                    case "cardback":
                    case "cardbacks":
                        loadout.CardBackId = normId;
                        break;
                    case "toy":
                    case "toys":
                        loadout.ToyId = normId;
                        break;
                    case "stash":
                    case "stashes":
                        loadout.StashId = normId;
                        break;
                    case "bank":
                    case "banks":
                        loadout.BankId = normId;
                        break;
                    case "album":
                    case "albums":
                        loadout.AlbumId = normId;
                        break;
                    case "card_skin":
                    case "cardskin":
                    case "cardskins":
                        loadout.CardSkinIds ??= new List<string>();
                        if (!loadout.CardSkinIds.Contains(normId, StringComparer.OrdinalIgnoreCase))
                        {
                            loadout.CardSkinIds.Add(normId);
                        }
                        break;
                }
            }
            result = loadout.Clone();
        }
        SaveAccount(acc);
        return result;
    }

    public HeroLoadoutState UnequipCollectible(Account acc, string heroId, string collectibleId)
    {
        string key = string.IsNullOrWhiteSpace(heroId) ? "Vanessa" : heroId.Trim();
        string normId = NormalizeCollectibleId(collectibleId);
        HeroLoadoutState result;
        lock (_lock)
        {
            var loadout = GetOrCreateHeroLoadoutLocked(acc, key);
            if (!string.IsNullOrEmpty(normId))
            {
                if (string.Equals(loadout.HeroSkinId, normId, StringComparison.OrdinalIgnoreCase)) loadout.HeroSkinId = "";
                if (string.Equals(loadout.BoardId, normId, StringComparison.OrdinalIgnoreCase)) loadout.BoardId = "";
                if (string.Equals(loadout.CarpetId, normId, StringComparison.OrdinalIgnoreCase)) loadout.CarpetId = "";
                if (string.Equals(loadout.CardBackId, normId, StringComparison.OrdinalIgnoreCase)) loadout.CardBackId = "";
                if (string.Equals(loadout.ToyId, normId, StringComparison.OrdinalIgnoreCase)) loadout.ToyId = "";
                if (string.Equals(loadout.StashId, normId, StringComparison.OrdinalIgnoreCase)) loadout.StashId = "";
                if (string.Equals(loadout.BankId, normId, StringComparison.OrdinalIgnoreCase)) loadout.BankId = "";
                if (string.Equals(loadout.AlbumId, normId, StringComparison.OrdinalIgnoreCase)) loadout.AlbumId = "";
                loadout.CardSkinIds?.RemoveAll(x => string.Equals(x, normId, StringComparison.OrdinalIgnoreCase));
            }
            result = loadout.Clone();
        }
        SaveAccount(acc);
        return result;
    }

    public HeroLoadoutState ApplyEquipLoadout(
        Account acc,
        string heroId,
        string? boardId,
        string? carpetId,
        string? cardBackId,
        string? toyId,
        string? stashId,
        string? bankId,
        string? albumId,
        string? heroSkinId)
    {
        string key = string.IsNullOrWhiteSpace(heroId) ? "Vanessa" : heroId.Trim();
        HeroLoadoutState result;
        lock (_lock)
        {
            var loadout = GetOrCreateHeroLoadoutLocked(acc, key);
            if (boardId != null) loadout.BoardId = NormalizeCollectibleId(boardId);
            if (carpetId != null) loadout.CarpetId = NormalizeCollectibleId(carpetId);
            if (cardBackId != null) loadout.CardBackId = NormalizeCollectibleId(cardBackId);
            if (toyId != null) loadout.ToyId = NormalizeCollectibleId(toyId);
            if (stashId != null) loadout.StashId = NormalizeCollectibleId(stashId);
            if (bankId != null) loadout.BankId = NormalizeCollectibleId(bankId);
            if (albumId != null) loadout.AlbumId = NormalizeCollectibleId(albumId);
            if (heroSkinId != null) loadout.HeroSkinId = NormalizeCollectibleId(heroSkinId);
            result = loadout.Clone();
        }
        SaveAccount(acc);
        return result;
    }

    public int GetUnopenedChestCount(Account acc)
    {
        lock (_lock) return acc.UnopenedChestCount;
    }

    public int GetGems(Account acc)
    {
        lock (_lock) return acc.Gems;
    }

    public List<string> GetOpenedCardSkinIds(Account acc)
    {
        lock (_lock) return acc.OpenedCardSkinIds.ToList();
    }

    public int AddUnopenedChests(Account acc, int count)
    {
        int total;
        lock (_lock)
        {
            acc.UnopenedChestCount = Math.Max(0, acc.UnopenedChestCount + Math.Max(0, count));
            total = acc.UnopenedChestCount;
        }
        SaveAccount(acc);
        return total;
    }

    public (bool Ok, List<ChestOpenDraw> Draws, int RemainingChests, int TotalGems) TryOpenChests(
        Account acc,
        int requestedCount,
        Func<HashSet<string>, ChestOpenDraw> drawOne)
    {
        var draws = new List<ChestOpenDraw>();
        int remaining;
        int totalGems;
        lock (_lock)
        {
            if (requestedCount <= 0 || acc.UnopenedChestCount <= 0)
            {
                return (false, draws, acc.UnopenedChestCount, acc.Gems);
            }
            int actual = Math.Min(requestedCount, acc.UnopenedChestCount);
            for (int i = 0; i < actual; i++)
            {
                var draw = drawOne(acc.OpenedCardSkinIds);
                draws.Add(draw);
                if (draw.IsDuplicate)
                {
                    acc.Gems += Math.Max(0, draw.GemsAwarded);
                }
                else if (draw.IsCardSkin && !string.IsNullOrEmpty(draw.NormalizedId))
                {
                    acc.OpenedCardSkinIds.Add(draw.NormalizedId);
                }
            }
            acc.UnopenedChestCount -= actual;
            remaining = acc.UnopenedChestCount;
            totalGems = acc.Gems;
        }
        SaveAccount(acc);
        return (true, draws, remaining, totalGems);
    }

    private static HeroLoadoutState GetOrCreateHeroLoadoutLocked(Account acc, string heroKey)
    {
        if (!acc.HeroLoadouts.TryGetValue(heroKey, out var loadout))
        {
            loadout = new HeroLoadoutState { HeroId = heroKey };
            acc.HeroLoadouts[heroKey] = loadout;
        }
        return loadout;
    }

    // ── 安全哈希与文本清洗工具 ──────────────────────────────────────────────

    public static string HashSecret(string rawSecret)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(rawSecret),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);
        return $"v1:{Pbkdf2Iterations}:{Convert.ToHexString(salt).ToLowerInvariant()}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static bool VerifySecretHash(string rawSecret, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(rawSecret) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        string[] parts = storedHash.Split(':');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out int iterations) || iterations < 1000)
            return false;

        try
        {
            byte[] salt = Convert.FromHexString(parts[2]);
            byte[] expected = Convert.FromHexString(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(rawSecret),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>剥离 Unity TMP 富文本尖括号、Unicode BiDi 双向控制符与不可见控制字符，防止 UI 注入。</summary>
    public static string SanitizeDisplayText(string? raw, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string stripped = TmpTagRegex.Replace(raw, string.Empty);
        var sb = new StringBuilder(Math.Min(stripped.Length, maxLength));
        foreach (char c in stripped)
        {
            if (IsForbiddenControlOrBidi(c)) continue;
            if (c == '<' || c == '>') continue;
            if (c == '\r' || c == '\n' || c == '\t')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString().Trim();
    }

    private static bool IsForbiddenControlOrBidi(char c)
    {
        if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
            return true;
        int code = c;
        return (code >= 0x202A && code <= 0x202E)
            || (code >= 0x2066 && code <= 0x2069)
            || code == 0x200E || code == 0x200F || code == 0xFEFF;
    }

    private static string StripBearerPrefix(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        string trimmed = token.Trim();
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return trimmed[7..].Trim();
        return trimmed;
    }

    private static string NormalizeClientKind(string? clientKind)
    {
        string k = (clientKind ?? "game").Trim().ToLowerInvariant();
        return k is "game" or "launcher" or "web" ? k : "game";
    }

    public static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Player";
        string sanitized = SanitizeDisplayText(raw, 64);
        var sb = new StringBuilder(sanitized.Length);
        foreach (char c in sanitized)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c > 127)
                sb.Append(c);
        }
        string s = sb.ToString();
        if (string.IsNullOrEmpty(s)) s = "Player";
        if (s.Length > 32) s = s[..32];
        return s;
    }

    private static string DeriveNameFromFingerprint(string ticket)
    {
        string fp = Fingerprint(ticket);
        return "Player_" + fp[..8];
    }

    public static string Fingerprint(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant();

    private static Guid GuidFromHash(string s)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        byte[] g = new byte[16];
        Array.Copy(hash, g, 16);
        return new Guid(g);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _accountSaveTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { FlushPendingAccountSaves(); } catch { }
        try { _accountSaveTimer.Dispose(); } catch { }
    }
}
