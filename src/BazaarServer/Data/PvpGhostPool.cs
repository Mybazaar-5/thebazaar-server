using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Data;

/// <summary>
/// 从 ghost_pool_balanced.jsonl 加载 7,200 套全 8 英雄 × 15 天 × 4 档强度的真实 PvP 镜像盘面，
/// 并支持从 SQLite pvp_ghosts 加载及在 Hour 6 PvP 时热写入真人玩家镜像，混合匹配同天近胜场对手。
/// </summary>
public sealed class PvpGhostPool
{
    public const int MaxBoardsPerDayBucket = 2500;

    // 客户端真实可用的 8 位英雄（包含第 8 英雄双龙 Hero8）
    private static readonly HashSet<string> SupportedClientHeroes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vanessa", "Dooley", "Pygmalien", "Mak", "Karnok", "Jules", "Stelle", "Hero8",
    };

    /// <summary>
    /// 统一解析 8 位受支持英雄（含 Karnok 与 Hero8），保证镜像预览卡与战斗对手板英雄严格一致。
    /// </summary>
    public static EHero ParseHero(string? hero)
    {
        if (!string.IsNullOrWhiteSpace(hero) &&
            Enum.TryParse<EHero>(hero.Trim(), ignoreCase: true, out var parsed) &&
            parsed != EHero.Common &&
            SupportedClientHeroes.Contains(parsed.ToString()))
        {
            return parsed;
        }
        return EHero.Vanessa;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class GhostItem
    {
        public int Slot { get; init; }
        public int Width { get; init; } = 1;
        public string TemplateId { get; init; } = "";
        public string InternalName { get; init; } = "";
        public string ZhName { get; init; } = "";
        public string Tier { get; init; } = "Bronze";
        public string? Enchantment { get; init; }
        public Dictionary<string, int> PermanentAttributes { get; init; } = new();
    }

    public sealed class GhostSkill
    {
        public string TemplateId { get; init; } = "";
        public string InternalName { get; init; } = "";
        public string ZhName { get; init; } = "";
        public string Tier { get; init; } = "Bronze";
    }

    public sealed class GhostBoard
    {
        public string BoardId { get; init; } = "";
        public int Day { get; init; } = 1;
        public string Hero { get; init; } = "Vanessa";
        public int Level { get; init; } = 1;
        public int HealthMax { get; init; } = 250;
        public int Prestige { get; init; } = 20;
        public int Wins { get; init; }
        public int Losses { get; init; }
        public int UnlockedSlotsMask { get; init; } = 120;
        public string PowerBucket { get; init; } = "Normal";
        public double EstimatedWinRate { get; init; } = 0.5;
        public bool IsPlayerGhost { get; init; }
        public bool IsCustomRule { get; init; }
        public List<GhostItem> Items { get; init; } = new();
        public List<GhostSkill> Skills { get; init; } = new();
    }

    private sealed class StoredGhostBoardPayload
    {
        public string BoardId { get; set; } = "";
        public string PowerBucket { get; set; } = "Normal";
        public double EstimatedWinRate { get; set; } = 0.5;
        public bool IsCustomRule { get; set; }
        public List<GhostItem> Items { get; set; } = new();
        public List<GhostSkill> Skills { get; set; } = new();
    }

    private readonly Dictionary<int, List<GhostBoard>> _byDay = new();
    private readonly List<GhostBoard> _all = new();
    private readonly HashSet<string> _knownBoardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock) return _all.Count;
        }
    }

    public int PlayerGhostCount
    {
        get
        {
            lock (_lock) return _all.Count(b => b.IsPlayerGhost);
        }
    }

    public int CustomRuleGhostCount
    {
        get
        {
            lock (_lock) return _all.Count(b => b.IsCustomRule);
        }
    }

    public static PvpGhostPool Load(string dataRoot)
    {
        var pool = new PvpGhostPool();
        string? path = ResolvePath(dataRoot);
        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("[PvpGhostPool] ghost_pool_balanced.jsonl not found; using dynamic fallback");
            return pool;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var board = ParseLine(line);
            if (board == null || !IsSaneGhostForDay(board, board.Day)) continue;
            pool.AddBoardUnsafe(board);
        }

        Console.WriteLine($"[PvpGhostPool] loaded {pool._all.Count} ghost boards from {path} ({pool._byDay.Count} day buckets)");
        return pool;
    }

    /// <summary>
    /// 依据 compass_distilled.json 天梯校准曲线返回每日 PvP 自然中位数血量预期（Day 4 ≈ 1000、Day 7 ≈ 2200、Day 10 ≈ 4400）。
    /// </summary>
    public static int GetExpectedMedianHealthForDay(int day) => Math.Max(1, day) switch
    {
        1 => 350,
        2 => 550,
        3 => 750,
        4 => 1000,
        5 => 1350,
        6 => 1750,
        7 => 2200,
        8 => 2800,
        9 => 3500,
        10 => 4400,
        11 => 5400,
        12 => 6400,
        13 => 7400,
        14 => 8400,
        int d => 9400 + (d - 15) * 1000,
    };

    /// <summary>
    /// 依据 compass_distilled.json 天梯血量曲线返回每日合理 PvP 镜像区间 (MinHp, MaxHp, MaxLevel)。
    /// 前期（Day 1..4）保持严格拦截异常脏镜像；中后期放宽至合法成长上界，配合 PickOpponent 频段权重实现两层治理。
    /// </summary>
    public static (int MinHp, int MaxHp, int MaxLevel) GetSaneBoundsForDay(int day) => Math.Max(1, day) switch
    {
        1 => (200, 550, 2),
        2 => (300, 900, 3),
        3 => (400, 1500, 4),
        4 => (500, 2500, 5),
        5 => (650, 4500, 6),
        6 => (850, 7500, 8),
        7 => (1100, 10000, 11),
        8 => (1400, 15000, 14),
        9 => (1750, 20000, 17),
        10 => (2100, 25000, 20),
        11 => (2400, 33000, 22),
        12 => (2700, 41000, 24),
        13 => (3000, 50000, 26),
        14 => (3200, 65000, 28),
        int d => (3500 + (d - 15) * 300, 80000 + (d - 15) * 5000, Math.Max(30, d + 15)),
    };

    /// <summary>返回指定天数对应的合法 PvP 镜像上限（MaxHp, MaxLevel），用于清理与拦截失真脏镜像。</summary>
    public static (int MaxHp, int MaxLevel) GetSaneCapsForDay(int day)
    {
        var (_, maxHp, maxLv) = GetSaneBoundsForDay(day);
        return (maxHp, maxLv);
    }

    /// <summary>校验指定镜像是否符合该天数基于罗盘校准的合理血量区间与等级上限（真人镜像允许最低 100 HP 兼容，同时严格拦截超标脏数据）。</summary>
    public static bool IsSaneGhostForDay(GhostBoard ghost, int day)
    {
        if (ghost == null) return false;
        if (ghost.Items == null || ghost.Items.Count == 0) return false;
        var (minHp, maxHp, maxLv) = GetSaneBoundsForDay(day);
        int effectiveMinHp = ghost.IsPlayerGhost ? Math.Min(100, minHp) : minHp;
        return ghost.HealthMax >= effectiveMinHp
            && ghost.HealthMax <= maxHp
            && ghost.Level >= 1
            && ghost.Level <= maxLv;
    }

    /// <summary>从 SQLite pvp_ghosts 表加载已沉淀的真人玩家镜像并热加入当日战力分桶。</summary>
    public int LoadFromDb(Persistence db, bool includeCustomRule = true)
    {
        if (db == null) return 0;
        var rows = db.LoadPvpGhosts(2000, includeCustomRule: includeCustomRule);
        int added = 0;

        lock (_lock)
        {
            foreach (var row in rows)
            {
                var board = ParseDbRow(row);
                if (board == null || !IsSaneGhostForDay(board, board.Day)) continue;
                if (AddBoardUnsafe(board)) added++;
            }
        }

        if (added > 0)
            Console.WriteLine($"[PvpGhostPool] loaded {added} player ghost boards from db (total={Count})");
        return added;
    }

    /// <summary>记录当前对局 Hour 6 PvP 玩家真实阵容到 SQLite pvp_ghosts 并立即热加载进当日分桶。</summary>
    public GhostBoard? RecordPlayerGhost(RunState run, Persistence db, bool isCustomRule = false)
    {
        if (run == null || db == null) return null;

        int day = (int)Math.Max(1u, run.Day);
        string boardId = $"player_{run.RunId}_d{day}";

        lock (_lock)
        {
            if (_knownBoardIds.Contains(boardId))
                return null;
        }

        var items = new List<GhostItem>();
        foreach (var entry in run.Cards.Values
                     .Where(c => c.Owner == ECombatantId.Player &&
                                 c.Type == ECardType.Item &&
                                 (c.Section == null || c.Section == EInventorySection.Hand))
                     .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99))
        {
            int slot = entry.Socket.HasValue ? (int)entry.Socket.Value : (entry.CoreCard?.StartSlot ?? 0);
            int width = Math.Clamp((int)entry.Size, 1, 3);

            var perm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entry.Attributes)
            {
                perm[kv.Key.ToString()] = kv.Value;
            }
            if (entry.CoreCard != null)
            {
                foreach (var kv in entry.CoreCard.PermanentAdd)
                {
                    if (Math.Abs(kv.Value) < 0.001) continue;
                    double baseVal = entry.CoreCard.BaseAttributes.GetValueOrDefault(kv.Key, 0);
                    double mult = entry.CoreCard.PermanentMult.GetValueOrDefault(kv.Key, 1.0);
                    int unscaled = (int)Math.Round((baseVal + kv.Value) * mult);
                    perm[kv.Key] = unscaled;
                }
            }

            items.Add(new GhostItem
            {
                Slot = Math.Clamp(slot, 0, 9),
                Width = width,
                TemplateId = entry.TemplateId.ToString(),
                InternalName = "",
                ZhName = "",
                Tier = entry.Tier.ToString(),
                Enchantment = entry.Enchantment?.ToString(),
                PermanentAttributes = perm,
            });
        }

        if (items.Count == 0)
            return null;

        var skills = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill)
            .Select(c => new GhostSkill
            {
                TemplateId = c.TemplateId.ToString(),
                InternalName = "",
                ZhName = "",
                Tier = c.Tier.ToString(),
            })
            .ToList();

        int wins = (int)run.Victories;
        int losses = (int)run.Defeats;
        int healthMax = Math.Max(100, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250));
        int prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20);
        int level = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
        int unlockedSlots = run.UnlockedSlots > 0 ? run.UnlockedSlots : 120;
        string hero = run.Hero == EHero.Common ? "Vanessa" : run.Hero.ToString();

        string powerBucket = wins >= Math.Max(1, day - 1)
            ? "Strong"
            : losses >= wins + 2
                ? "Weak"
                : "Normal";
        double estWr = Math.Clamp(0.45 + wins * 0.05 - losses * 0.04, 0.15, 0.95);

        var payload = new StoredGhostBoardPayload
        {
            BoardId = boardId,
            PowerBucket = powerBucket,
            EstimatedWinRate = estWr,
            IsCustomRule = isCustomRule,
            Items = items,
            Skills = skills,
        };

        var board = new GhostBoard
        {
            BoardId = boardId,
            Day = day,
            Hero = hero,
            Level = level,
            HealthMax = healthMax,
            Prestige = prestige,
            Wins = wins,
            Losses = losses,
            UnlockedSlotsMask = unlockedSlots,
            PowerBucket = powerBucket,
            EstimatedWinRate = estWr,
            IsPlayerGhost = true,
            IsCustomRule = isCustomRule,
            Items = items,
            Skills = skills,
        };

        // 拒绝记录超出当日合理血量/等级上限的异常脏镜像（防止测试/异常对局污染 pvp_ghosts）
        if (!IsSaneGhostForDay(board, day))
            return null;

        string boardJson = JsonSerializer.Serialize(payload, JsonOpts);
        var now = DateTime.UtcNow;

        db.InsertPvpGhost(new Persistence.PvpGhostRow(
            0, day, hero, wins, losses, healthMax, prestige, level, unlockedSlots, boardJson, now, isCustomRule));

        lock (_lock)
        {
            AddBoardUnsafe(board);
        }

        Console.WriteLine($"[PvpGhostPool] recorded player ghost {boardId} (Day={day}, Hero={hero}, W={wins}/L={losses}, Items={items.Count}, Skills={skills.Count}, CustomRule={isCustomRule})");
        return board;
    }

    private bool AddBoardUnsafe(GhostBoard board)
    {
        if (!string.IsNullOrEmpty(board.BoardId) && !_knownBoardIds.Add(board.BoardId))
            return false;

        int bucketDay = Math.Clamp(board.Day, 1, 15);
        if (!_byDay.TryGetValue(bucketDay, out var list))
        {
            list = new List<GhostBoard>();
            _byDay[bucketDay] = list;
        }
        list.Add(board);
        _all.Add(board);
        TrimDayBucketUnsafe(list);
        return true;
    }

    private void TrimDayBucketUnsafe(List<GhostBoard> dayList)
    {
        while (dayList.Count > MaxBoardsPerDayBucket)
        {
            int victimIdx = dayList.FindIndex(IsEvictablePlayerGhost);
            if (victimIdx < 0)
                break;

            var victim = dayList[victimIdx];
            dayList.RemoveAt(victimIdx);
            _all.Remove(victim);
            if (!string.IsNullOrEmpty(victim.BoardId))
                _knownBoardIds.Remove(victim.BoardId);
        }
    }

    private static bool IsEvictablePlayerGhost(GhostBoard board)
        => board.IsPlayerGhost ||
           board.BoardId.StartsWith("player_", StringComparison.OrdinalIgnoreCase);

    private static GhostBoard? ParseDbRow(Persistence.PvpGhostRow row)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<StoredGhostBoardPayload>(row.BoardJson, JsonOpts);
            if (payload == null) return null;

            string boardId = string.IsNullOrWhiteSpace(payload.BoardId)
                ? $"player_db_{row.Id}_d{row.Day}"
                : payload.BoardId;

            return new GhostBoard
            {
                BoardId = boardId,
                Day = row.Day,
                Hero = string.IsNullOrWhiteSpace(row.Hero) ? "Vanessa" : row.Hero,
                Level = Math.Max(1, row.Level),
                HealthMax = Math.Max(100, row.HealthMax),
                Prestige = row.Prestige,
                Wins = row.Wins,
                Losses = row.Losses,
                UnlockedSlotsMask = row.UnlockedSlots > 0 ? row.UnlockedSlots : 120,
                PowerBucket = string.IsNullOrWhiteSpace(payload.PowerBucket) ? "Normal" : payload.PowerBucket,
                EstimatedWinRate = payload.EstimatedWinRate > 0 ? payload.EstimatedWinRate : 0.5,
                IsPlayerGhost = true,
                IsCustomRule = row.IsCustomRule || payload.IsCustomRule,
                Items = payload.Items ?? new List<GhostItem>(),
                Skills = payload.Skills ?? new List<GhostSkill>(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolvePath(string dataRoot)
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_GHOSTS");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string inData = Path.Combine(dataRoot, "ghost_pool_balanced.jsonl");
        if (File.Exists(inData)) return inData;

        // 本地开发回退路径
        string repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output", "pvp_dataset", "ghost_pool_balanced.jsonl"));
        if (File.Exists(repoPath)) return repoPath;

        string wsPath = @"e:\thebazaar\output\pvp_dataset\ghost_pool_balanced.jsonl";
        if (File.Exists(wsPath)) return wsPath;

        return null;
    }

    private static GhostBoard? ParseLine(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var items = new List<GhostItem>();
            if (r.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in itemsEl.EnumerateArray())
                {
                    var perm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (it.TryGetProperty("permanentAttributes", out var pa) && pa.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var kv in pa.EnumerateObject())
                        {
                            if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt32(out int v))
                                perm[kv.Name] = v;
                        }
                    }
                    items.Add(new GhostItem
                    {
                        Slot = GetInt(it, "slot", 0),
                        Width = GetInt(it, "width", 1),
                        TemplateId = GetStr(it, "templateId") ?? "",
                        InternalName = GetStr(it, "internalName") ?? "",
                        ZhName = GetStr(it, "zhName") ?? "",
                        Tier = GetStr(it, "tier") ?? "Bronze",
                        Enchantment = GetStr(it, "enchantment"),
                        PermanentAttributes = perm,
                    });
                }
            }

            var skills = new List<GhostSkill>();
            if (r.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var sk in skillsEl.EnumerateArray())
                {
                    skills.Add(new GhostSkill
                    {
                        TemplateId = GetStr(sk, "templateId") ?? "",
                        InternalName = GetStr(sk, "internalName") ?? "",
                        ZhName = GetStr(sk, "zhName") ?? "",
                        Tier = GetStr(sk, "tier") ?? "Bronze",
                    });
                }
            }

            return new GhostBoard
            {
                BoardId = GetStr(r, "boardId") ?? "",
                Day = GetInt(r, "day", 1),
                Hero = GetStr(r, "hero") ?? "Vanessa",
                Level = GetInt(r, "level", 1),
                HealthMax = Math.Max(100, GetInt(r, "healthMax", 250)),
                Prestige = GetInt(r, "prestige", 20),
                Wins = GetInt(r, "wins", 0),
                Losses = GetInt(r, "losses", 0),
                UnlockedSlotsMask = GetInt(r, "unlockedSlotsMask", 120),
                PowerBucket = GetStr(r, "powerBucket") ?? "Normal",
                EstimatedWinRate = r.TryGetProperty("estimatedWinRate", out var ew) && ew.TryGetDouble(out double d) ? d : 0.5,
                IsPlayerGhost = false,
                Items = items,
                Skills = skills,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按同天数 + 近胜场 + 战力分桶从静态 7,200 套与 SQLite 真人镜像混合匹敌对手。
    /// 标准模式（includeCustomRuleGhosts = false）严格屏蔽自定义预设幽灵，防止开局预设污染标准公共匹配池。
    /// </summary>
    public GhostBoard PickOpponent(
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo = null,
        bool includeCustomRuleGhosts = false)
    {
        int bucketDay = Math.Clamp(day, 1, 15);

        lock (_lock)
        {
            if (_byDay.TryGetValue(bucketDay, out var dayList) && dayList.Count > 0)
            {
                var baseList = includeCustomRuleGhosts
                    ? dayList
                    : dayList.Where(b => !b.IsCustomRule).ToList();

                // 1. 全天数合理血量与等级门卫（拒收如 Day 4 却 3065 血的异常脏镜像）
                var saneList = baseList.Where(b => IsSaneGhostForDay(b, day)).ToList();

                // 2. 过滤不支持的英雄（如仅限服务端受支持的 8 位正式英雄），若合规桶皆为其他英雄才在合规集内回退
                var heroFiltered = saneList.Where(b => IsSupportedClientHero(b.Hero)).ToList();
                if (heroFiltered.Count == 0)
                    heroFiltered = saneList;

                // 3. 过滤含异常重复同模板（>= 3 件）或无效模板 ID 的脏样本
                var cleanList = heroFiltered.Where(b => !HasAbnormalDuplicateOrInvalidItems(b, repo)).ToList();
                var eligibleDayList = cleanList.Count > 0 ? cleanList : heroFiltered;

                // 4. 按天数偏好健康占格填充度（优先排除明显缺编/空板残缺样本；Day 1 维持严格 Lv<=1 且 HP<=320）
                if (day <= 1)
                {
                    var day1Strict = eligibleDayList
                        .Where(b => b.Level <= 1 && b.HealthMax <= 320 && GetTotalSlotWidth(b, repo) >= 2)
                        .ToList();
                    if (day1Strict.Count == 0)
                    {
                        day1Strict = eligibleDayList
                            .Where(b => b.Level <= 1 && b.HealthMax <= 320)
                            .ToList();
                    }
                    if (day1Strict.Count > 0)
                    {
                        eligibleDayList = day1Strict;
                    }
                    else if (eligibleDayList.Count == 0)
                    {
                        return BuildFallbackGhost(1, rng, repo);
                    }
                }
                else
                {
                    int minHealthyWidth = day switch
                    {
                        2 => 4,
                        3 => 5,
                        4 => 6,
                        _ => 8,
                    };
                    var completeBoards = eligibleDayList
                        .Where(b => GetTotalSlotWidth(b, repo) >= minHealthyWidth)
                        .ToList();
                    if (completeBoards.Count > 0)
                        eligibleDayList = completeBoards;
                }

                if (eligibleDayList.Count > 0)
                {
                    // 静态常规池对 Day 4+ 进一步优先抽满编 10 格盘面（同时保留 >= 8 格的真人镜像不被误杀）
                    var staticPreferredPool = eligibleDayList;
                    if (day >= 4)
                    {
                        var full10Boards = eligibleDayList
                            .Where(b => GetTotalSlotWidth(b, repo) >= 10)
                            .ToList();
                        if (full10Boards.Count > 0)
                            staticPreferredPool = full10Boards;
                    }

                    string[] preferredBuckets = (day <= 2 || playerLosses >= playerWins + 2)
                        ? ["Weak", "Normal"]
                        : playerWins >= day - 1
                            ? ["Strong", "Normal", "MetaBoss"]
                            : ["Normal", "Strong", "Weak"];

                    // 优先同天 + 近胜场（|wins - playerWins| <= 1）+ 战力分桶
                    var tier1 = staticPreferredPool
                        .Where(b => preferredBuckets.Contains(b.PowerBucket) && Math.Abs(b.Wins - playerWins) <= 1)
                        .ToList();
                    var tier2 = tier1.Count > 0
                        ? tier1
                        : staticPreferredPool.Where(b => Math.Abs(b.Wins - playerWins) <= 2 || preferredBuckets.Contains(b.PowerBucket)).ToList();
                    var pool = tier2.Count > 0 ? tier2 : staticPreferredPool;

                    // 真人镜像在合规同天近胜场池中给予 35% 混合出场权重；同时按中位数频段控制分布：
                    // 正常频段 [0.7x..1.8x] 中位数 >= 95%，中间带 <= 2%，超高尾 (> 2.2x 中位数) <= 3%
                    var closePlayerGhosts = eligibleDayList
                        .Where(b => b.IsPlayerGhost && Math.Abs(b.Wins - playerWins) <= 2)
                        .ToList();
                    if (closePlayerGhosts.Count == 0)
                        closePlayerGhosts = eligibleDayList.Where(b => b.IsPlayerGhost).ToList();

                    return PickByMedianBandDistribution(pool, closePlayerGhosts, day, rng);
                }
            }

            if (day <= 1)
                return BuildFallbackGhost(1, rng, repo);

            var eligibleAll = (includeCustomRuleGhosts
                ? _all
                : _all.Where(b => !b.IsCustomRule))
                .Where(b => IsSaneGhostForDay(b, day))
                .ToList();
            var eligibleAllHero = eligibleAll.Where(b => IsSupportedClientHero(b.Hero) && !HasAbnormalDuplicateOrInvalidItems(b, repo)).ToList();
            if (eligibleAllHero.Count > 0)
                return PickByMedianBandDistribution(eligibleAllHero, Array.Empty<GhostBoard>(), day, rng);

            if (eligibleAll.Count > 0)
                return PickByMedianBandDistribution(eligibleAll, Array.Empty<GhostBoard>(), day, rng);
        }

        return BuildFallbackGhost(day, rng, repo);
    }

    /// <summary>
    /// 按每日预期中位数血量将候选项分为正常频段 [0.7x, 1.8x]、低/过渡带与超高尾 (&gt; 2.2x)，
    /// 确保 >= 95% 对手落在正常成长频段，超高尾出现率严格控制在 &lt;= 3%。
    /// </summary>
    private static GhostBoard PickByMedianBandDistribution(
        IReadOnlyList<GhostBoard> staticPool,
        IReadOnlyList<GhostBoard> playerGhosts,
        int day,
        Random rng)
    {
        double median = GetExpectedMedianHealthForDay(day);
        int normalMin = (int)Math.Round(median * 0.70);
        int normalMax = (int)Math.Round(median * 1.80);
        int tailMin = (int)Math.Round(median * 2.20);

        bool IsNormal(GhostBoard b) => b.HealthMax >= normalMin && b.HealthMax <= normalMax;
        bool IsUltraTail(GhostBoard b) => b.HealthMax > tailMin;
        bool IsMidBand(GhostBoard b) => !IsNormal(b) && !IsUltraTail(b);

        var normalStatic = staticPool.Where(IsNormal).ToList();
        var normalPlayers = playerGhosts.Where(IsNormal).ToList();
        var ultraTail = staticPool.Concat(playerGhosts).Where(IsUltraTail).ToList();
        var midStatic = staticPool.Where(IsMidBand).ToList();
        var midPlayers = playerGhosts.Where(IsMidBand).ToList();

        double roll = rng.NextDouble();
        if (roll < 0.03 && ultraTail.Count > 0)
            return ultraTail[rng.Next(ultraTail.Count)];

        if (roll < 0.05 && (midStatic.Count > 0 || midPlayers.Count > 0))
        {
            if (midPlayers.Count > 0 && (midStatic.Count == 0 || rng.NextDouble() < 0.35))
                return midPlayers[rng.Next(midPlayers.Count)];
            if (midStatic.Count > 0)
                return midStatic[rng.Next(midStatic.Count)];
        }

        if (normalPlayers.Count > 0 && (normalStatic.Count == 0 || rng.NextDouble() < 0.35))
            return normalPlayers[rng.Next(normalPlayers.Count)];
        if (normalStatic.Count > 0)
            return normalStatic[rng.Next(normalStatic.Count)];

        if (midPlayers.Count > 0 && (midStatic.Count == 0 || rng.NextDouble() < 0.35))
            return midPlayers[rng.Next(midPlayers.Count)];
        if (midStatic.Count > 0)
            return midStatic[rng.Next(midStatic.Count)];

        if (playerGhosts.Count > 0 && (staticPool.Count == 0 || rng.NextDouble() < 0.35))
            return playerGhosts[rng.Next(playerGhosts.Count)];
        return staticPool[rng.Next(staticPool.Count)];
    }

    private static bool IsSupportedClientHero(string? hero)
        => !string.IsNullOrWhiteSpace(hero) && SupportedClientHeroes.Contains(hero.Trim());

    private static int GetTotalSlotWidth(GhostBoard board, GameDataRepository? repo)
    {
        int total = 0;
        foreach (var it in board.Items)
        {
            int w = Math.Clamp(it.Width, 1, 3);
            if (repo != null)
            {
                var tpl = repo.GetCard(it.TemplateId);
                if (tpl != null)
                    w = Math.Clamp((int)tpl.CardSize, 1, 3);
            }
            total += w;
        }
        return total;
    }

    private static bool HasAbnormalDuplicateOrInvalidItems(GhostBoard board, GameDataRepository? repo)
    {
        if (board.Items.Count == 0) return true;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in board.Items)
        {
            if (string.IsNullOrWhiteSpace(it.TemplateId)) return true;
            if (repo != null && repo.GetCard(it.TemplateId) == null) return true;
            counts[it.TemplateId] = counts.GetValueOrDefault(it.TemplateId, 0) + 1;
            if (counts[it.TemplateId] >= 3)
                return true;
        }
        return false;
    }

    /// <summary>计算覆盖全部已放置手牌槽位的有效解锁掩码（Day >= 5 强制全解锁 0x3FF）。</summary>
    public static int ComputeEffectiveUnlockedSlotsMask(GhostBoard ghost, GameDataRepository? repo = null)
    {
        int mask = ghost.Day >= 5 ? 1023 : Math.Max(0, ghost.UnlockedSlotsMask);
        if (ghost.Items != null)
        {
            foreach (var it in ghost.Items)
            {
                int w = Math.Clamp(it.Width, 1, 3);
                if (repo != null)
                {
                    var tpl = repo.GetCard(it.TemplateId);
                    if (tpl != null) w = Math.Clamp((int)tpl.CardSize, 1, 3);
                }
                for (int offset = 0; offset < w; offset++)
                {
                    int slot = it.Slot + offset;
                    if (slot is >= 0 and < 10)
                        mask |= 1 << slot;
                }
            }
        }
        return Math.Max(mask, 1023);
    }

    /// <summary>把 GhostBoard 转换为可供 BazaarCore.CombatSimulator 直接演算的 PlayerState（英雄由 ParseHero 统一解析，掩码覆写防丢卡）。</summary>
    public static PlayerState ToPlayerState(GhostBoard ghost, GameDataRepository repo)
    {
        EHero parsedHero = ParseHero(ghost.Hero);
        string safeHero = parsedHero.ToString();
        string label = ghost.IsPlayerGhost
            ? $"{safeHero}(真人镜像)"
            : $"{safeHero}镜像({ghost.PowerBucket})";

        var ps = new PlayerState(
            "pvp_" + ghost.BoardId,
            label,
            safeHero,
            ghost.HealthMax,
            ghost.Prestige,
            0,
            10)
        {
            Level = Math.Max(1, ghost.Level),
            Wins = ghost.Wins,
            Losses = ghost.Losses,
        };
        // 确保 unlockedSlotsMask 覆写全部已摆放手牌格子（Day >= 5 全 10 格 0x3FF），杜绝战斗前丢卡
        ps.Hand.UnlockedMask = ComputeEffectiveUnlockedSlotsMask(ghost, repo);

        int itemIdx = 0;
        foreach (var it in ghost.Items)
        {
            var tpl = repo.GetCard(it.TemplateId);
            if (tpl == null) continue;
            string prefix = tpl.Id.Length >= 6 ? tpl.Id[..6] : tpl.Id;
            var card = new CardInstance(tpl, TierHelper.ParseTier(it.Tier), it.Enchantment, $"opp_{itemIdx++}_{it.Slot}_{prefix}");
            foreach (var (ak, av) in it.PermanentAttributes)
            {
                card.PermanentAdd[ak] = av - card.BaseAttributes.GetValueOrDefault(ak, 0);
            }
            if (!ps.Hand.TryPlaceOrShift(card, Math.Clamp(it.Slot, 0, 9)))
                ps.Hand.TryAddAuto(card);
        }

        int skillIdx = 0;
        foreach (var sk in ghost.Skills)
        {
            var tpl = repo.GetCard(sk.TemplateId);
            if (tpl == null) continue;
            string prefix = tpl.Id.Length >= 6 ? tpl.Id[..6] : tpl.Id;
            ps.Skills.Add(new CardInstance(tpl, TierHelper.ParseTier(sk.Tier), null, $"oppskl_{skillIdx++}_{prefix}"));
        }

        ps.PrepareForCombat();
        return ps;
    }

    private static GhostBoard BuildFallbackGhost(int day, Random rng, GameDataRepository? repo)
    {
        var heroes = new[] { "Vanessa", "Dooley", "Pygmalien", "Mak", "Karnok", "Jules", "Stelle", "Hero8" };
        string hero = heroes[rng.Next(heroes.Length)];
        int clampedDay = Math.Max(1, day);
        var (minHp, maxHp, maxLv) = GetSaneBoundsForDay(clampedDay);
        int hp = clampedDay == 1
            ? 250
            : Math.Clamp(250 + (clampedDay - 1) * 350, minHp, maxHp);
        var items = new List<GhostItem>();
        if (repo != null)
        {
            var pool = repo.GetSpawnableItemsForHero(hero).Where(c => c.CardSize != CardSize.Large).ToList();
            int slot = 3;
            for (int i = 0; i < 3 && pool.Count > 0 && slot < 7; i++)
            {
                var tpl = pool[rng.Next(pool.Count)];
                int w = tpl.CardSize == CardSize.Medium ? 2 : 1;
                if (slot + w > 8) break;
                items.Add(new GhostItem
                {
                    Slot = slot,
                    Width = w,
                    TemplateId = tpl.Id,
                    InternalName = tpl.InternalName,
                    Tier = clampedDay >= 6 ? "Gold" : clampedDay >= 3 ? "Silver" : "Bronze",
                });
                slot += w;
            }
        }
        return new GhostBoard
        {
            BoardId = $"fallback_d{clampedDay}",
            Day = clampedDay,
            Hero = hero,
            Level = clampedDay,
            HealthMax = hp,
            Prestige = 20,
            UnlockedSlotsMask = clampedDay >= 4 ? 1023 : (clampedDay == 1 ? 120 : 252),
            PowerBucket = "Normal",
            Items = items,
        };
    }

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i : fallback;
}
