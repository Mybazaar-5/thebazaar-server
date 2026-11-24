using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarServer.Data;

namespace BazaarCore.Tests.TestInfrastructure;

/// <summary>
/// 进程级只读共享游戏资产底盘（同时支持 xUnit IClassFixture / ICollectionFixture 与静态单点复用）。
/// 整个 dotnet test 进程仅解析 1 次 GameData.db、1 次 translations/zh-CN.bytes、1 次 valid_card_ids.json 与 1 次 53.7MB ghost_pool_balanced.jsonl。
/// 隔离铁律：
/// - 凡需调用 CardPool.ApplyRepositoryOverlay 或 ServerModHost.BindPools 的测试，必须使用 CreateIsolatedCardPool() 与 CreateMonstersCopy()；
/// - 凡需调用 PvpGhostPool.RecordPlayerGhost / LoadFromDb 的测试，严禁使用共享 Ghosts，必须创建独立临时 PvpGhostPool；
/// - 凡需叠加自定义合成卡/野怪/升级表的测试，统一使用 SharedTestOverlay.Create(...) 零拷贝内存叠加。
/// </summary>
public sealed class SharedGameDataFixture
{
    public sealed class SharedGameDataSnapshot
    {
        public required string RepoRoot { get; init; }
        public required string MainRepoRoot { get; init; }
        public required string DataDir { get; init; }
        public required string DbPath { get; init; }
        public required string ZhCnPath { get; init; }
        public required string GhostPoolDir { get; init; }
        public required string ValidCardIdsPath { get; init; }
        public required string ModsRoot { get; init; }
        public required IReadOnlySet<string> ValidCardIds { get; init; }
        public required GameDataRepository Repo { get; init; }
        public required CardPool Pool { get; init; }
        public required List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; }
        public required LevelUpTable LevelUps { get; init; }
        public required GameModeConfig Mode { get; init; }
        public required CardArchetypeIndex ArchetypeIndex { get; init; }

        private readonly Lazy<PvpGhostPool> _ghostsLazy;
        private readonly Lazy<string> _compassPathLazy;

