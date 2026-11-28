using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using Microsoft.Win32.SafeHandles;

namespace BazaarServer.Data;

/// <summary>
/// 高性能分区 PvP 镜像池（支持无锁只读分桶查询、加载期脏数据清洗、模式分区隔离与无尽高层强化镜像生成）：
/// - 按 <c>(PoolPartition, Day)</c> 预建不可变只读分桶索引，彻底消除高并发全局锁 LINQ 扫描瓶颈；
/// - 加载期即剔除 Day 1 超血/Lv2+ 脏样本与 Day 4/5+ 残缺盘面，直降内存占用并修复 #9 / #25；
/// - 严格依据 <see cref="ResolveGhostPoolPartitionLocal"/>（合流胶水 #5 委派 ServerModHost）统一隔离标准池与自定义/新模式/Mod/沙盒局。
/// </summary>
public sealed class PvpGhostPool
{
    public const int MaxBoardsPerDayBucket = 2500;
    public const string StandardPartition = "standard";

    private static readonly HashSet<string> SupportedClientHeroes = new(HeroNames.PlayableHeroes, StringComparer.OrdinalIgnoreCase);

    /// <summary>D2-P1-02 / P0-30：精确判断对局是否包含真正的自定义 Mod 卡（排除原版卡与原位 Patch 卡）。</summary>
    public static bool HasTrueCustomCard(RunState? run)
    {
        if (run == null) return false;
        foreach (var c in run.Cards.Values)
        {
            if (!string.IsNullOrWhiteSpace(c.CustomCardId) &&
                !string.Equals(c.CustomCardId, c.TemplateId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// P0-30（合流胶水 #5）：统一幽灵池分区解析，直接委托单一权威纯函数 ServerModHost.ResolveEffectiveGhostPoolPartition。
    /// </summary>
    public static string ResolveGhostPoolPartitionLocal(
        CustomRunRuleSet? rules,
        bool hasTrueCustomCardInRun,
        ServerModHost? modHost)
        => ServerModHost.ResolveEffectiveGhostPoolPartition(rules, rules?.SelectedModIds, hasTrueCustomCardInRun, modHost);

    /// <summary>D3-P1-12（合流胶水 #5）：统一有效 Mod 集哈希，直接委托单一权威函数 ServerModHost.ComputeEffectiveModSetHash。</summary>
    public static string ComputeEffectiveModHashLocal(IReadOnlyList<string>? selectedModIds, ServerModHost? modHost)
        => ServerModHost.ComputeEffectiveModSetHash(selectedModIds, modHost);

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
        // D2-P1-10：向后兼容补齐 6 类真实战力字段（严守 CardInstance 实码边界，仅持久化真实存在的双层 Add/Mult、任务与标签）
        public Dictionary<string, double>? PermanentAdd { get; init; }
        public Dictionary<string, double>? PermanentMult { get; init; }
        public List<string>? CompletedQuests { get; init; }
        public Dictionary<string, double>? QuestProgress { get; init; }
        public List<string>? ExtraTags { get; init; }
    }

    public sealed class GhostSkill
    {
        public string TemplateId { get; init; } = "";
        public string InternalName { get; init; } = "";
        public string ZhName { get; init; } = "";
        public string Tier { get; init; } = "Bronze";
        // D2-P1-10：成长型技能永久属性与标签
        public Dictionary<string, int>? PermanentAttributes { get; init; }
        public Dictionary<string, double>? PermanentAdd { get; init; }
        public Dictionary<string, double>? PermanentMult { get; init; }
        public List<string>? ExtraTags { get; init; }
    }

    public sealed class GhostSocketEffect
    {
        public int Slot { get; init; }
        public string TemplateId { get; init; } = "";
        public string Tier { get; init; } = "Bronze";
        public string? Enchantment { get; init; }
        public Dictionary<string, int>? PermanentAttributes { get; init; }
    }

    public sealed class GhostBoard
    {
        public string BoardId { get; init; } = "";
        public int Day { get; init; } = 1;
        /// <summary>P0-43：镜像原始录制天数（0 表示历史未填字段，等价于 Day）。</summary>
        public int SourceDay { get; init; }
        /// <summary>P0-43：无尽爬塔外推目标挑战层数（0 表示未外推，等价于 Day）。</summary>
        public int TargetDay { get; init; }
        [JsonIgnore]
        public int EffectiveSourceDay => SourceDay > 0 ? SourceDay : Math.Max(1, Day);
        [JsonIgnore]
        public int EffectiveTargetDay => TargetDay > 0 ? TargetDay : Math.Max(1, Day);
        public string Hero { get; init; } = "Vanessa";
        public int Level { get; init; } = 1;
        public int HealthMax { get; init; } = GameDefaults.StartingHealth;
        public int Prestige { get; init; } = GameDefaults.StartingPrestige;
        public int Wins { get; init; }
        public int Losses { get; init; }
        public int UnlockedSlotsMask { get; init; } = GameDefaults.InitialUnlockedSlotsMask;
        public string PowerBucket { get; init; } = "Normal";
        public double EstimatedWinRate { get; init; } = 0.5;
        public string Source { get; init; } = "";
        public string Persona { get; init; } = "";
        public string Archetype { get; init; } = "";
        /// <summary>自定义对手展示名牌（可选，≤24 字；null 表示使用默认英雄名）。</summary>
        public string? DisplayName { get; init; }
        public bool IsPlayerGhost { get; init; }
        public bool IsCustomRule { get; init; }
        public string PoolPartition { get; init; } = StandardPartition;
        /// <summary>录制该镜像时的底层 GameData.db 版本指纹（单一权威持久化于 SQLite pvp_ghosts.game_data_hash 列，绝不写入 JSON）。</summary>
        [JsonIgnore]
        public string GameDataHash { get; init; } = "";
        public List<GhostItem> Items { get; init; } = new();
        public List<GhostSkill> Skills { get; init; } = new();
        public List<GhostSocketEffect>? SocketEffects { get; init; }

        /// <summary>是否来自真实玩家社区/天梯数据源（外部天梯统计源 / 巴扎++ 十胜库 / BazaarHub 实盘）。</summary>
        [JsonIgnore]
        public bool IsRealCommunityGhost =>
            !IsPlayerGhost &&
            (Source.StartsWith("real_", StringComparison.OrdinalIgnoreCase)
             || Source.StartsWith("bazaarplusplus", StringComparison.OrdinalIgnoreCase)
             || Source.StartsWith("compass", StringComparison.OrdinalIgnoreCase)
             || Source.StartsWith("bazaarhub", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>跨版本/旧锚点读时清洗审计聚合键。</summary>
    public readonly record struct GhostSanitizationAuditKey(
        string Source,
        string TemplateId,
        string Attribute,
        string Reason);

    /// <summary>跨版本/旧锚点读时清洗审计汇总快照。</summary>
    public sealed class GhostSanitizationAuditSummary
    {
        public int ScannedBoards { get; init; }
        public int AffectedBoards { get; init; }
        public long TotalCleanedEntries { get; init; }
        public IReadOnlyDictionary<GhostSanitizationAuditKey, long> CountsByKey { get; init; }
            = new Dictionary<GhostSanitizationAuditKey, long>();
        public IReadOnlyDictionary<string, long> CountsByReason { get; init; }
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>线程安全的读时清洗审计收集器（按 (source, templateId, attr, reason) 聚合计数）。</summary>
    public sealed class GhostSanitizationAuditCollector
    {
        private readonly ConcurrentDictionary<GhostSanitizationAuditKey, long> _counts = new();
        private int _scannedBoards;
        private int _affectedBoards;

        public void RecordBoardScanned(bool wasAffected)
        {
            Interlocked.Increment(ref _scannedBoards);
            if (wasAffected)
                Interlocked.Increment(ref _affectedBoards);
        }

        public void Record(string source, string templateId, string attribute, string reason)
        {
            var key = new GhostSanitizationAuditKey(source, templateId, attribute, reason);
            _counts.AddOrUpdate(key, 1L, static (_, old) => old + 1L);
        }

        public GhostSanitizationAuditSummary BuildSummary()
        {
            var snapshot = new Dictionary<GhostSanitizationAuditKey, long>(_counts);
            var byReason = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var (k, v) in snapshot)
            {
                total += v;
                byReason[k.Reason] = byReason.GetValueOrDefault(k.Reason, 0L) + v;
            }
            return new GhostSanitizationAuditSummary
            {
                ScannedBoards = Volatile.Read(ref _scannedBoards),
                AffectedBoards = Volatile.Read(ref _affectedBoards),
                TotalCleanedEntries = total,
                CountsByKey = snapshot,
                CountsByReason = byReason,
            };
        }
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
        public List<GhostSocketEffect>? SocketEffects { get; set; }
    }

    private readonly record struct BucketKey(string Partition, int Day);

    [Flags]
    private enum GhostEntryFlags : byte
    {
        None = 0,
        IsPlayerGhost = 1 << 0,
        IsCustomRule = 1 << 1,
        IsRealCommunityGhost = 1 << 2,
        IsFull10Slots = 1 << 3,
        IsHighPower = 1 << 4,
        IsLegacyAnchor = 1 << 5,
    }

    private const byte PowerBucketNormal = 0;
    private const byte PowerBucketStrong = 1;
    private const byte PowerBucketWeak = 2;
    private const byte PowerBucketMetaBoss = 3;

    private const byte HeroCodeUnknown = 0;
    private const byte HeroCodeVanessa = 1;
    private const byte HeroCodePygmalien = 2;
    private const byte HeroCodeDooley = 3;
    private const byte HeroCodeMak = 4;
    private const byte HeroCodeJules = 5;
    private const byte HeroCodeStelle = 6;
    private const byte HeroCodeKarnok = 7;
    private const byte HeroCodeHero8 = 8;

    private const byte SourceCodeUnknown = 0;
    private const byte SourceCodeCommunity = 1;
    private const byte SourceCodePlayer = 2;
    private const byte SourceCodeAi = 3;

    public const int DefaultLruCacheCapacity = 256;

    /// <summary>
    /// Wave 4A #7：轻量索引项。磁盘静态 JSONL 样本仅保留文件字节偏移 + 长度与筛选元数据（不常驻 GhostItem/Dictionary 对象图），
    /// 内存注入/SQLite 真人镜像样本则直接持有 InMemoryBoard，双轨统一进入分桶与严格等概率抽样。
    /// </summary>
    private sealed class GhostIndexEntry
    {
        public string BoardId { get; init; } = "";
        public string Partition { get; init; } = StandardPartition;
        public long FileOffset { get; init; } = -1;
        public int ByteLength { get; init; }
        public int HealthMax { get; init; }
        public ushort Day { get; init; }
        public byte Level { get; init; }
        public byte Wins { get; init; }
        public byte PowerBucketCode { get; init; }
        public byte HeroCode { get; init; }
        public byte SourceCode { get; init; }
        public GhostEntryFlags Flags { get; init; }
        public GhostBoard? InMemoryBoard { get; init; }

        public bool IsPlayerGhost => (Flags & GhostEntryFlags.IsPlayerGhost) != 0;
        public bool IsCustomRule => (Flags & GhostEntryFlags.IsCustomRule) != 0;
        public bool IsRealCommunityGhost => (Flags & GhostEntryFlags.IsRealCommunityGhost) != 0;
        public bool IsFull10Slots => (Flags & GhostEntryFlags.IsFull10Slots) != 0;
        public bool IsHighPower => (Flags & GhostEntryFlags.IsHighPower) != 0;
        public bool IsLegacyAnchor => (Flags & GhostEntryFlags.IsLegacyAnchor) != 0;
        public bool IsDiskBacked => InMemoryBoard == null && FileOffset >= 0 && ByteLength > 0;
    }

    private sealed class DayPartitionBucket
    {
        public required GhostIndexEntry[] All { get; init; }
        public required GhostIndexEntry[] StaticPreferred { get; init; }
        public required GhostIndexEntry[] RealCommunityPreferred { get; init; }
        public required GhostIndexEntry[] PlayerGhosts { get; init; }
        public required GhostIndexEntry[] HighPowerBoards { get; init; }
    }

    private sealed class GhostIndexSnapshot
    {
        public required FrozenDictionary<BucketKey, DayPartitionBucket> Buckets { get; init; }
        public required GhostIndexEntry[] StandardAll { get; init; }
        public required GhostIndexEntry[] HighDayStrongOrMeta { get; init; }
        public required GhostIndexEntry[] AllEntries { get; init; }
        public required FrozenDictionary<string, GhostIndexEntry> ByBoardId { get; init; }
        public required int TotalCount { get; init; }
        public required int PlayerGhostCount { get; init; }
        public required int CustomRuleGhostCount { get; init; }
        public required int DiskIndexedCount { get; init; }
    }

    /// <summary>按 (SourceFilePath, FileOffset) 持久化的磁盘 JSONL 模板编号缓存（跨快照复用，避免真人镜像入池触发重扫磁盘）。</summary>
    private sealed class DiskKeywordCache
    {
        public required string SourceFilePath { get; init; }
        public required FrozenDictionary<string, int> TemplateToCode { get; init; }
        public required FrozenDictionary<long, int[]> CodesByFileOffset { get; init; }
    }

    /// <summary>绑定到特定 <see cref="GhostIndexSnapshot"/> 实例的紧凑物品/技能模板关键词索引（懒加载）。</summary>
    private sealed class KeywordIndexData
    {
        public required GhostIndexSnapshot BoundSnapshot { get; init; }
        public DiskKeywordCache? DiskCache { get; init; }
        public required FrozenDictionary<string, int> TemplateToCode { get; init; }
        public required int[][] EntryTemplateCodes { get; init; }
    }

    /// <summary>有界线程安全 LRU 热点缓存（按 FileOffset 键缓存按需反序列化的 GhostBoard，锁内仅操作 O(1) 字典与链表指针）。</summary>
    private sealed class BoundedGhostLruCache
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly Dictionary<long, LinkedListNode<(long Key, GhostBoard Value)>> _map;
        private readonly LinkedList<(long Key, GhostBoard Value)> _order = new();
        private long _hits;
        private long _misses;

        public BoundedGhostLruCache(int capacity)
        {
            _capacity = Math.Max(16, capacity);
            _map = new Dictionary<long, LinkedListNode<(long Key, GhostBoard Value)>>(_capacity);
        }

        public int Capacity => _capacity;

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        public long HitCount => Volatile.Read(ref _hits);
        public long MissCount => Volatile.Read(ref _misses);

        public bool TryGet(long offset, out GhostBoard? board)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(offset, out var node))
                {
                    if (!ReferenceEquals(_order.First, node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                    }
                    Interlocked.Increment(ref _hits);
                    board = node.Value.Value;
                    return true;
                }
            }
            Interlocked.Increment(ref _misses);
            board = null;
            return false;
        }

        public void Put(long offset, GhostBoard board)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(offset, out var existing))
                {
                    existing.Value = (offset, board);
                    if (!ReferenceEquals(_order.First, existing))
                    {
                        _order.Remove(existing);
                        _order.AddFirst(existing);
                    }
                    return;
                }

                if (_map.Count >= _capacity && _order.Last != null)
                {
                    var tail = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(tail.Value.Key);
                    tail.Value = (offset, board);
                    _order.AddFirst(tail);
                    _map[offset] = tail;
                    return;
                }

                var node = new LinkedListNode<(long Key, GhostBoard Value)>((offset, board));
                _order.AddFirst(node);
                _map[offset] = node;
            }
        }
    }

    private readonly Dictionary<BucketKey, List<GhostIndexEntry>> _mutableBuckets = new();
    private readonly List<GhostIndexEntry> _all = new();
    private readonly HashSet<string> _knownBoardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private readonly BoundedGhostLruCache _lru;
    private volatile string? _sourceFilePath;

    private volatile GhostIndexSnapshot _index = BuildEmptySnapshot();
    private readonly object _keywordIndexLock = new();
    private volatile KeywordIndexData? _keywordIndex;
    private volatile DiskKeywordCache? _diskKeywordCache;
    private bool _keywordIndexBuilding;
    private volatile string _currentGameDataHash = "";

    private static volatile string _activeGameDataHash = "";
    private static readonly GhostSanitizationAuditCollector RuntimeSanitizationAudit = new();
    private static int _runtimeFirstAuditLogged;

    public PvpGhostPool()
        : this(DefaultLruCacheCapacity)
    {
    }

    public PvpGhostPool(int lruCacheCapacity)
    {
        _lru = new BoundedGhostLruCache(lruCacheCapacity);
    }

    /// <summary>当前服务实例加载的 GameData.db 短 SHA-256 指纹（16 位小写十六进制；未知时为空串）。</summary>
    public string CurrentGameDataHash => _currentGameDataHash;

    /// <summary>进程级当前激活的 GameData.db 短指纹（供静态 ToPlayerState 2 参调用读取）。</summary>
    public static string ActiveGameDataHash => _activeGameDataHash;

