using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarServer.Planner;

/// <summary>分享码与蓝图操作统一返回封装（供端点无损映射 HTTP 状态码与契约错误码）。</summary>
public sealed class BlueprintOpResult<T>
{
    public bool Ok { get; init; }
    public int HttpStatus { get; init; } = 200;
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Data { get; init; }

    public static BlueprintOpResult<T> Success(T data)
        => new() { Ok = true, HttpStatus = 200, Data = data };

    public static BlueprintOpResult<T> Fail(int status, string code, string error)
        => new() { Ok = false, HttpStatus = status, ErrorCode = code, ErrorMessage = error };
}

/// <summary>POST /api/planner/blueprints/export 请求体。</summary>
public sealed class BlueprintExportRequest
{
    public string Source { get; set; } = "active";         // active | recent | rules | lineup
    public string Scope { get; set; } = "events_only";     // events_only | events_and_ghosts（lineup 源总是带对手）
    public bool IncludeRules { get; set; }
    public string? Title { get; set; }
    public string? Hero { get; set; }
    /// <summary>source=lineup 时要分享的方案 ID。</summary>
    public string? LineupId { get; set; }
}

/// <summary>「把一局存成方案」的结果：哪些天是实际遇到的、哪些来自原方案、哪些是自动补的。</summary>
public sealed class LineupFromRunResult
{
    public CustomGhostLineup Lineup { get; set; } = new();
    public List<int> FacedDays { get; set; } = new();
    public List<int> PinnedDays { get; set; } = new();
    public List<int> AutoFilledDays { get; set; } = new();
    public List<int> SkippedDays { get; set; } = new();
    public string MessageZh { get; set; } = "";
}

/// <summary>一局（进行中 / 最近结束）的最小快照：种子、英雄、当前进度与规则。</summary>
internal sealed class RunSourceSnapshot
{
    public string RunId { get; init; } = "";
    public string Hero { get; init; } = "";
    public int MasterSeed { get; init; }
    public string SeedText { get; init; } = "";
    public int CurrentDay { get; init; }
    public int CurrentHour { get; init; }
    public bool IsActive { get; init; }
    public PvpGhostPool.GhostBoard? PendingGhost { get; init; }
    public CustomRunRuleSet Rules { get; init; } = new();
}

/// <summary>导出响应摘要。</summary>
public sealed class BlueprintExportSummary
{
    public string Hero { get; set; } = "";
    public string HeroZh { get; set; } = "";
    public string SeedText { get; set; } = "";
    public string Scope { get; set; } = "events_only";
    public List<int> GhostDays { get; set; } = new();
    public bool IncludeRules { get; set; }
}

/// <summary>POST /api/planner/blueprints/export 响应体。</summary>
public sealed class BlueprintExportResponse
{
    public string ShortCode { get; set; } = "";
    public string OfflineCode { get; set; } = "";
    public BlueprintExportSummary Summary { get; set; } = new();
}

/// <summary>POST /api/planner/blueprints/preview 请求体。</summary>
public sealed class BlueprintPreviewRequest
{
    public string? Code { get; set; }
}

/// <summary>POST /api/planner/blueprints/import 请求体。</summary>
public sealed class BlueprintImportRequest
{
    public string? Code { get; set; }
    public string Opponents { get; set; } = "lineup";      // lineup | random
    public bool ApplyRules { get; set; }
    public List<string>? EnableModIds { get; set; }
    public bool Apply { get; set; } = true;
}

/// <summary>导入响应中的规则补丁对象（供插件与网页消费）。</summary>
public sealed class BlueprintRulesPatch
{
    public string CustomSeedText { get; set; } = "";
    public string GhostOpponentMode { get; set; } = "Random"; // Random | FixedLineup
    public string? SelectedGhostLineupId { get; set; }
    public List<string> AddModIds { get; set; } = new();
    public CustomRunRuleSet? RulesSnapshot { get; set; }
}

/// <summary>导入新建剧本的缺失 Mod 条目（与契约 0.2 LineupSummary.missingMods 同构）。</summary>
public sealed class BlueprintLineupMissingMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>导入新建剧本摘要（与契约 0.2 LineupSummary 完全同构，避免耦合 GhostLineupStore）。</summary>
public sealed class BlueprintImportedLineupSummary
{
    public string LineupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<int> DayNumbers { get; set; } = new();
    public string SourceType { get; set; } = "imported";
    public string? SourceCode { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<BlueprintLineupMissingMod> MissingMods { get; set; } = new();
    public List<string> RequiredModIds { get; set; } = new();
    public bool IsSelected { get; set; }
}

/// <summary>POST /api/planner/blueprints/import 响应体。</summary>
public sealed class BlueprintImportResponse
{
    public BlueprintRulesPatch RulesPatch { get; set; } = new();
    public CustomRunRuleSet? Rules { get; set; }
    public BlueprintImportedLineupSummary? Lineup { get; set; }
    public string MessageZh { get; set; } = "";
}

/// <summary>GET /api/planner/blueprints/mine 单条记录。</summary>
public sealed class BlueprintMineItemDto
{
    public string ShortCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string Scope { get; set; } = "events_only";
    public string Hero { get; set; } = "";
    public string HeroZh { get; set; } = "";
    public string SeedText { get; set; } = "";
    public int ImportCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>GET /api/planner/blueprints/mine 响应体。</summary>
public sealed class BlueprintMineListResponse
{
    public List<BlueprintMineItemDto> Items { get; set; } = new();
}

/// <summary>
/// 对局种子与每日镜像蓝图的导出、防炸弹解析、Mod 差异预览、导入套用与个人分享码管理服务（计划第 5.1~5.2、8.2~8.4 节）。
/// </summary>
public sealed class RunBlueprintService
{
    public const int MaxOfflineCodeChars = 160 * 1024;       // 160 KB 离线码上限
    public const int MaxDecompressedBytes = 512 * 1024;      // 512 KB 解压/JSON 字节上限
    public const int StreamBufferBytes = 16 * 1024;          // 16 KB 流式解压缓冲

    private static readonly JsonSerializerOptions EncodeJsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DecodeJsonOpts = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Persistence _db;
    private readonly PlayerRuleStore _ruleStore;
    private readonly SessionStore _sessions;
    private readonly ServerModHost _modHost;
    private readonly CommunityModStore? _communityMods;
    private readonly CardPool _basePool;
    private readonly Func<List<BazaarServer.Data.MonsterTemplate>> _monstersProvider;
    private readonly Func<CardArchetypeIndex> _archetypesProvider;

    [ActivatorUtilitiesConstructor]
    public RunBlueprintService(
        Persistence db,
        PlayerRuleStore ruleStore,
        SessionStore sessions,
        ServerModHost modHost,
        CommunityModStore communityMods,
        CardPool basePool,
        MonsterPoolStore monsterPoolStore,
        ServerRuntimeSnapshotProvider runtimeSnapshots)
        : this(
            db,
            ruleStore,
            sessions,
            modHost,
            communityMods,
            basePool,
            () => monsterPoolStore.SnapshotList(),
            () => runtimeSnapshots.Current.ArchetypeIndex)
    {
    }

    public RunBlueprintService(
        Persistence db,
        PlayerRuleStore ruleStore,
        SessionStore sessions,
        ServerModHost modHost,
        CommunityModStore? communityMods,
        CardPool basePool,
        List<BazaarServer.Data.MonsterTemplate> baseMonsters,
        CardArchetypeIndex baseArchetypes)
        : this(
            db,
            ruleStore,
            sessions,
            modHost,
            communityMods,
            basePool,
            () => baseMonsters,
            () => baseArchetypes)
    {
    }

    public RunBlueprintService(
        Persistence db,
        PlayerRuleStore ruleStore,
        SessionStore sessions,
        ServerModHost modHost,
        CommunityModStore? communityMods,
        CardPool basePool,
        Func<List<BazaarServer.Data.MonsterTemplate>> monstersProvider,
        Func<CardArchetypeIndex> archetypesProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _modHost = modHost ?? throw new ArgumentNullException(nameof(modHost));
        _communityMods = communityMods;
        _basePool = basePool ?? throw new ArgumentNullException(nameof(basePool));
        _monstersProvider = monstersProvider ?? throw new ArgumentNullException(nameof(monstersProvider));
        _archetypesProvider = archetypesProvider ?? throw new ArgumentNullException(nameof(archetypesProvider));
    }

    // ── 1. 导出（计划 8.2）────────────────────────────────────────────────────