        public SharedGameDataSnapshot(
            Func<PvpGhostPool> ghostsFactory,
            Func<string> compassFactory)
        {
            _ghostsLazy = new Lazy<PvpGhostPool>(ghostsFactory, LazyThreadSafetyMode.ExecutionAndPublication);
            _compassPathLazy = new Lazy<string>(compassFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>只读共享幽灵池（按需加载且全进程仅加载 1 次；写入型测试严禁共享）。</summary>
        public PvpGhostPool Ghosts => _ghostsLazy.Value;

        /// <summary>按需解析的天梯校准数据 compass_distilled.json 路径。</summary>
        public string CompassPath => _compassPathLazy.Value;

        /// <summary>创建与共享底盘彻底隔离的 CardPool 副本（零 SQLite I/O，专供 ApplyRepositoryOverlay / ServerModHost.BindPools 使用）。</summary>
        public CardPool CreateIsolatedCardPool() => Pool.CreateIsolatedOverlay(Repo);

        /// <summary>创建独立的野怪模板列表浅拷贝（< 0.002ms，避免 ServerModHost.BindPools 就地 Clear/AddRange 污染共享实例）。</summary>
        public List<BazaarServer.Data.MonsterTemplate> CreateMonstersCopy() => new(Monsters);

        /// <summary>创建独立白名单集合副本。</summary>
        public HashSet<string> CreateValidCardIdsCopy() => new(ValidCardIds, StringComparer.OrdinalIgnoreCase);

        /// <summary>基于当前权威只读底盘构建零磁盘拷贝、零 Steam 泄漏的隔离内存叠加环境。</summary>
        public SharedTestOverlay CreateMemoryOverlay(
            IEnumerable<(string Id, string Json)>? customCards = null,
            IEnumerable<(string Id, string Json)>? customMonsters = null,
            IEnumerable<(int Level, string Json)>? customLevelUps = null,
            bool replaceLevelUps = false)
            => SharedTestOverlay.Create(this, customCards, customMonsters, customLevelUps, replaceLevelUps);
    }

    private static readonly Lazy<SharedGameDataSnapshot> SnapshotLazy =
        new(LoadSnapshotOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>全测试进程唯一只读共享快照。</summary>
    public static SharedGameDataSnapshot Shared => SnapshotLazy.Value;

    public string RepoRoot => Shared.RepoRoot;
    public string MainRepoRoot => Shared.MainRepoRoot;
    public string DataDir => Shared.DataDir;
    public string DbPath => Shared.DbPath;
    public string ZhCnPath => Shared.ZhCnPath;
    public string GhostPoolDir => Shared.GhostPoolDir;
    public string ValidCardIdsPath => Shared.ValidCardIdsPath;
    public string ModsRoot => Shared.ModsRoot;
    public IReadOnlySet<string> ValidCardIds => Shared.ValidCardIds;
    public GameDataRepository Repo => Shared.Repo;
    public CardPool Pool => Shared.Pool;
    public List<BazaarServer.Data.MonsterTemplate> Monsters => Shared.Monsters;
    public LevelUpTable LevelUps => Shared.LevelUps;
    public GameModeConfig Mode => Shared.Mode;
    public CardArchetypeIndex ArchetypeIndex => Shared.ArchetypeIndex;
    public PvpGhostPool Ghosts => Shared.Ghosts;
    public string CompassPath => Shared.CompassPath;

    public CardPool CreateIsolatedCardPool() => Shared.CreateIsolatedCardPool();
    public List<BazaarServer.Data.MonsterTemplate> CreateMonstersCopy() => Shared.CreateMonstersCopy();
    public HashSet<string> CreateValidCardIdsCopy() => Shared.CreateValidCardIdsCopy();

    public SharedTestOverlay CreateMemoryOverlay(
        IEnumerable<(string Id, string Json)>? customCards = null,
        IEnumerable<(string Id, string Json)>? customMonsters = null,
        IEnumerable<(int Level, string Json)>? customLevelUps = null,
        bool replaceLevelUps = false)
        => Shared.CreateMemoryOverlay(customCards, customMonsters, customLevelUps, replaceLevelUps);

    private static SharedGameDataSnapshot LoadSnapshotOnce()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string mainRepoRoot = TestDataLocator.ResolveMainRepoRoot();
        string dataDir = TestDataLocator.ResolveDataDir();
        string dbPath = TestDataLocator.ResolveGameDataDbPath();
        string zhPath = TestDataLocator.ResolveZhCnPath();
        string ghostDir = TestDataLocator.ResolveGhostPoolDir();
        string validIdsPath = TestDataLocator.ResolveValidCardIdsPath();
        string modsRoot = TestDataLocator.ResolveModsRoot();

        // 显式同时传入仓库 dbPath 与非空仓库 zhPath，彻底阻断 Steam 缓存回退
        var repo = GameDatabaseLoader.Load(dbPath, zhPath);
        var validIds = LoadWhitelistIds(validIdsPath);

        // 进程级单次构建权威只读 CardPool：
        // 优先经由 CardPool.Load(dbPath) 完整扫描 cards + level_ups 的全部 ReferencedCardIds（避免 D2-P2-01 漏扫 level_ups 导致升级池卡混入白昼 GuidOnly 目录）；
        // 仅当 dbPath 同目录存在仓库翻译文件（确保绝不触发 Steam 回退）时采用该路径，并二次校验禁止命中 Steam 缓存。
        string? dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        string siblingZhPath = dbDir != null ? Path.Combine(dbDir, "translations", "zh-CN.bytes") : string.Empty;
        CardPool pool;
        if (!string.IsNullOrWhiteSpace(siblingZhPath) && File.Exists(siblingZhPath) && !TestDataLocator.IsUnderSteamProdCache(siblingZhPath))
        {
            pool = CardPool.Load(dbPath);
            if (TestDataLocator.IsUnderSteamProdCache(pool.ZhTranslationLoadedFrom))
            {
                throw new InvalidOperationException(
                    $"SharedGameDataFixture 检测到 CardPool 翻译文件回退至 Steam 缓存，已阻断：{pool.ZhTranslationLoadedFrom}");
            }
        }
        else
        {
            pool = CardPool.FromRepository(repo, dataDir, validWhitelistOverride: validIds);
        }

        var monsters = MonsterPool.FromRepository(repo);
        var levelUps = LevelUpTable.FromRepository(repo);
        var mode = GameModeConfig.FromRepository(repo);
        var archetypeIndex = CardArchetypeIndex.Build(pool, repo);

        return new SharedGameDataSnapshot(
            ghostsFactory: () => PvpGhostPool.Load(ghostDir),
            compassFactory: TestDataLocator.ResolveCompassDistilledPath)
        {
            RepoRoot = repoRoot,
            MainRepoRoot = mainRepoRoot,
            DataDir = dataDir,
            DbPath = dbPath,
            ZhCnPath = zhPath,
            GhostPoolDir = ghostDir,
            ValidCardIdsPath = validIdsPath,
            ModsRoot = modsRoot,
            ValidCardIds = validIds,
            Repo = repo,
            Pool = pool,
            Monsters = monsters,
            LevelUps = levelUps,
            Mode = mode,
            ArchetypeIndex = archetypeIndex,
        };
    }

    internal static HashSet<string> LoadWhitelistIds(string whitelistPath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(whitelistPath)) return ids;

        using var doc = JsonDocument.Parse(File.ReadAllText(whitelistPath, Encoding.UTF8));
        JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : (doc.RootElement.TryGetProperty("valid_ids", out var prop) && prop.ValueKind == JsonValueKind.Array
                ? prop
                : default);
        if (arr.ValueKind != JsonValueKind.Array) return ids;

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                string? id = el.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }
        return ids;
    }
}