    /// <summary>注入当前运行期的底层 GameData.db 版本指纹（用于跨版本读时清洗判定与新录制镜像落库）。</summary>
    public void SetCurrentGameDataHash(string? hash)
    {
        string normalized = string.IsNullOrWhiteSpace(hash) ? "" : hash.Trim().ToLowerInvariant();
        _currentGameDataHash = normalized;
        _activeGameDataHash = normalized;
        Console.WriteLine($"[PvpGhostPool] GameDataHash={(string.IsNullOrEmpty(normalized) ? "<empty>" : normalized)}");
    }

    /// <summary>获取运行期累计的读时清洗审计汇总快照。</summary>
    public static GhostSanitizationAuditSummary GetRuntimeSanitizationAuditSummary()
        => RuntimeSanitizationAudit.BuildSummary();

    /// <summary>
    /// 在服务启动或审计测试中扫描池内全部旧版本罗盘锚点（real_compass_anchor）与跨版本已加载真人镜像，
    /// 直接按磁盘偏移只读扫描且不污染热点 LRU 缓存，输出按 (source, templateId, attr, reason) 聚合的清洗审计汇总。
    /// </summary>
    public GhostSanitizationAuditSummary RunStartupSanitizationAudit(GameDataRepository repo, bool logSummary = true)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var collector = new GhostSanitizationAuditCollector();
        var entries = _index.AllEntries;
        string currentHash = _currentGameDataHash;

        string? filePath = _sourceFilePath;
        SafeFileHandle? handle = null;
        byte[]? rented = null;
        try
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                GhostBoard? board = null;
                if (entry.InMemoryBoard != null)
                {
                    var mem = entry.InMemoryBoard;
                    bool needAudit = string.Equals(mem.Source, "real_compass_anchor", StringComparison.OrdinalIgnoreCase)
                        || (!IsExemptFromCrossVersionSanitization(mem.Source)
                            && mem.IsPlayerGhost
                            && !string.IsNullOrEmpty(currentHash)
                            && !string.Equals(mem.GameDataHash, currentHash, StringComparison.OrdinalIgnoreCase));
                    if (!needAudit) continue;
                    board = mem;
                }
                else if (entry.IsLegacyAnchor && entry.IsDiskBacked)
                {
                    if (handle == null)
                    {
                        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                            continue;
                        handle = File.OpenHandle(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            FileOptions.RandomAccess);
                    }
                    if (rented == null || rented.Length < entry.ByteLength)
                    {
                        if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                        rented = ArrayPool<byte>.Shared.Rent(Math.Max(entry.ByteLength, 8192));
                    }
                    int totalRead = 0;
                    while (totalRead < entry.ByteLength)
                    {
                        int n = RandomAccess.Read(
                            handle,
                            rented.AsSpan(totalRead, entry.ByteLength - totalRead),
                            entry.FileOffset + totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                    }
                    if (totalRead == entry.ByteLength)
                        board = ParseLineUtf8(rented.AsSpan(0, totalRead));
                }

                if (board != null)
                {
                    _ = ToPlayerState(board, repo, currentHash, collector);
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            handle?.Dispose();
        }

        var summary = collector.BuildSummary();
        if (logSummary)
            Console.WriteLine(FormatSanitizationAuditSummary("startup_audit", summary));
        return summary;
    }

    /// <summary>将清洗审计汇总格式化为单行可读日志（ASCII 键名确保不同控制台代码页下清晰可读）。</summary>
    public static string FormatSanitizationAuditSummary(string stage, GhostSanitizationAuditSummary summary)
    {
        var reasonsPart = summary.CountsByReason.Count == 0
            ? "none"
            : string.Join(", ", summary.CountsByReason
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        var topKeysPart = summary.CountsByKey.Count == 0
            ? "none"
            : string.Join("; ", summary.CountsByKey
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key.TemplateId, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(kv => $"({kv.Key.Source},{kv.Key.TemplateId},{kv.Key.Attribute},{kv.Key.Reason})={kv.Value}"));
        return $"[PvpGhostPool][SanitizeAudit] stage={stage} scannedBoards={summary.ScannedBoards} affectedBoards={summary.AffectedBoards} " +
               $"totalCleaned={summary.TotalCleanedEntries} uniqueKeys={summary.CountsByKey.Count} byReason={{{reasonsPart}}} top=[{topKeysPart}]";
    }

    public int Count => _index.TotalCount;
    public int PlayerGhostCount => _index.PlayerGhostCount;
    public int CustomRuleGhostCount => _index.CustomRuleGhostCount;
    public int DiskIndexedCount => _index.DiskIndexedCount;
    public int CachedMaterializedCount => _lru.Count;
    public int LruCapacity => _lru.Capacity;
    public long LruHitCount => _lru.HitCount;
    public long LruMissCount => _lru.MissCount;

    public static PvpGhostPool Load(string dataRoot)
    {
        var pool = new PvpGhostPool();
        string? path = ResolvePath(dataRoot);
        if (path == null || !File.Exists(path))
        {
            Console.WriteLine("[PvpGhostPool] ghost_pool_balanced.jsonl not found; using dynamic fallback");
            return pool;
        }

        pool._sourceFilePath = path;
        int rawLines = 0;
        int droppedDirty = 0;

        // Wave 4A #7：单遍 FileStream 字节扫描 + 零堆分配 Utf8JsonReader，按精确 (FileOffset, ByteLength) 建轻量索引，
        // 彻底避免全量留存 29,555 个 GhostBoard/GhostItem/Dictionary 对象图（堆内存由 ~523MB 降至 <15MB，冷启 <0.5s）。
        var reusableItemCounts = new Dictionary<string, int>(16, StringComparer.OrdinalIgnoreCase);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 256 * 1024, FileOptions.SequentialScan))
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                int buffered = 0;
                long bufStartOffset = 0;
                bool isFirstLine = true;

                while (true)
                {
                    if (buffered == buf.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        Buffer.BlockCopy(buf, 0, bigger, 0, buffered);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = bigger;
                    }

                    int read = fs.Read(buf, buffered, buf.Length - buffered);
                    int total = buffered + read;
                    int pos = 0;

                    while (pos < total)
                    {
                        int nlRel = Array.IndexOf(buf, (byte)'\n', pos, total - pos);
                        if (nlRel < 0)
                        {
                            if (read > 0)
                                break; // 行跨缓冲边界，留待下轮续读
                            nlRel = total; // 文件尾最后一行无换行符
                        }

                        int lineStart = pos;
                        int lineEnd = nlRel;
                        if (lineEnd > lineStart && buf[lineEnd - 1] == (byte)'\r')
                            lineEnd--;

                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            if (lineEnd - lineStart >= 3 && buf[lineStart] == 0xEF && buf[lineStart + 1] == 0xBB && buf[lineStart + 2] == 0xBF)
                                lineStart += 3;
                        }

                        while (lineStart < lineEnd && IsAsciiWhitespace(buf[lineStart])) lineStart++;
                        while (lineEnd > lineStart && IsAsciiWhitespace(buf[lineEnd - 1])) lineEnd--;

                        int lineLen = lineEnd - lineStart;
                        if (lineLen > 0)
                        {
                            rawLines++;
                            long lineOffset = bufStartOffset + lineStart;
                            var entry = TryIndexUtf8Line(buf.AsSpan(lineStart, lineLen), lineOffset, lineLen, reusableItemCounts);
                            if (entry == null)
                                droppedDirty++;
                            else
                                pool.AddEntryUnlocked(entry);
                        }

                        pos = nlRel < total ? nlRel + 1 : total;
                    }

                    if (pos < total)
                    {
                        int remaining = total - pos;
                        Buffer.BlockCopy(buf, pos, buf, 0, remaining);
                        buffered = remaining;
                        bufStartOffset += pos;
                    }
                    else
                    {
                        buffered = 0;
                        bufStartOffset += total;
                    }