    public async Task<BlueprintOpResult<BlueprintExportResponse>> ExportAsync(
        string accountId,
        string username,
        BlueprintExportRequest? req,
        CancellationToken ct = default)
    {
        req ??= new BlueprintExportRequest();
        string source = (req.Source ?? "active").Trim().ToLowerInvariant();
        string requestedScope = string.Equals(req.Scope?.Trim(), "events_and_ghosts", StringComparison.OrdinalIgnoreCase)
            ? "events_and_ghosts"
            : "events_only";

        string cleanTitle = AccountStore.SanitizeDisplayText(req.Title, PlannerCatalogConstants.MaxLineupName);
        string cleanUsername = AccountStore.SanitizeDisplayText(username, 40);

        string runId = "";
        string hero = "";
        int masterSeed = 0;
        string seedText = "";
        int currentDay = 0;
        int currentHour = 0;
        PvpGhostPool.GhostBoard? activePendingGhost = null;
        CustomRunRuleSet sourceRules;

        Dictionary<int, PvpGhostPool.GhostBoard>? lineupDays = null;
        List<string>? lineupModIds = null;

        if (source == "active" || source == "recent")
        {
            var (lStatus, lCode, lErr, snap) = await LoadRunSourceAsync(accountId, source, ct);
            if (snap == null)
                return BlueprintOpResult<BlueprintExportResponse>.Fail(lStatus, lCode, lErr);
            runId = snap.RunId;
            hero = snap.Hero;
            masterSeed = snap.MasterSeed;
            seedText = snap.SeedText;
            currentDay = snap.CurrentDay;
            currentHour = snap.CurrentHour;
            activePendingGhost = snap.PendingGhost;
            sourceRules = snap.Rules;
        }
        else if (source == "lineup")
        {
            // 分享一个对手方案：种子用方案自己配的，没配就用「下一局的种子」；对手 = 方案里安排的所有天
            string lid = (req.LineupId ?? "").Trim();
            var lineup = lid.Length == 0 ? null : _db.LoadGhostLineup(accountId, lid);
            if (lineup == null)
                return BlueprintOpResult<BlueprintExportResponse>.Fail(404, "NOT_FOUND", "这个方案不存在（可能已经删掉了）");

            sourceRules = _ruleStore.Get(accountId);
            string? seedRaw = !string.IsNullOrWhiteSpace(lineup.SeedText) ? lineup.SeedText : sourceRules.CustomSeedText;
            if (!DailyChallengeSeedGenerator.TryParseSeedText(seedRaw, out int lineupMaster, out string lineupSeedDisplay))
                return BlueprintOpResult<BlueprintExportResponse>.Fail(409, "SEED_UNAVAILABLE", "这个方案还没配种子：先在方案上方的「配套种子」里填一个");

            hero = PlannerCatalogConstants.NormalizeHeroId(req.Hero)
                ?? PlannerCatalogConstants.NormalizeHeroId(lineup.SeedHero)
                ?? "Vanessa";
            masterSeed = lineupMaster;
            seedText = lineupSeedDisplay;
            if (string.IsNullOrWhiteSpace(cleanTitle))
                cleanTitle = AccountStore.SanitizeDisplayText(lineup.Name, PlannerCatalogConstants.MaxLineupName);

            lineupDays = new Dictionary<int, PvpGhostPool.GhostBoard>();
            if (lineup.Days != null)
            {
                foreach (var (d, board) in lineup.Days.OrderBy(kv => kv.Key))
                {
                    if (d >= 1 && d <= PlannerCatalogConstants.MaxLineupDays && board != null)
                        lineupDays[d] = board;
                }
            }
            lineupModIds = lineup.ModEnvironment?.Mods?
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ModId))
                .Select(m => m.ModId.Trim())
                .ToList();
        }
        else if (source == "rules")
        {
            sourceRules = _ruleStore.Get(accountId);
            if (!DailyChallengeSeedGenerator.TryParseSeedText(sourceRules.CustomSeedText, out int parsedMaster, out string parsedDisplay))
                return BlueprintOpResult<BlueprintExportResponse>.Fail(409, "SEED_UNAVAILABLE", "你还没有设置种子");

            string? normHero = PlannerCatalogConstants.NormalizeHeroId(req.Hero);
            if (normHero == null)
                return BlueprintOpResult<BlueprintExportResponse>.Fail(400, "BAD_REQUEST", "请选择受支持的英雄");

            hero = normHero;
            masterSeed = parsedMaster;
            seedText = parsedDisplay;
            requestedScope = "events_only";
        }
        else
        {
            return BlueprintOpResult<BlueprintExportResponse>.Fail(400, "BAD_REQUEST", $"不支持的导出源：{req.Source}");
        }

        string normalizedHero = PlannerCatalogConstants.NormalizeHeroId(hero) ?? "Vanessa";
        // 分享出去的种子码按主种子生成（规范写法）
        seedText = DailyChallengeSeedGenerator.FormatSeedCode(masterSeed);

        // 对手收集（绝不冻结、绝不预抽未来天数；仅导出已交手天数 + 进行中 Hour 6 预览当天）；方案源 = 方案里安排的天
        Dictionary<int, PvpGhostPool.GhostBoard>? dayGhosts = null;
        string effectiveScope = "events_only";
        if (source == "lineup")
        {
            if (lineupDays is { Count: > 0 })
            {
                dayGhosts = lineupDays;
                effectiveScope = "events_and_ghosts";
            }
        }
        else if (requestedScope == "events_and_ghosts" && source != "rules" && !string.IsNullOrWhiteSpace(runId))
        {
            var filtered = CollectFacedDays(runId, source == "active", activePendingGhost, currentDay, currentHour);
            if (filtered.Count > 0)
            {
                dayGhosts = filtered;
                effectiveScope = "events_and_ghosts";
            }
        }

        // 规则快照：清空三新字段、FixedRunSeed 与 SelectedModIds（Mod 环境由 package.ModEnvironment 权威声明；保留 SpecialModeId 便于旧客户端兼容）
        CustomRunRuleSet? ruleSnapshot = null;
        if (req.IncludeRules)
        {
            var extraMutators = _modHost.GetAllAvailableMutators(sourceRules.SelectedModIds)
                .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            ruleSnapshot = sourceRules.CloneNormalized(extraMutators, preserveUnregisteredMutators: false);
            ruleSnapshot.CustomSeedText = null;
            ruleSnapshot.GhostOpponentMode = EGhostOpponentMode.Random;
            ruleSnapshot.SelectedGhostLineupId = null;
            ruleSnapshot.FixedRunSeed = null;
            ruleSnapshot.DailyChallengeDate = null;
            ruleSnapshot.SelectedModIds = new List<string>();
        }

        // 方案源：方案里卡牌用到的 Mod 也要写进分享环境，朋友导入时才知道要开哪些
        var envModIds = source == "lineup"
            ? MergeModIds(sourceRules.SelectedModIds, lineupModIds)
            : (sourceRules.SelectedModIds ?? new List<string>());
        var envRepo = _modHost.ResolveEffectiveRegistryForMods(envModIds).ActiveRepository;
        var modEnv = PlannerCatalogConstants.BuildModEnvironmentSpec(
            envModIds,
            _modHost,
            envRepo,
            sourceRules.CustomModeId);

        DateTime nowUtc = DateTime.UtcNow;
        var package = new RunBlueprintPackage
        {
            SchemaVersion = 1,
            SeedAlgoVersion = SeedAlgo.Current,
            ExportScope = effectiveScope,
            Title = cleanTitle,
            CreatorUsername = cleanUsername,
            CreatedAtUtc = nowUtc,
            Hero = normalizedHero,
            MasterSeed = masterSeed,
            SeedText = seedText,
            IncludeRules = req.IncludeRules,
            SourceSpecialModeId = sourceRules.SpecialModeId.ToString(),
            RuleSetSnapshot = ruleSnapshot,
            ModEnvironment = modEnv,
            DayGhosts = dayGhosts,
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(package, EncodeJsonOpts);
        byte[] gzipBytes = CompressGzip(jsonBytes);

        var (shortCode, _) = _db.SaveSharedBlueprint(
            creatorAccountId: accountId,
            creatorUsername: cleanUsername,
            title: cleanTitle,
            exportScope: effectiveScope,
            hero: normalizedHero,
            seedText: seedText,
            masterSeed: masterSeed,
            blueprintGzip: gzipBytes,
            maxPerAccount: PlannerCatalogConstants.MaxSharedBlueprintsPerAccount);

        string offlineCode = "BZR1:" + Base64UrlEncode(gzipBytes);
        var ghostDays = dayGhosts != null
            ? dayGhosts.Keys.OrderBy(d => d).ToList()
            : new List<int>();

        return BlueprintOpResult<BlueprintExportResponse>.Success(new BlueprintExportResponse
        {
            ShortCode = shortCode,
            OfflineCode = offlineCode,
            Summary = new BlueprintExportSummary
            {
                Hero = normalizedHero,
                HeroZh = PlannerCatalogConstants.GetHeroNameZh(normalizedHero),
                SeedText = seedText,
                Scope = effectiveScope,
                GhostDays = ghostDays,
                IncludeRules = req.IncludeRules,
            },
        });
    }

    // ── 1b. 读取一局的最小快照 / 已交手对手（导出与「存成方案」共用）──────────────