/// <summary>
/// 零拷贝内存测试 Overlay：替代传统测试中复制 45MB GameData.db 到临时目录再重复 5 次 SQLite Load 的低效做法。
/// 全程不碰磁盘、不调 SqliteConnection.ClearAllPools()、不丢失 Localization（绝不触发 Steam 回退）。
/// </summary>
public sealed class SharedTestOverlay : IDisposable
{
    private string? _tempDir;

    /// <summary>按需懒创建的隔离临时目录（不访问时零磁盘 I/O，访问后于 Dispose 自动清理，绝不写入仓库）。</summary>
    public string TempDir
    {
        get
        {
            if (_tempDir == null)
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "bazaar_test_ov_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }
            return _tempDir;
        }
    }

    public GameDataRepository Repo { get; }
    public CardPool Pool { get; }
    public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
    public LevelUpTable LevelUps { get; }
    public GameModeConfig Mode { get; }
    public PvpGhostPool Ghosts { get; }

    private SharedTestOverlay(
        GameDataRepository repo,
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        PvpGhostPool ghosts)
    {
        Repo = repo;
        Pool = pool;
        Monsters = monsters;
        LevelUps = levelUps;
        Mode = mode;
        Ghosts = ghosts;
    }

    public static SharedTestOverlay Create(
        SharedGameDataFixture.SharedGameDataSnapshot shared,
        IEnumerable<(string Id, string Json)>? customCards = null,
        IEnumerable<(string Id, string Json)>? customMonsters = null,
        IEnumerable<(int Level, string Json)>? customLevelUps = null,
        bool replaceLevelUps = false)
    {
        ArgumentNullException.ThrowIfNull(shared);

        var mergedWhitelist = shared.CreateValidCardIdsCopy();
        List<CardTemplate>? parsedCards = null;
        if (customCards != null)
        {
            parsedCards = new List<CardTemplate>();
            foreach (var (id, json) in customCards)
            {
                var card = JsonSerializer.Deserialize<CardTemplate>(json, GameDatabaseLoader.JsonOptions)
                    ?? throw new InvalidDataException($"反序列化测试合成卡牌失败: {id}");
                if (string.IsNullOrWhiteSpace(card.Id))
                    card.Id = id;
                parsedCards.Add(card);
                mergedWhitelist.Add(card.Id);
                if (!string.IsNullOrWhiteSpace(id))
                    mergedWhitelist.Add(id);
            }
        }

        List<BazaarCore.Models.MonsterTemplate>? parsedMonsters = null;
        if (customMonsters != null)
        {
            parsedMonsters = new List<BazaarCore.Models.MonsterTemplate>();
            foreach (var (id, json) in customMonsters)
            {
                var m = JsonSerializer.Deserialize<BazaarCore.Models.MonsterTemplate>(json, GameDatabaseLoader.JsonOptions)
                    ?? throw new InvalidDataException($"反序列化测试合成野怪失败: {id}");
                if (string.IsNullOrWhiteSpace(m.Id))
                    m.Id = id;
                parsedMonsters.Add(m);
            }
        }

        // CreateWithOverlay 在 BazaarCore 内部自动对新增卡执行 PrecompileCardTemplateRules，并保留 Localization
        GameDataRepository overlayRepo = shared.Repo.CreateWithOverlay(
            additionalCards: parsedCards,
            additionalMonsters: parsedMonsters);

        LevelUpTable levelUps = shared.LevelUps;
        if (customLevelUps != null)
        {
            var levelList = customLevelUps.ToList();
            if (levelList.Count > 0 || replaceLevelUps)
            {
                var mergedLevelUps = replaceLevelUps
                    ? new Dictionary<int, LevelUpTemplate>()
                    : new Dictionary<int, LevelUpTemplate>(overlayRepo.LevelUps);
                foreach (var (lvl, json) in levelList)
                {
                    var lu = JsonSerializer.Deserialize<LevelUpTemplate>(json, GameDatabaseLoader.JsonOptions)
                        ?? throw new InvalidDataException($"反序列化测试合成升级表失败: level={lvl}");
                    if (lu.Level == 0)
                        lu.Level = lvl;
                    mergedLevelUps[lvl] = lu;
                }

                // 完整保留 GameDataRepository 全 14 个构造参数（严防丢 Localization / SourceDataPath 导致回退 Steam）
                overlayRepo = new GameDataRepository(
                    sourceZipPath: overlayRepo.SourceZipPath,
                    gameMode: overlayRepo.GameMode,
                    cards: new Dictionary<string, CardTemplate>(overlayRepo.CardsById, StringComparer.OrdinalIgnoreCase),
                    monsters: new Dictionary<string, BazaarCore.Models.MonsterTemplate>(overlayRepo.MonstersById, StringComparer.OrdinalIgnoreCase),
                    levelUps: mergedLevelUps,
                    tooltips: new Dictionary<string, string>(overlayRepo.TooltipGlossary, StringComparer.OrdinalIgnoreCase),
                    observedRuleTypes: new HashSet<string>(overlayRepo.ObservedRuleTypes, StringComparer.Ordinal),
                    unknownRuleTypes: new HashSet<string>(overlayRepo.UnknownRuleTypes, StringComparer.Ordinal),
                    localization: overlayRepo.Localization,
                    sourceDataPath: overlayRepo.SourceDataPath,
                    challenges: new Dictionary<string, ChallengeTemplate>(overlayRepo.ChallengesById, StringComparer.OrdinalIgnoreCase),
                    collectibles: new Dictionary<string, CollectibleTemplate>(overlayRepo.CollectiblesById, StringComparer.OrdinalIgnoreCase),
                    seasons: new Dictionary<int, SeasonTemplate>(overlayRepo.SeasonsById),
                    tooltipSymbols: new Dictionary<string, TooltipSymbolTemplate>(overlayRepo.TooltipSymbolsById, StringComparer.OrdinalIgnoreCase));

                levelUps = LevelUpTable.FromRepository(overlayRepo);
            }
        }

        // 因 overlayRepo.HasLocalizationLoaded == true，CardPool.FromRepository 零 SQLite 且绝不触发 Steam 回退
        var pool = CardPool.FromRepository(
            overlayRepo,
            dataRoot: null,
            validWhitelistOverride: mergedWhitelist);

        var monsters = parsedMonsters is { Count: > 0 }
            ? MonsterPool.FromRepository(overlayRepo)
            : shared.CreateMonstersCopy();

        return new SharedTestOverlay(overlayRepo, pool, monsters, levelUps, shared.Mode, shared.Ghosts);
    }

    public void Dispose()
    {
        if (_tempDir != null)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }
    }
}
