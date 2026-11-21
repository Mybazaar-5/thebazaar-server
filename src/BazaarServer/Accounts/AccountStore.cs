using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Game;

namespace BazaarServer.Accounts;

/// <summary>
/// 账号、令牌与英雄生涯战绩持久化。
/// 自建服不依赖 Steam：有票据按票据建号，无票据按玩家名建号，所有生涯战绩落盘 SQLite。
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly string[] KnownPlayableHeroes =
    [
        "Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle", "Jules", "Karnok", "Hero8",
    ];

    private readonly BazaarServer.Storage.Persistence _db;
    private readonly ConcurrentDictionary<string, Account> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _byToken = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public AccountStore(BazaarServer.Storage.Persistence db) => _db = db;

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
        // 账号级英雄配装、未开启宝箱数、已解锁卡牌皮肤与宝石余额持久化
        public Dictionary<string, HeroLoadoutState> HeroLoadouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int? UnopenedChestCount { get; set; }
        public List<string> OpenedCardSkinIds { get; set; } = new();
        public int? Gems { get; set; }
    }

    public sealed class Account
    {
        public Guid AccountId { get; init; }
        public string Username { get; init; }
        public string IdentityKey { get; init; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime AccessExpiresAt { get; set; }
        public DateTime RefreshExpiresAt { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
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
    }

    /// <summary>按 Steam 票据建号（旧路径）。</summary>
    public Account GetOrCreateBySteamTicket(string ticketHex)
        => GetOrCreate("steam:" + Fingerprint(ticketHex), DeriveNameFromFingerprint(ticketHex));

    /// <summary>按用户名建号（新路径：输入名字即登录）。</summary>
    public Account GetOrCreateByName(string username)
    {
        string clean = NormalizeName(username);
        return GetOrCreate("name:" + clean.ToLowerInvariant(), clean);
    }

    private Account GetOrCreate(string key, string username)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                return IssueTokens(existing);
            }

            // 先查持久化
            var row = _db.LoadAccount(key);
            var acc = row != null
                ? AccountFromRow(row)
                : new Account
                {
                    AccountId = GuidFromHash("acc:" + key),
                    Username = username,
                    IdentityKey = key,
                };
            _byKey[key] = acc;
            SaveAccount(acc);
            return IssueTokens(acc);
        }
    }

    private static Account AccountFromRow(BazaarServer.Storage.Persistence.AccountRow row)
    {
        var acc = new Account
        {
            AccountId = row.AccountId,
            Username = row.Username,
            IdentityKey = row.IdentityKey,
            CreatedAt = row.CreatedAt,
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

    private Account IssueTokens(Account acc)
    {
        string access = "acc_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        string refresh = "ref_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        if (!string.IsNullOrEmpty(acc.AccessToken)) _byToken.TryRemove(acc.AccessToken, out _);
        if (!string.IsNullOrEmpty(acc.RefreshToken)) _byToken.TryRemove(acc.RefreshToken, out _);

        acc.AccessToken = access;
        acc.RefreshToken = refresh;
        acc.AccessExpiresAt = DateTime.UtcNow.AddHours(12);
        acc.RefreshExpiresAt = DateTime.UtcNow.AddDays(30);

        _byToken[access] = acc.IdentityKey;
        _byToken[refresh] = acc.IdentityKey;
        return acc;
    }

    public Account FindByAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
        if (!_byToken.TryGetValue(token, out var key)) return null;
        return _byKey.TryGetValue(key, out var acc) ? acc : null;
    }

    public Account FindByAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        string norm = accountId.Replace("-", "").ToLowerInvariant();
        foreach (var kv in _byKey)
        {
            if (kv.Value.AccountId.ToString("N") == norm) return kv.Value;
        }
        return null;
    }

    public Account FindByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        string key = "name:" + NormalizeName(username).ToLowerInvariant();
        return _byKey.TryGetValue(key, out var acc) ? acc : null;
    }

    public IEnumerable<Account> All => _byKey.Values;

    /// <summary>从 SQLite 加载全部账号与生涯战绩到内存（服务端启动时调用）。</summary>
    public void LoadAllFromDb()
    {
        try
        {
            var rows = _db.LoadAllAccounts();
            foreach (var row in rows)
            {
                if (_byKey.ContainsKey(row.IdentityKey)) continue;
                _byKey[row.IdentityKey] = AccountFromRow(row);
            }
            Console.WriteLine($"[AccountStore] loaded {_byKey.Count} accounts from db");
        }
        catch (Exception ex) { Console.WriteLine("[AccountStore] LoadAllFromDb: " + ex.Message); }
    }

    /// <summary>把账号与生涯战绩写回 SQLite。</summary>
    public void SaveAccount(Account acc)
    {
        if (acc == null) return;
        try
        {
            string careerJson;
            lock (_lock)
            {
                careerJson = SerializeCareerJson(acc);
            }
            _db.SaveAccount(new BazaarServer.Storage.Persistence.AccountRow(
                acc.AccountId, acc.Username, acc.IdentityKey,
                acc.RunsCompleted, acc.Victories, acc.Defeats, acc.BestDay,
                acc.CreatedAt, DateTime.UtcNow, careerJson));
        }
        catch { /* 持久化失败不影响游戏 */ }
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

        // 数据库层幂等校验：若 runs 表存在该局且已标记 career_recorded，则直接跳过
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

    /// <summary>局内增量统计（PvE 胜负、卖卡、消费、事件完成、战斗伤害等）。</summary>
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
        }

        SaveAccount(acc);
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

    /// <summary>将 GUID 统一规范为 32 位小写无连字符格式，对齐客户端 CollectionUtility.FixGuidNaming。</summary>
    public static string NormalizeCollectibleId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (Guid.TryParse(id.Trim(), out var g)) return g.ToString("N");
        return id.Trim().Replace("-", "").ToLowerInvariant();
    }

    /// <summary>获取指定英雄的当前配装快照（若无则创建默认空配装）。</summary>
    public HeroLoadoutState GetHeroLoadout(Account acc, string heroId)
    {
        string key = string.IsNullOrWhiteSpace(heroId) ? "Vanessa" : heroId.Trim();
        lock (_lock)
        {
            return GetOrCreateHeroLoadoutLocked(acc, key).Clone();
        }
    }

    /// <summary>获取所有指定英雄的配装快照列表。</summary>
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

    /// <summary>根据藏品分类将指定藏品装备到英雄对应槽位并落盘。</summary>
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

    /// <summary>从指定英雄配装中卸下指定藏品并落盘。</summary>
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

    /// <summary>批量应用 EquipLoadoutRequest 槽位配置并落盘。</summary>
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

    /// <summary>为账号增加未开启宝箱数量并落盘。</summary>
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

    /// <summary>原子扣除未开启宝箱并结算获得的卡牌皮肤与重复宝石奖励。</summary>
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

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Player";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c > 127) sb.Append(c);
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

    private static string Fingerprint(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant();

    private static Guid GuidFromHash(string s)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        byte[] g = new byte[16];
        Array.Copy(hash, g, 16);
        return new Guid(g);
    }
}
