using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Data;

/// <summary>
/// 高性能分区 PvP 镜像池（支持无锁只读分桶查询、加载期脏数据清洗、模式分区隔离与无尽高层强化镜像生成）：
/// - 按 <c>(PoolPartition, Day)</c> 预建不可变只读分桶索引，彻底消除高并发全局锁 LINQ 扫描瓶颈；
/// - 加载期即剔除 Day 1 超血/Lv2+ 脏样本与 Day 4/5+ 残缺盘面，直降内存占用并修复 #9 / #25；
/// - 严格依据 <see cref="CustomRunRuleSet.ResolveGhostPoolPartition"/> 隔离标准池与自定义/新模式/沙盒局。
/// </summary>
public sealed class PvpGhostPool
{
    public const int MaxBoardsPerDayBucket = 2500;
    public const string StandardPartition = "standard";

    private static readonly HashSet<string> SupportedClientHeroes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vanessa", "Dooley", "Pygmalien", "Mak", "Karnok", "Jules", "Stelle", "Hero8",
    };

    private static readonly string[] ValidFallbackEnchantments =
    [
        "Heavy", "Icy", "Turbo", "Shielded", "Restorative", "Toxic", "Fiery", "Deadly", "Radiant", "Obsidian"
    ];

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
        public string PoolPartition { get; init; } = StandardPartition;
        public List<GhostItem> Items { get; init; } = new();
        public List<GhostSkill> Skills { get; init; } = new();
    }

    private sealed class StoredGhostBoardPayload
    {
        public string BoardId { get; set; } = "";
        public string PowerBucket { get; set; } = "Normal";
        public double EstimatedWinRate { get; set; } = 0.5;
        public bool IsCustomRule { get; set; }
        public string? PoolPartition { get; set; }
        public List<GhostItem> Items { get; set; } = new();
        public List<GhostSkill> Skills { get; set; } = new();
    }

    private readonly record struct BucketKey(string Partition, int Day);

    private sealed class DayPartitionBucket
    {
        public required GhostBoard[] All { get; init; }
        public required GhostBoard[] StaticPreferred { get; init; }
        public required GhostBoard[] PlayerGhosts { get; init; }
        public required GhostBoard[] HighPowerBoards { get; init; }
    }

    private sealed class GhostIndexSnapshot
    {
        public required FrozenDictionary<BucketKey, DayPartitionBucket> Buckets { get; init; }
        public required GhostBoard[] StandardAll { get; init; }
        public required GhostBoard[] HighDayStrongOrMeta { get; init; }
        public required int TotalCount { get; init; }
        public required int PlayerGhostCount { get; init; }
        public required int CustomRuleGhostCount { get; init; }
    }

    private readonly Dictionary<BucketKey, List<GhostBoard>> _mutableBuckets = new();
    private readonly List<GhostBoard> _all = new();
    private readonly HashSet<string> _knownBoardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();

    private volatile GhostIndexSnapshot _index = BuildEmptySnapshot();

    public int Count => _index.TotalCount;
    public int PlayerGhostCount => _index.PlayerGhostCount;
    public int CustomRuleGhostCount => _index.CustomRuleGhostCount;

    public static PvpGhostPool Load(string dataRoot)
    {
        var pool = new PvpGhostPool();
        string? path = ResolvePath(dataRoot);
        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("[PvpGhostPool] ghost_pool_balanced.jsonl not found; using dynamic fallback");
            return pool;
        }

        int rawLines = 0;
        int droppedDirty = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rawLines++;
            var board = ParseLine(line);
            if (board == null || !IsCleanStaticGhostForLoad(board))
            {
                droppedDirty++;
                continue;
            }
            pool.AddBoardUnlocked(board);
        }

        pool.RebuildReadIndexUnlocked();
        Console.WriteLine(
            $"[PvpGhostPool] loaded {pool._all.Count}/{rawLines} clean ghost boards (dropped {droppedDirty} dirty/incomplete) " +
            $"from {path} ({pool._mutableBuckets.Count} partition-day buckets)");
        return pool;
    }

    /// <summary>
    /// 依据天梯校准曲线返回每日 PvP 自然中位数血量预期（Day 4 ≈ 1000、Day 7 ≈ 2200、Day 10 ≈ 4400）。
    /// </summary>
    public static int GetExpectedMedianHealthForDay(int day) => Math.Max(1, day) switch
    {
        1 => 300,
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
    /// 返回每日合理 PvP 镜像区间 (MinHp, MaxHp, MaxLevel)。
    /// 前期（Day 1..4）严格拦截超血/超等级异常脏镜像；中后期放宽至合法成长上界。
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

    /// <summary>校验指定镜像是否符合该天数合理血量区间与等级上限。</summary>
    public static bool IsSaneGhostForDay(GhostBoard ghost, int day)
    {
        if (ghost == null) return false;
        if (ghost.Items == null || ghost.Items.Count == 0) return false;
        var (minHp, maxHp, maxLv) = GetSaneBoundsForDay(day);
        int effectiveMinHp = ghost.IsPlayerGhost ? Math.Min(100, minHp) : minHp;
        int effectiveMaxHp = (ghost.IsPlayerGhost && day == 1) ? 550 : maxHp;
        int effectiveMaxLv = (ghost.IsPlayerGhost && day == 1) ? 2 : maxLv;
        return ghost.HealthMax >= effectiveMinHp
            && ghost.HealthMax <= effectiveMaxHp
            && ghost.Level >= 1
            && ghost.Level <= effectiveMaxLv;
    }

    /// <summary>
    /// 静态池加载期强力卫生过滤：剔除 Day 1 超血(&gt;550)/Lv3+、不支持英雄、异常同卡>=3件以及 Day 4/5+ 残缺未满编盘面，
    /// 彻底杜绝异常高阶脏样本与残缺样本常驻内存。
    /// </summary>
    private static bool IsCleanStaticGhostForLoad(GhostBoard ghost)
    {
        if (!IsSaneGhostForDay(ghost, ghost.Day)) return false;
        if (!IsSupportedClientHero(ghost.Hero)) return false;
        if (HasAbnormalDuplicateOrInvalidItems(ghost, repo: null)) return false;

        int totalWidth = GetTotalSlotWidth(ghost, repo: null);
        if (ghost.Day <= 1)
            return ghost.Level <= 2 && ghost.HealthMax <= 550 && totalWidth >= 2;
        if (ghost.Day == 2)
            return totalWidth >= 4;
        if (ghost.Day == 3)
            return totalWidth >= 5;
        if (ghost.Day == 4)
            return totalWidth >= 6;
        return totalWidth >= 8;
    }

    /// <summary>从 SQLite pvp_ghosts 表加载已沉淀的真人玩家镜像并热加入对应分区-天数分桶。</summary>
    public int LoadFromDb(Persistence db, bool includeCustomRule = true)
    {
        if (db == null) return 0;
        var rows = db.LoadPvpGhosts(2000, includeCustomRule: includeCustomRule);
        int added = 0;

        lock (_writeLock)
        {
            foreach (var row in rows)
            {
                var board = ParseDbRow(row);
                if (board == null) continue;
                if (!board.IsCustomRule && !IsSaneGhostForDay(board, board.Day)) continue;
                if (AddBoardUnlocked(board)) added++;
            }
            if (added > 0)
                RebuildReadIndexUnlocked();
        }

        if (added > 0)
            Console.WriteLine($"[PvpGhostPool] loaded {added} player ghost boards from db (total={Count})");
        return added;
    }

    /// <summary>
    /// 记录当前对局 Hour 6 PvP 玩家真实阵容到 SQLite pvp_ghosts 并热加载进对应分区。
    /// 严格执行沙盒三禁隔离：SandboxLab 或 <c>poolPartition == "none"</c> 绝不入池！
    /// </summary>
    public GhostBoard? RecordPlayerGhost(
        RunState run,
        Persistence db,
        bool isCustomRule = false,
        string? poolPartition = null)
    {
        if (run == null || db == null) return null;
        if (run.CustomRules?.ShouldSkipGhostPool == true)
            return null;

        string resolvedPartition = NormalizePartition(
            poolPartition ?? run.CustomRules?.ResolveGhostPoolPartition(),
            isCustomRule || run.IsCustomRuleRun);
        if (string.Equals(resolvedPartition, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        bool effectiveCustom = isCustomRule
            || run.IsCustomRuleRun
            || !string.Equals(resolvedPartition, StandardPartition, StringComparison.OrdinalIgnoreCase);

        int day = (int)Math.Max(1u, run.Day);
        string boardId = $"player_{run.RunId}_d{day}";

        lock (_writeLock)
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
            if (entry.CoreCard != null)
            {
                // 仅持久化 CoreCard 真实永久修改层（PermanentAdd / PermanentMult），绝不拷入含光环（Aura）/ 限时层（Hour/Day）
                // 或已按 PercentCooldownReduction 折算后的 CooldownMax，彻底杜绝真人 PvP 镜像双倍光环叠算与双重冷却缩减
                var permKeys = new HashSet<string>(entry.CoreCard.PermanentAdd.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var mk in entry.CoreCard.PermanentMult.Keys)
                    permKeys.Add(mk);

                foreach (string key in permKeys)
                {
                    if (IsDerivedGhostAttributeKey(key))
                        continue;
                    double baseVal = entry.CoreCard.BaseAttributes.GetValueOrDefault(key, 0);
                    double addVal = entry.CoreCard.PermanentAdd.GetValueOrDefault(key, 0);
                    double mult = entry.CoreCard.PermanentMult.GetValueOrDefault(key, 1.0);
                    int unscaled = (int)Math.Round((baseVal + addVal) * mult);
                    if (Math.Abs(unscaled - baseVal) > 0.001)
                        perm[key] = unscaled;
                }
            }
            else
            {
                foreach (var kv in entry.Attributes)
                {
                    string key = kv.Key.ToString();
                    if (IsDerivedGhostAttributeKey(key))
                        continue;
                    perm[key] = kv.Value;
                }
            }

            items.Add(new GhostItem
            {
                Slot = Math.Clamp(slot, 0, 9),
                Width = width,
                TemplateId = string.IsNullOrWhiteSpace(entry.CustomCardId) ? entry.TemplateId.ToString() : entry.CustomCardId!,
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
                TemplateId = string.IsNullOrWhiteSpace(c.CustomCardId) ? c.TemplateId.ToString() : c.CustomCardId!,
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
        int unlockedSlots = run.UnlockedSlots > 0 ? run.UnlockedSlots : DefaultUnlockedMaskForDay(day);
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
            IsCustomRule = effectiveCustom,
            PoolPartition = resolvedPartition,
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
            IsCustomRule = effectiveCustom,
            PoolPartition = resolvedPartition,
            Items = items,
            Skills = skills,
        };

        // 标准分区拒绝超血脏镜像；自定义/无尽高分区分允许其真实强度入专属分区，绝不污染标准池
        if (!effectiveCustom && !IsSaneGhostForDay(board, day))
            return null;

        string boardJson = JsonSerializer.Serialize(payload, JsonOpts);
        var now = DateTime.UtcNow;

        db.InsertPvpGhost(new Persistence.PvpGhostRow(
            0, day, hero, wins, losses, healthMax, prestige, level, unlockedSlots, boardJson, now, effectiveCustom));

        lock (_writeLock)
        {
            if (AddBoardUnlocked(board))
                RebuildReadIndexUnlocked();
        }

        Console.WriteLine(
            $"[PvpGhostPool] recorded player ghost {boardId} (Day={day}, Hero={hero}, W={wins}/L={losses}, " +
            $"Items={items.Count}, Skills={skills.Count}, Partition={resolvedPartition}, CustomRule={effectiveCustom})");
        return board;
    }

    /// <summary>
    /// 按 <c>(poolPartition, day)</c> 无锁快速匹配对手镜像（零锁竞争，亚毫秒级返回）：
    /// - 标准模式（<c>partition == "standard"</c>）100% 屏蔽任何非标准/自定义/Mod 镜像；
    /// - 无尽爬塔 Day 11+ 若同层专属样本不足 5 条，自动基于 Day 10~15 强力镜像按 <see cref="EndlessTowerScaler"/> 升阶附魔强化；
    /// - 向后兼容旧 6 参数签名。
    /// </summary>
    public GhostBoard PickOpponent(
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo = null,
        bool includeCustomRuleGhosts = false,
        string? poolPartition = null)
    {
        var snap = _index;
        int effectiveDay = Math.Max(1, day);
        string targetPartition = NormalizePartition(poolPartition, includeCustomRuleGhosts);

        // 1. 无尽爬塔高层（Day 11+）：优先专属 endless_tower 同层真人池（>=5条），否则由 Day 10~15 Strong/MetaBoss 升阶附魔构造
        if (string.Equals(targetPartition, "endless_tower", StringComparison.OrdinalIgnoreCase) && effectiveDay > 10)
        {
            if (snap.Buckets.TryGetValue(new BucketKey("endless_tower", effectiveDay), out var endlessBucket)
                && endlessBucket.All.Length >= 5)
            {
                var picked = PickFromBucket(endlessBucket, effectiveDay, playerWins, playerLosses, rng, repo);
                if (picked != null) return picked;
            }

            var baseStrong = snap.HighDayStrongOrMeta.Length > 0
                ? snap.HighDayStrongOrMeta[rng.Next(snap.HighDayStrongOrMeta.Length)]
                : PickStandardBucketOrFallback(snap, Math.Clamp(effectiveDay, 10, 15), playerWins, playerLosses, rng, repo);
            return BuildScaledEndlessGhost(baseStrong, effectiveDay, rng, repo);
        }

        // 2. 非标准专属分区优先匹配同分区同日真人镜像，不足时只读借用标准静态池（绝不反向污染标准池）
        if (!string.Equals(targetPartition, StandardPartition, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPartition, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (snap.Buckets.TryGetValue(new BucketKey(targetPartition, effectiveDay), out var customBucket)
                && customBucket.All.Length > 0)
            {
                // 同分区样本充足（>=3）或 50% 概率优先展示同模式真人对手
                if (customBucket.All.Length >= 3 || rng.NextDouble() < 0.50)
                {
                    var picked = PickFromBucket(customBucket, effectiveDay, playerWins, playerLosses, rng, repo);
                    if (picked != null) return picked;
                }
            }
        }

        // 3. 标准分区（或自定义分区样本不足时的只读回退）
        return PickStandardBucketOrFallback(snap, effectiveDay, playerWins, playerLosses, rng, repo);
    }

    private static GhostBoard PickStandardBucketOrFallback(
        GhostIndexSnapshot snap,
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo)
    {
        if (snap.Buckets.TryGetValue(new BucketKey(StandardPartition, day), out var exactBucket))
        {
            var picked = PickFromBucket(exactBucket, day, playerWins, playerLosses, rng, repo);
            if (picked != null) return picked;
        }

        // 若天数超过静态桶上限（如 Day 16+），向前探测最近有样本的标准桶
        if (day > 15)
        {
            for (int probe = Math.Min(day, 20); probe >= 10; probe--)
            {
                if (snap.Buckets.TryGetValue(new BucketKey(StandardPartition, probe), out var nearBucket))
                {
                    var picked = PickFromBucket(nearBucket, probe, playerWins, playerLosses, rng, repo);
                    if (picked != null) return picked;
                }
            }
        }

        if (day <= 1)
            return BuildFallbackGhost(1, rng, repo);

        // 全标准集内按合理区间回退
        var eligible = new List<GhostBoard>();
        foreach (var b in snap.StandardAll)
        {
            if (IsSaneGhostForDay(b, day) && !HasInvalidRepoItem(b, repo))
                eligible.Add(b);
        }
        if (eligible.Count > 0)
            return PickByMedianBandDistribution(eligible, Array.Empty<GhostBoard>(), day, rng);

        return BuildFallbackGhost(day, rng, repo);
    }

    private static GhostBoard? PickFromBucket(
        DayPartitionBucket bucket,
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo)
    {
        var staticSource = bucket.StaticPreferred.Length > 0 ? bucket.StaticPreferred : bucket.All;
        if (staticSource.Length == 0) return null;

        string[] preferredBuckets = playerLosses >= playerWins + 2
            ? ["Weak", "Normal"]
            : (day >= 3 && playerWins >= day - 1)
                ? ["Strong", "Normal", "MetaBoss"]
                : ["Normal", "Strong"];

        var tier1 = new List<GhostBoard>();
        var tier2 = new List<GhostBoard>();
        var fallback = new List<GhostBoard>();

        for (int i = 0; i < staticSource.Length; i++)
        {
            var b = staticSource[i];
            if (HasInvalidRepoItem(b, repo)) continue;
            if (day <= 1 && (b.Level > 2 || b.HealthMax > 550)) continue;

            fallback.Add(b);
            bool bucketHit = ContainsBucket(preferredBuckets, b.PowerBucket);
            int winDiff = Math.Abs(b.Wins - playerWins);
            if (bucketHit && winDiff <= 1)
                tier1.Add(b);
            else if (bucketHit || winDiff <= 2)
                tier2.Add(b);
        }

        var chosenPool = tier1.Count > 0 ? tier1 : (tier2.Count > 0 ? tier2 : fallback);
        if (chosenPool.Count == 0) return null;

        var closePlayers = new List<GhostBoard>();
        var anyPlayers = new List<GhostBoard>();
        for (int i = 0; i < bucket.PlayerGhosts.Length; i++)
        {
            var p = bucket.PlayerGhosts[i];
            if (HasInvalidRepoItem(p, repo)) continue;
            if (day <= 1 && (p.Level > 2 || p.HealthMax > 550)) continue;
            anyPlayers.Add(p);
            if (Math.Abs(p.Wins - playerWins) <= 2)
                closePlayers.Add(p);
        }

        var playerPool = closePlayers.Count > 0 ? closePlayers : anyPlayers;
        return PickByMedianBandDistribution(chosenPool, playerPool, day, rng);
    }

    private static bool ContainsBucket(string[] buckets, string target)
    {
        for (int i = 0; i < buckets.Length; i++)
        {
            if (string.Equals(buckets[i], target, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasInvalidRepoItem(GhostBoard board, GameDataRepository? repo)
    {
        if (repo == null) return false;
        for (int i = 0; i < board.Items.Count; i++)
        {
            if (repo.GetCard(board.Items[i].TemplateId) == null)
                return true;
        }
        return false;
    }

    private static GhostBoard BuildScaledEndlessGhost(
        GhostBoard source,
        int targetDay,
        Random rng,
        GameDataRepository? repo)
    {
        var scaling = EndlessTowerScaler.Compute(targetDay);
        var upgradedItems = new List<GhostItem>(source.Items.Count);
        int enchantBudget = scaling.GuaranteedRandomEnchantCount;

        for (int i = 0; i < source.Items.Count; i++)
        {
            var it = source.Items[i];
            var curTier = TierHelper.ParseTier(it.Tier);
            var targetTier = curTier < scaling.MinimumGhostItemTier ? scaling.MinimumGhostItemTier : curTier;
            var tpl = repo?.GetCard(it.TemplateId);
            if (tpl != null)
                targetTier = SpawnQueryEngine.ClampToSupportedTier(tpl, targetTier);

            string? ench = it.Enchantment;
            if (string.IsNullOrWhiteSpace(ench) && enchantBudget > 0)
            {
                ench = PickLegalEnchantment(tpl, rng);
                if (!string.IsNullOrWhiteSpace(ench))
                    enchantBudget--;
            }

            upgradedItems.Add(new GhostItem
            {
                Slot = it.Slot,
                Width = it.Width,
                TemplateId = it.TemplateId,
                InternalName = it.InternalName,
                ZhName = it.ZhName,
                Tier = targetTier.ToString(),
                Enchantment = ench,
                PermanentAttributes = new Dictionary<string, int>(it.PermanentAttributes, StringComparer.OrdinalIgnoreCase),
            });
        }

        return new GhostBoard
        {
            BoardId = $"endless_d{targetDay}_{source.BoardId}",
            Day = targetDay,
            Hero = source.Hero,
            Level = Math.Max(source.Level, targetDay),
            HealthMax = source.HealthMax, // 具体倍率由 CombatModifiers.FromRulesAndDay 统一缩放，防止双重乘算
            Prestige = Math.Max(1, source.Prestige),
            Wins = Math.Max(source.Wins, targetDay - 1),
            Losses = source.Losses,
            UnlockedSlotsMask = 1023,
            PowerBucket = "MetaBoss",
            EstimatedWinRate = Math.Max(0.75, source.EstimatedWinRate),
            IsPlayerGhost = source.IsPlayerGhost,
            IsCustomRule = true,
            PoolPartition = "endless_tower",
            Items = upgradedItems,
            Skills = new List<GhostSkill>(source.Skills),
        };
    }

    private static string? PickLegalEnchantment(CardTemplate? tpl, Random rng)
    {
        if (tpl?.Enchantments is { Count: > 0 })
        {
            var keys = tpl.Enchantments.Keys.ToList();
            return keys[rng.Next(keys.Count)];
        }
        return ValidFallbackEnchantments[rng.Next(ValidFallbackEnchantments.Length)];
    }

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

    private bool AddBoardUnlocked(GhostBoard board)
    {
        if (!string.IsNullOrEmpty(board.BoardId) && !_knownBoardIds.Add(board.BoardId))
            return false;

        int bucketDay = Math.Clamp(board.Day, 1, 99);
        string partition = NormalizePartition(board.PoolPartition, board.IsCustomRule);
        var key = new BucketKey(partition, bucketDay);
        if (!_mutableBuckets.TryGetValue(key, out var list))
        {
            list = new List<GhostBoard>();
            _mutableBuckets[key] = list;
        }
        list.Add(board);
        _all.Add(board);
        TrimDayBucketUnlocked(list);
        return true;
    }

    private void TrimDayBucketUnlocked(List<GhostBoard> dayList)
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

    private void RebuildReadIndexUnlocked()
    {
        var buckets = new Dictionary<BucketKey, DayPartitionBucket>(_mutableBuckets.Count);
        var standardAll = new List<GhostBoard>();
        var highDayStrong = new List<GhostBoard>();
        int playerCount = 0;
        int customCount = 0;

        foreach (var (key, list) in _mutableBuckets)
        {
            var allArr = list.ToArray();
            var playerList = new List<GhostBoard>();
            var full10List = new List<GhostBoard>();
            var highPower = new List<GhostBoard>();

            for (int i = 0; i < allArr.Length; i++)
            {
                var b = allArr[i];
                if (b.IsPlayerGhost) playerList.Add(b);
                if (key.Day >= 4 && GetTotalSlotWidth(b, null) >= 10)
                    full10List.Add(b);
                if (string.Equals(b.PowerBucket, "Strong", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b.PowerBucket, "MetaBoss", StringComparison.OrdinalIgnoreCase))
                {
                    highPower.Add(b);
                }
            }

            buckets[key] = new DayPartitionBucket
            {
                All = allArr,
                StaticPreferred = full10List.Count > 0 ? full10List.ToArray() : allArr,
                PlayerGhosts = playerList.ToArray(),
                HighPowerBoards = highPower.ToArray(),
            };

            if (string.Equals(key.Partition, StandardPartition, StringComparison.OrdinalIgnoreCase))
            {
                standardAll.AddRange(allArr);
                if (key.Day is >= 10 and <= 15)
                    highDayStrong.AddRange(highPower.Count > 0 ? highPower : allArr);
            }
        }

        for (int i = 0; i < _all.Count; i++)
        {
            if (_all[i].IsPlayerGhost) playerCount++;
            if (_all[i].IsCustomRule) customCount++;
        }

        _index = new GhostIndexSnapshot
        {
            Buckets = buckets.ToFrozenDictionary(),
            StandardAll = standardAll.ToArray(),
            HighDayStrongOrMeta = highDayStrong.ToArray(),
            TotalCount = _all.Count,
            PlayerGhostCount = playerCount,
            CustomRuleGhostCount = customCount,
        };
    }

    private static GhostIndexSnapshot BuildEmptySnapshot() => new()
    {
        Buckets = FrozenDictionary<BucketKey, DayPartitionBucket>.Empty,
        StandardAll = Array.Empty<GhostBoard>(),
        HighDayStrongOrMeta = Array.Empty<GhostBoard>(),
        TotalCount = 0,
        PlayerGhostCount = 0,
        CustomRuleGhostCount = 0,
    };

    private static string NormalizePartition(string? partition, bool isCustomRule)
    {
        if (!string.IsNullOrWhiteSpace(partition))
        {
            string norm = partition.Trim().ToLowerInvariant();
            // 严防自定义规则镜像误入标准分区：显式标记 isCustomRule=true 时绝不允许落在 "standard"
            if (isCustomRule && string.Equals(norm, StandardPartition, StringComparison.OrdinalIgnoreCase))
                return "custom";
            return norm;
        }
        return isCustomRule ? "custom" : StandardPartition;
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
            bool isCustom = row.IsCustomRule || payload.IsCustomRule;
            string partition = NormalizePartition(payload.PoolPartition, isCustom);

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
                UnlockedSlotsMask = row.UnlockedSlots > 0 ? row.UnlockedSlots : DefaultUnlockedMaskForDay(row.Day),
                PowerBucket = string.IsNullOrWhiteSpace(payload.PowerBucket) ? "Normal" : payload.PowerBucket,
                EstimatedWinRate = payload.EstimatedWinRate > 0 ? payload.EstimatedWinRate : 0.5,
                IsPlayerGhost = true,
                IsCustomRule = isCustom,
                PoolPartition = partition,
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

        string repoOutput = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output", "pvp_dataset", "ghost_pool_balanced.jsonl"));
        if (File.Exists(repoOutput)) return repoOutput;

        string cwdDist = Path.Combine(Environment.CurrentDirectory, "dist", "data", "ghost_pool_balanced.jsonl");
        if (File.Exists(cwdDist)) return cwdDist;

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

            int day = Math.Max(1, GetInt(r, "day", 1));
            return new GhostBoard
            {
                BoardId = GetStr(r, "boardId") ?? "",
                Day = day,
                Hero = GetStr(r, "hero") ?? "Vanessa",
                Level = GetInt(r, "level", 1),
                HealthMax = Math.Max(100, GetInt(r, "healthMax", 250)),
                Prestige = GetInt(r, "prestige", 20),
                Wins = GetInt(r, "wins", 0),
                Losses = GetInt(r, "losses", 0),
                UnlockedSlotsMask = GetInt(r, "unlockedSlotsMask", DefaultUnlockedMaskForDay(day)),
                PowerBucket = GetStr(r, "powerBucket") ?? "Normal",
                EstimatedWinRate = r.TryGetProperty("estimatedWinRate", out var ew) && ew.TryGetDouble(out double d) ? d : 0.5,
                IsPlayerGhost = false,
                IsCustomRule = false,
                PoolPartition = StandardPartition,
                Items = items,
                Skills = skills,
            };
        }
        catch
        {
            return null;
        }
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

    /// <summary>按官方成长节奏推导指定天数的基准解锁地毯掩码（Day 1=4格, Day 2=6格, Day 3=8格, Day 4+=10格）。</summary>
    public static int DefaultUnlockedMaskForDay(int day) => day switch
    {
        <= 1 => 0b00_0111_1000, // 120 (Socket_3..6, 4格)
        2 => 0b00_1111_1100,    // 252 (Socket_2..7, 6格)
        3 => 0b01_1111_1110,    // 510 (Socket_1..8, 8格)
        _ => 1023,              // 1023 (Socket_0..9, 10格)
    };

    /// <summary>
    /// 修复 P1-2：彻底移除末尾 <c>Math.Max(mask, 1023)</c> 硬解锁 Bug！
    /// 依据天数（Day 1=4格、Day 2=6格、Day 3=8格、Day >= 4=10格）与实际物品占格取并集，
    /// 既防止 Day 1 对手异常解锁 10 格，又确保所有随身装备无损落盘。
    /// </summary>
    public static int ComputeEffectiveUnlockedSlotsMask(GhostBoard ghost, GameDataRepository? repo = null)
    {
        int dayBaseline = DefaultUnlockedMaskForDay(ghost.Day);
        int rawMask = ghost.UnlockedSlotsMask > 0 ? (ghost.UnlockedSlotsMask & 1023) : dayBaseline;
        int mask = ghost.Day >= 4 ? 1023 : (dayBaseline | rawMask);

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
        return mask & 1023;
    }

    /// <summary>把 GhostBoard 转换为可供 BazaarCore.CombatSimulator 直接演算的 PlayerState。</summary>
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
                if (IsDerivedGhostAttributeKey(ak))
                    continue;
                double baseVal = card.BaseAttributes.GetValueOrDefault(ak, 0);
                double delta = av - baseVal;
                if (Math.Abs(delta) > 0.001)
                    card.PermanentAdd[ak] = delta;
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
        var (minHp, maxHp, _) = GetSaneBoundsForDay(clampedDay);
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
            Level = Math.Clamp(clampedDay + 1, 2, 20),
            HealthMax = hp,
            Prestige = 20,
            UnlockedSlotsMask = DefaultUnlockedMaskForDay(clampedDay),
            PowerBucket = "Normal",
            PoolPartition = StandardPartition,
            Items = items,
        };
    }

    private static bool IsDerivedGhostAttributeKey(string key)
        => string.Equals(key, "Ammo", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "BuyPrice", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "QuestCompletedCount", StringComparison.OrdinalIgnoreCase);

    private static string? GetStr(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}