    /// <summary>读取进行中（active）或最近结束（recent）那局的种子、英雄、进度与规则；失败时 snap 为 null。</summary>
    internal async Task<(int Status, string Code, string Error, RunSourceSnapshot? Snap)> LoadRunSourceAsync(
        string accountId,
        string source,
        CancellationToken ct)
    {
        if (source == "active")
        {
            var session = FindSessionForAccount(_sessions, accountId);
            if (session == null)
                return (404, "NO_RUN", "当前没有进行中的对局", null);

            try
            {
                await session.Gate.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return (404, "NO_RUN", "当前没有进行中的对局", null);
            }

            RunSourceSnapshot snap;
            try
            {
                var handle = SessionRegistry.Get(session.Sid);
                if (handle == null || Persistence.IsTerminalState(handle.Run.State))
                    return (404, "NO_RUN", "当前没有进行中的对局", null);

                // 锁内只深拷贝最小快照，绝不在 Session.Gate 内做 DB / 序列化 / GZip
                var run = handle.Run;
                snap = new RunSourceSnapshot
                {
                    RunId = run.RunId,
                    Hero = run.Hero.ToString(),
                    MasterSeed = run.MasterSeed,
                    SeedText = run.SeedText ?? "",
                    CurrentDay = (int)run.Day,
                    CurrentHour = (int)run.Hour,
                    IsActive = true,
                    PendingGhost = run.PendingPvpGhost != null ? DeepCloneGhostBoard(run.PendingPvpGhost) : null,
                    Rules = (run.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(
                        extraMutators: null,
                        preserveUnregisteredMutators: true),
                };
            }
            finally
            {
                try { session.Gate.Release(); } catch (ObjectDisposedException) { }
            }

            if (snap.MasterSeed == 0)
                return (409, "SEED_UNAVAILABLE", "这局没有种子，不能分享", null);
            return (200, "", "", snap);
        }

        if (source == "recent")
        {
            var latest = _db.LoadLatestFinishedRun(accountId);
            if (latest == null)
                return (404, "NO_RUN", "没有找到最近已结束的对局", null);

            RunSnapshotSerializer.TryReadHeader(latest.Value.SnapshotJson, null, out var header);
            var headerModIds = header?.CustomRules?.SelectedModIds;
            var effRegistry = _modHost.ResolveEffectiveRegistryForMods(headerModIds);
            if (!RunSnapshotSerializer.TryDeserialize(
                    latest.Value.SnapshotJson,
                    effRegistry.ActiveRepository,
                    out var recentRun,
                    out _,
                    extraMutators: effRegistry.CustomMutatorsById) || recentRun == null)
            {
                return (404, "NO_RUN", "最近一局快照无法读取", null);
            }

            if (recentRun.MasterSeed == 0)
                return (409, "SEED_UNAVAILABLE", "这局没有种子，不能分享", null);

            return (200, "", "", new RunSourceSnapshot
            {
                RunId = recentRun.RunId,
                Hero = recentRun.Hero.ToString(),
                MasterSeed = recentRun.MasterSeed,
                SeedText = recentRun.SeedText ?? "",
                CurrentDay = (int)recentRun.Day,
                CurrentHour = (int)recentRun.Hour,
                IsActive = false,
                PendingGhost = null,
                Rules = (recentRun.CustomRules ?? new CustomRunRuleSet()).CloneNormalized(
                    extraMutators: null,
                    preserveUnregisteredMutators: true),
            });
        }

        return (400, "BAD_REQUEST", $"不支持的对局来源：{source}", null);
    }

    /// <summary>这局实际交手过的对手（1..30 天）；进行中的局到了第 6 小时，当天已经揭晓的对手也算。</summary>
    private Dictionary<int, PvpGhostPool.GhostBoard> CollectFacedDays(
        string runId,
        bool isActive,
        PvpGhostPool.GhostBoard? pendingGhost,
        int currentDay,
        int currentHour)
    {
        var faced = _db.LoadRunGhosts(runId, "faced");
        if (isActive &&
            pendingGhost != null &&
            currentHour >= 6 &&
            currentDay >= 1 &&
            currentDay <= PlannerCatalogConstants.MaxLineupDays &&
            !faced.ContainsKey(currentDay))
        {
            faced[currentDay] = pendingGhost;
        }

        var filtered = new Dictionary<int, PvpGhostPool.GhostBoard>();
        foreach (var (d, board) in faced.OrderBy(kv => kv.Key))
        {
            if (d >= 1 && d <= PlannerCatalogConstants.MaxLineupDays && board != null)
                filtered[d] = board;
        }
        return filtered;
    }

    // ── 1c. 把一局存成对手方案（没打到的天自动从镜像池补）──────────────────────

    public const int DefaultFillToDay = 15;

    /// <summary>
    /// 把进行中 / 最近结束的一局存成一个新的对手方案，方便改完再分享：
    /// - 实际交手过的天 → 原样放进去；
    /// - 这局本来就用了方案、还没打到的天 → 沿用原方案那天的对手；
    /// - 剩下到 <paramref name="fillToDay"/> 为止的空天 → 按天数从镜像池挑一个普通强度的真实阵容（同一局每次补的结果一样）；
    /// - 种子记在方案上（配套种子），分享这个方案时一起带走。
    /// </summary>
    public async Task<BlueprintOpResult<LineupFromRunResult>> CreateLineupFromRunAsync(
        string accountId,
        string? source,
        int? fillToDay,
        PvpGhostPool? ghostPool,
        CancellationToken ct = default)
    {
        string src = string.Equals(source?.Trim(), "recent", StringComparison.OrdinalIgnoreCase) ? "recent" : "active";
        var (status, code, err, snap) = await LoadRunSourceAsync(accountId, src, ct);
        if (snap == null)
            return BlueprintOpResult<LineupFromRunResult>.Fail(status, code, err);

        var modCtx = _modHost.ResolveRunModContext(
            snap.Rules.SelectedModIds,
            _basePool,
            _monstersProvider(),
            _archetypesProvider());

        var faced = CollectFacedDays(snap.RunId, snap.IsActive, snap.PendingGhost, snap.CurrentDay, snap.CurrentHour);
        var pinned = new Dictionary<int, PvpGhostPool.GhostBoard>();
        foreach (var (d, b) in _db.LoadRunGhosts(snap.RunId, "pinned"))
        {
            if (d >= 1 && d <= PlannerCatalogConstants.MaxLineupDays && b != null && !faced.ContainsKey(d))
                pinned[d] = b;
        }

        int lastKnown = Math.Max(snap.CurrentDay, faced.Count > 0 ? faced.Keys.Max() : 0);
        int target = fillToDay ?? Math.Max(DefaultFillToDay, lastKnown);
        target = Math.Clamp(Math.Max(target, faced.Count > 0 ? faced.Keys.Max() : 1), 1, PlannerCatalogConstants.MaxLineupDays);

        string partition = PvpGhostPool.ResolveGhostPoolPartitionLocal(snap.Rules, false, _modHost);
        var result = new LineupFromRunResult();
        var days = new Dictionary<int, PvpGhostPool.GhostBoard>();

        bool TryAccept(int day, PvpGhostPool.GhostBoard? raw)
        {
            if (raw == null) return false;
            var filtered = StripUnknownCardsForRepository(raw, modCtx.Repository);
            var val = GhostBoardValidator.ValidateAndNormalize(day, filtered, modCtx.Repository, modCtx.Pool, _modHost);
            if (!val.Ok || !PvpGhostPool.HasAnyValidItem(val.Board, modCtx.Repository)) return false;
            days[day] = val.Board;
            return true;
        }

        for (int day = 1; day <= target; day++)
        {
            ct.ThrowIfCancellationRequested();
            if (faced.TryGetValue(day, out var fb) && TryAccept(day, fb)) { result.FacedDays.Add(day); continue; }
            if (pinned.TryGetValue(day, out var pb) && TryAccept(day, pb)) { result.PinnedDays.Add(day); continue; }
            if (ghostPool != null && TryAccept(day, PickPoolGhostForDay(ghostPool, modCtx.Repository, partition, snap.RunId, day)))
            {
                result.AutoFilledDays.Add(day);
                continue;
            }
            result.SkippedDays.Add(day);
        }

        if (days.Count == 0)
            return BlueprintOpResult<LineupFromRunResult>.Fail(409, "NOTHING_TO_SAVE", "这局还没有能存下来的对手（镜像池也是空的）");

        string seedText = DailyChallengeSeedGenerator.FormatSeedCode(snap.MasterSeed);
        string heroId = PlannerCatalogConstants.NormalizeHeroId(snap.Hero) ?? "Vanessa";
        string heroZh = PlannerCatalogConstants.GetHeroNameZh(heroId);

        var descParts = new List<string>();
        if (result.FacedDays.Count > 0) descParts.Add($"{FormatDayRanges(result.FacedDays)}：这局实际遇到的");
        if (result.PinnedDays.Count > 0) descParts.Add($"{FormatDayRanges(result.PinnedDays)}：原方案安排的");
        if (result.AutoFilledDays.Count > 0) descParts.Add($"{FormatDayRanges(result.AutoFilledDays)}：自动从镜像池补的，可以换");

        var entity = new CustomGhostLineup
        {
            AccountId = accountId,
            Name = AccountStore.SanitizeDisplayText($"{heroZh}这局的对手 · {seedText}", PlannerCatalogConstants.MaxLineupName),
            Description = AccountStore.SanitizeDisplayText(string.Join("；", descParts), PlannerCatalogConstants.MaxDescription),
            SourceType = "run",
            SourceCode = null,
            SeedText = seedText,
            SeedHero = heroId,
            ModEnvironment = PlannerCatalogConstants.BuildModEnvironmentSpec(
                CollectRetainedBoardModOrigins(days.Values, modCtx.Repository),
                _modHost,
                modCtx.Repository,
                customModeId: null),
            Days = days,
        };
        if (string.IsNullOrWhiteSpace(entity.Name)) entity.Name = "这局的对手";

        var (savedOk, saveErrCode, saved) = _db.SaveGhostLineup(
            entity,
            maxPerAccount: PlannerCatalogConstants.MaxLineups,
            maxJsonBytes: PlannerCatalogConstants.MaxLineupJsonBytes,
            requireExisting: false);
        if (!savedOk || saved == null)
            return MapSaveLineupError<LineupFromRunResult>(saveErrCode);

        result.Lineup = saved;
        var msg = new List<string> { $"已存成方案「{saved.Name}」，配套种子 {seedText}" };
        if (result.AutoFilledDays.Count > 0) msg.Add($"{FormatDayRanges(result.AutoFilledDays)}是自动补的，看看要不要换");
        if (result.SkippedDays.Count > 0) msg.Add($"{FormatDayRanges(result.SkippedDays)}没找到合适的阵容，那几天会随机");
        result.MessageZh = string.Join("；", msg);
        return BlueprintOpResult<LineupFromRunResult>.Success(result);
    }

    /// <summary>
    /// 按天数从镜像池挑一个阵容：优先「普通」强度、同分区；同一局同一天每次挑到的都一样。
    /// 镜像池里没有这么晚的天（比如第 20 天以后）时，往前找最近一天有阵容的。
    /// </summary>
    private static PvpGhostPool.GhostBoard? PickPoolGhostForDay(
        PvpGhostPool ghostPool,
        GameDataRepository repo,
        string partition,
        string runId,
        int day)
    {
        for (int d = day; d >= 1; d--)
        {
            var b = PickPoolGhostForExactDay(ghostPool, repo, partition, runId, day, d);
            if (b != null) return b;
        }
        return null;
    }

    private static PvpGhostPool.GhostBoard? PickPoolGhostForExactDay(
        PvpGhostPool ghostPool,
        GameDataRepository repo,
        string partition,
        string runId,
        int targetDay,
        int day)
    {
        var attempts = new (string? Power, string? Partition)[]
        {
            ("Normal", partition),
            (null, partition),
            ("Normal", null),
            (null, null),
        };
        foreach (var (power, part) in attempts)
        {
            var first = ghostPool.SearchGhosts(new GhostPoolSearchRequest { Day = day, PowerBucket = power, Partition = part, Page = 1, PageSize = 20 }, repo);
            int total = Math.Min(first.Total, 500);
            if (total <= 0) continue;

            int pick = (int)(StableHash($"{runId}|{targetDay}") % (uint)total);
            int pageSize = first.PageSize > 0 ? first.PageSize : 20;
            var page = pick / pageSize == 0
                ? first
                : ghostPool.SearchGhosts(new GhostPoolSearchRequest { Day = day, PowerBucket = power, Partition = part, Page = pick / pageSize + 1, PageSize = pageSize }, repo);
            var items = page.Items ?? new List<PvpGhostPool.GhostBoard>();
            if (items.Count == 0) continue;
            var chosen = items[Math.Min(pick % pageSize, items.Count - 1)];
            if (chosen != null) return DeepCloneGhostBoard(chosen);
        }
        return null;
    }

    private static uint StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    /// <summary>[1,2,3,5,7,8] → 「第 1–3、5、7–8 天」</summary>
    internal static string FormatDayRanges(IEnumerable<int> days)
    {
        var sorted = days.Distinct().OrderBy(d => d).ToList();
        if (sorted.Count == 0) return "";
        var parts = new List<string>();
        int start = sorted[0], prev = sorted[0];
        for (int i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] == prev + 1) { prev = sorted[i]; continue; }
            parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
            if (i < sorted.Count) { start = sorted[i]; prev = sorted[i]; }
        }
        return $"第 {string.Join("、", parts)} 天";
    }