                    if (read == 0)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        pool.RebuildReadIndexUnlocked();
        Console.WriteLine(
            $"[PvpGhostPool] indexed {pool._all.Count}/{rawLines} clean ghost boards (dropped {droppedDirty} dirty/incomplete, " +
            $"disk-offset mode, LRU={pool._lru.Capacity}) from {path} ({pool._mutableBuckets.Count} partition-day buckets)");
        return pool;
    }

    private static bool IsAsciiWhitespace(byte b)
        => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';

    private static bool IsRealCommunitySource(string? source)
        => !string.IsNullOrEmpty(source)
        && (source.StartsWith("real_", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("bazaarplusplus", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("compass", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("bazaarhub", StringComparison.OrdinalIgnoreCase));

    private static byte EncodePowerBucket(string? powerBucket)
    {
        if (string.IsNullOrWhiteSpace(powerBucket)) return PowerBucketNormal;
        if (powerBucket.Equals("Strong", StringComparison.OrdinalIgnoreCase)) return PowerBucketStrong;
        if (powerBucket.Equals("Weak", StringComparison.OrdinalIgnoreCase)) return PowerBucketWeak;
        if (powerBucket.Equals("MetaBoss", StringComparison.OrdinalIgnoreCase)) return PowerBucketMetaBoss;
        return PowerBucketNormal;
    }

    private static bool TryEncodePowerBucket(string? powerBucket, out byte code)
    {
        if (string.IsNullOrWhiteSpace(powerBucket))
        {
            code = PowerBucketNormal;
            return false;
        }
        string s = powerBucket.Trim();
        if (s.Equals("Normal", StringComparison.OrdinalIgnoreCase)) { code = PowerBucketNormal; return true; }
        if (s.Equals("Strong", StringComparison.OrdinalIgnoreCase)) { code = PowerBucketStrong; return true; }
        if (s.Equals("Weak", StringComparison.OrdinalIgnoreCase)) { code = PowerBucketWeak; return true; }
        if (s.Equals("MetaBoss", StringComparison.OrdinalIgnoreCase)) { code = PowerBucketMetaBoss; return true; }
        code = PowerBucketNormal;
        return false;
    }

    private static byte EncodeHeroCode(string? hero)
    {
        if (string.IsNullOrWhiteSpace(hero)) return HeroCodeUnknown;
        string s = hero.Trim();
        if (s.Equals(HeroNames.Vanessa, StringComparison.OrdinalIgnoreCase)) return HeroCodeVanessa;
        if (s.Equals(HeroNames.Pygmalien, StringComparison.OrdinalIgnoreCase)) return HeroCodePygmalien;
        if (s.Equals(HeroNames.Dooley, StringComparison.OrdinalIgnoreCase)) return HeroCodeDooley;
        if (s.Equals(HeroNames.Mak, StringComparison.OrdinalIgnoreCase)) return HeroCodeMak;
        if (s.Equals(HeroNames.Jules, StringComparison.OrdinalIgnoreCase)) return HeroCodeJules;
        if (s.Equals(HeroNames.Stelle, StringComparison.OrdinalIgnoreCase)) return HeroCodeStelle;
        if (s.Equals(HeroNames.Karnok, StringComparison.OrdinalIgnoreCase)) return HeroCodeKarnok;
        if (s.Equals(HeroNames.Hero8, StringComparison.OrdinalIgnoreCase)) return HeroCodeHero8;
        return HeroCodeUnknown;
    }

    private static byte EncodeSourceCode(bool isPlayerGhost, string? source)
    {
        if (isPlayerGhost)
            return SourceCodePlayer;
        if (string.IsNullOrWhiteSpace(source))
            return SourceCodeUnknown;

        string s = source.Trim();
        if (IsRealCommunitySource(s) || s.Equals("community", StringComparison.OrdinalIgnoreCase))
            return SourceCodeCommunity;
        if (s.Equals("player", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("player_", StringComparison.OrdinalIgnoreCase))
            return SourceCodePlayer;
        if (s.Equals("ai", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("ai_", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("selfplay", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("bot", StringComparison.OrdinalIgnoreCase)
            || s.Equals("fallback", StringComparison.OrdinalIgnoreCase))
            return SourceCodeAi;
        return SourceCodeUnknown;
    }

    private static bool TryEncodeSourceFilter(string? source, out byte code)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            code = SourceCodeUnknown;
            return false;
        }
        string s = source.Trim();
        if (s.Equals("community", StringComparison.OrdinalIgnoreCase)) { code = SourceCodeCommunity; return true; }
        if (s.Equals("player", StringComparison.OrdinalIgnoreCase)) { code = SourceCodePlayer; return true; }
        if (s.Equals("ai", StringComparison.OrdinalIgnoreCase)) { code = SourceCodeAi; return true; }
        code = SourceCodeUnknown;
        return false;
    }

    private static bool MatchesPowerBucketCode(byte code, string[] buckets)
    {
        string name = code switch
        {
            PowerBucketStrong => "Strong",
            PowerBucketWeak => "Weak",
            PowerBucketMetaBoss => "MetaBoss",
            _ => "Normal",
        };
        for (int i = 0; i < buckets.Length; i++)
        {
            if (string.Equals(buckets[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool PropEquals(ref Utf8JsonReader reader, ReadOnlySpan<byte> camel, ReadOnlySpan<byte> pascal)
        => reader.ValueTextEquals(camel) || reader.ValueTextEquals(pascal);

    /// <summary>
    /// 零 JsonDocument 分配的 Utf8JsonReader 单遍行索引器：100% 精确复现 IsCleanStaticGhostForLoad 清洗判定并产出极轻量的 GhostIndexEntry。
    /// </summary>
    private static GhostIndexEntry? TryIndexUtf8Line(
        ReadOnlySpan<byte> utf8Line,
        long fileOffset,
        int byteLength,
        Dictionary<string, int> reusableItemCounts)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            string boardId = "";
            int day = 1;
            string hero = "Vanessa";
            int level = 1;
            int healthMax = GameDefaults.StartingHealth;
            int wins = 0;
            string powerBucket = "Normal";
            string source = "";
            string persona = "";
            bool isPlayerGhost = false;
            bool isCustomRule = false;
            string? poolPartition = null;

            int itemCount = 0;
            int totalWidth = 0;
            bool allWidth1 = true;
            bool hasInvalidOrTripleItem = false;
            int skillCount = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (PropEquals(ref reader, "boardId"u8, "BoardId"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        boardId = reader.GetString() ?? "";
                }
                else if (PropEquals(ref reader, "day"u8, "Day"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int d))
                        day = Math.Max(1, d);
                }
                else if (PropEquals(ref reader, "hero"u8, "Hero"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        hero = reader.GetString() ?? "Vanessa";
                }
                else if (PropEquals(ref reader, "level"u8, "Level"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int lv))
                        level = lv;
                }
                else if (PropEquals(ref reader, "healthMax"u8, "HealthMax"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int hp))
                        healthMax = Math.Max(100, hp);
                }
                else if (PropEquals(ref reader, "wins"u8, "Wins"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int w))
                        wins = w;
                }
                else if (PropEquals(ref reader, "powerBucket"u8, "PowerBucket"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        powerBucket = reader.GetString() ?? "Normal";
                }
                else if (PropEquals(ref reader, "source"u8, "Source"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        source = reader.GetString() ?? "";
                }
                else if (PropEquals(ref reader, "persona"u8, "Persona"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        persona = reader.GetString() ?? "";
                }
                else if (PropEquals(ref reader, "isPlayerGhost"u8, "IsPlayerGhost"u8))
                {
                    reader.Read();
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        isPlayerGhost = reader.GetBoolean();
                }
                else if (PropEquals(ref reader, "isCustomRule"u8, "IsCustomRule"u8))
                {
                    reader.Read();
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        isCustomRule = reader.GetBoolean();
                }
                else if (PropEquals(ref reader, "poolPartition"u8, "PoolPartition"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        poolPartition = reader.GetString();
                }
                else if (PropEquals(ref reader, "items"u8, "Items"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        reusableItemCounts.Clear();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                reader.Skip();
                                continue;
                            }
                            int itemWidth = 1;
                            string? tplId = null;
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if (PropEquals(ref reader, "width"u8, "Width"u8))
                                {
                                    reader.Read();
                                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int iw))
                                        itemWidth = iw;
                                }
                                else if (PropEquals(ref reader, "templateId"u8, "TemplateId"u8))
                                {
                                    reader.Read();
                                    if (reader.TokenType == JsonTokenType.String)
                                        tplId = reader.GetString();
                                }
                                else
                                {
                                    reader.Read();
                                    reader.Skip();
                                }
                            }
                            itemCount++;
                            int clampedW = Math.Clamp(itemWidth, 1, 3);
                            totalWidth += clampedW;
                            if (clampedW != 1) allWidth1 = false;

                            if (string.IsNullOrWhiteSpace(tplId))
                            {
                                hasInvalidOrTripleItem = true;
                            }
                            else
                            {
                                int cnt = reusableItemCounts.GetValueOrDefault(tplId, 0) + 1;
                                reusableItemCounts[tplId] = cnt;
                                if (cnt >= 3) hasInvalidOrTripleItem = true;
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else if (PropEquals(ref reader, "skills"u8, "Skills"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            skillCount++;
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            // 100% 等价复现 IsCleanStaticGhostForLoad 清洗规则
            if (itemCount == 0 || hasInvalidOrTripleItem) return null;
            var (minHp, maxHp, maxLv) = GetSaneBoundsForDay(day);
            int effectiveMinHp = isPlayerGhost ? Math.Min(100, minHp) : minHp;
            int effectiveMaxHp = (isPlayerGhost && day == 1) ? 550 : maxHp;
            int effectiveMaxLv = (isPlayerGhost && day == 1) ? 2 : maxLv;
            if (healthMax < effectiveMinHp || healthMax > effectiveMaxHp || level < 1 || level > effectiveMaxLv)
                return null;
            if (!IsSupportedClientHero(hero)) return null;
            if (!string.IsNullOrEmpty(persona) && IsJunkSyntheticPersona(persona, day))
                return null;

            bool isSyntheticAi = string.Equals(source, "synthetic_selfplay", StringComparison.OrdinalIgnoreCase);
            if (isSyntheticAi)
            {
                if (day >= 3 && level < day) return null;
                if (day >= 4 && skillCount == 0) return null;
                if (day >= 5 && itemCount >= 8 && allWidth1) return null;
            }

            bool widthOk = day switch
            {
                <= 1 => level <= 2 && healthMax <= 550 && totalWidth >= 2,
                2 => totalWidth >= 4,
                3 => totalWidth >= 5,
                4 => totalWidth >= 6,
                _ => totalWidth >= 8,
            };
            if (!widthOk) return null;

            string normPartition = NormalizePartition(poolPartition, isCustomRule);
            if (string.Equals(normPartition, StandardPartition, StringComparison.Ordinal))
                normPartition = StandardPartition; // 共享常量引用减少堆分配

            byte pbCode = EncodePowerBucket(powerBucket);
            var flags = GhostEntryFlags.None;
            if (isPlayerGhost) flags |= GhostEntryFlags.IsPlayerGhost;
            if (isCustomRule) flags |= GhostEntryFlags.IsCustomRule;
            if (!isPlayerGhost && IsRealCommunitySource(source)) flags |= GhostEntryFlags.IsRealCommunityGhost;
            if (day >= 4 && totalWidth >= 10) flags |= GhostEntryFlags.IsFull10Slots;
            if (pbCode is PowerBucketStrong or PowerBucketMetaBoss) flags |= GhostEntryFlags.IsHighPower;
            if (string.Equals(source, "real_compass_anchor", StringComparison.OrdinalIgnoreCase)) flags |= GhostEntryFlags.IsLegacyAnchor;

            return new GhostIndexEntry
            {
                BoardId = boardId,
                Partition = normPartition,
                FileOffset = fileOffset,
                ByteLength = byteLength,
                HealthMax = healthMax,
                Day = (ushort)Math.Clamp(day, 1, 99),
                Level = (byte)Math.Clamp(level, 1, byte.MaxValue),
                Wins = (byte)Math.Clamp(wins, 0, byte.MaxValue),
                PowerBucketCode = pbCode,
                HeroCode = EncodeHeroCode(hero),
                SourceCode = EncodeSourceCode(isPlayerGhost, source),
                Flags = flags,
                InMemoryBoard = null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static GhostIndexEntry CreateInMemoryEntry(GhostBoard board)
    {
        int clampedDay = Math.Clamp(board.Day, 1, 99);
        string partition = NormalizePartition(board.PoolPartition, board.IsCustomRule);
        if (string.Equals(partition, StandardPartition, StringComparison.Ordinal))
            partition = StandardPartition;

        byte pbCode = EncodePowerBucket(board.PowerBucket);
        var flags = GhostEntryFlags.None;
        if (board.IsPlayerGhost) flags |= GhostEntryFlags.IsPlayerGhost;
        if (board.IsCustomRule) flags |= GhostEntryFlags.IsCustomRule;
        if (board.IsRealCommunityGhost) flags |= GhostEntryFlags.IsRealCommunityGhost;
        if (clampedDay >= 4 && GetTotalSlotWidth(board, null) >= 10) flags |= GhostEntryFlags.IsFull10Slots;
        if (pbCode is PowerBucketStrong or PowerBucketMetaBoss) flags |= GhostEntryFlags.IsHighPower;
        if (string.Equals(board.Source, "real_compass_anchor", StringComparison.OrdinalIgnoreCase)) flags |= GhostEntryFlags.IsLegacyAnchor;

        return new GhostIndexEntry
        {
            BoardId = board.BoardId ?? "",
            Partition = partition,
            FileOffset = -1,
            ByteLength = 0,
            HealthMax = board.HealthMax,
            Day = (ushort)clampedDay,
            Level = (byte)Math.Clamp(board.Level, 1, byte.MaxValue),
            Wins = (byte)Math.Clamp(board.Wins, 0, byte.MaxValue),
            PowerBucketCode = pbCode,
            HeroCode = EncodeHeroCode(board.Hero),
            SourceCode = EncodeSourceCode(board.IsPlayerGhost, board.Source),
            Flags = flags,
            InMemoryBoard = board,
        };
    }

    private GhostBoard? MaterializeEntry(GhostIndexEntry entry)
    {
        if (entry.InMemoryBoard != null)
            return entry.InMemoryBoard;
        if (!entry.IsDiskBacked)
            return null;

        if (_lru.TryGet(entry.FileOffset, out var cached) && cached != null)
            return cached;

        var board = MaterializeEntryNoCache(entry);
        if (board != null)
            _lru.Put(entry.FileOffset, board);
        return board;
    }

    /// <summary>
    /// 按磁盘偏移或内存引用实体化单条镜像盘面，但不读写热点 LRU 缓存（供编排台搜索与单条预览复用，避免污染战斗热缓存）。
    /// </summary>
    private GhostBoard? MaterializeEntryNoCache(GhostIndexEntry entry)
    {
        if (entry.InMemoryBoard != null)
            return entry.InMemoryBoard;
        if (!entry.IsDiskBacked)
            return null;

        string? filePath = _sourceFilePath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        byte[] rented = ArrayPool<byte>.Shared.Rent(entry.ByteLength);
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);

            int totalRead = 0;
            while (totalRead < entry.ByteLength)
            {
                int n = RandomAccess.Read(
                    handle,
                    rented.AsSpan(totalRead, entry.ByteLength - totalRead),
                    entry.FileOffset + totalRead);
                if (n <= 0) break;
                totalRead += n;
            }
            if (totalRead != entry.ByteLength)
                return null;

            return ParseLineUtf8(rented.AsSpan(0, totalRead));
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>按 BoardId O(1) 查询单条镜像盘面（包含磁盘静态池与内存真人镜像），读取时不污染战斗 LRU 缓存。</summary>
    public GhostBoard? GetGhostById(string boardId)
        => GetGhostById(boardId, effectiveRepo: null);

    /// <summary>按 BoardId O(1) 查询单条镜像盘面，可选按 <paramref name="effectiveRepo"/> 补齐缺失的物品/技能中英文名（不污染 LRU）。</summary>
    public GhostBoard? GetGhostById(string boardId, GameDataRepository? effectiveRepo)
    {
        if (string.IsNullOrWhiteSpace(boardId))
            return null;

        var snap = _index;
        if (!snap.ByBoardId.TryGetValue(boardId.Trim(), out var entry))
            return null;

        var board = MaterializeEntryNoCache(entry);
        if (board == null)
            return null;

        return EnrichBoardNamesIfNeeded(board, effectiveRepo);
    }

    /// <summary>
    /// 在内存索引快照上按多维条件搜索 PvP 镜像池（支持懒加载物品/技能关键词索引、可选 Mod 过滤、不污染战斗 LRU 缓存）。
    /// 每页上限 20 条，总命中上限 500 条（超限时 <see cref="GhostPoolSearchResult.Truncated"/> 为 true）。
    /// </summary>
    public GhostPoolSearchResult SearchGhosts(
        GhostPoolSearchRequest req,
        GameDataRepository? effectiveRepo = null,
        IReadOnlySet<string>? allowedModIds = null,
        Func<string, string?>? resolveTemplateModId = null)
    {
        req ??= new GhostPoolSearchRequest();
        const int MaxSearchResults = 500;
        int page = Math.Max(1, req.Page);
        int pageSize = req.PageSize <= 0 ? 20 : Math.Min(req.PageSize, 20);

        GhostPoolSearchResult EmptyResult(bool indexBuilding = false) => new()
        {
            Total = 0,
            Page = page,
            PageSize = pageSize,
            Truncated = false,
            IndexBuilding = indexBuilding,
            Items = new List<GhostBoard>(),
        };

        var snap = _index;
        if (snap.TotalCount == 0)
            return EmptyResult();

        int? dayFilter = req.Day;
        if (dayFilter.HasValue && dayFilter.Value <= 0)
            return EmptyResult();

        byte heroFilter = HeroCodeUnknown;
        if (!string.IsNullOrWhiteSpace(req.Hero))
        {
            heroFilter = EncodeHeroCode(req.Hero);
            if (heroFilter == HeroCodeUnknown)
                return EmptyResult();
        }

        byte? powerFilter = null;
        if (!string.IsNullOrWhiteSpace(req.PowerBucket))
        {
            if (!TryEncodePowerBucket(req.PowerBucket, out byte pb))
                return EmptyResult();
            powerFilter = pb;
        }

        byte? sourceFilter = null;
        if (!string.IsNullOrWhiteSpace(req.Source))
        {
            if (!TryEncodeSourceFilter(req.Source, out byte sc))
                return EmptyResult();
            sourceFilter = sc;
        }

        string? partitionFilter = string.IsNullOrWhiteSpace(req.Partition)
            ? null
            : req.Partition.Trim().ToLowerInvariant();

        int? hpMin = req.HpMin;
        int? hpMax = req.HpMax;
        if (hpMin.HasValue && hpMax.HasValue && hpMin.Value > hpMax.Value)
            return EmptyResult();

        int? levelMin = req.LevelMin;
        int? levelMax = req.LevelMax;
        if (levelMin.HasValue && levelMax.HasValue && levelMin.Value > levelMax.Value)
            return EmptyResult();

        string? keyword = string.IsNullOrWhiteSpace(req.Keyword) ? null : req.Keyword.Trim();
        KeywordIndexData? kwIndex = null;
        HashSet<int>? matchedTemplateCodes = null;
        int singleMatchedCode = -1;

        if (keyword != null)
        {
            if (!TryGetOrStartKeywordIndex(snap, out kwIndex) || kwIndex == null)
                return EmptyResult(indexBuilding: true);

            matchedTemplateCodes = ResolveMatchedTemplateCodes(kwIndex, keyword, effectiveRepo, out singleMatchedCode);
            if (matchedTemplateCodes.Count == 0)
                return EmptyResult();
        }

        bool hasModFilter = allowedModIds != null && resolveTemplateModId != null;
        Dictionary<string, bool>? templateAllowedCache = hasModFilter
            ? new Dictionary<string, bool>(64, StringComparer.Ordinal)
            : null;

        bool IsTemplateAllowedCached(string? templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
                return true;
            if (templateAllowedCache!.TryGetValue(templateId, out bool cached))
                return cached;
            bool allowed = IsTemplateAllowedByMods(templateId, allowedModIds!, resolveTemplateModId!);
            templateAllowedCache[templateId] = allowed;
            return allowed;
        }

        bool IsBoardAllowedCached(GhostBoard board)
        {
            if (board.Items != null)
            {
                for (int j = 0; j < board.Items.Count; j++)
                {
                    if (!IsTemplateAllowedCached(board.Items[j]?.TemplateId))
                        return false;
                }
            }
            if (board.Skills != null)
            {
                for (int j = 0; j < board.Skills.Count; j++)
                {
                    if (!IsTemplateAllowedCached(board.Skills[j]?.TemplateId))
                        return false;
                }
            }
            if (board.SocketEffects != null)
            {
                for (int j = 0; j < board.SocketEffects.Count; j++)
                {
                    if (!IsTemplateAllowedCached(board.SocketEffects[j]?.TemplateId))
                        return false;
                }
            }
            return true;
        }

        KeywordIndexData? modFilterKwIndex = null;
        HashSet<int>? disallowedTemplateCodes = null;
        int singleDisallowedCode = -1;

        if (hasModFilter)
        {
            var existingKw = kwIndex ?? TryGetReadyKeywordIndexWithoutDiskScan(snap);
            if (existingKw != null && ReferenceEquals(existingKw.BoundSnapshot, snap))
            {
                modFilterKwIndex = existingKw;
                foreach (var (tid, code) in existingKw.TemplateToCode)
                {
                    if (!IsTemplateAllowedCached(tid))
                    {
                        disallowedTemplateCodes ??= new HashSet<int>();
                        disallowedTemplateCodes.Add(code);
                    }
                }
                if (disallowedTemplateCodes is { Count: 1 })
                {
                    foreach (int c in disallowedTemplateCodes)
                        singleDisallowedCode = c;
                }
            }
        }

        var allEntries = snap.AllEntries;
        int skip = (page - 1) * pageSize;
        var pageEntries = new List<GhostIndexEntry>(pageSize);
        List<GhostBoard>? preMaterializedPage = null;
        int matchedCount = 0;
        bool truncated = false;

        // 当启用 Mod 过滤且磁盘关键词索引尚未就绪时，按单句柄直检磁盘行模板（不写 LRU），
        // 避免先把未验证的磁盘项计入 matchedCount 后再在翻页实体化时剔除导致 Total/单页条数错位。
        bool needDiskModCheck = hasModFilter && modFilterKwIndex == null && snap.DiskIndexedCount > 0;
        Func<string?, bool>? isTemplateAllowedDelegate = needDiskModCheck ? IsTemplateAllowedCached : null;
        SafeFileHandle? diskModHandle = null;
        byte[]? diskModBuf = null;
        string? diskFilePath = needDiskModCheck ? _sourceFilePath : null;
        bool diskFileUnavailable = false;

        try
        {
            for (int i = 0; i < allEntries.Length; i++)
            {
                var e = allEntries[i];
                if (dayFilter.HasValue && e.Day != dayFilter.Value) continue;
                if (heroFilter != HeroCodeUnknown && e.HeroCode != heroFilter) continue;
                if (powerFilter.HasValue && e.PowerBucketCode != powerFilter.Value) continue;
                if (sourceFilter.HasValue && e.SourceCode != sourceFilter.Value) continue;
                if (partitionFilter != null && !string.Equals(e.Partition, partitionFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (hpMin.HasValue && e.HealthMax < hpMin.Value) continue;
                if (hpMax.HasValue && e.HealthMax > hpMax.Value) continue;
                if (levelMin.HasValue && e.Level < levelMin.Value) continue;
                if (levelMax.HasValue && e.Level > levelMax.Value) continue;

                if (matchedTemplateCodes != null)
                {
                    var codes = kwIndex!.EntryTemplateCodes[i];
                    if (!HasAnyTemplateCode(codes, matchedTemplateCodes, singleMatchedCode))
                        continue;
                }

                GhostBoard? preParsedDiskBoard = null;

                if (hasModFilter)
                {
                    if (e.InMemoryBoard != null)
                    {
                        if (!IsBoardAllowedCached(e.InMemoryBoard))
                            continue;
                    }
                    else if (modFilterKwIndex != null)
                    {
                        if (disallowedTemplateCodes is { Count: > 0 })
                        {
                            var codes = modFilterKwIndex.EntryTemplateCodes[i];
                            if (HasAnyTemplateCode(codes, disallowedTemplateCodes, singleDisallowedCode))
                                continue;
                        }
                    }
                    else if (e.IsDiskBacked)
                    {
                        if (diskFileUnavailable)
                            continue;

                        if (diskModHandle == null)
                        {
                            if (string.IsNullOrEmpty(diskFilePath) || !File.Exists(diskFilePath))
                            {
                                diskFileUnavailable = true;
                                continue;
                            }
                            try
                            {
                                diskModHandle = File.OpenHandle(
                                    diskFilePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete,
                                    FileOptions.RandomAccess);
                            }
                            catch
                            {
                                diskFileUnavailable = true;
                                continue;
                            }
                        }

                        if (diskModBuf == null || diskModBuf.Length < e.ByteLength)
                        {
                            if (diskModBuf != null)
                                ArrayPool<byte>.Shared.Return(diskModBuf);
                            diskModBuf = ArrayPool<byte>.Shared.Rent(Math.Max(e.ByteLength, 8192));
                        }

                        int totalRead = 0;
                        try
                        {
                            while (totalRead < e.ByteLength)
                            {
                                int n = RandomAccess.Read(
                                    diskModHandle,
                                    diskModBuf.AsSpan(totalRead, e.ByteLength - totalRead),
                                    e.FileOffset + totalRead);
                                if (n <= 0) break;
                                totalRead += n;
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        if (totalRead != e.ByteLength)
                            continue;

                        ReadOnlySpan<byte> lineSpan = diskModBuf.AsSpan(0, totalRead);
                        if (!IsUtf8LineAllowedByMods(lineSpan, isTemplateAllowedDelegate!))
                            continue;

                        if (matchedCount < MaxSearchResults
                            && matchedCount >= skip
                            && (preMaterializedPage?.Count ?? 0) < pageSize)
                        {
                            preParsedDiskBoard = ParseLineUtf8(lineSpan);
                            if (preParsedDiskBoard == null)
                                continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                if (matchedCount >= MaxSearchResults)
                {
                    truncated = true;
                    break;
                }

                if (matchedCount >= skip)
                {
                    if (needDiskModCheck)
                    {
                        preMaterializedPage ??= new List<GhostBoard>(pageSize);
                        if (preMaterializedPage.Count < pageSize)
                        {
                            var readyBoard = preParsedDiskBoard ?? e.InMemoryBoard;
                            if (readyBoard != null)
                                preMaterializedPage.Add(EnrichBoardNamesIfNeeded(readyBoard, effectiveRepo));
                        }
                    }
                    else if (pageEntries.Count < pageSize)
                    {
                        pageEntries.Add(e);
                    }
                }

                matchedCount++;
            }
        }
        finally
        {
            diskModHandle?.Dispose();
            if (diskModBuf != null)
                ArrayPool<byte>.Shared.Return(diskModBuf);
        }

        List<GhostBoard> items;
        if (needDiskModCheck)
        {
            items = preMaterializedPage ?? new List<GhostBoard>();
        }
        else
        {
            items = new List<GhostBoard>(pageEntries.Count);
            for (int i = 0; i < pageEntries.Count; i++)
            {
                var board = MaterializeEntryNoCache(pageEntries[i]);
                if (board == null)
                    continue;
                if (hasModFilter && !IsBoardAllowedCached(board))
                    continue;
                items.Add(EnrichBoardNamesIfNeeded(board, effectiveRepo));
            }
        }

        return new GhostPoolSearchResult
        {
            Total = matchedCount,
            Page = page,
            PageSize = pageSize,
            Truncated = truncated,
            IndexBuilding = false,
            Items = items,
        };
    }

    private KeywordIndexData? TryGetReadyKeywordIndexWithoutDiskScan(GhostIndexSnapshot snap)
    {
        var current = _keywordIndex;
        if (current != null && ReferenceEquals(current.BoundSnapshot, snap))
            return current;

        if (snap.TotalCount == 0 || snap.DiskIndexedCount == 0 || _diskKeywordCache == null)
            return null;

        lock (_keywordIndexLock)
        {
            current = _keywordIndex;
            if (current != null && ReferenceEquals(current.BoundSnapshot, snap))
                return current;

            var incremental = TryBuildKeywordIndexFromDiskCache(snap, _diskKeywordCache, _sourceFilePath, current);
            if (incremental != null)
            {
                _keywordIndex = incremental;
                return incremental;
            }
            return null;
        }
    }

    private bool TryGetOrStartKeywordIndex(GhostIndexSnapshot snap, out KeywordIndexData? index)
    {
        var current = _keywordIndex;
        if (current != null && ReferenceEquals(current.BoundSnapshot, snap))
        {
            index = current;
            return true;
        }

        if (snap.TotalCount == 0)
        {
            index = null;
            return true;
        }

        bool shouldStart = false;
        lock (_keywordIndexLock)
        {
            current = _keywordIndex;
            if (current != null && ReferenceEquals(current.BoundSnapshot, snap))
            {
                index = current;
                return true;
            }

            // 若无磁盘项，或 _sourceFilePath 未变且所有磁盘项 FileOffset 均已命中磁盘缓存，
            // 直接 O(内存条数) 同步构建新快照索引，绝不重扫磁盘、绝不返回 IndexBuilding = true！
            string? currentFilePath = _sourceFilePath;
            var incremental = TryBuildKeywordIndexFromDiskCache(snap, _diskKeywordCache, currentFilePath, current);
            if (incremental != null)
            {
                _keywordIndex = incremental;
                index = incremental;
                return true;
            }

            if (!_keywordIndexBuilding)
            {
                _keywordIndexBuilding = true;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var targetSnap = snap;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        string? latestFilePath = _sourceFilePath;
                        var (built, builtDiskCache) = BuildDiskCacheAndKeywordIndex(targetSnap, latestFilePath);
                        lock (_keywordIndexLock)
                        {
                            if (builtDiskCache != null)
                                _diskKeywordCache = builtDiskCache;
                            _keywordIndex = built;

                            var latestSnap = _index;
                            string? afterFilePath = _sourceFilePath;
                            if ((ReferenceEquals(latestSnap, targetSnap)
                                 && string.Equals(afterFilePath, latestFilePath, StringComparison.Ordinal))
                                || latestSnap.TotalCount == 0)
                            {
                                _keywordIndexBuilding = false;
                                return;
                            }

                            // 后台扫描期间若仅新增内存真人镜像（磁盘缓存已覆盖全部磁盘项），同步完成增量构建即可，不再重扫磁盘
                            var incremental = TryBuildKeywordIndexFromDiskCache(latestSnap, _diskKeywordCache, afterFilePath, built);
                            if (incremental != null)
                            {
                                _keywordIndex = incremental;
                                _keywordIndexBuilding = false;
                                return;
                            }

                            targetSnap = latestSnap;
                        }
                    }

                    lock (_keywordIndexLock)
                    {
                        _keywordIndexBuilding = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PvpGhostPool] keyword index build failed: {ex.Message}");
                    lock (_keywordIndexLock)
                    {
                        _keywordIndexBuilding = false;
                    }
                }
            });
        }

        index = null;
        return false;
    }

    /// <summary>
    /// 尝试仅凭已持久化的磁盘行缓存 <see cref="DiskKeywordCache"/> 与内存 <c>InMemoryBoard</c> 同步构建新快照的关键词索引。
    /// 若存在未缓存的磁盘项或数据源文件路径变更，返回 <c>null</c> 以回退到单飞后台扫描。
    /// </summary>
    private static KeywordIndexData? TryBuildKeywordIndexFromDiskCache(
        GhostIndexSnapshot snap,
        DiskKeywordCache? diskCache,
        string? currentFilePath,
        KeywordIndexData? prevIndex)
    {
        if (snap.DiskIndexedCount > 0)
        {
            if (diskCache == null
                || string.IsNullOrEmpty(currentFilePath)
                || !string.Equals(diskCache.SourceFilePath, currentFilePath, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var allEntries = snap.AllEntries;
        int count = allEntries.Length;
        var entryCodes = new int[count][];

        bool canReusePrevSlots = prevIndex != null
            && diskCache != null
            && ReferenceEquals(prevIndex.DiskCache, diskCache);
        var prevEntries = canReusePrevSlots ? prevIndex!.BoundSnapshot.AllEntries : null;
        var prevCodes = canReusePrevSlots ? prevIndex!.EntryTemplateCodes : null;

        var baseTemplateMap = snap.DiskIndexedCount > 0 ? diskCache!.TemplateToCode : null;
        Dictionary<string, int>? extendedTemplateMap = null;

        int GetOrAddCode(string templateId)
        {
            if (extendedTemplateMap == null)
            {
                if (baseTemplateMap != null && baseTemplateMap.TryGetValue(templateId, out int existing))
                    return existing;
                extendedTemplateMap = baseTemplateMap != null
                    ? new Dictionary<string, int>(baseTemplateMap, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(64, StringComparer.OrdinalIgnoreCase);
            }
            if (!extendedTemplateMap.TryGetValue(templateId, out int code))
            {
                code = extendedTemplateMap.Count;
                extendedTemplateMap[templateId] = code;
            }
            return code;
        }

        var reusableCodes = new HashSet<int>(16);

        for (int i = 0; i < count; i++)
        {
            var e = allEntries[i];
            if (e.InMemoryBoard != null)
            {
                reusableCodes.Clear();
                var b = e.InMemoryBoard;
                if (b.Items != null)
                {
                    for (int j = 0; j < b.Items.Count; j++)
                    {
                        string? tid = b.Items[j]?.TemplateId;
                        if (!string.IsNullOrWhiteSpace(tid))
                            reusableCodes.Add(GetOrAddCode(tid));
                    }
                }
                if (b.Skills != null)
                {
                    for (int j = 0; j < b.Skills.Count; j++)
                    {
                        string? tid = b.Skills[j]?.TemplateId;
                        if (!string.IsNullOrWhiteSpace(tid))
                            reusableCodes.Add(GetOrAddCode(tid));
                    }
                }
                entryCodes[i] = reusableCodes.Count > 0 ? reusableCodes.ToArray() : Array.Empty<int>();
            }
            else if (e.IsDiskBacked)
            {
                if (prevEntries != null && i < prevEntries.Length && ReferenceEquals(prevEntries[i], e))
                {
                    entryCodes[i] = prevCodes![i];
                }
                else if (diskCache != null && diskCache.CodesByFileOffset.TryGetValue(e.FileOffset, out var cachedCodes))
                {
                    entryCodes[i] = cachedCodes;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                entryCodes[i] = Array.Empty<int>();
            }
        }

        var finalTemplateMap = extendedTemplateMap != null
            ? extendedTemplateMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            : (baseTemplateMap ?? FrozenDictionary<string, int>.Empty);

        return new KeywordIndexData
        {
            BoundSnapshot = snap,
            DiskCache = snap.DiskIndexedCount > 0 ? diskCache : null,
            TemplateToCode = finalTemplateMap,
            EntryTemplateCodes = entryCodes,
        };
    }

    private static (KeywordIndexData Index, DiskKeywordCache? DiskCache) BuildDiskCacheAndKeywordIndex(
        GhostIndexSnapshot snap,
        string? sourceFilePath)
    {
        var allEntries = snap.AllEntries;
        int count = allEntries.Length;
        var entryCodes = new int[count][];
        var diskTemplateToCode = new Dictionary<string, int>(2048, StringComparer.OrdinalIgnoreCase);

        int GetOrAddDiskCode(string templateId)
        {
            if (!diskTemplateToCode.TryGetValue(templateId, out int code))
            {
                code = diskTemplateToCode.Count;
                diskTemplateToCode[templateId] = code;
            }
            return code;
        }

        Dictionary<string, int>? diskSlotByBoardId = null;
        Dictionary<long, int>? diskSlotByOffset = null;
        int diskNeeded = 0;

        for (int i = 0; i < count; i++)
        {
            var e = allEntries[i];
            if (e.IsDiskBacked)
            {
                diskNeeded++;
                if (!string.IsNullOrWhiteSpace(e.BoardId))
                {
                    diskSlotByBoardId ??= new Dictionary<string, int>(snap.DiskIndexedCount, StringComparer.OrdinalIgnoreCase);
                    diskSlotByBoardId.TryAdd(e.BoardId, i);
                }
                else
                {
                    diskSlotByOffset ??= new Dictionary<long, int>();
                    diskSlotByOffset[e.FileOffset] = i;
                }
            }
        }

        DiskKeywordCache? newDiskCache = null;
        var reusableCodes = new HashSet<int>(16);

        if (diskNeeded > 0)
        {
            if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                throw new FileNotFoundException("Ghost pool JSONL source file not found.", sourceFilePath);

            var codesByOffset = new Dictionary<long, int[]>(diskNeeded);
            var reusableLineTemplates = new List<string>(24);
            using var fs = new FileStream(
                sourceFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256 * 1024,
                FileOptions.SequentialScan);

            byte[] buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                int buffered = 0;
                long bufStartOffset = 0;
                bool isFirstLine = true;

                while (true)
                {
                    if (buffered == buf.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        Buffer.BlockCopy(buf, 0, bigger, 0, buffered);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = bigger;
                    }

                    int read = fs.Read(buf, buffered, buf.Length - buffered);
                    int total = buffered + read;
                    int pos = 0;

                    while (pos < total)
                    {
                        int nlRel = Array.IndexOf(buf, (byte)'\n', pos, total - pos);
                        if (nlRel < 0)
                        {
                            if (read > 0)
                                break;
                            nlRel = total;
                        }

                        int lineStart = pos;
                        int lineEnd = nlRel;
                        if (lineEnd > lineStart && buf[lineEnd - 1] == (byte)'\r')
                            lineEnd--;

                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            if (lineEnd - lineStart >= 3 && buf[lineStart] == 0xEF && buf[lineStart + 1] == 0xBB && buf[lineStart + 2] == 0xBF)
                                lineStart += 3;
                        }

                        while (lineStart < lineEnd && IsAsciiWhitespace(buf[lineStart])) lineStart++;
                        while (lineEnd > lineStart && IsAsciiWhitespace(buf[lineEnd - 1])) lineEnd--;

                        int lineLen = lineEnd - lineStart;
                        if (lineLen > 0)
                        {
                            long lineOffset = bufStartOffset + lineStart;
                            reusableLineTemplates.Clear();
                            if (TryExtractBoardTemplatesUtf8(
                                    buf.AsSpan(lineStart, lineLen),
                                    diskSlotByOffset == null ? diskSlotByBoardId : null,
                                    out string? boardId,
                                    reusableLineTemplates))
                            {
                                int slot = -1;
                                if (!string.IsNullOrWhiteSpace(boardId) && diskSlotByBoardId != null && diskSlotByBoardId.TryGetValue(boardId, out int sById))
                                    slot = sById;
                                else if (diskSlotByOffset != null && diskSlotByOffset.TryGetValue(lineOffset, out int sByOff))
                                    slot = sByOff;

                                if (slot >= 0)
                                {
                                    reusableCodes.Clear();
                                    for (int t = 0; t < reusableLineTemplates.Count; t++)
                                        reusableCodes.Add(GetOrAddDiskCode(reusableLineTemplates[t]));
                                    int[] lineCodes = reusableCodes.Count > 0 ? reusableCodes.ToArray() : Array.Empty<int>();
                                    entryCodes[slot] = lineCodes;
                                    codesByOffset[allEntries[slot].FileOffset] = lineCodes;
                                }
                            }
                        }

                        pos = nlRel < total ? nlRel + 1 : total;
                    }

                    if (pos < total)
                    {
                        int remaining = total - pos;
                        Buffer.BlockCopy(buf, pos, buf, 0, remaining);
                        buffered = remaining;
                        bufStartOffset += pos;
                    }
                    else
                    {
                        buffered = 0;
                        bufStartOffset += total;
                    }

                    if (read == 0)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            // 兜底确保所有磁盘槽位均写入缓存映射
            for (int i = 0; i < count; i++)
            {
                var e = allEntries[i];
                if (e.IsDiskBacked)
                {
                    entryCodes[i] ??= Array.Empty<int>();
                    codesByOffset.TryAdd(e.FileOffset, entryCodes[i]);
                }
            }

            newDiskCache = new DiskKeywordCache
            {
                SourceFilePath = sourceFilePath,
                TemplateToCode = diskTemplateToCode.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                CodesByFileOffset = codesByOffset.ToFrozenDictionary(),
            };
        }

        // 在磁盘编号前缀 0..K-1 之上补充内存真人镜像的模板编号（磁盘行 int[] 零拷贝复用）
        var baseTemplateMap = newDiskCache?.TemplateToCode;
        Dictionary<string, int>? extendedTemplateMap = null;

        int GetOrAddMemoryCode(string templateId)
        {
            if (extendedTemplateMap == null)
            {
                if (baseTemplateMap != null && baseTemplateMap.TryGetValue(templateId, out int existing))
                    return existing;
                extendedTemplateMap = baseTemplateMap != null
                    ? new Dictionary<string, int>(baseTemplateMap, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(64, StringComparer.OrdinalIgnoreCase);
            }
            if (!extendedTemplateMap.TryGetValue(templateId, out int code))
            {
                code = extendedTemplateMap.Count;
                extendedTemplateMap[templateId] = code;
            }
            return code;
        }

        for (int i = 0; i < count; i++)
        {
            var e = allEntries[i];
            if (e.InMemoryBoard != null)
            {
                reusableCodes.Clear();
                var b = e.InMemoryBoard;
                if (b.Items != null)
                {
                    for (int j = 0; j < b.Items.Count; j++)
                    {
                        string? tid = b.Items[j]?.TemplateId;
                        if (!string.IsNullOrWhiteSpace(tid))
                            reusableCodes.Add(GetOrAddMemoryCode(tid));
                    }
                }
                if (b.Skills != null)
                {
                    for (int j = 0; j < b.Skills.Count; j++)
                    {
                        string? tid = b.Skills[j]?.TemplateId;
                        if (!string.IsNullOrWhiteSpace(tid))
                            reusableCodes.Add(GetOrAddMemoryCode(tid));
                    }
                }
                entryCodes[i] = reusableCodes.Count > 0 ? reusableCodes.ToArray() : Array.Empty<int>();
            }
            else
            {
                entryCodes[i] ??= Array.Empty<int>();
            }
        }

        var finalTemplateMap = extendedTemplateMap != null
            ? extendedTemplateMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            : (baseTemplateMap ?? FrozenDictionary<string, int>.Empty);

        var idx = new KeywordIndexData
        {
            BoundSnapshot = snap,
            DiskCache = newDiskCache,
            TemplateToCode = finalTemplateMap,
            EntryTemplateCodes = entryCodes,
        };
        return (idx, newDiskCache);
    }

    private static bool IsUtf8LineAllowedByMods(
        ReadOnlySpan<byte> utf8Line,
        Func<string?, bool> isTemplateAllowed)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (PropEquals(ref reader, "items"u8, "Items"u8)
                    || PropEquals(ref reader, "skills"u8, "Skills"u8)
                    || PropEquals(ref reader, "socketEffects"u8, "SocketEffects"u8))
                {
                    if (!reader.Read()) break;
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                reader.Skip();
                                continue;
                            }
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if (PropEquals(ref reader, "templateId"u8, "TemplateId"u8))
                                {
                                    if (!reader.Read()) break;
                                    if (reader.TokenType == JsonTokenType.String)
                                    {
                                        if (!isTemplateAllowed(reader.GetString()))
                                            return false;
                                    }
                                    else
                                    {
                                        reader.Skip();
                                    }
                                }
                                else
                                {
                                    if (!reader.Read()) break;
                                    reader.Skip();
                                }
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    if (!reader.Read()) break;
                    reader.Skip();
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractBoardTemplatesUtf8(
        ReadOnlySpan<byte> utf8Line,
        Dictionary<string, int>? filterByBoardId,
        out string? boardId,
        List<string> templateIds)
    {
        boardId = null;
        try
        {
            var reader = new Utf8JsonReader(utf8Line, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (PropEquals(ref reader, "boardId"u8, "BoardId"u8))
                {
                    if (!reader.Read()) break;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        boardId = reader.GetString();
                        if (filterByBoardId != null && !string.IsNullOrWhiteSpace(boardId) && !filterByBoardId.ContainsKey(boardId))
                            return false;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else if (PropEquals(ref reader, "items"u8, "Items"u8) || PropEquals(ref reader, "skills"u8, "Skills"u8))
                {
                    if (!reader.Read()) break;
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                reader.Skip();
                                continue;
                            }
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if (PropEquals(ref reader, "templateId"u8, "TemplateId"u8))
                                {
                                    if (!reader.Read()) break;
                                    if (reader.TokenType == JsonTokenType.String)
                                    {
                                        string? tid = reader.GetString();
                                        if (!string.IsNullOrWhiteSpace(tid))
                                            templateIds.Add(tid);
                                    }
                                    else
                                    {
                                        reader.Skip();
                                    }
                                }
                                else
                                {
                                    if (!reader.Read()) break;
                                    reader.Skip();
                                }
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    if (!reader.Read()) break;
                    reader.Skip();
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<int> ResolveMatchedTemplateCodes(
        KeywordIndexData kwIndex,
        string keyword,
        GameDataRepository? effectiveRepo,
        out int singleCode)
    {
        var matched = new HashSet<int>();
        singleCode = -1;

        foreach (var (tid, code) in kwIndex.TemplateToCode)
        {
            if (tid.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(code);
                continue;
            }

            if (effectiveRepo == null)
                continue;

            var tpl = effectiveRepo.GetCard(tid);
            if (tpl == null)
                continue;

            if ((!string.IsNullOrEmpty(tpl.InternalName) && tpl.InternalName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(tpl.DisplayTitle) && tpl.DisplayTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                matched.Add(code);
                continue;
            }

            var loc = effectiveRepo.Localization;
            if (loc != null)
            {
                string? locText = loc.GetLocalizedText(tpl.Localization?.Title);
                if (!string.IsNullOrEmpty(locText) && locText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(code);
                    continue;
                }
                string? locTitle = loc.GetLocalizedTitle(tpl);
                if (!string.IsNullOrEmpty(locTitle) && locTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(code);
                    continue;
                }
            }
        }

        if (matched.Count == 1)
        {
            foreach (int c in matched)
                singleCode = c;
        }
        return matched;
    }

    private static bool HasAnyTemplateCode(int[] codes, HashSet<int> targets, int singleTarget)
    {
        if (codes.Length == 0) return false;
        if (singleTarget >= 0)
        {
            for (int i = 0; i < codes.Length; i++)
            {
                if (codes[i] == singleTarget)
                    return true;
            }
            return false;
        }

        for (int i = 0; i < codes.Length; i++)
        {
            if (targets.Contains(codes[i]))
                return true;
        }
        return false;
    }

    private static bool IsTemplateAllowedByMods(
        string? templateId,
        IReadOnlySet<string> allowedModIds,
        Func<string, string?> resolveTemplateModId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return true;
        string? modId = resolveTemplateModId(templateId);
        if (string.IsNullOrWhiteSpace(modId))
            return true;
        return allowedModIds.Contains(modId);
    }

    private static string ResolveCardZhName(CardTemplate? tpl, GameDataRepository? repo)
    {
        if (tpl == null) return "";
        string? locTitle = repo?.Localization?.GetLocalizedTitle(tpl);
        if (!string.IsNullOrWhiteSpace(locTitle))
            return locTitle;
        string? locText = repo?.Localization?.GetLocalizedText(tpl.Localization?.Title);
        if (!string.IsNullOrWhiteSpace(locText))
            return locText;
        if (!string.IsNullOrWhiteSpace(tpl.DisplayTitle))
            return tpl.DisplayTitle;
        return tpl.InternalName ?? "";
    }

    private static GhostBoard EnrichBoardNamesIfNeeded(GhostBoard board, GameDataRepository? repo)
    {
        if (repo == null)
            return board;

        bool needsEnrich = false;
        if (board.Items != null)
        {
            for (int i = 0; i < board.Items.Count; i++)
            {
                var it = board.Items[i];
                if (it != null
                    && (string.IsNullOrWhiteSpace(it.ZhName) || string.IsNullOrWhiteSpace(it.InternalName))
                    && repo.GetCard(it.TemplateId) != null)
                {
                    needsEnrich = true;
                    break;
                }
            }
        }

        if (!needsEnrich && board.Skills != null)
        {
            for (int i = 0; i < board.Skills.Count; i++)
            {
                var sk = board.Skills[i];
                if (sk != null
                    && (string.IsNullOrWhiteSpace(sk.ZhName) || string.IsNullOrWhiteSpace(sk.InternalName))
                    && repo.GetCard(sk.TemplateId) != null)
                {
                    needsEnrich = true;
                    break;
                }
            }
        }

        if (!needsEnrich)
            return board;

        var enrichedItems = new List<GhostItem>(board.Items?.Count ?? 0);
        if (board.Items != null)
        {
            for (int i = 0; i < board.Items.Count; i++)
            {
                var it = board.Items[i];
                if (it == null) continue;
                if (string.IsNullOrWhiteSpace(it.ZhName) || string.IsNullOrWhiteSpace(it.InternalName))
                {
                    var tpl = repo.GetCard(it.TemplateId);
                    if (tpl != null)
                    {
                        enrichedItems.Add(new GhostItem
                        {
                            Slot = it.Slot,
                            Width = it.Width,
                            TemplateId = it.TemplateId,
                            InternalName = string.IsNullOrWhiteSpace(it.InternalName) ? (tpl.InternalName ?? "") : it.InternalName,
                            ZhName = string.IsNullOrWhiteSpace(it.ZhName) ? ResolveCardZhName(tpl, repo) : it.ZhName,
                            Tier = it.Tier,
                            Enchantment = it.Enchantment,
                            PermanentAttributes = it.PermanentAttributes,
                            PermanentAdd = it.PermanentAdd,
                            PermanentMult = it.PermanentMult,
                            CompletedQuests = it.CompletedQuests,
                            QuestProgress = it.QuestProgress,
                            ExtraTags = it.ExtraTags,
                        });
                        continue;
                    }
                }
                enrichedItems.Add(it);
            }
        }

        var enrichedSkills = new List<GhostSkill>(board.Skills?.Count ?? 0);
        if (board.Skills != null)
        {
            for (int i = 0; i < board.Skills.Count; i++)
            {
                var sk = board.Skills[i];
                if (sk == null) continue;
                if (string.IsNullOrWhiteSpace(sk.ZhName) || string.IsNullOrWhiteSpace(sk.InternalName))
                {
                    var tpl = repo.GetCard(sk.TemplateId);
                    if (tpl != null)
                    {
                        enrichedSkills.Add(new GhostSkill
                        {
                            TemplateId = sk.TemplateId,
                            InternalName = string.IsNullOrWhiteSpace(sk.InternalName) ? (tpl.InternalName ?? "") : sk.InternalName,
                            ZhName = string.IsNullOrWhiteSpace(sk.ZhName) ? ResolveCardZhName(tpl, repo) : sk.ZhName,
                            Tier = sk.Tier,
                            PermanentAttributes = sk.PermanentAttributes,
                            PermanentAdd = sk.PermanentAdd,
                            PermanentMult = sk.PermanentMult,
                            ExtraTags = sk.ExtraTags,
                        });
                        continue;
                    }
                }
                enrichedSkills.Add(sk);
            }
        }

        return new GhostBoard
        {
            BoardId = board.BoardId,
            Day = board.Day,
            SourceDay = board.SourceDay,
            TargetDay = board.TargetDay,
            Hero = board.Hero,
            Level = board.Level,
            HealthMax = board.HealthMax,
            Prestige = board.Prestige,
            Wins = board.Wins,
            Losses = board.Losses,
            UnlockedSlotsMask = board.UnlockedSlotsMask,
            PowerBucket = board.PowerBucket,
            EstimatedWinRate = board.EstimatedWinRate,
            Source = board.Source,
            Persona = board.Persona,
            Archetype = board.Archetype,
            DisplayName = board.DisplayName,
            IsPlayerGhost = board.IsPlayerGhost,
            IsCustomRule = board.IsCustomRule,
            PoolPartition = board.PoolPartition,
            Items = enrichedItems,
            Skills = enrichedSkills,
            SocketEffects = board.SocketEffects,
        };
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
    /// 静态池加载期强力卫生过滤：剔除 Day 1 超血(&gt;550)/Lv3+、不支持英雄、异常同卡>=3件、
    /// 故意演菜/乱搭的 AI 人格（CluelessNewbie / GreedyBust / MessyHybrid）、Day 4+ 低级垃圾拼盘与残缺盘面。
    /// </summary>
    private static bool IsCleanStaticGhostForLoad(GhostBoard ghost)
    {
        if (!IsSaneGhostForDay(ghost, ghost.Day)) return false;
        if (!IsSupportedClientHero(ghost.Hero)) return false;
        if (HasAbnormalDuplicateOrInvalidItems(ghost, repo: null)) return false;

        // 硬过滤故意演菜/胡乱混搭的合成 AI 人格，避免污染实战对手体验
        if (!string.IsNullOrEmpty(ghost.Persona) && IsJunkSyntheticPersona(ghost.Persona, ghost.Day))
            return false;

        bool isSyntheticAi = string.Equals(ghost.Source, "synthetic_selfplay", StringComparison.OrdinalIgnoreCase);
        if (isSyntheticAi)
        {
            if (ghost.Day >= 3 && ghost.Level < ghost.Day)
                return false;
            if (ghost.Day >= 4 && ghost.Skills.Count == 0)
                return false;
            // 剔除 Day 5+ 无核塞满 8+ 件全 1 格小垃圾的 AI 杂烩盘面
            if (ghost.Day >= 5 && ghost.Items.Count >= 8 && ghost.Items.All(it => Math.Clamp(it.Width, 1, 3) == 1))
                return false;
        }

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

    private static bool IsJunkSyntheticPersona(string persona, int day)
    {
        if (string.Equals(persona, "CluelessNewbie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(persona, "GreedyBust", StringComparison.OrdinalIgnoreCase)
            || string.Equals(persona, "MessyHybrid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return day >= 4 && string.Equals(persona, "Casual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 SQLite pvp_ghosts 表加载已沉淀的真人玩家镜像并热加入对应分区-天数分桶。</summary>
    public int LoadFromDb(Persistence db, bool includeCustomRule = true)
    {
        if (db == null) return 0;
        var rows = db.LoadPvpGhosts(2000, includeCustomRule: includeCustomRule);
        int added = 0;
        int crossVersionCount = 0;
        string currentHash = _currentGameDataHash;

        lock (_writeLock)
        {
            foreach (var row in rows)
            {
                var board = ParseDbRow(row);
                if (board == null) continue;
                if (!board.IsCustomRule && !IsSaneGhostForDay(board, board.Day)) continue;
                if (AddBoardUnlocked(board))
                {
                    added++;
                    if (!string.IsNullOrEmpty(currentHash)
                        && !IsExemptFromCrossVersionSanitization(board.Source)
                        && !string.Equals(board.GameDataHash, currentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        crossVersionCount++;
                    }
                }
            }
            if (added > 0)
                RebuildReadIndexUnlocked();
        }

        if (added > 0)
            Console.WriteLine($"[PvpGhostPool] loaded {added} player ghost boards from db (total={Count}, crossVersionReadSanitize={crossVersionCount})");
        return added;
    }

    /// <summary>校验镜像盘面是否至少包含一件在指定仓库中存在的合法物品模板（用于固定剧本缺 Mod 时的安全回落判定）。</summary>
    public static bool HasAnyValidItem(GhostBoard? board, GameDataRepository? repo)
    {
        if (board?.Items == null || board.Items.Count == 0 || repo == null)
            return false;
        for (int i = 0; i < board.Items.Count; i++)
        {
            var it = board.Items[i];
            if (it != null && !string.IsNullOrWhiteSpace(it.TemplateId) && repo.GetCard(it.TemplateId) != null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 从任意运行态 <see cref="RunState"/> 导出当前盘面快照为 <see cref="GhostBoard"/>（不受 <c>ShouldSkipGhostPool</c> / 沙盒隔离限制），
    /// 完整保留物品永久双层属性（PermanentAdd/PermanentMult）、任务进度、额外标签、技能与插槽效果，供沙盒捕获等场景使用。
    /// </summary>
    public static GhostBoard SnapshotBoardFromRun(
        RunState run,
        GameDataRepository? repo,
        int targetDay,
        string? displayName = null,
        string? poolPartition = null,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        int safeDay = Math.Max(1, targetDay);
        var (items, skills, socketEffects) = ExtractRunBoardComponents(run, repo);

        int wins = (int)run.Victories;
        int losses = (int)run.Defeats;
        int healthMax = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth));
        int prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);
        int level = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
        int unlockedSlots = run.UnlockedSlots > 0 ? run.UnlockedSlots : DefaultUnlockedMaskForDay(safeDay);
        string hero = run.Hero == EHero.Common ? "Vanessa" : run.Hero.ToString();

        string resolvedPartition;
        if (!string.IsNullOrWhiteSpace(poolPartition))
        {
            resolvedPartition = poolPartition.Trim().ToLowerInvariant();
        }
        else
        {
            resolvedPartition = NormalizePartition(run.CustomRules?.ResolveGhostPoolPartition(), run.IsCustomRuleRun);
            if (string.Equals(resolvedPartition, "none", StringComparison.OrdinalIgnoreCase))
                resolvedPartition = StandardPartition;
        }

        bool effectiveCustom = run.IsCustomRuleRun
            || !string.Equals(resolvedPartition, StandardPartition, StringComparison.OrdinalIgnoreCase);

        string powerBucket = wins >= Math.Max(1, safeDay - 1)
            ? "Strong"
            : losses >= wins + 2
                ? "Weak"
                : "Normal";
        double estWr = Math.Clamp(0.45 + wins * 0.05 - losses * 0.04, 0.15, 0.95);
        string runKey = string.IsNullOrWhiteSpace(run.RunId) ? "run" : run.RunId;

        return new GhostBoard
        {
            BoardId = $"snapshot_{runKey}_d{safeDay}",
            Day = safeDay,
            SourceDay = safeDay,
            TargetDay = safeDay,
            Hero = hero,
            Level = level,
            HealthMax = healthMax,
            Prestige = prestige,
            Wins = wins,
            Losses = losses,
            UnlockedSlotsMask = unlockedSlots,
            PowerBucket = powerBucket,
            EstimatedWinRate = estWr,
            Source = source ?? (run.IsSandboxRun ? "sandbox" : "player"),
            DisplayName = displayName,
            IsPlayerGhost = true,
            IsCustomRule = effectiveCustom,
            PoolPartition = resolvedPartition,
            Items = items,
            Skills = skills,
            SocketEffects = socketEffects.Count > 0 ? socketEffects : null,
        };
    }

    private static (List<GhostItem> Items, List<GhostSkill> Skills, List<GhostSocketEffect> SocketEffects) ExtractRunBoardComponents(
        RunState run,
        GameDataRepository? repo)
    {
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
            Dictionary<string, double>? permAdd = null;
            Dictionary<string, double>? permMult = null;
            List<string>? completedQuests = null;
            Dictionary<string, double>? questProgress = null;
            List<string>? extraTags = null;

            if (entry.CoreCard != null)
            {
                var core = entry.CoreCard;
                // 仅持久化 CoreCard 真实永久修改层（PermanentAdd / PermanentMult），绝不拷入含光环（Aura）/ 限时层（Hour/Day）
                var permKeys = new HashSet<string>(core.PermanentAdd.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var mk in core.PermanentMult.Keys)
                    permKeys.Add(mk);

                foreach (string key in permKeys)
                {
                    if (IsDerivedGhostAttributeKey(key))
                        continue;
                    double baseVal = core.BaseAttributes.GetValueOrDefault(key, 0);
                    double addVal = core.PermanentAdd.GetValueOrDefault(key, 0);
                    double mult = core.PermanentMult.GetValueOrDefault(key, 1.0);
                    if (Math.Abs(addVal) > 1e-6)
                    {
                        permAdd ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        permAdd[key] = addVal;
                    }
                    if (Math.Abs(mult - 1.0) > 1e-6)
                    {
                        permMult ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        permMult[key] = mult;
                    }
                    int unscaled = (int)Math.Round((baseVal + addVal) * mult);
                    if (Math.Abs(unscaled - baseVal) > 0.001)
                        perm[key] = unscaled;
                }

                if (core.CompletedQuests.Count > 0)
                    completedQuests = core.CompletedQuests.ToList();
                if (core.QuestProgress.Count > 0)
                    questProgress = new Dictionary<string, double>(core.QuestProgress, StringComparer.OrdinalIgnoreCase);
                if (core.ExtraTags.Count > 0)
                    extraTags = core.ExtraTags.ToList();
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

            string templateId = string.IsNullOrWhiteSpace(entry.CustomCardId) ? entry.TemplateId.ToString() : entry.CustomCardId!;
            CardTemplate? tpl = repo?.GetCard(templateId);

            items.Add(new GhostItem
            {
                Slot = Math.Clamp(slot, 0, 9),
                Width = width,
                TemplateId = templateId,
                InternalName = tpl?.InternalName ?? "",
                ZhName = tpl != null ? ResolveCardZhName(tpl, repo) : "",
                Tier = entry.Tier.ToString(),
                Enchantment = entry.Enchantment?.ToString(),
                PermanentAttributes = perm,
                PermanentAdd = permAdd,
                PermanentMult = permMult,
                CompletedQuests = completedQuests,
                QuestProgress = questProgress,
                ExtraTags = extraTags,
            });
        }

        var skills = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill)
            .Select(c =>
            {
                Dictionary<string, int>? skPerm = null;
                Dictionary<string, double>? skAdd = null;
                Dictionary<string, double>? skMult = null;
                List<string>? skTags = null;
                if (c.CoreCard != null)
                {
                    var core = c.CoreCard;
                    var keys = new HashSet<string>(core.PermanentAdd.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (var mk in core.PermanentMult.Keys) keys.Add(mk);
                    foreach (string key in keys)
                    {
                        if (IsDerivedGhostAttributeKey(key)) continue;
                        double baseVal = core.BaseAttributes.GetValueOrDefault(key, 0);
                        double addVal = core.PermanentAdd.GetValueOrDefault(key, 0);
                        double mult = core.PermanentMult.GetValueOrDefault(key, 1.0);
                        if (Math.Abs(addVal) > 1e-6)
                        {
                            skAdd ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            skAdd[key] = addVal;
                        }
                        if (Math.Abs(mult - 1.0) > 1e-6)
                        {
                            skMult ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            skMult[key] = mult;
                        }
                        int unscaled = (int)Math.Round((baseVal + addVal) * mult);
                        if (Math.Abs(unscaled - baseVal) > 0.001)
                        {
                            skPerm ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            skPerm[key] = unscaled;
                        }
                    }
                    if (core.ExtraTags.Count > 0)
                        skTags = core.ExtraTags.ToList();
                }

                string templateId = string.IsNullOrWhiteSpace(c.CustomCardId) ? c.TemplateId.ToString() : c.CustomCardId!;
                CardTemplate? tpl = repo?.GetCard(templateId);

                return new GhostSkill
                {
                    TemplateId = templateId,
                    InternalName = tpl?.InternalName ?? "",
                    ZhName = tpl != null ? ResolveCardZhName(tpl, repo) : "",
                    Tier = c.Tier.ToString(),
                    PermanentAttributes = skPerm,
                    PermanentAdd = skAdd,
                    PermanentMult = skMult,
                    ExtraTags = skTags,
                };
            })
            .ToList();

        var socketEffects = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.Type == ECardType.SocketEffect)
            .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99)
            .Select(c =>
            {
                Dictionary<string, int>? sePerm = null;
                if (c.CoreCard != null && (c.CoreCard.PermanentAdd.Count > 0 || c.CoreCard.PermanentMult.Count > 0))
                {
                    sePerm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in c.CoreCard.PermanentAdd.Keys.Concat(c.CoreCard.PermanentMult.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (IsDerivedGhostAttributeKey(k)) continue;
                        double baseVal = c.CoreCard.BaseAttributes.GetValueOrDefault(k, 0);
                        double addVal = c.CoreCard.PermanentAdd.GetValueOrDefault(k, 0);
                        double mult = c.CoreCard.PermanentMult.GetValueOrDefault(k, 1.0);
                        int unscaled = (int)Math.Round((baseVal + addVal) * mult);
                        if (Math.Abs(unscaled - baseVal) > 0.001)
                            sePerm[k] = unscaled;
                    }
                    if (sePerm.Count == 0) sePerm = null;
                }
                return new GhostSocketEffect
                {
                    Slot = Math.Clamp(c.Socket.HasValue ? (int)c.Socket.Value : (c.CoreCard?.StartSlot ?? 0), 0, 9),
                    TemplateId = string.IsNullOrWhiteSpace(c.CustomCardId) ? c.TemplateId.ToString() : c.CustomCardId!,
                    Tier = c.Tier.ToString(),
                    Enchantment = c.Enchantment?.ToString(),
                    PermanentAttributes = sePerm,
                };
            })
            .ToList();

        return (items, skills, socketEffects);
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

        var (items, skills, socketEffects) = ExtractRunBoardComponents(run, repo: null);

        if (items.Count == 0)
            return null;

        int wins = (int)run.Victories;
        int losses = (int)run.Defeats;
        int healthMax = Math.Max(100, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth));
        int prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);
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
            SocketEffects = socketEffects.Count > 0 ? socketEffects : null,
        };

        string currentGameDataHash = _currentGameDataHash;
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
            GameDataHash = currentGameDataHash,
            Items = items,
            Skills = skills,
            SocketEffects = socketEffects.Count > 0 ? socketEffects : null,
        };

        // 标准分区拒绝超血脏镜像；自定义/无尽高分区分允许其真实强度入专属分区，绝不污染标准池
        if (!effectiveCustom && !IsSaneGhostForDay(board, day))
            return null;

        string effectiveModeId = run.CustomRules?.SpecialModeId switch
        {
            ESpecialModeId.EndlessTower => "endless_tower",
            ESpecialModeId.BossRush => "boss_rush",
            ESpecialModeId.DailyChallenge => "daily",
            ESpecialModeId.CrossHeroMayhem => "cross_hero",
            ESpecialModeId.MutatorChallenge => "mutator",
            _ => effectiveCustom ? resolvedPartition : StandardPartition,
        };

        string boardJson = JsonSerializer.Serialize(payload, JsonOpts);
        var now = DateTime.UtcNow;

        // D2-P1-10 & v15.6 A3：完整传入 ModeId / PoolPartition / GameDataHash（单一权威持久化于数据库列，绝不写入 payload JSON）
        db.InsertPvpGhost(new Persistence.PvpGhostRow(
            Id: 0,
            Day: day,
            Hero: hero,
            Wins: wins,
            Losses: losses,
            HealthMax: healthMax,
            Prestige: prestige,
            Level: level,
            UnlockedSlots: unlockedSlots,
            BoardJson: boardJson,
            CreatedAt: now,
            IsCustomRule: effectiveCustom,
            ModeId: effectiveModeId,
            PoolPartition: resolvedPartition,
            GameDataHash: currentGameDataHash));

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
                var picked = PickFromBucket(endlessBucket, effectiveDay, playerWins, playerLosses, rng, repo, isStandardPartition: false);
                if (picked != null) return picked;
            }

            GhostBoard? baseStrong = TryMaterializeRandom(snap.HighDayStrongOrMeta, rng, repo)
                ?? PickStandardBucketOrFallback(snap, Math.Clamp(effectiveDay, 10, 15), playerWins, playerLosses, rng, repo);
            return BuildScaledEndlessGhost(baseStrong, effectiveDay, rng, repo);
        }

        // 2. 非标准专属分区：同分区真人镜像与标准实盘池按实际样本量等概率混合（绝不反向污染标准池，也不让少量自录盘面垄断）
        if (!string.Equals(targetPartition, StandardPartition, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPartition, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (snap.Buckets.TryGetValue(new BucketKey(targetPartition, effectiveDay), out var customBucket)
                && customBucket.All.Length > 0)
            {
                int customCount = customBucket.All.Length;
                int standardCount = snap.Buckets.TryGetValue(new BucketKey(StandardPartition, effectiveDay), out var stdBucket)
                    ? stdBucket.All.Length
                    : 0;
                int combinedCount = customCount + standardCount;
                if (standardCount == 0 || rng.Next(combinedCount) < customCount)
                {
                    var picked = PickFromBucket(customBucket, effectiveDay, playerWins, playerLosses, rng, repo, isStandardPartition: false);
                    if (picked != null) return picked;
                }
            }
        }

        // 3. 标准分区（或自定义分区样本不足时的只读回退）
        return PickStandardBucketOrFallback(snap, effectiveDay, playerWins, playerLosses, rng, repo);
    }

    private GhostBoard? TryMaterializeRandom(GhostIndexEntry[] entries, Random rng, GameDataRepository? repo)
    {
        if (entries.Length == 0) return null;
        int maxAttempts = Math.Min(entries.Length, 12);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var cand = entries[rng.Next(entries.Length)];
            var board = MaterializeEntry(cand);
            if (board != null && !HasInvalidRepoItem(board, repo))
                return board;
        }
        return null;
    }

    private GhostBoard PickStandardBucketOrFallback(
        GhostIndexSnapshot snap,
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo)
    {
        if (snap.Buckets.TryGetValue(new BucketKey(StandardPartition, day), out var exactBucket))
        {
            var picked = PickFromBucket(exactBucket, day, playerWins, playerLosses, rng, repo, isStandardPartition: true);
            if (picked != null) return picked;
        }

        // 若天数超过静态桶上限（如 Day 16+），向前探测最近有样本的标准桶
        if (day > 15)
        {
            for (int probe = Math.Min(day, 20); probe >= 10; probe--)
            {
                if (snap.Buckets.TryGetValue(new BucketKey(StandardPartition, probe), out var nearBucket))
                {
                    var picked = PickFromBucket(nearBucket, probe, playerWins, playerLosses, rng, repo, isStandardPartition: true);
                    if (picked != null) return picked;
                }
            }
        }

        if (day <= 1)
            return BuildFallbackGhost(1, rng, repo);

        // 全标准集内按合理区间回退
        var (minHp, maxHp, maxLv) = GetSaneBoundsForDay(day);
        var eligible = new List<GhostIndexEntry>();
        foreach (var e in snap.StandardAll)
        {
            int effMinHp = e.IsPlayerGhost ? Math.Min(100, minHp) : minHp;
            int effMaxHp = (e.IsPlayerGhost && day == 1) ? 550 : maxHp;
            int effMaxLv = (e.IsPlayerGhost && day == 1) ? 2 : maxLv;
            if (e.HealthMax >= effMinHp && e.HealthMax <= effMaxHp && e.Level >= 1 && e.Level <= effMaxLv)
                eligible.Add(e);
        }
        if (eligible.Count > 0)
        {
            var picked = PickByMedianBandDistribution(eligible, day, rng, repo);
            if (picked != null) return picked;
        }

        return BuildFallbackGhost(day, rng, repo);
    }

    private GhostBoard? PickFromBucket(
        DayPartitionBucket bucket,
        int day,
        int playerWins,
        int playerLosses,
        Random rng,
        GameDataRepository? repo,
        bool isStandardPartition = true)
    {
        // 真实社区/天梯盘面（巴扎++ 十胜库 / 外部天梯统计源实测盘）享有 90% 压倒性优先权，彻底告别胡扯 AI
        GhostIndexEntry[] staticSource;
        if (bucket.RealCommunityPreferred.Length > 0
            && (bucket.RealCommunityPreferred.Length >= 30 || rng.NextDouble() < 0.90))
        {
            staticSource = bucket.RealCommunityPreferred;
        }
        else
        {
            staticSource = bucket.StaticPreferred.Length > 0 ? bucket.StaticPreferred : bucket.All;
        }
        if (staticSource.Length == 0) return null;

        string[] preferredBuckets = playerLosses >= playerWins + 2
            ? ["Weak", "Normal"]
            : (day >= 3 && playerWins >= day - 1)
                ? ["Strong", "Normal", "MetaBoss"]
                : ["Normal", "Strong"];

        var tier1 = new List<GhostIndexEntry>();
        var tier2 = new List<GhostIndexEntry>();
        var fallback = new List<GhostIndexEntry>();
        var seenBoardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ConsiderCandidate(GhostIndexEntry e)
        {
            if (!string.IsNullOrEmpty(e.BoardId) && !seenBoardIds.Add(e.BoardId)) return;
            // D2-P1-10：仅标准分区施加 Day 1 血量/等级上限拦截，允许高血自定义/词条分区正常匹配 Day 1 同分区对手
            if (isStandardPartition && day <= 1 && (e.Level > 2 || e.HealthMax > 550)) return;

            fallback.Add(e);
            bool bucketHit = MatchesPowerBucketCode(e.PowerBucketCode, preferredBuckets);
            int winDiff = Math.Abs(e.Wins - playerWins);
            if (bucketHit && winDiff <= 1)
                tier1.Add(e);
            else if (bucketHit || winDiff <= 2)
                tier2.Add(e);
        }

        // 所有真实盘面（社区/天梯实盘 + 本服 SQLite 真人镜像）进入同一候选池，严格按 1:1 盘面等概率参与筛选与抽样
        for (int i = 0; i < staticSource.Length; i++)
            ConsiderCandidate(staticSource[i]);
        for (int i = 0; i < bucket.PlayerGhosts.Length; i++)
            ConsiderCandidate(bucket.PlayerGhosts[i]);

        // 按优先级逐级做严格等概率拒绝采样（单次抽中才按偏移读盘并校验 HasInvalidRepoItem，数学分布 100% 等价）
        if (tier1.Count > 0)
        {
            var picked = PickByMedianBandDistribution(tier1, day, rng, repo);
            if (picked != null) return picked;
        }
        if (tier2.Count > 0)
        {
            var picked = PickByMedianBandDistribution(tier2, day, rng, repo);
            if (picked != null) return picked;
        }
        if (fallback.Count > 0)
        {
            return PickByMedianBandDistribution(fallback, day, rng, repo);
        }
        return null;
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
        // D2-P1-10：同步校验 Skills 与 SocketEffects，任一模板在当前 repo 中缺失即拒绝抽中
        if (board.Skills != null)
        {
            for (int i = 0; i < board.Skills.Count; i++)
            {
                if (repo.GetCard(board.Skills[i].TemplateId) == null)
                    return true;
            }
        }
        if (board.SocketEffects != null)
        {
            for (int i = 0; i < board.SocketEffects.Count; i++)
            {
                if (repo.GetCard(board.SocketEffects[i].TemplateId) == null)
                    return true;
            }
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
                PermanentAdd = it.PermanentAdd != null ? new Dictionary<string, double>(it.PermanentAdd, StringComparer.OrdinalIgnoreCase) : null,
                PermanentMult = it.PermanentMult != null ? new Dictionary<string, double>(it.PermanentMult, StringComparer.OrdinalIgnoreCase) : null,
                CompletedQuests = it.CompletedQuests != null ? new List<string>(it.CompletedQuests) : null,
                QuestProgress = it.QuestProgress != null ? new Dictionary<string, double>(it.QuestProgress, StringComparer.OrdinalIgnoreCase) : null,
                ExtraTags = it.ExtraTags != null ? new List<string>(it.ExtraTags) : null,
            });
        }

        return new GhostBoard
        {
            BoardId = $"endless_d{targetDay}_{source.BoardId}",
            Day = targetDay,
            SourceDay = source.EffectiveSourceDay,
            TargetDay = targetDay,
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
            SocketEffects = source.SocketEffects != null ? new List<GhostSocketEffect>(source.SocketEffects) : null,
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

    private GhostBoard? PickByMedianBandDistribution(
        List<GhostIndexEntry> candidates,
        int day,
        Random rng,
        GameDataRepository? repo)
    {
        if (candidates.Count == 0) return null;
        double median = GetExpectedMedianHealthForDay(day);
        int normalMin = (int)Math.Round(median * 0.70);
        int normalMax = (int)Math.Round(median * 1.80);
        int tailMin = (int)Math.Round(median * 2.20);

        var normalAll = new List<GhostIndexEntry>();
        var ultraTail = new List<GhostIndexEntry>();
        var midAll = new List<GhostIndexEntry>();

        for (int i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e.HealthMax >= normalMin && e.HealthMax <= normalMax)
                normalAll.Add(e);
            else if (e.HealthMax > tailMin)
                ultraTail.Add(e);
            else
                midAll.Add(e);
        }

        // 所有真人数据（社区/天梯实盘与本服真人镜像）在各血量带内严格等概率均匀抽样，无额外倾斜权重
        double roll = rng.NextDouble();
        if (roll < 0.03 && ultraTail.Count > 0)
        {
            var picked = DrawValidFromBand(ultraTail, rng, repo);
            if (picked != null) return picked;
        }

        if (roll < 0.05 && midAll.Count > 0)
        {
            var picked = DrawValidFromBand(midAll, rng, repo);
            if (picked != null) return picked;
        }

        if (normalAll.Count > 0)
        {
            var picked = DrawValidFromBand(normalAll, rng, repo);
            if (picked != null) return picked;
        }

        if (midAll.Count > 0)
        {
            var picked = DrawValidFromBand(midAll, rng, repo);
            if (picked != null) return picked;
        }

        if (ultraTail.Count > 0)
        {
            var picked = DrawValidFromBand(ultraTail, rng, repo);
            if (picked != null) return picked;
        }

        return null;
    }

    private GhostBoard? DrawValidFromBand(List<GhostIndexEntry> band, Random rng, GameDataRepository? repo)
    {
        // 无放回均匀拒绝采样：抽中不合法项则 swap-remove，保证对剩余合法项保持严格均匀概率分布
        while (band.Count > 0)
        {
            int idx = rng.Next(band.Count);
            var entry = band[idx];
            var board = MaterializeEntry(entry);
            if (board != null && !HasInvalidRepoItem(board, repo))
                return board;

            int last = band.Count - 1;
            band[idx] = band[last];
            band.RemoveAt(last);
        }
        return null;
    }

    private bool AddBoardUnlocked(GhostBoard board)
        => AddEntryUnlocked(CreateInMemoryEntry(board));

    private bool AddEntryUnlocked(GhostIndexEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.BoardId) && !_knownBoardIds.Add(entry.BoardId))
            return false;

        int bucketDay = Math.Clamp((int)entry.Day, 1, 99);
        var key = new BucketKey(entry.Partition, bucketDay);
        if (!_mutableBuckets.TryGetValue(key, out var list))
        {
            list = new List<GhostIndexEntry>();
            _mutableBuckets[key] = list;
        }
        list.Add(entry);
        _all.Add(entry);
        TrimDayBucketUnlocked(list);
        return true;
    }

    private void TrimDayBucketUnlocked(List<GhostIndexEntry> dayList)
    {
        while (dayList.Count > MaxBoardsPerDayBucket)
        {
            int victimIdx = dayList.FindIndex(IsEvictablePlayerEntry);
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
        var standardAll = new List<GhostIndexEntry>();
        var highDayStrong = new List<GhostIndexEntry>();
        int playerCount = 0;
        int customCount = 0;
        int diskCount = 0;

        foreach (var (key, list) in _mutableBuckets)
        {
            var allArr = list.ToArray();
            var playerList = new List<GhostIndexEntry>();
            var full10List = new List<GhostIndexEntry>();
            var realCommAll = new List<GhostIndexEntry>();
            var realCommFull10 = new List<GhostIndexEntry>();
            var highPower = new List<GhostIndexEntry>();

            for (int i = 0; i < allArr.Length; i++)
            {
                var e = allArr[i];
                if (e.IsPlayerGhost) playerList.Add(e);
                if (e.IsFull10Slots)
                    full10List.Add(e);
                if (e.IsRealCommunityGhost)
                {
                    realCommAll.Add(e);
                    if (e.IsFull10Slots) realCommFull10.Add(e);
                }
                if (e.IsHighPower)
                    highPower.Add(e);
            }

            buckets[key] = new DayPartitionBucket
            {
                All = allArr,
                StaticPreferred = full10List.Count > 0 ? full10List.ToArray() : allArr,
                RealCommunityPreferred = realCommFull10.Count >= 16
                    ? realCommFull10.ToArray()
                    : realCommAll.ToArray(),
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

        var byBoardId = new Dictionary<string, GhostIndexEntry>(_all.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _all.Count; i++)
        {
            var e = _all[i];
            if (e.IsPlayerGhost) playerCount++;
            if (e.IsCustomRule) customCount++;
            if (e.IsDiskBacked) diskCount++;
            if (!string.IsNullOrWhiteSpace(e.BoardId))
                byBoardId.TryAdd(e.BoardId, e);
        }

        _index = new GhostIndexSnapshot
        {
            Buckets = buckets.ToFrozenDictionary(),
            StandardAll = standardAll.ToArray(),
            HighDayStrongOrMeta = highDayStrong.ToArray(),
            AllEntries = _all.ToArray(),
            ByBoardId = byBoardId.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            TotalCount = _all.Count,
            PlayerGhostCount = playerCount,
            CustomRuleGhostCount = customCount,
            DiskIndexedCount = diskCount,
        };
    }

    private static GhostIndexSnapshot BuildEmptySnapshot() => new()
    {
        Buckets = FrozenDictionary<BucketKey, DayPartitionBucket>.Empty,
        StandardAll = Array.Empty<GhostIndexEntry>(),
        HighDayStrongOrMeta = Array.Empty<GhostIndexEntry>(),
        AllEntries = Array.Empty<GhostIndexEntry>(),
        ByBoardId = FrozenDictionary<string, GhostIndexEntry>.Empty,
        TotalCount = 0,
        PlayerGhostCount = 0,
        CustomRuleGhostCount = 0,
        DiskIndexedCount = 0,
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

    private static bool IsEvictablePlayerEntry(GhostIndexEntry entry)
        => entry.IsPlayerGhost ||
           entry.BoardId.StartsWith("player_", StringComparison.OrdinalIgnoreCase);

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
            string partition = NormalizePartition(
                !string.IsNullOrWhiteSpace(row.PoolPartition) && !string.Equals(row.PoolPartition, StandardPartition, StringComparison.OrdinalIgnoreCase)
                    ? row.PoolPartition
                    : (payload.PoolPartition ?? row.PoolPartition),
                isCustom);

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
                GameDataHash = row.GameDataHash ?? "",
                Items = payload.Items ?? new List<GhostItem>(),
                Skills = payload.Skills ?? new List<GhostSkill>(),
                SocketEffects = payload.SocketEffects,
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

    private static GhostBoard? ParseLineUtf8(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var board = JsonSerializer.Deserialize<GhostBoard>(utf8Json, JsonOpts);
            if (board == null) return null;
            int day = Math.Max(1, board.Day);
            int healthMax = Math.Max(100, board.HealthMax);
            string hero = string.IsNullOrWhiteSpace(board.Hero) ? "Vanessa" : board.Hero;
            string powerBucket = string.IsNullOrWhiteSpace(board.PowerBucket) ? "Normal" : board.PowerBucket;
            string normPartition = NormalizePartition(board.PoolPartition, board.IsCustomRule);
            return new GhostBoard
            {
                BoardId = board.BoardId ?? "",
                Day = day,
                SourceDay = board.SourceDay,
                TargetDay = board.TargetDay,
                Hero = hero,
                Level = Math.Max(1, board.Level),
                HealthMax = healthMax,
                Prestige = board.Prestige > 0 ? board.Prestige : 20,
                Wins = board.Wins,
                Losses = board.Losses,
                UnlockedSlotsMask = board.UnlockedSlotsMask > 0 ? board.UnlockedSlotsMask : DefaultUnlockedMaskForDay(day),
                PowerBucket = powerBucket,
                EstimatedWinRate = board.EstimatedWinRate > 0 ? board.EstimatedWinRate : 0.5,
                Source = board.Source ?? "",
                Persona = board.Persona ?? "",
                Archetype = board.Archetype ?? "",
                DisplayName = board.DisplayName,
                IsPlayerGhost = board.IsPlayerGhost,
                IsCustomRule = board.IsCustomRule,
                PoolPartition = normPartition,
                Items = board.Items ?? new List<GhostItem>(),
                Skills = board.Skills ?? new List<GhostSkill>(),
                SocketEffects = board.SocketEffects,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedClientHero(string? hero)
        => !string.IsNullOrWhiteSpace(hero) && SupportedClientHeroes.Contains(hero.Trim());

    /// <summary>判断镜像物品是否携带 Tiny 缩小状态（PermanentAdd / PermanentAttributes 正值 Tiny 或历史 ExtraTags 含 "Tiny"）。</summary>
    public static bool HasGhostItemTiny(GhostItem? it)
    {
        if (it == null) return false;
        if (it.PermanentAdd != null)
        {
            foreach (var (k, v) in it.PermanentAdd)
            {
                if (string.Equals(k, "Tiny", StringComparison.OrdinalIgnoreCase) && double.IsFinite(v) && v > 0)
                    return true;
            }
        }
        if (it.PermanentAttributes != null)
        {
            foreach (var (k, v) in it.PermanentAttributes)
            {
                if (string.Equals(k, "Tiny", StringComparison.OrdinalIgnoreCase) && v > 0)
                    return true;
            }
        }
        if (it.ExtraTags != null)
        {
            for (int i = 0; i < it.ExtraTags.Count; i++)
            {
                if (string.Equals(it.ExtraTags[i]?.Trim(), "Tiny", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>计算镜像物品的有效占格宽度（带 Tiny 时 Large → 2、Medium → 1、Small → 1；无模板回退已存 Width）。</summary>
    public static int ResolveGhostItemEffectiveWidth(GhostItem it, CardTemplate? tpl)
    {
        if (tpl != null)
        {
            bool isTiny = HasGhostItemTiny(it);
            return isTiny
                ? (tpl.CardSize == CardSize.Large ? 2 : 1)
                : Math.Clamp(tpl.CardSize.SlotCount(), 1, 3);
        }
        return Math.Clamp(it.Width, 1, 3);
    }

    private static int GetTotalSlotWidth(GhostBoard board, GameDataRepository? repo)
    {
        int total = 0;
        foreach (var it in board.Items)
        {
            var tpl = repo?.GetCard(it.TemplateId);
            total += ResolveGhostItemEffectiveWidth(it, tpl);
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
                var tpl = repo?.GetCard(it.TemplateId);
                int w = ResolveGhostItemEffectiveWidth(it, tpl);
                for (int offset = 0; offset < w; offset++)
                {
                    int slot = it.Slot + offset;
                    if (slot is >= 0 and < 10)
                        mask |= 1 << slot;
                }
            }
        }
        if (ghost.SocketEffects != null)
        {
            foreach (var se in ghost.SocketEffects)
            {
                if (se != null && se.Slot is >= 0 and < 10)
                    mask |= 1 << se.Slot;
            }
        }
        return mask & 1023;
    }

    /// <summary>判断镜像来源是否豁免跨版本读时清洗（编排台自定义盘面与沙盒盘面绝不触及）。</summary>
    private static bool IsExemptFromCrossVersionSanitization(string? source)
        => string.Equals(source, "custom", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, "sandbox", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断属性键是否为受跨版本存在性与非倒扣守卫约束的输出属性（显式豁免 CooldownMax）。</summary>
    private static bool IsGuardedOutputAttribute(string attr)
        => !string.Equals(attr, "CooldownMax", StringComparison.OrdinalIgnoreCase)
        && (CardInstance.IsOutputMagnitudeAttribute(attr)
            || string.Equals(attr, "AmmoMax", StringComparison.OrdinalIgnoreCase));

    /// <summary>判断属性键能否解析为当前版本合法的 ECardAttributeType 枚举成员（拒绝纯数字字符串伪匹配）。</summary>
    private static bool IsValidCardAttributeKey(string? key)
        => !string.IsNullOrWhiteSpace(key)
        && !char.IsDigit(key[0])
        && key[0] != '-'
        && Enum.TryParse<ECardAttributeType>(key, ignoreCase: true, out var parsed)
        && Enum.IsDefined(typeof(ECardAttributeType), parsed);

    private static bool TryResolveValidEnchantment(CardTemplate tpl, string enchantment, out string canonical)
    {
        if (tpl.Enchantments != null)
        {
            if (tpl.Enchantments.ContainsKey(enchantment))
            {
                canonical = enchantment;
                return true;
            }
            foreach (string key in tpl.Enchantments.Keys)
            {
                if (string.Equals(key, enchantment, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = key;
                    return true;
                }
            }
        }
        canonical = "";
        return false;
    }

    private static bool TryResolveValidQuestKey(CardTemplate tpl, string questKey, out string canonical)
    {
        if (tpl.Quests is { Count: > 0 })
        {
            for (int qi = 0; qi < tpl.Quests.Count; qi++)
            {
                var entries = tpl.Quests[qi].Entries;
                for (int ei = 0; ei < entries.Count; ei++)
                {
                    string attr = entries[ei].AttributeType;
                    if (!string.IsNullOrWhiteSpace(attr)
                        && string.Equals(attr, questKey, StringComparison.OrdinalIgnoreCase))
                    {
                        canonical = attr;
                        return true;
                    }
                }
            }
        }
        canonical = "";
        return false;
    }

    /// <summary>把 GhostBoard 转换为可供 BazaarCore.CombatSimulator 直接演算的 PlayerState。</summary>
    public static PlayerState ToPlayerState(GhostBoard ghost, GameDataRepository repo)
        => ToPlayerState(ghost, repo, _activeGameDataHash, auditCollector: null);

    /// <summary>
    /// 把 GhostBoard 转换为 PlayerState，显式支持注入当前 GameDataHash 与审计收集器：
    /// 1) 统一先挂载任务并重建品阶/附魔/任务奖励，再挂载分层或平铺永久属性（消除平铺+已完成任务双倍叠算）；
    /// 2) 仅对旧版本罗盘锚点（real_compass_anchor）与跨版本真人镜像（含空指纹 "" 视作未知版本，不回写库）执行双轨读时清洗；
    /// 3) 编排台自定义盘面（custom）、沙盒盘面（sandbox）与当期同版本真人镜像严格零改动。
    /// </summary>
    public static PlayerState ToPlayerState(
        GhostBoard ghost,
        GameDataRepository repo,
        string? currentGameDataHash,
        GhostSanitizationAuditCollector? auditCollector = null)
    {
        EHero parsedHero = ParseHero(ghost.Hero);
        string safeHero = parsedHero.ToString();
        string label = ghost.IsPlayerGhost
            ? $"{safeHero}(真人镜像)"
            : $"{safeHero}镜像";

        string effectiveHash = currentGameDataHash ?? _activeGameDataHash;
        bool isExempt = IsExemptFromCrossVersionSanitization(ghost.Source);
        bool isLegacyAnchor = string.Equals(ghost.Source, "real_compass_anchor", StringComparison.OrdinalIgnoreCase);
        bool isCrossVersionPlayerGhost = !isExempt
            && ghost.IsPlayerGhost
            && !string.IsNullOrEmpty(effectiveHash)
            && !string.Equals(ghost.GameDataHash, effectiveHash, StringComparison.OrdinalIgnoreCase);
        bool shouldSanitize = !isExempt && (isLegacyAnchor || isCrossVersionPlayerGhost);

        string auditSource = !string.IsNullOrWhiteSpace(ghost.Source)
            ? ghost.Source
            : (ghost.IsPlayerGhost ? "player_ghost" : "unknown");
        var activeCollector = auditCollector ?? (shouldSanitize ? RuntimeSanitizationAudit : null);
        bool boardAffected = false;

        void RecordClean(string tplId, string attr, string reason)
        {
            boardAffected = true;
            activeCollector?.Record(auditSource, tplId, attr, reason);
        }

        void ApplySanitizedLayeredAttributes(
            CardInstance targetCard,
            string tplId,
            Dictionary<string, double>? permAdd,
            Dictionary<string, double>? permMult)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (permAdd != null)
            {
                foreach (string k in permAdd.Keys) keys.Add(k);
            }
            if (permMult != null)
            {
                foreach (string k in permMult.Keys) keys.Add(k);
            }

            foreach (string ak in keys)
            {
                if (IsDerivedGhostAttributeKey(ak))
                    continue;

                double addVal = 0.0;
                bool hasAdd = permAdd != null
                    && permAdd.TryGetValue(ak, out addVal)
                    && double.IsFinite(addVal)
                    && Math.Abs(addVal) > 1e-6;
                if (!hasAdd) addVal = 0.0;

                double multVal = 1.0;
                bool hasMult = permMult != null
                    && permMult.TryGetValue(ak, out multVal)
                    && double.IsFinite(multVal)
                    && Math.Abs(multVal - 1.0) > 1e-6;
                if (!hasMult) multVal = 1.0;

                if (!hasAdd && !hasMult)
                    continue;

                if (!IsValidCardAttributeKey(ak))
                {
                    RecordClean(tplId, ak, "invalid_attribute_key");
                    continue;
                }

                if (IsGuardedOutputAttribute(ak))
                {
                    bool hasOutputAttr = targetCard.BaseAttributes.ContainsKey(ak)
                        || targetCard.EnchantmentBaseAttributes.ContainsKey(ak);
                    if (!hasOutputAttr)
                    {
                        RecordClean(tplId, ak, "orphan_output_attribute");
                        continue;
                    }

                    double baseVal = targetCard.BaseAttributes.TryGetValue(ak, out double bv)
                        ? bv
                        : targetCard.EnchantmentBaseAttributes.GetValueOrDefault(ak, 0);
                    double effAdd = hasAdd ? addVal : 0.0;
                    double effMult = hasMult ? multVal : 1.0;
                    double projected = (baseVal + effAdd) * effMult;
                    if (projected < baseVal - 1e-6)
                    {
                        if (hasAdd && effAdd < -1e-6)
                        {
                            RecordClean(tplId, ak, "negative_output_delta");
                            hasAdd = false;
                            effAdd = 0.0;
                            projected = baseVal * effMult;
                        }
                        if (projected < baseVal - 1e-6 && hasMult && effMult < 1.0 - 1e-6)
                        {
                            RecordClean(tplId, ak, "negative_output_mult");
                            hasMult = false;
                        }
                    }
                }

                if (hasAdd)
                    targetCard.PermanentAdd[ak] = addVal;
                if (hasMult)
                    targetCard.PermanentMult[ak] = multVal;
            }
        }

        void ApplySanitizedFlatAttributes(
            CardInstance targetCard,
            CardTemplate tpl,
            Dictionary<string, int> permAttrs)
        {
            foreach (var (ak, av) in permAttrs)
            {
                if (IsDerivedGhostAttributeKey(ak))
                    continue;

                if (!IsValidCardAttributeKey(ak))
                {
                    RecordClean(tpl.Id, ak, "invalid_attribute_key");
                    continue;
                }

                if (string.Equals(ak, "Lifesteal", StringComparison.OrdinalIgnoreCase) && av <= 0)
                {
                    RecordClean(tpl.Id, ak, "non_positive_lifesteal");
                    continue;
                }

                if (string.Equals(ak, "Multicast", StringComparison.OrdinalIgnoreCase))
                {
                    double baseMc = targetCard.BaseAttributes.TryGetValue("Multicast", out double mcVal)
                        ? mcVal
                        : (string.Equals(tpl.Type, "Item", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);
                    if (av <= baseMc + 1e-6)
                    {
                        RecordClean(tpl.Id, ak, "redundant_multicast");
                        continue;
                    }
                    double mcDelta = av - baseMc;
                    if (Math.Abs(mcDelta) > 0.001)
                        targetCard.PermanentAdd[ak] = mcDelta;
                    continue;
                }

                if (IsGuardedOutputAttribute(ak))
                {
                    bool hasOutputAttr = targetCard.BaseAttributes.ContainsKey(ak)
                        || targetCard.EnchantmentBaseAttributes.ContainsKey(ak);
                    if (!hasOutputAttr)
                    {
                        RecordClean(tpl.Id, ak, "orphan_output_attribute");
                        continue;
                    }

                    double baseVal = targetCard.BaseAttributes.TryGetValue(ak, out double bv)
                        ? bv
                        : targetCard.EnchantmentBaseAttributes.GetValueOrDefault(ak, 0);
                    double delta = av - baseVal;
                    if (delta < -1e-6)
                    {
                        RecordClean(tpl.Id, ak, "negative_output_delta");
                        continue;
                    }
                    if (Math.Abs(delta) > 0.001)
                        targetCard.PermanentAdd[ak] = delta;
                    continue;
                }

                // CooldownMax 等非输出属性明确豁免，允许正向减 CD 与常规属性覆盖
                double nonGuardedBase = targetCard.BaseAttributes.GetValueOrDefault(ak, 0);
                double nonGuardedDelta = av - nonGuardedBase;
                if (Math.Abs(nonGuardedDelta) > 0.001)
                    targetCard.PermanentAdd[ak] = nonGuardedDelta;
            }
        }

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

            // 1. 构造 CardInstance 前先做附魔合法性清洗（仅跨版本/旧锚点触发）
            string? effectiveEnchantment = it.Enchantment;
            if (shouldSanitize && !string.IsNullOrWhiteSpace(effectiveEnchantment))
            {
                if (TryResolveValidEnchantment(tpl, effectiveEnchantment, out string canonicalEnch))
                {
                    effectiveEnchantment = canonicalEnch;
                }
                else
                {
                    RecordClean(tpl.Id, $"Enchantment:{effectiveEnchantment}", "invalid_enchantment");
                    effectiveEnchantment = null;
                }
            }

            var card = new CardInstance(tpl, TierHelper.ParseTier(it.Tier), effectiveEnchantment, $"opp_{itemIdx++}_{it.Slot}_{prefix}");

            // 2. 统一先挂载 QuestProgress 与 CompletedQuests（不可重复任务补满 Target），再重建品阶/附魔/任务奖励，
            //    确保后续平铺差值反解与分层跨版本检查基于完整的“品阶 + 附魔 + 已完成任务”基础值，彻底消除双倍叠算。
            if (it.QuestProgress is { Count: > 0 })
            {
                foreach (var (qk, qv) in it.QuestProgress)
                {
                    if (string.IsNullOrWhiteSpace(qk) || !double.IsFinite(qv))
                        continue;
                    if (shouldSanitize)
                    {
                        if (TryResolveValidQuestKey(tpl, qk, out string canonicalQ))
                            card.QuestProgress[canonicalQ] = qv;
                        else
                            RecordClean(tpl.Id, qk, "invalid_quest_progress");
                    }
                    else
                    {
                        card.QuestProgress[qk] = qv;
                    }
                }
            }
            if (it.CompletedQuests is { Count: > 0 })
            {
                bool anyCompletedQuest = false;
                foreach (string qid in it.CompletedQuests)
                {
                    if (string.IsNullOrWhiteSpace(qid))
                        continue;
                    string effectiveQid = qid;
                    if (shouldSanitize)
                    {
                        if (!TryResolveValidQuestKey(tpl, qid, out string canonicalQ))
                        {
                            RecordClean(tpl.Id, qid, "invalid_completed_quest");
                            continue;
                        }
                        effectiveQid = canonicalQ;
                    }
                    card.CompletedQuests.Add(effectiveQid);
                    anyCompletedQuest = true;
                    if (TooltipFormatter.TryFindNonRepeatableQuestTarget(tpl, effectiveQid, out double questTarget)
                        && card.QuestProgress.GetValueOrDefault(effectiveQid, 0) < questTarget)
                    {
                        card.QuestProgress[effectiveQid] = questTarget;
                    }
                }
                if (anyCompletedQuest)
                {
                    // 激活已完成任务对应的奖励能力与属性，使 BaseAttributes 包含任务奖励
                    card.RebuildTierAndEnchantmentData();
                }
            }

            // 3. 再挂载永久属性：优先无损还原 PermanentAdd / PermanentMult 双层；历史 JSONL 缺省时回退平铺 PermanentAttributes
            bool hasLayeredPerm = (it.PermanentAdd is { Count: > 0 }) || (it.PermanentMult is { Count: > 0 });
            if (hasLayeredPerm)
            {
                if (shouldSanitize)
                {
                    ApplySanitizedLayeredAttributes(card, tpl.Id, it.PermanentAdd, it.PermanentMult);
                }
                else
                {
                    if (it.PermanentAdd != null)
                    {
                        foreach (var (ak, av) in it.PermanentAdd)
                        {
                            if (!IsDerivedGhostAttributeKey(ak) && double.IsFinite(av) && Math.Abs(av) > 1e-6)
                                card.PermanentAdd[ak] = av;
                        }
                    }
                    if (it.PermanentMult != null)
                    {
                        foreach (var (ak, mv) in it.PermanentMult)
                        {
                            if (!IsDerivedGhostAttributeKey(ak) && double.IsFinite(mv) && Math.Abs(mv - 1.0) > 1e-6)
                                card.PermanentMult[ak] = mv;
                        }
                    }
                }
            }
            else if (it.PermanentAttributes != null)
            {
                if (shouldSanitize)
                {
                    ApplySanitizedFlatAttributes(card, tpl, it.PermanentAttributes);
                }
                else
                {
                    foreach (var (ak, av) in it.PermanentAttributes)
                    {
                        if (IsDerivedGhostAttributeKey(ak))
                            continue;
                        double baseVal = card.BaseAttributes.GetValueOrDefault(ak, 0);
                        double delta = av - baseVal;
                        if (Math.Abs(delta) > 0.001)
                            card.PermanentAdd[ak] = delta;
                    }
                }
            }

            if (it.ExtraTags is { Count: > 0 })
            {
                bool hasTinyExtraTag = false;
                foreach (string tag in it.ExtraTags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        string trimmedTag = tag.Trim();
                        card.ExtraTags.Add(trimmedTag);
                        if (string.Equals(trimmedTag, "Tiny", StringComparison.OrdinalIgnoreCase))
                            hasTinyExtraTag = true;
                    }
                }
                // D1：历史镜像仅在 ExtraTags 写 "Tiny" 未写属性时，补 PermanentAdd["Tiny"] = 1 保证客户端 IsTiny() 正常识别
                if (hasTinyExtraTag && (!card.PermanentAdd.TryGetValue("Tiny", out double curTiny) || curTiny <= 0))
                    card.PermanentAdd["Tiny"] = 1;
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
            var skillCard = new CardInstance(tpl, TierHelper.ParseTier(sk.Tier), null, $"oppskl_{skillIdx++}_{prefix}");
            bool hasSkLayered = (sk.PermanentAdd is { Count: > 0 }) || (sk.PermanentMult is { Count: > 0 });
            if (hasSkLayered)
            {
                if (shouldSanitize)
                {
                    ApplySanitizedLayeredAttributes(skillCard, tpl.Id, sk.PermanentAdd, sk.PermanentMult);
                }
                else
                {
                    if (sk.PermanentAdd != null)
                    {
                        foreach (var (ak, av) in sk.PermanentAdd)
                        {
                            if (!IsDerivedGhostAttributeKey(ak) && double.IsFinite(av) && Math.Abs(av) > 1e-6)
                                skillCard.PermanentAdd[ak] = av;
                        }
                    }
                    if (sk.PermanentMult != null)
                    {
                        foreach (var (ak, mv) in sk.PermanentMult)
                        {
                            if (!IsDerivedGhostAttributeKey(ak) && double.IsFinite(mv) && Math.Abs(mv - 1.0) > 1e-6)
                                skillCard.PermanentMult[ak] = mv;
                        }
                    }
                }
            }
            else if (sk.PermanentAttributes is { Count: > 0 })
            {
                if (shouldSanitize)
                {
                    ApplySanitizedFlatAttributes(skillCard, tpl, sk.PermanentAttributes);
                }
                else
                {
                    foreach (var (ak, av) in sk.PermanentAttributes)
                    {
                        if (IsDerivedGhostAttributeKey(ak)) continue;
                        double baseVal = skillCard.BaseAttributes.GetValueOrDefault(ak, 0);
                        double delta = av - baseVal;
                        if (Math.Abs(delta) > 0.001)
                            skillCard.PermanentAdd[ak] = delta;
                    }
                }
            }
            if (sk.ExtraTags is { Count: > 0 })
            {
                foreach (string tag in sk.ExtraTags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        skillCard.ExtraTags.Add(tag);
                }
            }
            ps.Skills.Add(skillCard);
        }

        // D2-P1-10：还原 Jules 炉灶/冷格等 SocketEffects
        if (ghost.SocketEffects is { Count: > 0 })
        {
            int seIdx = 0;
            foreach (var se in ghost.SocketEffects)
            {
                var tpl = repo.GetCard(se.TemplateId);
                if (tpl == null) continue;
                string prefix = tpl.Id.Length >= 6 ? tpl.Id[..6] : tpl.Id;
                string? seEnchantment = se.Enchantment;
                if (shouldSanitize && !string.IsNullOrWhiteSpace(seEnchantment))
                {
                    if (TryResolveValidEnchantment(tpl, seEnchantment, out string canonSeEnch))
                        seEnchantment = canonSeEnch;
                    else
                    {
                        RecordClean(tpl.Id, $"Enchantment:{seEnchantment}", "invalid_enchantment");
                        seEnchantment = null;
                    }
                }
                var seCard = new CardInstance(tpl, TierHelper.ParseTier(se.Tier), seEnchantment, $"oppse_{seIdx++}_{se.Slot}_{prefix}")
                {
                    StartSlot = Math.Clamp(se.Slot, 0, 9),
                };
                if (se.PermanentAttributes is { Count: > 0 })
                {
                    if (shouldSanitize)
                    {
                        ApplySanitizedFlatAttributes(seCard, tpl, se.PermanentAttributes);
                    }
                    else
                    {
                        foreach (var (ak, av) in se.PermanentAttributes)
                        {
                            if (IsDerivedGhostAttributeKey(ak)) continue;
                            double baseVal = seCard.BaseAttributes.GetValueOrDefault(ak, 0);
                            double delta = av - baseVal;
                            if (Math.Abs(delta) > 0.001)
                                seCard.PermanentAdd[ak] = delta;
                        }
                    }
                }
                ps.SocketEffects.Add(seCard);
            }
        }

        if (activeCollector != null)
        {
            activeCollector.RecordBoardScanned(boardAffected);
            if (auditCollector == null && boardAffected && Interlocked.CompareExchange(ref _runtimeFirstAuditLogged, 1, 0) == 0)
            {
                Console.WriteLine(FormatSanitizationAuditSummary("first_runtime_match", RuntimeSanitizationAudit.BuildSummary()));
            }
        }

        ps.PrepareForCombat();
        return ps;
    }

    private static GhostBoard BuildFallbackGhost(int day, Random rng, GameDataRepository? repo)
    {
        var heroes = HeroNames.PlayableHeroes;
        string hero = heroes[rng.Next(heroes.Length)];
        int clampedDay = Math.Max(1, day);
        int unlockedMask = DefaultUnlockedMaskForDay(clampedDay);
        var (minHp, maxHp, _) = GetSaneBoundsForDay(clampedDay);
        int hp = clampedDay == 1
            ? GameDefaults.StartingHealth
            : Math.Clamp(GameDefaults.StartingHealth + (clampedDay - 1) * 350, minHp, maxHp);

        // D2-P2-06：严格在 DefaultUnlockedMaskForDay(clampedDay) 已解锁连续槽位内落子（如 Day 1 仅限槽位 3..6），零越界
        int minSlot = 0;
        while (minSlot < 10 && (unlockedMask & (1 << minSlot)) == 0) minSlot++;
        int maxExclusiveSlot = minSlot;
        while (maxExclusiveSlot < 10 && (unlockedMask & (1 << maxExclusiveSlot)) != 0) maxExclusiveSlot++;

        var items = new List<GhostItem>();
        if (repo != null && maxExclusiveSlot > minSlot)
        {
            var pool = repo.GetSpawnableItemsForHero(hero).Where(c => c.CardSize != CardSize.Large).ToList();
            int slot = minSlot;
            for (int i = 0; i < 3 && pool.Count > 0 && slot < maxExclusiveSlot; i++)
            {
                var tpl = pool[rng.Next(pool.Count)];
                int w = tpl.CardSize == CardSize.Medium ? 2 : 1;
                if (slot + w > maxExclusiveSlot)
                {
                    var smallOnly = pool.Where(c => c.CardSize == CardSize.Small).ToList();
                    if (smallOnly.Count == 0 || slot + 1 > maxExclusiveSlot) break;
                    tpl = smallOnly[rng.Next(smallOnly.Count)];
                    w = 1;
                }
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
            UnlockedSlotsMask = unlockedMask,
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

/// <summary>镜像池多维内存搜索请求 DTO。</summary>
public sealed class GhostPoolSearchRequest
{
    public int? Day { get; set; }
    public string? Hero { get; set; }
    public string? PowerBucket { get; set; }     // "" | Weak | Normal | Strong | MetaBoss
    public string? Source { get; set; }          // "" | community | player | ai
    public string? Partition { get; set; }       // 可选分区过滤（为空时不限或按需）
    public int? HpMin { get; set; }
    public int? HpMax { get; set; }
    public int? LevelMin { get; set; }
    public int? LevelMax { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>镜像池搜索分页响应 DTO。</summary>
public sealed class GhostPoolSearchResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool Truncated { get; set; }
    public bool IndexBuilding { get; set; }
    public List<PvpGhostPool.GhostBoard> Items { get; set; } = new();
}