    // ── 2. 防炸弹解析与校验（计划 8.3）────────────────────────────────────────

    /// <summary>
    /// 解码并安全校验任意分享码（短码 BZ- / 离线码 BZR1: / 原始 JSON）：
    /// 严格防护超长码串、非 gzip、解压炸弹（>512KB）、深层 JSON（>16）、超基数/越界天数、非法模板 ID 与频率类属性篡改。
    /// </summary>
    public bool DecodeAndValidateCode(
        string? rawCode,
        out string codeKind,
        out string? shortCode,
        out RunBlueprintPackage? package,
        out Dictionary<string, HashSet<int>> missingModAffectedDays,
        out int errorStatus,
        out string errorCode,
        out string errorMessage)
    {
        codeKind = "";
        shortCode = null;
        package = null;
        missingModAffectedDays = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        errorStatus = 400;
        errorCode = "CODE_INVALID";
        errorMessage = "分享码格式无效";

        if (string.IsNullOrWhiteSpace(rawCode))
        {
            errorMessage = "分享码不能为空";
            return false;
        }

        string trimmed = rawCode.Trim();
        byte[] jsonBytes;

        if (trimmed.StartsWith("BZ-", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length > 32)
            {
                errorMessage = "分享短码格式无效（长度超出限制）";
                return false;
            }
            codeKind = "short";
            shortCode = trimmed.ToUpperInvariant();
            var rec = _db.LoadSharedBlueprint(shortCode);
            if (rec == null)
            {
                errorStatus = 404;
                errorCode = "CODE_NOT_FOUND";
                errorMessage = "这个分享码在本服务器上不存在";
                return false;
            }
            if (!TryDecompressGzipBounded(rec.BlueprintGzip, MaxDecompressedBytes, out jsonBytes, out string gzipErr))
            {
                errorStatus = 400;
                errorCode = "CODE_INVALID";
                errorMessage = gzipErr;
                return false;
            }
        }
        else if (trimmed.StartsWith("BZR1:", StringComparison.OrdinalIgnoreCase))
        {
            codeKind = "offline";
            if (trimmed.Length > MaxOfflineCodeChars)
            {
                errorMessage = "离线码长度超过 160 KB 上限";
                return false;
            }
            string payload = trimmed[5..].Trim();
            if (!TryBase64UrlDecode(payload, out byte[] compressed))
            {
                errorMessage = "离线码不完整或被改动过，请让朋友重新复制一次";
                return false;
            }
            if (!TryDecompressGzipBounded(compressed, MaxDecompressedBytes, out jsonBytes, out string gzipErr))
            {
                errorMessage = gzipErr;
                return false;
            }
        }
        else if (trimmed.StartsWith('{'))
        {
            codeKind = "json";
            if (Encoding.UTF8.GetByteCount(trimmed) > MaxDecompressedBytes)
            {
                errorMessage = "分享 JSON 体积超过 512 KB 上限";
                return false;
            }
            jsonBytes = Encoding.UTF8.GetBytes(trimmed);
        }
        else
        {
            errorMessage = "无法识别该分享码：短码以 BZ- 开头，离线码以 BZR1: 开头";
            return false;
        }

        RunBlueprintPackage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RunBlueprintPackage>(jsonBytes, DecodeJsonOpts);
        }
        catch (JsonException)
        {
            errorMessage = "蓝图 JSON 格式无效或嵌套层级过深";
            return false;
        }
        catch (NotSupportedException)
        {
            errorMessage = "蓝图 JSON 结构不支持";
            return false;
        }

        if (parsed == null)
        {
            errorMessage = "蓝图内容为空";
            return false;
        }

        if (parsed.SchemaVersion != 1)
        {
            errorMessage = $"不支持的蓝图协议版本（v{parsed.SchemaVersion}）";
            return false;
        }

        string? normalizedHero = PlannerCatalogConstants.NormalizeHeroId(parsed.Hero);
        if (normalizedHero == null)
        {
            errorMessage = $"蓝图包含不受支持的英雄：{parsed.Hero}";
            return false;
        }
        parsed.Hero = normalizedHero;

        if (parsed.MasterSeed == 0)
        {
            if (!DailyChallengeSeedGenerator.TryParseSeedText(parsed.SeedText, out int derivedMaster, out string derivedDisplay))
            {
                errorMessage = "蓝图缺少有效的主种子或种子文本";
                return false;
            }
            parsed.MasterSeed = derivedMaster;
            parsed.SeedText = derivedDisplay;
        }
        else if (string.IsNullOrWhiteSpace(parsed.SeedText))
        {
            parsed.SeedText = DailyChallengeSeedGenerator.FormatSeedCode(parsed.MasterSeed);
        }
        else
        {
            string? normText = DailyChallengeSeedGenerator.NormalizeSeedText(parsed.SeedText);
            if (normText == null)
            {
                parsed.SeedText = DailyChallengeSeedGenerator.FormatSeedCode(parsed.MasterSeed);
            }
            else if (DailyChallengeSeedGenerator.TryParseSeedText(normText, out int textSeed, out string normDisp))
            {
                if (textSeed != parsed.MasterSeed)
                {
                    errorMessage = "蓝图种子文本与主种子不一致，分享码可能已被篡改";
                    return false;
                }
                parsed.SeedText = normDisp;
            }
            else
            {
                errorMessage = "蓝图种子文本格式无效";
                return false;
            }
        }

        parsed.Title = AccountStore.SanitizeDisplayText(parsed.Title, PlannerCatalogConstants.MaxLineupName);
        parsed.CreatorUsername = AccountStore.SanitizeDisplayText(parsed.CreatorUsername, 40);
        parsed.ModEnvironment ??= new ModEnvironmentSpec();
        parsed.ModEnvironment.Mods ??= new List<ModRequirementEntry>();

        // 识别包声明的可用 Mod 与本服缺失 Mod
        var availablePkgModIds = new List<string>();
        var missingModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in parsed.ModEnvironment.Mods)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.ModId)) continue;
            string mid = m.ModId.Trim();
            if (ResolveModPackage(mid) != null)
                availablePkgModIds.Add(mid);
            else
                missingModIds.Add(mid);
        }

        // DayGhosts 基数、天数边界与逐盘面安全校验
        if (parsed.DayGhosts != null)
        {
            if (parsed.DayGhosts.Count > PlannerCatalogConstants.MaxLineupDays)
            {
                errorMessage = $"对手盘面数量超过 {PlannerCatalogConstants.MaxLineupDays} 天上限";
                return false;
            }

            foreach (int dayKey in parsed.DayGhosts.Keys)
            {
                if (dayKey < 1 || dayKey > PlannerCatalogConstants.MaxLineupDays)
                {
                    errorMessage = $"对手盘面包含非法天数（第 {dayKey} 天，仅允许 1–{PlannerCatalogConstants.MaxLineupDays}）";
                    return false;
                }
            }

            var pkgModCtx = _modHost.ResolveRunModContext(
                availablePkgModIds,
                _basePool,
                _monstersProvider(),
                _archetypesProvider());

            var sanitizedGhosts = new Dictionary<int, PvpGhostPool.GhostBoard>();
            foreach (var (day, rawBoard) in parsed.DayGhosts.OrderBy(kv => kv.Key))
            {
                if (rawBoard == null)
                {
                    errorMessage = $"第 {day} 天对手盘面为空";
                    return false;
                }

                PvpGhostPool.GhostBoard boardToValidate = rawBoard;
                if (missingModIds.Count > 0)
                {
                    boardToValidate = StripMissingModCardsForValidation(
                        rawBoard,
                        day,
                        pkgModCtx.Repository,
                        missingModIds,
                        missingModAffectedDays);
                }

                var validation = GhostBoardValidator.ValidateAndNormalize(
                    day,
                    boardToValidate,
                    pkgModCtx.Repository,
                    pkgModCtx.Pool,
                    _modHost);
                if (!validation.Ok)
                {
                    errorMessage = $"第 {day} 天对手盘面校验失败：{validation.Errors[0]}";
                    return false;
                }

                sanitizedGhosts[day] = validation.Board;
            }

            parsed.DayGhosts = sanitizedGhosts.Count > 0 ? sanitizedGhosts : null;
            parsed.ExportScope = parsed.DayGhosts != null ? "events_and_ghosts" : "events_only";
        }
        else
        {
            parsed.ExportScope = "events_only";
        }

        // 规则快照归一化（保留有效 Mod 词条，剥离垃圾词条与三新字段/旧种子）
        if (parsed.RuleSetSnapshot != null)
        {
            var extraMutators = _modHost.GetAllAvailableMutators(availablePkgModIds)
                .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            var normRules = parsed.RuleSetSnapshot.CloneNormalized(extraMutators, preserveUnregisteredMutators: false);
            normRules.CustomSeedText = null;
            normRules.GhostOpponentMode = EGhostOpponentMode.Random;
            normRules.SelectedGhostLineupId = null;
            normRules.FixedRunSeed = null;
            parsed.RuleSetSnapshot = normRules;
        }

        package = parsed;
        errorStatus = 200;
        errorCode = "";
        errorMessage = "";
        return true;
    }

    // ── 3. 导入预览（计划 5.2 & 8.4）──────────────────────────────────────────

    public Task<BlueprintOpResult<BlueprintImportPreview>> PreviewAsync(
        string accountId,
        string? rawCode,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!DecodeAndValidateCode(
                rawCode,
                out string codeKind,
                out _,
                out var package,
                out var missingModAffectedDays,
                out int errStatus,
                out string errCode,
                out string errMsg) || package == null)
        {
            return Task.FromResult(BlueprintOpResult<BlueprintImportPreview>.Fail(errStatus, errCode, errMsg));
        }

        var currentRules = _ruleStore.Get(accountId);
        var modDiff = BuildModDiff(package, currentRules, missingModAffectedDays);

        // warnings 仅放 modDiff 表达不了的事（严禁重复缺失/版本不一致/本服多开）
        var warnings = new List<string>();
        if (package.SeedAlgoVersion != SeedAlgo.Current)
            warnings.Add("服务器更新过，事件可能与原局不同。");

        string localDataVersion = PlannerCatalogConstants.ResolveGameDataVersion(_modHost.ActiveRepository);
        string pkgDataVersion = package.ModEnvironment.GameDataVersion?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(pkgDataVersion) &&
            !string.IsNullOrWhiteSpace(localDataVersion) &&
            !string.Equals(pkgDataVersion, localDataVersion, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("游戏数据版本与原局不同，部分事件或卡牌可能存在差异。");
        }

        if (IsFromDailyChallenge(package))
            warnings.Add("这个分享来自每日挑战：每日模式专属的规则不会带过来，事件可能有细微差别。");

        var ghostDays = package.DayGhosts != null
            ? package.DayGhosts.Keys.OrderBy(d => d).ToList()
            : new List<int>();

        bool hasRules = package.IncludeRules && package.RuleSetSnapshot != null;
        string rulesSummaryZh = hasRules ? BuildRulesSummaryZh(package.RuleSetSnapshot!) : "";

        var preview = new BlueprintImportPreview
        {
            CodeKind = codeKind,
            Title = package.Title,
            CreatorUsername = package.CreatorUsername,
            CreatedAtUtc = package.CreatedAtUtc,
            Scope = ghostDays.Count > 0 ? "events_and_ghosts" : "events_only",
            Hero = package.Hero,
            HeroZh = PlannerCatalogConstants.GetHeroNameZh(package.Hero),
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(package.MasterSeed),
            GhostDays = ghostDays,
            HasRules = hasRules,
            RulesSummaryZh = rulesSummaryZh,
            ModDiff = modDiff,
            Warnings = warnings,
            HasActiveRun = HasActiveNonTerminalRun(accountId),
        };

        return Task.FromResult(BlueprintOpResult<BlueprintImportPreview>.Success(preview));
    }

    // ── 4. 导入套用（计划 8.4）────────────────────────────────────────────────

    public Task<BlueprintOpResult<BlueprintImportResponse>> ImportAsync(
        string accountId,
        BlueprintImportRequest? req,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        req ??= new BlueprintImportRequest();

        if (!DecodeAndValidateCode(
                req.Code,
                out _,
                out string? shortCode,
                out var package,
                out _,
                out int errStatus,
                out string errCode,
                out string errMsg) || package == null)
        {
            return Task.FromResult(BlueprintOpResult<BlueprintImportResponse>.Fail(errStatus, errCode, errMsg));
        }

        var currentRules = _ruleStore.Get(accountId);
        var serverDefaultIds = GetServerDefaultModIds();

        // 1) 种子计算：来自每日挑战时使用标准种子码并在套用规则时切回 Standard
        bool fromDailyChallenge = IsFromDailyChallenge(package);
        // 一律换成固定格式的种子码；主种子不变，事件完全一样
        string effectiveSeedText = DailyChallengeSeedGenerator.FormatSeedCode(package.MasterSeed);

        // 2) Mod 递归依赖展开 + 过滤不存在与全服默认 Mod
        var addModIds = ResolveInstallableCommunityModIds(req.EnableModIds, serverDefaultIds);

        var mergedModIds = MergeModIds(currentRules.SelectedModIds, addModIds);

        // 3) 对手剧本导入：在导入后有效 Mod 环境下剔除缺失卡并复检，空板天跳过（回落随机）
        bool wantLineup = string.Equals(req.Opponents?.Trim(), "lineup", StringComparison.OrdinalIgnoreCase);
        CustomGhostLineup? savedLineup = null;
        if (wantLineup && package.DayGhosts is { Count: > 0 })
        {
            var importModCtx = _modHost.ResolveRunModContext(
                mergedModIds,
                _basePool,
                _monstersProvider(),
                _archetypesProvider());

            var validDays = new Dictionary<int, PvpGhostPool.GhostBoard>();
            foreach (var (day, board) in package.DayGhosts.OrderBy(kv => kv.Key))
            {
                if (board == null) continue;
                var filtered = StripUnknownCardsForRepository(board, importModCtx.Repository);
                var val = GhostBoardValidator.ValidateAndNormalize(
                    day,
                    filtered,
                    importModCtx.Repository,
                    importModCtx.Pool,
                    _modHost);
                if (val.Ok && PvpGhostPool.HasAnyValidItem(val.Board, importModCtx.Repository))
                    validDays[day] = val.Board;
            }

            if (validDays.Count > 0)
            {
                string labelSrc = !string.IsNullOrWhiteSpace(package.Title)
                    ? package.Title
                    : (!string.IsNullOrWhiteSpace(shortCode) ? shortCode! : effectiveSeedText);
                // 转了好几手的分享不要叠成「导入：导入：…」
                labelSrc = labelSrc.Trim();
                while (labelSrc.StartsWith("导入：", StringComparison.Ordinal)) labelSrc = labelSrc["导入：".Length..].Trim();
                if (labelSrc.Length == 0) labelSrc = effectiveSeedText;
                string lineupName = AccountStore.SanitizeDisplayText($"导入：{labelSrc}".Trim(), PlannerCatalogConstants.MaxLineupName);
                if (string.IsNullOrWhiteSpace(lineupName))
                    lineupName = "导入方案";

                string lineupDesc = AccountStore.SanitizeDisplayText(
                    (string.IsNullOrWhiteSpace(package.CreatorUsername)
                        ? "从分享码导入"
                        : $"来自 {package.CreatorUsername} 的分享").Trim(),
                    PlannerCatalogConstants.MaxDescription);

                // 按 validDays 中实际留存卡牌所属 Mod 重建 ModEnvironment，避免已剥离缺失 Mod 永久标红
                var retainedModIds = CollectRetainedBoardModOrigins(validDays.Values, importModCtx.Repository);
                var rebuiltModEnv = PlannerCatalogConstants.BuildModEnvironmentSpec(
                    retainedModIds,
                    _modHost,
                    importModCtx.Repository,
                    customModeId: null);

                var entity = new CustomGhostLineup
                {
                    AccountId = accountId,
                    Name = lineupName,
                    Description = lineupDesc,
                    SourceType = "imported",
                    SourceCode = shortCode,
                    SeedText = effectiveSeedText,
                    SeedHero = PlannerCatalogConstants.NormalizeHeroId(package.Hero),
                    ModEnvironment = rebuiltModEnv,
                    Days = validDays,
                };

                var (savedOk, saveErrCode, saved) = _db.SaveGhostLineup(
                    entity,
                    maxPerAccount: PlannerCatalogConstants.MaxLineups,
                    maxJsonBytes: PlannerCatalogConstants.MaxLineupJsonBytes,
                    requireExisting: false);
                if (!savedOk || saved == null)
                {
                    return Task.FromResult(MapSaveLineupError<BlueprintImportResponse>(saveErrCode));
                }
                savedLineup = saved;
            }
        }

        // 4) 短码导入计数 +1
        if (!string.IsNullOrWhiteSpace(shortCode))
        {
            try { _db.IncrementSharedBlueprintImportCount(shortCode); } catch { }
        }

        // 5) 构造 rulesPatch（仅当 req.ApplyRules && package.IncludeRules && RuleSetSnapshot != null 时提供清洗后快照；每日挑战切回 Standard）
        CustomRunRuleSet? rulesSnapshotForPatch = null;
        if (req.ApplyRules && package.IncludeRules && package.RuleSetSnapshot != null)
        {
            var extraMutators = _modHost.GetAllAvailableMutators(mergedModIds)
                .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            rulesSnapshotForPatch = package.RuleSetSnapshot.CloneNormalized(extraMutators, preserveUnregisteredMutators: false);
            if (fromDailyChallenge)
            {
                rulesSnapshotForPatch.SpecialModeId = ESpecialModeId.Standard;
                rulesSnapshotForPatch.DailyChallengeDate = null;
            }
            rulesSnapshotForPatch.CustomSeedText = null;
            rulesSnapshotForPatch.GhostOpponentMode = EGhostOpponentMode.Random;
            rulesSnapshotForPatch.SelectedGhostLineupId = null;
            rulesSnapshotForPatch.FixedRunSeed = null;
            // 清空原局 SelectedModIds，避免 apply=false 消费方把缺失 Mod 或未勾选 Mod 写进规则（统一由 rulesPatch.addModIds 增量合并）
            rulesSnapshotForPatch.SelectedModIds = new List<string>();
        }

        var targetGhostMode = savedLineup != null ? EGhostOpponentMode.FixedLineup : EGhostOpponentMode.Random;
        string? targetLineupId = savedLineup?.LineupId;

        var rulesPatch = new BlueprintRulesPatch
        {
            CustomSeedText = effectiveSeedText,
            GhostOpponentMode = targetGhostMode.ToString(),
            SelectedGhostLineupId = targetLineupId,
            AddModIds = addModIds,
            RulesSnapshot = rulesSnapshotForPatch,
        };

        // 6) apply=true 时写回 PlayerRuleStore；apply=false 仅新建剧本不改玩家规则
        CustomRunRuleSet? savedRules = null;
        if (req.Apply)
        {
            CustomRunRuleSet nextRules;
            if (rulesSnapshotForPatch != null)
            {
                var extraMutators = _modHost.GetAllAvailableMutators(mergedModIds)
                    .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
                nextRules = rulesSnapshotForPatch.CloneNormalized(extraMutators, preserveUnregisteredMutators: false);
                nextRules.SelectedModIds = mergedModIds;
            }
            else
            {
                var extraMutators = _modHost.GetAllAvailableMutators(mergedModIds)
                    .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
                nextRules = currentRules.CloneNormalized(extraMutators, preserveUnregisteredMutators: false);
                nextRules.SelectedModIds = mergedModIds;
            }

            nextRules.CustomSeedText = effectiveSeedText;
            if (nextRules.SpecialModeId != ESpecialModeId.DailyChallenge)
                nextRules.FixedRunSeed = null;
            nextRules.GhostOpponentMode = targetGhostMode;
            nextRules.SelectedGhostLineupId = targetLineupId;

            savedRules = _ruleStore.Set(accountId, nextRules);
        }

        // 7) 构造同构 LineupSummary（不耦合 GhostLineupStore）
        BlueprintImportedLineupSummary? lineupSummary = savedLineup != null
            ? BuildImportedLineupSummary(savedLineup, savedRules, serverDefaultIds)
            : null;

        string messageZh = savedLineup != null
            ? $"已导入：种子 {effectiveSeedText}，对手方案「{savedLineup.Name}」（{FormatDayRanges(savedLineup.Days.Keys)}固定，其余天随机），下一局生效"
            : $"已导入：种子 {effectiveSeedText}，对手全随机，下一局生效";
        if (wantLineup && package.DayGhosts is { Count: > 0 })
        {
            var dropped = package.DayGhosts.Keys
                .Where(d => savedLineup == null || !savedLineup.Days.ContainsKey(d))
                .ToList();
            if (dropped.Count > 0)
                messageZh += $"。注意：{FormatDayRanges(dropped)}的对手用到了本服没有的卡，那几天改成随机";
        }

        return Task.FromResult(BlueprintOpResult<BlueprintImportResponse>.Success(new BlueprintImportResponse
        {
            RulesPatch = rulesPatch,
            Rules = savedRules,
            Lineup = lineupSummary,
            MessageZh = messageZh,
        }));
    }

    // ── 5. 我的分享码与删除 ──────────────────────────────────────────────────

    public BlueprintMineListResponse ListMine(string accountId)
    {
        var records = _db.ListSharedBlueprintsByCreator(accountId);
        var items = new List<BlueprintMineItemDto>(records.Count);
        foreach (var rec in records)
        {
            string hero = PlannerCatalogConstants.NormalizeHeroId(rec.Hero) ?? rec.Hero;
            items.Add(new BlueprintMineItemDto
            {
                ShortCode = rec.ShareCode,
                Title = rec.Title,
                Scope = rec.ExportScope,
                Hero = hero,
                HeroZh = PlannerCatalogConstants.GetHeroNameZh(hero),
                SeedText = rec.SeedText,
                ImportCount = rec.ImportCount,
                CreatedAtUtc = rec.CreatedAtUtc,
            });
        }
        return new BlueprintMineListResponse { Items = items };
    }

    public BlueprintOpResult<bool> DeleteMine(string accountId, string? shortCode)
    {
        if (string.IsNullOrWhiteSpace(shortCode))
            return BlueprintOpResult<bool>.Fail(404, "CODE_NOT_FOUND", "分享码不存在或不是你的");

        bool deleted = _db.DeleteSharedBlueprint(accountId, shortCode.Trim().ToUpperInvariant());
        if (!deleted)
            return BlueprintOpResult<bool>.Fail(404, "CODE_NOT_FOUND", "分享码不存在或不是你的");

        return BlueprintOpResult<bool>.Success(true);
    }

    // ── 内部辅助：Mod 差异与依赖解析 ──────────────────────────────────────────

    private BlueprintModDiff BuildModDiff(
        RunBlueprintPackage package,
        CustomRunRuleSet currentRules,
        Dictionary<string, HashSet<int>> missingModAffectedDays)
    {
        var diff = new BlueprintModDiff();
        var serverDefaultIds = GetServerDefaultModIds();
        var playerSelectedIds = new HashSet<string>(
            currentRules.SelectedModIds ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var pkgModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mismatchSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredGameplayIds = new List<string>();

        foreach (var m in package.ModEnvironment.Mods)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.ModId)) continue;
            string mid = m.ModId.Trim();
            pkgModIds.Add(mid);

            var localPkg = ResolveModPackage(mid);
            if (localPkg == null)
            {
                if (missingSeen.Add(mid))
                {
                    var affected = missingModAffectedDays.TryGetValue(mid, out var daysSet)
                        ? daysSet.OrderBy(d => d).ToList()
                        : new List<int>();
                    diff.Missing.Add(new BlueprintMissingMod
                    {
                        ModId = mid,
                        Name = string.IsNullOrWhiteSpace(m.Name) ? mid : m.Name.Trim(),
                        Version = m.Version ?? "",
                        Author = m.Author ?? "",
                        AffectedDays = affected,
                    });
                }
                continue;
            }

            string localId = localPkg.Manifest.Id;
            string localName = string.IsNullOrWhiteSpace(localPkg.Manifest.Name) ? localId : localPkg.Manifest.Name.Trim();
            string localVer = localPkg.Manifest.Version?.Trim() ?? "";
            string expectedVer = m.Version?.Trim() ?? "";
            bool verDiff = !string.IsNullOrWhiteSpace(expectedVer) &&
                           !string.Equals(localVer, expectedVer, StringComparison.OrdinalIgnoreCase);
            bool hashDiff = !string.IsNullOrWhiteSpace(m.PackageHash) &&
                            !string.IsNullOrWhiteSpace(localPkg.PackageHash) &&
                            !string.Equals(localPkg.PackageHash, m.PackageHash, StringComparison.OrdinalIgnoreCase);
            if ((verDiff || hashDiff) && mismatchSeen.Add(localId))
            {
                string dispLocalVer = localVer;
                string dispExpectedVer = expectedVer;
                if (!verDiff && hashDiff)
                {
                    string localShort = ShortHash(localPkg.PackageHash);
                    string expShort = ShortHash(m.PackageHash);
                    dispLocalVer = string.IsNullOrEmpty(dispLocalVer) ? $"#{localShort}" : $"{dispLocalVer} (#{localShort})";
                    dispExpectedVer = string.IsNullOrEmpty(dispExpectedVer) ? $"#{expShort}" : $"{dispExpectedVer} (#{expShort})";
                }
                diff.VersionMismatch.Add(new BlueprintVersionMismatchMod
                {
                    ModId = localId,
                    Name = localName,
                    Version = dispLocalVer,
                    ExpectedVersion = dispExpectedVer,
                });
            }

            if (m.IsGameplay || ServerModHost.IsGameplayAffectingPackage(localPkg))
                requiredGameplayIds.Add(localId);
        }

        // 递归展开包要求的玩法 Mod 依赖并走 DescribeRequiredMods 校验可用性
        var expandedRequired = ExpandModDependencies(requiredGameplayIds);
        var described = _communityMods != null
            ? _communityMods.DescribeRequiredMods(expandedRequired)
            : new List<RequiredModSummary>();
        var describedById = described.ToDictionary(x => x.ModId, StringComparer.OrdinalIgnoreCase);

        var autoSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string modId in expandedRequired)
        {
            var pkg = ResolveModPackage(modId);
            if (pkg == null)
            {
                if (missingSeen.Add(modId))
                {
                    diff.Missing.Add(new BlueprintMissingMod
                    {
                        ModId = modId,
                        Name = modId,
                        Version = "",
                        Author = "",
                        AffectedDays = new List<int>(),
                    });
                }
                continue;
            }

            string canonicalId = pkg.Manifest.Id;
            if (serverDefaultIds.Contains(canonicalId) || playerSelectedIds.Contains(canonicalId))
                continue;

            bool available = describedById.TryGetValue(canonicalId, out var desc)
                ? desc.AvailableOnServer
                : true;
            if (!available || !autoSeen.Add(canonicalId))
                continue;

            diff.AutoEnable.Add(new BlueprintAutoEnableMod
            {
                ModId = canonicalId,
                Name = string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? canonicalId : pkg.Manifest.Name.Trim(),
                Version = pkg.Manifest.Version?.Trim() ?? "",
                Author = pkg.Manifest.Author?.Trim() ?? "",
            });
        }

        // 本服当前启用的玩法 Mod（全服默认 + 玩家已勾选）中不在包内的项
        var myEffectiveRegistry = _modHost.ResolveEffectiveRegistryForMods(currentRules.SelectedModIds);
        var extraSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in myEffectiveRegistry.LoadedPackages)
        {
            if (pkg?.Manifest == null || string.IsNullOrWhiteSpace(pkg.Manifest.Id)) continue;
            if (!ServerModHost.IsGameplayAffectingPackage(pkg)) continue;
            string pid = pkg.Manifest.Id.Trim();
            if (pkgModIds.Contains(pid) || !extraSeen.Add(pid)) continue;
            diff.ServerExtra.Add(new BlueprintServerExtraMod
            {
                ModId = pid,
                Name = string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? pid : pkg.Manifest.Name.Trim(),
            });
        }

        return diff;
    }

    private List<string> ResolveInstallableCommunityModIds(
        IReadOnlyList<string>? requestedIds,
        HashSet<string> serverDefaultIds)
    {
        if (requestedIds == null || requestedIds.Count == 0)
            return new List<string>();

        var expanded = ExpandModDependencies(requestedIds);
        var described = _communityMods != null
            ? _communityMods.DescribeRequiredMods(expanded)
            : new List<RequiredModSummary>();
        var describedById = described.ToDictionary(x => x.ModId, StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in expanded)
        {
            var pkg = ResolveModPackage(id);
            if (pkg == null) continue;
            string canonicalId = pkg.Manifest.Id;
            if (serverDefaultIds.Contains(canonicalId)) continue;
            if (describedById.TryGetValue(canonicalId, out var d) && !d.AvailableOnServer) continue;
            if (seen.Add(canonicalId))
                result.Add(canonicalId);
        }
        return result;
    }

    private List<string> ExpandModDependencies(IEnumerable<string> rootIds)
    {
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return;
            string id = rawId.Trim();
            if (visited.Contains(id) || !visiting.Add(id)) return;

            var pkg = ResolveModPackage(id);
            if (pkg != null)
            {
                if (pkg.Manifest.Dependencies is { Count: > 0 })
                {
                    foreach (string dep in pkg.Manifest.Dependencies)
                        Visit(dep);
                }
                string canonical = pkg.Manifest.Id;
                visiting.Remove(id);
                if (visited.Add(canonical))
                    ordered.Add(canonical);
            }
            else
            {
                visiting.Remove(id);
                if (visited.Add(id))
                    ordered.Add(id);
            }
        }

        foreach (string id in rootIds)
            Visit(id);

        return ordered;
    }

    private ModPackage? ResolveModPackage(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        string clean = modId.Trim();
        return _communityMods?.ResolvePackage(clean)
            ?? _modHost.FindDiscoveredPackage(clean)
            ?? _modHost.Registry.LoadedPackages.FirstOrDefault(
                p => p?.Manifest != null && string.Equals(p.Manifest.Id, clean, StringComparison.OrdinalIgnoreCase));
    }

    private HashSet<string> GetServerDefaultModIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in _modHost.Registry.LoadedPackages)
        {
            if (pkg?.Manifest != null && !string.IsNullOrWhiteSpace(pkg.Manifest.Id))
                set.Add(pkg.Manifest.Id.Trim());
        }
        return set;
    }

    private BlueprintImportedLineupSummary BuildImportedLineupSummary(
        CustomGhostLineup lineup,
        CustomRunRuleSet? currentRules,
        HashSet<string> serverDefaultIds)
    {
        var missing = new List<BlueprintLineupMissingMod>();
        var required = new List<string>();
        var reqSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (lineup.ModEnvironment?.Mods != null)
        {
            foreach (var m in lineup.ModEnvironment.Mods)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.ModId)) continue;
                string mid = m.ModId.Trim();
                if (m.IsGameplay && !serverDefaultIds.Contains(mid) && reqSeen.Add(mid))
                    required.Add(mid);
                if (m.IsGameplay && ResolveModPackage(mid) == null && missSeen.Add(mid))
                {
                    missing.Add(new BlueprintLineupMissingMod
                    {
                        ModId = mid,
                        Name = string.IsNullOrWhiteSpace(m.Name) ? mid : m.Name.Trim(),
                    });
                }
            }
        }
        required.Sort(StringComparer.OrdinalIgnoreCase);

        bool isSelected = currentRules != null
            && currentRules.GhostOpponentMode == EGhostOpponentMode.FixedLineup
            && string.Equals(currentRules.SelectedGhostLineupId, lineup.LineupId, StringComparison.OrdinalIgnoreCase);

        return new BlueprintImportedLineupSummary
        {
            LineupId = lineup.LineupId,
            Name = lineup.Name,
            Description = lineup.Description,
            DayNumbers = lineup.Days.Keys.OrderBy(d => d).ToList(),
            SourceType = lineup.SourceType,
            SourceCode = lineup.SourceCode,
            UpdatedAtUtc = lineup.UpdatedAtUtc,
            MissingMods = missing,
            RequiredModIds = required,
            IsSelected = isSelected,
        };
    }

    // ── 内部辅助：缺失 Mod 卡过滤、GZip 与 Base64Url ──────────────────────────

    private static PvpGhostPool.GhostBoard StripMissingModCardsForValidation(
        PvpGhostPool.GhostBoard board,
        int day,
        GameDataRepository repo,
        HashSet<string> missingModIds,
        Dictionary<string, HashSet<int>> affectedDaysByMod)
    {
        void RecordMissingCard(string? templateId)
        {
            string matchedMod = "";
            if (!string.IsNullOrWhiteSpace(templateId))
            {
                int colon = templateId.IndexOf(':');
                if (colon > 0)
                {
                    string prefix = templateId[..colon].Trim();
                    if (missingModIds.Contains(prefix))
                        matchedMod = prefix;
                }
            }

            if (!string.IsNullOrEmpty(matchedMod))
            {
                if (!affectedDaysByMod.TryGetValue(matchedMod, out var set))
                    affectedDaysByMod[matchedMod] = set = new HashSet<int>();
                set.Add(day);
            }
            else
            {
                foreach (string mid in missingModIds)
                {
                    if (!affectedDaysByMod.TryGetValue(mid, out var set))
                        affectedDaysByMod[mid] = set = new HashSet<int>();
                    set.Add(day);
                }
            }
        }

        var keptItems = new List<PvpGhostPool.GhostItem>();
        if (board.Items != null)
        {
            foreach (var it in board.Items)
            {
                if (it == null) continue;
                if (!string.IsNullOrWhiteSpace(it.TemplateId) && repo.GetCard(it.TemplateId.Trim()) != null)
                    keptItems.Add(it);
                else
                    RecordMissingCard(it.TemplateId);
            }
        }

        var keptSkills = new List<PvpGhostPool.GhostSkill>();
        if (board.Skills != null)
        {
            foreach (var sk in board.Skills)
            {
                if (sk == null) continue;
                if (!string.IsNullOrWhiteSpace(sk.TemplateId) && repo.GetCard(sk.TemplateId.Trim()) != null)
                    keptSkills.Add(sk);
                else
                    RecordMissingCard(sk.TemplateId);
            }
        }

        var keptSockets = new List<PvpGhostPool.GhostSocketEffect>();
        if (board.SocketEffects != null)
        {
            foreach (var se in board.SocketEffects)
            {
                if (se == null) continue;
                if (!string.IsNullOrWhiteSpace(se.TemplateId) && repo.GetCard(se.TemplateId.Trim()) != null)
                    keptSockets.Add(se);
                else
                    RecordMissingCard(se.TemplateId);
            }
        }

        return CloneBoardWithFilteredCards(board, keptItems, keptSkills, keptSockets);
    }

    private static PvpGhostPool.GhostBoard StripUnknownCardsForRepository(
        PvpGhostPool.GhostBoard board,
        GameDataRepository repo)
    {
        var keptItems = board.Items?
            .Where(it => it != null && !string.IsNullOrWhiteSpace(it.TemplateId) && repo.GetCard(it.TemplateId.Trim()) != null)
            .ToList() ?? new List<PvpGhostPool.GhostItem>();

        var keptSkills = board.Skills?
            .Where(sk => sk != null && !string.IsNullOrWhiteSpace(sk.TemplateId) && repo.GetCard(sk.TemplateId.Trim()) != null)
            .ToList() ?? new List<PvpGhostPool.GhostSkill>();

        var keptSockets = board.SocketEffects?
            .Where(se => se != null && !string.IsNullOrWhiteSpace(se.TemplateId) && repo.GetCard(se.TemplateId.Trim()) != null)
            .ToList() ?? new List<PvpGhostPool.GhostSocketEffect>();

        return CloneBoardWithFilteredCards(board, keptItems, keptSkills, keptSockets);
    }

    private static PvpGhostPool.GhostBoard CloneBoardWithFilteredCards(
        PvpGhostPool.GhostBoard board,
        List<PvpGhostPool.GhostItem> items,
        List<PvpGhostPool.GhostSkill> skills,
        List<PvpGhostPool.GhostSocketEffect> sockets)
        => new()
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
            IsPlayerGhost = board.IsPlayerGhost,
            IsCustomRule = board.IsCustomRule,
            PoolPartition = board.PoolPartition,
            DisplayName = board.DisplayName,
            Items = items,
            Skills = skills,
            SocketEffects = sockets,
        };

    private static PvpGhostPool.GhostBoard? DeepCloneGhostBoard(PvpGhostPool.GhostBoard board)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(board, EncodeJsonOpts);
            return JsonSerializer.Deserialize<PvpGhostPool.GhostBoard>(bytes, EncodeJsonOpts);
        }
        catch
        {
            return board;
        }
    }

    private static byte[] CompressGzip(byte[] rawBytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(rawBytes, 0, rawBytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>校验 gzip 魔数 (1F 8B) 并以 16 KB 分块流式解压，超过上限立刻中止防压缩炸弹。</summary>
    public static bool TryDecompressGzipBounded(
        byte[]? compressed,
        int maxOutputBytes,
        out byte[] output,
        out string errorMessage)
    {
        output = Array.Empty<byte>();
        errorMessage = "离线码压缩数据无效";
        if (compressed == null || compressed.Length < 2)
        {
            errorMessage = "离线码不是有效的 gzip 数据";
            return false;
        }
        if (compressed[0] != 0x1F || compressed[1] != 0x8B)
        {
            errorMessage = "离线码缺少有效的 gzip 魔数头";
            return false;
        }

        try
        {
            using var inputMs = new MemoryStream(compressed, writable: false);
            using var gz = new GZipStream(inputMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            byte[] buffer = new byte[StreamBufferBytes];
            int total = 0;
            while (true)
            {
                int read = gz.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                total += read;
                if (total > maxOutputBytes)
                {
                    errorMessage = $"蓝图解压体积超过 {maxOutputBytes / 1024} KB 安全上限";
                    return false;
                }
                outMs.Write(buffer, 0, read);
            }

            output = outMs.ToArray();
            errorMessage = "";
            return true;
        }
        catch (InvalidDataException)
        {
            errorMessage = "离线码压缩流损坏，无法解压";
            return false;
        }
        catch (Exception)
        {
            errorMessage = "离线码解压失败";
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(input)) return false;
        // 剥除聊天软件/剪贴板自动折行插入的所有空白字符后再解码
        string compact = string.Concat(input.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length == 0) return false;
        string s = compact.Replace('-', '+').Replace('_', '/');
        int mod = s.Length % 4;
        if (mod == 1) return false;
        if (mod == 2) s += "==";
        else if (mod == 3) s += "=";
        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsFromDailyChallenge(RunBlueprintPackage package)
        => string.Equals(package.SourceSpecialModeId?.Trim(), nameof(ESpecialModeId.DailyChallenge), StringComparison.OrdinalIgnoreCase)
           || package.RuleSetSnapshot?.SpecialModeId == ESpecialModeId.DailyChallenge;

    private static string ShortHash(string? rawHash)
    {
        if (string.IsNullOrWhiteSpace(rawHash)) return "unknown";
        string clean = rawHash.Trim();
        return clean.Length <= 8 ? clean : clean[..8];
    }

    private static List<string> CollectRetainedBoardModOrigins(
        IEnumerable<PvpGhostPool.GhostBoard> boards,
        GameDataRepository repo)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(string? tplId)
        {
            if (string.IsNullOrWhiteSpace(tplId)) return;
            var tpl = repo.GetCard(tplId.Trim());
            if (tpl != null && tpl.IsCustomModCard && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin))
            {
                string mid = tpl.CustomModOrigin.Trim();
                if (seen.Add(mid))
                    ids.Add(mid);
            }
        }

        foreach (var b in boards)
        {
            if (b == null) continue;
            if (b.Items != null)
                foreach (var it in b.Items) Collect(it?.TemplateId);
            if (b.Skills != null)
                foreach (var sk in b.Skills) Collect(sk?.TemplateId);
            if (b.SocketEffects != null)
                foreach (var se in b.SocketEffects) Collect(se?.TemplateId);
        }

        return ids;
    }

    // ── 内部辅助：会话查找、规则摘要与错误映射 ────────────────────────────────

    private static Session? FindSessionForAccount(SessionStore sessions, string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        string trimmed = accountId.Trim();
        if (Guid.TryParse(trimmed, out var parsed))
        {
            return sessions.GetByAccount(parsed.ToString("N"))
                ?? sessions.GetByAccount(parsed.ToString("D"));
        }
        return sessions.GetByAccount(trimmed);
    }

    private bool HasActiveNonTerminalRun(string accountId)
    {
        var session = FindSessionForAccount(_sessions, accountId);
        if (session == null) return false;
        var handle = SessionRegistry.Get(session.Sid);
        if (handle == null) return false;
        return !Persistence.IsTerminalState(handle.Run.PublicSummary.State);
    }

    private static List<string> MergeModIds(params IEnumerable<string>?[] lists)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            if (list == null) continue;
            foreach (string raw in list)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.Trim();
                if (seen.Add(trimmed))
                {
                    result.Add(trimmed);
                    if (result.Count >= 16)
                        return result;
                }
            }
        }
        return result;
    }

    private static string BuildRulesSummaryZh(CustomRunRuleSet rules)
    {
        var parts = new List<string>(4);
        string modeZh = rules.SpecialModeId switch
        {
            ESpecialModeId.EndlessTower => "无尽爬塔",
            ESpecialModeId.CrossHeroMayhem => "全英雄混战",
            ESpecialModeId.MutatorChallenge => "突变词条",
            ESpecialModeId.DailyChallenge => "每日挑战",
            ESpecialModeId.BossRush => "Boss 车轮战",
            ESpecialModeId.SandboxLab => "沙盒实验",
            _ => "标准模式",
        };
        if (rules.SpecialModeId != ESpecialModeId.Standard)
            parts.Add(modeZh);

        string presetZh = rules.PresetId switch
        {
            EGameModePreset.EnhancedFun => "爽玩增强",
            EGameModePreset.ExtremeRoguelike => "极限肉鸽",
            EGameModePreset.Custom => "自定义规则",
            _ => "经典官方",
        };
        parts.Add(presetZh);

        if (rules.MutatorIds is { Count: > 0 })
            parts.Add($"词条×{rules.MutatorIds.Count}");
        if (!string.IsNullOrWhiteSpace(rules.CustomModeId))
            parts.Add($"自定义模式:{rules.CustomModeId}");

        return string.Join(" / ", parts);
    }

    private static BlueprintOpResult<T> MapSaveLineupError<T>(string? errorCode) => errorCode switch
    {
        "LINEUP_LIMIT" => BlueprintOpResult<T>.Fail(409, "LINEUP_LIMIT", $"对手方案数量已达 {PlannerCatalogConstants.MaxLineups} 套上限，请先删除旧方案再导入"),
        "PAYLOAD_TOO_LARGE" => BlueprintOpResult<T>.Fail(413, "PAYLOAD_TOO_LARGE", "导入的对手方案数据超过 64 KB 上限"),
        _ => BlueprintOpResult<T>.Fail(400, errorCode ?? "BAD_REQUEST", "保存导入的对手方案失败"),
    };
}
